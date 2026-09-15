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
            // Drive the production clear itself. HandleRewindOnLoad needs a live
            // KSP, but the clear it performs after StripOrphanedSpawnedVessels is
            // RecordingStore.ClearPendingCleanupAfterRewindStrip, which is
            // headless-safe and is the single call site of the bug #134 fix.
            RecordingStore.PendingCleanupPids = new HashSet<uint> { 42, 77 };
            RecordingStore.PendingCleanupNames = new HashSet<string> { "Rocket", "Probe" };
            RecordingStore.PendingRevertPreExistingPids = new HashSet<uint> { 5 };

            RecordingStore.ClearPendingCleanupAfterRewindStrip();

            Assert.Null(RecordingStore.PendingCleanupPids);
            Assert.Null(RecordingStore.PendingCleanupNames);
            Assert.Null(RecordingStore.PendingRevertPreExistingPids);
        }

        [Fact]
        public void RewindStrip_AfterClear_OnFlightReadySkipsCleanup()
        {
            // Simulate the full sequence:
            // 1. Rewind path sets cleanup data
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
                { VesselName = "Aeris 4A", SpawnedVesselPersistentId = 100 });
            var allNames = RecordingStore.CollectAllRecordingVesselNames();
            RecordingStore.PendingCleanupNames = allNames.Count > 0 ? allNames : null;
            var (pids, _) = RecordingStore.CollectSpawnedVesselInfo();
            RecordingStore.PendingCleanupPids = pids.Count > 0 ? pids : null;

            Assert.NotNull(RecordingStore.PendingCleanupNames);
            Assert.NotNull(RecordingStore.PendingCleanupPids);

            // OnFlightReady's own gate says "run" while the data is armed.
            Assert.True(RecordingStore.ShouldRunPendingCleanupOnFlightReady());

            // 2. Strip runs (simulated), then the production clear fires.
            RecordingStore.ClearPendingCleanupAfterRewindStrip();

            // 3. OnFlightReady asks the same gate again - now it skips. Both
            // sides are the production expressions (ParsekFlight.OnFlightReady
            // calls ShouldRunPendingCleanupOnFlightReady), not a local copy.
            Assert.False(RecordingStore.ShouldRunPendingCleanupOnFlightReady(),
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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
            // The log line is now emitted by the production clear itself, so this
            // asserts what shipped rather than a copy of the message text.
            RecordingStore.PendingCleanupPids = new HashSet<uint> { 1 };
            RecordingStore.PendingCleanupNames = new HashSet<string> { "X" };

            RecordingStore.ClearPendingCleanupAfterRewindStrip();

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
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
                { VesselName = "Rocket", SpawnedVesselPersistentId = 42 });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
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
