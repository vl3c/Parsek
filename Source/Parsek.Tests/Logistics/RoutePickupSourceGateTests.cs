using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// M4b Phase B1 (plan D10 / OQ5): pins the pure all-or-nothing N-SOURCE
    /// dispatch gate derived from a route's pickup <see cref="RouteStop"/>s. The
    /// gate is exercised through <see cref="RoutePickupSourceGate"/> directly with
    /// a hand-rolled resolver + per-vessel stored-amount readers, so the tests stay
    /// xUnit-only (the production wiring in
    /// <see cref="LiveRouteRuntimeEnvironment.OriginHasCargo"/> resolves endpoints
    /// to live KSP vessels - that path is the in-game B4 surface).
    ///
    /// <para>Each test states the regression it guards. The headline invariants:
    /// all-or-nothing across N distinct sources (first-short by dock UT names the
    /// SOURCE), same-pid windows SUM against the one tank (the under-gate guard),
    /// and the docked-origin + pickup-source provenances are independent (no
    /// double-count).</para>
    /// </summary>
    public class RoutePickupSourceGateTests
    {
        // ---- fixture helpers -------------------------------------------------

        private static RouteStop PickupStop(uint endpointPid, double dockUT,
            Dictionary<string, double> pickup,
            List<InventoryPayloadItem> inventoryPickup = null)
        {
            return new RouteStop
            {
                Endpoint = new RouteEndpoint { VesselPersistentId = endpointPid },
                PickupManifest = pickup,
                InventoryPickupManifest = inventoryPickup,
                RecordedDockUT = dockUT,
            };
        }

        private static RouteStop DeliveryStop(uint endpointPid, double dockUT,
            Dictionary<string, double> delivery)
        {
            return new RouteStop
            {
                Endpoint = new RouteEndpoint { VesselPersistentId = endpointPid },
                DeliveryManifest = delivery,
                RecordedDockUT = dockUT,
            };
        }

        private static Route RouteWithStops(params RouteStop[] stops)
        {
            return new Route
            {
                Id = "route-b1",
                IsKscOrigin = false,
                Stops = new List<RouteStop>(stops),
            };
        }

        /// <summary>
        /// Hand-rolled resolver: maps each endpoint PID to a stored-amount map +
        /// vessel name. A PID absent from the map resolves as a MISS (mirrors a
        /// pickup-source vessel that has moved / recovered / been destroyed).
        /// Returns the SAME readers for the same pid so the same-pid SUM check
        /// reads one tank.
        /// </summary>
        private sealed class FakeResolver
        {
            // endpoint-pid -> resolved (pid, name, stored-resources, stored-inventory)
            public Dictionary<uint, (uint pid, string name,
                Dictionary<string, double> res, Dictionary<string, int> inv)> Resolved
                = new Dictionary<uint, (uint, string, Dictionary<string, double>, Dictionary<string, int>)>();

            public RoutePickupSourceGate.PickupSourceResolution Resolve(RouteEndpoint endpoint)
            {
                if (!Resolved.TryGetValue(endpoint.VesselPersistentId, out var r))
                    return RoutePickupSourceGate.PickupSourceResolution.Miss("pid-miss");

                var res = r.res ?? new Dictionary<string, double>();
                var inv = r.inv ?? new Dictionary<string, int>();
                return RoutePickupSourceGate.PickupSourceResolution.Ok(
                    r.pid, r.name,
                    name => res.TryGetValue(name, out double v) ? v : 0.0,
                    hash => inv.TryGetValue(hash, out int q) ? q : 0);
            }
        }

        private static RoutePickupSourceGate.GateResult Run(Route route, FakeResolver resolver)
        {
            bool built = RoutePickupSourceGate.TryBuildSourceGroups(
                route, resolver.Resolve, out var groups, out string unresolved);
            Assert.True(built, "expected all pickup endpoints to resolve; unresolved=" + unresolved);
            return RoutePickupSourceGate.Evaluate(groups);
        }

        // ---- 2-source all-or-nothing ----------------------------------------

        // catches: a 2-source route dispatching when BOTH sources cover (the gate
        // must pass all-or-nothing only when every source is stocked).
        [Fact]
        public void TwoSources_BothCover_Eligible()
        {
            // Source A (pid 10) supplies 100 Ore at dock UT 100; source B (pid 20)
            // supplies 200 Ore at dock UT 200 -> consolidation at a later station.
            var route = RouteWithStops(
                PickupStop(10u, 100.0, new Dictionary<string, double> { { "Ore", 100.0 } }),
                PickupStop(20u, 200.0, new Dictionary<string, double> { { "Ore", 200.0 } }));
            var resolver = new FakeResolver();
            resolver.Resolved[10u] = (10u, "Depot A", new Dictionary<string, double> { { "Ore", 100.0 } }, null);
            resolver.Resolved[20u] = (20u, "Depot B", new Dictionary<string, double> { { "Ore", 500.0 } }, null);

            var result = Run(route, resolver);

            Assert.True(result.Covered);
        }

        // catches: source A short NOT holding the route (all-or-nothing must fail)
        // and the hold naming the wrong source (it must name A, the first short by
        // dock UT).
        [Fact]
        public void TwoSources_AShort_HoldsNamingSourceA()
        {
            var route = RouteWithStops(
                PickupStop(10u, 100.0, new Dictionary<string, double> { { "Ore", 100.0 } }),
                PickupStop(20u, 200.0, new Dictionary<string, double> { { "Ore", 200.0 } }));
            var resolver = new FakeResolver();
            // A holds only 40 (short by 60); B fully stocked.
            resolver.Resolved[10u] = (10u, "Depot A", new Dictionary<string, double> { { "Ore", 40.0 } }, null);
            resolver.Resolved[20u] = (20u, "Depot B", new Dictionary<string, double> { { "Ore", 500.0 } }, null);

            var result = Run(route, resolver);

            Assert.False(result.Covered);
            Assert.Equal(10u, result.ShortSourcePid);
            Assert.Equal("Depot A", result.ShortSourceName);
            Assert.Equal("Ore", result.ShortResource);
            Assert.Equal(60.0, result.Shortfall, 6);
            Assert.Contains("Depot A", result.ShortHoldToken);
        }

        // catches: when ONLY the later source (B) is short, the gate naming the
        // earlier source A (it should name B, the actual short).
        [Fact]
        public void TwoSources_BShort_HoldsNamingSourceB()
        {
            var route = RouteWithStops(
                PickupStop(10u, 100.0, new Dictionary<string, double> { { "Ore", 100.0 } }),
                PickupStop(20u, 200.0, new Dictionary<string, double> { { "Ore", 200.0 } }));
            var resolver = new FakeResolver();
            // A fully stocked; B short by 150.
            resolver.Resolved[10u] = (10u, "Depot A", new Dictionary<string, double> { { "Ore", 100.0 } }, null);
            resolver.Resolved[20u] = (20u, "Depot B", new Dictionary<string, double> { { "Ore", 50.0 } }, null);

            var result = Run(route, resolver);

            Assert.False(result.Covered);
            Assert.Equal(20u, result.ShortSourcePid);
            Assert.Equal("Depot B", result.ShortSourceName);
            Assert.Equal("Ore", result.ShortResource);
            Assert.Equal(150.0, result.Shortfall, 6);
        }

        // catches: when BOTH sources are short, the first-short ordering keying on
        // anything but ascending dock UT (it must name the EARLIER-dock source A
        // even when the stops are presented in reverse UT order in the list).
        [Fact]
        public void TwoSources_BothShort_FirstShortByDockUT_NamesEarliest()
        {
            // Present B (later dock) FIRST in the stop list; the ascending-dock-UT
            // ordering must still name A (dock UT 100) as the first short.
            var route = RouteWithStops(
                PickupStop(20u, 200.0, new Dictionary<string, double> { { "Ore", 200.0 } }),
                PickupStop(10u, 100.0, new Dictionary<string, double> { { "Ore", 100.0 } }));
            var resolver = new FakeResolver();
            resolver.Resolved[10u] = (10u, "Depot A", new Dictionary<string, double> { { "Ore", 0.0 } }, null);
            resolver.Resolved[20u] = (20u, "Depot B", new Dictionary<string, double> { { "Ore", 0.0 } }, null);

            var result = Run(route, resolver);

            Assert.False(result.Covered);
            Assert.Equal(10u, result.ShortSourcePid);
            Assert.Equal("Depot A", result.ShortSourceName);
        }

        // ---- same-pid SUM (the under-gate guard) -----------------------------

        // catches: two pickup windows resolving to the SAME pid being checked
        // INDEPENDENTLY against the full tank (each window sees the whole tank and
        // under-gates). The gate must SUM the windows and check the summed manifest
        // against the one tank.
        [Fact]
        public void SamePid_TwoWindows_SumAgainstOneTank_Short()
        {
            // One station (pid 30) backs two pickup windows: 100 Ore then 200 Ore.
            // The summed need is 300; the tank holds only 250 -> short by 50.
            var route = RouteWithStops(
                PickupStop(30u, 100.0, new Dictionary<string, double> { { "Ore", 100.0 } }),
                PickupStop(30u, 300.0, new Dictionary<string, double> { { "Ore", 200.0 } }));
            var resolver = new FakeResolver();
            resolver.Resolved[30u] = (30u, "Station", new Dictionary<string, double> { { "Ore", 250.0 } }, null);

            var result = Run(route, resolver);

            Assert.False(result.Covered);
            Assert.Equal(30u, result.ShortSourcePid);
            Assert.Equal("Ore", result.ShortResource);
            Assert.Equal(50.0, result.Shortfall, 6); // 300 needed - 250 stored

            // And the SAME tank with 300 covers the summed manifest.
            resolver.Resolved[30u] = (30u, "Station", new Dictionary<string, double> { { "Ore", 300.0 } }, null);
            Assert.True(Run(route, resolver).Covered);
        }

        // catches: same-pid windows producing two SOURCE groups (must collapse to
        // one group keyed on the resolved pid).
        [Fact]
        public void SamePid_TwoWindows_CollapseToOneGroup()
        {
            var route = RouteWithStops(
                PickupStop(30u, 100.0, new Dictionary<string, double> { { "Ore", 50.0 } }),
                PickupStop(30u, 300.0, new Dictionary<string, double> { { "Ore", 50.0 } }));
            var resolver = new FakeResolver();
            resolver.Resolved[30u] = (30u, "Station", new Dictionary<string, double> { { "Ore", 999.0 } }, null);

            bool built = RoutePickupSourceGate.TryBuildSourceGroups(
                route, resolver.Resolve, out var groups, out _);

            Assert.True(built);
            Assert.Single(groups);
            Assert.Equal(100.0, groups[0].SummedResourceManifest["Ore"], 6); // 50 + 50
        }

        // ---- independent provenance (no double-count) ------------------------

        // catches: the pickup-source gate double-counting the docked-origin's
        // CostManifest, or vice versa. The pickup gate must ONLY gate the pickup
        // stops; a pure-delivery stop (the docked-origin's destination) contributes
        // no source. A route with one docked-origin delivery stop + one pickup stop
        // gates the pickup source ONLY against its own pickup manifest.
        [Fact]
        public void DockedOriginPlusPickupSource_DoNotDoubleCount()
        {
            // Stop 0: a DELIVERY stop (the docked-origin destination) - no pickup,
            // contributes no source. Stop 1: a pickup source (pid 40) supplying
            // 100 Ore. The gate sees ONLY the pickup source's 100 Ore, NOT any
            // origin CostManifest.
            var route = RouteWithStops(
                DeliveryStop(50u, 50.0, new Dictionary<string, double> { { "LiquidFuel", 999.0 } }),
                PickupStop(40u, 150.0, new Dictionary<string, double> { { "Ore", 100.0 } }));
            // route.CostManifest (the docked-origin provenance) is gated separately
            // by OriginProvenanceHasCargo; it must NOT leak into the pickup gate.
            route.CostManifest = new Dictionary<string, double> { { "LiquidFuel", 999.0 } };

            var resolver = new FakeResolver();
            resolver.Resolved[40u] = (40u, "Refinery", new Dictionary<string, double> { { "Ore", 100.0 } }, null);
            // pid 50 (the delivery destination) intentionally NOT registered: if the
            // gate wrongly treated the delivery stop as a source it would MISS-fail
            // on pid 50; instead it must skip it entirely.

            var result = Run(route, resolver);

            Assert.True(result.Covered); // refinery covers its 100 Ore; delivery stop is not a source
        }

        // ---- delivery-only byte-behaviour identity ---------------------------

        // catches: a delivery-only / single-origin route (no pickup stops) being
        // gated differently than before B1. With no pickup sources the gate builds
        // ZERO groups and returns Ok - byte-behaviour-identical to the pre-B1
        // OriginHasCargo (which had no pickup gate at all).
        [Fact]
        public void DeliveryOnly_NoPickupSources_ZeroGroups_Eligible()
        {
            var route = RouteWithStops(
                DeliveryStop(50u, 50.0, new Dictionary<string, double> { { "LiquidFuel", 100.0 } }));
            var resolver = new FakeResolver();
            // No pickup endpoints registered at all.

            bool built = RoutePickupSourceGate.TryBuildSourceGroups(
                route, resolver.Resolve, out var groups, out string unresolved);

            Assert.True(built);
            Assert.Empty(groups);
            Assert.Null(unresolved);
            Assert.True(RoutePickupSourceGate.Evaluate(groups).Covered);
        }

        // catches: a null / empty Stops list throwing instead of passing trivially.
        [Fact]
        public void NullAndEmptyStops_PassTrivially()
        {
            var resolver = new FakeResolver();

            bool builtNull = RoutePickupSourceGate.TryBuildSourceGroups(
                new Route { Stops = null }, resolver.Resolve, out var gNull, out _);
            Assert.True(builtNull);
            Assert.Empty(gNull);

            bool builtEmpty = RoutePickupSourceGate.TryBuildSourceGroups(
                RouteWithStops(), resolver.Resolve, out var gEmpty, out _);
            Assert.True(builtEmpty);
            Assert.Empty(gEmpty);
        }

        // ---- unresolved source ----------------------------------------------

        // catches: a pickup source whose endpoint no longer resolves silently
        // passing (it must hold the route with the unresolved reason).
        [Fact]
        public void PickupSourceUnresolved_HoldsWithReason()
        {
            var route = RouteWithStops(
                PickupStop(99u, 100.0, new Dictionary<string, double> { { "Ore", 100.0 } }));
            var resolver = new FakeResolver(); // pid 99 not registered -> miss

            bool built = RoutePickupSourceGate.TryBuildSourceGroups(
                route, resolver.Resolve, out _, out string unresolved);

            Assert.False(built);
            Assert.Equal("pid-miss", unresolved);
        }

        // ---- inventory pickup source ----------------------------------------

        // catches: an inventory pickup source not being gated (a stored-part pickup
        // window that the source no longer holds must hold the route).
        [Fact]
        public void InventoryPickupSource_Short_HoldsNamingSource()
        {
            var inventory = new List<InventoryPayloadItem>
            {
                new InventoryPayloadItem { IdentityHash = "hashA", Quantity = 2 },
            };
            var route = RouteWithStops(
                PickupStop(60u, 100.0, null, inventory));
            var resolver = new FakeResolver();
            // source holds only 1 of the 2 needed.
            resolver.Resolved[60u] = (60u, "Cargo Bay", null,
                new Dictionary<string, int> { { "hashA", 1 } });

            var result = Run(route, resolver);

            Assert.False(result.Covered);
            Assert.Equal(60u, result.ShortSourcePid);
            Assert.True(result.InventoryShort);
            Assert.StartsWith("inventory:", result.ShortResource);
            Assert.Contains("Cargo Bay", result.ShortHoldToken);
        }

        // catches: same-pid inventory windows not summing (two windows each pulling
        // 1 of identity hashA must sum to a need of 2 against the one bay).
        [Fact]
        public void SamePid_InventoryWindows_Sum()
        {
            var inv1 = new List<InventoryPayloadItem> { new InventoryPayloadItem { IdentityHash = "hashA", Quantity = 1 } };
            var inv2 = new List<InventoryPayloadItem> { new InventoryPayloadItem { IdentityHash = "hashA", Quantity = 1 } };
            var route = RouteWithStops(
                PickupStop(70u, 100.0, null, inv1),
                PickupStop(70u, 200.0, null, inv2));
            var resolver = new FakeResolver();
            resolver.Resolved[70u] = (70u, "Bay", null, new Dictionary<string, int> { { "hashA", 1 } });

            var result = Run(route, resolver);

            Assert.False(result.Covered); // need 2 summed, hold 1
            Assert.True(result.InventoryShort);

            // The same bay holding 2 covers the summed need.
            resolver.Resolved[70u] = (70u, "Bay", null, new Dictionary<string, int> { { "hashA", 2 } });
            Assert.True(Run(route, resolver).Covered);
        }
    }
}
