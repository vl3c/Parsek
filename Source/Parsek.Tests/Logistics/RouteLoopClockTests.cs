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

        // ==================================================================
        // TryGetRouteLoopState — span-clock pass-through
        // ==================================================================

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
        // IsCrossing predicate
        // ==================================================================

        // catches: a fresh cycle index past the last observed, actively playing,
        // dock-in-span NOT being recognized as a crossing.
        [Fact]
        public void IsCrossing_NewCycle_NotTail_DockInSpan_True()
        {
            var unit = BuildUnit(spanStartUT: 1000.0, spanEndUT: 1300.0);
            Assert.True(RouteLoopClock.IsCrossing(
                unit, cycleIndex: 0, isInInterCycleTail: false,
                recordedDockUT: 1150.0, lastObservedLoopCycleIndex: -1));
        }

        // catches: re-firing the same cycle (cycleIndex == lastObserved).
        [Fact]
        public void IsCrossing_SameCycleAsLastObserved_False()
        {
            var unit = BuildUnit();
            Assert.False(RouteLoopClock.IsCrossing(
                unit, cycleIndex: 3, isInInterCycleTail: false,
                recordedDockUT: 1150.0, lastObservedLoopCycleIndex: 3));
        }

        // catches: a crossing firing while the clock is parked in the idle tail
        // (vessel already delivered, nothing in transit).
        [Fact]
        public void IsCrossing_InTail_False()
        {
            var unit = BuildUnit();
            Assert.False(RouteLoopClock.IsCrossing(
                unit, cycleIndex: 1, isInInterCycleTail: true,
                recordedDockUT: 1150.0, lastObservedLoopCycleIndex: 0));
        }

        // catches: a crossing firing when the dock UT is outside the span (no
        // delivery binding inside the rendered [launch..undock] window).
        [Fact]
        public void IsCrossing_DockOutsideSpan_False()
        {
            var unit = BuildUnit(spanStartUT: 1000.0, spanEndUT: 1300.0);
            Assert.False(RouteLoopClock.IsCrossing(
                unit, cycleIndex: 0, isInInterCycleTail: false,
                recordedDockUT: 5000.0, lastObservedLoopCycleIndex: -1));
        }

        // catches: a multi-cycle warp jump being mishandled by the predicate —
        // it must still recognize the crossing (the orchestrator snaps forward
        // and fires ONCE, but IsCrossing itself only checks "advanced past last").
        [Fact]
        public void IsCrossing_WarpJump_StillTrue()
        {
            var unit = BuildUnit(spanStartUT: 1000.0, spanEndUT: 1300.0);
            // lastObserved = 0, current cycle jumped to 5 (fast warp).
            Assert.True(RouteLoopClock.IsCrossing(
                unit, cycleIndex: 5, isInInterCycleTail: false,
                recordedDockUT: 1150.0, lastObservedLoopCycleIndex: 0));
        }
    }
}
