using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// A route deleted mid-cycle (the Logistics window's Delete, <see cref="RouteStore.RemoveRoute"/>)
    /// AFTER a window took cargo from its source and BEFORE the window that delivers it. No
    /// design document states this outcome (the supply-routes design covers a deleted SOURCE
    /// RECORDING during transit, section 1.3, not a deleted route), so these cells pin TODAY's
    /// behavior: the taken cargo is lost (not refunded, not delivered), the pickup row stays in
    /// the route event log, the remaining escrow is dropped, and the removal now logs the
    /// abandoned cycle. Driven through the production
    /// <see cref="RouteOrchestrator.Tick(double, IRouteRuntimeEnvironment)"/> multi-stop path.
    /// A single-stop loop route has no such gap: its debit and delivery fire in one
    /// <c>EmitLoopCycle</c> at the dock crossing.
    /// </summary>
    [Collection("Sequential")]
    public class RouteRemovedMidCycleTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteRemovedMidCycleTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            RouteEventLog.ResetForTesting();
            Ledger.ResetForTesting();
            RecordingStore.ClearCommittedTreesInternal();
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.DeliveryRowEmitterForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            RouteOrchestrator.PickupDebitApplierForTesting = null;
        }

        public void Dispose()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.DeliveryRowEmitterForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            RouteOrchestrator.PickupDebitApplierForTesting = null;
            RouteStore.ResetForTesting();
            RouteEventLog.ResetForTesting();
            Ledger.ResetForTesting();
            RecordingStore.ClearCommittedTreesInternal();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private sealed class EligibleEnv : IRouteRuntimeEnvironment
        {
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason) { reason = string.Empty; return true; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason) { vessel = null; reason = string.Empty; return true; }
            public bool OriginHasCargo(Route route, out string lackingResource, out double shortfall) { shortfall = 0.0; lackingResource = string.Empty; return true; }
            public bool DestinationHasCapacity(Route route, out string fullResource) { fullResource = string.Empty; return true; }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        // Records every source debit and every delivery the production path asks for.
        private sealed class Seams
        {
            public readonly List<(uint pid, Dictionary<string, double> manifest)> Debits =
                new List<(uint, Dictionary<string, double>)>();
            public int Deliveries;
        }

        private Seams InstallSeams()
        {
            var seams = new Seams();
            RouteOrchestrator.PickupDebitApplierForTesting = (endpoint, manifest, env) =>
            {
                seams.Debits.Add((endpoint.VesselPersistentId, new Dictionary<string, double>(manifest)));
                return new RouteOrchestrator.OriginDebitOutcome
                {
                    ActualDebited = new Dictionary<string, double>(manifest),
                    OriginVesselPid = endpoint.VesselPersistentId,
                };
            };
            RouteOrchestrator.DeliveryRowEmitterForTesting =
                (route, currentUT, env, cycleId, stopIndex, bumpCompletedCycle) =>
                {
                    seams.Deliveries++;
                    RouteEventLog.Add(new RouteEvent
                    {
                        Type = RouteEventType.RouteCargoDelivered,
                        UT = currentUT,
                        RouteId = route.Id,
                        RouteCycleId = cycleId,
                        RouteStopIndex = stopIndex,
                        Sequence = stopIndex * RouteOrchestrator.SeqStride + 3,
                    });
                };
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1400.0,
                cadenceSeconds: 400.0, phaseAnchorUT: 1000.0);
            return seams;
        }

        // A 2-window shuttle: load 400 LF at the refinery (pid 500) at dock 1150, deliver
        // it at the station (pid 600) at dock 1300. Same shape as RouteEscrowFireTests'.
        private static Route Shuttle(string id)
        {
            return new Route
            {
                Id = id,
                Name = "Fuel Shuttle",
                Status = RouteStatus.Active,
                IsKscOrigin = false,
                BackingMissionTreeId = "tree-1",
                RecordedDockUT = 1300.0,
                DockMemberRecordingId = "rec-dock-station",
                LoopAnchorUT = 1000.0,
                LastObservedLoopCycleIndex = -1,
                DispatchInterval = 400.0,
                TransitDuration = 400.0,
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 500u },
                        PickupManifest = new Dictionary<string, double> { { "LiquidFuel", 400.0 } },
                        SegmentIndexBefore = 0,
                        RecordedDockUT = 1150.0,
                        LastFiredCycleIndex = -1,
                    },
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 600u },
                        DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 400.0 } },
                        SegmentIndexBefore = 1,
                        RecordedDockUT = 1300.0,
                        LastFiredCycleIndex = -1,
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-dock-station", TreeId = "tree-1", RouteProofHash = "deadbeef" },
                },
            };
        }

        private static List<RouteEvent> Rows(RouteEventType type) =>
            RouteEventLog.Events.Where(e => e.Type == type).ToList();

        // Fails if: deleting the route after its pickup window took the cargo and before
        // its delivery window either (a) still delivers (a deleted route keeps acting),
        // (b) re-debits or refunds the source, (c) leaks the cycle's escrow, (d) drops the
        // already-written pickup row, or (e) removes the route silently. Today's outcome,
        // pinned: the 400 LF taken at the refinery is LOST - not delivered, not returned.
        [Fact]
        public void RemoveRoute_AfterPickupBeforeDelivery_CargoLost_NotRefunded_Logged()
        {
            Route route = Shuttle("route-del-mid");
            RouteStore.AddRoute(route);
            Seams seams = InstallSeams();
            var env = new EligibleEnv();

            // The refinery window: dispatch + the 400 LF source debit.
            RouteOrchestrator.Tick(1150.0, env);
            Assert.Single(seams.Debits);
            Assert.Equal(500u, seams.Debits[0].pid);
            Assert.Equal(400.0, seams.Debits[0].manifest["LiquidFuel"]);
            Assert.Single(Rows(RouteEventType.RouteDispatched));
            Assert.Single(Rows(RouteEventType.RouteCargoPickedUp));
            Assert.Equal(0, seams.Deliveries);

            // The delete confirm names the loss: cargo is on its way.
            Assert.True(RouteStore.HasUndeliveredCargoInOpenCycle(route, RouteEventLog.Events));
            Assert.Contains("will be lost",
                LogisticsWindowUI.BuildDeleteConfirmBody(route,
                    RouteStore.HasUndeliveredCargoInOpenCycle(route, RouteEventLog.Events)));

            // The player deletes the route mid-transit.
            logLines.Clear();
            Assert.True(RouteStore.RemoveRoute(route.Id));

            // Past the station dock: the deleted route does nothing more.
            RouteOrchestrator.Tick(1350.0, env);
            RouteOrchestrator.Tick(1750.0, env);

            Assert.Equal(0, seams.Deliveries);
            Assert.Empty(Rows(RouteEventType.RouteCargoDelivered));
            Assert.Single(seams.Debits); // no second debit, and no refund path exists
            Assert.Single(Rows(RouteEventType.RouteCargoPickedUp)); // the pickup row stays
            Assert.False(RouteStore.TryGetRoute(route.Id, out _));
            Assert.Equal(0, RouteStore.EscrowRouteCountForTesting);

            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("removed mid-cycle")
                && l.Contains("cycle=cycle-0")
                && l.Contains("pickups=1")
                && l.Contains("deliveries=0")
                && l.Contains("not returned"));
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("removed")
                && !l.Contains("mid-cycle"));
        }

        // Fails if: a route deleted with no open cycle (never dispatched, or between cycles
        // after a completed one) logs an abandoned cycle it does not have.
        [Fact]
        public void RemoveRoute_BetweenCycles_LogsNoAbandonedCycle()
        {
            Route route = Shuttle("route-del-between");
            RouteStore.AddRoute(route);
            Seams seams = InstallSeams();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(1150.0, env);
            RouteOrchestrator.Tick(1350.0, env);
            Assert.Equal(1, seams.Deliveries);
            Assert.Equal(1, route.CompletedCycles);
            Assert.False(RouteStore.HasUndeliveredCargoInOpenCycle(route, RouteEventLog.Events));

            logLines.Clear();
            Assert.True(RouteStore.RemoveRoute(route.Id));
            Assert.DoesNotContain(logLines, l => l.Contains("removed mid-cycle"));
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("removed"));
        }

        // ---------- DescribeAbandonedCycle (pure) ----------

        private static RouteEvent Ev(RouteEventType type, string routeId, string cycleId)
        {
            return new RouteEvent { Type = type, RouteId = routeId, RouteCycleId = cycleId };
        }

        [Fact]
        public void DescribeAbandonedCycle_NoDispatchRowForCurrentCycle_IsNull()
        {
            var route = new Route { Id = "r1", CompletedCycles = 2, SkippedCycles = 1 };
            var events = new List<RouteEvent>
            {
                // Rows of a FINISHED cycle (cycle-2) and of another route's cycle-3.
                Ev(RouteEventType.RouteDispatched, "r1", "cycle-2"),
                Ev(RouteEventType.RouteCargoPickedUp, "r1", "cycle-2"),
                Ev(RouteEventType.RouteDispatched, "r2", "cycle-3"),
            };
            Assert.Null(RouteStore.DescribeAbandonedCycle(route, events));
            Assert.Null(RouteStore.DescribeAbandonedCycle(route, new List<RouteEvent>()));
            Assert.Null(RouteStore.DescribeAbandonedCycle(null, events));
            Assert.Null(RouteStore.DescribeAbandonedCycle(route, null));
        }

        // Fails if: the confirm's loss sentence shows for a cycle with nothing taken yet, for
        // a finished cycle, or for another route's rows.
        [Fact]
        public void HasUndeliveredCargoInOpenCycle_Truths()
        {
            var route = new Route { Id = "r1", CompletedCycles = 2, SkippedCycles = 1 };
            Assert.False(RouteStore.HasUndeliveredCargoInOpenCycle(null, new List<RouteEvent>()));
            Assert.False(RouteStore.HasUndeliveredCargoInOpenCycle(route, null));

            // Dispatched, nothing taken yet.
            var events = new List<RouteEvent> { Ev(RouteEventType.RouteDispatched, "r1", "cycle-3") };
            Assert.False(RouteStore.HasUndeliveredCargoInOpenCycle(route, events));

            // Taken on a finished cycle or by another route: not this trip.
            events.Add(Ev(RouteEventType.RouteCargoPickedUp, "r1", "cycle-2"));
            events.Add(Ev(RouteEventType.RouteCargoDebited, "r2", "cycle-3"));
            Assert.False(RouteStore.HasUndeliveredCargoInOpenCycle(route, events));

            // An origin debit or a pickup on the open cycle: cargo on its way.
            events.Add(Ev(RouteEventType.RouteCargoDebited, "r1", "cycle-3"));
            Assert.True(RouteStore.HasUndeliveredCargoInOpenCycle(route, events));
            var pickupOnly = new List<RouteEvent>
            {
                Ev(RouteEventType.RouteDispatched, "r1", "cycle-3"),
                Ev(RouteEventType.RouteCargoPickedUp, "r1", "cycle-3"),
            };
            Assert.True(RouteStore.HasUndeliveredCargoInOpenCycle(route, pickupOnly));

            // Taken but not dispatched on the open cycle: no open cycle, nothing to lose.
            var noDispatch = new List<RouteEvent> { Ev(RouteEventType.RouteCargoPickedUp, "r1", "cycle-3") };
            Assert.False(RouteStore.HasUndeliveredCargoInOpenCycle(route, noDispatch));
        }

        [Fact]
        public void DescribeAbandonedCycle_OpenCycle_CountsOnlyItsOwnRows()
        {
            var route = new Route { Id = "r1", CompletedCycles = 2, SkippedCycles = 1 };
            var events = new List<RouteEvent>
            {
                Ev(RouteEventType.RouteDispatched, "r1", "cycle-3"),
                Ev(RouteEventType.RouteCargoDebited, "r1", "cycle-3"),
                Ev(RouteEventType.RouteCargoPickedUp, "r1", "cycle-3"),
                Ev(RouteEventType.RouteCargoPickedUp, "r1", "cycle-3"),
                Ev(RouteEventType.RouteCargoDelivered, "r1", "cycle-3"),
                Ev(RouteEventType.RouteCargoPickedUp, "r1", "cycle-2"),
                Ev(RouteEventType.RouteCargoPickedUp, "r2", "cycle-3"),
                null,
            };
            string s = RouteStore.DescribeAbandonedCycle(route, events);
            Assert.NotNull(s);
            Assert.Contains("cycle=cycle-3", s);
            Assert.Contains("originDebits=1", s);
            Assert.Contains("pickups=2", s);
            Assert.Contains("deliveries=1", s);
        }
    }
}
