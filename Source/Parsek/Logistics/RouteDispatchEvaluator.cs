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

            // 4. ERS validation (defensive — RouteStore.RevalidateSources owns the canonical check).
            if (!env.RouteHasValidSourcesInErs(route))
                return RouteDispatchDecision.Skip("sources-stale");

            // 5. Endpoint resolution — every stop, plus the origin when it is a vessel (non-KSC).
            if (route.Stops != null)
            {
                for (int i = 0; i < route.Stops.Count; i++)
                {
                    RouteStop stop = route.Stops[i];
                    if (stop == null) continue;

                    if (!env.TryResolveEndpoint(stop.Endpoint, out string epReason))
                    {
                        return RouteDispatchDecision.EndpointLost(
                            currentUT + RouteOrchestrator.WaitRetryIntervalSec,
                            $"stop-{i}-{epReason}");
                    }
                }
            }
            if (!route.IsKscOrigin
                && !env.TryResolveEndpoint(route.Origin, out string originReason))
            {
                return RouteDispatchDecision.EndpointLost(
                    currentUT + RouteOrchestrator.WaitRetryIntervalSec,
                    $"origin-{originReason}");
            }

            // 6. Origin has cargo.
            if (!env.OriginHasCargo(route, out string lackingResource))
            {
                return RouteDispatchDecision.WaitResources(
                    currentUT + RouteOrchestrator.WaitRetryIntervalSec,
                    lackingResource);
            }

            // 7. Career funds (only when Career mode AND KSC origin).
            if (env.IsCareer && route.IsKscOrigin)
            {
                if (!env.KscFundsAvailable(route, out double shortfall))
                {
                    return RouteDispatchDecision.WaitFunds(
                        currentUT + RouteOrchestrator.WaitRetryIntervalSec,
                        shortfall);
                }
            }

            // 8. Destination capacity.
            if (!env.DestinationHasCapacity(route, out string fullResource))
            {
                return RouteDispatchDecision.WaitDestinationFull(
                    currentUT + RouteOrchestrator.WaitRetryIntervalSec,
                    fullResource);
            }

            // All conditions met.
            return RouteDispatchDecision.Dispatch();
        }
    }
}
