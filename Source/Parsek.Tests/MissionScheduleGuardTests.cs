using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    // Phase B + R2/R3 of the zero-drift per-window reschedule HARDENING plan
    // (docs/dev/plans/zero-drift-reschedule-hardening.md). Three groups:
    //   (a) INV-3 self-defending guard: TryComputeSpanLoopUT's schedule branch emits a rate-limited
    //       warning when it is handed a unit that ALSO carries loiterCuts / arrivalHold (a contract
    //       violation - they are mutually exclusive by construction; the schedule branch silently drops
    //       the cut/hold). Driven through the public span-clock seam with a contrived misuse unit.
    //   (b) R3 amber: the extracted pure ShouldTintTMinusAmber helper tints a SCHEDULED unit off the
    //       SCHEDULE's own worst-launch flag (AllLaunchesWithinTolerance), NOT the fixed m*P-fit
    //       WithinTolerance (which is false for the in-tolerance stock Mun), so a faithful unit reads
    //       green while a genuinely over-tolerance bounded-best schedule reads amber.
    //   (c) R2 (branch ii): a realistic SAME-PARENT config (Kerbin pad + Mun orbit/SOI + Mun-surface
    //       0.25 deg + Minmus orbit/SOI) IS bounded-best-reachable within ScheduleLookaheadMultiples
    //       (=4096) using physics-derived tolerances only; the schedule still resolves MONOTONICALLY
    //       INCREASING launches and surfaces AllLaunchesWithinTolerance==false. This proves INV-1's
    //       residual oracle must be scoped to the within-tol set (it does NOT bound a bounded-best
    //       config), and that the R3 flag the amber tint reads is correct on a real over-tolerance case.
    //
    // [Collection("Sequential")] because the guard test touches ParsekLog shared static state.
    [Collection("Sequential")]
    public class MissionScheduleGuardTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MissionScheduleGuardTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            // The INV-3 guard emits via VerboseRateLimited, which is gated on IsVerboseEnabled and a
            // per-key time window. Force verbose on and clear the rate-limit state so the FIRST keyed
            // occurrence emits deterministically.
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            MissionPeriodicity.SuppressLogging = false;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            MissionPeriodicity.SuppressLogging = false;
        }

        // ===================== Stock-like body magnitudes =====================
        // Kerbin sidereal day, Mun orbit, Minmus orbit (stock).
        private const double KerbinRotation = 21549.425;
        private const double MunOrbit = 138984.38;
        private const double MinmusOrbit = 1077311.0;

        // Physics-derived tolerances (the same forms TryBuildRelaunchSchedule / ToleranceSecondsFor use):
        //   rotation pad: 0.25 deg of the rotation period;
        //   orbital: SoiRadius / OrbitalVelocity (the time the body crosses its own SOI);
        //   transited-body surface rotation (Tight): 0.25 deg of the body's rotation period.
        private const double PadRotTolerance = KerbinRotation * (0.25 / 360.0);
        private const double MunSoiTolerance = 2429559.0 / 543.0;          // ~4474 s
        private const double MinmusSoiTolerance = 2247428.4 / 93.72;       // ~23980 s
        private const double MunSurfaceRotTolerance = MunOrbit * (0.25 / 360.0); // tidally locked -> period == orbit

        // The synthetic 100/31/tol=2 schedule (launches 900,1300,1800,...) used to drive the span clock
        // in the guard test. Identical magnitudes to MissionZeroDriftScheduleTests.SyntheticSchedule.
        private static MissionRelaunchSchedule SyntheticSchedule()
            => new MissionRelaunchSchedule(
                0.0, 100.0, new double[] { 31.0 }, new double[] { 2.0 }, floorUT: 0.0,
                lookaheadMultiples: 100);

        // A realistic SAME-PARENT WITHIN-TOLERANCE schedule (the stock Mun 2-constraint case: pad anchor +
        // Mun orbit/SOI). Reaches a within-tol window at k~=13, so AllLaunchesWithinTolerance stays true.
        private static MissionRelaunchSchedule StockMunWithinTolSchedule()
            => new MissionRelaunchSchedule(
                0.0, KerbinRotation, new double[] { MunOrbit }, new double[] { MunSoiTolerance },
                floorUT: 0.0, lookaheadMultiples: MissionPeriodicity.ScheduleLookaheadMultiples);

        // A realistic SAME-PARENT BOUNDED-BEST schedule (R2 branch ii): pad anchor + Mun orbit/SOI +
        // Mun-surface 0.25 deg + Minmus orbit/SOI. No k in [1,4096] satisfies all three physics-derived
        // tolerances simultaneously, so EVERY launch falls to bounded-best -> AllLaunchesWithinTolerance
        // is false. (The Mun appears twice: once as the orbital intercept, once as the tidally-locked
        // surface rotation, the same way a land-and-return mission's recorder captures both.)
        private static MissionRelaunchSchedule SameParentBoundedBestSchedule()
            => new MissionRelaunchSchedule(
                0.0, KerbinRotation,
                new double[] { MunOrbit, MunOrbit, MinmusOrbit },
                new double[] { MunSoiTolerance, MunSurfaceRotTolerance, MinmusSoiTolerance },
                floorUT: 0.0, lookaheadMultiples: MissionPeriodicity.ScheduleLookaheadMultiples);

        // ===================== (a) INV-3 self-defending guard =====================

        [Fact]
        public void SpanClock_Scheduled_WithLoiterCuts_EmitsMutexViolationWarning()
        {
            // Guards INV-3: a scheduled unit is mutually exclusive with loiter cuts by construction. The
            // span clock's schedule branch bypasses the cut/hold remap, so it must LOUDLY (but
            // rate-limited, this is a per-frame hot path) warn if it is ever handed both, instead of
            // silently dropping the cut. Feed both a schedule AND a loiterCut and assert the keyed
            // warning fires on the first occurrence.
            var schedule = SyntheticSchedule();
            var cuts = new List<GhostPlaybackLogic.LoopCut>
            {
                new GhostPlaybackLogic.LoopCut { StartUT = 10.0, LengthSeconds = 5.0 }
            };

            // currentUT 925 is inside the first scheduled launch's span (launch at 900, span [0,50]).
            bool ok = GhostPlaybackLogic.TryComputeSpanLoopUT(
                925.0, phaseAnchorUT: 900.0, spanStartUT: 0.0, spanEndUT: 50.0, cadenceSeconds: 50.0,
                out _, out _, out _, schedule, cuts);
            // The schedule branch still resolves (it bypasses, it does not fail).
            Assert.True(ok);

            Assert.Contains(logLines, l =>
                l.Contains("[MissionPeriodicity]")
                && l.Contains("INV-3 contract violation")
                && l.Contains("loiterCuts=1"));
        }

        [Fact]
        public void SpanClock_Scheduled_WithArrivalHold_EmitsMutexViolationWarning()
        {
            // Same INV-3 guard, via the arrivalHoldSeconds > 0 disjunct of the predicate.
            var schedule = SyntheticSchedule();

            bool ok = GhostPlaybackLogic.TryComputeSpanLoopUT(
                925.0, 900.0, 0.0, 50.0, 50.0, out _, out _, out _,
                schedule, loiterCuts: null, arrivalHoldSeconds: 30.0);
            Assert.True(ok);

            Assert.Contains(logLines, l =>
                l.Contains("[MissionPeriodicity]")
                && l.Contains("INV-3 contract violation")
                && l.Contains("arrivalHoldSeconds=30"));
        }

        [Fact]
        public void SpanClock_Scheduled_NoCutsNoHold_DoesNotWarn_ShippedPathIsClean()
        {
            // Guards that the guard is a NO-OP on the shipped path: a scheduled unit with no cuts and no
            // hold (the only way the builder ever produces a scheduled unit) emits NO violation line, so
            // the live build never spams.
            var schedule = SyntheticSchedule();

            bool ok = GhostPlaybackLogic.TryComputeSpanLoopUT(
                925.0, 900.0, 0.0, 50.0, 50.0, out _, out _, out _, schedule);
            Assert.True(ok);

            Assert.DoesNotContain(logLines, l => l.Contains("INV-3 contract violation"));
        }

        // ===================== (b) R3 amber predicate =====================

        [Fact]
        public void ShouldTintTMinusAmber_ScheduledWithinTol_StockMun_ReturnsFalse()
        {
            // Guards the R3 fix: a SCHEDULED, within-tolerance unit (the stock Mun, whose ACTUAL launches
            // are within tolerance by construction even though the FIXED m*P fit is over tolerance) must
            // NOT tint amber. The old predicate (off Solution.WithinTolerance) wrongly ambered it.
            // We drive the predicate with the schedule's REAL flag to tie it to actual data.
            var schedule = StockMunWithinTolSchedule();
            Assert.True(schedule.AllLaunchesWithinTolerance,
                "stock Mun 2-constraint should be within tolerance");

            bool amber = MissionsWindowUI.ShouldTintTMinusAmber(
                isPhaseLockedConstrained: true,
                isScheduled: true,
                scheduleAllLaunchesWithinTolerance: schedule.AllLaunchesWithinTolerance,
                // The fixed m*P fit IS over tolerance for the stock Mun (~993 s residual) - the old bug:
                // tinting off this falsely ambered the faithful unit. Pass false to prove the new
                // predicate ignores it for a scheduled unit.
                fixedFitWithinTolerance: false);
            Assert.False(amber);
        }

        [Fact]
        public void ShouldTintTMinusAmber_ScheduledBoundedBest_ReturnsTrue()
        {
            // Guards that a REAL over-tolerance schedule is NOT hidden: a bounded-best same-parent
            // schedule (no within-tol window in 4096) surfaces AllLaunchesWithinTolerance==false, so the
            // predicate ambers it even though we pass the fixed fit as "within" - the schedule flag, not
            // the fixed fit, drives a scheduled unit.
            var schedule = SameParentBoundedBestSchedule();
            Assert.False(schedule.AllLaunchesWithinTolerance,
                "the 4-constraint same-parent config should fall to bounded-best (over tolerance)");

            bool amber = MissionsWindowUI.ShouldTintTMinusAmber(
                isPhaseLockedConstrained: true,
                isScheduled: true,
                scheduleAllLaunchesWithinTolerance: schedule.AllLaunchesWithinTolerance,
                fixedFitWithinTolerance: true);  // even if the fixed fit claims within-tol, amber must fire
            Assert.True(amber);
        }

        [Fact]
        public void ShouldTintTMinusAmber_NonScheduled_TintsOffFixedFit_Unchanged()
        {
            // Guards no-regression on the non-scheduled path: a fixed-cadence (non-scheduled) unit tints
            // off the fixed m*P fit exactly as before. The schedule flag is irrelevant there.
            Assert.True(MissionsWindowUI.ShouldTintTMinusAmber(
                isPhaseLockedConstrained: true, isScheduled: false,
                scheduleAllLaunchesWithinTolerance: true,   // ignored on the non-scheduled path
                fixedFitWithinTolerance: false));            // over-tolerance fixed fit -> amber
            Assert.False(MissionsWindowUI.ShouldTintTMinusAmber(
                isPhaseLockedConstrained: true, isScheduled: false,
                scheduleAllLaunchesWithinTolerance: false,  // ignored on the non-scheduled path
                fixedFitWithinTolerance: true));             // within-tolerance fixed fit -> green
        }

        [Fact]
        public void ShouldTintTMinusAmber_NotPhaseLocked_NeverTints()
        {
            // Guards the gate: continuous / not-aligned / blank states (not phase-locked + constrained)
            // never tint, regardless of any tolerance flag.
            Assert.False(MissionsWindowUI.ShouldTintTMinusAmber(
                isPhaseLockedConstrained: false, isScheduled: true,
                scheduleAllLaunchesWithinTolerance: false, fixedFitWithinTolerance: false));
            Assert.False(MissionsWindowUI.ShouldTintTMinusAmber(
                isPhaseLockedConstrained: false, isScheduled: false,
                scheduleAllLaunchesWithinTolerance: false, fixedFitWithinTolerance: false));
        }

        [Fact]
        public void ShouldTintTMinusAmber_DriftAmberReason_TintsIndependentOfToleranceFlags()
        {
            // M4a D3: a station-drift reason tints a phase-locked cell even when every tolerance
            // flag is green (alignment is to the LIVE orbit; the amber flags that the recorded
            // approach may seam against the drifted station).
            Assert.True(MissionsWindowUI.ShouldTintTMinusAmber(
                isPhaseLockedConstrained: true, isScheduled: true,
                scheduleAllLaunchesWithinTolerance: true, fixedFitWithinTolerance: true,
                driftAmberReason: "station orbit drifted ~5.6% since recording"));
            // The phase-locked gate still wins: not phase-locked never tints, drift or not.
            Assert.False(MissionsWindowUI.ShouldTintTMinusAmber(
                isPhaseLockedConstrained: false, isScheduled: false,
                scheduleAllLaunchesWithinTolerance: true, fixedFitWithinTolerance: true,
                driftAmberReason: "station orbit drifted ~5.6% since recording"));
            // And a null reason leaves the existing tolerance-flag behavior untouched.
            Assert.False(MissionsWindowUI.ShouldTintTMinusAmber(
                isPhaseLockedConstrained: true, isScheduled: false,
                scheduleAllLaunchesWithinTolerance: true, fixedFitWithinTolerance: true,
                driftAmberReason: null));
        }

        [Fact]
        public void ShouldTintTMinusAmber_ArrivalAmber_TintsEvenWhenNotPhaseLocked()
        {
            // M4c (plan test 16a): the D8 arrival amber rides a BUILT RE-AIM unit, and re-aim
            // missions are never phase-locked - so a non-null arrival reason bypasses the
            // phase-lock gate entirely. Null keeps the existing gate byte-identical (the
            // not-phase-locked-never-tints contract holds for all pre-M4c inputs).
            Assert.True(MissionsWindowUI.ShouldTintTMinusAmber(
                isPhaseLockedConstrained: false, isScheduled: false,
                scheduleAllLaunchesWithinTolerance: true, fixedFitWithinTolerance: true,
                arrivalAmberReason: "landing rotation + station rendezvous at 'Duna': " +
                    "joint arrival alignment misses tolerance - the station/rotation lattice " +
                    "needs 4658 whole station periods to reach the 45s rotation tolerance " +
                    "(budget 64); faithful"));
            Assert.False(MissionsWindowUI.ShouldTintTMinusAmber(
                isPhaseLockedConstrained: false, isScheduled: false,
                scheduleAllLaunchesWithinTolerance: true, fixedFitWithinTolerance: true,
                arrivalAmberReason: null));
        }

        [Fact]
        public void ShouldTintTMinusAmber_DriftOnReaimUnit_Tints()
        {
            // M4c (plan test 16b): M4c makes DriftAmberReason reachable on re-aim missions for
            // the first time (a drifted cross-parent depot emits, then drift-compares).
            // isReaimUnit lets the drift reason tint there; without it (neither phase-locked nor
            // re-aim) the drift reason still never tints - the pre-M4c behavior.
            Assert.True(MissionsWindowUI.ShouldTintTMinusAmber(
                isPhaseLockedConstrained: false, isScheduled: false,
                scheduleAllLaunchesWithinTolerance: true, fixedFitWithinTolerance: true,
                driftAmberReason: "station orbit drifted ~5.6% since recording",
                isReaimUnit: true));
            Assert.False(MissionsWindowUI.ShouldTintTMinusAmber(
                isPhaseLockedConstrained: false, isScheduled: false,
                scheduleAllLaunchesWithinTolerance: true, fixedFitWithinTolerance: true,
                driftAmberReason: "station orbit drifted ~5.6% since recording",
                isReaimUnit: false));
        }

        [Fact]
        public void JoinAmberReasons_AllCombinations()
        {
            // The T- tooltip composer: both -> joined with "; ", one -> alone, neither -> null.
            Assert.Equal("drift; arrival", MissionsWindowUI.JoinAmberReasons("drift", "arrival"));
            Assert.Equal("drift", MissionsWindowUI.JoinAmberReasons("drift", null));
            Assert.Equal("arrival", MissionsWindowUI.JoinAmberReasons(null, "arrival"));
            Assert.Null(MissionsWindowUI.JoinAmberReasons(null, null));
            Assert.Null(MissionsWindowUI.JoinAmberReasons("", ""));
        }

        // ===================== (c) R2 branch (ii): bounded-best same-parent fixture =====================

        [Fact]
        public void R2_SameParentBoundedBest_ResolvesMonotonicallyIncreasing_WithAmberFlagSurfaced()
        {
            // R2 branch (ii) (docs/dev/plans/zero-drift-reschedule-hardening.md section 6 R2): a realistic
            // SAME-PARENT config CAN reach bounded-best within ScheduleLookaheadMultiples using only
            // physics-derived tolerances (the 4-constraint pad + Mun orbit/SOI + Mun-surface 0.25 deg +
            // Minmus orbit/SOI case - no k in [1,4096] satisfies all three other-constraints at once).
            // The schedule must STILL resolve monotonically increasing launches (never throw, never go
            // backward) and surface AllLaunchesWithinTolerance==false (the R3 amber flag), with a non-zero
            // worst residual. This is what relaxes INV-1's residual oracle for the bounded-best fixture:
            // INV-1's "<= m=9 fixed residual" bound is scoped to the WITHIN-TOL set; a bounded-best config
            // is allowed to exceed it as long as it stays monotonic and surfaces the amber flag.
            var schedule = SameParentBoundedBestSchedule();

            Assert.False(double.IsNaN(schedule.FirstLaunchUT));
            // Bounded-best: not within tolerance, and the worst residual exceeds the pad tolerance.
            Assert.False(schedule.AllLaunchesWithinTolerance);
            Assert.True(schedule.WorstResidualSeconds > PadRotTolerance,
                "a bounded-best launch should miss tolerance by more than the pad band, worst residual was "
                + schedule.WorstResidualSeconds);

            // Resolve a forward sweep through the schedule object and assert STRICTLY INCREASING launches
            // and a stable, non-decreasing cycle index (never goes backward across the bounded-best path).
            double prevLaunch = double.NegativeInfinity;
            long prevCycle = -1;
            double after = schedule.FirstLaunchUT - 1.0;  // start just before L_0
            const int n = 16;
            for (int i = 0; i < n; i++)
            {
                double next = schedule.NextLaunchAfter(after);
                Assert.False(double.IsNaN(next), "a realistic bounded-best schedule must keep resolving future launches");
                Assert.True(next > after, "NextLaunchAfter must be strictly after the probe");

                Assert.True(schedule.TryResolveActiveLaunch(next, out double active, out long cycle));
                Assert.Equal(next, active, 3);            // resolving exactly on a launch returns that launch
                Assert.True(active > prevLaunch, "launches must be strictly increasing");
                Assert.True(cycle > prevCycle, "the schedule index must increase monotonically");

                prevLaunch = active;
                prevCycle = cycle;
                after = next;
            }

            // The flag never flips back to true as the cache grows (monotonically pessimistic).
            Assert.False(schedule.AllLaunchesWithinTolerance);
        }

        [Fact]
        public void R2_StockMunWithinTol_StaysWithinTolerance_OracleScopeHolds()
        {
            // The complement of the bounded-best fixture (INV-1's proven within-tol set): the stock Mun
            // 2-constraint config DOES reach within-tol windows, so AllLaunchesWithinTolerance stays true
            // and the worst residual stays within the Mun SOI tolerance. This is the set INV-1's residual
            // oracle is scoped to; the bounded-best fixture above is explicitly OUT of that scope.
            var schedule = StockMunWithinTolSchedule();

            Assert.False(double.IsNaN(schedule.FirstLaunchUT));
            Assert.True(schedule.AllLaunchesWithinTolerance);
            Assert.True(schedule.WorstResidualSeconds <= MunSoiTolerance + 1e-6,
                "every stock-Mun launch should be within the Mun SOI tolerance, worst residual was "
                + schedule.WorstResidualSeconds);

            // And a forward sweep still resolves strictly increasing launches.
            double prevLaunch = double.NegativeInfinity;
            double after = schedule.FirstLaunchUT - 1.0;
            for (int i = 0; i < 8; i++)
            {
                double next = schedule.NextLaunchAfter(after);
                Assert.False(double.IsNaN(next));
                Assert.True(next > prevLaunch);
                prevLaunch = next;
                after = next;
            }
        }
    }
}
