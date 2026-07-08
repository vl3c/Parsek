using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Parsek.Reaim;

namespace Parsek.Tests
{
    // Re-aim Phase 4 (cross-parent destination-SOI arrival-UT alignment), implementation Phase 1:
    // the PURE arrival-window solver (DestinationArrivalSolver.SolveArrivalWindow /
    // CountEffectiveConstraints). No engine / resolver / loiter / render wiring is exercised here.
    //
    // The arrival window grid is SYNODIC-spaced; the solver wraps MissionPeriodicity.TryFindNextScheduleK
    // with the synodic spacing as the anchor period and the destination constraints as the "others"
    // sampled at k*synodic, so the residual at window k is max_j CircularPhaseError(k*synodic, period_j) -
    // an absolute function of k. Each test states the requirement it pins and recomputes the residual via
    // the public CircularPhaseError so it does not depend on a hand-copied magic window index.
    [Collection("Sequential")]
    public class DestinationArrivalSolverTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public DestinationArrivalSolverTests()
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

        // === fixtures =========================================================================

        private const string Launch = "Kerbin";

        private static PhaseConstraint Rotation(string body, double period)
            => new PhaseConstraint { Kind = ConstraintKind.Rotation, BodyName = body,
                PeriodSeconds = period, PhaseOffsetSeconds = 0.0, RelativeToParent = false };

        private static PhaseConstraint Orbital(string body, double period)
            => new PhaseConstraint { Kind = ConstraintKind.Orbital, BodyName = body,
                PeriodSeconds = period, PhaseOffsetSeconds = 0.0, RelativeToParent = true };

        // bodyInfo is consulted only for an Orbital constraint's SoiRadius/OrbitalVelocity tolerance;
        // Rotation tolerance is period * fraction (mode-dependent) and ignores bodyInfo.
        private sealed class NaNBody : IBodyInfo
        {
            public double RotationPeriod(string b) => double.NaN;
            public double OrbitPeriod(string b) => double.NaN;
            public string ReferenceBodyName(string b) => null;
            public double SoiRadius(string b) => double.NaN;
            public double OrbitalVelocity(string b) => double.NaN;
            public double GravParameter(string b) => double.NaN;
            public double Radius(string b) => 6.0e5;
            public bool TryGetVesselOrbit(uint pid, string recordedVesselGuid, out double periodSeconds, out string orbitBodyName)
            { periodSeconds = double.NaN; orbitBodyName = null; return false; }
        }

        // Gives the named moon a deterministic Orbital tolerance = soi / vel; NaN for any other body.
        private sealed class MoonBody : IBodyInfo
        {
            private readonly string moon;
            private readonly double soi;
            private readonly double vel;
            public MoonBody(string moon, double soi, double vel) { this.moon = moon; this.soi = soi; this.vel = vel; }
            public double RotationPeriod(string b) => double.NaN;
            public double OrbitPeriod(string b) => double.NaN;
            public string ReferenceBodyName(string b) => null;
            public double SoiRadius(string b) => b == moon ? soi : double.NaN;
            public double OrbitalVelocity(string b) => b == moon ? vel : double.NaN;
            public double GravParameter(string b) => double.NaN;
            public double Radius(string b) => 6.0e5;
            public bool TryGetVesselOrbit(uint pid, string recordedVesselGuid, out double periodSeconds, out string orbitBodyName)
            { periodSeconds = double.NaN; orbitBodyName = null; return false; }
        }

        private static double Err(double synodic, long k, double period)
            => MissionPeriodicity.CircularPhaseError(k * synodic, period);

        // === Phase-1 test cases ===============================================================

