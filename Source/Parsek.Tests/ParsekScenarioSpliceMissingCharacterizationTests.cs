using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization of <c>ParsekScenario.SpliceMissingCommittedRecordingsIntoLoadedTree</c>,
    /// pinned against the original single method before its stretches were extracted into
    /// named phase methods. Same contract as
    /// <see cref="ParsekScenarioOnLoadCharacterizationTests"/>: the body as an ordered token
    /// stream (string literals, invoked names, assignment targets; comments dropped), every
    /// call to a method named <c>SpliceMissingCommittedRecordingsPhase*</c> replaced by that
    /// method's own stream, compared with the golden file
    /// <c>Fixtures/SourceCharacterization/ParsekScenario.SpliceMissingCommittedRecordings.tokens.txt</c>
    /// captured from the original. The text read is <c>ParsekScenario.HydrationRepair.cs</c>,
    /// the partial that holds the method and its phases. Every phase is declared once, called once and reached, and
    /// holds no try, catch, finally, yield or return: the early returns and the returned
    /// count stay in the host. A phase may own a loop, and so a continue of that loop; that a
    /// branch never leaves its phase's block is proven on the IL, which this stream cannot see.
    /// </summary>
    public class ParsekScenarioSpliceMissingCharacterizationTests
    {
        internal const string Signature =
            "internal static int SpliceMissingCommittedRecordingsIntoLoadedTree(";
        internal const string PhasePrefix = "SpliceMissingCommittedRecordingsPhase";
        // The method and its phases live in this one older partial, which the joined
        // ParsekScenario text (ParsekScenarioSourceFiles.ReadAll) does not read.
        private const string HostFileName = "ParsekScenario.HydrationRepair.cs";
        private const string GoldenFile = "ParsekScenario.SpliceMissingCommittedRecordings.tokens.txt";

        private static string ClassText()
        {
            return File.ReadAllText(Path.Combine(
                ParsekScenarioOnLoadCharacterizationTests.ParsekSourceRoot(), HostFileName));
        }

        [Fact]
        public void SpliceMissingExpandedTokenStream_MatchesThePinnedOriginal()
        {
            List<string> actual = ParsekScenarioOnLoadCharacterizationTests.ExpandedTokens(
                ClassText(), Signature, PhasePrefix, new HashSet<string>(StringComparer.Ordinal));

            string goldenPath = ParsekScenarioOnLoadCharacterizationTests.GoldenPath(GoldenFile);
            string actualPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "ParsekScenario.SpliceMissingCommittedRecordings.tokens.actual.txt");
            if (!File.Exists(goldenPath))
            {
                File.WriteAllLines(actualPath, actual);
                Assert.True(false, "golden token stream missing: " + goldenPath
                    + " (actual stream written to " + actualPath + ")");
            }
            List<string> expected = File.ReadAllLines(goldenPath).Where(l => l.Length > 0).ToList();
            if (!expected.SequenceEqual(actual))
            {
                File.WriteAllLines(actualPath, actual);
                int i = 0;
                while (i < expected.Count && i < actual.Count && expected[i] == actual[i]) i++;
                Assert.True(false,
                    "SpliceMissingCommittedRecordingsIntoLoadedTree's expanded token stream diverges from the pinned original at token "
                    + i + ": expected " + (i < expected.Count ? "'" + expected[i] + "'" : "<end>")
                    + ", actual " + (i < actual.Count ? "'" + actual[i] + "'" : "<end>")
                    + " (expected " + expected.Count + " tokens, actual " + actual.Count
                    + "; actual stream written to " + actualPath + ")");
            }
        }

        [Fact]
        public void EverySpliceMissingPhaseMethod_IsDeclaredOnceCalledOnceAndHoldsNoExit()
        {
            string classText = ClassText();
            string masked = ParsekScenarioOnLoadCharacterizationTests.Mask(classText);
            var declared = ParsekScenarioOnLoadCharacterizationTests.PhaseDeclarations(classText, PhasePrefix);
            foreach (var kv in declared)
            {
                string name = kv.Key;
                Assert.True(kv.Value.Count == 1, name + " is declared " + kv.Value.Count + " times");
                int calls = ParsekScenarioOnLoadCharacterizationTests.CountCalls(masked, name) - 1;
                Assert.True(calls == 1, name + " is called " + calls + " times; a phase runs once");

                string phaseMasked = ParsekScenarioOnLoadCharacterizationTests.Mask(
                    ParsekScenarioOnLoadCharacterizationTests.MethodBody(classText, kv.Value[0]));
                foreach (string kw in new[] { "try", "catch", "finally", "yield", "return" })
                {
                    Assert.False(SourceScanText.ContainsIdentifier(phaseMasked, kw),
                        name + " holds '" + kw + "'; the exits stay in SpliceMissingCommittedRecordingsIntoLoadedTree");
                }
            }

            var reached = new HashSet<string>(StringComparer.Ordinal);
            ParsekScenarioOnLoadCharacterizationTests.ExpandedTokens(classText, Signature, PhasePrefix, reached);
            Assert.Equal(
                declared.Keys.OrderBy(k => k, StringComparer.Ordinal),
                reached.OrderBy(k => k, StringComparer.Ordinal));
        }
    }
}
