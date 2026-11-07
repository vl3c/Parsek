using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pre-Parsek backup hold (owner ruling 2026-11-06): when the first-time backup of a
    /// save fails, Parsek stays inert in that save through <see cref="ParsekGameModeGate"/>,
    /// saves its node back as KSP's empty one (so the save keeps no Parsek footprint and the
    /// retry is real), and a load from the main menu clears the hold and retries.
    /// </summary>
    [Collection("Sequential")]
    public class PreParsekBackupHoldTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PreParsekBackupHoldTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekGameModeGate.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekGameModeGate.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void NoHold_ActiveCareer_IsNotInert()
        {
            ParsekGameModeGate.ModeOverrideForTesting = Game.Modes.CAREER;
            ParsekGameModeGate.SaveFolderOverrideForTesting = "MyCareer";
            Assert.False(PreParsekBackup.IsAnyHoldArmed);
            Assert.False(ParsekGameModeGate.IsInertForCurrentGame);
            Assert.False(ParsekGameModeGate.CheckInert("test"));
            Assert.DoesNotContain(logLines, l => l.Contains("[GameModeGate]"));
        }

        [Fact]
        public void HoldOnCurrentSave_ActiveCareer_IsInert_AndLogsOncePerScene()
        {
            ParsekGameModeGate.ModeOverrideForTesting = Game.Modes.CAREER;
            ParsekGameModeGate.SceneOverrideForTesting = GameScenes.SPACECENTER;
            ParsekGameModeGate.SaveFolderOverrideForTesting = "MyCareer";
            PreParsekBackup.ArmHold("MyCareer", "first-contact-backup-failed");

            Assert.True(ParsekGameModeGate.IsInertForCurrentGame);
            Assert.True(ParsekGameModeGate.CheckInert("ParsekKSC.Start"));
            Assert.True(ParsekGameModeGate.CheckInert("StockUiOverlayController.Awake"));

            var gateLines = logLines.Where(l => l.Contains("[GameModeGate]")).ToList();
            Assert.Single(gateLines);
            Assert.Contains("[INFO]", gateLines[0]);
            Assert.Contains("save 'MyCareer' has no pre-Parsek backup", gateLines[0]);
            Assert.Contains("scene=SPACECENTER site=ParsekKSC.Start", gateLines[0]);
            Assert.DoesNotContain("game mode", gateLines[0]);

            ParsekGameModeGate.SceneOverrideForTesting = GameScenes.FLIGHT;
            Assert.True(ParsekGameModeGate.CheckInert("ParsekFlight.Start"));
            Assert.Equal(2, logLines.Count(l => l.Contains("[GameModeGate]")));
        }

        [Fact]
        public void HoldOnAnotherSave_DoesNotTouchThisSave()
        {
            ParsekGameModeGate.ModeOverrideForTesting = Game.Modes.CAREER;
            ParsekGameModeGate.SaveFolderOverrideForTesting = "OtherCareer";
            PreParsekBackup.ArmHold("MyCareer", "first-contact-backup-failed");

            Assert.True(PreParsekBackup.IsAnyHoldArmed);
            Assert.False(ParsekGameModeGate.IsInertForCurrentGame);
            Assert.False(ParsekGameModeGate.CheckInert("test"));
        }

        [Fact]
        public void ClearHold_ReleasesTheSave_AndLogs()
        {
            ParsekGameModeGate.ModeOverrideForTesting = Game.Modes.CAREER;
            ParsekGameModeGate.SaveFolderOverrideForTesting = "MyCareer";
            PreParsekBackup.ArmHold("MyCareer", "first-contact-backup-failed");
            Assert.True(ParsekGameModeGate.IsInertForCurrentGame);

            PreParsekBackup.ClearHold("fresh load from the main menu: retrying the backup");

            Assert.False(PreParsekBackup.IsAnyHoldArmed);
            Assert.False(ParsekGameModeGate.IsInertForCurrentGame);
            Assert.Contains(logLines, l => l.Contains("[Backup]") && l.Contains("[WARN]")
                && l.Contains("Backup hold armed: save='MyCareer' reason=first-contact-backup-failed"));
            Assert.Contains(logLines, l => l.Contains("[Backup]")
                && l.Contains("Backup hold cleared: save='MyCareer' reason=fresh load from the main menu"));
        }

        [Fact]
        public void ArmHold_WithoutASaveFolder_ArmsNothing()
        {
            PreParsekBackup.ArmHold("", "first-contact-backup-failed");
            Assert.False(PreParsekBackup.IsAnyHoldArmed);
            Assert.False(PreParsekBackup.IsHoldActiveFor(""));
            Assert.Contains(logLines, l => l.Contains("[Backup]") && l.Contains("Backup hold NOT armed"));
        }

        [Fact]
        public void IsHoldActiveFor_IsExactAndCaseSensitive()
        {
            PreParsekBackup.ArmHold("MyCareer", "first-contact-backup-failed");
            Assert.True(PreParsekBackup.IsHoldActiveFor("MyCareer"));
            Assert.False(PreParsekBackup.IsHoldActiveFor("mycareer"));
            Assert.False(PreParsekBackup.IsHoldActiveFor(null));
        }

        [Fact]
        public void MissionGame_WithAHold_StillReportsTheGameModeReason()
        {
            ParsekGameModeGate.ModeOverrideForTesting = Game.Modes.MISSION;
            ParsekGameModeGate.SaveFolderOverrideForTesting = "MyCareer";
            PreParsekBackup.ArmHold("MyCareer", "first-contact-backup-failed");

            Assert.True(ParsekGameModeGate.CheckInert("test"));
            string line = logLines.Single(l => l.Contains("[GameModeGate]"));
            Assert.Contains("game mode MISSION", line);
        }

        [Fact]
        public void HeldScenario_SavesTheEmptyNodeBack_SoTheSaveKeepsNoFootprint()
        {
            // The pristine save's node is exactly what KSP's AddToAllGames injection writes.
            var loaded = new ConfigNode("SCENARIO");
            loaded.AddValue("name", "ParsekScenario");
            loaded.AddValue("scene", "7, 5, 8, 6");

            ParsekGameModeGate.ModeOverrideForTesting = Game.Modes.CAREER;
            ParsekGameModeGate.SaveFolderOverrideForTesting = "MyCareer";
            PreParsekBackup.ArmHold("MyCareer", "first-contact-backup-failed");

            var scenario = new ParsekScenario();
            scenario.StashInertGameModeNode(loaded, "pre-Parsek backup hold");

            var saved = new ConfigNode("SCENARIO");
            saved.AddValue("name", "ParsekScenario");
            saved.AddValue("scene", "7, 5, 8, 6");
            Assert.True(ParsekGameModeGate.CheckInert("ParsekScenario.OnSave"));
            scenario.WriteInertGameModeNode(saved);

            // No format stamp, no stores: the next cold load still sees a footprint-free save
            // and retries the backup.
            Assert.Equal(2, saved.values.Count);
            Assert.Empty(saved.nodes.Cast<ConfigNode>());
            var root = new ConfigNode();
            root.AddNode("GAME").AddNode(saved);
            Assert.False(PreParsekBackup.HasParsekGameplayFootprint(root, parsekSubdirExists: false));

            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("OnLoad: pre-Parsek backup hold - Parsek node kept verbatim"));
            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("OnSave: pre-Parsek backup hold - wrote the loaded Parsek node back verbatim")
                && l.Contains("(0 entries)"));
        }

        [Fact]
        public void HoldScreenMessage_SaysParsekIsOff_AndHowToRetry()
        {
            string msg = PreParsekBackup.HoldScreenMessage;
            Assert.Contains("could not back up this save", msg);
            Assert.Contains("switched off", msg);
            Assert.Contains("from the main menu", msg);
        }

        [Fact]
        public void OnLoad_ChecksTheHoldBeforeTheGameModeGate_AndHoldsOnAFailedBackup()
        {
            string path = Path.Combine(RepoRoot(), "Source", "Parsek", "ParsekScenario.cs");
            Assert.True(File.Exists(path), "Source file not found at " + path);
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(ParsekScenarioSourceFiles.ReadText(path));
            int sig = prepared.IndexOf("public override void OnLoad(ConfigNode node)", StringComparison.Ordinal);
            Assert.True(sig >= 0, "OnLoad not found");
            string body = SourceScanText.BraceMatchedBlock(prepared, prepared.IndexOf('{', sig));

            int hold = body.IndexOf("ShouldKeepBackupHold()", StringComparison.Ordinal);
            int gate = body.IndexOf("ParsekGameModeGate.CheckInert(", StringComparison.Ordinal);
            Assert.True(hold >= 0, "OnLoad must check the backup hold");
            Assert.True(gate >= 0, "OnLoad must check the game-mode gate");
            Assert.True(hold < gate, "the hold check must run before the game-mode gate");

            int attempt = body.IndexOf("PreParsekBackup.MaybeBackupOnFirstColdContact()", StringComparison.Ordinal);
            int failed = body.IndexOf("BackupAttemptOutcome.Failed", StringComparison.Ordinal);
            int enter = body.IndexOf("EnterBackupHold(node", StringComparison.Ordinal);
            int externalFiles = body.IndexOf("LoadExternalFiles(node)", StringComparison.Ordinal);
            Assert.True(attempt >= 0 && failed > attempt && enter > failed,
                "a failed backup attempt must enter the hold");
            Assert.True(externalFiles > enter,
                "the hold must be entered before Parsek's first read of its own files");
        }

        [Fact]
        public void AutomationFault_ALoadWithNoAttempt_DropsIt_WithAWarn()
        {
            PreParsekBackup.ResetHoldForTesting();
            PreParsekBackup.ArmAutomationFaultForNextAttempt("test");
            Assert.True(PreParsekBackup.IsAutomationFaultArmedForTesting);

            PreParsekBackup.DropUnusedAutomationFault("an in-session load makes no backup attempt");

            Assert.False(PreParsekBackup.IsAutomationFaultArmedForTesting);
            Assert.Contains(logLines, l => l.Contains("[Backup]") && l.Contains("[WARN]")
                && l.Contains("Backup fault disarmed unused: an in-session load makes no backup attempt"));
        }

        [Fact]
        public void AutomationFault_DropWhenNotArmed_LogsNothing()
        {
            PreParsekBackup.ResetHoldForTesting();
            PreParsekBackup.DropUnusedAutomationFault("an inert load makes no backup attempt");
            Assert.DoesNotContain(logLines, l => l.Contains("Backup fault disarmed unused"));
        }

        [Fact]
        public void OnLoad_DropsAnUnusedFault_OnTheInertAndInSessionPaths()
        {
            string path = Path.Combine(RepoRoot(), "Source", "Parsek", "ParsekScenario.cs");
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(ParsekScenarioSourceFiles.ReadText(path));
            int sig = prepared.IndexOf("public override void OnLoad(ConfigNode node)", StringComparison.Ordinal);
            string body = SourceScanText.BraceMatchedBlock(prepared, prepared.IndexOf('{', sig));

            int gate = body.IndexOf("ParsekGameModeGate.CheckInert(", StringComparison.Ordinal);
            int stash = body.IndexOf("StashInertGameModeNode(node", StringComparison.Ordinal);
            int firstDrop = body.IndexOf("PreParsekBackup.DropUnusedAutomationFault(", StringComparison.Ordinal);
            int firstReturn = body.IndexOf("return;", gate, StringComparison.Ordinal);
            Assert.True(firstDrop > stash && firstDrop < firstReturn,
                "the inert / held return must drop an unused fault");

            int attempt = body.IndexOf("PreParsekBackup.MaybeBackupOnFirstColdContact()", StringComparison.Ordinal);
            int secondDrop = body.IndexOf("PreParsekBackup.DropUnusedAutomationFault(", firstDrop + 1, StringComparison.Ordinal);
            Assert.True(secondDrop > attempt,
                "a load that skips the cold backup attempt must drop an unused fault");
        }

        [Fact]
        public void SeamArmsTheFault_OnlyAfterEveryRefusal()
        {
            string path = Path.Combine(RepoRoot(), "Source", "Parsek", "TestCommands", "ParsekTestCommandAddon.cs");
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
            int arm = prepared.IndexOf("PreParsekBackup.ArmAutomationFaultForNextAttempt(", StringComparison.Ordinal);
            int routeFailed = prepared.IndexOf("route == LoadRoute.Failed", StringComparison.Ordinal);
            int focusRefusal = prepared.IndexOf("TestCommandLoadGame.DecideFocus(", StringComparison.Ordinal);
            Assert.True(arm >= 0, "the seam must arm the fault");
            Assert.True(arm > routeFailed && arm > focusRefusal,
                "the fault is armed after the last refusal, so a refused load arms nothing");
            Assert.Equal(arm, prepared.LastIndexOf("PreParsekBackup.ArmAutomationFaultForNextAttempt(", StringComparison.Ordinal));
        }

        private static string RepoRoot()
        {
            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
        }
    }
}
