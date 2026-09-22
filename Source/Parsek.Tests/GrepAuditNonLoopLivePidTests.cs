using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Parsek.Rendering;
using Xunit;

namespace Parsek.Tests
{
    public class GrepAuditNonLoopLivePidTests
    {
        [Fact]
        public void NonLoopLivePidAudit_AllForbiddenReadsStayDeleted()
        {
            string repoRoot = ResolveRepoRoot();
            string scriptPath = Path.Combine(repoRoot, "scripts", "grep-audit-non-loop-live-pid.ps1");
            Assert.True(File.Exists(scriptPath),
                "non-loop live-PID grep-audit script missing: " + scriptPath);

            string pwshPath;
            if (!TryFindExecutable("pwsh", out pwshPath)
                && !TryFindExecutable("pwsh.exe", out pwshPath))
            {
                RunManagedNonLoopLivePidAudit(repoRoot);
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
                Assert.True(finished, "non-loop live-PID grep-audit script did not finish within 60s.");
                proc.WaitForExit();

                string combined = "stdout:\n" + stdout + "\nstderr:\n" + stderr;
                Assert.True(proc.ExitCode == 0,
                    "non-loop live-PID grep-audit script exited with " + proc.ExitCode + ".\n" + combined);
            }
        }

        [Fact]
        public void NonLoopLivePidGuard_CounterResetsAndCountsDebugAttempts()
        {
            NonLoopLivePidGuard.ResetForTesting();
            Assert.Equal(0, NonLoopLivePidGuard.LivePidLookupAttemptsForTesting);

            NonLoopLivePidGuard.NonLoopRelativeLivePidLookupAttempted("test");
#if DEBUG
            Assert.Equal(1, NonLoopLivePidGuard.LivePidLookupAttemptsForTesting);
#else
            Assert.Equal(0, NonLoopLivePidGuard.LivePidLookupAttemptsForTesting);
#endif
        }

        // Must stay row-for-row identical to $checks / $requiredChecks in
        // scripts/grep-audit-non-loop-live-pid.ps1; only one arm runs per machine
        // in NonLoopLivePidAudit_AllForbiddenReadsStayDeleted, so
        // NonLoopLivePidAudit_ManagedArmMatchesPwshArm pins the rows together.
        // The scans below use IgnoreCase because Select-String without
        // -CaseSensitive matches case-insensitively; identical rows alone do not
        // make identical gates.
        private static readonly AuditCheck[] ForbiddenChecks =
        {
            new AuditCheck("Source/Parsek/IGhostPositioner.cs", "TryGetLiveAnchorWorldPosition", "IGhostPositioner live anchor API"),
            new AuditCheck("Source/Parsek/GhostPlaybackEngine.cs", "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid", "engine live-anchor appearance diagnostics"),
            new AuditCheck("Source/Parsek/ParsekFlight.cs", @"target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid", "recorded-relative flight playback live PID read"),
            new AuditCheck("Source/Parsek/GhostRenderTrace.cs", @"context\.AnchorVesselId\s*=|section\.AnchorVesselId", "recorded-relative trace section PID propagation"),
            new AuditCheck("Source/Parsek/ParsekKSC.cs", @"KscAnchorLookup|TryLookupKscAnchorFrame|FindVesselByPid\(anchorVesselId|anchorPid=|section\.anchorVesselId", "KSC Relative live PID playback"),
            new AuditCheck("Source/Parsek/GhostMapPresence.cs", @"ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFly\w*Point|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId", "map Relative live PID playback"),
        };

        private static readonly AuditCheck[] RequiredChecks =
        {
            new AuditCheck("Source/Parsek/ParsekFlight.cs", @"relativeLoopLiveAnchor\s*=\s*true", "loop-only LateUpdate live-anchor flag"),
            new AuditCheck("Source/Parsek/ParsekFlight.cs", @"NonLoopLivePidGuard\.NonLoopRelativeLivePidLookupAttempted", "non-loop LateUpdate live-PID DEBUG guard"),
        };

        [Fact]
        public void NonLoopLivePidAudit_ManagedArmPasses()
        {
            // Runs the managed arm on every host (not only when pwsh is absent),
            // so a Windows run exercises the same code path the Linux CI runner uses.
            RunManagedNonLoopLivePidAudit(ResolveRepoRoot());
        }

        [Fact]
        public void NonLoopLivePidAudit_ManagedArmMatchesPwshArm()
        {
            string scriptPath = Path.Combine(ResolveRepoRoot(), "scripts", "grep-audit-non-loop-live-pid.ps1");
            string script = File.ReadAllText(scriptPath).Replace("\r\n", "\n");

            const string requiredHeader = "$requiredChecks = @(";
            int forbiddenStart = script.IndexOf("$checks = @(", StringComparison.Ordinal);
            int requiredStart = script.IndexOf(requiredHeader, StringComparison.Ordinal);
            int loopStart = script.IndexOf("$violations = ", StringComparison.Ordinal);
            Assert.True(forbiddenStart >= 0 && requiredStart > forbiddenStart && loopStart > requiredStart,
                "could not locate $checks / $requiredChecks / $violations in " + scriptPath);

            AssertSameChecks("forbidden", ForbiddenChecks,
                ParsePwshChecks(script.Substring(forbiddenStart, requiredStart - forbiddenStart)));
            AssertSameChecks("required", RequiredChecks,
                ParsePwshChecks(script.Substring(requiredStart, loopStart - requiredStart)));
        }

