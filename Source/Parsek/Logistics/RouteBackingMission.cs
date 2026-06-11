using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Logistics
{
    /// <summary>
    /// The ONE backing-mission helper for Supply Routes (design §0; plan Phase 0
    /// task 4 / Phase 1). A v0 route renders as a looped Mission segment over its
    /// source tree's <c>[launch .. dock]</c> path: rendering STOPS at the docking
    /// moment (playtest follow-up), so the docked-together combined vessel (the
    /// dock-merged child, which spans dock..undock) is NOT rendered. This helper
    /// owns the route-side derivations:
    /// <list type="number">
    ///   <item><see cref="ComputeExcludedIntervalKeys"/> — which composition
    ///   intervals to drop so the rendered window end-trims to
    ///   <c>[launch .. dock]</c> (the docked combined-vessel tail AND the
    ///   post-undock survivor / payload tail are excluded).</item>
    ///   <item><see cref="ComputeAutoExcludedNewIntervalKeys"/> (M-MIS-9): which
    ///   intervals root at a recording UNKNOWN at route creation (a post-creation
    ///   branch) and are auto-excluded so the backing selection stays frozen to
    ///   the creation-time member set.</item>
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
    /// from the tree ROOT launch (<c>launchUT</c>, the root recording's StartUT),
    /// NOT from the mid-flight merged dock child. The END is the recorded DOCK UT
    /// (v0 stops rendering at the docking moment).
    /// </para>
    /// <para>
    /// This helper intentionally SKIPS <c>MissionThroughLineBuilder.Build</c>:
    /// verified safe because <c>MissionCompositionBuilder.Build</c> consumes only
    /// the <c>MissionStructure</c> (through-line construction is pure /
    /// non-mutating, so omitting it does not alter the composition the loop
    /// builder later derives from the same structure). The composition walk gives
    /// us everything the route derivation needs (selectable interval keys + their
    /// underlying recording ids).
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
        /// whose start is at-or-after <c>segmentEndUT - Epsilon</c> is treated as
        /// past the segment end and excluded (for v0 this catches the dock-merged
        /// combined vessel, whose start IS the dock = segment end); an interval that
        /// merely ENDS at the segment end is kept (its start is strictly before).
        /// Tiny relative to any real flight timeline so it only resolves
        /// exact-boundary ties.
        /// </summary>
        internal const double BoundaryEpsilonSeconds = 1e-6;

        /// <summary>
        /// Derives the set of composition-interval keys
        /// (<c>MissionCompositionNode.HeadLegId</c>) to EXCLUDE so the backing
        /// mission renders only <c>[launchUT .. segmentEndUT]</c>. Pure.
        /// </summary>
        /// <param name="tree">Source recording tree (read-only).</param>
        /// <param name="segmentEndUT">End of the route segment (v0: the recorded DOCK UT, so the docked combined-vessel tail is excluded).</param>
        /// <param name="launchUT">Start of the route segment (tree ROOT launch UT).</param>
        /// <returns>
        /// Excluded interval keys. Empty set on any guard failure (NaN inputs,
        /// <c>segmentEndUT &lt;= launchUT</c>, or no composition roots) — the whole
        /// segment renders, an honest fallback.
        /// </returns>
        internal static HashSet<string> ComputeExcludedIntervalKeys(
            RecordingTree tree, double segmentEndUT, double launchUT)
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
            if (double.IsNaN(segmentEndUT) || double.IsNaN(launchUT))
            {
                ParsekLog.Verbose(Tag,
                    $"ComputeExcludedIntervalKeys: NaN window tree={tree.Id ?? "<null>"} " +
                    $"segmentEndUT={segmentEndUT.ToString("R", ic)} launchUT={launchUT.ToString("R", ic)} " +
                    "-> empty (whole segment renders)");
                return excluded;
            }
            if (segmentEndUT <= launchUT)
            {
                ParsekLog.Verbose(Tag,
                    $"ComputeExcludedIntervalKeys: segmentEndUT<=launchUT tree={tree.Id ?? "<null>"} " +
                    $"segmentEndUT={segmentEndUT.ToString("R", ic)} launchUT={launchUT.ToString("R", ic)} " +
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

            // Collect the recording ids that separated at the route's TERMINAL
            // Undock branch point (the one at/nearest segmentEndUT). Keying the trim on
            // these (rather than a pure StartUT scan) is robust against
            // structural-peel edge clamping: MissionComposition clamps a peel UT
            // into [runStart, runEnd], so a leg's StartUT can drift; the
            // branch-point child ids are authoritative for "this leg came off at
            // undock". A leg's interval is post-undock if it (or its synthetic seg
            // children, keyed as "<headLegId>/segN") roots at such a leg.
            //
            // Scoped to the terminal undock (Phase 1 review carry-forward): an
            // earlier in-flight undock (a mid-mission separation BEFORE the route's
            // dock cycle) must NOT trim the survivor that continues on to the dock.
            // We pick the Undock branch point whose UT is closest to segmentEndUT, and
            // additionally AND the branch-child test with the StartUT>=boundary
            // guard in WalkAndClassify so a child that legitimately starts before
            // the boundary is never dropped on branch-id alone.
            var undockChildLegIds = CollectTerminalUndockChildLegIds(tree, segmentEndUT);

            // --- Walk every selectable interval; exclude post-undock ones. ---
            int scanned = 0;
            int excludedCount = 0;
            int keptCount = 0;
            double boundary = segmentEndUT - BoundaryEpsilonSeconds;
            for (int i = 0; i < roots.Count; i++)
            {
                WalkAndClassify(roots[i], boundary, undockChildLegIds, excluded,
                    ref scanned, ref excludedCount, ref keptCount);
            }

            ParsekLog.Verbose(Tag,
                $"ComputeExcludedIntervalKeys: tree={tree.Id ?? "<null>"} " +
                $"segmentEndUT={segmentEndUT.ToString("R", ic)} launchUT={launchUT.ToString("R", ic)} " +
                $"scanned={scanned.ToString(ic)} excluded={excludedCount.ToString(ic)} " +
                $"kept={keptCount.ToString(ic)} undockChildren={undockChildLegIds.Count.ToString(ic)}");
            return excluded;
        }

        /// <summary>
        /// (must-fix #3) Derives the set of underlying recording ids in the route's
        /// rendered <c>[launchUT .. segmentEndUT]</c> member window — every recording
        /// that backs a KEPT (non-excluded) selectable composition interval. On a
        /// multi-recording flight this is MORE than the single dock-child leaf, so
        /// the route widens <c>RecordingIds</c> / <c>SourceRefs</c> to cover the
        /// whole rendered path (one <c>RouteSourceRef</c> per member) and
        /// <c>RouteStore.RevalidateSources</c> tracks the whole path, not just the
        /// leaf. Pure; uses the SAME composition walk as
        /// <see cref="ComputeExcludedIntervalKeys"/>.
        /// </summary>
        /// <param name="tree">Source recording tree (read-only).</param>
        /// <param name="segmentEndUT">End of the route segment (v0: the recorded DOCK UT, so the docked combined-vessel tail is excluded).</param>
        /// <param name="launchUT">Start of the route segment (tree ROOT launch UT).</param>
        /// <returns>
        /// Member recording ids for the kept intervals. On any guard failure (the
        /// same guards as <see cref="ComputeExcludedIntervalKeys"/>) returns the
        /// tree root recording id alone (honest minimal fallback) when resolvable,
        /// else an empty set — the caller falls back to the leaf in that case.
        /// </returns>
        internal static HashSet<string> ComputeMemberRecordingIds(
            RecordingTree tree, double segmentEndUT, double launchUT)
        {
            var members = new HashSet<string>();
            var ic = CultureInfo.InvariantCulture;

            if (tree == null)
            {
                ParsekLog.Verbose(Tag,
                    "ComputeMemberRecordingIds: tree=<null> -> empty");
                return members;
            }

            // Mirror the guards in ComputeExcludedIntervalKeys: a malformed window
            // renders the whole segment, so the member set is "every recording id".
            bool malformed = double.IsNaN(segmentEndUT) || double.IsNaN(launchUT)
                || segmentEndUT <= launchUT;

            MissionStructure structure = MissionStructureBuilder.Build(tree);
            List<MissionCompositionNode> roots = MissionCompositionBuilder.Build(structure);
            if (roots == null || roots.Count == 0)
            {
                // No composition (single-leg / unstructured tree). Fall back to the
                // root recording id, or every recording id when even that is unset.
                AddRootOrAllRecordingIds(tree, members);
                ParsekLog.Verbose(Tag,
                    $"ComputeMemberRecordingIds: no composition roots tree={tree.Id ?? "<null>"} " +
                    $"-> members={members.Count.ToString(ic)} (fallback)");
                return members;
            }

            if (malformed)
            {
                // Whole-segment render: every selectable interval's recording id.
                int allCount = 0;
                for (int i = 0; i < roots.Count; i++)
                    CollectMemberRecordingIds(roots[i], collectAll: true,
                        undockChildLegIds: null, boundary: 0.0, members: members, ref allCount);
                if (members.Count == 0)
                    AddRootOrAllRecordingIds(tree, members);
                ParsekLog.Verbose(Tag,
                    $"ComputeMemberRecordingIds: malformed window tree={tree.Id ?? "<null>"} " +
                    $"segmentEndUT={segmentEndUT.ToString("R", ic)} launchUT={launchUT.ToString("R", ic)} " +
                    $"-> members={members.Count.ToString(ic)} (whole segment)");
                return members;
            }

            var undockChildLegIds = CollectTerminalUndockChildLegIds(tree, segmentEndUT);
            double boundary = segmentEndUT - BoundaryEpsilonSeconds;
            int kept = 0;
            for (int i = 0; i < roots.Count; i++)
                CollectMemberRecordingIds(roots[i], collectAll: false,
                    undockChildLegIds: undockChildLegIds, boundary: boundary,
                    members: members, ref kept);

            if (members.Count == 0)
                AddRootOrAllRecordingIds(tree, members);

            ParsekLog.Verbose(Tag,
                $"ComputeMemberRecordingIds: tree={tree.Id ?? "<null>"} " +
                $"segmentEndUT={segmentEndUT.ToString("R", ic)} launchUT={launchUT.ToString("R", ic)} " +
                $"keptIntervals={kept.ToString(ic)} members={members.Count.ToString(ic)}");
            return members;
        }

        // Recursively collects the underlying recording id of each KEPT selectable
        // node (or every selectable node when collectAll). The HeadLegId resolves
        // to a real recording id via the "/seg" prefix strip.
        private static void CollectMemberRecordingIds(
            MissionCompositionNode node, bool collectAll, HashSet<string> undockChildLegIds,
            double boundary, HashSet<string> members, ref int counter)
        {
            if (node == null)
                return;

            if (node.IsSelectable && !string.IsNullOrEmpty(node.HeadLegId)
                && !string.IsNullOrEmpty(node.OwnerHeadId))
            {
                bool postUndock = !collectAll &&
                    (node.StartUT >= boundary
                     || RootsAtUndockChild(node.HeadLegId, undockChildLegIds));
                if (collectAll || !postUndock)
                {
                    string recId = StripSegMarker(node.HeadLegId);
                    if (!string.IsNullOrEmpty(recId) && members.Add(recId))
                        counter++;
                }
            }

            for (int i = 0; i < node.Children.Count; i++)
                CollectMemberRecordingIds(node.Children[i], collectAll, undockChildLegIds,
                    boundary, members, ref counter);
        }

        // The bare recording id behind a composition HeadLegId: strip the synthetic
        // "<realLegId>/segN" suffix MissionCompositionBuilder mints for the 2nd+
        // structural interval of one vessel.
        private static string StripSegMarker(string headLegId)
        {
            if (string.IsNullOrEmpty(headLegId))
                return headLegId;
            int segMarker = headLegId.IndexOf("/seg", StringComparison.Ordinal);
            return segMarker > 0 ? headLegId.Substring(0, segMarker) : headLegId;
        }

        // Fallback member set: the root recording id when resolvable, else every
        // recording id in the tree (honest "we cannot trim, render everything").
        private static void AddRootOrAllRecordingIds(RecordingTree tree, HashSet<string> members)
        {
            if (tree?.Recordings == null)
                return;
            if (!string.IsNullOrEmpty(tree.RootRecordingId)
                && tree.Recordings.ContainsKey(tree.RootRecordingId))
            {
                members.Add(tree.RootRecordingId);
                return;
            }
            foreach (string id in tree.Recordings.Keys)
                if (!string.IsNullOrEmpty(id))
                    members.Add(id);
        }

        /// <summary>
        /// (M-MIS-9) Derives the set of composition-interval keys whose BASE
        /// recording id was NOT known at route creation: post-creation branches
        /// (a re-fly fork or a switch-fly continuation landing on the backing
        /// tree outside the member path) that would otherwise silently join the
        /// synthesized backing mission and extend the rendered loop + delivery
        /// span. Pure. Known-at-creation base ids are
        /// <c>route.SourceRefs[].RecordingId</c> UNION the base leg ids of
        /// <c>route.ExcludedIntervalKeys</c> (the synthetic <c>"/segN"</c>
        /// marker stripped via <see cref="StripSegMarker"/>, the same way
        /// <c>MissionCompositionBuilder</c> encodes it). The base-id rule keeps
        /// a NEW <c>"/segN"</c> re-peel of a known member recording included:
        /// only keys rooting at an unknown recording are returned.
        /// </summary>
        /// <param name="tree">Current backing tree (read-only).</param>
        /// <param name="route">The route whose creation-time member set anchors the freeze.</param>
        /// <returns>
        /// Interval keys to auto-exclude. Empty on any guard failure (null tree /
        /// route, no composition roots) and ALWAYS empty when
        /// <c>route.ExcludedIntervalKeys</c> is empty: an empty creation-time
        /// excluded set is the honest whole-segment-fallback contract (creation
        /// could not trim, the whole segment renders) and freezing on top of it
        /// would guess at a member set the route never recorded.
        /// </returns>
        internal static HashSet<string> ComputeAutoExcludedNewIntervalKeys(
            RecordingTree tree, Route route)
        {
            var autoExcluded = new HashSet<string>();
            var ic = CultureInfo.InvariantCulture;

            if (tree == null || route == null)
            {
                ParsekLog.Verbose(Tag,
                    "ComputeAutoExcludedNewIntervalKeys: " +
                    $"tree={(tree == null ? "<null>" : tree.Id ?? "<null>")} " +
                    $"route={(route == null ? "<null>" : route.Id ?? "<no-id>")} " +
                    "-> empty (no derivation)");
                return autoExcluded;
            }
            if (route.ExcludedIntervalKeys == null || route.ExcludedIntervalKeys.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"ComputeAutoExcludedNewIntervalKeys: route={route.Id ?? "<no-id>"} " +
                    $"tree={tree.Id ?? "<null>"} excludedIntervalKeys=empty " +
                    "-> empty (honest whole-segment fallback preserved)");
                return autoExcluded;
            }

            // Known-at-creation base recording ids: every SourceRef member UNION
            // the base leg ids of the creation-time excluded keys.
            var knownBaseIds = new HashSet<string>();
            if (route.SourceRefs != null)
            {
                for (int i = 0; i < route.SourceRefs.Count; i++)
                {
                    string srefId = route.SourceRefs[i]?.RecordingId;
                    if (!string.IsNullOrEmpty(srefId))
                        knownBaseIds.Add(srefId);
                }
            }
            foreach (string key in route.ExcludedIntervalKeys)
            {
                string baseId = StripSegMarker(key);
                if (!string.IsNullOrEmpty(baseId))
                    knownBaseIds.Add(baseId);
            }

            // --- Build the composition pipeline (read-only Missions seam). ---
            MissionStructure structure = MissionStructureBuilder.Build(tree);
            List<MissionCompositionNode> roots = MissionCompositionBuilder.Build(structure);
            if (roots == null || roots.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"ComputeAutoExcludedNewIntervalKeys: no composition roots tree={tree.Id ?? "<null>"} " +
                    "-> empty (no derivation)");
                return autoExcluded;
            }

            int scanned = 0;
            int knownCount = 0;
            int autoExcludedCount = 0;
            for (int i = 0; i < roots.Count; i++)
            {
                WalkAndClassifyNewKeys(roots[i], knownBaseIds, autoExcluded,
                    ref scanned, ref knownCount, ref autoExcludedCount);
            }

            ParsekLog.Verbose(Tag,
                $"ComputeAutoExcludedNewIntervalKeys: route={route.Id ?? "<no-id>"} " +
                $"tree={tree.Id ?? "<null>"} scanned={scanned.ToString(ic)} " +
                $"known={knownCount.ToString(ic)} autoExcluded={autoExcludedCount.ToString(ic)} " +
                $"knownBases={knownBaseIds.Count.ToString(ic)}");
            return autoExcluded;
        }

        // Recursively classifies each selectable composition node against the
        // known-at-creation base-id set. A key whose base recording id is known
        // is NEVER auto-excluded (the base-id rule); only keys rooting at a
        // recording unknown at creation are collected.
        private static void WalkAndClassifyNewKeys(
            MissionCompositionNode node, HashSet<string> knownBaseIds, HashSet<string> autoExcluded,
            ref int scanned, ref int knownCount, ref int autoExcludedCount)
        {
            if (node == null)
                return;

            if (node.IsSelectable && !string.IsNullOrEmpty(node.HeadLegId)
                && !string.IsNullOrEmpty(node.OwnerHeadId))
            {
                scanned++;
                string baseId = StripSegMarker(node.HeadLegId);
                if (!string.IsNullOrEmpty(baseId) && knownBaseIds.Contains(baseId))
                {
                    knownCount++;
                }
                else
                {
                    autoExcluded.Add(node.HeadLegId);
                    autoExcludedCount++;
                }
            }

            for (int i = 0; i < node.Children.Count; i++)
            {
                WalkAndClassifyNewKeys(node.Children[i], knownBaseIds, autoExcluded,
                    ref scanned, ref knownCount, ref autoExcludedCount);
            }
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

        // The child recording ids of the route's TERMINAL Undock branch point: the
        // Undock branch point whose UT is at/nearest <paramref name="segmentEndUT"/>.
        // These are the legs that separated at the route's undock — both the
        // survivor continuation and the undocked offshoot — so both end up
        // post-undock-excluded.
        //
        // Scoping to the single terminal undock (Phase 1 review carry-forward)
        // prevents an EARLIER in-flight undock (a mid-mission separation before the
        // route's dock cycle) from wrongly trimming a survivor that continues on to
        // the dock. The terminal undock's children start at/after segmentEndUT by
        // construction, so the StartUT>=boundary OR in WalkAndClassify still catches
        // them even when MissionComposition clamps a child's StartUT slightly below
        // the boundary.
        private static HashSet<string> CollectTerminalUndockChildLegIds(
            RecordingTree tree, double segmentEndUT)
        {
            var ids = new HashSet<string>();
            if (tree.BranchPoints == null)
                return ids;

            // Pick the Undock branch point closest to segmentEndUT. Ties (exact-UT
            // duplicates, which a well-formed tree should not have) keep the first.
            BranchPoint terminal = null;
            double bestDelta = double.PositiveInfinity;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                BranchPoint bp = tree.BranchPoints[i];
                if (bp == null || bp.Type != BranchPointType.Undock || bp.ChildRecordingIds == null)
                    continue;
                double delta = Math.Abs(bp.UT - segmentEndUT);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    terminal = bp;
                }
            }

            if (terminal == null)
                return ids;

            for (int c = 0; c < terminal.ChildRecordingIds.Count; c++)
            {
                string cid = terminal.ChildRecordingIds[c];
                if (!string.IsNullOrEmpty(cid))
                    ids.Add(cid);
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
        /// coarse field drops a whole vessel), and unions in the (M-MIS-9)
        /// auto-excluded post-creation branch keys
        /// (<see cref="ComputeAutoExcludedNewIntervalKeys"/>, signature-gated and
        /// cached on the route) so a branch landing on the tree after creation
        /// never extends the rendered loop or the delivery span. Sets <c>LoopPlayback=true</c>,
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

            // (M-MIS-9) Freeze the backing selection to creation-time members:
            // union the auto-excluded post-creation branch keys (signature-gated
            // derivation, cached on the route) into the synthesized mission so a
            // re-fly fork / switch-fly continuation landing outside the member
            // path never extends the rendered loop or the delivery span.
            int autoExcludedCount = 0;
            HashSet<string> autoExcluded = ResolveAutoExcludedNewIntervalKeys(route);
            if (autoExcluded != null)
            {
                foreach (string key in autoExcluded)
                {
                    if (!string.IsNullOrEmpty(key) && mission.ExcludedIntervalKeys.Add(key))
                        autoExcludedCount++;
                }
            }

            mission.LoopPlayback = true;
            mission.LoopIntervalSeconds = route.DispatchInterval;
            mission.LoopTimeUnit = LoopTimeUnit.Sec;
            mission.LoopAnchorUT = route.LoopAnchorUT;

            // Rate-limited (per route): BuildMission is called UNCONDITIONALLY every
            // render frame by the ghost-driving selector (and every delivery-clock tick /
            // Logistics-window frame), so a plain Verbose here floods the log (~21k lines
            // in one playtest). The output is static per route, so a per-route, 5s
            // real-time key is enough to keep one build observable without the storm.
            ParsekLog.VerboseRateLimited(Tag, "build-mission-" + missionId,
                $"BuildMission: id={missionId} tree={route.BackingMissionTreeId ?? "<null>"} " +
                $"excludedKeys={copied.ToString(ic)} " +
                $"autoExcludedKeys={autoExcludedCount.ToString(ic)} " +
                $"loopInterval={route.DispatchInterval.ToString("R", ic)} " +
                $"loopAnchorUT={route.LoopAnchorUT.ToString("R", ic)} " +
                $"currentUT={currentUT.ToString("R", ic)} (render phase owned by loop clock, anchor floored to spanEnd)",
                5.0);
            return mission;
        }

        /// <summary>
        /// (M-MIS-9) Resolves the route's auto-excluded new-interval-key set,
        /// re-deriving only when the backing tree's topology signature changed
        /// (a post-creation branch always moves the branch-point or recording
        /// count) and caching the result on the route. Unchanged signature
        /// returns the cached set with NO derivation and NO log, so routes whose
        /// tree has not gained branches are byte-identical in behavior. Logs
        /// Info once when a re-derivation produces a different, non-empty set.
        /// </summary>
        private static HashSet<string> ResolveAutoExcludedNewIntervalKeys(Route route)
        {
            if (string.IsNullOrEmpty(route.BackingMissionTreeId))
                return route.AutoExcludedNewIntervalKeys; // no backing tree to derive against

            RecordingTree tree = RouteTreeGuard.FindCommittedTree(route.BackingMissionTreeId);
            string signature = ComputeTopologySignature(tree);
            if (string.Equals(signature, route.AutoExcludeTopologySignature, StringComparison.Ordinal))
                return route.AutoExcludedNewIntervalKeys;

            HashSet<string> derived = ComputeAutoExcludedNewIntervalKeys(tree, route);
            bool setChanged = route.AutoExcludedNewIntervalKeys == null
                || !route.AutoExcludedNewIntervalKeys.SetEquals(derived);
            route.AutoExcludedNewIntervalKeys = derived;
            route.AutoExcludeTopologySignature = signature;

            if (derived.Count > 0 && setChanged)
            {
                ParsekLog.Info(Tag,
                    $"BuildMission: route={route.Id ?? "<no-id>"} auto-excluded " +
                    $"{derived.Count.ToString(CultureInfo.InvariantCulture)} new interval key(s); " +
                    $"a branch joined tree {route.BackingMissionTreeId} after route creation " +
                    "(backing selection frozen to creation-time members)");
            }
            return derived;
        }

        // Cheap topology signature over the backing tree, mirroring the per-tree
        // fold in MissionLoopUnitBuilder.BuildSignature: a post-creation branch
        // (re-fly fork / switch-fly continuation) always moves at least one of
        // the two counts. "<no-tree>" when the tree is not committed (yet), so
        // the cache re-derives once when it appears.
        private static string ComputeTopologySignature(RecordingTree tree)
        {
            if (tree == null)
                return "<no-tree>";
            var ic = CultureInfo.InvariantCulture;
            return (tree.BranchPoints?.Count ?? 0).ToString(ic) + "/"
                 + (tree.Recordings?.Count ?? 0).ToString(ic);
        }
    }
}
