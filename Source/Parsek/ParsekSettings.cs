namespace Parsek
{
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

        [GameParameters.CustomFloatParameterUI("Min sample interval (s)", minValue = 0.05f, maxValue = 1f,
            stepCount = 20, displayFormat = "F2",
            toolTip = "Minimum seconds between trajectory samples — caps sample rate during slow/jittery motion (e.g. EVA on surface)")]
        public float minSampleInterval = 0.2f;

        [GameParameters.CustomFloatParameterUI("Max sample interval (s)", minValue = 1f, maxValue = 10f,
            stepCount = 19, displayFormat = "F1",
            toolTip = "Maximum seconds between trajectory samples")]
        public float maxSampleInterval = 3.0f;

        [GameParameters.CustomFloatParameterUI("Direction threshold (\u00b0)", minValue = 0.5f, maxValue = 10f,
            stepCount = 20, displayFormat = "F1",
            toolTip = "Velocity direction change (degrees) that triggers a new sample")]
        public float velocityDirThreshold = 2.0f;

        [GameParameters.CustomFloatParameterUI("Speed threshold (%)", minValue = 1f, maxValue = 20f,
            stepCount = 20, displayFormat = "F0",
            toolTip = "Speed change (percent) that triggers a new sample")]
        public float speedChangeThreshold = 5.0f;

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
