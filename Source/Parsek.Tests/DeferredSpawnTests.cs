using Xunit;

namespace Parsek.Tests
{
    public class DeferredSpawnTests
    {
        #region ShouldFlushDeferredSpawns

        [Fact]
        public void ShouldFlush_PendingAndWarpInactive_ReturnsTrue()
        {
            Assert.True(ParsekFlight.ShouldFlushDeferredSpawns(3, false));
        }

        [Fact]
        public void ShouldFlush_PendingAndWarpActive_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldFlushDeferredSpawns(3, true));
        }

        [Fact]
        public void ShouldFlush_EmptyAndWarpInactive_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldFlushDeferredSpawns(0, false));
        }

        [Fact]
        public void ShouldFlush_EmptyAndWarpActive_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldFlushDeferredSpawns(0, true));
        }

        #endregion

        #region ShouldSkipDeferredSpawn

        [Fact]
        public void ShouldSkip_AlreadySpawned_ReturnsTrue()
        {
            Assert.True(ParsekFlight.ShouldSkipDeferredSpawn(true, true));
        }

        [Fact]
        public void ShouldSkip_NoSnapshot_ReturnsTrue()
        {
            Assert.True(ParsekFlight.ShouldSkipDeferredSpawn(false, false));
        }

        [Fact]
        public void ShouldSkip_SpawnedAndNoSnapshot_ReturnsTrue()
        {
            Assert.True(ParsekFlight.ShouldSkipDeferredSpawn(true, false));
        }

        [Fact]
        public void ShouldSkip_NotSpawnedWithSnapshot_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldSkipDeferredSpawn(false, true));
        }

        #endregion

        #region ShouldRestoreWatchMode

        [Fact]
        public void ShouldRestoreWatch_MatchingIdWithPid_ReturnsTrue()
        {
            Assert.True(ParsekFlight.ShouldRestoreWatchMode("rec-123", "rec-123", 42000));
        }

        [Fact]
        public void ShouldRestoreWatch_MatchingIdZeroPid_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldRestoreWatchMode("rec-123", "rec-123", 0));
        }

        [Fact]
        public void ShouldRestoreWatch_DifferentId_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldRestoreWatchMode("rec-123", "rec-456", 42000));
        }

        [Fact]
        public void ShouldRestoreWatch_NullPendingId_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldRestoreWatchMode(null, "rec-123", 42000));
        }

        [Fact]
        public void ShouldRestoreWatch_BothNull_ReturnsFalse()
        {
            // null == null is true in C#, but pid must be non-zero
            Assert.False(ParsekFlight.ShouldRestoreWatchMode(null, null, 0));
        }

        [Fact]
        public void ShouldRestoreWatch_BothNullWithPid_ReturnsTrue()
        {
            // Edge case: null == null is true, pid non-zero
            Assert.True(ParsekFlight.ShouldRestoreWatchMode(null, null, 42000));
        }

        #endregion
    }
}
