using System;
using System.IO;
using Xunit;
using Parsek.InGameTests;

namespace Parsek.Tests
{
    /// <summary>
    /// R5 dispatch fence. `InGameTestRunner` has four batch entry points and the
    /// isolated pair differs from the ordinary pair in WHICH TESTS EXECUTE, so an
    /// inverted or dropped branch is silent: the batch runs, prints an honest tally,
    /// and the tally is simply of the wrong population.
    ///
    /// WHY THIS FILE EXISTS, precisely. A mutation sweep over the first cut of R5
    /// found two defects that NO xUnit cell caught: swapping the branches inside the
    /// seam's `RunTestsImpl`, and dropping the isolated branch from `FireAutorun`'s
    /// RunAll case. Both survived because the dispatch calls into `InGameTestRunner`,
    /// which needs Unity, so the whole `if (isolated) ... else ...` was untestable
    /// wherever it appeared - and it appeared four times. The first was at least
    /// caught by the H21 scenario's live flight (its pinned `Using batch baseline
    /// slot ... for 2 restore-after-run test(s)` token is emitted only on the
    /// isolated path); the second was caught by nothing at all, because no committed
    /// spec drives the autorun path.
    ///
    /// The fix was to collapse all four copies into ONE
    /// `InGameTestRunner.RunBatchSelector`, whose DECISION half is the pure
    /// `ResolveBatchEntryPoint` covered below, and whose four-arm dispatch is fenced
    /// here by source text. That is the best available: the dispatch is one
    /// Unity-bound switch, and a source-text gate over one switch is proportionate
    /// where a gate over four scattered if/elses would not have been.
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
            Assert.Equal(expected,
                InGameTestRunner.ResolveBatchEntryPoint(category, isolated).ToString());
        }

        [Fact]
        public void ResolveBatchEntryPoint_IsolatedNeverCollapsesOntoTheOrdinaryArm()
        {
            // Guards the specific inversion the mutation sweep found: every isolated
            // resolution must differ from its ordinary counterpart. Written as a
            // relation rather than four literals so it still holds if the enum grows.
            foreach (string cat in new[] { null, "", "X" })
            {
                Assert.NotEqual(InGameTestRunner.ResolveBatchEntryPoint(cat, false),
                                InGameTestRunner.ResolveBatchEntryPoint(cat, true));
            }
        }

        // --- The dispatch (source-text fence) ---

        [Fact]
        public void RunBatchSelector_MapsEachArmToTheMatchingEntryPoint()
        {
            string body = Between(
                ReadSource("InGameTests", "InGameTestRunner.cs"),
                "internal void RunBatchSelector(",
                "public void RunSingle(");

            // Each isolated arm must reach an *IncludingFlightRestore method, and each
            // ordinary arm must not. Asserted as ordered pairs so a swap reds.
            int allIso = body.IndexOf("case BatchEntryPoint.RunAllIsolated:", StringComparison.Ordinal);
            int allOrd = body.IndexOf("case BatchEntryPoint.RunAll:", StringComparison.Ordinal);
            int catIso = body.IndexOf("case BatchEntryPoint.RunCategoryIsolated:", StringComparison.Ordinal);
            Assert.True(allIso >= 0 && allOrd >= 0 && catIso >= 0, body);

            Assert.Contains("RunAllIncludingFlightRestore();",
                            body.Substring(allIso, allOrd - allIso));
            Assert.DoesNotContain("IncludingFlightRestore",
                                  Between(body, "case BatchEntryPoint.RunAll:", "case BatchEntryPoint.RunCategoryIsolated:"));
            Assert.Contains("RunCategoryIncludingFlightRestore(category);",
                            Between(body, "case BatchEntryPoint.RunCategoryIsolated:", "default:"));
            // The default arm is the ordinary category path.
            Assert.DoesNotContain("IncludingFlightRestore",
                                  body.Substring(body.IndexOf("default:", StringComparison.Ordinal)));
        }

        [Fact]
        public void TheSeamRoutesThroughTheSingleDispatch()
        {
            string body = Between(
                ReadSource("TestCommands", "ParsekTestCommandAddon.cs"),
                "private void RunTestsImpl(", "private void CommitTreeImpl(");
            Assert.Contains("ownedRunner.RunBatchSelector(category, isolated);", body);
            // No direct entry-point call may survive here: that is exactly the copy the
            // mutation sweep inverted without any test noticing.
            Assert.DoesNotContain("IncludingFlightRestore", body);
            Assert.DoesNotContain("ownedRunner.RunCategory(", body);
            Assert.DoesNotContain("ownedRunner.RunAll(", body);
        }

        [Fact]
        public void EveryAutorunBranchRoutesThroughTheSingleDispatch()
        {
            // FireAutorun (IsAll + single-category) and AutorunMultiCategoryDriver are
            // three branches in one contiguous region; the IMGUI window below them is
            // the INTERACTIVE surface and is deliberately excluded.
            string region = Between(
                ReadSource("InGameTests", "TestRunnerShortcut.cs"),
                "private void FireAutorun()", "private bool MarkerWouldReconcile()");

            Assert.Equal(3, Count(region, "runner.RunBatchSelector("));
            Assert.Contains("runner.RunBatchSelector(null, isolated);", region);
            Assert.Equal(2, Count(region, "runner.RunBatchSelector(cat, isolated);"));
            Assert.DoesNotContain("IncludingFlightRestore", region);
            Assert.DoesNotContain("runner.RunCategory(", region);
            Assert.DoesNotContain("runner.RunAll(", region);
        }

        [Fact]
        public void TheInteractiveSurfacesStillCallTheEntryPointsDirectly()
        {
            // The other half of the contract, asserted so a future "tidy-up" that
            // routes the buttons through RunBatchSelector too is a deliberate change
            // rather than an accident. The operator picks the variant by which button
            // they press; there is no parsed argument to resolve.
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
