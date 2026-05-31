using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// The ONE backing-mission helper for Supply Routes (design §0; plan Phase 0
    /// task 4 / Phase 1). A v0 route renders as a looped Mission segment over its
    /// source tree's <c>[launch .. undock]</c> path. This helper owns the two
    /// route-side derivations:
    /// <list type="number">
    ///   <item><see cref="ComputeExcludedIntervalKeys"/> — which composition
    ///   intervals to drop so the rendered window end-trims to
    ///   <c>[launch .. undock]</c> (the post-undock survivor / payload tail is
    ///   excluded).</item>
    ///   <item><see cref="BuildMission"/> — the on-demand, route-owned
    ///   <see cref="Mission"/> object handed to <c>MissionLoopUnitBuilder.Build</c>.
    ///   It is NEVER inserted into <c>MissionStore</c>.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Consumes the Missions structure / composition pipeline READ-ONLY:
    /// <c>MissionStructureBuilder.Build(tree)</c> →
    /// <c>MissionCompositionBuilder.Build(structure)</c>. The window START derives
    /// from the tree ROOT launch (<paramref name="launchUT"/>, the root
    /// recording's StartUT), NOT from the mid-flight merged dock child. The END is
    /// the undock UT.
    /// </para>
    /// <para>
    /// Phase 0 clock-ownership pin: the route's dispatch PHASE is owned by the
    /// loop-clock crossing detector + <c>Route.LastObservedLoopCycleIndex</c>, NOT
    /// by the Mission anchor. <c>MissionLoopUnitBuilder</c> floors the anchor to
    /// <c>spanEndUT</c>, so a <c>LoopAnchorUT</c>-valued anchor is silently
    /// overridden — the Mission's <c>LoopIntervalSeconds</c> drives render CADENCE
    /// only.
    /// </para>
    /// </remarks>
    internal static class RouteBackingMission
    {
        private const string Tag = "Route";

        /// <summary>
        /// Small UT epsilon for the straddling-interval boundary test. An interval
        /// whose start is at-or-after <c>undockUT - Epsilon</c> is treated as
        /// post-undock and excluded; an interval that merely ENDS at the undock is
        /// kept (its start is strictly before). Tiny relative to any real flight
        /// timeline so it only resolves exact-boundary ties.
        /// </summary>
        internal const double BoundaryEpsilonSeconds = 1e-6;

        /// <summary>
        /// Derives the set of composition-interval keys
        /// (<c>MissionCompositionNode.HeadLegId</c>) to EXCLUDE so the backing
        /// mission renders only <c>[launchUT .. undockUT]</c>. Pure.
        /// </summary>
        /// <param name="tree">Source recording tree (read-only).</param>
        /// <param name="undockUT">End of the route segment (the undock instant).</param>
        /// <param name="launchUT">Start of the route segment (tree ROOT launch UT).</param>
        /// <returns>
        /// Excluded interval keys. Empty set on any guard failure (NaN inputs,
        /// <c>undockUT &lt;= launchUT</c>, or no composition roots) — the whole
        /// segment renders, an honest fallback.
        /// </returns>
        internal static HashSet<string> ComputeExcludedIntervalKeys(
            RecordingTree tree, double undockUT, double launchUT)
        {
            var excluded = new HashSet<string>();
            var ic = CultureInfo.InvariantCulture;

            // --- Guards: any malformed window renders the whole segment. ---
            if (tree == null)
            {
                ParsekLog.Verbose(Tag,
                    "ComputeExcludedIntervalKeys: tree=<null> -> empty (whole segment renders)");
                return excluded;
            }
            if (double.IsNaN(undockUT) || double.IsNaN(launchUT))
            {
                ParsekLog.Verbose(Tag,
                    $"ComputeExcludedIntervalKeys: NaN window tree={tree.Id ?? "<null>"} " +
                    $"undockUT={undockUT.ToString("R", ic)} launchUT={launchUT.ToString("R", ic)} " +
                    "-> empty (whole segment renders)");
                return excluded;
            }
            if (undockUT <= launchUT)
            {
                ParsekLog.Verbose(Tag,
                    $"ComputeExcludedIntervalKeys: undockUT<=launchUT tree={tree.Id ?? "<null>"} " +
                    $"undockUT={undockUT.ToString("R", ic)} launchUT={launchUT.ToString("R", ic)} " +
                    "-> empty (whole segment renders)");
                return excluded;
            }

            // --- Build the composition pipeline (read-only Missions seam). ---
            MissionStructure structure = MissionStructureBuilder.Build(tree);
            List<MissionCompositionNode> roots = MissionCompositionBuilder.Build(structure);
            if (roots == null || roots.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"ComputeExcludedIntervalKeys: no composition roots tree={tree.Id ?? "<null>"} " +
                    "-> empty (whole segment renders)");
                return excluded;
            }

            // Collect the recording ids that separated at an Undock branch point.
            // Keying the trim on these (rather than a pure StartUT scan) is robust
            // against structural-peel edge clamping: MissionComposition clamps a
            // peel UT into [runStart, runEnd], so a leg's StartUT can drift; the
            // branch-point child ids are authoritative for "this leg came off at
            // undock". A leg's interval is post-undock if it (or its synthetic seg
            // children, keyed as "<headLegId>/segN") roots at such a leg.
            var undockChildLegIds = CollectUndockChildLegIds(tree);

            // --- Walk every selectable interval; exclude post-undock ones. ---
            int scanned = 0;
            int excludedCount = 0;
            int keptCount = 0;
            double boundary = undockUT - BoundaryEpsilonSeconds;
            for (int i = 0; i < roots.Count; i++)
            {
                WalkAndClassify(roots[i], boundary, undockChildLegIds, excluded,
                    ref scanned, ref excludedCount, ref keptCount);
            }

            ParsekLog.Verbose(Tag,
                $"ComputeExcludedIntervalKeys: tree={tree.Id ?? "<null>"} " +
                $"undockUT={undockUT.ToString("R", ic)} launchUT={launchUT.ToString("R", ic)} " +
                $"scanned={scanned.ToString(ic)} excluded={excludedCount.ToString(ic)} " +
                $"kept={keptCount.ToString(ic)} undockChildren={undockChildLegIds.Count.ToString(ic)}");
            return excluded;
        }

        // Recursively classifies each selectable composition node. An interval is
        // excluded (post-undock) when its start straddles to/after the undock
        // boundary OR it roots at a leg that separated at an undock branch point.
        private static void WalkAndClassify(
            MissionCompositionNode node, double boundary, HashSet<string> undockChildLegIds,
            HashSet<string> excluded, ref int scanned, ref int excludedCount, ref int keptCount)
        {
            if (node == null)
                return;

            if (node.IsSelectable && !string.IsNullOrEmpty(node.HeadLegId)
                && !string.IsNullOrEmpty(node.OwnerHeadId))
            {
                scanned++;
                bool postUndock = node.StartUT >= boundary
                    || RootsAtUndockChild(node.HeadLegId, undockChildLegIds);
                if (postUndock)
                {
                    excluded.Add(node.HeadLegId);
                    excludedCount++;
                }
                else
                {
                    keptCount++;
                }
            }

            for (int i = 0; i < node.Children.Count; i++)
            {
                WalkAndClassify(node.Children[i], boundary, undockChildLegIds, excluded,
                    ref scanned, ref excludedCount, ref keptCount);
            }
        }

        // A composition node's HeadLegId is either a real recording id or a
        // synthetic "<realHeadLegId>/segN" key (MissionCompositionBuilder.BuildNode
        // mints these for the 2nd+ structural interval of one vessel). Both root at
        // the real leg before the first "/seg" marker, so test the prefix.
        private static bool RootsAtUndockChild(string headLegId, HashSet<string> undockChildLegIds)
        {
            if (undockChildLegIds.Count == 0)
                return false;
            if (undockChildLegIds.Contains(headLegId))
                return true;
            int segMarker = headLegId.IndexOf("/seg", StringComparison.Ordinal);
            if (segMarker > 0)
            {
                string realLeg = headLegId.Substring(0, segMarker);
                return undockChildLegIds.Contains(realLeg);
            }
            return false;
        }

        // Every child recording id of every Undock branch point. These are the legs
        // that separated at an undock — both the survivor continuation and the
        // undocked offshoot list here, so both end up post-undock-excluded.
        private static HashSet<string> CollectUndockChildLegIds(RecordingTree tree)
        {
            var ids = new HashSet<string>();
            if (tree.BranchPoints == null)
                return ids;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                BranchPoint bp = tree.BranchPoints[i];
                if (bp == null || bp.Type != BranchPointType.Undock || bp.ChildRecordingIds == null)
                    continue;
                for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                {
                    string cid = bp.ChildRecordingIds[c];
                    if (!string.IsNullOrEmpty(cid))
                        ids.Add(cid);
                }
            }
            return ids;
        }

        /// <summary>
        /// Builds the on-demand, route-owned <see cref="Mission"/> for
        /// <paramref name="route"/>. The Mission is handed to
        /// <c>MissionLoopUnitBuilder.Build</c> as a one-element list and is NEVER
        /// inserted into <c>MissionStore</c> (which would prune / normalize it by
        /// tree and surface it as a player mission).
        /// </summary>
        /// <remarks>
        /// Copies the excluded interval keys into <see cref="Mission.ExcludedIntervalKeys"/>
        /// ONLY — <see cref="Mission.ExcludedThroughLineHeadIds"/> stays empty (that
        /// coarse field drops a whole vessel). Sets <c>LoopPlayback=true</c>,
        /// <c>LoopIntervalSeconds = route.DispatchInterval</c> (render cadence),
        /// <c>LoopTimeUnit = Sec</c>, and <c>LoopAnchorUT = route.LoopAnchorUT</c>
        /// directly on the route-owned object — never via
        /// <c>MissionStore.SetLoopEnabled</c>, which mutates store siblings. The
        /// loop builder floors the anchor to <c>spanEndUT</c>, so the route does not
        /// own render phase. The stable derived id
        /// (<c>"&lt;routeId&gt;-backing"</c>) keeps logs greppable and feeds
        /// <c>BuildSignature</c> cache invalidation.
        /// </remarks>
        internal static Mission BuildMission(Route route, double currentUT)
        {
            if (route == null)
            {
                ParsekLog.Verbose(Tag, "BuildMission: route=<null> -> null");
                return null;
            }

            var ic = CultureInfo.InvariantCulture;
            string missionId = (route.Id ?? "<no-id>") + "-backing";
            var mission = new Mission(missionId, route.BackingMissionTreeId, route.Name);

            int copied = 0;
            if (route.ExcludedIntervalKeys != null)
            {
                foreach (string key in route.ExcludedIntervalKeys)
                {
                    if (!string.IsNullOrEmpty(key))
                    {
                        mission.ExcludedIntervalKeys.Add(key);
                        copied++;
                    }
                }
            }

            mission.LoopPlayback = true;
            mission.LoopIntervalSeconds = route.DispatchInterval;
            mission.LoopTimeUnit = LoopTimeUnit.Sec;
            mission.LoopAnchorUT = route.LoopAnchorUT;

            ParsekLog.Verbose(Tag,
                $"BuildMission: id={missionId} tree={route.BackingMissionTreeId ?? "<null>"} " +
                $"excludedKeys={copied.ToString(ic)} " +
                $"loopInterval={route.DispatchInterval.ToString("R", ic)} " +
                $"loopAnchorUT={route.LoopAnchorUT.ToString("R", ic)} " +
                $"currentUT={currentUT.ToString("R", ic)} (render phase owned by loop clock, anchor floored to spanEnd)");
            return mission;
        }
    }
}
