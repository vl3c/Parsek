using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Stock's quit without saving follows the save (owner ruling 2026-11-01): the flight since
    /// the last save is dropped with the world. The detector reads a confirm-exit with no game
    /// save after it in the same frame; the LoadScene prefix then shows no dialog, commits
    /// nothing and writes nothing, and arms the save-file load intent for the Space Center and
    /// the Tracking Station so their reload of persistent.sfs is a Cold load.
    /// </summary>
    [Collection("Sequential")]
    public class NoSaveSceneExitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly GameScenes priorScene;
        private int saves;
        private int frame = 100;
        private readonly List<string> deleted = new List<string>();
        private readonly List<string> known = new List<string>();

        public NoSaveSceneExitTests()
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
            SaveFileLoadIntent.ResetForTesting();
            SceneExitInterceptor.ResetTestOverrides();
            SceneExitInterceptor.PersistentSaveStepForTesting = _ => saves++;
            SceneExitInterceptor.SaveFileLoadIntentRealtimeForTesting = () => 50.0;
            SceneExitInterceptor.SaveFileLoadIntentFrameForTesting = () => frame;
            NoSaveSceneExit.FrameForTesting = () => frame;
            DiscardSidecarReap.ResetForTesting();
            DiscardSidecarReap.DeleteRecordingFilesForTesting = rec => { deleted.Add(rec.RecordingId); return true; };
            DiscardSidecarReap.KnownRecordingIdsForTesting = () => new HashSet<string>(known);
            ParsekScenario.SetCachedAutoMergeForTesting(true);
            priorScene = HighLogic.LoadedScene;
            HighLogic.LoadedScene = GameScenes.FLIGHT;
        }

        public void Dispose()
        {
            HighLogic.LoadedScene = priorScene;
            SceneExitInterceptor.ResetTestOverrides();
            NoSaveSceneExit.ResetForTesting();
            DiscardSidecarReap.ResetForTesting();
            SaveFileLoadIntent.ResetForTesting();
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

        private static ParsekScenario InstallScenario()
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

        // ---- pure ----------------------------------------------------------

        [Theory]
        [InlineData(100, false, 100, true)]   // confirm-exit, no save, same frame: quit without saving
        [InlineData(100, true, 100, false)]   // saveAndExit / SafeWritePersistent saved after it
        [InlineData(99, false, 100, false)]   // an older frame's note never counts
        [InlineData(-1, false, 100, false)]   // nothing noted
        public void IsNoSaveExit_OnlyAnUnsavedConfirmExitInThisFrame(
            int confirmFrame, bool saved, int nowFrame, bool expected)
        {
            Assert.Equal(expected, NoSaveSceneExit.IsNoSaveExit(confirmFrame, saved, nowFrame));
        }

        [Theory]
        [InlineData(GameScenes.SPACECENTER, true, false, false, false, true)]
        [InlineData(GameScenes.TRACKSTATION, true, false, false, false, true)]
        [InlineData(GameScenes.MAINMENU, true, false, false, false, true)]
        [InlineData(GameScenes.EDITOR, true, false, false, false, false)]
        [InlineData(GameScenes.SPACECENTER, false, false, false, false, false)]
        [InlineData(GameScenes.SPACECENTER, true, true, false, false, false)]
        [InlineData(GameScenes.SPACECENTER, true, false, true, false, false)]
        [InlineData(GameScenes.MAINMENU, true, false, false, true, false)]
        public void ShouldFollowTheSave_ThreeDestinations_NotForRewindReFlyOrJournal(
            GameScenes destination, bool noSave, bool rewinding, bool reFly, bool journal, bool expected)
        {
            Assert.Equal(expected, NoSaveSceneExit.ShouldFollowTheSave(
                destination, noSave, rewinding, reFly, journal));
        }

        [Fact]
        public void Notes_SaveAfterConfirmReadsAsSaved_AndConsumeClearsTheNote()
        {
            NoSaveSceneExit.NoteConfirmExit(frame);
            NoSaveSceneExit.NoteGameSaved(frame);
            Assert.False(NoSaveSceneExit.ConsumeAtLoadScene(GameScenes.SPACECENTER));

            NoSaveSceneExit.NoteConfirmExit(frame);
            Assert.True(NoSaveSceneExit.ConsumeAtLoadScene(GameScenes.SPACECENTER));
            Assert.False(NoSaveSceneExit.ConsumeAtLoadScene(GameScenes.SPACECENTER));

            // A save in an earlier frame does not mark a later confirm-exit as saved.
            NoSaveSceneExit.NoteGameSaved(frame);
            frame++;
            NoSaveSceneExit.NoteConfirmExit(frame);
            Assert.True(NoSaveSceneExit.ConsumeAtLoadScene(GameScenes.MAINMENU));
        }

        [Fact]
        public void Arm_IsFrameAndDestinationExact()
        {
            NoSaveSceneExit.Arm(GameScenes.SPACECENTER);

            Assert.True(NoSaveSceneExit.IsArmedFor(GameScenes.SPACECENTER));
            Assert.False(NoSaveSceneExit.IsArmedFor(GameScenes.TRACKSTATION));
            frame++;
            Assert.False(NoSaveSceneExit.IsArmedFor(GameScenes.SPACECENTER));
        }

        // ---- LoadScene prefix ---------------------------------------------

        [Fact]
        public void MainMenu_QuitWithoutSaving_CommitsNothingAndWritesNothing()
        {
            SceneExitInterceptor.DialogVariant? shown = null;
            SceneExitInterceptor.ShowDialogForTesting = (scene, variant) => shown = variant;
            PendingTreeSlot.StashPendingTree(FinishedTree("pend"), PendingTreeState.Finalized);
            NoSaveSceneExit.NoteConfirmExit(frame);

            bool allowStockLoad = HighLogic_LoadScene_Patch.Prefix(GameScenes.MAINMENU);

            Assert.True(allowStockLoad);
            Assert.Null(shown);
            Assert.Equal(0, saves);
            Assert.DoesNotContain(RecordingStore.CommittedTrees, t => t.Id == "pend");
            Assert.True(NoSaveSceneExit.IsArmedFor(GameScenes.MAINMENU));
            Assert.False(SaveFileLoadIntent.IsArmed);
            Assert.Contains(logLines, l => l.Contains("[SceneExit]")
                && l.Contains("quit without saving dest=MAINMENU - following the save"));
        }

        [Theory]
        [InlineData(GameScenes.SPACECENTER)]
        [InlineData(GameScenes.TRACKSTATION)]
        public void SpaceCenterOrTrackingStation_QuitWithoutSaving_ArmsTheColdLoad(GameScenes destination)
        {
            PendingTreeSlot.StashPendingTree(FinishedTree("pend"), PendingTreeState.Finalized);
            NoSaveSceneExit.NoteConfirmExit(frame);

            bool allowStockLoad = HighLogic_LoadScene_Patch.Prefix(destination);

            Assert.True(allowStockLoad);
            Assert.Equal(0, saves);
            Assert.True(SaveFileLoadIntent.IsArmed);
            Assert.True(NoSaveSceneExit.IsArmedFor(destination));
            Assert.Contains(logLines, l => l.Contains("[LoadPolicy]")
                && l.Contains("Save-file load intent armed")
                && l.Contains("quit without saving"));
        }

        [Fact]
        public void AutoMergeOff_QuitWithoutSaving_ShowsNoDialog()
        {
            ParsekScenario.SetCachedAutoMergeForTesting(false);
            SceneExitInterceptor.DialogVariant? shown = null;
            SceneExitInterceptor.ShowDialogForTesting = (scene, variant) => shown = variant;
            PendingTreeSlot.StashPendingTree(FinishedTree("pend"), PendingTreeState.Finalized);
            NoSaveSceneExit.NoteConfirmExit(frame);

            bool allowStockLoad = HighLogic_LoadScene_Patch.Prefix(GameScenes.SPACECENTER);

            Assert.True(allowStockLoad);
            Assert.Null(shown);
            Assert.Equal(0, saves);
        }

        [Fact]
        public void MainMenu_SavedExit_StillCommitsAndSaves()
        {
            PendingTreeSlot.StashPendingTree(FinishedTree("pend"), PendingTreeState.Finalized);
            NoSaveSceneExit.NoteConfirmExit(frame);
            NoSaveSceneExit.NoteGameSaved(frame);

            bool allowStockLoad = HighLogic_LoadScene_Patch.Prefix(GameScenes.MAINMENU);

            Assert.True(allowStockLoad);
            Assert.Contains(RecordingStore.CommittedTrees, t => t.Id == "pend");
            Assert.Equal(1, saves);
            Assert.False(NoSaveSceneExit.IsArmedFor(GameScenes.MAINMENU));
        }

        [Fact]
        public void ReFlyActive_QuitWithoutSaving_KeepsTheUsualHandling()
        {
            var scenario = InstallScenario();
            scenario.ActiveReFlySessionMarker = new ReFlySessionMarker
            {
                SessionId = "sess_nosave",
                TreeId = "pend",
            };
            PendingTreeSlot.StashPendingTree(FinishedTree("pend"), PendingTreeState.Finalized);
            NoSaveSceneExit.NoteConfirmExit(frame);
            SceneExitInterceptor.ShowDialogForTesting = (scene, variant) => { };

            HighLogic_LoadScene_Patch.Prefix(GameScenes.SPACECENTER);

            Assert.False(NoSaveSceneExit.IsArmedFor(GameScenes.SPACECENTER));
            Assert.False(SaveFileLoadIntent.IsArmed);
            Assert.Contains(logLines, l => l.Contains("[SceneExit]")
                && l.Contains("keeps the usual exit handling")
                && l.Contains("reFlyActive=True"));
        }
        // ---- the dropped flight's files (owner ruling "if unused, delete") ----

        [Theory]
        [InlineData("t1", "career", "t1", "career", true)]
        [InlineData("t1", "career", "t2", "career", false)]   // another tree was dropped
        [InlineData("t1", "career", "t1", "other", false)]    // another save was loaded: its folder
        [InlineData("t1", "career", null, "career", false)]   // nothing pending to drop
        [InlineData(null, "career", "t1", "career", false)]   // no quit without saving noted
        [InlineData("t1", null, "t1", null, false)]
        public void IsDroppedQuitWithoutSavingTree_SameTreeSameSaveOnly(
            string notedTree, string notedFolder, string droppedTree, string currentFolder, bool expected)
        {
            Assert.Equal(expected, NoSaveSceneExit.IsDroppedQuitWithoutSavingTree(
                notedTree, notedFolder, droppedTree, currentFolder));
        }

        private static RecordingTree TwoRecordingTree(string treeId)
        {
            var tree = FinishedTree(treeId);
            var child = new Recording { RecordingId = treeId + "_child", TreeId = treeId, VesselName = "Child" };
            tree.Recordings[child.RecordingId] = child;
            return tree;
        }

        [Fact]
        public void DroppedFlight_DeletesItsFilesAfterTheLoad_KeepsAnythingTheSaveNames()
        {
            NoSaveSceneExit.NoteStashedTree("qws", "career");
            NoSaveSceneExit.CaptureDroppedFlight(TwoRecordingTree("qws"), "career");
            known.Add("qws_root");   // the loaded save still names this one

            NoSaveSceneExit.ReapDroppedFlightSidecars(rewinding: false, reFlyMarkerActive: false, mergeJournalActive: false);

            Assert.Equal(new[] { "qws_child" }, deleted);
            Assert.Contains(logLines, l => l.Contains("[DiscardReap]")
                && l.Contains("context='quit-without-saving'")
                && l.Contains("reaped=1")
                && l.Contains("preservedKnown=1"));

            // Consumed: a second call deletes nothing.
            deleted.Clear();
            NoSaveSceneExit.ReapDroppedFlightSidecars(false, false, false);
            Assert.Empty(deleted);
        }

        [Fact]
        public void DroppedFlight_LoadOfAnotherSave_DeletesNothing_AndConsumesTheNote()
        {
            NoSaveSceneExit.NoteStashedTree("qws", "career");
            NoSaveSceneExit.CaptureDroppedFlight(TwoRecordingTree("qws"), "sandbox");
            NoSaveSceneExit.ReapDroppedFlightSidecars(false, false, false);
            Assert.Empty(deleted);

            // The note is gone: a later drop of the same tree id in the first folder deletes nothing.
            NoSaveSceneExit.CaptureDroppedFlight(TwoRecordingTree("qws"), "career");
            NoSaveSceneExit.ReapDroppedFlightSidecars(false, false, false);
            Assert.Empty(deleted);
            Assert.Contains(logLines, l => l.Contains("[SceneExit]") && l.Contains("not dropped here"));
        }

        [Theory]
        [InlineData(true, false, false, "rewinding")]
        [InlineData(false, true, false, "refly-marker-active")]
        [InlineData(false, false, true, "merge-journal-active")]
        public void DroppedFlight_RewindReFlyOrJournal_DeletesNothing(
            bool rewinding, bool reFly, bool journal, string reason)
        {
            NoSaveSceneExit.NoteStashedTree("qws", "career");
            NoSaveSceneExit.CaptureDroppedFlight(TwoRecordingTree("qws"), "career");

            NoSaveSceneExit.ReapDroppedFlightSidecars(rewinding, reFly, journal);

            Assert.Empty(deleted);
            Assert.Contains(logLines, l => l.Contains("[DiscardReap]")
                && l.Contains("skipped context='quit-without-saving'")
                && l.Contains("reason=" + reason));
        }

        [Fact]
        public void SaveScopedReset_CapturesTheQuitWithoutSavingStashBeforeDroppingIt()
        {
            NoSaveSceneExit.SaveFolderForTesting = () => "career";
            PendingTreeSlot.StashPendingTree(TwoRecordingTree("qws"), PendingTreeState.Limbo);
            NoSaveSceneExit.NoteStashedTree("qws", NoSaveSceneExit.CurrentSaveFolder());

            ParsekScenario.ResetSaveScopedSessionState("test");
            Assert.False(PendingTreeSlot.HasPendingTree);

            NoSaveSceneExit.ReapDroppedFlightSidecars(false, false, false);
            Assert.Equal(2, deleted.Count);
            Assert.Contains("qws_root", deleted);
            Assert.Contains("qws_child", deleted);
        }
    }
}
