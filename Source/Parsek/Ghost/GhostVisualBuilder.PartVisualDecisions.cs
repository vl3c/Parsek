using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    internal static partial class GhostVisualBuilder
    {
        // The decision units of AddPartVisuals: which renderers a part's ghost keeps, which
        // optional visuals it builds, and the per-part diagnostic lines. Each one is a block of
        // AddPartVisuals lifted out verbatim; the Unity object creation stays in AddPartVisuals.

        /// <summary>
        /// A part whose model holds no MeshRenderer and no SkinnedMeshRenderer (a null array
        /// counts as empty) builds no ghost visual at all.
        /// </summary>
        internal static bool HasNoRendererComponents(
            MeshRenderer[] meshRenderers, SkinnedMeshRenderer[] skinnedRenderers)
        {
            return (meshRenderers == null || meshRenderers.Length == 0) &&
                (skinnedRenderers == null || skinnedRenderers.Length == 0);
        }

        /// <summary>
        /// Whether renderers on inactive GameObjects are skipped: only for a part with
        /// ModulePartVariants whose prefab shows at least one active renderer. A variant part
        /// with no active renderer at all keeps every renderer (the variant fallback).
        /// </summary>
        internal static bool ShouldFilterInactiveVariantRenderers(
            bool hasPartVariants, int activeMR, int activeSMR)
        {
            return hasPartVariants && (activeMR > 0 || activeSMR > 0);
        }

        /// <summary>
        /// Every transform name whose renderers a stock module deactivates on its flight start,
        /// so the ghost leaves them out: the FlagDecalBackground meshes other than the selected
        /// one. One Verbose line per part when the set is non-empty.
        /// </summary>
        internal static HashSet<string> ResolveStockHiddenTransformNames(
            Part prefab, ConfigNode partConfig, ConfigNode partNode, string selectedVariantName,
            string partName, uint persistentId)
        {
            var names = new HashSet<string>(System.StringComparer.Ordinal);

            ConfigNode flagConfig = FindModuleNode(partConfig, "FlagDecalBackground");
            if (flagConfig != null)
            {
                HashSet<string> hiddenFlagMeshes = ResolveFlagDecalHiddenMeshNames(
                    flagConfig, FindModuleNode(partNode, "FlagDecalBackground"), selectedVariantName);
                names.UnionWith(hiddenFlagMeshes);
                ParsekLog.VerboseRateLimited("GhostVisual", $"flag-meshes-{partName}",
                    $"  Part '{partName}' pid={persistentId}: FlagDecalBackground hides " +
                    $"{hiddenFlagMeshes.Count} flag mesh(es) (variant='{selectedVariantName ?? "<none>"}')", 60.0);
            }

            return names;
        }

        /// <summary>
        /// The FlagDecalBackground meshes stock hides in flight, transcribed from
        /// <c>FlagDecalBackground.OnLoad</c> / <c>EnableCurrentFlagMesh</c> (KSP 1.12.5,
        /// decompiled). OnLoad keeps a MESH node only when it has a name, displayName and
        /// meshName, and gives it the index of the first earlier MESH with the same
        /// <c>name</c>, else one past the highest index so far (so the index counts distinct
        /// names in order of first appearance); orientation defaults to LANDSCAPE. A mesh is
        /// shown only when its index equals the persisted <c>flagSize</c> (default 0), its
        /// comma-separated <c>variantNames</c> contain the selected variant, and its orientation
        /// is PORTRAIT when <c>displayingPortrait</c> (default true; false when the module has
        /// no PORTRAIT mesh) else LANDSCAPE; every other mesh is SetActive(false), in order, so
        /// a repeated meshName ends in its last state. A null <paramref name="selectedVariantName"/>
        /// leaves the variant test out (stock would hide every mesh; that shape does not occur
        /// on a flag part, all of which carry variants). Returns the meshNames left hidden.
        /// </summary>
        internal static HashSet<string> ResolveFlagDecalHiddenMeshNames(
            ConfigNode flagModuleConfig, ConfigNode snapshotFlagModule, string selectedVariantName)
        {
            var hidden = new HashSet<string>(System.StringComparer.Ordinal);
            if (flagModuleConfig == null)
                return hidden;
            ConfigNode[] meshNodes = flagModuleConfig.GetNodes("MESH");
            if (meshNodes == null || meshNodes.Length == 0)
                return hidden;

            var names = new List<string>();
            var meshNames = new List<string>();
            var indices = new List<int>();
            var portrait = new List<bool>();
            var variants = new List<string>();
            int portraitCount = 0;
            for (int i = 0; i < meshNodes.Length; i++)
            {
                ConfigNode mesh = meshNodes[i];
                if (mesh == null) continue;
                string name = mesh.GetValue("name");
                string displayName = mesh.GetValue("displayName");
                string meshName = mesh.GetValue("meshName");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(displayName)
                    || string.IsNullOrEmpty(meshName))
                    continue;

                int index = -1;
                for (int j = 0; j < names.Count; j++)
                {
                    if (string.Equals(names[j], name))
                    {
                        index = indices[j];
                        break;
                    }
                }
                if (index == -1)
                {
                    for (int j = 0; j < indices.Count; j++)
                    {
                        if (indices[j] > index)
                            index = indices[j];
                    }
                    index++;
                }

                bool isPortrait = string.Equals(
                    (mesh.GetValue("orientation") ?? "").Trim(), "PORTRAIT",
                    System.StringComparison.OrdinalIgnoreCase);
                if (isPortrait)
                    portraitCount++;

                names.Add(name);
                meshNames.Add(meshName);
                indices.Add(index);
                portrait.Add(isPortrait);
                variants.Add(mesh.GetValue("variantNames") ?? "");
            }

            int flagSize = 0;
            string flagSizeRaw = snapshotFlagModule?.GetValue("flagSize");
            if (!string.IsNullOrEmpty(flagSizeRaw))
                int.TryParse(flagSizeRaw.Trim(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out flagSize);
            bool displayingPortrait = ParseBoolOr(
                snapshotFlagModule?.GetValue("displayingPortrait"), portraitCount > 0);

            var finalState = new Dictionary<string, bool>(System.StringComparer.Ordinal);
            for (int i = 0; i < meshNames.Count; i++)
            {
                bool show = false;
                if (indices[i] == flagSize
                    && (selectedVariantName == null
                        || System.Array.IndexOf(variants[i].Split(','), selectedVariantName) > -1))
                {
                    show = displayingPortrait ? portrait[i] : !portrait[i];
                }
                finalState[meshNames[i]] = show;
            }

            foreach (var kv in finalState)
            {
                if (!kv.Value)
                    hidden.Add(kv.Key);
            }
            return hidden;
        }

        /// <summary>
        /// The three variant-resolution lines of one part: the selected variant and its
        /// GAMEOBJECT rule count, the include-everything fallback, and the base-implicit variant.
        /// </summary>
        internal static void LogPartVariantResolution(
            bool hasPartVariants, bool hasVariantGameObjectRules, string partName,
            string selectedVariantName, string variantResolutionPath,
            Dictionary<string, bool> selectedVariantGameObjects,
            bool filterInactiveVariantRenderers, int activeMR, int activeSMR)
        {
            if (hasPartVariants && hasVariantGameObjectRules)
            {
                ParsekLog.VerboseRateLimited("GhostVisual", $"variant_sel_{partName}",
                    $"  Variant selection: '{selectedVariantName}' via {variantResolutionPath} with " +
                    $"{selectedVariantGameObjects.Count} GAMEOBJECT rules", 60.0);
            }
            if (hasPartVariants && !filterInactiveVariantRenderers && !hasVariantGameObjectRules)
            {
                ParsekLog.VerboseRateLimited("GhostVisual", $"variant_fb_{partName}",
                    $"  Variant fallback: no active variant renderers and no GAMEOBJECT rules " +
                    $"— including all renderers (active MR={activeMR}, active SMR={activeSMR})", 60.0);
            }
            if (hasPartVariants && variantResolutionPath == "base-implicit")
            {
                ParsekLog.VerboseRateLimited("GhostVisual", $"variant_base_{partName}",
                    $"  Variant '{selectedVariantName}' not in VARIANT nodes " +
                    $"— using base variant (prefab default materials)", 60.0);
            }
        }

        /// <summary>
        /// The busted-wheel names whose renderers are stripped from the ghost: only when the part
        /// has busted names that cannot be swapped (a busted name on the model root or one of its
        /// ancestors). A swappable busted mesh is cloned and hidden instead, so this is null.
        /// </summary>
        internal static HashSet<string> ResolveDamagedRendererFilterNames(
            HashSet<string> damagedWheelNames, bool wheelDamageSwappable)
        {
            return damagedWheelNames.Count > 0 && !wheelDamageSwappable
                ? damagedWheelNames
                : null;
        }

        /// <summary>
        /// The depth-mask and busted-wheel transform names a part's config names, one line each
        /// when the set is non-empty.
        /// </summary>
        internal static void LogPartRendererExclusionNames(
            HashSet<string> depthMaskNames, string partName, uint persistentId,
            HashSet<string> damagedWheelNames)
        {
            if (depthMaskNames.Count > 0)
            {
                ParsekLog.VerboseRateLimited("GhostVisual", $"depth_mask_{partName}",
                    $"  Part '{partName}' pid={persistentId}: found {depthMaskNames.Count} " +
                    $"depth-mask transform(s): [{string.Join(", ", depthMaskNames)}]; ghost skips them", 60.0);
            }
            if (damagedWheelNames.Count > 0)
            {
                ParsekLog.VerboseRateLimited("GhostVisual", $"wheel_damage_{partName}",
                    $"  Part '{partName}' pid={persistentId}: found {damagedWheelNames.Count} " +
                    $"ModuleWheelDamage damagedTransformName(s): [{string.Join(", ", damagedWheelNames)}]", 60.0);
            }
        }

        /// <summary>
        /// The MeshRenderer skip counts of one part's clone pass, one line each when non-zero.
        /// </summary>
        internal static void LogVariantRendererSkips(
            int variantSkipped, string partName, int variantRuleSkipped)
        {
            if (variantSkipped > 0)
                ParsekLog.VerboseRateLimited("GhostVisual", $"variant_skip_{partName}",
                    $"  Skipped {variantSkipped} MeshRenderers on inactive variant objects", 60.0);
            if (variantRuleSkipped > 0)
                ParsekLog.VerboseRateLimited("GhostVisual", $"variant_ruleskip_{partName}",
                    $"  Skipped {variantRuleSkipped} MeshRenderers by selected variant GAMEOBJECT rules", 60.0);
        }

        /// <summary>
        /// The clone summary of one part: MeshRenderers cloned (every counted mesh that is not a
        /// skinned one), SkinnedMeshRenderers cloned, and the three skip counts.
        /// </summary>
        internal static void LogPartCloneSummary(
            int meshCount, int skinnedCloned, string partName, uint persistentId,
            int nullMeshSkipped, int damagedSkipped, int depthMaskSkipped)
        {
            // Summary log for renderer cloning results: helps diagnose missing ghost parts
            int meshCloned = meshCount - skinnedCloned;
            ParsekLog.VerboseRateLimited("GhostVisual", $"clone_summary_{partName}",
                $"  Part '{partName}' pid={persistentId}: cloned {meshCloned} MeshRenderers, " +
                $"{skinnedCloned} SkinnedMeshRenderers, skipped {nullMeshSkipped} null-mesh SMRs, " +
                $"skipped {damagedSkipped} damaged-wheel renderers, " +
                $"skipped {depthMaskSkipped} depth-mask renderers", 60.0);
        }

        /// <summary>
        /// A snapshot that already shows the shroud jettisoned hides the jettison transforms at
        /// build time (#290): continuation recordings carry no seed event to hide them later.
        /// </summary>
        internal static bool ShouldHideJettisonTransformsAtBuild(
            JettisonGhostInfo jettisonInfo, ConfigNode partNode)
        {
            return jettisonInfo != null && partNode != null
                && IsJettisonedInSnapshot(partNode);
        }

        /// <summary>
        /// Why one ModuleJettison's shroud starts hidden on the ghost, or None when it starts
        /// shown. Mirrors what stock shows once the part has started in flight.
        /// </summary>
        internal enum JettisonSpawnHideReason
        {
            None,
            BottomNodeEmpty,
            Jettisoned,
            HideOverride,
        }

        /// <summary>
        /// Pure transcription of stock ModuleJettison's flight start (KSP 1.12.5, decompiled
        /// OnStart + SetupJettisonState, run from the first LateUpdate): OnStart folds
        /// shroudHideOverride into isJettisoned, then with ignoreNodes off a bottom node with no
        /// attached part hides the jettison transform WITHOUT setting isJettisoned, and
        /// otherwise a jettisoned shroud is hidden and an intact one shown. Stock reads only the
        /// node's attachedPart, so a surface-attached child under the engine does not count.
        /// The snapshot stands in for the live part: the bottom node is empty when its
        /// "attN = node, index" row carries a negative index. No row (node absent, or a
        /// snapshot without attN rows) is no evidence and leaves the shroud shown, as does a
        /// missing MODULE node.
        /// </summary>
        internal static JettisonSpawnHideReason ResolveJettisonSpawnHideReason(
            bool ignoreNodes, string bottomNodeName, ConfigNode partNode, ConfigNode snapshotModule)
        {
            if (!ignoreNodes && IsAttachNodeEmptyInSnapshot(partNode, bottomNodeName))
                return JettisonSpawnHideReason.BottomNodeEmpty;
            if (ReadSnapshotBool(snapshotModule, "isJettisoned"))
                return JettisonSpawnHideReason.Jettisoned;
            if (ReadSnapshotBool(snapshotModule, "shroudHideOverride"))
                return JettisonSpawnHideReason.HideOverride;
            return JettisonSpawnHideReason.None;
        }

        /// <summary>
        /// Pure: true only when the snapshot has an "attN = nodeId, index" row for the node and
        /// its index is negative (stock writes -1 for an empty node).
        /// </summary>
        internal static bool IsAttachNodeEmptyInSnapshot(ConfigNode partNode, string nodeId)
        {
            if (partNode == null || string.IsNullOrEmpty(nodeId)) return false;
            string[] rows = partNode.GetValues("attN");
            if (rows == null) return false;
            for (int i = 0; i < rows.Length; i++)
            {
                if (string.IsNullOrEmpty(rows[i])) continue;
                string[] fields = rows[i].Split(',');
                if (fields.Length < 2) continue;
                if (!string.Equals(fields[0].Trim(), nodeId, System.StringComparison.Ordinal)) continue;
                int idx;
                if (!int.TryParse(fields[1].Trim(), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out idx))
                    return false;
                return idx < 0;
            }
            return false;
        }

        private static bool ReadSnapshotBool(ConfigNode module, string key)
        {
            if (module == null) return false;
            string val = module.GetValue(key);
            return val != null && val.Trim().Equals("True", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether any of the three heat-driven animation modules (ModuleAnimateHeat,
        /// FXModuleAnimateThrottle, FXModuleAnimateRCS) was found.
        /// </summary>
        internal static bool HasAnyHeatAnimation(
            bool hasAnimateHeat, bool hasAnimateThrottle, bool hasAnimateRcs)
        {
            return hasAnimateHeat || hasAnimateThrottle || hasAnimateRcs;
        }

        /// <summary>
        /// The heat animation the ghost drives: ModuleAnimateHeat first, then
        /// FXModuleAnimateThrottle, then FXModuleAnimateRCS.
        /// </summary>
        internal static string SelectHeatAnimationName(
            bool hasAnimateHeat, string animateHeatAnimName,
            bool hasAnimateThrottle, string animateThrottleAnimName,
            string animateRcsAnimName)
        {
            return hasAnimateHeat ? animateHeatAnimName
                : hasAnimateThrottle ? animateThrottleAnimName
                : animateRcsAnimName;
        }

        /// <summary>
        /// The module name the heat info is built from, in the same precedence as
        /// <see cref="SelectHeatAnimationName"/>.
        /// </summary>
        internal static string SelectHeatAnimationSourceModule(
            bool hasAnimateHeat, bool hasAnimateThrottle)
        {
            return hasAnimateHeat ? "ModuleAnimateHeat"
                : hasAnimateThrottle ? "FXModuleAnimateThrottle"
                : "FXModuleAnimateRCS";
        }

        /// <summary>
        /// Whether one of the animation-driven modules found on the part (ladder, animation
        /// group, standalone ModuleAnimateGeneric, heat animation, aero / control surface,
        /// robot arm scanner, robotics) needs the full model hierarchy mirrored into the ghost.
        /// The three module-presence checks AddPartVisuals makes first (ModuleDeployablePart,
        /// ModuleWheelDeployment, ModuleCargoBay) stay there: they read the live prefab.
        /// </summary>
        internal static bool AnimatedModulesNeedFullModelHierarchy(
            bool hasRetractableLadder,
            bool hasAnimationGroupDeploy,
            bool hasStandaloneAnimateGenericDeploy,
            bool hasAnyHeatAnim,
            bool hasAeroSurfaceDeploy,
            bool hasControlSurfaceDeploy,
            bool hasRobotArmScannerDeploy,
            bool hasRoboticModules)
        {
            return hasRetractableLadder ||
                hasAnimationGroupDeploy ||
                hasStandaloneAnimateGenericDeploy ||
                hasAnyHeatAnim ||
                hasAeroSurfaceDeploy ||
                hasControlSurfaceDeploy ||
                hasRobotArmScannerDeploy ||
                hasRoboticModules;
        }

        /// <summary>
        /// A part whose config names a busted or an intact wheel transform gets the wheel damage
        /// swap resolved (which may still decide not to build one).
        /// </summary>
        internal static bool ShouldResolveWheelDamageInfo(
            HashSet<string> damagedWheelNames, HashSet<string> undamagedWheelNames)
        {
            return damagedWheelNames.Count > 0 || undamagedWheelNames.Count > 0;
        }
    }
}
