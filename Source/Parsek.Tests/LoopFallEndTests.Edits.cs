using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    // A loop edited while its falls are in the air. The Missions window changes the period without
    // re-stamping the loop anchor, so the rebuilt unit is live on the next frame and every cycle number
    // names another launch: a copy of the old schedule ends quietly, in both directions of the edit.
    public partial class LoopFallEndTests
    {
        private const string QuietLine = "Fall copy ended quietly the loop's schedule changed under it: #";

        private void AssertQuiet()
        {
            Assert.DoesNotContain(events, e => e.Contains("explosion=True") || e.Contains("ExplosionHoldStart"));
            Assert.Contains(logLines, l => l.Contains("[Engine]") && l.Contains(QuietLine));
        }

        private static void Edit(GhostPlaybackEngine engine, GhostPlaybackLogic.LoopUnit unit) =>
            engine.SetLoopUnits(SetOf(unit));

        // A copy of cycle n driven on its own schedule up to the edit.
        private GhostPlaybackState Fly(
            GhostPlaybackEngine engine, ZoneProbe probe, GhostPlaybackLogic.LoopUnit unit, MockTrajectory traj,
            long cycle, double period, double phase)
        {
            GhostPlaybackState copy = Seed(engine, cycle);
            Assert.Equal(CoreStart + phase,
                MemberFrame(engine, probe, unit, traj, Ctx(CoreSchedule + cycle * period + phase)), 9);
            return copy;
        }

        // ---- the pure rule ----

        [Theory]
        [InlineData(40, 1, true, 100.0, 100.0, 10.0, true)]       // newer than the newest launch
        [InlineData(1, 1, true, 100.0, 100.0, 10.0, false)]       // the newest launch's own copy
        [InlineData(1, 1, false, 100.0, 100.0, 10.0, true)]       // the same cycle beside the slot
        [InlineData(0, 1, false, 100.0, 100.0, 10.0, false)]
        [InlineData(0, 1, false, 100.0, 100.4, 10.0, false)]      // within the tolerance
        [InlineData(0, 1, false, 100.0, 130.0, 10.0, true)]       // its clock moved
        [InlineData(0, 1, false, 100.0, 70.0, 900.0, true)]       // moved past its end: no ending
        [InlineData(0, 1, false, 100.0, 100.0, 900.0, false)]     // its own end: the expiry's, not stale
        [InlineData(0, 1, false, double.NaN, 100.0, 10.0, false)] // no clock yet: adopted
        [InlineData(0, 1, false, double.NaN, 100.0, 900.0, true)]
        [InlineData(0, 1, false, double.NaN, 100.0, -5.0, true)]
        [InlineData(0, 1, false, 100.0, double.PositiveInfinity, double.NegativeInfinity, true)]
        [InlineData(1, 1, true, 100.0, double.PositiveInfinity, double.NegativeInfinity, false)]
        [InlineData(-1, 1, true, double.NaN, 100.0, -5.0, false)] // an ordinary ghost
        [InlineData(-3, 1, false, 0.0, 100.0, 900.0, false)]      // the first run's fall
        public void StaleFallCopy_IsOneOfAnotherSchedule(
            long cycle, long newest, bool inSlot, double stamped, double offset, double phase, bool stale)
        {
            Assert.Equal(stale, GhostPlaybackLogic.IsStaleFallCopy(
                cycle, newest, inSlot, stamped, offset, phase, Fall));
        }

        // ---- a uniform unit ----

        [Theory]
        [InlineData(6298.0)]
        [InlineData(20000.0)]
        [InlineData(100000.0)]
        public void PeriodRaised_LeavesNoCopyOfTheOldLaunches(double later)
        {
            var old = Unit(150);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(old, probe);
            MockTrajectory traj = Traj(CoreStart, CoreEnd, predictedEnd: true);
            Fly(engine, probe, old, traj, 40, 150, 10);
            var unit = Unit(3600);
            Edit(engine, unit);
            MemberFrame(engine, probe, unit, traj, Ctx(6298));
            double ut = MemberFrame(engine, probe, unit, traj, Ctx(later));
            double phase = (later - CoreSchedule) % 3600;
            Assert.True(phase > Fall);
            Assert.True(double.IsNaN(ut), "in the gap (phase " + phase + ") a copy is drawn at recorded UT " + ut);
            Assert.False(engine.ghostStates.ContainsKey(CoreIndex));
            Assert.Equal(0, Copies(engine));
            Assert.Equal(new[] { "cycle=40" }, destroyed);
            AssertQuiet();
        }

        [Fact]
        public void PeriodRaised_WhileTwoCopiesFall_KeepsNeitherBesideTheNewOne()
        {
            // Cycle 1 in the slot, cycle 0 beside it; on 900 s the newest launch is cycle 0.
            var old = Unit(150);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(old, probe);
            MockTrajectory traj = Traj(CoreStart, CoreEnd, predictedEnd: true);
            Fly(engine, probe, old, traj, 0, 150, 149);
            MemberFrame(engine, probe, old, traj, Ctx(CoreSchedule + 151));
            Assert.Equal(1, Copies(engine));
            Fly(engine, probe, old, traj, 1, 150, 2);
            var unit = Unit(900);
            Edit(engine, unit);
            MemberFrame(engine, probe, unit, traj, Ctx(CoreSchedule + 153));
            Assert.Equal(0, Copies(engine));
            Assert.Equal(new[] { "cycle=0", "cycle=1" }, destroyed.OrderBy(d => d, StringComparer.Ordinal));
            AssertQuiet();
            // one copy of cycle 0 is drawn again, from the slot
            Assert.Contains(logLines, l => l.Contains("Overlap: SpawnGhost failed for #1 cycle=0"));
        }

        // The shorter period: an old copy reads as ended (or as another moment of its fall).
        [Theory]
        [InlineData(150.0, 24L)]      // cycle 1's old copy is 3460 s past its end
        [InlineData(3000.0, 1L)]      // same cycle number, 610 s into a 213 s fall
        [InlineData(3550.0, 1L)]      // same cycle number, 60 s in: respawned there
        public void PeriodShortened_NeverExplodesOnTheEditFrame(double period, long newest)
        {
            var old = Unit(3600);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(old, probe);
            MockTrajectory traj = Traj(CoreStart, CoreEnd, predictedEnd: true);
            Fly(engine, probe, old, traj, 1, 3600, 10);
            var unit = Unit(period);
            Edit(engine, unit);
            double now = CoreSchedule + 3610;
            Assert.True(double.IsNaN(MemberFrame(engine, probe, unit, traj, Ctx(now))));
            Assert.Equal(new[] { "cycle=1" }, destroyed);
            Assert.Equal(0, Copies(engine));
            AssertQuiet();
            bool falls = now - CoreSchedule - newest * period <= Fall;
            Assert.Equal(falls, logLines.Any(l => l.Contains("Overlap: SpawnGhost failed for #1 cycle=" + newest)));
        }

        [Fact]
        public void RebuildThatChangesNothing_KeepsTheCopies()
        {
            var old = Unit(150);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(old, probe);
            MockTrajectory traj = Traj(CoreStart, CoreEnd, predictedEnd: true);
            Fly(engine, probe, old, traj, 4, 150, 100);
            GhostPlaybackState copy = engine.ghostStates[CoreIndex];
            var same = Unit(150);
            Edit(engine, same);
            Assert.Equal(CoreStart + 101, MemberFrame(engine, probe, same, traj, Ctx(CoreSchedule + 701)), 9);
            Assert.Same(copy, engine.ghostStates[CoreIndex]);
            Assert.Empty(destroyed);
        }

        // The global Auto period (or another unit) can cross the self-overlap line in either direction.
        [Theory]
        [InlineData(150.0, 30.0)]
        [InlineData(30.0, 150.0)]
        public void PeriodAcrossTheSelfOverlapLine_LeavesNoOldCopy(double from, double to)
        {
            var old = Unit(from);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(old, probe);
            MockTrajectory traj = Traj(CoreStart, CoreEnd, predictedEnd: true);
            GhostPlaybackState copy = Seed(engine, 40);
            copy.fallClockOffsetUT = CoreSchedule + 40 * from - CoreStart;
            var unit = Unit(to);
            Edit(engine, unit);
            double now = CoreSchedule + 40 * from + 10;
            GhostPlaybackState state = copy;
            bool active = false;
            Frame(probe, () =>
            {
                if (GhostPlaybackLogic.UnitMemberOverlaps(unit))
                    engine.UpdateUnitMemberSelfOverlap(
                        CoreIndex, traj, default(TrajectoryPlaybackFlags), Ctx(now),
                        new List<IPlaybackTrajectory> { traj, traj }, unit, "unit-0", SpanEnd - SpanStart,
                        CoreStart, CoreEnd, false, false, ref state, ref active);
                else
                    engine.UpdateUnitMemberFallSchedule(
                        CoreIndex, traj, default(TrajectoryPlaybackFlags), Ctx(now), unit, "unit-0",
                        CoreStart, CoreEnd, false, false, ref state, ref active);
            });
            Assert.Equal(new[] { "cycle=40" }, destroyed);
            Assert.Equal(0, Copies(engine));
            AssertQuiet();
        }

        // An include / exclude edit moves the span start: every launch of the member moves with it. A trim
        // of the member's own start does not.
        [Theory]
        [InlineData(50.0, CoreStart, true)]
        [InlineData(SpanStart, 130.0, false)]
        public void IncludeExcludeEdit_DropsTheCopiesItsLaunchesMovedUnder(
            double spanStart, double memberStart, bool dropped)
        {
            var old = Unit(150);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(old, probe);
            MockTrajectory traj = Traj(CoreStart, CoreEnd, predictedEnd: true);
            GhostPlaybackState copy = Fly(engine, probe, old, traj, 2, 150, 60);
            var unit = new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0, CoreIndex }, spanStart, SpanEnd, 150, Anchor, 150,
                new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>
                {
                    [0] = new GhostPlaybackLogic.LoopUnit.MemberWindow(spanStart, SpanEnd),
                    [CoreIndex] = new GhostPlaybackLogic.LoopUnit.MemberWindow(memberStart, CoreEnd)
                }, null, null, null, renderSpanEndUT: CoreEnd);
            Edit(engine, unit);
            GhostPlaybackState state = copy;
            bool active = false;
            double ut = Frame(probe, () => engine.UpdateUnitMemberFallSchedule(
                CoreIndex, traj, default(TrajectoryPlaybackFlags), Ctx(CoreSchedule + 361), unit, "unit-0",
                memberStart, CoreEnd, false, false, ref state, ref active));
            if (dropped)
            {
                Assert.Equal(new[] { "cycle=2" }, destroyed);
                AssertQuiet();
            }
            else
            {
                Assert.Empty(destroyed);
                Assert.Same(copy, engine.ghostStates[CoreIndex]);
                Assert.Equal(CoreStart + 61, ut, 9);
            }
        }

        [Fact]
        public void FirstRunsFall_SurvivesAPeriodEdit()
        {
            var old = Unit(900);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(old, probe);
            MockTrajectory traj = Traj(CoreStart, CoreEnd, predictedEnd: true);
            Seed(engine, -1);
            Assert.Equal(210, MemberFrame(engine, probe, old, traj, Ctx(210)), 9);
            var unit = Unit(150);
            Edit(engine, unit);
            Assert.Equal(250, MemberFrame(engine, probe, unit, traj, Ctx(250)), 9);
            Assert.Equal(1, Copies(engine));
            Assert.Empty(destroyed);
            // and goes with the loop falls when no path draws them any more
            engine.DropUndrivenFallCopies(CoreIndex, traj, default(TrajectoryPlaybackFlags), Ctx(251));
            Assert.Equal(new[] { "cycle=-3" }, destroyed);
            Assert.Empty(events);
        }

        // The edit made under high warp, seen when the warp drops.
        [Theory]
        [InlineData(150.0, 3600.0, 40L, 6298.0)]
        [InlineData(3600.0, 150.0, 1L, 3900.0)]
        public void PeriodEditUnderHighWarp_EndsTheWatchedCopyQuietly(
            double from, double to, long cycle, double now)
        {
            var old = Unit(from);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(old, probe);
            MockTrajectory traj = Traj(CoreStart, CoreEnd, predictedEnd: true);
            Fly(engine, probe, old, traj, cycle, from, 10);
            MemberFrame(engine, probe, old, traj, Ctx(now - 1, 1000f, watched: CoreIndex), suppress: true);
            var unit = Unit(to);
            Edit(engine, unit);
            MemberFrame(engine, probe, unit, traj, Ctx(now, 1000f, watched: CoreIndex), suppress: true);
            Assert.True(engine.ghostStates.ContainsKey(CoreIndex));
            MemberFrame(engine, probe, unit, traj, Ctx(now + 1, watched: CoreIndex));
            Assert.Equal(new[] { "cycle=" + cycle }, destroyed);
            Assert.Equal(0, Copies(engine));
            AssertQuiet();
        }

        // Out of this rule's scope on purpose: copies with a recorded end keep the old demotion.
        [Fact]
        public void PeriodEdit_OfARecordedEnd_KeepsTheOldHandling()
        {
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(Unit(100), probe);
            MockTrajectory debris = Traj(60, 117, predictedEnd: false, name: "Booster");
            GhostPlaybackState copy = Seed(engine, 40, DebrisIndex);
            DebrisFrame(engine, probe, debris, Ctx(300));
            Assert.True(engine.TryGetOverlapGhosts(DebrisIndex, out var overlaps));
            Assert.Same(copy, overlaps.Single());
            Assert.True(double.IsNaN(copy.fallClockOffsetUT));
            Assert.DoesNotContain(logLines, l => l.Contains(QuietLine));
        }

        // ---- a unit whose cycles are not uniform ----

        [Fact]
        public void NonUniform_PeriodRaised_LeavesNoCopyOfTheOldLaunches()
        {
            var probe = new ZoneProbe();
            GhostPlaybackLogic.LoopUnit unit = CutUnit(3600);
            GhostPlaybackEngine engine = Engine(unit, probe);
            MockTrajectory traj = Traj(CoreStart, CoreEnd, predictedEnd: true);
            engine.overlapGhosts[CoreIndex] = new List<GhostPlaybackState>
                { new GhostPlaybackState { vesselName = "Core", loopCycleIndex = 39 } };
            GhostPlaybackState state = null;
            Assert.True(double.IsNaN(NonUniformFrame(engine, probe, unit, traj, Anchor + 3600 + 2000, 1, ref state)));
            Assert.Equal(0, Copies(engine));
            AssertQuiet();
        }

        // The slot's and the kept copies' clocks: kept while the schedule stands, ended when it moved.
        [Theory]
        [InlineData(0.0, true)]
        [InlineData(0.3, true)]
        [InlineData(-400.0, false)]
        [InlineData(400.0, false)]
        public void NonUniform_AnOlderLaunchsFall_IsKeptOnlyOnItsOwnClock(double shift, bool kept)
        {
            var probe = new ZoneProbe();
            GhostPlaybackLogic.LoopUnit unit = CutUnit(150);
            GhostPlaybackEngine engine = Engine(unit, probe);
            MockTrajectory traj = Traj(CoreStart, CoreEnd, predictedEnd: true);
            // Cycle 0 reached its span end at 289.5; at 360 its fall stands at recorded UT 211.
            GhostPlaybackState slot = Seed(engine, 0);
            slot.fallClockOffsetUT = 289.5 - SpanEnd + shift;
            GhostPlaybackState state = slot;
            double ut = NonUniformFrame(engine, probe, unit, traj, 360, 1, ref state,
                GhostPlaybackLogic.UnitMemberRenderDecision.HiddenOutsideWindow);
            Assert.Null(state);
            if (kept)
            {
                Assert.Equal(211, ut, 9);
                Assert.Equal(1, Copies(engine));
                Assert.Empty(destroyed);
            }
            else
            {
                Assert.True(double.IsNaN(ut));
                Assert.Equal(0, Copies(engine));
                Assert.Equal(new[] { "cycle=0" }, destroyed);
                AssertQuiet();
            }
        }

        [Theory]
        [InlineData(0.0, true)]         // its own end, in the wait
        [InlineData(3000.0, false)]     // the edit put it past its end
        [InlineData(double.NaN, false)] // never driven on a fall clock
        public void NonUniform_AFallEndingInTheWait_ExplodesOnlyOnItsOwnClock(double shift, bool explodes)
        {
            var probe = new ZoneProbe();
            GhostPlaybackLogic.LoopUnit unit = CutUnit(900);
            GhostPlaybackEngine engine = Engine(unit, probe);
            MockTrajectory traj = Traj(CoreStart, CoreEnd, predictedEnd: true);
            GhostPlaybackState state = Seed(engine, 0);
            state.fallClockOffsetUT = 289.5 - SpanEnd + shift;
            NonUniformFrame(engine, probe, unit, traj, 500, 0, ref state);
            Assert.Null(state);
            Assert.Equal(0, Copies(engine));
            if (explodes)
                Assert.Equal(Cycle0Ending, events);
            else
                AssertQuiet();
        }

        [Fact]
        public void NonUniform_TheSpanClocksCopy_IsStampedBeforeItsFall()
        {
            var probe = new ZoneProbe();
            GhostPlaybackLogic.LoopUnit unit = CutUnit(150);
            GhostPlaybackEngine engine = Engine(unit, probe);
            MockTrajectory traj = Traj(CoreStart, CoreEnd, predictedEnd: true);
            GhostPlaybackState state = Seed(engine, 0);
            NonUniformFrame(engine, probe, unit, traj, 285, 0, ref state,
                GhostPlaybackLogic.UnitMemberRenderDecision.Render);
            Assert.Equal(289.5 - SpanEnd, state.fallClockOffsetUT, 9);
            Assert.Empty(destroyed);
        }

        // A supply route's unit rides a relaunch schedule its backing mission rebuilds.
        [Theory]
        [InlineData(1000.0, true)]
        [InlineData(500000.0, false)]
        public void RouteScheduleRebuilt_KeepsTheFallOnlyWhenItsLaunchStands(double floor, bool kept)
        {
            GhostPlaybackLogic.LoopUnit RouteUnit(double firstFloor)
            {
                var schedule = new MissionRelaunchSchedule(
                    SpanStart, 1000, new[] { 701.0 }, new[] { 25.0 }, firstFloor, 200);
                return new GhostPlaybackLogic.LoopUnit(
                    0, new[] { 0, CoreIndex }, SpanStart, SpanEnd, SpanEnd - SpanStart, schedule.FirstLaunchUT,
                    SpanEnd - SpanStart,
                    new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>
                    {
                        [0] = new GhostPlaybackLogic.LoopUnit.MemberWindow(SpanStart, SpanEnd),
                        [CoreIndex] = new GhostPlaybackLogic.LoopUnit.MemberWindow(CoreStart, CoreEnd)
                    }, schedule, null, null, renderSpanEndUT: CoreEnd);
            }

            GhostPlaybackLogic.LoopUnit before = RouteUnit(1000);
            GhostPlaybackLogic.LoopUnit after = RouteUnit(floor);
            Assert.True(GhostPlaybackLogic.TryComputeCycleSpanEndLiveUT(before, 0, out double spanEndLive));
            Assert.True(GhostPlaybackLogic.TryComputeCycleSpanEndLiveUT(after, 0, out double afterLive));
            Assert.Equal(kept, spanEndLive == afterLive);

            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(before, probe);
            MockTrajectory traj = Traj(CoreStart, CoreEnd, predictedEnd: true);
            GhostPlaybackState copy = new GhostPlaybackState { vesselName = "Core", loopCycleIndex = 0 };
            copy.fallClockOffsetUT = spanEndLive - SpanEnd;
            engine.overlapGhosts[CoreIndex] = new List<GhostPlaybackState> { copy };
            Edit(engine, after);
            GhostPlaybackState state = null;
            double ut = NonUniformFrame(engine, probe, after, traj, spanEndLive + 50, 1, ref state,
                GhostPlaybackLogic.UnitMemberRenderDecision.HiddenOutsideWindow);
            Assert.Equal(kept ? SpanEnd + 50 : double.NaN, ut, 6);
            Assert.Equal(kept ? 1 : 0, Copies(engine));
            if (!kept)
                AssertQuiet();
        }

        private static double NonUniformFrame(
            GhostPlaybackEngine engine, ZoneProbe probe, GhostPlaybackLogic.LoopUnit unit, MockTrajectory traj,
            double now, long unitCycle, ref GhostPlaybackState state,
            GhostPlaybackLogic.UnitMemberRenderDecision decision =
                GhostPlaybackLogic.UnitMemberRenderDecision.HiddenInterCycleTail)
        {
            GhostPlaybackState slot = state;
            bool active = false;
            double ut = SpanEnd;
            double drawn = Frame(probe, () => engine.UpdateNonUniformFall(
                CoreIndex, traj, default(TrajectoryPlaybackFlags), Ctx(now), unit, unitCycle, CoreStart, CoreEnd,
                false, false, ref decision, ref ut, ref slot, ref active));
            state = slot;
            return drawn;
        }

        // ---- debris ----

        [Theory]
        [InlineData(150.0, 3600.0, 40L)]
        [InlineData(3600.0, 150.0, 1L)]
        public void Debris_PeriodEdit_LeavesNoCopyOfTheOldLaunches(double from, double to, long cycle)
        {
            const double schedule = 259; // the debris's cycle 0
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(Unit(from), probe);
            MockTrajectory debris = Traj(90, 250, predictedEnd: true, name: "Booster");
            Seed(engine, cycle, DebrisIndex);
            Assert.Equal(100, DebrisFrame(engine, probe, debris, Ctx(schedule + cycle * from + 10)), 9);
            Edit(engine, Unit(to));
            DebrisFrame(engine, probe, debris, Ctx(schedule + cycle * from + 11));
            Assert.Equal(new[] { "cycle=" + cycle }, destroyed);
            Assert.Equal(0, Copies(engine, DebrisIndex));
            AssertQuiet();
        }

        [Theory]
        [InlineData(0.0, true)]
        [InlineData(3000.0, false)]
        [InlineData(double.NaN, false)]
        public void Debris_OfANonUniformUnit_ExplodesOnlyOnItsOwnClock(double shift, bool explodes)
        {
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(CutUnit(900), probe);
            MockTrajectory debris = Traj(90, 250, predictedEnd: true, name: "Booster");
            Seed(engine, 0, DebrisIndex).fallClockOffsetUT = 289.5 - SpanEnd + shift;
            DebrisFrame(engine, probe, debris, Ctx(420));
            Assert.False(engine.ghostStates.ContainsKey(DebrisIndex));
            Assert.Equal(0, Copies(engine, DebrisIndex));
            Assert.Equal(explodes ? Cycle0Ending : new string[0], events);
        }

        [Fact]
        public void Debris_UniformCyclesTakenAway_DropsItsCopies()
        {
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(CutUnit(900), probe);
            MockTrajectory debris = Traj(90, 250, predictedEnd: true, name: "Booster");
            engine.overlapGhosts[DebrisIndex] = new List<GhostPlaybackState>
                { new GhostPlaybackState { vesselName = "Booster", loopCycleIndex = 3, fallClockOffsetUT = 600 } };
            DebrisFrame(engine, probe, debris, Ctx(1500));
            Assert.Equal(0, Copies(engine, DebrisIndex));
            AssertQuiet();
        }

        // ---- copies no fall path drives any more ----

        [Fact]
        public void UndrivenCopies_OnlyTheFallsGo()
        {
            GhostPlaybackEngine engine = Engine(Unit(150), new ZoneProbe());
            MockTrajectory traj = Traj(CoreStart, CoreEnd, predictedEnd: true);
            var overlap = new GhostPlaybackState { loopCycleIndex = 2 };
            var secondary = new GhostPlaybackState { loopCycleIndex = 3, isBoundaryOverlapSecondary = true };
            engine.overlapGhosts[CoreIndex] = new List<GhostPlaybackState>
            {
                overlap,
                new GhostPlaybackState { loopCycleIndex = 1, fallClockOffsetUT = 500 },
                secondary,
                new GhostPlaybackState
                    { loopCycleIndex = GhostPlaybackLogic.FirstRunFallCycleIndex, fallClockOffsetUT = 0 }
            };
            engine.DropUndrivenFallCopies(CoreIndex, traj, default(TrajectoryPlaybackFlags), Ctx(500));
            Assert.Equal(new[] { overlap, secondary }, engine.overlapGhosts[CoreIndex]);
            AssertQuiet();
            engine.DropUndrivenFallCopies(7, traj, default(TrajectoryPlaybackFlags), Ctx(500));
        }

        [Fact]
        public void SkippedSlot_LeavesNoFallCopyInTheSky()
        {
            GhostPlaybackEngine engine = Engine(CutUnit(150), new ZoneProbe());
            MockTrajectory traj = Traj(CoreStart, CoreEnd, predictedEnd: true);
            engine.overlapGhosts[CoreIndex] = new List<GhostPlaybackState>
                { new GhostPlaybackState { loopCycleIndex = 1, fallClockOffsetUT = 500 } };
            engine.HandleSkippedPlaybackEntry(
                CoreIndex, traj,
                new TrajectoryPlaybackFlags { skipGhost = true, skipReason = GhostPlaybackSkipReason.PlaybackDisabled },
                Ctx(500));
            Assert.Equal(0, Copies(engine));
        }
    }
}
