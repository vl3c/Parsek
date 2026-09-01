using Xunit;

namespace Parsek.Tests
{
    // BuildReservationTooltip and BuildTooltipFromLedger are pure functions with no shared
    // static state, so this class does not need the Sequential collection or any
    // ResetForTesting plumbing.
    public class CurrencyReservationOverlayTests
    {
        [Fact]
        public void FundsTooltip_WhenReserved_ShowsTotalAndReservedWithThousandsSeparators()
        {
            string text = CurrencyReservationOverlay.BuildReservationTooltip(
                total: 100000, available: 70000, format: "N0", minProjected: 70000);

            Assert.Equal("Total: 100,000\nReserved: 30,000", text);
        }

        [Fact]
        public void ScienceTooltip_WhenReserved_ShowsOneDecimalPlace()
        {
            string text = CurrencyReservationOverlay.BuildReservationTooltip(
                total: 42.5, available: 30.0, format: "F1", minProjected: 30.0);

            Assert.Equal("Total: 42.5\nReserved: 12.5", text);
        }

        [Fact]
        public void NothingReserved_StillRendersWithZero()
        {
            // The tooltip always renders so the player can see that nothing is reserved,
            // rather than wondering why no tooltip appeared. No header line.
            string text = CurrencyReservationOverlay.BuildReservationTooltip(
                total: 50000, available: 50000, format: "N0", minProjected: 50000);

            Assert.Equal("Total: 50,000\nReserved: 0", text);
        }

        [Fact]
        public void AvailableExceedsTotal_ClampsReservedToZero()
        {
            // Defensive: negative "reserved" should never render.
            string text = CurrencyReservationOverlay.BuildReservationTooltip(
                total: 10000, available: 12000, format: "N0", minProjected: 10000);

            Assert.Equal("Total: 10,000\nReserved: 0", text);
        }

        [Fact]
        public void OverCommitted_AvailableClampedToZero_ShowsFullBalanceReserved()
        {
            // When committed-future spend exceeds the balance, the bar (available) is
            // clamped to 0 and the entire current balance is reserved. A projected
            // minimum of exactly zero is not a shortfall.
            string text = CurrencyReservationOverlay.BuildReservationTooltip(
                total: 50000, available: 0, format: "N0", minProjected: 0);

            Assert.Equal("Total: 50,000\nReserved: 50,000", text);
        }

        // RESERVATION-OVERLAY-GAPS (b): the Total / Reserved pair cannot express a deficit,
        // so the over-commit magnitude gets its own line and a floored-bar negative running
        // balance replaces the pair with the signed balance.

        [Fact]
        public void OverCommitted_WithShortfall_AddsShortByLine()
        {
            // Balance 50,000, committed future 62,000: the whole balance is reserved AND
            // the pool is 12,000 short of what is committed.
            string text = CurrencyReservationOverlay.BuildReservationTooltip(
                total: 50000, available: 0, format: "N0", minProjected: -12000);

            Assert.Equal("Total: 50,000\nReserved: 50,000\nShort by: 12,000", text);
        }

        [Fact]
        public void NegativeBalance_ShowsTheSignedBalance_NotTotalZeroReservedZero()
        {
            // The running balance itself is negative and the bar is floored at 0: nothing
            // is being held back, so "Total: 0 / Reserved: 0" would report the opposite of
            // the truth. The projected minimum equals the balance here, so no second line:
            // it would only repeat the same magnitude.
            string text = CurrencyReservationOverlay.BuildReservationTooltip(
                total: -5000, available: 0, format: "N0", minProjected: -5000);

            Assert.Equal("Balance: -5,000", text);
        }

        [Fact]
        public void NegativeBalance_FutureDigsDeeper_AddsShortByAtTheWorstPoint()
        {
            string text = CurrencyReservationOverlay.BuildReservationTooltip(
                total: -5000, available: 0, format: "N0", minProjected: -9000);

            Assert.Equal("Balance: -5,000\nShort by: 9,000", text);
        }

        [Fact]
        public void FromLedger_Healthy_TotalIsBarPlusReserved()
        {
            string text = CurrencyReservationOverlay.BuildTooltipFromLedger(
                runningBalance: 100000, available: 70000, minProjected: 70000, displayed: 70000, format: "N0");

            Assert.Equal("Total: 100,000\nReserved: 30,000", text);
        }

        [Fact]
        public void FromLedger_ScienceHoldBack_KeepsTotalMinusReservedEqualToTheBar()
        {
            // The pending-tech-unlock window holds the bar below the ledger availability;
            // Total is anchored on the bar so Total - Reserved is the on-screen number.
            string text = CurrencyReservationOverlay.BuildTooltipFromLedger(
                runningBalance: 100.0, available: 80.0, minProjected: 80.0, displayed: 65.0, format: "F1");

            Assert.Equal("Total: 85.0\nReserved: 20.0", text);
        }

        [Fact]
        public void FromLedger_GenuineDeficit_BarFloored_EngagesTheDeficitWording()
        {
            // Running balance -5,000, availability floored to 0, bar floored to 0: the old
            // derivation produced "Total: 0 / Reserved: 0" here.
            string text = CurrencyReservationOverlay.BuildTooltipFromLedger(
                runningBalance: -5000, available: 0, minProjected: -5000, displayed: 0, format: "N0");

            Assert.Equal("Balance: -5,000", text);
        }

        [Fact]
        public void FromLedger_NegativeLedgerButBarHeldUpByTheDrawdownGuard_StaysBarAnchored()
        {
            // KspStatePatcher's "keep what you earned" guard clamps the bar up to the live
            // value when the ledger runs below it, so the player sees 30,000. The tooltip
            // must reconcile with that number: no reservation, and the shortfall named.
            string text = CurrencyReservationOverlay.BuildTooltipFromLedger(
                runningBalance: -5000, available: 0, minProjected: -5000, displayed: 30000, format: "N0");

            Assert.Equal("Total: 30,000\nReserved: 0\nShort by: 5,000", text);
        }

        [Fact]
        public void FromLedger_OverCommitted_ReservesTheBalanceAndNamesTheShortfall()
        {
            string text = CurrencyReservationOverlay.BuildTooltipFromLedger(
                runningBalance: 50000, available: 0, minProjected: -12000, displayed: 0, format: "N0");

            Assert.Equal("Total: 50,000\nReserved: 50,000\nShort by: 12,000", text);
        }
    }
}
