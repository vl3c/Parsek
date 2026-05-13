using Xunit;

namespace Parsek.Tests
{
    public class FlightRecorderFormatVersionTests
    {
        [Fact]
        public void ResolveRelativeContractUpgradeTarget_PidOnlyRelativeSections_StopAtV10()
        {
            int target = FlightRecorder.ResolveRelativeContractUpgradeTarget(
                RecordingStore.CurrentRecordingFormatVersion,
                hasRelativeTrackSections: true);

            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, target);
        }

        [Fact]
        public void ResolveRelativeContractUpgradeTarget_NoRelativeSections_UsesCurrent()
        {
            int target = FlightRecorder.ResolveRelativeContractUpgradeTarget(
                RecordingStore.CurrentRecordingFormatVersion,
                hasRelativeTrackSections: false);

            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, target);
        }
    }
}
