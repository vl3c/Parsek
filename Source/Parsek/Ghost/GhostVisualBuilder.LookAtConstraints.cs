using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    internal static partial class GhostVisualBuilder
    {
        /// <summary>
        /// The CONSTRAINLOOKFX (targetName, rotatorsName) pairs of every FXModuleLookAtConstraint
        /// in a part config, in declaration order. Stock <c>OnLoad</c> (KSP 1.12.5, decompiled)
        /// reads only those two keys; a pair missing either is dropped (stock's SetupObjects finds
        /// no target or no mover for it).
        /// </summary>
        internal static List<KeyValuePair<string, string>> ParseLookAtConstraintNames(ConfigNode partConfig)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            ConfigNode[] modules = partConfig?.GetNodes("MODULE");
            if (modules == null) return pairs;
            for (int m = 0; m < modules.Length; m++)
            {
                if (modules[m]?.GetValue("name") != "FXModuleLookAtConstraint") continue;
                ConfigNode[] constraints = modules[m].GetNodes("CONSTRAINLOOKFX");
                if (constraints == null) continue;
                for (int c = 0; c < constraints.Length; c++)
                {
                    string target = constraints[c]?.GetValue("targetName");
                    string rotators = constraints[c]?.GetValue("rotatorsName");
                    if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(rotators)) continue;
                    pairs.Add(new KeyValuePair<string, string>(target, rotators));
                }
            }
            return pairs;
        }

        /// <summary>
        /// Binds the part's FXModuleLookAtConstraint pairs to their ghost clones (a target that is
        /// not under a renderer gets its chain mirrored), aims them once, and hands the list to
        /// the part's gimbal and deployable infos so they re-aim after their own pose writes.
        /// Stock's FindModelTransform / FindModelTransforms: the first transform with the target
        /// name, every transform with the rotators name.
        /// </summary>
        private static List<LookAtConstraintGhostInfo> BuildLookAtConstraints(
            ConfigNode partConfig, Transform modelRoot, Transform ghostModelNode, Transform ghostPartRoot,
            Dictionary<Transform, Transform> cloneMap, SynthesizedMotionGhostInfos synthesizedMotionInfos,
            DeployableGhostInfo deployableInfo, uint persistentId, string partName)
        {
            List<KeyValuePair<string, string>> names = ParseLookAtConstraintNames(partConfig);
            if (names.Count == 0 || modelRoot == null || ghostModelNode == null) return null;

            Transform[] all = modelRoot.GetComponentsInChildren<Transform>(true);
            var result = new List<LookAtConstraintGhostInfo>();
            int movers = 0, unresolved = 0;
            for (int i = 0; i < names.Count; i++)
            {
                Transform prefabTarget = FindTransformRecursive(modelRoot, names[i].Key);
                if (prefabTarget == null)
                {
                    unresolved++;
                    continue;
                }
                var info = new LookAtConstraintGhostInfo
                {
                    target = MapToGhost(prefabTarget, modelRoot, ghostModelNode, cloneMap),
                    upReference = ghostPartRoot,
                };
                for (int t = 0; t < all.Length; t++)
                {
                    if (all[t].name != names[i].Value) continue;
                    Transform mover = MapToGhost(all[t], modelRoot, ghostModelNode, cloneMap);
                    if (mover != null) info.movers.Add(mover);
                }
                if (info.target == null || info.movers.Count == 0)
                {
                    unresolved++;
                    continue;
                }
                movers += info.movers.Count;
                result.Add(info);
            }

            if (result.Count > 0)
            {
                GhostPlaybackLogic.ApplyLookAtConstraints(result);
                if (synthesizedMotionInfos?.gimbals != null)
                {
                    for (int g = 0; g < synthesizedMotionInfos.gimbals.Count; g++)
                    {
                        GimbalGhostInfo gimbal = synthesizedMotionInfos.gimbals[g];
                        if (gimbal == null || gimbal.partPersistentId != persistentId) continue;
                        gimbal.lookAtConstraints = result;
                        gimbal.lookAtDeflection = gimbal.currentDeflection;
                    }
                }
                if (deployableInfo != null)
                    deployableInfo.lookAtConstraints = result;
            }

            ParsekLog.VerboseRateLimited("GhostVisual", $"lookat-{partName}",
                $"LookAt constraints '{partName}' pid={persistentId}: pairs={names.Count} " +
                $"bound={result.Count} movers={movers} unresolved={unresolved} " +
                $"gimbal={(synthesizedMotionInfos?.gimbals != null && synthesizedMotionInfos.gimbals.Count > 0)} " +
                $"deployable={deployableInfo != null}", 60.0);
            return result.Count > 0 ? result : null;
        }

        private static Transform MapToGhost(Transform prefabTransform, Transform modelRoot,
            Transform ghostModelNode, Dictionary<Transform, Transform> cloneMap)
        {
            if (prefabTransform == modelRoot) return ghostModelNode;
            if (cloneMap.TryGetValue(prefabTransform, out Transform ghost)) return ghost;
            return MirrorTransformChain(prefabTransform, modelRoot, ghostModelNode, cloneMap);
        }
    }
}
