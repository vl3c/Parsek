using System;
using System.Collections.Generic;
using Parsek.Logistics;

namespace Parsek
{
    /// <summary>
    /// The sortable columns of the Logistics window's Active / Paused route tables
    /// (L2). The Candidate table is NOT sorted through here (it keeps its static
    /// header), so this enum covers only the route-row columns.
    /// </summary>
    internal enum LogisticsRouteSortColumn
    {
        Name = 0,
        Origin = 1,
        Destination = 2,
        Interval = 3,
        Cycles = 4,
        NextDelivery = 5,
        Status = 6,
        Delivery = 7,
    }

    /// <summary>
    /// The non-Route sort keys for one route that live only in the window's throttled
    /// legibility cache (the H1 next-delivery seconds and the H4 resolved destination
    /// text) plus a pre-resolved Origin / Status display string. Injected into
    /// <see cref="LogisticsSortPresentation.SortRoutes"/> so the comparer never touches
    /// Unity, the orchestrator, or the ledger and stays directly unit-testable.
    /// </summary>
    internal struct RouteSortKeys
    {
        /// <summary>Resolved Origin cell display text (FormatOrigin), for the Origin sort.</summary>
        public string OriginText;

        /// <summary>Resolved Destination cell display text (H4 vessel name or coords fallback).</summary>
        public string DestinationText;

        /// <summary>
        /// Seconds to the next delivery / recheck from the legibility cache. When the
        /// route has no countdown (<see cref="HasNextDelivery"/> false), this is ignored
        /// and the route sorts after every route that does have one.
        /// </summary>
        public double NextDeliverySeconds;

        /// <summary>True when <see cref="NextDeliverySeconds"/> is a real countdown.</summary>
        public bool HasNextDelivery;

        /// <summary>Resolved Status / Delivery-badge display text, for the Status / Delivery sorts.</summary>
        public string StatusText;

        /// <summary>Resolved Delivery-badge display text, for the Delivery sort.</summary>
        public string DeliveryText;
    }

    /// <summary>
    /// Pure, Unity-free, ledger-free sort helpers for the Logistics window's route
    /// tables (L2). Mirrors <see cref="SpawnControlPresentation.SortCandidates"/>:
    /// copy the input list, sort it with a deterministic comparer (a route-id tie-break
    /// keeps equal-key rows in a fixed order across re-sorts), return the copy (the input
    /// is never mutated). The comparer reads only <see cref="Route"/> scalar fields and
    /// the injected per-route <see cref="RouteSortKeys"/>, so it is unit-tested directly
    /// off the IMGUI path and never trips the ERS/ELS grep gate. String compares are
    /// OrdinalIgnoreCase; numeric compares use <c>CompareTo</c>. Null routes and null
    /// names are tolerated (they sort to a stable position, never throw).
    /// </summary>
    internal static class LogisticsSortPresentation
    {
        /// <summary>
        /// Returns a new list of <paramref name="routes"/> sorted by
        /// <paramref name="column"/> in the requested direction. The input list is not
        /// mutated. <paramref name="keys"/> supplies the legibility-derived sort values
        /// per route id (Origin / Destination / NextDelivery / Status / Delivery); a
        /// missing id falls back to empty / no-countdown keys so a not-yet-cached route
        /// still sorts deterministically.
        /// </summary>
        internal static List<Route> SortRoutes(
            IReadOnlyList<Route> routes,
            LogisticsRouteSortColumn column,
            bool ascending,
            IReadOnlyDictionary<string, RouteSortKeys> keys)
        {
            var sorted = new List<Route>();
            if (routes == null)
                return sorted;

            for (int i = 0; i < routes.Count; i++)
                sorted.Add(routes[i]);

            sorted.Sort((a, b) => CompareRoutes(a, b, column, ascending, keys));
            return sorted;
        }

        private static RouteSortKeys KeysFor(Route route, IReadOnlyDictionary<string, RouteSortKeys> keys)
        {
            if (route != null && keys != null && !string.IsNullOrEmpty(route.Id)
                && keys.TryGetValue(route.Id, out RouteSortKeys k))
                return k;
            return default(RouteSortKeys);
        }

        private static int CompareRoutes(
            Route a, Route b,
            LogisticsRouteSortColumn column, bool ascending,
            IReadOnlyDictionary<string, RouteSortKeys> keys)
        {
            // Null-route guard: nulls sort to the end in ascending order (and stay there
            // when the direction flips, because the +/- direction is applied only to the
            // non-null comparison below, not to the null ordering).
            if (a == null && b == null) return 0;
            if (a == null) return 1;
            if (b == null) return -1;

            RouteSortKeys ka = KeysFor(a, keys);
            RouteSortKeys kb = KeysFor(b, keys);

            int comparison;
            switch (column)
            {
                case LogisticsRouteSortColumn.Name:
                    comparison = CompareStrings(a.Name, b.Name);
                    break;

                case LogisticsRouteSortColumn.Origin:
                    comparison = CompareStrings(ka.OriginText, kb.OriginText);
                    break;

                case LogisticsRouteSortColumn.Destination:
                    comparison = CompareStrings(ka.DestinationText, kb.DestinationText);
                    break;

                case LogisticsRouteSortColumn.Interval:
                    comparison = a.DispatchInterval.CompareTo(b.DispatchInterval);
                    break;

                case LogisticsRouteSortColumn.Cycles:
                    comparison = a.CompletedCycles.CompareTo(b.CompletedCycles);
                    break;

                case LogisticsRouteSortColumn.NextDelivery:
                    comparison = CompareNextDelivery(ka, kb);
                    break;

                case LogisticsRouteSortColumn.Status:
                    comparison = CompareStrings(ka.StatusText, kb.StatusText);
                    break;

                case LogisticsRouteSortColumn.Delivery:
                    comparison = CompareStrings(ka.DeliveryText, kb.DeliveryText);
                    break;

                default:
                    comparison = CompareStrings(a.Name, b.Name);
                    break;
            }

            int primary = ascending ? comparison : -comparison;
            if (primary != 0)
                return primary;
            // Deterministic tie-break on route id (direction-independent): List.Sort is an
            // unstable introsort, so equal primary keys could otherwise reorder between
            // re-sorts. Comparing the stable route id keeps tied rows in a fixed order.
            return CompareStrings(a.Id, b.Id);
        }

        // A route with no countdown is treated as having a +infinity countdown so it
        // sorts LAST in ascending ("soonest delivery first") order. This is a uniform
        // numeric compare, so the caller's ascending/descending negation flips it like
        // any other key (descending puts the no-countdown routes first). Using a sentinel
        // rather than a special pre-signed branch keeps the ordering deterministic and
        // direction-consistent for the test.
        private static int CompareNextDelivery(RouteSortKeys a, RouteSortKeys b)
        {
            double sa = a.HasNextDelivery ? a.NextDeliverySeconds : double.PositiveInfinity;
            double sb = b.HasNextDelivery ? b.NextDeliverySeconds : double.PositiveInfinity;
            return sa.CompareTo(sb);
        }

        private static int CompareStrings(string a, string b)
        {
            return string.Compare(a ?? string.Empty, b ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
