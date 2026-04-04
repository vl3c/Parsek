using Xunit;

namespace Parsek.Tests
{
    public class DebrisFilterTests
    {
        // ShouldRecordDebris thresholds: partCount >= 3 OR mass >= 0.5t

        [Fact]
        public void SinglePartLowMass_NotRecorded()
        {
            Assert.False(ParsekFlight.ShouldRecordDebris(1, 0.1f));
        }

        [Fact]
        public void TwoPartsLowMass_NotRecorded()
        {
            Assert.False(ParsekFlight.ShouldRecordDebris(2, 0.3f));
        }

        [Fact]
        public void ZeroPartsZeroMass_NotRecorded()
        {
            Assert.False(ParsekFlight.ShouldRecordDebris(0, 0f));
        }

        [Fact]
        public void ThreeParts_Recorded()
        {
            // Meets part count threshold even with low mass
            Assert.True(ParsekFlight.ShouldRecordDebris(3, 0.1f));
        }

        [Fact]
        public void ManyParts_Recorded()
        {
            Assert.True(ParsekFlight.ShouldRecordDebris(10, 0.01f));
        }

        [Fact]
        public void HeavySinglePart_Recorded()
        {
            // Meets mass threshold even with one part (e.g., spent SRB)
            Assert.True(ParsekFlight.ShouldRecordDebris(1, 0.5f));
        }

        [Fact]
        public void HeavyMultiPart_Recorded()
        {
            Assert.True(ParsekFlight.ShouldRecordDebris(5, 2.0f));
        }

        [Fact]
        public void JustBelowBothThresholds_NotRecorded()
        {
            Assert.False(ParsekFlight.ShouldRecordDebris(2, 0.49f));
        }

        [Fact]
        public void AtExactMassThreshold_Recorded()
        {
            Assert.True(ParsekFlight.ShouldRecordDebris(1, 0.5f));
        }

        [Fact]
        public void AtExactPartThreshold_Recorded()
        {
            Assert.True(ParsekFlight.ShouldRecordDebris(3, 0f));
        }
    }
}
