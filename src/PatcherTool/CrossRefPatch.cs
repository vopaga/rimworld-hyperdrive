using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldPatcher;

/// <summary>
/// Patch 2 – DirectXmlCrossRefLoader thread-safe registration
///
/// Problem: wantedRefs (List) and wantedListDictRefs (Dictionary) are mutated without
///          synchronization in all Register* methods, causing data races when
///          ParseAndProcessXML is parallelized.
///
/// Fix: Add a static readonly lock object + a private helper method AddWantedRef_Locked
///      that wraps wantedRefs.Add with Monitor.Enter/Exit.
///
/// KEY: All type/method references are resolved via Cecil from the target module's
///      referenced assemblies (mscorlib), NOT via C# reflection (which would give
///      .NET 8 / System.Private.CoreLib refs incompatible with Mono/net40 Unity).
/// </summary>
static class CrossRefPatch
{
    // ── Cecil-native BCL type resolution ──────────────────────────────────────
    // NEVER use typeof(...) here — patcher runs on .NET 8 and reflection gives
    // System.Private.CoreLib references the game's Mono runtime can't load.

    private static TypeDefinition? _cachedObjectType;
    private static TypeDefinition? _cachedMonitorType;

    private static TypeDefinition GetObjectType(ModuleDefinition module)
    {
        if (_cachedObjectType != null) return _cachedObjectType;
        _cachedObjectType = ResolveTypeFromTargetRefs(module, "System.Object")
            ?? throw new Exception("Could not find System.Object in target assembly references");
        return _cachedObjectType;
    }

    private static TypeDefinition GetMonitorType(ModuleDefinition module)
    {
        if (_cachedMonitorType != null) return _cachedMonitorType;
        _cachedMonitorType = ResolveTypeFromTargetRefs(module, "System.Threading.Monitor")
            ?? throw new Exception("Could not find System.Threading.Monitor in target assembly references");
        return _cachedMonitorType;
    }

    private static TypeReference GetVoidType(ModuleDefinition module)
        => module.TypeSystem.Void;

    private static TypeDefinition? ResolveTypeFromTargetRefs(ModuleDefinition module, string fullName)
    {
        // Check the core library first (mscorlib / netstandard)
        try
        {
            var coreLib = module.AssemblyResolver.Resolve((AssemblyNameReference)module.TypeSystem.CoreLibrary);
            var found = coreLib.MainModule.GetType(fullName);
            if (found != null) return found;
        }
        catch { }

        // Walk all referenced assemblies
        foreach (var asmRef in module.AssemblyReferences)
        {
            try
            {
                var asm = module.AssemblyResolver.Resolve(asmRef);
                var found = asm.MainModule.GetType(fullName);
                if (found != null) return found;
            }
            catch { }
        }
        return null;
    }

