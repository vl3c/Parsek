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
    /// <para><b>Scope.</b> Captures <c>committedRecordings</c>, <c>committedTrees</c>,
    /// <c>pendingTree</c>/<c>pendingTreeState</c>, and the
    /// <see cref="RecordingGroupStore"/> auto-assigned-standalone-group dict — i.e. the
    /// recording-store fields <see cref="RecordingStore.ResetForTesting"/> mutates.
    /// Sidecar files on disk are not snapshotted; tests that produce sidecars must
    /// clean those up themselves (the production bug only mishandled in-memory state).
    /// </para>
    /// </summary>
    internal sealed class RecordingStoreTestSnapshot
    {
        private readonly List<Recording> committedRecordings;
        private readonly List<RecordingTree> committedTrees;
        private readonly RecordingTree pendingTree;
        private readonly PendingTreeState pendingTreeState;
        private readonly Dictionary<string, string> autoAssignedStandaloneGroups;

        private RecordingStoreTestSnapshot(
            List<Recording> committedRecordings,
            List<RecordingTree> committedTrees,
            RecordingTree pendingTree,
            PendingTreeState pendingTreeState,
            Dictionary<string, string> autoAssignedStandaloneGroups)
        {
            this.committedRecordings = committedRecordings;
            this.committedTrees = committedTrees;
            this.pendingTree = pendingTree;
            this.pendingTreeState = pendingTreeState;
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
                autoAssignedStandaloneGroups);
        }
    }
}
