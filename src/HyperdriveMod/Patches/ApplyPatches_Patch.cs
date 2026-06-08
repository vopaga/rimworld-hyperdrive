using System.Collections.Generic;
using System.Xml;
using HarmonyLib;
using Verse;
using Verse.StartupOptimizer;

namespace RimWorldHyperdrive.Patches
{
    // Patch 6 (Part A): build the O(1) (typeName, defName) -> XmlNode index before the
    // patch phase runs, and clear it afterwards. The Finalizer runs even if ApplyPatches
    // throws, so the index never leaks.
    [HarmonyPatch(typeof(LoadedModManager), "ApplyPatches",
        new[] { typeof(XmlDocument), typeof(Dictionary<XmlNode, LoadableXmlAsset>) })]
    public static class ApplyPatches_Patch
    {
        public static void Prefix(XmlDocument __0)
        {
            FastXPath.BuildIndex(__0);
        }

        public static void Finalizer()
        {
            FastXPath.ClearIndex();
        }
    }
}
