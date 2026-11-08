using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Parsek.Tests
{
    /// <summary>
    /// <c>LogisticsWindowUI</c> is one partial class. The text that was
    /// <c>UI/LogisticsWindowUI.cs</c> before it was split by
    /// <c>scripts/refactor/split_logisticswindowui.py</c> is now <c>LogisticsWindowUI.cs</c>
    /// (every field, constant, property, the candidate display and legibility types and the
    /// constructor, plus the testing seams, the window lifecycle and draw, the styles and the
    /// small shared helpers) and the seven
    /// <c>LogisticsWindowUI.&lt;Area&gt;.cs</c> files that script writes. A source gate that read
    /// "LogisticsWindowUI.cs" as a whole reads exactly that set as one text: the main file
    /// first, then the area files in ordinal file-name order, each file whole, so a member's
    /// body is never split across the join. A gate that reads ONE method reads it from that
    /// text with a brace-matched block, never with a slice up to the next declaration (the
    /// next declaration may now sit in another file).
    /// </summary>
    internal static class LogisticsWindowUISourceFiles
    {
        internal const string MainFileName = "LogisticsWindowUI.cs";
        internal const string Actions = "LogisticsWindowUI.Actions.cs";
        internal const string Detail = "LogisticsWindowUI.Detail.cs";
        internal const string Formatting = "LogisticsWindowUI.Formatting.cs";
        internal const string Legibility = "LogisticsWindowUI.Legibility.cs";
        internal const string LinkPicker = "LogisticsWindowUI.LinkPicker.cs";
        internal const string Rows = "LogisticsWindowUI.Rows.cs";
        internal const string Sections = "LogisticsWindowUI.Sections.cs";

        internal static readonly string[] SplitFileNames =
        {
            Actions, Detail, Formatting, Legibility, LinkPicker, Rows, Sections,
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
                nameof(LogisticsWindowUISourceFiles));
        }

        /// <summary>The whole class as one text (see the type summary for the order).</summary>
        internal static string ReadAll(string uiDir)
        {
            return string.Join("\n", Paths(uiDir).Select(File.ReadAllText));
        }
    }
}
