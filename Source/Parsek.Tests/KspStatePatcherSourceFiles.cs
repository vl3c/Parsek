using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Parsek.Tests
{
    /// <summary>
    /// <c>KspStatePatcher</c> is one partial class split over <c>KspStatePatcher.cs</c> (the
    /// <c>PatchAll</c> facade and every static field) and one <c>KspStatePatcher.&lt;Family&gt;.cs</c>
    /// file per stock state family. A source gate that reads "the patcher" reads all of them as
    /// one text: the main file first, then the family files in ordinal name order, each file
    /// whole, so a member's body is never split across the join.
    /// </summary>
    internal static class KspStatePatcherSourceFiles
    {
        internal static IReadOnlyList<string> Paths(string gameActionsDir)
        {
            string main = Path.Combine(gameActionsDir, "KspStatePatcher.cs");
            if (!File.Exists(main))
                throw new FileNotFoundException("KspStatePatcher.cs not found", main);
            var family = Directory.GetFiles(gameActionsDir, "KspStatePatcher.*.cs")
                .Where(p => !string.Equals(Path.GetFileName(p), "KspStatePatcher.cs", StringComparison.Ordinal))
                .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal);
            return new[] { main }.Concat(family).ToList();
        }

        internal static string ReadAll(string gameActionsDir)
        {
            return string.Join("\n", Paths(gameActionsDir).Select(File.ReadAllText));
        }
    }
}
