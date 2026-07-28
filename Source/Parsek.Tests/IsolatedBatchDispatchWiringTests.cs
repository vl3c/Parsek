using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;
using Parsek.InGameTests;

namespace Parsek.Tests
{
    /// <summary>
    /// R5 dispatch fence. `InGameTestRunner` has four batch entry points and the
    /// isolated pair differs from the ordinary pair in WHICH TESTS EXECUTE, so an
    /// inverted, dropped or emptied branch is silent: the batch runs, prints an honest
    /// tally, and the tally is of the wrong population.
    ///
    /// WHY THIS FILE EXISTS. A mutation sweep found defects that no xUnit cell caught,
    /// because the dispatch calls into `InGameTestRunner`, which needs Unity, so the
    /// whole `if (isolated) ... else` was untestable wherever it appeared - and it
    /// appeared four times. The fix was to collapse all four into
    /// `InGameTestRunner.RunBatchSelector`, whose DECISION half is the pure
    /// `ResolveBatchEntryPoint` covered below and whose dispatch half is fenced here.
    ///
    /// WHY THIS FILE LOOKS LIKE THIS. Its first cut was positional - it sliced the
    /// method body between `IndexOf` anchors. An adversarial review then showed that
    /// design was simultaneously TOO WEAK and TOO TIGHT:
    ///
    ///   too weak - it asserted the ORDINARY arms only negatively (`DoesNotContain`
    ///     "IncludingFlightRestore"), which an EMPTY arm satisfies, and it never
    ///     checked the ARGUMENTS feeding the resolver. Changing
    ///     `ResolveBatchEntryPoint(category, isolated)` to `(category, false)` made
    ///     the whole feature inert on every unattended route with the suite green.
    ///   too tight - reordering the switch cases into enum-declaration order threw
    ///     ArgumentOutOfRangeException (the slicing assumed RunAllIsolated preceded
    ///     RunAll); renaming the `category` parameter red; and adding a COMMENT that
    ///     merely mentioned IncludingFlightRestore red.
    ///
    /// So the gate now parses the switch STRUCTURALLY: comments are stripped, arms are
    /// matched by case label in any order, and the parameter names are read off the
    /// signature rather than assumed. Each arm is asserted POSITIVELY - what it must
    /// call - so "empty" and "calls the wrong one" both red.
    ///
    /// Pattern: DestinationLoiterTrimWiringTests (xUnit runs from
    /// Source/Parsek.Tests/bin/Debug/net472/ -> 5 ".." segments to the repo root).
    /// </summary>
    public class IsolatedBatchDispatchWiringTests
    {
        private static string ReadSource(params string[] relParts)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot, Path.Combine("Source", "Parsek"),
                                       Path.Combine(relParts));
            Assert.True(File.Exists(path), $"source not found at {path}");
            return File.ReadAllText(path);
        }

        /// <summary>Strip // and /* */ comments so prose cannot satisfy or violate a gate.</summary>
        private static string StripComments(string src)
        {
            src = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(src, @"//[^\n]*", " ");
        }

        private static string Between(string src, string startAnchor, string endAnchor)
        {
            int a = src.IndexOf(startAnchor, StringComparison.Ordinal);
            Assert.True(a >= 0, $"start anchor not found: {startAnchor}");
            int b = src.IndexOf(endAnchor, a, StringComparison.Ordinal);
            Assert.True(b > a, $"end anchor not found after start: {endAnchor}");
            return src.Substring(a, b - a);
        }

        private static int Count(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
            {
                n++;
                i += needle.Length;
            }
            return n;
        }

        private static string RunnerSource() => ReadSource("InGameTests", "InGameTestRunner.cs");

        /// <summary>
        /// The `RunBatchSelector` body, comments stripped, plus its two parameter names
        /// read off the signature so a rename cannot false-red the arm assertions.
        /// </summary>
        private static void ReadDispatch(out string body, out string categoryParam,
                                         out string isolatedParam)
        {
            string src = StripComments(RunnerSource());
            var sig = Regex.Match(src,
                @"internal\s+void\s+RunBatchSelector\s*\(\s*string\s+(?<cat>\w+)\s*,\s*bool\s+(?<iso>\w+)\s*\)");
            Assert.True(sig.Success,
                "RunBatchSelector(string, bool) signature not found - the dispatch was "
                + "renamed or resignatured, and every assertion below would be vacuous");
            categoryParam = sig.Groups["cat"].Value;
            isolatedParam = sig.Groups["iso"].Value;
            int start = sig.Index;
            int end = src.IndexOf("public void RunSingle(", start, StringComparison.Ordinal);
            Assert.True(end > start, "RunSingle terminator not found after RunBatchSelector");
            body = src.Substring(start, end - start);
        }

        /// <summary>case-label -> the statements of that arm, order-independent.</summary>
        private static Dictionary<string, string> SwitchArms(string body)
        {
            var arms = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(body,
                @"case\s+BatchEntryPoint\.(?<label>\w+)\s*:(?<arm>.*?)break\s*;",
                RegexOptions.Singleline))
            {
                arms[m.Groups["label"].Value] = m.Groups["arm"].Value;
            }
            var d = Regex.Match(body, @"\bdefault\s*:(?<arm>.*?)break\s*;", RegexOptions.Singleline);
            if (d.Success)
                arms["default"] = d.Groups["arm"].Value;
            return arms;
        }

        // --- The pure decision ---

        // Expected is passed by NAME, not as the enum value: the enum is internal and
        // an xUnit test method must be public, so a typed parameter would leak it.
        [Theory]
        [InlineData(null, false, "RunAll")]
        [InlineData(null, true, "RunAllIsolated")]
        [InlineData("", false, "RunAll")]
        [InlineData("", true, "RunAllIsolated")]
        [InlineData("SceneExitMerge", false, "RunCategory")]
        [InlineData("SceneExitMerge", true, "RunCategoryIsolated")]
        public void ResolveBatchEntryPoint_MapsEveryCombination(
            string category, bool isolated, string expected)
        {
            // NOTE on the "" rows: the RESOLVER treats absent and empty alike, which is
            // fine because the SEAM rejects a written-but-empty category before it ever
            // gets here (TestCommandRunTests.IsEmptyCategoryArg). These rows pin the
            // resolver's defensive behaviour, not a reachable seam shape.
            Assert.Equal(expected,
                InGameTestRunner.ResolveBatchEntryPoint(category, isolated).ToString());
        }

        [Fact]
        public void ResolveBatchEntryPoint_IsolatedNeverCollapsesOntoTheOrdinaryArm()
        {
            foreach (string cat in new[] { null, "", "X" })
            {
                Assert.NotEqual(InGameTestRunner.ResolveBatchEntryPoint(cat, false),
                                InGameTestRunner.ResolveBatchEntryPoint(cat, true));
            }
        }

        // --- The dispatch (structural source fence) ---

        [Fact]
        public void RunBatchSelector_FeedsTheResolverTheRealArguments()
        {
            // THE hole the review found: every arm can be perfect while the resolver is
            // handed a constant, which makes the feature inert on every unattended route.
            string body, cat, iso;
            ReadDispatch(out body, out cat, out iso);
            Assert.Matches(
                new Regex(@"ResolveBatchEntryPoint\s*\(\s*" + Regex.Escape(cat)
                          + @"\s*,\s*" + Regex.Escape(iso) + @"\s*\)"),
                body);
        }

        [Fact]
        public void RunBatchSelector_MapsEachArmToTheMatchingEntryPoint()
        {
            string body, cat, iso;
            ReadDispatch(out body, out cat, out iso);
            var arms = SwitchArms(body);

            // All four populations must be present. An arm that is MISSING would fall
            // through to default and an arm that is EMPTY is a silent no-op, so both the
            // key and its content are asserted.
            Assert.True(arms.ContainsKey("RunAllIsolated"), "no RunAllIsolated arm");
            Assert.True(arms.ContainsKey("RunAll"), "no RunAll arm");
            Assert.True(arms.ContainsKey("RunCategoryIsolated"), "no RunCategoryIsolated arm");
            Assert.True(arms.ContainsKey("default"), "no default arm");

            // POSITIVE assertions, the review's F2: `DoesNotContain` alone is satisfied
            // by an empty arm, which reported OK over a batch that never started.
            Assert.Contains("RunAllIncludingFlightRestore();", arms["RunAllIsolated"]);
            Assert.Contains("RunAll();", arms["RunAll"]);
            Assert.Contains("RunCategoryIncludingFlightRestore(" + cat + ");",
                            arms["RunCategoryIsolated"]);
            Assert.Contains("RunCategory(" + cat + ");", arms["default"]);

            // And the ordinary arms must not reach an isolated entry point.
            Assert.DoesNotContain("IncludingFlightRestore", arms["RunAll"]);
            Assert.DoesNotContain("IncludingFlightRestore", arms["default"]);
        }

        [Fact]
        public void RunBatchSelector_LogsTheAlreadyRunningGuardSkip()
        {
            // CLAUDE.md requires every guard-condition skip logged. Each entry point
            // opens with `if (isRunning) return;`, so an unconditional dispatch log
            // would assert a batch start that did not happen.
            string body, cat, iso;
            ReadDispatch(out body, out cat, out iso);
            Assert.Contains("if (isRunning)", body);
            Assert.Contains("batch dispatch SKIPPED", body);
        }

        [Fact]
        public void TheSeamRoutesThroughTheSingleDispatch()
        {
            string body = StripComments(Between(
                ReadSource("TestCommands", "ParsekTestCommandAddon.cs"),
                "private void RunTestsImpl(", "private void CommitTreeImpl("));

            Assert.Contains("ownedRunner.RunBatchSelector(category, isolated);", body);
            Assert.DoesNotContain("IncludingFlightRestore", body);
            Assert.DoesNotContain("ownedRunner.RunCategory(", body);
            Assert.DoesNotContain("ownedRunner.RunAll(", body);
        }

        [Fact]
        public void TheSeamParsesAndRejectsBothArgsFailClosed()
        {
            // The review's F6: the dispatch line was fenced but nothing above it, so
            // three separate ways of un-closing the fail-closed path all survived -
            // rejecting with OK, forgetting the `return;` (which falls through to an
            // ORDINARY batch, the exact silent fallback the feature exists to prevent),
            // and parsing the wrong argument.
            string body = StripComments(Between(
                ReadSource("TestCommands", "ParsekTestCommandAddon.cs"),
                "private void RunTestsImpl(", "private void CommitTreeImpl("));

            Assert.Contains("TryParseIsolatedArg(isolatedRaw, out isolated)", body);
            Assert.Contains("IsEmptyCategoryArg(category)", body);
            // Two independent reject arms, each terminal.
            Assert.Equal(2, Count(body, "SetExecResult(\"REJECTED\""));
            Assert.Equal(2, Count(body, "return;"));
            // The reject arms must precede the dispatch, or a fall-through would run the
            // batch anyway with the verdict overwritten by the later PendingVerdict.
            Assert.True(
                body.IndexOf("SetExecResult(\"REJECTED\"", StringComparison.Ordinal)
                < body.IndexOf("RunBatchSelector(", StringComparison.Ordinal),
                "a reject arm must come BEFORE the dispatch");
        }

        [Fact]
        public void EveryAutorunBranchRoutesThroughTheSingleDispatch()
        {
            string region = StripComments(Between(
                ReadSource("InGameTests", "TestRunnerShortcut.cs"),
                "private void FireAutorun()", "private bool MarkerWouldReconcile()"));

            Assert.Equal(3, Count(region, "runner.RunBatchSelector("));
            Assert.Contains("runner.RunBatchSelector(null, isolated);", region);
            Assert.Equal(2, Count(region, "runner.RunBatchSelector(cat, isolated);"));
            Assert.DoesNotContain("IncludingFlightRestore", region);
            Assert.DoesNotContain("runner.RunCategory(", region);
            Assert.DoesNotContain("runner.RunAll(", region);

            // The review's F4/F5: the flag must be READ from the parsed config and
            // HANDED to the multi-category driver. Both survived as one-token mutations.
            Assert.Contains("bool isolated = autorunConfig.Isolated;", region);
            Assert.Contains("AutorunMultiCategoryDriver(autorunConfig.Categories, exitArmed, isolated)",
                            region);
        }

        [Fact]
        public void TheAutorunEnvContractIsPinned()
        {
            // These three names are a CROSS-PROCESS wire contract with harness/run.py.
            // A typo in either half is silent: the C# reads nothing, the batch runs the
            // ordinary filter, and every restore-backed test reports Skipped - which
            // reads as a Parsek defect rather than a typo. Free to pin, so pin it.
            Assert.Equal("PARSEK_AUTORUN_TESTS", TestRunnerShortcut.EnvTestsVar);
            Assert.Equal("PARSEK_AUTORUN_EXIT", TestRunnerShortcut.EnvExitVar);
            Assert.Equal("PARSEK_AUTORUN_ISOLATED", TestRunnerShortcut.EnvIsolatedVar);

            // And the env vars must reach Parse in the declared order: swapping the last
            // two would make PARSEK_AUTORUN_EXIT arm isolation and vice versa.
            string parse = StripComments(ReadSource("InGameTests", "TestRunnerShortcut.cs"));
            Assert.Contains("AutorunHooks.Parse(testsVar, exitVar, isolatedVar)", parse);
            Assert.Contains("GetEnvironmentVariable(EnvTestsVar)", parse);
            Assert.Contains("GetEnvironmentVariable(EnvExitVar)", parse);
            Assert.Contains("GetEnvironmentVariable(EnvIsolatedVar)", parse);
        }

        [Fact]
        public void TheIsolatedFlagRendersOneWayInEveryLogLine()
        {
            // Two spellings were live in one feature: Bool(isolated) -> "true" at the
            // seam, {isolated} -> "True" in the runner and all four autorun lines. Any
            // logContract token or grep written against one silently misses the other.
            foreach (var rel in new[] { new[] { "InGameTests", "TestRunnerShortcut.cs" },
                                        new[] { "InGameTests", "InGameTestRunner.cs" } })
            {
                string src = StripComments(ReadSource(rel));
                Assert.DoesNotContain("isolated={isolated}", src);
                Assert.DoesNotContain("isolated={autorunConfig.Isolated}", src);
            }
        }

        [Fact]
        public void TheInteractiveSurfacesStillCallTheEntryPointsDirectly()
        {
            // The other half of the contract, asserted so a future "tidy-up" that routes
            // the buttons through RunBatchSelector too is deliberate rather than
            // accidental. The operator picks the variant by which button they press.
            string shortcut = ReadSource("InGameTests", "TestRunnerShortcut.cs");
            string ui = ReadSource("UI", "TestRunnerUI.cs");
            Assert.Contains("runner.RunAllIncludingFlightRestore();", shortcut);
            Assert.Contains("runner.RunCategoryIncludingFlightRestore(cat);", shortcut);
            Assert.Contains("testRunner.RunAllIncludingFlightRestore();", ui);
            Assert.Contains("testRunner.RunCategoryIncludingFlightRestore(category);", ui);
            Assert.DoesNotContain("RunBatchSelector", ui);
        }
    }
}
