using System;

namespace Parsek
{
    public static partial class RecordingStore
    {
        /// <summary>Forwarder to <see cref="CommittedTreeRestoreAttempt.CommittedTreeRestoreAttemptTreeId"/>.</summary>
        internal static string CommittedTreeRestoreAttemptTreeId =>
            CommittedTreeRestoreAttempt.CommittedTreeRestoreAttemptTreeId;

        /// <summary>Forwarder to <see cref="CommittedTreeRestoreAttempt.CommittedTreeRestoreAttemptReason"/>.</summary>
        internal static string CommittedTreeRestoreAttemptReason =>
            CommittedTreeRestoreAttempt.CommittedTreeRestoreAttemptReason;

        /// <summary>
        /// Production reset for a cold load (<c>ParsekScenario.ResetSaveScopedSessionState</c>,
        /// design-load-restores-the-save.md section 4.2). Forgets the save-scoped session
        /// state this store holds, in memory only:
        /// <list type="bullet">
        /// <item><description>the pending-tree slot and the saved-pending slot are DROPPED, not
        /// discarded: no <c>TreeDiscardPurge</c>, no events purge, no
        /// <c>treesDiscardedThisSession</c> entry, and every sidecar stays on disk (a save in
        /// the folder may still name it);</description></item>
        /// <item><description>the per-session discard set, the committed-tree restore attempt
        /// (its events are reloaded from the save with the rest), the durable committed-id
        /// hint, the scene-entry and fresh-rollout fields, the rewind replay scope, the rewind
        /// carries, the UT-adjust flags, the cleanup sets and the next-restore / next-commit
        /// suppressions.</description></item>
        /// </list>
        /// The committed lists stay: the cold branch removes them with notifications
        /// (<see cref="ClearCommittedInternal"/>), and the committed-list notification events
        /// stay subscribed for the new scene's controllers. Returns whether a pending tree
        /// (or a saved pending tree) was dropped.
        /// </summary>
        internal static bool ResetSaveScopedStateForLoad(out string droppedPendingTreeSummary)
        {
            droppedPendingTreeSummary = null;
            bool pendingDropped = PendingTreeSlot.PendingTree != null || PendingTreeSlot.SavedPendingTreeDuringActiveRestore != null;
            if (pendingDropped)
            {
                droppedPendingTreeSummary =
                    (PendingTreeSlot.PendingTree != null
                        ? $"pending='{PendingTreeSlot.PendingTree.TreeName ?? "<unnamed>"}' id={PendingTreeSlot.PendingTree.Id ?? "<null>"} state={PendingTreeSlot.PendingTreeStateValue}"
                        : "pending=<none>")
                    + (PendingTreeSlot.SavedPendingTreeDuringActiveRestore != null
                        ? $" savedPending='{PendingTreeSlot.SavedPendingTreeDuringActiveRestore.TreeName ?? "<unnamed>"}' id={PendingTreeSlot.SavedPendingTreeDuringActiveRestore.Id ?? "<null>"}"
                        : "");
            }

            PendingTreeSlot.ResetForLoad();

            treesDiscardedThisSession.Clear();
            CommittedTreeRestoreAttempt.ResetForLoad();
            durableCommittedRecordingIdHint = null;

            SceneEntryVesselScope.ResetForLoad();
            rewindCarriedRewindPoints = null;
            rewindCarriedStagedLists = null;
            RewindUTAdjustmentPending = false;
            RewindUTAdjustmentTargetUT = double.NaN;

            PendingCleanupPids = null;
            PendingCleanupNames = null;
            PendingRevertPreExistingPids = null;
            // The cold branch reloads committed trees from the save; inert ones come with them.
            InertRecordingTrees.ResetForLoad("save-scoped-reset");
            // Each recording carries its own auto-group key, which the cold load restores.
            RecordingGroupStore.ResetForSaveLoad();
            return pendingDropped;
        }
    }
}
