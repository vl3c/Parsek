using System;
using System.Collections.Generic;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo GHOST-HINGE-POSE-MODEL-INITIAL-ANGLE, mirrored half. Stock poses a servo's moving
    /// transform at <c>AngleAxis(JointTargetAngle, mainAxis)</c> (decompiled KSP 1.12.5,
    /// <c>OnVisualizeServo</c> / <c>SetTargetRotation(..., setRotation: true)</c>), with
    /// <c>ModuleRoboticServoHinge.JointTargetAngle = (mirrorRotation ? -targetAngle : targetAngle)
    /// - modelInitialAngle</c> and <c>ModuleRoboticRotationServo.JointTargetAngle =
    /// (mirrorRotation ? -targetAngle : targetAngle)</c>, negated again when <c>inverted</c>. The
    /// displayed angle (<c>UpdatePAWUI</c>, and the live value the recorder logs) is the
    /// transform angle plus the hinge's <c>modelInitialAngle</c>, so a mirrored servo's snapshot
    /// <c>targetAngle</c> must be turned into that displayed angle before the ghost poses it, or
    /// it spawns mirrored the wrong way. Every cell checks the ghost's final pose angle against
    /// stock's JointTargetAngle.
    /// </summary>
    [Collection("Sequential")]
    public class RoboticMirroredServoPoseTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RoboticMirroredServoPoseTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private static float StockHingeJointTargetAngle(float targetAngle, bool mirror, float modelInitialAngle)
            => (mirror ? -targetAngle : targetAngle) - modelInitialAngle;

        private static float StockRotationServoJointTargetAngle(float targetAngle, bool mirror, bool inverted)
        {
            float a = mirror ? -targetAngle : targetAngle;
            return inverted ? -a : a;
        }

        private static float GhostPose(string moduleName, float targetAngle, bool mirror, bool inverted, float offset)
            => GhostPlaybackLogic.ComputeRoboticPoseAngleDegrees(
                GhostVisualBuilder.ResolveSnapshotDisplayedServoAngle(moduleName, targetAngle, mirror, inverted),
                offset);

        [Theory]
        [InlineData(45f, true, 0f)]
        [InlineData(-30f, true, 0f)]
        [InlineData(90f, true, 90f)]    // mirrored hinge_03 at its default targetAngle
        [InlineData(150f, true, 90f)]
        [InlineData(10f, true, 90f)]
        [InlineData(45f, false, 0f)]
        [InlineData(-30f, false, 0f)]
        [InlineData(90f, false, 90f)]
        [InlineData(150f, false, 90f)]
        public void HingeSnapshotPose_MatchesStocksJointTargetAngle(float targetAngle, bool mirror, float offset)
        {
            Assert.Equal(StockHingeJointTargetAngle(targetAngle, mirror, offset),
                GhostPose("ModuleRoboticServoHinge", targetAngle, mirror, false, offset), 0.0001f);
        }

        [Theory]
        [InlineData(12.5f)]
        [InlineData(-90f)]
        [InlineData(177f)]
        public void UnmirroredHinge_DisplayedAngleIsTheTargetAngleUnchanged(float targetAngle)
        {
            Assert.Equal(targetAngle, GhostVisualBuilder.ResolveSnapshotDisplayedServoAngle(
                "ModuleRoboticServoHinge", targetAngle, false, false));
        }

        [Fact]
        public void HingeIgnoresInverted()
        {
            // The hinge declares no `inverted`; a stray key must not flip it.
            Assert.Equal(40f, GhostVisualBuilder.ResolveSnapshotDisplayedServoAngle(
                "ModuleRoboticServoHinge", 40f, false, true));
            Assert.Equal(-40f, GhostVisualBuilder.ResolveSnapshotDisplayedServoAngle(
                "ModuleRoboticServoHinge", 40f, true, true));
        }

        [Theory]
        [InlineData(30f, false, false)]
        [InlineData(30f, true, false)]
        [InlineData(30f, false, true)]
        [InlineData(30f, true, true)]
        [InlineData(-120f, true, false)]
        public void RotationServoSnapshotPose_MatchesStocksJointTargetAngle(float targetAngle, bool mirror, bool inverted)
        {
            Assert.Equal(StockRotationServoJointTargetAngle(targetAngle, mirror, inverted),
                GhostPose("ModuleRoboticRotationServo", targetAngle, mirror, inverted, 0f), 0.0001f);
        }

        [Theory]
        [InlineData("ModuleRoboticServoPiston")]
        [InlineData("ModuleRoboticServoRotor")]
        public void NonAngularServo_ValueUnchanged(string moduleName)
        {
            Assert.Equal(0.75f, GhostVisualBuilder.ResolveSnapshotDisplayedServoAngle(moduleName, 0.75f, true, true));
        }

        private static ConfigNode HingePart(string targetAngle, string mirrorRotation)
        {
            VesselSnapshotBuilder builder = VesselSnapshotBuilder.ProbeShip("MirrorProbe");
            builder.AddModuleToPart(0, "ModuleRoboticServoHinge",
                ("targetAngle", targetAngle),
                ("mirrorRotation", mirrorRotation),
                ("servoTransformPosition", "0,0,0"),
                ("servoTransformRotation", "0,0,0,1"));
            return builder.Build().GetNodes("PART")[0];
        }

        [Fact]
        public void Parser_MirroredHinge_BaselineIsTheDisplayedAngle_AndLogsOnce()
        {
            SnapshotPartBaseline baseline = GhostVisualBuilder.TryParseSnapshotPartBaseline(
                HingePart("35", "True"));

            Assert.NotNull(baseline);
            Assert.Single(baseline.roboticPoses);
            Assert.Equal(-35f, baseline.roboticPoses[0].value);
            Assert.Single(logLines.FindAll(l => l.Contains("[GhostVisual]")
                && l.Contains("Servo snapshot pose mirrored")
                && l.Contains("module=ModuleRoboticServoHinge")
                && l.Contains("targetAngle=35.0 mirrorRotation=True inverted=False displayed=-35.0")));
        }

        [Fact]
        public void Parser_UnmirroredHinge_BaselineUnchanged_AndNoMirrorLine()
        {
            SnapshotPartBaseline baseline = GhostVisualBuilder.TryParseSnapshotPartBaseline(
                HingePart("35", "False"));

            Assert.NotNull(baseline);
            Assert.Equal(35f, baseline.roboticPoses[0].value);
            Assert.DoesNotContain(logLines, l => l.Contains("Servo snapshot pose mirrored"));
        }

        [Fact]
        public void Parser_MissingOrMalformedMirrorFlag_ReadsAsUnmirrored()
        {
            Assert.Equal(35f, GhostVisualBuilder.TryParseSnapshotPartBaseline(
                HingePart("35", "maybe")).roboticPoses[0].value);
        }

        [Fact]
        public void Parser_InvertedMirroredRotationServo_CancelsOut()
        {
            VesselSnapshotBuilder builder = VesselSnapshotBuilder.ProbeShip("MirrorProbe");
            builder.AddModuleToPart(0, "ModuleRoboticRotationServo",
                ("targetAngle", "20"), ("mirrorRotation", "True"), ("inverted", "True"));
            SnapshotPartBaseline baseline = GhostVisualBuilder.TryParseSnapshotPartBaseline(
                builder.Build().GetNodes("PART")[0]);

            Assert.Equal(20f, baseline.roboticPoses[0].value);
            Assert.DoesNotContain(logLines, l => l.Contains("Servo snapshot pose mirrored"));
        }

        [Fact]
        public void Parser_InvertedRotationServo_IsNegated()
        {
            VesselSnapshotBuilder builder = VesselSnapshotBuilder.ProbeShip("MirrorProbe");
            builder.AddModuleToPart(0, "ModuleRoboticRotationServo",
                ("targetAngle", "20"), ("mirrorRotation", "False"), ("inverted", "True"));
            SnapshotPartBaseline baseline = GhostVisualBuilder.TryParseSnapshotPartBaseline(
                builder.Build().GetNodes("PART")[0]);

            Assert.Equal(-20f, baseline.roboticPoses[0].value);
            Assert.Contains(logLines, l => l.Contains("Servo snapshot pose mirrored")
                && l.Contains("targetAngle=20.0 mirrorRotation=False inverted=True displayed=-20.0"));
        }
    }
}
