using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GhostTrackingStationPatchTests
    {
        [Fact]
        public void TryClearSelectedVessel_WithPrivateSelection_ClearsAndReturnsPrevious()
        {
            var selected = new object();
            var tracking = new FakeTrackingStation(selected);

            bool cleared = GhostTrackingStationSelection.TryClearSelectedVessel(
                tracking,
                out object previousSelection,
                out string error);

            Assert.True(cleared);
            Assert.Same(selected, previousSelection);
            Assert.Null(tracking.SelectedForTesting);
            Assert.Null(error);
        }

        [Fact]
        public void TryClearSelectedVessel_WithNoSelection_ReturnsFalseWithoutError()
        {
            var tracking = new FakeTrackingStation(null);

            bool cleared = GhostTrackingStationSelection.TryClearSelectedVessel(
                tracking,
                out object previousSelection,
                out string error);

            Assert.False(cleared);
            Assert.Null(previousSelection);
            Assert.Null(tracking.SelectedForTesting);
            Assert.Null(error);
        }

        [Fact]
        public void TryClearSelectedVessel_WithoutPrivateField_ReportsReflectionError()
        {
            bool cleared = GhostTrackingStationSelection.TryClearSelectedVessel(
                new object(),
                out object previousSelection,
                out string error);

            Assert.False(cleared);
            Assert.Null(previousSelection);
            Assert.Equal("selectedVessel field not found", error);
        }

        private sealed class FakeTrackingStation
        {
            private object selectedVessel;

            public FakeTrackingStation(object selectedVessel)
            {
                this.selectedVessel = selectedVessel;
            }

            public object SelectedForTesting => selectedVessel;
        }
    }
}