        [Fact]
        public void DestRotation_Only_ZeroMoon_FirstInBandWindowSelected()
        {
            // A single DestRotation constraint, Tight band (250 s for the synthetic period). synodic and
            // period are chosen so the first in-band window is k=360 (every earlier window misses), which
            // pins "FIRST in-band selected", not merely "some in-band window".
            const double synodic = 361000.0;
            const double period = 360000.0; // Tight tol = 360000 * 0.25/360 = 250 s
            var cs = new List<PhaseConstraint> { Rotation("Duna", period) };

            var r = DestinationArrivalSolver.SolveArrivalWindow(
                synodic, cs, new NaNBody(), Launch, TransitedBodyRotationMode.Tight, kStart: 1, lookaheadWindows: 400);

            Assert.True(r.WithinTolerance);
            Assert.Equal(1, r.EffectiveConstraintCount);
            const double tightTol = 360000.0 * 0.25 / 360.0; // 250
            Assert.True(Err(synodic, r.ChosenWindowK, period) <= tightTol);
            // Every window strictly before the chosen one is out of band (FIRST in-band).
            for (long k = 1; k < r.ChosenWindowK; k++)
                Assert.True(Err(synodic, k, period) > tightTol, $"k={k} should be out of band");
        }

        [Fact]
        public void DestRotation_Only_NoInBandWindow_BoundedBest()
        {
            // Tight band, but a SHORT horizon that never reaches the aligned window: the solver must
            // return the bounded-best (min worst-residual) window, amber, never accumulating.
            const double synodic = 361000.0;
            const double period = 360000.0;
            var cs = new List<PhaseConstraint> { Rotation("Duna", period) };

            var r = DestinationArrivalSolver.SolveArrivalWindow(
                synodic, cs, new NaNBody(), Launch, TransitedBodyRotationMode.Tight, kStart: 1, lookaheadWindows: 10);

            Assert.False(r.WithinTolerance);
            Assert.Equal(1, r.EffectiveConstraintCount);
            Assert.Contains("bounded-best", r.Method);
            // The residual equals the minimum worst-residual over the scanned horizon, ties -> smallest k.
            double best = double.PositiveInfinity;
            long bestK = 1;
            for (long k = 1; k < 1 + 10; k++)
            {
                double e = Err(synodic, k, period);
                if (e < best) { best = e; bestK = k; }
            }
            Assert.Equal(bestK, r.ChosenWindowK);
            Assert.Equal(best, r.ResidualSeconds, 6);
        }

        [Fact]
        public void DestRotation_Plus_IndependentMoonConfig_JointWorstResidual()
        {
            // Rotation(dest) + Orbital(moon) with INCOMMENSURATE periods: the residual at the chosen window
            // must be the WORST of the two phase errors (not a sum, not a product), and both count.
            const double synodic = 361000.0;
            const double rotPeriod = 360000.0;
            const double moonPeriod = 271000.0;
            var cs = new List<PhaseConstraint> { Rotation("Duna", rotPeriod), Orbital("Ike", moonPeriod) };
            var body = new MoonBody("Ike", soi: 9000000.0, vel: 3000.0); // moon tol = 3000 s

            var r = DestinationArrivalSolver.SolveArrivalWindow(
                synodic, cs, body, Launch, TransitedBodyRotationMode.Loose, kStart: 1, lookaheadWindows: 4096);

            Assert.Equal(2, r.EffectiveConstraintCount);
            double rotErr = Err(synodic, r.ChosenWindowK, rotPeriod);
            double moonErr = Err(synodic, r.ChosenWindowK, moonPeriod);
            Assert.Equal(Math.Max(rotErr, moonErr), r.ResidualSeconds, 6);
            // The joint WithinTolerance is the INTERSECTION: BOTH constraints must be within their own
            // bands at the chosen window (guards against an OR / single-constraint / summed objective).
            const double looseRotTol = 360000.0 * 5.0 / 360.0; // 5000 (transited Rotation, Loose)
            const double moonTol = 9000000.0 / 3000.0;          // 3000 (SoiRadius / OrbitalVelocity)
            Assert.Equal(rotErr <= looseRotTol && moonErr <= moonTol, r.WithinTolerance);
        }

