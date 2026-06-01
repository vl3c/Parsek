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

        // === Epoch-split contract (orbit-raise gap glide) ===
        // The gap glide reconstructs the inertial POSITION at the point's recorded UT (rotation
        // phase) but seeds the orbit EPOCH at a SEPARATE shifted UT (recordedUT + loopEpochShift)
        // so the loop phase is preserved. TryComputeHistoricalSurfaceInputsWithEpoch is the pure
        // Unity-free seam the WithEpoch wrapper delegates to; both share this exact code path.

        [Fact]
        public void WithEpoch_SeedsPositionAtRecordedUTButEpochAtShiftedUT()
        {
            const double recordedUT = 52569971.2;
            const double shift = 1.002e9;
            double epochUT = recordedUT + shift;

            double phaseLat = double.NaN;
            double phaseLon = double.NaN;
            bool ok = OrbitReseed.TryComputeHistoricalSurfaceInputsWithEpoch(
                lat: 1.0,
                lon: 30.0,
                alt: 131000.0,
                recordedVelWorldYup: new Vector3d(2100, 0, 0),
                recordedUT: recordedUT,
                epochUT: epochUT,
                rotationPeriod: 21549.425,
                initialRotationDeg: 0.0,
                getBodyRelativeSurfacePositionYup: (lat, lon, alt) =>
                {
                    phaseLat = lat;
                    phaseLon = lon;
                    return new Vector3d(1, 2, 3);
                },
                out OrbitReseed.StateVectorInputs inputs,
                out double inertialLongitude,
                out double seedEpochUT,
                out string failureReason);

            Assert.True(ok);
            Assert.Null(failureReason);

            // Position phase: longitude lifted by the RECORDED-UT rotation phase, NOT the shifted UT.
            double expectedPhaseDeg = (recordedUT * 360.0) / 21549.425;
            double expectedInertialLon = OrbitReseed.WrapLongitudeDegrees(30.0 + expectedPhaseDeg);
            Assert.Equal(expectedInertialLon, inertialLongitude, 6);
            Assert.Equal(expectedInertialLon, phaseLon, 6);   // surface lookup queried at the recorded-UT phase
            Assert.Equal(1.0, phaseLat, 8);

            // Seed epoch: the SHIFTED UT, so the orbit is seeded at liveUT (loop phase preserved).
            Assert.Equal(epochUT, seedEpochUT, 3);
            Assert.NotEqual(recordedUT, seedEpochUT);

            // Velocity is a frame no-op pass-through (.xzy only): position is the only thing reframed.
            AssertVector(new Vector3d(2100, 0, 0).xzy, inputs.VelocityForUpdate);
        }

        [Fact]
        public void WithEpoch_NonFiniteRotationDeclines()
        {
            bool nan = OrbitReseed.TryComputeHistoricalSurfaceInputsWithEpoch(
                lat: 1.0, lon: 2.0, alt: 3.0,
                recordedVelWorldYup: new Vector3d(1, 2, 3),
                recordedUT: 10.0, epochUT: 1e9,
                rotationPeriod: double.NaN, initialRotationDeg: 0.0,
                getBodyRelativeSurfacePositionYup: (lat, lon, alt) => Vector3d.zero,
                out _, out _, out _, out string nanReason);
            Assert.False(nan);
            Assert.Equal("historical-rotation-unavailable", nanReason);

            bool zero = OrbitReseed.TryComputeHistoricalSurfaceInputsWithEpoch(
                lat: 1.0, lon: 2.0, alt: 3.0,
                recordedVelWorldYup: new Vector3d(1, 2, 3),
                recordedUT: 10.0, epochUT: 1e9,
                rotationPeriod: 0.0, initialRotationDeg: 0.0,
                getBodyRelativeSurfacePositionYup: (lat, lon, alt) => Vector3d.zero,
                out _, out _, out _, out string zeroReason);
            Assert.False(zero);
            Assert.Equal("historical-rotation-unavailable", zeroReason);
        }

        [Fact]
        public void WithEpoch_NonFiniteEpochDeclines()
        {
            bool ok = OrbitReseed.TryComputeHistoricalSurfaceInputsWithEpoch(
                lat: 1.0, lon: 2.0, alt: 3.0,
                recordedVelWorldYup: new Vector3d(1, 2, 3),
                recordedUT: 10.0, epochUT: double.NaN,
                rotationPeriod: 21549.425, initialRotationDeg: 0.0,
                getBodyRelativeSurfacePositionYup: (lat, lon, alt) => Vector3d.zero,
                out _, out _, out _, out string reason);
            Assert.False(ok);
            Assert.Equal("non-finite-epoch", reason);
        }

        [Fact]
        public void WithEpoch_DelegatesWithEqualEpochProducesByteIdenticalSeed()
        {
            // The single-UT path (epochUT == recordedUT) must reconstruct the same inertial position
            // AND seed at the recorded UT, guarding the OrbitSeedResolver MapPresence caller that
            // still uses the single-UT overload.
            const double recordedUT = 12345.0;
            bool ok = OrbitReseed.TryComputeHistoricalSurfaceInputsWithEpoch(
                lat: 5.0, lon: 100.0, alt: 70000.0,
                recordedVelWorldYup: new Vector3d(10, 20, 30),
                recordedUT: recordedUT, epochUT: recordedUT,
                rotationPeriod: 21549.425, initialRotationDeg: 15.0,
                getBodyRelativeSurfacePositionYup: (lat, lon, alt) => new Vector3d(lat, lon, alt),
                out OrbitReseed.StateVectorInputs inputs,
                out double inertialLongitude,
                out double seedEpochUT,
                out _);

            Assert.True(ok);
            Assert.Equal(recordedUT, seedEpochUT, 6);   // no shift baked in

            // Same as TryComputeHistoricalSurfaceInputs (the original single-UT math).
            bool baseOk = OrbitReseed.TryComputeHistoricalSurfaceInputs(
                lat: 5.0, lon: 100.0, alt: 70000.0,
                recordedVelWorldYup: new Vector3d(10, 20, 30),
                recordedUT: recordedUT,
                rotationPeriod: 21549.425, initialRotationDeg: 15.0,
                getBodyRelativeSurfacePositionYup: (lat, lon, alt) => new Vector3d(lat, lon, alt),
                out OrbitReseed.StateVectorInputs baseInputs,
                out double baseInertialLon,
                out _);

            Assert.True(baseOk);
            Assert.Equal(baseInertialLon, inertialLongitude, 8);
            AssertVector(baseInputs.PositionForUpdate, inputs.PositionForUpdate);
            AssertVector(baseInputs.VelocityForUpdate, inputs.VelocityForUpdate);
        }

        private static void AssertVector(Vector3d expected, Vector3d actual)
        {
            Assert.Equal(expected.x, actual.x, 8);
            Assert.Equal(expected.y, actual.y, 8);
            Assert.Equal(expected.z, actual.z, 8);
        }
    }
}
