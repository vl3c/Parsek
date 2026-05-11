using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    public class OrbitReseedPureTests
    {
        [Fact]
        public void ComputeRecordedVelocityInputs_SubtractsBodyPositionAndFlipsPositionAndVelocity()
        {
            var inputs = OrbitReseed.ComputeRecordedVelocityInputs(
                new Vector3d(10, 20, 30),
                new Vector3d(1, 2, 3),
                new Vector3d(4, 5, 6));

            AssertVector(new Vector3d(9, 27, 18), inputs.PositionForUpdate);
            AssertVector(new Vector3d(4, 6, 5), inputs.VelocityForUpdate);
        }

        [Fact]
        public void ComputeZupVelocityInputs_SubtractsBodyPositionAndPreservesVelocityFrame()
        {
            var inputs = OrbitReseed.ComputeZupVelocityInputs(
                new Vector3d(10, 20, 30),
                new Vector3d(1, 2, 3),
                new Vector3d(4, 5, 6));

            AssertVector(new Vector3d(9, 27, 18), inputs.PositionForUpdate);
            AssertVector(new Vector3d(4, 5, 6), inputs.VelocityForUpdate);
        }

        [Fact]
        public void TryComputeHistoricalSurfaceInputs_AppliesInitialRotationAndRecordedPhase()
        {
            bool called = false;
            bool ok = OrbitReseed.TryComputeHistoricalSurfaceInputs(
                lat: 1.0,
                lon: 170.0,
                alt: 10.0,
                recordedVelWorldYup: new Vector3d(1, 2, 3),
                recordedUT: 10.0,
                rotationPeriod: 40.0,
                initialRotationDeg: 20.0,
                getBodyRelativeSurfacePositionYup: (lat, lon, alt) =>
                {
                    called = true;
                    Assert.Equal(1.0, lat);
                    Assert.Equal(-80.0, lon, 8);
                    Assert.Equal(10.0, alt);
                    return new Vector3d(7, 8, 9);
                },
                out OrbitReseed.StateVectorInputs inputs,
                out double inertialLongitude,
                out string failureReason);

            Assert.True(ok);
            Assert.True(called);
            Assert.Null(failureReason);
            Assert.Equal(-80.0, inertialLongitude, 8);
            AssertVector(new Vector3d(7, 9, 8), inputs.PositionForUpdate);
            AssertVector(new Vector3d(1, 3, 2), inputs.VelocityForUpdate);
        }

        [Fact]
        public void TryComputeHistoricalSurfaceInputs_InvalidRotationDeclines()
        {
            bool ok = OrbitReseed.TryComputeHistoricalSurfaceInputs(
                lat: 1.0,
                lon: 2.0,
                alt: 3.0,
                recordedVelWorldYup: new Vector3d(1, 2, 3),
                recordedUT: 10.0,
                rotationPeriod: 0.0,
                initialRotationDeg: 0.0,
                getBodyRelativeSurfacePositionYup: (lat, lon, alt) => Vector3d.zero,
                out _,
                out _,
                out string failureReason);

            Assert.False(ok);
            Assert.Equal("historical-rotation-unavailable", failureReason);
        }

        private static void AssertVector(Vector3d expected, Vector3d actual)
        {
            Assert.Equal(expected.x, actual.x, 8);
            Assert.Equal(expected.y, actual.y, 8);
            Assert.Equal(expected.z, actual.z, 8);
        }
    }
}
