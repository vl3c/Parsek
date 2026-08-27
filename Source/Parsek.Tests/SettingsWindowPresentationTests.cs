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
            // Covers only what the simplified window still draws (the 2026-08-27
            // settings simplification retired the Recording / Stock UI sections and
            // the hidden/hardwired settings; the Defaults button no longer touches
            // those).
            SettingsWindowPresentation.SettingsDefaults defaults =
                SettingsWindowPresentation.BuildDefaults();

            Assert.True(defaults.VerboseLogging);
            Assert.True(defaults.WriteReadableSidecarMirrors);
            Assert.True(defaults.ShowRouteLines);
            Assert.Equal(SamplingDensity.Medium, defaults.SamplingDensityLevel);
            Assert.Equal((double)(float)LoopTiming.DefaultLoopIntervalSeconds, (double)defaults.AutoLoopIntervalSeconds, 6);
            Assert.Equal(LoopTimeUnit.Sec, defaults.AutoLoopDisplayUnit);
        }

        /// <summary>
        /// The hidden-but-kept settings (auto-record trio, autoMerge,
        /// forceFaithfulLoopPlayback) no longer appear in BuildDefaults - their
        /// shipping value IS the <see cref="ParsekSettings"/> field initializer, and
        /// the harness command seam is the only writer. Pin the hardwired values so
        /// an accidental default flip fails a test instead of silently changing the
        /// player-facing behavior.
        /// </summary>
        [Fact]
        public void HiddenSettings_FieldDefaults_ArePinned()
        {
            var s = new ParsekSettings();
            Assert.True(s.autoRecordOnLaunch);
            Assert.True(s.autoRecordOnEva);
            Assert.True(s.autoRecordOnFirstModificationAfterSwitch);
            Assert.True(s.autoMerge);
            Assert.False(s.forceFaithfulLoopPlayback);
        }
    }
}
