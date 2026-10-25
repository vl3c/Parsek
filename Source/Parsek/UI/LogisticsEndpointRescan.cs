using System.Collections.Generic;
using Parsek.Logistics;

namespace Parsek
{
    /// <summary>
    /// The click body of the Logistics window's "Re-scan for endpoint" button, in one place
    /// so the button (<c>LogisticsWindowUI.DrawEndpointRescan</c> and its multi-stop form)
    /// and the automation seam's <c>RouteCommand action=rescan</c> run the same code and
    /// print the same log line. A lane that presses the seam therefore proves what the
    /// player's click does, not a copy of it.
    ///
    /// <para>The button is drawn enabled only when <see cref="IsOffered"/> holds; the seam
    /// refuses otherwise. A single-stop route re-scans its one stop; a multi-stop route
    /// (GUI-P20) re-scans EVERY stop, because the dispatch gate needs all of them, and
    /// recovers only when every stop resolved. A recovery is
    /// <see cref="RouteOrchestrator.TryRecoverEndpointLostAfterRescan"/>, which restarts a
    /// loop route (its tick skips a stopped route) and clears a non-loop route's retry
    /// gate. A miss logs and leaves the route EndpointLost.</para>
    ///
    /// <para>The resolver is a parameter so the headless suite can drive the decision
    /// without <c>FlightGlobals</c>; production passes the live
    /// <see cref="RouteEndpointResolver.TryResolveEndpoint"/>, kept in its own method so
    /// mono never JITs a <c>FlightGlobals</c> read into a caller the tests run.</para>
    /// </summary>
    internal static class LogisticsEndpointRescan
    {
        /// <summary>Resolves one stop's endpoint: true with the vessel's name, or false
        /// with the resolver's reason token.</summary>
        internal delegate bool StopResolver(RouteEndpoint endpoint, out string vesselName, out string reason);

        /// <summary>What one press did.</summary>
        internal struct Outcome
        {
            /// <summary>Non-null stops the press re-scanned.</summary>
            internal int Stops;
            /// <summary>Of those, how many resolved.</summary>
            internal int Resolved;
            /// <summary>Every re-scanned stop resolved (and there was at least one).</summary>
            internal bool AllResolved;
            /// <summary>The recovery ran and accepted the route.</summary>
            internal bool Recovered;
        }

        /// <summary>
        /// True when the Logistics window draws the ENABLED "Re-scan for endpoint" button for
        /// this route: the single-stop gate on stop 0, or the multi-stop gate over every stop.
        /// Pure.
        /// </summary>
        internal static bool IsOffered(Route route)
        {
            if (route?.Stops == null || route.Stops.Count == 0)
                return false;
            if (route.Stops.Count > 1)
            {
                return LogisticsDeliveryPresentation.CountStops(route.Stops) > 0
                    && LogisticsDeliveryPresentation.ShouldOfferRouteEndpointRescan(route.Status, route.Stops);
            }
            RouteStop stop = route.Stops[0];
            return stop != null
                && LogisticsDeliveryPresentation.ShouldOfferEndpointRescan(route.Status, stop.Endpoint);
        }

        /// <summary>The press, against the live resolver.</summary>
        internal static Outcome Run(Route route, double currentUT)
        {
            return Run(route, currentUT, LiveResolve);
        }

        /// <summary>The press. The caller has already checked <see cref="IsOffered"/>.</summary>
        internal static Outcome Run(Route route, double currentUT, StopResolver resolve)
        {
            var outcome = new Outcome();
            if (route?.Stops == null || route.Stops.Count == 0 || resolve == null)
                return outcome;

            string shortId = LogisticsWindowUI.ShortId(route.Id);
            if (route.Stops.Count > 1)
            {
                var results = new List<LogisticsDeliveryPresentation.RescanStopResult>(route.Stops.Count);
                for (int i = 0; i < route.Stops.Count; i++)
                {
                    RouteStop s = route.Stops[i];
                    if (s == null) continue;
                    bool ok = resolve(s.Endpoint, out string name, out string reason);
                    results.Add(new LogisticsDeliveryPresentation.RescanStopResult(
                        i, ok, ok ? name : null, reason));
                    outcome.Stops++;
                    if (ok) outcome.Resolved++;
                }
                outcome.AllResolved = LogisticsDeliveryPresentation.AllStopsResolved(results);
                ParsekLog.Info("UI",
                    $"Logistics: endpoint re-scan route={shortId} " +
                    LogisticsDeliveryPresentation.FormatRescanOutcome(results) +
                    (outcome.AllResolved ? " (recovering)" : " (still EndpointLost)"));
                if (outcome.AllResolved)
                    outcome.Recovered = RouteOrchestrator.TryRecoverEndpointLostAfterRescan(route, currentUT);
                return outcome;
            }

            RouteStop stop = route.Stops[0];
            if (stop == null)
                return outcome;
            outcome.Stops = 1;
            bool resolved = resolve(stop.Endpoint, out string vesselName, out string missReason);
            if (resolved)
            {
                outcome.Resolved = 1;
                outcome.AllResolved = true;
                ParsekLog.Info("UI",
                    $"Logistics: endpoint re-scan route={shortId} resolved=true name='{vesselName}' (recovering)");
                outcome.Recovered = RouteOrchestrator.TryRecoverEndpointLostAfterRescan(route, currentUT);
            }
            else
            {
                ParsekLog.Info("UI",
                    $"Logistics: endpoint re-scan route={shortId} resolved=false reason='{missReason}' (still EndpointLost)");
            }
            return outcome;
        }

        private static bool LiveResolve(RouteEndpoint endpoint, out string vesselName, out string reason)
        {
            bool ok = RouteEndpointResolver.TryResolveEndpoint(endpoint, out Vessel v, out reason);
            bool found = ok && v != null;
            vesselName = found ? v.vesselName : null;
            return found;
        }
    }
}
