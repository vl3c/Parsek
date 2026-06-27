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
    }
}
