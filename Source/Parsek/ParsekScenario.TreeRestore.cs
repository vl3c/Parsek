using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // Restoring the pending and active trees from the scenario node, and the quickload-resume
    // context the restore arms.
    public partial class ParsekScenario
    {
        internal static bool IsActiveTreeNode(ConfigNode treeNode)
        {
            if (treeNode == null) return false;
            string val = treeNode.GetValue("isActive");
            return !string.IsNullOrEmpty(val)
                && bool.TryParse(val, out bool isActive) && isActive;
        }

        /// <summary>
        /// Returns true if a RECORDING_TREE ConfigNode represents a finalized pending
        /// tree written by <c>SavePendingTreeIfAny</c>, distinguished by
        /// <c>isPending=True</c>.
        /// </summary>
        internal static bool IsPendingTreeNode(ConfigNode treeNode)
        {
            if (treeNode == null) return false;
            string val = treeNode.GetValue("isPending");
            return !string.IsNullOrEmpty(val)
                && bool.TryParse(val, out bool isPending) && isPending;
        }

        internal static bool TryRestorePendingTreeNode(
            ConfigNode node,
            bool activeTreeRestoredFromSave = false)
        {
            if (node == null) return false;
            if (RewindContext.IsRewinding)
            {
                ParsekLog.Verbose("Scenario",
                    "TryRestorePendingTreeNode: skipped (rewind in progress)");
                return false;
            }

            ConfigNode[] treeNodes = node.GetNodes("RECORDING_TREE");
            int pendingMarkerCount = 0;
            int activeMarkerCount = 0;
            ConfigNode pendingNode = null;
            for (int t = 0; t < treeNodes.Length; t++)
            {
                if (IsActiveTreeNode(treeNodes[t]))
                    activeMarkerCount++;
                if (IsPendingTreeNode(treeNodes[t]))
                {
                    pendingMarkerCount++;
                    if (pendingNode == null)
                        pendingNode = treeNodes[t];
                }
            }

            if (pendingMarkerCount == 0)
                return false;

            bool preserveDuringActiveRestore = false;
            if (activeMarkerCount > 0)
            {
                preserveDuringActiveRestore = activeTreeRestoredFromSave;
                ParsekLog.Warn("Scenario",
                    $"TryRestorePendingTreeNode: found {pendingMarkerCount} pending marker(s) " +
                    $"alongside {activeMarkerCount} active marker(s); " +
                    (preserveDuringActiveRestore
                        ? "preserving saved pending tree separately while active tree restore keeps priority"
                        : "active restore did not run, restoring pending tree normally") +
                    $" (activeRestored={activeTreeRestoredFromSave})");
            }

            if (pendingMarkerCount > 1)
            {
                ParsekLog.Warn("Scenario",
                    $"TryRestorePendingTreeNode: found {pendingMarkerCount} pending marker(s); " +
                    "restoring the first and ignoring the rest");
            }

            var tree = RecordingTree.Load(pendingNode);
            if (tree.Recordings == null || tree.Recordings.Count == 0)
            {
                ParsekLog.Warn("Scenario",
                    $"TryRestorePendingTreeNode: skipped pending tree '{tree.TreeName}' id={tree.Id} " +
                    "because all recordings were rejected by the schema gate");
                return false;
            }
            int sidecarHydrationFailures = 0;
            int syntheticFixtureFailures = 0;
            foreach (var rec in tree.Recordings.Values)
            {
                if (!RecordingSidecarStore.LoadRecordingFiles(rec))
                {
                    sidecarHydrationFailures++;
                    if (IsSyntheticFixtureSidecarMarker(rec))
                        syntheticFixtureFailures++;
                }
            }

            // The pending-node mirror of the active-tree decline: a tree this session discarded
            // would come back as an empty shell to merge or auto-commit.
            if (TryDeclineDiscardedPendingTree(tree))
                return false;

            int droppedHydrationFailures = DropFailedSidecarHydrationRecordings(
                tree,
                "TryRestorePendingTreeNode");
            if (tree.Recordings == null || tree.Recordings.Count == 0)
            {
                ParsekLog.Warn("Scenario",
                    $"TryRestorePendingTreeNode: skipped pending tree '{tree.TreeName}' id={tree.Id} " +
                    $"after dropping {droppedHydrationFailures} recording(s) with incompatible sidecars");
                return false;
            }

            if (RepairMissingContiguousChainPredecessors(tree, "TryRestorePendingTreeNode") > 0)
                tree.RebuildBackgroundMap();
            // A tree written before a split moved its parent links names the head for
            // events on a later segment; point them at the segment that holds them.
            SplitParentLinks.RepairStaleParents(tree, "TryRestorePendingTreeNode");

            if (preserveDuringActiveRestore)
                PendingTreeSlot.PreservePendingTreeFromSaveDuringActiveRestore(tree);
            else
                PendingTreeSlot.RestorePendingTreeFromSave(tree);
            EmitSidecarHydrationRollup(
                tree.TreeName, sidecarHydrationFailures, syntheticFixtureFailures, "pending tree");

            ParsekLog.Info("Scenario",
                $"TryRestorePendingTreeNode: " +
                (preserveDuringActiveRestore ? "preserved" : "restored") +
                $" pending tree '{tree.TreeName}' " +
                $"({tree.Recordings.Count} recording(s), sidecarFailures={sidecarHydrationFailures}, " +
                $"stashedThisTransition={PendingTreeSlot.PendingStashedThisTransition})");
            RecorderStateLog.RecState("TryRestorePendingTreeNode:restored", CaptureScenarioRecorderState());
            return true;
        }

        /// <summary>
        /// Attempts to restore an active (in-flight) recording tree from the save node
        /// into the pending-limbo slot. Called from OnLoad immediately after settings
        /// persistence and before revert detection, so the revert-detection logic can
        /// decide whether to restore-and-resume (quickload) or finalize-and-commit
        /// (real revert).
        ///
        /// <para>Skipped when rewinding — <see cref="RewindContext.IsRewinding"/> means
        /// the load is Parsek's own rewind flow which explicitly resets playback state.</para>
        ///
        /// Returns true if an active tree was found and stashed as Limbo.
        /// <paramref name="earlyLoadKind"/> is the load's prologue kind (the save-attempt re-arm
        /// reads it) and <paramref name="loadedSceneIsFlight"/> whether the load lands in FLIGHT
        /// (the discarded-tree decline reads it).
        /// </summary>
        /// <summary>
        /// Pure: true when <paramref name="tree"/>, the active tree a load restores, is the live
        /// Re-Fly session's tree: the marker names its id and its active Re-Fly recording is a
        /// member (the field <c>LoadTimeSweep</c> validates the marker on). Such a load keeps
        /// the committed copy committed beside it, the shape the session had before the save.
        /// </summary>
        internal static bool IsResumedReFlySessionTree(ReFlySessionMarker marker, RecordingTree tree)
        {
            if (marker == null || tree == null || tree.Recordings == null)
                return false;
            if (string.IsNullOrEmpty(marker.TreeId) || string.IsNullOrEmpty(marker.ActiveReFlyRecordingId))
                return false;
            return string.Equals(marker.TreeId, tree.Id, StringComparison.Ordinal)
                && tree.Recordings.ContainsKey(marker.ActiveReFlyRecordingId);
        }

        /// <summary>
        /// Pure: true when an active-tree restore keeps the committed copy of its tree committed
        /// (and skips the splice) because a live committed-tree restore attempt is armed for
        /// that tree. Never after a save-attempt re-arm (that branch already keeps it) and
        /// never on a Re-Fly start, whose rewind-point load abandons the attempt's flight.
        /// </summary>
        internal static bool ShouldLiveAttemptKeepCommittedCopy(
            bool saveAttemptRearmed,
            EarlyLoadKind earlyLoadKind,
            bool attemptArmedForTree)
        {
            return !saveAttemptRearmed
                && earlyLoadKind != EarlyLoadKind.ReFlyStart
                && attemptArmedForTree;
        }

        /// <summary>
        /// Step of <see cref="TryRestoreActiveTreeNode"/>: the rewind-in-progress skip. Drops the resume rewind-save hint and consumes a
        /// pending active-tree restore suppression (the rewind already skips the restore).
        /// </summary>
        internal static void TryRestoreActiveTreeNodePhaseSkipForRewind()
        {
            pendingActiveTreeResumeRewindSave = null;
            if (PendingTreeSlot.TryConsumeNextActiveTreeRestoreSuppression(
                "TryRestoreActiveTreeNode:rewind-in-progress",
                out string rewindSuppressReason))
            {
                ParsekLog.Warn("Scenario",
                    "TryRestoreActiveTreeNode: consumed active-tree restore " +
                    $"suppression while rewind was already skipping restore reason='{rewindSuppressReason}'");
            }
            ParsekLog.Verbose("Scenario",
                "TryRestoreActiveTreeNode: skipped (rewind in progress — active tree deliberately discarded)");
        }

        /// <summary>
        /// Step of <see cref="TryRestoreActiveTreeNode"/>: the saved active tree lost every recording to the schema gate. Drops the resume
        /// rewind-save hint and the quickload-resume context.
        /// </summary>
        internal static void TryRestoreActiveTreeNodePhaseAbandonSchemaRejectedTree(RecordingTree tree)
        {
            pendingActiveTreeResumeRewindSave = null;
            ClearPendingQuickloadResumeContext();
            ParsekLog.Warn("Scenario",
                $"TryRestoreActiveTreeNode: skipped active tree '{tree.TreeName}' id={tree.Id} " +
                "because all recordings were rejected by the schema gate");
        }

        /// <summary>
        /// Step of <see cref="TryRestoreActiveTreeNode"/>: logs the quicksave facts just captured for <paramref name="tree"/>.
        /// </summary>
        internal static void TryRestoreActiveTreeNodePhaseLogCapturedQuicksaveFacts(RecordingTree tree)
        {
            ParsekLog.Verbose("Scenario",
                $"TryRestoreActiveTreeNode: captured quicksave facts tree='{tree.TreeName}' id={tree.Id} " +
                $"members={lastRestoredQuicksaveTreeFacts?.Members.Count ?? 0} " +
                $"treeCommittedInQuicksave={(lastRestoredQuicksaveTreeFacts != null && lastRestoredQuicksaveTreeFacts.TreeCommittedInQuicksave ? "true" : "false")}");
        }

        /// <summary>
        /// Step of <see cref="TryRestoreActiveTreeNode"/>: a consumed restore suppression declines the saved active tree. Drops the resume
        /// rewind-save hint and leaves the committed tree as it is.
        /// </summary>
        internal static void TryRestoreActiveTreeNodePhaseAbandonSuppressedTree(RecordingTree tree, string suppressReason)
        {
            pendingActiveTreeResumeRewindSave = null;
            ParsekLog.Info("Scenario",
                "TryRestoreActiveTreeNode: suppressed saved active tree restore " +
                $"tree='{tree.TreeName}' id={tree.Id} recordings={tree.Recordings.Count} " +
                $"activeRecId={tree.ActiveRecordingId ?? "<null>"} reason='{suppressReason}' - " +
                "leaving committed mission tree intact and not stashing pending-Limbo");
        }

        /// <summary>
        /// Step of <see cref="TryRestoreActiveTreeNode"/>: the sidecar-hydration drop emptied the saved active tree. Drops the resume
        /// rewind-save hint and the quickload-resume context.
        /// </summary>
        internal static void TryRestoreActiveTreeNodePhaseAbandonEmptiedTree(RecordingTree tree, int droppedHydrationFailures)
        {
            pendingActiveTreeResumeRewindSave = null;
            ClearPendingQuickloadResumeContext();
            ParsekLog.Warn("Scenario",
                $"TryRestoreActiveTreeNode: skipped active tree '{tree.TreeName}' id={tree.Id} " +
                $"after dropping {droppedHydrationFailures} recording(s) with incompatible sidecars");
        }

        /// <summary>
        /// Step of <see cref="TryRestoreActiveTreeNode"/>: repairs a missing contiguous chain predecessor (rebuilding the background map
        /// when one was repaired) and the stale split parent links.
        /// </summary>
        internal static void TryRestoreActiveTreeNodePhaseRepairChainLinks(RecordingTree tree)
        {
            if (RepairMissingContiguousChainPredecessors(tree, "TryRestoreActiveTreeNode") > 0)
                tree.RebuildBackgroundMap();
            // A tree written before a split moved its parent links names the head for
            // events on a later segment; point them at the segment that holds them.
            SplitParentLinks.RepairStaleParents(tree, "TryRestoreActiveTreeNode");
        }

        /// <summary>
        /// Step of <see cref="TryRestoreActiveTreeNode"/>: logs why the committed copy of the tree stayed committed (a resumed Re-Fly
        /// session, a re-armed or a live restore attempt), or that there was none to detach.
        /// </summary>
        internal static void TryRestoreActiveTreeNodePhaseLogCommittedCopyDisposition(RecordingTree tree, bool reFlySessionResumed, bool saveAttemptRearmed,
            bool liveAttemptKeepsCommittedCopy, bool committedCopyDetached)
        {
            if (reFlySessionResumed && !saveAttemptRearmed)
            {
                ParsekLog.Info("Scenario",
                    $"TryRestoreActiveTreeNode: kept the committed copy of tree '{tree.TreeName}' " +
                    $"(id={tree.Id}) committed: the restored tree is the live Re-Fly session " +
                    $"sess={Instance?.ActiveReFlySessionMarker?.SessionId ?? "<no-id>"}");
            }
            if (saveAttemptRearmed)
            {
                ParsekLog.Info("Scenario",
                    $"TryRestoreActiveTreeNode: kept the committed copy of tree '{tree.TreeName}' " +
                    $"(id={tree.Id}) committed and skipped the splice: the restored tree is the " +
                    "re-armed restore attempt's clone");
            }
            else if (liveAttemptKeepsCommittedCopy)
            {
                ParsekLog.Info("Scenario",
                    $"TryRestoreActiveTreeNode: kept the committed copy of tree '{tree.TreeName}' " +
                    $"(id={tree.Id}) committed: the restored tree is the live restore attempt's clone");
            }
            else if (!committedCopyDetached && !reFlySessionResumed)
            {
                ParsekLog.Verbose("Scenario",
                    $"TryRestoreActiveTreeNode: no committed copy of tree '{tree.TreeName}' " +
                    $"(id={tree.Id}) needed detaching");
            }
        }

        /// <summary>
        /// Step of <see cref="TryRestoreActiveTreeNode"/>: replaces the pending tree with the restored one (Limbo, or LimboVesselSwitch
        /// when it has no active recording), reads the resume rewind-save hint from
        /// <c>treeNodes[t]</c> and logs the stash.
        /// </summary>
        internal static void TryRestoreActiveTreeNodePhaseStashAsPendingLimbo(RecordingTree tree, ConfigNode[] treeNodes, int t,
            int sidecarHydrationFailures, int salvagedHydrationFailures, int splicedFromCommitted)
        {
            // Pop any existing pending tree silently — StashActiveTreeAsPendingLimbo
            // stashed the in-memory (future-timeline) version at OnSceneChangeRequested
            // time; we want the freshly-loaded disk version (matches the quicksave UT
            // the user is rewinding to). PopPendingTree is non-destructive (doesn't
            // delete sidecar files), and avoids the "overwriting existing pending tree"
            // warning on the expected-overwrite quickload path.
            PendingTreeSlot.PopPendingTree();

            // Bug #266: pick the stash state based on whether the saved tree had a
            // live active recording. If ActiveRecordingId is null, the tree was in
            // outsider state at OnSave time (the player switched to a vessel with
            // no recording context). The vessel-switch restore coroutine handles
            // this case; the quickload restore would bail on the null active rec.
            var stashState = string.IsNullOrEmpty(tree.ActiveRecordingId)
                ? PendingTreeState.LimboVesselSwitch
                : PendingTreeState.Limbo;
            PendingTreeSlot.StashPendingTree(tree, stashState);

            // Read resume hints for the restore coroutine (rewind save filename only).
            pendingActiveTreeResumeRewindSave = treeNodes[t].GetValue("resumeRewindSave");

            ParsekLog.Info("Scenario",
                $"TryRestoreActiveTreeNode: stashed active tree '{tree.TreeName}' " +
                $"({tree.Recordings.Count} recording(s), activeRecId={tree.ActiveRecordingId ?? "<null>"}) " +
                $"into pending-{stashState} slot for revert-detection dispatch" +
                (sidecarHydrationFailures > 0
                    ? $" with {sidecarHydrationFailures} sidecar hydration failure(s)" +
                      (salvagedHydrationFailures > 0
                          ? $", salvaged {salvagedHydrationFailures} from pending"
                          : "")
                    : "") +
                (splicedFromCommitted > 0
                    ? $", spliced {splicedFromCommitted} post-RP recording(s) from committed tree"
                    : ""));
            RecorderStateLog.RecState("TryRestoreActiveTreeNode:stashed", CaptureScenarioRecorderState());
        }

        /// <summary>
        /// Step of <see cref="TryRestoreActiveTreeNode"/>: no active tree node was found; consumes a pending active-tree restore
        /// suppression that had nothing to suppress.
        /// </summary>
        internal static void TryRestoreActiveTreeNodePhaseConsumeUnusedSuppression()
        {
            if (PendingTreeSlot.TryConsumeNextActiveTreeRestoreSuppression(
                "TryRestoreActiveTreeNode:no-active-tree",
                out string noActiveSuppressReason))
            {
                ParsekLog.Warn("Scenario",
                    "TryRestoreActiveTreeNode: consumed active-tree restore " +
                    $"suppression but no active tree node was present reason='{noActiveSuppressReason}'");
            }
        }

        internal static bool TryRestoreActiveTreeNode(
            ConfigNode node,
            EarlyLoadKind earlyLoadKind = EarlyLoadKind.InSession,
            bool loadedSceneIsFlight = true)
        {
            lastRestoredQuicksaveTreeFacts = null;
            pendingFreshRecordingAfterDiscard = null;
            if (node == null) return false;
            if (RewindContext.IsRewinding)
            {
                TryRestoreActiveTreeNodePhaseSkipForRewind();
                return false;
            }

            ConfigNode[] treeNodes = node.GetNodes("RECORDING_TREE");
            for (int t = 0; t < treeNodes.Length; t++)
            {
                if (!IsActiveTreeNode(treeNodes[t])) continue;

                var tree = RecordingTree.Load(treeNodes[t]);
                if (tree.Recordings == null || tree.Recordings.Count == 0)
                {
                    TryRestoreActiveTreeNodePhaseAbandonSchemaRejectedTree(tree);
                    return false;
                }

                // What the quicksave itself says about each member, read from the node before
                // hydration, salvage, the stale-epoch keep or the same-id refresh can bring the
                // committed future in. The quickload-resume trim reads it to leave whole a
                // member that was already committed history when the quicksave was taken.
                lastRestoredQuicksaveTreeFacts = CaptureQuicksaveTreeFacts(
                    tree, CollectQuicksaveCommittedRecordingIds(node), CollectQuicksaveCommittedTreeIds(node));
                TryRestoreActiveTreeNodePhaseLogCapturedQuicksaveFacts(tree);
                if (PendingTreeSlot.TryConsumeNextActiveTreeRestoreSuppression(
                    "TryRestoreActiveTreeNode:active-tree",
                    out string suppressReason))
                {
                    TryRestoreActiveTreeNodePhaseAbandonSuppressedTree(tree, suppressReason);
                    return false;
                }

                int sidecarHydrationFailures = 0;
                int staleEpochHydrationFailures = 0;
                // Hydrate bulk data from sidecar files for each recording.
                foreach (var rec in tree.Recordings.Values)
                {
                    if (!RecordingSidecarStore.LoadRecordingFiles(rec))
                    {
                        sidecarHydrationFailures++;
                        if (rec.SidecarLoadFailureReason == StaleSidecarEpochReason)
                            staleEpochHydrationFailures++;
                    }
                }

                // A tree this session discarded is not resumed into its deleted ids: the flight
                // records again as a new tree (QUICKLOAD-INTO-DISCARDED-FLIGHT-RECORDS-AGAIN).
                if (TryDeclineDiscardedActiveTree(tree, loadedSceneIsFlight))
                    return false;

                if (ShouldKeepPendingTreeAfterHydrationFailure(tree, staleEpochHydrationFailures))
                {
                    ParsekLog.Warn("Scenario",
                        $"TryRestoreActiveTreeNode: keeping in-memory pending tree " +
                        $"'{PendingTreeSlot.PendingTree.TreeName}' because saved active tree " +
                        $"'{tree.TreeName}' had {staleEpochHydrationFailures} stale-sidecar epoch failure(s)");
                    return true;
                }

                int salvagedHydrationFailures = RestoreHydrationFailedRecordingsFromPendingTree(tree);
                int droppedHydrationFailures = DropFailedSidecarHydrationRecordings(
                    tree,
                    "TryRestoreActiveTreeNode");
                if (tree.Recordings == null || tree.Recordings.Count == 0)
                {
                    TryRestoreActiveTreeNodePhaseAbandonEmptiedTree(tree, droppedHydrationFailures);
                    return false;
                }

                // Bug #601: Re-Fly load preserves post-RP merge tree mutations.
                // The RP's frozen .sfs snapshots the tree state AT RP creation time.
                // If a merge / SplitAtSection ran between RP creation and Re-Fly invocation,
                // the in-memory committed tree has post-split recording IDs (and updated
                // BranchPoint parent refs) that the loaded .sfs does NOT know about. The
                // .prec sidecars for the post-split halves still exist on disk, but they're
                // orphaned because the loaded tree never lists them. Splice those recordings
                // (and any committed-tree-only BranchPoints / parent-id updates) back into
                // the loaded tree BEFORE RemoveCommittedTreeById destroys the in-memory copy.
                // The spliced recordings are deep-cloned and marked dirty so the next OnSave
                // rewrites the .sfs + .prec with fresh sidecar epochs and the live shape.
                // The active recording id is forwarded so the same-ID refresh path can run
                // in recorder-state-preserving mode for the active recording — at this point
                // the recorder has not yet rebound (rebind fires on the deferred onFlightReady
                // pass after this scene-load OnLoad), so there is no in-flight payload state
                // to lose, but a small set of [NonSerialized] mitigation flags
                // (FilesDirty / SidecarLoadFailed / SidecarLoadFailureReason /
                // Pre-ReFly anchor trajectory) may already be set
                // by earlier load-time code paths and the refresh must not wipe them. The
                // structural fields (trajectory, orbit segments, track sections, terminal
                // state, ChildBranchPointId) ARE refreshed for the active recording — that is
                // the whole point of the P1 fix, since the active id IS most likely the stale
                // post-split first half from the RP's frozen .sfs.
                //
                // A cold load of a save written while a committed-tree restore attempt was live
                // (the tree is the attempt's copy-on-write clone; the save names it in
                // GAME_STATE/ATTEMPT_EVENTS) re-arms the attempt from the save's committed copy,
                // so the clone resumes as the live attempt it was. Both trees come from the same
                // OnSave and the clone descends from the committed copy, so the splice has
                // nothing newer to bring in; its same-id refresh would instead overwrite the
                // attempt's own edits (a segment's parent link) with the committed shape and mark
                // the members dirty, so the next save would write the committed shape over the
                // attempt's own sidecars. Both the splice and the detach are skipped.
                bool saveAttemptRearmed = TryRearmSaveAttemptForRestoredTree(tree, earlyLoadKind);
                // A restore attempt still armed in memory for this tree (an in-session load of a
                // save the live attempt wrote, e.g. the one stock makes before leaving flight) is
                // the same shape: the save's tree is the live clone, so it keeps its committed
                // original committed (the scene-exit merge must replace it, a Discard must find
                // it) and skips the splice (its same-id refresh would put the committed trajectory
                // over a recording resumed in place).
                // Not on a Re-Fly start: the rewind point's load abandons the pre-rewind flight
                // whose attempt this is (ConsumePostLoad discards it), so the session tree takes
                // the splice and the detach like any other restore, or the merge never sees the
                // post-rewind-point recordings of the slot it re-flies.
                bool liveAttemptKeepsCommittedCopy = ShouldLiveAttemptKeepCommittedCopy(
                    saveAttemptRearmed,
                    earlyLoadKind,
                    CommittedTreeRestoreAttempt.IsCommittedTreeRestoreAttemptTree(tree.Id));
                int splicedFromCommitted = saveAttemptRearmed || liveAttemptKeepsCommittedCopy
                    ? 0
                    : SpliceMissingCommittedRecordingsIntoLoadedTree(tree, tree.ActiveRecordingId);
                TryRestoreActiveTreeNodePhaseRepairChainLinks(tree);

                // If the same tree id is already in committedTrees (e.g. the player
                // quicksaved in flight, then exited to TS which committed the tree, then
                // quickloaded — the in-memory committedTrees retains the T3 version even
                // though the save file has the T2 active version), remove the committed
                // copy so the active version is the single source of truth. Otherwise
                // the next OnSave would write the tree twice with the same id.
                // A re-armed attempt keeps its committed original committed, as a live attempt
                // does: a later Discard drops only the clone, and a Merge replaces the original.
                // A Re-Fly session the save resumes keeps it too: a session runs with the
                // committed original beside its live tree (the Re-Fly start's bundle restore puts
                // it back), so ghost chains still hold back the vessels it claims (CI-3).
                bool reFlySessionResumed =
                    IsResumedReFlySessionTree(Instance?.ActiveReFlySessionMarker, tree);
                bool committedCopyDetached = !saveAttemptRearmed
                    && !reFlySessionResumed
                    && !liveAttemptKeepsCommittedCopy
                    && RecordingStore.RemoveCommittedTreeById(
                        tree.Id,
                        logContext: "TryRestoreActiveTreeNode");
                TryRestoreActiveTreeNodePhaseLogCommittedCopyDisposition(
                    tree, reFlySessionResumed, saveAttemptRearmed, liveAttemptKeepsCommittedCopy, committedCopyDetached);

                // Bug #290d: if the pending tree is already Finalized (set by
                // CommitTreeSceneExit during the same scene transition), it has
                // post-finalize data (MaxDistanceFromLaunch, terminal states, snapshots)
                // that the .sfs version does NOT have (OnSave runs BEFORE finalization).
                // Skip the replacement — the in-memory Finalized tree is authoritative.
                if (PendingTreeSlot.HasPendingTree
                    && PendingTreeSlot.PendingTreeStateValue == PendingTreeState.Finalized)
                {
                    ParsekLog.Info("Scenario",
                        $"TryRestoreActiveTreeNode: keeping in-memory Finalized tree " +
                        $"'{PendingTreeSlot.PendingTree.TreeName}' — skipping .sfs replacement " +
                        $"(OnSave ran before finalization, .sfs maxDist is stale)");
                    return true;
                }

                TryRestoreActiveTreeNodePhaseStashAsPendingLimbo(
                    tree, treeNodes, t, sidecarHydrationFailures, salvagedHydrationFailures, splicedFromCommitted);
                return true;
            }
            TryRestoreActiveTreeNodePhaseConsumeUnusedSuppression();
            return false;
        }

        private sealed class QuickloadResumeContext
        {
            internal string TreeId;
            internal QuickloadTrimScope TrimScope;
            internal string TrimScopeReason;
            // The load that armed the resume (the resume-prep line logs it).
            internal LoadKind LoadKind;
            // What the loaded save said about the tree's members (null when the resumed tree
            // did not come from this load's save node).
            internal QuicksaveTreeFacts QuicksaveFacts;
        }

        internal static void ConfigurePendingQuickloadResumeContext(
            RecordingTree tree, LoadKind loadKind)
        {
            if (tree == null || string.IsNullOrEmpty(tree.Id) || string.IsNullOrEmpty(tree.ActiveRecordingId))
            {
                ClearPendingQuickloadResumeContext();
                return;
            }

            var marker = Instance?.ActiveReFlySessionMarker;
            var trimScope = ChooseQuickloadTrimScope(tree.Id, marker, out string trimScopeReason);

            QuicksaveTreeFacts quicksaveFacts = lastRestoredQuicksaveTreeFacts != null
                && string.Equals(lastRestoredQuicksaveTreeFacts.TreeId, tree.Id, StringComparison.Ordinal)
                    ? lastRestoredQuicksaveTreeFacts
                    : null;
            lastRestoredQuicksaveTreeFacts = null;

            pendingQuickloadResumeContext = new QuickloadResumeContext
            {
                TreeId = tree.Id,
                TrimScope = trimScope,
                TrimScopeReason = trimScopeReason,
                LoadKind = loadKind,
                QuicksaveFacts = quicksaveFacts,
            };

            ParsekLog.Verbose("Scenario",
                $"Quickload-resume context armed: treeId={tree.Id} activeRecId={tree.ActiveRecordingId} " +
                $"trimScope={trimScope} ({trimScopeReason}) loadKind={loadKind} " +
                $"quicksaveFacts={(quicksaveFacts != null ? quicksaveFacts.Members.Count.ToString(CultureInfo.InvariantCulture) : "none")}");
        }

        /// <summary>
        /// The quicksave facts the pending quickload-resume context carries for
        /// <paramref name="treeId"/>, or null.
        /// </summary>
        internal static QuicksaveTreeFacts GetPendingQuickloadQuicksaveFacts(string treeId)
        {
            QuickloadResumeContext context = pendingQuickloadResumeContext;
            if (context == null
                || string.IsNullOrEmpty(treeId)
                || !string.Equals(context.TreeId, treeId, StringComparison.Ordinal))
            {
                return null;
            }
            return context.QuicksaveFacts;
        }

        internal static void ClearRestoredQuicksaveTreeFactsForTesting()
        {
            lastRestoredQuicksaveTreeFacts = null;
        }

        /// <summary>
        /// The load kind the pending quickload-resume context was armed with, or null when no
        /// context is armed for <paramref name="treeId"/>.
        /// </summary>
        internal static LoadKind? GetPendingQuickloadLoadKind(string treeId)
        {
            QuickloadResumeContext context = pendingQuickloadResumeContext;
            if (context == null
                || string.IsNullOrEmpty(treeId)
                || !string.Equals(context.TreeId, treeId, StringComparison.Ordinal))
            {
                return null;
            }
            return context.LoadKind;
        }

        internal static void RefreshPendingQuickloadTrimScope()
        {
            QuickloadResumeContext context = pendingQuickloadResumeContext;
            if (context == null)
                return;

            var marker = Instance?.ActiveReFlySessionMarker;
            var trimScope = ChooseQuickloadTrimScope(context.TreeId, marker, out string trimScopeReason);
            context.TrimScope = trimScope;
            context.TrimScopeReason = trimScopeReason;

            ParsekLog.Verbose("Scenario",
                $"Quickload-resume trim scope refreshed: treeId={context.TreeId} " +
                $"trimScope={trimScope} ({trimScopeReason})");
        }

        internal static bool MatchesPendingQuickloadResumeContext(string treeId)
        {
            return pendingQuickloadResumeContext != null
                && string.Equals(pendingQuickloadResumeContext.TreeId, treeId, StringComparison.Ordinal);
        }

        internal static QuickloadTrimScope GetPendingQuickloadTrimScope(
            string treeId,
            out string reason)
        {
            if (pendingQuickloadResumeContext == null)
            {
                reason = "no-pending-quickload-resume-context";
                return QuickloadTrimScope.TreeWide;
            }

            if (string.IsNullOrEmpty(treeId)
                || !string.Equals(pendingQuickloadResumeContext.TreeId, treeId, StringComparison.Ordinal))
            {
                reason = $"pending-context-tree-mismatch contextTree={pendingQuickloadResumeContext.TreeId ?? "<null>"} resumeTree={treeId ?? "<null>"}";
                return QuickloadTrimScope.TreeWide;
            }

            reason = pendingQuickloadResumeContext.TrimScopeReason ?? "pending-context-no-reason";
            return pendingQuickloadResumeContext.TrimScope;
        }

        internal static void ClearPendingQuickloadResumeContext()
        {
            pendingQuickloadResumeContext = null;
        }

        /// <summary>
        /// Restore path that <see cref="ParsekFlight.OnFlightReady"/> should run on
        /// the next flight load.
        /// </summary>
        internal enum ActiveTreeRestoreMode
        {
            /// <summary>No restore scheduled.</summary>
            None = 0,
            /// <summary>
            /// Quickload-resume path: name-match the active vessel against the tree's
            /// active recording and resume the in-flight recorder. Used after F9 / cold
            /// start with a saved <c>PARSEK_ACTIVE_TREE</c> node.
            /// </summary>
            Quickload = 1,
            /// <summary>
            /// Vessel-switch restore path (#266): the tree was pre-transitioned at stash
            /// time, so the restore coroutine just reinstalls it as <c>activeTree</c>
            /// and optionally promotes the new active vessel from <c>BackgroundMap</c>.
            /// </summary>
            VesselSwitch = 2,
        }
    }
}
