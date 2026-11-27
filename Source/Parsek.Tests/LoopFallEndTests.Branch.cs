using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    // A dock, boarding or undock inside an OVERLAPPING mission loop: the watched launch follows where its
    // vessel went (the host's branch answer, todo LOOP-WATCH-STAYS-ON-THE-DOCKED-SHIP) on that launch's own
    // copy, ahead of the same vessel's next row, and an answer whose copy cannot be followed ends the watch
    // of the launch (hold, then restart) instead of waiting or falling to the same-guid row.
    public partial class LoopFallEndTests
    {
        private const string Station = "22222222222222222222222222222222";

        private static Recording BranchRec(string id, uint pid, double start, double end, string guid,
            string childBp = null)
        {
            return new Recording
            {
                RecordingId = id, RecordedVesselGuid = guid, VesselName = "V_" + id, VesselPersistentId = pid,
                TreeId = "tree_loop_dock", ChildBranchPointId = childBp,
                ExplicitStartUT = start, ExplicitEndUT = end,
                Points = new List<TrajectoryPoint>
                    { new TrajectoryPoint { ut = start }, new TrajectoryPoint { ut = end } },
            };
        }

        private static List<RecordingTree> BranchTree(
            BranchPointType type, List<string> parents, List<string> children) => new List<RecordingTree>
        {
            new RecordingTree
            {
                Id = "tree_loop_dock",
                BranchPoints = new List<BranchPoint>
                {
                    new BranchPoint
                    {
                        Id = "bp", Type = type, UT = 81, ParentRecordingIds = parents, ChildRecordingIds = children,
                    },
                },
            },
        };

        // The host's side over the committed copies of the rows (index-aligned with the engine's rows).
        private static void WireBranchHost(
            GhostPlaybackEngine engine, List<Recording> committed, List<RecordingTree> trees, string identity)
        {
            engine.ResolveWatchedBranchContinuation = (index, isLive) =>
                GhostPlaybackLogic.FindUnitBranchWatchTarget(
                    committed[index], committed, trees, isLive, null, identity);
        }

        // Ship #0 [31, 81] docks into the station at 81; the merged vessel #1 [81, 140.5] keeps the
        // station's guid. Ascent cycle n plays over [200 + 100n, 250 + 100n].
        private static List<IPlaybackTrajectory> DockRows(out List<Recording> committed, out List<RecordingTree> trees)
        {
            committed = new List<Recording>
            {
                BranchRec("ship", 1111u, SpanStart, 81, Ship, childBp: "bp"),
                BranchRec("merged", 2222u, 81, SpanEnd, Station),
            };
            trees = BranchTree(BranchPointType.Dock,
                new List<string> { "ship", "station" }, new List<string> { "merged" });
            return TwoRowTrajectories(secondGuid: Station);
        }

        // The merged vessel #0 [31, 81] (station guid) undocks at 81: the station leg #1 keeps the guid, the
        // ship #2 leaves under its own. Both legs [81, 140.5].
        private static GhostPlaybackLogic.LoopUnit UndockRowsUnit() => new GhostPlaybackLogic.LoopUnit(
            0, new[] { 0, 1, 2 }, SpanStart, SpanEnd, SpanEnd - SpanStart, Anchor, OwnPeriod,
            new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>
            {
                [0] = new GhostPlaybackLogic.LoopUnit.MemberWindow(SpanStart, 81),
                [1] = new GhostPlaybackLogic.LoopUnit.MemberWindow(81, SpanEnd),
                [2] = new GhostPlaybackLogic.LoopUnit.MemberWindow(81, SpanEnd)
            });

        private static List<IPlaybackTrajectory> UndockRows(out List<Recording> committed, out List<RecordingTree> trees)
        {
            committed = new List<Recording>
            {
                BranchRec("merged", 2222u, SpanStart, 81, Station, childBp: "bp"),
                BranchRec("stationAfter", 2222u, 81, SpanEnd, Station),
                BranchRec("shipAfter", 3333u, 81, SpanEnd, Ship),
            };
            trees = BranchTree(BranchPointType.Undock,
                new List<string> { "merged" }, new List<string> { "stationAfter", "shipAfter" });
            return new List<IPlaybackTrajectory>
            {
                Ended(SpanStart, 81, null, "Ship", Station),
                Ended(81, SpanEnd, TerminalState.Landed, "Ship", Station),
                Ended(81, SpanEnd, TerminalState.Landed, "Ship", Ship),
            };
        }

        // Fails if: the overlap handoff's same-guid row search decides a dock (the merged vessel kept the
        // station's guid, so the watch holds and restarts on another launch), or the branch target is
        // handed over without that launch's cycle.
        [Theory]
        [InlineData(0L, 249.0, 250.1, true)]
        [InlineData(1L, 349.0, 350.1, true)]
        [InlineData(0L, 249.0, 250.1, false)]   // the control: no host answer, the PR's own rule
        public void Watch_OverlapDock_FollowsThatLaunchsDockedCopy(long cycle, double before, double now, bool host)
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows();
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = WatchEngine(unit, probe);
            List<IPlaybackTrajectory> all = DockRows(out List<Recording> committed, out List<RecordingTree> trees);
            if (host) WireBranchHost(engine, committed, trees, Ship);
            GhostPlaybackState ship = Seed(engine, cycle, 0);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(before, 0, cycle));
            Assert.Empty(events);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(now, 0, cycle));
            if (host)
            {
                Assert.Equal(new[] { "handoff UnitHandoffRetarget #1 cycle=" + cycle }, events);
                Assert.True(ship.endedCopyHeld);
                Assert.Contains("member #0->#1 at loopUT=81.10 via=branch",
                    Assert.Single(logLines, l => l.Contains("unit watch retarget owner=")));
                Assert.Contains("watch=HoldForNextRow", Assert.Single(EndLines()));
            }
            else
            {
                Assert.Equal(new[] { "camera EndHoldStart cycle=" + cycle, "expired cycle=" + cycle + " explosion=False" },
                    events);
                Assert.DoesNotContain(logLines, l => l.Contains("unit watch retarget owner="));
            }
        }

        // The docked row draws no ghost: nothing to wait for, the watch of the launch ends at once.
        // Fails if: a branch answer that cannot be followed is still handed over or waited for.
        [Fact]
        public void Watch_OverlapDock_DockedRowNotDrawn_HoldsThenRestarts()
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows();
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = WatchEngine(unit, probe);
            engine.frameFlags = Flags(CoreIndex);
            List<IPlaybackTrajectory> all = DockRows(out List<Recording> committed, out List<RecordingTree> trees);
            WireBranchHost(engine, committed, trees, Ship);
            Seed(engine, 0, 0);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(249, 0, 0));
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(250.1, 0, 0));
            Assert.Equal(new[] { "camera EndHoldStart cycle=0", "expired cycle=0 explosion=False" }, events);
            Assert.Contains("watch=HoldThenRestart", Assert.Single(EndLines()));
            Assert.Contains(logLines, l => l.Contains("[CameraFollow]") && l.Contains(
                "unit watch: no copy of the next row to follow: #0 \"Ship\" cycle=0 reason=NotDrawn"));
        }

        // The docked row started but its copy of the launch never comes: the bounded wait, then the hold.
        // Fails if: the branch answer waits without the frame bound (the camera stalls on a hidden copy).
        [Fact]
        public void Watch_OverlapDock_DockedCopyNeverBuilt_BoundedWaitThenHold()
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows();
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = WatchEngine(unit, probe);
            List<IPlaybackTrajectory> all = DockRows(out List<Recording> committed, out List<RecordingTree> trees);
            WireBranchHost(engine, committed, trees, Ship);
            Seed(engine, 0, 0);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(249, 0, 0));
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(250.1, 0, 0));
            Assert.Equal(new[] { "handoff UnitHandoffRetarget #1 cycle=0" }, events);
            int frames = 0;
            for (double now = 250.12; !events.Contains("camera EndHoldStart cycle=0"); now += 0.02)
            {
                events.Clear();
                SelfOverlapFrame(engine, probe, unit, 0, all, Watching(now, 0, 0));
                Assert.True(++frames <= GhostPlaybackEngine.MaxEndedCopyWaitFrames + 2, "the hold never ends");
            }
            Assert.Equal(GhostPlaybackEngine.MaxEndedCopyWaitFrames + 1, frames);
            Assert.Equal(new[] { "camera EndHoldStart cycle=0", "expired cycle=0 explosion=False" }, events);
            Assert.Contains(logLines, l => l.Contains("[CameraFollow]") && l.Contains(
                "unit watch: no copy of the next row to follow: #0 \"Ship\" cycle=0 reason=WaitedTooLong waitedFrames="
                    + GhostPlaybackEngine.MaxEndedCopyWaitFrames));
            Assert.Single(logLines, l => l.Contains("unit watch retarget owner="));
        }

        // Owner ruling 2026-11-20 in an overlapping loop: at the undock the ship, not the station leg that
        // shares the merged vessel's guid.
        // Fails if: the same-vessel next row is asked before the branch answer.
        [Theory]
        [InlineData(true, 2)]
        [InlineData(false, 1)]   // the control: without the host the same-guid station leg wins
        public void Watch_OverlapUndock_PicksTheShip_NotTheStationLeg(bool host, int expected)
        {
            GhostPlaybackLogic.LoopUnit unit = UndockRowsUnit();
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = WatchEngine(unit, probe);
            List<IPlaybackTrajectory> all = UndockRows(out List<Recording> committed, out List<RecordingTree> trees);
            if (host) WireBranchHost(engine, committed, trees, Ship);
            Seed(engine, 0, 0);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(249, 0, 0));
            foreach (double now in new[] { 250.1, 250.2 })
            {
                events.Clear();
                SelfOverlapFrame(engine, probe, unit, 0, all, Watching(now, 0, 0));
                Assert.Equal(new[] { "handoff UnitHandoffRetarget #" + expected + " cycle=0" }, events);
            }
            Assert.Contains("member #0->#" + expected + " at loopUT=81.10 via=" + (host ? "branch" : "same-vessel"),
                Assert.Single(logLines, l => l.Contains("unit watch retarget owner=")));
        }

        // The ship's leg draws no ghost: the watch of the launch ends; it never moves to the station leg.
        // Fails if: a branch answer that cannot be followed falls to the same-guid row.
        [Fact]
        public void Watch_OverlapUndock_ShipRowNotDrawn_HoldsThenRestarts_NeverTheStation()
        {
            GhostPlaybackLogic.LoopUnit unit = UndockRowsUnit();
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = WatchEngine(unit, probe);
            engine.frameFlags = Flags(2);
            List<IPlaybackTrajectory> all = UndockRows(out List<Recording> committed, out List<RecordingTree> trees);
            WireBranchHost(engine, committed, trees, Ship);
            Seed(engine, 0, 0);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(249, 0, 0));
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(250.1, 0, 0));
            Assert.Equal(new[] { "camera EndHoldStart cycle=0", "expired cycle=0 explosion=False" }, events);
            Assert.DoesNotContain(logLines, l => l.Contains("unit watch retarget owner="));
            Assert.False(engine.ghostStates.ContainsKey(0));
        }

        // A watched row that ends at no dock, boarding or undock keeps the same-vessel row: the host answers
        // -1 and nothing changes (the PR's own cells, with a host wired).
        [Fact]
        public void Watch_OverlapRowChange_WithAHostAndNoBranch_KeepsTheSameVesselRow()
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows();
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = WatchEngine(unit, probe);
            List<IPlaybackTrajectory> all = TwoRowTrajectories();
            var committed = new List<Recording>
            {
                BranchRec("ascent", 1111u, SpanStart, 81, Ship, childBp: "bp"),
                BranchRec("transfer", 1111u, 81, SpanEnd, Ship),
            };
            WireBranchHost(engine, committed, BranchTree(BranchPointType.JointBreak,
                new List<string> { "ascent" }, new List<string> { "transfer" }), Ship);
            Seed(engine, 0, 0);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(249, 0, 0));
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(250.1, 0, 0));
            Assert.Equal(new[] { "handoff UnitHandoffRetarget #1 cycle=0" }, events);
            Assert.Contains("via=same-vessel", Assert.Single(logLines, l => l.Contains("unit watch retarget owner=")));
        }
    }
}
