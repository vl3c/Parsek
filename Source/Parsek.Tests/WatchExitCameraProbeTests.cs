using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The bounded post-watch-exit camera probe: the pure clearance classifier, the logged-frame
    /// set, and the arm / tick / disarm lifecycle on a headless host (no FlightCamera, so every
    /// sample reads "unavailable" and the probe must still count down and disarm).
    /// </summary>
    [Collection("Sequential")]
    public class WatchExitCameraProbeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public WatchExitCameraProbeTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        [Theory]
        [InlineData(70.0, 66.0, "clear")]
        [InlineData(65.8, 66.0, "clear")]
        [InlineData(60.0, 66.0, "below-terrain")]
        [InlineData(double.NaN, 66.0, "unknown")]
        [InlineData(70.0, double.PositiveInfinity, "unknown")]
        public void ClassifyPostExitCameraClearance_ComparesCameraAgainstTerrain(
            double cameraAlt, double terrainAlt, string expected)
        {
            Assert.Equal(expected,
                WatchModeController.ClassifyPostExitCameraClearance(cameraAlt, terrainAlt));
        }

        [Fact]
        public void IsPostExitCameraProbeLoggedFrame_LogsOnlyTheFixedSampleFrames()
        {
            int logged = 0;
            for (int f = 0; f <= WatchModeController.PostExitCameraProbeFrames; f++)
                if (WatchModeController.IsPostExitCameraProbeLoggedFrame(f))
                    logged++;

            Assert.Equal(5, logged);
            Assert.True(WatchModeController.IsPostExitCameraProbeLoggedFrame(0));
            Assert.True(WatchModeController.IsPostExitCameraProbeLoggedFrame(
                WatchModeController.PostExitCameraProbeFrames));
            Assert.False(WatchModeController.IsPostExitCameraProbeLoggedFrame(2));
        }

        [Fact]
        public void TickPostExitCameraProbe_Unarmed_LogsNothing()
        {
            var watch = NewController();

            watch.TickPostExitCameraProbe();

            Assert.False(watch.IsPostExitCameraProbeArmedForTesting);
            Assert.DoesNotContain(logLines, l => l.Contains("Post-watch-exit camera"));
        }

        [Fact]
        public void ArmedProbe_SamplesFixedFramesThenDisarms()
        {
            var watch = NewController();
            var arm = typeof(WatchModeController).GetMethod("ArmPostExitCameraProbe",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            arm.Invoke(watch, new object[] { "rec=#0 \"Mining Rover RC\" -> Bug-E Buggy" });
            for (int i = 0; i < WatchModeController.PostExitCameraProbeFrames + 10; i++)
                watch.TickPostExitCameraProbe();

            Assert.False(watch.IsPostExitCameraProbeArmedForTesting);
            var samples = logLines.FindAll(l =>
                l.Contains("[CameraFollow]") && l.Contains("Post-watch-exit camera sample"));
            Assert.Equal(5, samples.Count);
            Assert.Contains(samples, l => l.Contains("frame=+0 ") && l.Contains("Mining Rover RC"));
            Assert.Contains(samples, l => l.Contains("frame=+240 "));
        }

        private static WatchModeController NewController()
        {
            var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            return new WatchModeController(host);
        }
    }
}
