using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class MergeEventDetectionTests
    {
        public MergeEventDetectionTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        #region BuildMergeBranchData — Dock (Two Parents)

        [Fact]
        public void BuildMergeBranchData_Dock_TwoParents_CreatesCorrectBranchPoint()
        {
            var parentIds = new List<string> { "parent_a", "parent_b" };
            var (bp, mergedChild) = ParsekFlight.BuildMergeBranchData(
                parentRecordingIds: parentIds,
                treeId: "tree_1",
                mergeUT: 5000.0,
                branchType: BranchPointType.Dock,
                mergedVesselPid: 100,
                mergedVesselName: "Docked Vessel");

            Assert.NotNull(bp);
            Assert.Equal(BranchPointType.Dock, bp.type);
            Assert.Equal(5000.0, bp.ut);
            Assert.Equal(2, bp.parentRecordingIds.Count);
            Assert.Contains("parent_a", bp.parentRecordingIds);
            Assert.Contains("parent_b", bp.parentRecordingIds);
            Assert.Single(bp.childRecordingIds);
            Assert.NotNull(bp.id);
            Assert.NotEmpty(bp.id);
        }

        [Fact]
        public void BuildMergeBranchData_Dock_SingleParent_CreatesCorrectBranchPoint()
        {
            var parentIds = new List<string> { "parent_a" };
            var (bp, mergedChild) = ParsekFlight.BuildMergeBranchData(
                parentRecordingIds: parentIds,
                treeId: "tree_1",
                mergeUT: 6000.0,
                branchType: BranchPointType.Dock,
                mergedVesselPid: 200,
                mergedVesselName: "Foreign Dock");

            Assert.NotNull(bp);
            Assert.Equal(BranchPointType.Dock, bp.type);
            Assert.Single(bp.parentRecordingIds);
            Assert.Equal("parent_a", bp.parentRecordingIds[0]);
            Assert.Single(bp.childRecordingIds);
        }

        #endregion

        #region BuildMergeBranchData — Board (Two Parents)

        [Fact]
        public void BuildMergeBranchData_Board_TwoParents_CreatesCorrectBranchPoint()
        {
            var parentIds = new List<string> { "kerbal_rec", "vessel_rec" };
            var (bp, mergedChild) = ParsekFlight.BuildMergeBranchData(
                parentRecordingIds: parentIds,
                treeId: "tree_2",
                mergeUT: 7000.0,
                branchType: BranchPointType.Board,
                mergedVesselPid: 300,
                mergedVesselName: "Boarded Vessel");

            Assert.NotNull(bp);
            Assert.Equal(BranchPointType.Board, bp.type);
            Assert.Equal(7000.0, bp.ut);
            Assert.Equal(2, bp.parentRecordingIds.Count);
            Assert.Contains("kerbal_rec", bp.parentRecordingIds);
            Assert.Contains("vessel_rec", bp.parentRecordingIds);
            Assert.Single(bp.childRecordingIds);
        }

        #endregion

        #region BuildMergeBranchData — Child Recording Fields

        [Fact]
        public void BuildMergeBranchData_Dock_ChildRecordingHasCorrectFields()
        {
            var parentIds = new List<string> { "parent_a", "parent_b" };
            var (bp, mergedChild) = ParsekFlight.BuildMergeBranchData(
                parentRecordingIds: parentIds,
                treeId: "tree_3",
                mergeUT: 8000.0,
                branchType: BranchPointType.Dock,
                mergedVesselPid: 400,
                mergedVesselName: "Combined Ship");

            Assert.NotNull(mergedChild);
            Assert.Equal("tree_3", mergedChild.TreeId);
            Assert.Equal(400u, mergedChild.VesselPersistentId);
            Assert.Equal("Combined Ship", mergedChild.VesselName);
            Assert.Equal(bp.id, mergedChild.ParentBranchPointId);
            Assert.Equal(8000.0, mergedChild.ExplicitStartUT);
            // Merge children don't have EVA-specific fields
            Assert.Null(mergedChild.EvaCrewName);
            Assert.Null(mergedChild.ParentRecordingId);
        }

        #endregion

        #region BuildMergeBranchData — ID Uniqueness and Linkage

        [Fact]
        public void BuildMergeBranchData_GeneratesUniqueIds()
        {
            var parentIds = new List<string> { "p1", "p2" };
            var (bp1, child1) = ParsekFlight.BuildMergeBranchData(
                parentIds, "tree", 1000.0, BranchPointType.Dock, 10, "Ship A");

            var (bp2, child2) = ParsekFlight.BuildMergeBranchData(
                parentIds, "tree", 2000.0, BranchPointType.Dock, 20, "Ship B");

            // All IDs should be unique across calls
            var allIds = new HashSet<string>
            {
                bp1.id, child1.RecordingId,
                bp2.id, child2.RecordingId
            };
            Assert.Equal(4, allIds.Count);
        }

        [Fact]
        public void BuildMergeBranchData_BpIdMatchesChildParentBranchPointId()
        {
            var parentIds = new List<string> { "p1" };
            var (bp, child) = ParsekFlight.BuildMergeBranchData(
                parentIds, "tree", 1000.0, BranchPointType.Board, 50, "Vessel");

            Assert.Equal(bp.id, child.ParentBranchPointId);
        }

        [Fact]
        public void BuildMergeBranchData_ChildIdMatchesBpChildRecordingIds()
        {
            var parentIds = new List<string> { "p1", "p2" };
            var (bp, child) = ParsekFlight.BuildMergeBranchData(
                parentIds, "tree", 3000.0, BranchPointType.Dock, 60, "Merged");

            Assert.Single(bp.childRecordingIds);
            Assert.Equal(child.RecordingId, bp.childRecordingIds[0]);
        }

        #endregion

        #region BranchPoint Serialization Round-Trips (Merge-Specific)

        [Fact]
        public void BranchPoint_Dock_TwoParents_SerializesRoundTrip()
        {
            var parentIds = new List<string> { "parent_x", "parent_y" };
            var (bp, child) = ParsekFlight.BuildMergeBranchData(
                parentIds, "tree", 9000.0, BranchPointType.Dock, 500, "Docked Ship");

            // Serialize
            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);

            // Deserialize
            var loaded = RecordingTree.LoadBranchPointFrom(node);

            Assert.Equal(bp.id, loaded.id);
            Assert.Equal(bp.ut, loaded.ut);
            Assert.Equal(BranchPointType.Dock, loaded.type);
            Assert.Equal(2, loaded.parentRecordingIds.Count);
            Assert.Contains("parent_x", loaded.parentRecordingIds);
            Assert.Contains("parent_y", loaded.parentRecordingIds);
            Assert.Single(loaded.childRecordingIds);
            Assert.Equal(bp.childRecordingIds[0], loaded.childRecordingIds[0]);
        }

        [Fact]
        public void BranchPoint_Board_SingleParent_SerializesRoundTrip()
        {
            var parentIds = new List<string> { "kerbal_only" };
            var (bp, child) = ParsekFlight.BuildMergeBranchData(
                parentIds, "tree", 10000.0, BranchPointType.Board, 600, "Boarded Ship");

            // Serialize
            var node = new ConfigNode("BRANCH_POINT");
            RecordingTree.SaveBranchPointInto(node, bp);

            // Deserialize
            var loaded = RecordingTree.LoadBranchPointFrom(node);

            Assert.Equal(bp.id, loaded.id);
            Assert.Equal(bp.ut, loaded.ut);
            Assert.Equal(BranchPointType.Board, loaded.type);
            Assert.Single(loaded.parentRecordingIds);
            Assert.Equal("kerbal_only", loaded.parentRecordingIds[0]);
            Assert.Single(loaded.childRecordingIds);
            Assert.Equal(bp.childRecordingIds[0], loaded.childRecordingIds[0]);
        }

        #endregion

        #region RebuildBackgroundMap — Merge-Specific Exclusions

        [Fact]
        public void RebuildBackgroundMap_ExcludesDockedRecordings()
        {
            var tree = new RecordingTree
            {
                Id = "tree_dock",
                TreeName = "Dock Test",
                RootRecordingId = "root",
                ActiveRecordingId = "active_child"
            };

            // Docked parent recording (should be excluded)
            tree.Recordings["docked_rec"] = new RecordingStore.Recording
            {
                RecordingId = "docked_rec",
                VesselPersistentId = 100,
                TerminalStateValue = TerminalState.Docked,
                TreeId = tree.Id
            };

            // Active child (should be excluded -- it's the active recording)
            tree.Recordings["active_child"] = new RecordingStore.Recording
            {
                RecordingId = "active_child",
                VesselPersistentId = 200,
                TreeId = tree.Id
            };

            tree.RebuildBackgroundMap();

            Assert.False(tree.BackgroundMap.ContainsKey(100)); // Docked -> excluded
            Assert.False(tree.BackgroundMap.ContainsKey(200)); // Active -> excluded
        }

        [Fact]
        public void RebuildBackgroundMap_ExcludesBoardedRecordings()
        {
            var tree = new RecordingTree
            {
                Id = "tree_board",
                TreeName = "Board Test",
                RootRecordingId = "root",
                ActiveRecordingId = "child"
            };

            // Boarded parent recording (should be excluded)
            tree.Recordings["boarded_rec"] = new RecordingStore.Recording
            {
                RecordingId = "boarded_rec",
                VesselPersistentId = 300,
                TerminalStateValue = TerminalState.Boarded,
                TreeId = tree.Id
            };

            tree.Recordings["child"] = new RecordingStore.Recording
            {
                RecordingId = "child",
                VesselPersistentId = 400,
                TreeId = tree.Id
            };

            tree.RebuildBackgroundMap();

            Assert.False(tree.BackgroundMap.ContainsKey(300)); // Boarded -> excluded
        }

        [Fact]
        public void RebuildBackgroundMap_ExcludesRecordingsWithChildBranchPoint()
        {
            var tree = new RecordingTree
            {
                Id = "tree_cbp",
                TreeName = "ChildBP Test",
                RootRecordingId = "root",
                ActiveRecordingId = "active"
            };

            // Recording that has already branched (should be excluded)
            tree.Recordings["branched_rec"] = new RecordingStore.Recording
            {
                RecordingId = "branched_rec",
                VesselPersistentId = 500,
                ChildBranchPointId = "some_bp",
                TreeId = tree.Id
            };

            tree.Recordings["active"] = new RecordingStore.Recording
            {
                RecordingId = "active",
                VesselPersistentId = 600,
                TreeId = tree.Id
            };

            tree.RebuildBackgroundMap();

            Assert.False(tree.BackgroundMap.ContainsKey(500)); // Has ChildBranchPointId -> excluded
        }

        [Fact]
        public void RebuildBackgroundMap_IncludesActiveRecordingsWithNoTerminalState()
        {
            var tree = new RecordingTree
            {
                Id = "tree_include",
                TreeName = "Include Test",
                RootRecordingId = "root",
                ActiveRecordingId = "active"
            };

            // Background recording: no terminal state, no child branch, not active
            tree.Recordings["bg_rec"] = new RecordingStore.Recording
            {
                RecordingId = "bg_rec",
                VesselPersistentId = 700,
                TreeId = tree.Id
            };

            tree.Recordings["active"] = new RecordingStore.Recording
            {
                RecordingId = "active",
                VesselPersistentId = 800,
                TreeId = tree.Id
            };

            tree.RebuildBackgroundMap();

            Assert.True(tree.BackgroundMap.ContainsKey(700)); // Should be in BackgroundMap
            Assert.Equal("bg_rec", tree.BackgroundMap[700]);
            Assert.False(tree.BackgroundMap.ContainsKey(800)); // Active -> excluded
        }

        #endregion

        #region BranchPointType enum values

        [Fact]
        public void BranchPointType_Dock_HasValue2()
        {
            Assert.Equal(2, (int)BranchPointType.Dock);
        }

        [Fact]
        public void BranchPointType_Board_HasValue3()
        {
            Assert.Equal(3, (int)BranchPointType.Board);
        }

        [Fact]
        public void BranchPointType_Dock_Serialization_RoundTrips()
        {
            int serialized = (int)BranchPointType.Dock;
            Assert.True(Enum.IsDefined(typeof(BranchPointType), serialized));
            Assert.Equal(BranchPointType.Dock, (BranchPointType)serialized);
        }

        [Fact]
        public void BranchPointType_Board_Serialization_RoundTrips()
        {
            int serialized = (int)BranchPointType.Board;
            Assert.True(Enum.IsDefined(typeof(BranchPointType), serialized));
            Assert.Equal(BranchPointType.Board, (BranchPointType)serialized);
        }

        #endregion

        #region Merge tree integration scenario

        [Fact]
        public void MergeIntegration_TwoParentDock_ProducesCorrectTreeStructure()
        {
            // Simulate: split creates two children, then dock merges them back
            var tree = new RecordingTree
            {
                Id = "tree_merge_int",
                TreeName = "Merge Integration",
                RootRecordingId = "root"
            };

            // Root recording (ended by split)
            tree.Recordings["root"] = new RecordingStore.Recording
            {
                RecordingId = "root",
                VesselPersistentId = 50,
                TreeId = tree.Id
            };

            // Create split branch
            var (splitBp, activeChild, bgChild) = ParsekFlight.BuildSplitBranchData(
                "root", tree.Id, 1000.0, BranchPointType.Undock,
                100, "Ship A", 200, "Ship B");

            tree.Recordings["root"].ChildBranchPointId = splitBp.id;
            tree.Recordings[activeChild.RecordingId] = activeChild;
            tree.Recordings[bgChild.RecordingId] = bgChild;
            tree.BranchPoints.Add(splitBp);
            tree.ActiveRecordingId = activeChild.RecordingId;
            tree.BackgroundMap[200] = bgChild.RecordingId;

            // Now create merge branch (dock: both children merge back)
            var mergeParents = new List<string> { activeChild.RecordingId, bgChild.RecordingId };
            var (mergeBp, mergedChild) = ParsekFlight.BuildMergeBranchData(
                mergeParents, tree.Id, 2000.0, BranchPointType.Dock,
                100, "Docked Ship");

            // Wire up parent recordings
            activeChild.ChildBranchPointId = mergeBp.id;
            activeChild.ExplicitEndUT = 2000.0;
            activeChild.TerminalStateValue = TerminalState.Docked;

            bgChild.ChildBranchPointId = mergeBp.id;
            bgChild.ExplicitEndUT = 2000.0;
            bgChild.TerminalStateValue = TerminalState.Docked;

            tree.Recordings[mergedChild.RecordingId] = mergedChild;
            tree.BranchPoints.Add(mergeBp);
            tree.ActiveRecordingId = mergedChild.RecordingId;
            tree.BackgroundMap.Remove(200); // absorbed vessel removed

            // Verify tree structure
            Assert.Equal(4, tree.Recordings.Count); // root + 2 split children + 1 merge child
            Assert.Equal(2, tree.BranchPoints.Count); // split + merge

            // Split branch point
            Assert.Single(splitBp.parentRecordingIds);
            Assert.Equal(2, splitBp.childRecordingIds.Count);

            // Merge branch point
            Assert.Equal(2, mergeBp.parentRecordingIds.Count);
            Assert.Single(mergeBp.childRecordingIds);
            Assert.Equal(mergedChild.RecordingId, mergeBp.childRecordingIds[0]);

            // Parent recordings are terminated
            Assert.Equal(TerminalState.Docked, activeChild.TerminalStateValue);
            Assert.Equal(TerminalState.Docked, bgChild.TerminalStateValue);
            Assert.Equal(mergeBp.id, activeChild.ChildBranchPointId);
            Assert.Equal(mergeBp.id, bgChild.ChildBranchPointId);

            // Merged child links back
            Assert.Equal(mergeBp.id, mergedChild.ParentBranchPointId);
            Assert.Equal(mergedChild.RecordingId, tree.ActiveRecordingId);

            // Rebuild and verify BackgroundMap exclusions
            tree.RebuildBackgroundMap();
            Assert.False(tree.BackgroundMap.ContainsKey(50));  // root: has ChildBranchPointId
            Assert.False(tree.BackgroundMap.ContainsKey(100)); // active child: Docked terminal / active merged child has same PID
            Assert.False(tree.BackgroundMap.ContainsKey(200)); // bg child: Docked terminal
        }

        [Fact]
        public void MergeIntegration_SingleParentBoard_ProducesCorrectTreeStructure()
        {
            // EVA kerbal boards a foreign vessel (single-parent merge)
            var tree = new RecordingTree
            {
                Id = "tree_board_int",
                TreeName = "Board Integration",
                RootRecordingId = "root"
            };

            // Root recording (ship, ended by EVA split)
            tree.Recordings["root"] = new RecordingStore.Recording
            {
                RecordingId = "root",
                VesselPersistentId = 50,
                TreeId = tree.Id
            };

            // EVA split
            var (splitBp, kerbalChild, shipChild) = ParsekFlight.BuildSplitBranchData(
                "root", tree.Id, 1000.0, BranchPointType.EVA,
                300, "Jeb Kerman", 50, "My Rocket",
                evaCrewName: "Jeb Kerman");

            tree.Recordings["root"].ChildBranchPointId = splitBp.id;
            tree.Recordings[kerbalChild.RecordingId] = kerbalChild;
            tree.Recordings[shipChild.RecordingId] = shipChild;
            tree.BranchPoints.Add(splitBp);
            tree.ActiveRecordingId = kerbalChild.RecordingId;
            tree.BackgroundMap[50] = shipChild.RecordingId;

            // Board merge: kerbal boards a FOREIGN vessel (not in tree)
            var boardParents = new List<string> { kerbalChild.RecordingId }; // single parent
            var (boardBp, boardedChild) = ParsekFlight.BuildMergeBranchData(
                boardParents, tree.Id, 1500.0, BranchPointType.Board,
                900, "Foreign Vessel"); // foreign vessel PID 900

            kerbalChild.ChildBranchPointId = boardBp.id;
            kerbalChild.ExplicitEndUT = 1500.0;
            kerbalChild.TerminalStateValue = TerminalState.Boarded;

            tree.Recordings[boardedChild.RecordingId] = boardedChild;
            tree.BranchPoints.Add(boardBp);
            tree.ActiveRecordingId = boardedChild.RecordingId;

            // Verify
            Assert.Equal(4, tree.Recordings.Count); // root + kerbal + ship + boarded child
            Assert.Equal(2, tree.BranchPoints.Count);

            // Board branch point has 1 parent (foreign vessel has no recording)
            Assert.Single(boardBp.parentRecordingIds);
            Assert.Single(boardBp.childRecordingIds);
            Assert.Equal(BranchPointType.Board, boardBp.type);

            // Kerbal recording is terminated
            Assert.Equal(TerminalState.Boarded, kerbalChild.TerminalStateValue);

            // Ship child is still in BackgroundMap (not affected by board merge with foreign vessel)
            tree.RebuildBackgroundMap();
            Assert.True(tree.BackgroundMap.ContainsKey(50));
        }

        #endregion
    }
}
