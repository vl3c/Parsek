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
                toggleIfAlreadyWatching: true);

            Assert.Equal(GhostMapWatchAction.DifferentBody, result);
        }

        [Fact]
        public void ResolveWatchAction_ActiveGhostOnSameBody_ReturnsStart()
        {
            GhostMapWatchAction result = GhostMapWatchHelper.ResolveWatchAction(
                recordingIndex: 2,
                watchedRecordingIndex: 1,
                hasActiveGhost: true,
                sameBody: true,
                toggleIfAlreadyWatching: true);

            Assert.Equal(GhostMapWatchAction.Start, result);
        }
    }
}
