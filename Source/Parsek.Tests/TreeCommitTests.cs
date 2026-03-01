using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class TreeCommitTests
    {
        public TreeCommitTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        // --- Helper methods ---

        private RecordingStore.Recording MakeRecording(string id, string treeId,
            string vesselName = "TestVessel", uint pid = 0,
            TerminalState? terminalState = null,
            ConfigNode vesselSnapshot = null,
            string childBranchPointId = null,
            string parentBranchPointId = null)
        {
            return new RecordingStore.Recording
            {
                RecordingId = id,
                TreeId = treeId,
                VesselName = vesselName,
                VesselPersistentId = pid,
                TerminalStateValue = terminalState,
                VesselSnapshot = vesselSnapshot,
                ChildBranchPointId = childBranchPointId,
                ParentBranchPointId = parentBranchPointId
            };
        }

        private ConfigNode MakeMinimalSnapshot()
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("name", "Test Vessel");
            node.AddValue("pid", "12345");
            return node;
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
                id = "bp1",
                ut = 17050.0,
                type = BranchPointType.Undock,
                parentRecordingIds = new List<string> { "root" },
                childRecordingIds = new List<string> { "child1", "child2" }
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
            rec.VesselDestroyed = true;
            rec.TakenControl = true;
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
            Assert.Equal("True", node.GetValue("vesselDestroyed"));
            Assert.Equal("True", node.GetValue("takenControl"));
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
            node.AddValue("vesselDestroyed", "True");
            node.AddValue("takenControl", "True");
            node.AddValue("lastResIdx", "7");
            node.AddValue("pointCount", "10");
            node.AddValue("recordingFormatVersion", "4");
            node.AddValue("ghostGeometryVersion", "1");
            node.AddValue("loopPlayback", "False");
            node.AddValue("loopPauseSeconds", "10");
            node.AddValue("ghostGeometryStrategy", "stub_v1");
            node.AddValue("ghostGeometryProbeStatus", "unknown");
            node.AddValue("ghostGeometryAvailable", "False");

            var rec = new RecordingStore.Recording();
            RecordingTree.LoadRecordingFrom(node, rec);

            Assert.Equal(42u, rec.SpawnedVesselPersistentId);
            Assert.True(rec.VesselDestroyed);
            Assert.True(rec.TakenControl);
            Assert.Equal(7, rec.LastAppliedResourceIndex);
        }

        [Fact]
        public void TreeSaveLoad_MutableStateRoundTrips()
        {
            var tree = MakeTreeWithBranch();
            tree.Recordings["child1"].TerminalStateValue = TerminalState.Orbiting;
            tree.Recordings["child1"].SpawnedVesselPersistentId = 55555;
            tree.Recordings["child1"].VesselDestroyed = false;
            tree.Recordings["child1"].TakenControl = true;
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
            Assert.True(restored.Recordings["child1"].TakenControl);
            Assert.Equal(12, restored.Recordings["child1"].LastAppliedResourceIndex);

            Assert.Equal(66666u, restored.Recordings["child2"].SpawnedVesselPersistentId);
            Assert.False(restored.Recordings["child2"].TakenControl);
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
                id = "bp1",
                ut = 17050.0,
                type = BranchPointType.Undock,
                parentRecordingIds = new List<string> { "root" },
                childRecordingIds = new List<string> { "leaf1", "leaf2" }
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
                id = "bp1",
                ut = 200.0,
                type = BranchPointType.Undock,
                parentRecordingIds = new List<string> { "root" },
                childRecordingIds = new List<string> { "childA", "childB" }
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                id = "bp2",
                ut = 400.0,
                type = BranchPointType.Dock,
                parentRecordingIds = new List<string> { "childA", "childB" },
                childRecordingIds = new List<string> { "merged" }
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
    }
}
