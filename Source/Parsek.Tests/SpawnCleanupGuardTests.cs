using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the PendingCleanup guard logic in the revert path (bug #109).
    /// Verifies that cleanup data set by the rewind path is not overwritten
    /// by the subsequent false-positive revert path.
    /// </summary>
    [Collection("Sequential")]
    public class SpawnCleanupGuardTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SpawnCleanupGuardTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        [Fact]
        public void RevertCleanupArming_WithProductionCollector_ArmsOnlySpawnedVessels()
        {
            // The collector half of the revert arming, wired the way OnLoad wires it:
            // ArmRevertCleanupData(RewindLauncher.CollectSpawnedVesselInfo). The two
            // arming cells below inject a fake collector, so neither of them can see
            // what the real one returns; this cell asserts the armed set IS that
            // return value. The unspawned recording is the discriminator: cleanup must
            // name the vessels Parsek actually spawned, not every recorded vessel
            // (the all-names collector is a separate, deliberately wider set).
            Assert.Null(RewindLauncher.PendingCleanupPids);
            Assert.Null(RewindLauncher.PendingCleanupNames);

            RecordingStore.AddRecordingWithTreeForTesting(new Recording
                { VesselName = "Probe", SpawnedVesselPersistentId = 77 });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
                { VesselName = "NeverSpawned", SpawnedVesselPersistentId = 0 });

            ParsekScenario.ArmRevertCleanupData(RewindLauncher.CollectSpawnedVesselInfo);

            Assert.NotNull(RewindLauncher.PendingCleanupPids);
            Assert.Equal(new HashSet<uint> { 77u }, RewindLauncher.PendingCleanupPids);
            Assert.NotNull(RewindLauncher.PendingCleanupNames);
            Assert.Equal(new HashSet<string> { "Probe" }, RewindLauncher.PendingCleanupNames);
        }

        [Fact]
        public void RevertToEditor_ReleasesCleanupSoTheNextLaunchDoesNotRecoverLaterSpawns()
        {
            // Revert to VAB / SPH lands in EDITOR; nothing consumes the OnFlightReady belt
            // there, so an armed set would fire on the NEXT launch with this revert's stale
            // whitelist and recover a vessel Parsek spawned at the Space Center in between.
            RewindLauncher.PendingCleanupPids = new HashSet<uint> { 42u };
            RewindLauncher.PendingCleanupNames = new HashSet<string> { "Rocket" };
            RewindLauncher.PendingRevertPreExistingPids = new HashSet<uint> { 7u };

            Assert.True(ParsekScenario.ReleaseRevertCleanupOutsideFlight(GameScenes.EDITOR));

            Assert.False(RewindLauncher.ShouldRunPendingCleanupOnFlightReady());
            Assert.Null(RewindLauncher.PendingRevertPreExistingPids);
        }

        [Fact]
        public void RevertToLaunch_KeepsCleanupArmedForOnFlightReady()
        {
            RewindLauncher.PendingCleanupPids = new HashSet<uint> { 42u };
            RewindLauncher.PendingRevertPreExistingPids = new HashSet<uint> { 7u };

            Assert.False(ParsekScenario.ReleaseRevertCleanupOutsideFlight(GameScenes.FLIGHT));

            Assert.True(RewindLauncher.ShouldRunPendingCleanupOnFlightReady());
            Assert.Equal(new HashSet<uint> { 7u }, RewindLauncher.PendingRevertPreExistingPids);
        }

        [Fact]
        public void RewindThenRevert_CleanupDataSurvives()
        {
            // Full sequence simulation: rewind sets data, then revert path fires.
            // Step 1: Rewind path sets cleanup data
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
                { VesselName = "Rocket", SpawnedVesselPersistentId = 42 });

            var (rewindPids, _) = RewindLauncher.CollectSpawnedVesselInfo();
            RewindLauncher.PendingCleanupPids = rewindPids.Count > 0 ? rewindPids : null;
            var allNames = RecordingStore.CollectAllRecordingVesselNames();
            RewindLauncher.PendingCleanupNames = allNames.Count > 0 ? allNames : null;

            // Step 2: ResetAllPlaybackState zeros spawn tracking
            RewindLauncher.ResetAllPlaybackState();

            // Step 3: CollectSpawnedVesselInfo now returns empty (PIDs are zero)
            var (emptyPids, emptyNames) = RewindLauncher.CollectSpawnedVesselInfo();
            Assert.Empty(emptyPids);
            Assert.Empty(emptyNames);

            // Step 4: Guard check (what the revert path does)
            bool alreadyHasCleanupData = RewindLauncher.PendingCleanupPids != null
                                          || RewindLauncher.PendingCleanupNames != null;
            Assert.True(alreadyHasCleanupData,
                "Cleanup data from rewind must survive through the revert path");

            // Step 5: Verify the original data is intact
            Assert.Contains(42u, RewindLauncher.PendingCleanupPids);
            Assert.Contains("Rocket", RewindLauncher.PendingCleanupNames);
        }

        [Fact]
        public void RevertCleanupArming_WhenRewindAlreadyArmedCleanup_DoesNotOverwrite()
        {
            // The PRODUCTION arming step, not a re-implementation of its guard: the
            // three cells above all rebuild the condition in the test, so none of them
            // would notice the guard going away. A rewind arms the set and then zeroes
            // live spawn tracking, so the revert load that follows collects nothing -
            // overwriting would leave those vessels in the save with nothing to clean
            // them up.
            RewindLauncher.PendingCleanupPids = new HashSet<uint> { 42u };
            RewindLauncher.PendingCleanupNames = new HashSet<string> { "Rocket" };
            int collectorCalls = 0;

            ParsekScenario.ArmRevertCleanupData(() =>
            {
                collectorCalls++;
                return (new HashSet<uint>(), new HashSet<string>());
            });

            Assert.Equal(0, collectorCalls);
            Assert.Contains(42u, RewindLauncher.PendingCleanupPids);
            Assert.Contains("Rocket", RewindLauncher.PendingCleanupNames);
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]")
                && l.Contains("revert path skipping cleanup collection")
                && l.Contains("already set (1 pid(s), 1 name(s))"));
        }

        [Fact]
        public void RevertCleanupArming_WhenNothingArmed_CollectsAndStores()
        {
            // Mirror: with nothing armed the collector DOES run and its result is what
            // gets stored, so the cell above is about the guard rather than about the
            // arming step never writing anything.
            Assert.Null(RewindLauncher.PendingCleanupPids);
            Assert.Null(RewindLauncher.PendingCleanupNames);
            int collectorCalls = 0;

            ParsekScenario.ArmRevertCleanupData(() =>
            {
                collectorCalls++;
                return (new HashSet<uint> { 7u }, new HashSet<string> { "Probe" });
            });

            Assert.Equal(1, collectorCalls);
            Assert.Contains(7u, RewindLauncher.PendingCleanupPids);
            Assert.Contains("Probe", RewindLauncher.PendingCleanupNames);
        }

        [Fact]
        public void ResetForTesting_ClearsPendingCleanupData()
        {
            RewindLauncher.PendingCleanupPids = new HashSet<uint> { 1, 2, 3 };
            RewindLauncher.PendingCleanupNames = new HashSet<string> { "A", "B" };

            RecordingStore.ResetForTesting();

            Assert.Null(RewindLauncher.PendingCleanupPids);
            Assert.Null(RewindLauncher.PendingCleanupNames);
        }

    }
}
