using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization of <c>ParsekScenario.LoadRecordingTrees</c>, pinned against the
    /// original single method before its stretches were extracted into named phase methods.
    /// Same contract as <see cref="ParsekScenarioOnLoadCharacterizationTests"/>: the body as an
    /// ordered token stream (string literals, invoked names, assignment targets; comments
    /// dropped), every call to a method named <c>LoadRecordingTreesPhase*</c> replaced by that
    /// method's own stream, compared with the golden file
    /// <c>Fixtures/SourceCharacterization/ParsekScenario.LoadRecordingTrees.tokens.txt</c>
    /// captured from the original. Every phase is declared once, called once and reached, and
    /// holds no try, catch, finally, yield, continue, break or return (the per-tree loop's
    /// control flow stays in LoadRecordingTrees).
    /// </summary>
    public class ParsekScenarioLoadRecordingTreesCharacterizationTests
    {
        internal const string Signature =
            "internal static void LoadRecordingTrees(ConfigNode node, IReadOnlyList<Recording> recordings)";
        internal const string PhasePrefix = "LoadRecordingTreesPhase";
        private const string GoldenFile = "ParsekScenario.LoadRecordingTrees.tokens.txt";

        private static string ClassText()
        {
            return ParsekScenarioSourceFiles.ReadAll(ParsekScenarioOnLoadCharacterizationTests.ParsekSourceRoot());
        }

        [Fact]
        public void LoadRecordingTreesExpandedTokenStream_MatchesThePinnedOriginal()
        {
            List<string> actual = ParsekScenarioOnLoadCharacterizationTests.ExpandedTokens(
                ClassText(), Signature, PhasePrefix, new HashSet<string>(StringComparer.Ordinal));

            string goldenPath = ParsekScenarioOnLoadCharacterizationTests.GoldenPath(GoldenFile);
            string actualPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "ParsekScenario.LoadRecordingTrees.tokens.actual.txt");
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
                    "LoadRecordingTrees' expanded token stream diverges from the pinned original at token "
                    + i + ": expected " + (i < expected.Count ? "'" + expected[i] + "'" : "<end>")
                    + ", actual " + (i < actual.Count ? "'" + actual[i] + "'" : "<end>")
                    + " (expected " + expected.Count + " tokens, actual " + actual.Count
                    + "; actual stream written to " + actualPath + ")");
            }
        }

        [Fact]
        public void EveryLoadRecordingTreesPhaseMethod_IsDeclaredOnceCalledOnceAndHoldsNoControlFlowExit()
        {
            string classText = ClassText();
            string masked = ParsekScenarioOnLoadCharacterizationTests.Mask(classText);
            var declared = ParsekScenarioOnLoadCharacterizationTests.PhaseDeclarations(classText, PhasePrefix);
            foreach (var kv in declared)
            {
                string name = kv.Key;
                Assert.True(kv.Value.Count == 1, name + " is declared " + kv.Value.Count + " times");
                int calls = ParsekScenarioOnLoadCharacterizationTests.CountCalls(masked, name) - 1;
                Assert.True(calls == 1, name + " is called " + calls + " times; a phase runs once per call site");

                string phaseMasked = ParsekScenarioOnLoadCharacterizationTests.Mask(
                    ParsekScenarioOnLoadCharacterizationTests.MethodBody(classText, kv.Value[0]));
                foreach (string kw in new[] { "try", "catch", "finally", "yield", "continue", "break", "return" })
                {
                    Assert.False(SourceScanText.ContainsIdentifier(phaseMasked, kw),
                        name + " holds '" + kw + "'; the per-tree loop's control flow stays in LoadRecordingTrees");
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
