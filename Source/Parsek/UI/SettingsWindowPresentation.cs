namespace Parsek
{
    /// <summary>
    /// Pure rules shared by the Settings window edit fields and Defaults button.
    /// Keeps IMGUI code focused on layout, persistence, and logging.
    /// </summary>
    internal static class SettingsWindowPresentation
    {
        internal struct AutoLoopEditResolution
        {
            internal double RequestedSeconds;
            internal float AppliedSeconds;
            internal bool WasClamped;
        }

        internal struct SettingsDefaults
        {
            internal bool AutoRecordOnLaunch;
            internal bool AutoRecordOnEva;
            internal bool AutoMerge;
            internal bool VerboseLogging;
            internal bool WriteReadableSidecarMirrors;
            internal SamplingDensity SamplingDensityLevel;
            internal float AutoLoopIntervalSeconds;
            internal LoopTimeUnit AutoLoopDisplayUnit;
            internal bool ShowGhostsInTrackingStation;
        }

        internal static bool TryResolveAutoLoopEdit(
            string text,
            LoopTimeUnit unit,
            out AutoLoopEditResolution resolution)
        {
            resolution = default;

            if (!ParsekUI.TryParseLoopInput(text, unit, out double parsed) || parsed < 0)
                return false;

            double requestedSeconds = ParsekUI.ConvertToSeconds(parsed, unit);
            bool wasClamped = requestedSeconds < LoopTiming.MinCycleDuration;

            resolution = new AutoLoopEditResolution
            {
                RequestedSeconds = requestedSeconds,
                AppliedSeconds = (float)(wasClamped ? LoopTiming.MinCycleDuration : requestedSeconds),
                WasClamped = wasClamped
            };
            return true;
        }

        internal static SettingsDefaults BuildDefaults()
        {
            return new SettingsDefaults
            {
                AutoRecordOnLaunch = true,
                AutoRecordOnEva = true,
                AutoMerge = false,
                VerboseLogging = true,
                WriteReadableSidecarMirrors = true,
                SamplingDensityLevel = SamplingDensity.Medium,
                AutoLoopIntervalSeconds = (float)LoopTiming.DefaultLoopIntervalSeconds,
                AutoLoopDisplayUnit = LoopTimeUnit.Sec,
                ShowGhostsInTrackingStation = true
            };
        }
    }
}
