using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// PURCHASE-STATE-PATCH-RUNS-WHEN-THE-FUNDS-PATCH-IS-CLAMPED: on a clock crossing, a
    /// committed part purchase or part upgrade the clock reached is marked bought only by a
    /// pass whose funds patch took its cost. The funds patch now runs before the two purchase
    /// state patches, and a crossing whose funds patch skipped holds them.
    /// </summary>
    [Collection("Sequential")]
    public class PurchaseStatePatchFundsGateTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PurchaseStatePatchFundsGateTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            KspStatePatcher.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateStore.SuppressLogging = false;
            KspStatePatcher.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static readonly IReadOnlyList<GameAction> NoRows = new List<GameAction>();

        private void PatchAllWith(FundsModule funds)
        {
            KspStatePatcher.PatchAll(new ScienceModule(), funds, new ReputationModule(),
                new MilestonesModule(), new FacilitiesModule(),
                partPurchaseActions: NoRows, walkUtCutoff: 100.0);
        }

        private bool Held() =>
            logLines.Exists(l => l.Contains("[KspStatePatcher]")
                && l.Contains("Clock-crossing purchase unlocks held"));

        private bool PurchasePassesRan() =>
            logLines.Exists(l => l.Contains("PatchPurchasedParts:"))
            && logLines.Exists(l => l.Contains("PatchUnlockedUpgrades:"));

        [Fact]
        public void Decide_HoldsOnlyACrossingWhoseFundsPatchSkippedWithAPoolToCharge()
        {
            Assert.False(KspStatePatcher.ShouldHoldPurchaseStatePatches(null));
            Assert.True(KspStatePatcher.ShouldHoldPurchaseStatePatches(
                new KspStatePatcher.ClockCrossing()));
            Assert.False(KspStatePatcher.ShouldHoldPurchaseStatePatches(
                new KspStatePatcher.ClockCrossing { FundsApplied = true }));
            Assert.False(KspStatePatcher.ShouldHoldPurchaseStatePatches(
                new KspStatePatcher.ClockCrossing { NoFundsPool = true }));
        }

        [Fact]
        public void Crossing_FundsPatchSkipped_HoldsBothPurchasePasses()
        {
            // A null funds module is a skip that is not "no pool": the reached rows' cost
            // did not reach the live pool, so nothing may be unlocked this pass.
            KspStatePatcher.ActiveClockCrossing = new KspStatePatcher.ClockCrossing { CrossedRows = 2 };

            PatchAllWith(null);

            Assert.True(Held());
            Assert.Contains(logLines, l => l.Contains("Clock-crossing purchase unlocks held")
                && l.Contains("crossedRows=2"));
            Assert.DoesNotContain(logLines, l => l.Contains("PatchPurchasedParts:"));
            Assert.DoesNotContain(logLines, l => l.Contains("PatchUnlockedUpgrades:"));
        }

        [Fact]
        public void Crossing_FundsPatchRunsBeforeThePurchasePasses()
        {
            KspStatePatcher.ActiveClockCrossing = new KspStatePatcher.ClockCrossing { CrossedRows = 1 };

            PatchAllWith(null);

            int funds = logLines.FindIndex(l => l.Contains("PatchFunds: null FundsModule"));
            int held = logLines.FindIndex(l => l.Contains("Clock-crossing purchase unlocks held"));
            Assert.True(funds >= 0);
            Assert.True(held > funds);
        }

        [Fact]
        public void Crossing_NoFundsPool_DoesNotHold()
        {
            // Headless there is no Funding singleton: sandbox / science mode, nothing to charge.
            var crossing = new KspStatePatcher.ClockCrossing { CrossedRows = 1 };
            KspStatePatcher.ActiveClockCrossing = crossing;

            PatchAllWith(new FundsModule());

            Assert.True(crossing.NoFundsPool);
            Assert.False(crossing.FundsApplied);
            Assert.False(Held());
            Assert.True(PurchasePassesRan());
        }

        [Fact]
        public void NoCrossing_FundsPatchSkipped_DoesNotHold()
        {
            KspStatePatcher.ActiveClockCrossing = null;

            PatchAllWith(null);

            Assert.False(Held());
            Assert.True(PurchasePassesRan());
        }
    }
}
