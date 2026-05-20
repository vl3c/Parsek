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
                total: 100000, available: 70000, format: "N0", epsilon: 0.5);

            Assert.Equal("Total: 100,000\nReserved: 30,000", text);
        }

        [Fact]
        public void ScienceTooltip_WhenReserved_ShowsOneDecimalPlace()
        {
            string text = CurrencyReservationOverlay.BuildReservationTooltip(
                total: 42.5, available: 30.0, format: "F1", epsilon: 0.05);

            Assert.Equal("Total: 42.5\nReserved: 12.5", text);
        }

        [Fact]
        public void NothingReserved_ReturnsNull()
        {
            string text = CurrencyReservationOverlay.BuildReservationTooltip(
                total: 50000, available: 50000, format: "N0", epsilon: 0.5);

            Assert.Null(text);
        }

        [Fact]
        public void ReservedBelowEpsilon_ReturnsNull()
        {
            // 0.3 reserved is rounding noise under the 0.5 funds epsilon.
            string text = CurrencyReservationOverlay.BuildReservationTooltip(
                total: 50000.3, available: 50000.0, format: "N0", epsilon: 0.5);

            Assert.Null(text);
        }

        [Fact]
        public void AvailableExceedsTotal_ReturnsNull()
        {
            // Defensive: negative "reserved" should never render.
            string text = CurrencyReservationOverlay.BuildReservationTooltip(
                total: 10000, available: 12000, format: "N0", epsilon: 0.5);

            Assert.Null(text);
        }

        [Fact]
        public void AlwaysShow_RendersEvenWhenNothingReserved()
        {
            // Testing affordance: with alwaysShow the box renders at reserved=0 so the hover
            // area can be verified in game. No header line - the hovered widget is obvious.
            string text = CurrencyReservationOverlay.BuildReservationTooltip(
                total: 0, available: 0, format: "F1", epsilon: 0.05, alwaysShow: true);

            Assert.Equal("Total: 0.0\nReserved: 0.0", text);
        }

        [Fact]
        public void OverCommitted_AvailableClampedToZero_ShowsFullBalanceReserved()
        {
            // When committed-future spend exceeds the balance, the bar (available) is
            // clamped to 0 and the entire current balance is reserved.
            string text = CurrencyReservationOverlay.BuildReservationTooltip(
                total: 50000, available: 0, format: "N0", epsilon: 0.5);

            Assert.Equal("Total: 50,000\nReserved: 50,000", text);
        }
    }
}
