using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    public class FxDiagnosticsTests
    {
        [Theory]
        [InlineData(0f, 0f, 1f, 0f, 0f, 1f, 0f)]
        [InlineData(0f, 0f, 1f, 1f, 0f, 0f, 90f)]
        [InlineData(0f, 1f, 0f, 0f, -1f, 0f, 180f)]
        public void ComputeDirectionAngleDegrees_ReturnsExpectedAngles(
            float sx, float sy, float sz,
            float tx, float ty, float tz,
            float expected)
        {
            float actual = GhostVisualBuilder.ComputeDirectionAngleDegrees(
                new Vector3(sx, sy, sz),
                new Vector3(tx, ty, tz));

            Assert.True(Mathf.Abs(actual - expected) < 0.001f,
                $"Expected angle {expected} but got {actual}");
        }

        [Fact]
        public void ComputeDirectionAngleDegrees_ZeroVector_ReturnsNaN()
        {
            float actual = GhostVisualBuilder.ComputeDirectionAngleDegrees(Vector3.zero, Vector3.forward);

            Assert.True(float.IsNaN(actual));
        }

        [Fact]
        public void BuildFxFrameDiagnostic_ReportsExpectedMetrics()
        {
            string line = GhostVisualBuilder.BuildFxFrameDiagnostic(
                sourcePartLocalPos: Vector3.zero,
                sourcePartLocalRot: Quaternion.identity,
                sourceForward: Vector3.forward,
                sourceUp: Vector3.up,
                targetPartLocalPos: Vector3.forward,
                targetPartLocalRot: new Quaternion(0f, 0.7071068f, 0f, 0.7071068f),
                targetForward: Vector3.right,
                targetUp: Vector3.up);

            Assert.Contains("deltaPosMag=1.0000", line);
            Assert.Contains("deltaRot=90.000", line);
            Assert.Contains("fwdAngle=90.000", line);
            Assert.Contains("upAngle=0.000", line);
        }

        [Fact]
        public void BuildEngineFxEmissionDiagnostic_IncludesCoreFields()
        {
            string line = GhostPlaybackLogic.BuildEngineFxEmissionDiagnostic(
                partName: "MassiveBooster",
                partPersistentId: 123u,
                moduleIndex: 2,
                power: 1f,
                particleName: "fx_smokeTrail_medium",
                parentName: "thrustTransform",
                localPosition: new Vector3(0f, 0f, 0.35f),
                localRotation: new Quaternion(-0.7071068f, 0f, 0f, 0.7071068f),
                worldPosition: new Vector3(1f, 2f, 3f),
                worldForward: Vector3.forward,
                worldUp: Vector3.up,
                emissionRate: 75f,
                startSpeed: 12f,
                isPlaying: true);

            Assert.Contains("part='MassiveBooster'", line);
            Assert.Contains("pid=123", line);
            Assert.Contains("midx=2", line);
            Assert.Contains("ps='fx_smokeTrail_medium'", line);
            Assert.Contains("parent='thrustTransform'", line);
            Assert.Contains("power=1.00", line);
            Assert.Contains("rate=75.00", line);
            Assert.Contains("speed=12.00", line);
            Assert.Contains("playing=True", line);
        }
    }
}
