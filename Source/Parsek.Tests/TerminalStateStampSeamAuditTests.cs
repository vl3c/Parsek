using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Allowlist-shaped audit gate for the terminal-stamp seam, in the spirit of
    /// <see cref="GrepAuditTests"/>'s ERS/ELS and UI-complexity-mode gates but written as a
    /// plain walk (no pwsh dependency, so it also runs on a non-Windows runner).
    ///
    /// <para><c>Recording.StampTerminalState</c> is the single door for DECIDING a
    /// recording's terminal verdict, because that decision has to retire crew end states
    /// inferred against the old verdict (<c>KerbalsModule.InvalidateCrewEndStatesForTerminalStamp</c>).
    /// The single-door property was audited by hand once and two verdict-deciding sites
    /// were missed, both in files that also hold legitimate raw writes - hence a gate, and
    /// hence an allowlist keyed on the exact source LINE rather than on the file.</para>
    /// </summary>
    public class TerminalStateStampSeamAuditTests
    {
        private const string AllowlistFileName = "terminal-state-stamp-audit-allowlist.txt";

        // `TerminalStateValue` followed by an assignment `=` — excluding `==`, `!=`, `>=`,
        // `<=` and the expression-bodied member arrow `=>`.
        private static readonly Regex RawAssignment = new Regex(
            @"TerminalStateValue\s*=(?![=>])",
            RegexOptions.CultureInvariant);

        [Fact]
        public void EveryRawTerminalStateAssignmentIsAllowlisted()
        {
            string repoRoot = ResolveRepoRoot();
            string sourceRoot = Path.Combine(repoRoot, "Source", "Parsek");
            string allowlistPath = Path.Combine(repoRoot, "scripts", AllowlistFileName);

            Assert.True(Directory.Exists(sourceRoot), "source root missing: " + sourceRoot);
            Assert.True(File.Exists(allowlistPath), "allowlist missing: " + allowlistPath);

            HashSet<string> allowedLines;
            List<string> allowedPrefixes;
            LoadAllowlist(allowlistPath, out allowedLines, out allowedPrefixes);

            var violations = new List<string>();
            int hits = 0;

            foreach (string file in Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Relative(repoRoot, file);
                bool fileAllowed = IsUnderAllowedPrefix(rel, allowedPrefixes);

                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = StripComment(lines[i]);
                    if (!RawAssignment.IsMatch(code)) continue;

                    hits++;
                    if (fileAllowed) continue;

                    string key = rel + " | " + lines[i].Trim();
                    if (allowedLines.Contains(key)) continue;

                    violations.Add(rel + ":" + (i + 1) + ": " + lines[i].Trim());
                }
            }

            if (violations.Count > 0)
            {
                var message = new StringBuilder();
                message.AppendLine(
                    violations.Count + " raw TerminalStateValue assignment(s) outside the seam "
                    + "(total hits scanned: " + hits + ").");
                message.AppendLine(
                    "Route the verdict through Recording.StampTerminalState(value, context) so crew "
                    + "end states inferred against the old verdict are retired, or - if the site is a "
                    + "structural copy that cannot leave a stale pair behind - add it to scripts/"
                    + AllowlistFileName + " with a rationale.");
                for (int i = 0; i < violations.Count; i++)
                    message.AppendLine("  " + violations[i]);
                Assert.True(false, message.ToString());
            }

            // Anti-vacuity: the walk must actually be finding assignments. A regex or path
            // regression that matched nothing would otherwise pass silently forever.
            Assert.True(hits >= 10,
                "allowlist audit found only " + hits + " TerminalStateValue assignment(s) — "
                + "the scan is probably not reaching the source tree");
        }

        [Fact]
        public void EveryAllowlistEntryStillExists()
        {
            // A stale entry is how an allowlist rots into a rubber stamp: it keeps granting
            // permission for a line nobody can find any more.
            string repoRoot = ResolveRepoRoot();
            string allowlistPath = Path.Combine(repoRoot, "scripts", AllowlistFileName);

            HashSet<string> allowedLines;
            List<string> allowedPrefixes;
            LoadAllowlist(allowlistPath, out allowedLines, out allowedPrefixes);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            string sourceRoot = Path.Combine(repoRoot, "Source", "Parsek");
            foreach (string file in Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Relative(repoRoot, file);
                foreach (string line in File.ReadAllLines(file))
                {
                    if (!RawAssignment.IsMatch(StripComment(line))) continue;
                    seen.Add(rel + " | " + line.Trim());
                }
            }

            var stale = new List<string>();
            foreach (string entry in allowedLines)
            {
                if (!seen.Contains(entry))
                    stale.Add(entry);
            }

            Assert.True(stale.Count == 0,
                "stale allowlist entries in scripts/" + AllowlistFileName
                + " (no matching source line): " + string.Join("; ", stale.ToArray()));

            foreach (string prefix in allowedPrefixes)
            {
                Assert.True(Directory.Exists(Path.Combine(repoRoot, prefix.Replace('/', Path.DirectorySeparatorChar))),
                    "stale allowlist directory prefix: " + prefix);
            }
        }

        private static void LoadAllowlist(
            string path,
            out HashSet<string> allowedLines,
            out List<string> allowedPrefixes)
        {
            allowedLines = new HashSet<string>(StringComparer.Ordinal);
            allowedPrefixes = new List<string>();

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                string normalized = line.Replace('\\', '/');
                if (normalized.EndsWith("/", StringComparison.Ordinal))
                {
                    allowedPrefixes.Add(normalized);
                    continue;
                }

                // Normalize spacing around the single '|' separator so the file stays
                // readable without the matcher depending on it.
                int sep = normalized.IndexOf('|');
                if (sep < 0) continue;
                string file = normalized.Substring(0, sep).Trim();
                string text = normalized.Substring(sep + 1).Trim();
                allowedLines.Add(file + " | " + text);
            }
        }

        private static bool IsUnderAllowedPrefix(string rel, List<string> prefixes)
        {
            for (int i = 0; i < prefixes.Count; i++)
            {
                if (rel.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Drops a trailing line comment so prose such as
        /// <c>// Sets TerminalStateValue = Destroyed</c> is not read as an assignment.
        /// Only strips when no quote precedes the <c>//</c>, so a string literal
        /// containing a slash pair is left intact.
        /// </summary>
        private static string StripComment(string line)
        {
            int idx = line.IndexOf("//", StringComparison.Ordinal);
            if (idx < 0) return line;
            string head = line.Substring(0, idx);
            return head.IndexOf('"') >= 0 ? line : head;
        }

        private static string Relative(string repoRoot, string fullPath)
        {
            string root = repoRoot.Replace('\\', '/').TrimEnd('/') + "/";
            string full = fullPath.Replace('\\', '/');
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length)
                : full;
        }

        private static string ResolveRepoRoot()
        {
            // xUnit runs from Source/Parsek.Tests/bin/Debug/net472/ — walk up until a
            // directory holds both 'scripts/' and 'Source/'.
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "scripts"))
                    && Directory.Exists(Path.Combine(dir, "Source")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException(
                "Could not locate repo root from " + AppContext.BaseDirectory);
        }
    }
}
