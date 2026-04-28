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

        [GameParameters.CustomParameterUI("Readable sidecar mirrors",
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

        /// <summary>
        /// Phase 1 of the ghost trajectory rendering pipeline (design doc
        /// §6.1 Stage 1, §17.3.1, §18 Phase 1). When true, ABSOLUTE-frame
        /// body-fixed ghost playback evaluates a Catmull-Rom smoothing
        /// spline instead of the legacy <c>BracketPointAtUT</c>. Default
        /// true; the flag exists so Phase 1 can ship behind a single rollout
        /// gate and so tests can exercise the legacy fall-through.
        /// </summary>
        [GameParameters.CustomParameterUI("Use smoothing splines",
            toolTip = "When on (Phase 1), absolute-frame ghost playback uses Catmull-Rom splines instead of bracketed nearest-sample lookup")]
        public bool useSmoothingSplines
        {
            get { return _useSmoothingSplines; }
            set
            {
                if (_useSmoothingSplines == value) return;
                bool prev = _useSmoothingSplines;
                _useSmoothingSplines = value;
                NotifyUseSmoothingSplinesChanged(prev, value);
                // Persist immediately so a user/debug flip survives a
                // save/load (or the rewind-load path that applies the
                // persistence layer over the .sfs-restored value). The
                // Record method is idempotent — when ApplyTo restores
                // from the store and assigns the property, the resulting
                // Record call short-circuits because the store already
                // matches.
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
                // See useSmoothingSplines comment above — persist immediately
                // so a user/debug flip survives a save/load cycle. Record is
                // idempotent so the ApplyTo-driven assignment is a no-op.
                ParsekSettingsPersistence.RecordUseAnchorCorrection(value);
            }
        }
        private bool _useAnchorCorrection = true;

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
    }
}
