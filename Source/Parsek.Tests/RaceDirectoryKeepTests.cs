using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Parsek/Race/ survives the in-game test batch snapshot / restore and the test-batch crash
    /// reconcile (design-competitive-multiplayer.md addition A7, plan R1-07), and a save with no
    /// race folder goes through both exactly as before: no race folder appears, no race log line.
    /// </summary>
    [Collection("Sequential")]
    public class RaceDirectoryKeepTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string root;
        private readonly string saveDir;
        private readonly string parsekDir;

        public RaceDirectoryKeepTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            root = Path.Combine(Path.GetTempPath(), "parsek-race-keep-" + Guid.NewGuid().ToString("N"));
            saveDir = Path.Combine(root, "save");
            parsekDir = Path.Combine(saveDir, "Parsek");
            Directory.CreateDirectory(saveDir);
            RecordingPaths.SaveRootOverrideForTesting = saveDir;
        }

        public void Dispose()
        {
            RecordingPaths.SaveRootOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }

        private static void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        private string[] SaveDirChildren()
        {
            return Directory.GetDirectories(saveDir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        }

        private bool AnyRaceLogLine()
        {
            return logLines.Any(l => l.Contains("Parsek/Race/"));
        }

        [Theory]
        [InlineData("Race", true)]
        [InlineData("Race/own/player.cfg", true)]
        [InlineData("Race\\local.cfg", true)]
        [InlineData("\\Race\\local.cfg", true)]
        [InlineData("race/local.cfg", true)]
        [InlineData("Recordings", false)]
        [InlineData("Recordings/Race/x.prec", false)]
        [InlineData("Racecar/x", false)]
        [InlineData("GameState/events.pgse", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsInRaceDirectory_MatchesOnlyTheTopLevelRaceFolder(string relative, bool expected)
        {
            Assert.Equal(expected, RaceDirectoryKeep.IsInRaceDirectory(relative));
        }

        [Fact]
        public void MoveAside_NoRaceFolder_ReturnsNullAndLogsNothing()
        {
            Write(Path.Combine(parsekDir, "Recordings", "a.prec"), "rec");

            Assert.Null(RaceDirectoryKeep.MoveAside(parsekDir));
            Assert.True(RaceDirectoryKeep.MoveBack(parsekDir, null));

            Assert.Equal(new[] { "Parsek" }, SaveDirChildren());
            Assert.False(AnyRaceLogLine());
        }

        [Fact]
        public void MoveBack_RaceFolderAlreadyThere_LeavesTheKeptFilesAsideAndWarns()
        {
            Write(Path.Combine(parsekDir, "Race", "local.cfg"), "live");
            string aside = RaceDirectoryKeep.MoveAside(parsekDir);
            Write(Path.Combine(parsekDir, "Race", "local.cfg"), "other");

            Assert.False(RaceDirectoryKeep.MoveBack(parsekDir, aside));

            Assert.Equal("live", File.ReadAllText(Path.Combine(aside, "local.cfg")));
            Assert.Equal("other", File.ReadAllText(Path.Combine(parsekDir, "Race", "local.cfg")));
            Assert.Contains(logLines, l => l.Contains("[TestBatch]") && l.Contains("already exists"));
        }

        [Fact]
        public void MoveBack_ParsekFolderGone_RecreatesItAndKeepsTheRaceFolder()
        {
            Write(Path.Combine(parsekDir, "Race", "own", "player.cfg"), "me");
            string aside = RaceDirectoryKeep.MoveAside(parsekDir);
            Directory.Delete(parsekDir, recursive: true);

            Assert.True(RaceDirectoryKeep.MoveBack(parsekDir, aside));

            Assert.Equal("me", File.ReadAllText(Path.Combine(parsekDir, "Race", "own", "player.cfg")));
            Assert.False(Directory.Exists(aside));
        }

        [Fact]
        public void RunnerSnapshot_LeavesTheRaceFolderOut()
        {
            Write(Path.Combine(parsekDir, "Recordings", "a.prec"), "rec");
            Write(Path.Combine(parsekDir, "Race", "own", "player.cfg"), "me");

            string snapshot = InGameTestRunner.CaptureBatchFlightParsekSaveSnapshot("pt-slot");

            Assert.Equal("rec", File.ReadAllText(Path.Combine(snapshot, "Recordings", "a.prec")));
            Assert.False(Directory.Exists(Path.Combine(snapshot, "Race")));
            Assert.Equal("me", File.ReadAllText(Path.Combine(parsekDir, "Race", "own", "player.cfg")));
        }

        [Fact]
        public void RunnerRestore_KeepsTheLiveRaceFolder()
        {
            Write(Path.Combine(parsekDir, "state.txt"), "baseline");
            Write(Path.Combine(parsekDir, "Race", "local.cfg"), "race-at-start");
            string snapshot = InGameTestRunner.CaptureBatchFlightParsekSaveSnapshot("pt-slot");
            // The batch changes Parsek state and the race folder moves on meanwhile.
            Write(Path.Combine(parsekDir, "state.txt"), "mutated");
            Write(Path.Combine(parsekDir, "Race", "local.cfg"), "race-now");
            Write(Path.Combine(parsekDir, "Race", "cache", "rival.cfg"), "rival");
            // A snapshot from an older build may carry a race folder; it is never restored.
            Write(Path.Combine(snapshot, "Race", "stale.cfg"), "stale");

            using (var staged = InGameTestRunner.CreateBatchFlightParsekSaveSnapshotStaging(parsekDir, snapshot))
            {
                staged.Activate();
            }

            Assert.Equal("baseline", File.ReadAllText(Path.Combine(parsekDir, "state.txt")));
            Assert.Equal("race-now", File.ReadAllText(Path.Combine(parsekDir, "Race", "local.cfg")));
            Assert.Equal("rival", File.ReadAllText(Path.Combine(parsekDir, "Race", "cache", "rival.cfg")));
            Assert.False(File.Exists(Path.Combine(parsekDir, "Race", "stale.cfg")));
            Assert.Equal(new[] { "Parsek", "pt-slot-parsek" }, SaveDirChildren());
            Assert.Contains(logLines, l => l.Contains("[TestBatch]") && l.Contains("kept Parsek/Race/ (2 files)"));
        }

        [Fact]
        public void RunnerRestore_SinglePlayerSave_IsUnchanged()
        {
            Write(Path.Combine(parsekDir, "state.txt"), "baseline");
            string snapshot = InGameTestRunner.CaptureBatchFlightParsekSaveSnapshot("pt-slot");
            Write(Path.Combine(parsekDir, "state.txt"), "mutated");

            using (var staged = InGameTestRunner.CreateBatchFlightParsekSaveSnapshotStaging(parsekDir, snapshot))
            {
                staged.Activate();
            }

            Assert.Equal("baseline", File.ReadAllText(Path.Combine(parsekDir, "state.txt")));
            Assert.False(Directory.Exists(Path.Combine(parsekDir, "Race")));
            Assert.Equal(new[] { "Parsek", "pt-slot-parsek" }, SaveDirChildren());
            Assert.False(AnyRaceLogLine());
        }

        [Fact]
        public void CrashReconcile_KeepsTheLiveRaceFolder()
        {
            string snapshot = Path.Combine(saveDir, "pt-slot-parsek");
            Write(Path.Combine(snapshot, "GameState", "events.pgse"), "clean");
            Write(Path.Combine(snapshot, "Race", "stale.cfg"), "stale");
            Write(Path.Combine(parsekDir, "GameState", "events.pgse"), "mutated");
            Write(Path.Combine(parsekDir, "Recordings", "batch.prec"), "batch-made");
            Write(Path.Combine(parsekDir, "Race", "local.cfg"), "race-now");

            Assert.True(ParsekScenario.RestoreParsekSidecarDirOnReconcile(snapshot));

            Assert.Equal("clean", File.ReadAllText(Path.Combine(parsekDir, "GameState", "events.pgse")));
            Assert.False(Directory.Exists(Path.Combine(parsekDir, "Recordings")));
            Assert.Equal("race-now", File.ReadAllText(Path.Combine(parsekDir, "Race", "local.cfg")));
            Assert.False(File.Exists(Path.Combine(parsekDir, "Race", "stale.cfg")));
            Assert.Equal(new[] { "Parsek", "pt-slot-parsek" }, SaveDirChildren());
            Assert.Contains(logLines, l => l.Contains("[TestBatch]") && l.Contains("kept Parsek/Race/ (1 files)"));
        }

        [Fact]
        public void CrashReconcile_SinglePlayerSave_IsUnchanged()
        {
            string snapshot = Path.Combine(saveDir, "pt-slot-parsek");
            Write(Path.Combine(snapshot, "GameState", "events.pgse"), "clean");
            Write(Path.Combine(parsekDir, "GameState", "events.pgse"), "mutated");

            Assert.True(ParsekScenario.RestoreParsekSidecarDirOnReconcile(snapshot));

            Assert.Equal("clean", File.ReadAllText(Path.Combine(parsekDir, "GameState", "events.pgse")));
            Assert.False(Directory.Exists(Path.Combine(parsekDir, "Race")));
            Assert.Equal(new[] { "Parsek", "pt-slot-parsek" }, SaveDirChildren());
            Assert.False(AnyRaceLogLine());
        }
    }
}
