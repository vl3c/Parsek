using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 8e S4 (map-render cutover): regression gate locking the deletion of the
    /// <c>mapRenderDirectorDrive</c> setting / gate. The modular Director render pipeline is now
    /// UNCONDITIONAL - the setting, its persistence, its UI, and every <c>&amp;&amp; ...mapRenderDirectorDrive</c>
    /// gate clause were removed. The per-leg DECISION predicates (<c>IsTracedPathOwnedThisFrame</c> /
    /// <c>IsDirectorDriveActive</c> / <c>IsDirectorTracking</c>) and the kept no-conic / suppressed-icon
    /// FLOOR mechanism (<c>ghostsWithSuppressedIcon</c> / the <c>directorDriveActive</c> local)
    /// SURVIVE - none of them contain the forbidden literal substring, so they are never flagged. This test
    /// runs <c>scripts/grep-audit-map-render-director-drive.ps1</c> against the source tree and asserts
    /// exit 0 - i.e. ANY file under <c>Source/Parsek/</c> that resurrects the deleted identifier (a field, a
    /// key, a gate-clause read, or even a doc-comment) is a build break. When <c>pwsh</c> is unavailable
    /// (non-Windows CI) it falls back to an equivalent managed scan so the gate still runs.
    /// Mirrors <see cref="GrepAuditActiveLegRecordingsTests"/>.
    /// </summary>
    public class GrepAuditMapRenderDirectorDriveTests
    {
        // The single forbidden token (case-sensitive substring): the deleted director-drive gate setting.
        private const string ForbiddenToken = "mapRenderDirectorDrive";

        [Fact]
        public void MapRenderDirectorDriveAudit_StaysDeletedRepoWide()
        {
            string repoRoot = ResolveRepoRoot();
            string scriptPath = Path.Combine(
                repoRoot, "scripts", "grep-audit-map-render-director-drive.ps1");
            Assert.True(File.Exists(scriptPath),
                "mapRenderDirectorDrive grep-audit script missing: " + scriptPath);

            string pwshPath;
            if (!TryFindExecutable("pwsh", out pwshPath)
                && !TryFindExecutable("pwsh.exe", out pwshPath))
            {
                RunManagedMapRenderDirectorDriveAudit(repoRoot);
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
                Assert.True(finished, "mapRenderDirectorDrive grep-audit script did not finish within 60s.");
                proc.WaitForExit();

                string combined = "stdout:\n" + stdout + "\nstderr:\n" + stderr;
                Assert.True(proc.ExitCode == 0,
                    "mapRenderDirectorDrive grep-audit script exited with " + proc.ExitCode + ".\n" + combined);
            }
        }

        private static void RunManagedMapRenderDirectorDriveAudit(string repoRoot)
        {
            string sourceRoot = Path.Combine(repoRoot, "Source", "Parsek");
            Assert.True(Directory.Exists(sourceRoot),
                "managed mapRenderDirectorDrive audit: source root not found: " + sourceRoot);

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
                "managed mapRenderDirectorDrive audit failed (resurrected '" + ForbiddenToken
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
