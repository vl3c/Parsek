using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the delivery clock of a descent-trigger route unit (todo
    /// ROUTE-DELIVERY-IGNORES-DESCENT-TRIGGER): a re-aim route whose last leg lands or docks at
    /// the destination renders that leg off the re-anchored descent head
    /// (<see cref="Parsek.Reaim.DescentTrigger"/>), so the dock crossing must fire when that head
    /// reaches the recorded dock UT, not when the raw span clock does.
    /// </summary>
    /// <remarks>
    /// Fixture timing (cycle 0, phase anchor 0, no loiter cuts): captureShift -1000 puts the shifted
    /// conic end at 200, so the icon reaches the deorbit point at live 200 (entry); the first live UT
    /// congruent to the recorded deorbit 1200 mod Trot 300 is 300 (trigger). The descent clip
    /// [1200, 1300] therefore renders over live [300, 400] and the recorded dock at 1250 is rendered
    /// at live 350, while the raw clock only reaches 1250 at live 1250. Pure math, no shared static
    /// state, so no Sequential collection (the re-aim resolver cache is empty, so the site-align
    /// offset is 0).
    /// </remarks>
    public class RouteDescentDeliveryClockTests
    {
        private const double RecDeorbit = 1200.0;
        private const double DescentEnd = 1300.0;
        private const double DockUT = 1250.0;
        private const double CaptureShift = -1000.0;
        private const double Trot = 300.0;
        private const double Tpark = 80.0;
        private const double SpanStart = 0.0;
        private const double SpanEnd = 2000.0;
        private const double Cadence = 4000.0;

        private static GhostPlaybackLogic.LoopUnit DescentUnit()
        {
            var plan = new Parsek.Reaim.ReaimMissionPlan { Supported = true };
            var sched = new Parsek.Reaim.ReaimWindowPlanner.ReaimWindowSchedule { Valid = true };
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 1, memberIndices: new[] { 0, 1 },
                spanStartUT: SpanStart, spanEndUT: SpanEnd, cadenceSeconds: Cadence, phaseAnchorUT: 0.0,
                overlapCadenceSeconds: Cadence, memberWindows: null, relaunchSchedule: null,
                reaimPlan: plan, reaimSchedule: sched,
                loiterCuts: null, arrivalHoldSeconds: 0.0, arrivalHoldAtUT: double.NaN,
                arrivalAlignPeriodSeconds: double.NaN, arrivalAmberReason: null,
                launchBodyRotationPeriodSeconds: double.NaN, launchHoldEngaged: false, recordedSoiExitUT: double.NaN,
                descentMemberIndices: new[] { 0 }, recordedDeorbitUT: RecDeorbit,
                descentEndUT: DescentEnd, destinationBodyRotationPeriodSeconds: Trot,
                loiterPeriodSeconds: Tpark, captureShiftSeconds: CaptureShift,
                transferMemberIndex: 1);
        }

        private static GhostPlaybackLogic.LoopUnit FlatUnit()
        {
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 1, memberIndices: new[] { 0, 1 },
                spanStartUT: SpanStart, spanEndUT: SpanEnd, cadenceSeconds: Cadence, phaseAnchorUT: 0.0);
        }

        private static bool Crossed(GhostPlaybackLogic.LoopUnit unit, double liveUT)
        {
            Assert.True(RouteLoopClock.TryGetRouteLoopState(
                unit, liveUT, out double loopUT, out long cycle, out _));
            return RouteLoopClock.TryGetOwedDockCrossing(
                unit, loopUT, cycle, DockUT, -1, out _);
        }

        [Fact]
        public void Fixture_TriggerTiming_IsTheRenderersTiming()
        {
            var unit = DescentUnit();
            Assert.True(unit.HasDescentTrigger);
            Parsek.Reaim.DescentTrigger.ComputeDescentTiming(
                0, unit.PhaseAnchorUT, unit.CadenceSeconds, unit.SpanStartUT, RecDeorbit, Trot,
                CaptureShift, null, out double conicEnd, out double entryUT, out double triggerUT);
            Assert.Equal(200.0, conicEnd, 6);
            Assert.Equal(200.0, entryUT, 6);
            Assert.Equal(300.0, triggerUT, 6);

            // The renderer's own head for the descent member at live 350 is the recorded dock UT.
            Assert.True(Parsek.Reaim.DescentTrigger.TryResolveDescentMemberHead(
                350.0, 0, unit.PhaseAnchorUT, unit.CadenceSeconds, unit.SpanStartUT, RecDeorbit,
                DescentEnd, Trot, Tpark, CaptureShift, null, RecDeorbit, DescentEnd,
                out double head, out _));
            Assert.Equal(DockUT, head, 6);
        }

        [Fact]
        public void DescentUnit_DockCrossing_FiresWhenTheRenderedGhostDocks()
        {
            var unit = DescentUnit();
            Assert.False(Crossed(unit, 340.0)); // rendered head 1240: still descending
            Assert.True(Crossed(unit, 351.0));  // rendered head 1251: docked / landed
            Assert.True(Crossed(unit, 900.0));  // descent done, raw clock still at 900
        }

        [Fact]
        public void DescentUnit_Loiter_HoldsBelowTheDeorbitPoint()
        {
            var unit = DescentUnit();
            Assert.True(RouteLoopClock.TryGetRouteLoopState(unit, 250.0, out double loopUT, out _, out _));
            Assert.True(loopUT < RecDeorbit, "loiter head must stay before the deorbit point, got " + loopUT);
            Assert.True(loopUT >= 200.0 - 1e-9, "loiter head never drops below the shifted conic end");
        }

        [Fact]
        public void DescentUnit_BeforeEntry_KeepsTheRawClock()
        {
            var unit = DescentUnit();
            Assert.True(RouteLoopClock.TryGetRouteLoopState(unit, 150.0, out double loopUT, out long cycle, out _));
            Assert.Equal(150.0, loopUT, 6);
            Assert.Equal(0L, cycle);
        }

        [Fact]
        public void DescentUnit_HeadIsCappedAtSpanEnd_AndTailFollowsIt()
        {
            var unit = DescentUnit();
            Assert.True(RouteLoopClock.TryGetRouteLoopState(unit, 3000.0, out double loopUT, out _, out bool tail));
            Assert.Equal(SpanEnd, loopUT, 6);
            Assert.True(tail);
        }

        [Fact]
        public void FlatUnit_SameTimes_RawClockUnchanged()
        {
            var unit = FlatUnit();
            Assert.False(unit.HasDescentTrigger);
            Assert.False(Crossed(unit, 351.0));
            Assert.True(Crossed(unit, 1251.0));
            Assert.True(RouteLoopClock.TryGetRouteLoopState(unit, 351.0, out double loopUT, out _, out _));
            Assert.Equal(351.0, loopUT, 6);
        }

        [Fact]
        public void DescentUnit_Countdown_Refuses()
        {
            Assert.False(RouteLoopClock.TryComputeSecondsToNextDockCrossing(
                DescentUnit(), 100.0, DockUT, -1, out _));
            Assert.True(RouteLoopClock.TryComputeSecondsToNextDockCrossing(
                FlatUnit(), 100.0, DockUT, -1, out double seconds));
            Assert.Equal(1150.0, seconds, 6);
        }

        [Fact]
        public void ResolveDescentDeliveryLoopUT_NonDescentUnit_IsIdentity()
        {
            Assert.Equal(777.0, RouteLoopClock.ResolveDescentDeliveryLoopUT(FlatUnit(), 351.0, 777.0, 0), 9);
            Assert.Equal(777.0, RouteLoopClock.ResolveDescentDeliveryLoopUT(DescentUnit(), 351.0, 777.0, -1), 9);
        }
    }
}
