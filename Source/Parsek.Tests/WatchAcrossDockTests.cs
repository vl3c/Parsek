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
    /// The dock-then-undock cells pin the owner ruling of 2026-11-20 ("follow the ship I was
    /// watching"): at the undock the child that continues the watched launch identity (its
    /// guid, or a fresh guid linked to it) wins over the child carrying the merge's pid; with
    /// the identity unknown or unmatched, the pid rule stays.
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
            string childBpId = null, bool debris = false, string guid = null)
        {
            return new Recording
            {
                RecordingId = id,
                RecordedVesselGuid = guid,
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

        private const string ShipGuid = "11111111111111111111111111111111";
        private const string StationGuid = "22222222222222222222222222222222";
        private const string FreshShipGuid = "33333333333333333333333333333333";

        // ship [100..200] + station [0..200] --Dock@200--> merged [200..400] (station pid and
        // guid: the merge kept the station) --Undock@400--> stationAfter (station pid, guid)
        // + shipAfter (fresh pid, guid shipAfterGuid). Indices: ship 0, station 1, merged 2,
        // stationAfter 3, shipAfter 4.
        private static List<RecordingTree> DockUndockTree(out List<Recording> committed, string shipAfterGuid)
        {
            committed = new List<Recording>
            {
                Rec("ship", ShipPid, 100, 200, childBpId: "bp_dock", guid: ShipGuid),
                Rec("station", StationPid, 0, 200, childBpId: "bp_dock", guid: StationGuid),
                Rec("merged", StationPid, 200, 400, childBpId: "bp_undock", guid: StationGuid),
                Rec("stationAfter", StationPid, 400, 600, guid: StationGuid),
                Rec("shipAfter", FreshShipPid, 400, 500, guid: shipAfterGuid),
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
                        ChildRecordingIds = new List<string> { "stationAfter", "shipAfter" },
                    },
                },
            };
            return new List<RecordingTree> { tree };
        }

        // Owner ruling 2026-11-20: watch follows the ship it started on. The ship that leaves
        // carries the watched launch guid, so it wins over the station child that carries the
        // merge's pid.
        // Fails if: the undock step follows the same-pid station child (index 3).
        [Fact]
        public void DockThenUndock_FollowsTheWatchedShip_NotTheStation()
        {
            var trees = DockUndockTree(out var committed, shipAfterGuid: ShipGuid);

            int afterDock = GhostPlaybackLogic.FindNextWatchTarget(
                committed[0], committed, trees, idx => idx >= 2, watchedIdentityGuid: ShipGuid);
            Assert.Equal(2, afterDock);

            int afterUndock = GhostPlaybackLogic.FindNextWatchTarget(
                committed[afterDock], committed, trees, idx => idx >= 3, watchedIdentityGuid: ShipGuid);
            Assert.Equal(4, afterUndock);
        }

        // KSP gives the ship that leaves a dock a fresh guid; the undock-return link row
        // (VesselNameStore, kind UndockReturn) joins it to the ship's identity.
        // Fails if: identity reads only a bare guid equality and misses the link row.
        [Fact]
        public void DockThenUndock_FreshGuidLinkedToTheShip_FollowsTheShip()
        {
            VesselNameStore.ResetForTesting();
            try
            {
                Assert.True(VesselNameStore.AddLink(FreshShipGuid, ShipGuid,
                    VesselIdentityLinkKind.UndockReturn, "test"));
                var trees = DockUndockTree(out var committed, shipAfterGuid: FreshShipGuid);
                int afterUndock = GhostPlaybackLogic.FindNextWatchTarget(
                    committed[2], committed, trees, idx => idx >= 3, watchedIdentityGuid: ShipGuid);
                Assert.Equal(4, afterUndock);
            }
            finally
            {
                VesselNameStore.ResetForTesting();
            }
        }

        // Watching the station through the same dock and undock still follows the station.
        [Fact]
        public void DockThenUndock_WatchingTheStation_FollowsTheStation()
        {
            var trees = DockUndockTree(out var committed, shipAfterGuid: ShipGuid);
            int afterUndock = GhostPlaybackLogic.FindNextWatchTarget(
                committed[2], committed, trees, idx => idx >= 3, watchedIdentityGuid: StationGuid);
            Assert.Equal(3, afterUndock);
        }

        // Unknown identity, or an identity neither child continues: today's rule (the child
        // carrying the merge's pid).
        [Fact]
        public void DockThenUndock_IdentityUnknownOrUnmatched_KeepsThePidRule()
        {
            var trees = DockUndockTree(out var committed, shipAfterGuid: FreshShipGuid);
            Assert.Equal(3, GhostPlaybackLogic.FindNextWatchTarget(
                committed[2], committed, trees, idx => idx >= 3));
            Assert.Equal(3, GhostPlaybackLogic.FindNextWatchTarget(
                committed[2], committed, trees, idx => idx >= 3, watchedIdentityGuid: ShipGuid));

            // The ship's child carries no guid: unknown, not a match.
            trees = DockUndockTree(out committed, shipAfterGuid: null);
            Assert.Equal(3, GhostPlaybackLogic.FindNextWatchTarget(
                committed[2], committed, trees, idx => idx >= 3, watchedIdentityGuid: ShipGuid));
        }

        // The ship's ghost is not spawned yet while the station's is: hold (-1) so the hold
        // timer retries, never jump to the station.
        // Fails if: the identity child's absence falls through to the station.
        [Fact]
        public void DockThenUndock_WatchedShipGhostNotActive_Holds()
        {
            var trees = DockUndockTree(out var committed, shipAfterGuid: ShipGuid);
            int next = GhostPlaybackLogic.FindNextWatchTarget(
                committed[2], committed, trees, idx => idx == 3, watchedIdentityGuid: ShipGuid);
            Assert.Equal(-1, next);
        }

        // The hold-extension mirror: a pending ship child extends the hold to ITS activation,
        // and the active station child does not cancel the wait.
        // Fails if: TryGetPendingWatchActivationUT still treats the station as the continuation.
        [Fact]
        public void DockThenUndock_PendingActivation_WaitsForTheWatchedShip()
        {
            var trees = DockUndockTree(out var committed, shipAfterGuid: ShipGuid);
            committed[4].ExplicitStartUT = 450;
            committed[4].Points[0] = new TrajectoryPoint { ut = 450 };
            bool pending = GhostPlaybackLogic.TryGetPendingWatchActivationUT(
                committed[2], committed, trees, idx => idx == 3, out double activationUT,
                watchedIdentityGuid: ShipGuid);
            Assert.True(pending);
            Assert.Equal(450, activationUT, 6);

            // Without an identity the active station child is the continuation: no pending wait.
            Assert.False(GhostPlaybackLogic.TryGetPendingWatchActivationUT(
                committed[2], committed, trees, idx => idx == 3, out _));
        }

        // The dock itself is unchanged by an identity: the merged vessel is the only child.
        [Fact]
        public void Dock_WithWatchedIdentity_StillFollowsMerged()
        {
            var trees = DockTree(out var committed, mergedPid: StationPid);
            committed[2].RecordedVesselGuid = StationGuid;
            int next = GhostPlaybackLogic.FindNextWatchTarget(
                committed[0], committed, trees, idx => idx == 2, watchedIdentityGuid: ShipGuid);
            Assert.Equal(2, next);
        }

        // The decision is logged: which child the identity picked, and the fall back to the pid rule.
        [Fact]
        public void DockThenUndock_LogsTheIdentityDecision()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;

            var trees = DockUndockTree(out var committed, shipAfterGuid: ShipGuid);
            GhostPlaybackLogic.FindNextWatchTarget(
                committed[2], committed, trees, idx => idx >= 3, watchedIdentityGuid: ShipGuid);
            Assert.Contains(lines, l => l.Contains("[Watch]")
                && l.Contains("undock bp_undock")
                && l.Contains("following the watched vessel identity=" + ShipGuid)
                && l.Contains("id=shipAfter"));

            trees = DockUndockTree(out committed, shipAfterGuid: FreshShipGuid);
            GhostPlaybackLogic.FindNextWatchTarget(
                committed[2], committed, trees, idx => idx >= 3, watchedIdentityGuid: ShipGuid);
            Assert.Contains(lines, l => l.Contains("[Watch]")
                && l.Contains("no child continues the watched vessel identity=" + ShipGuid)
                && l.Contains("keeping the same-PID rule"));
        }

        [Fact]
        public void GuidsPositivelySameIdentity_Truths()
        {
            Assert.True(VesselLaunchIdentity.GuidsPositivelySameIdentity(ShipGuid, ShipGuid.ToUpperInvariant()));
            Assert.False(VesselLaunchIdentity.GuidsPositivelySameIdentity(ShipGuid, StationGuid));
            Assert.False(VesselLaunchIdentity.GuidsPositivelySameIdentity(ShipGuid, null));
            Assert.False(VesselLaunchIdentity.GuidsPositivelySameIdentity(null, null));
            string empty = Guid.Empty.ToString("N");
            Assert.False(VesselLaunchIdentity.GuidsPositivelySameIdentity(empty, empty));
        }
    }
}
