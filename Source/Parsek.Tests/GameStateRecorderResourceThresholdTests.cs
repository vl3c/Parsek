using System.Reflection;
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
            Assert.False(InvokeIsReputationDeltaBelowThreshold(delta));
        }

        [Fact]
        public void IsReputationDeltaBelowThreshold_CumulativeFloatSubtractionShape_ReturnsFalse()
        {
            float oldReputation = 127.001f;
            float newReputation = oldReputation + 0.9999995f;
            float delta = newReputation - oldReputation;

            Assert.True(delta < 1.0f);
            Assert.False(InvokeIsReputationDeltaBelowThreshold(delta));
        }

        [Theory]
        [InlineData(0.5f)]
        [InlineData(-0.5f)]
        public void IsReputationDeltaBelowThreshold_ClearSubThresholdNoise_ReturnsTrue(float delta)
        {
            Assert.True(InvokeIsReputationDeltaBelowThreshold(delta));
        }

        private static bool InvokeIsReputationDeltaBelowThreshold(float delta)
        {
            var method = typeof(GameStateRecorder).GetMethod(
                "IsReputationDeltaBelowThreshold",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return (bool)method.Invoke(null, new object[] { delta });
        }
    }
}
