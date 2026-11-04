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
    /// (PartEventPolling, PhysicsSoi, State, Testing, TimeJump) were never part of that file and are NOT read,
    /// so a gate's scope stays what it was. Every <c>BackgroundRecorder.*.cs</c> on disk must be
    /// named in one of the two lists, or <see cref="Paths"/> throws naming the new file.
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

        /// <summary>
        /// The older sibling partials, never part of the split file and not read. Listed so a
        /// NEW <c>BackgroundRecorder.*.cs</c> fails <see cref="Paths"/> until it is declared.
        /// </summary>
        internal static readonly string[] OtherPartialFileNames =
        {
            "BackgroundRecorder.PartEventPolling.cs",
            "BackgroundRecorder.PhysicsSoi.cs",
            "BackgroundRecorder.State.cs",
            "BackgroundRecorder.Testing.cs",
            "BackgroundRecorder.TimeJump.cs",
        };

        internal static IReadOnlyList<string> Paths(string parsekSourceDir)
        {
            return PartialClassSourceFiles.Paths(
                parsekSourceDir,
                "BackgroundRecorder.cs",
                SplitFileNames,
                OtherPartialFileNames,
                nameof(BackgroundRecorderSourceFiles));
        }

        internal static string ReadAll(string parsekSourceDir)
        {
            return string.Join("\n", Paths(parsekSourceDir).Select(File.ReadAllText));
        }
    }
}
