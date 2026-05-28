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
    }
}
