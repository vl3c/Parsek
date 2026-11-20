using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Watch mode across a docking: what <see cref="GhostPlaybackLogic.FindNextWatchTarget"/>
    /// (the pure core behind <c>WatchModeController.FindNextWatchTarget</c>) picks when the
    /// watched ghost's recording ends at a <see cref="BranchPointType.Dock"/> branch point.
    /// A dock has two parents (the watched ship and the partner it docks into) and ONE child,
    /// the merged vessel, whose persistentId is whichever side KSP kept. Every case must
    /// follow the merged vessel, since it is the only place the watched ship went:
    /// <list type="bullet">
    /// <item>merged keeps the watched ship's pid: same-pid continuation;</item>
    /// <item>merged keeps the partner's pid: the different-pid fallback, allowed because a
    /// Dock is not a Breakup and the watched recording ended at the dock;</item>
    /// <item>watching the passive partner: same rule from the other parent.</item>
    /// </list>
    /// The dock-then-undock cell pins today's next step: the undock child carrying the
    /// merged vessel's pid wins, so a watched ship that docked into a station whose pid the
    /// merge kept is followed by the STATION after the undock, not the ship that leaves.
    /// </summary>
    [Collection("Sequential")]
    public class WatchAcrossDockTests : IDisposable
    {
        private const uint ShipPid = 1111u;
        private const uint StationPid = 2222u;
        private const uint FreshShipPid = 3333u;

        public WatchAcrossDockTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private static Recording Rec(string id, uint pid, double start, double end,
            string childBpId = null, bool debris = false)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "V_" + id,
                VesselPersistentId = pid,
                TreeId = "tree_dock",
                ChildBranchPointId = childBpId,
                IsDebris = debris,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = start },
                    new TrajectoryPoint { ut = end },
                },
            };
        }

        // ship [100..200] + station [0..200] --Dock@200--> merged [200..400] (mergedPid)
        private static List<RecordingTree> DockTree(out List<Recording> committed, uint mergedPid)
        {
            committed = new List<Recording>
            {
                Rec("ship", ShipPid, 100, 200, childBpId: "bp_dock"),
                Rec("station", StationPid, 0, 200, childBpId: "bp_dock"),
                Rec("merged", mergedPid, 200, 400),
            };
            var tree = new RecordingTree
            {
                Id = "tree_dock",
                BranchPoints = new List<BranchPoint>
                {
                    new BranchPoint
                    {
                        Id = "bp_dock",
                        Type = BranchPointType.Dock,
                        UT = 200,
                        ParentRecordingIds = new List<string> { "ship", "station" },
                        ChildRecordingIds = new List<string> { "merged" },
                    },
                },
            };
            return new List<RecordingTree> { tree };
        }

        [Fact]
        public void Dock_MergedKeepsWatchedPid_FollowsMerged()
        {
            var trees = DockTree(out var committed, mergedPid: ShipPid);
            int next = GhostPlaybackLogic.FindNextWatchTarget(
                committed[0], committed, trees, idx => idx == 2);
            Assert.Equal(2, next);
        }

        // Fails if: a dock into a vessel whose pid the merge kept drops the watch (-1)
        // because no child shares the watched ship's pid.
        [Fact]
        public void Dock_MergedKeepsPartnerPid_FollowsMergedThroughFallback()
        {
            var trees = DockTree(out var committed, mergedPid: StationPid);
            int next = GhostPlaybackLogic.FindNextWatchTarget(
                committed[0], committed, trees, idx => idx == 2);
            Assert.Equal(2, next);
        }

        [Fact]
        public void Dock_WatchingThePassivePartner_FollowsMerged()
        {
            var trees = DockTree(out var committed, mergedPid: StationPid);
            int next = GhostPlaybackLogic.FindNextWatchTarget(
                committed[1], committed, trees, idx => idx == 2);
            Assert.Equal(2, next);

            // Same from the passive side when the merge kept the SHIP's pid instead.
            trees = DockTree(out committed, mergedPid: ShipPid);
            next = GhostPlaybackLogic.FindNextWatchTarget(
                committed[1], committed, trees, idx => idx == 2);
            Assert.Equal(2, next);
        }

        // Fails if: the watch jumps to some other active ghost while the merged vessel's
        // ghost is not spawned yet. Nothing else is a place the ship went: hold (-1) and
        // let the caller retry.
        [Fact]
        public void Dock_MergedGhostNotActive_HoldsWithMinusOne()
        {
            var trees = DockTree(out var committed, mergedPid: StationPid);
            int next = GhostPlaybackLogic.FindNextWatchTarget(
                committed[0], committed, trees, idx => idx == 1);
            Assert.Equal(-1, next);

            trees = DockTree(out committed, mergedPid: ShipPid);
            next = GhostPlaybackLogic.FindNextWatchTarget(
                committed[0], committed, trees, idx => idx == 1);
            Assert.Equal(-1, next);
        }

        // Pins the step after: merged [200..400] --Undock@400--> stationAfter (merged pid)
        // + shipAfter (a pid other than the merge's). The
        // same-pid rule follows the station; the ship that leaves is not followed. Owner
        // question (reported, not changed): should watch remember the vessel it started on
        // across a dock-undock pair?
        [Fact]
        public void DockThenUndock_FollowsTheSidePidMatchingTheMerge_TodayTheStation()
        {
            var committed = new List<Recording>
            {
                Rec("ship", ShipPid, 100, 200, childBpId: "bp_dock"),
                Rec("station", StationPid, 0, 200, childBpId: "bp_dock"),
                Rec("merged", StationPid, 200, 400, childBpId: "bp_undock"),
                Rec("stationAfter", StationPid, 400, 600),
                Rec("shipAfter", FreshShipPid, 400, 500),
            };
            var tree = new RecordingTree
            {
                Id = "tree_dock",
                BranchPoints = new List<BranchPoint>
                {
                    new BranchPoint
                    {
                        Id = "bp_dock",
                        Type = BranchPointType.Dock,
                        UT = 200,
                        ParentRecordingIds = new List<string> { "ship", "station" },
                        ChildRecordingIds = new List<string> { "merged" },
                    },
                    new BranchPoint
                    {
                        Id = "bp_undock",
                        Type = BranchPointType.Undock,
                        UT = 400,
                        ParentRecordingIds = new List<string> { "merged" },
                        ChildRecordingIds = new List<string> { "shipAfter", "stationAfter" },
                    },
                },
            };
            var trees = new List<RecordingTree> { tree };

            int afterDock = GhostPlaybackLogic.FindNextWatchTarget(
                committed[0], committed, trees, idx => idx >= 2);
            Assert.Equal(2, afterDock);

            int afterUndock = GhostPlaybackLogic.FindNextWatchTarget(
                committed[afterDock], committed, trees, idx => idx >= 3);
            Assert.Equal(3, afterUndock); // stationAfter, not shipAfter (index 4)
        }
    }
}
