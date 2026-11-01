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
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void TryRecoverBrokenLedgerOnLoad_UnambiguousSavedFundsCharge_RewritesLegacyEventAndAction()
        {
            var partEvt = new GameStateEvent
            {
                ut = 200.0,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod",
                detail = "cost=450",
                valueBefore = 10450.0,
                valueAfter = 10000.0
            };
            GameStateStore.AddEvent(ref partEvt);
            var fundsEvt = new GameStateEvent
            {
                ut = 200.0,
                eventType = GameStateEventType.FundsChanged,
                key = "RnDPartPurchase",
                valueBefore = 10800.0,
                valueAfter = 10000.0
            };
            GameStateStore.AddEvent(ref fundsEvt);

            int recovered = LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad();

            Assert.Equal(1, recovered);

            var repairedEvent = Assert.Single(GameStateStore.Events
                .Where(evt => evt.eventType == GameStateEventType.PartPurchased));
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
        public void RepairLegacyPartPurchaseActionsOnLoad_WithUnambiguousSavedFundsCharge_RewritesPersistedFundsSpent()
        {
            var partEvt = new GameStateEvent
            {
                ut = 200.0,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod",
                detail = "cost=450",
                valueBefore = 10450.0,
                valueAfter = 10000.0
            };
            GameStateStore.AddEvent(ref partEvt);
            var fundsEvt = new GameStateEvent
            {
                ut = 200.0,
                eventType = GameStateEventType.FundsChanged,
                key = "RnDPartPurchase",
                valueBefore = 10800.0,
                valueAfter = 10000.0
            };
            GameStateStore.AddEvent(ref fundsEvt);

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
        public void LoadRepair_CurrentRecorderRowUnderAStrategyDiscount_KeepsTheGrossCost()
        {
            // RW-23 (2026-10-31_1802): an RT-10 bought under Aggressive Negotiations records the
            // gross 1200 row (cost= plus entryCost=) and a +18 conversion leg; stock's
            // FundsChanged(RnDPartPurchase) reads the net 1182. The cold-load repair rewrote the
            // row to 1182 while the leg stayed, so the walk expected +18 twice.
            var partEvt = new GameStateEvent
            {
                ut = 200.0,
                eventType = GameStateEventType.PartPurchased,
                key = "solidBooster.v2",
                detail = "cost=1200;entryCost=1200",
                valueBefore = 11200.0,
                valueAfter = 10000.0
            };
            GameStateStore.AddEvent(ref partEvt);
            var fundsEvt = new GameStateEvent
            {
                ut = 200.0,
                eventType = GameStateEventType.FundsChanged,
                key = "RnDPartPurchase",
                valueBefore = 11182.0,
                valueAfter = 10000.0
            };
            GameStateStore.AddEvent(ref fundsEvt);
            Ledger.AddAction(new GameAction
            {
                UT = 200.0,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                FundsSpent = 1200f,
                DedupKey = "solidBooster.v2"
            });

            int repairedEvents = GameStateStore.RepairLegacyPartPurchaseEventsForCurrentSemantics();
            int repairedActions = LedgerOrchestrator.RepairLegacyPartPurchaseActionsOnLoad(
                GameStateStore.Events, Ledger.Actions);

            Assert.Equal(0, repairedEvents);
            Assert.Equal(0, repairedActions);
            var kept = Assert.Single(GameStateStore.Events
                .Where(evt => evt.eventType == GameStateEventType.PartPurchased));
            Assert.Equal("cost=1200;entryCost=1200", kept.detail);
            Assert.Equal(11200.0, kept.valueBefore);
            Assert.Equal(1200f, Ledger.Actions.Single().FundsSpent);
        }

        [Theory]
        [InlineData("cost=1200;entryCost=1200", true)]
        [InlineData("cost=0;entryCost=450", true)]
        [InlineData("cost=450", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsCurrentRecorderPartPurchaseDetail_Cases(string detail, bool expected)
        {
            Assert.Equal(expected, GameStateStore.IsCurrentRecorderPartPurchaseDetail(detail));
        }

        [Fact]
        public void TryRecoverBrokenLedgerOnLoad_LegacyBypassShapeWithoutFundsEvent_RewritesToZeroCost()
        {
            GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = () => true;

            var partEvt = new GameStateEvent
            {
                ut = 200.0,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod",
                detail = "cost=450",
                valueBefore = 10450.0,
                valueAfter = 10000.0
            };
            GameStateStore.AddEvent(ref partEvt);

            int recovered = LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad();

            Assert.Equal(1, recovered);

            var repairedEvent = Assert.Single(GameStateStore.Events
                .Where(evt => evt.eventType == GameStateEventType.PartPurchased));
            Assert.Equal("cost=0", repairedEvent.detail);
            Assert.Equal(10000.0, repairedEvent.valueBefore);
            Assert.Equal(10000.0, repairedEvent.valueAfter);

            var action = Assert.Single(Ledger.Actions);
            Assert.Equal(GameActionType.FundsSpending, action.Type);
            Assert.Equal(FundsSpendingSource.Other, action.FundsSpendingSource);
            Assert.Equal("mk1pod", action.DedupKey);
            Assert.Equal(0f, action.FundsSpent);
        }

        [Fact]
        public void RepairLegacyPartPurchaseActionsOnLoad_LegacyBypassShapeWithoutFundsEvent_RewritesPersistedFundsSpentToZero()
        {
            GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = () => true;

            var partEvt = new GameStateEvent
            {
                ut = 200.0,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod",
                detail = "cost=450",
                valueBefore = 10450.0,
                valueAfter = 10000.0
            };
            GameStateStore.AddEvent(ref partEvt);

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
            Assert.Equal(0f, Ledger.Actions.Single().FundsSpent);
        }

        [Fact]
        public void RepairLegacyPartPurchaseActionsOnLoad_WithoutMatchingEvent_PreservesStoredCharge()
        {
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

            Assert.Equal(0, repaired);
            Assert.Equal(450f, Ledger.Actions.Single().FundsSpent);
        }

        [Fact]
        public void TryRecoverBrokenLedgerOnLoad_AmbiguousCoalescedFundsWindow_PreservesLegacyStoredCosts()
        {
            GameStateRecorder.BypassEntryPurchaseAfterResearchProviderForTesting = () => true;

            var mk1Evt = new GameStateEvent
            {
                ut = 200.00,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod",
                detail = "cost=450",
                valueBefore = 10450.0,
                valueAfter = 10000.0
            };
            GameStateStore.AddEvent(ref mk1Evt);
            var batteryEvt = new GameStateEvent
            {
                ut = 200.05,
                eventType = GameStateEventType.PartPurchased,
                key = "batteryPack",
                detail = "cost=120",
                valueBefore = 10120.0,
                valueAfter = 10000.0
            };
            GameStateStore.AddEvent(ref batteryEvt);
            var fundsEvt = new GameStateEvent
            {
                ut = 200.02,
                eventType = GameStateEventType.FundsChanged,
                key = "RnDPartPurchase",
                valueBefore = 10920.0,
                valueAfter = 10000.0
            };
            GameStateStore.AddEvent(ref fundsEvt);

            int recovered = LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad();

            Assert.Equal(2, recovered);

            var partEvents = GameStateStore.Events
                .Where(evt => evt.eventType == GameStateEventType.PartPurchased)
                .OrderBy(evt => evt.key)
                .ToArray();
            Assert.Equal(2, partEvents.Length);
            Assert.Equal("cost=120", partEvents[0].detail);
            Assert.Equal(10120.0, partEvents[0].valueBefore);
            Assert.Equal("cost=450", partEvents[1].detail);
            Assert.Equal(10450.0, partEvents[1].valueBefore);

            var actions = Ledger.Actions
                .Where(action => action.Type == GameActionType.FundsSpending)
                .OrderBy(action => action.DedupKey)
                .ToArray();
            Assert.Equal(2, actions.Length);
            Assert.Equal("batteryPack", actions[0].DedupKey);
            Assert.Equal(120f, actions[0].FundsSpent);
            Assert.Equal("mk1pod", actions[1].DedupKey);
            Assert.Equal(450f, actions[1].FundsSpent);
        }
    }
}
