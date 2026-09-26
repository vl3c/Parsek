using System;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>
    /// The one game-mode predicate: Parsek runs in SANDBOX, CAREER and SCIENCE_SANDBOX
    /// games and is inert in every other <see cref="Game.Modes"/> member - Making History
    /// missions (MISSION), the mission builder (MISSION_BUILDER) and stock training /
    /// scenario saves (SCENARIO, SCENARIO_NON_RESUMABLE) - plus any mode a future KSP adds
    /// (fail-closed). Owner ruling S9 (2026-09-26, todo KSP-SETTINGS-AUDIT-2026-09-26):
    /// scripted mission and scenario games own their own facility locks, recovery rules and
    /// end dialogs, so Parsek must not record, play ghosts, draw map presence, rewind,
    /// capture or patch the ledger, block stock controls, or show its toolbar button there.
    ///
    /// Stock never adds <see cref="ParsekScenario"/> to those games
    /// (<c>ScenarioCreationOptions.AddToAllGames</c> = 126 carries no mission flag, and
    /// <c>GamePersistence.CreateNewGame</c> / <c>UpdateScenarioModules</c> have no SCENARIO
    /// branch), but the KSPAddon controllers and the Harmony patches run in every game, and
    /// Parsek's static stores still hold the previously loaded career when a mission loads.
    /// Every entry point therefore reads this gate itself.
    ///
    /// The live read is a field read on <c>HighLogic.CurrentGame</c>, cheap enough for the
    /// per-physics-frame patch. No current game (main menu, xUnit) reads as NOT inert, so
    /// behavior there is unchanged.
    /// </summary>
    internal static class ParsekGameModeGate
    {
        internal const string Tag = "GameModeGate";

        /// <summary>Test seam: when set, replaces the live <c>HighLogic.CurrentGame.Mode</c> read.</summary>
        internal static Game.Modes? ModeOverrideForTesting;

        /// <summary>Test seam: when set, replaces the live <c>HighLogic.LoadedScene</c> read in the log key.</summary>
        internal static GameScenes? SceneOverrideForTesting;

        private static bool anyInertLogged;
        private static object lastLoggedGame;
        private static Game.Modes lastLoggedMode;
        private static GameScenes lastLoggedScene;

        /// <summary>True for the game modes Parsek runs in (SANDBOX, CAREER, SCIENCE_SANDBOX).</summary>
        internal static bool IsActiveMode(Game.Modes mode)
        {
            switch (mode)
            {
                case Game.Modes.SANDBOX:
                case Game.Modes.CAREER:
                case Game.Modes.SCIENCE_SANDBOX:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Human-readable reason Parsek is inert in <paramref name="mode"/>, or null when the
        /// mode is active.
        /// </summary>
        internal static string InertReason(Game.Modes mode)
        {
            switch (mode)
            {
                case Game.Modes.SANDBOX:
                case Game.Modes.CAREER:
                case Game.Modes.SCIENCE_SANDBOX:
                    return null;
                case Game.Modes.MISSION:
                    return "Making History mission";
                case Game.Modes.MISSION_BUILDER:
                    return "Making History mission builder";
                case Game.Modes.SCENARIO:
                    return "stock scenario save";
                case Game.Modes.SCENARIO_NON_RESUMABLE:
                    return "stock training / non-resumable scenario";
                default:
                    return "unknown game mode";
            }
        }

        /// <summary>
        /// Pure log-line builder for the one Info line an inert scene or load writes.
        /// </summary>
        internal static string FormatInertLogLine(Game.Modes mode, GameScenes scene, string site)
        {
            return "Parsek inert: game mode " + mode + " (" + InertReason(mode) + ")"
                + " scene=" + scene
                + " site=" + (string.IsNullOrEmpty(site) ? "?" : site)
                + " - no recording, ghost playback, map presence, rewind / re-fly,"
                + " ledger capture or patching, stock-control blocks or toolbar button"
                + " in this game";
        }

        /// <summary>
        /// Live gate without logging, for sites that read it more than once per frame
        /// after an entry point already logged. Null current game reads as not inert.
        /// </summary>
        internal static bool IsInertForCurrentGame
        {
            get
            {
                Game.Modes mode;
                object gameToken;
                if (!TryReadCurrentMode(out mode, out gameToken))
                    return false;
                return !IsActiveMode(mode);
            }
        }

        /// <summary>
        /// The entry-point gate: true when the current game's mode is inert. The first
        /// caller per (game instance, mode, scene) writes one Info line naming the mode,
        /// the reason and the calling site; later callers in the same scene stay silent,
        /// so a per-frame patch can call this without log spam.
        /// </summary>
        internal static bool CheckInert(string site)
        {
            Game.Modes mode;
            object gameToken;
            if (!TryReadCurrentMode(out mode, out gameToken))
                return false;
            if (IsActiveMode(mode))
                return false;

            GameScenes scene = ReadLoadedScene();
            if (!anyInertLogged
                || !ReferenceEquals(gameToken, lastLoggedGame)
                || mode != lastLoggedMode
                || scene != lastLoggedScene)
            {
                anyInertLogged = true;
                lastLoggedGame = gameToken;
                lastLoggedMode = mode;
                lastLoggedScene = scene;
                ParsekLog.Info(Tag, FormatInertLogLine(mode, scene, site));
            }
            return true;
        }

        internal static void ResetForTesting()
        {
            ModeOverrideForTesting = null;
            SceneOverrideForTesting = null;
            anyInertLogged = false;
            lastLoggedGame = null;
            lastLoggedMode = default(Game.Modes);
            lastLoggedScene = default(GameScenes);
        }

        private static readonly object OverrideGameToken = new object();

        private static bool TryReadCurrentMode(out Game.Modes mode, out object gameToken)
        {
            if (ModeOverrideForTesting.HasValue)
            {
                mode = ModeOverrideForTesting.Value;
                gameToken = OverrideGameToken;
                return true;
            }
            try
            {
                return ReadLiveModeCore(out mode, out gameToken);
            }
            catch (Exception)
            {
                mode = default(Game.Modes);
                gameToken = null;
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ReadLiveModeCore(out Game.Modes mode, out object gameToken)
        {
            Game game = HighLogic.CurrentGame;
            if (game == null)
            {
                mode = default(Game.Modes);
                gameToken = null;
                return false;
            }
            mode = game.Mode;
            gameToken = game;
            return true;
        }

        private static GameScenes ReadLoadedScene()
        {
            if (SceneOverrideForTesting.HasValue)
                return SceneOverrideForTesting.Value;
            try
            {
                return ReadLiveSceneCore();
            }
            catch (Exception)
            {
                return default(GameScenes);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static GameScenes ReadLiveSceneCore()
        {
            return HighLogic.LoadedScene;
        }
    }
}
