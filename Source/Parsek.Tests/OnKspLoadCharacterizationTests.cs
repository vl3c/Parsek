using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization golden for <c>LedgerOrchestrator.OnKspLoad</c>, written against the
    /// method as it stood before the wave-2 W7b extraction and kept unchanged through it.
    /// Scenarios: an empty ledger on an unready clock; past and future KSC rows with and
    /// without the current-UT cutoff feature, on a ready and an unready clock; the ledger
    /// held-through UT above and below a ready load clock and beside a clock not ready; a
    /// recording that failed to load beside a tagged row of a recording missing from the
    /// loaded set. For each it records every
    /// ledger row after the load (action ids masked) and the log lines the load itself owns:
    /// the OnKspLoad lines, the ledger's reconcile / repair lines and the first line of the
    /// recalc path it chose. The rest of the recalc's transcript is left out on purpose, so
    /// unrelated recalc logging cannot move this golden.
    /// </summary>
    [Collection("Sequential")]
    public class OnKspLoadCharacterizationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo savedCulture;

        public OnKspLoadCharacterizationTests()
        {
            savedCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            RuntimeHelpers.RunClassConstructor(typeof(ParsekSettings).TypeHandle);
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => 1000.0;
            ResetAll();
        }

        public void Dispose()
        {
            ResetAll();
            Thread.CurrentThread.CurrentCulture = savedCulture;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void ResetAll()
        {
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            LedgerOrchestrator.ResetForTesting();
            Ledger.ResetForTesting();
            LedgerHeldWindow.ResetForTesting();
            KerbalLoadRepairDiagnostics.Reset();
            CurrencyScenarioReadiness.ResetForTesting();
            StockShadowTakeBack.ResetForTesting();
            ParsekLog.ResetRateLimitsForTesting();
        }

        private static void AddKscSpend(string actionId, double ut, float cost)
        {
            Ledger.AddAction(new GameAction
            {
                ActionId = actionId,
                UT = ut,
                Type = GameActionType.FundsSpending,
                FundsSpent = cost,
                FundsSpendingSource = FundsSpendingSource.Other,
                Sequence = 1,
            });
        }

        private static void AddTaggedEarning(string actionId, double ut, string recordingId)
        {
            Ledger.AddAction(new GameAction
            {
                ActionId = actionId,
                UT = ut,
                Type = GameActionType.FundsEarning,
                RecordingId = recordingId,
                FundsAwarded = 250f,
                FundsSource = FundsEarningSource.Other,
                Sequence = 2,
            });
        }

        private static readonly Regex ActionIdLine = new Regex(@"^(\s*actionId\s*=\s*)(.*)$");

        private static readonly string[] OwnedOrchestratorMarkers =
        {
            "OnKspLoad",
            "Current-UT ledger recalculation",
            "RecalculateAndPatch: actionsTotal=",
            "MigrateKerbalAssignments",
            "RepairMissingRecoveryDedupKeys",
            "FlushStalePendingRecoveryFunds",
        };

        private static bool Owned(string line)
        {
            if (line.Contains("][Ledger] ") || line.Contains("][KerbalLoad] "))
                return true;
            if (!line.Contains("][LedgerOrchestrator] "))
                return false;
            foreach (string marker in OwnedOrchestratorMarkers)
                if (line.Contains(marker))
                    return true;
            return false;
        }

        private void Case(StringBuilder sb, string label, HashSet<string> validIds, double maxUT,
            bool cutoffFeature, ICollection<string> dropped)
        {
            logLines.Clear();
            ParsekLog.ResetRateLimitsForTesting();
            LedgerOrchestrator.OnKspLoad(validIds, maxUT, cutoffFeature, dropped);

            sb.Append("== ").Append(label).Append('\n');
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var parent = new ConfigNode("ROOT");
                actions[i].SerializeInto(parent);
                foreach (string raw in parent.ToString().Replace("\r", "").Split('\n'))
                {
                    string line = raw.TrimEnd();
                    if (line.Length == 0) continue;
                    var m = ActionIdLine.Match(line);
                    if (m.Success && !m.Groups[2].Value.StartsWith("seed-", StringComparison.Ordinal))
                        line = m.Groups[1].Value + "<fresh>";
                    sb.Append("row[").Append(i.ToString(CultureInfo.InvariantCulture)).Append("] ")
                        .Append(line).Append('\n');
                }
            }

            foreach (string line in logLines)
                if (Owned(line))
                    sb.Append("log ").Append(line).Append('\n');
        }

        private void Reset()
        {
            ResetAll();
            logLines.Clear();
        }

        private static void AssertGolden(string name, string expected, string actual)
        {
            if (string.Equals(expected, actual, StringComparison.Ordinal))
                return;
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "golden-actual");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ".txt");
            File.WriteAllText(path, actual);
            Assert.True(false, "golden mismatch for " + name + "; actual transcript written to " + path);
        }

        private static HashSet<string> Ids(params string[] ids) =>
            new HashSet<string>(ids, StringComparer.Ordinal);

        private static void PastAndFutureRows()
        {
            AddKscSpend("seed-past", 100.0, 40f);
            AddKscSpend("seed-future", 800.0, 60f);
        }

        [Fact]
        public void OnKspLoad_Golden()
        {
            var sb = new StringBuilder();

            Reset();
            Case(sb, "O01 empty ledger, clock not ready", Ids(), 0.0, false, null);

            Reset();
            PastAndFutureRows();
            Case(sb, "O02 ready clock, no cutoff feature", Ids(), 500.0, false, null);

            Reset();
            PastAndFutureRows();
            Case(sb, "O03 ready clock, cutoff feature, future rows", Ids(), 500.0, true, null);

            Reset();
            PastAndFutureRows();
            Case(sb, "O04 clock not ready, cutoff feature", Ids(), 0.0, true, null);

            Reset();
            AddKscSpend("seed-past", 100.0, 40f);
            Case(sb, "O05 ready clock, cutoff feature, no future rows", Ids(), 500.0, true, null);

            Reset();
            PastAndFutureRows();
            LedgerHeldWindow.SetForTesting(600.0);
            Case(sb, "O06 held-through UT above the clock", Ids(), 500.0, true, null);

            Reset();
            PastAndFutureRows();
            LedgerHeldWindow.SetForTesting(400.0);
            Case(sb, "O07 held-through UT below the clock", Ids(), 500.0, false, null);

            Reset();
            AddTaggedEarning("seed-dropped", 150.0, "rec-dropped");
            AddTaggedEarning("seed-gone", 160.0, "rec-gone");
            Case(sb, "O08 a recording that failed to load, one missing", Ids(), 500.0, false,
                new List<string> { "rec-dropped" });

            Reset();
            PastAndFutureRows();
            LedgerHeldWindow.SetForTesting(600.0);
            Case(sb, "O09 held-through UT on a clock not ready", Ids(), 0.0, true, null);

            AssertGolden("OnKspLoad", Golden, sb.ToString());
        }

        private static readonly string Golden = string.Join("\n", new[]
        {
            "== O01 empty ledger, clock not ready",
            "log [Parsek][INFO][Ledger] Reconcile complete: before=0, kept=0, prunedEarnings=0, prunedSpendings=0, prunedSpendingsByRecordingId=0, prunedContractLifecycle=0, prunedOther=0, maxUT=0, preserveFutureTimelineActions=True, validRecordingIds=0",
            "log [Parsek][INFO][LedgerOrchestrator] OnKspLoad recalc decision: useCurrentUtCutoff=False maxUT=0 featureSupportsCutoff=False currentUtReady=False hasFutureActions=False",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=0, actionsAfterCutoff=0, cutoffUT=null",
            "== O02 ready clock, no cutoff feature",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 100",
            "row[0] \t\ttype = 3",
            "row[0] \t\tactionId = seed-past",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tfundsSpent = 40",
            "row[0] \t\tfundsSpendingSource = 6",
            "row[0] \t}",
            "row[0] }",
            "log [Parsek][VERBOSE][Ledger] Pruned spending: type=FundsSpending, UT=800 > maxUT=500, recordingId='(null)'",
            "log [Parsek][INFO][Ledger] Reconcile complete: before=2, kept=1, prunedEarnings=0, prunedSpendings=1, prunedSpendingsByRecordingId=0, prunedContractLifecycle=0, prunedOther=0, maxUT=500, preserveFutureTimelineActions=False, validRecordingIds=0",
            "log [Parsek][INFO][LedgerOrchestrator] OnKspLoad recalc decision: useCurrentUtCutoff=False maxUT=500 featureSupportsCutoff=False currentUtReady=True hasFutureActions=False",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=1, actionsAfterCutoff=1, cutoffUT=null",
            "== O03 ready clock, cutoff feature, future rows",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 100",
            "row[0] \t\ttype = 3",
            "row[0] \t\tactionId = seed-past",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tfundsSpent = 40",
            "row[0] \t\tfundsSpendingSource = 6",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 800",
            "row[1] \t\ttype = 3",
            "row[1] \t\tactionId = seed-future",
            "row[1] \t\tseq = 1",
            "row[1] \t\tcseq = 2",
            "row[1] \t\tfundsSpent = 60",
            "row[1] \t\tfundsSpendingSource = 6",
            "row[1] \t}",
            "row[1] }",
            "log [Parsek][INFO][Ledger] Reconcile complete: before=2, kept=2, prunedEarnings=0, prunedSpendings=0, prunedSpendingsByRecordingId=0, prunedContractLifecycle=0, prunedOther=0, maxUT=500, preserveFutureTimelineActions=True, validRecordingIds=0",
            "log [Parsek][INFO][LedgerOrchestrator] OnKspLoad recalc decision: useCurrentUtCutoff=True maxUT=500 featureSupportsCutoff=True currentUtReady=True hasFutureActions=True",
            "log [Parsek][INFO][LedgerOrchestrator] Current-UT ledger recalculation: reason=ksp-load cutoffUT=500",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=2, actionsAfterCutoff=1, cutoffUT=500",
            "== O04 clock not ready, cutoff feature",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 100",
            "row[0] \t\ttype = 3",
            "row[0] \t\tactionId = seed-past",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tfundsSpent = 40",
            "row[0] \t\tfundsSpendingSource = 6",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 800",
            "row[1] \t\ttype = 3",
            "row[1] \t\tactionId = seed-future",
            "row[1] \t\tseq = 1",
            "row[1] \t\tcseq = 2",
            "row[1] \t\tfundsSpent = 60",
            "row[1] \t\tfundsSpendingSource = 6",
            "row[1] \t}",
            "row[1] }",
            "log [Parsek][INFO][Ledger] Reconcile complete: before=2, kept=2, prunedEarnings=0, prunedSpendings=0, prunedSpendingsByRecordingId=0, prunedContractLifecycle=0, prunedOther=0, maxUT=0, preserveFutureTimelineActions=True, validRecordingIds=0",
            "log [Parsek][INFO][LedgerOrchestrator] OnKspLoad recalc decision: useCurrentUtCutoff=False maxUT=0 featureSupportsCutoff=True currentUtReady=False hasFutureActions=True",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=2, actionsAfterCutoff=2, cutoffUT=null",
            "== O05 ready clock, cutoff feature, no future rows",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 100",
            "row[0] \t\ttype = 3",
            "row[0] \t\tactionId = seed-past",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tfundsSpent = 40",
            "row[0] \t\tfundsSpendingSource = 6",
            "row[0] \t}",
            "row[0] }",
            "log [Parsek][INFO][Ledger] Reconcile complete: before=1, kept=1, prunedEarnings=0, prunedSpendings=0, prunedSpendingsByRecordingId=0, prunedContractLifecycle=0, prunedOther=0, maxUT=500, preserveFutureTimelineActions=True, validRecordingIds=0",
            "log [Parsek][INFO][LedgerOrchestrator] OnKspLoad recalc decision: useCurrentUtCutoff=False maxUT=500 featureSupportsCutoff=True currentUtReady=True hasFutureActions=False",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=1, actionsAfterCutoff=1, cutoffUT=null",
            "== O06 held-through UT above the clock",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 100",
            "row[0] \t\ttype = 3",
            "row[0] \t\tactionId = seed-past",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tfundsSpent = 40",
            "row[0] \t\tfundsSpendingSource = 6",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 800",
            "row[1] \t\ttype = 3",
            "row[1] \t\tactionId = seed-future",
            "row[1] \t\tseq = 1",
            "row[1] \t\tcseq = 2",
            "row[1] \t\tfundsSpent = 60",
            "row[1] \t\tfundsSpendingSource = 6",
            "row[1] \t}",
            "row[1] }",
            "log [Parsek][INFO][LedgerOrchestrator] OnKspLoad: reconcile clock 500 raised to the ledger held-through UT 600",
            "log [Parsek][INFO][Ledger] Reconcile complete: before=2, kept=2, prunedEarnings=0, prunedSpendings=0, prunedSpendingsByRecordingId=0, prunedContractLifecycle=0, prunedOther=0, maxUT=600, preserveFutureTimelineActions=True, validRecordingIds=0",
            "log [Parsek][INFO][LedgerOrchestrator] OnKspLoad recalc decision: useCurrentUtCutoff=True maxUT=600 featureSupportsCutoff=True currentUtReady=True hasFutureActions=True",
            "log [Parsek][INFO][LedgerOrchestrator] Current-UT ledger recalculation: reason=ksp-load cutoffUT=600",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=2, actionsAfterCutoff=1, cutoffUT=600",
            "== O07 held-through UT below the clock",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 100",
            "row[0] \t\ttype = 3",
            "row[0] \t\tactionId = seed-past",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tfundsSpent = 40",
            "row[0] \t\tfundsSpendingSource = 6",
            "row[0] \t}",
            "row[0] }",
            "log [Parsek][VERBOSE][Ledger] Pruned spending: type=FundsSpending, UT=800 > maxUT=500, recordingId='(null)'",
            "log [Parsek][INFO][Ledger] Reconcile complete: before=2, kept=1, prunedEarnings=0, prunedSpendings=1, prunedSpendingsByRecordingId=0, prunedContractLifecycle=0, prunedOther=0, maxUT=500, preserveFutureTimelineActions=False, validRecordingIds=0",
            "log [Parsek][INFO][LedgerOrchestrator] OnKspLoad recalc decision: useCurrentUtCutoff=False maxUT=500 featureSupportsCutoff=False currentUtReady=True hasFutureActions=False",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=1, actionsAfterCutoff=1, cutoffUT=null",
            "== O08 a recording that failed to load, one missing",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 150",
            "row[0] \t\ttype = 2",
            "row[0] \t\tactionId = seed-dropped",
            "row[0] \t\tseq = 2",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tfundsAwarded = 250",
            "row[0] \t\tfundsSource = 4",
            "row[0] \t}",
            "row[0] }",
            "log [Parsek][INFO][Ledger] ClearRecordingTagForRecordings: cleared=1 recordingIds=1 total=2 \u2014 rows KEPT, attribution dropped so a retired id cannot scope a tombstone",
            "log [Parsek][INFO][LedgerOrchestrator] OnKspLoad: kept 1 ledger row(s) of 1 committed recording(s) that failed to load (O3, tag cleared, rows kept) ids=[rec-dropped]",
            "log [Parsek][VERBOSE][Ledger] Pruned earning: type=FundsEarning, recordingId='rec-gone' not in validRecordingIds, UT=160",
            "log [Parsek][INFO][Ledger] Reconcile complete: before=2, kept=1, prunedEarnings=1, prunedSpendings=0, prunedSpendingsByRecordingId=0, prunedContractLifecycle=0, prunedOther=0, maxUT=500, preserveFutureTimelineActions=False, validRecordingIds=0",
            "log [Parsek][INFO][LedgerOrchestrator] OnKspLoad recalc decision: useCurrentUtCutoff=False maxUT=500 featureSupportsCutoff=False currentUtReady=True hasFutureActions=False",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=1, actionsAfterCutoff=1, cutoffUT=null",
            "== O09 held-through UT on a clock not ready",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 100",
            "row[0] \t\ttype = 3",
            "row[0] \t\tactionId = seed-past",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tfundsSpent = 40",
            "row[0] \t\tfundsSpendingSource = 6",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 800",
            "row[1] \t\ttype = 3",
            "row[1] \t\tactionId = seed-future",
            "row[1] \t\tseq = 1",
            "row[1] \t\tcseq = 2",
            "row[1] \t\tfundsSpent = 60",
            "row[1] \t\tfundsSpendingSource = 6",
            "row[1] \t}",
            "row[1] }",
            "log [Parsek][INFO][Ledger] Reconcile complete: before=2, kept=2, prunedEarnings=0, prunedSpendings=0, prunedSpendingsByRecordingId=0, prunedContractLifecycle=0, prunedOther=0, maxUT=0, preserveFutureTimelineActions=True, validRecordingIds=0",
            "log [Parsek][INFO][LedgerOrchestrator] OnKspLoad recalc decision: useCurrentUtCutoff=False maxUT=0 featureSupportsCutoff=True currentUtReady=False hasFutureActions=True",
            "log [Parsek][VERBOSE][LedgerOrchestrator] RecalculateAndPatch: actionsTotal=2, actionsAfterCutoff=2, cutoffUT=null",
        }) + "\n";
    }
}
