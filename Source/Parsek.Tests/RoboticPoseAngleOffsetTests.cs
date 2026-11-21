using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo GHOST-HINGE-POSE-MODEL-INITIAL-ANGLE. Every recorded hinge angle is in stock's
    /// DISPLAYED convention: the live read and the part menu's <c>currentAngle</c> are
    /// <c>currentTransformAngle() + modelInitialAngle</c>, and the snapshot baseline's
    /// <c>targetAngle</c> is the commanded displayed angle. Stock poses the moving transform at
    /// <c>AngleAxis(targetAngle - modelInitialAngle, mainAxis)</c>
    /// (<c>ModuleRoboticServoHinge.JointTargetAngle</c> / <c>OnVisualizeServo</c>, decompiled
    /// KSP 1.12.5), so the model's rest pose IS the displayed <c>modelInitialAngle</c>. The ghost
    /// must take the offset back off, or hinge_03 / hinge_03_s / hinge_04
    /// (<c>modelInitialAngle = 90</c>) pose 90 degrees off.
    /// </summary>
    public class RoboticPoseAngleOffsetTests
    {
        [Theory]
        [InlineData(0f, 0f, 0f)]
        [InlineData(45f, 0f, 45f)]
        [InlineData(-30f, 0f, -30f)]
        [InlineData(90f, 90f, 0f)]    // hinge_03 at its default targetAngle: the model's rest
        [InlineData(180f, 90f, 90f)]  // hinge_03 fully open
        [InlineData(0f, 90f, -90f)]   // hinge_03 fully closed
        public void PoseAngle_IsTheRecordedDisplayedAngleLessTheModelOffset(
            float recorded, float offset, float expected)
        {
            Assert.Equal(expected, GhostPlaybackLogic.ComputeRoboticPoseAngleDegrees(recorded, offset), 0.0001f);
        }

        [Theory]
        [InlineData(12.5f)]
        [InlineData(-90f)]
        [InlineData(177f)]
        public void OffsetZeroHinge_PosesExactlyTheRecordedValue(float recorded)
        {
            // hinge_01 / hinge_01_s / hinge_02 / every rotation servo: replay is unchanged.
            Assert.Equal(recorded, GhostPlaybackLogic.ComputeRoboticPoseAngleDegrees(recorded, 0f));
        }

        [Fact]
        public void PoseOffset_IsTheHingesModelInitialAngle()
        {
            Assert.Equal(90f, GhostVisualBuilder.ResolveRoboticPoseAngleOffsetDegrees(
                "ModuleRoboticServoHinge", true, 90f));
            Assert.Equal(0f, GhostVisualBuilder.ResolveRoboticPoseAngleOffsetDegrees(
                "ModuleRoboticServoHinge", true, 0f));
        }

        [Theory]
        [InlineData("ModuleRoboticServoHinge", false, 0f)]
        [InlineData("ModuleRoboticServoHinge", true, float.NaN)]
        [InlineData("ModuleRoboticServoHinge", true, float.PositiveInfinity)]
        [InlineData("ModuleRoboticRotationServo", true, 90f)]
        [InlineData("ModuleRoboticServoPiston", true, 90f)]
        [InlineData("ModuleRoboticServoRotor", true, 90f)]
        public void PoseOffset_IsZeroForEveryOtherServoAndAnUnreadableField(
            string moduleName, bool hasField, float value)
        {
            // Only the hinge's displayed angle carries modelInitialAngle (UpdatePAWUI adds it on
            // the hinge alone), so only the hinge takes it back off.
            Assert.Equal(0f, GhostVisualBuilder.ResolveRoboticPoseAngleOffsetDegrees(moduleName, hasField, value));
        }

        [Fact]
        public void NoBaselineSpawnValue_IsThePrefabRestPose()
        {
            // With no snapshot baseline the ghost spawns at spawnValue; the prefab rest of a
            // hinge_03 is the displayed 90, which poses at zero offset from stowedRot.
            float offset = GhostVisualBuilder.ResolveRoboticPoseAngleOffsetDegrees(
                "ModuleRoboticServoHinge", true, 90f);
            float spawnValue = GhostVisualBuilder.ResolveRoboticDefaultSpawnValue(
                RoboticVisualMode.Rotational, offset);
            Assert.Equal(0f, GhostPlaybackLogic.ComputeRoboticPoseAngleDegrees(spawnValue, offset));
            Assert.Equal(0f, GhostVisualBuilder.ResolveRoboticDefaultSpawnValue(RoboticVisualMode.Linear, 0f));
            Assert.Equal(0f, GhostVisualBuilder.ResolveRoboticDefaultSpawnValue(RoboticVisualMode.RotorRpm, 0f));
        }
    }
}
