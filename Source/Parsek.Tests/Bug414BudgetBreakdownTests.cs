using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug #414 instrumentation tests.
    ///
    /// A one-shot 39ms playback frame budget spike fired ~11s after scene load with zero
    /// ghosts rendered. Since per-ghost cost was ruled out by the "0 ghosts" count, a
    /// per-phase breakdown is the next step toward localizing the responsible sub-phase.
    ///
    /// These tests exercise <see cref="DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown"/>
    /// by forcing a synthetic "budget exceeded" scenario and asserting that:
    ///  - the breakdown WARN fires exactly once (one-shot latch),
    ///  - it contains every phase field,
    ///  - it does NOT fire when the frame stayed within budget (zero cost on healthy frames),
    ///  - the existing "Playback frame budget exceeded" WARN continues to fire on subsequent
    ///    spikes (rate-limited) without re-emitting the breakdown.
    /// </summary>
    [Collection("Sequential")]
    public class Bug414BudgetBreakdownTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug414BudgetBreakdownTests()
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

        private static PlaybackBudgetPhases SyntheticSpikePhases()
        {
            // Represents the reported 39.3ms / 0-ghost spike with artificial attribution
            // so tests can assert the per-phase fields are formatted into the log line.
            return new PlaybackBudgetPhases
            {
                mainLoopMicroseconds = 5_000,          // 5.00ms — dispatch over many trajectories
                spawnMicroseconds = 2_000,             // 2.00ms
                destroyMicroseconds = 1_500,           // 1.50ms
                explosionCleanupMicroseconds = 250,    // 0.25ms
                deferredCreatedEventsMicroseconds = 1_000,   // 1.00ms
                deferredCompletedEventsMicroseconds = 28_000, // 28.00ms — pretend-culprit
                observabilityCaptureMicroseconds = 500,      // 0.50ms
                trajectoriesIterated = 249,
                createdEventsFired = 4,
                completedEventsFired = 6,
            };
        }

        [Fact]
        public void WithBreakdown_AboveThreshold_FiresBudgetExceededWarn()
        {
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                39_300, 0, 1.0f, SyntheticSpikePhases());

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[Diagnostics]") &&
                l.Contains("Playback frame budget exceeded"));
        }

        [Fact]
        public void WithBreakdown_AboveThreshold_FiresBreakdownWarn_WithAllPhases()
        {
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                39_300, 0, 1.0f, SyntheticSpikePhases());

            var breakdownLine = logLines.FirstOrDefault(l =>
                l.Contains("[WARN]") &&
                l.Contains("[Diagnostics]") &&
                l.Contains("Playback budget breakdown"));

            Assert.NotNull(breakdownLine);
            Assert.Contains("one-shot", breakdownLine);
            Assert.Contains("total=39.3ms", breakdownLine);
            Assert.Contains("mainLoop=5.00ms", breakdownLine);
            Assert.Contains("spawn=2.00ms", breakdownLine);
            Assert.Contains("destroy=1.50ms", breakdownLine);
            Assert.Contains("explosionCleanup=0.25ms", breakdownLine);
            Assert.Contains("deferredCreated=1.00ms", breakdownLine);
            Assert.Contains("(4 evts)", breakdownLine);
            Assert.Contains("deferredCompleted=28.00ms", breakdownLine);
            Assert.Contains("(6 evts)", breakdownLine);
            Assert.Contains("observabilityCapture=0.50ms", breakdownLine);
            Assert.Contains("trajectories=249", breakdownLine);
            Assert.Contains("ghosts=0", breakdownLine);
            Assert.Contains("warp=1x", breakdownLine);
        }

        [Fact]
        public void WithBreakdown_BelowThreshold_DoesNotFireAnyWarn()
        {
            // 3ms < 8ms threshold — no spike, so neither warning should appear.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                3_000, 0, 1.0f, SyntheticSpikePhases());

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[WARN]") && l.Contains("Playback frame budget exceeded"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[WARN]") && l.Contains("Playback budget breakdown"));
        }

        [Fact]
        public void WithBreakdown_BelowThreshold_LatchNotConsumed()
        {
            // A healthy frame must NOT consume the one-shot latch. If it did, the very
            // first real spike in a session would be missed.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                3_000, 0, 1.0f, SyntheticSpikePhases());
            Assert.DoesNotContain(logLines, l => l.Contains("Playback budget breakdown"));

            // Now fire a real spike — breakdown must appear because the latch was preserved.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                39_300, 0, 1.0f, SyntheticSpikePhases());
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("Playback budget breakdown"));
        }

        [Fact]
        public void WithBreakdown_SecondSpike_DoesNotReEmitBreakdown()
        {
            // First spike: breakdown fires.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                39_300, 0, 1.0f, SyntheticSpikePhases());
            int firstBreakdownCount = logLines.Count(l => l.Contains("Playback budget breakdown"));
            Assert.Equal(1, firstBreakdownCount);

            // Second spike in the same session — breakdown is latched, must not re-emit.
            // (The rate-limited "budget exceeded" line may also be suppressed by the
            // 30s rate limiter in the log layer, but the latch is the contract under test.)
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                42_000, 5, 2.0f, SyntheticSpikePhases());
            int secondBreakdownCount = logLines.Count(l => l.Contains("Playback budget breakdown"));
            Assert.Equal(1, secondBreakdownCount);
        }

        [Fact]
        public void WithBreakdown_DefaultPhases_FormatsZeroedFields()
        {
            // Back-compat path: old CheckPlaybackBudgetThreshold delegates to this method
            // with phases=default, so all phase fields render as 0.00ms. The breakdown
            // line still emits — it just reports zeroes. Future callers that do not yet
            // populate phases are therefore safe.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                39_300, 0, 1.0f, default);

            var breakdownLine = logLines.FirstOrDefault(l =>
                l.Contains("[WARN]") && l.Contains("Playback budget breakdown"));
            Assert.NotNull(breakdownLine);
            Assert.Contains("mainLoop=0.00ms", breakdownLine);
            Assert.Contains("spawn=0.00ms", breakdownLine);
            Assert.Contains("destroy=0.00ms", breakdownLine);
            Assert.Contains("trajectories=0", breakdownLine);
        }

        [Fact]
        public void LegacyCheckPlaybackBudgetThreshold_StillFiresBudgetExceededWarn()
        {
            // The original 3-arg method must still work (it now delegates to the new
            // overload with default phases). Covers ObservabilityLoggingTests expectations.
            DiagnosticsComputation.CheckPlaybackBudgetThreshold(10_000, 5, 1.0f);

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") &&
                l.Contains("[Diagnostics]") &&
                l.Contains("Playback frame budget exceeded"));
        }

        [Fact]
        public void ResetPlaybackBreakdownOneShotForTesting_ReArmsLatch()
        {
            // Fire once, latch consumed.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                39_300, 0, 1.0f, SyntheticSpikePhases());
            Assert.Equal(1, logLines.Count(l => l.Contains("Playback budget breakdown")));

            // Reset the latch explicitly — a fresh spike must now re-emit.
            DiagnosticsComputation.ResetPlaybackBreakdownOneShotForTesting();
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                39_300, 0, 1.0f, SyntheticSpikePhases());
            Assert.Equal(2, logLines.Count(l => l.Contains("Playback budget breakdown")));
        }
    }
}
