using Xunit;

namespace Parsek.Tests
{
    public class VesselPersistenceTests
    {
        [Fact]
        public void GetMergeDefault_ShortDistance_ReturnsRecover()
        {
            var result = RecordingStore.GetRecommendedAction(
                distance: 50, destroyed: false, hasSnapshot: true);

            Assert.Equal(RecordingStore.MergeDefault.Recover, result);
        }

        [Fact]
        public void GetMergeDefault_ZeroDistance_ReturnsRecover()
        {
            var result = RecordingStore.GetRecommendedAction(
                distance: 0, destroyed: false, hasSnapshot: true);

            Assert.Equal(RecordingStore.MergeDefault.Recover, result);
        }

        [Fact]
        public void GetMergeDefault_ShortDistance_DestroyedStillRecovers()
        {
            // Even if destroyed, short distance means recover
            var result = RecordingStore.GetRecommendedAction(
                distance: 10, destroyed: true, hasSnapshot: false);

            Assert.Equal(RecordingStore.MergeDefault.Recover, result);
        }

        [Fact]
        public void GetMergeDefault_FarAndDestroyed_ReturnsMergeOnly()
        {
            var result = RecordingStore.GetRecommendedAction(
                distance: 500, destroyed: true, hasSnapshot: false);

            Assert.Equal(RecordingStore.MergeDefault.MergeOnly, result);
        }

        [Fact]
        public void GetMergeDefault_FarAndIntact_ReturnsPersist()
        {
            var result = RecordingStore.GetRecommendedAction(
                distance: 500, destroyed: false, hasSnapshot: true);

            Assert.Equal(RecordingStore.MergeDefault.Persist, result);
        }

        [Fact]
        public void GetMergeDefault_ExactThreshold_ReturnsPersist()
        {
            // distance == 100m is >= 100, so should be Persist
            var result = RecordingStore.GetRecommendedAction(
                distance: 100, destroyed: false, hasSnapshot: true);

            Assert.Equal(RecordingStore.MergeDefault.Persist, result);
        }

        [Fact]
        public void GetMergeDefault_JustBelowThreshold_ReturnsRecover()
        {
            var result = RecordingStore.GetRecommendedAction(
                distance: 99.99, destroyed: false, hasSnapshot: true);

            Assert.Equal(RecordingStore.MergeDefault.Recover, result);
        }

        [Fact]
        public void GetMergeDefault_NoSnapshot_FarNotDestroyed_ReturnsMergeOnly()
        {
            // Defensive: far + not destroyed but snapshot missing
            var result = RecordingStore.GetRecommendedAction(
                distance: 500, destroyed: false, hasSnapshot: false);

            Assert.Equal(RecordingStore.MergeDefault.MergeOnly, result);
        }

        // --- Returned-to-pad tests ---

        [Fact]
        public void GetMergeDefault_ShortDistance_LongDuration_HighMaxDist_ReturnsPersist()
        {
            // Real mission that returned to pad — should persist
            var result = RecordingStore.GetRecommendedAction(
                distance: 50, destroyed: false, hasSnapshot: true,
                duration: 60, maxDistance: 5000);

            Assert.Equal(RecordingStore.MergeDefault.Persist, result);
        }

        [Fact]
        public void GetMergeDefault_ShortDistance_LongDuration_LowMaxDist_ReturnsRecover()
        {
            // Sat on pad for a while but didn't go anywhere
            var result = RecordingStore.GetRecommendedAction(
                distance: 50, destroyed: false, hasSnapshot: true,
                duration: 60, maxDistance: 50);

            Assert.Equal(RecordingStore.MergeDefault.Recover, result);
        }

        [Fact]
        public void GetMergeDefault_ShortDistance_ShortDuration_HighMaxDist_ReturnsRecover()
        {
            // Quick bounce — short duration means recover even with high max distance
            var result = RecordingStore.GetRecommendedAction(
                distance: 50, destroyed: false, hasSnapshot: true,
                duration: 5, maxDistance: 200);

            Assert.Equal(RecordingStore.MergeDefault.Recover, result);
        }

        [Fact]
        public void GetMergeDefault_ShortDistance_LongDuration_ExactMaxDistThreshold_ReturnsRecover()
        {
            // maxDistance exactly 100 is <= 100, so Recover
            var result = RecordingStore.GetRecommendedAction(
                distance: 50, destroyed: false, hasSnapshot: true,
                duration: 60, maxDistance: 100);

            Assert.Equal(RecordingStore.MergeDefault.Recover, result);
        }

        [Fact]
        public void GetMergeDefault_ShortDistance_ExactDurationThreshold_HighMaxDist_ReturnsRecover()
        {
            // duration exactly 10 is <= 10, so Recover
            var result = RecordingStore.GetRecommendedAction(
                distance: 50, destroyed: false, hasSnapshot: true,
                duration: 10, maxDistance: 5000);

            Assert.Equal(RecordingStore.MergeDefault.Recover, result);
        }

        [Fact]
        public void GetMergeDefault_ShortDistance_JustAboveBothThresholds_ReturnsPersist()
        {
            // Both duration > 10 and maxDistance > 100 → Persist
            var result = RecordingStore.GetRecommendedAction(
                distance: 50, destroyed: false, hasSnapshot: true,
                duration: 10.1, maxDistance: 100.1);

            Assert.Equal(RecordingStore.MergeDefault.Persist, result);
        }

        [Fact]
        public void GetMergeDefault_NegativeDistance_TreatsAsRecover()
        {
            // Negative distance (shouldn't happen, but test defensive behavior)
            var result = RecordingStore.GetRecommendedAction(
                distance: -50, destroyed: false, hasSnapshot: true);

            Assert.Equal(RecordingStore.MergeDefault.Recover, result);
        }

        [Fact]
        public void GetMergeDefault_ZeroDistance_Destroyed_Recovers()
        {
            var result = RecordingStore.GetRecommendedAction(
                distance: 0, destroyed: true, hasSnapshot: false);

            Assert.Equal(RecordingStore.MergeDefault.Recover, result);
        }

        [Fact]
        public void GetMergeDefault_BoundaryDuration_ExactlyTen()
        {
            // duration=10.0 exactly is <= 10, so still Recover
            var result = RecordingStore.GetRecommendedAction(
                distance: 50, destroyed: false, hasSnapshot: true,
                duration: 10.0, maxDistance: 5000);

            Assert.Equal(RecordingStore.MergeDefault.Recover, result);
        }

        [Fact]
        public void GetMergeDefault_LargeDistance_AllFlags()
        {
            // Large distance + destroyed + hasSnapshot → destroyed takes priority → MergeOnly
            var result = RecordingStore.GetRecommendedAction(
                distance: 50000, destroyed: true, hasSnapshot: true);

            Assert.Equal(RecordingStore.MergeDefault.MergeOnly, result);
        }
    }
}
