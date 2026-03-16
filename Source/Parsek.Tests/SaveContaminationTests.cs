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
            MilestoneStore.SuppressLogging = true;
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
            RecordingStore.CommittedRecordings.Clear();

            // Bug: CommittedTrees still has save A's tree
            Assert.Single(RecordingStore.CommittedTrees);
            Assert.Equal("tree_saveA", RecordingStore.CommittedTrees[0].Id);
        }

        [Fact]
        public void CommittedTrees_ExplicitClear_RemovesStaleTreesRegardlessOfNewTreeCount()
        {
            // Simulate save A: commit a tree
            var tree = MakeSimpleTree("tree_saveA");
            RecordingStore.CommitTree(tree);
            Assert.Single(RecordingStore.CommittedTrees);

            // Simulate the fix: always clear both lists on initial load
            RecordingStore.CommittedRecordings.Clear();
            RecordingStore.CommittedTrees.Clear();

            // Now save B has no trees — both lists are empty
            Assert.Empty(RecordingStore.CommittedTrees);
            Assert.Empty(RecordingStore.CommittedRecordings);
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
        public void DiscardPending_ClearsStalePendingRecording()
        {
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100 },
                new TrajectoryPoint { ut = 200 }
            };
            RecordingStore.StashPending(points, "StalePending");
            Assert.True(RecordingStore.HasPending);

            RecordingStore.DiscardPending();

            Assert.False(RecordingStore.HasPending);
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
