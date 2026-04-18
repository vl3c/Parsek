using System;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class LegacyPartPurchaseLoadCompatibilityTests : IDisposable
    {
        public LegacyPartPurchaseLoadCompatibilityTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GameStateRecorder.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            MilestoneStore.CurrentEpoch = 0;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.CurrentEpoch = 0;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void TryRecoverBrokenLedgerOnLoad_LegacyNoBypassPartPurchase_UsesCanonicalCharge()
        {
            GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = () => false;
            GameStateRecorder.PartEntryCostProviderForTesting =
                partName => partName == "mk1pod" ? 800f : (float?)null;

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 200.0,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod",
                detail = "cost=450",
                valueBefore = 10450.0,
                valueAfter = 10000.0
            });

            int recovered = LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad();

            Assert.Equal(1, recovered);

            var repairedEvent = Assert.Single(GameStateStore.Events);
            Assert.Equal("cost=800", repairedEvent.detail);
            Assert.Equal(10800.0, repairedEvent.valueBefore);
            Assert.Equal(10000.0, repairedEvent.valueAfter);

            var action = Assert.Single(Ledger.Actions);
            Assert.Equal(GameActionType.FundsSpending, action.Type);
            Assert.Equal(FundsSpendingSource.Other, action.FundsSpendingSource);
            Assert.Equal("mk1pod", action.DedupKey);
            Assert.Equal(800f, action.FundsSpent);
        }

        [Fact]
        public void RepairLegacyPartPurchaseActionsOnLoad_WithMatchingEvent_RewritesPersistedFundsSpent()
        {
            GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = () => false;
            GameStateRecorder.PartEntryCostProviderForTesting =
                partName => partName == "mk1pod" ? 800f : (float?)null;

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 200.0,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod",
                detail = "cost=450",
                valueBefore = 10450.0,
                valueAfter = 10000.0
            });

            Ledger.AddAction(new GameAction
            {
                UT = 200.0,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 450f,
                DedupKey = "mk1pod"
            });

            int repaired = LedgerOrchestrator.RepairLegacyPartPurchaseActionsOnLoad(
                GameStateStore.Events, Ledger.Actions);

            Assert.Equal(1, repaired);
            Assert.Equal(800f, Ledger.Actions.Single().FundsSpent);
        }

        [Fact]
        public void RepairLegacyPartPurchaseActionsOnLoad_WithoutMatchingEvent_UsesPartLookupFallback()
        {
            GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = () => false;
            GameStateRecorder.PartEntryCostProviderForTesting =
                partName => partName == "mk1pod" ? 800f : (float?)null;

            Ledger.AddAction(new GameAction
            {
                UT = 200.0,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 450f,
                DedupKey = "mk1pod"
            });

            int repaired = LedgerOrchestrator.RepairLegacyPartPurchaseActionsOnLoad(
                GameStateStore.Events, Ledger.Actions);

            Assert.Equal(1, repaired);
            Assert.Equal(800f, Ledger.Actions.Single().FundsSpent);
        }
    }
}
