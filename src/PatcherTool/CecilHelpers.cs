using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System;
using System.IO;
using System.Linq;

namespace RimWorldPatcher;

// ─────────────────────────────────────────────────────────────────────────────
// Cecil extension helpers
// ─────────────────────────────────────────────────────────────────────────────
static class CecilHelpers
{
    /// <summary>Finds a type in the module by simple name (no namespace required if unique).</summary>
    public static TypeDefinition MustGetType(this ModuleDefinition mod, string fullName)
    {
        var t = mod.Types.FirstOrDefault(x => x.FullName == fullName)
             ?? mod.GetAllTypes().FirstOrDefault(x => x.FullName == fullName);
        if (t == null) throw new Exception($"Type '{fullName}' not found in module {mod.Name}");
        return t;
    }

    /// <summary>Finds the first method matching name exactly (or throws).</summary>
    public static MethodDefinition MustGetMethod(this TypeDefinition type, string name)
    {
        var m = type.Methods.FirstOrDefault(x => x.Name == name);
        if (m == null) throw new Exception($"Method '{name}' not found in type {type.FullName}");
        return m;
    }

    /// <summary>Finds the first method matching name and predicate.</summary>
    public static MethodDefinition MustGetMethod(this TypeDefinition type, string name, Func<MethodDefinition, bool> predicate)
    {
        var m = type.Methods.FirstOrDefault(x => x.Name == name && predicate(x));
        if (m == null) throw new Exception($"Method '{name}' (with predicate) not found in type {type.FullName}");
        return m;
    }

    /// <summary>Finds a field by name (or throws).</summary>
    public static FieldDefinition MustGetField(this TypeDefinition type, string name)
    {
        var f = type.Fields.FirstOrDefault(x => x.Name == name);
        if (f == null) throw new Exception($"Field '{name}' not found in type {type.FullName}");
        return f;
    }

    /// <summary>True if the instruction calls the named method (partial match on name).</summary>
    public static bool CallsMethod(this Instruction instr, string methodName)
        => (instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt)
        && instr.Operand is MethodReference mr && mr.Name == methodName;

    /// <summary>True if the instruction loads/stores a field with this name.</summary>
    public static bool RefsField(this Instruction instr, string fieldName)
        => (instr.OpCode == OpCodes.Ldsfld || instr.OpCode == OpCodes.Ldfld
         || instr.OpCode == OpCodes.Stsfld || instr.OpCode == OpCodes.Stfld)
        && instr.Operand is FieldReference fr && fr.Name == fieldName;

    /// <summary>Replace all references from srcAssembly to targetAssembly in the given type ref.</summary>
    public static TypeReference ImportFromHelpers(this ModuleDefinition target, TypeReference typeRef, ModuleDefinition helpersModule)
    {
        if (typeRef.Module == helpersModule)
        {
            // Cross-module helper ref → resolve in helpers, find or import into target
            var resolved = typeRef.Resolve();
            if (resolved.Module == helpersModule)
            {
                // The type IS a helper type; find its clone in target (already copied)
                return target.Types.First(t => t.FullName == typeRef.FullName);
            }
        }
        return target.ImportReference(typeRef);
    }
}
