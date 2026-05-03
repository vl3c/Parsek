using Xunit;

namespace Parsek.Tests
{
    public class FlightRecorderFormatVersionTests
    {
        [Fact]
        public void ResolveRelativeContractUpgradeTarget_PidOnlyRelativeSections_StopAtV10()
        {
            int target = FlightRecorder.ResolveRelativeContractUpgradeTarget(
                RecordingStore.StructuralEventFlagFormatVersion,
                hasRelativeTrackSections: true);

            Assert.Equal(RecordingStore.StructuralEventFlagFormatVersion, target);
        }

        [Fact]
        public void ResolveRelativeContractUpgradeTarget_NoRelativeSections_UsesCurrent()
        {
            int target = FlightRecorder.ResolveRelativeContractUpgradeTarget(
                RecordingStore.StructuralEventFlagFormatVersion,
                hasRelativeTrackSections: false);

            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, target);
        }
    }
}
