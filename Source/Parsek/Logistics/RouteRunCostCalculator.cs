using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// Display-only net run-cost for a Supply Route, in funds (KSP credits).
    ///
    /// net run cost = vehicle launch cost - recovered credits
    ///
    /// The launch half is the gross cost of the recorded transport (dry parts
    /// plus the resources loaded at the recorded snapshot), delegated to
    /// <see cref="RouteOrchestrator.ComputeDispatchFundsCostForRoute"/> so this
    /// file never re-walks the snapshot. The recovery half sums the actual
    /// distance-scaled <c>FundsAwarded</c> the ledger captured for every
    /// vessel/part recovery in the route's SOURCE TREE (not just the rendered
    /// [root..undock] member set: the fly-home-and-recover leg is post-undock
    /// and excluded from the route member set, so scoping to
    /// <c>Route.RecordingIds</c> would silently return zero recovery, gotcha
    /// G1). All dependencies are injected so the arithmetic is unit-testable
    /// with deterministic lists; the production caller passes the live
    /// <see cref="EffectiveState.ComputeELS"/> result and the route's resolved
    /// tree-member id set.
    ///
    /// This is DISPLAY ONLY (decision D1): it does not change what the
    /// orchestrator charges per dispatch or what the funds gate checks.
    /// </summary>
    internal static class RouteRunCostCalculator
    {
        internal const string Tag = "RouteRunCost";

        /// <summary>
        /// Launch cost (gross), recovered credits (summed over the source tree),
        /// and the net the player actually pays per run of a Supply Route.
        /// </summary>
        internal struct RouteRunCost
        {
            /// <summary>Career AND KSC-origin: funds only exist in that case.</summary>
            public bool Applicable;

            /// <summary>
            /// <see cref="Applicable"/> AND the launch cost resolved (&gt; 0).
            /// False when the source snapshot is missing or not yet hydrated
            /// (gotcha G7): the UI must suppress the line rather than render a
            /// misleading "0 funds".
            /// </summary>
            public bool CostKnown;

            /// <summary>Gross launch cost from the recorded snapshot.</summary>
            public double LaunchCost;

            /// <summary>Sum of recovery payouts over the source tree.</summary>
            public double RecoveredCredits;

            /// <summary>max(0, LaunchCost - RecoveredCredits).</summary>
            public double NetCost;

            /// <summary>How many recovery rows were summed (tooltip / log).</summary>
            public int RecoveryEventCount;
        }

        /// <summary>
        /// Gross launch cost for the route, delegated to the existing
        /// ERS-backed, exception-safe
        /// <see cref="RouteOrchestrator.ComputeDispatchFundsCostForRoute"/>.
        /// Returns 0 when the source recording is not in ERS or its
        /// <c>VesselSnapshot</c> is null / not yet hydrated (gotcha G7).
        /// </summary>
        internal static double ComputeLaunchCost(Route route)
        {
            return RouteOrchestrator.ComputeDispatchFundsCostForRoute(route);
        }

        /// <summary>
        /// Sum <c>FundsAwarded</c> over <paramref name="els"/> for every row that
        /// is a recovery payout (<see cref="GameActionType.FundsEarning"/> with
        /// <see cref="FundsEarningSource.Recovery"/>) whose
        /// <c>RecordingId</c> is a member of the route's source tree
        /// (<paramref name="treeRecordingIds"/>). Both lists are injected so
        /// tests pass deterministic data and so the production caller can hand in
        /// the memoized <see cref="EffectiveState.ComputeELS"/> result (which
        /// already reflects supersede / tombstone state).
        /// </summary>
        /// <param name="route">Route the sum is for (logging only).</param>
        /// <param name="els">
        /// Effective ledger state, read via <see cref="EffectiveState.ComputeELS"/>
        /// by the production caller. A null list is treated as no recoveries.
        /// </param>
        /// <param name="treeRecordingIds">
        /// The recording-id set of the route's source tree (gotcha G1: the WHOLE
        /// tree, not just <c>Route.RecordingIds</c>). A null / empty set means
        /// no recordings are in scope, so the sum is 0.
        /// </param>
        /// <param name="recoveryEventCount">Out: number of rows summed.</param>
        internal static double SumRecoveredCredits(
            Route route,
            IReadOnlyList<GameAction> els,
            HashSet<string> treeRecordingIds,
            out int recoveryEventCount)
        {
            recoveryEventCount = 0;
            if (els == null || els.Count == 0 || treeRecordingIds == null || treeRecordingIds.Count == 0)
                return 0.0;

            double total = 0.0;
            int scanned = 0;
            int matched = 0;
            for (int i = 0; i < els.Count; i++)
            {
                GameAction a = els[i];
                if (a == null)
                    continue;
                if (a.Type != GameActionType.FundsEarning)
                    continue;
                if (a.FundsSource != FundsEarningSource.Recovery)
                    continue;
                scanned++;
                if (string.IsNullOrEmpty(a.RecordingId) || !treeRecordingIds.Contains(a.RecordingId))
                    continue;
                matched++;
                total += a.FundsAwarded;
            }

            recoveryEventCount = matched;
            ParsekLog.Verbose(Tag,
                $"SumRecoveredCredits route={ShortId(route)} recoveryRows={scanned} " +
                $"matched={matched} treeMembers={treeRecordingIds.Count} sum=" +
                total.ToString("R", CultureInfo.InvariantCulture));
            return total;
        }

        /// <summary>
        /// Resolve the recording-id set of the route's source tree
        /// (<c>Route.BackingMissionTreeId</c> -&gt;
        /// <see cref="RouteTreeGuard.FindCommittedTree"/> -&gt;
        /// <c>RecordingTree.Recordings.Keys</c>). Returns an empty set for a null
        /// route, a null / empty backing-tree id, or a tree that does not resolve
        /// (degenerate route): the caller then sees recovered = 0, an acceptable
        /// conservative over-statement of cost (gotcha G1).
        /// </summary>
        internal static HashSet<string> ResolveTreeRecordingIds(Route route)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (route == null)
            {
                ParsekLog.Verbose(Tag, "ResolveTreeRecordingIds: null route: empty member set");
                return ids;
            }

            string treeId = route.BackingMissionTreeId;
            if (string.IsNullOrEmpty(treeId))
            {
                ParsekLog.Verbose(Tag,
                    $"ResolveTreeRecordingIds route={ShortId(route)}: no backing tree id: empty member set");
                return ids;
            }

            RecordingTree tree = RouteTreeGuard.FindCommittedTree(treeId);
            if (tree == null || tree.Recordings == null)
            {
                ParsekLog.Verbose(Tag,
                    $"ResolveTreeRecordingIds route={ShortId(route)} treeId={ShortId(treeId)}: " +
                    "tree not resolved or has no recordings: empty member set");
                return ids;
            }

            foreach (var kv in tree.Recordings)
            {
                if (!string.IsNullOrEmpty(kv.Key))
                    ids.Add(kv.Key);
            }
            return ids;
        }

        /// <summary>
        /// Assemble the <see cref="RouteRunCost"/> from the launch cost, the
        /// injected effective ledger, and the injected tree-member id set.
        ///
        /// <para><c>Applicable = isCareer &amp;&amp; route.IsKscOrigin</c> (funds
        /// only exist in Career, and only a KSC-origin route charges funds rather
        /// than physical cargo, gotcha G5).</para>
        /// <para><c>CostKnown = Applicable &amp;&amp; LaunchCost &gt; 0</c>: a
        /// null / not-yet-hydrated snapshot makes the launch cost 0, which the UI
        /// must NOT render as "0 funds" (gotcha G7).</para>
        /// <para><c>NetCost = max(0, LaunchCost - RecoveredCredits)</c>: the net
        /// floors at 0 so an odd refund or value bug never shows a negative cost
        /// (the run never literally pays the player).</para>
        /// </summary>
        /// <param name="route">Route to cost.</param>
        /// <param name="isCareer">
        /// Career-mode probe, injected so tests do not touch <c>HighLogic</c>.
        /// </param>
        /// <param name="els">
        /// Effective ledger state from <see cref="EffectiveState.ComputeELS"/>
        /// (injected). Supersede / tombstone exclusions already applied by ELS.
        /// </param>
        /// <param name="treeRecordingIds">
        /// Source-tree recording-id set (gotcha G1: whole tree, not the route
        /// member set). Production callers build this via
        /// <see cref="ResolveTreeRecordingIds"/>.
        /// </param>
        internal static RouteRunCost Compute(
            Route route,
            bool isCareer,
            IReadOnlyList<GameAction> els,
            HashSet<string> treeRecordingIds)
        {
            double launchCost = ComputeLaunchCost(route);
            return Assemble(route, isCareer, launchCost, els, treeRecordingIds);
        }

        /// <summary>
        /// Pure assembly of the <see cref="RouteRunCost"/> from an already-known
        /// launch cost. Split out from <see cref="Compute"/> so the arithmetic
        /// (CostKnown gating, recovery sum, net flooring) is unit-testable with
        /// an injected launch cost instead of an ERS-backed snapshot walk.
        /// <see cref="Compute"/> is the production entry: it derives
        /// <paramref name="launchCost"/> from
        /// <see cref="ComputeLaunchCost"/> (ERS) and forwards here.
        /// </summary>
        internal static RouteRunCost Assemble(
            Route route,
            bool isCareer,
            double launchCost,
            IReadOnlyList<GameAction> els,
            HashSet<string> treeRecordingIds)
        {
            RouteRunCost result = default(RouteRunCost);
            result.Applicable = isCareer && route != null && route.IsKscOrigin;

            result.LaunchCost = launchCost;
            result.RecoveredCredits = SumRecoveredCredits(
                route, els, treeRecordingIds, out int recoveryEventCount);
            result.RecoveryEventCount = recoveryEventCount;

            result.CostKnown = result.Applicable && result.LaunchCost > 0.0;

            double net = result.LaunchCost - result.RecoveredCredits;
            result.NetCost = net > 0.0 ? net : 0.0;

            ParsekLog.Verbose(Tag,
                $"RunCost route={ShortId(route)} applicable={result.Applicable} " +
                $"known={result.CostKnown} launch=" +
                result.LaunchCost.ToString("R", CultureInfo.InvariantCulture) +
                " recovered=" +
                result.RecoveredCredits.ToString("R", CultureInfo.InvariantCulture) +
                " net=" +
                result.NetCost.ToString("R", CultureInfo.InvariantCulture) +
                $" recoveries={result.RecoveryEventCount} treeId=" +
                ShortId(route != null ? route.BackingMissionTreeId : null));

            return result;
        }

        private static string ShortId(Route route)
        {
            return ShortId(route != null ? route.Id : null);
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "<no-id>";
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }
    }
}
