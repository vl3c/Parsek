using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;

namespace Parsek
{
    /// <summary>
    /// Creates and starts a new save the way stock's main menu does: the overwrite delete of
    /// <c>saves/&lt;folder&gt;</c> (<c>MainMenu.DeleteDirectory</c>'s shape), then
    /// <c>MainMenu.CreateAndStartGame</c>'s calls in stock's order
    /// (<c>GamePersistence.CreateNewGame</c>, <c>GameEvents.onGameNewStart</c>,
    /// <c>Game.Start</c>).
    ///
    /// <para>The steps are separate calls so a caller can run its own work on the returned
    /// game between <see cref="CreateGame"/> and <see cref="StartGame"/> (onGameNewStart has
    /// fired by then, as in stock). The <c>NewGame</c> seam verb runs no such step. Every
    /// decision (mode, preset, flag, the kept folder entry) belongs to the caller.</para>
    ///
    /// <para>The delete step names no game type, so xUnit drives it on a temp folder; every
    /// KSP read is in a <c>NoInlining</c> core.</para>
    ///
    /// <para>It also holds the load-and-start route, a saved game resumed at the Space Center
    /// the way stock's Load menu does it: <see cref="LoadSave"/>, <see cref="FirePlayerLoad"/>
    /// for a player's load, <see cref="PrepareSpaceCenterStart"/>, then <c>Game.Start</c>.
    /// These steps write no log line: the <c>LoadGame</c> seam verb (their caller, which loads
    /// before it picks a route and logs between the steps) owns lines the harness pins
    /// byte for byte, so each caller names its own load.</para>
    /// </summary>
    internal static class NewSaveStarter
    {
        private const string Tag = "NewSave";

        /// <summary>What the delete step removed. The caller allocates it so the counts reached
        /// before an exception stay readable when the delete throws.</summary>
        internal sealed class DeleteResult
        {
            internal int Files;
            internal int Dirs;
            internal bool Preserved;
        }

        /// <summary><c>saves/&lt;saveFolder&gt;</c> under the KSP root.</summary>
        internal static string ResolveSaveDirectory(string saveFolder)
            => Path.Combine(Path.Combine(ReadApplicationRootPathCore(), "saves"), saveFolder);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string ReadApplicationRootPathCore() => KSPUtil.ApplicationRootPath;

        /// <summary>
        /// Stock's overwrite delete (clear attributes, delete files, recurse into directories,
        /// remove the directory), except that a top-level directory the predicate keeps is left
        /// whole, and then the save folder itself stays. A missing folder is a no-op. Exceptions
        /// propagate; <paramref name="counts"/> holds what was removed before the throw.
        /// </summary>
        internal static void DeleteSaveFolderLikeStockOverwrite(string saveDir,
            Func<string, bool> preserveTopLevelEntry, DeleteResult counts)
        {
            if (counts == null) throw new ArgumentNullException(nameof(counts));
            if (!Directory.Exists(saveDir))
            {
                ParsekLog.Info(Tag, "delete skipped: no folder path=" + saveDir);
                return;
            }
            File.SetAttributes(saveDir, FileAttributes.Normal);
            foreach (string file in Directory.GetFiles(saveDir))
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                counts.Files++;
            }
            foreach (string dir in Directory.GetDirectories(saveDir))
            {
                if (preserveTopLevelEntry != null && preserveTopLevelEntry(Path.GetFileName(dir)))
                {
                    counts.Preserved = true;
                    continue;
                }
                DeleteTreeLikeStock(dir, counts);
            }
            if (!counts.Preserved)
            {
                Directory.Delete(saveDir, false);
                counts.Dirs++;
            }
            ParsekLog.Info(Tag, "deleted save folder path=" + saveDir
                + " files=" + counts.Files.ToString(CultureInfo.InvariantCulture)
                + " dirs=" + counts.Dirs.ToString(CultureInfo.InvariantCulture)
                + " preserved=" + (counts.Preserved ? "true" : "false"));
        }

