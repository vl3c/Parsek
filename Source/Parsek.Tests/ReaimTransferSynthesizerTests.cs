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
    }
}
