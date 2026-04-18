using System.Globalization;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Regression tests for #412. The RecordingBuilder's <c>WithLoopPlayback</c> previously
    /// defaulted <c>intervalSeconds</c> to <c>0.0</c> (valid pre-#381 as "relaunch with no
    /// gap"), which post-#381 is below <c>MinCycleDuration</c> and fires
    /// <c>ResolveLoopInterval</c>'s clamp warning on every frame. The builder now auto-derives
    /// the period from trajectory duration so every fixture that reaches a save carries a real
    /// period. Covers the three emission paths (flat .sfs <c>Build</c>, v3-metadata
    /// <c>BuildV3Metadata</c>, and <c>ScenarioWriter.BuildRecording</c> via
    /// <c>GetLoopIntervalSeconds</c>).
    /// </summary>
    public class RecordingBuilderLoopIntervalTests
    {
        private static double ParseInterval(ConfigNode node)
        {
            string raw = node.GetValue("loopIntervalSeconds");
            Assert.NotNull(raw);
            return double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        [Fact]
        public void LoopEnabled_ZeroInterval_AutoDerivesFromTrajectoryDuration()
        {
            var b = new RecordingBuilder("Pad Walk")
                .WithLoopPlayback(loop: true, intervalSeconds: 0.0)
                .AddPoint(ut: 100, lat: 0, lon: 0, alt: 0)
                .AddPoint(ut: 124, lat: 0, lon: 0, alt: 0);

            // Derived period equals trajectory duration (24 s) — the UI's seamless-loop default.
            Assert.Equal(24.0, b.GetLoopIntervalSeconds());
            Assert.Equal(24.0, ParseInterval(b.Build()));
            Assert.Equal(24.0, ParseInterval(b.BuildV3Metadata()));

            // Raw field is preserved for callers that want to inspect what was set.
            Assert.Equal(0.0, b.GetRawLoopIntervalSeconds());
        }

        [Fact]
        public void LoopEnabled_ExplicitValidInterval_PassedThroughUnchanged()
        {
            var b = new RecordingBuilder("KSC Hopper")
                .WithLoopPlayback(loop: true, intervalSeconds: 90.0)
                .AddPoint(ut: 0, lat: 0, lon: 0, alt: 0)
                .AddPoint(ut: 30, lat: 0, lon: 0, alt: 0);

            Assert.Equal(90.0, b.GetLoopIntervalSeconds());
            Assert.Equal(90.0, ParseInterval(b.Build()));
            Assert.Equal(90.0, ParseInterval(b.BuildV3Metadata()));
        }

        [Fact]
        public void LoopEnabled_ZeroInterval_EmptyTrajectory_FallsBackToDefault()
        {
            var b = new RecordingBuilder("Empty")
                .WithLoopPlayback(loop: true, intervalSeconds: 0.0);

            // No points → builder can't derive duration → DefaultLoopIntervalSeconds (30 s).
            Assert.Equal(GhostPlaybackLogic.DefaultLoopIntervalSeconds, b.GetLoopIntervalSeconds());
            Assert.Equal(GhostPlaybackLogic.DefaultLoopIntervalSeconds, ParseInterval(b.Build()));
        }

        [Fact]
        public void LoopEnabled_ZeroInterval_ShortTrajectory_FallsBackToDefault()
        {
            // Trajectory duration of 0.5 s is still below MinCycleDuration, so the
            // derived value is rejected in favour of DefaultLoopIntervalSeconds.
            var b = new RecordingBuilder("Brief")
                .WithLoopPlayback(loop: true, intervalSeconds: 0.0)
                .AddPoint(ut: 10.0, lat: 0, lon: 0, alt: 0)
                .AddPoint(ut: 10.5, lat: 0, lon: 0, alt: 0);

            Assert.Equal(GhostPlaybackLogic.DefaultLoopIntervalSeconds, b.GetLoopIntervalSeconds());
        }

        [Fact]
        public void LoopDisabled_ZeroInterval_StoredAsIs()
        {
            // When loop is off the stored value is irrelevant at runtime (resolver never
            // consults it), so leave the raw field untouched for round-trip fidelity.
            var b = new RecordingBuilder("NonLoop")
                .WithLoopPlayback(loop: false, intervalSeconds: 0.0)
                .AddPoint(ut: 0, lat: 0, lon: 0, alt: 0)
                .AddPoint(ut: 60, lat: 0, lon: 0, alt: 0);

            Assert.Equal(0.0, b.GetLoopIntervalSeconds());
            Assert.Equal(0.0, ParseInterval(b.Build()));
        }
    }
}
