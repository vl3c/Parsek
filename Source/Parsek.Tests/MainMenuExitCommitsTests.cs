using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Leaving flight commits (owner ruling 2026-11-01). Under auto-merge a main-menu exit
    /// runs the merge dialog's Merge itself (the game unloads, so no destination scene can
    /// commit), and the switch-segment session branch follows auto-merge instead of always
    /// asking. A Re-Fly keeps its dialog; auto-merge off keeps today's dialog.
    /// </summary>
    [Collection("Sequential")]
    public class MainMenuExitCommitsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly GameScenes priorScene;
        private int saves;

        public MainMenuExitCommitsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = true;
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.PendingScienceSubjects.Clear();
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.SaveGameForTesting = null;
            MergeJournalOrchestrator.ResetTestOverrides();
            RewindPointReaper.ResetTestOverrides();
            TreeDiscardPurge.ResetTestOverrides();
            RewindContext.ResetForTesting();
            SceneExitInterceptor.ResetTestOverrides();
            SceneExitInterceptor.PersistentSaveStepForTesting = _ => saves++;
            ParsekScenario.SetCachedAutoMergeForTesting(true);
            priorScene = HighLogic.LoadedScene;
            HighLogic.LoadedScene = GameScenes.FLIGHT;
        }

        public void Dispose()
        {
            HighLogic.LoadedScene = priorScene;
            SceneExitInterceptor.ResetTestOverrides();
            RewindContext.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateRecorder.PendingScienceSubjects.Clear();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetCachedAutoMergeForTesting(false);
            RecordingStore.SaveGameForTesting = null;
            MergeJournalOrchestrator.ResetTestOverrides();
            RewindPointReaper.ResetTestOverrides();
            TreeDiscardPurge.ResetTestOverrides();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = false;
        }

        private static RecordingTree FinishedTree(string treeId)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Tree " + treeId,
                RootRecordingId = treeId + "_root",
                ActiveRecordingId = treeId + "_root",
            };
            var rec = new Recording
            {
                RecordingId = treeId + "_root",
                TreeId = treeId,
                VesselName = "Vessel " + treeId,
                TerminalStateValue = TerminalState.Landed,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 10, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 20, bodyName = "Kerbin" });
            tree.Recordings[rec.RecordingId] = rec;
            return tree;
        }

        private ParsekScenario InstallScenario()
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            return scenario;
        }

        private static void ArmSession(ParsekScenario scenario, string treeId)
        {
            scenario.ArmSwitchSegmentSession(new SwitchSegmentSession
            {
                SessionId = Guid.NewGuid(),
                IntentId = Guid.NewGuid(),
                EntryReason = SwitchSegmentEntryReason.MapSwitchTo,
                TreeId = treeId,
                ActiveSegmentRecordingId = treeId + "_root",
                SwitchUT = 15.0,
                PreSessionBranchPointIds = new List<string>(),
            });
        }

        // ---- pure ----------------------------------------------------------

        [Theory]
        [InlineData(GameScenes.MAINMENU, (int)SceneExitInterceptor.DialogVariant.RegularMerge, true, false, true)]
        [InlineData(GameScenes.MAINMENU, (int)SceneExitInterceptor.DialogVariant.RegularMerge, false, false, false)]
        [InlineData(GameScenes.MAINMENU, (int)SceneExitInterceptor.DialogVariant.RegularMerge, true, true, false)]
        [InlineData(GameScenes.MAINMENU, (int)SceneExitInterceptor.DialogVariant.ReFlyAttempt, true, false, false)]
        [InlineData(GameScenes.MAINMENU, (int)SceneExitInterceptor.DialogVariant.None, true, false, false)]
        [InlineData(GameScenes.SPACECENTER, (int)SceneExitInterceptor.DialogVariant.RegularMerge, true, false, false)]
        [InlineData(GameScenes.TRACKSTATION, (int)SceneExitInterceptor.DialogVariant.RegularMerge, true, false, false)]
        public void ShouldCommitMainMenuExitWithoutDialog_OnlyThePlainMainMenuDialogUnderAutoMerge(
            GameScenes destination, int variant,
            bool isAutoMerge, bool journalActive, bool expected)
        {
            Assert.Equal(expected, SceneExitInterceptor.ShouldCommitMainMenuExitWithoutDialog(
                destination, (SceneExitInterceptor.DialogVariant)variant, isAutoMerge, journalActive));
        }

        [Theory]
        [InlineData(true, false, true)]
        [InlineData(false, false, false)]
        [InlineData(true, true, false)]
        [InlineData(false, true, false)]
        public void ShouldSessionExitFollowAutoMerge_ReFlyAndAutoMergeOffKeepTheDialog(
            bool isAutoMerge, bool reFlyActive, bool expected)
        {
            Assert.Equal(expected,
                SceneExitInterceptor.ShouldSessionExitFollowAutoMerge(isAutoMerge, reFlyActive));
        }

        // ---- pending-tree branch (flight results, destroyed vessel) --------

        [Fact]
        public void MainMenu_FinalizedPendingTree_CommitsAndSavesWithoutDialog()
        {
            SceneExitInterceptor.DialogVariant? shown = null;
            SceneExitInterceptor.ShowDialogForTesting = (scene, variant) => shown = variant;
            PendingTreeSlot.StashPendingTree(FinishedTree("pend"), PendingTreeState.Finalized);

            bool allowStockLoad = HighLogic_LoadScene_Patch.Prefix(GameScenes.MAINMENU);

            Assert.True(allowStockLoad);
            Assert.Null(shown);
            Assert.False(PendingTreeSlot.HasPendingTree);
            Assert.Contains(RecordingStore.CommittedTrees, t => t.Id == "pend");
            Assert.Equal(1, saves);
            Assert.Contains(logLines, l => l.Contains("[SceneExit]")
                && l.Contains("main-menu exit committed without the dialog branch=pending-tree")
                && l.Contains("outcome=Completed"));
        }

        [Fact]
        public void MainMenu_AutoMergeOff_StillAsks()
        {
            ParsekScenario.SetCachedAutoMergeForTesting(false);
            SceneExitInterceptor.DialogVariant? shown = null;
            SceneExitInterceptor.ShowDialogForTesting = (scene, variant) => shown = variant;
            PendingTreeSlot.StashPendingTree(FinishedTree("pend"), PendingTreeState.Finalized);

            bool allowStockLoad = HighLogic_LoadScene_Patch.Prefix(GameScenes.MAINMENU);

            Assert.False(allowStockLoad);
            Assert.Equal(SceneExitInterceptor.DialogVariant.RegularMerge, shown);
            Assert.True(PendingTreeSlot.HasPendingTree);
            Assert.Equal(0, saves);
        }

        [Fact]
        public void MainMenu_CommitRefused_FallsBackToTheDialog()
        {
            SceneExitInterceptor.DialogVariant? shown = null;
            SceneExitInterceptor.ShowDialogForTesting = (scene, variant) => shown = variant;
            SceneExitInterceptor.CommitWithoutDialogForTesting =
                (finalize, branch) => MergeDialog.PreTransitionOutcome.Refused;
            PendingTreeSlot.StashPendingTree(FinishedTree("pend"), PendingTreeState.Finalized);

            bool allowStockLoad = HighLogic_LoadScene_Patch.Prefix(GameScenes.MAINMENU);

            Assert.False(allowStockLoad);
            Assert.Equal(SceneExitInterceptor.DialogVariant.RegularMerge, shown);
            Assert.Equal(0, saves);
            Assert.Contains(logLines, l => l.Contains("[SceneExit]")
                && l.Contains("outcome=Refused")
                && l.Contains("falling back to the merge dialog"));
        }

        [Fact]
        public void MainMenu_SaveFails_BlocksTheLoad()
        {
            SceneExitInterceptor.ShowDialogForTesting = (scene, variant) => { };
            SceneExitInterceptor.PersistentSaveStepForTesting =
                _ => throw new InvalidOperationException("disk full");
            PendingTreeSlot.StashPendingTree(FinishedTree("pend"), PendingTreeState.Finalized);

            bool allowStockLoad = HighLogic_LoadScene_Patch.Prefix(GameScenes.MAINMENU);

            Assert.False(allowStockLoad);
            Assert.Contains(RecordingStore.CommittedTrees, t => t.Id == "pend");
        }

        [Fact]
        public void SpaceCenter_FinalizedPendingTree_IsLeftToTheDestination()
        {
            // Control: other exits keep committing at the destination scene's load.
            SceneExitInterceptor.DialogVariant? shown = null;
            SceneExitInterceptor.ShowDialogForTesting = (scene, variant) => shown = variant;
            PendingTreeSlot.StashPendingTree(FinishedTree("pend"), PendingTreeState.Finalized);

            bool allowStockLoad = HighLogic_LoadScene_Patch.Prefix(GameScenes.SPACECENTER);

            Assert.True(allowStockLoad);
            Assert.Null(shown);
            Assert.True(PendingTreeSlot.HasPendingTree);
            Assert.Equal(0, saves);
        }

        // ---- switch-segment session branch (no live tree) -----------------

        [Theory]
        [InlineData(GameScenes.SPACECENTER)]
        [InlineData(GameScenes.TRACKSTATION)]
        [InlineData(GameScenes.MAINMENU)]
        public void Session_CommittedTree_ClearsTheSessionAndShowsNoDialog(GameScenes destination)
        {
            var scenario = InstallScenario();
            TreeCommitService.CommitTree(FinishedTree("committed"));
            ArmSession(scenario, "committed");
            SceneExitInterceptor.DialogVariant? shown = null;
            SceneExitInterceptor.ShowDialogForTesting = (scene, variant) => shown = variant;

            bool allowStockLoad = HighLogic_LoadScene_Patch.Prefix(destination);

            Assert.True(allowStockLoad);
            Assert.Null(shown);
            Assert.Null(scenario.ActiveSwitchSegmentSession);
            Assert.Contains(logLines, l => l.Contains("[SceneExit]")
                && l.Contains("has no pending tree - following auto-merge")
                && l.Contains("clearing the session, no dialog"));
        }

        [Fact]
        public void Session_PendingTree_MainMenu_CommitsItAndTheCommitClearsTheSession()
        {
            var scenario = InstallScenario();
            PendingTreeSlot.StashPendingTree(FinishedTree("seg"), PendingTreeState.Finalized);
            ArmSession(scenario, "seg");
            SceneExitInterceptor.DialogVariant? shown = null;
            SceneExitInterceptor.ShowDialogForTesting = (scene, variant) => shown = variant;

            bool allowStockLoad = HighLogic_LoadScene_Patch.Prefix(GameScenes.MAINMENU);

            Assert.True(allowStockLoad);
            Assert.Null(shown);
            Assert.Contains(RecordingStore.CommittedTrees, t => t.Id == "seg");
            Assert.Null(scenario.ActiveSwitchSegmentSession);
            Assert.Equal(1, saves);
            Assert.Contains(logLines, l => l.Contains("[SceneExit]")
                && l.Contains("is the pending tree - following auto-merge"));
        }

        [Fact]
        public void Session_PendingTree_SpaceCenter_LeavesTheTreeAndSessionToTheDestination()
        {
            var scenario = InstallScenario();
            PendingTreeSlot.StashPendingTree(FinishedTree("seg"), PendingTreeState.Finalized);
            ArmSession(scenario, "seg");
            SceneExitInterceptor.DialogVariant? shown = null;
            SceneExitInterceptor.ShowDialogForTesting = (scene, variant) => shown = variant;

            bool allowStockLoad = HighLogic_LoadScene_Patch.Prefix(GameScenes.SPACECENTER);

            Assert.True(allowStockLoad);
            Assert.Null(shown);
            Assert.True(PendingTreeSlot.HasPendingTree);
            Assert.NotNull(scenario.ActiveSwitchSegmentSession);
        }

        [Fact]
        public void Session_AutoMergeOff_StillAsks()
        {
            ParsekScenario.SetCachedAutoMergeForTesting(false);
            var scenario = InstallScenario();
            TreeCommitService.CommitTree(FinishedTree("committed"));
            ArmSession(scenario, "committed");
            SceneExitInterceptor.DialogVariant? shown = null;
            SceneExitInterceptor.ShowDialogForTesting = (scene, variant) => shown = variant;

            bool allowStockLoad = HighLogic_LoadScene_Patch.Prefix(GameScenes.SPACECENTER);

            Assert.False(allowStockLoad);
            Assert.Equal(SceneExitInterceptor.DialogVariant.RegularMerge, shown);
            Assert.NotNull(scenario.ActiveSwitchSegmentSession);
        }
    }
}
