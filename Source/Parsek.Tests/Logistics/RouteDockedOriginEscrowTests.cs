using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// ROUTE-DOCKED-ORIGIN-IGNORES-ESCROW. Two halves:
    /// <list type="number">
    /// <item><description>The docked-origin gate nets OTHER routes' cargo escrow on
    /// the origin vessel (<see cref="RoutePickupSourceGate.OriginCoversNetOfOtherRoutes"/>),
    /// the same net the pickup-source gate applies, so route A whose docked origin
    /// is depot D cannot dispatch on cargo an in-flight multi-stop route B reserved
    /// at D for its later pickup window.</description></item>
    /// <item><description>A delivery never writes more than the cycle's origin
    /// debit and pickups actually put on board
    /// (<see cref="RouteOrchestrator.ComputeCycleSupplyDeficit"/> + the planner cap), so a short supply
    /// that still happens (the player drains the depot between dispatch and the
    /// pickup window, a source that cannot be resolved) no longer creates cargo.</description></item>
    /// </list>
    /// </summary>
    [Collection("Sequential")]
    public class RouteDockedOriginEscrowTests : IDisposable
    {
        private const string LF = "LiquidFuel";
        private const string Ox = "Oxidizer";
        private const uint DepotPid = 4242u;

        public RouteDockedOriginEscrowTests()
        {
            ParsekLog.ResetTestOverrides();
            RouteStore.ResetForTesting();
        }

        public void Dispose()
        {
            RouteStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static Dictionary<string, double> M(params (string Name, double Amount)[] entries)
        {
            var d = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var e in entries) d[e.Name] = e.Amount;
            return d;
        }

        private static Func<string, double> Stored(Dictionary<string, double> stored)
            => name => stored.TryGetValue(name, out double v) ? v : 0.0;

        private static Func<string, double> OthersReservedOnDepot(string routeId)
            => name => RouteStore.OtherRoutesReservedFor(routeId, DepotPid, name);

        // ---- half 1: the docked-origin gate nets other routes' escrow -------------

        // catches: the defect itself. Depot D holds 100 LF, route B reserved 60 at D
        // for a later pickup window, docked-origin route A needs 50. The raw gate (the
        // pre-fix call) passes A on cargo B already claimed; the netted gate holds A,
        // names the resource, and classifies the short as escrow-caused.
        [Fact]
        public void OriginGate_OtherRouteReservedAtOrigin_HoldsWithEscrowCause()
        {
            RouteStore.ReserveCargo("route-B", DepotPid, LF, 60.0);
            var stored = M((LF, 100.0));
            var cost = M((LF, 50.0));

            Assert.True(RouteOriginCargoCheck.HasRequired(cost, Stored(stored), out _, out _));

            bool covered = RoutePickupSourceGate.OriginCoversNetOfOtherRoutes(
                cost, Stored(stored), OthersReservedOnDepot("route-A"),
                out string lacking, out double shortfall,
                out bool escrowCaused, out double raw, out double netted);

            Assert.False(covered);
            Assert.Equal(LF, lacking);
            Assert.Equal(10.0, shortfall, 9);
            Assert.True(escrowCaused);
            Assert.Equal(100.0, raw, 9);
            Assert.Equal(40.0, netted, 9);
        }

        // catches: a net that subtracts the route's OWN reservation (it owns what it
        // reserved, the pickup gate's contract).
        [Fact]
        public void OriginGate_OwnReservation_IsNotNetted()
        {
            RouteStore.ReserveCargo("route-A", DepotPid, LF, 60.0);

            Assert.True(RoutePickupSourceGate.OriginCoversNetOfOtherRoutes(
                M((LF, 50.0)), Stored(M((LF, 100.0))), OthersReservedOnDepot("route-A"),
                out _, out _, out bool escrowCaused, out _, out _));
            Assert.False(escrowCaused);
        }

        // catches: an escrow cause claimed for a depot that is short even with no
        // competitor (the hold must say "out of", not "reserved by").
        [Fact]
        public void OriginGate_PhysicallyShort_ClassifiesPhysical()
        {
            RouteStore.ReserveCargo("route-B", DepotPid, LF, 10.0);

            bool covered = RoutePickupSourceGate.OriginCoversNetOfOtherRoutes(
                M((LF, 50.0)), Stored(M((LF, 30.0))), OthersReservedOnDepot("route-A"),
                out string lacking, out double shortfall,
                out bool escrowCaused, out double raw, out double netted);

            Assert.False(covered);
            Assert.Equal(LF, lacking);
            Assert.Equal(30.0, shortfall, 9);
            Assert.False(escrowCaused);
            Assert.Equal(30.0, raw, 9);
            Assert.Equal(20.0, netted, 9);
        }

        // catches: a reservation on ANOTHER vessel or another resource leaking into
        // the origin gate; with nothing reserved on the origin the netted gate is the
        // raw gate.
        [Fact]
        public void OriginGate_ReservationElsewhere_DoesNotHold()
        {
            RouteStore.ReserveCargo("route-B", DepotPid + 1u, LF, 1000.0);
            RouteStore.ReserveCargo("route-B", DepotPid, Ox, 1000.0);

            Assert.True(RoutePickupSourceGate.OriginCoversNetOfOtherRoutes(
                M((LF, 50.0)), Stored(M((LF, 50.0))), OthersReservedOnDepot("route-A"),
                out string lacking, out _, out _, out _, out _));
            Assert.Equal(string.Empty, lacking);
        }

        // catches: the netted gate losing the all-or-nothing first-short naming
        // (ordinal order) of the raw gate.
        [Fact]
        public void OriginGate_TwoResources_NamesFirstShortInOrdinalOrder()
        {
            RouteStore.ReserveCargo("route-B", DepotPid, LF, 95.0);
            RouteStore.ReserveCargo("route-B", DepotPid, Ox, 95.0);

            bool covered = RoutePickupSourceGate.OriginCoversNetOfOtherRoutes(
                M((Ox, 10.0), (LF, 10.0)), Stored(M((LF, 100.0), (Ox, 100.0))),
                OthersReservedOnDepot("route-A"),
                out string lacking, out double shortfall, out bool escrowCaused, out _, out _);

            Assert.False(covered);
            Assert.Equal(LF, lacking);
            Assert.Equal(5.0, shortfall, 9);
            Assert.True(escrowCaused);
        }

        // catches: a null reservation reader failing closed instead of netting nothing,
        // and a null stored reader passing.
        [Fact]
        public void OriginGate_NullReaders()
        {
            Assert.True(RoutePickupSourceGate.OriginCoversNetOfOtherRoutes(
                M((LF, 5.0)), Stored(M((LF, 5.0))), null,
                out _, out _, out _, out _, out _));
            Assert.False(RoutePickupSourceGate.OriginCoversNetOfOtherRoutes(
                M((LF, 5.0)), null, OthersReservedOnDepot("route-A"),
                out string lacking, out _, out bool escrowCaused, out _, out _));
            Assert.Equal("null-stored-reader", lacking);
            Assert.False(escrowCaused);
        }

        // catches: the live gate reverting to the raw origin probe (the pre-fix shape).
        // The live gate needs FlightGlobals, so this pins its wiring by source:
        // OriginProvenanceHasCargo calls the netted helper with the escrow reader.
        [Fact]
        public void LiveOriginGate_CallsTheNettedHelperWithTheEscrowReader()
        {
            string body = MethodBody(
                ReadSource("Source/Parsek/Logistics/LiveRouteRuntimeEnvironment.cs"),
                "private bool OriginProvenanceHasCargo(");
            Assert.Contains("RoutePickupSourceGate.OriginCoversNetOfOtherRoutes(", body);
            Assert.Contains("RouteStore.OtherRoutesReservedFor(routeId, originPid, name)", body);
            Assert.DoesNotContain("RouteOriginCargoCheck.HasRequired(", body);
        }

        // ---- half 2: a delivery never exceeds what the cycle picked up ------------

        private static Route TwoStopRoute()
        {
            // stop 0: pickup 100 LF at depot D; stop 1: deliver 100 LF at station S.
            return new Route
            {
                Id = "route-B",
                Stops = new List<RouteStop>
                {
                    new RouteStop { PickupManifest = M((LF, 100.0)) },
                    new RouteStop { DeliveryManifest = M((LF, 100.0)) },
                },
            };
        }

        private static RouteEvent Row(RouteEventType type, string routeId, string cycleId,
            int stopIndex, int seq, Dictionary<string, double> actual,
            Dictionary<string, double> requested = null)
        {
            return new RouteEvent
            {
                Type = type,
                RouteId = routeId,
                RouteCycleId = cycleId,
                RouteStopIndex = stopIndex,
                Sequence = seq,
                RouteResourceManifest = actual,
                RouteRequestedResourceManifest = requested,
            };
        }

        private const int S = RouteOrchestrator.SeqStride;

        // catches: the cargo creation. The pickup window found only 40 of 100 LF
        // (row actual 40, requested 100); the later delivery's outstanding deficit is
        // 60, so the delivery writes 40, not the recorded 100.
        [Fact]
        public void Deficit_ShortPickup_CapsTheLaterDelivery()
        {
            Route route = TwoStopRoute();
            var events = new List<RouteEvent>
            {
                Row(RouteEventType.RouteDispatched, route.Id, "cycle-0", 0, 0, null),
                Row(RouteEventType.RouteCargoPickedUp, route.Id, "cycle-0", 0, 2,
                    M((LF, 40.0)), M((LF, 100.0))),
            };

            var deficit = RouteOrchestrator.ComputeCycleSupplyDeficit(route, "cycle-0", 1 * S + 3, events);

            Assert.Single(deficit);
            Assert.Equal(60.0, deficit[LF], 9);
            Assert.Equal(40.0, RouteDeliveryPlanner.CapBySupplyDeficit(100.0, deficit, LF), 9);
            Assert.Equal(100.0, RouteDeliveryPlanner.CapBySupplyDeficit(100.0, deficit, Ox), 9);
        }

        // catches: the mirror - a short ORIGIN debit (the docked-origin dispatch row)
        // must cap the delivery the same way a short pickup does; an unresolved
        // origin (zero actuals, full requested) caps it to zero.
        [Theory]
        [InlineData(30.0, 30.0)]
        [InlineData(0.0, 0.0)]
        public void Deficit_ShortOriginDebit_CapsTheDelivery(double debited, double expectedDelivered)
        {
            var route = new Route
            {
                Id = "route-A",
                Stops = new List<RouteStop> { new RouteStop { DeliveryManifest = M((LF, 50.0)) } },
            };
            var events = new List<RouteEvent>
            {
                Row(RouteEventType.RouteDispatched, route.Id, "cycle-3", -1, 0, null),
                Row(RouteEventType.RouteCargoDebited, route.Id, "cycle-3", -1, 1,
                    debited > 0.0 ? M((LF, debited)) : null, M((LF, 50.0))),
            };

            var deficit = RouteOrchestrator.ComputeCycleSupplyDeficit(route, "cycle-3", 3, events);

            Assert.Equal(expectedDelivered,
                RouteDeliveryPlanner.CapBySupplyDeficit(50.0, deficit, LF), 9);
        }

        // catches: a full supply (no requested-on-shortfall manifest) capping anything;
        // the common case must stay byte-identical.
        [Fact]
        public void Deficit_FullSupply_IsEmpty()
        {
            Route route = TwoStopRoute();
            var events = new List<RouteEvent>
            {
                Row(RouteEventType.RouteCargoDebited, route.Id, "cycle-0", 0, 1, M((LF, 10.0))),
                Row(RouteEventType.RouteCargoPickedUp, route.Id, "cycle-0", 0, 2, M((LF, 100.0))),
            };

            Assert.Empty(RouteOrchestrator.ComputeCycleSupplyDeficit(route, "cycle-0", 1 * S + 3, events));
        }

        // catches: rows of another cycle, another route, or a LATER window leaking
        // into the cap.
        [Fact]
        public void Deficit_IgnoresOtherCyclesRoutesAndLaterWindows()
        {
            Route route = TwoStopRoute();
            var events = new List<RouteEvent>
            {
                Row(RouteEventType.RouteCargoPickedUp, route.Id, "cycle-1", 0, 2,
                    M((LF, 0.0)), M((LF, 100.0))),
                Row(RouteEventType.RouteCargoPickedUp, "route-other", "cycle-0", 0, 2,
                    M((LF, 0.0)), M((LF, 100.0))),
                Row(RouteEventType.RouteCargoPickedUp, route.Id, "cycle-0", 2, 2 * S + 2,
                    M((LF, 0.0)), M((LF, 100.0))),
            };

            Assert.Empty(RouteOrchestrator.ComputeCycleSupplyDeficit(route, "cycle-0", 1 * S + 3, events));
        }

        // catches: a deficit charged twice. The pickup is 60 short (40 of 100), then
        // two deliveries of 50 each: the first is capped to 0 (50 - 60), which keeps
        // 50 on board, so the second sees a deficit of 10 and delivers 40. Total
        // delivered 40 == total picked up 40.
        [Fact]
        public void Deficit_EarlierCappedDelivery_AbsorbsItsShare()
        {
            var route = new Route
            {
                Id = "route-B",
                Stops = new List<RouteStop>
                {
                    new RouteStop { PickupManifest = M((LF, 100.0)) },
                    new RouteStop { DeliveryManifest = M((LF, 50.0)) },
                    new RouteStop { DeliveryManifest = M((LF, 50.0)) },
                },
            };
            var events = new List<RouteEvent>
            {
                Row(RouteEventType.RouteCargoPickedUp, route.Id, "cycle-0", 0, 2,
                    M((LF, 40.0)), M((LF, 100.0))),
            };

            var atFirst = RouteOrchestrator.ComputeCycleSupplyDeficit(route, "cycle-0", 1 * S + 3, events);
            double firstDelivered = RouteDeliveryPlanner.CapBySupplyDeficit(50.0, atFirst, LF);
            Assert.Equal(0.0, firstDelivered, 9);

            events.Add(Row(RouteEventType.RouteCargoDelivered, route.Id, "cycle-0", 1, 1 * S + 3,
                firstDelivered > 0.0 ? M((LF, firstDelivered)) : null, M((LF, 50.0))));
            var atSecond = RouteOrchestrator.ComputeCycleSupplyDeficit(route, "cycle-0", 2 * S + 3, events);
            double secondDelivered = RouteDeliveryPlanner.CapBySupplyDeficit(50.0, atSecond, LF);

            Assert.Equal(10.0, atSecond[LF], 9);
            Assert.Equal(40.0, secondDelivered, 9);
            Assert.Equal(40.0, firstDelivered + secondDelivered, 9);
        }

        // catches: cargo a FULL destination refused at an earlier window counted as
        // still on board beyond the deficit (the walk floors at zero; it never lets a
        // later delivery exceed its own recorded amount).
        [Fact]
        public void Deficit_EarlierCapacityClamp_FloorsAtZero()
        {
            var route = new Route
            {
                Id = "route-B",
                Stops = new List<RouteStop>
                {
                    new RouteStop { PickupManifest = M((LF, 100.0)) },
                    new RouteStop { DeliveryManifest = M((LF, 50.0)) },
                    new RouteStop { DeliveryManifest = M((LF, 50.0)) },
                },
            };
            var events = new List<RouteEvent>
            {
                Row(RouteEventType.RouteCargoPickedUp, route.Id, "cycle-0", 0, 2,
                    M((LF, 90.0)), M((LF, 100.0))),
                // stop 1 destination was full: delivered nothing of its 50.
                Row(RouteEventType.RouteCargoDelivered, route.Id, "cycle-0", 1, 1 * S + 3,
                    null, M((LF, 50.0))),
            };

            var atSecond = RouteOrchestrator.ComputeCycleSupplyDeficit(route, "cycle-0", 2 * S + 3, events);

            Assert.Empty(atSecond);
            Assert.Equal(50.0, RouteDeliveryPlanner.CapBySupplyDeficit(50.0, atSecond, LF), 9);
        }

        // catches: the planner ignoring the deficit, or reporting a capped line as
        // full. Requested stays the recorded amount so the row and the partial
        // summary read "delivered/recorded".
        [Fact]
        public void Planner_SupplyDeficit_CapsAvailableAndMarksPartial()
        {
            var route = new Route
            {
                Id = "route-B",
                Stops = new List<RouteStop> { new RouteStop { DeliveryManifest = M((LF, 50.0), (Ox, 20.0)) } },
            };
            var probe = new FixedCapacityProbe(1000.0);

            DeliveryPlan plain = RouteDeliveryPlanner.PrepareDelivery(route, 0, probe);
            Assert.False(plain.IsPartial);

            DeliveryPlan capped = RouteDeliveryPlanner.PrepareDelivery(
                route, 0, probe, M((LF, 20.0)));

            Assert.True(capped.IsPartial);
            Assert.Equal(2, capped.Resources.Count);
            ResourceDeliveryLine lf = capped.Resources[0];
            Assert.Equal(LF, lf.Name);
            Assert.Equal(50.0, lf.Requested, 9);
            Assert.Equal(30.0, lf.Available, 9);
            ResourceDeliveryLine ox = capped.Resources[1];
            Assert.Equal(20.0, ox.Available, 9);

            // Capacity below the capped amount still wins.
            DeliveryPlan tight = RouteDeliveryPlanner.PrepareDelivery(
                route, 0, new FixedCapacityProbe(10.0), M((LF, 20.0)));
            Assert.Equal(10.0, tight.Resources[0].Available, 9);
        }

        // catches: the live delivery applier not passing the deficit (the planner cap
        // would then be dead code in play).
        [Fact]
        public void LiveDelivery_PassesTheCycleSupplyDeficitToThePlanner()
        {
            string src = ReadSource("Source/Parsek/Logistics/RouteOrchestrator.Delivery.cs");
            Assert.Contains("ComputeCycleSupplyDeficit(", src);
            Assert.Contains("RouteDeliveryPlanner.PrepareDelivery(route, stopIndex, probe, supplyDeficit)", src);
        }

        private sealed class FixedCapacityProbe : IDeliveryCapacityProbe
        {
            private readonly double capacity;
            public FixedCapacityProbe(double capacity) { this.capacity = capacity; }
            public double ProbeResourceFreeCapacity(string resourceName) => capacity;
            public InventorySlotAddress ProbeFirstEmptyInventorySlot() => InventorySlotAddress.None;
            public void ConsumeInventorySlot(InventorySlotAddress address) { }
            public int ProbeInventoryStackableQuantity(InventoryPayloadItem item) => 1;
            public int ProbeInventoryUnitsThatFit(InventoryPayloadItem item, int requestedUnits) => 0;
            public void ConsumeInventoryCapacity(InventoryPayloadItem item, int units) { }
        }

        // ---- source helpers --------------------------------------------------------

        private static string ReadSource(string repoRelative)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string text = File.ReadAllText(Path.Combine(root, repoRelative));
            // Line comments read as code to a substring scan; drop them.
            return Regex.Replace(text, @"//[^\n]*", string.Empty);
        }

        private static string MethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, "method not found: " + signature);
            int open = source.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }
            return source.Substring(open);
        }
    }
}
