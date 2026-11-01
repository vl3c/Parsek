using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Source reader for the source-scanning gates over <c>LedgerOrchestrator</c>, a static
    /// partial class split across <c>GameActions/LedgerOrchestrator.cs</c> and the
    /// <c>GameActions/LedgerOrchestrator.&lt;Area&gt;.cs</c> files. A gate that read only the main
    /// file would lose every member that lives in an area file, so the gates read all of them.
    /// </summary>
    internal static class LedgerOrchestratorSource
    {
        /// <summary>
        /// The text of every LedgerOrchestrator file: <c>LedgerOrchestrator.cs</c> first, then
        /// each <c>LedgerOrchestrator.*.cs</c> in ordinal file-name order, joined by a newline.
        /// Every member sits whole inside one file, so a scan that starts at a member's
        /// declaration reads that member's body exactly as it did in a single file.
        /// </summary>
        internal static string ReadAll()
        {
            string dir = Path.Combine(
                SyntheticRecordingTests.ResolveProjectRoot(), "Source", "Parsek", "GameActions");
            string main = Path.Combine(dir, "LedgerOrchestrator.cs");
            Assert.True(File.Exists(main), "source not found at " + main);

            var parts = Directory.GetFiles(dir, "LedgerOrchestrator.*.cs")
                .Where(p => !string.Equals(Path.GetFileName(p), "LedgerOrchestrator.cs",
                    StringComparison.Ordinal))
                .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
                .ToList();
            Assert.True(parts.Count > 0, "no LedgerOrchestrator.<Area>.cs partial files under " + dir);

            var sb = new StringBuilder(File.ReadAllText(main));
            foreach (string part in parts)
                sb.Append('\n').Append(File.ReadAllText(part));
            return sb.ToString();
        }
    }
}
