using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for <see cref="ParsekFlight.ShouldShowOnFlightReadyMergeDialog"/>.
    /// The OnFlightReady fallback opens the tree merge dialog only when there is
    /// a pending tree, no restore coroutine owns it, AND no active Re-Fly session
    /// owns it. The Re-Fly guard prevents the fallback from firing immediately
    /// after an initial Re-Fly invocation or a Retry-from-Rewind-Point.
    /// </summary>
    [Collection("Sequential")]
    public class OnFlightReadyMergeDialogGuardTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public OnFlightReadyMergeDialogGuardTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void NoPendingTree_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldShowOnFlightReadyMergeDialog(
                hasPendingTree: false,
                restoringActiveTree: false,
                reFlySessionActive: false));
        }

        [Fact]
        public void PendingTree_NoRestoring_NoReFly_ReturnsTrue()
        {
            // Canonical fallback case: a pending tree leaked through to OnFlightReady
            // outside a Re-Fly. This is the only configuration that opens the dialog.
            Assert.True(ParsekFlight.ShouldShowOnFlightReadyMergeDialog(
                hasPendingTree: true,
                restoringActiveTree: false,
                reFlySessionActive: false));
        }

        [Fact]
        public void PendingTree_RestoringActiveTree_ReturnsFalse()
        {
            // #293: restore coroutine owns the pending tree mid-yield. Skipping the
            // fallback prevents auto-merging it and leaving the vessel with no
            // active recorder.
            Assert.False(ParsekFlight.ShouldShowOnFlightReadyMergeDialog(
                hasPendingTree: true,
                restoringActiveTree: true,
                reFlySessionActive: false));
        }

        [Fact]
        public void PendingTree_ReFlySessionActive_ReturnsFalse()
        {
            // Initial Re-Fly invocation or Retry-from-Rewind-Point: AtomicMarkerWrite
            // just attached a fresh fork to the pending tree, and the session marker
            // is set by the time OnFlightReady runs. The merge decision belongs to
            // the scene-exit path once the attempt actually finishes, not now.
            Assert.False(ParsekFlight.ShouldShowOnFlightReadyMergeDialog(
                hasPendingTree: true,
                restoringActiveTree: false,
                reFlySessionActive: true));
        }

        [Fact]
        public void PendingTree_RestoringAndReFlyActive_ReturnsFalse()
        {
            // Defense in depth: either skip reason is sufficient. Combined, the
            // dialog still must not fire.
            Assert.False(ParsekFlight.ShouldShowOnFlightReadyMergeDialog(
                hasPendingTree: true,
                restoringActiveTree: true,
                reFlySessionActive: true));
        }

        [Fact]
        public void NoPendingTree_RestoringActive_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldShowOnFlightReadyMergeDialog(
                hasPendingTree: false,
                restoringActiveTree: true,
                reFlySessionActive: false));
        }

        [Fact]
        public void NoPendingTree_ReFlySessionActive_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldShowOnFlightReadyMergeDialog(
                hasPendingTree: false,
                restoringActiveTree: false,
                reFlySessionActive: true));
        }

        // ------------------------------------------------------------------
        // ShouldUpgradeRestoreModeForReFlyRetry — the OnFlightReady decision
        // that schedules the recorder restore when a Re-Fly retry left a
        // freshly-attached fork on a Finalized pending tree.
        // ------------------------------------------------------------------

        [Fact]
        public void UpgradeRestoreMode_ReFlyRetryWithFinalizedPending_ReturnsTrue()
        {
            // Canonical Re-Fly retry case: previous attempt was finalized
            // post-destruction, AtomicMarkerWrite just attached a new fork,
            // ScheduleActiveTreeRestoreOnFlightReady stayed None because
            // TryRestoreActiveTreeNode kept the in-memory Finalized tree.
            // Without this upgrade, no Quickload restore would run and the
            // live vessel would have no recorder bound to the new fork.
            Assert.True(ParsekFlight.ShouldUpgradeRestoreModeForReFlyRetry(
                restoreMode: ParsekScenario.ActiveTreeRestoreMode.None,
                hasPendingTree: true,
                pendingTreeIsFinalized: true,
                reFlySessionActive: true));
        }

        [Fact]
        public void UpgradeRestoreMode_RestoreAlreadyScheduled_ReturnsFalse()
        {
            // Initial Re-Fly invocation hits the Limbo dispatch in OnLoad and
            // arrives at OnFlightReady with restoreMode=Quickload already
            // set. Upgrading would be a no-op (and could mask a bug if the
            // existing schedule were inconsistent).
            Assert.False(ParsekFlight.ShouldUpgradeRestoreModeForReFlyRetry(
                restoreMode: ParsekScenario.ActiveTreeRestoreMode.Quickload,
                hasPendingTree: true,
                pendingTreeIsFinalized: true,
                reFlySessionActive: true));
        }

        [Fact]
        public void UpgradeRestoreMode_VesselSwitchAlreadyScheduled_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldUpgradeRestoreModeForReFlyRetry(
                restoreMode: ParsekScenario.ActiveTreeRestoreMode.VesselSwitch,
                hasPendingTree: true,
                pendingTreeIsFinalized: true,
                reFlySessionActive: true));
        }

        [Fact]
        public void UpgradeRestoreMode_NoPendingTree_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldUpgradeRestoreModeForReFlyRetry(
                restoreMode: ParsekScenario.ActiveTreeRestoreMode.None,
                hasPendingTree: false,
                pendingTreeIsFinalized: false,
                reFlySessionActive: true));
        }

        [Fact]
        public void UpgradeRestoreMode_PendingNotFinalized_ReturnsFalse()
        {
            // Limbo state — covered by the existing Quickload-schedule path
            // in OnLoad. Our upgrade only applies to Finalized.
            Assert.False(ParsekFlight.ShouldUpgradeRestoreModeForReFlyRetry(
                restoreMode: ParsekScenario.ActiveTreeRestoreMode.None,
                hasPendingTree: true,
                pendingTreeIsFinalized: false,
                reFlySessionActive: true));
        }

        [Fact]
        public void UpgradeRestoreMode_NoReFlySession_ReturnsFalse()
        {
            // Without an active Re-Fly session, a Finalized pending tree at
            // OnFlightReady is the ordinary "auto-commit missed" case — let
            // the merge-dialog fallback handle it, do not silently restart
            // a recorder against a finalized tree.
            Assert.False(ParsekFlight.ShouldUpgradeRestoreModeForReFlyRetry(
                restoreMode: ParsekScenario.ActiveTreeRestoreMode.None,
                hasPendingTree: true,
                pendingTreeIsFinalized: true,
                reFlySessionActive: false));
        }

        // ------------------------------------------------------------------
        // ShouldAcceptFinalizedPendingTreeForReFlyRetry — the
        // RestoreActiveTreeFromPending coroutine's state-gate carve-out.
        // ------------------------------------------------------------------

        [Fact]
        public void AcceptFinalized_ReFlyActiveAndFinalized_ReturnsTrue()
        {
            Assert.True(ParsekFlight.ShouldAcceptFinalizedPendingTreeForReFlyRetry(
                hasPendingTree: true,
                pendingTreeIsFinalized: true,
                reFlySessionActive: true));
        }

        [Fact]
        public void AcceptFinalized_ReFlyActiveButNotFinalized_ReturnsFalse()
        {
            // Limbo state hits the existing gate; we only carve out Finalized.
            Assert.False(ParsekFlight.ShouldAcceptFinalizedPendingTreeForReFlyRetry(
                hasPendingTree: true,
                pendingTreeIsFinalized: false,
                reFlySessionActive: true));
        }

        [Fact]
        public void AcceptFinalized_FinalizedButNoReFly_ReturnsFalse()
        {
            // A Finalized pending tree with no active Re-Fly is the ordinary
            // auto-commit-missed case. Accepting it here would silently
            // start a recorder against a finalized tree, which is wrong.
            Assert.False(ParsekFlight.ShouldAcceptFinalizedPendingTreeForReFlyRetry(
                hasPendingTree: true,
                pendingTreeIsFinalized: true,
                reFlySessionActive: false));
        }

        [Fact]
        public void AcceptFinalized_NoPendingTree_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldAcceptFinalizedPendingTreeForReFlyRetry(
                hasPendingTree: false,
                pendingTreeIsFinalized: true,
                reFlySessionActive: true));
        }
    }
}
