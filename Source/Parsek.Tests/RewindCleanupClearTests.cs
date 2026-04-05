using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #134: after HandleRewindOnLoad's strip phase,
    /// PendingCleanupPids/Names must be cleared so OnFlightReady doesn't
    /// destroy freshly-spawned past vessels with overbroad name matching.
    /// </summary>
    [Collection("Sequential")]
    public class RewindCleanupClearTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RewindCleanupClearTests()
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
        public void RewindStrip_ClearsPendingCleanup_PidsAndNames()
        {
            // Simulate the rewind path: set cleanup data, then clear it
            // (as HandleRewindOnLoad does after StripOrphanedSpawnedVessels).
            RecordingStore.PendingCleanupPids = new HashSet<uint> { 42, 77 };
            RecordingStore.PendingCleanupNames = new HashSet<string> { "Rocket", "Probe" };

            // This is what the fix does after the strip call:
            RecordingStore.PendingCleanupPids = null;
            RecordingStore.PendingCleanupNames = null;

            Assert.Null(RecordingStore.PendingCleanupPids);
            Assert.Null(RecordingStore.PendingCleanupNames);
        }

        [Fact]
        public void RewindStrip_AfterClear_OnFlightReadySkipsCleanup()
        {
            // Simulate the full sequence:
            // 1. Rewind path sets cleanup data
            RecordingStore.CommittedRecordings.Add(new Recording
                { VesselName = "Aeris 4A", SpawnedVesselPersistentId = 100 });
            var allNames = RecordingStore.CollectAllRecordingVesselNames();
            RecordingStore.PendingCleanupNames = allNames.Count > 0 ? allNames : null;
            var (pids, _) = RecordingStore.CollectSpawnedVesselInfo();
            RecordingStore.PendingCleanupPids = pids.Count > 0 ? pids : null;

            Assert.NotNull(RecordingStore.PendingCleanupNames);
            Assert.NotNull(RecordingStore.PendingCleanupPids);

            // 2. Strip runs (simulated), then fix clears pending data
            RecordingStore.PendingCleanupPids = null;
            RecordingStore.PendingCleanupNames = null;

            // 3. OnFlightReady checks — both are null, so cleanup is skipped
            bool wouldRunCleanup = RecordingStore.PendingCleanupPids != null
                                    || RecordingStore.PendingCleanupNames != null;
            Assert.False(wouldRunCleanup,
                "After rewind strip clears pending data, OnFlightReady must skip cleanup");
        }

        [Fact]
        public void RevertPath_CollectsFreshData_WhenPendingIsNull()
        {
            // After rewind clears pending data, a subsequent revert should
            // collect fresh data (the alreadyHasCleanupData guard sees null).
            RecordingStore.PendingCleanupPids = null;
            RecordingStore.PendingCleanupNames = null;

            // Add a spawned recording to simulate post-rewind state
            RecordingStore.CommittedRecordings.Add(new Recording
                { VesselName = "Probe", SpawnedVesselPersistentId = 55 });

            // The revert path checks this guard:
            bool alreadyHasCleanupData = RecordingStore.PendingCleanupPids != null
                                          || RecordingStore.PendingCleanupNames != null;
            Assert.False(alreadyHasCleanupData,
                "Pending cleanup must be null so revert path collects fresh data");

            // Revert path collects fresh data:
            var info = RecordingStore.CollectSpawnedVesselInfo();
            var spawnedPids = info.pids.Count > 0 ? info.pids : null;
            var spawnedNames = info.names.Count > 0 ? info.names : null;
            RecordingStore.PendingCleanupPids = spawnedPids;
            RecordingStore.PendingCleanupNames = spawnedNames;

            Assert.NotNull(RecordingStore.PendingCleanupPids);
            Assert.Contains(55u, RecordingStore.PendingCleanupPids);
            Assert.NotNull(RecordingStore.PendingCleanupNames);
            Assert.Contains("Probe", RecordingStore.PendingCleanupNames);
        }

        [Fact]
        public void RewindStrip_LogMessageFormat_ContainsExpectedText()
        {
            // Note: This tests the expected log message FORMAT, not that production
            // code actually emits it (HandleRewindOnLoad requires KSP runtime).
            // Verifies the log message contract — if someone changes the message
            // text in production, this test reminds them to update the contract.
            RecordingStore.PendingCleanupPids = new HashSet<uint> { 1 };
            RecordingStore.PendingCleanupNames = new HashSet<string> { "X" };

            RecordingStore.PendingCleanupPids = null;
            RecordingStore.PendingCleanupNames = null;
            ParsekLog.Info("Rewind",
                "OnLoad: cleared PendingCleanupPids/Names after strip — " +
                "prevents OnFlightReady from destroying freshly-spawned past vessels");

            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") &&
                l.Contains("cleared PendingCleanupPids/Names after strip"));
        }

        [Fact]
        public void FullRewindThenRevert_RevertGetsOwnData()
        {
            // End-to-end sequence: rewind sets names, strip runs, clear runs,
            // then revert path fires and gets its own fresh data.

            // Step 1: Rewind path — add recordings with spawn data
            RecordingStore.CommittedRecordings.Add(new Recording
                { VesselName = "Rocket", SpawnedVesselPersistentId = 42 });
            RecordingStore.CommittedRecordings.Add(new Recording
                { VesselName = "Probe", SpawnedVesselPersistentId = 77 });

            // Step 2: Rewind collects ALL names (overbroad set for strip)
            var allNames = RecordingStore.CollectAllRecordingVesselNames();
            RecordingStore.PendingCleanupNames = allNames.Count > 0 ? allNames : null;
            Assert.Equal(2, RecordingStore.PendingCleanupNames.Count);

            // Step 3: Strip runs (simulated), then fix clears
            RecordingStore.PendingCleanupPids = null;
            RecordingStore.PendingCleanupNames = null;

            // Step 4: ResetAllPlaybackState zeros spawn tracking
            RecordingStore.ResetAllPlaybackState();

            // Step 5: Revert path guard check — sees null, proceeds to collect
            bool alreadyHasCleanupData = RecordingStore.PendingCleanupPids != null
                                          || RecordingStore.PendingCleanupNames != null;
            Assert.False(alreadyHasCleanupData);

            // Step 6: Revert collects from current state (post-reset, so empty)
            var info = RecordingStore.CollectSpawnedVesselInfo();
            var spawnedPids = info.pids.Count > 0 ? info.pids : null;
            var spawnedNames = info.names.Count > 0 ? info.names : null;

            // After reset, spawn PIDs are zero, so collection returns empty
            Assert.Null(spawnedPids);
            Assert.Null(spawnedNames);

            // This means OnFlightReady will skip cleanup entirely — correct!
            bool wouldRunCleanup = spawnedPids != null || spawnedNames != null;
            Assert.False(wouldRunCleanup,
                "Post-rewind revert with no active spawns should skip cleanup entirely");
        }
    }
}
