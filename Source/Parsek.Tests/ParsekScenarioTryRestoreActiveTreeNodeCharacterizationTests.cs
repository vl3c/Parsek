using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization of <c>ParsekScenario.TryRestoreActiveTreeNode</c>, pinned against the
    /// original single method before its stretches were extracted into named phase methods. Same
    /// contract as <see cref="ParsekScenarioOnLoadCharacterizationTests"/>: the body as an ordered
    /// token stream (string literals, invoked names, assignment targets; comments dropped), every
    /// call to a method named <c>TryRestoreActiveTreeNodePhase*</c> replaced by that method's own
    /// stream, compared with the golden file
    /// <c>Fixtures/SourceCharacterization/ParsekScenario.TryRestoreActiveTreeNode.tokens.txt</c>
    /// captured from the original. Every phase is declared once, called once and reached, and
    /// holds no try, catch, finally, yield, return, continue or break: the exits stay in the host.
    /// A phase also holds none of the calls the load gates pin by position inside the method's
    /// own body (<c>LoadReconcileWiringGateTests</c>, <c>ColdLoadRestoreAttemptRearmTests</c>,
    /// <c>ResumedReFlySessionCommittedCopyTests</c>): the node load, the quicksave-facts capture,
    /// the hydration, the discarded-tree decline, the stale-epoch keep, the salvage, the drop, the
    /// re-arm, the splice, the resumed-session guard and the detach stay in the host body.
    /// </summary>
    public class ParsekScenarioTryRestoreActiveTreeNodeCharacterizationTests
    {
        internal const string Signature = "internal static bool TryRestoreActiveTreeNode(";
        internal const string PhasePrefix = "TryRestoreActiveTreeNodePhase";
        private const string GoldenFile = "ParsekScenario.TryRestoreActiveTreeNode.tokens.txt";

        private static readonly string[] HostPinnedCalls =
        {
            "RecordingTree.Load", "CaptureQuicksaveTreeFacts", "CollectQuicksaveCommittedRecordingIds",
            "CollectQuicksaveCommittedTreeIds", "RecordingSidecarStore.LoadRecordingFiles",
            "TryDeclineDiscardedActiveTree", "ShouldKeepPendingTreeAfterHydrationFailure",
            "RestoreHydrationFailedRecordingsFromPendingTree", "DropFailedSidecarHydrationRecordings",
            "TryRearmSaveAttemptForRestoredTree", "ShouldLiveAttemptKeepCommittedCopy",
            "SpliceMissingCommittedRecordingsIntoLoadedTree", "IsResumedReFlySessionTree",
            "RecordingStore.RemoveCommittedTreeById",
        };

        private static string ClassText()
        {
            return ParsekScenarioSourceFiles.ReadAll(ParsekScenarioOnLoadCharacterizationTests.ParsekSourceRoot());
        }

        [Fact]
        public void TryRestoreActiveTreeNodeExpandedTokenStream_MatchesThePinnedOriginal()
        {
            List<string> actual = ParsekScenarioOnLoadCharacterizationTests.ExpandedTokens(
                ClassText(), Signature, PhasePrefix, new HashSet<string>(StringComparer.Ordinal));

            string goldenPath = ParsekScenarioOnLoadCharacterizationTests.GoldenPath(GoldenFile);
            string actualPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "ParsekScenario.TryRestoreActiveTreeNode.tokens.actual.txt");
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
                    "TryRestoreActiveTreeNode's expanded token stream diverges from the pinned original at token "
                    + i + ": expected " + (i < expected.Count ? "'" + expected[i] + "'" : "<end>")
                    + ", actual " + (i < actual.Count ? "'" + actual[i] + "'" : "<end>")
                    + " (expected " + expected.Count + " tokens, actual " + actual.Count
                    + "; actual stream written to " + actualPath + ")");
            }
        }

        [Fact]
        public void EveryTryRestoreActiveTreeNodePhaseMethod_IsDeclaredOnceCalledOnceAndHoldsNoExitOrPinnedCall()
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
                foreach (string kw in new[] { "try", "catch", "finally", "yield", "return", "continue", "break" })
                {
                    Assert.False(SourceScanText.ContainsIdentifier(phaseMasked, kw),
                        name + " holds '" + kw + "'; the exits stay in TryRestoreActiveTreeNode");
                }
                foreach (string pinned in HostPinnedCalls)
                {
                    Assert.True(ParsekScenarioOnLoadCharacterizationTests.CountCalls(phaseMasked, pinned) == 0,
                        name + " calls " + pinned + ", which the load gates pin by position inside "
                        + "TryRestoreActiveTreeNode's own body");
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
