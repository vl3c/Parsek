using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>The stock game mode a <c>NewGame</c> call creates. The wire tokens are the
    /// names of stock's <c>Game.Modes</c> members, which the applier maps them to.</summary>
    internal enum NewGameMode
    {
        None = 0,
        ScienceSandbox,
        Career,
        Sandbox,
    }

    /// <summary>The stock difficulty preset a <c>NewGame</c> call creates the game with. The wire
    /// tokens are the names of stock's <c>GameParameters.Preset</c> members (Custom is not
    /// offered: it is the difficulty screen's hand-edited state, not a preset).</summary>
    internal enum NewGamePreset
    {
        None = 0,
        Easy,
        Normal,
        Moderate,
        Hard,
    }

    /// <summary>Where a started <c>NewGame</c> is.</summary>
    internal enum NewGamePhase
    {
        /// <summary>The Space Center's quit to the main menu was requested; the game has not
        /// been created yet.</summary>
        Exiting,

        /// <summary>The new game was created and started at the main menu; waiting for the
        /// Space Center to settle on it.</summary>
        Starting,
    }

    /// <summary>What one completion poll of a started <c>NewGame</c> concludes.</summary>
    internal enum NewGamePollOutcome
    {
        /// <summary>Keep polling.</summary>
        NotYet,

        /// <summary>The main menu is up and settled: delete the old save and create the new
        /// game now.</summary>
        CreateNow,

        /// <summary>The Space Center settled on the new game: terminal OK.</summary>
        Ready,

        /// <summary>The Space Center settled on a game whose mode or preset is not the one
        /// requested: terminal ERROR.</summary>
        Mismatch,

        /// <summary>The scene settled back at the main menu after the new game was started:
        /// terminal ERROR.</summary>
        ReturnedToMenu,

        /// <summary>The main menu was never reached within the budget: terminal ERROR.</summary>
        ExitTimedOut,

        /// <summary>The Space Center never settled on the new game within the budget: terminal
        /// ERROR.</summary>
        StartTimedOut,
    }

    /// <summary>
    /// Pure half of the automation-only <c>NewGame</c> seam verb (scripted campaign program
    /// stage CS-0): it replaces the loaded save with a GENUINE stock new game of the requested
    /// mode and difficulty preset, under the same save name, through the path a player takes
    /// (Space Center -> quit to the main menu -> New Game -> overwrite).
    ///
    /// <para><b>Stock ground truth (decompiled KSP 1.12.5).</b> The Space Center's pause-menu
    /// "Quit to Main Menu" (<c>KSCPauseMenu</c>) checks
    /// <c>Parameters.SpaceCenter.CanLeaveToMainMenu</c>, saves <c>persistent</c> and calls
    /// <c>HighLogic.LoadScene(MAINMENU)</c>. <c>MainMenu.Start</c> nulls
    /// <c>HighLogic.CurrentGame</c>. The New Game dialog's overwrite
    /// (<c>MainMenu.OverWriteNewGame</c>) deletes <c>saves/&lt;name&gt;</c>, and
    /// <c>MainMenu.CreateAndStartGame</c> runs
    /// <c>GamePersistence.CreateNewGame(name, mode, parameters, flagURL, SPACECENTER,
    /// EditorFacility.None)</c> (which creates the folder, the roster, the scenario set for
    /// the mode, and writes <c>persistent.sfs</c>), the analytics call,
    /// <c>GameEvents.onGameNewStart.Fire()</c> and <c>Game.Start()</c>, whose non-FLIGHT,
    /// non-EDITOR branch is <c>HighLogic.LoadScene(startScene)</c>. The parameters are
    /// <c>GameParameters.GetDefaultParameters(mode, preset)</c> and the flag is the menu's
    /// <c>DefaultFlagURL</c> unless the player picked another.</para>
    ///
    /// <para><b>One deliberate difference from stock's overwrite.</b> The delete keeps the
    /// save's <c>Ships/</c> folder: the harness stages the lane's craft there (the fixture's own
    /// craft and the shared craft library), and a later <c>GoToEditor craft=</c> reads it. A
    /// player who overwrites a save loses those craft; a lane would lose the craft it launches.
    /// <c>CreateNewGame</c> only creates <c>Ships/VAB</c> and <c>Ships/SPH</c>, so the kept
    /// folder is what a new game holds anyway, plus the craft.</para>
    ///
    /// <para><b>Grammar.</b> <c>cmd=NewGame mode=&lt;SCIENCE_SANDBOX|CAREER|SANDBOX&gt;
    /// preset=&lt;Easy|Normal|Moderate|Hard&gt; save=&lt;the loaded save folder&gt;</c>, all
    /// three required, case-sensitive, fail-closed. Issued at the Space Center only. Mirrored
    /// by <c>hlib.NEWGAME_*</c> (<c>NewGameSourceSyncTests</c>).</para>
    /// </summary>
    internal static class TestCommandNewGame
    {
        internal const string Verb = "NewGame";

        internal const string ModeArg = "mode";
        internal const string PresetArg = "preset";
        internal const string SaveArg = "save";

        /// <summary>Mode tokens, in <see cref="NewGameMode"/> order (stock <c>Game.Modes</c>
        /// names).</summary>
        internal static readonly string[] ModeTokens = { "SCIENCE_SANDBOX", "CAREER", "SANDBOX" };

        /// <summary>Preset tokens, in <see cref="NewGamePreset"/> order (stock
        /// <c>GameParameters.Preset</c> names).</summary>
        internal static readonly string[] PresetTokens = { "Easy", "Normal", "Moderate", "Hard" };

        /// <summary>The save sub-folder the delete keeps (see the class remarks).</summary>
        internal const string PreservedSaveEntry = "Ships";

        /// <summary>The stock default flag, used only when the main menu's own
        /// <c>DefaultFlagURL</c> cannot be read.</summary>
        internal const string FallbackFlagUrl = "Squad/Flags/default";

        /// <summary>Settled polls the main menu must have had before the game is created, so
        /// <c>MainMenu.Start</c> (which nulls the current game) has long run.</summary>
        internal const int MinMenuSettlePolls = 5;

        /// <summary>Consecutive polls the settled Space Center must hold the ready conjuncts
        /// before OK.</summary>
        internal const int MinReadyPolls = 3;

        // ---- REJECTED reasons (no side effect) ----
        internal const string ModeArgMissingReason = "newgame-mode-arg-missing";
        internal const string ModeArgInvalidReason = "newgame-mode-arg-invalid";
        internal const string PresetArgMissingReason = "newgame-preset-arg-missing";
        internal const string PresetArgInvalidReason = "newgame-preset-arg-invalid";
        internal const string SaveArgMissingReason = "newgame-save-arg-missing";
        internal const string SaveArgInvalidReason = "newgame-save-arg-invalid";
        internal const string WrongSceneReason = "newgame-wrong-scene";
        internal const string SaveMismatchReason = "newgame-save-mismatch";
        internal const string MainMenuLockedReason = "newgame-main-menu-locked";
        internal const string RecorderLiveReason = "newgame-recorder-live";
        internal const string ActiveTreeReason = "newgame-active-tree";
        internal const string PendingTreeReason = "newgame-pending-tree";
        internal const string MergeDialogPendingReason = "newgame-merge-dialog-pending";
        internal const string ReFlySessionReason = "newgame-refly-session";
        internal const string SwitchSegmentSessionReason = "newgame-switch-segment-session";
        internal const string MergeJournalReason = "newgame-merge-journal";

        // ---- ERROR reasons (the verb acted) ----
        internal const string ExitTimeoutReason = "newgame-exit-timeout";
        internal const string DeleteFailedReason = "newgame-delete-failed";
        internal const string CreateFailedReason = "newgame-create-failed";
        internal const string ReturnedToMenuReason = "newgame-returned-to-menu";
        internal const string MismatchReason = "newgame-mismatch";
        internal const string StartTimeoutReason = "newgame-start-timeout";

        /// <summary>The REJECTED vocabulary, args first and then in gate order.
        /// <c>hlib.NEWGAME_REASONS</c> mirrors it.</summary>
        internal static readonly string[] Reasons =
        {
            "newgame-mode-arg-missing",
            "newgame-mode-arg-invalid",
            "newgame-preset-arg-missing",
            "newgame-preset-arg-invalid",
            "newgame-save-arg-missing",
            "newgame-save-arg-invalid",
            "newgame-wrong-scene",
            "newgame-save-mismatch",
            "newgame-main-menu-locked",
            "newgame-recorder-live",
            "newgame-active-tree",
            "newgame-pending-tree",
            "newgame-merge-dialog-pending",
            "newgame-refly-session",
            "newgame-switch-segment-session",
            "newgame-merge-journal",
        };

        /// <summary>The ERROR vocabulary: the verb left the Space Center and the new game did
        /// not land. <c>hlib.NEWGAME_ERROR_REASONS</c> mirrors it.</summary>
        internal static readonly string[] ErrorReasons =
        {
            "newgame-exit-timeout",
            "newgame-delete-failed",
            "newgame-create-failed",
            "newgame-returned-to-menu",
            "newgame-mismatch",
            "newgame-start-timeout",
        };

        internal static string ModeToken(NewGameMode mode)
        {
            int i = (int)mode - 1;
            return i >= 0 && i < ModeTokens.Length ? ModeTokens[i] : "none";
        }

        internal static string PresetToken(NewGamePreset preset)
        {
            int i = (int)preset - 1;
            return i >= 0 && i < PresetTokens.Length ? PresetTokens[i] : "none";
        }

        /// <summary>The <see cref="NewGameMode"/> a stock <c>Game.Modes</c> member NAME maps to,
        /// <see cref="NewGameMode.None"/> for any other mode (MISSION, MISSION_BUILDER).</summary>
        internal static NewGameMode ModeFromStockName(string stockName)
        {
            int i = Array.IndexOf(ModeTokens, stockName);
            return i < 0 ? NewGameMode.None : (NewGameMode)(i + 1);
        }

        /// <summary>The <see cref="NewGamePreset"/> a stock <c>GameParameters.Preset</c> member
        /// NAME maps to, <see cref="NewGamePreset.None"/> for Custom or anything else.</summary>
        internal static NewGamePreset PresetFromStockName(string stockName)
        {
            int i = Array.IndexOf(PresetTokens, stockName);
            return i < 0 ? NewGamePreset.None : (NewGamePreset)(i + 1);
        }

        /// <summary>
        /// Parses the three required args. Fail-closed and case-sensitive like LoadGame's
        /// <c>scene=</c>: a missing arg and an unknown spelling are separate reasons, an empty
        /// value is a typo (invalid), and <c>save=</c> must be a bare save-folder name (see
        /// <see cref="IsSafeSaveName"/>) because the verb deletes <c>saves/&lt;save&gt;</c>.
        /// <paramref name="detail"/> carries the offending value and the valid set.
        /// </summary>
        internal static bool TryParse(
            string rawMode, string rawPreset, string rawSave,
            out NewGameMode mode, out NewGamePreset preset, out string save,
            out string rejectReason, out string detail)
        {
            mode = NewGameMode.None;
            preset = NewGamePreset.None;
            save = null;
            rejectReason = null;
            detail = null;

            if (rawMode == null)
            {
                rejectReason = ModeArgMissingReason;
                detail = "valid=" + string.Join(",", ModeTokens);
                return false;
            }
            int m = Array.IndexOf(ModeTokens, rawMode);
            if (m < 0)
            {
                rejectReason = ModeArgInvalidReason;
                detail = "mode=" + rawMode + " valid=" + string.Join(",", ModeTokens);
                return false;
            }

            if (rawPreset == null)
            {
                rejectReason = PresetArgMissingReason;
                detail = "valid=" + string.Join(",", PresetTokens);
                return false;
            }
            int p = Array.IndexOf(PresetTokens, rawPreset);
            if (p < 0)
            {
                rejectReason = PresetArgInvalidReason;
                detail = "preset=" + rawPreset + " valid=" + string.Join(",", PresetTokens);
                return false;
            }

            if (rawSave == null)
            {
                rejectReason = SaveArgMissingReason;
                detail = "save=(none)";
                return false;
            }
            if (!IsSafeSaveName(rawSave))
            {
                rejectReason = SaveArgInvalidReason;
                detail = "save=" + rawSave;
                return false;
            }

            mode = (NewGameMode)(m + 1);
            preset = (NewGamePreset)(p + 1);
            save = rawSave;
            return true;
        }

        /// <summary>True for a non-blank bare folder name: no directory part, no <c>..</c>, no
        /// character a file name may not hold, no leading or trailing space, and no leading
        /// <c>#</c> (<c>CreateNewGame</c> runs the name through <c>Localizer.Format</c>, which
        /// would read a <c>#autoLOC</c> tag and create a different folder).</summary>
        internal static bool IsSafeSaveName(string save)
        {
            if (string.IsNullOrEmpty(save) || save.Trim().Length == 0) return false;
            if (save != save.Trim()) return false;
            if (save.Contains("..") || save == ".") return false;
            if (save.StartsWith("#", StringComparison.Ordinal)) return false;
            if (save.IndexOf('/') >= 0 || save.IndexOf('\\') >= 0 || save.IndexOf(':') >= 0)
                return false;
            return save.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0;
        }

        /// <summary>
        /// The pre-exit gate, in the order the applier meets it: the scene (Space Center only),
        /// the save (the folder the lane loaded, so the produced-save harvest finds the new
        /// game), stock's own <c>CanLeaveToMainMenu</c> (the pause-menu button refuses with a
        /// screen message without it), and then every piece of Parsek state the main-menu
        /// transition would discard or settle behind a dialog: a live recorder, an active or
        /// pending tree, a merge dialog, a Re-Fly session, a switch-segment session, a merge
        /// journal mid-finalize. At the Space Center the scene-exit interceptor passes a
        /// MAINMENU request straight through (it only intercepts exits FROM FLIGHT), so none of
        /// these would raise a modal on the way out; they are refused because a new game must
        /// start from a quiescent old one, and each is a state the lane should have resolved
        /// (commit, discard, answer) before it. Returns null to proceed.
        /// </summary>
        internal static string DecideGate(
            TestCommandScene scene, string loadedSaveFolder, string requestedSave,
            bool canLeaveToMainMenu, bool recorderLive, bool hasActiveTree, bool hasPendingTree,
            bool mergeDialogPending, bool reFlySession, bool switchSegmentSession,
            bool mergeJournal)
        {
            if (scene != TestCommandScene.SpaceCenter) return WrongSceneReason;
            if (!string.Equals(loadedSaveFolder, requestedSave, StringComparison.Ordinal))
                return SaveMismatchReason;
            if (!canLeaveToMainMenu) return MainMenuLockedReason;
            if (recorderLive) return RecorderLiveReason;
            if (hasActiveTree) return ActiveTreeReason;
            if (hasPendingTree) return PendingTreeReason;
            if (mergeDialogPending) return MergeDialogPendingReason;
            if (reFlySession) return ReFlySessionReason;
            if (switchSegmentSession) return SwitchSegmentSessionReason;
            if (mergeJournal) return MergeJournalReason;
            return null;
        }

        /// <summary>True when the delete keeps the top-level save entry named
        /// <paramref name="entryName"/> (case-insensitive: the save may sit on a
        /// case-insensitive file system).</summary>
        internal static bool ShouldPreserveSaveEntry(string entryName)
            => string.Equals(entryName, PreservedSaveEntry, StringComparison.OrdinalIgnoreCase);

        /// <summary>The flag the new game carries: the main menu's <c>DefaultFlagURL</c> (what
        /// <c>MainMenu.Start</c> seeds the dialog with), else the stock default.</summary>
        internal static string ResolveFlagUrl(string menuDefaultFlagUrl)
            => string.IsNullOrEmpty(menuDefaultFlagUrl) ? FallbackFlagUrl : menuDefaultFlagUrl;

        /// <summary>The pools the mode keeps, live once the Space Center's scenario modules
        /// loaded: CAREER all three, SCIENCE_SANDBOX science only, SANDBOX none.</summary>
        internal static bool HasFunds(NewGameMode mode) => mode == NewGameMode.Career;

        internal static bool HasScience(NewGameMode mode)
            => mode == NewGameMode.Career || mode == NewGameMode.ScienceSandbox;

        internal static bool HasReputation(NewGameMode mode) => mode == NewGameMode.Career;

        /// <summary>True when every pool singleton the mode keeps is live.</summary>
        internal static bool PoolsReady(NewGameMode mode, bool fundingPresent, bool researchPresent,
            bool reputationPresent)
        {
            if (HasFunds(mode) && !fundingPresent) return false;
            if (HasScience(mode) && !researchPresent) return false;
            if (HasReputation(mode) && !reputationPresent) return false;
            return true;
        }

        /// <summary>
        /// Decide one completion poll. The pump polls only at settled scenes, and a scene load
        /// raises its transition flag synchronously when requested, so the first settled
        /// observation after either request is the destination scene.
        ///
        /// <para><see cref="NewGamePhase.Exiting"/>: the main menu must be up
        /// (<paramref name="mainMenuUp"/>: the menu object exists and <c>MainMenu.Start</c>
        /// nulled the current game) for <see cref="MinMenuSettlePolls"/> polls; the budget is
        /// the catch-all (a foreign prefix that swallowed the load leaves the Space Center
        /// up).</para>
        ///
        /// <para><see cref="NewGamePhase.Starting"/>: a main menu is the fast failure (the start
        /// did not leave it). The Space Center counts only with a game, Parsek's scenario, the
        /// requested save folder; a mode or preset other than the requested one is a mismatch;
        /// otherwise the mode's pools must be live for <see cref="MinReadyPolls"/> consecutive
        /// polls (<paramref name="readyPolls"/>, counted by the applier including this one).</para>
        /// </summary>
        internal static NewGamePollOutcome DecidePoll(
            NewGamePhase phase, TestCommandScene scene, bool mainMenuUp, int menuPolls,
            bool gameLoaded, bool scenarioPresent, bool saveFolderMatches, bool modeMatches,
            bool presetMatches, bool poolsReady, int readyPolls, bool budgetExpired)
        {
            if (phase == NewGamePhase.Exiting)
            {
                if (scene == TestCommandScene.MainMenu && mainMenuUp
                    && menuPolls >= MinMenuSettlePolls)
                    return NewGamePollOutcome.CreateNow;
                return budgetExpired ? NewGamePollOutcome.ExitTimedOut : NewGamePollOutcome.NotYet;
            }

            if (scene == TestCommandScene.MainMenu)
                return NewGamePollOutcome.ReturnedToMenu;
            if (scene == TestCommandScene.SpaceCenter && gameLoaded && scenarioPresent
                && saveFolderMatches)
            {
                if (!modeMatches || !presetMatches)
                    return NewGamePollOutcome.Mismatch;
                if (poolsReady && readyPolls >= MinReadyPolls)
                    return NewGamePollOutcome.Ready;
            }
            return budgetExpired ? NewGamePollOutcome.StartTimedOut : NewGamePollOutcome.NotYet;
        }

        /// <summary>Invariant round-trip text for a pool value a test or a log contract reads.</summary>
        internal static string FormatPool(double value)
            => value.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>
        /// The OK payload, in this order: <c>mode preset save scene seed</c>, then
        /// <c>funds</c> (CAREER), <c>science</c> (CAREER, SCIENCE_SANDBOX), <c>rep</c> (CAREER).
        /// A pool the mode keeps but that is null is written <c>-</c> (unreachable after the
        /// ready poll; kept so the key set depends on the mode alone).
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildCompletePayload(
            NewGameMode mode, NewGamePreset preset, string save, string scene, int seed,
            double? funds, double? science, double? reputation)
        {
            var list = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("mode", ModeToken(mode)),
                new KeyValuePair<string, string>("preset", PresetToken(preset)),
                new KeyValuePair<string, string>("save", save ?? string.Empty),
                new KeyValuePair<string, string>("scene", scene ?? string.Empty),
                new KeyValuePair<string, string>("seed", seed.ToString(CultureInfo.InvariantCulture)),
            };
            if (HasFunds(mode))
                list.Add(new KeyValuePair<string, string>("funds", funds.HasValue ? FormatPool(funds.Value) : "-"));
            if (HasScience(mode))
                list.Add(new KeyValuePair<string, string>("science", science.HasValue ? FormatPool(science.Value) : "-"));
            if (HasReputation(mode))
                list.Add(new KeyValuePair<string, string>("rep", reputation.HasValue ? FormatPool(reputation.Value) : "-"));
            return list;
        }

        /// <summary>The one Info line of an OK: <c>newgame complete</c> plus the payload as
        /// <c>key=value</c> and the elapsed wall time.</summary>
        internal static string FormatCompleteLine(
            List<KeyValuePair<string, string>> payload, double elapsedSeconds)
        {
            var sb = new System.Text.StringBuilder("newgame complete");
            if (payload != null)
                foreach (KeyValuePair<string, string> kv in payload)
                    sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
            sb.Append(" elapsed=").Append(elapsedSeconds.ToString("F1", CultureInfo.InvariantCulture)).Append('s');
            return sb.ToString();
        }

        /// <summary>The start line, written once the gate passed and before the exit.</summary>
        internal static string FormatStartLine(NewGameMode mode, NewGamePreset preset, string save,
            string scene)
            => "newgame start mode=" + ModeToken(mode) + " preset=" + PresetToken(preset)
               + " save=" + (save ?? string.Empty) + " scene=" + (scene ?? string.Empty)
               + " - quitting to the main menu the way the Space Center's pause menu does";

        /// <summary>The create line, written at the main menu just before the four stock
        /// new-game calls.</summary>
        internal static string FormatCreateLine(NewGameMode mode, NewGamePreset preset, string save,
            string flagUrl, int deletedFiles, int deletedDirs, bool preserved)
            => "newgame create mode=" + ModeToken(mode) + " preset=" + PresetToken(preset)
               + " save=" + (save ?? string.Empty) + " flag=" + (flagUrl ?? string.Empty)
               + " deletedFiles=" + deletedFiles.ToString(CultureInfo.InvariantCulture)
               + " deletedDirs=" + deletedDirs.ToString(CultureInfo.InvariantCulture)
               + " preserved=" + (preserved ? PreservedSaveEntry : "none");
    }
}
