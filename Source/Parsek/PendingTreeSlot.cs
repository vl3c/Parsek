using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// The pending-tree slot (RecordingStore services plan, service I): the tree awaiting the
    /// merge dialog or auto-commit, its <see cref="PendingTreeState"/>, whether a save node
    /// already holds it, the saved Finalized pending tree an active-tree quickload restore
    /// parks beside it, the stashed-this-transition flag, and the one-shot next-commit /
    /// next-restore suppressions. <c>ParsekScenario.OnSave</c> serializes the slot and
    /// <c>ParsekScenario.OnLoad</c> dispatches it (Limbo / revert / quickload). The state is
    /// save-scoped: a cold load drops it, files untouched (<see cref="ResetForLoad"/>).
    /// Committing and discarding the pending tree stay on <see cref="RecordingStore"/>
    /// (<see cref="TreeCommitService.CommitPendingTree"/>, <see cref="RecordingStore.DiscardPendingTree"/>),
    /// which empty the slot through <see cref="ClearSlot"/>.
    /// </summary>
    internal static class PendingTreeSlot
    {
        // Set true by StashPendingTree during OnSceneChangeRequested.
        // Checked by ParsekScenario.OnLoad to distinguish a freshly-stashed pending
        // from the current scene transition (keep it long enough for revert-vs-
        // quickload-vs-non-flight dispatch) from a stale pending left over from a
        // previous flight (discard per #64).
        internal static bool PendingStashedThisTransition;
        private static bool suppressNextTreeSceneExitCommit;
        private static string suppressNextTreeSceneExitCommitReason;
        private static bool suppressNextActiveTreeRestore;
        private static string suppressNextActiveTreeRestoreReason;

        // Pending tree awaiting merge dialog (Task 8) or auto-commit
        private static RecordingTree pendingTree;
        // State of the pending tree slot. Finalized = legacy behavior (tree has terminal
        // state, snapshots, ready to commit). Limbo = new quickload-resume path: tree is
        // still "in-flight", untriaged, waiting for OnLoad to decide revert-vs-quickload.
        // See docs/dev/plans/quickload-resume-recording.md.
        private static PendingTreeState pendingTreeState = PendingTreeState.Finalized;
        // True only for a Finalized pending tree known to be represented by an
        // isPending=True save node; every pending-slot ownership/state change resets it.
        private static bool pendingTreeSerializedForSave;
        // Active-tree quickload resume temporarily uses pendingTree as a Limbo
        // handoff slot. If the same save also carries a finalized isPending=True
        // tree, keep it here until the active tree is popped back into flight.
        // Invariant: this side slot only holds a saved Finalized pending tree
        // awaiting OnLoad -> PromoteSavedPendingTreeAfterActiveRestore; normal
        // stash/commit/pop/unstash lifecycle methods intentionally leave it alone.
        private static RecordingTree savedPendingTreeDuringActiveRestore;
        private static bool savedPendingTreeDuringActiveRestoreSerializedForSave;

        public static bool HasPendingTree => pendingTree != null;
        public static RecordingTree PendingTree => pendingTree;
        public static PendingTreeState PendingTreeStateValue => pendingTreeState;
        internal static bool PendingTreeSerializedForSave => pendingTreeSerializedForSave;
        internal static bool HasSavedPendingTreeDuringActiveRestore =>
            savedPendingTreeDuringActiveRestore != null;
        internal static RecordingTree SavedPendingTreeDuringActiveRestore =>
            savedPendingTreeDuringActiveRestore;
        internal static bool SavedPendingTreeDuringActiveRestoreSerializedForSave =>
            savedPendingTreeDuringActiveRestoreSerializedForSave;

        /// <summary>
        /// Empties the pending-tree slot: no tree, <see cref="PendingTreeState.Finalized"/>,
        /// not serialized. Leaves the saved-pending slot, the stashed-this-transition flag and
        /// the suppressions alone.
        /// </summary>
        internal static void ClearSlot()
        {
            pendingTree = null;
            pendingTreeState = PendingTreeState.Finalized;
            pendingTreeSerializedForSave = false;
        }

        /// <summary>
        /// Cold-load reset, called by <see cref="RecordingStore.ResetSaveScopedStateForLoad"/>:
        /// the pending tree and the saved pending tree are DROPPED, not discarded (no purge,
        /// sidecars stay on disk), and the stashed-this-transition flag and both suppressions
        /// are forgotten.
        /// </summary>
        internal static void ResetForLoad()
        {
            pendingTree = null;
            pendingTreeState = PendingTreeState.Finalized;
            pendingTreeSerializedForSave = false;
            savedPendingTreeDuringActiveRestore = null;
            savedPendingTreeDuringActiveRestoreSerializedForSave = false;
            PendingStashedThisTransition = false;
            suppressNextTreeSceneExitCommit = false;
            suppressNextTreeSceneExitCommitReason = null;
            suppressNextActiveTreeRestore = false;
            suppressNextActiveTreeRestoreReason = null;
        }

        /// <summary>Test reset, called by <c>RecordingStore.ResetForTestingInternal</c>.</summary>
        internal static void ResetForTesting()
        {
            pendingTree = null;
            pendingTreeState = PendingTreeState.Finalized;
            pendingTreeSerializedForSave = false;
            savedPendingTreeDuringActiveRestore = null;
            savedPendingTreeDuringActiveRestoreSerializedForSave = false;
            PendingStashedThisTransition = false;
            suppressNextTreeSceneExitCommit = false;
            suppressNextTreeSceneExitCommitReason = null;
            suppressNextActiveTreeRestore = false;
            suppressNextActiveTreeRestoreReason = null;
        }

        /// <summary>
        /// Re-installs the slot a <see cref="RecordingStoreTestSnapshot"/> captured. Used only by
        /// <see cref="RecordingStore.RestoreFromSnapshotForTesting"/>.
        /// </summary>
        internal static void RestoreFromSnapshotForTesting(
            RecordingTree snapshotPendingTree,
            PendingTreeState snapshotPendingState,
            RecordingTree snapshotSavedPendingTreeDuringActiveRestore,
            bool snapshotSavedPendingTreeDuringActiveRestoreSerializedForSave)
        {
            pendingTree = snapshotPendingTree;
            pendingTreeState = snapshotPendingState;
            pendingTreeSerializedForSave = false;
            savedPendingTreeDuringActiveRestore = snapshotSavedPendingTreeDuringActiveRestore;
            savedPendingTreeDuringActiveRestoreSerializedForSave =
                snapshotSavedPendingTreeDuringActiveRestoreSerializedForSave;
        }


        /// <summary>
        /// Stashes a tree as pending with <see cref="PendingTreeState.Finalized"/>.
        /// Legacy behavior — the tree has already been through
        /// <c>FinalizeTreeRecordings</c> and is ready for the merge dialog or auto-commit.
        /// </summary>
        public static void StashPendingTree(RecordingTree tree)
            => StashPendingTree(tree, PendingTreeState.Finalized);

        /// <summary>
        /// Stashes a tree as pending with an explicit state.
        /// <see cref="PendingTreeState.Finalized"/> is the legacy path (terminal state set,
        /// snapshots preserved). <see cref="PendingTreeState.Limbo"/> is the quickload-resume
        /// path — tree is still in-flight, recorder was torn down without finalization, and
        /// OnLoad will decide whether to restore-and-resume or finalize-then-commit.
        /// </summary>
        public static void StashPendingTree(RecordingTree tree, PendingTreeState state)
        {
            if (pendingTree != null)
            {
                ParsekLog.Warn("RecordingStore",
                    $"StashPendingTree({state}): overwriting existing pending tree " +
                    $"'{pendingTree.TreeName}' (state={pendingTreeState}) with '{tree?.TreeName ?? "<null>"}'");
            }
            pendingTree = tree;
            pendingTreeState = state;
            pendingTreeSerializedForSave = false;
            if (tree != null)
            {
                PendingStashedThisTransition = true;
                RecordingStore.Log($"[Parsek] Stashed pending tree '{tree.TreeName}' ({tree.Recordings.Count} recordings, state={state})");
                Recording activeStashRec = null;
                if (!string.IsNullOrEmpty(tree.ActiveRecordingId))
                    tree.Recordings.TryGetValue(tree.ActiveRecordingId, out activeStashRec);
                if (activeStashRec != null)
                    ParsekLog.Verbose("RecordingStore",
                        $"StashPendingTree: active {activeStashRec.DebugName}");
            }
        }

        internal static void MarkPendingTreeSerializedForSave(string context)
        {
            if (pendingTree == null)
            {
                ParsekLog.Verbose("RecordingStore",
                    $"MarkPendingTreeSerializedForSave skipped: no pending tree context={context ?? "<none>"}");
                return;
            }

            pendingTreeSerializedForSave = true;
            ParsekLog.Verbose("RecordingStore",
                $"Pending tree '{pendingTree.TreeName}' marked as serialized " +
                $"(state={pendingTreeState}, context={context ?? "<none>"})");
        }

        internal static void RestorePendingTreeFromSave(RecordingTree tree)
        {
            if (tree == null)
            {
                ParsekLog.Verbose("RecordingStore",
                    "RestorePendingTreeFromSave called with null tree");
                return;
            }

            if (pendingTree != null)
            {
                ParsekLog.Warn("RecordingStore",
                    $"RestorePendingTreeFromSave: overwriting existing pending tree " +
                    $"'{pendingTree.TreeName}' (state={pendingTreeState}, serialized={pendingTreeSerializedForSave}) " +
                    $"with saved pending tree '{tree.TreeName}'");
            }

            pendingTree = tree;
            // This path reinstalls saved isPending nodes (Finalized pending trees) only.
            // Limbo resume trees take the isActive marker instead and round-trip through
            // TryRestoreActiveTreeNode / StashPendingTree, never here, so reinstating as
            // Finalized is correct. Does not arm PendingStashedThisTransition.
            pendingTreeState = PendingTreeState.Finalized;
            PendingStashedThisTransition = false;
            pendingTreeSerializedForSave = true;
            RecordingStore.Log($"[Parsek] Restored pending tree '{tree.TreeName}' from save " +
                $"({tree.Recordings.Count} recordings, state=Finalized, stashedThisTransition=False)");
        }

        internal static void PreservePendingTreeFromSaveDuringActiveRestore(RecordingTree tree)
        {
            if (tree == null)
            {
                ParsekLog.Verbose("RecordingStore",
                    "PreservePendingTreeFromSaveDuringActiveRestore called with null tree");
                return;
            }

            if (savedPendingTreeDuringActiveRestore != null)
            {
                ParsekLog.Warn("RecordingStore",
                    $"PreservePendingTreeFromSaveDuringActiveRestore: overwriting saved pending tree " +
                    $"'{savedPendingTreeDuringActiveRestore.TreeName}' with '{tree.TreeName}'");
            }

            savedPendingTreeDuringActiveRestore = tree;
            savedPendingTreeDuringActiveRestoreSerializedForSave = true;
            RecordingStore.Log($"[Parsek] Preserved saved pending tree '{tree.TreeName}' while active-tree restore owns " +
                $"the pending-Limbo slot ({tree.Recordings.Count} recordings)");
        }

        internal static bool MarkSavedPendingTreeDuringActiveRestoreSerializedForSave(string context)
        {
            if (savedPendingTreeDuringActiveRestore == null)
            {
                ParsekLog.Verbose("RecordingStore",
                    $"MarkSavedPendingTreeDuringActiveRestoreSerializedForSave skipped: no saved pending tree context={context ?? "<none>"}");
                return false;
            }

            savedPendingTreeDuringActiveRestoreSerializedForSave = true;
            ParsekLog.Verbose("RecordingStore",
                $"Saved pending tree '{savedPendingTreeDuringActiveRestore.TreeName}' preserved during active restore " +
                $"marked as serialized (context={context ?? "<none>"})");
            return true;
        }

        internal static bool PromoteSavedPendingTreeAfterActiveRestore(string context)
        {
            if (savedPendingTreeDuringActiveRestore == null)
                return false;

            if (pendingTree != null)
            {
                ParsekLog.Warn("RecordingStore",
                    $"PromoteSavedPendingTreeAfterActiveRestore: cannot promote saved pending tree " +
                    $"'{savedPendingTreeDuringActiveRestore.TreeName}' because pending slot is occupied by " +
                    $"'{pendingTree.TreeName}' (state={pendingTreeState}, context={context ?? "<none>"})");
                return false;
            }

            pendingTree = savedPendingTreeDuringActiveRestore;
            pendingTreeState = PendingTreeState.Finalized;
            pendingTreeSerializedForSave = savedPendingTreeDuringActiveRestoreSerializedForSave;
            PendingStashedThisTransition = false;

            savedPendingTreeDuringActiveRestore = null;
            savedPendingTreeDuringActiveRestoreSerializedForSave = false;

            RecordingStore.Log($"[Parsek] Promoted saved pending tree '{pendingTree.TreeName}' after active-tree restore " +
                $"({pendingTree.Recordings.Count} recordings, serialized={pendingTreeSerializedForSave}, " +
                $"context={context ?? "<none>"})");
            return true;
        }

        /// <summary>
        /// Arms a one-shot guard for scene transitions that intentionally throw
        /// away the active flight's in-memory tree. Discard Re-Fly uses this
        /// before loading the origin RP's save: the loaded save already contains
        /// the pre-Re-Fly state, so the outgoing scene must not auto-stash the
        /// discarded attempt as a pending merge tree.
        /// </summary>
        internal static void ArmNextTreeSceneExitCommitSuppression(string reason)
        {
            suppressNextTreeSceneExitCommit = true;
            suppressNextTreeSceneExitCommitReason =
                string.IsNullOrEmpty(reason) ? "<unspecified>" : reason;
            ParsekLog.Info("RecordingStore",
                $"Armed next tree scene-exit commit suppression reason='{suppressNextTreeSceneExitCommitReason}'");
        }

        internal static bool TryConsumeNextTreeSceneExitCommitSuppression(
            GameScenes destinationScene,
            out string reason)
        {
            reason = null;
            if (!suppressNextTreeSceneExitCommit)
                return false;

            reason = suppressNextTreeSceneExitCommitReason ?? "<unspecified>";
            suppressNextTreeSceneExitCommit = false;
            suppressNextTreeSceneExitCommitReason = null;
            ParsekLog.Info("RecordingStore",
                $"Consumed tree scene-exit commit suppression reason='{reason}' dest={destinationScene}");
            return true;
        }

        /// <summary>
        /// Read-only peek (no consume) of the tree-scene-exit-commit
        /// suppression flag. Production callers (the
        /// <c>HighLogic.LoadScene</c> prefix in
        /// <c>SceneExitInterceptor</c>) check this to detect that a
        /// transition is already owned by Discard Re-Fly so they can
        /// bypass without stealing the flag from
        /// <c>FinalizeTreeOnSceneChange</c>'s consume contract.
        /// </summary>
        internal static bool IsNextTreeSceneExitCommitSuppressionArmed
            => suppressNextTreeSceneExitCommit;

        /// <summary>
        /// Arms a one-shot guard for the next saved active-tree restore pass.
        /// Discard Re-Fly uses this after loading the origin RP save: the save can
        /// still contain an isActive tree for quickload resume, but this load is an
        /// intentional reset back to the RP, not a resume or merge candidate.
        /// </summary>
        internal static void ArmNextActiveTreeRestoreSuppression(string reason)
        {
            suppressNextActiveTreeRestore = true;
            suppressNextActiveTreeRestoreReason =
                string.IsNullOrEmpty(reason) ? "<unspecified>" : reason;
            ParsekLog.Info("RecordingStore",
                $"Armed next active-tree restore suppression reason='{suppressNextActiveTreeRestoreReason}'");
        }

        internal static bool TryConsumeNextActiveTreeRestoreSuppression(
            string context,
            out string reason)
        {
            reason = null;
            if (!suppressNextActiveTreeRestore)
                return false;

            reason = suppressNextActiveTreeRestoreReason ?? "<unspecified>";
            suppressNextActiveTreeRestore = false;
            suppressNextActiveTreeRestoreReason = null;
            ParsekLog.Info("RecordingStore",
                $"Consumed active-tree restore suppression reason='{reason}' context={context ?? "<none>"}");
            return true;
        }

        internal static bool NextActiveTreeRestoreSuppressionArmedForTesting
            => suppressNextActiveTreeRestore;

        /// <summary>
        /// #434: "soft" unstash for the revert path. Clears the pending-tree slot WITHOUT
        /// deleting sidecar files or purging tagged events. Rationale: KSP's revert preserves
        /// persistent.sfs and sidecar files on both revert-to-launch (cache-driven, never
        /// touches disk) and revert-to-VAB/SPH (persistent.sfs rewritten with prelaunch state,
        /// but sidecars untouched). A player who F5'd during the flight and later F9s that
        /// quicksave must find the Parsek recording files still there; the ACTIVE_TREE node
        /// in that quicksave's persistent.sfs references sidecar files by recording id, which
        /// would dangle if we deleted them.
        ///
        /// Event-store staleness after revert is handled by the recording-id visibility
        /// filter: tagged events stay hidden until a matching quickload restores the
        /// active tree. <see cref="ParsekFileCollector"/> at the next cold-start reclaims
        /// sidecars no quicksave still references.
        ///
        /// Contrast with <see cref="RecordingStore.DiscardPendingTree"/>, which runs the #431 purge +
        /// file deletion for pending-only ids on the merge-dialog Discard button's
        /// explicit "throw it away" choice.
        /// </summary>
        public static void UnstashPendingTreeOnRevert()
        {
            if (pendingTree == null)
            {
                ParsekLog.Verbose("RecordingStore", "UnstashPendingTreeOnRevert called with no pending tree");
                // P2 fix: clear science subjects even when there's no pending tree — the
                // in-flight recorder may have captured subjects that were never stashed.
                int strayBefore = GameStateRecorder.PendingScienceSubjects.Count;
                if (strayBefore > 0)
                {
                    GameStateRecorder.PendingScienceSubjects.Clear();
                    ParsekLog.Info("RecordingStore",
                        $"UnstashPendingTreeOnRevert: cleared {strayBefore} in-flight science subject(s) even with no pending tree");
                }
                return;
            }

            string treeName = pendingTree.TreeName;
            var prevState = pendingTreeState;
            int recCount = pendingTree.Recordings.Count;

            // P2 fix: GameStateRecorder.PendingScienceSubjects is a static in-memory list
            // that is neither epoch-tagged nor serialized. If we don't clear it here, the
            // next committed recording's NotifyLedgerTreeCommitted will flush these stale
            // subjects onto an unrelated mission. F9-from-flight-quicksave semantics don't
            // lose anything — the list wasn't in the quicksave anyway, so no regression vs.
            // the pre-#434 discard path (which cleared this list unconditionally).
            int subjectsCleared = GameStateRecorder.PendingScienceSubjects.Count;
            GameStateRecorder.PendingScienceSubjects.Clear();

            pendingTree = null;
            pendingTreeState = PendingTreeState.Finalized;
            pendingTreeSerializedForSave = false;
            RecordingStore.Log($"[Parsek] Unstashed pending tree '{treeName}' on revert " +
                $"(was state={prevState}, {recCount} recording(s), {subjectsCleared} pending science subject(s) cleared): " +
                "sidecar files preserved for F9-from-flight-quicksave; " +
                "events stay in-memory and on-disk, filtered by recording-id visibility");
        }

        internal static bool IsPendingRecordingId(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return false;
            if (pendingTree != null
                && pendingTree.Recordings != null
                && pendingTree.Recordings.ContainsKey(recordingId))
                return true;
            return savedPendingTreeDuringActiveRestore != null
                && savedPendingTreeDuringActiveRestore.Recordings != null
                && savedPendingTreeDuringActiveRestore.Recordings.ContainsKey(recordingId);
        }

        /// <summary>
        /// Re-labels a <see cref="PendingTreeState.LimboVesselSwitch"/> pending tree whose
        /// pre-transition was undone (it has an active recording again) as a plain
        /// <see cref="PendingTreeState.Limbo"/> stash. Returns false and changes nothing
        /// unless the slot holds a LimboVesselSwitch tree with a non-empty
        /// <c>ActiveRecordingId</c>.
        /// </summary>
        internal static bool ConvertPendingVesselSwitchStashToLimbo(string context)
        {
            if (pendingTree == null
                || pendingTreeState != PendingTreeState.LimboVesselSwitch
                || string.IsNullOrEmpty(pendingTree.ActiveRecordingId))
            {
                ParsekLog.Verbose("RecordingStore",
                    $"ConvertPendingVesselSwitchStashToLimbo skipped: tree=" +
                    $"{pendingTree?.TreeName ?? "<none>"} state={pendingTreeState} " +
                    $"activeRecId={pendingTree?.ActiveRecordingId ?? "<null>"} " +
                    $"context={context ?? "<none>"}");
                return false;
            }
            pendingTreeState = PendingTreeState.Limbo;
            pendingTreeSerializedForSave = false;
            ParsekLog.Info("RecordingStore",
                $"Pending tree '{pendingTree.TreeName}' transitioned LimboVesselSwitch -> Limbo " +
                $"(activeRecId={pendingTree.ActiveRecordingId}, context={context ?? "<none>"})");
            return true;
        }

        /// <summary>
        /// Marks the pending tree's state as Finalized after the revert-detection dispatch
        /// has run FinalizeTreeRecordings on a previously-Limbo tree. Called by
        /// ParsekScenario.OnLoad on the Limbo + isRevert path before the auto-commit /
        /// merge dialog flow runs.
        /// </summary>
        internal static void MarkPendingTreeFinalized()
        {
            if (pendingTree == null) return;
            if (pendingTreeState == PendingTreeState.Finalized) return;
            pendingTreeState = PendingTreeState.Finalized;
            pendingTreeSerializedForSave = false;
            ParsekLog.Info("RecordingStore",
                $"Pending tree '{pendingTree.TreeName}' transitioned Limbo → Finalized");
        }

        /// <summary>
        /// Removes the pending tree from the slot WITHOUT deleting its sidecar files or
        /// clearing pending science subjects. Returns the tree so the caller can take
        /// ownership (typically to re-install as the active tree during quickload-resume).
        /// Unlike <see cref="RecordingStore.DiscardPendingTree"/>, this is non-destructive — the caller
        /// is responsible for the tree's lifetime after popping.
        /// </summary>
        internal static RecordingTree PopPendingTree()
        {
            var tree = pendingTree;
            pendingTree = null;
            pendingTreeState = PendingTreeState.Finalized;
            pendingTreeSerializedForSave = false;
            if (tree != null)
            {
                RecordingStore.Log($"[Parsek] Popped pending tree '{tree.TreeName}' (caller takes ownership, files preserved)");
            }
            return tree;
        }

        /// <summary>
        /// #431: sets <see cref="PendingTreeStateValue"/> directly. For unit tests only —
        /// production always transitions state via <see cref="StashPendingTree"/>,
        /// <see cref="TreeCommitService.CommitPendingTree"/>, <see cref="RecordingStore.DiscardPendingTree"/>, or the limbo-stash
        /// paths. Used by the LimboVesselSwitch tag-fallback test.
        /// </summary>
        internal static void SetPendingTreeStateForTesting(PendingTreeState state)
        {
            pendingTreeState = state;
            pendingTreeSerializedForSave = false;
        }
    }
}
