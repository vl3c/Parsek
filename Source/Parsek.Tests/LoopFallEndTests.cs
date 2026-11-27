using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Where a loop unit's predicted fall ENDS: at its own end, on every path that draws it. A launch
    /// period longer than the fall leaves a gap with no copy (mesh and map), high warp leaves no copy
    /// behind. A copy whose end is recorded ends there the same way (LoopFallEndTests.OwnEnds.cs).
    /// </summary>
    [Collection("Sequential")]
    public partial class LoopFallEndTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> destroyed = new List<string>();
        private readonly List<string> events = new List<string>();

        public LoopFallEndTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GhostPlaybackLogic.ResetForTesting();
        }

        public void Dispose()
        {
            GhostPlaybackLogic.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // Records the playback UT of every copy the frame reaches, then leaves the frame before any
        // mesh work.
        private sealed class ZoneProbe : IGhostPositioner
        {
            public readonly List<double> PlaybackUTs = new List<double>();
            public bool HideByZone; // the copy is far away: the frame goes on to the next copy

            public ZoneRenderingResult ApplyZoneRendering(int index, GhostPlaybackState state,
                IPlaybackTrajectory traj, double distance, double playbackUT, int protectedIndex)
            {
                PlaybackUTs.Add(playbackUT);
                if (HideByZone) return new ZoneRenderingResult { hiddenByZone = true };
                throw new OperationCanceledException();
            }

            public void InterpolateAndPosition(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double ut, bool suppressFx) { }
            public void InterpolateAndPositionRelative(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double ut, bool suppressFx, RelativeSectionPlaybackTarget target) { }
            public bool TryPositionFromBodyFixedPrimary(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double playbackUT, RelativeSectionPlaybackTarget target, out double a, out double b) { a = b = double.NaN; return false; }
            public void PositionAtPoint(int index, IPlaybackTrajectory traj, GhostPlaybackState state, TrajectoryPoint point) { }
            public void PositionAtSurface(int index, IPlaybackTrajectory traj, GhostPlaybackState state) { }
            public void PositionFromOrbit(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double ut) { }
            public void PositionLoop(int index, IPlaybackTrajectory traj, GhostPlaybackState state, double ut, bool suppressFx) { }
            public bool TryResolveExplosionAnchorPosition(int index, IPlaybackTrajectory traj, GhostPlaybackState state, out Vector3 world) { world = default(Vector3); return false; }
        }

        // Recorded span [31, 140.5]; the core falls over [119, 332.4] (213.4 s); launches from 200, so the
        // core's cycle n starts at 288 + n * period.
        private const double Anchor = 200, SpanStart = 31, SpanEnd = 140.5, CoreStart = 119, CoreEnd = 332.4;
        private const double CoreSchedule = Anchor + (CoreStart - SpanStart);
        private const double Fall = CoreEnd - CoreStart;
        private const int CoreIndex = 1;
        private const int DebrisIndex = 2;

        private static GhostPlaybackLogic.LoopUnit Unit(
            double period, IReadOnlyList<GhostPlaybackLogic.LoopCut> cuts = null)
        {
            return new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0, CoreIndex }, SpanStart, SpanEnd, Math.Max(period, SpanEnd - SpanStart), Anchor,
                period,
                new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>
                {
                    [0] = new GhostPlaybackLogic.LoopUnit.MemberWindow(SpanStart, SpanEnd),
                    [CoreIndex] = new GhostPlaybackLogic.LoopUnit.MemberWindow(CoreStart, CoreEnd)
                }, null, null, null, cuts, renderSpanEndUT: CoreEnd);
        }

        private static GhostPlaybackLogic.LoopUnit CutUnit(double period) =>
            Unit(period, new[] { new GhostPlaybackLogic.LoopCut { StartUT = 60, LengthSeconds = 20 } });

        private static GhostPlaybackLogic.LoopUnitSet SetOf(GhostPlaybackLogic.LoopUnit unit) =>
            new GhostPlaybackLogic.LoopUnitSet(
                new Dictionary<int, GhostPlaybackLogic.LoopUnit> { [unit.OwnerIndex] = unit },
                unit.MemberIndices.ToDictionary(i => i, i => unit.OwnerIndex));

        private static FrameContext Ctx(double ut, float warp = 1f, int watched = -1) => new FrameContext
        {
            currentUT = ut, warpRate = warp, activeVesselPos = new Vector3d(0, 0, 0),
            protectedIndex = watched, protectedLoopCycleIndex = -1, autoLoopIntervalSeconds = 30
        };

        private static MockTrajectory Traj(double start, double end, bool predictedEnd, string name = "Core")
        {
            // IsDebris keeps a relaunch's spawn on the early-fail path, so the call stays managed.
            var traj = new MockTrajectory
            {
                IsDebris = true, VesselName = name, RecordingId = name,
                TerminalStateValue = TerminalState.Destroyed
            }.WithTimeRange(start, end);
            if (predictedEnd)
                traj.TrackSections.Add(new TrackSection
                    { startUT = end - 30, endUT = end, source = TrackSectionSource.Predicted });
            return traj;
        }

        private GhostPlaybackEngine Engine(GhostPlaybackLogic.LoopUnit unit, ZoneProbe probe)
        {
            var engine = new GhostPlaybackEngine(probe);
            engine.DestroyGhostResourcesOverrideForTesting = s => destroyed.Add("cycle=" + s.loopCycleIndex);
            engine.OnOverlapExpired += e => events.Add("expired cycle=" + e.CycleIndex + " explosion=" + e.ExplosionFired);
            engine.OnOverlapCameraAction += e => events.Add("camera " + e.Action + " cycle=" + e.NewCycleIndex);
            engine.SetLoopUnits(SetOf(unit));
            return engine;
        }

        private static readonly string[] Cycle0Ending =
            { "camera ExplosionHoldStart cycle=0", "expired cycle=0 explosion=True" };

        private static GhostPlaybackState Seed(GhostPlaybackEngine engine, long cycle, int index = CoreIndex)
        {
            var state = new GhostPlaybackState { vesselName = "Core", loopCycleIndex = cycle };
            engine.ghostStates[index] = state;
            return state;
        }

        // One frame; the first playback UT it reached a copy at (the primary's), or NaN when it drew none.
        private static double Frame(ZoneProbe probe, Action frame)
        {
            probe.PlaybackUTs.Clear();
            try { frame(); }
            catch (OperationCanceledException) { }
            return probe.PlaybackUTs.Count > 0 ? probe.PlaybackUTs[0] : double.NaN;
        }

        private static double MemberFrame(
            GhostPlaybackEngine engine, ZoneProbe probe, GhostPlaybackLogic.LoopUnit unit,
            IPlaybackTrajectory traj, FrameContext ctx, bool suppress = false)
        {
            engine.ghostStates.TryGetValue(CoreIndex, out GhostPlaybackState state);
            bool active = false;
            return Frame(probe, () => engine.UpdateUnitMemberFallSchedule(
                CoreIndex, traj, default(TrajectoryPlaybackFlags), ctx, unit, "unit-0", CoreStart, CoreEnd,
                suppress, suppress, ref state, ref active));
        }

        private static double DebrisFrame(
            GhostPlaybackEngine engine, ZoneProbe probe, MockTrajectory debris, FrameContext ctx,
            bool suppress = false)
        {
            var parent = new MockTrajectory { VesselName = "Stack" }.WithTimeRange(SpanStart, SpanEnd);
            debris.LoopSyncParentIdx = 0;
            var all = new List<IPlaybackTrajectory> { parent, parent, debris };
            engine.ghostStates.TryGetValue(DebrisIndex, out GhostPlaybackState state);
            bool active = false;
            return Frame(probe, () => Assert.True(engine.TryUpdateLoopSyncedDebris(
                DebrisIndex, debris, default(TrajectoryPlaybackFlags), ctx, all, suppress, suppress,
                debris.StartUT, true, true, false, false, ref state, ref active)));
        }

        private static int Copies(GhostPlaybackEngine engine, int index = CoreIndex) =>
            engine.TryGetOverlapGhosts(index, out var overlaps) ? overlaps.Count : 0;

        // ---- the pure clock ----

        [Theory]
        [InlineData(495.0, false)]
        [InlineData(501.3, false)]
        [InlineData(501.5, true)]
        [InlineData(1187.0, true)]
        [InlineData(287.0, false)]         // before cycle 0: its phase is negative
        [InlineData(1188.5, false)]        // cycle 1 is falling
        public void CopyHasEnded_OncePastItsOwnDuration(double now, bool ended)
        {
            long newest = Math.Max(0, (long)Math.Floor((now - CoreSchedule) / 900));
            Assert.Equal(ended, GhostPlaybackLogic.HasOverlapCopyEnded(now, newest, CoreSchedule, 900, Fall));
            Assert.False(GhostPlaybackLogic.HasOverlapCopyEnded(now, -1, CoreSchedule, 900, Fall));
            Assert.False(GhostPlaybackLogic.HasOverlapCopyEnded(
                now, GhostPlaybackLogic.FirstRunFallCycleIndex, CoreSchedule, 900, Fall));
        }

        [Fact]
        public void EndsInPredictedTail_ReadsTheDataAtTheWindowsEndOnly()
        {
            Assert.False(GhostPlaybackLogic.EndsInPredictedTail(null, 300));
            MockTrajectory recorded = Traj(100, 300, predictedEnd: false);
            Assert.False(GhostPlaybackLogic.EndsInPredictedTail(recorded, 300));
            recorded.OrbitSegments.Add(new OrbitSegment { startUT = 250, endUT = 300, isPredicted = false });
            recorded.OrbitSegments.Add(new OrbitSegment { startUT = 150, endUT = 200, isPredicted = true });
            Assert.False(GhostPlaybackLogic.EndsInPredictedTail(recorded, 300));
            recorded.OrbitSegments.Add(new OrbitSegment { startUT = 200, endUT = 300, isPredicted = true });
            Assert.True(GhostPlaybackLogic.EndsInPredictedTail(recorded, 300));
            // a window trimmed to before the tail ends in recorded data
            MockTrajectory chute = Traj(100, 300, predictedEnd: true);
            Assert.True(GhostPlaybackLogic.EndsInPredictedTail(chute, 300));
            Assert.False(GhostPlaybackLogic.EndsInPredictedTail(chute, 260));
            // an explicit end a sample step past the last predicted section
            Assert.True(GhostPlaybackLogic.EndsInPredictedTail(chute, 300.4));
            Assert.False(GhostPlaybackLogic.EndsInPredictedTail(chute, 310));
        }

        // ---- a uniform unit, the member path ----

        [Fact]
        public void Member_PeriodLongerThanTheFall_EndsAtItsImpact_AndWaitsForTheNextLaunch()
        {
            var unit = Unit(900);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(unit, probe);
            MockTrajectory traj = Traj(CoreStart, CoreEnd, predictedEnd: true);
            Seed(engine, 0);

            Assert.Equal(CoreStart + 207, MemberFrame(engine, probe, unit, traj, Ctx(495)), 9);
            Assert.Empty(events);
            // impact at 501.4
            Assert.True(double.IsNaN(MemberFrame(engine, probe, unit, traj, Ctx(502))));
            Assert.Equal(new[] { "cycle=0" }, destroyed);
            Assert.Equal(Cycle0Ending, events);
            Assert.Contains(logLines, l => l.Contains("[Engine]") && l.Contains(
                "Fall copy reached its end before the next launch: #1 \"Core\" cycle=0 newestCycle=0 UT=502.00 duration=213.40"));
            Assert.Contains(logLines, l => l.Contains("[Engine]") && l.Contains(
                "Ghost EXITED range: #1 \"Core\" cycle=0 (overlap expired)"));
            foreach (double now in new[] { 600.0, 1100.0, 1187.0 })
            {
                Assert.True(double.IsNaN(MemberFrame(engine, probe, unit, traj, Ctx(now))), "drawn at " + now);
                Assert.False(engine.ghostStates.ContainsKey(CoreIndex));
                Assert.Equal(0, Copies(engine));
            }
            Assert.Equal(2, events.Count);
            Assert.DoesNotContain(logLines, l => l.Contains("SpawnGhost failed"));
            // the next launch spawns cycle 1 (this host cannot build its mesh)
            MemberFrame(engine, probe, unit, traj, Ctx(1189));
            Assert.Contains(logLines, l => l.Contains("Overlap: SpawnGhost failed for #1 cycle=1"));
        }

        // Three launches sampled every half second: the newest copy is drawn exactly while its own clock
        // runs, and the map lists a copy exactly then.
        [Theory]
        [InlineData(Fall)]
        [InlineData(Fall + 1.25)]
        [InlineData(Fall - 1.25)]
        [InlineData(300)]
        [InlineData(150)]
        public void Member_PeriodNearTheFall_TheNewestCopyEndsAtItsOwnEnd_OnMeshAndMap(double period)
        {
            var unit = Unit(period);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(unit, probe);
            MockTrajectory traj = Traj(CoreStart, CoreEnd, predictedEnd: true);
            var committed = new List<Recording>
            {
                new Recording { RecordingId = "a", VesselName = "a", ExplicitStartUT = SpanStart, ExplicitEndUT = SpanEnd },
                new Recording { RecordingId = "b", VesselName = "b", ExplicitStartUT = CoreStart, ExplicitEndUT = CoreEnd }
            };
            GhostPlaybackLogic.LoopUnitSet map = SetOf(unit).WithFallCopies();
            int drawn = 0, gap = 0;
            for (double now = CoreSchedule + 0.25; now < CoreSchedule + 2.9 * period; now += 0.5)
            {
                long newest = (long)Math.Floor((now - CoreSchedule) / period);
                double phase = now - CoreSchedule - newest * period;
                // A launch's own spawn cannot run here: seed the copy it leaves in the slot.
                if (phase <= Fall && !(engine.ghostStates.TryGetValue(CoreIndex, out var slot)
                        && slot.loopCycleIndex == newest))
                {
                    MemberFrame(engine, probe, unit, traj, Ctx(now));
                    Seed(engine, newest);
                }
                double ut = MemberFrame(engine, probe, unit, traj, Ctx(now));
                GhostMapPresence.OverlapCyclesForTesting(
                    committed[CoreIndex], CoreIndex, committed, map, now, out long first, out long last);
                Assert.Equal(phase <= Fall, first <= last);
                if (phase <= Fall)
                {
                    drawn++;
                    Assert.Equal(CoreStart + phase, ut, 6);
                    Assert.Equal(newest, engine.ghostStates[CoreIndex].loopCycleIndex);
                }
                else
                {
                    gap++;
                    Assert.True(double.IsNaN(ut), "a copy is drawn at " + now);
                    Assert.False(engine.ghostStates.ContainsKey(CoreIndex));
                    Assert.Equal(0, Copies(engine));
                }
            }
            Assert.True(drawn > 100);
            Assert.Equal(period > Fall, gap > 0);
        }

        [Fact]
        public void Member_FirstRunHandover_PeriodLongerThanTheFall()
        {
            var unit = Unit(900);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(unit, probe);
            MockTrajectory traj = Traj(CoreStart, CoreEnd, predictedEnd: true);
            GhostPlaybackState firstRun = Seed(engine, -1);

            // handed over while falling: it stays on the real clock
            Assert.Equal(210, MemberFrame(engine, probe, unit, traj, Ctx(210)), 9);
            Assert.Equal(GhostPlaybackLogic.FirstRunFallCycleIndex, firstRun.loopCycleIndex);
            GhostPlaybackState cycle0 = Seed(engine, 0);
            MemberFrame(engine, probe, unit, traj, Ctx(300));          // cycle 0 falls beside it
            Assert.True(engine.TryGetOverlapGhosts(CoreIndex, out var overlaps));
            Assert.Same(firstRun, overlaps.Single());
            Assert.Same(cycle0, engine.ghostStates[CoreIndex]);
            Assert.True(double.IsNaN(MemberFrame(engine, probe, unit, traj, Ctx(502))));
            Assert.Equal(0, Copies(engine));
            // both ended; only the loop copy raises watch events
            Assert.Equal(new[] { "cycle=-3", "cycle=0" }, destroyed.OrderBy(d => d, StringComparer.Ordinal));
            Assert.Equal(Cycle0Ending, events);

            // an ordinary ghost found past its end in the gap goes, with no watch event
            events.Clear();
            Seed(engine, -1);
            Assert.True(double.IsNaN(MemberFrame(engine, probe, unit, traj, Ctx(700))));
            Assert.False(engine.ghostStates.ContainsKey(CoreIndex));
            Assert.Equal(0, Copies(engine));
            Assert.Empty(events);
        }

        // ---- warp ----

        [Fact]
        public void Member_HighWarp_DropsACopyThatEndsUnderIt()
        {
            var unit = Unit(900);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(unit, probe);
            MockTrajectory traj = Traj(CoreStart, CoreEnd, predictedEnd: true);
            Seed(engine, 0);
            MemberFrame(engine, probe, unit, traj, Ctx(400, 1000f), suppress: true);
            Assert.True(engine.ghostStates.ContainsKey(CoreIndex));    // hidden, still falling
            MemberFrame(engine, probe, unit, traj, Ctx(600, 1000f), suppress: true);
            Assert.False(engine.ghostStates.ContainsKey(CoreIndex));
            // back below the warp limit in the gap: no late explosion, no copy
            Assert.True(double.IsNaN(MemberFrame(engine, probe, unit, traj, Ctx(700))));
            Assert.Empty(events);
        }

        [Fact]
        public void Member_HighWarp_KeepsTheWatchedCopy_AndEndsItOnceBelowTheLimit()
        {
            var unit = Unit(900);
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(unit, probe);
            MockTrajectory traj = Traj(CoreStart, CoreEnd, predictedEnd: true);
            Seed(engine, 0);
            MemberFrame(engine, probe, unit, traj, Ctx(400, watched: CoreIndex));
            MemberFrame(engine, probe, unit, traj, Ctx(600, 1000f, watched: CoreIndex), suppress: true);
            Assert.True(engine.ghostStates.ContainsKey(CoreIndex));
            MemberFrame(engine, probe, unit, traj, Ctx(700, watched: CoreIndex));
            MemberFrame(engine, probe, unit, traj, Ctx(701, watched: CoreIndex));
            Assert.Equal(Cycle0Ending, events);
            Assert.False(engine.ghostStates.ContainsKey(CoreIndex));
        }

        [Theory]
        [InlineData(360.0, 1L)]        // cycle 1 has launched, the core's window has not opened
        [InlineData(2000.0, 12L)]
        public void NonUniform_HighWarp_LeavesNoOlderFallCopy(double now, long unitCycle)
        {
            GhostPlaybackLogic.LoopUnit unit = CutUnit(150);
            GhostPlaybackEngine engine = Engine(unit, new ZoneProbe());
            MockTrajectory traj = Traj(CoreStart, CoreEnd, predictedEnd: true);
            engine.overlapGhosts[CoreIndex] = new List<GhostPlaybackState>
                { new GhostPlaybackState { vesselName = "Core", loopCycleIndex = 0 } };
            GhostPlaybackState state = null;
            bool active = false;
            var decision = GhostPlaybackLogic.UnitMemberRenderDecision.HiddenOutsideWindow;
            double ut = SpanStart + 10;
            bool keep = engine.UpdateNonUniformFall(
                CoreIndex, traj, default(TrajectoryPlaybackFlags), Ctx(now, 1000f), unit, unitCycle,
                CoreStart, CoreEnd, true, true, ref decision, ref ut, ref state, ref active);
            engine.HideUnitMemberOutsideWindow(
                CoreIndex, traj, default(TrajectoryPlaybackFlags), Ctx(now, 1000f), unit, "unit-0", decision, ut,
                CoreStart, CoreEnd, false, false, false, 0, 0, true, true, state, active);
            Assert.False(keep);
            Assert.Equal(0, Copies(engine));
            Assert.Equal(new[] { "cycle=0" }, destroyed);
            Assert.Empty(events);
        }

        [Theory]
        [InlineData(0L, -1, false)]           // an older launch's fall goes
        [InlineData(1L, -1, true)]            // this launch's stays (its caller hides it)
        [InlineData(0L, CoreIndex, true)]     // the watched one stays
        public void NonUniform_HighWarp_DropsAnOlderLaunchsPrimary(long cycle, int watched, bool kept)
        {
            GhostPlaybackLogic.LoopUnit unit = CutUnit(150);
            GhostPlaybackEngine engine = Engine(unit, new ZoneProbe());
            GhostPlaybackState state = Seed(engine, cycle);
            bool active = true;
            var decision = GhostPlaybackLogic.UnitMemberRenderDecision.HiddenOutsideWindow;
            double ut = SpanStart + 10;
            Assert.False(engine.UpdateNonUniformFall(
                CoreIndex, Traj(CoreStart, CoreEnd, true), default(TrajectoryPlaybackFlags),
                Ctx(360, 1000f, watched), unit, 1, CoreStart, CoreEnd, true, true,
                ref decision, ref ut, ref state, ref active));
            Assert.Equal(kept, engine.ghostStates.ContainsKey(CoreIndex));
            Assert.Equal(kept, state != null);
            Assert.Equal(kept, active);
        }

        // ---- debris ----

        [Fact]
        public void Debris_PeriodLongerThanItsFall_EndsAtItsImpact()
        {
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(Unit(900), probe);
            MockTrajectory debris = Traj(90, 250, predictedEnd: true, name: "Booster");
            // launches at 259 + n * 900; impact 160 s later, at 419
            Seed(engine, 0, DebrisIndex);
            Assert.Equal(249, DebrisFrame(engine, probe, debris, Ctx(418)), 9);
            Assert.True(double.IsNaN(DebrisFrame(engine, probe, debris, Ctx(420))));
            Assert.Equal(Cycle0Ending, events);
            foreach (double now in new[] { 500.0, 1158.0 })
            {
                Assert.True(double.IsNaN(DebrisFrame(engine, probe, debris, Ctx(now))));
                Assert.False(engine.ghostStates.ContainsKey(DebrisIndex));
                Assert.Equal(0, Copies(engine, DebrisIndex));
            }
            Assert.Equal(2, events.Count);
            DebrisFrame(engine, probe, debris, Ctx(1160));
            Assert.Contains(logLines, l => l.Contains("Overlap: SpawnGhost failed for #2 cycle=1"));
        }

        [Fact]
        public void Debris_OrdinaryGhostFoundInTheGap_Goes_WithNoWatchEvent()
        {
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(Unit(900), probe);
            MockTrajectory debris = Traj(90, 250, predictedEnd: true, name: "Booster");
            Seed(engine, -1, DebrisIndex);
            Assert.True(double.IsNaN(DebrisFrame(engine, probe, debris, Ctx(700))));
            Assert.False(engine.ghostStates.ContainsKey(DebrisIndex));
            Assert.Equal(0, Copies(engine, DebrisIndex));
            Assert.Empty(events);
        }

        [Fact]
        public void Debris_HighWarp_DropsACopyThatEndsUnderIt()
        {
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(Unit(900), probe);
            MockTrajectory debris = Traj(90, 250, predictedEnd: true, name: "Booster");
            Seed(engine, 0, DebrisIndex);
            DebrisFrame(engine, probe, debris, Ctx(400, 1000f), suppress: true);
            Assert.True(engine.ghostStates.ContainsKey(DebrisIndex));
            DebrisFrame(engine, probe, debris, Ctx(600, 1000f), suppress: true);
            Assert.False(engine.ghostStates.ContainsKey(DebrisIndex));
            Assert.Empty(events);
        }

        // Debris whose whole flight lies INSIDE the span, in a unit that overlaps itself on a cadence
        // longer than the debris: nothing replaces its newest copy until the next launch.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SelfOverlappingDebris_EndsAtItsEnd_PredictedOrRecorded(bool predictedEnd)
        {
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(Unit(100), probe);
            MockTrajectory debris = Traj(60, 117, predictedEnd, name: "Booster");
            // launches at 229 + n * 100; ends 57 s later
            Seed(engine, 0, DebrisIndex);
            Assert.Equal(116, DebrisFrame(engine, probe, debris, Ctx(285)), 9);
            double ut = DebrisFrame(engine, probe, debris, Ctx(300));
            Assert.True(double.IsNaN(ut));
            Assert.False(engine.ghostStates.ContainsKey(DebrisIndex));
            Assert.Equal(Cycle0Ending, events);
            Assert.Equal(predictedEnd, logLines.Any(l => l.Contains("Fall copy reached its end")));
            Assert.Equal(!predictedEnd, EndLines().Any());
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SelfOverlappingMember_EndsAtItsEnd_PredictedOrRecorded(bool predictedEnd)
        {
            var unit = new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0, CoreIndex }, SpanStart, SpanEnd, SpanEnd - SpanStart, Anchor, 100,
                new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>
                {
                    [0] = new GhostPlaybackLogic.LoopUnit.MemberWindow(SpanStart, SpanEnd),
                    [CoreIndex] = new GhostPlaybackLogic.LoopUnit.MemberWindow(60, 117)
                });
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(unit, probe);
            MockTrajectory traj = Traj(60, 117, predictedEnd);
            var all = new List<IPlaybackTrajectory> { traj, traj };
            GhostPlaybackState state = Seed(engine, 0);
            bool active = false;
            double At(double now) => Frame(probe, () => engine.UpdateUnitMemberSelfOverlap(
                CoreIndex, traj, default(TrajectoryPlaybackFlags), Ctx(now), all, unit, "unit-0",
                SpanEnd - SpanStart, 60, 117, false, false, ref state, ref active));
            Assert.Equal(116, At(285), 9);
            double ut = At(300);
            Assert.True(double.IsNaN(ut));
            Assert.False(engine.ghostStates.ContainsKey(CoreIndex));
            Assert.Equal(Cycle0Ending, events);
            Assert.Equal(predictedEnd, logLines.Any(l => l.Contains("Fall copy reached its end")));
            Assert.Equal(!predictedEnd, EndLines().Any());
        }

        [Fact]
        public void NonUniformDebris_EndsWithItsEnding_InTheWait()
        {
            var probe = new ZoneProbe();
            GhostPlaybackEngine engine = Engine(CutUnit(900), probe);
            MockTrajectory debris = Traj(90, 250, predictedEnd: true, name: "Booster");
            // The cut puts cycle 0's span end at 289.5; the fall ends 109.5 s later, at 399.
            Seed(engine, 0, DebrisIndex).fallClockOffsetUT = 289.5 - SpanEnd;
            DebrisFrame(engine, probe, debris, Ctx(420));
            Assert.False(engine.ghostStates.ContainsKey(DebrisIndex));
            Assert.Equal(0, Copies(engine, DebrisIndex));
            Assert.Equal(Cycle0Ending, events);
            DebrisFrame(engine, probe, debris, Ctx(430));
            Assert.Equal(2, events.Count);
        }

        // ---- watch ----

        [Fact]
        public void WatchClock_StopsFollowingAnEndedFall_UntilTheNextLaunch()
        {
            GhostPlaybackEngine engine = Engine(Unit(900), new ZoneProbe());
            Assert.True(engine.TryResolveUnitMemberPlaybackUT(CoreIndex, 495, CoreStart, CoreEnd, out double ut));
            Assert.Equal(CoreStart + 207, ut, 9);
            foreach (double now in new[] { 502.0, 1187.0 })
                Assert.False(engine.TryResolveUnitMemberPlaybackUT(CoreIndex, now, CoreStart, CoreEnd, out ut));
            Assert.True(engine.TryResolveUnitMemberPlaybackUT(CoreIndex, 1198, CoreStart, CoreEnd, out ut));
            Assert.Equal(CoreStart + 10, ut, 9);
        }
    }
}