        [Fact]
        public void DegenerateSynodic_FailsClosed()
        {
            // A degenerate window spacing cannot be solved: fail closed to the un-aligned window
            // (within=false, method=degenerate-input), never a k=0 / NaN-residual sentinel.
            var cs = new List<PhaseConstraint> { Rotation("Duna", 360000.0) };

            foreach (double bad in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
            {
                var r = DestinationArrivalSolver.SolveArrivalWindow(
                    bad, cs, new NaNBody(), Launch, TransitedBodyRotationMode.Loose, kStart: 3, lookaheadWindows: 100);
                Assert.False(r.WithinTolerance);
                Assert.Equal(3, r.ChosenWindowK);     // kStart, not the 0 = recorded-play sentinel
                Assert.Equal(0.0, r.ResidualSeconds);  // not NaN
                Assert.Equal("degenerate-input", r.Method);
            }

            // An empty look-ahead horizon is the same fail-closed contract.
            var rEmpty = DestinationArrivalSolver.SolveArrivalWindow(
                361000.0, cs, new NaNBody(), Launch, TransitedBodyRotationMode.Loose, kStart: 3, lookaheadWindows: 0);
            Assert.False(rEmpty.WithinTolerance);
            Assert.Equal("degenerate-input", rEmpty.Method);
        }

        [Fact]
        public void TidallyLockedMoon_CollapsesToOneEffectiveConstraint()
        {
            // Orbital(moon) period == Rotation(dest) period to within the relative tolerance (the Duna/Ike
            // tidal lock): the solver must count ONE effective constraint and produce the same window +
            // residual as the rotation-only case (no double-count). The moon tolerance is set generous so
            // the rotation term governs, making the two solves directly comparable.
            const double synodic = 361000.0;
            const double period = 65517.0;
            var rotOnly = new List<PhaseConstraint> { Rotation("Duna", period) };
            var both = new List<PhaseConstraint> { Rotation("Duna", period), Orbital("Ike", period) };
            var body = new MoonBody("Ike", soi: 100000000.0, vel: 1000.0); // huge moon tol -> never binds

            var rRot = DestinationArrivalSolver.SolveArrivalWindow(
                synodic, rotOnly, body, Launch, TransitedBodyRotationMode.Loose, kStart: 1, lookaheadWindows: 4096);
            var rBoth = DestinationArrivalSolver.SolveArrivalWindow(
                synodic, both, body, Launch, TransitedBodyRotationMode.Loose, kStart: 1, lookaheadWindows: 4096);

            Assert.Equal(1, rRot.EffectiveConstraintCount);
            Assert.Equal(1, rBoth.EffectiveConstraintCount); // collapsed, not 2
            Assert.Equal(rRot.ChosenWindowK, rBoth.ChosenWindowK);
            Assert.Equal(rRot.ResidualSeconds, rBoth.ResidualSeconds, 6);
            Assert.Contains("tidal-collapse", rBoth.Method);
            Assert.Contains("single-constraint", rRot.Method);
        }

        [Fact]
        public void LooseVsPrecise_ChangesSelectedK()
        {
            // Same DestRotation set, Loose vs Precise(Tight): Loose admits an earlier window that Precise
            // rejects, proving the tolerance ladder drives the selected k. synodic/period are chosen so
            // window k=1 has residual 1000 s: inside Loose (5000) but outside Precise (250); Precise's first
            // aligned window is k=360.
            const double synodic = 361000.0;
            const double period = 360000.0;
            var cs = new List<PhaseConstraint> { Rotation("Duna", period) };

            var loose = DestinationArrivalSolver.SolveArrivalWindow(
                synodic, cs, new NaNBody(), Launch, TransitedBodyRotationMode.Loose, kStart: 1, lookaheadWindows: 1000);
            var precise = DestinationArrivalSolver.SolveArrivalWindow(
                synodic, cs, new NaNBody(), Launch, TransitedBodyRotationMode.Tight, kStart: 1, lookaheadWindows: 1000);

            Assert.True(loose.WithinTolerance);
            Assert.True(precise.WithinTolerance);
            Assert.True(loose.ChosenWindowK < precise.ChosenWindowK,
                $"Loose k={loose.ChosenWindowK} should precede Precise k={precise.ChosenWindowK}");
            // Loose's chosen window is one Precise would have rejected: its residual exceeds the tight band.
            const double tightTol = 360000.0 * 0.25 / 360.0; // 250
            Assert.True(Err(synodic, loose.ChosenWindowK, period) > tightTol);
        }

        [Fact]
        public void Off_DropsDestRotation_KeepsMoonConfig()
        {
            // Off (Drop): the transited DestRotation is removed; only the MoonConfig is enforced. A window
            // that MISSES the rotation phase but matches the moon is now within tolerance.
            const double synodic = 100.0;
            const double rotPeriod = 360000.0;     // rotation would miss at the moon-aligned window
            const double moonPeriod = 31.0;        // 100k mod 31 within tol 2 -> first at k=9 (7k mod 31)
            var cs = new List<PhaseConstraint> { Rotation("Duna", rotPeriod), Orbital("moon", moonPeriod) };
            var body = new MoonBody("moon", soi: 2.0, vel: 1.0); // moon tol = 2 s

            var r = DestinationArrivalSolver.SolveArrivalWindow(
                synodic, cs, body, Launch, TransitedBodyRotationMode.Drop, kStart: 1, lookaheadWindows: 100);

            Assert.True(r.WithinTolerance);
            Assert.Equal(9, r.ChosenWindowK);                 // moon-only first in-band window
            Assert.Equal(1, r.EffectiveConstraintCount);      // moon only; rotation dropped
            Assert.Equal(Err(synodic, 9, moonPeriod), r.ResidualSeconds, 6);
            Assert.Contains("single-constraint", r.Method);
            // Sanity: the rotation phase is genuinely OUT of its Precise band at k=9, so the window is
            // only acceptable because Drop removed it.
            const double tightRotTol = 360000.0 * 0.25 / 360.0; // 250
            Assert.True(Err(synodic, 9, rotPeriod) > tightRotTol);
        }

        [Fact]
        public void NoDrift_AcrossKStartShifts()
        {
            // The residual at a chosen window is an ABSOLUTE function of k (the phase offsets cancel), so
            // advancing kStart steps to the NEXT in-band window without accumulating error. synodic=100,
            // moon period=31, tol=2 -> in-band windows 9, 13, 18 (7k mod 31 <= 2), residuals 1, 2, 2.
            const double synodic = 100.0;
            const double moonPeriod = 31.0;
            var cs = new List<PhaseConstraint> { Orbital("moon", moonPeriod) };
            var body = new MoonBody("moon", soi: 2.0, vel: 1.0); // tol = 2 s

            var r0 = DestinationArrivalSolver.SolveArrivalWindow(
                synodic, cs, body, Launch, TransitedBodyRotationMode.Loose, kStart: 1, lookaheadWindows: 100);
            var r1 = DestinationArrivalSolver.SolveArrivalWindow(
                synodic, cs, body, Launch, TransitedBodyRotationMode.Loose, kStart: 10, lookaheadWindows: 100);
            var r2 = DestinationArrivalSolver.SolveArrivalWindow(
                synodic, cs, body, Launch, TransitedBodyRotationMode.Loose, kStart: 14, lookaheadWindows: 100);

            Assert.Equal(9, r0.ChosenWindowK);
            Assert.Equal(13, r1.ChosenWindowK);
            Assert.Equal(18, r2.ChosenWindowK);
            Assert.True(r0.WithinTolerance && r1.WithinTolerance && r2.WithinTolerance);
            // Each residual equals the direct CircularPhaseError and stays within tol (non-accumulating).
            Assert.Equal(Err(synodic, 9, moonPeriod), r0.ResidualSeconds, 6);
            Assert.Equal(Err(synodic, 13, moonPeriod), r1.ResidualSeconds, 6);
            Assert.Equal(Err(synodic, 18, moonPeriod), r2.ResidualSeconds, 6);
            Assert.True(r0.ResidualSeconds <= 2.0 && r1.ResidualSeconds <= 2.0 && r2.ResidualSeconds <= 2.0);
        }

        [Fact]
        public void SummaryLogLine_EmittedOnce()
        {
            // The batch-counting convention: exactly one [ReaimArrival] summary line per solve, carrying
            // the chosen window, residual, within-tolerance, mode, and effective-constraint count.
            const double synodic = 361000.0;
            const double period = 360000.0;
            var cs = new List<PhaseConstraint> { Rotation("Duna", period) };

            DestinationArrivalSolver.SolveArrivalWindow(
                synodic, cs, new NaNBody(), Launch, TransitedBodyRotationMode.Loose, kStart: 1, lookaheadWindows: 400);

            var arrivalLines = logLines.Where(l => l.Contains("[ReaimArrival]")).ToList();
            Assert.Single(arrivalLines);
            string line = arrivalLines[0];
            Assert.Contains("k=", line);
            Assert.Contains("residual=", line);
            Assert.Contains("within=", line);
            Assert.Contains("mode=", line);
            Assert.Contains("effConstraints=", line);
        }

        // === Post-M4c joint wiring: hold-aware sampling + the joint-hold lattice ==============

        private static PhaseConstraint Station(string orbitedBody, double period)
            => new PhaseConstraint { Kind = ConstraintKind.VesselOrbital, BodyName = orbitedBody,
                PeriodSeconds = period, PhaseOffsetSeconds = 0.0, AnchorVesselPid = 4242 };

        [Fact]
        public void HoldAware_StationExact_RotationWithinTol_PicksFirstWindow()
        {
            // Hold-aware sampling (the D8 landing+station joint model): the residual is no longer
            // sampled at raw k*synodic - the hold snaps FORWARD onto the station lattice (station
            // exact, residual 0) and extends by whole station periods until the rotation is within
            // its Loose 5-deg band. Geometry: synodic 12345, T_sta 100, T_rot 360 (Loose tol 5s).
            // At k=1: base snap 12345 -> 12400 (rot phase 160), i=1 -> 12500 (100), i=2 -> 12600 =
            // 35*360 exactly -> residual 0. The pick is the FIRST window with i=2.
            var cs = new List<PhaseConstraint> { Rotation("Duna", 360.0), Station("Duna", 100.0) };

            var r = DestinationArrivalSolver.SolveArrivalWindow(
                12345.0, cs, new NaNBody(), Launch, TransitedBodyRotationMode.Loose,
                kStart: 1, lookaheadWindows: 8,
                holdAlignPeriodSeconds: 100.0, maxWholeHoldPeriods: 64);

            Assert.True(r.WithinTolerance);
            Assert.Equal(1, r.ChosenWindowK);
            Assert.Equal(2, r.ChosenHoldWholePeriods);
            Assert.Equal(0.0, r.ResidualSeconds, 9);
            Assert.Equal(2, r.EffectiveConstraintCount);
            Assert.StartsWith("joint-hold", r.Method);
        }

        [Fact]
        public void HoldAware_BudgetTooSmall_BoundedBest_NotWithin()
        {
            // The same geometry with a whole-period budget of 1: no window in the horizon reaches
            // the 5s rotation band (the lattice needs up to 17 whole periods), so the solve falls
            // to bounded-best and reports NOT within tolerance - the fail-closed signal the
            // planner turns into the amber.
            var cs = new List<PhaseConstraint> { Rotation("Duna", 360.0), Station("Duna", 100.0) };

            var r = DestinationArrivalSolver.SolveArrivalWindow(
                12345.0, cs, new NaNBody(), Launch, TransitedBodyRotationMode.Loose,
                kStart: 1, lookaheadWindows: 8,
                holdAlignPeriodSeconds: 100.0, maxWholeHoldPeriods: 1);

            Assert.False(r.WithinTolerance);
            Assert.True(r.ResidualSeconds > 5.0);
            Assert.EndsWith("/bounded-best", r.Method);
        }

        [Fact]
        public void HoldAware_TidalPeriods_HeldConstraintCollapses()
        {
            // T_rot == T_station: both constraints ride the hold lattice (period-equality match),
            // every window is exact at i=0, and the honest count collapses to one (tidal).
            var cs = new List<PhaseConstraint> { Rotation("Duna", 100.0), Station("Duna", 100.0) };

            var r = DestinationArrivalSolver.SolveArrivalWindow(
                12345.0, cs, new NaNBody(), Launch, TransitedBodyRotationMode.Loose,
                kStart: 1, lookaheadWindows: 8,
                holdAlignPeriodSeconds: 100.0, maxWholeHoldPeriods: 64);

            Assert.True(r.WithinTolerance);
            Assert.Equal(1, r.ChosenWindowK);
            Assert.Equal(0, r.ChosenHoldWholePeriods);
            Assert.Equal(0.0, r.ResidualSeconds, 9);
            Assert.Equal(1, r.EffectiveConstraintCount);
            Assert.StartsWith("tidal-collapse", r.Method);
        }

        [Fact]
        public void HoldAware_OmittedParams_ByteIdenticalToRawGrid()
        {
            // Omitting the hold parameters (or passing the NaN sentinel) preserves the shipped
            // raw-grid sampling byte-identically - the single-constraint consumers see no change.
            const double synodic = 361000.0;
            var cs = new List<PhaseConstraint> { Rotation("Duna", 360000.0) };

            var omitted = DestinationArrivalSolver.SolveArrivalWindow(
                synodic, cs, new NaNBody(), Launch, TransitedBodyRotationMode.Tight, 1, 400);
            var nanPassed = DestinationArrivalSolver.SolveArrivalWindow(
                synodic, cs, new NaNBody(), Launch, TransitedBodyRotationMode.Tight, 1, 400,
                holdAlignPeriodSeconds: double.NaN, maxWholeHoldPeriods: 64);

            Assert.Equal(omitted.ChosenWindowK, nanPassed.ChosenWindowK);
            Assert.Equal(omitted.ResidualSeconds, nanPassed.ResidualSeconds, 12);
            Assert.Equal(omitted.WithinTolerance, nanPassed.WithinTolerance);
            Assert.Equal(omitted.Method, nanPassed.Method);
            Assert.Equal(0, nanPassed.ChosenHoldWholePeriods);
        }

        [Fact]
        public void PlanJointHoldLattice_Feasible_ReportsWorstMissRun()
        {
            // T_sta 100 / T_rot 360 / tol 5: the lattice orbit i*100 mod 360 hits the band every
            // 18th point, so the worst run of consecutive misses is 17 - inside a 64 budget.
            var plan = DestinationArrivalSolver.PlanJointHoldLattice(100.0, 360.0, 5.0, 64);

            Assert.True(plan.Feasible);
            Assert.Equal(17, plan.WorstMissRun);
            Assert.Equal(65 * 100.0, plan.MaxHoldSeconds, 9);
        }

        [Fact]
        public void PlanJointHoldLattice_IncommensurateTight_Infeasible()
        {
            // The fail-closed fixture: T_sta 100 / T_rot 3617.7 under the Tight 0.25-deg band
            // (2.51s). The lattice's worst miss-run is in the thousands - far past any budget -
            // so the plan is infeasible and the planner ambers instead of a partial alignment.
            double tol = 3617.7 * 0.25 / 360.0;
            var plan = DestinationArrivalSolver.PlanJointHoldLattice(100.0, 3617.7, tol, 64);

            Assert.False(plan.Feasible);
            Assert.True(plan.WorstMissRun > 64,
                $"expected a miss-run past the budget, got {plan.WorstMissRun}");
        }

        [Fact]
        public void PlanJointHoldLattice_TidalExact_FeasibleAtZeroBudget()
        {
            // Identical periods: every lattice point is exact, worst run 0 - feasible even with
            // no whole-period budget at all.
            var plan = DestinationArrivalSolver.PlanJointHoldLattice(100.0, 100.0, 0.0, 0);

            Assert.True(plan.Feasible);
            Assert.Equal(0, plan.WorstMissRun);
        }

        [Fact]
        public void PlanJointHoldLattice_DegenerateInputs_Infeasible()
        {
            Assert.False(DestinationArrivalSolver.PlanJointHoldLattice(double.NaN, 360.0, 5.0, 64).Feasible);
            Assert.False(DestinationArrivalSolver.PlanJointHoldLattice(100.0, 0.0, 5.0, 64).Feasible);
            Assert.False(DestinationArrivalSolver.PlanJointHoldLattice(100.0, 360.0, double.NaN, 64).Feasible);
            Assert.False(DestinationArrivalSolver.PlanJointHoldLattice(100.0, 360.0, 5.0, -1).Feasible);
            Assert.False(DestinationArrivalSolver.PlanJointHoldLattice(-100.0, 360.0, 5.0, 64).Feasible);
        }
    }
}
