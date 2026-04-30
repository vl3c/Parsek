using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests that ghost chains are correctly re-derived from committed recordings
    /// on every load. Chains are derived data (from committed trees + currentUT),
    /// never persisted. Re-running ComputeAllGhostChains with the same input must
    /// produce the same output.
    ///
    /// These tests verify the save/load chain re-evaluation invariants from
    /// Task 6b-5 of the recording system extension plan.
    /// </summary>
    [Collection("Sequential")]
    public class ChainSaveLoadTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ChainSaveLoadTests()
        {
            RecordingStore.SuppressLogging = true;
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
        /// Builds a standard single-chain scenario: vessel A (PID=50) docks to
        /// station S (PID=100) at UT=1060, combined vessel recorded until UT=1120.
        /// </summary>
        static (List<RecordingTree> trees, uint claimedPid) BuildSingleChainScenario()
        {
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock");

            var dockBp = MakeBranchPoint("bp-dock", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });

            var tree = MakeTree("tree-1", new[] { r1, r1Leaf },
                new[] { dockBp });

            return (new List<RecordingTree> { tree }, 100);
        }

        /// <summary>
        /// Builds a longer chain: recording from UT=100 through docking at UT=500,
        /// combined vessel until UT=2000. Useful for mid-chain UT testing.
        /// </summary>
        static (List<RecordingTree> trees, uint claimedPid) BuildLongChainScenario()
        {
            var r1 = MakeRecording("R1", 50, 100, 500, childBpId: "bp-dock");
            var r1Leaf = MakeRecording("R1-leaf", 100, 500, 2000,
                parentBpId: "bp-dock");

            var dockBp = MakeBranchPoint("bp-dock", BranchPointType.Dock,
                500, 100, new[] { "R1" }, new[] { "R1-leaf" });

            var tree = MakeTree("tree-long", new[] { r1, r1Leaf },
                new[] { dockBp });

            return (new List<RecordingTree> { tree }, 100);
        }

        #endregion

        #region Determinism — re-derivation produces identical results

        /// <summary>
        /// Calling ComputeAllGhostChains twice with the same input produces
        /// identical output: same chain count, same vessel PIDs, same tip
        /// recording IDs, same SpawnUT values.
        /// Guards: re-derivation is deterministic (no random/time-dependent state).
        /// </summary>
        [Fact]
        public void ChainsDerivedFromCommittedTrees_Deterministic()
        {
            var (trees, claimedPid) = BuildSingleChainScenario();
            double rewindUT = 900;

            var chains1 = GhostChainWalker.ComputeAllGhostChains(trees, rewindUT);
            var chains2 = GhostChainWalker.ComputeAllGhostChains(trees, rewindUT);

            // Same number of chains
            Assert.Equal(chains1.Count, chains2.Count);

            // Same vessel PIDs
            foreach (var pid in chains1.Keys)
                Assert.True(chains2.ContainsKey(pid),
                    $"Second computation missing chain for PID={pid}");

            // Same tip and SpawnUT for each chain
            foreach (var kvp in chains1)
            {
                var c1 = kvp.Value;
                var c2 = chains2[kvp.Key];
                Assert.Equal(c1.OriginalVesselPid, c2.OriginalVesselPid);
                Assert.Equal(c1.TipRecordingId, c2.TipRecordingId);
                Assert.Equal(c1.SpawnUT, c2.SpawnUT);
                Assert.Equal(c1.Links.Count, c2.Links.Count);
                Assert.Equal(c1.IsTerminated, c2.IsTerminated);
                Assert.Equal(c1.GhostStartUT, c2.GhostStartUT);
            }
        }

        /// <summary>
        /// Determinism with a more complex scenario: two independent chains
        /// from two separate trees. Both computations must match exactly.
        /// Guards: multi-chain determinism.
        /// </summary>
        [Fact]
        public void DeterministicWithMultipleChains()
        {
            // Tree 1: dock to PID=100
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock1");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock1");
            var dock1 = MakeBranchPoint("bp-dock1", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });
            var tree1 = MakeTree("tree-1", new[] { r1, r1Leaf },
                new[] { dock1 });

            // Tree 2: dock to PID=200
            var r2 = MakeRecording("R2", 70, 2000, 2060, childBpId: "bp-dock2");
            var r2Leaf = MakeRecording("R2-leaf", 200, 2060, 2120,
                parentBpId: "bp-dock2");
            var dock2 = MakeBranchPoint("bp-dock2", BranchPointType.Dock,
                2060, 200, new[] { "R2" }, new[] { "R2-leaf" });
            var tree2 = MakeTree("tree-2", new[] { r2, r2Leaf },
                new[] { dock2 });

            var trees = new List<RecordingTree> { tree1, tree2 };

            var chains1 = GhostChainWalker.ComputeAllGhostChains(trees, 900);
            var chains2 = GhostChainWalker.ComputeAllGhostChains(trees, 900);

            Assert.Equal(chains1.Count, chains2.Count);
            foreach (var kvp in chains1)
            {
                Assert.True(chains2.ContainsKey(kvp.Key));
                Assert.Equal(kvp.Value.TipRecordingId, chains2[kvp.Key].TipRecordingId);
                Assert.Equal(kvp.Value.SpawnUT, chains2[kvp.Key].SpawnUT);
                Assert.Equal(kvp.Value.Links.Count, chains2[kvp.Key].Links.Count);
            }
        }

        #endregion

        #region Empty state

        /// <summary>
        /// Empty committed tree list produces no chains. This is the state
        /// after a fresh save with no committed recordings.
        /// Guards: empty state path works.
        /// </summary>
        [Fact]
        public void NoCommittedTrees_NoChainsOnLoad()
        {
            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree>(), 1000);

            Assert.NotNull(chains);
            Assert.Empty(chains);
        }

        /// <summary>
        /// Null committed tree list also produces no chains (defensive).
        /// Guards: null safety.
        /// </summary>
        [Fact]
        public void NullCommittedTrees_NoChainsOnLoad()
        {
            var chains = GhostChainWalker.ComputeAllGhostChains(null, 1000);

            Assert.NotNull(chains);
            Assert.Empty(chains);
        }

        /// <summary>
        /// Committed trees that contain no claiming interactions produce no chains.
        /// Guards: trees without docking/undocking/etc. don't create spurious chains.
        /// </summary>
        [Fact]
        public void CommittedTreesWithoutClaims_NoChainsOnLoad()
        {
            var r1 = MakeRecording("R1", 50, 1000, 1120);
            var launchBp = MakeBranchPoint("bp-launch", BranchPointType.Launch,
                1000, 0, new string[0], new[] { "R1" });
            var tree = MakeTree("tree-1", new[] { r1 }, new[] { launchBp });

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.Empty(chains);
        }

        #endregion

        #region UT-dependent chain activity

        /// <summary>
        /// Same committed tree evaluated at different UTs produces different
        /// ghosting decisions. At UT=500, chain is active (500 < SpawnUT=1120).
        /// At UT=1500, chain still exists but SpawnUT is in the past.
        /// Guards: UT-dependent chain activity works correctly.
        /// </summary>
        [Fact]
        public void ChainsDerivedAtDifferentUTs_ProduceDifferentResults()
        {
            var (trees, claimedPid) = BuildSingleChainScenario();
            // Chain: dock at UT=1060, tip EndUT=1120

            // Compute at UT=500 — before the chain tip's SpawnUT=1120
            var chainsEarly = GhostChainWalker.ComputeAllGhostChains(trees, 500);
            Assert.Single(chainsEarly);
            var chainEarly = chainsEarly[claimedPid];
            Assert.Equal(1120.0, chainEarly.SpawnUT);
            // currentUT=500 < SpawnUT=1120 → vessel should be ghosted
            Assert.True(500.0 < chainEarly.SpawnUT);

            // Compute at UT=1500 — after the chain tip's SpawnUT=1120
            var chainsLate = GhostChainWalker.ComputeAllGhostChains(trees, 1500);
            Assert.Single(chainsLate);
            var chainLate = chainsLate[claimedPid];
            Assert.Equal(1120.0, chainLate.SpawnUT);
            // currentUT=1500 >= SpawnUT=1120 → vessel should NOT be ghosted
            Assert.False(1500.0 < chainLate.SpawnUT);

            // GhostStartUT is the earliest link UT (dock at 1060), independent of rewindUT
            Assert.Equal(1060.0, chainEarly.GhostStartUT);
            Assert.Equal(1060.0, chainLate.GhostStartUT);
        }

        #endregion

        #region Mid-chain UT

        /// <summary>
        /// Chain spans UT=100 to UT=2000 (dock at 500, tip EndUT=2000).
        /// Computing at UT=1000 (mid-chain) produces an active chain
        /// because SpawnUT=2000 > currentUT=1000.
        /// Guards: mid-chain save/load produces correct active chain.
        /// </summary>
        [Fact]
        public void MidChainUT_ChainActive()
        {
            var (trees, claimedPid) = BuildLongChainScenario();
            // Chain: dock at UT=500, tip EndUT=2000

            double midUT = 1000;
            var chains = GhostChainWalker.ComputeAllGhostChains(trees, midUT);

            Assert.Single(chains);
            var chain = chains[claimedPid];
            Assert.Equal(2000.0, chain.SpawnUT);
            Assert.Equal(500.0, chain.GhostStartUT); // earliest link UT (dock at 500)

            // Mid-chain: currentUT=1000 < SpawnUT=2000 → active
            Assert.True(midUT < chain.SpawnUT);
        }

        /// <summary>
        /// At the exact SpawnUT boundary, the chain is NOT active (spawn has happened).
        /// Guards: boundary condition.
        /// </summary>
        [Fact]
        public void ExactSpawnUT_ChainNotActive()
        {
            var (trees, claimedPid) = BuildLongChainScenario();

            double exactUT = 2000;
            var chains = GhostChainWalker.ComputeAllGhostChains(trees, exactUT);

            Assert.Single(chains);
            var chain = chains[claimedPid];
            Assert.Equal(2000.0, chain.SpawnUT);

            // At exact SpawnUT: currentUT=2000 is NOT < SpawnUT=2000 → not active
            Assert.False(exactUT < chain.SpawnUT);
        }

        /// <summary>
        /// Just before SpawnUT, chain is still active.
        /// Guards: epsilon boundary.
        /// </summary>
        [Fact]
        public void JustBeforeSpawnUT_ChainStillActive()
        {
            var (trees, claimedPid) = BuildLongChainScenario();

            double justBeforeUT = 1999.999;
            var chains = GhostChainWalker.ComputeAllGhostChains(trees, justBeforeUT);

            Assert.Single(chains);
            var chain = chains[claimedPid];
            Assert.True(justBeforeUT < chain.SpawnUT);
        }

        #endregion

        #region Re-evaluation with new committed trees

        /// <summary>
        /// First computation sees one chain. After adding a new tree (simulating
        /// a new commit between save/load cycles), recomputation picks up the
        /// new tree's chains.
        /// Guards: re-evaluation picks up new committed recordings.
        /// </summary>
        [Fact]
        public void MultipleLoads_ChainsReEvaluated()
        {
            // First load: one tree claiming PID=100
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock1");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock1");
            var dock1 = MakeBranchPoint("bp-dock1", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });
            var tree1 = MakeTree("tree-1", new[] { r1, r1Leaf },
                new[] { dock1 });

            var trees = new List<RecordingTree> { tree1 };

            var chains1 = GhostChainWalker.ComputeAllGhostChains(trees, 900);
            Assert.Single(chains1);
            Assert.True(chains1.ContainsKey(100));
            Assert.False(chains1.ContainsKey(200));

            // Simulate new commit: add a second tree claiming PID=200
            var r2 = MakeRecording("R2", 70, 2000, 2060, childBpId: "bp-dock2");
            var r2Leaf = MakeRecording("R2-leaf", 200, 2060, 2120,
                parentBpId: "bp-dock2");
            var dock2 = MakeBranchPoint("bp-dock2", BranchPointType.Dock,
                2060, 200, new[] { "R2" }, new[] { "R2-leaf" });
            var tree2 = MakeTree("tree-2", new[] { r2, r2Leaf },
                new[] { dock2 });

            trees.Add(tree2);

            // Second load: recomputation sees both chains
            var chains2 = GhostChainWalker.ComputeAllGhostChains(trees, 900);
            Assert.Equal(2, chains2.Count);
            Assert.True(chains2.ContainsKey(100));
            Assert.True(chains2.ContainsKey(200));
        }

        /// <summary>
        /// After removing a tree (simulating deletion or revert), recomputation
        /// no longer includes the removed tree's chain.
        /// Guards: re-evaluation reflects current state, not stale state.
        /// </summary>
        [Fact]
        public void TreeRemoved_ChainDisappearsOnReEvaluation()
        {
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock1");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock1");
            var dock1 = MakeBranchPoint("bp-dock1", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });
            var tree1 = MakeTree("tree-1", new[] { r1, r1Leaf },
                new[] { dock1 });

            var r2 = MakeRecording("R2", 70, 2000, 2060, childBpId: "bp-dock2");
            var r2Leaf = MakeRecording("R2-leaf", 200, 2060, 2120,
                parentBpId: "bp-dock2");
            var dock2 = MakeBranchPoint("bp-dock2", BranchPointType.Dock,
                2060, 200, new[] { "R2" }, new[] { "R2-leaf" });
            var tree2 = MakeTree("tree-2", new[] { r2, r2Leaf },
                new[] { dock2 });

            var trees = new List<RecordingTree> { tree1, tree2 };

            var chains1 = GhostChainWalker.ComputeAllGhostChains(trees, 900);
            Assert.Equal(2, chains1.Count);

            // Remove tree-2 (simulating a revert that removes its recordings)
            trees.RemoveAt(1);

            var chains2 = GhostChainWalker.ComputeAllGhostChains(trees, 900);
            Assert.Single(chains2);
            Assert.True(chains2.ContainsKey(100));
            Assert.False(chains2.ContainsKey(200));
        }

        #endregion

        #region Backward compatibility — v6 recordings with merge

        /// <summary>
        /// A v6-format tree with a Dock BranchPoint targeting an external vessel
        /// triggers ghosting via ComputeAllGhostChains. This verifies that existing
        /// (pre-Phase-6) committed recordings retroactively trigger ghosting when
        /// the chain walker runs on them.
        /// Guards: existing v6 recordings with MERGE events create chains (Section 10.1).
        /// </summary>
        [Fact]
        public void BackwardCompat_V6RecordingsWithMerge_TriggerGhosting()
        {
            // Simulate a v6 recording tree (same format as pre-Phase-6 recordings)
            // that has a Dock branch point with a TargetVesselPersistentId
            var r1 = MakeRecording("v6-R1", 50, 1000, 1060, childBpId: "bp-dock");
            var r1Leaf = MakeRecording("v6-R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock");

            var dockBp = MakeBranchPoint("bp-dock", BranchPointType.Dock,
                1060, 100, new[] { "v6-R1" }, new[] { "v6-R1-leaf" });

            var tree = MakeTree("v6-tree", new[] { r1, r1Leaf },
                new[] { dockBp });

            // The chain walker operates on BranchPoints/PartEvents — data that
            // already exists in v6 format. No format upgrade needed.
            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.Single(chains);
            Assert.True(chains.ContainsKey(100));
            Assert.Equal("v6-R1-leaf", chains[100].TipRecordingId);
            Assert.Equal(1120.0, chains[100].SpawnUT);
        }

        /// <summary>
        /// A v6 recording tree with background event claims (PartEvent with
        /// ghosting-trigger type on a non-root-lineage vessel) also triggers
        /// chain creation retroactively.
        /// Guards: background event path in backward compat.
        /// </summary>
        [Fact]
        public void BackwardCompat_V6BackgroundEvents_TriggerGhosting()
        {
            // Root recording (vessel A, PID=50)
            var r1 = MakeRecording("v6-R1", 50, 1000, 1120);

            // Background recording for station S (PID=100) with structural event
            var bgRec = MakeRecording("v6-BG-S", 100, 1000, 1120);
            bgRec.PartEvents = new List<PartEvent>
            {
                new PartEvent
                {
                    ut = 1050,
                    partPersistentId = 999,
                    eventType = PartEventType.Decoupled,
                    partName = "dockingPort"
                }
            };

            var tree = MakeTree("v6-tree", new[] { r1, bgRec }, null);

            var chains = GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree> { tree }, 900);

            Assert.Single(chains);
            Assert.True(chains.ContainsKey(100));
            Assert.Equal("BACKGROUND_EVENT", chains[100].Links[0].interactionType);
        }

        #endregion

        #region Chain state not persisted in scenario

        /// <summary>
        /// Verifies that ParsekScenario.cs does not reference GhostChain or
        /// GhostChainWalker. Chain state is never persisted — it is always
        /// re-derived from committed recordings on scene load.
        /// Guards: chain state is never accidentally persisted.
        /// </summary>
        [Fact]
        public void ChainStateNotPersistedInScenario()
        {
            // Read ParsekScenario.cs source and verify no ghost chain references
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string scenarioPath = Path.Combine(projectRoot,
                "Source", "Parsek", "ParsekScenario.cs");

            // If the file doesn't exist from this path, try an alternative
            if (!File.Exists(scenarioPath))
            {
                scenarioPath = Path.Combine(projectRoot,
                    "Parsek", "ParsekScenario.cs");
            }

            Assert.True(File.Exists(scenarioPath),
                $"ParsekScenario.cs not found at {scenarioPath}");

            string source = File.ReadAllText(scenarioPath);

            // ParsekScenario should NOT contain references to ghost chain types.
            // Chains are derived in ParsekFlight, not persisted in the scenario.
            Assert.DoesNotContain("GhostChain", source);
            Assert.DoesNotContain("GhostChainWalker", source);
            Assert.DoesNotContain("activeGhostChains", source);
            Assert.DoesNotContain("GHOST_CHAIN", source);
        }

        #endregion

        #region Log assertions

        /// <summary>
        /// Deterministic re-derivation: repeated calls with stable input return
        /// identical chains. Per-frame callers (tracking station Update) hit this
        /// path every tick; the diagnostic lines are emitted once via
        /// VerboseOnChange and silently coalesced on stable repeats. The
        /// determinism guarantee asserted here is on the returned chain map,
        /// not on log-line counts.
        /// </summary>
        [Fact]
        public void DeterministicReDerivation_StableInput_ReturnsIdenticalChains()
        {
            var (trees, _) = BuildSingleChainScenario();

            logLines.Clear();
            var first = GhostChainWalker.ComputeAllGhostChains(trees, 900);
            Assert.Contains(logLines, l =>
                l.Contains("[ChainWalker]") && l.Contains("Chain built"));

            var second = GhostChainWalker.ComputeAllGhostChains(trees, 900);

            Assert.Equal(first.Count, second.Count);
            foreach (var kvp in first)
            {
                Assert.True(second.TryGetValue(kvp.Key, out var s));
                Assert.Equal(kvp.Value.OriginalVesselPid, s.OriginalVesselPid);
                Assert.Equal(kvp.Value.Links.Count, s.Links.Count);
                Assert.Equal(kvp.Value.TipRecordingId, s.TipRecordingId);
                Assert.Equal(kvp.Value.SpawnUT, s.SpawnUT);
                Assert.Equal(kvp.Value.IsTerminated, s.IsTerminated);
            }
        }

        /// <summary>
        /// Empty trees log "No committed trees" diagnostic.
        /// </summary>
        [Fact]
        public void EmptyTrees_LogsNoCommittedTrees()
        {
            logLines.Clear();

            GhostChainWalker.ComputeAllGhostChains(
                new List<RecordingTree>(), 1000);

            Assert.Contains(logLines, l =>
                l.Contains("[ChainWalker]") && l.Contains("No committed trees"));
        }

        /// <summary>
        /// Re-evaluation after adding a new tree logs the new chain.
        /// </summary>
        [Fact]
        public void ReEvaluation_NewChainLogged()
        {
            var r1 = MakeRecording("R1", 50, 1000, 1060, childBpId: "bp-dock1");
            var r1Leaf = MakeRecording("R1-leaf", 100, 1060, 1120,
                parentBpId: "bp-dock1");
            var dock1 = MakeBranchPoint("bp-dock1", BranchPointType.Dock,
                1060, 100, new[] { "R1" }, new[] { "R1-leaf" });
            var tree1 = MakeTree("tree-1", new[] { r1, r1Leaf },
                new[] { dock1 });

            var trees = new List<RecordingTree> { tree1 };

            // First evaluation
            GhostChainWalker.ComputeAllGhostChains(trees, 900);

            // Add second tree
            var r2 = MakeRecording("R2", 70, 2000, 2060, childBpId: "bp-dock2");
            var r2Leaf = MakeRecording("R2-leaf", 200, 2060, 2120,
                parentBpId: "bp-dock2");
            var dock2 = MakeBranchPoint("bp-dock2", BranchPointType.Dock,
                2060, 200, new[] { "R2" }, new[] { "R2-leaf" });
            var tree2 = MakeTree("tree-2", new[] { r2, r2Leaf },
                new[] { dock2 });
            trees.Add(tree2);

            logLines.Clear();
            GhostChainWalker.ComputeAllGhostChains(trees, 900);

            // Should log chain for vessel=200
            Assert.Contains(logLines, l =>
                l.Contains("[ChainWalker]") && l.Contains("Chain built")
                && l.Contains("vessel=200"));
        }

        #endregion
    }
}
