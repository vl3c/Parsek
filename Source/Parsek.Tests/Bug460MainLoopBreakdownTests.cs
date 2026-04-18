using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug #460 instrumentation tests.
    ///
    /// Post-B3 playtest <c>logs/2026-04-18_1947_450-b3-playtest/KSP.log</c> fired three
    /// <c>Playback frame budget exceeded</c> WARNs at 17.7 / 18.8 / 24.8 ms with
    /// <c>0 ghosts, warp=1x</c> and <c>spawn=0 destroy=0</c> after the session's first
    /// spike had already consumed the #414 and #450 latches. Without a third one-shot
    /// latch the phase attribution is guesswork. Phase A ships a latch that fires on
    /// the next mainLoop-dominated spike and reports per-phase ms + per-dispatch means,
    /// so Phase B can pick a targeted fix.
    ///
    /// These tests exercise <see cref="DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown"/>
    /// by forcing synthetic spikes through the budget check and asserting the mainLoop
    /// breakdown WARN's gating and formatting. Every gate in the plan
    /// (<c>docs/dev/plan-460-mainloop-breakdown.md</c>) has at least one negative test;
    /// latch independence is asserted pairwise (#414, #450, #460) and end-to-end.
    /// </summary>
    [Collection("Sequential")]
    public class Bug460MainLoopBreakdownTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug460MainLoopBreakdownTests()
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
        /// Mirrors the post-B3 <c>24.8 ms, 0 ghosts, spawn=0, destroy=0</c> spike shape.
        /// mainLoop=20 ms sits above the 10 ms floor; every other non-spawn/destroy
        /// phase stays well below mainLoop so the dominance check (gate 6) passes;
        /// spawn/destroy are exactly zero so gates 3+4 pass.
        /// </summary>
        private static PlaybackBudgetPhases MainLoopDominatedPhases()
        {
            return new PlaybackBudgetPhases
            {
                mainLoopMicroseconds = 20_000,              // 20 ms, above 10 ms floor
                spawnMicroseconds = 0,
                destroyMicroseconds = 0,
                explosionCleanupMicroseconds = 50,          // 0.05 ms
                deferredCreatedEventsMicroseconds = 2_000,  // 2.00 ms
                deferredCompletedEventsMicroseconds = 2_000, // 2.00 ms
                observabilityCaptureMicroseconds = 750,     // 0.75 ms
                trajectoriesIterated = 289,                 // pre-B3 smoke reference
                overlapGhostIterationCount = 0,
                createdEventsFired = 3,
                completedEventsFired = 1,
                spawnsAttempted = 0,
                spawnsThrottled = 0,
                spawnMaxMicroseconds = 0,
                // All #450 heaviest-spawn fields zeroed — no spawn this frame.
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

        /// <summary>
        /// Mirrors <see cref="Bug450BuildBreakdownTests"/>'s bimodal single-spawn shape
        /// (40.1 ms frame, 28.11 ms single spawn, <c>ghosts=0</c>). Fires #414 + #450 but
        /// MUST NOT fire #460 — gate 3 (`spawnMicroseconds &lt; 1 ms`) suppresses it.
        /// </summary>
        private static PlaybackBudgetPhases BimodalSingleSpawnPhases()
        {
            return new PlaybackBudgetPhases
            {
                mainLoopMicroseconds = 11_340,              // above 10 ms floor
                spawnMicroseconds = 28_110,                 // triggers #450 gate 3 suppression
                destroyMicroseconds = 0,
                explosionCleanupMicroseconds = 0,
                deferredCreatedEventsMicroseconds = 240,
                deferredCompletedEventsMicroseconds = 0,
                observabilityCaptureMicroseconds = 430,
                trajectoriesIterated = 1,
                overlapGhostIterationCount = 0,
                createdEventsFired = 1,
                completedEventsFired = 0,
                spawnsAttempted = 1,
                spawnsThrottled = 0,
                spawnMaxMicroseconds = 28_110,
                buildSnapshotResolveMicroseconds = 110,
                buildTimelineFromSnapshotMicroseconds = 24_500,
                buildDictionariesMicroseconds = 1_800,
                buildReentryFxMicroseconds = 1_500,
                buildOtherMicroseconds = 200,
                heaviestSpawnSnapshotResolveMicroseconds = 110,
                heaviestSpawnTimelineFromSnapshotMicroseconds = 24_500,
                heaviestSpawnDictionariesMicroseconds = 1_800,
                heaviestSpawnReentryFxMicroseconds = 1_500,
                heaviestSpawnOtherMicroseconds = 200,
                heaviestSpawnBuildType = HeaviestSpawnBuildType.RecordingStartSnapshot,
            };
        }

        [Fact]
        public void AboveGates_MainLoopDominated_FiresBreakdown_WithAllFields()
        {
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                24_800, 0, 1.0f, MainLoopDominatedPhases());

            var breakdownLine = logLines.FirstOrDefault(l =>
                l.Contains("[WARN]") &&
                l.Contains("[Diagnostics]") &&
                l.Contains("Playback mainLoop breakdown"));
            Assert.NotNull(breakdownLine);
            Assert.Contains("one-shot", breakdownLine);
            Assert.Contains("mainLoop-dominated", breakdownLine);
            // Every field from the plan's format string, in expected units.
            // mainLoop/20000us = 69.20 us/traj at 289 trajectories.
            Assert.Contains("total=24.8ms", breakdownLine);
            Assert.Contains("mainLoop=20.00ms", breakdownLine);
            Assert.Contains("trajectories=289", breakdownLine);
            Assert.Contains("overlapIterations=0", breakdownLine);
            Assert.Contains("meanPerTraj=69.20us", breakdownLine);
            Assert.Contains("meanPerDispatch=69.20us", breakdownLine);
            Assert.Contains("deferredCreated=2.00ms (3 evts)", breakdownLine);
            Assert.Contains("deferredCompleted=2.00ms (1 evts)", breakdownLine);
            Assert.Contains("observabilityCapture=0.75ms", breakdownLine);
            Assert.Contains("explosionCleanup=0.05ms", breakdownLine);
            Assert.Contains("spawn=0.00ms", breakdownLine);
            Assert.Contains("destroy=0.00ms", breakdownLine);
            Assert.Contains("ghosts=0", breakdownLine);
            Assert.Contains("warp=1x", breakdownLine);
        }

        [Fact]
        public void MeanPerTrajectory_IsUsPerDispatch_NotMs()
        {
            // Regression guard: an earlier implementation draft computed the mean as
            // `mainLoopMicroseconds / 1000.0 / trajectoriesIterated`, which prints
            // `0.20ms` instead of `200.00us`. This test pins the correct unit.
            var phases = MainLoopDominatedPhases();
            phases.trajectoriesIterated = 100;  // mainLoop/100 = 200.00 us/traj

            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                24_800, 0, 1.0f, phases);

            var breakdownLine = logLines.First(l => l.Contains("Playback mainLoop breakdown"));
            Assert.Contains("meanPerTraj=200.00us", breakdownLine);
            Assert.Contains("meanPerDispatch=200.00us", breakdownLine);
            Assert.DoesNotContain("0.20ms", breakdownLine);
        }

        [Fact]
        public void ZeroTrajectoriesAndOverlap_RendersMeanAsNa()
        {
            // Degenerate: 20 ms mainLoop spent somehow with no top-level iterations and
            // no overlap dispatch. Implausible in practice (the for-loop must have run
            // at least once to accumulate the time) but the renderer must NOT throw
            // DivideByZeroException and must NOT report a fake `0.00us`. "n/a" sentinel
            // makes the degenerate case visible to a human reader.
            var phases = MainLoopDominatedPhases();
            phases.trajectoriesIterated = 0;
            phases.overlapGhostIterationCount = 0;

            var ex = Record.Exception(() =>
                DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                    24_800, 0, 1.0f, phases));
            Assert.Null(ex);

            var breakdownLine = logLines.First(l => l.Contains("Playback mainLoop breakdown"));
            Assert.Contains("meanPerTraj=n/a", breakdownLine);
            Assert.Contains("meanPerDispatch=n/a", breakdownLine);
        }

        [Fact]
        public void OverlapFanOut_MeanPerDispatchSmallerThanMeanPerTraj()
        {
            // 100 top-level trajectories + 300 overlap iterations. meanPerTraj = 200 us
            // (only top-level in denominator); meanPerDispatch = 50 us (all dispatch).
            // A 4x gap between the two means the reader should investigate overlap
            // fan-out in Phase B, NOT per-trajectory dispatch — exactly the
            // disambiguation the #460 WARN was designed for.
            var phases = MainLoopDominatedPhases();
            phases.trajectoriesIterated = 100;
            phases.overlapGhostIterationCount = 300;

            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                24_800, 0, 1.0f, phases);

            var breakdownLine = logLines.First(l => l.Contains("Playback mainLoop breakdown"));
            Assert.Contains("trajectories=100", breakdownLine);
            Assert.Contains("overlapIterations=300", breakdownLine);
            Assert.Contains("meanPerTraj=200.00us", breakdownLine);
            Assert.Contains("meanPerDispatch=50.00us", breakdownLine);
        }

        [Fact]
        public void GhostsProcessedNonZero_DoesNotFireBreakdown()
        {
            // Gate 2: ghostsProcessed > 0 means per-ghost rendering happened this frame;
            // the cost is #414 territory, not #460. Latch must remain armed.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                24_800, ghostsProcessed: 1, warpRate: 1.0f, phases: MainLoopDominatedPhases());

            Assert.DoesNotContain(logLines, l => l.Contains("Playback mainLoop breakdown"));

            // Latch still armed — a real 0-ghost spike next fires the breakdown.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                24_800, 0, 1.0f, MainLoopDominatedPhases());
            Assert.Contains(logLines, l => l.Contains("Playback mainLoop breakdown"));
        }

        [Fact]
        public void SpawnOverOneMs_DoesNotFireBreakdown()
        {
            // Gate 3: a same-frame spawn (possibly on a trajectory whose state was null
            // at frame start — no ghostsProcessed increment) belongs to #450's bimodal
            // territory, NOT #460. The 1 ms cutoff catches even cheap spawns so #460
            // is reserved for spikes no per-ghost instrumentation explains.
            var phases = MainLoopDominatedPhases();
            phases.spawnMicroseconds = 2_000;  // 2 ms, above the 1 ms cutoff

            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                24_800, 0, 1.0f, phases);

            Assert.DoesNotContain(logLines, l => l.Contains("Playback mainLoop breakdown"));

            // Latch still armed — a real zero-spawn spike next fires the breakdown.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                24_800, 0, 1.0f, MainLoopDominatedPhases());
            Assert.Contains(logLines, l => l.Contains("Playback mainLoop breakdown"));
        }

        [Fact]
        public void DestroyOverOneMs_DoesNotFireBreakdown()
        {
            // Gate 4: symmetric to gate 3 — a same-frame destroy is also per-ghost work.
            var phases = MainLoopDominatedPhases();
            phases.destroyMicroseconds = 2_000;

            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                24_800, 0, 1.0f, phases);

            Assert.DoesNotContain(logLines, l => l.Contains("Playback mainLoop breakdown"));

            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                24_800, 0, 1.0f, MainLoopDominatedPhases());
            Assert.Contains(logLines, l => l.Contains("Playback mainLoop breakdown"));
        }

        [Fact]
        public void MainLoopBelowFloor_DoesNotFireBreakdown()
        {
            // Gate 5: mainLoop below the 10 ms floor. Total budget still tripped (pad
            // 8 ms into deferredCompleted) so the rate-limited WARN fires, but the #460
            // breakdown must not — 8 ms mainLoop is inside the pre-B3 already-captured
            // range and would waste the session's only #460 sample.
            var phases = MainLoopDominatedPhases();
            phases.mainLoopMicroseconds = 8_000;
            phases.deferredCompletedEventsMicroseconds = 8_000;  // keep total > budget

            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                20_000, 0, 1.0f, phases);

            Assert.DoesNotContain(logLines, l => l.Contains("Playback mainLoop breakdown"));

            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                24_800, 0, 1.0f, MainLoopDominatedPhases());
            Assert.Contains(logLines, l => l.Contains("Playback mainLoop breakdown"));
        }

        [Fact]
        public void MainLoopNotDominant_DoesNotFireBreakdown()
        {
            // Gate 6: mainLoop above floor but deferredCompleted is the dominant
            // non-spawn/destroy bucket. That spike belongs to a future #460-sibling
            // diagnostic (deferred-events breakdown), not #460 itself. Burning the
            // latch here would send Phase B down the wrong path.
            var phases = MainLoopDominatedPhases();
            phases.mainLoopMicroseconds = 11_000;               // above floor
            phases.deferredCompletedEventsMicroseconds = 30_000; // but not dominant

            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                45_000, 0, 1.0f, phases);

            Assert.DoesNotContain(logLines, l => l.Contains("Playback mainLoop breakdown"));

            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                24_800, 0, 1.0f, MainLoopDominatedPhases());
            Assert.Contains(logLines, l => l.Contains("Playback mainLoop breakdown"));
        }

        [Fact]
        public void TotalBelowBudget_DoesNotFireBreakdown()
        {
            // Gate 1 (already enforced by the caller, but tested defensively): healthy
            // frame below the 8 ms budget. If the #460 branch runs before the budget
            // check short-circuits the method, the latch would burn on a healthy frame.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                7_000, 0, 1.0f, MainLoopDominatedPhases());

            Assert.DoesNotContain(logLines, l => l.Contains("Playback mainLoop breakdown"));

            // Latch still armed.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                24_800, 0, 1.0f, MainLoopDominatedPhases());
            Assert.Contains(logLines, l => l.Contains("Playback mainLoop breakdown"));
        }

        [Fact]
        public void LatchIndependentOfBug414Latch()
        {
            // Pre-consume #414's latch to simulate the mid-session rollout case: a
            // spike already happened (and consumed #414's latch) BEFORE #460's code
            // loaded. The next qualifying spike must still fire #460.
            DiagnosticsComputation.SetBug414BreakdownLatchFiredForTesting();

            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                24_800, 0, 1.0f, MainLoopDominatedPhases());

            Assert.DoesNotContain(logLines, l => l.Contains("Playback budget breakdown"));
            Assert.Contains(logLines, l => l.Contains("Playback mainLoop breakdown"));
        }

        [Fact]
        public void LatchIndependentOfBug450Latch()
        {
            // Pre-consume #450's latch — the #460 branch must not be gated on it.
            // (#450 wouldn't physically fire at spawnMaxMicroseconds=0, but the seam
            // is needed to prove the independence direction.)
            DiagnosticsComputation.SetBug450BreakdownLatchFiredForTesting();

            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                24_800, 0, 1.0f, MainLoopDominatedPhases());

            Assert.DoesNotContain(logLines, l => l.Contains("Playback spawn build breakdown"));
            Assert.Contains(logLines, l => l.Contains("Playback mainLoop breakdown"));
        }

        [Fact]
        public void SpawnDominated0GhostSpike_FiresBug414And450_But_NOT_460()
        {
            // This is the reviewer-flagged correctness case: a bimodal single-spawn
            // spike at 0 ghosts fires #414 + #450, but #460's gate 3 must suppress
            // it. Otherwise the #460 latch burns on spawn-territory data and the
            // mainLoop diagnostic it was designed to capture never happens.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                40_100, 0, 1.0f, BimodalSingleSpawnPhases());

            Assert.Contains(logLines, l => l.Contains("Playback budget breakdown"));
            Assert.Contains(logLines, l => l.Contains("Playback spawn build breakdown"));
            Assert.DoesNotContain(logLines, l => l.Contains("Playback mainLoop breakdown"));

            // #460 latch still armed — real mainLoop spike fires it.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                24_800, 0, 1.0f, MainLoopDominatedPhases());
            Assert.Contains(logLines, l => l.Contains("Playback mainLoop breakdown"));
        }

        [Fact]
        public void FreshSession_HelperSpike_FiresBug414And460_But_NOT_450()
        {
            // Fresh session — all three latches armed. A spawn-less 20 ms mainLoop
            // spike consumes #414 (first spike in session always does) and #460
            // (all its gates pass). #450 does NOT fire because spawnMaxMicroseconds=0
            // is far below its 15 ms threshold.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                24_800, 0, 1.0f, MainLoopDominatedPhases());

            Assert.Equal(1, logLines.Count(l => l.Contains("Playback budget breakdown")));
            Assert.Equal(0, logLines.Count(l => l.Contains("Playback spawn build breakdown")));
            Assert.Equal(1, logLines.Count(l => l.Contains("Playback mainLoop breakdown")));

            // Second spike of the same shape: only the rate-limited budget-exceeded
            // WARN lands, no breakdowns.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                24_800, 0, 1.0f, MainLoopDominatedPhases());

            Assert.Equal(1, logLines.Count(l => l.Contains("Playback budget breakdown")));
            Assert.Equal(1, logLines.Count(l => l.Contains("Playback mainLoop breakdown")));
        }

        [Fact]
        public void ResetForTesting_ReArmsAllThreeLatches()
        {
            // Fire a bimodal spawn spike to consume #414 + #450 in one frame.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                40_100, 0, 1.0f, BimodalSingleSpawnPhases());
            // Then a mainLoop-dominated spike to consume #460. (#414 already spent.)
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                24_800, 0, 1.0f, MainLoopDominatedPhases());

            Assert.Equal(1, logLines.Count(l => l.Contains("Playback budget breakdown")));
            Assert.Equal(1, logLines.Count(l => l.Contains("Playback spawn build breakdown")));
            Assert.Equal(1, logLines.Count(l => l.Contains("Playback mainLoop breakdown")));

            // Reset re-arms all three. Same two spikes fire each breakdown one more time.
            DiagnosticsComputation.ResetPlaybackBreakdownOneShotForTesting();

            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                40_100, 0, 1.0f, BimodalSingleSpawnPhases());
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                24_800, 0, 1.0f, MainLoopDominatedPhases());

            Assert.Equal(2, logLines.Count(l => l.Contains("Playback budget breakdown")));
            Assert.Equal(2, logLines.Count(l => l.Contains("Playback spawn build breakdown")));
            Assert.Equal(2, logLines.Count(l => l.Contains("Playback mainLoop breakdown")));
        }

        [Fact]
        public void BelowThreshold_DoesNotFireAnyBreakdown()
        {
            // Healthy frame — no WARN of any kind, all latches preserved.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                3_000, 0, 1.0f, MainLoopDominatedPhases());

            Assert.DoesNotContain(logLines, l => l.Contains("Playback mainLoop breakdown"));
            Assert.DoesNotContain(logLines, l => l.Contains("Playback budget breakdown"));
            Assert.DoesNotContain(logLines, l => l.Contains("Playback spawn build breakdown"));

            // Latches preserved — next real spike still fires each applicable breakdown.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                24_800, 0, 1.0f, MainLoopDominatedPhases());
            Assert.Contains(logLines, l => l.Contains("Playback mainLoop breakdown"));
        }
    }
}
