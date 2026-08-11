using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    // The pure decision in ReaimTransferSynthesizer (the rest is Unity-bound live glue exercised by
    // the in-game canary CrossParentReaimCanaryInGameTest). IsSaneTransferConic is the plan-review-M3
    // validate-and-skip guard that rejects a degenerate Lambert result before it reaches CalculatePatch.
    public class ReaimTransferSynthesizerTests
    {
        [Theory]
        [InlineData(0.0, 1.0e10, true)]    // circular elliptic transfer - sane
        [InlineData(0.4, 1.7e10, true)]    // typical Hohmann-ish ellipse - sane
        [InlineData(0.999, 1.0e10, true)]  // very eccentric but still bound - sane
        [InlineData(1.0, 1.0e10, false)]   // parabolic - reject
        [InlineData(1.5, -1.0e10, false)]  // hyperbolic (negative sma) - reject
        [InlineData(0.4, 0.0, false)]      // non-positive sma - reject
        [InlineData(0.4, -5.0, false)]     // negative sma - reject
        public void IsSaneTransferConic_AcceptsBoundEllipsesRejectsDegenerate(
            double ecc, double sma, bool expected)
        {
            Assert.Equal(expected, ReaimTransferSynthesizer.IsSaneTransferConic(ecc, sma));
        }

        [Fact]
        public void IsSaneTransferConic_NaNInfinity_Rejected()
        {
            Assert.False(ReaimTransferSynthesizer.IsSaneTransferConic(double.NaN, 1.0e10));
            Assert.False(ReaimTransferSynthesizer.IsSaneTransferConic(0.4, double.NaN));
            Assert.False(ReaimTransferSynthesizer.IsSaneTransferConic(double.PositiveInfinity, 1.0e10));
            Assert.False(ReaimTransferSynthesizer.IsSaneTransferConic(0.4, double.PositiveInfinity));
        }

        // IsRetrogradeTransfer is the handedness predicate the synth uses to match the synthesized
        // transfer's direction to the RECORDED transfer's (the re-aim adapts to what was recorded; a
        // recorded-prograde mission stays prograde, a recorded-retrograde one stays retrograde).
        [Theory]
        [InlineData(0.0, false)]     // equatorial prograde
        [InlineData(0.08, false)]    // the recorded Kerbin->Duna transfer inclination (prograde)
        [InlineData(5.0, false)]     // a few degrees, still prograde
        [InlineData(89.9, false)]    // just under polar, prograde side
        [InlineData(90.1, true)]     // just over polar -> retrograde
        [InlineData(179.14, true)]   // the flipped re-aim transfer seen in the playtest
        [InlineData(180.0, true)]    // fully retrograde
        public void IsRetrogradeTransfer_FlagsInclinationOver90(double incDeg, bool expected)
        {
            Assert.Equal(expected, ReaimTransferSynthesizer.IsRetrogradeTransfer(incDeg));
        }

        [Fact]
        public void IsRetrogradeTransfer_NaN_NotRetrograde()
        {
            // NaN inclination (no recorded leg found) must not be classified retrograde, so the synth
            // falls back to the prograde default rather than throwing.
            Assert.False(ReaimTransferSynthesizer.IsRetrogradeTransfer(double.NaN));
        }

        // ProjectOntoPlane flattens the target endpoint into the launch body's orbital plane to kill the
        // near-180-degree Lambert plane singularity (which otherwise forced the departure search to step
        // days off the synodic window and desynced the transfer perigee from live Kerbin).
        [Fact]
        public void ProjectOntoPlane_RemovesNormalComponent()
        {
            // Normal along +z: projecting (3, 4, 5) drops the z component, keeping (3, 4, 0).
            var normal = new Vector3d(0, 0, 2); // length need not be 1
            var v = new Vector3d(3, 4, 5);
            var projected = ReaimTransferSynthesizer.ProjectOntoPlane(v, normal);
            Assert.Equal(3.0, projected.x, 9);
            Assert.Equal(4.0, projected.y, 9);
            Assert.Equal(0.0, projected.z, 9);
        }

        [Fact]
        public void ProjectOntoPlane_VectorAlreadyInPlane_Unchanged()
        {
            // A vector already orthogonal to the normal (in the plane) is returned unchanged: the launch
            // endpoint r1 lies in the launch body's plane, so projecting it must be a no-op.
            var normal = new Vector3d(0, 0, 1);
            var v = new Vector3d(10, -7, 0);
            var projected = ReaimTransferSynthesizer.ProjectOntoPlane(v, normal);
            Assert.Equal(10.0, projected.x, 9);
            Assert.Equal(-7.0, projected.y, 9);
            Assert.Equal(0.0, projected.z, 9);
        }

        [Fact]
        public void ProjectOntoPlane_ResultIsOrthogonalToNormal()
        {
            // For an arbitrary (non-axis-aligned) normal, the projection must be orthogonal to it.
            var normal = new Vector3d(1, 2, 3);
            var v = new Vector3d(-4, 5, 6);
            var projected = ReaimTransferSynthesizer.ProjectOntoPlane(v, normal);
            double dot = projected.x * normal.x + projected.y * normal.y + projected.z * normal.z;
            Assert.Equal(0.0, dot, 6);
        }

        [Fact]
        public void ProjectOntoPlane_DegenerateNormal_ReturnsInput()
        {
            // A zero-length normal (degenerate launch geometry) must not divide by zero; return v as-is.
            var v = new Vector3d(1, 2, 3);
            var projected = ReaimTransferSynthesizer.ProjectOntoPlane(v, Vector3d.zero);
            Assert.Equal(v.x, projected.x, 9);
            Assert.Equal(v.y, projected.y, 9);
            Assert.Equal(v.z, projected.z, 9);
        }

        // ===== Bug A: heliocentric transfer plane-tilt correction (plan section 5.1 #2-#7). =====

        // The zero sentinel (exact zero vector) returned on degenerate ComputeIntendedPlaneNormal input.
        private static void AssertZeroSentinel(Vector3d v)
        {
            Assert.Equal(0.0, v.x, 12);
            Assert.Equal(0.0, v.y, 12);
            Assert.Equal(0.0, v.z, 12);
        }

        // The plane inclination (degrees) implied by an angular-momentum direction h: the angle from the
        // reference-plane (ecliptic) normal. KSP's un-swizzled WORLD frame is Y-up, so the ecliptic normal
        // is +Y => acos(|h.y|/|h|). These tests therefore build geometry in the WORLD frame: the ecliptic is
        // the xz-plane, "up" is +Y, and a prograde orbit's angular momentum points along +Y.
        private static double IncOfNormal(Vector3d h)
        {
            double m = h.magnitude;
            double c = System.Math.Abs(h.y) / m;
            if (c > 1.0) c = 1.0;
            return System.Math.Acos(c) * 180.0 / System.Math.PI;
        }

        // #2: the correction body pins the plane onto nIntended (when r1 is IN that plane so the constraint is
        // exact), preserves |v1|, preserves the radial component, AND preserves handedness (sign of
        // (r1 x v1) . launchPlaneNormal). Degenerate nIntended => false + v1 unchanged.
        [Fact]
        public void ConstrainTransferPlane_PinsPlaneAndPreservesSpeedHandedness()
        {
            // nIntended = +y (ecliptic normal, world Y-up). r1 along +x lies IN that plane (r-hat
            // perpendicular to nIntended), so n_ach == nIntended and the constraint is exact.
            var nIntended = new Vector3d(0, 1, 0);
            var r1 = new Vector3d(7.0e9, 0, 0);
            // A v1 whose r1 x v1 tilts ~3 deg off +y: dominant prograde -z transverse (x cross -z = +y),
            // a small +y tilt, and a radial +x component.
            double tilt = 3.0 * System.Math.PI / 180.0;
            var v1 = new Vector3d(800.0, 9000.0 * System.Math.Sin(tilt), -9000.0 * System.Math.Cos(tilt));
            var launchPlaneNormal = Vector3d.Cross(r1, v1); // the prograde handedness reference

            bool ok = ReaimTransferSynthesizer.ConstrainTransferPlane(r1, v1, nIntended, out Vector3d v1c);
            Assert.True(ok);

            // (a) plane pinned: r1 x v1' parallel to nIntended => inclination ~0.
            Vector3d hCorr = Vector3d.Cross(r1, v1c);
            Assert.Equal(0.0, IncOfNormal(hCorr), 6);

            // (b) speed preserved.
            Assert.Equal(v1.magnitude, v1c.magnitude, 3);

            // (c) radial component preserved (v . r-hat).
            Vector3d rHat = r1 / r1.magnitude;
            Assert.Equal(Vector3d.Dot(v1, rHat), Vector3d.Dot(v1c, rHat), 3);

            // (d) handedness preserved (sign of (r1 x v) . launchPlaneNormal).
            double sBefore = Vector3d.Dot(Vector3d.Cross(r1, v1), launchPlaneNormal);
            double sAfter = Vector3d.Dot(hCorr, launchPlaneNormal);
            Assert.True(System.Math.Sign(sBefore) == System.Math.Sign(sAfter) && System.Math.Sign(sAfter) != 0,
                $"handedness must be preserved (before={sBefore} after={sAfter})");

            // Degenerate nIntended => false + v1 unchanged.
            bool degen = ReaimTransferSynthesizer.ConstrainTransferPlane(r1, v1, Vector3d.zero, out Vector3d vd);
            Assert.False(degen);
            Assert.Equal(v1.x, vd.x, 9);
            Assert.Equal(v1.y, vd.y, 9);
            Assert.Equal(v1.z, vd.z, 9);
        }

        // #3 (the over-determination test the naive draft would hide): with r1 OFF the nIntended node, the
        // result inclination equals AchievablePlaneInclinationDegrees(r1, nIntended) (n_ach), NOT exact
        // nIntended; it collapses to 0 at phi=90 deg. This pins that the result is the ACHIEVABLE plane.
        [Theory]
        [InlineData(0.0)]
        [InlineData(45.0)]
        [InlineData(90.0)]
        [InlineData(135.0)]
        public void ConstrainTransferPlane_OffPlaneR1_RespectsAchievableBound(double phiDeg)
        {
            // nIntended tilted 7 deg about the +x axis from +y, so its node line is the +x axis. Vary r1's
            // phase phi in the ecliptic (xz-plane): at phi=0 r1 is on the node (achievable == 7 deg); at
            // phi=90 r1 is node-perpendicular (achievable collapses to 0).
            double inc = 7.0 * System.Math.PI / 180.0;
            // nIntended = rotate +y about +x by inc: (0, cos inc, sin inc) (node line along +x).
            var nIntended = new Vector3d(0.0, System.Math.Cos(inc), System.Math.Sin(inc));
            double phi = phiDeg * System.Math.PI / 180.0;
            // r1 in the ecliptic (xz-plane) at angle phi from +x (the node line).
            var r1 = new Vector3d(7.0e9 * System.Math.Cos(phi), 0.0, 7.0e9 * System.Math.Sin(phi));
            // An arbitrary v1 with a transverse component (so the rotation is well-defined).
            var v1 = new Vector3d(500.0, 1500.0, 9000.0);

            double expectedAch = ReaimTransferSynthesizer.AchievablePlaneInclinationDegrees(r1, nIntended);

            bool ok = ReaimTransferSynthesizer.ConstrainTransferPlane(r1, v1, nIntended, out Vector3d v1c);
            Assert.True(ok);
            double resultInc = IncOfNormal(Vector3d.Cross(r1, v1c));

            // The rendered plane is n_ach, so the result inc must equal AchievablePlaneInclinationDegrees,
            // NOT exact nIntended (7 deg). At phi=90 it collapses toward 0.
            Assert.Equal(expectedAch, resultInc, 3);
            if (System.Math.Abs(phiDeg - 90.0) < 1e-9)
                Assert.True(resultInc < 0.01, $"at node-perpendicular phase the achievable inc collapses to ~0 (got {resultInc})");
        }

        // #4 (the load-bearing GATE test): Duna (nTarget ~ ecliptic) is safe at ALL phases; Moho (nTarget
        // inc ~7 deg) is safe only at favorable phase (r1 near the node) and UNsafe at adverse phase.
        [Fact]
        public void ConstrainTransferPlaneIsSafe_GatesDunaApplyMohoAdverseDecline()
        {
            double tol = ReaimTransferSynthesizer.InclinationToleranceDegrees;

            // Duna: nTarget ~ ecliptic (+y, real inc ~0.06 deg). Treat as ~equatorial: incAch ~ 0 at all
            // phases and targetInc ~ 0.06, so the gate (|incAch - targetInc| <= tol) is satisfied everywhere.
            var nDuna = new Vector3d(0.0, 1.0, 0.0);
            double dunaInc = 0.06;
            foreach (double phiDeg in new[] { 0.0, 45.0, 90.0, 135.0, 179.0 })
            {
                double phi = phiDeg * System.Math.PI / 180.0;
                var r1 = new Vector3d(13.6e9 * System.Math.Cos(phi), 0.0, 13.6e9 * System.Math.Sin(phi));
                Assert.True(ReaimTransferSynthesizer.ConstrainTransferPlaneIsSafe(r1, nDuna, dunaInc, tol),
                    $"Duna gate must be SAFE at phi={phiDeg} (nTarget ~ ecliptic => achievable ~ target at all phases)");
            }

            // Moho: nTarget inc 7 deg (node line along +x). Safe when r1 is on the node (phi 0 / 180 deg),
            // UNsafe at adverse phase (phi 30/45/60/90/135 collapses the achievable inc far below 7 deg).
            double mohoIncRad = 7.0 * System.Math.PI / 180.0;
            var nMoho = new Vector3d(0.0, System.Math.Cos(mohoIncRad), System.Math.Sin(mohoIncRad));
            double mohoInc = 7.0;
            Vector3d R1AtPhi(double phiDeg)
            {
                double phi = phiDeg * System.Math.PI / 180.0;
                return new Vector3d(8.0e9 * System.Math.Cos(phi), 0.0, 8.0e9 * System.Math.Sin(phi));
            }
            // phi=0 (and 180/179 ~ node line): r1 ON the node => achievable ~ 7 deg => SAFE.
            Assert.True(ReaimTransferSynthesizer.ConstrainTransferPlaneIsSafe(R1AtPhi(0.0), nMoho, mohoInc, tol),
                "Moho gate must be SAFE at the node (phi=0) where the achievable plane IS Moho's 7 deg plane");
            Assert.True(ReaimTransferSynthesizer.ConstrainTransferPlaneIsSafe(R1AtPhi(180.0), nMoho, mohoInc, tol),
                "Moho gate must be SAFE at phi=180 (still on the node line)");
            // adverse phases: UNsafe (decline).
            foreach (double phiDeg in new[] { 30.0, 45.0, 60.0, 90.0, 135.0 })
                Assert.False(ReaimTransferSynthesizer.ConstrainTransferPlaneIsSafe(R1AtPhi(phiDeg), nMoho, mohoInc, tol),
                    $"Moho gate must be UNSAFE at adverse phi={phiDeg} (achievable inc collapses far below 7 deg => decline, never over-flatten)");
        }

        // #5: the intended normal is normalize(r2 x v2Target); zero/NaN inputs => zero sentinel.
        [Fact]
        public void ComputeIntendedPlaneNormal_KnownGeometryAndDegenerate()
        {
            // A circular orbit in the ECLIPTIC (world xz-plane): r2 along +x, prograde v2 along -z =>
            // h = r2 x v2 along +y => the normal is +y (inc 0). A known tilt of v2 toward +y raises the inc.
            var r2 = new Vector3d(20.7e9, 0.0, 0.0);
            var v2 = new Vector3d(0.0, 0.0, -7000.0);
            Vector3d n = ReaimTransferSynthesizer.ComputeIntendedPlaneNormal(r2, v2);
            Assert.Equal(1.0, n.magnitude, 6);
            Assert.Equal(0.0, IncOfNormal(n), 6);
            Assert.True(n.y > 0.0, "the +y (prograde) normal expected for this geometry");

            // A 7-deg-inclined velocity: the prograde (-z) velocity tilted toward +y.
            double inc = 7.0 * System.Math.PI / 180.0;
            var v2Inc = new Vector3d(0.0, 7000.0 * System.Math.Sin(inc), -7000.0 * System.Math.Cos(inc));
            Vector3d nInc = ReaimTransferSynthesizer.ComputeIntendedPlaneNormal(r2, v2Inc);
            Assert.Equal(7.0, IncOfNormal(nInc), 3);

            // Degenerate: zero v2Target => zero sentinel (collinear/zero cross). NaN v2Target => zero sentinel.
            AssertZeroSentinel(ReaimTransferSynthesizer.ComputeIntendedPlaneNormal(r2, Vector3d.zero));
            var nanVec = new Vector3d(double.NaN, 0.0, 0.0);
            AssertZeroSentinel(ReaimTransferSynthesizer.ComputeIntendedPlaneNormal(r2, nanVec));
            // Collinear r2/v2 (cross ~ 0) => zero sentinel.
            AssertZeroSentinel(ReaimTransferSynthesizer.ComputeIntendedPlaneNormal(r2, new Vector3d(5.0, 0.0, 0.0)));
        }

        // #6: the spurious-vs-real discriminator against a target-derived bound. Moho's real 7.0 deg exceeds
        // Duna's worst 5.06 deg spurious tilt, so only the target-derived bound separates them.
        [Theory]
        [InlineData(2.3573, 0.56, true)]   // Duna loop1 spurious tilt > Duna bound => excessive
        [InlineData(5.0573, 0.56, true)]   // Duna loop2 spurious tilt > Duna bound => excessive
        [InlineData(0.1312, 0.56, false)]  // the already-in-plane Duna window => no-op
        [InlineData(7.0, 7.5, false)]      // Moho's real 7 deg under Moho's 7.5 bound => NOT excessive (real inc)
        [InlineData(9.0, 7.5, true)]       // a Moho window 1.5 deg over its bound => excessive
        [InlineData(95.0, 0.56, false)]    // > 90 => retrograde domain, declined upstream => not handled here
        [InlineData(double.NaN, 0.56, false)] // NaN inc => not excessive
        public void IsExcessiveTiltTransfer_Theory(double inc, double bound, bool expected)
        {
            Assert.Equal(expected, ReaimTransferSynthesizer.IsExcessiveTiltTransfer(inc, bound));
        }

        // #7: the target-derived bound = max(max(launchInc, targetInc), 0) + tol. NaN body inc => contributes 0.
        [Theory]
        [InlineData(0.0, 0.06, 0.56)]   // Kerbin ~0 / Duna ~0.06 => ~0.56
        [InlineData(0.0, 7.0, 7.5)]     // Kerbin ~0 / Moho ~7.0 => ~7.5
        [InlineData(0.0, 2.1, 2.6)]     // Kerbin ~0 / Eve ~2.1 => ~2.6
        [InlineData(0.0, 6.15, 6.65)]   // Kerbin ~0 / Eeloo ~6.15 => ~6.65
        public void InclinationBoundDegrees_Theory(double launchInc, double targetInc, double expected)
        {
            Assert.Equal(expected, ReaimTransferSynthesizer.InclinationBoundDegrees(launchInc, targetInc), 6);
        }

        [Fact]
        public void InclinationBoundDegrees_NaN_Handled()
        {
            double tol = ReaimTransferSynthesizer.InclinationToleranceDegrees;
            // NaN body inclination contributes 0; the other body still governs.
            Assert.Equal(7.0 + tol, ReaimTransferSynthesizer.InclinationBoundDegrees(double.NaN, 7.0), 6);
            Assert.Equal(tol, ReaimTransferSynthesizer.InclinationBoundDegrees(double.NaN, double.NaN), 6);
            // A negative launch inclination cannot drop the bound below tol (the max(...,0) clamp).
            Assert.Equal(tol, ReaimTransferSynthesizer.InclinationBoundDegrees(-5.0, 0.0), 6);
        }

        // ================= E1: the Kerbin->Eve CYCLE-0 geometry, pinned in-repo =================
        //
        // WHY THESE CELLS EXIST (docs/dev/plans/reaim-inclined-target-tilt-retention.md section 3, E1).
        // On 2026-08-11 the V8-eve-player-loop lane raised `seam-endpoint-outside-soi` for the first
        // time in its existence, bit-identically on five bracketed runs. The route: an ENGAGED
        // Kerbin->Eve loop unit whose cycle-0 window declined at the plane-tilt achievability gate on
        // ALL 27 tof candidates -
        //   `tilt-correction ... incAch=1.2358 targetInc=2.1000 tol=0.50 state=declined
        //    reason=unreachable-plane` x27
        // - so the window fell back to the FAITHFUL recorded transfer, rendered one synodic late, which
        // misses the moved Eve by 4.6216 SOI radii. These cells drive the REAL helpers
        // (AchievablePlaneInclinationDegrees / ConstrainTransferPlaneIsSafe / InclinationBoundDegrees)
        // and the REAL band law (ReaimTofSearch.BuildParkingCandidateTofs) over a stock-constant model
        // of that window, so the arithmetic behind the diagnosis is a repo fact rather than a log
        // reading. They are PHASE 0: they assert what today's code does, and Phase 1's disposition
        // change must leave every one of them green (it changes what the synth DOES with a failed gate,
        // never what the gate computes).
        //
        // The decisive finding they pin: the gate's two inputs (r1 at departureUT, and the target's
        // plane-invariant normal) are BOTH candidate-invariant, so the gate literally cannot see r2 -
        // every candidate declines identically - while the flatten the correction would have applied
        // misses Eve by ~3.4 SOI radii. So the gate answered its own question correctly (flattening
        // here would miss); the defect is its DISPOSITION (kill the candidate rather than retain the
        // un-corrected conic), which is what Phase 1 changes.

        // Frame guard (the .z-vs-.y trap, ReaimTransferSynthesizer.cs:128-131). The production helpers
        // measure inclination against world +Y; a textbook Z-up elements model reads ~90 deg for a flat
        // orbit and would make every number below meaningless while still "passing" a naive threshold.
        // Kerbin (i=0) must read ~0 and Eve must read back its declared 2.1 deg.
        [Fact]
        public void EveCycleZero_ModelFrameIsYUp_FlatOrbitReadsZeroInclination()
        {
            EveCycleZeroGeometry.StateAt(EveCycleZeroGeometry.Kerbin, EveCycleZeroGeometry.DepartureUT,
                out Vector3d r1, out Vector3d v1Launch);
            double kerbinPlaneInc = EveCycleZeroGeometry.InclinationOfNormalDegrees(Vector3d.Cross(r1, v1Launch));
            Assert.True(kerbinPlaneInc < 1e-9,
                $"a flat (i=0) orbit's plane normal must read inclination ~0 against world +Y, got {kerbinPlaneInc.ToString("F6", CultureInfo.InvariantCulture)} deg (a Z-up model reads ~90)");

            EveCycleZeroGeometry.StateAt(EveCycleZeroGeometry.Eve,
                EveCycleZeroGeometry.DepartureUT + EveCycleZeroGeometry.RecordedTofSeconds,
                out Vector3d r2, out Vector3d v2);
            double eveOrbitInc = EveCycleZeroGeometry.InclinationOfNormalDegrees(
                ReaimTransferSynthesizer.ComputeIntendedPlaneNormal(r2, v2));
            Assert.True(Math.Abs(eveOrbitInc - EveCycleZeroGeometry.EveInclinationDegrees) < 1e-6,
                $"the modelled Eve plane must read back its declared 2.1 deg, got {eveOrbitInc.ToString("F6", CultureInfo.InvariantCulture)} deg");

            // And the modelled band must be the product's own: 27 candidates, the count the V8 resolver
            // logged (`synth failed across 27 tof candidates`).
            Assert.Equal(27, EveCycleZeroGeometry.CandidateTofs().Count);
        }

        // E1 (i) - THE decisive arithmetic. incAch is 1.2358 deg and is IDENTICAL for every one of the
        // 27 candidates, because AchievablePlaneInclinationDegrees consumes only r1 (fixed at
        // departureUT) and the target's angular-momentum DIRECTION (plane-invariant for a Kepler
        // orbit). The V8 logs' "incAch CONSTANT at 1.2358 x27" is forced arithmetic, not a numerical
        // curiosity - which is why widening the tof search can never rescue this window.
        [Fact]
        public void EveCycleZero_AchievablePlaneInclination_Is1p2358AndCandidateInvariant()
        {
            EveCycleZeroGeometry.StateAt(EveCycleZeroGeometry.Kerbin, EveCycleZeroGeometry.DepartureUT,
                out Vector3d r1, out _);
            IReadOnlyList<double> tofs = EveCycleZeroGeometry.CandidateTofs();

            double incAtStep0 = double.NaN;
            double minInc = double.MaxValue, maxInc = double.MinValue;
            for (int i = 0; i < tofs.Count; i++)
            {
                EveCycleZeroGeometry.StateAt(EveCycleZeroGeometry.Eve,
                    EveCycleZeroGeometry.DepartureUT + tofs[i], out Vector3d r2, out Vector3d v2);
                Vector3d nTarget = ReaimTransferSynthesizer.ComputeIntendedPlaneNormal(r2, v2);
                Assert.True(nTarget.magnitude > 0.0, "the target plane normal must be non-degenerate");
                double incAch = ReaimTransferSynthesizer.AchievablePlaneInclinationDegrees(r1, nTarget);
                if (i == 0) incAtStep0 = incAch;
                if (incAch < minInc) minInc = incAch;
                if (incAch > maxInc) maxInc = incAch;
            }

            // Measured live (V8, 5/5 runs): incAch=1.2358. Modelled here: 1.2358263809.
            Assert.True(Math.Abs(incAtStep0 - 1.2358) < 0.01,
                $"step-0 incAch must be the measured 1.2358 deg, got {incAtStep0.ToString("F6", CultureInfo.InvariantCulture)}");
            // Candidate-INVARIANCE is the finding: band edges must equal step 0 to double precision.
            Assert.True(maxInc - minInc < 1e-9,
                $"incAch must be candidate-invariant (the gate cannot see r2): spread over {tofs.Count.ToString(CultureInfo.InvariantCulture)} candidates was {(maxInc - minInc).ToString("E3", CultureInfo.InvariantCulture)} deg");
        }

        // E1 (ii) - the gate's verdict, from the real predicate: |1.2358 - 2.1| = 0.864 > 0.5, so
        // ConstrainTransferPlaneIsSafe is false and TODAY that kills the candidate (the
        // `unreachable-plane` decline at ReaimTransferSynthesizer.cs:427-437). Also pinned: the tilt
        // that sends the window into the gate at all - IsExcessiveTiltTransfer against the 2.6 bound.
        [Fact]
        public void EveCycleZero_ConstrainTransferPlaneIsSafe_RefusesTheWindow()
        {
            EveCycleZeroGeometry.StateAt(EveCycleZeroGeometry.Kerbin, EveCycleZeroGeometry.DepartureUT,
                out Vector3d r1, out _);
            IReadOnlyList<double> tofs = EveCycleZeroGeometry.CandidateTofs();
            double tol = ReaimTransferSynthesizer.InclinationToleranceDegrees;
            double bound = ReaimTransferSynthesizer.InclinationBoundDegrees(
                EveCycleZeroGeometry.KerbinInclinationDegrees, EveCycleZeroGeometry.EveInclinationDegrees);
            Assert.Equal(2.6, bound, 9); // Kerbin 0 / Eve 2.1 + 0.5

            for (int i = 0; i < tofs.Count; i++)
            {
                EveCycleZeroGeometry.StateAt(EveCycleZeroGeometry.Eve,
                    EveCycleZeroGeometry.DepartureUT + tofs[i], out Vector3d r2, out Vector3d v2);
                Vector3d nTarget = ReaimTransferSynthesizer.ComputeIntendedPlaneNormal(r2, v2);

                // The conic's plane is plane(r1,r2) by construction (UvLambert returns v1 in span(r1,r2)),
                // so its inclination is what IsExcessiveTiltTransfer sees. Every candidate is excessive...
                double conicInc = EveCycleZeroGeometry.InclinationOfNormalDegrees(
                    EveCycleZeroGeometry.PlaneNormalOfEndpoints(r1, r2));
                Assert.True(ReaimTransferSynthesizer.IsExcessiveTiltTransfer(conicInc, bound),
                    $"candidate {i.ToString(CultureInfo.InvariantCulture)}: inc={conicInc.ToString("F4", CultureInfo.InvariantCulture)} must exceed bound {bound.ToString("F2", CultureInfo.InvariantCulture)} (so the gate runs)");
                // ...and every candidate then fails the achievability gate identically.
                Assert.False(ReaimTransferSynthesizer.ConstrainTransferPlaneIsSafe(
                        r1, nTarget, EveCycleZeroGeometry.EveInclinationDegrees, tol),
                    $"candidate {i.ToString(CultureInfo.InvariantCulture)}: the Eve cycle-0 gate must be UNSAFE (|incAch-2.1| > 0.5) - this is the measured decline");
            }
        }

        // E1 (iii) - the gate's verdict is CORRECT as a correction-safety question, which is why the fix
        // is Design A (retain the un-corrected conic) and not Design B (re-tune the gate). Had the
        // correction fired, the flattened conic would have passed |dot(r2, n_ach)| ~ 278-290 Mm away
        // from Eve - 3.3-3.4x its 85.1 Mm SOI - and died at the downstream encounter check anyway.
        [Fact]
        public void EveCycleZero_FlattenMiss_ExceedsThreeEveSoiRadii()
        {
            EveCycleZeroGeometry.StateAt(EveCycleZeroGeometry.Kerbin, EveCycleZeroGeometry.DepartureUT,
                out Vector3d r1, out _);
            IReadOnlyList<double> tofs = EveCycleZeroGeometry.CandidateTofs();
            const double Floor = 2.5e8; // metres; the plan's asserted floor (measured band 277.8-289.6 Mm)

            double minMiss = double.MaxValue;
            for (int i = 0; i < tofs.Count; i++)
            {
                EveCycleZeroGeometry.StateAt(EveCycleZeroGeometry.Eve,
                    EveCycleZeroGeometry.DepartureUT + tofs[i], out Vector3d r2, out Vector3d v2);
                Vector3d nTarget = ReaimTransferSynthesizer.ComputeIntendedPlaneNormal(r2, v2);
                double miss = EveCycleZeroGeometry.FlattenMissMeters(r1, r2, nTarget);
                Assert.True(miss > Floor,
                    $"candidate {i.ToString(CultureInfo.InvariantCulture)} (tof={tofs[i].ToString("F0", CultureInfo.InvariantCulture)}): flatten miss {miss.ToString("F0", CultureInfo.InvariantCulture)} m must exceed the {Floor.ToString("E1", CultureInfo.InvariantCulture)} m floor");
                Assert.True(miss > 3.0 * EveCycleZeroGeometry.EveSoiMeters,
                    $"candidate {i.ToString(CultureInfo.InvariantCulture)}: flatten miss {miss.ToString("F0", CultureInfo.InvariantCulture)} m must exceed 3x Eve's SOI ({(3.0 * EveCycleZeroGeometry.EveSoiMeters).ToString("F0", CultureInfo.InvariantCulture)} m)");
                if (miss < minMiss) minMiss = miss;
            }

            // The recorded-tof arrival is the one the plan's appendix A quotes (288.5 Mm); pin it.
            EveCycleZeroGeometry.StateAt(EveCycleZeroGeometry.Eve,
                EveCycleZeroGeometry.DepartureUT + EveCycleZeroGeometry.RecordedTofSeconds,
                out Vector3d r2Rec, out Vector3d v2Rec);
            double missAtRecorded = EveCycleZeroGeometry.FlattenMissMeters(
                r1, r2Rec, ReaimTransferSynthesizer.ComputeIntendedPlaneNormal(r2Rec, v2Rec));
            Assert.True(Math.Abs(missAtRecorded / 1e6 - 288.5) < 1.0,
                $"the flatten miss at the recorded tof must be the independently-derived 288.5 Mm, got {(missAtRecorded / 1e6).ToString("F2", CultureInfo.InvariantCulture)} Mm");
        }

        // E1 (iv) - the tilt is LOAD-BEARING, not error to be corrected: plane(r1,r2) is the only plane
        // a single conic through both endpoints can use, and it exceeds the 2.6-deg bound for EVERY
        // candidate. So the 2.6 bound structurally excludes every conic that can encounter Eve at this
        // window - no tof choice inside the band rescues it.
        //
        // BAND NOTE (deliberate, do not "fix" by tightening): the modelled inclinations run 4.65-72.51
        // deg across the 27 candidates, wider than the 10.20-14.71 deg band the V8 runs logged, because
        // the live path departs from a re-phased heliocentric PARK-END while this model departs from
        // Kerbin's center, and inclination is violently amplified as the transfer angle approaches 180
        // deg. The claim asserted here is the one that is invariant to that difference (> bound for
        // every candidate); the recorded-tof value is pinned separately against the plan's appendix A.
        [Fact]
        public void EveCycleZero_PlaneOfEndpointsInclination_ExceedsTheBoundForEveryCandidate()
        {
            EveCycleZeroGeometry.StateAt(EveCycleZeroGeometry.Kerbin, EveCycleZeroGeometry.DepartureUT,
                out Vector3d r1, out _);
            double bound = ReaimTransferSynthesizer.InclinationBoundDegrees(
                EveCycleZeroGeometry.KerbinInclinationDegrees, EveCycleZeroGeometry.EveInclinationDegrees);
            IReadOnlyList<double> tofs = EveCycleZeroGeometry.CandidateTofs();

            for (int i = 0; i < tofs.Count; i++)
            {
                EveCycleZeroGeometry.StateAt(EveCycleZeroGeometry.Eve,
                    EveCycleZeroGeometry.DepartureUT + tofs[i], out Vector3d r2, out _);
                double planeInc = EveCycleZeroGeometry.InclinationOfNormalDegrees(
                    EveCycleZeroGeometry.PlaneNormalOfEndpoints(r1, r2));
                Assert.True(planeInc > bound,
                    $"candidate {i.ToString(CultureInfo.InvariantCulture)} (tof={tofs[i].ToString("F0", CultureInfo.InvariantCulture)}): plane(r1,r2) inc {planeInc.ToString("F4", CultureInfo.InvariantCulture)} must exceed the bound {bound.ToString("F2", CultureInfo.InvariantCulture)} - no candidate can satisfy it");
                Assert.True(planeInc < 90.0,
                    $"candidate {i.ToString(CultureInfo.InvariantCulture)}: and must stay prograde (inc {planeInc.ToString("F4", CultureInfo.InvariantCulture)} < 90), so the tilt gate - not the direction guard - is what declines it");
            }

            // The plan's appendix A value at the recorded tof (~18.5 deg).
            EveCycleZeroGeometry.StateAt(EveCycleZeroGeometry.Eve,
                EveCycleZeroGeometry.DepartureUT + EveCycleZeroGeometry.RecordedTofSeconds,
                out Vector3d r2Rec, out _);
            double incAtRecorded = EveCycleZeroGeometry.InclinationOfNormalDegrees(
                EveCycleZeroGeometry.PlaneNormalOfEndpoints(r1, r2Rec));
            Assert.True(Math.Abs(incAtRecorded - 18.5369) < 0.01,
                $"plane(r1,r2) inc at the recorded tof must be the independently-derived 18.5369 deg, got {incAtRecorded.ToString("F4", CultureInfo.InvariantCulture)}");
        }

        // E1 (v) - the near-180 regime is what amplifies Eve's out-of-plane offset into a double-digit
        // plane inclination (hypothesis H1's mechanism, CONFIRMED). Pinning the transfer angle keeps the
        // model honest about which regime the numbers above were measured in.
        [Fact]
        public void EveCycleZero_TransferAngleAtRecordedTof_IsNear180()
        {
            EveCycleZeroGeometry.StateAt(EveCycleZeroGeometry.Kerbin, EveCycleZeroGeometry.DepartureUT,
                out Vector3d r1, out _);
            EveCycleZeroGeometry.StateAt(EveCycleZeroGeometry.Eve,
                EveCycleZeroGeometry.DepartureUT + EveCycleZeroGeometry.RecordedTofSeconds,
                out Vector3d r2, out _);

            double angle = EveCycleZeroGeometry.TransferAngleDegrees(r1, r2);
            Assert.True(Math.Abs(angle - 175.0) < 2.0,
                $"the cycle-0 transfer angle at the recorded tof must be ~175 deg (Hohmann-class), got {angle.ToString("F4", CultureInfo.InvariantCulture)}");

            // And Eve really is ~271 Mm out of the departure plane at arrival - the offset the near-180
            // geometry amplifies (world +Y is the reference-plane normal, so r2.y IS that offset).
            Assert.True(Math.Abs(Math.Abs(r2.y) / 1e6 - 271.1) < 1.0,
                $"Eve's out-of-plane offset at the recorded arrival must be ~271.1 Mm, got {(Math.Abs(r2.y) / 1e6).ToString("F2", CultureInfo.InvariantCulture)} Mm");
        }

        // ============ E3: the committed Eve recording is a BROKEN-PLANE transfer (fixture fact) ============
        //
        // WHY THIS CELL EXISTS. It kills Design D and scopes Design C out of this branch (plan section 2).
        // Design D would raise the tilt bound to the RECORDED transfer's own inclination - but the player
        // did not fly one plane: they flew a FLAT Sun leg (inc 0.0021 deg), then a mid-course plane change
        // onto Eve's own plane (2.0627), then approached at 2.0689. The resolver's
        // RecordedHeliocentricInclination reads the FIRST in-window Sun segment, so a recorded-inclination
        // bound would read 0.0021 and barely move the 2.6 bound - Eve would still decline. And a single
        // center-to-center conic structurally cannot reproduce a two-conic broken-plane profile, so
        // matching the recorded STYLE (Design C) is a multi-leg-synthesis feature, not this branch.
        [Fact]
        public void EveRecordedTransfer_SunLegInclinations_ShowABrokenPlaneTransfer()
        {
            // xUnit runs from Source/Parsek.Tests/bin/Debug/net472/ - five '..' segments reach the root.
            string repoRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string precPath = Path.Combine(repoRoot,
                "harness", "fixtures", "saves", "eve-orbit-recorded", "Parsek", "Recordings",
                "75a6ab25a0f445219a82b7b841e44ba8.prec.txt");
            Assert.True(File.Exists(precPath), $"the committed Eve fixture recording must exist at {precPath}");

            List<Dictionary<string, string>> sunSegments = ReadOrbitSegments(precPath, "Sun");
            // The recorded Sun chain: flat leg (x2 across a bookkeeping seam), plane-changed leg (x2),
            // final approach (x2). Six segments in three conic pairs.
            Assert.True(sunSegments.Count >= 3,
                $"expected the recorded Sun-leg chain, found {sunSegments.Count.ToString(CultureInfo.InvariantCulture)} Sun ORBIT_SEGMENTs");

            double firstInc = ParseInvariant(sunSegments[0]["inc"]);
            double lastInc = ParseInvariant(sunSegments[sunSegments.Count - 1]["inc"]);
            double planeChangedInc = double.NaN;
            foreach (Dictionary<string, string> seg in sunSegments)
            {
                double inc = ParseInvariant(seg["inc"]);
                if (inc > 1.0) { planeChangedInc = inc; break; } // the first post-plane-change leg
            }

            // The three measured inclinations (2026-08-11 reading of the committed fixture).
            Assert.True(Math.Abs(firstInc - 0.0021) < 0.01,
                $"the first post-ejection Sun leg must be FLAT (~0.0021 deg), got {firstInc.ToString("F6", CultureInfo.InvariantCulture)}");
            Assert.True(Math.Abs(planeChangedInc - 2.0627) < 0.01,
                $"the post-plane-change Sun leg must sit on Eve's own plane (~2.0627 deg), got {planeChangedInc.ToString("F6", CultureInfo.InvariantCulture)}");
            Assert.True(Math.Abs(lastInc - 2.0689) < 0.01,
                $"the final approach Sun leg must be ~2.0689 deg, got {lastInc.ToString("F6", CultureInfo.InvariantCulture)}");

            // The statement itself: the recorded transfer CHANGED PLANE mid-flight. A bound derived from
            // the recorded inclination (Design D) would read the flat first leg and move nothing.
            Assert.True(lastInc - firstInc > 2.0,
                $"the recorded transfer must be broken-plane (last {lastInc.ToString("F4", CultureInfo.InvariantCulture)} - first {firstInc.ToString("F4", CultureInfo.InvariantCulture)} > 2 deg)");
            double designDBound = ReaimTransferSynthesizer.InclinationBoundDegrees(
                EveCycleZeroGeometry.KerbinInclinationDegrees, firstInc);
            Assert.True(designDBound < 1.0,
                $"a recorded-inclination bound would be {designDBound.ToString("F4", CultureInfo.InvariantCulture)} deg - BELOW today's 2.6, so Design D cannot rescue the window");
        }

        // Minimal ORBIT_SEGMENT reader for the .prec.txt fixture: the recording format is a flat
        // ConfigNode text dump, and the cell only needs the key/value pairs of segments whose `body`
        // matches. Deliberately local (no ConfigNode dependency, no production reader) so the cell
        // states the fixture's contents, not the loader's behaviour.
        private static List<Dictionary<string, string>> ReadOrbitSegments(string path, string bodyName)
        {
            var result = new List<Dictionary<string, string>>();
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim() != "ORBIT_SEGMENT")
                    continue;
                var fields = new Dictionary<string, string>();
                for (int j = i + 2; j < lines.Length; j++)  // +2 skips the opening brace
                {
                    string line = lines[j].Trim();
                    if (line == "}")
                        break;
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                        fields[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                if (fields.TryGetValue("body", out string body) && body == bodyName)
                    result.Add(fields);
            }
            return result;
        }

        private static double ParseInvariant(string value)
            => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
