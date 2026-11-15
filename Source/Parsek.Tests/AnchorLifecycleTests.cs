using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for anchor vessel load/unload lifecycle logic:
    /// - loadedAnchorVessels set tracking semantics
    /// - Log assertions for anchor load/unload events
    /// </summary>
    [Collection("Sequential")]
    public class AnchorLifecycleTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public AnchorLifecycleTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(null);
        }

        #region Anchor Gating Combined with ShouldSpawnLoopedGhost

        [Fact]
        public void AnchoredLoop_AnchorLoaded_ShouldSpawn_ReturnsTrue()
        {
            var rec = new Recording
            {
                VesselName = "TestLoop",
                LoopPlayback = true,
                LoopAnchorVesselId = 42,
                LoopAnchorBodyName = "Kerbin",
            };

            bool spawnOk = GhostPlaybackLogic.ShouldSpawnLoopedGhost(
                rec, true, "Kerbin", rec.LoopAnchorBodyName);

            Assert.True(spawnOk);
        }

        #endregion

        #region Anchor Load/Unload with ComputeLoopPhaseFromUT

        [Fact]
        public void AnchorLoaded_PhaseComputed_AtMidCycle()
        {
            // Simulates: anchor loads mid-flight, phase should be computed from UT
            var rec = new Recording
            {
                VesselName = "PhaseTest",
                LoopPlayback = true,
                LoopAnchorVesselId = 42,
                LoopAnchorBodyName = "Kerbin",
            };
            // Recording: UT 100..200 (duration=100), interval=10 (cycle=110)
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100, latitude = 0, longitude = 0, altitude = 70,
                bodyName = "Kerbin", rotation = UnityEngine.Quaternion.identity,
                velocity = UnityEngine.Vector3.zero
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 200, latitude = 0, longitude = 0, altitude = 70,
                bodyName = "Kerbin", rotation = UnityEngine.Quaternion.identity,
                velocity = UnityEngine.Vector3.zero
            });

            // #381: period=110 > duration=100, cycleDuration=110.
            double currentUT = 260.0; // elapsed=160, cycle=1, cycleTime=50
            double interval = 110.0;

            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT, rec.StartUT, rec.EndUT, interval);

            Assert.Equal(150.0, loopUT);
            Assert.Equal(1, cycleIndex);
            Assert.False(isInPause);
        }

        [Fact]
        public void AnchorReloaded_PhaseRecomputed_NotFromStart()
        {
            // Simulates: anchor unloads then reloads — phase should be recomputed
            // from current UT, not start from cycle 0

            // #381: period=110 > duration=100 (single-ghost loop with 10s pause tail).
            // elapsed=270, cycleDuration=110, cycle=2 (270/110=2.45), cycleTime=50.
            double currentUT = 370.0;
            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT, 100.0, 200.0, 110.0);

            Assert.Equal(150.0, loopUT);
            Assert.Equal(2, cycleIndex);
            Assert.False(isInPause);
        }

        #endregion

    }
}
