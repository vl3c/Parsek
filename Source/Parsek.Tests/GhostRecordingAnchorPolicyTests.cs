using Xunit;

namespace Parsek.Tests
{
    public class GhostRecordingAnchorPolicyTests
    {
        [Fact]
        public void ShouldRecordGhostAnchorCandidates_NoReFlyMarker_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldRecordGhostAnchorCandidates(null));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ShouldRecordGhostAnchorCandidates_MarkerWithoutActiveRecording_ReturnsFalse(string activeRecordingId)
        {
            var marker = new ReFlySessionMarker { ActiveReFlyRecordingId = activeRecordingId };

            Assert.False(ParsekFlight.ShouldRecordGhostAnchorCandidates(marker));
        }

        [Fact]
        public void ShouldRecordGhostAnchorCandidates_ActiveReFlyMarker_ReturnsTrue()
        {
            var marker = new ReFlySessionMarker { ActiveReFlyRecordingId = "rec-active-refly" };

            Assert.True(ParsekFlight.ShouldRecordGhostAnchorCandidates(marker));
        }
    }
}
