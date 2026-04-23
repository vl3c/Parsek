using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 3 of Rewind-to-Staging (design §11.7): regression harness for the
    /// ERS/ELS grep-audit CI gate. Runs <c>scripts/grep-audit-ers-els.ps1</c>
    /// against the current source tree and asserts exit 0 — i.e. every
    /// <c>RecordingStore.CommittedRecordings</c> and <c>Ledger.Actions</c>
    /// reader outside the allowlist is a build break.
    /// </summary>
    public class GrepAuditTests
    {
        [Fact]
        public void GrepAudit_AllRawAccessIsAllowlisted()
        {
            // Non-Windows CI runners may not have pwsh; also skip if the binary
            // isn't on PATH. This is a regression gate, not a correctness test
            // (the script itself is not platform-specific but the caller is).
            if (!IsWindowsWithPwsh())
            {
                // xUnit v2.4 has no first-class Skip; emitting via stdout keeps
                // the intent auditable without failing the suite.
                Console.WriteLine("GrepAuditTests: skipped (pwsh not available).");
                return;
            }

            string repoRoot = ResolveRepoRoot();
            string scriptPath = Path.Combine(repoRoot, "scripts", "grep-audit-ers-els.ps1");
            Assert.True(File.Exists(scriptPath),
                "grep-audit script missing: " + scriptPath);

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
                Assert.True(finished, "grep-audit script did not finish within 60s.");
                // Flush async reads.
                proc.WaitForExit();

                string combined = "stdout:\n" + stdout + "\nstderr:\n" + stderr;
                Assert.True(proc.ExitCode == 0,
                    "grep-audit script exited with " + proc.ExitCode + ".\n" + combined);
            }
        }

        private static bool IsWindowsWithPwsh()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string candidate = Path.Combine(dir, "pwsh.exe");
                    if (File.Exists(candidate)) return true;
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
