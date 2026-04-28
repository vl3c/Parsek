using Xunit;

namespace Parsek.Tests
{
    public class ParsekFlightWarpCheckpointTests
    {
        [Fact]
        public void ShouldSkipDuplicateWarpCheckpointEvent_FirstEvent_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldSkipDuplicateWarpCheckpointEvent(
                currentWarpRate: 1.0f,
                currentUT: 100.0,
                hasLastEvent: false,
                lastWarpRate: 1.0f,
                lastUT: 100.0));
        }

        [Fact]
        public void ShouldSkipDuplicateWarpCheckpointEvent_SameRateSameUt_ReturnsTrue()
        {
            Assert.True(ParsekFlight.ShouldSkipDuplicateWarpCheckpointEvent(
                currentWarpRate: 1.0f,
                currentUT: 100.0,
                hasLastEvent: true,
                lastWarpRate: 1.0f,
                lastUT: 100.0));
        }

        [Fact]
        public void ShouldSkipDuplicateWarpCheckpointEvent_SameRateAdvancedUt_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldSkipDuplicateWarpCheckpointEvent(
                currentWarpRate: 1.0f,
                currentUT: 101.0,
                hasLastEvent: true,
                lastWarpRate: 1.0f,
                lastUT: 100.0));
        }

        [Fact]
        public void ShouldSkipDuplicateWarpCheckpointEvent_ChangedRateSameUt_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldSkipDuplicateWarpCheckpointEvent(
                currentWarpRate: 2.0f,
                currentUT: 100.0,
                hasLastEvent: true,
                lastWarpRate: 1.0f,
                lastUT: 100.0));
        }

        [Fact]
        public void ShouldSkipDuplicateWarpCheckpointEvent_NonFiniteInputs_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldSkipDuplicateWarpCheckpointEvent(
                currentWarpRate: float.NaN,
                currentUT: 100.0,
                hasLastEvent: true,
                lastWarpRate: 1.0f,
                lastUT: 100.0));
        }
    }
}
