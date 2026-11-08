using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Parsek.Tests
{
    /// <summary>
    /// <c>MissionsWindowUI</c> is one partial class. The text that was
    /// <c>UI/MissionsWindowUI.cs</c> before it was split by
    /// <c>scripts/refactor/split_missionswindowui.py</c> is now <c>MissionsWindowUI.cs</c>
    /// (every field, constant, property, nested cache type and the constructor, plus the
    /// testing seams, styles, the reveal navigation, the tab draw, sorting and the column
    /// header) and the seven
    /// <c>MissionsWindowUI.&lt;Area&gt;.cs</c> files that script writes. A source gate that read
    /// "MissionsWindowUI.cs" as a whole reads exactly that set as one text: the main file
    /// first, then the area files in ordinal file-name order, each file whole, so a member's
    /// body is never split across the join. A gate that reads ONE method reads it from that
    /// text with a brace-matched block, never with a slice up to the next declaration (the
    /// next declaration may now sit in another file).
    /// </summary>
    internal static class MissionsWindowUISourceFiles
    {
        internal const string MainFileName = "MissionsWindowUI.cs";
        internal const string ForeignDocks = "MissionsWindowUI.ForeignDocks.cs";
        internal const string Interact = "MissionsWindowUI.Interact.cs";
        internal const string Loop = "MissionsWindowUI.Loop.cs";
        internal const string MissionRows = "MissionsWindowUI.MissionRows.cs";
        internal const string Periodicity = "MissionsWindowUI.Periodicity.cs";
        internal const string VesselRows = "MissionsWindowUI.VesselRows.cs";
        internal const string ViewData = "MissionsWindowUI.ViewData.cs";

        internal static readonly string[] SplitFileNames =
        {
            ForeignDocks, Interact, Loop, MissionRows, Periodicity, VesselRows, ViewData,
        };

        /// <summary>
        /// Partial files of the class that the whole-class readers deliberately skip: none.
        /// Every other file on disk must be in <see cref="SplitFileNames"/>, or
        /// <see cref="Paths"/> throws naming it.
        /// </summary>
        internal static readonly string[] OtherPartialFileNames = Array.Empty<string>();

        /// <summary>The main file then the area files (ordinal), all under <paramref name="uiDir"/>.</summary>
        internal static IReadOnlyList<string> Paths(string uiDir)
        {
            return PartialClassSourceFiles.Paths(
                uiDir,
                MainFileName,
                SplitFileNames,
                OtherPartialFileNames,
                nameof(MissionsWindowUISourceFiles));
        }

        /// <summary>The whole class as one text (see the type summary for the order).</summary>
        internal static string ReadAll(string uiDir)
        {
            return string.Join("\n", Paths(uiDir).Select(File.ReadAllText));
        }
    }
}