    private static MethodReference GetObjectCtor(ModuleDefinition module)
    {
        var objType = GetObjectType(module);
        var ctor = objType.Methods.First(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
        return module.ImportReference(ctor);
    }

    private static MethodReference GetMonitorEnter(ModuleDefinition module)
    {
        var monitor = GetMonitorType(module);
        // Enter(object obj) — single-param overload
        var method = monitor.Methods.First(m => m.Name == "Enter" && m.Parameters.Count == 1);
        return module.ImportReference(method);
    }

    private static MethodReference GetMonitorExit(ModuleDefinition module)
    {
        var monitor = GetMonitorType(module);
        var method = monitor.Methods.First(m => m.Name == "Exit" && m.Parameters.Count == 1);
        return module.ImportReference(method);
    }

    // ── Main patch entry point ────────────────────────────────────────────────

    public static void Apply(ModuleDefinition module)
    {
        Console.WriteLine("[Patch2] Patching DirectXmlCrossRefLoader for thread-safe registration...");

        // Reset cached types (in case Apply is called multiple times)
        _cachedObjectType = null;
        _cachedMonitorType = null;

        var type = module.MustGetType("Verse.DirectXmlCrossRefLoader");

        // ── Step 2a: Add the lock field ──────────────────────────────────────
        const string lockFieldName = "_registrationLock";
        if (type.Fields.Any(f => f.Name == lockFieldName))
        {
            Console.WriteLine("[Patch2] Lock field already exists — skipping add.");
        }
        else
        {
            // Use Cecil's own TypeSystem.Object — guaranteed to resolve to the
            // target module's object (mscorlib), not to System.Private.CoreLib
            var lockField = new FieldDefinition(
                lockFieldName,
                FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly,
                module.TypeSystem.Object);
            type.Fields.Add(lockField);
            Console.WriteLine("[Patch2] Added field: " + lockFieldName);

            AddLockFieldInit(module, type, lockField);
        }

        var lockFieldDef = type.MustGetField(lockFieldName);

        // ── Step 2b: Patch all 3 RegisterObjectWantsCrossRef overloads ───────
        int patchedCount = 0;
        foreach (var method in type.Methods.Where(m => m.Name == "RegisterObjectWantsCrossRef"))
        {
            if (PatchRegisterObject(module, type, method, lockFieldDef))
                patchedCount++;
        }
        Console.WriteLine($"[Patch2] Patched {patchedCount} RegisterObjectWantsCrossRef overload(s)");

        // ── Step 2d: Patch RegisterListWantsCrossRef ──────────────────────────
        foreach (var method in type.Methods.Where(m => m.Name == "RegisterListWantsCrossRef"))
        {
            if (PatchRegisterListOrDict(module, method, lockFieldDef))
                Console.WriteLine("[Patch2] Patched RegisterListWantsCrossRef");
        }

        // ── Step 2e: Patch RegisterDictionaryWantsCrossRef ────────────────────
        foreach (var method in type.Methods.Where(m => m.Name == "RegisterDictionaryWantsCrossRef"))
        {
            if (PatchRegisterListOrDict(module, method, lockFieldDef))
                Console.WriteLine("[Patch2] Patched RegisterDictionaryWantsCrossRef");
        }

        Console.WriteLine("[Patch2] Done.");
    }

    // ── Implementation helpers ────────────────────────────────────────────────

    private static void AddLockFieldInit(ModuleDefinition module, TypeDefinition type, FieldDefinition lockField)
    {
        var cctor = type.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic);
        if (cctor == null)
        {
            cctor = new MethodDefinition(
                ".cctor",
                MethodAttributes.Private | MethodAttributes.Static |
                MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                module.TypeSystem.Void);  // Cecil's TypeSystem.Void — safe for target
            type.Methods.Add(cctor);
            var il2 = cctor.Body.GetILProcessor();
            il2.Append(il2.Create(OpCodes.Ret));
        }

        var il = cctor.Body.GetILProcessor();
        var ret = cctor.Body.Instructions.Last();

        // Get object.ctor from target module's mscorlib
        var objCtor = GetObjectCtor(module);
        il.InsertBefore(ret, il.Create(OpCodes.Newobj, objCtor));
        il.InsertBefore(ret, il.Create(OpCodes.Stsfld, lockField));
    }

