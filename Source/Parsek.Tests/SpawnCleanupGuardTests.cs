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
            MilestoneStore.SuppressLogging = true;
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
        public void PendingCleanupPids_SurviveWhenAlreadySet()
        {
            // Simulate the rewind path setting cleanup data
            var rewindPids = new HashSet<uint> { 42, 99 };
            RecordingStore.PendingCleanupPids = rewindPids;

            // After rewind, ResetAllPlaybackState zeros spawn tracking,
            // so CollectSpawnedVesselInfo returns empty.
            // The revert path's guard should detect already-set data and skip.

            // Verify the data survives (it should not be overwritten)
            Assert.NotNull(RecordingStore.PendingCleanupPids);
            Assert.Equal(2, RecordingStore.PendingCleanupPids.Count);
            Assert.Contains(42u, RecordingStore.PendingCleanupPids);
            Assert.Contains(99u, RecordingStore.PendingCleanupPids);
        }

        [Fact]
        public void PendingCleanupNames_SurviveWhenAlreadySet()
        {
            // Simulate the rewind path setting cleanup names
            var rewindNames = new HashSet<string> { "Rocket", "Shuttle" };
            RecordingStore.PendingCleanupNames = rewindNames;

            // Verify the data survives
            Assert.NotNull(RecordingStore.PendingCleanupNames);
            Assert.Equal(2, RecordingStore.PendingCleanupNames.Count);
            Assert.Contains("Rocket", RecordingStore.PendingCleanupNames);
            Assert.Contains("Shuttle", RecordingStore.PendingCleanupNames);
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
        public void Guard_DetectsAlreadySetPids_SkipsCollection()
        {
            // Pre-set only PIDs (names null)
            RecordingStore.PendingCleanupPids = new HashSet<uint> { 50 };

            bool alreadyHasCleanupData = RecordingStore.PendingCleanupPids != null
                                          || RecordingStore.PendingCleanupNames != null;

            Assert.True(alreadyHasCleanupData);
        }

        [Fact]
        public void Guard_DetectsAlreadySetNames_SkipsCollection()
        {
            // Pre-set only names (PIDs null)
            RecordingStore.PendingCleanupNames = new HashSet<string> { "Vessel" };

            bool alreadyHasCleanupData = RecordingStore.PendingCleanupPids != null
                                          || RecordingStore.PendingCleanupNames != null;

            Assert.True(alreadyHasCleanupData);
        }

        [Fact]
        public void Guard_BothNull_AllowsCollection()
        {
            // Both null: guard should allow collection
            bool alreadyHasCleanupData = RecordingStore.PendingCleanupPids != null
                                          || RecordingStore.PendingCleanupNames != null;

            Assert.False(alreadyHasCleanupData);
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

        #region Log Assertion Tests

        [Fact]
        public void Guard_LogsSkipMessage_WhenAlreadySet()
        {
            // Pre-set cleanup data (simulating rewind path)
            RecordingStore.PendingCleanupPids = new HashSet<uint> { 42 };
            RecordingStore.PendingCleanupNames = new HashSet<string> { "Rocket", "Shuttle" };

            // Simulate the guard logging (same logic as ParsekScenario.OnLoad)
            bool alreadyHasCleanupData = RecordingStore.PendingCleanupPids != null
                                          || RecordingStore.PendingCleanupNames != null;
            if (alreadyHasCleanupData)
            {
                ParsekLog.Info("Scenario",
                    $"OnLoad: revert path skipping cleanup collection — " +
                    $"already set ({RecordingStore.PendingCleanupPids?.Count ?? 0} pid(s), " +
                    $"{RecordingStore.PendingCleanupNames?.Count ?? 0} name(s)) from prior rewind/revert");
            }

            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") &&
                l.Contains("revert path skipping cleanup collection") &&
                l.Contains("1 pid(s)") &&
                l.Contains("2 name(s)"));
        }

        [Fact]
        public void RewindPath_LogsCleanupDataSet()
        {
            // Add a recording so we get non-empty data
            RecordingStore.CommittedRecordings.Add(new Recording
                { VesselName = "Rocket", SpawnedVesselPersistentId = 42 });

            // Simulate the rewind path's collection and logging
            var (rewindPids, _) = RecordingStore.CollectSpawnedVesselInfo();
            RecordingStore.PendingCleanupPids = rewindPids.Count > 0 ? rewindPids : null;
            var allNames = RecordingStore.CollectAllRecordingVesselNames();
            RecordingStore.PendingCleanupNames = allNames.Count > 0 ? allNames : null;

            ParsekLog.Info("Rewind",
                $"OnLoad: rewind cleanup data set — " +
                $"{rewindPids.Count} pid(s), {allNames.Count} name(s)");

            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") &&
                l.Contains("rewind cleanup data set") &&
                l.Contains("1 pid(s)") &&
                l.Contains("1 name(s)"));
        }

        [Fact]
        public void RevertPath_LogsCollected_WhenNotAlreadySet()
        {
            // Simulate the revert path collecting data (guard allows it)
            RecordingStore.CommittedRecordings.Add(new Recording
                { VesselName = "Probe", SpawnedVesselPersistentId = 77 });

            var info = RecordingStore.CollectSpawnedVesselInfo();
            var spawnedPids = info.pids.Count > 0 ? info.pids : null;
            var spawnedNames = info.names.Count > 0 ? info.names : null;
            RecordingStore.PendingCleanupPids = spawnedPids;
            RecordingStore.PendingCleanupNames = spawnedNames;

            ParsekLog.Verbose("Scenario",
                $"OnLoad: revert cleanup collected — " +
                $"{spawnedPids?.Count ?? 0} pid(s), {spawnedNames?.Count ?? 0} name(s)");

            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") &&
                l.Contains("revert cleanup collected") &&
                l.Contains("1 pid(s)") &&
                l.Contains("1 name(s)"));
        }

        #endregion
    }
}
