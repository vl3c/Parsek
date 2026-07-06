using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Drift guard for the M-MIS-11 (PR #1237) item 2 scope trim: the read-only
    /// UI countdown <see cref="RouteLoopClock.TryComputeSecondsToNextDockCrossing"/>
    /// was deliberately NOT routed through the owed-crossing emitter
    /// <see cref="RouteLoopClock.TryGetOwedDockCrossing"/>; it keeps its own
    /// closed-form dock-UT math (dockUT_k = PhaseAnchorUT + k*cadence + offset)
    /// that mirrors the emitter / span-clock semantics BY CONVENTION only (the
    /// cadence clamp cites "SAME as TryComputeSpanLoopUT (edge 14)", the re-fire
    /// guard cites "matches IsDockCrossing's guard"). A future change to the
    /// emitter or the span-clock clamp could silently drift the UI countdown
    /// from the actual fire path. These tests cross-pin the two: for every
    /// covered unit/state the countdown-predicted next-dock UT must be EXACTLY
    /// the UT at which the emitter first fires when driven through the real
    /// fire path (<see cref="RouteLoopClock.TryGetRouteLoopState"/> +
    /// <see cref="RouteLoopClock.TryGetOwedDockCrossing"/>), and for the
    /// non-uniform units the countdown refuses (returns false) the closed-form
    /// approximation while the fire path stays alive.
    /// </summary>
    /// <remarks>
    /// Pure math, no shared static state, so no [Collection("Sequential")]
    /// (mirrors RouteOwedDockCrossingTests). All UT fixtures are integer-valued
    /// so the dock-instant boundaries are floating-point exact.
    /// </remarks>
    public class RouteCountdownEmitterDriftGuardTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // Just below the boundary; far above double noise at these UT scales.
        private const double BoundaryEpsilon = 1e-3;

        // ==================================================================
        // Fixtures (same shapes as RouteNextCrossingTests / RouteOwedDockCrossingTests)
        // ==================================================================

        private static GhostPlaybackLogic.LoopUnit BuildUnit(
            double spanStartUT = 1000.0, double spanEndUT = 1300.0,
            double cadenceSeconds = 300.0, double phaseAnchorUT = 1000.0)
        {
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: spanStartUT, spanEndUT: spanEndUT,
                cadenceSeconds: cadenceSeconds, phaseAnchorUT: phaseAnchorUT);
        }

        // Zero-drift scheduled unit: uniform anchor-only schedule launching at
        // 1000 + k*300 (k >= 1, so FirstLaunchUT = 1300), span [1000, 1300].
        private static GhostPlaybackLogic.LoopUnit BuildScheduledUnit()
        {
            var schedule = new MissionRelaunchSchedule(
                ut0: 1000.0, anchorPeriod: 300.0,
                otherPeriods: null, otherTolerances: null,
                floorUT: 1000.0, lookaheadMultiples: 8);
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1300.0,
                cadenceSeconds: 300.0, phaseAnchorUT: schedule.FirstLaunchUT,
                overlapCadenceSeconds: 300.0, memberWindows: null,
                relaunchSchedule: schedule);
        }

        // Re-aim-shaped unit: Flat kind but non-identity within-cycle phase (a
        // 40s whole-period loiter cut at [1050, 1090]).
        private static GhostPlaybackLogic.LoopUnit BuildUnitWithLoiterCuts()
        {
            var cuts = new List<GhostPlaybackLogic.LoopCut>
            {
                new GhostPlaybackLogic.LoopCut { StartUT = 1050.0, LengthSeconds = 40.0 },
            };
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1300.0,
                cadenceSeconds: 300.0, phaseAnchorUT: 1000.0,
                overlapCadenceSeconds: 300.0, memberWindows: null,
                relaunchSchedule: null, reaimPlan: null, reaimSchedule: null,
                loiterCuts: cuts);
        }

        // ==================================================================
        // Fire-path helpers
        // ==================================================================

        // Drives the ACTUAL fire path for a single sample: resolve the loop
        // state the way ProcessLoopRoute does, then ask the emitter. False when
        // the loop state does not resolve (pre-anchor / pre-first-launch park).
        private static bool EmitterFiresAt(
            GhostPlaybackLogic.LoopUnit unit, double ut, double recordedDockUT,
            long lastObservedCycleIndex, out RouteLoopClock.OwedDockCrossing owed)
        {
            owed = default(RouteLoopClock.OwedDockCrossing);
            if (!RouteLoopClock.TryGetRouteLoopState(
                    unit, ut, out double loopUT, out long cycleIndex, out _))
                return false;
            return RouteLoopClock.TryGetOwedDockCrossing(
                unit, loopUT, cycleIndex, recordedDockUT, lastObservedCycleIndex,
                out owed);
        }

        // Coarse forward scan for the first UT at which the emitter fires.
        private static double? FindFirstFireUT(
            GhostPlaybackLogic.LoopUnit unit, double fromUT, double toUT,
            double stepSeconds, double recordedDockUT, long lastObservedCycleIndex,
            out RouteLoopClock.OwedDockCrossing owed)
        {
            for (double ut = fromUT; ut <= toUT; ut += stepSeconds)
            {
                if (EmitterFiresAt(unit, ut, recordedDockUT, lastObservedCycleIndex, out owed))
                    return ut;
            }
            owed = default(RouteLoopClock.OwedDockCrossing);
            return null;
        }

        // THE cross-pin: the countdown's predicted next-dock UT must be exactly
        // the first UT at which the emitter fires for the same unit/state.
        //  - precondition: nothing is owed at nowUT (otherwise "next" is
        //    ambiguous, the orchestrator would fire immediately);
        //  - no fire at any sampled UT strictly before the predicted UT,
        //    including immediately before the boundary;
        //  - a fire exactly AT the predicted UT, for the next unconsumed cycle
        //    (lastObserved + 1, the fire-once snap-forward target).
        private static void AssertCountdownMatchesFirstEmitterFire(
            GhostPlaybackLogic.LoopUnit unit, double nowUT, double recordedDockUT,
            long lastObservedCycleIndex, int scanSamples = 64)
        {
            Assert.False(
                EmitterFiresAt(unit, nowUT, recordedDockUT, lastObservedCycleIndex, out _),
                $"fixture invalid: a crossing is already owed at nowUT={nowUT.ToString("R", IC)}");

            bool ok = RouteLoopClock.TryComputeSecondsToNextDockCrossing(
                unit, nowUT, recordedDockUT, lastObservedCycleIndex, out double seconds);
            Assert.True(ok, "countdown unexpectedly declined a uniform v0 unit");
            Assert.True(seconds > 0.0, "countdown must be strictly positive");
            double predictedDockUT = nowUT + seconds;

            for (int i = 1; i <= scanSamples; i++)
            {
                double ut = nowUT + (predictedDockUT - nowUT) * i / (scanSamples + 1);
                Assert.False(
                    EmitterFiresAt(unit, ut, recordedDockUT, lastObservedCycleIndex, out var early),
                    $"emitter fired at UT {ut.ToString("R", IC)} (cycle {early.DockCycleIndex.ToString(IC)}), " +
                    $"BEFORE the countdown-predicted dock UT {predictedDockUT.ToString("R", IC)}: " +
                    "the UI countdown overshoots the actual fire path");
            }
            Assert.False(
                EmitterFiresAt(unit, predictedDockUT - BoundaryEpsilon, recordedDockUT,
                    lastObservedCycleIndex, out _),
                $"emitter fired {BoundaryEpsilon.ToString("R", IC)}s before the predicted dock UT " +
                $"{predictedDockUT.ToString("R", IC)}: countdown/emitter boundary drift");

            Assert.True(
                EmitterFiresAt(unit, predictedDockUT, recordedDockUT,
                    lastObservedCycleIndex, out var owed),
                $"emitter did NOT fire at the countdown-predicted dock UT " +
                $"{predictedDockUT.ToString("R", IC)}: the UI countdown undershoots the actual fire path");
            Assert.Equal(lastObservedCycleIndex + 1, owed.DockCycleIndex);
        }

        // ==================================================================
        // (1) Flat route, cadence == span (the common v0 shape)
        // ==================================================================

        // span [1000,1300], cadence 300, dock 1150 -> dockUT_k = 1000 + k*300 + 150.
        [Theory]
        [InlineData(1100.0, -1L)] // before cycle 0's dock -> 1150
        [InlineData(1200.0, 0L)]  // after cycle 0's dock, consumed -> 1450
        [InlineData(1100.0, 1L)]  // lastObserved advanced -> skips to 1750
        public void Flat_CadenceEqualsSpan_CountdownMatchesFirstFire(
            double nowUT, long lastObserved)
        {
            AssertCountdownMatchesFirstEmitterFire(
                BuildUnit(), nowUT, recordedDockUT: 1150.0, lastObserved);
        }

        // ==================================================================
        // (2) Flat route, cadence > span (parked inter-cycle tail)
        // ==================================================================

        // span [1000,1300], cadence 500 -> dockUT_k = 1000 + k*500 + 150; the
        // clock parks at spanEnd for phase [300,500) of each cycle.
        [Theory]
        [InlineData(1400.0, 0L)] // inside cycle 0's parked tail -> 1650
        [InlineData(1550.0, 0L)] // cycle 1, before its dock -> 1650
        [InlineData(1900.0, 1L)] // inside cycle 1's parked tail -> 2150
        public void Flat_CadenceExceedsSpan_ParkedTail_CountdownMatchesFirstFire(
            double nowUT, long lastObserved)
        {
            AssertCountdownMatchesFirstEmitterFire(
                BuildUnit(cadenceSeconds: 500.0), nowUT,
                recordedDockUT: 1150.0, lastObserved);
        }

        // ==================================================================
        // (3) Multi-stop dock offsets (early / mid / late in the span)
        // ==================================================================

        // The multi-stop scan feeds each stop's recordedDockUT through the SAME
        // emitter; the countdown must agree per stop for every in-span offset.
        [Theory]
        [InlineData(1030.0, 0L)]  // early-span stop, cycle 0 consumed -> 1330
        [InlineData(1150.0, -1L)] // mid-span stop -> 1150
        [InlineData(1290.0, -1L)] // late-span stop (near spanEnd) -> 1290
        public void MultiStop_PerStopOffsets_CountdownMatchesFirstFire(
            double recordedDockUT, long lastObserved)
        {
            AssertCountdownMatchesFirstEmitterFire(
                BuildUnit(), nowUT: 1100.0, recordedDockUT, lastObserved);
        }

        // ==================================================================
        // (4) Cadence-clamp edge (edge 14 mirror)
        // ==================================================================

        // cadence 1 < LoopTiming.MinCycleDuration: the span clock clamps the
        // cadence INTERNALLY; the countdown duplicates that clamp. If either
        // side's clamp changes alone, the predicted dock UT (8) and the actual
        // fire instant diverge.
        [Fact]
        public void SubMinCadence_ClampMirrored_CountdownMatchesFirstFire()
        {
            var unit = BuildUnit(
                spanStartUT: 0.0, spanEndUT: 10.0,
                cadenceSeconds: 1.0, phaseAnchorUT: 0.0);

            // clamped cadence 5 -> dockUT_k = 5k + 3; cycle 0 (dock 3) consumed,
            // next fire at 8.
            AssertCountdownMatchesFirstEmitterFire(
                unit, nowUT: 4.0, recordedDockUT: 3.0, lastObservedCycleIndex: 0);
        }

        // ==================================================================
        // (5) Dense nowUT sweep over the parked-tail configuration
        // ==================================================================

        // For every sampled nowUT the state is normalized to "everything up to
        // now consumed" (lastObserved = the owed cycle index the emitter itself
        // reports at nowUT, populated even on a no-fire result), then the
        // cross-pin must hold. Covers play-phase, dock-boundary, and parked-tail
        // nowUTs in one sweep.
        [Fact]
        public void Flat_DenseNowUTSweep_CountdownAlwaysMatchesFirstFire()
        {
            var unit = BuildUnit(cadenceSeconds: 500.0); // dockUT_k = 1150 + 500k

            for (double nowUT = 1001.0; nowUT <= 2999.0; nowUT += 13.0)
            {
                Assert.True(RouteLoopClock.TryGetRouteLoopState(
                    unit, nowUT, out double loopUT, out long cycleIndex, out _));
                RouteLoopClock.TryGetOwedDockCrossing(
                    unit, loopUT, cycleIndex, 1150.0, long.MinValue + 1, out var owedNow);
                long lastObserved = owedNow.DockCycleIndex; // consume everything up to now

                AssertCountdownMatchesFirstEmitterFire(
                    unit, nowUT, recordedDockUT: 1150.0, lastObserved, scanSamples: 16);
            }
        }

        // ==================================================================
        // (6) Non-uniform units: the countdown REFUSES, the fire path stays alive
        // ==================================================================

        // Zero-drift scheduled route: launch UTs are non-uniform by definition,
        // so the closed-form countdown must DECLINE (the documented trim) while
        // the emitter still fires through the schedule branch of the span clock.
        // If a future change makes the countdown answer for Scheduled units
        // without routing through the emitter, this pin fails.
        [Fact]
        public void ScheduledUnit_CountdownDeclines_WhileEmitterStillFires()
        {
            var unit = BuildScheduledUnit(); // launches 1300, 1600, ...; dock offset 150

            bool ok = RouteLoopClock.TryComputeSecondsToNextDockCrossing(
                unit, nowUT: 1350.0, recordedDockUT: 1150.0,
                lastObservedLoopCycleIndex: -1, out double seconds);
            Assert.False(ok, "countdown must decline a Scheduled unit, not approximate it");
            Assert.Equal(0.0, seconds);

            double? firstFireUT = FindFirstFireUT(
                unit, fromUT: 1300.0, toUT: 2500.0, stepSeconds: 1.0,
                recordedDockUT: 1150.0, lastObservedCycleIndex: -1, out var owed);
            Assert.True(firstFireUT.HasValue,
                "fire path must still emit crossings for a Scheduled unit (the countdown " +
                "decline is a UI trim, not a missing crossing)");
            Assert.Equal(CycleIndexKind.Scheduled, owed.Kind);
        }

        // Re-aim-shaped unit (Flat kind, loiter cuts): the cuts compress the
        // within-cycle phase, so the naive closed-form dock UT is WRONG for it;
        // the countdown must decline while the emitter fires on the compressed
        // clock. Also pins that the actual first fire differs from the naive
        // formula's prediction - the concrete reason Flat kind alone does not
        // license the closed-form math.
        [Fact]
        public void LoiterCutsUnit_CountdownDeclines_WhileEmitterStillFires()
        {
            var unit = BuildUnitWithLoiterCuts(); // 40s cut at [1050,1090], dock 1150

            bool ok = RouteLoopClock.TryComputeSecondsToNextDockCrossing(
                unit, nowUT: 1000.0, recordedDockUT: 1150.0,
                lastObservedLoopCycleIndex: -1, out double seconds);
            Assert.False(ok, "countdown must decline a loiter-cuts unit, not approximate it");
            Assert.Equal(0.0, seconds);

            double? firstFireUT = FindFirstFireUT(
                unit, fromUT: 1000.0, toUT: 1600.0, stepSeconds: 1.0,
                recordedDockUT: 1150.0, lastObservedCycleIndex: -1, out var owed);
            Assert.True(firstFireUT.HasValue,
                "fire path must still emit crossings for a loiter-cuts unit");
            Assert.Equal(CycleIndexKind.Flat, owed.Kind);

            // Naive closed-form dockUT_0 = anchor + (dock - spanStart) = 1150; the
            // cut pulls the actual fire earlier (compressed phase reaches the dock
            // at ~1110). If these ever coincide the trim rationale is stale.
            double naiveDockUT0 = 1000.0 + (1150.0 - 1000.0);
            Assert.True(firstFireUT.Value < naiveDockUT0,
                $"expected the compressed-clock fire ({firstFireUT.Value.ToString("R", IC)}) " +
                $"strictly before the naive closed-form dock UT ({naiveDockUT0.ToString("R", IC)})");
        }
    }
}
