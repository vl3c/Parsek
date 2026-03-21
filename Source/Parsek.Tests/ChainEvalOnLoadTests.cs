using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests the chain evaluation logic used by ParsekFlight.EvaluateAndApplyGhostChains.
    /// Verifies ComputeAllGhostChains produces correct chains and that the UT-based
    /// ghost/skip decision is correct. Cannot test KSP integration (requires running game),
    /// but validates the pure data flow that drives ghosting decisions on scene load.
    /// </summary>
    [Collection("Sequential")]
    public class ChainEvalOnLoadTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ChainEvalOnLoadTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        #region Helpers

        static RecordingTree MakeTree(string treeId, Recording[] recordings,
            BranchPoint[] branchPoints)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                RootRecordingId = recordings.Length > 0 ? recordings[0].RecordingId : ""
            };

            for (int i = 0; i < recordings.Length; i++)
            {
                recordings[i].TreeId = treeId;
                tree.Recordings[recordings[i].RecordingId] = recordings[i];
            }

            if (branchPoints != null)
            {
                for (int i = 0; i < branchPoints.Length; i++)
                    tree.BranchPoints.Add(branchPoints[i]);
            }

            return tree;
        }

        static Recording MakeRecording(string id, uint vesselPid,
            double startUT, double endUT,
            TerminalState? terminal = null,
            string parentBpId = null, string childBpId = null)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselPersistentId = vesselPid,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                TerminalStateValue = terminal,
                ParentBranchPointId = parentBpId,
                ChildBranchPointId = childBpId,
                Points = new List<TrajectoryPoint>()
            };
            return rec;
        }

        static BranchPoint MakeBranchPoint(string id, BranchPointType type,
            double ut, uint targetVesselPid,
            string[] parentRecIds, string[] childRecIds)
        {
            var bp = new BranchPoint
            {
                Id = id,
                Type = type,
                UT = ut,
                TargetVesselPersistentId = targetVesselPid,
                ParentRecordingIds = new List<string>(parentRecIds),
                ChildRecordingIds = new List<string>(childRecIds)
            };
            return bp;
        }

        /// <summary>
        /// Simulates the UT-based ghost skip decision from EvaluateAndApplyGhostChains:
        /// returns true if the vessel should be ghosted (currentUT is before SpawnUT).
        /// This is the pure decision extracted from ParsekFlight for testability.
        /// </summary>
        static bool ShouldGhostAtUT(GhostChain chain, double currentUT)
        {
            return currentUT < chain.SpawnUT;
        }

        #endregion

        #region NoCommittedTrees

        /// <summary>
        /// With no committed trees, ComputeAllGhostChains returns empty dict.
        /// EvaluateAndApplyGhostChains would set activeGhostChains=null.
        /// </summary>
        [Fact]
        public void NoCommittedTrees_ProducesNullChains()
        {
            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree>(), 1000);

            Assert.NotNull(chains);
            Assert.Empty(chains);
        }

        #endregion

        #region SingleChain

        /// <summary>
        /// Single tree with Dock BP claiming vessel PID=100.
        /// ComputeAllGhostChains produces one chain keyed by PID=100.
        /// </summary>
        [Fact]
        public void SingleChain_ProducesCorrectVesselPid()
        {
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock");

            var dockBp = MakeBranchPoint("bp-dock", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });

            var tree = MakeTree("tree-1", new[] { r1, r1Leaf },
                new[] { dockBp });

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.Single(chains);
            Assert.True(chains.ContainsKey(100));
            Assert.Equal((uint)100, chains[100].OriginalVesselPid);
            Assert.Equal(1120.0, chains[100].SpawnUT);
        }

        #endregion

        #region MultipleChains

        /// <summary>
        /// Two independent docking events claim vessels PID=100 and PID=200.
        /// Both chains are detected, neither merges with the other.
        /// </summary>
        [Fact]
        public void MultipleChains_AllDetected()
        {
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock1");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock1");
            var dock1 = MakeBranchPoint("bp-dock1", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });

            var r2 = MakeRecording("R2", 70, 1000, 1060, childBpId: "bp-dock2");
            var r2Leaf = MakeRecording("R2-leaf", 200, 1060, 1120,
                parentBpId: "bp-dock2");
            var dock2 = MakeBranchPoint("bp-dock2", BranchPointType.Dock,
                1060, 200, new[] { "R2" }, new[] { "R2-leaf" });

            var tree = MakeTree("tree-1",
                new[] { r1, r1Leaf, r2, r2Leaf },
                new[] { dock1, dock2 });

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.Equal(2, chains.Count);
            Assert.True(chains.ContainsKey(100));
            Assert.True(chains.ContainsKey(200));
        }

        #endregion

        #region UT-based ghost decision

        /// <summary>
        /// When currentUT >= chain.SpawnUT, the chain tip is in the past — the vessel
        /// has already been spawned. EvaluateAndApplyGhostChains skips ghosting.
        /// </summary>
        [Fact]
        public void ChainWithSpawnUTInPast_SkippedForGhosting()
        {
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock");
            var dockBp = MakeBranchPoint("bp-dock", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });
            var tree = MakeTree("tree-1", new[] { r1, r1Leaf },
                new[] { dockBp });

            // currentUT=1200 is AFTER chain tip's SpawnUT=1120
            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 1200);

            Assert.Single(chains);
            var chain = chains[100];
            Assert.Equal(1120.0, chain.SpawnUT);

            // The UT-based decision: currentUT >= SpawnUT means DON'T ghost
            Assert.False(ShouldGhostAtUT(chain, 1200));
            Assert.False(ShouldGhostAtUT(chain, 1120)); // exact boundary: spawn has happened
        }

        /// <summary>
        /// When currentUT is before chain.SpawnUT, the chain tip is still in the future —
        /// the vessel should be ghosted. EvaluateAndApplyGhostChains calls GhostVessel.
        /// </summary>
        [Fact]
        public void ChainWithSpawnUTInFuture_MarkedForGhosting()
        {
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock");
            var dockBp = MakeBranchPoint("bp-dock", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });
            var tree = MakeTree("tree-1", new[] { r1, r1Leaf },
                new[] { dockBp });

            // currentUT=900 is BEFORE chain tip's SpawnUT=1120
            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.Single(chains);
            var chain = chains[100];
            Assert.Equal(1120.0, chain.SpawnUT);

            // The UT-based decision: currentUT < SpawnUT means DO ghost
            Assert.True(ShouldGhostAtUT(chain, 900));
            Assert.True(ShouldGhostAtUT(chain, 1119.9)); // just before spawn
        }

        /// <summary>
        /// Mixed scenario: two chains, one with SpawnUT in the past and one in the future.
        /// Only the future chain should be ghosted.
        /// </summary>
        [Fact]
        public void MixedChains_OnlyFutureOnesGhosted()
        {
            // Chain 1: SpawnUT=1120 (past if currentUT=1200)
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock1");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock1");
            var dock1 = MakeBranchPoint("bp-dock1", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });

            // Chain 2: SpawnUT=2120 (future if currentUT=1200)
            var r2 = MakeRecording("R2", 70, 2000, 2060, childBpId: "bp-dock2");
            var r2Leaf = MakeRecording("R2-leaf", 200, 2060, 2120,
                parentBpId: "bp-dock2");
            var dock2 = MakeBranchPoint("bp-dock2", BranchPointType.Dock,
                2060, 200, new[] { "R2" }, new[] { "R2-leaf" });

            var tree = MakeTree("tree-1",
                new[] { r1, r1Leaf, r2, r2Leaf },
                new[] { dock1, dock2 });

            double currentUT = 1200;
            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, currentUT);

            Assert.Equal(2, chains.Count);

            // Chain for PID=100: SpawnUT=1120, currentUT=1200 -> skip
            Assert.False(ShouldGhostAtUT(chains[100], currentUT));

            // Chain for PID=200: SpawnUT=2120, currentUT=1200 -> ghost
            Assert.True(ShouldGhostAtUT(chains[200], currentUT));
        }

        #endregion

        #region Log assertions

        /// <summary>
        /// When no committed trees exist, the log should contain the "no committed trees" message.
        /// This verifies the early-exit path in EvaluateAndApplyGhostChains.
        /// </summary>
        [Fact]
        public void NoTrees_LogsNoCommittedTrees()
        {
            logLines.Clear();

            GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree>(), 1000);

            Assert.Contains(logLines, l =>
                l.Contains("[ChainWalker]") && l.Contains("No committed trees"));
        }

        /// <summary>
        /// When chains are found, the log should contain the "Chain built" message
        /// with the vessel PID and link count.
        /// </summary>
        [Fact]
        public void ChainsFound_LogsChainBuilt()
        {
            logLines.Clear();

            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock");
            var dockBp = MakeBranchPoint("bp-dock", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });
            var tree = MakeTree("tree-1", new[] { r1, r1Leaf },
                new[] { dockBp });

            GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.Contains(logLines, l =>
                l.Contains("[ChainWalker]") && l.Contains("Chain built")
                && l.Contains("vessel=100"));
        }

        #endregion
    }
}
