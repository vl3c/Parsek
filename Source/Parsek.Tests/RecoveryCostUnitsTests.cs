using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct cases for the two recovery-funds units split out of
    /// <c>LedgerOrchestrator.AddVesselRecoveryCostActions</c>:
    /// <see cref="LedgerOrchestrator.AddPairedRecoveryFundsAction"/> and
    /// <see cref="LedgerOrchestrator.AddHeuristicRecoveryFundsAction"/>.
    /// </summary>
    [Collection("Sequential")]
    public class RecoveryCostUnitsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo savedCulture;

        public RecoveryCostUnitsTests()
        {
            savedCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            Thread.CurrentThread.CurrentCulture = savedCulture;
        }

        private static GameStateEvent FundsEvent(double ut, double before, double after)
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.FundsChanged,
                key = LedgerOrchestrator.VesselRecoveryReasonKey,
                valueBefore = before,
                valueAfter = after,
            };
        }

        private static Recording Rec(params double[] funds)
        {
            var rec = new Recording { RecordingId = "rec-u" };
            for (int i = 0; i < funds.Length; i++)
                rec.Points.Add(new TrajectoryPoint { ut = 100 + i, funds = funds[i] });
            return rec;
        }

        [Fact]
        public void Paired_PositiveDelta_AddsKeyedRecoveryRowAtEventUT()
        {
            var result = new List<GameAction>();
            LedgerOrchestrator.AddPairedRecoveryFundsAction(
                result, "rec-u", FundsEvent(205.0, 1000.0, 1750.0), "dedup-key-1");

            var row = Assert.Single(result);
            Assert.Equal(205.0, row.UT);
            Assert.Equal(GameActionType.FundsEarning, row.Type);
            Assert.Equal("rec-u", row.RecordingId);
            Assert.Equal(750f, row.FundsAwarded);
            Assert.Equal(FundsEarningSource.Recovery, row.FundsSource);
            Assert.Equal("dedup-key-1", row.DedupKey);
            var line = Assert.Single(logLines);
            Assert.Contains(
                "CreateVesselCostActions: paired recovery funds=750 (before=1000, after=1750) for 'rec-u'",
                line);
        }

        [Theory]
        [InlineData(1000.0, 1000.0, "0.0")]
        [InlineData(1000.0, 900.0, "-100.0")]
        public void Paired_NonPositiveDelta_AddsNothingAndLogsTheSkip(
            double before, double after, string deltaText)
        {
            var result = new List<GameAction>();
            LedgerOrchestrator.AddPairedRecoveryFundsAction(
                result, "rec-u", FundsEvent(205.2, before, after), "dedup-key-1");

            Assert.Empty(result);
            var line = Assert.Single(logLines);
            Assert.Contains(
                "CreateVesselCostActions: paired recovery event for 'rec-u' at ut=205.2 " +
                "has non-positive delta " + deltaText + " — skipping",
                line);
        }

        [Fact]
        public void Heuristic_PositiveLastStep_AddsUnkeyedRecoveryRowAtEndUT()
        {
            var result = new List<GameAction>();
            // An earlier, larger step must not count: only the last two points are read.
            LedgerOrchestrator.AddHeuristicRecoveryFundsAction(
                result, "rec-u", 300.0, Rec(0.0, 5000.0, 5200.0, 5600.0));

            var row = Assert.Single(result);
            Assert.Equal(300.0, row.UT);
            Assert.Equal(GameActionType.FundsEarning, row.Type);
            Assert.Equal("rec-u", row.RecordingId);
            Assert.Equal(400f, row.FundsAwarded);
            Assert.Equal(FundsEarningSource.Recovery, row.FundsSource);
            Assert.Null(row.DedupKey);
            var line = Assert.Single(logLines);
            Assert.Contains(
                "CreateVesselCostActions: recovery funds=400 (last=5600, penultimate=5200) for 'rec-u'",
                line);
        }

        [Theory]
        [InlineData(5200.0, 5200.0)]
        [InlineData(5200.0, 5100.0)]
        public void Heuristic_NonPositiveLastStep_AddsNothingAndLogsNothing(
            double penultimate, double last)
        {
            var result = new List<GameAction>();
            LedgerOrchestrator.AddHeuristicRecoveryFundsAction(
                result, "rec-u", 300.0, Rec(0.0, 9000.0, penultimate, last));

            Assert.Empty(result);
            Assert.Empty(logLines);
        }

        [Fact]
        public void BothUnits_AppendToTheGivenList()
        {
            var result = new List<GameAction> { new GameAction { Type = GameActionType.FundsSpending } };
            LedgerOrchestrator.AddPairedRecoveryFundsAction(
                result, "rec-u", FundsEvent(205.0, 0.0, 10.0), "k");
            LedgerOrchestrator.AddHeuristicRecoveryFundsAction(
                result, "rec-u", 300.0, Rec(1.0, 3.0));

            Assert.Equal(3, result.Count);
            Assert.Equal(GameActionType.FundsSpending, result[0].Type);
            Assert.Equal(10f, result[1].FundsAwarded);
            Assert.Equal(2f, result[2].FundsAwarded);
        }
    }
}
