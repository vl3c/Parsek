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

        private static void RunManagedNonLoopLivePidAudit(string repoRoot)
        {
            var forbiddenChecks = new[]
            {
                new AuditCheck("Source/Parsek/IGhostPositioner.cs", "TryGetLiveAnchorWorldPosition", "IGhostPositioner live anchor API"),
                new AuditCheck("Source/Parsek/GhostPlaybackEngine.cs", "DescribeAppearanceLiveAnchorContext|TryGetLiveAnchorWorldPosition|legacyAnchorPid", "engine live-anchor appearance diagnostics"),
                new AuditCheck("Source/Parsek/ParsekFlight.cs", @"target\.Section\.anchorVesselId|FindVesselByPid\(section\.anchorVesselId|FindVesselByPid\(e\.anchorVesselId|legacyAnchorPid", "recorded-relative flight playback live PID read"),
                new AuditCheck("Source/Parsek/GhostRenderTrace.cs", @"context\.AnchorVesselId\s*=|section\.AnchorVesselId", "recorded-relative trace section PID propagation"),
                new AuditCheck("Source/Parsek/ParsekKSC.cs", @"KscAnchorLookup|TryLookupKscAnchorFrame|FindVesselByPid\(anchorVesselId|anchorPid=|section\.anchorVesselId", "KSC Relative live PID playback"),
                new AuditCheck("Source/Parsek/GhostMapPresence.cs", @"ResolveAnchorInScene|AnchorResolvableForTesting|TryResolveActiveReFlyBodyFixedPrimaryPoint|FindVesselByPid\(resolution\.AnchorPid|section\.anchorVesselId|currentSection\.Value\.anchorVesselId", "map Relative live PID playback"),
            };
            var requiredChecks = new[]
            {
                new AuditCheck("Source/Parsek/ParsekFlight.cs", @"relativeLoopLiveAnchor\s*=\s*true", "loop-only LateUpdate live-anchor flag"),
                new AuditCheck("Source/Parsek/ParsekFlight.cs", @"NonLoopLivePidGuard\.NonLoopRelativeLivePidLookupAttempted", "non-loop LateUpdate live-PID DEBUG guard"),
            };

            var violations = new System.Collections.Generic.List<string>();
            foreach (AuditCheck check in forbiddenChecks)
            {
                string path = Path.Combine(repoRoot, check.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    violations.Add("missing file: " + check.RelativePath);
                    continue;
                }

                Regex regex = new Regex(check.Pattern);
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

                Regex regex = new Regex(check.Pattern);
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
