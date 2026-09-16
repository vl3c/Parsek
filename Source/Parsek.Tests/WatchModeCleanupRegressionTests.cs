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

        // Renamed from ...LogsBeforeDestroyAllGhosts. The ordering half the old name headlined
        // compared the helper's own log against a destroy line the TEST wrote immediately after
        // the call, so it could not fail: moving the production call below DestroyAllGhosts left
        // it green. The real call-site order is pinned by
        // DestroyAllTimelineGhosts_ExitsWatchModeBeforeDestroyingTheGhosts below; what this cell
        // proves is the helper's own contract - detach first, then exit with skipCameraRestore.
        [Fact]
        public void ExitWatchModeBeforeTimelineGhostCleanup_WhenWatching_DetachesThenExitsSkippingCameraRestore()
        {
            bool? skipCameraRestore = null;
            var steps = new List<string>();

            bool exited = ParsekFlight.ExitWatchModeBeforeTimelineGhostCleanup(
                isWatchingGhost: true,
                exitWatchMode: skip =>
                {
                    steps.Add(skip ? "exit-true" : "exit-false");
                    skipCameraRestore = skip;
                },
                detachStockCameraTarget: () => steps.Add("detach"),
                context: "unit-test",
                watchFocusForLogs: "watch=rec#7");

            Assert.True(exited);
            Assert.True(skipCameraRestore.HasValue && skipCameraRestore.Value);
            Assert.Equal(new[] { "detach", "exit-true" }, steps);

            Assert.Contains(logLines, line =>
                line.Contains("[CameraFollow]")
                && line.Contains("Exiting watch mode before timeline ghost cleanup")
                && line.Contains("unit-test")
                && line.Contains("watch=rec#7"));
        }

        // The call-site ORDER, which no runtime cell can see (DestroyAllTimelineGhosts needs a
        // live ParsekFlight, an engine and the ghost map). Pinned as a source gate over the
        // brace-matched method body, on comment-stripped and literal-masked text so a
        // commented-out or quoted call cannot satisfy it: the watch-mode exit must run BEFORE the
        // engine destroys the ghost GameObjects, or the stock camera is left following a
        // destroyed transform.
        [Fact]
        public void DestroyAllTimelineGhosts_ExitsWatchModeBeforeDestroyingTheGhosts()
        {
            string path = LocateParsekFlightSource();
            Assert.True(System.IO.File.Exists(path), $"ParsekFlight.cs not found at {path}");

            string prepared = SourceScanText.StripCommentsAndMaskLiterals(
                System.IO.File.ReadAllText(path));
            const string signature = "void DestroyAllTimelineGhosts()";
            int sigIdx = prepared.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(sigIdx >= 0, "DestroyAllTimelineGhosts() no longer exists");
            Assert.Equal(sigIdx, prepared.LastIndexOf(signature, StringComparison.Ordinal));

            int bodyStart = prepared.IndexOf('{', sigIdx);
            Assert.True(bodyStart > 0, "DestroyAllTimelineGhosts has no body");
            string body = SourceScanText.BraceMatchedBlock(prepared, bodyStart);

            int exitIdx = body.IndexOf(
                "ExitWatchModeBeforeTimelineGhostCleanup(", StringComparison.Ordinal);
            int destroyIdx = body.IndexOf("engine.DestroyAllGhosts();", StringComparison.Ordinal);
            Assert.True(exitIdx >= 0,
                "DestroyAllTimelineGhosts no longer exits watch mode");
            Assert.True(destroyIdx >= 0,
                "DestroyAllTimelineGhosts no longer calls engine.DestroyAllGhosts()");
            Assert.True(exitIdx < destroyIdx,
                "REGRESSION: watch mode must be exited BEFORE the engine destroys the ghost "
                + "GameObjects, or the stock camera keeps following a destroyed transform.");
        }


        private static string LocateParsekFlightSource()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                string candidate = System.IO.Path.Combine(dir, "Source", "Parsek", "ParsekFlight.cs");
                if (System.IO.File.Exists(candidate)) return candidate;
                dir = System.IO.Path.GetDirectoryName(dir);
            }

            return System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "Parsek", "ParsekFlight.cs"));
        }

        [Fact]
        public void ExitWatchModeBeforeTimelineGhostCleanup_WhenNotWatching_LeavesWatchStateAlone()
        {
            bool callbackInvoked = false;

            bool exited = ParsekFlight.ExitWatchModeBeforeTimelineGhostCleanup(
                isWatchingGhost: false,
                exitWatchMode: _ => callbackInvoked = true,
                detachStockCameraTarget: () => callbackInvoked = true,
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
