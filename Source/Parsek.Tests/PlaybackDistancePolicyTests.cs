using Xunit;

namespace Parsek.Tests
{
    public class PlaybackDistancePolicyTests
    {
        [Fact]
        public void TryResolvePlaybackDistanceReferencePosition_PrefersCameraAnchorOverCamera()
        {
            // The LOD radius must be centered on the camera anchor (real vessel / watched
            // ghost), NOT the zoomable camera transform — even when both are available.
            bool resolved = ParsekFlight.TryResolvePlaybackDistanceReferencePosition(
                cameraAnchorWorldPosition: new Vector3d(5000, 6000, 7000),
                cameraWorldPosition: new Vector3d(50, 60, 70),
                out Vector3d referencePosition);

            Assert.True(resolved);
            AssertVectorEquals(new Vector3d(5000, 6000, 7000), referencePosition);
        }

        [Fact]
        public void TryResolvePlaybackDistanceReferencePosition_AnchorOnly_Resolves()
        {
            bool resolved = ParsekFlight.TryResolvePlaybackDistanceReferencePosition(
                cameraAnchorWorldPosition: new Vector3d(5000, 6000, 7000),
                cameraWorldPosition: null,
                out Vector3d referencePosition);

            Assert.True(resolved);
            AssertVectorEquals(new Vector3d(5000, 6000, 7000), referencePosition);
        }

        [Fact]
        public void TryResolvePlaybackDistanceReferencePosition_NoAnchor_FallsBackToCamera()
        {
            bool resolved = ParsekFlight.TryResolvePlaybackDistanceReferencePosition(
                cameraAnchorWorldPosition: null,
                cameraWorldPosition: new Vector3d(50, 60, 70),
                out Vector3d referencePosition);

            Assert.True(resolved);
            AssertVectorEquals(new Vector3d(50, 60, 70), referencePosition);
        }

        [Fact]
        public void TryResolvePlaybackDistanceReferencePosition_InvalidAnchor_FallsBackToCamera()
        {
            bool resolved = ParsekFlight.TryResolvePlaybackDistanceReferencePosition(
                cameraAnchorWorldPosition: new Vector3d(double.NaN, 6000, 7000),
                cameraWorldPosition: new Vector3d(50, 60, 70),
                out Vector3d referencePosition);

            Assert.True(resolved);
            AssertVectorEquals(new Vector3d(50, 60, 70), referencePosition);
        }

        [Fact]
        public void TryResolvePlaybackDistanceReferencePosition_BothInvalid_ReturnsFalse()
        {
            bool resolved = ParsekFlight.TryResolvePlaybackDistanceReferencePosition(
                cameraAnchorWorldPosition: new Vector3d(double.NaN, 6000, 7000),
                cameraWorldPosition: new Vector3d(50, double.PositiveInfinity, 70),
                out Vector3d referencePosition);

            Assert.False(resolved);
            AssertVectorEquals(Vector3d.zero, referencePosition);
        }

        [Fact]
        public void TryResolvePlaybackDistanceReferencePosition_BothNull_ReturnsFalse()
        {
            bool resolved = ParsekFlight.TryResolvePlaybackDistanceReferencePosition(
                cameraAnchorWorldPosition: null,
                cameraWorldPosition: null,
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
