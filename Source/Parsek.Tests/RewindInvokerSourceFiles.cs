using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Parsek.Tests
{
    /// <summary>
    /// <c>RewindInvoker</c> is one partial class. The text that was <c>RewindInvoker.cs</c>
    /// before it was split by <c>scripts/refactor/split_rewindinvoker.py</c> is now
    /// <c>RewindInvoker.cs</c> (every field and constant, plus <c>StartInvoke</c>, the temp
    /// quicksave delete and the error handlers) and the nine <c>RewindInvoker.&lt;Area&gt;.cs</c>
    /// files that script writes. A source gate that read "RewindInvoker.cs" as a whole reads
    /// exactly that set as one text: the main file first, then the area files in ordinal
    /// file-name order, each file whole, so a member's body is never split across the join.
    /// A gate that reads ONE method (or an anchored span inside one method) reads the file
    /// that holds it, named by the constants below, and scopes to the method with a
    /// brace-matched block, never a slice up to the next declaration (the next declaration
    /// may now live in another file).
    /// </summary>
    internal static class RewindInvokerSourceFiles
    {
        internal const string MainFileName = "RewindInvoker.cs";
        internal const string Advisory = "RewindInvoker.Advisory.cs";
        internal const string ForkIdentity = "RewindInvoker.ForkIdentity.cs";
        internal const string Gate = "RewindInvoker.Gate.cs";
        internal const string InPlaceDebris = "RewindInvoker.InPlaceDebris.cs";
        internal const string MarkerWrite = "RewindInvoker.MarkerWrite.cs";
        internal const string PostLoad = "RewindInvoker.PostLoad.cs";
        internal const string QuicksaveScrub = "RewindInvoker.QuicksaveScrub.cs";
        internal const string SlotStrip = "RewindInvoker.SlotStrip.cs";
        internal const string StripActivate = "RewindInvoker.StripActivate.cs";

        internal static readonly string[] SplitFileNames =
        {
            Advisory, ForkIdentity, Gate, InPlaceDebris, MarkerWrite, PostLoad, QuicksaveScrub,
            SlotStrip, StripActivate,
        };

        /// <summary>
        /// Partial files of the class that the whole-class readers deliberately skip: none.
        /// Every other file on disk must be in <see cref="SplitFileNames"/>, or
        /// <see cref="Paths"/> throws naming it.
        /// </summary>
        internal static readonly string[] OtherPartialFileNames = Array.Empty<string>();

        /// <summary>The main file then the area files (ordinal), all under <paramref name="parsekDir"/>.</summary>
        internal static IReadOnlyList<string> Paths(string parsekDir)
        {
            return PartialClassSourceFiles.Paths(
                parsekDir,
                MainFileName,
                SplitFileNames,
                OtherPartialFileNames,
                nameof(RewindInvokerSourceFiles));
        }

        /// <summary>The whole class as one text (see the type summary for the order).</summary>
        internal static string ReadAll(string parsekDir)
        {
            return string.Join("\n", Paths(parsekDir).Select(File.ReadAllText));
        }

        /// <summary>
        /// The whole class when <paramref name="path"/> names the main file, otherwise
        /// <see cref="ParsekScenarioSourceFiles.ReadText"/> (which reads the other split
        /// classes whole and any other file as it is), so a reader that takes file names from a
        /// table keeps its other rows as they are.
        /// </summary>
        internal static string ReadText(string path)
        {
            if (string.Equals(Path.GetFileName(path), MainFileName, StringComparison.Ordinal))
                return ReadAll(Path.GetDirectoryName(path));
            return ParsekScenarioSourceFiles.ReadText(path);
        }
    }
}
