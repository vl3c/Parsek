using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    #region Bug #64 — Discard pending on revert

    /// <summary>
    /// Tests that pending tree/recording state is detected and would be
    /// cleared during a revert. The actual discard is called in ParsekScenario.OnLoad
    /// (KSP runtime), so we test the guard conditions and discard behavior.
    /// </summary>
    [Collection("Sequential")]
    public class Bug64_DiscardPendingOnRevertTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug64_DiscardPendingOnRevertTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void HasPendingTree_WhenTreeStashed_ReturnsTrue()
        {
            var tree = new RecordingTree { Id = "test-tree-64" };
            RecordingStore.StashPendingTree(tree);

            Assert.True(RecordingStore.HasPendingTree);
        }

        [Fact]
        public void DiscardPendingTree_ClearsPendingState()
        {
            var tree = new RecordingTree { Id = "test-tree-64-discard" };
            RecordingStore.StashPendingTree(tree);

            Assert.True(RecordingStore.HasPendingTree);

            RecordingStore.DiscardPendingTree();

            Assert.False(RecordingStore.HasPendingTree);
        }

        [Fact]
        public void HasPending_WhenRecordingStashed_ReturnsTrue()
        {
            // Need at least 2 points for StashPending to accept
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
                new TrajectoryPoint { ut = 200.0 }
            };
            RecordingStore.StashPending(points, "Test Vessel");

            Assert.True(RecordingStore.HasPending);
        }

        [Fact]
        public void DiscardPending_ClearsPendingState()
        {
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
                new TrajectoryPoint { ut = 200.0 }
            };
            RecordingStore.StashPending(points, "Test Vessel Discard");

            Assert.True(RecordingStore.HasPending);

            RecordingStore.DiscardPending();

            Assert.False(RecordingStore.HasPending);
        }

        /// <summary>
        /// The revert guard should clear both pending tree AND pending recording
        /// when both exist simultaneously.
        /// </summary>
        [Fact]
        public void DiscardBoth_ClearsAllPendingState()
        {
            var tree = new RecordingTree { Id = "test-tree-64-both" };
            RecordingStore.StashPendingTree(tree);

            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
                new TrajectoryPoint { ut = 200.0 }
            };
            RecordingStore.StashPending(points, "Test Vessel Both");

            Assert.True(RecordingStore.HasPendingTree);
            Assert.True(RecordingStore.HasPending);

            // Simulate the revert guard from ParsekScenario.OnLoad
            if (RecordingStore.HasPendingTree)
                RecordingStore.DiscardPendingTree();
            if (RecordingStore.HasPending)
                RecordingStore.DiscardPending();

            Assert.False(RecordingStore.HasPendingTree);
            Assert.False(RecordingStore.HasPending);
        }
    }

    #endregion

    #region Bug #79 — SpawnCrossedChainTips returns spawned PIDs without mutating input

    [Collection("Sequential")]
    public class Bug79_SpawnCrossedChainTipsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug79_SpawnCrossedChainTipsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        /// <summary>
        /// With a null ghoster, crossed tips are found but not spawned.
        /// The input dict must NOT be mutated (#79 fix).
        /// </summary>
        [Fact]
        public void SpawnCrossedChainTips_DoesNotMutateInputDict()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 100, MakeChain(100, 1500.0) },
                { 200, MakeChain(200, 1600.0) },
                { 300, MakeChain(300, 2000.0) }  // not crossed
            };

            int originalCount = chains.Count;

            // ghoster is null so no actual spawning, but method should still not mutate dict
            var spawnedPids = TimeJumpManager.SpawnCrossedChainTips(
                chains, null, 1000.0, 1700.0);

            // Dict should be unchanged — method no longer mutates
            Assert.Equal(originalCount, chains.Count);
            Assert.True(chains.ContainsKey(100));
            Assert.True(chains.ContainsKey(200));
            Assert.True(chains.ContainsKey(300));

            // Return value is empty because ghoster is null (no actual spawn)
            Assert.Empty(spawnedPids);

            // Log should show skip warnings for null ghoster
            Assert.Contains(logLines, l =>
                l.Contains("[TimeJump]") && l.Contains("Spawn skipped (no ghoster)"));
        }

        [Fact]
        public void SpawnCrossedChainTips_ReturnsEmptyForNoCrossedTips()
        {
            var chains = new Dictionary<uint, GhostChain>
            {
                { 100, MakeChain(100, 3000.0) }  // not crossed (too far in future)
            };

            var spawnedPids = TimeJumpManager.SpawnCrossedChainTips(
                chains, null, 1000.0, 1500.0);

            Assert.Empty(spawnedPids);
            Assert.Single(chains); // not mutated
        }

        [Fact]
        public void SpawnCrossedChainTips_ReturnsEmptyForNullChains()
        {
            var spawnedPids = TimeJumpManager.SpawnCrossedChainTips(
                null, null, 1000.0, 1500.0);

            Assert.Empty(spawnedPids);
        }

        [Fact]
        public void SpawnCrossedChainTips_ReturnsEmptyForEmptyChains()
        {
            var chains = new Dictionary<uint, GhostChain>();

            var spawnedPids = TimeJumpManager.SpawnCrossedChainTips(
                chains, null, 1000.0, 1500.0);

            Assert.Empty(spawnedPids);
            Assert.Empty(chains);
        }

        private static GhostChain MakeChain(uint pid, double spawnUT)
        {
            return new GhostChain
            {
                OriginalVesselPid = pid,
                SpawnUT = spawnUT,
                GhostStartUT = spawnUT - 100,
                TipRecordingId = $"rec-{pid}",
                TipTreeId = $"tree-{pid}",
                IsTerminated = false
            };
        }
    }

    #endregion

    #region Bug #84 — ComputeLoopPhaseFromUT integer overflow

    public class Bug84_LoopPhaseOverflowTests
    {
        /// <summary>
        /// For a 0.01s recording looping for ~250 real-Earth days (21.6M seconds),
        /// the cycle count (2.16 billion) exceeds int.MaxValue (2,147,483,647).
        /// With the long type fix, this must not overflow.
        /// </summary>
        [Fact]
        public void ComputeLoopPhaseFromUT_LargeElapsed_NoOverflow()
        {
            double startUT = 100.0;
            double endUT = 100.01;  // 0.01s recording
            // Elapsed time chosen so elapsed/cycleDuration > int.MaxValue
            // 0.01s cycle, need > 2.147e9 * 0.01 = 2.147e7 seconds (~248 Earth-days)
            double elapsed = 3e7; // 30M seconds (~347 Earth-days)
            double currentUT = startUT + elapsed;

            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT, startUT, endUT, intervalSeconds: 0.0);

            // Should NOT overflow — cycleIndex should be a large positive number
            Assert.True(cycleIndex > 0, $"cycleIndex should be positive, got {cycleIndex}");
            Assert.True(cycleIndex > int.MaxValue,
                $"cycleIndex should exceed int.MaxValue for this test case, got {cycleIndex}");
            // Allow +-1 for floating point: (long)(3e7 / 0.01) might be 2999999999 or 3000000000
            Assert.InRange(cycleIndex, (long)(elapsed / 0.01) - 1, (long)(elapsed / 0.01) + 1);
            Assert.False(isInPause);
        }

        /// <summary>
        /// At exactly the int.MaxValue boundary, ensure correct behavior.
        /// </summary>
        [Fact]
        public void ComputeLoopPhaseFromUT_IntMaxBoundary_HandledCorrectly()
        {
            double startUT = 0.0;
            double duration = 1.0;
            double interval = 0.0;
            double cycleDuration = duration + Math.Max(0, interval); // = 1.0
            double elapsedAtIntMax = (double)int.MaxValue * cycleDuration;
            double currentUT = startUT + elapsedAtIntMax + 0.5; // midway through cycle int.MaxValue+1

            var (loopUT, cycleIndex, isInPause) = GhostPlaybackLogic.ComputeLoopPhaseFromUT(
                currentUT, startUT, startUT + duration, interval);

            // cycleIndex should be int.MaxValue (or just past it), not negative from overflow
            Assert.True(cycleIndex >= int.MaxValue,
                $"cycleIndex should be >= int.MaxValue, got {cycleIndex}");
            Assert.False(isInPause);
        }

        /// <summary>
        /// GetActiveCycles also uses the long type for cycle counts.
        /// MinCycleDuration clamps to 1.0s, so we need elapsed > int.MaxValue * 1.0.
        /// Use 3 billion seconds (~95 years) to exceed int.MaxValue (2.147 billion).
        /// </summary>
        [Fact]
        public void GetActiveCycles_LargeElapsed_NoOverflow()
        {
            double startUT = 0.0;
            double endUT = 1.0;  // 1.0s recording (won't be clamped)
            double elapsed = 3e9; // 3 billion seconds, 1.0s cycle = 3 billion cycles
            double currentUT = startUT + elapsed;

            long firstCycle, lastCycle;
            GhostPlaybackLogic.GetActiveCycles(
                currentUT, startUT, endUT, intervalSeconds: 0.0,
                maxCycles: 20, out firstCycle, out lastCycle);

            Assert.True(lastCycle > int.MaxValue,
                $"lastCycle should exceed int.MaxValue, got {lastCycle}");
            Assert.True(firstCycle >= 0);
            Assert.True(lastCycle >= firstCycle);
        }

        /// <summary>
        /// TryComputeLoopPlaybackUT static overload handles large cycles.
        /// MinCycleDuration clamps to 1.0s, so we need elapsed > int.MaxValue * 1.0.
        /// With zero interval and 1.0s recording, cycleDuration=1.0s is at the boundary.
        /// Use a small positive interval to push cycleDuration to 2.0s so there's no
        /// false-positive from the pause-window return-false path.
        /// Actually, with zero interval and 1.0s recording, phase == duration is
        /// handled by the (phase > duration + epsilon) check; at exact duration the
        /// method returns true. Use negative interval for overlap path (no pause check).
        /// Simpler: use 2s recording with 0 interval, cycleDuration=2.0s, need 4.3e9 elapsed.
        /// </summary>
        [Fact]
        public void TryComputeLoopPlaybackUT_LargeElapsed_NoOverflow()
        {
            double startUT = 100.0;
            double endUT = 102.0; // 2.0s recording
            double elapsed = 5e9; // 5 billion seconds, 2.0s cycle = 2.5 billion cycles
            double currentUT = startUT + elapsed;

            bool result = GhostPlaybackLogic.TryComputeLoopPlaybackUT(
                currentUT, startUT, endUT, intervalSeconds: -1.0,
                out double loopUT, out long cycleIndex);

            Assert.True(result);
            Assert.True(cycleIndex > int.MaxValue,
                $"cycleIndex should exceed int.MaxValue, got {cycleIndex}");
        }
    }

    #endregion

    #region Bug #130 — GhostPlaybackState.vesselName

    public class Bug130_GhostPlaybackStateVesselNameTests
    {
        [Fact]
        public void VesselName_DefaultIsNull()
        {
            var state = new GhostPlaybackState();
            Assert.Null(state.vesselName);
        }

        [Fact]
        public void VesselName_CanBeAssigned()
        {
            var state = new GhostPlaybackState
            {
                vesselName = "Test Rocket"
            };
            Assert.Equal("Test Rocket", state.vesselName);
        }

        /// <summary>
        /// Verifies the fallback chain pattern used in DestroyGhost and HandleGhostDestroyed:
        /// state?.vesselName ?? traj?.VesselName ?? "Unknown"
        /// </summary>
        [Fact]
        public void VesselName_FallbackChain_UsesStateFirst()
        {
            var state = new GhostPlaybackState { vesselName = "From State" };
            string name = state.vesselName ?? "Unknown";
            Assert.Equal("From State", name);
        }

        [Fact]
        public void VesselName_FallbackChain_FallsToDefault()
        {
            var state = new GhostPlaybackState(); // vesselName is null
            string name = state.vesselName ?? "Unknown";
            Assert.Equal("Unknown", name);
        }
    }

    #endregion
}
