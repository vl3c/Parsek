using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// <para><b>Background threads</b>: runs
    /// <c>scripts/grep-audit-background-threads.ps1</c> and asserts exit 0, i.e.
    /// any thread-start site (<c>new Thread</c>, <c>Task.Run</c>,
    /// <c>Task.Factory.StartNew</c>, <c>new Task</c> / <c>ContinueWith</c>,
    /// <c>ThreadPool.*QueueUserWorkItem</c>,
    /// <c>Parallel.*</c>, <c>System.Threading.Timer</c> / <c>System.Timers.Timer</c>)
    /// in <c>Source/Parsek</c> outside the (empty) allowlist is a build break.
    /// KSP pins <c>CultureInfo.CreateSpecificCulture("en")</c> on the MAIN thread
    /// only, and the InvariantCulture rule in <c>.claude/CLAUDE.md</c> lets the
    /// culture-sensitive <c>ParsekLog.*</c> format specifiers stand on that pin;
    /// this gate is what keeps the "Parsek starts no threads" premise true.</para>
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

        [Fact]
        public void GrepAudit_SourceParsekStartsNoBackgroundThreads()
        {
            RunGrepAuditScript("grep-audit-background-threads.ps1", RunManagedBackgroundThreadsAudit);
        }

        [Fact]
        public void GrepAudit_GuiMockWriteSetIsUiOnly()
        {
            RunGrepAuditScript("grep-audit-gui-mock-writeset.ps1", RunManagedGuiMockWriteSetAudit);
        }

        [Fact]
        public void GrepAudit_GuiMockWriteSetScanIsNotVacuous()
        {
            // Anti-vacuity for the gate above, over SYNTHETIC lines rather than over the
            // tree: the gallery files' own headers name every store they do not touch, so
            // a scan that read comments would fire on the prose that documents the rule
            // and would have to be silenced by deleting the explanation.
            Assert.Empty(GuiMockLineViolations(
                "            // RecordingStore.CommittedRecordings is never read here."));
            Assert.Empty(GuiMockLineViolations(
                "        /// <para>Never calls MissionStore.Missions.</para>"));
            // A literal is not code either: a state's Covers keys are literals like
            // "RosterStatus.Lost", and the mask is what keeps them from reading as types.
            Assert.Empty(GuiMockLineViolations(
                "                new[] { \"RosterStatus.Lost\", \"KerbalEndState.Dead\" },"));
            // An allowlisted type is clean.
            Assert.Empty(GuiMockLineViolations(
                "            return MissionStructureListBuilder.Build(tree, structure);"));
        }

        [Fact]
        public void GrepAudit_GuiMockWriteSetCatchesEveryWriteTheDenylistMissed()
        {
            // THE REGRESSION CELLS. Each of these three passed the first version of this
            // gate - the denylist - and each is a real write into shared state. They are
            // pinned as NEGATIVE cases so the allowlist cannot quietly become a denylist
            // again.
            foreach (string line in new[]
                     {
                         "            Ledger.AddActions(actions);",
                         "            CrewReservationManager.ClearReplacementsInternal();",
                         "            RS.ResetForTesting();",
                         "            RecordingStore.ClearCommittedInternal();",
                         "            MissionStore.Missions.Clear();",
                         "            RouteStore.CommittedRoutes.Clear();",
                         "            EffectiveState.ComputeERS();",
                         "            GamePersistence.SaveGame(\"x\", \"y\", SaveMode.OVERWRITE);",
                     })
            {
                List<string> hits = GuiMockLineViolations(line);
                Assert.True(hits.Count > 0,
                    "the write-set gate accepts '" + line.Trim()
                    + "', which reaches shared state from a gallery file");
            }

            // And the ALIAS, which is how RS.ResetForTesting() got in: banned outright,
            // because an alias defeats a name-based gate by construction.
            List<string> aliasHits = GuiMockLineViolations(
                "using RS = Parsek.RecordingStore;");
            Assert.Single(aliasHits);
            Assert.StartsWith("[alias]", aliasHits[0], StringComparison.Ordinal);
        }

        [Fact]
        public void GrepAudit_GuiMockAllowlistMirrorsTheScriptsOwn()
        {
            // Two copies of one list, so a type added to the script and not here (or the
            // reverse) would make the managed fallback and the pwsh gate disagree - and on
            // a runner without pwsh only the fallback runs.
            string path = Path.Combine(ResolveRepoRoot(), "scripts",
                                       "grep-audit-gui-mock-writeset.ps1");
            Assert.True(File.Exists(path), "the script moved: " + path);
            string src = File.ReadAllText(path);
            int at = src.IndexOf("$allowedTypes = @(", StringComparison.Ordinal);
            Assert.True(at > 0, "the $allowedTypes initializer moved");
            int end = src.IndexOf("\n)", at, StringComparison.Ordinal);
            Assert.True(end > at, "the $allowedTypes initializer has no terminator");
            var scriptTypes = new List<string>();
            foreach (Match m in Regex.Matches(src.Substring(at, end - at), @"'([A-Za-z0-9_]+)'"))
                scriptTypes.Add(m.Groups[1].Value);

            Assert.Equal(
                GuiMockAllowedTypes.OrderBy(t => t, StringComparer.Ordinal).ToArray(),
                scriptTypes.OrderBy(t => t, StringComparer.Ordinal).ToArray());
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

        // Managed mirror of scripts/grep-audit-background-threads.ps1: the same
        // case-sensitive SUBSTRING tokens over RAW lines (comments included, like
        // the sibling gates), allowlisted via
        // scripts/background-threads-audit-allowlist.txt. Zero hits is the
        // healthy state here, so the shared walk runs with expectHits: false.
        private static void RunManagedBackgroundThreadsAudit(string repoRoot)
        {
            string[] tokens =
            {
                "new Thread(",
                "Task.Run(",
                "Task.Factory.StartNew(",
                "new Task(",
                ".ContinueWith(",
                "ThreadPool.QueueUserWorkItem(",
                "ThreadPool.UnsafeQueueUserWorkItem(",
                "Parallel.",
                "System.Threading.Timer",
                "System.Timers.Timer",
                "new Timer(",
            };
            RunManagedAllowlistAudit(
                repoRoot, "background-threads-audit-allowlist.txt", "background-thread",
                line =>
                {
                    foreach (string token in tokens)
                    {
                        if (line.IndexOf(token, StringComparison.Ordinal) >= 0) return true;
                    }
                    return false;
                },
                expectHits: false,
                failureHint:
                    "Parsek must run on KSP's main thread only: the CultureInfo.CreateSpecificCulture(\"en\") "
                    + "pin from HighLogic.Awake is PER-THREAD, so code on any other thread formats ParsekLog "
                    + "output under the OS culture. See the InvariantCulture rule under 'KSP API & code "
                    + "gotchas' in .claude/CLAUDE.md.");
        }

        // Shared allowlist-audit walk mirroring both scripts' semantics: scan
        // Source/Parsek/**/*.cs, compute repo-relative forward-slash paths, and
        // allow a hit when its file is an exact allowlist entry or under a
        // trailing-slash directory-prefix entry (both OrdinalIgnoreCase, matching
        // the scripts). With expectHits (the ERS/ELS and UI-mode gates) hits are
        // EXPECTED (the allowlisted definitions alone guarantee a nonzero count),
        // so a zero total means the scan itself broke and fails loud rather than
        // passing vacuously. A zero-reference gate (background threads) passes
        // expectHits: false, where zero hits is the healthy state; its scan is
        // kept honest by the source-root existence assert instead.
        private static void RunManagedAllowlistAudit(
            string repoRoot, string allowlistFileName, string label, Func<string, bool> lineMatches,
            bool expectHits = true, string failureHint = null)
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
                    + hitsTotal + ")."
                    + (failureHint == null ? string.Empty : "\n" + failureHint)
                    + "\n" + string.Join("\n", violations));
            if (expectHits)
            {
                Assert.True(hitsTotal > 0,
                    "managed " + label + " audit saw ZERO pattern hits — the allowlisted definitions alone "
                        + "should match, so the scan is broken (wrong root or dead patterns), not clean.");
            }
        }

        /// <summary>
        /// Every type a GUI-state-gallery file may reference, mirroring
        /// <c>$allowedTypes</c> in <c>scripts/grep-audit-gui-mock-writeset.ps1</c>. A cell
        /// below asserts the two lists are EQUAL, so neither can drift.
        ///
        /// <para>AN ALLOWLIST RATHER THAN A DENYLIST, and the reason is measured: the
        /// first version banned a list of store names and a review walked three writes
        /// straight through it - <c>Ledger.AddActions(...)</c>,
        /// <c>CrewReservationManager.ClearReplacementsInternal()</c> and
        /// <c>RS.ResetForTesting()</c> behind <c>using RS = Parsek.RecordingStore;</c>.
        /// A denylist can only ban the writers somebody thought of.</para>
        /// </summary>
        internal static readonly string[] GuiMockAllowedTypes =
        {
            "GuiMockSession", "GuiMockCatalogue", "GuiMockState", "GuiMockPayload",
            "GuiMockStructure", "GuiMockWitness", "GuiMockSuppressionSite",
            "GuiMockKerbalsStates", "GuiMockCareerStates", "GuiMockStructureStates",
            "MissionInputs", "RosterInputs", "RouteShape", "FlightShape",

            "TestCommandUiMock", "TestCommandUiAction", "TestCommandUiState",
            "TestCommandUiFind", "TestCommandCaptureScreenshot", "TestCommandDumpGuiTree",
            "TestCommandSaveGame", "TestCommandScene", "UiActionOp", "UiActionRect",
            "UiWindowSpec", "UiWindowHandle", "UiActionPending", "UiActionSettleOutcome",
            "GuiTreeDumpPollOutcome", "ParsedCommand", "DeferralBudget",
            "ParsekTestCommandAddon", "MockIntent",

            "KerbalsWindowUI", "KerbalsPresentation", "CareerStateWindowUI",
            "StructureListWindowUI", "ParsekUI", "UiComplexityMode", "UiSurface",
            "UiSurfaceVisibility",

            "KerbalsModule", "KerbalEndState", "GameAction", "GameActionType",
            "StrategyResource", "ContractsModule", "StrategiesModule", "FacilitiesModule",
            "MilestonesModule", "Game",
            "Recording", "RecordingTree", "BranchPoint", "BranchPointType", "PartEvent",
            "PartEventType", "TerminalState", "StructureStep", "StructureStepKind",
            "MissionStructure", "MissionStructureBuilder", "MissionStructureListBuilder",
            "MissionCompositionBuilder", "StructureLocationFormatter",
            "Route", "RouteStop", "RouteEndpoint", "RouteConnectionWindow",
            "RouteStructureListBuilder", "RouteEndpointLocationFormatter",

            "ParsekLog", "GuiTreeRecorder", "GuiTreeResult", "GuiTreeNode",
            "GuiTreeAssembler",

            "System", "Parsek", "Logistics", "Gallery", "TestCommands", "UI",
            "Action", "Func", "List", "Dictionary", "HashSet", "IEnumerable",
            "IReadOnlyList", "IReadOnlyCollection", "IReadOnlyDictionary",
            "KeyValuePair", "StringComparer", "StringComparison", "CultureInfo",
            "Exception", "InvalidOperationException", "ArgumentOutOfRangeException",
            "DateTime", "Math", "Enum", "StringBuilder", "Guid", "Globalization",
            "Rect", "Time", "UnityEngine", "Object",
        };

        /// <summary>The GUI-state-gallery write set, as absolute paths. Named once so the
        /// managed fallback and the negative cells cannot describe different sets.</summary>
        internal static List<string> GuiMockScanSet(string repoRoot)
        {
            string sourceRoot = Path.Combine(repoRoot, "Source", "Parsek");
            string galleryDir = Path.Combine(sourceRoot, "UI", "Gallery");
            Assert.True(Directory.Exists(galleryDir),
                "gui-mock write-set audit: gallery directory not found (this gate is "
                + "vacuous): " + galleryDir);
            var files = new List<string>(
                Directory.EnumerateFiles(galleryDir, "*.cs", SearchOption.AllDirectories));
            foreach (string rel in new[]
                     {
                         Path.Combine("TestCommands", "ParsekTestCommandAddon.UiMock.cs"),
                         Path.Combine("TestCommands", "TestCommandUiMock.cs"),
                     })
            {
                string p = Path.Combine(sourceRoot, rel);
                Assert.True(File.Exists(p),
                    "gui-mock write-set audit: scan-set file not found (this gate is "
                    + "vacuous): " + p);
                files.Add(p);
            }
            return files;
        }

        // The three regexes the script uses, mirrored. The LITERAL mask is not optional: a
        // catalogue state's Covers keys are literals like "RosterStatus.Lost", and without
        // it every one reads as a type reference.
        private static readonly Regex GuiMockTypeRef =
            new Regex(@"(?<![A-Za-z0-9_.])([A-Z][A-Za-z0-9_]*)\s*\.",
                      RegexOptions.CultureInvariant);
        private static readonly Regex GuiMockAliasUsing =
            new Regex(@"^\s*using\s+[A-Za-z_][A-Za-z0-9_]*\s*=",
                      RegexOptions.CultureInvariant);
        private static readonly Regex GuiMockStringLiteral =
            new Regex("\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.CultureInvariant);

        /// <summary>Strips a C# LINE comment, which is what makes this gate scannable at
        /// all: the gallery files' own headers explain which stores they do not touch, so
        /// a scan over raw text would fire on the prose that documents the rule.</summary>
        internal static string StripLineComment(string line)
        {
            if (line == null) return string.Empty;
            int cut = line.IndexOf("//", StringComparison.Ordinal);
            return cut >= 0 ? line.Substring(0, cut) : line;
        }

        /// <summary>
        /// Every un-allowlisted type reference and every alias on one source line, as the
        /// script would report them. Empty for a clean line.
        /// </summary>
        internal static List<string> GuiMockLineViolations(string rawLine)
        {
            var hits = new List<string>();
            string code = GuiMockStringLiteral.Replace(StripLineComment(rawLine), "\"\"");
            if (code.Trim().Length == 0) return hits;
            if (GuiMockAliasUsing.IsMatch(code))
            {
                hits.Add("[alias] " + code.Trim());
                return hits;
            }
            var allowed = new HashSet<string>(GuiMockAllowedTypes, StringComparer.Ordinal);
            foreach (Match m in GuiMockTypeRef.Matches(code))
            {
                string name = m.Groups[1].Value;
                if (allowed.Contains(name)) continue;
                hits.Add("[type] " + name);
            }
            return hits;
        }

        // Managed mirror of scripts/grep-audit-gui-mock-writeset.ps1: the same scan set,
        // the same allowlist, the same comment stripping and literal masking. INVERTED
        // from the allowlist gates above - this one scans NAMED FILES and allows NAMED
        // TYPES - so it does not reuse RunManagedAllowlistAudit.
        private static void RunManagedGuiMockWriteSetAudit(string repoRoot)
        {
            List<string> files = GuiMockScanSet(repoRoot);
            Assert.True(files.Count >= 5,
                "managed gui-mock write-set audit: only " + files.Count + " file(s) in the "
                + "scan set; the layout moved and this gate is vacuous.");

            string repoRootNorm = repoRoot.Replace('\\', '/');
            var violations = new List<string>();
            int referencesSeen = 0;
            var allowed = new HashSet<string>(GuiMockAllowedTypes, StringComparer.Ordinal);
            foreach (string path in files)
            {
                string rel = path.Replace('\\', '/');
                if (rel.StartsWith(repoRootNorm, StringComparison.OrdinalIgnoreCase))
                    rel = rel.Substring(repoRootNorm.Length).TrimStart('/');

                int lineNumber = 0;
                foreach (string raw in File.ReadLines(path))
                {
                    lineNumber++;
                    Assert.True(raw.IndexOf("/*", StringComparison.Ordinal) < 0,
                        "managed gui-mock write-set audit: block comment in " + rel + ":"
                        + lineNumber + "; the line-based stripper cannot see inside one.");
                    string code = GuiMockStringLiteral.Replace(
                        StripLineComment(raw), "\"\"");
                    if (code.Trim().Length == 0) continue;
                    referencesSeen += GuiMockTypeRef.Matches(code).Count;
                    foreach (string hit in GuiMockLineViolations(raw))
                        violations.Add(rel + ":" + lineNumber + ": " + hit);
                }
            }

            Assert.True(referencesSeen >= 100,
                "managed gui-mock write-set audit parsed only " + referencesSeen
                + " type reference(s); the parse broke and this gate is vacuous");
            Assert.True(violations.Count == 0,
                "managed gui-mock write-set audit failed: a GUI-state-gallery file names a "
                + "type outside the allowlist, or aliases one. A mocked view model must "
                + "reach NO save - every member the applier writes is a UI-layer field no "
                + "writer reads (docs/dev/design-gui-state-gallery.md section 7.5, layer "
                + "1).\n" + string.Join("\n", violations));
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
