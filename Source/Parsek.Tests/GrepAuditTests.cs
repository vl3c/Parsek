using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Allowlist-shaped grep-audit CI gates.
    ///
    /// <para><b>ERS/ELS</b> (Phase 3 of Rewind-to-Staging, design §11.7): runs
    /// <c>scripts/grep-audit-ers-els.ps1</c> against the current source tree and
    /// asserts exit 0 — i.e. every <c>RecordingStore.CommittedRecordings</c> and
    /// <c>Ledger.Actions</c> reader outside the allowlist is a build break.</para>
    ///
    /// <para><b>UI complexity mode</b> (Phase 8 of Basic / Advanced UI mode,
    /// design §13.4): runs <c>scripts/grep-audit-ui-complexity-mode.ps1</c> and
    /// asserts exit 0 — i.e. the mode vocabulary appearing in any file outside
    /// the UI / deferred-apply / settings allowlist is a build break. That is the
    /// mechanical enforcement of the design §9 visibility-only invariant.</para>
    ///
    /// <para>When <c>pwsh</c> is unavailable on PATH, each gate falls back to an
    /// equivalent managed scan (same patterns, same allowlist semantics) so the
    /// gate still runs instead of silently skipping — e.g. on a future CI runner
    /// image without PowerShell. Mirrors the fallback shape of the other
    /// GrepAudit* gate files.</para>
    /// </summary>
    public class GrepAuditTests
    {
        [Fact]
        public void GrepAudit_AllRawAccessIsAllowlisted()
        {
            RunGrepAuditScript("grep-audit-ers-els.ps1", RunManagedErsElsAudit);
        }

        [Fact]
        public void GrepAudit_UiComplexityModeVocabularyIsAllowlisted()
        {
            RunGrepAuditScript("grep-audit-ui-complexity-mode.ps1", RunManagedUiComplexityModeAudit);
        }

        private static void RunGrepAuditScript(string scriptFileName, Action<string> managedFallback)
        {
            // Cross-platform pwsh probe (PowerShell 7 ships on ubuntu-latest CI
            // runners, so this gate runs there too); when the binary is genuinely
            // absent from PATH, fall back to an equivalent managed scan so the
            // gate still runs instead of silently skipping. Mirrors the probe +
            // fallback shape of the other GrepAudit* gate files.
            string pwshPath;
            if (!TryFindExecutable("pwsh", out pwshPath)
                && !TryFindExecutable("pwsh.exe", out pwshPath))
            {
                managedFallback(ResolveRepoRoot());
                return;
            }

            string repoRoot = ResolveRepoRoot();
            string scriptPath = Path.Combine(repoRoot, "scripts", scriptFileName);
            Assert.True(File.Exists(scriptPath),
                "grep-audit script missing: " + scriptPath);

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
                Assert.True(finished,
                    "grep-audit script did not finish within 60s: " + scriptFileName);
                // Flush async reads.
                proc.WaitForExit();

                string combined = "stdout:\n" + stdout + "\nstderr:\n" + stderr;
                Assert.True(proc.ExitCode == 0,
                    "grep-audit script " + scriptFileName + " exited with "
                        + proc.ExitCode + ".\n" + combined);
            }
        }

        // Managed mirror of scripts/grep-audit-ers-els.ps1: the same two regexes,
        // one hit per line maximum (the script breaks after the first matching
        // pattern), allowlisted via scripts/ers-els-audit-allowlist.txt.
        // IgnoreCase mirrors PowerShell's -match, which is case-insensitive by
        // default — the script also fires on e.g. an instance-variable read
        // spelled 'ledger.Actions', and so must this fallback.
        private static void RunManagedErsElsAudit(string repoRoot)
        {
            var patterns = new[]
            {
                new Regex(@"\.CommittedRecordings\b", RegexOptions.IgnoreCase),
                new Regex(@"\bLedger\.Actions\b", RegexOptions.IgnoreCase),
            };
            RunManagedAllowlistAudit(
                repoRoot, "ers-els-audit-allowlist.txt", "ERS/ELS raw-access",
                line =>
                {
                    foreach (Regex pattern in patterns)
                    {
                        if (pattern.IsMatch(line)) return true;
                    }
                    return false;
                });
        }

        // Managed mirror of scripts/grep-audit-ui-complexity-mode.ps1: the same
        // five case-sensitive SUBSTRING tokens (deliberately not word-boundary
        // matches — see the script header), allowlisted via
        // scripts/ui-complexity-mode-audit-allowlist.txt.
        private static void RunManagedUiComplexityModeAudit(string repoRoot)
        {
            string[] tokens =
            {
                "UiComplexityMode",
                "UiSurfaceVisibility",
                "UiSurface",
                "uiComplexityMode",
                "IsSpawnControlReachable",
            };
            RunManagedAllowlistAudit(
                repoRoot, "ui-complexity-mode-audit-allowlist.txt", "UI complexity-mode",
                line =>
                {
                    foreach (string token in tokens)
                    {
                        if (line.IndexOf(token, StringComparison.Ordinal) >= 0) return true;
                    }
                    return false;
                });
        }

        // Shared allowlist-audit walk mirroring both scripts' semantics: scan
        // Source/Parsek/**/*.cs, compute repo-relative forward-slash paths, and
        // allow a hit when its file is an exact allowlist entry or under a
        // trailing-slash directory-prefix entry (both OrdinalIgnoreCase, matching
        // the scripts). Unlike the zero-reference GrepAudit* fallbacks, hits are
        // EXPECTED here — the allowlisted definitions alone guarantee a nonzero
        // count — so a zero total means the scan itself broke and fails loud
        // rather than passing vacuously.
        private static void RunManagedAllowlistAudit(
            string repoRoot, string allowlistFileName, string label, Func<string, bool> lineMatches)
        {
            string sourceRoot = Path.Combine(repoRoot, "Source", "Parsek");
            Assert.True(Directory.Exists(sourceRoot),
                "managed " + label + " audit: source root not found: " + sourceRoot);
            string allowlistPath = Path.Combine(repoRoot, "scripts", allowlistFileName);
            Assert.True(File.Exists(allowlistPath),
                "managed " + label + " audit: allowlist not found: " + allowlistPath);

            var allowedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allowedPrefixes = new List<string>();
            foreach (string rawLine in File.ReadLines(allowlistPath))
            {
                string entry = rawLine.Trim();
                if (entry.Length == 0) continue;
                if (entry.StartsWith("#", StringComparison.Ordinal)) continue;
                string normalized = entry.Replace('\\', '/');
                if (normalized.EndsWith("/", StringComparison.Ordinal))
                    allowedPrefixes.Add(normalized);
                else
                    allowedFiles.Add(normalized);
            }

            string repoRootNorm = repoRoot.Replace('\\', '/');
            var violations = new List<string>();
            int hitsTotal = 0;
            foreach (string path in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
            {
                string rel = path.Replace('\\', '/');
                if (rel.StartsWith(repoRootNorm, StringComparison.OrdinalIgnoreCase))
                    rel = rel.Substring(repoRootNorm.Length).TrimStart('/');

                bool allowed = allowedFiles.Contains(rel);
                if (!allowed)
                {
                    foreach (string prefix in allowedPrefixes)
                    {
                        if (rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            allowed = true;
                            break;
                        }
                    }
                }

                int lineNumber = 0;
                foreach (string line in File.ReadLines(path))
                {
                    lineNumber++;
                    if (!lineMatches(line)) continue;
                    hitsTotal++;
                    if (!allowed)
                    {
                        violations.Add(string.Format(
                            "{0}:{1}: {2}", rel, lineNumber, line.Trim()));
                    }
                }
            }

            Assert.True(violations.Count == 0,
                "managed " + label + " audit failed (un-allowlisted reference(s), total pattern hits: "
                    + hitsTotal + "):\n" + string.Join("\n", violations));
            Assert.True(hitsTotal > 0,
                "managed " + label + " audit saw ZERO pattern hits — the allowlisted definitions alone "
                    + "should match, so the scan is broken (wrong root or dead patterns), not clean.");
        }

        private static bool TryFindExecutable(string fileName, out string path)
        {
            path = null;
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
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
                    // Skip unreadable PATH entries.
                }
            }
            return false;
        }

        private static string ResolveRepoRoot()
        {
            // xUnit runs from Source/Parsek.Tests/bin/Debug/net472/ — walk up
            // until we find a directory containing 'scripts/' + 'Source/'.
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
