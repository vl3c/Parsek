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
                bodyWatchable: false,
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
                bodyWatchable: true,
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
                bodyWatchable: true,
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
                bodyWatchable: true,
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
                bodyWatchable: false,
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
                bodyWatchable: true,
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
                bodyWatchable: true,
                inRange: true,
                toggleIfAlreadyWatching: true);

            Assert.Equal(GhostMapWatchAction.Start, result);
        }

        [Fact]
        public void IsWatchActionEnabled_OutOfRange_ReturnsFalse()
        {
            Assert.False(GhostMapWatchHelper.IsWatchActionEnabled(GhostMapWatchAction.OutOfRange));
        }

        // Step 4 (design-far-watch.md 12.2): "Watch (Too Far)" and "Watch (Other SOI)" are a
        // plain Watch, greyed with the far watch's reason as the hover.
        [Theory]
        [InlineData((int)GhostMapWatchAction.OutOfRange, "Watch")]
        [InlineData((int)GhostMapWatchAction.DifferentBody, "Watch")]
        [InlineData((int)GhostMapWatchAction.Start, "Watch")]
        [InlineData((int)GhostMapWatchAction.NoActiveGhost, "Watch (Inactive)")]
        [InlineData((int)GhostMapWatchAction.Stop, "Stop Watching")]
        [InlineData((int)GhostMapWatchAction.Refresh, "Refresh Watch")]
        public void GetWatchButtonLabel_NoTooFarOrOtherSoiVariants(int action, string label)
        {
            Assert.Equal(label, GhostMapWatchHelper.GetWatchButtonLabel((GhostMapWatchAction)action));
        }

        [Fact]
        public void GetWatchButtonReason_FarRefusalFirst_ThenTheBodyOrRangeLine()
        {
            Assert.Equal("ship unsafe", GhostMapWatchHelper.GetWatchButtonReason(GhostMapWatchAction.OutOfRange, "ship unsafe"));
            Assert.Equal(GhostMapWatchHelper.TooFarReasonText, GhostMapWatchHelper.GetWatchButtonReason(GhostMapWatchAction.OutOfRange, null));
            Assert.Equal(GhostMapWatchHelper.OtherBodyReasonText, GhostMapWatchHelper.GetWatchButtonReason(GhostMapWatchAction.DifferentBody, "x"));
            Assert.Equal(string.Empty, GhostMapWatchHelper.GetWatchButtonReason(GhostMapWatchAction.Start, "x"));
        }

        // A ghost at another body with the far watch available reads bodyWatchable=true, and
        // a far start the ship's safety allows reads inRange=true: a plain enabled Watch.
        [Fact]
        public void ResolveWatchAction_FarGhostAtAnotherBody_IsAStart()
        {
            GhostMapWatchAction result = GhostMapWatchHelper.ResolveWatchAction(
                recordingIndex: 4, watchedRecordingIndex: -1, hasActiveGhost: true,
                bodyWatchable: true, inRange: true, toggleIfAlreadyWatching: true);
            Assert.Equal(GhostMapWatchAction.Start, result);
            Assert.True(GhostMapWatchHelper.IsWatchActionEnabled(result));
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
