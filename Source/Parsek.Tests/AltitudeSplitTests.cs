using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class AltitudeSplitTests
    {
        // Mun: radius 200km, threshold = 200000 * 0.15 = 30000m
        private const double MunRadius = 200000.0;
        private const double MunThreshold = 30000.0;

        [Fact]
        public void ZeroThreshold_ReturnsFalse()
        {
            bool result = FlightRecorder.ShouldSplitAtAltitudeBoundary(
                altitude: 10000, threshold: 0,
                wasAbove: true, pendingCross: true, pendingUT: 0, currentUT: 10);
            Assert.False(result);
        }

        [Fact]
        public void NegativeThreshold_ReturnsFalse()
        {
            bool result = FlightRecorder.ShouldSplitAtAltitudeBoundary(
                altitude: 10000, threshold: -1000,
                wasAbove: true, pendingCross: true, pendingUT: 0, currentUT: 10);
            Assert.False(result);
        }

        [Fact]
        public void SameSide_AboveThreshold_ReturnsFalse()
        {
            // Still above threshold, no boundary crossed
            bool result = FlightRecorder.ShouldSplitAtAltitudeBoundary(
                altitude: 50000, threshold: MunThreshold,
                wasAbove: true, pendingCross: true, pendingUT: 0, currentUT: 10);
            Assert.False(result);
        }

        [Fact]
        public void SameSide_BelowThreshold_ReturnsFalse()
        {
            // Still below threshold, no boundary crossed
            bool result = FlightRecorder.ShouldSplitAtAltitudeBoundary(
                altitude: 10000, threshold: MunThreshold,
                wasAbove: false, pendingCross: true, pendingUT: 0, currentUT: 10);
            Assert.False(result);
        }

        [Fact]
        public void CrossedButNotFarEnough_ReturnsFalse()
        {
            // Descended below threshold by only 100m (default hysteresis for 30km threshold = max(1000, 600) = 1000m)
            bool result = FlightRecorder.ShouldSplitAtAltitudeBoundary(
                altitude: MunThreshold - 100, threshold: MunThreshold,
                wasAbove: true, pendingCross: true, pendingUT: 0, currentUT: 10);
            Assert.False(result);
        }

        [Fact]
        public void CrossedFarButNotLongEnough_ReturnsFalse()
        {
            // Past 2000m below threshold but timer not started (pendingCross = false)
            bool result = FlightRecorder.ShouldSplitAtAltitudeBoundary(
                altitude: MunThreshold - 2000, threshold: MunThreshold,
                wasAbove: true, pendingCross: false, pendingUT: 0, currentUT: 10);
            Assert.False(result);
        }

        [Fact]
        public void CrossedFarAndLongEnough_Descending_ReturnsTrue()
        {
            // Descending below threshold: was above, now 2000m below, 5s elapsed
            bool result = FlightRecorder.ShouldSplitAtAltitudeBoundary(
                altitude: MunThreshold - 2000, threshold: MunThreshold,
                wasAbove: true, pendingCross: true, pendingUT: 100, currentUT: 105);
            Assert.True(result);
        }

        [Fact]
        public void CrossedFarAndLongEnough_Ascending_ReturnsTrue()
        {
            // Ascending above threshold: was below, now 2000m above, 5s elapsed
            bool result = FlightRecorder.ShouldSplitAtAltitudeBoundary(
                altitude: MunThreshold + 2000, threshold: MunThreshold,
                wasAbove: false, pendingCross: true, pendingUT: 100, currentUT: 105);
            Assert.True(result);
        }

        [Fact]
        public void HysteresisTimeNotMet_ReturnsFalse()
        {
            // Only 2s elapsed, need 3s
            bool result = FlightRecorder.ShouldSplitAtAltitudeBoundary(
                altitude: MunThreshold - 2000, threshold: MunThreshold,
                wasAbove: true, pendingCross: true, pendingUT: 100, currentUT: 102);
            Assert.False(result);
        }

        [Fact]
        public void ExactlyAtHysteresisThreshold_ReturnsTrue()
        {
            // Exactly 3s elapsed, exactly 1000m past boundary
            bool result = FlightRecorder.ShouldSplitAtAltitudeBoundary(
                altitude: MunThreshold - 1000, threshold: MunThreshold,
                wasAbove: true, pendingCross: true, pendingUT: 100, currentUT: 103);
            Assert.True(result);
        }

        [Fact]
        public void CustomHysteresis_Respected()
        {
            // Custom: 1s time, 500m distance
            bool result = FlightRecorder.ShouldSplitAtAltitudeBoundary(
                altitude: MunThreshold - 600, threshold: MunThreshold,
                wasAbove: true, pendingCross: true, pendingUT: 100, currentUT: 101.5,
                hysteresisSeconds: 1.0, hysteresisMeters: 500.0);
            Assert.True(result);

            // Same custom, but not enough distance
            bool result2 = FlightRecorder.ShouldSplitAtAltitudeBoundary(
                altitude: MunThreshold - 400, threshold: MunThreshold,
                wasAbove: true, pendingCross: true, pendingUT: 100, currentUT: 101.5,
                hysteresisSeconds: 1.0, hysteresisMeters: 500.0);
            Assert.False(result2);
        }

        // --- ComputeApproachAltitude tests ---

        [Fact]
        public void ComputeApproachAltitude_MunRadius()
        {
            // 200km * 0.15 = 30km
            double threshold = FlightRecorder.ComputeApproachAltitude(200000);
            Assert.Equal(30000, threshold);
        }

        [Fact]
        public void ComputeApproachAltitude_MinmusRadius()
        {
            // 60km * 0.15 = 9km (above floor)
            double threshold = FlightRecorder.ComputeApproachAltitude(60000);
            Assert.Equal(9000, threshold);
        }

        [Fact]
        public void ComputeApproachAltitude_GillyRadius_FloorClamp()
        {
            // 13km * 0.15 = 1950m, clamped to 5000m floor
            double threshold = FlightRecorder.ComputeApproachAltitude(13000);
            Assert.Equal(5000, threshold);
        }

        [Fact]
        public void ComputeApproachAltitude_TyloRadius()
        {
            // 600km * 0.15 = 90km
            double threshold = FlightRecorder.ComputeApproachAltitude(600000);
            Assert.Equal(90000, threshold);
        }

        [Fact]
        public void ComputeApproachAltitude_HugeBody_CeilingClamp()
        {
            // 2000km * 0.15 = 300km, clamped to 200km ceiling
            double threshold = FlightRecorder.ComputeApproachAltitude(2000000);
            Assert.Equal(200000, threshold);
        }

        [Fact]
        public void ComputeApproachAltitude_IkeRadius()
        {
            // 130km * 0.15 = 19.5km
            double threshold = FlightRecorder.ComputeApproachAltitude(130000);
            Assert.Equal(19500, threshold);
        }

        [Fact]
        public void ComputeApproachAltitude_MohoRadius()
        {
            // 250km * 0.15 = 37.5km
            double threshold = FlightRecorder.ComputeApproachAltitude(250000);
            Assert.Equal(37500, threshold);
        }

        [Fact]
        public void ComputeApproachAltitude_EelooRadius()
        {
            // 210km * 0.15 = 31.5km
            double threshold = FlightRecorder.ComputeApproachAltitude(210000);
            Assert.Equal(31500, threshold);
        }

        [Fact]
        public void HysteresisMeters_ScalesWithThreshold()
        {
            // For a large threshold (90km), default hysteresis = max(1000, 90000*0.02) = 1800m
            // Crossing by 1500m should NOT be enough
            bool result = FlightRecorder.ShouldSplitAtAltitudeBoundary(
                altitude: 90000 - 1500, threshold: 90000,
                wasAbove: true, pendingCross: true, pendingUT: 100, currentUT: 105);
            Assert.False(result);

            // Crossing by 2000m should be enough
            bool result2 = FlightRecorder.ShouldSplitAtAltitudeBoundary(
                altitude: 90000 - 2000, threshold: 90000,
                wasAbove: true, pendingCross: true, pendingUT: 100, currentUT: 105);
            Assert.True(result2);
        }
    }
}
