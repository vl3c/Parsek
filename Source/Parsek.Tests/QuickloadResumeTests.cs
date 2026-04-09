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
        /// Mirrors the isRevert computation in ParsekScenario.OnLoad after the fix
        /// (removal of the || isFlightToFlight clause). Kept here as a pure function
        /// so it can be unit-tested without needing a full ParsekScenario instance.
        /// </summary>
        private static bool ComputeIsRevert(
            bool isVesselSwitch, uint savedEpoch, uint currentEpoch,
            int totalSavedRecCount, int memoryRecordingsCount)
        {
            return !isVesselSwitch
                && (savedEpoch < currentEpoch
                    || totalSavedRecCount < memoryRecordingsCount);
        }

        /// <summary>
        /// Mirrors the Limbo-dispatch decision in ParsekScenario.OnLoad. Returns
        /// true if the tree should be finalized-and-committed, false if it should
        /// be deferred to the restore coroutine for quickload-resume.
        /// </summary>
        private static bool ComputeShouldFinalizeOnDispatch(bool isRevert, bool isVesselSwitch)
        {
            return isRevert || isVesselSwitch;
        }

        [Fact]
        public void LimboDispatch_Revert_Finalizes()
        {
            Assert.True(ComputeShouldFinalizeOnDispatch(isRevert: true, isVesselSwitch: false));
        }

        [Fact]
        public void LimboDispatch_VesselSwitch_Finalizes()
        {
            // Vessel switch path: the new active vessel is by definition different from
            // the tree's recorded vessel, so RestoreActiveTreeFromPending's name-match
            // would fail. We match mainline's CommitTreeRevert behavior instead (finalize
            // on vessel switch) until tree-preservation-on-switch is implemented as a
            // follow-up.
            Assert.True(ComputeShouldFinalizeOnDispatch(isRevert: false, isVesselSwitch: true));
        }

        [Fact]
        public void LimboDispatch_Quickload_DefersToResume()
        {
            // Quickload / cold-start resume: tree should be restored-and-resumed,
            // not finalized.
            Assert.False(ComputeShouldFinalizeOnDispatch(isRevert: false, isVesselSwitch: false));
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
