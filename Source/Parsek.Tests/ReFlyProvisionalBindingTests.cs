using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// R1-EMPTY-PROVISIONAL, the fixture-independent statement: a Re-Fly session can
    /// reach the merge orchestrator with no recorder ever bound to its provisional,
    /// and nothing in between refuses.
    ///
    /// <para>
    /// These cells pin the two detection points that were ignored, and the named
    /// non-throwing outcome at the merge. They deliberately do NOT assert any route
    /// by which a session reaches that state: the only demonstrated route is
    /// fixture-shaped and the general case is unproven in either direction.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyProvisionalBindingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ReFlyProvisionalBindingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static ReFlySessionMarker InPlaceMarker(
            string treeId = "tree-origin",
            string forkId = "rec-fork",
            string originId = "rec-origin")
        {
            return new ReFlySessionMarker
            {
                SessionId = "sess_r1",
                TreeId = treeId,
                ActiveReFlyRecordingId = forkId,
                OriginChildRecordingId = originId,
                SupersedeTargetId = originId,
                InPlaceContinuation = true,
            };
        }

        // ================================================================
        // Detection point 1: the restore gave up
        // ================================================================

        [Fact]
        public void RestoreGiveUp_NoMarker_DoesNotRaise()
        {
            var r = ReFlyProvisionalBinding.EvaluateRestoreGiveUp(null, "tree-anything");
            Assert.False(r.ShouldRaise);
            Assert.Equal("no-inplace-refly-session", r.Reason);
        }

        [Fact]
        public void RestoreGiveUp_PlaceholderModeMarker_DoesNotRaise()
        {
            // Placeholder mode cannot bind through the #585 swap by design; the
            // merge-dialog fallback is its documented recovery, so a give-up there
            // is expected rather than anomalous.
            var marker = InPlaceMarker();
            marker.InPlaceContinuation = false;

            var r = ReFlyProvisionalBinding.EvaluateRestoreGiveUp(marker, "tree-origin");

            Assert.False(r.ShouldRaise);
            Assert.Equal("no-inplace-refly-session", r.Reason);
        }

        [Fact]
        public void RestoreGiveUp_OnMarkerOwnTree_RaisesWithMarkerTreeReason()
        {
            var r = ReFlyProvisionalBinding.EvaluateRestoreGiveUp(
                InPlaceMarker(treeId: "tree-origin"), "tree-origin");

            Assert.True(r.ShouldRaise);
            Assert.Equal("refly-restore-gave-up-on-marker-tree", r.Reason);
        }

        /// <summary>
        /// R1 flight 3's shape: the restore was working on the pre-rewind flight's
        /// tree while the marker named another one. The reason token distinguishes
        /// it because the two have different causes and the next occurrence has to
        /// be triageable from KSP.log alone.
        /// </summary>
        [Fact]
        public void RestoreGiveUp_OnADifferentTree_RaisesWithOtherTreeReason()
        {
            var r = ReFlyProvisionalBinding.EvaluateRestoreGiveUp(
                InPlaceMarker(treeId: "tree-b9-stack-root"), "b435c4ad");

            Assert.True(r.ShouldRaise);
            Assert.Equal("refly-restore-gave-up-on-other-tree", r.Reason);
        }

        [Fact]
        public void RestoreGiveUp_UnknownAttemptedTree_RaisesAsOtherTree()
        {
            var r = ReFlyProvisionalBinding.EvaluateRestoreGiveUp(
                InPlaceMarker(treeId: "tree-origin"), null);

            Assert.True(r.ShouldRaise);
            Assert.Equal("refly-restore-gave-up-on-other-tree", r.Reason);
        }

        // ================================================================
        // Detection point 2: OnSave sees the provisional with no trajectory
        // ================================================================

        [Fact]
        public void SidecarRewrite_SessionProvisionalWithNoTrajectory_Raises()
        {
            var r = ReFlyProvisionalBinding.EvaluateSidecarRewrite(
                InPlaceMarker(forkId: "rec_5b0697a6"),
                "rec_5b0697a6",
                "trajectory-missing");

            Assert.True(r.ShouldRaise);
            Assert.Equal("refly-provisional-has-no-trajectory-at-save", r.Reason);
        }

        [Fact]
        public void SidecarRewrite_ADifferentRecording_DoesNotRaise()
        {
            var r = ReFlyProvisionalBinding.EvaluateSidecarRewrite(
                InPlaceMarker(forkId: "rec_5b0697a6"),
                "some-other-recording",
                "trajectory-missing");

            Assert.False(r.ShouldRaise);
            Assert.Equal("not-the-session-provisional", r.Reason);
        }

        /// <summary>
        /// Ordinary sidecar churn on a recording that DOES have data must stay
        /// silent, or the raise becomes noise and stops meaning anything.
        /// </summary>
        [Theory]
        [InlineData("trajectory-epoch-mismatch")]
        [InlineData("trajectory-schema-mismatch")]
        [InlineData("trajectory-invalid")]
        [InlineData("vessel-snapshot-missing")]
        [InlineData(null)]
        public void SidecarRewrite_OtherReasons_DoNotRaise(string reason)
        {
            var r = ReFlyProvisionalBinding.EvaluateSidecarRewrite(
                InPlaceMarker(forkId: "rec-fork"), "rec-fork", reason);

            Assert.False(r.ShouldRaise);
            Assert.Equal("sidecar-reason-is-not-trajectory-missing", r.Reason);
        }

        [Fact]
        public void SidecarRewrite_NoMarker_DoesNotRaise()
        {
            var r = ReFlyProvisionalBinding.EvaluateSidecarRewrite(
                null, "rec-fork", "trajectory-missing");

            Assert.False(r.ShouldRaise);
            Assert.Equal("no-inplace-refly-session", r.Reason);
        }

        [Fact]
        public void SidecarRewrite_NullRecordingId_DoesNotRaise()
        {
            var r = ReFlyProvisionalBinding.EvaluateSidecarRewrite(
                InPlaceMarker(), null, "trajectory-missing");

            Assert.False(r.ShouldRaise);
            Assert.Equal("not-the-session-provisional", r.Reason);
        }

        /// <summary>
        /// The two points must be independently sufficient. Point 1 cannot fire when
        /// the restore coroutine was never scheduled; point 2 covers that shape, and
        /// vice versa when the session is concluded before any save.
        /// </summary>
        [Fact]
        public void BothDetectionPoints_FireIndependentlyOnTheSameSession()
        {
            var marker = InPlaceMarker(treeId: "tree-b9-stack-root", forkId: "rec_5b0697a6");

            Assert.True(ReFlyProvisionalBinding
                .EvaluateRestoreGiveUp(marker, "b435c4ad").ShouldRaise);
            Assert.True(ReFlyProvisionalBinding
                .EvaluateSidecarRewrite(marker, "rec_5b0697a6", "trajectory-missing").ShouldRaise);
        }
    }
}
