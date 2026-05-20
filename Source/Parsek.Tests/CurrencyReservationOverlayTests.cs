using Xunit;

namespace Parsek.Tests
{
    // BuildReservationTooltip is a pure function with no shared static state, so this
    // class does not need the Sequential collection or any ResetForTesting plumbing.
    public class CurrencyReservationOverlayTests
    {
        [Fact]
        public void FundsTooltip_WhenReserved_ShowsTotalAndReservedWithThousandsSeparators()
        {
            string text = CurrencyReservationOverlay.BuildReservationTooltip(
                total: 100000, available: 70000, format: "N0");

            Assert.Equal("Total: 100,000\nReserved: 30,000", text);
        }

        [Fact]
        public void ScienceTooltip_WhenReserved_ShowsOneDecimalPlace()
        {
            string text = CurrencyReservationOverlay.BuildReservationTooltip(
                total: 42.5, available: 30.0, format: "F1");

            Assert.Equal("Total: 42.5\nReserved: 12.5", text);
        }

        [Fact]
        public void NothingReserved_StillRendersWithZero()
        {
            // The tooltip always renders so the player can see that nothing is reserved,
            // rather than wondering why no tooltip appeared. No header line.
            string text = CurrencyReservationOverlay.BuildReservationTooltip(
                total: 50000, available: 50000, format: "N0");

            Assert.Equal("Total: 50,000\nReserved: 0", text);
        }

        [Fact]
        public void AvailableExceedsTotal_ClampsReservedToZero()
        {
            // Defensive: negative "reserved" should never render.
            string text = CurrencyReservationOverlay.BuildReservationTooltip(
                total: 10000, available: 12000, format: "N0");

            Assert.Equal("Total: 10,000\nReserved: 0", text);
        }

        [Fact]
        public void OverCommitted_AvailableClampedToZero_ShowsFullBalanceReserved()
        {
            // When committed-future spend exceeds the balance, the bar (available) is
            // clamped to 0 and the entire current balance is reserved.
            string text = CurrencyReservationOverlay.BuildReservationTooltip(
                total: 50000, available: 0, format: "N0");

            Assert.Equal("Total: 50,000\nReserved: 50,000", text);
        }
    }
}
