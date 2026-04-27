// [ERS-exempt] Phase 2 anchor builder reads marker.OriginChildRecordingId
// (NotCommitted) and its sibling recordings from CommittedRecordings to
// compute the recorded offset (design doc §7.1). ERS would filter
// NotCommitted provisionals, hiding the active re-fly target by
// construction. See scripts/ers-els-audit-allowlist.txt for the matching
// rationale entry.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.Rendering
{
    /// <summary>
    /// Read-only context handed to <see cref="RenderSessionState.RebuildFromMarker"/>'s
    /// test overload. Pairs a recording's owning <see cref="RecordingTree"/> with
    /// the <see cref="BranchPoint"/> whose <c>ChildRecordingIds</c> include the
    /// origin recording — i.e. the split point where the live and ghost siblings
    /// diverged. The test seam lets unit tests inject a synthetic tree without
    /// standing up <see cref="RecordingStore.CommittedTrees"/>.
    /// </summary>
    internal readonly struct RecordingTreeContext
    {
        public readonly RecordingTree Tree;
        public readonly BranchPoint ParentBranchPoint;

        public RecordingTreeContext(RecordingTree tree, BranchPoint parentBranchPoint)
        {
            Tree = tree;
            ParentBranchPoint = parentBranchPoint;
        }
    }

    /// <summary>
    /// In-memory store of Phase 2 <see cref="AnchorCorrection"/>s computed at
    /// re-fly session entry (design doc §6.3 / §7.1 / §18 Phase 2).
    /// <see cref="RebuildFromMarker"/> reads the live vessel's spawn-time world
    /// position EXACTLY ONCE (HR-15) and freezes it; every ghost sibling's ε is
    /// then computed as <c>target − P_smoothed_world</c> where target is
    /// <c>live_world_at_spawn + recordedOffset</c>.
    ///
    /// <para>
    /// Lifetime: scene-scoped. <see cref="Clear"/> on scene transitions and
    /// re-fly session end. <see cref="RebuildFromMarker"/> overwrites any prior
    /// state — there is no stale-data leak between sessions (HR-9).
    /// </para>
    ///
    /// <para>
    /// Subsystem tags (design doc §19.1 / §19.2):
    /// <list type="bullet">
    ///   <item><description><c>Pipeline-Session</c> for rebuild start / complete
    ///   / clear lifecycle (L12 / L13 / L16).</description></item>
    ///   <item><description><c>Pipeline-Anchor</c> for per-anchor ε emission
    ///   (L17 / L18 / L20).</description></item>
    /// </list>
    /// </para>
    /// </summary>
    internal static class RenderSessionState
    {
        // --- bubble-radius sanity check -------------------------------------
        // KSP physics bubble is 2.5 km — anchors with ε exceeding this are
        // almost certainly a frame mismatch or DAG-propagation bug, not a
        // real recorded offset. HR-9 says emit Warn but keep the value (do
        // not silently zero it) so the visible failure is the next render.
        private const double BubbleRadiusMetres = 2500.0;

        private static readonly object Lock = new object();
        private static readonly Dictionary<AnchorKey, AnchorCorrection> Anchors
            = new Dictionary<AnchorKey, AnchorCorrection>();
        private static string s_currentSessionId;

        /// <summary>Number of anchors in the current session map.</summary>
        internal static int Count
        {
            get { lock (Lock) { return Anchors.Count; } }
        }

        /// <summary>Session id of the marker that produced the current map (null when cleared).</summary>
        internal static string CurrentSessionId
        {
            get { lock (Lock) { return s_currentSessionId; } }
        }

        /// <summary>
        /// Looks up an anchor by recordingId / sectionIndex / side. Returns
        /// false when no entry exists; callers must NOT inject phantom
        /// translations on miss (HR-9).
        /// </summary>
        internal static bool TryLookup(
            string recordingId, int sectionIndex, AnchorSide side, out AnchorCorrection ac)
        {
            ac = default;
            if (string.IsNullOrEmpty(recordingId)) return false;
            lock (Lock)
            {
                return Anchors.TryGetValue(
                    new AnchorKey(recordingId, sectionIndex, side), out ac);
            }
        }

        /// <summary>
        /// Convenience wrapper for the Phase 2 single-anchor case (Stage 4 not
        /// implemented yet) — returns the start-side correction or null. Phase
        /// 3 will replace this with an interval-evaluator that lerps between
        /// start and end ε.
        /// </summary>
        internal static AnchorCorrection? LookupForSegmentStart(string recordingId, int sectionIndex)
        {
            if (TryLookup(recordingId, sectionIndex, AnchorSide.Start, out AnchorCorrection ac))
                return ac;
            return null;
        }

        /// <summary>
        /// Empties the anchor map. Always emits a Pipeline-Session Info line
        /// (L16) with the supplied reason — silent clears would let stale
        /// state escape detection (HR-9).
        /// </summary>
        internal static void Clear(string reason)
        {
            int count;
            string sessionId;
            lock (Lock)
            {
                count = Anchors.Count;
                sessionId = s_currentSessionId;
                Anchors.Clear();
                s_currentSessionId = null;
            }
            ParsekLog.Info("Pipeline-Session",
                $"Clear: reason={reason ?? "<unspecified>"} previousSessionId={sessionId ?? "<none>"} clearedCount={count}");
        }

        /// <summary>Test-only: clears state silently (no log) for harness setup/teardown.</summary>
        internal static void ResetForTesting()
        {
            lock (Lock)
            {
                Anchors.Clear();
                s_currentSessionId = null;
            }
            SurfaceLookupOverrideForTesting = null;
        }

        // -------------------------------------------------------------------
        //  Production overload — resolves recordings + tree + live position
        //  from the live game state. Writes the in-memory anchor map.
        //
        //  ERS-exempt rationale (file-level comment at top of file):
        //  the marker's OriginChildRecordingId points at the active re-fly
        //  target which is NotCommitted by construction; ERS would filter
        //  it out by definition.
        // -------------------------------------------------------------------

        /// <summary>
        /// Test-only override for the body-surface-position lookup used inside
        /// <see cref="RebuildFromMarker"/>. xUnit cannot construct real
        /// <see cref="CelestialBody"/> instances, so tests inject a closure
        /// returning a precomputed world position keyed by (bodyName, lat, lon,
        /// alt). Production code reads <c>null</c> here and falls through to
        /// <see cref="CelestialBody.GetWorldSurfacePosition"/>.
        /// </summary>
        internal static Func<string, double, double, double, Vector3d> SurfaceLookupOverrideForTesting;

        /// <summary>
        /// Production entry point invoked from <c>RewindInvoker.ConsumePostLoad</c>
        /// and <c>ParsekScenario.OnLoad</c> (design doc §17.2). Resolves
        /// recordings via <see cref="RecordingStore.CommittedRecordings"/>,
        /// trees via <see cref="RecordingStore.CommittedTrees"/>, and the live
        /// vessel's world position via <see cref="FlightGlobals.Vessels"/>.
        /// </summary>
        internal static void RebuildFromMarker(ReFlySessionMarker marker)
        {
            // Snapshot the committed list once so the test overload sees a
            // stable view even if the live store is mutated mid-rebuild.
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
                // Should not happen — RecordingStore.CommittedRecordings is a
                // simple property — but if a race ever surfaces, log Warn and
                // bail out cleanly. HR-9.
                ParsekLog.Warn("Pipeline-Session",
                    $"RebuildFromMarker: snapshot failed ex={ex.GetType().Name}:{ex.Message}");
                Clear("snapshot-failed");
                return;
            }

            Func<string, RecordingTreeContext> treeLookup = recordingId =>
                ResolveTreeContextFromCommittedTrees(recordingId);

            Func<string, Vector3d?> liveProvider = recordingId =>
                ResolveLiveWorldPositionForRecording(recordingId, recordings);

            RebuildFromMarker(marker, recordings, treeLookup, liveProvider);
        }

        private static RecordingTreeContext ResolveTreeContextFromCommittedTrees(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return new RecordingTreeContext(null, null);

            List<RecordingTree> trees = RecordingStore.CommittedTrees;
            if (trees == null) return new RecordingTreeContext(null, null);

            for (int i = 0; i < trees.Count; i++)
            {
                RecordingTree tree = trees[i];
                if (tree == null || tree.Recordings == null) continue;
                if (!tree.Recordings.ContainsKey(recordingId)) continue;

                BranchPoint parent = null;
                if (tree.BranchPoints != null)
                {
                    for (int b = 0; b < tree.BranchPoints.Count; b++)
                    {
                        BranchPoint bp = tree.BranchPoints[b];
                        if (bp == null || bp.ChildRecordingIds == null) continue;
                        if (bp.ChildRecordingIds.Contains(recordingId))
                        {
                            parent = bp;
                            break;
                        }
                    }
                }
                return new RecordingTreeContext(tree, parent);
            }
            return new RecordingTreeContext(null, null);
        }

        private static Vector3d? ResolveLiveWorldPositionForRecording(
            string recordingId, IReadOnlyList<Recording> recordings)
        {
            if (string.IsNullOrEmpty(recordingId) || recordings == null) return null;

            // Find the recording's spawned-vessel persistentId. The provisional
            // re-fly target writes its PID to SpawnedVesselPersistentId once
            // the spawn lands; before then the production overload will get
            // null here and short-circuit (HR-15 — frozen value or no value).
            uint spawnedPid = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                Recording r = recordings[i];
                if (r == null) continue;
                if (string.Equals(r.RecordingId, recordingId, StringComparison.Ordinal))
                {
                    spawnedPid = r.SpawnedVesselPersistentId;
                    break;
                }
            }
            if (spawnedPid == 0) return null;

            try
            {
                if (!FlightGlobals.ready) return null;
                var vessels = FlightGlobals.Vessels;
                if (vessels == null) return null;
                for (int i = 0; i < vessels.Count; i++)
                {
                    Vessel v = vessels[i];
                    if (v == null) continue;
                    if (v.persistentId == spawnedPid)
                        return v.GetWorldPos3D();
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Pipeline-Anchor",
                    $"ResolveLiveWorldPositionForRecording: ex={ex.GetType().Name}:{ex.Message}");
            }
            return null;
        }

        // -------------------------------------------------------------------
        //  Test overload — fully injectable so unit tests don't need KSP.
        // -------------------------------------------------------------------

        /// <summary>
        /// Test-friendly overload. Computes ε per ghost sibling using the
        /// supplied recordings list, tree-lookup function, and frozen-once
        /// live-position provider. Production code calls into this via the
        /// no-param overload.
        /// </summary>
        internal static void RebuildFromMarker(
            ReFlySessionMarker marker,
            IReadOnlyList<Recording> recordings,
            Func<string, RecordingTreeContext> treeLookup,
            Func<string, Vector3d?> liveWorldPositionProvider)
        {
            // L12 — Pipeline-Session Info line, fired at the START of every
            // rebuild attempt (success or failure). Bookended by L13 on
            // success or by an explicit Warn/Info on the early-out paths.
            string sessionId = marker?.SessionId ?? "<null>";
            string activeReFly = marker?.ActiveReFlyRecordingId ?? "<null>";
            string originChild = marker?.OriginChildRecordingId ?? "<null>";
            ParsekLog.Info("Pipeline-Session",
                $"RebuildFromMarker start: sessionId={sessionId} " +
                $"activeReFlyRecordingId={activeReFly} originChildRecordingId={originChild}");

            // Guard 1: marker null OR origin id empty → clear and bail. We
            // never want stale anchors leaking into a non-session render.
            if (marker == null || string.IsNullOrEmpty(marker.OriginChildRecordingId))
            {
                Clear("marker-null");
                return;
            }

            if (recordings == null || recordings.Count == 0)
            {
                ParsekLog.Warn("Pipeline-Anchor",
                    $"RebuildFromMarker: no recordings supplied " +
                    $"sessionId={marker.SessionId ?? "<no-id>"}");
                Clear("no-recordings");
                return;
            }

            // Guard 2: resolve R_origin by id from the committed list.
            Recording rOrigin = FindRecordingById(recordings, marker.OriginChildRecordingId);
            if (rOrigin == null)
            {
                ParsekLog.Warn("Pipeline-Anchor",
                    $"RebuildFromMarker: marker-orphan-origin-missing " +
                    $"originChildRecordingId={marker.OriginChildRecordingId} sessionId={marker.SessionId ?? "<no-id>"}");
                Clear("marker-orphan-origin-missing");
                return;
            }

            // Guard 3: resolve the parent BranchPoint via the supplied tree
            // lookup. A missing parent BP is the orphan-marker case described
            // in §15 / log L14 — we Warn (HR-9) and bail.
            RecordingTreeContext context = treeLookup != null
                ? treeLookup(rOrigin.RecordingId)
                : default;
            if (context.ParentBranchPoint == null)
            {
                ParsekLog.Warn("Pipeline-Anchor",
                    $"RebuildFromMarker: orphan-marker-no-parent-branchpoint " +
                    $"originRecordingId={rOrigin.RecordingId} sessionId={marker.SessionId ?? "<no-id>"}");
                Clear("orphan-marker-no-parent-branchpoint");
                return;
            }

            BranchPoint bp = context.ParentBranchPoint;

            // Sibling enumeration.
            var siblingIds = new List<string>();
            if (bp.ChildRecordingIds != null)
            {
                for (int i = 0; i < bp.ChildRecordingIds.Count; i++)
                {
                    string id = bp.ChildRecordingIds[i];
                    if (string.IsNullOrEmpty(id)) continue;
                    if (string.Equals(id, marker.OriginChildRecordingId, StringComparison.Ordinal)) continue;
                    siblingIds.Add(id);
                }
            }
            if (siblingIds.Count == 0)
            {
                ParsekLog.Verbose("Pipeline-Anchor",
                    $"RebuildFromMarker: no-siblings " +
                    $"branchPointId={bp.Id ?? "<no-id>"} originRecordingId={rOrigin.RecordingId} sessionId={marker.SessionId ?? "<no-id>"}");
                // Replace any prior session's anchors with an empty map for
                // this session's id, so subsequent lookups don't return stale
                // values.
                lock (Lock)
                {
                    Anchors.Clear();
                    s_currentSessionId = marker.SessionId;
                }
                ParsekLog.Info("Pipeline-Session",
                    $"RebuildFromMarker complete: sessionId={marker.SessionId ?? "<no-id>"} " +
                    $"siblingsConsidered=0 anchorsWritten=0 skippedNoLivePoint=0 " +
                    $"skippedNoGhostPoint=0 skippedRelativeFrame=0 skippedBodyMissing=0 skippedSplineSection=0");
                return;
            }

            // Live world read — HR-15 frozen single-shot.
            Vector3d? maybeLive = liveWorldPositionProvider != null
                ? liveWorldPositionProvider(marker.OriginChildRecordingId)
                : (Vector3d?)null;
            if (!maybeLive.HasValue)
            {
                ParsekLog.Warn("Pipeline-Anchor",
                    $"RebuildFromMarker: live-vessel-missing " +
                    $"originChildRecordingId={marker.OriginChildRecordingId} sessionId={marker.SessionId ?? "<no-id>"} " +
                    $"branchPointId={bp.Id ?? "<no-id>"}");
                Clear("live-vessel-missing");
                return;
            }
            Vector3d live_world_at_spawn = maybeLive.Value;
            // L18 — pin the frozen live position once per session. Tests
            // assert exactly one of these lines per session (HR-15 audit).
            ParsekLog.Verbose("Pipeline-Anchor",
                string.Format(CultureInfo.InvariantCulture,
                    "Live anchor read: sessionId={0} originChildRecordingId={1} anchorUT={2} frozenWorldPos=({3},{4},{5})",
                    marker.SessionId ?? "<no-id>", marker.OriginChildRecordingId,
                    bp.UT.ToString("R", CultureInfo.InvariantCulture),
                    live_world_at_spawn.x.ToString("R", CultureInfo.InvariantCulture),
                    live_world_at_spawn.y.ToString("R", CultureInfo.InvariantCulture),
                    live_world_at_spawn.z.ToString("R", CultureInfo.InvariantCulture)));

            // Resolve the live recording's section that owns bp.UT once. We
            // need it for the RELATIVE-frame guard on the live side; if it's
            // RELATIVE we cannot compute a world-space recordedOffset for
            // any sibling and must skip every one of them.
            int liveSectionIdx = TrajectoryMath.FindTrackSectionForUT(rOrigin.TrackSections, bp.UT);
            ReferenceFrame liveFrame = ReferenceFrame.Absolute;
            if (liveSectionIdx >= 0 && rOrigin.TrackSections != null)
                liveFrame = rOrigin.TrackSections[liveSectionIdx].referenceFrame;

            // Begin a fresh anchor map for this session.
            int anchorsWritten = 0;
            int skippedNoGhostPoint = 0;
            int skippedNoLivePoint = 0;
            int skippedRelative = 0;
            int skippedBodyMissing = 0;
            int skippedSplineSection = 0;

            lock (Lock)
            {
                Anchors.Clear();
                s_currentSessionId = marker.SessionId;
            }

            // Resolve liveFirstPoint once — every sibling pairs against the
            // same live boundary point.
            TrajectoryPoint liveFirst = default;
            bool liveHasPoint = TryFindFirstPointAtOrAfter(rOrigin.Points, bp.UT, out liveFirst);
            if (!liveHasPoint)
            {
                ParsekLog.Verbose("Pipeline-Anchor",
                    $"RebuildFromMarker: skipping-all-siblings reason=live-no-point " +
                    $"originRecordingId={rOrigin.RecordingId} bpUT={bp.UT.ToString("R", CultureInfo.InvariantCulture)}");
                ParsekLog.Info("Pipeline-Session",
                    $"RebuildFromMarker complete: sessionId={marker.SessionId ?? "<no-id>"} " +
                    $"siblingsConsidered={siblingIds.Count} anchorsWritten=0 skippedNoLivePoint={siblingIds.Count} " +
                    $"skippedNoGhostPoint=0 skippedRelativeFrame=0 skippedBodyMissing=0 skippedSplineSection=0");
                return;
            }

            // Capture the surface-lookup once per rebuild so production and
            // test paths share one closure. Test override wins when set.
            Func<string, double, double, double, Vector3d> surfaceLookup =
                SurfaceLookupOverrideForTesting ?? DefaultSurfaceLookup;

            // Validate live body resolves before iterating siblings — a null
            // body up here means every sibling would be skipped for the same
            // reason; surface the failure once.
            Vector3d liveProbe;
            bool liveBodyOk = TryLookupSurfacePosition(
                surfaceLookup, liveFirst.bodyName, liveFirst.latitude, liveFirst.longitude, liveFirst.altitude, out liveProbe);
            if (!liveBodyOk)
            {
                ParsekLog.Verbose("Pipeline-Anchor",
                    $"RebuildFromMarker: live-body-resolve-failed bodyName={liveFirst.bodyName ?? "<null>"}");
            }

            for (int s = 0; s < siblingIds.Count; s++)
            {
                string sibId = siblingIds[s];
                Recording rSib = FindRecordingById(recordings, sibId);
                if (rSib == null)
                {
                    ParsekLog.Verbose("Pipeline-Anchor",
                        $"RebuildFromMarker: sibling-not-in-recordings sibId={sibId}");
                    skippedNoGhostPoint++;
                    continue;
                }

                if (!TryFindFirstPointAtOrAfter(rSib.Points, bp.UT, out TrajectoryPoint ghostFirst))
                {
                    ParsekLog.Verbose("Pipeline-Anchor",
                        $"RebuildFromMarker: ghost-no-point-after-bpUT sibId={sibId} " +
                        $"bpUT={bp.UT.ToString("R", CultureInfo.InvariantCulture)}");
                    skippedNoGhostPoint++;
                    continue;
                }

                // RELATIVE-frame guard. The pipeline cannot resolve
                // metres-along-anchor-axes to a world-space offset without
                // dispatching through TryResolveRelativeOffsetWorldPosition,
                // which is a Phase 4+ concern. For Phase 2 we skip the
                // section entirely and emit a Verbose line that grep gates
                // pin (L22).
                int sibSectionIdx = TrajectoryMath.FindTrackSectionForUT(rSib.TrackSections, bp.UT);
                ReferenceFrame sibFrame = ReferenceFrame.Absolute;
                if (sibSectionIdx >= 0 && rSib.TrackSections != null)
                    sibFrame = rSib.TrackSections[sibSectionIdx].referenceFrame;

                if (sibFrame == ReferenceFrame.Relative
                    || sibFrame == ReferenceFrame.OrbitalCheckpoint
                    || liveFrame == ReferenceFrame.Relative
                    || liveFrame == ReferenceFrame.OrbitalCheckpoint)
                {
                    ParsekLog.Verbose("Pipeline-Anchor",
                        $"RebuildFromMarker: section-relative-skip sibId={sibId} " +
                        $"sibFrame={sibFrame} liveFrame={liveFrame} bpUT={bp.UT.ToString("R", CultureInfo.InvariantCulture)}");
                    skippedRelative++;
                    continue;
                }

                Vector3d ghost_abs_world;
                bool ghostBodyOk = TryLookupSurfacePosition(
                    surfaceLookup, ghostFirst.bodyName, ghostFirst.latitude, ghostFirst.longitude, ghostFirst.altitude, out ghost_abs_world);
                if (!ghostBodyOk || !liveBodyOk)
                {
                    ParsekLog.Verbose("Pipeline-Anchor",
                        $"RebuildFromMarker: body-resolve-failed sibId={sibId} " +
                        $"liveBody={liveFirst.bodyName ?? "<null>"} ghostBody={ghostFirst.bodyName ?? "<null>"}");
                    skippedBodyMissing++;
                    continue;
                }

                if (sibSectionIdx < 0)
                {
                    ParsekLog.Warn("Pipeline-Anchor",
                        $"RebuildFromMarker: sibling-section-not-found sibId={sibId} " +
                        $"bpUT={bp.UT.ToString("R", CultureInfo.InvariantCulture)}");
                    skippedSplineSection++;
                    continue;
                }

                Vector3d live_abs_world = liveProbe;

                Vector3d recordedOffset = ghost_abs_world - live_abs_world;
                Vector3d target = live_world_at_spawn + recordedOffset;

                // Resolve P_smoothed_world. If a Phase 1 spline is available
                // for the sibling section, evaluate at bp.UT; otherwise fall
                // back to the raw boundary sample.
                Vector3d pSmoothedWorld;
                bool splineHit = false;
                if (SectionAnnotationStore.TryGetSmoothingSpline(rSib.RecordingId, sibSectionIdx, out SmoothingSpline spline)
                    && spline.IsValid)
                {
                    Vector3d posLatLonAlt = TrajectoryMath.CatmullRomFit.Evaluate(spline, bp.UT);
                    if (!TryLookupSurfacePosition(
                            surfaceLookup, ghostFirst.bodyName, posLatLonAlt.x, posLatLonAlt.y, posLatLonAlt.z,
                            out pSmoothedWorld))
                    {
                        ParsekLog.Verbose("Pipeline-Anchor",
                            $"RebuildFromMarker: smoothed-body-resolve-failed sibId={sibId} sectionIdx={sibSectionIdx}");
                        pSmoothedWorld = ghost_abs_world;
                    }
                    else
                    {
                        splineHit = true;
                    }
                }
                else
                {
                    ParsekLog.Verbose("Pipeline-Anchor",
                        $"RebuildFromMarker: no-spline-using-raw sibId={sibId} sectionIdx={sibSectionIdx}");
                    pSmoothedWorld = ghost_abs_world;
                }

                Vector3d epsilon = target - pSmoothedWorld;
                double epsilonMagnitudeM = epsilon.magnitude;

                if (epsilonMagnitudeM > BubbleRadiusMetres)
                {
                    // L20: HR-9 visible-failure Warn. Keep the value (do not
                    // zero) so the next render shows the misalignment instead
                    // of silently masking it.
                    ParsekLog.Warn("Pipeline-Anchor",
                        string.Format(CultureInfo.InvariantCulture,
                            "Anchor ε exceeds bubble radius: recordingId={0} sectionIndex={1} side={2} " +
                            "anchorSource={3} epsilonMagnitudeM={4} bubbleRadiusM={5}",
                            rSib.RecordingId, sibSectionIdx, AnchorSide.Start,
                            AnchorSource.LiveSeparation,
                            epsilonMagnitudeM.ToString("R", CultureInfo.InvariantCulture),
                            BubbleRadiusMetres.ToString("F0", CultureInfo.InvariantCulture)));
                }

                var ac = new AnchorCorrection(
                    recordingId: rSib.RecordingId,
                    sectionIndex: sibSectionIdx,
                    side: AnchorSide.Start,
                    ut: bp.UT,
                    epsilon: epsilon,
                    source: AnchorSource.LiveSeparation);

                lock (Lock)
                {
                    Anchors[new AnchorKey(rSib.RecordingId, sibSectionIdx, AnchorSide.Start)] = ac;
                }
                anchorsWritten++;

                ParsekLog.Info("Pipeline-Anchor",
                    string.Format(CultureInfo.InvariantCulture,
                        "Anchor ε computed: recordingId={0} sectionIndex={1} side={2} source={3} " +
                        "epsilonMagnitudeM={4} splineHit={5} bpUT={6}",
                        rSib.RecordingId, sibSectionIdx, AnchorSide.Start,
                        AnchorSource.LiveSeparation,
                        epsilonMagnitudeM.ToString("R", CultureInfo.InvariantCulture),
                        splineHit ? "true" : "false",
                        bp.UT.ToString("R", CultureInfo.InvariantCulture)));
            }

            // L13 — Pipeline-Session Info summary close-out. Bookends L12.
            ParsekLog.Info("Pipeline-Session",
                $"RebuildFromMarker complete: sessionId={marker.SessionId ?? "<no-id>"} " +
                $"siblingsConsidered={siblingIds.Count} anchorsWritten={anchorsWritten} " +
                $"skippedNoLivePoint={skippedNoLivePoint} skippedNoGhostPoint={skippedNoGhostPoint} " +
                $"skippedRelativeFrame={skippedRelative} skippedBodyMissing={skippedBodyMissing} " +
                $"skippedSplineSection={skippedSplineSection}");
        }

        // -------------------------------------------------------------------
        //  helpers
        // -------------------------------------------------------------------

        private static Recording FindRecordingById(IReadOnlyList<Recording> recordings, string id)
        {
            if (recordings == null || string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < recordings.Count; i++)
            {
                Recording r = recordings[i];
                if (r == null) continue;
                if (string.Equals(r.RecordingId, id, StringComparison.Ordinal))
                    return r;
            }
            return null;
        }

        /// <summary>
        /// Linear scan for the first <see cref="TrajectoryPoint"/> with
        /// <c>ut &gt;= ut</c>. Recordings store points in monotonically
        /// increasing UT order so a binary search would also work; the
        /// list is small enough at re-fly entry that linear is acceptable
        /// and matches existing patterns in <see cref="TrajectoryMath"/>.
        /// </summary>
        private static bool TryFindFirstPointAtOrAfter(
            List<TrajectoryPoint> points, double ut, out TrajectoryPoint pt)
        {
            pt = default;
            if (points == null || points.Count == 0) return false;
            for (int i = 0; i < points.Count; i++)
            {
                if (points[i].ut >= ut)
                {
                    pt = points[i];
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Resolves <c>(bodyName, lat, lon, alt)</c> to a world-space position
        /// via either the test override or KSP's <see cref="CelestialBody"/>
        /// API. Returns false when the body cannot be resolved (xUnit, missing
        /// body name, or transient KSP state).
        /// </summary>
        private static bool TryLookupSurfacePosition(
            Func<string, double, double, double, Vector3d> lookup,
            string bodyName,
            double lat, double lon, double alt,
            out Vector3d worldPos)
        {
            worldPos = default;
            if (lookup == null || string.IsNullOrEmpty(bodyName)) return false;
            try
            {
                worldPos = lookup(bodyName, lat, lon, alt);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Vector3d DefaultSurfaceLookup(string bodyName, double lat, double lon, double alt)
        {
            CelestialBody body = string.IsNullOrEmpty(bodyName)
                ? null
                : FlightGlobals.GetBodyByName(bodyName);
            if (body == null)
                throw new InvalidOperationException($"CelestialBody '{bodyName}' not resolvable");
            return body.GetWorldSurfacePosition(lat, lon, alt);
        }
    }
}
