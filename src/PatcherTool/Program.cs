using Mono.Cecil;
using Mono.Cecil.Rocks;
using System;
using System.IO;
using System.Linq;

namespace RimWorldPatcher;

class Program
{
    static int Main(string[] args)
    {
        // ── Paths ────────────────────────────────────────────────────────────
        string gameDir   = args.FirstOrDefault(a => !a.StartsWith("--"))
            ?? @"C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed";
        string helpersDir = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"))
            ?? Path.Combine(AppContext.BaseDirectory, "..", "Helpers", "bin", "Release", "net472");

        string targetDll  = Path.Combine(gameDir, "Assembly-CSharp.dll");
        string helpersDll = Path.Combine(helpersDir, "RimWorldStartupHelpers.dll");
        string backupDll  = targetDll + ".original";

        Console.WriteLine("══════════════════════════════════════════════");
        Console.WriteLine("RimWorld Hyperdrive — DLL Patcher");
        Console.WriteLine("══════════════════════════════════════════════");
        Console.WriteLine($"Target : {targetDll}");
        Console.WriteLine($"Helpers: {helpersDll}");

        // ── Flags ────────────────────────────────────────────────────────────
        bool freshRequested   = args.Any(a => a == "--fresh");
        bool restoreRequested = args.Any(a => a == "--restore");

        var skipPatches = new System.Collections.Generic.HashSet<int>();
        foreach (var a in args)
            if (a.StartsWith("--skip="))
                foreach (var p in a.Substring(7).Split(','))
                    if (int.TryParse(p.Trim(), out int n)) skipPatches.Add(n);

        if (skipPatches.Count > 0)
            Console.WriteLine($"[INFO] Skipping patches: {string.Join(", ", skipPatches)}");

        if (!File.Exists(targetDll)) { Console.Error.WriteLine($"ERROR: Target not found: {targetDll}"); return 1; }

        // ── Restore mode ─────────────────────────────────────────────────────
        if (restoreRequested)
        {
            if (!File.Exists(backupDll)) { Console.Error.WriteLine("ERROR: No backup found to restore."); return 1; }
            File.Copy(backupDll, targetDll, overwrite: true);
            File.Delete(backupDll);
            Console.WriteLine($"Restored original Assembly-CSharp.dll. Backup removed.");
            return 0;
        }

        if (!File.Exists(helpersDll)) { Console.Error.WriteLine($"ERROR: Helpers not found: {helpersDll}"); return 1; }

        // ── Backup / fresh ───────────────────────────────────────────────────
        // --fresh: user signals a Steam update happened — discard the old backup
        if (freshRequested && File.Exists(backupDll))
        {
            File.Delete(backupDll);
            Console.WriteLine("--fresh: Old backup removed. Creating fresh backup from current DLL.");
        }

        if (!File.Exists(backupDll))
        {
            File.Copy(targetDll, backupDll);
            Console.WriteLine($"Backup created: {backupDll}");
        }
        else
        {
            // Always patch from the clean original — avoids double-patching on re-runs
            File.Copy(backupDll, targetDll, overwrite: true);
            Console.WriteLine("Restored clean original from backup (ensures idempotent re-patch).");
        }

        // ── Load assemblies ──────────────────────────────────────────────────
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(gameDir);
        var readerParams = new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadWrite = false,
            ReadSymbols = false,
            InMemory = true
        };

