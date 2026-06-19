using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// M4b Phase B2 (plan D11 / OQ7): pins the RAM-only cargo ESCROW map on
    /// <see cref="RouteStore"/> - the reserve / release / drop / clear lifecycle and
    /// the competing-route NET semantics. The escrow is a static map (shared state),
    /// so this class is <c>[Collection("Sequential")]</c> and resets it in the ctor
    /// + Dispose; a leaked reservation would corrupt a later test in the collection.
    ///
    /// <para><b>NET semantics under test (the crux):</b> the amount route R may rely
    /// on for a source pid+resource = live stored MINUS the sum of reservations held
    /// by EVERY OTHER route on that pid+resource. R does NOT subtract its OWN
    /// reservation (it owns what it reserved). Each test states the regression it
    /// guards.</para>
    ///
    /// <para>The production gate (<see cref="LiveRouteRuntimeEnvironment"/>) wraps
    /// its live stored-amount reader as
    /// <c>name =&gt; max(0, live(name) - RouteStore.OtherRoutesReservedFor(routeId, pid, name))</c>;
    /// these tests reproduce that exact wrap via <see cref="GateAvailable"/> and also
    /// drive the pure <see cref="RoutePickupSourceGate"/> end-to-end so the
    /// competing-route hold is proven through the real all-or-nothing decision.</para>
    /// </summary>
    [Collection("Sequential")]
    public class RouteCargoEscrowTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteCargoEscrowTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---- the production net wrap, reproduced for the gate-available read ----

        /// <summary>
        /// The amount route <paramref name="routeId"/> sees as available on
        /// <paramref name="pid"/> for <paramref name="resource"/>, given a live
        /// stored amount: the exact production wrap from
        /// <see cref="LiveRouteRuntimeEnvironment"/>.
        /// </summary>
        private static double GateAvailable(
            string routeId, uint pid, string resource, double liveStored)
        {
            double available =
                liveStored - RouteStore.OtherRoutesReservedFor(routeId, pid, resource);
            return available > 0.0 ? available : 0.0;
        }

        // ---- A reserves: B sees less, A excludes its own ---------------------

        // catches: a competing route NOT seeing another route's reservation
        // (double-claim hazard) AND a route wrongly subtracting its OWN reservation
        // (it owns what it reserved).
        [Fact]
        public void Reserve_OtherRouteSeesLess_OwnerExcludesOwn()
        {
            const uint depotX = 100u;
            RouteStore.ReserveCargo("route-A", depotX, "LiquidFuel", 100.0);

            // Depot X has 300 LF live. Route B (a different route) sees 300 - 100.
            Assert.Equal(200.0, GateAvailable("route-B", depotX, "LiquidFuel", 300.0));
            // Route A (the owner) sees the full 300 - it does not subtract its own.
            Assert.Equal(300.0, GateAvailable("route-A", depotX, "LiquidFuel", 300.0));

            // The reserve logged a new-total line for audit.
            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("ReserveCargo") && l.Contains("newTotal=100"));
        }

        // catches: the net leaking across resource names (LF reservation reducing
        // Oxidizer availability) or across pids.
        [Fact]
        public void Reserve_NetIsPerResourceAndPerPid()
        {
            const uint depotX = 100u;
            const uint depotY = 200u;
            RouteStore.ReserveCargo("route-A", depotX, "LiquidFuel", 100.0);

            // Same pid, DIFFERENT resource: untouched.
            Assert.Equal(50.0, GateAvailable("route-B", depotX, "Oxidizer", 50.0));
            // DIFFERENT pid, same resource: untouched.
            Assert.Equal(80.0, GateAvailable("route-B", depotY, "LiquidFuel", 80.0));
        }

        // ---- two routes reserve; a third sees both; release one --------------

        // catches: the net summing only ONE competing reservation instead of ALL
        // other routes' reservations on the pid+resource.
        [Fact]
        public void TwoRoutesReserve_ThirdSeesBoth_ReleaseRaisesAvailable()
        {
            const uint depotX = 100u;
            RouteStore.ReserveCargo("route-A", depotX, "Ore", 100.0);
            RouteStore.ReserveCargo("route-B", depotX, "Ore", 200.0);

            // Route C (neither A nor B) sees 500 - (100 + 200) = 200.
            Assert.Equal(200.0, GateAvailable("route-C", depotX, "Ore", 500.0));

            // Release A's reservation -> C now sees 500 - 200 = 300.
            RouteStore.ReleaseCargo("route-A", depotX, "Ore", 100.0);
            Assert.Equal(300.0, GateAvailable("route-C", depotX, "Ore", 500.0));

            // A's own reservation is gone (released to zero -> key removed).
            Assert.Equal(0.0, RouteStore.GetReservedForTesting("route-A", depotX, "Ore"));
            // B's reservation is intact.
            Assert.Equal(200.0, RouteStore.GetReservedForTesting("route-B", depotX, "Ore"));
        }

        // catches: a partial release clamping wrong, or a release below zero
        // producing a negative residual the net would then ADD back to availability.
        [Fact]
        public void Release_PartialThenOverRelease_ClampsAtZero()
        {
            const uint depotX = 100u;
            RouteStore.ReserveCargo("route-A", depotX, "Ore", 100.0);

            // Partial release: 100 - 30 = 70 remains.
            RouteStore.ReleaseCargo("route-A", depotX, "Ore", 30.0);
            Assert.Equal(70.0, RouteStore.GetReservedForTesting("route-A", depotX, "Ore"));
            Assert.Equal(30.0, GateAvailable("route-B", depotX, "Ore", 100.0));

            // Over-release: clamps to zero, key removed (no negative residual).
            RouteStore.ReleaseCargo("route-A", depotX, "Ore", 999.0);
            Assert.Equal(0.0, RouteStore.GetReservedForTesting("route-A", depotX, "Ore"));
            Assert.Equal(100.0, GateAvailable("route-B", depotX, "Ore", 100.0));
        }

        // catches: accumulate semantics broken (a second reserve replacing instead
        // of summing) - two same-pid windows must accumulate (OQ6).
        [Fact]
        public void Reserve_Accumulates()
        {
            const uint depotX = 100u;
            RouteStore.ReserveCargo("route-A", depotX, "LiquidFuel", 100.0);
            RouteStore.ReserveCargo("route-A", depotX, "LiquidFuel", 50.0);
            Assert.Equal(150.0, RouteStore.GetReservedForTesting("route-A", depotX, "LiquidFuel"));
            // A competing route sees the full accumulated reservation subtracted.
            Assert.Equal(150.0, GateAvailable("route-B", depotX, "LiquidFuel", 300.0));
        }

        // ---- DropRouteEscrow / RemoveRoute -----------------------------------

        // catches: DropRouteEscrow leaving stragglers (B's available not fully
        // restored) or dropping the WRONG route's reservation.
        [Fact]
        public void DropRouteEscrow_RemovesAllOfARoute_RaisesCompetitorAvailable()
        {
            const uint depotX = 100u;
            const uint depotY = 200u;
            RouteStore.ReserveCargo("route-A", depotX, "LiquidFuel", 100.0);
            RouteStore.ReserveCargo("route-A", depotY, "Oxidizer", 60.0);
            RouteStore.ReserveCargo("route-B", depotX, "LiquidFuel", 40.0);

            // Before drop: C sees 300 - (A 100 + B 40) = 160 on depotX.
            Assert.Equal(160.0, GateAvailable("route-C", depotX, "LiquidFuel", 300.0));

            RouteStore.DropRouteEscrow("route-A");

            // After drop: A's reservations (both pids) are gone; C sees only B's 40.
            Assert.Equal(260.0, GateAvailable("route-C", depotX, "LiquidFuel", 300.0));
            Assert.Equal(0.0, RouteStore.GetReservedForTesting("route-A", depotX, "LiquidFuel"));
            Assert.Equal(0.0, RouteStore.GetReservedForTesting("route-A", depotY, "Oxidizer"));
            // B untouched.
            Assert.Equal(40.0, RouteStore.GetReservedForTesting("route-B", depotX, "LiquidFuel"));

            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("DropRouteEscrow") && l.Contains("droppedPids=2"));
        }

        // catches: RemoveRoute NOT dropping the removed route's escrow (a tombstoned
        // route leaving a phantom reservation that strands a competitor forever).
        [Fact]
        public void RemoveRoute_DropsItsEscrow()
        {
            const uint depotX = 100u;
            var routeA = new Route { Id = "route-A", Stops = new List<RouteStop>() };
            RouteStore.AddRoute(routeA);
            RouteStore.ReserveCargo("route-A", depotX, "Ore", 100.0);

            // Competitor sees the reservation before removal.
            Assert.Equal(150.0, GateAvailable("route-B", depotX, "Ore", 250.0));

            bool removed = RouteStore.RemoveRoute("route-A");
            Assert.True(removed);

            // Removal dropped the escrow -> competitor's available fully restored.
            Assert.Equal(250.0, GateAvailable("route-B", depotX, "Ore", 250.0));
            Assert.Equal(0, RouteStore.EscrowRouteCountForTesting);
        }

        // ---- ClearAllEscrow / ResetForTesting --------------------------------

        // catches: ClearAllEscrow (the scene-change / main-menu lifecycle clear)
        // leaving any reservation behind.
        [Fact]
        public void ClearAllEscrow_EmptiesEveryRoute()
        {
            RouteStore.ReserveCargo("route-A", 100u, "LiquidFuel", 100.0);
            RouteStore.ReserveCargo("route-B", 200u, "Oxidizer", 50.0);
            Assert.Equal(2, RouteStore.EscrowRouteCountForTesting);

            RouteStore.ClearAllEscrow("test-scene-change");

            Assert.Equal(0, RouteStore.EscrowRouteCountForTesting);
            Assert.Equal(100.0, GateAvailable("route-C", 100u, "LiquidFuel", 100.0));
            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("ClearAllEscrow") && l.Contains("clearedRoutes=2"));
        }

        // catches: ResetForTesting NOT clearing the escrow -> a reservation leaking
        // across [Collection("Sequential")] tests (the steady-state guard for the
        // whole class).
        [Fact]
        public void ResetForTesting_ClearsEscrow_NoLeak()
        {
            RouteStore.ReserveCargo("route-A", 100u, "Ore", 100.0);
            Assert.Equal(1, RouteStore.EscrowRouteCountForTesting);

            RouteStore.ResetForTesting();

            Assert.Equal(0, RouteStore.EscrowRouteCountForTesting);
            Assert.Equal(0.0, RouteStore.OtherRoutesReservedFor("route-B", 100u, "Ore"));
        }

        // ---- not serialized --------------------------------------------------

        // catches: the escrow accidentally being persisted into the save (it MUST be
        // RAM-only) - a SaveRoutesTo / LoadRoutesFrom round-trip must leave the map
        // untouched and a loaded route must carry no escrow.
        [Fact]
        public void Escrow_IsNotSerialized_RoundTripLeavesItUntouched()
        {
            const uint depotX = 100u;
            var routeA = new Route
            {
                Id = "route-A",
                IsKscOrigin = true,
                Stops = new List<RouteStop>(),
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-1" }
                },
            };
            RouteStore.AddRoute(routeA);
            RouteStore.ReserveCargo("route-A", depotX, "LiquidFuel", 100.0);

            // Save writes ONLY route data; the escrow value never enters the node.
            var scenarioNode = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(scenarioNode);
            string saved = scenarioNode.ToString();
            Assert.DoesNotContain("cargoEscrow", saved, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("escrow", saved, StringComparison.OrdinalIgnoreCase);

            // Reservation survives the save in RAM (save is read-only over escrow).
            Assert.Equal(100.0, RouteStore.GetReservedForTesting("route-A", depotX, "LiquidFuel"));

            // Load wholesale-replaces routes from the node; the loaded route carries
            // NO escrow (the map is not touched by the load path either - the
            // pre-load reservation persists because nothing in load clears it; a
            // freshly loaded route's escrow is whatever RAM holds, here still A's
            // pre-existing reservation, which the lifecycle clears handle in prod).
            RouteStore.LoadRoutesFrom(scenarioNode);

            // The decisive point: the loaded ROUTE data carried no escrow into RAM
            // (the value present is the live reservation, not a deserialized one).
            // Prove the load did not ADD or CHANGE escrow by clearing first then
            // loading: a clean-RAM load yields zero escrow.
            RouteStore.ClearAllEscrow("pre-load-check");
            RouteStore.LoadRoutesFrom(scenarioNode);
            Assert.Equal(0, RouteStore.EscrowRouteCountForTesting);
            Assert.Equal(0.0, RouteStore.GetReservedForTesting("route-A", depotX, "LiquidFuel"));
        }

        // ---- competing-route case end-to-end through the real gate -----------

        // catches: the headline B2 invariant through the PRODUCTION decision path -
        // route A reserves a depot's cargo at dispatch, and route B gating the SAME
        // depot before A's physical debit is HELD (the depot's available, net of A's
        // reservation, no longer covers B's pickup). Proven through the real
        // RoutePickupSourceGate all-or-nothing Evaluate, with the resource reader
        // wrapped exactly as production wraps it.
        [Fact]
        public void CompetingRoute_HeldByReservation_ThroughRealGate()
        {
            const uint depotX = 100u;
            const double depotLiveLf = 400.0;

            // Route A reserves 300 LF from depot X at its dispatch (B3 will do this
            // in production; B2 lets a test reserve directly).
            RouteStore.ReserveCargo("route-A", depotX, "LiquidFuel", 300.0);

            // Route B wants to pick up 200 LF from the SAME depot X. Build B's pickup
            // stop + run it through the real gate with B's net-wrapped reader.
            var routeB = new Route
            {
                Id = "route-B",
                IsKscOrigin = false,
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = depotX },
                        PickupManifest = new Dictionary<string, double> { { "LiquidFuel", 200.0 } },
                        RecordedDockUT = 1000.0,
                    }
                },
            };

            RoutePickupSourceGate.PickupSourceResolution Resolver(RouteEndpoint ep)
            {
                // Net wrap identical to LiveRouteRuntimeEnvironment: live MINUS other
                // routes' reservations (B does not subtract its own).
                Func<string, double> nettedReader = name =>
                {
                    double live = string.Equals(name, "LiquidFuel", StringComparison.Ordinal)
                        ? depotLiveLf : 0.0;
                    double avail = live - RouteStore.OtherRoutesReservedFor("route-B", depotX, name);
                    return avail > 0.0 ? avail : 0.0;
                };
                return RoutePickupSourceGate.PickupSourceResolution.Ok(
                    depotX, "Depot X", nettedReader, hash => 0);
            }

            bool built = RoutePickupSourceGate.TryBuildSourceGroups(
                routeB, Resolver, out var groups, out string unresolved);
            Assert.True(built, "expected depot to resolve; unresolved=" + unresolved);

            // Depot live 400, A reserved 300 -> B sees 100 available < 200 needed: HOLD.
            RoutePickupSourceGate.GateResult heldResult = RoutePickupSourceGate.Evaluate(groups);
            Assert.False(heldResult.Covered);
            Assert.Equal(depotX, heldResult.ShortSourcePid);
            Assert.Equal("LiquidFuel", heldResult.ShortResource);

            // Now A's cycle physically debits + releases its 300 reservation. B
            // gating again sees the full 400 (minus A's now-zero reservation) and
            // dispatches.
            RouteStore.ReleaseCargo("route-A", depotX, "LiquidFuel", 300.0);

            bool builtAgain = RoutePickupSourceGate.TryBuildSourceGroups(
                routeB, Resolver, out var groupsAgain, out _);
            Assert.True(builtAgain);
            RoutePickupSourceGate.GateResult coveredResult =
                RoutePickupSourceGate.Evaluate(groupsAgain);
            Assert.True(coveredResult.Covered);
        }

        // catches: a no-reservation gate read differing by a single bit from B1 (the
        // net must be a NO-OP when nothing is reserved - byte-behaviour-identical to
        // B1 in production until B3 wires the reserve).
        [Fact]
        public void NoReservation_NetIsNoOp()
        {
            Assert.Equal(0.0, RouteStore.OtherRoutesReservedFor("route-A", 100u, "LiquidFuel"));
            Assert.Equal(300.0, GateAvailable("route-A", 100u, "LiquidFuel", 300.0));
            Assert.Equal(0, RouteStore.EscrowRouteCountForTesting);
        }
    }
}
