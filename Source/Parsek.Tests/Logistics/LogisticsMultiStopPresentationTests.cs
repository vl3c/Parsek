using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// GUI-P20: pins the multi-stop helpers in <see cref="LogisticsDeliveryPresentation"/>
    /// that back the Logistics window's "Delivers per cycle", Destination, DestinationFull
    /// capacity and "Re-scan for endpoint" cells. Each helper's single-stop path must
    /// produce exactly the text the window drew before multi-stop support (the cells read
    /// <c>route.Stops[0]</c> only), so every single-stop case below is asserted against
    /// the pre-existing per-stop formatter it used to call.
    /// </summary>
    public class LogisticsMultiStopPresentationTests
    {
        private static RouteEndpoint Surface(string body, double lat, double lon)
        {
            return new RouteEndpoint { BodyName = body, Latitude = lat, Longitude = lon, IsSurface = true };
        }

        private static RouteEndpoint Orbit(string body)
        {
            return new RouteEndpoint { BodyName = body, IsSurface = false };
        }

        private static RouteStop Delivery(RouteEndpoint ep, params (string key, double amount)[] resources)
        {
            var manifest = new Dictionary<string, double>();
            foreach (var r in resources) manifest[r.key] = r.amount;
            return new RouteStop
            {
                Endpoint = ep,
                DeliveryManifest = manifest,
                InventoryDeliveryManifest = new List<InventoryPayloadItem>(),
            };
        }

        private static RouteStop Pickup(RouteEndpoint ep, string key, double amount)
        {
            return new RouteStop
            {
                Endpoint = ep,
                DeliveryManifest = new Dictionary<string, double>(),
                InventoryDeliveryManifest = new List<InventoryPayloadItem>(),
                PickupManifest = new Dictionary<string, double> { { key, amount } },
            };
        }

        // ------------------------------------------------------------------
        // Delivers per cycle
        // ------------------------------------------------------------------

        [Fact]
        public void DeliveryPerCycle_SingleStop_IsByteIdenticalToTheStopManifest()
        {
            RouteStop stop = Delivery(Surface("Mun", 1, 2), ("LiquidFuel", 282.0), ("Oxidizer", 345.0));
            stop.InventoryDeliveryManifest.Add(new InventoryPayloadItem());
            var stops = new List<RouteStop> { stop };

            string text = LogisticsDeliveryPresentation.FormatRouteDeliveryPerCycle(stops);

            Assert.Equal(
                LogisticsDeliveryPresentation.FormatWouldDeliver(stop.DeliveryManifest, stop.InventoryDeliveryManifest),
                text);
            Assert.Equal("282.0 LiquidFuel, 345.0 Oxidizer, 1 inventory item(s)", text);
        }

        [Fact]
        public void DeliveryPerCycle_SingleStopEmptyOrNull_KeepsThePreviousText()
        {
            Assert.Equal("(nothing)", LogisticsDeliveryPresentation.FormatRouteDeliveryPerCycle(
                new List<RouteStop> { new RouteStop() }));
            Assert.Equal("-", LogisticsDeliveryPresentation.FormatRouteDeliveryPerCycle(
                new List<RouteStop> { null }));
            Assert.Equal("-", LogisticsDeliveryPresentation.FormatRouteDeliveryPerCycle(new List<RouteStop>()));
            Assert.Equal("-", LogisticsDeliveryPresentation.FormatRouteDeliveryPerCycle(null));
        }

        [Fact]
        public void DeliveryPerCycle_MultiStop_SumsAResourceDeliveredToSeveralStops()
        {
            var stops = new List<RouteStop>
            {
                Delivery(Surface("Mun", 1, 2), ("LiquidFuel", 100.0), ("Oxidizer", 120.0)),
                Delivery(Surface("Mun", 3, 4), ("LiquidFuel", 50.0), ("MonoPropellant", 10.0)),
            };
            stops[1].InventoryDeliveryManifest.Add(new InventoryPayloadItem());

            Assert.Equal(
                "150.0 LiquidFuel, 120.0 Oxidizer, 10.0 MonoPropellant, 1 inventory item(s) across 2 stops",
                LogisticsDeliveryPresentation.FormatRouteDeliveryPerCycle(stops));
        }

        // The rover-relay shape (RVR-7): stop 1 is a pure pickup at B, stop 2 delivers
        // at A. Before GUI-P20 the cell read stop 1 only and showed "(nothing)".
        [Fact]
        public void DeliveryPerCycle_PickupThenDelivery_ShowsTheDeliveryNotNothing()
        {
            var stops = new List<RouteStop>
            {
                Pickup(Surface("Kerbin", 0, 0), "LiquidFuel", 200.0),
                Delivery(Surface("Kerbin", 0.1, 0.1), ("LiquidFuel", 154.4)),
            };

            Assert.Equal("154.4 LiquidFuel",
                LogisticsDeliveryPresentation.FormatRouteDeliveryPerCycle(stops));
        }

        [Fact]
        public void DeliveryPerCycle_MultiStopNoDeliveries_IsNothing()
        {
            var stops = new List<RouteStop>
            {
                Pickup(Surface("Kerbin", 0, 0), "Ore", 10.0),
                Pickup(Surface("Kerbin", 1, 1), "Ore", 10.0),
            };
            Assert.Equal("(nothing)", LogisticsDeliveryPresentation.FormatRouteDeliveryPerCycle(stops));
            Assert.Equal("-", LogisticsDeliveryPresentation.FormatRouteDeliveryPerCycle(
                new List<RouteStop> { null, null }));
        }

        // ------------------------------------------------------------------
        // Destination
        // ------------------------------------------------------------------

        [Fact]
        public void Destination_SingleStop_NamesStopZeroAndKeepsTheTextUnchanged()
        {
            var stops = new List<RouteStop> { Pickup(Surface("Mun", 1, 2), "Ore", 1.0) };
            Assert.Equal(0, LogisticsDeliveryPresentation.ResolveDestinationStopIndex(stops));
            Assert.Equal(-1, LogisticsDeliveryPresentation.ResolveDestinationStopIndex(new List<RouteStop> { null }));
            Assert.Equal("Mun Base", LogisticsDeliveryPresentation.FormatMultiStopDestination("Mun Base", 1));
            Assert.Equal("Mun (surface) 1.00,2.00",
                LogisticsDeliveryPresentation.FormatMultiStopDestination("Mun (surface) 1.00,2.00", 1));
        }

        [Fact]
        public void Destination_MultiStop_AppendsTheOtherStopCount()
        {
            Assert.Equal("Mun Base (+1 stop)", LogisticsDeliveryPresentation.FormatMultiStopDestination("Mun Base", 2));
            Assert.Equal("Mun Base (+2 stops)", LogisticsDeliveryPresentation.FormatMultiStopDestination("Mun Base", 3));
            Assert.Equal("- (+1 stop)", LogisticsDeliveryPresentation.FormatMultiStopDestination(null, 2));
        }

        [Fact]
        public void Destination_MultiStop_NamesTheFirstStopThatReceivesCargo()
        {
            var relay = new List<RouteStop>
            {
                Pickup(Surface("Kerbin", 0, 0), "LiquidFuel", 200.0),
                Delivery(Surface("Kerbin", 0.1, 0.1), ("LiquidFuel", 154.4)),
            };
            Assert.Equal(1, LogisticsDeliveryPresentation.ResolveDestinationStopIndex(relay));

            var twoDeliveries = new List<RouteStop>
            {
                null,
                Delivery(Surface("Mun", 1, 2), ("LiquidFuel", 1.0)),
                Delivery(Surface("Mun", 3, 4), ("LiquidFuel", 1.0)),
            };
            Assert.Equal(1, LogisticsDeliveryPresentation.ResolveDestinationStopIndex(twoDeliveries));

            var noDeliveries = new List<RouteStop>
            {
                null,
                Pickup(Surface("Mun", 1, 2), "Ore", 1.0),
                Pickup(Surface("Mun", 3, 4), "Ore", 1.0),
            };
            Assert.Equal(1, LogisticsDeliveryPresentation.ResolveDestinationStopIndex(noDeliveries));
            Assert.Equal(-1, LogisticsDeliveryPresentation.ResolveDestinationStopIndex(new List<RouteStop> { null, null }));
            Assert.Equal(2, LogisticsDeliveryPresentation.CountStops(twoDeliveries));
        }

        [Fact]
        public void Destination_StopListTooltip_IsOneLineInVisitOrderWithRoles()
        {
            RouteStop mixed = Delivery(Surface("Mun", 1, 2), ("LiquidFuel", 1.0));
            mixed.PickupManifest = new Dictionary<string, double> { { "Ore", 5.0 } };
            var stops = new List<RouteStop>
            {
                Pickup(Surface("Kerbin", 0, 0), "LiquidFuel", 1.0),
                Delivery(Surface("Kerbin", 1, 1), ("LiquidFuel", 1.0)),
                mixed,
                new RouteStop(),
            };
            var roles = new List<string>();
            foreach (RouteStop s in stops) roles.Add(LogisticsDeliveryPresentation.StopRoleLabel(s));

            string tooltip = LogisticsDeliveryPresentation.FormatStopListTooltip(
                new[] { "B", "A", "Mun (surface) 1.00,2.00", null }, roles, LogisticsBudget);

            Assert.Equal(
                "Stops in order: 1. B (pickup), 2. A (delivery), 3. Mun (surface) 1.00,2.00 (pickup + delivery), 4. - (no cargo)",
                tooltip);
            Assert.DoesNotContain("\n", tooltip);
            Assert.Equal(string.Empty,
                LogisticsDeliveryPresentation.FormatStopListTooltip(new[] { "A" }, null, LogisticsBudget));
        }

        private static int LogisticsBudget =>
            TooltipEchoBox.BudgetChars(LogisticsWindowUI.DefaultWindowWidth, TooltipEchoBox.SingleLine);

        // Two stops always fit the strip, so the cap leaves the text exactly as the
        // uncapped form.
        [Fact]
        public void Destination_StopListTooltip_TwoStopsAreUnchangedByTheCap()
        {
            var texts = new[] { "Kerbin (surface) -89.99,-179.99", "Kerbin (surface) 89.99,179.99" };
            var roles = new[] { "pickup + delivery", "pickup + delivery" };
            string capped = LogisticsDeliveryPresentation.FormatStopListTooltip(texts, roles, LogisticsBudget);
            Assert.Equal(LogisticsDeliveryPresentation.FormatStopListTooltip(texts, roles, 0), capped);
            Assert.Equal(
                "Stops in order: 1. Kerbin (surface) -89.99,-179.99 (pickup + delivery), "
                + "2. Kerbin (surface) 89.99,179.99 (pickup + delivery)",
                capped);
        }

        [Fact]
        public void Destination_StopListTooltip_OverBudget_KeepsLeadingStopsAndCountsTheRest()
        {
            var texts = new[] { "Alpha", "Bravo", "Charlie", "Delta" };
            var roles = new[] { "pickup", "delivery", "delivery", "delivery" };
            // Full form is 94 chars; 60 holds "1. Alpha (pickup), 2. Bravo (delivery)" plus the suffix.
            string capped = LogisticsDeliveryPresentation.FormatStopListTooltip(texts, roles, 60);
            Assert.Equal("Stops in order: 1. Alpha (pickup), ... +3 more", capped);
            Assert.True(capped.Length <= 60);

            string wider = LogisticsDeliveryPresentation.FormatStopListTooltip(texts, roles, 72);
            Assert.Equal("Stops in order: 1. Alpha (pickup), 2. Bravo (delivery), ... +2 more", wider);

            // Not even one stop fits beside the suffix: the bare count.
            Assert.Equal("Stops in order: ... +4 more",
                LogisticsDeliveryPresentation.FormatStopListTooltip(texts, roles, 30));
        }

        // ------------------------------------------------------------------
        // DestinationFull capacity line
        // ------------------------------------------------------------------

        [Fact]
        public void CapacityRequested_SingleStop_IsThatStopsManifest()
        {
            RouteStop stop = Delivery(Surface("Mun", 1, 2), ("LiquidFuel", 150.0), ("Oxidizer", 183.0));
            Dictionary<string, double> combined = LogisticsDeliveryPresentation.CombineRequestedForFullStop(
                new List<RouteStop> { stop }, 0, i => true);
            Assert.Equal(stop.DeliveryManifest, combined);
        }

        [Fact]
        public void CapacityRequested_MultiStop_CountsTheFullStopAndEarlierSameVesselStopsOnly()
        {
            var stops = new List<RouteStop>
            {
                Delivery(Surface("Mun", 1, 2), ("LiquidFuel", 100.0)),          // same vessel as stop 3
                Delivery(Surface("Mun", 5, 6), ("LiquidFuel", 40.0)),           // another vessel
                Delivery(Surface("Mun", 1, 2), ("LiquidFuel", 50.0), ("Ore", 2.0)), // the full stop
                Delivery(Surface("Mun", 1, 2), ("LiquidFuel", 999.0)),          // after the full stop
            };
            Dictionary<string, double> combined = LogisticsDeliveryPresentation.CombineRequestedForFullStop(
                stops, 2, i => i == 0 || i == 3);

            Assert.Equal(2, combined.Count);
            Assert.Equal(150.0, combined["LiquidFuel"]);
            Assert.Equal(2.0, combined["Ore"]);

            Assert.Null(LogisticsDeliveryPresentation.CombineRequestedForFullStop(stops, -1, null));
            Assert.Null(LogisticsDeliveryPresentation.CombineRequestedForFullStop(stops, 4, null));
            Assert.Null(LogisticsDeliveryPresentation.CombineRequestedForFullStop(
                new List<RouteStop> { Pickup(Surface("Mun", 1, 2), "Ore", 1.0) }, 0, null));
        }

        // ------------------------------------------------------------------
        // Re-scan for endpoint
        // ------------------------------------------------------------------

        [Fact]
        public void Rescan_SingleStop_MatchesThePerEndpointDecisionAndReason()
        {
            RouteEndpoint surface = Surface("Mun", 1, 2);
            RouteEndpoint orbit = Orbit("Mun");
            foreach (RouteEndpoint ep in new[] { surface, orbit, new RouteEndpoint { IsSurface = true } })
            {
                var stops = new List<RouteStop> { new RouteStop { Endpoint = ep } };
                Assert.Equal(
                    LogisticsDeliveryPresentation.ShouldOfferEndpointRescan(RouteStatus.EndpointLost, ep),
                    LogisticsDeliveryPresentation.ShouldOfferRouteEndpointRescan(RouteStatus.EndpointLost, stops));
                Assert.Equal(
                    LogisticsDeliveryPresentation.RescanIneligibleReason(ep),
                    LogisticsDeliveryPresentation.RouteRescanIneligibleReason(stops));
            }
        }

        [Fact]
        public void Rescan_MultiStop_OfferedWhenAnyStopIsARecoverableSurfaceEndpoint()
        {
            var mixed = new List<RouteStop>
            {
                new RouteStop { Endpoint = Orbit("Mun") },
                new RouteStop { Endpoint = Surface("Mun", 1, 2) },
            };
            Assert.True(LogisticsDeliveryPresentation.ShouldOfferRouteEndpointRescan(RouteStatus.EndpointLost, mixed));
            Assert.False(LogisticsDeliveryPresentation.ShouldOfferRouteEndpointRescan(RouteStatus.Active, mixed));

            var orbital = new List<RouteStop>
            {
                null,
                new RouteStop { Endpoint = Orbit("Mun") },
                new RouteStop { Endpoint = Orbit("Minmus") },
            };
            Assert.False(LogisticsDeliveryPresentation.ShouldOfferRouteEndpointRescan(RouteStatus.EndpointLost, orbital));
            Assert.Equal(LogisticsDeliveryPresentation.RescanIneligibleReason(Orbit("Mun")),
                LogisticsDeliveryPresentation.RouteRescanIneligibleReason(orbital));
            Assert.Null(LogisticsDeliveryPresentation.RouteRescanIneligibleReason(new List<RouteStop> { null }));
        }

        [Fact]
        public void Rescan_Outcome_NamesEveryStopAndGatesTheRetryClearOnAllResolved()
        {
            var partial = new List<LogisticsDeliveryPresentation.RescanStopResult>
            {
                new LogisticsDeliveryPresentation.RescanStopResult(0, true, "Mun Base", null),
                new LogisticsDeliveryPresentation.RescanStopResult(2, false, null, "no-vessel-near"),
            };
            Assert.Equal("stops=2 resolved=1/2 [1:'Mun Base' 3:unresolved('no-vessel-near')]",
                LogisticsDeliveryPresentation.FormatRescanOutcome(partial));
            Assert.False(LogisticsDeliveryPresentation.AllStopsResolved(partial));

            var all = new List<LogisticsDeliveryPresentation.RescanStopResult>
            {
                new LogisticsDeliveryPresentation.RescanStopResult(0, true, "A", null),
                new LogisticsDeliveryPresentation.RescanStopResult(1, true, "B", null),
            };
            Assert.True(LogisticsDeliveryPresentation.AllStopsResolved(all));
            Assert.False(LogisticsDeliveryPresentation.AllStopsResolved(
                new List<LogisticsDeliveryPresentation.RescanStopResult>()));
            Assert.Equal("stops=0 resolved=0/0 []", LogisticsDeliveryPresentation.FormatRescanOutcome(null));
        }
    }
}
