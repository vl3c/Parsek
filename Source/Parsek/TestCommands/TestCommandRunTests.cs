using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure result-payload builder for the two-phase <c>RunTests</c> verb (P5.6). When
    /// the owned <c>InGameTestRunner</c> batch stops running, the addon reads its
    /// Passed / Failed / Skipped counts and feeds them here; the exported results file
    /// name is a fixed contract the orchestrator tails. Kept pure so the payload shape
    /// is xUnit-covered without Unity.
    /// </summary>
    internal static class TestCommandRunTests
    {
        /// <summary>The fixed results file name (in the KSP root) the runner exports to.</summary>
        internal const string ResultsFileName = "parsek-test-results.txt";

        internal static List<KeyValuePair<string, string>> BuildResultPayload(int passed, int failed, int skipped)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("passed", passed.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("failed", failed.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("skipped", skipped.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("results", ResultsFileName),
            };
    }
}