        private static System.Collections.Generic.List<AuditCheck> ParsePwshChecks(string arrayText)
        {
            // Rows are @{ Path = "..."  Pattern = "..."  Label = "..." } hashtables.
            // Comment lines are dropped first so a quoted row in a comment cannot count.
            var code = new StringBuilder();
            foreach (string line in arrayText.Split('\n'))
            {
                if (!line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                    code.Append(line).Append('\n');
            }

            var rows = new System.Collections.Generic.List<AuditCheck>();
            var rowRegex = new Regex(
                @"@\{\s*Path\s*=\s*""(?<path>[^""]*)""\s*Pattern\s*=\s*""(?<pattern>[^""]*)""\s*Label\s*=\s*""(?<label>[^""]*)""\s*\}");
            foreach (Match m in rowRegex.Matches(code.ToString()))
            {
                string pattern = m.Groups["pattern"].Value;
                // PowerShell expands $ and ` inside double quotes; a pattern using
                // either would not be the literal the regex here reads.
                Assert.True(pattern.IndexOf('$') < 0 && pattern.IndexOf('`') < 0,
                    "pwsh pattern uses an expandable character; parse it by hand: " + pattern);
                rows.Add(new AuditCheck(m.Groups["path"].Value, pattern, m.Groups["label"].Value));
            }

            int declared = Regex.Matches(code.ToString(), @"@\{").Count;
            Assert.True(declared == rows.Count,
                string.Format("parsed {0} of {1} pwsh check rows; the row shape changed", rows.Count, declared));
            return rows;
        }

        private static void AssertSameChecks(
            string kind,
            AuditCheck[] managed,
            System.Collections.Generic.List<AuditCheck> pwsh)
        {
            Assert.True(pwsh.Count > 0, "no pwsh " + kind + " checks parsed");
            var diffs = new System.Collections.Generic.List<string>();
            int n = Math.Max(managed.Length, pwsh.Count);
            for (int i = 0; i < n; i++)
            {
                string m = i < managed.Length ? Describe(managed[i]) : "<absent>";
                string p = i < pwsh.Count ? Describe(pwsh[i]) : "<absent>";
                if (!string.Equals(m, p, StringComparison.Ordinal))
                    diffs.Add(string.Format("{0} row {1}:\n  managed: {2}\n  pwsh:    {3}", kind, i, m, p));
            }
            Assert.True(diffs.Count == 0,
                "managed and pwsh non-loop live-PID audit arms drifted:\n" + string.Join("\n", diffs));
        }

        private static string Describe(AuditCheck check)
        {
            return check.RelativePath + " | " + check.Pattern + " | " + check.Label;
        }

        private static void RunManagedNonLoopLivePidAudit(string repoRoot)
        {
            var forbiddenChecks = ForbiddenChecks;
            var requiredChecks = RequiredChecks;

            var violations = new System.Collections.Generic.List<string>();
            foreach (AuditCheck check in forbiddenChecks)
            {
                string path = Path.Combine(repoRoot, check.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    violations.Add("missing file: " + check.RelativePath);
                    continue;
                }

                Regex regex = new Regex(check.Pattern, RegexOptions.IgnoreCase);
                int lineNumber = 0;
                foreach (string line in File.ReadLines(path))
                {
                    lineNumber++;
                    if (regex.IsMatch(line))
                    {
                        violations.Add(string.Format(
                            "{0}: {1}:{2}: {3}",
                            check.Label,
                            check.RelativePath,
                            lineNumber,
                            line.Trim()));
                    }
                }
            }

            foreach (AuditCheck check in requiredChecks)
            {
                string path = Path.Combine(repoRoot, check.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    violations.Add("missing file: " + check.RelativePath);
                    continue;
                }

                Regex regex = new Regex(check.Pattern, RegexOptions.IgnoreCase);
                bool found = false;
                foreach (string line in File.ReadLines(path))
                {
                    if (regex.IsMatch(line))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    violations.Add(string.Format(
                        "missing required guard: {0}: {1}: pattern '{2}'",
                        check.Label,
                        check.RelativePath,
                        check.Pattern));
                }
            }

            Assert.True(
                violations.Count == 0,
                "managed non-loop live-PID audit failed:\n" + string.Join("\n", violations));
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

        private struct AuditCheck
        {
            public readonly string RelativePath;
            public readonly string Pattern;
            public readonly string Label;

            public AuditCheck(string relativePath, string pattern, string label)
            {
                RelativePath = relativePath;
                Pattern = pattern;
                Label = label;
            }
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
