using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Parsek
{
    internal static partial class GhostVisualBuilder
    {
        private static readonly Regex trailingNumericSuffixRegex =
            new Regex(@"^(.*)_\d+$", RegexOptions.Compiled);
        private const string LightsShowcaseRecordingPrefix = "Part Showcase - Light";
        private const string RcsShowcaseRecordingPrefix = "Part Showcase - RCS";
        private const float LightsShowcaseVisualYOffset = 2f;
        /// <summary>The TryBuildHeatInfo source whose clip stock plays through ModuleAnimationSetter.</summary>
        internal const string HeatSourceAnimateHeat = "ModuleAnimateHeat";
        private const float LightMinimumIntensity = 1f;
        private const float LightMinimumRange = 8f;
        private const float LightShowcaseIntensityScale = 2f;
        private const float LightShowcaseRangeScale = 2f;
        private const float LightShowcaseMinimumIntensity = 2f;
        private const float LightShowcaseMinimumRange = 25f;
        private const float RcsShowcaseEmissionScale = 1f;
        private const float RcsShowcaseSpeedScale = 1f;
        internal const float GhostAudioSpatialBlend = 0.75f;
        internal const string GhostVisualsRootName = "ghost_visuals";
        private static readonly Color HeatTintColor = new Color(1f, 0.45f, 0.2f, 1f);
        private static readonly Color HeatEmissionColor = new Color(1.5f, 0.6f, 0.15f, 1f);

        // Reentry FX: the scalar and its constants are stock's AerodynamicsFX (GhostAeroFx,
        // AeroFxConstants); these are the ghost-side glow ramp and the drive smoothing.
        internal static readonly Color ReentryHotEmissionLow = new Color(1.5f, 0.6f, 0.15f, 1f);
        internal static readonly Color ReentryHotEmissionHigh = new Color(2.0f, 1.5f, 0.8f, 1f);
        internal const float ReentrySmoothingRate = 18.0f;
        internal const float ReentryLayerAThreshold = 0.02f;

        // Cache for PREFAB_PARTICLE fx_* prefabs found on PartLoader part prefabs.
        // Built once from PartLoader.LoadedPartsList (stable prefab templates).
        private static Dictionary<string, GameObject> fxPrefabCache;

        // Builtin Effects/ assets resolved for the Waterfall fallback; kept separate from
        // fxPrefabCache so stock-path FindFxPrefab behavior never depends on gated calls.
        private static readonly Dictionary<string, GameObject> builtinFxPrefabCache =
            new Dictionary<string, GameObject>(System.StringComparer.OrdinalIgnoreCase);
        private static bool fxLoadedObjectScanCompleted;
        private static readonly Dictionary<string, string[]> fxPrefabFallbacks =
            new Dictionary<string, string[]>(System.StringComparer.OrdinalIgnoreCase)
            {
                { "fx_exhaustFlame_yellow_tiny_Z", new[] { "fx_exhaustFlame_yellow_tiny" } },
                { "fx_smokeTrail_veryLarge", new[] { "fx_smokeTrail_large", "fx_smokeTrail_light" } },
                // Parts whose patches name this trail. The RAPIER loads its own trail exactly
                // (EngineFxBuilder.IsRapierOwnTrail) and never reaches this substitution.
                { "fx_smokeTrail_aeroSpike", new[] { "fx_smokeTrail_large", "fx_smokeTrail_light" } }
            };

        /// <summary>
        /// Groups a flat list of ColorChangerGhostInfo by partPersistentId.
        /// Used at ghost build time to populate GhostPlaybackState.colorChangerInfos.
        /// </summary>
        internal static Dictionary<uint, List<ColorChangerGhostInfo>> GroupColorChangersByPartId(
            List<ColorChangerGhostInfo> list)
        {
            var dict = new Dictionary<uint, List<ColorChangerGhostInfo>>();
            if (list == null) return dict;
            for (int i = 0; i < list.Count; i++)
            {
                uint pid = list[i].partPersistentId;
                List<ColorChangerGhostInfo> bucket;
                if (!dict.TryGetValue(pid, out bucket))
                {
                    bucket = new List<ColorChangerGhostInfo>();
                    dict[pid] = bucket;
                }
                bucket.Add(list[i]);
            }
            return dict;
        }

        /// <summary>
        /// Builds a ghost GameObject from a recording's vessel snapshot, returning
        /// all per-module-type ghost info lists bundled in a GhostBuildResult.
        /// Returns null if the snapshot is missing or produces zero visuals.
        /// </summary>
        internal static GhostBuildResult BuildTimelineGhostFromSnapshot(
            IPlaybackTrajectory rec, string rootName)
        {
            ConfigNode snapshotNode = GetGhostSnapshot(rec);
            HeaviestSpawnBuildType buildType = rec != null && rec.GhostVisualSnapshot != null
                ? HeaviestSpawnBuildType.RecordingStartSnapshot
                : HeaviestSpawnBuildType.VesselSnapshot;
            PendingGhostVisualBuild build =
                TryBeginTimelineGhostBuild(rec, snapshotNode, rootName, buildType);
            if (build == null)
                return null;

            AdvanceTimelineGhostBuild(build, long.MaxValue);
            return CompleteTimelineGhostBuild(build, rec);
        }

        internal static PendingGhostVisualBuild TryBeginTimelineGhostBuild(
            IPlaybackTrajectory rec, ConfigNode snapshotNode, string rootName, HeaviestSpawnBuildType buildType)
        {
            if (snapshotNode == null)
            {
                ParsekLog.Info("GhostVisual",
                    $"Ghost build aborted for '{rec?.VesselName ?? "unknown"}': no snapshot node");
                return null;
            }

            var partNodes = snapshotNode.GetNodes("PART");
            if (partNodes == null || partNodes.Length == 0)
            {
                ParsekLog.Info("GhostVisual",
                    $"Ghost build aborted for '{rec?.VesselName ?? "unknown"}': snapshot has no PART nodes");
                return null;
            }

            if (!SnapshotBaselineTrustedForBuildType(buildType))
            {
                ParsekLog.Verbose("GhostVisual",
                    $"Snapshot module baseline suppressed for '{rec?.VesselName ?? "unknown"}': "
                    + $"buildType={buildType.ToLogToken()} — the node is the recording's END "
                    + "state, not its start, so module state (gear / panels / bays / lamps / "
                    + "chutes / servos) is NOT read from it; geometry and part variants still "
                    + "are, exactly as before M1");
            }

            var root = new GameObject(rootName);
            root.SetActive(false);

            return new PendingGhostVisualBuild
            {
                rootName = rootName,
                root = root,
                visualsRoot = EnsureGhostVisualsRoot(root.transform),
                snapshotNode = snapshotNode,
                partNodes = partNodes,
                raiseLightVisualOnly =
                    !string.IsNullOrEmpty(rec?.VesselName)
                    && rec.VesselName.StartsWith(LightsShowcaseRecordingPrefix, System.StringComparison.Ordinal),
                raiseRcsVisualOnly =
                    !string.IsNullOrEmpty(rec?.VesselName)
                    && rec.VesselName.StartsWith(RcsShowcaseRecordingPrefix, System.StringComparison.Ordinal),
                buildType = buildType,
            };
        }

        internal static bool AdvanceTimelineGhostBuild(PendingGhostVisualBuild build, long maxTicks)
        {
            if (build == null || build.partNodes == null)
                return true;

            bool parseSnapshotBaselines = SnapshotBaselineTrustedForBuildType(build.buildType);

            long startedAt = Stopwatch.GetTimestamp();
            while (build.nextPartIndex < build.partNodes.Length)
            {
                ConfigNode partNode = build.partNodes[build.nextPartIndex++];
                // Proto snapshots use "name" for part ID in PART nodes.
                // Some synthetic builders may also emit "part"; support both.
                string rawPart = partNode.GetValue("name") ?? partNode.GetValue("part");
                string partName = TryExtractPartName(rawPart);
                if (string.IsNullOrEmpty(partName))
                {
                    build.skippedName++;
                }
                else
                {
                    // Read persistentId for ghost part naming (enables O(1) lookup during playback)
                    string pidStr = partNode.GetValue("persistentId");
                    uint persistentId = 0;
                    if (pidStr != null)
                        uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out persistentId);

                    AvailablePart ap = ResolveAvailablePart(partName);
                    if (ap == null || ap.partPrefab == null)
                    {
                        build.skippedPrefab++;
                    }
                    else
                    {
                        int meshCount = 0;
                        ParachuteGhostInfo parachuteInfo;
                        JettisonGhostInfo jettisonInfo;
                        List<EngineGhostInfo> partEngineInfos;
                        DeployableGhostInfo deployableInfo;
                        WheelDamageGhostInfo wheelDamageInfo;
                        HeatGhostInfo heatInfo;
                        LightGhostInfo lightInfo;
                        FairingGhostInfo fairingInfo;
                        List<RcsGhostInfo> partRcsInfos;
                        List<RoboticGhostInfo> partRoboticInfos;
                        SynthesizedMotionGhostInfos partSynthesizedMotionInfos;
                        List<ColorChangerGhostInfo> partColorChangerInfos;
                        CompoundPartGhostInfo compoundPartInfo;
                        GhostAppliedSkin appliedSkin;
                        bool partVisualAdded = AddPartVisuals(build.visualsRoot, partNode, ap.partPrefab,
                            persistentId, partName, out meshCount, out parachuteInfo, out jettisonInfo,
                            out partEngineInfos, out deployableInfo, out wheelDamageInfo,
                            out heatInfo, out lightInfo, out fairingInfo,
                            out partRcsInfos, out partRoboticInfos, out partSynthesizedMotionInfos,
                            out partColorChangerInfos, out compoundPartInfo, out appliedSkin,
                            build.raiseLightVisualOnly, build.raiseRcsVisualOnly);
                        if (partVisualAdded)
                            build.visualCount++;
                        else
                            build.skippedMesh++;
                        build.addedAnyVisual = build.addedAnyVisual || partVisualAdded;

                        if (parachuteInfo != null)
                            build.parachuteInfos.Add(parachuteInfo);
                        if (jettisonInfo != null)
                            build.jettisonInfos.Add(jettisonInfo);
                        if (partEngineInfos != null)
                            build.engineInfos.AddRange(partEngineInfos);
                        if (deployableInfo != null)
                            build.deployableInfos.Add(deployableInfo);
                        if (wheelDamageInfo != null)
                            build.wheelDamageInfos.Add(wheelDamageInfo);
                        if (heatInfo != null)
                        {
                            StampPersistedGlowLevels(heatInfo, build.snapshotNode, parseSnapshotBaselines);
                            build.heatInfos.Add(heatInfo);
                        }
                        if (lightInfo != null)
                            build.lightInfos.Add(lightInfo);
                        if (fairingInfo != null)
                            build.fairingInfos.Add(fairingInfo);
                        if (partRcsInfos != null)
                            build.rcsInfos.AddRange(partRcsInfos);
                        if (partRoboticInfos != null)
                        {
                            build.roboticInfos.AddRange(partRoboticInfos);
                            CollectServoFollowerInputs(build, build.nextPartIndex - 1, partNode, partRoboticInfos);
                        }
                        if (partSynthesizedMotionInfos != null)
                            MergeSynthesizedMotionInfos(build.synthesizedMotionInfos, partSynthesizedMotionInfos);
                        if (partColorChangerInfos != null)
                            build.colorChangerInfos.AddRange(partColorChangerInfos);
                        if (compoundPartInfo != null)
                            build.compoundPartInfos.Add(compoundPartInfo);
                        // Read-only record of the skin the visuals were built with (the part
                        // parity sweep's skin proof); nothing in the build or playback reads it.
                        if (appliedSkin != null)
                            build.appliedSkins[persistentId] = appliedSkin;

                        // Build audio sources for engines and RCS on this part
                        var partAudioInfos = TryBuildAudioFX(ap.partPrefab, persistentId, partName, build.root);
                        if (partAudioInfos != null)
                            build.audioInfos.AddRange(partAudioInfos);

                        // M1: read the part's persisted module state so the ghost spawns
                        // looking the way the craft actually looked, instead of at the
                        // prefab/all-stowed pose. Prefab-sourced cargo-bay pairing is
                        // resolved here because the animTime -> open/closed mapping is
                        // per-part config, not snapshot data. pid 0 is skipped: the
                        // baseline dictionary is pid-keyed and every applier looks up by
                        // pid, so a pid-less part could only collide. buildType gates the
                        // whole read: on the VesselSnapshot fallback the node is the
                        // END-of-recording state, which is the wrong end of the recording
                        // to spawn from (see SnapshotBaselineTrustedForBuildType).
                        if (persistentId != 0 && parseSnapshotBaselines)
                        {
                            ResolvePrefabCargoBayPairing(
                                ap.partPrefab,
                                out float? cargoClosedPosition,
                                out int cargoDeployModuleIndex,
                                out bool cargoAllowDeployLimit,
                                out bool cargoRevClampPercent);
                            SnapshotPartBaseline baseline = TryParseSnapshotPartBaseline(
                                partNode, cargoClosedPosition, cargoDeployModuleIndex,
                                cargoAllowDeployLimit, cargoRevClampPercent);
                            if (baseline != null)
                                build.snapshotBaselines[persistentId] = baseline;
                        }
                    }
                }

                if (build.nextPartIndex < build.partNodes.Length
                    && maxTicks != long.MaxValue
                    && Stopwatch.GetTimestamp() - startedAt >= maxTicks)
                {
                    // `maxTicks == 0` is the intentional one-part-yield test seam: the
                    // budget check runs AFTER each processed part, so zero still makes
                    // forward progress once, then returns Pending when work remains.
                    return false;
                }
            }

            return true;
        }

        /// <summary>S3: folds one part's discovered synthesis families into the whole-ghost container.</summary>
        private static void MergeSynthesizedMotionInfos(
            SynthesizedMotionGhostInfos sink, SynthesizedMotionGhostInfos part)
        {
            if (sink == null || part == null) return;
            if (part.gimbals != null)
            {
                if (sink.gimbals == null) sink.gimbals = new List<GimbalGhostInfo>();
                sink.gimbals.AddRange(part.gimbals);
            }
            if (part.controlSurfaces != null)
            {
                if (sink.controlSurfaces == null) sink.controlSurfaces = new List<ControlSurfaceGhostInfo>();
                sink.controlSurfaces.AddRange(part.controlSurfaces);
            }
            if (part.sunTrackers != null)
            {
                if (sink.sunTrackers == null) sink.sunTrackers = new List<SunTrackingGhostInfo>();
                sink.sunTrackers.AddRange(part.sunTrackers);
            }
            if (part.converterLoops != null)
            {
                if (sink.converterLoops == null) sink.converterLoops = new List<ConverterLoopGhostInfo>();
                sink.converterLoops.AddRange(part.converterLoops);
            }
        }

        internal static GhostBuildResult CompleteTimelineGhostBuild(
            PendingGhostVisualBuild build, IPlaybackTrajectory rec)
        {
            if (build == null)
                return null;

            ParsekLog.VerboseRateLimited("GhostVisual", $"ghost_built_{build.rootName}",
                $"Ghost built: {build.visualCount}/{build.partNodes.Length} parts with visuals" +
                (build.skippedName > 0 ? $", {build.skippedName} bad name" : "") +
                (build.skippedPrefab > 0 ? $", {build.skippedPrefab} no prefab" : "") +
                (build.skippedMesh > 0 ? $", {build.skippedMesh} no mesh" : ""), 60.0);

            if (!build.addedAnyVisual)
            {
                DestroyPendingTimelineGhostBuild(build);
                ParsekLog.Warn("GhostVisual",
                    $"Ghost build produced zero visuals for '{rec?.VesselName ?? "unknown"}' (parts={build.partNodes.Length})");
                return null;
            }

            return new GhostBuildResult
            {
                root = build.root,
                parachuteInfos = build.parachuteInfos.Count > 0 ? build.parachuteInfos : null,
                jettisonInfos = build.jettisonInfos.Count > 0 ? build.jettisonInfos : null,
                engineInfos = build.engineInfos.Count > 0 ? build.engineInfos : null,
                deployableInfos = build.deployableInfos.Count > 0 ? build.deployableInfos : null,
                wheelDamageInfos = build.wheelDamageInfos.Count > 0 ? build.wheelDamageInfos : null,
                heatInfos = build.heatInfos.Count > 0 ? build.heatInfos : null,
                lightInfos = build.lightInfos.Count > 0 ? build.lightInfos : null,
                fairingInfos = build.fairingInfos.Count > 0 ? build.fairingInfos : null,
                rcsInfos = build.rcsInfos.Count > 0 ? build.rcsInfos : null,
                roboticInfos = build.roboticInfos.Count > 0 ? build.roboticInfos : null,
                synthesizedMotionInfos =
                    build.synthesizedMotionInfos != null && !build.synthesizedMotionInfos.IsEmpty
                        ? build.synthesizedMotionInfos : null,
                colorChangerInfos = build.colorChangerInfos.Count > 0 ? build.colorChangerInfos : null,
                compoundPartInfos = build.compoundPartInfos.Count > 0 ? build.compoundPartInfos : null,
                audioInfos = build.audioInfos.Count > 0 ? build.audioInfos : null,
                snapshotBaselines =
                    build.snapshotBaselines.Count > 0 ? build.snapshotBaselines : null,
                servoFollowerPlan = ResolveServoFollowerPlan(build, rec),
                appliedSkins = build.appliedSkins.Count > 0 ? build.appliedSkins : null,
            };
        }

        /// <summary>
        /// Reads the prefab's cargo-bay pairing for the M1 baseline: which end of the
        /// paired animation counts as "shut" (<c>closedPosition</c>) and which module
        /// index carries that animation (<c>DeployModuleIndex</c>). Both are prefab
        /// config, not snapshot data, which is why the pure parser takes them as inputs.
        /// No cargo bay → no closedPosition, which is also the signal that routes the
        /// part's ModuleAnimateGeneric to the standalone family. The paired animation's
        /// config-only deploy-limit flags ride along (see
        /// <see cref="ResolvePrefabCargoAnimateGeneric"/>).
        /// </summary>
        private static void ResolvePrefabCargoBayPairing(
            Part prefab, out float? closedPosition, out int deployModuleIndex,
            out bool allowDeployLimit, out bool revClampPercent)
        {
            closedPosition = null;
            deployModuleIndex = -1;
            allowDeployLimit = false;
            revClampPercent = false;
            if (prefab == null)
                return;

            try
            {
                ModuleCargoBay cargo = prefab.FindModuleImplementing<ModuleCargoBay>();
                if (cargo == null)
                    return;
                closedPosition = cargo.closedPosition;
                deployModuleIndex = cargo.DeployModuleIndex;
                ModuleAnimateGeneric anim = ResolvePrefabCargoAnimateGeneric(prefab, cargo);
                if (anim != null)
                {
                    allowDeployLimit = anim.allowDeployLimit;
                    revClampPercent = anim.revClampPercent;
                }
            }
            catch (System.Exception ex)
            {
                ParsekLog.VerboseRateLimited("GhostVisual", "cargo-pairing-probe",
                    $"ModuleCargoBay pairing probe threw {ex.GetType().Name} on prefab " +
                    $"'{prefab.partInfo?.name ?? "unknown"}': {ex.Message}; " +
                    $"cargo-bay baseline skipped for this part", 30.0);
                closedPosition = null;
                deployModuleIndex = -1;
                allowDeployLimit = false;
                revClampPercent = false;
            }
        }

        /// <summary>
        /// The prefab's cargo-bay animation module: the one at <c>DeployModuleIndex</c>, or,
        /// when that index does not resolve (KSP inserting internal modules shifts indices),
        /// the first <c>ModuleAnimateGeneric</c> on the part. One resolver for the baseline
        /// pairing and the ghost's pose sampling.
        /// </summary>
        private static ModuleAnimateGeneric ResolvePrefabCargoAnimateGeneric(
            Part prefab, ModuleCargoBay cargoBay)
        {
            if (prefab == null || cargoBay == null)
                return null;
            int deployIdx = cargoBay.DeployModuleIndex;
            ModuleAnimateGeneric animModule = (deployIdx >= 0 && deployIdx < prefab.Modules.Count)
                ? prefab.Modules[deployIdx] as ModuleAnimateGeneric
                : null;
            return animModule ?? prefab.FindModuleImplementing<ModuleAnimateGeneric>();
        }

        internal static void DestroyPendingTimelineGhostBuild(PendingGhostVisualBuild build)
        {
            if (build?.root != null)
                Object.Destroy(build.root);
        }

        /// <summary>
        /// Resolves the node a ghost is BUILT from. The <c>?? rec.VesselSnapshot</c> fallback
        /// is the END-of-recording snapshot (<c>BuildCaptureRecording</c> captures it at
        /// stop), which is fine for GEOMETRY — the part list and variants are what the build
        /// needs and they barely differ — but is the wrong end of the recording for MODULE
        /// STATE. See <see cref="SnapshotBaselineTrustedForBuildType"/>.
        /// </summary>
        internal static ConfigNode GetGhostSnapshot(IPlaybackTrajectory rec)
        {
            if (rec == null) return null;
            return rec.GhostVisualSnapshot ?? rec.VesselSnapshot;
        }

        /// <summary>
        /// Whether the M1 module-state baseline may be read out of the node this build is
        /// using. TRUE only for <see cref="HeaviestSpawnBuildType.RecordingStartSnapshot"/>,
        /// i.e. the recording's own <c>GhostVisualSnapshot</c>.
        ///
        /// <see cref="HeaviestSpawnBuildType.VesselSnapshot"/> means
        /// <see cref="GetGhostSnapshot"/> fell back to the END-of-recording snapshot, and
        /// spawning a ghost at its own end state is a new way to be wrong: a chute whose end
        /// state is CUT would hide its canopy through the entire descent, gear that came down
        /// on final approach would be down from the pad. Pre-M1 the fallback was harmless
        /// because nothing read module state out of it; suppressing the read restores exactly
        /// that. Geometry and part variants keep using the node either way.
        ///
        /// RESIDUAL, stated rather than papered over. This gate only catches the node-level
        /// fallback. Several sites STAMP <c>GhostVisualSnapshot = VesselSnapshot.CreateCopy()</c>
        /// and the copy is indistinguishable from a genuine start snapshot at build time:
        /// <c>FlightRecorder.BuildCaptureRecording</c> when the start capture was null,
        /// <c>RecordingOptimizer.SplitAtSection</c> step 8's #271 safety net,
        /// <c>MergeDialog.Commit</c>, <c>BackgroundRecorder</c>'s child promotion and
        /// <c>ParsekFlight.Finalization</c>. Those recordings still read module state out of
        /// an end-state node. Closing that needs provenance carried on the recording (a
        /// schema field) or stamped into the node at copy time, decided per site — not a
        /// build-time inference, because by then the information is gone.
        /// </summary>
        internal static bool SnapshotBaselineTrustedForBuildType(HeaviestSpawnBuildType buildType)
        {
            return buildType == HeaviestSpawnBuildType.RecordingStartSnapshot;
        }

        internal static Transform EnsureGhostVisualsRoot(Transform ghostRoot)
        {
            if (ghostRoot == null)
                return null;

            Transform visualsRoot = ghostRoot.Find(GhostVisualsRootName);
            if (visualsRoot != null)
                return visualsRoot;

            var visualsRootObj = new GameObject(GhostVisualsRootName);
            visualsRootObj.transform.SetParent(ghostRoot, false);
            return visualsRootObj.transform;
        }

        internal static Transform GetGhostPartContainer(Transform ghostRoot)
        {
            if (ghostRoot == null)
                return null;

            Transform visualsRoot = ghostRoot.Find(GhostVisualsRootName);
            return visualsRoot ?? ghostRoot;
        }

        internal static Transform FindGhostPartTransform(GameObject ghostRoot, uint partPersistentId)
        {
            if (ghostRoot == null)
                return null;

            Transform partContainer = GetGhostPartContainer(ghostRoot.transform);
            if (partContainer == null)
                return null;

            return partContainer.Find($"ghost_part_{partPersistentId}");
        }

        /// <summary>
        /// Find the visual model root transform for a part prefab.
        /// KSP stores visual content under a "model" child. KerbalEVA uses "model01".
        /// Falls back to the prefab root if neither exists.
        /// </summary>
        private static Transform FindModelRoot(Part prefab)
        {
            // KerbalEVA has body mesh under "model01"; "model" only has accessories
            // (parachute, backpack). Check model01 first to get the actual kerbal.
            Transform modelRoot = prefab.transform.Find("model01");
            if (modelRoot != null) return modelRoot;
            modelRoot = prefab.transform.Find("model");
            if (modelRoot != null) return modelRoot;
            return prefab.transform;
        }

        /// <summary>
        /// Walk from descendant up to find the immediate child of ancestor.
        /// Returns null if descendant is not under ancestor.
        /// </summary>
        private static Transform FindImmediateChildOf(Transform descendant, Transform ancestor)
        {
            Transform cur = descendant;
            while (cur != null && cur.parent != ancestor)
                cur = cur.parent;
            return (cur != null && cur.parent == ancestor) ? cur : null;
        }

        /// <summary>
        /// Mirror the transform hierarchy from modelRoot down to a renderer's transform,
        /// creating intermediate GameObjects under partRoot with matching local transforms.
        /// Uses cloneMap to deduplicate shared parent nodes.
        /// Returns the cloned leaf transform.
        /// </summary>
        private static Transform MirrorTransformChain(Transform rendererTransform, Transform modelRoot,
            Transform partRootTransform, Dictionary<Transform, Transform> cloneMap)
        {
            // Build path from renderer up to modelRoot
            var chain = new List<Transform>();
            Transform cur = rendererTransform;
            while (cur != null && cur != modelRoot)
            {
                chain.Add(cur);
                cur = cur.parent;
            }
            chain.Reverse(); // root-first order

            Transform parent = partRootTransform;
            for (int i = 0; i < chain.Count; i++)
            {
                Transform src = chain[i];
                if (cloneMap.TryGetValue(src, out Transform existing))
                {
                    parent = existing;
                    continue;
                }
                GameObject clone = new GameObject(src.gameObject.name);
                clone.transform.SetParent(parent, false);
                clone.transform.localPosition = src.localPosition;
                clone.transform.localRotation = src.localRotation;
                clone.transform.localScale = src.localScale;
                cloneMap[src] = clone.transform;
                parent = clone.transform;
            }

            return parent;
        }

        /// <summary>
        /// Configure KSP FX controller components on cloned particle system GameObjects.
        /// KSPParticleEmitter is KEPT alive (it handles material setup and particle creation)
        /// but set to emit=false initially. It is collected into kspEmitterSink for later
        /// control via reflection in SetEngineEmission/SetRcsEmission.
        /// Unity's emission module is disabled separately (in ConfigureGhostEngineParticleSystems)
        /// to prevent Unity from creating its own material-less "bubble" particles.
        /// SmokeTrailControl is STRIPPED — tested keeping alive (audit #113) but it sets material
        /// alpha to 0 on ghosts, making smoke invisible. Needs vessel context to work correctly.
        /// ModelMultiParticlePersistFX/ModelParticleFX are EffectBehaviour subclasses that reference
        /// Host (the Part). KEPT ALIVE — stripping kills smoke trails. Any NREs are non-fatal.
        /// FXPrefab (stock's FloatingOrigin registration) is stripped: GhostWorldSpaceEmitter moves
        /// every world-space system of the clone when the floating origin shifts.
        ///
        /// Audit #113: FXModuleAnimateThrottle and FXModuleAnimateRCS are PartModules (not
        /// MonoBehaviours) requiring Part/Vessel context. They are reimplemented via HeatGhostInfo
        /// animation sampling, which is architecturally correct: one-shot build cost cached per
        /// part type, near-zero runtime cost (3-level quantized transform/material snaps on event
        /// boundaries), correct multi-instance disambiguation. Native modules are infeasible on
        /// ghosts and the reimplementation is intentionally minimal per the visual efficiency
        /// design principle.
        /// </summary>
        // ReStock's SRB smoke models (restock-fx-srb-smoke-1/2/3) are world-space TRAIL assets
        // with a baked velocity of 0 to 1 m/s: the column comes from the vessel flying away from
        // it. A real booster that burns standing still gathers its smoke in a ball at the nozzle
        // (1.0 m mean, measured), and a standing showcase ghost would too. Owner ruling 2026-11-24:
        // the showcase keeps its flowing look, so a standing ghost's smoke leaves at
        // WorldSpaceEmitterFloorSpeed, and a moving one's is not touched
        // (GhostWorldSpaceEmitter.StandingFlow counts the ghost's own speed). Do not remove it, and
        // do not let it reach a launched flight, without asking.
        internal const float WorldSpaceEmitterSlowSpeedThreshold = 4f;
        internal const float WorldSpaceEmitterFloorSpeed = GhostWorldSpaceEmitter.StandingFlowSpeed;

        // #383: Ghost engine flames render undersized vs live stock because Parsek's FX
        // pipeline never applies stock's per-frame FXGroup.SetPower runPower multiplier
        // (up to 1.75x at full thrust for legacy engines). Instead of replicating per-frame
        // modulation — which needs per-layer curves and risks destroying prefab curve modes
        // when rewriting startSize — apply a fixed build-time size bump that preserves curve
        // modes via startSizeMultiplier. Chosen to sit near stock's full-thrust appearance
        // without over-sizing partial-throttle plumes. Caller-supplied so RAPIER white flame
        // (which already has its own 0.45x shrink) composes naturally.
        internal const float GhostEngineFxSizeBoost = 1.5f;

        // Per-part overrides relative to the 1.5x default boost. Values are absolute
        // startSizeMultiplier/startLifetimeMultiplier factors vs the prefab:
        //   0.6x — tiny engines (Twitch/Spider/Ant/Sepratron); prefab particles are
        //          already in scale with the tiny part, default boost over-sizes them.
        //   1.0x — Flea/Hammer SRBs; default boost reads ~30% too wide, pin to prefab.
        //   2.0x — Skipper/Mainsail; default boost reads ~30% too narrow.
        // Keys are runtime part names (KSP converts underscores to dots at runtime:
        // `smallRadialEngine_v2` registers as `smallRadialEngine.v2`).
        private static readonly Dictionary<string, float> engineFxSizeOverride =
            new Dictionary<string, float>(System.StringComparer.Ordinal)
            {
                { "smallRadialEngine",      0.6f  }, // 24-77 Twitch
                { "smallRadialEngine.v2",   0.6f  }, // 24-77 Twitch v2
                { "radialEngineMini.v2",    0.6f  }, // LV-1R Spider
                { "microEngine.v2",         0.6f  }, // LV-1 Ant
                { "sepMotor1",              0.6f  }, // Sepratron I
                { "solidBooster.sm.v2",     1.0f  }, // RT-5 "Flea" SRB
                { "solidBooster.v2",        1.0f  }, // RT-10 "Hammer" SRB
                { "engineLargeSkipper.v2",  2.0f  }, // T-1 Toroidal "Skipper"
                { "liquidEngineMainsail.v2",2.0f  }, // RE-M3 "Mainsail"
            };

        // Cache: partName -> semi-deployed + deployed canopy states (root and animated bones);
        // sampled once per part type, reused across ghosts.
        private static readonly Dictionary<string, CanopySample> canopyCache =
            new Dictionary<string, CanopySample>();

        // Unified cache for all animation-sampled transform states (deployable, gear, ladder, cargo bay).
        // Key collision is impossible: Deployable/Gear/CargoBay use plain part names but a part enters
        // exactly one code path (priority cascade in TryBuildDeployableInfo). Ladder uses compound keys
        // with '|' separators that never match plain part names.
        private static readonly Dictionary<string, List<(string path, Vector3 sPos, Quaternion sRot, Vector3 sScale,
            Vector3 dPos, Quaternion dRot, Vector3 dScale)>> animationSampleCache =
            new Dictionary<string, List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)>>();

        /// <summary>
        /// S7: how many poses around a running loop get sampled. Twelve is a deliberate middle:
        /// enough that a rotating drill head reads as rotating rather than as three jerks, few
        /// enough that the storage is trivial (12 poses x a handful of transforms per part, built
        /// once per part TYPE and cached) and the per-frame cost stays one lerp+slerp per transform.
        /// </summary>
        internal const int ConverterLoopPhaseCount = 12;

        // S7: partKey|animName -> the sampled loop phases, and the running clip's own length.
        // Keyed by BOTH because the running clip is a different clip from the deploy clip on the
        // same part, and animationClipLengthCache is per-part last-write-wins — writing the running
        // length into that cache would corrupt the DEPLOY duration the S2 interpolation reads.
        private static readonly Dictionary<string, (List<(string path, ConverterLoopPose[] phases)> samples,
            float clipSeconds)> converterLoopSampleCache =
            new Dictionary<string, (List<(string, ConverterLoopPose[])>, float)>();

        // Cache: partName(+anim) -> sampled 3-state ModuleAnimateHeat transform states
        private static readonly Dictionary<string, List<(string path,
            Vector3 coldPos, Quaternion coldRot, Vector3 coldScale,
            Vector3 medPos, Quaternion medRot, Vector3 medScale,
            Vector3 hotPos, Quaternion hotRot, Vector3 hotScale)>> animateHeatCache =
            new Dictionary<string, List<(string, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3, Vector3, Quaternion, Vector3)>>();

        // S2: partKey -> the prefab deployable clip's own length in seconds, already clamped.
        // Populated by SampleAnimationStates; read by the deployable-info builders.
        private static readonly Dictionary<string, float> animationClipLengthCache =
            new Dictionary<string, float>();

        // KSP model transforms can have names containing '/' (e.g.
        // "Squad/Parts/Thermal/FoldingRadiators/foldingRadSmall(Clone)").
        // Use \x01 as path separator so Split() doesn't break those names.
        private const char PathSep = '\x01';

        /// <summary>
        /// Find a transform by PathSep-separated path relative to a root.
        /// Each segment is a direct child name (may contain '/' in KSP models).
        /// Uses manual child iteration instead of Transform.Find() because
        /// Unity's Find() also treats '/' as a path separator.
        /// </summary>
        private static Transform FindTransformByPath(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            string[] parts = path.Split(PathSep);
            Transform cur = root;
            for (int i = 0; i < parts.Length; i++)
            {
                Transform found = null;
                for (int c = 0; c < cur.childCount; c++)
                {
                    if (cur.GetChild(c).name == parts[i])
                    {
                        found = cur.GetChild(c);
                        break;
                    }
                }
                if (found == null) return null;
                cur = found;
            }
            return cur;
        }

        internal static Transform FindTransformRecursive(Transform parent, string name)
        {
            if (parent.name == name) return parent;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform found = FindTransformRecursive(parent.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// How many transforms one hierarchy dump may SPELL OUT before the rest is summarised as a count. Walking
        /// past it still COUNTS (so the total stays honest), it just stops appending. A stock engine prefab runs
        /// tens of nodes, so this only ever bites a pathological modded part - where the alternative is a
        /// tens-of-KB single line that some downstream reader truncates SILENTLY, losing the tail with no marker.
        /// Truncating here is loud: the line says how many were dropped.
        /// </summary>
        private const int HierarchyDumpMaxNodes = 500;


        #region Ghost Audio FX

        // Per-ghost dedupe set for "AudioClip not found" warnings. Keyed by ghost root
        // GameObject name; each entry is a set of "pid|clipPath" strings that have
        // already warned for that ghost. Cleared by ClearMissingAudioClipWarnings when
        // the ghost is destroyed so a fresh spawn can warn again if the clip is still
        // missing. Fixes bug #421: before dedupe, a ghost with a missing ion-engine
        // clip emitted one warn per loop rebuild (~7 per 3.5 min).
        private static readonly Dictionary<string, HashSet<string>> missingAudioClipWarnedByGhost =
            new Dictionary<string, HashSet<string>>(System.StringComparer.Ordinal);

        #endregion

        internal static List<Transform> FindTransformsRecursive(Transform parent, string name)
        {
            var results = new List<Transform>();
            FindTransformsRecursiveHelper(parent, name, results);
            return results;
        }

        private static void FindTransformsRecursiveHelper(Transform parent, string name, List<Transform> results)
        {
            if (parent.name == name) results.Add(parent);
            for (int i = 0; i < parent.childCount; i++)
                FindTransformsRecursiveHelper(parent.GetChild(i), name, results);
        }

        private static bool IsDescendantOf(Transform child, Transform ancestor)
        {
            Transform cur = child;
            while (cur != null)
            {
                if (cur == ancestor) return true;
                cur = cur.parent;
            }
            return false;
        }

        internal const string StockLinkedMeshModuleName = "CModuleLinkedMesh";
        internal const string ReStockLinkedMeshModuleName = "ModuleRestockLinkedMesh";

        /// <summary>
        /// The part config's linked-mesh MODULE node: the one named
        /// <paramref name="linkedMeshModuleName"/> (the prefab module implementing
        /// CModuleLinkedMesh, e.g. ReStock's ModuleRestockLinkedMesh subclass), else null.
        /// </summary>
        private static ConfigNode FindLinkedMeshModuleNode(ConfigNode partConfig, string linkedMeshModuleName)
        {
            if (partConfig == null || string.IsNullOrEmpty(linkedMeshModuleName)) return null;
            ConfigNode[] modules = partConfig.GetNodes("MODULE");
            for (int m = 0; m < modules.Length; m++)
            {
                if (modules[m].GetValue("name") == linkedMeshModuleName)
                    return modules[m];
            }
            return null;
        }

        /// <summary>
        /// ReStock's <c>stretchAxis</c> ("y" = 1, anything else = 0, as its OnStart reads it)
        /// and <c>stretchTextures</c> (space-separated, default <c>_MainTex</c>).
        /// </summary>
        internal static void ParseReStockStretch(ConfigNode moduleNode, out string[] textures, out int axisIndex)
        {
            string axis = moduleNode?.GetValue("stretchAxis") ?? "x";
            axisIndex = axis != "y" ? 0 : 1;
            string list = moduleNode?.GetValue("stretchTextures") ?? "_MainTex";
            textures = list.Split(' ');
        }

        /// <summary>
        /// ReStock <c>UpdateStretch</c>: stretch = line z scale / part scaleFactor; the texture
        /// scale on the stretched axis is the stretch and its offset (1 - stretch) / 2, the
        /// other axis keeps scale 1 and offset 0.
        /// </summary>
        internal static void ComputeReStockTextureStretch(float lineLocalScaleZ, float scaleFactor,
            int axisIndex, out Vector2 scale, out Vector2 offset)
        {
            float stretch = scaleFactor != 0f ? lineLocalScaleZ / scaleFactor : lineLocalScaleZ;
            scale = Vector2.one;
            offset = Vector2.zero;
            if (axisIndex == 1)
            {
                scale.y = stretch;
                offset.y = (1f - stretch) / 2f;
            }
            else
            {
                scale.x = stretch;
                offset.x = (1f - stretch) / 2f;
            }
        }

        /// <summary>
        /// Parses compound part target data from a snapshot PART node.
        /// Returns false (no-op) for non-compound parts or corrupt data.
        /// Pure method — no Unity Transform access, directly testable.
        /// </summary>
        internal static bool TryParseCompoundPartData(
            ConfigNode partNode, ConfigNode partConfig, bool isCompoundPart,
            uint persistentId, string partName,
            out CompoundPartData data)
            => TryParseCompoundPartData(partNode, partConfig, isCompoundPart, persistentId, partName,
                StockLinkedMeshModuleName, out data);

        /// <summary>
        /// <paramref name="linkedMeshModuleName"/> is the moduleName of the prefab module that
        /// implements CModuleLinkedMesh (stock's own, or a subclass such as ReStock's).
        /// </summary>
        internal static bool TryParseCompoundPartData(
            ConfigNode partNode, ConfigNode partConfig, bool isCompoundPart,
            uint persistentId, string partName, string linkedMeshModuleName,
            out CompoundPartData data)
        {
            data = new CompoundPartData
            {
                lineObjName = "obj_line",
                targetAnchorName = "obj_targetAnchor",
                targetCapName = "obj_targetCap"
            };

            // Only process parts that have CModuleLinkedMesh — avoids false positives
            // from non-compound parts that happen to have a PARTDATA node.
            ConfigNode linkedMeshModule = FindLinkedMeshModuleNode(partConfig, linkedMeshModuleName);
            if (linkedMeshModule == null)
                return false;

            ConfigNode partData = partNode.GetNode("PARTDATA");
            if (partData == null)
            {
                // Expected no-op for non-compound parts. Log if the prefab IS a compound
                // part — that would indicate corrupt/missing snapshot data.
                if (isCompoundPart)
                {
                }
                return false;
            }

            if (!TryParseVector3(partData.GetValue("pos"), out data.targetPos))
            {
                return false;
            }

            if (!TryParseQuaternion(partData.GetValue("rot"), out data.targetRot))
            {
                data.targetRot = Quaternion.identity;
            }

            string targetPidStr = partData.GetValue("tpersID");
            if (targetPidStr != null)
                uint.TryParse(targetPidStr, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out data.targetPersistentId);

            // Read transform names from the linked-mesh module config (varies: fuel line
            // uses obj_line, strut uses obj_strut, ReStock line / strut).
            data.lineObjName = linkedMeshModule.GetValue("lineObjName") ?? data.lineObjName;
            data.targetAnchorName = linkedMeshModule.GetValue("targetAnchorName") ?? data.targetAnchorName;
            data.targetCapName = linkedMeshModule.GetValue("targetCapName") ?? data.targetCapName;
            if (linkedMeshModuleName == ReStockLinkedMeshModuleName)
                ParseReStockStretch(linkedMeshModule, out data.stretchTextures, out data.stretchAxisIndex);

            return true;
        }

        /// <summary>
        /// Pure: a compound part whose persisted target pid is 0 has no target; stock
        /// <c>CompoundPart.OnSave</c> always writes <c>tpersID</c>, 0 when unconnected.
        /// </summary>
        internal static bool IsUnconnectedLinkedMesh(uint targetPersistentId)
            => targetPersistentId == 0;

        /// <summary>
        /// Fixes up compound part visuals (fuel lines, struts) by repositioning the
        /// target anchor and stretching the line mesh using PARTDATA from the snapshot.
        /// In KSP, CModuleLinkedMesh handles this at runtime; ghosts need manual fixup.
        /// </summary>
        private static CompoundPartGhostInfo TryFixupCompoundPartVisuals(
            Part prefab, ConfigNode partNode, Transform partRoot,
            Dictionary<Transform, Transform> cloneMap,
            uint persistentId, string partName)
        {
            // The module is found by TYPE: ReStock replaces CModuleLinkedMesh with its
            // ModuleRestockLinkedMesh subclass, whose config node carries that name.
            CompoundParts.CModuleLinkedMesh linkedMesh =
                prefab.FindModuleImplementing<CompoundParts.CModuleLinkedMesh>();
            string linkedMeshModuleName = linkedMesh != null ? linkedMesh.moduleName : StockLinkedMeshModuleName;
            CompoundPartData data;
            if (!TryParseCompoundPartData(partNode, prefab.partInfo?.partConfig,
                    prefab is CompoundPart, persistentId, partName, linkedMeshModuleName, out data))
                return null;

            var compoundPartInfo = new CompoundPartGhostInfo
            {
                partPersistentId = persistentId,
                targetPersistentId = data.targetPersistentId,
                partTransform = partRoot
            };

            // Find source transforms on prefab, then look up ghost clones in cloneMap
            Transform srcLine = prefab.FindModelTransform(data.lineObjName);
            Transform srcTargetAnchor = prefab.FindModelTransform(data.targetAnchorName);
            Transform srcTargetCap = prefab.FindModelTransform(data.targetCapName);

            Transform ghostLine = null, ghostTargetAnchor = null, ghostTargetCap = null;
            if (srcLine != null) cloneMap.TryGetValue(srcLine, out ghostLine);
            if (srcTargetAnchor != null) cloneMap.TryGetValue(srcTargetAnchor, out ghostTargetAnchor);
            if (srcTargetCap != null) cloneMap.TryGetValue(srcTargetCap, out ghostTargetCap);

            if (IsUnconnectedLinkedMesh(data.targetPersistentId))
            {
                // Stock CModuleLinkedMesh.OnStart hides the line and the target anchor (the end
                // cap rides on it) and only OnTargetSet shows them; an unconnected strut or fuel
                // line (PARTDATA tpersID = 0) never gets a target.
                if (ghostLine != null) ghostLine.gameObject.SetActive(false);
                if (ghostTargetAnchor != null) ghostTargetAnchor.gameObject.SetActive(false);
                ParsekLog.VerboseRateLimited("GhostVisual", $"compound-unconnected-{partName}",
                    $"  Compound part '{partName}' pid={persistentId}: no target (tpersID=0); " +
                    $"line '{data.lineObjName}' found={ghostLine != null} and target anchor " +
                    $"'{data.targetAnchorName}' found={ghostTargetAnchor != null} hidden as stock does", 60.0);
                return compoundPartInfo;
            }

            if (ghostLine == null)
            {
                return compoundPartInfo;
            }

            // PARTDATA.pos is in compound part local space (= partRoot space).
            // Move target anchor to the correct position — endCap is a child, moves with it.
            if (ghostTargetAnchor != null)
            {
                ghostTargetAnchor.position = partRoot.TransformPoint(data.targetPos);
                ghostTargetAnchor.rotation = partRoot.rotation * data.targetRot;
            }

            // Stretch line mesh from its position to the target end.
            // Mirrors CModuleLinkedMesh.TrackAnchor: LookRotation + Z-scale.
            Vector3 lineWorldPos = ghostLine.position;
            Vector3 targetWorldPos;
            if (ghostTargetCap != null)
                targetWorldPos = ghostTargetCap.position;
            else if (ghostTargetAnchor != null)
                targetWorldPos = ghostTargetAnchor.position;
            else
                targetWorldPos = partRoot.TransformPoint(data.targetPos);

            // NOTE: delta points from target toward source (line - target), matching
            // CModuleLinkedMesh.TrackAnchor which computes (line.position - endCap.position).
            // The stock line mesh faces -Z in the prefab, so LookRotation along this reversed
            // vector produces the correct orientation. Do not "fix" by swapping operands.
            Vector3 delta = lineWorldPos - targetWorldPos;
            float distance = delta.magnitude;
            const float lineMinimumLength = 0.01f;

            if (distance > lineMinimumLength)
            {
                ghostLine.rotation = Quaternion.LookRotation(delta.normalized, partRoot.forward);
                // prefab.scaleFactor is the stock prefab value (typically rescaleFactor, default
                // 1.25). Mods like TweakScale may change scaleFactor on the live part, but the
                // prefab retains the original value — acceptable for ghost visuals.
                ghostLine.localScale = new Vector3(
                    ghostLine.localScale.x,
                    ghostLine.localScale.y,
                    distance * prefab.scaleFactor);

                if (ghostTargetCap != null)
                    ghostTargetCap.rotation = ghostLine.rotation;

                if (data.stretchTextures != null)
                    ApplyReStockLineTextureStretch(ghostLine, data, prefab.scaleFactor, partName, persistentId);
            }
            else
            {
                ghostLine.gameObject.SetActive(false);
            }

            return compoundPartInfo;
        }

        /// <summary>
        /// ReStock <c>UpdateStretch</c> on the ghost: the first material of each line renderer
        /// (stock reads <c>Renderer.material</c>; cloned here, the prefab's is shared) gets the
        /// stretched texture scale and offset for every listed texture property it has.
        /// </summary>
        private static void ApplyReStockLineTextureStretch(Transform ghostLine, CompoundPartData data,
            float scaleFactor, string partName, uint persistentId)
        {
            ComputeReStockTextureStretch(ghostLine.localScale.z, scaleFactor, data.stretchAxisIndex,
                out Vector2 scale, out Vector2 offset);
            MeshRenderer[] renderers = ghostLine.GetComponentsInChildren<MeshRenderer>(true);
            int written = 0;
            for (int r = 0; r < renderers.Length; r++)
            {
                Material[] slots = renderers[r].sharedMaterials;
                if (slots == null || slots.Length == 0 || slots[0] == null) continue;
                var clone = new Material(slots[0]);
                bool any = false;
                for (int t = 0; t < data.stretchTextures.Length; t++)
                {
                    string property = data.stretchTextures[t];
                    if (string.IsNullOrEmpty(property) || !clone.HasProperty(property)) continue;
                    clone.SetTextureScale(property, scale);
                    clone.SetTextureOffset(property, offset);
                    any = true;
                }
                if (!any)
                {
                    Object.Destroy(clone);
                    continue;
                }
                slots[0] = clone;
                renderers[r].materials = slots;
                written++;
            }
            ParsekLog.VerboseRateLimited("GhostVisual", $"compound-stretch-{partName}",
                $"  Compound part '{partName}' pid={persistentId}: ReStock texture stretch " +
                $"scale=({scale.x.ToString("0.###", CultureInfo.InvariantCulture)}," +
                $"{scale.y.ToString("0.###", CultureInfo.InvariantCulture)}) on {written} renderer(s)", 60.0);
        }

        private static bool AddPartVisuals(Transform root, ConfigNode partNode, Part prefab,
            uint persistentId, string partName, out int meshCount,
            out ParachuteGhostInfo parachuteInfo, out JettisonGhostInfo jettisonInfo,
            out List<EngineGhostInfo> engineInfos, out DeployableGhostInfo deployableInfo,
            out WheelDamageGhostInfo wheelDamageInfo,
            out HeatGhostInfo heatInfo,
            out LightGhostInfo lightInfo, out FairingGhostInfo fairingInfo,
            out List<RcsGhostInfo> rcsInfos, out List<RoboticGhostInfo> roboticInfos,
            out SynthesizedMotionGhostInfos synthesizedMotionInfos,
            out List<ColorChangerGhostInfo> colorChangerInfos, out CompoundPartGhostInfo compoundPartInfo,
            out GhostAppliedSkin appliedSkin,
            bool raiseLightVisualOnly, bool raiseRcsVisualOnly)
        {
            meshCount = 0;
            appliedSkin = null;
            synthesizedMotionInfos = null;
            parachuteInfo = null;
            jettisonInfo = null;
            engineInfos = null;
            deployableInfo = null;
            wheelDamageInfo = null;
            heatInfo = null;
            lightInfo = null;
            fairingInfo = null;
            rcsInfos = null;
            roboticInfos = null;
            colorChangerInfos = null;
            compoundPartInfo = null;
            Transform modelRoot = FindModelRoot(prefab);

            // Dump full hierarchy for engine parts to diagnose missing nozzle/shroud meshes.
            // Search from the Part root (not just modelRoot) to catch siblings of "model".
            bool isEnginePart = prefab.FindModuleImplementing<ModuleEngines>() != null;
            bool hasJettison = prefab.FindModuleImplementing<ModuleJettison>() != null;
            if (isEnginePart)
                LogEnginePartHierarchyDump(prefab, modelRoot, partName, persistentId);

            // Parts with ModulePartVariants have multiple visual variants where only
            // one set of GameObjects should be visible (e.g. Poodle v2: DoubleBell vs
            // SingleBell). Active-state alone is unreliable on some prefabs, so we use
            // selected/default VARIANT GAMEOBJECT rules from part config when available,
            // with active-state filtering as an additional safeguard.
            bool hasPartVariants = prefab.FindModuleImplementing<ModulePartVariants>() != null;

            var meshRenderers = modelRoot.GetComponentsInChildren<MeshRenderer>(true);
            var skinnedRenderers = modelRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (HasNoRendererComponents(meshRenderers, skinnedRenderers))
                return false;

            int totalMR = meshRenderers != null ? meshRenderers.Length : 0;
            int totalSMR = skinnedRenderers != null ? skinnedRenderers.Length : 0;
            bool hasVariantGameObjectRules = false;
            string selectedVariantName = null;
            string variantResolutionPath = null;
            Dictionary<string, bool> selectedVariantGameObjects = null;
            if (hasPartVariants)
            {
                hasVariantGameObjectRules = TryGetSelectedVariantGameObjectStates(
                    prefab,
                    partNode,
                    out selectedVariantName,
                    out selectedVariantGameObjects,
                    out variantResolutionPath);
            }
            int activeMR = 0;
            int activeSMR = 0;
            if (meshRenderers != null)
            {
                for (int i = 0; i < meshRenderers.Length; i++)
                {
                    var mr = meshRenderers[i];
                    if (mr != null && mr.gameObject.activeInHierarchy)
                        activeMR++;
                }
            }
            if (skinnedRenderers != null)
            {
                for (int i = 0; i < skinnedRenderers.Length; i++)
                {
                    var smr = skinnedRenderers[i];
                    if (smr != null && smr.gameObject.activeInHierarchy && smr.sharedMesh != null)
                        activeSMR++;
                }
            }
            bool filterInactiveVariantRenderers = ShouldFilterInactiveVariantRenderers(
                hasPartVariants, activeMR, activeSMR);
            // One line per part per window. The modelRoot local pos/rot used to be a SECOND per-part
            // "[DIAG] part ..." line emitted further down (todo entry 160's "per-part cloning details");
            // it says nothing this line cannot carry, so it is folded in here instead of doubling the volume.
            ParsekLog.VerboseRateLimited("GhostVisual", $"part_summary_{partName}",
                $"  Part '{partName}' pid={persistentId}: modelRoot='{modelRoot.name}' " +
                $"modelScale={modelRoot.localScale} modelPos={modelRoot.localPosition} " +
                $"modelRot={modelRoot.localRotation}, " +
                $"{totalMR} MeshRenderers, {totalSMR} SkinnedMeshRenderers" +
                (hasPartVariants ? " (has ModulePartVariants)" : ""), 60.0);
            LogPartVariantResolution(
                hasPartVariants, hasVariantGameObjectRules, partName,
                selectedVariantName, variantResolutionPath, selectedVariantGameObjects,
                filterInactiveVariantRenderers, activeMR, activeSMR);
            if (hasPartVariants)
                appliedSkin = new GhostAppliedSkin
                {
                    variantName = selectedVariantName,
                    variantResolutionPath = variantResolutionPath,
                };

            // Name by persistentId for O(1) lookup during playback; fall back to part name
            string partLabel = persistentId != 0
                ? persistentId.ToString()
                : (prefab.partInfo?.name ?? "unknown");
            GameObject partRoot = new GameObject($"ghost_part_{partLabel}");
            partRoot.transform.SetParent(root, false);

            Vector3 localPos;
            if (TryParseVector3(GetPartPositionRaw(partNode), out localPos))
                partRoot.transform.localPosition = localPos;

            Quaternion localRot;
            if (TryParseQuaternion(GetPartRotationRaw(partNode), out localRot))
                partRoot.transform.localRotation = localRot;

            bool added = false;

            // Clone map: maps prefab transforms → ghost cloned transforms.
            // Deduplicates shared parent nodes across multiple renderers.
            var cloneMap = new Dictionary<Transform, Transform>();

            // Preserve the model root's own local transform (position/rotation/scale).
            // Many KSP parts have non-identity transforms on the "model" child.
            // partRoot already has snapshot pos/rot, so add an intermediate node.
            GameObject modelNode = new GameObject(modelRoot.name);
            modelNode.transform.SetParent(partRoot.transform, false);
            modelNode.transform.localPosition = modelRoot.localPosition;
            modelNode.transform.localRotation = modelRoot.localRotation;
            modelNode.transform.localScale = modelRoot.localScale;
            // (The per-part "[DIAG] part ... localRot/localPos/localScale" line that used to sit here is folded
            //  into the single part_summary_ line above - same facts, one line per part instead of two.)
            cloneMap[modelRoot] = modelNode.transform;

            // For light showcase recordings, lift only light-part visuals so probes stay
            // fixed while the lamp geometry sits clearly above the probe body.
            if (raiseLightVisualOnly && prefab.FindModuleImplementing<ModuleLight>() != null)
                modelNode.transform.localPosition += new Vector3(0f, LightsShowcaseVisualYOffset, 0f);

            // Collect the ModuleWheelDamage transform names from part config. The busted wheel
            // mesh (damagedTransformName) is CLONED onto the ghost and hidden at build time, so a
            // recorded WheelBroken can swap it in for the intact one (undamagedTransformName),
            // exactly as stock SetDamaged does. It must never render alongside the intact mesh:
            // TryBuildWheelDamageInfo hides every cloned busted transform before the ghost is
            // shown. The one shape that cannot be swapped safely (a busted name matching the
            // model root or one of its ancestors, where hiding it would hide the whole part)
            // keeps the old behaviour and strips the busted renderers instead.
            ConfigNode partConfig = prefab.partInfo?.partConfig;
            HashSet<string> damagedWheelNames = GetDamagedWheelTransformNames(partConfig);
            HashSet<string> undamagedWheelNames = GetUndamagedWheelTransformNames(partConfig);
            bool wheelDamageSwappable = damagedWheelNames.Count > 0
                && !IsRendererOnDamagedTransform(modelRoot, damagedWheelNames);
            HashSet<string> damagedFilterNames = ResolveDamagedRendererFilterNames(
                damagedWheelNames, wheelDamageSwappable);

            // Collect depth-mask transform names (ReStock's ModuleRestockDepthMask and
            // the community ModuleDepthMask pattern). A depth-mask mesh writes DEPTH
            // ONLY at render queue ~1999 and relies on the live plugin managing the
            // part's material queues; cloned onto a ghost it occludes everything behind
            // it instead, punching a see-through hole to the sky/ocean (the round-6
            // "blueish missing texture" on ReStock claws and jet exhausts). Skip them.
            HashSet<string> depthMaskNames = GetDepthMaskTransformNames(partConfig);
            LogPartRendererExclusionNames(depthMaskNames, partName, persistentId, damagedWheelNames);

            // Transforms a stock module hides on every flight start (a flag part's other flag
            // meshes, a hinge's piston arm): their renderers are left off the ghost, on the same name-or-ancestor
            // test as the busted wheel names.
            HashSet<string> stockHiddenNames = ResolveStockHiddenTransformNames(
                prefab, partConfig, partNode, selectedVariantName, partName, persistentId);
            if (stockHiddenNames.Count > 0)
            {
                if (damagedFilterNames != null)
                    stockHiddenNames.UnionWith(damagedFilterNames);
                damagedFilterNames = stockHiddenNames;
            }

            // B9PartSwitch: the prefab carries no subtype, so the shown subtype of each module
            // decides which transforms the ghost hides (same filter) and, after cloning, which
            // cloned transforms it moves, turns and scales.
            List<B9ModuleSelection> b9Selections = ResolveB9Subtypes(
                partConfig, partNode, modelRoot, partName, persistentId, out HashSet<string> b9HiddenNames);
            if (b9HiddenNames != null)
            {
                if (damagedFilterNames != null)
                    b9HiddenNames.UnionWith(damagedFilterNames);
                damagedFilterNames = b9HiddenNames;
            }
            if (b9Selections.Count > 0)
            {
                if (appliedSkin == null)
                    appliedSkin = new GhostAppliedSkin();
                appliedSkin.b9Subtypes = ToAppliedB9Subtypes(b9Selections);
            }

            // Asteroids and comets: stock builds the rock at runtime from the persisted seed and
            // destroys the prefab's placeholder lump, so the ghost does the same (null for any
            // other part).
            RockGhostPlan rockPlan = SpaceObjectGhostVisual.TryPlan(prefab, partNode, partName);

            int variantSkipped = 0;
            int variantRuleSkipped = 0;
            int damagedSkipped = 0;
            int depthMaskSkipped = 0;
            int skinnedCloned = 0;
            int nullMeshSkipped = 0;
            for (int r = 0; r < meshRenderers.Length; r++)
            {
                var mr = meshRenderers[r];
                if (mr == null) continue;
                // Skip inactive renderers only when no GAMEOBJECT rules exist (#125).
                // When rules are present, they are the sole authority on which objects to include —
                // the prefab's base variant may have different objects active than the snapshot's variant.
                if (filterInactiveVariantRenderers && !hasVariantGameObjectRules && !mr.gameObject.activeInHierarchy)
                {
                    variantSkipped++;
                    continue;
                }
                if (hasVariantGameObjectRules &&
                    !IsRendererEnabledByVariantRule(
                        mr.transform, modelRoot, selectedVariantGameObjects,
                        out string _, out bool _))
                {
                    variantRuleSkipped++;
                    continue;
                }
                if (IsRendererOnDamagedTransform(mr.transform, damagedFilterNames))
                {
                    damagedSkipped++;
                    continue;
                }
                if (IsDepthMaskRenderer(mr, depthMaskNames))
                {
                    depthMaskSkipped++;
                    continue;
                }
                if (rockPlan != null && rockPlan.ReplacesPlaceholder
                    && SpaceObjectGhostVisual.IsUnderPlaceholder(mr.transform, modelRoot))
                    continue;
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                Transform leaf = MirrorTransformChain(mr.transform, modelRoot, modelNode.transform, cloneMap);
                leaf.gameObject.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                leaf.gameObject.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
                meshCount++;
                added = true;
            }
            LogVariantRendererSkips(variantSkipped, partName, variantRuleSkipped);

            skinnedCloned = CloneSkinnedMeshRenderers(
                skinnedRenderers, modelRoot, modelNode.transform, partRoot.transform,
                prefab, cloneMap,
                filterInactiveVariantRenderers, hasVariantGameObjectRules, selectedVariantGameObjects,
                damagedFilterNames, depthMaskNames, persistentId, partName,
                ref meshCount, ref damagedSkipped, ref nullMeshSkipped, ref depthMaskSkipped);
            if (skinnedCloned > 0)
                added = true;

            ApplyB9TransformModifiers(b9Selections, modelRoot, cloneMap, partName, persistentId);

            LogPartCloneSummary(meshCount, skinnedCloned, partName, persistentId,
                nullMeshSkipped, damagedSkipped, depthMaskSkipped);

            if (rockPlan != null
                && SpaceObjectGhostVisual.Attach(rockPlan, partRoot.transform, modelNode.transform,
                    partName, persistentId))
            {
                meshCount++;
                added = true;
            }

            if (hasPartVariants)
            {
                if (TryGetSelectedVariantTextureRules(prefab, partNode,
                    out string texVariantName, out List<VariantTextureRule> texRules))
                {
                    ParsekLog.VerboseRateLimited("GhostVisual", $"variant_tex_{partName}",
                        $"Part '{partName}' pid={persistentId}: variant '{texVariantName}' has {texRules.Count} TEXTURE rules");
                    int applied = ApplyVariantTextureRules(
                        modelNode.transform, texRules, partName, persistentId);
                }
            }

            // Fix up compound part visuals (fuel lines, struts) — CModuleLinkedMesh
            // normally stretches the line mesh at runtime; ghosts need manual fixup.
            compoundPartInfo = TryFixupCompoundPartVisuals(prefab, partNode, partRoot.transform,
                cloneMap, persistentId, partName);

            // Detect parachute parts via cloneMap (after cloneMap is fully populated)
            parachuteInfo = TryBuildParachuteInfo(prefab, partName, persistentId,
                cloneMap, modelRoot, partRoot.transform, ref meshCount, ref added);
            if (parachuteInfo != null)
                parachuteInfo.symmetryCount = GhostPlaybackLogic.StockParachuteSymmetryCount(
                    partNode != null ? partNode.GetValues("sym").Length : 0);

            // Detect jettison parts (shrouds/fairings) via cloneMap.
            jettisonInfo = ResolveJettisonTransforms(
                prefab, cloneMap, persistentId, partName, partNode);

            // Engine plates show only the selected mount pattern.
            ApplyDynamicNodeMeshVisibility(
                prefab.partInfo?.partConfig, partNode, modelNode.transform, persistentId, partName);

            // A snapshot that shows any ModuleJettison jettisoned hides every jettison transform
            // of the part (#290: continuation recordings carry no seed event to hide it later).
            // The resolver already marked the per-module stock flight-start hides (an empty
            // bottom node, a hide override); both land in spawnHiddenTransforms so a loop cycle
            // restarts from the same look.
            bool wholePartJettisoned = ShouldHideJettisonTransformsAtBuild(jettisonInfo, partNode);
            if (wholePartJettisoned)
                jettisonInfo.spawnHiddenTransforms = new List<Transform>(jettisonInfo.jettisonTransforms);
            int jettisonHidden = GhostPlaybackLogic.ApplyJettisonSpawnBaseline(jettisonInfo);
            if (jettisonHidden > 0)
            {
                ParsekLog.VerboseRateLimited("GhostVisual",
                    $"jettison-hidden-{partName}-{persistentId}",
                    $"    Part '{partName}' pid={persistentId}: {jettisonHidden} jettison transform(s) hidden at build " +
                    (wholePartJettisoned
                        ? "(isJettisoned=True in snapshot MODULE data)"
                        : "(stock flight-start rule: empty bottom node or hide override)"),
                    5.0);
            }

            // Detect engine parts and clone FX particle systems
            // selectedVariantGameObjects is null when no variant rules exist or when
            // the base-implicit variant is selected (prefab defaults, no geometry toggling).
            engineInfos = EngineFxBuilder.TryBuildEngineFX(prefab, persistentId, partName, modelRoot,
                modelNode.transform, cloneMap, selectedVariantGameObjects);
            GhostFxFingerprint.LogEngineInfos(partName, engineInfos);

            // Detect RCS parts and clone FX particle systems
            rcsInfos = TryBuildRcsFX(prefab, persistentId, partName, modelRoot,
                modelNode.transform, cloneMap, raiseRcsVisualOnly, selectedVariantGameObjects);
            GhostFxFingerprint.LogRcsInfos(partName, rcsInfos);

            string ladderAnimName;
            string ladderAnimRootName;
            bool hasRetractableLadder = TryGetRetractableLadderAnimation(
                prefab, out ladderAnimName, out ladderAnimRootName);
            string animationGroupDeployAnimName;
            bool hasAnimationGroupDeploy = TryGetAnimationGroupDeployAnimation(
                prefab, out animationGroupDeployAnimName);
            string standaloneAnimateGenericAnimName;
            bool hasStandaloneAnimateGenericDeploy = TryGetStandaloneAnimateGenericDeployAnimation(
                prefab, out standaloneAnimateGenericAnimName);
            string animateHeatAnimName;
            bool hasAnimateHeat = TryGetAnimateHeatAnimation(
                prefab, out animateHeatAnimName);
            string animateThrottleAnimName = null;
            bool hasAnimateThrottle = false;
            if (!hasAnimateHeat)
            {
                hasAnimateThrottle = TryGetAnimateThrottleAnimation(prefab, out animateThrottleAnimName);
            }
            string animateRcsAnimName = null;
            bool hasAnimateRcs = false;
            if (!hasAnimateHeat && !hasAnimateThrottle)
            {
                hasAnimateRcs = TryGetAnimateRcsAnimation(prefab, out animateRcsAnimName);
            }
            bool hasAnyHeatAnim = HasAnyHeatAnimation(hasAnimateHeat, hasAnimateThrottle, hasAnimateRcs);
            string heatAnimName = SelectHeatAnimationName(
                hasAnimateHeat, animateHeatAnimName,
                hasAnimateThrottle, animateThrottleAnimName,
                animateRcsAnimName);
            string aeroSurfaceTransformName;
            float aeroSurfaceDeployAngle;
            bool hasAeroSurfaceDeploy = TryGetAeroSurfaceDeployInfo(
                prefab, out aeroSurfaceTransformName, out aeroSurfaceDeployAngle);
            string controlSurfaceTransformName;
            float controlSurfaceDeployAngle;
            bool hasControlSurfaceDeploy = TryGetControlSurfaceDeployInfo(
                prefab, out controlSurfaceTransformName, out controlSurfaceDeployAngle);
            List<string> robotArmScannerAnimCandidates;
            bool hasRobotArmScannerDeploy = TryGetRobotArmScannerDeployAnimations(
                prefab, out robotArmScannerAnimCandidates);
            bool hasRoboticModules = HasRoboticModules(prefab);

            // If the part has any animated modules (deployable, gear, ladder, animation-group, cargo bay), ensure
            // the full model transform hierarchy is mirrored into the ghost.  The mesh-cloning
            // phase above only creates transforms leading to MeshRenderers/SkinnedMeshRenderers.
            // Animations often move intermediate non-mesh transforms that would otherwise be
            // missing, causing FindTransformByPath to fail during deploy/retract playback.
            bool needsFullHierarchy =
                prefab.FindModuleImplementing<ModuleDeployablePart>() != null ||
                prefab.FindModuleImplementing<ModuleWheels.ModuleWheelDeployment>() != null ||
                prefab.FindModuleImplementing<ModuleCargoBay>() != null ||
                AnimatedModulesNeedFullModelHierarchy(
                    hasRetractableLadder,
                    hasAnimationGroupDeploy,
                    hasStandaloneAnimateGenericDeploy,
                    hasAnyHeatAnim,
                    hasAeroSurfaceDeploy,
                    hasControlSurfaceDeploy,
                    hasRobotArmScannerDeploy,
                    hasRoboticModules);

            if (needsFullHierarchy)
                EnsureFullModelHierarchy(modelRoot, modelNode.transform, cloneMap, partName);

            if (hasRoboticModules)
                roboticInfos = TryBuildRoboticInfos(
                    prefab, persistentId, partName, modelRoot, modelNode.transform, cloneMap);

            // S3: discovered right after the robotic family and against the SAME resolved
            // hierarchy — needsFullHierarchy above already covers control surfaces and deployable
            // parts, and a gimbal transform lives on an engine bell that is always cloned.
            synthesizedMotionInfos = TryBuildSynthesizedMotionInfos(
                prefab, persistentId, partName, modelRoot, modelNode.transform, cloneMap);

            // Detect deployable parts via animation cascade (solar panels, gear, ladders, etc.)
            deployableInfo = TryBuildDeployableInfo(prefab, partNode, persistentId, partName,
                modelRoot, modelNode.transform, cloneMap,
                hasRetractableLadder, ladderAnimName, ladderAnimRootName,
                hasAnimationGroupDeploy, animationGroupDeployAnimName,
                hasStandaloneAnimateGenericDeploy, standaloneAnimateGenericAnimName,
                hasAeroSurfaceDeploy, aeroSurfaceTransformName, aeroSurfaceDeployAngle,
                hasControlSurfaceDeploy, controlSurfaceTransformName, controlSurfaceDeployAngle,
                hasRobotArmScannerDeploy, robotArmScannerAnimCandidates);

            // Detect ModuleAnimateHeat / FXModuleAnimateThrottle / FXModuleAnimateRCS visual states
            if (hasAnyHeatAnim)
            {
                string heatSource = SelectHeatAnimationSourceModule(hasAnimateHeat, hasAnimateThrottle);
                heatInfo = TryBuildHeatInfo(prefab, heatAnimName, heatSource,
                    modelNode.transform, partName, persistentId, modelRoot, cloneMap);
            }

            // Detect light parts and clone Light components for ghost playback
            lightInfo = BuildLightGhostInfo(prefab, partNode, modelRoot, modelNode.transform, cloneMap,
                persistentId, partName, raiseLightVisualOnly);

            // Detect ModuleColorChanger: cabin lights (Pattern A) and heat shield char (Pattern B)
            // The prefab's own PART config is passed alongside the snapshot node because every
            // field this builder reads is config-only and so never survives into a persisted
            // snapshot node - GS6-GHOST-HAS-NO-COLORCHANGER-STATE. Same source the fairing
            // structure hider already uses two calls below.
            List<ThrottleGlowDriver> throttleGlowDrivers = CollectThrottleGlowDrivers(prefab, partNode);
            colorChangerInfos = BuildColorChangerInfos(
                partNode, prefab.partInfo?.partConfig, modelNode.transform, persistentId, partName,
                ThrottleDrivenModuleIds(throttleGlowDrivers));

            // Material / active-state clips the live part holds at rest (the ion engine's glow,
            // ReStock's scanner screen): written after the heat, light and colour-changer infos
            // so it reuses their material clones and leaves their properties to them.
            // A standalone ModuleAnimateGeneric deploy clip that animates only colours joins the
            // deployable info here, so its deploy event switches the colours.
            ApplyAnimationRestStates(prefab, partNode, modelRoot, modelNode.transform, cloneMap,
                heatAnimName, ref heatInfo, lightInfo, colorChangerInfos, persistentId, partName,
                hasStandaloneAnimateGenericDeploy ? standaloneAnimateGenericAnimName : null,
                ref deployableInfo);

            // Engine / RCS nozzle glow: FXModuleAnimateThrottle / FXModuleAnimateRCS clips and the
            // colour changers an FXModuleThrottleEffects names, driven by the recorded engine / RCS
            // power. After the rest-state write, whose material clones it reuses.
            BuildNozzleGlows(prefab, partNode, modelRoot, modelNode.transform, cloneMap,
                ref heatInfo, lightInfo, colorChangerInfos, throttleGlowDrivers, persistentId, partName);

            // Procedural fairings: stock-built panel shell + stock truss / cap visibility rule
            fairingInfo = BuildFairingVisual(partNode, prefab, modelNode.transform, persistentId, partName);
            if (fairingInfo != null)
                added = true;

            // ModuleStructuralNode on a part that is not a procedural fairing (the Making
            // History MEM lander's Adapter): stock applies the same CheckDisplay rule.
            if (ShouldBuildNonFairingStructureNodes(partNode, prefab.partInfo?.partConfig))
            {
                BuildFairingStructureNodes(
                    prefab.partInfo?.partConfig, partNode, modelNode.transform, persistentId, partName);
            }

            // FXModuleLookAtConstraint: stock re-aims gimbal and door pistons every frame; the
            // ghost aims them once here, after every build-time pose, and again after each
            // gimbal / deployable pose write.
            BuildLookAtConstraints(prefab.partInfo?.partConfig, modelRoot, modelNode.transform,
                partRoot.transform, cloneMap, synthesizedMotionInfos, deployableInfo,
                persistentId, partName);

            // Wheel damage swap: resolved LAST, against the fully populated cloneMap, so every
            // cloned busted transform is found and hidden before the ghost is ever shown.
            if (ShouldResolveWheelDamageInfo(damagedWheelNames, undamagedWheelNames))
            {
                wheelDamageInfo = TryBuildWheelDamageInfo(
                    persistentId, partName, wheelDamageSwappable,
                    damagedWheelNames, undamagedWheelNames, cloneMap, modelNode.transform);
            }

            if (!added)
            {
                compoundPartInfo = null;
                wheelDamageInfo = null;
                Object.Destroy(partRoot);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Why a part with a ModuleWheelDamage did or did not get a <see cref="WheelDamageGhostInfo"/>.
        /// </summary>
        internal enum WheelDamageBuildDecision
        {
            /// Build the info: the swap has something to show in place of the intact wheel.
            Build,
            /// The busted name matches the model root or an ancestor; its renderers were stripped.
            NotSwappable,
            /// A busted mesh is configured but no renderer of it cloned onto the ghost (a variant,
            /// depth-mask or null-mesh filter skipped it, leaving at most an empty transform, or
            /// the name resolves to nothing). Building would let a break hide the
            /// intact wheel with nothing in its place, so the break has no visual instead.
            BustedMeshNotCloned,
            /// Neither name resolved to a cloned transform.
            NothingCloned,
        }

        /// <summary>
        /// Pure gate for <see cref="TryBuildWheelDamageInfo"/>. When the part names no busted mesh
        /// at all, stock's own break just hides the intact transform, and the ghost mirrors that.
        /// </summary>
        internal static WheelDamageBuildDecision ClassifyWheelDamageBuild(
            bool swappable, int damagedNameCount, int damagedRenderedClones, int undamagedClones)
        {
            if (damagedNameCount > 0 && !swappable) return WheelDamageBuildDecision.NotSwappable;
            if (damagedRenderedClones == 0 && undamagedClones == 0) return WheelDamageBuildDecision.NothingCloned;
            if (damagedNameCount > 0 && damagedRenderedClones == 0) return WheelDamageBuildDecision.BustedMeshNotCloned;
            return WheelDamageBuildDecision.Build;
        }

        /// <summary>
        /// Collects the cloned transforms named by the part's ModuleWheelDamage
        /// <c>damagedTransformName</c> / <c>undamagedTransformName</c> (stock resolves them with
        /// <c>Part.FindModelTransform</c>, i.e. by name under the model) and HIDES every busted
        /// one, so a freshly built ghost shows the intact wheel. The model node itself is never
        /// collected: hiding it would hide the whole part.
        /// </summary>
        private static WheelDamageGhostInfo TryBuildWheelDamageInfo(
            uint persistentId, string partName, bool swappable,
            HashSet<string> damagedNames, HashSet<string> undamagedNames,
            Dictionary<Transform, Transform> cloneMap, Transform modelNode)
        {
            var damaged = new List<Transform>();
            var undamaged = new List<Transform>();
            if (cloneMap != null)
            {
                foreach (Transform clone in cloneMap.Values)
                {
                    if (clone == null || clone == modelNode) continue;
                    if (damagedNames != null && damagedNames.Contains(clone.name))
                        damaged.Add(clone);
                    else if (undamagedNames != null && undamagedNames.Contains(clone.name))
                        undamaged.Add(clone);
                }
            }

            // A busted transform counts only when a renderer cloned under it: the full-hierarchy
            // mirror clones every transform, so an empty busted transform is common when its
            // renderers were skipped (variant, depth-mask or null-mesh filters), and swapping to it
            // would make the wheel vanish.
            int damagedRendered = 0;
            for (int i = 0; i < damaged.Count; i++)
                if (damaged[i].GetComponentsInChildren<Renderer>(true).Length > 0)
                    damagedRendered++;

            WheelDamageBuildDecision decision = ClassifyWheelDamageBuild(
                swappable, damagedNames?.Count ?? 0, damagedRendered, undamaged.Count);
            if (decision != WheelDamageBuildDecision.Build)
            {
                // A busted mesh that cloned but will not be swapped must still never show.
                for (int i = 0; i < damaged.Count; i++)
                    damaged[i].gameObject.SetActive(false);
                ParsekLog.VerboseRateLimited("GhostVisual", $"wheel_damage_skip_{partName}",
                    $"  Part '{partName}' pid={persistentId}: no wheel damage swap ({decision}): " +
                    $"busted clones={damaged.Count} (with renderers={damagedRendered}) " +
                    $"intact clones={undamaged.Count} " +
                    "- a recorded WheelBroken will have no visual", 60.0);
                return null;
            }

            for (int i = 0; i < damaged.Count; i++)
                damaged[i].gameObject.SetActive(false);

            ParsekLog.VerboseRateLimited("GhostVisual", $"wheel_damage_info_{partName}",
                $"  Part '{partName}' pid={persistentId}: wheel damage swap resolved " +
                $"(busted={damaged.Count} hidden at build, with renderers={damagedRendered}, " +
                $"intact={undamaged.Count})", 60.0);
            return new WheelDamageGhostInfo
            {
                partPersistentId = persistentId,
                damagedTransforms = damaged,
                undamagedTransforms = undamaged,
                damaged = false,
            };
        }

        /// <summary>
        /// The ModuleColorChanger MODULE nodes of one PART node, in declaration order.
        /// Pure over ConfigNode: never null, possibly empty.
        /// </summary>
        internal static List<ConfigNode> CollectColorChangerModuleNodes(ConfigNode partNode)
        {
            var found = new List<ConfigNode>();
            if (partNode == null) return found;

            var moduleNodes = partNode.GetNodes("MODULE");
            if (moduleNodes == null) return found;

            for (int m = 0; m < moduleNodes.Length; m++)
            {
                if (moduleNodes[m] == null) continue;
                if (moduleNodes[m].GetValue("name") == "ModuleColorChanger")
                    found.Add(moduleNodes[m]);
            }
            return found;
        }

        /// <summary>
        /// WHY THIS EXISTS. Everything the colour-changer builder needs - <c>shaderProperty</c>,
        /// <c>toggleInFlight</c> and the red/green/blue/alpha curves - is CONFIG-ONLY on stock's
        /// ModuleColorChanger: none of it is a persistent KSPField, so a PART node KSP itself wrote
        /// into a save, a .craft or a Parsek ghost sidecar carries only
        /// <c>isEnabled</c> / <c>animState</c> / <c>stagingEnabled</c>. Reading the snapshot node
        /// alone therefore resolved NOTHING on any ghost built from a recording, and nothing on a
        /// synthetic part showcase row either (its node declares no ModuleColorChanger); only a
        /// test node that authors the fields by hand resolved (GS6-GHOST-HAS-NO-COLORCHANGER-STATE).
        ///
        /// The rule, one per ModuleColorChanger instance and matched by DECLARATION ORDER:
        /// prefer the snapshot node when it actually carries a shaderProperty (so a node that
        /// authors the fields keeps its exact behaviour, overrides included), else fall
        /// back to the prefab's own part config at the same index. When the snapshot part node
        /// declares no ModuleColorChanger at all, the prefab's list is used whole.
        /// </summary>
        internal static List<ConfigNode> ResolveColorChangerConfigNodes(
            ConfigNode snapshotPartNode, ConfigNode prefabPartConfig)
        {
            List<ConfigNode> snapshot = CollectColorChangerModuleNodes(snapshotPartNode);
            List<ConfigNode> prefab = CollectColorChangerModuleNodes(prefabPartConfig);

            if (snapshot.Count == 0)
                return prefab;

            var resolved = new List<ConfigNode>(snapshot.Count);
            for (int i = 0; i < snapshot.Count; i++)
            {
                bool snapshotCarriesConfig =
                    !string.IsNullOrEmpty(snapshot[i].GetValue("shaderProperty"));
                if (!snapshotCarriesConfig && i < prefab.Count)
                    resolved.Add(prefab[i]);
                else
                    resolved.Add(snapshot[i]);
            }
            return resolved;
        }

        /// <summary>
        /// Resolves the char scalars stock ModuleAblator writes into the ModuleColorChanger
        /// whose moduleID equals the ablator's <c>charModuleName</c>: the snapshot's current
        /// char (<c>charMin + (charMax - charMin) * fraction</c>, fraction = remaining ablator
        /// amount times the persisted <c>nominalAmountRecip</c>, capped at 1) and the fully
        /// charred end (<c>charMin</c>). Config-only ablator fields come from the prefab config.
        /// Returns false when no ablator with <c>useChar</c> and a non-empty
        /// <c>ablativeResource</c> drives this colour changer (stock then never calls SetScalar).
        /// </summary>
        internal static bool TryResolveAblationCharScalars(
            ConfigNode snapshotPartNode, ConfigNode prefabPartConfig, string colorChangerModuleId,
            out float currentScalar, out float charredScalar)
        {
            currentScalar = 0f;
            charredScalar = 0f;
            string moduleId = string.IsNullOrEmpty(colorChangerModuleId)
                ? "colorChanger" : colorChangerModuleId;

            ConfigNode ablatorConfig = FindModuleNode(prefabPartConfig, "ModuleAblator");
            ConfigNode ablatorSnapshot = FindModuleNode(snapshotPartNode, "ModuleAblator");
            if (ablatorConfig == null)
                ablatorConfig = ablatorSnapshot;
            if (ablatorConfig == null)
                return false;

            bool useChar = true;
            string useCharStr = ablatorConfig.GetValue("useChar");
            if (!string.IsNullOrEmpty(useCharStr) && bool.TryParse(useCharStr, out bool parsedUseChar))
                useChar = parsedUseChar;
            if (!useChar)
                return false;

            string charModuleName = ablatorConfig.GetValue("charModuleName");
            if (string.IsNullOrEmpty(charModuleName))
                charModuleName = "shieldChar";
            if (!string.Equals(charModuleName, moduleId, System.StringComparison.Ordinal))
                return false;

            string ablativeResource = ablatorConfig.GetValue("ablativeResource");
            if (string.IsNullOrEmpty(ablativeResource))
                return false;

            float charMax = ParseFloatOr(ablatorConfig.GetValue("charMax"), 0.85f);
            float charMin = ParseFloatOr(ablatorConfig.GetValue("charMin"), 0.1f);

            float fraction = 1f;
            ConfigNode resourceNode = FindResourceNode(snapshotPartNode, ablativeResource);
            if (resourceNode != null
                && double.TryParse(resourceNode.GetValue("amount"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double amount))
            {
                double recip = -1.0;
                if (ablatorSnapshot != null)
                    double.TryParse(ablatorSnapshot.GetValue("nominalAmountRecip"), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out recip);
                if (recip <= 0.0
                    && double.TryParse(resourceNode.GetValue("maxAmount"), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double maxAmount)
                    && maxAmount > 0.0)
                {
                    recip = 1.0 / maxAmount;
                }
                if (recip > 0.0)
                    fraction = (float)System.Math.Max(0.0, System.Math.Min(1.0, amount * recip));
            }

            currentScalar = charMin + (charMax - charMin) * fraction;
            charredScalar = charMin;
            return true;
        }

        private static ConfigNode FindResourceNode(ConfigNode partNode, string resourceName)
        {
            if (partNode == null) return null;
            ConfigNode[] resources = partNode.GetNodes("RESOURCE");
            if (resources == null) return null;
            for (int i = 0; i < resources.Length; i++)
            {
                if (resources[i] != null && resources[i].GetValue("name") == resourceName)
                    return resources[i];
            }
            return null;
        }

        private static float ParseFloatOr(string value, float fallback)
        {
            if (!string.IsNullOrEmpty(value)
                && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                return parsed;
            return fallback;
        }

        /// <summary>
        /// Evaluates redCurve/greenCurve/blueCurve/alphaCurve from a ModuleColorChanger config
        /// at a given time t. Returns a Color with the evaluated RGBA values.
        /// </summary>
        internal static Color EvaluateColorCurves(ConfigNode moduleNode, float t)
        {
            float r = EvaluateSingleCurve(moduleNode.GetNode("redCurve"), t);
            float g = EvaluateSingleCurve(moduleNode.GetNode("greenCurve"), t);
            float b = EvaluateSingleCurve(moduleNode.GetNode("blueCurve"), t);
            float a = EvaluateSingleCurve(moduleNode.GetNode("alphaCurve"), t, defaultValue: 1f);
            return new Color(r, g, b, a);
        }

        /// <summary>
        /// Evaluates a single FloatCurve-style ConfigNode at time t.
        /// Each key line is "time value [inTangent outTangent]".
        /// Uses simple linear interpolation between keys.
        /// </summary>
        internal static float EvaluateSingleCurve(ConfigNode curveNode, float t, float defaultValue = 0f)
        {
            if (curveNode == null)
                return defaultValue;

            string[] keys = curveNode.GetValues("key");
            if (keys == null || keys.Length == 0)
                return defaultValue;

            // Parse key entries: "time value [inTangent outTangent]"
            var parsed = new List<(float time, float value)>();
            for (int i = 0; i < keys.Length; i++)
            {
                string[] parts = keys[i].Split(new[] { ' ', '\t' },
                    System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    ParsekLog.Warn("GhostVisual", $"EvaluateSingleCurve key[{i}] has fewer than 2 parts: '{keys[i]}'");
                    continue;
                }

                float keyTime, keyValue;
                if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out keyTime) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out keyValue))
                {
                    parsed.Add((keyTime, keyValue));
                }
                else
                {
                    ParsekLog.Warn("GhostVisual", $"EvaluateSingleCurve key[{i}] failed to parse time/value: '{keys[i]}'");
                }
            }

            if (parsed.Count == 0)
                return defaultValue;

            // Sort by time
            parsed.Sort((a, b) => a.time.CompareTo(b.time));

            // Clamp to range
            if (t <= parsed[0].time)
                return parsed[0].value;
            if (t >= parsed[parsed.Count - 1].time)
                return parsed[parsed.Count - 1].value;

            // Linear interpolation between adjacent keys
            for (int i = 0; i < parsed.Count - 1; i++)
            {
                if (t >= parsed[i].time && t <= parsed[i + 1].time)
                {
                    float span = parsed[i + 1].time - parsed[i].time;
                    if (span <= 0f)
                        return parsed[i].value;
                    float fraction = (t - parsed[i].time) / span;
                    return Mathf.Lerp(parsed[i].value, parsed[i + 1].value, fraction);
                }
            }

            return parsed[parsed.Count - 1].value;
        }

        // Cached soft-circle texture for explosion FX (avoids per-explosion allocation in looping playback)
        private static Texture2D cachedExplosionTexture;

        // Cached FieldInfo for FXMonger's private static `fetch` singleton. Reading this
        // field directly mirrors FXMonger.Explode's own null-check (`if (!(fetch != null)) return;`)
        // without the per-call cost of Object.FindObjectOfType<FXMonger>() scanning the scene.
        private static System.Reflection.FieldInfo fxMongerFetchField;

        // Cached FieldInfo for FXMonger's protected `explosionObjects` list (the per-explosion
        // FXObject roster stock FXMonger.LateUpdate appends to as each ProtoExplosion fires).
        // Used by the pause/unpause path to walk every in-flight stock explosion GameObject and
        // pause their AudioSources alongside Parsek's own tracked sources — without this,
        // FXMonger-spawned PlayOneShot voices would punch through Parsek's per-source pause.
        private static System.Reflection.FieldInfo fxMongerExplosionObjectsField;

        /// <summary>
        /// S4: the puff's steady emission rate. A jetpack has no throttle a player can set, so
        /// there is one rate rather than a power curve.
        /// </summary>
        internal const float EvaJetpackPlumeEmissionRate = 45f;

        /// <summary>Pure: the vessel name a replayed flag is planted under.</summary>
        internal static string FlagVesselNameOf(FlagEvent evt)
        {
            return !string.IsNullOrEmpty(evt.flagSiteName) ? evt.flagSiteName : "Flag";
        }

        /// <summary>
        /// Spawns a real KSP flag vessel at the recorded position via ProtoVessel.
        /// Returns the flag vessel's root GameObject, or null on failure.
        /// The flag part is "prebuilt" with no .mu mesh — meshes are generated at runtime
        /// by FlagDecalBackground, so we must spawn a real vessel rather than clone a ghost.
        /// </summary>
        internal static GameObject SpawnFlagVessel(FlagEvent evt)
        {
            ProtoVessel pv = null;
            try
            {
                CelestialBody body = FlightGlobals.Bodies?.Find(b => b.name == evt.bodyName);
                if (body == null)
                {
                    ParsekLog.Warn("GhostVisual",
                        $"Cannot spawn flag vessel: body '{evt.bodyName}' not found");
                    return null;
                }

                // Build flag part node with FlagSite module in "Placed" state
                uint flightId = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
                ConfigNode partNode = ProtoVessel.CreatePartNode("flag", flightId);
                string flagURL = !string.IsNullOrEmpty(evt.flagURL) ? evt.flagURL : "Squad/Flags/default";
                partNode.SetValue("flag", flagURL, true);

                ConfigNode moduleNode = new ConfigNode("MODULE");
                moduleNode.AddValue("name", "FlagSite");
                moduleNode.AddValue("state", "Placed");
                moduleNode.AddValue("PlaqueText", evt.plaqueText ?? "");
                moduleNode.AddValue("placedBy", evt.placedBy ?? "");
                partNode.AddNode(moduleNode);

                // Build orbit (required by ProtoVessel even for landed vessels)
                Orbit orbit = new Orbit(0, 0, body.Radius + evt.altitude, 0, 0, 0, 0, body);
                ConfigNode[] partNodes = new ConfigNode[] { partNode };

                string vesselName = FlagVesselNameOf(evt);
                ConfigNode vesselNode = ProtoVessel.CreateVesselNode(
                    vesselName, VesselType.Flag, orbit, 0, partNodes);

                // Set landed position
                var ic = CultureInfo.InvariantCulture;
                vesselNode.SetValue("sit", Vessel.Situations.LANDED.ToString(), true);
                vesselNode.SetValue("landed", "True", true);
                vesselNode.SetValue("splashed", "False", true);
                vesselNode.SetValue("lat", evt.latitude.ToString("R", ic), true);
                vesselNode.SetValue("lon", evt.longitude.ToString("R", ic), true);
                vesselNode.SetValue("alt", evt.altitude.ToString("R", ic), true);
                vesselNode.SetValue("landedAt", body.name, true);

                // ProtoVessel.rot is loaded into vesselRef.srfRelRotation, so keep the
                // recorded surface-relative frame here. Live ghost transforms compose
                // body.bodyTransform.rotation separately when assigning Transform.rotation.
                Quaternion surfRot = new Quaternion(evt.rotX, evt.rotY, evt.rotZ, evt.rotW);
                VesselSnapshotOps.TryApplySpawnRotationFromSurfaceRelative(
                    vesselNode,
                    body,
                    surfRot,
                    "Flag spawn");

                // Spawn via ProtoVessel
                pv = new ProtoVessel(vesselNode, HighLogic.CurrentGame);
                HighLogic.CurrentGame.flightState.protoVessels.Add(pv);
                pv.Load(HighLogic.CurrentGame.flightState);

                if (pv.vesselRef == null)
                {
                    ParsekLog.Warn("GhostVisual",
                        $"Flag vessel spawn failed: ProtoVessel.Load() produced null vesselRef");
                    VesselSpawner.CleanupFailedSpawnedProtoVessel(pv, "GhostVisual", "Flag spawn cleanup");
                    return null;
                }

                // The flag prefab is PhysicsSignificance=1 but KSP promotes single-part
                // roots back to FULL. Without hardening, an Eve flag at the hot atmosphere
                // bottom can overheat (prefab maxTemp=2000) and a high-speed active-vessel
                // collision with a replay flag can exceed crashTolerance=12. Same fix
                // shape as the GhostMapPresence map-presence markers.
                GhostMapPresence.HardenGhostVesselPartPhysics(pv.vesselRef, "SpawnFlagVessel");

                ParsekLog.Verbose("GhostVisual",
                    $"Spawned flag vessel: '{vesselName}' by '{evt.placedBy}' " +
                    $"at ({evt.latitude:F4},{evt.longitude:F4},{evt.altitude:F1}) on {evt.bodyName}");

                return pv.vesselRef.gameObject;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Error("GhostVisual",
                    $"Failed to spawn flag vessel: {ex.Message}");
                VesselSpawner.CleanupFailedSpawnedProtoVessel(pv, "GhostVisual", "Flag spawn cleanup");
                return null;
            }
        }

        /// <summary>
        /// Creates a simple sphere GameObject as a fallback ghost visual
        /// when no vessel snapshot is available.
        /// </summary>
        internal static GameObject CreateGhostSphere(string name, Color color)
        {
            GameObject ghost = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ghost.name = name;
            ghost.transform.localScale = Vector3.one * 12f; // 12m diameter

            Collider collider = ghost.GetComponent<Collider>();
            if (collider != null)
                collider.enabled = false;

            Renderer renderer = ghost.GetComponent<Renderer>();
            if (renderer != null)
            {
                Shader shader = Shader.Find("KSP/Emissive/Diffuse");
                if (shader != null)
                {
                    Material mat = new Material(shader);
                    mat.color = color;
                    mat.SetColor("_EmissiveColor", color);
                    renderer.material = mat;
                }
                else
                {
                    ParsekLog.Warn("GhostVisual", "Could not find KSP/Emissive/Diffuse shader, using default");
                }
            }

            return ghost;
        }

    }
}
