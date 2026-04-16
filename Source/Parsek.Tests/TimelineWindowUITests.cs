using Xunit;

namespace Parsek.Tests
{
    public class TimelineWindowUITests
    {
        [Fact]
        public void ShouldShowLoopToggle_FutureRecording_ReturnsFalse()
        {
            var rec = new Recording { LaunchSiteName = "LaunchPad" };

            Assert.False(TimelineWindowUI.ShouldShowLoopToggle(rec, isFuture: true));
        }

        [Fact]
        public void ShouldShowLoopToggle_PastLoopableRecording_ReturnsTrue()
        {
            var rec = new Recording { SegmentPhase = "atmo" };

            Assert.True(TimelineWindowUI.ShouldShowLoopToggle(rec, isFuture: false));
        }

        [Fact]
        public void ShouldShowLoopToggle_PastActiveLoopOutsideHeuristic_ReturnsTrue()
        {
            var rec = new Recording { LoopPlayback = true };

            Assert.True(TimelineWindowUI.ShouldShowLoopToggle(rec, isFuture: false));
        }

        [Fact]
        public void ShouldShowLoopToggle_PastNonLoopableInactiveRecording_ReturnsFalse()
        {
            var rec = new Recording();

            Assert.False(TimelineWindowUI.ShouldShowLoopToggle(rec, isFuture: false));
        }
    }
}
