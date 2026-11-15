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

        // Reentry FX thresholds — matched to KSP stock Physics.cfg aeroFX parameters
        // KSP shows thermal FX (orange flames) starting at Mach 2.5, fully orange at Mach 3.75
        // aeroFXStrength ∝ velocity^3.5 * (0.0091 * density^0.5 + 0.09 * density^2)
        // Density fade starts at 0.0015 kg/m³ (near edge of atmosphere)
        internal const float AeroFxThermalStartMach = 2.5f;
        internal const float AeroFxThermalFullMach = 3.75f;
        internal const float AeroFxVelocityExponent = 3.5f;
        internal const float AeroFxDensityScalar1 = 0.0091f;
        internal const float AeroFxDensityExponent1 = 0.5f;
        internal const float AeroFxDensityScalar2 = 0.09f;
        internal const float AeroFxDensityExponent2 = 2f;
        internal const float AeroFxDensityFadeStart = 0.0015f;
        internal static readonly Color ReentryHotEmissionLow = new Color(1.5f, 0.6f, 0.15f, 1f);
        internal static readonly Color ReentryHotEmissionHigh = new Color(2.0f, 1.5f, 0.8f, 1f);
        internal const float ReentrySmoothingRate = 18.0f;
        internal const float ReentryLayerAThreshold = 0.02f;
        internal const float ReentryFireThreshold = 0.02f;

        // Fire envelope particle configuration. Bug #538 targets roughly 2x the
        // previous visible density; emission is the primary dial, while the cap only
        // needs enough extra headroom to avoid clipping the denser stream.
        internal const float ReentryFireDensityMultiplier = 2f;
        internal const float ReentryFireLifetimeMin = 0.1f;
        internal const float ReentryFireLifetimeMax = 0.35f;
        internal const int ReentryFireMaxParticles = 2000;
        internal const float ReentryFireEmissionMin = 300f * ReentryFireDensityMultiplier;
        internal const float ReentryFireEmissionMax = 2000f * ReentryFireDensityMultiplier;

        // Fire shell overlay: ghost meshes drawn at offsets along velocity
        // Approximates KSP's FXCamera replacement shader (vertex displacement along airflow)
        internal const int ReentryFireShellPasses = 4;
        internal const float ReentryFireShellMaxOffset = 0.05f; // fraction of vesselLength
        internal static readonly Color ReentryFireShellColor = new Color(1f, 0.45f, 0.12f, 1f);

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
                // RAPIER's smokePoint frame matches stock large-smoke setups more closely.
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
                        bool partVisualAdded = AddPartVisuals(build.visualsRoot, partNode, ap.partPrefab,
                            persistentId, partName, out meshCount, out parachuteInfo, out jettisonInfo,
                            out partEngineInfos, out deployableInfo, out wheelDamageInfo,
                            out heatInfo, out lightInfo, out fairingInfo,
                            out partRcsInfos, out partRoboticInfos, out partSynthesizedMotionInfos,
                            out partColorChangerInfos, out compoundPartInfo,
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
                            build.heatInfos.Add(heatInfo);
                        if (lightInfo != null)
                            build.lightInfos.Add(lightInfo);
                        if (fairingInfo != null)
                            build.fairingInfos.Add(fairingInfo);
                        if (partRcsInfos != null)
                            build.rcsInfos.AddRange(partRcsInfos);
                        if (partRoboticInfos != null)
                            build.roboticInfos.AddRange(partRoboticInfos);
                        if (partSynthesizedMotionInfos != null)
                            MergeSynthesizedMotionInfos(build.synthesizedMotionInfos, partSynthesizedMotionInfos);
                        if (partColorChangerInfos != null)
                            build.colorChangerInfos.AddRange(partColorChangerInfos);
                        if (compoundPartInfo != null)
                            build.compoundPartInfos.Add(compoundPartInfo);

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
        /// FXPrefab registers particles with FloatingOrigin — pollutes global state on ghosts.
        ///
        /// Audit #113: FXModuleAnimateThrottle and FXModuleAnimateRCS are PartModules (not
        /// MonoBehaviours) requiring Part/Vessel context. They are reimplemented via HeatGhostInfo
        /// animation sampling, which is architecturally correct: one-shot build cost cached per
        /// part type, near-zero runtime cost (3-level quantized transform/material snaps on event
        /// boundaries), correct multi-instance disambiguation. Native modules are infeasible on
        /// ghosts and the reimplementation is intentionally minimal per the visual efficiency
        /// design principle.
        /// </summary>
        // ReStock's SRB smoke models (restock-fx-srb-smoke-1/2/3) are world-space
        // TRAIL assets: live, the plume column comes from the EMITTER (the flying
        // vessel) moving away from emitted particles; the baked particle velocity is
        // near zero (smoke-3 is exactly zero; in modern KSP both damping and
        // emitterVelocityScale are dead fields, and the cfg speed curve maxes at 1.0,
        // so live full-power velocity equals the baked value). A stationary ghost
        // (static showcase fixture, hover) never moves, so every particle pools at
        // the nozzle point. Give such emitters a minimum exhaust-ward flow so the
        // smoke flows instead of gathering. Provably a no-op on non-ReStock installs:
        // a 2026-06-12 scan of every Squad/SquadExpansion/ReStock/ReStockPlus .mu
        // found ZERO stock world-space emitters; only ReStock's three srb-smoke
        // models (and drill FX outside the engine FX path) are world-space.
        // Accepted deviation for MOVING ghosts: the smoke column drifts a few m/s
        // exhaust-ward on top of the motion-painted trail, visually minor next to
        // vessel speed and physically plausible for exhaust flow.
        internal const float WorldSpaceEmitterSlowSpeedThreshold = 4f;
        internal const float WorldSpaceEmitterFloorSpeed = 6f;

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
        //   2.7x — Rhino; prefab plume is visibly narrow even at 1.5x, ~80% wider.
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
                { "Size3AdvancedEngine",    2.7f  }, // KR-2L+ Rhino — widen prefab plume
            };

        // Cache: partName → (semi-deployed + deployed canopy states) — sample once per part type, reuse across ghosts
        private static readonly Dictionary<string, (Vector3 sdScale, Vector3 sdPos, Quaternion sdRot, bool semiDeployedSampled,
            Vector3 dScale, Vector3 dPos, Quaternion dRot)> canopyCache =
            new Dictionary<string, (Vector3, Vector3, Quaternion, bool, Vector3, Vector3, Quaternion)>();

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

        private static bool HasCModuleLinkedMesh(ConfigNode partConfig)
        {
            if (partConfig == null) return false;
            ConfigNode[] modules = partConfig.GetNodes("MODULE");
            for (int m = 0; m < modules.Length; m++)
            {
                if (modules[m].GetValue("name") == "CModuleLinkedMesh")
                    return true;
            }
            return false;
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
        {
            data = new CompoundPartData
            {
                lineObjName = "obj_line",
                targetAnchorName = "obj_targetAnchor",
                targetCapName = "obj_targetCap"
            };

            // Only process parts that have CModuleLinkedMesh — avoids false positives
            // from non-compound parts that happen to have a PARTDATA node.
            if (!HasCModuleLinkedMesh(partConfig))
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

            // Read transform names from CModuleLinkedMesh part config (varies: fuel line
            // uses obj_line, strut uses obj_strut).
            if (partConfig != null)
            {
                ConfigNode[] modules = partConfig.GetNodes("MODULE");
                for (int m = 0; m < modules.Length; m++)
                {
                    if (modules[m].GetValue("name") == "CModuleLinkedMesh")
                    {
                        data.lineObjName = modules[m].GetValue("lineObjName") ?? data.lineObjName;
                        data.targetAnchorName = modules[m].GetValue("targetAnchorName") ?? data.targetAnchorName;
                        data.targetCapName = modules[m].GetValue("targetCapName") ?? data.targetCapName;
                        break;
                    }
                }
            }

            return true;
        }

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
            CompoundPartData data;
            if (!TryParseCompoundPartData(partNode, prefab.partInfo?.partConfig,
                    prefab is CompoundPart, persistentId, partName, out data))
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

            }
            else
            {
                ghostLine.gameObject.SetActive(false);
            }

            return compoundPartInfo;
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
            bool raiseLightVisualOnly, bool raiseRcsVisualOnly)
        {
            meshCount = 0;
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

            LogPartCloneSummary(meshCount, skinnedCloned, partName, persistentId,
                nullMeshSkipped, damagedSkipped, depthMaskSkipped);

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

            // Detect jettison parts (shrouds/fairings) via cloneMap.
            jettisonInfo = ResolveJettisonTransforms(
                prefab, cloneMap, persistentId, partName, partNode);

            // Engine plates show only the selected mount pattern.
            ApplyDynamicNodeMeshVisibility(
                prefab.partInfo?.partConfig, partNode, modelNode.transform, persistentId, partName);

            // If the snapshot MODULE data shows the shroud is already jettisoned,
            // hide the transforms immediately. Continuation recordings (tree
            // promotions) skip seed events to avoid poisoning FindLastInterestingUT,
            // so the ghost would otherwise display the shroud until the first
            // playback event — which never comes for continuations (#290).
            if (ShouldHideJettisonTransformsAtBuild(jettisonInfo, partNode))
            {
                for (int ji = 0; ji < jettisonInfo.jettisonTransforms.Count; ji++)
                {
                    var jt = jettisonInfo.jettisonTransforms[ji];
                    if (jt != null) jt.gameObject.SetActive(false);
                }
                ParsekLog.VerboseRateLimited("GhostVisual",
                    $"jettison-hidden-{partName}-{persistentId}",
                    $"    Part '{partName}' pid={persistentId}: jettison transforms hidden " +
                    $"(isJettisoned=True in snapshot MODULE data)",
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
                    modelNode.transform, partName, persistentId);
            }

            // Detect light parts and clone Light components for ghost playback
            lightInfo = BuildLightGhostInfo(prefab, modelRoot, modelNode.transform, cloneMap,
                persistentId, partName, raiseLightVisualOnly);

            // Detect ModuleColorChanger: cabin lights (Pattern A) and heat shield char (Pattern B)
            // The prefab's own PART config is passed alongside the snapshot node because every
            // field this builder reads is config-only and so never survives into a persisted
            // snapshot node - GS6-GHOST-HAS-NO-COLORCHANGER-STATE. Same source the fairing
            // structure hider already uses two calls below.
            colorChangerInfos = BuildColorChangerInfos(
                partNode, prefab.partInfo?.partConfig, modelNode.transform, persistentId, partName);

            // Procedural fairings: stock-built panel shell + stock truss / cap visibility rule
            fairingInfo = BuildFairingVisual(partNode, prefab, modelNode.transform, persistentId, partName);
            if (fairingInfo != null)
                added = true;

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
        /// Scans part config for ModuleColorChanger instances and builds ColorChangerGhostInfo
        /// for each. Pattern A (toggleInFlight=True, _EmissiveColor) handles cabin lights.
        /// Pattern B (toggleInFlight=False, _BurnColor) handles heat shield ablation char.
        /// Returns null if no ColorChanger modules are detected.
        ///
        /// <paramref name="prefabPartConfig"/> is the prefab's own PART config
        /// (<c>AvailablePart.partPrefab.partInfo.partConfig</c>) and supplies the config-only
        /// fields a persisted snapshot node cannot carry - see
        /// <see cref="ResolveColorChangerConfigNodes"/>.
        /// </summary>
        internal static List<ColorChangerGhostInfo> BuildColorChangerInfos(
            ConfigNode partNode, ConfigNode prefabPartConfig, Transform ghostModelNode,
            uint persistentId, string partName)
        {
            if (partNode == null || ghostModelNode == null)
                return null;

            List<ConfigNode> colorChangerNodes =
                ResolveColorChangerConfigNodes(partNode, prefabPartConfig);
            if (colorChangerNodes.Count == 0)
                return null;

            List<ColorChangerGhostInfo> results = null;
            int skippedNoShaderProperty = 0;
            int skippedUnrecognisedPattern = 0;

            for (int m = 0; m < colorChangerNodes.Count; m++)
            {
                string shaderProperty = colorChangerNodes[m].GetValue("shaderProperty");
                if (string.IsNullOrEmpty(shaderProperty))
                {
                    skippedNoShaderProperty++;
                    continue;
                }

                string toggleStr = colorChangerNodes[m].GetValue("toggleInFlight");
                bool toggleInFlight = false;
                if (!string.IsNullOrEmpty(toggleStr))
                    bool.TryParse(toggleStr, out toggleInFlight);

                bool isCabinLight = toggleInFlight && shaderProperty == "_EmissiveColor";
                bool isAblationChar = !toggleInFlight && shaderProperty == "_BurnColor";

                if (!isCabinLight && !isAblationChar)
                {
                    skippedUnrecognisedPattern++;
                    continue;
                }

                // Evaluate color curves from config to get off/on colors
                // Pattern A: off = curves at t=0 (black), on = curves at t=1 (warm glow)
                // Pattern B: off = curves at t=0 (unburnt), on = curves at t=1 (fully charred)
                Color offColor = EvaluateColorCurves(colorChangerNodes[m], 0f);
                Color onColor = EvaluateColorCurves(colorChangerNodes[m], 1f);

                // Find renderers on the ghost model that have this shader property
                var renderers = ghostModelNode.GetComponentsInChildren<Renderer>(true);
                if (renderers == null || renderers.Length == 0)
                {
                    continue;
                }

                var materialStates = CollectColorChangerMaterials(
                    renderers, shaderProperty, offColor, onColor);

                if (materialStates.Count > 0)
                {
                    if (results == null)
                        results = new List<ColorChangerGhostInfo>();

                    var info = new ColorChangerGhostInfo
                    {
                        partPersistentId = persistentId,
                        shaderProperty = shaderProperty,
                        isCabinLight = isCabinLight,
                        materials = materialStates
                    };
                    results.Add(info);

                    // Initialize to off state
                    for (int i = 0; i < materialStates.Count; i++)
                    {
                        if (materialStates[i].material != null)
                            materialStates[i].material.SetColor(shaderProperty, offColor);
                    }

                }
            }

            // Batch counting convention: one summary per part, never one line per module.
            ParsekLog.VerboseRateLimited("GhostVisual", $"colorchanger-{partName}",
                $"ColorChanger resolve '{partName}' pid={persistentId}: " +
                $"modules={colorChangerNodes.Count} built={(results != null ? results.Count : 0)} " +
                $"noShaderProperty={skippedNoShaderProperty} " +
                $"unrecognisedPattern={skippedUnrecognisedPattern}", 60.0);

            return results;
        }

        /// <summary>
        /// Scans all renderers for materials with the given shader property, clones matching
        /// materials, and builds ColorChangerMaterialState entries. Swaps renderer material
        /// arrays in place for renderers that had any cloned materials.
        /// </summary>
        private static List<ColorChangerMaterialState> CollectColorChangerMaterials(
            Renderer[] renderers, string shaderProperty, Color offColor, Color onColor)
        {
            var materialStates = new List<ColorChangerMaterialState>();

            for (int r = 0; r < renderers.Length; r++)
            {
                Renderer renderer = renderers[r];
                if (renderer == null) continue;

                // Skip particle and trail renderers
                if (renderer is ParticleSystemRenderer || renderer is TrailRenderer)
                    continue;

                Material[] sourceMaterials = renderer.sharedMaterials;
                if (sourceMaterials == null || sourceMaterials.Length == 0)
                    continue;

                bool clonedAny = false;
                var cloned = new Material[sourceMaterials.Length];
                for (int mi = 0; mi < sourceMaterials.Length; mi++)
                {
                    Material source = sourceMaterials[mi];
                    if (source == null)
                    {
                        cloned[mi] = null;
                        continue;
                    }

                    if (!source.HasProperty(shaderProperty))
                    {
                        cloned[mi] = source;
                        continue;
                    }

                    Material materialClone = new Material(source);
                    cloned[mi] = materialClone;
                    clonedAny = true;

                    materialStates.Add(new ColorChangerMaterialState
                    {
                        material = materialClone,
                        offColor = offColor,
                        onColor = onColor
                    });
                }

                if (clonedAny)
                    renderer.materials = cloned;
            }

            return materialStates;
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

        /// <summary>
        /// Builds a ghost GameObject for a planted flag from the flag part prefab.
        /// Returns the ghost (initially inactive). Applies flag texture to the mesh_flag quad.
        /// </summary>
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

                string vesselName = !string.IsNullOrEmpty(evt.flagSiteName) ? evt.flagSiteName : "Flag";
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
