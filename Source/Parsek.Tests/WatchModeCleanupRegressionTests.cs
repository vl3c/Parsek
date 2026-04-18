using System;
using System.Collections.Generic;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class WatchModeCleanupRegressionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public WatchModeCleanupRegressionTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            SunLateUpdateGuardPatch.ResetForTesting();
        }

        public void Dispose()
        {
            SunLateUpdateGuardPatch.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void ExitWatchModeBeforeTimelineGhostCleanup_WhenWatching_LogsBeforeDestroyAllGhosts()
        {
            bool? skipCameraRestore = null;

            bool exited = ParsekFlight.ExitWatchModeBeforeTimelineGhostCleanup(
                isWatchingGhost: true,
                exitWatchMode: skip => skipCameraRestore = skip,
                context: "unit-test",
                watchFocusForLogs: "watch=rec#7");

            ParsekLog.Info("Engine", "DestroyAllGhosts: clearing 1 primary + 0 overlap entries");

            Assert.True(exited);
            Assert.True(skipCameraRestore.HasValue && skipCameraRestore.Value);

            int exitIndex = logLines.FindIndex(line =>
                line.Contains("[CameraFollow]")
                && line.Contains("Exiting watch mode before timeline ghost cleanup"));
            int destroyIndex = logLines.FindIndex(line =>
                line.Contains("[Engine]")
                && line.Contains("DestroyAllGhosts: clearing 1 primary + 0 overlap entries"));

            Assert.True(exitIndex >= 0, "expected CameraFollow exit log");
            Assert.True(destroyIndex >= 0, "expected Engine destroy log");
            Assert.True(exitIndex < destroyIndex,
                $"expected watch exit log before destroy log, exitIndex={exitIndex} destroyIndex={destroyIndex}");
        }

        [Fact]
        public void ExitWatchModeBeforeTimelineGhostCleanup_WhenNotWatching_LeavesWatchStateAlone()
        {
            bool callbackInvoked = false;

            bool exited = ParsekFlight.ExitWatchModeBeforeTimelineGhostCleanup(
                isWatchingGhost: false,
                exitWatchMode: _ => callbackInvoked = true,
                context: "unit-test-noop",
                watchFocusForLogs: "watch=none");

            Assert.False(exited);
            Assert.False(callbackInvoked);
            Assert.Contains(logLines, line =>
                line.Contains("[CameraFollow]")
                && line.Contains("no active watch mode before unit-test-noop"));
        }

        [Fact]
        public void SunLateUpdateGuardPatch_MissingTargetWarningLatch_IsPurelyOneShot()
        {
            Assert.True(SunLateUpdateGuardPatch.ShouldSkipLateUpdate(null));
            Assert.True(SunLateUpdateGuardPatch.ShouldEmitMissingTargetWarning(false));
            Assert.False(SunLateUpdateGuardPatch.ShouldEmitMissingTargetWarning(true));
        }
    }
}
