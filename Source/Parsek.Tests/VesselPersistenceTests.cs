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
    }
}
