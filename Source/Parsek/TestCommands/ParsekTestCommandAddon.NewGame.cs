using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin Unity applier for the two-phase <c>NewGame</c> verb. Every decision is in the
    /// pure <see cref="TestCommandNewGame"/>; this file samples live state, runs stock's own
    /// quit-to-main-menu and new-game calls in the order stock runs them, and polls the settled
    /// scenes.
    ///
    /// <para>The addon itself survives both scene loads: it is a
    /// <c>KSPAddon(Startup.Instantly, once: true)</c> object that calls
    /// <c>DontDestroyOnLoad</c> in <c>Awake</c>, and its pump runs at any settled non-LOADING
    /// scene, the main menu included (the <c>LoadGame</c> boot verb runs from there). The
    /// two-phase state below lives on the addon, so it crosses the main menu with it.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private sealed class NewGamePending
        {
            internal NewGameMode Mode;
            internal NewGamePreset Preset;
            internal string Save;
            internal NewGamePhase Phase;
            internal int MenuPolls;
            internal int ReadyPolls;
            internal string FlagUrl;
        }

        private NewGamePending newGamePending;

        private void ResetNewGameState() => newGamePending = null;

        private static Game.Modes ToStockMode(NewGameMode mode)
        {
            switch (mode)
            {
                case NewGameMode.Career: return Game.Modes.CAREER;
                case NewGameMode.ScienceSandbox: return Game.Modes.SCIENCE_SANDBOX;
                default: return Game.Modes.SANDBOX;
            }
        }

        private static GameParameters.Preset ToStockPreset(NewGamePreset preset)
        {
            switch (preset)
            {
                case NewGamePreset.Easy: return GameParameters.Preset.Easy;
                case NewGamePreset.Moderate: return GameParameters.Preset.Moderate;
                case NewGamePreset.Hard: return GameParameters.Preset.Hard;
                default: return GameParameters.Preset.Normal;
            }
        }

        // ----- NewGame (two-phase) -----

        private void NewGameImpl(ParsedCommand cmd)
        {
            // GUI state gallery: the SaveGame / LoadGame rule, uniform across every verb that
            // saves or loads (a mock scope cannot survive the scene loads this verb drives).
            if (Parsek.UI.Gallery.GuiMockSession.IsLive)
            {
                ParsekLog.Warn(Tag,
                    "newgame refused reason=" + TestCommandSaveGame.RefusedGuiMockReason
                    + " mockState=" + Parsek.UI.Gallery.GuiMockSession.StateId);
                SetExecResult("REJECTED", null,
                    TestCommandSaveGame.RefusedGuiMockReason
                    + " mockState=" + Parsek.UI.Gallery.GuiMockSession.StateId
                    + " (clear it with UiAction op=mock mockState=none first)");
                return;
            }

            if (!TestCommandNewGame.TryParse(
                    ArgOrNull(cmd, TestCommandNewGame.ModeArg),
                    ArgOrNull(cmd, TestCommandNewGame.PresetArg),
                    ArgOrNull(cmd, TestCommandNewGame.SaveArg),
                    out NewGameMode mode, out NewGamePreset preset, out string save,
                    out string parseReject, out string parseDetail))
            {
                ParsekLog.Warn(Tag, $"newgame rejected reason={parseReject} {parseDetail}");
                SetExecResult("REJECTED", null, parseReject + " " + parseDetail);
                return;
            }

            string asReject = TestCommandNewGame.ParseAs(
                ArgOrNull(cmd, TestCommandNewGame.AsArg), save, out string target);
            if (asReject != null)
            {
                ParsekLog.Warn(Tag, $"newgame rejected reason={asReject} as={ArgOrNull(cmd, TestCommandNewGame.AsArg)} save={save}");
                SetExecResult("REJECTED", null, asReject + " as=" + ArgOrNull(cmd, TestCommandNewGame.AsArg));
                return;
            }
            bool switchesSave = !string.Equals(target, save, StringComparison.Ordinal);

            ParsekScenario scenario = ParsekScenario.Instance;
            ParsekFlight flight = ParsekFlight.Instance;
            string sceneName = HighLogic.LoadedScene.ToString();
            string loadedSave = HighLogic.SaveFolder;
            bool canLeave = HighLogic.CurrentGame != null
                && HighLogic.CurrentGame.Parameters != null
                && HighLogic.CurrentGame.Parameters.SpaceCenter.CanLeaveToMainMenu;
            bool recorderLive = ParsekFlight.HasLiveRecorderForTagging();
            bool hasActiveTree = flight != null && flight.HasActiveTree;
            bool hasPendingTree = RecordingStore.HasPendingTree;
            bool mergeDialogPending = ParsekScenario.MergeDialogPending;
            bool reFlySession = scenario != null && scenario.ActiveReFlySessionMarker != null;
            bool switchSession = scenario != null && scenario.ActiveSwitchSegmentSession != null;
            bool mergeJournal = scenario != null && scenario.ActiveMergeJournal != null;

            string gate = TestCommandNewGame.DecideGate(
                MapScene(HighLogic.LoadedScene), loadedSave, save, canLeave, recorderLive,
                hasActiveTree, hasPendingTree, mergeDialogPending, reFlySession, switchSession,
                mergeJournal);
            if (gate != null)
            {
                string why = "scene=" + sceneName + " save=" + save + " loadedSave=" + (loadedSave ?? string.Empty)
                    + " canLeaveToMainMenu=" + Bool(canLeave) + " recorderLive=" + Bool(recorderLive)
                    + " activeTree=" + Bool(hasActiveTree) + " pendingTree=" + Bool(hasPendingTree)
                    + " mergeDialogPending=" + Bool(mergeDialogPending) + " reFlySession=" + Bool(reFlySession)
                    + " switchSegmentSession=" + Bool(switchSession) + " mergeJournal=" + Bool(mergeJournal);
                ParsekLog.Warn(Tag, $"newgame rejected reason={gate} {why}");
                SetExecResult("REJECTED", null, gate + " scene=" + sceneName);
                return;
            }

            ParsekLog.Info(Tag, TestCommandNewGame.FormatStartLine(mode, preset, save, sceneName,
                switchesSave ? target : null));

            // Stock's Space Center "Quit to Main Menu" (KSCPauseMenu): save persistent, the
            // analytics close call (internal to Assembly-CSharp, so not callable here; it has
            // no gameplay effect), then the MAINMENU load. The interceptor's LoadScene prefix
            // passes a request made outside FLIGHT straight through.
            string saved = null;
            try
            {
                saved = GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"newgame quit-save threw {ex.GetType().Name}: {ex.Message} - continuing, the save is replaced anyway");
            }
            ParsekLog.Info(Tag,
                $"newgame quit to main menu: persisted={Bool(!string.IsNullOrEmpty(saved))} save={HighLogic.SaveFolder} " +
                "analytics=skipped(internal) - requesting MAINMENU");

            newGamePending = new NewGamePending
            {
                Mode = mode,
                Preset = preset,
                // as=: the game is created (and its folder overwritten) in the other folder;
                // the loaded save was persisted above and is left as it is.
                Save = target,
                Phase = NewGamePhase.Exiting,
            };
            HighLogic.LoadScene(GameScenes.MAINMENU);
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteNewGame(double now)
        {
            NewGamePending p = newGamePending;
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds(TestCommandNewGame.Verb);
            bool expired = DeferralBudget.ShouldTimeout(completionStartedAt, now, budget);
            if (p == null)
            {
                FinishNewGame("ERROR", null, TestCommandNewGame.StartTimeoutReason + " no-pending-state", elapsed);
                return;
            }

            TestCommandScene scene = MapScene(HighLogic.LoadedScene);
            Game game = HighLogic.CurrentGame;

            bool mainMenuUp = false;
            bool scenarioPresent = false, saveMatches = false, modeMatches = false, presetMatches = false,
                poolsReady = false;
            if (p.Phase == NewGamePhase.Exiting)
            {
                mainMenuUp = HighLogic.LoadedScene == GameScenes.MAINMENU
                    && game == null && Object.FindObjectOfType<MainMenu>() != null;
                if (mainMenuUp) p.MenuPolls++;
            }
            else
            {
                scenarioPresent = ParsekScenario.Instance != null;
                saveMatches = string.Equals(HighLogic.SaveFolder, p.Save, StringComparison.Ordinal);
                modeMatches = game != null
                    && TestCommandNewGame.ModeFromStockName(game.Mode.ToString()) == p.Mode;
                presetMatches = game != null && game.Parameters != null
                    && TestCommandNewGame.PresetFromStockName(game.Parameters.preset.ToString()) == p.Preset;
                poolsReady = TestCommandNewGame.PoolsReady(p.Mode, Funding.Instance != null,
                    ResearchAndDevelopment.Instance != null, Reputation.Instance != null);
                bool allReady = scene == TestCommandScene.SpaceCenter && game != null && scenarioPresent
                    && saveMatches && modeMatches && presetMatches && poolsReady;
                p.ReadyPolls = allReady ? p.ReadyPolls + 1 : 0;
            }

            NewGamePollOutcome outcome = TestCommandNewGame.DecidePoll(
                p.Phase, scene, mainMenuUp, p.MenuPolls, game != null, scenarioPresent, saveMatches,
                modeMatches, presetMatches, poolsReady, p.ReadyPolls, expired);

            switch (outcome)
            {
                case NewGamePollOutcome.NotYet:
                    // A stalled exit or start must be readable from the log without a re-run.
                    ParsekLog.VerboseRateLimited(Tag, "newgame-wait",
                        $"newgame wait phase={p.Phase} elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s " +
                        $"scene={HighLogic.LoadedScene} gameLoaded={Bool(game != null)} mainMenuUp={Bool(mainMenuUp)} " +
                        $"menuPolls={Int(p.MenuPolls)} scenario={Bool(scenarioPresent)} saveMatches={Bool(saveMatches)} " +
                        $"modeMatches={Bool(modeMatches)} presetMatches={Bool(presetMatches)} pools={Bool(poolsReady)} " +
                        $"readyPolls={Int(p.ReadyPolls)}");
                    return;
                case NewGamePollOutcome.CreateNow:
                    CreateAndStartNewGame(p, elapsed);
                    return;
                case NewGamePollOutcome.Ready:
                    FinishNewGame("OK", p, null, elapsed);
                    return;
                case NewGamePollOutcome.Mismatch:
                    FinishNewGame("ERROR", p, TestCommandNewGame.MismatchReason
                        + " mode=" + (game != null ? game.Mode.ToString() : "-")
                        + " preset=" + (game != null && game.Parameters != null ? game.Parameters.preset.ToString() : "-")
                        + " expectedMode=" + TestCommandNewGame.ModeToken(p.Mode)
                        + " expectedPreset=" + TestCommandNewGame.PresetToken(p.Preset), elapsed);
                    return;
                case NewGamePollOutcome.ReturnedToMenu:
                    FinishNewGame("ERROR", p, TestCommandNewGame.ReturnedToMenuReason, elapsed);
                    return;
                case NewGamePollOutcome.ExitTimedOut:
                    TestCommandDiagnostics.Timeout(completionId, completionVerb, elapsed, TestCommandNewGame.ExitTimeoutReason);
                    FinishNewGame("ERROR", p, TestCommandNewGame.ExitTimeoutReason
                        + " scene=" + HighLogic.LoadedScene + " gameLoaded=" + Bool(game != null)
                        + " menuPolls=" + Int(p.MenuPolls), elapsed);
                    return;
                default:
                    TestCommandDiagnostics.Timeout(completionId, completionVerb, elapsed, TestCommandNewGame.StartTimeoutReason);
                    FinishNewGame("ERROR", p, TestCommandNewGame.StartTimeoutReason
                        + " scene=" + HighLogic.LoadedScene + " gameLoaded=" + Bool(game != null)
                        + " scenario=" + Bool(scenarioPresent) + " saveMatches=" + Bool(saveMatches)
                        + " pools=" + Bool(poolsReady) + " readyPolls=" + Int(p.ReadyPolls), elapsed);
                    return;
            }
        }

        // At the settled main menu: stock's overwrite (delete saves/<save>, keeping Ships/) and
        // then MainMenu.CreateAndStartGame's four calls, with the menu's own default flag and
        // GameParameters.GetDefaultParameters(mode, preset).
        private void CreateAndStartNewGame(NewGamePending p, double elapsed)
        {
            string root = KSPUtil.ApplicationRootPath;
            string saveDir = Path.Combine(Path.Combine(root, "saves"), p.Save);
            int deletedFiles = 0, deletedDirs = 0;
            bool preserved = false;
            try
            {
                DeleteSaveLikeStockOverwrite(saveDir, ref deletedFiles, ref deletedDirs, ref preserved);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag, $"newgame delete failed path={saveDir} deletedFiles={Int(deletedFiles)} " +
                    $"deletedDirs={Int(deletedDirs)}: {ex.GetType().Name}: {ex.Message}");
                FinishNewGame("ERROR", p, TestCommandNewGame.DeleteFailedReason + " " + ex.GetType().Name, elapsed);
                return;
            }

            MainMenu menu = Object.FindObjectOfType<MainMenu>();
            p.FlagUrl = TestCommandNewGame.ResolveFlagUrl(menu != null ? menu.DefaultFlagURL : null);
            ParsekLog.Info(Tag, TestCommandNewGame.FormatCreateLine(
                p.Mode, p.Preset, p.Save, p.FlagUrl, deletedFiles, deletedDirs, preserved));

            try
            {
                Game.Modes stockMode = ToStockMode(p.Mode);
                GameParameters parameters = GameParameters.GetDefaultParameters(stockMode, ToStockPreset(p.Preset));
                HighLogic.CurrentGame = GamePersistence.CreateNewGame(
                    p.Save, stockMode, parameters, p.FlagUrl, GameScenes.SPACECENTER, EditorFacility.None);
                if (HighLogic.CurrentGame == null)
                {
                    FinishNewGame("ERROR", p, TestCommandNewGame.CreateFailedReason + " null-game", elapsed);
                    return;
                }
                // AnalyticsUtil.LogSaveGameCreated is internal to Assembly-CSharp (analytics
                // only); the two gameplay calls follow in stock's order.
                GameEvents.onGameNewStart.Fire();
                ParsekLog.Info(Tag,
                    $"newgame created title='{HighLogic.CurrentGame.Title}' saveFolder={HighLogic.SaveFolder} " +
                    $"seed={HighLogic.CurrentGame.Seed.ToString(CultureInfo.InvariantCulture)} " +
                    "onGameNewStart=fired - starting the game (Game.Start -> SPACECENTER)");
                p.Phase = NewGamePhase.Starting;
                p.ReadyPolls = 0;
                HighLogic.CurrentGame.Start();
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag, $"newgame create failed save={p.Save}: {ex.GetType().Name}: {ex.Message}");
                FinishNewGame("ERROR", p, TestCommandNewGame.CreateFailedReason + " " + ex.GetType().Name, elapsed);
            }
        }

        // MainMenu.DeleteDirectory's shape (clear attributes, delete files, recurse into
        // directories, remove the directory), except that the top-level Ships/ folder is kept
        // (TestCommandNewGame's class remarks say why).
        private static void DeleteSaveLikeStockOverwrite(string saveDir, ref int files, ref int dirs,
            ref bool preserved)
        {
            if (!Directory.Exists(saveDir)) return;
            File.SetAttributes(saveDir, FileAttributes.Normal);
            foreach (string file in Directory.GetFiles(saveDir))
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                files++;
            }
            foreach (string dir in Directory.GetDirectories(saveDir))
            {
                if (TestCommandNewGame.ShouldPreserveSaveEntry(Path.GetFileName(dir)))
                {
                    preserved = true;
                    continue;
                }
                DeleteTreeLikeStock(dir, ref files, ref dirs);
            }
            if (!preserved)
            {
                Directory.Delete(saveDir, false);
                dirs++;
            }
        }

        private static void DeleteTreeLikeStock(string dir, ref int files, ref int dirs)
        {
            File.SetAttributes(dir, FileAttributes.Normal);
            foreach (string file in Directory.GetFiles(dir))
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                files++;
            }
            foreach (string sub in Directory.GetDirectories(dir))
                DeleteTreeLikeStock(sub, ref files, ref dirs);
            Directory.Delete(dir, false);
            dirs++;
        }

        private void FinishNewGame(string verdict, NewGamePending p, string msg, double elapsed)
        {
            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            newGamePending = null;
            ClearTwoPhase();
            if (verdict != "OK" || p == null)
            {
                ParsekLog.Error(Tag, $"newgame error {msg} elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null, msg, dequeueHead: true);
                return;
            }

            Game game = HighLogic.CurrentGame;
            double? funds = Funding.Instance != null ? Funding.Instance.Funds : (double?)null;
            double? science = ResearchAndDevelopment.Instance != null
                ? (double)ResearchAndDevelopment.Instance.Science : (double?)null;
            double? rep = Reputation.Instance != null ? (double)Reputation.Instance.reputation : (double?)null;
            List<KeyValuePair<string, string>> payload = TestCommandNewGame.BuildCompletePayload(
                p.Mode, p.Preset, p.Save, HighLogic.LoadedScene.ToString(),
                game != null ? game.Seed : -1, funds, science, rep);
            ParsekLog.Info(Tag, TestCommandNewGame.FormatCompleteLine(payload, elapsed));
            EmitExecutedTerminal(id, seq, verb, "OK", payload, null, dequeueHead: true);
        }
    }
}
