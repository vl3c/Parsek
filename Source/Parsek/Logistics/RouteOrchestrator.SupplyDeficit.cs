using System;
using System.Collections.Generic;

namespace Parsek.Logistics
{
    // The cycle supply deficit: the delivery cap that keeps a route from creating cargo.
    // Pure: how much of each resource the transport is MISSING at a delivery window
    // of one cycle, because this cycle's origin debit or a pickup window took less
    // than the route recorded. A route's delivery manifests are the recorded run's,
    // so without this cap a short supply (a depot drained between the dispatch and
    // its pickup window, a source that could not be resolved) would still deliver
    // the full recorded amount and create cargo (ROUTE-DOCKED-ORIGIN-IGNORES-ESCROW).
    //
    // The walk follows the cargo on board, in window order (the
    // RouteEvent.Sequence stride, which is the stop's DockUT order):
    // a RouteEventType.RouteCargoDebited or
    // RouteEventType.RouteCargoPickedUp row adds its shortfall
    // (requested - actual for every resource its requested-on-shortfall
    // manifest names); an earlier RouteEventType.RouteCargoDelivered
    // row of the cycle subtracts what it left on board (recorded - actual,
    // the recorded amount being that stop's RouteStop.DeliveryManifest),
    // because a window that delivered less kept the difference on the transport.
    // The deficit never goes below zero: cargo a full destination refused is not
    // carried forward into a LATER delivery beyond what that delivery recorded.
    // Every input is the persisted route event log, so the cap is the same after a
    // save / reload mid-cycle. Resources only: no delivery row carries its
    // stored-part actuals, so an inventory deficit cannot be walked the same way.
    //
    // The deficit is how much less is on board than the recorded run had at that
    // window. A delivery holds back only the part of it the route's LATER recorded
    // deliveries cannot absorb (ComputeDeliveryHoldback): the cargo that is aboard
    // goes to the first stop that wants it, and the shortfall lands on the last
    // ones, as it would for a real ship. A later pickup counts against that
    // absorption, because the later delivery it feeds may come out of that pickup
    // rather than out of the cargo aboard now (and a short later pickup adds its own
    // deficit there), so the held-back amount never lets the cycle deliver more than
    // it took on. No onboard balance is kept: a harvest-origin route, or a docked
    // origin whose debit the harvest reduced, books no row for the harvested cargo,
    // so a balance would read it as missing; the deficit only counts rows that came
    // up short, and a fully met cycle holds nothing back.
    internal static partial class RouteOrchestrator
    {
        /// <summary>Below this a per-resource deficit is float noise, not a short supply.</summary>
        internal const double SupplyDeficitEpsilon = 1e-9;

        /// <summary>
        /// The per-resource supply deficit outstanding at the delivery window with
        /// row sequence <paramref name="deliverySequence"/> of cycle
        /// <paramref name="cycleId"/> on <paramref name="route"/>: only rows of that
        /// route and cycle with a lower Sequence count. Returns an empty map when
        /// nothing is missing (the common case). Pure.
        /// </summary>
        internal static Dictionary<string, double> ComputeCycleSupplyDeficit(
            Route route, string cycleId, int deliverySequence, IReadOnlyList<RouteEvent> events)
        {
            var deficit = new Dictionary<string, double>(StringComparer.Ordinal);
            if (route == null || string.IsNullOrEmpty(route.Id)
                || string.IsNullOrEmpty(cycleId) || events == null || events.Count == 0)
                return deficit;

            var rows = new List<RouteEvent>();
            for (int i = 0; i < events.Count; i++)
            {
                RouteEvent e = events[i];
                if (e == null || e.Sequence >= deliverySequence) continue;
                if (e.Type != RouteEventType.RouteCargoDebited
                    && e.Type != RouteEventType.RouteCargoPickedUp
                    && e.Type != RouteEventType.RouteCargoDelivered)
                    continue;
                if (!string.Equals(e.RouteId, route.Id, StringComparison.Ordinal)) continue;
                if (!string.Equals(e.RouteCycleId, cycleId, StringComparison.Ordinal)) continue;
                rows.Add(e);
            }
            if (rows.Count == 0)
                return deficit;

            // Stable window order: Sequence first (the per-window stride), list order
            // for equal sequences.
            var order = new List<int>(rows.Count);
            for (int i = 0; i < rows.Count; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                int c = rows[a].Sequence.CompareTo(rows[b].Sequence);
                return c != 0 ? c : a.CompareTo(b);
            });

