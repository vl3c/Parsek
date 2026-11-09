using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization of <c>ParsekScenario.OnSave</c>, pinned against the original
    /// single-method OnSave before its stretches were extracted into named phase methods.
    /// Same contract as <see cref="ParsekScenarioOnLoadCharacterizationTests"/>: OnSave's body
    /// as an ordered token stream (string literals, invoked names, assignment targets; comments
    /// dropped), every call to a method named <c>OnSavePhase*</c> replaced by that method's own
    /// stream, compared with the golden file
    /// <c>Fixtures/SourceCharacterization/ParsekScenario.OnSave.tokens.txt</c> captured from
    /// the original. The skeleton stays in OnSave: the ordered <c>savePhase = "..."</c>
    /// values, <c>phasesCompleted</c>, and the try/catch/finally; no phase touches
    /// <c>savePhase</c> or <c>phasesCompleted</c>, or holds a try, catch, finally or yield.
    /// </summary>
    public class ParsekScenarioOnSaveCharacterizationTests
    {
        internal const string OnSaveSignature = "public override void OnSave(ConfigNode node)";
        internal const string PhasePrefix = "OnSavePhase";
        private const string GoldenFile = "ParsekScenario.OnSave.tokens.txt";

        private static readonly string[] PinnedSavePhases =
        {
            "\"entry\"", "\"safety-net\"", "\"clear-nodes\"", "\"recordings\"", "\"missions\"",
            "\"vessel-names\"", "\"preserve-tree-state-guard\"", "\"game-state\"",
            "\"refly-state-persist\"", "\"routes\"", "\"stock-pool-reference\"",
            "\"ledger-held-window\"", "\"rewind-vessel-holds\"", "\"playback-replay-scope\"",
            "\"removed-replay-flags\"", "\"save-stamp\"", "\"save-format\"",
        };

        private static string ClassText()
        {
            return ParsekScenarioSourceFiles.ReadAll(ParsekScenarioOnLoadCharacterizationTests.ParsekSourceRoot());
        }

        [Fact]
        public void OnSaveExpandedTokenStream_MatchesThePinnedOriginal()
        {
            List<string> actual = ParsekScenarioOnLoadCharacterizationTests.ExpandedTokens(
                ClassText(), OnSaveSignature, PhasePrefix, new HashSet<string>(StringComparer.Ordinal));

            string goldenPath = ParsekScenarioOnLoadCharacterizationTests.GoldenPath(GoldenFile);
            string actualPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "ParsekScenario.OnSave.tokens.actual.txt");
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
                    "OnSave's expanded token stream diverges from the pinned original at token "
                    + i + ": expected " + (i < expected.Count ? "'" + expected[i] + "'" : "<end>")
                    + ", actual " + (i < actual.Count ? "'" + actual[i] + "'" : "<end>")
                    + " (expected " + expected.Count + " tokens, actual " + actual.Count
                    + "; actual stream written to " + actualPath + ")");
            }
        }

        [Fact]
        public void OnSaveOwnBody_KeepsTheSavePhaseSkeleton()
        {
            string body = ParsekScenarioOnLoadCharacterizationTests.MethodBody(ClassText(), OnSaveSignature);
            List<string> tokens = ParsekScenarioOnLoadCharacterizationTests.Tokenize(body);
            var phases = new List<string>();
            for (int i = 0; i + 1 < tokens.Count; i++)
            {
                if (tokens[i] == "A:savePhase") phases.Add(tokens[i + 1]);
            }

            Assert.Equal(PinnedSavePhases.Select(p => "S:" + p), phases);
            Assert.Contains("A:phasesCompleted", tokens);
            string masked = ParsekScenarioOnLoadCharacterizationTests.Mask(body);
            foreach (string kw in new[] { "try", "catch", "finally" })
                Assert.True(SourceScanText.ContainsIdentifier(masked, kw), "OnSave lost its '" + kw + "'");
        }

        [Fact]
        public void EveryOnSavePhaseMethod_IsDeclaredOnceCalledOnceAndHoldsNoSkeleton()
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
                List<string> tokens = ParsekScenarioOnLoadCharacterizationTests.Tokenize(phaseBody);
                string phaseMasked = ParsekScenarioOnLoadCharacterizationTests.Mask(phaseBody);
                Assert.DoesNotContain("A:savePhase", tokens);
                Assert.DoesNotContain("A:phasesCompleted", tokens);
                foreach (string kw in new[] { "try", "catch", "finally", "yield" })
                {
                    Assert.False(SourceScanText.ContainsIdentifier(phaseMasked, kw),
                        name + " holds '" + kw + "'; the skeleton stays in OnSave");
                }
            }

            var reached = new HashSet<string>(StringComparer.Ordinal);
            ParsekScenarioOnLoadCharacterizationTests.ExpandedTokens(
                classText, OnSaveSignature, PhasePrefix, reached);
            Assert.Equal(
                declared.Keys.OrderBy(k => k, StringComparer.Ordinal),
                reached.OrderBy(k => k, StringComparer.Ordinal));
        }
    }
}
