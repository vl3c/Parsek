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
        public void ShouldEnableWatchButton_CurrentlyWatchedUnavailableRow_ReturnsTrue()
        {
            Assert.True(TimelineWindowUI.ShouldEnableWatchButton(
                canWatch: false, isWatching: true));
            Assert.False(TimelineWindowUI.ShouldEnableWatchButton(
                canWatch: false, isWatching: false));
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
        public void GetWatchButtonAction_TogglesWatchedRowsOff()
        {
            Assert.Equal(TimelineWindowUI.TimelineWatchButtonAction.Enter,
                TimelineWindowUI.GetWatchButtonAction(isWatching: false));
            Assert.Equal(TimelineWindowUI.TimelineWatchButtonAction.Exit,
                TimelineWindowUI.GetWatchButtonAction(isWatching: true));
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

        [Fact]
        public void DrawEntryRow_AddsWatchButtonBeforeRewindAndFastForward_PinnedBySourceInspection()
        {
            string srcRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "Parsek"));
            string uiSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(srcRoot, "UI", "TimelineWindowUI.cs"));

            int watchIndex = uiSrc.IndexOf("// Watch button - flight only.", System.StringComparison.Ordinal);
            int fastForwardIndex = uiSrc.IndexOf("// Future recording: FF button", System.StringComparison.Ordinal);

            Assert.True(watchIndex >= 0, "timeline watch-button block should exist");
            Assert.True(fastForwardIndex > watchIndex,
                "timeline watch button should be rendered before the rewind/fast-forward buttons");
        }

        [Fact]
        public void DrawEntryRow_WatchButton_WiresToggleAndEnabledState_PinnedBySourceInspection()
        {
            string srcRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "Parsek"));
            string uiSrc = System.IO.File.ReadAllText(
                System.IO.Path.Combine(srcRoot, "UI", "TimelineWindowUI.cs"));
            int watchIndex = uiSrc.IndexOf("// Watch button - flight only.", System.StringComparison.Ordinal);
            int fastForwardIndex = uiSrc.IndexOf("bool showFastForward = ShouldShowFastForwardButton", System.StringComparison.Ordinal);
            string watchBlock = uiSrc.Substring(watchIndex, fastForwardIndex - watchIndex);

            Assert.Contains("TimelineWatchButtonAction watchAction = GetWatchButtonAction(isWatching);", watchBlock);
            Assert.Contains("GUI.enabled = ShouldEnableWatchButton(canWatch, isWatching);", watchBlock);
            Assert.Contains("if (watchAction == TimelineWatchButtonAction.Exit)", watchBlock);
            Assert.Contains("flight.EnterWatchMode(recIndex);", watchBlock);
        }
    }
}
