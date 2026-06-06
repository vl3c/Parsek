using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the M4 pure helpers in <see cref="LogisticsDeliveryPresentation"/>: the
    /// DestinationFull free-capacity context formatter
    /// (<see cref="LogisticsDeliveryPresentation.FormatCapacityContext"/>) and the
    /// EndpointLost re-scan eligibility decision
    /// (<see cref="LogisticsDeliveryPresentation.ShouldOfferEndpointRescan"/> /
    /// <see cref="LogisticsDeliveryPresentation.RescanIneligibleReason"/>). Both are
    /// Unity-free (the live capacity probe and the actual re-scan resolve happen in
    /// the window, not here), so they are exercised directly without IMGUI or a live
    /// Vessel. <see cref="RouteStatus"/> is internal, so the re-scan theories take an
    /// int ordinal and cast inside (mirrors the other Logistics tests).
    /// </summary>
    public class LogisticsCapacityContextTests
    {
        // ------------------------------------------------------------------
        // FormatCapacityContext
        // ------------------------------------------------------------------

        private static LogisticsDeliveryPresentation.CapacityEntry Entry(
            string resource, double requested, double free)
        {
            return new LogisticsDeliveryPresentation.CapacityEntry(resource, requested, free);
        }

        // catches: the full-tank case not reading as "tanks full". A 150 request
        // against 0 free renders "0.0 of 150.0 LiquidFuel free" with the vessel name.
        [Fact]
        public void FormatCapacityContext_FullTank_ZeroFree()
        {
            var entries = new List<LogisticsDeliveryPresentation.CapacityEntry>
            {
                Entry("LiquidFuel", 150.0, 0.0)
            };
            Assert.Equal(
                "Munar Station tanks full: 0.0 of 150.0 LiquidFuel free",
                LogisticsDeliveryPresentation.FormatCapacityContext("Munar Station", entries));
        }

        // catches: a partial-free case not showing the remaining headroom. 40 free
        // of a 150 request renders the partial number, not 0.
        [Fact]
        public void FormatCapacityContext_PartialFree()
        {
            var entries = new List<LogisticsDeliveryPresentation.CapacityEntry>
            {
                Entry("LiquidFuel", 150.0, 40.0)
            };
            Assert.Equal(
                "Munar Station tanks full: 40.0 of 150.0 LiquidFuel free",
                LogisticsDeliveryPresentation.FormatCapacityContext("Munar Station", entries));
        }

        // catches: an unstable multi-resource order. Entries are ordinal-sorted by
        // resource key, so Oxidizer follows LiquidFuel regardless of input order.
        [Fact]
        public void FormatCapacityContext_MultiResource_OrdinalSorted()
        {
            var entries = new List<LogisticsDeliveryPresentation.CapacityEntry>
            {
                Entry("Oxidizer", 50.0, 5.0),
                Entry("LiquidFuel", 150.0, 0.0)
            };
            Assert.Equal(
                "Base tanks full: 0.0 of 150.0 LiquidFuel free, 5.0 of 50.0 Oxidizer free",
                LogisticsDeliveryPresentation.FormatCapacityContext("Base", entries));
        }

        // catches: a comma-locale misformat (decimal comma). InvariantCulture must
        // render F1 with a dot, never a comma, so the resource separator stays
        // unambiguous on a comma-locale system.
        [Fact]
        public void FormatCapacityContext_InvariantCulture_NoCommaDecimal()
        {
            var entries = new List<LogisticsDeliveryPresentation.CapacityEntry>
            {
                Entry("LiquidFuel", 12.5, 3.5)
            };
            string text = LogisticsDeliveryPresentation.FormatCapacityContext("Base", entries);
            Assert.Contains("3.5 of 12.5 LiquidFuel free", text);
            Assert.DoesNotContain("3,5", text);
            Assert.DoesNotContain("12,5", text);
        }

        // catches: a blank line when the destination was DestinationFull but could
        // not be probed. Null / empty entries fall back to a "(capacity unknown)"
        // clause rather than an empty body.
        [Fact]
        public void FormatCapacityContext_NullOrEmptyEntries_CapacityUnknown()
        {
            Assert.Equal("Base tanks full: (capacity unknown)",
                LogisticsDeliveryPresentation.FormatCapacityContext("Base", null));
            Assert.Equal("Base tanks full: (capacity unknown)",
                LogisticsDeliveryPresentation.FormatCapacityContext(
                    "Base", new List<LogisticsDeliveryPresentation.CapacityEntry>()));
        }

        // catches: a null destination name producing "null tanks full". Falls back to
        // a generic "Destination" label.
        [Fact]
        public void FormatCapacityContext_NullName_FallsBackToDestination()
        {
            var entries = new List<LogisticsDeliveryPresentation.CapacityEntry>
            {
                Entry("LiquidFuel", 10.0, 0.0)
            };
            Assert.Equal(
                "Destination tanks full: 0.0 of 10.0 LiquidFuel free",
                LogisticsDeliveryPresentation.FormatCapacityContext(null, entries));
        }

        // ------------------------------------------------------------------
        // ShouldOfferEndpointRescan
        // ------------------------------------------------------------------

        private static RouteEndpoint Endpoint(bool isSurface, string body)
        {
            return new RouteEndpoint { IsSurface = isSurface, BodyName = body };
        }

        // catches: a recoverable surface EndpointLost not offering the re-scan. A
        // surface endpoint with a body, lost, is the one case re-scan can recover.
        [Fact]
        public void ShouldOfferEndpointRescan_SurfaceLostWithBody_True()
        {
            Assert.True(LogisticsDeliveryPresentation.ShouldOfferEndpointRescan(
                RouteStatus.EndpointLost, Endpoint(isSurface: true, body: "Mun")));
        }

        // catches: an orbital EndpointLost wrongly offering re-scan. Re-running the
        // resolver only repeats the identical baked-PID lookup, so re-scan cannot
        // recover an orbital endpoint.
        [Fact]
        public void ShouldOfferEndpointRescan_OrbitalLost_False()
        {
            Assert.False(LogisticsDeliveryPresentation.ShouldOfferEndpointRescan(
                RouteStatus.EndpointLost, Endpoint(isSurface: false, body: "Mun")));
        }

        // catches: a surface endpoint with no body anchor offering re-scan. The
        // surface-proximity fallback needs a body name.
        [Fact]
        public void ShouldOfferEndpointRescan_SurfaceLostNoBody_False()
        {
            Assert.False(LogisticsDeliveryPresentation.ShouldOfferEndpointRescan(
                RouteStatus.EndpointLost, Endpoint(isSurface: true, body: "")));
        }

        // catches: a non-EndpointLost status offering re-scan. Re-scan is only for the
        // lost-endpoint state; every other status must not offer it.
        [Theory]
        [InlineData((int)RouteStatus.Active)]
        [InlineData((int)RouteStatus.InTransit)]
        [InlineData((int)RouteStatus.DestinationFull)]
        [InlineData((int)RouteStatus.Paused)]
        [InlineData((int)RouteStatus.MissingSourceRecording)]
        [InlineData((int)RouteStatus.SourceChanged)]
        public void ShouldOfferEndpointRescan_NonEndpointLost_False(int statusOrdinal)
        {
            // Even with a perfectly recoverable surface endpoint, a non-lost status
            // never offers re-scan.
            Assert.False(LogisticsDeliveryPresentation.ShouldOfferEndpointRescan(
                (RouteStatus)statusOrdinal, Endpoint(isSurface: true, body: "Mun")));
        }

        // ------------------------------------------------------------------
        // RescanIneligibleReason
        // ------------------------------------------------------------------

        // catches: the orbital and no-body explanations collapsing into one string.
        // They must steer the player to re-create for distinct reasons.
        [Fact]
        public void RescanIneligibleReason_OrbitalVsNoBody_Distinct()
        {
            string orbital = LogisticsDeliveryPresentation.RescanIneligibleReason(
                Endpoint(isSurface: false, body: "Mun"));
            string noBody = LogisticsDeliveryPresentation.RescanIneligibleReason(
                Endpoint(isSurface: true, body: ""));

            Assert.Contains("Orbital endpoint", orbital);
            Assert.Contains("no body reference", noBody);
            Assert.NotEqual(orbital, noBody);
        }
    }
}
