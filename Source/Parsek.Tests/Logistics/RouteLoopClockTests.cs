using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins <see cref="RouteLoopClock"/> (plan Phase 4 task 1). The clock wraps
    /// the LOCKED <c>GhostPlaybackLogic.TryComputeSpanLoopUT</c> seam (consumed
    /// read-only) and the crossing-detection predicate the orchestrator's
    /// loop-route path uses. All tests build a <see cref="GhostPlaybackLogic.LoopUnit"/>
    /// directly (internal ctor, visible via InternalsVisibleTo) so the math is
    /// exercised without Planetarium / RecordingStore.
    /// </summary>
    /// <remarks>
    /// Phase 4 tests set <c>DispatchInterval &gt;= span</c> (so cadence ==
    /// interval and one crossing == one cycle); the &gt;=-clamp itself is wired in
    /// Phase 5 <c>RouteBuilder</c>. The clock is pure (no shared static state), so
    /// this class does not need <c>[Collection("Sequential")]</c>.
    /// </remarks>
    public class RouteLoopClockTests
    {
        // span [1000, 1300] (300s). cadence == span -> cadenceSeconds = 300.
        private static GhostPlaybackLogic.LoopUnit BuildUnit(
            double spanStartUT = 1000.0,
            double spanEndUT = 1300.0,
            double cadenceSeconds = 300.0,
            double phaseAnchorUT = 1000.0)
        {
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0,
                memberIndices: new[] { 0 },
                spanStartUT: spanStartUT,
                spanEndUT: spanEndUT,
                cadenceSeconds: cadenceSeconds,
                phaseAnchorUT: phaseAnchorUT);
        }

        // span [0,50] driven by a non-uniform zero-drift schedule (launches at
        // ~900, 1300, 1800...). The synthetic 100/31 schedule is the same one the
        // Missions zero-drift tests use; the clock parks between launches because
        // the ~400-500s interval exceeds the 50s span.
        private static GhostPlaybackLogic.LoopUnit BuildScheduledUnit(out MissionRelaunchSchedule schedule)
        {
            schedule = new MissionRelaunchSchedule(
                0.0, 100.0, new double[] { 31.0 }, new double[] { 2.0 }, floorUT: 0.0,
                lookaheadMultiples: 100);
            double span = 50.0;
            double cad = System.Math.Max(span, schedule.MinIntervalSeconds);
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 0.0, spanEndUT: span, cadenceSeconds: cad,
                phaseAnchorUT: schedule.FirstLaunchUT,
                overlapCadenceSeconds: cad, memberWindows: null, relaunchSchedule: schedule);
        }

        // ==================================================================
        // TryGetRouteLoopState — span-clock pass-through
        // ==================================================================

        // ==================================================================
        // Phase 6: schedule passthrough (the inter-body seam)
        // ==================================================================

        // catches: RouteLoopClock hardcoding schedule:null instead of threading the
        // unit's own RelaunchSchedule. A scheduled (inter-body / synodic) unit must
        // crossing-fire on its NON-UNIFORM scheduled-launch UTs, not on a fixed
        // cadence. v0 same-body routes carry a null schedule so this is a no-op for
        // them, but the seam must consume a non-null schedule when present.
        [Fact]
        public void TryGetRouteLoopState_ConsumesUnitSchedule_FiresOnScheduledLaunches()
        {
            var unit = BuildScheduledUnit(out MissionRelaunchSchedule schedule);

            // Resolve the first three scheduled launch UTs directly from the
            // schedule so the test pins the SAME non-uniform UTs the clock uses.
            Assert.True(schedule.TryResolveActiveLaunch(1_000_000.0, out _, out _));
            double launch0 = schedule.FirstLaunchUT;
            Assert.True(schedule.TryResolveActiveLaunch(launch0 + 1.0, out _, out long idx0));
            Assert.Equal(0, idx0);

            // 25s into the FIRST scheduled launch -> cycle index 0, loopUT inside the
            // span (spanStart + 25), NOT parked in the tail.
            Assert.True(RouteLoopClock.TryGetRouteLoopState(unit, launch0 + 25.0,
                out double loopUT0, out long cyc0, out bool tail0));
            Assert.Equal(0, cyc0);
            Assert.False(tail0);
            Assert.Equal(25.0, loopUT0, 6);

            // Past the 50s span but before the next launch -> parked in the
            // inter-launch tail (schedule interval >> span), still cycle 0.
            Assert.True(RouteLoopClock.TryGetRouteLoopState(unit, launch0 + 70.0,
                out _, out long cycTail, out bool tailParked));
            Assert.Equal(0, cycTail);
            Assert.True(tailParked);

            // Discover the SECOND scheduled launch (index 1) directly from the
            // schedule by scanning forward (the intervals are non-uniform, so the
            // index is not a fixed function of elapsed time). Then assert the clock
            // ticks the cycle index to 1 at that launch — proving it tracks the
            // schedule's launch index, not a fixed cadence.
            double launch1 = double.NaN;
            for (double probe = launch0 + 1.0; probe <= launch0 + 5000.0; probe += 1.0)
            {
                if (schedule.TryResolveActiveLaunch(probe, out double resolvedLaunch, out long resolvedIdx)
                    && resolvedIdx == 1)
                {
                    launch1 = resolvedLaunch;
                    break;
                }
            }
            Assert.False(double.IsNaN(launch1), "schedule must produce a second launch (index 1)");
            Assert.True(launch1 > launch0);

            Assert.True(RouteLoopClock.TryGetRouteLoopState(unit, launch1 + 10.0,
                out double loopUT1, out long cyc1, out bool tail1));
            Assert.Equal(1, cyc1);
            Assert.False(tail1);
            Assert.Equal(10.0, loopUT1, 6);
        }

        // catches: a scheduled unit being treated as "live" before its first
        // scheduled launch. The schedule path returns false (parked) before
        // FirstLaunchUT, so the clock must report not-resolved there.
        [Fact]
        public void TryGetRouteLoopState_ScheduledUnit_BeforeFirstLaunch_ReturnsFalse()
        {
            var unit = BuildScheduledUnit(out MissionRelaunchSchedule schedule);
            double before = schedule.FirstLaunchUT - 100.0;
            Assert.False(RouteLoopClock.TryGetRouteLoopState(unit, before,
                out _, out long idx, out bool tail));
            Assert.Equal(0, idx);
            Assert.False(tail);
        }

        // catches: clock not advancing the cycle index once per period when
        // cadence == interval. Mid-span at anchor+100 is cycle 0; one full
        // period later (anchor+400, past the 300s span) is cycle 1.
        [Fact]
        public void CycleIndex_IncrementsOncePerPeriod_WhenCadenceEqualsSpan()
        {
            var unit = BuildUnit(spanStartUT: 1000.0, spanEndUT: 1300.0, cadenceSeconds: 300.0, phaseAnchorUT: 1000.0);

            // Cycle 0: ut = 1100 (100s into the first cycle).
            Assert.True(RouteLoopClock.TryGetRouteLoopState(unit, 1100.0,
                out double loopUT0, out long idx0, out bool tail0));
            Assert.Equal(0, idx0);
            Assert.Equal(1100.0, loopUT0); // spanStart + phase
            Assert.False(tail0);

            // Cycle 1: ut = 1450 (one full 300s period + 150 into the next).
            Assert.True(RouteLoopClock.TryGetRouteLoopState(unit, 1450.0,
                out double loopUT1, out long idx1, out bool tail1));
            Assert.Equal(1, idx1);
            Assert.Equal(1150.0, loopUT1); // wrapped: spanStart + 150
            Assert.False(tail1);
        }

        // catches: loopUT not wrapping back into the span on the second cycle.
        [Fact]
        public void LoopUT_WrapsBackIntoSpan_EachCycle()
        {
            var unit = BuildUnit(spanStartUT: 1000.0, spanEndUT: 1300.0, cadenceSeconds: 300.0, phaseAnchorUT: 1000.0);

            // Same in-cycle offset (50s) on cycle 0 and cycle 2 -> same loopUT.
            Assert.True(RouteLoopClock.TryGetRouteLoopState(unit, 1050.0, out double a, out long ia, out _));
            Assert.True(RouteLoopClock.TryGetRouteLoopState(unit, 1650.0, out double b, out long ib, out _));
            Assert.Equal(0, ia);
            Assert.Equal(2, ib);
            Assert.Equal(a, b);
            Assert.Equal(1050.0, a);
        }

        // catches: a pre-anchor sample being reported as a live cycle.
        [Fact]
        public void PreAnchor_ReturnsFalse()
        {
            var unit = BuildUnit(phaseAnchorUT: 1000.0);
            Assert.False(RouteLoopClock.TryGetRouteLoopState(unit, 500.0,
                out double loopUT, out long idx, out bool tail));
            Assert.Equal(0, idx);
            Assert.False(tail);
        }

        // catches: a degenerate (zero/negative) span being treated as a cycle.
        [Fact]
        public void DegenerateSpan_ReturnsFalse()
        {
            var unit = BuildUnit(spanStartUT: 1300.0, spanEndUT: 1300.0); // zero span
            Assert.False(RouteLoopClock.TryGetRouteLoopState(unit, 1400.0, out _, out _, out _));
        }

        // ==================================================================
        // isInInterCycleTail threading (cadence > span)
        // ==================================================================

        // catches: the parked tail flag not being surfaced when the dispatch
        // interval EXCEEDS the span (the common v0 case, since the clamp is >=).
        // With cadence 600 > span 300, the clock plays [1000,1300] then parks at
        // 1300 for [1300,1600) before the next cycle.
        [Fact]
        public void IsInInterCycleTail_TrueWhenParked_CadenceExceedsSpan()
        {
            var unit = BuildUnit(spanStartUT: 1000.0, spanEndUT: 1300.0, cadenceSeconds: 600.0, phaseAnchorUT: 1000.0);

            // ut = 1150: actively playing inside the span -> not tail.
            Assert.True(RouteLoopClock.TryGetRouteLoopState(unit, 1150.0, out _, out long idxPlay, out bool tailPlay));
            Assert.Equal(0, idxPlay);
            Assert.False(tailPlay);

            // ut = 1450: phase 450 > span 300 -> parked at spanEnd, tail engaged.
            Assert.True(RouteLoopClock.TryGetRouteLoopState(unit, 1450.0, out double loopUTtail, out long idxTail, out bool tailParked));
            Assert.Equal(0, idxTail);
            Assert.True(tailParked);
            Assert.Equal(1300.0, loopUTtail); // parked at spanEnd
        }

        // ==================================================================
        // IsDockUTInSpan
        // ==================================================================

        [Fact]
        public void IsDockUTInSpan_InsideAndBoundary_True_OutsideFalse()
        {
            var unit = BuildUnit(spanStartUT: 1000.0, spanEndUT: 1300.0);
            Assert.True(RouteLoopClock.IsDockUTInSpan(unit, 1150.0)); // inside
            Assert.True(RouteLoopClock.IsDockUTInSpan(unit, 1000.0)); // start boundary
            Assert.True(RouteLoopClock.IsDockUTInSpan(unit, 1300.0)); // end boundary
            Assert.False(RouteLoopClock.IsDockUTInSpan(unit, 999.0));  // before
            Assert.False(RouteLoopClock.IsDockUTInSpan(unit, 1301.0)); // after
        }

        // ==================================================================
        // ComputeDockCycleIndex (DEL-2 dock-phase index)
        // ==================================================================

        // catches: the dock-phase index not advancing to the current cycle once
        // loopUT reaches/passes the recorded dock UT (and staying on the PRIOR
        // cycle while the ghost is still pre-dock).
        [Fact]
        public void ComputeDockCycleIndex_BeforeAndAfterDockPhase()
        {
            // Pre-dock (loopUT < dock): the most recently DOCKED cycle is the prior one.
            Assert.Equal(-1, RouteLoopClock.ComputeDockCycleIndex(loopUT: 1100.0, cycleIndex: 0, recordedDockUT: 1150.0));
            Assert.Equal(2, RouteLoopClock.ComputeDockCycleIndex(loopUT: 1100.0, cycleIndex: 3, recordedDockUT: 1150.0));
            // At/after the dock phase (loopUT >= dock): this cycle's dock has passed.
            Assert.Equal(0, RouteLoopClock.ComputeDockCycleIndex(loopUT: 1150.0, cycleIndex: 0, recordedDockUT: 1150.0));
            Assert.Equal(3, RouteLoopClock.ComputeDockCycleIndex(loopUT: 1300.0, cycleIndex: 3, recordedDockUT: 1150.0));
        }

        // ==================================================================
        // IsDockCrossing predicate (DEL-2 dock-phase gate)
        // ==================================================================

        // catches DEL-2: a fresh cycle firing at SPAN START (loopUT == spanStart,
        // ghost just launching) before the dock phase. The dock-phase gate must
        // NOT fire here even though the cycle index is new.
        [Fact]
        public void IsDockCrossing_AtSpanStart_BeforeDockPhase_False()
        {
            var unit = BuildUnit(spanStartUT: 1000.0, spanEndUT: 1300.0);
            // loopUT 1000 (spanStart) < dock 1150 -> not yet at the dock phase.
            Assert.False(RouteLoopClock.IsDockCrossing(
                unit, loopUT: 1000.0, cycleIndex: 0, recordedDockUT: 1150.0,
                lastObservedLoopCycleIndex: -1, out long dockIdx));
            Assert.Equal(-1, dockIdx); // prior cycle; no new dock crossed
        }

        // catches DEL-2: the crossing firing once loopUT reaches/passes the dock.
        [Fact]
        public void IsDockCrossing_AtDockPhase_NewCycle_True()
        {
            var unit = BuildUnit(spanStartUT: 1000.0, spanEndUT: 1300.0);
            // loopUT 1150 == dock -> the cycle-0 dock instant has been reached.
            Assert.True(RouteLoopClock.IsDockCrossing(
                unit, loopUT: 1150.0, cycleIndex: 0, recordedDockUT: 1150.0,
                lastObservedLoopCycleIndex: -1, out long dockIdx));
            Assert.Equal(0, dockIdx);
        }

        // catches: re-firing the same cycle once its dock has already been
        // delivered (dockCycleIndex == lastObserved).
        [Fact]
        public void IsDockCrossing_SameDockCycleAsLastObserved_False()
        {
            var unit = BuildUnit();
            // loopUT past dock -> dockCycleIndex == cycleIndex == 3, already observed.
            Assert.False(RouteLoopClock.IsDockCrossing(
                unit, loopUT: 1300.0, cycleIndex: 3, recordedDockUT: 1150.0,
                lastObservedLoopCycleIndex: 3, out long dockIdx));
            Assert.Equal(3, dockIdx);
        }

        // catches DEL-2 (parked tail): a cold-start sample parked in the
        // cadence > span tail (loopUT == spanEnd >= dock) whose cycle was NEVER
        // delivered must still recognize the dock crossing for that cycle. The
        // dock happened (we are past it); the LastObservedLoopCycleIndex snap is
        // the sole re-fire guard, not a tail flag.
        [Fact]
        public void IsDockCrossing_ParkedTail_DockPassed_NotYetDelivered_True()
        {
            // cadence 600 > span 300 -> at loopUT == spanEnd the clock is parked.
            var unit = BuildUnit(spanStartUT: 1000.0, spanEndUT: 1300.0, cadenceSeconds: 600.0);
            Assert.True(RouteLoopClock.IsDockCrossing(
                unit, loopUT: 1300.0, cycleIndex: 0, recordedDockUT: 1150.0,
                lastObservedLoopCycleIndex: -1, out long dockIdx));
            Assert.Equal(0, dockIdx);
            // Once delivered (lastObserved == 0), the parked tail does NOT re-fire.
            Assert.False(RouteLoopClock.IsDockCrossing(
                unit, loopUT: 1300.0, cycleIndex: 0, recordedDockUT: 1150.0,
                lastObservedLoopCycleIndex: 0, out long dockIdx2));
            Assert.Equal(0, dockIdx2);
        }

        // catches: a crossing firing when the dock UT is outside the span (no
        // delivery binding inside the rendered [launch..undock] window). Without
        // the span guard the pre-dock dockCycleIndex (cycleIndex-1) would
        // eventually fire a stale prior cycle.
        [Fact]
        public void IsDockCrossing_DockOutsideSpan_False()
        {
            var unit = BuildUnit(spanStartUT: 1000.0, spanEndUT: 1300.0);
            // dock 5000 outside span; loopUT can never reach it -> never fire.
            Assert.False(RouteLoopClock.IsDockCrossing(
                unit, loopUT: 1300.0, cycleIndex: 0, recordedDockUT: 5000.0,
                lastObservedLoopCycleIndex: -1, out _));
        }

        // catches DEL-2 warp: a single tick that jumps several cycles AND lands
        // PAST the dock of the new cycle fires once for that cycle's dock.
        [Fact]
        public void IsDockCrossing_WarpJump_PastDock_FiresHighestPassedCycle()
        {
            var unit = BuildUnit(spanStartUT: 1000.0, spanEndUT: 1300.0);
            // lastObserved 0; warp lands in cycle 5 with loopUT 1200 (>= dock 1150).
            Assert.True(RouteLoopClock.IsDockCrossing(
                unit, loopUT: 1200.0, cycleIndex: 5, recordedDockUT: 1150.0,
                lastObservedLoopCycleIndex: 0, out long dockIdx));
            Assert.Equal(5, dockIdx); // cycle 5's dock passed -> fire cycle 5
        }

        // catches DEL-2 warp: a single tick that jumps several cycles but lands
        // BEFORE the dock of the new cycle fires once for the PRIOR cycle's dock
        // (which was passed), NOT the new cycle (whose dock is still ahead).
        [Fact]
        public void IsDockCrossing_WarpJump_BeforeNewDock_FiresPriorCycle()
        {
            var unit = BuildUnit(spanStartUT: 1000.0, spanEndUT: 1300.0);
            // lastObserved 0; warp lands in cycle 5 with loopUT 1100 (< dock 1150).
            Assert.True(RouteLoopClock.IsDockCrossing(
                unit, loopUT: 1100.0, cycleIndex: 5, recordedDockUT: 1150.0,
                lastObservedLoopCycleIndex: 0, out long dockIdx));
            Assert.Equal(4, dockIdx); // cycle 4 dock passed; cycle 5 dock still ahead
        }

        // ==================================================================
        // M-MIS-5 (R2): the dock phase lands exactly ON the span end
        // ==================================================================

        // catches (M-MIS-5 D4/R2): with the route window end flipped from the last
        // undock to the last DOCK, recordedDockUT == SpanEndUT exactly - the dock
        // phase sits on the span boundary. Driving the REAL span clock
        // (TryGetRouteLoopState) across two full cycles at cadence == span, the
        // crossing must fire EXACTLY once per cycle (never zero at the boundary,
        // never twice from the boundary frame + the next in-play frame). Per the
        // documented equality convention (ComputeDockCycleIndex uses loopUT >=
        // dock; IsDockUTInSpan is end-inclusive) the crossing for cycle k fires at
        // the first sample at/after cycle k+1's start via dockCycleIndex =
        // cycleIndex - 1.
        [Fact]
        public void DockPhaseAtSpanEnd_CrossingFiresOncePerCycle()
        {
            // span [1000, 1300], cadence == span (300), anchor 1000; dock AT the
            // span end (the M-MIS-5 route geometry).
            var unit = BuildUnit();
            const double dockUT = 1300.0; // == SpanEndUT
            Assert.True(RouteLoopClock.IsDockUTInSpan(unit, dockUT)); // end-inclusive

            long lastObserved = -1;
            int fires = 0;
            var firedCycles = new System.Collections.Generic.List<long>();

            // Sample UTs walking cycles 0..2: mid cycle 0, just before the boundary,
            // the exact boundary, early cycle 1, mid cycle 1, the next boundary,
            // early cycle 2.
            double[] samples = { 1150.0, 1299.5, 1300.0, 1310.0, 1450.0, 1600.0, 1610.0 };
            foreach (double ut in samples)
            {
                Assert.True(RouteLoopClock.TryGetRouteLoopState(
                    unit, ut, out double loopUT, out long cycleIndex, out _));
                if (RouteLoopClock.IsDockCrossing(
                        unit, loopUT, cycleIndex, dockUT, lastObserved, out long dockCycleIndex))
                {
                    fires++;
                    firedCycles.Add(dockCycleIndex);
                    lastObserved = dockCycleIndex; // the caller's snap-forward
                }
            }

            // Exactly one fire per completed cycle: cycle 0's dock (reached at the
            // 1300 boundary) and cycle 1's dock (reached at the 1600 boundary).
            Assert.Equal(2, fires);
            Assert.Equal(new long[] { 0, 1 }, firedCycles.ToArray());
        }
    }
}
