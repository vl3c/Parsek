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
using UnityEngine;

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
    /// list per (recordingId, sectionIndex) from Phase 6 commit time, the
    /// active <see cref="ReFlySessionMarker"/>, and an
    /// <see cref="IAnchorWorldFrameResolver"/> that produces the world-frame
    /// reference position for §7.4 / §7.5 / §7.6 / §7.10. Outputs: entries
    /// written into <see cref="RenderSessionState"/>'s map.
    /// </para>
    ///
    /// <para>
    /// Deferred sources (each emits ε = 0 and reserves the priority slot;
    /// the corresponding world-frame resolver lands in a follow-up):
    /// <list type="bullet">
    ///   <item>§7.8 CoBubblePeer — Phase 5 territory; reserved enum value
    ///   only.</item>
    ///   <item>§7.9 SurfaceContinuous — Phase 7 terrain raycast. Phase 6
    ///   emits the candidate marker so Phase 7 can hook the per-frame
    ///   resolver in without touching commit-time code.</item>
    /// </list>
    /// §7.7 BubbleEntry / BubbleExit shipped in v0.9.1 — adjacent
    /// TrackSection source-class transitions
    /// (Active|Background ↔ Checkpoint) emit candidates and the resolver
    /// reads the LAST/FIRST physics-active sample as the high-fidelity
    /// reference.
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
        /// Test seam: production resolver replacement. xUnit injects a
        /// hand-crafted <see cref="IAnchorWorldFrameResolver"/> stub and
        /// asserts the propagator forwarded the correct
        /// (recording, section, side, UT) tuple. Production callers leave
        /// this null and the propagator constructs a
        /// <see cref="ProductionAnchorWorldFrameResolver"/>.
        /// </summary>
        internal static IAnchorWorldFrameResolver ResolverOverrideForTesting;

        /// <summary>
        /// Test seam: smoothed-position-at-UT lookup, used to compute
        /// <c>ε = referenceWorldPos − P_smoothed_world(UT)</c>. xUnit
        /// injects a deterministic mapping; production resolves through
        /// the spline + frame-tag dispatch on the live KSP body. Reset
        /// via <see cref="ResetForTesting"/>.
        /// </summary>
        internal static System.Func<Recording, int, double, Vector3d?> SmoothedPositionForTesting;

        /// <summary>
        /// Test seam: replaces <see cref="FlightGlobals.Bodies"/> lookup
        /// inside the smoothed-position evaluator. xUnit cannot stand up
        /// CelestialBody instances; production callers leave this null.
        /// </summary>
        internal static System.Func<string, CelestialBody> BodyResolverForTesting;

        /// <summary>
        /// Test seam: when set, replaces
        /// <see cref="SessionSuppressionState.IsSuppressed"/>. xUnit can't
        /// stand up <see cref="ParsekScenario.Instance"/> + the
        /// <see cref="EffectiveState"/> closure cache that the suppression
        /// helper relies on. The seam takes a recordingId and returns
        /// <see langword="true"/> when that id should be treated as
        /// suppressed by the active session. Production code reads
        /// <c>null</c> here and routes through
        /// <see cref="SessionSuppressionState"/>.
        /// </summary>
        internal static System.Func<string, bool> SuppressionPredicateForTesting;

        /// <summary>Test-only: clears injected seams.</summary>
        internal static void ResetForTesting()
        {
            ResolverOverrideForTesting = null;
            SmoothedPositionForTesting = null;
            BodyResolverForTesting = null;
            SuppressionPredicateForTesting = null;
        }

        private static bool IsSuppressed(string recordingId)
        {
            var seam = SuppressionPredicateForTesting;
            if (seam != null) return seam(recordingId);
            return SessionSuppressionState.IsSuppressed(recordingId);
        }
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

            Run(marker, recordings, trees, /* surfaceLookup */ null,
                resolver: ResolverOverrideForTesting ?? new ProductionAnchorWorldFrameResolver());
        }

        /// <summary>
        /// Test-friendly overload. All side inputs injected; xUnit can
        /// reach every code path without standing up KSP. The
        /// <paramref name="surfaceLookup"/> argument is forwarded to the
        /// smoothed-position evaluator; <paramref name="resolver"/>
        /// supplies the world-frame reference positions for §7.4 / §7.5 /
        /// §7.6 / §7.10. When <paramref name="resolver"/> is null and the
        /// override seam is null too, the propagator skips the world-frame
        /// resolution pass and leaves ε = 0 for those sources (the
        /// priority slot is still reserved).
        /// </summary>
        internal static void Run(
            ReFlySessionMarker marker,
            IReadOnlyList<Recording> recordings,
            IReadOnlyList<RecordingTree> trees,
            Func<string, double, double, double, Vector3d> surfaceLookup,
            IAnchorWorldFrameResolver resolver = null)
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

            // Phase 1 — emit non-DockOrMerge seed anchors. For §7.4 / §7.5 /
            // §7.6 / §7.7 / §7.10 the world-frame resolver computes a real ε;
            // for §7.8 / §7.9 (deferred sources — see the class docstring)
            // the slot is reserved with ε = 0 so the §7.11 priority resolver
            // still runs.
            int resolvedRel = 0, resolvedOrb = 0, resolvedSoi = 0,
                resolvedLoop = 0, resolvedBubble = 0,
                deferredNoResolver = 0, deferredNoSpline = 0;
            IAnchorWorldFrameResolver activeResolver = ResolverOverrideForTesting ?? resolver;
            if (recordings != null)
            {
                for (int i = 0; i < recordings.Count; i++)
                {
                    Recording r = recordings[i];
                    if (r == null) continue;
                    if (r.TrackSections == null) continue;
                    if (IsSuppressed(r.RecordingId))
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

                            // Resolve world-frame reference position by source.
                            Vector3d epsilon = Vector3d.zero;
                            bool resolved = TryResolveSeedEpsilon(
                                activeResolver, surfaceLookup, r, sIdx, cand,
                                ref resolvedRel, ref resolvedOrb, ref resolvedSoi,
                                ref resolvedLoop, ref resolvedBubble,
                                ref deferredNoResolver, ref deferredNoSpline,
                                out epsilon);

                            var ac = new AnchorCorrection(
                                recordingId: r.RecordingId,
                                sectionIndex: sIdx,
                                side: cand.Side,
                                ut: cand.UT,
                                epsilon: resolved ? epsilon : Vector3d.zero,
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
            // Chain edges (PID continuity across recordings). Reviewer
            // P1-2: chain continuity is encoded by Recording.ChainId +
            // Recording.ChainIndex, NOT Recording.ParentRecordingId
            // (which is EVA child linkage — already covered by the
            // BranchPointType.EVA loop above). Group recordings by
            // ChainId, sort by ChainIndex, emit edges between consecutive
            // members at the boundary UT (parent.EndUT == child.StartUT).
            if (recordings != null)
            {
                var chainGroups = new Dictionary<string, List<Recording>>(StringComparer.Ordinal);
                for (int i = 0; i < recordings.Count; i++)
                {
                    Recording r = recordings[i];
                    if (r == null) continue;
                    if (string.IsNullOrEmpty(r.ChainId)) continue;
                    if (r.ChainIndex < 0) continue;
                    if (!chainGroups.TryGetValue(r.ChainId, out var list))
                    {
                        list = new List<Recording>();
                        chainGroups[r.ChainId] = list;
                    }
                    list.Add(r);
                }
                const double chainBoundaryToleranceSeconds = 1e-3;
                foreach (var kvp in chainGroups)
                {
                    List<Recording> members = kvp.Value;
                    if (members.Count < 2) continue;
                    members.Sort((a, b) => a.ChainIndex.CompareTo(b.ChainIndex));
                    for (int i = 0; i < members.Count - 1; i++)
                    {
                        Recording parent = members[i];
                        Recording child = members[i + 1];
                        // Verify boundary continuity within tolerance.
                        // Recordings whose endUT/startUT do not abut are
                        // either misordered or have a gap — skip emit
                        // and surface a Verbose so a real chain corruption
                        // doesn't silently propagate stale ε.
                        double parentEnd = parent.EndUT;
                        double childStart = child.StartUT;
                        if (System.Math.Abs(parentEnd - childStart) > chainBoundaryToleranceSeconds)
                        {
                            ParsekLog.Verbose("Pipeline-AnchorPropagate", string.Format(CultureInfo.InvariantCulture,
                                "chain-edge-boundary-mismatch: chainId={0} parent={1} parentEndUT={2} child={3} childStartUT={4}",
                                kvp.Key, parent.RecordingId,
                                parentEnd.ToString("R", CultureInfo.InvariantCulture),
                                child.RecordingId,
                                childStart.ToString("R", CultureInfo.InvariantCulture)));
                            continue;
                        }
                        edges.Add(new Edge(parent.RecordingId, child.RecordingId,
                            childStart, BranchPointType.Terminal /* sentinel — chain edges */, isChain: true));
                    }
                }
            }

            // Phase 2 worklist (ultrareview P2-A + follow-up: slot-keyed).
            // The first iteration keyed worklist + outgoing-edge index by
            // parent recordingId; that fixed BranchPoints list-order
            // dependence but introduced a subtler order-dependence bug
            // (subsequent ultrareview P1): when a recording was queued
            // because an UNRELATED section had a seed anchor, edges whose
            // parentSectionIdx was a DIFFERENT, still-unanchored section
            // would fall through to ε = 0, write a stale child anchor,
            // and mark the edge visitedEdges. When the real upstream
            // anchor for that edge's section was later written, the
            // edge was already visited and the corrected ε never flowed
            // downstream.
            //
            // The slot-keyed worklist makes the propagation actually
            // section-precise: index by (parentRecordingId, parentSectionIdx),
            // seed by walking every populated AnchorKey, and only walk
            // edges whose parent slot has been seeded. This way an edge
            // is processed exactly when its specific upstream anchor
            // exists, never sooner.
            //
            // Slot keys are encoded as `recordingId@sectionIdx` strings to
            // keep using HashSet<string> / Dictionary<string, _> without
            // a value-tuple-based equality comparer.
            var outgoingByParentSlot = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int e = 0; e < edges.Count; e++)
            {
                Edge ed = edges[e];
                if (string.IsNullOrEmpty(ed.ParentId)) continue;
                if (!byId.TryGetValue(ed.ParentId, out Recording rParent)) continue;
                if (rParent.TrackSections == null) continue;
                int parentSectionIdx = TrajectoryMath.FindTrackSectionForUT(rParent.TrackSections, ed.UT);
                if (parentSectionIdx < 0) continue;
                string slotKey = ed.ParentId + "@" +
                    parentSectionIdx.ToString(CultureInfo.InvariantCulture);
                if (!outgoingByParentSlot.TryGetValue(slotKey, out var bucket))
                {
                    bucket = new List<int>();
                    outgoingByParentSlot[slotKey] = bucket;
                }
                bucket.Add(e);
            }

            // Seed the worklist with every (recordingId, sectionIndex)
            // that already has an anchor written (any Side). Phase 2
            // LiveSeparation seeds from RebuildFromMarker plus the per-
            // source seed pass above both populate the anchor map BEFORE
            // this code runs. Slots without anchors are never enqueued —
            // edges depending on them stay deferred until a future
            // worklist iteration writes the missing anchor.
            var worklist = new Queue<string>();
            var enqueuedSlots = new HashSet<string>(StringComparer.Ordinal);
            if (recordings != null)
            {
                for (int i = 0; i < recordings.Count; i++)
                {
                    Recording r = recordings[i];
                    if (r == null || string.IsNullOrEmpty(r.RecordingId)) continue;
                    if (r.TrackSections == null) continue;
                    for (int sIdx = 0; sIdx < r.TrackSections.Count; sIdx++)
                    {
                        if (RenderSessionState.TryLookup(r.RecordingId, sIdx, AnchorSide.Start, out _)
                            || RenderSessionState.TryLookup(r.RecordingId, sIdx, AnchorSide.End, out _))
                        {
                            string slotKey = r.RecordingId + "@" +
                                sIdx.ToString(CultureInfo.InvariantCulture);
                            if (enqueuedSlots.Add(slotKey))
                                worklist.Enqueue(slotKey);
                        }
                    }
                }
            }

            // Drive the walk. For each anchored parent slot, propagate ε
            // along its outgoing edges. If propagation actually inserts a
            // new anchor on the child slot (TryWriteAnchor returned true),
            // enqueue THAT slot — not just the child recording — so only
            // edges whose parent slot is the newly-anchored one re-fire.
            // Cycle defense — the visitedEdges set ensures each edge
            // processes at most once even if the same slot gets enqueued
            // multiple times via priority overwrites.
            while (worklist.Count > 0)
            {
                string parentSlotKey = worklist.Dequeue();
                if (!outgoingByParentSlot.TryGetValue(parentSlotKey, out var edgeIndices))
                    continue;

                for (int ei = 0; ei < edgeIndices.Count; ei++)
                {
                    int e = edgeIndices[ei];
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

                bool childSuppressed = IsSuppressed(edge.ChildId);
                bool parentSuppressed = IsSuppressed(edge.ParentId);
                if (childSuppressed || parentSuppressed)
                {
                    suppressedSkipped++;
                    ParsekLog.Verbose("Pipeline-AnchorPropagate", string.Format(CultureInfo.InvariantCulture,
                        "suppressed-predecessor: parent={0} child={1} reason={2}",
                        edge.ParentId, edge.ChildId,
                        childSuppressed ? "suppressed-child" : "suppressed-parent"));
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

                // §9.1 propagation rule. For non-chain edges we evaluate
                // (recordedOffset_at_event - smoothedOffset_at_event) and
                // feed it through Propagate(). For chain edges (PID
                // continuity across recordings) the recorded offset is
                // zero by construction — the same vessel writes both
                // sides — so we keep ε' = ε without invoking the
                // per-side evaluator.
                Vector3d recordedOffset = Vector3d.zero;
                Vector3d smoothedOffset = Vector3d.zero;
                bool nineOneApplied = false;
                string nineOneFallbackReason = null;
                if (!edge.IsChain)
                {
                    // Pass our body resolver so FrameTag=1 inertial splines
                    // dispatch through LowerFromInertialToWorld instead of
                    // falling back to the raw sample (ultrareview P1-B).
                    Func<string, CelestialBody> bodyResolver = ResolveBody;
                    bool parentOk = RenderSessionState.TryEvaluatePerSegmentWorldPositions(
                        rParent, parentSectionIdx, edge.UT, surfaceLookup,
                        out Vector3d parentRecorded, out Vector3d parentSmoothed,
                        out bool parentSplineHit, out string parentReason,
                        bodyResolver);
                    bool childOk = RenderSessionState.TryEvaluatePerSegmentWorldPositions(
                        rChild, childSectionIdx, edge.UT, surfaceLookup,
                        out Vector3d childRecorded, out Vector3d childSmoothed,
                        out bool childSplineHit, out string childReason,
                        bodyResolver);
                    if (parentOk && childOk)
                    {
                        recordedOffset = childRecorded - parentRecorded;
                        smoothedOffset = childSmoothed - parentSmoothed;
                        nineOneApplied = true;

                        // HR-9: surface a "no-spline-skip" Verbose when
                        // either side fell through to recorded == smoothed.
                        // The propagation formula still runs (the offset
                        // delta is just zero on that side) but the
                        // operator needs to see that the §9.1 correction
                        // term is degraded.
                        if (!parentSplineHit || !childSplineHit)
                        {
                            ParsekLog.Verbose("Pipeline-AnchorPropagate", string.Format(CultureInfo.InvariantCulture,
                                "no-spline-skip: parent={0} parentSection={1} parentSplineHit={2} child={3} childSection={4} childSplineHit={5} bpUT={6}",
                                edge.ParentId, parentSectionIdx, parentSplineHit ? "true" : "false",
                                edge.ChildId, childSectionIdx, childSplineHit ? "true" : "false",
                                edge.UT.ToString("R", CultureInfo.InvariantCulture)));
                        }
                    }
                    else
                    {
                        // Either side lacks an Absolute section, or no
                        // recorded sample at bp.UT, or body resolve failed.
                        // Identity-propagate (ε' = ε) and surface the
                        // failure reason once per edge.
                        nineOneFallbackReason = !parentOk ? parentReason : childReason;
                        string failTag = !parentOk ? "no-sample-skip" : "no-sample-skip";
                        // section-not-absolute / no-sample / body-resolve-failed
                        // each get their own diagnostic suffix so log readers
                        // can distinguish them without parsing the reason field.
                        if (string.Equals(nineOneFallbackReason, "section-not-absolute", StringComparison.Ordinal))
                            failTag = "section-not-absolute-skip";
                        else if (string.Equals(nineOneFallbackReason, "no-sample", StringComparison.Ordinal))
                            failTag = "no-sample-skip";
                        else
                            failTag = "no-spline-skip"; // catch-all for body-resolve / out-of-range
                        ParsekLog.Verbose("Pipeline-AnchorPropagate", string.Format(CultureInfo.InvariantCulture,
                            "{0}: parent={1} parentSection={2} parentReason={3} child={4} childSection={5} childReason={6} bpUT={7}",
                            failTag, edge.ParentId, parentSectionIdx, parentReason ?? "<ok>",
                            edge.ChildId, childSectionIdx, childReason ?? "<ok>",
                            edge.UT.ToString("R", CultureInfo.InvariantCulture)));
                    }
                }

                Vector3d epsilonChild = Propagate(epsilonUpstream, recordedOffset, smoothedOffset);

                AnchorSide side = AnchorSide.Start;
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
                        "Edge propagated: {0}->{1} bpType={2} bpUT={3} chainEdge={4} nineOneApplied={5} epsilonDeltaM={6}",
                        edge.ParentId, edge.ChildId, edge.IsChain ? "chain" : edge.BranchType.ToString(),
                        edge.UT.ToString("R", CultureInfo.InvariantCulture),
                        edge.IsChain ? "true" : "false",
                        nineOneApplied ? "true" : "false",
                        (epsilonChild - epsilonUpstream).magnitude.ToString("F3", CultureInfo.InvariantCulture)));

                    // Worklist propagation: the child slot now has an
                    // anchor, so enqueue (childRecordingId, childSectionIdx)
                    // — not just the recording — so only edges whose
                    // parent slot IS this newly-anchored slot can re-fire
                    // downstream. visitedEdges keeps cycle defense; once
                    // an edge has run, a later §7.11 priority overwrite
                    // on the same child slot will NOT re-flow through
                    // visited edges. That limitation is documented in
                    // docs/dev/todo-and-known-bugs.md (Phase 6 known
                    // gaps); the iterative-refinement variant is tracked
                    // for a future phase.
                    string childSlotKey = edge.ChildId + "@" +
                        childSectionIdx.ToString(CultureInfo.InvariantCulture);
                    if (enqueuedSlots.Add(childSlotKey))
                        worklist.Enqueue(childSlotKey);
                }
                else
                {
                    candidatesDeferred++;
                }
                } // for ei
            } // while worklist

            sw.Stop();
            ParsekLog.Info("Pipeline-AnchorPropagate", string.Format(CultureInfo.InvariantCulture,
                "DAG walk summary: sessionId={0} edgesVisited={1} edgesPropagated={2} " +
                "seedCandidatesEmitted={3} candidatesDeferredByPriority={4} " +
                "resolvedRel={5} resolvedOrb={6} resolvedSoi={7} resolvedLoop={8} " +
                "resolvedBubble={9} " +
                "deferredNoResolver={10} deferredNoSpline={11} " +
                "suppressedSkipped={12} cycleSkipped={13} durationMs={14}",
                sessionId, edgesVisited, edgesPropagated,
                seedCandidatesEmitted, candidatesDeferred,
                resolvedRel, resolvedOrb, resolvedSoi, resolvedLoop,
                resolvedBubble,
                deferredNoResolver, deferredNoSpline,
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

        /// <summary>
        /// Resolve ε for a non-LiveSeparation, non-DockOrMerge candidate.
        /// Source dispatch ↔ resolver:
        /// <list type="bullet">
        ///   <item>RelativeBoundary  → <see cref="IAnchorWorldFrameResolver.TryResolveRelativeBoundaryWorldPos"/></item>
        ///   <item>OrbitalCheckpoint → <see cref="IAnchorWorldFrameResolver.TryResolveOrbitalCheckpointWorldPos"/></item>
        ///   <item>SoiTransition    → <see cref="IAnchorWorldFrameResolver.TryResolveSoiBoundaryWorldPos"/></item>
        ///   <item>Loop             → <see cref="IAnchorWorldFrameResolver.TryResolveLoopAnchorWorldPos"/></item>
        ///   <item>BubbleEntry / BubbleExit → <see cref="IAnchorWorldFrameResolver.TryResolveBubbleEntryExitWorldPos"/></item>
        ///   <item>SurfaceContinuous / CoBubblePeer
        ///         → deferred (returns false, ε stays 0).</item>
        /// </list>
        /// Returns true with a finite world-frame ε; returns false on
        /// resolver miss or when no smoothed-position is available
        /// (HR-9 visible failure: emits a Verbose log with the reason).
        /// </summary>
        private static bool TryResolveSeedEpsilon(
            IAnchorWorldFrameResolver resolver,
            Func<string, double, double, double, Vector3d> surfaceLookup,
            Recording rec, int sectionIndex, AnchorCandidate cand,
            ref int resolvedRel, ref int resolvedOrb, ref int resolvedSoi,
            ref int resolvedLoop, ref int resolvedBubble,
            ref int deferredNoResolver, ref int deferredNoSpline,
            out Vector3d epsilon)
        {
            epsilon = Vector3d.zero;

            // Sources that are explicitly deferred — see class docstring.
            // BubbleEntry / BubbleExit are NO LONGER deferred (§7.7 ships
            // in v0.9.1) — the dispatch below handles them via the new
            // resolver method.
            if (cand.Source == AnchorSource.CoBubblePeer
                || cand.Source == AnchorSource.SurfaceContinuous)
            {
                return false;
            }

            if (resolver == null)
            {
                deferredNoResolver++;
                ParsekLog.Verbose("Pipeline-Anchor", string.Format(CultureInfo.InvariantCulture,
                    "skip-no-resolver recordingId={0} sectionIndex={1} side={2} source={3}",
                    rec.RecordingId, sectionIndex, cand.Side, cand.Source));
                return false;
            }

            Vector3d worldRef = Vector3d.zero;
            bool ok = false;
            switch (cand.Source)
            {
                case AnchorSource.RelativeBoundary:
                    ok = resolver.TryResolveRelativeBoundaryWorldPos(rec, sectionIndex, cand.Side, cand.UT, out worldRef);
                    if (ok) resolvedRel++;
                    break;
                case AnchorSource.OrbitalCheckpoint:
                    ok = resolver.TryResolveOrbitalCheckpointWorldPos(rec, sectionIndex, cand.Side, cand.UT, out worldRef);
                    if (ok) resolvedOrb++;
                    break;
                case AnchorSource.SoiTransition:
                    ok = resolver.TryResolveSoiBoundaryWorldPos(rec, sectionIndex, cand.Side, cand.UT, out worldRef);
                    if (ok) resolvedSoi++;
                    break;
                case AnchorSource.Loop:
                    ok = resolver.TryResolveLoopAnchorWorldPos(rec, sectionIndex, cand.Side, cand.UT, out worldRef);
                    if (ok) resolvedLoop++;
                    break;
                case AnchorSource.BubbleEntry:
                case AnchorSource.BubbleExit:
                    // §7.7 priority rank 6 means a real OrbitalCheckpoint
                    // (rank 3) ε always wins on hypothetical collision; in
                    // practice the candidate emits on the Checkpoint
                    // segment's own index (Side=Start for BubbleExit,
                    // Side=End for BubbleEntry) while §7.5 candidates emit
                    // on the ABSOLUTE neighbour's index, so real collision
                    // is impossible.
                    ok = resolver.TryResolveBubbleEntryExitWorldPos(rec, sectionIndex, cand.Side, cand.UT, out worldRef);
                    if (ok) resolvedBubble++;
                    break;
            }
            if (!ok)
            {
                ParsekLog.Verbose("Pipeline-Anchor", string.Format(CultureInfo.InvariantCulture,
                    "resolver-miss recordingId={0} sectionIndex={1} side={2} source={3}",
                    rec.RecordingId, sectionIndex, cand.Side, cand.Source));
                return false;
            }

            // Compute P_smoothed_world(UT) so ε = worldRef - P_smoothed.
            Vector3d? maybeSmoothed = TryEvaluateSmoothedWorldPos(rec, sectionIndex, cand.UT, surfaceLookup);
            if (!maybeSmoothed.HasValue)
            {
                deferredNoSpline++;
                ParsekLog.Verbose("Pipeline-Anchor", string.Format(CultureInfo.InvariantCulture,
                    "skip-no-spline recordingId={0} sectionIndex={1} side={2} source={3}",
                    rec.RecordingId, sectionIndex, cand.Side, cand.Source));
                return false;
            }

            epsilon = worldRef - maybeSmoothed.Value;
            return true;
        }

        /// <summary>
        /// Evaluate the smoothed body-fixed-or-inertial world position at
        /// <paramref name="ut"/> for a given (recordingId, sectionIndex).
        /// Routes through the test seam first (xUnit injects a closure);
        /// otherwise composes the existing Phase 1 + Phase 4 pipeline:
        /// <see cref="SectionAnnotationStore.TryGetSmoothingSpline"/> →
        /// <see cref="TrajectoryMath.CatmullRomFit.Evaluate"/> →
        /// <see cref="TrajectoryMath.FrameTransform.DispatchSplineWorldByFrameTag"/>.
        /// For OrbitalCheckpoint sections (which never carry a smoothing
        /// spline) the helper instead evaluates the analytical Kepler
        /// position via <see cref="TrajectoryMath.FindOrbitSegment"/> +
        /// <see cref="Orbit.getPositionAtUT"/>; without this dispatch the
        /// §7.7 BubbleEntry/Exit candidates (which land on the Checkpoint
        /// segment's index) would always fall through to "no spline" and
        /// the propagator would never compute a real correction.
        /// Returns null when the section has no spline / no checkpoint or
        /// the body can't be resolved — caller logs the skip and falls
        /// back to ε = 0 (reserves the priority slot).
        /// </summary>
        private static Vector3d? TryEvaluateSmoothedWorldPos(
            Recording rec, int sectionIndex, double ut,
            Func<string, double, double, double, Vector3d> surfaceLookup)
        {
            var seam = SmoothedPositionForTesting;
            if (seam != null) return seam(rec, sectionIndex, ut);

            if (rec == null || rec.TrackSections == null) return null;
            if (sectionIndex < 0 || sectionIndex >= rec.TrackSections.Count) return null;

            TrackSection section = rec.TrackSections[sectionIndex];

            // OrbitalCheckpoint sections have no spline by construction —
            // resolve via the analytical Kepler propagation instead. This
            // is the path §7.7 BubbleEntry/Exit candidates take (they land
            // on the Checkpoint segment's own index, where the Kepler
            // segment is the "smoothed" reference). The shared helper
            // covers the partial-last-checkpoint endpoint-fallback case
            // that previously left ε = 0 when the candidate UT equalled
            // the section's endUT but the last sampled checkpoint's
            // endUT was a hair below — see TrajectoryMath.EvaluateOrbitSegmentAtUT.
            if (section.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
            {
                return TrajectoryMath.EvaluateOrbitSegmentAtUT(
                    section.checkpoints, ut, ResolveBody);
            }

            if (!SectionAnnotationStore.TryGetSmoothingSpline(rec.RecordingId, sectionIndex, out SmoothingSpline spline)
                || !spline.IsValid)
            {
                return null;
            }

            Vector3d posLatLonAlt = TrajectoryMath.CatmullRomFit.Evaluate(spline, ut);

            // Resolve body via the same dispatch the playback code uses.
            string bodyName = ResolveSectionBodyName(rec.TrackSections[sectionIndex]);
            if (string.IsNullOrEmpty(bodyName)) return null;

            CelestialBody body = ResolveBody(bodyName);
            if (body == null)
            {
                // xUnit path: the surfaceLookup seam can resolve world pos
                // directly without a CelestialBody handle. Use it for tag-0
                // splines only — inertial splines need a real body for the
                // rotation phase.
                if (spline.FrameTag == 0 && surfaceLookup != null)
                {
                    try
                    {
                        return surfaceLookup(bodyName, posLatLonAlt.x, posLatLonAlt.y, posLatLonAlt.z);
                    }
                    catch
                    {
                        return null;
                    }
                }
                return null;
            }

            // Production / tag-aware path.
            Vector3d worldPos = TrajectoryMath.FrameTransform.DispatchSplineWorldByFrameTag(
                spline.FrameTag,
                posLatLonAlt.x, posLatLonAlt.y, posLatLonAlt.z,
                body, ut, rec.RecordingId, sectionIndex);
            if (double.IsNaN(worldPos.x) || double.IsNaN(worldPos.y) || double.IsNaN(worldPos.z))
                return null;
            return worldPos;
        }

        private static string ResolveSectionBodyName(in TrackSection s)
        {
            if (s.frames != null && s.frames.Count > 0)
                return s.frames[0].bodyName;
            if (s.absoluteFrames != null && s.absoluteFrames.Count > 0)
                return s.absoluteFrames[0].bodyName;
            if (s.checkpoints != null && s.checkpoints.Count > 0)
                return s.checkpoints[0].bodyName;
            return null;
        }

        private static CelestialBody ResolveBody(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName)) return null;
            var seam = BodyResolverForTesting;
            if (seam != null) return seam(bodyName);
            try
            {
                return FlightGlobals.Bodies?.Find(b => b != null && b.bodyName == bodyName);
            }
            catch
            {
                return null;
            }
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
