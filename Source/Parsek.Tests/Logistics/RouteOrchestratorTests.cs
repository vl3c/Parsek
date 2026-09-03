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
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteOrchestrator.OriginDebitApplierForTesting = null;
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

            public bool OriginHasCargo(Route route, out string lackingResource, out double shortfall)
            {
                OnAnyCall?.Invoke();
                ThrowIfMatch(route);
                OriginHasCargoCalls++;
                lackingResource = OriginHasCargoResult ? string.Empty : OriginLackingResource;
                shortfall = 0.0;
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

        // catches (M1, D8): the per-tick snapshot not being sorted by the
        // dispatch-priority comparator. The lower-priority-VALUE route must be
        // processed first even though it was committed later, has a LATER
        // NextDispatchUT, and a LATER ordinal id (so only priority explains the
        // order). Order is asserted via the per-route "Dispatch:" log lines plus
        // the route-tick-order breadcrumb.
        [Fact]
        public void Tick_ProcessesRoutesInPriorityOrder()
        {
            var late = BuildActiveDueKscRoute(id: "route-a", nextDispatchUT: 100.0);
            late.DispatchPriority = 1;
            var early = BuildActiveDueKscRoute(id: "route-z", nextDispatchUT: 150.0);
            early.DispatchPriority = 0;
            // Commit-list order deliberately contradicts priority order.
            RouteStore.AddRoute(late);
            RouteStore.AddRoute(early);
            var env = new FakeRouteRuntimeEnvironment();

            RouteOrchestrator.Tick(200.0, env);

            // Both dispatched, priority-0 route first.
            int earlyIdx = logLines.FindIndex(l =>
                l.Contains("[Route]") && l.Contains("Dispatch: route route-z"));
            int lateIdx = logLines.FindIndex(l =>
                l.Contains("[Route]") && l.Contains("Dispatch: route route-a"));
            Assert.True(earlyIdx >= 0, "priority-0 route must dispatch");
            Assert.True(lateIdx >= 0, "priority-1 route must dispatch");
            Assert.True(earlyIdx < lateIdx,
                "lower priority value must be processed first within the tick");

            // The applied order is breadcrumbed once per tick (count > 1).
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("Tick order:")
                && l.Contains("route-z:0,route-a:1"));
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

        // catches (M1, D11 legacy-path containment): the legacy non-loop
        // self-timer path applying the physical origin debit. ApplyDispatch
        // fires at cycle start (not the recorded dock phase) and has no replay
        // backstop, so EmitDispatchDebit MUST receive
        // applyPhysicalOriginDebit=false from it: the seam is never invoked,
        // the row keeps the v0 shape (CostManifest clone, no requested
        // manifest, pid 0), and the Verbose skip breadcrumb fires. The
        // companion pin is Tick_NonKscOrigin_CostManifestPopulated_FundsCostZero
        // staying green UNCHANGED above.
        [Fact]
        public void LegacyDispatchPath_NonKscOrigin_SkipsPhysicalDebit_Logs()
        {
            var route = BuildActiveDueKscRoute();
            route.IsKscOrigin = false; // non-KSC, non-loop (no BackingMissionTreeId)
            RouteStore.AddRoute(route);
            int seamCalls = 0;
            RouteOrchestrator.OriginDebitApplierForTesting = (r, ut, env) =>
            {
                seamCalls++;
                return new RouteOrchestrator.OriginDebitOutcome { OriginVesselPid = 999u };
            };

            RouteOrchestrator.Tick(200.0, new FakeRouteRuntimeEnvironment());

            // Dispatched through the legacy path, physical debit skipped.
            Assert.Equal(RouteStatus.InTransit, route.Status);
            Assert.Equal(0, seamCalls);
            var debited = Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RouteCargoDebited);
            Assert.NotNull(debited);
            // v0 row shape byte-identical: CostManifest clone, no requested
            // manifest, no origin pid.
            Assert.NotNull(debited.RouteResourceManifest);
            Assert.NotSame(route.CostManifest, debited.RouteResourceManifest);
            Assert.Equal(route.CostManifest.Count, debited.RouteResourceManifest.Count);
            Assert.Null(debited.RouteRequestedResourceManifest);
            Assert.Equal(0u, debited.RouteOriginVesselPid);
            // The D11 skip breadcrumb names the legacy path.
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("legacy dispatch path: physical origin debit skipped"));
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
        // M6 hold reasons: legacy-path capture (ApplyWait / ApplyEndpointLost)
        // and the dispatch / player-Activate clears
        // ==================================================================

        // catches (M6): the legacy wait applier not persisting the hold. The
        // legacy path stores the PREFIXED decision token ("origin-lacks-X"),
        // not the loop path's bare resource name.
        [Fact]
        public void ApplyWait_LegacyPath_RecordsHold()
        {
            var route = BuildActiveDueKscRoute();
            route.IsKscOrigin = false; // non-loop, non-KSC -> OriginHasCargo consulted
            RouteStore.AddRoute(route);
            var env = new FakeRouteRuntimeEnvironment
            {
                OriginHasCargoResult = false,
                OriginLackingResource = "LiquidFuel",
            };

            RouteOrchestrator.Tick(200.0, env);

            Assert.Equal(RouteStatus.WaitingForResources, route.Status);
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo,
                route.LastHoldKind);
            Assert.Equal("origin-lacks-LiquidFuel", route.LastHoldDetail);
            Assert.Equal(0.0, route.LastHoldShortfall);
            Assert.Equal(200.0, route.LastHoldUT);
        }

        // catches (M6): the legacy endpoint-lost applier not persisting the
        // resolver token onto the hold.
        [Fact]
        public void ApplyEndpointLost_RecordsHold()
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
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.EndpointLost,
                route.LastHoldKind);
            Assert.Equal("stop-0-pid-miss-no-surface-fallback", route.LastHoldDetail);
            Assert.Equal(200.0, route.LastHoldUT);
        }

        // catches (M6): a successful legacy dispatch leaving a stale hold behind.
        [Fact]
        public void ApplyDispatch_ClearsHold()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 100.0);
            route.RecordHold(
                RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo,
                "origin-lacks-LiquidFuel", 0.0, 50.0);
            RouteStore.AddRoute(route);
            var env = new FakeRouteRuntimeEnvironment();

            RouteOrchestrator.Tick(200.0, env);

            Assert.Equal(RouteStatus.InTransit, route.Status); // genuinely dispatched
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.None, route.LastHoldKind);
            Assert.Null(route.LastHoldDetail);
            Assert.Equal(-1.0, route.LastHoldUT);
        }

        // catches (M6): a player Activate carrying a prior-session hold forward.
        // Activation resets loop observation, so a stale reason must not present
        // as current. (Pause deliberately KEEPS the hold; no clear there.)
        [Fact]
        public void TryActivate_ClearsHold()
        {
            var route = BuildActiveDueKscRoute();
            route.Status = RouteStatus.Paused;
            route.RecordHold(
                RouteDispatchEvaluator.EligibilityFailureKind.FundsShort,
                "funds-short", 750.0, 50.0);

            bool ok = RouteOrchestrator.TryActivate(route, 100.0);

            Assert.True(ok);
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.None, route.LastHoldKind);
            Assert.Null(route.LastHoldDetail);
            Assert.Equal(0.0, route.LastHoldShortfall);
            Assert.Equal(-1.0, route.LastHoldUT);
        }

        // catches (M6): the outcome->kind map dropping a wait outcome, or a
        // future enum addition mapping to a non-None kind by accident (totality).
        [Fact]
        public void HoldKindForOutcome_MapsAllWaitOutcomes()
        {
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo,
                RouteOrchestrator.HoldKindForOutcome(RouteDispatchOutcome.WaitResources));
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.FundsShort,
                RouteOrchestrator.HoldKindForOutcome(RouteDispatchOutcome.WaitFunds));
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull,
                RouteOrchestrator.HoldKindForOutcome(RouteDispatchOutcome.WaitDestinationFull));
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.EndpointLost,
                RouteOrchestrator.HoldKindForOutcome(RouteDispatchOutcome.EndpointLost));

            // Non-hold outcomes map to None; the map is total over the enum.
            foreach (RouteDispatchOutcome outcome in
                (RouteDispatchOutcome[])System.Enum.GetValues(typeof(RouteDispatchOutcome)))
            {
                var kind = RouteOrchestrator.HoldKindForOutcome(outcome);
                if (outcome == RouteDispatchOutcome.Skip
                    || outcome == RouteDispatchOutcome.Dispatch
                    || outcome == RouteDispatchOutcome.InTransitComplete)
                {
                    Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.None, kind);
                }
                else
                {
                    Assert.NotEqual(RouteDispatchEvaluator.EligibilityFailureKind.None, kind);
                }
            }
        }

        // ==================================================================
        // TrySendOneCycleNow (v0 Logistics UI Send Once button) — arms a
        // one-shot dispatch via PauseAfterCurrentCycle, skips the interval
        // wait, but preserves per-cycle eligibility gates.
        // ==================================================================

        [Fact]
        public void TrySendOneCycleNow_NullRoute_ReturnsFalse()
        {
            bool ok = RouteOrchestrator.TrySendOneCycleNow(null, 100.0);
            Assert.False(ok);
        }

        [Fact]
        public void TrySendOneCycleNow_ActiveRoute_ArmsOneShotAndPullsScheduleForward()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 1_000_000.0);
            bool ok = RouteOrchestrator.TrySendOneCycleNow(route, 100.0);
            Assert.True(ok);
            Assert.Equal(100.0, route.NextDispatchUT);
            Assert.Null(route.NextEligibilityCheckUT);
            Assert.True(route.PauseAfterCurrentCycle);
            Assert.Equal(RouteStatus.Active, route.Status);
        }

        [Fact]
        public void TrySendOneCycleNow_AlreadyDue_StillArmsPauseAfter()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 50.0);
            route.NextEligibilityCheckUT = 75.0;
            bool ok = RouteOrchestrator.TrySendOneCycleNow(route, 100.0);
            Assert.True(ok);
            // NextDispatchUT was already in the past — left alone, but the
            // one-shot flag is still set and the wait-retry backoff is cleared.
            Assert.Equal(50.0, route.NextDispatchUT);
            Assert.Null(route.NextEligibilityCheckUT);
            Assert.True(route.PauseAfterCurrentCycle);
        }

        [Fact]
        public void TrySendOneCycleNow_InTransit_Refuses()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 1_000_000.0);
            route.Status = RouteStatus.InTransit;
            bool ok = RouteOrchestrator.TrySendOneCycleNow(route, 100.0);
            Assert.False(ok);
            Assert.Equal(1_000_000.0, route.NextDispatchUT);
            Assert.False(route.PauseAfterCurrentCycle);
        }

        [Fact]
        public void TrySendOneCycleNow_Paused_UnPausesAndArmsOneShot()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 1_000_000.0);
            route.Status = RouteStatus.Paused;
            bool ok = RouteOrchestrator.TrySendOneCycleNow(route, 100.0);
            Assert.True(ok);
            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.Equal(100.0, route.NextDispatchUT);
            Assert.True(route.PauseAfterCurrentCycle);
        }

        [Fact]
        public void TrySendOneCycleNow_MissingSourceRecording_Refuses()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 1_000_000.0);
            route.Status = RouteStatus.MissingSourceRecording;
            bool ok = RouteOrchestrator.TrySendOneCycleNow(route, 100.0);
            Assert.False(ok);
            Assert.False(route.PauseAfterCurrentCycle);
        }

        [Fact]
        public void TrySendOneCycleNow_WaitingForResources_ArmsOneShot()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 1_000_000.0);
            route.Status = RouteStatus.WaitingForResources;
            route.NextEligibilityCheckUT = 999_999.0;
            bool ok = RouteOrchestrator.TrySendOneCycleNow(route, 100.0);
            Assert.True(ok);
            Assert.Equal(100.0, route.NextDispatchUT);
            Assert.Null(route.NextEligibilityCheckUT);
            Assert.True(route.PauseAfterCurrentCycle);
            // Status stays WaitingForResources — the evaluator will refresh it
            // when it next runs (it transitioned in here through the Active
            // → WaitingForResources path; un-pausing is only for Paused).
            Assert.Equal(RouteStatus.WaitingForResources, route.Status);
        }

        // ==================================================================
        // TryActivate / TryPause lifecycle (Create→Paused→Active→Paused)
        // ==================================================================

        [Fact]
        public void TryActivate_Null_ReturnsFalse()
        {
            Assert.False(RouteOrchestrator.TryActivate(null, 100.0));
        }

        [Fact]
        public void TryActivate_PausedRoute_TransitionsToActive()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 50.0);
            route.Status = RouteStatus.Paused;

            bool ok = RouteOrchestrator.TryActivate(route, 100.0);

            Assert.True(ok);
            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.False(route.PauseAfterCurrentCycle);
            // Stale (past) NextDispatchUT pulled up to currentUT so we don't fire a backlog.
            Assert.Equal(100.0, route.NextDispatchUT);
        }

        [Fact]
        public void TryActivate_PausedRoute_FutureNextDispatch_NotPulledBackward()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 5000.0);
            route.Status = RouteStatus.Paused;

            bool ok = RouteOrchestrator.TryActivate(route, 100.0);

            Assert.True(ok);
            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.Equal(5000.0, route.NextDispatchUT);
        }

        [Fact]
        public void TryActivate_NonPausedRoute_ReturnsFalseAndKeepsStatus()
        {
            var route = BuildActiveDueKscRoute();
            route.Status = RouteStatus.Active;

            bool ok = RouteOrchestrator.TryActivate(route, 100.0);

            Assert.False(ok);
            Assert.Equal(RouteStatus.Active, route.Status);
        }

        [Fact]
        public void TryPause_Null_ReturnsFalse()
        {
            Assert.False(RouteOrchestrator.TryPause(null));
        }

        [Fact]
        public void TryPause_ActiveRoute_TransitionsToPaused()
        {
            var route = BuildActiveDueKscRoute();
            route.Status = RouteStatus.Active;

            bool ok = RouteOrchestrator.TryPause(route);

            Assert.True(ok);
            Assert.Equal(RouteStatus.Paused, route.Status);
            Assert.False(route.PauseAfterCurrentCycle);
        }

        [Fact]
        public void TryPause_BlockedActiveRoute_TransitionsToPaused()
        {
            var route = BuildActiveDueKscRoute();
            route.Status = RouteStatus.WaitingForResources;

            bool ok = RouteOrchestrator.TryPause(route);

            Assert.True(ok);
            Assert.Equal(RouteStatus.Paused, route.Status);
        }

        [Fact]
        public void TryPause_InTransitRoute_ArmsPauseAfterCurrentCycle()
        {
            var route = BuildActiveDueKscRoute();
            route.Status = RouteStatus.InTransit;

            bool ok = RouteOrchestrator.TryPause(route);

            Assert.True(ok);
            // Mid-cycle: route keeps running but will pause after delivery.
            Assert.Equal(RouteStatus.InTransit, route.Status);
            Assert.True(route.PauseAfterCurrentCycle);
        }

        [Fact]
        public void TryPause_AlreadyPaused_ReturnsFalse()
        {
            var route = BuildActiveDueKscRoute();
            route.Status = RouteStatus.Paused;

            bool ok = RouteOrchestrator.TryPause(route);

            Assert.False(ok);
            Assert.Equal(RouteStatus.Paused, route.Status);
        }

        // ==================================================================
        // Route-timeline events: pause / resume lifecycle markers + Send Once
        // provenance (route-timeline events PR)
        // ==================================================================

        [Fact]
        public void TryPause_ImmediatePause_EmitsRoutePausedMarker()
        {
            var route = BuildActiveDueKscRoute();
            route.Status = RouteStatus.Active;

            bool ok = RouteOrchestrator.TryPause(route, 500.0, null);

            Assert.True(ok);
            var marker = Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RoutePaused);
            Assert.NotNull(marker);
            Assert.Equal(route.Id, marker.RouteId);
            Assert.Equal(500.0, marker.UT);
            Assert.Equal("player-pause", marker.RouteEndpointReason);
            Assert.Null(marker.RecordingId); // free-standing route row contract
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("LifecycleMarker"));
        }

        [Theory]
        [InlineData(-1.0)]
        [InlineData(0.0)] // some UI fallbacks surface 0 when Planetarium is missing (BUG-F lesson)
        public void TryPause_UnresolvedUT_SkipsMarkerEmission(double degenerateUT)
        {
            // A marker with a bogus UT would survive every rewind cutoff, so the
            // degenerate no-UT path must skip emission (Warn) rather than emit.
            var route = BuildActiveDueKscRoute();
            route.Status = RouteStatus.Active;

            bool ok = RouteOrchestrator.TryPause(route, degenerateUT, null);

            Assert.True(ok);
            Assert.Equal(RouteStatus.Paused, route.Status);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.RoutePaused);
            Assert.Contains(logLines, l => l.Contains("LifecycleMarker") && l.Contains("SKIPPED"));
        }

        [Fact]
        public void TryPause_InTransitArm_DoesNotEmitMarkerYet()
        {
            // The armed pause lands later at the delivery applier; arming alone is
            // not a pause point on the timeline.
            var route = BuildActiveDueKscRoute();
            route.Status = RouteStatus.InTransit;

            Assert.True(RouteOrchestrator.TryPause(route, 500.0, null));
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.RoutePaused);
        }

        [Fact]
        public void TryPause_InTransitDuringSendOnceCycle_ClearsSendOnceArm()
        {
            // Review finding 1 (PR #1327): a player Pause during an in-flight
            // Send-Once cycle supersedes the one-shot provenance so the UI label
            // flips from "Sending one cycle" to the pausing-after-cycle state.
            // The dispatched row was already stamped at emit, so no ledger loss.
            var route = BuildActiveDueKscRoute();
            route.Status = RouteStatus.InTransit;
            route.SendOnceArmed = true;
            route.PauseAfterCurrentCycle = true;

            Assert.True(RouteOrchestrator.TryPause(route, 500.0, null));

            Assert.False(route.SendOnceArmed);
            Assert.True(route.PauseAfterCurrentCycle);
            Assert.Equal(RouteStatus.InTransit, route.Status);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("cleared Send Once arm"));
        }

        [Fact]
        public void TryActivate_EmitsRouteResumedMarker()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 50.0);
            route.Status = RouteStatus.Paused;

            bool ok = RouteOrchestrator.TryActivate(route, 700.0);

            Assert.True(ok);
            var marker = Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RouteResumed);
            Assert.NotNull(marker);
            Assert.Equal(route.Id, marker.RouteId);
            Assert.Equal(700.0, marker.UT);
            Assert.Equal("player-activate", marker.RouteEndpointReason);
        }

        [Fact]
        public void TrySendOneCycleNow_SetsSendOnceArm_AndPauseClearsIt()
        {
            var route = BuildActiveDueKscRoute();
            route.Status = RouteStatus.Paused;

            Assert.True(RouteOrchestrator.TrySendOneCycleNow(route, 100.0));
            Assert.True(route.SendOnceArmed);
            Assert.True(route.PauseAfterCurrentCycle);
            // Send Once un-pauses without a RouteResumed row: the one-shot is
            // bracketed by the stamped dispatch + the post-delivery pause instead.
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.RouteResumed);

            // A player pause cancels the never-fired arm and leaves no dispatch trace.
            Assert.True(RouteOrchestrator.TryPause(route, 200.0, null));
            Assert.False(route.SendOnceArmed);
            Assert.False(route.PauseAfterCurrentCycle);
        }

        [Fact]
        public void TryActivate_ClearsSendOnceArm()
        {
            var route = BuildActiveDueKscRoute();
            route.Status = RouteStatus.Paused;
            Assert.True(RouteOrchestrator.TrySendOneCycleNow(route, 100.0));
            route.TransitionTo(RouteStatus.Paused, "test-rearm");

            Assert.True(RouteOrchestrator.TryActivate(route, 300.0));
            Assert.False(route.SendOnceArmed);
            Assert.False(route.PauseAfterCurrentCycle);
        }

        [Fact]
        public void Tick_SendOnceArmedRoute_StampsRouteSendOnceOnDispatchedRow()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 5000.0);
            route.Status = RouteStatus.Paused;
            RouteStore.AddRoute(route);
            Assert.True(RouteOrchestrator.TrySendOneCycleNow(route, 100.0));
            var env = new FakeRouteRuntimeEnvironment();

            RouteOrchestrator.Tick(200.0, env);

            var dispatched = Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RouteDispatched);
            Assert.NotNull(dispatched);
            Assert.True(dispatched.RouteSendOnce,
                "a Send-Once-armed cycle's dispatched row must carry the provenance stamp");
        }

        [Fact]
        public void Tick_AutoCycleRoute_LeavesRouteSendOnceFalse()
        {
            var route = BuildActiveDueKscRoute(nextDispatchUT: 100.0);
            RouteStore.AddRoute(route);
            var env = new FakeRouteRuntimeEnvironment();

            RouteOrchestrator.Tick(200.0, env);

            var dispatched = Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RouteDispatched);
            Assert.NotNull(dispatched);
            Assert.False(dispatched.RouteSendOnce);
        }

        [Fact]
        public void EmitRouteLifecycleMarker_UsesInjectedEmitterAndSequence()
        {
            var route = BuildActiveDueKscRoute();
            var captured = new List<GameAction>();

            RouteOrchestrator.EmitRouteLifecycleMarker(
                route, 900.0, GameActionType.RoutePaused, "delivered-then-paused",
                sequence: 4, emitter: captured.Add);

            Assert.Single(captured);
            Assert.Equal(GameActionType.RoutePaused, captured[0].Type);
            Assert.Equal(4, captured[0].Sequence);
            Assert.Equal("delivered-then-paused", captured[0].RouteEndpointReason);
            Assert.Empty(Ledger.Actions); // injected emitter bypasses the static ledger
        }

        // ==================================================================
        // M2 funds-basis selection (plan D9 / OQ1): the basis log names which
        // resource-term basis ComputeDispatchFundsCostForRoute applied
        // ==================================================================

        /// <summary>
        /// Commits a single-recording tree into the store (so ComputeERS
        /// resolves it) whose recording carries the given run manifest, plus a
        /// route pointing at it, and runs
        /// <see cref="RouteOrchestrator.ComputeDispatchFundsCostForRoute"/>.
        /// State is torn down inside the helper so the suite's other tests
        /// keep their store-free shape.
        /// </summary>
        private double RunFundsBasisCase(RouteRunCargoManifest runManifest)
        {
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            try
            {
                var snapshot = new ConfigNode("VESSEL");
                ConfigNode part = snapshot.AddNode("PART");
                part.AddValue("name", "fuelTank");

                var rec = new Recording
                {
                    RecordingId = "rec-funds-basis",
                    VesselName = "Funds Basis Transport",
                    MergeState = MergeState.Immutable,
                    VesselSnapshot = snapshot,
                    RouteRunManifest = runManifest,
                };
                RecordingStore.AddRecordingWithTreeForTesting(rec);

                var route = new Route
                {
                    Id = "route-funds-basis",
                    SourceRefs = new List<RouteSourceRef>
                    {
                        new RouteSourceRef { RecordingId = "rec-funds-basis", TreeId = rec.TreeId },
                    },
                };

                return RouteOrchestrator.ComputeDispatchFundsCostForRoute(route);
            }
            finally
            {
                RecordingStore.ResetForTesting();
                EffectiveState.ResetCachesForTesting();
                ParsekScenario.ResetInstanceForTesting();
            }
        }

        // catches: the orchestrator threading a manifest basis without a
        // COMPLETE run manifest, or not logging which basis it applied -
        // gate and charge must not diverge (plan risk 7), and the basis log
        // is the observable that says which one fired.
        [Fact]
        public void ComputeDispatchFundsCostForRoute_CompleteManifest_LogsLaunchManifestBasis()
        {
            RunFundsBasisCase(new RouteRunCargoManifest
            {
                TransportPartPersistentIds = new List<uint> { 100001u },
                StartTransportResources = new Dictionary<string, ResourceAmount>
                {
                    ["LiquidFuel"] = new ResourceAmount { amount = 50.0, maxAmount = 100.0 },
                },
                EndTransportResources = new Dictionary<string, ResourceAmount>
                {
                    ["LiquidFuel"] = new ResourceAmount { amount = 10.0, maxAmount = 100.0 },
                },
                EndCaptured = true,
            });

            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("FundsCost basis=launch-manifest")
                && l.Contains("route=route-fu")
                && l.Contains("source=rec-funds-basis"));
        }

        // catches: a start-only (ForceStop-shaped) manifest sneaking onto the
        // launch basis - the completeness gate requires BOTH halves, exactly
        // like the analysis presence gate.
        [Fact]
        public void ComputeDispatchFundsCostForRoute_StartOnlyManifest_LogsStopSnapshotBasis()
        {
            RunFundsBasisCase(new RouteRunCargoManifest
            {
                TransportPartPersistentIds = new List<uint> { 100001u },
                StartTransportResources = new Dictionary<string, ResourceAmount>
                {
                    ["LiquidFuel"] = new ResourceAmount { amount = 50.0, maxAmount = 100.0 },
                },
                EndCaptured = false,
            });

            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("FundsCost basis=stop-snapshot")
                && l.Contains("source=rec-funds-basis"));
        }

        // catches: pre-M2 recordings (no run manifest at all) leaving the
        // legacy basis - the containment half of OQ1.
        [Fact]
        public void ComputeDispatchFundsCostForRoute_NoManifest_LogsStopSnapshotBasis()
        {
            RunFundsBasisCase(runManifest: null);

            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("FundsCost basis=stop-snapshot")
                && l.Contains("source=rec-funds-basis"));
        }

        // ==================================================================
        // ROUTE-DISPATCH-COST-FREE-ON-SNAPSHOTLESS-ROOT: the snapshot-less
        // root fallback
        //
        // The always-tree ROOT gets its VesselSnapshot at the first stop/split
        // capture site, so a root that never reaches one (the rover-route
        // shape: a runway stub ending at the transport's undock split, 0
        // trajectory points) keeps VesselSnapshot == null forever and used to
        // make the whole KSC dispatch FREE in career.
        //
        // WHICH SNAPSHOT WAS WALKED is proved mechanically, not just by the
        // basis line: every part name the walk visits emits its own
        // "Unknown part cost: name=..." Warn (headless PartLoader prices
        // everything at 0), so each fixture recording carries a DISTINCT part
        // name and the assertions read the visited set directly.
        // ==================================================================

        private const string RoverTreeId = "tree-rover-supply";
        private const string RoverRootId = "rover-root";
        private const string RoverTransportId = "rover-transport";
        private const string RoverDockMergeId = "rover-dock-merge";
        private const double RoverRootStartUT = 1000.0;
        private const double RoverDockUT = 3000.0;
        private const string RootPartName = "roverRootStubPart";
        private const string TransportPartName = "roverTransportTankPart";
        private const string DepotPartName = "roverDepotStationPart";

        private static ConfigNode PartsSnapshot(params string[] partNames)
        {
            var snapshot = new ConfigNode("VESSEL");
            for (int i = 0; i < partNames.Length; i++)
                snapshot.AddNode("PART").AddValue("name", partNames[i]);
            return snapshot;
        }

        private static Recording RoverMember(
            string id, int treeOrder, ConfigNode snapshot, double startUT, double endUT)
        {
            return new Recording
            {
                RecordingId = id,
                TreeId = RoverTreeId,
                TreeOrder = treeOrder,
                VesselName = id,
                MergeState = MergeState.Immutable,
                VesselSnapshot = snapshot,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
            };
        }

        /// <summary>
        /// The rover-route tree shape: a snapshot-LESS runway-stub root, the
        /// transport leg that split off it (snapshot), and the dock-merged
        /// child that carries the connection window and a COMBINED
        /// transport + depot snapshot. Commits whichever members the caller
        /// names, builds a route whose SourceRefs list them in the given
        /// order, and returns the computed cost.
        /// </summary>
        private double RunRoverFundsCase(
            RouteRunCargoManifest rootManifest,
            bool rootHasSnapshot,
            params string[] sourceRefOrder)
        {
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            try
            {
                Recording root = RoverMember(
                    RoverRootId, 0,
                    rootHasSnapshot ? PartsSnapshot(RootPartName) : null,
                    RoverRootStartUT, RoverDockUT);
                root.RouteRunManifest = rootManifest;

                Recording transport = RoverMember(
                    RoverTransportId, 1, PartsSnapshot(TransportPartName),
                    RoverRootStartUT + 100.0, RoverDockUT);

                Recording merged = RoverMember(
                    RoverDockMergeId, 2,
                    PartsSnapshot(TransportPartName, DepotPartName),
                    RoverDockUT, RoverDockUT + 500.0);
                merged.RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "w-rover",
                        DockUT = RoverDockUT,
                        UndockUT = RoverDockUT + 400.0,
                    },
                };

                var byId = new Dictionary<string, Recording>(StringComparer.Ordinal)
                {
                    [RoverRootId] = root,
                    [RoverTransportId] = transport,
                    [RoverDockMergeId] = merged,
                };

                var refs = new List<RouteSourceRef>();
                for (int i = 0; i < sourceRefOrder.Length; i++)
                {
                    Recording member = byId[sourceRefOrder[i]];
                    RecordingStore.AddRecordingWithTreeForTesting(member);
                    refs.Add(new RouteSourceRef
                    {
                        RecordingId = member.RecordingId,
                        TreeId = RoverTreeId,
                        TreeOrder = member.TreeOrder,
                        StartUT = member.StartUT,
                        EndUT = member.EndUT,
                    });
                }

                var route = new Route
                {
                    Id = "route-rover-supply",
                    RecordedDockUT = RoverDockUT,
                    DockMemberRecordingId = RoverDockMergeId,
                    SourceRefs = refs,
                };

                return RouteOrchestrator.ComputeDispatchFundsCostForRoute(route);
            }
            finally
            {
                RecordingStore.ResetForTesting();
                EffectiveState.ResetCachesForTesting();
                ParsekScenario.ResetInstanceForTesting();
            }
        }

        private static RouteRunCargoManifest CompleteRootManifest()
        {
            return new RouteRunCargoManifest
            {
                TransportPartPersistentIds = new List<uint> { 100001u },
                StartTransportResources = new Dictionary<string, ResourceAmount>
                {
                    ["LiquidFuel"] = new ResourceAmount { amount = 97.6, maxAmount = 100.0 },
                },
                EndTransportResources = new Dictionary<string, ResourceAmount>
                {
                    ["LiquidFuel"] = new ResourceAmount { amount = 0.0, maxAmount = 100.0 },
                },
                EndCaptured = true,
            };
        }

        private bool VisitedPart(string partName) =>
            logLines.Any(l => l.Contains("Unknown part cost: name=" + partName));

        // catches: the fallback stealing the PREFERRED basis. A root that HAS a
        // VesselSnapshot must still be priced from the root, from the root's own
        // launch manifest, with fallback=0 - byte-identical to pre-fix behavior.
        [Fact]
        public void ComputeDispatchFundsCostForRoute_RootHasSnapshot_PricesRoot_NoFallback()
        {
            RunRoverFundsCase(
                CompleteRootManifest(), rootHasSnapshot: true,
                RoverRootId, RoverTransportId, RoverDockMergeId);

            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("FundsCost basis=launch-manifest")
                && l.Contains("snapshotSource=" + RoverRootId)
                && l.Contains("fallback=0"));
            // Only the ROOT's snapshot was walked.
            Assert.True(VisitedPart(RootPartName));
            Assert.False(VisitedPart(TransportPartName));
            Assert.False(VisitedPart(DepotPartName));
        }

        // THE defect. A snapshot-less root used to return 0 BEFORE the run-manifest
        // branch was reachable, so the KSC dispatch was silently free in career (the
        // zero fed BOTH the funds eligibility gate and the charge). The parts basis
        // must fall back to the first member that HAS a snapshot while the resource
        // term stays on the root's complete launch manifest.
        [Fact]
        public void ComputeDispatchFundsCostForRoute_SnapshotlessRoot_FallsBackToMemberParts_KeepsRootLaunchManifest()
        {
            RunRoverFundsCase(
                CompleteRootManifest(), rootHasSnapshot: false,
                RoverRootId, RoverTransportId, RoverDockMergeId);

            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("FundsCost basis=launch-manifest")
                && l.Contains("source=" + RoverRootId)
                && l.Contains("snapshotSource=" + RoverTransportId)
                && l.Contains("fallback=1"));
            Assert.Contains(logLines, l =>
                l.Contains("has no VesselSnapshot - parts basis falls back to member "
                    + RoverTransportId));
            // The TRANSPORT's snapshot was walked; the combined dock-merge snapshot
            // was not (that would charge for the depot every cycle).
            Assert.True(VisitedPart(TransportPartName));
            Assert.False(VisitedPart(DepotPartName));
            Assert.DoesNotContain(logLines, l => l.Contains("UNCOSTED"));
        }

        // catches: the fallback silently promoting an INCOMPLETE root manifest onto
        // the launch basis. With no complete manifest the resource term must come
        // from the chosen member's snapshot through the legacy stop-snapshot walk -
        // the same completeness gate the root-snapshot path applies.
        [Fact]
        public void ComputeDispatchFundsCostForRoute_SnapshotlessRoot_IncompleteManifest_UsesMemberSnapshotResources()
        {
            RunRoverFundsCase(
                new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { 100001u },
                    StartTransportResources = new Dictionary<string, ResourceAmount>
                    {
                        ["LiquidFuel"] = new ResourceAmount { amount = 97.6, maxAmount = 100.0 },
                    },
                    EndCaptured = false,
                },
                rootHasSnapshot: false,
                RoverRootId, RoverTransportId, RoverDockMergeId);

            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("FundsCost basis=stop-snapshot")
                && l.Contains("snapshotSource=" + RoverTransportId)
                && l.Contains("fallback=1"));
        }

        // catches: the fallback pricing the COMBINED (dock-merged) vessel. The
        // dock-merged child IS a SourceRefs member - RouteBuilder adds the
        // window-carrying leaf unconditionally - and its snapshot is transport +
        // endpoint, so charging from it would bill the player for the destination
        // station on every cycle. Ordered here with the merged child AHEAD of the
        // transport so the walk must skip it rather than merely never reach it.
        [Fact]
        public void ComputeDispatchFundsCostForRoute_SnapshotlessRoot_SkipsDockMergedMember()
        {
            RunRoverFundsCase(
                CompleteRootManifest(), rootHasSnapshot: false,
                RoverRootId, RoverDockMergeId, RoverTransportId);

            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("snapshotSource=" + RoverTransportId)
                && l.Contains("fallback=1"));
            Assert.Contains(logLines, l =>
                l.Contains("skipping member " + RoverDockMergeId)
                && l.Contains("dock-merged (combined-vessel) snapshot"));
            Assert.True(VisitedPart(TransportPartName));
            Assert.False(VisitedPart(DepotPartName));
        }

        // catches: the "no basis at all" case going silent again. When the root has
        // no snapshot and the only other member is the dock-merged child, there IS
        // no honest basis - the dispatch stays uncosted, and that must say so in a
        // line distinct from the fallback breadcrumb (rate-limited: this method runs
        // per UI repaint).
        [Fact]
        public void ComputeDispatchFundsCostForRoute_NoMemberWithUsableSnapshot_ReturnsZero_LogsUncosted()
        {
            double cost = RunRoverFundsCase(
                CompleteRootManifest(), rootHasSnapshot: false,
                RoverRootId, RoverDockMergeId);

            Assert.Equal(0.0, cost);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("FundsCost: route ")
                && l.Contains("UNCOSTED")
                && l.Contains("a career KSC dispatch charges nothing"));
            Assert.DoesNotContain(logLines, l => l.Contains("FundsCost basis="));
            Assert.False(VisitedPart(DepotPartName));
        }

        // catches: the combined-vessel predicate losing one of its three positive
        // facts. Each disjunct identifies a dock-merged member on its own, and a
        // pre-dock transport leg must NOT match any of them.
        [Fact]
        public void IsCombinedVesselSourceMember_MatchesEachPositiveFact_NotThePreDockTransport()
        {
            var route = new Route
            {
                Id = "route-predicate",
                RecordedDockUT = RoverDockUT,
                DockMemberRecordingId = RoverDockMergeId,
            };

            // (1) carries a connection window (the dock-merge site's own stamp).
            var windowed = RoverMember("member-windowed", 5, PartsSnapshot("x"),
                RoverRootStartUT, RoverDockUT);
            windowed.RouteConnectionWindows = new List<RouteConnectionWindow>
            {
                new RouteConnectionWindow { WindowId = "w", DockUT = RoverDockUT },
            };
            Assert.True(RouteOrchestrator.IsCombinedVesselSourceMember(route, null, windowed));

            // (2) it is the route's own named dock member.
            var named = RoverMember(RoverDockMergeId, 6, PartsSnapshot("x"),
                RoverRootStartUT, RoverDockUT);
            Assert.True(RouteOrchestrator.IsCombinedVesselSourceMember(route, null, named));

            // (3) it starts at or after the route's dock UT.
            var postDock = RoverMember("member-post-dock", 7, PartsSnapshot("x"),
                RoverDockUT, RoverDockUT + 100.0);
            Assert.True(RouteOrchestrator.IsCombinedVesselSourceMember(route, null, postDock));

            // The pre-dock transport leg matches none of them.
            var transport = RoverMember(RoverTransportId, 1, PartsSnapshot(TransportPartName),
                RoverRootStartUT + 100.0, RoverDockUT);
            Assert.False(RouteOrchestrator.IsCombinedVesselSourceMember(route, null, transport));
        }

        // ==================================================================
        // ROUTE-DISPATCH-COST-FREE-ON-SNAPSHOTLESS-ROOT, ROUND 2
        // (measured live by lane RVR-4 on the career fixture, 2026-09-01)
        //
        // The round-1 fallback assumed a separate un-merged TRANSPORT member to
        // fall back to. RouteBuilder does not produce one for this tree shape:
        // ComputeMemberRecordingIds collects one id per composition THROUGH-LINE
        // HEAD, and the transport's driving legs are further structural intervals
        // of the same vessel line whose HeadLegId strips back to the root id. The
        // flight logged "keptIntervals=1 members=1" -> SourceRefs are exactly
        // [snapshot-less root, dock-merged leaf] ("members=2 excluded=3").
        //
        // Blocker 2, also measured: by dispatch time NO member had a
        // VesselSnapshot at all. ParsekScenario's OnLoad crew-auto-unreserve
        // sweep nulls VesselSnapshot for every committed recording with
        // !VesselSpawned && currentUT > EndUT ("Auto-unreserved crew for
        // recording #2 (B)" / "#3 (B)"), which is why the flight never even
        // logged the combined-member skip. GhostVisualSnapshot survives that
        // sweep and is the durable costing surface.
        // ==================================================================

        private const uint RoverTransportPartPid = 100001u;
        private const uint RoverDepotPartPid = 200001u;

        /// <summary>
        /// The dock-merged (combined) snapshot the real tree produces: the
        /// transport's tank plus the endpoint depot's tank, each pid-stamped and
        /// each carrying LiquidFuel, with DISTINCT part names so the
        /// "Unknown part cost: name=" warns prove which side was walked.
        /// </summary>
        private static ConfigNode MergedPartsSnapshot()
        {
            var snapshot = new ConfigNode("VESSEL");
            AddPricedPart(snapshot, TransportPartName, RoverTransportPartPid, 40.0);
            AddPricedPart(snapshot, DepotPartName, RoverDepotPartPid, 500.0);
            return snapshot;
        }

        private static void AddPricedPart(
            ConfigNode snapshot, string partName, uint pid, double liquidFuel)
        {
            ConfigNode part = snapshot.AddNode("PART");
            part.AddValue("name", partName);
            part.AddValue("persistentId",
                pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            ConfigNode res = part.AddNode("RESOURCE");
            res.AddValue("name", "LiquidFuel");
            res.AddValue("amount",
                liquidFuel.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// The REAL rover member set: SourceRefs = [snapshot-less root,
        /// dock-merged leaf], nothing else. <paramref name="transportPids"/> is
        /// the connection window's transport pid set (null / empty models a
        /// window that never captured one). When
        /// <paramref name="mergedSurfaceIsGhost"/> the merged leaf's
        /// VesselSnapshot is null and only GhostVisualSnapshot survives - the
        /// post-OnLoad-sweep state.
        /// </summary>
        private double RunRoverSubsetCase(
            RouteRunCargoManifest rootManifest,
            List<uint> transportPids,
            bool mergedSurfaceIsGhost)
        {
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            try
            {
                Recording root = RoverMember(
                    RoverRootId, 0, null, RoverRootStartUT, RoverDockUT);
                root.RouteRunManifest = rootManifest;

                ConfigNode merged = MergedPartsSnapshot();
                Recording mergedRec = RoverMember(
                    RoverDockMergeId, 2,
                    mergedSurfaceIsGhost ? null : merged,
                    RoverDockUT, RoverDockUT + 500.0);
                if (mergedSurfaceIsGhost)
                    mergedRec.GhostVisualSnapshot = merged;
                mergedRec.RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "w-rover",
                        DockUT = RoverDockUT,
                        UndockUT = RoverDockUT + 400.0,
                        TransportPartPersistentIds = transportPids,
                        EndpointPartPersistentIds = new List<uint> { RoverDepotPartPid },
                    },
                };

                var refs = new List<RouteSourceRef>();
                foreach (Recording member in new[] { root, mergedRec })
                {
                    RecordingStore.AddRecordingWithTreeForTesting(member);
                    refs.Add(new RouteSourceRef
                    {
                        RecordingId = member.RecordingId,
                        TreeId = RoverTreeId,
                        TreeOrder = member.TreeOrder,
                        StartUT = member.StartUT,
                        EndUT = member.EndUT,
                    });
                }

                var route = new Route
                {
                    Id = "route-rover-subset",
                    RecordedDockUT = RoverDockUT,
                    DockMemberRecordingId = RoverDockMergeId,
                    SourceRefs = refs,
                };

                return RouteOrchestrator.ComputeDispatchFundsCostForRoute(route);
            }
            finally
            {
                RecordingStore.ResetForTesting();
                EffectiveState.ResetCachesForTesting();
                ParsekScenario.ResetInstanceForTesting();
            }
        }

        // catches: THE measured defect. With the merged leaf as the only
        // snapshot-bearing member the dispatch was free; it must now price the
        // TRANSPORT subset of the combined snapshot, keeping the root's complete
        // launch manifest as the resource term.
        [Fact]
        public void ComputeDispatchFundsCostForRoute_OnlyDockMergedMember_PricesTransportSubset()
        {
            RunRoverSubsetCase(
                CompleteRootManifest(),
                new List<uint> { RoverTransportPartPid },
                mergedSurfaceIsGhost: false);

            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("FundsCost basis=launch-manifest")
                && l.Contains("source=" + RoverRootId)
                && l.Contains("snapshotSource=" + RoverDockMergeId)
                && l.Contains("fallback=1 subset=transport snapshotSurface=vessel parts=1/2"));
            Assert.Contains(logLines, l =>
                l.Contains("no un-merged member carries a snapshot")
                && l.Contains("RESTRICTED to window w-rover transport pids"));
            // Only the transport half of the combined snapshot was walked.
            Assert.True(VisitedPart(TransportPartName));
            Assert.False(VisitedPart(DepotPartName));
            Assert.DoesNotContain(logLines, l => l.Contains("UNCOSTED"));
        }

        // catches: an incomplete root manifest silently promoting to the launch
        // basis. With no complete manifest the resource term comes from the
        // snapshot walk - which, restricted, excludes the depot tank.
        [Fact]
        public void ComputeDispatchFundsCostForRoute_OnlyDockMergedMember_IncompleteManifest_UsesFilteredSnapshotResources()
        {
            RunRoverSubsetCase(
                new RouteRunCargoManifest
                {
                    TransportPartPersistentIds = new List<uint> { RoverTransportPartPid },
                    StartTransportResources = new Dictionary<string, ResourceAmount>
                    {
                        ["LiquidFuel"] = new ResourceAmount { amount = 97.6, maxAmount = 100.0 },
                    },
                    EndCaptured = false,
                },
                new List<uint> { RoverTransportPartPid },
                mergedSurfaceIsGhost: false);

            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("FundsCost basis=stop-snapshot")
                && l.Contains("snapshotSource=" + RoverDockMergeId)
                && l.Contains("fallback=1 subset=transport snapshotSurface=vessel parts=1/2"));
            // The depot part was never visited, so neither was its 500-unit tank:
            // the resource term of the restricted walk is transport-only.
            Assert.True(VisitedPart(TransportPartName));
            Assert.False(VisitedPart(DepotPartName));
        }

        // catches: an empty / missing transport pid set falling OPEN and pricing
        // the combined vessel. With nothing to separate transport from endpoint
        // the dispatch must stay UNCOSTED.
        [Fact]
        public void ComputeDispatchFundsCostForRoute_EmptyTransportPidSet_StaysUncosted()
        {
            double cost = RunRoverSubsetCase(
                CompleteRootManifest(), new List<uint>(), mergedSurfaceIsGhost: false);

            Assert.Equal(0.0, cost);
            Assert.Contains(logLines, l =>
                l.Contains("carries no usable transport pid set")
                && l.Contains("refusing to price the combined vessel"));
            Assert.Contains(logLines, l =>
                l.Contains("UNCOSTED")
                && l.Contains("a career KSC dispatch charges nothing"));
            Assert.DoesNotContain(logLines, l => l.Contains("FundsCost basis="));
            Assert.False(VisitedPart(DepotPartName));
        }

        // catches: a transport pid set that matches NOTHING in the chosen surface
        // (a pid-less snapshot, or pids from a different launch) charging the
        // resource term alone - a plausible-looking but wrong bill.
        [Fact]
        public void ComputeDispatchFundsCostForRoute_TransportPidsMatchNoPart_StaysUncosted()
        {
            double cost = RunRoverSubsetCase(
                CompleteRootManifest(),
                new List<uint> { 999999u },       // in no PART node of the snapshot
                mergedSurfaceIsGhost: false);

            Assert.Equal(0.0, cost);
            Assert.Contains(logLines, l =>
                l.Contains("transport pid subset matched 0 of 2 PART node(s)")
                && l.Contains("refusing a parts-less charge"));
            Assert.Contains(logLines, l => l.Contains("UNCOSTED"));
            Assert.DoesNotContain(logLines, l => l.Contains("FundsCost basis="));
        }

        // catches: blocker 2 on the SUBSET basis. After a load the merged leaf has
        // no VesselSnapshot either (the OnLoad crew-auto-unreserve sweep nulled
        // it); the surviving GhostVisualSnapshot must carry the pricing, and the
        // line must say which surface was used.
        [Fact]
        public void ComputeDispatchFundsCostForRoute_GhostSurfaceOnMergedMember_PricesTransportSubset()
        {
            RunRoverSubsetCase(
                CompleteRootManifest(),
                new List<uint> { RoverTransportPartPid },
                mergedSurfaceIsGhost: true);

            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("FundsCost basis=launch-manifest")
                && l.Contains("snapshotSource=" + RoverDockMergeId)
                && l.Contains("fallback=1 subset=transport snapshotSurface=ghost parts=1/2"));
            Assert.True(VisitedPart(TransportPartName));
            Assert.False(VisitedPart(DepotPartName));
            Assert.DoesNotContain(logLines, l => l.Contains("UNCOSTED"));
        }

        // catches: blocker 2 on the PREFERRED (root) basis. A root that was priced
        // directly before a save must still be priced directly after a load, off
        // its ghost surface, with fallback=0 and no subset.
        [Fact]
        public void ComputeDispatchFundsCostForRoute_GhostSurfaceOnRoot_PricesRootDirectly()
        {
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            try
            {
                Recording root = RoverMember(
                    RoverRootId, 0, null, RoverRootStartUT, RoverDockUT);
                root.RouteRunManifest = CompleteRootManifest();
                root.GhostVisualSnapshot = PartsSnapshot(RootPartName);
                RecordingStore.AddRecordingWithTreeForTesting(root);

                var route = new Route
                {
                    Id = "route-rover-ghost-root",
                    RecordedDockUT = RoverDockUT,
                    DockMemberRecordingId = RoverDockMergeId,
                    SourceRefs = new List<RouteSourceRef>
                    {
                        new RouteSourceRef
                        {
                            RecordingId = RoverRootId,
                            TreeId = RoverTreeId,
                            TreeOrder = 0,
                            StartUT = root.StartUT,
                            EndUT = root.EndUT,
                        },
                    },
                };

                RouteOrchestrator.ComputeDispatchFundsCostForRoute(route);
            }
            finally
            {
                RecordingStore.ResetForTesting();
                EffectiveState.ResetCachesForTesting();
                ParsekScenario.ResetInstanceForTesting();
            }

            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("FundsCost basis=launch-manifest")
                && l.Contains("snapshotSource=" + RoverRootId)
                && l.Contains("fallback=0 snapshotSurface=ghost"));
            Assert.DoesNotContain(logLines, l => l.Contains("subset=transport"));
            Assert.True(VisitedPart(RootPartName));
            Assert.DoesNotContain(logLines, l => l.Contains("UNCOSTED"));
        }

        // catches: the window selector picking an arbitrary window on a
        // multi-dock member. The route's own RecordedDockUT wins; with no match
        // the EARLIEST dock does (the transport pid set is a launch fact).
        [Fact]
        public void ResolveTransportSubsetWindow_PrefersRouteDockUT_ThenEarliest()
        {
            var early = new RouteConnectionWindow { WindowId = "early", DockUT = RoverDockUT - 500.0 };
            var exact = new RouteConnectionWindow { WindowId = "exact", DockUT = RoverDockUT };
            var late = new RouteConnectionWindow { WindowId = "late", DockUT = RoverDockUT + 500.0 };

            var member = RoverMember("multi-dock", 3, PartsSnapshot("x"),
                RoverDockUT, RoverDockUT + 900.0);
            member.RouteConnectionWindows = new List<RouteConnectionWindow> { late, exact, early };

            var matching = new Route { Id = "r1", RecordedDockUT = RoverDockUT };
            Assert.Same(exact, RouteOrchestrator.ResolveTransportSubsetWindow(matching, member));

            // No route dock UT to match on -> earliest dock.
            var unanchored = new Route { Id = "r2", RecordedDockUT = -1.0 };
            Assert.Same(early, RouteOrchestrator.ResolveTransportSubsetWindow(unanchored, member));

            // No windows at all -> null (the caller then stays UNCOSTED).
            var windowless = RoverMember("windowless", 4, PartsSnapshot("x"),
                RoverDockUT, RoverDockUT + 10.0);
            Assert.Null(RouteOrchestrator.ResolveTransportSubsetWindow(matching, windowless));
        }

        // catches: the costing-surface resolver preferring the wrong surface, or
        // reporting a surface name the FundsCost line does not use.
        [Fact]
        public void ResolveCostingSnapshot_PrefersVessel_FallsBackToGhost()
        {
            ConfigNode vesselNode = PartsSnapshot("v");
            ConfigNode ghostNode = PartsSnapshot("g");

            var both = RoverMember("both", 0, vesselNode, 0.0, 1.0);
            both.GhostVisualSnapshot = ghostNode;
            Assert.Same(vesselNode, RouteOrchestrator.ResolveCostingSnapshot(both, out string s1));
            Assert.Equal("vessel", s1);

            var ghostOnly = RoverMember("ghost-only", 0, null, 0.0, 1.0);
            ghostOnly.GhostVisualSnapshot = ghostNode;
            Assert.Same(ghostNode, RouteOrchestrator.ResolveCostingSnapshot(ghostOnly, out string s2));
            Assert.Equal("ghost", s2);

            var neither = RoverMember("neither", 0, null, 0.0, 1.0);
            Assert.Null(RouteOrchestrator.ResolveCostingSnapshot(neither, out string s3));
            Assert.Null(s3);

            Assert.Null(RouteOrchestrator.ResolveCostingSnapshot(null, out string s4));
            Assert.Null(s4);
        }
    }
}
