using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimWorldHyperdrive
{
    // Harmony (in-memory) edition of Hyperdrive.
    // Patches are applied in the Mod constructor, which runs before the XML load
    // phase (verified by the timing-gate prototype). Each patch is applied
    // independently and fail-soft: if one cannot be applied (game update, conflict),
    // it is skipped and logged, the rest still apply, and the game keeps loading.
    public class HyperdriveMod : Mod
    {
        private const string HarmonyId = "vopaga.hyperdrive";

        public HyperdriveMod(ModContentPack content) : base(content)
        {
            var harmony = new Harmony(HarmonyId);

            // Body-replacing patch: its Prefix returns false, so it cannot coexist with
            // another mod's Prefix/Transpiler on the same method without one silently
            // suppressing the other. If someone else already touched LoadModXML, stand down
            // and let them run. (Best-effort: this only sees mods whose patch ran before
            // ours, i.e. that load earlier. Mods loading after us stack normally and one
            // wins, no crash.)
            ApplyGuarded(harmony, typeof(Patches.LoadModXML_Patch), "parallel mod XML load",
                AccessTools.Method(typeof(LoadedModManager), "LoadModXML"));

            // Additive / self-guarding patches: Harmony stacks them safely. The transpilers
            // pattern-match and no-op (with a log) if the IL was already changed by someone
            // else; ApplyPatches only builds/clears an index and never replaces the body. So
            // a foreign patch here is not a conflict, and guarding them would only risk
            // dropping our biggest win when another mod merely adds a postfix.
            ApplyFailSoft(harmony, typeof(Patches.XmlAssetsInModFolder_Patch), "XML loader thread count");
            ApplyFailSoft(harmony, typeof(Patches.LoadTextureViaImageConversion_Patch), "texture prefetch cache");
            ApplyFailSoft(harmony, typeof(Patches.ApplyPatches_Patch), "XPath index build/clear");
            ApplyFailSoft(harmony, typeof(Patches.PatchOperationApplyWorker_Patch), "XPath fast-select redirect");
        }

        private static void ApplyGuarded(Harmony harmony, Type patchClass, string label, MethodBase target)
        {
            var conflict = ForeignReplacement(target);
            if (conflict != null)
            {
                Log.Warning($"[Hyperdrive] STOOD DOWN on {label}: '{target?.Name}' is already patched by '{conflict}'. Letting it run instead.");
                return;
            }
            ApplyFailSoft(harmony, patchClass, label);
        }

        // Returns the owner id of a foreign Prefix/Transpiler on the method, or null.
        // A foreign Postfix is fine (it still runs on our __result), so those are ignored.
        private static string ForeignReplacement(MethodBase target)
        {
            if (target == null) return null;
            var info = Harmony.GetPatchInfo(target);
            if (info == null) return null;
            foreach (var p in info.Prefixes)
                if (p.owner != HarmonyId) return p.owner;
            foreach (var t in info.Transpilers)
                if (t.owner != HarmonyId) return t.owner;
            return null;
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
