using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Orchestrator shell for the route dispatch scheduler. Driven from
    /// <c>ParsekScenario.Update</c> at the cadence defined by
    /// <see cref="TickIntervalSec"/>. Pure-logic decisions live in
    /// <see cref="RouteDispatchEvaluator"/>; this class wires the evaluator
    /// up to live KSP state via <see cref="LiveRouteRuntimeEnvironment"/> and
    /// applies the resulting <see cref="RouteDispatchDecision"/> to the
    /// committed routes — including action emission on Dispatch / EndpointLost
    /// (Phase 4 of the dispatch-scheduler plan).
    /// </summary>
    internal static class RouteOrchestrator
    {
        /// <summary>Wall-clock cadence at which the orchestrator advances routes.</summary>
        internal const double TickIntervalSec = 1.0;

        /// <summary>
        /// Retry backoff for transient wait states (resources, funds, capacity,
        /// endpoint loss). Sets <c>Route.NextEligibilityCheckUT = currentUT + this</c>.
        /// </summary>
        internal const double WaitRetryIntervalSec = 30.0;

        /// <summary>
        /// Cap on the number of catch-up cycles processed in a single tick when
        /// the route is behind schedule (long unfocused warp). Prevents pathological
        /// per-tick work explosions on resume.
        /// </summary>
        internal const int MaxCatchUpCyclesPerTick = 32;

        /// <summary>
        /// Surface proximity radius (metres) for endpoint resolution fallback. When
        /// the saved <see cref="RouteEndpoint.VesselPersistentId"/> no longer
        /// resolves but the endpoint is surface-typed, the resolver searches for
        /// the closest matching surface vessel within this radius.
        /// </summary>
        internal const double SurfaceProximityRadiusMeters = 2000.0;

        /// <summary>
        /// Canonical <see cref="ParsekLog"/> subsystem tag for every route-subsystem
        /// log line. Do not introduce new tag names — the integration follow-up
        /// unified all route logs under this single tag.
        /// </summary>
        internal const string Tag = "Route";

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // ==================================================================
        // Tick entry points
        // ==================================================================

        /// <summary>
        /// Production tick entry point invoked from <c>ParsekScenario.Update</c>.
        /// Constructs a fresh <see cref="LiveRouteRuntimeEnvironment"/> for the
        /// tick (caches ERS internally for the duration of the call) and
        /// delegates to the env-injected overload.
        /// </summary>
        internal static void Tick(double currentUT)
        {
            Tick(currentUT, new LiveRouteRuntimeEnvironment());
        }

        /// <summary>
        /// Env-injected tick used by unit tests. Production calls the no-env
        /// overload which constructs a <see cref="LiveRouteRuntimeEnvironment"/>;
        /// tests supply a fake so xUnit can exercise the orchestrator without
        /// touching KSP statics.
        /// </summary>
        internal static void Tick(double currentUT, IRouteRuntimeEnvironment env)
        {
            if (env == null)
            {
                ParsekLog.Warn(Tag, "Tick: null env — skipped");
                return;
            }

            var routes = RouteStore.CommittedRoutes;
            if (routes == null || routes.Count == 0)
                return;

            // Snapshot the route list to avoid mid-iteration mutation if AddRoute
            // fires (Apply* methods do not call AddRoute today, but a user-driven
            // RouteCreationDialog can still mutate the store between Apply calls).
            int initialCount = routes.Count;
            Route[] snapshot = new Route[initialCount];
            for (int i = 0; i < initialCount; i++)
                snapshot[i] = routes[i];

            int tickedRoutes = 0;
            int dispatched = 0;
            int transitioned = 0;
            int skipped = 0;
            int errored = 0;

            for (int i = 0; i < snapshot.Length; i++)
            {
                Route route = snapshot[i];
                if (route == null) continue;
                tickedRoutes++;

                try
                {
                    ProcessOneRoute(route, currentUT, env,
                        ref dispatched, ref transitioned, ref skipped);
                }
                catch (Exception ex)
                {
                    errored++;
                    ParsekLog.Error(Tag,
                        $"Tick: route {ShortIdForLog(route)} threw {ex.GetType().Name}: {ex.Message}");
                }
            }

            ParsekLog.VerboseRateLimited(Tag, "route-tick",
                $"Tick: ut={currentUT.ToString("R", IC)} " +
                $"routes={tickedRoutes.ToString(IC)} " +
                $"dispatched={dispatched.ToString(IC)} " +
                $"transitioned={transitioned.ToString(IC)} " +
                $"skipped={skipped.ToString(IC)} " +
                $"errored={errored.ToString(IC)}");
        }

        /// <summary>
        /// Per-route catch-up loop. Re-evaluates the route until a terminal
        /// outcome lands (Skip / Dispatch / Wait* / EndpointLost /
        /// InTransitComplete). Dispatch transitions the route to InTransit and
        /// breaks immediately — an InTransit route cannot dispatch again in
        /// the same tick. The loop body is currently bounded by the evaluator
        /// always returning a terminal outcome on a single pass; the
        /// <see cref="MaxCatchUpCyclesPerTick"/> cap is defensive for future
        /// synodic-window dispatch that may legitimately fire multiple cycles
        /// per tick.
        /// </summary>
        private static void ProcessOneRoute(
            Route route, double currentUT, IRouteRuntimeEnvironment env,
            ref int dispatched, ref int transitioned, ref int skipped)
        {
            // Pre-evaluator delivery hook (item 6 Phase B). When a pending
            // delivery boundary has come due, apply the delivery FIRST and then
            // fall through so the dispatch evaluator can fire the next cycle in
            // the same tick if <c>NextDispatchUT</c> is also due. Status gates:
            // Paused and EndpointLost routes skip delivery entirely — applying
            // a delivery to a paused or aborted route would silently charge
            // funds the player did not authorize.
            if (route.PendingDeliveryUT.HasValue
                && route.PendingDeliveryUT.Value <= currentUT
                && route.Status != RouteStatus.Paused
                && route.Status != RouteStatus.EndpointLost)
            {
                ApplyDelivery(route, currentUT, env);
                transitioned++;
                // Intentionally do NOT return — fall through to the evaluator
                // loop so a cycle whose <c>NextDispatchUT</c> is also due can
                // dispatch the next cycle in the same tick. The
                // <see cref="MaxCatchUpCyclesPerTick"/> cap below bounds the
                // catch-up work if delivery + dispatch repeatedly land in the
                // same tick (e.g. long unfocused warp).
            }

            // CS0162 suppression: every switch branch below `return`s on the
            // first iteration today, so the loop increment is currently
            // unreachable. The loop body is the forward-looking shape for
            // future synodic / catch-up dispatch that legitimately fires
            // multiple cycles per tick (see XML doc on MaxCatchUpCyclesPerTick).
#pragma warning disable CS0162
            for (int iter = 0; iter < MaxCatchUpCyclesPerTick; iter++)
            {
                RouteDispatchDecision decision = RouteDispatchEvaluator.EvaluateRoute(route, currentUT, env);

                switch (decision.Outcome)
                {
                    case RouteDispatchOutcome.Skip:
                        skipped++;
                        return;

                    case RouteDispatchOutcome.Dispatch:
                        ApplyDispatch(route, currentUT, env);
                        dispatched++;
                        transitioned++;
                        // Route is now InTransit — cannot dispatch again until
                        // arrival. Returning here is the v0 contract.
                        return;

                    case RouteDispatchOutcome.WaitResources:
                    case RouteDispatchOutcome.WaitFunds:
                    case RouteDispatchOutcome.WaitDestinationFull:
                        ApplyWait(route, decision);
                        transitioned++;
                        return;

                    case RouteDispatchOutcome.EndpointLost:
                        ApplyEndpointLost(route, currentUT, decision);
                        transitioned++;
                        return;

                    case RouteDispatchOutcome.InTransitComplete:
                        ApplyInTransitComplete(route, currentUT);
                        transitioned++;
                        return;

                    default:
                        ParsekLog.Warn(Tag,
                            $"ProcessOneRoute: unknown outcome {decision.Outcome} for route {ShortIdForLog(route)} — skipping");
                        return;
                }
            }

            // Forward-looking defensive guard. Today the evaluator always returns
            // a terminal outcome on the first call so this branch is unreachable
            // from production; once synodic / catch-up dispatch lands it becomes
            // the cap for legitimate multi-cycle ticks.
            ParsekLog.Warn(Tag,
                $"ProcessOneRoute: route {ShortIdForLog(route)} hit max catch-up iter " +
                $"({MaxCatchUpCyclesPerTick.ToString(IC)}), aborting per-route loop");
#pragma warning restore CS0162
        }

        // ==================================================================
        // Outcome appliers
        // ==================================================================

        /// <summary>
        /// Dispatch happy-path: compute KSC funds cost, emit
        /// <see cref="GameActionType.RouteDispatched"/> +
        /// <see cref="GameActionType.RouteCargoDebited"/> ledger rows,
        /// transition the route to <see cref="RouteStatus.InTransit"/>, and
        /// advance <c>NextDispatchUT</c>. <c>NextEligibilityCheckUT</c> is
        /// cleared because the route is no longer in a wait state.
        /// </summary>
        private static void ApplyDispatch(Route route, double currentUT, IRouteRuntimeEnvironment env)
        {
            // Cycle id pins the dispatch + debit pair together in the ledger.
            // Format is "cycle-<N>" where N = total cycles ever started for this
            // route (completed + skipped — both increment past cycles even
            // though SkippedCycles is currently informational only).
            //
            // Cycle id formula: cycle-{CompletedCycles + SkippedCycles}. Item-6
            // RouteCargoDelivered must use the SAME formula so dispatch/debit/
            // deliver triple stays aligned per cycle. First cycle is cycle-0
            // (both counters start at 0).
            string cycleId = "cycle-" + (route.CompletedCycles + route.SkippedCycles).ToString(IC);

            // Funds cost: only meaningful for Career + KSC origin. We compute it
            // unconditionally so the persisted KscDispatchFundsCost stays in sync
            // with what the evaluator's funds gate saw, but the emitted action
            // only carries the cost when the actual charge would be non-zero.
            bool isCareerKsc = env.IsCareer && route.IsKscOrigin;
            double computedCost = 0.0;
            if (isCareerKsc)
            {
                computedCost = ComputeDispatchFundsCostForRoute(route);
                // Route.KscDispatchFundsCost is double-typed; preserve full
                // precision here. The ledger-row cast below is the only float
                // truncation, intentional because GameAction defines the field
                // as float.
                route.KscDispatchFundsCost = computedCost;
            }

            // RouteDispatched — the scheduler-decision marker. No manifest;
            // the debit row carries the resources/funds payload.
            var dispatchedAction = new GameAction
            {
                Type = GameActionType.RouteDispatched,
                UT = currentUT,
                RouteId = route.Id,
                RouteCycleId = cycleId,
                RouteStopIndex = -1,
                Sequence = 0,
            };

            // RouteCargoDebited — the physical/funds debit. Carries the cost
            // manifest (resource amounts) and the KSC funds cost. Sequence=1
            // pins the debit AFTER the dispatched row at the same UT, which
            // matters for any future walker that orders by (UT, Sequence).
            var debitedAction = new GameAction
            {
                Type = GameActionType.RouteCargoDebited,
                UT = currentUT,
                RouteId = route.Id,
                RouteCycleId = cycleId,
                RouteStopIndex = -1,
                Sequence = 1,
                RouteResourceManifest = CloneManifest(route.CostManifest),
                // RouteKscFundsCost is float-typed on GameAction by design;
                // double precision is preserved on Route.KscDispatchFundsCost
                // for diagnostics.
                RouteKscFundsCost = isCareerKsc ? (float)computedCost : 0f,
            };

            Ledger.AddActions(new[] { dispatchedAction, debitedAction });

            // State transition: Active/Wait* → InTransit, advance the next-due
            // UT by one DispatchInterval, clear the retry timer.
            route.TransitionTo(RouteStatus.InTransit, "dispatched");
            route.CurrentCycleStartUT = currentUT;
            route.NextDispatchUT = currentUT + route.DispatchInterval;
            route.NextEligibilityCheckUT = null;

            ParsekLog.Info(Tag,
                $"Dispatch: route {ShortIdForLog(route)} cycle={cycleId} " +
                $"ut={currentUT.ToString("R", IC)} " +
                $"cost={computedCost.ToString("R", IC)} " +
                $"careerKsc={(isCareerKsc ? "1" : "0")} " +
                $"nextDispatchUT={route.NextDispatchUT.ToString("R", IC)}");
        }

        /// <summary>
        /// Wait-state applier (resources / funds / destination-full). Updates
        /// <c>Status</c> and <c>NextEligibilityCheckUT</c>; per design §10.4
        /// <c>NextDispatchUT</c> is intentionally NOT touched, so a destination-
        /// full route holds its cycle slot until the destination has capacity.
        /// </summary>
        private static void ApplyWait(Route route, RouteDispatchDecision decision)
        {
            if (decision.NextStatus.HasValue)
                route.TransitionTo(decision.NextStatus.Value, decision.Reason);

            route.NextEligibilityCheckUT = decision.NewNextEligibilityCheckUT;

            // §10.4: do NOT advance NextDispatchUT for any wait state. The route
            // re-evaluates at NextEligibilityCheckUT and either dispatches at the
            // original cycle slot or remains queued.

            ParsekLog.Info(Tag,
                $"Wait: route {ShortIdForLog(route)} " +
                $"status={(decision.NextStatus.HasValue ? decision.NextStatus.Value.ToString() : "<none>")} " +
                $"reason={decision.Reason ?? "<none>"} " +
                $"retryAt={(decision.NewNextEligibilityCheckUT.HasValue ? decision.NewNextEligibilityCheckUT.Value.ToString("R", IC) : "<none>")}");
        }

        /// <summary>
        /// Endpoint-lost applier. Transitions status, sets the retry timer, and
        /// emits a <see cref="GameActionType.RouteEndpointLost"/> ledger row so
        /// the timeline records the failure. Design §10.1/§10.2 — endpoint loss
        /// is distinct from a player Pause because it may auto-recover via the
        /// surface-proximity fallback on a later tick.
        /// </summary>
        private static void ApplyEndpointLost(Route route, double currentUT, RouteDispatchDecision decision)
        {
            if (decision.NextStatus.HasValue)
                route.TransitionTo(decision.NextStatus.Value, decision.Reason);

            route.NextEligibilityCheckUT = decision.NewNextEligibilityCheckUT;

            var action = new GameAction
            {
                Type = GameActionType.RouteEndpointLost,
                UT = currentUT,
                RouteId = route.Id,
                RouteStopIndex = -1,
                Sequence = 0,
                RouteEndpointReason = decision.Reason,
            };
            Ledger.AddAction(action);

            ParsekLog.Info(Tag,
                $"EndpointLost: route {ShortIdForLog(route)} " +
                $"reason={decision.Reason ?? "<none>"} " +
                $"retryAt={(decision.NewNextEligibilityCheckUT.HasValue ? decision.NewNextEligibilityCheckUT.Value.ToString("R", IC) : "<none>")}");
        }

        /// <summary>
        /// In-transit arrival applier. Sets <c>PendingDeliveryUT</c> + stop
        /// index so the upcoming item-6 delivery code can pick the boundary
        /// up. Status stays <see cref="RouteStatus.InTransit"/>: delivery
        /// completion (and the next-cycle reset) is owned by the delivery
        /// pipeline, not the dispatch orchestrator.
        /// </summary>
        internal static void ApplyInTransitComplete(Route route, double currentUT)
        {
            // The evaluator guards against re-emission via the
            // !route.PendingDeliveryUT.HasValue check, so this applier only
            // runs on the FIRST arrival. Guard defensively anyway in case a
            // future evaluator change relaxes the gate.
            if (route.PendingDeliveryUT.HasValue)
            {
                ParsekLog.Verbose(Tag,
                    $"InTransitComplete: route {ShortIdForLog(route)} already has " +
                    $"PendingDeliveryUT={route.PendingDeliveryUT.Value.ToString("R", IC)}; skipping re-set");
                return;
            }

            route.PendingDeliveryUT = currentUT;
            // v0: single-stop routes (design §11). Stop index 0 is the only
            // stop on every route; multi-stop will reuse this field with the
            // actual stop index resolved from CurrentSegmentIndex.
            route.PendingStopIndex = 0;

            ParsekLog.Info(Tag,
                $"InTransitComplete: route {ShortIdForLog(route)} reached transit boundary; " +
                $"PendingDeliveryUT={currentUT.ToString("R", IC)} stopIndex=0");
        }

        // ==================================================================
        // Delivery applier (item 6 Phase B)
        // ==================================================================

        /// <summary>
        /// Apply a pending delivery cycle (item 6 Phase B). Re-resolves the
        /// destination endpoint, builds a capacity probe against the live
        /// vessel (loaded or unloaded), asks the pure planner what to deliver,
        /// applies the planned resource + inventory writes, debits Career
        /// funds when applicable, emits a <see cref="GameActionType.RouteCargoDelivered"/>
        /// ledger row, and transitions the route back to
        /// <see cref="RouteStatus.Active"/>. Idempotent against ledger replay
        /// via an ELS lookup keyed on <c>(RouteId, RouteCycleId)</c>.
        /// </summary>
        private static void ApplyDelivery(Route route, double currentUT, IRouteRuntimeEnvironment env)
        {
            // Cycle id matches the dispatch/debit pair (cycle-{Completed + Skipped}).
            // The dispatch applier bumps neither counter; delivery is the FIRST
            // place CompletedCycles increments, so the cycle id at delivery time
            // still names the in-flight cycle.
            string cycleId = "cycle-" + (route.CompletedCycles + route.SkippedCycles).ToString(IC);

            // Stop index from the dispatch-arrival hand-off. v0 single-stop
            // routes always land at stopIndex=0; multi-stop will reuse this
            // field with the actual stop index resolved from CurrentSegmentIndex.
            int stopIndex = route.PendingStopIndex >= 0 ? route.PendingStopIndex : 0;

            // STEP 1: idempotency guard. If ELS already contains a
            // RouteCargoDelivered row for (routeId, cycleId), the cycle was
            // delivered on a previous tick (or recovered after crash); applying
            // again would double-charge funds and double-write resources. This
            // is the orchestrator's ONLY ELS read — every other consumer
            // routes through ERS or accepts dispatch evaluator's surface.
            if (IsDeliveryAlreadyInLedger(route.Id, cycleId))
            {
                ParsekLog.Verbose(Tag,
                    $"Delivery: route {ShortIdForLog(route)} cycle={cycleId} replay detected — already in ledger");
                route.PendingDeliveryUT = null;
                route.PendingStopIndex = -1;
                // Clear any stale retry timer carried over from a pre-dispatch
                // wait state — mirrors the success branch in
                // ApplyDeliveryFromPlan and the ApplyDispatch transition so
                // a delivered cycle never leaves the route gated behind an
                // old WaitRetryIntervalSec deadline.
                route.NextEligibilityCheckUT = null;
                route.TransitionTo(RouteStatus.Active, "delivered-replay");
                return;
            }

            // STEP 2: re-resolve endpoint vessel. The dispatch evaluator already
            // resolved the endpoint at dispatch time, but during transit (which
            // can span many hours of warp) the destination may have been
            // destroyed, recovered, or simply moved out of the surface-proximity
            // radius. Re-resolving here is the only way to avoid forging a
            // delivery onto a vessel that no longer exists.
            RouteStop stop = (route.Stops != null && stopIndex < route.Stops.Count) ? route.Stops[stopIndex] : null;
            if (stop == null)
            {
                ParsekLog.Warn(Tag,
                    $"Delivery: route {ShortIdForLog(route)} cycle={cycleId} stopIndex={stopIndex.ToString(IC)} " +
                    $"missing — aborting delivery and clearing pending state");
                route.PendingDeliveryUT = null;
                route.PendingStopIndex = -1;
                return;
            }

            if (!env.TryResolveEndpointVessel(stop.Endpoint, out Vessel destVessel, out string endpointReason)
                || destVessel == null)
            {
                ParsekLog.Info(Tag,
                    $"Delivery: route {ShortIdForLog(route)} cycle={cycleId} endpoint lost at delivery " +
                    $"reason={endpointReason ?? "<none>"}");
                EmitEndpointLostAction(route, currentUT, "endpoint-destroyed-at-delivery:" + (endpointReason ?? "unknown"));
                route.TransitionTo(RouteStatus.EndpointLost, "endpoint-lost-at-delivery");
                route.PendingDeliveryUT = null;
                route.PendingStopIndex = -1;
                route.NextEligibilityCheckUT = currentUT + WaitRetryIntervalSec;
                return;
            }

            // STEP 3: build a live capacity probe over the destination vessel.
            // Loaded (rendered, parts list populated) vs unloaded (background-
            // physics protoVessel) is decided here once; the probe handles both.
            LiveDeliveryCapacityProbe probe = new LiveDeliveryCapacityProbe(destVessel);

            // STEP 4: planner. Pure decision over the resource + inventory
            // manifest, capacity-clamped per resource and slot-aware for
            // inventory. The planner is unit-tested in isolation (Phase A).
            DeliveryPlan plan = RouteDeliveryPlanner.PrepareDelivery(route, stopIndex, probe);

            // STEP 5/6: apply the planned writes. Build an ApplyDeliveryContext
            // that funnels the live mutations behind delegates so the testable
            // helper does not need to instantiate Vessel / Funding /
            // ProtoPartSnapshot. The "writer" delegates are the only place
            // KSP-state mutation happens; ApplyDeliveryFromPlan owns the
            // bookkeeping (actuals, partial detection, status transition,
            // ledger row construction).
            var liveWriters = new LiveDeliveryWriters(route, destVessel, plan);
            bool isCareerKsc = env.IsCareer && route.IsKscOrigin;
            var ctx = new ApplyDeliveryContext
            {
                CycleId = cycleId,
                CurrentUT = currentUT,
                StopIndex = stopIndex,
                IsCareer = env.IsCareer,
                IsKscOrigin = route.IsKscOrigin,
                KscFundsCost = isCareerKsc ? route.KscDispatchFundsCost : 0.0,
                ResourceWriter = liveWriters.WriteResource,
                ResourceActualReader = liveWriters.ReadActualResource,
                InventoryWriter = liveWriters.WriteInventory,
                InventoryActualCountReader = liveWriters.ReadInventoryActualCount,
                FundsDebiter = LiveDebitFunds,
                LedgerEmitter = Ledger.AddAction,
            };
            ApplyDeliveryFromPlan(route, plan, ctx);
        }

        /// <summary>
        /// Test-injectable apply core. Idempotency, endpoint resolution, and
        /// the live-probe build all happen in <see cref="ApplyDelivery"/> (the
        /// wrapper); this method takes a pre-built <see cref="DeliveryPlan"/>
        /// and a context bag of delegates so xUnit can verify the apply
        /// bookkeeping (resource writer invocations, funds-debit conditional,
        /// ledger row shape, status transition) without instantiating any
        /// live KSP objects.
        /// </summary>
        internal static void ApplyDeliveryFromPlan(Route route, DeliveryPlan plan, ApplyDeliveryContext ctx)
        {
            // Apply resource writes. The writer delegate returns nothing — the
            // actual transferred amount is fetched separately via
            // <see cref="ApplyDeliveryContext.ResourceActualReader"/> so the
            // delegate signature stays simple. Planner guarantees Available <=
            // capacity, so for the unloaded path the writer can write the full
            // amount; the loaded path may transfer less if mid-tick resource
            // state changed.
            int resourceLinesApplied = 0;
            if (plan.Resources != null)
            {
                for (int i = 0; i < plan.Resources.Count; i++)
                {
                    ResourceDeliveryLine line = plan.Resources[i];
                    if (line.Available <= 0.0) continue;
                    ctx.ResourceWriter(line.Name, line.Available);
                    resourceLinesApplied++;
                }
            }

            // Apply inventory writes. Items with AssignedSlot < 0 were skipped
            // by the planner (no empty slot at probe time); the writer is only
            // called for assigned slots. The writer may itself fail (stock
            // <c>StoreCargoPartAtSlot</c> can return false on edge cases like
            // mass-limit overruns); the actual-count reader returns how many
            // writes succeeded.
            int inventoryLinesAttempted = 0;
            if (plan.Inventory != null)
            {
                for (int i = 0; i < plan.Inventory.Count; i++)
                {
                    InventoryDeliveryLine line = plan.Inventory[i];
                    if (line.AssignedSlot < 0) continue;
                    if (line.Item == null) continue;
                    ctx.InventoryWriter(line.Item, line.AssignedSlot);
                    inventoryLinesAttempted++;
                }
            }

            // STEP 7: Career funds debit. Skip silently in Sandbox/Science or
            // when the route is not a KSC origin — the dispatch evaluator's
            // KscFundsAvailable check is only consulted in (Career AND KSC),
            // so the debit must mirror that exact predicate to keep the
            // dispatch/delivery pair internally consistent.
            if (ctx.IsCareer && ctx.IsKscOrigin && ctx.KscFundsCost > 0.0)
            {
                ctx.FundsDebiter(ctx.KscFundsCost);
                ParsekLog.Info(Tag,
                    $"Delivery: route {ShortIdForLog(route)} Career KSC funds debited: " +
                    $"-{ctx.KscFundsCost.ToString("R", IC)}");
            }

            // STEP 8: build and emit the RouteCargoDelivered row. The actuals
            // are populated from the readers; the requested manifest is only
            // populated when the actual fell short of requested for at least
            // one resource (saves bytes on the happy full-fill case).
            Dictionary<string, double> actualManifest = null;
            Dictionary<string, double> requestedManifest = null;
            if (plan.Resources != null)
            {
                for (int i = 0; i < plan.Resources.Count; i++)
                {
                    ResourceDeliveryLine line = plan.Resources[i];
                    double actual = ctx.ResourceActualReader(line.Name);
                    if (actual > 0.0)
                    {
                        if (actualManifest == null)
                            actualManifest = new Dictionary<string, double>(plan.Resources.Count, StringComparer.Ordinal);
                        actualManifest[line.Name] = actual;
                    }
                    if (line.Available < line.Requested)
                    {
                        if (requestedManifest == null)
                            requestedManifest = new Dictionary<string, double>(plan.Resources.Count, StringComparer.Ordinal);
                        requestedManifest[line.Name] = line.Requested;
                    }
                }
            }

            var action = new GameAction
            {
                Type = GameActionType.RouteCargoDelivered,
                UT = ctx.CurrentUT,
                RouteId = route.Id,
                RouteCycleId = ctx.CycleId,
                RouteStopIndex = ctx.StopIndex,
                Sequence = 0,
                RouteResourceManifest = actualManifest,
                RouteRequestedResourceManifest = requestedManifest,
                // GameAction.RouteKscFundsCost is float-typed by design (same
                // as RouteCargoDebited). The double precision on
                // ApplyDeliveryContext.KscFundsCost is preserved upstream for
                // diagnostics.
                RouteKscFundsCost = (ctx.IsCareer && ctx.IsKscOrigin) ? (float)ctx.KscFundsCost : 0f,
            };
            ctx.LedgerEmitter(action);

            // STEP 9: mutate route state + transition. Delivery completion is
            // the only place CompletedCycles increments. Status transitions
            // back to Active with a reason string carrying the partial/full
            // discriminator so the UI can surface "delivered partial" badges.
            route.CompletedCycles += 1;
            route.PendingDeliveryUT = null;
            route.PendingStopIndex = -1;
            // Successful delivery exits the route from any wait state — clear
            // the retry timer so a future tick is not blocked by a stale
            // NextEligibilityCheckUT inherited from a pre-dispatch
            // WaitingForResources / WaitingForFunds / DestinationFull pass.
            // Mirrors ApplyDispatch (line ~301).
            route.NextEligibilityCheckUT = null;

            int inventoryActual = ctx.InventoryActualCountReader();
            route.TransitionTo(RouteStatus.Active, plan.IsPartial ? "delivered-partial" : "delivered");

            ParsekLog.Info(Tag,
                $"Delivery: route {ShortIdForLog(route)} cycle={ctx.CycleId} " +
                $"resources={resourceLinesApplied.ToString(IC)} " +
                $"inventory={inventoryActual.ToString(IC)}/{inventoryLinesAttempted.ToString(IC)} " +
                $"partial={(plan.IsPartial ? "1" : "0")} " +
                $"ut={ctx.CurrentUT.ToString("R", IC)}");
        }

        /// <summary>
        /// ELS-based idempotency check (item 6 Phase B). Returns <c>true</c> if
        /// any non-tombstoned <see cref="GameActionType.RouteCargoDelivered"/>
        /// row already exists for the given <paramref name="routeId"/> +
        /// <paramref name="cycleId"/> pair. This is the orchestrator's only
        /// raw-ELS read — every other ledger interaction in this file goes
        /// through <see cref="Ledger.AddAction"/> on the write side.
        /// </summary>
        private static bool IsDeliveryAlreadyInLedger(string routeId, string cycleId)
        {
            if (string.IsNullOrEmpty(routeId) || string.IsNullOrEmpty(cycleId))
                return false;

            IReadOnlyList<GameAction> els;
            try
            {
                els = EffectiveState.ComputeELS();
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"IsDeliveryAlreadyInLedger: ComputeELS threw {ex.GetType().Name}: {ex.Message}; treating as not-in-ledger");
                return false;
            }

            if (els == null) return false;
            for (int i = 0; i < els.Count; i++)
            {
                GameAction a = els[i];
                if (a == null) continue;
                if (a.Type != GameActionType.RouteCargoDelivered) continue;
                if (!string.Equals(a.RouteId, routeId, StringComparison.Ordinal)) continue;
                if (!string.Equals(a.RouteCycleId, cycleId, StringComparison.Ordinal)) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Emit a <see cref="GameActionType.RouteEndpointLost"/> ledger row from
        /// the delivery applier. Mirrors <see cref="ApplyEndpointLost"/> from
        /// the dispatch side but the reason string is delivery-specific so the
        /// ledger / UI can distinguish "endpoint lost before dispatch" from
        /// "endpoint destroyed mid-transit".
        /// </summary>
        private static void EmitEndpointLostAction(Route route, double currentUT, string reason)
        {
            var action = new GameAction
            {
                Type = GameActionType.RouteEndpointLost,
                UT = currentUT,
                RouteId = route.Id,
                RouteStopIndex = -1,
                Sequence = 0,
                RouteEndpointReason = reason,
            };
            Ledger.AddAction(action);
            ParsekLog.Info(Tag,
                $"EndpointLost: route {ShortIdForLog(route)} reason={reason ?? "<none>"} (at delivery)");
        }

        /// <summary>
        /// Resource-write filter shared by the live writers and the live
        /// capacity probe. Returns <c>true</c> only when the destination tank
        /// is BOTH:
        /// <list type="bullet">
        ///   <item><c>flowState == true</c> — the player has not closed the
        ///     tank's flow toggle. Writing to a closed tank silently violates
        ///     player intent (the tank is locked from the player's POV).</item>
        ///   <item><paramref name="flowMode"/> not <see cref="ResourceFlowMode.NO_FLOW"/>
        ///     — v0 simplicity: <c>SolidFuel</c>, <c>EVAPropellant</c>,
        ///     <c>Ablator</c>, etc. don't cross part boundaries in stock, so
        ///     depositing them via route delivery would be a stock contract
        ///     violation. v1+ may surface a player option to override.</item>
        /// </list>
        /// Extracting this as a pure helper lets xUnit pin the policy without
        /// touching live KSP <c>PartResource</c> / <c>ProtoPartResourceSnapshot</c>
        /// instances; the writers and probes call it at the tank-iteration
        /// boundary.
        /// </summary>
        internal static bool ShouldDeliverToResource(bool flowState, ResourceFlowMode flowMode)
        {
            if (!flowState) return false;
            if (flowMode == ResourceFlowMode.NO_FLOW) return false;
            return true;
        }

        /// <summary>
        /// Looks up the <see cref="ResourceFlowMode"/> for a named resource via
        /// <see cref="PartResourceLibrary"/>. Returns <see cref="ResourceFlowMode.ALL_VESSEL"/>
        /// (a "delivery allowed" value) when the library is not available or
        /// the resource is missing, so writes/probes don't accidentally suppress
        /// a legitimate delivery when the library is mid-load. <see cref="NO_FLOW"/>
        /// is a deliberate gate — only the explicit definition triggers the
        /// suppression in <see cref="ShouldDeliverToResource"/>.
        /// </summary>
        internal static ResourceFlowMode LookupResourceFlowMode(string resourceName)
        {
            if (string.IsNullOrEmpty(resourceName)) return ResourceFlowMode.ALL_VESSEL;
            try
            {
                PartResourceDefinition def =
                    PartResourceLibrary.Instance != null
                        ? PartResourceLibrary.Instance.GetDefinition(resourceName)
                        : null;
                return def != null ? def.resourceFlowMode : ResourceFlowMode.ALL_VESSEL;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"LookupResourceFlowMode({resourceName}) threw {ex.GetType().Name}: {ex.Message}; defaulting ALL_VESSEL");
                return ResourceFlowMode.ALL_VESSEL;
            }
        }

        /// <summary>
        /// Production funds-debit delegate. Skipped silently in Sandbox/Science
        /// because <see cref="ApplyDeliveryFromPlan"/> already gates the call
        /// on <c>(IsCareer AND IsKscOrigin)</c>. Defensive-null-check on
        /// <c>Funding.Instance</c> survives early-load ticks where the funding
        /// singleton is not yet published.
        /// </summary>
        private static void LiveDebitFunds(double cost)
        {
            try
            {
                if (Funding.Instance != null && cost > 0.0)
                {
                    Funding.Instance.AddFunds(-cost, TransactionReasons.None);
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"LiveDebitFunds({cost.ToString("R", IC)}) threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ==================================================================
        // Delivery seam types (item 6 Phase B)
        // ==================================================================

        /// <summary>
        /// Context bag for <see cref="ApplyDeliveryFromPlan"/>. Bundles cycle
        /// identity, Career/KSC gates, and the apply-writer delegates so the
        /// test side can inject fakes that record calls without touching
        /// <c>Vessel</c> / <c>Funding</c> / <c>Ledger.AddAction</c>.
        /// </summary>
        internal struct ApplyDeliveryContext
        {
            public string CycleId;
            public double CurrentUT;
            public int StopIndex;
            public bool IsCareer;
            public bool IsKscOrigin;
            public double KscFundsCost;
            public Action<string, double> ResourceWriter;
            public Func<string, double> ResourceActualReader;
            public Action<InventoryPayloadItem, int> InventoryWriter;
            public Func<int> InventoryActualCountReader;
            public Action<double> FundsDebiter;
            public Action<GameAction> LedgerEmitter;
        }

        /// <summary>
        /// Production apply-writer bundle used by <see cref="ApplyDelivery"/>.
        /// Owns the per-resource actual-transferred totals and the inventory
        /// success counter so the context delegates stay zero-allocation
        /// closures around this single object. Created fresh per delivery.
        /// </summary>
        private sealed class LiveDeliveryWriters
        {
            private readonly Route route;
            private readonly Vessel vessel;
            private readonly DeliveryPlan plan;
            private readonly Dictionary<string, double> actualPerResource;
            private int inventorySuccessCount;

            internal LiveDeliveryWriters(Route route, Vessel vessel, DeliveryPlan plan)
            {
                this.route = route;
                this.vessel = vessel;
                this.plan = plan;
                this.actualPerResource = new Dictionary<string, double>(
                    plan.Resources?.Count ?? 0, StringComparer.Ordinal);
                this.inventorySuccessCount = 0;
            }

            internal void WriteResource(string resourceName, double amount)
            {
                if (string.IsNullOrEmpty(resourceName) || amount <= 0.0)
                    return;

                double actual = 0.0;
                try
                {
                    if (vessel.loaded && !vessel.packed)
                    {
                        actual = WriteResourceLoaded(resourceName, amount);
                    }
                    else
                    {
                        actual = WriteResourceUnloaded(resourceName, amount);
                    }
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag,
                        $"WriteResource({resourceName}, {amount.ToString("R", IC)}) threw {ex.GetType().Name}: {ex.Message}");
                    actual = 0.0;
                }

                if (actual > 0.0)
                {
                    if (actualPerResource.TryGetValue(resourceName, out double existing))
                        actualPerResource[resourceName] = existing + actual;
                    else
                        actualPerResource[resourceName] = actual;
                }
            }

            internal double ReadActualResource(string resourceName)
            {
                if (string.IsNullOrEmpty(resourceName)) return 0.0;
                return actualPerResource.TryGetValue(resourceName, out double v) ? v : 0.0;
            }

            internal void WriteInventory(InventoryPayloadItem item, int slot)
            {
                if (item == null || slot < 0) return;

                bool stored = false;
                try
                {
                    if (vessel.loaded && !vessel.packed)
                    {
                        stored = WriteInventoryLoaded(item, slot);
                    }
                    else
                    {
                        stored = WriteInventoryUnloaded(item, slot);
                    }
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag,
                        $"WriteInventory(part={item.PartName}, slot={slot.ToString(IC)}) " +
                        $"threw {ex.GetType().Name}: {ex.Message}");
                    stored = false;
                }

                if (stored) inventorySuccessCount++;
            }

            internal int ReadInventoryActualCount() => inventorySuccessCount;

            /// <summary>
            /// Distributes <paramref name="amount"/> across the destination
            /// vessel's parts that hold the named resource. Walks parts in
            /// vessel order and fills each up to its <c>maxAmount</c> until the
            /// requested amount is satisfied or capacity runs out.
            /// </summary>
            private double WriteResourceLoaded(string resourceName, double amount)
            {
                double remaining = amount;
                double total = 0.0;
                if (vessel.parts == null) return 0.0;

                for (int i = 0; i < vessel.parts.Count && remaining > 0.0; i++)
                {
                    Part p = vessel.parts[i];
                    if (p == null || p.Resources == null) continue;
                    PartResource pr = p.Resources.Get(resourceName);
                    if (pr == null) continue;
                    // Mirror the probe's flowState gate AND suppress NO_FLOW
                    // resources at the seam. ShouldDeliverToResource is the
                    // single policy point shared with the unloaded writer and
                    // both probe paths so capacity/actual stay symmetric.
                    ResourceFlowMode mode = pr.info != null ? pr.info.resourceFlowMode : ResourceFlowMode.ALL_VESSEL;
                    if (!ShouldDeliverToResource(pr.flowState, mode)) continue;
                    double free = pr.maxAmount - pr.amount;
                    if (free <= 0.0) continue;
                    double delta = free < remaining ? free : remaining;
                    pr.amount += delta;
                    remaining -= delta;
                    total += delta;
                }
                return total;
            }

            /// <summary>
            /// Unloaded-vessel resource fill. Same distribution as the loaded
            /// path but writes <c>ProtoPartResourceSnapshot.amount</c>; the
            /// next time the vessel loads, the live <c>PartResource</c> values
            /// initialize from the proto snapshots so the delivered amounts
            /// become visible.
            /// </summary>
            private double WriteResourceUnloaded(string resourceName, double amount)
            {
                double remaining = amount;
                double total = 0.0;
                ProtoVessel pv = vessel.protoVessel;
                if (pv == null || pv.protoPartSnapshots == null) return 0.0;

                // NO_FLOW gate is per-resource definition, not per-tank — look
                // it up once outside the part loop so we don't hammer the
                // library for every proto-part. flowState stays per-snapshot.
                ResourceFlowMode mode = LookupResourceFlowMode(resourceName);

                for (int i = 0; i < pv.protoPartSnapshots.Count && remaining > 0.0; i++)
                {
                    ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                    if (pps == null || pps.resources == null) continue;
                    for (int j = 0; j < pps.resources.Count && remaining > 0.0; j++)
                    {
                        ProtoPartResourceSnapshot prs = pps.resources[j];
                        if (prs == null) continue;
                        if (!string.Equals(prs.resourceName, resourceName, StringComparison.Ordinal)) continue;
                        // Mirror the probe + loaded-writer gate. Closed proto
                        // tanks and NO_FLOW resources never receive a write.
                        if (!ShouldDeliverToResource(prs.flowState, mode)) continue;
                        double free = prs.maxAmount - prs.amount;
                        if (free <= 0.0) continue;
                        double delta = free < remaining ? free : remaining;
                        prs.amount += delta;
                        remaining -= delta;
                        total += delta;
                    }
                }
                return total;
            }

            /// <summary>
            /// Loaded-path inventory store. Locates the first
            /// <see cref="ModuleInventoryPart"/> on the vessel, converts the
            /// STOREDPART ConfigNode payload to a <see cref="ProtoPartSnapshot"/>
            /// via the canonical KSP <c>(ConfigNode, ProtoVessel, Game)</c>
            /// constructor (see B0 finding below), and delegates to stock
            /// <c>StoreCargoPartAtSlot</c>.
            ///
            /// B0 finding: <see cref="ProtoPartSnapshot(ConfigNode, ProtoVessel, Game)"/>
            /// is the canonical stock constructor — verified against decompiled
            /// <c>Assembly-CSharp.dll</c> (StoredPart.Load line 103,
            /// ModuleInventoryPart.OnLoad line 3073). The constructor expects a
            /// PART-shaped node (i.e. the inner PART subnode of a STOREDPART),
            /// not the STOREDPART wrapper itself. Our payload is a STOREDPART
            /// ConfigNode (see <see cref="VesselSpawner.BuildInventoryPayloadItem"/>),
            /// so we extract the inner PART node before constructing.
            /// </summary>
            private bool WriteInventoryLoaded(InventoryPayloadItem item, int slot)
            {
                if (item.StoredPartSnapshot == null) return false;
                ModuleInventoryPart module = FindFirstInventoryModule(vessel);
                if (module == null) return false;

                ProtoPartSnapshot pps = BuildProtoPartSnapshotForDelivery(
                    item.StoredPartSnapshot, vessel.protoVessel);
                if (pps == null) return false;

                return module.StoreCargoPartAtSlot(pps, slot);
            }

            /// <summary>
            /// Unloaded-path inventory store. Appends a deep-cloned STOREDPART
            /// ConfigNode under the first <see cref="ModuleInventoryPart"/>
            /// module's persistent <c>STOREDPARTS</c> child, matching the
            /// on-disk shape stock writes via <c>StoredPart.Save</c>. The slot
            /// index is persisted as the <c>slotIndex</c> value so stock's
            /// <c>OnLoad</c> (legacy and modern paths) restores the slot
            /// position when the vessel next loads.
            /// </summary>
            private bool WriteInventoryUnloaded(InventoryPayloadItem item, int slot)
            {
                if (item.StoredPartSnapshot == null) return false;
                ProtoVessel pv = vessel.protoVessel;
                if (pv == null || pv.protoPartSnapshots == null) return false;

                for (int i = 0; i < pv.protoPartSnapshots.Count; i++)
                {
                    ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                    if (pps == null || pps.modules == null) continue;
                    for (int m = 0; m < pps.modules.Count; m++)
                    {
                        ProtoPartModuleSnapshot mod = pps.modules[m];
                        if (mod == null || mod.moduleName != "ModuleInventoryPart") continue;
                        ConfigNode mv = mod.moduleValues;
                        if (mv == null) continue;
                        ConfigNode storedParts = mv.GetNode("STOREDPARTS");
                        if (storedParts == null) storedParts = mv.AddNode("STOREDPARTS");

                        ConfigNode storedPartCopy = item.StoredPartSnapshot.CreateCopy();
                        // Stock's StoredPart.Save writes slotIndex as a value
                        // child of the STOREDPART node. Our payload comes from
                        // VesselSpawner which preserves the original slotIndex;
                        // override it to the planner-assigned slot so the
                        // STOREDPART lands in the right place on next OnLoad.
                        storedPartCopy.name = "STOREDPART";
                        storedPartCopy.RemoveValues("slotIndex");
                        storedPartCopy.AddValue("slotIndex", slot.ToString(IC));
                        storedParts.AddNode(storedPartCopy);
                        return true;
                    }
                }
                return false;
            }

            private static ModuleInventoryPart FindFirstInventoryModule(Vessel v)
            {
                if (v == null || v.parts == null) return null;
                for (int i = 0; i < v.parts.Count; i++)
                {
                    Part p = v.parts[i];
                    if (p == null || p.Modules == null) continue;
                    for (int j = 0; j < p.Modules.Count; j++)
                    {
                        if (p.Modules[j] is ModuleInventoryPart m) return m;
                    }
                }
                return null;
            }

            /// <summary>
            /// Convert a STOREDPART ConfigNode (the Parsek-canonical payload
            /// shape) to a <see cref="ProtoPartSnapshot"/> via the stock
            /// <c>(ConfigNode, ProtoVessel, Game)</c> constructor. Stock
            /// <c>StoredPart.Load</c> reads the inner PART child node and feeds
            /// it to the same constructor with <c>(node.GetNode("PART"), null, null)</c>;
            /// we replicate that exactly here. Returns <c>null</c> when the
            /// STOREDPART payload has no inner PART node (defensive — every
            /// VesselSpawner-built payload includes one).
            /// </summary>
            internal static ProtoPartSnapshot BuildProtoPartSnapshotForDelivery(
                ConfigNode storedPartNode, ProtoVessel hostProtoVessel)
            {
                if (storedPartNode == null) return null;
                ConfigNode partNode = storedPartNode.GetNode("PART");
                if (partNode == null) return null;
                // Stock passes Game=null at StoredPart.Load:103 and at
                // ModuleInventoryPart.OnLoad:3073. We mirror that.
                return new ProtoPartSnapshot(partNode, hostProtoVessel, null);
            }
        }

        /// <summary>
        /// Live <see cref="IDeliveryCapacityProbe"/> over the destination vessel.
        /// Picks the loaded or unloaded probe automatically based on
        /// <c>vessel.loaded</c> / <c>vessel.packed</c>. Tracks
        /// <c>consumedSlots</c> across calls so the planner's per-item
        /// inventory walk can ask for "next empty slot" without re-querying
        /// the same module repeatedly.
        /// </summary>
        private sealed class LiveDeliveryCapacityProbe : IDeliveryCapacityProbe
        {
            private readonly Vessel vessel;
            private readonly HashSet<int> consumedSlots = new HashSet<int>();
            private readonly bool isLoaded;

            internal LiveDeliveryCapacityProbe(Vessel vessel)
            {
                this.vessel = vessel;
                this.isLoaded = vessel != null && vessel.loaded && !vessel.packed;
            }

            public double ProbeResourceFreeCapacity(string resourceName)
            {
                if (string.IsNullOrEmpty(resourceName) || vessel == null) return 0.0;
                try
                {
                    return isLoaded
                        ? ProbeLoadedResourceFree(resourceName)
                        : ProbeUnloadedResourceFree(resourceName);
                }
                catch (Exception ex)
                {
                    ParsekLog.Verbose(Tag,
                        $"ProbeResourceFreeCapacity({resourceName}) threw {ex.GetType().Name}: {ex.Message}; returning 0");
                    return 0.0;
                }
            }

            public int ProbeFirstEmptyInventorySlot()
            {
                if (vessel == null) return -1;
                try
                {
                    return isLoaded ? ProbeLoadedFirstEmpty() : ProbeUnloadedFirstEmpty();
                }
                catch (Exception ex)
                {
                    ParsekLog.Verbose(Tag,
                        $"ProbeFirstEmptyInventorySlot threw {ex.GetType().Name}: {ex.Message}; returning -1");
                    return -1;
                }
            }

            public void ConsumeInventorySlot(int slotIndex)
            {
                if (slotIndex < 0) return;
                consumedSlots.Add(slotIndex);
            }

            private double ProbeLoadedResourceFree(string resourceName)
            {
                if (vessel.parts == null) return 0.0;
                double total = 0.0;
                for (int i = 0; i < vessel.parts.Count; i++)
                {
                    Part p = vessel.parts[i];
                    if (p == null || p.Resources == null) continue;
                    PartResource pr = p.Resources.Get(resourceName);
                    if (pr == null) continue;
                    // Capacity must match what the writer will actually fill —
                    // closed tanks and NO_FLOW resources are non-deliverable.
                    ResourceFlowMode mode = pr.info != null ? pr.info.resourceFlowMode : ResourceFlowMode.ALL_VESSEL;
                    if (!ShouldDeliverToResource(pr.flowState, mode)) continue;
                    double free = pr.maxAmount - pr.amount;
                    if (free > 0.0) total += free;
                }
                return total;
            }

            private double ProbeUnloadedResourceFree(string resourceName)
            {
                ProtoVessel pv = vessel.protoVessel;
                if (pv == null || pv.protoPartSnapshots == null) return 0.0;
                double total = 0.0;

                // NO_FLOW is a per-resource definition — look it up once and
                // either return 0 immediately or reuse the mode in the loop.
                ResourceFlowMode mode = LookupResourceFlowMode(resourceName);

                for (int i = 0; i < pv.protoPartSnapshots.Count; i++)
                {
                    ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                    if (pps == null || pps.resources == null) continue;
                    for (int j = 0; j < pps.resources.Count; j++)
                    {
                        ProtoPartResourceSnapshot prs = pps.resources[j];
                        if (prs == null) continue;
                        if (!string.Equals(prs.resourceName, resourceName, StringComparison.Ordinal)) continue;
                        // Mirror the writer-side gate so probe capacity and
                        // actual transferable stay symmetric.
                        if (!ShouldDeliverToResource(prs.flowState, mode)) continue;
                        double free = prs.maxAmount - prs.amount;
                        if (free > 0.0) total += free;
                    }
                }
                return total;
            }

            private int ProbeLoadedFirstEmpty()
            {
                if (vessel.parts == null) return -1;
                for (int i = 0; i < vessel.parts.Count; i++)
                {
                    Part p = vessel.parts[i];
                    if (p == null || p.Modules == null) continue;
                    for (int m = 0; m < p.Modules.Count; m++)
                    {
                        if (!(p.Modules[m] is ModuleInventoryPart module)) continue;
                        // Walk slot indices [0, InventorySlots) in order so the
                        // result is deterministic and matches stock's
                        // FirstEmptySlot() contract; skip anything the planner
                        // has already claimed this pass.
                        for (int s = 0; s < module.InventorySlots; s++)
                        {
                            if (consumedSlots.Contains(s)) continue;
                            if (module.storedParts != null && module.storedParts.ContainsKey(s)) continue;
                            return s;
                        }
                        return -1;
                    }
                }
                return -1;
            }

            private int ProbeUnloadedFirstEmpty()
            {
                ProtoVessel pv = vessel.protoVessel;
                if (pv == null || pv.protoPartSnapshots == null) return -1;
                for (int i = 0; i < pv.protoPartSnapshots.Count; i++)
                {
                    ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                    if (pps == null || pps.modules == null) continue;
                    for (int m = 0; m < pps.modules.Count; m++)
                    {
                        ProtoPartModuleSnapshot mod = pps.modules[m];
                        if (mod == null || mod.moduleName != "ModuleInventoryPart") continue;
                        ConfigNode mv = mod.moduleValues;
                        if (mv == null) continue;

                        // InventorySlots default is 9 (ModuleInventoryPart.InventorySlots).
                        // The proto module's moduleValues may not carry the
                        // value when the part hasn't been individually
                        // configured (KSPField, not isPersistant by default),
                        // so fall back to the stock default if missing.
                        int slotCount = 9;
                        string slotsStr = mv.GetValue("InventorySlots");
                        if (!string.IsNullOrEmpty(slotsStr))
                            int.TryParse(slotsStr, System.Globalization.NumberStyles.Integer, IC, out slotCount);

                        // Build occupied set from existing STOREDPART children.
                        HashSet<int> occupied = new HashSet<int>();
                        ConfigNode storedParts = mv.GetNode("STOREDPARTS");
                        if (storedParts != null)
                        {
                            ConfigNode[] sps = storedParts.GetNodes("STOREDPART");
                            for (int s = 0; s < sps.Length; s++)
                            {
                                string idxStr = sps[s].GetValue("slotIndex");
                                if (!string.IsNullOrEmpty(idxStr)
                                    && int.TryParse(idxStr, System.Globalization.NumberStyles.Integer, IC, out int idx))
                                {
                                    occupied.Add(idx);
                                }
                            }
                        }

                        for (int s = 0; s < slotCount; s++)
                        {
                            if (consumedSlots.Contains(s)) continue;
                            if (occupied.Contains(s)) continue;
                            return s;
                        }
                        return -1;
                    }
                }
                return -1;
            }
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        /// <summary>
        /// Locates the route's source <see cref="Recording"/> in ERS, then
        /// computes the dispatch funds cost from its <c>VesselSnapshot</c> via
        /// <see cref="RouteFundsCalculator.ComputeDispatchFundsCost"/>. Returns
        /// 0 when ERS cannot be queried (e.g. during early load) or the
        /// recording / snapshot is missing.
        /// </summary>
        private static double ComputeDispatchFundsCostForRoute(Route route)
        {
            if (route == null || route.SourceRefs == null || route.SourceRefs.Count == 0)
                return 0.0;

            // v0 routes have a single source recording — chain-source routes
            // will pick the first/transport recording once multi-source funds
            // computation is specified.
            string sourceId = route.SourceRefs[0]?.RecordingId;
            if (string.IsNullOrEmpty(sourceId))
                return 0.0;

            var ers = SafeComputeErs();
            if (ers == null) return 0.0;

            Recording source = null;
            for (int i = 0; i < ers.Count; i++)
            {
                var rec = ers[i];
                if (rec != null && string.Equals(rec.RecordingId, sourceId, StringComparison.Ordinal))
                {
                    source = rec;
                    break;
                }
            }
            if (source == null || source.VesselSnapshot == null)
            {
                ParsekLog.Verbose(Tag,
                    $"ComputeDispatchFundsCostForRoute: route {ShortIdForLog(route)} source " +
                    $"recording {sourceId} not in ERS or has no VesselSnapshot; cost=0");
                return 0.0;
            }

            return RouteFundsCalculator.ComputeDispatchFundsCost(
                source.VesselSnapshot,
                LiveRouteRuntimeEnvironment.LookupPartCost,
                LiveRouteRuntimeEnvironment.LookupResourceUnitCost);
        }

        /// <summary>
        /// Defensive <see cref="EffectiveState.ComputeERS"/> wrapper used by
        /// <see cref="ComputeDispatchFundsCostForRoute"/>. ComputeERS can throw
        /// during early KSP load if the scenario module is not yet published;
        /// the orchestrator survives such ticks by treating the failure as
        /// "no recordings available" and emitting a Verbose breadcrumb.
        /// </summary>
        private static IReadOnlyList<Recording> SafeComputeErs()
        {
            try
            {
                return EffectiveState.ComputeERS();
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"SafeComputeErs: ComputeERS threw {ex.GetType().Name}: {ex.Message}; treating as empty");
                return null;
            }
        }

        private static Dictionary<string, double> CloneManifest(Dictionary<string, double> source)
        {
            if (source == null) return null;
            return new Dictionary<string, double>(source);
        }

        private static string ShortIdForLog(Route route)
        {
            if (route == null || string.IsNullOrEmpty(route.Id)) return "<no-id>";
            return route.Id.Length > 8 ? route.Id.Substring(0, 8) : route.Id;
        }

        // ==================================================================
        // Production IRouteRuntimeEnvironment implementation
        // ==================================================================

        /// <summary>
        /// Production <see cref="IRouteRuntimeEnvironment"/> backed by live KSP
        /// statics (<see cref="HighLogic"/>, <see cref="Funding"/>,
        /// <see cref="PartLoader"/>, <see cref="EffectiveState.ComputeERS"/>).
        /// One instance per <see cref="Tick(double)"/> call so the per-tick ERS
        /// snapshot stays scoped and cannot leak between ticks.
        /// </summary>
        /// <remarks>
        /// v0 limitations (item 6 wires these):
        /// <list type="bullet">
        ///   <item><c>OriginHasCargo</c>: non-KSC origins always return
        ///     <c>false</c> with reason <c>"non-ksc-origin-unsupported-in-v0"</c>.
        ///     KSC origins return <c>true</c> unconditionally (funds are
        ///     gated separately via <see cref="KscFundsAvailable"/>).</item>
        ///   <item><c>DestinationHasCapacity</c>: always returns <c>true</c>.
        ///     Live capacity reads against the resolved destination vessel
        ///     wait until item 6.</item>
        /// </list>
        /// </remarks>
        private sealed class LiveRouteRuntimeEnvironment : IRouteRuntimeEnvironment
        {
            // Per-tick ERS cache. Built lazily on first call and shared across
            // every method on this instance — multiple env queries during the
            // same tick (one per route) all see the same ERS snapshot.
            private Dictionary<string, Recording> ersById;
            private bool ersBuilt;

            public bool IsCareer
            {
                get
                {
                    try
                    {
                        return HighLogic.CurrentGame != null
                            && HighLogic.CurrentGame.Mode == Game.Modes.CAREER;
                    }
                    catch (Exception ex)
                    {
                        ParsekLog.Verbose(Tag,
                            $"IsCareer probe threw {ex.GetType().Name}: {ex.Message}; defaulting false");
                        return false;
                    }
                }
            }

            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason)
            {
                // The dispatch-time check does not need the live Vessel reference
                // (cargo / capacity checks are stubbed in v0 and the delivery
                // side resolves the vessel separately via
                // <see cref="TryResolveEndpointVessel"/>). Discard the out-vessel
                // here so the env interface stays narrowly scoped for dispatch.
                return RouteEndpointResolver.TryResolveEndpoint(endpoint, out _, out reason);
            }

            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason)
            {
                // Same resolver as the bool-only overload above, but exposes the
                // <see cref="Vessel"/> so the delivery applier can decide between
                // the loaded (live <c>parts</c>) and unloaded
                // (<c>protoVessel.protoPartSnapshots</c>) write paths.
                return RouteEndpointResolver.TryResolveEndpoint(endpoint, out vessel, out reason);
            }

            public bool OriginHasCargo(Route route, out string lackingResource)
            {
                lackingResource = string.Empty;
                if (route == null)
                {
                    lackingResource = "null-route";
                    return false;
                }

                if (route.IsKscOrigin)
                {
                    // KSC origin: per design §6.1, KSC has unlimited cargo.
                    // The dispatch cost is charged via funds (KscFundsAvailable),
                    // not by checking a resource bin.
                    return true;
                }

                // v0: non-KSC origins are deferred to item 6. We CANNOT silently
                // return true — that would let routes dispatch without verifying
                // the live origin vessel has the manifest. Returning false with
                // a stable reason puts these routes in WaitingForResources until
                // the live origin-cargo check lands.
                lackingResource = "non-ksc-origin-unsupported-in-v0";
                ParsekLog.VerboseRateLimited(Tag, "non-ksc-origin-stub",
                    $"OriginHasCargo: non-KSC origin path is stubbed in v0; " +
                    $"route {ShortIdForRoute(route)} held in WaitingForResources until item 6");
                return false;
            }

            public bool KscFundsAvailable(Route route, out double shortfall)
            {
                shortfall = 0.0;
                if (route == null) return false;

                double cost = ComputeDispatchFundsCostForRoute(route);
                double funds = 0.0;
                try
                {
                    funds = Funding.Instance != null ? Funding.Instance.Funds : 0.0;
                }
                catch (Exception ex)
                {
                    ParsekLog.Verbose(Tag,
                        $"KscFundsAvailable: Funding.Instance probe threw {ex.GetType().Name}: {ex.Message}; treating funds=0");
                    funds = 0.0;
                }

                if (funds >= cost)
                    return true;

                shortfall = cost - funds;
                return false;
            }

            public bool DestinationHasCapacity(Route route, out string fullResource)
            {
                // v0: capacity check not yet implemented; item 6 wires this
                // by reading each stop endpoint's live vessel resource
                // capacity vs the cost manifest. Defaulting to true means
                // every dispatch attempts as if the destination is empty —
                // matches the design doc's "v0 minimal viable scheduler"
                // posture.
                //
                // Without a log, a destination-full scenario in v0 silently
                // dispatches and the only evidence in KSP.log is the
                // successful dispatch line. Emit a per-route rate-limited
                // breadcrumb so operators see the v0 limitation in context —
                // same shape as OriginHasCargo's non-KSC stub above.
                fullResource = string.Empty;
                string routeId = route?.Id ?? "<none>";
                ParsekLog.VerboseRateLimited(Tag,
                    "route-destcap-" + routeId,
                    $"DestinationHasCapacity: v0 stub returning true " +
                    $"(item-6 wires real check); routeId={routeId}");
                return true;
            }

            public bool RouteHasValidSourcesInErs(Route route)
            {
                if (route == null || route.SourceRefs == null || route.SourceRefs.Count == 0)
                    return false;

                EnsureErsBuilt();
                if (ersById == null) return false;

                for (int i = 0; i < route.SourceRefs.Count; i++)
                {
                    var sref = route.SourceRefs[i];
                    if (sref == null || string.IsNullOrEmpty(sref.RecordingId))
                        return false;
                    if (!ersById.ContainsKey(sref.RecordingId))
                        return false;
                }
                return true;
            }

            private void EnsureErsBuilt()
            {
                if (ersBuilt) return;
                ersBuilt = true;
                var ers = SafeComputeErs();
                if (ers == null) return;
                ersById = new Dictionary<string, Recording>(StringComparer.Ordinal);
                for (int i = 0; i < ers.Count; i++)
                {
                    var rec = ers[i];
                    if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;
                    ersById[rec.RecordingId] = rec;
                }
            }

            // ---- Stock cost lookups used by ComputeDispatchFundsCostForRoute ----

            internal static float LookupPartCost(string partName)
            {
                if (string.IsNullOrEmpty(partName)) return 0f;
                try
                {
                    AvailablePart ap = PartLoader.getPartInfoByName(partName);
                    return ap != null ? ap.cost : 0f;
                }
                catch (Exception ex)
                {
                    ParsekLog.Verbose(Tag,
                        $"LookupPartCost({partName}) threw {ex.GetType().Name}: {ex.Message}; returning 0");
                    return 0f;
                }
            }

            internal static float LookupResourceUnitCost(string resourceName)
            {
                if (string.IsNullOrEmpty(resourceName)) return 0f;
                try
                {
                    PartResourceDefinition def =
                        PartResourceLibrary.Instance != null
                            ? PartResourceLibrary.Instance.GetDefinition(resourceName)
                            : null;
                    return def != null ? def.unitCost : 0f;
                }
                catch (Exception ex)
                {
                    ParsekLog.Verbose(Tag,
                        $"LookupResourceUnitCost({resourceName}) threw {ex.GetType().Name}: {ex.Message}; returning 0");
                    return 0f;
                }
            }

            private static string ShortIdForRoute(Route route)
            {
                if (route == null || string.IsNullOrEmpty(route.Id)) return "<no-id>";
                return route.Id.Length > 8 ? route.Id.Substring(0, 8) : route.Id;
            }
        }
    }
}
