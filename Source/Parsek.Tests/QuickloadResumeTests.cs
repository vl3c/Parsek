using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for the quickload-resume recording pipeline (Bug C fix).
    /// Covers:
    /// - <see cref="PendingTreeState"/> transitions
    /// - <see cref="RecordingStore.StashPendingTree(RecordingTree, PendingTreeState)"/>
    /// - <see cref="RecordingStore.PopPendingTree"/> (non-destructive pop)
    /// - <see cref="RecordingStore.MarkPendingTreeFinalized"/>
    /// - ScenarioWriter round-trip of an active tree flagged with isActive=True
    /// - <see cref="ParsekScenario.IsActiveTreeNode"/> dispatch
    ///
    /// End-to-end testing (OnSave → scene reload → OnLoad → resume coroutine)
    /// requires Unity runtime and is covered by in-game tests, not here.
    /// </summary>
    [Collection("Sequential")]
    public class QuickloadResumeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public QuickloadResumeTests()
        {
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();

            // Enable logging through the test sink (matches TreeLogVerificationTests pattern)
            RecordingStore.SuppressLogging = false;
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            // Suppress side effects that would crash outside Unity
            GameStateStore.SuppressLogging = true;
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        // ============================================================
        // PendingTreeState transitions
        // ============================================================

        [Fact]
        public void DefaultState_IsFinalized()
        {
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
        }

        [Fact]
        public void StashPendingTree_DefaultsToFinalized()
        {
            var tree = MakeTree("tree_a", "Launch", 2);
            RecordingStore.StashPendingTree(tree);
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
            Assert.Same(tree, RecordingStore.PendingTree);
        }

        [Fact]
        public void StashPendingTree_WithLimboState_IsLimbo()
        {
            var tree = MakeTree("tree_a", "Launch", 2);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
            Assert.Contains(logLines, l => l.Contains("state=Limbo"));
        }

        [Fact]
        public void StashPendingTree_Overwriting_LogsWarning()
        {
            var first = MakeTree("first", "First", 1);
            var second = MakeTree("second", "Second", 1);
            RecordingStore.StashPendingTree(first, PendingTreeState.Limbo);
            logLines.Clear();

            RecordingStore.StashPendingTree(second, PendingTreeState.Finalized);

            Assert.Same(second, RecordingStore.PendingTree);
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
            Assert.Contains(logLines,
                l => l.Contains("overwriting existing pending tree") && l.Contains("'First'"));
        }

        [Fact]
        public void MarkPendingTreeFinalized_FlipsLimboToFinalized()
        {
            var tree = MakeTree("tree_a", "Mun", 2);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);
            logLines.Clear();

            RecordingStore.MarkPendingTreeFinalized();

            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
            Assert.Contains(logLines, l => l.Contains("Limbo → Finalized"));
        }

        [Fact]
        public void MarkPendingTreeFinalized_OnAlreadyFinalized_NoOp()
        {
            var tree = MakeTree("tree_a", "Mun", 2);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
            logLines.Clear();

            RecordingStore.MarkPendingTreeFinalized();

            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
            // No Limbo → Finalized log line since no transition happened
            Assert.DoesNotContain(logLines, l => l.Contains("Limbo → Finalized"));
        }

        [Fact]
        public void MarkPendingTreeFinalized_NoPendingTree_NoOp()
        {
            RecordingStore.MarkPendingTreeFinalized();
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
            Assert.Null(RecordingStore.PendingTree);
        }

        // ============================================================
        // PopPendingTree non-destructive behavior
        // ============================================================

        [Fact]
        public void PopPendingTree_ReturnsTreeAndClearsSlot()
        {
            var tree = MakeTree("tree_a", "Mun", 2);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);

            var popped = RecordingStore.PopPendingTree();

            Assert.Same(tree, popped);
            Assert.Null(RecordingStore.PendingTree);
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
        }

        [Fact]
        public void PopPendingTree_NoTree_ReturnsNull()
        {
            var popped = RecordingStore.PopPendingTree();
            Assert.Null(popped);
        }

        // ============================================================
        // Discard and Clear reset state
        // ============================================================

        [Fact]
        public void DiscardPendingTree_ResetsStateToFinalized()
        {
            var tree = MakeTree("tree_a", "Mun", 2);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);
            RecordingStore.DiscardPendingTree();
            Assert.Null(RecordingStore.PendingTree);
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
        }

        [Fact]
        public void CommitPendingTree_ResetsStateToFinalized()
        {
            var tree = MakeTree("tree_a", "Mun", 2);
            // CommitTree needs file writes which fail outside Unity, so this test
            // only verifies the guard path (no pending tree → no-op)
            RecordingStore.ResetForTesting();
            RecordingStore.CommitPendingTree();
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
        }

        [Fact]
        public void Clear_ResetsStateToFinalized()
        {
            var tree = MakeTree("tree_a", "Mun", 2);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);
            RecordingStore.Clear();
            Assert.Null(RecordingStore.PendingTree);
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
        }

        // ============================================================
        // IsActiveTreeNode dispatch
        // ============================================================

        [Fact]
        public void IsActiveTreeNode_NullNode_ReturnsFalse()
        {
            Assert.False(ParsekScenario.IsActiveTreeNode(null));
        }

        [Fact]
        public void IsActiveTreeNode_MissingFlag_ReturnsFalse()
        {
            var node = new ConfigNode("RECORDING_TREE");
            node.AddValue("id", "tree_x");
            Assert.False(ParsekScenario.IsActiveTreeNode(node));
        }

        [Fact]
        public void IsActiveTreeNode_FlagFalse_ReturnsFalse()
        {
            var node = new ConfigNode("RECORDING_TREE");
            node.AddValue("isActive", "False");
            Assert.False(ParsekScenario.IsActiveTreeNode(node));
        }

        [Fact]
        public void IsActiveTreeNode_FlagTrue_ReturnsTrue()
        {
            var node = new ConfigNode("RECORDING_TREE");
            node.AddValue("isActive", "True");
            Assert.True(ParsekScenario.IsActiveTreeNode(node));
        }

        [Fact]
        public void IsActiveTreeNode_FlagTrueMixedCase_ReturnsTrue()
        {
            var node = new ConfigNode("RECORDING_TREE");
            node.AddValue("isActive", "true");
            Assert.True(ParsekScenario.IsActiveTreeNode(node));
        }

        // ============================================================
        // TryRestoreActiveTreeNode end-to-end (parse → stash as Limbo)
        // ============================================================

        [Fact]
        public void TryRestoreActiveTreeNode_NoActiveTreeInSave_ReturnsFalseAndLeavesPendingEmpty()
        {
            var scenarioNode = new ConfigNode("PARSEK_SCENARIO");
            // Add a committed (non-active) tree to verify it's NOT stashed as Limbo
            var committedTreeNode = scenarioNode.AddNode("RECORDING_TREE");
            var committedTree = MakeTree("committed", "Launched", 2);
            committedTree.Save(committedTreeNode);

            bool result = ParsekScenario.TryRestoreActiveTreeNode(scenarioNode);

            Assert.False(result);
            Assert.Null(RecordingStore.PendingTree);
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
        }

        [Fact]
        public void TryRestoreActiveTreeNode_WithActiveTree_StashesAsLimbo()
        {
            var scenarioNode = new ConfigNode("PARSEK_SCENARIO");
            var activeTreeNode = scenarioNode.AddNode("RECORDING_TREE");
            var activeTree = MakeTree("in_flight", "Launching", 2);
            activeTree.Save(activeTreeNode);
            activeTreeNode.AddValue("isActive", "True");

            bool result = ParsekScenario.TryRestoreActiveTreeNode(scenarioNode);

            Assert.True(result);
            Assert.NotNull(RecordingStore.PendingTree);
            Assert.Equal("Launching", RecordingStore.PendingTree.TreeName);
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
        }

        [Fact]
        public void TryRestoreActiveTreeNode_SkipsCommittedTreeStashesActiveTree()
        {
            var scenarioNode = new ConfigNode("PARSEK_SCENARIO");
            // Committed tree first (no isActive flag)
            var committedNode = scenarioNode.AddNode("RECORDING_TREE");
            var committedTree = MakeTree("committed", "Prior Mission", 3);
            committedTree.Save(committedNode);
            // Then the active tree
            var activeNode = scenarioNode.AddNode("RECORDING_TREE");
            var activeTree = MakeTree("in_flight", "Current Mission", 2);
            activeTree.Save(activeNode);
            activeNode.AddValue("isActive", "True");

            bool result = ParsekScenario.TryRestoreActiveTreeNode(scenarioNode);

            Assert.True(result);
            Assert.Equal("Current Mission", RecordingStore.PendingTree.TreeName);
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
        }

        [Fact]
        public void TryRestoreActiveTreeNode_ParsesResumeRewindSave()
        {
            var scenarioNode = new ConfigNode("PARSEK_SCENARIO");
            var activeNode = scenarioNode.AddNode("RECORDING_TREE");
            var activeTree = MakeTree("in_flight", "Mun Return", 1);
            activeTree.Save(activeNode);
            activeNode.AddValue("isActive", "True");
            activeNode.AddValue("resumeRewindSave", "parsek_rw_abc123");

            bool result = ParsekScenario.TryRestoreActiveTreeNode(scenarioNode);

            Assert.True(result);
            Assert.Equal("parsek_rw_abc123", ParsekScenario.pendingActiveTreeResumeRewindSave);

            // Cleanup for subsequent tests
            ParsekScenario.pendingActiveTreeResumeRewindSave = null;
        }

        [Fact]
        public void TryRestoreActiveTreeNode_NoResumeRewindSave_ClearsStaleValue()
        {
            // Pre-populate a stale resume hint from a prior restore
            ParsekScenario.pendingActiveTreeResumeRewindSave = "stale_save";

            var scenarioNode = new ConfigNode("PARSEK_SCENARIO");
            var activeNode = scenarioNode.AddNode("RECORDING_TREE");
            var activeTree = MakeTree("in_flight", "Pad Walk", 1);
            activeTree.Save(activeNode);
            activeNode.AddValue("isActive", "True");
            // No resumeRewindSave key

            ParsekScenario.TryRestoreActiveTreeNode(scenarioNode);

            // Value becomes null (no key present)
            Assert.Null(ParsekScenario.pendingActiveTreeResumeRewindSave);
        }

        [Fact]
        public void TryRestoreActiveTreeNode_DoesNotWriteDeadBoundaryAnchorUT()
        {
            // BoundaryAnchor can't round-trip (needs full TrajectoryPoint, not just UT),
            // so we removed the resumeBoundaryAnchorUT serialization and parsing. This
            // test documents that a save with a legacy resumeBoundaryAnchorUT key from
            // an older build is simply ignored, not parsed back into anything.
            var scenarioNode = new ConfigNode("PARSEK_SCENARIO");
            var activeNode = scenarioNode.AddNode("RECORDING_TREE");
            var activeTree = MakeTree("in_flight", "Legacy Save", 1);
            activeTree.Save(activeNode);
            activeNode.AddValue("isActive", "True");
            activeNode.AddValue("resumeBoundaryAnchorUT", "99999.99"); // legacy key

            bool result = ParsekScenario.TryRestoreActiveTreeNode(scenarioNode);

            Assert.True(result);
            // The active tree is still restored; we just don't try to parse the anchor.
            Assert.Equal("Legacy Save", RecordingStore.PendingTree.TreeName);
        }

        [Fact]
        public void TryRestoreActiveTreeNode_DedupeCommittedTreeById()
        {
            // Reviewer edge case: flight → quicksave (writes isActive=True) →
            // exit to TS (commits tree into committedTrees) → quickload.
            // In-memory committedTrees still has the T3 version, disk save has the
            // T2 active version. Without dedupe, next OnSave writes the tree twice
            // with the same id. TryRestoreActiveTreeNode must remove the committed
            // copy before stashing the active copy.
            var committedTree = MakeTree("tree_x", "Duplicate Id", 2);
            RecordingStore.CommitTree(committedTree);
            Assert.Contains(committedTree, RecordingStore.CommittedTrees);

            // Build a save node with an isActive=True tree using the SAME id
            var scenarioNode = new ConfigNode("PARSEK_SCENARIO");
            var activeNode = scenarioNode.AddNode("RECORDING_TREE");
            var activeTree = MakeTree("tree_x", "Duplicate Id (active)", 2);
            activeTree.Save(activeNode);
            activeNode.AddValue("isActive", "True");

            ParsekScenario.TryRestoreActiveTreeNode(scenarioNode);

            // Committed copy should be gone; pending-Limbo holds the active version.
            Assert.DoesNotContain(
                RecordingStore.CommittedTrees,
                t => t.Id == "tree_x");
            Assert.Equal("Duplicate Id (active)", RecordingStore.PendingTree.TreeName);
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
        }

        [Fact]
        public void LastRecordedAltitude_DefaultsToNaN()
        {
            // LastRecordedAltitude caches the last committed point's altitude so
            // HandleSoiAutoSplit can read it even after FlushRecorderIntoActiveTreeForSerialization
            // clears the Recording buffer. The default is NaN (no points recorded yet),
            // which HandleSoiAutoSplit treats as "exo" (no altitude → fall through to
            // the default phase classification).
            var recorder = new FlightRecorder();
            Assert.True(double.IsNaN(recorder.LastRecordedAltitude));
        }

        [Fact]
        public void TryRestoreActiveTreeNode_NoOverwriteWarningOnExpectedOverwrite()
        {
            // StashActiveTreeAsPendingLimbo stashes the in-memory (future-timeline) tree
            // at OnSceneChangeRequested time. Then TryRestoreActiveTreeNode loads the
            // fresh disk version and stashes it. Without the PopPendingTree call,
            // StashPendingTree's overwrite-warning log fires on every successful
            // quickload. With the fix, TryRestoreActiveTreeNode pops first, then stashes
            // silently.
            var staleInMemoryTree = MakeTree("tree_y", "Stale In-Memory", 1);
            RecordingStore.StashPendingTree(staleInMemoryTree, PendingTreeState.Limbo);

            logLines.Clear();

            var scenarioNode = new ConfigNode("PARSEK_SCENARIO");
            var activeNode = scenarioNode.AddNode("RECORDING_TREE");
            var diskTree = MakeTree("tree_y", "Disk Version", 1);
            diskTree.Save(activeNode);
            activeNode.AddValue("isActive", "True");

            ParsekScenario.TryRestoreActiveTreeNode(scenarioNode);

            Assert.Equal("Disk Version", RecordingStore.PendingTree.TreeName);
            // No "overwriting existing pending tree" warning on the expected-overwrite path
            Assert.DoesNotContain(logLines,
                l => l.Contains("overwriting existing pending tree"));
        }

        // ============================================================
        // isRevert logic: removal of || isFlightToFlight clause
        // ============================================================

        [Fact]
        public void IsRevert_EpochDecreased_IsTrue()
        {
            // Pure logic test of the isRevert condition after fix (no FLIGHT→FLIGHT clause)
            bool isRevert = ComputeIsRevert(
                isVesselSwitch: false,
                savedEpoch: 5,
                currentEpoch: 6,
                totalSavedRecCount: 10,
                memoryRecordingsCount: 10);
            Assert.True(isRevert);
        }

        [Fact]
        public void IsRevert_CountDecreased_IsTrue()
        {
            bool isRevert = ComputeIsRevert(
                isVesselSwitch: false,
                savedEpoch: 5,
                currentEpoch: 5,
                totalSavedRecCount: 8,
                memoryRecordingsCount: 10);
            Assert.True(isRevert);
        }

        [Fact]
        public void IsRevert_QuickloadSameEpochSameCount_IsFalse()
        {
            // Quickload: both epoch and count match the memory state (since quicksave
            // captured both at the current moment).
            bool isRevert = ComputeIsRevert(
                isVesselSwitch: false,
                savedEpoch: 5,
                currentEpoch: 5,
                totalSavedRecCount: 10,
                memoryRecordingsCount: 10);
            Assert.False(isRevert);
        }

        [Fact]
        public void IsRevert_OrphanedLimboTree_FlightToFlight_IsTrue_Bug300()
        {
            // Bug #300: first-ever flight, no prior commits. Epoch and count both
            // zero on both sides. The orphaned Limbo tree (stashed from memory but
            // NOT found in the save file) is the revert signal.
            bool isRevert = ComputeIsRevert(
                isVesselSwitch: false,
                savedEpoch: 0,
                currentEpoch: 0,
                totalSavedRecCount: 0,
                memoryRecordingsCount: 0,
                isFlightToFlight: true,
                hasOrphanedLimboTree: true);
            Assert.True(isRevert);
        }

        [Fact]
        public void IsRevert_OrphanedLimboTree_NotFlightToFlight_IsFalse_Bug300()
        {
            // Safety: orphaned Limbo tree should only trigger revert detection in
            // FLIGHT→FLIGHT transitions, not on e.g. SPACECENTER→FLIGHT.
            bool isRevert = ComputeIsRevert(
                isVesselSwitch: false,
                savedEpoch: 0,
                currentEpoch: 0,
                totalSavedRecCount: 0,
                memoryRecordingsCount: 0,
                isFlightToFlight: false,
                hasOrphanedLimboTree: true);
            Assert.False(isRevert);
        }

        [Fact]
        public void IsRevert_LimboTreeRestoredFromSave_IsFalse_Bug300()
        {
            // Quickload (F5/F9): the save file contained the active tree, so
            // TryRestoreActiveTreeNode returned true → hasOrphanedLimboTree=false.
            // Should NOT be detected as a revert.
            bool isRevert = ComputeIsRevert(
                isVesselSwitch: false,
                savedEpoch: 0,
                currentEpoch: 0,
                totalSavedRecCount: 0,
                memoryRecordingsCount: 0,
                isFlightToFlight: true,
                hasOrphanedLimboTree: false);
            Assert.False(isRevert);
        }

        [Fact]
        public void IsRevert_OrphanedLimboTree_VesselSwitch_IsFalse_Bug300()
        {
            // Vessel switch suppresses revert even with an orphaned Limbo tree.
            bool isRevert = ComputeIsRevert(
                isVesselSwitch: true,
                savedEpoch: 0,
                currentEpoch: 0,
                totalSavedRecCount: 0,
                memoryRecordingsCount: 0,
                isFlightToFlight: true,
                hasOrphanedLimboTree: true);
            Assert.False(isRevert);
        }

        [Fact]
        public void IsRevert_VesselSwitch_IsFalseEvenIfEpochRegresses()
        {
            // Vessel switch flag suppresses isRevert regardless of other indicators
            // (defensive — in practice vessel switches preserve epoch/count anyway)
            bool isRevert = ComputeIsRevert(
                isVesselSwitch: true,
                savedEpoch: 4,
                currentEpoch: 6,
                totalSavedRecCount: 5,
                memoryRecordingsCount: 10);
            Assert.False(isRevert);
        }

        /// <summary>
        /// Mirrors the isRevert computation in ParsekScenario.OnLoad after bug #300.
        /// Kept here as a pure function so it can be unit-tested without needing a
        /// full ParsekScenario instance.
        /// </summary>
        private static bool ComputeIsRevert(
            bool isVesselSwitch, uint savedEpoch, uint currentEpoch,
            int totalSavedRecCount, int memoryRecordingsCount,
            bool isFlightToFlight = false, bool hasOrphanedLimboTree = false)
        {
            return !isVesselSwitch
                && (savedEpoch < currentEpoch
                    || totalSavedRecCount < memoryRecordingsCount
                    || (isFlightToFlight && hasOrphanedLimboTree));
        }

        /// <summary>
        /// Disposition the OnLoad Limbo-dispatch can take. Mirrors the four branches
        /// in ParsekScenario.cs after the bug #266 fix:
        /// <list type="bullet">
        ///   <item><c>Finalize</c> — real revert (terminal state set, merge dialog).</item>
        ///   <item><c>VesselSwitchRestore</c> — pre-transitioned tree (#266) reinstalled
        ///   via the new restore coroutine.</item>
        ///   <item><c>QuickloadRestore</c> — quickload / cold-start, name-match resume.</item>
        ///   <item><c>SafetyNetFinalize</c> — Limbo state but the OnLoad classifier still
        ///   says vessel switch (the stash didn't pre-transition because a guard bailed,
        ///   e.g. pendingTreeDockMerge). Falls back to pre-#266 finalize.</item>
        /// </list>
        /// </summary>
        internal enum LimboDispatchOutcome
        {
            Finalize = 0,
            VesselSwitchRestore = 1,
            QuickloadRestore = 2,
            SafetyNetFinalize = 3,
        }

        /// <summary>
        /// Mirrors the Limbo-dispatch decision in ParsekScenario.OnLoad after the
        /// bug #266 fix. Pure function — keeps the four-way decision tree unit-testable.
        /// </summary>
        internal static LimboDispatchOutcome ComputeLimboDispatch(
            bool isRevert, bool isVesselSwitch, PendingTreeState pendState)
        {
            if (isRevert) return LimboDispatchOutcome.Finalize;
            if (pendState == PendingTreeState.LimboVesselSwitch)
                return LimboDispatchOutcome.VesselSwitchRestore;
            if (isVesselSwitch) return LimboDispatchOutcome.SafetyNetFinalize;
            return LimboDispatchOutcome.QuickloadRestore;
        }

        [Fact]
        public void HasOrphanedLimboTree_LimboStashedButNotRestoredFromSave_IsTrue_Bug300()
        {
            // Revert-to-launch scenario: StashActiveTreeAsPendingLimbo put a tree
            // into Limbo, then TryRestoreActiveTreeNode scans the launch quicksave
            // which has NO active tree → returns false → orphaned.
            var tree = MakeTree("tree_revert", "Kerbal X", 3);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);

            // Launch quicksave: no RECORDING_TREE with isActive=True
            var launchSave = new ConfigNode("PARSEK_SCENARIO");
            bool activeTreeRestoredFromSave = ParsekScenario.TryRestoreActiveTreeNode(launchSave);

            Assert.False(activeTreeRestoredFromSave);
            bool hasOrphanedLimboTree = RecordingStore.HasPendingTree
                && RecordingStore.PendingTreeStateValue == PendingTreeState.Limbo
                && !activeTreeRestoredFromSave;
            Assert.True(hasOrphanedLimboTree);
        }

        [Fact]
        public void HasOrphanedLimboTree_LimboOverwrittenByRestore_IsFalse_Bug300()
        {
            // Quickload (F5/F9) scenario: StashActiveTreeAsPendingLimbo put a tree
            // into Limbo, then TryRestoreActiveTreeNode finds the save-file version
            // (F5 save has isActive=True) → returns true → not orphaned.
            var tree = MakeTree("tree_ql", "Kerbal X", 3);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);

            // F5 save: has RECORDING_TREE with isActive=True
            var f5Save = new ConfigNode("PARSEK_SCENARIO");
            var activeNode = f5Save.AddNode("RECORDING_TREE");
            var saveTree = MakeTree("tree_ql", "Kerbal X (save)", 3);
            saveTree.Save(activeNode);
            activeNode.AddValue("isActive", "True");

            bool activeTreeRestoredFromSave = ParsekScenario.TryRestoreActiveTreeNode(f5Save);

            Assert.True(activeTreeRestoredFromSave);
            bool hasOrphanedLimboTree = RecordingStore.HasPendingTree
                && RecordingStore.PendingTreeStateValue == PendingTreeState.Limbo
                && !activeTreeRestoredFromSave;
            Assert.False(hasOrphanedLimboTree);
        }

        [Fact]
        public void HasOrphanedLimboTree_FinalizedState_IsFalse_Bug300()
        {
            // Finalized trees are committed scene-exit trees, not Limbo. Even when
            // TryRestoreActiveTreeNode returns false, the Limbo state check rejects.
            var tree = MakeTree("tree_fin", "Committed Flight", 2);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);

            var emptySave = new ConfigNode("PARSEK_SCENARIO");
            bool activeTreeRestoredFromSave = ParsekScenario.TryRestoreActiveTreeNode(emptySave);

            Assert.False(activeTreeRestoredFromSave);
            bool hasOrphanedLimboTree = RecordingStore.HasPendingTree
                && RecordingStore.PendingTreeStateValue == PendingTreeState.Limbo
                && !activeTreeRestoredFromSave;
            Assert.False(hasOrphanedLimboTree);
        }

        [Fact]
        public void LimboDispatch_OrphanedLimboTree_RoutesToFinalize_Bug300()
        {
            // End-to-end dispatch: orphaned Limbo tree (revert) → isRevert=true → Finalize
            bool isRevert = ComputeIsRevert(
                isVesselSwitch: false,
                savedEpoch: 0,
                currentEpoch: 0,
                totalSavedRecCount: 0,
                memoryRecordingsCount: 0,
                isFlightToFlight: true,
                hasOrphanedLimboTree: true);
            Assert.True(isRevert);

            var outcome = ComputeLimboDispatch(isRevert, isVesselSwitch: false,
                PendingTreeState.Limbo);
            Assert.Equal(LimboDispatchOutcome.Finalize, outcome);
        }

        [Fact]
        public void LimboDispatch_Revert_Finalizes()
        {
            // Real revert wipes the in-progress mission regardless of state.
            Assert.Equal(LimboDispatchOutcome.Finalize,
                ComputeLimboDispatch(isRevert: true, isVesselSwitch: false,
                    pendState: PendingTreeState.Limbo));
        }

        [Fact]
        public void LimboDispatch_Revert_OverridesLimboVesselSwitch()
        {
            // Even if the stash pre-transitioned for a vessel switch, a real revert
            // (epoch/count regression) takes priority. The pre-#266 behavior is
            // preserved for the revert path.
            Assert.Equal(LimboDispatchOutcome.Finalize,
                ComputeLimboDispatch(isRevert: true, isVesselSwitch: true,
                    pendState: PendingTreeState.LimboVesselSwitch));
        }

        [Fact]
        public void LimboDispatch_VesselSwitch_PreTransitioned_Restores_Bug266()
        {
            // Bug #266: tree was pre-transitioned at stash time
            // (StashActiveTreeForVesselSwitch). OnLoad routes to the vessel-switch
            // restore coroutine instead of finalizing. The mission is preserved
            // across the FLIGHT→FLIGHT scene reload.
            Assert.Equal(LimboDispatchOutcome.VesselSwitchRestore,
                ComputeLimboDispatch(isRevert: false, isVesselSwitch: true,
                    pendState: PendingTreeState.LimboVesselSwitch));
        }

        [Fact]
        public void LimboDispatch_VesselSwitch_NotPreTransitioned_FinalizesViaSafetyNet_Bug266()
        {
            // Safety net: vessel-switch detected at OnLoad time, but the stash did
            // NOT pre-transition (the in-flight pre-transition guard bailed because
            // pendingTreeDockMerge / pendingSplit was active). Fall back to pre-#266
            // finalize behavior — better to lose the tree than to leak a half-
            // transitioned state into the restore path.
            Assert.Equal(LimboDispatchOutcome.SafetyNetFinalize,
                ComputeLimboDispatch(isRevert: false, isVesselSwitch: true,
                    pendState: PendingTreeState.Limbo));
        }

        [Fact]
        public void LimboDispatch_Quickload_DefersToQuickloadRestore()
        {
            // Quickload / cold-start resume: tree should be restored-and-resumed,
            // not finalized.
            Assert.Equal(LimboDispatchOutcome.QuickloadRestore,
                ComputeLimboDispatch(isRevert: false, isVesselSwitch: false,
                    pendState: PendingTreeState.Limbo));
        }

        [Fact]
        public void LimboDispatch_LimboVesselSwitch_WithoutSwitchFlag_StillRestores_Bug266()
        {
            // Cold-start path: the .sfs holds a LimboVesselSwitch tree (player F5'd
            // in outsider state, then quit, then resumed). vesselSwitchPending is
            // false because no live switch happened in this session, but the saved
            // state still needs the vessel-switch restore.
            Assert.Equal(LimboDispatchOutcome.VesselSwitchRestore,
                ComputeLimboDispatch(isRevert: false, isVesselSwitch: false,
                    pendState: PendingTreeState.LimboVesselSwitch));
        }

        // ============================================================
        // Bug #266: TryRestoreActiveTreeNode picks state based on
        // whether the saved tree has an active recording.
        // ============================================================

        [Fact]
        public void TryRestoreActiveTreeNode_TreeWithActiveRecording_StashesAsLimbo_Bug266()
        {
            // Tree has a populated ActiveRecordingId — quickload-resume path.
            var scenarioNode = new ConfigNode("PARSEK_SCENARIO");
            var activeNode = scenarioNode.AddNode("RECORDING_TREE");
            var tree = MakeTree("tree_alive", "Live Recording", 2);
            // MakeTree sets ActiveRecordingId = "root_tree_alive" by default.
            tree.Save(activeNode);
            activeNode.AddValue("isActive", "True");

            ParsekScenario.TryRestoreActiveTreeNode(scenarioNode);

            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
        }

        [Fact]
        public void TryRestoreActiveTreeNode_TreeWithoutActiveRecording_StashesAsLimboVesselSwitch_Bug266()
        {
            // Outsider state: tree was alive at OnSave time but had no active
            // recording (player switched to a vessel with no recording context).
            // Bug #266: stash as LimboVesselSwitch so the restore coroutine
            // doesn't try to name-match a non-existent active vessel.
            var scenarioNode = new ConfigNode("PARSEK_SCENARIO");
            var activeNode = scenarioNode.AddNode("RECORDING_TREE");
            var tree = MakeTree("tree_outsider", "Outsider Hop", 2);
            tree.ActiveRecordingId = null; // outsider state
            tree.Save(activeNode);
            activeNode.AddValue("isActive", "True");

            ParsekScenario.TryRestoreActiveTreeNode(scenarioNode);

            Assert.Equal(PendingTreeState.LimboVesselSwitch, RecordingStore.PendingTreeStateValue);
            Assert.Null(RecordingStore.PendingTree.ActiveRecordingId);
            Assert.Contains(logLines, l => l.Contains("LimboVesselSwitch"));
        }

        // ============================================================
        // Bug #266: pre-transition logic — calls the real production
        // helper ParsekFlight.ApplyPreTransitionForVesselSwitch so the
        // tests stay locked to the actual implementation.
        // ============================================================

        [Fact]
        public void PreTransition_RecorderPidPreferred_MovesActiveToBackgroundMap_Bug266()
        {
            var tree = MakeTree("tree_t", "Launch", 2);
            tree.Recordings["root_tree_t"].VesselPersistentId = 999; // stale
            // recorder PID is the live source of truth
            uint recorderPid = 12345;

            bool moved = ParsekFlight.ApplyPreTransitionForVesselSwitch(tree, recorderPid);

            Assert.True(moved);
            Assert.Null(tree.ActiveRecordingId);
            Assert.True(tree.BackgroundMap.ContainsKey(12345));
            Assert.Equal("root_tree_t", tree.BackgroundMap[12345]);
            // Stale tree-rec PID is NOT used since recorder PID was live
            Assert.False(tree.BackgroundMap.ContainsKey(999));
        }

        [Fact]
        public void PreTransition_FallbackToTreeRecPid_WhenRecorderPidZero_Bug266()
        {
            var tree = MakeTree("tree_t", "Launch", 2);
            tree.Recordings["root_tree_t"].VesselPersistentId = 4242;

            // Recorder PID = 0 (e.g. recorder was already torn down before stash)
            bool moved = ParsekFlight.ApplyPreTransitionForVesselSwitch(tree, 0);

            Assert.True(moved);
            Assert.Null(tree.ActiveRecordingId);
            Assert.True(tree.BackgroundMap.ContainsKey(4242));
            Assert.Equal("root_tree_t", tree.BackgroundMap[4242]);
        }

        [Fact]
        public void PreTransition_NullActiveRec_NullsAndDoesNotMove_Bug266()
        {
            var tree = MakeTree("tree_t", "Launch", 2);
            tree.ActiveRecordingId = null;

            bool moved = ParsekFlight.ApplyPreTransitionForVesselSwitch(tree, 12345);

            Assert.False(moved);
            Assert.Null(tree.ActiveRecordingId);
            Assert.Empty(tree.BackgroundMap);
        }

        [Fact]
        public void PreTransition_BothPidsZero_NullsAndDoesNotMove_Bug266()
        {
            var tree = MakeTree("tree_t", "Launch", 2);
            tree.Recordings["root_tree_t"].VesselPersistentId = 0;

            // No PID source available (degenerate case — recorder gone, tree
            // recording was never populated). Tree is still nulled out, but
            // there's no entry in BackgroundMap. Restore will treat the new
            // active vessel as outsider regardless of who it is.
            bool moved = ParsekFlight.ApplyPreTransitionForVesselSwitch(tree, 0);

            Assert.False(moved);
            Assert.Null(tree.ActiveRecordingId);
            Assert.Empty(tree.BackgroundMap);
        }

        [Fact]
        public void PreTransition_PreservesExistingBackgroundMapEntries_Bug266()
        {
            // Round-trip case: tree already has background entries from prior
            // hops. The new switch should add a new entry, not clear existing ones.
            var tree = MakeTree("tree_t", "Multi-Hop", 3);
            tree.BackgroundMap[5555] = "child_tree_t_1";
            tree.BackgroundMap[6666] = "child_tree_t_2";

            bool moved = ParsekFlight.ApplyPreTransitionForVesselSwitch(tree, 7777);

            Assert.True(moved);
            Assert.Equal(3, tree.BackgroundMap.Count);
            Assert.Equal("child_tree_t_1", tree.BackgroundMap[5555]);
            Assert.Equal("child_tree_t_2", tree.BackgroundMap[6666]);
            Assert.Equal("root_tree_t", tree.BackgroundMap[7777]);
        }

        // ============================================================
        // Test helpers
        // ============================================================

        private static RecordingTree MakeTree(string id, string name, int recordingCount)
        {
            var tree = new RecordingTree
            {
                Id = id,
                TreeName = name,
                RootRecordingId = "root_" + id,
                ActiveRecordingId = "root_" + id,
            };
            for (int i = 0; i < recordingCount; i++)
            {
                string recId = i == 0 ? "root_" + id : $"child_{id}_{i}";
                var rec = new Recording
                {
                    RecordingId = recId,
                    VesselName = $"{name} #{i}",
                    TreeId = id,
                    ExplicitStartUT = 100 + i * 10,
                    ExplicitEndUT = 110 + i * 10,
                };
                rec.Points.Add(new TrajectoryPoint { ut = rec.ExplicitStartUT });
                rec.Points.Add(new TrajectoryPoint { ut = rec.ExplicitEndUT });
                tree.Recordings[recId] = rec;
            }
            return tree;
        }
    }
}
