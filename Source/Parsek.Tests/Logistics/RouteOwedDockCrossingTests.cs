using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the M-MIS-11 item 2 owed-dock-crossing emitter
    /// (<see cref="RouteLoopClock.TryGetOwedDockCrossing"/>) and the item 3
    /// cycle-index semantic typing (<see cref="CycleIndexKind"/> /
    /// <see cref="RouteLoopClock.DeriveCycleIndexKind"/>).
    /// </summary>
    /// <remarks>
    /// Parity contract: the emitter is THE fire-once-snap-forward core; the
    /// pre-M-MIS-11 <see cref="RouteLoopClock.IsDockCrossing"/> predicate is a
    /// thin wrapper over it, so for every state the two must agree on both the
    /// crossing bool and the dock cycle index. The parity tests sweep the same
    /// scenario families the fire paths exercise: normal advance, warp-skip
    /// multi-cycle jumps (landing past and before the new cycle's dock), the
    /// cadence &gt; span parked tail, save/reload resume (a persisted
    /// last-observed index), the dock-outside-span gate, and the non-uniform
    /// zero-drift schedule (sawtooth) case. Pure math, no shared static state,
    /// so no [Collection("Sequential")] (mirrors RouteLoopClockTests).
    /// </remarks>
    public class RouteOwedDockCrossingTests
    {
        // span [1000, 1300] (300s), cadence == span. Same fixture shape as
        // RouteLoopClockTests.
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

        // span [0,50] driven by a non-uniform zero-drift schedule; the clock
        // parks between launches (interval >> span). Same synthetic 100/31
        // schedule as RouteLoopClockTests.BuildScheduledUnit.
        private static GhostPlaybackLogic.LoopUnit BuildScheduledUnit(
            out MissionRelaunchSchedule schedule)
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

        // A re-aim-shaped unit: RelaunchSchedule null BY CONSTRUCTION (the
        // builder's re-aim branch always sets relaunchSchedule = null), with a
        // Supported plan + Valid synodic schedule so IsReaim is true.
        private static GhostPlaybackLogic.LoopUnit BuildReaimUnit()
        {
            var plan = new Parsek.Reaim.ReaimMissionPlan
            {
                Supported = true,
                LaunchBody = "Kerbin",
                TargetBody = "Duna",
                CommonAncestor = "Sun",
                RecordedDepartureUT = 1050.0,
                RecordedArrivalUT = 1250.0,
                RecordedTransferTofSeconds = 200.0,
            };
            var sched = new Parsek.Reaim.ReaimWindowPlanner.ReaimWindowSchedule
            {
                Valid = true,
                FirstDepartureUT = 1050.0,
                SynodicPeriodSeconds = 600.0,
                TofSeconds = 200.0,
                PhaseAnchorUT = 1000.0,
                CadenceSeconds = 600.0,
                Prograde = true,
            };
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1300.0, cadenceSeconds: 600.0,
                phaseAnchorUT: 1000.0, overlapCadenceSeconds: 600.0,
                memberWindows: null, relaunchSchedule: null,
                reaimPlan: plan, reaimSchedule: sched);
            Assert.True(unit.IsReaim, "fixture must be a fully-resolved re-aim unit");
            return unit;
        }

        // Asserts the emitter and the legacy predicate agree on one state.
        private static void AssertParity(
            GhostPlaybackLogic.LoopUnit unit, double loopUT, long cycleIndex,
            double recordedDockUT, long lastObserved)
        {
            bool legacy = RouteLoopClock.IsDockCrossing(
                unit, loopUT, cycleIndex, recordedDockUT, lastObserved, out long legacyIdx);
            bool emitted = RouteLoopClock.TryGetOwedDockCrossing(
                unit, loopUT, cycleIndex, recordedDockUT, lastObserved,
                out RouteLoopClock.OwedDockCrossing owed);
            Assert.Equal(legacy, emitted);
            Assert.Equal(legacyIdx, owed.DockCycleIndex);
        }

        // ==================================================================
        // Emitter parity: same crossing sequence as the pre-extraction logic
        // ==================================================================

        // catches: the emitter drifting from the inline fire-once logic across a
        // NORMAL 1x advance - a caller-side snap loop over three cycles must
        // fire exactly cycles 0, 1, 2 in order, once each.
        [Fact]
        public void NormalAdvance_ThreeCycles_FiresEachDockOnceInOrder()
        {
            var unit = BuildUnit(); // span [1000,1300], cadence 300
            const double dockUT = 1150.0;
            long lastObserved = -1;
            var fired = new List<long>();

            // Sample every 50s of UT across three cycles, resolving loop state
            // through the SAME clock the orchestrator uses.
            for (double ut = 1000.0; ut <= 1900.0; ut += 50.0)
            {
                if (!RouteLoopClock.TryGetRouteLoopState(
                        unit, ut, out double loopUT, out long cycleIndex, out _))
                    continue;
                AssertParity(unit, loopUT, cycleIndex, dockUT, lastObserved);
                if (RouteLoopClock.TryGetOwedDockCrossing(
                        unit, loopUT, cycleIndex, dockUT, lastObserved,
                        out RouteLoopClock.OwedDockCrossing owed))
                {
                    fired.Add(owed.DockCycleIndex);
                    lastObserved = owed.DockCycleIndex; // fire once, snap forward
                }
            }

            Assert.Equal(new long[] { 0, 1, 2 }, fired);
        }

        // catches: a warp tick that jumps several cycles landing PAST the new
        // cycle's dock - exactly ONE owed crossing for the highest passed cycle.
        [Fact]
        public void WarpJump_PastNewCycleDock_OneCrossingForHighestPassedCycle()
        {
            var unit = BuildUnit();
            AssertParity(unit, loopUT: 1200.0, cycleIndex: 5,
                recordedDockUT: 1150.0, lastObserved: 0);
            Assert.True(RouteLoopClock.TryGetOwedDockCrossing(
                unit, loopUT: 1200.0, cycleIndex: 5, recordedDockUT: 1150.0,
                lastObservedCycleIndex: 0, out RouteLoopClock.OwedDockCrossing owed));
            Assert.Equal(5, owed.DockCycleIndex);
            // After the snap, nothing further is owed at the same state.
            Assert.False(RouteLoopClock.TryGetOwedDockCrossing(
                unit, loopUT: 1200.0, cycleIndex: 5, recordedDockUT: 1150.0,
                lastObservedCycleIndex: 5, out _));
        }

        // catches: a warp tick landing BEFORE the new cycle's dock - the owed
        // crossing is the PRIOR cycle's dock (which was passed), not the new one.
        [Fact]
        public void WarpJump_BeforeNewCycleDock_OwesPriorCycle()
        {
            var unit = BuildUnit();
            AssertParity(unit, loopUT: 1100.0, cycleIndex: 5,
                recordedDockUT: 1150.0, lastObserved: 0);
            Assert.True(RouteLoopClock.TryGetOwedDockCrossing(
                unit, loopUT: 1100.0, cycleIndex: 5, recordedDockUT: 1150.0,
                lastObservedCycleIndex: 0, out RouteLoopClock.OwedDockCrossing owed));
            Assert.Equal(4, owed.DockCycleIndex);
        }

        // catches: save/reload resume - the persisted last-observed index is the
        // sole re-fire guard, so re-presenting the SAME state after a reload owes
        // nothing, and the NEXT cycle's dock still fires.
        [Fact]
        public void SaveReloadResume_PersistedLastObserved_NoRefire_NextCycleFires()
        {
            var unit = BuildUnit();
            const double dockUT = 1150.0;

            // Pre-save: cycle 1's dock fired and lastObserved=1 was persisted.
            Assert.True(RouteLoopClock.TryGetRouteLoopState(
                unit, 1460.0, out double loopUT1, out long cyc1, out _)); // cycle 1, past dock
            Assert.True(RouteLoopClock.TryGetOwedDockCrossing(
                unit, loopUT1, cyc1, dockUT, 0, out RouteLoopClock.OwedDockCrossing owed1));
            Assert.Equal(1, owed1.DockCycleIndex);

            // Post-reload at the same UT with the persisted index: nothing owed.
            AssertParity(unit, loopUT1, cyc1, dockUT, lastObserved: 1);
            Assert.False(RouteLoopClock.TryGetOwedDockCrossing(
                unit, loopUT1, cyc1, dockUT, 1, out _));

            // The next cycle's dock still fires from the persisted index.
            Assert.True(RouteLoopClock.TryGetRouteLoopState(
                unit, 1760.0, out double loopUT2, out long cyc2, out _)); // cycle 2, past dock
            Assert.True(RouteLoopClock.TryGetOwedDockCrossing(
                unit, loopUT2, cyc2, dockUT, 1, out RouteLoopClock.OwedDockCrossing owed2));
            Assert.Equal(2, owed2.DockCycleIndex);
        }

        // catches: the cadence > span parked tail - the parked sample owes its
        // own cycle's (passed) dock exactly once; parity holds in the tail.
        [Fact]
        public void ParkedTail_OwesOnce_ThenSilentWhileParked()
        {
            var unit = BuildUnit(cadenceSeconds: 600.0); // parks at spanEnd
            const double dockUT = 1150.0;
            Assert.True(RouteLoopClock.TryGetRouteLoopState(
                unit, 1450.0, out double loopUT, out long cyc, out bool tail));
            Assert.True(tail);
            AssertParity(unit, loopUT, cyc, dockUT, lastObserved: -1);
            Assert.True(RouteLoopClock.TryGetOwedDockCrossing(
                unit, loopUT, cyc, dockUT, -1, out RouteLoopClock.OwedDockCrossing owed));
            Assert.Equal(0, owed.DockCycleIndex);
            Assert.False(RouteLoopClock.TryGetOwedDockCrossing(
                unit, loopUT, cyc, dockUT, 0, out _)); // no re-fire while parked
        }

        // catches: a dock UT outside the span producing a crossing (the span
        // gate must hold in the emitter exactly as in the legacy predicate).
        [Fact]
        public void DockOutsideSpan_NeverOwed_ParityHolds()
        {
            var unit = BuildUnit();
            AssertParity(unit, loopUT: 1300.0, cycleIndex: 3,
                recordedDockUT: 5000.0, lastObserved: -1);
            Assert.False(RouteLoopClock.TryGetOwedDockCrossing(
                unit, loopUT: 1300.0, cycleIndex: 3, recordedDockUT: 5000.0,
                lastObservedCycleIndex: -1, out _));
        }

        // catches: the emitter drifting from the legacy predicate on a scheduled
        // (non-uniform sawtooth) unit - crossings fire per SCHEDULED launch index
        // and the result carries Kind=Scheduled.
        [Fact]
        public void ScheduledUnit_SawtoothLaunches_FiresPerScheduledIndex_KindScheduled()
        {
            var unit = BuildScheduledUnit(out MissionRelaunchSchedule schedule);
            const double dockUT = 40.0; // inside the [0,50] span
            long lastObserved = -1;
            var fired = new List<long>();

            // Walk far enough to cover the first two scheduled launches.
            for (double ut = 0.0; ut <= 5000.0; ut += 10.0)
            {
                if (!RouteLoopClock.TryGetRouteLoopState(
                        unit, ut, out double loopUT, out long cycleIndex, out _))
                    continue;
                AssertParity(unit, loopUT, cycleIndex, dockUT, lastObserved);
                if (RouteLoopClock.TryGetOwedDockCrossing(
                        unit, loopUT, cycleIndex, dockUT, lastObserved,
                        out RouteLoopClock.OwedDockCrossing owed))
                {
                    Assert.Equal(CycleIndexKind.Scheduled, owed.Kind);
                    fired.Add(owed.DockCycleIndex);
                    lastObserved = owed.DockCycleIndex;
                }
            }

            // One fire per scheduled launch whose dock phase was reached, in
            // ascending scheduled-index order starting at 0.
            Assert.True(fired.Count >= 2, "walk must cover at least two scheduled launches");
            for (int i = 0; i < fired.Count; i++)
                Assert.Equal(i, fired[i]);
        }

        // ==================================================================
        // CycleIndexKind classification (item 3)
        // ==================================================================

        // catches: a null-schedule (v0 same-body flat-cadence) unit not
        // classifying Flat.
        [Fact]
        public void DeriveCycleIndexKind_NullSchedule_Flat()
        {
            Assert.Equal(CycleIndexKind.Flat, RouteLoopClock.DeriveCycleIndexKind(BuildUnit()));
        }

        // catches: a zero-drift scheduled unit not classifying Scheduled (its
        // cycleIndex is MissionRelaunchSchedule's sIdx; launch UTs are
        // non-uniform, so flat anchor + k*cadence arithmetic is invalid).
        [Fact]
        public void DeriveCycleIndexKind_RelaunchSchedule_Scheduled()
        {
            var unit = BuildScheduledUnit(out _);
            Assert.Equal(CycleIndexKind.Scheduled, RouteLoopClock.DeriveCycleIndexKind(unit));
        }

        // A RE-AIM unit classifies FLAT. Determination (M-MIS-11 item 3): the
        // builder's re-aim branch sets relaunchSchedule = null, so at runtime the
        // unit resolves through TryComputeSpanLoopUT's UNIFORM branch
        // (cycleIndex = floor(elapsed / synodicCadence)) - a flat-cadence index
        // over synodic-spaced cycles, and that same flat index doubles as the
        // synodic window index (ReaimPlaybackResolver: windowIndex = cycleIndex).
        // Scheduled is reserved for the genuinely NON-uniform
        // MissionRelaunchSchedule index, whose launch UTs cannot be derived by
        // flat arithmetic. (The re-aim unit's non-identity WITHIN-cycle phase
        // mapping is guarded separately by the LoiterCuts gate in
        // TryComputeSecondsToNextDockCrossing, not by the kind.)
        [Fact]
        public void DeriveCycleIndexKind_ReaimUnit_Flat()
        {
            var unit = BuildReaimUnit();
            Assert.Null(unit.RelaunchSchedule); // by construction in the builder
            Assert.Equal(CycleIndexKind.Flat, RouteLoopClock.DeriveCycleIndexKind(unit));
        }

        // catches: the emitter stamping the wrong kind on a flat unit's result.
        [Fact]
        public void Emitter_FlatUnit_ResultCarriesKindFlat()
        {
            var unit = BuildUnit();
            RouteLoopClock.TryGetOwedDockCrossing(
                unit, loopUT: 1200.0, cycleIndex: 0, recordedDockUT: 1150.0,
                lastObservedCycleIndex: -1, out RouteLoopClock.OwedDockCrossing owed);
            Assert.Equal(CycleIndexKind.Flat, owed.Kind);
        }
    }
}
