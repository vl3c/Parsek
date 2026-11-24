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
    /// The <c>LoopUnit_</c> cells drive the same dock and undock through a loop unit's own watch
    /// handoff, which must follow the same way.
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

        // ------------------------------------------------------------------
        // The same dock and undock inside a LOOPED mission, where the camera moves by the loop
        // unit's own handoff (GhostPlaybackEngine.LogUnitTransitionIfChanged) instead of the
        // end-of-recording follow. Todo LOOP-WATCH-STAYS-ON-THE-DOCKED-SHIP.
        // ------------------------------------------------------------------

        private sealed class UnitWatch : IDisposable
        {
            internal readonly List<CameraActionEvent> Events = new List<CameraActionEvent>();
            internal int Watched;
            private readonly GhostPlaybackEngine engine = new GhostPlaybackEngine(null);
            private readonly GhostPlaybackLogic.LoopUnit unit;
            private readonly List<Recording> committed;

            internal UnitWatch(
                List<Recording> committed, List<RecordingTree> trees, int watched, string identity,
                int[] members = null)
            {
                this.committed = committed;
                Watched = watched;
                if (members == null)
                {
                    members = new int[committed.Count];
                    for (int i = 0; i < members.Length; i++) members[i] = i;
                }
                unit = new GhostPlaybackLogic.LoopUnit(
                    ownerIndex: members[0], memberIndices: members,
                    spanStartUT: 0, spanEndUT: 600, cadenceSeconds: 1000, phaseAnchorUT: 0);
                engine.OnLoopCameraAction += e => Events.Add(e);
                if (trees != null)
                    engine.ResolveWatchedBranchContinuation = (index, isLive) =>
                        GhostPlaybackLogic.FindUnitBranchWatchTarget(
                            committed[index], committed, trees, isLive, null, identity);
            }

            internal void Frame(double loopUT, bool tail = false, bool suppress = false)
            {
                ParsekLog.ResetRateLimitsForTesting();
                engine.LogUnitTransitionIfChanged(
                    unit, "unit-test", committed, loopUT, 0, 5000 + loopUT, tail, Watched, suppress);
            }

            /// <summary>One frame; the host lands the transfer the event asked for.</summary>
            internal int Step(double loopUT)
            {
                int before = Events.Count;
                Frame(loopUT);
                if (Events.Count > before)
                    Watched = Events[Events.Count - 1].Index;
                return Watched;
            }

            public void Dispose() => engine.Dispose();
        }

        // Fails if: the loop unit's raw launch-guid comparison decides a dock: the merge kept the
        // station's guid, so the camera stays on the ended ship where the ordinary path follows.
        [Fact]
        public void LoopUnit_Dock_FollowsMerged()
        {
            var trees = DockUndockTree(out var committed, shipAfterGuid: ShipGuid);
            using (var w = new UnitWatch(committed, trees, watched: 0, identity: ShipGuid))
            {
                Assert.Equal(0, w.Step(150));
                Assert.Equal(2, w.Step(201));
                Assert.Single(w.Events);
                Assert.Equal(CameraActionType.UnitHandoffRetarget, w.Events[0].Action);
            }
        }

        // The ordinary path from the passive side: the station's own ghost docks too.
        [Fact]
        public void LoopUnit_Dock_FromThePassiveSide_FollowsMerged()
        {
            var trees = DockUndockTree(out var committed, shipAfterGuid: ShipGuid);
            committed[2].RecordedVesselGuid = ShipGuid; // the merge kept the ship's guid
            using (var w = new UnitWatch(committed, trees, watched: 1, identity: StationGuid))
            {
                Assert.Equal(1, w.Step(150));
                Assert.Equal(2, w.Step(201));
            }
        }

        // Owner ruling 2026-11-20 inside a loop: at the undock the ship, not the same-guid station.
        // Fails if: the guid tier (merged and stationAfter share the station's guid) wins.
        [Fact]
        public void LoopUnit_DockThenUndock_FollowsTheWatchedShip()
        {
            var trees = DockUndockTree(out var committed, shipAfterGuid: ShipGuid);
            using (var w = new UnitWatch(committed, trees, watched: 0, identity: ShipGuid))
            {
                w.Step(150);
                Assert.Equal(2, w.Step(201));
                Assert.Equal(2, w.Step(300));
                Assert.Equal(4, w.Step(401));
            }
        }

        // No identity to follow: the station child, as the ordinary path and the guid tier both say.
        [Fact]
        public void LoopUnit_Undock_IdentityUnmatched_FollowsTheStation()
        {
            var trees = DockUndockTree(out var committed, shipAfterGuid: FreshShipGuid);
            using (var w = new UnitWatch(committed, trees, watched: 2, identity: ShipGuid))
            {
                w.Step(300);
                Assert.Equal(3, w.Step(401));
            }
        }

        // The merged ghost takes frames to build: the event re-fires until the transfer lands.
        [Fact]
        public void LoopUnit_Dock_RefiresUntilTheTransferLands()
        {
            var trees = DockUndockTree(out var committed, shipAfterGuid: ShipGuid);
            using (var w = new UnitWatch(committed, trees, watched: 0, identity: ShipGuid))
            {
                w.Frame(150);
                w.Frame(201);
                w.Frame(202);
                Assert.Equal(2, w.Events.Count);
                w.Watched = 2;
                w.Frame(203);
                w.Frame(204);
                Assert.Equal(2, w.Events.Count);
            }
        }

        // A mission that does not play the merged leg has nowhere to follow to: hold.
        [Fact]
        public void LoopUnit_Dock_MergedNotAMember_Holds()
        {
            var trees = DockUndockTree(out var committed, shipAfterGuid: ShipGuid);
            using (var w = new UnitWatch(committed, trees, watched: 0, identity: ShipGuid,
                members: new[] { 0, 1 }))
            {
                w.Step(150);
                Assert.Equal(0, w.Step(201));
                Assert.Empty(w.Events);
            }
        }

        [Fact]
        public void LoopUnit_Dock_InstanceEndedOrTail_Holds()
        {
            var trees = DockUndockTree(out var committed, shipAfterGuid: ShipGuid);
            using (var w = new UnitWatch(committed, trees, watched: 0, identity: ShipGuid))
            {
                w.Frame(150);
                w.Frame(201, suppress: true);
                Assert.Empty(w.Events);
            }
            using (var w = new UnitWatch(committed, trees, watched: 0, identity: ShipGuid))
            {
                w.Frame(150);
                w.Frame(201, tail: true);
                Assert.Empty(w.Events);
            }
        }

        // The unit handoff's own rule stays at every other split: a pod that ended (destroyed,
        // beside its kerbal on EVA) holds for its own end, never jumps to the sibling.
        // Fails if: the branch answer is asked at any branch type.
        [Theory]
        [InlineData(BranchPointType.EVA)]
        [InlineData(BranchPointType.JointBreak)]
        [InlineData(BranchPointType.Breakup)]
        public void LoopUnit_OtherSplits_KeepTheSameVesselRule(BranchPointType type)
        {
            var trees = DockUndockTree(out var committed, shipAfterGuid: ShipGuid);
            trees[0].BranchPoints[0].Type = type;
            using (var w = new UnitWatch(committed, trees, watched: 0, identity: ShipGuid))
            {
                w.Step(150);
                Assert.Equal(0, w.Step(201));
                Assert.Empty(w.Events);
            }
        }

        // Boarding is a merge too: the kerbal's recording ends in the vessel.
        [Fact]
        public void LoopUnit_Board_FollowsTheVessel()
        {
            var trees = DockUndockTree(out var committed, shipAfterGuid: ShipGuid);
            trees[0].BranchPoints[0].Type = BranchPointType.Board;
            using (var w = new UnitWatch(committed, trees, watched: 0, identity: ShipGuid))
            {
                w.Step(150);
                Assert.Equal(2, w.Step(201));
            }
        }

        // No host (tests, other scenes): the old same-vessel rule, unchanged.
        [Fact]
        public void LoopUnit_NoHostResolver_KeepsTheSameVesselRule()
        {
            DockUndockTree(out var committed, shipAfterGuid: ShipGuid);
            using (var w = new UnitWatch(committed, null, watched: 0, identity: ShipGuid))
            {
                w.Step(150);
                Assert.Equal(0, w.Step(201));
                Assert.Empty(w.Events);
            }
        }

        [Fact]
        public void LoopUnit_Dock_LogsTheBranchRetarget()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            var trees = DockUndockTree(out var committed, shipAfterGuid: ShipGuid);
            using (var w = new UnitWatch(committed, trees, watched: 0, identity: ShipGuid))
            {
                w.Step(150);
                w.Step(201);
            }
            Assert.Contains(lines, l => l.Contains("[CameraFollow]")
                && l.Contains("unit watch retarget owner=0 member #0->#2 at loopUT=201.00 via=branch"));
        }

        // Source gate: the flight host hands its watch controller to the engine.
        [Fact]
        public void LoopUnit_HostWiresTheBranchResolver()
        {
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..",
                "Source", "Parsek", "Controllers", "ParsekFlight.cs"));
            Assert.Contains(
                "engine.ResolveWatchedBranchContinuation = watchMode.FindWatchedBranchContinuation;", src);
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
