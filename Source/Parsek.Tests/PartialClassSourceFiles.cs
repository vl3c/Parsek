using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Parsek.Tests
{
    /// <summary>
    /// Shared resolver for the source readers that read a NAMED subset of a partial class's
    /// files (<see cref="BackgroundRecorderSourceFiles"/>,
    /// <see cref="GhostTrajectoryPolylineRendererSourceFiles"/>,
    /// <see cref="RecordingsTableUISourceFiles"/>, <see cref="ParsekFlightSourceFiles"/>,
    /// <see cref="ParsekScenarioSourceFiles"/>). It enumerates every
    /// <c>&lt;Class&gt;.*.cs</c> on disk and refuses when that set differs from the reader's
    /// declared lists, so a new partial file cannot be skipped silently by a gate that reads
    /// "the whole class": the failure names the file and tells the author which list to add it
    /// to.
    /// </summary>
    internal static class PartialClassSourceFiles
    {
        /// <summary>
        /// The main file, then <paramref name="readFileNames"/> in ordinal order, all under
        /// <paramref name="dir"/>. <paramref name="otherFileNames"/> are partial files that
        /// exist on disk but are deliberately NOT read.
        /// </summary>
        internal static IReadOnlyList<string> Paths(
            string dir,
            string mainFileName,
            IEnumerable<string> readFileNames,
            IEnumerable<string> otherFileNames,
            string readerName)
        {
            string main = Path.Combine(dir, mainFileName);
            if (!File.Exists(main))
                throw new FileNotFoundException(mainFileName + " not found", main);

            string stem = Path.GetFileNameWithoutExtension(mainFileName);
            var onDisk = new HashSet<string>(
                Directory.GetFiles(dir, stem + ".*.cs")
                    .Select(Path.GetFileName)
                    .Where(n => !string.Equals(n, mainFileName, StringComparison.Ordinal)),
                StringComparer.Ordinal);
            var read = readFileNames.ToList();
            var declared = new HashSet<string>(read.Concat(otherFileNames), StringComparer.Ordinal);

            var undeclared = onDisk.Where(n => !declared.Contains(n))
                .OrderBy(n => n, StringComparer.Ordinal).ToList();
            var missing = declared.Where(n => !onDisk.Contains(n))
                .OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (undeclared.Count > 0 || missing.Count > 0)
            {
                string message = readerName + ": the " + stem + ".*.cs files under " + dir
                    + " differ from its declared lists.";
                if (undeclared.Count > 0)
                    message += " Undeclared partial file(s): " + string.Join(", ", undeclared)
                        + ". Add each to SplitFileNames if the gates that read the whole class"
                        + " must read it, or to OtherPartialFileNames if they must not.";
                if (missing.Count > 0)
                    message += " Declared but not found: " + string.Join(", ", missing) + ".";
                throw new InvalidOperationException(message);
            }

            return new[] { main }
                .Concat(read.OrderBy(n => n, StringComparer.Ordinal).Select(n => Path.Combine(dir, n)))
                .ToList();
        }
    }
}
