using System;
using HarmonyLib;
using Verse;

namespace RimWorldHyperdrive
{
    // Harmony (in-memory) edition of RimWorld Hyperdrive.
    // Patches are applied in the Mod constructor, which runs before the XML load
    // phase (verified by the timing-gate prototype). Each patch is applied
    // independently and fail-soft: if one cannot be applied (game update, conflict),
    // it is skipped and logged, the rest still apply, and the game keeps loading.
    public class HyperdriveMod : Mod
    {
        public HyperdriveMod(ModContentPack content) : base(content)
        {
            var harmony = new Harmony("vopaga.hyperdrive");
            ApplyFailSoft(harmony, typeof(Patches.LoadModXML_Patch), "parallel mod XML load");
            ApplyFailSoft(harmony, typeof(Patches.XmlAssetsInModFolder_Patch), "XML loader thread count");
            ApplyFailSoft(harmony, typeof(Patches.LoadTextureViaImageConversion_Patch), "texture prefetch cache");
            ApplyFailSoft(harmony, typeof(Patches.ApplyPatches_Patch), "XPath index build/clear");
            ApplyFailSoft(harmony, typeof(Patches.PatchOperationApplyWorker_Patch), "XPath fast-select redirect");
        }

        private static void ApplyFailSoft(Harmony harmony, Type patchClass, string label)
        {
            try
            {
                harmony.CreateClassProcessor(patchClass).Patch();
                Log.Message($"[Hyperdrive] applied: {label}");
            }
            catch (Exception e)
            {
                Log.Warning($"[Hyperdrive] SKIPPED {label} (patch failed, game still loads): {e.Message}");
            }
        }
    }
}
