using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class TreeCommitTests : System.IDisposable
    {
        public TreeCommitTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RecordingStore.SkipSidecarCurrencyCheckForTesting = true;
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekScenario.ResetInstanceForTesting();
        }

        // --- Helper methods ---

        private Recording MakeRecording(string id, string treeId,
            string vesselName = "TestVessel", uint pid = 0,
            TerminalState? terminalState = null,
            ConfigNode vesselSnapshot = null,
            string childBranchPointId = null,
            string parentBranchPointId = null,
            string evaCrewName = null)
        {
            return new Recording
            {
                RecordingId = id,
                TreeId = treeId,
                VesselName = vesselName,
                VesselPersistentId = pid,
                TerminalStateValue = terminalState,
                VesselSnapshot = vesselSnapshot,
                ChildBranchPointId = childBranchPointId,
                ParentBranchPointId = parentBranchPointId,
                EvaCrewName = evaCrewName
            };
        }

        private ConfigNode MakeMinimalSnapshot()
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("name", "Test Vessel");
            node.AddValue("pid", "12345");
            return node;
        }

        private static ChildSlot Slot(int index, string recId)
        {
            return new ChildSlot
            {
                SlotIndex = index,
                OriginChildRecordingId = recId,
                Controllable = true,
            };
        }

        private static ParsekScenario InstallScenarioWithRps(params RewindPoint[] rps)
        {
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint>(rps ?? Array.Empty<RewindPoint>()),
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            return scenario;
        }

        private RecordingTree MakeSimpleTree(string treeId = "tree001")
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Test Tree",
                RootRecordingId = "root",
                ActiveRecordingId = "root"
            };

            tree.Recordings["root"] = MakeRecording("root", treeId, "Root Vessel", 1000,
                vesselSnapshot: MakeMinimalSnapshot());

            return tree;
        }

        private RecordingTree MakeTreeWithBranch(string treeId = "tree002")
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Branching Tree",
                RootRecordingId = "root",
                ActiveRecordingId = "child1"
            };

            // Root has a child branch point
            tree.Recordings["root"] = MakeRecording("root", treeId, "Root", 1000,
                childBranchPointId: "bp1", vesselSnapshot: MakeMinimalSnapshot());

            // Two children
            tree.Recordings["child1"] = MakeRecording("child1", treeId, "Child 1", 2000,
                parentBranchPointId: "bp1", vesselSnapshot: MakeMinimalSnapshot());
            tree.Recordings["child2"] = MakeRecording("child2", treeId, "Child 2", 3000,
                parentBranchPointId: "bp1", vesselSnapshot: MakeMinimalSnapshot());

            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp1",
                UT = 17050.0,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "root" },
                ChildRecordingIds = new List<string> { "child1", "child2" }
            });

            return tree;
        }

        // --- GetSpawnableLeaves tests ---

        [Fact]
        public void GetSpawnableLeaves_SimpleTree_ReturnsRoot()
        {
            var tree = MakeSimpleTree();
            tree.Recordings["root"].TerminalStateValue = TerminalState.Orbiting;

            var leaves = tree.GetSpawnableLeaves();

            Assert.Single(leaves);
            Assert.Equal("root", leaves[0].RecordingId);
        }

        [Fact]
        public void GetSpawnableLeaves_BranchTree_ReturnsChildren()
        {
            var tree = MakeTreeWithBranch();
            tree.Recordings["child1"].TerminalStateValue = TerminalState.Orbiting;
            tree.Recordings["child2"].TerminalStateValue = TerminalState.Landed;

            var leaves = tree.GetSpawnableLeaves();

            Assert.Equal(2, leaves.Count);
            // Both children are leaves (root has childBranchPointId set)
            var ids = new HashSet<string>();
            foreach (var l in leaves) ids.Add(l.RecordingId);
            Assert.Contains("child1", ids);
            Assert.Contains("child2", ids);
        }

        [Fact]
        public void GetSpawnableLeaves_DestroyedLeaf_Excluded()
        {
            var tree = MakeTreeWithBranch();
            tree.Recordings["child1"].TerminalStateValue = TerminalState.Orbiting;
            tree.Recordings["child2"].TerminalStateValue = TerminalState.Destroyed;
            tree.Recordings["child2"].VesselSnapshot = null;

            var leaves = tree.GetSpawnableLeaves();

            Assert.Single(leaves);
            Assert.Equal("child1", leaves[0].RecordingId);
        }

        [Fact]
        public void GetSpawnableLeaves_AllTerminalStates()
        {
            var tree = new RecordingTree
            {
                Id = "ts_tree",
                TreeName = "Terminal States",
                RootRecordingId = "root",
                ActiveRecordingId = null
            };

            // Root branches to all 8 terminal states
            tree.Recordings["root"] = MakeRecording("root", "ts_tree", childBranchPointId: "bp1");

            var terminalStates = new[]
            {
                TerminalState.Orbiting,
                TerminalState.Landed,
                TerminalState.Splashed,
                TerminalState.SubOrbital,
                TerminalState.Destroyed,
                TerminalState.Recovered,
                TerminalState.Docked,
                TerminalState.Boarded
            };

            for (int i = 0; i < terminalStates.Length; i++)
            {
                string id = $"leaf_{i}";
                bool hasSnapshot = terminalStates[i] != TerminalState.Destroyed
                    && terminalStates[i] != TerminalState.Recovered;
                tree.Recordings[id] = MakeRecording(id, "ts_tree",
                    parentBranchPointId: "bp1",
                    terminalState: terminalStates[i],
                    vesselSnapshot: hasSnapshot ? MakeMinimalSnapshot() : null);
            }

            var spawnable = tree.GetSpawnableLeaves();

            // Orbiting, Landed, Splashed, SubOrbital should be spawnable (4)
            Assert.Equal(4, spawnable.Count);
            var spawnableStates = new HashSet<TerminalState>();
            foreach (var l in spawnable)
                spawnableStates.Add(l.TerminalStateValue.Value);

            Assert.Contains(TerminalState.Orbiting, spawnableStates);
            Assert.Contains(TerminalState.Landed, spawnableStates);
            Assert.Contains(TerminalState.Splashed, spawnableStates);
            Assert.Contains(TerminalState.SubOrbital, spawnableStates);
        }

        [Fact]
        public void GetSpawnableLeaves_NullSnapshot_Excluded()
        {
            var tree = MakeSimpleTree();
            tree.Recordings["root"].TerminalStateValue = TerminalState.Orbiting;
            tree.Recordings["root"].VesselSnapshot = null;

            var leaves = tree.GetSpawnableLeaves();

            Assert.Empty(leaves);
        }

        [Fact]
        public void GetSpawnableLeaves_InternalNodeWithChild_Excluded()
        {
            var tree = MakeTreeWithBranch();
            tree.Recordings["child1"].TerminalStateValue = TerminalState.Orbiting;
            tree.Recordings["child2"].TerminalStateValue = TerminalState.Landed;

            var leaves = tree.GetSpawnableLeaves();

            // Root should NOT be in the list (it has childBranchPointId)
            foreach (var l in leaves)
                Assert.NotEqual("root", l.RecordingId);
        }

        // --- GetAllLeaves tests ---

        [Fact]
        public void GetAllLeaves_IncludesDestroyed()
        {
            var tree = MakeTreeWithBranch();
            tree.Recordings["child1"].TerminalStateValue = TerminalState.Orbiting;
            tree.Recordings["child2"].TerminalStateValue = TerminalState.Destroyed;
            tree.Recordings["child2"].VesselSnapshot = null;

            var allLeaves = tree.GetAllLeaves();
            var spawnableLeaves = tree.GetSpawnableLeaves();

            Assert.Equal(2, allLeaves.Count);
            Assert.Single(spawnableLeaves);
        }

        // --- DetermineTerminalState tests ---

        [Theory]
        [InlineData(32, TerminalState.Orbiting)]   // ORBITING
        [InlineData(1, TerminalState.Landed)]       // LANDED
        [InlineData(2, TerminalState.Splashed)]     // SPLASHED
        [InlineData(16, TerminalState.SubOrbital)]  // SUB_ORBITAL
        [InlineData(8, TerminalState.SubOrbital)]   // FLYING
        [InlineData(64, TerminalState.SubOrbital)]  // ESCAPING
        [InlineData(4, TerminalState.Landed)]       // PRELAUNCH
        [InlineData(128, TerminalState.Docked)]     // DOCKED
        public void DetermineTerminalState_AllSituations(int situation, TerminalState expected)
        {
            var result = RecordingTree.DetermineTerminalState(situation);
            Assert.Equal(expected, result);
        }

        // --- IsSpawnableLeaf tests ---

        [Fact]
        public void IsSpawnableLeaf_OrbitingWithSnapshot_True()
        {
            var rec = MakeRecording("r1", "t1",
                terminalState: TerminalState.Orbiting,
                vesselSnapshot: MakeMinimalSnapshot());

            Assert.True(RecordingTree.IsSpawnableLeaf(rec));
        }

        [Fact]
        public void IsSpawnableLeaf_DestroyedWithSnapshot_False()
        {
            var rec = MakeRecording("r1", "t1",
                terminalState: TerminalState.Destroyed,
                vesselSnapshot: MakeMinimalSnapshot());

            Assert.False(RecordingTree.IsSpawnableLeaf(rec));
        }

        [Fact]
        public void IsSpawnableLeaf_DockedWithSnapshot_False()
        {
            var rec = MakeRecording("r1", "t1",
                terminalState: TerminalState.Docked,
                vesselSnapshot: MakeMinimalSnapshot());

            Assert.False(RecordingTree.IsSpawnableLeaf(rec));
        }

        [Fact]
        public void IsSpawnableLeaf_BoardedWithSnapshot_False()
        {
            var rec = MakeRecording("r1", "t1",
                terminalState: TerminalState.Boarded,
                vesselSnapshot: MakeMinimalSnapshot());

            Assert.False(RecordingTree.IsSpawnableLeaf(rec));
        }

        [Fact]
        public void IsSpawnableLeaf_HasChild_False()
        {
            var rec = MakeRecording("r1", "t1",
                terminalState: TerminalState.Orbiting,
                vesselSnapshot: MakeMinimalSnapshot(),
                childBranchPointId: "bp1");

            Assert.False(RecordingTree.IsSpawnableLeaf(rec));
        }

        [Fact]
        public void IsSpawnableLeaf_NoTerminalState_WithSnapshot_True()
        {
            // Recording still active at commit time — no terminal state yet
            var rec = MakeRecording("r1", "t1",
                vesselSnapshot: MakeMinimalSnapshot());

            Assert.True(RecordingTree.IsSpawnableLeaf(rec));
        }

        // --- CommitTree storage tests ---

        [Fact]
        public void CommitTree_AddsRecordingsToCommittedList()
        {
            var tree = MakeTreeWithBranch();
            tree.Recordings["child1"].TerminalStateValue = TerminalState.Orbiting;
            tree.Recordings["child2"].TerminalStateValue = TerminalState.Landed;

            RecordingStore.CommitTree(tree);

            // All 3 recordings (root + 2 children) should be in CommittedRecordings
            Assert.Equal(3, RecordingStore.CommittedRecordings.Count);
            Assert.Single(RecordingStore.CommittedTrees);
            Assert.Equal("tree002", RecordingStore.CommittedTrees[0].Id);
        }

        [Fact]
        public void CommitTree_MarksPriorSpawnEndpointSupersededByContinuation()
        {
            var prior = MakeRecording("old-butterfly", null,
                vesselName: "Butterfly Rover",
                pid: 22060629,
                terminalState: TerminalState.Landed,
                vesselSnapshot: MakeMinimalSnapshot());
            prior.ExplicitStartUT = 18.0;
            prior.ExplicitEndUT = 61.0;
            prior.VesselSpawned = true;
            prior.SpawnedVesselPersistentId = 1215753389u;
            RecordingStore.AddRecordingWithTreeForTesting(prior, "Butterfly Rover");

            var tree = MakeSimpleTree("continued_tree");
            var continued = tree.Recordings["root"];
            continued.RecordingId = "continued-butterfly";
            continued.VesselName = "Butterfly Rover";
            continued.VesselPersistentId = 1215753389u;
            continued.TerminalStateValue = TerminalState.Landed;
            continued.VesselSnapshot = MakeMinimalSnapshot();
            continued.ExplicitStartUT = 97.0;
            continued.ExplicitEndUT = 116.0;

            RecordingStore.CommitTree(tree);

            Assert.Equal("continued-butterfly",
                prior.TerminalSpawnSupersededByRecordingId);

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                prior, isActiveChainMember: false, isChainLooping: false);
            Assert.False(needsSpawn);
            Assert.Contains("terminal spawn superseded", reason);
        }

        // F5 / #976: an adoption-stamped prior (SpawnedVesselPersistentId == its craft-baked
        // VesselPersistentId) must NOT be marked superseded by a DIFFERENT launch of the same craft
        // (same baked pid, different launch guid). Real spawns (distinct spawn pid) are unaffected.
        [Fact]
        public void CommitTree_DoesNotSupersedeAdoptionStampedPriorByDifferentLaunch()
        {
            var prior = MakeRecording("launch-A", null,
                vesselName: "Kerbal X",
                pid: 2708531065u,
                terminalState: TerminalState.Landed,
                vesselSnapshot: MakeMinimalSnapshot());
            prior.ExplicitStartUT = 18.0;
            prior.ExplicitEndUT = 61.0;
            prior.VesselSpawned = true;
            prior.SpawnedVesselPersistentId = 2708531065u;             // adoption stamp == baked pid
            prior.RecordedVesselGuid = "2b6e6a60d2c947489753371317fa067e";
            RecordingStore.AddRecordingWithTreeForTesting(prior, "Kerbal X");

            var tree = MakeSimpleTree("relaunch_tree");
            var continued = tree.Recordings["root"];
            continued.RecordingId = "launch-B";
            continued.VesselName = "Kerbal X";
            continued.VesselPersistentId = 2708531065u;                // relaunch reuses the baked pid
            continued.TerminalStateValue = TerminalState.Landed;
            continued.VesselSnapshot = MakeMinimalSnapshot();
            continued.ExplicitStartUT = 97.0;
            continued.ExplicitEndUT = 116.0;
            continued.RecordedVesselGuid = "a424011b746440baae6030e225c9de31"; // different launch

            RecordingStore.CommitTree(tree);

            Assert.Null(prior.TerminalSpawnSupersededByRecordingId);
        }

        [Fact]
        public void CommitTree_MarksPriorSpawnEndpointSupersededByPidMatchAfterRename()
        {
            var prior = MakeRecording("old-butterfly", null,
                vesselName: "Butterfly Rover",
                pid: 22060629,
                terminalState: TerminalState.Landed,
                vesselSnapshot: MakeMinimalSnapshot());
            prior.ExplicitStartUT = 18.0;
            prior.ExplicitEndUT = 61.0;
            prior.VesselSpawned = true;
            prior.SpawnedVesselPersistentId = 1215753389u;
            RecordingStore.AddRecordingWithTreeForTesting(prior, "Butterfly Rover");

            var tree = MakeSimpleTree("continued_tree");
            var continued = tree.Recordings["root"];
            continued.RecordingId = "continued-butterfly-renamed";
            continued.VesselName = "Butterfly Rover Mk II";
            continued.VesselPersistentId = 1215753389u;
            continued.TerminalStateValue = TerminalState.Landed;
            continued.VesselSnapshot = MakeMinimalSnapshot();
            continued.ExplicitStartUT = 97.0;
            continued.ExplicitEndUT = 116.0;

            RecordingStore.CommitTree(tree);

            Assert.Equal("continued-butterfly-renamed",
                prior.TerminalSpawnSupersededByRecordingId);

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                prior, isActiveChainMember: false, isChainLooping: false);
            Assert.False(needsSpawn);
            Assert.Contains("terminal spawn superseded", reason);
        }

        [Fact]
        public void ResetAllPlaybackState_MarksPollutedSameNameEndpointSupersededBeforePidReset()
        {
            var prior = MakeRecording("old-butterfly", null,
                vesselName: "Butterfly Rover",
                pid: 22060629,
                terminalState: TerminalState.Landed,
                vesselSnapshot: MakeMinimalSnapshot());
            prior.ExplicitStartUT = 18.0;
            prior.ExplicitEndUT = 61.0;
            prior.VesselSpawned = true;
            prior.SpawnedVesselPersistentId = 3394657290u;
            RecordingStore.AddRecordingWithTreeForTesting(prior, "Butterfly Rover");

            var tree = new RecordingTree
            {
                Id = "continued-tree",
                TreeName = "Crater Crawler",
                RootRecordingId = "crater-root",
                ActiveRecordingId = "continued-butterfly"
            };
            var root = MakeRecording("crater-root", tree.Id,
                vesselName: "Crater Crawler",
                pid: 4056933280u,
                terminalState: TerminalState.Landed,
                vesselSnapshot: MakeMinimalSnapshot());
            root.TreeOrder = 0;
            root.ExplicitStartUT = 8.0;
            root.ExplicitEndUT = 116.0;

            var continued = MakeRecording("continued-butterfly", tree.Id,
                vesselName: "Butterfly Rover",
                pid: 1215753389u,
                terminalState: TerminalState.Landed,
                vesselSnapshot: MakeMinimalSnapshot());
            continued.TreeOrder = 1;
            continued.ExplicitStartUT = 97.0;
            continued.ExplicitEndUT = 116.0;
            continued.VesselSpawned = true;
            continued.SpawnedVesselPersistentId = 1215753389u;

            tree.Recordings[root.RecordingId] = root;
            tree.Recordings[continued.RecordingId] = continued;
            RecordingStore.AddCommittedInternal(root);
            RecordingStore.AddCommittedInternal(continued);
            RecordingStore.AddCommittedTreeInternal(tree);

            RecordingStore.ResetAllPlaybackState();

            Assert.Equal("continued-butterfly",
                prior.TerminalSpawnSupersededByRecordingId);
            Assert.Equal(0u, prior.SpawnedVesselPersistentId);

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                prior, isActiveChainMember: false, isChainLooping: false);
            Assert.False(needsSpawn);
            Assert.Contains("terminal spawn superseded", reason);
        }

        [Fact]
        public void RewindReplayTargetScope_SurvivesCommittedListReload()
        {
            var rewindTarget = MakeRecording("rewind-target", null, pid: 123u);

            RecordingStore.SetRewindReplayTargetScope(rewindTarget);
            RecordingStore.ClearCommittedInternal();
            RecordingStore.ClearCommittedTreesInternal();

            Assert.Equal(123u, RecordingStore.RewindReplayTargetSourcePid);
            Assert.Equal("rewind-target", RecordingStore.RewindReplayTargetRecordingId);

            RecordingStore.ClearCommitted();

            Assert.Equal(0u, RecordingStore.RewindReplayTargetSourcePid);
            Assert.Null(RecordingStore.RewindReplayTargetRecordingId);
        }

        [Fact]
        public void CommitTree_DestroyedChildUnderRewindPoint_PromotesToCommittedProvisional()
        {
            var tree = MakeTreeWithBranch("rewind_tree");
            tree.BranchPoints[0].RewindPointId = "rp_stage";
            tree.Recordings["child1"].TerminalStateValue = TerminalState.Orbiting;
            tree.Recordings["child2"].VesselName = "Kerbal X Probe";
            tree.Recordings["child2"].TerminalStateValue = TerminalState.Destroyed;
            var rp = new RewindPoint
            {
                RewindPointId = "rp_stage",
                BranchPointId = "bp1",
                SessionProvisional = true,
                CreatingSessionId = null,
                ChildSlots = new List<ChildSlot>
                {
                    Slot(0, "child1"),
                    Slot(1, "child2"),
                }
            };
            InstallScenarioWithRps(rp);

            RecordingStore.CommitTree(tree);

            Assert.Equal(MergeState.CommittedProvisional,
                tree.Recordings["child2"].MergeState);
            Assert.False(rp.SessionProvisional);
            Assert.Null(rp.CreatingSessionId);
            Assert.Contains(tree.Recordings["child2"],
                RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void CommitTree_DestroyedChildUnderRewindPointWithoutSlot_RemainsImmutable()
        {
            var tree = MakeTreeWithBranch("rewind_tree_missing_slot");
            tree.BranchPoints[0].RewindPointId = "rp_stage";
            tree.Recordings["child1"].TerminalStateValue = TerminalState.Orbiting;
            tree.Recordings["child2"].VesselName = "Kerbal X Debris";
            tree.Recordings["child2"].TerminalStateValue = TerminalState.Destroyed;
            var rp = new RewindPoint
            {
                RewindPointId = "rp_stage",
                BranchPointId = "bp1",
                SessionProvisional = true,
                CreatingSessionId = null,
                ChildSlots = new List<ChildSlot>
                {
                    Slot(0, "child1"),
                    Slot(1, "unrelated_controlled_child"),
                }
            };
            InstallScenarioWithRps(rp);

            RecordingStore.CommitTree(tree);

            Assert.Equal(MergeState.Immutable,
                tree.Recordings["child2"].MergeState);
            Assert.Contains(tree.Recordings["child2"],
                RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void CommitTree_NormalStagingRewindPointPromoted_AllImmutableSlotsCanReap()
        {
            var tree = MakeTreeWithBranch("clean_rewind_tree");
            tree.BranchPoints[0].RewindPointId = "rp_stage";
            tree.Recordings["child1"].TerminalStateValue = TerminalState.Orbiting;
            tree.Recordings["child2"].TerminalStateValue = TerminalState.Landed;
            var rp = new RewindPoint
            {
                RewindPointId = "rp_stage",
                BranchPointId = "bp1",
                SessionProvisional = true,
                CreatingSessionId = null,
                ChildSlots = new List<ChildSlot>
                {
                    Slot(0, "child1"),
                    Slot(1, "child2"),
                }
            };
            var scenario = InstallScenarioWithRps(rp);

            RecordingStore.CommitTree(tree);
            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.False(rp.SessionProvisional);
            Assert.Equal(1, reaped);
            Assert.Empty(scenario.RewindPoints);
            Assert.Null(tree.BranchPoints[0].RewindPointId);
        }

        [Fact]
        public void CommitTree_OrbitingNonFocusChildUnderRewindPoint_PromotesToCommittedProvisional()
        {
            var tree = MakeTreeWithBranch("stable_leaf_tree");
            tree.BranchPoints[0].RewindPointId = "rp_stage";
            tree.Recordings["child1"].TerminalStateValue = TerminalState.Landed;
            tree.Recordings["child2"].TerminalStateValue = TerminalState.Orbiting;
            var rp = new RewindPoint
            {
                RewindPointId = "rp_stage",
                BranchPointId = "bp1",
                SessionProvisional = true,
                CreatingSessionId = null,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    Slot(0, "child1"),
                    Slot(1, "child2"),
                }
            };
            InstallScenarioWithRps(rp);

            RecordingStore.CommitTree(tree);

            Assert.Equal(MergeState.Immutable, tree.Recordings["child1"].MergeState);
            Assert.Equal(MergeState.CommittedProvisional, tree.Recordings["child2"].MergeState);
            Assert.False(rp.SessionProvisional);
        }

        [Fact]
        public void CommitTree_LandedEvaChildUnderRewindPoint_AutoSealsInsteadOfPromoting()
        {
            AssertStableSurfaceEvaChildAutoSeals(TerminalState.Landed);
        }

        [Fact]
        public void CommitTree_SplashedEvaChildUnderRewindPoint_AutoSealsInsteadOfPromoting()
        {
            AssertStableSurfaceEvaChildAutoSeals(TerminalState.Splashed);
        }

        private void AssertStableSurfaceEvaChildAutoSeals(TerminalState terminal)
        {
            bool priorSuppress = ParsekLog.SuppressLogging;
            bool? priorVerboseOverride = ParsekLog.VerboseOverrideForTesting;
            var logLines = new List<string>();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            try
            {
                var tree = MakeTreeWithBranch("stable_eva_tree");
                tree.BranchPoints[0].Type = BranchPointType.EVA;
                tree.BranchPoints[0].RewindPointId = "rp_stage";
                tree.Recordings["child1"].TerminalStateValue = TerminalState.Landed;
                tree.Recordings["child2"].TerminalStateValue = terminal;
                tree.Recordings["child2"].VesselName = "Jebediah Kerman";
                tree.Recordings["child2"].EvaCrewName = "Jebediah Kerman";

                var rp = new RewindPoint
                {
                    RewindPointId = "rp_stage",
                    BranchPointId = "bp1",
                    SessionProvisional = true,
                    CreatingSessionId = null,
                    FocusSlotIndex = 0,
                    ChildSlots = new List<ChildSlot>
                    {
                        Slot(0, "child1"),
                        Slot(1, "child2"),
                    }
                };
                InstallScenarioWithRps(rp);

                RecordingStore.CommitTree(tree);

                Assert.Equal(MergeState.Immutable, tree.Recordings["child1"].MergeState);
                // Stable-EVA conclusion leaves the slot's tip Immutable (the
                // born state) instead of demoting to CommittedProvisional, so
                // the slot reads closed.
                Assert.Equal(MergeState.Immutable, tree.Recordings["child2"].MergeState);
                // Shape still qualifies (strandedEva), but the slot is closed
                // because its effective tip is Immutable.
                Assert.True(UnfinishedFlightClassifier.TryQualify(
                    tree.Recordings["child2"], rp.ChildSlots[1], rp,
                    out string sealedReason, tree));
                Assert.Equal("strandedEva", sealedReason);
                Assert.False(UnfinishedFlightClassifier.IsSlotEffectiveTipOpen(rp.ChildSlots[1]));
                Assert.Contains(logLines, l =>
                    l.Contains("[UnfinishedFlights]")
                    && l.Contains("CommitTree auto-sealed stable EVA")
                    && l.Contains("rec=child2")
                    && l.Contains($"terminal={terminal}")
                    && l.Contains("reason=strandedEva"));
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
                ParsekLog.SuppressLogging = priorSuppress;
                ParsekLog.VerboseOverrideForTesting = priorVerboseOverride;
            }
        }

        [Fact]
        public void CommitTree_OrbitingEvaChildUnderRewindPoint_StillPromotesToCommittedProvisional()
        {
            var tree = MakeTreeWithBranch("orbiting_eva_tree");
            tree.BranchPoints[0].Type = BranchPointType.EVA;
            tree.BranchPoints[0].RewindPointId = "rp_stage";
            tree.Recordings["child1"].TerminalStateValue = TerminalState.Landed;
            tree.Recordings["child2"].TerminalStateValue = TerminalState.Orbiting;
            tree.Recordings["child2"].VesselName = "Jebediah Kerman";
            tree.Recordings["child2"].EvaCrewName = "Jebediah Kerman";
            var rp = new RewindPoint
            {
                RewindPointId = "rp_stage",
                BranchPointId = "bp1",
                SessionProvisional = true,
                CreatingSessionId = null,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    Slot(0, "child1"),
                    Slot(1, "child2"),
                }
            };
            InstallScenarioWithRps(rp);

            RecordingStore.CommitTree(tree);

            Assert.Equal(MergeState.Immutable, tree.Recordings["child1"].MergeState);
            Assert.Equal(MergeState.CommittedProvisional, tree.Recordings["child2"].MergeState);
            Assert.True(UnfinishedFlightClassifier.IsSlotEffectiveTipOpen(rp.ChildSlots[1]));
        }

        [Fact]
        public void CommitTree_AlreadyConcludedOrbitingChild_StaysImmutable()
        {
            // A previously concluded (sealed) orbiting child: its tip is
            // already Immutable when the tree is committed. The first-commit
            // guard skips it on re-commit, and promotion does not re-open it.
            // Open/closed is read from the tip MergeState, so the slot stays
            // closed (collapse-seal-into-mergestate).
            var tree = MakeTreeWithBranch("stable_leaf_sealed_tree");
            tree.BranchPoints[0].RewindPointId = "rp_stage";
            tree.Recordings["child1"].TerminalStateValue = TerminalState.Landed;
            tree.Recordings["child2"].TerminalStateValue = TerminalState.Orbiting;
            var concludedSlot = Slot(1, "child2");
            var rp = new RewindPoint
            {
                RewindPointId = "rp_stage",
                BranchPointId = "bp1",
                SessionProvisional = true,
                CreatingSessionId = null,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    Slot(0, "child1"),
                    concludedSlot,
                }
            };
            InstallScenarioWithRps(rp);

            // Commit once: child2 (non-focus Orbiting) promotes to CP (open).
            RecordingStore.CommitTree(tree);
            Assert.Equal(MergeState.CommittedProvisional, tree.Recordings["child2"].MergeState);

            // Seal it (flip the tip to Immutable), then re-commit the tree.
            tree.Recordings["child2"].MergeState = MergeState.Immutable;
            RecordingStore.CommitTree(tree);

            Assert.Equal(MergeState.Immutable, tree.Recordings["child1"].MergeState);
            // The first-commit guard keeps the concluded tip Immutable; the
            // slot stays closed.
            Assert.Equal(MergeState.Immutable, tree.Recordings["child2"].MergeState);
            Assert.False(rp.SessionProvisional);
            Assert.False(UnfinishedFlightClassifier.IsSlotEffectiveTipOpen(concludedSlot));
        }

        [Fact]
        public void CommitTree_OrbitingFocusChildUnderRewindPoint_RemainsImmutable()
        {
            var tree = MakeTreeWithBranch("stable_focus_tree");
            tree.BranchPoints[0].RewindPointId = "rp_stage";
            tree.Recordings["child1"].TerminalStateValue = TerminalState.Orbiting;
            tree.Recordings["child2"].TerminalStateValue = TerminalState.Landed;
            var rp = new RewindPoint
            {
                RewindPointId = "rp_stage",
                BranchPointId = "bp1",
                SessionProvisional = true,
                CreatingSessionId = null,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    Slot(0, "child1"),
                    Slot(1, "child2"),
                }
            };
            InstallScenarioWithRps(rp);

            RecordingStore.CommitTree(tree);

            Assert.Equal(MergeState.Immutable, tree.Recordings["child1"].MergeState);
            Assert.Equal(MergeState.Immutable, tree.Recordings["child2"].MergeState);
            Assert.False(rp.SessionProvisional);
        }

        [Fact]
        public void CommitTree_DestroyedChildWithoutRewindPoint_RemainsImmutable()
        {
            var tree = MakeTreeWithBranch("plain_tree");
            tree.Recordings["child1"].TerminalStateValue = TerminalState.Orbiting;
            tree.Recordings["child2"].TerminalStateValue = TerminalState.Destroyed;

            RecordingStore.CommitTree(tree);

            Assert.Equal(MergeState.Immutable,
                tree.Recordings["child2"].MergeState);
        }

        [Fact]
        public void CommitTree_DuplicateTreeId_Skipped()
        {
            var tree = MakeSimpleTree("dup_tree");
            RecordingStore.CommitTree(tree);

            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Single(RecordingStore.CommittedTrees);

            // Try to commit same tree again
            RecordingStore.CommitTree(tree);

            // Should not have duplicated
            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Single(RecordingStore.CommittedTrees);
        }

        [Fact]
        public void CommitTree_DuplicateTreeIdEquivalentCopy_Skipped()
        {
            var original = MakeSimpleTree("dup_tree_copy");
            RecordingStore.CommitTree(original);

            var equivalentCopy = MakeSimpleTree("dup_tree_copy");
            equivalentCopy.TreeName = "Equivalent Copy With Different Name";
            RecordingStore.CommitTree(equivalentCopy);

            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Single(RecordingStore.CommittedTrees);
            Assert.Same(original, RecordingStore.CommittedTrees[0]);
        }

        [Fact]
        public void CommitTree_DuplicateTreeIdPoorerCopy_DoesNotReplaceCommittedTree()
        {
            var richer = MakeTreeWithBranch("dup_tree_poorer");
            RecordingStore.CommitTree(richer);

            var staleCopy = MakeSimpleTree("dup_tree_poorer");
            RecordingStore.CommitTree(staleCopy);

            Assert.Equal(3, RecordingStore.CommittedRecordings.Count);
            Assert.Single(RecordingStore.CommittedTrees);
            Assert.Same(richer, RecordingStore.CommittedTrees[0]);
            Assert.Equal(3, RecordingStore.CommittedTrees[0].Recordings.Count);
            Assert.Contains("child1", RecordingStore.CommittedTrees[0].Recordings.Keys);
            Assert.Contains("child2", RecordingStore.CommittedTrees[0].Recordings.Keys);
        }

        [Fact]
        public void CommitPendingTree_SameTreeIdDifferentTopology_ReplacesCommittedTreeAndSavesReplacement()
        {
            var original = MakeSimpleTree("refly_tree");
            RecordingStore.CommitTree(original);

            var replacement = MakeSimpleTree("refly_tree");
            replacement.TreeName = "Updated Re-Fly Tree";
            replacement.ActiveRecordingId = "rec_refly";
            replacement.Recordings["root"].VesselName = "Root Vessel Updated";
            replacement.Recordings["root"].ChildBranchPointId = "bp_refly";
            replacement.Recordings["rec_refly"] = MakeRecording(
                "rec_refly",
                "refly_tree",
                "Re-Fly Vessel",
                2000,
                terminalState: TerminalState.Landed,
                vesselSnapshot: MakeMinimalSnapshot(),
                parentBranchPointId: "bp_refly");
            replacement.BranchPoints.Add(new BranchPoint
            {
                Id = "bp_refly",
                UT = 17100.0,
                Type = BranchPointType.Launch,
                ParentRecordingIds = new List<string> { "root" },
                ChildRecordingIds = new List<string> { "rec_refly" }
            });

            RecordingStore.StashPendingTree(replacement);
            RecordingStore.CommitPendingTree();

            Assert.False(RecordingStore.HasPendingTree);
            Assert.Single(RecordingStore.CommittedTrees);
            Assert.Same(replacement, RecordingStore.CommittedTrees[0]);
            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);
            Assert.Contains(RecordingStore.CommittedRecordings,
                r => r.RecordingId == "rec_refly");

            var scenarioNode = new ConfigNode("SCENARIO");
            ParsekScenario.SaveTreeRecordings(scenarioNode);

            var treeNodes = scenarioNode.GetNodes("RECORDING_TREE");
            Assert.Single(treeNodes);
            Assert.Equal("Updated Re-Fly Tree", treeNodes[0].GetValue("treeName"));
            Assert.Equal("rec_refly", treeNodes[0].GetValue("activeRecordingId"));
            Assert.Contains(treeNodes[0].GetNodes("RECORDING"),
                node => node.GetValue("recordingId") == "rec_refly");
        }

        [Fact]
        public void CommitPendingTree_SameTreeIdReplacement_PreservesRewindSaveFileName()
        {
            // Production regression: post Re-Fly merge the launch-row Rewind
            // button vanished because the topology-update replace path in
            // FinalizeTreeCommit overwrote the existing committed Recording
            // (which owned the launch save filename) with a pending-tree
            // instance whose RewindSaveFileName was null. ShouldShowLegacyRewindButton
            // then fell through to the "no-owner" branch and the button
            // disappeared from the launch row.
            var original = MakeTreeWithBranch("refly_tree");
            original.TreeName = "Kerbal X";
            // The launch save lives on the root recording.
            original.Recordings["root"].RewindSaveFileName = "parsek_rw_launch_001";
            RecordingStore.CommitTree(original);

            // Replacement pending tree (e.g. Re-Fly merge): same recording ids,
            // but the new instances DON'T carry RewindSaveFileName because the
            // pending-tree construction path doesn't propagate it.
            var replacement = MakeTreeWithBranch("refly_tree");
            replacement.TreeName = "Kerbal X";
            // Topology-changing addition so ShouldReplaceCommittedTree returns true.
            replacement.Recordings["rec_refly"] = MakeRecording(
                "rec_refly",
                "refly_tree",
                "Re-Fly Vessel",
                4000,
                terminalState: TerminalState.Landed,
                vesselSnapshot: MakeMinimalSnapshot(),
                parentBranchPointId: "bp1");
            replacement.BranchPoints[0].ChildRecordingIds.Add("rec_refly");
            replacement.ActiveRecordingId = "rec_refly";
            // Critically: replacement's "root" instance has no rewind save.
            Assert.Null(replacement.Recordings["root"].RewindSaveFileName);

            RecordingStore.StashPendingTree(replacement);
            RecordingStore.CommitPendingTree();

            var rootCommitted = RecordingStore.CommittedRecordings
                .Single(r => r.RecordingId == "root");
            Assert.Equal("parsek_rw_launch_001", rootCommitted.RewindSaveFileName);
            // GetRewindRecording / GetRewindSaveFileName resolve through the
            // root, so the launch row's Rewind-to-Launch button continues to
            // work after the topology-update commit.
            var owner = RecordingStore.GetRewindRecording(rootCommitted);
            Assert.Same(rootCommitted, owner);
            Assert.Equal("parsek_rw_launch_001", RecordingStore.GetRewindSaveFileName(rootCommitted));
        }

        [Fact]
        public void CommitPendingTree_SameTreeIdReplacement_PreservesSpawnStateCluster()
        {
            // Reviewer-flagged P2 scope-expansion: the topology-update replace
            // path was only preserving RewindSaveFileName. The full #264 spawn
            // state cluster (VesselSpawned, SpawnedVesselPersistentId,
            // TerminalSpawnSupersededByRecordingId, SpawnAttempts,
            // CollisionBlockCount, SpawnAbandoned, WalkbackExhausted,
            // DuplicateBlockerRecovered, SpawnDeathCount), the rewind-reserved
            // resource ledger, the playback resource cursor
            // (LastAppliedResourceIndex), and the suppression metadata
            // (SpawnSuppressedByRewind*) must also survive.
            var original = MakeTreeWithBranch("refly_tree");
            original.TreeName = "Kerbal X";
            // Stamp the live committed instance with non-default runtime fields.
            var existingRoot = original.Recordings["root"];
            existingRoot.RewindSaveFileName = "parsek_rw_launch_xyz";
            existingRoot.RewindReservedFunds = 12345.0;
            existingRoot.LastAppliedResourceIndex = 7;
            existingRoot.VesselSpawned = true;
            existingRoot.SpawnedVesselPersistentId = 999u;
            existingRoot.TerminalSpawnSupersededByRecordingId = "later_rec";
            existingRoot.MaxDistanceFromLaunch = 1500.0;
            existingRoot.SpawnAttempts = 2;
            existingRoot.SpawnDeathCount = 1;
            existingRoot.WalkbackExhausted = true;
            existingRoot.SpawnSuppressedByRewind = true;
            existingRoot.SpawnSuppressedByRewindReason = "test-reason";
            existingRoot.SceneExitSituation = 4;
            RecordingStore.CommitTree(original);

            // Replacement pending tree: brand-new instance for "root" with
            // every runtime field at default.
            var replacement = MakeTreeWithBranch("refly_tree");
            replacement.TreeName = "Kerbal X";
            replacement.Recordings["rec_refly"] = MakeRecording(
                "rec_refly",
                "refly_tree",
                "Re-Fly Vessel",
                4000,
                terminalState: TerminalState.Landed,
                vesselSnapshot: MakeMinimalSnapshot(),
                parentBranchPointId: "bp1");
            replacement.BranchPoints[0].ChildRecordingIds.Add("rec_refly");
            replacement.ActiveRecordingId = "rec_refly";

            RecordingStore.StashPendingTree(replacement);
            RecordingStore.CommitPendingTree();

            var rootCommitted = RecordingStore.CommittedRecordings
                .Single(r => r.RecordingId == "root");
            // Every preserved field comes through to the replacement instance.
            Assert.Equal("parsek_rw_launch_xyz", rootCommitted.RewindSaveFileName);
            Assert.Equal(12345.0, rootCommitted.RewindReservedFunds);
            Assert.Equal(7, rootCommitted.LastAppliedResourceIndex);
            Assert.True(rootCommitted.VesselSpawned);
            Assert.Equal(999u, rootCommitted.SpawnedVesselPersistentId);
            Assert.Equal("later_rec", rootCommitted.TerminalSpawnSupersededByRecordingId);
            Assert.Equal(1500.0, rootCommitted.MaxDistanceFromLaunch);
            Assert.Equal(2, rootCommitted.SpawnAttempts);
            Assert.Equal(1, rootCommitted.SpawnDeathCount);
            Assert.True(rootCommitted.WalkbackExhausted);
            Assert.True(rootCommitted.SpawnSuppressedByRewind);
            Assert.Equal("test-reason", rootCommitted.SpawnSuppressedByRewindReason);
            Assert.Equal(4, rootCommitted.SceneExitSituation);
        }

        [Fact]
        public void CommitPendingTree_SameTreeIdReplacement_PreservesAutoGroupName()
        {
            // Production regression: each Re-Fly merge invoked CommitTree's
            // replace path on a fresh pending tree whose AutoGenerated*GroupName
            // fields were null, so AutoGroupTreeRecordings re-routed through
            // GenerateUniqueGroupName, found "Kerbal X" still tagged on
            // first-commit recordings, and bumped to "Kerbal X (2)" / "(3)" /...
            // Each Re-Fly therefore landed in a distinct table group instead
            // of consolidating into the original mission's group.
            var original = MakeTreeWithBranch("refly_tree");
            original.TreeName = "Kerbal X";
            RecordingStore.CommitTree(original);

            string firstCommitGroup = RecordingStore.CommittedTrees[0].AutoGeneratedRootGroupName;
            Assert.Equal("Kerbal X", firstCommitGroup);

            var replacement = MakeTreeWithBranch("refly_tree");
            replacement.TreeName = "Kerbal X";
            replacement.Recordings["rec_refly"] = MakeRecording(
                "rec_refly",
                "refly_tree",
                "Re-Fly Vessel",
                4000,
                terminalState: TerminalState.Landed,
                vesselSnapshot: MakeMinimalSnapshot(),
                parentBranchPointId: "bp1");
            replacement.BranchPoints[0].ChildRecordingIds.Add("rec_refly");
            replacement.ActiveRecordingId = "rec_refly";

            RecordingStore.StashPendingTree(replacement);
            RecordingStore.CommitPendingTree();

            Assert.Single(RecordingStore.CommittedTrees);
            Assert.Equal("Kerbal X",
                RecordingStore.CommittedTrees[0].AutoGeneratedRootGroupName);
            // Spot-check: the new Re-Fly recording joined the existing "Kerbal X"
            // group, not "Kerbal X (2)".
            var reflyRec = RecordingStore.CommittedRecordings
                .Single(r => r.RecordingId == "rec_refly");
            Assert.Contains("Kerbal X", reflyRec.RecordingGroups);
            Assert.DoesNotContain(RecordingStore.CommittedRecordings,
                r => r.RecordingGroups != null
                    && r.RecordingGroups.Any(g => g.StartsWith("Kerbal X (")));
        }

        [Fact]
        public void StashPendingTree_Stashes()
        {
            var tree = MakeSimpleTree();

            RecordingStore.StashPendingTree(tree);

            Assert.True(RecordingStore.HasPendingTree);
            Assert.Equal("tree001", RecordingStore.PendingTree.Id);
        }

        [Fact]
        public void CommitPendingTree_CommitsAndClears()
        {
            var tree = MakeSimpleTree();
            RecordingStore.StashPendingTree(tree);

            RecordingStore.CommitPendingTree();

            Assert.False(RecordingStore.HasPendingTree);
            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Single(RecordingStore.CommittedTrees);
        }

        [Fact]
        public void DiscardPendingTree_ClearsWithoutCommitting()
        {
            var tree = MakeSimpleTree();
            RecordingStore.StashPendingTree(tree);

            RecordingStore.DiscardPendingTree();

            Assert.False(RecordingStore.HasPendingTree);
            Assert.Empty(RecordingStore.CommittedRecordings);
            Assert.Empty(RecordingStore.CommittedTrees);
        }

        [Fact]
        public void ClearCommitted_ClearsTreesToo()
        {
            var tree = MakeTreeWithBranch();
            RecordingStore.CommitTree(tree);

            Assert.Equal(3, RecordingStore.CommittedRecordings.Count);
            Assert.Single(RecordingStore.CommittedTrees);

            RecordingStore.ClearCommitted();

            Assert.Empty(RecordingStore.CommittedRecordings);
            Assert.Empty(RecordingStore.CommittedTrees);
        }

        // --- Serialization round-trip tests ---

        [Fact]
        public void SaveRecordingInto_IncludesMutableState()
        {
            var rec = MakeRecording("r1", "t1");
            rec.SpawnedVesselPersistentId = 42;
            rec.TerminalSpawnSupersededByRecordingId = "continued-tip";
            rec.VesselDestroyed = true;
            rec.LastAppliedResourceIndex = 7;
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100, latitude = 0, longitude = 0, altitude = 100,
                rotation = Quaternion.identity, velocity = Vector3.zero, bodyName = "Kerbin"
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 110, latitude = 0, longitude = 0, altitude = 200,
                rotation = Quaternion.identity, velocity = Vector3.zero, bodyName = "Kerbin"
            });

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);

            Assert.Equal("42", node.GetValue("spawnedPid"));
            Assert.Equal("continued-tip", node.GetValue("terminalSpawnSupersededBy"));
            Assert.Equal("True", node.GetValue("vesselDestroyed"));

            Assert.Equal("7", node.GetValue("lastResIdx"));
            Assert.Equal("2", node.GetValue("pointCount"));
        }

        [Fact]
        public void LoadRecordingFrom_RestoresMutableState()
        {
            var node = new ConfigNode("RECORDING");
            node.AddValue("recordingId", "r1");
            node.AddValue("vesselName", "Test");
            node.AddValue("vesselPersistentId", "100");
            node.AddValue("spawnedPid", "42");
            node.AddValue("terminalSpawnSupersededBy", "continued-tip");
            node.AddValue("vesselDestroyed", "True");

            node.AddValue("lastResIdx", "7");
            node.AddValue("pointCount", "10");
            node.AddValue("recordingFormatVersion", RecordingStore.CurrentRecordingFormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            node.AddValue("recordingSchemaGeneration", RecordingStore.CurrentRecordingSchemaGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
            node.AddValue("loopPlayback", "False");
            node.AddValue("loopIntervalSeconds", "10");

            var rec = new Recording();
            RecordingTree.LoadRecordingFrom(node, rec);

            Assert.Equal(42u, rec.SpawnedVesselPersistentId);
            Assert.Equal("continued-tip", rec.TerminalSpawnSupersededByRecordingId);
            Assert.True(rec.VesselDestroyed);
            Assert.Equal(7, rec.LastAppliedResourceIndex);
        }

        [Fact]
        public void TreeSaveLoad_MutableStateRoundTrips()
        {
            var tree = MakeTreeWithBranch();
            tree.Recordings["child1"].TerminalStateValue = TerminalState.Orbiting;
            tree.Recordings["child1"].SpawnedVesselPersistentId = 55555;
            tree.Recordings["child1"].VesselDestroyed = false;

            tree.Recordings["child1"].LastAppliedResourceIndex = 12;
            tree.Recordings["child2"].TerminalStateValue = TerminalState.Landed;
            tree.Recordings["child2"].SpawnedVesselPersistentId = 66666;
            tree.Recordings["child2"].LastAppliedResourceIndex = 5;

            // Save
            var treeNode = new ConfigNode("RECORDING_TREE");
            tree.Save(treeNode);

            // Load
            var restored = RecordingTree.Load(treeNode);

            // Verify mutable state round-tripped
            Assert.Equal(55555u, restored.Recordings["child1"].SpawnedVesselPersistentId);

            Assert.Equal(12, restored.Recordings["child1"].LastAppliedResourceIndex);

            Assert.Equal(66666u, restored.Recordings["child2"].SpawnedVesselPersistentId);

            Assert.Equal(5, restored.Recordings["child2"].LastAppliedResourceIndex);
        }

        [Fact]
        public void TreeSaveLoad_TerminalOrbitRoundTrips()
        {
            var tree = MakeSimpleTree();
            var rec = tree.Recordings["root"];
            rec.TerminalStateValue = TerminalState.Orbiting;
            rec.TerminalOrbitInclination = 28.5;
            rec.TerminalOrbitEccentricity = 0.001;
            rec.TerminalOrbitSemiMajorAxis = 700000.0;
            rec.TerminalOrbitLAN = 45.0;
            rec.TerminalOrbitArgumentOfPeriapsis = 90.0;
            rec.TerminalOrbitMeanAnomalyAtEpoch = 1.5;
            rec.TerminalOrbitEpoch = 18000.0;
            rec.TerminalOrbitBody = "Kerbin";

            var treeNode = new ConfigNode("RECORDING_TREE");
            tree.Save(treeNode);

            var restored = RecordingTree.Load(treeNode);
            var restoredRec = restored.Recordings["root"];

            Assert.Equal(TerminalState.Orbiting, restoredRec.TerminalStateValue);
            Assert.Equal(28.5, restoredRec.TerminalOrbitInclination);
            Assert.Equal(0.001, restoredRec.TerminalOrbitEccentricity);
            Assert.Equal(700000.0, restoredRec.TerminalOrbitSemiMajorAxis);
            Assert.Equal(45.0, restoredRec.TerminalOrbitLAN);
            Assert.Equal(90.0, restoredRec.TerminalOrbitArgumentOfPeriapsis);
            Assert.Equal(1.5, restoredRec.TerminalOrbitMeanAnomalyAtEpoch);
            Assert.Equal(18000.0, restoredRec.TerminalOrbitEpoch);
            Assert.Equal("Kerbin", restoredRec.TerminalOrbitBody);
        }

        [Fact]
        public void TreeSaveLoad_TerminalPositionRoundTrips()
        {
            var tree = MakeSimpleTree();
            var rec = tree.Recordings["root"];
            rec.TerminalStateValue = TerminalState.Landed;
            rec.TerminalPosition = new SurfacePosition
            {
                body = "Mun",
                latitude = -0.5,
                longitude = 23.4,
                altitude = 1234.5,
                rotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f),
                situation = SurfaceSituation.Landed
            };

            var treeNode = new ConfigNode("RECORDING_TREE");
            tree.Save(treeNode);

            var restored = RecordingTree.Load(treeNode);
            var restoredRec = restored.Recordings["root"];

            Assert.Equal(TerminalState.Landed, restoredRec.TerminalStateValue);
            Assert.NotNull(restoredRec.TerminalPosition);
            Assert.Equal("Mun", restoredRec.TerminalPosition.Value.body);
            Assert.Equal(-0.5, restoredRec.TerminalPosition.Value.latitude);
            Assert.Equal(23.4, restoredRec.TerminalPosition.Value.longitude);
        }

        // --- Deep tree tests ---

        [Fact]
        public void GetSpawnableLeaves_DeepChainOfSplits_OnlyFinalLeaves()
        {
            var tree = new RecordingTree
            {
                Id = "deep",
                TreeName = "Deep Tree",
                RootRecordingId = "root"
            };

            // root -> bp1 -> (mid, leaf1)
            // mid -> bp2 -> (leaf2, leaf3)
            tree.Recordings["root"] = MakeRecording("root", "deep",
                childBranchPointId: "bp1", vesselSnapshot: MakeMinimalSnapshot());
            tree.Recordings["mid"] = MakeRecording("mid", "deep",
                parentBranchPointId: "bp1", childBranchPointId: "bp2",
                vesselSnapshot: MakeMinimalSnapshot());
            tree.Recordings["leaf1"] = MakeRecording("leaf1", "deep",
                parentBranchPointId: "bp1",
                terminalState: TerminalState.Orbiting,
                vesselSnapshot: MakeMinimalSnapshot());
            tree.Recordings["leaf2"] = MakeRecording("leaf2", "deep",
                parentBranchPointId: "bp2",
                terminalState: TerminalState.Landed,
                vesselSnapshot: MakeMinimalSnapshot());
            tree.Recordings["leaf3"] = MakeRecording("leaf3", "deep",
                parentBranchPointId: "bp2",
                terminalState: TerminalState.Destroyed,
                vesselSnapshot: null);

            var spawnable = tree.GetSpawnableLeaves();
            var all = tree.GetAllLeaves();

            // leaf1 (Orbiting) + leaf2 (Landed) are spawnable
            // leaf3 (Destroyed) and mid/root (have children) are not
            Assert.Equal(2, spawnable.Count);
            Assert.Equal(3, all.Count); // leaf1, leaf2, leaf3

            var spawnableIds = new HashSet<string>();
            foreach (var l in spawnable) spawnableIds.Add(l.RecordingId);
            Assert.Contains("leaf1", spawnableIds);
            Assert.Contains("leaf2", spawnableIds);
            Assert.DoesNotContain("root", spawnableIds);
            Assert.DoesNotContain("mid", spawnableIds);
        }

        [Fact]
        public void ResetForTesting_ClearsTrees()
        {
            var tree = MakeSimpleTree();
            RecordingStore.CommitTree(tree);
            RecordingStore.StashPendingTree(MakeSimpleTree("pending_tree"));

            RecordingStore.ResetForTesting();

            Assert.Empty(RecordingStore.CommittedRecordings);
            Assert.Empty(RecordingStore.CommittedTrees);
            Assert.False(RecordingStore.HasPendingTree);
        }

        // ============================================================
        // B4: IsSpawnableLeaf — 5 missing truth-table cases
        // ============================================================

        [Fact]
        public void IsSpawnableLeaf_NullSnapshot_NoTerminal_False()
        {
            var rec = MakeRecording("r1", "t1",
                terminalState: null,
                vesselSnapshot: null);

            Assert.False(RecordingTree.IsSpawnableLeaf(rec));
        }

        [Fact]
        public void IsSpawnableLeaf_Recovered_WithSnapshot_False()
        {
            var rec = MakeRecording("r1", "t1",
                terminalState: TerminalState.Recovered,
                vesselSnapshot: MakeMinimalSnapshot());

            Assert.False(RecordingTree.IsSpawnableLeaf(rec));
        }

        [Fact]
        public void IsSpawnableLeaf_Landed_WithSnapshot_True()
        {
            var rec = MakeRecording("r1", "t1",
                terminalState: TerminalState.Landed,
                vesselSnapshot: MakeMinimalSnapshot());

            Assert.True(RecordingTree.IsSpawnableLeaf(rec));
        }

        [Fact]
        public void IsSpawnableLeaf_Splashed_WithSnapshot_True()
        {
            var rec = MakeRecording("r1", "t1",
                terminalState: TerminalState.Splashed,
                vesselSnapshot: MakeMinimalSnapshot());

            Assert.True(RecordingTree.IsSpawnableLeaf(rec));
        }

        [Fact]
        public void IsSpawnableLeaf_SubOrbital_WithSnapshot_True()
        {
            var rec = MakeRecording("r1", "t1",
                terminalState: TerminalState.SubOrbital,
                vesselSnapshot: MakeMinimalSnapshot());

            Assert.True(RecordingTree.IsSpawnableLeaf(rec));
        }

        // ============================================================
        // B6: GetAllLeaves vs GetSpawnableLeaves — Recovered leaf
        // ============================================================

        [Fact]
        public void GetAllLeaves_IncludesRecovered_GetSpawnableExcludes()
        {
            var tree = new RecordingTree
            {
                Id = "tree_recovered",
                TreeName = "Recovered Test",
                RootRecordingId = "root",
                ActiveRecordingId = null
            };

            // Root branches to two children
            tree.Recordings["root"] = MakeRecording("root", "tree_recovered",
                childBranchPointId: "bp1", vesselSnapshot: MakeMinimalSnapshot());

            // Leaf 1: Recovered, no snapshot
            tree.Recordings["leaf1"] = MakeRecording("leaf1", "tree_recovered",
                parentBranchPointId: "bp1",
                terminalState: TerminalState.Recovered,
                vesselSnapshot: null);

            // Leaf 2: Orbiting, with snapshot
            tree.Recordings["leaf2"] = MakeRecording("leaf2", "tree_recovered",
                parentBranchPointId: "bp1",
                terminalState: TerminalState.Orbiting,
                vesselSnapshot: MakeMinimalSnapshot());

            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp1",
                UT = 17050.0,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "root" },
                ChildRecordingIds = new List<string> { "leaf1", "leaf2" }
            });

            var allLeaves = tree.GetAllLeaves();
            var spawnableLeaves = tree.GetSpawnableLeaves();

            Assert.Equal(2, allLeaves.Count);
            Assert.Single(spawnableLeaves);
            Assert.Equal("leaf2", spawnableLeaves[0].RecordingId);
        }

        // ============================================================
        // B7: GetSpawnableLeaves after dock merge — DAG
        // ============================================================

        [Fact]
        public void GetSpawnableLeaves_DockMerge_DAG_ReturnsOnlyMergedChild()
        {
            var tree = new RecordingTree
            {
                Id = "tree_dag",
                TreeName = "DAG Test",
                RootRecordingId = "root",
                ActiveRecordingId = null
            };

            // root -> bp1 -> (childA, childB) -> bp2 (dock) -> merged
            tree.Recordings["root"] = MakeRecording("root", "tree_dag",
                childBranchPointId: "bp1", vesselSnapshot: MakeMinimalSnapshot());

            tree.Recordings["childA"] = MakeRecording("childA", "tree_dag",
                parentBranchPointId: "bp1", childBranchPointId: "bp2",
                terminalState: TerminalState.Docked,
                vesselSnapshot: MakeMinimalSnapshot());

            tree.Recordings["childB"] = MakeRecording("childB", "tree_dag",
                parentBranchPointId: "bp1", childBranchPointId: "bp2",
                terminalState: TerminalState.Docked,
                vesselSnapshot: MakeMinimalSnapshot());

            tree.Recordings["merged"] = MakeRecording("merged", "tree_dag",
                parentBranchPointId: "bp2",
                terminalState: TerminalState.Orbiting,
                vesselSnapshot: MakeMinimalSnapshot());

            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp1",
                UT = 200.0,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "root" },
                ChildRecordingIds = new List<string> { "childA", "childB" }
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp2",
                UT = 400.0,
                Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { "childA", "childB" },
                ChildRecordingIds = new List<string> { "merged" }
            });

            var spawnable = tree.GetSpawnableLeaves();

            Assert.Single(spawnable);
            Assert.Equal("merged", spawnable[0].RecordingId);

            // Verify root, childA, childB are NOT in spawnable
            var allLeaves = tree.GetAllLeaves();
            Assert.Single(allLeaves); // only "merged" has no child branch point
        }

        // ============================================================
        // D3: CommitTree(null) — no crash, no state change
        // ============================================================

        [Fact]
        public void CommitTree_Null_NoCrashNoStateChange()
        {
            int beforeRecordings = RecordingStore.CommittedRecordings.Count;
            int beforeTrees = RecordingStore.CommittedTrees.Count;

            RecordingStore.CommitTree(null);

            Assert.Equal(beforeRecordings, RecordingStore.CommittedRecordings.Count);
            Assert.Equal(beforeTrees, RecordingStore.CommittedTrees.Count);
        }

        // ============================================================
        // DetermineTerminalState(int, Vessel) overload tests
        // ============================================================

        [Theory]
        [InlineData(32, TerminalState.Orbiting)]   // ORBITING
        [InlineData(1, TerminalState.Landed)]       // LANDED
        [InlineData(2, TerminalState.Splashed)]     // SPLASHED
        [InlineData(16, TerminalState.SubOrbital)]  // SUB_ORBITAL
        [InlineData(8, TerminalState.SubOrbital)]   // FLYING
        [InlineData(64, TerminalState.SubOrbital)]  // ESCAPING
        [InlineData(4, TerminalState.Landed)]       // PRELAUNCH
        [InlineData(128, TerminalState.Docked)]     // DOCKED
        public void DetermineTerminalState_IntOverload_BackwardCompat(int situation, TerminalState expected)
        {
            // The single-argument (int) overload must continue to work as before
            var result = RecordingTree.DetermineTerminalState(situation);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData(32, TerminalState.Orbiting)]
        [InlineData(1, TerminalState.Landed)]
        [InlineData(2, TerminalState.Splashed)]
        [InlineData(16, TerminalState.SubOrbital)]
        [InlineData(8, TerminalState.SubOrbital)]
        [InlineData(64, TerminalState.SubOrbital)]
        [InlineData(4, TerminalState.Landed)]
        [InlineData(128, TerminalState.Docked)]
        public void DetermineTerminalState_VesselOverload_NullVessel_FallsThrough(int situation, TerminalState expected)
        {
            // When vessel is null, the (int, Vessel) overload should produce
            // the same result as the (int) overload — the orbit check is skipped.
            var result = RecordingTree.DetermineTerminalState(situation, null);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void DetermineTerminalState_UnexpectedSituation_DefaultsToSubOrbital()
        {
            // Both overloads should default to SubOrbital for unknown situation values
            var resultInt = RecordingTree.DetermineTerminalState(999);
            Assert.Equal(TerminalState.SubOrbital, resultInt);

            var resultVessel = RecordingTree.DetermineTerminalState(999, null);
            Assert.Equal(TerminalState.SubOrbital, resultVessel);
        }

        [Fact]
        public void DetermineTerminalStateFromOrbitEvidence_SubOrbitalBoundAboveSurface_ReturnsOrbiting()
        {
            var result = RecordingTree.DetermineTerminalStateFromOrbitEvidence(
                (int)Vessel.Situations.SUB_ORBITAL,
                eccentricity: 0.15,
                periapsisRadius: 650000.0,
                bodyRadius: 600000.0);

            Assert.Equal(TerminalState.Orbiting, result);
        }

        [Fact]
        public void DetermineTerminalStateFromOrbitEvidence_OrbitingSubSurfacePeriapsis_ReturnsSubOrbital()
        {
            var result = RecordingTree.DetermineTerminalStateFromOrbitEvidence(
                (int)Vessel.Situations.ORBITING,
                eccentricity: 0.289,
                periapsisRadius: 412757.0,
                bodyRadius: 600000.0);

            Assert.Equal(TerminalState.SubOrbital, result);
        }

        [Fact]
        public void DetermineTerminalStateFromOrbitEvidence_OrbitingUnbound_ReturnsSubOrbital()
        {
            var result = RecordingTree.DetermineTerminalStateFromOrbitEvidence(
                (int)Vessel.Situations.ORBITING,
                eccentricity: 1.02,
                periapsisRadius: 675000.0,
                bodyRadius: 600000.0);

            Assert.Equal(TerminalState.SubOrbital, result);
        }

        [Fact]
        public void DetermineTerminalStateFromOrbitEvidence_OrbitingAboveSurface_RemainsOrbiting()
        {
            var result = RecordingTree.DetermineTerminalStateFromOrbitEvidence(
                (int)Vessel.Situations.ORBITING,
                eccentricity: 0.05,
                periapsisRadius: 675000.0,
                bodyRadius: 600000.0);

            Assert.Equal(TerminalState.Orbiting, result);
        }

        [Fact]
        public void DetermineTerminalStateFromOrbitEvidence_OrbitingInsideAtmosphere_ReturnsSubOrbital()
        {
            var result = RecordingTree.DetermineTerminalStateFromOrbitEvidence(
                (int)Vessel.Situations.ORBITING,
                eccentricity: 0.0967,
                periapsisRadius: 636642.0,
                bodyRadius: 600000.0,
                bodyHasAtmosphere: true,
                atmosphereDepth: 70000.0);

            Assert.Equal(TerminalState.SubOrbital, result);
        }

        [Fact]
        public void DetermineTerminalStateFromOrbitEvidence_SubOrbitalInsideAtmosphere_RemainsSubOrbital()
        {
            var result = RecordingTree.DetermineTerminalStateFromOrbitEvidence(
                (int)Vessel.Situations.SUB_ORBITAL,
                eccentricity: 0.0967,
                periapsisRadius: 636642.0,
                bodyRadius: 600000.0,
                bodyHasAtmosphere: true,
                atmosphereDepth: 70000.0);

            Assert.Equal(TerminalState.SubOrbital, result);
        }

        // NOTE: The orbit-aware override path in DetermineTerminalState(int, Vessel)
        // requires a real KSP Vessel object with populated orbit data. The pure
        // orbit-evidence helpers above pin the classification logic; the dispatch
        // site is exercised via in-game testing.

        // ============================================================
        // IsBoundOrbitAboveAtmosphere — pure decision tests
        // ============================================================
        // Atmosphereless body: existing PeR > Radius behaviour is preserved.

        [Fact]
        public void IsBoundOrbitAboveAtmosphere_NoAtmosphere_AcceptsAboveSurface()
        {
            // ecc=0.5, Pe=200,000+10 m (above bare-rock surface), atmosphere=false
            bool result = RecordingTree.IsBoundOrbitAboveAtmosphere(
                eccentricity: 0.5,
                periapsisRadius: 200010,
                bodyRadius: 200000,
                bodyHasAtmosphere: false,
                atmosphereDepth: 0);
            Assert.True(result);
        }

        [Fact]
        public void IsBoundOrbitAboveAtmosphere_NoAtmosphere_RejectsBelowSurface()
        {
            // Pe inside the body
            bool result = RecordingTree.IsBoundOrbitAboveAtmosphere(
                eccentricity: 0.5,
                periapsisRadius: 199000,
                bodyRadius: 200000,
                bodyHasAtmosphere: false,
                atmosphereDepth: 0);
            Assert.False(result);
        }

        // Atmosphere body (Kerbin-like): orbit must clear atmosphereDepth, not just radius.

        [Fact]
        public void IsBoundOrbitAboveAtmosphere_Atmosphere_RejectsInsideAtmo()
        {
            // The user's bug scenario: SMA=669km, ecc=0.0967, PeR=636642
            // → Pe alt = 36642 m, well inside Kerbin's 70km atmosphere.
            bool result = RecordingTree.IsBoundOrbitAboveAtmosphere(
                eccentricity: 0.0967,
                periapsisRadius: 636642,
                bodyRadius: 600000,
                bodyHasAtmosphere: true,
                atmosphereDepth: 70000);
            Assert.False(result);
        }

        [Fact]
        public void IsBoundOrbitAboveAtmosphere_Atmosphere_AcceptsAboveAtmo()
        {
            // Pe at 80 km altitude on Kerbin (above 70km atmosphere top)
            bool result = RecordingTree.IsBoundOrbitAboveAtmosphere(
                eccentricity: 0.05,
                periapsisRadius: 680000,
                bodyRadius: 600000,
                bodyHasAtmosphere: true,
                atmosphereDepth: 70000);
            Assert.True(result);
        }

        [Fact]
        public void IsBoundOrbitAboveAtmosphere_Atmosphere_AtAtmosphereTopExactly()
        {
            // Pe == atmosphereDepth → false (strict inequality; atmosphereDepth is the
            // boundary at which drag goes to zero in KSP, so the orbit must be strictly
            // above to be stable).
            bool result = RecordingTree.IsBoundOrbitAboveAtmosphere(
                eccentricity: 0.05,
                periapsisRadius: 670000,
                bodyRadius: 600000,
                bodyHasAtmosphere: true,
                atmosphereDepth: 70000);
            Assert.False(result);
        }

        [Fact]
        public void IsBoundOrbitAboveAtmosphere_Hyperbolic_AlwaysFalse()
        {
            // Hyperbolic / parabolic orbits are never bound regardless of Pe.
            bool result = RecordingTree.IsBoundOrbitAboveAtmosphere(
                eccentricity: 1.2,
                periapsisRadius: 800000,
                bodyRadius: 600000,
                bodyHasAtmosphere: true,
                atmosphereDepth: 70000);
            Assert.False(result);
        }

        [Fact]
        public void IsBoundOrbitAboveAtmosphere_ParabolicEdge_RejectsAtEcc1()
        {
            // Ecc == 1 is the parabolic boundary — also unbound.
            bool result = RecordingTree.IsBoundOrbitAboveAtmosphere(
                eccentricity: 1.0,
                periapsisRadius: 800000,
                bodyRadius: 600000,
                bodyHasAtmosphere: true,
                atmosphereDepth: 70000);
            Assert.False(result);
        }

        // ============================================================
        // vesselSwitchPending flag — integration test placeholder
        // ============================================================

        // NOTE: vesselSwitchPending is a private static field in ParsekScenario.
        // It is set by OnVesselSwitching (GameEvents callback) and consumed in
        // OnLoad to distinguish vessel-switch FLIGHT->FLIGHT reloads from reverts.
        // Testing this requires:
        //   1. A running KSP instance with GameEvents wired up
        //   2. Triggering OnVesselSwitching (vessel switch in flight)
        //   3. Verifying that the subsequent OnLoad does NOT run revert cleanup
        // This must be verified via in-game integration testing, not unit tests.
    }
}
