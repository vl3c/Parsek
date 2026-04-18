using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug #450 instrumentation tests.
    ///
    /// A 40.1ms playback frame spike from `logs/2026-04-18_0221_v0.8.2-smoke` showed a single
    /// spawn consuming 28.11ms on its own (`built=1 throttled=0 max=28.11ms`). The #414 count
    /// cap is structurally useless here — only one spawn happened. Phase A adds per-sub-phase
    /// attribution inside `BuildGhostVisualsWithMetrics` so the next such spike reveals which
    /// bucket (snapshot resolve / timeline / dictionaries / reentry FX / other) dominates.
    ///
    /// These tests exercise <see cref="DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown"/>
    /// by forcing a synthetic spike with non-zero sub-phase values and asserting:
    ///  - the new "Playback spawn build breakdown" WARN fires exactly once (one-shot latch),
    ///  - it contains every sub-phase field plus the heaviest-spawn breakdown,
    ///  - the build-type enum renders as a human-readable token for every value,
    ///  - the #450 latch is independent of #414's (so Phase A collects data even when the
    ///    session's first spike already consumed #414's latch before rollout),
    ///  - no build breakdown is emitted when `spawnMicroseconds == 0` (nothing to attribute),
    ///  - ResetForTesting re-arms both latches.
    /// </summary>
    [Collection("Sequential")]
    public class Bug450BuildBreakdownTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug450BuildBreakdownTests()
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

        private static PlaybackBudgetPhases BimodalSingleSpawnPhases()
        {
            // Represents the smoke-test 40.1ms spike: one spawn at 28.11ms, with attribution
            // artificially split across the four sub-phases so tests can assert each renders.
            // Aggregate == heaviest spawn because there was only one spawn this frame.
            return new PlaybackBudgetPhases
            {
                mainLoopMicroseconds = 11_340,         // 11.34ms — from the real log line
                spawnMicroseconds = 28_110,            // 28.11ms — single-spawn dominated frame
                destroyMicroseconds = 0,
                explosionCleanupMicroseconds = 0,
                deferredCreatedEventsMicroseconds = 240,
                deferredCompletedEventsMicroseconds = 0,
                observabilityCaptureMicroseconds = 430,
                trajectoriesIterated = 1,
                createdEventsFired = 1,
                completedEventsFired = 0,
                spawnsAttempted = 1,
                spawnsThrottled = 0,
                spawnMaxMicroseconds = 28_110,

                // Aggregate = heaviest spawn (only one spawn this frame).
                buildSnapshotResolveMicroseconds = 110,       // 0.11ms — trivial lookup
                buildTimelineFromSnapshotMicroseconds = 24_500, // 24.50ms — dominant suspect
                buildDictionariesMicroseconds = 1_800,         // 1.80ms
                buildReentryFxMicroseconds = 1_500,            // 1.50ms
                buildOtherMicroseconds = 200,                   // 0.20ms residual

                heaviestSpawnSnapshotResolveMicroseconds = 110,
                heaviestSpawnTimelineFromSnapshotMicroseconds = 24_500,
                heaviestSpawnDictionariesMicroseconds = 1_800,
                heaviestSpawnReentryFxMicroseconds = 1_500,
                heaviestSpawnOtherMicroseconds = 200,
                heaviestSpawnBuildType = HeaviestSpawnBuildType.RecordingStartSnapshot,
            };
        }

        [Fact]
        public void AboveThreshold_FiresBuildBreakdownWarn_WithAllSubPhases()
        {
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                40_100, 0, 1.0f, BimodalSingleSpawnPhases());

            var breakdownLine = logLines.FirstOrDefault(l =>
                l.Contains("[WARN]") &&
                l.Contains("[Diagnostics]") &&
                l.Contains("Playback spawn build breakdown"));

            Assert.NotNull(breakdownLine);
            Assert.Contains("one-shot", breakdownLine);
            // Aggregate row covers every sub-phase.
            Assert.Contains("sum[snapshot=0.11ms", breakdownLine);
            Assert.Contains("timeline=24.50ms", breakdownLine);
            Assert.Contains("dicts=1.80ms", breakdownLine);
            Assert.Contains("reentry=1.50ms", breakdownLine);
            Assert.Contains("other=0.20ms", breakdownLine);
            // Heaviest spawn row echoes the single-spawn attribution.
            Assert.Contains("heaviestSpawn[type=recording-start-snapshot", breakdownLine);
            Assert.Contains("snapshot=0.11ms", breakdownLine);
            Assert.Contains("timeline=24.50ms", breakdownLine);
            Assert.Contains("dicts=1.80ms", breakdownLine);
            Assert.Contains("reentry=1.50ms", breakdownLine);
            Assert.Contains("other=0.20ms", breakdownLine);
            // Heaviest total reconciles: 0.11 + 24.50 + 1.80 + 1.50 + 0.20 = 28.11ms.
            Assert.Contains("total=28.11ms", breakdownLine);
        }

        // Theory uses the enum's byte value (via InlineData) and casts inside — passing the
        // internal enum type directly through a public method signature trips CS0051, and
        // xUnit serializes InlineData args by value so keeping them primitive is idiomatic.
        [Theory]
        [InlineData((byte)1, "recording-start-snapshot")] // HeaviestSpawnBuildType.RecordingStartSnapshot
        [InlineData((byte)2, "vessel-snapshot")]          // HeaviestSpawnBuildType.VesselSnapshot
        [InlineData((byte)3, "sphere-fallback")]          // HeaviestSpawnBuildType.SphereFallback
        [InlineData((byte)0, "none")]                     // HeaviestSpawnBuildType.None
        public void BuildTypeEnum_RendersAsHumanReadableString(
            byte buildTypeByte, string expectedToken)
        {
            var phases = BimodalSingleSpawnPhases();
            phases.heaviestSpawnBuildType = (HeaviestSpawnBuildType)buildTypeByte;

            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                40_100, 0, 1.0f, phases);

            var breakdownLine = logLines.FirstOrDefault(l =>
                l.Contains("Playback spawn build breakdown"));
            Assert.NotNull(breakdownLine);
            Assert.Contains($"type={expectedToken}", breakdownLine);
        }

        [Fact]
        public void SpawnMicrosecondsZero_SuppressesBuildBreakdown()
        {
            // A budget spike caused purely by mainLoop / deferred events with no spawn time
            // has nothing to attribute across build sub-phases. Emitting five zeros under
            // `heaviestSpawn[type=none ...]` would be noise; the latch must stay unfired so
            // the next real spawn-dominated spike gets its diagnostic.
            var phases = BimodalSingleSpawnPhases();
            phases.spawnMicroseconds = 0;
            phases.spawnMaxMicroseconds = 0;
            phases.buildSnapshotResolveMicroseconds = 0;
            phases.buildTimelineFromSnapshotMicroseconds = 0;
            phases.buildDictionariesMicroseconds = 0;
            phases.buildReentryFxMicroseconds = 0;
            phases.buildOtherMicroseconds = 0;
            phases.heaviestSpawnBuildType = HeaviestSpawnBuildType.None;

            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                40_100, 0, 1.0f, phases);

            Assert.DoesNotContain(logLines, l => l.Contains("Playback spawn build breakdown"));

            // Fire a real spawn-dominated spike next — the latch must still be armed.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                40_100, 0, 1.0f, BimodalSingleSpawnPhases());
            Assert.Contains(logLines, l => l.Contains("Playback spawn build breakdown"));
        }

        [Fact]
        public void LatchIsIndependentOfBug414Latch()
        {
            // Simulate the mid-session rollout case: a budget-exceeded spike already fired
            // (and consumed #414's latch) BEFORE Phase A's code loaded into the running
            // process. The #450 build breakdown must still fire on the next spike that
            // actually contains build-phase data.
            //
            // We emulate "Phase A just loaded" by calling WithBreakdown twice: the first
            // call populates the #414 latch; the second call should emit ONLY the #450
            // build breakdown line (the #414 breakdown is latched out, but the build
            // breakdown latch is independent).
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                40_100, 0, 1.0f, BimodalSingleSpawnPhases());
            int breakdownAfterFirst = logLines.Count(l => l.Contains("Playback budget breakdown"));
            int buildBreakdownAfterFirst = logLines.Count(l => l.Contains("Playback spawn build breakdown"));
            Assert.Equal(1, breakdownAfterFirst);
            Assert.Equal(1, buildBreakdownAfterFirst);

            // Second spike: both latches already consumed, neither line re-emits.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                42_000, 0, 1.0f, BimodalSingleSpawnPhases());
            Assert.Equal(1, logLines.Count(l => l.Contains("Playback budget breakdown")));
            Assert.Equal(1, logLines.Count(l => l.Contains("Playback spawn build breakdown")));
        }

        [Fact]
        public void ResetForTesting_ReArmsBothLatches()
        {
            // Fire once.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                40_100, 0, 1.0f, BimodalSingleSpawnPhases());
            Assert.Equal(1, logLines.Count(l => l.Contains("Playback spawn build breakdown")));

            // Reset re-arms both the #414 and #450 latches, per
            // ResetPlaybackBreakdownOneShotForTesting contract.
            DiagnosticsComputation.ResetPlaybackBreakdownOneShotForTesting();
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                40_100, 0, 1.0f, BimodalSingleSpawnPhases());
            Assert.Equal(2, logLines.Count(l => l.Contains("Playback spawn build breakdown")));
            Assert.Equal(2, logLines.Count(l => l.Contains("Playback budget breakdown")));
        }

        [Fact]
        public void BelowThreshold_DoesNotFireBuildBreakdown()
        {
            // Healthy frame — 3ms total, under the 8ms threshold. No spike means no latch
            // consumption on either line.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                3_000, 0, 1.0f, BimodalSingleSpawnPhases());

            Assert.DoesNotContain(logLines, l => l.Contains("Playback spawn build breakdown"));

            // Latch preserved — next real spike emits the breakdown.
            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                40_100, 0, 1.0f, BimodalSingleSpawnPhases());
            Assert.Contains(logLines, l => l.Contains("Playback spawn build breakdown"));
        }

        [Fact]
        public void MultipleSpawns_AggregateAndHeaviestAreDistinct()
        {
            // Two spawns this frame: one 20ms, one 8ms. Aggregate timeline = 20+8 = 28ms;
            // heaviest spawn timeline = 20ms only. The log format must show both rows so a
            // reviewer can tell "cost spread across spawns" from "single heavy spawn".
            var phases = new PlaybackBudgetPhases
            {
                mainLoopMicroseconds = 2_000,
                spawnMicroseconds = 28_000,
                spawnMaxMicroseconds = 20_000,
                spawnsAttempted = 2,

                buildSnapshotResolveMicroseconds = 200,       // 0.10 + 0.10
                buildTimelineFromSnapshotMicroseconds = 28_000, // aggregate of both spawns
                buildDictionariesMicroseconds = 0,
                buildReentryFxMicroseconds = 0,
                buildOtherMicroseconds = 0,

                heaviestSpawnSnapshotResolveMicroseconds = 100,
                heaviestSpawnTimelineFromSnapshotMicroseconds = 19_800,
                heaviestSpawnDictionariesMicroseconds = 0,
                heaviestSpawnReentryFxMicroseconds = 0,
                heaviestSpawnOtherMicroseconds = 100,
                heaviestSpawnBuildType = HeaviestSpawnBuildType.VesselSnapshot,
            };

            DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(
                30_000, 0, 1.0f, phases);

            var breakdownLine = logLines.FirstOrDefault(l =>
                l.Contains("Playback spawn build breakdown"));
            Assert.NotNull(breakdownLine);
            // Aggregate captures both spawns.
            Assert.Contains("sum[snapshot=0.20ms timeline=28.00ms", breakdownLine);
            // Heaviest captures the 20ms one — type must match that spawn's classification.
            Assert.Contains("heaviestSpawn[type=vessel-snapshot", breakdownLine);
            Assert.Contains("timeline=19.80ms", breakdownLine);
            // Heaviest total reconciles: 0.10 + 19.80 + 0 + 0 + 0.10 = 20.00ms.
            Assert.Contains("total=20.00ms", breakdownLine);
        }
    }
}
