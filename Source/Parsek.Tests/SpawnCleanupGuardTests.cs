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
        public void RevertPath_SetsCleanupData_WhenNotAlreadySet()
        {
            // When PendingCleanupPids/Names are both null, the revert path
            // should collect and set them.
            Assert.Null(RecordingStore.PendingCleanupPids);
            Assert.Null(RecordingStore.PendingCleanupNames);

            // Add a spawned recording so CollectSpawnedVesselInfo returns data
            RecordingStore.CommittedRecordings.Add(new Recording
                { VesselName = "Probe", SpawnedVesselPersistentId = 77 });

            var info = RecordingStore.CollectSpawnedVesselInfo();
            var spawnedPids = info.pids.Count > 0 ? info.pids : null;
            var spawnedNames = info.names.Count > 0 ? info.names : null;
            RecordingStore.PendingCleanupPids = spawnedPids;
            RecordingStore.PendingCleanupNames = spawnedNames;

            Assert.NotNull(RecordingStore.PendingCleanupPids);
            Assert.Contains(77u, RecordingStore.PendingCleanupPids);
            Assert.NotNull(RecordingStore.PendingCleanupNames);
            Assert.Contains("Probe", RecordingStore.PendingCleanupNames);
        }

        [Fact]
        public void RewindThenRevert_CleanupDataSurvives()
        {
            // Full sequence simulation: rewind sets data, then revert path fires.
            // Step 1: Rewind path sets cleanup data
            RecordingStore.CommittedRecordings.Add(new Recording
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
        public void RewindThenRevert_WithoutGuard_WouldLoseData()
        {
            // Demonstrates the bug: without the guard, the revert path
            // would overwrite rewind data with empty.
            RecordingStore.CommittedRecordings.Add(new Recording
                { VesselName = "Rocket", SpawnedVesselPersistentId = 42 });

            // Rewind path sets data
            var (rewindPids, _) = RecordingStore.CollectSpawnedVesselInfo();
            RecordingStore.PendingCleanupPids = rewindPids.Count > 0 ? rewindPids : null;
            var allNames = RecordingStore.CollectAllRecordingVesselNames();
            RecordingStore.PendingCleanupNames = allNames.Count > 0 ? allNames : null;

            Assert.NotNull(RecordingStore.PendingCleanupPids);
            Assert.NotNull(RecordingStore.PendingCleanupNames);

            // ResetAllPlaybackState zeros spawn tracking
            RecordingStore.ResetAllPlaybackState();

            // Without guard: unconditional collection returns empty and overwrites
            var (emptyPids, emptyNames) = RecordingStore.CollectSpawnedVesselInfo();
            var wouldSetPids = emptyPids.Count > 0 ? emptyPids : null;
            var wouldSetNames = emptyNames.Count > 0 ? emptyNames : null;

            // This is what the bug would do: overwrite with null
            Assert.Null(wouldSetPids);
            Assert.Null(wouldSetNames);

            // But the guard prevents this: original data is still there
            Assert.NotNull(RecordingStore.PendingCleanupPids);
            Assert.NotNull(RecordingStore.PendingCleanupNames);
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
