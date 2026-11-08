using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the three per-currency arms <c>LedgerOrchestrator.BuildStrategyConversionAction</c>
    /// was split into (wave 2 W7b). The dispatch and every arm end to end are pinned by
    /// <see cref="BuildStrategyConversionActionCharacterizationTests"/>.
    /// </summary>
    [Collection("Sequential")]
    public class StrategyConversionArmBuilderTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public StrategyConversionArmBuilderTests()
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

        private static StrategyConversionLeg Leg(StrategyConversionCurrency currency, double delta)
        {
            return new StrategyConversionLeg { Currency = currency, Delta = delta };
        }

        [Fact]
        public void Science_Debit_IsAConverterDebitOfTheMagnitude()
        {
            GameAction row = LedgerOrchestrator.BuildStrategyScienceConversionAction(
                10.0, Leg(StrategyConversionCurrency.Science, -4.5), "rec-1");
            Assert.Equal(GameActionType.StrategyScienceDebit, row.Type);
            Assert.Equal(10.0, row.UT);
            Assert.Equal("rec-1", row.RecordingId);
            Assert.Equal(4.5f, row.Cost);
            Assert.Equal(StrategyConversionSource.Converter, row.ConversionSource);
            Assert.Equal(0f, row.ScienceAwarded);
            Assert.Empty(logLines);
        }

        [Fact]
        public void Science_ZeroOrCredit_IsACredit()
        {
            GameAction credit = LedgerOrchestrator.BuildStrategyScienceConversionAction(
                10.0, Leg(StrategyConversionCurrency.Science, 2.25), null);
            Assert.Equal(GameActionType.StrategyScienceCredit, credit.Type);
            Assert.Null(credit.RecordingId);
            Assert.Equal(2.25f, credit.ScienceAwarded);

            GameAction zero = LedgerOrchestrator.BuildStrategyScienceConversionAction(
                10.0, Leg(StrategyConversionCurrency.Science, 0.0), null);
            Assert.Equal(GameActionType.StrategyScienceCredit, zero.Type);
            Assert.Equal(0f, zero.ScienceAwarded);
            Assert.Empty(logLines);
        }

        [Fact]
        public void Funds_Debit_IsAStrategyConverterSpending_AndLogsInfo()
        {
            GameAction row = LedgerOrchestrator.BuildStrategyFundsConversionAction(
                20.0, Leg(StrategyConversionCurrency.Funds, -12.5), "ContractReward", "rec-2");
            Assert.Equal(GameActionType.FundsSpending, row.Type);
            Assert.Equal("rec-2", row.RecordingId);
            Assert.Equal(12.5f, row.FundsSpent);
            Assert.Equal(FundsSpendingSource.StrategyConverter, row.FundsSpendingSource);
            Assert.Equal(
                "[Parsek][INFO][LedgerOrchestrator] Strategy conversion: funds DEBIT captured "
                + "nominal=-12.5 at UT=20.0 (reason=ContractReward) - the ordinary channel carries "
                + "this transaction's CONFIGURED GROSS amount, so this row is the diverted half of "
                + "the pool movement",
                Assert.Single(logLines));
        }

        [Fact]
        public void Funds_Zero_IsNull_AndWarns()
        {
            GameAction row = LedgerOrchestrator.BuildStrategyFundsConversionAction(
                20.0, Leg(StrategyConversionCurrency.Funds, 0.0), null, "rec-2");
            Assert.Null(row);
            Assert.Equal(
                "[Parsek][WARN][LedgerOrchestrator] Strategy conversion: zero funds delta at "
                + "UT=20.0 (reason=(none)) - not captured",
                Assert.Single(logLines));
        }

        [Fact]
        public void Funds_Credit_IsAStrategyEarning_Silently()
        {
            GameAction row = LedgerOrchestrator.BuildStrategyFundsConversionAction(
                20.0, Leg(StrategyConversionCurrency.Funds, 3.25), "r", null);
            Assert.Equal(GameActionType.FundsEarning, row.Type);
            Assert.Null(row.RecordingId);
            Assert.Equal(3.25f, row.FundsAwarded);
            Assert.Equal(FundsEarningSource.Strategy, row.FundsSource);
            Assert.Empty(logLines);
        }

        [Fact]
        public void Reputation_Credit_IsANominalStrategyEarning()
        {
            GameAction row = LedgerOrchestrator.BuildStrategyReputationConversionAction(
                30.0, Leg(StrategyConversionCurrency.Reputation, 0.5), null, "rec-3");
            Assert.Equal(GameActionType.ReputationEarning, row.Type);
            Assert.Equal("rec-3", row.RecordingId);
            Assert.Equal(0.5f, row.NominalRep);
            Assert.Equal(ReputationSource.Strategy, row.RepSource);
            Assert.Contains("reputation CREDIT captured pre-curve nominal=0.5 at UT=30.0 (reason=(none))",
                Assert.Single(logLines));
        }

        [Fact]
        public void Reputation_ZeroOrDebit_IsAConverterPenaltyOfTheMagnitude()
        {
            GameAction debit = LedgerOrchestrator.BuildStrategyReputationConversionAction(
                30.0, Leg(StrategyConversionCurrency.Reputation, -1.5), "ContractReward", null);
            Assert.Equal(GameActionType.ReputationPenalty, debit.Type);
            Assert.Equal(1.5f, debit.NominalPenalty);
            Assert.Equal(ReputationPenaltySource.StrategyConverter, debit.RepPenaltySource);
            Assert.Contains("reputation DEBIT captured pre-curve nominal=-1.5 at UT=30.0 (reason=ContractReward)",
                Assert.Single(logLines));

            logLines.Clear();
            GameAction zero = LedgerOrchestrator.BuildStrategyReputationConversionAction(
                30.0, Leg(StrategyConversionCurrency.Reputation, 0.0), null, null);
            Assert.Equal(GameActionType.ReputationPenalty, zero.Type);
            Assert.Equal(0f, zero.NominalPenalty);
            Assert.Contains("reputation DEBIT captured pre-curve nominal=0 at", Assert.Single(logLines));
        }

        [Fact]
        public void Dispatch_EmptyRecordingIdBecomesAnUntaggedRow_AndUnknownCurrencyTakesTheReputationArm()
        {
            GameAction science = LedgerOrchestrator.BuildStrategyConversionAction(
                1.0, Leg(StrategyConversionCurrency.Science, 1.0), null, "");
            Assert.Null(science.RecordingId);

            GameAction unknown = LedgerOrchestrator.BuildStrategyConversionAction(
                1.0, Leg((StrategyConversionCurrency)7, 2.0), null, "rec-4");
            Assert.Equal(GameActionType.ReputationEarning, unknown.Type);
            Assert.Equal("rec-4", unknown.RecordingId);
        }
    }
}
