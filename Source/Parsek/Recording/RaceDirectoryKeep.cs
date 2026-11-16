using System;
using System.IO;

namespace Parsek
{
    /// <summary>
    /// The race save's local directory <c>Parsek/Race/</c> (design-competitive-multiplayer.md
    /// 15.3, addition A7) holds the rival cache, the witness log and the own mirror: files no
    /// save names and no test batch made. The in-game test batch snapshot / restore and the
    /// test-batch crash reconcile replace the whole <c>Parsek/</c> folder, so they leave this
    /// one subfolder out of every copy and move the live one aside before the swap and back
    /// after it. A single-player save has no such folder: every member here is then a no-op
    /// and writes no log line.
    /// </summary>
    internal static class RaceDirectoryKeep
    {
        /// <summary>The race folder's name under the save's <c>Parsek/</c> folder, beside
        /// <c>Recordings</c>, <c>RewindPoints</c>, <c>Saves</c> and <c>GameState</c>.</summary>
        internal const string DirectoryName = "Race";

        private const string Tag = "TestBatch";
        private const string AsideInfix = "-race-keep-";

        /// <summary>True when a path relative to the <c>Parsek/</c> folder lies in its
        /// <c>Race/</c> subfolder (the folder itself included). Case-insensitive, because the
        /// Windows file system is.</summary>
        internal static bool IsInRaceDirectory(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                return false;
            string trimmed = relativePath.TrimStart(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            int sep = trimmed.IndexOfAny(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            string first = sep < 0 ? trimmed : trimmed.Substring(0, sep);
            return string.Equals(first, DirectoryName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Moves <c>&lt;parsekDirectory&gt;/Race/</c> to a sibling of
        /// <c>parsekDirectory</c> and returns that path, or null when there is no race folder.
        /// Throws when the move fails, so a caller that has not yet deleted anything stops
        /// with the live folder untouched.</summary>
        internal static string MoveAside(string parsekDirectory)
        {
            if (string.IsNullOrEmpty(parsekDirectory))
                return null;
            string raceDirectory = Path.Combine(parsekDirectory, DirectoryName);
            if (!Directory.Exists(raceDirectory))
                return null;

            string trimmed = parsekDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string parent = Path.GetDirectoryName(trimmed) ?? string.Empty;
            string aside = Path.Combine(parent,
                Path.GetFileName(trimmed) + AsideInfix + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.Move(raceDirectory, aside);
            ParsekLog.Verbose(Tag, $"moved Parsek/Race/ aside to '{aside}'");
            return aside;
        }

        /// <summary>Moves a folder <see cref="MoveAside"/> returned back to
        /// <c>&lt;parsekDirectory&gt;/Race/</c>. Null is a no-op success. Never overwrites: when
        /// a race folder is already there, or the move fails, the kept files stay at the aside
        /// path, a warning names it, and the result is false.</summary>
        internal static bool MoveBack(string parsekDirectory, string asideDirectory)
        {
            if (string.IsNullOrEmpty(asideDirectory))
                return true;
            try
            {
                if (!Directory.Exists(asideDirectory))
                {
                    ParsekLog.Warn(Tag, $"Parsek/Race/ aside folder '{asideDirectory}' is gone; nothing to move back");
                    return false;
                }
                int files = Directory.GetFiles(asideDirectory, "*", SearchOption.AllDirectories).Length;
                Directory.CreateDirectory(parsekDirectory);
                string raceDirectory = Path.Combine(parsekDirectory, DirectoryName);
                if (Directory.Exists(raceDirectory))
                {
                    ParsekLog.Warn(Tag,
                        $"Parsek/Race/ already exists after the restore; the live race folder stays at '{asideDirectory}'");
                    return false;
                }
                Directory.Move(asideDirectory, raceDirectory);
                ParsekLog.Info(Tag, $"kept Parsek/Race/ ({files} files)");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"Parsek/Race/ move back failed; the live race folder stays at '{asideDirectory}': {ex.Message}");
                return false;
            }
        }
    }
}
