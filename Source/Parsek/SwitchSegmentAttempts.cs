using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Parsek
{
    /// <summary>
    /// Switch-segment attempts (RecordingStore services plan, service J): the scoped Discard of
    /// an armed <see cref="SwitchSegmentSession"/> (<see cref="TryDiscardActiveSwitchSegmentAttempt"/>:
    /// the segment subtree's recordings, branch points, game-state events and sidecars go, the
    /// irreversible ledger tail is preserved, a committed-restore clone is dropped), the no-op
    /// classification of a resumed segment (<see cref="TryClassifyActiveSwitchSegmentNoOp"/>,
    /// over the pure <see cref="SwitchSegmentNoOpClassifier"/>), the segment-subtree walks they
    /// share, and the marker-ownership predicate the restore-attempt suppression consults. No
    /// state: the session marker lives on <see cref="ParsekScenario"/>.
    /// </summary>
    internal static class SwitchSegmentAttempts
    {
        /// <summary>
        /// Returns true when <paramref name="recordingId"/> identifies a recording
        /// owned by the currently active <see cref="SwitchSegmentSession"/> — i.e.
        /// the recording's <see cref="Recording.SwitchSegmentSessionId"/> matches
        /// the live session's <c>SessionId</c>. Used by #866 suppression / save
        /// sites to exempt marker-owned new segment recording ids from same-id
        /// committed-tree restore-attempt suppression. Returns false when no
        /// session is armed, no matching recording is found, or the recording's
        /// stamp belongs to a different / no session.
        ///
        /// <para>Looks up the recording in committed storage, then the pending
        /// tree, then the active tree (via <see cref="ParsekFlight.Instance"/>).
        /// Pure ownership query: no Unity globals and no mutation of suppression
        /// state. <c>[ERS-exempt]</c> rationale: this is a metadata-only
        /// ownership predicate that walks the raw committed list to match by
        /// recording id — exactly the kind of structural query the allowlist
        /// already grants <c>RecordingStore.cs</c>.</para>
        /// </summary>
        internal static bool IsMarkerOwnedSwitchSegmentRecordingId(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return false;

            var session = ParsekScenario.Instance?.ActiveSwitchSegmentSession;
            if (session == null)
                return false;

            Recording rec = FindRecordingByIdAcrossStores(recordingId);
            if (rec == null || string.IsNullOrEmpty(rec.SwitchSegmentSessionId))
                return false;

            string activeSessionId = session.SessionId.ToString(
                "D",
                CultureInfo.InvariantCulture);
            return string.Equals(
                rec.SwitchSegmentSessionId,
                activeSessionId,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Looks up a Recording by id across committed storage, the pending tree,
        /// the saved-pending-during-active-restore tree, and the active tree.
        /// Returns null if no matching recording is found anywhere. Used only by
        /// the switch-segment ownership predicate.
        /// </summary>
        private static Recording FindRecordingByIdAcrossStores(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return null;

            Recording rec = RecordingStore.TryFindCommittedRecordingById(recordingId);
            if (rec != null)
                return rec;

            if (PendingTreeSlot.PendingTree?.Recordings != null
                && PendingTreeSlot.PendingTree.Recordings.TryGetValue(recordingId, out rec))
            {
                return rec;
            }

            if (PendingTreeSlot.SavedPendingTreeDuringActiveRestore?.Recordings != null
                && PendingTreeSlot.SavedPendingTreeDuringActiveRestore.Recordings.TryGetValue(
                    recordingId,
                    out rec))
            {
                return rec;
            }

            var activeTree = ParsekFlight.Instance?.ActiveTreeForSerialization;
            if (activeTree?.Recordings != null
                && activeTree.Recordings.TryGetValue(recordingId, out rec))
            {
                return rec;
            }

            return null;
        }

        /// <summary>
        /// Scene-exit Discard hook for an armed <see cref="SwitchSegmentSession"/>.
        /// Mirrors <see cref="MergeDialog.TryDiscardActiveReFlyAttempt"/>'s
        /// placement: <see cref="MergeDialog.MergeDiscard"/> calls this BEFORE
        /// falling back to the whole-pending-tree
        /// <see cref="DiscardPendingTree"/> path, so committed mission history
        /// is preserved when the only new pending work is the switch/Fly
        /// segment itself.
        ///
        /// <para>On success, removes the session's segment recording + every
        /// descendant in the segment subtree (regardless of
        /// <see cref="Recording.SwitchSegmentSessionId"/> stamp — debris from
        /// a Breakup-during-segment, EVA children, dock children all in
        /// scope) + their branch points + their game-state events + their
        /// sidecar files + the persisted <see cref="SwitchSegmentSession"/>
        /// marker. Preserves all committed recording IDs, their sidecars,
        /// their game-state events at or before the switch UT, and any
        /// pre-existing pending recordings the session did not author.</para>
        ///
        /// <para>Returns the disposition path so the caller can branch on
        /// clone-restore (drop the clone) vs pending-tree (segment subtree
        /// pruned in place). Returns
        /// <see cref="RecordingStore.SwitchSegmentDiscardDisposition.NoActiveSession"/> when
        /// no session is armed; the caller should fall through to the regular
        /// whole-pending-tree discard.</para>
        ///
        /// <para>[ERS-exempt] rationale: this is a topology cleanup helper
        /// that mirrors the structural-id collection already done by
        /// <see cref="MergeDialog.CollectReFlyAttemptOwnedRecordingIds"/>;
        /// the surviving committed timeline is reconstructed via the normal
        /// <see cref="EffectiveState"/> pipeline after the helper returns.</para>
        /// </summary>
        internal static RecordingStore.SwitchSegmentDiscardDisposition TryDiscardActiveSwitchSegmentAttempt(
            out string reason)
        {
            reason = null;
            var scenario = ParsekScenario.Instance;
            var session = scenario?.ActiveSwitchSegmentSession;
            if (session == null)
            {
                reason = "no-active-session";
                ParsekLog.Verbose("SwitchSegment",
                    $"TryDiscardActiveSwitchSegmentAttempt: {reason}");
                return RecordingStore.SwitchSegmentDiscardDisposition.NoActiveSession;
            }

            string sessionIdStr = session.SessionId.ToString("D", CultureInfo.InvariantCulture);
            bool isCommittedRestoreClone = IsCommittedRestoreCloneSession(
                CommittedTreeRestoreAttempt.CommittedTreeRestoreAttemptTreeId, session);

            // Locate the tree the marker-owned recordings actually live in.
            // The session.TreeId may point at the pending tree, the active
            // (in-flight) tree, or a saved-pending-during-active-restore tree.
            RecordingTree segmentTree = FindSegmentTreeForSession(session);
            if (segmentTree == null)
            {
                reason = "session-tree-missing";
                ParsekLog.Warn("SwitchSegment",
                    $"TryDiscardActiveSwitchSegmentAttempt refused: " +
                    $"sessionId={sessionIdStr} treeId={session.TreeId ?? "<null>"} " +
                    $"reason={reason}");
                return RecordingStore.SwitchSegmentDiscardDisposition.NoActiveSession;
            }

            // Bug 2 (post-#876 playtest 2026-05-17): collect the TOPOLOGICAL
            // subtree rooted at session.ActiveSegmentRecordingId. Sweeps all
            // descendants regardless of SwitchSegmentSessionId stamp — debris
            // from a Breakup-during-segment, EVA children, dock children. The
            // marker-only filter the original implementation used left debris
            // behind and tripped a second dialog; the second-dialog flow has
            // been deleted in favor of this broader sweep.
            var ownedIds = CollectSwitchSegmentSubtreeRecordingIds(segmentTree, session);
            int ownedCount = ownedIds.Count;

            // Rec-3 observability (see DiscardPendingTree): report any supply-route
            // physical mutation that fired inside this discarded switch-segment subtree
            // and persists un-reversed. Behavior-neutral; rewind/RP-backed sessions are
            // skipped inside the reporter. Collected here while the subtree is still
            // resolvable, before the purge/removal below.
            {
                var discardedRecs = CollectTreeRecordingsForIds(segmentTree, ownedIds);
                Parsek.Logistics.RouteDiscardObservability.ReportDiscardLeakForRecordings(
                    discardedRecs,
                    Parsek.Logistics.RouteEventLog.Events,
                    ParsekScenario.IsReFlySessionActiveForQuickloadDiscard(),
                    $"SwitchSegment scoped discard sess={sessionIdStr}");
            }

            // Snapshot session-authored branch point ids = current BPs minus
            // PreSessionBranchPointIds baseline. Mirrors the ReFly approach.
            HashSet<string> sessionAuthoredBpIds = CollectSessionAuthoredBranchPointIds(
                segmentTree, session);

            int purgedEvents = 0;
            int deletedSidecars = 0;
            if (ownedIds.Count > 0)
            {
                // Preserve irreversible live-gameplay economy (contracts/milestones/science)
                // for the discarded segment subtree before the purge drops it — KSP applied
                // it live and the ledger must keep it. Runs once here, before the disposition
                // branch split below, so both the clone and prune branches are covered.
                LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard(
                    ownedIds, $"SwitchSegment scoped discard sess={sessionIdStr}");
                purgedEvents = GameStateStore.PurgeEventsForRecordings(
                    ownedIds,
                    $"SwitchSegment scoped discard sess={sessionIdStr}");

                foreach (string id in ownedIds)
                {
                    if (string.IsNullOrEmpty(id))
                        continue;
                    Recording rec;
                    if (segmentTree.Recordings != null
                        && segmentTree.Recordings.TryGetValue(id, out rec)
                        && rec != null)
                    {
                        RecordingFileJanitor.DeleteRecordingFiles(rec);
                        deletedSidecars++;
                    }
                }
            }

            int removedRecordings = RemoveRecordingIdsFromTree(segmentTree, ownedIds);
            int removedBranchPoints = RemoveSessionAuthoredBranchPointsFromTree(
                segmentTree, sessionAuthoredBpIds);

            // Repair parent recording's ChildBranchPointId if it now points at
            // a removed BP. Mirrors PruneSessionCreatedBranchPoints.
            RepairBranchPointLinksAfterSegmentDiscard(segmentTree, sessionAuthoredBpIds);

            // Repair ActiveRecordingId if it pointed at a removed segment.
            if (!string.IsNullOrEmpty(segmentTree.ActiveRecordingId)
                && ownedIds.Contains(segmentTree.ActiveRecordingId))
            {
                string priorActive = segmentTree.ActiveRecordingId;
                segmentTree.ActiveRecordingId = ResolveActiveRecordingIdAfterSegmentDiscard(
                    segmentTree, session.ParentRecordingId);
                ParsekLog.Info("SwitchSegment",
                    $"TryDiscardActiveSwitchSegmentAttempt: tree.ActiveRecordingId " +
                    $"reset from '{priorActive}' to '{segmentTree.ActiveRecordingId ?? "<null>"}' " +
                    $"sessionId={sessionIdStr}");
            }

            ParsekLog.Info("SwitchSegment",
                $"TryDiscardActiveSwitchSegmentAttempt pruned: " +
                $"sessionId={sessionIdStr} " +
                $"intentId={session.IntentId.ToString("D", CultureInfo.InvariantCulture)} " +
                $"entryReason={session.EntryReason} " +
                $"treeId={segmentTree.Id ?? "<null>"} " +
                $"ownedRecordingIds={ownedCount} " +
                $"removedRecordings={removedRecordings} " +
                $"removedBranchPoints={removedBranchPoints} " +
                $"purgedEvents={purgedEvents} " +
                $"deletedSidecars={deletedSidecars}");

            scenario.ClearSwitchSegmentSession("scoped-discard");

            // Final disposition.
            if (isCommittedRestoreClone)
            {
                // Drop the active clone wrapper if it lives in the pending
                // slot for this committed-tree restore. The original
                // committed tree remains untouched in RecordingStore.CommittedTrees.
                bool droppedPending = false;
                if (PendingTreeSlot.PendingTree != null
                    && string.Equals(PendingTreeSlot.PendingTree.Id, CommittedTreeRestoreAttempt.CommittedTreeRestoreAttemptTreeId,
                        StringComparison.Ordinal))
                {
                    PendingTreeSlot.ClearSlot();
                    droppedPending = true;
                }

                CommittedTreeRestoreAttempt.DiscardCommittedTreeRestoreAttempt(
                    "switch-segment-scoped-discard committed-restore-clone",
                    preserveIrreversibleTails: true);
                // Pending science was already scoped-removed for this segment subtree by
                // PreserveIrreversibleLiveGameplayOnDiscard(ownedIds) above. Do NOT
                // blanket-Clear() here: that would silently drop a DIFFERENT live
                // recording's still-uncommitted science (it is not in ownedIds), against
                // the scoped commit/discard contract.
                SceneEntryVesselScope.ClearRewindReplayTargetScope();

                reason = "scoped-discard-success";
                ParsekLog.Info("SwitchSegment",
                    $"TryDiscardActiveSwitchSegmentAttempt disposition=committed-restore-clone " +
                    $"sessionId={sessionIdStr} droppedPendingClone={droppedPending} " +
                    $"committedTreeId={CommittedTreeRestoreAttempt.CommittedTreeRestoreAttemptTreeId ?? "<null>"}");
                return RecordingStore.SwitchSegmentDiscardDisposition.CommittedRestoreClone;
            }

            reason = "scoped-discard-success";
            ParsekLog.Info("SwitchSegment",
                $"TryDiscardActiveSwitchSegmentAttempt disposition=pending-tree-prune " +
                $"sessionId={sessionIdStr} " +
                $"prunedTreeId={segmentTree.Id ?? "<null>"} " +
                $"prunedTreeRemainingRecordings={(segmentTree.Recordings?.Count ?? 0)}");
            return RecordingStore.SwitchSegmentDiscardDisposition.PendingTreePrune;
        }

        /// <summary>
        /// True when the session's segment lives in the clone a committed-tree restore attempt
        /// made: the attempt's tree id (non-empty) names the session's tree or its committed
        /// tree. Pure.
        /// </summary>
        internal static bool IsCommittedRestoreCloneSession(
            string restoreAttemptTreeId, SwitchSegmentSession session)
        {
            return !string.IsNullOrEmpty(restoreAttemptTreeId)
                && (string.Equals(restoreAttemptTreeId, session.TreeId,
                        StringComparison.Ordinal)
                    || string.Equals(restoreAttemptTreeId, session.CommittedTreeId,
                        StringComparison.Ordinal));
        }

        /// <summary>
        /// The recordings of <paramref name="tree"/> named by <paramref name="ids"/>, in the
        /// set's enumeration order (empty ids and absent or null entries skipped). Pure.
        /// </summary>
        internal static List<Recording> CollectTreeRecordingsForIds(RecordingTree tree, HashSet<string> ids)
        {
            var recs = new List<Recording>(ids.Count);
            if (tree.Recordings != null)
            {
                foreach (string oid in ids)
                {
                    Recording drec;
                    if (!string.IsNullOrEmpty(oid)
                        && tree.Recordings.TryGetValue(oid, out drec)
                        && drec != null)
                        recs.Add(drec);
                }
            }
            return recs;
        }

        /// <summary>
        /// After a scoped discard removed the session-authored branch points: a surviving
        /// recording whose parent link names one loses it, and one whose child link names one
        /// gets back the continuing branch that link had overwritten
        /// (<see cref="FindOverwrittenContinuingBranchPointId"/>, null when none). Each repaired
        /// recording is marked dirty.
        /// </summary>
        internal static void RepairBranchPointLinksAfterSegmentDiscard(
            RecordingTree segmentTree, HashSet<string> sessionAuthoredBpIds)
        {
            if (segmentTree.Recordings != null && sessionAuthoredBpIds.Count > 0)
            {
                foreach (var rec in segmentTree.Recordings.Values)
                {
                    if (rec == null) continue;
                    if (!string.IsNullOrEmpty(rec.ParentBranchPointId)
                        && sessionAuthoredBpIds.Contains(rec.ParentBranchPointId))
                    {
                        rec.ParentBranchPointId = null;
                        rec.MarkFilesDirty();
                    }
                    if (!string.IsNullOrEmpty(rec.ChildBranchPointId)
                        && sessionAuthoredBpIds.Contains(rec.ChildBranchPointId))
                    {
                        string removedBpId = rec.ChildBranchPointId;
                        rec.ChildBranchPointId = FindOverwrittenContinuingBranchPointId(segmentTree, rec);
                        rec.MarkFilesDirty();
                        if (rec.ChildBranchPointId != null)
                            ParsekLog.Info("SwitchSegment",
                                $"Discard: restored rec={rec.RecordingId} childBranchPointId={rec.ChildBranchPointId} " +
                                $"(the removed session branch {removedBpId} had overwritten it)");
                    }
                }
            }
        }

        /// <summary>
        /// The active recording id a tree takes when the discard removed its active segment:
        /// the session's parent recording when it survives in the tree, null otherwise. Pure.
        /// </summary>
        internal static string ResolveActiveRecordingIdAfterSegmentDiscard(
            RecordingTree segmentTree, string parentRecordingId)
        {
            return !string.IsNullOrEmpty(parentRecordingId)
                && segmentTree.Recordings != null
                && segmentTree.Recordings.ContainsKey(parentRecordingId)
                    ? parentRecordingId
                    : null;
        }

        /// <summary>
        /// The scene-exit teardown disposition of a segment: a committed-restore clone first,
        /// then Standalone when the segment subtree is the whole tree, else a background member
        /// or mixed tree. Pure.
        /// </summary>
        internal static SwitchSegmentDisposition ClassifySwitchSegmentDisposition(
            bool isCommittedClone, int treeRecordingCount, int subtreeRecordingCount)
        {
            if (isCommittedClone)
                return SwitchSegmentDisposition.CommittedRestoreClone;
            if (treeRecordingCount == subtreeRecordingCount)
                return SwitchSegmentDisposition.Standalone;
            return SwitchSegmentDisposition.BgMemberOrMixed;
        }

        /// <summary>
        /// Classifies the armed <see cref="SwitchSegmentSession"/>'s segment as a
        /// no-op (safe to auto-discard) or not, and reports the discard
        /// <see cref="SwitchSegmentDisposition"/>. The caller (ParsekFlight) MUST
        /// have flushed the live recorder into the active tree first
        /// (<see cref="ParsekFlight.FlushRecorderIntoActiveTreeForSerialization"/>)
        /// so the segment recording carries its in-flight payload.
        ///
        /// <para>Returns false (keep) — with a diagnostic <paramref name="reason"/>
        /// — when there is no session, the session tree / segment cannot be
        /// resolved, the segment is not the live active recording (so the flush
        /// did not populate it), or the pure
        /// <see cref="SwitchSegmentNoOpClassifier.IsNoOpSegment"/> predicate keeps
        /// it. Re-Fly / merge-journal guards live in the ParsekFlight wrapper.</para>
        /// </summary>
        internal static bool TryClassifyActiveSwitchSegmentNoOp(
            out string reason, out SwitchSegmentDisposition disposition)
        {
            reason = null;
            disposition = SwitchSegmentDisposition.None;

            var scenario = ParsekScenario.Instance;
            var session = object.ReferenceEquals(null, scenario)
                ? null
                : scenario.ActiveSwitchSegmentSession;
            if (session == null)
            {
                reason = "no-session";
                return false;
            }

            RecordingTree tree = FindSegmentTreeForSession(session);
            if (tree == null || tree.Recordings == null)
            {
                reason = "session-tree-missing";
                return false;
            }

            string segId = session.ActiveSegmentRecordingId;
            if (string.IsNullOrEmpty(segId)
                || !tree.Recordings.TryGetValue(segId, out Recording segment)
                || segment == null)
            {
                reason = "segment-missing";
                return false;
            }

            // The flush only populates activeTree.ActiveRecordingId. If the
            // segment is not the active recording its payload may be stale /
            // empty (e.g. a torn-down tree after a mid-segment destroy), so we
            // cannot evaluate it — keep, conservatively.
            if (!string.Equals(tree.ActiveRecordingId, segId, StringComparison.Ordinal))
            {
                reason = "segment-not-active-recording";
                return false;
            }

            // Descendants: the subtree set includes the segment itself, so a count
            // > 1 means dock / undock / EVA / decouple / breakup children exist.
            // Count 0 = segment absent from the walk (cannot evaluate) -> keep.
            var subtreeIds = CollectSwitchSegmentSubtreeRecordingIds(tree, session);
            if (subtreeIds.Count == 0)
            {
                reason = "segment-absent-from-subtree";
                return false;
            }
            bool hasDescendants = subtreeIds.Count > 1;

            // Disposition for the scene-exit teardown choice.
            bool isCommittedClone = IsCommittedRestoreCloneSession(
                CommittedTreeRestoreAttempt.CommittedTreeRestoreAttemptTreeId, session);
            disposition = ClassifySwitchSegmentDisposition(
                isCommittedClone, tree.Recordings.Count, subtreeIds.Count);

            bool noOp = SwitchSegmentNoOpClassifier.IsNoOpSegment(
                segment, hasDescendants, out string keepReason);
            reason = noOp ? "no-op" : keepReason;

            // A committed-restore clone is torn down WHOLE on a no-op, so a segment
            // that did nothing must not take other members' new work with it (the
            // player flew a background member and came back): every recording
            // outside the segment's subtree must also be unchanged since the switch.
            if (noOp && disposition == SwitchSegmentDisposition.CommittedRestoreClone
                && !CommittedCloneMembersOutsideSegmentUnchanged(
                    tree, subtreeIds, session.SwitchUT, out string memberReason))
            {
                noOp = false;
                reason = memberReason;
            }

            ParsekLog.Verbose("SwitchSegment",
                $"TryClassifyActiveSwitchSegmentNoOp: sessionId={session.SessionId:D} " +
                $"segId={segId} disposition={disposition} subtreeCount={subtreeIds.Count} " +
                $"hasDescendants={hasDescendants} noOp={noOp} reason={reason ?? "<none>"}");

            return noOp;
        }

        /// <summary>
        /// Pure: true when every recording of <paramref name="tree"/> outside
        /// <paramref name="segmentSubtreeIds"/> changed nothing at or after
        /// <paramref name="sinceUT"/> (<see cref="SwitchSegmentNoOpClassifier.IsNoOpResumeTail"/>,
        /// the same tail rule the no-session committed resume applies tree-wide;
        /// committed history before the switch passes). The first member that did
        /// something is named in <paramref name="keepReason"/> as
        /// <c>member:&lt;id&gt;:&lt;reason&gt;</c>.
        /// </summary>
        internal static bool CommittedCloneMembersOutsideSegmentUnchanged(
            RecordingTree tree, HashSet<string> segmentSubtreeIds, double sinceUT,
            out string keepReason)
        {
            keepReason = null;
            if (tree?.Recordings == null)
                return true;
            foreach (var kvp in tree.Recordings)
            {
                Recording rec = kvp.Value;
                if (rec == null) continue;
                if (segmentSubtreeIds != null && segmentSubtreeIds.Contains(kvp.Key))
                    continue;
                if (!SwitchSegmentNoOpClassifier.IsNoOpResumeTail(rec, sinceUT, out string memberReason))
                {
                    keepReason = "member:" + (kvp.Key ?? "<null>") + ":" + (memberReason ?? "<none>");
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Locates the in-memory tree that owns the active switch-segment
        /// session. Prefers the pending tree (where scene-exit Stash usually
        /// puts the active tree) and falls back to the live active tree, the
        /// saved-pending-during-active-restore tree, then committed trees.
        /// Returns null when the session's TreeId resolves to no in-memory
        /// tree (treated as a degenerate state by the caller).
        /// </summary>
        private static RecordingTree FindSegmentTreeForSession(SwitchSegmentSession session)
        {
            if (session == null || string.IsNullOrEmpty(session.TreeId))
                return null;

            if (PendingTreeSlot.PendingTree != null
                && string.Equals(PendingTreeSlot.PendingTree.Id, session.TreeId, StringComparison.Ordinal))
                return PendingTreeSlot.PendingTree;

            if (PendingTreeSlot.SavedPendingTreeDuringActiveRestore != null
                && string.Equals(PendingTreeSlot.SavedPendingTreeDuringActiveRestore.Id, session.TreeId,
                    StringComparison.Ordinal))
                return PendingTreeSlot.SavedPendingTreeDuringActiveRestore;

            var activeTree = ParsekFlight.Instance?.ActiveTreeForSerialization;
            if (activeTree != null
                && string.Equals(activeTree.Id, session.TreeId, StringComparison.Ordinal))
                return activeTree;

            if (RecordingStore.CommittedTrees != null)
            {
                for (int i = 0; i < RecordingStore.CommittedTrees.Count; i++)
                {
                    var t = RecordingStore.CommittedTrees[i];
                    if (t != null
                        && string.Equals(t.Id, session.TreeId, StringComparison.Ordinal))
                        return t;
                }
            }

            return null;
        }

        /// <summary>
        /// Cycle / pathological-fanout safety cap on the descendant-walk loop
        /// in <see cref="CollectSwitchSegmentSubtreeRecordingIds"/>. The
        /// queue-driven walk visits each branch-point edge once, with this
        /// cap guarding against a corrupted branch-point graph that forms a
        /// cycle or fans out wider than expected. A healthy production tree
        /// terminates in O(tree depth) iterations; reaching the cap means
        /// something is wrong, so the walk breaks and logs a Warn with the
        /// session id and the partial collection size so the failure leaves a
        /// diagnostic trail. Phase F review fix (1c): replaces a bare 1024
        /// literal that broke silently.
        /// </summary>
        internal const int SwitchSegmentRecordingTreeWalkMaxIterations = 1024;

        /// <summary>
        /// Bug 2 (post-#876 playtest 2026-05-17): collect the topological
        /// subtree rooted at <see cref="SwitchSegmentSession.ActiveSegmentRecordingId"/>
        /// in <paramref name="tree"/>. The closure walks every recording
        /// reachable via <see cref="Recording.ChildBranchPointId"/> →
        /// <see cref="BranchPoint.ChildRecordingIds"/>, regardless of the
        /// child's <see cref="Recording.SwitchSegmentSessionId"/> stamp. This
        /// is the load-bearing semantic difference from the original
        /// marker-only filter: debris from a Breakup-during-segment, EVA
        /// children, dock children — all in scope when topologically
        /// descended from the segment recording.
        ///
        /// <para>Pre-segment recordings in OTHER trees stay handled by the
        /// regular per-tree dialog (this function only walks the segment's
        /// own tree).</para>
        ///
        /// <para>Replaces the deleted second-dialog flow: the scoped Discard
        /// now sweeps the full subtree on its own and the secondary
        /// whole-pending-tree dialog is no longer needed.</para>
        /// </summary>
        internal static HashSet<string> CollectSwitchSegmentSubtreeRecordingIds(
            RecordingTree tree, SwitchSegmentSession session)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (tree?.Recordings == null || session == null)
                return ids;

            if (string.IsNullOrEmpty(session.ActiveSegmentRecordingId)
                || !tree.Recordings.ContainsKey(session.ActiveSegmentRecordingId))
            {
                return ids;
            }

            string sessionIdStr = session.SessionId.ToString("D", CultureInfo.InvariantCulture);

            // BFS over branch-point children rooted at the segment recording.
            var queue = new Queue<string>();
            queue.Enqueue(session.ActiveSegmentRecordingId);
            int safety = 0;
            while (queue.Count > 0)
            {
                if (++safety > SwitchSegmentRecordingTreeWalkMaxIterations)
                {
                    // Cycle or pathological fanout — log Warn so the partial
                    // collection that the caller is about to act on is at
                    // least traceable in KSP.log. Phase F review fix (1c).
                    ParsekLog.Warn("SwitchSegment",
                        $"CollectSubtree: iteration cap reached, breaking walk: " +
                        $"sessionId={sessionIdStr} " +
                        $"treeId={tree.Id ?? "<null>"} cap={SwitchSegmentRecordingTreeWalkMaxIterations} " +
                        $"collectedSoFar={ids.Count}");
                    break;
                }

                string id = queue.Dequeue();
                if (string.IsNullOrEmpty(id) || !ids.Add(id))
                    continue;
                if (!tree.Recordings.TryGetValue(id, out Recording rec) || rec == null)
                    continue;
                // A branch point the (still recording) parent does NOT reference
                // through ChildBranchPointId is found by parent id: a ground part
                // placed by this recording's kerbal (GroundPartPlaced), and an
                // earlier breakup or decouple of a breakup-continuous recording
                // whose ChildBranchPointId a later split overwrote.
                EnqueueParentedBranchChildren(tree, id, ids, queue);
                if (string.IsNullOrEmpty(rec.ChildBranchPointId))
                    continue;
                BranchPoint bp = FindSwitchSegmentBranchPointById(tree, rec.ChildBranchPointId);
                if (bp?.ChildRecordingIds == null)
                    continue;
                for (int i = 0; i < bp.ChildRecordingIds.Count; i++)
                {
                    string childId = bp.ChildRecordingIds[i];
                    if (string.IsNullOrEmpty(childId)) continue;
                    if (ids.Contains(childId)) continue;
                    queue.Enqueue(childId);
                }
            }

            return ids;
        }

        /// <summary>
        /// The branch point a later split overwrote on <paramref name="rec"/>: the latest
        /// surviving Breakup / JointBreak / Launch branch that still lists the recording as a
        /// parent (a breakup-continuous recording keeps flying past those, so a switch
        /// continuation attached under it replaced the link). Null when there is none.
        /// </summary>
        internal static string FindOverwrittenContinuingBranchPointId(RecordingTree tree, Recording rec)
        {
            if (tree?.BranchPoints == null || rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return null;
            BranchPoint best = null;
            for (int b = 0; b < tree.BranchPoints.Count; b++)
            {
                BranchPoint bp = tree.BranchPoints[b];
                if (bp == null || bp.ParentRecordingIds == null
                    || !RecordingTree.BranchTypeCanLeaveParentRecording(bp.Type)
                    || !bp.ParentRecordingIds.Contains(rec.RecordingId))
                    continue;
                if (best == null || bp.UT > best.UT)
                    best = bp;
            }
            return best?.Id;
        }

        internal static void EnqueueParentedBranchChildren(
            RecordingTree tree, string parentId, HashSet<string> ids, Queue<string> queue)
        {
            if (tree.BranchPoints == null) return;
            for (int b = 0; b < tree.BranchPoints.Count; b++)
            {
                BranchPoint bp = tree.BranchPoints[b];
                if (bp == null) continue;
                if (bp.ParentRecordingIds == null || !bp.ParentRecordingIds.Contains(parentId)) continue;
                if (bp.ChildRecordingIds == null) continue;
                for (int i = 0; i < bp.ChildRecordingIds.Count; i++)
                {
                    string childId = bp.ChildRecordingIds[i];
                    if (string.IsNullOrEmpty(childId) || ids.Contains(childId)) continue;
                    queue.Enqueue(childId);
                }
            }
        }

        internal static BranchPoint FindSwitchSegmentBranchPointById(
            RecordingTree tree, string bpId)
        {
            if (tree?.BranchPoints == null || string.IsNullOrEmpty(bpId))
                return null;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                var bp = tree.BranchPoints[i];
                if (bp != null && string.Equals(bp.Id, bpId, StringComparison.Ordinal))
                    return bp;
            }
            return null;
        }

        /// <summary>
        /// Collects branch-point IDs authored by the active session: every
        /// current BP not in <see cref="SwitchSegmentSession.PreSessionBranchPointIds"/>.
        /// Mirrors <c>MergeDialog.PruneSessionCreatedBranchPoints</c>: a null
        /// baseline is treated as "unknown" and skipped (returns empty), a
        /// present-but-empty baseline means every current BP is session-authored.
        /// </summary>
        internal static HashSet<string> CollectSessionAuthoredBranchPointIds(
            RecordingTree tree, SwitchSegmentSession session)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (tree?.BranchPoints == null || session == null
                || session.PreSessionBranchPointIds == null)
                return ids;

            var preSessionIds = new HashSet<string>(
                session.PreSessionBranchPointIds, StringComparer.Ordinal);
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                var bp = tree.BranchPoints[i];
                if (bp == null || string.IsNullOrEmpty(bp.Id)) continue;
                if (preSessionIds.Contains(bp.Id)) continue;
                ids.Add(bp.Id);
            }
            return ids;
        }

        internal static int RemoveRecordingIdsFromTree(
            RecordingTree tree, HashSet<string> ids)
        {
            if (tree?.Recordings == null || ids == null || ids.Count == 0)
                return 0;
            int removed = 0;
            foreach (string id in ids)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (tree.Recordings.Remove(id))
                    removed++;
            }
            // Scrub remaining branch-point parent/child id refs that point at
            // a removed recording.
            if (tree.BranchPoints != null)
            {
                for (int i = 0; i < tree.BranchPoints.Count; i++)
                {
                    var bp = tree.BranchPoints[i];
                    if (bp == null) continue;
                    ScrubRecordingIdsFromList(bp.ParentRecordingIds, ids);
                    ScrubRecordingIdsFromList(bp.ChildRecordingIds, ids);
                }
            }
            return removed;
        }

        internal static void ScrubRecordingIdsFromList(
            List<string> list, HashSet<string> idsToRemove)
        {
            if (list == null || idsToRemove == null || idsToRemove.Count == 0)
                return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (idsToRemove.Contains(list[i]))
                    list.RemoveAt(i);
            }
        }

        internal static int RemoveSessionAuthoredBranchPointsFromTree(
            RecordingTree tree, HashSet<string> sessionAuthoredBpIds)
        {
            if (tree?.BranchPoints == null
                || sessionAuthoredBpIds == null || sessionAuthoredBpIds.Count == 0)
                return 0;
            int removed = 0;
            for (int i = tree.BranchPoints.Count - 1; i >= 0; i--)
            {
                var bp = tree.BranchPoints[i];
                if (bp == null || string.IsNullOrEmpty(bp.Id)) continue;
                if (!sessionAuthoredBpIds.Contains(bp.Id)) continue;
                tree.BranchPoints.RemoveAt(i);
                removed++;
            }
            return removed;
        }
    }
}
