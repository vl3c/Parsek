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
            RouteEventLog.ResetForTesting();
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
            Assert.Equal(40.0, RouteDeliveryPlanner.CapBySupplyHoldback(100.0, deficit, LF), 9);
            Assert.Equal(100.0, RouteDeliveryPlanner.CapBySupplyHoldback(100.0, deficit, Ox), 9);
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
                RouteDeliveryPlanner.CapBySupplyHoldback(50.0, deficit, LF), 9);
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

        // catches: a deficit charged twice, or the cargo aboard withheld from the first
        // stop. The pickup is 60 short (40 of 100), then two deliveries of 50 each: the
        // second can absorb 50 of the 60, so the first holds back 10 and delivers the 40
        // aboard; the walk counts the 10 it kept, so the second sees a deficit of 50 and
        // delivers nothing. Total delivered 40 == total picked up 40.
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

            double firstDelivered = CappedAt(route, 1, events, 50.0);
            Assert.Equal(40.0, firstDelivered, 9);

            events.Add(Row(RouteEventType.RouteCargoDelivered, route.Id, "cycle-0", 1, 1 * S + 3,
                firstDelivered > 0.0 ? M((LF, firstDelivered)) : null, M((LF, 50.0))));
            var atSecond = RouteOrchestrator.ComputeCycleSupplyDeficit(route, "cycle-0", 2 * S + 3, events);
            double secondDelivered = CappedAt(route, 2, events, 50.0);

            Assert.Equal(50.0, atSecond[LF], 9);
            Assert.Equal(0.0, secondDelivered, 9);
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
            Assert.Equal(50.0, RouteDeliveryPlanner.CapBySupplyHoldback(50.0, atSecond, LF), 9);
        }

        // ---- the cap is physical: the cargo aboard goes to the first stop ----------

        /// <summary>
        /// The amount the delivery at <paramref name="stopIndex"/> may write of its recorded
        /// LF, through the production chain (deficit, then holdback, then cap).
        /// </summary>
        private static double CappedAt(Route route, int stopIndex, IReadOnlyList<RouteEvent> events,
            double recorded, string cycleId = "cycle-0")
        {
            var deficit = RouteOrchestrator.ComputeCycleSupplyDeficit(
                route, cycleId, stopIndex * S + 3, events);
            var holdback = RouteOrchestrator.ComputeDeliveryHoldback(route, stopIndex, deficit);
            return RouteDeliveryPlanner.CapBySupplyHoldback(recorded, holdback, LF);
        }

        /// <summary>
        /// Docked origin (stop 0, debit row on its window) then two deliveries of 50 LF.
        /// The origin debit took <paramref name="debited"/> of the 100 the route records.
        /// </summary>
        private static (Route route, List<RouteEvent> events) DebitThenTwoDeliveries(double debited)
        {
            var route = new Route
            {
                Id = "route-C",
                CostManifest = M((LF, 100.0)),
                Stops = new List<RouteStop>
                {
                    new RouteStop { DeliveryManifest = M((LF, 50.0)) },
                    new RouteStop { DeliveryManifest = M((LF, 50.0)) },
                },
            };
            var events = new List<RouteEvent>
            {
                Row(RouteEventType.RouteDispatched, route.Id, "cycle-0", 0, 0, null),
                Row(RouteEventType.RouteCargoDebited, route.Id, "cycle-0", 0, 1,
                    M((LF, debited)), debited < 100.0 ? M((LF, 100.0)) : null),
            };
            return (route, events);
        }

        private static RouteEvent Delivered(Route route, int stopIndex, double actual, double recorded)
            => Row(RouteEventType.RouteCargoDelivered, route.Id, "cycle-0", stopIndex, stopIndex * S + 3,
                actual > 0.0 ? M((LF, actual)) : null, actual < recorded ? M((LF, recorded)) : null);

        // catches: the whole deficit landing on the FIRST delivery. The debit is 40
        // short (60 of 100); stop 0 records 50 and stop 1 records 50. The 60 aboard
        // gives stop 0 its full 50 and stop 1 the remaining 10.
        [Fact]
        public void Holdback_ShortDebit_FirstStopGetsWhatIsAboard()
        {
            var (route, events) = DebitThenTwoDeliveries(60.0);

            var deficitAtFirst = RouteOrchestrator.ComputeCycleSupplyDeficit(route, "cycle-0", 3, events);
            Assert.Equal(40.0, deficitAtFirst[LF], 9);
            Assert.Empty(RouteOrchestrator.ComputeDeliveryHoldback(route, 0, deficitAtFirst));
            double first = CappedAt(route, 0, events, 50.0);
            Assert.Equal(50.0, first, 9);

            events.Add(Delivered(route, 0, first, 50.0));
            double second = CappedAt(route, 1, events, 50.0);

            Assert.Equal(10.0, second, 9);
            Assert.Equal(60.0, first + second, 9);
        }

        // catches: a full first destination costing the second stop cargo. Stop 0's
        // destination takes nothing, so its 50 stays aboard and stop 1 gets its full 50;
        // a partly full stop 0 (20 taken) leaves 40 aboard for stop 1. Never more in
        // total than the 60 the debit took.
        [Theory]
        [InlineData(0.0, 50.0)]
        [InlineData(20.0, 40.0)]
        public void Holdback_FullFirstDestination_LeavesMoreAboardForTheSecond(
            double firstTook, double expectedSecond)
        {
            var (route, events) = DebitThenTwoDeliveries(60.0);
            Assert.Equal(50.0, CappedAt(route, 0, events, 50.0), 9);
            events.Add(Delivered(route, 0, firstTook, 50.0));

            double second = CappedAt(route, 1, events, 50.0);

            Assert.Equal(expectedSecond, second, 9);
            Assert.True(firstTook + second <= 60.0 + 1e-9);
        }

        // catches: cargo lost to a full SECOND destination that the first stop could
        // have taken. With the whole deficit on the first stop, a full stop 1 would
        // leave 40 of the 60 aboard undelivered; now stop 0 takes its 50.
        [Fact]
        public void Holdback_FullSecondDestination_FirstStopAlreadyTookItsShare()
        {
            var (route, events) = DebitThenTwoDeliveries(60.0);
            double first = CappedAt(route, 0, events, 50.0);
            events.Add(Delivered(route, 0, first, 50.0));
            double secondAllowed = CappedAt(route, 1, events, 50.0);
            // Stop 1's destination is full: the planner clamps to zero free capacity.
            var plan = RouteDeliveryPlanner.PrepareDelivery(route, 1, new FixedCapacityProbe(0.0),
                RouteOrchestrator.ComputeDeliveryHoldback(route, 1,
                    RouteOrchestrator.ComputeCycleSupplyDeficit(route, "cycle-0", 1 * S + 3, events)));

            Assert.Equal(50.0, first, 9);
            Assert.Equal(10.0, secondAllowed, 9);
            Assert.Equal(0.0, plan.Resources[0].Available, 9);
        }

        // catches: a later PICKUP counted as cargo already aboard. The debit is
        // entirely short (0 of 100); stop 0 records 50, stop 1 picks up 100 and
        // delivers 50. Stop 1's delivery comes out of its own pickup, so nothing can
        // absorb the debit's deficit and stop 0 delivers nothing; when the stop 1
        // pickup is short too, stop 1 delivers nothing either. Never more than taken on.
        [Theory]
        [InlineData(100.0, 50.0)]
        [InlineData(0.0, 0.0)]
        public void Holdback_LaterPickup_DoesNotAbsorbTheEarlierDeficit(
            double laterPickedUp, double expectedSecond)
        {
            var route = new Route
            {
                Id = "route-D",
                CostManifest = M((LF, 50.0)),
                Stops = new List<RouteStop>
                {
                    new RouteStop { DeliveryManifest = M((LF, 50.0)) },
                    new RouteStop { PickupManifest = M((LF, 100.0)), DeliveryManifest = M((LF, 50.0)) },
                },
            };
            Assert.Equal(0.0, RouteOrchestrator.LaterDeliveriesAbsorbable(route, 0, LF), 9);
            var events = new List<RouteEvent>
            {
                Row(RouteEventType.RouteCargoDebited, route.Id, "cycle-0", 0, 1, null, M((LF, 50.0))),
            };

            double first = CappedAt(route, 0, events, 50.0);
            Assert.Equal(0.0, first, 9);
            events.Add(Delivered(route, 0, first, 50.0));
            events.Add(Row(RouteEventType.RouteCargoPickedUp, route.Id, "cycle-0", 1, 1 * S + 2,
                laterPickedUp > 0.0 ? M((LF, laterPickedUp)) : null,
                laterPickedUp < 100.0 ? M((LF, 100.0)) : null));
            double second = CappedAt(route, 1, events, 50.0);

            Assert.Equal(expectedSecond, second, 9);
            Assert.True(first + second <= laterPickedUp + 1e-9);
        }

        // catches: the running total ignoring the window order: a later stop's pickup
        // only offsets deliveries at or after it, so the absorbable amount is the
        // largest running total read at each later delivery.
        [Fact]
        public void LaterDeliveriesAbsorbable_ReadsTheRunningTotalInWindowOrder()
        {
            var route = new Route
            {
                Id = "route-E",
                Stops = new List<RouteStop>
                {
                    new RouteStop { DeliveryManifest = M((LF, 10.0)) },
                    new RouteStop { DeliveryManifest = M((LF, 30.0)) },
                    new RouteStop { PickupManifest = M((LF, 100.0)) },
                    new RouteStop { DeliveryManifest = M((LF, 20.0)) },
                    null,
                },
            };

            Assert.Equal(30.0, RouteOrchestrator.LaterDeliveriesAbsorbable(route, 0, LF), 9);
            Assert.Equal(0.0, RouteOrchestrator.LaterDeliveriesAbsorbable(route, 1, LF), 9);
            Assert.Equal(20.0, RouteOrchestrator.LaterDeliveriesAbsorbable(route, 2, LF), 9);
            Assert.Equal(0.0, RouteOrchestrator.LaterDeliveriesAbsorbable(route, 3, LF), 9);
            Assert.Equal(0.0, RouteOrchestrator.LaterDeliveriesAbsorbable(route, 0, Ox), 9);
            Assert.Equal(0.0, RouteOrchestrator.LaterDeliveriesAbsorbable(null, 0, LF), 9);
            Assert.Empty(RouteOrchestrator.ComputeDeliveryHoldback(route, 0, null));
        }

        // catches: a harvest route capped. A harvest-origin route books an empty debit
        // row, and a docked origin whose debit the harvest reduced books a debit below
        // the deliveries; neither is a short supply, so neither delivery is held back.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Holdback_HarvestRoute_IsNeverCapped(bool harvestOrigin)
        {
            var route = new Route
            {
                Id = "route-H",
                IsHarvestOrigin = harvestOrigin,
                CostManifest = harvestOrigin ? M() : M((LF, 30.0)),
                Stops = new List<RouteStop>
                {
                    new RouteStop { DeliveryManifest = M((LF, 50.0)) },
                    new RouteStop { DeliveryManifest = M((LF, 50.0)) },
                },
            };
            var events = new List<RouteEvent>
            {
                Row(RouteEventType.RouteDispatched, route.Id, "cycle-0", 0, 0, null),
                Row(RouteEventType.RouteCargoDebited, route.Id, "cycle-0", 0, 1,
                    harvestOrigin ? null : M((LF, 30.0))),
            };

            Assert.Equal(50.0, CappedAt(route, 0, events, 50.0), 9);
            events.Add(Delivered(route, 0, 50.0, 50.0));
            Assert.Equal(50.0, CappedAt(route, 1, events, 50.0), 9);
        }

        // catches: a single-delivery route behaving differently from before: with no
        // later delivery the whole deficit is held back at its one stop.
        [Fact]
        public void Holdback_SingleDelivery_HoldsBackTheWholeDeficit()
        {
            var route = new Route
            {
                Id = "route-A",
                Stops = new List<RouteStop> { new RouteStop { DeliveryManifest = M((LF, 50.0)) } },
            };
            var events = new List<RouteEvent>
            {
                Row(RouteEventType.RouteCargoDebited, route.Id, "cycle-3", -1, 1,
                    M((LF, 30.0)), M((LF, 50.0))),
            };

            var deficit = RouteOrchestrator.ComputeCycleSupplyDeficit(route, "cycle-3", 3, events);
            var holdback = RouteOrchestrator.ComputeDeliveryHoldback(route, 0, deficit);

            Assert.Equal(20.0, holdback[LF], 9);
            Assert.Equal(30.0, RouteDeliveryPlanner.CapBySupplyHoldback(50.0, holdback, LF), 9);
        }

        // catches: the cap changing across a save / reload between the two deliveries.
        // Every input is the persisted route event log and the route's stops, so the
        // second delivery reads the same 10 from the reloaded log and route.
        [Fact]
        public void Holdback_SaveReloadMidCycle_SecondDeliveryUnchanged()
        {
            var (route, events) = DebitThenTwoDeliveries(60.0);
            double first = CappedAt(route, 0, events, 50.0);
            events.Add(Delivered(route, 0, first, 50.0));
            double before = CappedAt(route, 1, events, 50.0);

            RouteEventLog.ResetForTesting();
            RouteEventLog.AddRange(events);
            var parsek = new ConfigNode("PARSEK");
            RouteEventLog.SaveInto(parsek);
            var routeNode = new ConfigNode("ROUTE");
            RouteCodec.SerializeInto(route, routeNode);
            RouteEventLog.ResetForTesting();
            Assert.Empty(RouteEventLog.Events);

            RouteEventLog.LoadFrom(parsek);
            Route reloaded = RouteCodec.DeserializeFrom(routeNode);
            double after = CappedAt(reloaded, 1, RouteEventLog.Events, 50.0);

            Assert.Equal(10.0, before, 9);
            Assert.Equal(before, after, 9);
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
            Assert.Contains("ComputeDeliveryHoldback(", src);
            Assert.Contains("RouteDeliveryPlanner.PrepareDelivery(route, stopIndex, probe, supplyHoldback)", src);
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
