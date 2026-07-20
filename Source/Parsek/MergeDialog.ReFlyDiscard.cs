using System.Collections.Generic;

namespace Parsek
{
    public static partial class MergeDialog
    {
        /// <summary>
        /// Implements the "Discard" branch of the dialog. Does NOT call
        /// <see cref="RecordingStore.MarkTreeAsApplied"/>: the tree is
        /// removed from storage by <see cref="RecordingStore.DiscardPendingTree"/>
        /// so there is no surviving caller that needs recording indexes advanced.
        ///
        /// <para>Refuses with a Warn log + screen message if a merge journal
        /// is active (<see cref="ParsekScenario.ActiveMergeJournal"/>) -
        /// mirrors <see cref="RevertInterceptor.DiscardReFlyHandler"/>'s
        /// guard so a discard mid-merge does not race the journal
        /// finisher's rollback.</para>
        /// </summary>
        internal static void MergeDiscard(RecordingTree tree)
        {
            MergeDiscardRanToCompletion(tree);
        }

        /// <summary>
        /// <see cref="MergeDiscard"/> variant that returns a bool: true when
        /// the discard ran end-to-end and the caller may proceed with any
        /// scene-transition continuation, false when the merge-journal-active
        /// guard refused. Bug 2 (post-#876 playtest 2026-05-17) collapsed the
        /// tri-state outcome to bool — the secondary-dialog deferral case is
        /// gone because the topology-based scoped Discard now sweeps the full
        /// segment subtree and never needs a follow-up whole-tree prompt.
        /// </summary>
        /// <param name="tree">The pending tree to discard.</param>
        internal static bool MergeDiscardRanToCompletion(RecordingTree tree)
        {
            if (tree == null)
            {
                ParsekLog.Warn("MergeDialog", "MergeDiscard: tree is null - nothing to discard");
                return false;
            }

            var scenario = ParsekScenario.Instance;
            if (!object.ReferenceEquals(null, scenario) && scenario.ActiveMergeJournal != null)
            {
                ParsekLog.Warn("MergeDialog",
                    $"MergeDiscard: refusing - merge journal active " +
                    $"journal={scenario.ActiveMergeJournal.JournalId ?? "<no-id>"}");
                ParsekLog.ScreenMessage("Discard: merge in progress - retry in a moment", 3f);
                return false;
            }

            foreach (var rec in tree.Recordings.Values)
            {
                if (rec.VesselSnapshot != null)
                    CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
            }

            if (TryDiscardActiveReFlyAttempt(tree))
                return true;

            // Switch/Fly segment scoped Discard (plan §"Merge and Discard Scope").
            // Mirrors the ReFly placement: try scoped first, then fall back to
            // whole-pending-tree discard ONLY when no session was armed.
            //
            // Bug 2 (post-#876 playtest 2026-05-17): scoped Discard now sweeps
            // the topological subtree rooted at the segment recording
            // (descendants regardless of SwitchSegmentSessionId stamp). The
            // old "secondary whole-pending-tree dialog when pre-existing
            // changes remain" flow has been deleted — the broader sweep makes
            // it unnecessary in practice, and the second dialog was
            // confusing in playtest (e.g. an orphan debris recording from a
            // Breakup-during-segment falsely triggered the prompt).
            string switchSegmentReason;
            var switchSegmentDisposition =
                RecordingStore.TryDiscardActiveSwitchSegmentAttempt(
                    out switchSegmentReason);
            if (switchSegmentDisposition
                != RecordingStore.SwitchSegmentDiscardDisposition.NoActiveSession)
            {
                ParsekLog.Info("MergeDialog",
                    $"MergeDiscard: scoped switch-segment discard succeeded " +
                    $"disposition={switchSegmentDisposition} reason={switchSegmentReason}");
                ClearPendingFlag("merge dialog switch-segment scoped discard");
                ParsekLog.ScreenMessage("Switch segment discarded", 2f);
                return true;
            }

            // M1 (PR #876 round-5 review): the whole-pending-tree branch
            // below reads RecordingStore.PendingTree as its canonical input
            // (DiscardPendingTreeAndRecalculate, RefreshSaveAndQuicksaveAfterDiscard
            // and friends). A session-resolved tree from the CommittedTrees
            // slot would diverge here and we'd discard the wrong tree (or
            // no-op, leaving the session live). The earlier Re-Fly and
            // switch-segment scoped branches already had their chance — at
            // this point a tree != pending is genuinely the misrouted Bug-C
            // case the round-5 review flagged. Refuse with Warn.
            var pendingForGuard = RecordingStore.PendingTree;
            if (pendingForGuard == null
                || !object.ReferenceEquals(pendingForGuard, tree))
            {
                ParsekLog.Warn("MergeDialog",
                    $"merge-discard-tree-mismatch " +
                    $"passedTreeId={tree.Id ?? "<null>"} " +
                    $"pendingTreeId={pendingForGuard?.Id ?? "<null>"} — " +
                    "refusing whole-pending-tree discard (Re-Fly / switch-segment scoped branches already declined)");
                ClearPendingFlag("merge-discard-tree-mismatch refused");
                return false;
            }

            bool refreshSerializedPendingMarker =
                RecordingStore.HasPendingTree
                && RecordingStore.PendingTreeSerializedForSave;
            int discardedRecordingCount = tree.Recordings?.Count ?? 0;

            // #466: while the merge/discard choice is pending, mid-flight effects stay live
            // in KSP and patching is deferred. Discard must now rebuild from the committed
            // ledger immediately after the pending tree is removed.
            ParsekScenario.DiscardPendingTreeAndRecalculate("merge dialog discard");
            if (refreshSerializedPendingMarker)
            {
                RecordingStore.RefreshSaveAndQuicksaveAfterDiscard(
                    "merge dialog Tree Discard", discardedRecordingCount);
            }
            else
            {
                ParsekLog.Verbose("MergeDialog",
                    "MergeDiscard: save refresh skipped because pending tree " +
                    "was not marked as serialized");
            }
            ClearPendingFlag("merge dialog discard button");
            ParsekLog.ScreenMessage("Recording discarded", 2f);
            ParsekLog.Info("MergeDialog",
                $"User chose: Tree Discard (tree='{tree.TreeName}', " +
                $"recordings={tree.Recordings.Count})");
            return true;
        }

