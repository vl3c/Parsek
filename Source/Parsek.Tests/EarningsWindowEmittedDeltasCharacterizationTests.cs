using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization golden for <c>LedgerOrchestrator.ComputeEarningsWindowEmittedDeltas</c>,
    /// written against the method as it stood before the wave-2 W7b extraction and kept
    /// unchanged through it. The method is reached by reflection (it is private, and so is
    /// the struct it returns), so the same test reads the original and the split method.
    /// Each scenario hands it a row list: every switch arm, the Effective gates on and off,
    /// rows inside the reputation seed, kerbal-death penalties paired and unpaired with
    /// their VesselLoss event, strategy conversion rows folded from the ledger (scoped and
    /// unscoped) and null rows. It records every field of the returned struct, the totals
    /// as bit patterns, and every log line.
    /// </summary>
    [Collection("Sequential")]
    public class EarningsWindowEmittedDeltasCharacterizationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public EarningsWindowEmittedDeltasCharacterizationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static readonly MethodInfo Compute = typeof(LedgerOrchestrator).GetMethod(
            "ComputeEarningsWindowEmittedDeltas", BindingFlags.NonPublic | BindingFlags.Static);

        private static string Bits(double d) =>
            BitConverter.DoubleToInt64Bits(d).ToString("X16", CultureInfo.InvariantCulture);

        private static string Describe(object deltas)
        {
            var sb = new StringBuilder();
            var fields = deltas.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
            Array.Sort(fields, (a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (FieldInfo f in fields)
            {
                object v = f.GetValue(deltas);
                sb.Append(f.Name).Append('=');
                if (v is double d)
                    sb.Append("d:").Append(Bits(d));
                else if (v is List<GameAction> list)
                {
                    sb.Append('[');
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(list[i].ActionId ?? "<null>");
                    }
                    sb.Append(']');
                }
                else if (v == null)
                    sb.Append("<null>");
                else
                    sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static GameAction Row(string id, GameActionType type, double ut = 150.0, string rec = "rec-a",
            bool effective = true)
        {
            return new GameAction { ActionId = id, Type = type, UT = ut, RecordingId = rec, Effective = effective };
        }

        private static GameStateEvent RepEvent(string key, double before, double after, double ut, string rec)
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.ReputationChanged,
                key = key,
                valueBefore = before,
                valueAfter = after,
                recordingId = rec,
            };
        }

        /// <summary>One row of every arm, each with distinct power-of-two amounts.</summary>
        private static List<GameAction> EveryArm(bool effective)
        {
            var rows = new List<GameAction>();
            var a = Row("fe", GameActionType.FundsEarning, effective: effective); a.FundsAwarded = 1f; rows.Add(a);
            a = Row("fs", GameActionType.FundsSpending, effective: effective); a.FundsSpent = 2f; rows.Add(a);
            a = Row("re", GameActionType.ReputationEarning, effective: effective); a.EffectiveRep = 4f; a.NominalRep = 64f; rows.Add(a);
            a = Row("rp", GameActionType.ReputationPenalty, effective: effective); a.EffectiveRep = -8f; a.NominalPenalty = 8f;
            a.RepPenaltySource = ReputationPenaltySource.Other; rows.Add(a);
            a = Row("cc", GameActionType.ContractComplete, effective: effective);
            a.TransformedFundsReward = 16f; a.EffectiveRep = 32f; a.TransformedScienceReward = 0.5f; rows.Add(a);
            a = Row("cp", GameActionType.ContractParameterComplete, effective: effective);
            a.TransformedFundsReward = 128f; a.EffectiveRep = 0.25f; a.TransformedScienceReward = 256f; rows.Add(a);
            a = Row("cf", GameActionType.ContractFail, effective: effective); a.FundsPenalty = 512f; a.EffectiveRep = -0.125f; rows.Add(a);
            a = Row("cx", GameActionType.ContractCancel, effective: effective); a.FundsPenalty = 1024f; a.EffectiveRep = -2048f; rows.Add(a);
            a = Row("ca", GameActionType.ContractAccept, effective: effective); a.AdvanceFunds = 4096f; rows.Add(a);
            a = Row("fu", GameActionType.FacilityUpgrade, effective: effective); a.FacilityCost = 8192f; rows.Add(a);
            a = Row("fr", GameActionType.FacilityRepair, effective: effective); a.FacilityCost = 16384f; rows.Add(a);
            a = Row("ma", GameActionType.MilestoneAchievement, effective: effective);
            a.MilestoneFundsAwarded = 32768f; a.EffectiveRep = 65536f; a.MilestoneScienceAwarded = 0.0625f; rows.Add(a);
            a = Row("se", GameActionType.ScienceEarning, effective: effective); a.EffectiveScience = 131072f; a.ScienceAwarded = 3f; rows.Add(a);
            a = Row("ss", GameActionType.ScienceSpending, effective: effective); a.Cost = 262144f; rows.Add(a);
            a = Row("sd", GameActionType.StrategyScienceDebit, effective: effective); a.Cost = 0.03125f; rows.Add(a);
            a = Row("sc", GameActionType.StrategyScienceCredit, effective: effective); a.ScienceAwarded = 524288f; rows.Add(a);
            a = Row("sl", GameActionType.ScienceLabEarning, effective: effective); a.ScienceAwarded = 1048576f; rows.Add(a);
            a = Row("ri", GameActionType.ReputationInitial, effective: effective); a.InitialReputation = 9f; rows.Add(a);
            return rows;
        }

        private void Case(StringBuilder sb, string label, List<GameAction> newActions,
            IReadOnlyList<GameStateEvent> events, IReadOnlyList<GameAction> ledgerActions,
            double startUT, double endUT, string recordingId)
        {
            logLines.Clear();
            object result = Compute.Invoke(null, new object[] { newActions, events, ledgerActions, startUT, endUT, recordingId });
            sb.Append("== ").Append(label).Append('\n');
            sb.Append(Describe(result));
            foreach (string line in logLines)
                sb.Append("log ").Append(line).Append('\n');
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
        public void ComputeEarningsWindowEmittedDeltas_Golden()
        {
            Assert.NotNull(Compute);
            var none = new List<GameStateEvent>();
            var noRows = new List<GameAction>();
            var sb = new StringBuilder();

            Case(sb, "E01 null rows, null ledger", null, none, null, 100.0, 200.0, "rec-a");
            Case(sb, "E02 empty rows", new List<GameAction>(), none, noRows, 100.0, 200.0, "rec-a");
            Case(sb, "E03 every arm effective", EveryArm(true), none, noRows, 100.0, 200.0, "rec-a");
            Case(sb, "E04 every arm not effective", EveryArm(false), none, noRows, 100.0, 200.0, "rec-a");

            var withNulls = EveryArm(true);
            withNulls.Insert(0, null);
            withNulls.Insert(5, null);
            withNulls.Add(null);
            Case(sb, "E05 null rows inside the list", withNulls, none, noRows, 100.0, 200.0, "rec-a");

            // Inside the reputation seed: the rep leg is the estimate, not EffectiveRep.
            var seed = new List<GameAction>();
            var r = Row("se-re", GameActionType.ReputationEarning); r.EffectiveRep = 0f; r.NominalRep = 10f;
            r.InsideReputationSeed = true; seed.Add(r);
            r = Row("se-rp-kd", GameActionType.ReputationPenalty); r.EffectiveRep = 0f; r.NominalPenalty = 6f;
            r.RepPenaltySource = ReputationPenaltySource.KerbalDeath; r.InsideReputationSeed = true; seed.Add(r);
            r = Row("se-ma", GameActionType.MilestoneAchievement); r.EffectiveRep = 0f; r.MilestoneRepAwarded = 2f;
            r.MilestoneFundsAwarded = 100f; r.InsideReputationSeed = true; seed.Add(r);
            r = Row("se-fe", GameActionType.FundsEarning); r.FundsAwarded = 7f; r.InsideReputationSeed = true; seed.Add(r);
            var seedEvents = new List<GameStateEvent> { RepEvent("Progression", 50.0, 52.0, 120.0, "rec-a") };
            Case(sb, "E06 rows inside the reputation seed", seed, seedEvents, noRows, 100.0, 200.0, "rec-a");

            // Kerbal-death penalty: paired when the VesselLoss event's magnitude agrees.
            var kd = new List<GameAction>();
            r = Row("kd-paired", GameActionType.ReputationPenalty); r.RepPenaltySource = ReputationPenaltySource.KerbalDeath;
            r.NominalPenalty = 10f; r.EffectiveRep = -10f; kd.Add(r);
            r = Row("kd-drift", GameActionType.ReputationPenalty, rec: "rec-b"); r.RepPenaltySource = ReputationPenaltySource.KerbalDeath;
            r.NominalPenalty = 25f; r.EffectiveRep = -25f; kd.Add(r);
            r = Row("kd-untagged", GameActionType.ReputationPenalty, rec: null); r.RepPenaltySource = ReputationPenaltySource.KerbalDeath;
            r.NominalPenalty = 10f; r.EffectiveRep = -10f; kd.Add(r);
            var kdEvents = new List<GameStateEvent>
            {
                RepEvent(KerbalDeathRepPenalty.VesselLossEventKey, 100.0, 90.0, 150.0, "rec-a"),
                RepEvent(KerbalDeathRepPenalty.VesselLossEventKey, 100.0, 80.0, 150.0, "rec-b"),
            };
            Case(sb, "E07 kerbal-death penalties", kd, kdEvents, noRows, 100.0, 200.0, "rec-a");

            // Strategy conversion rows live only in the ledger; the scoped walk folds those
            // carrying the tag (past the window end too), the unscoped one those in the window.
            var ledger = new List<GameAction>();
            r = Row("sc-in", GameActionType.StrategyScienceCredit); r.ScienceAwarded = 4f; ledger.Add(r);
            r = Row("sd-in", GameActionType.StrategyScienceDebit); r.Cost = 2f; r.ConversionSource = StrategyConversionSource.Converter; ledger.Add(r);
            r = Row("sd-exchanger", GameActionType.StrategyScienceDebit); r.Cost = 64f; r.ConversionSource = StrategyConversionSource.Exchanger; ledger.Add(r);
            r = Row("fs-conv", GameActionType.FundsSpending); r.FundsSpent = 8f; r.FundsSpendingSource = FundsSpendingSource.StrategyConverter; ledger.Add(r);
            r = Row("sc-late", GameActionType.StrategyScienceCredit, ut: 250.0); r.ScienceAwarded = 16f; ledger.Add(r);
            r = Row("sc-other", GameActionType.StrategyScienceCredit, rec: "rec-z"); r.ScienceAwarded = 32f; ledger.Add(r);
            r = Row("sc-untagged", GameActionType.StrategyScienceCredit, rec: null); r.ScienceAwarded = 128f; ledger.Add(r);
            r = Row("sc-early", GameActionType.StrategyScienceCredit, ut: 50.0, rec: null); r.ScienceAwarded = 256f; ledger.Add(r);
            var carried = Row("sc-carried", GameActionType.StrategyScienceCredit); carried.ScienceAwarded = 1f;
            ledger.Add(carried);
            var own = new List<GameAction> { carried };
            Case(sb, "E08 conversion rows, scoped", own, none, ledger, 100.0, 200.0, "rec-a");
            Case(sb, "E09 conversion rows, unscoped", new List<GameAction>(), none, ledger, 100.0, 200.0, null);

            AssertGolden("ComputeEarningsWindowEmittedDeltas_Golden", Golden, sb.ToString());
        }

        private static readonly string Golden = string.Join("\n", new[]
        {
            "== E01 null rows, null ledger",
            "ContractAcceptCount=0",
            "EffectiveScienceActions=[]",
            "EmittedFundsDelta=d:0000000000000000",
            "EmittedRepDelta=d:0000000000000000",
            "EmittedSciDelta=d:0000000000000000",
            "FacilityRepairCount=0",
            "FacilityUpgradeCount=0",
            "InsideSeedRepRowsCounted=0",
            "KerbalDeathPairedSkipped=0",
            "StrategyConversionRowsFolded=0",
            "== E02 empty rows",
            "ContractAcceptCount=0",
            "EffectiveScienceActions=[]",
            "EmittedFundsDelta=d:0000000000000000",
            "EmittedRepDelta=d:0000000000000000",
            "EmittedSciDelta=d:0000000000000000",
            "FacilityRepairCount=0",
            "FacilityUpgradeCount=0",
            "InsideSeedRepRowsCounted=0",
            "KerbalDeathPairedSkipped=0",
            "StrategyConversionRowsFolded=0",
            "== E03 every arm effective",
            "ContractAcceptCount=1",
            "EffectiveScienceActions=[se]",
            "EmittedFundsDelta=d:40C5478000000000",
            "EmittedRepDelta=d:40EF038400000000",
            "EmittedSciDelta=d:4136010088000000",
            "FacilityRepairCount=1",
            "FacilityUpgradeCount=1",
            "InsideSeedRepRowsCounted=0",
            "KerbalDeathPairedSkipped=0",
            "StrategyConversionRowsFolded=0",
            "== E04 every arm not effective",
            "ContractAcceptCount=1",
            "EffectiveScienceActions=[]",
            "EmittedFundsDelta=d:C0B0020000000000",
            "EmittedRepDelta=d:C010000000000000",
            "EmittedSciDelta=d:4133FFFFF8000000",
            "FacilityRepairCount=0",
            "FacilityUpgradeCount=1",
            "InsideSeedRepRowsCounted=0",
            "KerbalDeathPairedSkipped=0",
            "StrategyConversionRowsFolded=0",
            "== E05 null rows inside the list",
            "ContractAcceptCount=1",
            "EffectiveScienceActions=[se]",
            "EmittedFundsDelta=d:40C5478000000000",
            "EmittedRepDelta=d:40EF038400000000",
            "EmittedSciDelta=d:4136010088000000",
            "FacilityRepairCount=1",
            "FacilityUpgradeCount=1",
            "InsideSeedRepRowsCounted=0",
            "KerbalDeathPairedSkipped=0",
            "StrategyConversionRowsFolded=0",
            "== E06 rows inside the reputation seed",
            "ContractAcceptCount=0",
            "EffectiveScienceActions=[]",
            "EmittedFundsDelta=d:405AC00000000000",
            "EmittedRepDelta=d:4017FFDCD8000000",
            "EmittedSciDelta=d:0000000000000000",
            "FacilityRepairCount=0",
            "FacilityUpgradeCount=0",
            "InsideSeedRepRowsCounted=3",
            "KerbalDeathPairedSkipped=0",
            "StrategyConversionRowsFolded=0",
            "== E07 kerbal-death penalties",
            "ContractAcceptCount=0",
            "EffectiveScienceActions=[]",
            "EmittedFundsDelta=d:0000000000000000",
            "EmittedRepDelta=d:C041800000000000",
            "EmittedSciDelta=d:0000000000000000",
            "FacilityRepairCount=0",
            "FacilityUpgradeCount=0",
            "InsideSeedRepRowsCounted=0",
            "KerbalDeathPairedSkipped=1",
            "StrategyConversionRowsFolded=0",
            "== E08 conversion rows, scoped",
            "ContractAcceptCount=0",
            "EffectiveScienceActions=[]",
            "EmittedFundsDelta=d:C020000000000000",
            "EmittedRepDelta=d:0000000000000000",
            "EmittedSciDelta=d:4033000000000000",
            "FacilityRepairCount=0",
            "FacilityUpgradeCount=0",
            "InsideSeedRepRowsCounted=0",
            "KerbalDeathPairedSkipped=0",
            "StrategyConversionRowsFolded=4",
            "== E09 conversion rows, unscoped",
            "ContractAcceptCount=0",
            "EffectiveScienceActions=[]",
            "EmittedFundsDelta=d:C020000000000000",
            "EmittedRepDelta=d:0000000000000000",
            "EmittedSciDelta=d:4064600000000000",
            "FacilityRepairCount=0",
            "FacilityUpgradeCount=0",
            "InsideSeedRepRowsCounted=0",
            "KerbalDeathPairedSkipped=0",
            "StrategyConversionRowsFolded=6",
        }) + "\n";
    }
}
