using System;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    // Logistics route live-anchor bind: unit coverage for the guid gate on the anchor's
    // live-vessel match (Step 1) and the LiveAnchorBindTracker that drives the Step-2
    // double-suppression. The guid tests touch the shared GhostPlaybackLogic
    // vessel-exists / guid-resolver overrides and the tracker is static, so the class is
    // [Collection("Sequential")] and resets all of them in the constructor + Dispose.
    [Collection("Sequential")]
    public class RouteLiveAnchorTests : IDisposable
    {
        private const uint DepotPid = 4277041026u;
        private const string DepotRecordingId = "7e0d79b5d57a40d29400afd8b49d1906";
        private const string DepotLaunchGuid = "424fd14c8870407f81dffdf606a92db2";
        private const string DepotRelaunchGuid = "05d3ea0f00000000000000000000ffff";

        public RouteLiveAnchorTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GhostPlaybackLogic.ResetVesselExistsOverride();
            GhostPlaybackLogic.ResetVesselCacheForTesting();
            LiveAnchorBindTracker.ResetForTesting();
        }

        public void Dispose()
        {
            GhostPlaybackLogic.ResetVesselExistsOverride();
            GhostPlaybackLogic.ResetVesselCacheForTesting();
            LiveAnchorBindTracker.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // --- Step 1: guid-gated live-vessel match -------------------------------

        [Fact]
        public void RealVesselExistsForRecording_SameCraftDifferentLaunch_DoesNotMatch()
        {
            // The Depot recording's craft is loaded (same baked pid) but it is a
            // DIFFERENT launch (guid conclusively differs). A bare pid match would
            // bind the live-anchor to the wrong same-craft vessel; the guid gate
            // rejects it. Proves the three on-disk Depot launches never cross-bind.
            Recording rec = MakeDepotAnchorRecording();
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == DepotPid);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => DepotRelaunchGuid);

            Assert.False(GhostPlaybackLogic.RealVesselExistsForRecording(rec));
        }

        [Fact]
        public void RealVesselExistsForRecording_SameLaunch_Matches()
        {
            // Recording + live vessel share the launch guid: the fix fires for the
            // real Depot the player is flying.
            Recording rec = MakeDepotAnchorRecording();
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == DepotPid);
            GhostPlaybackLogic.SetVesselGuidResolverOverrideForTesting(pid => DepotLaunchGuid);

            Assert.True(GhostPlaybackLogic.RealVesselExistsForRecording(rec));
        }

        // --- Step 2: LiveAnchorBindTracker ground-truth signal ------------------

        [Theory]
        [InlineData(100, 100, true)]  // bound this frame
        [InlineData(100, 101, true)]  // bound last frame
        [InlineData(100, 102, true)]  // window edge (default 2)
        [InlineData(100, 103, false)] // just past the window: anchor ghost re-appears
        [InlineData(100, 99, false)]  // negative delta (future stamp) never counts
        public void IsRecentBind_WindowBoundaries(int boundFrame, int currentFrame, bool expected)
        {
            Assert.Equal(
                expected,
                LiveAnchorBindTracker.IsRecentBind(
                    boundFrame, currentFrame, LiveAnchorBindTracker.DefaultRecencyWindowFrames));
        }

        [Fact]
        public void WasLiveBoundRecently_AfterRecord_TrueThroughWindowThenFalse()
        {
            // The resolver stamps the anchor at frame 100 when it live-binds (source=live);
            // the suppression sites read it on the same and following frames.
            LiveAnchorBindTracker.RecordLiveBind(DepotRecordingId, 100);

            Assert.True(LiveAnchorBindTracker.WasLiveBoundRecently(DepotRecordingId, 100));
            Assert.True(LiveAnchorBindTracker.WasLiveBoundRecently(DepotRecordingId, 102));
            Assert.False(LiveAnchorBindTracker.WasLiveBoundRecently(DepotRecordingId, 103));
        }

        [Fact]
        public void WasLiveBoundRecently_DifferentRecordingId_False()
        {
            // A bind on one anchor must never suppress a different recording's ghost.
            LiveAnchorBindTracker.RecordLiveBind(DepotRecordingId, 100);

            Assert.False(LiveAnchorBindTracker.WasLiveBoundRecently("some-other-recording", 100));
        }

        [Fact]
        public void WasLiveBoundRecently_NoRecordOrNullId_False()
        {
            Assert.False(LiveAnchorBindTracker.WasLiveBoundRecently(DepotRecordingId, 100));
            Assert.False(LiveAnchorBindTracker.WasLiveBoundRecently(null, 100));
            Assert.False(LiveAnchorBindTracker.WasLiveBoundRecently(string.Empty, 100));
        }

        [Fact]
        public void RecordLiveBind_NullOrEmptyId_NoOp()
        {
            // Must not throw and must not register a phantom suppression key.
            LiveAnchorBindTracker.RecordLiveBind(null, 100);
            LiveAnchorBindTracker.RecordLiveBind(string.Empty, 100);

            Assert.False(LiveAnchorBindTracker.WasLiveBoundRecently(string.Empty, 100));
        }

        [Fact]
        public void RecordLiveBind_Restamp_ExtendsRecencyFromLatestFrame()
        {
            // A sustained docking window re-stamps each in-window frame; the recency
            // anchors to the LATEST stamp so the suppression holds for the whole window.
            LiveAnchorBindTracker.RecordLiveBind(DepotRecordingId, 100);
            Assert.False(LiveAnchorBindTracker.WasLiveBoundRecently(DepotRecordingId, 103));

            LiveAnchorBindTracker.RecordLiveBind(DepotRecordingId, 103);
            Assert.True(LiveAnchorBindTracker.WasLiveBoundRecently(DepotRecordingId, 103));
        }

        private static Recording MakeDepotAnchorRecording()
        {
            return new Recording
            {
                RecordingId = DepotRecordingId,
                VesselName = "Depot",
                VesselPersistentId = DepotPid,
                RecordedVesselGuid = DepotLaunchGuid,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            };
        }
    }
}
