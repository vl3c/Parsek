using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 5b (map-render cutover): regression gate locking the deletion of the typed-spine cutover
    /// flag + the legacy TracedPath side-channel. The typed PhaseChain spine is UNCONDITIONAL - the
    /// compile-time flag const, its class-level home, the in-game force seam, the flag-aware selector
    /// property, and the per-pid legacy side-channel dictionary were all removed (the old flag pins
    /// <c>PhaseSpineDriveFlag_DefaultsOn_TheCutoverFlip</c> /
    /// <c>PhaseSpineDriveActive_ConstCarriesTheCutover</c> and the rollback source gate
    /// <c>IsTracedPathOwnedThisFrame_LegacyElseBranch_RetainedForRollback_SourceGate</c> became THIS
    /// flag-GONE gate). The surviving predicates (<c>IsTracedPathOwnedThisFrame</c> /
    /// <c>IsDirectorTracedPathActiveFromIntent</c> / <c>IsDirectorDriveActive</c> /
    /// <c>IsDirectorTracking</c>) do not contain any forbidden literal, so they are never flagged. This
    /// test runs <c>scripts/grep-audit-map-render-phase-spine-drive.ps1</c> against the source tree and
    /// asserts exit 0 - i.e. ANY file under <c>Source/Parsek/</c> that resurrects a deleted identifier
    /// (a const, a field, a gate-clause read, or even a doc-comment) is a build break. When <c>pwsh</c>
    /// is unavailable (non-Windows CI) it falls back to an equivalent managed scan so the gate still
    /// runs. Mirrors <see cref="GrepAuditMapRenderDirectorDriveTests"/>.
    /// </summary>
    public class GrepAuditMapRenderPhaseSpineDriveTests
    {
        // The forbidden tokens (case-sensitive substrings): the deleted cutover flag const, the deleted
        // in-game force seam, the deleted selector property, and the deleted legacy side-channel dict.
        private static readonly string[] ForbiddenTokens =
        {
            "MapRenderPhaseSpineDrive",
            "ForceSpineDriveForTesting",
            "PhaseSpineDriveActive",
            "tracedPathByPid",
        };

        [Fact]
        public void MapRenderPhaseSpineDriveAudit_StaysDeletedRepoWide()
        {
            string repoRoot = ResolveRepoRoot();
            string scriptPath = Path.Combine(
                repoRoot, "scripts", "grep-audit-map-render-phase-spine-drive.ps1");
            Assert.True(File.Exists(scriptPath),
                "phase-spine-drive grep-audit script missing: " + scriptPath);

            string pwshPath;
            if (!TryFindExecutable("pwsh", out pwshPath)
                && !TryFindExecutable("pwsh.exe", out pwshPath))
            {
                RunManagedPhaseSpineDriveAudit(repoRoot);
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
                Assert.True(finished, "phase-spine-drive grep-audit script did not finish within 60s.");
                proc.WaitForExit();

                string combined = "stdout:\n" + stdout + "\nstderr:\n" + stderr;
                Assert.True(proc.ExitCode == 0,
                    "phase-spine-drive grep-audit script exited with " + proc.ExitCode + ".\n" + combined);
            }
        }

        private static void RunManagedPhaseSpineDriveAudit(string repoRoot)
        {
            string sourceRoot = Path.Combine(repoRoot, "Source", "Parsek");
            Assert.True(Directory.Exists(sourceRoot),
                "managed phase-spine-drive audit: source root not found: " + sourceRoot);

            var violations = new List<string>();
            foreach (string path in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
            {
                int lineNumber = 0;
                foreach (string line in File.ReadLines(path))
                {
                    lineNumber++;
                    foreach (string token in ForbiddenTokens)
                    {
                        if (line.IndexOf(token, StringComparison.Ordinal) >= 0)
                        {
                            string rel = path;
                            if (rel.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
                                rel = rel.Substring(repoRoot.Length).TrimStart('\\', '/');
                            violations.Add(string.Format(
                                "{0}:{1}: [{2}] {3}",
                                rel.Replace('\\', '/'), lineNumber, token, line.Trim()));
                            break;
                        }
                    }
                }
            }

            Assert.True(
                violations.Count == 0,
                "managed phase-spine-drive audit failed (resurrected deleted symbol(s) under "
                    + "Source/Parsek/):\n" + string.Join("\n", violations));
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
