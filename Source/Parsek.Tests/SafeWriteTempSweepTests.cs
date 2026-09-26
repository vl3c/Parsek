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
                && l.Contains("7 bytes") && l.Contains("destExists=True"));
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
        public void Sweep_TmpWithoutRealFile_DeletesTmpOnly()
        {
            // First-ever save interrupted: nothing to preserve, residue still goes.
            string path = Path.Combine(root, "x.cfg");
            WriteText(path + ".tmp", "partial");

            Assert.True(FileIOUtils.SweepStaleSafeWriteTemp(path, "SweepTest"));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.False(File.Exists(path));
            Assert.Contains(logLines, l => l.Contains("[SweepTest]") && l.Contains("destExists=False"));
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
            WriteText(Path.Combine(dir, "baseline_250.5.pgsb.tmp"), "b");
            WriteText(Path.Combine(dir, "baseline_300.pgsb.tmpx"), "not ours");
            WriteText(Path.Combine(dir, "events.pgse.tmp"), "other store");

            int deleted = FileIOUtils.SweepStaleSafeWriteTemps(dir, "baseline_*.pgsb", "SweepTest");

            Assert.Equal(2, deleted);
            Assert.False(File.Exists(Path.Combine(dir, "baseline_100.pgsb.tmp")));
            Assert.False(File.Exists(Path.Combine(dir, "baseline_250.5.pgsb.tmp")));
            Assert.True(File.Exists(Path.Combine(dir, "baseline_100.pgsb")));
            Assert.True(File.Exists(Path.Combine(dir, "baseline_300.pgsb.tmpx")));
            Assert.True(File.Exists(Path.Combine(dir, "events.pgse.tmp")));
            Assert.Contains(logLines, l => l.Contains("[SweepTest]")
                && l.Contains("swept stale temp files") && l.Contains("deleted=2 failed=0"));
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
        public void LedgerLoad_NoRealFile_SweepsTmp()
        {
            string path = Path.Combine(root, "GameState", "ledger.pgld");
            WriteText(path + ".tmp", "partial");

            Assert.True(Ledger.LoadFromFile(path));

            Assert.False(File.Exists(path + ".tmp"));
            Assert.Empty(Ledger.Actions);
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
            WriteText(Path.Combine(dir, "baseline_100.pgsb.tmp"), "partial");
            WriteText(Path.Combine(dir, "baseline_200.pgsb.tmp"), "partial");

            GameStateStore.LoadBaselines();

            Assert.False(File.Exists(Path.Combine(dir, "baseline_100.pgsb.tmp")));
            Assert.False(File.Exists(Path.Combine(dir, "baseline_200.pgsb.tmp")));
            Assert.Empty(GameStateStore.Baselines);
            Assert.Contains(logLines, l => l.Contains("[GameStateStore]") && l.Contains("deleted=2 failed=0"));
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
            ParsekSettingsPersistence.FilePathOverrideForTesting = path;

            ParsekSettingsPersistence.LoadIfNeeded();

            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.False(ParsekSettingsPersistence.GetStoredShowRouteLines().Value);
            Assert.Contains(logLines, l => l.Contains("settings.cfg.tmp") && l.Contains("deleted stale temp file"));
        }
    }
}
