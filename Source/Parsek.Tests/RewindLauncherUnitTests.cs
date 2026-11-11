using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct cells for the RewindLauncher units the gates and the save pre-process are made
    /// of: the per-recording playback reset, the two refusal pre-checks, the entry-point
    /// refusals that run before any KSP call, the temp-copy delete, and the pre-process edges
    /// (missing FLIGHTSTATE, bad UT, the windback clamp, the PID strip and the surviving-PID
    /// whitelist).
    /// </summary>
    [Collection("Sequential")]
    public class RewindLauncherUnitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> screenMessages = new List<string>();
        private readonly string tempDir;

        public RewindLauncherUnitTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            WarpToTimeRequest.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ScreenMessageSinkForTesting = (msg, _) => screenMessages.Add(msg);

            tempDir = Path.Combine(Path.GetTempPath(), "parsek_rwl_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekScenario.ResetInstanceForTesting();
            WarpToTimeRequest.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
            catch { }
        }

        // ---------- ResetRecordingPlaybackFields ----------

        private static Recording MakeSpawnedRecording(string id = "rec-a")
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Ship",
                VesselSpawned = true,
                VesselDestroyed = true,
                SpawnAttempts = 3,
                SpawnDeathCount = 2,
                SpawnedVesselPersistentId = 4242u,
                CollisionBlockCount = 5,
                SpawnAbandoned = true,
                WalkbackExhausted = true,
                DuplicateBlockerRecovered = true,
                TerminalSpawnSafetyDeferred = true,
                TerminalSpawnSafetyReason = "deferred",
                LastAppliedResourceIndex = 7,
                SpawnSuppressedByRewind = true,
                SpawnSuppressedByRewindReason = "active-source",
                SpawnSuppressedByRewindUT = 120.0,
                SceneExitSituation = 4,
                TerminalStateValue = TerminalState.Orbiting,
            };
        }

        private static void AssertPerPlaybackFieldsCleared(Recording rec)
        {
            Assert.Equal(-1, rec.LastAppliedResourceIndex);
            Assert.False(rec.SpawnSuppressedByRewind);
            Assert.Null(rec.SpawnSuppressedByRewindReason);
            Assert.True(double.IsNaN(rec.SpawnSuppressedByRewindUT));
            Assert.Equal(-1, rec.SceneExitSituation);
        }

        [Fact]
        public void PlaybackReset_NotKept_ZeroesTheSpawnBookkeeping()
        {
            var rec = MakeSpawnedRecording();

            RewindLauncher.ResetRecordingPlaybackFields(rec);

            Assert.False(rec.VesselSpawned);
            Assert.False(rec.VesselDestroyed);
            Assert.Equal(0, rec.SpawnAttempts);
            Assert.Equal(0, rec.SpawnDeathCount);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.Equal(0, rec.CollisionBlockCount);
            Assert.False(rec.SpawnAbandoned);
            Assert.False(rec.WalkbackExhausted);
            Assert.False(rec.DuplicateBlockerRecovered);
            Assert.False(rec.TerminalSpawnSafetyDeferred);
            Assert.Null(rec.TerminalSpawnSafetyReason);
            AssertPerPlaybackFieldsCleared(rec);
            // The terminal verdict is recorded content, never reset.
            Assert.Equal(TerminalState.Orbiting, rec.TerminalStateValue);
        }

        [Fact]
        public void PlaybackReset_Kept_KeepsTheSpawnLinkButClearsThePerPlaybackFields()
        {
            var rec = MakeSpawnedRecording();
            var kept = new HashSet<string>(StringComparer.Ordinal);

            RewindLauncher.ResetRecordingPlaybackFields(
                rec, new HashSet<string> { "rec-a" }, kept);

            Assert.True(rec.VesselSpawned);
            Assert.True(rec.VesselDestroyed);
            Assert.Equal(3, rec.SpawnAttempts);
            Assert.Equal(2, rec.SpawnDeathCount);
            Assert.Equal(4242u, rec.SpawnedVesselPersistentId);
            Assert.Equal(5, rec.CollisionBlockCount);
            Assert.True(rec.SpawnAbandoned);
            Assert.True(rec.WalkbackExhausted);
            Assert.True(rec.DuplicateBlockerRecovered);
            Assert.True(rec.TerminalSpawnSafetyDeferred);
            AssertPerPlaybackFieldsCleared(rec);
            Assert.Contains("rec-a", kept);
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("Playback reset kept spawn state for committed history \"Ship\"")
                && l.Contains("id=rec-a spawnedPid=4242 vesselSpawned=True"));
        }

        [Fact]
        public void PlaybackReset_Kept_LogsOncePerRecordingAcrossCalls()
        {
            var keep = new HashSet<string> { "rec-a" };
            var kept = new HashSet<string>(StringComparer.Ordinal);

            RewindLauncher.ResetRecordingPlaybackFields(MakeSpawnedRecording(), keep, kept);
            RewindLauncher.ResetRecordingPlaybackFields(MakeSpawnedRecording(), keep, kept);

            Assert.Single(logLines, l => l.Contains("Playback reset kept spawn state"));
        }

        [Fact]
        public void PlaybackReset_Kept_WithoutALogSet_KeepsQuietly()
        {
            var rec = MakeSpawnedRecording();

            RewindLauncher.ResetRecordingPlaybackFields(rec, new HashSet<string> { "rec-a" }, null);

            Assert.True(rec.VesselSpawned);
            Assert.DoesNotContain(logLines, l => l.Contains("Playback reset kept spawn state"));
        }

        [Fact]
        public void PlaybackReset_KeepSetNamingAnotherRecording_Resets()
        {
            var rec = MakeSpawnedRecording();

            RewindLauncher.ResetRecordingPlaybackFields(rec, new HashSet<string> { "rec-b" }, null);

            Assert.False(rec.VesselSpawned);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
        }

        [Fact]
        public void PlaybackReset_EmptyRecordingIdIsNeverKept_EvenWhenTheSetHoldsEmpty()
        {
            var rec = MakeSpawnedRecording(id: "");

            RewindLauncher.ResetRecordingPlaybackFields(rec, new HashSet<string> { "" }, null);

            Assert.False(rec.VesselSpawned);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
        }

        [Fact]
        public void PlaybackReset_LogsTheSuppressionItClears_WithItsReason()
        {
            var rec = MakeSpawnedRecording();

            RewindLauncher.ResetRecordingPlaybackFields(rec);

            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("SpawnSuppressedByRewind reset: \"Ship\" id=rec-a reason=active-source"));
        }

        [Fact]
        public void PlaybackReset_SuppressionWithoutReason_LogsNone()
        {
            var rec = MakeSpawnedRecording();
            rec.SpawnSuppressedByRewindReason = null;

            RewindLauncher.ResetRecordingPlaybackFields(rec);

            Assert.Contains(logLines, l => l.Contains("SpawnSuppressedByRewind reset:")
                && l.Contains("reason=<none>"));
        }

        [Fact]
        public void PlaybackReset_NoSuppression_LogsNothingAboutIt()
        {
            var rec = MakeSpawnedRecording();
            rec.SpawnSuppressedByRewind = false;

            RewindLauncher.ResetRecordingPlaybackFields(rec);

            Assert.DoesNotContain(logLines, l => l.Contains("SpawnSuppressedByRewind reset:"));
            AssertPerPlaybackFieldsCleared(rec);
        }

        [Fact]
        public void PlaybackReset_SuppressLogging_WritesNoLines()
        {
            RecordingStore.SuppressLogging = true;
            var rec = MakeSpawnedRecording();

            RewindLauncher.ResetRecordingPlaybackFields(
                rec, new HashSet<string> { "rec-a" }, new HashSet<string>());
            RewindLauncher.ResetRecordingPlaybackFields(MakeSpawnedRecording("rec-b"));

            Assert.DoesNotContain(logLines, l => l.Contains("[Rewind]"));
        }

        // ---------- CanRewindPreFileCheck ----------

        [Fact]
        public void RewindPreCheck_AllClear_PassesWithEmptyReason()
        {
            Assert.True(RewindLauncher.CanRewindPreFileCheck("parsek_rw_x", out string reason, false));
            Assert.Equal("", reason);
        }

        [Fact]
        public void RewindPreCheck_RewindInProgress_WinsOverEveryOtherRefusal()
        {
            RewindContext.BeginRewind(10.0, default(BudgetSummary), 0, 0, 0f);
            PendingTreeSlot.StashPendingTree(new RecordingTree());

            Assert.False(RewindLauncher.CanRewindPreFileCheck(null, out string reason, true));
            Assert.Equal("Rewind already in progress", reason);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void RewindPreCheck_NoSave_WinsOverRecordingAndPendingTree(string save)
        {
            PendingTreeSlot.StashPendingTree(new RecordingTree());

            Assert.False(RewindLauncher.CanRewindPreFileCheck(save, out string reason, true));
            Assert.Equal("No rewind save available", reason);
        }

        [Fact]
        public void RewindPreCheck_Recording_WinsOverPendingTree()
        {
            PendingTreeSlot.StashPendingTree(new RecordingTree());

            Assert.False(RewindLauncher.CanRewindPreFileCheck("s", out string reason, true));
            Assert.Equal("Stop recording before rewinding", reason);
        }

        [Fact]
        public void RewindPreCheck_PendingTree_Refuses()
        {
            PendingTreeSlot.StashPendingTree(new RecordingTree());

            Assert.False(RewindLauncher.CanRewindPreFileCheck("s", out string reason, false));
            Assert.Equal("Merge or discard pending tree first", reason);
        }

        // ---------- CanFastForwardPreRuntime ----------

        private static Recording MakeRecordingWithPoints()
        {
            var rec = new Recording { RecordingId = "ff", VesselName = "FF" };
            rec.Points.Add(new TrajectoryPoint { ut = 100 });
            rec.Points.Add(new TrajectoryPoint { ut = 200 });
            return rec;
        }

        [Fact]
        public void FastForwardPreCheck_AllClear_PassesWithEmptyReason()
        {
            Assert.True(RewindLauncher.CanFastForwardPreRuntime(
                MakeRecordingWithPoints(), out string reason, false));
            Assert.Equal("", reason);
        }

        [Fact]
        public void FastForwardPreCheck_RewindInProgress_WinsOverAMissingRecording()
        {
            RewindContext.BeginRewind(10.0, default(BudgetSummary), 0, 0, 0f);

            Assert.False(RewindLauncher.CanFastForwardPreRuntime(null, out string reason, true));
            Assert.Equal("Rewind already in progress", reason);
        }

        [Fact]
        public void FastForwardPreCheck_NullOrPointlessRecording_IsNotAvailable()
        {
            Assert.False(RewindLauncher.CanFastForwardPreRuntime(null, out string reason, true));
            Assert.Equal("Recording not available", reason);

            Assert.False(RewindLauncher.CanFastForwardPreRuntime(
                new Recording(), out reason, true));
            Assert.Equal("Recording not available", reason);
        }

        [Fact]
        public void FastForwardPreCheck_Recording_WinsOverPendingTree()
        {
            PendingTreeSlot.StashPendingTree(new RecordingTree());

            Assert.False(RewindLauncher.CanFastForwardPreRuntime(
                MakeRecordingWithPoints(), out string reason, true));
            Assert.Equal("Stop recording before fast-forwarding", reason);
        }

        [Fact]
        public void FastForwardPreCheck_PendingTree_Refuses()
        {
            PendingTreeSlot.StashPendingTree(new RecordingTree());

            Assert.False(RewindLauncher.CanFastForwardPreRuntime(
                MakeRecordingWithPoints(), out string reason, false));
            Assert.Equal("Merge or discard pending tree first", reason);
        }

        // ---------- Entry-point refusals before any KSP call ----------

        private static void InstallScenario(string journalPhase, int supersedeRows)
        {
            var scenario = new ParsekScenario();
            if (journalPhase != null)
                scenario.ActiveMergeJournal = new MergeJournal { Phase = journalPhase };
            for (int i = 0; i < supersedeRows; i++)
                scenario.RecordingSupersedes.Add(new RecordingSupersedeRelation());
            ParsekScenario.SetInstanceForTesting(scenario);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void CareerStart_NoSaveName_RefusesWithAnError(string save)
        {
            Assert.False(RewindLauncher.InitiateRewindToCareerStart(save));
            Assert.Contains(logLines, l => l.Contains("[ERROR][Rewind]")
                && l.Contains("InitiateRewindToCareerStart: null/empty save name"));
            Assert.False(RewindContext.IsRewinding);
        }

        [Fact]
        public void CareerStart_UnfinishedJournal_RefusesAndTellsThePlayer()
        {
            InstallScenario(MergeJournal.Phases.Supersede, supersedeRows: 0);

            Assert.False(RewindLauncher.InitiateRewindToCareerStart("parsek_start"));

            Assert.Contains(logLines, l => l.Contains("[ERROR][Rewind]")
                && l.Contains("Cannot warp to game start during an active re-fly merge")
                && l.Contains("journalPhase=Supersede"));
            Assert.Contains("Cannot warp to game start during an active re-fly merge", screenMessages);
            Assert.False(RewindContext.IsRewinding);
        }

        [Fact]
        public void CareerStart_CompleteJournal_FallsThroughToTheSupersedeRefusal()
        {
            InstallScenario(MergeJournal.Phases.Complete, supersedeRows: 2);

            Assert.False(RewindLauncher.InitiateRewindToCareerStart("parsek_start"));

            Assert.Empty(screenMessages);
            Assert.Contains(logLines, l => l.Contains("[WARN][Rewind]")
                && l.Contains("InitiateRewindToCareerStart refused: 2 supersede relation(s) present"));
            Assert.False(RewindContext.IsRewinding);
        }

        [Fact]
        public void InitiateRewind_NoOwner_LogsAndReturns()
        {
            var rec = new Recording { RecordingId = "orphan", VesselName = "Lost" };

            Assert.False(RewindLauncher.InitiateRewind(rec));

            Assert.Contains(logLines, l => l.Contains("[ERROR][Rewind]")
                && l.Contains("InitiateRewind: no rewind owner for 'Lost' (id=orphan)"));
            Assert.False(RewindContext.IsRewinding);
        }

        [Fact]
        public void InitiateRewind_UnfinishedJournal_RefusesBeforeArming()
        {
            InstallScenario(MergeJournal.Phases.Begin, supersedeRows: 0);
            var rec = new Recording { RecordingId = "own", VesselName = "Owner", RewindSaveFileName = "parsek_rw_own" };

            Assert.False(RewindLauncher.InitiateRewind(rec));

            Assert.Contains(logLines, l => l.Contains("[ERROR][Rewind]")
                && l.Contains("Cannot rewind during an active re-fly merge")
                && l.Contains("journalPhase=Begin"));
            Assert.Contains("Cannot rewind during an active re-fly merge", screenMessages);
            Assert.False(RewindContext.IsRewinding);
        }

        // ---------- Warp to time: a rewind that does not launch drops the pending warp ----------
        // WARP-REQUEST-SURVIVES-A-FAILED-REWIND: Warp to time arms the forward jump before the
        // rewind; a launch that refuses or fails stays in this scene, so the request must go,
        // or the next Space Center load of the process jumps the clock to the old target.

        private void AssertPendingWarpDroppedFor(string reason)
        {
            Assert.False(WarpToTimeRequest.HasPending);
            Assert.Equal(0.0, WarpToTimeRequest.TargetUT);
            Assert.Contains(logLines, l => l.Contains("[INFO][WarpTime]")
                && l.Contains("Pending warp dropped: targetUT=86400.0 reason=" + reason));
        }

        [Fact]
        public void WarpToTime_RewindWithNoOwner_DropsThePendingWarp()
        {
            var rec = new Recording { RecordingId = "orphan", VesselName = "Lost" };

            bool launched = WarpToTimeController.ArmPendingWarpAndLaunch(
                86400.0, () => RewindLauncher.InitiateRewind(rec), "rewind-not-launched");

            Assert.False(launched);
            AssertPendingWarpDroppedFor("rewind-not-launched");
        }

        [Fact]
        public void WarpToTime_RewindRefusedByAnUnfinishedJournal_DropsThePendingWarp()
        {
            InstallScenario(MergeJournal.Phases.Begin, supersedeRows: 0);
            var rec = new Recording { RecordingId = "own", VesselName = "Owner", RewindSaveFileName = "parsek_rw_own" };

            bool launched = WarpToTimeController.ArmPendingWarpAndLaunch(
                86400.0, () => RewindLauncher.InitiateRewind(rec), "rewind-not-launched");

            Assert.False(launched);
            AssertPendingWarpDroppedFor("rewind-not-launched");
        }

        // The load-failure path: no merge journal, so InitiateRewind arms the rewind and reaches
        // ExecuteRewindSaveLoad, whose save copy fails headless (no KSP install, no rewind save);
        // its catch resets the flags and InitiateRewind reports the failure.
        [Fact]
        public void WarpToTime_RewindWhoseLoadFails_ReturnsFalseAndDropsThePendingWarp()
        {
            var rec = new Recording
            {
                RecordingId = "own", VesselName = "Owner", RewindSaveFileName = "parsek_rw_missing_" + Guid.NewGuid().ToString("N"),
            };

            bool launched = WarpToTimeController.ArmPendingWarpAndLaunch(
                86400.0, () => RewindLauncher.InitiateRewind(rec), "rewind-not-launched");

            Assert.False(launched);
            Assert.False(RewindContext.IsRewinding);
            Assert.Contains(logLines, l => l.Contains("[ERROR][Rewind]") && l.Contains("Rewind failed:"));
            AssertPendingWarpDroppedFor("rewind-not-launched");
        }

        [Fact]
        public void WarpToTime_CareerStartRefused_DropsThePendingWarp()
        {
            InstallScenario(MergeJournal.Phases.Complete, supersedeRows: 1);

            bool launched = WarpToTimeController.ArmPendingWarpAndLaunch(
                86400.0, () => RewindLauncher.InitiateRewindToCareerStart("parsek_start"),
                "career-start-reset-not-launched");

            Assert.False(launched);
            AssertPendingWarpDroppedFor("career-start-reset-not-launched");
        }

        // ---------- TryDeleteFileQuietly ----------

        [Fact]
        public void QuietDelete_RemovesAnExistingFile()
        {
            string path = Path.Combine(tempDir, "copy.sfs");
            File.WriteAllText(path, "x");

            RewindLauncher.TryDeleteFileQuietly(path);

            Assert.False(File.Exists(path));
        }

        [Fact]
        public void QuietDelete_SwallowsAMissingFileAndANullPath()
        {
            Assert.Null(Record.Exception(() =>
                RewindLauncher.TryDeleteFileQuietly(Path.Combine(tempDir, "absent.sfs"))));
            Assert.Null(Record.Exception(() => RewindLauncher.TryDeleteFileQuietly(null)));
        }

        // ---------- PreProcessRewindSave edges ----------

        private string WriteSave(string content)
        {
            string path = Path.Combine(tempDir, "rw.sfs");
            File.WriteAllText(path, content);
            return path;
        }

        private static ConfigNode LoadFlightState(string path)
        {
            ConfigNode root = ConfigNode.Load(path);
            ConfigNode game = root.HasNode("GAME") ? root.GetNode("GAME") : root;
            return game.GetNode("FLIGHTSTATE");
        }

        [Fact]
        public void PreProcess_NoFlightState_WarnsAndReturnsNaN()
        {
            string sfs = WriteSave("GAME\n{\n  Mode = CAREER\n}\n");

            double own = RewindLauncher.PreProcessRewindSave(
                sfs, new RewindSpawnScope.RewindOwnerStrip(), null, 15.0);

            Assert.True(double.IsNaN(own));
            Assert.Contains(logLines, l => l.Contains("[WARN][Rewind]")
                && l.Contains("PreProcessRewindSave: no FLIGHTSTATE node in save"));
            Assert.Equal("CAREER", ConfigNode.Load(sfs).GetNode("GAME").GetValue("Mode"));
        }

        [Fact]
        public void PreProcess_UnparseableUT_WarnsAndPassesNaNToTheResolver()
        {
            string sfs = WriteSave("FLIGHTSTATE\n{\n  UT = soon\n}\n");
            double seen = 0;

            double own = RewindLauncher.PreProcessRewindSave(
                sfs, new RewindSpawnScope.RewindOwnerStrip(), ut => { seen = ut; return null; }, 15.0);

            Assert.True(double.IsNaN(own));
            Assert.True(double.IsNaN(seen));
            Assert.Equal("soon", LoadFlightState(sfs).GetValue("UT"));
            Assert.Contains(logLines, l => l.Contains("[WARN][Rewind]")
                && l.Contains("missing or invalid UT value 'soon' in FLIGHTSTATE"));
        }

        [Fact]
        public void PreProcess_MissingUT_WarnsWithNullMarker()
        {
            string sfs = WriteSave("FLIGHTSTATE\n{\n  version = 1\n}\n");

            double own = RewindLauncher.PreProcessRewindSave(
                sfs, new RewindSpawnScope.RewindOwnerStrip(), null, 15.0);

            Assert.True(double.IsNaN(own));
            Assert.Contains(logLines, l => l.Contains("missing or invalid UT value '(null)'"));
        }

        [Fact]
        public void PreProcess_UTBelowTheLead_ClampsToZeroAndReturnsTheSavesOwnUT()
        {
            string sfs = WriteSave("FLIGHTSTATE\n{\n  UT = 4.5\n}\n");
            double seen = -1;

            double own = RewindLauncher.PreProcessRewindSave(
                sfs, new RewindSpawnScope.RewindOwnerStrip(), ut => { seen = ut; return null; }, 15.0);

            Assert.Equal(4.5, own);
            Assert.Equal(0.0, seen);
            Assert.Equal("0", LoadFlightState(sfs).GetValue("UT"));
        }

        [Fact]
        public void PreProcess_WritesTheWoundBackUTRoundTrip()
        {
            string sfs = WriteSave("FLIGHTSTATE\n{\n  UT = 1000.25\n}\n");

            double own = RewindLauncher.PreProcessRewindSave(
                sfs, new RewindSpawnScope.RewindOwnerStrip(), null, 15.0);

            Assert.Equal(1000.25, own);
            Assert.Equal("985.25", LoadFlightState(sfs).GetValue("UT"));
        }

        [Fact]
        public void PreProcess_ReadsAGameWrappedFlightState()
        {
            string sfs = WriteSave("GAME\n{\n  FLIGHTSTATE\n  {\n    UT = 100\n  }\n}\n");

            double own = RewindLauncher.PreProcessRewindSave(
                sfs, new RewindSpawnScope.RewindOwnerStrip(), null, 15.0);

            Assert.Equal(100.0, own);
            Assert.Equal("85", LoadFlightState(sfs).GetValue("UT"));
        }

        private const string PidSave =
            "FLIGHTSTATE\n{\n  UT = 500\n" +
            "  VESSEL\n  {\n    name = A\n    persistentId = 7\n  }\n" +
            "  VESSEL\n  {\n    name = B\n    persistentId = +7\n  }\n" +
            "  VESSEL\n  {\n    name = C\n    persistentId = 8\n  }\n" +
            "  VESSEL\n  {\n    name = D\n    persistentId = abc\n  }\n" +
            "  VESSEL\n  {\n    name = E\n  }\n" +
            "}\n";

        [Fact]
        public void PreProcess_PidStrip_RemovesEveryIntegerSpellingOfAStrippedPid()
        {
            string sfs = WriteSave(PidSave);

            RewindLauncher.PreProcessRewindSave(sfs, new HashSet<string>(), new HashSet<uint> { 7u }, 15.0);

            var names = LoadFlightState(sfs).GetNodes("VESSEL").Select(v => v.GetValue("name")).ToList();
            Assert.Equal(new[] { "C", "D", "E" }, names);
            Assert.Contains(logLines, l => l.Contains("Stripped 2 vessel(s) from save")
                && l.Contains("2 by PID"));
        }

        [Fact]
        public void PreProcess_SurvivingPids_WhitelistOnlyTheParseablePids()
        {
            string sfs = WriteSave(PidSave);

            RewindLauncher.PreProcessRewindSave(sfs, new HashSet<string>(), new HashSet<uint> { 7u }, 15.0);

            Assert.Equal(new HashSet<uint> { 8u }, RewindContext.RewindQuicksaveVesselPids);
            Assert.Contains(logLines, l => l.Contains("Captured 1 surviving vessel PID(s) from quicksave"));
        }

        [Fact]
        public void PreProcess_EmptyPidSet_StripsNothingByPid()
        {
            string sfs = WriteSave(PidSave);

            RewindLauncher.PreProcessRewindSave(sfs, new HashSet<string>(), new HashSet<uint>(), 15.0);

            Assert.Equal(5, LoadFlightState(sfs).GetNodes("VESSEL").Length);
            Assert.Equal(new HashSet<uint> { 7u, 8u }, RewindContext.RewindQuicksaveVesselPids);
        }

        [Fact]
        public void PreProcess_NoParseableSurvivor_ClearsTheWhitelist()
        {
            RewindContext.SetQuicksaveVesselPids(new HashSet<uint> { 99u });
            string sfs = WriteSave(
                "FLIGHTSTATE\n{\n  UT = 500\n" +
                "  VESSEL\n  {\n    name = D\n    persistentId = abc\n  }\n}\n");

            RewindLauncher.PreProcessRewindSave(sfs, new HashSet<string>(), (HashSet<uint>)null, 15.0);

            Assert.Null(RewindContext.RewindQuicksaveVesselPids);
            Assert.Contains(logLines, l => l.Contains("Captured 0 surviving vessel PID(s) from quicksave"));
        }

        // ---------- Extracted units ----------

        [Theory]
        [InlineData(false, "a", false)]
        [InlineData(true, "a", true)]
        [InlineData(true, "b", false)]
        [InlineData(true, "", false)]
        [InlineData(true, null, false)]
        public void ShouldKeepSpawnState_OnlyForANamedNonEmptyId(bool haveSet, string id, bool expected)
        {
            var keep = haveSet ? new HashSet<string> { "a", "" } : null;
            Assert.Equal(expected, RewindLauncher.ShouldKeepSpawnState(keep, id));
        }

        [Fact]
        public void ClearSpawnState_ZeroesTheSpawnFieldsOnly()
        {
            var rec = MakeSpawnedRecording();

            RewindLauncher.ClearSpawnState(rec);

            Assert.False(rec.VesselSpawned);
            Assert.False(rec.VesselDestroyed);
            Assert.Equal(0, rec.SpawnAttempts);
            Assert.Equal(0, rec.SpawnDeathCount);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.Equal(0, rec.CollisionBlockCount);
            Assert.False(rec.SpawnAbandoned);
            Assert.False(rec.WalkbackExhausted);
            Assert.False(rec.DuplicateBlockerRecovered);
            Assert.False(rec.TerminalSpawnSafetyDeferred);
            // The per-playback fields belong to the caller.
            Assert.Equal(7, rec.LastAppliedResourceIndex);
            Assert.True(rec.SpawnSuppressedByRewind);
            Assert.Equal(4, rec.SceneExitSituation);
            Assert.Empty(logLines);
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData(MergeJournal.Phases.Complete, false)]
        [InlineData(MergeJournal.Phases.Begin, true)]
        [InlineData(MergeJournal.Phases.Durable2Done, true)]
        public void IsMergeJournalUnfinished_TrueUntilComplete(string phase, bool expected)
        {
            MergeJournal journal = phase == null ? null : new MergeJournal { Phase = phase };
            Assert.Equal(expected, RewindLauncher.IsMergeJournalUnfinished(journal));
        }

        [Fact]
        public void RewindSavesDir_JoinsRootSavesAndFolder_NullsReadAsEmpty()
        {
            Assert.Equal(Path.Combine("/ksp", "saves", "career"),
                RewindLauncher.BuildRewindSavesDir("/ksp", "career"));
            Assert.Equal(Path.Combine("", "saves", ""),
                RewindLauncher.BuildRewindSavesDir(null, null));
        }

        [Fact]
        public void RewindTempSavePath_AppendsTheSfsExtension()
        {
            Assert.Equal(Path.Combine("/ksp/saves/career", "parsek_rw_ab.sfs"),
                RewindLauncher.BuildRewindTempSavePath("/ksp/saves/career", "parsek_rw_ab"));
        }

        [Fact]
        public void DeleteFileIfPresent_DeletesAnExistingFile_IgnoresAMissingOne()
        {
            string path = Path.Combine(tempDir, "tmp.sfs");
            File.WriteAllText(path, "x");

            RewindLauncher.DeleteFileIfPresent(path);
            Assert.False(File.Exists(path));

            Assert.Null(Record.Exception(() => RewindLauncher.DeleteFileIfPresent(path)));
            Assert.Null(Record.Exception(() => RewindLauncher.DeleteFileIfPresent(null)));
        }

        [Fact]
        public void WindBack_SubtractsTheLeadAndLogsBothClocks()
        {
            var fs = new ConfigNode("FLIGHTSTATE");
            fs.AddValue("UT", "50");

            double adjusted = RewindLauncher.WindBackFlightStateUT(fs, 15.0, out double own);

            Assert.Equal(35.0, adjusted);
            Assert.Equal(50.0, own);
            Assert.Equal("35", fs.GetValue("UT"));
            Assert.Contains(logLines, l => l.Contains("[INFO][Rewind]") && l.Contains("UT adjusted:"));
        }

        [Fact]
        public void WindBack_ZeroLeadKeepsTheClock()
        {
            var fs = new ConfigNode("FLIGHTSTATE");
            fs.AddValue("UT", "12.5");

            double adjusted = RewindLauncher.WindBackFlightStateUT(fs, 0.0, out double own);

            Assert.Equal(12.5, adjusted);
            Assert.Equal(12.5, own);
            Assert.Equal("12.5", fs.GetValue("UT"));
        }

        [Fact]
        public void WindBack_UnparseableUT_LeavesTheNodeAndReturnsNaN()
        {
            var fs = new ConfigNode("FLIGHTSTATE");
            fs.AddValue("UT", "");

            double adjusted = RewindLauncher.WindBackFlightStateUT(fs, 15.0, out double own);

            Assert.True(double.IsNaN(adjusted));
            Assert.True(double.IsNaN(own));
            Assert.Equal("", fs.GetValue("UT"));
            Assert.Contains(logLines, l => l.Contains("[WARN][Rewind]") && l.Contains("UT value ''"));
        }

        private static ConfigNode Vessel(string name, string pid)
        {
            var v = new ConfigNode("VESSEL");
            v.AddValue("name", name);
            if (pid != null) v.AddValue("persistentId", pid);
            return v;
        }

        [Fact]
        public void Strip_RemovesByNameAndByPid_AndLogsOneSummary()
        {
            var fs = new ConfigNode("FLIGHTSTATE");
            fs.AddNode(Vessel("Rocket", "1"));
            fs.AddNode(Vessel("Probe", "7"));
            fs.AddNode(Vessel("Rocket", "2"));
            fs.AddNode(Vessel("Station", "9"));

            RewindLauncher.StripRewindSaveVessels(fs,
                RewindSpawnScope.RewindOwnerStrip.FromNames(new[] { "Rocket" }), new HashSet<uint> { 7u });

            Assert.Equal(new[] { "Station" }, fs.GetNodes("VESSEL").Select(v => v.GetValue("name")).ToArray());
            Assert.Single(logLines, l => l.Contains("Stripped 3 vessel(s) from save")
                && l.Contains("(2 by name [Rocket], 0 by owner guid, 1 by PID; kept 0 other launch(es)"));
        }

        [Fact]
        public void Strip_NullPidSet_StripsByNameOnly()
        {
            var fs = new ConfigNode("FLIGHTSTATE");
            fs.AddNode(Vessel("Rocket", "1"));
            fs.AddNode(Vessel("Probe", "7"));

            RewindLauncher.StripRewindSaveVessels(fs,
                RewindSpawnScope.RewindOwnerStrip.FromNames(new[] { "Rocket" }), null);

            Assert.Equal(new[] { "Probe" }, fs.GetNodes("VESSEL").Select(v => v.GetValue("name")).ToArray());
        }

        [Fact]
        public void ReadVesselNodeGuids_OneSlotPerNode_NullWithoutAGuid()
        {
            var guid = Guid.NewGuid();
            var withGuid = Vessel("A", "1");
            withGuid.AddValue("pid", guid.ToString());

            string[] guids = RewindLauncher.ReadVesselNodeGuids(new[] { withGuid, Vessel("B", "2") });

            Assert.Equal(2, guids.Length);
            Assert.Equal(guid.ToString("N"), guids[0]);
            Assert.Null(guids[1]);
            Assert.Empty(RewindLauncher.ReadVesselNodeGuids(new ConfigNode[0]));
        }

        [Theory]
        [InlineData("7", true)]
        [InlineData("+7", true)]
        [InlineData(" 7 ", true)]
        [InlineData("8", false)]
        [InlineData("7.0", false)]
        [InlineData("-7", false)]
        [InlineData("abc", false)]
        [InlineData(null, false)]
        public void IsVesselNodePidIn_InvariantIntegerMembership(string pid, bool expected)
        {
            Assert.Equal(expected,
                RewindLauncher.IsVesselNodePidIn(Vessel("V", pid), new HashSet<uint> { 7u }));
        }

        [Fact]
        public void CollectSurvivingVesselPids_SkipsUnparseableAndMissingPids()
        {
            var fs = new ConfigNode("FLIGHTSTATE");
            fs.AddNode(Vessel("A", "3"));
            fs.AddNode(Vessel("B", "x"));
            fs.AddNode(Vessel("C", null));
            fs.AddNode(Vessel("D", "4"));

            Assert.Equal(new HashSet<uint> { 3u, 4u }, RewindLauncher.CollectSurvivingVesselPids(fs));
            Assert.Empty(RewindLauncher.CollectSurvivingVesselPids(new ConfigNode("FLIGHTSTATE")));
        }
    }
}
