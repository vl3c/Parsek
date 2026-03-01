using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class TreeLogVerificationTests
    {
        private readonly List<string> capturedLines = new List<string>();

        public TreeLogVerificationTests()
        {
            // Reset all shared state
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();

            // Enable logging through the test sink
            RecordingStore.SuppressLogging = false;
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => capturedLines.Add(line);

            // Suppress side effects that would crash outside Unity
            GameStateStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
        }

        private RecordingStore.Recording MakeTreeRecording(string id, string treeId, string vesselName = "Ship")
        {
            return new RecordingStore.Recording
            {
                RecordingId = id,
                TreeId = treeId,
                VesselName = vesselName,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0
            };
        }

        private RecordingTree MakeTree(string treeId, string treeName, int recordingCount)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeName,
                RootRecordingId = "rec_0"
            };
            for (int i = 0; i < recordingCount; i++)
            {
                string recId = $"rec_{i}";
                tree.Recordings[recId] = MakeTreeRecording(recId, treeId, $"Ship {i}");
            }
            return tree;
        }

        // ============================================================
        // C1: CommitTree logs tree name and recording count
        // ============================================================

        [Fact]
        public void CommitTree_LogsTreeNameAndRecordingCount()
        {
            var tree = MakeTree("tree_c1", "Mun Mission", 3);

            RecordingStore.CommitTree(tree);

            Assert.Contains(capturedLines,
                line => line.Contains("Committed tree 'Mun Mission' (3 recordings)"));
        }

        // ============================================================
        // C2: StashPendingTree logs tree name
        // ============================================================

        [Fact]
        public void StashPendingTree_LogsTreeNameAndRecordingCount()
        {
            var tree = MakeTree("tree_c2", "Mun Mission", 3);

            RecordingStore.StashPendingTree(tree);

            Assert.Contains(capturedLines,
                line => line.Contains("Stashed pending tree 'Mun Mission' (3 recordings)"));
        }

        // ============================================================
        // C3: DiscardPendingTree logs tree name
        // ============================================================

        [Fact]
        public void DiscardPendingTree_LogsTreeName()
        {
            var tree = MakeTree("tree_c3", "Mun Mission", 3);
            RecordingStore.StashPendingTree(tree);
            capturedLines.Clear();

            RecordingStore.DiscardPendingTree();

            Assert.Contains(capturedLines,
                line => line.Contains("Discarded pending tree 'Mun Mission'"));
        }

        // ============================================================
        // C4: ComputeTotal tree loop — per-tree verbose log
        // ============================================================

        [Fact]
        public void ComputeTotal_LogsPerTreeBreakdown()
        {
            var treeA = new RecordingTree
            {
                Id = "treeA",
                TreeName = "Applied Tree",
                DeltaFunds = -3000,
                ResourcesApplied = true
            };
            var treeB = new RecordingTree
            {
                Id = "treeB",
                TreeName = "Pending Tree",
                DeltaFunds = -2000,
                ResourcesApplied = false
            };

            var trees = new List<RecordingTree> { treeA, treeB };

            ResourceBudget.ComputeTotal(
                new List<RecordingStore.Recording>(),
                new List<Milestone>(),
                trees);

            // Tree A logged as applied (funds=0 since TreeCommittedFundsCost returns 0 for applied)
            Assert.Contains(capturedLines,
                line => line.Contains("Applied Tree") && line.Contains("resourcesApplied=True"));

            // Tree B logged as not applied with cost contribution
            Assert.Contains(capturedLines,
                line => line.Contains("Pending Tree") && line.Contains("resourcesApplied=False"));
        }

        // ============================================================
        // C5: RecordingTree.Save logs summary
        // ============================================================

        [Fact]
        public void RecordingTreeSave_LogsSummary()
        {
            var tree = new RecordingTree
            {
                Id = "tree_c5",
                TreeName = "Save Test",
                RootRecordingId = "rec_0",
                ResourcesApplied = true
            };

            tree.Recordings["rec_0"] = MakeTreeRecording("rec_0", "tree_c5");
            tree.Recordings["rec_1"] = MakeTreeRecording("rec_1", "tree_c5");
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp1",
                UT = 150.0,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "rec_0" },
                ChildRecordingIds = new List<string> { "rec_1" }
            });

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);

            Assert.Contains(capturedLines,
                line => line.Contains("Save Test")
                     && line.Contains("recordings=2")
                     && line.Contains("branchPoints=1")
                     && line.Contains("resourcesApplied=True"));
        }

        // ============================================================
        // C6: RecordingTree.Load logs summary
        // ============================================================

        [Fact]
        public void RecordingTreeLoad_LogsSummary()
        {
            var tree = new RecordingTree
            {
                Id = "tree_c6",
                TreeName = "Load Test",
                RootRecordingId = "rec_0",
                ResourcesApplied = false
            };
            tree.Recordings["rec_0"] = MakeTreeRecording("rec_0", "tree_c6");
            tree.Recordings["rec_1"] = MakeTreeRecording("rec_1", "tree_c6");
            tree.Recordings["rec_2"] = MakeTreeRecording("rec_2", "tree_c6");

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);

            capturedLines.Clear();

            RecordingTree.Load(node);

            Assert.Contains(capturedLines,
                line => line.Contains("tree_c6")
                     && line.Contains("recordings=3"));
        }
    }
}
