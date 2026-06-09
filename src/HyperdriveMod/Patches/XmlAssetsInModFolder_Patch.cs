using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldHyperdrive.Patches
{
    // Patch 1: thread count in the per-mod XML loader. Hardcoded 2 -> Max(3, CPU-1).
    // Stack-neutral: replaces a single `ldc.i4.2` (pushes one int) with an
    // expression that also pushes exactly one int, right before `newarr Thread`.
    [HarmonyPatch(typeof(DirectXmlLoader), "XmlAssetsInModFolder")]
    public static class XmlAssetsInModFolder_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var getProcessorCount = AccessTools.PropertyGetter(typeof(Environment), nameof(Environment.ProcessorCount));
            var mathMax = AccessTools.Method(typeof(Math), nameof(Math.Max), new[] { typeof(int), typeof(int) });

            for (int i = 0; i < list.Count - 1; i++)
            {
                if (list[i].opcode == OpCodes.Ldc_I4_2 &&
                    list[i + 1].opcode == OpCodes.Newarr &&
                    list[i + 1].operand is Type t && t == typeof(Thread))
                {
                    var replacement = new List<CodeInstruction>
                    {
                        new CodeInstruction(OpCodes.Call, getProcessorCount), // ProcessorCount
                        new CodeInstruction(OpCodes.Ldc_I4_1),                 // 1
                        new CodeInstruction(OpCodes.Sub),                      // ProcessorCount - 1
                        new CodeInstruction(OpCodes.Ldc_I4_3),                 // 3
                        new CodeInstruction(OpCodes.Call, mathMax),            // Max(3, ProcessorCount-1)
                    };
                    // carry over any labels/exception-block markers from the replaced instruction
                    replacement[0].labels.AddRange(list[i].labels);
                    replacement[0].blocks.AddRange(list[i].blocks);
                    list.RemoveAt(i);
                    list.InsertRange(i, replacement);
                    return list;
                }
            }

            Log.Warning("[Hyperdrive] thread-count pattern not found in XmlAssetsInModFolder (skipped, no effect)");
            return list;
        }
    }
}
