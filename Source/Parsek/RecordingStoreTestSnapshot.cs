using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Non-destructive snapshot of <see cref="RecordingStore"/> state for in-game test
    /// fixtures that need to inject synthetic recordings without wiping the player's
    /// live save data.
    ///
    /// <para><b>Why this exists.</b> The in-game test runner (Ctrl+Shift+T) executes
    /// inside the player's running KSP session. <see cref="RecordingStore"/>'s
    /// committed lists are static and shared with the live save. Calling
    /// <c>RecordingStore.ResetForTesting()</c> from an in-game test silently destroys
    /// the player's recordings (observed in production 2026-05-01: 5 committed
    /// recordings wiped by <c>PersistenceSplitOptimizerTest</c>). xUnit tests do not
    /// hit this path because they run outside Unity play mode.</para>
    ///
    /// <para><b>Usage.</b> Call <see cref="Capture"/> at the start of the in-game test,
    /// add synthetic recordings via the existing <c>AddRecordingWithTreeForTesting</c>
    /// helper, and call <see cref="Restore"/> in a <c>finally</c> block. The synthetic
    /// recordings get removed; everything the player had before the test is reinstated
    /// in-place.</para>
    ///
    /// <para><b>Scope — what IS captured.</b> Exactly six pieces of in-memory state:
    /// <list type="bullet">
    ///   <item><description><c>committedRecordings</c> — list contents and ordering, reference-shallow.</description></item>
    ///   <item><description><c>committedTrees</c> — list contents and ordering, reference-shallow.</description></item>
    ///   <item><description><c>pendingTree</c> — the slot reference, reference-shallow.</description></item>
    ///   <item><description><c>pendingTreeState</c> — the enum value.</description></item>
    ///   <item><description><c>savedPendingTreeDuringActiveRestore</c> — the preserved pending-tree reference, reference-shallow.</description></item>
    ///   <item><description><c>RecordingGroupStore.AutoAssignedStandaloneGroups</c> — dict copy.</description></item>
    /// </list></para>
    ///
    /// <para><b>Scope — what is NOT captured.</b> <see cref="RecordingStore.ResetForTesting"/>
    /// also clears a number of unrelated subsystems that this snapshot does not touch:
    /// <c>SceneEntryActiveVesselPid</c>, the rewind-replay-target scope, <c>RewindContext</c>
    /// state, <c>RewindUTAdjustmentPending</c>, <c>GameStateRecorder.PendingScienceSubjects</c>,
    /// <c>PendingCleanupPids</c> / <c>PendingCleanupNames</c>, <c>PendingStashedThisTransition</c>,
    /// and the legacy-merge-state-migration emit-once flag. This snapshot is intended for
    /// SPACECENTER-scene synthetic-fixture tests that only mutate the recording-store
    /// in-memory lists; rewind / cleanup / scene-entry state is out of scope. Callers that
    /// run inside FLIGHT or that touch any of the above must not rely on this snapshot for
    /// rollback.</para>
    ///
    /// <para><b>Reference-shallow.</b> Every captured collection is a copy of the list
    /// header, not a deep clone of the underlying <c>Recording</c> / <c>RecordingTree</c>
    /// instances. <see cref="Restore"/> only reinstates list membership and ordering — it
    /// CANNOT undo per-instance field mutations on shared <c>Recording</c> /
    /// <c>RecordingTree</c> references. Tests that hand a captured-then-shared instance to
    /// production code which mutates fields in place (e.g. <c>RunOptimizationPass</c> sets
    /// <c>ChainId</c>, <c>SegmentBodyName</c>, <c>FilesDirty</c>) will see those mutations
    /// survive Restore. This is why <see cref="Parsek.InGameTests.PersistenceSplitOptimizerTest"/>
    /// no longer runs the global optimizer pass over live recordings — Restore cannot roll
    /// back the in-place mutation, nor can it undo the sidecar file writes / deletes the
    /// optimizer triggers.</para>
    ///
    /// <para><b>Disk.</b> Sidecar files on disk are not snapshotted; tests that produce
    /// sidecars must clean those up themselves (the production bug only mishandled
    /// in-memory state).</para>
    /// </summary>
    internal sealed class RecordingStoreTestSnapshot
    {
        private readonly List<Recording> committedRecordings;
        private readonly List<RecordingTree> committedTrees;
        private readonly RecordingTree pendingTree;
        private readonly PendingTreeState pendingTreeState;
        private readonly RecordingTree savedPendingTreeDuringActiveRestore;
        private readonly bool savedPendingTreeDuringActiveRestoreSerializedForSave;
        private readonly Dictionary<string, string> autoAssignedStandaloneGroups;

        private RecordingStoreTestSnapshot(
            List<Recording> committedRecordings,
            List<RecordingTree> committedTrees,
            RecordingTree pendingTree,
            PendingTreeState pendingTreeState,
            RecordingTree savedPendingTreeDuringActiveRestore,
            bool savedPendingTreeDuringActiveRestoreSerializedForSave,
            Dictionary<string, string> autoAssignedStandaloneGroups)
        {
            this.committedRecordings = committedRecordings;
            this.committedTrees = committedTrees;
            this.pendingTree = pendingTree;
            this.pendingTreeState = pendingTreeState;
            this.savedPendingTreeDuringActiveRestore = savedPendingTreeDuringActiveRestore;
            this.savedPendingTreeDuringActiveRestoreSerializedForSave =
                savedPendingTreeDuringActiveRestoreSerializedForSave;
            this.autoAssignedStandaloneGroups = autoAssignedStandaloneGroups;
        }

        public int CommittedRecordingCount => committedRecordings.Count;
        public int CommittedTreeCount => committedTrees.Count;
        public bool HasPendingTree => pendingTree != null;

        /// <summary>
        /// Captures the live <see cref="RecordingStore"/> state. Cheap — copies the list
        /// references but not the underlying recording bodies. The store is left fully
        /// populated; the caller can layer test fixtures on top.
        /// </summary>
        public static RecordingStoreTestSnapshot Capture()
        {
            return new RecordingStoreTestSnapshot(
                new List<Recording>(RecordingStore.CommittedRecordings),
                new List<RecordingTree>(RecordingStore.CommittedTrees),
                RecordingStore.PendingTree,
                RecordingStore.PendingTreeStateValue,
                RecordingStore.SavedPendingTreeDuringActiveRestore,
                RecordingStore.SavedPendingTreeDuringActiveRestoreSerializedForSave,
                RecordingGroupStore.SnapshotAutoAssignedStandaloneGroupsForTesting());
        }

        /// <summary>
        /// Restores the captured state in place. Anything the test added (synthetic
        /// recordings, synthetic trees, a fresh pending tree) is replaced by the
        /// snapshot contents; anything the test removed comes back.
        /// </summary>
        public void Restore()
        {
            RecordingStore.RestoreFromSnapshotForTesting(
                committedRecordings,
                committedTrees,
                pendingTree,
                pendingTreeState,
                savedPendingTreeDuringActiveRestore,
                savedPendingTreeDuringActiveRestoreSerializedForSave,
                autoAssignedStandaloneGroups);
        }
    }
}
