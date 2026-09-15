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
            // ParsekScenario.CountSavedCommittedRecordingNodes is the production
            // arithmetic OnLoad's revert classification runs on. Re-implementing the
            // loop in the test body (as this cell used to) also got the rule wrong:
            // in-flight and pending marker trees are NOT committed and must not count.
            var scenarioNode = new ConfigNode("SCENARIO");
            scenarioNode.AddNode("RECORDING");
            scenarioNode.AddNode("RECORDING");

            var treeNode = scenarioNode.AddNode("RECORDING_TREE");
            treeNode.AddValue("id", "tree1");
            treeNode.AddValue("treeName", "Test Tree");
            treeNode.AddValue("rootRecordingId", "r1");
            treeNode.AddNode("RECORDING").AddValue("recordingId", "r1");
            treeNode.AddNode("RECORDING").AddValue("recordingId", "r2");

            // In-flight marker tree: its recordings are not committed.
            var activeNode = scenarioNode.AddNode("RECORDING_TREE");
            activeNode.AddValue("id", "tree_active");
            activeNode.AddValue("isActive", "True");
            activeNode.AddNode("RECORDING").AddValue("recordingId", "r3");

            // Finalized pending tree: likewise not committed yet.
            var pendingNode = scenarioNode.AddNode("RECORDING_TREE");
            pendingNode.AddValue("id", "tree_pending");
            pendingNode.AddValue("isPending", "True");
            pendingNode.AddNode("RECORDING").AddValue("recordingId", "r4");

            int savedTreeRecCount;
            int totalSavedRecCount = ParsekScenario.CountSavedCommittedRecordingNodes(
                scenarioNode, out savedTreeRecCount);

            // 2 standalone + 2 committed tree recordings; the marker trees' two
            // recordings are excluded.
            Assert.Equal(2, savedTreeRecCount);
            Assert.Equal(4, totalSavedRecCount);

            // A save with no nodes at all counts zero rather than throwing, and a null
            // node (no SCENARIO written yet) is the same answer.
            Assert.Equal(0, ParsekScenario.CountSavedCommittedRecordingNodes(
                new ConfigNode("SCENARIO"), out savedTreeRecCount));
            Assert.Equal(0, savedTreeRecCount);
            Assert.Equal(0, ParsekScenario.CountSavedCommittedRecordingNodes(
                null, out savedTreeRecCount));
            Assert.Equal(0, savedTreeRecCount);
        }

        #endregion
    }
}
