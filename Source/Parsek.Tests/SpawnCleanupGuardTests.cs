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
            // ArmRevertCleanupData(RecordingStore.CollectSpawnedVesselInfo). The two
            // arming cells below inject a fake collector, so neither of them can see
            // what the real one returns; this cell asserts the armed set IS that
            // return value. The unspawned recording is the discriminator: cleanup must
            // name the vessels Parsek actually spawned, not every recorded vessel
            // (the all-names collector is a separate, deliberately wider set).
            Assert.Null(RecordingStore.PendingCleanupPids);
            Assert.Null(RecordingStore.PendingCleanupNames);

            RecordingStore.AddRecordingWithTreeForTesting(new Recording
                { VesselName = "Probe", SpawnedVesselPersistentId = 77 });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
                { VesselName = "NeverSpawned", SpawnedVesselPersistentId = 0 });

            ParsekScenario.ArmRevertCleanupData(RecordingStore.CollectSpawnedVesselInfo);

            Assert.NotNull(RecordingStore.PendingCleanupPids);
            Assert.Equal(new HashSet<uint> { 77u }, RecordingStore.PendingCleanupPids);
            Assert.NotNull(RecordingStore.PendingCleanupNames);
            Assert.Equal(new HashSet<string> { "Probe" }, RecordingStore.PendingCleanupNames);
        }

        [Fact]
        public void RewindThenRevert_CleanupDataSurvives()
        {
            // Full sequence simulation: rewind sets data, then revert path fires.
            // Step 1: Rewind path sets cleanup data
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
                { VesselName = "Rocket", SpawnedVesselPersistentId = 42 });

            var (rewindPids, _) = RecordingStore.CollectSpawnedVesselInfo();
            RecordingStore.PendingCleanupPids = rewindPids.Count > 0 ? rewindPids : null;
            var allNames = RecordingStore.CollectAllRecordingVesselNames();
            RecordingStore.PendingCleanupNames = allNames.Count > 0 ? allNames : null;

            // Step 2: ResetAllPlaybackState zeros spawn tracking
            RecordingStore.ResetAllPlaybackState();

            // Step 3: CollectSpawnedVesselInfo now returns empty (PIDs are zero)
            var (emptyPids, emptyNames) = RecordingStore.CollectSpawnedVesselInfo();
            Assert.Empty(emptyPids);
            Assert.Empty(emptyNames);

            // Step 4: Guard check (what the revert path does)
            bool alreadyHasCleanupData = RecordingStore.PendingCleanupPids != null
                                          || RecordingStore.PendingCleanupNames != null;
            Assert.True(alreadyHasCleanupData,
                "Cleanup data from rewind must survive through the revert path");

            // Step 5: Verify the original data is intact
            Assert.Contains(42u, RecordingStore.PendingCleanupPids);
            Assert.Contains("Rocket", RecordingStore.PendingCleanupNames);
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
            RecordingStore.PendingCleanupPids = new HashSet<uint> { 42u };
            RecordingStore.PendingCleanupNames = new HashSet<string> { "Rocket" };
            int collectorCalls = 0;

            ParsekScenario.ArmRevertCleanupData(() =>
            {
                collectorCalls++;
                return (new HashSet<uint>(), new HashSet<string>());
            });

            Assert.Equal(0, collectorCalls);
            Assert.Contains(42u, RecordingStore.PendingCleanupPids);
            Assert.Contains("Rocket", RecordingStore.PendingCleanupNames);
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
            Assert.Null(RecordingStore.PendingCleanupPids);
            Assert.Null(RecordingStore.PendingCleanupNames);
            int collectorCalls = 0;

            ParsekScenario.ArmRevertCleanupData(() =>
            {
                collectorCalls++;
                return (new HashSet<uint> { 7u }, new HashSet<string> { "Probe" });
            });

            Assert.Equal(1, collectorCalls);
            Assert.Contains(7u, RecordingStore.PendingCleanupPids);
            Assert.Contains("Probe", RecordingStore.PendingCleanupNames);
        }

        [Fact]
        public void ResetForTesting_ClearsPendingCleanupData()
        {
            RecordingStore.PendingCleanupPids = new HashSet<uint> { 1, 2, 3 };
            RecordingStore.PendingCleanupNames = new HashSet<string> { "A", "B" };

            RecordingStore.ResetForTesting();

            Assert.Null(RecordingStore.PendingCleanupPids);
            Assert.Null(RecordingStore.PendingCleanupNames);
        }

    }
}
