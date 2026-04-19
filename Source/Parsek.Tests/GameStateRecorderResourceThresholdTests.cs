using Xunit;

namespace Parsek.Tests
{
    public class GameStateRecorderResourceThresholdTests
    {
        [Theory]
        [InlineData(0.9999995f)]
        [InlineData(-0.9999995f)]
        public void IsReputationDeltaBelowThreshold_StockRoundedOnePointZero_ReturnsFalse(float delta)
        {
            Assert.False(GameStateRecorder.IsReputationDeltaBelowThreshold(delta));
        }

        [Fact]
        public void IsReputationDeltaBelowThreshold_CumulativeFloatSubtractionShape_ReturnsFalse()
        {
            float oldReputation = 127.001f;
            float newReputation = oldReputation + 0.9999995f;
            float delta = newReputation - oldReputation;

            Assert.True(delta < 1.0f);
            Assert.False(GameStateRecorder.IsReputationDeltaBelowThreshold(delta));
        }

        [Theory]
        [InlineData(0.998f)]
        [InlineData(-0.998f)]
        public void IsReputationDeltaBelowThreshold_ClearSubThresholdNoise_ReturnsTrue(float delta)
        {
            Assert.True(GameStateRecorder.IsReputationDeltaBelowThreshold(delta));
        }
    }
}
