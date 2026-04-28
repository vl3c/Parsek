// [ERS-exempt] Phase 6 anchor propagator (ghost-trajectory-rendering-design
// §17.3.1 / §18 Phase 6 / §9.1) reads RecordingStore.CommittedRecordings and
// CommittedTrees to walk every BranchPoint edge in scope. ERS would filter
// the active re-fly target's NotCommitted provisional out of the recording
// list, hiding the very seed anchor (LiveSeparation) that drives the DAG
// walk. The walk is read-only against trajectory data (HR-1 / HR-5). See
// scripts/ers-els-audit-allowlist.txt for the matching rationale entry.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Parsek.Rendering
{
    /// <summary>
    /// Phase 6 session-time DAG walk (design doc §6.3 / §7.11 / §9 / §18
    /// Phase 6). Translates the persisted <see cref="AnchorCandidate"/>
    /// entries in <see cref="SectionAnnotationStore"/> into resolved
    /// <see cref="AnchorCorrection"/> ε values and propagates them along
    /// <see cref="BranchPoint"/> edges per the §9.1 rule:
    /// <c>ε' = ε + (recordedOffsetAtEvent − smoothedOffsetAtEvent)</c>.
    ///
    /// <para>
    /// Inputs (all session-only): the live-separation anchors already in
    /// <see cref="RenderSessionState"/>'s map (Phase 2 seeds), the candidate
    /// list per (recordingId, sectionIndex) from Phase 6 commit time, and
    /// the active <see cref="ReFlySessionMarker"/>. Outputs: additional
    /// entries written into <see cref="RenderSessionState"/>'s map. Phase 7
    /// (terrain raycast) and Phase 5 (co-bubble blend) layer on top of this
    /// at consumer time; the propagator does not touch their concerns.
    /// </para>
    ///
    /// <para>
    /// The DAG is acyclic by HR-13 by construction; the walk additionally
    /// keeps a visited set to defend against malformed inputs (HR-9 visible
    /// failure on cycle suspect — Warn line + halt subtree).
    /// </para>
    /// </summary>
    internal static class AnchorPropagator
    {
        /// <summary>
        /// Pure §9.1 propagation rule. Translates an upstream ε to a
        /// downstream ε using the recorded vs smoothed offset deltas at the
        /// event UT. Public-internal for unit tests.
        /// </summary>
        internal static Vector3d Propagate(
            Vector3d epsilonUpstream,
            Vector3d recordedOffsetAtEvent,
            Vector3d smoothedOffsetAtEvent)
        {
            return epsilonUpstream + (recordedOffsetAtEvent - smoothedOffsetAtEvent);
        }

        /// <summary>
        /// Production overload. Resolves recordings + trees from
        /// <see cref="RecordingStore"/> and the marker. Walks every Dock /
        /// Split / chain edge in scope and writes the resulting anchors
        /// into <see cref="RenderSessionState"/> (only when the per-slot
        /// §7.11 priority resolver allows the new entry to win).
        /// </summary>
        internal static void Run(ReFlySessionMarker marker)
        {
            if (!AnchorCandidateBuilder.ResolveUseAnchorTaxonomy())
            {
                ParsekLog.Verbose("Pipeline-AnchorPropagate",
                    "useAnchorTaxonomy=false, skipping AnchorPropagator.Run");
                return;
            }

            var recordings = new List<Recording>();
            try
            {
                IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
                if (committed != null)
                {
                    for (int i = 0; i < committed.Count; i++)
                        recordings.Add(committed[i]);
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Pipeline-AnchorPropagate",
                    $"Run: snapshot failed ex={ex.GetType().Name}:{ex.Message}");
                return;
            }

            var trees = new List<RecordingTree>();
            try
            {
                List<RecordingTree> committedTrees = RecordingStore.CommittedTrees;
                if (committedTrees != null)
                {
                    for (int i = 0; i < committedTrees.Count; i++)
                        trees.Add(committedTrees[i]);
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Pipeline-AnchorPropagate",
                    $"Run: tree snapshot failed ex={ex.GetType().Name}:{ex.Message}");
                return;
            }

            Run(marker, recordings, trees, /* surfaceLookup */ null);
        }

        /// <summary>
        /// Test-friendly overload. All side inputs injected; xUnit can
        /// reach every code path without standing up KSP. The
        /// <paramref name="surfaceLookup"/> argument is reserved for
        /// future ε resolution that needs body-frame world positions
        /// (§7.4 RELATIVE-boundary, §7.5 OrbitalCheckpoint); Phase 6
        /// emits the candidates and propagates DockOrMerge ε but defers
        /// the world-frame resolvers for the non-LiveSeparation sources
        /// to a follow-up — see the README on the AnchorPropagator class.
        /// </summary>
        internal static void Run(
            ReFlySessionMarker marker,
            IReadOnlyList<Recording> recordings,
            IReadOnlyList<RecordingTree> trees,
            Func<string, double, double, double, Vector3d> surfaceLookup)
        {
            if (!AnchorCandidateBuilder.ResolveUseAnchorTaxonomy())
            {
                ParsekLog.Verbose("Pipeline-AnchorPropagate",
                    "useAnchorTaxonomy=false, skipping AnchorPropagator.Run");
                return;
            }

            string sessionId = marker?.SessionId ?? "<no-id>";
            string treeId = marker?.TreeId ?? "<no-id>";
            string activeReFly = marker?.ActiveReFlyRecordingId ?? "<no-id>";
            ParsekLog.Info("Pipeline-AnchorPropagate", string.Format(CultureInfo.InvariantCulture,
                "DAG walk start: sessionId={0} treeId={1} rootRecordingId={2}",
                sessionId, treeId, activeReFly));

            var sw = Stopwatch.StartNew();
            int edgesVisited = 0;
            int edgesPropagated = 0;
            int suppressedSkipped = 0;
            int cycleSkipped = 0;
            int seedCandidatesEmitted = 0;
            int candidatesDeferred = 0;

            // Index recordings by id for O(1) lookup during the walk.
            var byId = new Dictionary<string, Recording>(StringComparer.Ordinal);
            if (recordings != null)
            {
                for (int i = 0; i < recordings.Count; i++)
                {
                    Recording r = recordings[i];
                    if (r == null || string.IsNullOrEmpty(r.RecordingId)) continue;
                    byId[r.RecordingId] = r;
                }
            }

            // Phase 1 — emit non-DockOrMerge seed anchors directly into the
            // session state. RELATIVE-boundary, OrbitalCheckpoint, SOI,
            // BubbleEntry/Exit, Loop, SurfaceContinuous: Phase 6 records the
            // candidate's UT/source/side as the anchor metadata; ε is set to
            // zero today. The §7 ε formulas for these sources require a
            // surface / Kepler / live-vessel resolver that Phase 6 does not
            // wire (see the §7 references on each branch in the design doc;
            // §7.9 is explicitly Phase 7 work). The slot is occupied so the
            // §7.11 priority resolver still runs — a higher-priority
            // LiveSeparation never gets clobbered.
            if (recordings != null)
            {
                for (int i = 0; i < recordings.Count; i++)
                {
                    Recording r = recordings[i];
                    if (r == null) continue;
                    if (r.TrackSections == null) continue;
                    if (SessionSuppressionState.IsSuppressed(r.RecordingId))
                    {
                        // Suppressed-predecessor symmetry: a suppressed
                        // recording cannot seed candidates either.
                        continue;
                    }

                    for (int sIdx = 0; sIdx < r.TrackSections.Count; sIdx++)
                    {
                        if (!SectionAnnotationStore.TryGetAnchorCandidates(
                                r.RecordingId, sIdx, out AnchorCandidate[] arr) || arr == null)
                            continue;
                        for (int k = 0; k < arr.Length; k++)
                        {
                            AnchorCandidate cand = arr[k];
                            // DockOrMerge candidates are propagated by the
                            // edge walk below — skip them here so the seed
                            // pass and the walk don't double-write the
                            // same slot.
                            if (cand.Source == AnchorSource.DockOrMerge) continue;

                            // Phase 6 ε for non-LiveSeparation, non-DockOrMerge:
                            // record metadata only, ε = 0. The slot reserves
                            // the priority rank so future phases that wire the
                            // resolver can drop in real ε values without
                            // changing slot ownership.
                            var ac = new AnchorCorrection(
                                recordingId: r.RecordingId,
                                sectionIndex: sIdx,
                                side: cand.Side,
                                ut: cand.UT,
                                epsilon: Vector3d.zero,
                                source: cand.Source);
                            if (TryWriteAnchor(ac))
                                seedCandidatesEmitted++;
                            else
                                candidatesDeferred++;
                        }
                    }
                }
            }

            // Phase 2 — walk the DockOrMerge / Split BranchPoint edges +
            // chain edges to propagate ε along the §9.1 rule. Visited keys
            // guard against cycles.
            var visitedEdges = new HashSet<string>(StringComparer.Ordinal);

            // Build the unified edge set.
            var edges = new List<Edge>();
            if (trees != null)
            {
                for (int t = 0; t < trees.Count; t++)
                {
                    RecordingTree tree = trees[t];
                    if (tree == null || tree.BranchPoints == null) continue;
                    for (int b = 0; b < tree.BranchPoints.Count; b++)
                    {
                        BranchPoint bp = tree.BranchPoints[b];
                        if (bp == null) continue;
                        // Only propagate on edges that imply ε passes
                        // through the structural event. Terminal /
                        // Launch / Breakup are not propagation events
                        // for Phase 6.
                        if (bp.Type != BranchPointType.Dock
                            && bp.Type != BranchPointType.Board
                            && bp.Type != BranchPointType.Undock
                            && bp.Type != BranchPointType.EVA
                            && bp.Type != BranchPointType.JointBreak)
                        {
                            continue;
                        }
                        if (bp.ParentRecordingIds == null || bp.ChildRecordingIds == null) continue;
                        for (int p = 0; p < bp.ParentRecordingIds.Count; p++)
                        {
                            string pid = bp.ParentRecordingIds[p];
                            if (string.IsNullOrEmpty(pid)) continue;
                            for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                            {
                                string cid = bp.ChildRecordingIds[c];
                                if (string.IsNullOrEmpty(cid)) continue;
                                if (string.Equals(pid, cid, StringComparison.Ordinal)) continue;
                                edges.Add(new Edge(pid, cid, bp.UT, bp.Type, isChain: false));
                            }
                        }
                    }
                }
            }
            // Chain edges (PID continuity across recordings).
            if (recordings != null)
            {
                for (int i = 0; i < recordings.Count; i++)
                {
                    Recording r = recordings[i];
                    if (r == null) continue;
                    if (string.IsNullOrEmpty(r.ParentRecordingId)) continue;
                    edges.Add(new Edge(r.ParentRecordingId, r.RecordingId,
                        r.StartUT, BranchPointType.Terminal /* sentinel */, isChain: true));
                }
            }

            for (int e = 0; e < edges.Count; e++)
            {
                Edge edge = edges[e];
                edgesVisited++;

                string edgeKey = edge.ParentId + "->" + edge.ChildId + "@" +
                    edge.UT.ToString("R", CultureInfo.InvariantCulture);
                if (!visitedEdges.Add(edgeKey))
                {
                    // Cycle suspected (HR-13 defense) — halt the subtree.
                    cycleSkipped++;
                    ParsekLog.Warn("Pipeline-AnchorPropagate", string.Format(CultureInfo.InvariantCulture,
                        "cycle suspected, halting subtree: parent={0} child={1} ut={2}",
                        edge.ParentId, edge.ChildId,
                        edge.UT.ToString("R", CultureInfo.InvariantCulture)));
                    continue;
                }

                if (SessionSuppressionState.IsSuppressed(edge.ChildId)
                    || SessionSuppressionState.IsSuppressed(edge.ParentId))
                {
                    suppressedSkipped++;
                    ParsekLog.Verbose("Pipeline-AnchorPropagate", string.Format(CultureInfo.InvariantCulture,
                        "suppressed-predecessor: parent={0} child={1} reason={2}",
                        edge.ParentId, edge.ChildId,
                        SessionSuppressionState.IsSuppressed(edge.ChildId)
                            ? "suppressed-child" : "suppressed-parent"));
                    continue;
                }

                if (!byId.TryGetValue(edge.ParentId, out Recording rParent)) continue;
                if (!byId.TryGetValue(edge.ChildId, out Recording rChild)) continue;
                if (rParent.TrackSections == null || rChild.TrackSections == null) continue;

                int parentSectionIdx = TrajectoryMath.FindTrackSectionForUT(rParent.TrackSections, edge.UT);
                int childSectionIdx = TrajectoryMath.FindTrackSectionForUT(rChild.TrackSections, edge.UT);
                if (parentSectionIdx < 0 || childSectionIdx < 0) continue;

                // Look up parent's End ε (preferred), then Start ε, then
                // zero. Phase 2 LiveSeparation seeds usually populate the
                // Start side; we accept either side as the upstream
                // reference.
                Vector3d epsilonUpstream = Vector3d.zero;
                if (RenderSessionState.TryLookup(edge.ParentId, parentSectionIdx, AnchorSide.End, out AnchorCorrection acEnd))
                    epsilonUpstream = acEnd.Epsilon;
                else if (RenderSessionState.TryLookup(edge.ParentId, parentSectionIdx, AnchorSide.Start, out AnchorCorrection acStart))
                    epsilonUpstream = acStart.Epsilon;

                // Phase 6: Vector3d.zero for the recorded/smoothed offsets
                // at the event UT. The §9.1 rule reduces to ε' = ε in this
                // configuration — sufficient for chain continuity (PID
                // continuity → recordedOffset = 0 by construction) and for
                // Dock/Board/Split where the recorder's sample-time
                // alignment is the design-doc invariant. A surface-lookup
                // capable resolver would compute the recorded vs smoothed
                // delta and feed it through Propagate(); see the propagator
                // class README for the §7.4 / §7.5 follow-up.
                Vector3d epsilonChild = Propagate(epsilonUpstream, Vector3d.zero, Vector3d.zero);

                AnchorSide side = edge.IsChain ? AnchorSide.Start : AnchorSide.Start;
                var acChild = new AnchorCorrection(
                    recordingId: edge.ChildId,
                    sectionIndex: childSectionIdx,
                    side: side,
                    ut: edge.UT,
                    epsilon: epsilonChild,
                    source: AnchorSource.DockOrMerge);

                if (TryWriteAnchor(acChild))
                {
                    edgesPropagated++;
                    ParsekLog.Verbose("Pipeline-AnchorPropagate", string.Format(CultureInfo.InvariantCulture,
                        "Edge propagated: {0}->{1} bpType={2} bpUT={3} chainEdge={4} epsilonDeltaM={5}",
                        edge.ParentId, edge.ChildId, edge.IsChain ? "chain" : edge.BranchType.ToString(),
                        edge.UT.ToString("R", CultureInfo.InvariantCulture),
                        edge.IsChain ? "true" : "false",
                        (epsilonChild - epsilonUpstream).magnitude.ToString("F3", CultureInfo.InvariantCulture)));
                }
                else
                {
                    candidatesDeferred++;
                }
            }

            sw.Stop();
            ParsekLog.Info("Pipeline-AnchorPropagate", string.Format(CultureInfo.InvariantCulture,
                "DAG walk summary: sessionId={0} edgesVisited={1} edgesPropagated={2} " +
                "seedCandidatesEmitted={3} candidatesDeferredByPriority={4} " +
                "suppressedSkipped={5} cycleSkipped={6} durationMs={7}",
                sessionId, edgesVisited, edgesPropagated,
                seedCandidatesEmitted, candidatesDeferred,
                suppressedSkipped, cycleSkipped,
                sw.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture)));
        }

        /// <summary>
        /// Per-slot priority-aware write: only inserts the anchor when no
        /// existing entry holds the slot OR when §7.11 says the candidate's
        /// source outranks the existing one. Returns true on insert; emits
        /// a Pipeline-Anchor Verbose line ("Anchor source priority
        /// resolution") through <see cref="RenderSessionState"/> on
        /// contention.
        /// </summary>
        private static bool TryWriteAnchor(AnchorCorrection candidate)
        {
            return RenderSessionState.PutAnchorWithPriority(candidate);
        }

        // -------------------------------------------------------------------

        private readonly struct Edge
        {
            public readonly string ParentId;
            public readonly string ChildId;
            public readonly double UT;
            public readonly BranchPointType BranchType;
            public readonly bool IsChain;
            public Edge(string parent, string child, double ut, BranchPointType bt, bool isChain)
            {
                ParentId = parent; ChildId = child; UT = ut; BranchType = bt; IsChain = isChain;
            }
        }
    }
}
