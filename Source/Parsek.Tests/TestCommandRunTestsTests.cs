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
    }
}
