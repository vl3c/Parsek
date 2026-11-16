using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Source gate: the playback engine counts ghost lifecycle health events and publishes its
    /// frame budget, history sample and threshold check only through its
    /// <see cref="IGhostPlaybackDiagnosticsSink"/>, never by naming <c>DiagnosticsState</c> or
    /// <c>DiagnosticsComputation</c> itself, so a second engine given another sink cannot feed
    /// the player's own diagnostics. Reads every <c>GhostPlaybackEngine*.cs</c> file with
    /// comments stripped (a comment naming the counter is not a write).
    /// </summary>
    public class GhostPlaybackEngineDiagnosticsSourceGateTests
    {
        private static readonly string[] ForbiddenTokens =
        {
            "DiagnosticsState.",
            "DiagnosticsComputation.",
            "ghostBuildsThisSession",
            "reentryFxDeferredThisSession",
            "reentryFxSkippedThisSession",
            "reentryFxBuildsThisSession",
            "ghostReusedAcrossCycleThisSession",
            "ghostDestroysThisSession",
        };

        // Each sink member and how many engine call sites it has today. A dropped call would
        // silently stop a counter the diagnostics report and the run index read.
        private static readonly (string call, int count)[] RequiredSinkCalls =
        {
            ("diagnostics.CountGhostBuild()", 1),
            ("diagnostics.CountReentryFxDeferred()", 1),
            ("diagnostics.CountReentryFxSkipped()", 1),
            ("diagnostics.CountReentryFxBuild()", 1),
            ("diagnostics.CountGhostReusedAcrossCycle()", 1),
            ("diagnostics.CountGhostDestroy()", 1),
            ("diagnostics.ClearPlaybackBudget()", 3),
            ("diagnostics.PublishPlaybackBudget(", 1),
            ("diagnostics.AppendPlaybackFrame(", 1),
            ("diagnostics.CheckPlaybackBudgetThreshold(", 1),
        };

        // The end of UpdatePlayback publishes in this order: the budget, then the history
        // sample, then the threshold check (which reads neither, but logs last).
        private static readonly string[] FrameEndCallOrder =
        {
            "diagnostics.PublishPlaybackBudget(",
            "diagnostics.AppendPlaybackFrame(",
            "diagnostics.CheckPlaybackBudgetThreshold(",
        };

        private static string[] EngineFiles()
        {
            string dir = Path.Combine(
                SyntheticRecordingTests.ResolveProjectRoot(), "Source", "Parsek", "Ghost");
            string[] files = Directory.GetFiles(dir, "GhostPlaybackEngine*.cs");
            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }

        [Fact]
        public void EngineFiles_AreFound()
        {
            string[] files = EngineFiles();
            Assert.Contains(files, f => Path.GetFileName(f) == "GhostPlaybackEngine.cs");
            Assert.Contains(files, f => Path.GetFileName(f) == "GhostPlaybackEngine.SpawnDestroy.cs");
            Assert.True(files.Length >= 10, "engine partial files found: " + files.Length);
        }

        [Fact]
        public void EngineFiles_NeverWriteTheGlobalHealthCounters()
        {
            foreach (string file in EngineFiles())
            {
                string code = StripComments(File.ReadAllText(file));
                foreach (string token in ForbiddenTokens)
                    Assert.False(code.Contains(token),
                        $"{Path.GetFileName(file)} names '{token}'; count through the diagnostics sink instead");
            }
        }

        [Fact]
        public void EngineFiles_CallEverySinkMemberAtItsSites()
        {
            string all = string.Join("\n", EngineFiles().Select(f => StripComments(File.ReadAllText(f))));
            foreach (var (call, count) in RequiredSinkCalls)
            {
                int found = CountOccurrences(all, call);
                Assert.True(found == count, $"'{call}' found {found} times in the engine files, expected {count}");
            }
        }

        [Fact]
        public void EngineFiles_PublishTheFrameBudgetInOrder()
        {
            string all = string.Join("\n", EngineFiles().Select(f => StripComments(File.ReadAllText(f))));
            int previous = -1;
            foreach (string call in FrameEndCallOrder)
            {
                int at = all.IndexOf(call, StringComparison.Ordinal);
                Assert.True(at > previous, $"'{call}' at {at} does not follow the previous frame-end call at {previous}");
                previous = at;
            }
        }

        [Fact]
        public void StripComments_RemovesLineAndBlockCommentsButKeepsStrings()
        {
            string src = "a(); // DiagnosticsState.health.x++\n/* DiagnosticsState.health */ b(\"// kept\");";
            string stripped = StripComments(src);
            Assert.DoesNotContain("DiagnosticsState.health", stripped);
            Assert.Contains("a();", stripped);
            Assert.Contains("b(\"// kept\");", stripped);
        }

        private static int CountOccurrences(string text, string token)
        {
            int n = 0;
            for (int i = text.IndexOf(token, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
                n++;
            return n;
        }

        // Removes // and /* */ comments; string and char literals (regular, verbatim and
        // interpolated, without nested holes holding quotes) are copied through untouched.
        internal static string StripComments(string src)
        {
            var sb = new StringBuilder(src.Length);
            int i = 0;
            while (i < src.Length)
            {
                char c = src[i];
                char next = i + 1 < src.Length ? src[i + 1] : '\0';
                if (c == '/' && next == '/')
                {
                    while (i < src.Length && src[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && next == '*')
                {
                    int end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? src.Length : end + 2;
                    sb.Append(' ');
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    bool verbatim = i > 0 && src[i - 1] == '@'
                        || i > 1 && src[i - 1] == '$' && src[i - 2] == '@';
                    sb.Append(c);
                    i++;
                    while (i < src.Length)
                    {
                        char d = src[i];
                        sb.Append(d);
                        i++;
                        if (!verbatim && d == '\\' && i < src.Length)
                        {
                            sb.Append(src[i]);
                            i++;
                            continue;
                        }
                        if (d == c)
                        {
                            if (verbatim && c == '"' && i < src.Length && src[i] == '"')
                            {
                                sb.Append('"');
                                i++;
                                continue;
                            }
                            break;
                        }
                        if (d == '\n' && !verbatim)
                            break;
                    }
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
    }
}
