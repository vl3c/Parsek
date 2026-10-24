using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// LOOP-ROUTE-RESCAN-DOES-NOTHING: a loop route whose endpoint is lost at a delivery goes
    /// EndpointLost, and its tick skips every route that is not ghost-driving, so the old
    /// Re-scan (which only cleared the legacy retry gate) left it stopped for good. A Re-scan
    /// that finds every stop again now restarts the route through
    /// <see cref="RouteOrchestrator.TryRecoverEndpointLostAfterRescan"/>.
    /// </summary>
    [Collection("Sequential")]
    public class RouteEndpointRescanRecoveryTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private readonly List<string> logLines = new List<string>();

        public RouteEndpointRescanRecoveryTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
        }

        public void Dispose()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // span [1000, 1300], cadence 300, dock at 1150: one crossing per 300 s.
        private static void InstallUnit()
        {
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1300.0,
                cadenceSeconds: 300.0, phaseAnchorUT: 1000.0);
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => unit;
        }

        private static void InstallFakeDelivery()
        {
            RouteOrchestrator.DeliveryApplierForTesting = (route, currentUT, env) =>
            {
                RouteEventLog.Add(new RouteEvent
                {
                    Type = RouteEventType.RouteCargoDelivered,
                    UT = currentUT,
                    RouteId = route.Id,
                    RouteCycleId = "cycle-" + (route.CompletedCycles + route.SkippedCycles).ToString(IC),
                    RouteStopIndex = 0,
                });
                route.CompletedCycles += 1;
                route.PendingDeliveryUT = null;
                route.PendingStopIndex = -1;
                route.TransitionTo(RouteStatus.Active, "delivered-loop-fake");
            };
        }

        private static Route BuildLoopRoute(RouteStatus status = RouteStatus.Active)
        {
            return new Route
            {
                Id = "route-rescan",
                Status = status,
                IsKscOrigin = true,
                BackingMissionTreeId = "tree-1",
                RecordedDockUT = 1150.0,
                DockMemberRecordingId = "rec-dock",
                LoopAnchorUT = 1000.0,
                LastObservedLoopCycleIndex = -1,
                DispatchInterval = 300.0,
                TransitDuration = 300.0,
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 42u },
                        DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-dock", TreeId = "tree-1", RouteProofHash = "deadbeef" },
                },
            };
        }

        private sealed class Env : IRouteRuntimeEnvironment
        {
            public bool DeliveryVesselFound = true;
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason) { reason = string.Empty; return true; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason)
            {
                vessel = null;
                reason = DeliveryVesselFound ? string.Empty : "no-live-vessels";
                return DeliveryVesselFound;
            }
            public bool OriginHasCargo(Route route, out string lackingResource, out double shortfall) { shortfall = 0.0; lackingResource = string.Empty; return true; }
            public bool DestinationHasCapacity(Route route, out string fullResource) { fullResource = string.Empty; return true; }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        // catches: the bug itself. A loop route stopped by an endpoint loss at delivery
        // stayed stopped after a successful Re-scan, because the loop tick skips a route
        // that is not ghost-driving and the old Re-scan only cleared the legacy retry gate.
        [Fact]
        public void LoopRoute_LostAtDelivery_RescanRestartsIt_AndItDeliversAgain()
        {
            var route = BuildLoopRoute();
            RouteStore.AddRoute(route);
            InstallUnit();
            var env = new Env { DeliveryVesselFound = false };

            RouteOrchestrator.Tick(1150.0, env);
            Assert.Equal(RouteStatus.EndpointLost, route.Status);

            // The endpoint comes back, but the stopped route does not run on its own.
            env.DeliveryVesselFound = true;
            InstallFakeDelivery();
            RouteOrchestrator.Tick(1450.0, env);
            Assert.Equal(RouteStatus.EndpointLost, route.Status);
            Assert.DoesNotContain(RouteEventLog.Events, e => e.Type == RouteEventType.RouteCargoDelivered);

            Assert.True(RouteOrchestrator.TryRecoverEndpointLostAfterRescan(route, 1500.0));

            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.Equal(-1, route.LastObservedLoopCycleIndex);
            Assert.Equal(1500.0, route.LoopAnchorUT);
            Assert.Null(route.NextEligibilityCheckUT);
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.None, route.LastHoldKind);
            var resumed = RouteEventLog.Events.SingleOrDefault(e => e.Type == RouteEventType.RouteResumed);
            Assert.NotNull(resumed);
            Assert.Equal(1500.0, resumed.UT);
            Assert.Equal(RouteOrchestrator.EndpointRescanReason, resumed.RouteEndpointReason);
            // The lost cycle was dispatched but never delivered: counted skipped, so the
            // next crossing opens cycle-1 instead of replay-skipping cycle-0.
            Assert.Equal(0, route.CompletedCycles);
            Assert.Equal(1, route.SkippedCycles);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("EndpointRescan:")
                && l.Contains("loopRoute=1") && l.Contains("EndpointLost->Active"));

            RouteOrchestrator.Tick(1750.0, env);
            Assert.Contains(RouteEventLog.Events, e => e.Type == RouteEventType.RouteCargoDelivered
                && e.UT >= 1500.0 && e.RouteCycleId == "cycle-1");
        }

        // catches: a multi-stop route's partly delivered cycle counted skipped on the
        // restart. Its crossing path resumes a logged dispatch and fires only the windows
        // still owed, so a skip would open a new cycle: a second origin debit and a second
        // load for the stop that already received this cycle's cargo.
        [Fact]
        public void MultiStopRoute_PartlyDeliveredLostCycle_IsNotSkipped()
        {
            var route = BuildLoopRoute(RouteStatus.EndpointLost);
            route.Stops.Add(new RouteStop
            {
                Endpoint = new RouteEndpoint { VesselPersistentId = 43u },
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 50.0 } },
            });
            RouteEventLog.Add(new RouteEvent
            {
                Type = RouteEventType.RouteDispatched, UT = 1100.0, RouteId = route.Id,
                RouteCycleId = "cycle-0", RouteStopIndex = 0,
            });
            RouteEventLog.Add(new RouteEvent
            {
                Type = RouteEventType.RouteCargoDelivered, UT = 1150.0, RouteId = route.Id,
                RouteCycleId = "cycle-0", RouteStopIndex = 0,
            });

            Assert.True(RouteOrchestrator.TryRecoverEndpointLostAfterRescan(route, 1500.0));

            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.Equal(0, route.CompletedCycles);
            Assert.Equal(0, route.SkippedCycles);
            Assert.DoesNotContain(logLines, l => l.Contains("counted skipped"));
        }

        // catches: a recovery un-pausing a route the kept timeline has paused (a rewind
        // writes that verdict to PreMissingStatus while the route is EndpointLost).
        [Fact]
        public void LoopRoute_PausedBaseline_RecoversToPaused_WithNoMarker()
        {
            var route = BuildLoopRoute(RouteStatus.EndpointLost);
            route.PreMissingStatus = RouteStatus.Paused;
            route.LastObservedLoopCycleIndex = 4;

            Assert.True(RouteOrchestrator.TryRecoverEndpointLostAfterRescan(route, 1500.0));

            Assert.Equal(RouteStatus.Paused, route.Status);
            Assert.Equal(RouteStatus.Active, route.PreMissingStatus);
            Assert.Equal(4, route.LastObservedLoopCycleIndex);
            Assert.Empty(RouteEventLog.Events);
        }

        // catches: an armed Send Once lost on the recovery (the player asked for that run).
        [Fact]
        public void LoopRoute_Recovery_KeepsAnArmedSendOnce()
        {
            var route = BuildLoopRoute(RouteStatus.EndpointLost);
            route.SendOnceArmed = true;

            Assert.True(RouteOrchestrator.TryRecoverEndpointLostAfterRescan(route, 1500.0));

            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.True(route.SendOnceArmed);
        }

        // catches: the legacy (non-loop) route losing its own recovery path: its evaluator
        // re-resolves the endpoint and flips the status, so a re-scan only clears the gate.
        [Fact]
        public void NonLoopRoute_OnlyClearsTheRetryGate()
        {
            var route = BuildLoopRoute(RouteStatus.EndpointLost);
            route.BackingMissionTreeId = null;
            route.NextEligibilityCheckUT = 1530.0;
            route.LastObservedLoopCycleIndex = 4;

            Assert.True(RouteOrchestrator.TryRecoverEndpointLostAfterRescan(route, 1500.0));

            Assert.Equal(RouteStatus.EndpointLost, route.Status);
            Assert.Null(route.NextEligibilityCheckUT);
            Assert.Equal(4, route.LastObservedLoopCycleIndex);
            Assert.Empty(RouteEventLog.Events);
        }

        [Theory]
        [InlineData((int)RouteStatus.Active)]
        [InlineData((int)RouteStatus.Paused)]
        [InlineData((int)RouteStatus.SourceChanged)]
        public void NotEndpointLost_IsRefused(int statusValue)
        {
            var status = (RouteStatus)statusValue;
            var route = BuildLoopRoute(status);

            Assert.False(RouteOrchestrator.TryRecoverEndpointLostAfterRescan(route, 1500.0));

            Assert.Equal(status, route.Status);
            Assert.Empty(RouteEventLog.Events);
            Assert.Contains(logLines, l => l.Contains("EndpointRescan:") && l.Contains("only an EndpointLost route recovers"));
        }
    }
}
