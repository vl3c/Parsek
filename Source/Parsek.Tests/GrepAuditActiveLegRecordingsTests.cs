using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 8e S3b (map-render cutover): regression gate locking the deletion of the legacy polyline
    /// ownership publish <c>activeLegRecordings</c>. The drew set (<c>drewNonOrbitalLegRecordings</c>) is
    /// the single ownership source now; the legacy set and its S3a deletion-safety gate were removed. This
    /// test runs <c>scripts/grep-audit-active-leg-recordings.ps1</c> against the source tree and asserts
    /// exit 0 - i.e. ANY file under <c>Source/Parsek/</c> that resurrects the deleted identifier (a field, a
    /// publish, a read, or even a doc-comment) is a build break. When <c>pwsh</c> is unavailable (non-
    /// Windows CI) it falls back to an equivalent managed scan so the gate still runs.
    /// Mirrors <see cref="GrepAuditNonLoopLivePidTests"/>.
    /// </summary>
    public class GrepAuditActiveLegRecordingsTests
    {
        // The single forbidden token (case-sensitive substring): the deleted legacy ownership set.
        private const string ForbiddenToken = "activeLegRecordings";

        [Fact]
        public void ActiveLegRecordingsAudit_StaysDeletedRepoWide()
        {
            string repoRoot = ResolveRepoRoot();
            string scriptPath = Path.Combine(
                repoRoot, "scripts", "grep-audit-active-leg-recordings.ps1");
            Assert.True(File.Exists(scriptPath),
                "activeLegRecordings grep-audit script missing: " + scriptPath);

            string pwshPath;
            if (!TryFindExecutable("pwsh", out pwshPath)
                && !TryFindExecutable("pwsh.exe", out pwshPath))
            {
                RunManagedActiveLegRecordingsAudit(repoRoot);
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = pwshPath,
                Arguments = string.Format(
                    "-NoProfile -File \"{0}\" -RepoRoot \"{1}\"",
                    scriptPath, repoRoot),
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using (var proc = new Process { StartInfo = psi })
            {
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                bool finished = proc.WaitForExit(60_000);
                Assert.True(finished, "activeLegRecordings grep-audit script did not finish within 60s.");
                proc.WaitForExit();

                string combined = "stdout:\n" + stdout + "\nstderr:\n" + stderr;
                Assert.True(proc.ExitCode == 0,
                    "activeLegRecordings grep-audit script exited with " + proc.ExitCode + ".\n" + combined);
            }
        }

        private static void RunManagedActiveLegRecordingsAudit(string repoRoot)
        {
            string sourceRoot = Path.Combine(repoRoot, "Source", "Parsek");
            Assert.True(Directory.Exists(sourceRoot),
                "managed activeLegRecordings audit: source root not found: " + sourceRoot);

            var violations = new List<string>();
            foreach (string path in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
            {
                int lineNumber = 0;
                foreach (string line in File.ReadLines(path))
                {
                    lineNumber++;
                    if (line.IndexOf(ForbiddenToken, StringComparison.Ordinal) >= 0)
                    {
                        string rel = path;
                        if (rel.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
                            rel = rel.Substring(repoRoot.Length).TrimStart('\\', '/');
                        violations.Add(string.Format(
                            "{0}:{1}: {2}", rel.Replace('\\', '/'), lineNumber, line.Trim()));
                    }
                }
            }

            Assert.True(
                violations.Count == 0,
                "managed activeLegRecordings audit failed (resurrected '" + ForbiddenToken
                    + "' under Source/Parsek/):\n" + string.Join("\n", violations));
        }

        private static bool TryFindExecutable(string fileName, out string path)
        {
            path = null;
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate))
                    {
                        path = candidate;
                        return true;
                    }
                }
                catch
                {
                }
            }
            return false;
        }

        private static string ResolveRepoRoot()
        {
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
