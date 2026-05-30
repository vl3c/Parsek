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
    }
}
