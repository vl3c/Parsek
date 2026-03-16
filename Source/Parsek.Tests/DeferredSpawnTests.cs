using Xunit;

namespace Parsek.Tests
{
    public class DeferredSpawnTests
    {
        #region ShouldFlushDeferredSpawns

        [Fact]
        public void ShouldFlush_PendingAndWarpInactive_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldFlushDeferredSpawns(3, false));
        }

        [Fact]
        public void ShouldFlush_PendingAndWarpActive_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldFlushDeferredSpawns(3, true));
        }

        [Fact]
        public void ShouldFlush_EmptyAndWarpInactive_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldFlushDeferredSpawns(0, false));
        }

        [Fact]
        public void ShouldFlush_EmptyAndWarpActive_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldFlushDeferredSpawns(0, true));
        }

        #endregion

        #region ShouldSkipDeferredSpawn

        [Fact]
        public void ShouldSkip_AlreadySpawned_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldSkipDeferredSpawn(true, true));
        }

        [Fact]
        public void ShouldSkip_NoSnapshot_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldSkipDeferredSpawn(false, false));
        }

        [Fact]
        public void ShouldSkip_SpawnedAndNoSnapshot_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldSkipDeferredSpawn(true, false));
        }

        [Fact]
        public void ShouldSkip_NotSpawnedWithSnapshot_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldSkipDeferredSpawn(false, true));
        }

        #endregion

        #region ShouldRestoreWatchMode

        [Fact]
        public void ShouldRestoreWatch_MatchingIdWithPid_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldRestoreWatchMode("rec-123", "rec-123", 42000));
        }

        [Fact]
        public void ShouldRestoreWatch_MatchingIdZeroPid_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldRestoreWatchMode("rec-123", "rec-123", 0));
        }

        [Fact]
        public void ShouldRestoreWatch_DifferentId_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldRestoreWatchMode("rec-123", "rec-456", 42000));
        }

        [Fact]
        public void ShouldRestoreWatch_NullPendingId_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldRestoreWatchMode(null, "rec-123", 42000));
        }

        [Fact]
        public void ShouldRestoreWatch_BothNull_ReturnsFalse()
        {
            // null == null is true in C#, but pid must be non-zero
            Assert.False(GhostPlaybackLogic.ShouldRestoreWatchMode(null, null, 0));
        }

        [Fact]
        public void ShouldRestoreWatch_BothNullWithPid_ReturnsFalse()
        {
            // Defensive: null pendingWatchId means no watch was active
            Assert.False(GhostPlaybackLogic.ShouldRestoreWatchMode(null, null, 42000));
        }

        #endregion
    }
}
