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
                route.KscDispatchFundsCost = (float)computedCost;
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
        private static void ApplyInTransitComplete(Route route, double currentUT)
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
                $"awaiting item-6 delivery wiring " +
                $"(PendingDeliveryUT={currentUT.ToString("R", IC)} stopIndex=0)");
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
                // The orchestrator does not need the live Vessel object at
                // dispatch time (cargo / capacity checks are stubbed in v0
                // and item 6 will resolve the vessel separately on the
                // delivery side). Discard the out-vessel here so the env
                // interface stays narrowly scoped.
                return RouteEndpointResolver.TryResolveEndpoint(endpoint, out _, out reason);
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
                fullResource = string.Empty;
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
