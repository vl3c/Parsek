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

        // ---- B3 escrow reservation builder ----------------------------------

        // catches (B3): the reservation builder NOT keying on the same resolved pid
        // the gate nets on, or NOT summing same-pid windows. The reserve list must
        // carry one entry per resolved pid with the SUMMED per-resource amount, so
        // reserve(pid) == sum-of-per-window-releases(pid).
        [Fact]
        public void Reservations_KeyOnResolvedPid_SumSamePidWindows()
        {
            // Source A (pid 10) one window 100 Ore; source B (pid 20) two same-pid
            // windows 200 + 50 Ore -> summed 250 against pid 20.
            var route = RouteWithStops(
                PickupStop(10u, 100.0, new Dictionary<string, double> { { "Ore", 100.0 } }),
                PickupStop(20u, 200.0, new Dictionary<string, double> { { "Ore", 200.0 } }),
                PickupStop(20u, 300.0, new Dictionary<string, double> { { "Ore", 50.0 } }));
            var resolver = new FakeResolver();
            resolver.Resolved[10u] = (10u, "Depot A", new Dictionary<string, double>(), null);
            resolver.Resolved[20u] = (20u, "Depot B", new Dictionary<string, double>(), null);

            bool built = RoutePickupSourceGate.TryBuildReservations(
                route, resolver.Resolve, out var reservations, out string unresolved);

            Assert.True(built, "unresolved=" + unresolved);
            Assert.Equal(2, reservations.Count);

            var a = reservations.Find(r => r.ResolvedPid == 10u);
            Assert.NotNull(a);
            Assert.Equal(100.0, a.SummedResourceManifest["Ore"], 6);

            var b = reservations.Find(r => r.ResolvedPid == 20u);
            Assert.NotNull(b);
            Assert.Equal(250.0, b.SummedResourceManifest["Ore"], 6); // 200 + 50 summed
        }

        // catches (B3): an inventory-only / delivery-only route building a spurious
        // resource reservation. The resource escrow is the primary B3 deliverable;
        // an inventory-only pickup window contributes NO resource reservation (the
        // inventory escrow is the deferred B3 seam, not wired).
        [Fact]
        public void Reservations_InventoryOnlyAndDeliveryOnly_ReserveNothing()
        {
            var inv = new List<InventoryPayloadItem> { new InventoryPayloadItem { IdentityHash = "hashA", Quantity = 1 } };
            var route = RouteWithStops(
                PickupStop(70u, 100.0, null, inv),                 // inventory-only pickup
                DeliveryStop(80u, 200.0, new Dictionary<string, double> { { "Ore", 50.0 } })); // delivery-only
            var resolver = new FakeResolver();
            resolver.Resolved[70u] = (70u, "Bay", null, new Dictionary<string, int> { { "hashA", 1 } });

            bool built = RoutePickupSourceGate.TryBuildReservations(
                route, resolver.Resolve, out var reservations, out _);

            Assert.True(built);
            Assert.Empty(reservations); // no resource reservation
        }

        // catches (B3): an unresolved pickup source NOT short-circuiting the reserve
        // build - a partial reservation set (some sources reserved, one missing)
        // would leak. The builder must return false on the first unresolved source so
        // the caller reserves NOTHING.
        [Fact]
        public void Reservations_UnresolvedSource_ReturnsFalse_NoPartialSet()
        {
            var route = RouteWithStops(
                PickupStop(10u, 100.0, new Dictionary<string, double> { { "Ore", 100.0 } }),
                PickupStop(99u, 200.0, new Dictionary<string, double> { { "Ore", 200.0 } })); // 99 not in resolver
            var resolver = new FakeResolver();
            resolver.Resolved[10u] = (10u, "Depot A", new Dictionary<string, double>(), null);

            bool built = RoutePickupSourceGate.TryBuildReservations(
                route, resolver.Resolve, out var reservations, out string unresolved);

            Assert.False(built);
            Assert.Equal("pid-miss", unresolved);
        }

        // catches (B3 C1): the un-fired-window filter NOT excluding an already-fired
        // window. The re-establish-on-resume path builds reservations over ONLY the
        // windows whose LastFiredCycleIndex < the resumed cycle index; a window that
        // already debited+released this cycle must be EXCLUDED (re-reserving it would
        // double the hold).
        [Fact]
        public void Reservations_UnfiredWindowFilter_ExcludesAlreadyFiredWindow()
        {
            // Two sources for cycle index 0; window A (pid 10) already fired this
            // cycle (LastFiredCycleIndex == 0), window B (pid 20) has NOT (-1).
            var stopA = PickupStop(10u, 100.0, new Dictionary<string, double> { { "Ore", 100.0 } });
            stopA.LastFiredCycleIndex = 0;
            var stopB = PickupStop(20u, 200.0, new Dictionary<string, double> { { "Ore", 200.0 } });
            stopB.LastFiredCycleIndex = -1;
            var route = RouteWithStops(stopA, stopB);
            var resolver = new FakeResolver();
            resolver.Resolved[10u] = (10u, "Depot A", new Dictionary<string, double>(), null);
            resolver.Resolved[20u] = (20u, "Depot B", new Dictionary<string, double>(), null);

            // Filter to un-fired windows of cycle 0 (LastFiredCycleIndex < 0).
            bool built = RoutePickupSourceGate.TryBuildReservations(
                route, resolver.Resolve,
                stop => stop != null && stop.LastFiredCycleIndex < 0L,
                out var reservations, out string unresolved);

            Assert.True(built, "unresolved=" + unresolved);
            // Only window B's source is rebuilt - window A (already fired) is excluded.
            Assert.Single(reservations);
            Assert.Equal(20u, reservations[0].ResolvedPid);
            Assert.Equal(200.0, reservations[0].SummedResourceManifest["Ore"], 6);
            Assert.Null(reservations.Find(r => r.ResolvedPid == 10u));
        }

        // ---- M6 escrow-hold legibility: physical vs escrow short cause --------

        /// <summary>
        /// M6 escrow-hold legibility resolver variant: also supplies the RAW
        /// (un-netted) stored map + a reserving-route name lookup, mirroring the
        /// production wiring in <see cref="LiveRouteRuntimeEnvironment"/> (the
        /// netted map plays the escrow-netted reader's role).
        /// </summary>
        private sealed class EscrowFakeResolver
        {
            public Dictionary<uint, (uint pid, string name,
                Dictionary<string, double> netted, Dictionary<string, double> raw,
                System.Func<string, string> reservingLookup)> Resolved
                = new Dictionary<uint, (uint, string, Dictionary<string, double>,
                    Dictionary<string, double>, System.Func<string, string>)>();

            public RoutePickupSourceGate.PickupSourceResolution Resolve(RouteEndpoint endpoint)
            {
                if (!Resolved.TryGetValue(endpoint.VesselPersistentId, out var r))
                    return RoutePickupSourceGate.PickupSourceResolution.Miss("pid-miss");

                var netted = r.netted ?? new Dictionary<string, double>();
                var raw = r.raw ?? new Dictionary<string, double>();
                return RoutePickupSourceGate.PickupSourceResolution.Ok(
                    r.pid, r.name,
                    name => netted.TryGetValue(name, out double v) ? v : 0.0,
                    hash => 0,
                    name => raw.TryGetValue(name, out double v) ? v : 0.0,
                    r.reservingLookup);
            }
        }

        private static RoutePickupSourceGate.GateResult RunEscrow(
            Route route, EscrowFakeResolver resolver)
        {
            bool built = RoutePickupSourceGate.TryBuildSourceGroups(
                route, resolver.Resolve, out var groups, out string unresolved);
            Assert.True(built, "expected all pickup endpoints to resolve; unresolved=" + unresolved);
            return RoutePickupSourceGate.Evaluate(groups);
        }

        // catches: the pure classifier misreading the three cause shapes. Escrow
        // = physically sufficient (raw covers need) but netted short; physical =
        // raw itself short, even when escrow deepens the gap (mixed); degenerate
        // need / NaN inputs never classify escrow.
        [Fact]
        public void IsEscrowCausedShort_TruthTable()
        {
            // Escrow-caused: raw 150 covers need 100, netted 40 fell short.
            Assert.True(RoutePickupSourceGate.IsEscrowCausedShort(100.0, 150.0, 40.0));
            // Raw exactly at need still counts as physically sufficient.
            Assert.True(RoutePickupSourceGate.IsEscrowCausedShort(100.0, 100.0, 99.5));
            // Mixed: physically short (raw 80 < need 100) AND escrow netted lower
            // -> physical (the depot would hold the route with no competitors).
            Assert.False(RoutePickupSourceGate.IsEscrowCausedShort(100.0, 80.0, 30.0));
            // Pure physical: no escrow at all (raw == netted, both short).
            Assert.False(RoutePickupSourceGate.IsEscrowCausedShort(100.0, 40.0, 40.0));
            // Degenerate zero need is never escrow-short.
            Assert.False(RoutePickupSourceGate.IsEscrowCausedShort(0.0, 10.0, 10.0));
            // NaN inputs classify physical (comparisons fail).
            Assert.False(RoutePickupSourceGate.IsEscrowCausedShort(100.0, double.NaN, 40.0));
            Assert.False(RoutePickupSourceGate.IsEscrowCausedShort(double.NaN, 150.0, 40.0));
        }

        // catches: an escrow-caused short (depot physically stocked, netted short
        // because a competing route reserved it) rendering the SAME "source:" token
        // as a physically-empty depot - the M6 escrow-hold-legibility headline. The
        // gate must emit the "source-reserved:" token naming the reserving route
        // and flag EscrowShort with the raw/netted amounts carried for the log.
        [Fact]
        public void EscrowShort_EmitsReservedTokenNamingReservingRoute()
        {
            var route = RouteWithStops(
                PickupStop(10u, 100.0, new Dictionary<string, double> { { "Ore", 100.0 } }));
            var resolver = new EscrowFakeResolver();
            // Depot physically holds 150 Ore (raw) but a competitor reserved 110,
            // so this route's netted view is 40 - short by 60, escrow-caused.
            resolver.Resolved[10u] = (10u, "Depot A",
                new Dictionary<string, double> { { "Ore", 40.0 } },
                new Dictionary<string, double> { { "Ore", 150.0 } },
                name => name == "Ore" ? "Fuel Run Alpha" : null);

            var result = RunEscrow(route, resolver);

            Assert.False(result.Covered);
            Assert.True(result.EscrowShort);
            Assert.Equal("Fuel Run Alpha", result.ReservingRouteName);
            Assert.Equal("Ore", result.ShortResource);
            Assert.Equal(60.0, result.Shortfall, 6);
            Assert.Equal(150.0, result.ShortRawStored, 6);
            Assert.Equal(40.0, result.ShortNettedStored, 6);
            Assert.Equal("source-reserved:10:Depot A:Ore:Fuel Run Alpha", result.ShortHoldToken);
        }

        // catches: a PHYSICAL short leaking into the new token when the escrow
        // wiring is present - the pre-M6 "source:" token must stay byte-identical
        // whenever the depot is genuinely short (raw == netted, no reservations).
        [Fact]
        public void PhysicalShort_WithEscrowWiring_KeepsByteIdenticalSourceToken()
        {
            var route = RouteWithStops(
                PickupStop(10u, 100.0, new Dictionary<string, double> { { "Ore", 100.0 } }));
            var resolver = new EscrowFakeResolver();
            int lookupCalls = 0;
            resolver.Resolved[10u] = (10u, "Depot A",
                new Dictionary<string, double> { { "Ore", 40.0 } },
                new Dictionary<string, double> { { "Ore", 40.0 } },
                name => { lookupCalls++; return "Should Not Appear"; });

            var result = RunEscrow(route, resolver);

            Assert.False(result.Covered);
            Assert.False(result.EscrowShort);
            Assert.Null(result.ReservingRouteName);
            Assert.Equal(RoutePickupSourceGate.BuildHoldToken(10u, "Depot A", "Ore"),
                result.ShortHoldToken);
            Assert.Equal("source:10:Depot A:Ore", result.ShortHoldToken);
            // The reserving-route lookup must not even be consulted on a
            // physical short (classification gates the lookup).
            Assert.Equal(0, lookupCalls);
        }

        // catches: a MIXED short (physically short AND escrow-reduced further)
        // classifying escrow - it must stay physical; the depot cannot cover the
        // need even with every reservation released.
        [Fact]
        public void MixedShort_PhysicallyShort_ClassifiesPhysical()
        {
            var route = RouteWithStops(
                PickupStop(10u, 100.0, new Dictionary<string, double> { { "Ore", 100.0 } }));
            var resolver = new EscrowFakeResolver();
            int lookupCalls = 0;
            // raw 80 < need 100 (physically short); escrow nets it down to 30.
            resolver.Resolved[10u] = (10u, "Depot A",
                new Dictionary<string, double> { { "Ore", 30.0 } },
                new Dictionary<string, double> { { "Ore", 80.0 } },
                name => { lookupCalls++; return "Competitor"; });

            var result = RunEscrow(route, resolver);

            Assert.False(result.Covered);
            Assert.False(result.EscrowShort);
            Assert.StartsWith("source:", result.ShortHoldToken);
            Assert.Equal(0, lookupCalls);
        }

        // catches: an escrow-caused short with NO reserving route found (the
        // lookup returns null - e.g. the reservation vanished between the net and
        // the lookup) emitting a reserved token naming nobody. It must fall back
        // to the physical token - the new token fires ONLY when a competing
        // reservation actually explains the shortfall.
        [Fact]
        public void EscrowCaused_NoReservingRouteFound_FallsBackToPhysicalToken()
        {
            var route = RouteWithStops(
                PickupStop(10u, 100.0, new Dictionary<string, double> { { "Ore", 100.0 } }));
            var resolver = new EscrowFakeResolver();
            resolver.Resolved[10u] = (10u, "Depot A",
                new Dictionary<string, double> { { "Ore", 40.0 } },
                new Dictionary<string, double> { { "Ore", 150.0 } },
                name => null);

            var result = RunEscrow(route, resolver);

            Assert.False(result.Covered);
            Assert.False(result.EscrowShort);
            Assert.Null(result.ReservingRouteName);
            Assert.Equal("source:10:Depot A:Ore", result.ShortHoldToken);
        }

        // catches: a legacy resolution (no raw reader, the pre-M6 4-arg Ok used by
        // every older call site) changing behavior - the short must classify
        // physical with the byte-identical token.
        [Fact]
        public void LegacyResolution_NoRawReader_PhysicalTokenByteIdentical()
        {
            var route = RouteWithStops(
                PickupStop(10u, 100.0, new Dictionary<string, double> { { "Ore", 100.0 } }));
            var resolver = new FakeResolver();
            resolver.Resolved[10u] = (10u, "Depot A", new Dictionary<string, double> { { "Ore", 40.0 } }, null);

            var result = Run(route, resolver);

            Assert.False(result.Covered);
            Assert.False(result.EscrowShort);
            Assert.Equal("source:10:Depot A:Ore", result.ShortHoldToken);
            // Without a raw reader the raw amount mirrors the netted one.
            Assert.Equal(result.ShortNettedStored, result.ShortRawStored, 6);
        }

        // catches: colons in the vessel or reserving-route name breaking the
        // presentation's 4-way token split (both must be sanitized like the
        // existing "source:" token's vessel name).
        [Fact]
        public void BuildReservedHoldToken_SanitizesColons()
        {
            Assert.Equal("source-reserved:5:A_B:Ore:R_1",
                RoutePickupSourceGate.BuildReservedHoldToken(5u, "A:B", "Ore", "R:1"));
            Assert.Equal("source-reserved:5:<unnamed>:Ore:<unnamed>",
                RoutePickupSourceGate.BuildReservedHoldToken(5u, null, "Ore", null));
        }
    }
}
