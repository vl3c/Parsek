using Xunit;

namespace Parsek.Tests
{
    public class TrajectorySidecarProbeVersionContractTests
    {
        [Fact]
        public void CurrentVersionPair_IsAccepted()
        {
            Assert.True(RecordingStore.IsAcceptableSidecarVersionLag(
                RecordingStore.CurrentRecordingFormatVersion,
                RecordingStore.CurrentRecordingFormatVersion));
        }

        [Fact]
        public void LegacyEqualVersionPair_IsRejected()
        {
            Assert.False(RecordingStore.IsAcceptableSidecarVersionLag(
                RecordingStore.CurrentRecordingFormatVersion - 1,
                RecordingStore.CurrentRecordingFormatVersion - 1));
        }

        [Fact]
        public void MismatchedVersionPair_IsRejected()
        {
            Assert.False(RecordingStore.IsAcceptableSidecarVersionLag(
                RecordingStore.CurrentRecordingFormatVersion - 1,
                RecordingStore.CurrentRecordingFormatVersion));
        }
    }
}
