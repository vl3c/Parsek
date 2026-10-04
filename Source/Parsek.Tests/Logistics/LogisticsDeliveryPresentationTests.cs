using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the pure presentation helpers in
    /// <see cref="LogisticsDeliveryPresentation"/> backing Phase 2 H2 (realized
    /// delivery + cumulative total), H3 (delivery badge classify), H4 (destination
    /// name formatter), and H5 (source recording name formatter). Every helper is
    /// Unity-free and side-effect-free, so they are exercised directly without IMGUI
    /// or a live store. RouteStatus is internal, so the H3 badge tests compute the
    /// ghostDriving bool inside the test body from
    /// <see cref="RouteStatusPolicy.GhostDriving"/> rather than passing the enum
    /// through a public signature.
    /// </summary>
    public class LogisticsDeliveryPresentationTests
    {
        // ------------------------------------------------------------------
        // H2: FormatRealizedDelivery
        // ------------------------------------------------------------------

        // Full fill: requested is null (the recorder's "no shortfall" signal), so the
        // line is just "delivered N Resource" with no shortfall clause.
        [Fact]
        public void FormatRealizedDelivery_FullFill_NoShortfallClause()
        {
            var actual = new Dictionary<string, double> { { "LiquidFuel", 150.0 } };
            string s = LogisticsDeliveryPresentation.FormatRealizedDelivery(null, actual);
            Assert.Equal("delivered 150.0 LiquidFuel", s);
        }

        // Partial fill: requested set above actual; show delivered-of-requested and
        // the amount that did not fit.
        [Fact]
        public void FormatRealizedDelivery_PartialFill_ShowsShortfall()
        {
            var requested = new Dictionary<string, double> { { "LiquidFuel", 150.0 } };
            var actual = new Dictionary<string, double> { { "LiquidFuel", 40.0 } };
            string s = LogisticsDeliveryPresentation.FormatRealizedDelivery(requested, actual);
            Assert.Equal("delivered 40.0 of 150.0 LiquidFuel (110.0 did not fit)", s);
        }

        // A requested entry that matches the actual exactly is treated as a full fill
        // (no shortfall clause) even though a requested manifest was present.
        [Fact]
        public void FormatRealizedDelivery_RequestedEqualsActual_NoShortfall()
        {
            var requested = new Dictionary<string, double> { { "Ore", 20.0 } };
            var actual = new Dictionary<string, double> { { "Ore", 20.0 } };
            string s = LogisticsDeliveryPresentation.FormatRealizedDelivery(requested, actual);
            Assert.Equal("delivered 20.0 Ore", s);
        }

        // Empty / null actual: nothing was delivered this cycle.
        [Fact]
        public void FormatRealizedDelivery_EmptyActual_DeliveredNothing()
        {
            Assert.Equal("delivered nothing",
                LogisticsDeliveryPresentation.FormatRealizedDelivery(null, new Dictionary<string, double>()));
            Assert.Equal("delivered nothing",
                LogisticsDeliveryPresentation.FormatRealizedDelivery(null, null));
        }

        // Comma-decimal locale must not change the output: F1 + InvariantCulture, no
        // thousands separator on a large amount.
        [Fact]
        public void FormatRealizedDelivery_IsInvariantUnderCommaLocale()
        {
            CultureInfo prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var actual = new Dictionary<string, double> { { "LiquidFuel", 1240.0 } };
                string s = LogisticsDeliveryPresentation.FormatRealizedDelivery(null, actual);
                Assert.Equal("delivered 1240.0 LiquidFuel", s);
                Assert.DoesNotContain(",", s);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        [Fact]
        public void HasShortfall_OnlyTrueWhenRequestedExceedsActual()
        {
            var actual = new Dictionary<string, double> { { "LiquidFuel", 40.0 } };
            Assert.True(LogisticsDeliveryPresentation.HasShortfall(
                new Dictionary<string, double> { { "LiquidFuel", 150.0 } }, actual));
            Assert.False(LogisticsDeliveryPresentation.HasShortfall(
                new Dictionary<string, double> { { "LiquidFuel", 40.0 } }, actual));
            // Null requested is a full fill, never a shortfall.
            Assert.False(LogisticsDeliveryPresentation.HasShortfall(null, actual));
        }

        // A resource that was requested but fully blocked (absent from actual) is a
        // shortfall, not just a partial fill of a delivered resource.
        [Fact]
        public void HasShortfall_RequestedResourceFullyBlocked_IsShortfall()
        {
            var actual = new Dictionary<string, double> { { "LiquidFuel", 40.0 } };
            var requested = new Dictionary<string, double> { { "LiquidFuel", 40.0 }, { "Oxidizer", 20.0 } };
            // LiquidFuel fully filled, but Oxidizer was requested and 0 delivered.
            Assert.True(LogisticsDeliveryPresentation.HasShortfall(requested, actual));
        }

        // Multi-resource output is sorted by resource key (ordinal) so the per-resource
        // order is stable across cache refreshes regardless of dictionary insertion order.
        [Fact]
        public void FormatRealizedDelivery_MultiResource_OrdinalSortedStableOrder()
        {
            // Inserted Oxidizer-first; output must still list LiquidFuel first.
            var actual = new Dictionary<string, double> { { "Oxidizer", 10.0 }, { "LiquidFuel", 100.0 } };
            string s = LogisticsDeliveryPresentation.FormatRealizedDelivery(null, actual);
            Assert.Equal("delivered 100.0 LiquidFuel, 10.0 Oxidizer", s);
        }

        // Mixed partial cycle: one resource partially filled, another requested but
        // fully blocked. The fully-blocked resource is included as "0.0 of X (X did not fit)".
        [Fact]
        public void FormatRealizedDelivery_RequestedButFullyBlocked_IncludesZeroDeliveredClause()
        {
            var requested = new Dictionary<string, double> { { "LiquidFuel", 50.0 }, { "Oxidizer", 20.0 } };
            var actual = new Dictionary<string, double> { { "LiquidFuel", 40.0 } };
            string s = LogisticsDeliveryPresentation.FormatRealizedDelivery(requested, actual);
            Assert.Equal(
                "delivered 40.0 of 50.0 LiquidFuel (10.0 did not fit), 0.0 of 20.0 Oxidizer (20.0 did not fit)",
                s);
        }

        // ------------------------------------------------------------------
        // H2: SummarizeRouteDeliveries + FormatCumulativeTotal
        // ------------------------------------------------------------------

        [Fact]
        public void SummarizeRouteDeliveries_EmptyList_ZeroSummary()
        {
            var summary = LogisticsDeliveryPresentation.SummarizeRouteDeliveries(
                new List<LogisticsDeliveryPresentation.DeliveryRow>());
            Assert.False(summary.HasAny);
            Assert.Equal(0, summary.RowCount);
            Assert.Null(summary.LastActual);
            Assert.Empty(summary.CumulativeTotal);
        }

        [Fact]
        public void SummarizeRouteDeliveries_NullList_ZeroSummary()
        {
            var summary = LogisticsDeliveryPresentation.SummarizeRouteDeliveries(null);
            Assert.False(summary.HasAny);
            Assert.Equal(0, summary.RowCount);
        }

        // Latest row is the one with the maximum UT regardless of input order; the
        // cumulative total sums each row's actual per resource.
        [Fact]
        public void SummarizeRouteDeliveries_PicksLatestByUt_AndSumsCumulative()
        {
            var rows = new List<LogisticsDeliveryPresentation.DeliveryRow>
            {
                new LogisticsDeliveryPresentation.DeliveryRow(
                    new Dictionary<string, double> { { "LiquidFuel", 100.0 } }, null, ut: 100.0),
                // Latest by UT, but listed in the middle to prove ordering is by UT.
                new LogisticsDeliveryPresentation.DeliveryRow(
                    new Dictionary<string, double> { { "LiquidFuel", 40.0 } },
                    new Dictionary<string, double> { { "LiquidFuel", 150.0 } }, ut: 300.0),
                new LogisticsDeliveryPresentation.DeliveryRow(
                    new Dictionary<string, double> { { "LiquidFuel", 60.0 }, { "Oxidizer", 10.0 } }, null, ut: 200.0),
            };

            var summary = LogisticsDeliveryPresentation.SummarizeRouteDeliveries(rows);

            Assert.True(summary.HasAny);
            Assert.Equal(3, summary.RowCount);
            // Latest row (ut=300) had the partial fill.
            Assert.Equal(40.0, summary.LastActual["LiquidFuel"]);
            Assert.NotNull(summary.LastRequested);
            Assert.Equal(150.0, summary.LastRequested["LiquidFuel"]);
            // Cumulative sums across all three rows.
            Assert.Equal(200.0, summary.CumulativeTotal["LiquidFuel"]);
            Assert.Equal(10.0, summary.CumulativeTotal["Oxidizer"]);
        }

        [Fact]
        public void FormatCumulativeTotal_EmptyOrNull_IsEmpty()
        {
            // No "(none)" placeholder: an empty total is "" and the callers drop the clause.
            Assert.Equal("", LogisticsDeliveryPresentation.FormatCumulativeTotal(null));
            Assert.Equal("", LogisticsDeliveryPresentation.FormatCumulativeTotal(new Dictionary<string, double>()));
        }

        // Cumulative total is sorted by resource key (ordinal) for stable display order.
        [Fact]
        public void FormatCumulativeTotal_MultiResource_OrdinalSortedStableOrder()
        {
            var total = new Dictionary<string, double> { { "Oxidizer", 10.0 }, { "LiquidFuel", 100.0 } };
            Assert.Equal("100.0 LiquidFuel, 10.0 Oxidizer",
                LogisticsDeliveryPresentation.FormatCumulativeTotal(total));
        }

        [Fact]
        public void FormatCumulativeTotal_IsInvariantNoSeparator()
        {
            CultureInfo prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var total = new Dictionary<string, double> { { "LiquidFuel", 1240.0 } };
                string s = LogisticsDeliveryPresentation.FormatCumulativeTotal(total);
                Assert.Equal("1240.0 LiquidFuel", s);
                Assert.DoesNotContain(",", s);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        // ------------------------------------------------------------------
        // L3: FormatWouldDeliver (Candidates section "Would deliver" cell)
        // ------------------------------------------------------------------

        // Resources only: "<key> <F1 amount>" comma-joined, InvariantCulture.
        [Fact]
        public void FormatWouldDeliver_ResourcesOnly_FormatsF1Invariant()
        {
            var resources = new Dictionary<string, double> { { "LiquidFuel", 150.0 } };
            Assert.Equal("150.0 LiquidFuel",
                LogisticsDeliveryPresentation.FormatWouldDeliver(resources, null));
        }

        // Inventory only: "<N> inventory item(s)" with no resource clause.
        [Fact]
        public void FormatWouldDeliver_InventoryOnly_CountsItems()
        {
            var inventory = new List<InventoryPayloadItem>
            {
                new InventoryPayloadItem(),
                new InventoryPayloadItem(),
            };
            Assert.Equal("2 inventory item(s)",
                LogisticsDeliveryPresentation.FormatWouldDeliver(null, inventory));
        }

        // Both resources and inventory: comma-joined, resources first.
        [Fact]
        public void FormatWouldDeliver_ResourcesAndInventory_CommaJoined()
        {
            var resources = new Dictionary<string, double> { { "Ore", 40.0 } };
            var inventory = new List<InventoryPayloadItem> { new InventoryPayloadItem() };
            Assert.Equal("40.0 Ore, 1 inventory item(s)",
                LogisticsDeliveryPresentation.FormatWouldDeliver(resources, inventory));
        }

        // Empty / null both ways falls back to "(nothing)" (never a blank cell).
        [Fact]
        public void FormatWouldDeliver_NullBoth_IsNothing()
        {
            Assert.Equal("(nothing)",
                LogisticsDeliveryPresentation.FormatWouldDeliver(null, null));
        }

        // Empty (non-null) collections also fall back to "(nothing)".
        [Fact]
        public void FormatWouldDeliver_EmptyBoth_IsNothing()
        {
            Assert.Equal("(nothing)",
                LogisticsDeliveryPresentation.FormatWouldDeliver(
                    new Dictionary<string, double>(), new List<InventoryPayloadItem>()));
        }

        // ------------------------------------------------------------------
        // H4: FormatDestinationDisplay + FormatEndpointCoords
        // ------------------------------------------------------------------

        // Resolved: the supplied vessel name is returned verbatim, coords ignored.
        [Fact]
        public void FormatDestinationDisplay_Resolved_ReturnsVesselName()
        {
            var ep = new RouteEndpoint
            {
                BodyName = "Mun",
                IsSurface = false,
                Latitude = 12.34,
                Longitude = 56.78
            };
            Assert.Equal("Munar Station",
                LogisticsDeliveryPresentation.FormatDestinationDisplay("Munar Station", ep));
        }

        // Fallback (null and empty name): the coords string, equal to FormatEndpointCoords.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void FormatDestinationDisplay_Unresolved_FallsBackToCoords(string name)
        {
            var ep = new RouteEndpoint
            {
                BodyName = "Mun",
                IsSurface = true,
                Latitude = 12.34,
                Longitude = 56.78
            };
            string expected = LogisticsDeliveryPresentation.FormatEndpointCoords(ep);
            Assert.Equal(expected, LogisticsDeliveryPresentation.FormatDestinationDisplay(name, ep));
            Assert.Equal("Mun (surface) 12.34,56.78", expected);
        }

        // Orbit situation label and F2 + InvariantCulture coords.
        [Fact]
        public void FormatEndpointCoords_OrbitSituation_InvariantF2()
        {
            CultureInfo prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var ep = new RouteEndpoint
                {
                    BodyName = "Kerbin",
                    IsSurface = false,
                    Latitude = -0.5,
                    Longitude = 100.125
                };
                string s = LogisticsDeliveryPresentation.FormatEndpointCoords(ep);
                Assert.Equal("Kerbin (orbit) -0.50,100.13", s);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        // Empty body name plus an unresolved vessel: dash.
        [Fact]
        public void FormatDestinationDisplay_EmptyBodyAndNoName_ReturnsDash()
        {
            var ep = new RouteEndpoint { BodyName = null };
            Assert.Equal("-", LogisticsDeliveryPresentation.FormatDestinationDisplay(null, ep));
            Assert.Equal("-", LogisticsDeliveryPresentation.FormatEndpointCoords(ep));
        }
    }
}
