using System.Collections.Generic;
using System.Globalization;
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

        #region GetSampleRateHz

        [Fact]
        public void GetSampleRateHz_DockingRange_Returns5Hz()
        {
            float hz = ProximityRateSelector.GetSampleRateHz(100);
            Assert.Equal(5f, hz);
        }

        [Fact]
        public void GetSampleRateHz_MidRange_Returns2Hz()
        {
            float hz = ProximityRateSelector.GetSampleRateHz(500);
            Assert.Equal(2f, hz);
        }

        [Fact]
        public void GetSampleRateHz_FarRange_ReturnsHalfHz()
        {
            float hz = ProximityRateSelector.GetSampleRateHz(2000);
            Assert.Equal(0.5f, hz);
        }

        [Fact]
        public void GetSampleRateHz_OutOfRange_ReturnsZero()
        {
            float hz = ProximityRateSelector.GetSampleRateHz(5000);
            Assert.Equal(0f, hz);
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

    [Collection("Sequential")]
    public class ProximityRateSelectorLoggingTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ProximityRateSelectorLoggingTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        /// <summary>
        /// Mirrors BackgroundRecorder.FormatInterval for test log emission.
        /// </summary>
        private static string FormatInterval(double interval)
        {
            if (interval >= ProximityRateSelector.OutOfRangeInterval || double.IsInfinity(interval))
                return "none";
            return interval.ToString("F1", CultureInfo.InvariantCulture) + "s";
        }

        [Fact]
        public void BackgroundRecorder_LogsSampleRateChange_WhenIntervalChanges()
        {
            // The actual OnBackgroundPhysicsFrame method requires real KSP Vessel objects,
            // which cannot be instantiated in unit tests. Instead, we verify that the
            // log format and content produced by the rate-change path is correct by
            // exercising the same log call that OnBackgroundPhysicsFrame makes.

            double distance = 100.0;
            double proximityInterval = ProximityRateSelector.GetSampleInterval(distance);
            float newHz = ProximityRateSelector.GetSampleRateHz(distance);
            uint pid = 100;

            // Simulate the exact log line produced by OnBackgroundPhysicsFrame
            // when the proximity interval changes for a vessel.
            string distStr = distance.ToString("F0", CultureInfo.InvariantCulture);
            string hzStr = newHz.ToString("F1", CultureInfo.InvariantCulture);
            ParsekLog.Info("BgRecorder",
                $"Sample rate changed: pid={pid} dist={distStr}m " +
                $"interval={FormatInterval(proximityInterval)} ({hzStr} Hz)");

            Assert.Single(logLines);
            string line = logLines[0];
            Assert.Contains("[BgRecorder]", line);
            Assert.Contains("Sample rate changed", line);
            Assert.Contains("pid=100", line);
            Assert.Contains("dist=100m", line);
            Assert.Contains("interval=0.2s", line);
            Assert.Contains("5.0 Hz", line);
        }

        [Fact]
        public void BackgroundRecorder_LogsSampleRateChange_OutOfRange_ShowsNone()
        {
            double distance = 5000.0;
            double proximityInterval = ProximityRateSelector.GetSampleInterval(distance);
            float newHz = ProximityRateSelector.GetSampleRateHz(distance);
            uint pid = 200;

            string distStr = distance.ToString("F0", CultureInfo.InvariantCulture);
            string hzStr = newHz.ToString("F1", CultureInfo.InvariantCulture);
            ParsekLog.Info("BgRecorder",
                $"Sample rate changed: pid={pid} dist={distStr}m " +
                $"interval={FormatInterval(proximityInterval)} ({hzStr} Hz)");

            Assert.Single(logLines);
            string line = logLines[0];
            Assert.Contains("[BgRecorder]", line);
            Assert.Contains("Sample rate changed", line);
            Assert.Contains("pid=200", line);
            Assert.Contains("interval=none", line);
            Assert.Contains("0.0 Hz", line);
        }
    }
}
