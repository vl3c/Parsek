using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Parsek.Tests
{
    /// <summary>
    /// <c>ParsekFlight</c> is one partial class. <c>scripts/refactor/split_parsekflight.py</c>
    /// splits the text of <c>ParsekFlight.cs</c> into <c>ParsekFlight.cs</c> (every field,
    /// constant, initialized property and constructor, the Unity callbacks and the members not
    /// yet split) and the <c>ParsekFlight.&lt;Area&gt;.cs</c> files it writes, listed in
    /// <see cref="SplitFileNames"/>. A source gate that read "ParsekFlight.cs" as a whole reads
    /// exactly that set as one text: the main file first, then the area files in ordinal
    /// file-name order, each file whole, so a member's body is never split across the join. The
    /// older siblings in <see cref="OtherPartialFileNames"/> were never part of that file and
    /// are NOT read, so a gate's scope stays what it was. Every <c>ParsekFlight.*.cs</c> on disk
    /// must be named in one of the two lists, or <see cref="Paths"/> throws naming the new file.
    /// A gate that pins a COUNT in one file keeps naming that file. Phase B of the plan lifts
    /// members out of the partial class into service types (<see cref="ServiceFileNames"/>);
    /// <see cref="ReadAll"/> appends their files after the partials, so a gate that read a moved
    /// body in "ParsekFlight.cs" still reads it. The partials under 200 lines (five split files
    /// and the older siblings BlockedChainTipGhost, BreakupChildSeed, ClaimedVesselHoldGhost,
    /// GroundPartPlacement and TailLift) were folded back into <c>ParsekFlight.cs</c> by
    /// <c>scripts/refactor/fold_parsekflight_partials.py</c>, so a whole-class read now covers
    /// those five older siblings too.
    /// </summary>
    internal static class ParsekFlightSourceFiles
    {
        internal static readonly string[] SplitFileNames =
        {
            "ParsekFlight.GhostPositioner.cs",
            "ParsekFlight.GhostPositioning.cs",
            "ParsekFlight.MergeFlow.cs",
            "ParsekFlight.PadEvidence.cs",
            "ParsekFlight.RecorderSession.cs",
            "ParsekFlight.SplitBranching.cs",
            "ParsekFlight.SwitchContinuation.cs",
            "ParsekFlight.TimelinePlayback.cs",
            "ParsekFlight.TreeRestore.cs",
        };

        /// <summary>
        /// The older sibling partials, never part of the split file and not read. Listed so a
        /// NEW <c>ParsekFlight.*.cs</c> fails <see cref="Paths"/> until it is declared.
        /// </summary>
        internal static readonly string[] OtherPartialFileNames =
        {
            "ParsekFlight.Finalization.cs",
            "ParsekFlight.TerminalEvents.cs",
            "ParsekFlight.TerminalOrbit.cs",
        };

        /// <summary>
        /// The service types lifted out of the partial class (ARCH-PARSEKFLIGHT-CHANGE-HUB
        /// Phase B), each holding members moved verbatim apart from the host re-homing.
        /// </summary>
        internal static readonly string[] ServiceFileNames =
        {
            "ParsekFlightGhostPositioner.cs",
            "ParsekFlightMergeFlow.cs",
            "ParsekFlightPostSwitchAutoRecord.cs",
            "ParsekFlightPlaybackHost.cs",
            "ParsekFlightSpawnHost.cs",
            "ParsekFlightSplitBrancher.cs",
            "ParsekFlightSurfaceCapture.cs",
            "ParsekFlightSwitchContinuation.cs",
        };

        internal static IReadOnlyList<string> Paths(string parsekSourceDir)
        {
            return PartialClassSourceFiles.Paths(
                parsekSourceDir,
                "ParsekFlight.cs",
                SplitFileNames,
                OtherPartialFileNames,
                nameof(ParsekFlightSourceFiles));
        }

        internal static string ReadAll(string parsekSourceDir)
        {
            return string.Join("\n", Paths(parsekSourceDir)
                .Concat(ServiceFileNames.Select(n => Path.Combine(parsekSourceDir, n)))
                .Select(File.ReadAllText));
        }

        /// <summary>
        /// <see cref="File.ReadAllText(string)"/> for a source reader that takes a path: the
        /// path of <c>ParsekFlight.cs</c> reads <see cref="ReadAll"/> (the text that was
        /// ParsekFlight.cs), any other path reads that one file.
        /// </summary>
        internal static string ReadText(string path)
        {
            if (string.Equals(Path.GetFileName(path), "ParsekFlight.cs", StringComparison.Ordinal))
                return ReadAll(Path.GetDirectoryName(path));
            return File.ReadAllText(path);
        }
    }
}
