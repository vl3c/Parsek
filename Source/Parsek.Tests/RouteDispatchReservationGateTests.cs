using System;
using System.Collections.Generic;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// ROUTE-DISPATCH-IGNORES-RESERVATIONS: a Career KSC route dispatch may spend only the
    /// smaller of the live funds counter and what the ledger leaves free once committed
    /// future costs are reserved (logistics design section 8 step 4).
    /// </summary>
    [Collection("Sequential")]
    public class RouteDispatchReservationGateTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteDispatchReservationGateTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void Decide_ReservationBelowLive_RefusesOnTheReservedBound()
        {
            bool ok = LiveRouteRuntimeEnvironment.DecideKscFundsAvailable(
                cost: 30000, liveFunds: 120000, reservationAwareFunds: 20000,
                out double shortfall, out string bound);

            Assert.False(ok);
            Assert.Equal(10000, shortfall, 3);
            Assert.Equal("reserved", bound);
        }

        [Fact]
        public void Decide_ReservationCoversCost_Allows()
        {
            bool ok = LiveRouteRuntimeEnvironment.DecideKscFundsAvailable(
                cost: 30000, liveFunds: 120000, reservationAwareFunds: 30000,
                out double shortfall, out string bound);

            Assert.True(ok);
            Assert.Equal(0, shortfall);
            Assert.Equal("none", bound);
        }

        [Fact]
        public void Decide_LiveBelowReservation_RefusesOnTheLiveBound()
        {
            bool ok = LiveRouteRuntimeEnvironment.DecideKscFundsAvailable(
                cost: 30000, liveFunds: 25000, reservationAwareFunds: 90000,
                out double shortfall, out string bound);

            Assert.False(ok);
            Assert.Equal(5000, shortfall, 3);
            Assert.Equal("live", bound);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void Decide_UnknownReservation_GatesOnLiveOnly(double reservation)
        {
            Assert.True(LiveRouteRuntimeEnvironment.DecideKscFundsAvailable(
                30000, 30000, reservation, out _, out _));
            Assert.False(LiveRouteRuntimeEnvironment.DecideKscFundsAvailable(
                30000, 29999, reservation, out double shortfall, out string bound));
            Assert.Equal(1, shortfall, 3);
            Assert.Equal("live", bound);
        }

        [Fact]
        public void ResolveReservationAwareFunds_CommittedFuturePurchase_IsReserved()
        {
            // 120k now; a committed 100k purchase lies ahead at UT 500. Only 20k is free, so a
            // 30k dispatch at UT 200 must wait even though the counter shows 120k.
            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = 120000f
            });
            Ledger.AddAction(new GameAction
            {
                UT = 500.0,
                Type = GameActionType.FundsSpending,
                FundsSpent = 100000f,
                RecordingId = "rec-future-purchase"
            });
            LedgerOrchestrator.NowUtProviderForTesting = () => 200.0;

            double reservationAware = LiveRouteRuntimeEnvironment.ResolveReservationAwareFunds(30000);
            bool ok = LiveRouteRuntimeEnvironment.DecideKscFundsAvailable(
                30000, 120000, reservationAware, out double shortfall, out string bound);

            Assert.Equal(20000, reservationAware, 1);
            Assert.False(ok);
            Assert.Equal("reserved", bound);
            Assert.Equal(10000, shortfall, 1);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("CanAffordFundsSpending")
                && l.Contains("affordable=False"));
        }

        [Fact]
        public void ResolveReservationAwareFunds_NoCommittedFuture_CoversTheCounter()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = 120000f
            });
            LedgerOrchestrator.NowUtProviderForTesting = () => 200.0;

            double reservationAware = LiveRouteRuntimeEnvironment.ResolveReservationAwareFunds(30000);

            Assert.Equal(120000, reservationAware, 1);
            Assert.True(LiveRouteRuntimeEnvironment.DecideKscFundsAvailable(
                30000, 120000, reservationAware, out _, out _));
        }
    }
}
