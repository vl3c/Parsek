using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Parsek.Tests
{
    /// <summary>
    /// <c>ParsekFlightGhostPositioner</c> is one partial class. The text that was
    /// <c>ParsekFlightGhostPositioner.cs</c> before <c>scripts/refactor/split_ghostpositioner.py</c>
    /// cut it is now <c>ParsekFlightGhostPositioner.cs</c> (the class comment, <c>host</c>, the
    /// constructor, every field and constant, the thin IGhostPositioner entry points, the body
    /// lookups and the engine distance queries) and the eleven
    /// <c>ParsekFlightGhostPositioner.&lt;Group&gt;.cs</c> files that script writes. A source gate
    /// that read "ParsekFlightGhostPositioner.cs" as a whole reads exactly that set as one text:
    /// the main file first, then the group files in ordinal file-name order, each file whole, so
    /// a member's body is never split across the join. <see cref="ParsekFlightSourceFiles"/>
    /// reads this set wherever it read the positioner's one file. Every
    /// <c>ParsekFlightGhostPositioner.*.cs</c> on disk must be named in
    /// <see cref="SplitFileNames"/> or <see cref="OtherPartialFileNames"/>, or <see cref="Paths"/>
    /// throws naming the new file.
    /// </summary>
    internal static class ParsekFlightGhostPositionerSourceFiles
    {
        internal const string MainFileName = "ParsekFlightGhostPositioner.cs";

        /// <summary>The module folder under Source/Parsek that holds the class's files.</summary>
        internal const string ModuleFolder = "Controllers";

        internal static readonly string[] SplitFileNames =
        {
            "ParsekFlightGhostPositioner.BodyFixedPrimary.cs",
            "ParsekFlightGhostPositioner.Checkpoint.cs",
            "ParsekFlightGhostPositioner.Loop.cs",
            "ParsekFlightGhostPositioner.Orbit.cs",
            "ParsekFlightGhostPositioner.OrbitTailContinuity.cs",
            "ParsekFlightGhostPositioner.Points.cs",
            "ParsekFlightGhostPositioner.RecordedRelative.cs",
            "ParsekFlightGhostPositioner.RelativeAnchor.cs",
            "ParsekFlightGhostPositioner.TailLift.cs",
            "ParsekFlightGhostPositioner.Trace.cs",
            "ParsekFlightGhostPositioner.WorldPosition.cs",
        };

        /// <summary>
        /// Partial files that are not part of the split text. None today; listed so a NEW
        /// <c>ParsekFlightGhostPositioner.*.cs</c> fails <see cref="Paths"/> until it is declared.
        /// </summary>
        internal static readonly string[] OtherPartialFileNames = { };

        internal static IReadOnlyList<string> Paths(string parsekSourceDir)
        {
            return PartialClassSourceFiles.Paths(
                PartialClassSourceFiles.FamilyDir(parsekSourceDir, MainFileName, ModuleFolder),
                MainFileName,
                SplitFileNames,
                OtherPartialFileNames,
                nameof(ParsekFlightGhostPositionerSourceFiles));
        }

        internal static string ReadAll(string parsekSourceDir)
        {
            return string.Join("\n", Paths(parsekSourceDir).Select(File.ReadAllText));
        }
    }
}
