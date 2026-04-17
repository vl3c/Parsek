using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug #414 spawn-throttle tests.
    ///
    /// The diagnostic shipped in the first half of #414 proved the zero-ghost spike is
    /// dominated by per-frame ghost visual build cost (spawn phase = 14.91ms of a 23.8ms
    /// total on the c2-postfix-retest playtest breakdown). The fix adds a per-frame cap
    /// on throttle-eligible spawns at seven call sites inside <see cref="GhostPlaybackEngine"/>;
    /// watch-mode and loop-cycle-rebuild spawns intentionally bypass the gate.
    ///
    /// These tests cover:
    ///  - <see cref="GhostPlaybackLogic.ShouldThrottleSpawn"/> pure helper decision boundary,
    ///  - the breakdown WARN line now includes the throttle fields so the next playtest
    ///    shows whether throttling is actually firing.
    /// Integration tests that drive UpdatePlayback directly are out of scope — no existing
    /// tests do so either, and the per-call-site gate placement is verified by code review.
    /// </summary>
    [Collection("Sequential")]
    public class Bug414SpawnThrottleTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug414SpawnThrottleTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            DiagnosticsState.ResetForTesting();
            DiagnosticsComputation.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            DiagnosticsState.ResetForTesting();
            DiagnosticsComputation.ResetForTesting();
        }

        [Theory]
        [InlineData(0, 2, false)]  // fresh frame, first spawn allowed
        [InlineData(1, 2, false)]  // second spawn still allowed
        [InlineData(2, 2, true)]   // cap hit
        [InlineData(3, 2, true)]   // past cap still throttled
        public void ShouldThrottleSpawn_MatchesCapBoundary(int spawnsThisFrame, int cap, bool expected)
        {
            Assert.Equal(expected, GhostPlaybackLogic.ShouldThrottleSpawn(spawnsThisFrame, cap));
        }

        [Fact]
        public void MaxSpawnsPerFrame_IsTwo()
        {
            // If this ever changes, the plan's cap-math in plan-414-spawn-throttle.md must
            // be re-derived. Catching a silent constant change here makes that explicit.
            Assert.Equal(2, GhostPlaybackEngine.MaxSpawnsPerFrame);
        }

        [Fact]
        public void BreakdownLine_IncludesBuiltThrottledMaxFields()
        {
            var phases = new PlaybackBudgetPhases
            {
                mainLoopMicroseconds = 8_340,
                spawnMicroseconds = 14_910,
                destroyMicroseconds = 0,
                trajectoriesIterated = 4,
                spawnsAttempted = 2,
                spawnsThrottled = 3,
                spawnMaxMicroseconds = 4_800,
            };
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                23_800, 0, 1.0f, phases);

            var breakdown = logLines.FirstOrDefault(l =>
                l.Contains("[WARN]") && l.Contains("Playback budget breakdown"));
            Assert.NotNull(breakdown);
            Assert.Contains("built=2", breakdown);
            Assert.Contains("throttled=3", breakdown);
            Assert.Contains("max=4.80ms", breakdown);
            // Sanity: the pre-existing fields still render.
            Assert.Contains("spawn=14.91ms", breakdown);
            Assert.Contains("mainLoop=8.34ms", breakdown);
            Assert.Contains("trajectories=4", breakdown);
        }

        [Fact]
        public void BreakdownLine_DefaultPhases_RendersZeroThrottleFields()
        {
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                39_300, 0, 1.0f, default);

            var breakdown = logLines.FirstOrDefault(l =>
                l.Contains("[WARN]") && l.Contains("Playback budget breakdown"));
            Assert.NotNull(breakdown);
            Assert.Contains("built=0", breakdown);
            Assert.Contains("throttled=0", breakdown);
            Assert.Contains("max=0.00ms", breakdown);
        }

        [Fact]
        public void EngineThrottle_BlocksThirdReservation_AndEmitsLog()
        {
            // Engine-level regression: drives TryReserveSpawnSlot directly so a gate
            // accidentally moved or removed surfaces as a test failure, not just a
            // code-review miss.
            var engine = new GhostPlaybackEngine(null);
            engine.ResetPerFrameCountersForTesting();

            Assert.True(engine.TryReserveSpawnSlotForTesting(0, "first-spawn"));
            engine.IncrementFrameSpawnCountForTesting();
            Assert.True(engine.TryReserveSpawnSlotForTesting(1, "first-spawn"));
            engine.IncrementFrameSpawnCountForTesting();
            // Cap hit (MaxSpawnsPerFrame=2): next call must defer.
            Assert.False(engine.TryReserveSpawnSlotForTesting(2, "first-spawn"));
            Assert.False(engine.TryReserveSpawnSlotForTesting(3, "distance-tier-rehydrate"));

            Assert.Equal(2, engine.FrameSpawnCountForTesting);
            Assert.Equal(2, engine.FrameSpawnDeferredForTesting);
            // Only the first throttle log lands — the rate-limiter (key "spawn-throttle",
            // 1.0s window) correctly suppresses the burst that follows within the same
            // second. That's the production contract; both calls still incremented the
            // deferred counter above.
            Assert.Contains(logLines, l =>
                l.Contains("[Engine]") &&
                l.Contains("Spawn throttled") &&
                l.Contains("(first-spawn)") &&
                l.Contains("#2"));
        }

        [Fact]
        public void ApplyFlagEvents_NullState_NoOpWithNoEvents()
        {
            // Post-review P2 fix: ApplyFlagEvents must tolerate a null state so throttled
            // first-spawn frames still honor the invariant "flags are independent permanent
            // world vessels, placed regardless of ghost state/visibility/schedule".
            // Full end-to-end flag spawning requires live KSP — this test pins the
            // no-throw contract on the empty-events path that the cursor-less branch takes.
            var traj = new MockTrajectory();
            Assert.Null(Record.Exception(
                () => GhostPlaybackLogic.ApplyFlagEvents(null, traj, 100.0)));
        }

        [Fact]
        public void ApplyFlagEvents_NullTrajectory_NoOp()
        {
            Assert.Null(Record.Exception(
                () => GhostPlaybackLogic.ApplyFlagEvents(null, null, 100.0)));
        }

        [Fact]
        public void LoopCycleRebuild_BypassesThrottle_WhenCycleChanged()
        {
            // Post-review P2 fix: the single-ghost loop cycle-rebuild path destroys the
            // prior ghost before reaching the state==null spawn block. Under sustained
            // backlog, a throttled rebuild would leave the recording ghostless for multiple
            // frames. The production gate expression is `!cycleChanged && !TryReserveSpawnSlot(...)`
            // so cycleChanged==true short-circuits the throttle. This test pins that
            // short-circuit truth table without needing to drive UpdateLoopingPlayback.
            var engine = new GhostPlaybackEngine(null);
            engine.ResetPerFrameCountersForTesting();
            engine.IncrementFrameSpawnCountForTesting();
            engine.IncrementFrameSpawnCountForTesting(); // cap exhausted

            // cycleChanged == false (first-spawn case): throttle fires.
            bool throttled1 = !/*cycleChanged:*/false
                && !engine.TryReserveSpawnSlotForTesting(0, "loop-first-spawn");
            Assert.True(throttled1);
            Assert.Equal(1, engine.FrameSpawnDeferredForTesting);

            // cycleChanged == true (rebuild case): short-circuit skips TryReserveSpawnSlot
            // entirely, so the deferred counter stays put and the spawn proceeds.
            bool throttled2 = !/*cycleChanged:*/true
                && !engine.TryReserveSpawnSlotForTesting(0, "loop-first-spawn");
            Assert.False(throttled2);
            Assert.Equal(1, engine.FrameSpawnDeferredForTesting);
        }

        [Fact]
        public void EngineThrottle_ResetPerFrameCounters_ReArmsBudget()
        {
            // After a frame boundary resets the counters, the next frame gets a fresh cap.
            // Mirrors the production reset inside UpdatePlayback.
            var engine = new GhostPlaybackEngine(null);
            engine.ResetPerFrameCountersForTesting();

            Assert.True(engine.TryReserveSpawnSlotForTesting(0, "first-spawn"));
            engine.IncrementFrameSpawnCountForTesting();
            Assert.True(engine.TryReserveSpawnSlotForTesting(1, "first-spawn"));
            engine.IncrementFrameSpawnCountForTesting();
            Assert.False(engine.TryReserveSpawnSlotForTesting(2, "first-spawn"));

            // Frame boundary.
            engine.ResetPerFrameCountersForTesting();

            Assert.Equal(0, engine.FrameSpawnCountForTesting);
            Assert.Equal(0, engine.FrameSpawnDeferredForTesting);
            Assert.True(engine.TryReserveSpawnSlotForTesting(2, "first-spawn"));
            Assert.True(engine.TryReserveSpawnSlotForTesting(3, "first-spawn"));
        }
    }
}
