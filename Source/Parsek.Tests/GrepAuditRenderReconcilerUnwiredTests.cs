using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 8 (map/TS render overhaul, migration plan section 10): regression gate locking the UNWIRING
    /// of the now-circular intent-vs-old-truth comparator. Through Phases 0-7 the end-of-frame
    /// <c>MapRenderProbe</c> called <c>GhostRenderReconciler.CheckIntentAgainstOldTruth</c> to compare the
    /// new spine's intent against the OLD path's rendered truth; once the spine drives the render that OLD
    /// truth IS the spine's own consequence, so the comparison became circular / self-confirming. The
    /// Phase-0 recorded-vs-rendered RenderParityOracle (a DISTINCT axis since Phase 0) is now the SOLE
    /// acceptance oracle, so the LIVE probe call site was removed.
    ///
    /// This is an UNWIRING, NOT a deletion of the type: <c>GhostRenderReconciler</c>, its pure predicates,
    /// and the <c>CheckIntentAgainstOldTruth</c> METHOD are KEPT (exercised by
    /// <see cref="GhostRenderReconcilerTests"/>). What must stay gone is any LIVE production CALL SITE. The
    /// forbidden token is the CALL form <c>.CheckIntentAgainstOldTruth(</c> (a leading dot + a trailing open
    /// paren): it matches an invocation but NOT the method DEFINITION (no leading dot) nor a doc-comment cref
    /// (no trailing paren), so the kept method + its kept doc-comments are never flagged while a resurrected
    /// live call site is. The audit scans only <c>Source/Parsek/</c> (production), so the xUnit
    /// <see cref="GhostRenderReconcilerTests"/> call sites (which legitimately drive the kept method) are out
    /// of scope.
    ///
    /// This test runs <c>scripts/grep-audit-render-reconciler-unwired.ps1</c> against the source tree and
    /// asserts exit 0 - i.e. ANY file under <c>Source/Parsek/</c> that resurrects a live call site is a build
    /// break. When <c>pwsh</c> is unavailable (non-Windows CI) it falls back to an equivalent managed scan so
    /// the gate still runs. Mirrors <see cref="GrepAuditMapRenderDirectorDriveTests"/>.
    /// </summary>
    public class GrepAuditRenderReconcilerUnwiredTests
    {
        // The single forbidden token (case-sensitive substring): the CALL form (leading dot + trailing open
        // paren). The method definition (no leading dot) and the doc-comment crefs (no trailing paren) do NOT
        // contain this literal and are therefore never flagged.
        private const string ForbiddenToken = ".CheckIntentAgainstOldTruth(";

        [Fact]
        public void RenderReconcilerComparator_StaysUnwiredRepoWide()
        {
            string repoRoot = ResolveRepoRoot();
            string scriptPath = Path.Combine(
                repoRoot, "scripts", "grep-audit-render-reconciler-unwired.ps1");
            Assert.True(File.Exists(scriptPath),
                "render-reconciler-unwired grep-audit script missing: " + scriptPath);

            string pwshPath;
            if (!TryFindExecutable("pwsh", out pwshPath)
                && !TryFindExecutable("pwsh.exe", out pwshPath))
            {
                RunManagedRenderReconcilerUnwiredAudit(repoRoot);
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
                Assert.True(finished, "render-reconciler-unwired grep-audit script did not finish within 60s.");
                proc.WaitForExit();

                string combined = "stdout:\n" + stdout + "\nstderr:\n" + stderr;
                Assert.True(proc.ExitCode == 0,
                    "render-reconciler-unwired grep-audit script exited with " + proc.ExitCode + ".\n" + combined);
            }
        }

        // The KEEP half of the Phase-8 contract: the unwiring removed only the LIVE call site, NOT the
        // method. The grep-audit forbids the CALL form; this asserts the DEFINITION (no leading dot) still
        // exists in production source, so a future "delete the method to trivially satisfy the gate" move
        // (which would also break GhostRenderReconcilerTests) is caught here too. Together the two facts pin
        // the exact intended end-state: method present, zero live call sites.
        [Fact]
        public void RenderReconcilerComparator_MethodIsKept_NotDeleted()
        {
            string repoRoot = ResolveRepoRoot();
            string reconcilerPath = Path.Combine(
                repoRoot, "Source", "Parsek", "MapRender", "GhostRenderReconciler.cs");
            Assert.True(File.Exists(reconcilerPath),
                "GhostRenderReconciler.cs missing: " + reconcilerPath);

            bool sawDefinition = false;
            foreach (string line in File.ReadLines(reconcilerPath))
            {
                // The method DEFINITION carries no leading dot, so it is NOT the forbidden call-form token.
                if (line.IndexOf("void CheckIntentAgainstOldTruth(", StringComparison.Ordinal) >= 0)
                {
                    sawDefinition = true;
                    break;
                }
            }

            Assert.True(sawDefinition,
                "the Phase-8 unwiring KEEPS the CheckIntentAgainstOldTruth method (exercised by "
                    + "GhostRenderReconcilerTests); only its LIVE call site is removed. The method definition "
                    + "must still be present in GhostRenderReconciler.cs.");
        }

        private static void RunManagedRenderReconcilerUnwiredAudit(string repoRoot)
        {
            string sourceRoot = Path.Combine(repoRoot, "Source", "Parsek");
            Assert.True(Directory.Exists(sourceRoot),
                "managed render-reconciler-unwired audit: source root not found: " + sourceRoot);

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
                "managed render-reconciler-unwired audit failed (resurrected '" + ForbiddenToken
                    + "' call site under Source/Parsek/):\n" + string.Join("\n", violations));
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
