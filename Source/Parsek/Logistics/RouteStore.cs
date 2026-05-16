using System.Collections.Generic;

namespace Parsek.Logistics
{
    /// <summary>
    /// Static in-memory store for committed supply routes (design §4.7).
    /// Survives scene changes within a KSP session. Save/load is handled by
    /// <see cref="ParsekScenario"/> driving <see cref="SaveRoutesTo"/> /
    /// <see cref="LoadRoutesFrom"/>.
    ///
    /// Phase 3 owns CRUD + codec drivers only. Source-ref validation against
    /// the effective recording set (Phase 5) deliberately lives elsewhere —
    /// this file must not read the raw committed-recording list or raw ledger
    /// actions directly; route everything through EffectiveState (CI gated by
    /// <c>scripts/grep-audit-ers-els.ps1</c>).
    /// </summary>
    internal static class RouteStore
    {
        private const string Tag = "RouteStore";
        private const string RoutesParentNodeName = "ROUTES";
        private const string RouteChildNodeName = "ROUTE";

        private static readonly List<Route> committedRoutes = new List<Route>();

        /// <summary>Read-only view of currently committed routes.</summary>
        internal static IReadOnlyList<Route> CommittedRoutes => committedRoutes;

        /// <summary>
        /// Add a route. Idempotent on <see cref="Route.Id"/>: a second call
        /// with the same Id logs a Warn and does NOT replace the existing
        /// entry. Callers wanting replace semantics must remove-then-add.
        /// </summary>
        internal static void AddRoute(Route route)
        {
            if (route == null)
            {
                ParsekLog.Warn(Tag, "AddRoute: null route — ignored");
                return;
            }
            if (string.IsNullOrEmpty(route.Id))
            {
                ParsekLog.Warn(Tag, "AddRoute: route with empty Id — ignored");
                return;
            }

            for (int i = 0; i < committedRoutes.Count; i++)
            {
                if (string.Equals(committedRoutes[i].Id, route.Id, System.StringComparison.Ordinal))
                {
                    ParsekLog.Warn(Tag,
                        $"AddRoute: duplicate id={ShortId(route.Id)} (full={route.Id}); " +
                        "keeping the original entry. Callers wanting replace semantics " +
                        "must RemoveRoute first, then AddRoute.");
                    return;
                }
            }

            committedRoutes.Add(route);
            int stopCount = route.Stops != null ? route.Stops.Count : 0;
            ParsekLog.Info(Tag,
                $"Route {ShortId(route.Id)} added: status={route.Status} stops={stopCount}");
        }

        /// <summary>
        /// Remove a route by Id. Returns true on removal, false on miss or on
        /// an empty/null id (both logged at Warn).
        /// </summary>
        internal static bool RemoveRoute(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                ParsekLog.Warn(Tag, "RemoveRoute: null or empty id — ignored");
                return false;
            }

            for (int i = 0; i < committedRoutes.Count; i++)
            {
                if (string.Equals(committedRoutes[i].Id, id, System.StringComparison.Ordinal))
                {
                    committedRoutes.RemoveAt(i);
                    ParsekLog.Info(Tag, $"Route {ShortId(id)} removed");
                    return true;
                }
            }

            ParsekLog.Warn(Tag, $"RemoveRoute: route {ShortId(id)} not found (full={id})");
            return false;
        }

        /// <summary>
        /// Look up a route by Id. Silent on both hit and miss — callers
        /// decide whether to log absence as a warning.
        /// </summary>
        internal static bool TryGetRoute(string id, out Route route)
        {
            if (string.IsNullOrEmpty(id))
            {
                route = null;
                return false;
            }

            for (int i = 0; i < committedRoutes.Count; i++)
            {
                if (string.Equals(committedRoutes[i].Id, id, System.StringComparison.Ordinal))
                {
                    route = committedRoutes[i];
                    return true;
                }
            }

            route = null;
            return false;
        }

