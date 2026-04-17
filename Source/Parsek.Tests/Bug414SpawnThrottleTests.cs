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
    }
}
