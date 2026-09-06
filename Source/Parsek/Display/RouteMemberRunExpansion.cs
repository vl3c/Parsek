using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;

namespace Parsek.Display
{
    /// <summary>
    /// A ROUTE MEMBER IS A RUN, NOT A RECORDING. <see cref="Route.RecordingIds"/> is
    /// <c>RouteBackingMission.ComputeMemberRecordingIds</c>, which walks the backing tree's
    /// composition and keys every interval of one physical vessel's through-line under that run's
    /// HEAD leg id (<c>MissionCompositionBuilder</c> walks the run via
    /// <see cref="MissionThroughLineBuilder.ContinuationSuccessor"/>; every
    /// <c>headLegId + "/segN"</c> key strips back to the head). The route render then resolved each
    /// member id to ONE <see cref="Recording"/>, so every chain-CONTINUATION segment of a member run
    /// was invisible to the route line - and an interplanetary transfer always lands in one
    /// (ROUTE-LINE-MEMBER-DROPS-CONTINUATION-SEGMENTS). This module closes that gap: it expands a
    /// member id to the ordered recordings of its whole continuation run, so the renderer draws the
    /// path the member set already claims to cover.
    ///
    /// <para>THE RUN WALK IS NOT RE-IMPLEMENTED HERE. <see cref="MissionThroughLineBuilder.Build"/>
    /// already produces, for every run in a tree, the ordered
    /// <see cref="MissionThroughLine.MemberLegIds"/> its <c>ContinuationSuccessor</c> walk yields -
    /// the SAME walk <c>MissionCompositionBuilder</c> uses to key intervals under the head. The
    /// expansion finds the run containing the member id and takes the suffix from that member
    /// onward.
    ///
    /// <para>ONE PLACE THE BUILDER ALONE IS NOT ENOUGH, and the claim pass that closes it: a Dock
    /// branch point hands its single merged child to BOTH parents, and the builder has no shared
    /// visited set, so that child appears in TWO through-lines while composition keys its intervals
    /// under exactly ONE of them. <see cref="RunClaimIndex"/> re-applies composition's shared-visited,
    /// first-head-wins rule over the view before any suffix is taken, so the renderer and the
    /// member-set producer cannot disagree about what a run is.</para>
    /// </para>
    ///
    /// <para>THREE FILTERS BOUND THE WALK, each mirroring an authority that already exists:
    /// <list type="number">
    /// <item>VISIBILITY - a segment is kept only when it resolves through the caller's
    /// visible-recording resolver (live: the ERS index, so a superseded or rewind-retired segment
    /// is never walked into). The walk STOPS at the first hidden segment rather than skipping it:
    /// past that point the vessel's visible through-line has ended.</item>
    /// <item>KNOWN AT CREATION - <see cref="Route.CreationTreeRecordingIds"/>, the creation-time
    /// known-recording union. A post-creation branch (a re-fly fork, a switch-fly continuation)
    /// mints ids outside it, and the backing mission auto-excludes exactly those
    /// (<c>RouteBackingMission.ComputeAutoExcludedNewIntervalKeys</c> prong 1). Empty set = no
    /// snapshot: FAIL OPEN, the same contract <c>RouteRunCostCalculator</c> gives it.</item>
    /// <item>EXCLUDED INTERVALS - a <see cref="Route.ExcludedIntervalKeys"/> entry naming a WHOLE
    /// recording (no <c>/seg</c> or <c>@dock</c> marker) is a leg the route deliberately does not
    /// render. A key naming a SUB-interval of a member (<c>&lt;id&gt;/seg3</c>) is NOT a
    /// whole-recording exclusion and is ignored here, exactly as it is today: the renderer works at
    /// recording granularity and already draws such a member whole.</item>
    /// </list>
    /// The declared member (the head) itself is never filtered - it is the shipped member set - so
    /// the expansion can only ADD geometry, never take a member away.
    /// </para>
    ///
    /// <para>The dock clip (<c>RouteTrajectoryLineRenderer.LegWithinDockClip</c>) still runs per LEG
    /// afterwards, so an expanded post-dock segment contributes nothing, and each segment is built
    /// by <c>GhostTrajectoryPolylineRenderer.BuildLegsForRecording</c> against its OWN recording -
    /// which is what keeps the RELATIVE-frame contract intact (a Relative section resolves through
    /// its own recording's <c>bodyFixedFrames</c> exactly as before).</para>
    /// </summary>
    internal static class RouteMemberRunExpansion
    {
        private const string Tag = "RouteLine";

