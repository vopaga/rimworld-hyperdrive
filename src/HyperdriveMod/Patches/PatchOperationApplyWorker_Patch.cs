using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Xml;
using HarmonyLib;
using Verse;
using Verse.StartupOptimizer;

namespace RimWorldHyperdrive.Patches
{
    // Patch 6 (Part B): in every vanilla PatchOperation subclass's ApplyWorker, redirect
    // XmlDocument.SelectNodes / SelectSingleNode to the FastXPath index lookups.
    // Scoped to Assembly-CSharp only (same as the build-time patcher) so we don't rewrite
    // mod-added operations that might call Select* on non-document nodes.
    [HarmonyPatch]
    public static class PatchOperationApplyWorker_Patch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var baseType = typeof(PatchOperation);
            var gameAsm = baseType.Assembly; // Assembly-CSharp
            foreach (var type in AccessTools.GetTypesFromAssembly(gameAsm))
            {
                if (type == baseType || !baseType.IsAssignableFrom(type)) continue;
                var m = AccessTools.Method(type, "ApplyWorker", new[] { typeof(XmlDocument) });
                if (m != null && m.DeclaringType == type)
                    yield return m;
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var fastSelectNodes = AccessTools.Method(typeof(FastXPath), nameof(FastXPath.FastSelectNodes));
            var fastSelectSingle = AccessTools.Method(typeof(FastXPath), nameof(FastXPath.FastSelectSingleNode));

            foreach (var ci in instructions)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) &&
                    ci.operand is MethodInfo mi &&
                    mi.DeclaringType != null && typeof(XmlNode).IsAssignableFrom(mi.DeclaringType) &&
                    mi.GetParameters().Length == 1 &&
                    mi.GetParameters()[0].ParameterType == typeof(string))
                {
                    // Stack at the call site is [XmlDocument, xpathString] — exactly what the
                    // static FastSelect* methods take. Just swap callvirt -> call (labels/blocks kept).
                    if (mi.Name == "SelectNodes")
                    {
                        ci.opcode = OpCodes.Call;
                        ci.operand = fastSelectNodes;
                    }
                    else if (mi.Name == "SelectSingleNode")
                    {
                        ci.opcode = OpCodes.Call;
                        ci.operand = fastSelectSingle;
                    }
                }
                yield return ci;
            }
        }
    }
}
