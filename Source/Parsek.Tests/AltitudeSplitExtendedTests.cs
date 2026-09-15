using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class AltitudeSplitExtendedTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public AltitudeSplitExtendedTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // --- Simulated Mun descent: points from 100km down to 0km ---

        [Fact]
        public void SimulatedMunDescent_SplitsAtThreshold()
        {
            // Mun: radius 200km, threshold = 30km
            double threshold = FlightRecorder.ComputeApproachAltitude(200000);
            Assert.Equal(30000, threshold);

            // Simulate descent: check each 5km step
            bool wasAbove = true;
            bool pending = false;
            double pendingUT = 0;
            double splitUT = -1;

            for (double alt = 100000; alt >= 0; alt -= 5000)
            {
                double ut = (100000 - alt) / 1000; // 1 km/s descent, UT from 0 to 100

                bool nowAbove = alt >= threshold;
                if (nowAbove != wasAbove && !pending)
                {
                    // Start hysteresis timer
                    pending = true;
                    pendingUT = ut;
                }

                if (pending && FlightRecorder.ShouldSplitAtAltitudeBoundary(
                    alt, threshold, wasAbove, pending, pendingUT, ut))
                {
                    splitUT = ut;
                    break;
                }
            }

            // Should have split somewhere around 30km - hysteresisMeters
            Assert.True(splitUT > 0, "Expected split during Mun descent");
            // At 5km steps, we cross 30km at alt=25000 (step past by 5000m > 1000m hysteresis)
            // Timer started at alt=25000 (ut=75), confirmed at alt=20000 (ut=80) — 5s elapsed > 3s hysteresis
            Assert.True(splitUT <= 85, $"Split should occur within ~85s, got {splitUT}");
        }

        [Fact]
        public void SimulatedMunAscent_SplitsAtThreshold()
        {
            double threshold = FlightRecorder.ComputeApproachAltitude(200000);

            bool wasAbove = false; // start below threshold
            bool pending = false;
            double pendingUT = 0;
            double splitUT = -1;

            for (double alt = 0; alt <= 100000; alt += 5000)
            {
                double ut = alt / 1000;

                bool nowAbove = alt >= threshold;
                if (nowAbove != wasAbove && !pending)
                {
                    pending = true;
                    pendingUT = ut;
                }

                if (pending && FlightRecorder.ShouldSplitAtAltitudeBoundary(
                    alt, threshold, wasAbove, pending, pendingUT, ut))
                {
                    splitUT = ut;
                    break;
                }
            }

            Assert.True(splitUT > 0, "Expected split during Mun ascent");
        }

        // --- Stock airless body threshold coverage ---

        [Theory]
        [InlineData(200000, 30000)]      // Mun
        [InlineData(60000, 9000)]         // Minmus
        [InlineData(600000, 90000)]       // Tylo
        [InlineData(13000, 5000)]         // Gilly (floor clamp)
        [InlineData(130000, 19500)]       // Ike
        [InlineData(138000, 20700)]       // Dres
        [InlineData(250000, 37500)]       // Moho
        [InlineData(210000, 31500)]       // Eeloo
        [InlineData(65000, 9750)]         // Bop
        [InlineData(44000, 6600)]         // Pol
        public void StockAirlessBodies_ThresholdValues(double bodyRadius, double expectedThreshold)
        {
            double threshold = FlightRecorder.ComputeApproachAltitude(bodyRadius);
            Assert.Equal(expectedThreshold, threshold);
        }

        // --- Fresh-recorder altitude flag defaults ---

        [Fact]
        public void FreshRecorder_AltitudeFlagsDefaultFalse()
        {
            // The name this cell used to carry (AltitudeBoundaryConfirmed_LogsCorrectly)
            // claimed the confirm log; nothing here logs and nothing crosses a boundary.
            // What it can prove is the field defaults, and those are load-bearing:
            // ParsekFlight's altitude-phase split early-returns on
            // !recorder.AltitudeBoundaryCrossed and then reads DescendedBelowThreshold to
            // pick the phase, so a fresh recorder defaulting either flag to true would
            // split a phase on a flight that never crossed anything.
            //
            // The confirm path itself (CheckAltitudeBoundary -> AltitudeBoundaryCrossed =
            // true plus the "Altitude boundary confirmed" Info line) needs a live Vessel
            // with a mainBody, an altitude and a UT, so it stays out of the headless
            // suite; its pure decision half is covered by ShouldSplitAtAltitudeBoundary
            // above and in AltitudeSplitTests.
            var recorder = new FlightRecorder();
            Assert.False(recorder.AltitudeBoundaryCrossed);
            Assert.False(recorder.DescendedBelowThreshold);

            // The not-recording guard must not set either flag either: this is the state
            // every physics frame of a stopped recorder lands in.
            recorder.CheckAltitudeBoundary(null);
            Assert.False(recorder.AltitudeBoundaryCrossed);
            Assert.False(recorder.DescendedBelowThreshold);
        }

        // --- Phase tagging for "approach" label ---

        [Fact]
        public void GetSegmentPhaseLabel_ApproachPhase()
        {
            var rec = new Recording
            {
                SegmentPhase = "approach",
                SegmentBodyName = "Mun"
            };
            Assert.Equal("Mun approach", RecordingStore.GetSegmentPhaseLabel(rec));
        }

        [Fact]
        public void GetSegmentPhaseLabel_ApproachWithoutBody()
        {
            var rec = new Recording { SegmentPhase = "approach" };
            Assert.Equal("approach", RecordingStore.GetSegmentPhaseLabel(rec));
        }

        // --- ClearBoundaryFlags clears altitude state ---

        [Fact]
        public void ClearBoundaryFlags_ClearsAltitudeState()
        {
            var recorder = new FlightRecorder();
            // Can't set the properties directly (private set), but verify they default to false
            // and ClearBoundaryFlags doesn't throw
            recorder.ClearBoundaryFlags();
            Assert.False(recorder.AltitudeBoundaryCrossed);
            Assert.False(recorder.DescendedBelowThreshold);

            Assert.Contains(logLines, l => l.Contains("Boundary flags cleared"));
        }
    }
}
