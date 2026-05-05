using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Parsek.Rendering;
using Xunit;

namespace Parsek.Tests
{
    public class GrepAuditNonLoopLivePidTests
    {
        [Fact]
        public void NonLoopLivePidAudit_AllForbiddenReadsStayDeleted()
        {
            if (!IsWindowsWithPwsh())
            {
                Console.WriteLine("GrepAuditNonLoopLivePidTests: skipped (pwsh not available).");
                return;
            }

            string repoRoot = ResolveRepoRoot();
            string scriptPath = Path.Combine(repoRoot, "scripts", "grep-audit-non-loop-live-pid.ps1");
            Assert.True(File.Exists(scriptPath),
                "non-loop live-PID grep-audit script missing: " + scriptPath);

            var psi = new ProcessStartInfo
            {
                FileName = "pwsh",
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

        private static bool IsWindowsWithPwsh()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string candidate = Path.Combine(dir, "pwsh.exe");
                    if (File.Exists(candidate)) return true;
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
