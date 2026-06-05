using System;
using System.Collections.Generic;

namespace Parsek.Logistics
{
    /// <summary>
    /// Mutual-exclusion guard between Supply Routes and manual looping (design
    /// §0.5 / §0.6): a tree is EITHER a supply route OR a manually looped
    /// recording / mission, never both. A route binds its source tree
    /// (<c>SourceRefs[].TreeId</c>) whenever its status
    /// <see cref="RouteStatusPolicy.BindsTree"/> is TRUE (which is every status:
    /// a route owns its tree until the player explicitly removes it). While a
    /// tree is bound:
    /// <list type="bullet">
    ///   <item>the Missions-window mission Loop toggle for that tree is greyed
    ///   OFF and a turn-ON is blocked at commit; and</item>
    ///   <item>every Recordings-window loop control (per-recording / group /
    ///   chain / bulk) whose affected recording set intersects the bound tree
    ///   is greyed OFF and a turn-ON is blocked at commit.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Plan: <c>docs/dev/plan-logistics-routes-on-missions.md</c> Phase 2.
    /// Design: <c>docs/parsek-logistics-supply-routes-design.md</c> §0.5 / §0.6.
    /// </para>
    /// <para>
    /// This file reads ONLY <see cref="RouteStore.CommittedRoutes"/> (the route
    /// store surface) for binding decisions. It does NOT read the raw
    /// committed-recording list or raw ledger actions, so it sits outside the
    /// ERS/ELS grep gate. The per-recording loop clear in
    /// <see cref="ForceClearManualLoopForRouteTree"/> walks the tree's OWN
    /// <see cref="RecordingTree.Recordings"/> dictionary (tree-local, via
    /// <see cref="RecordingStore.CommittedTrees"/>) rather than the raw
    /// <c>CommittedRecordings</c> list, again staying off the gate.
    /// </para>
    /// <para>
    /// Pure with respect to Unity: no <c>GameObject</c> / <c>Planetarium</c>
    /// reads. Callers pass <c>currentUT</c> in. This keeps the predicates and
    /// the clear directly xUnit-testable.
    /// </para>
    /// </remarks>
    internal static class RouteTreeGuard
    {
        private const string Tag = "RouteGuard";

        /// <summary>
        /// TRUE when at least one committed route in a
        /// <see cref="RouteStatusPolicy.BindsTree"/> status binds
        /// <paramref name="treeId"/> via one of its
        /// <c>SourceRefs[].TreeId</c> entries. A null / empty tree id never
        /// binds (returns FALSE, no log). A TRUE result is Verbose-logged with
        /// the binding route's id + status so a greyed toggle is explainable
        /// from the log.
        /// </summary>
        internal static bool IsTreeBoundToActiveRoute(string treeId)
        {
            return RouteBindingFor(treeId, out _);
        }

        /// <summary>
        /// Resolves the FIRST committed route (list order) that binds
        /// <paramref name="treeId"/>. Returns TRUE + sets
        /// <paramref name="route"/> when found, FALSE + null otherwise. Used by
        /// the UI to label "Looped by route: &lt;name&gt;" and by the log lines.
        /// </summary>
        internal static bool RouteBindingFor(string treeId, out Route route)
        {
            route = null;
            if (string.IsNullOrEmpty(treeId))
                return false;

            var routes = RouteStore.CommittedRoutes;
            if (routes == null)
                return false;

            for (int i = 0; i < routes.Count; i++)
            {
                Route r = routes[i];
                if (r == null)
                    continue;
                if (!RouteStatusPolicy.BindsTree(r.Status))
                    continue;
                if (!RouteBindsTreeId(r, treeId))
                    continue;

                route = r;
                ParsekLog.Verbose(Tag,
                    $"IsTreeBoundToActiveRoute: tree={treeId} BOUND by route " +
                    $"{ShortId(r.Id)} status={r.Status} name='{r.Name ?? "<none>"}'");
                return true;
            }

            return false;
        }

