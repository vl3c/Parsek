using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    // Owner ruling 2026-11-24: in an overlapping loop EVERY copy ends at its own recorded end. The
    // member's whole flight lies inside the span of a mission that relaunches on a cadence longer than it.
    public partial class LoopFallEndTests
    {
        private const double OwnPeriod = 100;

        // One member [60, 117] in a 109.5 s span on 100 s launches: cycle n plays over
        // [229 + 100n, 286 + 100n] and leaves a 43 s gap.
        private static GhostPlaybackLogic.LoopUnit ShortMemberUnit(double start = 60, double end = 117, double period = OwnPeriod)
        {
            return new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0, CoreIndex }, SpanStart, SpanEnd, SpanEnd - SpanStart, Anchor, period,
                new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>
                {
                    [0] = new GhostPlaybackLogic.LoopUnit.MemberWindow(SpanStart, SpanEnd),
                    [CoreIndex] = new GhostPlaybackLogic.LoopUnit.MemberWindow(start, end)
                });
        }

        private static MockTrajectory Ended(
            double start, double end, TerminalState? terminal, string name = "Core", string guid = null)
        {
            MockTrajectory traj = Traj(start, end, predictedEnd: false, name: name);
            traj.TerminalStateValue = terminal;
            traj.RecordedVesselGuid = guid;
            return traj;
        }

        private static FrameContext Watching(double ut, int index, long cycle, float warp = 1f)
        {
            FrameContext ctx = Ctx(ut, warp, index);
            ctx.protectedLoopCycleIndex = cycle;
            return ctx;
        }

        private static double SelfOverlapFrame(
            GhostPlaybackEngine engine, ZoneProbe probe, GhostPlaybackLogic.LoopUnit unit, int index,
            IReadOnlyList<IPlaybackTrajectory> all, FrameContext ctx, bool suppress = false)
        {
            engine.ghostStates.TryGetValue(index, out GhostPlaybackState state);
            bool active = false;
            IPlaybackTrajectory traj = all[index];
            return Frame(probe, () => engine.UpdateUnitMemberSelfOverlap(
                index, traj, default(TrajectoryPlaybackFlags), ctx, all, unit, "unit-0",
                unit.SpanEndUT - unit.SpanStartUT, unit.MemberStartUT(index, traj.StartUT),
                unit.MemberEndUT(index, traj.EndUT), suppress, suppress, ref state, ref active));
        }

        private IEnumerable<string> EndLines(string subsystem = "[Engine]") =>
            logLines.Where(l => l.Contains(subsystem) && l.Contains("Loop copy ended at its own end: #"));

        private static readonly string[] QuietCycle0 =
            { "camera ExplosionHoldEnd cycle=0", "expired cycle=0 explosion=False" };

        // ---- every ending, flight ----

        [Theory]
        [InlineData(TerminalState.Destroyed, true, "destroyed")]
        [InlineData(TerminalState.Landed, false, "recorded-end")]
        [InlineData(TerminalState.Splashed, false, "recorded-end")]
        [InlineData(TerminalState.Orbiting, false, "recorded-end")]
        [InlineData(TerminalState.SubOrbital, false, "recorded-end")]
        [InlineData(TerminalState.Docked, false, "recorded-end")]
        [InlineData(TerminalState.Boarded, false, "recorded-end")]
        [InlineData(TerminalState.Recovered, false, "recorded-end")]
        [InlineData(TerminalState.Disassembled, false, "recorded-end")]
        [InlineData(null, false, "recorded-end")]
        public void OwnEnd_Member_EveryEnding_EndsOnceAtItsRecordedEnd(
            TerminalState? terminal, bool explodes, string reason)
        {
            GhostPlaybackLogic.LoopUnit unit = ShortMemberUnit();
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(unit, probe);
            MockTrajectory traj = Ended(60, 117, terminal);
            var all = new List<IPlaybackTrajectory> { Ended(SpanStart, SpanEnd, TerminalState.Landed, "Ship"), traj };
            Seed(engine, 0);

            Assert.Equal(116, SelfOverlapFrame(engine, probe, unit, CoreIndex, all, Ctx(285)), 9);
            Assert.Empty(events);
            foreach (double now in new[] { 286.1, 300.0, 328.9 })
            {
                Assert.True(double.IsNaN(SelfOverlapFrame(engine, probe, unit, CoreIndex, all, Ctx(now))),
                    "a copy is drawn at " + now);
                Assert.False(engine.ghostStates.ContainsKey(CoreIndex));
                Assert.Equal(0, Copies(engine));
            }
            Assert.Equal(explodes ? Cycle0Ending : QuietCycle0, events);
            Assert.Equal(new[] { "cycle=0" }, destroyed);
            string line = Assert.Single(EndLines());
            Assert.Contains("#1 \"Core\" cycle=0 loopUT=117.10 endUT=117.00 reason=" + reason
                + " watch=NotWatched", line);
            Assert.DoesNotContain(logLines, l => l.Contains("Fall copy"));
            // the next launch draws its own copy
            SelfOverlapFrame(engine, probe, unit, CoreIndex, all, Ctx(329.5));
            Assert.Contains(logLines, l => l.Contains("Overlap: SpawnGhost failed for #1 cycle=1"));
        }

        [Theory]
        [InlineData(TerminalState.Destroyed, true)]
        [InlineData(TerminalState.Landed, false)]
        [InlineData(TerminalState.SubOrbital, false)]
        public void OwnEnd_Debris_EndsOnceAtItsRecordedEnd(TerminalState terminal, bool explodes)
        {
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(Unit(OwnPeriod), probe);
            MockTrajectory debris = Ended(60, 117, terminal, "Booster");
            Seed(engine, 0, DebrisIndex);
            Assert.Equal(116, DebrisFrame(engine, probe, debris, Ctx(285)), 9);
            foreach (double now in new[] { 286.1, 300.0, 328.9 })
            {
                Assert.True(double.IsNaN(DebrisFrame(engine, probe, debris, Ctx(now))));
                Assert.False(engine.ghostStates.ContainsKey(DebrisIndex));
                Assert.Equal(0, Copies(engine, DebrisIndex));
            }
            Assert.Equal(explodes ? Cycle0Ending : QuietCycle0, events);
            Assert.Contains("#2 \"Booster\" cycle=0", Assert.Single(EndLines()));
        }

        // Two copies found past their ends (a time jump): each ends once, each with its own line.
        [Fact]
        public void OwnEnd_OlderCopy_EndsWithTheSameLine()
        {
            GhostPlaybackLogic.LoopUnit unit = ShortMemberUnit();
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(unit, probe);
            var all = new List<IPlaybackTrajectory>
                { Ended(SpanStart, SpanEnd, null, "Ship"), Ended(60, 117, TerminalState.Landed) };
            engine.overlapGhosts[CoreIndex] = new List<GhostPlaybackState>
                { new GhostPlaybackState { vesselName = "Core", loopCycleIndex = 0, fallClockOffsetUT = 229 - 60 } };
            Seed(engine, 1).fallClockOffsetUT = 329 - 60;
            Assert.True(double.IsNaN(SelfOverlapFrame(engine, probe, unit, CoreIndex, all, Ctx(390))));
            Assert.Equal(new[] { "cycle=0", "cycle=1" }, destroyed.OrderBy(d => d, StringComparer.Ordinal));
            Assert.Equal(2, EndLines().Count());
            Assert.Contains(EndLines(), l => l.Contains("cycle=0 loopUT=221.00 endUT=117.00"));
            Assert.Contains(EndLines(), l => l.Contains("cycle=1 loopUT=121.00 endUT=117.00"));
        }

        // ---- trim ----

        // Destroyed at 117, the mission trims the member at 100: its copy ends there, quietly.
        [Theory]
        [InlineData(100.0, false, "trimmed-end")]
        [InlineData(116.9, false, "trimmed-end")]
        [InlineData(116.96, true, "destroyed")]   // a physics step off the recording's end is the end
        [InlineData(117.0, true, "destroyed")]
        public void OwnEnd_TrimmedMember_EndsQuietlyAtTheTrim(double trimmedEnd, bool explodes, string reason)
        {
            GhostPlaybackLogic.LoopUnit unit = ShortMemberUnit(end: trimmedEnd);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(unit, probe);
            var all = new List<IPlaybackTrajectory>
                { Ended(SpanStart, SpanEnd, null, "Ship"), Ended(60, 117, TerminalState.Destroyed) };
            Seed(engine, 0);
            double endLive = 229 + (trimmedEnd - 60);
            SelfOverlapFrame(engine, probe, unit, CoreIndex, all, Ctx(endLive - 1));
            Assert.True(double.IsNaN(SelfOverlapFrame(engine, probe, unit, CoreIndex, all, Ctx(endLive + 0.5))));
            Assert.Equal(explodes ? Cycle0Ending : QuietCycle0, events);
            Assert.Contains("reason=" + reason, Assert.Single(EndLines()));
        }

        [Fact]
        public void OwnEnd_EndUT_IsTheWindowsEnd_ForAMissionCopy_AndTheLoopEnd_ForAStandaloneLoop()
        {
            MockTrajectory traj = Ended(60, 117, TerminalState.Destroyed);
            var trimmed = new GhostPlaybackLogic.OverlapCopyEnds(100, recorded: true);
            Assert.Equal(100, GhostPlaybackLogic.ResolveOverlapCopyEndUT(trimmed, traj));
            Assert.False(GhostPlaybackLogic.OverlapCopyExplodes(trimmed, traj));
            // a recording's own loop sub-range says nothing about a mission member's window
            traj.LoopEndUT = 90;
            Assert.Equal(117, GhostPlaybackLogic.ResolveOverlapCopyEndUT(
                new GhostPlaybackLogic.OverlapCopyEnds(117, recorded: true), traj));
            GhostPlaybackLogic.OverlapCopyEnds standalone = default(GhostPlaybackLogic.OverlapCopyEnds);
            Assert.Equal(90, GhostPlaybackLogic.ResolveOverlapCopyEndUT(standalone, traj));
            Assert.False(GhostPlaybackLogic.OverlapCopyExplodes(standalone, traj));
            traj.LoopEndUT = 116.96; // inside the mission tolerance, still not a standalone loop's explosion
            Assert.False(GhostPlaybackLogic.OverlapCopyExplodes(standalone, traj));
            traj.LoopEndUT = double.NaN;
            Assert.True(GhostPlaybackLogic.OverlapCopyExplodes(standalone, traj));
            traj.QuietDestroyedEnd = true;
            Assert.Equal("destroyed-quietly", GhostPlaybackLogic.DescribeOverlapCopyEnd(traj, 117));
            Assert.False(GhostPlaybackLogic.WindowReachesRecordingEnd(null, 117));
            Assert.False(GhostPlaybackLogic.ExplodesAtWindowEnd(null, 117));
        }

        // The mesh work needs the game: the placing, the part events and the flag walk are pinned to the
        // same window end in the source, in flight and at the Space Center.
        [Fact]
        public void OwnEnd_BothHosts_PlaceAndPlayOutTheCopyAtItsWindowEnd()
        {
            string flight = ReadSource("Ghost", "GhostPlaybackEngine.LoopPlayback.cs");
            Assert.Contains("PositionGhostAtLoopEndUT(index, traj, ovState, endUT);", flight);
            Assert.Contains("ApplyEndPartEvents(index, traj, ovState, endUT, phase - duration, suppressVisualFx);", flight);
            Assert.True(flight.IndexOf("ApplyEndPartEvents(", StringComparison.Ordinal)
                < flight.IndexOf("TriggerExplosionIfDestroyed(ovState, traj, index, ctx.warpRate);", StringComparison.Ordinal));
            Assert.DoesNotContain("endAtOwnEnd", flight);

            string ksc = ReadSource("Controllers", "ParsekKSC.cs");
            Assert.DoesNotContain("endAtOwnEnd", ksc);
            Assert.Contains(
                "ends: new GhostPlaybackLogic.OverlapCopyEnds(memberEndUT, recorded: !memberHasFall && !GhostPlaybackLogic.EndsInPredictedTail(rec, memberEndUT)));", ksc);
            Assert.Contains("double endUT = GhostPlaybackLogic.ResolveOverlapCopyEndUT(ends, rec);", ksc);
            Assert.Contains("PositionGhostAtLoopEndUT(recIdx, rec, ovState, endUT);", ksc);
            Assert.Contains("ApplyKscEndEvents(recIdx, rec, ovState, endUT, phase - duration, suppressVisualFx);", ksc);
            Assert.Contains("GhostPlaybackLogic.ApplyFlagEvents(state, rec, endUT);", ksc);
            Assert.Contains("GhostPlaybackLogic.OverlapCopyExplodes(ends, rec)", ksc);
            Assert.Contains("if (ends.AtOwnEnd && GhostPlaybackLogic.HasOverlapCopyEnded(", ksc);
        }

        // One text, comments gone and whitespace folded.
        private static string ReadSource(params string[] parts)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "Source", "Parsek")))
                dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            var text = new StringBuilder();
            foreach (string raw in File.ReadAllLines(
                         Path.Combine(new[] { dir, "Source", "Parsek" }.Concat(parts).ToArray())))
            {
                string line = raw.Trim();
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0) line = line.Substring(0, comment).Trim();
                if (line.Length > 0) text.Append(line).Append(' ');
            }
            return text.ToString().Replace("( ", "(");
        }

        // ---- the Space Center decides with the same functions ----

        [Theory]
        [InlineData(TerminalState.Destroyed, true, "destroyed")]
        [InlineData(TerminalState.Landed, false, "recorded-end")]
        [InlineData(TerminalState.Splashed, false, "recorded-end")]
        [InlineData(TerminalState.Orbiting, false, "recorded-end")]
        [InlineData(TerminalState.SubOrbital, false, "recorded-end")]
        [InlineData(TerminalState.Docked, false, "recorded-end")]
        [InlineData(TerminalState.Boarded, false, "recorded-end")]
        [InlineData(TerminalState.Recovered, false, "recorded-end")]
        [InlineData(TerminalState.Disassembled, false, "recorded-end")]
        public void OwnEnd_SpaceCenter_EveryEnding(TerminalState terminal, bool explodes, string reason)
        {
            var rec = new Recording
            {
                RecordingId = "b", VesselName = "Core", ExplicitStartUT = 60, ExplicitEndUT = 117,
                TerminalStateValue = terminal
            };
            GhostPlaybackLogic.LoopUnit unit = ShortMemberUnit();
            var ends = new GhostPlaybackLogic.OverlapCopyEnds(117, recorded: true);
            double cycle = GhostPlaybackLogic.MemberOverlapCycleSeconds(unit, 57, false);
            Assert.Equal(OwnPeriod, cycle);
            Assert.False(GhostPlaybackLogic.HasOverlapCopyEnded(285, 0, 229, cycle, 57));
            Assert.True(GhostPlaybackLogic.HasOverlapCopyEnded(286.1, 0, 229, cycle, 57));
            Assert.Equal(explodes, GhostPlaybackLogic.OverlapCopyExplodes(ends, rec));
            Assert.Equal(reason, GhostPlaybackLogic.DescribeOverlapCopyEnd(rec, ends.WindowEndUT));
            // no watch camera there
            Assert.Equal(GhostPlaybackLogic.WatchedCopyEnd.NotWatched,
                GhostPlaybackLogic.DecideWatchedCopyEnd(ends, 0, explodes));
        }

        // ---- mesh, map, Tracking Station and watch clock agree ----

        [Theory]
        [InlineData(57.0)]
        [InlineData(58.25)]
        [InlineData(56.0)]
        [InlineData(100.0)]
        [InlineData(20.0)]
        public void OwnEnd_MeshMapTrackingStationAndWatchClock_AgreeOnWhenACopyExists(double period)
        {
            GhostPlaybackLogic.LoopUnit unit = ShortMemberUnit(period: period);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(unit, probe);
            var all = new List<IPlaybackTrajectory>
                { Ended(SpanStart, SpanEnd, null, "Ship"), Ended(60, 117, TerminalState.Landed) };
            var committed = new List<Recording>
            {
                new Recording { RecordingId = "a", VesselName = "a", ExplicitStartUT = SpanStart, ExplicitEndUT = SpanEnd },
                new Recording { RecordingId = "b", VesselName = "b", ExplicitStartUT = 60, ExplicitEndUT = 117 }
            };
            GhostPlaybackLogic.LoopUnitSet station = SetOf(unit);
            GhostPlaybackLogic.LoopUnitSet map = station.WithFallCopies();
            var heads = new List<(long cycle, double headUT)>();
            int drawn = 0, gap = 0;
            for (double now = 229.31; now < 229 + 4 * period; now += 0.5)
            {
                long newest = (long)Math.Floor((now - 229) / period);
                double phase = now - 229 - newest * period;
                if (phase <= 57 && !(engine.ghostStates.TryGetValue(CoreIndex, out var slot)
                        && slot.loopCycleIndex == newest))
                {
                    SelfOverlapFrame(engine, probe, unit, CoreIndex, all, Ctx(now));
                    Seed(engine, newest);
                }
                double ut = SelfOverlapFrame(engine, probe, unit, CoreIndex, all, Ctx(now));
                foreach (GhostPlaybackLogic.LoopUnitSet set in new[] { station, map })
                {
                    GhostMapPresence.OverlapCyclesForTesting(
                        committed[CoreIndex], CoreIndex, committed, set, now, out long first, out long last);
                    Assert.Equal(phase <= 57, first <= last);
                    Assert.True(GhostMapPresence.TryGetLiveOverlapHeadUTs(
                        committed[CoreIndex], CoreIndex, committed, set, now, heads));
                    Assert.Equal(phase <= 57, heads.Any(h => h.cycle == newest));
                }
                Assert.Equal(phase <= 57, engine.TryResolveUnitMemberPlaybackUT(
                    CoreIndex, now, 60, 117, out double watchUT));
                if (phase <= 57)
                {
                    drawn++;
                    Assert.Equal(60 + phase, ut, 6);
                    Assert.Equal(60 + phase, watchUT, 6);
                }
                else
                {
                    gap++;
                    Assert.True(double.IsNaN(ut), "a copy is drawn at " + now);
                    Assert.False(engine.ghostStates.ContainsKey(CoreIndex));
                }
            }
            Assert.True(drawn > 50);
            Assert.Equal(period > 57, gap > 0);
        }

        // ---- high warp, loop edits ----

        [Fact]
        public void OwnEnd_HighWarp_DropsACopyThatEndsUnderIt_AndKeepsTheWatchedOne()
        {
            GhostPlaybackLogic.LoopUnit unit = ShortMemberUnit();
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(unit, probe);
            var all = new List<IPlaybackTrajectory>
                { Ended(SpanStart, SpanEnd, null, "Ship"), Ended(60, 117, TerminalState.Destroyed) };
            Seed(engine, 0);
            SelfOverlapFrame(engine, probe, unit, CoreIndex, all, Ctx(285));
            SelfOverlapFrame(engine, probe, unit, CoreIndex, all, Ctx(300, 1000f), suppress: true);
            Assert.False(engine.ghostStates.ContainsKey(CoreIndex));
            SelfOverlapFrame(engine, probe, unit, CoreIndex, all, Ctx(310));
            Assert.Empty(events); // no late explosion

            Seed(engine, 1);
            SelfOverlapFrame(engine, probe, unit, CoreIndex, all, Watching(380, CoreIndex, 1));
            SelfOverlapFrame(engine, probe, unit, CoreIndex, all, Watching(400, CoreIndex, 1, 1000f), suppress: true);
            Assert.True(engine.ghostStates.ContainsKey(CoreIndex));
        }

        // Raised to 300 s (cycle 4 of the old launches is above the newest one) or lowered to 58 s while a
        // copy flies: it ends quietly on the frame of the edit, and nothing hangs at its start.
        [Theory]
        [InlineData(300.0)]
        [InlineData(58.0)]
        public void OwnEnd_PeriodEdited_EndsTheOldCopiesQuietly(double newPeriod)
        {
            GhostPlaybackLogic.LoopUnit old = ShortMemberUnit();
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(old, probe);
            var all = new List<IPlaybackTrajectory>
                { Ended(SpanStart, SpanEnd, null, "Ship"), Ended(60, 117, TerminalState.Destroyed) };
            Seed(engine, 4);
            double now = 229 + 4 * OwnPeriod + 10;
            Assert.Equal(70, SelfOverlapFrame(engine, probe, old, CoreIndex, all, Ctx(now)), 9);
            // 300 s is above the span, so that unit stops overlapping: its stamped copies go through the
            // span clock's undriven drop. 58 s keeps overlapping.
            if (newPeriod < SpanEnd - SpanStart)
            {
                GhostPlaybackLogic.LoopUnit unit = ShortMemberUnit(period: newPeriod);
                Edit(engine, unit);
                SelfOverlapFrame(engine, probe, unit, CoreIndex, all, Ctx(now + 0.1));
            }
            else
            {
                engine.overlapGhosts[CoreIndex] = new List<GhostPlaybackState> { engine.ghostStates[CoreIndex] };
                engine.ghostStates.Remove(CoreIndex);
                engine.DropUndrivenFallCopies(CoreIndex, all[CoreIndex], default(TrajectoryPlaybackFlags), Ctx(now + 0.1));
            }
            Assert.Equal(new[] { "cycle=4" }, destroyed);
            Assert.Equal(0, Copies(engine));
            Assert.DoesNotContain(events, e => e.Contains("explosion=True"));
            Assert.Contains(logLines, l => l.Contains("[Engine]") && l.Contains(
                "Loop copy ended quietly the loop's schedule changed under it: #1"));
            Assert.DoesNotContain(logLines, l => l.Contains("Fall copy"));
        }

        // Period raised inside the overlap: the old launches' copies are numbered above the newest one.
        [Fact]
        public void OwnEnd_PeriodRaised_LeavesNoCopyWaitingAtItsStart()
        {
            GhostPlaybackLogic.LoopUnit old = ShortMemberUnit(period: 30);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(old, probe);
            var all = new List<IPlaybackTrajectory>
                { Ended(SpanStart, SpanEnd, null, "Ship"), Ended(60, 117, TerminalState.Landed) };
            Seed(engine, 40);
            double now = 229 + 40 * 30 + 10;
            Assert.Equal(70, SelfOverlapFrame(engine, probe, old, CoreIndex, all, Ctx(now)), 9);
            GhostPlaybackLogic.LoopUnit unit = ShortMemberUnit(period: 100);
            Edit(engine, unit);
            double later = 229 + 12 * 100 + 60; // past cycle 12's end
            Assert.True(double.IsNaN(SelfOverlapFrame(engine, probe, unit, CoreIndex, all, Ctx(later))));
            Assert.False(engine.ghostStates.ContainsKey(CoreIndex));
            Assert.Equal(0, Copies(engine));
            Assert.Equal(new[] { "cycle=40" }, destroyed);
        }

        // ---- loops that do not overlap themselves ----

        [Fact]
        public void OwnEnd_StandaloneOverlapLoop_IsUntouched()
        {
            var probe = new ZoneProbe();
            var engine = new GhostPlaybackEngine(probe);
            engine.DestroyGhostResourcesOverrideForTesting = s => destroyed.Add("cycle=" + s.loopCycleIndex);
            engine.OnOverlapCameraAction += e => events.Add("camera " + e.Action + " cycle=" + e.NewCycleIndex);
            MockTrajectory traj = Ended(100, 160, TerminalState.Landed);
            traj.LoopPlayback = true;
            traj.LoopIntervalSeconds = 40;
            traj.LoopTimeUnit = LoopTimeUnit.Sec;
            engine.overlapGhosts[0] = new List<GhostPlaybackState>
                { new GhostPlaybackState { vesselName = "Core", loopCycleIndex = 0 } };
            // Watched, past its end: a standalone loop keeps the quiet bridge and prints no mission line.
            Frame(probe, () => engine.UpdateOverlapPlaybackForTesting(
                0, traj, default(TrajectoryPlaybackFlags), Watching(165, 0, 0),
                new GhostPlaybackState { vesselName = "Core", loopCycleIndex = 1 }, false));
            Assert.Empty(EndLines());
            Assert.DoesNotContain(events, e => e.Contains("EndHoldStart"));
            Assert.Equal(GhostPlaybackLogic.WatchedCopyEnd.NotWatched, GhostPlaybackLogic.DecideWatchedCopyEnd(
                default(GhostPlaybackLogic.OverlapCopyEnds), 0, false));
            // its period is always shorter than its loop, so its newest copy never reaches its end
            foreach (double period in new[] { 5.0, 40.0, 59.9 })
            {
                double cadence = GhostPlaybackLogic.ComputeEffectiveLaunchCadence(
                    period, 60, GhostPlayback.MaxOverlapGhostsPerRecording);
                for (double now = 100; now < 100 + 5 * cadence; now += 0.1)
                    Assert.False(GhostPlaybackLogic.HasOverlapCopyEnded(
                        now, (long)Math.Floor((now - 100) / cadence), 100, cadence, 60));
            }
        }

        [Fact]
        public void OwnEnd_UnitThatDoesNotOverlapItself_KeepsItsSpanClock()
        {
            GhostPlaybackLogic.LoopUnit unit = ShortMemberUnit(period: 300);
            Assert.False(GhostPlaybackLogic.UnitMemberOverlaps(unit));
            GhostPlaybackEngine engine = Engine(unit, new ZoneProbe());
            Assert.False(engine.IsSelfOverlapUnitMember(CoreIndex));
            Assert.True(Engine(ShortMemberUnit(), new ZoneProbe()).IsSelfOverlapUnitMember(CoreIndex));
            Assert.False(engine.IsSelfOverlapUnitMember(99));
            // the span clock answers past the member's own end, as before
            Assert.True(engine.TryResolveUnitMemberPlaybackUT(CoreIndex, 300, 60, 117, out double ut));
            Assert.Equal(131, ut, 9);
            Assert.False(engine.TryResolveSelfOverlapRestart(
                CoreIndex, new List<IPlaybackTrajectory>(), 300, out int target, out long cycle));
            Assert.Equal(-1, target);
            Assert.Equal(-1, cycle);
        }

        // ---- watch ----

        private const string Ship = "11111111111111111111111111111111";

        // Ascent #0 [31, 81] then transfer #1 [81, 140.5] of the same vessel; the period is longer than
        // either row: ascent cycle n plays over [200 + 100n, 250 + 100n].
        private static GhostPlaybackLogic.LoopUnit TwoRows(
            double period = OwnPeriod, double secondStart = 81, double firstEnd = 81)
        {
            return new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0, CoreIndex }, SpanStart, SpanEnd, SpanEnd - SpanStart, Anchor, period,
                new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>
                {
                    [0] = new GhostPlaybackLogic.LoopUnit.MemberWindow(SpanStart, firstEnd),
                    [CoreIndex] = new GhostPlaybackLogic.LoopUnit.MemberWindow(secondStart, SpanEnd)
                });
        }

        private static List<IPlaybackTrajectory> TwoRowTrajectories(
            TerminalState? firstTerminal = null, TerminalState? secondTerminal = TerminalState.Landed,
            string secondGuid = Ship)
        {
            return new List<IPlaybackTrajectory>
            {
                Ended(SpanStart, 81, firstTerminal, "Ship", Ship),
                Ended(81, SpanEnd, secondTerminal, "Ship", secondGuid)
            };
        }

        private GhostPlaybackEngine WatchEngine(GhostPlaybackLogic.LoopUnit unit, ZoneProbe probe)
        {
            GhostPlaybackEngine engine = Engine(unit, probe);
            engine.OnLoopCameraAction += e => events.Add(
                "handoff " + e.Action + " #" + e.Index + " cycle="
                + (e.RequiredCycleIndex.HasValue ? e.RequiredCycleIndex.Value.ToString() : "any"));
            return engine;
        }

        [Fact]
        public void Watch_RowChange_HoldsTheEndedCopy_AndHandsOverToThatLaunchsNextRow()
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows();
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = WatchEngine(unit, probe);
            List<IPlaybackTrajectory> all = TwoRowTrajectories();
            GhostPlaybackState ascent = Seed(engine, 0, 0);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(249, 0, 0));
            Assert.Empty(events);

            // The row ends: the copy disappears but stays the camera's target; the handoff names the next
            // row of the SAME launch, every frame until the watch lands there.
            foreach (double now in new[] { 250.1, 250.2, 250.3 })
            {
                events.Clear();
                Assert.True(double.IsNaN(SelfOverlapFrame(engine, probe, unit, 0, all, Watching(now, 0, 0))));
                Assert.Equal(new[] { "handoff UnitHandoffRetarget #1 cycle=0" }, events);
                Assert.Same(ascent, engine.ghostStates[0]);
                Assert.True(ascent.endedCopyHeld);
                Assert.Empty(destroyed);
            }
            string line = Assert.Single(EndLines());
            Assert.Contains("#0 \"Ship\" cycle=0 loopUT=81.10 endUT=81.00 reason=recorded-end watch=HoldForNextRow", line);
            Assert.Contains(logLines, l => l.Contains("[CameraFollow]")
                && l.Contains("unit watch: copy ended, holding its end for the next row: #0 \"Ship\" cycle=0"));
            Assert.False(engine.TryResolveUnitMemberPlaybackUT(0, 250.2, SpanStart, 81, out _));

            // The watch is on row 1 now: the held copy goes, with its one line already written.
            events.Clear();
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(250.4, CoreIndex, 0));
            Assert.False(engine.ghostStates.ContainsKey(0));
            Assert.Equal(new[] { "cycle=0" }, destroyed);
            Assert.Equal(QuietCycle0, events);
            Assert.Single(EndLines());
        }

        // A handoff first seen when the row is already over (the watch moved onto it a frame earlier, or
        // the row between starts a moment later) still fires: the held copy keeps it pending.
        [Fact]
        public void Watch_SampleSizedGapBetweenRows_WaitsForTheNextRow()
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows(secondStart: 83);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = WatchEngine(unit, probe);
            List<IPlaybackTrajectory> all = TwoRowTrajectories();
            GhostPlaybackState ascent = Seed(engine, 0, 0);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(249, 0, 0));
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(250.5, 0, 0));
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(251.5, 0, 0));
            Assert.Empty(events);
            Assert.Same(ascent, engine.ghostStates[0]);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(252.1, 0, 0));
            Assert.Equal(new[] { "handoff UnitHandoffRetarget #1 cycle=0" }, events);
        }

        // The next launch is already flying: the older watched copy holds beside the slot and the handoff
        // still names its own launch, never the newer one.
        [Fact]
        public void Watch_RowChange_OfAnOlderLaunch_NeverNamesTheNewerOne()
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows(period: 30);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = WatchEngine(unit, probe);
            List<IPlaybackTrajectory> all = TwoRowTrajectories();
            var older = new GhostPlaybackState { vesselName = "Ship", loopCycleIndex = 0, fallClockOffsetUT = 200 - SpanStart };
            engine.overlapGhosts[0] = new List<GhostPlaybackState> { older };
            Seed(engine, 1, 0);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(249, 0, 0));
            events.Clear();
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(250.1, 0, 0));
            Assert.Equal(new[] { "handoff UnitHandoffRetarget #1 cycle=0" }, events);
            // no ended copy is destroyed under the camera
            Assert.DoesNotContain("cycle=0", destroyed);
            Assert.True(engine.TryGetCopyOfCycle(0, 0, out GhostPlaybackState copy));
            Assert.Same(older, copy);
            Assert.True(engine.TryGetCopyOfCycle(0, 1, out copy));
            Assert.Equal(1, copy.loopCycleIndex);
            Assert.False(engine.TryGetCopyOfCycle(CoreIndex, 0, out _));
        }

        // Crash of the watched row, with the launch's other rows still flying: the explosion hold, no
        // handoff in that frame and none while the camera holds (-2).
        [Fact]
        public void Watch_Crash_HoldsAtTheExplosion_AndNoHandoffFightsIt()
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows();
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = WatchEngine(unit, probe);
            List<IPlaybackTrajectory> all = TwoRowTrajectories(
                TerminalState.Destroyed, secondGuid: "22222222222222222222222222222222");
            Seed(engine, 0, 0);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(249, 0, 0));
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(250.1, 0, 0));
            Assert.Equal(Cycle0Ending, events);
            Assert.Contains("reason=destroyed watch=HoldAtExplosion", Assert.Single(EndLines()));
            events.Clear();
            for (double now = 250.2; now < 320; now += 0.5)
                SelfOverlapFrame(engine, probe, unit, 0, all,
                    Watching(now, 0, GhostPlaybackEngine.WatchHoldCycleIndex));
            Assert.Empty(events);
        }

        // The newest launch crosses the same row change while the camera holds at an end: no handoff.
        [Theory]
        [InlineData(GhostPlaybackEngine.WatchHoldCycleIndex, false)]
        [InlineData(-1L, true)]   // the control: an unpinned camera does follow it
        public void Watch_Hold_IsNotPulledAway_ByTheNewestLaunch(long cycle, bool handsOver)
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows();
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = WatchEngine(unit, probe);
            List<IPlaybackTrajectory> all = TwoRowTrajectories();
            Seed(engine, 0, 0);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(248, 0, cycle));
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(249, 0, cycle));
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(250.1, 0, cycle));
            Assert.Equal(handsOver, events.Contains("handoff UnitHandoffRetarget #1 cycle=0"));
        }

        // Even if the rows look like one vessel, a crash is never left for another row in the same frame.
        [Fact]
        public void Watch_ExplosionWinsOverANextRow()
        {
            var ends = new GhostPlaybackLogic.OverlapCopyEnds(
                81, recorded: true, watchedCycle: 3, watchedRowContinues: true);
            Assert.Equal(GhostPlaybackLogic.WatchedCopyEnd.HoldAtExplosion,
                GhostPlaybackLogic.DecideWatchedCopyEnd(ends, 3, explodes: true));
            Assert.Equal(GhostPlaybackLogic.WatchedCopyEnd.HoldForNextRow,
                GhostPlaybackLogic.DecideWatchedCopyEnd(ends, 3, explodes: false));
            Assert.Equal(GhostPlaybackLogic.WatchedCopyEnd.NotWatched,
                GhostPlaybackLogic.DecideWatchedCopyEnd(ends, 2, explodes: false));
            Assert.Equal(GhostPlaybackLogic.WatchedCopyEnd.HoldThenRestart,
                GhostPlaybackLogic.DecideWatchedCopyEnd(
                    new GhostPlaybackLogic.OverlapCopyEnds(81, true, 3, false), 3, explodes: false));
            Assert.Equal(GhostPlaybackLogic.WatchedCopyEnd.NotWatched,
                GhostPlaybackLogic.DecideWatchedCopyEnd(
                    new GhostPlaybackLogic.OverlapCopyEnds(81, true), -1, explodes: true));
        }

        // The last row lands: the camera holds there (EndHoldStart), and the copy is gone at once.
        [Theory]
        [InlineData(TerminalState.Landed)]
        [InlineData(TerminalState.Orbiting)]
        public void Watch_LastRowsQuietEnd_HoldsTheCamera(TerminalState terminal)
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows();
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = WatchEngine(unit, probe);
            List<IPlaybackTrajectory> all = TwoRowTrajectories(secondTerminal: terminal);
            Seed(engine, 0);
            SelfOverlapFrame(engine, probe, unit, CoreIndex, all, Watching(309, CoreIndex, 0));
            SelfOverlapFrame(engine, probe, unit, CoreIndex, all, Watching(309.6, CoreIndex, 0));
            Assert.Equal(new[] { "camera EndHoldStart cycle=0", "expired cycle=0 explosion=False" }, events);
            Assert.False(engine.ghostStates.ContainsKey(CoreIndex));
            Assert.Contains("watch=HoldThenRestart", Assert.Single(EndLines()));
        }

        // A stretch between the rows the player trimmed out is not a row change.
        [Fact]
        public void Watch_TrimmedOutStretch_IsAnEnd_NotARowChange()
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows(secondStart: 120);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = WatchEngine(unit, probe);
            List<IPlaybackTrajectory> all = TwoRowTrajectories();
            Seed(engine, 0, 0);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(249, 0, 0));
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(250.1, 0, 0));
            Assert.Contains("camera EndHoldStart cycle=0", events);
            Assert.False(engine.ghostStates.ContainsKey(0));
        }

        // High warp drops other copies but never the held target in the slot.
        [Fact]
        public void Watch_HeldCopy_SurvivesHighWarp()
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows();
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = WatchEngine(unit, probe);
            List<IPlaybackTrajectory> all = TwoRowTrajectories();
            GhostPlaybackState ascent = Seed(engine, 0, 0);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(249, 0, 0));
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(250.1, 0, 0));
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(260, 0, 0, 1000f), suppress: true);
            Assert.Same(ascent, engine.ghostStates[0]);
            Assert.Empty(destroyed);
        }

        // An edit that un-trims the held row gives it more to play: a hidden copy never plays it.
        [Fact]
        public void Watch_HeldCopy_WhoseRowGrows_IsReplaced()
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows(firstEnd: 70, secondStart: 70);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = WatchEngine(unit, probe);
            List<IPlaybackTrajectory> all = TwoRowTrajectories();
            Seed(engine, 0, 0);
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(238, 0, 0));
            SelfOverlapFrame(engine, probe, unit, 0, all, Watching(239.1, 0, 0));
            Assert.True(engine.ghostStates[0].endedCopyHeld);
            GhostPlaybackLogic.LoopUnit grown = TwoRows();
            Edit(engine, grown);
            SelfOverlapFrame(engine, probe, grown, 0, all, Watching(239.2, 0, 0));
            Assert.Equal(new[] { "cycle=0" }, destroyed);
            Assert.Contains(logLines, l => l.Contains("Overlap: SpawnGhost failed for #0 cycle=0"));
        }

        [Theory]
        [InlineData(0L, 249.9, true)]    // the next row starts within the look-ahead
        [InlineData(0L, 246.0, false)]
        [InlineData(0L, 250.1, true)]
        [InlineData(0L, 309.4, true)]
        [InlineData(0L, 309.6, false)]   // the launch is over
        [InlineData(1L, 350.1, true)]    // each launch on its own clock
        [InlineData(1L, 250.1, false)]
        [InlineData(-1L, 250.1, false)]
        public void ContinuingRow_ReadsTheWatchedLaunchsOwnClock(long cycle, double now, bool continues)
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows();
            Assert.Equal(continues, GhostPlaybackLogic.HasContinuingRow(unit, TwoRowTrajectories(), 0, cycle, now));
            // another vessel's row never continues it
            Assert.False(GhostPlaybackLogic.HasContinuingRow(
                unit, TwoRowTrajectories(secondGuid: "22222222222222222222222222222222"), 0, cycle, now));
            Assert.False(GhostPlaybackLogic.HasContinuingRow(unit, null, 0, cycle, now));
            Assert.False(GhostPlaybackLogic.HasContinuingRow(unit, TwoRowTrajectories(), 7, cycle, now));
        }

        // After a hold: the first row's live copy, else the newest launch's live row, else any row's slot.
        [Fact]
        public void Restart_PicksACopyThatIsDrawn()
        {
            GhostPlaybackLogic.LoopUnit unit = TwoRows();
            GhostPlaybackEngine engine = WatchEngine(unit, new ZoneProbe());
            List<IPlaybackTrajectory> all = TwoRowTrajectories();
            Assert.False(engine.TryResolveSelfOverlapRestart(CoreIndex, all, 312, out int target, out long cycle));
            Assert.Equal(-1, target);

            // 312: launch 1 is 12 s into its ascent
            Seed(engine, 1, 0);
            Assert.True(engine.TryResolveSelfOverlapRestart(CoreIndex, all, 312, out target, out cycle));
            Assert.Equal(0, target);
            Assert.Equal(1, cycle);

            // 360: launch 1's ascent is over (a held copy is not drawn), its second row flies
            engine.ghostStates[0].endedCopyHeld = true;
            Seed(engine, 1);
            Assert.True(engine.TryResolveSelfOverlapRestart(0, all, 360, out target, out cycle));
            Assert.Equal(CoreIndex, target);
            Assert.Equal(1, cycle);

            // only an older launch's copy is left
            Seed(engine, 0);
            Assert.True(engine.TryResolveSelfOverlapRestart(0, all, 360, out target, out cycle));
            Assert.Equal(CoreIndex, target);
            Assert.Equal(0, cycle);
            Assert.False(engine.TryResolveSelfOverlapRestart(99, all, 360, out target, out cycle));
        }

        // ---- the watch controller ----

        private static WatchModeController Controller(GhostPlaybackEngine engine, int watched, long cycle)
        {
            var flight = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            SetField(flight, "engine", engine);
            var controller = new WatchModeController(flight);
            SetField(flight, "watchMode", controller);
            SetField(controller, "watchedRecordingIndex", watched);
            SetField(controller, "watchedRecordingId", "rec-" + watched);
            SetField(controller, "watchedOverlapCycleIndex", cycle);
            return controller;
        }

        private static FieldInfo Field(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return field;
        }

        private static void SetField(object target, string name, object value) => Field(target, name).SetValue(target, value);

        private static T GetField<T>(object target, string name) => (T)Field(target, name).GetValue(target);

        private static void CommitTwoRows()
        {
            RecordingStore.ResetForTesting();
            RecordingStore.AddCommittedInternal(new Recording
                { RecordingId = "rec-0", VesselName = "Ship", ExplicitStartUT = SpanStart, ExplicitEndUT = 81 });
            RecordingStore.AddCommittedInternal(new Recording
                { RecordingId = "rec-1", VesselName = "Ship", ExplicitStartUT = 81, ExplicitEndUT = SpanEnd });
        }

        // The handoff waits for its own launch's copy: the slot's copy of another launch is not it.
        [Fact]
        public void Watch_Transfer_WaitsForItsOwnLaunchsCopy()
        {
            CommitTwoRows();
            try
            {
                GhostPlaybackEngine engine = Engine(TwoRows(), new ZoneProbe());
                WatchModeController controller = Controller(engine, 0, 1);
                MethodInfo prepare = typeof(WatchModeController).GetMethod(
                    "TryPrepareTransferCopy", BindingFlags.Instance | BindingFlags.NonPublic);
                bool Ready(long cycle, out GhostPlaybackState copy)
                {
                    var args = new object[] { CoreIndex, RecordingStore.CommittedRecordings[CoreIndex], cycle, null };
                    bool ready = (bool)prepare.Invoke(controller, args);
                    copy = (GhostPlaybackState)args[3];
                    return ready;
                }
                GhostPlaybackState launch0 = Seed(engine, 0); // row 1's slot still shows launch 0
                Assert.False(Ready(1, out GhostPlaybackState found));
                Assert.Null(found);
                // an overlap copy whose mesh is not built is never forced: the handoff asks again
                engine.ghostStates.Remove(CoreIndex);
                engine.overlapGhosts[CoreIndex] = new List<GhostPlaybackState> { launch0 };
                Assert.False(Ready(0, out found));
                Assert.Same(launch0, found);
            }
            finally
            {
                RecordingStore.ResetForTesting();
            }
        }

        // The hold ends with nothing to follow: it goes on, and the session never ends there.
        [Fact]
        public void Watch_HoldWithNoCopyToFollow_GoesOn()
        {
            CommitTwoRows();
            Func<double> now = WatchModeController.CurrentUTNow;
            try
            {
                WatchModeController.CurrentUTNow = () => 400;
                GhostPlaybackEngine engine = Engine(TwoRows(), new ZoneProbe());
                WatchModeController controller = Controller(engine, 0, GhostPlaybackEngine.WatchHoldCycleIndex);
                SetField(controller, "overlapRetargetAfterUT", 253.0);
                MethodInfo restart = typeof(WatchModeController).GetMethod(
                    "TryRestartMissionWatchAfterHold", BindingFlags.Instance | BindingFlags.NonPublic);
                for (int i = 0; i < 20; i++)
                    Assert.False((bool)restart.Invoke(controller, null));
                controller.ValidateWatchedGhostStillActive();
                Assert.Equal(0, controller.WatchedRecordingIndex);
                Assert.Equal(GhostPlaybackEngine.WatchHoldCycleIndex, controller.WatchedLoopCycleIndex);
                Assert.Equal(253.0, GetField<double>(controller, "overlapRetargetAfterUT"));
                Assert.Contains(logLines, l => l.Contains("[CameraFollow]")
                    && l.Contains("Overlap: hold of #0 goes on, no copy of the mission to follow yet"));
            }
            finally
            {
                WatchModeController.CurrentUTNow = now;
                RecordingStore.ResetForTesting();
            }
        }

        // The camera side that needs the game, pinned in the source: the hold sentinel, the restart that
        // replaces the old root-only retarget, and the bridge that becomes a hold instead of an exit.
        [Fact]
        public void Watch_ControllerWiring()
        {
            string watch = ReadSource("Controllers", "WatchModeController.cs");
            Assert.Contains("TransferWatchToNextSegment(evt.Index, evt.RequiredCycleIndex)", watch);
            Assert.Contains("if (requiredCycle.HasValue && !TryPrepareTransferCopy(nextIndex, committed[nextIndex], requiredCycle.Value, out cycleCopy)) { LogAutoFollowDeferred(nextIndex, committed[nextIndex].RecordingId); return false; }", watch);
            Assert.Contains("case CameraActionType.EndHoldStart: if (ShouldIgnoreOverlapCycleEvent(evt.NewCycleIndex, watchedOverlapCycleIndex)) break;", watch);
            Assert.Contains("watchedOverlapCycleIndex = GhostPlaybackEngine.WatchHoldCycleIndex; watchNoTargetFrames = 0;", watch);
            Assert.Contains("if (host.Engine.IsSelfOverlapUnitMember(watchedRecordingIndex)) return !TryRestartMissionWatchAfterHold();", watch);
            Assert.Contains("if (bridgeState == OverlapBridgeRetargetState.ExitWatch && host.Engine.IsSelfOverlapUnitMember(watchedRecordingIndex))", watch);
        }
    }
}