        private static void DeleteTreeLikeStock(string dir, DeleteResult counts)
        {
            File.SetAttributes(dir, FileAttributes.Normal);
            foreach (string file in Directory.GetFiles(dir))
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                counts.Files++;
            }
            foreach (string sub in Directory.GetDirectories(dir))
                DeleteTreeLikeStock(sub, counts);
            Directory.Delete(dir, false);
            counts.Dirs++;
        }

        /// <summary>
        /// <c>GamePersistence.CreateNewGame</c> into <c>HighLogic.CurrentGame</c> (Space Center
        /// start, no editor), then <c>onGameNewStart</c> when a game came back. Returns the game,
        /// or null when stock created none. Stock's analytics call between the two is internal
        /// to Assembly-CSharp and has no gameplay effect, so it is not made.
        /// </summary>
        internal static Game CreateGame(string saveFolder, Game.Modes mode, GameParameters parameters,
            string flagUrl)
        {
            HighLogic.CurrentGame = GamePersistence.CreateNewGame(
                saveFolder, mode, parameters, flagUrl, GameScenes.SPACECENTER, EditorFacility.None);
            Game game = HighLogic.CurrentGame;
            if (game == null)
            {
                ParsekLog.Warn(Tag, "create returned no game folder=" + saveFolder + " mode=" + mode);
                return null;
            }
            GameEvents.onGameNewStart.Fire();
            ParsekLog.Info(Tag, "created game title='" + game.Title + "' mode=" + game.Mode
                + " folder=" + saveFolder + " onGameNewStart=fired");
            return game;
        }

        /// <summary><c>Game.Start</c>: the Space Center load of the created game.</summary>
        internal static void StartGame(Game game)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            ParsekLog.Info(Tag, "start requested title='" + game.Title + "' (Game.Start -> SPACECENTER)");
            game.Start();
        }

        /// <summary>
        /// Reads <c>saves/&lt;saveFolder&gt;/&lt;slotName&gt;.sfs</c>: points
        /// <c>HighLogic.SaveFolder</c> at the folder when one is named, then
        /// <c>GamePersistence.LoadGame(slotName, saveFolder, true, false)</c> (null when the
        /// save is missing or incompatible, no stock popup). Exceptions propagate. Nothing is
        /// adopted or started: the caller picks the route from the returned game.
        /// </summary>
        internal static Game LoadSave(string saveFolder, string slotName)
        {
            if (!string.IsNullOrEmpty(saveFolder))
                HighLogic.SaveFolder = saveFolder;
            return GamePersistence.LoadGame(slotName, saveFolder, true, false);
        }

        /// <summary>
        /// Announces a player's load of a save file the way stock's own load paths do (F9, the
        /// pause-menu and main-menu Load fire <c>onGameStatePostLoad</c> with the loaded game's
        /// node after a compatible load and before the scene request);
        /// <c>GamePersistence.LoadGame</c> fires nothing. Parsek's save-file load intent arms
        /// on it, so the next OnLoad classifies the load as a save-file load.
        /// </summary>
        internal static void FirePlayerLoad(Game game)
        {
            GameEvents.onGameStatePostLoad.Fire(game.config);
        }

        /// <summary>
        /// Readies a loaded game for <c>Game.Start</c> at the Space Center exactly as the stock
        /// Load-menu resume does (decompiled <c>MainMenu.OnLoadDialogPipelineFinished</c>, KSP
        /// 1.12.5): adopt it as <c>HighLogic.CurrentGame</c>, point <c>startScene</c> at the
        /// KSC, re-add the <c>AddToAllGames</c> scenario modules (ParsekScenario) the save lacks,
        /// and write the result to <c>persistent.sfs</c>. Returns <c>SaveGame</c>'s path, empty
        /// when nothing was written. The caller then calls <c>game.Start()</c>.
        ///
        /// <para>The write is load-bearing: <c>Game.Start</c> routes through
        /// <c>HighLogic.LoadScene(SPACECENTER)</c>, and <c>SpaceCenterMain.Start</c> re-reads
        /// <c>persistent.sfs</c> from disk and runs <c>ScenarioRunner.SetProtoModules</c> on
        /// THAT game, never on the in-memory one. Without the write a save with no ParsekScenario
        /// node (the fresh career fixtures) boots with no ParsekScenario and OnLoad never
        /// runs.</para>
        /// </summary>
        internal static string PrepareSpaceCenterStart(Game game, string saveFolder)
        {
            HighLogic.CurrentGame = game;
            game.startScene = GameScenes.SPACECENTER;
            GamePersistence.UpdateScenarioModules(game);
            return GamePersistence.SaveGame(game, "persistent", saveFolder, SaveMode.OVERWRITE);
        }
    }
}