        /// <summary>
        /// All distinct tree ids currently bound by a committed route in a
        /// <see cref="RouteStatusPolicy.BindsTree"/> status. Used by the
        /// load-time reconcile pass and tests. Order is route-list /
        /// source-ref order; duplicates collapse.
        /// </summary>
        internal static IReadOnlyList<string> BoundTreeIds()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();

            var routes = RouteStore.CommittedRoutes;
            if (routes == null)
                return result;

            for (int i = 0; i < routes.Count; i++)
            {
                Route r = routes[i];
                if (r == null || r.SourceRefs == null)
                    continue;
                if (!RouteStatusPolicy.BindsTree(r.Status))
                    continue;
                for (int s = 0; s < r.SourceRefs.Count; s++)
                {
                    RouteSourceRef sref = r.SourceRefs[s];
                    if (sref == null || string.IsNullOrEmpty(sref.TreeId))
                        continue;
                    if (seen.Add(sref.TreeId))
                        result.Add(sref.TreeId);
                }
            }

            return result;
        }

        /// <summary>
        /// Clears BOTH manual-loop surfaces for <paramref name="treeId"/> so a
        /// newly-activated route's tree never has a competing manual loop
        /// (route looping wins over any pre-existing manual loop):
        /// <list type="number">
        ///   <item>every looping <see cref="Mission"/> on that tree is turned
        ///   OFF via the existing <see cref="MissionStore.SetLoopEnabled"/>
        ///   (read-only consumption of the locked Missions subsystem); and</item>
        ///   <item>every recording on that tree that carries
        ///   <see cref="Recording.LoopPlayback"/> is set to FALSE (the
        ///   Recordings-tab loop bypasses MissionStore entirely, so this is the
        ///   only thing that clears a per-recording loop).</item>
        /// </list>
        /// Idempotent: a second call clears nothing and logs zero counts.
        /// Clearing does NOT auto-start any loop; release on route
        /// delete/clear is passive (the predicate recomputes FALSE next frame).
        /// Logs a single Info summary of both surfaces.
        /// </summary>
        /// <param name="treeId">Source tree id to clear.</param>
        /// <param name="currentUT">Game UT, passed to
        /// <see cref="MissionStore.SetLoopEnabled"/> (it stamps a loop anchor on
        /// enable; on disable the value is ignored).</param>
        /// <returns>The total number of manual loops actually turned off (mission
        /// loops + per-recording loops). 0 when the tree had no manual loop, so the
        /// caller can fire a "loop turned off" toast ONLY on a real clear (M5).</returns>
        internal static int ForceClearManualLoopForRouteTree(string treeId, double currentUT)
        {
            if (string.IsNullOrEmpty(treeId))
            {
                ParsekLog.Verbose(Tag,
                    "ForceClearManualLoopForRouteTree: null/empty treeId: skipped");
                return 0;
            }

            // (a) Mission loop surface. SetLoopEnabled only flips bools on
            // same-tree siblings (never adds/removes), so iterating Missions
            // while calling it is safe. Snapshot into a local list first to
            // avoid any subtle re-entrancy concern.
            int missionsCleared = 0;
            var missions = MissionStore.Missions;
            if (missions != null)
            {
                // Materialize the targets first; SetLoopEnabled mutates the
                // shared list's elements (bools only) but never structure.
                List<Mission> toClear = null;
                for (int i = 0; i < missions.Count; i++)
                {
                    Mission m = missions[i];
                    if (m == null || !m.LoopPlayback)
                        continue;
                    if (!string.Equals(m.TreeId, treeId, StringComparison.Ordinal))
                        continue;
                    (toClear ?? (toClear = new List<Mission>())).Add(m);
                }
                if (toClear != null)
                {
                    for (int i = 0; i < toClear.Count; i++)
                    {
                        MissionStore.SetLoopEnabled(toClear[i], false, currentUT);
                        missionsCleared++;
                    }
                }
            }

            // (b) Per-recording loop surface. Walk the tree's OWN recording
            // dictionary (tree-local; stays off the CommittedRecordings grep
            // gate). Route looping wins over a pre-existing per-recording loop.
            int recordingsCleared = 0;
            RecordingTree tree = FindCommittedTree(treeId);
            if (tree != null && tree.Recordings != null)
            {
                foreach (var kv in tree.Recordings)
                {
                    Recording rec = kv.Value;
                    if (rec == null || !rec.LoopPlayback)
                        continue;
                    rec.LoopPlayback = false;
                    recordingsCleared++;
                }
            }

            ParsekLog.Info(Tag,
                $"ForceClearManualLoopForRouteTree: tree={treeId} cleared " +
                $"{missionsCleared} mission loop(s) + {recordingsCleared} per-recording loop(s)");
            return missionsCleared + recordingsCleared;
        }

