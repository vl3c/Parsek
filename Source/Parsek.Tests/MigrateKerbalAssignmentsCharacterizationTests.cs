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
    /// Characterization golden for <c>LedgerOrchestrator.MigrateKerbalAssignments</c>, written
    /// against the method as it stood before the wave-2 W7b extraction and kept unchanged
    /// through it. The method is private, so the test reaches it by reflection (as the other
    /// suites do). Scenarios: no committed recording; a fresh derivation, then a second pass
    /// that finds the rows matching; a stand-in row rewritten to its owner; a stored row whose
    /// end state changed (its action id inherited); a stored tourist row whose role is carried;
    /// and several recordings at once beside rows of an uncommitted id and an untagged row.
    /// For each it records every ledger row (seeded action ids printed, fresh ones masked),
    /// every log line and the load-repair diagnostics summary the method feeds.
    /// </summary>
    [Collection("Sequential")]
    public class MigrateKerbalAssignmentsCharacterizationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo savedCulture;

        private static readonly MethodInfo Migrate = typeof(LedgerOrchestrator).GetMethod(
            "MigrateKerbalAssignments", BindingFlags.Static | BindingFlags.NonPublic);

        public MigrateKerbalAssignmentsCharacterizationTests()
        {
            savedCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
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
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            Thread.CurrentThread.CurrentCulture = savedCulture;
        }

        private static void ResetAll()
        {
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            KerbalLoadRepairDiagnostics.Reset();
            StockShadowTakeBack.ResetForTesting();
            ParsekLog.ResetRateLimitsForTesting();
        }

        private static Recording CrewRec(string id, double start, double end, TerminalState? terminal,
            params string[] crew)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            foreach (string c in crew)
                part.AddValue("crew", c);
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Ship " + id,
                GhostVisualSnapshot = snapshot,
                TerminalStateValue = terminal,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            return rec;
        }

        private static void AddRow(string actionId, string recordingId, string kerbal, string role,
            double start, double end, KerbalEndState endState)
        {
            Ledger.AddAction(new GameAction
            {
                ActionId = actionId,
                UT = start,
                Type = GameActionType.KerbalAssignment,
                RecordingId = recordingId,
                KerbalName = kerbal,
                KerbalRole = role,
                StartUT = (float)start,
                EndUT = (float)end,
                KerbalEndStateField = endState,
                Sequence = 1,
            });
        }

        private static readonly Regex ActionIdLine = new Regex(@"^(\s*actionId\s*=\s*)(.*)$");

        private void Case(StringBuilder sb, string label)
        {
            logLines.Clear();
            KerbalLoadRepairDiagnostics.Begin();
            Migrate.Invoke(null, null);
            KerbalLoadRepairDiagnostics.EmitAndReset();
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
        public void MigrateKerbalAssignments_Golden()
        {
            Assert.NotNull(Migrate);
            var sb = new StringBuilder();

            Reset();
            AddRow("seed-stray", "rec-gone", "Jebediah Kerman", "Pilot", 10, 20, KerbalEndState.Recovered);
            Case(sb, "K01 no committed recording");

            Reset();
            CrewRec("rec-fresh", 10, 20, TerminalState.Landed, "Jebediah Kerman", "Bill Kerman");
            Case(sb, "K02a fresh derivation");
            Case(sb, "K02b second pass, rows match");

            Reset();
            CrewReservationManager.SetReplacement("Jebediah Kerman", "Leia Kerman");
            var standIn = CrewRec("rec-standin", 10, 20, null, "Leia Kerman");
            standIn.CrewEndStates = new Dictionary<string, KerbalEndState>
            {
                { "Jebediah Kerman", KerbalEndState.Recovered }
            };
            AddRow("seed-leia", "rec-standin", "Leia Kerman", "Pilot", 10, 20, KerbalEndState.Unknown);
            Case(sb, "K03 stand-in row rewritten to its owner");

            Reset();
            CrewRec("rec-dead", 30, 45, TerminalState.Destroyed, "Valentina Kerman");
            AddRow("seed-val", "rec-dead", "Valentina Kerman", "Pilot", 30, 45, KerbalEndState.Recovered);
            Case(sb, "K04 end state changed, action id inherited");

            Reset();
            CrewRec("rec-tourist", 50, 60, TerminalState.Landed, "Gerdan Kerman");
            AddRow("seed-tourist", "rec-tourist", "Gerdan Kerman", "Tourist", 50, 60, KerbalEndState.Unknown);
            Case(sb, "K05a tourist role carried, rows match");

            Reset();
            CrewRec("rec-tourist", 50, 60, TerminalState.Landed, "Gerdan Kerman", "Bill Kerman");
            AddRow("seed-tourist", "rec-tourist", "Gerdan Kerman", "Tourist", 50, 60, KerbalEndState.Unknown);
            Case(sb, "K05b tourist role carried, a crew row missing");

            Reset();
            CrewRec("rec-one", 100, 120, TerminalState.Landed, "Jebediah Kerman");
            CrewRec("rec-two", 130, 150, TerminalState.Destroyed, "Bob Kerman", "Bill Kerman");
            CrewRec("rec-empty", 160, 170, TerminalState.Landed);
            AddRow("seed-bob", "rec-two", "Bob Kerman", "Scientist", 130, 150, KerbalEndState.Recovered);
            AddRow("seed-extra", "rec-two", "Wernher Kerman", "Engineer", 130, 150, KerbalEndState.Recovered);
            AddRow("seed-orphan", "rec-uncommitted", "Bob Kerman", "Scientist", 10, 20, KerbalEndState.Recovered);
            AddRow("seed-untagged", null, "Bill Kerman", "Engineer", 5, 6, KerbalEndState.Recovered);
            Case(sb, "K06 several recordings, orphan and untagged rows");

            AssertGolden("MigrateKerbalAssignments", Golden, sb.ToString());
        }

        private static readonly string Golden = string.Join("\n", new[]
        {
            "== K01 no committed recording",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 10",
            "row[0] \t\ttype = 11",
            "row[0] \t\tactionId = seed-stray",
            "row[0] \t\trecordingId = rec-gone",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tkerbalName = Jebediah Kerman",
            "row[0] \t\tkerbalRole = Pilot",
            "row[0] \t\tstartUT = 10",
            "row[0] \t\tendUT = 20",
            "row[0] \t\tendState = 2",
            "row[0] \t\txpGained = 0",
            "row[0] \t}",
            "row[0] }",
            "== K02a fresh derivation",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 10",
            "row[0] \t\ttype = 11",
            "row[0] \t\tactionId = <fresh>",
            "row[0] \t\trecordingId = rec-fresh",
            "row[0] \t\tseq = 1",
            "row[0] \t\tkerbalName = Jebediah Kerman",
            "row[0] \t\tkerbalRole = Pilot",
            "row[0] \t\tstartUT = 10",
            "row[0] \t\tendUT = 20",
            "row[0] \t\tendState = 3",
            "row[0] \t\txpGained = 0",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 10",
            "row[1] \t\ttype = 11",
            "row[1] \t\tactionId = <fresh>",
            "row[1] \t\trecordingId = rec-fresh",
            "row[1] \t\tseq = 2",
            "row[1] \t\tkerbalName = Bill Kerman",
            "row[1] \t\tkerbalRole = Pilot",
            "row[1] \t\tstartUT = 10",
            "row[1] \t\tendUT = 20",
            "row[1] \t\tendState = 3",
            "row[1] \t\txpGained = 0",
            "row[1] \t}",
            "row[1] }",
            "log [Parsek][INFO][LedgerOrchestrator] CreateKerbalAssignmentActions: 2 crew members from 'rec-fresh'",
            "log [Parsek][VERBOSE][LedgerOrchestrator] MigrateKerbalAssignments: re-derived recording 'rec-fresh' rows=2 inheritedActionIds=0 freshActionIds=2",
            "log [Parsek][VERBOSE][Ledger] ReplaceActionsForRecording: type=KerbalAssignment, recordingId='rec-fresh', removed=0, inserted=2, total=2",
            "log [Parsek][INFO][LedgerOrchestrator] MigrateKerbalAssignments: repaired 1 recording(s) (oldRows=0, newRows=2, inheritedActionIds=0, freshActionIds=2)",
            "log [Parsek][INFO][KerbalLoad] repair summary: slotSource=none slots=0 chainEntries=0 ignored=0 chainExtensions=0 repairedRecordings=1 oldRows=0 newRows=2 remappedRows=0 endStateRewrites=0 touristRowsSkipped=0 retiredRecreated=0 retiredKept=0 deletedUnused=0",
            "== K02b second pass, rows match",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 10",
            "row[0] \t\ttype = 11",
            "row[0] \t\tactionId = <fresh>",
            "row[0] \t\trecordingId = rec-fresh",
            "row[0] \t\tseq = 1",
            "row[0] \t\tkerbalName = Jebediah Kerman",
            "row[0] \t\tkerbalRole = Pilot",
            "row[0] \t\tstartUT = 10",
            "row[0] \t\tendUT = 20",
            "row[0] \t\tendState = 3",
            "row[0] \t\txpGained = 0",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 10",
            "row[1] \t\ttype = 11",
            "row[1] \t\tactionId = <fresh>",
            "row[1] \t\trecordingId = rec-fresh",
            "row[1] \t\tseq = 2",
            "row[1] \t\tkerbalName = Bill Kerman",
            "row[1] \t\tkerbalRole = Pilot",
            "row[1] \t\tstartUT = 10",
            "row[1] \t\tendUT = 20",
            "row[1] \t\tendState = 3",
            "row[1] \t\txpGained = 0",
            "row[1] \t}",
            "row[1] }",
            "log [Parsek][INFO][LedgerOrchestrator] CreateKerbalAssignmentActions: 2 crew members from 'rec-fresh'",
            "== K03 stand-in row rewritten to its owner",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 10",
            "row[0] \t\ttype = 11",
            "row[0] \t\tactionId = seed-leia",
            "row[0] \t\trecordingId = rec-standin",
            "row[0] \t\tseq = 1",
            "row[0] \t\tkerbalName = Jebediah Kerman",
            "row[0] \t\tkerbalRole = Pilot",
            "row[0] \t\tstartUT = 10",
            "row[0] \t\tendUT = 20",
            "row[0] \t\tendState = 2",
            "row[0] \t\txpGained = 0",
            "row[0] \t}",
            "row[0] }",
            "log [Parsek][INFO][LedgerOrchestrator] CreateKerbalAssignmentActions: 1 crew members from 'rec-standin'",
            "log [Parsek][VERBOSE][LedgerOrchestrator] InheritKerbalAssignmentActionIds: row 'Jebediah Kerman' in recording 'rec-standin' inherits the id of stored row 'Leia Kerman' (same slot owner 'Jebediah Kerman')",
            "log [Parsek][VERBOSE][LedgerOrchestrator] MigrateKerbalAssignments: re-derived recording 'rec-standin' rows=1 inheritedActionIds=1 freshActionIds=0",
            "log [Parsek][VERBOSE][Ledger] ReplaceActionsForRecording: type=KerbalAssignment, recordingId='rec-standin', removed=1, inserted=1, total=1",
            "log [Parsek][INFO][LedgerOrchestrator] MigrateKerbalAssignments: repaired 1 recording(s) (oldRows=1, newRows=1, inheritedActionIds=1, freshActionIds=0)",
            "log [Parsek][INFO][KerbalLoad] repair summary: slotSource=none slots=0 chainEntries=0 ignored=0 chainExtensions=0 repairedRecordings=1 oldRows=1 newRows=1 remappedRows=1 endStateRewrites=1 touristRowsSkipped=0 retiredRecreated=0 retiredKept=0 deletedUnused=0",
            "log [Parsek][INFO][KerbalLoad] repair samples: remap rec-standin Leia Kerman->Jebediah Kerman; end-state rec-standin Jebediah Kerman Unknown->Recovered",
            "== K04 end state changed, action id inherited",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 30",
            "row[0] \t\ttype = 11",
            "row[0] \t\tactionId = seed-val",
            "row[0] \t\trecordingId = rec-dead",
            "row[0] \t\tseq = 1",
            "row[0] \t\tkerbalName = Valentina Kerman",
            "row[0] \t\tkerbalRole = Pilot",
            "row[0] \t\tstartUT = 30",
            "row[0] \t\tendUT = 45",
            "row[0] \t\tendState = 1",
            "row[0] \t\txpGained = 0",
            "row[0] \t}",
            "row[0] }",
            "log [Parsek][INFO][LedgerOrchestrator] NeedsCrewEndStatePopulation: recording='rec-dead' admitted via ghost-visual-only crew source (no vessel snapshot, terminalState=Destroyed, hasCrew=True)",
            "log [Parsek][VERBOSE][KerbalsModule] InferCrewEndState: crew='Valentina Kerman' terminalState=Destroyed -> Dead",
            "log [Parsek][INFO][KerbalsModule] PopulateCrewEndStates: recording='Ship rec-dead' (id=rec-dead) crew=1 aboard=0 dead=1 recovered=0 unknown=0",
            "log [Parsek][VERBOSE][KerbalsModule] Crew death respawn policy not stamped: recording='rec-dead' deadCrew=1 - no game difficulty readable; the death stays permanent",
            "log [Parsek][INFO][LedgerOrchestrator] CreateKerbalAssignmentActions: 1 crew members from 'rec-dead'",
            "log [Parsek][VERBOSE][LedgerOrchestrator] MigrateKerbalAssignments: re-derived recording 'rec-dead' rows=1 inheritedActionIds=1 freshActionIds=0",
            "log [Parsek][VERBOSE][Ledger] ReplaceActionsForRecording: type=KerbalAssignment, recordingId='rec-dead', removed=1, inserted=1, total=1",
            "log [Parsek][INFO][LedgerOrchestrator] MigrateKerbalAssignments: repaired 1 recording(s) (oldRows=1, newRows=1, inheritedActionIds=1, freshActionIds=0)",
            "log [Parsek][INFO][KerbalLoad] repair summary: slotSource=none slots=0 chainEntries=0 ignored=0 chainExtensions=0 repairedRecordings=1 oldRows=1 newRows=1 remappedRows=0 endStateRewrites=1 touristRowsSkipped=0 retiredRecreated=0 retiredKept=0 deletedUnused=0",
            "log [Parsek][INFO][KerbalLoad] repair samples: end-state rec-dead Valentina Kerman Recovered->Dead",
            "== K05a tourist role carried, rows match",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 50",
            "row[0] \t\ttype = 11",
            "row[0] \t\tactionId = seed-tourist",
            "row[0] \t\trecordingId = rec-tourist",
            "row[0] \t\tseq = 1",
            "row[0] \t\tcseq = 1",
            "row[0] \t\tkerbalName = Gerdan Kerman",
            "row[0] \t\tkerbalRole = Tourist",
            "row[0] \t\tstartUT = 50",
            "row[0] \t\tendUT = 60",
            "row[0] \t\tendState = 3",
            "row[0] \t\txpGained = 0",
            "row[0] \t}",
            "row[0] }",
            "log [Parsek][INFO][LedgerOrchestrator] CreateKerbalAssignmentActions: 1 crew members from 'rec-tourist'",
            "log [Parsek][VERBOSE][LedgerOrchestrator] MigrateKerbalAssignments: recording 'rec-tourist' kept the tourist role on 1 row(s) the roster can no longer confirm",
            "== K05b tourist role carried, a crew row missing",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 50",
            "row[0] \t\ttype = 11",
            "row[0] \t\tactionId = seed-tourist",
            "row[0] \t\trecordingId = rec-tourist",
            "row[0] \t\tseq = 1",
            "row[0] \t\tkerbalName = Gerdan Kerman",
            "row[0] \t\tkerbalRole = Tourist",
            "row[0] \t\tstartUT = 50",
            "row[0] \t\tendUT = 60",
            "row[0] \t\tendState = 3",
            "row[0] \t\txpGained = 0",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 50",
            "row[1] \t\ttype = 11",
            "row[1] \t\tactionId = <fresh>",
            "row[1] \t\trecordingId = rec-tourist",
            "row[1] \t\tseq = 2",
            "row[1] \t\tkerbalName = Bill Kerman",
            "row[1] \t\tkerbalRole = Pilot",
            "row[1] \t\tstartUT = 50",
            "row[1] \t\tendUT = 60",
            "row[1] \t\tendState = 3",
            "row[1] \t\txpGained = 0",
            "row[1] \t}",
            "row[1] }",
            "log [Parsek][INFO][LedgerOrchestrator] CreateKerbalAssignmentActions: 2 crew members from 'rec-tourist'",
            "log [Parsek][VERBOSE][LedgerOrchestrator] MigrateKerbalAssignments: recording 'rec-tourist' kept the tourist role on 1 row(s) the roster can no longer confirm",
            "log [Parsek][VERBOSE][LedgerOrchestrator] MigrateKerbalAssignments: re-derived recording 'rec-tourist' rows=2 inheritedActionIds=1 freshActionIds=1",
            "log [Parsek][VERBOSE][Ledger] ReplaceActionsForRecording: type=KerbalAssignment, recordingId='rec-tourist', removed=1, inserted=2, total=2",
            "log [Parsek][INFO][LedgerOrchestrator] MigrateKerbalAssignments: repaired 1 recording(s) (oldRows=1, newRows=2, inheritedActionIds=1, freshActionIds=1)",
            "log [Parsek][INFO][KerbalLoad] repair summary: slotSource=none slots=0 chainEntries=0 ignored=0 chainExtensions=0 repairedRecordings=1 oldRows=1 newRows=2 remappedRows=0 endStateRewrites=0 touristRowsSkipped=1 retiredRecreated=0 retiredKept=0 deletedUnused=0",
            "log [Parsek][INFO][KerbalLoad] repair samples: tourist-skip rec-tourist count=1",
            "== K06 several recordings, orphan and untagged rows",
            "row[0] ROOT",
            "row[0] {",
            "row[0] \tGAME_ACTION",
            "row[0] \t{",
            "row[0] \t\tut = 130",
            "row[0] \t\ttype = 11",
            "row[0] \t\tactionId = seed-bob",
            "row[0] \t\trecordingId = rec-two",
            "row[0] \t\tseq = 1",
            "row[0] \t\tkerbalName = Bob Kerman",
            "row[0] \t\tkerbalRole = Pilot",
            "row[0] \t\tstartUT = 130",
            "row[0] \t\tendUT = 150",
            "row[0] \t\tendState = 1",
            "row[0] \t\txpGained = 0",
            "row[0] \t}",
            "row[0] }",
            "row[1] ROOT",
            "row[1] {",
            "row[1] \tGAME_ACTION",
            "row[1] \t{",
            "row[1] \t\tut = 130",
            "row[1] \t\ttype = 11",
            "row[1] \t\tactionId = <fresh>",
            "row[1] \t\trecordingId = rec-two",
            "row[1] \t\tseq = 2",
            "row[1] \t\tkerbalName = Bill Kerman",
            "row[1] \t\tkerbalRole = Pilot",
            "row[1] \t\tstartUT = 130",
            "row[1] \t\tendUT = 150",
            "row[1] \t\tendState = 1",
            "row[1] \t\txpGained = 0",
            "row[1] \t}",
            "row[1] }",
            "row[2] ROOT",
            "row[2] {",
            "row[2] \tGAME_ACTION",
            "row[2] \t{",
            "row[2] \t\tut = 10",
            "row[2] \t\ttype = 11",
            "row[2] \t\tactionId = seed-orphan",
            "row[2] \t\trecordingId = rec-uncommitted",
            "row[2] \t\tseq = 1",
            "row[2] \t\tcseq = 3",
            "row[2] \t\tkerbalName = Bob Kerman",
            "row[2] \t\tkerbalRole = Scientist",
            "row[2] \t\tstartUT = 10",
            "row[2] \t\tendUT = 20",
            "row[2] \t\tendState = 2",
            "row[2] \t\txpGained = 0",
            "row[2] \t}",
            "row[2] }",
            "row[3] ROOT",
            "row[3] {",
            "row[3] \tGAME_ACTION",
            "row[3] \t{",
            "row[3] \t\tut = 5",
            "row[3] \t\ttype = 11",
            "row[3] \t\tactionId = seed-untagged",
            "row[3] \t\tseq = 1",
            "row[3] \t\tcseq = 4",
            "row[3] \t\tkerbalName = Bill Kerman",
            "row[3] \t\tkerbalRole = Engineer",
            "row[3] \t\tstartUT = 5",
            "row[3] \t\tendUT = 6",
            "row[3] \t\tendState = 2",
            "row[3] \t\txpGained = 0",
            "row[3] \t}",
            "row[3] }",
            "row[4] ROOT",
            "row[4] {",
            "row[4] \tGAME_ACTION",
            "row[4] \t{",
            "row[4] \t\tut = 100",
            "row[4] \t\ttype = 11",
            "row[4] \t\tactionId = <fresh>",
            "row[4] \t\trecordingId = rec-one",
            "row[4] \t\tseq = 1",
            "row[4] \t\tkerbalName = Jebediah Kerman",
            "row[4] \t\tkerbalRole = Pilot",
            "row[4] \t\tstartUT = 100",
            "row[4] \t\tendUT = 120",
            "row[4] \t\tendState = 3",
            "row[4] \t\txpGained = 0",
            "row[4] \t}",
            "row[4] }",
            "log [Parsek][INFO][LedgerOrchestrator] CreateKerbalAssignmentActions: 1 crew members from 'rec-one'",
            "log [Parsek][VERBOSE][LedgerOrchestrator] MigrateKerbalAssignments: re-derived recording 'rec-one' rows=1 inheritedActionIds=0 freshActionIds=1",
            "log [Parsek][VERBOSE][Ledger] ReplaceActionsForRecording: type=KerbalAssignment, recordingId='rec-one', removed=0, inserted=1, total=5",
            "log [Parsek][INFO][LedgerOrchestrator] NeedsCrewEndStatePopulation: recording='rec-two' admitted via ghost-visual-only crew source (no vessel snapshot, terminalState=Destroyed, hasCrew=True)",
            "log [Parsek][VERBOSE][KerbalsModule] InferCrewEndState: crew='Bob Kerman' terminalState=Destroyed -> Dead",
            "log [Parsek][VERBOSE][KerbalsModule] InferCrewEndState: crew='Bill Kerman' terminalState=Destroyed -> Dead",
            "log [Parsek][INFO][KerbalsModule] PopulateCrewEndStates: recording='Ship rec-two' (id=rec-two) crew=2 aboard=0 dead=2 recovered=0 unknown=0",
            "log [Parsek][VERBOSE][KerbalsModule] Crew death respawn policy not stamped: recording='rec-two' deadCrew=2 - no game difficulty readable; the death stays permanent",
            "log [Parsek][INFO][LedgerOrchestrator] CreateKerbalAssignmentActions: 2 crew members from 'rec-two'",
            "log [Parsek][VERBOSE][LedgerOrchestrator] MigrateKerbalAssignments: re-derived recording 'rec-two' rows=2 inheritedActionIds=1 freshActionIds=1",
            "log [Parsek][VERBOSE][Ledger] ReplaceActionsForRecording: type=KerbalAssignment, recordingId='rec-two', removed=2, inserted=2, total=5",
            "log [Parsek][INFO][LedgerOrchestrator] MigrateKerbalAssignments: repaired 2 recording(s) (oldRows=2, newRows=3, inheritedActionIds=1, freshActionIds=2)",
            "log [Parsek][INFO][KerbalLoad] repair summary: slotSource=none slots=0 chainEntries=0 ignored=0 chainExtensions=0 repairedRecordings=2 oldRows=2 newRows=3 remappedRows=0 endStateRewrites=0 touristRowsSkipped=0 retiredRecreated=0 retiredKept=0 deletedUnused=0",
        }) + "\n";
    }
}
