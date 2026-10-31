using System.Globalization;
using Parsek;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Headless guards for the <c>standalone-loop-map</c> preset behind lanes SLM-1, SLM-2 and
    /// SLM-1T (<see cref="StandaloneLoopMapFixture"/>). Each cell names the lane property it
    /// protects, so a fixture drift that would make a flight vacuous (a loop that overlaps
    /// itself, a stop that lands in the wrong phase, an id the in-game cell cannot find) reds
    /// here instead of costing a KSP boot.
    /// </summary>
    public class StandaloneLoopMapFixtureTests
    {
        // The three lanes' WarpToUT stops (harness/scenarios/SLM-*.toml).
        private const double Slm1ReplayUT = 4355.0;
        private const double Slm2PauseUT = 6555.0;
        private const double Slm1TReplayUT = 8455.0;

        private static RecordingBuilder Build()
            => StandaloneLoopMapFixture.BuildRecording(StandaloneLoopMapFixture.HostSaveUT);

        // The loaded recording's EndUT is its orbit segment's end (Recording.TryGetActualTrajectoryBounds
        // takes the segments), not the builder's last point: the fixture has one point at the start.
        private static double EndUT(RecordingBuilder b)
        {
            ConfigNode seg = b.Build().GetNode("ORBIT_SEGMENT");
            Assert.NotNull(seg);
            return double.Parse(seg.GetValue("endUT"), CultureInfo.InvariantCulture);
        }

        [Fact]
        public void TheInGameCellLooksUpThePresetsRecordingId()
        {
            Assert.Equal(StandaloneLoopMapFixture.RecordingId,
                Parsek.InGameTests.StandaloneLoopMapInGameTests.PresetRecordingId);
        }

        [Fact]
        public void TheLoopNeverOverlapsItself_SoTheStandaloneSeamOwnsIt()
        {
            RecordingBuilder b = Build();
            double duration = EndUT(b) - b.GetStartUT();
            Assert.Equal(StandaloneLoopMapFixture.DurationSeconds, duration, 6);
            Assert.True(b.GetLoopPlayback());
            double period = b.GetLoopIntervalSeconds();
            Assert.Equal(StandaloneLoopMapFixture.LoopPeriodSeconds, period, 6);
            Assert.False(GhostPlaybackLogic.IsOverlapLoop(period, duration));
            Assert.True(b.GetStartUT() > StandaloneLoopMapFixture.HostSaveUT);
        }

        [Fact]
        public void TheMetadataKeepsTheLoopThroughTheLoadSanitizer()
        {
            ConfigNode meta = Build().BuildV3Metadata();
            Assert.Equal("True", meta.GetValue("loopPlayback"));
            Assert.Equal("LaunchPad", meta.GetValue("launchSiteName"));
            Assert.Equal(StandaloneLoopMapFixture.LoopPeriodSeconds,
                double.Parse(meta.GetValue("loopIntervalSeconds"), CultureInfo.InvariantCulture), 9);
            Assert.Equal(((int)TerminalState.Orbiting).ToString(CultureInfo.InvariantCulture),
                meta.GetValue("terminalState"));
            Assert.Null(meta.GetValue("loopTimeUnit")); // Sec: no auto queue
            Assert.True(Recording.IsLoopableRecording(new Recording { LaunchSiteName = "LaunchPad" }));
        }

        [Theory]
        [InlineData(Slm1ReplayUT, 1L, false)]
        [InlineData(Slm2PauseUT, 1L, true)]
        [InlineData(Slm1TReplayUT, 2L, false)]
        public void EachLaneStopLandsInThePhaseItsSpecPins(double liveUT, long expectedCycle, bool expectedPause)
        {
            RecordingBuilder b = Build();
            double start = b.GetStartUT();
            double duration = EndUT(b) - start;
            double period = b.GetLoopIntervalSeconds();
            long cycle = (long)System.Math.Floor((liveUT - start) / period);
            double phase = (liveUT - start) - cycle * period;
            Assert.Equal(expectedCycle, cycle);
            Assert.Equal(expectedPause, phase >= duration);
            // At least 200 s from either edge, so the cell's 1 s settle wait and a slow scene
            // load cannot carry the read across a run boundary.
            Assert.True(System.Math.Abs(phase - duration) > 200.0 && phase > 200.0,
                "stop at UT " + liveUT.ToString(CultureInfo.InvariantCulture)
                + " is within 200 s of a run edge (phase " + phase.ToString("F1", CultureInfo.InvariantCulture) + ")");
            // The stop is far from the live clock's own position on the orbit: the wrong clock
            // (the live UT) would put the ghost at least 10 deg away on this ~1958 s orbit.
            if (!expectedPause)
            {
                double orbitPeriod = 2.0 * System.Math.PI * System.Math.Sqrt(
                    System.Math.Pow(StandaloneLoopMapFixture.SemiMajorAxisMeters, 3) / 3.5316e12);
                double shiftFraction = (cycle * period / orbitPeriod) % 1.0;
                double shiftDeg = System.Math.Min(shiftFraction, 1.0 - shiftFraction) * 360.0;
                Assert.True(shiftDeg > 10.0, "the loop shift is only " + shiftDeg.ToString("F1") + " deg of orbit");
            }
        }
    }
}
