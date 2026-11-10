using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization golden for <c>LedgerOrchestrator.AddVesselRecoveryCostActions</c>,
    /// written against the method as it stood before the wave-2 W7b extraction and kept
    /// unchanged through it. The method is private, so the test reaches it by reflection and
    /// reads the original and the split method alike. Scenarios: a recording that did not end
    /// Recovered, a paired recovery event with a positive, zero and negative delta, a paired
    /// event of another vessel (falling back), an unnamed event, a paired event outside the UT
    /// window, the in-flight recover mark awaiting the stock payout, and the last-two-points
    /// heuristic with a positive, zero and negative delta and with a single point. For each
    /// it records the return value, every row added (action ids masked) and every log line.
    /// </summary>
    [Collection("Sequential")]
    public class AddVesselRecoveryCostActionsCharacterizationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo savedCulture;

        private static readonly MethodInfo AddRecoveryCost = typeof(LedgerOrchestrator).GetMethod(
            "AddVesselRecoveryCostActions", BindingFlags.NonPublic | BindingFlags.Static);

        public AddVesselRecoveryCostActionsCharacterizationTests()
        {
            // Some lines use the host culture (":F0"); read the golden under the invariant one.
            savedCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ResetAll();
        }

        public void Dispose()
        {
            ResetAll();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            Thread.CurrentThread.CurrentCulture = savedCulture;
        }

        private static void ResetAll()
        {
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            InFlightRecoveryRequest.ResetForTesting();
            RecoveryPayoutContextStore.ResetForTesting();
            LedgerRecoveryFundsPairing.ResetForTesting();
            Ledger.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
        }

        private static Recording Rec(string name, TerminalState terminal, params double[] utFunds)
        {
            var rec = new Recording
            {
                RecordingId = "rec-1",
                VesselName = name,
                TerminalStateValue = terminal,
            };
            for (int i = 0; i + 1 < utFunds.Length; i += 2)
                rec.Points.Add(new TrajectoryPoint { ut = utFunds[i], funds = utFunds[i + 1] });
            return rec;
        }

        private static void AddFundsEvent(double ut, double before, double after, string detail)
        {
            var e = new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                detail = detail,
                valueBefore = before,
                valueAfter = after,
            };
            GameStateStore.AddEvent(ref e);
        }

        private static readonly Regex ActionIdLine = new Regex(@"^(\s*actionId\s*=\s*).*$");

        private void Case(StringBuilder sb, string label, Recording rec, double endUT)
        {
            logLines.Clear();
            var result = new List<GameAction>();
            object ret = AddRecoveryCost.Invoke(null, new object[] { result, rec.RecordingId, endUT, rec });
            sb.Append("== ").Append(label).Append('\n');
            sb.Append("return=").Append((bool)ret ? "True" : "False").Append('\n');
            for (int i = 0; i < result.Count; i++)
            {
                var parent = new ConfigNode("ROOT");
                result[i].SerializeInto(parent);
                foreach (string raw in parent.ToString().Replace("\r", "").Split('\n'))
                {
                    string line = raw.TrimEnd();
                    if (line.Length == 0) continue;
                    var m = ActionIdLine.Match(line);
                    if (m.Success)
                        line = m.Groups[1].Value + "<masked>";
                    sb.Append("row[").Append(i.ToString(CultureInfo.InvariantCulture)).Append("] ")
                        .Append(line).Append('\n');
                }
            }
            foreach (string line in logLines)
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

        [Fact]
        public void AddVesselRecoveryCostActions_Golden()
        {
            Assert.NotNull(AddRecoveryCost);
            var sb = new StringBuilder();

            Reset();
            AddFundsEvent(300.0, 1000.0, 5000.0, "Flea");
            Case(sb, "V01 not recovered", Rec("Flea", TerminalState.Landed, 100.0, 1000.0, 300.0, 4000.0), 300.0);

            Reset();
            AddFundsEvent(300.05, 1000.0, 5250.25, "Flea");
            Case(sb, "V02 paired, positive", Rec("Flea", TerminalState.Recovered, 100.0, 1000.0, 300.0, 9000.0), 300.0);

            Reset();
            AddFundsEvent(300.0, 1000.0, 1000.0, "Flea");
            Case(sb, "V03 paired, zero", Rec("Flea", TerminalState.Recovered, 100.0, 1000.0, 300.0, 9000.0), 300.0);

            Reset();
            AddFundsEvent(299.95, 1000.0, 400.0, "Flea");
            Case(sb, "V04 paired, negative", Rec("Flea", TerminalState.Recovered, 100.0, 1000.0, 300.0, 9000.0), 300.0);

            Reset();
            AddFundsEvent(300.0, 1000.0, 5000.0, "Other Ship");
            Case(sb, "V05 event of another vessel, heuristic fallback",
                Rec("Flea", TerminalState.Recovered, 100.0, 1000.0, 300.0, 3500.0), 300.0);

            Reset();
            AddFundsEvent(300.0, 1000.0, 2500.0, null);
            Case(sb, "V06 unnamed event pairs", Rec("Flea", TerminalState.Recovered, 100.0, 1000.0, 300.0, 9000.0), 300.0);

            Reset();
            AddFundsEvent(300.5, 1000.0, 5000.0, "Flea");
            Case(sb, "V07 event outside the window, heuristic",
                Rec("Flea", TerminalState.Recovered, 100.0, 1000.0, 300.0, 1600.0), 300.0);

            Reset();
            InFlightRecoveryRequest.MarkAwaitingStockPayoutForTesting("rec-1");
            Case(sb, "V08 awaiting the stock payout",
                Rec("Flea", TerminalState.Recovered, 100.0, 1000.0, 300.0, 3500.0), 300.0);

            Reset();
            InFlightRecoveryRequest.MarkAwaitingStockPayoutForTesting("rec-1");
            AddFundsEvent(300.0, 1000.0, 5000.0, "Flea");
            Case(sb, "V09 paired wins over the awaiting mark",
                Rec("Flea", TerminalState.Recovered, 100.0, 1000.0, 300.0, 3500.0), 300.0);

            Reset();
            Case(sb, "V10 heuristic, positive", Rec("Flea", TerminalState.Recovered, 100.0, 1000.0, 200.0, 1200.0, 300.0, 1750.25), 300.0);
            Reset();
            Case(sb, "V11 heuristic, zero", Rec("Flea", TerminalState.Recovered, 100.0, 1000.0, 300.0, 1000.0), 300.0);
            Reset();
            Case(sb, "V12 heuristic, negative", Rec("Flea", TerminalState.Recovered, 100.0, 1000.0, 300.0, 900.0), 300.0);
            Reset();
            Case(sb, "V13 heuristic, one point", Rec("Flea", TerminalState.Recovered, 300.0, 9000.0), 300.0);
            Reset();
            Case(sb, "V14 heuristic, no points", Rec("Flea", TerminalState.Recovered), 300.0);

            AssertGolden("AddVesselRecoveryCostActions", Golden, sb.ToString());
        }

        private static readonly string Golden = string.Join("\n", new[]
        {
            "== V01 not recovered",
            "return=False",
            "== V02 paired, positive",
            "return=False",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 300.05",
            "row[0] \t\ttype = 2",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\trecordingId = rec-1",
            "row[0] \t\tfundsAwarded = 4250.25",
            "row[0] \t\tfundsSource = 2",
            "row[0] \t\tdedupKey = FundsChanged|VesselRecovery|300.05|1000|5250.25|",
            "row[0] \t}",
            "row[0] }",
            "log [Parsek][VERBOSE][LedgerOrchestrator] CreateVesselCostActions: paired recovery funds=4250 (before=1000, after=5250) for 'rec-1'",
            "== V03 paired, zero",
            "return=False",
            "log [Parsek][VERBOSE][LedgerOrchestrator] CreateVesselCostActions: paired recovery event for 'rec-1' at ut=300.0 has non-positive delta 0.0 \u2014 skipping",
            "== V04 paired, negative",
            "return=False",
            "log [Parsek][VERBOSE][LedgerOrchestrator] CreateVesselCostActions: paired recovery event for 'rec-1' at ut=300.0 has non-positive delta -600.0 \u2014 skipping",
            "== V05 event of another vessel, heuristic fallback",
            "return=False",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 300",
            "row[0] \t\ttype = 2",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\trecordingId = rec-1",
            "row[0] \t\tfundsAwarded = 2500",
            "row[0] \t\tfundsSource = 2",
            "row[0] \t}",
            "row[0] }",
            "log [Parsek][VERBOSE][LedgerOrchestrator] CreateVesselCostActions: recovery funds=2500 (last=3500, penultimate=1000) for 'rec-1'",
            "== V06 unnamed event pairs",
            "return=False",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 300",
            "row[0] \t\ttype = 2",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\trecordingId = rec-1",
            "row[0] \t\tfundsAwarded = 1500",
            "row[0] \t\tfundsSource = 2",
            "row[0] \t\tdedupKey = FundsChanged|VesselRecovery|300|1000|2500|",
            "row[0] \t}",
            "row[0] }",
            "log [Parsek][VERBOSE][LedgerOrchestrator] CreateVesselCostActions: paired recovery funds=1500 (before=1000, after=2500) for 'rec-1'",
            "== V07 event outside the window, heuristic",
            "return=False",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 300",
            "row[0] \t\ttype = 2",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\trecordingId = rec-1",
            "row[0] \t\tfundsAwarded = 600",
            "row[0] \t\tfundsSource = 2",
            "row[0] \t}",
            "row[0] }",
            "log [Parsek][VERBOSE][LedgerOrchestrator] CreateVesselCostActions: recovery funds=600 (last=1600, penultimate=1000) for 'rec-1'",
            "== V08 awaiting the stock payout",
            "return=True",
            "log [Parsek][VERBOSE][LedgerOrchestrator] CreateVesselCostActions: 'rec-1' recovered in flight - payout not yet paid, the stock recovery writes it (no heuristic row)",
            "== V09 paired wins over the awaiting mark",
            "return=False",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 300",
            "row[0] \t\ttype = 2",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\trecordingId = rec-1",
            "row[0] \t\tfundsAwarded = 4000",
            "row[0] \t\tfundsSource = 2",
            "row[0] \t\tdedupKey = FundsChanged|VesselRecovery|300|1000|5000|",
            "row[0] \t}",
            "row[0] }",
            "log [Parsek][VERBOSE][LedgerOrchestrator] CreateVesselCostActions: paired recovery funds=4000 (before=1000, after=5000) for 'rec-1'",
            "== V10 heuristic, positive",
            "return=False",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 300",
            "row[0] \t\ttype = 2",
            "row[0] \t\tactionId = <masked>",
            "row[0] \t\trecordingId = rec-1",
            "row[0] \t\tfundsAwarded = 550.25",
            "row[0] \t\tfundsSource = 2",
            "row[0] \t}",
            "row[0] }",
            "log [Parsek][VERBOSE][LedgerOrchestrator] CreateVesselCostActions: recovery funds=550 (last=1750, penultimate=1200) for 'rec-1'",
            "== V11 heuristic, zero",
            "return=False",
            "== V12 heuristic, negative",
            "return=False",
            "== V13 heuristic, one point",
            "return=False",
            "== V14 heuristic, no points",
            "return=False",
        }) + "\n";
    }
}
