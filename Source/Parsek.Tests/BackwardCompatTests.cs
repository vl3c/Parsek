using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class BackwardCompatTests
    {
        public BackwardCompatTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;
        }

        #region Test 1: Legacy recording fields default to null/zero/NaN

        [Fact]
        public void LegacyRecording_TreeFieldsDefaultToNull()
        {
            var rec = new Recording();

            Assert.Null(rec.TreeId);
            Assert.Null(rec.TerminalStateValue);
            Assert.Null(rec.ChildBranchPointId);
            Assert.Null(rec.ParentBranchPointId);
            Assert.True(double.IsNaN(rec.ExplicitStartUT));
            Assert.True(double.IsNaN(rec.ExplicitEndUT));
            Assert.Null(rec.SurfacePos);
            Assert.Equal(0u, rec.VesselPersistentId);
        }

        #endregion

        // Cleanup 2026-08-29 (RESOURCE-BUDGET-READOUTS-ARE-DEAD): the regions
        // "Test 2: Standalone recording (TreeId null) included in budget" and
        // "Test 3: Chain + tree recordings do not interfere" are gone with
        // ResourceBudget.ComputeTotal - both cells asserted through it.

        #region Test 4: RecordingTree.Load with missing legacy fields defaults safely

        [Fact]
        public void RecordingTree_Load_MissingLegacyFields_DefaultsSafely()
        {
            // Build a minimal RECORDING_TREE ConfigNode with only id and treeName.
            // Load should default TreeFormatVersion to 0 and leave the tree empty.
            var treeNode = new ConfigNode("RECORDING_TREE");
            treeNode.AddValue("id", "legacy-tree");
            treeNode.AddValue("treeName", "Legacy Tree");
            treeNode.AddValue("rootRecordingId", "");

            var tree = RecordingTree.Load(treeNode);

            Assert.Equal("legacy-tree", tree.Id);
            Assert.Equal("Legacy Tree", tree.TreeName);
            Assert.Equal(0, tree.TreeFormatVersion);
            Assert.Null(tree.ActiveRecordingId);
            Assert.Empty(tree.Recordings);
            Assert.Empty(tree.BranchPoints);
        }

        #endregion

        #region Test 5: Revert detection counting with tree recordings

        [Fact]
        public void RevertDetection_TreeRecordingsCounted_InTotalSavedRecCount()
        {
            // Simulate the counting logic from ParsekScenario.OnLoad lines 235-242:
            //   savedTreeRecCount = sum of RECORDING nodes inside each RECORDING_TREE
            //   totalSavedRecCount = savedRecNodes.Length + savedTreeRecCount

            // Build a scenario ConfigNode with 2 standalone RECORDING nodes
            var scenarioNode = new ConfigNode("SCENARIO");
            scenarioNode.AddNode("RECORDING");
            scenarioNode.AddNode("RECORDING");

            // Build 1 RECORDING_TREE node containing 2 RECORDING child nodes
            var treeNode = scenarioNode.AddNode("RECORDING_TREE");
            treeNode.AddValue("id", "tree1");
            treeNode.AddValue("treeName", "Test Tree");
            treeNode.AddValue("rootRecordingId", "r1");
            var recNode1 = treeNode.AddNode("RECORDING");
            recNode1.AddValue("recordingId", "r1");
            recNode1.AddValue("vesselName", "Ship 1");
            var recNode2 = treeNode.AddNode("RECORDING");
            recNode2.AddValue("recordingId", "r2");
            recNode2.AddValue("vesselName", "Ship 2");

            // Replicate the counting logic from ParsekScenario.OnLoad
            ConfigNode[] savedRecNodes = scenarioNode.GetNodes("RECORDING");
            ConfigNode[] savedTreeNodes = scenarioNode.GetNodes("RECORDING_TREE");
            int savedTreeRecCount = 0;
            for (int t = 0; t < savedTreeNodes.Length; t++)
                savedTreeRecCount += savedTreeNodes[t].GetNodes("RECORDING").Length;
            int totalSavedRecCount = savedRecNodes.Length + savedTreeRecCount;

            // 2 standalone + 2 tree recordings = 4 total
            Assert.Equal(2, savedRecNodes.Length);
            Assert.Single(savedTreeNodes);
            Assert.Equal(2, savedTreeRecCount);
            Assert.Equal(4, totalSavedRecCount);
        }

        #endregion
    }
}
