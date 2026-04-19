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

        [Fact]
        public void ShouldShowFastForwardButton_FutureRecording_ReturnsTrue()
        {
            var rec = new Recording();

            Assert.True(TimelineWindowUI.ShouldShowFastForwardButton(rec, isFuture: true));
        }

        [Fact]
        public void ShouldShowRewindButton_PastRecordingWithSave_ReturnsTrue()
        {
            var rec = new Recording { RewindSaveFileName = "rewind.sfs" };

            Assert.True(TimelineWindowUI.ShouldShowRewindButton(rec, isFuture: false));
        }

        [Fact]
        public void HasActionableRewindOrFastForwardButton_FutureRecordingStart_ReturnsTrue()
        {
            var entry = new TimelineEntry
            {
                Type = TimelineEntryType.RecordingStart,
                UT = 200,
                RecordingId = "rec-1"
            };
            var rec = new Recording { RecordingId = "rec-1" };

            Assert.True(TimelineWindowUI.HasActionableRewindOrFastForwardButton(
                entry, rec, currentUT: 100, canFastForward: true, canRewind: false));
        }

        [Fact]
        public void HasActionableRewindOrFastForwardButton_FutureRecordingStartWithoutAvailableFastForward_ReturnsFalse()
        {
            var entry = new TimelineEntry
            {
                Type = TimelineEntryType.RecordingStart,
                UT = 200,
                RecordingId = "rec-1"
            };
            var rec = new Recording { RecordingId = "rec-1" };

            Assert.False(TimelineWindowUI.HasActionableRewindOrFastForwardButton(
                entry, rec, currentUT: 100, canFastForward: false, canRewind: false));
        }

        [Fact]
        public void HasActionableRewindOrFastForwardButton_PastRecordingWithoutRewindSave_ReturnsFalse()
        {
            var entry = new TimelineEntry
            {
                Type = TimelineEntryType.RecordingStart,
                UT = 100,
                RecordingId = "rec-1"
            };
            var rec = new Recording { RecordingId = "rec-1" };

            Assert.False(TimelineWindowUI.HasActionableRewindOrFastForwardButton(
                entry, rec, currentUT: 200, canFastForward: false, canRewind: true));
        }

        [Fact]
        public void HasActionableRewindOrFastForwardButton_PastRecordingWithDisabledRewind_ReturnsFalse()
        {
            var entry = new TimelineEntry
            {
                Type = TimelineEntryType.RecordingStart,
                UT = 100,
                RecordingId = "rec-1"
            };
            var rec = new Recording
            {
                RecordingId = "rec-1",
                RewindSaveFileName = "rewind.sfs"
            };

            Assert.False(TimelineWindowUI.HasActionableRewindOrFastForwardButton(
                entry, rec, currentUT: 200, canFastForward: false, canRewind: false));
        }
    }
}
