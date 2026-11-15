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
