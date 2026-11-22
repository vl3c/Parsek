using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// CHANGELOG entries stay short (owner rule, `.claude/CLAUDE.md` "Documentation updates"):
    /// from version 1.0.3 on at most two lines each, in older sections at most five, a line
    /// being 100 characters (a longer physical line counts as several). The mechanism, logs,
    /// tests and history of a change live in the todo entry and the PR / commit message. Without
    /// a gate the 0.10.4 / 0.10.5 entries grew to 85 lines each. Every section is checked.
    /// </summary>
    public class ChangelogEntryLengthTests
    {
        internal const int MaxEntryLines = 5;
        internal const int MaxEntryLinesFromShortRule = 2;
        internal static readonly Version ShortRuleFirstVersion = new Version(1, 0, 3);
        internal const int LineWidth = 100;

        [Fact]
        public void EveryChangelogEntry_IsWithinItsSectionLimit()
        {
            string path = Path.Combine(ResolveRepoRoot(), "CHANGELOG.md");
            Assert.True(File.Exists(path), "missing CHANGELOG.md: " + path);

            var offenders = FindOverlongEntries(File.ReadAllText(path));
            Assert.True(offenders.Count == 0,
                "CHANGELOG entries over their limit (" + MaxEntryLinesFromShortRule + " lines of "
                + LineWidth + " characters from " + ShortRuleFirstVersion + " on, " + MaxEntryLines
                + " before; keep the essentials; detail goes in the todo entry and the PR):\n"
                + string.Join("\n", offenders));
        }

        [Fact]
        public void FindOverlongEntries_CountsWrappedLines_InEverySection()
        {
            string wide = "- **Wide.** " + new string('x', LineWidth * 5);
            string text = string.Join("\n", new[]
            {
                "# Changelog",
                "",
                "## 1.0.4",
                "",
                "- **NewTwo.** a",
                "  b",
                "- **NewThree.** a",
                "  b",
                "  c",
                "",
                "## 0.10.6",
                "",
                "### Fixed",
                "",
                "- **Short.** One line.",
                "- **Five.** a",
                "  b",
                "  c",
                "  d",
                "  e",
                "- **Six.** a",
                "  b",
                "  c",
                "  d",
                "  e",
                "  f",
                "",
                wide,
                "",
                "## 0.10.3",
                "",
                "- **Old.** a",
                "  b",
                "  c",
                "  d",
                "  e",
                "  f",
            });

            var offenders = FindOverlongEntries(text);

            Assert.Equal(4, offenders.Count);
            Assert.Contains(offenders, o => o.Contains("**NewThree.**") && o.Contains("3 lines"));
            Assert.DoesNotContain(offenders, o => o.Contains("**NewTwo.**"));
            Assert.DoesNotContain(offenders, o => o.Contains("**Five.**"));
            Assert.Contains(offenders, o => o.Contains("**Six.**") && o.Contains("6 lines"));
            Assert.Contains(offenders, o => o.Contains("**Wide.**") && o.Contains("6 lines"));
            Assert.Contains(offenders, o => o.Contains("**Old.**") && o.Contains("6 lines"));
        }

        /// <summary>
        /// An entry starts with "- " at column 0 and runs to the next such line, a "#" heading,
        /// a "---" separator or the end. Its length is the sum over its non-empty lines of
        /// max(1, ceil(length / <see cref="LineWidth"/>)). Its limit comes from the last
        /// "## x.y.z" heading above it: two lines from <see cref="ShortRuleFirstVersion"/> on,
        /// five before it or under a heading that is not a version.
        /// </summary>
        internal static List<string> FindOverlongEntries(string text)
        {
            var offenders = new List<string>();
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            int start = -1;
            int limit = MaxEntryLines;
            for (int i = 0; i <= lines.Length; i++)
            {
                string line = i < lines.Length ? lines[i] : null;
                bool boundary = line == null
                    || line.StartsWith("- ", StringComparison.Ordinal)
                    || line.StartsWith("#", StringComparison.Ordinal)
                    || line.Trim() == "---";
                if (!boundary) continue;
                if (start >= 0) Check(lines, start, i, limit, offenders);
                if (line != null && line.StartsWith("## ", StringComparison.Ordinal))
                    limit = LimitForSection(line.Substring(3).Trim());
                start = line != null && line.StartsWith("- ", StringComparison.Ordinal) ? i : -1;
            }
            return offenders;
        }

        internal static int LimitForSection(string heading)
        {
            Version version;
            if (Version.TryParse(heading, out version) && version >= ShortRuleFirstVersion)
                return MaxEntryLinesFromShortRule;
            return MaxEntryLines;
        }

        private static void Check(string[] lines, int start, int end, int limit, List<string> offenders)
        {
            int count = 0;
            for (int i = start; i < end; i++)
            {
                int length = lines[i].TrimEnd().Length;
                if (lines[i].Trim().Length == 0) continue;
                count += Math.Max(1, (length + LineWidth - 1) / LineWidth);
            }
            if (count <= limit) return;
            string lead = lines[start].Length > 70 ? lines[start].Substring(0, 70) : lines[start];
            offenders.Add("line " + (start + 1) + ": " + count + " lines: " + lead);
        }

        private static string ResolveRepoRoot()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "CHANGELOG.md"))
                    && Directory.Exists(Path.Combine(dir, ".claude")))
                    return dir;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            throw new InvalidOperationException("repo root not found above " + AppDomain.CurrentDomain.BaseDirectory);
        }
    }
}
