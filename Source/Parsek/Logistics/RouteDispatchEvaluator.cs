namespace Parsek.Logistics
{
    /// <summary>
    /// Pure decision engine for the dispatch scheduler. Given a route, the
    /// current UT, and an <see cref="IRouteRuntimeEnvironment"/>, returns a
    /// <see cref="RouteDispatchDecision"/> describing what the orchestrator
    /// should do. No KSP statics, no logging, no mutation — the orchestrator
    /// owns side effects.
    /// </summary>
    internal static class RouteDispatchEvaluator
    {
        /// <summary>
        /// Per-cycle eligibility-failure kinds shared by the legacy self-timer
        /// path (<see cref="EvaluateRoute"/>) and the loop-clock path
        /// (<see cref="RouteOrchestrator"/>'s <c>ProcessLoopRoute</c>, plan
        /// Phase 4). The loop path only cares whether the cycle is eligible at
        /// all; the legacy path maps each kind onto its specific
        /// <see cref="RouteDispatchDecision"/>.
        /// </summary>
        internal enum EligibilityFailureKind
        {
            /// <summary>All per-cycle conditions met.</summary>
            None = 0,
            /// <summary>Defensive ERS cross-check failed (sources stale).</summary>
            SourcesStale,
            /// <summary>A stop's destination (or the non-KSC origin) endpoint did not resolve.</summary>
            EndpointLost,
            /// <summary>Origin lacks the manifest's required resources.</summary>
            OriginLacksCargo,
            /// <summary>Career + KSC origin without funds for the dispatch cost.</summary>
            FundsShort,
            /// <summary>Destination has no capacity for the cost manifest.</summary>
            DestinationFull,
        }

        /// <summary>
        /// Pure per-cycle eligibility result. <see cref="Eligible"/> is the
        /// loop-path's only interest; the legacy path additionally reads
        /// <see cref="Kind"/> / <see cref="Reason"/> / <see cref="Shortfall"/>
        /// to build its specific <see cref="RouteDispatchDecision"/>.
        /// </summary>
        internal readonly struct EligibilityResult
        {
            internal EligibilityResult(EligibilityFailureKind kind, string reason, double shortfall)
            {
                Kind = kind;
                Reason = reason;
                Shortfall = shortfall;
            }

            internal bool Eligible => Kind == EligibilityFailureKind.None;
            internal EligibilityFailureKind Kind { get; }
            internal string Reason { get; }
            internal double Shortfall { get; }

            internal static EligibilityResult Ok() =>
                new EligibilityResult(EligibilityFailureKind.None, null, 0.0);
        }

        /// <summary>
        /// Evaluate a single route at <paramref name="currentUT"/>. See
        /// <see cref="RouteDispatchOutcome"/> for the discrete result set.
        /// </summary>
        internal static RouteDispatchDecision EvaluateRoute(
            Route route,
            double currentUT,
            IRouteRuntimeEnvironment env)
        {
            if (route == null)
                return RouteDispatchDecision.Skip("null-route");
            if (env == null)
                return RouteDispatchDecision.Skip("null-env");

            // 1. Status filter — silent skip for permanent blockers, or in-transit
            //    arrival check. Active and the wait states fall through to the
            //    full dispatch decision chain.
            switch (route.Status)
            {
                case RouteStatus.Paused:
                case RouteStatus.MissingSourceRecording:
                case RouteStatus.SourceChanged:
                    return RouteDispatchDecision.Skip("status-permanent-block");

                case RouteStatus.InTransit:
                    // Arrival: cycle has been running, transit duration has elapsed,
                    // and we have not yet emitted the pending-delivery transition.
                    if (route.CurrentCycleStartUT.HasValue
                        && currentUT >= route.CurrentCycleStartUT.Value + route.TransitDuration
                        && !route.PendingDeliveryUT.HasValue)
                    {
                        return RouteDispatchDecision.InTransitComplete();
                    }
                    return RouteDispatchDecision.Skip("in-transit-pending");

                // Active, WaitingForResources, WaitingForFunds, DestinationFull,
                // EndpointLost: fall through to the dispatch decision chain.
            }

            // 2. Rate-limit on retry.
            if (route.NextEligibilityCheckUT.HasValue && currentUT < route.NextEligibilityCheckUT.Value)
                return RouteDispatchDecision.Skip("rate-limited");

            // 3. NextDispatchUT due.
            if (currentUT < route.NextDispatchUT)
                return RouteDispatchDecision.Skip("not-due-yet");

            // 4-8. Per-cycle eligibility (ERS, endpoint, cargo, funds, capacity).
            //      Extracted into CheckEligibility (plan Phase 4 task 3) so the
            //      loop-clock path can reuse the identical gate WITHOUT this
            //      method's Dispatch->InTransit + PendingDeliveryUT self-timer
            //      machinery. Map each failure kind onto its specific decision so
            //      the legacy path's behavior is byte-identical to the inline
            //      checks it replaces.
            EligibilityResult elig = CheckEligibility(route, currentUT, env);
            switch (elig.Kind)
            {
                case EligibilityFailureKind.None:
                    return RouteDispatchDecision.Dispatch();

                case EligibilityFailureKind.SourcesStale:
                    return RouteDispatchDecision.Skip(elig.Reason);

                case EligibilityFailureKind.EndpointLost:
                    return RouteDispatchDecision.EndpointLost(
                        currentUT + RouteOrchestrator.WaitRetryIntervalSec,
                        elig.Reason);

                case EligibilityFailureKind.OriginLacksCargo:
                    return RouteDispatchDecision.WaitResources(
                        currentUT + RouteOrchestrator.WaitRetryIntervalSec,
                        elig.Reason);

                case EligibilityFailureKind.FundsShort:
                    return RouteDispatchDecision.WaitFunds(
                        currentUT + RouteOrchestrator.WaitRetryIntervalSec,
                        elig.Shortfall);

                case EligibilityFailureKind.DestinationFull:
                    return RouteDispatchDecision.WaitDestinationFull(
                        currentUT + RouteOrchestrator.WaitRetryIntervalSec,
                        elig.Reason);

                default:
                    return RouteDispatchDecision.Skip("eligibility-unknown");
            }
        }

        /// <summary>
        /// Pure per-cycle eligibility gate (plan Phase 4 task 3, must-fix #3).
        /// Runs ONLY the per-cycle condition checks (ERS sources, endpoint
        /// resolution for every stop + non-KSC origin, origin cargo, Career/KSC
        /// funds, destination capacity) and returns the first failure (or
        /// <see cref="EligibilityResult.Ok"/>). It does NOT touch route status,
        /// scheduling, or the <c>PendingDeliveryUT</c> self-timer — that
        /// machinery is owned by <see cref="EvaluateRoute"/> for the legacy path
        /// and is intentionally bypassed by the loop-clock path, which calls this
        /// helper directly before emitting a full cycle. The check ORDER mirrors
        /// the inline <see cref="EvaluateRoute"/> steps 4-8 so both paths surface
        /// the same first-failure reason. Pure: no logging, no mutation.
        /// </summary>
        internal static EligibilityResult CheckEligibility(
            Route route,
            double currentUT,
            IRouteRuntimeEnvironment env)
        {
            if (route == null)
                return new EligibilityResult(EligibilityFailureKind.SourcesStale, "null-route", 0.0);
            if (env == null)
                return new EligibilityResult(EligibilityFailureKind.SourcesStale, "null-env", 0.0);

            // 4. ERS validation (defensive — RouteStore.RevalidateSources owns the canonical check).
            if (!env.RouteHasValidSourcesInErs(route))
                return new EligibilityResult(EligibilityFailureKind.SourcesStale, "sources-stale", 0.0);

            // 5. Endpoint resolution — every stop, plus the origin when it is a vessel (non-KSC).
            if (route.Stops != null)
            {
                for (int i = 0; i < route.Stops.Count; i++)
                {
                    RouteStop stop = route.Stops[i];
                    if (stop == null) continue;

                    if (!env.TryResolveEndpoint(stop.Endpoint, out string epReason))
                    {
                        return new EligibilityResult(
                            EligibilityFailureKind.EndpointLost, $"stop-{i}-{epReason}", 0.0);
                    }
                }
            }
            // Harvest origins (M2, plan D7) have NO origin vessel: the Origin
            // endpoint is a display-only harvest-site descriptor (pid 0), so
            // there is nothing to resolve - skipping keeps the route from
            // holding EndpointLost forever. The origin-cargo gate below still
            // runs; the env answers true for harvest origins (nothing to gate).
            if (!route.IsKscOrigin && !route.IsHarvestOrigin
                && !env.TryResolveEndpoint(route.Origin, out string originReason))
            {
                return new EligibilityResult(
                    EligibilityFailureKind.EndpointLost, $"origin-{originReason}", 0.0);
            }

            // 6. Origin has cargo.
            if (!env.OriginHasCargo(route, out string lackingResource))
            {
                return new EligibilityResult(
                    EligibilityFailureKind.OriginLacksCargo, lackingResource, 0.0);
            }

            // 7. Career funds (only when Career mode AND KSC origin).
            if (env.IsCareer && route.IsKscOrigin)
            {
                if (!env.KscFundsAvailable(route, out double shortfall))
                {
                    return new EligibilityResult(
                        EligibilityFailureKind.FundsShort, "funds-short", shortfall);
                }
            }

            // 8. Destination capacity.
            if (!env.DestinationHasCapacity(route, out string fullResource))
            {
                return new EligibilityResult(
                    EligibilityFailureKind.DestinationFull, fullResource, 0.0);
            }

            // All conditions met.
            return EligibilityResult.Ok();
        }
    }
}
