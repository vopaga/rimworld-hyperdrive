using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldPatcher;

/// <summary>
/// Patch 1 – DirectXmlLoader.XmlAssetsInModFolder
/// Change hardcoded Thread[2] to Thread[Max(3, ProcessorCount-1)]
/// This affects every mod folder XML load (called ~150× on heavy mod lists)
///
/// KEY: All BCL method references are resolved via Cecil from the target module's
///      referenced assemblies (mscorlib), NOT via C# reflection.
/// </summary>
static class XmlLoaderPatch
{
    public static void Apply(ModuleDefinition module)
    {
        Console.WriteLine("[Patch1] Patching DirectXmlLoader.XmlAssetsInModFolder thread count...");

        var type   = module.MustGetType("Verse.DirectXmlLoader");
        var method = type.MustGetMethod("XmlAssetsInModFolder");
        var body   = method.Body;
        var il     = body.GetILProcessor();

        // Resolve Environment.get_ProcessorCount and Math.Max from TARGET mscorlib
        var getProcessorCount = ResolveMethod(module, "System.Environment", "get_ProcessorCount", 0);
        var mathMax           = ResolveMethod(module, "System.Math", "Max",
            2, paramTypes: new[] { "Int32", "Int32" });

        // Find: ldc.i4.2  followed by  newarr Thread
        Instruction? target = null;
        for (int i = 0; i < body.Instructions.Count - 1; i++)
        {
            var cur  = body.Instructions[i];
            var next = body.Instructions[i + 1];
            if (cur.OpCode == OpCodes.Ldc_I4_2 &&
                next.OpCode == OpCodes.Newarr &&
                next.Operand is TypeReference tr && tr.Name == "Thread")
            {
                target = cur;
                break;
            }
        }

        if (target == null)
        {
            Console.WriteLine("[Patch1] WARN: Could not find 'ldc.i4.2 / newarr Thread' — already patched?");
            return;
        }

        // Replace  ldc.i4.2  with:
        //   call Environment::get_ProcessorCount()   → ProcessorCount
        //   ldc.i4.1                                  → 1
        //   sub                                       → ProcessorCount - 1
        //   ldc.i4.3                                  → 3
        //   call Math::Max(int, int)                  → Max(3, ProcessorCount-1)
        il.InsertBefore(target, il.Create(OpCodes.Call, getProcessorCount));
        il.InsertBefore(target, il.Create(OpCodes.Ldc_I4_1));
        il.InsertBefore(target, il.Create(OpCodes.Sub));
        il.InsertBefore(target, il.Create(OpCodes.Ldc_I4_3));
        il.InsertBefore(target, il.Create(OpCodes.Call, mathMax));
        il.Remove(target); // remove the old ldc.i4.2

        Console.WriteLine("[Patch1] OK — Thread count changed from 2 to Max(3, ProcessorCount-1)");
    }

    // ── Cecil-native type/method resolution (no typeof() reflection) ──────────

    private static MethodReference ResolveMethod(
        ModuleDefinition module,
        string typeFullName,
        string methodName,
        int paramCount,
        string[]? paramTypes = null)
    {
        // Try core library first
        TypeDefinition? found = null;
        try
        {
            var coreLib = module.AssemblyResolver.Resolve(
                (AssemblyNameReference)module.TypeSystem.CoreLibrary);
            found = coreLib.MainModule.GetType(typeFullName);
        }
        catch { }

        if (found == null)
        {
            foreach (var asmRef in module.AssemblyReferences)
            {
                try
                {
                    var asm = module.AssemblyResolver.Resolve(asmRef);
                    found = asm.MainModule.GetType(typeFullName);
                    if (found != null) break;
                }
                catch { }
            }
        }

        if (found == null)
            throw new Exception($"Type '{typeFullName}' not found in target references");

        MethodDefinition? method;
        if (paramTypes != null)
        {
            method = found.Methods.FirstOrDefault(m =>
                m.Name == methodName &&
                m.Parameters.Count == paramCount &&
                m.Parameters.Select(p => p.ParameterType.Name).SequenceEqual(paramTypes));
        }
        else
        {
            method = found.Methods.FirstOrDefault(m =>
                m.Name == methodName && m.Parameters.Count == paramCount);
        }

        if (method == null)
            throw new Exception($"Method '{methodName}' (params={paramCount}) not found in '{typeFullName}'");

        return module.ImportReference(method);
    }
}
