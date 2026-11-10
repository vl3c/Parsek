using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the phases extracted from <c>ParsekScenario.HandleRewindOnLoad</c>
    /// (<c>HandleRewindOnLoadPhase*</c>). Their place inside the method is pinned by
    /// <see cref="ParsekScenarioHandleRewindOnLoadCharacterizationTests"/>; these cells pin what
    /// each one does. Touches shared static state (ParsekLog, RecordingStore, RewindLauncher,
    /// RewindContext, SceneEntryVesselScope), hence Sequential.
    /// </summary>
    [Collection("Sequential")]
    public class ParsekScenarioHandleRewindOnLoadPhaseTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ParsekScenarioHandleRewindOnLoadPhaseTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            ClearCleanupStatics();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            ClearCleanupStatics();
            SceneEntryVesselScope.RewindReplayTargetSourcePid = 0;
            SceneEntryVesselScope.RewindReplayTargetRecordingId = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void ClearCleanupStatics()
        {
            RewindLauncher.PendingCleanupPids = null;
            RewindLauncher.PendingCleanupNames = null;
            RewindLauncher.PendingRevertPreExistingPids = null;
        }

        // ---- cleanup data ----

        [Fact]
        public void CollectCleanupData_ArmsTheSpawnedPidsAndEveryRecordingName()
        {
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
                { RecordingId = "a", VesselName = "Aeris", SpawnedVesselPersistentId = 100 });
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
                { RecordingId = "b", VesselName = "Probe" });

            ParsekScenario.HandleRewindOnLoadPhaseCollectCleanupData();

            Assert.Equal(new HashSet<uint> { 100 }, RewindLauncher.PendingCleanupPids);
            Assert.Equal(new HashSet<string> { "Aeris", "Probe" }, RewindLauncher.PendingCleanupNames);
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("OnLoad: rewind cleanup data set") && l.Contains("1 pid(s), 2 name(s)"));
        }

        [Fact]
        public void CollectCleanupData_NothingCommitted_LeavesBothSetsNull()
        {
            RewindLauncher.PendingCleanupPids = new HashSet<uint> { 9 };
            RewindLauncher.PendingCleanupNames = new HashSet<string> { "Stale" };

            ParsekScenario.HandleRewindOnLoadPhaseCollectCleanupData();

            Assert.Null(RewindLauncher.PendingCleanupPids);
            Assert.Null(RewindLauncher.PendingCleanupNames);
            Assert.Contains(logLines, l => l.Contains("0 pid(s), 0 name(s)"));
        }

        // ---- playback reset and source protection ----

        [Fact]
        public void ResetPlaybackAndProtectSource_ResetsAllButTheKeptHistoryAndMarksOnlyTheSource()
        {
            const uint sourcePid = 4242u;
            var source = new Recording
            {
                RecordingId = "source", VesselName = "Kerbal X", TreeId = "t1",
                VesselPersistentId = sourcePid, VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Splashed,
                VesselSpawned = true, SpawnedVesselPersistentId = 700,
            };
            var future = new Recording
            {
                RecordingId = "future", VesselName = "Kerbal X Lander", TreeId = "t1",
                VesselPersistentId = sourcePid, VesselSnapshot = new ConfigNode("VESSEL"),
                ExplicitStartUT = 500, ExplicitEndUT = 600, TerminalStateValue = TerminalState.Landed,
                VesselSpawned = true, SpawnedVesselPersistentId = 701,
            };
            var kept = new Recording
            {
                RecordingId = "kept", VesselName = "Station", TreeId = "t0",
                VesselPersistentId = 9u, VesselSpawned = true, SpawnedVesselPersistentId = 555,
            };
            RecordingStore.AddRecordingWithTreeForTesting(source);
            RecordingStore.AddRecordingWithTreeForTesting(future);
            RecordingStore.AddRecordingWithTreeForTesting(kept);
            RewindContext.BeginRewind(100.0, default(BudgetSummary), 0, 0, 0);
            RewindContext.SetHistoricalSpawnKeepRecordingIds(new HashSet<string> { "kept" });
            SceneEntryVesselScope.RewindReplayTargetSourcePid = sourcePid;
            SceneEntryVesselScope.RewindReplayTargetRecordingId = "source";

            ParsekScenario.HandleRewindOnLoadPhaseResetPlaybackAndProtectSource(
                new List<Recording> { source, future, kept });

            Assert.False(source.VesselSpawned);
            Assert.Equal(0u, source.SpawnedVesselPersistentId);
            Assert.Equal(0u, future.SpawnedVesselPersistentId);
            Assert.True(kept.VesselSpawned);
            Assert.Equal(555u, kept.SpawnedVesselPersistentId);
            Assert.True(source.SpawnSuppressedByRewind);
            Assert.False(future.SpawnSuppressedByRewind);
            Assert.False(kept.SpawnSuppressedByRewind);
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("OnLoad: resetting playback state for 3 recordings + 0 trees"));
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("SpawnSuppressedByRewind scope evaluated") && l.Contains("protectedActiveSource=1;"));
        }

        [Fact]
        public void ResetPlaybackAndProtectSource_NoReplayTarget_ProtectsNothing()
        {
            var rec = new Recording
            {
                RecordingId = "r", VesselName = "Probe", TreeId = "t1",
                VesselPersistentId = 77u, VesselSpawned = true, SpawnedVesselPersistentId = 10,
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            RewindContext.BeginRewind(100.0, default(BudgetSummary), 0, 0, 0);

            ParsekScenario.HandleRewindOnLoadPhaseResetPlaybackAndProtectSource(new List<Recording> { rec });

            Assert.False(rec.VesselSpawned);
            Assert.False(rec.SpawnSuppressedByRewind);
            Assert.Contains(logLines, l => l.Contains("protectedActiveSource=0;"));
        }

        // ---- future-vessel strips ----
        // HighLogic.CurrentGame reads HighLogic.fetch, which the headless host never creates, so
        // both flightState strips see no game here; the strips themselves are covered by
        // RewindPrelaunchStripTests, ForeignAdoptionRewindStripTests and RewindOwnerStripTests.
        // These cells pin what the phase does around them on every rewind.

        [Fact]
        public void StripFutureVessels_ClearsThePendingCleanupAndDropsTheQuicksavePids()
        {
            RewindLauncher.PendingCleanupPids = new HashSet<uint> { 42 };
            RewindLauncher.PendingCleanupNames = new HashSet<string> { "Rocket" };
            RewindLauncher.PendingRevertPreExistingPids = new HashSet<uint> { 5 };
            RewindContext.SetQuicksaveVesselPids(new HashSet<uint> { 1, 2 });

            ParsekScenario.HandleRewindOnLoadPhaseStripFutureVessels();

            Assert.Null(RewindLauncher.PendingCleanupPids);
            Assert.Null(RewindLauncher.PendingCleanupNames);
            Assert.Null(RewindLauncher.PendingRevertPreExistingPids);
            Assert.Null(RewindContext.RewindQuicksaveVesselPids);
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("OnLoad: cleared PendingCleanupPids/Names after strip"));
        }

        [Fact]
        public void StripFutureVessels_NoQuicksavePids_StillClearsThePendingCleanup()
        {
            RewindLauncher.PendingCleanupNames = new HashSet<string> { "Rocket" };

            ParsekScenario.HandleRewindOnLoadPhaseStripFutureVessels();

            Assert.Null(RewindLauncher.PendingCleanupNames);
            Assert.Null(RewindContext.RewindQuicksaveVesselPids);
            Assert.DoesNotContain(logLines, l => l.Contains("SetQuicksaveVesselPids"));
        }
    }
}