        // The ERS list REFERENCE doubles as the cache version: EffectiveState caches it on the
        // store version, the supersede version and the active re-fly marker identity, so a
        // different reference means one of those moved and every index below is stale. DrawAll runs
        // on the map onPreCull hook, so without this the through-line view would be rebuilt for
        // every route on every frame.
        private static IReadOnlyList<Recording> cachedErs;
        private static Dictionary<string, Recording> visibleById;
        private static readonly Dictionary<string, RunClaimIndex> claimsByTreeId =
            new Dictionary<string, RunClaimIndex>(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<Recording>> visibleRunByMemberId =
            new Dictionary<string, List<Recording>>(StringComparer.Ordinal);

        internal static int ViewBuildCountForTesting;

        /// <summary>
        /// One leg id belongs to exactly ONE run, decided the way
        /// <c>MissionComposition.BuildNode</c> decides it: a shared visited set, first head wins, and
        /// a run that reaches an already-claimed leg STOPS there (it does not skip past it).
        ///
        /// <para>WHY THE EXPANDER NEEDS ITS OWN CLAIM PASS. The module contract above is that the
        /// renderer and the member-set producer cannot disagree about what a run is, and
        /// <see cref="MissionThroughLineBuilder.Build"/> alone does not deliver that: at a Dock branch
        /// point <c>MissionStructure.BuildBranchLinks</c> gives the single merged child to BOTH
        /// parents' <c>BranchChildIds</c> and marks it <c>IsBranchContinuation</c>, so
        /// <c>ContinuationSuccessor</c> returns it for both parents and the builder - which has no
        /// shared visited set - lists it in TWO through-lines' <c>MemberLegIds</c>.
        /// <c>MissionCompositionBuilder</c>, the producer of <see cref="Route.RecordingIds"/>, keys the
        /// merged run's intervals under exactly one of them (the first its DFS reaches). Without this
        /// pass the expander could walk the merged run from the OTHER head, i.e. from a member the
        /// route's own member set does not own it under - reachable on any N-stop route whose
        /// intermediate dock's merged run falls inside the clip.</para>
        ///
        /// <para>ORDER: roots in <c>RootHeadIds</c> order, depth-first into <c>OffshootHeadIds</c>,
        /// which is composition's own traversal (roots in <c>RootLegIds</c> order, recursing into each
        /// run's peels) under the same (StartUT, ordinal id) sort both lists are built with. Heads a
        /// root cannot reach are claimed last in that same sort, so the pass is total and
        /// deterministic on any tree shape. The one residual difference from composition is the order
        /// of SIBLING offshoots of one run (composition orders peels by their parent leg's position in
        /// the run then by child sort; this orders them chronologically), which can only re-decide
        /// which of two sibling runs claims a leg they both merge into - deterministic either way.</para>
        /// </summary>
        internal sealed class RunClaimIndex
        {
            /// <summary>Head id -&gt; the ordered leg ids that head CLAIMED (its run, truncated at the
            /// first leg another head already claimed).</summary>
            public readonly Dictionary<string, List<string>> RunByHeadId =
                new Dictionary<string, List<string>>(StringComparer.Ordinal);

            /// <summary>Leg id -&gt; the head that claimed it.</summary>
            public readonly Dictionary<string, string> HeadByMemberId =
                new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>Legs a later head's walk reached and had to stop at (the merge count).</summary>
            public int ContestedMembers;
        }

        /// <summary>Counters for one member's expansion.</summary>
        internal struct RunTally
        {
            /// <summary>Continuation segments kept AFTER the head, after the route filters.</summary>
            public int Segments;

            /// <summary>Continuation segments the route filters cut off the walked run.</summary>
            public int Dropped;
            /// <summary>Walk stopped because the next segment is not visible (superseded / retired).</summary>
            public int StoppedHidden;
            /// <summary>Walk stopped at a segment unknown at route creation.</summary>
            public int StoppedUnknownAtCreation;
            /// <summary>Walk stopped at a whole-recording excluded interval.</summary>
            public int StoppedExcluded;
        }

        // ------------------------------------------------------------------
        // Pure expansion
        // ------------------------------------------------------------------

        /// <summary>
        /// The ordered VISIBLE continuation run of <paramref name="memberRecordingId"/>, starting at
        /// <paramref name="head"/> (always element 0, never filtered). Pure over the tree + the
        /// caller's resolver; no Unity, no store reads. A null tree (unresolvable owner) or a member
        /// that is not a leg in the tree yields the head alone - the shipped behaviour.
        /// </summary>
        internal static List<Recording> ExpandVisibleRun(
            RecordingTree tree,
            string memberRecordingId,
            Recording head,
            Func<string, Recording> resolveVisible,
            out RunTally tally)
        {
            tally = default(RunTally);
            var run = new List<Recording>(1);
            if (head != null) run.Add(head);
            if (tree == null || string.IsNullOrEmpty(memberRecordingId) || resolveVisible == null)
                return run;

            RunClaimIndex claims = ResolveRunClaims(tree);
            List<string> successors = SuccessorIdsAfter(claims, memberRecordingId);
            if (successors == null || successors.Count == 0)
                return run;

            for (int i = 0; i < successors.Count; i++)
            {
                string id = successors[i];
                if (string.IsNullOrEmpty(id)) break;
                Recording seg = resolveVisible(id);
                if (seg == null)
                {
                    tally.StoppedHidden++;
                    LogStop(memberRecordingId, id, "not-visible");
                    break;
                }
                run.Add(seg);
                tally.Segments++;
            }
            return run;
        }

        /// <summary>
        /// THE route-scoped filter, and the only expression of it: keeps the head unconditionally
        /// and stops the run at the first segment the route did not know at creation
        /// (<paramref name="creationKnownIds"/>, fail-open when empty) or excluded whole
        /// (<paramref name="excludedIntervalKeys"/>). Returns the input list unchanged when nothing
        /// is filtered.
        /// </summary>
        internal static List<Recording> ApplyRouteFilters(
            List<Recording> visibleRun,
            ICollection<string> creationKnownIds,
            ICollection<string> excludedIntervalKeys,
            ref RunTally tally)
        {
            if (visibleRun == null || visibleRun.Count <= 1) return visibleRun;

            bool gateOnCreation = creationKnownIds != null && creationKnownIds.Count > 0;
            int keep = visibleRun.Count;
            for (int i = 1; i < visibleRun.Count; i++)
            {
                string id = visibleRun[i]?.RecordingId;
                string reason = null;
                if (string.IsNullOrEmpty(id))
                {
                    reason = "no-id";
                }
                else if (gateOnCreation && !creationKnownIds.Contains(id))
                {
                    tally.StoppedUnknownAtCreation++;
                    reason = "unknown-at-creation";
                }
                else if (IsWholeRecordingExcluded(excludedIntervalKeys, id))
                {
                    tally.StoppedExcluded++;
                    reason = "excluded-interval";
                }
                if (reason == null) continue;
                LogStop(visibleRun[0]?.RecordingId, id, reason);
                keep = i;
                break;
            }
            if (keep == visibleRun.Count) return visibleRun;

            // Dropped is the filter's OWN counter. Segments is "kept after the head", so it is clamped
            // at 0 rather than decremented blind: on the live path the filter runs against a memoized
            // visible run with a FRESH tally (the walk that produced it ran on an earlier call), so a
            // bare decrement went negative and made the field unreadable.
            int dropped = visibleRun.Count - keep;
            tally.Dropped += dropped;
            tally.Segments = Math.Max(0, tally.Segments - dropped);
            return visibleRun.GetRange(0, keep);
        }

        /// <summary>
        /// The run member ids AFTER <paramref name="memberRecordingId"/> in the run that CLAIMED it
        /// (<see cref="RunClaimIndex"/>), in walk order. Null when no run claims the id (an
        /// unstructured tree, or a debris id that never became a leg). Never crosses into another
        /// head's claim: a claimed run is already truncated at its first contested leg.
        /// </summary>
        internal static List<string> SuccessorIdsAfter(
            RunClaimIndex claims, string memberRecordingId)
        {
            if (claims == null || string.IsNullOrEmpty(memberRecordingId)) return null;
            if (!claims.HeadByMemberId.TryGetValue(memberRecordingId, out string headId)) return null;
            if (!claims.RunByHeadId.TryGetValue(headId, out List<string> run) || run == null)
                return null;
            int at = run.IndexOf(memberRecordingId);
            if (at < 0) return null;
            var rest = new List<string>(run.Count - at - 1);
            for (int i = at + 1; i < run.Count; i++)
                rest.Add(run[i]);
            return rest;
        }

        /// <summary>Convenience overload: claim the view first, then read the suffix.</summary>
        internal static List<string> SuccessorIdsAfter(
            MissionThroughLineView view, string memberRecordingId)
            => SuccessorIdsAfter(BuildRunClaims(view), memberRecordingId);

        /// <summary>
        /// Assigns every through-line leg to exactly one run, composition's way: heads in composition's
        /// traversal order, first head wins, a later head's run truncated at its first already-claimed
        /// leg. Pure. See <see cref="RunClaimIndex"/> for why the expander cannot read
        /// <see cref="MissionThroughLine.MemberLegIds"/> directly.
        /// </summary>
        internal static RunClaimIndex BuildRunClaims(MissionThroughLineView view)
        {
            var index = new RunClaimIndex();
            if (view == null || view.ByHeadId.Count == 0) return index;

            var order = new List<string>(view.ByHeadId.Count);
            var queued = new HashSet<string>(StringComparer.Ordinal);
            if (view.RootHeadIds != null)
            {
                for (int i = 0; i < view.RootHeadIds.Count; i++)
                    QueueHeadDepthFirst(view, view.RootHeadIds[i], order, queued);
            }
            // Heads no root reaches (a view built from a partial structure): claimed last, in the same
            // (StartUT, ordinal id) order every other head list uses, so the pass is total.
            var orphans = new List<string>();
            foreach (string headId in view.ByHeadId.Keys)
                if (!queued.Contains(headId)) orphans.Add(headId);
            orphans.Sort((a, b) => CompareHeadStart(view, a, b));
            for (int i = 0; i < orphans.Count; i++)
                QueueHeadDepthFirst(view, orphans[i], order, queued);

            for (int i = 0; i < order.Count; i++)
            {
                string headId = order[i];
                if (!view.ByHeadId.TryGetValue(headId, out MissionThroughLine line) || line == null)
                    continue;
                var run = new List<string>(line.MemberLegIds != null ? line.MemberLegIds.Count : 0);
                if (line.MemberLegIds != null)
                {
                    for (int m = 0; m < line.MemberLegIds.Count; m++)
                    {
                        string legId = line.MemberLegIds[m];
                        if (string.IsNullOrEmpty(legId)) break;
                        if (index.HeadByMemberId.ContainsKey(legId))
                        {
                            // A merge: the leg belongs to the head that reached it first, and this run
                            // ENDS here (composition's shared-visited rule, not a skip-and-continue).
                            index.ContestedMembers++;
                            break;
                        }
                        index.HeadByMemberId[legId] = headId;
                        run.Add(legId);
                    }
                }
                index.RunByHeadId[headId] = run;
            }
            return index;
        }

        private static void QueueHeadDepthFirst(
            MissionThroughLineView view, string headId, List<string> order, HashSet<string> queued)
        {
            if (string.IsNullOrEmpty(headId) || !view.ByHeadId.ContainsKey(headId)) return;
            if (!queued.Add(headId)) return;
            order.Add(headId);
            if (!view.ByHeadId.TryGetValue(headId, out MissionThroughLine line) || line == null) return;
            for (int i = 0; i < line.OffshootHeadIds.Count; i++)
                QueueHeadDepthFirst(view, line.OffshootHeadIds[i], order, queued);
        }

        private static int CompareHeadStart(MissionThroughLineView view, string a, string b)
        {
            double sa = view.ByHeadId.TryGetValue(a, out MissionThroughLine ta) ? ta.StartUT : 0.0;
            double sb = view.ByHeadId.TryGetValue(b, out MissionThroughLine tb) ? tb.StartUT : 0.0;
            int cmp = sa.CompareTo(sb);
            return cmp != 0 ? cmp : string.CompareOrdinal(a, b);
        }

        /// <summary>
        /// Whether an excluded-interval key set excludes the WHOLE recording
        /// <paramref name="recordingId"/>. Sub-interval keys (<c>&lt;id&gt;/segN</c>,
        /// <c>&lt;id&gt;@dockM</c>) name part of a recording the renderer cannot cut, so they are
        /// not whole-recording exclusions - the shipped render draws such a member whole and this
        /// walk must not start dropping it.
        /// </summary>
        internal static bool IsWholeRecordingExcluded(
            ICollection<string> excludedIntervalKeys, string recordingId)
        {
            if (excludedIntervalKeys == null || excludedIntervalKeys.Count == 0) return false;
            if (string.IsNullOrEmpty(recordingId)) return false;
            return excludedIntervalKeys.Contains(recordingId);
        }

        /// <summary>
        /// Pure one-shot entry (visibility walk + route filters), for callers that hold the tree
        /// themselves - the offline / unit-test path.
        /// </summary>
        internal static List<Recording> ExpandRunForRoute(
            RecordingTree tree,
            string memberRecordingId,
            Recording head,
            Func<string, Recording> resolveVisible,
            ICollection<string> creationKnownIds,
            ICollection<string> excludedIntervalKeys,
            out RunTally tally)
        {
            List<Recording> run = ExpandVisibleRun(
                tree, memberRecordingId, head, resolveVisible, out tally);
            return ApplyRouteFilters(run, creationKnownIds, excludedIntervalKeys, ref tally);
        }

        // ------------------------------------------------------------------
        // Live expander
        // ------------------------------------------------------------------

        /// <summary>
        /// The live member-run resolver for one route: member id -&gt; its ERS-visible continuation
        /// run, route-filtered. The tree-scoped half (through-line view + visible run) is memoized
        /// against the ERS list identity, so the view is built once per store/supersede change
        /// rather than once per map frame; the route-scoped filters re-run per call (set lookups).
        /// </summary>
        internal static Func<string, IReadOnlyList<Recording>> CreateLiveExpander(Route route)
        {
            return memberId =>
            {
                if (string.IsNullOrEmpty(memberId)) return null;
                EnsureCaches();

                if (!visibleRunByMemberId.TryGetValue(memberId, out List<Recording> visibleRun))
                {
                    Recording head = ResolveVisible(memberId);
                    if (head == null)
                    {
                        // Not ERS-visible: leave the shipped head resolution (raw committed) to the
                        // renderer, which drops or draws it exactly as before.
                        visibleRunByMemberId[memberId] = null;
                        return null;
                    }
                    RecordingTree tree = EffectiveState.ResolveOwningTree(head, null);
                    visibleRun = ExpandVisibleRun(
                        tree, memberId, head, ResolveVisible, out RunTally walkTally);
                    visibleRunByMemberId[memberId] = visibleRun;
                    if (walkTally.Segments > 0)
                        ParsekLog.Verbose(Tag, string.Format(CultureInfo.InvariantCulture,
                            "Route member run: member={0} tree={1} walked={2}",
                            RouteIds.Short(memberId),
                            RouteIds.Short(tree != null ? tree.Id : null), walkTally.Segments));
                }
                if (visibleRun == null) return null;

                var tally = default(RunTally);
                return ApplyRouteFilters(
                    visibleRun,
                    route != null ? route.CreationTreeRecordingIds : null,
                    route != null ? route.ExcludedIntervalKeys : null,
                    ref tally);
            };
        }

        private static void EnsureCaches()
        {
            IReadOnlyList<Recording> ers = EffectiveState.ComputeERS();
            if (ReferenceEquals(ers, cachedErs) && visibleById != null) return;

            cachedErs = ers;
            visibleById = new Dictionary<string, Recording>(
                ers != null ? ers.Count : 0, StringComparer.Ordinal);
            if (ers != null)
            {
                for (int i = 0; i < ers.Count; i++)
                {
                    Recording rec = ers[i];
                    if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;
                    visibleById[rec.RecordingId] = rec;
                }
            }
            claimsByTreeId.Clear();
            visibleRunByMemberId.Clear();
            ParsekLog.Verbose(Tag, string.Format(CultureInfo.InvariantCulture,
                "Route member run cache reset: visible={0}", visibleById.Count));
        }

        private static Recording ResolveVisible(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId) || visibleById == null) return null;
            return visibleById.TryGetValue(recordingId, out Recording rec) ? rec : null;
        }

