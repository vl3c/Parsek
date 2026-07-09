using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the pre-Parsek safety backup feature: the pure decision helpers in
    /// <see cref="PreParsekBackup"/>, the <see cref="FileIOUtils.CopyDirectory"/> helper it uses,
    /// and the persistence round-trip / defaults wiring for the
    /// <c>autoBackupExistingSaves</c> setting.
    /// </summary>
    [Collection("Sequential")]
    public class PreParsekBackupTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public PreParsekBackupTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekSettingsPersistence.ResetForTesting();

            tempDir = Path.Combine(
                Path.GetTempPath(), "parsek_backup_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekSettingsPersistence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }

        // ------------------------------------------------------------------ ShouldBackup

        [Theory]
        // isColdLoad, enabled, marker, footprint, isBackup, brandNew -> expected, expectedReason.
        // The reason literals are grepped verbatim by the in-game runbook in
        // docs/dev/todo-and-known-bugs.md, so they are pinned here - a rename must fail a test.
        [InlineData(true, true, false, false, false, false, true, "eligible")]
        [InlineData(false, true, false, false, false, false, false, "not-cold-load")]
        [InlineData(true, false, false, false, false, false, false, "disabled")]
        [InlineData(true, true, true, false, false, false, false, "marker-present")]
        [InlineData(true, true, false, true, false, false, false, "already-parsek-footprint")]
        [InlineData(true, true, false, false, true, false, false, "is-backup-folder")]
        [InlineData(true, true, false, false, false, true, false, "brand-new-empty")]
        public void ShouldBackup_TruthTable(
            bool cold, bool enabled, bool marker, bool footprint, bool isBackup, bool brandNew,
            bool expected, string expectedReason)
        {
            bool actual = PreParsekBackup.ShouldBackup(
                cold, enabled, marker, footprint, isBackup, brandNew, out string reason);
            Assert.Equal(expected, actual);
            Assert.Equal(expectedReason, reason);
        }

        [Fact]
        public void ShouldBackup_FootprintBeatsBrandNew_SoParsekTouchedSaveNeverCaptured()
        {
            // Both footprint and brandNew true: footprint must win (skip reason=already-parsek-footprint),
            // never capturing a Parsek-touched file as "pristine".
            PreParsekBackup.ShouldBackup(true, true, false, true, false, true, out string reason);
            Assert.Equal("already-parsek-footprint", reason);
        }

        // ------------------------------------------------------------------ SanitizeSaveName

        [Theory]
        [InlineData("MyCareer", "MyCareer")]
        [InlineData("orbital supply route", "orbital supply route")]
        [InlineData("a:b*c?", "a_b_c_")]
        [InlineData("", "save")]
        [InlineData(null, "save")]
        [InlineData("   ", "save")]
        [InlineData(" MyCareer ", "MyCareer")]
        public void SanitizeSaveName_ReplacesInvalidChars(string input, string expected)
        {
            Assert.Equal(expected, PreParsekBackup.SanitizeSaveName(input));
        }

        // ------------------------------------------------------------------ BuildBackupFolderName

        [Fact]
        public void BuildBackupFolderName_FormatsLocalTimestamp()
        {
            var dt = new DateTime(2026, 7, 9, 21, 30, 0);
            string name = PreParsekBackup.BuildBackupFolderName("MyCareer", dt, _ => false);
            Assert.Equal("MyCareer (pre-Parsek 2026-07-09_2130)", name);
        }

        [Fact]
        public void BuildBackupFolderName_AppendsSuffixOnCollision()
        {
            var dt = new DateTime(2026, 7, 9, 21, 30, 0);
            var taken = new HashSet<string>
            {
                "S (pre-Parsek 2026-07-09_2130)",
                "S (pre-Parsek 2026-07-09_2130)_2",
            };
            string name = PreParsekBackup.BuildBackupFolderName("S", dt, taken.Contains);
            Assert.Equal("S (pre-Parsek 2026-07-09_2130)_3", name);
        }

        [Fact]
        public void BuildBackupFolderName_SanitizesBaseName()
        {
            var dt = new DateTime(2026, 1, 2, 3, 4, 0);
            string name = PreParsekBackup.BuildBackupFolderName("a:b", dt, _ => false);
            Assert.StartsWith("a_b (pre-Parsek", name);
        }

        // ------------------------------------------------------------------ IsBrandNewEmptySave

        [Fact]
        public void IsBrandNewEmptySave_TrueForParsedEmptySave()
        {
            var s = new CareerSaveSnapshot { Parsed = true };
            Assert.True(PreParsekBackup.IsBrandNewEmptySave(s));
        }

        [Fact]
        public void IsBrandNewEmptySave_FalseWhenAnyActivityPresent()
        {
            var withVessel = new CareerSaveSnapshot { Parsed = true };
            withVessel.Vessels.Add(default);
            Assert.False(PreParsekBackup.IsBrandNewEmptySave(withVessel));

            var withScience = new CareerSaveSnapshot { Parsed = true };
            withScience.SubjectScience["x"] = 1.0;
            Assert.False(PreParsekBackup.IsBrandNewEmptySave(withScience));

            var withMilestone = new CareerSaveSnapshot { Parsed = true };
            withMilestone.CompletedMilestoneIds.Add("firstLaunch");
            Assert.False(PreParsekBackup.IsBrandNewEmptySave(withMilestone));

            var withContract = new CareerSaveSnapshot { Parsed = true };
            withContract.ActiveContractGuids.Add("guid");
            Assert.False(PreParsekBackup.IsBrandNewEmptySave(withContract));
        }

        [Fact]
        public void IsBrandNewEmptySave_FailOpen_FalseWhenUnparsed()
        {
            // Unparseable save -> not "brand new" -> back up (fail-open).
            Assert.False(PreParsekBackup.IsBrandNewEmptySave(new CareerSaveSnapshot { Parsed = false }));
            Assert.False(PreParsekBackup.IsBrandNewEmptySave(null));
        }

        // ------------------------------------------------------------------ HasParsekGameplayFootprint

        [Fact]
        public void HasParsekGameplayFootprint_TrueWhenParsekSubdirExists()
        {
            Assert.True(PreParsekBackup.HasParsekGameplayFootprint(null, parsekSubdirExists: true));
        }

        [Fact]
        public void HasParsekGameplayFootprint_FalseForEmptyInjectedNode()
        {
            // The empty SCENARIO{name=ParsekScenario} KSP injects (name+scene only) is NOT a footprint.
            ConfigNode root = BuildPersistent(addParsekScenario: true, populated: false);
            Assert.False(PreParsekBackup.HasParsekGameplayFootprint(root, parsekSubdirExists: false));
        }

        [Fact]
        public void HasParsekGameplayFootprint_TrueForPopulatedNode()
        {
            ConfigNode root = BuildPersistent(addParsekScenario: true, populated: true);
            Assert.True(PreParsekBackup.HasParsekGameplayFootprint(root, parsekSubdirExists: false));
        }

        [Fact]
        public void HasParsekGameplayFootprint_FalseWhenNoParsekNode()
        {
            ConfigNode root = BuildPersistent(addParsekScenario: false, populated: false);
            Assert.False(PreParsekBackup.HasParsekGameplayFootprint(root, parsekSubdirExists: false));
        }

        [Fact]
        public void HasParsekGameplayFootprint_TrueForValueOnlyNode()
        {
            // A ParsekScenario node carrying a scalar value beyond name+scene but no child nodes
            // must count as a footprint (the values.Count > 2 branch, distinct from the child-node
            // branch the "populated" case exercises).
            var root = new ConfigNode();
            ConfigNode game = root.AddNode("GAME");
            ConfigNode scn = game.AddNode("SCENARIO");
            scn.AddValue("name", "ParsekScenario");
            scn.AddValue("scene", "7, 5, 6, 8");
            scn.AddValue("someState", "1");
            Assert.True(PreParsekBackup.HasParsekGameplayFootprint(root, parsekSubdirExists: false));
        }

        [Fact]
        public void HasParsekGameplayFootprint_FalseForNullRootWithoutSubdir()
        {
            Assert.False(PreParsekBackup.HasParsekGameplayFootprint(null, parsekSubdirExists: false));
        }

        private static ConfigNode BuildPersistent(bool addParsekScenario, bool populated)
        {
            var root = new ConfigNode();
            ConfigNode game = root.AddNode("GAME");
            // A stock scenario that must always be ignored.
            ConfigNode funding = game.AddNode("SCENARIO");
            funding.AddValue("name", "Funding");
            funding.AddValue("scene", "7, 8, 5");
            if (addParsekScenario)
            {
                ConfigNode parsek = game.AddNode("SCENARIO");
                parsek.AddValue("name", "ParsekScenario");
                parsek.AddValue("scene", "7, 5, 6, 8");
                if (populated)
                    parsek.AddNode("RECORDING_TREE");
            }
            return root;
        }

        // ------------------------------------------------------------------ IsParsekBackupFolder

        [Fact]
        public void IsParsekBackupFolder_TrueOnNameFragment()
        {
            Assert.True(PreParsekBackup.IsParsekBackupFolder(null, "MyCareer (pre-Parsek 2026-07-09_2130)"));
        }

        [Fact]
        public void IsParsekBackupFolder_TrueWhenSentinelPresent()
        {
            File.WriteAllText(Path.Combine(tempDir, PreParsekBackup.SentinelName), "x");
            Assert.True(PreParsekBackup.IsParsekBackupFolder(tempDir, "some plain name"));
        }

        [Fact]
        public void IsParsekBackupFolder_FalseForOrdinarySave()
        {
            Assert.False(PreParsekBackup.IsParsekBackupFolder(tempDir, "MyCareer"));
        }

        // ------------------------------------------------------------------ CopyDirectory

        [Fact]
        public void CopyDirectory_CopiesTreeAndCounts()
        {
            string src = Path.Combine(tempDir, "src");
            string sub = Path.Combine(src, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(src, "a.txt"), "aaaa");   // 4 bytes
            File.WriteAllText(Path.Combine(sub, "b.txt"), "bb");     // 2 bytes

            string dst = Path.Combine(tempDir, "dst");
            bool ok = FileIOUtils.CopyDirectory(src, dst, null, "Test", out int files, out long bytes);

            Assert.True(ok);
            Assert.Equal(2, files);
            Assert.Equal(6, bytes);
            Assert.True(File.Exists(Path.Combine(dst, "a.txt")));
            Assert.True(File.Exists(Path.Combine(dst, "sub", "b.txt")));
        }

        [Fact]
        public void CopyDirectory_HonorsTopLevelExclude_CaseInsensitive()
        {
            string src = Path.Combine(tempDir, "src2");
            Directory.CreateDirectory(Path.Combine(src, "Parsek"));
            Directory.CreateDirectory(Path.Combine(src, "Ships"));
            File.WriteAllText(Path.Combine(src, "Parsek", "skip.txt"), "x");
            File.WriteAllText(Path.Combine(src, "Ships", "keep.txt"), "y");

            var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "parsek" };
            string dst = Path.Combine(tempDir, "dst2");
            bool ok = FileIOUtils.CopyDirectory(src, dst, exclude, "Test", out int files, out _);

            Assert.True(ok);
            Assert.Equal(1, files);
            Assert.False(Directory.Exists(Path.Combine(dst, "Parsek")));
            Assert.True(File.Exists(Path.Combine(dst, "Ships", "keep.txt")));
        }

        [Fact]
        public void CopyDirectory_NonExistentSource_IsNoOpSuccess()
        {
            string dst = Path.Combine(tempDir, "dst3");
            bool ok = FileIOUtils.CopyDirectory(
                Path.Combine(tempDir, "nope"), dst, null, "Test", out int files, out long bytes);
            Assert.True(ok);
            Assert.Equal(0, files);
            Assert.Equal(0, bytes);
        }

        [Fact]
        public void CopyDirectory_ReturnsFalseAndWarns_OnCopyFailure()
        {
            string src = Path.Combine(tempDir, "src4");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "a.txt"), "x");

            string dst = Path.Combine(tempDir, "dst4");
            // Pre-create the destination file so the non-overwriting File.CopyTo throws.
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(dst, "a.txt"), "existing");
            logLines.Clear();

            bool ok = FileIOUtils.CopyDirectory(src, dst, null, "Test", out _, out _);

            Assert.False(ok);
            Assert.Contains(logLines, l => l.Contains("[Test]") && l.Contains("CopyDirectory") && l.Contains("failed"));
        }

        // ------------------------------------------------------------------ Settings round-trip / defaults

        [Fact]
        public void AutoBackupExistingSaves_RoundTripsThroughPersistentStore()
        {
            var settings = new ParsekSettings { autoBackupExistingSaves = true };
            ParsekSettingsPersistence.SetStoredAutoBackupExistingSavesForTesting(false);

            ParsekSettingsPersistence.ApplyTo(settings);

            Assert.False(settings.autoBackupExistingSaves);
            Assert.Equal(false, ParsekSettingsPersistence.GetStoredAutoBackupExistingSaves());
            Assert.Contains(logLines, line =>
                line.Contains("[SettingsStore]") &&
                line.Contains("autoBackupExistingSaves") &&
                line.Contains("True -> False"));
        }

        [Fact]
        public void Defaults_EnableAutoBackup()
        {
            Assert.True(SettingsWindowPresentation.BuildDefaults().AutoBackupExistingSaves);
            Assert.True(new ParsekSettings().autoBackupExistingSaves);
        }
    }
}
