using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Tree discard (RecordingStore services plan, service K): the pending-tree discard
    /// (<see cref="DiscardPendingTree"/>: the pending-only recordings' game-state events,
    /// rewind points, supersede rows, tombstones and sidecars go, committed-overlap ids are
    /// preserved, the irreversible ledger tail is re-homed on a live discard), the single
    /// committed-recording removal with its tree prune (<see cref="RemoveRecordingAt"/>, which
    /// changes the committed list only through <see cref="RecordingStore.RemoveCommittedAt"/>),
    /// the persistent.sfs refresh after a discard, and the process-lifetime set of tree ids a
    /// discard deleted sidecars for. <see cref="TreeDiscardPurge"/> stays the purge core.
    /// </summary>
    internal static class TreeDiscardService
    {
        // Ids of the trees DiscardPendingTree deleted sidecars for in this process: the proof
        // ParsekScenario.DecideDiscardedActiveTreeRestore needs that a saved active tree whose
        // every trajectory is missing was thrown away on purpose, not damaged. Process
        // lifetime: tree ids are Guids and the files it vouches for are gone for good.
        private static readonly HashSet<string> treesDiscardedThisSession =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Save-scoped reset (<see cref="RecordingStore.ResetSaveScopedStateForLoad"/>): forgets
        /// the discarded tree ids, as the store did before the set moved here.
        /// </summary>
        internal static void ResetForLoad()
        {
            treesDiscardedThisSession.Clear();
        }

        /// <summary>Test reset, run by <see cref="RecordingStore.ResetForTesting"/>.</summary>
        internal static void ResetForTesting()
        {
            treesDiscardedThisSession.Clear();
        }

        /// <summary>
        /// Records that a discard deleted the sidecars of <paramref name="treeId"/>'s
        /// recordings in this process: <see cref="DiscardPendingTree"/>, the in-flight
        /// discard core's reap (<see cref="DiscardSidecarReap.ReapDiscardedTreeSidecars"/>)
        /// or the <c>DiscardTree</c> test-command verb's own reap. A later load of a save that
        /// still holds the tree as its active tree reads this to tell a deliberate discard
        /// from a damaged or incomplete save folder.
        /// </summary>
        internal static void NoteTreeDiscardedThisSession(string treeId, string treeName, int removedSidecarSets)
        {
            if (string.IsNullOrEmpty(treeId))
                return;
            bool added = treesDiscardedThisSession.Add(treeId);
            ParsekLog.Verbose("RecordingStore",
                $"Noted tree '{treeName ?? "<unnamed>"}' id={treeId} as discarded this session " +
                $"(removedSidecarSets={removedSidecarSets.ToString(CultureInfo.InvariantCulture)}, " +
                $"new={(added ? "true" : "false")})");
        }

        /// <summary>
        /// True when a discard deleted sidecars of this tree id in this
        /// process (see <see cref="NoteTreeDiscardedThisSession"/>).
        /// </summary>
        internal static bool WasTreeDiscardedThisSession(string treeId)
        {
            return !string.IsNullOrEmpty(treeId) && treesDiscardedThisSession.Contains(treeId);
        }

        /// <summary>
        /// Discards the pending tree and cleans up its recording files.
        /// #431: also purges every <see cref="GameStateEvent"/> tagged with one of the
        /// tree's pending-only recording ids — both from the live store and from any
        /// milestone the flush-on-save path may have already moved them into. Contract
        /// snapshots orphaned by the purge (i.e. whose accept event was among the
        /// purged set) are removed too. Recording ids still present in committed
        /// history are preserved.
        /// </summary>
        public static void DiscardPendingTree(bool preserveIrreversibleLiveGameplay = true)
        {
            if (PendingTreeSlot.PendingTree == null)
            {
                ParsekLog.Verbose("RecordingStore", "DiscardPendingTree called with no pending tree");
                return;
            }

            bool pendingMatchesCommittedRestoreAttempt =
                CommittedTreeRestoreAttempt.PendingTreeMatchesCommittedTreeRestoreAttempt();
            // A live discard re-homes the attempt's irreversible tails before they are
            // purged; the abandon path (preserve=false) has its economy rolled back.
            int purgedSameIdAttemptEventTails =
                CommittedTreeRestoreAttempt.PurgeCommittedTreeRestoreAttemptEventTailsForPendingDiscard(
                    preserveIrreversibleLiveGameplay);

            // #431: purge tagged events for pending-only recording IDs first. A pending
            // tree can intentionally reference a committed recording ID; those events,
            // milestone entries, contract snapshots, and sidecars belong to committed
            // history and must survive discard.
            var idsToPurge = new HashSet<string>();
            int skippedCommittedEventPurges = 0;
            int skippedByDurableHint = 0;
            foreach (var rec in PendingTreeSlot.PendingTree.Recordings.Values)
            {
                string recordingId = rec?.RecordingId;
                if (string.IsNullOrEmpty(recordingId))
                    continue;

                // Committed-overlap guard. On a COLD load the in-memory store is not
                // populated yet (DiscardStalePendingState runs before LoadRecordingTrees),
                // so the save node's declared ids are consulted as well - see
                // SetDurableCommittedRecordingIdHint.
                bool committedInMemory = RecordingStore.IsCommittedRecordingId(recordingId);
                bool durableHint = RecordingStore.IsDurableCommittedRecordingIdHint(recordingId);
                if (ShouldPreserveCommittedOverlapOnPendingDiscard(committedInMemory, durableHint))
                {
                    skippedCommittedEventPurges++;
                    if (!committedInMemory)
                        skippedByDurableHint++;
                    continue;
                }

                idsToPurge.Add(recordingId);
            }

            // Phase 11 of Rewind-to-Staging (design §3.5 invariant 7 / §6.10):
            // tree discard is the ONLY purge path for RewindPoints, supersede
            // relations, and ledger tombstones whose endpoints tie back to
            // the discarded tree. Use the actual pending tree instance and the
            // pending-only recording id set so same-id committed trees and
            // committed-overlap recordings are preserved.
            // Preserve irreversible live-gameplay economy (completed/failed/cancelled
            // contracts, achieved milestones, collected science) BEFORE the purge/clear
            // below drops it. KSP applied it irreversibly during the live flight, so the
            // ledger must keep it or it diverges from KSP (re-listing a completed contract
            // as active in the in-progress tab + a re-completable duplicate reward + a
            // silent fund drop). Scoped to pending-only ids (committed-overlap excluded).
            //
            // ONLY on a genuine live discard. The abandon path
            // (DiscardPendingTreeAndAbandonDeferredFlightResults: quickload-backwards,
            // revert, and stale-pending-from-a-different-save on load) passes
            // preserveIrreversibleLiveGameplay=false: there KSP's economy is rolled back
            // (or belongs to another save), so preserving would diverge the ledger the
            // OTHER way (credit economy KSP no longer reflects / cross-save contamination).
            if (preserveIrreversibleLiveGameplay && idsToPurge.Count > 0)
                LedgerOrchestrator.PreserveIrreversibleLiveGameplayOnDiscard(
                    idsToPurge, $"DiscardPendingTree '{PendingTreeSlot.PendingTree.TreeName}'");

            // Rec-3 observability (plan Phase 4 / report risk #6): a supply route that
            // physically fired inside this discarded window persists un-reversed in the
            // surviving timeline (funds AND cargo both kept — economically consistent,
            // but the discard did not undo it). Log the residual so it is greppable.
            // Behavior-neutral: nothing is reversed, retired, or gated. Only on the
            // genuine live-discard path — the abandon path (preserve=false) has already
            // had its world reverted by the quickload/revert. Rewind/RP-backed sessions
            // are skipped inside the reporter (the quicksave is their rollback).
            if (preserveIrreversibleLiveGameplay)
            {
                // Scope the reported window to the recordings actually purged (idsToPurge),
                // NOT the whole pending tree — committed-overlap recordings are preserved,
                // not discarded, so their span must not widen the window (mirrors the
                // idsToPurge scoping of PreserveIrreversibleLiveGameplayOnDiscard above and
                // the owned-id resolution in TryDiscardActiveSwitchSegmentAttempt).
                var discardedRecs = new List<Recording>(idsToPurge.Count);
                if (PendingTreeSlot.PendingTree.Recordings != null)
                {
                    foreach (string purgeId in idsToPurge)
                    {
                        Recording drec;
                        if (!string.IsNullOrEmpty(purgeId)
                            && PendingTreeSlot.PendingTree.Recordings.TryGetValue(purgeId, out drec)
                            && drec != null)
                            discardedRecs.Add(drec);
                    }
                }
                Parsek.Logistics.RouteDiscardObservability.ReportDiscardLeakForRecordings(
                    discardedRecs,
                    Parsek.Logistics.RouteEventLog.Events,
                    ParsekScenario.IsReFlySessionActiveForQuickloadDiscard(),
                    $"DiscardPendingTree '{PendingTreeSlot.PendingTree.TreeName}'");
            }

            TreeDiscardPurge.PurgeTree(PendingTreeSlot.PendingTree, idsToPurge);
            if (idsToPurge.Count > 0)
                GameStateStore.PurgeEventsForRecordings(idsToPurge, $"DiscardPendingTree '{PendingTreeSlot.PendingTree.TreeName}'");
            if (skippedCommittedEventPurges > 0)
            {
                ParsekLog.Warn("RecordingStore",
                    $"DiscardPendingTree: skipped destructive event/milestone purge for " +
                    $"{skippedCommittedEventPurges} committed-overlap recording ID(s) " +
                    $"(viaDurableSaveHint={skippedByDurableHint})" +
                    (pendingMatchesCommittedRestoreAttempt
                        ? $"; purged same-id attempt tails={purgedSameIdAttemptEventTails}"
                        : ""));
            }

            int skippedCommittedDeletes = 0;
            int skippedDeletesByDurableHint = 0;
            int removedSidecarSets = 0;
            foreach (var rec in PendingTreeSlot.PendingTree.Recordings.Values)
            {
                string deleteCandidateId = rec?.RecordingId;
                bool deleteCommittedInMemory = RecordingStore.IsCommittedRecordingId(deleteCandidateId);
                bool deleteDurableHint = RecordingStore.IsDurableCommittedRecordingIdHint(deleteCandidateId);
                if (ShouldPreserveCommittedOverlapOnPendingDiscard(
                        deleteCommittedInMemory, deleteDurableHint))
                {
                    skippedCommittedDeletes++;
                    if (!deleteCommittedInMemory)
                        skippedDeletesByDurableHint++;
                    continue;
                }
                // Counted only when nothing was left behind: a locked file that survives
                // is not proof of a discard.
                if (RecordingFileJanitor.DeleteRecordingFiles(rec))
                    removedSidecarSets++;
            }
            if (removedSidecarSets > 0)
                NoteTreeDiscardedThisSession(PendingTreeSlot.PendingTree.Id, PendingTreeSlot.PendingTree.TreeName, removedSidecarSets);
            if (skippedCommittedDeletes > 0)
            {
                ParsekLog.Warn("RecordingStore",
                    $"DiscardPendingTree: skipped deleting {skippedCommittedDeletes} recording sidecar set(s) " +
                    "because the recording ID still exists in committed history " +
                    $"(viaDurableSaveHint={skippedDeletesByDurableHint})");
            }
            if (skippedDeletesByDurableHint > 0)
            {
                ParsekLog.Warn("RecordingStore",
                    "committed-overlap sidecars preserved by durable save hint: " +
                    $"{skippedDeletesByDurableHint} recording(s) in discarded pending tree " +
                    $"'{PendingTreeSlot.PendingTree.TreeName ?? "<unnamed>"}' are declared committed by the save " +
                    "file but the in-memory committed store is not loaded yet; their .prec/.craft " +
                    "sidecars were NOT deleted (copy-on-write committed-tree restore overlap)");
            }
            if (preserveIrreversibleLiveGameplay)
            {
                // Genuine live discard: the re-home above already converted this tree's
                // irreversible science into direct ledger actions and scoped-removed those
                // subjects. Drop any remaining pending subjects tagged to the discarded ids,
                // but PRESERVE a DIFFERENT live recording's still-uncommitted science (and
                // untagged KSC captures) — matching the scoped commit path
                // (LedgerOrchestrator.NotifyLedgerTreeCommitted). A blanket Clear() here
                // silently dropped another recording's uncommitted science. This explicit
                // call is also the backstop for the re-home's nothing-to-re-home early-return
                // (zero irreversible events + zero pending science for these ids), which
                // returns before its own internal scoped removal runs.
                LedgerOrchestrator.RemovePendingScienceSubjectsForRecordings(
                    idsToPurge, $"DiscardPendingTree '{PendingTreeSlot.PendingTree.TreeName}'");
            }
            else
            {
                // Abandon path (quickload-backwards / revert / cross-save stale pending):
                // KSP's economy is rolled back or belongs to another save, so ALL pending
                // science is from the discarded future and must NOT survive. Wipe it
                // wholesale (the quickload handler in ParsekScenario also clears it at the
                // scenario level for the same reason).
                GameStateRecorder.PendingScienceSubjects.Clear();
            }
            RecordingStore.Log($"[Parsek] Discarded pending tree '{PendingTreeSlot.PendingTree.TreeName}' (state={PendingTreeSlot.PendingTreeStateValue})");
            PendingTreeSlot.ClearSlot();
            if (pendingMatchesCommittedRestoreAttempt)
                CommittedTreeRestoreAttempt.ClearCommittedTreeRestoreAttempt("DiscardPendingTree abandoned restored active copy");
            SceneEntryVesselScope.ClearRewindReplayTargetScope();
        }

        /// <summary>
        /// Removes a single committed recording by index, deleting its external files.
        /// If the recording is part of a chain, all remaining chain siblings are degraded to standalone.
        /// Raises <see cref="RecordingStore.CommittedRecordingRemoving"/> before and
        /// <see cref="RecordingStore.CommittedRecordingRemoved"/> after the removal; in FLIGHT those carry the
        /// ghost teardown and the index reindex. Crew unreservation stays with the caller.
        /// </summary>
        internal static void RemoveRecordingAt(int index)
        {
            if (index < 0 || index >= RecordingStore.CommittedRecordings.Count)
            {
                ParsekLog.Warn("RecordingStore", $"RemoveRecordingAt called with invalid index={index} (count={RecordingStore.CommittedRecordings.Count})");
                return;
            }

            var rec = RecordingStore.CommittedRecordings[index];

            // If part of a chain, degrade remaining chain siblings to standalone
            if (!string.IsNullOrEmpty(rec.ChainId))
            {
                string chainId = rec.ChainId;
                for (int i = 0; i < RecordingStore.CommittedRecordings.Count; i++)
                {
                    if (i == index) continue;
                    var other = RecordingStore.CommittedRecordings[i];
                    if (other.ChainId == chainId)
                    {
                        RecordingStore.Log($"[Parsek]   Degrading recording '{other.VesselName}' " +
                            $"(id={other.RecordingId}, idx={other.ChainIndex}, branch={other.ChainBranch}) to standalone");
                        other.ChainId = null;
                        other.ChainIndex = -1;
                        other.ChainBranch = 0;
                    }
                }
            }

            // Prune the row from the committed tree that owns it BEFORE the files go:
            // SaveTreeRecordings walks tree.Recordings and re-materializes every member's
            // sidecars, so a row left in its tree comes back on the next OnSave with its
            // files rewritten and is reloaded into the committed list as if never deleted
            // (measured on S0.11's first flight: 8 committed recordings saved, 9 in the
            // tree node, the deleted id's five sidecars re-created at FlushAndQuit). The
            // helper also scrubs the branch-point endpoint refs and any stale root /
            // active id.
            if (!string.IsNullOrEmpty(rec.RecordingId))
            {
                int prunedFromTrees = PruneTaggedRecordingsFromCommittedTrees(
                    new HashSet<string>(StringComparer.Ordinal) { rec.RecordingId }, null);
                if (prunedFromTrees > 0)
                    RecordingStore.Log($"[Parsek]   Pruned recording id={rec.RecordingId} from {prunedFromTrees} committed tree membership(s)");
            }

            RecordingFileJanitor.DeleteRecordingFiles(rec);
            RecordingStore.RemoveCommittedAt(index, null);
            RecordingStore.Log($"[Parsek] Removed recording '{rec.VesselName}' (id={rec.RecordingId}) at index {index}");
        }

        /// <summary>
        /// Strips the given recording ids from every committed tree's
        /// <c>Recordings</c> dictionary, scrubs the same ids out of every
        /// surviving branch point's <c>ParentRecordingIds</c> /
        /// <c>ChildRecordingIds</c> lists, drops branch points whose entire
        /// parent OR child endpoint set went empty (a BP with zero parents OR
        /// zero children can no longer connect anything), clears surviving
        /// recordings' <c>ParentBranchPointId</c> / <c>ChildBranchPointId</c>
        /// values that pointed at the dropped BPs, resets stale
        /// <c>ActiveRecordingId</c> AND <c>RootRecordingId</c> to
        /// <paramref name="fallbackActiveRecordingId"/> when present (or
        /// nulled), and rebuilds the affected tree's background map. Returns
        /// the total number of dictionary entries removed across all trees.
        /// Without this pass, <c>SaveTreeRecordings</c> serialises branch
        /// points (independently from Recordings) so a Recordings-only sweep
        /// still leaves dangling topology edges to deleted Re-Fly fragments
        /// on disk.
        /// </summary>
        private static int PruneTaggedRecordingsFromCommittedTrees(
            HashSet<string> ids, string fallbackActiveRecordingId)
        {
            if (ids == null || ids.Count == 0) return 0;
            int prunedTotal = 0;
            int scrubbedRefsTotal = 0;
            int clearedRecordingBpRefsTotal = 0;
            int droppedBpsTotal = 0;
            for (int t = 0; t < RecordingStore.CommittedTrees.Count; t++)
            {
                var tree = RecordingStore.CommittedTrees[t];
                if (tree?.Recordings == null) continue;

                int prunedHere = 0;
                foreach (var id in ids)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    if (tree.Recordings.Remove(id))
                        prunedHere++;
                }
                if (prunedHere == 0) continue;

                bool useFallback =
                    !string.IsNullOrEmpty(fallbackActiveRecordingId)
                    && tree.Recordings.ContainsKey(fallbackActiveRecordingId);
                if (!string.IsNullOrEmpty(tree.ActiveRecordingId)
                    && !tree.Recordings.ContainsKey(tree.ActiveRecordingId))
                {
                    string oldActive = tree.ActiveRecordingId;
                    tree.ActiveRecordingId = useFallback
                        ? fallbackActiveRecordingId
                        : null;
                    ParsekLog.Info("RecordingStore",
                        $"PruneTaggedRecordingsFromCommittedTrees: reset stale " +
                        $"tree.ActiveRecordingId from '{oldActive}' to " +
                        $"'{tree.ActiveRecordingId ?? "<null>"}' on tree={tree.Id ?? "<none>"}");
                }
                if (!string.IsNullOrEmpty(tree.RootRecordingId)
                    && !tree.Recordings.ContainsKey(tree.RootRecordingId))
                {
                    string oldRoot = tree.RootRecordingId;
                    tree.RootRecordingId = useFallback
                        ? fallbackActiveRecordingId
                        : null;
                    ParsekLog.Warn("RecordingStore",
                        $"PruneTaggedRecordingsFromCommittedTrees: reset stale " +
                        $"tree.RootRecordingId from '{oldRoot}' to " +
                        $"'{tree.RootRecordingId ?? "<null>"}' on tree={tree.Id ?? "<none>"} " +
                        "(rollback removed the prior root — verify the resulting tree is consistent)");
                }

                int scrubbedRefs = ScrubBranchPointEndpointsAndDropEmpty(tree, ids,
                    out HashSet<string> droppedBranchPointIds);
                int droppedBpsHere = droppedBranchPointIds?.Count ?? 0;
                int clearedRecordingBpRefs = 0;
                if (droppedBpsHere > 0)
                {
                    foreach (var rec in tree.Recordings.Values)
                    {
                        if (rec == null) continue;
                        if (!string.IsNullOrEmpty(rec.ParentBranchPointId)
                            && droppedBranchPointIds.Contains(rec.ParentBranchPointId))
                        {
                            rec.ParentBranchPointId = null;
                            clearedRecordingBpRefs++;
                        }
                        if (!string.IsNullOrEmpty(rec.ChildBranchPointId)
                            && droppedBranchPointIds.Contains(rec.ChildBranchPointId))
                        {
                            rec.ChildBranchPointId = null;
                            clearedRecordingBpRefs++;
                        }
                    }
                    ParsekLog.Verbose("RecordingStore",
                        $"PruneTaggedRecordingsFromCommittedTrees: dropped " +
                        $"{droppedBpsHere} branch point(s) with empty endpoint sets " +
                        $"on tree={tree.Id ?? "<none>"} " +
                        $"ids=[{string.Join(",", droppedBranchPointIds)}]");
                }

                tree.RebuildBackgroundMap();
                prunedTotal += prunedHere;
                scrubbedRefsTotal += scrubbedRefs;
                clearedRecordingBpRefsTotal += clearedRecordingBpRefs;
                droppedBpsTotal += droppedBpsHere;
                ParsekLog.Verbose("RecordingStore",
                    $"PruneTaggedRecordingsFromCommittedTrees: tree={tree.Id ?? "<none>"} " +
                    $"prunedRecordings={prunedHere} scrubbedBranchPointRefs={scrubbedRefs} " +
                    $"droppedBranchPoints={droppedBpsHere} " +
                    $"clearedRecordingBpRefs={clearedRecordingBpRefs}");
            }
            if (prunedTotal > 0 || scrubbedRefsTotal > 0
                || clearedRecordingBpRefsTotal > 0 || droppedBpsTotal > 0)
            {
                ParsekLog.Verbose("RecordingStore",
                    $"PruneTaggedRecordingsFromCommittedTrees totals: " +
                    $"prunedRecordings={prunedTotal} " +
                    $"scrubbedBranchPointRefs={scrubbedRefsTotal} " +
                    $"droppedBranchPoints={droppedBpsTotal} " +
                    $"clearedRecordingBpRefs={clearedRecordingBpRefsTotal}");
            }
            return prunedTotal;
        }

        /// <summary>
        /// Walks <paramref name="tree"/>'s <c>BranchPoints</c> and removes any
        /// occurrences of <paramref name="ids"/> from each branch point's
        /// <c>ParentRecordingIds</c> / <c>ChildRecordingIds</c> lists. A BP is
        /// dropped from the tree when EITHER its parent or its child endpoint
        /// set went empty, since a BP with zero endpoints on either side can
        /// no longer connect anything; the dropped ids are surfaced via
        /// <paramref name="droppedBranchPointIds"/> so the caller can clear
        /// surviving recordings' BP back-references. Returns the number of
        /// id occurrences scrubbed out of endpoint lists.
        /// </summary>
        private static int ScrubBranchPointEndpointsAndDropEmpty(
            RecordingTree tree,
            HashSet<string> ids,
            out HashSet<string> droppedBranchPointIds)
        {
            droppedBranchPointIds = null;
            if (tree?.BranchPoints == null || ids == null || ids.Count == 0)
                return 0;

            int scrubbed = 0;
            for (int i = tree.BranchPoints.Count - 1; i >= 0; i--)
            {
                var bp = tree.BranchPoints[i];
                if (bp == null) continue;
                if (bp.ParentRecordingIds != null)
                {
                    for (int p = bp.ParentRecordingIds.Count - 1; p >= 0; p--)
                    {
                        if (ids.Contains(bp.ParentRecordingIds[p]))
                        {
                            bp.ParentRecordingIds.RemoveAt(p);
                            scrubbed++;
                        }
                    }
                }
                if (bp.ChildRecordingIds != null)
                {
                    for (int c = bp.ChildRecordingIds.Count - 1; c >= 0; c--)
                    {
                        if (ids.Contains(bp.ChildRecordingIds[c]))
                        {
                            bp.ChildRecordingIds.RemoveAt(c);
                            scrubbed++;
                        }
                    }
                }

                bool emptyParents = bp.ParentRecordingIds == null
                    || bp.ParentRecordingIds.Count == 0;
                bool emptyChildren = bp.ChildRecordingIds == null
                    || bp.ChildRecordingIds.Count == 0;
                if (emptyParents || emptyChildren)
                {
                    if (droppedBranchPointIds == null)
                        droppedBranchPointIds = new HashSet<string>(StringComparer.Ordinal);
                    if (!string.IsNullOrEmpty(bp.Id))
                        droppedBranchPointIds.Add(bp.Id);
                    tree.BranchPoints.RemoveAt(i);
                }
            }
            return scrubbed;
        }

        /// <summary>
        /// Refreshes persistent.sfs after an explicit pending-tree discard when the
        /// discarded tree had already been serialized as <c>isPending=True</c>. This
        /// removes stale pending metadata from the game's current save without committing
        /// the discarded tree. quicksave.sfs is the player's F5 point and is not written
        /// (design-load-restores-the-save.md 4.6): a save-file load restores the save
        /// exactly, so an F9 to it is consistent.
        /// </summary>
        internal static void RefreshSaveAndQuicksaveAfterDiscard(
            string reason,
            int discardedRecordingCount)
        {
            var saveFn = RecordingStore.SaveGameForTesting ?? GamePersistence.SaveGame;

            if (RecordingStore.SaveGameForTesting == null)
            {
                if (HighLogic.LoadedScene == GameScenes.LOADING)
                {
                    ParsekLog.Verbose("Quicksave",
                        $"Discard save refresh skipped (LOADING scene): reason={reason}");
                    return;
                }
                if (HighLogic.CurrentGame == null)
                {
                    ParsekLog.Verbose("Quicksave",
                        $"Discard save refresh skipped (CurrentGame is null): reason={reason}");
                    return;
                }
            }

            RefreshSaveAfterDiscard(saveFn, "persistent", "persistent.sfs", reason, discardedRecordingCount);
            ParsekLog.Verbose("Quicksave",
                $"Quicksave not refreshed after discard (the player's F5 point is kept): reason={reason}");
        }

        /// <summary>
        /// The persistent.sfs refresh for an AUTOMATIC discard. Same scene / game guards as
        /// <see cref="RefreshSaveAndQuicksaveAfterDiscard"/>, without its Verbose line.
        /// </summary>
        internal static void RefreshPersistentSaveAfterDiscard(
            string reason,
            int discardedRecordingCount)
        {
            var saveFn = RecordingStore.SaveGameForTesting ?? GamePersistence.SaveGame;
            if (RecordingStore.SaveGameForTesting == null)
            {
                if (HighLogic.LoadedScene == GameScenes.LOADING || HighLogic.CurrentGame == null)
                {
                    ParsekLog.Verbose("Quicksave",
                        $"Discard persistent refresh skipped (LOADING scene or no CurrentGame): reason={reason}");
                    return;
                }
            }
            RefreshSaveAfterDiscard(saveFn, "persistent", "persistent.sfs", reason, discardedRecordingCount);
        }

        private static void RefreshSaveAfterDiscard(
            System.Func<string, string, SaveMode, string> saveFn,
            string saveName,
            string displayName,
            string reason,
            int discardedRecordingCount)
        {
            try
            {
                string result = saveFn(saveName, HighLogic.SaveFolder, SaveMode.OVERWRITE);
                if (string.IsNullOrEmpty(result))
                {
                    ParsekLog.Warn("Quicksave",
                        $"GamePersistence.SaveGame returned null refreshing {displayName} " +
                        $"after {reason} — {displayName} NOT refreshed");
                    return;
                }
                ParsekLog.Info("Quicksave",
                    $"Refreshed {displayName} after {reason} " +
                    $"(discarded tree had {discardedRecordingCount} recording IDs)");
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn("Quicksave",
                    $"Exception refreshing {displayName} after {reason}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Pure committed-overlap decision for a pending-tree discard: a recording id
        /// that is committed IN MEMORY, or that the save file declares committed while
        /// the store is still unpopulated, must keep its events and its sidecars. Only
        /// an id owned solely by the pending tree may be purged and deleted.
        /// </summary>
        internal static bool ShouldPreserveCommittedOverlapOnPendingDiscard(
            bool committedInMemory, bool durableCommittedHint)
        {
            return committedInMemory || durableCommittedHint;
        }
    }
}
