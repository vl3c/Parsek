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
            float min = GetMinSampleInterval(level);
            float max = GetMaxSampleInterval(level);
            float dir = GetVelocityDirThreshold(level);
            float spd = GetSpeedChangeThreshold(level);
            return $"Sampling: every {min:F2}\u2013{max:F1}s, {dir:F1}\u00b0 / {spd:F0}% thresholds";
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
    }
}
