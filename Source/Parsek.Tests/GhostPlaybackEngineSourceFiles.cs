using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Parsek.Tests
{
    /// <summary>
    /// <c>GhostPlaybackEngine</c> is one partial class split over <c>GhostPlaybackEngine.cs</c>
    /// (every field, event, property, constant and the constructor, plus the per-frame entry
    /// point) and the <c>GhostPlaybackEngine.&lt;Area&gt;.cs</c> files. A source gate that reads
    /// "the engine" reads all of them as one text: the main file first, then every
    /// <c>GhostPlaybackEngine.*.cs</c> in ordinal file-name order, each file whole, so a member's
    /// body is never split across the join.
    /// </summary>
    internal static class GhostPlaybackEngineSourceFiles
    {
        internal static IReadOnlyList<string> Paths(string parsekSourceDir)
        {
            string main = Path.Combine(parsekSourceDir, "GhostPlaybackEngine.cs");
            if (!File.Exists(main))
                throw new FileNotFoundException("GhostPlaybackEngine.cs not found", main);
            var parts = Directory.GetFiles(parsekSourceDir, "GhostPlaybackEngine.*.cs")
                .Where(p => !string.Equals(Path.GetFileName(p), "GhostPlaybackEngine.cs", StringComparison.Ordinal))
                .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal);
            return new[] { main }.Concat(parts).ToList();
        }

        internal static string ReadAll(string parsekSourceDir)
        {
            return string.Join("\n", Paths(parsekSourceDir).Select(File.ReadAllText));
        }
    }
}
