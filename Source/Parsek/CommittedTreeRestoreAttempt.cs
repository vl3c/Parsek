using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The committed-tree restore attempt (RecordingStore services plan, service H): the
    /// one-shot context armed when a committed spawned vessel is resumed through a
    /// copy-on-write clone of its committed tree. While it is armed, the clone's same-id
    /// game-state events past each recording's committed end stay off disk
    /// (<see cref="ShouldSuppressCommittedTreeRestoreAttemptEventPersistence"/>), and a
    /// discard purges them (<see cref="DiscardCommittedTreeRestoreAttempt"/>). The state is
    /// save-scoped: a cold load forgets it (<see cref="ResetForLoad"/>).
    /// </summary>
    internal static class CommittedTreeRestoreAttempt
    {
        // One-shot context for the committed-spawned-vessel restore path. That
        // path keeps the original committed tree durable and gives the live
        // flight a copy-on-write clone. While the clone is unmerged, dirty
        // overlap sidecars must not be written over committed history, and
        // Discard must remove same-id game-state event tails authored after the
        // committed recording's original end UT.
        private static string committedTreeRestoreAttemptTreeId;
        private static string committedTreeRestoreAttemptReason;
        private static HashSet<string> committedTreeRestoreAttemptRecordingIds;
        private static Dictionary<string, double> committedTreeRestoreAttemptEventCutoffs;

        internal static bool HasCommittedTreeRestoreAttempt =>
            committedTreeRestoreAttemptTreeId != null;
        internal static bool HasCommittedTreeRestoreAttemptForTesting =>
            HasCommittedTreeRestoreAttempt;

        /// <summary>The tree id of the live committed-tree restore attempt, or null.</summary>
        internal static string CommittedTreeRestoreAttemptTreeId => committedTreeRestoreAttemptTreeId;

        /// <summary>The arm reason of the live committed-tree restore attempt, or null.</summary>
        internal static string CommittedTreeRestoreAttemptReason => committedTreeRestoreAttemptReason;

        /// <summary>Cold-load reset, called by <c>RecordingStore.ResetSaveScopedStateForLoad</c>
        /// at the point where it used to clear these fields itself. The attempt's events are
        /// reloaded from the save with the rest, so nothing is purged.</summary>
        internal static void ResetForLoad()
        {
            committedTreeRestoreAttemptTreeId = null;
            committedTreeRestoreAttemptReason = null;
            committedTreeRestoreAttemptRecordingIds = null;
            committedTreeRestoreAttemptEventCutoffs = null;
        }

        /// <summary>Test reset, called by <c>RecordingStore.ResetForTestingInternal</c> at the
        /// point where it used to clear these fields itself.</summary>
        internal static void ResetForTesting()
        {
            committedTreeRestoreAttemptTreeId = null;
            committedTreeRestoreAttemptReason = null;
            committedTreeRestoreAttemptRecordingIds = null;
            committedTreeRestoreAttemptEventCutoffs = null;
        }

        internal static void ArmCommittedTreeRestoreAttempt(RecordingTree tree, string reason)
        {
            if (tree == null || string.IsNullOrEmpty(tree.Id))
            {
                ParsekLog.Warn("RecordingStore",
                    $"ArmCommittedTreeRestoreAttempt skipped: tree={(tree == null ? "<null>" : "<no-id>")} " +
                    $"reason={reason ?? "<none>"}");
                return;
            }

            if (!string.IsNullOrEmpty(committedTreeRestoreAttemptTreeId))
            {
                ParsekLog.Warn("RecordingStore",
                    $"ArmCommittedTreeRestoreAttempt: replacing stale context " +
                    $"tree={committedTreeRestoreAttemptTreeId} " +
                    $"reason={committedTreeRestoreAttemptReason ?? "<none>"}");
            }

            committedTreeRestoreAttemptTreeId = tree.Id;
            committedTreeRestoreAttemptReason = reason ?? "<unspecified>";
            committedTreeRestoreAttemptRecordingIds =
                BuildCommittedTreeRestoreRecordingIds(tree);
            committedTreeRestoreAttemptEventCutoffs =
                BuildCommittedTreeRestoreEventCutoffs(tree);
            ParsekLog.Info("RecordingStore",
                $"Armed committed-tree restore attempt for '{tree.TreeName ?? "<unnamed>"}' " +
                $"(id={tree.Id}, recordings={tree.Recordings?.Count ?? 0}, " +
                $"cutoffs={committedTreeRestoreAttemptEventCutoffs.Count}, " +
                $"reason={committedTreeRestoreAttemptReason})");
        }

        internal static void ClearCommittedTreeRestoreAttempt(string reason)
        {
            if (string.IsNullOrEmpty(committedTreeRestoreAttemptTreeId))
                return;

            ParsekLog.Verbose("RecordingStore",
                $"Cleared committed-tree restore attempt " +
                $"tree={committedTreeRestoreAttemptTreeId} " +
                $"reason={reason ?? "<none>"} " +
                $"armedReason={committedTreeRestoreAttemptReason ?? "<none>"}");
            committedTreeRestoreAttemptTreeId = null;
            committedTreeRestoreAttemptReason = null;
            committedTreeRestoreAttemptRecordingIds = null;
            committedTreeRestoreAttemptEventCutoffs = null;
        }

        /// <summary>
        /// Discards the armed committed-tree restore attempt as a whole: purges the
        /// attempt's same-id post-cutoff game-state event tails FIRST (see
        /// <see cref="PurgeCommittedTreeRestoreAttemptEventTails"/> - the clear nulls
        /// the cutoff map), then clears it. No-op when no attempt is armed.
        /// <paramref name="preserveIrreversibleTails"/> is true on a LIVE discard (KSP keeps
        /// what the player did in the attempt, so its irreversible tail events and science
        /// become direct ledger rows before the purge) and false on a rollback (a rewind or a
        /// load reverts KSP with the save).
        /// Discard paths whose caller drops the attempt's clone tree before any
        /// drop-site id match can run (the no-op no-session resume revert, the
        /// switch-segment scoped discard, the pre-switch dialog discard) must call
        /// THIS, not the bare clear: the caller-side clear leaves the clone's
        /// suppressed post-cutoff events to become durable on the next save.
        /// </summary>
        internal static void DiscardCommittedTreeRestoreAttempt(
            string reason, bool preserveIrreversibleTails)
        {
            if (string.IsNullOrEmpty(committedTreeRestoreAttemptTreeId))
                return;

            PurgeCommittedTreeRestoreAttemptEventTails(
                reason ?? "<none>", preserveIrreversibleTails);
            ClearCommittedTreeRestoreAttempt(reason);
        }

        internal static void ClearCommittedTreeRestoreAttemptForTree(string treeId, string reason)
        {
            if (!DroppedTreeMatchesArmedRestoreAttempt(committedTreeRestoreAttemptTreeId, treeId))
                return;

            ClearCommittedTreeRestoreAttempt(reason);
        }

        /// <summary>
        /// Pure: true when a tree a scene reset is about to drop is the clone the armed
        /// committed-tree restore attempt guards (same tree id), so the attempt must be
        /// cleared with it. An attempt armed for a different tree id stays armed; a null
        /// or empty id on either side never clears.
        /// </summary>
        internal static bool DroppedTreeMatchesArmedRestoreAttempt(
            string armedAttemptTreeId, string droppedTreeId)
        {
            if (string.IsNullOrEmpty(armedAttemptTreeId) || string.IsNullOrEmpty(droppedTreeId))
                return false;

            return string.Equals(armedAttemptTreeId, droppedTreeId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Clears the armed committed-tree restore attempt when <paramref name="droppedTreeId"/>
        /// is its clone, so a reset that drops the clone tree (for example
        /// <c>ParsekFlight.ResetFlightReadyState</c>) leaves no stale attempt armed: while it
        /// stays armed every OnSave defers the pending-event milestone flush and the next arm
        /// logs a replacing-stale-context warn. The attempt's same-id post-cutoff event tails
        /// are purged FIRST through <see cref="DiscardCommittedTreeRestoreAttempt"/> (the same
        /// purge-then-clear order DiscardPendingTree uses), because the clear nulls the cutoff
        /// map and the clone's abandoned post-restore events must not become durable on the
        /// next save. An attempt for another tree id, or no attempt at all, is left untouched.
        /// <paramref name="preserveIrreversibleTails"/> as in
        /// <see cref="DiscardCommittedTreeRestoreAttempt"/>: true when the drop is a live discard,
        /// false when a load or rewind already reverted KSP. Returns true when an attempt was
        /// cleared.
        /// </summary>
        internal static bool ClearCommittedTreeRestoreAttemptForDroppedTree(
            string droppedTreeId, string reason, bool preserveIrreversibleTails)
        {
            if (!DroppedTreeMatchesArmedRestoreAttempt(
                    committedTreeRestoreAttemptTreeId, droppedTreeId))
                return false;

            ParsekLog.Info("RecordingStore",
                $"ClearCommittedTreeRestoreAttemptForDroppedTree: dropped tree={droppedTreeId} " +
                $"matches the armed restore attempt; clearing it reason={reason ?? "<none>"} " +
                $"preserveIrreversibleTails={preserveIrreversibleTails}");
            DiscardCommittedTreeRestoreAttempt(reason, preserveIrreversibleTails);
            return true;
        }

        /// <summary>
        /// True when <paramref name="treeId"/> is the tree currently armed as a
        /// committed-tree restore attempt (a copy-on-write clone of a committed
        /// tree is the live active tree). Used by the in-flight discard helper to
        /// detect a live committed clone BEFORE the discard clears the attempt,
        /// so it can tear the clone down (the committed original survives in
        /// committedTrees) instead of leaving it to strand.
        /// </summary>
        internal static bool IsCommittedTreeRestoreAttemptTree(string treeId)
        {
            return !string.IsNullOrEmpty(treeId)
                && string.Equals(committedTreeRestoreAttemptTreeId, treeId,
                    StringComparison.Ordinal);
        }

        internal static bool IsCommittedTreeRestoreAttemptRecordingId(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)
                || committedTreeRestoreAttemptRecordingIds == null)
                return false;

            return committedTreeRestoreAttemptRecordingIds.Contains(recordingId);
        }

        internal static bool ShouldSuppressCommittedTreeRestoreAttemptEventPersistence(
            GameStateEvent evt)
        {
            string recordingId = evt.recordingId;
            if (string.IsNullOrEmpty(recordingId)
                || string.IsNullOrEmpty(committedTreeRestoreAttemptTreeId))
            {
                return false;
            }

            // Switch-segment narrowing (segment-scoped-switch-fly-autorecord Phase D):
            // a marker-owned new recording id created by SwitchSegmentSession is real
            // pending state and must survive F5/save/reload, even when a #866
            // committed-tree restore attempt is concurrently armed. Bypass before the
            // existing same-id cutoff / pending-only checks so the suppression contract
            // for original committed recording ids remains unchanged.
            if (RecordingStore.IsMarkerOwnedSwitchSegmentRecordingId(recordingId))
            {
                var session = ParsekScenario.Instance?.ActiveSwitchSegmentSession;
                ParsekLog.Verbose("RecordingStore",
                    $"event persistence not-suppressed reason=marker-owned-switch-segment " +
                    $"recId={recordingId} " +
                    $"sessionId={(session != null ? session.SessionId.ToString("D", CultureInfo.InvariantCulture) : "<null>")}");
                return false;
            }

            if (committedTreeRestoreAttemptEventCutoffs != null
                && committedTreeRestoreAttemptEventCutoffs.TryGetValue(
                    recordingId,
                    out double cutoffUT))
            {
                return evt.ut > cutoffUT;
            }

            return IsPendingOnlyCommittedTreeRestoreAttemptRecordingId(recordingId);
        }

        private static bool IsPendingOnlyCommittedTreeRestoreAttemptRecordingId(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)
                || string.IsNullOrEmpty(committedTreeRestoreAttemptTreeId))
            {
                return false;
            }

            if (committedTreeRestoreAttemptRecordingIds != null
                && committedTreeRestoreAttemptRecordingIds.Contains(recordingId))
            {
                return false;
            }

            if (RecordingStore.IsCommittedRecordingId(recordingId))
                return false;

            return TreeContainsRecordingId(
                    RecordingStore.PendingTree,
                    committedTreeRestoreAttemptTreeId,
                    recordingId)
                || TreeContainsRecordingId(
                    PendingTreeSlot.SavedPendingTreeDuringActiveRestore,
                    committedTreeRestoreAttemptTreeId,
                    recordingId)
                || ParsekFlight.IsActiveTreeRecordingIdForTree(
                    recordingId,
                    committedTreeRestoreAttemptTreeId);
        }

        private static bool TreeContainsRecordingId(
            RecordingTree tree,
            string treeId,
            string recordingId)
        {
            return tree != null
                && !string.IsNullOrEmpty(treeId)
                && string.Equals(tree.Id, treeId, StringComparison.Ordinal)
                && tree.Recordings != null
                && tree.Recordings.ContainsKey(recordingId);
        }

        private static HashSet<string> BuildCommittedTreeRestoreRecordingIds(RecordingTree tree)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (tree?.Recordings == null)
                return result;

            foreach (Recording rec in tree.Recordings.Values)
            {
                if (!string.IsNullOrEmpty(rec?.RecordingId))
                    result.Add(rec.RecordingId);
            }

            return result;
        }

        private static Dictionary<string, double> BuildCommittedTreeRestoreEventCutoffs(
            RecordingTree tree)
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            if (tree?.Recordings == null)
                return result;

            foreach (Recording rec in tree.Recordings.Values)
            {
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (!rec.HasActualTrajectoryBounds && double.IsNaN(rec.ExplicitEndUT))
                    continue;

                double cutoff = rec.EndUT;
                if (double.IsNaN(cutoff) || double.IsInfinity(cutoff))
                    continue;

                result[rec.RecordingId] = cutoff;
            }

            return result;
        }

        internal static bool PendingTreeMatchesCommittedTreeRestoreAttempt()
        {
            return RecordingStore.PendingTree != null
                && !string.IsNullOrEmpty(committedTreeRestoreAttemptTreeId)
                && string.Equals(
                    RecordingStore.PendingTree.Id,
                    committedTreeRestoreAttemptTreeId,
                    StringComparison.Ordinal);
        }

        /// <summary>
        /// Purges the armed attempt's same-id post-cutoff game-state event tails - the events
        /// <see cref="ShouldSuppressCommittedTreeRestoreAttemptEventPersistence"/> keeps off
        /// disk while the attempt is armed - keyed by the cutoff map built at arm time. Call
        /// BEFORE <see cref="ClearCommittedTreeRestoreAttempt"/>: the clear nulls that map.
        /// <paramref name="contextLabel"/> is the per-recording reason GameStateStore logs,
        /// <paramref name="warnLabel"/> prefixes the summary line (emitted only when
        /// something was purged). When <paramref name="preserveIrreversibleTails"/> (a live
        /// discard), the tails' irreversible events and science subjects are re-homed to the
        /// direct ledger first (<see cref="LedgerOrchestrator.PreserveIrreversibleRestoreAttemptTailsOnDiscard"/>).
        /// Returns the number of events purged.
        /// </summary>
        private static int PurgeCommittedTreeRestoreAttemptEventTails(
            string contextLabel, bool preserveIrreversibleTails, string warnLabel = null)
        {
            if (committedTreeRestoreAttemptEventCutoffs == null
                || committedTreeRestoreAttemptEventCutoffs.Count == 0)
            {
                return 0;
            }

            if (preserveIrreversibleTails)
                LedgerOrchestrator.PreserveIrreversibleRestoreAttemptTailsOnDiscard(
                    committedTreeRestoreAttemptEventCutoffs, contextLabel);

            int purged = 0;
            foreach (var kvp in committedTreeRestoreAttemptEventCutoffs)
            {
                purged += GameStateStore.PurgeEventsForRecordingAfterUT(
                    kvp.Key,
                    kvp.Value,
                    contextLabel);
            }

            if (purged > 0)
            {
                ParsekLog.Warn("RecordingStore",
                    $"{warnLabel ?? contextLabel}: purged {purged} same-id game-state event tail(s) " +
                    $"for committed-tree restore attempt tree={committedTreeRestoreAttemptTreeId} " +
                    $"armedReason={committedTreeRestoreAttemptReason ?? "<none>"}");
            }

            return purged;
        }

        internal static int PurgeCommittedTreeRestoreAttemptEventTailsForPendingDiscard(
            bool preserveIrreversibleTails)
        {
            if (!PendingTreeMatchesCommittedTreeRestoreAttempt()
                || committedTreeRestoreAttemptEventCutoffs == null
                || committedTreeRestoreAttemptEventCutoffs.Count == 0)
            {
                return 0;
            }

            return PurgeCommittedTreeRestoreAttemptEventTails(
                $"DiscardPendingTree committed-spawned-vessel attempt '{RecordingStore.PendingTree.TreeName}'",
                preserveIrreversibleTails,
                "DiscardPendingTree");
        }
    }
}
