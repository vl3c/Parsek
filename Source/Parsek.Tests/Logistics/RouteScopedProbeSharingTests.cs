using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// The multi-stop gates share one probe per destination so a route's combined manifest is
    /// checked against one set of tanks. Once deliveries are scoped to each endpoint's own
    /// parts (ROUTE-DELIVERY-INTO-DOCKED-VISITOR), "one destination" is a vessel pid PLUS a
    /// part scope: a station and a lander docked together after both were recorded resolve to
    /// one composite pid but are written through disjoint part sets. Sharing the first stop's
    /// probe by pid alone let the gate plan the lander's cargo against the station's tanks,
    /// pass, debit the origin for the full manifest, and then drop what the lander's own tanks
    /// could not take - every cycle.
    ///
    /// <para>Driven through the production pieces: <see cref="EndpointScopedCache{TProbe}"/>
    /// (what the destination gate and the Logistics window use) feeding
    /// <see cref="RouteDestinationCapacityCheck.HasCapacityForAllStops"/>, and
    /// <see cref="RoutePickupSourceGate"/>'s grouping for the pickup side.</para>
    /// </summary>
    public class RouteScopedProbeSharingTests
    {
        private const uint CompositePid = 777u;

        private sealed class FakeProbe : IDeliveryCapacityProbe
        {
            public readonly Dictionary<string, double> Free = new Dictionary<string, double>();

            public double ProbeResourceFreeCapacity(string resourceName)
                => resourceName != null && Free.TryGetValue(resourceName, out double v) ? v : 0.0;

            public InventorySlotAddress ProbeFirstEmptyInventorySlot() => InventorySlotAddress.None;
            public void ConsumeInventorySlot(InventorySlotAddress address) { }
            public int ProbeInventoryStackableQuantity(InventoryPayloadItem item) => 1;
            public int ProbeInventoryUnitsThatFit(InventoryPayloadItem item, int requestedUnits) => 0;
            public void ConsumeInventoryCapacity(InventoryPayloadItem item, int units) { }
        }

        private static RouteStop Delivery(double liquidFuel)
        {
            return new RouteStop
            {
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", liquidFuel } },
            };
        }

        // Station S owns parts 0-2 of the composite; lander L owns parts 3-5.
        private static EndpointPartScope StationScope() =>
            new EndpointPartScope(new[] { true, true, true, false, false, false }, true, 1);

        private static EndpointPartScope LanderScope() =>
            new EndpointPartScope(new[] { false, false, false, true, true, true }, true, 1);

        /// <summary>The destination gate's per-stop lookup shape: resolve the stop (both stops
        /// here resolve to the one composite pid), build that stop's scope, share a probe per
        /// (pid, scope).</summary>
        private static bool Gate(Route route, EndpointPartScope[] scopeByStop,
            Func<EndpointPartScope, FakeProbe> probeOver, out int fullStop, out int probesBuilt)
        {
            var cache = new EndpointScopedCache<IDeliveryCapacityProbe>();
            int built = 0;
            bool ok = RouteDestinationCapacityCheck.HasCapacityForAllStops(
                route,
                stopIndex =>
                {
                    EndpointPartScope scope = scopeByStop[stopIndex];
                    return cache.GetOrAdd(CompositePid, scope, () =>
                    {
                        built++;
                        return probeOver(scope);
                    });
                },
                out _, out fullStop);
            probesBuilt = built;
            return ok;
        }

        // catches: the BLOCKING lost-cargo path - stop 1 (the lander) reusing stop 0's
        // station-scoped probe. The station has room for both manifests, the lander has
        // none; the gate must refuse, because the lander's writer fills only the lander.
        [Fact]
        public void TwoStopsOnOneComposite_DifferentScopes_GateMatchesEachWriter()
        {
            var route = new Route { Id = "r", Stops = new List<RouteStop> { Delivery(50.0), Delivery(50.0) } };
            var station = StationScope();
            var lander = LanderScope();

            bool ok = Gate(route, new[] { station, lander },
                scope => EndpointPartScope.KeyOf(scope) == EndpointPartScope.KeyOf(station)
                    ? new FakeProbe { Free = { { "LiquidFuel", 100.0 } } }
                    : new FakeProbe { Free = { { "LiquidFuel", 0.0 } } },
                out int fullStop, out int built);

            Assert.False(ok);
            Assert.Equal(1, fullStop);
            Assert.Equal(2, built);
        }

        // The mirror: the lander has room, so the cycle passes with each stop checked
        // against its own parts.
        [Fact]
        public void TwoStopsOnOneComposite_DifferentScopes_EachFits_Passes()
        {
            var route = new Route { Id = "r", Stops = new List<RouteStop> { Delivery(50.0), Delivery(50.0) } };
            var station = StationScope();

            bool ok = Gate(route, new[] { station, LanderScope() },
                scope => new FakeProbe { Free = { { "LiquidFuel", 60.0 } } },
                out _, out int built);

            Assert.True(ok);
            Assert.Equal(2, built);
        }

        // catches: losing the shared-capacity accounting for two windows to the SAME
        // endpoint (same scope built twice) - the combined manifest must be checked against
        // one tank, the hole the shared probe exists to close.
        [Fact]
        public void TwoStopsSameScope_ShareOneProbe_CombinedManifestRefused()
        {
            var route = new Route { Id = "r", Stops = new List<RouteStop> { Delivery(60.0), Delivery(60.0) } };

            bool ok = Gate(route, new[] { StationScope(), StationScope() },
                scope => new FakeProbe { Free = { { "LiquidFuel", 100.0 } } },
                out int fullStop, out int built);

            Assert.False(ok);
            Assert.Equal(1, fullStop);
            Assert.Equal(1, built);
        }

        // An undocked destination (null scope = whole vessel) keeps the old per-pid sharing.
        [Fact]
        public void WholeVesselStops_ShareOneProbe()
        {
            var route = new Route { Id = "r", Stops = new List<RouteStop> { Delivery(60.0), Delivery(60.0) } };

            bool ok = Gate(route, new EndpointPartScope[] { null, null },
                scope => new FakeProbe { Free = { { "LiquidFuel", 100.0 } } },
                out _, out int built);

            Assert.False(ok);
            Assert.Equal(1, built);
        }

        [Fact]
        public void KeyOf_IdentifiesThePartSet()
        {
            Assert.Equal("whole", EndpointPartScope.KeyOf(null));
            Assert.Equal(EndpointPartScope.KeyOf(StationScope()), EndpointPartScope.KeyOf(StationScope()));
            Assert.NotEqual(EndpointPartScope.KeyOf(StationScope()), EndpointPartScope.KeyOf(LanderScope()));
            Assert.NotEqual(
                EndpointPartScope.KeyOf(new EndpointPartScope(new[] { true, false }, true, 1)),
                EndpointPartScope.KeyOf(new EndpointPartScope(new[] { true, false }, false, 1)));
            Assert.NotEqual(
                EndpointScopedCache<object>.KeyFor(CompositePid, StationScope()),
                EndpointScopedCache<object>.KeyFor(CompositePid, LanderScope()));
            Assert.NotEqual(
                EndpointScopedCache<object>.KeyFor(1u, null),
                EndpointScopedCache<object>.KeyFor(2u, null));
        }

        // ---- pickup side ------------------------------------------------------

        private static RouteStop Pickup(uint endpointPid, double dockUT, double liquidFuel)
        {
            return new RouteStop
            {
                Endpoint = new RouteEndpoint { VesselPersistentId = endpointPid },
                PickupManifest = new Dictionary<string, double> { { "LiquidFuel", liquidFuel } },
                RecordedDockUT = dockUT,
            };
        }

        // catches: two pickup sources docked into one composite summed against the FIRST
        // source's readers. Depot D holds 100, depot E (its own parts) holds 0; each window
        // picks up 50, so E's window cannot be served and the gate must say so.
        [Fact]
        public void PickupSourcesOnOneComposite_DifferentScopes_AreSeparateSources()
        {
            var route = new Route
            {
                Id = "r",
                IsKscOrigin = false,
                Stops = new List<RouteStop> { Pickup(11u, 10.0, 50.0), Pickup(22u, 20.0, 50.0) },
            };
            RoutePickupSourceGate.PickupSourceResolution Resolve(RouteEndpoint endpoint)
            {
                bool depotD = endpoint.VesselPersistentId == 11u;
                double held = depotD ? 100.0 : 0.0;
                return RoutePickupSourceGate.PickupSourceResolution.Ok(
                        CompositePid, depotD ? "D" : "E", _ => held, _ => 0)
                    .WithPartScopeKey(depotD ? "loaded:0,1,2" : "loaded:3,4,5");
            }

            Assert.True(RoutePickupSourceGate.TryBuildSourceGroups(
                route, Resolve, out List<RoutePickupSourceGate.PickupSourceGroup> groups, out _));
            Assert.Equal(2, groups.Count);

            RoutePickupSourceGate.GateResult result = RoutePickupSourceGate.Evaluate(groups);
            Assert.False(result.Covered);
            Assert.Equal("E", result.ShortSourceName);
        }

        // Same pid and same scope still sum against one source (the under-gate guard).
        [Fact]
        public void PickupWindowsSameScope_StillSum()
        {
            var route = new Route
            {
                Id = "r",
                IsKscOrigin = false,
                Stops = new List<RouteStop> { Pickup(11u, 10.0, 60.0), Pickup(11u, 20.0, 60.0) },
            };
            RoutePickupSourceGate.PickupSourceResolution Resolve(RouteEndpoint endpoint) =>
                RoutePickupSourceGate.PickupSourceResolution.Ok(CompositePid, "D", _ => 100.0, _ => 0)
                    .WithPartScopeKey("loaded:0,1,2");

            Assert.True(RoutePickupSourceGate.TryBuildSourceGroups(
                route, Resolve, out List<RoutePickupSourceGate.PickupSourceGroup> groups, out _));
            Assert.Single(groups);
            Assert.False(RoutePickupSourceGate.Evaluate(groups).Covered);
        }
    }
}
