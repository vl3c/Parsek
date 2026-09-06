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
    /// onward, so the renderer and the member-set producer cannot disagree about what a run is.
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
        private static readonly Dictionary<string, MissionThroughLineView> viewByTreeId =
            new Dictionary<string, MissionThroughLineView>(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<Recording>> visibleRunByMemberId =
            new Dictionary<string, List<Recording>>(StringComparer.Ordinal);

        internal static int ViewBuildCountForTesting;

        /// <summary>Counters for one member's expansion.</summary>
        internal struct RunTally
        {
            /// <summary>Continuation segments kept AFTER the head.</summary>
            public int Segments;
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

            MissionThroughLineView view = ResolveThroughLineView(tree);
            List<string> successors = SuccessorIdsAfter(view, memberRecordingId);
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

            tally.Segments -= visibleRun.Count - keep;
            return visibleRun.GetRange(0, keep);
        }

        /// <summary>
        /// The run member ids AFTER <paramref name="memberRecordingId"/> in whichever through-line
        /// contains it, in walk order. Null when no run in the view contains the id (an unstructured
        /// tree, or a debris id that never became a leg).
        /// </summary>
        internal static List<string> SuccessorIdsAfter(
            MissionThroughLineView view, string memberRecordingId)
        {
            if (view == null || string.IsNullOrEmpty(memberRecordingId)) return null;
            foreach (var line in view.ByHeadId.Values)
            {
                if (line?.MemberLegIds == null) continue;
                int at = line.MemberLegIds.IndexOf(memberRecordingId);
                if (at < 0) continue;
                var rest = new List<string>(line.MemberLegIds.Count - at - 1);
                for (int i = at + 1; i < line.MemberLegIds.Count; i++)
                    rest.Add(line.MemberLegIds[i]);
                return rest;
            }
            return null;
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
                            "Route member run: member={0} tree={1} segments={2}",
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
            viewByTreeId.Clear();
            visibleRunByMemberId.Clear();
            ParsekLog.Verbose(Tag, string.Format(CultureInfo.InvariantCulture,
                "Route member run cache reset: visible={0}", visibleById.Count));
        }

        private static Recording ResolveVisible(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId) || visibleById == null) return null;
            return visibleById.TryGetValue(recordingId, out Recording rec) ? rec : null;
        }

        private static MissionThroughLineView ResolveThroughLineView(RecordingTree tree)
        {
            string key = tree != null && !string.IsNullOrEmpty(tree.Id) ? tree.Id : string.Empty;
            if (viewByTreeId.TryGetValue(key, out MissionThroughLineView cachedView))
                return cachedView;

            MissionStructure structure = MissionStructureBuilder.Build(tree);
            MissionThroughLineView view = MissionThroughLineBuilder.Build(structure);
            viewByTreeId[key] = view;
            ViewBuildCountForTesting++;
            return view;
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
            viewByTreeId.Clear();
            visibleRunByMemberId.Clear();
        }

        internal static void ResetForTesting()
        {
            ClearCaches();
            ViewBuildCountForTesting = 0;
        }
    }
}
