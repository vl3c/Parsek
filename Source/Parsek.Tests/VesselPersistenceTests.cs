using Xunit;

namespace Parsek.Tests
{
    public class VesselPersistenceTests
    {
        [Fact]
        public void GetMergeDefault_IntactWithSnapshot_ReturnsPersist()
        {
            var result = RecordingStore.GetRecommendedAction(
                destroyed: false, hasSnapshot: true);

            Assert.Equal(MergeDefault.Persist, result);
        }

        [Fact]
        public void GetMergeDefault_DestroyedNoSnapshot_ReturnsGhostOnly()
        {
            var result = RecordingStore.GetRecommendedAction(
                destroyed: true, hasSnapshot: false);

            Assert.Equal(MergeDefault.GhostOnly, result);
        }

        [Fact]
        public void GetMergeDefault_DestroyedWithSnapshot_ReturnsGhostOnly()
        {
            // destroyed=true takes priority over hasSnapshot=true
            var result = RecordingStore.GetRecommendedAction(
                destroyed: true, hasSnapshot: true);

            Assert.Equal(MergeDefault.GhostOnly, result);
        }

        [Fact]
        public void GetMergeDefault_NotDestroyedNoSnapshot_ReturnsGhostOnly()
        {
            // Defensive: not destroyed but snapshot missing
            var result = RecordingStore.GetRecommendedAction(
                destroyed: false, hasSnapshot: false);

            Assert.Equal(MergeDefault.GhostOnly, result);
        }
    }
}
