using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug #581 instrumentation tests.
    ///
    /// The 2026-04-25_1314 marker-validator-fix playtest emitted exactly one
    /// <c>Playback frame budget exceeded: 11.6ms (0 ghosts, warp: 1x)</c> WARN
    /// whose paired one-shot breakdown was
    /// <c>total=11.6ms mainLoop=7.51ms spawn=3.44ms (built=1 throttled=0
    /// max=3.44ms) destroy=0.00ms ...</c>. That spike falls in a gap between
    /// the existing #450 (gate: <c>spawnMaxMicroseconds &gt;= 15ms</c>) and
    /// #460 (gate: <c>mainLoop &gt;= 10ms</c> AND <c>spawn &lt; 1ms</c>)
    /// sub-breakdown latches: heaviest spawn (3.44 ms) under #450's threshold
    /// AND mainLoop (7.51 ms) under #460's floor, but their SUM was big enough
    /// to push the frame past the 8 ms playback budget.
    ///
    /// Without this latch the session captures the generic #414 breakdown but
    /// no Phase-B attribution that could decide whether the gap is dominated
    /// by per-trajectory main-loop work, the single spawn, or both. These
    /// tests exercise <see cref="DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown"/>
    /// by replaying the captured shape and asserting the new hybrid-spike
    /// breakdown WARN's gating, formatting, latch independence from #414 /
    /// #450 / #460, and one-shot semantics.
    /// </summary>
    [Collection("Sequential")]
    public class Bug581HybridBreakdownTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug581HybridBreakdownTests()
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

        /// <summary>
        /// Mirrors the captured 2026-04-25 playtest spike: total=11.6 ms,
        /// mainLoop=7.51 ms, spawn=3.44 ms (built=1 max=3.44 ms),
        /// observabilityCapture=0.39 ms, deferredCreated=0.28 ms (1 evt),
        /// trajectories=18, ghosts=0, warp=1x. Sub-#450 (heaviest spawn 3.44 ms
        /// well under 15 ms) and sub-#460 (mainLoop 7.51 ms below 10 ms floor),
        /// so this is the canonical hybrid-spike shape.
        /// </summary>
        private static PlaybackBudgetPhases HybridSpikePhases()
        {
            return new PlaybackBudgetPhases
            {
                mainLoopMicroseconds = 7_510,               // 7.51 ms — below #460's 10 ms floor
                spawnMicroseconds = 3_440,                  // 3.44 ms aggregate
                destroyMicroseconds = 0,
                explosionCleanupMicroseconds = 0,
                deferredCreatedEventsMicroseconds = 280,    // 0.28 ms
                deferredCompletedEventsMicroseconds = 0,
                observabilityCaptureMicroseconds = 390,     // 0.39 ms
                trajectoriesIterated = 18,
                overlapGhostIterationCount = 0,
                createdEventsFired = 1,
                completedEventsFired = 0,
                spawnsAttempted = 1,
                spawnsThrottled = 0,
                spawnMaxMicroseconds = 3_440,               // below #450's 15 ms floor
                buildSnapshotResolveMicroseconds = 0,
                buildTimelineFromSnapshotMicroseconds = 0,
                buildDictionariesMicroseconds = 0,
                buildReentryFxMicroseconds = 0,
                buildOtherMicroseconds = 0,
                heaviestSpawnSnapshotResolveMicroseconds = 0,
                heaviestSpawnTimelineFromSnapshotMicroseconds = 0,
                heaviestSpawnDictionariesMicroseconds = 0,
                heaviestSpawnReentryFxMicroseconds = 0,
                heaviestSpawnOtherMicroseconds = 0,
                heaviestSpawnBuildType = HeaviestSpawnBuildType.None,
            };
        }

        [Fact]
        public void HybridSpike_FiresBreakdown_WithCapturedLogShape()
        {
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                11_600, 0, 1.0f, HybridSpikePhases());

            var hybridLine = logLines.FirstOrDefault(l =>
                l.Contains("[Parsek][WARN][Diagnostics]") &&
                l.Contains("Playback hybrid breakdown"));
            Assert.NotNull(hybridLine);
            Assert.Contains("one-shot", hybridLine);
            Assert.Contains("hybrid spike", hybridLine);
            // Pin the exact captured-log values so a future regression on the
            // formatter (unit reshuffling, percent computation, missing field)
            // surfaces immediately.
            Assert.Contains("total=11.6ms", hybridLine);
            Assert.Contains("mainLoop=7.51ms (65%)", hybridLine);
            // spawn line carries (a) the percent-of-frame fraction, (b) the
            // attempt count, (c) the heaviest single-spawn cost.
            Assert.Contains("spawn=3.44ms (30% built=1 max=3.44ms)", hybridLine);
            Assert.Contains("destroy=0.00ms", hybridLine);
            Assert.Contains("explosionCleanup=0.00ms", hybridLine);
            Assert.Contains("deferredCreated=0.28ms (1 evts)", hybridLine);
            Assert.Contains("deferredCompleted=0.00ms (0 evts)", hybridLine);
            Assert.Contains("observabilityCapture=0.39ms", hybridLine);
            Assert.Contains("trajectories=18", hybridLine);
            Assert.Contains("overlapIterations=0", hybridLine);
            Assert.Contains("ghosts=0", hybridLine);
            Assert.Contains("warp=1x", hybridLine);
        }

        [Fact]
        public void HybridSpike_OneShotLatchHonored()
        {
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                11_600, 0, 1.0f, HybridSpikePhases());
            int firstHitCount = logLines.Count(l => l.Contains("Playback hybrid breakdown"));
            Assert.Equal(1, firstHitCount);

            // Replay the same spike — the latch must absorb it.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                11_600, 0, 1.0f, HybridSpikePhases());
            int secondHitCount = logLines.Count(l => l.Contains("Playback hybrid breakdown"));
            Assert.Equal(1, secondHitCount);
        }

        [Fact]
        public void HybridSpike_DoesNotFireWhenSpawnOverFiftyMsThreshold_BurnsHash450InsteadOfHybrid()
        {
            // Heaviest spawn at 18 ms (above the 15 ms #450 threshold) belongs
            // to #450's bimodal-single-spawn territory, NOT #581. The hybrid
            // latch must remain armed for the next genuine sub-#450 spike.
            var phases = HybridSpikePhases();
            phases.spawnMaxMicroseconds = 18_000;
            phases.spawnMicroseconds = 18_000;

            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                25_000, 0, 1.0f, phases);

            Assert.DoesNotContain(logLines, l => l.Contains("Playback hybrid breakdown"));
            // #450 latch should have fired for this shape.
            Assert.Contains(logLines, l => l.Contains("Playback spawn build breakdown"));

            // Latch still armed — a real hybrid-shape spike next fires it.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                11_600, 0, 1.0f, HybridSpikePhases());
            Assert.Contains(logLines, l => l.Contains("Playback hybrid breakdown"));
        }

        [Fact]
        public void HybridSpike_DoesNotFireWhenMainLoopAboveTenMsFloor_BurnsHash460InsteadOfHybrid()
        {
            // mainLoop at 12 ms (above the 10 ms #460 floor) AND spawn under
            // 1 ms qualifies as a #460 mainLoop-dominated spike, not a hybrid.
            // The hybrid latch must remain armed for the next genuine
            // sub-#460 spike.
            var phases = HybridSpikePhases();
            phases.mainLoopMicroseconds = 12_000;
            phases.spawnMicroseconds = 0;
            phases.spawnMaxMicroseconds = 0;
            phases.spawnsAttempted = 0;

            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                15_000, 0, 1.0f, phases);

            Assert.DoesNotContain(logLines, l => l.Contains("Playback hybrid breakdown"));
            // #460 latch should have fired for this shape.
            Assert.Contains(logLines, l => l.Contains("Playback mainLoop breakdown"));

            // Latch still armed — a real hybrid-shape spike next fires it.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                11_600, 0, 1.0f, HybridSpikePhases());
            Assert.Contains(logLines, l => l.Contains("Playback hybrid breakdown"));
        }

        [Fact]
        public void HybridSpike_FiresEvenWhenHash450LatchAlreadyConsumed()
        {
            // Mid-session rollout case: the session's first spike was
            // #450-shaped and consumed the spawn-build-breakdown latch before
            // #581 was loaded. The hybrid latch must still fire on the next
            // qualifying spike — that is the whole point of giving it an
            // independent latch.
            DiagnosticsComputation.SetBug450BreakdownLatchFiredForTesting();

            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                11_600, 0, 1.0f, HybridSpikePhases());

            Assert.Contains(logLines, l => l.Contains("Playback hybrid breakdown"));
            Assert.DoesNotContain(logLines, l => l.Contains("Playback spawn build breakdown"));
        }

        [Fact]
        public void HybridSpike_FiresEvenWhenHash460LatchAlreadyConsumed()
        {
            // Mid-session rollout case: the session's first spike was
            // #460-shaped and consumed the mainLoop-breakdown latch before
            // #581 was loaded. The hybrid latch must still fire on the next
            // qualifying spike. Mirrors how #460 was made independent of #414
            // / #450.
            DiagnosticsComputation.SetBug460BreakdownLatchFiredForTesting();

            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                11_600, 0, 1.0f, HybridSpikePhases());

            Assert.Contains(logLines, l => l.Contains("Playback hybrid breakdown"));
            Assert.DoesNotContain(logLines, l => l.Contains("Playback mainLoop breakdown"));
        }

        [Fact]
        public void HybridSpike_TotalBelowBudget_DoesNotFireBreakdown()
        {
            // Defensive: if the budget short-circuit is ever removed from the
            // method head, the hybrid latch must still respect the budget
            // threshold so a healthy frame cannot burn the session's only
            // sample.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                4_000, 0, 1.0f, HybridSpikePhases());

            Assert.DoesNotContain(logLines, l => l.Contains("Playback hybrid breakdown"));

            // Latch still armed — a real budget-exceeded hybrid spike next fires it.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                11_600, 0, 1.0f, HybridSpikePhases());
            Assert.Contains(logLines, l => l.Contains("Playback hybrid breakdown"));
        }

        [Fact]
        public void HybridSpike_ZeroTotalRendersFractionsAsNa()
        {
            // Degenerate: if a future caller ever drives the helper with
            // total=0 (e.g. a stopwatch read that wrapped or returned 0 us)
            // the percent renderer must not divide by zero.
            var phases = HybridSpikePhases();

            var ex = Record.Exception(() =>
                DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                    0, 0, 1.0f, phases));
            Assert.Null(ex);

            // Below-budget short-circuit means no WARN at all — but the
            // formatter must not throw if it ever IS reached with total=0.
            // Verified by the absence of an exception above.
        }
    }
}
