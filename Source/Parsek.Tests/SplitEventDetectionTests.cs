using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class SplitEventDetectionTests : System.IDisposable
    {
        public SplitEventDetectionTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        #region IsTrackableVesselType

        [Fact]
        public void IsTrackableVesselType_SpaceObject_ReturnsTrue()
        {
            Assert.True(ParsekFlight.IsTrackableVesselType(VesselType.SpaceObject));
        }

        [Fact]
        public void IsTrackableVesselType_Debris_ReturnsFalse()
        {
            Assert.False(ParsekFlight.IsTrackableVesselType(VesselType.Debris));
        }

        [Fact]
        public void IsTrackableVesselType_Ship_ReturnsFalse()
        {
            // Ship alone is not trackable — needs module check (requires Unity)
            Assert.False(ParsekFlight.IsTrackableVesselType(VesselType.Ship));
        }

        [Fact]
        public void IsTrackableVesselType_Probe_ReturnsFalse()
        {
            // Probe type alone is not trackable — needs module check
            Assert.False(ParsekFlight.IsTrackableVesselType(VesselType.Probe));
        }

        [Fact]
        public void IsTrackableVesselType_EVA_ReturnsTrue()
        {
            // EVA kerbals are directly controllable by the player and must count
            // as a controllable output for split-event classification, even though
            // their part carries KerbalEVA rather than ModuleCommand. Without this
            // an EVA split classifies as single-controllable, no RewindPoint is
            // authored, and a destroyed EVA kerbal cannot become an Unfinished
            // Flight (#XXX).
            Assert.True(ParsekFlight.IsTrackableVesselType(VesselType.EVA));
        }

        #endregion

        #region Deferred breakup scan filtering

        [Fact]
        public void IsSpaceObjectLikeBreakupScanReject_SpaceObject_ReturnsTrue()
        {
            bool rejected = ParsekFlight.IsSpaceObjectLikeBreakupScanReject(
                VesselType.SpaceObject,
                Array.Empty<string>(),
                Array.Empty<string>(),
                out string reason);

            Assert.True(rejected);
            Assert.Equal("space-object-type", reason);
        }

        [Fact]
        public void IsSpaceObjectLikeBreakupScanReject_AsteroidModule_ReturnsTrue()
        {
            bool rejected = ParsekFlight.IsSpaceObjectLikeBreakupScanReject(
                VesselType.Ship,
                new[] { "probeCoreOcto" },
                new[] { "ModuleAsteroid" },
                out string reason);

            Assert.True(rejected);
            Assert.Equal("asteroid-comet-module", reason);
        }

        [Fact]
        public void IsSpaceObjectLikeBreakupScanReject_PotatoRoidPart_ReturnsTrue()
        {
            bool rejected = ParsekFlight.IsSpaceObjectLikeBreakupScanReject(
                VesselType.Ship,
                new[] { "PotatoRoid" },
                Array.Empty<string>(),
                out string reason);

            Assert.True(rejected);
            Assert.Equal("asteroid-comet-part", reason);
        }

        [Fact]
        public void IsSpaceObjectLikeBreakupScanReject_NormalShip_ReturnsFalse()
        {
            bool rejected = ParsekFlight.IsSpaceObjectLikeBreakupScanReject(
                VesselType.Ship,
                new[] { "mk1pod" },
                new[] { "ModuleCommand" },
                out string reason);

            Assert.False(rejected);
            Assert.Null(reason);
        }

        #endregion

        #region GetEvaBackgroundInitialEnvironmentOverride

        [Fact]
        public void GetEvaBackgroundInitialEnvironmentOverride_LandedStationaryShip_ReturnsSurfaceStationary()
        {
            var result = ParsekFlight.GetEvaBackgroundInitialEnvironmentOverride(
                BranchPointType.EVA,
                backgroundChildIsEva: true,
                activeSituation: (int)Vessel.Situations.LANDED,
                backgroundSrfSpeed: 0.0);

            Assert.Equal(SegmentEnvironment.SurfaceStationary, result);
        }

        [Fact]
        public void GetEvaBackgroundInitialEnvironmentOverride_LandedMovingShip_ReturnsSurfaceMobile()
        {
            var result = ParsekFlight.GetEvaBackgroundInitialEnvironmentOverride(
                BranchPointType.EVA,
                backgroundChildIsEva: true,
                activeSituation: (int)Vessel.Situations.LANDED,
                backgroundSrfSpeed: 2.0);

            Assert.Equal(SegmentEnvironment.SurfaceMobile, result);
        }

        [Fact]
        public void GetEvaBackgroundInitialEnvironmentOverride_UsesBackgroundEvaSpeed()
        {
            var result = ParsekFlight.GetEvaBackgroundInitialEnvironmentOverride(
                BranchPointType.EVA,
                backgroundChildIsEva: true,
                activeSituation: (int)Vessel.Situations.LANDED,
                backgroundSrfSpeed: 0.0);

            Assert.Equal(SegmentEnvironment.SurfaceStationary, result);
        }

        [Fact]
        public void GetEvaBackgroundInitialEnvironmentOverride_NonSurfaceShip_ReturnsNull()
        {
            var result = ParsekFlight.GetEvaBackgroundInitialEnvironmentOverride(
                BranchPointType.EVA,
                backgroundChildIsEva: true,
                activeSituation: (int)Vessel.Situations.FLYING,
                backgroundSrfSpeed: 50.0);

            Assert.Null(result);
        }

        [Fact]
        public void GetEvaBackgroundInitialEnvironmentOverride_NonEvaBackgroundChild_ReturnsNull()
        {
            var result = ParsekFlight.GetEvaBackgroundInitialEnvironmentOverride(
                BranchPointType.EVA,
                backgroundChildIsEva: false,
                activeSituation: (int)Vessel.Situations.LANDED,
                backgroundSrfSpeed: 0.0);

            Assert.Null(result);
        }

        #endregion

        #region BuildSplitBranchData — Undock

        [Fact]
        public void BuildSplitBranchData_Undock_CreatesCorrectBranchPoint()
        {
            var (bp, activeChild, bgChild) = ParsekFlight.BuildSplitBranchData(
                parentRecordingId: "parent_rec",
                treeId: "tree_1",
                branchUT: 1000.0,
                branchType: BranchPointType.Undock,
                activeVesselPid: 100,
                activeVesselName: "My Ship",
                backgroundVesselPid: 200,
                backgroundVesselName: "Debris Ship");

            Assert.NotNull(bp);
            Assert.Equal(BranchPointType.Undock, bp.Type);
            Assert.Equal(1000.0, bp.UT);
            Assert.Single(bp.ParentRecordingIds);
            Assert.Equal("parent_rec", bp.ParentRecordingIds[0]);
            Assert.Equal(2, bp.ChildRecordingIds.Count);
            Assert.NotNull(bp.Id);
            Assert.NotEmpty(bp.Id);
        }

        [Fact]
        public void BuildSplitBranchData_Undock_CreatesCorrectChildRecordings()
        {
            var (bp, activeChild, bgChild) = ParsekFlight.BuildSplitBranchData(
                parentRecordingId: "parent_rec",
                treeId: "tree_1",
                branchUT: 1000.0,
                branchType: BranchPointType.Undock,
                activeVesselPid: 100,
                activeVesselName: "My Ship",
                backgroundVesselPid: 200,
                backgroundVesselName: "Undocked Part");

            // Active child
            Assert.NotNull(activeChild);
            Assert.Equal("tree_1", activeChild.TreeId);
            Assert.Equal(100u, activeChild.VesselPersistentId);
            Assert.Equal("My Ship", activeChild.VesselName);
            Assert.Equal(bp.Id, activeChild.ParentBranchPointId);
            Assert.Equal(1000.0, activeChild.ExplicitStartUT);
            Assert.Null(activeChild.EvaCrewName);
            Assert.Null(activeChild.ParentRecordingId);

            // Background child
            Assert.NotNull(bgChild);
            Assert.Equal("tree_1", bgChild.TreeId);
            Assert.Equal(200u, bgChild.VesselPersistentId);
            Assert.Equal("Undocked Part", bgChild.VesselName);
            Assert.Equal(bp.Id, bgChild.ParentBranchPointId);
            Assert.Equal(1000.0, bgChild.ExplicitStartUT);
            Assert.Null(bgChild.EvaCrewName);
        }

        [Fact]
        public void BuildSplitBranchData_Undock_ChildIdsMatchBranchPoint()
        {
            var (bp, activeChild, bgChild) = ParsekFlight.BuildSplitBranchData(
                parentRecordingId: "parent_rec",
                treeId: "tree_1",
                branchUT: 1000.0,
                branchType: BranchPointType.Undock,
                activeVesselPid: 100,
                activeVesselName: "Ship",
                backgroundVesselPid: 200,
                backgroundVesselName: "Other");

            Assert.Contains(activeChild.RecordingId, bp.ChildRecordingIds);
            Assert.Contains(bgChild.RecordingId, bp.ChildRecordingIds);
        }

        [Fact]
        public void BuildSplitBranchData_Undock_GeneratesUniqueIds()
        {
            var (bp, activeChild, bgChild) = ParsekFlight.BuildSplitBranchData(
                parentRecordingId: "parent_rec",
                treeId: "tree_1",
                branchUT: 1000.0,
                branchType: BranchPointType.Undock,
                activeVesselPid: 100,
                activeVesselName: "Ship",
                backgroundVesselPid: 200,
                backgroundVesselName: "Other");

            // All IDs should be different
            Assert.NotEqual(activeChild.RecordingId, bgChild.RecordingId);
            Assert.NotEqual(activeChild.RecordingId, bp.Id);
            Assert.NotEqual(bgChild.RecordingId, bp.Id);
        }

        #endregion

        #region BuildSplitBranchData — EVA

        [Fact]
        public void BuildSplitBranchData_Eva_SetsEvaCrewNameOnActiveChild()
        {
            var (bp, activeChild, bgChild) = ParsekFlight.BuildSplitBranchData(
                parentRecordingId: "parent_rec",
                treeId: "tree_1",
                branchUT: 2000.0,
                branchType: BranchPointType.EVA,
                activeVesselPid: 300, // EVA kerbal (active)
                activeVesselName: "Jeb Kerman",
                backgroundVesselPid: 100, // Ship (background)
                backgroundVesselName: "My Rocket",
                evaCrewName: "Jeb Kerman");

            Assert.Equal(BranchPointType.EVA, bp.Type);
            Assert.Equal("Jeb Kerman", activeChild.EvaCrewName);
            Assert.Null(bgChild.EvaCrewName);
        }

        [Fact]
        public void BuildSplitBranchData_Eva_SetsParentRecordingIdOnKerbalChild()
        {
            var (bp, activeChild, bgChild) = ParsekFlight.BuildSplitBranchData(
                parentRecordingId: "parent_rec",
                treeId: "tree_1",
                branchUT: 2000.0,
                branchType: BranchPointType.EVA,
                activeVesselPid: 300,
                activeVesselName: "Jeb Kerman",
                backgroundVesselPid: 100,
                backgroundVesselName: "My Rocket",
                evaCrewName: "Jeb Kerman");

            // EVA kerbal's ParentRecordingId points to the vessel (background child)
            Assert.Equal(bgChild.RecordingId, activeChild.ParentRecordingId);
        }

        [Fact]
        public void BuildSplitBranchData_Eva_NoCrewName_NoSpecialFields()
        {
            // EVA branch without crew name (edge case)
            var (bp, activeChild, bgChild) = ParsekFlight.BuildSplitBranchData(
                parentRecordingId: "parent_rec",
                treeId: "tree_1",
                branchUT: 2000.0,
                branchType: BranchPointType.EVA,
                activeVesselPid: 300,
                activeVesselName: "Unknown Kerbal",
                backgroundVesselPid: 100,
                backgroundVesselName: "My Rocket",
                evaCrewName: null);

            Assert.Null(activeChild.EvaCrewName);
            Assert.Null(activeChild.ParentRecordingId);
        }

        [Fact]
        public void BuildSplitBranchData_Eva_KerbalIsBackground_SetsEvaFieldsOnBackgroundChild()
        {
            // When the ship is still active and the kerbal is the background child,
            // EvaCrewName should be set on the background child, not the active child.
            var (bp, activeChild, bgChild) = ParsekFlight.BuildSplitBranchData(
                parentRecordingId: "parent_rec",
                treeId: "tree_1",
                branchUT: 2000.0,
                branchType: BranchPointType.EVA,
                activeVesselPid: 100,        // Ship (still active)
                activeVesselName: "My Rocket",
                backgroundVesselPid: 300,    // EVA kerbal (background)
                backgroundVesselName: "Jeb Kerman",
                evaCrewName: "Jeb Kerman",
                evaVesselPid: 300);          // Explicitly identify the kerbal

            Assert.Null(activeChild.EvaCrewName); // Ship should NOT have EvaCrewName
            Assert.Equal("Jeb Kerman", bgChild.EvaCrewName); // Kerbal should
            Assert.Equal(activeChild.RecordingId, bgChild.ParentRecordingId); // Kerbal points to ship
        }

        #endregion

        #region BuildSplitBranchData — JointBreak

        [Fact]
        public void BuildSplitBranchData_JointBreak_CorrectType()
        {
            var (bp, activeChild, bgChild) = ParsekFlight.BuildSplitBranchData(
                parentRecordingId: "parent_rec",
                treeId: "tree_1",
                branchUT: 3000.0,
                branchType: BranchPointType.JointBreak,
                activeVesselPid: 100,
                activeVesselName: "Main Ship",
                backgroundVesselPid: 400,
                backgroundVesselName: "Broken Section");

            Assert.Equal(BranchPointType.JointBreak, bp.Type);
            Assert.Equal(3000.0, bp.UT);
            Assert.Null(activeChild.EvaCrewName);
            Assert.Null(bgChild.EvaCrewName);
        }

        #endregion

        #region Tree creation from BuildSplitBranchData

        [Fact]
        public void TreeCreation_FirstSplit_BranchDataHasCorrectTreeId()
        {
            string treeId = "test_tree_id";
            var (bp, activeChild, bgChild) = ParsekFlight.BuildSplitBranchData(
                parentRecordingId: "root_rec",
                treeId: treeId,
                branchUT: 500.0,
                branchType: BranchPointType.Undock,
                activeVesselPid: 100,
                activeVesselName: "Ship A",
                backgroundVesselPid: 200,
                backgroundVesselName: "Ship B");

            Assert.Equal(treeId, activeChild.TreeId);
            Assert.Equal(treeId, bgChild.TreeId);
        }

        [Fact]
        public void TreeCreation_BackgroundMap_PopulatedCorrectly()
        {
            // Simulate what CreateSplitBranch does: add bgChild to BackgroundMap
            var tree = new RecordingTree
            {
                Id = "tree_test",
                TreeName = "Test",
                RootRecordingId = "root_rec",
                ActiveRecordingId = null
            };

            var (bp, activeChild, bgChild) = ParsekFlight.BuildSplitBranchData(
                parentRecordingId: "root_rec",
                treeId: tree.Id,
                branchUT: 500.0,
                branchType: BranchPointType.Undock,
                activeVesselPid: 100,
                activeVesselName: "Ship A",
                backgroundVesselPid: 200,
                backgroundVesselName: "Ship B");

            tree.Recordings[activeChild.RecordingId] = activeChild;
            tree.Recordings[bgChild.RecordingId] = bgChild;
            tree.BranchPoints.Add(bp);
            tree.ActiveRecordingId = activeChild.RecordingId;
            tree.BackgroundMap[bgChild.VesselPersistentId] = bgChild.RecordingId;

            Assert.True(tree.BackgroundMap.ContainsKey(200));
            Assert.Equal(bgChild.RecordingId, tree.BackgroundMap[200]);
            Assert.False(tree.BackgroundMap.ContainsKey(100)); // active child not in background
        }

        [Fact]
        public void TreeCreation_RebuildBackgroundMap_MatchesManualSetup()
        {
            var tree = new RecordingTree
            {
                Id = "tree_rebuild",
                TreeName = "Rebuild Test",
                RootRecordingId = "root_rec"
            };

            // Root recording (terminated)
            tree.Recordings["root_rec"] = new Recording
            {
                RecordingId = "root_rec",
                VesselPersistentId = 50,
                TerminalStateValue = null, // not terminated but is the parent
                TreeId = tree.Id
            };

            var (bp, activeChild, bgChild) = ParsekFlight.BuildSplitBranchData(
                parentRecordingId: "root_rec",
                treeId: tree.Id,
                branchUT: 500.0,
                branchType: BranchPointType.Undock,
                activeVesselPid: 100,
                activeVesselName: "Ship A",
                backgroundVesselPid: 200,
                backgroundVesselName: "Ship B");

            // Set ChildBranchPointId on root (as CreateSplitBranch does at runtime)
            tree.Recordings["root_rec"].ChildBranchPointId = bp.Id;

            tree.Recordings[activeChild.RecordingId] = activeChild;
            tree.Recordings[bgChild.RecordingId] = bgChild;
            tree.ActiveRecordingId = activeChild.RecordingId;
            tree.BranchPoints.Add(bp);

            tree.RebuildBackgroundMap();

            // Root (pid=50) has ChildBranchPointId set (branched) -> should NOT be in background
            Assert.False(tree.BackgroundMap.ContainsKey(50));
            // Background child (pid=200) is not active, not terminated, no child branch -> should be in background
            Assert.True(tree.BackgroundMap.ContainsKey(200));
            // Active child (pid=100) should NOT be in background
            Assert.False(tree.BackgroundMap.ContainsKey(100));
        }

        #endregion

        #region FlightRecorder joint break signaling

        [Fact]
        public void FlightRecorder_JointBreakCheck_DefaultFalse()
        {
            var recorder = new FlightRecorder();
            Assert.False(recorder.HasPendingJointBreakCheck);
        }

        #endregion

        #region Branch data consistency across multiple splits

        [Fact]
        public void MultipleSplits_ProduceDifferentIds()
        {
            var (bp1, active1, bg1) = ParsekFlight.BuildSplitBranchData(
                "parent1", "tree", 100.0, BranchPointType.Undock,
                10, "Ship", 20, "Other");

            var (bp2, active2, bg2) = ParsekFlight.BuildSplitBranchData(
                "parent2", "tree", 200.0, BranchPointType.Undock,
                10, "Ship", 30, "Other2");

            // All IDs should be unique across calls
            var allIds = new HashSet<string>
            {
                bp1.Id, active1.RecordingId, bg1.RecordingId,
                bp2.Id, active2.RecordingId, bg2.RecordingId
            };
            Assert.Equal(6, allIds.Count); // all 6 are unique
        }

        [Fact]
        public void BuildSplitBranchData_PreservesExplicitStartUT()
        {
            var (bp, activeChild, bgChild) = ParsekFlight.BuildSplitBranchData(
                "parent", "tree", 12345.678, BranchPointType.EVA,
                10, "Kerbal", 20, "Ship", "Jeb");

            Assert.Equal(12345.678, activeChild.ExplicitStartUT);
            Assert.Equal(12345.678, bgChild.ExplicitStartUT);
        }

        #endregion
    }
}