        Console.WriteLine("\nLoading assemblies...");
        AssemblyDefinition targetAsm;
        AssemblyDefinition helpersAsm;
        try
        {
            targetAsm  = AssemblyDefinition.ReadAssembly(targetDll, readerParams);
            helpersAsm = AssemblyDefinition.ReadAssembly(helpersDll, new ReaderParameters
            {
                AssemblyResolver = resolver,
                ReadSymbols = false
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR loading assemblies: " + ex);
            return 1;
        }

        var targetModule  = targetAsm.MainModule;
        var helpersModule = helpersAsm.MainModule;

        Console.WriteLine($"Target  : {targetAsm.FullName} ({targetModule.Types.Count} types)");
        Console.WriteLine($"Helpers : {helpersAsm.FullName} ({helpersModule.Types.Count} types)");

        // ── Apply patches ─────────────────────────────────────────────────────
        // Patch 4 (ParseAndProcessXML body replacement) is permanently disabled:
        // it breaks Harmony transpiler patches from mods targeting that method,
        // and contributes only ~1s vs the 55s+ saved by Patches 1, 3, 5, 6.
        skipPatches.Add(4);

        Console.WriteLine("\n── Applying patches ──────────────────────────────");
        bool success = true;

        if (!skipPatches.Contains(1)) try
        { XmlLoaderPatch.Apply(targetModule); }
        catch (Exception ex) { Console.Error.WriteLine("[Patch1] FAILED: " + ex.Message); success = false; }
        else Console.WriteLine("[Patch1] SKIPPED");

        if (!skipPatches.Contains(2)) try
        { CrossRefPatch.Apply(targetModule); }
        catch (Exception ex) { Console.Error.WriteLine("[Patch2] FAILED: " + ex.Message); success = false; }
        else Console.WriteLine("[Patch2] SKIPPED");

        if (!skipPatches.Contains(3) || !skipPatches.Contains(4))
        {
            bool applyLoad  = !skipPatches.Contains(3);
            bool applyParse = !skipPatches.Contains(4);
            try { ModManagerPatch.Apply(targetModule, helpersModule, applyLoad, applyParse); }
            catch (Exception ex)
            { Console.Error.WriteLine("[Patch3/4] FAILED: " + ex.Message); Console.Error.WriteLine(ex.StackTrace); success = false; }
        }
        else Console.WriteLine("[Patch3/4] SKIPPED");

        if (!skipPatches.Contains(5)) try
        { ContentPrefetchPatch.Apply(targetModule, helpersModule); }
        catch (Exception ex) { Console.Error.WriteLine("[Patch5] FAILED: " + ex.Message); success = false; }
        else Console.WriteLine("[Patch5] SKIPPED");

        if (!skipPatches.Contains(6)) try
        { ApplyPatchesPatch.Apply(targetModule, helpersModule); }
        catch (Exception ex) { Console.Error.WriteLine("[Patch6] FAILED: " + ex.Message); success = false; }
        else Console.WriteLine("[Patch6] SKIPPED");

        if (!success)
        {
            Console.Error.WriteLine("\nOne or more patches FAILED. Assembly NOT written.");
            return 1;
        }

        // ── Write patched assembly ────────────────────────────────────────────
        Console.WriteLine("\n── Writing patched assembly ──────────────────────");
        try
        {
            string tempPath = targetDll + ".patching";
            targetAsm.Write(tempPath);
            targetAsm.Dispose();
            if (File.Exists(targetDll)) File.Delete(targetDll);
            File.Move(tempPath, targetDll);
            Console.WriteLine($"Written: {targetDll}");
        }
        catch (Exception ex) { Console.Error.WriteLine("ERROR writing assembly: " + ex); return 1; }

        // ── Deploy helpers DLL to Managed/ ───────────────────────────────────
        Console.WriteLine("\n── Deploying helpers DLL ──────────────────────────────");
        string helpersDeployPath = Path.Combine(gameDir, "RimWorldStartupHelpers.dll");
        try
        {
            File.Copy(helpersDll, helpersDeployPath, overwrite: true);
            Console.WriteLine($"Deployed: {helpersDeployPath}");
        }
        catch (Exception ex) { Console.Error.WriteLine("ERROR deploying helpers DLL: " + ex); return 1; }

        Console.WriteLine("\n══════════════════════════════════════════════");
        Console.WriteLine("ALL PATCHES APPLIED SUCCESSFULLY");
        Console.WriteLine($"Startup time: expect ~60% of vanilla with a heavy modlist.");
        Console.WriteLine("To restore original: run patch.ps1 -Restore");
        Console.WriteLine("══════════════════════════════════════════════");
        return 0;
    }
}
