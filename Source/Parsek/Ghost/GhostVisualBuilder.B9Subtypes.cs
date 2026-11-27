using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    internal static partial class GhostVisualBuilder
    {
        // B9PartSwitch subtypes on a part's ghost (rules: GhostB9Subtypes). The prefab the ghost
        // clones carries no subtype, so the shown subtype of every B9 module is applied here: its
        // transform enable lists hide renderers (through the same name-or-ancestor filter the stock
        // hidden transforms use) and its TRANSFORM offsets move, turn and scale the cloned
        // transforms.

        /// <summary>The names of every transform below <paramref name="modelRoot"/> (the root
        /// itself excluded, as B9's own <c>TraverseHierarchy</c> does).</summary>
        private static List<string> DescendantTransformNames(Transform modelRoot)
        {
            var names = new List<string>();
            if (modelRoot == null) return names;
            Transform[] all = modelRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i] != modelRoot) names.Add(all[i].name);
            return names;
        }

        /// <summary>The shown B9 subtypes of the part, and the transform names they hide (null when
        /// they hide none). One Verbose line per part name.</summary>
        internal static List<B9ModuleSelection> ResolveB9Subtypes(
            ConfigNode partConfig, ConfigNode partNode, Transform modelRoot,
            string partName, uint persistentId, out HashSet<string> hiddenNames)
        {
            hiddenNames = null;
            List<B9ModuleSelection> selections = GhostB9Subtypes.Resolve(partConfig, partNode);
            if (selections.Count == 0) return selections;
            HashSet<string> hidden = GhostB9Subtypes.HiddenTransformNames(
                selections, DescendantTransformNames(modelRoot));
            // The ghost's hide filter also matches a renderer's ancestors above the model root,
            // which B9 never touches: a B9 name equal to the model root's or an ancestor's would
            // hide the whole part, so those names are left out.
            for (Transform t = modelRoot; t != null; t = t.parent)
                hidden.Remove(t.name);
            if (hidden.Count > 0) hiddenNames = hidden;
            ParsekLog.VerboseRateLimited("GhostVisual", $"b9-subtypes-{partName}",
                $"  Part '{partName}' pid={persistentId}: B9 subtypes {GhostB9Subtypes.Describe(selections)} " +
                $"hide {hidden.Count} transform name(s)" +
                (hidden.Count > 0 ? $" [{string.Join(", ", new List<string>(hidden).ToArray())}]" : ""), 60.0);
            return selections;
        }

        /// <summary>Applies the shown subtypes' TRANSFORM offsets to the cloned transforms: every
        /// prefab transform below the model root whose name a modifier matches and that the ghost
        /// cloned. Returns how many offsets were applied; one Verbose line per part name.</summary>
        internal static int ApplyB9TransformModifiers(
            List<B9ModuleSelection> selections, Transform modelRoot,
            Dictionary<Transform, Transform> cloneMap, string partName, uint persistentId)
        {
            if (selections == null || selections.Count == 0 || modelRoot == null || cloneMap == null)
                return 0;
            int applied = 0;
            int matchedNotCloned = 0;
            Transform[] all = modelRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform src = all[i];
                if (src == null || src == modelRoot) continue;
                List<B9TransformModifier> mods = GhostB9Subtypes.ModifiersFor(selections, src.name);
                if (mods.Count == 0) continue;
                if (!cloneMap.TryGetValue(src, out Transform clone) || clone == null)
                {
                    matchedNotCloned++;
                    continue;
                }
                for (int m = 0; m < mods.Count; m++)
                {
                    B9TransformModifier mod = mods[m];
                    if (mod.PositionOffset.HasValue)
                        clone.localPosition = GhostB9Subtypes.ApplyPosition(clone.localPosition, mod.PositionOffset);
                    if (mod.RotationOffsetEuler.HasValue)
                        clone.localRotation = clone.localRotation * Quaternion.Euler(mod.RotationOffsetEuler.Value);
                    if (mod.ScaleOffset.HasValue)
                        clone.localScale = GhostB9Subtypes.ApplyScale(clone.localScale, mod.ScaleOffset);
                    applied++;
                }
            }
            ParsekLog.VerboseRateLimited("GhostVisual", $"b9-transforms-{partName}",
                $"  Part '{partName}' pid={persistentId}: B9 TRANSFORM offsets applied={applied} " +
                $"matchedNotCloned={matchedNotCloned} ({GhostB9Subtypes.Describe(selections)})", 60.0);
            return applied;
        }

        /// <summary>The read-only skin record of the part's B9 modules.</summary>
        internal static List<GhostAppliedB9Subtype> ToAppliedB9Subtypes(List<B9ModuleSelection> selections)
        {
            if (selections == null || selections.Count == 0) return null;
            var list = new List<GhostAppliedB9Subtype>(selections.Count);
            foreach (B9ModuleSelection s in selections)
                list.Add(new GhostAppliedB9Subtype
                {
                    moduleId = s.ModuleId,
                    subtype = s.Subtype,
                    resolutionPath = s.ResolutionPath,
                    modelled = s.Modelled,
                });
            return list;
        }
    }
}
