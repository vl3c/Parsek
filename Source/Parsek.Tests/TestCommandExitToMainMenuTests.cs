using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;
using DialogVariant = Parsek.SceneExitInterceptor.DialogVariant;

namespace Parsek.Tests
{
    /// <summary>
    /// The <c>ExitToMainMenu</c> seam verb (the flight Esc-menu Quit to Main Menu): its wedge
    /// gate, completion poll, payload and dispatch gates. Pure cells only: the live predicates
    /// the applier samples are passed in as their results.
    /// </summary>
    public class TestCommandExitToMainMenuTests
    {
        private static ExitGateDecision Gate(
            bool hasActiveTree = false, bool session = false,
            DialogVariant active = DialogVariant.None, DialogVariant pending = DialogVariant.None,
            bool sessionFollowsAutoMerge = false, bool activeCommits = false, bool pendingCommits = false)
            => TestCommandExitToMainMenu.DecideExitGate(
                hasActiveTree, session, active, pending, sessionFollowsAutoMerge, activeCommits, pendingCommits);

        [Fact]
        public void Gate_NothingLive_Proceeds()
        {
            Assert.Equal(ExitGateDecision.Proceed, Gate());
        }

        [Fact]
        public void Gate_LiveTreeCommittedWithoutDialog_Proceeds()
        {
            // Auto-merge on, no Re-Fly: the matrix says RegularMerge for MAINMENU, and the
            // prefix's main-menu branch commits it without the dialog. The verb's subject.
            Assert.Equal(ExitGateDecision.Proceed,
                Gate(hasActiveTree: true, active: DialogVariant.RegularMerge, activeCommits: true));
        }

        [Fact]
        public void Gate_LiveTreeDialogShown_Refuses()
        {
            // Auto-merge off: the plain merge dialog would spawn and no verb answers it.
            Assert.Equal(ExitGateDecision.RefusedActiveTreeDialog,
                Gate(hasActiveTree: true, active: DialogVariant.RegularMerge, activeCommits: false));
        }

        [Fact]
        public void Gate_LiveReFly_Refuses()
        {
            // A Re-Fly keeps its dialog at the main menu too (ShouldCommitMainMenuExitWithoutDialog
            // is false for ReFlyAttempt); AnswerMergeDialog exit=mainMenu answers it instead.
            bool commits = SceneExitInterceptor.ShouldCommitMainMenuExitWithoutDialog(
                GameScenes.MAINMENU, DialogVariant.ReFlyAttempt, isAutoMerge: true, mergeJournalActive: false);
            Assert.False(commits);
            Assert.Equal(ExitGateDecision.RefusedActiveTreeDialog,
                Gate(hasActiveTree: true, active: DialogVariant.ReFlyAttempt, activeCommits: commits));
        }

        [Fact]
        public void Gate_PendingTreeCommittedWithoutDialog_Proceeds()
        {
            Assert.Equal(ExitGateDecision.Proceed,
                Gate(pending: DialogVariant.RegularMerge, pendingCommits: true));
        }

        [Fact]
        public void Gate_PendingTreeDialogShown_Refuses()
        {
            Assert.Equal(ExitGateDecision.RefusedPendingTreeDialog,
                Gate(pending: DialogVariant.RegularMerge, pendingCommits: false));
        }

        [Fact]
        public void Gate_SessionNotFollowingAutoMerge_Refuses_EvenWhenAPendingTreeWouldCommit()
        {
            // The prefix's session branch runs first and asks about the SESSION'S tree.
            Assert.Equal(ExitGateDecision.RefusedSwitchSegmentSession,
                Gate(session: true, pending: DialogVariant.RegularMerge, pendingCommits: true));
        }

        [Fact]
        public void Gate_SessionFollowingAutoMerge_FallsToThePendingBranch()
        {
            Assert.Equal(ExitGateDecision.Proceed,
                Gate(session: true, sessionFollowsAutoMerge: true,
                    pending: DialogVariant.RegularMerge, pendingCommits: true));
        }

