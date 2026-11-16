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
    }
}
