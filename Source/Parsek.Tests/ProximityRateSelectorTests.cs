using Xunit;

namespace Parsek.Tests
{
    public class ProximityRateSelectorTests
    {
        #region GetSampleInterval — Docking Range (< 200m)

        [Fact]
        public void GetSampleInterval_AtFocusedVessel_ReturnsDockingInterval()
        {
            double interval = ProximityRateSelector.GetSampleInterval(0);
            Assert.Equal(ProximityRateSelector.DockingInterval, interval);
        }

        [Fact]
        public void GetSampleInterval_InsideDockingRange_ReturnsDockingInterval()
        {
            double interval = ProximityRateSelector.GetSampleInterval(100);
            Assert.Equal(ProximityRateSelector.DockingInterval, interval);
        }

        [Fact]
        public void GetSampleInterval_JustInsideDockingRange_ReturnsDockingInterval()
        {
            double interval = ProximityRateSelector.GetSampleInterval(199.9);
            Assert.Equal(ProximityRateSelector.DockingInterval, interval);
        }

        #endregion

        #region GetSampleInterval — Mid Range (200m - 1km)

        [Fact]
        public void GetSampleInterval_AtDockingBoundary_ReturnsMidInterval()
        {
            // 200m is the boundary — no longer < 200, so it falls to mid range
            double interval = ProximityRateSelector.GetSampleInterval(200);
            Assert.Equal(ProximityRateSelector.MidInterval, interval);
        }

        [Fact]
        public void GetSampleInterval_MidRange_ReturnsMidInterval()
        {
            double interval = ProximityRateSelector.GetSampleInterval(500);
            Assert.Equal(ProximityRateSelector.MidInterval, interval);
        }

        [Fact]
        public void GetSampleInterval_JustInsideMidRange_ReturnsMidInterval()
        {
            double interval = ProximityRateSelector.GetSampleInterval(999.9);
            Assert.Equal(ProximityRateSelector.MidInterval, interval);
        }

        #endregion

        #region GetSampleInterval — Far Range (1km - 2.3km)

        [Fact]
        public void GetSampleInterval_AtMidBoundary_ReturnsFarInterval()
        {
            // 1000m is the boundary — no longer < 1000, so it falls to far range
            double interval = ProximityRateSelector.GetSampleInterval(1000);
            Assert.Equal(ProximityRateSelector.FarInterval, interval);
        }

        [Fact]
        public void GetSampleInterval_FarRange_ReturnsFarInterval()
        {
            double interval = ProximityRateSelector.GetSampleInterval(2000);
            Assert.Equal(ProximityRateSelector.FarInterval, interval);
        }

        [Fact]
        public void GetSampleInterval_JustInsidePhysicsBubble_ReturnsFarInterval()
        {
            double interval = ProximityRateSelector.GetSampleInterval(2299.9);
            Assert.Equal(ProximityRateSelector.FarInterval, interval);
        }

        #endregion

        #region GetSampleInterval — Out of Range (>= 2.3km)

        [Fact]
        public void GetSampleInterval_AtPhysicsBubbleBoundary_ReturnsMaxValue()
        {
            // 2300m is the boundary — no longer < 2300, so it falls out of range
            double interval = ProximityRateSelector.GetSampleInterval(2300);
            Assert.Equal(double.MaxValue, interval);
        }

        [Fact]
        public void GetSampleInterval_FarBeyondPhysicsBubble_ReturnsMaxValue()
        {
            double interval = ProximityRateSelector.GetSampleInterval(100000);
            Assert.Equal(double.MaxValue, interval);
        }

        #endregion

        #region Constants Consistency

        [Fact]
        public void Constants_ThresholdsAreOrdered()
        {
            Assert.True(ProximityRateSelector.DockingRange < ProximityRateSelector.MidRange);
            Assert.True(ProximityRateSelector.MidRange < ProximityRateSelector.PhysicsBubble);
        }

        [Fact]
        public void Constants_IntervalsIncreaseWithDistance()
        {
            Assert.True(ProximityRateSelector.DockingInterval < ProximityRateSelector.MidInterval);
            Assert.True(ProximityRateSelector.MidInterval < ProximityRateSelector.FarInterval);
            Assert.True(ProximityRateSelector.FarInterval < ProximityRateSelector.OutOfRangeInterval);
        }

        #endregion
    }
}
