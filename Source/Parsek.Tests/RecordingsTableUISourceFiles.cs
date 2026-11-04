using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Parsek.Tests
{
    /// <summary>
    /// <c>RecordingsTableUI</c> is one partial class. The text that was
    /// <c>UI/RecordingsTableUI.cs</c> before it was split by
    /// <c>scripts/refactor/split_recordingstableui.py</c> is now <c>RecordingsTableUI.cs</c>
    /// (every field, constant, property, nested state type and the constructor, plus the
    /// lifecycle, navigation, rename and testing seams) and the nine
    /// <c>RecordingsTableUI.&lt;Area&gt;.cs</c> files that script writes. A source gate that read
    /// "RecordingsTableUI.cs" as a whole reads exactly that set as one text: the main file
    /// first, then the area files in ordinal file-name order, each file whole, so a member's
    /// body is never split across the join. A gate that reads ONE method (or an anchored span
    /// inside one method) reads the file that holds it, named by the constants below.
    /// </summary>
    internal static class RecordingsTableUISourceFiles
    {
        internal const string MainFileName = "RecordingsTableUI.cs";
        internal const string GroupAggregates = "RecordingsTableUI.GroupAggregates.cs";
        internal const string GroupTree = "RecordingsTableUI.GroupTree.cs";
        internal const string LoopPeriod = "RecordingsTableUI.LoopPeriod.cs";
        internal const string RewindColumns = "RecordingsTableUI.RewindColumns.cs";
        internal const string SortAndStatus = "RecordingsTableUI.SortAndStatus.cs";
        internal const string TableRows = "RecordingsTableUI.TableRows.cs";
        internal const string UnfinishedFlights = "RecordingsTableUI.UnfinishedFlights.cs";
        internal const string WatchButton = "RecordingsTableUI.WatchButton.cs";
        internal const string Window = "RecordingsTableUI.Window.cs";

        internal static readonly string[] SplitFileNames =
        {
            GroupAggregates, GroupTree, LoopPeriod, RewindColumns, SortAndStatus, TableRows,
            UnfinishedFlights, WatchButton, Window,
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
                nameof(RecordingsTableUISourceFiles));
        }

        /// <summary>The whole class as one text (see the type summary for the order).</summary>
        internal static string ReadAll(string uiDir)
        {
            return string.Join("\n", Paths(uiDir).Select(File.ReadAllText));
        }
    }
}
