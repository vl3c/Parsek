using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for anchor vessel load/unload lifecycle logic:
    /// - IsAnchorLoaded gating (pure static)
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

        #region IsAnchorLoaded — Pure Gating Logic

        [Fact]
        public void IsAnchorLoaded_ZeroAnchorPid_AlwaysReturnsTrue()
        {
            // Unanchored loops (anchorPid == 0) should always be allowed
            var loadedSet = new HashSet<uint>();
            bool result = GhostPlaybackLogic.IsAnchorLoaded(0, loadedSet);
            Assert.True(result);
        }

        [Fact]
        public void IsAnchorLoaded_AnchorInLoadedSet_ReturnsTrue()
        {
            var loadedSet = new HashSet<uint> { 42, 100, 999 };
            bool result = GhostPlaybackLogic.IsAnchorLoaded(42, loadedSet);
            Assert.True(result);
            Assert.Contains(logLines, l => l.Contains("[Loop]") && l.Contains("anchorPid=42") && l.Contains("loaded=True"));
        }

        [Fact]
        public void IsAnchorLoaded_AnchorNotInLoadedSet_ReturnsFalse()
        {
            var loadedSet = new HashSet<uint> { 100, 200 };
            bool result = GhostPlaybackLogic.IsAnchorLoaded(42, loadedSet);
            Assert.False(result);
            Assert.Contains(logLines, l => l.Contains("[Loop]") && l.Contains("anchorPid=42") && l.Contains("loaded=False"));
        }

        [Fact]
        public void IsAnchorLoaded_NullLoadedSet_ReturnsFalse()
        {
            bool result = GhostPlaybackLogic.IsAnchorLoaded(42, null);
            Assert.False(result);
            Assert.Contains(logLines, l => l.Contains("[Loop]") && l.Contains("null"));
        }

        [Fact]
        public void IsAnchorLoaded_EmptyLoadedSet_AnchorNotFound_ReturnsFalse()
        {
            var loadedSet = new HashSet<uint>();
            bool result = GhostPlaybackLogic.IsAnchorLoaded(999, loadedSet);
            Assert.False(result);
        }

        [Fact]
        public void IsAnchorLoaded_ZeroAnchorPid_EmptySet_StillReturnsTrue()
        {
            // Even with empty loaded set, unanchored loops always pass
            var loadedSet = new HashSet<uint>();
            bool result = GhostPlaybackLogic.IsAnchorLoaded(0, loadedSet);
            Assert.True(result);
        }

        [Fact]
        public void IsAnchorLoaded_ZeroAnchorPid_NullSet_StillReturnsTrue()
        {
            // Null set should not affect unanchored loops
            bool result = GhostPlaybackLogic.IsAnchorLoaded(0, null);
            Assert.True(result);
        }

        #endregion

        #region Loaded Set Tracking Semantics

        [Fact]
        public void LoadedSet_AddAndContains_TracksCorrectly()
        {
            // Simulates the load/unload tracking that ParsekFlight does
            var loadedSet = new HashSet<uint>();

            // Vessel loads
            loadedSet.Add(42);
            Assert.True(GhostPlaybackLogic.IsAnchorLoaded(42, loadedSet));

            // Another vessel loads
            loadedSet.Add(100);
            Assert.True(GhostPlaybackLogic.IsAnchorLoaded(42, loadedSet));
            Assert.True(GhostPlaybackLogic.IsAnchorLoaded(100, loadedSet));

            // First vessel unloads
            loadedSet.Remove(42);
            Assert.False(GhostPlaybackLogic.IsAnchorLoaded(42, loadedSet));
            Assert.True(GhostPlaybackLogic.IsAnchorLoaded(100, loadedSet));
        }

        #endregion

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

            var loadedSet = new HashSet<uint> { 42 };

            // Both checks pass: anchor is loaded AND spawn conditions met
            bool anchorOk = GhostPlaybackLogic.IsAnchorLoaded(rec.LoopAnchorVesselId, loadedSet);
            bool spawnOk = GhostPlaybackLogic.ShouldSpawnLoopedGhost(
                rec, true, "Kerbin", rec.LoopAnchorBodyName);

            Assert.True(anchorOk);
            Assert.True(spawnOk);
        }

        [Fact]
        public void AnchoredLoop_AnchorNotLoaded_GhostSuppressed()
        {
            var rec = new Recording
            {
                VesselName = "TestLoop",
                LoopPlayback = true,
                LoopAnchorVesselId = 42,
                LoopAnchorBodyName = "Kerbin",
            };

            var loadedSet = new HashSet<uint>(); // anchor 42 not loaded

            bool anchorOk = GhostPlaybackLogic.IsAnchorLoaded(rec.LoopAnchorVesselId, loadedSet);
            Assert.False(anchorOk);
        }

        [Fact]
        public void UnanchoredLoop_AlwaysAllowed_RegardlessOfLoadedSet()
        {
            var rec = new Recording
            {
                VesselName = "FreeLoop",
                LoopPlayback = true,
                LoopAnchorVesselId = 0, // no anchor
            };

            var loadedSet = new HashSet<uint>();

            bool anchorOk = GhostPlaybackLogic.IsAnchorLoaded(rec.LoopAnchorVesselId, loadedSet);
            Assert.True(anchorOk);
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
            var loadedSet = new HashSet<uint>();

            // Initially loaded
            loadedSet.Add(42);
            Assert.True(GhostPlaybackLogic.IsAnchorLoaded(42, loadedSet));

            // Unloaded
            loadedSet.Remove(42);
            Assert.False(GhostPlaybackLogic.IsAnchorLoaded(42, loadedSet));

            // Reloaded (at a later UT)
            loadedSet.Add(42);
            Assert.True(GhostPlaybackLogic.IsAnchorLoaded(42, loadedSet));

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

        #region Log Assertions for Lifecycle Events

        [Fact]
        public void IsAnchorLoaded_LogsLoadedStatus_WhenAnchorPresent()
        {
            var loadedSet = new HashSet<uint> { 55 };
            GhostPlaybackLogic.IsAnchorLoaded(55, loadedSet);

            Assert.Contains(logLines, l =>
                l.Contains("[Loop]") && l.Contains("anchorPid=55") && l.Contains("loaded=True"));
        }

        [Fact]
        public void IsAnchorLoaded_LogsNotLoaded_WhenAnchorMissing()
        {
            var loadedSet = new HashSet<uint>();
            GhostPlaybackLogic.IsAnchorLoaded(77, loadedSet);

            Assert.Contains(logLines, l =>
                l.Contains("[Loop]") && l.Contains("anchorPid=77") && l.Contains("loaded=False"));
        }

        [Fact]
        public void IsAnchorLoaded_LogsNullSet_WhenNull()
        {
            GhostPlaybackLogic.IsAnchorLoaded(88, null);

            Assert.Contains(logLines, l =>
                l.Contains("[Loop]") && l.Contains("anchorPid=88") && l.Contains("null"));
        }

        [Fact]
        public void IsAnchorLoaded_NoLog_ForUnanchoredLoop()
        {
            // anchorPid=0 returns early without logging — it's the common non-anchor case
            logLines.Clear();
            var loadedSet = new HashSet<uint>();
            GhostPlaybackLogic.IsAnchorLoaded(0, loadedSet);

            // No verbose log expected for the fast path
            Assert.DoesNotContain(logLines, l => l.Contains("anchorPid=0") && l.Contains("loaded="));
        }

        #endregion

        #region Multiple Recordings with Same Anchor

        [Fact]
        public void MultipleRecordings_SameAnchor_AllGatedTogether()
        {
            uint anchorPid = 42;
            var loadedSet = new HashSet<uint>();

            var rec1 = new Recording
            {
                VesselName = "Loop1",
                LoopPlayback = true,
                LoopAnchorVesselId = anchorPid,
            };
            var rec2 = new Recording
            {
                VesselName = "Loop2",
                LoopPlayback = true,
                LoopAnchorVesselId = anchorPid,
            };

            // Before anchor loads, both are gated out
            Assert.False(GhostPlaybackLogic.IsAnchorLoaded(rec1.LoopAnchorVesselId, loadedSet));
            Assert.False(GhostPlaybackLogic.IsAnchorLoaded(rec2.LoopAnchorVesselId, loadedSet));

            // Anchor loads — both allowed
            loadedSet.Add(anchorPid);
            Assert.True(GhostPlaybackLogic.IsAnchorLoaded(rec1.LoopAnchorVesselId, loadedSet));
            Assert.True(GhostPlaybackLogic.IsAnchorLoaded(rec2.LoopAnchorVesselId, loadedSet));

            // Anchor unloads — both gated again
            loadedSet.Remove(anchorPid);
            Assert.False(GhostPlaybackLogic.IsAnchorLoaded(rec1.LoopAnchorVesselId, loadedSet));
            Assert.False(GhostPlaybackLogic.IsAnchorLoaded(rec2.LoopAnchorVesselId, loadedSet));
        }

        [Fact]
        public void DifferentAnchors_IndependentGating()
        {
            var loadedSet = new HashSet<uint>();

            var rec1 = new Recording
            {
                VesselName = "Loop1",
                LoopPlayback = true,
                LoopAnchorVesselId = 42,
            };
            var rec2 = new Recording
            {
                VesselName = "Loop2",
                LoopPlayback = true,
                LoopAnchorVesselId = 99,
            };

            // Load only anchor 42
            loadedSet.Add(42);
            Assert.True(GhostPlaybackLogic.IsAnchorLoaded(rec1.LoopAnchorVesselId, loadedSet));
            Assert.False(GhostPlaybackLogic.IsAnchorLoaded(rec2.LoopAnchorVesselId, loadedSet));

            // Load anchor 99 too
            loadedSet.Add(99);
            Assert.True(GhostPlaybackLogic.IsAnchorLoaded(rec1.LoopAnchorVesselId, loadedSet));
            Assert.True(GhostPlaybackLogic.IsAnchorLoaded(rec2.LoopAnchorVesselId, loadedSet));

            // Unload anchor 42 only
            loadedSet.Remove(42);
            Assert.False(GhostPlaybackLogic.IsAnchorLoaded(rec1.LoopAnchorVesselId, loadedSet));
            Assert.True(GhostPlaybackLogic.IsAnchorLoaded(rec2.LoopAnchorVesselId, loadedSet));
        }

        #endregion
    }
}
