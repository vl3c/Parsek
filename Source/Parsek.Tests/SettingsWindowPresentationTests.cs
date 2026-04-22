using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the pure Settings window edit/default rules extracted from IMGUI draw code.
    /// </summary>
    public class SettingsWindowPresentationTests
    {
        [Fact]
        public void TryResolveAutoLoopEdit_SecondsBelowMinimum_ClampsToMinCycleDuration()
        {
            bool ok = SettingsWindowPresentation.TryResolveAutoLoopEdit(
                "3",
                LoopTimeUnit.Sec,
                out SettingsWindowPresentation.AutoLoopEditResolution resolution);

            Assert.True(ok);
            Assert.True(resolution.WasClamped);
            Assert.Equal(3.0, resolution.RequestedSeconds, 6);
            Assert.Equal((double)(float)LoopTiming.MinCycleDuration, (double)resolution.AppliedSeconds, 6);
        }

        [Fact]
        public void TryResolveAutoLoopEdit_MinutesInput_PreservesFractionWithoutClamp()
        {
            bool ok = SettingsWindowPresentation.TryResolveAutoLoopEdit(
                "1.5",
                LoopTimeUnit.Min,
                out SettingsWindowPresentation.AutoLoopEditResolution resolution);

            Assert.True(ok);
            Assert.False(resolution.WasClamped);
            Assert.Equal(90.0, resolution.RequestedSeconds, 6);
            Assert.Equal(90.0, (double)resolution.AppliedSeconds, 6);
        }

        [Theory]
        [InlineData("abc", LoopTimeUnit.Sec)]
        [InlineData("-5", LoopTimeUnit.Sec)]
        [InlineData("-0.5", LoopTimeUnit.Min)]
        public void TryResolveAutoLoopEdit_InvalidOrNegativeInput_ReturnsFalse(
            string text,
            LoopTimeUnit unit)
        {
            bool ok = SettingsWindowPresentation.TryResolveAutoLoopEdit(
                text,
                unit,
                out SettingsWindowPresentation.AutoLoopEditResolution resolution);

            Assert.False(ok);
            Assert.Equal(0.0, resolution.RequestedSeconds, 6);
            Assert.Equal(0.0, (double)resolution.AppliedSeconds, 6);
            Assert.False(resolution.WasClamped);
        }

        [Fact]
        public void BuildDefaults_MatchesSettingsWindowResetValues()
        {
            SettingsWindowPresentation.SettingsDefaults defaults =
                SettingsWindowPresentation.BuildDefaults();

            Assert.True(defaults.AutoRecordOnLaunch);
            Assert.True(defaults.AutoRecordOnEva);
            Assert.False(defaults.AutoMerge);
            Assert.True(defaults.VerboseLogging);
            Assert.True(defaults.WriteReadableSidecarMirrors);
            Assert.Equal(SamplingDensity.Medium, defaults.SamplingDensityLevel);
            Assert.Equal((double)(float)LoopTiming.DefaultLoopIntervalSeconds, (double)defaults.AutoLoopIntervalSeconds, 6);
            Assert.Equal(LoopTimeUnit.Sec, defaults.AutoLoopDisplayUnit);
            Assert.True(defaults.ShowGhostsInTrackingStation);
        }
    }
}