        private static RunClaimIndex ResolveRunClaims(RecordingTree tree)
        {
            // An empty tree id is not a cache key: two different id-less trees would share one entry
            // and the second would be walked against the first's runs. Build without caching instead -
            // an id-less tree is not a shape the live path produces, so the cost is unreachable.
            string key = tree != null ? tree.Id : null;
            bool cacheable = !string.IsNullOrEmpty(key);
            if (cacheable && claimsByTreeId.TryGetValue(key, out RunClaimIndex cachedClaims))
                return cachedClaims;

            MissionStructure structure = MissionStructureBuilder.Build(tree);
            MissionThroughLineView view = MissionThroughLineBuilder.Build(structure);
            RunClaimIndex claims = BuildRunClaims(view);
            if (cacheable) claimsByTreeId[key] = claims;
            ViewBuildCountForTesting++;
            return claims;
        }

        private static void LogStop(string memberId, string segmentId, string reason)
        {
            ParsekLog.Verbose(Tag, string.Format(CultureInfo.InvariantCulture,
                "Route member run stop: member={0} at={1} reason={2}",
                RouteIds.Short(memberId), RouteIds.Short(segmentId), reason));
        }

        /// <summary>
        /// Drops the memoized ERS index / through-line views / member runs. Called on the route
        /// line's own cross-save flush + scene teardown; the ERS-identity check would rebuild them
        /// anyway, this just does not carry a dead save's trees into the next one.
        /// </summary>
        internal static void ClearCaches()
        {
            cachedErs = null;
            visibleById = null;
            claimsByTreeId.Clear();
            visibleRunByMemberId.Clear();
        }

        internal static void ResetForTesting()
        {
            ClearCaches();
            ViewBuildCountForTesting = 0;
        }
    }
}
