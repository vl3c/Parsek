using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>The poll verdict of the two-phase <c>ExitToMainMenu</c> completion.</summary>
    internal enum MainMenuExitCompletion
    {
        /// <summary>The main menu is not up (or not yet stable): keep polling.</summary>
        StillWaiting,

        /// <summary>MAINMENU settled with no game loaded and the menu object present for
        /// <see cref="TestCommandExitToMainMenu.MinMenuSettlePolls"/> polls in a row.</summary>
        CompleteOk,

        /// <summary>The budget expired before the main menu settled.</summary>
        Timeout,
    }

    /// <summary>
    /// Pure decision + payload helpers for the two-phase <c>ExitToMainMenu</c> seam verb: the
    /// player's Esc-menu "Quit to Main Menu" out of FLIGHT. Stock's flight pause menu runs
    /// <c>saveAndExit(MAINMENU)</c> (decompiled <c>PauseMenu</c>, KSP 1.12.5:
    /// <c>onSceneConfirmExit</c>, <c>ClearpersistentIdDictionaries</c>, <c>SaveGame</c>,
    /// <c>LoadScene(MAINMENU)</c>), which is <c>SceneExitInterceptor.SafeWritePersistent</c>
    /// followed by <c>HighLogic.LoadScene</c>, the sequence <c>ExitToSpaceCenter</c> drives
    /// for the Space Center.
    ///
    /// <para>Its subject is the main-menu branch of Parsek's <c>LoadScene</c> prefix: under
    /// auto-merge a live or pending tree is committed WITHOUT the merge dialog and the save is
    /// written before the game unloads (<c>SceneExitInterceptor.ShouldCommitMainMenuExitWithoutDialog</c>,
    /// owner ruling 2026-11-01: leaving flight commits). Everything that would spawn a modal
    /// instead (a live Re-Fly, auto-merge off, a merge journal still finishing, an armed switch
    /// segment session) is refused before the exit, with <c>ExitToSpaceCenter</c>'s
    /// <c>dialog-required variant=</c> wording: no seam verb answers a whole-tree merge
    /// popup, and a live Re-Fly is concluded at the main menu by
    /// <c>AnswerMergeDialog exit=mainMenu</c>.</para>
    ///
    /// <para>The game unloads at the main menu, so a lane reads what was committed by loading
    /// the save back (<c>LoadGame</c> is the boot channel and runs from the main menu).</para>
    /// </summary>
    internal static class TestCommandExitToMainMenu
    {
        internal const string Verb = "ExitToMainMenu";

        /// <summary>Terminal ERROR when the main menu never settled inside the budget.</summary>
        internal const string TimeoutReason = "exittomainmenu-timeout";

        /// <summary>Terminal ERROR when the pre-exit save threw: Parsek's prefix hard-blocks a
        /// main-menu exit whose save failed, so the verb does not request the load.</summary>
        internal const string SaveFailedReason = "exittomainmenu-save-failed";

        /// <summary>Consecutive polls the main menu must hold (MAINMENU, no game, the menu
        /// object present) before the verb answers: <c>NewGame</c>'s exit settle.</summary>
        internal const int MinMenuSettlePolls = TestCommandNewGame.MinMenuSettlePolls;

        /// <summary>
        /// Decide whether a driven FLIGHT -> MAINMENU exit is safe to initiate. Same order as
        /// the prefix: <see cref="TestCommandExitToSpaceCenter.DecideExitGate"/> answers for the
        /// session, live-tree and pending-tree branches, and a refused live or pending tree is
        /// let through when the prefix commits it without the dialog
        /// (<paramref name="activeTreeCommitsWithoutDialog"/> /
        /// <paramref name="pendingTreeCommitsWithoutDialog"/>, each
        /// <c>ShouldCommitMainMenuExitWithoutDialog</c> for that branch's variant).
        /// Conservative for the same reason as the Space Center gate: the prefix's mutating
        /// fast paths are never probed.
        /// </summary>
        internal static ExitGateDecision DecideExitGate(
            bool hasActiveTree,
            bool switchSegmentSessionArmed,
            SceneExitInterceptor.DialogVariant activeTreeVariant,
            SceneExitInterceptor.DialogVariant pendingTreeVariant,
            bool sessionFollowsAutoMerge,
            bool activeTreeCommitsWithoutDialog,
            bool pendingTreeCommitsWithoutDialog)
        {
            ExitGateDecision gate = TestCommandExitToSpaceCenter.DecideExitGate(
                hasActiveTree, switchSegmentSessionArmed, activeTreeVariant, pendingTreeVariant,
                sessionFollowsAutoMerge);
            if (gate == ExitGateDecision.RefusedActiveTreeDialog && activeTreeCommitsWithoutDialog)
                return ExitGateDecision.Proceed;
            if (gate == ExitGateDecision.RefusedPendingTreeDialog && pendingTreeCommitsWithoutDialog)
                return ExitGateDecision.Proceed;
            return gate;
        }

        /// <summary>
        /// One completion poll. The main menu counts only once it held for
        /// <see cref="MinMenuSettlePolls"/> polls (<paramref name="menuPolls"/> is the count of
        /// consecutive polls that saw it); the budget is the catch-all for an exit a foreign
        /// prefix blocked or a modal held in FLIGHT.
        /// </summary>
        internal static MainMenuExitCompletion DecideCompletion(
            double elapsedSeconds, double budgetSeconds, int menuPolls)
        {
            if (menuPolls >= MinMenuSettlePolls)
                return MainMenuExitCompletion.CompleteOk;
            if (elapsedSeconds >= budgetSeconds)
                return MainMenuExitCompletion.Timeout;
            return MainMenuExitCompletion.StillWaiting;
        }

        /// <summary>Pure: whether this poll sees the main menu up (the <c>NewGame</c> exit
        /// predicate: MAINMENU, no game loaded, the menu object present).</summary>
        internal static bool IsMainMenuUp(TestCommandScene scene, bool gameLoaded, bool menuPresent)
            => scene == TestCommandScene.MainMenu && !gameLoaded && menuPresent;

        /// <summary>The terminal OK payload: the settled scene and whether the pre-exit save
        /// was written.</summary>
        internal static List<KeyValuePair<string, string>> BuildCompletePayload(bool persisted)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("scene", "MAINMENU"),
                new KeyValuePair<string, string>("persisted", persisted ? "true" : "false"),
            };
    }
}
