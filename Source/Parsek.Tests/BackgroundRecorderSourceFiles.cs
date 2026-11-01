using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Parsek.Tests
{
    /// <summary>
    /// <c>BackgroundRecorder</c> is one partial class. The text that was <c>BackgroundRecorder.cs</c>
    /// before it was split by <c>scripts/refactor/split_backgroundrecorder.py</c> is now
    /// <c>BackgroundRecorder.cs</c> (every field, constant, property and the constructor, plus the
    /// public lifecycle API) and the eight <c>BackgroundRecorder.&lt;Area&gt;.cs</c> files that script
    /// writes. A source gate that read "BackgroundRecorder.cs" as a whole reads exactly that set as
    /// one text: the main file first, then the area files in ordinal file-name order, each file
    /// whole, so a member's body is never split across the join. The older siblings
    /// (PartEventPolling, State, Testing, TimeJump) were never part of that file and are NOT read,
    /// so a gate's scope stays what it was.
    /// </summary>
    internal static class BackgroundRecorderSourceFiles
    {
        internal static readonly string[] SplitFileNames =
        {
            "BackgroundRecorder.Finalization.cs",
            "BackgroundRecorder.GameEventHandlers.cs",
            "BackgroundRecorder.MemberReconcile.cs",
            "BackgroundRecorder.RelativeAnchors.cs",
            "BackgroundRecorder.Sampling.cs",
            "BackgroundRecorder.SplitDetection.cs",
            "BackgroundRecorder.StateInitialization.cs",
            "BackgroundRecorder.TrackSections.cs",
        };

        internal static IReadOnlyList<string> Paths(string parsekSourceDir)
        {
            var names = new[] { "BackgroundRecorder.cs" }
                .Concat(SplitFileNames.OrderBy(n => n, StringComparer.Ordinal));
            var paths = new List<string>();
            foreach (string name in names)
            {
                string path = Path.Combine(parsekSourceDir, name);
                if (!File.Exists(path))
                    throw new FileNotFoundException(name + " not found", path);
                paths.Add(path);
            }
            return paths;
        }

        internal static string ReadAll(string parsekSourceDir)
        {
            return string.Join("\n", Paths(parsekSourceDir).Select(File.ReadAllText));
        }
    }
}
