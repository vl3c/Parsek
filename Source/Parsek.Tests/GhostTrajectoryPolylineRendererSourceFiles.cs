using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Parsek.Tests
{
    /// <summary>
    /// <c>GhostTrajectoryPolylineRenderer</c> is one partial class. The text that was
    /// <c>Display/GhostTrajectoryPolylineRenderer.cs</c> before it was split by
    /// <c>scripts/refactor/split_ghosttrajectorypolylinerenderer.py</c> is now
    /// <c>GhostTrajectoryPolylineRenderer.cs</c> (every field, constant and property, the nested
    /// <c>Driver</c> MonoBehaviour whole, the ownership readers and paint set,
    /// <c>DidOwnershipPublishRunOnFrame</c>, the cache lifecycle, the walk-inclusion gates, the
    /// leg / arc data structs and the testing seams) and the six
    /// <c>GhostTrajectoryPolylineRenderer.&lt;Area&gt;.cs</c> files that script writes. A source
    /// gate whose claim is about the CLASS (a "the draw path never reads X" negative) reads
    /// exactly that set as one text: the main file first, then the area files in ordinal
    /// file-name order, each file whole, so a member's body is never split across the join. A
    /// gate that reads ONE method reads the file that holds it, named by the constants below; a
    /// gate pinned to the Driver walk keeps reading the main file alone.
    /// </summary>
    internal static class GhostTrajectoryPolylineRendererSourceFiles
    {
        internal const string MainFileName = "GhostTrajectoryPolylineRenderer.cs";
        internal const string Anchoring = "GhostTrajectoryPolylineRenderer.Anchoring.cs";
        internal const string Builder = "GhostTrajectoryPolylineRenderer.Builder.cs";
        internal const string Diagnostics = "GhostTrajectoryPolylineRenderer.Diagnostics.cs";
        internal const string ForwardArcs = "GhostTrajectoryPolylineRenderer.ForwardArcs.cs";
        internal const string LegDraw = "GhostTrajectoryPolylineRenderer.LegDraw.cs";
        internal const string SeamBridges = "GhostTrajectoryPolylineRenderer.SeamBridges.cs";

        internal static readonly string[] SplitFileNames =
        {
            Anchoring, Builder, Diagnostics, ForwardArcs, LegDraw, SeamBridges,
        };

        /// <summary>The main file then the area files (ordinal), all under <paramref name="displayDir"/>.</summary>
        internal static IReadOnlyList<string> Paths(string displayDir)
        {
            var names = new[] { MainFileName }
                .Concat(SplitFileNames.OrderBy(n => n, StringComparer.Ordinal));
            var paths = new List<string>();
            foreach (string name in names)
            {
                string path = Path.Combine(displayDir, name);
                if (!File.Exists(path))
                    throw new FileNotFoundException(name + " not found", path);
                paths.Add(path);
            }
            return paths;
        }

        /// <summary>The whole class as one text (see the type summary for the order).</summary>
        internal static string ReadAll(string displayDir)
        {
            return string.Join("\n", Paths(displayDir).Select(File.ReadAllText));
        }
    }
}