        /// <summary>
        /// Convenience for the AddRoute production sites: clears both manual-loop
        /// surfaces for every distinct source-tree the route binds (the
        /// guaranteed-equal <c>SourceRefs[].TreeId</c> ==
        /// <see cref="Route.BackingMissionTreeId"/> set). A null route or a route
        /// with no resolvable source trees is a no-op (Verbose-logged). Each tree
        /// is cleared at most once.
        /// </summary>
        /// <returns>The total number of manual loops actually turned off across the
        /// distinct source trees (0 when nothing was cleared), so the create path can
        /// fire a "loop turned off" toast ONLY on a real clear (M5).</returns>
        internal static int ForceClearManualLoopForRoute(Route route, double currentUT)
        {
            if (route == null)
            {
                ParsekLog.Verbose(Tag, "ForceClearManualLoopForRoute: null route: skipped");
                return 0;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            int totalCleared = 0;

            // Prefer the explicit source-ref tree ids; fall back to the
            // backing-mission tree id when source refs are absent (defensive).
            if (route.SourceRefs != null)
            {
                for (int s = 0; s < route.SourceRefs.Count; s++)
                {
                    RouteSourceRef sref = route.SourceRefs[s];
                    if (sref == null || string.IsNullOrEmpty(sref.TreeId))
                        continue;
                    if (seen.Add(sref.TreeId))
                        totalCleared += ForceClearManualLoopForRouteTree(sref.TreeId, currentUT);
                }
            }
            if (!string.IsNullOrEmpty(route.BackingMissionTreeId)
                && seen.Add(route.BackingMissionTreeId))
            {
                totalCleared += ForceClearManualLoopForRouteTree(route.BackingMissionTreeId, currentUT);
            }

            if (seen.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"ForceClearManualLoopForRoute: route {ShortId(route.Id)} has no resolvable source tree(s): nothing cleared");
            }

            return totalCleared;
        }

        // True when any of the route's SourceRefs carries the given tree id.
        private static bool RouteBindsTreeId(Route route, string treeId)
        {
            if (route == null || route.SourceRefs == null)
                return false;
            for (int s = 0; s < route.SourceRefs.Count; s++)
            {
                RouteSourceRef sref = route.SourceRefs[s];
                if (sref == null)
                    continue;
                if (string.Equals(sref.TreeId, treeId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        // Tree-local lookup by id over CommittedTrees. CommittedTrees is NOT a
        // grep-gated surface (the ERS/ELS gate only fires on the raw committed-
        // recording list and the raw ledger-actions list), and walking a tree's
        // own Recordings dictionary is tree-scoped, not an ERS read.
        // Exposed internal so the Logistics layer (RouteRunCostCalculator) can
        // reuse the one tree-by-id lookup instead of duplicating the walk.
        internal static RecordingTree FindCommittedTree(string treeId)
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees == null)
                return null;
            for (int i = 0; i < trees.Count; i++)
            {
                RecordingTree t = trees[i];
                if (t != null && string.Equals(t.Id, treeId, StringComparison.Ordinal))
                    return t;
            }
            return null;
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "<no-id>";
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }
    }
}
