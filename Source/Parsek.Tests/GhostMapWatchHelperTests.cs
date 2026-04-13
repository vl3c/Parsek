using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    public class GhostMapWatchHelperTests
    {
        [Fact]
        public void ResolveWatchAction_InvalidRecording_ReturnsUnavailable()
        {
            GhostMapWatchAction result = GhostMapWatchHelper.ResolveWatchAction(
                recordingIndex: -1,
                watchedRecordingIndex: -1,
                hasActiveGhost: false,
                sameBody: false,
                inRange: false,
                toggleIfAlreadyWatching: true);

            Assert.Equal(GhostMapWatchAction.Unavailable, result);
        }

        [Fact]
        public void ResolveWatchAction_MenuClickOnWatchedGhost_ReturnsStop()
        {
            GhostMapWatchAction result = GhostMapWatchHelper.ResolveWatchAction(
                recordingIndex: 4,
                watchedRecordingIndex: 4,
                hasActiveGhost: true,
                sameBody: true,
                inRange: true,
                toggleIfAlreadyWatching: true);

            Assert.Equal(GhostMapWatchAction.Stop, result);
        }

        [Fact]
        public void ResolveWatchAction_DoubleClickOnWatchedGhost_ReturnsRefresh()
        {
            GhostMapWatchAction result = GhostMapWatchHelper.ResolveWatchAction(
                recordingIndex: 4,
                watchedRecordingIndex: 4,
                hasActiveGhost: true,
                sameBody: true,
                inRange: true,
                toggleIfAlreadyWatching: false);

            Assert.Equal(GhostMapWatchAction.Refresh, result);
        }

        [Fact]
        public void ResolveWatchAction_InactiveGhost_ReturnsNoActiveGhost()
        {
            GhostMapWatchAction result = GhostMapWatchHelper.ResolveWatchAction(
                recordingIndex: 2,
                watchedRecordingIndex: -1,
                hasActiveGhost: false,
                sameBody: true,
                inRange: true,
                toggleIfAlreadyWatching: true);

            Assert.Equal(GhostMapWatchAction.NoActiveGhost, result);
        }

        [Fact]
        public void ResolveWatchAction_DifferentBody_ReturnsDifferentBody()
        {
            GhostMapWatchAction result = GhostMapWatchHelper.ResolveWatchAction(
                recordingIndex: 2,
                watchedRecordingIndex: -1,
                hasActiveGhost: true,
                sameBody: false,
                inRange: true,
                toggleIfAlreadyWatching: true);

            Assert.Equal(GhostMapWatchAction.DifferentBody, result);
        }

        [Fact]
        public void ResolveWatchAction_OutOfRangeGhost_ReturnsOutOfRange()
        {
            GhostMapWatchAction result = GhostMapWatchHelper.ResolveWatchAction(
                recordingIndex: 2,
                watchedRecordingIndex: -1,
                hasActiveGhost: true,
                sameBody: true,
                inRange: false,
                toggleIfAlreadyWatching: true);

            Assert.Equal(GhostMapWatchAction.OutOfRange, result);
        }

        [Fact]
        public void ResolveWatchAction_ActiveGhostOnSameBodyInRange_ReturnsStart()
        {
            GhostMapWatchAction result = GhostMapWatchHelper.ResolveWatchAction(
                recordingIndex: 2,
                watchedRecordingIndex: 1,
                hasActiveGhost: true,
                sameBody: true,
                inRange: true,
                toggleIfAlreadyWatching: true);

            Assert.Equal(GhostMapWatchAction.Start, result);
        }

        [Fact]
        public void IsWatchActionEnabled_OutOfRange_ReturnsFalse()
        {
            Assert.False(GhostMapWatchHelper.IsWatchActionEnabled(GhostMapWatchAction.OutOfRange));
        }

        [Fact]
        public void IsWatchActionEnabled_StartAndStop_ReturnTrue()
        {
            Assert.True(GhostMapWatchHelper.IsWatchActionEnabled(GhostMapWatchAction.Start));
            Assert.True(GhostMapWatchHelper.IsWatchActionEnabled(GhostMapWatchAction.Stop));
            Assert.True(GhostMapWatchHelper.IsWatchActionEnabled(GhostMapWatchAction.Refresh));
        }
    }
}