        [Fact]
        public void Gate_ActiveCommitFlagNeverReleasesAPendingRefusal()
        {
            // Each branch reads its own commit predicate.
            Assert.Equal(ExitGateDecision.RefusedPendingTreeDialog,
                Gate(pending: DialogVariant.RegularMerge, activeCommits: true, pendingCommits: false));
        }

        [Fact]
        public void MainMenuUp_NeedsSceneNoGameAndTheMenu()
        {
            Assert.True(TestCommandExitToMainMenu.IsMainMenuUp(TestCommandScene.MainMenu, false, true));
            Assert.False(TestCommandExitToMainMenu.IsMainMenuUp(TestCommandScene.MainMenu, true, true));
            Assert.False(TestCommandExitToMainMenu.IsMainMenuUp(TestCommandScene.MainMenu, false, false));
            Assert.False(TestCommandExitToMainMenu.IsMainMenuUp(TestCommandScene.Loading, false, true));
            Assert.False(TestCommandExitToMainMenu.IsMainMenuUp(TestCommandScene.Flight, false, true));
        }

        [Fact]
        public void Completion_WaitsForTheSettlePolls()
        {
            int need = TestCommandExitToMainMenu.MinMenuSettlePolls;
            Assert.True(need > 1);
            Assert.Equal(MainMenuExitCompletion.StillWaiting,
                TestCommandExitToMainMenu.DecideCompletion(1.0, 120.0, need - 1));
            Assert.Equal(MainMenuExitCompletion.CompleteOk,
                TestCommandExitToMainMenu.DecideCompletion(1.0, 120.0, need));
        }

        [Fact]
        public void Completion_TimesOutAtTheBudget()
        {
            Assert.Equal(MainMenuExitCompletion.Timeout,
                TestCommandExitToMainMenu.DecideCompletion(120.0, 120.0, 0));
            Assert.Equal(MainMenuExitCompletion.StillWaiting,
                TestCommandExitToMainMenu.DecideCompletion(119.9, 120.0, 0));
        }

        [Fact]
        public void Completion_ASettledMenuWinsOverTheBudget()
        {
            Assert.Equal(MainMenuExitCompletion.CompleteOk,
                TestCommandExitToMainMenu.DecideCompletion(500.0, 120.0,
                    TestCommandExitToMainMenu.MinMenuSettlePolls));
        }

        [Fact]
        public void Payload_NamesTheSceneAndTheSave()
        {
            List<KeyValuePair<string, string>> p = TestCommandExitToMainMenu.BuildCompletePayload(true);
            Assert.Equal("MAINMENU", p.First(kv => kv.Key == "scene").Value);
            Assert.Equal("true", p.First(kv => kv.Key == "persisted").Value);
        }

        [Fact]
        public void Budget_IsTheExitToSpaceCenterSize()
        {
            Assert.Equal(120.0, DeferralBudget.BudgetSeconds(TestCommandExitToMainMenu.Verb));
        }

        [Fact]
        public void Dispatch_InFlight_Executes()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd(), new DispatchState { Scene = TestCommandScene.Flight, GameLoaded = true, SettingsPresent = true });
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void Dispatch_OutsideFlight_Defers()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd(), new DispatchState { Scene = TestCommandScene.SpaceCenter, GameLoaded = true, SettingsPresent = true });
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal("not-in-flight", r.Reason);
        }

        [Fact]
        public void Dispatch_LiveRecorder_StillExecutes()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd(),
                new DispatchState { Scene = TestCommandScene.Flight, GameLoaded = true, SettingsPresent = true, Recording = true, HasTree = true });
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void Dispatch_LoadInFlight_Rejects()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd(),
                new DispatchState { Scene = TestCommandScene.Flight, GameLoaded = true, SettingsPresent = true, LoadInFlight = true });
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("load-in-flight", r.Reason);
        }

        [Fact]
        public void Dispatch_MergeJournalInFlight_Rejects()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd(),
                new DispatchState { Scene = TestCommandScene.Flight, GameLoaded = true, SettingsPresent = true, MergeJournalInFlight = true });
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("merge-journal-in-flight", r.Reason);
        }

        private static ParsedCommand Cmd()
            => TestCommandParser.ParseLine("id=1 cmd=" + TestCommandExitToMainMenu.Verb, 1);
    }
}
