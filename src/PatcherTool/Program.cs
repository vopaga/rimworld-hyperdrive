using Mono.Cecil;
using Mono.Cecil.Rocks;
using MonoMod;
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
        // --fresh: user signals a Steam update happened — discard the old backup and
        // re-baseline from the current (assumed vanilla) DLL. Guard the data-loss case:
        // if the current DLL is still PATCHED, capturing it as the "clean" backup would
        // destroy the real original. Refuse in that case.
        if (freshRequested && File.Exists(backupDll))
        {
            if (IsPatched(targetDll))
            {
                Console.Error.WriteLine(
                    "ERROR: --fresh refused. The current Assembly-CSharp.dll is already PATCHED, so a fresh\n" +
                    "backup would overwrite your clean original with a patched copy. If RimWorld was NOT\n" +
                    "updated, just re-run without --fresh (it re-patches from the existing backup). If you\n" +
                    "really did update the game, run --restore first, then patch normally.");
                return 1;
            }
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

        // ── Load + merge helper into Assembly-CSharp (via MonoMod) ────────────
        // Instead of shipping RimWorldStartupHelpers.dll in Managed/, merge its types
        // straight into Assembly-CSharp. MonoMod copies the new types and relinks the
        // helper's references to Assembly-CSharp back into the module itself.
        // (A separate DLL in Managed/ crashes Prepatcher — see issue #3.)
        Console.WriteLine("\nLoading + merging helper into Assembly-CSharp...");
        MonoModder modder;
        try
        {
            modder = new MonoModder { InputPath = targetDll, MissingDependencyThrow = false };
            modder.DependencyDirs.Add(gameDir);
            modder.Read();
            modder.ReadMod(helpersDll);
            modder.MapDependencies();
            modder.AutoPatch();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR merging helper into Assembly-CSharp: " + ex);
            return 1;
        }

        var targetModule = modder.Module;
        Console.WriteLine($"Target  : {targetModule.Assembly.FullName} ({targetModule.Types.Count} types after merge)");

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
            try { ModManagerPatch.Apply(targetModule, applyLoad, applyParse); }
            catch (Exception ex)
            { Console.Error.WriteLine("[Patch3/4] FAILED: " + ex.Message); Console.Error.WriteLine(ex.StackTrace); success = false; }
        }
        else Console.WriteLine("[Patch3/4] SKIPPED");

        // Patch 5 is the only version-fragile target (the texture method was renamed across
        // 1.6 builds). It does all its lookups before mutating IL, so a miss can't half-apply.
        // Treat a failure as a skip — the prefetch optimization is lost, not the whole patch.
        if (!skipPatches.Contains(5)) try
        { ContentPrefetchPatch.Apply(targetModule); }
        catch (Exception ex) { Console.Error.WriteLine("[Patch5] SKIPPED (could not apply, continuing): " + ex.Message); }
        else Console.WriteLine("[Patch5] SKIPPED");

        if (!skipPatches.Contains(6)) try
        { ApplyPatchesPatch.Apply(targetModule); }
        catch (Exception ex) { Console.Error.WriteLine("[Patch6] FAILED: " + ex.Message); success = false; }
        else Console.WriteLine("[Patch6] SKIPPED");

        if (!success)
        {
            Console.Error.WriteLine("\nOne or more patches FAILED. Assembly NOT written.");
            modder.Dispose();
            return 1;
        }

        // ── Safety: no MonoMod / helper-DLL refs may leak into the output ─────
        var leakedRefs = targetModule.AssemblyReferences
            .Where(r => r.Name.StartsWith("MonoMod") || r.Name == "RimWorldStartupHelpers")
            .Select(r => r.Name).Distinct().ToList();
        if (leakedRefs.Count > 0)
        {
            Console.Error.WriteLine("ERROR: merged assembly still references: " + string.Join(", ", leakedRefs));
            modder.Dispose();
            return 1;
        }

        // ── Write patched assembly ────────────────────────────────────────────
        Console.WriteLine("\n── Writing patched assembly ──────────────────────");
        try
        {
            string tempPath = targetDll + ".patching";
            modder.Write(outputPath: tempPath);
            modder.Dispose();                       // releases the read lock on targetDll
            if (File.Exists(targetDll)) File.Delete(targetDll);
            File.Move(tempPath, targetDll);
            Console.WriteLine($"Written: {targetDll}");
        }
        catch (Exception ex) { Console.Error.WriteLine("ERROR writing assembly: " + ex); return 1; }

        // ── Remove helper DLL left over from older versions ──────────────────
        // v1.0.x deployed RimWorldStartupHelpers.dll here; it is now merged in, and an
        // orphaned copy crashes Prepatcher (issue #3), so delete it on (re)patch.
        string staleHelper = Path.Combine(gameDir, "RimWorldStartupHelpers.dll");
        try
        {
            if (File.Exists(staleHelper))
            {
                File.Delete(staleHelper);
                Console.WriteLine($"Removed stale helper DLL: {staleHelper}");
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("WARN: could not remove stale helper DLL: " + ex.Message); }

        Console.WriteLine("\n══════════════════════════════════════════════");
        Console.WriteLine("ALL PATCHES APPLIED SUCCESSFULLY");
        Console.WriteLine($"Startup time: expect ~60% of vanilla with a heavy modlist.");
        Console.WriteLine("To restore original: patch.ps1 -Restore (Windows) or patch.sh --restore (Linux/macOS).");
        Console.WriteLine("══════════════════════════════════════════════");
        return 0;
    }

    // Detects a patched Assembly-CSharp by looking for our merged helper namespace, whose
    // type/namespace names live as UTF-8 strings in the metadata. Vanilla DLLs never contain it.
    static bool IsPatched(string dll)
    {
        try
        {
            var bytes = File.ReadAllBytes(dll);
            return System.Text.Encoding.ASCII.GetString(bytes).Contains("StartupOptimizer");
        }
        catch { return false; }
    }
}
