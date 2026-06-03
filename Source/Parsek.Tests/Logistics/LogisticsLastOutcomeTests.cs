using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins <see cref="LogisticsWindowUI.ClassifyLastOutcome"/>, the pure window-side
    /// helper (H3) that maps a route's status + its realized-delivery summary to a
    /// <see cref="LogisticsDeliveryPresentation.DeliveryOutcome"/> for the delivery
    /// badge. The key rule: a blocked-but-flying wait state forces
    /// <see cref="LogisticsDeliveryPresentation.DeliveryOutcome.None"/> regardless of
    /// any stale row (this cycle is delivering nothing), so a WaitingForResources route
    /// reads as "Flying, not delivering" even if an old Full row exists. RouteStatus is
    /// internal, so the status inputs stay inside each method body. Unity-free, so
    /// exercised directly.
    /// </summary>
    public class LogisticsLastOutcomeTests
    {
        private static LogisticsDeliveryPresentation.RouteDeliverySummary SummaryWith(
            IReadOnlyDictionary<string, double> lastActual,
            IReadOnlyDictionary<string, double> lastRequested,
            int rowCount = 1)
        {
            return new LogisticsDeliveryPresentation.RouteDeliverySummary
            {
                RowCount = rowCount,
                LastActual = lastActual,
                LastRequested = lastRequested,
                CumulativeTotal = new Dictionary<string, double>()
            };
        }

        // An Active route whose latest row is a full fill (no requested manifest) is
        // Full.
        [Fact]
        public void Active_FullFillRow_IsFull()
        {
            var summary = SummaryWith(
                lastActual: new Dictionary<string, double> { { "LiquidFuel", 150.0 } },
                lastRequested: null);
            Assert.Equal(
                LogisticsDeliveryPresentation.DeliveryOutcome.Full,
                LogisticsWindowUI.ClassifyLastOutcome(RouteStatus.Active, summary));
        }

        // An Active route whose latest row recorded a requested manifest (a shortfall)
        // is Partial.
        [Fact]
        public void Active_PartialRow_IsPartial()
        {
            var summary = SummaryWith(
                lastActual: new Dictionary<string, double> { { "LiquidFuel", 40.0 } },
                lastRequested: new Dictionary<string, double> { { "LiquidFuel", 150.0 } });
            Assert.Equal(
                LogisticsDeliveryPresentation.DeliveryOutcome.Partial,
                LogisticsWindowUI.ClassifyLastOutcome(RouteStatus.Active, summary));
        }

        // No delivered rows at all -> None.
        [Fact]
        public void Active_NoRows_IsNone()
        {
            var summary = new LogisticsDeliveryPresentation.RouteDeliverySummary
            {
                RowCount = 0,
                CumulativeTotal = new Dictionary<string, double>()
            };
            Assert.Equal(
                LogisticsDeliveryPresentation.DeliveryOutcome.None,
                LogisticsWindowUI.ClassifyLastOutcome(RouteStatus.Active, summary));
        }

        // A null summary -> None (defensive; the cache builds a default struct on a
        // missing route).
        [Fact]
        public void NullSummary_IsNone()
        {
            Assert.Equal(
                LogisticsDeliveryPresentation.DeliveryOutcome.None,
                LogisticsWindowUI.ClassifyLastOutcome(RouteStatus.Active, null));
        }

        // A row with an empty actual manifest reads as None (delivered nothing).
        [Fact]
        public void Active_EmptyActual_IsNone()
        {
            var summary = SummaryWith(
                lastActual: new Dictionary<string, double>(),
                lastRequested: null);
            Assert.Equal(
                LogisticsDeliveryPresentation.DeliveryOutcome.None,
                LogisticsWindowUI.ClassifyLastOutcome(RouteStatus.Active, summary));
        }

        // The load-bearing wait-state rule: a blocked-but-flying route forces None even
        // when a stale Full row exists, so the badge reads "Flying, not delivering".
        [Theory]
        [InlineData((int)RouteStatus.WaitingForResources)]
        [InlineData((int)RouteStatus.WaitingForFunds)]
        [InlineData((int)RouteStatus.DestinationFull)]
        public void WaitState_WithStaleFullRow_ForcesNone(int statusOrdinal)
        {
            var summary = SummaryWith(
                lastActual: new Dictionary<string, double> { { "LiquidFuel", 150.0 } },
                lastRequested: null);
            Assert.Equal(
                LogisticsDeliveryPresentation.DeliveryOutcome.None,
                LogisticsWindowUI.ClassifyLastOutcome((RouteStatus)statusOrdinal, summary));
        }

        // End-to-end through the badge classifier: a WaitingForResources route that is
        // ghost-driving with a stale Full row reads as FlyingNotDelivering, not
        // Delivering.
        [Fact]
        public void WaitState_BadgeReadsFlyingNotDelivering()
        {
            var summary = SummaryWith(
                lastActual: new Dictionary<string, double> { { "LiquidFuel", 150.0 } },
                lastRequested: null);
            LogisticsDeliveryPresentation.DeliveryOutcome outcome =
                LogisticsWindowUI.ClassifyLastOutcome(RouteStatus.WaitingForResources, summary);
            bool ghostDriving = RouteStatusPolicy.GhostDriving(RouteStatus.WaitingForResources);
            LogisticsDeliveryPresentation.DeliveryBadge badge =
                LogisticsDeliveryPresentation.ClassifyDeliveryBadge(
                    ghostDriving, outcome, completedCycles: 2, skippedCycles: 1);
            Assert.Equal(LogisticsDeliveryPresentation.DeliveryBadge.FlyingNotDelivering, badge);
        }

        // End-to-end: an Active route with a fresh Full row reads as Delivering.
        [Fact]
        public void Active_BadgeReadsDelivering()
        {
            var summary = SummaryWith(
                lastActual: new Dictionary<string, double> { { "LiquidFuel", 150.0 } },
                lastRequested: null);
            LogisticsDeliveryPresentation.DeliveryOutcome outcome =
                LogisticsWindowUI.ClassifyLastOutcome(RouteStatus.Active, summary);
            bool ghostDriving = RouteStatusPolicy.GhostDriving(RouteStatus.Active);
            LogisticsDeliveryPresentation.DeliveryBadge badge =
                LogisticsDeliveryPresentation.ClassifyDeliveryBadge(
                    ghostDriving, outcome, completedCycles: 5, skippedCycles: 0);
            Assert.Equal(LogisticsDeliveryPresentation.DeliveryBadge.Delivering, badge);
        }
    }
}
