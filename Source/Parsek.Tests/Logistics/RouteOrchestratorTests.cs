using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins <see cref="RouteOrchestrator.Tick(double, IRouteRuntimeEnvironment)"/>
    /// behavior. Drives the orchestrator with a fully-faked
    /// <see cref="IRouteRuntimeEnvironment"/> so the tests stay xUnit-only and
    /// never touch KSP statics (Planetarium / Funding / PartLoader). The
    /// no-env <c>Tick(double)</c> overload — and its <see cref="LiveRouteRuntimeEnvironment"/>
    /// production env — are out of scope here; that path is exercised inside
    /// KSP via runtime tests in a later phase.
    /// </summary>
    [Collection("Sequential")]
    public class RouteOrchestratorTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteOrchestratorTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ==================================================================
        // Fake env
        // ==================================================================

        private sealed class FakeRouteRuntimeEnvironment : IRouteRuntimeEnvironment
        {
            public bool IsCareer { get; set; }
            public bool EndpointResolvable { get; set; } = true;
            public string EndpointResolveFailureReason { get; set; } = "pid-miss";
            public bool OriginHasCargoResult { get; set; } = true;
            public string OriginLackingResource { get; set; } = "LiquidFuel";
            public bool KscFundsAvailableResult { get; set; } = true;
            public double KscFundsShortfall { get; set; } = 0.0;
            public bool DestinationHasCapacityResult { get; set; } = true;
            public string DestinationFullResource { get; set; } = "Ore";
            public bool RouteHasValidSourcesResult { get; set; } = true;
            public Action OnAnyCall { get; set; }

            // When non-null, any env method that receives a Route argument
            // throws InvalidOperationException if route.Id matches. Keying on
            // route id (instead of a global call counter) keeps the test
            // robust to evaluator-call-order reshuffles: a future evaluator
            // tweak that reorders origin/funds/destination/sources checks
            // would no longer silently shift which route throws.
            public string ThrowOnRouteId { get; set; }

            public int OriginHasCargoCalls;
            public int KscFundsAvailableCalls;

            private void ThrowIfMatch(Route route)
            {
                if (ThrowOnRouteId != null && route != null && route.Id == ThrowOnRouteId)
                    throw new InvalidOperationException("synthetic-env-failure-" + route.Id);
            }

            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason)
            {
                OnAnyCall?.Invoke();
                reason = EndpointResolvable ? string.Empty : EndpointResolveFailureReason;
                return EndpointResolvable;
            }

            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason)
            {
                // Orchestrator-dispatch tests in this class never exercise the
                // delivery applier's vessel resolution; delivery-applier tests
                // live in RouteOrchestratorDeliveryTests with their own fake.
                // Surface a null Vessel here to mirror the dispatch-only
                // resolver shape — instantiating a real Vessel would drag in
                // KSP statics and break the xUnit-only contract.
                vessel = null;
                return TryResolveEndpoint(endpoint, out reason);
            }

            public bool OriginHasCargo(Route route, out string lackingResource)
            {
                OnAnyCall?.Invoke();
                ThrowIfMatch(route);
                OriginHasCargoCalls++;
                lackingResource = OriginHasCargoResult ? string.Empty : OriginLackingResource;
                return OriginHasCargoResult;
            }

            public bool KscFundsAvailable(Route route, out double shortfall)
            {
                OnAnyCall?.Invoke();
                ThrowIfMatch(route);
                KscFundsAvailableCalls++;
                shortfall = KscFundsAvailableResult ? 0.0 : KscFundsShortfall;
                return KscFundsAvailableResult;
            }

            public bool DestinationHasCapacity(Route route, out string fullResource)
            {
                OnAnyCall?.Invoke();
                ThrowIfMatch(route);
                fullResource = DestinationHasCapacityResult ? string.Empty : DestinationFullResource;
                return DestinationHasCapacityResult;
            }

            public bool RouteHasValidSourcesInErs(Route route)
            {
                OnAnyCall?.Invoke();
                ThrowIfMatch(route);
                return RouteHasValidSourcesResult;
            }
        }

        // ==================================================================
        // Fixture builders
        // ==================================================================

        private static Route BuildActiveDueKscRoute(
            string id = "route-1",
            double nextDispatchUT = 100.0,
            double dispatchInterval = 3600.0,
            double transitDuration = 60.0,
            Dictionary<string, double> costManifest = null)
        {
            return new Route
            {
                Id = id,
                Status = RouteStatus.Active,
                IsKscOrigin = true,
                NextDispatchUT = nextDispatchUT,
                DispatchInterval = dispatchInterval,
                TransitDuration = transitDuration,
                CostManifest = costManifest ?? new Dictionary<string, double>
                {
                    { "LiquidFuel", 100.0 },
                    { "Oxidizer", 120.0 },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef
                    {
                        RecordingId = "rec-" + id,
                        TreeId = "tree-1",
                        RouteProofHash = "deadbeef",
                    },
                },
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 42u },
                        SegmentIndexBefore = 0,
                        DeliveryOffsetSeconds = 0.0,
                    },
                },
            };
        }

        // ==================================================================
        // Tests
        // ==================================================================

        // catches: empty-store ticks logging noise or throwing.
        [Fact]
        public void Tick_NoRoutes_NoOp_NoLogs()
        {
            var env = new FakeRouteRuntimeEnvironment();

            RouteOrchestrator.Tick(200.0, env);

            // No routes means no summary log, no per-route work, no exceptions.
            Assert.DoesNotContain(logLines, l => l.Contains("[Route]") && l.Contains("Tick:"));
            Assert.Empty(Ledger.Actions);
        }

        // catches: not-due routes mutating state.
        [Fact]
        public void Tick_RoutesNotDue_NoStatusChanges()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 500.0);
            RouteStore.AddRoute(route);
            var env = new FakeRouteRuntimeEnvironment();

            RouteOrchestrator.Tick(100.0, env);

            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.Equal(500.0, route.NextDispatchUT);
            Assert.Null(route.CurrentCycleStartUT);
            Assert.Empty(Ledger.Actions);
        }

        // catches: dispatch path failing to flip status / advance schedule.
        [Fact]
        public void Tick_DueRoute_TransitionsToInTransit_AndAdvancesNextDispatchUT()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 100.0, dispatchInterval: 3600.0);
            RouteStore.AddRoute(route);
            var env = new FakeRouteRuntimeEnvironment();

            RouteOrchestrator.Tick(200.0, env);

            Assert.Equal(RouteStatus.InTransit, route.Status);
            Assert.Equal(200.0, route.CurrentCycleStartUT);
            Assert.Equal(200.0 + 3600.0, route.NextDispatchUT);
            Assert.Null(route.NextEligibilityCheckUT);
        }

        // catches: item-6 contract broken — arrival not setting PendingDeliveryUT
        // or flipping status away from InTransit prematurely.
        [Fact]
        public void Tick_InTransitArrival_SetsPendingDeliveryUT_StatusStaysInTransit()
        {
            var route = BuildActiveDueKscRoute();
            route.Status = RouteStatus.InTransit;
            route.CurrentCycleStartUT = 100.0;
            route.TransitDuration = 60.0;
            route.PendingDeliveryUT = null;
            RouteStore.AddRoute(route);
            var env = new FakeRouteRuntimeEnvironment();

            RouteOrchestrator.Tick(200.0, env);

            Assert.True(route.PendingDeliveryUT.HasValue);
            Assert.Equal(200.0, route.PendingDeliveryUT.Value);
            Assert.Equal(0, route.PendingStopIndex);
            // CRITICAL: the arrival applier runs BEFORE Phase B's delivery
            // hook checks pending state, so within this single tick the status
            // stays InTransit. The next tick where PendingDeliveryUT <=
            // currentUT applies the delivery (covered by
            // RouteOrchestratorDeliveryTests).
            Assert.Equal(RouteStatus.InTransit, route.Status);
        }

        // catches: a pending delivery whose UT lies in the past being IGNORED.
        // Item 6 Phase B added the delivery applier hook; a tick where
        // PendingDeliveryUT <= currentUT must consume the pending state. This
        // test goes through the full Tick → ProcessOneRoute → ApplyDelivery
        // path; the orchestrator-level fake can't supply a non-null Vessel
        // reference (xUnit-only contract), so ApplyDelivery's null-vessel
        // guard treats it as endpoint-lost-at-delivery. That's the correct
        // production behavior when the destination vessel is unresolvable at
        // delivery time — funds NOT debited, RouteCargoDelivered NOT emitted,
        // status flipped to EndpointLost. The happy-path (live Vessel)
        // coverage lives in RouteOrchestratorDeliveryTests via the testable
        // ApplyDeliveryFromPlan helper.
        [Fact]
        public void Tick_PendingDeliveryDue_NullVessel_RoutesToEndpointLost()
        {
            var route = BuildActiveDueKscRoute();
            route.Status = RouteStatus.InTransit;
            route.CurrentCycleStartUT = 100.0;
            route.TransitDuration = 60.0;
            route.PendingDeliveryUT = 150.0; // earlier tick already set it
            route.PendingStopIndex = 0;
            // Push NextDispatchUT well into the future so the post-delivery
            // eval doesn't re-fire on the same tick.
            route.NextDispatchUT = 1_000_000.0;
            RouteStore.AddRoute(route);
            // Default fake env returns (resolvable=true, vessel=null) which is
            // the test-mode signal for ApplyDelivery to abort to EndpointLost.
            var env = new FakeRouteRuntimeEnvironment();

            RouteOrchestrator.Tick(200.0, env);

            // Pending fields cleared, status flipped, EndpointLost row emitted,
            // NO RouteCargoDelivered row, NO CompletedCycles increment.
            Assert.False(route.PendingDeliveryUT.HasValue);
            Assert.Equal(-1, route.PendingStopIndex);
            Assert.Equal(RouteStatus.EndpointLost, route.Status);
            Assert.Equal(0, route.CompletedCycles);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.RouteCargoDelivered);
            Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.RouteEndpointLost);
        }

        // catches: wait state silently advancing NextDispatchUT (would let a
        // resource-short route fast-forward through cycle slots).
        [Fact]
        public void Tick_WaitResources_SetsRetry_AndKeepsNextDispatchUT()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 100.0);
            // Force non-KSC so OriginHasCargo is consulted by the evaluator.
            route.IsKscOrigin = false;
            // For non-KSC the evaluator runs endpoint resolution on the origin too,
            // so wire the env to keep the origin resolvable.
            RouteStore.AddRoute(route);
            var env = new FakeRouteRuntimeEnvironment
            {
                OriginHasCargoResult = false,
                OriginLackingResource = "LiquidFuel",
            };

            RouteOrchestrator.Tick(200.0, env);

            Assert.Equal(RouteStatus.WaitingForResources, route.Status);
            Assert.True(route.NextEligibilityCheckUT.HasValue);
            // NextDispatchUT MUST NOT advance.
            Assert.Equal(100.0, route.NextDispatchUT);
        }

        // catches: §10.4 regression at the orchestrator level. DestinationFull
        // must hold the cycle slot.
        [Fact]
        public void Tick_DestinationFull_DoesNotAdvanceNextDispatchUT()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 100.0);
            RouteStore.AddRoute(route);
            var env = new FakeRouteRuntimeEnvironment
            {
                DestinationHasCapacityResult = false,
                DestinationFullResource = "Ore",
            };

            RouteOrchestrator.Tick(200.0, env);

            Assert.Equal(RouteStatus.DestinationFull, route.Status);
            Assert.True(route.NextEligibilityCheckUT.HasValue);
            Assert.Equal(100.0, route.NextDispatchUT);
        }

        // catches: a tick on an empty store STILL emitting noise; a tick on a
        // populated store with paused routes failing to emit the summary.
        [Fact]
        public void Tick_PausedRoute_SummaryLogged_NoPerRouteTransitionLog()
        {
            var route = BuildActiveDueKscRoute();
            route.Status = RouteStatus.Paused;
            RouteStore.AddRoute(route);
            var env = new FakeRouteRuntimeEnvironment();

            RouteOrchestrator.Tick(200.0, env);

            // Per-route Paused routes go through the evaluator's
            // status-permanent-block Skip; no Dispatch / Wait / EndpointLost log
            // line should fire for the route.
            Assert.DoesNotContain(logLines, l => l.Contains("Dispatch: route"));
            Assert.DoesNotContain(logLines, l => l.Contains("Wait: route"));
            Assert.DoesNotContain(logLines, l => l.Contains("EndpointLost: route"));
            // Status untouched.
            Assert.Equal(RouteStatus.Paused, route.Status);
            // No ledger actions produced.
            Assert.Empty(Ledger.Actions);
        }

        // catches: an in-tick AddRoute corrupting the iteration (the snapshot
        // must isolate the iterator from store mutation).
        [Fact]
        public void Tick_MidIterationAdd_NewRoutePickedUpNextTick()
        {
            var routeA = BuildActiveDueKscRoute(id: "route-A");
            RouteStore.AddRoute(routeA);

            // Capture how many actions exist after the first tick — then add a
            // second route and verify it is picked up on the second tick.
            var env = new FakeRouteRuntimeEnvironment();
            RouteOrchestrator.Tick(200.0, env);
            int actionsAfterFirstTick = Ledger.Actions.Count;
            Assert.Equal(RouteStatus.InTransit, routeA.Status);

            // Adding a new route now must not retroactively re-process routeA.
            var routeB = BuildActiveDueKscRoute(id: "route-B", nextDispatchUT: 100.0);
            RouteStore.AddRoute(routeB);

            // Second tick: routeA is now InTransit-pending so it stays put;
            // routeB dispatches.
            RouteOrchestrator.Tick(300.0, env);
            Assert.Equal(RouteStatus.InTransit, routeB.Status);
            Assert.Equal(300.0, routeB.CurrentCycleStartUT);

            // Ledger grew by exactly the new dispatch+debit pair (2 entries).
            Assert.Equal(actionsAfterFirstTick + 2, Ledger.Actions.Count);
        }

        // catches: endpoint loss not emitting the ledger row OR failing to
        // transition status.
        [Fact]
        public void Tick_EndpointLost_EmitsAction_AndTransitions()
        {
            var route = BuildActiveDueKscRoute();
            RouteStore.AddRoute(route);
            var env = new FakeRouteRuntimeEnvironment
            {
                EndpointResolvable = false,
                EndpointResolveFailureReason = "pid-miss-no-surface-fallback",
            };

            RouteOrchestrator.Tick(200.0, env);

            Assert.Equal(RouteStatus.EndpointLost, route.Status);
            Assert.True(route.NextEligibilityCheckUT.HasValue);

            var last = Ledger.Actions.Last();
            Assert.Equal(GameActionType.RouteEndpointLost, last.Type);
            Assert.Equal(route.Id, last.RouteId);
            Assert.Contains("pid-miss-no-surface-fallback", last.RouteEndpointReason ?? "");
        }

        // catches: dispatch happy path failing to emit BOTH actions with the
        // right cycle id pairing or wrong Sequence ordering.
        [Fact]
        public void Tick_DispatchEmitsTwoLedgerActions_WithMatchingCycleId()
        {
            var route = BuildActiveDueKscRoute(id: "route-X");
            RouteStore.AddRoute(route);
            var env = new FakeRouteRuntimeEnvironment();

            RouteOrchestrator.Tick(200.0, env);

            Assert.Equal(2, Ledger.Actions.Count);

            var dispatched = Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RouteDispatched);
            var debited = Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RouteCargoDebited);

            Assert.NotNull(dispatched);
            Assert.NotNull(debited);
            Assert.Equal(route.Id, dispatched.RouteId);
            Assert.Equal(route.Id, debited.RouteId);
            Assert.Equal(dispatched.RouteCycleId, debited.RouteCycleId);
            Assert.False(string.IsNullOrEmpty(dispatched.RouteCycleId));
            // Debit MUST sequence after the dispatched row at the same UT.
            Assert.Equal(0, dispatched.Sequence);
            Assert.Equal(1, debited.Sequence);
            Assert.Equal(200.0, dispatched.UT);
            Assert.Equal(200.0, debited.UT);
        }

        // catches: non-KSC dispatch routing funds-cost where it should be 0 +
        // missing the cost manifest on the debit row.
        [Fact]
        public void Tick_NonKscOrigin_CostManifestPopulated_FundsCostZero()
        {
            var route = BuildActiveDueKscRoute();
            route.IsKscOrigin = false;
            RouteStore.AddRoute(route);
            var env = new FakeRouteRuntimeEnvironment
            {
                IsCareer = true, // career + non-ksc still means zero funds cost
            };

            RouteOrchestrator.Tick(200.0, env);

            var debited = Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RouteCargoDebited);
            Assert.NotNull(debited);
            Assert.Equal(0f, debited.RouteKscFundsCost);
            // CostManifest copied (not the same reference) onto the action.
            Assert.NotNull(debited.RouteResourceManifest);
            Assert.NotSame(route.CostManifest, debited.RouteResourceManifest);
            Assert.Equal(route.CostManifest.Count, debited.RouteResourceManifest.Count);
            foreach (var kv in route.CostManifest)
            {
                Assert.True(debited.RouteResourceManifest.TryGetValue(kv.Key, out double v));
                Assert.Equal(kv.Value, v);
            }
        }

        // catches: Sandbox + KSC origin dispatch silently charging funds.
        [Fact]
        public void Tick_SandboxKscOrigin_EmitsZeroFundsCost()
        {
            var route = BuildActiveDueKscRoute();
            RouteStore.AddRoute(route);
            var env = new FakeRouteRuntimeEnvironment
            {
                IsCareer = false, // sandbox / science
                KscFundsAvailableResult = true, // evaluator short-circuits, but be safe
            };

            RouteOrchestrator.Tick(200.0, env);

            var debited = Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RouteCargoDebited);
            Assert.NotNull(debited);
            Assert.Equal(0f, debited.RouteKscFundsCost);
            // Evaluator must not have called the funds check in sandbox.
            Assert.Equal(0, env.KscFundsAvailableCalls);
        }

        // catches: an env method throwing taking down the whole tick, instead
        // of being caught + logged per-route.
        [Fact]
        public void Tick_ApplyExceptionDoesNotPropagate()
        {
            var routeA = BuildActiveDueKscRoute(id: "route-A");
            var routeB = BuildActiveDueKscRoute(id: "route-B");
            RouteStore.AddRoute(routeA);
            RouteStore.AddRoute(routeB);

            // Trigger the throw by route id so the test stays robust to
            // future evaluator-call-order reshuffles. A global call-counter
            // trigger would pass even if routeB ended up being the thrower
            // after a reorder — and the attribution check below would be
            // silently wrong.
            var env = new FakeRouteRuntimeEnvironment
            {
                ThrowOnRouteId = routeA.Id,
            };

            // The exception during routeA processing must be caught — routeB
            // must still be processed and the tick must complete.
            var ex = Record.Exception(() => RouteOrchestrator.Tick(200.0, env));
            Assert.Null(ex);

            // routeB went through cleanly (the throw fired during routeA).
            Assert.Equal(RouteStatus.InTransit, routeB.Status);
            // routeA aborted before status change — stays Active.
            Assert.Equal(RouteStatus.Active, routeA.Status);

            // Error log entry references the route that threw by its short id
            // (orchestrator logs use ShortIdForLog which truncates to 8 chars).
            Assert.Contains(logLines, l =>
                l.Contains("[ERROR]")
                && l.Contains("[Route]")
                && l.Contains("Tick:")
                && l.Contains(routeA.Id));
        }

        // catches: ERS-stale routes silently dispatching. Defensive cross-check
        // against the canonical RevalidateSources transition (status would
        // normally already be MissingSourceRecording; this guards the case
        // where status is Active but ERS lookup fails mid-tick).
        [Fact]
        public void Tick_SourcesStale_SkipsDispatch()
        {
            var route = BuildActiveDueKscRoute();
            RouteStore.AddRoute(route);
            var env = new FakeRouteRuntimeEnvironment
            {
                RouteHasValidSourcesResult = false,
            };

            RouteOrchestrator.Tick(200.0, env);

            // Evaluator returns Skip("sources-stale") — no transition, no ledger row.
            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.Empty(Ledger.Actions);
        }

        // catches: a Wait* outcome's reason text not being preserved into the
        // status-transition log line (operators rely on the reason token to
        // debug stuck routes).
        [Fact]
        public void Tick_WaitResources_LogsReasonToken()
        {
            var route = BuildActiveDueKscRoute();
            route.IsKscOrigin = false;
            RouteStore.AddRoute(route);
            var env = new FakeRouteRuntimeEnvironment
            {
                OriginHasCargoResult = false,
                OriginLackingResource = "MonoPropellant",
            };

            RouteOrchestrator.Tick(200.0, env);

            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("Wait: route") && l.Contains("MonoPropellant"));
        }

        // ==================================================================
        // TrySendOneCycleNow (v0 Logistics UI Send-Now button)
        // ==================================================================

        [Fact]
        public void TrySendOneCycleNow_NullRoute_ReturnsFalse()
        {
            bool ok = RouteOrchestrator.TrySendOneCycleNow(null, 100.0);
            Assert.False(ok);
        }

        [Fact]
        public void TrySendOneCycleNow_ActiveRouteFutureDispatch_PullsScheduleForward()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 1_000_000.0);
            bool ok = RouteOrchestrator.TrySendOneCycleNow(route, 100.0);
            Assert.True(ok);
            Assert.Equal(100.0, route.NextDispatchUT);
            Assert.Null(route.NextEligibilityCheckUT);
        }

        [Fact]
        public void TrySendOneCycleNow_RouteAlreadyDue_NoOps()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 50.0);
            // Set a wait-retry to verify it stays untouched on the already-due path.
            route.NextEligibilityCheckUT = 75.0;
            bool ok = RouteOrchestrator.TrySendOneCycleNow(route, 100.0);
            Assert.True(ok);
            Assert.Equal(50.0, route.NextDispatchUT); // unchanged
            Assert.Equal(75.0, route.NextEligibilityCheckUT); // unchanged
        }

        [Fact]
        public void TrySendOneCycleNow_InTransit_Refuses()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 1_000_000.0);
            route.Status = RouteStatus.InTransit;
            bool ok = RouteOrchestrator.TrySendOneCycleNow(route, 100.0);
            Assert.False(ok);
            Assert.Equal(1_000_000.0, route.NextDispatchUT);
        }

        [Fact]
        public void TrySendOneCycleNow_Paused_Refuses()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 1_000_000.0);
            route.Status = RouteStatus.Paused;
            bool ok = RouteOrchestrator.TrySendOneCycleNow(route, 100.0);
            Assert.False(ok);
            Assert.Equal(1_000_000.0, route.NextDispatchUT);
        }

        [Fact]
        public void TrySendOneCycleNow_MissingSourceRecording_Refuses()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 1_000_000.0);
            route.Status = RouteStatus.MissingSourceRecording;
            bool ok = RouteOrchestrator.TrySendOneCycleNow(route, 100.0);
            Assert.False(ok);
        }

        [Fact]
        public void TrySendOneCycleNow_WaitingForResources_AcceptsAndPullsForward()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 1_000_000.0);
            route.Status = RouteStatus.WaitingForResources;
            route.NextEligibilityCheckUT = 999_999.0;
            bool ok = RouteOrchestrator.TrySendOneCycleNow(route, 100.0);
            Assert.True(ok);
            Assert.Equal(100.0, route.NextDispatchUT);
            Assert.Null(route.NextEligibilityCheckUT);
        }
    }
}
