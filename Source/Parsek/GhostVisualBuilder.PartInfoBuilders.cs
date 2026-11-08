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
                var (sdScale, sdPos, sdRot, semiOk, dScale, dPos, dRot) = SampleCanopyStates(prefab, chute);
                var parachuteInfo = new ParachuteGhostInfo
                {
                    partPersistentId = persistentId,
                    canopyTransform = ghostCanopy,
                    capTransform = ghostCap,
                    deployedCanopyScale = dScale,
                    deployedCanopyPos = dPos,
                    deployedCanopyRot = dRot,
                    semiDeployedCanopyScale = sdScale,
                    semiDeployedCanopyPos = sdPos,
                    semiDeployedCanopyRot = sdRot,
                    semiDeployedSampled = semiOk
                };

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
        /// Returns null if no heat transforms or materials were resolved.
        /// </summary>
        private static HeatGhostInfo TryBuildHeatInfo(
            Part prefab, string heatAnimName, string heatSource,
            Transform modelNodeTransform, string partName, uint persistentId)
        {
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

            List<HeatMaterialState> heatMaterialStates =
                BuildHeatMaterialStates(modelNodeTransform, partName, persistentId, resolvedHeatTransforms);

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
                    break; // one deployment module per part
                }
            }
            else if (prefab.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>() != null)
            {
            }

            // Detect stock retractable ladders (RetractableLadder module) — reuses DeployableGhostInfo
            if (deployableInfo == null && hasRetractableLadder)
            {
                var sampledStates = SampleLadderStates(prefab, ladderAnimName, ladderAnimRootName);
                deployableInfo = ResolveSampledStatesToDeployableInfo(
                    sampledStates, modelNodeTransform, persistentId, partName, "Ladder", logUnresolved: true);
            }

            // Detect ModuleAnimationGroup deploy animations (e.g. drills) — reuses DeployableGhostInfo
            if (deployableInfo == null && hasAnimationGroupDeploy)
            {
                var sampledStates = SampleLadderStates(prefab, animationGroupDeployAnimName, null);
                deployableInfo = ResolveSampledStatesToDeployableInfo(
                    sampledStates, modelNodeTransform, persistentId, partName, "AnimationGroup", logUnresolved: true);
            }

            // Detect standalone ModuleAnimateGeneric deploy animations (e.g. science/inflatable parts)
            if (deployableInfo == null && hasStandaloneAnimateGenericDeploy)
            {
                var sampledStates = SampleLadderStates(prefab, standaloneAnimateGenericAnimName, null);
                deployableInfo = ResolveSampledStatesToDeployableInfo(
                    sampledStates, modelNodeTransform, persistentId, partName, "AnimateGeneric", logUnresolved: true);
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
            Part prefab, Transform modelRoot, Transform modelNodeTransform,
            Dictionary<Transform, Transform> cloneMap,
            uint persistentId, string partName,
            bool raiseLightVisualOnly)
        {
            ModuleLight lightModule = prefab.FindModuleImplementing<ModuleLight>();
            if (lightModule == null)
                return null;

            var prefabLights = modelRoot.GetComponentsInChildren<Light>(true);
            if (prefabLights == null || prefabLights.Length == 0)
                return null;

            var clonedLights = new List<Light>();
            for (int li = 0; li < prefabLights.Length; li++)
            {
                Light srcLight = prefabLights[li];
                if (srcLight == null) continue;

                // Mirror the transform chain to find the correct ghost parent
                Transform ghostParent = MirrorTransformChain(
                    srcLight.transform, modelRoot, modelNodeTransform, cloneMap);

                float clonedIntensity = srcLight.intensity;
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
                ghostLight.color = srcLight.color;
                ghostLight.intensity = clonedIntensity;
                ghostLight.range = clonedRange;
                ghostLight.spotAngle = srcLight.spotAngle;
                ghostLight.cullingMask = srcLight.cullingMask;
                ghostLight.shadows = LightShadows.None;
                ghostLight.renderMode = raiseLightVisualOnly
                    ? LightRenderMode.ForcePixel
                    : srcLight.renderMode;
                ghostLight.enabled = false;
                clonedLights.Add(ghostLight);
            }

            if (clonedLights.Count > 0)
            {
                return new LightGhostInfo
                {
                    partPersistentId = persistentId,
                    lights = clonedLights
                };
            }

            return null;
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
                List<string> jettisonNames = SelectJettisonNames(
                    jettisonNameList, snapJettison != null ? snapJettison.GetValue("activejettisonName") : null);
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

                    if (ghostJettisonTransforms.Contains(ghostJettison)) continue;
                    ghostJettisonTransforms.Add(ghostJettison);
                    resolvedJettisonNames.Add(jettisonName);
                }
            }

            if (ghostJettisonTransforms.Count > 0)
            {
                return new JettisonGhostInfo
                {
                    partPersistentId = persistentId,
                    jettisonTransforms = ghostJettisonTransforms
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
