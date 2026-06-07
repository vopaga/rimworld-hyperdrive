using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Linq;

namespace RimWorldPatcher;

/// <summary>
/// Patch 6 – ApplyPatches XPath index
///
/// a) Injects FastXPath.BuildIndex(xmlDoc) at start of LoadedModManager.ApplyPatches
///    and FastXPath.ClearIndex() at the end.
///
/// b) In each PatchOperation*.ApplyWorker, replaces every call to
///    XmlDocument.SelectNodes(string) / XmlDocument.SelectSingleNode(string)
///    with FastXPath.FastSelectNodes(XmlDocument, string) / FastSelectSingleNode.
///
/// Net effect: 13,300 full-document XPath scans → O(1) hash lookups.
/// Expected speedup: ~10x for ApplyPatches (76s → ~8s).
/// </summary>
static class ApplyPatchesPatch
{
    public static void Apply(ModuleDefinition target, ModuleDefinition helpers)
    {
        Console.WriteLine("[Patch6] Patching ApplyPatches with FastXPath index...");

        var fastXPathType       = helpers.MustGetType("Verse.StartupOptimizer.FastXPath");
        var buildIndexRef       = target.ImportReference(fastXPathType.MustGetMethod("BuildIndex"));
        var clearIndexRef       = target.ImportReference(fastXPathType.MustGetMethod("ClearIndex"));
        var fastSelectNodesRef  = target.ImportReference(fastXPathType.MustGetMethod("FastSelectNodes"));
        var fastSelectSingleRef = target.ImportReference(fastXPathType.MustGetMethod("FastSelectSingleNode"));

        // ── Part A: inject BuildIndex / ClearIndex into LoadedModManager.ApplyPatches ─
        var modMgr = target.MustGetType("Verse.LoadedModManager");
        PatchApplyPatchesMethod(target, modMgr, buildIndexRef, clearIndexRef);

        // ── Part B: auto-scan ALL PatchOperation subclasses for SelectNodes/SelectSingleNode ─
        // This is safer than a manual list — it catches any subclass in Assembly-CSharp.dll,
        // including ones we missed or that were added in newer game versions.
        int totalPatched = 0;
        int typesPatched = 0;

        foreach (var type in target.Types)
        {
            if (!DerivesFromPatchOperation(type)) continue;

            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;

                int nNodes  = ReplaceSelectNodes(method, fastSelectNodesRef);
                int nSingle = ReplaceSelectSingleNode(method, fastSelectSingleRef);

                if (nNodes + nSingle == 0) continue;

                Console.WriteLine($"  [{type.Name}.{method.Name}]  SelectNodes={nNodes}  SelectSingleNode={nSingle}");
                totalPatched += nNodes + nSingle;
                typesPatched++;
            }
        }

        Console.WriteLine($"[Patch6] XPath calls redirected: {totalPatched} across {typesPatched} method(s).");
        Console.WriteLine("[Patch6] Done.");
    }

    // Returns true if 'type' derives from Verse.PatchOperation (at any depth).
    private static bool DerivesFromPatchOperation(TypeDefinition type)
    {
        try
        {
            var bt = type.BaseType;
            while (bt != null)
            {
                if (bt.FullName == "Verse.PatchOperation") return true;
                var resolved = bt.Resolve();
                if (resolved == null) break;
                bt = resolved.BaseType;
            }
        }
        catch { /* unresolvable external base type — skip */ }
        return false;
    }

    // ── ApplyPatches: BuildIndex at entry, ClearIndex before every ret ────────

    private static void PatchApplyPatchesMethod(
        ModuleDefinition target,
        TypeDefinition modMgr,
        MethodReference buildIndexRef,
        MethodReference clearIndexRef)
    {
        // Find ApplyPatches — signature: static void ApplyPatches(XmlDocument, Dictionary<XmlNode,LoadableXmlAsset>)
        var method = modMgr.Methods.FirstOrDefault(m =>
            m.Name == "ApplyPatches" && m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.Name == "XmlDocument");

        if (method == null)
            throw new Exception("LoadedModManager.ApplyPatches(XmlDocument, ...) not found");

        var body = method.Body;
        var il   = body.GetILProcessor();

        // INSERT at the very beginning: ldarg.0; call BuildIndex
        var first = body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));        // push XmlDocument
        il.InsertBefore(first, il.Create(OpCodes.Call, buildIndexRef));

        // INSERT before every Ret: call ClearIndex
        // (collect first, then insert to avoid iterator invalidation)
        var retInstructions = body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
        foreach (var ret in retInstructions)
        {
            il.InsertBefore(ret, il.Create(OpCodes.Call, clearIndexRef));
        }

        Console.WriteLine($"  [LoadedModManager.ApplyPatches] BuildIndex at entry, ClearIndex before {retInstructions.Count} ret(s)");
    }

    // ── Replace callvirt SelectNodes(string) → call FastSelectNodes(XmlDocument, string) ─

    private static int ReplaceSelectNodes(MethodDefinition method, MethodReference fastRef)
    {
        int count = 0;
        var instructions = method.Body.Instructions;

        for (int i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];
            if (instr.OpCode != OpCodes.Callvirt) continue;
            if (instr.Operand is not MethodReference mr) continue;
            if (mr.Name != "SelectNodes") continue;
            if (mr.Parameters.Count != 1) continue; // must be SelectNodes(string)
            if (mr.Parameters[0].ParameterType.FullName != "System.String") continue;

            // Replace callvirt → call static
            // Stack at this point: [xmlDocument, xpathString] — exactly what FastSelectNodes needs
            instr.OpCode  = OpCodes.Call;
            instr.Operand = fastRef;
            count++;
        }
        return count;
    }

    private static int ReplaceSelectSingleNode(MethodDefinition method, MethodReference fastRef)
    {
        int count = 0;
        var instructions = method.Body.Instructions;

        for (int i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];
            if (instr.OpCode != OpCodes.Callvirt) continue;
            if (instr.Operand is not MethodReference mr) continue;
            if (mr.Name != "SelectSingleNode") continue;
            if (mr.Parameters.Count != 1) continue;
            if (mr.Parameters[0].ParameterType.FullName != "System.String") continue;

            instr.OpCode  = OpCodes.Call;
            instr.Operand = fastRef;
            count++;
        }
        return count;
    }
}
