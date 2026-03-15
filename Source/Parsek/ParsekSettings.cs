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

        public static ParsekSettings Current =>
            HighLogic.CurrentGame?.Parameters?.CustomParams<ParsekSettings>();
    }
}
