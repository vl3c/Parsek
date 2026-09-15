using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class SaveContaminationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SaveContaminationTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        // --- IsSaveFolderMismatch tests ---

        [Fact]
        public void IsSaveFolderMismatch_DifferentFolders_ReturnsTrue()
        {
            Assert.True(ParsekScenario.IsSaveFolderMismatch("saveA", "saveB"));
        }

        [Fact]
        public void IsSaveFolderMismatch_SameFolders_ReturnsFalse()
        {
            Assert.False(ParsekScenario.IsSaveFolderMismatch("saveA", "saveA"));
        }

        [Fact]
        public void IsSaveFolderMismatch_NullScenarioFolder_ReturnsFalse()
        {
            Assert.False(ParsekScenario.IsSaveFolderMismatch(null, "saveB"));
        }

        [Fact]
        public void IsSaveFolderMismatch_EmptyScenarioFolder_ReturnsFalse()
        {
            Assert.False(ParsekScenario.IsSaveFolderMismatch("", "saveB"));
        }

        [Fact]
        public void IsSaveFolderMismatch_BothNull_ReturnsFalse()
        {
            Assert.False(ParsekScenario.IsSaveFolderMismatch(null, null));
        }

        // --- CommittedTrees contamination scenario ---

        [Fact]
        public void CommittedTrees_SurvivesRecordingsClear_DemonstratesBugScenario()
        {
            // Simulate save A: commit a tree (adds to both CommittedTrees and CommittedRecordings)
            var tree = MakeSimpleTree("tree_saveA");
            RecordingStore.CommitTree(tree);

            Assert.Single(RecordingStore.CommittedTrees);
            Assert.Single(RecordingStore.CommittedRecordings);

            // Simulate initial load of save B: only CommittedRecordings.Clear() runs.
            // This is what the old code did when save B had no trees.
            RecordingStore.ClearCommittedInternal();

            // Bug: CommittedTrees still has save A's tree
            Assert.Single(RecordingStore.CommittedTrees);
            Assert.Equal("tree_saveA", RecordingStore.CommittedTrees[0].Id);
        }

        [Fact]
        public void CommittedTrees_InitialLoadOfATreelessSave_RemovesStaleTrees()
        {
            // The contamination this guards is cross-save: save A's trees must not
            // survive into save B when save B's SCENARIO node carries no
            // RECORDING_TREE at all. Calling CommittedTrees.Clear() in the test body
            // proved only that List.Clear works; the clear that matters is the one
            // inside the real load path, so this drives it.
            var tree = MakeSimpleTree("tree_saveA");
            RecordingStore.CommitTree(tree);
            Assert.Single(RecordingStore.CommittedTrees);
            Assert.Single(RecordingStore.CommittedRecordings);

            // OnLoad's cold-load order for save B: clear the committed recordings,
            // then hand the new save's node to the tree loader. The node has no
            // RECORDING_TREE children - the exact shape that used to leak.
            RecordingStore.ClearCommittedInternal();
            ParsekScenario.LoadRecordingTrees(
                new ConfigNode("SCENARIO"), new List<Recording>());

            Assert.Empty(RecordingStore.CommittedTrees);
            Assert.Empty(RecordingStore.CommittedRecordings);
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") &&
                l.Contains("OnLoad initial: cleared CommittedTrees, loading 0 tree(s)"));
        }

        [Fact]
        public void DiscardPendingTree_ClearsStalePendingTree()
        {
            var tree = MakeSimpleTree("pending_tree");
            RecordingStore.StashPendingTree(tree);
            Assert.True(RecordingStore.HasPendingTree);

            RecordingStore.SuppressLogging = false;
            RecordingStore.DiscardPendingTree();

            Assert.False(RecordingStore.HasPendingTree);
        }

        [Fact]
        public void CreateRecordingFromFlightData_DoesNotStorePending()
        {
            // Standalone pending no longer exists. CreateRecordingFromFlightData
            // returns a recording without storing it in any pending slot.
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100 },
                new TrajectoryPoint { ut = 200 }
            };
            var rec = RecordingStore.CreateRecordingFromFlightData(points, "TestRec");

            Assert.NotNull(rec);
            Assert.Equal("TestRec", rec.VesselName);
        }

        // --- Helpers ---

        private RecordingTree MakeSimpleTree(string treeId)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Test Tree",
                RootRecordingId = "root",
                ActiveRecordingId = "root"
            };

            tree.Recordings["root"] = new Recording
            {
                RecordingId = "root",
                TreeId = treeId,
                VesselName = "Root Vessel",
                VesselPersistentId = 1000
            };

            return tree;
        }
    }
}
