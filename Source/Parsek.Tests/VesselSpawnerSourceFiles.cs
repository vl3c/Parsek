using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Parsek.Tests
{
    /// <summary>
    /// <c>VesselSpawner</c> is one partial class. The text that was <c>VesselSpawner.cs</c>
    /// before it was split by <c>scripts/refactor/split_vesselspawner.py</c> is now
    /// <c>VesselSpawner.cs</c> (every field, constant and property, plus the shared spawn core)
    /// and the eight <c>VesselSpawner.&lt;Area&gt;.cs</c> files that script writes. A source gate
    /// that read "VesselSpawner.cs" as a whole reads exactly that set as one text: the main file
    /// first, then the area files in ordinal file-name order, each file whole, so a member's body
    /// is never split across the join. Every <c>VesselSpawner.*.cs</c> on disk must be named in
    /// <see cref="SplitFileNames"/> or <see cref="OtherPartialFileNames"/>, or
    /// <see cref="Paths"/> throws naming the new file.
    /// </summary>
    internal static class VesselSpawnerSourceFiles
    {
        internal static readonly string[] SplitFileNames =
        {
            "VesselSpawner.CrewRoster.cs",
            "VesselSpawner.EndOfRecording.cs",
            "VesselSpawner.KscRetirement.cs",
            "VesselSpawner.Respawn.cs",
            "VesselSpawner.SourceIdentity.cs",
            "VesselSpawner.SpawnRotation.cs",
            "VesselSpawner.TerminalOrbit.cs",
            "VesselSpawner.Validation.cs",
        };

        /// <summary>
        /// Partial files that are not part of the split text. None today; listed so a NEW
        /// <c>VesselSpawner.*.cs</c> fails <see cref="Paths"/> until it is declared.
        /// </summary>
        internal static readonly string[] OtherPartialFileNames = { };

        internal static IReadOnlyList<string> Paths(string parsekSourceDir)
        {
            return PartialClassSourceFiles.Paths(
                parsekSourceDir,
                "VesselSpawner.cs",
                SplitFileNames,
                OtherPartialFileNames,
                nameof(VesselSpawnerSourceFiles));
        }

        internal static string ReadAll(string parsekSourceDir)
        {
            return string.Join("\n", Paths(parsekSourceDir).Select(File.ReadAllText));
        }
    }
}
