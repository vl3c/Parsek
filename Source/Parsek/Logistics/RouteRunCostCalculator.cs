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
    /// vessel/part recovery in the route's SOURCE TREE AS OF ROUTE CREATION
    /// (not just the rendered [root..undock] member set: the
    /// fly-home-and-recover leg is post-undock and excluded from the route
    /// member set, so scoping to <c>Route.RecordingIds</c> would silently
    /// return zero recovery, gotcha G1; and not the whole CURRENT tree:
    /// branches added after creation would inflate the recurring credit,
    /// M-MIS-9-R1). All dependencies are injected so the arithmetic is unit-testable
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
        /// The recording-id set of the route's source tree (gotcha G1: the whole
        /// tree as of route creation, not just <c>Route.RecordingIds</c>; see
        /// <see cref="ResolveTreeRecordingIds(Route)"/>). A null / empty set
        /// means no recordings are in scope, so the sum is 0.
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
        /// <c>RecordingTree.Recordings.Keys</c>), scoped to the route's
        /// creation-time membership (M-MIS-9-R1): when
        /// <c>Route.CreationTreeRecordingIds</c> is non-empty, ids the tree
        /// gained AFTER route creation (re-fly forks, switch-fly continuations)
        /// are dropped so a post-creation recovered branch cannot inflate the
        /// recurring credit. The snapshot was taken from the WHOLE tree at
        /// creation, so the post-undock fly-home-and-recover leg stays in
        /// scope (gotcha G1). An empty snapshot FAILS OPEN to the whole
        /// current tree (degenerate / pre-field route), preserving G1's
        /// never-silently-zero contract. Returns an empty set for a null
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

            // (M-MIS-9-R1) Creation-time freeze: intersect with the snapshot
            // captured at route creation so post-creation branches never enter
            // the recovery-credit scope. Empty snapshot = fail open (whole
            // current tree), logged so the degenerate path is visible.
            HashSet<string> creation = route.CreationTreeRecordingIds;
            if (creation == null || creation.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"ResolveTreeRecordingIds route={ShortId(route)} treeId={ShortId(treeId)}: " +
                    $"no creation snapshot: fail-open whole tree ({ids.Count.ToString(CultureInfo.InvariantCulture)} ids)");
                return ids;
            }

            int before = ids.Count;
            ids.IntersectWith(creation);
            int dropped = before - ids.Count;
            ParsekLog.Verbose(Tag,
                $"ResolveTreeRecordingIds route={ShortId(route)} treeId={ShortId(treeId)}: " +
                $"creation-scope kept={ids.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"droppedPostCreation={dropped.ToString(CultureInfo.InvariantCulture)} " +
                $"snapshot={creation.Count.ToString(CultureInfo.InvariantCulture)}");
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
        /// Source-tree recording-id set (gotcha G1: whole tree as of route
        /// creation, not the route member set). Production callers build this
        /// via <see cref="ResolveTreeRecordingIds(Route)"/>.
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

        // ==================================================================
        // Candidate path (no Route object yet)
        // ==================================================================
        //
        // The route-creation summary and the candidate row run BEFORE any
        // Route exists: the player has not promoted the Supply Run yet. The
        // inputs are the same (source snapshot for launch, source tree for
        // recovery), so the candidate path mirrors Compute / Assemble but
        // takes the source recording + tree + an explicit KSC-origin flag
        // instead of reading them off a Route.

        /// <summary>
        /// Resolve the recording-id set of a candidate's source tree directly
        /// from the in-hand <see cref="RecordingTree"/> (the candidate already
        /// holds it, so no <see cref="RouteTreeGuard.FindCommittedTree"/> store
        /// lookup is needed). No creation-time scoping here (M-MIS-9-R1): the
        /// candidate has no Route yet, so creation is NOW and the whole tree IS
        /// the creation-time set. Returns an empty set for a null tree or a tree
        /// with no recordings (then recovered = 0, the same conservative
        /// over-statement as the route path, gotcha G1).
        /// </summary>
        internal static HashSet<string> ResolveTreeRecordingIds(RecordingTree tree)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (tree == null || tree.Recordings == null)
            {
                ParsekLog.Verbose(Tag,
                    "ResolveTreeRecordingIds(tree): null tree or no recordings: empty member set");
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
        /// Sum recovery <c>FundsAwarded</c> over <paramref name="els"/> for rows
        /// whose <c>RecordingId</c> is in <paramref name="treeRecordingIds"/>.
        /// The Route-less sibling of <see cref="SumRecoveredCredits(Route,
        /// IReadOnlyList{GameAction}, HashSet{string}, out int)"/> used by the
        /// candidate path; identical predicate (FundsEarning + Recovery + tree
        /// membership), logged without a route id.
        /// </summary>
        private static double SumRecoveredCreditsForCandidate(
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
                $"SumRecoveredCredits candidate recoveryRows={scanned} " +
                $"matched={matched} treeMembers={treeRecordingIds.Count} sum=" +
                total.ToString("R", CultureInfo.InvariantCulture));
            return total;
        }

        /// <summary>
        /// Assemble a <see cref="RouteRunCost"/> for a candidate from an
        /// already-known launch cost, an explicit KSC-origin flag, the injected
        /// effective ledger, and the injected tree-member id set. Pure (every
        /// dependency injected), so the arithmetic is unit-testable without a
        /// live snapshot walk. <see cref="ComputeForCandidate"/> is the
        /// production entry that derives <paramref name="launchCost"/> from the
        /// source snapshot and forwards here.
        ///
        /// <para><c>Applicable = isCareer &amp;&amp; isKscOrigin</c> (gotcha
        /// G5), <c>CostKnown = Applicable &amp;&amp; LaunchCost &gt; 0</c>
        /// (gotcha G7), <c>NetCost = max(0, LaunchCost - RecoveredCredits)</c>.
        /// </para>
        /// </summary>
        internal static RouteRunCost AssembleForCandidate(
            bool isCareer,
            bool isKscOrigin,
            double launchCost,
            IReadOnlyList<GameAction> els,
            HashSet<string> treeRecordingIds)
        {
            RouteRunCost result = default(RouteRunCost);
            result.Applicable = isCareer && isKscOrigin;

            result.LaunchCost = launchCost;
            result.RecoveredCredits = SumRecoveredCreditsForCandidate(
                els, treeRecordingIds, out int recoveryEventCount);
            result.RecoveryEventCount = recoveryEventCount;

            result.CostKnown = result.Applicable && result.LaunchCost > 0.0;

            double net = result.LaunchCost - result.RecoveredCredits;
            result.NetCost = net > 0.0 ? net : 0.0;

            ParsekLog.Verbose(Tag,
                $"RunCost candidate applicable={result.Applicable} " +
                $"known={result.CostKnown} launch=" +
                result.LaunchCost.ToString("R", CultureInfo.InvariantCulture) +
                " recovered=" +
                result.RecoveredCredits.ToString("R", CultureInfo.InvariantCulture) +
                " net=" +
                result.NetCost.ToString("R", CultureInfo.InvariantCulture) +
                $" recoveries={result.RecoveryEventCount}");

            return result;
        }

        /// <summary>
        /// Decide a candidate's KSC origin the way <c>RouteBuilder</c> decides
        /// <see cref="Route.IsKscOrigin"/>: the launch-site / start-body info
        /// lives on the FIRST recording of the flight (the tree ROOT), NOT on the
        /// window-carrying merged child the candidate's
        /// <c>analysis.SourceRecording</c> points at. That child started
        /// mid-flight at the dock, so it has no launch site
        /// (<c>LaunchSiteName == null</c>), and every supply route requires a
        /// dock+transfer+undock, so on the common multi-recording docking flight
        /// reading the predicate off the dock child would wrongly report
        /// non-KSC. Resolve the tree root via
        /// <c>tree.RootRecordingId -&gt; tree.Recordings[...]</c> (mirroring
        /// <c>RouteBuilder</c>) and test <c>LaunchSiteName</c> set AND
        /// <c>StartBodyName == "Kerbin"</c> on it; fall back to
        /// <paramref name="source"/> only when the tree has no resolvable root
        /// (the legacy single-recording case where the source IS the root). This
        /// keeps the candidate KSC gate consistent with the built
        /// <see cref="Route.IsKscOrigin"/> across all three display surfaces
        /// (gotcha G5 / decision D2).
        /// </summary>
        internal static bool IsCandidateKscOrigin(Recording source, RecordingTree tree)
        {
            Recording originRec = source;
            bool usedRoot = false;
            if (tree?.Recordings != null
                && !string.IsNullOrEmpty(tree.RootRecordingId)
                && tree.Recordings.TryGetValue(tree.RootRecordingId, out Recording rootRec)
                && rootRec != null)
            {
                originRec = rootRec;
                usedRoot = true;
            }

            bool isKscOrigin = originRec != null
                && !string.IsNullOrEmpty(originRec.LaunchSiteName)
                && string.Equals(originRec.StartBodyName, "Kerbin", StringComparison.Ordinal);

            ParsekLog.Verbose(Tag,
                $"IsCandidateKscOrigin source={ShortId(source != null ? source.RecordingId : null)} " +
                $"tree={ShortId(tree != null ? tree.Id : null)} " +
                $"rootId={ShortId(tree != null ? tree.RootRecordingId : null)} usedRoot={usedRoot} " +
                $"launchSite='{(originRec != null ? originRec.LaunchSiteName ?? "<null>" : "<null>")}' " +
                $"startBody='{(originRec != null ? originRec.StartBodyName ?? "<null>" : "<null>")}' " +
                $"kscOrigin={isKscOrigin}");

            return isKscOrigin;
        }

        /// <summary>
        /// Production candidate entry: compute the launch cost from the source
        /// recording's <c>VesselSnapshot</c> via
        /// <see cref="RouteFundsCalculator.ComputeDispatchFundsCost"/> (the same
        /// gross walk the route path uses through ERS, but the candidate already
        /// holds its source recording, so no ERS lookup is needed), then
        /// assemble the struct. The two cost lookups are injected so tests pass
        /// deterministic prices; production hands in the live
        /// <see cref="LiveRouteRuntimeEnvironment.LookupPartCost"/> /
        /// <see cref="LiveRouteRuntimeEnvironment.LookupResourceUnitCost"/>. A
        /// null source or null <c>VesselSnapshot</c> yields launch = 0, which
        /// makes <c>CostKnown</c> false (gotcha G7: the UI then suppresses the
        /// line rather than rendering "0 funds").
        /// </summary>
        internal static RouteRunCost ComputeForCandidate(
            Recording source,
            RecordingTree tree,
            bool isCareer,
            bool isKscOrigin,
            IReadOnlyList<GameAction> els,
            Func<string, float> partCostLookup,
            Func<string, float> resourceUnitCostLookup)
        {
            double launchCost = (source != null && source.VesselSnapshot != null)
                ? RouteFundsCalculator.ComputeDispatchFundsCost(
                    source.VesselSnapshot, partCostLookup, resourceUnitCostLookup)
                : 0.0;
            HashSet<string> treeRecordingIds = ResolveTreeRecordingIds(tree);
            return AssembleForCandidate(
                isCareer, isKscOrigin, launchCost, els, treeRecordingIds);
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
