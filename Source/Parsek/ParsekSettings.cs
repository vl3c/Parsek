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
        /// LoopTimeUnit.Auto. Must be &gt;= GhostPlaybackLogic.MinCycleDuration. Overlap
        /// emerges when the period is shorter than the recording's duration.
        /// </summary>
        public float autoLoopIntervalSeconds = 10.0f;
        public int autoLoopTimeUnit = 0; // 0=Sec, 1=Min, 2=Hour

        [GameParameters.CustomFloatParameterUI("Ghost audio volume", minValue = 0f, maxValue = 1f,
            stepCount = 20, displayFormat = "P0",
            toolTip = "Volume multiplier for ghost vessel audio (engines, decouplers, explosions). 0 = muted.")]
        public float ghostAudioVolume = 0.7f;

        // Ghost camera cutoff distance in km — watch mode auto-exits beyond this.
        public float ghostCameraCutoffKm = DistanceThresholds.GhostFlight.DefaultWatchCameraCutoffKm;

        public LoopTimeUnit AutoLoopDisplayUnit
        {
            get => autoLoopTimeUnit == 1 ? LoopTimeUnit.Min
                 : autoLoopTimeUnit == 2 ? LoopTimeUnit.Hour
                 : LoopTimeUnit.Sec;
            set => autoLoopTimeUnit = value == LoopTimeUnit.Min ? 1
                 : value == LoopTimeUnit.Hour ? 2 : 0;
        }

        public static ParsekSettings Current =>
            HighLogic.CurrentGame?.Parameters?.CustomParams<ParsekSettings>();

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
    }
}
