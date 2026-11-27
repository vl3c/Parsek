using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Parsek
{
    // Per-part ghost info builders used by AddPartVisuals: parachute, heat, deployable, light, skinned mesh, surface deployable and jettison info, and the engine hierarchy dump.
    internal static partial class GhostVisualBuilder
    {
        /// <summary>
        /// Resolve a source bone transform to its ghost counterpart.
        /// Lookup order: cloneMap direct hit, then MirrorTransformChain from modelRoot,
        /// then MirrorTransformChain from prefab.transform (EVA rig fallback).
        /// Sets <paramref name="usedPartRootFallback"/> to true when the prefab fallback path is taken.
        /// </summary>
        private static Transform ResolveGhostBone(
            Transform srcBone, Transform modelRoot, Transform modelNodeTransform,
            Transform partRootTransform, Part prefab,
            Dictionary<Transform, Transform> cloneMap, ref bool usedPartRootFallback)
        {
            Transform ghostBone;
            if (!cloneMap.TryGetValue(srcBone, out ghostBone))
            {
                if (IsDescendantOf(srcBone, modelRoot))
                {
                    ghostBone = MirrorTransformChain(srcBone, modelRoot, modelNodeTransform, cloneMap);
                }
                else if (IsDescendantOf(srcBone, prefab.transform))
                {
                    ghostBone = MirrorTransformChain(srcBone, prefab.transform, partRootTransform, cloneMap);
                    usedPartRootFallback = true;
                }
            }
            return ghostBone;
        }

        /// <summary>
        /// Appends the full transform hierarchy of a part prefab into <paramref name="sb"/> for diagnostics: one
        /// <c>depth:name[components](INACTIVE)</c> entry per transform, separated by " | ", up to
        /// <see cref="HierarchyDumpMaxNodes"/> entries. <paramref name="nodeCount"/> comes back as the TOTAL number
        /// of transforms visited, capped or not, so the caller can report the overflow.
        ///
        /// <para>ONE STRING, NOT ONE LINE PER NODE. This walk used to emit a rate-limited log line per transform,
        /// which is a per-item emit inside an unbounded recursive loop - the batch-counting convention forbids it,
        /// and it measured ~344 lines of a single session's log (todo entry 160). The caller now emits the whole
        /// tree as ONE line, so the same information survives at a fraction of the volume.</para>
        /// </summary>
        private static void AppendTransformHierarchy(
            Transform t, int depth, System.Text.StringBuilder sb, ref int nodeCount)
        {
            if (nodeCount < HierarchyDumpMaxNodes)
            {
                var components = t.gameObject.GetComponents<Component>();
                var compNames = new List<string>();
                foreach (var c in components)
                {
                    if (c == null) continue;
                    string cName = c.GetType().Name;
                    if (cName == "Transform") continue; // skip ubiquitous Transform
                    compNames.Add(cName);
                }
                string compStr = compNames.Count > 0 ? "[" + string.Join(", ", compNames) + "]" : "";
                string activeStr = t.gameObject.activeSelf ? "" : "(INACTIVE)";
                if (nodeCount > 0) sb.Append(" | ");
                sb.Append(depth.ToString(CultureInfo.InvariantCulture)).Append(':')
                  .Append(t.name).Append(compStr).Append(activeStr);
            }
            nodeCount++;
            for (int i = 0; i < t.childCount; i++)
                AppendTransformHierarchy(t.GetChild(i), depth + 1, sb, ref nodeCount);
        }

        #region Extracted helpers for AddPartVisuals

        /// <summary>
        /// Detects ModuleParachute on the prefab, clones canopy/cap transforms into the ghost,
        /// and builds a ParachuteGhostInfo with sampled semi-deployed and deployed states.
        /// Returns null if the part has no parachute or the canopy transform could not be resolved.
        /// </summary>
        /// <summary>
        /// Maps each sampled canopy bone onto the ghost through cloneMap and captures its
        /// build-time (stowed) local pose. Bones the ghost did not clone are skipped and counted.
        /// Returns null when nothing resolved, so stock canopies keep the root-only pose.
        /// </summary>
        private static List<CanopyBonePose> ResolveCanopyBonePoses(
            List<CanopyBoneSample> samples, Transform srcCanopy,
            Dictionary<Transform, Transform> cloneMap, string partName, uint persistentId)
        {
            if (samples == null || samples.Count == 0 || srcCanopy == null)
                return null;

            List<CanopyBonePose> poses = null;
            int unresolved = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                CanopyBoneSample s = samples[i];
                Transform srcBone = ResolveChildIndexPath(srcCanopy, s.childIndexPath);
                Transform ghostBone = null;
                if (srcBone == null || !cloneMap.TryGetValue(srcBone, out ghostBone) || ghostBone == null)
                {
                    unresolved++;
                    continue;
                }
                if (poses == null)
                    poses = new List<CanopyBonePose>();
                poses.Add(new CanopyBonePose
                {
                    bone = ghostBone,
                    stowedPos = ghostBone.localPosition,
                    stowedRot = ghostBone.localRotation,
                    stowedScale = ghostBone.localScale,
                    semiDeployedSampled = s.semiDeployedSampled,
                    semiPos = s.semiPos,
                    semiRot = s.semiRot,
                    semiScale = s.semiScale,
                    deployedPos = s.deployedPos,
                    deployedRot = s.deployedRot,
                    deployedScale = s.deployedScale
                });
            }

            ParsekLog.Verbose("GhostVisual",
                $"  Part '{partName}' pid={persistentId}: canopy rig sampled={samples.Count} " +
                $"resolved={(poses != null ? poses.Count : 0)} unresolved={unresolved} animated canopy bones");
            return poses;
        }

        /// <summary>
        /// Re-expresses the sampled semi / deployed canopy pose (model frame, see
        /// <see cref="CanopySample.deployedInModelFrame"/>) in the ghost canopy's parent frame,
        /// through the ghost's own clone of the sampled model root. Left as sampled when the
        /// ghost has no clone of that root or the canopy has no parent.
        /// </summary>
        private static void MoveCanopySampleToParentFrame(
            Part prefab, Transform partRootTransform, Dictionary<Transform, Transform> cloneMap,
            Transform ghostCanopy, CanopySample sample, ParachuteGhostInfo info,
            string partName, uint persistentId)
        {
            Transform parent = ghostCanopy.parent;
            // The sampler's frame: the clone of prefab "model", else of the prefab root.
            Transform sampleRoot = prefab.transform.Find("model") ?? prefab.transform;
            cloneMap.TryGetValue(sampleRoot, out Transform ghostModel);
            if (parent == null || ghostModel == null)
            {
                ParsekLog.Verbose("GhostVisual",
                    $"  Part '{partName}' pid={persistentId}: canopy sample left in model frame " +
                    $"(parent={(parent != null ? "yes" : "none")} ghostModel={(ghostModel != null ? "yes" : "none")})");
                return;
            }
            Quaternion toParent = Quaternion.Inverse(parent.rotation) * ghostModel.rotation;
            if (sample.deployedInModelFrame)
            {
                info.deployedCanopyPos = parent.InverseTransformPoint(ghostModel.TransformPoint(sample.dPos));
                info.deployedCanopyRot = toParent * sample.dRot;
            }
            if (sample.semiDeployedSampled)
            {
                info.semiDeployedCanopyPos = parent.InverseTransformPoint(ghostModel.TransformPoint(sample.sdPos));
                info.semiDeployedCanopyRot = toParent * sample.sdRot;
            }
            ParsekLog.Verbose("GhostVisual",
                $"  Part '{partName}' pid={persistentId}: canopy sample moved to parent frame " +
                $"deployed={sample.deployedInModelFrame} semi={sample.semiDeployedSampled} " +
                $"offset={(info.deployedCanopyPos - sample.dPos).magnitude.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}m");
        }

        private static ParachuteGhostInfo TryBuildParachuteInfo(
            Part prefab, string partName, uint persistentId,
            Dictionary<Transform, Transform> cloneMap, Transform modelRoot,
            Transform partRootTransform, ref int meshCount, ref bool added)
        {
            ModuleParachute chute = prefab.FindModuleImplementing<ModuleParachute>();
            if (chute == null)
                return null;

            string canopyName = chute.canopyName;
            string capName = chute.capName;

            Transform srcCanopy = !string.IsNullOrEmpty(canopyName)
                ? prefab.FindModelTransform(canopyName) : null;
            Transform srcCap = !string.IsNullOrEmpty(capName)
                ? prefab.FindModelTransform(capName) : null;

            // If canopy exists on the prefab but wasn't cloned (e.g. EVA kerbals where
            // canopy lives under "model" but FindModelRoot returned "model01"), lazily
            // clone only the canopy and cap transforms so they enter cloneMap.
            // We intentionally skip all other meshes in the subtree (backpack, storage,
            // parachute housing, etc.) — only the canopy/cap are needed for playback.
            //
            // P8-S4 PROBED THE BACKPACK HERE AND DELIBERATELY LEFT IT OUT. The finding, so nobody
            // re-probes it: the asset is trivially reachable — `KerbalEVA.JetpackTransform` is a
            // public serialized Transform field, so no name guessing is needed, and stock's own
            // `UpdatePackModels` does nothing more than `JetpackTransform.gameObject.SetActive(flag)`.
            // The blocker is the GATE, not the asset. That `flag` is `HasJetpack`, driven by whether
            // an `evaJetpack` part is in the kerbal's ModuleInventoryPart — and Parsek records no
            // such signal. Every gate reachable from recorded data renders visibly wrong:
            // showing the pack on the first EvaJetpackDeployed event pops a backpack into existence
            // mid-spacewalk and leaves every ground EVA (who never deploys) with no pack at all,
            // while showing it unconditionally puts a jetpack on kerbals who carry none. Either is a
            // worse lie than the current absence. The honest fix is to read the snapshot's
            // ModuleInventoryPart contents as a new baseline surface, which is its own piece of work.
            Transform canopySubtreeRoot = null;
            if (srcCanopy != null && !cloneMap.ContainsKey(srcCanopy))
            {
                Transform canopyVisualRoot = FindImmediateChildOf(srcCanopy, prefab.transform);
                if (canopyVisualRoot != null && canopyVisualRoot != modelRoot)
                {

                    GameObject subNode = new GameObject(canopyVisualRoot.name);
                    subNode.transform.SetParent(partRootTransform, false);
                    subNode.transform.localPosition = canopyVisualRoot.localPosition;
                    subNode.transform.localRotation = canopyVisualRoot.localRotation;
                    subNode.transform.localScale = canopyVisualRoot.localScale;
                    cloneMap[canopyVisualRoot] = subNode.transform;
                    canopySubtreeRoot = subNode.transform;

                    // Clone only the canopy and cap meshes, not the entire subtree
                    Transform[] targets = srcCap != null
                        ? new[] { srcCanopy, srcCap }
                        : new[] { srcCanopy };
                    foreach (var target in targets)
                    {
                        var mr = target.GetComponent<MeshRenderer>();
                        var mf = target.GetComponent<MeshFilter>();
                        if (mr != null && mf != null && mf.sharedMesh != null)
                        {
                            Transform leaf = MirrorTransformChain(target, canopyVisualRoot,
                                subNode.transform, cloneMap);
                            leaf.gameObject.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                            leaf.gameObject.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
                            meshCount++;
                            added = true;
                        }
                        else
                        {
                            // No mesh directly on the transform — still mirror the chain
                            // so it enters cloneMap for lookup
                            MirrorTransformChain(target, canopyVisualRoot,
                                subNode.transform, cloneMap);
                        }
                    }
                }
            }

            // Look up ghost clones via cloneMap (deterministic, no name collisions).
            // If canopy was outside modelRoot, only canopy/cap were cloned above.
            Transform ghostCanopy = null, ghostCap = null;
            if (srcCanopy != null) cloneMap.TryGetValue(srcCanopy, out ghostCanopy);
            if (srcCap != null) cloneMap.TryGetValue(srcCap, out ghostCap);

            if (ghostCanopy != null)
            {
                CanopySample sample = SampleCanopyStates(prefab, chute);
                var parachuteInfo = new ParachuteGhostInfo
                {
                    partPersistentId = persistentId,
                    canopyTransform = ghostCanopy,
                    capTransform = ghostCap,
                    deployedCanopyScale = sample.dScale,
                    deployedCanopyPos = sample.dPos,
                    deployedCanopyRot = sample.dRot,
                    semiDeployedCanopyScale = sample.sdScale,
                    semiDeployedCanopyPos = sample.sdPos,
                    semiDeployedCanopyRot = sample.sdRot,
                    semiDeployedSampled = sample.semiDeployedSampled,
                    // Stock's airflow aim inputs. ModuleEvaChute poses its canopy kerbal-relative
                    // (and is the one module that sets the protected dontRotateParachute), so an
                    // EVA canopy keeps the fixed overhead pose below.
                    partTransform = partRootTransform,
                    aimCanopy = !(chute is ModuleEvaChute) && !partName.StartsWith("kerbalEVA"),
                    invertCanopy = chute.invertCanopy,
                    rotationSpeedDegPerSec = chute.rotationSpeedDPS,
                    spreadAngleDeg = chute.spreadAngle,
                    canopyAimStage = CanopyPoseStage.Stowed
                };

                // The sampler reads the clip's end pose in the sampled model's frame, but the
                // event appliers write it as the canopy's LOCAL pose: a canopy under an offset
                // parent (Mk2-R / Mk12-R / Mk25 'model/chute/canopy') landed off by that offset.
                if (canopySubtreeRoot == null)
                    MoveCanopySampleToParentFrame(prefab, partRootTransform, cloneMap, ghostCanopy,
                        sample, parachuteInfo, partName, persistentId);

                // Stowed = invisible (canopy mesh starts at zero scale in KSP prefabs)
                ghostCanopy.localScale = Vector3.zero;

                if (canopySubtreeRoot != null && partName.StartsWith("kerbalEVA"))
                {
                    // EVA deploy animation (fullyDeploySmall) only captures chute-module
                    // movement — the kerbal body pose change that swings the backpack
                    // overhead is a separate animation we can't sample. Override with a
                    // position above the kerbal's head and dome-down rotation.
                    ghostCanopy.SetParent(partRootTransform, false);
                    ghostCanopy.localPosition = Vector3.zero;
                    ghostCanopy.localRotation = Quaternion.identity;
                    ghostCanopy.localScale = Vector3.zero;
                    // EVA: both semi-deployed and deployed appear above the kerbal's head
                    parachuteInfo.semiDeployedCanopyPos = new Vector3(0f, 1f, 0f);
                    parachuteInfo.semiDeployedCanopyRot = Quaternion.Euler(270f, 0f, 0f);
                    parachuteInfo.deployedCanopyPos = new Vector3(0f, 1f, 0f);
                    parachuteInfo.deployedCanopyRot = Quaternion.Euler(270f, 0f, 0f);
                }
                else if (canopySubtreeRoot != null)
                {
                    // Non-EVA part with canopy outside modelRoot: reparent under
                    // subtree root so root-relative positions work.
                    ghostCanopy.SetParent(canopySubtreeRoot, false);
                    ghostCanopy.localPosition = Vector3.zero;
                    ghostCanopy.localRotation = Quaternion.identity;
                }

                // Capture the stowed pose LAST, after the EVA override and the outside-modelRoot
                // reparent have both had their say, so ParachuteRepacked can restore the actual
                // spawn pose on every part shape rather than an assumed one.
                parachuteInfo.stowedCanopyScale = ghostCanopy.localScale;
                parachuteInfo.stowedCanopyPos = ghostCanopy.localPosition;
                parachuteInfo.stowedCanopyRot = ghostCanopy.localRotation;

                // EVA canopies take the fixed overhead override above, so their bone rig is
                // left alone.
                if (!partName.StartsWith("kerbalEVA"))
                    parachuteInfo.canopyBones = ResolveCanopyBonePoses(
                        sample.bones, srcCanopy, cloneMap, partName, persistentId);

                return parachuteInfo;
            }
            else if (srcCanopy != null)
            {
            }

            return null;
        }

        /// <summary>
        /// Samples heat animation states and builds material states for ModuleAnimateHeat,
        /// FXModuleAnimateThrottle, or FXModuleAnimateRCS. Resolves sampled transforms onto
        /// the ghost model node and initializes materials to cold state.
        /// Returns null if no heat transforms or materials were resolved. A ModuleAnimateHeat whose
        /// clip no Animation of the model holds gets an info that draws nothing: stock's
        /// AnimStartup (decompiled ModuleAnimationSetter) then finds no animation state, so the
        /// recorded heat levels move nothing on the real part (stock's plain nose cones, whose
        /// cfg names 'Cone_Heat').
        /// </summary>
        private static HeatGhostInfo TryBuildHeatInfo(
            Part prefab, string heatAnimName, string heatSource,
            Transform modelNodeTransform, string partName, uint persistentId, Transform modelRoot,
            Dictionary<Transform, Transform> cloneMap)
        {
            if (IsHeatClipMissing(heatSource, ModelHoldsClip(modelRoot, heatAnimName)))
            {
                ParsekLog.VerboseRateLimited("GhostVisual", $"heat-clip-missing-{partName}",
                    $"Part '{partName}' pid={persistentId}: {heatSource} names clip '{heatAnimName}', " +
                    "which the model lacks; heat events draw nothing, as in stock", 60.0);
                return new HeatGhostInfo { partPersistentId = persistentId, heatClipMissing = true };
            }

            List<HeatTransformState> resolvedHeatTransforms = null;
            var sampledHeatStates = SampleAnimateHeatStates(prefab, heatAnimName);
            if (sampledHeatStates != null && sampledHeatStates.Count > 0)
            {
                resolvedHeatTransforms = new List<HeatTransformState>();
                int unresolved = 0;
                for (int s = 0; s < sampledHeatStates.Count; s++)
                {
                    var (path, coldPos, coldRot, coldScale, medPos, medRot, medScale, hotPos, hotRot, hotScale) = sampledHeatStates[s];
                    Transform ghostT = FindTransformByPath(modelNodeTransform, path);
                    if (ghostT != null)
                    {
                        resolvedHeatTransforms.Add(new HeatTransformState
                        {
                            t = ghostT,
                            coldPos = coldPos,
                            coldRot = coldRot,
                            coldScale = coldScale,
                            mediumPos = medPos,
                            mediumRot = medRot,
                            mediumScale = medScale,
                            hotPos = hotPos,
                            hotRot = hotRot,
                            hotScale = hotScale
                        });
                    }
                    else
                    {
                        unresolved++;
                        if (unresolved <= 5)
                            ParsekLog.VerboseRateLimited("GhostVisual", $"ghost-build-{partName}",
                                $"    [DIAG] {heatSource} '{partName}': unresolved path '{path}'", 60.0);
                    }
                }
            }

            // A ModuleAnimateHeat clip's own colours; the generic ramp only for a part whose colours
            // another source drives, or a clip that animates none.
            List<HeatMaterialState> heatMaterialStates = null;
            if (heatSource == HeatSourceAnimateHeat)
                heatMaterialStates = BuildClipHeatMaterialStates(
                    prefab, modelRoot, modelNodeTransform, cloneMap, heatAnimName, partName, persistentId);
            if (heatMaterialStates == null)
                heatMaterialStates = BuildHeatMaterialStates(
                    modelNodeTransform, partName, persistentId, resolvedHeatTransforms);

            if ((resolvedHeatTransforms != null && resolvedHeatTransforms.Count > 0) ||
                (heatMaterialStates != null && heatMaterialStates.Count > 0))
            {
                var heatInfo = new HeatGhostInfo
                {
                    partPersistentId = persistentId,
                    transforms = resolvedHeatTransforms,
                    materialStates = heatMaterialStates
                };

                if (heatMaterialStates != null)
                {
                    for (int i = 0; i < heatMaterialStates.Count; i++)
                    {
                        HeatMaterialState materialState = heatMaterialStates[i];
                        if (materialState.material == null) continue;

                        if (!string.IsNullOrEmpty(materialState.colorProperty))
                            materialState.material.SetColor(materialState.colorProperty, materialState.coldColor);
                        if (!string.IsNullOrEmpty(materialState.emissiveProperty))
                            materialState.material.SetColor(materialState.emissiveProperty, materialState.coldEmission);
                    }
                }

                return heatInfo;
            }
            return null;
        }

        /// <summary>Pure: a ModuleAnimateHeat clip the model lacks plays nothing in stock.</summary>
        internal static bool IsHeatClipMissing(string heatSource, bool modelHoldsClip)
            => heatSource == HeatSourceAnimateHeat && !modelHoldsClip;

        private static bool ModelHoldsClip(Transform modelRoot, string clipName)
        {
            if (modelRoot == null || string.IsNullOrEmpty(clipName)) return false;
            Animation[] animations = modelRoot.GetComponentsInChildren<Animation>(true);
            for (int i = 0; i < animations.Length; i++)
                if (animations[i] != null && animations[i][clipName] != null) return true;
            return false;
        }

        /// <summary>
        /// Runs the deployable animation cascade: tries ModuleDeployablePart, gear, ladder,
        /// animation-group, animate-generic, aero-surface, control-surface, robot-arm-scanner,
        /// and cargo-bay detection in priority order. Returns the first successfully resolved
        /// DeployableGhostInfo, or null if none matched.
        /// </summary>
        private static DeployableGhostInfo TryBuildDeployableInfo(
            Part prefab, ConfigNode partNode, uint persistentId, string partName,
            Transform modelRoot, Transform modelNodeTransform,
            Dictionary<Transform, Transform> cloneMap,
            bool hasRetractableLadder, string ladderAnimName, string ladderAnimRootName,
            bool hasAnimationGroupDeploy, string animationGroupDeployAnimName,
            bool hasStandaloneAnimateGenericDeploy, string standaloneAnimateGenericAnimName,
            bool hasAeroSurfaceDeploy, string aeroSurfaceTransformName, float aeroSurfaceDeployAngle,
            bool hasControlSurfaceDeploy, string controlSurfaceTransformName, float controlSurfaceDeployAngle,
            bool hasRobotArmScannerDeploy, List<string> robotArmScannerAnimCandidates)
        {
            // Detect deployable parts (solar panels, antennas, radiators) and pre-resolve transform states
            DeployableGhostInfo deployableInfo = null;
            ModuleDeployablePart deployable = prefab.FindModuleImplementing<ModuleDeployablePart>();
            if (deployable != null)
            {
                var sampledStates = SampleDeployableStates(prefab, deployable);
                deployableInfo = ResolveSampledStatesToDeployableInfo(
                    sampledStates, modelNodeTransform, persistentId, partName, "Deployable", logUnresolved: true);
                // PollDeployables emits at EXTENDED / RETRACTED, the end of stock's clip, which
                // ModuleDeployablePart plays at speed 1 in flight (decompiled Extend / Retract).
                if (deployableInfo != null)
                    StampEndMarkedDeployMotion(deployableInfo, partName, persistentId, "Deployable", 1f, 1f, 1f);

                // S6: the break subtree, resolved INDEPENDENTLY of the animation cascade above.
                // A fixed (non-retractable) panel has no animationName and so produces no
                // deployableInfo, but it can still break — so when the animation path found
                // nothing we materialise a transforms-only-empty info to carry the break root.
                Transform breakRoot = TryResolveDeployableBreakSubtreeRoot(
                    prefab, deployable, modelRoot, modelNodeTransform, cloneMap, partName, persistentId);
                if (breakRoot != null)
                {
                    if (deployableInfo == null)
                    {
                        deployableInfo = new DeployableGhostInfo
                        {
                            partPersistentId = persistentId,
                            transforms = new List<DeployableTransformState>(),
                            clipLengthSeconds = ResolveDeployableClipSeconds(partName)
                        };
                    }
                    deployableInfo.breakSubtreeRoot = breakRoot;
                }
            }

            // Detect landing gear / legs (ModuleWheels.ModuleWheelDeployment) — reuses DeployableGhostInfo
            if (deployableInfo == null)
            {
                for (int m = 0; m < prefab.Modules.Count; m++)
                {
                    var wheel = prefab.Modules[m] as ModuleWheels.ModuleWheelDeployment;
                    if (wheel == null) continue;

                    string animStateName = wheel.animationStateName;
                    string animTrfName = wheel.animationTrfName;
                    float depPos = wheel.deployedPosition;
                    var sampledStates = SampleGearStates(prefab, animStateName, animTrfName, depPos);
                    deployableInfo = ResolveSampledStatesToDeployableInfo(
                        sampledStates, modelNodeTransform, persistentId, partName, "Gear", logUnresolved: false);
                    // PollGear emits at "Deployed" / "Retracted", the end of stock's clip, which
                    // ModuleWheelDeployment plays at its own extend / retract duration factors
                    // (decompiled: animationState.speed = extendDurationFactor; the stock legs
                    // extend at 0.5).
                    if (deployableInfo != null)
                        StampEndMarkedDeployMotion(deployableInfo, partName, persistentId, "Gear",
                            wheel.extendDurationFactor, wheel.retractDurationFactor, 1f);
                    break; // one deployment module per part
                }
            }
            else if (prefab.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>() != null)
            {
            }

            // Detect stock retractable ladders (RetractableLadder module) - reuses DeployableGhostInfo.
            // Fixed stock rest-state endpoints, never endpoint scoring: the model's rest pose is
            // the extended ladder.
            if (deployableInfo == null && hasRetractableLadder)
            {
                var sampledStates = SampleRetractableLadderStates(prefab, ladderAnimName, ladderAnimRootName);
                deployableInfo = ResolveSampledStatesToDeployableInfo(
                    sampledStates, modelNodeTransform, persistentId, partName, "Ladder", logUnresolved: true);
                // PollLadders emits at Extended / Retracted, the end of stock's clip (speed 1).
                if (deployableInfo != null)
                    StampEndMarkedDeployMotion(deployableInfo, partName, persistentId, "Ladder", 1f, 1f, 1f);
            }

            // Detect ModuleAnimationGroup deploy animations (e.g. drills) — reuses DeployableGhostInfo
            if (deployableInfo == null && hasAnimationGroupDeploy)
            {
                var sampledStates = SampleLadderStates(prefab, animationGroupDeployAnimName, null);
                deployableInfo = ResolveSampledStatesToDeployableInfo(
                    sampledStates, modelNodeTransform, persistentId, partName, "AnimationGroup", logUnresolved: true);
            }

            // Detect standalone ModuleAnimateGeneric deploy animations (e.g. science/inflatable parts).
            // Fixed stock animTime endpoints, never endpoint scoring: an inflatable's model rest
            // pose is the inflated one.
            if (deployableInfo == null && hasStandaloneAnimateGenericDeploy)
            {
                var sampledStates = SampleStandaloneAnimateGenericStates(prefab, standaloneAnimateGenericAnimName);
                deployableInfo = ResolveSampledStatesToDeployableInfo(
                    sampledStates, modelNodeTransform, persistentId, partName, "AnimateGeneric", logUnresolved: true);
                // PollAnimateGeneric emits at animTime 0.99 / 0.01, the end of stock's clip, which
                // the Toggle event plays at speed 1 in flight (decompiled UpdateAnimSwitch).
                if (deployableInfo != null)
                    StampEndMarkedDeployMotion(deployableInfo, partName, persistentId, "AnimateGeneric", 1f, 1f, 1f);
            }

            // Detect ModuleAeroSurface visual transforms (airbrakes/control surfaces).
            if (deployableInfo == null && hasAeroSurfaceDeploy)
            {
                deployableInfo = BuildSurfaceDeployableInfo(
                    modelRoot, modelNodeTransform, cloneMap,
                    aeroSurfaceTransformName, aeroSurfaceDeployAngle,
                    persistentId, partName, "AeroSurface");
            }

            // Detect ModuleControlSurface visual transforms (elevons/rudders/prop blades).
            if (deployableInfo == null && hasControlSurfaceDeploy)
            {
                deployableInfo = BuildSurfaceDeployableInfo(
                    modelRoot, modelNodeTransform, cloneMap,
                    controlSurfaceTransformName, controlSurfaceDeployAngle,
                    persistentId, partName, "ControlSurface");
            }

            // Detect ModuleRobotArmScanner deploy/unpack animation (Breaking Ground ROC arms).
            if (deployableInfo == null && hasRobotArmScannerDeploy)
            {
                string selectedAnimName = null;
                List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
                    Vector3 dPos, Quaternion dRot, Vector3 dScale)> sampledStates = null;
                int bestCount = -1;

                for (int i = 0; i < robotArmScannerAnimCandidates.Count; i++)
                {
                    string candidateAnim = robotArmScannerAnimCandidates[i];
                    var candidateStates = SampleLadderStates(prefab, candidateAnim, null);
                    int candidateCount = candidateStates != null ? candidateStates.Count : 0;
                    if (candidateCount > bestCount)
                    {
                        bestCount = candidateCount;
                        sampledStates = candidateStates;
                        selectedAnimName = candidateAnim;
                    }
                }

                deployableInfo = ResolveSampledStatesToDeployableInfo(
                    sampledStates, modelNodeTransform, persistentId, partName, "RobotArmScanner", logUnresolved: true);
            }

            // Detect cargo bays (ModuleCargoBay + linked ModuleAnimateGeneric) — reuses DeployableGhostInfo
            if (deployableInfo == null)
            {
                ModuleCargoBay cargoBay = prefab.FindModuleImplementing<ModuleCargoBay>();
                if (cargoBay != null)
                {
                    // Falls back to any ModuleAnimateGeneric when DeployModuleIndex does not
                    // resolve (KSP inserting internal modules shifts indices).
                    ModuleAnimateGeneric animModule = ResolvePrefabCargoAnimateGeneric(prefab, cargoBay);

                    if (animModule != null && !string.IsNullOrEmpty(animModule.animationName))
                    {
                        // The limit comes from THIS vessel's snapshot (deployPercent persists)
                        // through the same node pick and resolver the snapshot baseline uses,
                        // so the spawn pose and every later CargoBayOpened agree.
                        float? deployLimitStop = ResolveSnapshotCargoBayDeployLimitStop(
                            FindSnapshotCargoAnimateGenericNode(partNode, cargoBay.DeployModuleIndex),
                            animModule.allowDeployLimit, animModule.revClampPercent,
                            cargoBay.closedPosition);
                        var sampledStates = SampleCargoBayStates(
                            prefab, animModule.animationName, cargoBay.closedPosition, deployLimitStop);
                        deployableInfo = ResolveSampledStatesToDeployableInfo(
                            sampledStates, modelNodeTransform, persistentId, partName, "CargoBay", logUnresolved: true);
                        // PollCargoBays emits once the doors settle at an end or the deploy-limit
                        // stop, the end of stock's clip (Toggle, speed 1); a limited bay travels
                        // only the share of the clip up to its stop.
                        if (deployableInfo != null)
                            StampEndMarkedDeployMotion(deployableInfo, partName, persistentId, "CargoBay", 1f, 1f,
                                CargoBayMotionTravel(cargoBay.closedPosition, deployLimitStop));

                        if (deployableInfo != null)
                        {
                            // Snap ghost to closed (stowed) state at build time.
                            // Mk3 cargo bays have closedPosition=1 so prefab default (animTime=0) is OPEN.
                            // Without this snap, the ghost would show open doors until the first event.
                            var resolvedTransforms = deployableInfo.transforms;
                            for (int i = 0; i < resolvedTransforms.Count; i++)
                            {
                                var ts = resolvedTransforms[i];
                                if (ts.t == null) continue;
                                ts.t.localPosition = ts.stowedPos;
                                ts.t.localRotation = ts.stowedRot;
                                ts.t.localScale = ts.stowedScale;
                            }

                        }
                    }
                }
            }

            return deployableInfo;
        }

        /// <summary>
        /// S6: resolves the ghost-side clone of the transform stock hides when a
        /// ModuleDeployablePart breaks.
        ///
        /// THE NAME, and why the fallback is not optional. Stock reads
        /// <c>panelBreakTransform = part.FindModelTransform(breakName)</c> in OnStart, having
        /// first defaulted <c>breakName</c> to <c>pivotName</c> when the config left it empty
        /// (ModuleDeployablePart, KSP 1.12.5). We read a PREFAB, whose OnStart has not run, so
        /// <c>breakName</c> is still the raw config value — usually empty, because almost no stock
        /// part sets it. Reading it without the fallback would resolve nothing for nearly every
        /// breakable part in the game, i.e. the feature would silently do nothing.
        ///
        /// Returns null when neither name resolves on the prefab or the transform cannot be
        /// mapped onto the ghost. A DeployableBroken event then degrades to no visual, which is
        /// the pre-P8 behaviour, rather than to hiding the wrong subtree.
        /// </summary>
        private static Transform TryResolveDeployableBreakSubtreeRoot(
            Part prefab, ModuleDeployablePart deployable,
            Transform modelRoot, Transform modelNodeTransform,
            Dictionary<Transform, Transform> cloneMap,
            string partName, uint persistentId)
        {
            if (prefab == null || deployable == null || modelRoot == null || modelNodeTransform == null)
                return null;

            string breakName = deployable.breakName;
            string resolvedFrom = "breakName";
            if (string.IsNullOrEmpty(breakName))
            {
                breakName = deployable.pivotName;
                resolvedFrom = "pivotName (stock OnStart default)";
            }
            if (string.IsNullOrEmpty(breakName))
                return null;

            Transform sourceBreak = prefab.FindModelTransform(breakName);
            if (sourceBreak == null)
            {
                ParsekLog.VerboseRateLimited("GhostVisual", $"break-subtree-miss-{partName}",
                    $"    Deployable '{partName}' pid={persistentId}: break transform '{breakName}' " +
                    $"({resolvedFrom}) not found on the prefab — DeployableBroken will have no visual", 60.0);
                return null;
            }

            Transform ghostBreak = ResolveGhostTransformForPrefabTransform(
                sourceBreak, modelRoot, modelNodeTransform, cloneMap);
            if (ghostBreak == null)
            {
                ParsekLog.VerboseRateLimited("GhostVisual", $"break-subtree-unmapped-{partName}",
                    $"    Deployable '{partName}' pid={persistentId}: break transform '{breakName}' " +
                    $"({resolvedFrom}) did not map onto the ghost — DeployableBroken will have no visual", 60.0);
                return null;
            }

            ParsekLog.VerboseRateLimited("GhostVisual", $"break-subtree-{partName}",
                $"    Deployable '{partName}' pid={persistentId}: break subtree '{breakName}' " +
                $"resolved from {resolvedFrom}", 30.0);
            return ghostBreak;
        }

        /// <summary>
        /// Pure: the share of a cargo bay's clip its open motion travels: to the deploy-limit
        /// stop when one applies, else the whole clip.
        /// </summary>
        internal static float CargoBayMotionTravel(float closedPosition, float? deployLimitStop)
        {
            if (!deployLimitStop.HasValue) return 1f;
            float closedEnd = closedPosition > 0.5f ? 1f : 0f;
            return Mathf.Clamp01(Mathf.Abs(deployLimitStop.Value - closedEnd));
        }

        /// <summary>
        /// Marks a deployable info built for a family whose recorder event marks the END of
        /// stock's motion, with its two motion times; one Verbose line per part build.
        /// </summary>
        private static void StampEndMarkedDeployMotion(
            DeployableGhostInfo info, string partName, uint persistentId, string family,
            float extendSpeedFactor, float retractSpeedFactor, float travel)
        {
            GhostPlaybackLogic.StampEndMarkedMotion(info, extendSpeedFactor, retractSpeedFactor, travel);
            ParsekLog.VerboseRateLimited("GhostVisual", $"deploy-motion-end-stamp-{partName}",
                string.Format(CultureInfo.InvariantCulture,
                    "    {0} '{1}' pid={2}: event marks the end of the motion; extend={3:F2}s retract={4:F2}s " +
                    "(clip={5:F2}s speed={6:F2}/{7:F2} travel={8:F2})",
                    family, partName, persistentId, info.extendMotionSeconds, info.retractMotionSeconds,
                    info.clipLengthSeconds, extendSpeedFactor, retractSpeedFactor, travel), 60.0);
        }

        /// <summary>
        /// Resolves sampled animation states to ghost transforms and builds a DeployableGhostInfo.
        /// Deduplicates the repeated resolve-loop pattern used by deployable, gear, ladder,
        /// animation-group, animate-generic, and robot-arm-scanner detection paths.
        /// Returns null if sampledStates is null or no transforms could be resolved.
        /// </summary>
        private static DeployableGhostInfo ResolveSampledStatesToDeployableInfo(
            List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
                Vector3 dPos, Quaternion dRot, Vector3 dScale)> sampledStates,
            Transform modelNodeTransform,
            uint persistentId,
            string partName,
            string moduleLabel,
            bool logUnresolved)
        {
            if (sampledStates == null)
                return null;

            var resolvedTransforms = new List<DeployableTransformState>();
            int unresolved = 0;
            for (int s = 0; s < sampledStates.Count; s++)
            {
                var (path, sPos, sRot, sScale, dPos, dRot, dScale) = sampledStates[s];
                Transform ghostT = FindTransformByPath(modelNodeTransform, path);
                if (ghostT != null)
                {
                    resolvedTransforms.Add(new DeployableTransformState
                    {
                        t = ghostT,
                        stowedPos = sPos,
                        stowedRot = sRot,
                        stowedScale = sScale,
                        deployedPos = dPos,
                        deployedRot = dRot,
                        deployedScale = dScale
                    });
                }
                else if (logUnresolved)
                {
                    unresolved++;
                    if (unresolved <= 5)
                        ParsekLog.VerboseRateLimited("GhostVisual", $"ghost-build-{partName}",
                            $"    [DIAG] {moduleLabel} '{partName}': unresolved path '{path}'", 60.0);
                }
            }

            if (resolvedTransforms.Count > 0)
            {
                return new DeployableGhostInfo
                {
                    partPersistentId = persistentId,
                    transforms = resolvedTransforms,
                    // S2: the prefab clip's own length, so a 1.2 s gear cycle and a 12 s solar
                    // array each animate at their real pace instead of a shared guess.
                    clipLengthSeconds = ResolveDeployableClipSeconds(partName)
                };
            }

            return null;
        }

        /// <summary>
        /// Builds a LightGhostInfo by cloning Light components from the prefab model into the ghost.
        /// Returns null if no lights were found or cloned.
        /// </summary>
        private static LightGhostInfo BuildLightGhostInfo(
            Part prefab, ConfigNode partNode, Transform modelRoot, Transform modelNodeTransform,
            Dictionary<Transform, Transform> cloneMap,
            uint persistentId, string partName,
            bool raiseLightVisualOnly)
        {
            ModuleLight lightModule = prefab.FindModuleImplementing<ModuleLight>();
            if (lightModule == null)
                return null;

            // Stock OnStart -> ModifyLightVector aims the lamp head whether or not the model
            // carries a Light, so this runs before the no-Light return below.
            ApplyLightMovementTransforms(prefab, lightModule, partNode, modelRoot, modelNodeTransform,
                cloneMap, persistentId, partName);

            var prefabLights = modelRoot.GetComponentsInChildren<Light>(true);
            if (prefabLights == null || prefabLights.Length == 0)
                return null;

            // Stock (ModuleLight, decompiled): OnStart colours every Light named lightName with the
            // persisted colour, and its FixedUpdate brings their intensity to 1 (useAutoDim, which
            // its own first SetLightState turns on; the model's authored 1.2 to 2 never shows). A
            // Light of another name is not the module's and keeps what the model authored.
            Color stockColor = ResolveLightOnColor(FindModuleNode(partNode, lightModule.moduleName),
                lightModule.lightR, lightModule.lightG, lightModule.lightB);
            int stockDriven = 0, cookies = 0;
            Texture moduleCookie = ResolveReStockLightCookie(prefab, partName);

            var clonedLights = new List<Light>();
            var drivenFlags = new List<bool>();
            var onIntensities = new List<float>();
            for (int li = 0; li < prefabLights.Length; li++)
            {
                Light srcLight = prefabLights[li];
                if (srcLight == null) continue;

                // Mirror the transform chain to find the correct ghost parent
                Transform ghostParent = MirrorTransformChain(
                    srcLight.transform, modelRoot, modelNodeTransform, cloneMap);

                bool driven = IsModuleLightDriven(srcLight.gameObject.name, lightModule.lightName);
                if (driven) stockDriven++;
                Color clonedColor = driven ? stockColor : srcLight.color;
                float clonedIntensity = driven ? ModuleLightSettledIntensity : srcLight.intensity;
                float clonedRange = srcLight.range;
                if (clonedIntensity <= 0.001f)
                    clonedIntensity = LightMinimumIntensity;
                if (clonedRange <= 0.001f)
                    clonedRange = LightMinimumRange;

                if (raiseLightVisualOnly)
                {
                    clonedIntensity = Mathf.Max(
                        clonedIntensity * LightShowcaseIntensityScale,
                        LightShowcaseMinimumIntensity);
                    clonedRange = Mathf.Max(
                        clonedRange * LightShowcaseRangeScale,
                        LightShowcaseMinimumRange);
                }

                // Create a new Light component on the ghost transform
                Light ghostLight = ghostParent.gameObject.AddComponent<Light>();
                ghostLight.type = srcLight.type;
                ghostLight.color = clonedColor;
                ghostLight.intensity = clonedIntensity;
                ghostLight.range = clonedRange;
                ghostLight.spotAngle = srcLight.spotAngle;
                ghostLight.cullingMask = srcLight.cullingMask;
                ghostLight.cookie = driven && moduleCookie != null ? moduleCookie : srcLight.cookie;
                ghostLight.cookieSize = srcLight.cookieSize;
                if (ghostLight.cookie != null) cookies++;
                ghostLight.shadows = LightShadows.None;
                ghostLight.renderMode = raiseLightVisualOnly
                    ? LightRenderMode.ForcePixel
                    : srcLight.renderMode;
                ghostLight.enabled = false;
                clonedLights.Add(ghostLight);
                drivenFlags.Add(driven);
                onIntensities.Add(clonedIntensity);
            }

            if (clonedLights.Count > 0)
            {
                var info = new LightGhostInfo
                {
                    partPersistentId = persistentId,
                    lights = clonedLights,
                    stockDriven = drivenFlags,
                    onIntensities = onIntensities,
                    brightenSpeed = lightModule.lightBrightenSpeed,
                    dimSpeed = lightModule.lightDimSpeed,
                    animationDim = lightModule.useAnimationDim
                };
                AttachLightLensAndFlare(info, lightModule, partNode, modelRoot, cloneMap,
                    persistentId, partName);
                ParsekLog.VerboseRateLimited("GhostVisual", "light-values-" + partName,
                    $"  Light '{partName}' pid={persistentId}: {clonedLights.Count} light(s), " +
                    $"{stockDriven} driven as stock ('{lightModule.lightName}') at intensity " +
                    $"{ModuleLightSettledIntensity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"color {stockColor} cookies={cookies} fade brighten={info.brightenSpeed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"dim={info.dimSpeed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} animationDim={info.animationDim}", 60.0);
                return info;
            }

            return null;
        }

        internal const string ReStockEnhancedLightModule = "ModuleRestockEnhancedLight";

        /// <summary>
        /// The light pattern ReStock's ModuleRestockEnhancedLight puts on every Light its part's
        /// ModuleLight drives (decompiled OnStart: <c>GameDatabase.GetTexture(cookiePath)</c>,
        /// wrap mode clamp). The live part gets it at start, so the prefab's Light carries none.
        /// Null when the part has no such module or the texture is not loaded.
        /// </summary>
        private static Texture ResolveReStockLightCookie(Part prefab, string partName)
        {
            PartModule module = null;
            for (int m = 0; m < prefab.Modules.Count && module == null; m++)
                if (prefab.Modules[m].moduleName == ReStockEnhancedLightModule) module = prefab.Modules[m];
            if (module == null) return null;
            string path = module.Fields.GetValue("cookiePath") as string;
            Texture2D cookie = string.IsNullOrEmpty(path)
                ? null
                : GameDatabase.Instance.GetTexture(path, false);
            if (cookie != null) cookie.wrapMode = TextureWrapMode.Clamp;
            ParsekLog.VerboseRateLimited("GhostVisual", "light-cookie-" + partName,
                $"  Light '{partName}': {ReStockEnhancedLightModule} cookie '{path}' " +
                (cookie != null ? "applied" : "not found, plain light"), 60.0);
            return cookie;
        }

        /// <summary>The intensity stock's ModuleLight settles its lights at (no malfunction, power met).</summary>
        internal const float ModuleLightSettledIntensity = 1f;

        /// <summary>
        /// Pure: whether a Light is one of ModuleLight's own, per
        /// <c>Part.FindModelComponents&lt;Light&gt;(lightName)</c> (decompiled): the exact object
        /// name, any object when the name is empty.
        /// </summary>
        internal static bool IsModuleLightDriven(string lightObjectName, string moduleLightName)
            => moduleLightName != null && (moduleLightName.Length == 0 || lightObjectName == moduleLightName);

        /// <summary>
        /// Stock <c>ModuleLight.GetAngleVector</c> (KSP 1.12.5, decompiled): the first character
        /// of the axis name picks X / Y / Z (case-insensitive), a '-' second character negates
        /// the angle, anything else (or an empty name) is no rotation.
        /// </summary>
        internal static Vector3 ResolveLightAngleVector(string axisName, float angle)
        {
            if (string.IsNullOrEmpty(axisName)) return Vector3.zero;
            if (axisName.Length > 1 && axisName[1] == '-')
                angle = -angle;
            string axis = axisName.Substring(0, 1).ToUpperInvariant();
            if (axis == "X") return new Vector3(angle, 0f, 0f);
            if (axis == "Y") return new Vector3(0f, angle, 0f);
            if (axis == "Z") return new Vector3(0f, 0f, angle);
            return Vector3.zero;
        }

        /// <summary>
        /// Stock <c>ModuleLight.OnStart</c>'s transform lookup: the combined transform is
        /// <c>movementTransformName</c>, replaced by the rotate name when the rotate and pitch
        /// names are equal; only when that finds nothing are the separate rotate and pitch
        /// transforms looked up. Generic over the lookup so it is testable without Unity.
        /// </summary>
        internal static void ResolveLightMovementTransforms<T>(
            string movementName, string rotateName, string pitchName, System.Func<string, T> find,
            out T combined, out T rotate, out T pitch) where T : class
        {
            combined = find(movementName);
            if (rotateName == pitchName)
                combined = find(rotateName);
            rotate = null;
            pitch = null;
            if (combined == null)
            {
                rotate = find(rotateName);
                pitch = find(pitchName);
            }
        }

        /// <summary>Stock SetRotationLimits / SetPitchLimits: Mathf.Clamp(value, min, max).</summary>
        internal static float ClampLightAngle(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        /// <summary>
        /// Stock <c>ModuleLight.ModifyLightVector</c>, run once at build: the combined transform
        /// gets Euler(rotate) * Euler(pitch), the separate rotate / pitch transforms their own
        /// Euler. The angles are the snapshot's persisted <c>rotationAngle</c> / <c>pitchAngle</c>
        /// clamped to the module's limits (0 / 0 when absent, which is identity, NOT the model's
        /// rest rotation - ReStock's lamp heads rest 180 degrees off it). The persisted
        /// <c>movementTransformName</c> is read from the snapshot first.
        /// </summary>
        private static void ApplyLightMovementTransforms(
            Part prefab, ModuleLight lightModule, ConfigNode partNode, Transform modelRoot,
            Transform modelNodeTransform, Dictionary<Transform, Transform> cloneMap,
            uint persistentId, string partName)
        {
            ConfigNode snapshotModule = FindModuleNode(partNode, lightModule.moduleName);
            string movementName = snapshotModule?.GetValue("movementTransformName");
            if (string.IsNullOrEmpty(movementName))
                movementName = lightModule.movementTransformName;
            float rotation = ParseFloatOr(snapshotModule?.GetValue("rotationAngle"), lightModule.rotationAngle);
            float pitch = ParseFloatOr(snapshotModule?.GetValue("pitchAngle"), lightModule.pitchAngle);
            rotation = ClampLightAngle(rotation, lightModule.rotateMin, lightModule.rotateMax);
            pitch = ClampLightAngle(pitch, lightModule.pitchMin, lightModule.pitchMax);

            System.Func<string, Transform> find = name =>
            {
                if (string.IsNullOrEmpty(name)) return null;
                Transform src = prefab.FindModelTransform(name);
                if (src == null) return null;
                if (src == modelRoot) return modelNodeTransform;
                return cloneMap.TryGetValue(src, out Transform ghost)
                    ? ghost
                    : MirrorTransformChain(src, modelRoot, modelNodeTransform, cloneMap);
            };
            ResolveLightMovementTransforms(movementName, lightModule.movementRotateTransformName,
                lightModule.movementPitchTransformName, find,
                out Transform combined, out Transform rotateTransform, out Transform pitchTransform);

            Vector3 rotateEuler = ResolveLightAngleVector(lightModule.rotateAxisName, rotation);
            Vector3 pitchEuler = ResolveLightAngleVector(lightModule.pitchAxisName, pitch);
            if (combined != null)
                combined.localRotation = Quaternion.Euler(rotateEuler) * Quaternion.Euler(pitchEuler);
            if (rotateTransform != null)
                rotateTransform.localRotation = Quaternion.Euler(rotateEuler);
            if (pitchTransform != null)
                pitchTransform.localRotation = Quaternion.Euler(pitchEuler);

            if (combined != null || rotateTransform != null || pitchTransform != null)
            {
                ParsekLog.VerboseRateLimited("GhostVisual", $"light-move-{partName}",
                    $"  Light '{partName}' pid={persistentId}: movement transforms aimed as stock " +
                    $"combined={(combined != null ? combined.name : "none")} " +
                    $"rotate={(rotateTransform != null ? rotateTransform.name : "none")} " +
                    $"pitch={(pitchTransform != null ? pitchTransform.name : "none")} " +
                    $"rotationAngle={rotation.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"pitchAngle={pitch.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}", 60.0);
            }
        }

        /// <summary>
        /// Binds the ghost clones of stock ModuleLight's lens renderer
        /// (<c>lightMeshRendererName</c>) and flare renderer (<c>flareRendererName</c>,
        /// default "Flare") and starts them OFF, as stock does on a light that is off: its
        /// FixedUpdate fades the lens <c>_EmissiveColor</c> between black and the light colour
        /// and the flare <c>_TintColor</c> between black and white (KSP 1.12.5, decompiled).
        /// Stock's OnStart writes the off colour (the default, transparent black) at once for a
        /// light that is off, so without this the lens of an unlit ghost keeps the prefab
        /// material's lit emissive and glows. Only the first material is driven (stock writes
        /// <c>renderer.material</c>); it is cloned per ghost so the prefab's shared material
        /// is never written. With no lens name, or a name that finds nothing, stock falls back
        /// to the part's first renderer (the one-mesh spotLight1 / spotLight2 bodies), and so
        /// does the ghost; the flare's TransparentFX-layer fallback is not transcribed.
        /// </summary>
        private static void AttachLightLensAndFlare(
            LightGhostInfo info, ModuleLight lightModule, ConfigNode partNode,
            Transform modelRoot, Dictionary<Transform, Transform> cloneMap,
            uint persistentId, string partName)
        {
            ConfigNode snapshotLight = FindModuleNode(partNode, "ModuleLight");
            info.lensOnColor = ResolveLightOnColor(snapshotLight,
                lightModule.lightR, lightModule.lightG, lightModule.lightB);

            info.lensMaterials = CloneFirstMaterialOfNamedRenderer(
                modelRoot, cloneMap, lightModule.lightMeshRendererName, "_EmissiveColor",
                fallbackToFirstRenderer: true);
            info.flareMaterials = CloneFirstMaterialOfNamedRenderer(
                modelRoot, cloneMap, lightModule.flareRendererName, "_TintColor",
                fallbackToFirstRenderer: false);

            GhostPlaybackLogic.WriteLightLensAndFlare(info, false);

            int lensCount = info.lensMaterials != null ? info.lensMaterials.Count : 0;
            int flareCount = info.flareMaterials != null ? info.flareMaterials.Count : 0;
            ParsekLog.VerboseRateLimited("GhostVisual", "light-lens-" + partName,
                "  Light '" + partName + "' pid=" + persistentId + ": lens='"
                + lightModule.lightMeshRendererName + "' materials=" + lensCount
                + " flare='" + lightModule.flareRendererName + "' materials=" + flareCount
                + " onColor=" + info.lensOnColor, 60.0);
        }

        private static List<Material> CloneFirstMaterialOfNamedRenderer(
            Transform modelRoot, Dictionary<Transform, Transform> cloneMap,
            string rendererName, string shaderProperty, bool fallbackToFirstRenderer)
        {
            if (modelRoot == null || cloneMap == null)
                return null;
            MeshRenderer src = null;
            if (!string.IsNullOrEmpty(rendererName))
            {
                MeshRenderer[] prefabRenderers = modelRoot.GetComponentsInChildren<MeshRenderer>(true);
                for (int i = 0; i < prefabRenderers.Length; i++)
                {
                    if (prefabRenderers[i] != null && prefabRenderers[i].transform.name == rendererName)
                    {
                        src = prefabRenderers[i];
                        break;
                    }
                }
            }
            if (src == null && fallbackToFirstRenderer)
            {
                // Stock: lightMeshRenderer = (MeshRenderer)part.GetPartRenderers()[0], the
                // first renderer under the part's "model" transform.
                Renderer[] all = modelRoot.GetComponentsInChildren<Renderer>(true);
                if (all != null && all.Length > 0)
                    src = all[0] as MeshRenderer;
            }
            if (src == null)
                return null;

            Transform ghostTransform;
            if (!cloneMap.TryGetValue(src.transform, out ghostTransform) || ghostTransform == null)
                return null;
            MeshRenderer ghostRenderer = ghostTransform.GetComponent<MeshRenderer>();
            if (ghostRenderer == null) return null;
            Material[] mats = ghostRenderer.sharedMaterials;
            if (mats == null || mats.Length == 0 || mats[0] == null
                || !mats[0].HasProperty(shaderProperty))
                return null;
            var copy = (Material[])mats.Clone();
            copy[0] = new Material(mats[0]);
            ghostRenderer.sharedMaterials = copy;
            return new List<Material> { copy[0] };
        }

        /// <summary>
        /// Pure: the colour stock ModuleLight lights with, <c>new Color(lightR, lightG,
        /// lightB)</c> from the persisted fields in the snapshot module, each falling back to
        /// the prefab's value. Black is stock's default grey: SetLightState lights a black lamp
        /// with it (decompiled <c>defaultColor</c>).
        /// </summary>
        internal static Color ResolveLightOnColor(
            ConfigNode snapshotLightModule, float prefabR, float prefabG, float prefabB)
        {
            float r = ReadFloatOr(snapshotLightModule, "lightR", prefabR);
            float g = ReadFloatOr(snapshotLightModule, "lightG", prefabG);
            float b = ReadFloatOr(snapshotLightModule, "lightB", prefabB);
            return r == 0f && g == 0f && b == 0f
                ? new Color(0.875f, 0.875f, 0.875f)
                : new Color(r, g, b);
        }

        private static float ReadFloatOr(ConfigNode node, string key, float fallback)
        {
            string raw = node != null ? node.GetValue(key) : null;
            float v;
            if (!string.IsNullOrEmpty(raw)
                && float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return v;
            return fallback;
        }

        /// <summary>
        /// Clones SkinnedMeshRenderers from the prefab model into the ghost, preserving bone
        /// bindings and skinning quality. Returns the number of SkinnedMeshRenderers cloned.
        /// </summary>
        private static int CloneSkinnedMeshRenderers(
            SkinnedMeshRenderer[] skinnedRenderers,
            Transform modelRoot, Transform modelNodeTransform, Transform partRootTransform,
            Part prefab, Dictionary<Transform, Transform> cloneMap,
            bool filterInactiveVariantRenderers,
            bool hasVariantGameObjectRules,
            Dictionary<string, bool> selectedVariantGameObjects,
            HashSet<string> damagedWheelNames,
            HashSet<string> depthMaskNames,
            uint persistentId, string partName,
            ref int meshCount, ref int damagedSkipped, ref int nullMeshSkipped,
            ref int depthMaskSkipped)
        {
            int skinnedCloned = 0;
            for (int r = 0; r < skinnedRenderers.Length; r++)
            {
                var smr = skinnedRenderers[r];
                if (smr == null) continue;
                if (smr.sharedMesh == null)
                {
                    nullMeshSkipped++;
                    ParsekLog.Warn("GhostVisual", $"Part '{partName}' pid={persistentId}: " +
                        $"SkinnedMeshRenderer '{smr.name}' has null sharedMesh — tire mesh " +
                        $"may be procedurally generated at runtime. Ghost will be missing this mesh.");
                    continue;
                }
                if (filterInactiveVariantRenderers && !hasVariantGameObjectRules && !smr.gameObject.activeInHierarchy) continue;
                if (hasVariantGameObjectRules &&
                    !IsRendererEnabledByVariantRule(
                        smr.transform, modelRoot, selectedVariantGameObjects,
                        out string _, out bool _))
                    continue;
                if (IsRendererOnDamagedTransform(smr.transform, damagedWheelNames))
                {
                    damagedSkipped++;
                    continue;
                }
                if (IsDepthMaskRenderer(smr, depthMaskNames))
                {
                    depthMaskSkipped++;
                    continue;
                }

                Transform leaf = MirrorTransformChain(smr.transform, modelRoot, modelNodeTransform, cloneMap);

                // Preserve skinned meshes as skinned renderers so bone-driven animations
                // (e.g. drills) still articulate on ghosts.
                Transform[] ghostBones = null;
                int resolvedBones = 0;
                bool usedPartRootFallbackForBones = false;
                if (smr.bones != null && smr.bones.Length > 0)
                {
                    ghostBones = new Transform[smr.bones.Length];
                    for (int b = 0; b < smr.bones.Length; b++)
                    {
                        Transform srcBone = smr.bones[b];
                        if (srcBone == null) continue;

                        Transform ghostBone = ResolveGhostBone(
                            srcBone, modelRoot, modelNodeTransform,
                            partRootTransform, prefab, cloneMap,
                            ref usedPartRootFallbackForBones);

                        ghostBones[b] = ghostBone;
                        if (ghostBone != null)
                            resolvedBones++;
                    }
                }

                Transform ghostRootBone = null;
                Transform srcRootBone = smr.rootBone;
                if (srcRootBone != null)
                {
                    ghostRootBone = ResolveGhostBone(
                        srcRootBone, modelRoot, modelNodeTransform,
                        partRootTransform, prefab, cloneMap,
                        ref usedPartRootFallbackForBones);
                }

                var ghostSmr = leaf.gameObject.AddComponent<SkinnedMeshRenderer>();
                ghostSmr.sharedMesh = smr.sharedMesh;
                ghostSmr.sharedMaterials = smr.sharedMaterials;
                ghostSmr.bones = ghostBones ?? new Transform[0];
                ghostSmr.rootBone = ghostRootBone != null ? ghostRootBone : leaf;
                ghostSmr.quality = smr.quality;
                ghostSmr.updateWhenOffscreen = true;
                ghostSmr.localBounds = smr.localBounds;
                ghostSmr.shadowCastingMode = smr.shadowCastingMode;
                ghostSmr.receiveShadows = smr.receiveShadows;
                meshCount++;
                skinnedCloned++;
            }

            return skinnedCloned;
        }

        /// <summary>
        /// Resolves a surface-transform-based deployable (AeroSurface or ControlSurface) by finding
        /// the named transforms and computing deployed rotation from the deploy angle.
        /// Returns null if no transforms were resolved.
        /// </summary>
        private static DeployableGhostInfo BuildSurfaceDeployableInfo(
            Transform modelRoot, Transform modelNodeTransform,
            Dictionary<Transform, Transform> cloneMap,
            string transformName, float deployAngleDegrees,
            uint persistentId, string partName, string moduleLabel)
        {
            var sourceTransforms = FindTransformsRecursive(modelRoot, transformName);
            if (sourceTransforms == null || sourceTransforms.Count == 0)
            {
                return null;
            }

            var resolvedTransforms = new List<DeployableTransformState>();
            for (int i = 0; i < sourceTransforms.Count; i++)
            {
                Transform sourceTransform = sourceTransforms[i];
                if (sourceTransform == null) continue;

                Transform ghostT;
                if (!cloneMap.TryGetValue(sourceTransform, out ghostT) || ghostT == null)
                {
                    string path = GetTransformPath(sourceTransform, modelRoot);
                    ghostT = FindTransformByPath(modelNodeTransform, path);
                }
                if (ghostT == null) continue;

                Vector3 stowedPos = ghostT.localPosition;
                Quaternion stowedRot = ghostT.localRotation;
                Vector3 stowedScale = ghostT.localScale;
                Quaternion deployedRot = stowedRot * Quaternion.AngleAxis(deployAngleDegrees, Vector3.right);

                resolvedTransforms.Add(new DeployableTransformState
                {
                    t = ghostT,
                    stowedPos = stowedPos,
                    stowedRot = stowedRot,
                    stowedScale = stowedScale,
                    deployedPos = stowedPos,
                    deployedRot = deployedRot,
                    deployedScale = stowedScale
                });
            }

            if (resolvedTransforms.Count > 0)
            {
                return new DeployableGhostInfo
                {
                    partPersistentId = persistentId,
                    transforms = resolvedTransforms,
                    // S2: the prefab clip's own length, so a 1.2 s gear cycle and a 12 s solar
                    // array each animate at their real pace instead of a shared guess.
                    clipLengthSeconds = ResolveDeployableClipSeconds(partName)
                };
            }

            return null;
        }

        /// <summary>
        /// Logs a diagnostic hierarchy dump for engine parts, including warnings about
        /// MeshRenderers that live outside the model subtree (e.g. shroud transforms).
        /// </summary>
        private static void LogEnginePartHierarchyDump(
            Part prefab, Transform modelRoot, string partName, uint persistentId)
        {
            var hierarchy = new System.Text.StringBuilder();
            int nodeCount = 0;
            AppendTransformHierarchy(prefab.transform, 0, hierarchy, ref nodeCount);
            if (nodeCount > HierarchyDumpMaxNodes)
                hierarchy.Append(" ... +")
                         .Append((nodeCount - HierarchyDumpMaxNodes).ToString(CultureInfo.InvariantCulture))
                         .Append(" more (truncated at ")
                         .Append(HierarchyDumpMaxNodes.ToString(CultureInfo.InvariantCulture))
                         .Append(')');
            ParsekLog.VerboseRateLimited("GhostVisual", $"part_dump_{partName}",
                $"  ENGINE PART HIERARCHY DUMP for '{partName}' pid={persistentId}: " +
                $"{nodeCount.ToString(CultureInfo.InvariantCulture)} transforms " +
                $"[depth:name[components](INACTIVE)] {hierarchy}", 60.0);

            // Also log MeshRenderers found from Part root vs modelRoot
            var allMR = prefab.GetComponentsInChildren<MeshRenderer>(true);
            var modelMR = modelRoot.GetComponentsInChildren<MeshRenderer>(true);
            if (allMR.Length != modelMR.Length)
            {
                ParsekLog.VerboseRateLimited("GhostVisual", $"engine_mr_warn_{partName}",
                    $"  WARNING: Part root has {allMR.Length} MeshRenderers but " +
                    $"modelRoot '{modelRoot.name}' has only {modelMR.Length}! " +
                    $"Missing renderers are OUTSIDE the model subtree.", 60.0);
                foreach (var mr in allMR)
                {
                    bool isUnderModel = false;
                    Transform cur = mr.transform;
                    while (cur != null && cur != prefab.transform)
                    {
                        if (cur == modelRoot) { isUnderModel = true; break; }
                        cur = cur.parent;
                    }
                    if (!isUnderModel)
                    {
                        var mf = mr.GetComponent<MeshFilter>();
                        string meshName = mf?.sharedMesh?.name ?? "(no mesh)";
                        ParsekLog.VerboseRateLimited("GhostVisual", $"outside_mr_{partName}_{mr.gameObject.name}",
                            $"    OUTSIDE-MODEL MR: '{mr.gameObject.name}' mesh={meshName} " +
                            $"path={GetTransformPath(mr.transform, prefab.transform)}", 60.0);
                    }
                }
            }
        }

        /// <summary>
        /// Resolves jettison (shroud) transforms from all ModuleJettison modules on the prefab,
        /// maps them through the cloneMap to ghost transforms, and ensures they are visible.
        /// Some parts expose multiple ModuleJettison modules and/or comma-separated
        /// jettisonName values (e.g. fairingL,fairingR) that must all toggle together.
        /// Returns a JettisonGhostInfo if any jettison transforms were resolved, null otherwise.
        /// </summary>
        private static JettisonGhostInfo ResolveJettisonTransforms(
            Part prefab, Dictionary<Transform, Transform> cloneMap,
            uint persistentId, string partName, ConfigNode partNode)
        {
            var ghostJettisonTransforms = new List<Transform>();
            var resolvedJettisonNames = new List<string>();
            List<Transform> spawnHidden = null;
            List<ConfigNode> snapshotJettisons = SnapshotModulesNamed(partNode, "ModuleJettison");
            int jettisonOrdinal = 0;
            for (int moduleIndex = 0; moduleIndex < prefab.Modules.Count; moduleIndex++)
            {
                var jettison = prefab.Modules[moduleIndex] as ModuleJettison;
                if (jettison == null) continue;
                ConfigNode snapJettison = jettisonOrdinal < snapshotJettisons.Count
                    ? snapshotJettisons[jettisonOrdinal] : null;
                jettisonOrdinal++;

                string jettisonNameList = jettison.jettisonName;
                if (string.IsNullOrWhiteSpace(jettisonNameList)) continue;

                // A shroud list over variant meshes (engine plates): only the one stock
                // recorded as active, never a forced-on mesh of another variant.
                string activeJettisonName = snapJettison != null ? snapJettison.GetValue("activejettisonName") : null;
                List<string> jettisonNames = SelectJettisonNamesForGhost(
                    jettisonNameList, activeJettisonName,
                    name =>
                    {
                        Transform src = prefab.FindModelTransform(name);
                        Transform ghost;
                        return src != null && cloneMap.TryGetValue(src, out ghost) && ghost != null;
                    });
                if (!string.IsNullOrEmpty(activeJettisonName) && jettisonNames.Count == 1
                    && !string.Equals(jettisonNames[0], activeJettisonName.Trim(), System.StringComparison.Ordinal))
                {
                    ParsekLog.VerboseRateLimited("GhostVisual", $"jettison_active_remap_{partName}_{moduleIndex}",
                        $"    Part '{partName}' pid={persistentId} ModuleJettison[{moduleIndex}]: persisted " +
                        $"activejettisonName '{activeJettisonName}' has no ghost mesh in this variant; " +
                        $"using '{jettisonNames[0]}' (stock re-resolves on variant apply)", 60.0);
                }

                // Stock's flight-start rule decides whether this module's shroud is up when the
                // part starts; the ghost mirrors it so no event is needed to hide a shroud stock
                // never showed (todo GHOST-SHROUD-EMPTY-BOTTOM-NODE).
                JettisonSpawnHideReason hideReason = ResolveJettisonSpawnHideReason(
                    jettison.ignoreNodes, jettison.bottomNodeName, partNode, snapJettison);
                int moduleTransforms = 0;
                for (int nameIndex = 0; nameIndex < jettisonNames.Count; nameIndex++)
                {
                    string jettisonName = jettisonNames[nameIndex];
                    if (string.IsNullOrEmpty(jettisonName)) continue;

                    Transform srcJettison = prefab.FindModelTransform(jettisonName);
                    if (srcJettison == null)
                    {
                        continue;
                    }

                    Transform ghostJettison;
                    if (!cloneMap.TryGetValue(srcJettison, out ghostJettison) || ghostJettison == null)
                    {
                        ParsekLog.VerboseRateLimited("GhostVisual", $"jettison_miss_{partName}_{jettisonName}",
                            $"    Jettison '{jettisonName}' found on prefab but not in cloneMap", 60.0);
                        continue;
                    }

                    // ModuleJettison.OnStart() hasn't run on the prefab, so jettison
                    // transforms may be inactive or zero-scaled. Force them visible
                    // in the ghost — playback will hide them via ShroudJettisoned events.
                    if (!ghostJettison.gameObject.activeSelf)
                    {
                        ParsekLog.VerboseRateLimited("GhostVisual", $"jettison_activate_{partName}_{jettisonName}",
                            $"    Jettison '{jettisonName}' was inactive on ghost — activating", 60.0);
                        ghostJettison.gameObject.SetActive(true);
                    }
                    if (ghostJettison.localScale.sqrMagnitude < 0.001f)
                    {
                        ParsekLog.VerboseRateLimited("GhostVisual", $"jettison_rescale_{partName}_{jettisonName}",
                            $"    Jettison '{jettisonName}' had near-zero scale {ghostJettison.localScale} — resetting to (1,1,1)", 60.0);
                        ghostJettison.localScale = UnityEngine.Vector3.one;
                    }

                    // Log diagnostic info for jettison transform state
                    var jmf = ghostJettison.GetComponent<MeshFilter>();
                    var jmr = ghostJettison.GetComponent<MeshRenderer>();
                    string jmeshInfo = jmf?.sharedMesh != null
                        ? $"verts={jmf.sharedMesh.vertexCount} bounds={jmf.sharedMesh.bounds}"
                        : "(no mesh on jettison transform)";
                    string matInfo = "(no renderer)";
                    if (jmr != null)
                    {
                        var mats = jmr.sharedMaterials;
                        if (mats == null || mats.Length == 0)
                            matInfo = "NO MATERIALS";
                        else
                        {
                            var matNames = new System.Text.StringBuilder();
                            for (int m = 0; m < mats.Length; m++)
                            {
                                if (m > 0) matNames.Append(", ");
                                matNames.Append(mats[m] != null
                                    ? $"{mats[m].name}[{mats[m].shader?.name}]"
                                    : "null");
                            }
                            matInfo = matNames.ToString();
                        }
                    }
                    ParsekLog.VerboseRateLimited("GhostVisual", $"jettison_hit_{partName}_{jettisonName}",
                        $"    Jettison '{jettisonName}' matched: scale={ghostJettison.localScale} " +
                        $"active={ghostJettison.gameObject.activeSelf} " +
                        $"srcScale={srcJettison.localScale} srcActive={srcJettison.gameObject.activeSelf} " +
                        $"srcPos={srcJettison.localPosition} " +
                        $"mesh={jmeshInfo} materials=[{matInfo}]", 60.0);
                    // Log world position chain for spatial debugging
                    ParsekLog.VerboseRateLimited("GhostVisual", $"jettison_pos_{partName}_{jettisonName}",
                        $"    Jettison '{jettisonName}' ghost chain: " +
                        $"ghostLocalPos={ghostJettison.localPosition} " +
                        $"parentName={ghostJettison.parent?.name} " +
                        $"parentLocalPos={ghostJettison.parent?.localPosition}", 60.0);

                    moduleTransforms++;
                    if (hideReason != JettisonSpawnHideReason.None)
                    {
                        if (spawnHidden == null) spawnHidden = new List<Transform>();
                        if (!spawnHidden.Contains(ghostJettison)) spawnHidden.Add(ghostJettison);
                    }
                    if (ghostJettisonTransforms.Contains(ghostJettison)) continue;
                    ghostJettisonTransforms.Add(ghostJettison);
                    resolvedJettisonNames.Add(jettisonName);
                }

                if (moduleTransforms > 0)
                {
                    ParsekLog.VerboseRateLimited("GhostVisual",
                        $"jettison-spawn-{partName}-{persistentId}-{moduleIndex}",
                        $"    Part '{partName}' pid={persistentId} ModuleJettison[{moduleIndex}] '{jettisonNameList}': " +
                        (hideReason != JettisonSpawnHideReason.None ? "starts hidden" : "starts shown") +
                        $" (reason={hideReason} bottomNode='{jettison.bottomNodeName}' " +
                        $"bottomEmpty={IsAttachNodeEmptyInSnapshot(partNode, jettison.bottomNodeName)} " +
                        $"ignoreNodes={jettison.ignoreNodes} transforms={moduleTransforms})", 60.0);
                }
            }

            if (ghostJettisonTransforms.Count > 0)
            {
                return new JettisonGhostInfo
                {
                    partPersistentId = persistentId,
                    jettisonTransforms = ghostJettisonTransforms,
                    spawnHiddenTransforms = spawnHidden
                };
            }

            return null;
        }

        /// <summary>
        /// Checks the part snapshot's MODULE nodes for a ModuleJettison with
        /// isJettisoned = True, indicating the shroud was already jettisoned
        /// when the snapshot was captured.
        /// </summary>
        internal static bool IsJettisonedInSnapshot(ConfigNode partNode)
        {
            var modules = partNode.GetNodes("MODULE");
            for (int i = 0; i < modules.Length; i++)
            {
                if (modules[i].GetValue("name") != "ModuleJettison") continue;
                string val = modules[i].GetValue("isJettisoned");
                if (val != null && val.Equals("True", System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Mirrors all model transforms into the ghost cloneMap so that animations can
        /// find intermediate non-mesh transforms. The mesh-cloning phase only creates
        /// transforms leading to renderers; this fills in the rest.
        /// </summary>
        private static void EnsureFullModelHierarchy(
            Transform modelRoot, Transform ghostModelNode,
            Dictionary<Transform, Transform> cloneMap, string partName)
        {
            var allModelTransforms = modelRoot.GetComponentsInChildren<Transform>(true);
            int created = 0;
            for (int t = 0; t < allModelTransforms.Length; t++)
            {
                Transform src = allModelTransforms[t];
                if (src == modelRoot) continue;  // already mapped
                if (cloneMap.ContainsKey(src)) continue;  // already cloned

                // MirrorTransformChain walks from src up to modelRoot, creating
                // any missing intermediate nodes and registering them in cloneMap.
                MirrorTransformChain(src, modelRoot, ghostModelNode, cloneMap);
                created++;
            }
            ParsekLog.VerboseRateLimited("GhostVisual", $"ghost-build-{partName}",
                $"    EnsureFullHierarchy '{partName}': " +
                (created > 0
                    ? $"created {created} missing intermediate transforms for animation support"
                    : $"all {allModelTransforms.Length} model transforms already in cloneMap"), 60.0);
        }

        #endregion
    }
}
