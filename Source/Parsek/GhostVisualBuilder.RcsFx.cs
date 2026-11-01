using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Parsek
{
    // RCS FX: effect-group model node scan, the pristine RCS FX fallback and RCS FX build.
    internal static partial class GhostVisualBuilder
    {
        /// <summary>
        /// Scans one RCS effect group for MODEL_MULTI_PARTICLE(_PERSIST) nodes and appends
        /// FX definitions plus the first emission/speed curves found. Shared by the post-MM
        /// scan and the pristine-config Waterfall fallback.
        /// </summary>
        internal static void ScanRcsEffectGroupModelNodes(
            ConfigNode effectGroup,
            List<FxModelDefinition> fxDefinitions,
            ref FloatCurve emissionCurve,
            ref FloatCurve speedCurve)
        {
            ConfigNode[] mmpNodes = effectGroup.GetNodes("MODEL_MULTI_PARTICLE_PERSIST");
            if (mmpNodes.Length == 0)
                mmpNodes = effectGroup.GetNodes("MODEL_MULTI_PARTICLE");

            for (int mp = 0; mp < mmpNodes.Length; mp++)
            {
                string transformName = mmpNodes[mp].GetValue("transformName");
                string modelName = mmpNodes[mp].GetValue("modelName");
                if (!string.IsNullOrEmpty(transformName))
                {
                    Vector3 localOffset = Vector3.zero;
                    Vector3 localScale = Vector3.one;
                    Quaternion localRotation = Quaternion.identity;
                    Vector3 parsedVector;
                    if (TryParseVector3(mmpNodes[mp].GetValue("localOffset"), out parsedVector))
                        localOffset = parsedVector;
                    if (TryParseVector3(mmpNodes[mp].GetValue("localScale"), out parsedVector))
                        localScale = parsedVector;
                    TryParseFxLocalRotation(mmpNodes[mp].GetValue("localRotation"), out localRotation);

                    fxDefinitions.Add(new FxModelDefinition
                    {
                        transformName = transformName,
                        modelName = modelName ?? "",
                        localOffset = localOffset,
                        localRotation = localRotation,
                        localScale = localScale
                    });

                    if (emissionCurve == null)
                    {
                        ConfigNode emNode = mmpNodes[mp].GetNode("emission");
                        if (emNode != null)
                        {
                            emissionCurve = new FloatCurve();
                            emissionCurve.Load(emNode);
                        }
                        ConfigNode spNode = mmpNodes[mp].GetNode("speed");
                        if (spNode != null)
                        {
                            speedCurve = new FloatCurve();
                            speedCurve.Load(spNode);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Waterfall fallback for RCS: rebuilds thruster FX definitions from the
        /// ReStock-authored EFFECTS when ReStock authored one for this part (the
        /// install-faithful look; see ReStockPatchFxIndex), else from the pristine
        /// on-disk part config (see PristinePartFxResolver). The running group is
        /// resolved by RCS-module ordinal in both stages.
        /// </summary>
        private static void TryApplyPristineRcsFxFallback(
            Part prefab, int moduleIndex, string partName,
            List<FxModelDefinition> fxDefinitions,
            ref FloatCurve emissionCurve, ref FloatCurve speedCurve)
        {
            string runtimeName = prefab.partInfo?.name ?? partName;
            var pristine = PristinePartFxResolver.GetForPart(runtimeName, prefab.partInfo?.configFileFullName);

            if (ReStockPatchFxIndex.TryGetForPart(runtimeName, out var restock) &&
                restock.EffectsNode != null)
            {
                string restockGroupName = ReStockPatchFxIndex.ResolveRcsRunningGroupName(
                    restock, pristine, moduleIndex);
                ConfigNode restockGroup = restock.EffectsNode.GetNode(restockGroupName);
                if (restockGroup != null)
                {
                    int beforeRestock = fxDefinitions.Count;
                    ScanRcsEffectGroupModelNodes(restockGroup, fxDefinitions,
                        ref emissionCurve, ref speedCurve);
                    if (fxDefinitions.Count > beforeRestock)
                    {
                        ParsekLog.VerboseRateLimited("GhostVisual", $"rcs-restock-fallback-{partName}-{moduleIndex}",
                            $"RCS restock recovery: '{partName}' midx={moduleIndex} added " +
                            $"{fxDefinitions.Count - beforeRestock} FX definitions " +
                            $"(group='{restockGroupName}') from '{restock.SourceFile}'",
                            5.0);
                        return;
                    }
                }

                ParsekLog.VerboseRateLimited("GhostVisual", $"rcs-restock-miss-{partName}-{moduleIndex}",
                    $"RCS restock recovery: '{partName}' midx={moduleIndex} group " +
                    $"'{restockGroupName}' {(restockGroup == null ? "not found" : "yielded nothing")}; " +
                    "falling through to pristine recovery",
                    5.0);
            }

            if (pristine == null || !pristine.Found || pristine.EffectsNode == null)
            {
                ParsekLog.VerboseRateLimited("GhostVisual", $"rcs-pristine-miss-{partName}-{moduleIndex}",
                    $"RCS waterfall fallback: '{partName}' midx={moduleIndex} pristine EFFECTS unavailable",
                    5.0);
                return;
            }

            string pristineRunning = moduleIndex < pristine.RcsRunningEffectNames.Count
                ? pristine.RcsRunningEffectNames[moduleIndex]
                : "running";
            ConfigNode pristineGroup = pristine.EffectsNode.GetNode(pristineRunning);
            if (pristineGroup == null)
            {
                ParsekLog.VerboseRateLimited("GhostVisual", $"rcs-pristine-group-miss-{partName}-{moduleIndex}",
                    $"RCS waterfall fallback: '{partName}' midx={moduleIndex} " +
                    $"pristine group '{pristineRunning}' not found",
                    5.0);
                return;
            }

            int before = fxDefinitions.Count;
            ScanRcsEffectGroupModelNodes(pristineGroup, fxDefinitions, ref emissionCurve, ref speedCurve);
            ParsekLog.VerboseRateLimited("GhostVisual", $"rcs-pristine-fallback-{partName}-{moduleIndex}",
                $"RCS waterfall fallback: '{partName}' midx={moduleIndex} pristine recovery added " +
                $"{fxDefinitions.Count - before} FX definitions (group='{pristineRunning}') " +
                $"from '{pristine.SourcePath}'",
                5.0);
        }

        private static List<RcsGhostInfo> TryBuildRcsFX(
            Part prefab, uint persistentId, string partName,
            Transform modelRoot, Transform ghostModelNode,
            Dictionary<Transform, Transform> cloneMap,
            bool raiseRcsVisualOnly,
            Dictionary<string, bool> selectedVariantGameObjects)
        {
            // Find all ModuleRCS on the part (catches both ModuleRCS and ModuleRCSFX)
            int midx = 0;
            var rcsModules = new List<(ModuleRCS rcs, int moduleIndex)>();
            for (int m = 0; m < prefab.Modules.Count; m++)
            {
                var rcs = prefab.Modules[m] as ModuleRCS;
                if (rcs != null)
                {
                    rcsModules.Add((rcs, midx));
                    midx++;
                }
            }
            if (rcsModules.Count == 0) return null;

            var result = new List<RcsGhostInfo>();

            for (int r = 0; r < rcsModules.Count; r++)
            {
                var (rcs, moduleIndex) = rcsModules[r];
                var info = new RcsGhostInfo
                {
                    partPersistentId = persistentId,
                    moduleIndex = moduleIndex,
                    emissionScale = raiseRcsVisualOnly ? RcsShowcaseEmissionScale : 1f,
                    speedScale = raiseRcsVisualOnly ? RcsShowcaseSpeedScale : 1f
                };

                ConfigNode partConfig = prefab.partInfo?.partConfig;
                if (partConfig == null)
                {
                    continue;
                }

                ConfigNode effectsNode = partConfig.GetNode("EFFECTS");

                // Determine the running effect name from the module.
                // ModuleRCSFX exposes runningEffectName; base ModuleRCS defaults to "running".
                string runningEffect = "running";
                var rcsFX = rcs as ModuleRCSFX;
                if (rcsFX != null && !string.IsNullOrEmpty(rcsFX.runningEffectName))
                    runningEffect = rcsFX.runningEffectName;

                // Only scan the effect group matching runningEffectName
                // (prevents picking up engine FX on mixed engine+RCS parts)
                ConfigNode effectGroup = effectsNode != null ? effectsNode.GetNode(runningEffect) : null;

                var fxDefinitions = new List<FxModelDefinition>();
                FloatCurve emissionCurve = null;
                FloatCurve speedCurve = null;

                if (effectGroup != null)
                {
                    ScanRcsEffectGroupModelNodes(effectGroup, fxDefinitions,
                        ref emissionCurve, ref speedCurve);
                }

                // Waterfall config packs strip the particle nodes from the RCS running
                // group (the group itself survives, audio-only). Recover the pristine
                // definitions from the on-disk cfg; gate closed on stock installs.
                if (fxDefinitions.Count == 0 && WaterfallCompat.PartHasWaterfallModule(prefab))
                {
                    TryApplyPristineRcsFxFallback(prefab, moduleIndex, partName, fxDefinitions,
                        ref emissionCurve, ref speedCurve);
                }

                if (fxDefinitions.Count == 0)
                {
                    continue;
                }

                info.emissionCurve = emissionCurve;
                info.speedCurve = speedCurve;

                for (int f = 0; f < fxDefinitions.Count; f++)
                {
                    string transformName = fxDefinitions[f].transformName;
                    string modelName = fxDefinitions[f].modelName;

                    // Note: RCS discovery uses FindTransformsRecursive (not rcs.thrusterTransforms
                    // directly). If a thrusterTransforms-based path is added in the future,
                    // it would also need variant filtering here.
                    var fxTransforms = FindTransformsRecursive(prefab.transform, transformName);
                    // #242c: exclude transforms under disabled variant GameObjects
                    if (selectedVariantGameObjects != null && selectedVariantGameObjects.Count > 0)
                    {
                        int preFilter = fxTransforms.Count;
                        fxTransforms.RemoveAll(t =>
                            !IsRendererEnabledByVariantRule(
                                t, modelRoot, selectedVariantGameObjects, out _, out _));
                        if (fxTransforms.Count < preFilter)
                            ParsekLog.VerboseRateLimited("GhostVisual",
                                $"rcs-variant-filter-{partName}-{moduleIndex}-{transformName}",
                                $"RCS '{partName}' midx={moduleIndex}: " +
                                $"variant filter removed {preFilter - fxTransforms.Count} of {preFilter} " +
                                $"'{transformName}' RCS FX transforms",
                                5.0);
                    }
                    if (fxTransforms.Count == 0)
                    {
                        continue;
                    }

                    for (int t = 0; t < fxTransforms.Count; t++)
                    {
                        Transform srcFxTransform = fxTransforms[t];

                        Transform ghostFxParent = ResolveGhostFxParent(
                            srcFxTransform, prefab.transform, modelRoot, ghostModelNode, cloneMap);
                        if (ghostFxParent == null)
                        {
                            ParsekLog.VerboseRateLimited("GhostVisual",
                                $"rcs-parent-miss-{partName}-{moduleIndex}-{transformName}",
                                $"RCS '{partName}' midx={moduleIndex}: " +
                                $"transform='{transformName}' parent resolution failed",
                                5.0);
                            continue;
                        }
                        LogFxParentAlignmentDiagnostic(
                            partName, moduleIndex, "RCS_MODEL_MULTI_PARTICLE", transformName,
                            prefab.transform, ghostModelNode, srcFxTransform, ghostFxParent);

                        if (!string.IsNullOrEmpty(modelName))
                        {
                            GameObject fxPrefab = GameDatabase.Instance.GetModelPrefab(modelName);
                            if (fxPrefab != null)
                            {
                                GameObject fxInstance = Object.Instantiate(fxPrefab);
                                fxInstance.SetActive(true);
                                fxInstance.transform.SetParent(ghostFxParent, false);
                                fxInstance.transform.localPosition = fxDefinitions[f].localOffset;
                                fxInstance.transform.localRotation = fxDefinitions[f].localRotation;
                                fxInstance.transform.localScale = fxDefinitions[f].localScale;

                                StripKspFxControllers(fxInstance, info.kspEmitters);

                                // Configure ALL particle systems in the FX hierarchy (not just the first).
                                // KSP RCS FX models have multiple child systems (plume + glow/smoke).
                                // Using singular GetComponentInChildren only stopped the first one —
                                // the rest kept playOnAwake=true and auto-played on ghost activation.
                                var allPs = fxInstance.GetComponentsInChildren<ParticleSystem>(true);
                                for (int p = 0; p < allPs.Length; p++)
                                {
                                    var ps = allPs[p];
                                    var main = ps.main;
                                    main.playOnAwake = false;
                                    main.prewarm = false;

                                    // Only disable Unity emission when KSPParticleEmitter is present
                                    // (same logic as engine FX — smoke FX need Unity emission).
                                    bool hasKspEmitter = fxInstance.GetComponent("KSPParticleEmitter") != null;
                                    var emission = ps.emission;
                                    if (hasKspEmitter)
                                        emission.enabled = false;
                                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                                    ps.Clear(true);

                                    // Disable renderer at build time (matching engine FX pattern).
                                    // SetRcsEmission re-enables renderers when RCS events fire.
                                    var psRenderer = ps.GetComponent<ParticleSystemRenderer>();
                                    if (psRenderer != null)
                                        psRenderer.enabled = raiseRcsVisualOnly;

                                    info.particleSystems.Add(ps);
                                }
                                if (allPs.Length > 0)
                                {
                                    LogFxInstancePlacementDiagnostic(
                                        partName,
                                        moduleIndex,
                                        "RCS_MODEL_MULTI_PARTICLE",
                                        transformName,
                                        modelName,
                                        prefab.transform,
                                        ghostModelNode,
                                        srcFxTransform,
                                        ghostFxParent,
                                        fxInstance.transform,
                                        fxDefinitions[f].localOffset,
                                        fxDefinitions[f].localRotation,
                                        true);
                                }
                            }
                        }
                    }
                }

                if (info.particleSystems.Count > 0)
                {
                    result.Add(info);
                }
            }

            // S1: one magnitude-baseline capture per finished RCS module — same placement rule as
            // the engine side (after every clone / override branch, so the baseline is the look the
            // ghost actually spawned with).
            for (int i = 0; i < result.Count; i++)
            {
                CaptureFxMagnitudeBaselines(
                    result[i].kspEmitters, result[i].particleSystems, result[i].particleBaselines,
                    partName, result[i].moduleIndex);
            }

            return result.Count > 0 ? result : null;
        }
    }
}
