using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Recorder sample density presets. Each level maps to a fixed set of
    /// adaptive-sampling thresholds (min/max interval, direction, speed).
    /// </summary>
    public enum SamplingDensity
    {
        Low = 0,
        Medium = 1,
        High = 2,
    }

    public class ParsekSettings : GameParameters.CustomParameterNode
    {
        private const string SamplingDensityKey = "samplingDensity";
        private const string LegacyMinSampleIntervalKey = "minSampleInterval";
        private const string LegacyMaxSampleIntervalKey = "maxSampleInterval";
        private const string LegacyVelocityDirThresholdKey = "velocityDirThreshold";
        private const string LegacySpeedChangeThresholdKey = "speedChangeThreshold";

        public override string Title => "Parsek";
        public override GameParameters.GameMode GameMode => GameParameters.GameMode.ANY;
        public override string Section => "Parsek";
        public override string DisplaySection => "Parsek";
        public override int SectionOrder => 1;
        public override bool HasPresets => false;

        [GameParameters.CustomParameterUI("Auto-record on launch",
            toolTip = "Automatically start recording when a vessel leaves the pad or runway")]
        public bool autoRecordOnLaunch = true;

        [GameParameters.CustomParameterUI("Auto-record on EVA",
            toolTip = "Automatically start recording when a kerbal goes EVA from the pad")]
        public bool autoRecordOnEva = true;

        [GameParameters.CustomParameterUI("Auto-record on first modification after switch",
            toolTip = "Automatically arm after switching to a real vessel and start recording on the first meaningful physical change")]
        public bool autoRecordOnFirstModificationAfterSwitch = true;

        [GameParameters.CustomParameterUI("Auto-merge recordings",
            toolTip = "When enabled, recordings are committed to the timeline automatically. When disabled, a confirmation dialog appears after each recording.")]
        public bool autoMerge = false;

        [GameParameters.CustomParameterUI("Verbose logging",
            toolTip = "When enabled, write detailed diagnostics to KSP.log (default for development)")]
        public bool verboseLogging = true;

        [GameParameters.CustomParameterUI("Ghost render tracing (Warning: huge logs)",
            toolTip = "When enabled, write detailed per-ghost render placement diagnostics to KSP.log. Leave off for normal playtests.")]
        public bool ghostRenderTracing = false;

        [GameParameters.CustomParameterUI("Readable sidecar mirrors (Warning: extra disk usage)",
            toolTip = "When enabled, also write human-readable .txt mirrors of recording sidecars for debugging and binary/text comparison")]
        public bool writeReadableSidecarMirrors = true;

        /// <summary>
        /// #388 — when false, Parsek ghost ProtoVessels and atmospheric ghost markers
        /// are hidden from the tracking station (both the vessel list and the map view).
        /// Toggles are picked up live via force-tick in <c>ParsekTrackingStation.Update</c>.
        /// </summary>
        [GameParameters.CustomParameterUI("Show ghosts in Tracking Station",
            toolTip = "When off, Parsek ghosts are hidden from the tracking station vessel list and map view")]
        public bool showGhostsInTrackingStation = true;

        [GameParameters.CustomParameterUI("Show committed-future overlays in stock UI",
            toolTip = "When on, stock R&D, Astronaut Complex, and Mission Control screens show actions already committed on the timeline")]
        public bool showCommittedFutureOverlays = true;

        [GameParameters.CustomParameterUI("Block player actions that conflict with committed timeline",
            toolTip = "When on, stock UI actions already committed by pending recordings are blocked to preserve timeline consistency")]
        public bool blockCommittedActions = true;

        /// <summary>
        /// Phase 1 of the ghost trajectory rendering pipeline (design doc
        /// §6.1 Stage 1, §17.3.1, §18 Phase 1). When true, Catmull-Rom
        /// smoothing splines are available to playback modes whose product
        /// gate explicitly allows spline-positioned rendering. Normal
        /// Watch/loop playback stays on section-frame lerp by default.
        /// </summary>
        [GameParameters.CustomParameterUI("Use smoothing splines",
            toolTip = "Developer gate for Catmull-Rom spline playback; normal Watch/loop playback remains linear by default")]
        public bool useSmoothingSplines
        {
            get { return _useSmoothingSplines; }
            set
            {
                if (_useSmoothingSplines == value) return;
                bool prev = _useSmoothingSplines;
                _useSmoothingSplines = value;
                NotifyUseSmoothingSplinesChanged(prev, value);
                // Persist only after ParsekSettingsPersistence.ApplyTo has
                // reconciled the store with live settings (PR #328 P2-A).
                // Pre-reconciliation, a setter call most likely comes from
                // KSP's GameParameters infrastructure deserializing the
                // .sfs node; recording that stale value would clobber the
                // user's persisted intent before ApplyTo can restore it.
                // ApplyTo's restoration assignment also lands here; it
                // happens after reconciledWithLiveSettings is set, so the
                // gate lets it through and the idempotent guard inside
                // RecordUseSmoothingSplines makes it a no-op.
                if (ParsekSettingsPersistence.IsReconciled)
                    ParsekSettingsPersistence.RecordUseSmoothingSplines(value);
            }
        }
        private bool _useSmoothingSplines = true;

        /// <summary>
        /// Phase 2 of the ghost trajectory rendering pipeline (design doc
        /// §6.3 Stage 3, §7.1, §18 Phase 2). When true, ghost siblings of an
        /// active re-fly target are rendered with an additive
        /// <c>AnchorCorrection</c> ε computed once at session-entry and held
        /// constant across the segment (Phase 2's single-anchor case).
        /// Default true; the flag exists so Phase 2 ships behind a single
        /// rollout gate parallel to <see cref="useSmoothingSplines"/>.
        /// </summary>
        [GameParameters.CustomParameterUI("Use anchor correction",
            toolTip = "When on (Phase 2), ghost siblings during a re-fly are rigid-translated by the recorded separation offset so they spawn aligned with the live vessel")]
        public bool useAnchorCorrection
        {
            get { return _useAnchorCorrection; }
            set
            {
                if (_useAnchorCorrection == value) return;
                bool prev = _useAnchorCorrection;
                _useAnchorCorrection = value;
                NotifyUseAnchorCorrectionChanged(prev, value);
                // See useSmoothingSplines comment above for the
                // reconciliation gate rationale (PR #328 P2-A).
                if (ParsekSettingsPersistence.IsReconciled)
                    ParsekSettingsPersistence.RecordUseAnchorCorrection(value);
            }
        }
        private bool _useAnchorCorrection = true;

        /// <summary>
        /// Phase 6 of the ghost trajectory rendering pipeline (design doc
        /// §18 Phase 6, §7.1 — §7.10, §7.11). Gates BOTH the commit-time
        /// emission of <see cref="Parsek.Rendering.AnchorCandidate"/> entries
        /// (via <c>AnchorCandidateBuilder.BuildAndStorePerSection</c>) AND
        /// the session-time DAG walk (via <c>AnchorPropagator.Run</c>). When
        /// off, the rendering pipeline collapses back to Phase 2 behaviour:
        /// LiveSeparation anchors are still computed (the Phase-2 path), but
        /// no other taxonomy entries (Dock/Merge, RELATIVE-boundary,
        /// OrbitalCheckpoint, SOI, BubbleEntry/Exit, Loop, SurfaceContinuous)
        /// are produced. Default true.
        ///
        /// <para>
        /// Two-flag rationale (vs. reusing <see cref="useAnchorCorrection"/>):
        /// <see cref="useAnchorCorrection"/> gates render-time consumption of
        /// any anchor in the <see cref="Parsek.Rendering.RenderSessionState"/>
        /// map; <see cref="useAnchorTaxonomy"/> gates whether non-LiveSeparation
        /// entries get into the map at all. Two flags decouple the rollout
        /// from regression bisection.
        /// </para>
        /// </summary>
        [GameParameters.CustomParameterUI("Use anchor taxonomy",
            toolTip = "When on (Phase 6), every anchor type from §7.1–§7.10 produces an AnchorCorrection and DAG propagation walks BranchPoint edges. Phase 2 LiveSeparation behaviour is unchanged.")]
        public bool useAnchorTaxonomy
        {
            get { return _useAnchorTaxonomy; }
            set
            {
                if (_useAnchorTaxonomy == value) return;
                bool prev = _useAnchorTaxonomy;
                _useAnchorTaxonomy = value;
                NotifyUseAnchorTaxonomyChanged(prev, value);
                if (ParsekSettingsPersistence.IsReconciled)
                    ParsekSettingsPersistence.RecordUseAnchorTaxonomy(value);
            }
        }
        private bool _useAnchorTaxonomy = true;

        /// <summary>
        /// Phase 5 of the ghost trajectory rendering pipeline (design doc §6.5
        /// / §10 / §18 Phase 5). When true, ghost peers that shared a physics
        /// bubble with a co-recorded primary at recording time render via the
        /// stored co-bubble offset trace (sub-meter relative geometry) instead
        /// of falling through to standalone Stages 1+2+3+4. Default false:
        /// off-by-default while the v0.10 playtest cycle evaluates whether
        /// standalone Absolute rendering is visually acceptable for Re-Fly
        /// stage separations without nearby formation peers.
        ///
        /// <para>
        /// The flag participates in the <c>.pann</c> ConfigurationHash (HR-10
        /// freshness): flipping it invalidates every cached <c>.pann</c> with
        /// co-bubble traces so a stale empty block can't masquerade as fresh.
        /// </para>
        /// </summary>
        [GameParameters.CustomParameterUI("Use co-bubble blend",
            toolTip = "When on (Phase 5), close-formation ghosts blend toward sub-meter offsets stored in the recording. Off (default during v0.10 playtest) → standalone Stages 1-4 only.")]
        public bool useCoBubbleBlend
        {
            get { return _useCoBubbleBlend; }
            set
            {
                if (_useCoBubbleBlend == value) return;
                bool prev = _useCoBubbleBlend;
                _useCoBubbleBlend = value;
                NotifyUseCoBubbleBlendChanged(prev, value);
                if (ParsekSettingsPersistence.IsReconciled)
                    ParsekSettingsPersistence.RecordUseCoBubbleBlend(value);
            }
        }
        private bool _useCoBubbleBlend = false;

        /// <summary>
        /// Rollback toggle (off by default) for the narrowed-gate re-fly
        /// Relative anchor selection. The DEFAULT behavior is now: while a
        /// re-fly is active, the recorder runs the nearest-search against
        /// out-of-tree candidates only (the
        /// <c>ReFlyAnchorSelection.FilterCandidatesForReFlyProvisional</c>
        /// filter drops every candidate in the same
        /// <see cref="RecordingTree"/> as the provisional). Re-fly with no
        /// nearby real anchor authors Absolute (PR 901's validated default);
        /// re-fly with a nearby real station / base / live loop anchor
        /// authors Relative-against-the-real-anchor.
        ///
        /// <para>When this toggle is ON, the recorder additionally skips
        /// the nearest-search entirely for re-fly provisionals: even an
        /// out-of-tree real anchor in range is ignored, and the recorder
        /// forces Absolute. Plus a Relative-to-Absolute downgrade fires in
        /// <c>FlightRecorder.RestoreTrackSectionAfterFalseAlarm</c>.
        /// Provided as a strict rollback path for the narrowed-gate
        /// change; expected to be deleted alongside the orphaned
        /// <see cref="ReFlyAnchorSelection.TryResolveReFlyProvisionalAnchor"/>
        /// and its apply helpers after one release of soak time.</para>
        ///
        /// <para>The setting does NOT participate in <c>.pann</c>
        /// ConfigurationHash because it only affects <c>.prec</c> authoring,
        /// not pannotation block generation, so flipping it does not
        /// invalidate cached <c>.pann</c> sidecars. See
        /// <c>docs/dev/plans/force-absolute-refly-provisional.md</c> (PR 901
        /// experiment design) and
        /// <c>docs/dev/plans/narrow-refly-relative-gate.md</c> (current
        /// default behavior).</para>
        /// </summary>
        [GameParameters.CustomParameterUI("Force Absolute for re-fly provisional (rollback)",
            toolTip = "Rollback path for the narrowed-gate default. Default behavior already authors Absolute for re-fly forks without a real out-of-tree anchor nearby; this toggle additionally forces Absolute even when a real station / base / live loop anchor IS in range. Off (default) keeps Relative-against-real-anchor where reachable; On forces fully Absolute. Flipping mid-recording produces a section boundary.")]
        public bool forceAbsoluteForReFlyProvisional
        {
            get { return _forceAbsoluteForReFlyProvisional; }
            set
            {
                if (_forceAbsoluteForReFlyProvisional == value) return;
                bool prev = _forceAbsoluteForReFlyProvisional;
                _forceAbsoluteForReFlyProvisional = value;
                // Gate BOTH the user-flip log and the persistence write on the
                // same IsReconciled latch. KSP's GameParameters.OnLoad calls
                // this setter to restore the field from .sfs on every scene /
                // quicksave load (a fresh ParsekSettings.Current instance, so
                // the backing field starts at the default `false`). That is a
                // state restore, not a user toggle, and must NOT log a
                // "False->True" Anchor flip line: the 2026-05-19 PR 901
                // validation playtest emitted 14 such spurious lines across
                // four minutes of play. The IsReconciled latch is the
                // existing per-cycle gate (ParsekSettings.OnLoad flips it
                // false before base.OnLoad, ApplyTo flips it true after the
                // external store overlay completes), so a real user toggle
                // through the UI lands in the IsReconciled==true window and
                // logs as expected.
                if (ParsekSettingsPersistence.IsReconciled)
                {
                    NotifyForceAbsoluteForReFlyProvisionalChanged(prev, value);
                    ParsekSettingsPersistence.RecordForceAbsoluteForReFlyProvisional(value);
                }
            }
        }
        private bool _forceAbsoluteForReFlyProvisional = false;

        /// <summary>
        /// Phase 8 of the ghost trajectory rendering pipeline (design doc
        /// §14, §18 Phase 8). When true, kraken-event single-frame
        /// teleports are rejected before the smoothing spline is fit so the
        /// spline does not deflect through physics-glitch samples. Off →
        /// the spline fits raw samples (legacy behaviour). Default true.
        ///
        /// <para>
        /// The flag participates in the <c>.pann</c> ConfigurationHash
        /// (HR-10 freshness): flipping it invalidates every cached
        /// <c>.pann</c> with outlier flags so a stale empty block can't
        /// masquerade as fresh.
        /// </para>
        /// </summary>
        [GameParameters.CustomParameterUI("Use outlier rejection",
            toolTip = "When on (Phase 8), kraken-event samples are rejected before smoothing. Off → spline fits raw samples including kraken spikes.")]
        public bool useOutlierRejection
        {
            get { return _useOutlierRejection; }
            set
            {
                if (_useOutlierRejection == value) return;
                bool prev = _useOutlierRejection;
                _useOutlierRejection = value;
                NotifyUseOutlierRejectionChanged(prev, value);
                if (ParsekSettingsPersistence.IsReconciled)
                    ParsekSettingsPersistence.RecordUseOutlierRejection(value);
            }
        }
        private bool _useOutlierRejection = true;

        /// <summary>
        /// Recorder sample density preset (0=Low, 1=Medium, 2=High).
        /// Replaces the four individual sampling sliders (minSampleInterval,
        /// maxSampleInterval, velocityDirThreshold, speedChangeThreshold).
        /// Serialized as int for ConfigNode round-trip.
        /// </summary>
        [GameParameters.CustomIntParameterUI("Recorder sample density", minValue = 0, maxValue = 2,
            stepSize = 1, displayFormat = "N0",
            toolTip = "Trajectory sampling preset. 0 = Low, 1 = Medium, 2 = High. " +
                      "The Parsek settings window shows labeled buttons and an exact threshold summary.")]
        public int samplingDensity = 1; // Medium

        public SamplingDensity SamplingDensityLevel
        {
            get => samplingDensity >= 0 && samplingDensity <= 2
                ? (SamplingDensity)samplingDensity
                : SamplingDensity.Medium;
            set => samplingDensity = (int)value;
        }

        // --- Derived sampling thresholds from preset ---

        public float minSampleInterval => GetMinSampleInterval(SamplingDensityLevel);
        public float maxSampleInterval => GetMaxSampleInterval(SamplingDensityLevel);
        public float velocityDirThreshold => GetVelocityDirThreshold(SamplingDensityLevel);
        public float speedChangeThreshold => GetSpeedChangeThreshold(SamplingDensityLevel);

        internal static float GetMinSampleInterval(SamplingDensity level) =>
            level == SamplingDensity.Low ? 0.5f
            : level == SamplingDensity.High ? 0.05f
            : 0.2f;

        internal static float GetMaxSampleInterval(SamplingDensity level) =>
            level == SamplingDensity.Low ? 8.0f
            : level == SamplingDensity.High ? 1.0f
            : 3.0f;

        internal static float GetVelocityDirThreshold(SamplingDensity level) =>
            level == SamplingDensity.Low ? 6.0f
            : level == SamplingDensity.High ? 0.5f
            : 2.0f;

        internal static float GetSpeedChangeThreshold(SamplingDensity level) =>
            level == SamplingDensity.Low ? 12.0f
            : level == SamplingDensity.High ? 1.0f
            : 5.0f;

        internal static string DensityLabel(SamplingDensity level) =>
            level == SamplingDensity.Low ? "Low"
            : level == SamplingDensity.High ? "High"
            : "Medium";

        internal static string DensityTooltip(SamplingDensity level) =>
            level == SamplingDensity.Low
                ? "Fewer samples \u2014 smaller files, less CPU. Trajectories may look angular during sharp maneuvers."
            : level == SamplingDensity.High
                ? "Dense sampling \u2014 smooth curves for cinematic recordings. Larger files."
            : "Balanced sampling for most flights.";

        internal static string DensitySummary(SamplingDensity level)
        {
            var ic = CultureInfo.InvariantCulture;
            float min = GetMinSampleInterval(level);
            float max = GetMaxSampleInterval(level);
            float dir = GetVelocityDirThreshold(level);
            float spd = GetSpeedChangeThreshold(level);
            return $"Sampling: every {min.ToString("F2", ic)}\u2013{max.ToString("F1", ic)}s, " +
                   $"{dir.ToString("F1", ic)}\u00b0 / {spd.ToString("F0", ic)}% thresholds";
        }

        /// <summary>
        /// Default launch-to-launch period in seconds (#381) for recordings with
        /// LoopTimeUnit.Auto. Must be &gt;= LoopTiming.MinCycleDuration. Overlap
        /// emerges when the period is shorter than the recording's duration.
        /// </summary>
        public float autoLoopIntervalSeconds = (float)LoopTiming.DefaultLoopIntervalSeconds;
        public int autoLoopTimeUnit = 0; // 0=Sec, 1=Min, 2=Hour

        [GameParameters.CustomFloatParameterUI("Ghost audio volume", minValue = 0f, maxValue = 1f,
            stepCount = 20, displayFormat = "P0",
            toolTip = "Volume multiplier for ghost vessel audio (engines, decouplers, explosions). 0 = muted.")]
        public float ghostAudioVolume = 0.7f;

        public LoopTimeUnit AutoLoopDisplayUnit
        {
            get => autoLoopTimeUnit == 1 ? LoopTimeUnit.Min
                 : autoLoopTimeUnit == 2 ? LoopTimeUnit.Hour
                 : LoopTimeUnit.Sec;
            set => autoLoopTimeUnit = value == LoopTimeUnit.Min ? 1
                 : value == LoopTimeUnit.Hour ? 2 : 0;
        }

        public static ParsekSettings Current =>
            CurrentOverrideForTesting ?? HighLogic.CurrentGame?.Parameters?.CustomParams<ParsekSettings>();

        /// <summary>
        /// Test-only override for <see cref="Current"/>. Lets unit tests exercise
        /// code paths that branch on a non-null <c>ParsekSettings.Current</c> without
        /// standing up a full <c>HighLogic.CurrentGame</c>. Production code must not
        /// set this.
        /// </summary>
        internal static ParsekSettings CurrentOverrideForTesting;

        public override void OnLoad(ConfigNode node)
        {
            // Reset the reconciliation latch BEFORE base.OnLoad. KSP's
            // GameParameters.OnLoad deserializes the .sfs node into our
            // property-decorated members, including the persistent
            // useSmoothingSplines / useAnchorCorrection setters. Those
            // setters check ParsekSettingsPersistence.IsReconciled to
            // decide whether to call Record*; if the latch is left over
            // from a prior load cycle (true), the stale .sfs value would
            // clobber the persistent store. Closing the gate here makes
            // every per-load cycle start fresh — ApplyTo flips it back
            // to true after ParsekScenario.OnLoad runs (PR #328 P2-A).
            ParsekSettingsPersistence.InvalidateReconciliation();

            base.OnLoad(node);

            SamplingDensity level = ResolveSamplingDensityFromConfig(
                node, out bool migratedFromLegacy, out string invalidSamplingDensityValue);
            SamplingDensityLevel = level;

            if (!string.IsNullOrEmpty(invalidSamplingDensityValue))
            {
                ParsekLog.Warn("Settings",
                    $"Invalid samplingDensity='{invalidSamplingDensityValue}' in config; " +
                    $"using {(migratedFromLegacy ? "legacy-derived" : "default")} " +
                    $"{DensityLabel(level)} preset");
            }

            if (migratedFromLegacy &&
                TryReadLegacySamplingThresholds(node,
                    out float legacyMin, out float legacyMax, out float legacyDir, out float legacySpeed))
            {
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Info("Settings",
                    $"Migrated legacy sampling thresholds ({legacyMin.ToString("F2", ic)}s/" +
                    $"{legacyMax.ToString("F1", ic)}s/{legacyDir.ToString("F1", ic)}\u00b0/" +
                    $"{legacySpeed.ToString("F0", ic)}%) to samplingDensity={DensityLabel(level)}");
            }
        }

        internal static SamplingDensity ResolveSamplingDensityFromConfig(
            ConfigNode node, out bool migratedFromLegacy, out string invalidSamplingDensityValue)
        {
            migratedFromLegacy = false;
            invalidSamplingDensityValue = null;

            if (TryReadSamplingDensityFromConfig(node, out SamplingDensity storedLevel))
                return storedLevel;

            invalidSamplingDensityValue = GetConfigValueOrNull(node, SamplingDensityKey);

            if (TryReadLegacySamplingThresholds(node,
                out float legacyMin, out float legacyMax, out float legacyDir, out float legacySpeed))
            {
                migratedFromLegacy = true;
                return DeriveSamplingDensityFromLegacyThresholds(
                    legacyMin, legacyMax, legacyDir, legacySpeed);
            }

            return SamplingDensity.Medium;
        }

        internal static SamplingDensity DeriveSamplingDensityFromLegacyThresholds(
            float minSampleInterval, float maxSampleInterval,
            float velocityDirThreshold, float speedChangeThreshold)
        {
            SamplingDensity bestLevel = SamplingDensity.Medium;
            double bestScore = double.MaxValue;

            foreach (SamplingDensity level in new[]
            {
                SamplingDensity.Low,
                SamplingDensity.Medium,
                SamplingDensity.High
            })
            {
                double score =
                    Square(NormalizeLegacyDistance(minSampleInterval, GetMinSampleInterval(level), 0.95)) +
                    Square(NormalizeLegacyDistance(maxSampleInterval, GetMaxSampleInterval(level), 9.0)) +
                    Square(NormalizeLegacyDistance(velocityDirThreshold, GetVelocityDirThreshold(level), 9.5)) +
                    Square(NormalizeLegacyDistance(speedChangeThreshold, GetSpeedChangeThreshold(level), 19.0));

                if (score < bestScore)
                {
                    bestScore = score;
                    bestLevel = level;
                }
            }

            return bestLevel;
        }

        internal static bool TryReadLegacySamplingThresholds(
            ConfigNode node,
            out float minSampleInterval,
            out float maxSampleInterval,
            out float velocityDirThreshold,
            out float speedChangeThreshold)
        {
            minSampleInterval = GetMinSampleInterval(SamplingDensity.Medium);
            maxSampleInterval = GetMaxSampleInterval(SamplingDensity.Medium);
            velocityDirThreshold = GetVelocityDirThreshold(SamplingDensity.Medium);
            speedChangeThreshold = GetSpeedChangeThreshold(SamplingDensity.Medium);

            if (node == null) return false;

            bool sawLegacyField = false;
            sawLegacyField |= TryReadFloat(node, LegacyMinSampleIntervalKey, ref minSampleInterval);
            sawLegacyField |= TryReadFloat(node, LegacyMaxSampleIntervalKey, ref maxSampleInterval);
            sawLegacyField |= TryReadFloat(node, LegacyVelocityDirThresholdKey, ref velocityDirThreshold);
            sawLegacyField |= TryReadFloat(node, LegacySpeedChangeThresholdKey, ref speedChangeThreshold);
            return sawLegacyField;
        }

        private static bool TryReadSamplingDensityFromConfig(ConfigNode node, out SamplingDensity level)
        {
            level = SamplingDensity.Medium;
            string rawValue = GetConfigValueOrNull(node, SamplingDensityKey);
            if (string.IsNullOrEmpty(rawValue))
                return false;

            if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                return false;
            if (parsed < 0 || parsed > 2)
                return false;

            level = (SamplingDensity)parsed;
            return true;
        }

        private static string GetConfigValueOrNull(ConfigNode node, string key)
        {
            if (node == null) return null;
            string value = node.GetValue(key);
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static bool TryReadFloat(ConfigNode node, string key, ref float value)
        {
            string rawValue = GetConfigValueOrNull(node, key);
            if (rawValue == null)
                return false;

            if (float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                value = parsed;
            return true;
        }

        private static double NormalizeLegacyDistance(float actual, float preset, double range)
            => (actual - preset) / range;

        private static double Square(double value) => value * value;

        /// <summary>
        /// Emits a single Pipeline-Smoothing log line when
        /// <see cref="useSmoothingSplines"/> flips. Phase 1 spec (design doc
        /// §19.2 Stage 1 row "Settings flag flip") requires Info-level
        /// visibility for the toggle so a developer can see the rollout-gate
        /// state in KSP.log. UI / settings code should call this at the
        /// assignment site once T6 wires the live toggle.
        /// </summary>
        internal static void NotifyUseSmoothingSplinesChanged(bool oldValue, bool newValue)
        {
            if (oldValue == newValue) return;
            ParsekLog.Info("Pipeline-Smoothing", $"useSmoothingSplines: {oldValue}->{newValue}");
        }

        /// <summary>
        /// Emits a single Pipeline-Anchor log line when
        /// <see cref="useAnchorCorrection"/> flips. Phase 2 spec (design doc
        /// §19.2 Stage 3 row, §18 Phase 2) requires Info-level visibility for
        /// the rollout gate so a developer can attribute a visual artifact to
        /// the toggle moment in KSP.log. UI / settings code should call this
        /// at the assignment site once T6 wires the live toggle.
        /// </summary>
        internal static void NotifyUseAnchorCorrectionChanged(bool oldValue, bool newValue)
        {
            if (oldValue == newValue) return;
            ParsekLog.Info("Pipeline-Anchor", $"useAnchorCorrection: {oldValue}->{newValue}");
        }

        /// <summary>
        /// Emits a single Pipeline-Anchor log line when
        /// <see cref="useAnchorTaxonomy"/> flips. Phase 6 spec (design doc
        /// §19.2 Stage 3 / Stage 3b row, §18 Phase 6) requires Info-level
        /// visibility for the rollout gate so a developer can attribute a
        /// visual artifact to the toggle moment in KSP.log. Mirrors the
        /// Phase 2 <see cref="NotifyUseAnchorCorrectionChanged"/> contract
        /// line-for-line.
        /// </summary>
        internal static void NotifyUseAnchorTaxonomyChanged(bool oldValue, bool newValue)
        {
            if (oldValue == newValue) return;
            ParsekLog.Info("Pipeline-Anchor", $"useAnchorTaxonomy: {oldValue}->{newValue}");
        }

        /// <summary>
        /// Emits a single Pipeline-CoBubble log line when
        /// <see cref="useCoBubbleBlend"/> flips. Phase 5 spec (design doc
        /// §19.2 Stage 5 row "Settings flag flip") requires Info-level
        /// visibility for the rollout gate so a developer can attribute a
        /// visual artifact to the toggle moment in KSP.log.
        /// </summary>
        internal static void NotifyUseCoBubbleBlendChanged(bool oldValue, bool newValue)
        {
            if (oldValue == newValue) return;
            ParsekLog.Info("Pipeline-CoBubble", $"useCoBubbleBlend: {oldValue}->{newValue}");
        }

        /// <summary>
        /// Emits a single Pipeline-Outlier log line when
        /// <see cref="useOutlierRejection"/> flips. Phase 8 spec (design doc
        /// §19.2 Outlier Rejection row "Settings flag flip") requires
        /// Info-level visibility for the rollout gate so a developer can
        /// attribute a visual artifact to the toggle moment in KSP.log.
        /// </summary>
        internal static void NotifyUseOutlierRejectionChanged(bool oldValue, bool newValue)
        {
            if (oldValue == newValue) return;
            ParsekLog.Info("Pipeline-Outlier", $"useOutlierRejection: {oldValue}->{newValue}");
        }

        /// <summary>
        /// Emits a single Anchor log line when
        /// <see cref="forceAbsoluteForReFlyProvisional"/> flips. Lets a
        /// developer attribute a visual artifact to the toggle moment in
        /// KSP.log when A/B testing the experiment.
        /// </summary>
        internal static void NotifyForceAbsoluteForReFlyProvisionalChanged(bool oldValue, bool newValue)
        {
            if (oldValue == newValue) return;
            ParsekLog.Info("Anchor", $"forceAbsoluteForReFlyProvisional: {oldValue}->{newValue}");
        }
    }
}
