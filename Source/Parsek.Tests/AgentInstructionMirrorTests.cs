using System;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// `.claude/CLAUDE.md` is the canonical agent instruction file. The repo-root
    /// `AGENTS.md` (read natively by Codex and other agent tools) must be a
    /// byte-identical copy of it. The two drifted apart for months before
    /// 2026-09-01 - a second, hand-maintained copy is exactly how the drift went
    /// unnoticed - so this cell pins them mechanically on every suite run.
    ///
    /// Fix on failure, from the repo root: <c>cp .claude/CLAUDE.md AGENTS.md</c>.
    /// </summary>
    public class AgentInstructionMirrorTests
    {
        [Fact]
        public void RootAgentsMd_IsByteIdenticalTo_ClaudeMd()
        {
            string repoRoot = ResolveRepoRoot();
            string canonical = Path.Combine(repoRoot, ".claude", "CLAUDE.md");
            string mirror = Path.Combine(repoRoot, "AGENTS.md");

            Assert.True(File.Exists(canonical), "missing canonical instruction file: " + canonical);
            Assert.True(File.Exists(mirror), "missing repo-root AGENTS.md mirror: " + mirror);

            string canonicalText = NormalizeLineEndings(File.ReadAllText(canonical));
            string mirrorText = NormalizeLineEndings(File.ReadAllText(mirror));

            Assert.True(
                string.Equals(canonicalText, mirrorText, StringComparison.Ordinal),
                "AGENTS.md must be a byte-identical copy of .claude/CLAUDE.md (line endings "
                + "excepted). It drifted. Fix from the repo root: cp .claude/CLAUDE.md AGENTS.md");
        }

        private static string NormalizeLineEndings(string text)
        {
            return text.Replace("\r\n", "\n");
        }

        private static string ResolveRepoRoot()
        {
            // xUnit runs from Source/Parsek.Tests/bin/Debug/net472/ - walk up until
            // we find a directory containing both 'Source/' and '.claude/'.
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "Source"))
                    && Directory.Exists(Path.Combine(dir, ".claude")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException(
                "Could not locate the repo root from " + AppContext.BaseDirectory);
        }
    }
}