        /// <summary>
        /// Removes the active Re-Fly attempt's recordings, branch-point
        /// topology (session-authored branch points + dangling parent/child
        /// id refs), sidecar files, transient marker fields, and any stale
        /// <c>tree.ActiveRecordingId</c> from in-memory and on-disk storage.
        /// Shared between Discard call sites - the post-transition merge
        /// dialog (<see cref="TryDiscardActiveReFlyAttempt"/>) and the
        /// Esc/Revert dialog (<see cref="RevertInterceptor.DiscardReFlyHandler"/>)
        /// - so both paths converge on the same attempt-id collection,
        /// pruning, and topology cleanup. Pre-PR-#734 the Revert path only
        /// removed the fork from <see cref="RecordingStore.CommittedRecordings"/>,
        /// leaving the committed-tree fork dictionary entry, attempt-authored
        /// debris children created by <c>CreateBreakupChildRecording</c>,
        /// session-authored branch points, dangling parent/child id refs,
        /// and stale <c>ActiveRecordingId</c> behind for OnSave to serialise
        /// as committed mission history.
        /// Caller is responsible for clearing the marker / scenario journal
        /// and any flow-specific cleanup; this helper handles only the
        /// recording/topology side.
        /// </summary>
        internal static AttemptDiscardSummary PruneActiveReFlyAttemptOwnedTopology(
            ReFlySessionMarker marker, string callSite)
        {
            var summary = new AttemptDiscardSummary();
            if (marker == null)
            {
                ParsekLog.Verbose("MergeDialog",
                    $"PruneActiveReFlyAttemptOwnedTopology: null marker " +
                    $"callSite={callSite ?? "<none>"} - nothing to prune");
                return summary;
            }

            var tree = RewindInvoker.FindTreeForReFlyFork(marker.TreeId);
            if (tree == null)
            {
                // Degenerate case: marker points at a tree id that no
                // longer lives in PendingTree, CommittedTrees, or the
                // live activeTree. Fall back to the legacy single-id fork
                // removal so the flat committed list is at least cleaned.
                // Topology pruning is impossible without a tree handle -
                // future Re-Fly on this id would have to discover the
                // missing context another way.
                if (!string.IsNullOrEmpty(marker.ActiveReFlyRecordingId))
                {
                    var single = new HashSet<string>(System.StringComparer.Ordinal)
                    {
                        marker.ActiveReFlyRecordingId,
                    };
                    summary.AttemptIds = single;
                    summary.RemovedCommitted = RemoveCommittedAttemptRecordings(single);
                }
                ParsekLog.Warn("MergeDialog",
                    $"PruneActiveReFlyAttemptOwnedTopology: no in-memory tree found " +
                    $"for treeId={marker.TreeId ?? "<none>"} callSite={callSite ?? "<none>"} " +
                    $"sess={marker.SessionId ?? "<none>"} - falling back to single-id removal " +
                    $"(lost descendant + topology cleanup; removedCommitted={summary.RemovedCommitted})");
                return summary;
            }

            summary.AttemptIds = CollectReFlyAttemptOwnedRecordingIds(tree, marker);
            summary.RemovedCommitted = RemoveCommittedAttemptRecordings(summary.AttemptIds);
            summary.PurgedEvents = GameStateStore.PurgeEventsForRecordings(
                summary.AttemptIds,
                $"{callSite ?? "PruneActiveReFlyAttemptOwnedTopology"} sess={marker.SessionId ?? "<no-id>"}");
            summary.DeletedFiles = DeleteAttemptRecordingFiles(tree, summary.AttemptIds);
            summary.PrunedCommittedTreeEntries = PruneAttemptRecordingsFromCommittedTrees(
                summary.AttemptIds, marker);
            summary.TransientCleared = ClearReFlyAttemptTransientFields(
                tree, marker, summary.AttemptIds);

            ParsekLog.Info("MergeDialog",
                $"PruneActiveReFlyAttemptOwnedTopology callSite={callSite ?? "<none>"} " +
                $"sess={marker.SessionId ?? "<none>"} " +
                $"tree={tree.Id ?? "<none>"} " +
                $"attemptIds={summary.AttemptIds.Count} " +
                $"removedCommitted={summary.RemovedCommitted} " +
                $"purgedEvents={summary.PurgedEvents} " +
                $"deletedFiles={summary.DeletedFiles} " +
                $"prunedCommittedTreeEntries={summary.PrunedCommittedTreeEntries} " +
                $"transientCleared={summary.TransientCleared}");

            return summary;
        }

        /// <summary>
        /// Per-call counters from <see cref="PruneActiveReFlyAttemptOwnedTopology"/>.
        /// Callers use the counts in their own end-of-flow summary log lines
        /// and tests assert on them directly.
        /// </summary>
        internal struct AttemptDiscardSummary
        {
            internal HashSet<string> AttemptIds;
            internal int RemovedCommitted;
            internal int PurgedEvents;
            internal int DeletedFiles;
            internal int PrunedCommittedTreeEntries;
            internal int TransientCleared;
        }

