using System;
using System.Collections.Generic;
using System.Diagnostics;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the pure static helpers extracted from GhostPlaybackEngine's two
    /// hot-path methods (UpdatePlayback / RenderInRangeGhost) in the behavior-preserving
    /// decomposition pass:
    ///   - BuildPlaybackBudgetPhases (U1): the #414/#450 tick-to-microsecond budget-phase
    ///     arithmetic + struct build.
    ///   - IsSpawnSuppressedDeadOnArrival (R-A): the #688 dead-on-arrival spawn-suppression
    ///     predicate (including the De Morgan held-flag boundary).
    /// Both helpers are pure (no instance state, no logging, no Unity), so the test class
    /// keeps the standard log-capture teardown only for parity with sibling *ExtractedTests.
    /// </summary>
    [Collection("Sequential")]
    public class GhostPlaybackEngineExtractedTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostPlaybackEngineExtractedTests()
        {
            RecordingStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // Mirror the helper's exact conversion so assertions are independent of the
        // host machine's Stopwatch.Frequency.
        private static long TicksToMicroseconds(long ticks) =>
            ticks * 1000000L / Stopwatch.Frequency;

        #region BuildPlaybackBudgetPhases (U1)

        [Fact]
        public void BuildPlaybackBudgetPhases_TicksConvertToMicroseconds()
        {
            // Choose tick counts as integer multiples of Stopwatch.Frequency so the
            // expected microsecond values are exact regardless of the host frequency.
            long freq = Stopwatch.Frequency;

            PlaybackBudgetPhases phases = GhostPlaybackEngine.BuildPlaybackBudgetPhases(
                elapsedTicksAtLoopEnd: freq * 5, // 5,000,000 us elapsed at loop end
                spawnMicroseconds: 0,
                destroyMicroseconds: 0,
                deferredCreatedTicks: freq,        // -> 1,000,000 us
                deferredCompletedTicks: freq * 2,  // -> 2,000,000 us
                observabilityTicks: freq * 3,      // -> 3,000,000 us
                trajectoriesIterated: 0,
                overlapGhostIterationCount: 0,
                createdEventsFired: 0,
                completedEventsFired: 0,
                spawnsAttempted: 0,
                spawnsThrottled: 0,
                frameMaxSpawnTicks: freq * 4,      // -> 4,000,000 us
                buildSnapshotResolveTicks: 0,
                buildTimelineTicks: 0,
                buildDictionariesTicks: 0,
                buildReentryFxTicks: 0,
                heaviestSnapshotResolveTicks: 0,
                heaviestTimelineTicks: 0,
                heaviestDictionariesTicks: 0,
                heaviestReentryTicks: 0,
                heaviestOtherTicks: 0,
                heaviestBuildType: HeaviestSpawnBuildType.None);

            Assert.Equal(1_000_000L, phases.deferredCreatedEventsMicroseconds);
            Assert.Equal(2_000_000L, phases.deferredCompletedEventsMicroseconds);
            Assert.Equal(3_000_000L, phases.observabilityCaptureMicroseconds);
            Assert.Equal(4_000_000L, phases.spawnMaxMicroseconds);
            // mainLoop = elapsed(5,000,000) - spawn(0) - destroy(0) = 5,000,000.
            Assert.Equal(5_000_000L, phases.mainLoopMicroseconds);
        }

        [Fact]
        public void BuildPlaybackBudgetPhases_NegativeMainLoop_ClampsToZero()
        {
            // spawn + destroy exceed the elapsed-at-loop-end window -> mainLoop floors to 0.
            long freq = Stopwatch.Frequency;

            PlaybackBudgetPhases phases = GhostPlaybackEngine.BuildPlaybackBudgetPhases(
                elapsedTicksAtLoopEnd: freq,       // -> 1,000,000 us elapsed
                spawnMicroseconds: 800_000,
                destroyMicroseconds: 700_000,      // spawn+destroy = 1,500,000 > 1,000,000
                deferredCreatedTicks: 0,
                deferredCompletedTicks: 0,
                observabilityTicks: 0,
                trajectoriesIterated: 0,
                overlapGhostIterationCount: 0,
                createdEventsFired: 0,
                completedEventsFired: 0,
                spawnsAttempted: 0,
                spawnsThrottled: 0,
                frameMaxSpawnTicks: 0,
                buildSnapshotResolveTicks: 0,
                buildTimelineTicks: 0,
                buildDictionariesTicks: 0,
                buildReentryFxTicks: 0,
                heaviestSnapshotResolveTicks: 0,
                heaviestTimelineTicks: 0,
                heaviestDictionariesTicks: 0,
                heaviestReentryTicks: 0,
                heaviestOtherTicks: 0,
                heaviestBuildType: HeaviestSpawnBuildType.None);

            Assert.Equal(0L, phases.mainLoopMicroseconds);
        }

        [Fact]
        public void BuildPlaybackBudgetPhases_NegativeBuildOther_ClampsToZero()
        {
            // The four sub-phase microsecond conversions sum above spawnMicroseconds, so the
            // residual buildOtherMicroseconds = spawn - sum would be negative and must floor.
            long freq = Stopwatch.Frequency;

            PlaybackBudgetPhases phases = GhostPlaybackEngine.BuildPlaybackBudgetPhases(
                elapsedTicksAtLoopEnd: freq * 10,
                spawnMicroseconds: 1_000_000,      // 1,000,000 us spawn total
                destroyMicroseconds: 0,
                deferredCreatedTicks: 0,
                deferredCompletedTicks: 0,
                observabilityTicks: 0,
                trajectoriesIterated: 0,
                overlapGhostIterationCount: 0,
                createdEventsFired: 0,
                completedEventsFired: 0,
                spawnsAttempted: 0,
                spawnsThrottled: 0,
                frameMaxSpawnTicks: 0,
                // Each sub-phase = 1,000,000 us; four of them sum to 4,000,000 > 1,000,000.
                buildSnapshotResolveTicks: freq,
                buildTimelineTicks: freq,
                buildDictionariesTicks: freq,
                buildReentryFxTicks: freq,
                heaviestSnapshotResolveTicks: 0,
                heaviestTimelineTicks: 0,
                heaviestDictionariesTicks: 0,
                heaviestReentryTicks: 0,
                heaviestOtherTicks: 0,
                heaviestBuildType: HeaviestSpawnBuildType.None);

            Assert.Equal(0L, phases.buildOtherMicroseconds);
            // Sub-phase conversions still land at their own values.
            Assert.Equal(1_000_000L, phases.buildSnapshotResolveMicroseconds);
            Assert.Equal(1_000_000L, phases.buildTimelineFromSnapshotMicroseconds);
            Assert.Equal(1_000_000L, phases.buildDictionariesMicroseconds);
            Assert.Equal(1_000_000L, phases.buildReentryFxMicroseconds);
        }

        [Fact]
        public void BuildPlaybackBudgetPhases_PositiveBuildOther_IsResidual()
        {
            // spawn(2,000,000) - sub-phases(4 x 250,000 = 1,000,000) = 1,000,000 residual.
            long freq = Stopwatch.Frequency;
            long quarterFreqTicks = freq / 4; // -> 250,000 us per sub-phase (freq divisible enough)
            long expectedSubPhase = TicksToMicroseconds(quarterFreqTicks);

            PlaybackBudgetPhases phases = GhostPlaybackEngine.BuildPlaybackBudgetPhases(
                elapsedTicksAtLoopEnd: freq * 10,
                spawnMicroseconds: 2_000_000,
                destroyMicroseconds: 0,
                deferredCreatedTicks: 0,
                deferredCompletedTicks: 0,
                observabilityTicks: 0,
                trajectoriesIterated: 0,
                overlapGhostIterationCount: 0,
                createdEventsFired: 0,
                completedEventsFired: 0,
                spawnsAttempted: 0,
                spawnsThrottled: 0,
                frameMaxSpawnTicks: 0,
                buildSnapshotResolveTicks: quarterFreqTicks,
                buildTimelineTicks: quarterFreqTicks,
                buildDictionariesTicks: quarterFreqTicks,
                buildReentryFxTicks: quarterFreqTicks,
                heaviestSnapshotResolveTicks: 0,
                heaviestTimelineTicks: 0,
                heaviestDictionariesTicks: 0,
                heaviestReentryTicks: 0,
                heaviestOtherTicks: 0,
                heaviestBuildType: HeaviestSpawnBuildType.None);

            long expectedOther = 2_000_000L - 4 * expectedSubPhase;
            Assert.Equal(expectedOther, phases.buildOtherMicroseconds);
            // spawnMicroseconds passes through unchanged (reused, never recomputed).
            Assert.Equal(2_000_000L, phases.spawnMicroseconds);
        }

        [Fact]
        public void BuildPlaybackBudgetPhases_PassThroughFields()
        {
            // Integer counters, destroyMicroseconds, and the heaviest-spawn build type
            // land in the struct unchanged.
            PlaybackBudgetPhases phases = GhostPlaybackEngine.BuildPlaybackBudgetPhases(
                elapsedTicksAtLoopEnd: 0,
                spawnMicroseconds: 111,
                destroyMicroseconds: 222,
                deferredCreatedTicks: 0,
                deferredCompletedTicks: 0,
                observabilityTicks: 0,
                trajectoriesIterated: 7,
                overlapGhostIterationCount: 13,
                createdEventsFired: 3,
                completedEventsFired: 5,
                spawnsAttempted: 9,
                spawnsThrottled: 4,
                frameMaxSpawnTicks: 0,
                buildSnapshotResolveTicks: 0,
                buildTimelineTicks: 0,
                buildDictionariesTicks: 0,
                buildReentryFxTicks: 0,
                heaviestSnapshotResolveTicks: 0,
                heaviestTimelineTicks: 0,
                heaviestDictionariesTicks: 0,
                heaviestReentryTicks: 0,
                heaviestOtherTicks: 0,
                heaviestBuildType: HeaviestSpawnBuildType.VesselSnapshot);

            Assert.Equal(111L, phases.spawnMicroseconds);
            Assert.Equal(222L, phases.destroyMicroseconds);
            Assert.Equal(7, phases.trajectoriesIterated);
            Assert.Equal(13, phases.overlapGhostIterationCount);
            Assert.Equal(3, phases.createdEventsFired);
            Assert.Equal(5, phases.completedEventsFired);
            Assert.Equal(9, phases.spawnsAttempted);
            Assert.Equal(4, phases.spawnsThrottled);
            Assert.Equal(HeaviestSpawnBuildType.VesselSnapshot, phases.heaviestSpawnBuildType);
            // explosionCleanupMicroseconds is hard-coded to 0 (FXMonger owns it).
            Assert.Equal(0L, phases.explosionCleanupMicroseconds);
        }

        #endregion

        #region IsSpawnSuppressedDeadOnArrival (R-A)

        [Fact]
        public void IsSpawnSuppressedDeadOnArrival_PastEndUT_NotHeld_True()
        {
            // currentUT past endUT, well before chainEndUT, not held -> suppress.
            Assert.True(GhostPlaybackEngine.IsSpawnSuppressedDeadOnArrival(
                currentUT: 110.0, endUT: 100.0, chainEndUT: 200.0, ghostHeld: false));
        }

        [Fact]
        public void IsSpawnSuppressedDeadOnArrival_PastChainEndUTOnly_NotHeld_True()
        {
            // currentUT before endUT but past chainEndUT, not held -> suppress
            // (original used OR: currentUT > endUT || currentUT > chainEndUT).
            Assert.True(GhostPlaybackEngine.IsSpawnSuppressedDeadOnArrival(
                currentUT: 150.0, endUT: 200.0, chainEndUT: 100.0, ghostHeld: false));
        }

        [Fact]
        public void IsSpawnSuppressedDeadOnArrival_PastEnd_Held_False()
        {
            // Past both ends but held -> never suppress (the De Morgan boundary:
            // ghostHeld == true makes !ghostHeld == false, killing the AND).
            Assert.False(GhostPlaybackEngine.IsSpawnSuppressedDeadOnArrival(
                currentUT: 300.0, endUT: 100.0, chainEndUT: 200.0, ghostHeld: true));
        }

        [Fact]
        public void IsSpawnSuppressedDeadOnArrival_BeforeBothEnds_NotHeld_False()
        {
            // In range of both ends, not held -> not dead-on-arrival, do not suppress.
            Assert.False(GhostPlaybackEngine.IsSpawnSuppressedDeadOnArrival(
                currentUT: 50.0, endUT: 100.0, chainEndUT: 200.0, ghostHeld: false));
        }

        [Fact]
        public void IsSpawnSuppressedDeadOnArrival_AtEndUTBoundary_False()
        {
            // Original predicate uses strict > , so currentUT == endUT (and == chainEndUT)
            // is NOT past-end -> do not suppress.
            Assert.False(GhostPlaybackEngine.IsSpawnSuppressedDeadOnArrival(
                currentUT: 100.0, endUT: 100.0, chainEndUT: 100.0, ghostHeld: false));
        }

        #endregion
    }
}