        /// <summary>
        /// Clear in-memory state. Test seam — production paths should
        /// remove individual routes through <see cref="RemoveRoute"/>.
        /// </summary>
        internal static void ResetForTesting()
        {
            int prevCount = committedRoutes.Count;
            committedRoutes.Clear();
            ParsekLog.Verbose(Tag, $"ResetForTesting prevCount={prevCount}");
        }

        /// <summary>
        /// Write the current store into <paramref name="parent"/>. Strips any
        /// pre-existing <c>ROUTES</c> children first so stale entries from a
        /// prior save do not leak. When the store is empty, no <c>ROUTES</c>
        /// node is written at all — saves stay lean and
        /// <see cref="LoadRoutesFrom"/> treats a missing node as zero routes.
        /// </summary>
        internal static void SaveRoutesTo(ConfigNode parent)
        {
            if (parent == null)
            {
                ParsekLog.Warn(Tag, "SaveRoutesTo: null parent — skipped");
                return;
            }

            // Always strip pre-existing wrappers before deciding what to
            // write. A previously-saved ROUTES node with stale entries would
            // otherwise survive an empty-store save.
            parent.RemoveNodes(RoutesParentNodeName);

            if (committedRoutes.Count == 0)
            {
                ParsekLog.Verbose(Tag, "SaveRoutesTo: no routes to save");
                return;
            }

            ConfigNode routesNode = parent.AddNode(RoutesParentNodeName);
            for (int i = 0; i < committedRoutes.Count; i++)
            {
                Route route = committedRoutes[i];
                if (route == null) continue;
                ConfigNode routeNode = routesNode.AddNode(RouteChildNodeName);
                route.SerializeInto(routeNode);
            }

            ParsekLog.Info(Tag, $"SaveRoutesTo: wrote {committedRoutes.Count} route(s)");
        }

        /// <summary>
        /// Replace in-memory state with the contents of the <c>ROUTES</c>
        /// child node under <paramref name="parent"/>. Missing
        /// <c>ROUTES</c> node is the common "save with no routes" path —
        /// returns zero without warning. Routes that the Phase-2 codec
        /// rejects (null) are dropped silently here; the codec already
        /// emitted its own Warn explaining the reject reason.
        /// </summary>
        /// <returns>Number of routes successfully loaded.</returns>
        internal static int LoadRoutesFrom(ConfigNode parent)
        {
            // Wholesale replace: clear first, then fill from the save node.
            // Mirrors MilestoneStore.LoadMilestoneFile / RecordingStore load
            // semantics so callers do not have to manage the reset themselves.
            committedRoutes.Clear();

            if (parent == null)
            {
                ParsekLog.Verbose(Tag, "LoadRoutesFrom: null parent — 0 loaded");
                return 0;
            }

            ConfigNode routesNode = parent.GetNode(RoutesParentNodeName);
            if (routesNode == null)
            {
                ParsekLog.Verbose(Tag, "LoadRoutesFrom: no ROUTES node, 0 loaded");
                return 0;
            }

            ConfigNode[] routeNodes = routesNode.GetNodes(RouteChildNodeName);
            int loaded = 0;
            int dropped = 0;
            for (int i = 0; i < routeNodes.Length; i++)
            {
                Route route = Route.DeserializeFrom(routeNodes[i]);
                if (route == null)
                {
                    // Codec already logged the Warn explaining why.
                    dropped++;
                    continue;
                }
                committedRoutes.Add(route);
                loaded++;
            }

            if (dropped > 0)
            {
                ParsekLog.Info(Tag,
                    $"LoadRoutesFrom: loaded {loaded} route(s), {dropped} dropped (see prior Warn lines)");
            }
            else
            {
                ParsekLog.Info(Tag, $"LoadRoutesFrom: loaded {loaded} route(s)");
            }

            return loaded;
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "<no-id>";
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }
    }
}
