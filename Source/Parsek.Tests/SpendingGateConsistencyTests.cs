using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// BUG-G: the affordability gate (CanAffordScienceSpending / CanAffordFundsSpending)
    /// must agree with what the pool patch writes. Since the drawdown guard reads the stock
    /// pool reference (committed-value rule plan section 2.4), the patch writes the
    /// reservation-aware target plus the kept leak, added before the 0 floor, so the gate
    /// spends exactly that: an earning the ledger does not model stays spendable, and a
    /// genuine reservation still gates.
    ///
    /// These cover the pure helper LedgerOrchestrator.ComputeEffectiveAffordable. The live
    /// wrappers are exercised in-game (IncompleteBallisticRuntimeTests); the
    /// reservation/cutoff contract of the wrappers stays covered by RewindUtCutoffTests.
    /// </summary>
    public class SpendingGateConsistencyTests
    {
        [Fact]
        public void KeptLeakNoReservation_IsSpendable()
        {
            // Ledger available 1.004, the reference kept a leak of 123.764 (live 124.768).
            double eff = LedgerOrchestrator.ComputeEffectiveAffordable(
                unflooredAvailable: 1.004, cumLeak: 123.764);

            Assert.Equal(124.768, eff, 3);
        }

        [Fact]
        public void ReservationNoLeak_RespectsReservation_ReturnsAvailable()
        {
            // Running 100, a committed cost of 80 ahead: projected minimum 20.
            double eff = LedgerOrchestrator.ComputeEffectiveAffordable(
                unflooredAvailable: 20.0, cumLeak: 0.0);

            Assert.Equal(20.0, eff, 6);
        }

        [Fact]
        public void KeptLeakAndReservation_SubtractsReservationFromTheLeak()
        {
            // Running 70 with a reserve of 50 (minimum 20), a kept leak of 30: 50, the same
            // value the patch writes for that pool.
            double eff = LedgerOrchestrator.ComputeEffectiveAffordable(
                unflooredAvailable: 20.0, cumLeak: 30.0);

            Assert.Equal(50.0, eff, 6);
            Assert.Equal(StockPoolReference.ResolveCurrencyWriteTarget(20.0, 70.0, 100.0, false, 30.0),
                eff, 6);
        }

        [Fact]
        public void NoLeakNoReservation_ReturnsAvailableUnchanged()
        {
            Assert.Equal(100.0, LedgerOrchestrator.ComputeEffectiveAffordable(100.0, 0.0), 6);
        }

        [Fact]
        public void NegativeLeak_LowersTheSpendableAmount()
        {
            // A spend the ledger does not model (an untracked purchase) is held, so the gate
            // cannot spend it a second time.
            Assert.Equal(90.0, LedgerOrchestrator.ComputeEffectiveAffordable(100.0, -10.0), 6);
        }

        [Fact]
        public void LeakIsAddedBeforeTheFloor_NotAfter()
        {
            // An overcommitted timeline (minimum -40) with a kept leak of 30: the floor
            // applies to the sum (0), never 0 + 30, which would let a kept leak pay for a
            // reserved cost twice (review case B3).
            Assert.Equal(0.0, LedgerOrchestrator.ComputeEffectiveAffordable(-40.0, 30.0), 6);
            Assert.Equal(10.0, LedgerOrchestrator.ComputeEffectiveAffordable(-20.0, 30.0), 6);
        }
    }
}
