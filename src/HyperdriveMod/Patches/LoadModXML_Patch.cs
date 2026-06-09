using System.Collections.Generic;
using HarmonyLib;
using Verse;
using Verse.StartupOptimizer;

namespace RimWorldHyperdrive.Patches
{
    // Patch 3: replace the sequential mod-XML loader with the parallel one from the
    // shared brain. Also kicks off the background texture/sound prefetch.
    [HarmonyPatch(typeof(LoadedModManager), "LoadModXML")]
    public static class LoadModXML_Patch
    {
        // __0 = the original method's first argument (bool hotReload).
        public static bool Prefix(ref List<LoadableXmlAsset> __result, bool __0)
        {
            __result = OptimizedModManager.LoadModXML_Parallel(
                LoadedModManager.RunningModsListForReading, __0);
            return false; // skip the original sequential implementation
        }
    }
}
