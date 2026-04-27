using Xunit;

namespace Parsek.Tests
{
    public class TimelineWindowUITests
    {
        [Fact]
        public void GetRowActionButtonWidth_ShortTimelineActionsShareWidth_AndGoToStaysWider()
        {
            float watch = TimelineWindowUI.GetRowActionButtonWidth(TimelineWindowUI.TimelineRowActionButtonKind.Watch);
            float fastForward = TimelineWindowUI.GetRowActionButtonWidth(TimelineWindowUI.TimelineRowActionButtonKind.FastForward);
            float rewind = TimelineWindowUI.GetRowActionButtonWidth(TimelineWindowUI.TimelineRowActionButtonKind.Rewind);
            float loop = TimelineWindowUI.GetRowActionButtonWidth(TimelineWindowUI.TimelineRowActionButtonKind.Loop);
            float goTo = TimelineWindowUI.GetRowActionButtonWidth(TimelineWindowUI.TimelineRowActionButtonKind.GoTo);

            Assert.Equal(35f, watch);
            Assert.Equal(watch, fastForward);
            Assert.Equal(watch, rewind);
            Assert.Equal(watch, loop);
            Assert.Equal(48f, goTo);
            Assert.True(goTo > watch);
        }

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
        public void ShouldShowLoopToggle_PastLoopableUnfinishedFlight_ReturnsFalse()
        {
            var rec = new Recording { SegmentPhase = "atmo" };

            Assert.False(TimelineWindowUI.ShouldShowLoopToggle(
                rec, isFuture: false, isUnfinishedFlight: true));
        }

        [Fact]
        public void ShouldShowLoopToggle_PastActiveLoopUnfinishedFlight_ReturnsFalse()
        {
            var rec = new Recording { LoopPlayback = true };

            Assert.False(TimelineWindowUI.ShouldShowLoopToggle(
                rec, isFuture: false, isUnfinishedFlight: true));
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
        public void ShouldShowWatchButton_InFlightRecording_ReturnsTrue()
        {
            Assert.True(TimelineWindowUI.ShouldShowWatchButton(
                inFlightMode: true, rec: new Recording()));
        }

        [Fact]
        public void ShouldShowWatchButton_NonFlightRecording_ReturnsFalse()
        {
            Assert.False(TimelineWindowUI.ShouldShowWatchButton(
                inFlightMode: false, rec: new Recording()));
        }

        [Fact]
        public void BuildWatchButtonDescriptor_EligibleRow_IsEnabledForEnter()
        {
            var descriptor = TimelineWindowUI.BuildWatchButtonDescriptor(
                isWatching: false, hasGhost: true, sameBody: true, inRange: true, isDebris: false);

            Assert.Equal("W", descriptor.Label);
            Assert.Equal("Follow ghost in watch mode", descriptor.Tooltip);
            Assert.True(descriptor.Enabled);
            Assert.True(descriptor.CanWatch);
            Assert.Equal(TimelineWindowUI.TimelineWatchButtonAction.Enter, descriptor.Action);
        }

        [Fact]
        public void BuildRecordingIndexLookup_MapsRecordingIdsToIndices()
        {
            var lookup = TimelineWindowUI.BuildRecordingIndexLookup(new[]
            {
                new Recording { RecordingId = "rec-a" },
                new Recording { RecordingId = string.Empty },
                new Recording { RecordingId = "rec-b" }
            });

            Assert.Equal(0, lookup["rec-a"]);
            Assert.Equal(2, lookup["rec-b"]);
            Assert.False(lookup.ContainsKey(string.Empty));
        }

        [Fact]
        public void BuildRecordingIndexLookup_NullRecordings_ReturnsEmpty()
        {
            var lookup = TimelineWindowUI.BuildRecordingIndexLookup(null);

            Assert.Empty(lookup);
        }

        [Fact]
        public void BuildWatchButtonDescriptor_WatchedUnavailableRow_UsesExitTooltipAndStaysEnabled()
        {
            var descriptor = TimelineWindowUI.BuildWatchButtonDescriptor(
                isWatching: true, hasGhost: false, sameBody: false, inRange: false, isDebris: false);

            Assert.Equal("W*", descriptor.Label);
            Assert.Equal("Exit watch mode", descriptor.Tooltip);
            Assert.True(descriptor.Enabled);
            Assert.False(descriptor.CanWatch);
            Assert.Equal(TimelineWindowUI.TimelineWatchButtonAction.Exit, descriptor.Action);
        }

        [Fact]
        public void GetWatchButtonAction_TogglesWatchedRowsOff()
        {
            Assert.Equal(TimelineWindowUI.TimelineWatchButtonAction.Enter,
                TimelineWindowUI.GetWatchButtonAction(isWatching: false));
            Assert.Equal(TimelineWindowUI.TimelineWatchButtonAction.Exit,
                TimelineWindowUI.GetWatchButtonAction(isWatching: true));
        }

        [Fact]
        public void ApplyWatchButtonAction_ExitAction_CallsExitOnly()
        {
            bool exitCalled = false;
            int enteredIndex = -1;

            TimelineWindowUI.ApplyWatchButtonAction(
                TimelineWindowUI.TimelineWatchButtonAction.Exit,
                recIndex: 7,
                exitWatchMode: () => exitCalled = true,
                enterWatchMode: index => enteredIndex = index);

            Assert.True(exitCalled);
            Assert.Equal(-1, enteredIndex);
        }

        [Fact]
        public void ApplyWatchButtonAction_EnterAction_CallsEnterWithRecordingIndex()
        {
            bool exitCalled = false;
            int enteredIndex = -1;

            TimelineWindowUI.ApplyWatchButtonAction(
                TimelineWindowUI.TimelineWatchButtonAction.Enter,
                recIndex: 7,
                exitWatchMode: () => exitCalled = true,
                enterWatchMode: index => enteredIndex = index);

            Assert.False(exitCalled);
            Assert.Equal(7, enteredIndex);
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
