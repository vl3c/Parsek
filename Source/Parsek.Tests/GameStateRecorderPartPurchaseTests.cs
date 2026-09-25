using Xunit;

namespace Parsek.Tests
{
    public class GameStateRecorderPartPurchaseTests
    {
        [Fact]
        public void CreatePartPurchasedEvent_BypassOn_WritesZeroChargedCost()
        {
            var evt = GameStateRecorder.CreatePartPurchasedEvent(
                "solidBooster.v2",
                entryCost: 812.5f,
                bypassEntryPurchaseAfterResearch: true,
                ut: 123.4,
                currentFunds: 10000);

            Assert.Equal(GameStateEventType.PartPurchased, evt.eventType);
            Assert.Equal("solidBooster.v2", evt.key);
            Assert.Equal("cost=0", evt.detail);
            Assert.Equal(10000, evt.valueBefore);
            Assert.Equal(10000, evt.valueAfter);
        }

        [Fact]
        public void CreatePartPurchasedEvent_BypassOff_WritesEntryCostIntoFundsDelta()
        {
            var evt = GameStateRecorder.CreatePartPurchasedEvent(
                "solidBooster.v2",
                entryCost: 812.5f,
                bypassEntryPurchaseAfterResearch: false,
                ut: 123.4,
                currentFunds: 10000);

            Assert.Equal(GameStateEventType.PartPurchased, evt.eventType);
            Assert.Equal("solidBooster.v2", evt.key);
            Assert.Equal("cost=812.5", evt.detail);
            Assert.Equal(10812.5, evt.valueBefore);
            Assert.Equal(10000, evt.valueAfter);
        }

        [Fact]
        public void CreatePartPurchasedEvent_IdenticalPartBoughtFree_WritesZeroCost_AndStillAFundsRowForThePart()
        {
            // Stock fires OnPartPurchased for an identical part with costsFunds false and
            // debits nothing for it. The row stays (it is the purchase the state patch
            // re-applies after a rewind), at zero cost.
            var evt = GameStateRecorder.CreatePartPurchasedEvent(
                "mk1pod",
                entryCost: 1600f,
                bypassEntryPurchaseAfterResearch: false,
                ut: 123.4,
                currentFunds: 10000,
                costsFunds: false);

            Assert.Equal("cost=0", evt.detail);
            Assert.Equal(10000, evt.valueBefore);
            Assert.Equal(10000, evt.valueAfter);

            var action = GameStateEventConverter.ConvertEvent(evt, null);
            Assert.NotNull(action);
            Assert.Equal(GameActionType.FundsSpending, action.Type);
            Assert.Equal(FundsSpendingSource.Other, action.FundsSpendingSource);
            Assert.Equal("mk1pod", action.DedupKey);
            Assert.Equal(0f, action.FundsSpent);
        }
    }
}
