using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Linq;

namespace RimWorldPatcher;

/// <summary>
/// Patches 3 &amp; 4 – LoadedModManager.LoadModXML + ParseAndProcessXML
///
/// Strategy:
///   RimWorldStartupHelpers.dll is deployed alongside Assembly-CSharp.dll in Managed/.
///   We simply redirect LoadModXML and ParseAndProcessXML to call the optimized
///   methods in that external DLL. No type copying needed — Mono loads the DLL
///   at runtime just like any other assembly in Managed/.
/// </summary>
static class ModManagerPatch
{
    public static void Apply(ModuleDefinition target, ModuleDefinition helpers,
        bool applyLoadXML = true, bool applyParseXML = true)
    {
        if (!applyLoadXML && !applyParseXML) return;
        Console.WriteLine("[Patch3/4] Patching LoadedModManager with external helpers DLL...");

        var helperType = helpers.MustGetType("Verse.StartupOptimizer.OptimizedModManager");
        var modMgr           = target.MustGetType("Verse.LoadedModManager");
        var runningModsField = modMgr.Fields.First(f => f.Name == "runningMods");
        var patchedDefsField = modMgr.Fields.First(f => f.Name == "patchedDefs");

        if (applyLoadXML)
        {
            var loadParallelRef = target.ImportReference(helperType.MustGetMethod("LoadModXML_Parallel"));
            ReplaceMethodBody_LoadModXML(target, modMgr.MustGetMethod("LoadModXML"),
                runningModsField, loadParallelRef);
            Console.WriteLine("[Patch3] LoadModXML → LoadModXML_Parallel");
        }
        else Console.WriteLine("[Patch3] LoadModXML SKIPPED");

        // Patch 4 is permanently disabled in Program.cs (skipPatches.Add(4)).
        // WHY: some mods (e.g. HugsLib) use Harmony transpilers that target
        // ParseAndProcessXML by IL anchor. Replacing the full method body with a
        // 6-instruction trampoline destroys those anchors → "failed to apply patch"
        // errors at runtime. Net gain was only ~1s vs the 55s+ from Patches 3+6,
        // so correctness wins. Code kept here for future research.
        if (applyParseXML)
        {
            var parseParallelRef = target.ImportReference(helperType.MustGetMethod("ParseAndProcessXML_Optimized"));
            ReplaceMethodBody_ParseAndProcessXML(target, modMgr.MustGetMethod("ParseAndProcessXML"),
                patchedDefsField, runningModsField, parseParallelRef);
            Console.WriteLine("[Patch4] ParseAndProcessXML → ParseAndProcessXML_Optimized");
        }
        else Console.WriteLine("[Patch4] ParseAndProcessXML SKIPPED (permanently disabled — see comment above)");

        Console.WriteLine("[Patch3/4] Done.");
    }

    private static void ReplaceMethodBody_LoadModXML(
        ModuleDefinition target,
        MethodDefinition method,
        FieldDefinition runningModsField,
        MethodReference parallelHelper)
    {
        // ldsfld   runningMods   ; List<ModContentPack>
        // ldarg.0               ; bool hotReload
        // call     LoadModXML_Parallel
        // ret
        var body = method.Body;
        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();

        var il = body.GetILProcessor();
        il.Emit(OpCodes.Ldsfld, target.ImportReference(runningModsField));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, parallelHelper);
        il.Emit(OpCodes.Ret);
    }

    private static void ReplaceMethodBody_ParseAndProcessXML(
        ModuleDefinition target,
        MethodDefinition method,
        FieldDefinition patchedDefsField,
        FieldDefinition runningModsField,
        MethodReference parallelHelper)
    {
        // ldarg.0   ; XmlDocument
        // ldarg.1   ; Dictionary assetlookup
        // ldarg.2   ; bool hotReload
        // ldsfld    patchedDefs
        // ldsfld    runningMods
        // call      ParseAndProcessXML_Optimized
        // ret
        var body = method.Body;
        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();

        var il = body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldsfld, target.ImportReference(patchedDefsField));
        il.Emit(OpCodes.Ldsfld, target.ImportReference(runningModsField));
        il.Emit(OpCodes.Call, parallelHelper);
        il.Emit(OpCodes.Ret);
    }
}
