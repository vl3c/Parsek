using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The 1.0 save-format rule "never destroy data this build does not understand" for the
    /// ledger: a LEDGER node of a newer version (or an Upgradable one with no upgrade path) is
    /// held verbatim, written back unchanged to the save node and ledger.pgld, and blocks every
    /// KSP state patch while held; a pre-1.0 ledger still starts empty, at Info.
    /// </summary>
    [Collection("Sequential")]
    public class SaveFormatLedgerHoldTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public SaveFormatLedgerHoldTests()
        {
            SaveFormat.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            MilestoneStore.ResetForTesting();
            MissionStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            Ledger.ResetForTesting();
            tempDir = Path.Combine(Path.GetTempPath(), "parsek-saveformat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            SaveFormat.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            Ledger.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            MilestoneStore.ResetForTesting();
            MissionStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            try { Directory.Delete(tempDir, true); } catch { }
        }

        // ---------------------------------------------------------------- ledger

        private static ConfigNode LedgerNodeWithVersion(string version)
        {
            Ledger.ResetForTesting();
            Ledger.AddAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.FacilityUpgrade,
                FacilityId = "SpaceCenter/MissionControl",
                ToLevel = 1,
                FacilityCost = 75000f,
            });
            Ledger.AddAction(new GameAction { UT = 0.0, Type = GameActionType.FundsInitial, InitialFunds = 25000f });
            ConfigNode node = Ledger.BuildSaveNode();
            if (version == null)
                node.RemoveValue("version");
            else
                node.SetValue("version", version, true);
            Ledger.ResetForTesting();
            return node;
        }

        [Fact]
        public void Ledger_NewerVersion_IsHeld_AndReEmittedByteIdentical()
        {
            ConfigNode original = LedgerNodeWithVersion("1");
            original.AddValue("futureLedgerKey", "kept as written");
            original.AddNode("FUTURE_CHILD").AddValue("x", "1");
            string originalText = original.ToString();

            Assert.False(Ledger.LoadFromNode(original));

            Assert.Empty(Ledger.Actions);
            Assert.True(Ledger.IsHeldFromNewerVersion);
            Assert.Single(logLines, l => l.Contains("[WARN][Ledger]")
                && l.Contains("written by a newer Parsek: version=1 > current=0")
                && l.Contains("held verbatim (2 row(s))"));
            Assert.Equal(originalText, Ledger.BuildSaveNode().ToString());
            Assert.Equal(originalText, original.ToString());

            // The mirror file gets the held node too, never an empty ledger.
            string path = Path.Combine(tempDir, "ledger.pgld");
            Assert.True(Ledger.SaveToFile(path));
            ConfigNode reread = ConfigNode.Load(path);
            Assert.Equal("1", reread.GetValue("version"));
            Assert.Equal(2, reread.GetNodes("GAME_ACTION").Length);
            Assert.Equal("kept as written", reread.GetValue("futureLedgerKey"));

            // The next load of a readable ledger releases the hold.
            Assert.True(Ledger.LoadFromNode(LedgerNodeWithVersion("0")));
            Assert.False(Ledger.IsHeldFromNewerVersion);
            Assert.Equal(2, Ledger.Actions.Count);
        }

        [Fact]
        public void Ledger_NewerVersionFile_IsNotOverwrittenByTheNextSave()
        {
            string path = Path.Combine(tempDir, "ledger.pgld");
            ConfigNode original = LedgerNodeWithVersion("3");
            original.Save(path);
            Assert.False(Ledger.LoadFromFile(path));
            Assert.True(Ledger.IsHeldFromNewerVersion);

            // A session row (not a seed) is reported, not written.
            Ledger.AddAction(new GameAction { UT = 50.0, Type = GameActionType.FundsSpending, FundsSpent = 10f });
            Assert.True(Ledger.SaveToFile(path));

            ConfigNode reread = ConfigNode.Load(path);
            Assert.Equal("3", reread.GetValue("version"));
            Assert.Equal(2, reread.GetNodes("GAME_ACTION").Length);
            Assert.Contains(logLines, l => l.Contains("[WARN][Ledger]")
                && l.Contains("1 row(s) made this session are not saved"));
        }

        [Theory]
        [InlineData("-1")]
        [InlineData(null)]
        public void Ledger_PreBaselineVersion_StartsEmpty_WithAnInfoLine(string version)
        {
            Assert.False(Ledger.LoadFromNode(LedgerNodeWithVersion(version)));

            Assert.Empty(Ledger.Actions);
            Assert.False(Ledger.IsHeldFromNewerVersion);
            Assert.Contains(logLines, l => l.Contains("[INFO][Ledger]")
                && l.Contains("pre-1.0 version='" + (version ?? "(missing)") + "'")
                && l.Contains("starting with empty ledger"));
            Assert.DoesNotContain(logLines, l => l.Contains("[WARN][Ledger]"));
            Assert.Empty(Ledger.BuildSaveNode().GetNodes("GAME_ACTION"));
        }

        [Fact]
        public void Ledger_Upgradable_RunsTheSteps_ThenLoads()
        {
            SaveFormat.SetCurrentForTesting(SaveFormat.Ledger, Ledger.CurrentLedgerVersion + 1);
            int stepRuns = 0;
            SaveFormat.RegisterStepForTesting(SaveFormat.Ledger, new SaveFormatUpgradeStep
            {
                From = Ledger.CurrentLedgerVersion,
                UpgradeNode = n => { stepRuns++; return n.GetNodes("GAME_ACTION").Length == 2; },
            });

            Assert.True(Ledger.LoadFromNode(LedgerNodeWithVersion("0")));

            Assert.Equal(1, stepRuns);
            Assert.Equal(2, Ledger.Actions.Count);
            Assert.False(Ledger.IsHeldFromNewerVersion);
        }

        [Fact]
        public void Ledger_UpgradableWithoutAPath_IsHeld()
        {
            SaveFormat.SetCurrentForTesting(SaveFormat.Ledger, Ledger.CurrentLedgerVersion + 1);
            ConfigNode original = LedgerNodeWithVersion("0");

            Assert.False(Ledger.LoadFromNode(original));

            Assert.True(Ledger.IsHeldFromNewerVersion);
            Assert.Empty(Ledger.Actions);
            Assert.Contains(logLines, l => l.Contains("[WARN][Ledger]") && l.Contains("could not be upgraded to 1 (no-upgrade-path)"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void HeldLedger_BlocksEveryKspStatePatch(bool cutoffWalk)
        {
            Assert.False(Ledger.LoadFromNode(LedgerNodeWithVersion("1")));
            LedgerOrchestrator.Initialize();
            logLines.Clear();

            if (cutoffWalk)
                LedgerOrchestrator.RecalculateAndPatch(100.0);
            else
                LedgerOrchestrator.RecalculateAndPatch();

            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("deferred KSP state patch (ledger held from a newer Parsek version)"));
            Assert.Contains(logLines, l => l.Contains("[INFO][LedgerOrchestrator]")
                && l.Contains("KSP state patch skipped (ledger held from a newer Parsek version)"));
            Assert.DoesNotContain(logLines, l => l.Contains("[KspStatePatcher]") && l.Contains("PatchAll complete"));
        }

        [Fact]
        public void ReadableLedger_StillPatches()
        {
            Assert.True(Ledger.LoadFromNode(LedgerNodeWithVersion("0")));
            LedgerOrchestrator.Initialize();
            logLines.Clear();

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Contains(logLines, l => l.Contains("[KspStatePatcher]") && l.Contains("PatchAll complete"));
            Assert.DoesNotContain(logLines, l => l.Contains("ledger held from a newer Parsek version"));
        }
    }
}