        /// <summary>
        /// Re-Fly-specific Discard branch for the scene-exit merge dialog.
        /// A Re-Fly pending tree often reuses the original committed tree id;
        /// the ordinary tree-discard path would route through
        /// <see cref="TreeDiscardPurge.PurgeTree"/> and purge the mission's
        /// persistent rewind/supersede/tombstone state. This path abandons
        /// only the active session attempt and leaves the committed mission
        /// timeline intact.
        /// </summary>
        internal static bool TryDiscardActiveReFlyAttempt(RecordingTree tree)
        {
            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario))
                return false;

            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null)
                return false;

            // Defensive belt-and-braces guard for any caller that bypasses
            // MergeDiscard's gate (test seam, future call site). Mirrors
            // RevertInterceptor.DiscardReFlyHandler's guard at
            // RevertInterceptor.cs:345-352.
            if (scenario.ActiveMergeJournal != null)
            {
                ParsekLog.Warn("MergeDialog",
                    $"TryDiscardActiveReFlyAttempt: refusing - merge journal active " +
                    $"sess={marker.SessionId ?? "<no-id>"} " +
                    $"journal={scenario.ActiveMergeJournal.JournalId ?? "<no-id>"}");
                ParsekLog.ScreenMessage("Discard Re-Fly: merge in progress - retry in a moment", 3f);
                return false;
            }

            if (!IsReFlyMarkerScopedToTree(marker, tree))
            {
                ParsekLog.Warn("MergeDialog",
                    $"TryDiscardActiveReFlyAttempt: active marker sess={marker.SessionId ?? "<no-id>"} " +
                    $"tree={marker.TreeId ?? "<none>"} does not match dialog tree={tree?.Id ?? "<none>"} - " +
                    "falling back to regular tree discard");
                return false;
            }

            string sessionId = marker.SessionId;
            var attemptIds = CollectReFlyAttemptOwnedRecordingIds(tree, marker);

            int removedCommitted = RemoveCommittedAttemptRecordings(attemptIds);
            int purgedEvents = GameStateStore.PurgeEventsForRecordings(
                attemptIds,
                $"MergeDialog Re-Fly discard sess={sessionId ?? "<no-id>"}");
            int deletedFiles = DeleteAttemptRecordingFiles(tree, attemptIds);
            int prunedCommittedTreeEntries = PruneAttemptRecordingsFromCommittedTrees(
                attemptIds, marker);
            int transientCleared = ClearReFlyAttemptTransientFields(tree, marker, attemptIds);
            bool committedTreeDetached = !CommittedTreeExists(tree.Id);
            bool rpPromoted = PromoteOriginRewindPointForDiscard(scenario, marker);
            int discardedSessionRps = PurgeDiscardedSessionRewindPoints(scenario, marker);
            bool restoredCommittedTree = committedTreeDetached
                && RestoreSanitizedPendingTreeIfDetached(tree, marker, attemptIds);

            RecordingStore.PopPendingTree();
            GameStateRecorder.PendingScienceSubjects.Clear();
            RecordingStore.ClearRewindReplayTargetScope();

            scenario.ActiveReFlySessionMarker = null;
            Parsek.Rendering.RenderSessionState.Clear("marker-cleared");
            scenario.ActiveMergeJournal = null;
            // Live variant (route-timeline events): the Re-Fly discard dialog
            // choice is player-driven; a route whose sources this discard
            // restores or retires stamps its auto-pause / auto-resume marker.
            scenario.BumpSupersedeStateVersionLive();
            ReFlyRevertButtonGate.Apply("MergeDialog:discard-refly-attempt");
            SupersedeCommit.ClearPreReFlyAnchorSnapshotsForSession(sessionId);

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions(
                ParsekScenario.GetCurrentTimelineUTForLedgerRecalc(),
                "merge-dialog-discard-refly");
            ClearPendingFlag();
            bool durableSaved = SaveDiscardedReFlyStateDurably(sessionId);

            ParsekLog.ScreenMessage("Re-Fly attempt discarded", 2f);
            ParsekLog.Info("MergeDialog",
                $"User chose: Re-Fly Attempt Discard (tree='{tree.TreeName}', " +
                $"treeId={tree.Id ?? "<none>"}, sess={sessionId ?? "<no-id>"}, " +
                $"origin={marker.OriginChildRecordingId ?? "<none>"}, " +
                $"active={marker.ActiveReFlyRecordingId ?? "<none>"}, " +
                $"attemptIds={attemptIds.Count}, removedCommitted={removedCommitted}, " +
                $"purgedEvents={purgedEvents}, deletedFiles={deletedFiles}, " +
                $"prunedCommittedTreeEntries={prunedCommittedTreeEntries}, " +
                $"transientCleared={transientCleared}, " +
                $"rpPromoted={rpPromoted}, discardedSessionRps={discardedSessionRps}, " +
                $"restoredCommittedTree={restoredCommittedTree}, durableSaved={durableSaved})");
            ParsekLog.Info("ReFlySession",
                $"End reason=discardReFlyAttemptFromMergeDialog sess={sessionId ?? "<no-id>"} " +
                $"tree={tree.Id ?? "<none>"} active={marker.ActiveReFlyRecordingId ?? "<none>"} " +
                $"origin={marker.OriginChildRecordingId ?? "<none>"}");
            return true;
        }

        private static bool IsReFlyMarkerScopedToTree(
            ReFlySessionMarker marker, RecordingTree tree)
        {
            if (marker == null || tree == null)
                return false;
            if (string.IsNullOrEmpty(marker.TreeId))
                return true;
            return string.Equals(marker.TreeId, tree.Id, System.StringComparison.Ordinal);
        }

        private static HashSet<string> CollectReFlyAttemptOwnedRecordingIds(
            RecordingTree tree, ReFlySessionMarker marker)
        {
            var ids = new HashSet<string>(System.StringComparer.Ordinal);
            if (marker == null)
                return ids;

            AddAttemptIdIfSafe(ids, marker.ActiveReFlyRecordingId, marker, tree);

            if (tree == null || tree.Recordings == null)
                return ids;

            foreach (var rec in tree.Recordings.Values)
            {
                if (!IsReFlyAttemptOwnedRecording(rec, marker))
                    continue;
                AddAttemptIdIfSafe(ids, rec.RecordingId, marker, tree);
            }

            // Catch attempt-authored child recordings linked to session-authored
            // branch points. Production helpers like ParsekFlight.CreateBreakupChildRecording
            // do NOT propagate the marker's SessionId / RewindPointId /
            // SupersedeTargetId onto the new child Recording - they only
            // append the new id to bp.ChildRecordingIds. As a result the
            // marker-tag scan above misses them, so without this descendant
            // walk Discard would leave abandoned debris/children in
            // tree.Recordings (and on next save in CommittedRecordings) for
            // OnSave to serialise as committed mission history. The walk is
            // a single linear pass because chained breakups (child A spawns
            // its own breakup BP_B with grand-child B) produce ANOTHER
            // session-authored branch point, which the same loop visits.
            AddSessionBranchPointDescendantAttemptIds(ids, tree, marker);

            return ids;
        }

        private static void AddSessionBranchPointDescendantAttemptIds(
            HashSet<string> ids, RecordingTree tree, ReFlySessionMarker marker)
        {
            if (ids == null || tree == null || tree.BranchPoints == null
                || marker == null)
            {
                return;
            }

            // A null PreSessionBranchPointIds baseline means the marker was
            // written by code that does not snapshot branch-point ids - we
            // cannot tell session-authored from pre-session BPs, so skip
            // (mirrors PruneSessionCreatedBranchPoints' null-baseline skip).
            if (marker.PreSessionBranchPointIds == null)
                return;

            var preSessionBpIds = new HashSet<string>(
                marker.PreSessionBranchPointIds,
                System.StringComparer.Ordinal);

            int adopted = 0;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                var bp = tree.BranchPoints[i];
                if (bp == null || string.IsNullOrEmpty(bp.Id))
                    continue;
                if (preSessionBpIds.Contains(bp.Id))
                    continue;
                if (bp.ChildRecordingIds == null)
                    continue;

                for (int j = 0; j < bp.ChildRecordingIds.Count; j++)
                {
                    string childId = bp.ChildRecordingIds[j];
                    if (string.IsNullOrEmpty(childId))
                        continue;
                    if (ids.Contains(childId))
                        continue;
                    // Defence in depth: the protected origin/supersede-target
                    // ids should never appear as a session-authored BP child
                    // in production (origin pre-existed the attempt; supersede
                    // targets are pre-existing too), but skip them anyway.
                    if (IsProtectedReFlyRecordingId(childId, marker))
                        continue;

                    ids.Add(childId);
                    adopted++;
                }
            }

            if (adopted > 0)
            {
                ParsekLog.Info("MergeDialog",
                    $"AddSessionBranchPointDescendantAttemptIds: " +
                    $"adopted {adopted} attempt-authored child recording(s) " +
                    $"linked to session-authored branch points " +
                    $"(tree={tree.Id ?? "<none>"}, sess={marker.SessionId ?? "<none>"})");
            }
        }

        private static bool IsReFlyAttemptOwnedRecording(
            Recording rec, ReFlySessionMarker marker)
        {
            if (rec == null || marker == null || string.IsNullOrEmpty(rec.RecordingId))
                return false;

            if (!string.IsNullOrEmpty(marker.ActiveReFlyRecordingId)
                && string.Equals(rec.RecordingId,
                    marker.ActiveReFlyRecordingId, System.StringComparison.Ordinal))
                return true;

            if (!string.IsNullOrEmpty(marker.SessionId)
                && string.Equals(rec.CreatingSessionId,
                    marker.SessionId, System.StringComparison.Ordinal))
                return true;

            if (!string.IsNullOrEmpty(marker.RewindPointId)
                && string.Equals(rec.ProvisionalForRpId,
                    marker.RewindPointId, System.StringComparison.Ordinal))
                return true;

            if (rec.MergeState == MergeState.NotCommitted
                && !string.IsNullOrEmpty(rec.SupersedeTargetId))
            {
                if (string.Equals(rec.SupersedeTargetId,
                        marker.OriginChildRecordingId, System.StringComparison.Ordinal))
                    return true;
                if (string.Equals(rec.SupersedeTargetId,
                        marker.SupersedeTargetId, System.StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static void AddAttemptIdIfSafe(
            HashSet<string> ids,
            string recordingId,
            ReFlySessionMarker marker,
            RecordingTree dialogTree)
        {
            if (ids == null || string.IsNullOrEmpty(recordingId))
                return;

            if (IsProtectedReFlyRecordingId(recordingId, marker))
            {
                ParsekLog.Verbose("MergeDialog",
                    $"TryDiscardActiveReFlyAttempt: protected original rec={recordingId} " +
                    "excluded from attempt discard");
                return;
            }

            if (CommittedTreeContainsRecording(dialogTree?.Id, recordingId))
            {
                // AtomicMarkerWrite attaches the in-place fork to whichever
                // tree owns origin's tree id (pending OR committed). When no
                // pending tree exists at marker-write time, the fork is
                // attached to the committed tree as a NotCommitted recording.
                // That fork is the active Re-Fly attempt itself, not committed
                // mission history, so the guard must let it through; the
                // tree-pruning step in TryDiscardActiveReFlyAttempt removes
                // the entry from the committed tree's Recordings dictionary
                // so OnSave does not serialise the fork as history.
                if (IsMarkerOwnedNotCommittedFork(recordingId, marker))
                {
                    ParsekLog.Info("MergeDialog",
                        $"TryDiscardActiveReFlyAttempt: rec={recordingId} is the marker-owned " +
                        $"NotCommitted Re-Fly fork attached to committed tree " +
                        $"{dialogTree?.Id ?? "<none>"}; including in attempt discard");
                    ids.Add(recordingId);
                    return;
                }

                ParsekLog.Warn("MergeDialog",
                    $"TryDiscardActiveReFlyAttempt: rec={recordingId} exists in committed tree " +
                    $"{dialogTree?.Id ?? "<none>"} - excluding from attempt discard to protect mission history");
                return;
            }

            ids.Add(recordingId);
        }

        private static bool IsMarkerOwnedNotCommittedFork(
            string recordingId, ReFlySessionMarker marker)
        {
            if (string.IsNullOrEmpty(recordingId) || marker == null)
                return false;
            var rec = LookupCommittedRecordingById(recordingId);
            if (rec == null || rec.MergeState != MergeState.NotCommitted)
                return false;
            return IsReFlyAttemptOwnedRecording(rec, marker);
        }

        private static Recording LookupCommittedRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return null;
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null)
                return null;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec != null
                    && string.Equals(rec.RecordingId, recordingId,
                        System.StringComparison.Ordinal))
                {
                    return rec;
                }
            }
            return null;
        }

        private static bool IsProtectedReFlyRecordingId(
            string recordingId, ReFlySessionMarker marker)
        {
            if (string.IsNullOrEmpty(recordingId) || marker == null)
                return true;
            if (string.Equals(recordingId,
                    marker.OriginChildRecordingId, System.StringComparison.Ordinal))
                return true;
            if (string.Equals(recordingId,
                    marker.SupersedeTargetId, System.StringComparison.Ordinal))
                return true;
            return false;
        }

        private static bool CommittedTreeContainsRecording(
            string treeId, string recordingId)
        {
            if (string.IsNullOrEmpty(treeId) || string.IsNullOrEmpty(recordingId))
                return false;

            var committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees == null)
                return false;

            for (int i = 0; i < committedTrees.Count; i++)
            {
                var committedTree = committedTrees[i];
                if (committedTree == null || committedTree.Recordings == null)
                    continue;
                if (!string.Equals(committedTree.Id, treeId, System.StringComparison.Ordinal))
                    continue;
                if (committedTree.Recordings.ContainsKey(recordingId))
                    return true;
            }

            return false;
        }

        private static int RemoveCommittedAttemptRecordings(HashSet<string> attemptIds)
        {
            if (attemptIds == null || attemptIds.Count == 0)
                return 0;

            int removed = 0;
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null)
                return 0;

            for (int i = committed.Count - 1; i >= 0; i--)
            {
                var rec = committed[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (!attemptIds.Contains(rec.RecordingId))
                    continue;

                if (RecordingStore.RemoveCommittedInternal(rec))
                {
                    removed++;
                    ParsekLog.Info("RecordingStore",
                        $"Removed Re-Fly attempt rec={rec.RecordingId} during merge-dialog discard");
                }
            }

            return removed;
        }

        private static int DeleteAttemptRecordingFiles(
            RecordingTree tree, HashSet<string> attemptIds)
        {
            if (tree == null || tree.Recordings == null
                || attemptIds == null || attemptIds.Count == 0)
                return 0;

            int deleted = 0;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (!attemptIds.Contains(rec.RecordingId))
                    continue;
                RecordingStore.DeleteRecordingFiles(rec);
                deleted++;
            }
            return deleted;
        }

        /// <summary>
        /// Removes attempt-owned recording entries AND attempt-authored
        /// topology (branch points, branch-point parent/child id references,
        /// recording parent/child branch-point ids) from any committed tree
        /// the in-place attempt mutated. Required for the committed-tree-attach
        /// shape where <see cref="RewindInvoker.AtomicMarkerWrite"/> attached the
        /// in-place fork to a committed tree because no pending tree existed.
        /// Without this, the fork dictionary entry, any session-authored branch
        /// points (e.g. an in-flight stage separation that ran before the
        /// merge dialog), and dangling parent/child id refs would survive
        /// discard and OnSave would serialise them as committed mission history.
        /// The pending-tree case is naturally handled by
        /// <see cref="RecordingStore.PopPendingTree"/> later in the discard flow.
        /// </summary>
        private static int PruneAttemptRecordingsFromCommittedTrees(
            HashSet<string> attemptIds, ReFlySessionMarker marker)
        {
            if (attemptIds == null || attemptIds.Count == 0)
                return 0;

            int prunedRecordings = 0;
            int totalScrubbedBranchPointRefs = 0;
            int totalRemovedSessionBranchPoints = 0;
            var committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees == null)
                return 0;

            for (int i = 0; i < committedTrees.Count; i++)
            {
                var committedTree = committedTrees[i];
                if (committedTree == null || committedTree.Recordings == null)
                    continue;

                int prunedHere = 0;
                foreach (var attemptId in attemptIds)
                {
                    if (string.IsNullOrEmpty(attemptId))
                        continue;
                    if (committedTree.Recordings.Remove(attemptId))
                        prunedHere++;
                }

                // Repair stale ActiveRecordingId. RestoreActiveTreeFromPending's
                // markerSwap branch sets `tree.ActiveRecordingId = marker.ActiveReFlyRecordingId`
                // (the fork id) when the marker takes over an active tree.
                // The fork was just removed from `committedTree.Recordings`,
                // so leaving ActiveRecordingId pointing at the deleted id
                // would round-trip through OnSave and any consumer reading
                // `tree.ActiveRecordingId ?? tree.RootRecordingId` raw
                // (e.g. ParsekFlight's recorder-rebind seed) would target a
                // missing recording on the next session. Mirrors the equivalent
                // repair `RestoreSanitizedPendingTreeIfDetached` already does
                // for the detached-pending path.
                if (prunedHere > 0
                    && !string.IsNullOrEmpty(committedTree.ActiveRecordingId)
                    && !committedTree.Recordings.ContainsKey(committedTree.ActiveRecordingId))
                {
                    string oldActive = committedTree.ActiveRecordingId;
                    committedTree.ActiveRecordingId =
                        marker != null
                        && !string.IsNullOrEmpty(marker.OriginChildRecordingId)
                        && committedTree.Recordings.ContainsKey(marker.OriginChildRecordingId)
                            ? marker.OriginChildRecordingId
                            : null;
                    ParsekLog.Info("MergeDialog",
                        $"PruneAttemptRecordingsFromCommittedTrees: reset stale " +
                        $"tree.ActiveRecordingId from '{oldActive}' to " +
                        $"'{committedTree.ActiveRecordingId ?? "<null>"}' on tree={committedTree.Id ?? "<none>"} " +
                        $"(prior id was an attempt recording removed by this prune)");
                }

                // Session-authored branch points (e.g. an in-flight stage
                // separation booked during the abandoned attempt) must be
                // dropped too. Done BEFORE the topology scrub so the scrub
                // only walks BPs that will survive Discard - any work on a
                // about-to-be-removed BP is wasted. PreSessionBranchPointIds
                // is captured against marker.TreeId only, so applying it to
                // a different committed tree would erroneously delete that
                // tree's unrelated branch points - gate on tree id match.
                int removedSessionBps = 0;
                if (marker != null
                    && !string.IsNullOrEmpty(marker.TreeId)
                    && string.Equals(committedTree.Id, marker.TreeId,
                        System.StringComparison.Ordinal))
                {
                    removedSessionBps = PruneSessionCreatedBranchPoints(
                        committedTree, marker);
                }

                // Branch-point ParentRecordingIds / ChildRecordingIds on
                // surviving BPs may reference now-removed attempt recordings;
                // scrub them so serialised topology never points at deleted
                // ids. Mirrors RemoveAttemptRecordingsFromTree for the
                // detached-pending path.
                int scrubbedRefs = ScrubAttemptIdsFromBranchPointTopology(
                    committedTree, attemptIds);

                if (prunedHere > 0 || scrubbedRefs > 0 || removedSessionBps > 0)
                {
                    // The fork's pid lived in the recorder's active slot AND
                    // in the tree's background map. After pruning, the map
                    // must be rebuilt so the abandoned pid does not survive
                    // as a stale background entry.
                    committedTree.RebuildBackgroundMap();
                    ParsekLog.Info("MergeDialog",
                        $"PruneAttemptRecordingsFromCommittedTrees: " +
                        $"tree={committedTree.Id ?? "<none>"} " +
                        $"prunedRecordings={prunedHere} " +
                        $"scrubbedBranchPointRefs={scrubbedRefs} " +
                        $"removedSessionBranchPoints={removedSessionBps}");
                }

                prunedRecordings += prunedHere;
                totalScrubbedBranchPointRefs += scrubbedRefs;
                totalRemovedSessionBranchPoints += removedSessionBps;
            }

            if (totalScrubbedBranchPointRefs > 0
                || totalRemovedSessionBranchPoints > 0)
            {
                ParsekLog.Verbose("MergeDialog",
                    $"PruneAttemptRecordingsFromCommittedTrees totals: " +
                    $"prunedRecordings={prunedRecordings} " +
                    $"scrubbedBranchPointRefs={totalScrubbedBranchPointRefs} " +
                    $"removedSessionBranchPoints={totalRemovedSessionBranchPoints}");
            }

            return prunedRecordings;
        }

        private static int ScrubAttemptIdsFromBranchPointTopology(
            RecordingTree tree, HashSet<string> attemptIds)
        {
            if (tree == null || tree.BranchPoints == null
                || attemptIds == null || attemptIds.Count == 0)
                return 0;

            int scrubbed = 0;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                var bp = tree.BranchPoints[i];
                if (bp == null) continue;
                scrubbed += CountAndRemoveAttemptIds(bp.ParentRecordingIds, attemptIds);
                scrubbed += CountAndRemoveAttemptIds(bp.ChildRecordingIds, attemptIds);
            }
            return scrubbed;
        }

        private static int CountAndRemoveAttemptIds(
            List<string> recordingIds, HashSet<string> attemptIds)
        {
            if (recordingIds == null || attemptIds == null || attemptIds.Count == 0)
                return 0;
            int removed = 0;
            for (int i = recordingIds.Count - 1; i >= 0; i--)
            {
                if (attemptIds.Contains(recordingIds[i]))
                {
                    recordingIds.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        private static int ClearReFlyAttemptTransientFields(
            RecordingTree tree,
            ReFlySessionMarker marker,
            HashSet<string> attemptIds)
        {
            int cleared = 0;
            cleared += ClearTransientFieldsInRecordings(
                RecordingStore.CommittedRecordings, marker, attemptIds);
            if (tree != null && tree.Recordings != null)
                cleared += ClearTransientFieldsInRecordings(
                    tree.Recordings.Values, marker, attemptIds);
            return cleared;
        }

        private static int ClearTransientFieldsInRecordings(
            IEnumerable<Recording> recordings,
            ReFlySessionMarker marker,
            HashSet<string> attemptIds)
        {
            if (recordings == null || marker == null)
                return 0;

            int cleared = 0;
            foreach (var rec in recordings)
            {
                if (rec == null)
                    continue;

                bool sessionOwned = !string.IsNullOrEmpty(marker.SessionId)
                    && string.Equals(rec.CreatingSessionId,
                        marker.SessionId, System.StringComparison.Ordinal);
                bool rpOwned = !string.IsNullOrEmpty(marker.RewindPointId)
                    && string.Equals(rec.ProvisionalForRpId,
                        marker.RewindPointId, System.StringComparison.Ordinal);
                bool attemptOwned = attemptIds != null
                    && !string.IsNullOrEmpty(rec.RecordingId)
                    && attemptIds.Contains(rec.RecordingId);

                if (!sessionOwned && !rpOwned && !attemptOwned)
                    continue;

                if (!string.IsNullOrEmpty(rec.CreatingSessionId)
                    || !string.IsNullOrEmpty(rec.ProvisionalForRpId)
                    || !string.IsNullOrEmpty(rec.SupersedeTargetId))
                {
                    cleared++;
                }

                rec.CreatingSessionId = null;
                rec.ProvisionalForRpId = null;
                rec.SupersedeTargetId = null;
            }
            return cleared;
        }

        private static bool RestoreSanitizedPendingTreeIfDetached(
            RecordingTree tree,
            ReFlySessionMarker marker,
            HashSet<string> attemptIds)
        {
            if (tree == null)
                return false;
            if (CommittedTreeExists(tree.Id))
                return false;

            int prunedRecordings = RemoveAttemptRecordingsFromTree(tree, attemptIds);
            int prunedSessionBranchPoints = PruneSessionCreatedBranchPoints(tree, marker);
            if (tree.Recordings == null || tree.Recordings.Count == 0)
            {
                // Expected unreachable in production: AddAttemptIdIfSafe protects
                // the origin id, so a valid Re-Fly tree keeps at least that recording.
                ParsekLog.Warn("MergeDialog",
                    $"RestoreSanitizedPendingTreeIfDetached: tree={tree.Id ?? "<none>"} " +
                    "has no recordings after Re-Fly attempt pruning - cannot restore committed tree");
                return false;
            }

            if (string.IsNullOrEmpty(tree.ActiveRecordingId)
                || !tree.Recordings.ContainsKey(tree.ActiveRecordingId))
            {
                tree.ActiveRecordingId = !string.IsNullOrEmpty(marker?.OriginChildRecordingId)
                    && tree.Recordings.ContainsKey(marker.OriginChildRecordingId)
                        ? marker.OriginChildRecordingId
                        : FirstRecordingId(tree);
            }
            if (string.IsNullOrEmpty(tree.RootRecordingId)
                || !tree.Recordings.ContainsKey(tree.RootRecordingId))
            {
                tree.RootRecordingId = tree.ActiveRecordingId ?? FirstRecordingId(tree);
            }

            foreach (var rec in tree.Recordings.Values)
            {
                if (rec != null && string.IsNullOrEmpty(rec.TreeId))
                    rec.TreeId = tree.Id;
            }

            int groupRepairs = RecordingGroupStore.RepairAutoGeneratedTreeGroups(tree);
            tree.RebuildBackgroundMap();
            int addedRecordings = 0;
            int skippedExisting = 0;
            int dirtyQueuedForScenarioSave = 0;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec == null)
                    continue;
                if (CommittedRecordingIdExists(rec.RecordingId))
                {
                    skippedExisting++;
                }
                else
                {
                    RecordingStore.AddCommittedInternal(rec);
                    addedRecordings++;
                }
                if (rec.FilesDirty)
                    dirtyQueuedForScenarioSave++;
            }
            RecordingStore.AddCommittedTreeInternal(tree);
            ParsekLog.Info("MergeDialog",
                $"RestoreSanitizedPendingTreeIfDetached: restored committed tree " +
                $"tree={tree.Id ?? "<none>"} recordings={tree.Recordings.Count} " +
                $"prunedAttemptRecordings={prunedRecordings} " +
                $"prunedSessionBranchPoints={prunedSessionBranchPoints} " +
                $"groupRepairs={groupRepairs} " +
                $"addedRecordings={addedRecordings} skippedExisting={skippedExisting} " +
                $"dirtyQueuedForScenarioSave={dirtyQueuedForScenarioSave}");
            return true;
        }

        private static int RemoveAttemptRecordingsFromTree(
            RecordingTree tree, HashSet<string> attemptIds)
        {
            if (tree == null || tree.Recordings == null
                || attemptIds == null || attemptIds.Count == 0)
                return 0;

            int removed = 0;
            foreach (string id in attemptIds)
            {
                if (string.IsNullOrEmpty(id))
                    continue;
                if (tree.Recordings.Remove(id))
                    removed++;
            }

            if (tree.BranchPoints != null)
            {
                for (int i = 0; i < tree.BranchPoints.Count; i++)
                {
                    var bp = tree.BranchPoints[i];
                    if (bp == null)
                        continue;
                    RemoveAttemptIds(bp.ParentRecordingIds, attemptIds);
                    RemoveAttemptIds(bp.ChildRecordingIds, attemptIds);
                }
            }

            return removed;
        }

        private static int PruneSessionCreatedBranchPoints(
            RecordingTree tree,
            ReFlySessionMarker marker)
        {
            if (tree == null || tree.BranchPoints == null || tree.BranchPoints.Count == 0)
                return 0;
            if (marker == null || marker.PreSessionBranchPointIds == null)
                return 0;

            // A present-but-empty baseline is intentional: the marker was
            // written by code that snapshots branch point IDs, and the tree had
            // no branch points at invocation. In that case every current branch
            // point is session-authored and must be discarded. A null baseline
            // is the legacy/unknown case and is skipped above.
            var preSessionIds = new HashSet<string>(
                marker.PreSessionBranchPointIds,
                System.StringComparer.Ordinal);
            var removedIds = new HashSet<string>(System.StringComparer.Ordinal);
            for (int i = tree.BranchPoints.Count - 1; i >= 0; i--)
            {
                var bp = tree.BranchPoints[i];
                if (bp == null || string.IsNullOrEmpty(bp.Id))
                    continue;
                if (preSessionIds.Contains(bp.Id))
                    continue;
                removedIds.Add(bp.Id);
                tree.BranchPoints.RemoveAt(i);
            }

            if (removedIds.Count == 0)
                return 0;

            if (tree.Recordings != null)
            {
                foreach (var rec in tree.Recordings.Values)
                {
                    if (rec == null)
                        continue;

                    bool changed = false;
                    if (!string.IsNullOrEmpty(rec.ParentBranchPointId)
                        && removedIds.Contains(rec.ParentBranchPointId))
                    {
                        rec.ParentBranchPointId = null;
                        changed = true;
                    }
                    if (!string.IsNullOrEmpty(rec.ChildBranchPointId)
                        && removedIds.Contains(rec.ChildBranchPointId))
                    {
                        rec.ChildBranchPointId = null;
                        changed = true;
                    }

                    if (changed)
                        rec.MarkFilesDirty();
                }
            }

            ParsekLog.Verbose("MergeDialog",
                $"PruneSessionCreatedBranchPoints: tree={tree.Id ?? "<none>"} " +
                $"sess={marker.SessionId ?? "<none>"} removed={removedIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            return removedIds.Count;
        }

        private static void RemoveAttemptIds(
            List<string> recordingIds, HashSet<string> attemptIds)
        {
            if (recordingIds == null || attemptIds == null || attemptIds.Count == 0)
                return;
            for (int i = recordingIds.Count - 1; i >= 0; i--)
            {
                if (attemptIds.Contains(recordingIds[i]))
                    recordingIds.RemoveAt(i);
            }
        }

        private static string FirstRecordingId(RecordingTree tree)
        {
            if (tree == null || tree.Recordings == null)
                return null;
            foreach (var kvp in tree.Recordings)
                return kvp.Key;
            return null;
        }

        private static bool CommittedTreeExists(string treeId)
        {
            if (string.IsNullOrEmpty(treeId))
                return false;
            var committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees == null)
                return false;
            for (int i = 0; i < committedTrees.Count; i++)
            {
                var committedTree = committedTrees[i];
                if (committedTree == null)
                    continue;
                if (string.Equals(committedTree.Id, treeId, System.StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool CommittedRecordingIdExists(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return false;
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null)
                return false;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null)
                    continue;
                if (string.Equals(rec.RecordingId, recordingId, System.StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static RewindPoint FindRewindPointForMarker(
            ParsekScenario scenario, ReFlySessionMarker marker)
        {
            if (object.ReferenceEquals(null, scenario)
                || scenario.RewindPoints == null
                || marker == null
                || string.IsNullOrEmpty(marker.RewindPointId))
            {
                return null;
            }

            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                var rp = scenario.RewindPoints[i];
                if (rp == null)
                    continue;
                if (string.Equals(rp.RewindPointId,
                        marker.RewindPointId, System.StringComparison.Ordinal))
                    return rp;
            }
            return null;
        }

        private static bool PromoteOriginRewindPointForDiscard(
            ParsekScenario scenario,
            ReFlySessionMarker marker)
        {
            RewindPoint rp = FindRewindPointForMarker(scenario, marker);
            if (rp == null)
                return false;

            bool promoted = rp.SessionProvisional;
            bool hadCreatingSessionId = !string.IsNullOrEmpty(rp.CreatingSessionId);
            rp.SessionProvisional = false;
            rp.CreatingSessionId = null;
            if (promoted)
            {
                ParsekLog.Info("ReFlySession",
                    $"Origin RP promoted to persistent rp={marker.RewindPointId} " +
                    $"sess={marker.SessionId ?? "<no-id>"} reason=discardReFlyAttemptFromMergeDialog");
            }
            else if (hadCreatingSessionId)
            {
                ParsekLog.Info("ReFlySession",
                    $"Origin RP session metadata cleared rp={marker.RewindPointId} " +
                    $"sess={marker.SessionId ?? "<no-id>"} reason=discardReFlyAttemptFromMergeDialog");
            }
            else
            {
                ParsekLog.Verbose("ReFlySession",
                    $"Origin RP already persistent rp={marker.RewindPointId} " +
                    $"sess={marker.SessionId ?? "<no-id>"} reason=discardReFlyAttemptFromMergeDialog");
            }
            return promoted;
        }

        private static int PurgeDiscardedSessionRewindPoints(
            ParsekScenario scenario,
            ReFlySessionMarker marker)
        {
            if (object.ReferenceEquals(null, scenario)
                || scenario.RewindPoints == null
                || marker == null
                || string.IsNullOrEmpty(marker.SessionId))
            {
                return 0;
            }

            int removed = 0;
            for (int i = scenario.RewindPoints.Count - 1; i >= 0; i--)
            {
                RewindPoint rp = scenario.RewindPoints[i];
                if (rp == null)
                    continue;
                if (!rp.SessionProvisional)
                    continue;
                if (!string.Equals(rp.CreatingSessionId,
                        marker.SessionId, System.StringComparison.Ordinal))
                    continue;
                if (!string.IsNullOrEmpty(marker.RewindPointId)
                    && string.Equals(rp.RewindPointId,
                        marker.RewindPointId, System.StringComparison.Ordinal))
                    continue;

                RewindPointReaper.TryDeleteQuicksaveFile(rp);
                scenario.RewindPoints.RemoveAt(i);
                RecordingsTableUI.ClearRewindSlotCanInvokeLogState(rp.RewindPointId);
                RewindPointReaper.ClearBranchPointBackref(rp);
                ParsekLog.Info("ReFlySession",
                    $"Discard removed session provisional RP rp={rp.RewindPointId ?? "<no-id>"} " +
                    $"bp={rp.BranchPointId ?? "<no-bp>"} sess={marker.SessionId ?? "<no-id>"}");
                removed++;
            }

            if (removed > 0)
            {
                ParsekLog.Info("ReFlySession",
                    $"Discard removed {removed.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"session provisional RP(s) sess={marker.SessionId ?? "<no-id>"}");
            }
            return removed;
        }

        private static bool SaveDiscardedReFlyStateDurably(string sessionId)
        {
            var saveFn = RecordingStore.SaveGameForTesting ?? GamePersistence.SaveGame;
            if (RecordingStore.SaveGameForTesting == null)
            {
                if (HighLogic.LoadedScene == GameScenes.LOADING)
                {
                    ParsekLog.Verbose("MergeDialog",
                        $"Discard Re-Fly durable save skipped during LOADING sess={sessionId ?? "<no-id>"}");
                    return false;
                }
                if (HighLogic.CurrentGame == null
                    || string.IsNullOrEmpty(HighLogic.SaveFolder))
                {
                    ParsekLog.Verbose("MergeDialog",
                        $"Discard Re-Fly durable save skipped (no current game/save folder) " +
                        $"sess={sessionId ?? "<no-id>"}");
                    return false;
                }
            }
            // SaveGameForTesting deliberately bypasses the scene/current-game
            // guards above so xUnit can assert the durable-save call without
            // constructing a live KSP HighLogic state. Test hooks ignore the
            // SaveFolder argument.

            try
            {
                string result = saveFn("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                if (string.IsNullOrEmpty(result))
                {
                    ParsekLog.Warn("MergeDialog",
                        $"Discard Re-Fly durable save returned null sess={sessionId ?? "<no-id>"}");
                    return false;
                }

                ParsekLog.Info("MergeDialog",
                    $"Discard Re-Fly state persisted via persistent.sfs sess={sessionId ?? "<no-id>"}");
                return true;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn("MergeDialog",
                    $"Discard Re-Fly durable save threw sess={sessionId ?? "<no-id>"} " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}
