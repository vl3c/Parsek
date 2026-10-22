using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// BUG-G: the affordability gate (CanAffordScienceSpending / CanAffordFundsSpending)
    /// must agree with what the pool patch writes. A measured leak is a StockPoolAdjustment
    /// ledger row (committed-value rule plan section 2.4), so the probe's walk already carries
    /// it: the gate spends the probe's projected minimum, floored at 0, which is the value the
    /// patch writes for that pool. An earning the ledger does not model stays spendable
    /// through its adjustment row, and a genuine reservation still gates.
    ///
    /// These cover the pure helper LedgerOrchestrator.ComputeEffectiveAffordable. The live
    /// wrappers are exercised in-game (IncompleteBallisticRuntimeTests); the
    /// reservation/cutoff contract of the wrappers stays covered by RewindUtCutoffTests.
    /// </summary>
    public class SpendingGateConsistencyTests
    {
        [Fact]
        public void AdjustedMinimumNoReservation_IsSpendable()
        {
            // Ledger rows give 1.004, an adjustment row of 123.764 is in the walk: the
            // probe's minimum is 124.768, all spendable.
            double eff = LedgerOrchestrator.ComputeEffectiveAffordable(1.004 + 123.764);

            Assert.Equal(124.768, eff, 3);
        }

        [Fact]
        public void Reservation_RespectsReservation_ReturnsAvailable()
        {
            // Running 100, a committed cost of 80 ahead: projected minimum 20.
            double eff = LedgerOrchestrator.ComputeEffectiveAffordable(20.0);

            Assert.Equal(20.0, eff, 6);
        }

        [Fact]
        public void AdjustmentAndReservation_AgreesWithThePatchWrite()
        {
            // Running 70 with a reserve of 50 (minimum 20) and an adjustment row of 30 in the
            // walk: minimum 50, the same value the patch writes (its adjustment for this
            // patch is 0, since the row is already counted).
            double eff = LedgerOrchestrator.ComputeEffectiveAffordable(50.0);

            Assert.Equal(50.0, eff, 6);
            Assert.Equal(StockPoolReference.ResolveCurrencyWriteTarget(50.0, 100.0, 100.0, false, 0.0),
                eff, 6);
        }

        [Fact]
        public void NoReservation_ReturnsAvailableUnchanged()
        {
            Assert.Equal(100.0, LedgerOrchestrator.ComputeEffectiveAffordable(100.0), 6);
        }

        [Fact]
        public void OvercommittedMinimum_FloorsAtZero()
        {
            // An overcommitted timeline (minimum -40, adjustment rows included): nothing is
            // spendable, so a kept leak never pays for a reserved cost twice (review case B3).
            Assert.Equal(0.0, LedgerOrchestrator.ComputeEffectiveAffordable(-40.0 + 30.0), 6);
            Assert.Equal(10.0, LedgerOrchestrator.ComputeEffectiveAffordable(-20.0 + 30.0), 6);
        }
    }
}