            for (int k = 0; k < order.Count; k++)
            {
                RouteEvent e = rows[order[k]];
                if (e.Type == RouteEventType.RouteCargoDelivered)
                {
                    if (deficit.Count == 0) continue;
                    Dictionary<string, double> recorded = RecordedDeliveryFor(route, e.RouteStopIndex);
                    if (recorded == null) continue;
                    var names = new List<string>(deficit.Keys);
                    for (int n = 0; n < names.Count; n++)
                    {
                        string name = names[n];
                        if (!recorded.TryGetValue(name, out double want) || !(want > 0.0)) continue;
                        double got = ReadDeficitAmount(e.RouteResourceManifest, name);
                        double keptOnBoard = want - got;
                        if (!(keptOnBoard > 0.0)) continue;
                        double next = deficit[name] - keptOnBoard;
                        if (next > SupplyDeficitEpsilon) deficit[name] = next;
                        else deficit.Remove(name);
                    }
                    continue;
                }

                // Debit / pickup row: the requested-on-shortfall manifest names every
                // resource whose actual fell short of the recorded requirement.
                Dictionary<string, double> requested = e.RouteRequestedResourceManifest;
                if (requested == null || requested.Count == 0) continue;
                foreach (var kv in requested)
                {
                    if (string.IsNullOrEmpty(kv.Key) || !(kv.Value > 0.0)) continue;
                    double actual = ReadDeficitAmount(e.RouteResourceManifest, kv.Key);
                    double missing = kv.Value - actual;
                    if (!(missing > SupplyDeficitEpsilon)) continue;
                    deficit.TryGetValue(kv.Key, out double cur);
                    deficit[kv.Key] = cur + missing;
                }
            }
            return deficit;
        }

        /// <summary>
        /// The per-resource amount the delivery at <paramref name="stopIndex"/> must hold
        /// back, given the outstanding <paramref name="deficit"/> there (from
        /// <see cref="ComputeCycleSupplyDeficit"/>): the deficit less what the route's later
        /// recorded deliveries of the resource can absorb
        /// (<see cref="LaterDeliveriesAbsorbable"/>), never below zero. Returns an empty map
        /// when this delivery may write its full recorded amount. Pure.
        /// </summary>
        internal static Dictionary<string, double> ComputeDeliveryHoldback(
            Route route, int stopIndex, IReadOnlyDictionary<string, double> deficit)
        {
            var holdback = new Dictionary<string, double>(StringComparer.Ordinal);
            if (route == null || deficit == null || deficit.Count == 0)
                return holdback;
            foreach (var kv in deficit)
            {
                if (string.IsNullOrEmpty(kv.Key) || !(kv.Value > SupplyDeficitEpsilon)) continue;
                double absorbable = LaterDeliveriesAbsorbable(route, stopIndex, kv.Key);
                double held = kv.Value - absorbable;
                if (held > SupplyDeficitEpsilon)
                    holdback[kv.Key] = held;
            }
            return holdback;
        }

        /// <summary>
        /// How much of a deficit outstanding at the delivery of <paramref name="stopIndex"/>
        /// the route's later windows of <paramref name="resourceName"/> can still take: the
        /// largest running total, over the later stops in window order, of their recorded
        /// deliveries less their recorded pickups (a stop's pickup comes before its
        /// delivery), read at each later delivery and never below zero. That is the least
        /// cargo the recorded run still carried after this stop, so withholding only the
        /// rest keeps the transport from delivering cargo it does not have. Pure.
        /// </summary>
        internal static double LaterDeliveriesAbsorbable(Route route, int stopIndex, string resourceName)
        {
            if (route?.Stops == null || string.IsNullOrEmpty(resourceName)) return 0.0;
            int first = (stopIndex >= 0 ? stopIndex : 0) + 1;
            double running = 0.0;
            double best = 0.0;
            for (int j = first; j < route.Stops.Count; j++)
            {
                RouteStop stop = route.Stops[j];
                if (stop == null) continue;
                running -= ReadDeficitAmount(stop.PickupManifest, resourceName);
                double delivered = ReadDeficitAmount(stop.DeliveryManifest, resourceName);
                if (!(delivered > 0.0)) continue;
                running += delivered;
                if (running > best) best = running;
            }
            return best;
        }

        private static Dictionary<string, double> RecordedDeliveryFor(Route route, int stopIndex)
        {
            int idx = stopIndex >= 0 ? stopIndex : 0;
            if (route.Stops == null || idx >= route.Stops.Count) return null;
            return route.Stops[idx]?.DeliveryManifest;
        }

        private static double ReadDeficitAmount(Dictionary<string, double> manifest, string name)
        {
            if (manifest == null || string.IsNullOrEmpty(name)) return 0.0;
            return manifest.TryGetValue(name, out double v) && v > 0.0 ? v : 0.0;
        }
    }
}
