using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    // Phase A of the zero-drift per-window reschedule (docs/dev/plans/zero-drift-reschedule.md):
    // the PURE solver - NextJointNearCoincidenceUT / TryFindNextScheduleK / TryBuildRelaunchSchedule
    // / MissionRelaunchSchedule. No engine wiring is exercised here. Each test states the regression
    // it guards. The synthetic dominant=100 / dropped=31 / tol=2 case is fully hand-checkable
    // (7k mod 31); the stock Kerbin-rotation + Mun-orbit case guards the realistic magnitudes +
    // non-accumulation.
    [Collection("Sequential")]
    public class MissionZeroDriftScheduleTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MissionZeroDriftScheduleTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            MissionPeriodicity.SuppressLogging = false;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            MissionPeriodicity.SuppressLogging = false;
        }

        // Stock-like values (Kerbin sidereal day, Mun orbit).
        private const double KerbinRotation = 21549.425;
        private const double MunOrbit = 138984.38;

        private sealed class FakeBodyInfo : IBodyInfo
        {
            public double RotationPeriod(string b) => double.NaN;
            public double OrbitPeriod(string b) => double.NaN;
            public string ReferenceBodyName(string b) => null;
            public double SoiRadius(string b) => double.NaN;
            public double OrbitalVelocity(string b) => double.NaN;
        }

        private static PhaseConstraint Rotation(string body, double period, double offset = 0.0)
            => new PhaseConstraint { Kind = ConstraintKind.Rotation, BodyName = body,
                PeriodSeconds = period, PhaseOffsetSeconds = offset, RelativeToParent = false };

        private static PhaseConstraint Orbital(string body, double period, double offset = 0.0,
            bool crossParent = false)
            => new PhaseConstraint { Kind = ConstraintKind.Orbital, BodyName = body,
                PeriodSeconds = period, PhaseOffsetSeconds = offset, RelativeToParent = crossParent };

        // ===================== NextJointNearCoincidenceUT: synthetic, hand-checkable =====================

        [Fact]
        public void NextJoint_Synthetic_ChainsTheWithinTolWindows_NonUniformNonAccumulating()
        {
            // Guards the core rule: dominant=100 locked exactly, dropped period=31, tol=2. The
            // within-tol k (residual <= 2) are 9, 13, 18, 22 (verify 7k mod 31 by hand). Chaining
            // NextJointNearCoincidenceUT from each launch must yield UT 900, 1300, 1800, 2200 -
            // NON-UNIFORM intervals 400, 500, 400 - and each residual stays <= tol (non-accumulating).
            double[] dropped = { 31.0 };
            double[] tol = { 2.0 };

            double l0 = MissionPeriodicity.NextJointNearCoincidenceUT(
                0.0, 0.0, 100.0, dropped, tol, 100, out double r0, out bool w0);
            Assert.Equal(900.0, l0, 6);
            Assert.True(w0);
            Assert.True(r0 <= 2.0);

            double l1 = MissionPeriodicity.NextJointNearCoincidenceUT(
                l0, 0.0, 100.0, dropped, tol, 100, out double r1, out bool w1);
            Assert.Equal(1300.0, l1, 6);
            Assert.True(w1);
            Assert.True(r1 <= 2.0);

            double l2 = MissionPeriodicity.NextJointNearCoincidenceUT(
                l1, 0.0, 100.0, dropped, tol, 100, out double r2, out bool w2);
            Assert.Equal(1800.0, l2, 6);
            Assert.True(w2);

            double l3 = MissionPeriodicity.NextJointNearCoincidenceUT(
                l2, 0.0, 100.0, dropped, tol, 100, out double r3, out bool w3);
            Assert.Equal(2200.0, l3, 6);
            Assert.True(w3);

            // Non-uniform: 400, 500, 400.
            Assert.Equal(400.0, l1 - l0, 6);
            Assert.Equal(500.0, l2 - l1, 6);
            Assert.Equal(400.0, l3 - l2, 6);
        }

        [Fact]
        public void NextJoint_BoundedBestFallback_WhenNoWithinTolInWindow()
        {
            // Guards: with a tolerance tighter than any residual in the look-ahead window, the search
            // returns the bounded-best (min worst-dropped-residual) launch, withinTolerance=false,
            // never throws. From kStart=1 over 8 multiples the residuals are 7,14,10,3,4,11,13,6 ->
            // min at k=4 (residual 3) -> UT 400.
            double[] dropped = { 31.0 };
            double[] tol = { 0.5 };
            double l = MissionPeriodicity.NextJointNearCoincidenceUT(
                0.0, 0.0, 100.0, dropped, tol, 8, out double resid, out bool within);
            Assert.Equal(400.0, l, 6);
            Assert.False(within);
            Assert.Equal(3.0, resid, 6);
        }

        [Fact]
        public void NextJoint_DoesNotReturnTheLaunchAtAfterUT()
        {
            // Guards: afterUT landing exactly on a launch (k=9, UT 900) advances past it (next is
            // k=13, UT 1300), never re-returns the same launch.
            double[] dropped = { 31.0 };
            double[] tol = { 2.0 };
            double next = MissionPeriodicity.NextJointNearCoincidenceUT(
                900.0, 0.0, 100.0, dropped, tol, 100, out _, out _);
            Assert.Equal(1300.0, next, 6);
        }

        [Fact]
        public void NextJoint_DegenerateDominantPeriod_ReturnsNaN()
        {
            // Guards: a zero / NaN / infinite dominant period yields NaN (no schedule), no divide-by-zero.
            double[] dropped = { 31.0 };
            double[] tol = { 2.0 };
            Assert.True(double.IsNaN(MissionPeriodicity.NextJointNearCoincidenceUT(
                0.0, 0.0, 0.0, dropped, tol, 16, out _, out _)));
            Assert.True(double.IsNaN(MissionPeriodicity.NextJointNearCoincidenceUT(
                0.0, 0.0, double.NaN, dropped, tol, 16, out _, out _)));
            Assert.True(double.IsNaN(MissionPeriodicity.NextJointNearCoincidenceUT(
                0.0, double.NaN, 100.0, dropped, tol, 16, out _, out _)));
        }

        // ===================== TryFindNextScheduleK: tolerance boundary =====================

        [Fact]
        public void FindNextK_ToleranceBoundary_FlipsWithinTolerance()
        {
            // Guards the green/amber threshold: searching from k=10, the first candidate with the
            // smallest k that could be within-tol is k=13 (residual exactly 2). tol=2 -> within;
            // tol=1.9 -> not within (falls to bounded-best, still k=13 as the min-residual, residual 2).
            double[] dropped = { 31.0 };

            bool ok1 = MissionPeriodicity.TryFindNextScheduleK(
                100.0, dropped, new double[] { 2.0 }, 10, 10, out long k1, out double r1, out bool w1);
            Assert.True(ok1);
            Assert.Equal(13, k1);
            Assert.True(w1);
            Assert.Equal(2.0, r1, 6);

            bool ok2 = MissionPeriodicity.TryFindNextScheduleK(
                100.0, dropped, new double[] { 1.9 }, 10, 10, out long k2, out double r2, out bool w2);
            Assert.True(ok2);
            Assert.Equal(13, k2);       // bounded-best is still the min-residual k
            Assert.False(w2);            // but not within the tighter tolerance
            Assert.Equal(2.0, r2, 6);
        }

        [Fact]
        public void FindNextK_NonPositiveLookahead_ReturnsFalse()
        {
            Assert.False(MissionPeriodicity.TryFindNextScheduleK(
                100.0, new double[] { 31.0 }, new double[] { 2.0 }, 1, 0, out _, out _, out _));
        }

        // ===================== Stock Mun case: realistic magnitudes + non-accumulation =====================

        [Fact]
        public void MunCase_ZeroDrift_NeverWorseThanFixedCadence_AndDoesNotAccumulate()
        {
            // Guards the actual zero-drift property on the real case: the per-launch dropped residual
            // (Kerbin pad rotation) over the first many relaunches stays bounded and never exceeds the
            // best fixed multiple (m=9 -> ~993 s), whereas a FIXED cadence at m=9 accumulates
            // (~993, ~1986, ~2978, ... s). We compare the two residual streams directly.
            double[] dropped = { KerbinRotation };
            double tolRot = KerbinRotation * (0.25 / 360.0);
            double[] tol = { tolRot };

            // Zero-drift schedule: chain NextJointNearCoincidenceUT for N relaunches from UT0=0.
            const int n = 12;
            double after = 0.0;
            double zeroDriftWorst = 0.0;
            for (int i = 0; i < n; i++)
            {
                double l = MissionPeriodicity.NextJointNearCoincidenceUT(
                    after, 0.0, MunOrbit, dropped, tol, 4096, out double resid, out _);
                Assert.False(double.IsNaN(l));
                Assert.True(l > after);                     // strictly increasing
                if (resid > zeroDriftWorst) zeroDriftWorst = resid;
                after = l;
            }

            // Fixed cadence m=9 (the Phase-2 result): launches at k=9,18,27,... pad residuals grow.
            double fixedWorst = 0.0;
            for (int i = 1; i <= n; i++)
            {
                double resid = MissionPeriodicity.CircularPhaseError(9L * i * MunOrbit, KerbinRotation);
                if (resid > fixedWorst) fixedWorst = resid;
            }

            // Fixed cadence drifts to well over a thousand seconds; zero-drift stays far smaller.
            Assert.True(fixedWorst > 2000.0,
                "fixed-cadence m=9 should accumulate past 2000 s, was " + fixedWorst);
            Assert.True(zeroDriftWorst < fixedWorst,
                "zero-drift worst residual " + zeroDriftWorst + " should be < fixed " + fixedWorst);
            // The first fixed multiple (m=9) residual is ~993 s; zero-drift's best window beats it.
            double fixedM9 = MissionPeriodicity.CircularPhaseError(9.0 * MunOrbit, KerbinRotation);
            Assert.True(zeroDriftWorst <= fixedM9 + 1e-6,
                "zero-drift worst " + zeroDriftWorst + " should be <= the best fixed multiple " + fixedM9);
        }

        // ===================== TryBuildRelaunchSchedule: gating =====================

        [Fact]
        public void BuildSchedule_MultiDistinctPeriod_BuildsSchedule()
        {
            // Guards: a phase-locked multi-constraint config with distinct periods (Mun intercept +
            // Kerbin rotation) DOES get a schedule, anchored at or after the floor.
            var constraints = new List<PhaseConstraint>
            {
                Rotation("Kerbin", KerbinRotation),
                Orbital("Mun", MunOrbit)
            };
            bool ok = MissionPeriodicity.TryBuildRelaunchSchedule(
                constraints, Support.Supported, ut0: 0.0, floorUT: 1000.0, new FakeBodyInfo(),
                out MissionRelaunchSchedule schedule);
            Assert.True(ok);
            Assert.NotNull(schedule);
            Assert.False(double.IsNaN(schedule.FirstLaunchUT));
            Assert.True(schedule.FirstLaunchUT >= 1000.0);   // first-play floor honored
        }

        [Fact]
        public void BuildSchedule_SingleConstraint_NoSchedule()
        {
            // Guards: a single-constraint config does not drift (uniform schedule == fixed cadence),
            // so no schedule is attached.
            var constraints = new List<PhaseConstraint> { Orbital("Mun", MunOrbit) };
            bool ok = MissionPeriodicity.TryBuildRelaunchSchedule(
                constraints, Support.Supported, 0.0, 0.0, new FakeBodyInfo(), out var schedule);
            Assert.False(ok);
            Assert.Null(schedule);
        }

        [Fact]
        public void BuildSchedule_TidalCollapse_NoSchedule()
        {
            // Guards: when all dropped constraints share the dominant period (tidal lock), they line
            // up at every window -> no drift -> no schedule.
            var constraints = new List<PhaseConstraint>
            {
                Rotation("Mun", MunOrbit),    // tidally locked: rotation == orbit period
                Orbital("Mun", MunOrbit)
            };
            bool ok = MissionPeriodicity.TryBuildRelaunchSchedule(
                constraints, Support.Supported, 0.0, 0.0, new FakeBodyInfo(), out var schedule);
            Assert.False(ok);
            Assert.Null(schedule);
        }

        [Fact]
        public void BuildSchedule_Unsupported_NoSchedule()
        {
            // Guards: an unsupported config (cross-parent / rendezvous) never gets a schedule.
            var constraints = new List<PhaseConstraint>
            {
                Rotation("Kerbin", KerbinRotation),
                Orbital("Duna", 17315400.0, crossParent: true)
            };
            bool ok = MissionPeriodicity.TryBuildRelaunchSchedule(
                constraints, Support.UnsupportedCrossParent, 0.0, 0.0, new FakeBodyInfo(),
                out var schedule);
            Assert.False(ok);
            Assert.Null(schedule);
        }

        [Fact]
        public void BuildSchedule_DegenerateDroppedPeriod_FilteredWithWarn()
        {
            // Guards: a NaN dropped period is FILTERED (not read as spuriously satisfied) with a Warn.
            // Here the only dropped constraint is degenerate, so after filtering there is nothing to
            // drift against -> no schedule, and a Warn is emitted.
            var constraints = new List<PhaseConstraint>
            {
                Orbital("Mun", MunOrbit),
                Rotation("Bad", double.NaN)
            };
            bool ok = MissionPeriodicity.TryBuildRelaunchSchedule(
                constraints, Support.Supported, 0.0, 0.0, new FakeBodyInfo(), out var schedule);
            Assert.False(ok);
            Assert.Null(schedule);
            Assert.Contains(logLines, l =>
                l.Contains("[MissionPeriodicity]") && l.Contains("degenerate") && l.Contains("filtered"));
        }

        // ===================== MissionRelaunchSchedule: resolution =====================

        [Fact]
        public void Schedule_ResolveActiveLaunch_ParkedBeforeFirst_ThenResolvesAndExtends()
        {
            // Guards the consumption contract: parked before L_0; the active launch is the largest
            // scheduled launch <= currentUT; lazily extends for a far-future currentUT; monotonic.
            // Synthetic case (dominant=100, dropped=31, tol=2): launches at UT 900,1300,1800,2200,...
            var schedule = new MissionRelaunchSchedule(
                0.0, 100.0, new double[] { 31.0 }, new double[] { 2.0 }, floorUT: 0.0,
                lookaheadMultiples: 100);
            Assert.Equal(900.0, schedule.FirstLaunchUT, 6);

            // Parked before the first launch.
            Assert.False(schedule.TryResolveActiveLaunch(500.0, out _, out _));

            // Exactly on the first launch.
            Assert.True(schedule.TryResolveActiveLaunch(900.0, out double a0, out long c0));
            Assert.Equal(900.0, a0, 6);
            Assert.Equal(0, c0);

            // Between L_1 and L_2 -> active is L_1 (1300).
            Assert.True(schedule.TryResolveActiveLaunch(1500.0, out double a1, out long c1));
            Assert.Equal(1300.0, a1, 6);
            Assert.Equal(1, c1);

            // Far-future warp: extends the cache and resolves correctly (active is the launch just
            // below currentUT, still increasing).
            Assert.True(schedule.TryResolveActiveLaunch(100000.0, out double aFar, out long cFar));
            Assert.True(aFar <= 100000.0);
            Assert.True(cFar > 1);
        }

        [Fact]
        public void Schedule_NextLaunchAfter_TargetsTheNextScheduledRelaunch()
        {
            // Guards the UI countdown / warp target: the next scheduled launch strictly after now.
            var schedule = new MissionRelaunchSchedule(
                0.0, 100.0, new double[] { 31.0 }, new double[] { 2.0 }, 0.0, 100);
            Assert.Equal(900.0, schedule.NextLaunchAfter(0.0), 6);     // parked -> first launch
            Assert.Equal(1300.0, schedule.NextLaunchAfter(900.0), 6);  // strictly after L_0
            Assert.Equal(1800.0, schedule.NextLaunchAfter(1500.0), 6); // next after a between-time
        }

        [Fact]
        public void Schedule_MinIntervalSeconds_ReflectsTheShortestProbeInterval()
        {
            // Guards the overlap gate input: min interval over the eager prefix. Synthetic intervals
            // are 400,500,400,... so the min is 400.
            var schedule = new MissionRelaunchSchedule(
                0.0, 100.0, new double[] { 31.0 }, new double[] { 2.0 }, 0.0, 100);
            Assert.Equal(400.0, schedule.MinIntervalSeconds, 6);
        }

        [Fact]
        public void Schedule_FutureDatedUT0_FirstLaunchAtOrAfterFloor()
        {
            // Guards future-dated recordings (UT0 > live, e.g. after a career rewind): L_0 is the
            // first faithful window at or after the floor, never before it.
            var schedule = new MissionRelaunchSchedule(
                ut0: 1_000_000.0, anchorPeriod: 100.0,
                otherPeriods: new double[] { 31.0 }, otherTolerances: new double[] { 2.0 },
                floorUT: 1_005_000.0, lookaheadMultiples: 200);
            Assert.False(double.IsNaN(schedule.FirstLaunchUT));
            Assert.True(schedule.FirstLaunchUT >= 1_005_000.0);
        }

        [Fact]
        public void Schedule_DegenerateInputs_NoFirstLaunch()
        {
            // Guards: a degenerate dominant period yields no first launch (NaN), so the builder rejects.
            var schedule = new MissionRelaunchSchedule(
                0.0, 0.0, new double[] { 31.0 }, new double[] { 2.0 }, 0.0, 100);
            Assert.True(double.IsNaN(schedule.FirstLaunchUT));
        }

        [Fact]
        public void Schedule_FloorUT_NaN_NoFirstLaunch()
        {
            // Guards the floorUT=NaN early-return branch.
            var schedule = new MissionRelaunchSchedule(
                0.0, 100.0, new double[] { 31.0 }, new double[] { 2.0 }, double.NaN, 100);
            Assert.True(double.IsNaN(schedule.FirstLaunchUT));
        }

        // ===================== Multiple dropped constraints (the headline multi-constraint path) ===

        [Fact]
        public void FindNextK_MultipleDistinctDroppedPeriods_WorstIsMax_AllWithinTol()
        {
            // Guards the multi-dropped path (worst = max over j; the per-constraint j<Count tolerance
            // check). dominant=100, dropped periods 31 and 23, tol 3 each. The chosen k must have BOTH
            // CircularPhaseError(100k,31) and CircularPhaseError(100k,23) within tol, the reported
            // residual is the MAX of the two, and it is the SMALLEST such k (cross-checked by brute force).
            double[] dropped = { 31.0, 23.0 };
            double[] tol = { 3.0, 3.0 };

            bool ok = MissionPeriodicity.TryFindNextScheduleK(
                100.0, dropped, tol, 1, 1000, out long k, out double resid, out bool within);
            Assert.True(ok);
            Assert.True(within);

            double e31 = MissionPeriodicity.CircularPhaseError(k * 100.0, 31.0);
            double e23 = MissionPeriodicity.CircularPhaseError(k * 100.0, 23.0);
            Assert.True(e31 <= 3.0);
            Assert.True(e23 <= 3.0);
            Assert.Equal(Math.Max(e31, e23), resid, 6);

            // Brute-force the smallest k>=1 with both within tol; must match.
            long expected = -1;
            for (long kk = 1; kk <= 1000 && expected < 0; kk++)
                if (MissionPeriodicity.CircularPhaseError(kk * 100.0, 31.0) <= 3.0
                    && MissionPeriodicity.CircularPhaseError(kk * 100.0, 23.0) <= 3.0)
                    expected = kk;
            Assert.Equal(expected, k);
        }

        [Fact]
        public void FindNextK_FewerTolerancesThanPeriods_MissingToleranceTreatedAsZero()
        {
            // Guards the j<droppedTolerances.Count fallback: a missing tolerance is treated as 0
            // (never within-tol unless an exact alignment happens). With a small look-ahead the
            // second period (tol 0) is never exactly aligned, so the search falls to bounded-best.
            double[] dropped = { 31.0, 23.0 };
            double[] tol = { 100.0 };  // tolerance only for the first; second is missing -> 0
            bool ok = MissionPeriodicity.TryFindNextScheduleK(
                100.0, dropped, tol, 1, 10, out _, out _, out bool within);
            Assert.True(ok);
            Assert.False(within); // the second constraint (tol 0) is not aligned in [1,10] -> bounded-best
        }

        // ===================== Safety cap + monotonicity =====================

        [Fact]
        public void Schedule_SafetyCap_ParksWithoutCrash_AndNextLaunchAfterIsNaN()
        {
            // Guards the MaxScheduleSteps CPU safety valve: a pathological tiny dominant period with a
            // tolerance nothing meets produces densely-spaced bounded-best launches, so a far-future
            // currentUT cannot be reached within the cap. The resolver must park (no crash, no infinite
            // loop), emit a Warn, and NextLaunchAfter must return NaN (not a past target).
            var schedule = new MissionRelaunchSchedule(
                0.0, 0.001, new double[] { 0.0007 }, new double[] { 1e-12 }, 0.0, 8);
            Assert.False(double.IsNaN(schedule.FirstLaunchUT));

            // currentUT astronomically beyond what 8192 launches at ~0.001 spacing can reach.
            Assert.True(schedule.TryResolveActiveLaunch(1e9, out double launchUT, out _));
            Assert.True(launchUT <= 1e9);
            Assert.False(double.IsNaN(launchUT)); // parked at the last cached launch

            Assert.True(double.IsNaN(schedule.NextLaunchAfter(1e9)));
            Assert.Contains(logLines, l =>
                l.Contains("[MissionPeriodicity]") && l.Contains("MaxScheduleSteps"));
        }

        [Fact]
        public void Schedule_RepeatedAndDecreasingResolves_StayConsistent()
        {
            // Guards monotonic, stable resolution: after a far-future resolve grows the cache, resolving
            // an earlier UT still returns the correct (earlier) active launch, and re-resolving the same
            // UT is idempotent (the cache only grows, never shrinks).
            var schedule = new MissionRelaunchSchedule(
                0.0, 100.0, new double[] { 31.0 }, new double[] { 2.0 }, 0.0, 100);

            Assert.True(schedule.TryResolveActiveLaunch(50000.0, out double aFar, out long cFar));
            // Now resolve an earlier time.
            Assert.True(schedule.TryResolveActiveLaunch(1500.0, out double aMid, out long cMid));
            Assert.Equal(1300.0, aMid, 6);
            Assert.Equal(1, cMid);
            Assert.True(aMid < aFar && cMid < cFar);
            // Idempotent: re-resolve the same earlier time.
            Assert.True(schedule.TryResolveActiveLaunch(1500.0, out double aMid2, out long cMid2));
            Assert.Equal(aMid, aMid2, 6);
            Assert.Equal(cMid, cMid2);
        }

        // ===================== Span-clock consumption (Phase B) =====================

        // A schedule with launches at UT 900, 1300, 1800, ... (synthetic 100/31 case), used to drive
        // the span clock. The span is 50 s, far shorter than the ~400 s relaunch interval, so the
        // clock parks (inter-cycle tail) between launches.
        private static MissionRelaunchSchedule SyntheticSchedule()
            => new MissionRelaunchSchedule(
                0.0, 100.0, new double[] { 31.0 }, new double[] { 2.0 }, floorUT: 0.0,
                lookaheadMultiples: 100);

        [Fact]
        public void SpanClock_Scheduled_ParkedBeforeFirstLaunch()
        {
            var schedule = SyntheticSchedule();
            bool ok = GhostPlaybackLogic.TryComputeSpanLoopUT(
                500.0, phaseAnchorUT: 900.0, spanStartUT: 0.0, spanEndUT: 50.0, cadenceSeconds: 50.0,
                out _, out _, out _, schedule);
            Assert.False(ok); // before the first scheduled launch -> unresolved (render nothing)
        }

        [Fact]
        public void SpanClock_Scheduled_RendersDuringSpan_ThenParksInTailBetweenLaunches()
        {
            var schedule = SyntheticSchedule();

            // On the first launch (UT 900): phase 0 -> loopUT = spanStart, cycle 0, not tail.
            Assert.True(GhostPlaybackLogic.TryComputeSpanLoopUT(
                900.0, 900.0, 0.0, 50.0, 50.0, out double l0, out long c0, out bool tail0, schedule));
            Assert.Equal(0.0, l0, 6);
            Assert.Equal(0, c0);
            Assert.False(tail0);

            // 25 s into the first launch's span: loopUT = spanStart + 25, still rendering.
            Assert.True(GhostPlaybackLogic.TryComputeSpanLoopUT(
                925.0, 900.0, 0.0, 50.0, 50.0, out double lMid, out _, out bool tailMid, schedule));
            Assert.Equal(25.0, lMid, 6);
            Assert.False(tailMid);

            // 70 s in (past the 50 s span): parked at spanEnd, inter-cycle tail (render nothing).
            Assert.True(GhostPlaybackLogic.TryComputeSpanLoopUT(
                970.0, 900.0, 0.0, 50.0, 50.0, out double lTail, out _, out bool tailGap, schedule));
            Assert.Equal(50.0, lTail, 6);
            Assert.True(tailGap);

            // On the second scheduled launch (UT 1300): phase 0 again, cycle 1.
            Assert.True(GhostPlaybackLogic.TryComputeSpanLoopUT(
                1300.0, 900.0, 0.0, 50.0, 50.0, out double l1, out long c1, out bool tail1, schedule));
            Assert.Equal(0.0, l1, 6);
            Assert.Equal(1, c1);
            Assert.False(tail1);
        }

        [Fact]
        public void DecideUnitMemberRender_Scheduled_RenderInSpan_HiddenInTail_UnresolvedBeforeFirst()
        {
            var schedule = SyntheticSchedule();

            // Member window covers the whole span [0,50].
            var inSpan = GhostPlaybackLogic.DecideUnitMemberRender(
                925.0, 900.0, 0.0, 50.0, 50.0, 0.0, 50.0, out _, out _, out _, schedule);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.Render, inSpan);

            var inGap = GhostPlaybackLogic.DecideUnitMemberRender(
                970.0, 900.0, 0.0, 50.0, 50.0, 0.0, 50.0, out _, out _, out _, schedule);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.HiddenInterCycleTail, inGap);

            var beforeFirst = GhostPlaybackLogic.DecideUnitMemberRender(
                500.0, 900.0, 0.0, 50.0, 50.0, 0.0, 50.0, out _, out _, out _, schedule);
            Assert.Equal(GhostPlaybackLogic.UnitMemberRenderDecision.SpanClockUnresolved, beforeFirst);
        }

        [Fact]
        public void SpanClock_NullSchedule_ByteIdenticalToUniformPath()
        {
            // Guards no-regression: with a null schedule the uniform path is unchanged. anchor=1000,
            // span [0,100], cadence=100. At currentUT=1025 -> cycle 0, loopUT = 0 + 25 = 25.
            Assert.True(GhostPlaybackLogic.TryComputeSpanLoopUT(
                1025.0, 1000.0, 0.0, 100.0, 100.0, out double loopUT, out long cycle, out bool tail));
            Assert.Equal(25.0, loopUT, 6);
            Assert.Equal(0, cycle);
            Assert.False(tail);
            // Before the anchor -> unresolved (the uniform parked-before-anchor behavior).
            Assert.False(GhostPlaybackLogic.TryComputeSpanLoopUT(
                900.0, 1000.0, 0.0, 100.0, 100.0, out _, out _, out _));
            // Degenerate span -> unresolved (guards the reordered span<=0 check: both after-anchor and
            // before-anchor cases must still return false when span<=0, null schedule).
            Assert.False(GhostPlaybackLogic.TryComputeSpanLoopUT(
                1025.0, 1000.0, 0.0, 0.0, 100.0, out _, out _, out _));
            Assert.False(GhostPlaybackLogic.TryComputeSpanLoopUT(
                900.0, 1000.0, 0.0, 0.0, 100.0, out _, out _, out _));
        }

        [Fact]
        public void Schedule_Deterministic_TwoIndependentBuildsResolveIdentically()
        {
            // Guards the determinism the engine/UI separation relies on: two schedules built from the
            // SAME snapshotted inputs (e.g. the engine's LoopUnitSet and the UI's display mirror, or a
            // schedule re-derived after a save/reload) produce IDENTICAL launches, so the displayed
            // countdown can never drift from when the engine actually relaunches.
            var a = new MissionRelaunchSchedule(
                0.0, MunOrbit, new double[] { KerbinRotation },
                new double[] { KerbinRotation * (0.25 / 360.0) }, floorUT: 5000.0, lookaheadMultiples: 4096);
            var b = new MissionRelaunchSchedule(
                0.0, MunOrbit, new double[] { KerbinRotation },
                new double[] { KerbinRotation * (0.25 / 360.0) }, floorUT: 5000.0, lookaheadMultiples: 4096);

            Assert.Equal(a.FirstLaunchUT, b.FirstLaunchUT, 6);
            Assert.Equal(a.MinIntervalSeconds, b.MinIntervalSeconds, 6);
            foreach (double probe in new[] { 0.0, a.FirstLaunchUT, a.FirstLaunchUT + MunOrbit, 5e8 })
            {
                bool ra = a.TryResolveActiveLaunch(probe, out double la, out long ca);
                bool rb = b.TryResolveActiveLaunch(probe, out double lb, out long cb);
                Assert.Equal(ra, rb);
                if (ra)
                {
                    Assert.Equal(la, lb, 6);
                    Assert.Equal(ca, cb);
                }
                Assert.Equal(a.NextLaunchAfter(probe), b.NextLaunchAfter(probe), 6);
            }
        }

        // A fake that supplies the Mun's SOI radius + orbital velocity, so an Orbital constraint's
        // tolerance (SoiRadius/OrbitalVelocity) is the generous SOI-width value (~4475 s for the Mun),
        // far looser than the rotation tolerance (a fraction of a degree).
        private sealed class SoiFake : IBodyInfo
        {
            public double RotationPeriod(string b) => double.NaN;
            public double OrbitPeriod(string b) => double.NaN;
            public string ReferenceBodyName(string b) => null;
            public double SoiRadius(string b) => b == "Mun" ? 2429559.0 : double.NaN;
            public double OrbitalVelocity(string b) => b == "Mun" ? 543.0 : double.NaN;
        }

        // ===================== Anchor = tightest band (max cadence) =====================

        [Fact]
        public void SelectAnchorConstraintIndex_PicksTightestBand_ThePadNotTheMun()
        {
            // Guards the max-cadence rule: the schedule anchors on the constraint with the SMALLEST duty
            // cycle (tolerance/period). The launch-pad rotation (tol ~0.25 deg) is far tighter than the
            // Mun intercept (tol ~ one SOI width), so the anchor is the ROTATION, not the longest-period
            // Mun. (The OLD wrong choice - longest period = the Mun - made windows ~3.5 years apart.)
            var constraints = new List<PhaseConstraint>
            {
                Orbital("Mun", MunOrbit),        // index 0: generous SOI tolerance
                Rotation("Kerbin", KerbinRotation) // index 1: tight rotation tolerance
            };
            int anchor = MissionPeriodicity.SelectAnchorConstraintIndex(constraints, new SoiFake());
            Assert.Equal(1, anchor); // the Kerbin rotation (tightest band), NOT the Mun
        }

        // ===================== Player throttle (launch less often than the max cadence) =====================

        [Fact]
        public void Schedule_NoThrottle_LaunchesEveryFaithfulWindow_MaxCadence()
        {
            // Guards: minSpacing=0 (default / Auto) launches at EVERY faithful window - the maximum
            // attainable cadence. Synthetic 100/31 faithful windows: 900, 1300, 1800, 2200, ...
            var schedule = new MissionRelaunchSchedule(
                0.0, 100.0, new double[] { 31.0 }, new double[] { 2.0 }, floorUT: 0.0,
                lookaheadMultiples: 100, minSpacingSeconds: 0.0);
            Assert.Equal(900.0, schedule.FirstLaunchUT, 6);
            Assert.Equal(1300.0, schedule.NextLaunchAfter(900.0), 6); // the very next faithful window
            Assert.Equal(1800.0, schedule.NextLaunchAfter(1300.0), 6);
        }

        [Fact]
        public void Schedule_PlayerThrottle_SkipsFaithfulWindowsToHonorPeriod()
        {
            // Guards the throttle: the player picks a LOWER cadence than the max, so the schedule skips
            // faithful windows. minSpacing=700: each relaunch is >= 700 past the prior, snapped to the
            // next faithful window. Faithful windows: 900,1300,1800,2200,3100,...; with the throttle the
            // schedule launches 900 -> 1800 (>= 900+700, skips 1300) -> 3100 (>= 1800+700, skips 2200).
            var schedule = new MissionRelaunchSchedule(
                0.0, 100.0, new double[] { 31.0 }, new double[] { 2.0 }, floorUT: 0.0,
                lookaheadMultiples: 100, minSpacingSeconds: 700.0);
            Assert.Equal(900.0, schedule.FirstLaunchUT, 6);             // L_0 unaffected by the throttle
            Assert.Equal(1800.0, schedule.NextLaunchAfter(900.0), 6);    // skipped 1300 (only 400 past 900)
            Assert.Equal(3100.0, schedule.NextLaunchAfter(1800.0), 6);   // skipped 2200 (only 400 past 1800)
            // Every scheduled gap honors the throttle.
            Assert.True(1800.0 - 900.0 >= 700.0);
            Assert.True(3100.0 - 1800.0 >= 700.0);
        }

        [Fact]
        public void Schedule_BoundedBestOnly_SkipsEagerProbe_PreservesOverlapInvariant()
        {
            // Guards the perf guard + overlap invariant for a config whose joint window is BEYOND the
            // look-ahead (no within-tolerance launch in range): period 99991 with tol 1 has a duty so
            // small no within-tol k falls inside lookahead=200, so the first launch is BOUNDED-BEST.
            // The schedule still attaches one launch (never refused), the eager 8-probe is skipped, and
            // MinIntervalSeconds reports the span-floored throttle max(anchorPeriod, minSpacing) so the
            // builder's overlap gate (MinIntervalSeconds >= span) passes exactly as before the guard.
            const double minSpacing = 5000.0;
            var schedule = new MissionRelaunchSchedule(
                0.0, 100.0, new double[] { 99991.0 }, new double[] { 1.0 }, floorUT: 0.0,
                lookaheadMultiples: 200, minSpacingSeconds: minSpacing);
            Assert.False(double.IsNaN(schedule.FirstLaunchUT));         // bounded-best L_0 attached
            Assert.Equal(Math.Max(100.0, minSpacing), schedule.MinIntervalSeconds, 6);
            Assert.Equal(schedule.MinIntervalSeconds, schedule.AverageIntervalSeconds, 6);
            // Lazy extension still resolves (never NaN / throws) for a bounded-best schedule.
            double next = schedule.NextLaunchAfter(schedule.FirstLaunchUT);
            Assert.True(next > schedule.FirstLaunchUT);
        }

        [Fact]
        public void LoopUnit_WithSchedule_IsNonOverlapping_Invariant()
        {
            // Guards the INVARIANT: a unit carrying a schedule is built non-overlapping
            // (OverlapCadenceSeconds >= span), so UnitMemberOverlaps is false and the overlap engine
            // path never sees a scheduled unit.
            var schedule = SyntheticSchedule();
            double span = 50.0;
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 }, spanStartUT: 0.0, spanEndUT: span,
                cadenceSeconds: Math.Max(span, schedule.MinIntervalSeconds),
                phaseAnchorUT: schedule.FirstLaunchUT,
                overlapCadenceSeconds: Math.Max(span, schedule.MinIntervalSeconds),
                memberWindows: null, relaunchSchedule: schedule);
            Assert.NotNull(unit.RelaunchSchedule);
            Assert.False(GhostPlaybackLogic.UnitMemberOverlaps(unit));
        }

        // ===================== Transited-body rotation A/B mode (Drop / Loose / Tight) =====================

        [Fact]
        public void IsTransitedBodyRotation_OnlyNonLaunchBodyRotation()
        {
            // Guards which constraints the A/B mode governs: a Rotation of a body OTHER than the launch
            // body (a Mun landing), and ONLY that.
            Assert.True(MissionPeriodicity.IsTransitedBodyRotation(Rotation("Mun", MunOrbit), "Kerbin"));
            Assert.False(MissionPeriodicity.IsTransitedBodyRotation(Rotation("Kerbin", KerbinRotation), "Kerbin")); // the pad
            Assert.False(MissionPeriodicity.IsTransitedBodyRotation(Orbital("Mun", MunOrbit), "Kerbin"));            // not a rotation
            Assert.False(MissionPeriodicity.IsTransitedBodyRotation(Rotation("Mun", MunOrbit), null));               // no launch body
        }

        [Fact]
        public void ScheduleToleranceSecondsFor_LoosensOnlyTransitedBodyRotation_InLooseMode()
        {
            var fake = new SoiFake();
            var munRot = Rotation("Mun", MunOrbit);
            double tight = MissionPeriodicity.ScheduleToleranceSecondsFor(munRot, fake, "Kerbin", TransitedBodyRotationMode.Tight);
            double loose = MissionPeriodicity.ScheduleToleranceSecondsFor(munRot, fake, "Kerbin", TransitedBodyRotationMode.Loose);
            Assert.Equal(MunOrbit * (0.25 / 360.0), tight, 3);  // Tight = the normal 0.25 deg rotation tolerance
            Assert.Equal(MunOrbit * (5.0 / 360.0), loose, 3);   // Loose = 5 deg (TransitedBodyLooseRotationDegrees)
            Assert.True(loose > tight);
            // The launch-body (pad) rotation is NEVER loosened, even in Loose mode.
            var pad = Rotation("Kerbin", KerbinRotation);
            double padLoose = MissionPeriodicity.ScheduleToleranceSecondsFor(pad, fake, "Kerbin", TransitedBodyRotationMode.Loose);
            Assert.Equal(KerbinRotation * (0.25 / 360.0), padLoose, 3);
        }

        [Fact]
        public void TryBuildRelaunchSchedule_TransitedBodyRotationMode_TighterIsRarer_DropLtLooseLtTight()
        {
            // A land-and-return Mun config: pad rotation + Mun intercept + Mun LANDING rotation (tidally
            // locked, so its period == the Mun orbit). The mode sets the Mun-rotation tolerance: Drop
            // excludes it (only the Mun SOI tolerance, ~4474s, governs); Loose uses ~5 deg (~1930s);
            // Tight uses 0.25 deg (~96s). A tighter tolerance is a SUBSET of a looser one, so the FIRST
            // faithful window is strictly LATER (rarer) as the tolerance tightens. We compare the first
            // launch UT (a robust cadence proxy; the min-interval probe can coincide across modes). All
            // anchor on the pad (Kerbin).
            var constraints = new List<PhaseConstraint>
            {
                Rotation("Kerbin", KerbinRotation),
                Rotation("Mun", MunOrbit),   // tidally locked: rotation period == orbit period
                Orbital("Mun", MunOrbit)
            };
            var fake = new SoiFake();

            double FirstLaunch(TransitedBodyRotationMode mode)
            {
                bool ok = MissionPeriodicity.TryBuildRelaunchSchedule(
                    constraints, Support.Supported, ut0: 0.0, floorUT: 0.0, fake, out var sched,
                    minSpacingSeconds: 0.0, launchBodyName: "Kerbin", mode: mode);
                Assert.True(ok, $"schedule should build for mode {mode}");
                Assert.False(double.IsNaN(sched.FirstLaunchUT));
                return sched.FirstLaunchUT;
            }

            double fDrop = FirstLaunch(TransitedBodyRotationMode.Drop);
            double fLoose = FirstLaunch(TransitedBodyRotationMode.Loose);
            double fTight = FirstLaunch(TransitedBodyRotationMode.Tight);

            Assert.True(fDrop < fLoose, $"Drop first window {fDrop} should be < Loose {fLoose}");
            Assert.True(fLoose < fTight, $"Loose first window {fLoose} should be < Tight {fTight}");
        }
    }
}
