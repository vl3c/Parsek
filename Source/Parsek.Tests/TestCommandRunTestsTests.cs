using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// P5.6 coverage for the two-phase RunTests result payload. When the owned batch
    /// finishes, the terminal response must carry passed/failed/skipped and the fixed
    /// results file name the orchestrator tails. Fails if a count key drifts or the
    /// results file name changes out from under the harness.
    /// </summary>
    public class TestCommandRunTestsTests
    {
        private static string Val(List<KeyValuePair<string, string>> p, string key)
            => p.First(kv => kv.Key == key).Value;

        [Fact]
        public void BuildResultPayload_CarriesCountsAndResultsFile()
        {
            var p = TestCommandRunTests.BuildResultPayload(passed: 42, failed: 0, skipped: 3);
            Assert.Equal("42", Val(p, "passed"));
            Assert.Equal("0", Val(p, "failed"));
            Assert.Equal("3", Val(p, "skipped"));
            Assert.Equal("parsek-test-results.txt", Val(p, "results"));
        }

        [Fact]
        public void BuildResultPayload_KeyOrderIsStable()
        {
            var p = TestCommandRunTests.BuildResultPayload(1, 2, 3);
            Assert.Equal(new[] { "passed", "failed", "skipped", "results" }, p.Select(kv => kv.Key).ToArray());
        }

        [Fact]
        public void ResultsFileName_IsTheDocumentedConstant()
        {
            Assert.Equal("parsek-test-results.txt", TestCommandRunTests.ResultsFileName);
        }

        // --- R5: the `isolated` arg ---

        // An ABSENT arg is the ordinary path. This is the whole back-compatibility
        // claim: every spec written before R5 omits the arg and must keep routing to
        // RunCategory / RunAll. Fails if the default ever flips to isolated, which
        // would silently change the admission filter for 38 committed specs.
        [Fact]
        public void TryParseIsolatedArg_Absent_IsOrdinaryPath()
        {
            bool isolated;
            Assert.True(TestCommandRunTests.TryParseIsolatedArg(null, out isolated));
            Assert.False(isolated);
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData("false", false)]
        public void TryParseIsolatedArg_AcceptsTheTwoWireLiterals(string raw, bool expected)
        {
            bool isolated;
            Assert.True(TestCommandRunTests.TryParseIsolatedArg(raw, out isolated));
            Assert.Equal(expected, isolated);
        }

        // FAIL-CLOSED, and case-sensitively so. "True" is the specific spelling a TOML
        // bool produces on the wire (run.py::encode_value is str(value), and Python's
        // str(True) == "True"), so an author who writes `isolated = true` instead of
        // `isolated = "true"` gets a REJECTED verdict here rather than a silent
        // fallback to the ordinary path. A silent fallback is the dangerous shape: the
        // batch would run the non-isolated filter, emit an all-skipped tally, and the
        // failure would read as a Parsek defect instead of a spec typo.
        //
        // "" is rejected rather than read as absent: a written-but-empty value is a
        // typo, and treating it as "the author did not ask for isolation" would hide it.
        [Theory]
        [InlineData("True")]
        [InlineData("False")]
        [InlineData("TRUE")]
        [InlineData("1")]
        [InlineData("0")]
        [InlineData("yes")]
        [InlineData(" true")]
        [InlineData("true ")]
        [InlineData("")]
        public void TryParseIsolatedArg_RejectsEverythingElse(string raw)
        {
            bool isolated;
            Assert.False(TestCommandRunTests.TryParseIsolatedArg(raw, out isolated));
            // The out param stays false on a reject so a caller that ignores the
            // return value still fails toward the ordinary path, never toward the
            // destructive one.
            Assert.False(isolated);
        }

        // --- The `category` arg: ABSENT is RunAll, WRITTEN-but-empty is a typo ---

        [Fact]
        public void IsEmptyCategoryArg_AbsentIsNotEmpty()
        {
            // Absent must stay the RunAll shape: that is the documented meaning of
            // `RunTests` with no `category` arg, and every pre-R5 caller relies on it.
            Assert.False(TestCommandRunTests.IsEmptyCategoryArg(null));
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("   ")]
        [InlineData("\t")]
        public void IsEmptyCategoryArg_WrittenButEmptyIsATypo(string raw)
        {
            // `category=` really is producible: TestCommandProtocol.TrySplitToken yields
            // an empty value for it. Before this it fell into the RunAll branch, so a
            // typo silently ran the WHOLE assembly - and with isolated=true it would run
            // all 539 declarations through the destructive filter, quickload-restoring a
            // flight baseline after each one.
            Assert.True(TestCommandRunTests.IsEmptyCategoryArg(raw));
        }

        [Theory]
        [InlineData("SceneExitMerge")]
        [InlineData(" SceneExitMerge ")]
        public void IsEmptyCategoryArg_ARealCategoryIsNotEmpty(string raw)
        {
            Assert.False(TestCommandRunTests.IsEmptyCategoryArg(raw));
        }

        [Fact]
        public void CategoryArgEmptyReason_IsTheDocumentedConstant()
        {
            Assert.Equal("category-arg-empty", TestCommandRunTests.CategoryArgEmptyReason);
        }

        [Fact]
        public void TheTwoArgsAgreeOnWhatAnEmptyValueMeans()
        {
            // The convention this feature settled on: for BOTH args of this verb, absent
            // is a meaningful omission and a written-but-empty value is a typo that
            // fails closed. They disagreed in the first cut, which is how the empty
            // category stayed a footgun while the empty isolated was already rejected.
            bool ignored;
            Assert.True(TestCommandRunTests.TryParseIsolatedArg(null, out ignored));
            Assert.False(TestCommandRunTests.TryParseIsolatedArg("", out ignored));
            Assert.False(TestCommandRunTests.IsEmptyCategoryArg(null));
            Assert.True(TestCommandRunTests.IsEmptyCategoryArg(""));
        }

        [Fact]
        public void IsolatedArgInvalidReason_IsTheDocumentedConstant()
        {
            // The harness matches this token in the seam response msg=, so it is a
            // wire contract, not an internal string.
            Assert.Equal("isolated-arg-invalid", TestCommandRunTests.IsolatedArgInvalidReason);
        }
    }
}
