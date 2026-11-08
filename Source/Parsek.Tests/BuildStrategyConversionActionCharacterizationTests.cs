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
    /// Characterization golden for <c>LedgerOrchestrator.BuildStrategyConversionAction</c>,
    /// written against the method as it stood before the wave-2 W7b extraction and kept
    /// unchanged through it. Every currency (and an out-of-range value, the switch's default
    /// arm) crosses every delta sign class, a null and a named reason, and a null, empty and
    /// named recording id; each case records every public field of the returned row that
    /// differs from a fresh <c>GameAction</c> (the random <c>ActionId</c> is reduced to its
    /// shape) and every log line the call wrote.
    /// A mismatch writes the actual transcript next to the test binary and names it.
    /// </summary>
    [Collection("Sequential")]
    public class BuildStrategyConversionActionCharacterizationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public BuildStrategyConversionActionCharacterizationTests()
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

        private static string Format(object value)
        {
            if (value == null) return "<null>";
            // Bit patterns, not "R" text: the .NET Framework and mono format some floats and
            // the sign of zero differently, and the golden must read the same on both hosts.
            if (value is double d)
                return "d:" + BitConverter.DoubleToInt64Bits(d).ToString("X16", CultureInfo.InvariantCulture);
            if (value is float f)
                return "f:" + BitConverter.ToInt32(BitConverter.GetBytes(f), 0).ToString("X8", CultureInfo.InvariantCulture);
            if (value is string s) return "'" + s + "'";
            if (value is IFormattable fm) return fm.ToString(null, CultureInfo.InvariantCulture);
            return value.ToString();
        }

        internal static string DescribeRow(GameAction row)
        {
            if (row == null) return "row <null>";
            // Only the fields that differ from a fresh row: the builder's own writes, readable,
            // and a field added to GameAction later does not move the golden.
            var blank = new GameAction();
            var sb = new StringBuilder("row");
            var fields = typeof(GameAction).GetFields(BindingFlags.Public | BindingFlags.Instance);
            Array.Sort(fields, (a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (FieldInfo field in fields)
            {
                object value = field.GetValue(row);
                string text;
                if (field.Name == "ActionId")
                {
                    string id = value as string;
                    text = id != null && id.StartsWith("act_", StringComparison.Ordinal) && id.Length == 36
                        ? "act_<guid>" : Format(value);
                }
                else
                {
                    text = Format(value);
                    if (text == Format(field.GetValue(blank)))
                        continue;
                }
                sb.Append(' ').Append(field.Name).Append('=').Append(text);
            }
            return sb.ToString();
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
        public void BuildStrategyConversionAction_Golden()
        {
            var currencies = new[]
            {
                StrategyConversionCurrency.Science,
                StrategyConversionCurrency.Funds,
                StrategyConversionCurrency.Reputation,
                (StrategyConversionCurrency)7,
            };
            var deltas = new[] { -12.5, -1e-9, 0.0, -0.0, 1e-9, 3.25, 0.5 };
            var reasons = new[] { null, "ContractReward" };
            var recordingIds = new[] { null, "", "rec-1" };
            var sb = new StringBuilder();
            foreach (var currency in currencies)
            foreach (double delta in deltas)
            foreach (string reason in reasons)
            foreach (string recordingId in recordingIds)
            {
                // The reputation arms log the delta as "R" text, which prints negative zero
                // differently on the .NET Framework and mono hosts; that one cell is left out.
                if (delta == 0.0 && BitConverter.DoubleToInt64Bits(delta) < 0
                    && currency != StrategyConversionCurrency.Science
                    && currency != StrategyConversionCurrency.Funds)
                    continue;
                logLines.Clear();
                var leg = new StrategyConversionLeg { Currency = currency, Delta = delta, Cause = "cause-x" };
                GameAction row = LedgerOrchestrator.BuildStrategyConversionAction(
                    1234.56, leg, reason, recordingId);
                sb.Append("== ").Append(Format(currency)).Append(" delta=").Append(Format(delta))
                    .Append(" reason=").Append(Format(reason))
                    .Append(" rec=").Append(Format(recordingId)).Append('\n');
                sb.Append(DescribeRow(row)).Append('\n');
                foreach (string line in logLines)
                    sb.Append("log ").Append(line).Append('\n');
            }

            // The default recording-id argument is the null overload.
            logLines.Clear();
            GameAction defaulted = LedgerOrchestrator.BuildStrategyConversionAction(
                5.0, new StrategyConversionLeg { Currency = StrategyConversionCurrency.Funds, Delta = 2.0 },
                "r");
            sb.Append("== default-recording-id\n").Append(DescribeRow(defaulted)).Append('\n');
            foreach (string line in logLines)
                sb.Append("log ").Append(line).Append('\n');

            AssertGolden("BuildStrategyConversionAction_Golden", Golden, sb.ToString());
        }

        private static readonly string Golden = string.Join("\n", new[]
        {
            "== Science delta=d:C029000000000000 reason=<null> rec=<null>",
            "row ActionId=act_<guid> ConversionSource=Converter Cost=f:41480000 Type=StrategyScienceDebit UT=d:40934A3D70A3D70A",
            "== Science delta=d:C029000000000000 reason=<null> rec=''",
            "row ActionId=act_<guid> ConversionSource=Converter Cost=f:41480000 Type=StrategyScienceDebit UT=d:40934A3D70A3D70A",
            "== Science delta=d:C029000000000000 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> ConversionSource=Converter Cost=f:41480000 RecordingId='rec-1' Type=StrategyScienceDebit UT=d:40934A3D70A3D70A",
            "== Science delta=d:C029000000000000 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> ConversionSource=Converter Cost=f:41480000 Type=StrategyScienceDebit UT=d:40934A3D70A3D70A",
            "== Science delta=d:C029000000000000 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> ConversionSource=Converter Cost=f:41480000 Type=StrategyScienceDebit UT=d:40934A3D70A3D70A",
            "== Science delta=d:C029000000000000 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> ConversionSource=Converter Cost=f:41480000 RecordingId='rec-1' Type=StrategyScienceDebit UT=d:40934A3D70A3D70A",
            "== Science delta=d:BE112E0BE826D695 reason=<null> rec=<null>",
            "row ActionId=act_<guid> ConversionSource=Converter Cost=f:3089705F Type=StrategyScienceDebit UT=d:40934A3D70A3D70A",
            "== Science delta=d:BE112E0BE826D695 reason=<null> rec=''",
            "row ActionId=act_<guid> ConversionSource=Converter Cost=f:3089705F Type=StrategyScienceDebit UT=d:40934A3D70A3D70A",
            "== Science delta=d:BE112E0BE826D695 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> ConversionSource=Converter Cost=f:3089705F RecordingId='rec-1' Type=StrategyScienceDebit UT=d:40934A3D70A3D70A",
            "== Science delta=d:BE112E0BE826D695 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> ConversionSource=Converter Cost=f:3089705F Type=StrategyScienceDebit UT=d:40934A3D70A3D70A",
            "== Science delta=d:BE112E0BE826D695 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> ConversionSource=Converter Cost=f:3089705F Type=StrategyScienceDebit UT=d:40934A3D70A3D70A",
            "== Science delta=d:BE112E0BE826D695 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> ConversionSource=Converter Cost=f:3089705F RecordingId='rec-1' Type=StrategyScienceDebit UT=d:40934A3D70A3D70A",
            "== Science delta=d:0000000000000000 reason=<null> rec=<null>",
            "row ActionId=act_<guid> Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:0000000000000000 reason=<null> rec=''",
            "row ActionId=act_<guid> Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:0000000000000000 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> RecordingId='rec-1' Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:0000000000000000 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:0000000000000000 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:0000000000000000 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> RecordingId='rec-1' Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:8000000000000000 reason=<null> rec=<null>",
            "row ActionId=act_<guid> ScienceAwarded=f:80000000 Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:8000000000000000 reason=<null> rec=''",
            "row ActionId=act_<guid> ScienceAwarded=f:80000000 Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:8000000000000000 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> RecordingId='rec-1' ScienceAwarded=f:80000000 Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:8000000000000000 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> ScienceAwarded=f:80000000 Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:8000000000000000 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> ScienceAwarded=f:80000000 Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:8000000000000000 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> RecordingId='rec-1' ScienceAwarded=f:80000000 Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:3E112E0BE826D695 reason=<null> rec=<null>",
            "row ActionId=act_<guid> ScienceAwarded=f:3089705F Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:3E112E0BE826D695 reason=<null> rec=''",
            "row ActionId=act_<guid> ScienceAwarded=f:3089705F Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:3E112E0BE826D695 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> RecordingId='rec-1' ScienceAwarded=f:3089705F Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:3E112E0BE826D695 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> ScienceAwarded=f:3089705F Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:3E112E0BE826D695 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> ScienceAwarded=f:3089705F Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:3E112E0BE826D695 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> RecordingId='rec-1' ScienceAwarded=f:3089705F Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:400A000000000000 reason=<null> rec=<null>",
            "row ActionId=act_<guid> ScienceAwarded=f:40500000 Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:400A000000000000 reason=<null> rec=''",
            "row ActionId=act_<guid> ScienceAwarded=f:40500000 Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:400A000000000000 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> RecordingId='rec-1' ScienceAwarded=f:40500000 Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:400A000000000000 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> ScienceAwarded=f:40500000 Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:400A000000000000 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> ScienceAwarded=f:40500000 Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:400A000000000000 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> RecordingId='rec-1' ScienceAwarded=f:40500000 Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:3FE0000000000000 reason=<null> rec=<null>",
            "row ActionId=act_<guid> ScienceAwarded=f:3F000000 Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:3FE0000000000000 reason=<null> rec=''",
            "row ActionId=act_<guid> ScienceAwarded=f:3F000000 Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:3FE0000000000000 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> RecordingId='rec-1' ScienceAwarded=f:3F000000 Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:3FE0000000000000 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> ScienceAwarded=f:3F000000 Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:3FE0000000000000 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> ScienceAwarded=f:3F000000 Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Science delta=d:3FE0000000000000 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> RecordingId='rec-1' ScienceAwarded=f:3F000000 Type=StrategyScienceCredit UT=d:40934A3D70A3D70A",
            "== Funds delta=d:C029000000000000 reason=<null> rec=<null>",
            "row ActionId=act_<guid> FundsSpendingSource=StrategyConverter FundsSpent=f:41480000 Type=FundsSpending UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: funds DEBIT captured nominal=-12.5 at UT=1234.6 (reason=(none)) - the ordinary channel carries this transaction's CONFIGURED GROSS amount, so this row is the diverted half of the pool movement",
            "== Funds delta=d:C029000000000000 reason=<null> rec=''",
            "row ActionId=act_<guid> FundsSpendingSource=StrategyConverter FundsSpent=f:41480000 Type=FundsSpending UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: funds DEBIT captured nominal=-12.5 at UT=1234.6 (reason=(none)) - the ordinary channel carries this transaction's CONFIGURED GROSS amount, so this row is the diverted half of the pool movement",
            "== Funds delta=d:C029000000000000 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> FundsSpendingSource=StrategyConverter FundsSpent=f:41480000 RecordingId='rec-1' Type=FundsSpending UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: funds DEBIT captured nominal=-12.5 at UT=1234.6 (reason=(none)) - the ordinary channel carries this transaction's CONFIGURED GROSS amount, so this row is the diverted half of the pool movement",
            "== Funds delta=d:C029000000000000 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> FundsSpendingSource=StrategyConverter FundsSpent=f:41480000 Type=FundsSpending UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: funds DEBIT captured nominal=-12.5 at UT=1234.6 (reason=ContractReward) - the ordinary channel carries this transaction's CONFIGURED GROSS amount, so this row is the diverted half of the pool movement",
            "== Funds delta=d:C029000000000000 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> FundsSpendingSource=StrategyConverter FundsSpent=f:41480000 Type=FundsSpending UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: funds DEBIT captured nominal=-12.5 at UT=1234.6 (reason=ContractReward) - the ordinary channel carries this transaction's CONFIGURED GROSS amount, so this row is the diverted half of the pool movement",
            "== Funds delta=d:C029000000000000 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> FundsSpendingSource=StrategyConverter FundsSpent=f:41480000 RecordingId='rec-1' Type=FundsSpending UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: funds DEBIT captured nominal=-12.5 at UT=1234.6 (reason=ContractReward) - the ordinary channel carries this transaction's CONFIGURED GROSS amount, so this row is the diverted half of the pool movement",
            "== Funds delta=d:BE112E0BE826D695 reason=<null> rec=<null>",
            "row ActionId=act_<guid> FundsSpendingSource=StrategyConverter FundsSpent=f:3089705F Type=FundsSpending UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: funds DEBIT captured nominal=-1E-09 at UT=1234.6 (reason=(none)) - the ordinary channel carries this transaction's CONFIGURED GROSS amount, so this row is the diverted half of the pool movement",
            "== Funds delta=d:BE112E0BE826D695 reason=<null> rec=''",
            "row ActionId=act_<guid> FundsSpendingSource=StrategyConverter FundsSpent=f:3089705F Type=FundsSpending UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: funds DEBIT captured nominal=-1E-09 at UT=1234.6 (reason=(none)) - the ordinary channel carries this transaction's CONFIGURED GROSS amount, so this row is the diverted half of the pool movement",
            "== Funds delta=d:BE112E0BE826D695 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> FundsSpendingSource=StrategyConverter FundsSpent=f:3089705F RecordingId='rec-1' Type=FundsSpending UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: funds DEBIT captured nominal=-1E-09 at UT=1234.6 (reason=(none)) - the ordinary channel carries this transaction's CONFIGURED GROSS amount, so this row is the diverted half of the pool movement",
            "== Funds delta=d:BE112E0BE826D695 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> FundsSpendingSource=StrategyConverter FundsSpent=f:3089705F Type=FundsSpending UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: funds DEBIT captured nominal=-1E-09 at UT=1234.6 (reason=ContractReward) - the ordinary channel carries this transaction's CONFIGURED GROSS amount, so this row is the diverted half of the pool movement",
            "== Funds delta=d:BE112E0BE826D695 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> FundsSpendingSource=StrategyConverter FundsSpent=f:3089705F Type=FundsSpending UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: funds DEBIT captured nominal=-1E-09 at UT=1234.6 (reason=ContractReward) - the ordinary channel carries this transaction's CONFIGURED GROSS amount, so this row is the diverted half of the pool movement",
            "== Funds delta=d:BE112E0BE826D695 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> FundsSpendingSource=StrategyConverter FundsSpent=f:3089705F RecordingId='rec-1' Type=FundsSpending UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: funds DEBIT captured nominal=-1E-09 at UT=1234.6 (reason=ContractReward) - the ordinary channel carries this transaction's CONFIGURED GROSS amount, so this row is the diverted half of the pool movement",
            "== Funds delta=d:0000000000000000 reason=<null> rec=<null>",
            "row <null>",
            "log [Parsek][WARN][LedgerOrchestrator] Strategy conversion: zero funds delta at UT=1234.6 (reason=(none)) - not captured",
            "== Funds delta=d:0000000000000000 reason=<null> rec=''",
            "row <null>",
            "log [Parsek][WARN][LedgerOrchestrator] Strategy conversion: zero funds delta at UT=1234.6 (reason=(none)) - not captured",
            "== Funds delta=d:0000000000000000 reason=<null> rec='rec-1'",
            "row <null>",
            "log [Parsek][WARN][LedgerOrchestrator] Strategy conversion: zero funds delta at UT=1234.6 (reason=(none)) - not captured",
            "== Funds delta=d:0000000000000000 reason='ContractReward' rec=<null>",
            "row <null>",
            "log [Parsek][WARN][LedgerOrchestrator] Strategy conversion: zero funds delta at UT=1234.6 (reason=ContractReward) - not captured",
            "== Funds delta=d:0000000000000000 reason='ContractReward' rec=''",
            "row <null>",
            "log [Parsek][WARN][LedgerOrchestrator] Strategy conversion: zero funds delta at UT=1234.6 (reason=ContractReward) - not captured",
            "== Funds delta=d:0000000000000000 reason='ContractReward' rec='rec-1'",
            "row <null>",
            "log [Parsek][WARN][LedgerOrchestrator] Strategy conversion: zero funds delta at UT=1234.6 (reason=ContractReward) - not captured",
            "== Funds delta=d:8000000000000000 reason=<null> rec=<null>",
            "row <null>",
            "log [Parsek][WARN][LedgerOrchestrator] Strategy conversion: zero funds delta at UT=1234.6 (reason=(none)) - not captured",
            "== Funds delta=d:8000000000000000 reason=<null> rec=''",
            "row <null>",
            "log [Parsek][WARN][LedgerOrchestrator] Strategy conversion: zero funds delta at UT=1234.6 (reason=(none)) - not captured",
            "== Funds delta=d:8000000000000000 reason=<null> rec='rec-1'",
            "row <null>",
            "log [Parsek][WARN][LedgerOrchestrator] Strategy conversion: zero funds delta at UT=1234.6 (reason=(none)) - not captured",
            "== Funds delta=d:8000000000000000 reason='ContractReward' rec=<null>",
            "row <null>",
            "log [Parsek][WARN][LedgerOrchestrator] Strategy conversion: zero funds delta at UT=1234.6 (reason=ContractReward) - not captured",
            "== Funds delta=d:8000000000000000 reason='ContractReward' rec=''",
            "row <null>",
            "log [Parsek][WARN][LedgerOrchestrator] Strategy conversion: zero funds delta at UT=1234.6 (reason=ContractReward) - not captured",
            "== Funds delta=d:8000000000000000 reason='ContractReward' rec='rec-1'",
            "row <null>",
            "log [Parsek][WARN][LedgerOrchestrator] Strategy conversion: zero funds delta at UT=1234.6 (reason=ContractReward) - not captured",
            "== Funds delta=d:3E112E0BE826D695 reason=<null> rec=<null>",
            "row ActionId=act_<guid> FundsAwarded=f:3089705F FundsSource=Strategy Type=FundsEarning UT=d:40934A3D70A3D70A",
            "== Funds delta=d:3E112E0BE826D695 reason=<null> rec=''",
            "row ActionId=act_<guid> FundsAwarded=f:3089705F FundsSource=Strategy Type=FundsEarning UT=d:40934A3D70A3D70A",
            "== Funds delta=d:3E112E0BE826D695 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> FundsAwarded=f:3089705F FundsSource=Strategy RecordingId='rec-1' Type=FundsEarning UT=d:40934A3D70A3D70A",
            "== Funds delta=d:3E112E0BE826D695 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> FundsAwarded=f:3089705F FundsSource=Strategy Type=FundsEarning UT=d:40934A3D70A3D70A",
            "== Funds delta=d:3E112E0BE826D695 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> FundsAwarded=f:3089705F FundsSource=Strategy Type=FundsEarning UT=d:40934A3D70A3D70A",
            "== Funds delta=d:3E112E0BE826D695 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> FundsAwarded=f:3089705F FundsSource=Strategy RecordingId='rec-1' Type=FundsEarning UT=d:40934A3D70A3D70A",
            "== Funds delta=d:400A000000000000 reason=<null> rec=<null>",
            "row ActionId=act_<guid> FundsAwarded=f:40500000 FundsSource=Strategy Type=FundsEarning UT=d:40934A3D70A3D70A",
            "== Funds delta=d:400A000000000000 reason=<null> rec=''",
            "row ActionId=act_<guid> FundsAwarded=f:40500000 FundsSource=Strategy Type=FundsEarning UT=d:40934A3D70A3D70A",
            "== Funds delta=d:400A000000000000 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> FundsAwarded=f:40500000 FundsSource=Strategy RecordingId='rec-1' Type=FundsEarning UT=d:40934A3D70A3D70A",
            "== Funds delta=d:400A000000000000 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> FundsAwarded=f:40500000 FundsSource=Strategy Type=FundsEarning UT=d:40934A3D70A3D70A",
            "== Funds delta=d:400A000000000000 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> FundsAwarded=f:40500000 FundsSource=Strategy Type=FundsEarning UT=d:40934A3D70A3D70A",
            "== Funds delta=d:400A000000000000 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> FundsAwarded=f:40500000 FundsSource=Strategy RecordingId='rec-1' Type=FundsEarning UT=d:40934A3D70A3D70A",
            "== Funds delta=d:3FE0000000000000 reason=<null> rec=<null>",
            "row ActionId=act_<guid> FundsAwarded=f:3F000000 FundsSource=Strategy Type=FundsEarning UT=d:40934A3D70A3D70A",
            "== Funds delta=d:3FE0000000000000 reason=<null> rec=''",
            "row ActionId=act_<guid> FundsAwarded=f:3F000000 FundsSource=Strategy Type=FundsEarning UT=d:40934A3D70A3D70A",
            "== Funds delta=d:3FE0000000000000 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> FundsAwarded=f:3F000000 FundsSource=Strategy RecordingId='rec-1' Type=FundsEarning UT=d:40934A3D70A3D70A",
            "== Funds delta=d:3FE0000000000000 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> FundsAwarded=f:3F000000 FundsSource=Strategy Type=FundsEarning UT=d:40934A3D70A3D70A",
            "== Funds delta=d:3FE0000000000000 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> FundsAwarded=f:3F000000 FundsSource=Strategy Type=FundsEarning UT=d:40934A3D70A3D70A",
            "== Funds delta=d:3FE0000000000000 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> FundsAwarded=f:3F000000 FundsSource=Strategy RecordingId='rec-1' Type=FundsEarning UT=d:40934A3D70A3D70A",
            "== Reputation delta=d:C029000000000000 reason=<null> rec=<null>",
            "row ActionId=act_<guid> NominalPenalty=f:41480000 RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-12.5 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:C029000000000000 reason=<null> rec=''",
            "row ActionId=act_<guid> NominalPenalty=f:41480000 RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-12.5 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:C029000000000000 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> NominalPenalty=f:41480000 RecordingId='rec-1' RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-12.5 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:C029000000000000 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> NominalPenalty=f:41480000 RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-12.5 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:C029000000000000 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> NominalPenalty=f:41480000 RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-12.5 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:C029000000000000 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> NominalPenalty=f:41480000 RecordingId='rec-1' RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-12.5 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:BE112E0BE826D695 reason=<null> rec=<null>",
            "row ActionId=act_<guid> NominalPenalty=f:3089705F RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-1E-09 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:BE112E0BE826D695 reason=<null> rec=''",
            "row ActionId=act_<guid> NominalPenalty=f:3089705F RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-1E-09 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:BE112E0BE826D695 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> NominalPenalty=f:3089705F RecordingId='rec-1' RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-1E-09 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:BE112E0BE826D695 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> NominalPenalty=f:3089705F RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-1E-09 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:BE112E0BE826D695 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> NominalPenalty=f:3089705F RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-1E-09 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:BE112E0BE826D695 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> NominalPenalty=f:3089705F RecordingId='rec-1' RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-1E-09 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:0000000000000000 reason=<null> rec=<null>",
            "row ActionId=act_<guid> NominalPenalty=f:80000000 RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=0 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:0000000000000000 reason=<null> rec=''",
            "row ActionId=act_<guid> NominalPenalty=f:80000000 RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=0 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:0000000000000000 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> NominalPenalty=f:80000000 RecordingId='rec-1' RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=0 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:0000000000000000 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> NominalPenalty=f:80000000 RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=0 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:0000000000000000 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> NominalPenalty=f:80000000 RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=0 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:0000000000000000 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> NominalPenalty=f:80000000 RecordingId='rec-1' RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=0 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:3E112E0BE826D695 reason=<null> rec=<null>",
            "row ActionId=act_<guid> NominalRep=f:3089705F RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=1E-09 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:3E112E0BE826D695 reason=<null> rec=''",
            "row ActionId=act_<guid> NominalRep=f:3089705F RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=1E-09 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:3E112E0BE826D695 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> NominalRep=f:3089705F RecordingId='rec-1' RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=1E-09 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:3E112E0BE826D695 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> NominalRep=f:3089705F RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=1E-09 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:3E112E0BE826D695 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> NominalRep=f:3089705F RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=1E-09 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:3E112E0BE826D695 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> NominalRep=f:3089705F RecordingId='rec-1' RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=1E-09 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:400A000000000000 reason=<null> rec=<null>",
            "row ActionId=act_<guid> NominalRep=f:40500000 RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=3.25 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:400A000000000000 reason=<null> rec=''",
            "row ActionId=act_<guid> NominalRep=f:40500000 RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=3.25 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:400A000000000000 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> NominalRep=f:40500000 RecordingId='rec-1' RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=3.25 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:400A000000000000 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> NominalRep=f:40500000 RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=3.25 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:400A000000000000 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> NominalRep=f:40500000 RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=3.25 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:400A000000000000 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> NominalRep=f:40500000 RecordingId='rec-1' RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=3.25 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:3FE0000000000000 reason=<null> rec=<null>",
            "row ActionId=act_<guid> NominalRep=f:3F000000 RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=0.5 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:3FE0000000000000 reason=<null> rec=''",
            "row ActionId=act_<guid> NominalRep=f:3F000000 RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=0.5 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:3FE0000000000000 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> NominalRep=f:3F000000 RecordingId='rec-1' RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=0.5 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:3FE0000000000000 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> NominalRep=f:3F000000 RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=0.5 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:3FE0000000000000 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> NominalRep=f:3F000000 RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=0.5 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== Reputation delta=d:3FE0000000000000 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> NominalRep=f:3F000000 RecordingId='rec-1' RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=0.5 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:C029000000000000 reason=<null> rec=<null>",
            "row ActionId=act_<guid> NominalPenalty=f:41480000 RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-12.5 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:C029000000000000 reason=<null> rec=''",
            "row ActionId=act_<guid> NominalPenalty=f:41480000 RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-12.5 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:C029000000000000 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> NominalPenalty=f:41480000 RecordingId='rec-1' RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-12.5 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:C029000000000000 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> NominalPenalty=f:41480000 RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-12.5 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:C029000000000000 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> NominalPenalty=f:41480000 RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-12.5 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:C029000000000000 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> NominalPenalty=f:41480000 RecordingId='rec-1' RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-12.5 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:BE112E0BE826D695 reason=<null> rec=<null>",
            "row ActionId=act_<guid> NominalPenalty=f:3089705F RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-1E-09 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:BE112E0BE826D695 reason=<null> rec=''",
            "row ActionId=act_<guid> NominalPenalty=f:3089705F RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-1E-09 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:BE112E0BE826D695 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> NominalPenalty=f:3089705F RecordingId='rec-1' RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-1E-09 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:BE112E0BE826D695 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> NominalPenalty=f:3089705F RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-1E-09 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:BE112E0BE826D695 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> NominalPenalty=f:3089705F RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-1E-09 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:BE112E0BE826D695 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> NominalPenalty=f:3089705F RecordingId='rec-1' RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=-1E-09 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:0000000000000000 reason=<null> rec=<null>",
            "row ActionId=act_<guid> NominalPenalty=f:80000000 RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=0 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:0000000000000000 reason=<null> rec=''",
            "row ActionId=act_<guid> NominalPenalty=f:80000000 RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=0 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:0000000000000000 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> NominalPenalty=f:80000000 RecordingId='rec-1' RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=0 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:0000000000000000 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> NominalPenalty=f:80000000 RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=0 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:0000000000000000 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> NominalPenalty=f:80000000 RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=0 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:0000000000000000 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> NominalPenalty=f:80000000 RecordingId='rec-1' RepPenaltySource=StrategyConverter Type=ReputationPenalty UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation DEBIT captured pre-curve nominal=0 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:3E112E0BE826D695 reason=<null> rec=<null>",
            "row ActionId=act_<guid> NominalRep=f:3089705F RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=1E-09 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:3E112E0BE826D695 reason=<null> rec=''",
            "row ActionId=act_<guid> NominalRep=f:3089705F RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=1E-09 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:3E112E0BE826D695 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> NominalRep=f:3089705F RecordingId='rec-1' RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=1E-09 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:3E112E0BE826D695 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> NominalRep=f:3089705F RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=1E-09 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:3E112E0BE826D695 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> NominalRep=f:3089705F RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=1E-09 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:3E112E0BE826D695 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> NominalRep=f:3089705F RecordingId='rec-1' RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=1E-09 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:400A000000000000 reason=<null> rec=<null>",
            "row ActionId=act_<guid> NominalRep=f:40500000 RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=3.25 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:400A000000000000 reason=<null> rec=''",
            "row ActionId=act_<guid> NominalRep=f:40500000 RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=3.25 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:400A000000000000 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> NominalRep=f:40500000 RecordingId='rec-1' RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=3.25 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:400A000000000000 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> NominalRep=f:40500000 RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=3.25 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:400A000000000000 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> NominalRep=f:40500000 RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=3.25 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:400A000000000000 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> NominalRep=f:40500000 RecordingId='rec-1' RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=3.25 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:3FE0000000000000 reason=<null> rec=<null>",
            "row ActionId=act_<guid> NominalRep=f:3F000000 RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=0.5 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:3FE0000000000000 reason=<null> rec=''",
            "row ActionId=act_<guid> NominalRep=f:3F000000 RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=0.5 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:3FE0000000000000 reason=<null> rec='rec-1'",
            "row ActionId=act_<guid> NominalRep=f:3F000000 RecordingId='rec-1' RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=0.5 at UT=1234.6 (reason=(none)) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:3FE0000000000000 reason='ContractReward' rec=<null>",
            "row ActionId=act_<guid> NominalRep=f:3F000000 RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=0.5 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:3FE0000000000000 reason='ContractReward' rec=''",
            "row ActionId=act_<guid> NominalRep=f:3F000000 RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=0.5 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== 7 delta=d:3FE0000000000000 reason='ContractReward' rec='rec-1'",
            "row ActionId=act_<guid> NominalRep=f:3F000000 RecordingId='rec-1' RepSource=Strategy Type=ReputationEarning UT=d:40934A3D70A3D70A",
            "log [Parsek][INFO][LedgerOrchestrator] Strategy conversion: reputation CREDIT captured pre-curve nominal=0.5 at UT=1234.6 (reason=ContractReward) - ReputationModule applies KSP's granular curve to it at the reconstruction's own running rep",
            "== default-recording-id",
            "row ActionId=act_<guid> FundsAwarded=f:40000000 FundsSource=Strategy Type=FundsEarning UT=d:4014000000000000",
        }) + "\n";
    }
}
