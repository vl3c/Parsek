using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// BUG-G: the affordability gate (CanAffordScienceSpending / CanAffordFundsSpending)
    /// must be consistent with the "Keep what you earned" drawdown guard
    /// (docs/dev/plans/recalc-patch-drawdown-guard.md). The guard preserves the LIVE pool
    /// when the running balance has leaked below it with no time-travel context, so the
    /// gate must respect that preserved value (or it blocks a purchase the player can make
    /// AND — pre-fix — the stock deduction already consumed the resource).
    ///
    /// These cover the pure reconciliation helper LedgerOrchestrator.ComputeEffectiveAffordable.
    /// The live wrappers' singleton reconciliation is exercised in
    /// RuntimeTests (in-game, real ResearchAndDevelopment/Funding singletons); the
    /// reservation/cutoff contract of the wrappers themselves stays covered by
    /// RewindUtCutoffTests (null-singleton fallback to the pure-ledger available).
    /// </summary>
    public class SpendingGateConsistencyTests
    {
        // ----------------------------------------------------------------
        // Leak case (BUG-G): running has fallen below live, no reservation.
        // The guard preserves live, so the gate must too.
        // ----------------------------------------------------------------

        [Fact]
        public void LeakNoReservation_AddsBackTheFullLeakGap_ReturnsLive()
        {
            // available == running (no reservation) == 1.004, live == 124.768 (guard-preserved).
            double eff = LedgerOrchestrator.ComputeEffectiveAffordable(
                available: 1.004, runningBalance: 1.004, liveValue: 124.768,
                authoritativeReduction: false);

            Assert.Equal(124.768, eff, 3);
        }

        [Fact]
        public void LeakNoReservation_TheExactBugGCase_IsAffordable()
        {
            // The recorded BUG-G frame: cost 45 was blocked with available=1.0 while live
            // science was preserved at 124.768. With the fix the gate sees the live value.
            double eff = LedgerOrchestrator.ComputeEffectiveAffordable(
                available: 1.004, runningBalance: 1.004, liveValue: 124.768,
                authoritativeReduction: false);

            Assert.True(eff >= 45.0);
        }

        // ----------------------------------------------------------------
        // Reservation case: running is INTACT (>= live), available < running.
        // The reservation must still be respected — this is NOT max(available, live).
        // ----------------------------------------------------------------

        [Fact]
        public void ReservationNoLeak_RespectsReservation_ReturnsAvailable()
        {
            // running 100 == live 100 (no leak), available 20 (reservation of 80).
            double eff = LedgerOrchestrator.ComputeEffectiveAffordable(
                available: 20.0, runningBalance: 100.0, liveValue: 100.0,
                authoritativeReduction: false);

            Assert.Equal(20.0, eff, 6);
        }

        [Fact]
        public void ReservationNoLeak_BlocksOverspendOfReservedScience()
        {
            // A plain max(available, live) would return 100 and let the player overspend
            // the 80 reserved for committed future unlocks. The correct value is 20.
            double eff = LedgerOrchestrator.ComputeEffectiveAffordable(
                available: 20.0, runningBalance: 100.0, liveValue: 100.0,
                authoritativeReduction: false);

            Assert.False(eff >= 50.0);
        }

        [Fact]
        public void LeakAndReservation_SubtractsReservationFromLeakCorrectedBase()
        {
            // Leak (running 70 < live 100) AND reservation (available 20, reserved 50).
            // Effective = available + (live - running) = 20 + 30 = 50 == live - reserved.
            double eff = LedgerOrchestrator.ComputeEffectiveAffordable(
                available: 20.0, runningBalance: 70.0, liveValue: 100.0,
                authoritativeReduction: false);

            Assert.Equal(50.0, eff, 6);
        }

        // ----------------------------------------------------------------
        // No leak, no reservation: gate is unchanged from the pure-ledger value.
        // ----------------------------------------------------------------

        [Fact]
        public void NoLeakNoReservation_ReturnsAvailableUnchanged()
        {
            double eff = LedgerOrchestrator.ComputeEffectiveAffordable(
                available: 100.0, runningBalance: 100.0, liveValue: 100.0,
                authoritativeReduction: false);

            Assert.Equal(100.0, eff, 6);
        }

        [Fact]
        public void RunningAboveLive_DoesNotReduceBelowAvailable()
        {
            // Transient where a recent debit hit live but not the ledger (running > live).
            // The guard clamps UP-only, so the gate must never reduce below available.
            double eff = LedgerOrchestrator.ComputeEffectiveAffordable(
                available: 100.0, runningBalance: 120.0, liveValue: 110.0,
                authoritativeReduction: false);

            Assert.Equal(100.0, eff, 6);
        }

        // ----------------------------------------------------------------
        // Authoritative (time-travel) reduction: the guard does NOT preserve live, so the
        // reservation-aware available is ground truth and is returned unchanged even when
        // running < live (Blocker-1 plain-rewind path symmetry).
        // ----------------------------------------------------------------

        [Fact]
        public void AuthoritativeReduction_IgnoresLeakGap_ReturnsAvailable()
        {
            double eff = LedgerOrchestrator.ComputeEffectiveAffordable(
                available: 10.0, runningBalance: 10.0, liveValue: 124.768,
                authoritativeReduction: true);

            Assert.Equal(10.0, eff, 6);
        }

        [Fact]
        public void AuthoritativeReduction_StillRespectsReservation()
        {
            double eff = LedgerOrchestrator.ComputeEffectiveAffordable(
                available: 20.0, runningBalance: 100.0, liveValue: 100.0,
                authoritativeReduction: true);

            Assert.Equal(20.0, eff, 6);
        }

        // ----------------------------------------------------------------
        // Boundary: live exactly equals running → no gap, returns available.
        // ----------------------------------------------------------------

        [Fact]
        public void LiveEqualsRunning_ReturnsAvailable()
        {
            double eff = LedgerOrchestrator.ComputeEffectiveAffordable(
                available: 42.0, runningBalance: 50.0, liveValue: 50.0,
                authoritativeReduction: false);

            Assert.Equal(42.0, eff, 6);
        }
    }
}
