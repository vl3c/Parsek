using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the pure parts of the ghost FX emission probe (mean-velocity math,
    /// log-line format, session-key building). The Unity sampling side runs in-game.
    /// </summary>
    public class GhostFxEmissionProbeTests
    {
        [Fact]
        public void TryComputeMeanVelocity_AveragesWorldVelocities()
        {
            var velocities = new List<Vector3>
            {
                new Vector3(0f, -6f, 0f),
                new Vector3(0.5f, -5f, 0f),
                new Vector3(-0.5f, -7f, 0f)
            };

            Assert.True(GhostFxEmissionProbe.TryComputeMeanVelocity(
                velocities, out Vector3 dir, out float speed));
            Assert.True(dir.y < -0.99f, $"mean direction {dir} is not straight down");
            Assert.InRange(speed, 5.9f, 6.2f);
        }

        [Fact]
        public void TryComputeMeanVelocity_EmptyOrCancelling_ReturnsFalse()
        {
            Assert.False(GhostFxEmissionProbe.TryComputeMeanVelocity(
                new List<Vector3>(), out _, out _));
            Assert.False(GhostFxEmissionProbe.TryComputeMeanVelocity(
                new List<Vector3> { new Vector3(0f, 1f, 0f), new Vector3(0f, -1f, 0f) },
                out _, out _));
        }

        [Fact]
        public void BuildProbeLogLine_CarriesTheDiagnosisFields()
        {
            string line = GhostFxEmissionProbe.BuildProbeLogLine(
                "Size3EngineCluster", 0, "fx_smokeTrail_veryLarge", 24,
                Vector3.up, 4.5f, 180f, Quaternion.identity, Vector3.forward,
                new Vector3(120.5f, 64.25f, -8.75f), "Parsek_Ghost_3", 0);

            Assert.Contains("part='Size3EngineCluster'", line);
            Assert.Contains("fx='fx_smokeTrail_veryLarge'", line);
            Assert.Contains("particles=24", line);
            Assert.Contains("angleFromDown=180.0", line);
            Assert.Contains("meanDirWorld=(0.00,1.00,0.00)", line);
            Assert.Contains("posWorld=(120.5,64.3,-8.8)", line);
            Assert.Contains("root='Parsek_Ghost_3'", line);
            Assert.Contains("rootRenderers=0", line);
        }

        [Fact]
        public void BuildKey_DistinguishesPartModuleAndFx()
        {
            Assert.NotEqual(
                GhostFxEmissionProbe.BuildKey("RAPIER", 0, "fx_a"),
                GhostFxEmissionProbe.BuildKey("RAPIER", 1, "fx_a"));
            Assert.NotEqual(
                GhostFxEmissionProbe.BuildKey("RAPIER", 0, "fx_a"),
                GhostFxEmissionProbe.BuildKey("RAPIER", 0, "fx_b"));
        }
    }
}