    private static void AddWantedRefLockedHelper(ModuleDefinition module, TypeDefinition type, FieldDefinition lockField)
    {
        var wantedRefsField = type.Fields.First(f => f.Name == "wantedRefs");
        var wantedRefType   = type.NestedTypes.First(t => t.Name == "WantedRef");
        var wantedRefRef    = module.ImportReference(wantedRefType);

        var listAddMethod  = GetListAddMethod(module, wantedRefType);
        var monitorEnter   = GetMonitorEnter(module);
        var monitorExit    = GetMonitorExit(module);

        var helper = new MethodDefinition(
            "AddWantedRef_Locked",
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        helper.Parameters.Add(new ParameterDefinition("item", ParameterAttributes.None, wantedRefRef));
        type.Methods.Add(helper);

        var body = helper.Body;
        body.InitLocals = false;
        var il = body.GetILProcessor();

        // Monitor.Enter(_registrationLock)
        il.Emit(OpCodes.Ldsfld, lockField);
        il.Emit(OpCodes.Call, monitorEnter);

        // try { wantedRefs.Add(item); }
        var tryStart = il.Create(OpCodes.Ldsfld, wantedRefsField);
        il.Append(tryStart);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, listAddMethod);

        // finally { Monitor.Exit(_registrationLock); }
        var finallyStart = il.Create(OpCodes.Ldsfld, lockField);
        il.Append(finallyStart);
        il.Emit(OpCodes.Call, monitorExit);

        var ret = il.Create(OpCodes.Ret);
        il.Append(ret);

        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
        {
            TryStart     = tryStart,
            TryEnd       = finallyStart,
            HandlerStart = finallyStart,
            HandlerEnd   = ret
        });
    }

    private static bool PatchRegisterObject(
        ModuleDefinition module,
        TypeDefinition type,
        MethodDefinition method,
        FieldDefinition lockField)
    {
        var body = method.Body;
        var il   = body.GetILProcessor();
        var instructions = body.Instructions.ToList();

        var monitorEnter = GetMonitorEnter(module);
        var monitorExit  = GetMonitorExit(module);

        for (int i = 0; i < instructions.Count - 2; i++)
        {
            if (instructions[i].RefsField("wantedRefs") &&
                instructions[i].OpCode == OpCodes.Ldsfld &&
                instructions[i + 2].CallsMethod("Add"))
            {
                var ldsfldInstr = instructions[i];
                var addCall     = instructions[i + 2];

                // Insert Monitor.Enter(_lock) BEFORE ldsfld wantedRefs
                il.InsertBefore(ldsfldInstr, il.Create(OpCodes.Ldsfld, lockField));
                il.InsertBefore(ldsfldInstr, il.Create(OpCodes.Call, monitorEnter));

                // Insert Monitor.Exit(_lock) AFTER callvirt Add (reuse existing Add instruction as-is)
                var updatedInstrs = body.Instructions.ToList();
                int addCallIdx = updatedInstrs.IndexOf(addCall);
                if (addCallIdx >= 0 && addCallIdx + 1 < updatedInstrs.Count)
                {
                    var afterAdd = updatedInstrs[addCallIdx + 1];
                    il.InsertBefore(afterAdd, il.Create(OpCodes.Ldsfld, lockField));
                    il.InsertBefore(afterAdd, il.Create(OpCodes.Call, monitorExit));
                }
                return true;
            }
        }
        Console.WriteLine($"[Patch2] WARN: Could not find wantedRefs.Add pattern in " +
            $"{method.Name}({string.Join(", ", method.Parameters.Select(p => p.ParameterType.Name))})");
        return false;
    }

    private static bool PatchRegisterListOrDict(
        ModuleDefinition module,
        MethodDefinition method,
        FieldDefinition lockField)
    {
        var body = method.Body;
        var il   = body.GetILProcessor();
        var instructions = body.Instructions.ToList();

        var monitorEnter = GetMonitorEnter(module);
        var monitorExit  = GetMonitorExit(module);

        // Find the first ldsfld wantedListDictRefs
        int startIdx = -1;
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i].RefsField("wantedListDictRefs") &&
                instructions[i].OpCode == OpCodes.Ldsfld)
            { startIdx = i; break; }
        }

        // Find the last wantedRefs.Add
        int endIdx = -1;
        for (int i = instructions.Count - 1; i >= 1; i--)
        {
            if (instructions[i].RefsField("wantedRefs") && instructions[i].OpCode == OpCodes.Ldsfld)
            {
                for (int j = i; j < Math.Min(instructions.Count, i + 5); j++)
                {
                    if (instructions[j].CallsMethod("Add")) { endIdx = j; break; }
                }
                if (endIdx >= 0) break;
            }
        }

        if (startIdx < 0 || endIdx < 0)
        {
            Console.WriteLine($"[Patch2] WARN: Could not find TryGetValue+Add pattern in {method.Name}");
            return false;
        }

        // Insert Monitor.Enter(_lock) BEFORE the first wantedListDictRefs access
        var startInstr = instructions[startIdx];
        il.InsertBefore(startInstr, il.Create(OpCodes.Ldsfld, lockField));
        il.InsertBefore(startInstr, il.Create(OpCodes.Call, monitorEnter));

        // Insert Monitor.Exit(_lock) AFTER the wantedRefs.Add call
        var updatedInstrs = body.Instructions.ToList();
        int addCallIdx = updatedInstrs.IndexOf(instructions[endIdx]);
        if (addCallIdx >= 0 && addCallIdx + 1 < updatedInstrs.Count)
        {
            var afterAdd = updatedInstrs[addCallIdx + 1];
            il.InsertBefore(afterAdd, il.Create(OpCodes.Ldsfld, lockField));
            il.InsertBefore(afterAdd, il.Create(OpCodes.Call, monitorExit));
        }

        return true;
    }

    private static MethodReference GetListAddMethod(ModuleDefinition module, TypeDefinition wantedRefType)
    {
        // Find List<T> from target mscorlib
        TypeDefinition? listTypeDef = null;
        try
        {
            var coreLib = module.AssemblyResolver.Resolve(
                (AssemblyNameReference)module.TypeSystem.CoreLibrary);
            listTypeDef = coreLib.MainModule.GetType("System.Collections.Generic.List`1");
        }
        catch { }

        if (listTypeDef == null)
        {
            foreach (var asmRef in module.AssemblyReferences)
            {
                try
                {
                    var asm = module.AssemblyResolver.Resolve(asmRef);
                    listTypeDef = asm.MainModule.GetType("System.Collections.Generic.List`1");
                    if (listTypeDef != null) break;
                }
                catch { }
            }
        }

        if (listTypeDef == null)
            throw new Exception("Could not find System.Collections.Generic.List`1 in target references");

        var listTypeRef = new GenericInstanceType(module.ImportReference(listTypeDef));
        listTypeRef.GenericArguments.Add(module.ImportReference(wantedRefType));

        var addMethod = new MethodReference("Add", module.TypeSystem.Void, listTypeRef) { HasThis = true };
        addMethod.Parameters.Add(new ParameterDefinition(module.ImportReference(wantedRefType)));
        return addMethod;
    }
}
