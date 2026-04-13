using Xunit;

namespace Parsek.Tests
{
    public class PlaybackDistancePolicyTests
    {
        [Fact]
        public void TryResolvePlaybackDistanceReferencePosition_FlightView_PrefersSceneCamera()
        {
            bool resolved = ParsekFlight.TryResolvePlaybackDistanceReferencePosition(
                mapViewEnabled: false,
                cameraWorldPosition: new Vector3d(50, 60, 70),
                activeVesselWorldPosition: new Vector3d(5000, 6000, 7000),
                out Vector3d referencePosition);

            Assert.True(resolved);
            AssertVectorEquals(new Vector3d(50, 60, 70), referencePosition);
        }

        [Fact]
        public void TryResolvePlaybackDistanceReferencePosition_MapView_PrefersActiveVessel()
        {
            bool resolved = ParsekFlight.TryResolvePlaybackDistanceReferencePosition(
                mapViewEnabled: true,
                cameraWorldPosition: new Vector3d(50, 60, 70),
                activeVesselWorldPosition: new Vector3d(5000, 6000, 7000),
                out Vector3d referencePosition);

            Assert.True(resolved);
            AssertVectorEquals(new Vector3d(5000, 6000, 7000), referencePosition);
        }

        [Fact]
        public void TryResolvePlaybackDistanceReferencePosition_NoCamera_FallsBackToActiveVessel()
        {
            bool resolved = ParsekFlight.TryResolvePlaybackDistanceReferencePosition(
                mapViewEnabled: false,
                cameraWorldPosition: null,
                activeVesselWorldPosition: new Vector3d(5000, 6000, 7000),
                out Vector3d referencePosition);

            Assert.True(resolved);
            AssertVectorEquals(new Vector3d(5000, 6000, 7000), referencePosition);
        }

        [Fact]
        public void TryResolvePlaybackDistanceReferencePosition_NoActiveVessel_FallsBackToCamera()
        {
            bool resolved = ParsekFlight.TryResolvePlaybackDistanceReferencePosition(
                mapViewEnabled: false,
                cameraWorldPosition: new Vector3d(50, 60, 70),
                activeVesselWorldPosition: null,
                out Vector3d referencePosition);

            Assert.True(resolved);
            AssertVectorEquals(new Vector3d(50, 60, 70), referencePosition);
        }

        [Fact]
        public void TryResolvePlaybackDistanceReferencePosition_InvalidCamera_FallsBackToActiveVessel()
        {
            bool resolved = ParsekFlight.TryResolvePlaybackDistanceReferencePosition(
                mapViewEnabled: false,
                cameraWorldPosition: new Vector3d(double.NaN, 60, 70),
                activeVesselWorldPosition: new Vector3d(5000, 6000, 7000),
                out Vector3d referencePosition);

            Assert.True(resolved);
            AssertVectorEquals(new Vector3d(5000, 6000, 7000), referencePosition);
        }

        [Fact]
        public void TryResolvePlaybackDistanceReferencePosition_InvalidInputs_ReturnsFalse()
        {
            bool resolved = ParsekFlight.TryResolvePlaybackDistanceReferencePosition(
                mapViewEnabled: false,
                cameraWorldPosition: new Vector3d(double.NaN, 60, 70),
                activeVesselWorldPosition: new Vector3d(5000, double.PositiveInfinity, 7000),
                out Vector3d referencePosition);

            Assert.False(resolved);
            AssertVectorEquals(Vector3d.zero, referencePosition);
        }

        private static void AssertVectorEquals(Vector3d expected, Vector3d actual)
        {
            Assert.Equal(expected.x, actual.x, 12);
            Assert.Equal(expected.y, actual.y, 12);
            Assert.Equal(expected.z, actual.z, 12);
        }
    }
}
