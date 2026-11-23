using System.Collections.Generic;
using System.Globalization;
using Object = UnityEngine.Object;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin Unity applier for the two-phase <c>ExitToMainMenu</c> verb (the flight Esc-menu
    /// "Quit to Main Menu"). Every decision is in the pure <see cref="TestCommandExitToMainMenu"/>;
    /// this file samples live state, runs stock's <c>saveAndExit(MAINMENU)</c> sequence and polls
    /// for the settled main menu. The addon is <c>DontDestroyOnLoad</c> and its pump runs at the
    /// main menu (the <c>LoadGame</c> boot verb runs from there), so the two-phase state crosses
    /// the game unload with it.
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private int exitToMainMenuPolls;
        private bool exitToMainMenuPersisted;

        private void ExitToMainMenuImpl(ParsedCommand cmd)
        {
            // Dispatch already gated FLIGHT (RequiresFlight), load-in-flight and
            // merge-journal-in-flight.
            ParsekFlight flight = ParsekFlight.Instance;
            ParsekScenario scenario = ParsekScenario.Instance;

            bool hasActiveTree = flight != null && flight.HasActiveTree;
            bool sessionArmed = scenario != null && scenario.ActiveSwitchSegmentSession != null;
            bool reFlyLive = scenario != null && scenario.ActiveReFlySessionMarker != null;
            bool journalActive = scenario != null && scenario.ActiveMergeJournal != null;
            bool isAutoMerge = SceneExitInterceptor.IsAutoMergeForSceneExitLive(GameScenes.MAINMENU, "exittomainmenu");

            // The prefix's own live predicates for this destination, so the guard cannot drift
            // out of step with the interceptor.
            SceneExitInterceptor.DialogVariant activeTreeVariant =
                SceneExitInterceptor.ShouldShowDialogBeforeSceneChangeLive(GameScenes.MAINMENU, flight);
            SceneExitInterceptor.DialogVariant pendingTreeVariant =
                SceneExitInterceptor.ShouldShowPendingTreeDialogBeforeSceneChangeLive(GameScenes.MAINMENU);
            bool activeCommits = SceneExitInterceptor.ShouldCommitMainMenuExitWithoutDialog(
                GameScenes.MAINMENU, activeTreeVariant, isAutoMerge, journalActive);
            bool pendingCommits = SceneExitInterceptor.ShouldCommitMainMenuExitWithoutDialog(
                GameScenes.MAINMENU, pendingTreeVariant, isAutoMerge, journalActive);
            bool sessionFollowsAutoMerge = SceneExitInterceptor.ShouldSessionExitFollowAutoMerge(
                isAutoMerge, reFlyLive);

            ParsekLog.Info(Tag,
                $"exittomainmenu start scene={HighLogic.LoadedScene} autoMerge={Bool(isAutoMerge)} " +
                $"hasActiveTree={Bool(hasActiveTree)} hasPendingTree={Bool(PendingTreeSlot.HasPendingTree)} " +
                $"switchSegmentSession={Bool(sessionArmed)} reFly={Bool(reFlyLive)} " +
                $"activeTreeVariant={activeTreeVariant} pendingTreeVariant={pendingTreeVariant} " +
                $"activeCommitsWithoutDialog={Bool(activeCommits)} pendingCommitsWithoutDialog={Bool(pendingCommits)}");

            ExitGateDecision gate = TestCommandExitToMainMenu.DecideExitGate(
                hasActiveTree, sessionArmed, activeTreeVariant, pendingTreeVariant,
                sessionFollowsAutoMerge, activeCommits, pendingCommits);
            if (gate != ExitGateDecision.Proceed)
            {
                string variant = TestCommandExitToSpaceCenter.VariantToken(
                    gate, activeTreeVariant, pendingTreeVariant);
                string msg = TestCommandExitToSpaceCenter.RefusalMsg(variant);
                ParsekLog.Warn(Tag,
                    $"exittomainmenu refused reason={TestCommandExitToSpaceCenter.DialogRequiredReason} " +
                    $"variant={variant} gate={gate} - a merge modal would block the exit and no seam verb " +
                    "answers it here (a live Re-Fly: AnswerMergeDialog exit=mainMenu; otherwise set autoMerge=true)");
                SetExecResult("REJECTED", null, msg);
                return;
            }

            // Stock saveAndExit(MAINMENU): confirm-exit, pid dictionaries, SaveGame, then the
            // load. SafeWritePersistent returns false only for a MAINMENU save throw, the case
            // Parsek's own prefix hard-blocks, so the verb stops there instead of requesting a
            // load that would be refused.
            bool persisted = SceneExitInterceptor.SafeWritePersistent(GameScenes.MAINMENU);
            if (!persisted)
            {
                ParsekLog.Error(Tag,
                    $"exittomainmenu failed reason={TestCommandExitToMainMenu.SaveFailedReason} " +
                    "- the pre-exit save threw; the main-menu load is not requested");
                SetExecResult("ERROR", null, TestCommandExitToMainMenu.SaveFailedReason);
                return;
            }
            exitToMainMenuPolls = 0;
            exitToMainMenuPersisted = true;
            ParsekLog.Info(Tag,
                "exittomainmenu driving scene-exit dest=MAINMENU persisted=true " +
                "(stock Quit to Main Menu: confirm-exit, save, load) - the prefix commits a live or pending " +
                "tree without the dialog under auto-merge");
            HighLogic.LoadScene(GameScenes.MAINMENU);
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteExitToMainMenu(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds(TestCommandExitToMainMenu.Verb);
            TestCommandScene scene = MapScene(HighLogic.LoadedScene);
            bool gameLoaded = HighLogic.CurrentGame != null;
            bool menuPresent = scene == TestCommandScene.MainMenu && Object.FindObjectOfType<MainMenu>() != null;
            if (TestCommandExitToMainMenu.IsMainMenuUp(scene, gameLoaded, menuPresent))
                exitToMainMenuPolls++;
            else
                exitToMainMenuPolls = 0;

            MainMenuExitCompletion decision =
                TestCommandExitToMainMenu.DecideCompletion(elapsed, budget, exitToMainMenuPolls);
            if (decision == MainMenuExitCompletion.StillWaiting)
            {
                ParsekLog.VerboseRateLimited(Tag, "exittomainmenu-wait",
                    $"exittomainmenu wait elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s " +
                    $"scene={HighLogic.LoadedScene} gameLoaded={Bool(gameLoaded)} menuPresent={Bool(menuPresent)} " +
                    $"menuPolls={exitToMainMenuPolls.ToString(CultureInfo.InvariantCulture)}");
                return;
            }

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            bool persisted = exitToMainMenuPersisted;
            ClearTwoPhase();
            exitToMainMenuPolls = 0;
            exitToMainMenuPersisted = false;

            if (decision == MainMenuExitCompletion.CompleteOk)
            {
                List<KeyValuePair<string, string>> payload =
                    TestCommandExitToMainMenu.BuildCompletePayload(persisted);
                ParsekLog.Info(Tag,
                    $"exittomainmenu complete scene=MAINMENU game-loaded=false " +
                    $"elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
                EmitExecutedTerminal(id, seq, verb, "OK", payload, null, dequeueHead: true);
                return;
            }

            TestCommandDiagnostics.Timeout(id, verb, elapsed, TestCommandExitToMainMenu.TimeoutReason);
            ParsekLog.Error(Tag,
                $"exittomainmenu timeout scene={scene} gameLoaded={Bool(gameLoaded)} " +
                $"elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s - the main menu never settled " +
                "(a foreign LoadScene prefix, or a modal spawned after the gate passed)");
            EmitExecutedTerminal(id, seq, verb, "ERROR", null, TestCommandExitToMainMenu.TimeoutReason, dequeueHead: true);
        }
    }
}
