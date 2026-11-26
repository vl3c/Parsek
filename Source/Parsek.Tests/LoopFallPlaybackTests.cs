using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling 2026-11-24, the RENDERING half: a member or debris whose predicted tail outlasts the
    /// unit's recorded span is drawn past it (per launch in flight, one copy cut at the next relaunch at
    /// the Space Center and in the Tracking Station), with the cadence untouched and the copies past
    /// the cap ended quietly. Units with no such member keep their old handling.
    /// </summary>
    [Collection("Sequential")]
    public class LoopFallPlaybackTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LoopFallPlaybackTests()
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

        // GS-12 stage 1: recorded span [31, 140.5], core [119, 332.4], one launch every 150 s from 200.
        private const double Anchor = 200, SpanStart = 31, SpanEnd = 140.5, CoreStart = 119, CoreEnd = 332.4;
        private const double SpanLen = SpanEnd - SpanStart;

        private static GhostPlaybackLogic.LoopUnit Unit(
            double cadence = 150, double overlapCadence = 150, double renderEnd = CoreEnd,
            MissionRelaunchSchedule schedule = null,
            IReadOnlyList<GhostPlaybackLogic.LoopCut> cuts = null)
        {
            var windows = new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>
            {
                [0] = new GhostPlaybackLogic.LoopUnit.MemberWindow(SpanStart, SpanEnd),
                [1] = new GhostPlaybackLogic.LoopUnit.MemberWindow(CoreStart, renderEnd)
            };
            return new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0, 1 }, SpanStart, SpanEnd, cadence, Anchor, overlapCadence, windows,
                schedule, null, null, cuts, renderSpanEndUT: renderEnd);
        }

        private static GhostPlaybackLogic.LoopUnitSet SetOf(GhostPlaybackLogic.LoopUnit unit) =>
            new GhostPlaybackLogic.LoopUnitSet(
                new Dictionary<int, GhostPlaybackLogic.LoopUnit> { [unit.OwnerIndex] = unit },
                unit.MemberIndices.ToDictionary(i => i, i => unit.OwnerIndex));

        // ---- who falls ----

        [Fact]
        public void MemberHasLoopFall_OnlyPastTheTimingSpan_OfAUnitThatHasFalls()
        {
            Assert.True(GhostPlaybackLogic.MemberHasLoopFall(Unit(), CoreEnd));
            Assert.False(GhostPlaybackLogic.MemberHasLoopFall(Unit(), SpanEnd));
            // A unit built with no drawn end past its span (every hand-made and no-tail unit).
            var plain = new GhostPlaybackLogic.LoopUnit(0, new[] { 0 }, SpanStart, SpanEnd, 150, Anchor);
            Assert.Equal(SpanEnd, plain.RenderSpanEndUT);
            Assert.False(GhostPlaybackLogic.MemberHasLoopFall(plain, CoreEnd));
            Assert.False(GhostPlaybackLogic.MemberHasLoopFall(default(GhostPlaybackLogic.LoopUnit), CoreEnd));
        }

        [Fact]
        public void DebrisHasLoopFall_ReadsPredictedDataOnly()
        {
            var unit = Unit(renderEnd: SpanEnd);
            var recorded = new MockTrajectory { VesselName = "D" }.WithTimeRange(100, 300);
            Assert.False(GhostPlaybackLogic.DebrisHasLoopFall(unit, recorded));
            recorded.OrbitSegments.Add(new OrbitSegment { startUT = 120, endUT = 300, isPredicted = false });
            Assert.False(GhostPlaybackLogic.DebrisHasLoopFall(unit, recorded));
            // a tail that ends inside the span is drawn by the span clock as before
            recorded.OrbitSegments.Add(new OrbitSegment { startUT = 110, endUT = 130, isPredicted = true });
            Assert.False(GhostPlaybackLogic.DebrisHasLoopFall(unit, recorded));
            recorded.OrbitSegments.Add(new OrbitSegment { startUT = 300, endUT = 301, isPredicted = true });
            Assert.True(GhostPlaybackLogic.DebrisHasLoopFall(unit, recorded));
            var chute = new MockTrajectory { VesselName = "C" };
            chute.TrackSections.Add(new TrackSection
                { startUT = 130, endUT = 220, source = TrackSectionSource.Predicted });
            Assert.True(GhostPlaybackLogic.DebrisHasLoopFall(unit, chute));
            Assert.False(GhostPlaybackLogic.DebrisHasLoopFall(unit, null));
        }

        // ---- cadence and the copy cap ----

        [Theory]
        [InlineData(150, 213.4)]
        [InlineData(10, 213.4)]       // 22 copies at the launches' 10 s: over the cap
        [InlineData(5, 1e9)]          // the horizon
        public void FallCadence_IsNeverRaised(double cadence, double duration)
        {
            Assert.Equal(cadence, GhostPlaybackLogic.ResolveOverlapCadence(cadence, duration, keepCadence: true));
            // the standalone / no-fall rule is untouched
            Assert.Equal(
                GhostPlaybackLogic.ComputeEffectiveLaunchCadence(
                    cadence, duration, GhostPlayback.MaxOverlapGhostsPerRecording),
                GhostPlaybackLogic.ResolveOverlapCadence(cadence, duration, keepCadence: false));
        }

        [Fact]
        public void FallCadence_StillFloorsAtTheMinimumCycle()
        {
            Assert.Equal(LoopTiming.MinCycleDuration,
                GhostPlaybackLogic.ResolveOverlapCadence(0.1, 100, keepCadence: true));
        }

        [Fact]
        public void NewestCycle_OfACappedFall_FollowsTheLaunches()
        {
            // 10 s launches, 213.4 s fall: at 250 s in, the newest copy is cycle 25 with the launches'
            // own phase; the raised cadence (10.67 s) would read cycle 23.
            Assert.True(GhostPlaybackLogic.TryComputeNewestOverlapPlaybackUT(
                1251, 10, 213.4, CoreStart, 1000, out double ut, out long cycle, keepCadence: true));
            Assert.Equal(25, cycle);
            Assert.Equal(CoreStart + 1, ut, 9);
            Assert.True(GhostPlaybackLogic.TryComputeNewestOverlapPlaybackUT(
                1251, 10, 213.4, CoreStart, 1000, out _, out long raised));
            Assert.Equal(23, raised);
        }

        [Fact]
        public void CopyCap_KeepsTwentyCopies_AndCountsTheFirstRunAsTheOldest()
        {
            Assert.Equal(-19, GhostPlaybackLogic.OldestDrawnFallCycle(0));
            Assert.Equal(6, GhostPlaybackLogic.OldestDrawnFallCycle(25));
            long firstRun = GhostPlaybackLogic.FirstRunFallCycleIndex;
            long capped = GhostPlaybackLogic.OldestDrawnFallCycle(25);
            Assert.True(GhostPlaybackLogic.IsFallCopyPastCap(5, capped));
            Assert.False(GhostPlaybackLogic.IsFallCopyPastCap(6, capped));
            Assert.True(GhostPlaybackLogic.IsFallCopyPastCap(firstRun, capped));
            Assert.True(GhostPlaybackLogic.IsFallCopyPastCap(
                firstRun, GhostPlaybackLogic.OldestDrawnFallCycle(19)));
            long young = GhostPlaybackLogic.OldestDrawnFallCycle(18);
            Assert.False(GhostPlaybackLogic.IsFallCopyPastCap(firstRun, young));
            Assert.False(GhostPlaybackLogic.IsFallCopyPastCap(0, young));
            // every other overlap loop: no cap here
            Assert.False(GhostPlaybackLogic.IsFallCopyPastCap(0, long.MinValue));
            Assert.False(GhostPlaybackLogic.IsFallCopyPastCap(-1, long.MinValue));
        }

        [Fact]
        public void OverlapClock_PlainPhase_IsTheOldFormula()
        {
            foreach (long cycle in new long[] { -1, 0, 1, 7 })
                Assert.Equal(1234.5 - (1000.25 + cycle * 30.0),
                    GhostPlaybackLogic.OverlapCopyPhaseSeconds(1234.5, cycle, 1000.25, 30, 50));
            // the first run's kept fall replays on the real clock
            Assert.Equal(1234.5 - 50, GhostPlaybackLogic.OverlapCopyPhaseSeconds(
                1234.5, GhostPlaybackLogic.FirstRunFallCycleIndex, 1000.25, 30, 50));
        }

        [Fact]
        public void UniformFall_IsDrawnPerLaunch_OnTheUnitsOwnCadence()
        {
            var unit = Unit();
            Assert.True(GhostPlaybackLogic.MemberFallUsesOverlapSchedule(unit, CoreEnd));
            Assert.False(GhostPlaybackLogic.MemberFallUsesOverlapSchedule(unit, SpanEnd));
            Assert.Equal(150, GhostPlaybackLogic.UnitLaunchCadenceSeconds(unit));
            // Auto with a global interval past the span: the launches run on the span-clock cadence.
            Assert.Equal(SpanLen, GhostPlaybackLogic.UnitLaunchCadenceSeconds(Unit(SpanLen, 300)));
            // Self-overlap: the overlap cadence, and the member is already on the overlap path.
            Assert.Equal(30, GhostPlaybackLogic.UnitLaunchCadenceSeconds(Unit(SpanLen, 30)));
            Assert.False(GhostPlaybackLogic.MemberFallUsesOverlapSchedule(Unit(SpanLen, 30), CoreEnd));
            var cuts = new[] { new GhostPlaybackLogic.LoopCut { StartUT = 60, LengthSeconds = 20 } };
            Assert.False(GhostPlaybackLogic.MemberFallUsesOverlapSchedule(Unit(cuts: cuts), CoreEnd));
        }

        // ---- the fall clock ----

        [Fact]
        public void FallClock_RunsOneToOneFromTheSpanEnd_PerCycle()
        {
            var unit = Unit();
            Assert.True(GhostPlaybackLogic.TryComputeCycleSpanEndLiveUT(unit, 2, out double liveUT));
            Assert.Equal(Anchor + 300 + SpanLen, liveUT, 9);
            Assert.False(GhostPlaybackLogic.TryComputeFallLoopUT(unit, 2, liveUT - 0.1, out _));
            Assert.True(GhostPlaybackLogic.TryComputeFallLoopUT(unit, 2, liveUT + 42, out double fallUT));
            Assert.Equal(SpanEnd + 42, fallUT, 9);
            Assert.False(GhostPlaybackLogic.TryComputeCycleSpanEndLiveUT(unit, -1, out _));
            // loiter cuts shorten the live span the fall starts after
            var cuts = new[] { new GhostPlaybackLogic.LoopCut { StartUT = 60, LengthSeconds = 20 } };
            Assert.True(GhostPlaybackLogic.TryComputeCycleSpanEndLiveUT(Unit(cuts: cuts), 2, out double cutUT));
            Assert.Equal(liveUT - 20, cutUT, 9);
        }

        [Fact]
        public void SingleCopy_FallsThroughTheWait_AndIsCutAtTheNextRelaunch()
        {
            var unit = Unit();
            var set = SetOf(unit);
            // in the span: the span clock, unchanged
            double at = Anchor + (CoreStart - SpanStart) + 5;
            Assert.Equal(CoreStart + 5,
                GhostPlaybackLogic.ResolveTrackingStationSampleUT(1, 0, 0, at, set, out bool hidden), 9);
            Assert.False(hidden);
            // in the wait: the core plays its fall, the stack is hidden as before
            double wait = Anchor + SpanLen + 20;
            Assert.Equal(SpanEnd + 20,
                GhostPlaybackLogic.ResolveTrackingStationSampleUT(1, 0, 0, wait, set, out hidden), 9);
            Assert.False(hidden);
            GhostPlaybackLogic.ResolveTrackingStationSampleUT(0, 0, 0, wait, set, out hidden);
            Assert.True(hidden);
            // the next relaunch takes the clock: the fall is cut
            GhostPlaybackLogic.ResolveTrackingStationSampleUT(1, 0, 0, Anchor + 150 + 1, set, out hidden);
            Assert.True(hidden);
        }

        [Fact]
        public void SingleCopy_EndsAtItsOwnEnd_WhenTheWaitOutlastsTheFall()
        {
            var set = SetOf(Unit(cadence: 900, overlapCadence: 900));
            double impact = Anchor + SpanLen + (CoreEnd - SpanEnd);
            GhostPlaybackLogic.ResolveTrackingStationSampleUT(1, 0, 0, impact - 1, set, out bool hidden);
            Assert.False(hidden);
            GhostPlaybackLogic.ResolveTrackingStationSampleUT(1, 0, 0, impact + 1, set, out hidden);
            Assert.True(hidden);
        }

        [Fact]
        public void NoFall_TheWaitHidesEveryMember_AsBefore()
        {
            var set = SetOf(Unit(renderEnd: SpanEnd));
            for (double t = Anchor; t < Anchor + 400; t += 3.7)
            {
                var plain = new GhostPlaybackLogic.LoopUnit(
                    0, new[] { 0, 1 }, SpanStart, SpanEnd, 150, Anchor, 150,
                    new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>
                    {
                        [0] = new GhostPlaybackLogic.LoopUnit.MemberWindow(SpanStart, SpanEnd),
                        [1] = new GhostPlaybackLogic.LoopUnit.MemberWindow(CoreStart, SpanEnd)
                    });
                double a = GhostPlaybackLogic.ResolveTrackingStationSampleUT(1, 0, 0, t, set, out bool ha);
                double b = GhostPlaybackLogic.ResolveTrackingStationSampleUT(1, 0, 0, t, SetOf(plain), out bool hb);
                Assert.Equal(b, a);
                Assert.Equal(hb, ha);
            }
        }

        [Fact]
        public void ExtendIntoFall_TouchesOnlyTheWait()
        {
            var unit = Unit();
            foreach (GhostPlaybackLogic.UnitMemberRenderDecision d in
                     Enum.GetValues(typeof(GhostPlaybackLogic.UnitMemberRenderDecision)))
            {
                var decision = d;
                double ut = 7;
                bool extended = GhostPlaybackLogic.TryExtendRenderIntoFall(
                    unit, 1, Anchor + SpanLen + 20, CoreStart, CoreEnd, 0, ref decision, ref ut);
                if (d == GhostPlaybackLogic.UnitMemberRenderDecision.HiddenInterCycleTail)
                {
                    Assert.True(extended);
                    Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.Render, decision);
                    Assert.Equal(SpanEnd + 20, ut, 9);
                }
                else
                {
                    Assert.False(extended);
                    Assert.Equal(d, decision);
                    Assert.Equal(7, ut);
                }
            }
        }

        [Fact]
        public void ScheduledUnit_FallsFromEachLaunchsOwnSpanEnd()
        {
            // Two incommensurate periods give a non-uniform schedule.
            const double span = 400;
            var schedule = new MissionRelaunchSchedule(
                0, 1000, new[] { 701.0 }, new[] { 25.0 }, 5000, 200);
            var unit = new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0 }, 0, span, span, schedule.FirstLaunchUT, span,
                new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>
                    { [0] = new GhostPlaybackLogic.LoopUnit.MemberWindow(0, span + 1e7) },
                schedule, null, null, renderSpanEndUT: span + 1e7);
            Assert.False(GhostPlaybackLogic.UnitHasUniformCycles(unit));
            Assert.True(schedule.TryResolveActiveLaunch(
                schedule.FirstLaunchUT + 40 * span, out double launch, out long cycle));
            Assert.True(GhostPlaybackLogic.TryComputeCycleSpanEndLiveUT(unit, cycle, out double liveUT));
            Assert.Equal(launch + span, liveUT, 6);
            Assert.True(GhostPlaybackLogic.TryComputeCycleSpanEndLiveUT(unit, 0, out liveUT));
            Assert.True(cycle > 0);
            Assert.Equal(schedule.FirstLaunchUT + span, liveUT, 6);
            Assert.False(GhostPlaybackLogic.TryComputeCycleSpanEndLiveUT(unit, 1000000, out _));
            Assert.Equal(span + 33, GhostPlaybackLogic.OverlapCopyPhaseSeconds(
                schedule.FirstLaunchUT + span + 33, 0, double.NaN, 0, 0, unit), 6);
            Assert.True(double.IsNegativeInfinity(GhostPlaybackLogic.OverlapCopyPhaseSeconds(
                schedule.FirstLaunchUT + span - 1, 0, double.NaN, 0, 0, unit)));
        }

        // ---- the map: per launch in flight, one copy in the Tracking Station ----

        [Fact]
        public void Map_DrawsAFallPerLaunch_InFlightOnly()
        {
            var committed = new List<Recording> { MapRec("a", SpanStart, SpanEnd), MapRec("b", CoreStart, CoreEnd) };
            var ts = SetOf(Unit());
            var flight = ts.WithFallCopies();
            Assert.False(GhostMapPresence.IsOverlapRecording(committed[1], 1, committed, ts));
            Assert.True(GhostMapPresence.IsOverlapRecording(committed[1], 1, committed, flight));
            Assert.False(GhostMapPresence.IsOverlapRecording(committed[0], 0, committed, flight));
            Assert.True(GhostMapPresence.ResolveOverlapSchedule(
                committed[1], 1, committed, flight, out double playbackStart, out double scheduleStart,
                out double duration, out double cadence, out _));
            Assert.Equal(CoreStart, playbackStart);
            Assert.Equal(Anchor + (CoreStart - SpanStart), scheduleStart);
            Assert.Equal(CoreEnd - CoreStart, duration, 9);
            Assert.Equal(150, cadence);
        }

        [Fact]
        public void Map_SelfOverlappingUnit_KeepsTheLaunchCadenceForAFall_AndItsOldOneWithout()
        {
            var committed = new List<Recording> { MapRec("a", SpanStart, SpanEnd), MapRec("b", CoreStart, CoreEnd) };
            var set = SetOf(Unit(SpanLen, 10));
            Assert.True(GhostMapPresence.ResolveOverlapSchedule(
                committed[1], 1, committed, set, out _, out _, out _, out double fallCadence, out _));
            Assert.Equal(10, fallCadence);
            Assert.True(GhostMapPresence.ResolveOverlapSchedule(
                committed[0], 0, committed, set, out _, out _, out _, out double stackCadence, out _));
            Assert.Equal(10, stackCadence);
            // at most 20 copies, the newest ones
            GhostMapPresence.OverlapCyclesForTesting(
                committed[1], 1, committed, set, Anchor + (CoreStart - SpanStart) + 251,
                out long first, out long last);
            Assert.Equal(25, last);
            Assert.Equal(6, first);
        }

        private static Recording MapRec(string id, double start, double end) => new Recording
        {
            RecordingId = id, VesselName = id, ExplicitStartUT = start, ExplicitEndUT = end
        };

        // ---- the engine ----

        private static FrameContext Ctx(double ut) => new FrameContext
        {
            currentUT = ut, warpRate = 1f, activeVesselPos = new Vector3d(0, 0, 0),
            protectedIndex = -1, protectedLoopCycleIndex = -1, autoLoopIntervalSeconds = 30
        };

        // IsDebris keeps the replacement primary on the early-fail path, so the call stays managed.
        private static MockTrajectory Core(double end = CoreEnd) =>
            new MockTrajectory { IsDebris = true, VesselName = "Core", RecordingId = "core" }
                .WithTimeRange(CoreStart, end);

        private GhostPlaybackEngine Engine(GhostPlaybackLogic.LoopUnitSet set)
        {
            var engine = new GhostPlaybackEngine(positioner: null);
            engine.DestroyGhostResourcesOverrideForTesting = _ => { };
            engine.SetLoopUnits(set);
            return engine;
        }

        [Fact]
        public void Engine_FallingMember_DemotesOnTheLaunchCadence_WithNoAdjustment()
        {
            var unit = Unit();
            var engine = Engine(SetOf(unit));
            GhostPlaybackState state = new GhostPlaybackState { vesselName = "Core", loopCycleIndex = 0 };
            engine.ghostStates[1] = state;
            bool active = false;
            GhostPlaybackState cycle0 = state;
            // Cycle 1's core launches at 438; cycle 0's has 63 s of its 213 s left.
            engine.UpdateUnitMemberFallSchedule(
                1, Core(), default(TrajectoryPlaybackFlags), Ctx(440), unit, "unit-0", CoreStart, CoreEnd,
                suppressGhosts: false, suppressVisualFx: false, ref state, ref active);

            Assert.Contains(logLines, l => l.Contains(
                "Loop cadence #1 \"Core\": requested=150.00s duration=213.40s cap=20 effective=150.00s (cycles=2) no adjustment"));
            Assert.Contains(logLines, l => l.Contains("Mission fall unit owner=0 member #1 launchCadence=150.00"));
            Assert.True(engine.TryGetOverlapGhosts(1, out var overlaps));
            Assert.Same(cycle0, overlaps.Single());
            Assert.Equal(0, cycle0.loopCycleIndex);
        }

        [Fact]
        public void Engine_FallOverTheCap_IsNotRaised()
        {
            var unit = Unit(cadence: 110, overlapCadence: 110, renderEnd: 9000);
            var engine = Engine(SetOf(unit));
            GhostPlaybackState state = new GhostPlaybackState { vesselName = "Core", loopCycleIndex = 0 };
            engine.ghostStates[1] = state;
            bool active = false;
            engine.UpdateUnitMemberFallSchedule(
                1, Core(9000), default(TrajectoryPlaybackFlags), Ctx(Anchor + 88 + 111), unit, "unit-0",
                CoreStart, 9000, suppressGhosts: false, suppressVisualFx: false, ref state, ref active);
            Assert.Contains(logLines, l => l.Contains("requested=110.00s") && l.Contains("effective=110.00s (cycles=81) no adjustment"));
            Assert.DoesNotContain(logLines, l => l.Contains("auto-adjusted"));
        }

        [Fact]
        public void Engine_FirstRunStillFalling_IsKeptBesideTheCopies()
        {
            var unit = Unit();
            var engine = Engine(SetOf(unit));
            GhostPlaybackState ordinary = new GhostPlaybackState
            {
                vesselName = "Core", loopCycleIndex = -1,
                pendingSpawnLifecycle = PendingSpawnLifecycle.StandardEnter
            };
            engine.ghostStates[1] = ordinary;
            GhostPlaybackState state = ordinary;
            bool active = true;
            Assert.True(engine.TryKeepFirstRunFall(1, Core(), Ctx(210), Anchor, ref state, ref active));
            Assert.Null(state);
            Assert.False(active);
            Assert.False(engine.ghostStates.ContainsKey(1));
            Assert.True(engine.TryGetOverlapGhosts(1, out var overlaps));
            Assert.Same(ordinary, overlaps.Single());
            Assert.Equal(GhostPlaybackLogic.FirstRunFallCycleIndex, ordinary.loopCycleIndex);
            Assert.Equal(PendingSpawnLifecycle.None, ordinary.pendingSpawnLifecycle);
            Assert.Contains(logLines, l => l.Contains(
                "Loop first-run handover: #1 \"Core\" UT=210.00 anchor=200.00 endUT=332.40 - the first run's fall keeps playing"));
        }

        [Theory]
        [InlineData(0, 210.0)]     // a loop copy
        [InlineData(-1, 340.0)]    // an ordinary ghost past its own end
        public void Engine_NothingToKeep_LeavesTheSlotAlone(long cycle, double now)
        {
            var engine = Engine(SetOf(Unit()));
            GhostPlaybackState state = new GhostPlaybackState { loopCycleIndex = cycle };
            engine.ghostStates[1] = state;
            bool active = true;
            Assert.False(engine.TryKeepFirstRunFall(1, Core(), Ctx(now), Anchor, ref state, ref active));
            Assert.NotNull(state);
            Assert.True(active);
            Assert.True(engine.ghostStates.ContainsKey(1));
            GhostPlaybackState none = null;
            Assert.False(engine.TryKeepFirstRunFall(1, Core(), Ctx(now), Anchor, ref none, ref active));
        }

        [Fact]
        public void Engine_NonUniformUnit_PlaysTheFallThroughTheWait()
        {
            var cuts = new[] { new GhostPlaybackLogic.LoopCut { StartUT = 60, LengthSeconds = 20 } };
            var unit = Unit(cuts: cuts);
            var engine = Engine(SetOf(unit));
            GhostPlaybackState state = new GhostPlaybackState { loopCycleIndex = 0 };
            bool active = false;
            var decision = GhostPlaybackLogic.UnitMemberRenderDecision.HiddenInterCycleTail;
            double ut = SpanEnd;
            Assert.False(engine.UpdateNonUniformFall(
                1, Core(), default(TrajectoryPlaybackFlags), Ctx(Anchor + SpanLen - 20 + 9), unit, 0,
                CoreStart, CoreEnd, suppressGhosts: false, suppressVisualFx: false,
                ref decision, ref ut, ref state, ref active));
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.Render, decision);
            Assert.Equal(SpanEnd + 9, ut, 9);
            Assert.NotNull(state);
        }

        [Fact]
        public void WatchClock_OfAFallingMember_IsItsNewestCopy()
        {
            var engine = Engine(SetOf(Unit()));
            // 60 s after cycle 1's core launched (438)
            Assert.True(engine.TryResolveUnitMemberPlaybackUT(1, 498, CoreStart, CoreEnd, out double ut));
            Assert.Equal(CoreStart + 60, ut, 9);
            // cycle 2 has relaunched (500) but its core has not (588): cycle 1's copy is the newest
            Assert.True(engine.TryResolveUnitMemberPlaybackUT(1, 520, CoreStart, CoreEnd, out ut));
            Assert.Equal(CoreStart + 82, ut, 9);
            // the stack stays on the span clock
            Assert.True(engine.TryResolveUnitMemberPlaybackUT(0, Anchor + 10, SpanStart, SpanEnd, out ut));
            Assert.Equal(SpanStart + 10, ut, 9);
        }
    }
}
