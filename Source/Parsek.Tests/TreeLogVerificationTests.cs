using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class TreeLogVerificationTests : System.IDisposable
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
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private Recording MakeTreeRecording(string id, string treeId, string vesselName = "Ship")
        {
            return new Recording
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
                line => line.Contains("Stashed pending tree 'Mun Mission' (3 recordings, state=Finalized)"));
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

        // C4: ComputeTotal logging removed — pure computation should not log (log spam fix)

        // ============================================================
        // C5: RecordingTree.Save is intentionally silent (log-spam fix)
        // ============================================================

        [Fact]
        public void RecordingTreeSave_DoesNotLogPerTreeLine()
        {
            var tree = new RecordingTree
            {
                Id = "tree_c5",
                TreeName = "Save Test",
                RootRecordingId = "rec_0"
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

            // Serialization still works...
            Assert.Equal(2, node.GetNodes("RECORDING").Length);
            Assert.Single(node.GetNodes("BRANCH_POINT"));

            // ...but Save no longer emits its own per-tree summary line: it is called
            // once per committed tree inside ParsekScenario.SaveTreeRecordings' loop, which
            // would emit one line per tree (hundreds with a large save). The caller logs a
            // single batched summary; the single-tree active/pending callers log their own
            // Info line.
            Assert.DoesNotContain(capturedLines,
                line => line.Contains("[RecordingTree]") && line.Contains("Save: tree="));
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
                RootRecordingId = "rec_0"
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
