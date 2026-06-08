using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld.IO;
using UnityEngine;
using Verse;
using Verse.StartupOptimizer;

namespace RimWorldHyperdrive.Patches
{
    // Patch 5: serve texture bytes from the background prefetch cache when available,
    // otherwise fall back to the original disk read. Equivalent to:
    //   byte[] data = OptimizedModManager.GetCachedBytes(file.FullPath) ?? file.ReadAllBytes();
    [HarmonyPatch(typeof(ModContentLoader<Texture2D>), "LoadTextureViaImageConversion")]
    public static class LoadTextureViaImageConversion_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var list = instructions.ToList();
            var getCached = AccessTools.Method(typeof(OptimizedModManager), nameof(OptimizedModManager.GetCachedBytes));
            var getFullPath = AccessTools.PropertyGetter(typeof(VirtualFile), "FullPath");

            int idx = -1;
            for (int i = 0; i < list.Count; i++)
            {
                if ((list[i].opcode == OpCodes.Callvirt || list[i].opcode == OpCodes.Call) &&
                    list[i].operand is MethodInfo m && m.Name == "ReadAllBytes" &&
                    m.DeclaringType != null && m.DeclaringType.Name == "VirtualFile")
                {
                    idx = i;
                    break;
                }
            }

            if (idx < 0 || idx + 1 >= list.Count)
            {
                Log.Warning("[Hyperdrive] ReadAllBytes pattern not found in LoadTextureViaImageConversion (skipped, no effect)");
                return list;
            }

            // Cache-hit branch target = the instruction right after ReadAllBytes (it receives the byte[]).
            var cachedOk = il.DefineLabel();
            list[idx + 1].labels.Add(cachedOk);

            // The file (VirtualFile) is already on the stack right before ReadAllBytes.
            //   callvirt get_FullPath  ; file -> string
            //   call     GetCachedBytes; string -> byte[]|null
            //   dup
            //   brtrue   cachedOk      ; non-null -> skip the disk read
            //   pop                    ; discard the null
            //   ldarg.0                ; reload file for the fallback ReadAllBytes
            var inject = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Callvirt, getFullPath),
                new CodeInstruction(OpCodes.Call, getCached),
                new CodeInstruction(OpCodes.Dup),
                new CodeInstruction(OpCodes.Brtrue, cachedOk),
                new CodeInstruction(OpCodes.Pop),
                new CodeInstruction(OpCodes.Ldarg_0),
            };

            list.InsertRange(idx, inject);
            return list;
        }
    }
}
