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

        // === SpawnAtPosition no-override reseed: pure frame-conversion contract ===
        // VesselSpawner.SpawnAtPosition, on the EVA / breakup / non-terminal-orbit respawn
        // paths, used to feed an absolute Y-up worldPos and a recorder-frame Y-up velocity
        // straight into Orbit.UpdateFromStateVectors. That produced a physically-wrong orbit
        // (sma out of band, periapsis subsurface) — the live 2026-07-08 GrappleCapture spawn
        // that materialized off-position at a 214.7 km station and flipped SUB_ORBITAL a frame
        // later. The fix routes that path through OrbitReseed.FromWorldPosAndRecordedVelocity,
        // whose pure core is ComputeRecordedVelocityInputs.
        //
        // SCOPE: this headless test guards the pure frame-conversion CONTRACT (subtract
        // body.position, then .xzy both vectors) at station scale. It does NOT pin the
        // VesselSpawner call site — the changed line and OrbitReseed.FromWorldPosAndRecordedVelocity
        // are Unity-bound (Orbit.UpdateFromStateVectors / body.position). The call-site regression
        // (reverting to the bare call) is caught by the in-game test
        // RuntimeTests.SpawnAtPosition_NoOverrideStationAltitude_ReseedsInBand (SpawnTerminalOrbit).

        [Fact]
        public void RecordedVelocityInputs_StationAltitudeSpawn_SubtractsBodyOffsetAndFlipsFrame()
        {
            // Kerbin-scale numbers: R=600 km body, station at 214.7 km alt (|bodyRel| ≈ 814.7 km),
            // body offset well away from the world origin (floating-origin flight scene).
            var bodyPosition = new Vector3d(2_000_000.0, -500_000.0, 750_000.0);
            var bodyRelPos = new Vector3d(814_700.0, 5_000.0, 3_000.0);   // Y-up, body-relative
            Vector3d worldPos = bodyPosition + bodyRelPos;                 // absolute Y-up, what SpawnAtPosition passes
            var recordedVelYup = new Vector3d(0.0, 100.0, 2_200.0);        // recorder-frame Y-up world velocity

            var inputs = OrbitReseed.ComputeRecordedVelocityInputs(
                worldPos, bodyPosition, recordedVelYup);

            // Position: body offset removed, then YZ-flipped into Zup-local.
            AssertVector(bodyRelPos.xzy, inputs.PositionForUpdate);
            // Velocity: recorder Y-up flipped into Zup-local.
            AssertVector(recordedVelYup.xzy, inputs.VelocityForUpdate);

            // The reseed distance is the true body-relative station distance (≈ Radius+alt),
            // NOT the absolute |worldPos| the bare UpdateFromStateVectors would have used — the
            // gap between them is exactly the frame bug (feeding |worldPos| ≈ 2.9 Mm as if it
            // were body-relative is what pushed sma / periapsis out of band).
            Assert.Equal(bodyRelPos.magnitude, inputs.PositionForUpdate.magnitude, 6);
            Assert.True(
                System.Math.Abs(worldPos.magnitude - inputs.PositionForUpdate.magnitude) > 1_000_000.0,
                $"bare |worldPos|={worldPos.magnitude:F1} must diverge from the corrected " +
                $"|bodyRelPos|={inputs.PositionForUpdate.magnitude:F1} by more than 1 Mm");

            // And the corrected velocity is NOT the raw recorder vector the bare call passed
            // (the .xzy reorders y and z here).
            Assert.NotEqual(recordedVelYup.y, inputs.VelocityForUpdate.y);
            Assert.NotEqual(recordedVelYup.z, inputs.VelocityForUpdate.z);
        }

        private static void AssertVector(Vector3d expected, Vector3d actual)
        {
            Assert.Equal(expected.x, actual.x, 8);
            Assert.Equal(expected.y, actual.y, 8);
            Assert.Equal(expected.z, actual.z, 8);
        }
    }
}
