using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization of <c>ParsekScenario.HandleRewindOnLoad</c>, pinned against the original
    /// single method before its stretches were extracted into named phase methods. Same contract
    /// as <see cref="ParsekScenarioOnLoadCharacterizationTests"/>: the body as an ordered token
    /// stream (string literals, invoked names, assignment targets; comments dropped), every call
    /// to a method named <c>HandleRewindOnLoadPhase*</c> replaced by that method's own stream,
    /// compared with the golden file
    /// <c>Fixtures/SourceCharacterization/ParsekScenario.HandleRewindOnLoad.tokens.txt</c>
    /// captured from the original. Every phase is declared once, called once and reached, and
    /// holds no try, catch, finally, yield, return or StartCoroutine. A phase also holds none of
    /// the calls the route and career gates pin between the method's entry and exit markers
    /// (<c>RouteGoBackRewindReconcileTests</c>, <c>RouteRewindLoopPositionTests</c>): those stay
    /// in the host body.
    /// </summary>
    public class ParsekScenarioHandleRewindOnLoadCharacterizationTests
    {
        internal const string Signature =
            "private void HandleRewindOnLoad(ConfigNode node, IReadOnlyList<Recording> recordings)";
        internal const string PhasePrefix = "HandleRewindOnLoadPhase";
        private const string GoldenFile = "ParsekScenario.HandleRewindOnLoad.tokens.txt";

        private static readonly string[] HostPinnedCalls =
        {
            "ReconcileAtGoBackRewind", "ResolveRewindCutoffUT", "PruneBaselinesAfterUT",
            "RecalculateAndPatch", "EndRewind", "RecState",
        };

        private static string ClassText()
        {
            return ParsekScenarioSourceFiles.ReadAll(ParsekScenarioOnLoadCharacterizationTests.ParsekSourceRoot());
        }

        [Fact]
        public void HandleRewindOnLoadExpandedTokenStream_MatchesThePinnedOriginal()
        {
            List<string> actual = ParsekScenarioOnLoadCharacterizationTests.ExpandedTokens(
                ClassText(), Signature, PhasePrefix, new HashSet<string>(StringComparer.Ordinal));

            string goldenPath = ParsekScenarioOnLoadCharacterizationTests.GoldenPath(GoldenFile);
            string actualPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "ParsekScenario.HandleRewindOnLoad.tokens.actual.txt");
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
                    "HandleRewindOnLoad's expanded token stream diverges from the pinned original at token "
                    + i + ": expected " + (i < expected.Count ? "'" + expected[i] + "'" : "<end>")
                    + ", actual " + (i < actual.Count ? "'" + actual[i] + "'" : "<end>")
                    + " (expected " + expected.Count + " tokens, actual " + actual.Count
                    + "; actual stream written to " + actualPath + ")");
            }
        }

        [Fact]
        public void EveryHandleRewindOnLoadPhaseMethod_IsDeclaredOnceCalledOnceAndHoldsNoPinnedCall()
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

                string phaseBody = ParsekScenarioOnLoadCharacterizationTests.MethodBody(classText, kv.Value[0]);
                string phaseMasked = ParsekScenarioOnLoadCharacterizationTests.Mask(phaseBody);
                foreach (string kw in new[] { "try", "catch", "finally", "yield", "return", "StartCoroutine" })
                {
                    Assert.False(SourceScanText.ContainsIdentifier(phaseMasked, kw),
                        name + " holds '" + kw + "'; that stays in HandleRewindOnLoad");
                }
                foreach (string pinned in HostPinnedCalls)
                {
                    Assert.True(ParsekScenarioOnLoadCharacterizationTests.CountCalls(phaseMasked, pinned) == 0,
                        name + " calls " + pinned + ", which the route and career gates pin inside "
                        + "HandleRewindOnLoad's entry/exit markers");
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
