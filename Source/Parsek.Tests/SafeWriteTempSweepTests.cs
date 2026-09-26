using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// SAFE-WRITE-CONFIGNODE-TMP-NOT-SWEPT: a crash between a safe-write's temp write and its
    /// swap leaves <c>&lt;file&gt;.tmp</c> beside the previous file. The recordings sweep only
    /// covers <c>Parsek/Recordings/</c>, so the fixed-path stores (ledger, game-state events,
    /// baselines, milestones, settings) delete their own residue on load. These cells drive
    /// the helpers and each store's real load path against a temp directory.
    /// </summary>
    [Collection("Sequential")]
    public class SafeWriteTempSweepTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string root;
        private readonly string originalSaveFolder;

        public SafeWriteTempSweepTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            root = Path.Combine(Path.GetTempPath(), "parsek-safewrite-sweep-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            originalSaveFolder = HighLogic.SaveFolder;
            Ledger.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekSettingsPersistence.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingPaths.SaveRootOverrideForTesting = null;
            HighLogic.SaveFolder = originalSaveFolder;
            Ledger.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekSettingsPersistence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            try { Directory.Delete(root, true); } catch { }
        }

        private static void WriteText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        private string ArmSaveRoot()
        {
            // A unique save folder resets the events / milestone stores' once-per-save gate.
            HighLogic.SaveFolder = "parsek-safewrite-sweep-" + Guid.NewGuid().ToString("N");
            RecordingPaths.SaveRootOverrideForTesting = root;
            string dir = Path.Combine(root, "Parsek", "GameState");
            Directory.CreateDirectory(dir);
            return dir;
        }

        // ----- Helper -----

        [Fact]
        public void Sweep_DeletesTmp_LeavesRealFileByteIdentical()
        {
            string path = Path.Combine(root, "x.cfg");
            WriteText(path, "real = 1\n");
            WriteText(path + ".tmp", "partial");
            byte[] before = File.ReadAllBytes(path);

            bool deleted = FileIOUtils.SweepStaleSafeWriteTemp(path, "SweepTest");

            Assert.True(deleted);
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Contains(logLines, l => l.Contains("[SweepTest]")
                && l.Contains("deleted stale temp file") && l.Contains("x.cfg.tmp")
                && l.Contains("7 bytes"));
        }

        [Fact]
        public void Sweep_AbsentTmp_IsNoOp()
        {
            string path = Path.Combine(root, "x.cfg");
            WriteText(path, "real = 1\n");
            byte[] before = File.ReadAllBytes(path);

            Assert.False(FileIOUtils.SweepStaleSafeWriteTemp(path, "SweepTest"));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.DoesNotContain(logLines, l => l.Contains("[SweepTest]"));
        }

        [Fact]
        public void Sweep_TmpWithoutRealFile_KeepsTmp_AndWarns()
        {
            // Real file missing: either the move-aside swap crashed between its two renames
            // (the .tmp holds the newest complete bytes) or a first-ever save died mid-write
            // (a partial .tmp). Indistinguishable, so the .tmp is kept, not deleted or promoted.
            string path = Path.Combine(root, "x.cfg");
            WriteText(path + ".tmp", "partial");

            Assert.False(FileIOUtils.SweepStaleSafeWriteTemp(path, "SweepTest"));
            Assert.True(File.Exists(path + ".tmp"));
            Assert.Equal("partial", File.ReadAllText(path + ".tmp"));
            Assert.False(File.Exists(path));
            Assert.Contains(logLines, l => l.Contains("[SweepTest]") && l.Contains("[WARN]")
                && l.Contains("kept stale temp file") && l.Contains("x.cfg.tmp")
                && l.Contains("7 bytes") && l.Contains("real file") && l.Contains("is missing")
                && l.Contains("bakSiblings=none"));
        }

        [Fact]
        public void Sweep_InterruptedMoveAsideSwap_KeepsTmp_AndNamesTheBak()
        {
            // The ReplaceDestination fallback moved the real file to .bak.<guid> and died
            // before moving the .tmp into place: the .tmp is the newest complete copy.
            string path = Path.Combine(root, "x.cfg");
            string bakName = "x.cfg.bak." + Guid.NewGuid().ToString("N");
            WriteText(Path.Combine(root, bakName), "previous");
            WriteText(path + ".tmp", "newest");

            Assert.False(FileIOUtils.SweepStaleSafeWriteTemp(path, "SweepTest"));
            Assert.Equal("newest", File.ReadAllText(path + ".tmp"));
            Assert.Equal("previous", File.ReadAllText(Path.Combine(root, bakName)));
            Assert.False(File.Exists(path));
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("kept stale temp file")
                && l.Contains("bakSiblings=" + bakName));
        }

        [Fact]
        public void Sweep_NullOrEmptyPath_ReturnsFalse()
        {
            Assert.False(FileIOUtils.SweepStaleSafeWriteTemp(null, "SweepTest"));
            Assert.False(FileIOUtils.SweepStaleSafeWriteTemp("", "SweepTest"));
        }

        [Fact]
        public void Sweep_NeverTouchesSwapFallbackBackup()
        {
            // The swap fallback's .bak.<guid> can hold the only copy of the previous bytes.
            string path = Path.Combine(root, "x.cfg");
            string bak = path + ".bak." + Guid.NewGuid().ToString("N");
            WriteText(bak, "previous");
            WriteText(path + ".tmp", "partial");

            FileIOUtils.SweepStaleSafeWriteTemp(path, "SweepTest");

            Assert.True(File.Exists(bak));
        }

        [Fact]
        public void SweepDirectory_DeletesOnlyMatchingTmps()
        {
            string dir = Path.Combine(root, "GameState");
            WriteText(Path.Combine(dir, "baseline_100.pgsb"), "real");
            WriteText(Path.Combine(dir, "baseline_100.pgsb.tmp"), "a");
            WriteText(Path.Combine(dir, "baseline_250.5.pgsb"), "real");
            WriteText(Path.Combine(dir, "baseline_250.5.pgsb.tmp"), "b");
            WriteText(Path.Combine(dir, "baseline_400.pgsb.tmp"), "no real file");
            WriteText(Path.Combine(dir, "baseline_300.pgsb.tmpx"), "not ours");
            WriteText(Path.Combine(dir, "events.pgse.tmp"), "other store");

            int deleted = FileIOUtils.SweepStaleSafeWriteTemps(dir, "baseline_*.pgsb", "SweepTest");

            Assert.Equal(2, deleted);
            Assert.False(File.Exists(Path.Combine(dir, "baseline_100.pgsb.tmp")));
            Assert.False(File.Exists(Path.Combine(dir, "baseline_250.5.pgsb.tmp")));
            Assert.True(File.Exists(Path.Combine(dir, "baseline_400.pgsb.tmp")));
            Assert.True(File.Exists(Path.Combine(dir, "baseline_100.pgsb")));
            Assert.True(File.Exists(Path.Combine(dir, "baseline_250.5.pgsb")));
            Assert.True(File.Exists(Path.Combine(dir, "baseline_300.pgsb.tmpx")));
            Assert.True(File.Exists(Path.Combine(dir, "events.pgse.tmp")));
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("baseline_400.pgsb.tmp")
                && l.Contains("kept stale temp file"));
            Assert.Contains(logLines, l => l.Contains("[SweepTest]")
                && l.Contains("swept stale temp files")
                && l.Contains("deleted=2 keptRealMissing=1 failed=0"));
        }

        [Fact]
        public void SweepDirectory_MissingDirOrNoMatches_ReturnsZeroSilently()
        {
            Assert.Equal(0, FileIOUtils.SweepStaleSafeWriteTemps(
                Path.Combine(root, "nope"), "baseline_*.pgsb", "SweepTest"));
            Assert.Equal(0, FileIOUtils.SweepStaleSafeWriteTemps(root, "baseline_*.pgsb", "SweepTest"));
            Assert.DoesNotContain(logLines, l => l.Contains("[SweepTest]"));
        }

        [Fact]
        public void SafeWrite_UsesTheSweptSuffix()
        {
            // The producer and the sweeper must agree on the temp name; a crash hook would
            // leave exactly the file the sweep deletes.
            Assert.Equal(".tmp", FileIOUtils.SafeWriteTempSuffix);
            Assert.True(RecordingStore.IsTransientSidecarArtifactFile("abc.prec" + FileIOUtils.SafeWriteTempSuffix));
        }

        // ----- Store load paths -----

        [Fact]
        public void LedgerLoad_SweepsTmp_AndLoadsPreviousFile()
        {
            string path = Path.Combine(root, "GameState", "ledger.pgld");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            Assert.True(Ledger.SaveToFile(path));
            byte[] before = File.ReadAllBytes(path);
            WriteText(path + ".tmp", "PARSEK_LEDGER { partial");

            Assert.True(Ledger.LoadFromFile(path));

            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Contains(logLines, l => l.Contains("[Ledger]") && l.Contains("deleted stale temp file"));
        }

        [Fact]
        public void LedgerLoad_NoRealFile_KeepsTmp()
        {
            string path = Path.Combine(root, "GameState", "ledger.pgld");
            WriteText(path + ".tmp", "partial");

            Assert.True(Ledger.LoadFromFile(path));

            Assert.True(File.Exists(path + ".tmp"));
            Assert.Empty(Ledger.Actions);
            Assert.Contains(logLines, l => l.Contains("[Ledger]") && l.Contains("[WARN]")
                && l.Contains("kept stale temp file"));
        }

        [Fact]
        public void GameStateEventsLoad_SweepsTmp_AndLeavesRealFile()
        {
            string dir = ArmSaveRoot();
            string path = Path.Combine(dir, "events.pgse");
            WriteText(path, "version = 1\n");
            byte[] before = File.ReadAllBytes(path);
            WriteText(path + ".tmp", "partial");

            Assert.True(GameStateStore.LoadEventFile());

            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Contains(logLines, l => l.Contains("[GameStateStore]") && l.Contains("events.pgse.tmp")
                && l.Contains("deleted stale temp file"));
        }

        [Fact]
        public void GameStateBaselinesLoad_SweepsPerUtTmps()
        {
            string dir = ArmSaveRoot();
            string real100 = Path.Combine(dir, "baseline_100.pgsb");
            string real200 = Path.Combine(dir, "baseline_200.pgsb");
            WriteText(real100, "ut = 100\n");
            WriteText(real200, "ut = 200\n");
            byte[] before100 = File.ReadAllBytes(real100);
            WriteText(real100 + ".tmp", "partial");
            WriteText(real200 + ".tmp", "partial");
            WriteText(Path.Combine(dir, "baseline_300.pgsb.tmp"), "no real file");

            GameStateStore.LoadBaselines();

            Assert.False(File.Exists(real100 + ".tmp"));
            Assert.False(File.Exists(real200 + ".tmp"));
            Assert.True(File.Exists(Path.Combine(dir, "baseline_300.pgsb.tmp")));
            Assert.Equal(before100, File.ReadAllBytes(real100));
            Assert.Contains(logLines, l => l.Contains("[GameStateStore]")
                && l.Contains("deleted=2 keptRealMissing=1 failed=0"));
        }

        [Fact]
        public void MilestonesLoad_SweepsTmp_AndLeavesRealFile()
        {
            string dir = ArmSaveRoot();
            string path = Path.Combine(dir, "milestones.pgsm");
            WriteText(path, "version = 1\n");
            byte[] before = File.ReadAllBytes(path);
            WriteText(path + ".tmp", "partial");

            Assert.True(MilestoneStore.LoadMilestoneFile());

            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Contains(logLines, l => l.Contains("[MilestoneStore]") && l.Contains("milestones.pgsm.tmp")
                && l.Contains("deleted stale temp file"));
        }

        [Fact]
        public void SettingsLoad_SweepsTmp_AndReadsRealFile()
        {
            string path = Path.Combine(root, "PluginData", "settings.cfg");
            WriteText(path, "showRouteLines = False\n");
            byte[] before = File.ReadAllBytes(path);
            WriteText(path + ".tmp", "showRouteLines = True");
            ParsekSettingsPersistence.SetFilePathOverrideForTesting(path);

            ParsekSettingsPersistence.LoadIfNeeded();

            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.False(ParsekSettingsPersistence.GetStoredShowRouteLines().Value);
            Assert.Contains(logLines, l => l.Contains("settings.cfg.tmp") && l.Contains("deleted stale temp file"));
        }
    }
}
