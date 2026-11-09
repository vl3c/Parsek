using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization golden for
    /// <see cref="LedgerOrchestrator.BuildContractReinstateCutoffsForPatch"/>, written against
    /// the method as it stood before the wave-2 W7b extraction and kept unchanged through it.
    /// Each scenario installs a scenario (or none), ledger rows and committed recordings, and
    /// records the returned per-contract cutoffs (null, or every guid with its UT as a "R"
    /// string, sorted by guid) and every log line. Scenarios: no scenario, null and empty
    /// tombstone lists, ledger rows that never map (no id, no contract, an unparseable
    /// contract id, a non-contract type), tombstones that never map (null, no id, no
    /// retiring recording, an unknown action), the retiring recording's StartUT against the
    /// tombstone UT fallback, the per-contract minimum, the per-recording cache (a found id
    /// and a missing id each named twice), a NaN tombstone UT, a repeated ActionId across two
    /// contract rows, and a retiring recording without an explicit start.
    /// </summary>
    [Collection("Sequential")]
    public class BuildContractReinstateCutoffsCharacterizationTests : IDisposable
    {
        private static readonly Guid GA = new Guid("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid GB = new Guid("bbbbbbbb-0000-0000-0000-000000000002");
        private static readonly Guid GC = new Guid("cccccccc-0000-0000-0000-000000000003");

        private readonly List<string> logLines = new List<string>();

        public BuildContractReinstateCutoffsCharacterizationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            Reset();
        }

        public void Dispose()
        {
            Reset();
            RecordingStore.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void Reset()
        {
            LedgerOrchestrator.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            EffectiveState.ResetCachesForTesting();
            Ledger.ResetForTesting();
            GameStateStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        private static GameAction Contract(string id, GameActionType type, string contractId)
        {
            var a = new GameAction { Type = type, ContractId = contractId, UT = 50.0 };
            if (id != null)
                a.ActionId = id;
            Ledger.AddAction(a);
            return a;
        }

        private static void Rec(string id, double explicitStartUT = double.NaN)
        {
            RecordingStore.AddRecordingWithTreeForTesting(new Recording
            {
                RecordingId = id,
                VesselName = id,
                ExplicitStartUT = explicitStartUT,
            });
        }

        private static LedgerTombstone T(string actionId, string retiring, double ut) =>
            new LedgerTombstone { ActionId = actionId, RetiringRecordingId = retiring, UT = ut };

        private static void Install(List<LedgerTombstone> tombstones)
        {
            ParsekScenario.SetInstanceForTesting(new ParsekScenario { LedgerTombstones = tombstones });
        }

        private void Case(StringBuilder sb, string label)
        {
            logLines.Clear();
            var cutoffs = LedgerOrchestrator.BuildContractReinstateCutoffsForPatch();
            sb.Append("== ").Append(label).Append('\n');
            if (cutoffs == null)
            {
                sb.Append("cutoffs=null\n");
            }
            else
            {
                var keys = new List<Guid>(cutoffs.Keys);
                keys.Sort((x, y) => string.CompareOrdinal(x.ToString(), y.ToString()));
                sb.Append("cutoffs=").Append(cutoffs.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
                foreach (Guid k in keys)
                    sb.Append("  ").Append(k.ToString()).Append('=')
                        .Append(cutoffs[k].ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            }
            foreach (string line in logLines)
                sb.Append("log ").Append(line).Append('\n');
            Reset();
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
        public void BuildContractReinstateCutoffsForPatch_Golden()
        {
            var sb = new StringBuilder();
            logLines.Clear();

            Contract("acc-a", GameActionType.ContractAccept, GA.ToString());
            Case(sb, "C01 no scenario");

            Contract("acc-a", GameActionType.ContractAccept, GA.ToString());
            Install(null);
            Case(sb, "C02 null tombstone list");

            Contract("acc-a", GameActionType.ContractAccept, GA.ToString());
            Install(new List<LedgerTombstone>());
            Case(sb, "C03 empty tombstone list");

            Ledger.AddAction(new GameAction { ActionId = "fe", Type = GameActionType.FundsEarning, UT = 1.0 });
            Install(new List<LedgerTombstone> { T("fe", "rec-a", 900.0) });
            Case(sb, "C04 no contract rows in the ledger");

            Contract("", GameActionType.ContractComplete, GA.ToString());
            Contract("no-contract", GameActionType.ContractComplete, "");
            Contract("bad-guid", GameActionType.ContractFail, "not-a-guid");
            Contract("milestone", GameActionType.MilestoneAchievement, GB.ToString());
            Rec("rec-a", 300.0);
            Install(new List<LedgerTombstone>
            {
                T("", "rec-a", 900.0),
                T("no-contract", "rec-a", 900.0),
                T("bad-guid", "rec-a", 900.0),
                T("milestone", "rec-a", 900.0),
            });
            Case(sb, "C05 ledger rows that never map");

            Contract("cmp-a", GameActionType.ContractComplete, GA.ToString());
            Rec("rec-a", 300.0);
            Install(new List<LedgerTombstone>
            {
                null,
                T(null, "rec-a", 900.0),
                T("", "rec-a", 900.0),
                T("cmp-a", null, 900.0),
                T("cmp-a", "", 900.0),
                T("unknown", "rec-a", 900.0),
            });
            Case(sb, "C06 tombstones that never map");

            Contract("acc-a", GameActionType.ContractAccept, GA.ToString());
            Contract("cmp-a", GameActionType.ContractComplete, GA.ToString());
            Contract("fail-b", GameActionType.ContractFail, GB.ToString());
            Contract("cancel-c", GameActionType.ContractCancel, GC.ToString());
            Rec("rec-early", 300.0);
            Rec("rec-late", 800.0);
            Install(new List<LedgerTombstone>
            {
                T("cmp-a", "rec-late", 900.0),
                T("acc-a", "rec-early", 950.0),
                T("fail-b", "rec-missing", 1234.0),
                T("fail-b", "rec-missing", 1200.5),
                T("cancel-c", "rec-late", 990.0),
                T("cancel-c", "rec-early", 10.0),
            });
            Case(sb, "C07 retiring StartUT, fallback, per-contract min and the cache");

            Contract("cmp-a", GameActionType.ContractComplete, GA.ToString());
            Contract("fail-b", GameActionType.ContractFail, GB.ToString());
            Install(new List<LedgerTombstone>
            {
                T("cmp-a", "rec-missing", double.NaN),
                T("fail-b", "rec-missing", 77.25),
            });
            Case(sb, "C08 NaN tombstone UT with a missing recording");

            Contract("dup", GameActionType.ContractAccept, GA.ToString());
            Contract("dup", GameActionType.ContractComplete, GB.ToString());
            Rec("rec-a", 300.0);
            Install(new List<LedgerTombstone> { T("dup", "rec-a", 900.0) });
            Case(sb, "C09 one ActionId on two contract rows");

            Contract("cmp-a", GameActionType.ContractComplete, GA.ToString());
            Rec("rec-nostart");
            Install(new List<LedgerTombstone> { T("cmp-a", "rec-nostart", 900.0) });
            Case(sb, "C10 retiring recording without an explicit start");

            Contract("cmp-a", GameActionType.ContractComplete, GA.ToString());
            Rec("rec-a", 300.0);
            Install(new List<LedgerTombstone>
            {
                T("cmp-a", "rec-missing", 250.0),
                T("cmp-a", "rec-a", 900.0),
            });
            Case(sb, "C11 fallback lower than the found StartUT");

            AssertGolden("BuildContractReinstateCutoffsForPatch", Golden, sb.ToString());
        }

        private static readonly string Golden = string.Join("\n", new[]
        {
            "== C01 no scenario",
            "cutoffs=null",
            "== C02 null tombstone list",
            "cutoffs=null",
            "== C03 empty tombstone list",
            "cutoffs=null",
            "== C04 no contract rows in the ledger",
            "cutoffs=null",
            "== C05 ledger rows that never map",
            "cutoffs=null",
            "== C06 tombstones that never map",
            "cutoffs=null",
            "== C07 retiring StartUT, fallback, per-contract min and the cache",
            "cutoffs=3",
            "  aaaaaaaa-0000-0000-0000-000000000001=300",
            "  bbbbbbbb-0000-0000-0000-000000000002=1200.5",
            "  cccccccc-0000-0000-0000-000000000003=300",
            "== C08 NaN tombstone UT with a missing recording",
            "cutoffs=1",
            "  bbbbbbbb-0000-0000-0000-000000000002=77.25",
            "== C09 one ActionId on two contract rows",
            "cutoffs=1",
            "  bbbbbbbb-0000-0000-0000-000000000002=300",
            "== C10 retiring recording without an explicit start",
            "cutoffs=1",
            "  aaaaaaaa-0000-0000-0000-000000000001=0",
            "== C11 fallback lower than the found StartUT",
            "cutoffs=1",
            "  aaaaaaaa-0000-0000-0000-000000000001=250",
        }) + "\n";
    }
}
