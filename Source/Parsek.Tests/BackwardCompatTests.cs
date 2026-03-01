using System.Collections.Generic;
using UnityEngine;
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
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;
        }

        /// <summary>
        /// Helper: creates a recording with trajectory points and PreLaunchFunds set.
        /// TreeId is left null (standalone/legacy recording).
        /// </summary>
        private RecordingStore.Recording MakeStandaloneRecording(
            double preLaunchFunds, double endFunds)
        {
            var rec = new RecordingStore.Recording
            {
                PreLaunchFunds = preLaunchFunds
            };

            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100,
                funds = preLaunchFunds - 5000,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            });

            rec.Points.Add(new TrajectoryPoint
            {
                ut = 200,
                funds = endFunds,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            });

            return rec;
        }

        #region Test 1: Legacy recording fields default to null/zero/NaN

        [Fact]
        public void LegacyRecording_TreeFieldsDefaultToNull()
        {
            var rec = new RecordingStore.Recording();

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

        #region Test 2: Standalone recording (TreeId null) included in budget

        [Fact]
        public void ComputeTotal_StandaloneRecording_TreeIdNull_IncludedInBudget()
        {
            // Standalone recording: preLaunch=50000, end=35000 -> cost=15000
            var rec = MakeStandaloneRecording(50000, 35000);
            Assert.Null(rec.TreeId); // verify it is standalone

            var recordings = new List<RecordingStore.Recording> { rec };
            var budget = ResourceBudget.ComputeTotal(recordings, new List<Milestone>(), null);

            Assert.Equal(15000, budget.reservedFunds);
        }

        #endregion

        #region Test 3: Chain + tree recordings do not interfere

        [Fact]
        public void ComputeTotal_ChainAndTree_NoInterference()
        {
            // Chain recording 1: standalone chain segment (TreeId null, ChainId set)
            // preLaunch=50000, end=45000 -> cost=5000
            var chain1 = MakeStandaloneRecording(50000, 45000);
            chain1.ChainId = "chain-abc";
            chain1.ChainIndex = 0;
            Assert.Null(chain1.TreeId);

            // Chain recording 2: another chain segment
            // preLaunch=45000, end=42000 -> cost=3000
            var chain2 = MakeStandaloneRecording(45000, 42000);
            chain2.ChainId = "chain-abc";
            chain2.ChainIndex = 1;
            Assert.Null(chain2.TreeId);

            // Tree recording: has TreeId set, should be excluded from per-recording sum
            // preLaunch=60000, end=50000 -> would be cost=10000, but skipped
            var treeRec = MakeStandaloneRecording(60000, 50000);
            treeRec.TreeId = "tree-xyz";

            // Corresponding RecordingTree with negative DeltaFunds (spent 2000)
            var tree = new RecordingTree
            {
                Id = "tree-xyz",
                DeltaFunds = -2000,
                ResourcesApplied = false
            };

            var recordings = new List<RecordingStore.Recording> { chain1, chain2, treeRec };
            var trees = new List<RecordingTree> { tree };
            var budget = ResourceBudget.ComputeTotal(recordings, new List<Milestone>(), trees);

            // chain1 cost (5000) + chain2 cost (3000) + tree delta cost (2000) = 10000
            // treeRec's per-recording cost (10000) is NOT included because TreeId != null
            Assert.Equal(10000, budget.reservedFunds);
        }

        #endregion

        #region Test 4: RecordingTree.Load with missing fields defaults safely

        [Fact]
        public void RecordingTree_Load_MissingFields_DefaultsSafely()
        {
            // Build a minimal RECORDING_TREE ConfigNode with only id and treeName.
            // All resource fields, resourcesApplied, etc. are absent.
            var treeNode = new ConfigNode("RECORDING_TREE");
            treeNode.AddValue("id", "legacy-tree");
            treeNode.AddValue("treeName", "Legacy Tree");
            treeNode.AddValue("rootRecordingId", "");
            // No resourcesApplied, no deltaFunds, no deltaScience, no deltaRep,
            // no preTreeFunds, no preTreeScience, no preTreeRep

            var tree = RecordingTree.Load(treeNode);

            Assert.Equal("legacy-tree", tree.Id);
            Assert.Equal("Legacy Tree", tree.TreeName);
            Assert.False(tree.ResourcesApplied);
            Assert.Equal(0, tree.DeltaFunds);
            Assert.Equal(0, tree.DeltaScience);
            Assert.Equal(0f, tree.DeltaReputation);
            Assert.Equal(0, tree.PreTreeFunds);
            Assert.Equal(0, tree.PreTreeScience);
            Assert.Equal(0f, tree.PreTreeReputation);
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
