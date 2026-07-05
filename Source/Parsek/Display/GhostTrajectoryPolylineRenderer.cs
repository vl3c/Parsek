// [ERS-exempt] Non-orbital map polyline renderer reads
// RecordingStore.CommittedRecordings raw to draw physical-visibility map
// geometry for atmospheric-only recordings (no ledger / ERS routing
// applies; same physical-visibility scope as DrawAtmosphericMarkers).
// See scripts/ers-els-audit-allowlist.txt for the matching allowlist
// entry. Design plan: docs/dev/plans/map-trajectory-polyline.md v6.
using System;
using System.Collections.Generic;
using UnityEngine;
using Vectrosity;

namespace Parsek.Display
{
    /// <summary>
    /// Per-recording non-orbital map polyline renderer (design plan
    /// docs/dev/plans/map-trajectory-polyline.md v6).
    ///
    /// Bridges the gap between successive ghost orbit-line arcs by drawing a
    /// polyline through the recorded trajectory points for atmospheric /
    /// propulsive / surface phases that have no Keplerian arc.
    ///
    /// The pure builder caches the recorded body-fixed (lat, lon, alt)
    /// triples per leg point and does NO geometry conversion (so it stays
    /// deterministic + xUnit-testable without a live CelestialBody). The
    /// Driver's per-frame hot path converts each triple to a world position
    /// via the live <c>CelestialBody.GetWorldSurfacePosition(lat, lon, alt)</c>
    /// call -- exactly the conversion <c>ParsekTrackingStation</c>'s
    /// atmospheric-marker resolver uses at line 1199 -- then runs it through
    /// <c>ScaledSpace.LocalToScaledSpace</c>. Reusing
    /// <see cref="LegPolyline.scratchScaledSpace"/> + each leg's own
    /// <c>VectorLine.points3</c> keeps the hot path zero-alloc. The 200-point
    /// per-leg cap (§1.3) keeps the per-frame GetWorldSurfacePosition cost
    /// well within budget.
    ///
    /// Data structures + pure builder + cache lifecycle helpers, the
    /// Vectrosity Driver MonoBehaviour, and the per-frame walk over
    /// CommittedRecordings are all in this file. Each leg owns its own
    /// VectorLine (drawn full-range); a single shared line per recording
    /// does NOT work because <c>VectorLine.Draw3D()</c> zeroes every vertex
    /// outside the current <c>drawStart</c>/<c>drawEnd</c> window on each
    /// call, so only the last leg drawn would survive.
    /// </summary>
    internal static class GhostTrajectoryPolylineRenderer
    {
        private const string Tag = "GhostMap";

        /// <summary>
        /// Soft per-leg point cap. Legs longer than this are downsampled at
        /// cache-build time keeping the first + last sample and uniform-
        /// striding the remaining (cap - 2) interior samples (§1.3).
        /// </summary>
        internal const int MaxPolylinePointsPerLeg = 200;

        /// <summary>
        /// Per-forward-arc sample count (Step 3 C). 180 matches the stock
        /// <c>OrbitRendererBase.OrbitPoints</c> length the current-arc patch passes to
        /// <see cref="OrbitArcSampler.SampleSegmentArc"/> (sampleResolution 2.0), so a forward arc reads at
        /// the identical resolution as the stock current arc.
        /// </summary>
        internal const int ForwardArcSampleCount = 180;

        /// <summary>
        /// Per-body surface geometry needed to decide whether an OrbitSegment is
        /// degenerate (its drawn conic plunges below the SURFACE so the orbit line
        /// cannot usably trace it). Injected into the pure builder via
        /// <see cref="BodySurfaceProvider"/> so the builder never calls
        /// FlightGlobals (the Driver populates it from the live bodies). Only the
        /// body radius is needed (CHANGE 2: the exclusion boundary is the surface,
        /// not the atmosphere top).
        /// </summary>
        internal struct BodySurfaceInfo
        {
            /// <summary>Body radius (metres).</summary>
            public double radius;
        }

        /// <summary>
        /// Seam (FIX #27): resolves per-body surface geometry (radius) by body
        /// name. Returns false when the body is unknown, in which case the orbital
        /// cover keeps EVERY segment (byte-identical to the pre-fix behaviour, so
        /// a recording with no degenerate/below-surface segments is unaffected).
        /// The Driver supplies a FlightGlobals-backed lookup; the xUnit builder
        /// tests pass null (no exclusion) or a synthetic provider.
        /// </summary>
        internal delegate bool BodySurfaceProvider(string bodyName, out BodySurfaceInfo info);

        // RecordingId -> per-recording leg set. Atmospheric-only recordings
        // have pid=0 so PID is NOT a usable key; RecordingId (string) is.
        // Matches the pattern MapMarkerRenderer.stickyMarkers uses.
        private static readonly Dictionary<string, LegPolylineSet> polylineCache =
            new Dictionary<string, LegPolylineSet>(StringComparer.Ordinal);

        /// <summary>
        /// Phase 8b.2 / 8e S3a.1 (actual-draw ownership): recordings whose non-orbital polyline leg
        /// actually DREW this frame - on EITHER the OWNED <c>TracedPathTreatment.TryDrawOwnedLeg</c> path
        /// (Director decided Visible+TracedPath) OR the Driver-direct path (the Director classified the
        /// span StockConic, e.g. the re-aim "bridge" legs that lie on the conic). The DRAW is the
        /// authoritative ownership signal; the Director's StockConic/TracedPath classification is
        /// irrelevant to whether the proto line/icon must be hidden, so this set is DECOUPLED from
        /// <c>IsTracedPathOwnedThisFrame</c> (8e S3a.1). Populated only on an ACTUAL draw (preserving
        /// 8b.1's "proto hidden iff a leg drew" robustness), so it can never report ownership for a frame
        /// where nothing drew (the degenerate-leg / head-in-gap / traj.Points-vs-TrackSections divergence
        /// gap - see <see cref="ResolveNonOrbitalLegOwnership"/>). 8e S3b: this is now the SOLE ownership
        /// source - the legacy gate-off fallback set was DELETED after the S3a gate + an in-game re-fly
        /// proved this drew-set byte-identical + a superset, so <see cref="ResolveNonOrbitalLegOwnership"/>
        /// returns drew-set membership GATE-INDEPENDENTLY. Published on the per-recording
        /// <c>if (anyDrawn)</c> block and cleared at the top of every <c>LateUpdate</c>.
        /// </summary>
        private static readonly HashSet<string> drewNonOrbitalLegRecordings =
            new HashSet<string>(StringComparer.Ordinal);

        // --- Render-EVENT diff (map-render-event-logging): appear / disappear of a recording's drawn
        // polyline. The Driver already computes drewNonOrbitalLegRecordings (the current-element ownership
        // set) per frame and an actual-draw forward set per onPreCull; diffing each against the prior frame
        // emits ONE PolylineLegChange appear/disappear EVENT per recording (the count-keyed polyline.drawset
        // VerboseOnChange misses a same-count swap and is on the Driver tag, not the unified MapRenderTrace).
        // OBSERVABILITY ONLY - read-only diffs of sets the draw code already populated; no draw change.
        // Maintained / diffed ONLY while mapRenderTracing is on; cleared on cross-save flush so a stale set
        // never fires spurious events. Static to match drewNonOrbitalLegRecordings (the Driver is a DDOL
        // singleton); the recordingId keys are stable across a scene switch, so a missed clear can only miss
        // an event, never invent one.
        private static readonly HashSet<string> previousDrewNonOrbitalLegRecordings =
            new HashSet<string>(StringComparer.Ordinal);
        // FORWARD-arc/leg actual-draw set this onPreCull + last onPreCull (the "extra line AHEAD of the
        // ghost"), diffed under surface=PolylineForwardArc. currentDrewForwardArcRecordings is cleared at the
        // top of OnMapCameraPreCull and populated where a forward leg / forward arc actually paints.
        private static readonly HashSet<string> currentDrewForwardArcRecordings =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> previousDrewForwardArcRecordings =
            new HashSet<string>(StringComparer.Ordinal);
        // Reusable scratch for the set diffs (allocated once; populated only when tracing is enabled).
        private static readonly List<string> diffAppearedScratch = new List<string>();
        private static readonly List<string> diffDisappearedScratch = new List<string>();
        // Per-(surface, recordingId, event) wall-clock floor for the PolylineLegChange appear/disappear EVENT.
        // The draw-set CAN oscillate drawn<->not-drawn every frame at high warp (the documented warp-reseed-lag
        // line-blink), which would otherwise re-emit an appear+disappear pair to Info every frame - per-frame
        // spam, and worst of all on exactly the bug a maintainer enables the tracer to debug. The floor
        // collapses a sustained oscillation to onset + a ~1/s heartbeat per event; a stable appear/disappear
        // still emits immediately (its key's floor is long expired). Keyed by wall-clock (warp-stable, the
        // #1063 rule); cleared on cross-save flush + capped for warp safety. Only touched when tracing is on.
        private const double LegChangeEmitMinIntervalSeconds = 1.0;
        private static readonly Dictionary<string, double> legChangeLastEmitRealtime =
            new Dictionary<string, double>(StringComparer.Ordinal);

        // RecordingId -> per-recording FORWARD-ARC line set (Step 3 C, forward-trajectory-render plan).
        // Holds the sampled Vectrosity VectorLines for the orbit (StockConic) segments AHEAD of the icon up
        // to forwardStopUT, plus the cache key (currentElementIndex|bodyName|reaimWindowIndex) so a window
        // rollover / element advance re-samples. SEPARATE from polylineCache so the forward additive pass is
        // fully decoupled from the current-element leg draw + its ownership publish (the SAFEST ownership
        // approach in the plan's Step 3 CRITICAL bullet: forward draws NEVER touch
        // drewNonOrbitalLegRecordings). One VectorLine per forward segment.
        private static readonly Dictionary<string, ForwardArcSet> forwardArcCache =
            new Dictionary<string, ForwardArcSet>(StringComparer.Ordinal);

        // BOUNDARY-OVERLAP forward-arc namespace (launch->escape seam render, review H1): forwardArcCache is
        // keyed by recording id, but during the borrow window of a zero-slack re-aim launch loop the Driver
        // runs the forward pass TWICE for the SAME recording in one frame - the PRIMARY at the through-line
        // head (e.g. the Duna approach) then the SECONDARY at the early-launch head (the Kerbin ascent +
        // escape). The two heads select DISJOINT arc sets, so a single recording-id key made the second pass
        // overwrite the first's cached arcs (destroying its VectorLines), and the first pass's already-enqueued
        // PendingForwardArcDraw indices then read the wrong array (out-of-range -> the primary's forward conic
        // vanished). The secondary's arcs live under recordingId + this suffix so BOTH coexist within one
        // frame. The suffix uses a control char no real recording id contains. The primary path is unchanged
        // (key == recordingId), so every non-launch-hold member / aligned loop is byte-identical.
        private const string ForwardSecondaryCacheSuffix = "bsec";

        internal static string ForwardArcDictKey(string recordingId, bool boundaryOverlapSecondary)
            => (boundaryOverlapSecondary && !string.IsNullOrEmpty(recordingId))
                ? recordingId + ForwardSecondaryCacheSuffix
                : recordingId;

        // Reusable scratch buffer for OrbitArcSampler.SampleSegmentArc output (body-LOCAL points). The
        // sampler fills it transiently and RefreshForwardArcs copies the body-relative offsets out into each
        // arc's own array before the next sample, so a single shared buffer is safe (the map render loop is
        // single-threaded; RefreshForwardArcs runs sequentially per recording). Avoids a per-cache-miss
        // 180-element allocation on the multi-ghost map walk.
        private static readonly Vector3d[] forwardArcSampleScratch = new Vector3d[ForwardArcSampleCount];

        // SEAM BRIDGE lines (playtest 6, generalized playtest 7): fillers connecting each drawn
        // body-fixed leg to its adjacent inertial conics. Keyed per (legRecordingId|legIndex|side) so
        // a leg can carry a start-side AND an end-side bridge simultaneously (the Duna descent legs).
        // One fixed-size (BridgeMergeSampleCount+1) VectorLine per key, rebuilt point-wise per frame
        // while bridging; hidden by the bridge deactivation sweep when not drawn. Destroyed with the
        // forward arcs in Clear() / ReleaseForRecording so no Vectrosity GameObject leaks.
        private sealed class BridgeLineEntry
        {
            public VectorLine line;
            public int lastDrawnFrame;
            // Map-line mode (0=unbuilt, 1=3D Draw3D, 2=2D Draw) the line was last built for. Bug 1:
            // Vectrosity's in-place VectorObject3D<->VectorObject2D swap leaves the 2D canvas Graphic
            // non-rendering, so the line is rebuilt when this differs from the live mode (stock MakeLine-
            // on-flip). See RebuildMapLineIfModeChanged.
            public int lineMode;
        }

        private static readonly Dictionary<string, BridgeLineEntry> bridgeLineByRecording =
            new Dictionary<string, BridgeLineEntry>(StringComparer.Ordinal);

        // On-demand bridge conic samples (playtest 7): 61 inertial body-relative points along a
        // conic's lead-in or tail, for bridge sides whose conic is NOT a cached forward arc (the
        // CURRENT element - e.g. the final orbit the icon rides while the landing leg lies ahead).
        // Sampled per (recording|segment|side) via Orbit.getPositionAtUT and RESAMPLED IN PLACE when
        // the co-rotating world frame turns under the capture (playtest-9, see
        // ForwardArcSet.captureInverseRotAngle); capped and cleared with the forward caches.
        private sealed class BridgeConicSamples
        {
            public Vector3d[] pts;
            public double captureInverseRotAngle;
        }

        private static readonly Dictionary<string, BridgeConicSamples> bridgeConicSampleCache =
            new Dictionary<string, BridgeConicSamples>(StringComparer.Ordinal);

        /// <summary>Cap for <see cref="bridgeConicSampleCache"/>; on overflow the whole cache is
        /// dropped (entries are cheap to resample).</summary>
        private const int BridgeConicSampleCacheCap = 32;

        /// <summary>
        /// PURE on-demand bridge sample span (playtest-8 star fix): a third of the segment's duration
        /// OR of one orbital period, whichever is SHORTER (>= 1 s). Without the period clamp, a
        /// multi-revolution segment (the ~660-rev parking-ellipse loiter) yields 61 samples tens of
        /// thousands of seconds apart - arbitrary orbit phases that drew as a star polygon around
        /// Kerbin. A non-finite period (hyperbolic / unknown mu) falls back to duration/3.
        /// xUnit-testable.
        /// </summary>
        internal static double ComputeBridgeSampleSpanSeconds(
            double segStartUT, double segEndUT, double periodSeconds)
        {
            double duration = segEndUT - segStartUT;
            if (duration <= 0.0) return 0.0;
            double baseSpan = duration;
            if (!double.IsNaN(periodSeconds) && !double.IsInfinity(periodSeconds)
                && periodSeconds > 0.0 && periodSeconds < baseSpan)
                baseSpan = periodSeconds;
            return System.Math.Max(baseSpan / 3.0, 1.0);
        }

        /// <summary>
        /// Returns 61 inertial body-relative samples along <paramref name="seg"/>'s lead-in
        /// (<paramref name="tail"/>=false: [startUT, startUT+span]) or tail (true:
        /// [endUT-span, endUT]), span per <see cref="ComputeBridgeSampleSpanSeconds"/> (duration- AND
        /// period-clamped), cached per (recording|segment|side). Null on a degenerate segment / orbit
        /// construction fault (the caller skips the bridge).
        /// </summary>
        private static Vector3d[] GetBridgeConicSamples(
            string recordingId, OrbitSegment seg, CelestialBody body, bool tail)
        {
            if (body == null || seg.endUT <= seg.startUT) return null;
            // Key includes the conic's ELEMENTS (review MINOR-3): a re-aim synodic-window rollover can
            // swap a segment's Kepler elements while keeping its UT span, and a UT-only key would then
            // serve stale wrong-aimed samples (the forward-arc cache keys on the re-aim window for the
            // same reason; sma+ecc discriminate the swapped geometry without plumbing the window index).
            string key = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}|{1:F1}|{2:F1}|{3:F0}|{4:F6}|{5}", recordingId, seg.startUT, seg.endUT,
                seg.semiMajorAxis, seg.eccentricity, tail ? "t" : "l");
            double liveRotAngle = Planetarium.InverseRotAngle;
            if (bridgeConicSampleCache.TryGetValue(key, out var cached))
            {
                // Rotating-frame staleness (playtest 9): when the co-rotating world frame turned
                // since capture, refill the SAME array (orbit evaluation is frame-correct per call).
                if (!HasFrameRotationDrift(cached.captureInverseRotAngle, liveRotAngle))
                    return cached.pts;
                if (FillBridgeConicSamples(seg, body, tail, cached.pts))
                {
                    cached.captureInverseRotAngle = liveRotAngle;
                    return cached.pts;
                }
                return cached.pts; // resample fault: keep the stale frame rather than dropping the bridge
            }

            var pts = new Vector3d[BridgeMergeSampleCount + 1];
            if (!FillBridgeConicSamples(seg, body, tail, pts))
                return null;

            if (bridgeConicSampleCache.Count >= BridgeConicSampleCacheCap)
                bridgeConicSampleCache.Clear();
            bridgeConicSampleCache[key] = new BridgeConicSamples
            {
                pts = pts,
                captureInverseRotAngle = liveRotAngle,
            };
            return pts;
        }

        /// <summary>
        /// Samples <paramref name="seg"/>'s lead-in/tail span into <paramref name="pts"/> (in place;
        /// the rotating-frame resample reuses the cached array). False on a degenerate segment /
        /// orbit fault / non-finite sample - the array is then left untouched past the failure point,
        /// so callers must not publish a half-filled FRESH array (the in-place resample tolerates it:
        /// a stale-but-coherent frame beats a torn one only when the prior content was coherent,
        /// which a cache hit guarantees).
        /// </summary>
        private static bool FillBridgeConicSamples(
            OrbitSegment seg, CelestialBody body, bool tail, Vector3d[] pts)
        {
            double span = ComputeBridgeSampleSpanSeconds(
                seg.startUT, seg.endUT,
                ForwardRenderWindow.ComputePeriod(seg.semiMajorAxis, body.gravParameter));
            if (span <= 0.0) return false;
            double t0 = tail ? seg.endUT - span : seg.startUT;
            try
            {
                var orbit = new Orbit(
                    seg.inclination, seg.eccentricity, seg.semiMajorAxis,
                    seg.longitudeOfAscendingNode, seg.argumentOfPeriapsis,
                    seg.meanAnomalyAtEpoch, seg.epoch, body);
                Vector3d bodyPos = body.position;
                for (int i = 0; i <= BridgeMergeSampleCount; i++)
                {
                    double ut = t0 + span * i / BridgeMergeSampleCount;
                    Vector3d world = orbit.getPositionAtUT(ut);
                    if (!IsFiniteVec(world)) return false;
                    pts[i] = world - bodyPos;
                }
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Marker ride-robustness (pan-stability): the last on-line position the marker successfully
        /// rode for a recording, kept so a TRANSIENT ride dropout (the leg was not drawn this frame -
        /// the dominant case during an active map-camera pan - or the head sits in an inter-leg gap
        /// inside the recording's overall span) holds the marker on the line instead of snapping to the
        /// body-fixed head. Stamped on every successful <c>RodeLeg</c> in
        /// <see cref="TryAnchorMarkerToPolyline(string,double,out Vector3,out MapRenderTrace.MarkerRideReason,out int)"/>;
        /// consumed by <see cref="TryHoldLastGood"/>, which bounds it by frame-age + head-UT delta so a
        /// genuine orbital-phase exit (head past the last leg, or a long stall) still falls through to
        /// the deep fallback. Cleared with the ownership sets in <see cref="Clear"/>, per-recording in
        /// <see cref="ReleaseForRecording"/>, and on scene switch via the Driver's HandleLevelWasLoaded.
        /// </summary>
        private struct LastGoodOnLine
        {
            public Vector3 worldPos;
            public double headUT;
            public int frame;
            public int legIndex;
        }

        private static readonly Dictionary<string, LastGoodOnLine> lastGoodOnLine =
            new Dictionary<string, LastGoodOnLine>(StringComparer.Ordinal);

        /// <summary>
        /// Max frames a held last-good on-line position stays valid after the last successful ride. At
        /// 8 frames a transient pan dropout (typically a single frame, occasionally a short run) is
        /// covered, while a sustained stall (line genuinely stopped drawing) expires the hold and the
        /// marker falls through to the body-fixed head.
        /// </summary>
        internal const int LastGoodMaxFrameAge = 8;

        /// <summary>
        /// Max head-UT delta (seconds) between the live head and the cached head for the held position
        /// to remain valid. Bounds the spatial error of holding a stale on-line point: a head that has
        /// advanced more than a few seconds of recorded time is no longer near the cached point, so the
        /// hold expires rather than glue the marker to a now-distant spot.
        /// </summary>
        internal const double LastGoodMaxHeadUtDeltaSeconds = 5.0;

        /// <summary>
        /// Pure: does a fresh, near-UT last-good on-line position exist for this recording? Returns true
        /// (and the held world position + leg index) when a cached entry exists, is within
        /// <see cref="LastGoodMaxFrameAge"/> frames, and the live head is within
        /// <see cref="LastGoodMaxHeadUtDeltaSeconds"/> of the cached head. False (deep fallback) when no
        /// cache entry, the entry is stale by frame, or the head has moved too far in UT. xUnit-testable
        /// (no Unity calls); the cache is seeded via the dictionary the ride path stamps.
        /// </summary>
        private static bool TryHoldLastGood(
            string recordingId, double headUT, int frame, out Vector3 worldPos, out int legIndex)
        {
            worldPos = Vector3.zero;
            legIndex = -1;
            if (string.IsNullOrEmpty(recordingId)) return false;
            if (!lastGoodOnLine.TryGetValue(recordingId, out var lg)) return false;
            if (frame - lg.frame > LastGoodMaxFrameAge) return false;
            if (System.Math.Abs(headUT - lg.headUT) > LastGoodMaxHeadUtDeltaSeconds) return false;
            worldPos = lg.worldPos;
            legIndex = lg.legIndex;
            return true;
        }

        /// <summary>
        /// Pure classifier: is the head inside the recording's overall leg span (between the first leg's
        /// start and the last leg's end) yet outside every individual leg - i.e. in an inter-leg GAP
        /// (e.g. a connector/deorbit seam between two drawn legs)? Distinguishes the held-hold case (gap
        /// inside the span) from a genuine orbital-phase exit (head before the first leg or past the last
        /// leg), which must fall through to the deep fallback. xUnit-testable without Unity.
        /// </summary>
        internal static bool IsHeadInInterLegGap(
            double firstStartUT, double lastEndUT, double headUT)
            => headUT >= firstStartUT && headUT <= lastEndUT;

        /// <summary>
        /// Test-only seam: stamps the last-good on-line cache the ride path populates so
        /// <see cref="TryHoldLastGood"/> can be exercised from xUnit (the ride itself needs Unity to
        /// compute the world position). Cleared by <see cref="Clear"/>.
        /// </summary>
        internal static void SetLastGoodOnLineForTesting(
            string recordingId, Vector3 worldPos, double headUT, int frame, int legIndex)
        {
            if (string.IsNullOrEmpty(recordingId)) return;
            lastGoodOnLine[recordingId] = new LastGoodOnLine
            {
                worldPos = worldPos,
                headUT = headUT,
                frame = frame,
                legIndex = legIndex
            };
        }

        /// <summary>
        /// Test-only query of <see cref="TryHoldLastGood"/> (which is private because the live ride path
        /// is the only caller). Lets the xUnit suite assert the frame-age + head-UT bounds.
        /// </summary>
        internal static bool TryHoldLastGoodForTesting(
            string recordingId, double headUT, int frame, out Vector3 worldPos, out int legIndex)
            => TryHoldLastGood(recordingId, headUT, frame, out worldPos, out legIndex);

        /// <summary>
        /// PURE ownership dispatch (8b.2 / 8e S3b / 8e S4): is the polyline the authoritative owner of this
        /// recording's non-orbital phase? Returns the actual-draw drew-set membership
        /// (<paramref name="inDrewSet"/>). 8e S3b DELETED the legacy ownership set and the gate-on-union /
        /// gate-off-only branches: the S3a gate + an in-game re-fly proved
        /// <see cref="drewNonOrbitalLegRecordings"/> byte-identical + a superset of the legacy set, so it is
        /// the SOLE ownership source. 8e S4 dropped the director-drive gate entirely (the
        /// <c>directorDriveGateOn</c> param is gone), so this is now an identity over the membership bit.
        /// The drew set is actual-draw-only, so this is true iff SOME leg actually drew -> proto hidden iff
        /// polyline drew. Unit-testable without Unity (the membership bit is passed in).
        /// </summary>
        internal static bool ResolveNonOrbitalLegOwnership(bool inDrewSet)
            => inDrewSet;

        /// <summary>
        /// True when the trajectory polyline is currently drawing a non-orbital
        /// leg for <paramref name="recordingId"/>. Read by <c>GhostMapPresence</c> to
        /// suppress the overlapping proto-vessel orbit line for that phase.
        /// 8b.2 / 8e S3b: the SOLE source is the actual-draw-published
        /// <see cref="drewNonOrbitalLegRecordings"/> (the legacy ownership set was deleted in S3b);
        /// <see cref="ResolveNonOrbitalLegOwnership"/> returns its membership gate-independently.
        /// </summary>
        internal static bool IsRenderingNonOrbitalLeg(string recordingId)
        {
            if (recordingId == null) return false;
            return ResolveNonOrbitalLegOwnership(
                drewNonOrbitalLegRecordings.Contains(recordingId));
        }

        /// <summary>
        /// PURE set diff for the polyline render-EVENT appear/disappear emit: given the recordings drawn on
        /// the PREVIOUS frame (<paramref name="previous"/>) and the recordings drawn on the CURRENT frame
        /// (<paramref name="current"/>), fills <paramref name="appeared"/> (in current, not previous) and
        /// <paramref name="disappeared"/> (in previous, not current), each sorted Ordinal for deterministic
        /// output. The output lists are cleared first. No Unity dependency, so it is unit-testable directly;
        /// the caller owns updating <paramref name="previous"/> := <paramref name="current"/> after emitting.
        /// </summary>
        internal static void DiffDrawnSets(
            HashSet<string> previous, HashSet<string> current,
            List<string> appeared, List<string> disappeared)
        {
            appeared.Clear();
            disappeared.Clear();
            if (current != null)
                foreach (string id in current)
                    if (previous == null || !previous.Contains(id))
                        appeared.Add(id);
            if (previous != null)
                foreach (string id in previous)
                    if (current == null || !current.Contains(id))
                        disappeared.Add(id);
            appeared.Sort(StringComparer.Ordinal);
            disappeared.Sort(StringComparer.Ordinal);
        }

        /// <summary>
        /// Diff <paramref name="current"/> against <paramref name="previous"/> and emit ONE
        /// <c>phase=PolylineLegChange event=appear|disappear</c> MapRenderTrace structural EVENT per
        /// recording that started / stopped drawing on <paramref name="surface"/> (Polyline = current
        /// element, PolylineForwardArc = the forward chain ahead of the icon), then copy current into
        /// previous so the next frame diffs against this one. Each line carries the recordingId in BOTH the
        /// pid= and recId= slots (the polyline is a recording-scoped surface that may have no proto ghost,
        /// so the recordingId IS its identity). Emitted as IMPORTANT (no detailed-window open) so the
        /// appear/disappear shows even with Verbose off, matching the GhostCreated/GhostDestroyed tier. The
        /// whole method is IsEnabled-gated by the caller; the diff scratch lists are reused. Read-only.
        /// </summary>
        private static void EmitPolylineDrawSetChanges(
            MapRenderTrace.RenderSurface surface,
            HashSet<string> previous, HashSet<string> current, double currentUT)
        {
            DiffDrawnSets(previous, current, diffAppearedScratch, diffDisappearedScratch);
            if (diffAppearedScratch.Count == 0 && diffDisappearedScratch.Count == 0)
                return; // steady state: nothing changed, no copy needed (sets are equal)
            for (int i = 0; i < diffAppearedScratch.Count; i++)
                EmitPolylineLegChange(surface, diffAppearedScratch[i], "appear", currentUT);
            for (int i = 0; i < diffDisappearedScratch.Count; i++)
                EmitPolylineLegChange(surface, diffDisappearedScratch[i], "disappear", currentUT);
            previous.Clear();
            foreach (string id in current)
                previous.Add(id);
        }

        /// <summary>Test seam: drive the appear/disappear <c>PolylineLegChange</c> EVENT diff directly (the
        /// production caller is the Driver's per-frame onPreCull / walk, which needs a live render frame). Used
        /// by the Phase-8 tracer-coverage matrix in-game test to prove the Polyline / PolylineForwardArc
        /// appear/disappear wiring lights up end-to-end. Clears the per-(surface, recordingId, event)
        /// wall-clock floor first so a repeated in-game run is not suppressed by a stale onset timestamp.
        /// No-op emit when tracing is off (the underlying structural emit early-returns).</summary>
        internal static void EmitPolylineDrawSetChangesForTesting(
            MapRenderTrace.RenderSurface surface,
            HashSet<string> previous, HashSet<string> current, double currentUT)
        {
            legChangeLastEmitRealtime.Clear();
            EmitPolylineDrawSetChanges(surface, previous, current, currentUT);
        }

        private static void EmitPolylineLegChange(
            MapRenderTrace.RenderSurface surface, string recordingId, string ev, double currentUT)
        {
            // Soft per-(surface, recordingId, event) wall-clock floor so a per-frame oscillating draw-set
            // (warp-reseed-lag line-blink) collapses to onset + a ~1/s heartbeat instead of flooding Info
            // every frame; a stable appear/disappear emits immediately (floor long expired). The caller copies
            // previous:=current regardless, so a throttled transition is simply not re-detected. Only reached
            // when MapRenderTrace.IsEnabled (caller-gated), so disabled play pays nothing for this.
            string key = ((int)surface).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "|" + recordingId + "|" + ev;
            double now = UnityEngine.Time.realtimeSinceStartup;
            if (legChangeLastEmitRealtime.TryGetValue(key, out double last)
                && now - last < LegChangeEmitMinIntervalSeconds)
                return;
            // Warp safeguard: bound the dict before inserting a NEW key (recordingIds are stable, so this is
            // belt-and-braces; clearing only drops other keys' floors, harmless - they re-emit on next change).
            if (!legChangeLastEmitRealtime.ContainsKey(key) && legChangeLastEmitRealtime.Count >= 4096)
                legChangeLastEmitRealtime.Clear();
            legChangeLastEmitRealtime[key] = now;

            MapRenderTrace.EmitStructural(
                "PolylineLegChange", surface, recordingId, currentUT, currentUT,
                windowSeconds: 0.0,
                details: string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "event={0} scene={1} warp={2:F0}x",
                    ev, HighLogic.LoadedScene, TimeWarp.CurrentRate),
                recId: recordingId);
        }

        /// <summary>
        /// Test-only seam (8b.2 / 8e S3b): stamps the per-frame actual-draw ownership publish set the
        /// Driver's <c>LateUpdate</c> populates (Unity-coupled, not reachable from xUnit), so the gate-read
        /// + dispatch in <see cref="IsRenderingNonOrbitalLeg"/> can be exercised end-to-end. Mirrors the
        /// real publish: <paramref name="inDrewSet"/> models the actual-draw publish (any leg drew, owned
        /// or Driver-direct). 8e S3b deleted the legacy set, so the second parameter is gone. Cleared by
        /// <see cref="Clear"/>.
        /// </summary>
        internal static void SetOwnershipPublishForTesting(
            string recordingId, bool inDrewSet)
        {
            if (string.IsNullOrEmpty(recordingId)) return;
            if (inDrewSet) drewNonOrbitalLegRecordings.Add(recordingId);
            else drewNonOrbitalLegRecordings.Remove(recordingId);
        }

        /// <summary>
        /// Test-only seam: injects a single 2-point polyline leg into the cache so the marker-ride fallback
        /// gate (<see cref="ResolveUndrawnLegFallback"/> inside
        /// <see cref="TryAnchorMarkerToPolyline(string,double,out Vector3,out MapRenderTrace.MarkerRideReason,out int)"/>)
        /// can be exercised end-to-end from an in-game test (the ride itself needs Unity's
        /// <c>Time.frameCount</c> / <c>ScaledSpace</c>, so this cannot run in xUnit). The leg spans
        /// <paramref name="startUT"/>..<paramref name="endUT"/> with the given conic-anchor state and
        /// last-drawn frame; pass <paramref name="lastDrawnFrame"/> != the current frame to model the
        /// "leg not drawn this frame" branch. Cleared by <see cref="Clear"/>.
        /// </summary>
        internal static void SetLegForTesting(
            string recordingId, double startUT, double endUT, bool wasAnchored, int lastDrawnFrame)
        {
            if (string.IsNullOrEmpty(recordingId)) return;
            var leg = new LegPolyline
            {
                lats = new double[] { 0.0, 0.0 },
                lons = new double[] { 0.0, 0.0 },
                alts = new double[] { 0.0, 0.0 },
                recordedUTs = new double[] { startUT, endUT },
                scratchScaledSpace = new Vector3[] { Vector3.zero, Vector3.one },
                bodyName = null,
                startUT = startUT,
                endUT = endUT,
                vectorLine = null,
                lastDrawnFrame = lastDrawnFrame,
                wasAnchored = wasAnchored,
                lineMode = 0,
            };
            polylineCache[recordingId] = new LegPolylineSet { legs = new[] { leg }, contentHash = 0 };
        }

        /// <summary>
        /// Test-only accessor: returns the live cache dictionary so the
        /// xUnit suite can assert on cache contents after a refresh.
        /// </summary>
        internal static IReadOnlyDictionary<string, LegPolylineSet> CacheForTesting => polylineCache;

        /// <summary>
        /// Test-only accessor: returns the number of cached entries.
        /// </summary>
        internal static int CacheCountForTesting => polylineCache.Count;

        /// <summary>
        /// OBSERVABILITY (descent render-window tracing): emit one line per RENDERED polyline object - every
        /// cached leg AND forward-arc whose Vectrosity <c>VectorLine</c> is live this frame (active, or drawn
        /// this frame). These VectorLines are NOT in <see cref="GhostMapPresence.ghostMapVesselPids"/>, so the
        /// per-pid <see cref="MapRenderProbe"/> never sees them - they are exactly the "rendered but not logged"
        /// extra trajectory lines the per-frame map-scene snapshot exists to catch. <c>staleActive</c> flags a
        /// VectorLine still <c>active</c> but NOT drawn this frame (a one-shot Draw3D mesh that the deactivation
        /// sweep has not yet hidden - a candidate stray line). Walks both static caches; Unity-coupled (reads
        /// <c>VectorLine.active</c>), so it is driven only from the live probe, never xUnit. Each line is handed
        /// to <paramref name="emit"/>; the caller owns the surface/prefix.
        /// </summary>
        internal static void EmitRenderedPolylineSnapshot(int currentFrame, System.Action<string> emit)
        {
            if (emit == null) return;
            var ic = System.Globalization.CultureInfo.InvariantCulture;

            // FRAME-LAG NOTE: this runs from MapRenderProbe.LateUpdate, but Draw3D + the per-frame deactivation
            // sweep (RunDeactivationSweep) run LATER, in OnMapCameraPreCull. So a leg redrawn EVERY frame is
            // stamped lastDrawnFrame == currentFrame-1 when read here (the prior frame's preCull), never
            // currentFrame. "drawnRecently" therefore means lastDrawnFrame >= currentFrame-1 (the steady-state
            // baseline for a continuously-drawn leg), and staleActive flags only a GENUINELY lingering mesh:
            // active but NOT redrawn for 2+ frames (lastDrawnFrame < currentFrame-1) - the case the sweep should
            // have hidden. (The earlier `== currentFrame` form flagged every redrawn leg as stale: 3825 false
            // positives in the 2026-06-21 capture.)
            foreach (var kv in polylineCache)
            {
                LegPolyline[] legs = kv.Value.legs;
                if (legs == null) continue;
                for (int l = 0; l < legs.Length; l++)
                {
                    LegPolyline leg = legs[l];
                    bool hasLine = leg.vectorLine != null;
                    bool active = hasLine && leg.vectorLine.active;
                    bool drawnRecently = leg.lastDrawnFrame >= currentFrame - 1;
                    if (!active && !drawnRecently) continue; // not rendered recently
                    emit(string.Format(ic,
                        "polyline-leg rec={0} leg={1}/{2} body={3} ut=[{4}-{5}] active={6} drawnRecently={7} "
                        + "staleActive={8} pts={9} lastDrawnFrame={10}",
                        kv.Key, l, legs.Length, leg.bodyName ?? "(null)",
                        leg.startUT.ToString("F1", ic), leg.endUT.ToString("F1", ic),
                        active, drawnRecently, active && !drawnRecently, leg.PointCount, leg.lastDrawnFrame));
                }
            }

            foreach (var kv in forwardArcCache)
            {
                ForwardArc[] arcs = kv.Value.arcs;
                if (arcs == null) continue;
                for (int a = 0; a < arcs.Length; a++)
                {
                    ForwardArc arc = arcs[a];
                    bool hasLine = arc.vectorLine != null;
                    bool active = hasLine && arc.vectorLine.active;
                    bool drawnRecently = arc.lastDrawnFrame >= currentFrame - 1;
                    if (!active && !drawnRecently) continue; // not rendered recently
                    emit(string.Format(ic,
                        "forward-arc rec={0} arc={1}/{2} body={3} ut=[{4}-{5}] active={6} drawnRecently={7} "
                        + "staleActive={8} lastDrawnFrame={9}",
                        kv.Key, a, arcs.Length, arc.bodyName ?? "(null)",
                        arc.startUT.ToString("F1", ic), arc.endUT.ToString("F1", ic),
                        active, drawnRecently, active && !drawnRecently, arc.lastDrawnFrame));
                }
            }
        }

        /// <summary>
        /// A1 cutover-instrument (design §14, ParityMode.Synthesized polyline leg): for every cached leg
        /// whose Vectrosity <c>VectorLine</c> is RENDERED this frame, hand the caller two flat
        /// <c>[x0,y0,z0, x1,...]</c> XYZ-triple arrays in ONE consistent BODY-RELATIVE WORLD metres frame:
        /// the RENDERED polyline geometry (the leg's live <c>VectorLine.points3</c>, each scaled-space
        /// vertex inverted back to a body-relative world offset by
        /// <see cref="Parsek.MapRender.RenderGeometrySampler.RenderedScaledVertexToBodyRelative"/>) and the
        /// RECORDED reference geometry - plus the leg's float-quantization METROLOGY FLOOR in metres (the
        /// last callback arg; see <see cref="Parsek.MapRender.RenderGeometrySampler.DrawnVertexQuantizationFloorMeters"/>)
        /// the caller clamps its tolerance up to.
        ///
        /// <para><b>FRAME-COHERENT capture (the false-drift fix, take 2).</b> BOTH sides come from the
        /// DRAW's own frame: <see cref="TryDrawLeg"/> snapshots the body-relative offsets it builds the
        /// vertices from (<see cref="LegPolyline.parityRecordedRel"/>) plus the exact centre float it added
        /// in (<see cref="LegPolyline.parityDrawCentreScaled"/>), tracing-gated; this capture inverts
        /// <c>points3</c> against that RECORDED centre and diffs against that RECORDED snapshot. It never
        /// re-reads <c>GetWorldSurfacePosition</c> / <c>body.position</c> / <c>scaledXform.position</c> at
        /// capture time: the Driver draws in a different part of the frame than the probe (exec order -50
        /// vs +10000, with ScaledSpace's own update in between), so a capture-time re-read skews the
        /// reference by one frame of body translation AND rotation, scaled by warp - the live FP signature
        /// this replaced (a ~280 m 1x floor = one frame of the body's heliocentric motion, with 16-24 km
        /// spikes under warp; the FIRST capture inverse additionally strobed through
        /// <c>ScaledSpace.totalOffset</c>). Subtracting the identical recorded centre float also cancels
        /// the vertex sum's quantization of the centre exactly. A leg whose snapshot is missing or from a
        /// different draw than its <c>points3</c> (<see cref="LegPolyline.parityDrawFrame"/> !=
        /// <see cref="LegPolyline.lastDrawnFrame"/>: the absolute-fallback draw branch, or a leg not yet
        /// redrawn since tracing came on) is SKIPPED this frame (batch-counted, rate-limited summary) and
        /// captured on its next traced draw.</para>
        ///
        /// <para>CONIC-ANCHORED legs are SKIPPED (a known, stated limitation). A leg the draw rotated ~96 deg
        /// onto the bracketing conic seam (<see cref="TryAnchorLegToConicSeam"/> returned true, recorded in
        /// <see cref="LegPolyline.wasAnchored"/>) draws its <c>points3</c> INTENTIONALLY far from its raw
        /// recorded surface track, so diffing rendered-<c>points3</c> against the raw <c>lats/lons/alts</c>
        /// would report ~body-radius drift on EVERY correctly-anchored leg (a false positive) while a leg
        /// that FAILED to anchor would read ~0 - exactly backwards. The anchor transform is a per-point Slerp
        /// of two live-computed <c>FromToRotation</c> quaternions, not a cheaply recoverable single transform
        /// to rebuild the reference in, so this lens deliberately covers ONLY non-anchored body-fixed legs
        /// (descent / atmospheric / surface - the descent re-stitch the audit cares about). For those the
        /// rendered <c>points3</c> ARE the raw body-fixed points, so rendered == recorded by construction and
        /// any genuine mis-draw drifts. The two arrays use the SAME index set (the first
        /// <c>min(points3.Count, leg.PointCount)</c> samples), so the diff is a like-for-like recorded-vs-
        /// rendered comparison even though the oracle itself is count-independent.</para>
        ///
        /// <para>Unity-coupled (reads <c>VectorLine.points3</c> + <c>ScaledSpace.ScaleFactor</c>; the
        /// reference and centre come from the draw-frame snapshot, no capture-time body reads), so it is
        /// driven ONLY from the live <see cref="MapRenderProbe"/> (never xUnit) and only while tracing is
        /// on. NaN/Inf-safe: a non-finite vertex or snapshot value is written through unchanged (the oracle
        /// filters non-finite points and never raises a false anomaly). Each drawn leg is handed to
        /// <paramref name="onLeg"/> as
        /// <c>(recordingId, legIndex, legCount, bodyName, renderedBodyRelFlat, recordedBodyRelFlat,
        /// quantizationFloorMeters)</c>; the caller owns the oracle diff + emit so the decision math stays
        /// pure/unit-testable.</para>
        /// </summary>
        internal static void CaptureRenderedVsRecordedLegGeometry(
            int currentFrame,
            System.Action<string, int, int, string, double[], double[], double> onLeg)
        {
            if (onLeg == null) return;

            int skippedNoSnapshot = 0;
            foreach (var kv in polylineCache)
            {
                LegPolyline[] legs = kv.Value.legs;
                if (legs == null) continue;
                for (int l = 0; l < legs.Length; l++)
                {
                    LegPolyline leg = legs[l];
                    VectorLine line = leg.vectorLine;
                    if (line == null) continue;
                    bool active = line.active;
                    bool drawnRecently = leg.lastDrawnFrame >= currentFrame - 1;
                    if (!active && !drawnRecently) continue; // not rendered recently

                    // SKIP conic-anchored legs: a leg the last draw rotated onto the bracketing conic seam
                    // (TryAnchorLegToConicSeam returned true, stamped here) draws its points3 ~96 deg off the
                    // RAW recorded surface track on PURPOSE, so diffing rendered-points3 against the raw
                    // recorded snapshot would report ~body-radius drift on EVERY correctly-anchored leg (false
                    // positive) - and a leg that FAILED to anchor (the real regression) reads ~0 (exactly
                    // backwards). The anchor rotation is a per-point Slerp of two live-computed FromToRotation
                    // quaternions, not a cheaply recoverable single transform, so this lens does NOT cover
                    // anchored vacuum-maneuver legs - a STATED limitation. It validates ONLY non-anchored
                    // body-fixed legs (descent / atmospheric / surface - the descent re-stitch the audit cares
                    // about). The skip decision is the pure ShouldCaptureLegForParity (xUnit-covered).
                    if (!ShouldCaptureLegForParity(leg.wasAnchored)) continue;

                    var points3 = line.points3;
                    if (points3 == null || points3.Count == 0) continue;

                    // FRAME-COHERENT reference: the diff runs against the parity snapshot the DRAW itself
                    // took (leg.parityRecordedRel + parityDrawCentreScaled, filled inside the vertex-build
                    // loop while tracing is on), NEVER a capture-time re-read. The draw and this probe run
                    // in different parts of the frame: re-evaluating GetWorldSurfacePosition/body.position/
                    // scaledXform.position here skews the reference by a frame of body translation +
                    // rotation (x warp) - the pre-fix ~280m@1x floor and 16-24km warp spikes were exactly
                    // that artifact. parityDrawFrame must EQUAL lastDrawnFrame so the snapshot and points3
                    // came from the SAME draw (a leg drawn via the absolute fallback, or tracing enabled
                    // mid-flight before this leg redrew, is skipped this frame and captured on its next
                    // draw). Subtracting the recorded centre float also cancels the vertex-sum quantization
                    // of the centre exactly.
                    if (leg.parityRecordedRel == null
                        || leg.parityDrawFrame != leg.lastDrawnFrame
                        || leg.parityRecordedRel.Length < 3)
                    {
                        skippedNoSnapshot++;
                        continue;
                    }

                    // Like-for-like index set: points3 hold this leg's drawn samples 1:1 with the snapshot
                    // (both written from the same loop). Diff over the common prefix so a transiently-
                    // resized line never misaligns.
                    int m = leg.PointCount;
                    if (m > points3.Count) m = points3.Count;
                    if (m > leg.parityRecordedRel.Length / 3) m = leg.parityRecordedRel.Length / 3;
                    if (m <= 0) continue;

                    Vector3d bodyCentreScaled = (Vector3d)leg.parityDrawCentreScaled;
                    double scaleFactor = ScaledSpace.ScaleFactor;
                    var renderedFlat = new double[m * 3];
                    var recordedFlat = new double[m * 3];
                    for (int i = 0; i < m; i++)
                    {
                        // RENDERED: the drawn scaled-space vertex float -> body-relative world offset,
                        // inverted against the SAME centre float the draw added in (cancels the centre,
                        // totalOffset, and the centre's own float quantization outright).
                        Vector3d renderedRel =
                            Parsek.MapRender.RenderGeometrySampler.RenderedScaledVertexToBodyRelative(
                                points3[i], bodyCentreScaled, scaleFactor);
                        renderedFlat[i * 3] = renderedRel.x;
                        renderedFlat[i * 3 + 1] = renderedRel.y;
                        renderedFlat[i * 3 + 2] = renderedRel.z;

                        // RECORDED reference: the draw-frame snapshot of the leg's body-fixed surface
                        // track, body-relative (what the draw ACTUALLY encoded into the vertices).
                        recordedFlat[i * 3] = leg.parityRecordedRel[i * 3];
                        recordedFlat[i * 3 + 1] = leg.parityRecordedRel[i * 3 + 1];
                        recordedFlat[i * 3 + 2] = leg.parityRecordedRel[i * 3 + 2];
                    }

                    // METROLOGY FLOOR: the drawn Vector3 vertex quantizes at the CENTRE's float ulp (x
                    // scaleFactor in real metres) - on a body far from the scaled origin (a non-focused
                    // body in the TS) that noise can exceed a small leg's 0.1% scale tolerance. The oracle
                    // clamps its tolerance up to this floor: sub-floor deviations are float noise, not
                    // geometry; a real mis-draw above the floor still fires.
                    double noiseFloor =
                        Parsek.MapRender.RenderGeometrySampler.DrawnVertexQuantizationFloorMeters(
                            bodyCentreScaled, scaleFactor);

                    onLeg(kv.Key, l, legs.Length, leg.bodyName ?? "(null)", renderedFlat, recordedFlat,
                        noiseFloor);
                }
            }

            // Batch summary (convention: one line after the loop, never per-item). Rate-limited: this runs
            // per frame while tracing is on; a sustained skip usually just means legs drawn before tracing
            // came on.
            if (skippedNoSnapshot > 0)
                ParsekLog.VerboseRateLimited(Tag, "parity-capture-skip",
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Leg parity capture skipped {0} leg(s) with no same-draw snapshot (absolute-fallback "
                        + "draw, or drawn before tracing enabled; captured on their next traced draw)",
                        skippedNoSnapshot),
                    10.0);
        }

        /// <summary>
        /// One body-coherent contiguous polyline leg covering a non-orbital
        /// phase of the recording. Built ONCE at cache-build time from
        /// either a TrackSection's per-frame sample list (Absolute /
        /// Relative-bodyFixed) or a body-grouped run of the flat
        /// Recording.Points fallback (§1.3).
        /// </summary>
        internal struct LegPolyline
        {
            /// <summary>
            /// M recorded body-fixed latitudes (degrees). Paired index-wise
            /// with <see cref="lons"/> / <see cref="alts"/>. Each (lat, lon, alt)
            /// triple is converted to a scaled-space position each frame via
            /// <c>CelestialBody.GetWorldSurfacePosition</c> (the same call
            /// ParsekTrackingStation.cs:1199 uses for the atmospheric marker, so the
            /// polyline lands exactly where a marker would), built relative to the
            /// scaled body centre so it stays strobe-free under time warp - see the
            /// "warp-strobe fix" geometry comment in the draw loop for the full rationale.
            /// </summary>
            public double[] lats;

            /// <summary>M recorded body-fixed longitudes (degrees).</summary>
            public double[] lons;

            /// <summary>M recorded body-fixed altitudes (metres above body radius).</summary>
            public double[] alts;

            /// <summary>
            /// M recorded UTs (paired index-wise with lats/lons/alts). Used by the conic-anchor
            /// (<see cref="TryAnchorLegToConicSeam"/>) to interpolate the seam-calibrated rotation across
            /// the leg by recorded-time fraction (the body's rotation accrued over the leg's own span is
            /// sub-2 deg but real). Null on legs built before this field existed -> the anchor falls back
            /// to index-fraction interpolation.
            /// </summary>
            public double[] recordedUTs;

            /// <summary>
            /// M-element scratch buffer for per-frame ScaledSpace output
            /// (zero-alloc hot path; the Driver copies into this leg's own
            /// VectorLine.points3 each frame).
            /// </summary>
            public Vector3[] scratchScaledSpace;

            /// <summary>Name of the CelestialBody the leg's lat/lon/alt
            /// were sampled against (for live body.position lookup).</summary>
            public string bodyName;

            /// <summary>Leg's first sample's recorded UT.</summary>
            public double startUT;

            /// <summary>Leg's last sample's recorded UT.</summary>
            public double endUT;

            /// <summary>
            /// This leg's own <c>LineType.Continuous</c> Vectrosity
            /// VectorLine. One VectorLine PER leg (NOT one shared line per
            /// recording): a shared line drawn once per leg via
            /// <c>drawStart</c>/<c>drawEnd</c> range slicing does NOT work,
            /// because <c>VectorLine.Draw3D()</c> zeroes every vertex OUTSIDE
            /// the current [drawStart, drawEnd] window on each call, so only
            /// the last leg drawn would survive. Each leg's line holds exactly
            /// this leg's points and is drawn full-range. Null until the first
            /// Driver tick inflates it.
            /// </summary>
            public VectorLine vectorLine;

            /// <summary>
            /// <c>Time.frameCount</c> of the last frame the Driver drew this
            /// leg. The per-frame deactivation sweep compares this against the
            /// current frame and sets <c>vectorLine.active = false</c> for any
            /// leg NOT drawn this frame, so a stale Vectrosity mesh does not
            /// linger on screen when the leg stops drawing (loop-hidden,
            /// suppressed, body missing, or the recording removed from
            /// <c>CommittedRecordings</c>): <c>Draw3D()</c> is one-shot and
            /// never hides a line on its own.
            /// </summary>
            public int lastDrawnFrame;

            /// <summary>
            /// Whether the LAST draw of this leg rotated its <see cref="scratchScaledSpace"/> onto the
            /// bracketing conic seam (<see cref="TryAnchorLegToConicSeam"/> returned true). Stamped every
            /// draw in <c>TryDrawLeg</c> and persisted via the same struct write-back as
            /// <see cref="lastDrawnFrame"/>. The marker-ride fallback reads it: a conic-anchored leg's drawn
            /// line sits ~96 deg off the body-fixed head under the loop shift, so when the leg is not drawn
            /// this frame the marker must HOLD its last on-line position to stay on the line; a NON-anchored
            /// body-fixed leg (descent / atmospheric / surface) draws the raw body-fixed points, so the
            /// caller's fresh body-fixed head is already on the line and current and is preferred over a
            /// stale hold. See <see cref="ResolveUndrawnLegFallback"/>.
            /// </summary>
            public bool wasAnchored;

            /// <summary>
            /// Map-line mode (0=unbuilt, 1=3D Draw3D, 2=2D Draw) <see cref="vectorLine"/> was last built
            /// for. Bug 1: Vectrosity's in-place VectorObject3D&lt;-&gt;VectorObject2D swap leaves the 2D
            /// canvas Graphic non-rendering, so the line is rebuilt when this differs from the live mode
            /// (stock MakeLine-on-flip). See <c>RebuildMapLineIfModeChanged</c>.
            /// </summary>
            public int lineMode;

            /// <summary>
            /// TRACING-ONLY (null in normal play): the parity-lens REFERENCE snapshot taken IN THE DRAW's
            /// own frame - the flat <c>[x,y,z]*M</c> body-relative world offsets (<c>world - body.position</c>)
            /// the draw itself computed the vertices from. The capture
            /// (<see cref="CaptureRenderedVsRecordedLegGeometry"/>) diffs the drawn <c>points3</c> floats
            /// against THIS, never a re-evaluation at capture time: the draw runs in a different part of the
            /// frame than the probe, and re-computing <c>GetWorldSurfacePosition</c>/<c>body.position</c> at
            /// capture time skews the reference by a frame of body translation + rotation (x warp - the
            /// observed ~280m@1x floor and 16-24km warp spikes, pure artifact). Allocated / refilled per draw
            /// only while <c>MapRenderTrace.IsEnabled</c>.
            /// </summary>
            public double[] parityRecordedRel;

            /// <summary>
            /// TRACING-ONLY: the exact <c>scaledXform.position</c> float the draw added into every vertex
            /// this leg's <see cref="parityRecordedRel"/> snapshot was taken with. The capture inverts
            /// <c>points3</c> against THIS value (not a live re-read): ScaledSpace moves the body's scaled
            /// transform between the draw and the probe, and even at rest the two reads quantize differently
            /// - subtracting the identical float cancels the centre exactly.
            /// </summary>
            public Vector3 parityDrawCentreScaled;

            /// <summary>
            /// <c>Time.frameCount</c> of the draw that filled <see cref="parityRecordedRel"/> /
            /// <see cref="parityDrawCentreScaled"/>. The capture requires it to EQUAL
            /// <see cref="lastDrawnFrame"/> (the same draw that wrote <c>points3</c>), so a leg drawn via
            /// the no-scaled-body absolute fallback (which never stamps) is skipped. NOTE: an early-out
            /// BETWEEN the geometry build and the draw stamp is NOT covered by staleness alone - the fill
            /// mutates the aliased snapshot array in place and the stale stamps can be pairwise equal - so
            /// the anchor-reject early-out in <c>TryDrawLeg</c> EXPLICITLY resets this to 0 ("never
            /// filled") on its exit path; the next successful draw refills and restamps.
            /// </summary>
            public int parityDrawFrame;

            /// <summary>Number of points in this leg (M).</summary>
            public int PointCount => lats != null ? lats.Length : 0;
        }

        /// <summary>
        /// One recording's complete polyline data: an array of body-coherent
        /// legs (each owning its own Vectrosity VectorLine) plus the cache
        /// invariant hash. Stored by RecordingId in <see cref="polylineCache"/>.
        /// </summary>
        internal struct LegPolylineSet
        {
            public LegPolyline[] legs;

            /// <summary>
            /// Cheap content hash derived from rec.Points.Count,
            /// rec.OrbitSegments.Count, rec.TrackSections.Count, rec.EndUT,
            /// XORed with every Points[i].ut and every
            /// TrackSection.startUT / endUT. A supersede-time re-cut that
            /// preserves the counts still flips the XOR-of-UTs (§1.4).
            /// </summary>
            public long contentHash;
        }

        /// <summary>
        /// One forward orbit (StockConic) segment AHEAD of the icon, sampled into its own Vectrosity
        /// VectorLine (Step 3 C). The arc geometry is BODY-LOCAL Kepler sampling converted to absolute
        /// scaled space via <c>ScaledSpace.LocalToScaledSpace</c> (the ARC pipeline,
        /// GhostOrbitLinePatch.cs:1091, NOT the leg's surface-relative path) - so the forward arc<->leg seam
        /// stays continuous with the current stock arc. One VectorLine PER segment (the same shared-line
        /// drawStart/drawEnd constraint as legs: Draw3D zeroes every vertex outside the window). The arc is
        /// re-sampled only on a cache-key change (element/body/window); the per-frame draw just reactivates
        /// + Draw3D's the cached line (the scaled-space points are absolute and frame-stable for an inertial
        /// conic, so no per-frame recompute is needed - unlike body-fixed legs).
        /// </summary>
        internal struct ForwardArc
        {
            /// <summary>Reference body the conic was sampled against (live body.position + scaledBody lookup).</summary>
            public string bodyName;

            /// <summary>Sampled segment's recorded [startUT, endUT] (diagnostic / draw-order).</summary>
            public double startUT;
            public double endUT;

            /// <summary>
            /// BODY-RELATIVE Kepler sample offsets (metres) = <c>worldSample - body.position</c> captured at
            /// sample time. Frame-STABLE for an inertial conic (the offset is purely the orbit's shape about
            /// its body centre, independent of the body's world motion), so they are sampled ONCE on a cache-
            /// key change and re-projected to scaled space every frame in <see cref="Driver.DrawForwardArc"/>
            /// via <c>scaledBody.position + offset * invScale</c> - the SAME strobe-free geometry the leg draw
            /// uses (see the "warp-strobe fix" comment in <see cref="TryDrawLeg"/>). Baking absolute scaled-
            /// space points once would STROBE under warp (ScaledSpace.totalOffset oscillates every render
            /// frame and a non-registered VectorLine inherits it - the leg's failed approach (a)).
            /// </summary>
            public Vector3d[] bodyRelOffsets;

            /// <summary>This arc's own continuous VectorLine; points3 re-projected to scaled space per frame.</summary>
            public VectorLine vectorLine;

            /// <summary>Per-frame scratch for the scaled-space projection (zero-alloc hot path).</summary>
            public Vector3[] scratchScaledSpace;

            /// <summary><c>Time.frameCount</c> of the last frame this arc drew (deactivation sweep).</summary>
            public int lastDrawnFrame;

            /// <summary>
            /// Map-line mode (0=unbuilt, 1=3D Draw3D, 2=2D Draw) <see cref="vectorLine"/> was last built
            /// for. Bug 1: Vectrosity's in-place VectorObject3D&lt;-&gt;VectorObject2D swap leaves the 2D
            /// canvas Graphic non-rendering, so the line is rebuilt when this differs from the live mode
            /// (stock MakeLine-on-flip). See <c>RebuildMapLineIfModeChanged</c>.
            /// </summary>
            public int lineMode;

            /// <summary>
            /// The OrbitSegment this arc was sampled from (playtest-9 rotating-frame fix): kept so the
            /// offsets can be RESAMPLED IN PLACE when KSP's co-rotating world frame turns under them
            /// (see <see cref="ForwardArcSet.captureInverseRotAngle"/>) without rebuilding the
            /// VectorLines.
            /// </summary>
            public OrbitSegment sourceSegment;
        }

        /// <summary>
        /// One recording's complete FORWARD-ARC line set: the sampled forward conic arcs (each owning its
        /// own VectorLine) plus the cache key that produced them. Re-sampled only when
        /// <see cref="cacheKey"/> changes (<see cref="BuildForwardArcKey"/>:
        /// currentElementIndex|bodyName|reaimWindowIndex). Stored by RecordingId in
        /// <see cref="forwardArcCache"/>.
        /// </summary>
        internal struct ForwardArcSet
        {
            public ForwardArc[] arcs;

            /// <summary>The (selectedSegmentIndices|reaimWindowIndex) key (see
            /// <see cref="BuildForwardArcKey"/>) the arcs were sampled for.</summary>
            public string cacheKey;

            /// <summary>
            /// <c>Planetarium.InverseRotAngle</c> at sample time (playtest-9 rotating-frame fix). At
            /// LOW altitude KSP's world frame CO-ROTATES with the main body; a "frame-stable" inertial
            /// offset captured once then effectively rotates WITH the planet (the true inertial orbit
            /// counter-rotates in that frame), freezing the arc/bridge geometry against the body-fixed
            /// legs until the next cache rebuild - the playtest-9 "bridges only update at transitions"
            /// symptom. When the live angle drifts from this capture, the offsets are resampled IN
            /// PLACE (orbit evaluation is frame-correct); in inertial-frame eras the angle does not
            /// move, so the cache holds exactly as before.
            /// </summary>
            public double captureInverseRotAngle;
        }

        /// <summary>
        /// PURE drift predicate for the rotating-frame resample (playtest-9): the world frame has
        /// rotated since capture when the two <c>Planetarium.InverseRotAngle</c> readings differ
        /// beyond a tiny epsilon (degrees). xUnit-testable.
        /// </summary>
        internal static bool HasFrameRotationDrift(double captureAngleDeg, double currentAngleDeg)
            => System.Math.Abs(captureAngleDeg - currentAngleDeg) > 1e-7;

        /// <summary>
        /// Refreshes the cache for one recording. Recomputes the cheap
        /// content hash; rebuilds the legs only when the hash changed. When
        /// the hash flips, every leg's previous VectorLine (if any) is
        /// destroyed via <see cref="VectorLine.Destroy(ref VectorLine)"/>
        /// before the rebuild so no Vectrosity GameObjects leak.
        ///
        /// The per-leg VectorLines themselves are constructed lazily on the
        /// next Driver LateUpdate: building them here would create Vectrosity
        /// GameObjects from xUnit (no Unity GameObject backing), which the
        /// unit-test surface forbids. Each leg's <c>vectorLine</c> field is
        /// null after a refresh; the Driver inflates it on first use.
        /// </summary>
        internal static void RefreshForRecording(
            Recording rec, BodySurfaceProvider surface = null, ConicGapSampler gapSampler = null)
        {
            if (rec == null) return;
            string id = rec.RecordingId;
            if (string.IsNullOrEmpty(id)) return;

            long hash = ComputeContentHash(rec);
            if (polylineCache.TryGetValue(id, out var existing)
                && existing.contentHash == hash)
            {
                return; // cache hit
            }

            // Stale Vectrosity objects on a rebuild: destroy every leg's line
            // before overwriting so the GameObjects do not leak.
            if (polylineCache.TryGetValue(id, out var stale))
                DestroyLegLines(stale.legs);

            var legs = BuildLegsForRecording(rec, surface, gapSampler);
            var legArray = legs.ToArray();

            polylineCache[id] = new LegPolylineSet
            {
                legs = legArray,
                contentHash = hash
            };

            ParsekLog.Verbose(Tag,
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Polyline cache refresh: rec={0} legs={1} hash={2:X}",
                    id, legArray.Length, hash));

            // Full leg STRUCTURE (once per content change, not per frame): every leg's index, recorded
            // [start,end] span, length, body, point count, and altitude range - so the complete polyline
            // layout is always in the log and a long non-orbital leg (e.g. the ~100s escape burn the icon
            // dwells on) is identifiable by span + high altitude (orbital-vacuum) without a screenshot.
            if (legArray.Length > 0)
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < legArray.Length; i++)
                {
                    var lg = legArray[i];
                    int mi = lg.PointCount;
                    sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}:[{1:F1}-{2:F1} {3:F0}s {4} pts={5} alt={6:F0}..{7:F0}] ",
                        i, lg.startUT, lg.endUT, lg.endUT - lg.startUT, lg.bodyName ?? "?", mi,
                        mi > 0 ? lg.alts[0] : 0.0, mi > 0 ? lg.alts[mi - 1] : 0.0);
                }
                ParsekLog.Verbose(Tag,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Polyline legs: rec={0} count={1} | {2}", id, legArray.Length, sb.ToString().TrimEnd()));
            }
        }

        /// <summary>
        /// Releases the cache entry for a single recording (chain handoff,
        /// supersede, delete). Destroys every leg's Vectrosity VectorLine via
        /// <see cref="VectorLine.Destroy(ref VectorLine)"/> before dropping
        /// the dict entry so the Vectrosity GameObjects do not leak.
        /// </summary>
        internal static void ReleaseForRecording(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return;
            if (polylineCache.TryGetValue(recordingId, out var set))
                DestroyLegLines(set.legs);
            // Forward-arc lines for this recording (Step 3 C): destroyed + dropped on the SAME lifecycle as
            // the legs so a reused RecordingId never inherits a stale wrong-aimed forward arc. The
            // boundary-overlap secondary (review H1) keeps its arcs under a namespaced key, released here too.
            if (forwardArcCache.TryGetValue(recordingId, out var fwd))
                DestroyForwardArcLines(fwd.arcs);
            forwardArcCache.Remove(recordingId);
            string fwdSecKey = ForwardArcDictKey(recordingId, true);
            if (forwardArcCache.TryGetValue(fwdSecKey, out var fwdSec))
                DestroyForwardArcLines(fwdSec.arcs);
            forwardArcCache.Remove(fwdSecKey);
            // Seam-bridge lines (playtest 6/7): same lifecycle as the forward arcs they connect to.
            // Keys are (recordingId|legIndex|side); collect the recording's keys first (cannot mutate
            // while enumerating). The conic-sample cache entries for this recording drop too (cheap to
            // resample; key prefix is the recording id).
            List<string> staleBridgeKeys = null;
            string bridgeKeyPrefix = recordingId + "|";
            foreach (var kvp in bridgeLineByRecording)
            {
                if (!kvp.Key.StartsWith(bridgeKeyPrefix, StringComparison.Ordinal)) continue;
                (staleBridgeKeys ?? (staleBridgeKeys = new List<string>())).Add(kvp.Key);
            }
            if (staleBridgeKeys != null)
            {
                for (int i = 0; i < staleBridgeKeys.Count; i++)
                {
                    var entry = bridgeLineByRecording[staleBridgeKeys[i]];
                    if (entry.line != null)
                    {
                        var bl = entry.line;
                        VectorLine.Destroy(ref bl);
                    }
                    bridgeLineByRecording.Remove(staleBridgeKeys[i]);
                }
            }
            bridgeConicSampleCache.Clear();
            // Drop the marker hold for this recording so a reused RecordingId (supersede / delete +
            // re-add) never inherits a stale held on-line point.
            lastGoodOnLine.Remove(recordingId);
            if (polylineCache.Remove(recordingId))
            {
                ParsekLog.Verbose(Tag,
                    "Polyline cache release: rec=" + recordingId);
            }
        }

        /// <summary>
        /// Drops the entire cache. Iterates the cached entries to call
        /// <see cref="VectorLine.Destroy(ref VectorLine)"/> on every leg's
        /// VectorLine BEFORE dropping the dict, otherwise the Vectrosity
        /// GameObjects leak. Called from the Driver's <c>onGameStateLoad</c>
        /// handler for the cross-save invariant flush (§1.4 / §6). A stale line
        /// for a recording removed from <c>CommittedRecordings</c> is hidden by
        /// the per-frame deactivation sweep rather than dropped here.
        /// </summary>
        internal static void Clear()
        {
            // Drop the per-frame ownership publish set first (before the empty-cache early-return), so a
            // cross-save flush / test reset never leaves a stale ownership behind. It is re-cleared
            // every LateUpdate, so this is belt-and-suspenders in normal play and the reset hook in tests.
            drewNonOrbitalLegRecordings.Clear();
            // Render-EVENT diff baselines: drop the previous-frame drawn sets on the same cross-save / test
            // reset lifecycle so a new save's first walk does not diff against a prior save's recordings (the
            // recordingIds collide across saves). Re-baselined on the next walk.
            previousDrewNonOrbitalLegRecordings.Clear();
            currentDrewForwardArcRecordings.Clear();
            previousDrewForwardArcRecordings.Clear();
            legChangeLastEmitRealtime.Clear();
            // Drop the marker hold cache on the same cross-save / test-reset lifecycle as the ownership
            // sets so a stale held on-line point never survives a save load or a scene switch.
            lastGoodOnLine.Clear();
            // Forward-arc cache (Step 3 C): destroy every arc's VectorLine + drop the dict on the same
            // cross-save / test-reset lifecycle as the leg cache.
            int fwdDropped = forwardArcCache.Count;
            foreach (var kvp in forwardArcCache)
                DestroyForwardArcLines(kvp.Value.arcs);
            forwardArcCache.Clear();
            // Seam-bridge lines + conic sample cache (playtest 6/7): same cross-save / test-reset
            // lifecycle.
            foreach (var kvp in bridgeLineByRecording)
            {
                var bl = kvp.Value.line;
                if (bl != null)
                    VectorLine.Destroy(ref bl);
            }
            bridgeLineByRecording.Clear();
            bridgeConicSampleCache.Clear();
            if (polylineCache.Count == 0)
            {
                if (fwdDropped > 0)
                    ParsekLog.Verbose(Tag,
                        "Forward-arc cache clear: dropped=" + fwdDropped);
                return;
            }
            int dropped = polylineCache.Count;
            foreach (var kvp in polylineCache)
                DestroyLegLines(kvp.Value.legs);
            polylineCache.Clear();
            ParsekLog.Verbose(Tag,
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Polyline cache clear: dropped={0} fwdArcDropped={1}", dropped, fwdDropped));
        }

        /// <summary>
        /// Destroys every leg's Vectrosity VectorLine in the given array via
        /// <see cref="VectorLine.Destroy(ref VectorLine)"/> so no Vectrosity
        /// GameObjects leak. Null-safe on the array and on per-leg null lines.
        /// </summary>
        private static void DestroyLegLines(LegPolyline[] legs)
        {
            if (legs == null) return;
            for (int i = 0; i < legs.Length; i++)
            {
                var line = legs[i].vectorLine;
                if (line != null)
                    VectorLine.Destroy(ref line);
            }
        }

        /// <summary>
        /// Destroys every forward arc's Vectrosity VectorLine via
        /// <see cref="VectorLine.Destroy(ref VectorLine)"/> so no Vectrosity GameObjects leak. Null-safe on
        /// the array and on per-arc null lines (Step 3 C).
        /// </summary>
        private static void DestroyForwardArcLines(ForwardArc[] arcs)
        {
            if (arcs == null) return;
            for (int i = 0; i < arcs.Length; i++)
            {
                var line = arcs[i].vectorLine;
                if (line != null)
                    VectorLine.Destroy(ref line);
            }
        }

        /// <summary>
        /// Decision for the per-frame deactivation sweep: a cached leg line
        /// should be hidden this frame when it is currently active but was NOT
        /// drawn this frame (loop-hidden, suppressed, body missing, fewer than
        /// two points, or its recording removed from <c>CommittedRecordings</c>).
        /// Pure so the contract is xUnit-testable without a Unity VectorLine.
        /// </summary>
        internal static bool ShouldDeactivateLeg(
            bool currentlyActive, int lastDrawnFrame, int drawFrame)
            => currentlyActive && lastDrawnFrame != drawFrame;

        /// <summary>
        /// Per-leg head-UT visibility gate: a non-orbital leg is drawn only
        /// while the ghost's current playback position (<paramref name="headUT"/>,
        /// in the recording's own timeline) lies within the leg's recorded
        /// [<paramref name="legStartUT"/>, <paramref name="legEndUT"/>] span
        /// (inclusive). Outside that span the leg is skipped and the
        /// deactivation sweep hides it, so the polyline tracks the moving ghost
        /// (visible only where the ghost currently is) instead of painting the
        /// whole recorded path continuously. Pure so the contract is xUnit
        /// testable without Unity.
        /// </summary>
        internal static bool ShouldDrawLegAtHeadUT(
            double legStartUT, double legEndUT, double headUT)
            => headUT >= legStartUT && headUT <= legEndUT;

        /// <summary>
        /// Forward-window overlap leg-gate (Step 3 B', forward-trajectory-render plan): should this
        /// non-orbital leg be drawn as part of the FORWARD chain ahead of the icon? True when the leg's
        /// recorded [<paramref name="legStartUT"/>, <paramref name="legEndUT"/>] span OVERLAPS the forward
        /// render window <c>(<paramref name="forwardWindowStartUT"/>, <paramref name="forwardStopUT"/>]</c>
        /// AND the leg is NOT the CURRENT leg (the one already drawn in full by the head-gated current-leg
        /// pass via <see cref="ShouldDrawLegAtHeadUT"/>). Excluding the current leg keeps the forward pass
        /// PURELY ADDITIVE: the current element renders exactly as today (and publishes ownership exactly as
        /// today), and the forward legs only extend the line ahead. An empty forward range
        /// (<c>forwardStopUT &lt;= forwardWindowStartUT</c>, e.g. the icon sits on a full-loop closed orbit)
        /// draws no forward leg. Pure so the contract is xUnit-testable without Unity.
        /// </summary>
        /// <param name="legStartUT">Leg's first recorded UT.</param>
        /// <param name="legEndUT">Leg's last recorded UT.</param>
        /// <param name="forwardWindowStartUT">The current element's startUT (window lower bound).</param>
        /// <param name="forwardStopUT">The forward stop UT (window upper bound, exclusive of the next-SOI /
        /// full-loop element).</param>
        /// <param name="headUT">The ghost's current playback UT, used to identify and EXCLUDE the current
        /// leg (drawn by the head-gated pass).</param>
        internal static bool ShouldDrawForwardLeg(
            double legStartUT, double legEndUT,
            double forwardWindowStartUT, double forwardStopUT, double headUT)
        {
            // Empty forward range -> nothing forward to draw.
            if (forwardStopUT <= forwardWindowStartUT) return false;
            // The current leg is already drawn in full by the head-gated pass; never double-draw it here.
            if (ShouldDrawLegAtHeadUT(legStartUT, legEndUT, headUT)) return false;
            // Overlap test against the half-open forward window (legStart < stop && legEnd > windowStart).
            return legStartUT < forwardStopUT && legEndUT > forwardWindowStartUT;
        }

        /// <summary>
        /// Chain-aware run membership (playtest-4 chain-boundary fix): the recordings whose legs/arcs
        /// participate in ONE render run. A chain (shared <c>Recording.ChainId</c>) splits one logical
        /// flight across multiple recordings at handoff seams, so a run computed over a single member
        /// cannot span the seam: a launch segment that hands off before reaching orbit carries ZERO
        /// OrbitSegments (no window at all while the icon rides it, so the ascent leg draws alone), and
        /// after the handoff the next member's run only reaches its OWN legs (the ascent leg vanishes) -
        /// exactly the playtest-4 symptom. Returns every committed member of <paramref name="rec"/>'s
        /// chain ordered by StartUT (chain members partition one shared recorded-UT axis, so run-window
        /// boundaries computed over the concatenated effective segments are directly comparable), or just
        /// <paramref name="rec"/> itself for a standalone recording. Fills the caller's scratch list
        /// (clear-and-fill, per-frame hot path). Pure; xUnit-testable without Unity.
        /// </summary>
        internal static void CollectChainRunMembers(
            IReadOnlyList<Recording> committed, Recording rec, int recordingIndex,
            List<(int index, Recording rec)> members,
            GhostPlaybackLogic.LoopUnitSet loopUnits = null)
        {
            if (members == null) return;
            members.Clear();
            if (rec == null) return;
            if (!rec.IsChainRecording || committed == null)
            {
                // DESCENT TRIGGER: a standalone (chain=none) descent member must NOT be added to the run -
                // it is drawn solely by its own trigger-gated primary pass at the re-anchored head. The
                // chain-member exclusion loop below never runs for a non-chain rec, so the exclusion has to
                // be applied on this direct-add path too (the live leak: a chain=none descent member like
                // "Merger Kerman" drew its descent leg on the visible member's loop clock). Leave members
                // empty for it. A non-descent standalone recording is unaffected (guard false).
                if (!IsDescentTriggerMember(recordingIndex, loopUnits))
                    members.Add((recordingIndex, rec));
                return;
            }
            for (int i = 0; i < committed.Count; i++)
            {
                var m = committed[i];
                if (m == null || string.IsNullOrEmpty(m.RecordingId)) continue;
                if (!string.Equals(m.ChainId, rec.ChainId, StringComparison.Ordinal)) continue;
                // DESCENT TRIGGER: a descent-set member renders ONLY via its own trigger-gated primary pass
                // (hidden during the loiter, drawn at the re-anchored head during its rotation-aligned window).
                // Exclude it from the chain RUN entirely: the run draws sibling members' body-fixed legs against
                // the VISIBLE (transfer) member's LOOP-CLOCK head, so a descent member left in the run would draw
                // its descent line on the loop clock every pass over its recorded slot - desynced from the
                // trigger-controlled icon (the descent line painted on the loiter while the icon still waits).
                // Its legs AND arcs stay out of the run, matching the descent member's intended full hide.
                if (IsDescentTriggerMember(i, loopUnits))
                    continue;
                members.Add((i, m));
            }
            if (members.Count == 0)
            {
                // Defensive: rec itself was not in the committed list (caller passed a detached
                // recording) - fall back to the single-member run so the pass still works.
                // Mirror the exclusion: never re-add a descent-trigger member here either (a
                // chain=none descent member reaches this fallback because the chain-member loop
                // above matched none, and re-adding it would draw its descent leg on the run's
                // loop-clock head - exactly the leak the descent member's own trigger-gated
                // primary pass already covers). Leave members empty so its legs/arcs/bridges
                // stay out of the run.
                if (!IsDescentTriggerMember(recordingIndex, loopUnits))
                    members.Add((recordingIndex, rec));
                return;
            }
            members.Sort((a, b) => a.rec.StartUT.CompareTo(b.rec.StartUT));
        }

        /// <summary>
        /// True when committed index <paramref name="index"/> is a descent-set member of a re-aim looped
        /// arrival (its owning unit <see cref="GhostPlaybackLogic.LoopUnit.HasDescentTrigger"/> and the index
        /// is in its descent set). A descent member renders ONLY via its own trigger-gated primary pass (drawn
        /// at the re-anchored rotation-aligned head, hidden during the loiter); it must be kept out of EVERY
        /// forward-run leg/arc/bridge path, which all draw against the VISIBLE member's raw LOOP-CLOCK head and
        /// would otherwise paint the descent line on the loiter while the icon still waits. The single seam for
        /// the three forward-run exclusion sites (chain run membership, the head-leg bridge candidate, and the
        /// per-member run loop) so they cannot drift apart. <c>loopUnits == null</c> / non-member / no descent
        /// trigger -> false (byte-identical to the no-trigger path). Pure; xUnit-testable without Unity.
        /// </summary>
        internal static bool IsDescentTriggerMember(int index, GhostPlaybackLogic.LoopUnitSet loopUnits)
        {
            return loopUnits != null
                && loopUnits.TryGetUnitForMember(index, out GhostPlaybackLogic.LoopUnit du)
                && du.HasDescentTrigger && du.IsDescentMember(index);
        }

        /// <summary>
        /// Clamp B (re-aim descent trigger): cap a forward-run window's <paramref name="chainDataEndUT"/> at the
        /// unit's SHIFTED parking-conic end (<c>RecordedDeorbitUT + CaptureShiftSeconds</c>, captureShift NEGATIVE)
        /// when <paramref name="recordingIndex"/> is the DESTINATION transfer member
        /// (<see cref="GhostPlaybackLogic.LoopUnit.TransferMemberIndex"/>) of a descent-trigger
        /// unit. PR #1177 shifts that member's destination parking conics ~|captureShift| EARLIER to meet the
        /// early-arriving re-aimed transfer, so the rendered conics end at <c>deorbit+captureShift</c>. Leaving the
        /// forward-run window at the UNSHIFTED <c>rec.EndUT</c> (~the seam, ~|captureShift| later) lets the
        /// <see cref="ForwardRenderWindow"/> past-end branch (<c>currentUT &lt;= dataEndUT + 1</c>) fire once the
        /// loiter icon passes the shifted conic end and paint the member's OWN unshifted recorded approach tail
        /// plus a ~|captureShift|-gap bridge — the spurious "second path" the icon cannot follow. Capping at the
        /// shifted conic end makes that guard FAIL the moment the icon leaves the shifted conic, killing the leak;
        /// the shifted parking conic the icon rides is untouched. Byte-identical (returns
        /// <paramref name="chainDataEndUT"/> unchanged) when: <paramref name="loopUnits"/> is null, the index is
        /// not a unit member, the unit has no descent trigger, the index is NOT the destination transfer member
        /// (the owner, every ride-along in a different/unshifted frame whose forward window must not be clipped at
        /// the transfer member's geometry, or a descent-set member whose line comes only from its own trigger-gated
        /// pass), RecordedDeorbitUT/CaptureShiftSeconds is NaN, captureShift is not negative, or the window already
        /// ends at/before the shifted conic end. Pure; xUnit-testable without Unity.
        /// </summary>
        internal static double ClampChainDataEndForDescentTransfer(
            double chainDataEndUT, int recordingIndex, GhostPlaybackLogic.LoopUnitSet loopUnits)
        {
            if (loopUnits != null
                && loopUnits.TryGetUnitForMember(recordingIndex, out GhostPlaybackLogic.LoopUnit unit)
                && unit.HasDescentTrigger && recordingIndex == unit.TransferMemberIndex
                && !double.IsNaN(unit.RecordedDeorbitUT) && !double.IsNaN(unit.CaptureShiftSeconds))
            {
                double shiftedConicEndUT = unit.RecordedDeorbitUT + unit.CaptureShiftSeconds;
                if (shiftedConicEndUT < unit.RecordedDeorbitUT && chainDataEndUT > shiftedConicEndUT)
                    return shiftedConicEndUT;
            }
            return chainDataEndUT;
        }

        /// <summary>
        /// Run-arc cache key (Step 3 Cache bullet): the run-arc VectorLine set is re-sampled only when the
        /// SELECTED segment set (the indices <see cref="SelectForwardArcSegmentIndices"/> returned) or the
        /// re-aim synodic WINDOW changes. Keying on the actual selected set (not currentElementIndex) is
        /// required by the revised run rule: with PAST arcs included, the same currentElementIndex maps to
        /// different selections as the icon crosses element boundaries within a run (the excluded current arc
        /// changes), so an index-only key would serve a stale arc set. The recorded segment geometry is
        /// static, but a re-aim window rollover swaps the EFFECTIVE geometry without changing the recorded
        /// segment count, so <paramref name="reaimWindowIndex"/> (the <c>ResolveEffectiveMapOrbitSegments</c>
        /// out window index, also the <c>ShadowRenderDriver.BuildChainSignature</c> discriminator) MUST be
        /// part of the key or a stale wrong-aimed arc would survive the rollover. The selected indices are
        /// emitted in ascending order (the selector scans ascending), so the key is order-stable. Pure;
        /// xUnit-testable.
        /// </summary>
        internal static string BuildForwardArcKey(
            List<int> selectedSegmentIndices, long reaimWindowIndex)
        {
            string indices = selectedSegmentIndices == null || selectedSegmentIndices.Count == 0
                ? "-"
                : string.Join(",", selectedSegmentIndices);
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}|{1}", indices, reaimWindowIndex);
        }

        /// <summary>
        /// Phase 8b.1 no-double-draw routing predicate (Driver side): should this recording's current
        /// non-orbital leg be drawn by the OWNED <c>TracedPathTreatment</c> instead of the Driver's
        /// direct <c>TryDrawLeg</c> call? True exactly when the Director owns the ghost's active leg as a
        /// fresh TracedPath this frame (<paramref name="directorOwnsTracedPath"/> =
        /// <c>ShadowRenderDriver.IsTracedPathOwnedThisFrame(pid, frame)</c>). The Driver routes through
        /// the treatment when true and STANDS DOWN on its own direct call; it draws directly when false.
        /// Because the treatment's draw and the Driver's stand-down are the SAME boolean - and the icon-
        /// drive / orbit-line patches read the same predicate to suppress the stock proto - the leg can
        /// never be drawn twice (treatment + Driver, or polyline + proto) on any frame. Pure mirror of
        /// <c>TracedPathTreatment.ShouldOwnLeg</c>; xUnit-testable without Unity.
        /// </summary>
        internal static bool ShouldDrawLegOwnedByTreatment(bool directorOwnsTracedPath)
            => directorOwnsTracedPath;

        /// <summary>
        /// Pan-stability split predicate (FIX 1): WILL this leg draw this frame? The Driver's decide
        /// pass (at <c>[DefaultExecutionOrder(-50)]</c>) publishes leg ownership BEFORE the actual point
        /// recompute + <c>Draw3D</c> moves to the post-camera-pan <c>Camera.onPreCull</c> slot, so the
        /// ownership publish can no longer key on the real draw return. This mirrors <see cref="TryDrawLeg"/>'s
        /// only NON-degenerate early returns: the body must be resolved (the body-null skip lives at the
        /// decide-pass call site, which <c>continue</c>s before reaching here) and the leg must carry at
        /// least two points (<c>m &lt; 2</c> skip). The lazy VectorLine-inflate-failure path
        /// (<c>BuildLegVectorLine</c> returns null) is treated as will-draw for ownership purposes: it is
        /// effectively never hit in map view, and keeping the predicate the exact mirror of the
        /// non-degenerate returns means will-draw == actual-draw for every real leg, so the order-0
        /// <c>GhostOrbitLinePatch</c> still observes "polyline owns this phase iff a leg draws" with no
        /// decision-without-draw gap. Pure; xUnit-testable without Unity.
        /// </summary>
        internal static bool WillLegDraw(int pointCount, bool bodyResolved)
            => bodyResolved && pointCount >= 2;

        /// <summary>
        /// Diagnostic: body-relative WORLD longitude (degrees, atan2(z,x) in Y-up world axes) of a
        /// recorded leg point as it is ACTUALLY DRAWN - i.e. <c>GetWorldSurfacePosition(lat,lon,alt)</c>
        /// on the LIVE body minus the body centre. This is the leg's body-FIXED position; comparing it to
        /// the orbit's inertial longitude (the MapRenderProbe icon-off-orbit lonOrbit*) exposes how far
        /// the polyline is rotated from the orbits under the loop shift (the escape-burn "isolated
        /// segment" sits ~one body-rotation-over-the-shift off the inertial loiter/hyperbolic). Matches
        /// <c>MapRenderProbe.LongitudeDeg</c> so the two numbers are directly comparable.
        /// </summary>
        private static double LegPointBodyRelLonDeg(CelestialBody body, double lat, double lon, double alt)
        {
            Vector3d rel = body.GetWorldSurfacePosition(lat, lon, alt) - body.position;
            return System.Math.Atan2(rel.z, rel.x) * (180.0 / System.Math.PI);
        }

        /// <summary>
        /// The world position of an OrbitSegment's RECORDED conic at <paramref name="ut"/>
        /// (raw recorded epoch, no loop shift), built from the stored Kepler elements exactly as
        /// <c>GhostMapPresence.ApplyOrbitToVessel</c> does. Used to locate the conic seam (the loiter's
        /// position at the burn start / the escape's position at the burn end). Returns false on any orbit
        /// construction fault.
        /// </summary>
        private static bool TryConicWorldAtUT(
            OrbitSegment seg, CelestialBody body, double ut, out Vector3d world)
        {
            world = Vector3d.zero;
            if (body == null) return false;
            try
            {
                Orbit orbit = new Orbit(
                    seg.inclination,
                    seg.eccentricity,
                    seg.semiMajorAxis,
                    seg.longitudeOfAscendingNode,
                    seg.argumentOfPeriapsis,
                    seg.meanAnomalyAtEpoch,
                    seg.epoch,
                    body);
                world = orbit.getPositionAtUT(ut);
                return IsFiniteVec(world);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Finite-component guard for a Vector3d (KSP's Vector3d has no instance IsFinite).</summary>
        private static bool IsFiniteVec(Vector3d v)
            => !double.IsNaN(v.x) && !double.IsInfinity(v.x)
               && !double.IsNaN(v.y) && !double.IsInfinity(v.y)
               && !double.IsNaN(v.z) && !double.IsInfinity(v.z);

        /// <summary>
        /// PURE diagnostic helper: the OrbitSegment indices that bracket a non-orbital leg in recorded-UT
        /// space - the same-body conic that ENDS at/just before the leg start (the loiter the burn leaves)
        /// and the same-body conic that STARTS at/just after the leg end (the escape the burn enters).
        /// <paramref name="beforeIdx"/> / <paramref name="afterIdx"/> are -1 when none. The 1 s tolerance
        /// absorbs the boundary sample shared between a conic and the adjacent burn leg. xUnit-testable
        /// (no Unity).
        /// </summary>
        /// <summary>Absolute floor for the conic-anchor seam-residual reject (km); covers small-radius bodies.</summary>
        internal const float AnchorMaxResidualKm = 50f;

        /// <summary>Relative (fraction of leg radius) ceiling for the conic-anchor seam-residual reject.</summary>
        internal const float AnchorMaxRelResidual = 0.05f;

        /// <summary>
        /// PURE guard predicate (Duna/Ike arrival regression): should the conic-anchor REJECT a leg (keep it
        /// body-fixed) because the bracketing conic seam does NOT meet the leg endpoints? The conic-anchor is
        /// only valid for a vacuum maneuver whose endpoints lie ON the bracketing orbits (residual ~0, e.g.
        /// the Kerbin escape burn = 0 km). A Duna/Ike ARRIVAL HYPERBOLA's <c>getPositionAtUT</c> at the leg
        /// boundary lands an inbound arm tens of Mm from the leg, so the radial residual is huge (430-46543
        /// km) and rotating to it would swing the leg to the wrong arm. Reject when the radial residual
        /// exceeds the absolute floor OR the relative fraction of the leg radius. (A radius-only / "ecc&lt;1"
        /// test is insufficient: a mis-bracketed ELLIPTICAL leg measured 430-3041 km off.) xUnit-testable.
        /// </summary>
        internal static bool IsSeamResidualTooLarge(
            float predResidStartKm, float predResidEndKm, float legRadiusKm)
        {
            if (predResidStartKm > AnchorMaxResidualKm || predResidEndKm > AnchorMaxResidualKm)
                return true;
            float relResid = legRadiusKm > 1f
                ? Mathf.Max(predResidStartKm, predResidEndKm) / legRadiusKm : 0f;
            return relResid > AnchorMaxRelResidual;
        }

        /// <summary>
        /// PURE run-leg frame classifier (playtest-5 rule, 2026-06-09): may this leg participate in the
        /// PERSISTENT render run (drawn while the icon is NOT on it)? True only when the leg is bracketed
        /// by a same-body conic on BOTH sides - the precondition for <see cref="TryAnchorLegToConicSeam"/>
        /// to rotate it onto the INERTIAL conic seam. A one-sided leg (launch ascent = after-only,
        /// descent-to-surface = before-only, no conics at all) stays BODY-FIXED when drawn, so as a
        /// persistent run leg it would visibly rotate with the planet against the inertial arcs (the
        /// observed gap-then-overlap sweep at the chain handoff); such legs draw ONLY while the icon is on
        /// them (the head-gated pass, where body-fixed is correct - the live ghost is glued to the
        /// pad/terrain). The bracket lookup uses the leg's OWN recording's segments - the same list the
        /// draw-time anchor consults - so decide and draw can never disagree on candidacy.
        /// xUnit-testable (no Unity).
        /// </summary>
        internal static bool IsRunLegAnchorCandidate(
            List<OrbitSegment> segs, string bodyName, double legStartUT, double legEndUT)
        {
            FindBracketingOrbitSegments(
                segs, bodyName, legStartUT, legEndUT, out int beforeIdx, out int afterIdx);
            return beforeIdx >= 0 && afterIdx >= 0;
        }

        // ------------------------------------------------------------------
        // SEAM BRIDGE (playtest 6): the body-fixed head leg (A, drawn at the LIVE planet rotation)
        // and the first inertial run element (B, the recorded conic) are separated by the body
        // rotation accrued over the loop shift - a visible angular gap that only closes as the icon
        // reaches the handoff. The bridge fills it WITH B'S OWN CURVATURE: it draws B's first
        // BridgeMergeSampleCount samples with the seam rotation UNWOUND along them (sample i rotated
        // by seamAngle*(1 - i/M) about the seam axis), so bridge[0] lands exactly on A's drawn end
        // and bridge[M] lands exactly on B's sample M. While the bridge draws, B's own VectorLine
        // draw range starts at sample M, so the lead-in is REPLACED, never double-drawn - and as the
        // gap closes (seamAngle -> 0) the bridge degenerates continuously into B's own lead-in, so
        // the handoff has no pop.
        // ------------------------------------------------------------------

        /// <summary>
        /// Number of B samples the seam bridge consumes (bridge point count = this + 1). One third of
        /// the 180-sample forward arc: long enough to unwind a few degrees of seam rotation gently,
        /// short enough that most of B still draws as itself.
        /// </summary>
        internal const int BridgeMergeSampleCount = 60;

        /// <summary>
        /// Max seam angle (radians, 45 deg) the bridge will span. A larger gap (a loop whose cadence
        /// is far from rotation-aligned) would draw a wild spiral; an honest gap reads better.
        /// </summary>
        internal const double BridgeMaxAngleRadians = 0.7853981633974483;

        /// <summary>
        /// Max recorded-time gap (seconds) between the head leg's endUT and the bridge target arc's
        /// startUT. The seam boundary sample is typically shared (gap of a few seconds at most); a
        /// far-later arc is not the continuation of this leg.
        /// </summary>
        internal const double BridgeMaxSeamGapSeconds = 120.0;

        /// <summary>
        /// Shared-seam-boundary slack (seconds) for the intervening-continuation-leg rule
        /// (<see cref="HasInterveningContinuationLeg"/>): a continuation leg whose seam coincides with the
        /// candidate leg's seam (a true handoff, gap of a second or less) still counts as intervening.
        /// Mirrors the 1 s shared-boundary tolerance <see cref="IsBridgeAdjacentConic"/> uses.
        /// </summary>
        internal const double BridgeSeamSharedBoundaryToleranceSeconds = 1.0;

        /// <summary>
        /// Min seam angle (radians, 5 deg) below which no bridge draws: the leg already MEETS the conic
        /// (an anchored leg calibrated onto the seam, a closed rotation gap, or the now-common re-aim
        /// launch-aligned ascent whose end lands within a few km of the escape conic). The bridge always
        /// draws a fixed ~74 deg conic merge slice (<see cref="BridgeMergeSampleCount"/> samples) whose
        /// off-chord bulge is ~200-370 km REGARDLESS of the gap, so for a near-meet (5 deg at Kerbin
        /// radius is a ~50 km chord; the launch alignment collapses the seam to ~0-3 deg) it draws a
        /// disproportionate arc that reads as a spurious extra segment beside the correct trajectory.
        /// Above 5 deg the gap is comparable to the bridge's own bulge - the moderate-misalignment range
        /// (5-45 deg) the bridge is designed to smooth - so it still draws. Was 1e-4 rad (~0.006 deg),
        /// which only skipped a perfectly degenerate seam and let the launch-aligned near-meet bridge.
        /// </summary>
        internal const double BridgeMinAngleRadians = 0.08726646259971647; // 5 deg

        /// <summary>
        /// Below the 5 deg merge-slice floor (<see cref="BridgeMinAngleRadians"/>) the seam still has a
        /// small but VISIBLE gap - most often the re-aim launch-aligned ascent, whose forward-drawn end
        /// LEADS the icon by the rotation over the remaining ascent and so lands a few degrees (tens to
        /// ~120 km) short of the inertial escape conic, a gap that shrinks to 0 as the icon climbs to the
        /// handoff. The merge slice cannot fill it (its ~200-370 km bulge is worse than the gap, which is
        /// why the 5 deg floor mutes it), but a STRAIGHT CHORD can - it links the leg's drawn end straight
        /// to the conic's near endpoint with zero bulge. So in (this, 5 deg] a chord bridge draws instead
        /// of the merge slice. BELOW this floor the leg already meets the conic within a few km (an
        /// anchored leg, a closed rotation gap), so neither bridge draws - a sub-this chord would be a
        /// degenerate near-zero-length line. 0.5 deg at Kerbin escape radius (~1370 km) is a ~12 km chord.
        /// </summary>
        internal const double BridgeChordMinAngleRadians = 0.008726646259971648; // 0.5 deg

        /// <summary>
        /// PURE: angle (radians) between two body-relative rays, used by the decide-side bridge gate.
        /// Returns PositiveInfinity for a degenerate (near-zero) input so the gate always rejects it.
        /// xUnit-testable (System.Math only, no Unity ECalls).
        /// </summary>
        internal static double SeamBridgeAngleRad(Vector3d a, Vector3d b)
        {
            double am = a.magnitude, bm = b.magnitude;
            if (am < 1e-9 || bm < 1e-9) return double.PositiveInfinity;
            Vector3d an = a / am, bn = b / bm;
            double cross = Vector3d.Cross(an, bn).magnitude;
            double dot = Vector3d.Dot(an, bn);
            return System.Math.Atan2(cross, dot);
        }

        /// <summary>How the decide-side gate treats a seam given its leg-to-conic angle.</summary>
        internal enum SeamBridgeKind { SkipAngleTooLarge, SkipMeetsConic, Chord, MergeSlice }

        /// <summary>
        /// PURE four-band seam-bridge classifier by leg-to-conic angle (launch-&gt;escape seam render):
        /// (a) infinite / &gt; <see cref="BridgeMaxAngleRadians"/> (45 deg) -&gt; SkipAngleTooLarge (honest
        /// gap; the merge slice would draw a wild spiral); (b) (5 deg, 45 deg] -&gt; MergeSlice (the
        /// moderate-misalignment smoother); (c) (<see cref="BridgeChordMinAngleRadians"/> 0.5 deg, 5 deg]
        /// -&gt; Chord (a small but visible gap the merge slice mutes itself on - a zero-bulge straight
        /// chord closes it); (d) &lt;= 0.5 deg -&gt; SkipMeetsConic (the leg already meets the conic within
        /// a few km; a chord would be degenerate). xUnit-testable.
        /// </summary>
        internal static SeamBridgeKind ClassifySeamBridgeByAngle(double angleRad)
        {
            if (double.IsInfinity(angleRad) || angleRad > BridgeMaxAngleRadians)
                return SeamBridgeKind.SkipAngleTooLarge;
            if (angleRad <= BridgeChordMinAngleRadians)
                return SeamBridgeKind.SkipMeetsConic;
            if (angleRad <= BridgeMinAngleRadians)
                return SeamBridgeKind.Chord;
            return SeamBridgeKind.MergeSlice;
        }

        /// <summary>
        /// PURE seam-bridge geometry (playtest 6): fills <paramref name="outPoints"/> with
        /// <paramref name="mergeCount"/>+1 body-relative points connecting <paramref name="endARel"/>
        /// (the body-fixed head leg's drawn end) to <c>arcRelPoints[mergeCount]*arcScale</c> (a point
        /// ON the inertial arc B), following B'S OWN SHAPE with the seam rotation unwound along it:
        /// point i is <c>arcRelPoints[i]*arcScale</c> rotated about the seam axis (the minimal-rotation
        /// axis from B's first sample ray to A's end ray - for same-latitude seam points that IS the
        /// body spin axis, the conic-anchor precedent) by <c>seamAngle*(1 - i/mergeCount)</c>, with a
        /// radial blend so point 0 lands EXACTLY on <paramref name="endARel"/>. Returns false (no
        /// bridge) when inputs are degenerate, the rays are antiparallel (no unique axis), or the seam
        /// angle exceeds <paramref name="maxAngleRad"/>. Rodrigues rotation in doubles - no Unity
        /// ECalls, so it is directly xUnit-testable.
        /// </summary>
        /// <param name="endARel">Bridge start: head leg's last drawn point, body-relative.</param>
        /// <param name="arcRelPoints">B's body-relative sample offsets (forward-arc cache).</param>
        /// <param name="arcScale">Scale applied to every arc sample (1 for metre-space tests; the
        /// Driver passes ScaledSpace.InverseScaleFactor so the output is scaled-space-relative).</param>
        /// <param name="mergeCount">B sample index where the bridge merges into B (uses 0..mergeCount).</param>
        /// <param name="maxAngleRad">Seam-angle gate; larger gaps draw no bridge.</param>
        /// <param name="outPoints">Filled with mergeCount+1 body-relative points.</param>
        /// <param name="seamAngleRad">The seam angle actually measured (diagnostic).</param>
        internal static bool TryBuildSeamBridgeLocalPoints(
            Vector3d endARel, Vector3d[] arcRelPoints, double arcScale, int mergeCount,
            double maxAngleRad, Vector3d[] outPoints, out double seamAngleRad)
        {
            seamAngleRad = double.NaN;
            if (arcRelPoints == null || outPoints == null) return false;
            if (mergeCount < 1 || arcRelPoints.Length <= mergeCount) return false;
            if (outPoints.Length < mergeCount + 1) return false;

            Vector3d b0 = arcRelPoints[0] * arcScale;
            double aMag = endARel.magnitude, bMag = b0.magnitude;
            if (aMag < 1e-9 || bMag < 1e-9) return false;

            Vector3d axisRaw = Vector3d.Cross(b0 / bMag, endARel / aMag);
            double sinA = axisRaw.magnitude;
            double cosA = Vector3d.Dot(b0 / bMag, endARel / aMag);
            double angle = System.Math.Atan2(sinA, cosA);
            seamAngleRad = angle;
            if (angle > maxAngleRad) return false;
            // Antiparallel rays have no unique rotation axis; also covered by any sane maxAngleRad,
            // but guard explicitly so a caller passing a huge gate cannot divide by ~0 below.
            if (sinA < 1e-12)
            {
                if (cosA < 0.0) return false;
                // Aligned rays (seam closed): the bridge IS B's lead-in, with only the radial blend.
                for (int i = 0; i <= mergeCount; i++)
                {
                    double w = (double)i / mergeCount;
                    Vector3d p = arcRelPoints[i] * arcScale;
                    double radial = 1.0 + (aMag / bMag - 1.0) * (1.0 - w);
                    outPoints[i] = p * radial;
                }
                return true;
            }

            Vector3d axis = axisRaw / sinA;
            double radial0 = aMag / bMag;
            for (int i = 0; i <= mergeCount; i++)
            {
                double w = (double)i / mergeCount;
                Vector3d p = arcRelPoints[i] * arcScale;
                double theta = angle * (1.0 - w);
                // Rodrigues: v cos(t) + (axis x v) sin(t) + axis (axis . v)(1 - cos(t)).
                double ct = System.Math.Cos(theta), st = System.Math.Sin(theta);
                Vector3d rotated = p * ct
                    + Vector3d.Cross(axis, p) * st
                    + axis * (Vector3d.Dot(axis, p) * (1.0 - ct));
                // Radial blend so bridge[0] lands EXACTLY on endARel (the rotation preserves |p|, and
                // A's end can sit at a slightly different radius than B's first sample).
                double radial = 1.0 + (radial0 - 1.0) * (1.0 - w);
                outPoints[i] = rotated * radial;
            }
            return true;
        }

        /// <summary>
        /// PURE small-gap CHORD builder (launch-&gt;escape seam render): a STRAIGHT line from the leg's
        /// drawn end (<paramref name="endARel"/>, scaled body-relative) to the conic's near endpoint
        /// (<paramref name="conicNearRel"/>, the conic's first sample in body-LOCAL metres, scaled by
        /// <paramref name="arcScale"/>). Used instead of <see cref="TryBuildSeamBridgeLocalPoints"/>'s
        /// conic-merge slice when the seam gap is small (a few degrees): the merge slice's fixed
        /// ~200-370 km off-chord bulge would dwarf the gap, but a chord just closes it with zero bulge.
        /// outPoints[0] == endARel (leg end), outPoints[mergeCount] == the conic near endpoint, linearly
        /// interpolated between. No rotation, no radial blend, no conic clip (the chord ends exactly where
        /// the conic begins). xUnit-testable (no Unity ECalls).
        /// </summary>
        internal static bool TryBuildSeamBridgeChordLocalPoints(
            Vector3d endARel, Vector3d conicNearRel, double arcScale, int mergeCount, Vector3d[] outPoints)
        {
            if (outPoints == null || mergeCount < 1 || outPoints.Length < mergeCount + 1) return false;
            Vector3d b0 = conicNearRel * arcScale;
            for (int i = 0; i <= mergeCount; i++)
            {
                double w = (double)i / mergeCount;
                outPoints[i] = endARel * (1.0 - w) + b0 * w;
            }
            return true;
        }

        /// <summary>
        /// PURE bridge-target selector: among the cached forward arcs of one recording, the index of
        /// the arc that CONTINUES the body-fixed head leg - same body, starting at/just after the
        /// leg's endUT (1 s shared-boundary tolerance) and within
        /// <paramref name="maxSeamGapSeconds"/> - choosing the earliest-starting candidate. -1 when
        /// none. xUnit-testable (operates on the cached arc metadata only).
        /// </summary>
        /// <summary>
        /// PURE diagnostic: max perpendicular deviation of the interior points from the straight chord
        /// (points[0] -> points[count-1]), in the points' own units. Quantifies a bridge's CURVATURE
        /// in the draw log (playtest-11 "the bridge looks straight" report): a genuinely straight
        /// bridge reads ~0, a healthy conic-tail bridge reads a substantial fraction of its length.
        /// xUnit-testable.
        /// </summary>
        internal static double MaxChordDeviation(Vector3d[] points, int count)
        {
            if (points == null || count < 3 || points.Length < count) return 0.0;
            Vector3d a = points[0];
            Vector3d ab = points[count - 1] - a;
            double abLen = ab.magnitude;
            if (abLen < 1e-9) return 0.0;
            Vector3d dir = ab / abLen;
            double max = 0.0;
            for (int i = 1; i < count - 1; i++)
            {
                Vector3d ap = points[i] - a;
                Vector3d perp = ap - dir * Vector3d.Dot(ap, dir);
                double d = perp.magnitude;
                if (d > max) max = d;
            }
            return max;
        }

        /// <summary>
        /// PURE adjacency rule (playtest-7 generalized bridges): is this conic the inertial NEIGHBOUR of
        /// a body-fixed leg at the given seam? <paramref name="atLegStart"/>=true tests the PREV side
        /// (the conic ENDS at/just before the leg's startUT, 1 s shared-boundary tolerance forward);
        /// false tests the NEXT side (the conic STARTS at/just after the leg's endUT). Same-body only;
        /// the seam UT gap is bounded by <paramref name="maxSeamGapSeconds"/>. The single load-bearing
        /// predicate behind the Driver's adjacent-conic search, so the tested rule IS the production
        /// rule. xUnit-testable.
        /// </summary>
        internal static bool IsBridgeAdjacentConic(
            string segBodyName, double segStartUT, double segEndUT,
            string legBodyName, double legSeamUT, bool atLegStart, double maxSeamGapSeconds)
        {
            if (string.IsNullOrEmpty(legBodyName)) return false;
            if (!string.Equals(segBodyName, legBodyName, StringComparison.Ordinal)) return false;
            return atLegStart
                ? segEndUT >= legSeamUT - maxSeamGapSeconds && segEndUT <= legSeamUT + 1.0
                : segStartUT >= legSeamUT - 1.0 && segStartUT <= legSeamUT + maxSeamGapSeconds;
        }

        /// <summary>
        /// One bridge-eligible leg's UT span + body, for the pure intervening-leg predicate. A flat
        /// projection of <c>BridgeLegCandidate</c> so the rule is xUnit-testable without the private
        /// Driver scratch type.
        /// </summary>
        internal struct BridgeLegSpan
        {
            public double startUT;
            public double endUT;
            public string bodyName;
        }

        /// <summary>
        /// PURE intervening-ascent-leg rule (launch-escape-seam render fix): once an adjacent conic has
        /// been chosen for a candidate leg's seam, is there ANOTHER body-fixed leg in the same chain run
        /// (same body) that CONTINUES from this leg's seam BEFORE the conic - i.e. the conic is NOT this
        /// leg's immediate next/prev segment? A launch records across two consecutive body-fixed legs
        /// (pad ascent -> continuation ascent) feeding one escape conic; the pad-ascent leg's end is
        /// adjacent to the conic only in UT, but the CONTINUATION leg sits between them, so the pad
        /// leg's bridge would shortcut over it. Only the leg IMMEDIATELY adjacent to the conic may bridge.
        ///
        /// <para>END side (<paramref name="atLegStart"/>=false): the conic STARTS at <paramref name="conicSeamUT"/>
        /// (its startUT); skip this bridge when another body-fixed leg STARTS at/after this leg's
        /// <paramref name="legSeamUT"/> (the leg-end, shared-boundary slack) and STRICTLY before the conic
        /// start. A launch's continuation leg starts exactly at the pad leg's end and ends before the
        /// conic, so its start lands in <c>[legEnd - tol, conicStart - tol)</c> - it intervenes. START side
        /// (true): the conic ENDS at <paramref name="conicSeamUT"/> (its endUT); skip when another body-fixed
        /// leg ENDS at/before this leg's start (shared slack) and strictly after the conic end - i.e. its end
        /// lands in <c>(conicEnd + tol, thisLegStart + tol]</c>.</para>
        ///
        /// <para>The candidate itself (<paramref name="selfIndex"/>) is excluded; only same-body legs
        /// count (a different body's leg cannot be a continuation across an SOI seam here). The boundary
        /// tolerance <paramref name="seamTolSeconds"/> mirrors the 1 s shared-seam slack so a leg whose
        /// start coincides with this leg's end (a true continuation handoff) still counts as intervening.
        /// xUnit-testable (no Unity ECalls).</para>
        /// </summary>
        internal static bool HasInterveningContinuationLeg(
            BridgeLegSpan[] legs, int selfIndex, string candBodyName,
            double legSeamUT, double conicSeamUT, bool atLegStart, double seamTolSeconds,
            out int interveningIndex, out double interveningSeamUT)
        {
            interveningIndex = -1;
            interveningSeamUT = double.NaN;
            if (legs == null || string.IsNullOrEmpty(candBodyName)) return false;
            for (int i = 0; i < legs.Length; i++)
            {
                if (i == selfIndex) continue;
                var other = legs[i];
                if (!string.Equals(other.bodyName, candBodyName, StringComparison.Ordinal)) continue;
                // The other leg's seam that attaches it to the chain at the conic-facing end: its START on
                // the end side (it continues forward from this leg's end toward the conic), its END on the
                // start side (it feeds backward from this leg's start toward the conic behind it).
                double otherSeam = atLegStart ? other.endUT : other.startUT;
                bool intervenes = atLegStart
                    // start side: another leg ENDS at/before this leg's start (shared slack) yet strictly
                    // after the conic that ends behind it - it sits between the conic and this leg.
                    ? otherSeam <= legSeamUT + seamTolSeconds && otherSeam > conicSeamUT + seamTolSeconds
                    // end side: another leg STARTS at/after this leg's end (shared slack) yet strictly
                    // before the conic that starts ahead of it - it sits between this leg and the conic.
                    : otherSeam >= legSeamUT - seamTolSeconds && otherSeam < conicSeamUT - seamTolSeconds;
                if (intervenes)
                {
                    interveningIndex = i;
                    interveningSeamUT = otherSeam;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// PURE signed-gap rule (playtest-7, maintainer rule): a bridge fills a REAL trajectory gap -
        /// the previous element's drawn end sits BEHIND the next element's drawn start along the
        /// direction of travel. When the previous end has rotated PAST the next start (overshoot, the
        /// two lines already overlap), drawing a bridge would double back - skip it. The displacement
        /// from the previous end to the next start projected on the travel direction decides:
        /// positive = real gap (bridge), non-positive = overshoot/contact (no bridge). All three
        /// vectors are body-relative in the same frame; scale-invariant. xUnit-testable.
        /// </summary>
        internal static bool IsSeamGapAhead(
            Vector3d prevEndRel, Vector3d nextStartRel, Vector3d travelDir)
            => Vector3d.Dot(nextStartRel - prevEndRel, travelDir) > 0.0;

        /// <summary>
        /// PURE run-leg visibility rule (playtest-7 revision of the playtest-5 hide): a body-fixed
        /// (non-anchorable) run leg is hidden only when it is entirely in the PAST (the icon has moved
        /// beyond its end) - the trailing piece would rotate-sweep into the inertial line behind the
        /// icon. A FUTURE body-fixed leg (the Duna landing descent) DRAWS, body-fixed where the planet
        /// rotation currently places it, connected to the inertial run by the seam bridges; previously
        /// it was hidden until the icon reached it, which read as the landing line "appearing at the
        /// end". xUnit-testable.
        /// </summary>
        internal static bool ShouldHideBodyFixedRunLeg(
            bool anchorCandidate, double legEndUT, double headUT)
            => !anchorCandidate && legEndUT <= headUT;

        /// <summary>
        /// PURE terminal-leg exception input (playtest 11): does any ABOVE-SURFACE conic in this
        /// segment list start at/after <paramref name="ut"/> (1 s shared-boundary tolerance) and
        /// before <paramref name="windowStopUT"/>? The past-hide rule exists because a trailing
        /// body-fixed leg SWEEPS INTO inertial geometry that FOLLOWS it (the launch ascent under the
        /// suborbital conic). A TERMINAL past leg - the Duna landing trail, with nothing after it in
        /// the run - cannot overlap anything and stays visible (glued to its landing site, bridged to
        /// the orbit behind it); hiding it at touchdown read as "the landing line disappeared while
        /// the rest of the trajectory stayed". Below-surface conics do not count: they are never
        /// drawn. xUnit-testable (surface seam injected).
        /// </summary>
        internal static bool AnyAboveSurfaceConicStartsAtOrAfter(
            List<OrbitSegment> segs, double ut, double windowStopUT, BodySurfaceProvider surface)
        {
            if (segs == null) return false;
            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                if (s.endUT <= s.startUT) continue;
                if (s.startUT < ut - 1.0) continue;
                if (s.startUT >= windowStopUT) continue;
                if (IsOrbitSegmentBelowSurface(s, surface)) continue;
                return true;
            }
            return false;
        }

        internal static void FindBracketingOrbitSegments(
            List<OrbitSegment> segs, string bodyName, double legStartUT, double legEndUT,
            out int beforeIdx, out int afterIdx)
        {
            beforeIdx = -1;
            afterIdx = -1;
            if (segs == null) return;
            const double tol = 1.0;
            double bestBeforeEnd = double.NegativeInfinity;
            double bestAfterStart = double.PositiveInfinity;
            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                if (s.endUT <= s.startUT) continue;
                if (!string.Equals(s.bodyName, bodyName, StringComparison.Ordinal)) continue;
                if (s.endUT <= legStartUT + tol && s.endUT > bestBeforeEnd)
                {
                    bestBeforeEnd = s.endUT;
                    beforeIdx = i;
                }
                if (s.startUT >= legEndUT - tol && s.startUT < bestAfterStart)
                {
                    bestAfterStart = s.startUT;
                    afterIdx = i;
                }
            }
        }

        /// <summary>
        /// PURE diagnostic helper: the body-fixed longitude (deg) to pass to
        /// <see cref="CelestialBody.GetWorldSurfacePosition"/> so the resulting world point sits where the
        /// surface point at <paramref name="recordedLon"/> actually WAS at <paramref name="legStartUT"/>,
        /// not where the LIVE body rotation places it now. GetWorldSurfacePosition applies the body's
        /// CURRENT spin; counter-rotating the longitude by the spin accumulated between legStartUT and
        /// liveUT cancels that drift, so a fixed-UT conic seam (<c>getPositionAtUT(legStartUT)</c>) and this
        /// body-fixed point are compared on ONE rotation basis. The shift is self-consistent with
        /// GetWorldSurfacePosition's own eastward (= prograde) longitude convention (world azimuth =
        /// longitude + rotationAngle(t), with rotationAngle advancing prograde over time), so the constant
        /// offset and absolute sign cancel in the difference and no explicit spin-axis / rotationAngle
        /// lookup is needed. A non-rotating body (<paramref name="rotationPeriod"/> == 0 / non-finite)
        /// returns <paramref name="recordedLon"/> unchanged - no spin drift to undo. xUnit-testable (no
        /// Unity).
        /// </summary>
        internal static double BodyFixedLongitudeAtUT(
            double recordedLon, double legStartUT, double liveUT, double rotationPeriod)
        {
            if (double.IsNaN(rotationPeriod) || double.IsInfinity(rotationPeriod)
                || System.Math.Abs(rotationPeriod) <= 1e-9)
                return recordedLon;
            return recordedLon + (legStartUT - liveUT) * 360.0 / rotationPeriod;
        }

        /// <summary>
        /// PURE inverse of <see cref="BodyFixedLongitudeAtUT"/> (playtest-12 gap-fill): the RECORDED
        /// body-fixed longitude of a world point evaluated at <paramref name="sampleUT"/> but converted
        /// through the body's LIVE rotation (<c>CelestialBody.GetLongitude</c> uses the current spin).
        /// Counter-rotating by the spin accrued between sampleUT and liveUT recovers the longitude the
        /// recorder WOULD have stored at sampleUT, so a conic-sampled gap-fill point lands on the same
        /// rotation basis as the recorded leg points around it. Roundtrip contract:
        /// <c>BodyFixedLongitudeAtUT(RecordedLongitudeAtUT(x, t, live), t, live) == x</c>.
        /// xUnit-testable.
        /// </summary>
        internal static double RecordedLongitudeAtUT(
            double lonAtLiveRotation, double sampleUT, double liveUT, double rotationPeriod)
        {
            if (double.IsNaN(rotationPeriod) || double.IsInfinity(rotationPeriod)
                || System.Math.Abs(rotationPeriod) <= 1e-9)
                return lonAtLiveRotation;
            return lonAtLiveRotation + (liveUT - sampleUT) * 360.0 / rotationPeriod;
        }

        /// <summary>
        /// Diagnostic (Bug 2 / Root B): a polyline leg bracketed by an orbit on only ONE side stays
        /// body-fixed (launch ascent = after-only; descent-from-orbit = before-only). For the descent case
        /// this logs the TRUE geometric world gap + body-relative longitudes between the leg's body-fixed
        /// start and the preceding orbit's seam, BOTH evaluated at leg.startUT (the conic via its fixed-UT
        /// getPositionAtUT, the body-fixed point via <see cref="BodyFixedLongitudeAtUT"/> so live planet
        /// rotation does not inflate the value - it reads ~0 km when the orbit-to-descent seam is geometrically
        /// continuous, as at the s15 "Duna One" re-aim landing). Rate-limited per rec; render-neutral.
        /// </summary>
        private static void EmitOneSidedBracketDiagnostic(
            Recording rec, LegPolyline leg, CelestialBody body, int beforeIdx, int afterIdx)
        {
            if (rec == null || body == null || leg.PointCount < 1) return;
            // Lazy Func: the conic build (new Orbit + getPositionAtUT via TryConicWorldAtUT) +
            // GetWorldSurfacePosition + atan2 + format run ONLY when the 2s rate-limit actually emits, not
            // every frame this one-sided leg draws.
            ParsekLog.VerboseRateLimited(Tag, "polyline.onesided." + rec.RecordingId,
                () =>
                {
                    var ic = System.Globalization.CultureInfo.InvariantCulture;
                    string seamInfo = "before=none(ascent)";
                    if (beforeIdx >= 0
                        && TryConicWorldAtUT(rec.OrbitSegments[beforeIdx], body, leg.startUT, out Vector3d cBefore))
                    {
                        // Evaluate the body-fixed deorbit point on the SAME UT/rotation basis as the conic
                        // (leg.startUT), NOT the live body rotation. A raw GetWorldSurfacePosition uses the
                        // body's CURRENT spin, so it drifts away from the fixed-UT conic seam as the planet
                        // turns while this leg stays the active drawn leg - inflating the logged gap (s15
                        // "Duna One": 0 km at the aligned instant -> ~8 km a few seconds later, purely from
                        // Duna's spin) and overstating the true ~0 km geometric seam. BodyFixedLongitudeAtUT
                        // counter-rotates the longitude so the gap reads the real orbit-to-descent seam.
                        double lonAtStartUT = BodyFixedLongitudeAtUT(
                            leg.lons[0], leg.startUT, Planetarium.GetUniversalTime(), body.rotationPeriod);
                        Vector3d bf = body.GetWorldSurfacePosition(leg.lats[0], lonAtStartUT, leg.alts[0]);
                        double seamGapKm = Vector3d.Distance(bf, cBefore) / 1000.0;
                        Vector3d relBf = bf - body.position;
                        Vector3d relC = cBefore - body.position;
                        double lonBf = System.Math.Atan2(relBf.z, relBf.x) * (180.0 / System.Math.PI);
                        double lonSeam = System.Math.Atan2(relC.z, relC.x) * (180.0 / System.Math.PI);
                        var s = rec.OrbitSegments[beforeIdx];
                        seamInfo = string.Format(ic,
                            "before=seg{0}(ecc={1:F3} sma={2:F0}) seamGap={3:F0}km@startUT lonBodyFixed={4:F1} lonOrbitSeam={5:F1}",
                            beforeIdx, s.eccentricity, s.semiMajorAxis, seamGapKm, lonBf, lonSeam);
                    }
                    return string.Format(ic,
                        "Anchor leg SKIPPED (one-sided): rec={0} leg=[{1:F1},{2:F1}] body={3} anchored=false " +
                        "reason=one-sided-bracket {4} after={5}",
                        rec.RecordingId, leg.startUT, leg.endUT, leg.bodyName ?? "(null)",
                        seamInfo, afterIdx >= 0 ? ("seg" + afterIdx) : "none");
                },
                2.0);
        }

        /// <summary>
        /// CONIC ANCHOR (2026-06-03): rotate a vacuum-maneuver leg's already-captured scaled-space points
        /// so the leg lands on the faithful bracketing conic seam, fixing the loop-shift body rotation
        /// that draws the escape burn ~96 deg off the loiter/hyperbola lines. The body-fixed capture has
        /// the correct SHAPE but the wrong rotation (a pure spin-axis rotation = the body's rotation over
        /// the loop shift); this calibrates that rotation DIRECTLY from the recorded OrbitSegment conics
        /// via <c>getPositionAtUT</c> (the proven-faithful source - NOT the longitude-lift, which lands
        /// 600-1200 km off the conic) at BOTH seam endpoints and Slerps it across the leg.
        ///
        /// <para>Applies ONLY to legs bracketed by a conic on BOTH sides - a vacuum maneuver BETWEEN two
        /// orbits (the escape burn, an orbit raise, a circularization). A launch ascent (no preceding
        /// conic) or a descent-to-surface (no following conic) is left body-fixed so it stays glued to the
        /// rotating pad / landing site. Self-calibrating: where the body-fixed capture already coincides
        /// with the conic (the early parking/raise region, seam gap ~0 km) the recovered rotation is
        /// ~identity, so this is a no-op there - it does NOT regress the regions the reverted longitude-
        /// lift broke.</para>
        ///
        /// <para>Rotates <c>leg.scratchScaledSpace</c> in place (the array is shared with the cached leg,
        /// so the rotation persists into the draw). Returns true when applied. The minimal
        /// <c>FromToRotation</c> between the two same-latitude rays IS the spin-axis rotation, so this
        /// needs no explicit rotation-axis lookup.</para>
        /// </summary>
        private static bool TryAnchorLegToConicSeam(
            Recording rec, LegPolyline leg, CelestialBody body, Transform scaledXform)
        {
            int m = leg.PointCount;
            if (rec == null || body == null || m < 2 || leg.scratchScaledSpace == null) return false;

            FindBracketingOrbitSegments(
                rec.OrbitSegments, leg.bodyName, leg.startUT, leg.endUT,
                out int beforeIdx, out int afterIdx);
            if (beforeIdx < 0 || afterIdx < 0)
            {
                // One-sided bracket -> leg stays body-fixed (correct: a launch ascent off the rotating pad
                // = after-only; a descent-to-surface = before-only). Bug 2 / Root B diagnostic: for the
                // descent case (an orbit BEFORE, surface after) log the TRUE geometric seam gap +
                // body-relative longitudes between the leg's body-fixed start and the preceding orbit's
                // seam, BOTH on the leg.startUT rotation basis (so the value reads the real orbit-to-descent
                // continuity, ~0 km when geometrically continuous, NOT live-rotation drift). Render-neutral.
                EmitOneSidedBracketDiagnostic(rec, leg, body, beforeIdx, afterIdx);
                return false;
            }

            if (!TryConicWorldAtUT(rec.OrbitSegments[beforeIdx], body, leg.startUT, out Vector3d cBeforeWorld)
                || !TryConicWorldAtUT(rec.OrbitSegments[afterIdx], body, leg.endUT, out Vector3d cAfterWorld))
                return false;

            Vector3 center = scaledXform != null
                ? scaledXform.position
                : (Vector3)ScaledSpace.LocalToScaledSpace(body.position);
            Vector3 cBefore = (Vector3)ScaledSpace.LocalToScaledSpace(cBeforeWorld);
            Vector3 cAfter = (Vector3)ScaledSpace.LocalToScaledSpace(cAfterWorld);

            Vector3 relStart = leg.scratchScaledSpace[0] - center;
            Vector3 relEnd = leg.scratchScaledSpace[m - 1] - center;
            Vector3 cRelStart = cBefore - center;
            Vector3 cRelEnd = cAfter - center;
            if (relStart.sqrMagnitude < 1e-10f || relEnd.sqrMagnitude < 1e-10f
                || cRelStart.sqrMagnitude < 1e-10f || cRelEnd.sqrMagnitude < 1e-10f)
                return false;

            // GUARD (Duna/Ike arrival regression): the body-fixed-vs-conic offset is a pure spin-axis
            // rotation ONLY when the conic seam actually MEETS the leg endpoints (same radius). At Kerbin
            // the bracketing conics are near-circular, so the seam coincides with the leg (residual 0 km).
            // At a Duna/Ike ARRIVAL HYPERBOLA the bracketing conic's getPositionAtUT lands tens of Mm from
            // the leg (an inbound arm far from periapsis), so FromToRotation would swing the whole leg -
            // and the marker that rides it - to the WRONG arm. Reject when the seam radius does not match
            // the leg endpoint radius. The radial residual is the proven discriminator: 0 km at Kerbin vs
            // 430-46543 km at Duna/Ike; an "elliptical-only / ecc<1" test is NOT sufficient (a mis-bracketed
            // ELLIPTICAL leg measured 430-3041 km off). Computed BEFORE mutating so a rejected leg stays
            // body-fixed and TryAnchorMarkerToPolyline samples the body-fixed points.
            float sf = (float)ScaledSpace.ScaleFactor;
            float predResidStartKm = Mathf.Abs(relStart.magnitude - cRelStart.magnitude) * sf / 1000f;
            float predResidEndKm = Mathf.Abs(relEnd.magnitude - cRelEnd.magnitude) * sf / 1000f;
            float legRadiusKm = relStart.magnitude * sf / 1000f;
            float relResid = legRadiusKm > 1f
                ? Mathf.Max(predResidStartKm, predResidEndKm) / legRadiusKm : 0f;
            var segB = rec.OrbitSegments[beforeIdx];
            var segA = rec.OrbitSegments[afterIdx];
            if (IsSeamResidualTooLarge(predResidStartKm, predResidEndKm, legRadiusKm))
            {
                ParsekLog.VerboseRateLimited(Tag, "polyline.anchor." + rec.RecordingId,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Anchor leg SKIPPED: rec={0} leg=[{1:F1},{2:F1}] body={3} anchored=false reason=seam-mismatch " +
                        "predResidStart={4:F0}km predResidEnd={5:F0}km legRadius={6:F0}km relResid={7:F2} " +
                        "before=seg{8}(ecc={9:F3} sma={10:F0}) after=seg{11}(ecc={12:F3} sma={13:F0})",
                        rec.RecordingId, leg.startUT, leg.endUT, leg.bodyName ?? "(null)",
                        predResidStartKm, predResidEndKm, legRadiusKm, relResid,
                        beforeIdx, segB.eccentricity, segB.semiMajorAxis,
                        afterIdx, segA.eccentricity, segA.semiMajorAxis),
                    2.0);
                return false; // leave the leg body-fixed; the marker rides the body-fixed head
            }

            Quaternion rotStart = Quaternion.FromToRotation(relStart, cRelStart);
            Quaternion rotEnd = Quaternion.FromToRotation(relEnd, cRelEnd);

            double t0 = leg.startUT, span = leg.endUT - leg.startUT;
            bool haveUTs = leg.recordedUTs != null && leg.recordedUTs.Length == m && span > 0.0;
            for (int i = 0; i < m; i++)
            {
                float frac = haveUTs
                    ? (float)((leg.recordedUTs[i] - t0) / span)
                    : (m > 1 ? (float)i / (m - 1) : 0f);
                if (frac < 0f) frac = 0f; else if (frac > 1f) frac = 1f;
                Quaternion rot = Quaternion.Slerp(rotStart, rotEnd, frac);
                leg.scratchScaledSpace[i] = center + rot * (leg.scratchScaledSpace[i] - center);
            }

            // Residual proves the pin (should be ~0 after the guard). Enriched with the bracketing-conic
            // sma/ecc + applied rotation angles so a future mismatch is self-evident without cross-grep.
            float residStart = Vector3.Distance(leg.scratchScaledSpace[0], cBefore) * sf / 1000f;
            float residEnd = Vector3.Distance(leg.scratchScaledSpace[m - 1], cAfter) * sf / 1000f;
            ParsekLog.VerboseRateLimited(Tag, "polyline.anchor." + rec.RecordingId,
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Anchor leg: rec={0} leg=[{1:F1},{2:F1}] body={3} anchored=true before=seg{4}(ecc={5:F3} sma={6:F0}) " +
                    "after=seg{7}(ecc={8:F3} sma={9:F0}) residualStart={10:F0}km residualEnd={11:F0}km " +
                    "rotAngleStart={12:F1} rotAngleEnd={13:F1}",
                    rec.RecordingId, leg.startUT, leg.endUT, leg.bodyName ?? "(null)",
                    beforeIdx, segB.eccentricity, segB.semiMajorAxis,
                    afterIdx, segA.eccentricity, segA.semiMajorAxis, residStart, residEnd,
                    Quaternion.Angle(Quaternion.identity, rotStart),
                    Quaternion.Angle(Quaternion.identity, rotEnd)),
                2.0);
            return true;
        }

        /// <summary>
        /// What a marker riding the polyline should do when the leg whose head-UT span it covers was NOT
        /// drawn this frame (its <see cref="LegPolyline.scratchScaledSpace"/> is stale).
        /// </summary>
        internal enum UndrawnLegFallback : byte
        {
            /// <summary>
            /// Hold the last on-line position (the conic-anchored leg's drawn line is rotated off the
            /// body-fixed head, so the caller's body-fixed fallback would land off the line).
            /// </summary>
            HoldLastGoodOnLine = 0,

            /// <summary>
            /// Let the caller keep its fresh body-fixed head (a non-anchored body-fixed leg draws the raw
            /// body-fixed points, so the head is already on the line AND current - no stale hold needed).
            /// </summary>
            UseFreshBodyFixedHead = 1,
        }

        /// <summary>
        /// PURE decision (descent-icon visibility): for a leg covering the marker's head UT that was NOT
        /// drawn this frame, should the marker hold its last on-line position or fall through to the
        /// caller's fresh body-fixed head? A CONIC-ANCHORED leg's drawn line is rotated ~96 deg off the
        /// body-fixed head under the loop shift, so the body-fixed fallback would jump off the line -> HOLD.
        /// A NON-anchored body-fixed leg (descent / atmospheric / surface) draws the raw body-fixed points,
        /// so the caller's fresh body-fixed head is the SAME on-line point but current -> USE it (a
        /// &lt;=5 s-stale hold would only lag the moving descent at warp / on the IMGUI Layout-pass dropout).
        /// This decouples the descent marker's presence from whether the polyline leg happened to be redrawn
        /// THIS frame, without moving the icon off the line. xUnit-testable without Unity.
        /// </summary>
        internal static UndrawnLegFallback ResolveUndrawnLegFallback(bool legWasConicAnchored)
            => legWasConicAnchored
                ? UndrawnLegFallback.HoldLastGoodOnLine
                : UndrawnLegFallback.UseFreshBodyFixedHead;

        /// <summary>
        /// PURE decision (A1 polyline-leg parity lens): should this leg be included in the rendered-vs-
        /// recorded leg-geometry parity capture (<see cref="CaptureRenderedVsRecordedLegGeometry"/>)? A
        /// CONIC-ANCHORED leg (<see cref="LegPolyline.wasAnchored"/>: the draw rotated its points ~96 deg onto
        /// the bracketing conic seam) is EXCLUDED - its rendered points3 are INTENTIONALLY far from the raw
        /// recorded surface track, so a rendered-vs-raw-recorded diff would false-fire on a CORRECT anchor
        /// (and read ~0 on a leg that FAILED to anchor - exactly backwards). A NON-anchored body-fixed leg
        /// (descent / atmospheric / surface) draws the raw body-fixed points, so rendered == recorded by
        /// construction and a genuine mis-draw drifts -> INCLUDED. The anchor's per-point Slerp is not cheaply
        /// recoverable to rebuild the reference in, so anchored vacuum-maneuver legs are a STATED, documented
        /// lens limitation. xUnit-testable without Unity.
        /// </summary>
        internal static bool ShouldCaptureLegForParity(bool legWasConicAnchored)
            => !legWasConicAnchored;

        /// <summary>
        /// Rides the polyline with a labeled marker (icon + label): returns the world position ON the
        /// drawn polyline at <paramref name="headUT"/> (recorded frame), so the marker sits exactly on the
        /// corrected burn line instead of the body-fixed head (~96 deg off under the loop shift). It
        /// samples the leg's per-frame DRAWN points (<see cref="LegPolyline.scratchScaledSpace"/> - already
        /// conic-anchored by <see cref="TryAnchorLegToConicSeam"/>, or plain body-fixed for a non-anchored
        /// leg, so the marker always matches whatever the line actually shows - no separate rotation to
        /// drift) and interpolates by recorded-time fraction, then converts scaled-&gt;world. Returns false
        /// (caller keeps the body-fixed head) when the head is not inside a leg drawn THIS frame, so a stale
        /// scratch is never read. Call only after the Driver's LateUpdate (e.g. from OnGUI marker draw).
        /// </summary>
        internal static bool TryAnchorMarkerToPolyline(
            string recordingId, double headUT, out Vector3 worldPos)
        {
            // Thin wrapper preserving the original 3-arg contract; the reason/leg out-params are
            // only consumed by the marker tracer. Behavior is byte-identical to the diagnostics
            // overload below (same control flow, same returns).
            return TryAnchorMarkerToPolyline(
                recordingId, headUT, out worldPos, out _, out _);
        }

        /// <summary>
        /// Diagnostics overload of <see cref="TryAnchorMarkerToPolyline(string,double,out Vector3)"/>
        /// that ALSO reports WHY the ride did or did not happen (<paramref name="rideReason"/>) and,
        /// on a successful ride, the leg index (<paramref name="legIndex"/>). The ride LOGIC is
        /// unchanged - every branch sets the reason then takes the exact same return path as before -
        /// so the marker still rides or falls back identically; only the explanation is surfaced.
        /// </summary>
        internal static bool TryAnchorMarkerToPolyline(
            string recordingId, double headUT, out Vector3 worldPos,
            out MapRenderTrace.MarkerRideReason rideReason, out int legIndex)
        {
            worldPos = Vector3.zero;
            rideReason = MapRenderTrace.MarkerRideReason.FallbackNoCache;
            legIndex = -1;
            if (string.IsNullOrEmpty(recordingId)) return false;
            if (!polylineCache.TryGetValue(recordingId, out var set) || set.legs == null) return false;

            // A cache entry exists; the default now becomes "head fell outside every leg" unless a
            // leg matches below.
            rideReason = MapRenderTrace.MarkerRideReason.FallbackHeadOutsideLegs;
            int frame = Time.frameCount;
            for (int li = 0; li < set.legs.Length; li++)
            {
                var leg = set.legs[li];
                if (headUT < leg.startUT || headUT > leg.endUT) continue;
                int m = leg.PointCount;
                if (m < 2 || leg.scratchScaledSpace == null
                    || leg.recordedUTs == null || leg.recordedUTs.Length != m)
                {
                    rideReason = MapRenderTrace.MarkerRideReason.FallbackMissingRecordedUTs;
                    return false; // missing recorded-UT / scratch arrays -> cannot bracket (hard fallback)
                }
                if (leg.lastDrawnFrame != frame)
                {
                    // The leg matched the head but was not drawn this frame, so its scratch is stale.
                    // ResolveUndrawnLegFallback decides what to do by the leg's anchor state:
                    //  - CONIC-ANCHORED leg: the drawn line is rotated ~96 deg off the body-fixed head, so
                    //    snapping the caller to the body-fixed head would jump it off the line. Hold the last
                    //    on-line position if it is still fresh + near in UT (the dominant active-pan case);
                    //    a sustained stall expires the hold (frame-age / UT bound) and falls through to false.
                    //  - NON-anchored body-fixed leg (descent / atmospheric / surface): the drawn line IS the
                    //    raw body-fixed points, so the caller's fresh body-fixed head is already on the line
                    //    AND current. Return false WITHOUT a hold so the caller keeps that fresh head - the
                    //    descent-icon decouple: a stale hold would only lag the moving descent at warp / on
                    //    the IMGUI Layout-pass dropout. (TryHoldLastGood is skipped entirely, so the marker is
                    //    never pinned to a <=5 s-old position for a body-fixed descent leg.)
                    if (ResolveUndrawnLegFallback(leg.wasAnchored) == UndrawnLegFallback.HoldLastGoodOnLine
                        && TryHoldLastGood(recordingId, headUT, frame, out worldPos, out legIndex))
                    {
                        rideReason = MapRenderTrace.MarkerRideReason.HeldLastGood;
                        return true;
                    }
                    rideReason = leg.wasAnchored
                        ? MapRenderTrace.MarkerRideReason.FallbackLegNotDrawnThisFrame
                        : MapRenderTrace.MarkerRideReason.FallbackNonAnchoredUseHead;
                    return false;
                }

                // Bracket headUT between two recorded sample UTs and lerp the drawn (anchored) points.
                int idx = m - 2;
                for (int i = 0; i < m - 1; i++)
                {
                    if (headUT <= leg.recordedUTs[i + 1]) { idx = i; break; }
                }
                double u0 = leg.recordedUTs[idx], u1 = leg.recordedUTs[idx + 1];
                float frac = u1 > u0 ? (float)((headUT - u0) / (u1 - u0)) : 0f;
                if (frac < 0f) frac = 0f; else if (frac > 1f) frac = 1f;
                Vector3 scaled = Vector3.Lerp(
                    leg.scratchScaledSpace[idx], leg.scratchScaledSpace[idx + 1], frac);
                worldPos = (Vector3)ScaledSpace.ScaledToLocalSpace(scaled);
                rideReason = MapRenderTrace.MarkerRideReason.RodeLeg;
                legIndex = li;
                // Stamp the last-good cache so a transient ride dropout (active pan / inter-leg gap) can
                // hold this on-line position instead of snapping to the body-fixed head.
                lastGoodOnLine[recordingId] = new LastGoodOnLine
                {
                    worldPos = worldPos,
                    headUT = headUT,
                    frame = frame,
                    legIndex = li
                };
                return true;
            }

            // Head matched no leg's [start,end]. If it sits in an inter-leg GAP inside the recording's
            // overall span (a connector/deorbit seam between two drawn legs - the case that otherwise
            // starves a leg of its icon), hold the last on-line position. A head BEFORE the first leg or
            // PAST the last leg is a genuine orbital-phase exit and must fall through to the deep fallback.
            if (set.legs.Length > 0)
            {
                double firstStartUT = set.legs[0].startUT;
                double lastEndUT = set.legs[set.legs.Length - 1].endUT;
                if (IsHeadInInterLegGap(firstStartUT, lastEndUT, headUT)
                    && TryHoldLastGood(recordingId, headUT, frame, out worldPos, out legIndex))
                {
                    rideReason = MapRenderTrace.MarkerRideReason.HeldLastGood;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Cheap content-hash key for cache invalidation (§1.4). XORs every
        /// sample UT and every TrackSection start/end UT so a
        /// supersede-time re-cut that preserves the four counts and the
        /// rec.EndUT still flips the hash.
        /// </summary>
        internal static long ComputeContentHash(Recording rec)
        {
            if (rec == null) return 0;
            long hash = 0;
            int pointCount = rec.Points != null ? rec.Points.Count : 0;
            int segCount = rec.OrbitSegments != null ? rec.OrbitSegments.Count : 0;
            int sectionCount = rec.TrackSections != null ? rec.TrackSections.Count : 0;
            hash ^= pointCount;
            hash ^= (long)segCount << 16;
            hash ^= (long)sectionCount << 32;
            hash ^= BitConverter.DoubleToInt64Bits(rec.EndUT);
            if (rec.Points != null)
            {
                for (int i = 0; i < rec.Points.Count; i++)
                    hash ^= BitConverter.DoubleToInt64Bits(rec.Points[i].ut);
            }
            if (rec.TrackSections != null)
            {
                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    var s = rec.TrackSections[i];
                    hash ^= BitConverter.DoubleToInt64Bits(s.startUT);
                    hash ^= BitConverter.DoubleToInt64Bits(s.endUT);
                }
            }
            return hash;
        }

        /// <summary>
        /// Pure leg-construction (§1.3). Collects every non-orbital sample from
        /// the recording's TrackSections (dispatched per referenceFrame) plus the
        /// flat Recording.Points outside any section, drops samples inside an
        /// OrbitSegment interval (the orbit-arc covers them), then MERGES the
        /// remaining samples into one leg per contiguous non-orbital span.
        ///
        /// The merge is the important part: the recorder fragments a single burn
        /// into many short env-class sections (e.g. circularization or the
        /// trans-Munar relight each split into 5-7 sub-second ExoBallistic<->
        /// ExoPropulsive sections). One-leg-per-section plus the head-UT draw gate
        /// would then show only a short stub under the playback head. Merging
        /// contiguous samples (splitting only on a body change or an OrbitSegment
        /// coast falling between two samples) makes the whole burn render as one
        /// continuous line from the end of the previous orbit arc to the start of
        /// the next.
        ///
        /// Per-section source policy:
        /// - Absolute: walk section.frames (body-fixed lat/lon/alt).
        /// - Relative with non-null bodyFixedFrames: walk section.bodyFixedFrames.
        /// - Relative WITHOUT bodyFixedFrames: SKIP; reading
        ///   section.frames[i].latitude/longitude/altitude as lat/lon/alt would
        ///   place the leg deep inside the planet (the CLAUDE.md RELATIVE-frame
        ///   footgun: those fields are metre offsets along the anchor's local
        ///   x/y/z, NOT lat/lon/alt).
        /// </summary>
        /// <summary>
        /// Min recorded-time gap (seconds) between two consecutive same-body leg samples for the
        /// conic gap-fill to engage (playtest-12 straight-chord fix). Shorter gaps render fine as
        /// linear chords.
        /// </summary>
        internal const double GapFillMinSeconds = 15.0;

        /// <summary>Max synthetic interior points inserted per frameless gap.</summary>
        internal const int GapFillMaxPointsPerGap = 30;

        /// <summary>
        /// Seam (playtest-12 gap-fill): samples a recorded conic at one UT and returns the
        /// RECORDED-basis body-fixed (lat, lon, alt) triple (lon counter-rotated via
        /// <see cref="RecordedLongitudeAtUT"/> so it matches the recorded points around it). The
        /// Driver supplies a live Orbit-backed implementation; xUnit tests pass a synthetic one. The
        /// pure builder never touches KSP through this.
        /// </summary>
        internal delegate bool ConicGapSampler(
            OrbitSegment seg, double ut, out double lat, out double lon, out double alt);

        /// <summary>
        /// PURE gap-fill pass (playtest-12 straight-chord fix): the Duna landing leg merges Absolute
        /// frame clusters ACROSS frameless OrbitalCheckpoint spans whose covering conics are
        /// BELOW-SURFACE (excluded from the orbital cover by FIX #27, so no arc draws them and the
        /// leg chords straight across - the "straight segment right before the landing"). For every
        /// consecutive same-body sample pair separated by more than <see cref="GapFillMinSeconds"/>
        /// whose open span is covered by a below-surface recorded conic, inserts up to
        /// <see cref="GapFillMaxPointsPerGap"/> interior points sampled from THAT CONIC via
        /// <paramref name="sampler"/> - the recorded descent shape itself, so the chord gains the
        /// exact recorded curvature. RENDER-time only: the recording's own lists are never touched
        /// (points are appended to the build stream copy). Returns the number of points inserted
        /// (caller re-sorts the stream when &gt; 0). No-op without a sampler or surface provider
        /// (below-surface classification needs body radii). xUnit-testable with synthetic seams.
        /// </summary>
        internal static int FillFramelessGapsFromConics(
            List<TrajectoryPoint> pts, Recording rec, BodySurfaceProvider surface,
            ConicGapSampler sampler)
        {
            if (pts == null || pts.Count < 2 || rec == null || rec.OrbitSegments == null
                || sampler == null || surface == null)
                return 0;

            int inserted = 0;
            int originalCount = pts.Count;
            for (int i = 0; i < originalCount - 1; i++)
            {
                var p = pts[i];
                var q = pts[i + 1];
                if (!string.Equals(p.bodyName, q.bodyName, StringComparison.Ordinal)) continue;
                double dt = q.ut - p.ut;
                if (dt <= GapFillMinSeconds) continue;

                // The covering BELOW-SURFACE conic (the recorded descent shape). Above-surface conics
                // never reach here: their cover intervals split the leg at step (2)'s
                // OrbitalIntervalBetween, so a gap inside one cannot exist within a single run.
                int segIdx = -1;
                for (int s = 0; s < rec.OrbitSegments.Count; s++)
                {
                    var seg = rec.OrbitSegments[s];
                    if (seg.endUT <= seg.startUT) continue;
                    if (!string.Equals(seg.bodyName, p.bodyName, StringComparison.Ordinal)) continue;
                    if (seg.startUT >= q.ut || seg.endUT <= p.ut) continue; // no overlap with the gap
                    if (!IsOrbitSegmentBelowSurface(seg, surface)) continue;
                    segIdx = s;
                    break;
                }
                if (segIdx < 0) continue;

                var src = rec.OrbitSegments[segIdx];
                // Sample only where the conic actually covers the gap (the checkpoint conic may start
                // a few seconds after the cluster's last frame).
                double from = System.Math.Max(p.ut, src.startUT);
                double to = System.Math.Min(q.ut, src.endUT);
                if (to - from <= GapFillMinSeconds) continue;
                int n = (int)System.Math.Min(GapFillMaxPointsPerGap, System.Math.Floor((to - from) / 7.0));
                if (n < 1) continue;
                for (int k = 1; k <= n; k++)
                {
                    double ut = from + (to - from) * k / (n + 1);
                    if (!sampler(src, ut, out double lat, out double lon, out double alt)) break;
                    pts.Add(new TrajectoryPoint
                    {
                        ut = ut,
                        latitude = lat,
                        longitude = lon,
                        altitude = alt,
                        bodyName = p.bodyName,
                    });
                    inserted++;
                }
            }
            return inserted;
        }

        internal static List<LegPolyline> BuildLegsForRecording(
            Recording rec, BodySurfaceProvider surface = null, ConicGapSampler gapSampler = null)
        {
            var legs = new List<LegPolyline>();
            if (rec == null) return legs;

            var orbitalIntervals = ComputeOrbitalCoverIntervals(rec.OrbitSegments, surface);

            // FIX #27: count + report the degenerate below-SURFACE segments
            // the cover now excludes (one-shot, build-time). When any are
            // excluded the descent samples they used to drop merge into the
            // descent leg, so log the resulting leg span window for diagnosis.
            int excludedBelowSurfaceSegments = 0;
            if (surface != null && rec.OrbitSegments != null)
            {
                for (int i = 0; i < rec.OrbitSegments.Count; i++)
                {
                    var seg = rec.OrbitSegments[i];
                    if (seg.endUT <= seg.startUT) continue;
                    if (IsOrbitSegmentBelowSurface(seg, surface))
                        excludedBelowSurfaceSegments++;
                }
            }

            int skippedRelativeWithoutBodyFixed = 0;
            int sectionPointCount = 0;
            int flatPointCount = 0;

            // (1) Collect every non-orbital sample into one stream. Per-section
            //     dispatch + the orbital-interval filter are unchanged; the merge
            //     into one leg per contiguous span happens in step (2).
            var pts = new List<TrajectoryPoint>();
            if (rec.TrackSections != null)
            {
                for (int s = 0; s < rec.TrackSections.Count; s++)
                {
                    var section = rec.TrackSections[s];
                    List<TrajectoryPoint> source = ResolveSourceListForSection(section);
                    if (source == null)
                    {
                        if (section.referenceFrame == ReferenceFrame.Relative)
                            skippedRelativeWithoutBodyFixed++;
                        continue;
                    }
                    var filtered = FilterPointsForLeg(source, section.startUT, section.endUT, orbitalIntervals);
                    pts.AddRange(filtered);
                    sectionPointCount += filtered.Count;
                }
            }

            // Flat Recording.Points OUTSIDE every section range (and outside any
            // orbital interval) fold into the same stream (pre/post-section
            // fallback coverage).
            if (rec.Points != null && rec.Points.Count > 0)
            {
                for (int i = 0; i < rec.Points.Count; i++)
                {
                    var p = rec.Points[i];
                    if (IsInsideAnySection(p.ut, rec.TrackSections)) continue;
                    if (IsInsideAnyOrbitalInterval(p.ut, orbitalIntervals)) continue;
                    pts.Add(p);
                    flatPointCount++;
                }
            }

            // (2) UT-sort and split into legs. A new leg starts on a body change
            //     or when an OrbitSegment coast lies between two consecutive
            //     non-orbital samples (the orbit arc owns that span). Otherwise
            //     contiguous same-body samples MERGE into one leg, so the head-UT
            //     draw gate shows a whole non-orbital span (the full burn arc)
            //     instead of a single fragmented section.
            pts.Sort((a, b) => a.ut.CompareTo(b.ut));

            // Playtest-12 straight-chord fix: fill frameless below-surface-conic gaps with the
            // recorded conic's own shape so the landing descent renders curved instead of a long
            // linear chord. Build-time only; the recording is never touched.
            int gapFilled = FillFramelessGapsFromConics(pts, rec, surface, gapSampler);
            if (gapFilled > 0)
                pts.Sort((a, b) => a.ut.CompareTo(b.ut));

            var run = new List<TrajectoryPoint>();
            string runBody = null;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                if (run.Count > 0)
                {
                    var prev = run[run.Count - 1];
                    bool sameBody = string.Equals(p.bodyName, runBody, StringComparison.Ordinal);
                    // Dedupe a sample shared at a section boundary ONLY within the same
                    // body, so an SOI crossing recorded at a single UT still starts a new
                    // leg below instead of being silently dropped.
                    if (sameBody && p.ut == prev.ut) continue;
                    bool breakRun =
                        !sameBody || OrbitalIntervalBetween(prev.ut, p.ut, orbitalIntervals);
                    if (breakRun)
                    {
                        FlushPolylineRun(run, runBody, legs);
                        run.Clear();
                    }
                }
                if (run.Count == 0) runBody = p.bodyName;
                run.Add(p);
            }
            FlushPolylineRun(run, runBody, legs);

            ParsekLog.Verbose(Tag,
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Polyline build: rec={0} legs={1} (sectionPts={2} flatPts={3} skippedRelNoBodyFixed={4} excludedBelowSurfaceSegs={5} gapFilled={6})",
                    rec.RecordingId,
                    legs.Count, sectionPointCount, flatPointCount,
                    skippedRelativeWithoutBodyFixed, excludedBelowSurfaceSegments, gapFilled));

            // FIX #27 one-shot: when the cover excluded degenerate
            // below-surface segments, the descent samples they used to drop
            // now merge into a leg. Report that leg's span (the last leg, which
            // is the descent tail) so a coverage hole is diagnosable from the log.
            if (excludedBelowSurfaceSegments > 0 && legs.Count > 0)
            {
                var descentLeg = legs[legs.Count - 1];
                ParsekLog.Verbose(Tag,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "excluded {0} below-surface orbit segments from cover rec={1} -> descent leg [{2:F1},{3:F1}]",
                        excludedBelowSurfaceSegments, rec.RecordingId,
                        descentLeg.startUT, descentLeg.endUT));
            }

            return legs;
        }

        /// <summary>
        /// Appends a merged non-orbital run to <paramref name="legs"/> as one leg
        /// (downsampled, endpoints preserved). Runs shorter than two points or
        /// with no resolvable body are dropped.
        /// </summary>
        private static void FlushPolylineRun(
            List<TrajectoryPoint> run, string body, List<LegPolyline> legs)
        {
            if (run == null || run.Count < 2) return;
            if (string.IsNullOrEmpty(body)) return;
            legs.Add(BuildLegFromBodyFixedPoints(run, body));
        }

        /// <summary>
        /// True when an OrbitSegment coast interval overlaps the OPEN span between
        /// two consecutive non-orbital samples, i.e. an orbit arc owns the gap so
        /// the polyline must break into a new leg there instead of drawing a chord
        /// across the orbit. Touching exactly at an interval endpoint (a burn leg
        /// meeting the orbit-arc boundary) does not count. Pure / xUnit-testable.
        /// </summary>
        internal static bool OrbitalIntervalBetween(
            double a, double b, List<(double startUT, double endUT)> intervals)
        {
            if (intervals == null) return false;
            double lo = a < b ? a : b;
            double hi = a < b ? b : a;
            for (int i = 0; i < intervals.Count; i++)
            {
                if (intervals[i].endUT > lo && intervals[i].startUT < hi)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the per-section source list per §1.3 policy. Null for
        /// Relative sections without bodyFixedFrames coverage and for
        /// OrbitalCheckpoint sections.
        /// </summary>
        internal static List<TrajectoryPoint> ResolveSourceListForSection(TrackSection section)
        {
            if (section.referenceFrame == ReferenceFrame.Absolute)
                return section.frames;
            if (section.referenceFrame == ReferenceFrame.Relative)
                return section.bodyFixedFrames; // null when no body-fixed coverage
            // OrbitalCheckpoint sections have no per-frame trajectory points; the
            // orbit-arc renderer covers those UTs.
            return null;
        }

        /// <summary>
        /// Builds a single leg from a body-coherent set of body-fixed
        /// TrajectoryPoints. Downsamples to <see cref="MaxPolylinePointsPerLeg"/>
        /// keeping the first + last sample and uniform-striding the
        /// remaining (cap - 2) interior samples (§1.3 endpoint preservation).
        ///
        /// Stores ONLY the recorded body-fixed (lat, lon, alt) triples; no
        /// geometry conversion happens here so the builder stays pure +
        /// xUnit-testable. The Driver converts each triple to a live world
        /// position per frame via <c>CelestialBody.GetWorldSurfacePosition</c>.
        /// </summary>
        internal static LegPolyline BuildLegFromBodyFixedPoints(
            List<TrajectoryPoint> points, string bodyName)
        {
            var sampled = DownsamplePreservingEndpoints(points, MaxPolylinePointsPerLeg);
            int m = sampled.Count;
            var lats = new double[m];
            var lons = new double[m];
            var alts = new double[m];
            var uts = new double[m];
            for (int i = 0; i < m; i++)
            {
                var p = sampled[i];
                lats[i] = p.latitude;
                lons[i] = p.longitude;
                alts[i] = p.altitude;
                uts[i] = p.ut;
            }
            return new LegPolyline
            {
                lats = lats,
                lons = lons,
                alts = alts,
                recordedUTs = uts,
                scratchScaledSpace = new Vector3[m],
                bodyName = bodyName,
                startUT = sampled[0].ut,
                endUT = sampled[m - 1].ut
            };
        }

        /// <summary>
        /// Downsamples a list of points keeping the first + last sample and
        /// uniform-striding the remaining (cap - 2) interior samples. Pure;
        /// reused by the unit tests directly.
        /// </summary>
        internal static List<TrajectoryPoint> DownsamplePreservingEndpoints(
            List<TrajectoryPoint> points, int cap)
        {
            if (points == null || points.Count == 0) return new List<TrajectoryPoint>();
            if (cap < 2) cap = 2;
            int n = points.Count;
            if (n <= cap)
            {
                return new List<TrajectoryPoint>(points);
            }

            var result = new List<TrajectoryPoint>(cap);
            result.Add(points[0]);
            int interiorCap = cap - 2;
            int interiorPool = n - 2;
            for (int i = 0; i < interiorCap; i++)
            {
                // Stride to pick a representative from the interior. Adds 1
                // to skip the first sample, +0.5 to centre the step bucket.
                int srcIdx = 1 + (int)((i + 0.5) * interiorPool / (double)interiorCap);
                if (srcIdx < 1) srcIdx = 1;
                if (srcIdx > n - 2) srcIdx = n - 2;
                result.Add(points[srcIdx]);
            }
            result.Add(points[n - 1]);
            return result;
        }

        /// <summary>
        /// Pure (FIX #27): is this OrbitSegment DEGENERATE, i.e. does its drawn
        /// Keplerian conic plunge below the body SURFACE so the orbit line cannot
        /// usably trace it? True when the segment's periapsis radius is below the
        /// body radius (periapsis altitude &lt; 0).
        ///
        /// Periapsis radius is <c>sma * (1 - ecc)</c>, which is valid for the
        /// hyperbolic case too (sma &lt; 0, ecc &gt; 1 give a positive periapsis
        /// radius). The boundary is the SURFACE, not the atmosphere top
        /// (CHANGE 2): the orbit line only becomes UNUSABLE when the conic is
        /// degenerate (dives under the ground). A valid conic that merely grazes
        /// the atmosphere at periapsis but stays above the surface (periapsis
        /// altitude in [0, atmosphereDepth]) is still drawn correctly by the orbit
        /// line, so the polyline must NOT claim it - excluding such an in-space
        /// eccentric orbit (e.g. a Kerbin parking orbit with periapsis a few km
        /// above the ground and a high apoapsis) would double-draw it. For the
        /// Duna arrival the final descent segments have periapsis well BELOW the
        /// surface (~-17 km), so they are still excluded and the descent hole is
        /// still fixed.
        ///
        /// Returns false (segment kept as orbit-owned) when the provider is null
        /// or the body is unknown, so a normal in-space orbit segment is
        /// UNCHANGED. Gated strictly on periapsis below the surface, so an
        /// ordinary parking / transfer / grazing orbit is never excluded.
        /// </summary>
        internal static bool IsOrbitSegmentBelowSurface(
            OrbitSegment segment, BodySurfaceProvider surface)
        {
            if (surface == null) return false;
            if (string.IsNullOrEmpty(segment.bodyName)) return false;
            if (!surface(segment.bodyName, out BodySurfaceInfo info)) return false;
            double periapsisRadius = segment.semiMajorAxis * (1.0 - segment.eccentricity);
            return periapsisRadius < info.radius;
        }

        /// <summary>
        /// Computes the union of every OrbitSegment's [startUT, endUT]
        /// interval. Points whose UT falls inside the union are dropped
        /// from the polyline at filter time (the orbit-arc covers them).
        ///
        /// FIX #27: a degenerate below-SURFACE segment (see
        /// <see cref="IsOrbitSegmentBelowSurface"/>) is EXCLUDED from the
        /// cover so the polyline picks up the descent samples the unusable
        /// orbit line abandons there, tiling the two surfaces without a gap. When
        /// <paramref name="surface"/> is null (the xUnit builder default) no
        /// segment is excluded, so a recording with no degenerate segments is
        /// byte-identical to the pre-fix behaviour.
        /// </summary>
        internal static List<(double startUT, double endUT)> ComputeOrbitalCoverIntervals(
            List<OrbitSegment> segments, BodySurfaceProvider surface = null)
        {
            var intervals = new List<(double, double)>();
            if (segments == null || segments.Count == 0) return intervals;
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                if (s.endUT <= s.startUT) continue;
                if (IsOrbitSegmentBelowSurface(s, surface)) continue;
                intervals.Add((s.startUT, s.endUT));
            }
            return intervals;
        }

        internal static bool IsInsideAnyOrbitalInterval(
            double ut, List<(double startUT, double endUT)> intervals)
        {
            if (intervals == null) return false;
            for (int i = 0; i < intervals.Count; i++)
            {
                var iv = intervals[i];
                if (ut >= iv.startUT && ut <= iv.endUT) return true;
            }
            return false;
        }

        internal static bool IsInsideAnySection(double ut, List<TrackSection> sections)
        {
            if (sections == null) return false;
            for (int i = 0; i < sections.Count; i++)
            {
                var s = sections[i];
                if (ut >= s.startUT && ut <= s.endUT) return true;
            }
            return false;
        }

        /// <summary>
        /// PURE forward-arc segment selector (Step 3 C). Given the EFFECTIVE (re-aim-resolved) orbit
        /// segments, the computed forward window <c>(<paramref name="forwardWindowStartUT"/>,
        /// <paramref name="forwardStopUT"/>]</c>, and the ghost's current <paramref name="headUT"/>, returns
        /// the INDICES of the segments to draw as FORWARD arcs ahead of the icon. A segment is selected when
        /// it:
        ///   - overlaps the forward window (<c>seg.startUT &lt; forwardStopUT &amp;&amp;
        ///     seg.endUT &gt; forwardWindowStartUT</c>),
        ///   - is NOT the segment the icon currently sits on (the CURRENT arc is drawn in full by the stock
        ///     <c>OrbitRenderer</c> + <c>GhostOrbitArcPatch</c>, so the forward pass must never double-draw
        ///     it - this keeps "the current element renders exactly as today"),
        ///   - is ABOVE the surface (<see cref="IsOrbitSegmentBelowSurface"/> false): a descent that dips
        ///     below the surface is drawn as a forward LEG (B'), not an arc (Step 3 / FIX #27),
        ///   - has a positive span (<c>endUT &gt; startUT</c>).
        /// An empty forward range (<c>forwardStopUT &lt;= forwardWindowStartUT</c>) selects nothing. Pure
        /// (the only KSP coupling, the below-surface test, is injected via <paramref name="surface"/>); the
        /// live Driver then samples each selected segment's Kepler conic. xUnit-testable.
        /// </summary>
        internal static List<int> SelectForwardArcSegmentIndices(
            List<OrbitSegment> effectiveSegments,
            double forwardWindowStartUT, double forwardStopUT, double headUT,
            BodySurfaceProvider surface)
        {
            var indices = new List<int>();
            SelectForwardArcSegmentIndices(
                effectiveSegments, forwardWindowStartUT, forwardStopUT, headUT, surface, indices);
            return indices;
        }

        /// <summary>
        /// Hot-path overload (forward-render review finding): fills the caller-provided
        /// <paramref name="indices"/> buffer (clear-and-fill) instead of allocating a fresh
        /// <c>List&lt;int&gt;</c> each call. The Driver's forward decide pass runs this once per
        /// leg-bearing committed recording per frame on the multi-ghost map path, so it reuses a single
        /// per-Driver scratch list to avoid sustained Gen-0 churn proportional to the ghost count. The
        /// allocating overload above wraps this for tests / one-shot callers. Pure selection logic is
        /// unchanged: same current-arc exclusion, below-surface skip, and half-open window overlap test.
        /// </summary>
        internal static void SelectForwardArcSegmentIndices(
            List<OrbitSegment> effectiveSegments,
            double forwardWindowStartUT, double forwardStopUT, double headUT,
            BodySurfaceProvider surface, List<int> indices)
        {
            if (indices == null) return;
            indices.Clear();
            if (effectiveSegments == null) return;
            if (forwardStopUT <= forwardWindowStartUT) return;
            for (int i = 0; i < effectiveSegments.Count; i++)
            {
                var seg = effectiveSegments[i];
                if (seg.endUT <= seg.startUT) continue;
                // The CURRENT arc (icon's element) is drawn by stock; never forward-draw it.
                if (headUT >= seg.startUT && headUT < seg.endUT) continue;
                // Below-surface descent conic -> drawn as a forward leg, not an arc.
                if (IsOrbitSegmentBelowSurface(seg, surface)) continue;
                // Overlap the half-open forward window.
                if (seg.startUT < forwardStopUT && seg.endUT > forwardWindowStartUT)
                    indices.Add(i);
            }
        }

        /// <summary>
        /// Filters a per-section sample list to the section's UT range AND
        /// drops samples covered by an orbital interval (the orbit-arc
        /// already draws them).
        /// </summary>
        internal static List<TrajectoryPoint> FilterPointsForLeg(
            List<TrajectoryPoint> source, double sectionStartUT, double sectionEndUT,
            List<(double startUT, double endUT)> orbitalIntervals)
        {
            var result = new List<TrajectoryPoint>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                var p = source[i];
                if (p.ut < sectionStartUT || p.ut > sectionEndUT) continue;
                if (IsInsideAnyOrbitalInterval(p.ut, orbitalIntervals)) continue;
                result.Add(p);
            }
            return result;
        }

        /// <summary>
        /// Reason a recording is excluded from the STATIC polyline pass.
        /// </summary>
        internal enum PolylineStaticSkipReason
        {
            None,
            NullRecording,
            Debris,
            NoTrajectoryPoints,
            SuppressedByChainFilter,
        }

        /// <summary>
        /// Pure STATIC visibility filter for the full-path polyline (§0.1).
        ///
        /// The polyline is a static bridge drawn for the WHOLE recording
        /// regardless of where the playback head currently is, so it must
        /// NOT inherit <c>ParsekTrackingStation.ClassifyAtmosphericMarkerSkip</c>'s
        /// per-head-UT gates (<c>OrbitSegmentActive</c> / <c>NativeIconActive</c>):
        /// those blink the whole polyline out whenever the head enters an
        /// orbital phase or while an un-suppressed ghost ProtoVessel exists,
        /// which defeats the bridge. We replicate only the recording-level
        /// static subset of that helper:
        /// debris exclusion, no-committed-trajectory, and suppression. The
        /// missing-body filter and the loop-unit <c>renderHidden</c> filter
        /// are applied separately by the Driver (renderHidden via
        /// <c>ResolveTrackingStationSampleUT</c>; missing body per leg).
        /// </summary>
        internal static PolylineStaticSkipReason ClassifyPolylineStaticSkip(
            Recording rec, HashSet<string> suppressedIds)
        {
            if (rec == null) return PolylineStaticSkipReason.NullRecording;
            if (rec.IsDebris) return PolylineStaticSkipReason.Debris;
            if (rec.Points == null || rec.Points.Count == 0)
                return PolylineStaticSkipReason.NoTrajectoryPoints;
            if (suppressedIds != null && suppressedIds.Contains(rec.RecordingId))
                return PolylineStaticSkipReason.SuppressedByChainFilter;
            return PolylineStaticSkipReason.None;
        }

        /// <summary>
        /// Pure per-recording WALK-INCLUSION decision for the Driver's renderHidden gate
        /// (launch-&gt;escape seam render fix). After the static skip + renderHidden resolve, the Driver
        /// must decide whether to walk this recording at all and, if so, which heads contribute legs.
        ///
        /// The renderHidden flag is the PRIMARY head's "loopUT is outside this member's window" verdict
        /// (the continuing through-line instance N is at a downstream orbital phase). The OLD gate skipped
        /// the whole recording whenever the primary was hidden, which dropped a launch recording whose
        /// boundary-overlap SECONDARY (the early-launching instance N+1's in-SOI ascent) was live in its
        /// own window - so the secondary's forward ascent polyline + escape conic never drew.
        ///
        /// Decision table (skip / drawPrimaryLegs / drawSecondaryLegs):
        /// - primaryRenders &amp;&amp; !hasSecondary  -&gt; walk, primary only (the common case, byte-identical)
        /// - primaryRenders &amp;&amp;  hasSecondary  -&gt; walk, primary + secondary. REACHED in the single-recording
        ///   validated case (review H1): the launch member's window is the whole recording, so during the
        ///   borrow window the primary (instance N near the destination) is still in-window AND the
        ///   secondary (N+1 ascent) is live, so BOTH forward passes run for the SAME recording in one frame
        ///   - which is why the forward-arc cache is namespaced per primary/secondary (ForwardArcDictKey).
        /// - !primaryRenders &amp;&amp;  hasSecondary -&gt; walk, secondary ONLY (the fix: launch ascent renders while
        ///   the primary is at the destination)
        /// - !primaryRenders &amp;&amp; !hasSecondary -&gt; SKIP (the old renderHidden skip, unchanged)
        ///
        /// Inert for every non-launch-hold member / aligned loop: hasSecondary is always false there, so the
        /// outcome collapses to (walk iff primaryRenders), exactly the old gate. Pure; unit-tested.
        /// </summary>
        internal static void DecidePolylineWalkInclusion(
            bool primaryRenders, bool hasSecondary,
            out bool skip, out bool drawPrimaryLegs, out bool drawSecondaryLegs)
        {
            skip = !primaryRenders && !hasSecondary;
            drawPrimaryLegs = !skip && primaryRenders;
            drawSecondaryLegs = !skip && hasSecondary;
        }

        // ------------------------------------------------------------------
        // Per-leg draw mechanics (Phase 8b.0 extraction)
        //
        // The single-leg build + conic-anchor + VectorLine submit pipeline,
        // pulled OUT of the autonomous Driver's per-frame walk verbatim and
        // parameterized on EXPLICIT inputs so a future TracedPathTreatment
        // (Phase 8b.1) can draw one leg through the SAME code instead of going
        // through the Driver's CommittedRecordings walk. The Driver now calls
        // TryDrawLeg per leg, so its per-frame output is byte-identical to the
        // pre-extraction inlined body (the code below IS that body, with the
        // captured locals passed as arguments). This is a mechanical move +
        // parameterization, not a logic change: no draw decision, no anchor
        // math, no VectorLine state, and no activation/deactivation differs.
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds (lazily, once), positions, fills, conic-anchors, and draws a
        /// single non-orbital leg's own VectorLine for ONE frame, exactly as the
        /// Driver's per-leg inner loop did inline. Scene-agnostic: every input it
        /// needs is passed explicitly (the resolved <paramref name="body"/>, the
        /// owning <paramref name="rec"/> for the conic-anchor, the target
        /// <paramref name="targetLayer"/> and <paramref name="drawFrame"/>, and the
        /// <paramref name="recordingId"/> / <paramref name="legIndex"/> used only to
        /// name the lazily-inflated line). Mutates <paramref name="leg"/> in place
        /// (inflates <c>vectorLine</c>, fills <c>scratchScaledSpace</c>, stamps
        /// <c>lastDrawnFrame</c>) so the caller writes the struct back into its
        /// cached array, identical to the old <c>set.legs[li] = leg</c>.
        ///
        /// Returns true when the leg was drawn this frame (the old <c>anyDrawn</c>
        /// signal), false when it was skipped because it has fewer than two points
        /// or its VectorLine could not be inflated (identical skip conditions to the
        /// inlined body; the body-missing skip stays at the call site since it owns
        /// the body resolution + the rate-limited missing-body log).
        /// </summary>
        internal static bool TryDrawLeg(
            ref LegPolyline leg, Recording rec, CelestialBody body,
            int targetLayer, int drawFrame, string recordingId, int legIndex,
            bool requireConicAnchor = false)
        {
            int m = leg.PointCount;
            if (m < 2) return false;

            // Inflate this leg's own VectorLine lazily. One line PER
            // leg: a single shared line drawn once per leg via
            // drawStart/drawEnd range slicing does NOT work, because
            // VectorLine.Draw3D() zeroes every vertex outside the
            // current window on each call, leaving only the last leg.
            // Bug 1: rebuild the line when the map-line mode flipped (3D Draw3D <-> 2D Draw at the
            // far-zoom threshold) - Vectrosity's in-place swap leaves the 2D canvas line non-rendering, so
            // (like stock) the line is recreated on flip. See RebuildLineForMode.
            int legWantMode = MapLineUses3D() ? 1 : 2;
            if (leg.vectorLine != null && leg.lineMode != legWantMode)
                leg.vectorLine = RebuildLineForMode(leg.vectorLine, m);
            if (leg.vectorLine == null)
                leg.vectorLine = BuildLegVectorLine(recordingId, legIndex, m);
            leg.lineMode = legWantMode;
            if (leg.vectorLine == null) return false;

            // Layer 31 always; in 3D mode also zero the world transform so the absolute-scaled-space mesh
            // is not double-transformed (matches OrbitRendererBase's REDRAW path). In 2D mode the line is
            // a Vectrosity canvas child and the world-position reset would shove it off the canvas plane
            // (Bug 1 zoom-out vanish), so it is skipped there. See PrepareMapLineTransform.
            PrepareMapLineTransform(leg.vectorLine, targetLayer);

            // CRITICAL geometry (warp-strobe fix, take 2 - strobe-free AND never invisible).
            // A body-fixed surface point must follow the planet's spin. Two failed approaches and
            // why, from the strobe probe:
            //   (a) ABSOLUTE: scratchScaledSpace[i] = LocalToScaledSpace(GetWorldSurfacePosition(.))
            //       STROBES under warp into a parallel duplicate - ScaledSpace.totalOffset (the
            //       scaled-origin recentering) oscillates every RENDER frame and our non-registered
            //       VectorLine inherits it (|dPlive| up to ~750 scaled units, alternating with 0).
            //   (b) FREEZE: capture each point in scaledBody-local once and re-project - kills the
            //       strobe but goes INVISIBLE under warp, because a capture taken at a strobe phase
            //       bakes a bad totalOffset into the local and lands the line off-screen until a
            //       clean 1x recapture.
            // The probe shows GetWorldSurfacePosition is frame-stable (|dWorld|=0) and
            // scaledBody.transform is a registered ScaledSpace object whose position is frame-stable
            // (|dPfrozen|~0). So build the point from the STABLE pieces only: the scaled body centre
            // (scaledXform.position) plus the body-relative surface offset (world - body.position) *
            // invScale. That offset is totalOffset- AND floating-origin-free (both terms live in the
            // same frame, so it cancels), and equals LocalToScaledSpace(world) exactly when
            // scaledXform.position == LocalToScaledSpace(body.position) - but it never touches the
            // strobing totalOffset. Recomputed LIVE every frame: strobe-free at all warps, and with
            // no capture it can never go stale / invisible. scaledXform also feeds the conic anchor.
            var scaledBody = body.scaledBody;
            Transform scaledXform = scaledBody != null ? scaledBody.transform : null;
            bool parityFilled = false;
            if (scaledXform != null)
            {
                Vector3 bodyCentreScaled = scaledXform.position;
                double invScale = ScaledSpace.InverseScaleFactor;
                Vector3d bodyPos = body.position;
                // PARITY SNAPSHOT (tracing-only): record the body-relative offsets THIS draw builds the
                // vertices from, plus the exact centre float added in, so the parity capture diffs the
                // drawn points3 against a SAME-FRAME reference. Re-evaluating the surface positions /
                // centre at capture time (a different part of the frame) skews the reference by a frame
                // of body translation + rotation (x warp) - pure artifact, not draw drift. Null / not
                // stamped in normal play: zero cost while tracing is off.
                double[] parityRel = null;
                if (MapRenderTrace.IsEnabled)
                {
                    parityRel = leg.parityRecordedRel;
                    if (parityRel == null || parityRel.Length != m * 3)
                        parityRel = new double[m * 3];
                }
                for (int i = 0; i < m; i++)
                {
                    Vector3d world = body.GetWorldSurfacePosition(
                        leg.lats[i], leg.lons[i], leg.alts[i]);
                    Vector3d rel = world - bodyPos;
                    leg.scratchScaledSpace[i] = bodyCentreScaled + (Vector3)(rel * invScale);
                    if (parityRel != null)
                    {
                        parityRel[i * 3] = rel.x;
                        parityRel[i * 3 + 1] = rel.y;
                        parityRel[i * 3 + 2] = rel.z;
                    }
                }
                if (parityRel != null)
                {
                    leg.parityRecordedRel = parityRel;
                    leg.parityDrawCentreScaled = bodyCentreScaled;
                    parityFilled = true; // frame stamped at the draw write-back below
                }
            }
            else
            {
                // No scaled body available (should not happen in map view): fall back to the
                // direct absolute path. Strobes under warp, but at least renders on the surface.
                // No parity snapshot: this branch strobes by design, so the parity lens skips it
                // (parityDrawFrame stays != lastDrawnFrame).
                for (int i = 0; i < m; i++)
                {
                    Vector3d world = body.GetWorldSurfacePosition(
                        leg.lats[i], leg.lons[i], leg.alts[i]);
                    leg.scratchScaledSpace[i] =
                        (Vector3)ScaledSpace.LocalToScaledSpace(world);
                }
            }

            // CONIC ANCHOR: for a vacuum maneuver between two orbits (escape burn, orbit
            // raise) rotate the captured body-fixed scaled points onto the faithful bracketing
            // conic seam so the leg CONNECTS the loiter/hyperbola lines instead of drawing
            // ~96 deg off under the loop shift. No-op for legs not bracketed both sides (launch
            // ascent / descent-to-surface) and where body-fixed already matches the conic.
            bool anchored = TryAnchorLegToConicSeam(rec, leg, body, scaledXform);
            // Persist this draw's anchor outcome so the marker-ride fallback knows whether the drawn line
            // is conic-rotated off the body-fixed head (hold the last on-line point) or is the raw
            // body-fixed points (the caller's fresh body-fixed head is already on the line). Written back
            // into the cached leg via the same struct write-back as lastDrawnFrame below.
            leg.wasAnchored = anchored;

            // RUN-LEG body-fixed hide (playtest-5 rule, 2026-06-09): a persistent run leg (icon NOT on
            // it, requireConicAnchor=true) that could not be anchored to the inertial conic seam would
            // draw BODY-FIXED and visibly rotate with the planet against the inertial arcs (the observed
            // gap-then-overlap sweep at the chain handoff). Skip the draw - the deactivation sweep hides
            // any previously drawn line this frame. The decide-side IsRunLegAnchorCandidate pre-filter
            // already drops one-sided legs; this catches the residual-rejected remainder (the
            // seam-does-not-meet-the-leg guard inside TryAnchorLegToConicSeam, e.g. a mis-bracketed
            // arrival hyperbola). Head-gated current legs (requireConicAnchor=false) keep drawing
            // body-fixed - the live ghost is glued to the pad/terrain there, so body-fixed is correct.
            if (requireConicAnchor && !anchored)
            {
                // Invalidate the tracing-only parity snapshot: the fill above already overwrote the
                // snapshot array IN PLACE (it is aliased by the cache), but this leg will NOT draw this
                // frame - points3 still hold the PREVIOUS draw's (possibly conic-anchored) vertices.
                // Without this, the stale stamps can be pairwise EQUAL (parityDrawFrame ==
                // lastDrawnFrame, both from the prior drawn frame), so the capture's coherence check
                // would pass and diff the old anchored points3 against the fresh RAW snapshot - a false
                // parity-drift of anchor-rotation magnitude on the anchored->seam-rejected transition.
                // 0 = "never filled"; the next successful draw refills and restamps.
                leg.parityDrawFrame = 0;
                ParsekLog.VerboseRateLimited(Tag, "runleg-anchor-reject." + recordingId,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Run leg hidden (conic anchor unavailable): rec={0} leg={1} [{2:F1},{3:F1}] " +
                        "body={4} - body-fixed leg would rotate against the inertial run",
                        recordingId, legIndex, leg.startUT, leg.endUT, leg.bodyName ?? "(null)"),
                    3.0);
                return false;
            }

            CopyLegIntoVectorLine(leg.vectorLine, leg.scratchScaledSpace, 0);
            leg.vectorLine.drawStart = 0;
            leg.vectorLine.drawEnd = m - 1;
            // Reactivate if a prior frame's sweep hid this leg, then
            // draw and stamp the frame. The single write-back below
            // persists BOTH the lazily-inflated line AND the frame
            // stamp into the cached array (set.legs is the same array
            // reference the dict holds, so writing set.legs[li]
            // carries through without re-storing the struct).
            if (!leg.vectorLine.active) leg.vectorLine.active = true;
            // Bug 1 fix: draw in the stock map-line mode (Draw3D below the far-zoom threshold, 2D Draw()
            // past it) so the leg never vanishes on zoom-out. See DrawMapLine.
            DrawMapLine(leg.vectorLine);
            leg.lastDrawnFrame = drawFrame;
            // Stamp the parity snapshot as belonging to THIS draw only after the draw actually happened.
            // NOTE: a stale stamp alone is NOT protection - the fill mutates the aliased snapshot array
            // in place, and the stale parityDrawFrame can EQUAL the stale lastDrawnFrame (both from the
            // prior drawn frame), which passes the capture's coherence check. The anchor-reject early-out
            // above therefore invalidates the stamp EXPLICITLY (parityDrawFrame = 0) on its exit path.
            if (parityFilled)
                leg.parityDrawFrame = drawFrame;
            return true;
        }

        /// <summary>
        /// Constructs a fresh per-leg <c>LineType.Continuous</c>
        /// VectorLine sized to hold exactly this leg's points. One line
        /// per leg (see <see cref="LegPolyline.vectorLine"/>): a single
        /// shared line drawn once per leg via <c>drawStart</c>/<c>drawEnd</c>
        /// range slicing does NOT work, because <c>Draw3D()</c> zeroes
        /// every vertex outside the current window on each call. Uses
        /// <c>MapView.OrbitLinesMaterial</c> (the SOLID stock orbit-line
        /// material, NOT the dotted/dashed one) with the same 5f width and
        /// distance/direction fade, so each leg reads as one unbroken
        /// orbit-style line with no dashes, gaps, or interruptions (mirrors
        /// <c>OrbitRendererBase.MakeLine</c>). A per-line vertex colour via
        /// <see cref="VectorLine.SetColor(Color)"/> is set to the EXACT stock
        /// vessel orbit-line grey so the polyline matches the ghost's own
        /// orbit arcs; being a per-line colour it does not mutate the shared
        /// material (which would dim every stock orbit line).
        /// </summary>
        internal static VectorLine BuildLegVectorLine(
            string recordingId, int legIndex, int pointCount)
        {
            return BuildContinuousMapLine(
                "ParsekGhostTrajectoryPolyline-" + recordingId + "-leg" + legIndex, pointCount);
        }

        /// <summary>
        /// Shared builder for a stock-orbit-styled <c>LineType.Continuous</c> map line with
        /// <paramref name="pointCount"/> zero points (width 5f, <see cref="ApplyStockOrbitLineStyle"/>).
        /// Backs <see cref="BuildLegVectorLine"/>, <see cref="BuildForwardArcVectorLine"/>, and the Bug 1
        /// <see cref="RebuildLineForMode"/> rebuild (which preserves the prior line's name).
        /// </summary>
        internal static VectorLine BuildContinuousMapLine(string name, int pointCount)
        {
            if (pointCount <= 0) return null;
            var points = new List<Vector3>(pointCount);
            for (int i = 0; i < pointCount; i++)
                points.Add(Vector3.zero);
            var line = new VectorLine(name, points, 5f, LineType.Continuous);
            ApplyStockOrbitLineStyle(line);
            return line;
        }

        /// <summary>
        /// Bug 1 rebuild-on-flip: destroys <paramref name="existing"/> and recreates it (same name +
        /// <paramref name="pointCount"/>, stock orbit-line style) for the current map-line mode. Vectrosity's
        /// in-place <c>VectorObject3D</c>&lt;-&gt;<c>VectorObject2D</c> swap (toggling <c>Draw3D()</c>/
        /// <c>Draw()</c> on ONE line at the far-zoom 2D flip) leaves the 2D canvas Graphic non-rendering, so
        /// the line vanishes past the threshold; stock sidesteps this by rebuilding the line on every mode
        /// flip (<c>OrbitRendererBase</c> calls <c>MakeLine</c>), which this mirrors. The caller re-copies
        /// the points into the fresh line the same frame, so no geometry is lost.
        /// </summary>
        internal static VectorLine RebuildLineForMode(VectorLine existing, int pointCount)
        {
            string name = existing != null ? existing.name : "ParsekGhostMapLine";
            if (existing != null)
            {
                var old = existing;
                VectorLine.Destroy(ref old);
            }
            return BuildContinuousMapLine(name, pointCount);
        }

        /// <summary>
        /// Styles a fresh <c>LineType.Continuous</c> VectorLine to look EXACTLY like a stock vessel orbit
        /// line so a polyline leg / forward arc is indistinguishable from the ghost's own orbit arcs: the
        /// SOLID <c>MapView.OrbitLinesMaterial</c> (NOT the dotted/dashed material), the same 5f width and
        /// distance/direction fade (mirrors <c>OrbitRendererBase.MakeLine</c>), and the stock vessel
        /// orbit-line grey via a PER-LINE vertex colour (so the shared material is never dimmed). Shared by
        /// the leg lines (<see cref="BuildLegVectorLine"/>) and the forward-arc lines
        /// (<see cref="BuildForwardArcVectorLine"/>) so current + future read as one continuous line, the
        /// uniform style the plan requires. _FadeStrength / _FadeSign are global GameSettings values set on
        /// the SHARED material (idempotent: stock sets the same values every orbit line), so this does not
        /// disturb real orbit lines.
        /// </summary>
        private static void ApplyStockOrbitLineStyle(VectorLine line)
        {
            if (line == null) return;
            Material orbitMat = MapView.OrbitLinesMaterial;
            if (orbitMat != null)
            {
                line.texture = orbitMat.mainTexture;
                line.material = orbitMat;
                orbitMat.SetFloat("_FadeStrength", GameSettings.ORBIT_FADE_STRENGTH);
                orbitMat.SetFloat("_FadeSign",
                    GameSettings.ORBIT_FADE_DIRECTION_INV ? -1f : 1f);
            }
            line.continuousTexture = true;
            line.UpdateImmediate = true;
            // EXACT stock vessel orbit-line colour: KSP's OrbitRenderer seeds an unfocused vessel with
            // SetColor(new Color(0.71,0.71,0.71,1)) and draws the line at orbitColor = nodeColor * 0.5
            // (alpha preserved) with lineOpacity 1 (OrbitRenderer.GetOrbitColour / OrbitRendererBase),
            // i.e. the mid-grey below. Per-line vertex colour, so the shared OrbitLinesMaterial is untouched.
            Color stockNode = new Color(0.71f, 0.71f, 0.71f, 1f);
            Color stockOrbit = stockNode * 0.5f;
            stockOrbit.a = stockNode.a;
            line.SetColor(stockOrbit);
        }

        /// <summary>
        /// Pure draw-mode decision mirroring stock <c>OrbitRendererBase.DrawSpline</c>: a map / scaled
        /// orbit line is drawn with <c>VectorLine.Draw3D()</c> only when <c>MapView.Draw3DLines</c> is
        /// true, otherwise with the 2D <c>VectorLine.Draw()</c> screen-space path. Stock flips
        /// <c>MapView.Draw3DLines</c> to false (<c>MapView.UpdateMap</c>) when the map camera zooms out
        /// past <c>max3DlineDrawDist</c> (1500) or too many orbits are on screen
        /// (<c>MAP_MAX_ORBIT_BEFORE_FORCE2D</c> = 150), precisely because Vectrosity's <c>Draw3D</c>
        /// world-space vertex reconstruction (<c>cam3D.ScreenToWorldPoint</c> at each point's far camera
        /// distance) degrades at far zoom and drops the line. Parsek previously ALWAYS called
        /// <c>Draw3D()</c>, so its legs / arcs / bridges vanished past that same 1500 threshold while stock
        /// orbit lines (flipped to 2D) persisted (Bug 1: looped-mission trajectory lines vanishing when
        /// zoomed out). When MapView is absent (<paramref name="mapViewPresent"/> false, e.g. a transient
        /// DDOL tick during a scene switch) default to 3D - the prior behavior, and never an NPE. Kept
        /// <c>internal static</c> + parameterized for headless unit-testability (no <c>MapView</c> ECall).
        /// </summary>
        internal static bool ShouldDraw3DMapLine(bool mapViewPresent, bool mapViewDraw3DLines)
        {
            return !mapViewPresent || mapViewDraw3DLines;
        }

        /// <summary>
        /// Live wrapper over <see cref="ShouldDraw3DMapLine"/>: reads the runtime MapView state (the
        /// <c>MapView.Draw3DLines</c> read short-circuited behind the <c>MapView.fetch</c> null check) and
        /// returns whether map lines are in stock 3D (<c>Draw3D()</c>) mode this frame, false in the 2D
        /// (<c>Draw()</c>) mode stock flips to past the far-zoom threshold. Used by both
        /// <see cref="DrawMapLine"/> and the draw-site transform setup so they agree within a frame.
        /// </summary>
        internal static bool MapLineUses3D()
        {
            bool present = MapView.fetch != null;
            return ShouldDraw3DMapLine(present, present && MapView.Draw3DLines);
        }

        /// <summary>
        /// Applies a leg / arc / bridge line's per-frame transform for the CURRENT map-line mode, called
        /// right before <see cref="DrawMapLine"/>. The layer (stock orbit-line layer 31) is set in BOTH
        /// modes. The world-transform zeroing (<c>position = 0</c>, identity rotation, unit scale) is done
        /// ONLY in 3D (<c>Draw3D()</c>) mode: there the line is an unparented world mesh of ABSOLUTE
        /// scaled-space points, so its GameObject transform must be the identity or the mesh inherits a
        /// parent transform and drifts as the map camera pans. In 2D (<c>Draw()</c>) mode the line lives
        /// under KSP's Vectrosity ScreenSpaceCamera canvas (<c>vectorCam</c>, layer 31); forcing world
        /// <c>position = 0</c> every frame shoves the canvas child off the canvas plane so it never paints
        /// - the Bug 1 zoom-out vanish past the 2D-flip threshold. In 2D the canvas-managed RectTransform
        /// is left untouched (stock writes position only on recalculation, which is why stock 2D orbit
        /// lines render and Parsek's did not).
        /// </summary>
        internal static void PrepareMapLineTransform(VectorLine line, int targetLayer)
        {
            if (line == null || line.rectTransform == null) return;
            line.rectTransform.gameObject.layer = targetLayer;
            if (!MapLineUses3D()) return;
            var xform = line.rectTransform;
            xform.position = Vector3.zero;
            xform.rotation = Quaternion.identity;
            xform.localScale = Vector3.one;
        }

        /// <summary>
        /// Draws a leg / arc / bridge line in the SAME map-line mode stock orbit lines use this frame:
        /// <c>Draw3D()</c> when <see cref="ShouldDraw3DMapLine"/> (the live <c>MapView.Draw3DLines</c>),
        /// else the 2D <c>Draw()</c> path. This IS the Bug 1 fix - past the stock far-zoom 2D-flip
        /// threshold the 2D path renders the line through Vectrosity's <c>vectorCam</c> overlay on the same
        /// shared canvas + layer 31 stock orbit lines use, so Parsek lines stop vanishing on zoom-out. The
        /// <c>MapView.Draw3DLines</c> read is short-circuited behind the <c>MapView.fetch</c> null check so
        /// it never NPEs during a scene transition. Replaces the reverted provisional mesh-bounds override,
        /// which was a confirmed no-op: the real vanish lives INSIDE <c>Draw3D</c> (a screen-space path the
        /// AABB frustum cull never reached), and in 2D mode Vectrosity destroys the MeshFilter, so there is
        /// no mesh to stamp at all.
        /// </summary>
        internal static void DrawMapLine(VectorLine line)
        {
            if (line == null) return;
            if (MapLineUses3D())
                line.Draw3D();
            else
                line.Draw();
        }

        /// <summary>
        /// Constructs a fresh forward-arc <c>LineType.Continuous</c> VectorLine sized to hold exactly the
        /// arc's sample count (Step 3 C). One line PER forward segment (same Draw3D drawStart/drawEnd
        /// constraint as legs). Identical orbit-line style to the legs via
        /// <see cref="ApplyStockOrbitLineStyle"/> so the forward arc reads as one continuous line with the
        /// current stock arc and the legs.
        /// </summary>
        internal static VectorLine BuildForwardArcVectorLine(
            string recordingId, int arcIndex, int pointCount)
        {
            return BuildContinuousMapLine(
                "ParsekGhostForwardArc-" + recordingId + "-arc" + arcIndex, pointCount);
        }

        /// <summary>
        /// Copies a leg's scratch <c>Vector3[]</c> into the leg's own
        /// VectorLine's <c>points3</c> list starting at the given offset
        /// (0 for per-leg lines).
        /// </summary>
        internal static void CopyLegIntoVectorLine(
            VectorLine line, Vector3[] scratch, int startIdx)
        {
            if (line == null || scratch == null) return;
            var points3 = line.points3;
            if (points3 == null) return;
            for (int i = 0; i < scratch.Length; i++)
            {
                int dst = startIdx + i;
                if (dst < 0 || dst >= points3.Count) continue;
                points3[dst] = scratch[i];
            }
        }

        /// <summary>
        /// Scene-wide MonoBehaviour that performs the per-frame walk over
        /// <c>RecordingStore.CommittedRecordings</c>, refreshes the cache
        /// (hash-gated), and draws each leg's own VectorLine full-range.
        ///
        /// Single instance for the AppDomain lifetime; lives across scene
        /// transitions via <see cref="MonoBehaviour"/>+
        /// <c>DontDestroyOnLoad</c>, matching the
        /// <c>TestRunnerShortcut.cs:51-59</c> repo precedent. KSC scene is
        /// out of scope for v1 (§1.1); the LateUpdate scene gate skips any
        /// scene other than TRACKSTATION / FLIGHT.
        /// </summary>
        // Run this Driver's LateUpdate BEFORE stock components (OrbitRendererBase is
        // at default execution order 0). The Driver publishes drewNonOrbitalLegRecordings in
        // its LateUpdate and GhostOrbitLinePatch (on OrbitRendererBase.LateUpdate)
        // reads it; without a forced order the orbit patch ran first and read the
        // PREVIOUS frame's set, so at a burn's first frame it still showed the prior
        // orbit arc while the polyline drew the burn (the "RENDER OVERLAP" handoff
        // artifact). Running the Driver first makes the publish current when the
        // patch reads it. Safe w.r.t. loop units: cachedLoopUnits is computed in the
        // scene controllers' Update(), which always precedes every LateUpdate
        // regardless of execution order, so the Driver still reads current units.
        [DefaultExecutionOrder(-50)]
        [KSPAddon(KSPAddon.Startup.Instantly, true /* once */)]
        internal sealed class Driver : MonoBehaviour
        {
            private const string DriverTag = "GhostMap";
            private static Driver instance;

            internal static Driver Instance => instance;

            // Per-scene controller cache (MINOR-1): FindObjectOfType is an
            // expensive scene scan, so cache the resolved controller and
            // re-resolve only when it is null or when the scene changed.
            private GameScenes cachedControllerScene = (GameScenes)(-1);
            private ParsekTrackingStation cachedTsController;
            private ParsekFlight cachedFlightController;

            // Per-frame name->CelestialBody cache (MINOR-2): FlightGlobals.Bodies
            // is a linear scan; build a name->body map once and rebuild only
            // when the scene changes (bodies are stable within a scene).
            private readonly Dictionary<string, CelestialBody> bodyByName =
                new Dictionary<string, CelestialBody>(StringComparer.Ordinal);
            private GameScenes bodyMapScene = (GameScenes)(-1);

            // SEAM-RENDER OBSERVABILITY 3 (docs/dev/design-reaim-launch-hold-seam.md): logging-only latch
            // tracking the last secondary-cycle for which the boundary-overlap second-head ascent leg was
            // first drawn, per recording. Used SOLELY to emit the per-cycle "first-draw" line once (when the
            // additive secondary leg first enqueues this cycle). Does NOT gate or alter any draw - the leg is
            // enqueued by the existing per-leg logic; this only decides whether to LOG the first-draw line.
            private readonly Dictionary<string, long> boundarySecondaryFirstDrawCycle =
                new Dictionary<string, long>(StringComparer.Ordinal);

            // ----------------------------------------------------------------
            // Pan-stability draw split (FIX 1)
            //
            // The polyline mesh is baked by Vectrosity's Draw3D() against
            // PlanetariumCamera.Camera's CURRENT matrix. At [DefaultExecutionOrder(-50)]
            // the decide pass runs BEFORE the map camera commits its pan for the
            // frame, so a Draw3D() here lags the camera by one frame during an
            // active pan (the line wobbles, settling when the pan stops). To fix
            // that WITHOUT disturbing the ownership-publish-at--50 contract that
            // GhostOrbitLinePatch (order 0) depends on, the per-frame decide
            // (ownership publish + head-UT gate + body resolve) stays at -50 and
            // ONLY the point-recompute + Draw3D + deactivation sweep move to a
            // Camera.onPreCull pass filtered to the map camera (the proven repo
            // slot - ParsekFlight.OnCameraPreCull - with a hard "after every
            // LateUpdate" guarantee, so it sees the committed pan).
            //
            // The decide pass enqueues a PendingLegDraw per will-draw leg; the
            // onPreCull pass dequeues them and runs the real TryDrawLeg /
            // TryDrawOwnedLeg + the deactivation sweep. Both read only frame-stable
            // cached state (polylineCache, body transforms, ScaledSpace scale),
            // none camera-dependent, so they are safe in the later slot.
            // ----------------------------------------------------------------
            private struct PendingLegDraw
            {
                public string recordingId;   // polylineCache key
                public int legIndex;         // index into set.legs
                public CelestialBody body;   // resolved in the decide pass
                public Recording rec;        // for the conic anchor
                public bool ownedByTreatment;
                public uint ghostPid;        // for TryDrawOwnedLeg
                // Step 3 B' (forward additive pass): a leg enqueued as part of the FORWARD chain ahead of
                // the icon. The forward legs draw identically (same TryDrawLeg) but are EXCLUDED from the
                // anyDrawn / ownership accounting, so they NEVER flip drewNonOrbitalLegRecordings (the
                // CRITICAL Step 3 ownership prerequisite: forward draws must not suppress the current arc).
                public bool forward;
                // Playtest-7 past/future split: PAST run legs must anchor (a past body-fixed leg would
                // rotate-sweep into the inertial line behind the icon - the playtest-5 rule); FUTURE run
                // legs draw regardless (anchored when possible, body-fixed otherwise - the Duna landing
                // descent), connected to the inertial run by the seam bridges.
                public bool requireConicAnchor;
                // Phase 5b: evaluate the orbit<->landing G1 tangent seam after this leg draws (the
                // stitched descent member's seam-entry leg, owned draw, tracing on - the pure gate
                // CrossMemberSeamStitcher.ShouldEvaluateTangentSeamAtDraw). The tangents exist only at
                // the DRAW site (a RenderSegment carries no points), so the Tier-C
                // rigid-seam-tangent-discontinuity raise is wired here.
                public bool evalTangentSeam;
            }
            private readonly List<PendingLegDraw> pendingDraws = new List<PendingLegDraw>();
            private int pendingDrawsFrame = -1;

            // Step 3 C (forward additive pass): forward-ARC draws decided this frame, drawn in the same
            // post-pan onPreCull slot as the legs. Each entry is a (recordingId, arcIndex) into
            // forwardArcCache; the arc geometry is already sampled (cache-key-gated) so the draw pass just
            // reactivates + Draw3D's the cached line. Forward arcs never touch drewNonOrbitalLegRecordings.
            private struct PendingForwardArcDraw
            {
                public string recordingId;
                public int arcIndex;
                // SEAM BRIDGE (playtest 6): when this arc is a bridge's NEXT-side target, its draw
                // range starts at BridgeMergeSampleCount so the bridge REPLACES the arc's lead-in
                // (never a double line). 0 = draw from the first sample.
                public int drawStartIndex;
                // SEAM BRIDGE (playtest 7): when this arc is a bridge's PREV-side source, its draw
                // range ends at (len-1 - BridgeMergeSampleCount) so the bridge REPLACES the arc's
                // tail. -1 = draw to the last sample.
                public int drawEndIndex;
            }
            private readonly List<PendingForwardArcDraw> pendingForwardArcs =
                new List<PendingForwardArcDraw>();

            // SEAM BRIDGE pending draws (playtest 6, generalized playtest 7): decided in the -50 pass
            // alongside the run legs/arcs, drawn in the post-pan onPreCull slot AFTER the legs (whose
            // fresh scratchScaledSpace supplies each bridge's leg-side endpoint) and the arcs. One
            // entry per (drawn body-fixed leg, side) that passed the adjacency + angle + signed-gap
            // gates - the Duna descent legs carry a start-side AND an end-side bridge simultaneously.
            private struct PendingBridgeDraw
            {
                public string legRecordingId;
                public int legIndex;
                public bool atLegStart;      // true: prev-conic-end -> leg start; false: leg end -> next-conic-start
                public string arcRecordingId; // cached forward-arc source (null when on-demand sampled)
                public int arcIndex;          // cached forward-arc index (-1 when on-demand sampled)
                public string sampleKeyRecordingId; // on-demand source: owning recording id
                public OrbitSegment sampleSegment;  // on-demand source: the conic to sample
                // Index into pendingForwardArcs whose draw range gets clipped at the merge sample,
                // applied ONLY when this bridge actually DRAWS (review MINOR-2: a decide-time clip
                // with a failed bridge draw left a one-frame hole in the arc). -1 = no clip.
                public int clipArcPendingIndex;
                // SMALL-GAP CHORD (launch->escape seam render): when true, the bridge is a STRAIGHT chord
                // from the leg's drawn end to the conic's near endpoint (no merge-slice, no bulge, no conic
                // clip), drawn for a sub-5 deg gap the merge slice mutes (BridgeChordMinAngleRadians,5deg].
                public bool chord;
            }
            private readonly List<PendingBridgeDraw> pendingBridges = new List<PendingBridgeDraw>();
            private int pendingBridgeFrame = -1;

            // Drawn body-fixed legs of the chain being decided (bridge candidates): filled per chain
            // pass, consumed by DecideSeamBridges within the same call.
            private struct BridgeLegCandidate
            {
                public string recordingId;
                public Recording rec;
                public int legIndex;
                public double startUT;
                public double endUT;
                public string bodyName;
            }
            private readonly List<BridgeLegCandidate> bridgeLegScratch = new List<BridgeLegCandidate>();

            // Flat UT-span projection of bridgeLegScratch (body + start/end UT) for the pure
            // intervening-continuation-leg predicate, rebuilt once per DecideSeamBridges call. Reusable
            // per-Driver buffer (single-threaded LateUpdate decide pass).
            private readonly List<BridgeLegSpan> bridgeLegSpanScratch = new List<BridgeLegSpan>();

            // Reusable scratches for the bridge geometry (fixed size: merge count + 1): the arc-side
            // sample slice (lead-in forward / tail reversed) and the unwound output points.
            private readonly Vector3d[] bridgeArcSliceScratch = new Vector3d[BridgeMergeSampleCount + 1];
            private readonly Vector3d[] bridgePointScratch = new Vector3d[BridgeMergeSampleCount + 1];

            // Per-Driver reusable scratch buffer for the forward-arc index selection (forward-render review
            // finding): SelectForwardArcSegmentIndices runs once per leg-bearing recording per frame on the
            // multi-ghost path, so the fill-into-buffer overload clears + refills this single list instead of
            // allocating a fresh List<int> each call. Single-threaded per Driver instance (LateUpdate decide
            // pass), so one shared scratch is safe; consumed immediately within DecideForwardWindowForRecording.
            private readonly List<int> forwardArcIndexScratch = new List<int>();

            // Live conic gap-fill sampler (playtest-12 straight-chord fix): cached delegate instance so
            // the per-frame RefreshForRecording calls never allocate a fresh delegate. Bound in Awake.
            private ConicGapSampler liveGapSampler;

            // Chain-aware run scratch (playtest-4 chain-boundary fix): one render run spans every
            // member of a recording CHAIN, but the per-recording walk only reaches the VISIBLE member
            // (looped-chain siblings are renderHidden), so the run pass resolves the sibling members'
            // effective segments + leg caches itself. Per-Driver reusable buffers, consumed
            // synchronously within DecideForwardWindowForRecording (single-threaded LateUpdate decide
            // pass, one chain at a time), so shared scratch is safe.
            private readonly List<(int index, Recording rec)> chainRunMemberScratch =
                new List<(int index, Recording rec)>();
            private readonly List<List<OrbitSegment>> chainRunMemberSegsScratch =
                new List<List<OrbitSegment>>();
            private readonly List<long> chainRunMemberReaimScratch = new List<long>();
            private readonly List<OrbitSegment> chainRunConcatScratch = new List<OrbitSegment>();

            // Chains whose run was already decided this frame: the run is computed ONCE per chain per
            // frame, by the first non-hidden member the walk reaches (for a looped chain that is
            // exactly the active member). Later visible members of the same chain skip, which also
            // prevents double-enqueueing the same run legs/arcs for historical non-loop chains where
            // every member passes the renderHidden gate. Cleared at the top of every LateUpdate.
            private readonly HashSet<string> chainRunProcessedThisFrame =
                new HashSet<string>(StringComparer.Ordinal);

            // De-dupe: onPreCull can fire more than once per frame (multiple
            // cameras), but the map camera filter + this frame guard keep the
            // draw pass to exactly one run per frame.
            private int precullDrawnFrame = -1;
            // The target layer the decide pass resolved; carried so the onPreCull
            // draw pass uses the identical value without re-deriving it.
            private int pendingTargetLayer = 31;
            // Diagnostic: legs the most recent onPreCull deactivation sweep hid.
            // Read back by the next LateUpdate summary (the sweep no longer runs
            // in LateUpdate).
            private int lastSweepDeactivatedCount;

            void Awake()
            {
                if (instance != null)
                {
                    Destroy(gameObject);
                    return;
                }
                instance = this;
                DontDestroyOnLoad(gameObject);
                // Cached delegate so per-frame RefreshForRecording calls never allocate (playtest 12).
                liveGapSampler = SampleConicBodyFixedAtUT;
                GameEvents.onGameStateLoad.Add(OnGameStateLoad);
                GameEvents.onLevelWasLoaded.Add(HandleLevelWasLoaded);
                // Pan-stability (FIX 1): the actual point recompute + Draw3D run in this hook AFTER the
                // map camera commits its pan, so the mesh no longer lags the camera by one frame. The
                // decide pass (ownership publish + head-UT gate) stays at -50 in LateUpdate.
                Camera.onPreCull += OnMapCameraPreCull;
                ParsekLog.Verbose(DriverTag,
                    "GhostTrajectoryPolylineRenderer.Driver awake (DDOL singleton)");
            }

            void OnDestroy()
            {
                if (instance == this)
                {
                    instance = null;
                    GameEvents.onGameStateLoad.Remove(OnGameStateLoad);
                    GameEvents.onLevelWasLoaded.Remove(HandleLevelWasLoaded);
                    Camera.onPreCull -= OnMapCameraPreCull;
                    ParsekLog.Verbose(DriverTag,
                        "GhostTrajectoryPolylineRenderer.Driver destroyed");
                }
            }

            /// <summary>
            /// Drops the cached per-scene controller + body-name map on every
            /// scene load so the next LateUpdate re-resolves them once for the
            /// new scene (MINOR-1 / MINOR-2). The DDOL Driver outlives scene
            /// transitions, so a stale controller from the previous scene must
            /// not be reused.
            ///
            /// Named HandleLevelWasLoaded (not OnLevelWasLoaded) to avoid colliding
            /// with Unity's deprecated magic message of that name: Unity scans
            /// MonoBehaviours for a method called OnLevelWasLoaded and, finding our
            /// GameScenes-typed handler instead of the magic int signature, logs a
            /// spurious "[ERR] Script error: OnLevelWasLoaded" on every scene load.
            /// The real subscription is the KSP GameEvent GameEvents.onLevelWasLoaded.
            /// </summary>
            private void HandleLevelWasLoaded(GameScenes scene)
            {
                cachedControllerScene = (GameScenes)(-1);
                cachedTsController = null;
                cachedFlightController = null;
                bodyMapScene = (GameScenes)(-1);
                bodyByName.Clear();

                // Flush the marker hold cache on every scene switch: a held on-line WORLD position from
                // the previous scene must never glue a marker in the new scene. The per-frame deactivation
                // sweep does not touch this cache (it is consumed in OnGUI, after the sweep), so flush it
                // here on the same lifecycle as the controller / body-map drops. The pending-draw handoff
                // is reset too so a half-decided frame from the prior scene cannot draw into the new one.
                lastGoodOnLine.Clear();
                pendingDraws.Clear();
                pendingForwardArcs.Clear();
                pendingDrawsFrame = -1;
                precullDrawnFrame = -1;
                pendingBridges.Clear();
                pendingBridgeFrame = -1;
            }

            /// <summary>
            /// Cross-save guard (§1.4 / §6 MAJOR-3). <c>ParsekScenario.OnLoad</c>
            /// calls <c>RecordingStore.ClearCommitted()</c>; a same-RecordingId
            /// in the next-loaded save would otherwise hit the stale cache.
            /// The XOR-of-UTs content hash is byte-stable across a load
            /// round-trip, so a content-hash gate alone cannot flush. Drop
            /// every cached entry + destroy the underlying Vectrosity
            /// GameObjects here so the next per-frame walk rebuilds from
            /// scratch.
            /// </summary>
            private void OnGameStateLoad(ConfigNode node)
            {
                ParsekLog.Verbose(DriverTag,
                    "Polyline driver: onGameStateLoad -> Clear() (cross-save flush)");
                Clear();
                // Drop any half-decided draw handoff so the onPreCull pass cannot draw a freed leg into
                // the next-loaded save before the next LateUpdate re-decides.
                pendingDraws.Clear();
                pendingForwardArcs.Clear();
                pendingDrawsFrame = -1;
                precullDrawnFrame = -1;
                pendingBridges.Clear();
                pendingBridgeFrame = -1;
            }

            void LateUpdate()
            {
                // Publish-set for GhostMapPresence orbit suppression: clear FIRST,
                // before any early return, so it reflects only recordings whose
                // non-orbital leg actually draws this frame (empty when the
                // polyline is off / not in map view / wrong scene). 8b.2 / 8e S3b:
                // the actual-draw set repopulates only on an actual draw this frame,
                // so a stale ownership can never leak a hidden proto into the next phase.
                drewNonOrbitalLegRecordings.Clear();

                // Phase 8e S0 (PURELY ADDITIVE diagnostics): clear this frame's coverage-closure sets on
                // the SAME pre-early-return lifecycle as the ownership sets, so they reflect only the
                // recordings this frame's walk draws (empty when the polyline is off / not in map view /
                // wrong scene). Only touched in tracing mode (the populate site below is IsEnabled-gated),
                // but the clear is unconditional + cheap so a tracing toggle mid-session never leaves a
                // stale drawn/coverage entry behind for the probe to misread.
                GhostMapPresence.ClearFrameCoverageSets();

                // Pan-stability (FIX 1): drop last frame's pending-draw handoff before any early return,
                // so a frame that bails (wrong scene / not in map view / no controller) leaves nothing for
                // the onPreCull pass to draw. The pending list is repopulated below only for legs that
                // WILL draw this frame; the onPreCull pass keys on pendingDrawsFrame == Time.frameCount.
                pendingDraws.Clear();
                pendingForwardArcs.Clear();
                pendingDrawsFrame = -1;
                pendingBridges.Clear();
                pendingBridgeFrame = -1;
                // Chain-run dedupe set is per-frame: each chain's run is decided once per LateUpdate.
                chainRunProcessedThisFrame.Clear();

                // Scene gate: v1 ships TRACKSTATION + FLIGHT only (§1.1).
                var scene = HighLogic.LoadedScene;
                if (scene != GameScenes.TRACKSTATION && scene != GameScenes.FLIGHT)
                    return;

                if (!MapView.MapIsEnabled) return;

                // Pull the per-frame filter inputs ONCE, outside the loop.
                var suppressed = GhostMapPresence.CachedTrackingStationSuppressedIds;
                // Layer 31 ALWAYS, matching stock map orbit lines. KSP's
                // OrbitRendererBase keeps layerMask=31 (never reassigned) and
                // puts every orbit VectorLine on it (decompiled
                // OrbitRendererBase: `protected int layerMask = 31;` +
                // `l.rectTransform.gameObject.layer = layerMask;`), regardless of
                // MapView.Draw3DLines. The earlier `Draw3DLines ? 24 : 31` put the
                // polyline on layer 24 (the map-NODE/icon layer, used by
                // MapNode.Create(..., 24, ...)) whenever 3D lines were on: the
                // flight map camera happens to render layer 24, but the Tracking
                // Station map camera does not, so the polyline drew (drawn=1 in the
                // log) yet was invisible in the TS. Since the polyline always uses
                // Draw3D(), it belongs on the same 3D orbit-line layer stock uses in
                // both scenes.
                const int targetLayer = 31;
                double currentUT = Planetarium.GetUniversalTime();
                int drawFrame = Time.frameCount;

                // Resolve cachedLoopUnits per-scene. The underlying field
                // is a private per-scene instance member on two
                // different MonoBehaviours; the DDOL singleton Driver has
                // no direct handle, so it looks up the matching scene
                // controller and reads through the internal
                // CurrentCachedLoopUnits accessor. The controller is cached
                // and only re-resolved when null or on a scene change
                // (MINOR-1). Transitional frames before the controller's
                // Awake DEFER the draw rather than substituting
                // LoopUnitSet.Empty (Empty would defeat the renderHidden
                // filter).
                GhostPlaybackLogic.LoopUnitSet loopUnits;
                if (scene == GameScenes.TRACKSTATION)
                {
                    var tsCtl = ResolveTrackingStationController(scene);
                    if (tsCtl == null)
                    {
                        ParsekLog.VerboseRateLimited(DriverTag,
                            "polyline-defer-ts-controller",
                            "Deferring polyline draw: ParsekTrackingStation not yet awake.",
                            5.0);
                        return;
                    }
                    loopUnits = tsCtl.CurrentCachedLoopUnits;
                }
                else // FLIGHT
                {
                    var flCtl = ResolveFlightController(scene);
                    if (flCtl == null)
                    {
                        ParsekLog.VerboseRateLimited(DriverTag,
                            "polyline-defer-flight-controller",
                            "Deferring polyline draw: ParsekFlight not yet awake.",
                            5.0);
                        return;
                    }
                    loopUnits = flCtl.CurrentCachedLoopUnits;
                }

                // FIX #27: per-body surface provider for the cover exclusion.
                // Built once per frame from the per-scene body map (so the pure
                // builder never calls FlightGlobals). The map is rebuilt lazily
                // per scene inside ResolveBodyByName; ensure it is populated here
                // so RefreshForRecording can resolve below-surface segments.
                EnsureBodyMap(scene);
                BodySurfaceProvider surface = ResolveBodySurface;

                // [ERS-exempt] Driver walks RecordingStore.CommittedRecordings
                // directly: atmospheric-only recordings are absent from
                // the ghost-bearing / pending-create iterators that
                // GhostMapPresence and ParsekPlaybackPolicy use, so the
                // polyline must reach the raw committed list.
                //
                // ---- Phase 5b FENCE (migration plan section 7, 5b): the walk is RETAINED ----
                // The plan's original "delete the autonomous Driver ownership walk" was re-scoped at 5b
                // after mapping the populations end-to-end: this walk is the SINGLE draw host, and it is
                // the only renderer for populations the spine does NOT enumerate:
                //  (1) proto-less pid-0 recordings (atmospheric-only ascents; scene.GhostPids never
                //      reports them, so no intent is ever stamped),
                //  (2) Driver-direct "bridge" legs the Director classifies StockConic (the re-aim bridge
                //      legs on the conic - the 8b.2 actual-draw decoupling),
                //  (3) the boundary-overlap SECONDARY head legs (deliberately non-owned),
                //  (4) the additive FORWARD legs / forward arcs / seam bridges (predicted-path surfaces),
                // AND the dispatch host for the OWNED TracedPathTreatment.TryDrawOwnedLeg draw itself,
                // AND the sole feeder of drewNonOrbitalLegRecordings (the 8e S3b SOLE ownership source -
                // KEPT). What 5b DID delete here: the walk's direct deorbit-clock reads (now consumed via
                // the CrossMemberSeamStitcher absorb APIs; source-gated) - and it WIRED the Tier-C
                // rigid-seam-tangent evaluation at the owned descent draw. Re-examine this fence whenever
                // the spine learns a population above.
                var committed = RecordingStore.CommittedRecordings;
                int frameDrawn = 0;
                int frameSkippedSuppressed = 0;
                int frameSkippedHidden = 0;
                int frameSkippedStatic = 0;
                int frameSkippedNoLegs = 0;
                int frameSkippedNoBody = 0;
                int frameLegsHeadUtGated = 0;
                // Forward additive pass (Step 3) batch counters: legs + arcs enqueued ahead of the icon
                // this frame, and recordings whose forward pass was skipped because the Director held the
                // ghost in a gap (re-aim trim / interior FlexibleSoi gap).
                int frameForwardLegs = 0;
                int frameForwardArcs = 0;
                int frameForwardSkippedGapHold = 0;
                for (int recordingIndex = 0; recordingIndex < committed.Count; recordingIndex++)
                {
                    var rec = committed[recordingIndex];
                    if (rec == null) continue;
                    if (suppressed != null && suppressed.Contains(rec.RecordingId))
                    {
                        frameSkippedSuppressed++;
                        continue;
                    }

                    // RECORDING-level static filter: debris / no-trajectory /
                    // suppression. This is intentionally NOT the
                    // ClassifyAtmosphericMarkerSkip per-head-UT recording gate
                    // (OrbitSegmentActive / NativeIconActive), which would blink
                    // the WHOLE recording's polyline out the moment a ghost
                    // ProtoVessel or orbit line exists. Instead the polyline is
                    // gated per-LEG on the head UT (in the leg loop below), so
                    // it follows the ghost through each non-orbital phase and
                    // hands off cleanly to the orbit arc during orbital phases.
                    var staticSkip = ClassifyPolylineStaticSkip(rec, suppressed);
                    if (staticSkip != PolylineStaticSkipReason.None)
                    {
                        frameSkippedStatic++;
                        continue;
                    }

                    // renderHidden gate (loop-unit visibility): hide the
                    // polyline for a loop unit the marker pass is hiding too.
                    // The returned headUT is the ghost's CURRENT playback
                    // position in this recording's own timeline (loopUT for a
                    // loop member, liveUT otherwise); the per-leg gate below
                    // uses it so the line follows the ghost instead of painting
                    // the whole recorded path at once.
                    // Dual-clock head (docs/dev/plan-launch-boundary-overlap.md 4.1): the PRIMARY head UT (the
                    // continuing instance N, byte-identical to before) PLUS the optional boundary-overlap SECONDARY
                    // head UT (the early-launching instance N+1's in-SOI ascent during the borrow window of a
                    // zero-slack re-aim launch loop). hasSecondaryHead is false for every non-launch-hold member and
                    // every already-aligned loop, so the second-head pass below is dead for them (byte-identical).
                    double headUT = GhostPlaybackLogic.ResolveTrackingStationSampleFrame(
                        recordingIndex,
                        rec.StartUT,
                        rec.EndUT,
                        currentUT,
                        loopUnits,
                        out bool renderHidden,
                        out bool hasSecondaryHead,
                        out double secondaryHeadUT,
                        out long secondaryHeadCycle);

                    // OBSERVABILITY (mapRenderTracing): publish the descent render WINDOW (Loiter = the loiter
                    // orbit, Descent = the descent-to-landing) for this frame so the end-of-frame MapRenderProbe
                    // dumps EVERY rendered map object (orbit lines + polyline legs) during exactly those two
                    // windows - catching extra / stale trajectory lines the per-pid change-based probe misses.
                    // Computed before the renderHidden skip so it fires off whichever member renders this phase
                    // (the transfer member during Loiter, the descent member during Descent). No-op when tracing
                    // is off or this is not a descent-trigger unit member.
                    if (MapRenderTrace.IsEnabled
                        && GhostPlaybackLogic.TryGetDescentUnitRenderPhase(
                            loopUnits, recordingIndex, currentUT, rec.StartUT, rec.EndUT,
                            out Parsek.Reaim.DescentTrigger.DescentHeadPhase descentRenderPhase)
                        && (descentRenderPhase == Parsek.Reaim.DescentTrigger.DescentHeadPhase.Loiter
                            || descentRenderPhase == Parsek.Reaim.DescentTrigger.DescentHeadPhase.Descent))
                    {
                        MapRenderTrace.NoteDescentRenderWindow(
                            Time.frameCount, descentRenderPhase.ToString(), rec.RecordingId);
                    }
                    // PRIMARY-HIDDEN-BUT-SECONDARY-LIVE (launch->escape seam render fix): the primary head can be
                    // hidden (its loopUT is OUTSIDE this member's window - the continuing through-line instance N is
                    // months downstream near the destination, an orbital phase with no in-window non-orbital leg)
                    // while a boundary-overlap SECONDARY is live in this member's own window (the early-launching
                    // instance N+1's in-SOI ascent during the borrow window of a zero-slack re-aim launch loop). The
                    // OLD gate skipped the WHOLE recording on renderHidden, which dropped the launch recording before
                    // the second-head pass below could ever run - the secondary's forward ascent polyline + forward
                    // escape conic never drew. The secondary is NOT keyed on the primary's window: it has its own
                    // in-window leg (its proto-vessel icon + escape conic already render via the SEPARATE
                    // RunOverlapPerInstanceSweep map-presence walk, which has no renderHidden gate). So skip the whole
                    // recording ONLY when the primary is hidden AND there is no live secondary either. When the
                    // primary is hidden but the secondary is live, fall through with primaryRenders=false: the primary
                    // leg loop + primary forward pass are gated off (the primary genuinely has nothing in-window) and
                    // only the secondary second-head + secondary forward pass run. Inert for every non-launch-hold
                    // member / aligned loop (hasSecondaryHead is always false there, so this is the old skip exactly).
                    DecidePolylineWalkInclusion(
                        primaryRenders: !renderHidden, hasSecondary: hasSecondaryHead,
                        out bool skipRecording, out bool primaryRenders, out _);
                    if (skipRecording)
                    {
                        frameSkippedHidden++;
                        continue;
                    }

                    RefreshForRecording(rec, surface, liveGapSampler);
                    if (!polylineCache.TryGetValue(rec.RecordingId, out var set))
                    {
                        frameSkippedNoLegs++;
                        continue;
                    }
                    if (set.legs == null || set.legs.Length == 0)
                    {
                        frameSkippedNoLegs++;
                        continue;
                    }

                    // Phase 8b.1: resolve this recording's live ghost map pid ONCE (committed-list
                    // index -> ghost vessel pid; 0 when the recording has no proto-vessel ghost, e.g. an
                    // atmospheric-only recording). The TracedPath treatment ownership decision below is
                    // pid-keyed, the SAME freshness contract the icon-drive / orbit-line patches read to
                    // suppress the stock proto. Resolving here, outside the leg loop, keeps the per-leg
                    // routing cheap. pid 0 (no ghost) is never stamped by the shadow, so those recordings
                    // always take the Driver-direct path.
                    //
                    // Phase 5b: the owned-draw routing reads the single intent-sourced selector
                    // (IsTracedPathOwnedThisFrame == the spine's GhostRenderIntent stamp; the legacy
                    // side-channel was deleted with the cutover flag). A stamped ghost's leg draws via the
                    // OWNED TracedPathTreatment path and the Driver-direct draw stands down for it; the
                    // proto/marker consumers read the SAME selector, so no double-draw, no gap.
                    uint ghostPid = GhostMapPresence.GetGhostVesselPidForRecording(recordingIndex);
                    bool directorOwnsTracedPath =
                        Parsek.MapRender.ShadowRenderDriver.IsTracedPathOwnedThisFrame(ghostPid, drawFrame);

                    bool anyDrawn = false;
                    // Disjoint-leg guard (plan 4.2): the leg index the PRIMARY head landed on this frame (-1 when
                    // none). The boundary-overlap second-head pass below skips this leg so the SAME leg is never
                    // enqueued twice in one frame (it would be a no-op redraw anyway, but the guard keeps the
                    // secondary's head strictly the disjoint in-SOI leg the primary - far downstream - is not on).
                    int primaryDrawnLegIndex = -1;
                    // PRIMARY leg loop runs only when the primary head is in-window (primaryRenders). When the
                    // primary is hidden but a boundary-overlap secondary is live, this is skipped (the primary has
                    // no in-window non-orbital leg - it is the downstream through-line at an orbital phase) and only
                    // the secondary second-head + secondary forward pass below run. primaryRenders is always true for
                    // every non-launch-hold member / aligned loop (renderHidden false there), so this is unchanged.
                    if (primaryRenders)
                    {
                    // I1 (re-aim descent "renders disconnected from the loiter"): for the DESTINATION transfer
                    // member (TransferMemberIndex) of a descent-trigger unit, the deorbit/approach legs that lead
                    // DOWN TO the seam live INSIDE this member (not in the descent set) and would otherwise gate
                    // off entirely - the loiter loopUT sits ~|captureShift| below their recorded UTs, so
                    // ShouldDrawLegAtHeadUT(..., headUT) is false every frame, and during the descent this member
                    // is HIDDEN by the handoff. Resolve a re-anchored deorbit head ONCE (Loiter-phase only); the
                    // per-leg gate below substitutes it for the deorbit-tail legs so they sweep down to the seam
                    // and join the descent member's first head at the trigger. Gated on the EXACT transfer member
                    // (not "every non-descent member"): the owner and every ride-along in a different/unshifted
                    // frame (e.g. a launch-body-orbit probe) have no shifted deorbit tail and must not draw one.
                    // Byte-identical-off for them, for non-re-aim units (hasDeorbitHead false), and for every phase
                    // other than Loiter on the transfer member.
                    //
                    // Phase 5b: the walk consumes this clock EXCLUSIVELY through the Phase-6
                    // CrossMemberSeamStitcher absorb APIs (TryResolveTransferDeorbitTailHead below +
                    // ResolveDeorbitTailLegHead in the leg gate) - the stitcher owns the deorbit clock;
                    // the walk's direct DescentTrigger / span-clock deorbit reads were deleted (a source
                    // gate forbids them in this file). RETAINED (not deleted) because the spine renders
                    // the promoted DescentPhase only from the trigger onward; the LOITER-phase deorbit-
                    // tail sweep on the transfer member has no spine equivalent and would regress on
                    // every looped landing mission if this block died.
                    bool hasDeorbitHead = false;
                    double deorbitHead = double.NaN;
                    double deorbitSeamUT = double.NaN;
                    double deorbitConicEndUT = double.NaN;
                    if (loopUnits != null
                        && loopUnits.TryGetUnitForMember(recordingIndex, out GhostPlaybackLogic.LoopUnit dtUnit)
                        && dtUnit.HasDescentTrigger
                        && recordingIndex == dtUnit.TransferMemberIndex)
                    {
                        hasDeorbitHead = Parsek.MapRender.CrossMemberSeamStitcher.TryResolveTransferDeorbitTailHead(
                            dtUnit, recordingIndex, currentUT, rec.StartUT, rec.EndUT,
                            out deorbitHead, out deorbitConicEndUT, out deorbitSeamUT);
                    }

                    for (int li = 0; li < set.legs.Length; li++)
                    {
                        var leg = set.legs[li];

                        // Head-UT gate: draw a leg only while the ghost's
                        // current playback position (headUT) is within the leg's
                        // recorded [startUT, endUT] span. Outside it (an orbital
                        // phase, or another leg's window) the leg is skipped and
                        // the deactivation sweep below hides it, so the line
                        // tracks the moving ghost and a multi-leg recording shows
                        // only the single leg it is currently flying instead of
                        // every leg at once.
                        //
                        // I1: a deorbit-tail leg (the contiguous post-shifted-conic destination tail, UT window
                        // conicEnd < legEnd <= seam+eps) on the transfer member gates on the re-anchored
                        // deorbitHead instead of the loop head; every other leg keeps headUT (byte-identical).
                        // Phase 5b: routed through the stitcher's per-leg absorb (it owns the clock).
                        bool deorbitTailLeg = hasDeorbitHead
                            && !double.IsNaN(deorbitConicEndUT) && !double.IsNaN(deorbitSeamUT)
                            && leg.endUT > deorbitConicEndUT;
                        double legHeadUT = Parsek.MapRender.CrossMemberSeamStitcher.ResolveDeorbitTailLegHead(
                            leg.endUT, deorbitSeamUT, 1.0, headUT, deorbitHead, deorbitTailLeg);
                        if (!ShouldDrawLegAtHeadUT(leg.startUT, leg.endUT, legHeadUT))
                        {
                            frameLegsHeadUtGated++;
                            continue;
                        }

                        CelestialBody body = ResolveBodyByName(scene, leg.bodyName);
                        if (body == null)
                        {
                            ParsekLog.VerboseRateLimited(DriverTag,
                                "polyline-body-missing-" + leg.bodyName,
                                "Polyline body missing for leg, skipping: " + leg.bodyName,
                                5.0);
                            frameSkippedNoBody++;
                            continue;
                        }

                        // Phase 8b routing decision: when the Director owns this ghost's CURRENT
                        // leg as a TracedPath (directorOwnsTracedPath, the pid-keyed
                        // IsTracedPathOwnedThisFrame), the OWNED TracedPathTreatment path is the structural
                        // owner of the leg; otherwise the Driver-direct path draws (the fenced 5b
                        // populations: no fresh TracedPath intent, pid-0 proto-less, StockConic bridges).
                        bool ownedByTreatment = ShouldDrawLegOwnedByTreatment(directorOwnsTracedPath);

                        // Pan-stability split (FIX 1): this decide pass runs at -50, BEFORE the map camera
                        // commits its pan, so it does NOT call Draw3D here (that would lag the camera by a
                        // frame). Instead it (a) decides will-draw via the pure WillLegDraw predicate - the
                        // exact mirror of TryDrawLeg's non-degenerate early returns (body resolved + m>=2;
                        // the body-null skip already continued above), (b) publishes ownership NOW on the
                        // will-draw decision so the order-0 GhostOrbitLinePatch still sees "polyline owns
                        // this phase" this frame, and (c) enqueues a PendingLegDraw the onPreCull pass
                        // dequeues to run the actual point recompute + Draw3D AFTER the pan commits. The
                        // VectorLine inflate + the set.legs[li]=leg write-back move with the draw into the
                        // onPreCull pass (the struct is already in the array by reference; the decide pass
                        // does not mutate it). Will-draw == actual-draw for every real leg, so there is no
                        // decision-without-draw gap (see WillLegDraw + the single-biggest-risk note).
                        bool willDraw = WillLegDraw(leg.PointCount, body != null);
                        if (!willDraw)
                            continue;

                        pendingDraws.Add(new PendingLegDraw
                        {
                            recordingId = rec.RecordingId,
                            legIndex = li,
                            body = body,
                            rec = rec,
                            ownedByTreatment = ownedByTreatment,
                            ghostPid = ghostPid,
                            // Phase 5b tangent-seam wiring: evaluate the orbit<->landing G1 seam after
                            // the draw for the stitched descent member's seam-entry leg (owned draw,
                            // tracing on). Pure gate; false for every other population.
                            evalTangentSeam =
                                Parsek.MapRender.CrossMemberSeamStitcher.ShouldEvaluateTangentSeamAtDraw(
                                    MapRenderTrace.IsEnabled, ownedByTreatment,
                                    IsDescentTriggerMember(recordingIndex, loopUnits), li == 0)
                        });
                        anyDrawn = true;
                        primaryDrawnLegIndex = li;
                    }
                    }

                    // BOUNDARY-OVERLAP second head (docs/dev/plan-launch-boundary-overlap.md 4.1/4.2): the
                    // early-launching instance N+1's in-SOI ascent leg, drawn ADDITIVELY beside the primary leg
                    // during the borrow window. The primary head (instance N, far downstream / near the destination)
                    // and the secondary head (instance N+1, in-SOI ascent) select DISJOINT legs because they are
                    // months apart in recorded-span phase, so the two heads draw two different legs at two different
                    // places. The secondary's leg carries the SECONDARY ghost pid for symmetry with the primary's
                    // owned-leg path, but the leg is NON-owned (ownedByTreatment=false below), so the pid is INERT
                    // here - only TryDrawOwnedLeg consumes PendingLegDraw.ghostPid; TryDrawLeg ignores it. The
                    // secondary's own proto orbit line is hidden by the map-presence path (its overlap-instance
                    // ProtoVessel), NOT by this leg's pid. The leg is EXCLUDED from anyDrawn /
                    // drewNonOrbitalLegRecordings (the primary owns the ownership publish). Same-leg defensive
                    // guard: if both heads land in the same
                    // leg (impossible for an interplanetary transfer - the heliocentric tof is months), draw only the
                    // primary's leg. Inert (hasSecondaryHead false) for every non-launch-hold member / aligned loop.
                    // Resolved once for BOTH the secondary second-head leg pass and the secondary forward pass
                    // below (0 when no overlap-instance ghost exists this frame, which only happens before the
                    // map-presence sweep creates it - the leg still draws, keyed pid 0, no proto to hide).
                    uint secondaryGhostPid = hasSecondaryHead
                        ? GhostMapPresence.GetNewestOverlapInstancePidForRecording(recordingIndex)
                        : 0u;
                    if (hasSecondaryHead)
                    {
                        for (int li = 0; li < set.legs.Length; li++)
                        {
                            if (li == primaryDrawnLegIndex)
                                continue; // same-leg defensive guard: never enqueue the primary's leg twice
                            var secLeg = set.legs[li];
                            if (!ShouldDrawLegAtHeadUT(secLeg.startUT, secLeg.endUT, secondaryHeadUT))
                                continue;
                            CelestialBody secBody = ResolveBodyByName(scene, secLeg.bodyName);
                            if (secBody == null)
                            {
                                frameSkippedNoBody++;
                                continue;
                            }
                            if (!WillLegDraw(secLeg.PointCount, true))
                                continue;
                            // Additive, NON-ownership leg (mirrors the forward-additive mechanism): the
                            // boundary-overlap secondary leg is NOT owned-by-treatment (it is the raw in-SOI ascent
                            // leg the Driver draws directly) and does NOT publish ownership. It carries the secondary
                            // ghost pid for symmetry, but TryDrawLeg ignores ghostPid on this NON-owned path (only
                            // TryDrawOwnedLeg consumes it); the secondary's proto orbit line is hidden by the
                            // map-presence path, not by this field.
                            pendingDraws.Add(new PendingLegDraw
                            {
                                recordingId = rec.RecordingId,
                                legIndex = li,
                                body = secBody,
                                rec = rec,
                                ownedByTreatment = false,
                                ghostPid = secondaryGhostPid,
                                forward = false,
                                requireConicAnchor = false
                            });
                            ParsekLog.VerboseRateLimited(DriverTag,
                                "polyline-boundary-secondary." + rec.RecordingId,
                                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                    "Polyline boundary-overlap second head: rec={0} secondaryHeadUT={1:F1} secondaryCycle={2} " +
                                    "drewLeg={3} primaryLeg={4} secondaryPid={5}",
                                    rec.RecordingId, secondaryHeadUT, secondaryHeadCycle, li, primaryDrawnLegIndex, secondaryGhostPid),
                                2.0);

                            // SEAM-RENDER OBSERVABILITY 3 (docs/dev/design-reaim-launch-hold-seam.md): the
                            // ascent LINE's first-draw this cycle. Shows whether the secondary's body-fixed
                            // ascent polyline appears AT the clock launch (observability 1's currentUT) even
                            // while the icon/conic map-presence (observability 2) lags behind a pre-Segment gap.
                            // Emitted once per (recording, secondaryCycle) via the logging-only latch (the leg
                            // was already enqueued above; this only decides whether to LOG the first draw).
                            if (!boundarySecondaryFirstDrawCycle.TryGetValue(rec.RecordingId, out long loggedCycle)
                                || loggedCycle != secondaryHeadCycle)
                            {
                                boundarySecondaryFirstDrawCycle[rec.RecordingId] = secondaryHeadCycle;
                                ParsekLog.VerboseRateLimited(DriverTag,
                                    "boundary-overlap-secondary-polyline-first-draw." + rec.RecordingId,
                                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                        "boundary-overlap secondary polyline first-draw: currentUT={0:R} headUT={1:R} " +
                                        "secondaryLoopUT={2:R} rec={3} secondaryCycle={4} leg={5}",
                                        currentUT, secondaryHeadUT, secondaryHeadUT, rec.RecordingId, secondaryHeadCycle, li),
                                    2.0);
                            }
                        }
                    }

                    // Diagnostic (multi-leg / re-aim recordings): the head's position vs the leg windows,
                    // so a non-orbital phase that should draw a polyline but does not (head in a gap
                    // between legs, head stuck/frozen, or head past the last leg) is visible. Logs which
                    // leg (if any) contains the head and the first/last leg spans. Rate-limited per rec.
                    if (set.legs.Length > 1)
                    {
                        int activeLeg = -1;
                        for (int li = 0; li < set.legs.Length; li++)
                        {
                            if (ShouldDrawLegAtHeadUT(set.legs[li].startUT, set.legs[li].endUT, headUT))
                            {
                                activeLeg = li;
                                break;
                            }
                        }

                        // LOGGING GAP FILL: the DRAWN leg's span, length, body, and its body-relative
                        // WORLD longitude (where the polyline actually renders). A long isolated segment
                        // (e.g. the escape-burn leg, ~100s span) the icon dwells on, drawn far from the
                        // inertial loiter/hyperbolic orbits, is the body-fixed-vs-inertial loop-shift
                        // rotation: compare lon0/lonN here to the probe's lonOrbit* for the same ghost.
                        // Built lazily: the two LegPointBodyRelLonDeg (=GetWorldSurfacePosition) calls run
                        // only when one of the two logs below actually emits (rate-limit elapsed / change),
                        // not every frame for a multi-leg recording.
                        int activeLegCaptured = activeLeg;
                        var setCaptured = set;
                        GameScenes sceneCaptured = scene;
                        Func<string> activeLegInfoFactory = () =>
                        {
                            if (activeLegCaptured < 0) return "activeLeg=none";
                            var al = setCaptured.legs[activeLegCaptured];
                            int mAl = al.PointCount;
                            CelestialBody alBody = ResolveBodyByName(sceneCaptured, al.bodyName);
                            if (alBody != null && mAl >= 1)
                                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                    "DRAWN-leg{0}=[{1:F1},{2:F1}] span={3:F0}s body={4} pts={5} lon0={6:F1} lonN={7:F1} alt0={8:F0} altN={9:F0}",
                                    activeLegCaptured, al.startUT, al.endUT, al.endUT - al.startUT, al.bodyName ?? "(null)", mAl,
                                    LegPointBodyRelLonDeg(alBody, al.lats[0], al.lons[0], al.alts[0]),
                                    LegPointBodyRelLonDeg(alBody, al.lats[mAl - 1], al.lons[mAl - 1], al.alts[mAl - 1]),
                                    al.alts[0], al.alts[mAl - 1]);
                            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "DRAWN-leg{0}=[{1:F1},{2:F1}] span={3:F0}s body={4} pts={5} (no body/pts)",
                                activeLegCaptured, al.startUT, al.endUT, al.endUT - al.startUT, al.bodyName ?? "(null)", mAl);
                        };

                        bool anyDrawnCaptured = anyDrawn;
                        double headUtCaptured = headUT;
                        ParsekLog.VerboseRateLimited(DriverTag, "polyline.head." + rec.RecordingId,
                            () => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "Polyline head: rec={0} legs={1} headUT={2:F1} activeLeg={3} drawn={4} {5} " +
                                "firstLeg=[{6:F1},{7:F1}] lastLeg=[{8:F1},{9:F1}] body0={10} bodyN={11}",
                                rec.RecordingId, setCaptured.legs.Length, headUtCaptured, activeLegCaptured, anyDrawnCaptured, activeLegInfoFactory(),
                                setCaptured.legs[0].startUT, setCaptured.legs[0].endUT,
                                setCaptured.legs[setCaptured.legs.Length - 1].startUT, setCaptured.legs[setCaptured.legs.Length - 1].endUT,
                                setCaptured.legs[0].bodyName ?? "(null)", setCaptured.legs[setCaptured.legs.Length - 1].bodyName ?? "(null)"),
                            2.0);

                        // CHANGE-based companion to the rate-limited head log: a discrete event whenever the
                        // active leg, its body, or the drawn state flips. The polyline's part of the SOI-exit
                        // blink (active leg jumps a Kerbin escape leg -> the Sun transfer leg, or drawn toggles
                        // on/off in a head-in-gap frame) shows as alternating MapTraj-style lines instead of
                        // being hidden in the 2s rate-limited samples. The cheap state key is built eagerly;
                        // the GetWorldSurfacePosition-heavy detail is deferred to the change emit.
                        string activeLegBody = activeLeg >= 0
                            ? (set.legs[activeLeg].bodyName ?? "(null)") : "none";
                        ParsekLog.VerboseOnChange(DriverTag, "polyline-active." + rec.RecordingId,
                            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "{0}|{1}|{2}", activeLeg, activeLegBody, anyDrawn),
                            () => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "Polyline active-leg CHANGED: rec={0} headUT={1:F1} activeLeg={2} body={3} " +
                                "drawn={4} legs={5} {6}",
                                rec.RecordingId, headUtCaptured, activeLegCaptured, activeLegBody, anyDrawnCaptured, setCaptured.legs.Length,
                                activeLegInfoFactory()));
                    }

                    // SINGLE-LEG draw-state companion (logging gap fill): the multi-leg head/active-leg
                    // diagnostics above only run for set.legs.Length > 1, so a SINGLE-leg recording (e.g. a
                    // descent member's lone deorbit->landing clip, recs acf1435a / c5d0148a) had NO
                    // per-recording line when its one leg toggled drawn<->head-UT-gated. That toggle is exactly
                    // the descent-revert symptom on the polyline surface (the descent leg stops drawing the
                    // instant the head leaves its window), so surface it on CHANGE keyed by recordingId: one
                    // discrete line whenever this recording's single-leg draw state flips, carrying the head UT
                    // and the leg span so the flip is greppable alongside the [ReaimDescent] phase line and the
                    // [GhostMap] ghost teardown for the same recording. Restricted to single-leg recordings so
                    // the multi-leg path keeps its richer active-leg CHANGED line as the sole signal.
                    if (set.legs.Length == 1)
                    {
                        var oneLeg = set.legs[0];
                        bool anyDrawnOne = anyDrawn;
                        double headUtOne = headUT;
                        string recIdOne = rec.RecordingId;
                        ParsekLog.VerboseOnChange(DriverTag, "polyline-singleleg." + rec.RecordingId,
                            anyDrawnOne ? "drawn" : "gated",
                            () => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "Polyline single-leg draw CHANGED: rec={0} drawn={1} headUT={2:F1} " +
                                "leg=[{3:F1},{4:F1}] span={5:F0}s body={6} pts={7}",
                                recIdOne, anyDrawnOne, headUtOne, oneLeg.startUT, oneLeg.endUT,
                                oneLeg.endUT - oneLeg.startUT, oneLeg.bodyName ?? "(null)", oneLeg.PointCount));
                    }

                    if (anyDrawn)
                    {
                        frameDrawn++;
                        // 8e S3b (SOLE ownership publish): a leg actually drew this frame, so the polyline
                        // owns this non-orbital phase - regardless of whether the Director classified the
                        // span TracedPath (owned-by-treatment) or StockConic (Driver-direct "bridge" leg).
                        // The DRAW is the authoritative ownership signal (8b.2 actual-draw principle); the
                        // Director's StockConic/TracedPath classification is irrelevant to whether the proto
                        // line/icon must be hidden. Tell GhostMapPresence the polyline owns this recording's
                        // current phase so it hides the overlapping orbit line. This is the ONLY ownership
                        // set since S3b deleted the legacy publish; published gate-independently on the
                        // per-recording if (anyDrawn) condition, so gate-off reads correct membership.
                        drewNonOrbitalLegRecordings.Add(rec.RecordingId);

                        // Phase 8e S0 Instrument 1 (PURELY ADDITIVE): record this recording into the
                        // coverage-closure DRAWN set (will-draw == actual-draw here), and - when it has
                        // NO ProtoVessel ghost (ghostPid == 0, a pid-0 atmospheric/ascent recording the
                        // Director's ghostMapVesselPids enumeration cannot see) - into the proto-less
                        // COVERAGE set, the Director's genuine accounting of it via this non-proto walk.
                        // Gated on tracing so default play pays nothing; no render/draw effect.
                        if (MapRenderTrace.IsEnabled)
                        {
                            GhostMapPresence.NoteDrawnRecordingCoverage(rec.RecordingId, ghostPid);
                        }
                    }

                    // ---- FORWARD ADDITIVE PASS (Step 3, forward-trajectory-render plan) ----
                    // Draw the FUTURE portion of the trajectory ahead of the icon as one continuous chain,
                    // up to the forward stop (first full-loop closed orbit / first SOI change / end of
                    // data). This is PURELY ADDITIVE: it enqueues forward legs (B') + forward arcs (C) that
                    // do NOT touch drewNonOrbitalLegRecordings / anyDrawn, so the current element renders
                    // exactly as today and the ownership contract is unchanged (the CRITICAL Step 3
                    // prerequisite, safest option (a)). Gated on the SAME visibility the rest of the loop
                    // resolved (renderHidden already continued above) plus the Director's gap-hold.
                    //
                    // PRIMARY forward pass runs only when the primary is in-window. When the primary is hidden
                    // (downstream through-line at an orbital phase) but a secondary is live, the primary has no
                    // forward trajectory to draw here - the secondary forward pass below carries the launch
                    // ascent + escape conic.
                    if (primaryRenders)
                    {
                        DecideForwardWindowForRecording(
                            scene, recordingIndex, rec, set, headUT, currentUT, loopUnits,
                            ghostPid, drawFrame, surface, committed, suppressed,
                            ref frameForwardLegs, ref frameForwardArcs, ref frameForwardSkippedGapHold);
                    }

                    // ---- BOUNDARY-OVERLAP SECONDARY forward pass (launch->escape seam render fix) ----
                    // The early-launching instance N+1's FORWARD trajectory: the ascent polyline AHEAD of its
                    // icon + the escape conic ahead, drawn additively at secondaryHeadUT. Without this, only the
                    // single in-window ascent leg (the second-head pass above) drew - the takeoff/ascent
                    // polyline and the escape conic AHEAD of the icon never rendered after launch (the playtest
                    // bug). Keyed on the secondary ghost pid (so its forward legs/arcs publish under the
                    // secondary, and the bridges/run window resolve the secondary's phase). The chain-dedupe +
                    // gap-hold are bypassed for the secondary (see DecideForwardWindowForRecording). Inert when
                    // hasSecondaryHead is false (every non-launch-hold member / aligned loop).
                    if (hasSecondaryHead)
                    {
                        DecideForwardWindowForRecording(
                            scene, recordingIndex, rec, set, secondaryHeadUT, currentUT, loopUnits,
                            secondaryGhostPid, drawFrame, surface, committed, suppressed,
                            ref frameForwardLegs, ref frameForwardArcs, ref frameForwardSkippedGapHold,
                            boundaryOverlapSecondary: true);
                    }
                }

                // Pan-stability (FIX 1): hand the decided legs to the onPreCull draw pass. Stamp the
                // frame + target layer LAST, after the full decide walk, so the onPreCull pass only fires
                // when this LateUpdate ran to completion (early returns above leave pendingDrawsFrame=-1).
                // The actual point recompute + Draw3D + the deactivation sweep run in OnMapCameraPreCull
                // AFTER the map camera commits its pan. frameDeactivated below reports the PRIOR onPreCull
                // pass's sweep count (the sweep no longer runs in LateUpdate); it is a diagnostic only.
                pendingTargetLayer = targetLayer;
                pendingDrawsFrame = drawFrame;
                int frameDeactivated = lastSweepDeactivatedCount;

                ParsekLog.VerboseRateLimited(DriverTag, "polyline.frame.summary",
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Polyline frame: scene={0} drawn={1} warp={10:F0}x suppressed={2} hidden={3} staticSkip={4} noLegs={5} noBody={6} headUtGated={7} deactivated={8} cached={9} fwdLegs={11} fwdArcs={12} fwdGapHold={13} fwdCached={14}",
                        scene, frameDrawn, frameSkippedSuppressed, frameSkippedHidden,
                        frameSkippedStatic, frameSkippedNoLegs, frameSkippedNoBody,
                        frameLegsHeadUtGated, frameDeactivated, polylineCache.Count,
                        TimeWarp.CurrentRate,
                        frameForwardLegs, frameForwardArcs, frameForwardSkippedGapHold,
                        forwardArcCache.Count),
                    5.0);

                // DOUBLE-DRAW PIN (Bug 3): the 5 s rate-limited summary hides transient drawn>=2 frames
                // (the co-drawn landing seam). Emit on CHANGE of (drawn-count + the co-drawn recording set),
                // so a brief two-leg overlap is never rate-limited away. Names the recordings drawing this
                // frame + the warp rate (the user reports the second line only at 1x). The per-rec "Polyline
                // active-leg CHANGED" lines carry each leg's span, so this + those pin which two legs overlap.
                if (frameDrawn >= 1)
                {
                    // Key on the cheap drawn-COUNT (the Bug-3 signal is 1<->2); build the rec list lazily so
                    // the steady state (drawn unchanged) pays no per-frame List/Join/Format allocation.
                    int drawnCount = frameDrawn;
                    var legRecs = drewNonOrbitalLegRecordings;
                    var sceneForLog = scene;
                    ParsekLog.VerboseOnChange(DriverTag, "polyline.drawset",
                        drawnCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        () =>
                        {
                            var drawnIds = new List<string>(legRecs);
                            drawnIds.Sort(StringComparer.Ordinal);
                            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "Polyline draw-set CHANGED: scene={0} drawn={1} warp={2:F0}x recs=[{3}]",
                                sceneForLog, drawnCount, TimeWarp.CurrentRate, string.Join(",", drawnIds.ToArray()));
                        });
                }

                // Render-EVENT diff (map-render-event-logging): emit one PolylineLegChange appear/disappear
                // per recording whose current-element leg started / stopped drawing this completed walk
                // (vs the previous walk). Runs UNCONDITIONALLY here (not under frameDrawn>=1) so the
                // all-disappeared transition is caught. Gated on IsEnabled so disabled play pays nothing.
                if (MapRenderTrace.IsEnabled)
                    EmitPolylineDrawSetChanges(
                        MapRenderTrace.RenderSurface.Polyline,
                        previousDrewNonOrbitalLegRecordings, drewNonOrbitalLegRecordings,
                        Planetarium.GetUniversalTime());
            }

            /// <summary>
            /// Pan-stability draw pass (FIX 1). Runs from <c>Camera.onPreCull</c> filtered to the map
            /// camera, AFTER every LateUpdate (so the map camera has committed its pan for the frame) and
            /// AFTER the -50 decide pass enqueued this frame's will-draw legs. Dequeues each
            /// <see cref="PendingLegDraw"/> and runs the actual point recompute + conic-anchor +
            /// <c>Draw3D</c> via the SAME shared <see cref="TryDrawLeg"/> / <c>TryDrawOwnedLeg</c> the
            /// Driver used inline before; the only change is the SLOT, so the drawn bytes are identical.
            /// The deactivation sweep moves here too, so it observes <c>lastDrawnFrame == frame</c> for
            /// legs drawn this frame. <c>lastDrawnFrame</c> is stamped here, still BEFORE OnGUI, so the
            /// marker ride's "drawn this frame" guard keeps working. Reads only frame-stable cached state
            /// (none camera-dependent), so it is safe in this later slot.
            /// </summary>
            private void OnMapCameraPreCull(Camera cam)
            {
                // Map camera filter: PlanetariumCamera.Camera is the only camera whose committed matrix
                // Draw3D bakes against in map view. Other cameras (UI / scaled / shadow) must not trigger
                // the draw pass, and the per-frame guard de-dupes if onPreCull fires more than once.
                if (PlanetariumCamera.fetch == null || cam != PlanetariumCamera.Camera) return;
                int frame = Time.frameCount;
                if (pendingDrawsFrame != frame) return; // nothing decided this frame (early-returned LateUpdate)
                if (precullDrawnFrame == frame) return;  // already drew this frame
                precullDrawnFrame = frame;

                // Render-EVENT diff (map-render-event-logging): clear this frame's forward-render actual-draw
                // set before the draw loops populate it; diffed against the previous frame at the end of this
                // method to emit PolylineForwardArc appear/disappear EVENTs. Only touched when tracing is on.
                if (MapRenderTrace.IsEnabled)
                    currentDrewForwardArcRecordings.Clear();

                int drawn = 0;
                // Draw-side instrumentation (run model): account RUN legs (forward=true) separately and
                // capture the otherwise-silent lookup-skip paths, so a "past segment hidden" symptom is
                // diagnosable from the log. The decide side already logs the run window + runLegs+= ENQUEUE
                // count; this logs whether those enqueued run legs actually reach TryDrawLeg and DRAW.
                int runEnq = 0, runDrawn = 0, runSkipNoCache = 0, runSkipBadIndex = 0;
                for (int i = 0; i < pendingDraws.Count; i++)
                {
                    var p = pendingDraws[i];
                    bool fwd = p.forward;
                    if (fwd) runEnq++;
                    if (string.IsNullOrEmpty(p.recordingId)) { if (fwd) runSkipNoCache++; continue; }
                    if (!polylineCache.TryGetValue(p.recordingId, out var set) || set.legs == null)
                    {
                        if (fwd)
                        {
                            runSkipNoCache++;
                            ParsekLog.VerboseRateLimited(DriverTag, "run-leg-nocache." + p.recordingId,
                                "Run leg skip (no polyline cache / null legs): rec=" + p.recordingId
                                    + " leg=" + p.legIndex, 3.0);
                        }
                        continue;
                    }
                    if (p.legIndex < 0 || p.legIndex >= set.legs.Length)
                    {
                        if (fwd)
                        {
                            runSkipBadIndex++;
                            ParsekLog.VerboseRateLimited(DriverTag, "run-leg-badidx." + p.recordingId,
                                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                    "Run leg skip (index OOB): rec={0} leg={1} legCount={2}",
                                    p.recordingId, p.legIndex, set.legs.Length), 3.0);
                        }
                        continue;
                    }
                    var leg = set.legs[p.legIndex];
                    // PAST run legs require the conic anchor to SUCCEED (playtest-5/7 body-fixed hide):
                    // a trailing leg that cannot be rotated onto the inertial seam is skipped here
                    // rather than drawn body-fixed, where it would sweep with the planet. FUTURE run
                    // legs and the head-gated current leg draw regardless (anchored when possible,
                    // body-fixed otherwise), connected by the seam bridges.
                    bool legDrawn = p.ownedByTreatment
                        ? Parsek.MapRender.TracedPathTreatment.TryDrawOwnedLeg(
                            ref leg, p.rec, p.body, pendingTargetLayer, frame, p.recordingId, p.legIndex, p.ghostPid)
                        : TryDrawLeg(
                            ref leg, p.rec, p.body, pendingTargetLayer, frame, p.recordingId, p.legIndex,
                            requireConicAnchor: p.requireConicAnchor);
                    // Persist the lazily-inflated line + the lastDrawnFrame stamp back into the cached
                    // array (set.legs is the same array reference the dict holds).
                    set.legs[p.legIndex] = leg;
                    if (legDrawn) drawn++;
                    if (fwd)
                    {
                        if (legDrawn) runDrawn++;
                        // Render-EVENT diff: a forward (run) leg that actually painted means the forward
                        // trajectory chain is being drawn for this recording this frame.
                        if (legDrawn && MapRenderTrace.IsEnabled)
                            currentDrewForwardArcRecordings.Add(p.recordingId);
                        if (!legDrawn) ParsekLog.VerboseRateLimited(DriverTag, "run-leg-nodraw." + p.recordingId,
                            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "Run leg NOT drawn (TryDrawLeg false): rec={0} leg={1} pts={2} body={3} bodyNull={4} " +
                                "startUT={5:F1} endUT={6:F1}",
                                p.recordingId, p.legIndex, leg.PointCount,
                                string.IsNullOrEmpty(leg.bodyName) ? "<none>" : leg.bodyName,
                                p.body == null, leg.startUT, leg.endUT), 3.0);
                    }

                    // GAP-2: first-class Polyline-surface trace at the ACTUAL draw site. The
                    // surface=Polyline slot was previously blind under MapRenderTrace (the Driver only
                    // emitted Verbose lines on its own tag), so a reader could not grep the polyline
                    // draw under the tracer - exactly the TS-invisible-polyline bug class (the layer-31
                    // fix above is "decided to draw but didn't paint where expected"). This lives in the
                    // SHARED Driver, so the one insertion covers FLIGHT and TRACKSTATION. Rate-limit /
                    // change KEY is (Polyline, p.recordingId) only - warp-stable per the #1063 rule;
                    // the per-leg index / UT / draw count / frame are warp-advancing and live in the
                    // message BODY, never the key (EmitMarker rate-limits per (surface, key) on
                    // wall-clock). Emitted only on an ACTUAL leg draw so a skipped leg is not traced.
                    if (legDrawn && MapRenderTrace.IsEnabled)
                        MapRenderTrace.EmitMarker(
                            MapRenderTrace.RenderSurface.Polyline, p.recordingId,
                            Planetarium.GetUniversalTime(),
                            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "scene={0} leg={1} body={2} pts={3} owned={4} layer={5} startUT={6:F1} endUT={7:F1}",
                                HighLogic.LoadedScene, p.legIndex,
                                string.IsNullOrEmpty(leg.bodyName) ? "<none>" : leg.bodyName,
                                leg.PointCount, p.ownedByTreatment, pendingTargetLayer,
                                leg.startUT, leg.endUT),
                            recId: p.recordingId);

                    // Phase 5b: the deferred Tier-C rigid-seam-tangent raise, at the only place the live
                    // world tangents exist - the stitched descent's seam-entry leg just DREW, so its first
                    // two world points (the entering tangent) and the bracketing capture conic sampled at
                    // the seam (the leaving tangent) are both available. Tracing-gated by the decide-pass
                    // flag (ShouldEvaluateTangentSeamAtDraw), once-per-onset inside. A continuous seam
                    // emits nothing.
                    if (legDrawn && p.evalTangentSeam)
                        EvaluateDescentSeamTangents(p, leg);
                }
                pendingDraws.Clear();

                // SEAM BRIDGE draws (playtest 6/7, review MINOR-2 reorder): run AFTER the legs (each
                // leg's fresh scratchScaledSpace supplies its bridge endpoints) but BEFORE the arcs,
                // so a bridge that actually DRAWS can clip its target arc's lead-in/tail at the merge
                // sample - and a failed bridge leaves the arc unclipped (no one-frame hole). Bridges
                // read only cached offsets, never the arcs' draw output, so the reorder is safe.
                int bridgeDrawn = 0;
                GameScenes fwdScene = HighLogic.LoadedScene;
                if (pendingBridgeFrame == frame)
                {
                    for (int b = 0; b < pendingBridges.Count; b++)
                    {
                        var bridge = pendingBridges[b];
                        if (!TryDrawSeamBridge(fwdScene, bridge, frame)) continue;
                        bridgeDrawn++;
                        if (bridge.clipArcPendingIndex >= 0
                            && bridge.clipArcPendingIndex < pendingForwardArcs.Count
                            && forwardArcCache.TryGetValue(bridge.arcRecordingId, out var clipSet)
                            && clipSet.arcs != null && bridge.arcIndex < clipSet.arcs.Length
                            && clipSet.arcs[bridge.arcIndex].bodyRelOffsets != null)
                        {
                            var pd = pendingForwardArcs[bridge.clipArcPendingIndex];
                            int len = clipSet.arcs[bridge.arcIndex].bodyRelOffsets.Length;
                            if (bridge.atLegStart) pd.drawEndIndex = len - 1 - BridgeMergeSampleCount;
                            else pd.drawStartIndex = BridgeMergeSampleCount;
                            pendingForwardArcs[bridge.clipArcPendingIndex] = pd;
                        }
                    }
                }

                // FORWARD ARCS (Step 3 C): draw the future orbit arcs decided this frame in the same
                // post-pan slot as the legs. The arc geometry is already sampled (cache-key-gated in the
                // decide pass), so this just reactivates + Draw3D's the cached line. Forward arcs are a
                // separate additive surface - they never touch drewNonOrbitalLegRecordings, so the current
                // arc + icon (stock OrbitRenderer) render exactly as today.
                int fwdArcsDrawn = 0;
                for (int i = 0; i < pendingForwardArcs.Count; i++)
                {
                    var fp = pendingForwardArcs[i];
                    if (string.IsNullOrEmpty(fp.recordingId)) continue;
                    if (!forwardArcCache.TryGetValue(fp.recordingId, out var fset) || fset.arcs == null)
                        continue;
                    if (fp.arcIndex < 0 || fp.arcIndex >= fset.arcs.Length) continue;
                    var arc = fset.arcs[fp.arcIndex];
                    if (DrawForwardArc(
                            fwdScene, ref arc, pendingTargetLayer, frame,
                            fp.drawStartIndex, fp.drawEndIndex))
                        fwdArcsDrawn++;
                    fset.arcs[fp.arcIndex] = arc; // persist the lastDrawnFrame stamp (same array ref)

                    if (arc.vectorLine != null && arc.lastDrawnFrame == frame && MapRenderTrace.IsEnabled)
                    {
                        // Render-EVENT diff: this forward arc actually painted for this recording this frame.
                        currentDrewForwardArcRecordings.Add(fp.recordingId);
                        // surface=PolylineForwardArc (was Polyline + a "FWD-ARC" string prefix): a distinct
                        // surface so a grep separates "the extra predicted line ahead of the ghost" from the
                        // current element.
                        MapRenderTrace.EmitMarker(
                            MapRenderTrace.RenderSurface.PolylineForwardArc, fp.recordingId,
                            Planetarium.GetUniversalTime(),
                            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "scene={0} arc={1} body={2} startUT={3:F1} endUT={4:F1} layer={5}",
                                HighLogic.LoadedScene, fp.arcIndex,
                                string.IsNullOrEmpty(arc.bodyName) ? "<none>" : arc.bodyName,
                                arc.startUT, arc.endUT, pendingTargetLayer),
                            recId: fp.recordingId);
                    }
                }
                pendingForwardArcs.Clear();
                if (fwdArcsDrawn > 0)
                    ParsekLog.VerboseRateLimited(DriverTag, "fwd-arc-draw",
                        string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "Forward arcs drawn: scene={0} count={1} legsDrawn={2} warp={3:F0}x",
                            fwdScene, fwdArcsDrawn, drawn, TimeWarp.CurrentRate),
                        5.0);

                // Deactivation sweep MOVES here so it runs in the same slot as the draws: a leg drawn
                // this frame has lastDrawnFrame == frame and is NOT hidden, while any cached leg not
                // drawn this frame (recording-level skip, head-UT gate, body missing, removed recording)
                // is hidden. Draw3D() is one-shot, so a line stays visible until explicitly deactivated.
                lastSweepDeactivatedCount = RunDeactivationSweep(frame);
                // Forward-arc deactivation sweep (Step 3 C): same one-shot-Draw3D contract - hide any
                // forward arc not drawn this frame (window advanced past it, gap-hold, recording removed).
                int fwdArcsDeactivated = RunForwardArcDeactivationSweep(frame);
                // Bridge deactivation sweep: hide any bridge line not drawn this frame (icon left the
                // body-fixed head leg, gap closed at handoff, run cleared). Entries are classes, so the
                // in-place mutation never invalidates the enumerator.
                foreach (var kvp in bridgeLineByRecording)
                {
                    var entry = kvp.Value;
                    if (entry.line != null && entry.line.active && entry.lastDrawnFrame != frame)
                        entry.line.active = false;
                }

                // ALWAYS-ON onPreCull draw summary (run-model diagnosis). Unlike the arc-only "Forward arcs
                // drawn" line above (gated on fwdArcsDrawn>0), this fires every frame the map onPreCull
                // COMPLETES, so a phase where only RUN LEGS draw (no arcs) is no longer blind. A "past
                // segment hidden" symptom shows here as runEnq>0 with runDrawn<runEnq (enqueued run legs not
                // reaching / clearing TryDrawLeg), and its absence while the decide side logs "Render run"
                // means onPreCull early-returned (the draw never ran this frame).
                ParsekLog.VerboseRateLimited(DriverTag, "polyline-precull-draw",
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "onPreCull draw: scene={0} totalLegsDrawn={1} runLegs={2}/{3} (noCache={4} badIdx={5}) " +
                        "arcsDrawn={6} bridge={7} legsDeact={8} arcsDeact={9} frame={10} warp={11:F0}x",
                        HighLogic.LoadedScene, drawn, runDrawn, runEnq, runSkipNoCache, runSkipBadIndex,
                        fwdArcsDrawn, bridgeDrawn, lastSweepDeactivatedCount, fwdArcsDeactivated, frame,
                        TimeWarp.CurrentRate),
                    2.0);

                // Render-EVENT diff (map-render-event-logging): emit PolylineForwardArc appear/disappear per
                // recording whose forward (predicted) trajectory chain started / stopped painting this frame
                // vs the previous onPreCull. Read-only diff of the actual-draw set populated above.
                if (MapRenderTrace.IsEnabled)
                    EmitPolylineDrawSetChanges(
                        MapRenderTrace.RenderSurface.PolylineForwardArc,
                        previousDrewForwardArcRecordings, currentDrewForwardArcRecordings,
                        Planetarium.GetUniversalTime());
            }

            // The UT step used to sample the leaving (capture-conic) tangent just before the seam.
            private const double TangentSeamConicSampleDtSeconds = 1.0;

            /// <summary>
            /// Phase 5b: the Tier-C <c>rigid-seam-tangent-discontinuity</c> production raise, wired at
            /// the descent DRAW site (the only place the live world tangents exist - a RenderSegment
            /// carries no points; see the Phase-6 deferral note in
            /// <see cref="Parsek.MapRender.CrossMemberSeamStitcher"/>). Called only for a drawn
            /// seam-entry leg of a stitched descent member (the decide-pass
            /// <c>ShouldEvaluateTangentSeamAtDraw</c> gate, tracing-gated). ENTERING tangent = the drawn
            /// descent leg's first two body-relative world points at the live rotation; LEAVING tangent =
            /// the same-body capture/parking conic ending at the seam, sampled just before its end. An
            /// unmeasurable side (no bracketing conic / degenerate geometry) reads CONTINUOUS - no false
            /// anomaly. Once-per-onset via <see cref="MapRenderTrace.ShouldEmitTangentSeamOnChange"/>
            /// (both states feed the signature gate, so a healed seam re-arms the next onset); a real
            /// kink emits the anomaly, a continuous seam emits nothing.
            /// </summary>
            private void EvaluateDescentSeamTangents(PendingLegDraw p, LegPolyline leg)
            {
                if (!MapRenderTrace.IsEnabled) return; // decide-pass flag is tracing-gated; cheap re-check
                if (leg.PointCount < 2 || p.body == null || p.rec == null) return;

                // ROTATION-ALIGNMENT ASSUMPTION: the entering tangent below is body-fixed recorded points
                // at the LIVE rotation; the leaving tangent is the recorded conic at the RECORDED seam
                // epoch (inertial). The two frames coincide because the descent trigger fires
                // rotation-aligned; a trigger with a nonzero site-rotation residual (loiter-cut cycles)
                // can measure a residual-sized angle. If this anomaly ever fires in a tracing run, check
                // siteRotResidual in the DESCENT RENDERED line before suspecting a real seam kink.

                // ENTERING tangent: the drawn descent leg's first two recorded points as body-relative
                // world positions at the LIVE rotation (double subtraction first - float would
                // catastrophically cancel at raw world magnitudes).
                Vector3d bodyPos = p.body.position;
                Vector3d w0 = p.body.GetWorldSurfacePosition(leg.lats[0], leg.lons[0], leg.alts[0]) - bodyPos;
                Vector3d w1 = p.body.GetWorldSurfacePosition(leg.lats[1], leg.lons[1], leg.alts[1]) - bodyPos;
                Vector3 entering = Parsek.MapRender.CrossMemberSeamStitcher.TangentFromPositions(
                    (Vector3)w0, (Vector3)w1);

                // LEAVING tangent: the capture/parking conic ENDING at the leg's start (the seam) - the
                // same-body OrbitSegment whose endUT is nearest leg.startUT within the bridge seam
                // tolerance, sampled as two conic positions just before the seam. No such conic (or a
                // degenerate/throwing one) leaves the tangent unmeasurable -> continuous (no false
                // anomaly).
                Vector3 leaving = Vector3.zero;
                var segs = p.rec.OrbitSegments;
                if (segs != null)
                {
                    double bestDist = double.PositiveInfinity;
                    int bestIdx = -1;
                    for (int i = 0; i < segs.Count; i++)
                    {
                        var s = segs[i];
                        if (s.endUT <= s.startUT) continue;
                        if (!string.Equals(s.bodyName, leg.bodyName, StringComparison.Ordinal)) continue;
                        double dist = System.Math.Abs(leg.startUT - s.endUT);
                        if (dist <= BridgeMaxSeamGapSeconds && dist < bestDist)
                        {
                            bestDist = dist;
                            bestIdx = i;
                        }
                    }
                    if (bestIdx >= 0)
                    {
                        var s = segs[bestIdx];
                        try
                        {
                            var orbit = new Orbit(
                                s.inclination, s.eccentricity, s.semiMajorAxis,
                                s.longitudeOfAscendingNode, s.argumentOfPeriapsis,
                                s.meanAnomalyAtEpoch, s.epoch, p.body);
                            Vector3d c0 = orbit.getPositionAtUT(s.endUT - TangentSeamConicSampleDtSeconds);
                            Vector3d c1 = orbit.getPositionAtUT(s.endUT);
                            if (IsFiniteVec(c0) && IsFiniteVec(c1))
                                leaving = Parsek.MapRender.CrossMemberSeamStitcher.TangentFromPositions(
                                    (Vector3)(c0 - bodyPos), (Vector3)(c1 - bodyPos));
                        }
                        catch (Exception)
                        {
                            leaving = Vector3.zero; // unmeasurable -> no anomaly
                        }
                    }
                }

                bool continuous = Parsek.MapRender.CrossMemberSeamStitcher.IsTangentSeamContinuous(
                    leaving, entering);
                double angleRad = Parsek.MapRender.CrossMemberSeamStitcher.TangentSeamAngleRadians(
                    leaving, entering);

                // Once-per-onset: feed the state through the signature gate in BOTH states (a healed seam
                // re-arms the next onset); only a CHANGED discontinuous state emits the anomaly.
                string pidKey = p.ghostPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string signature = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0}|leg{1}|{2}", p.recordingId, p.legIndex, continuous ? "cont" : "disc");
                bool changed = MapRenderTrace.ShouldEmitTangentSeamOnChange(pidKey, signature);
                if (changed && !continuous)
                {
                    Parsek.MapRender.CrossMemberSeamStitcher.EmitTangentDiscontinuity(
                        p.ghostPid, p.recordingId, Planetarium.GetUniversalTime(),
                        leaving, entering, angleRad);
                }

                ParsekLog.VerboseRateLimited(DriverTag, "descent-seam-tangent." + p.recordingId,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Descent G1 seam tangents: rec={0} leg={1} continuous={2} angleRad={3:F4} " +
                        "leavingMeasured={4} enteringMeasured={5} pid={6}",
                        p.recordingId, p.legIndex, continuous, angleRad,
                        leaving != Vector3.zero, entering != Vector3.zero, p.ghostPid),
                    5.0);
            }

            /// <summary>
            /// Live <see cref="ConicGapSampler"/> (playtest-12 straight-chord fix): evaluates the
            /// below-surface descent conic at one UT through the frame-correct stock Orbit and
            /// converts to the RECORDED-basis body-fixed triple - latitude/altitude are rotation-free,
            /// the longitude is counter-rotated via <see cref="RecordedLongitudeAtUT"/> so the filled
            /// point sits on the same rotation basis as the recorded leg points around it (the
            /// azimuth-minus-rotation-at-sample-time identity makes the result time-invariant, so the
            /// cached leg never drifts between rebuilds).
            /// </summary>
            private bool SampleConicBodyFixedAtUT(
                OrbitSegment seg, double ut, out double lat, out double lon, out double alt)
            {
                lat = 0; lon = 0; alt = 0;
                CelestialBody body = ResolveBodyByName(HighLogic.LoadedScene, seg.bodyName);
                if (body == null) return false;
                try
                {
                    var orbit = new Orbit(
                        seg.inclination, seg.eccentricity, seg.semiMajorAxis,
                        seg.longitudeOfAscendingNode, seg.argumentOfPeriapsis,
                        seg.meanAnomalyAtEpoch, seg.epoch, body);
                    Vector3d world = orbit.getPositionAtUT(ut);
                    if (!IsFiniteVec(world)) return false;
                    lat = body.GetLatitude(world);
                    lon = RecordedLongitudeAtUT(
                        body.GetLongitude(world), ut, Planetarium.GetUniversalTime(),
                        body.rotationPeriod);
                    alt = body.GetAltitude(world);
                }
                catch (Exception)
                {
                    return false;
                }
                return true;
            }

            /// <summary>
            /// Resamples one cached forward arc's body-relative offsets IN PLACE from its source
            /// segment (playtest-9 rotating-frame fix): same VectorLine, same arrays - only the
            /// values refresh, evaluated through the live (frame-correct) Orbit. False on a body /
            /// orbit / sampler fault; the stale offsets are then kept (a coherent stale frame beats
            /// a torn one).
            /// </summary>
            private bool ResampleForwardArcOffsetsInPlace(GameScenes scene, ref ForwardArc arc)
            {
                if (arc.bodyRelOffsets == null) return false;
                CelestialBody body = ResolveBodyByName(scene, arc.bodyName);
                if (body == null) return false;
                var seg = arc.sourceSegment;
                Orbit orbit;
                try
                {
                    orbit = new Orbit(
                        seg.inclination, seg.eccentricity, seg.semiMajorAxis,
                        seg.longitudeOfAscendingNode, seg.argumentOfPeriapsis,
                        seg.meanAnomalyAtEpoch, seg.epoch, body);
                }
                catch (Exception)
                {
                    return false;
                }
                OrbitArcSampler.ArcSampleResult res =
                    OrbitArcSampler.SampleSegmentArc(orbit, seg.startUT, seg.endUT, forwardArcSampleScratch);
                if (!res.Sampled || res.Count != arc.bodyRelOffsets.Length) return false;
                Vector3d bodyPos = body.position;
                for (int i = 0; i < res.Count; i++)
                    arc.bodyRelOffsets[i] = forwardArcSampleScratch[i] - bodyPos;
                return true;
            }

            /// <summary>
            /// SEAM BRIDGE draw, one (leg, side) pair (playtest 6, generalized playtest 7): connects
            /// the leg's drawn endpoint to the adjacent inertial conic with the CONIC'S OWN curvature,
            /// the seam rotation unwound along it (<see cref="TryBuildSeamBridgeLocalPoints"/>). The
            /// leg endpoint comes from its scratchScaledSpace (fresh - the leg drew earlier in this
            /// same onPreCull pass, so the bridge tracks it EVERY frame at any warp); the conic
            /// samples are the cached forward-arc offsets (lead-in for an end-side bridge, tail
            /// REVERSED for a start-side bridge) or the on-demand samples for a stock-drawn current
            /// element, re-projected with the same strobe-free scaled-centre geometry the arc draw
            /// uses. Returns false (the sweep hides any prior line) when the leg did not draw this
            /// frame, the caches are gone, the body is unresolvable, or the seam is degenerate.
            /// </summary>
            private bool TryDrawSeamBridge(GameScenes scene, PendingBridgeDraw bridge, int frame)
            {
                if (!polylineCache.TryGetValue(bridge.legRecordingId, out var legSet)
                    || legSet.legs == null
                    || bridge.legIndex < 0 || bridge.legIndex >= legSet.legs.Length)
                    return false;
                var leg = legSet.legs[bridge.legIndex];
                int m = leg.PointCount;
                // The leg must have ACTUALLY drawn this frame: its scratchScaledSpace is then fresh
                // for this frame's camera, and a bridge without its leg line would float alone.
                if (m < 1 || leg.scratchScaledSpace == null || leg.lastDrawnFrame != frame)
                    return false;

                // Conic-side sample slice into the fixed 61-point scratch. End-side bridges unwind the
                // conic's LEAD-IN forward (slice[0]=conic start gets the full seam rotation, lands on
                // the leg end). Start-side bridges unwind the conic's TAIL reversed (slice[0]=conic end
                // gets the full rotation, lands on the leg start; slice[60]=the tail merge sample) -
                // the output polyline's point order does not matter for rendering.
                string bodyName;
                if (bridge.arcIndex >= 0)
                {
                    if (!forwardArcCache.TryGetValue(bridge.arcRecordingId, out var fset)
                        || fset.arcs == null
                        || bridge.arcIndex >= fset.arcs.Length)
                        return false;
                    var arc = fset.arcs[bridge.arcIndex];
                    var offs = arc.bodyRelOffsets;
                    if (offs == null || offs.Length <= BridgeMergeSampleCount) return false;
                    int last = offs.Length - 1;
                    for (int i = 0; i <= BridgeMergeSampleCount; i++)
                        bridgeArcSliceScratch[i] = bridge.atLegStart ? offs[last - i] : offs[i];
                    bodyName = arc.bodyName;
                }
                else
                {
                    CelestialBody sampleBody = ResolveBodyByName(scene, bridge.sampleSegment.bodyName);
                    var samples = GetBridgeConicSamples(
                        bridge.sampleKeyRecordingId, bridge.sampleSegment, sampleBody,
                        tail: bridge.atLegStart);
                    if (samples == null || samples.Length <= BridgeMergeSampleCount) return false;
                    for (int i = 0; i <= BridgeMergeSampleCount; i++)
                        bridgeArcSliceScratch[i] = bridge.atLegStart
                            ? samples[BridgeMergeSampleCount - i] : samples[i];
                    bodyName = bridge.sampleSegment.bodyName;
                }

                CelestialBody body = ResolveBodyByName(scene, bodyName);
                if (body == null) return false;
                var scaledBody = body.scaledBody;
                Transform scaledXform = scaledBody != null ? scaledBody.transform : null;
                Vector3 center = scaledXform != null
                    ? scaledXform.position
                    : (Vector3)ScaledSpace.LocalToScaledSpace(body.position);

                // Leg endpoint in scaled space, body-relative (the leg's points are absolute scaled).
                int pi = bridge.atLegStart ? 0 : m - 1;
                Vector3d legEndRel = (Vector3d)(leg.scratchScaledSpace[pi] - center);
                double seamAngleRad;
                if (bridge.chord)
                {
                    // SMALL-GAP CHORD (launch->escape seam render): a straight line from the leg's drawn
                    // end to the conic's NEAR endpoint (bridgeArcSliceScratch[0] is the conic sample
                    // nearest the leg for either side, after the slice fill above). No rotation, no clip.
                    if (!TryBuildSeamBridgeChordLocalPoints(
                            legEndRel, bridgeArcSliceScratch[0], ScaledSpace.InverseScaleFactor,
                            BridgeMergeSampleCount, bridgePointScratch))
                        return false;
                    seamAngleRad = SeamBridgeAngleRad(
                        legEndRel, bridgeArcSliceScratch[0] * ScaledSpace.InverseScaleFactor);
                }
                else if (!TryBuildSeamBridgeLocalPoints(
                        legEndRel, bridgeArcSliceScratch, ScaledSpace.InverseScaleFactor,
                        BridgeMergeSampleCount, BridgeMaxAngleRadians, bridgePointScratch,
                        out seamAngleRad))
                    return false;

                string lineKey = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0}|{1}|{2}", bridge.legRecordingId, bridge.legIndex,
                    bridge.atLegStart ? "s" : "e");
                if (!bridgeLineByRecording.TryGetValue(lineKey, out var entry))
                {
                    entry = new BridgeLineEntry
                    {
                        line = BuildLegVectorLine(lineKey, -1, BridgeMergeSampleCount + 1),
                    };
                    bridgeLineByRecording[lineKey] = entry;
                }
                // Bug 1: rebuild on a map-line mode flip (3D Draw3D <-> 2D Draw) - the in-place Vectrosity
                // swap leaves the 2D canvas line non-rendering. See RebuildLineForMode.
                int bridgeWantMode = MapLineUses3D() ? 1 : 2;
                if (entry.line != null && entry.lineMode != bridgeWantMode)
                    entry.line = RebuildLineForMode(entry.line, BridgeMergeSampleCount + 1);
                if (entry.line == null)
                    entry.line = BuildLegVectorLine(lineKey, -1, BridgeMergeSampleCount + 1);
                entry.lineMode = bridgeWantMode;
                var line = entry.line;
                if (line == null) return false;

                var pts = line.points3;
                if (pts == null || pts.Count < BridgeMergeSampleCount + 1) return false;
                for (int i = 0; i <= BridgeMergeSampleCount; i++)
                    pts[i] = center + (Vector3)bridgePointScratch[i];
                line.drawStart = 0;
                line.drawEnd = BridgeMergeSampleCount;

                // Layer 31 always; world-transform zeroing only in 3D mode (2D canvas children must keep
                // their canvas placement or they vanish on zoom-out - Bug 1). See PrepareMapLineTransform.
                PrepareMapLineTransform(line, pendingTargetLayer);
                if (!line.active) line.active = true;
                // Bug 1 fix: draw in the stock map-line mode (Draw3D / 2D Draw past the far-zoom
                // threshold) so the seam bridge never vanishes on zoom-out. See DrawMapLine.
                DrawMapLine(line);
                entry.lastDrawnFrame = frame;

                // Lazy: the chord-deviation + slice-span diagnostics (playtest-11 straightness report)
                // compute only when the rate limit actually emits. Both scratches are consumed
                // synchronously within this call, so the capture is safe.
                ParsekLog.VerboseRateLimited(DriverTag, "bridge-draw." + lineKey,
                    () => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Seam bridge drawn: rec={0} leg={1} side={2} src={3} angleDeg={4:F2} body={5} " +
                        "merge={6} chordDevKm={7:F1} sliceSpanDeg={8:F2}",
                        bridge.legRecordingId, bridge.legIndex, bridge.atLegStart ? "start" : "end",
                        bridge.chord ? "chord" : (bridge.arcIndex >= 0 ? "cached-arc" : "sampled-conic"),
                        seamAngleRad * (180.0 / System.Math.PI), bodyName ?? "?",
                        BridgeMergeSampleCount,
                        MaxChordDeviation(bridgePointScratch, BridgeMergeSampleCount + 1)
                            * ScaledSpace.ScaleFactor / 1000.0,
                        SeamBridgeAngleRad(
                            bridgeArcSliceScratch[0], bridgeArcSliceScratch[BridgeMergeSampleCount])
                            * (180.0 / System.Math.PI)),
                    2.0);
                return true;
            }

            /// <summary>
            /// Hides every cached leg line NOT drawn this frame (currently active but
            /// <c>lastDrawnFrame != frame</c>) via the pure <see cref="ShouldDeactivateLeg"/> contract.
            /// Returns the count hidden (diagnostic). Only flips currently-active lines, so the steady
            /// state where everything draws is a cheap scan.
            /// </summary>
            private int RunDeactivationSweep(int frame)
            {
                int deactivated = 0;
                foreach (var kvp in polylineCache)
                {
                    var legs = kvp.Value.legs;
                    if (legs == null) continue;
                    for (int i = 0; i < legs.Length; i++)
                    {
                        var line = legs[i].vectorLine;
                        if (line != null &&
                            ShouldDeactivateLeg(line.active, legs[i].lastDrawnFrame, frame))
                        {
                            line.active = false;
                            deactivated++;
                        }
                    }
                }
                return deactivated;
            }

            /// <summary>
            /// Forward-arc analogue of <see cref="RunDeactivationSweep"/> (Step 3 C): hides every cached
            /// forward-arc VectorLine NOT drawn this frame, via the SAME pure <see cref="ShouldDeactivateLeg"/>
            /// contract. A forward arc stops drawing when the window advances past it (icon moved into a new
            /// element), the ghost is gap-held, or the recording is removed; Draw3D is one-shot so the line
            /// must be explicitly deactivated or it lingers.
            /// </summary>
            private int RunForwardArcDeactivationSweep(int frame)
            {
                int deactivated = 0;
                foreach (var kvp in forwardArcCache)
                {
                    var arcs = kvp.Value.arcs;
                    if (arcs == null) continue;
                    for (int i = 0; i < arcs.Length; i++)
                    {
                        var line = arcs[i].vectorLine;
                        if (line != null &&
                            ShouldDeactivateLeg(line.active, arcs[i].lastDrawnFrame, frame))
                        {
                            line.active = false;
                            deactivated++;
                        }
                    }
                }
                return deactivated;
            }

            /// <summary>
            /// Returns the cached <see cref="ParsekTrackingStation"/> for the
            /// current scene, re-resolving via <c>FindObjectOfType</c> only
            /// when null or after a scene change (MINOR-1).
            /// </summary>
            private ParsekTrackingStation ResolveTrackingStationController(GameScenes scene)
            {
                if (cachedControllerScene != scene || cachedTsController == null)
                {
                    cachedTsController = FindObjectOfType<ParsekTrackingStation>();
                    cachedControllerScene = scene;
                }
                return cachedTsController;
            }

            /// <summary>
            /// Returns the cached <see cref="ParsekFlight"/> for the current
            /// scene, re-resolving via <c>FindObjectOfType</c> only when null
            /// or after a scene change (MINOR-1).
            /// </summary>
            private ParsekFlight ResolveFlightController(GameScenes scene)
            {
                if (cachedControllerScene != scene || cachedFlightController == null)
                {
                    cachedFlightController = FindObjectOfType<ParsekFlight>();
                    cachedControllerScene = scene;
                }
                return cachedFlightController;
            }

            /// <summary>
            /// Resolves a CelestialBody by name via a per-scene cached
            /// name->body map (MINOR-2), avoiding the linear
            /// <c>FlightGlobals.Bodies</c> scan per leg per frame. The map is
            /// rebuilt once per scene (bodies are stable within a scene; the
            /// scene-change handler also clears it).
            /// </summary>
            private CelestialBody ResolveBodyByName(GameScenes scene, string name)
            {
                if (string.IsNullOrEmpty(name)) return null;
                EnsureBodyMap(scene);
                return bodyByName.TryGetValue(name, out var body) ? body : null;
            }

            /// <summary>
            /// Rebuilds the per-scene name-&gt;CelestialBody map when it is empty
            /// or stale (scene changed). Bodies are stable within a scene; the
            /// scene-change handler clears the map.
            /// </summary>
            private void EnsureBodyMap(GameScenes scene)
            {
                if (bodyMapScene == scene && bodyByName.Count != 0) return;
                bodyByName.Clear();
                var bodies = FlightGlobals.Bodies;
                if (bodies != null)
                {
                    for (int i = 0; i < bodies.Count; i++)
                    {
                        var b = bodies[i];
                        if (b != null && !string.IsNullOrEmpty(b.name))
                            bodyByName[b.name] = b;
                    }
                }
                bodyMapScene = scene;
            }

            /// <summary>
            /// FIX #27 surface seam (a <see cref="BodySurfaceProvider"/>):
            /// resolves a body's radius from the per-scene body map for the pure
            /// cover-exclusion builder. Returns false for an unknown body so the
            /// builder keeps every segment (byte-identical to the pre-fix path).
            /// Only the radius is needed (CHANGE 2: the exclusion boundary is the
            /// surface, not the atmosphere top).
            /// </summary>
            private bool ResolveBodySurface(string bodyName, out BodySurfaceInfo info)
            {
                info = default(BodySurfaceInfo);
                if (string.IsNullOrEmpty(bodyName)) return false;
                if (!bodyByName.TryGetValue(bodyName, out var body) || body == null)
                    return false;
                info = new BodySurfaceInfo
                {
                    radius = body.Radius
                };
                return true;
            }

            /// <summary>
            /// Per-body gravitational-parameter seam (Step 1): resolves a body's GM from the per-scene body
            /// map for <see cref="ForwardRenderWindow.ComputeForwardWindow"/>'s full-loop period test.
            /// Returns NaN for an unknown body so the period is non-finite and no segment is classified a
            /// full loop (the chain still stops on body-change / end-of-data, per the plan's null-mu note).
            /// </summary>
            private double ResolveBodyMu(string bodyName)
            {
                if (string.IsNullOrEmpty(bodyName)) return double.NaN;
                if (!bodyByName.TryGetValue(bodyName, out var body) || body == null)
                    return double.NaN;
                return body.gravParameter;
            }

            /// <summary>
            /// FORWARD ADDITIVE PASS (Step 3, forward-trajectory-render plan, Option 1). For one recording,
            /// computes the per-ghost forward render window from the re-aimed EFFECTIVE segments and enqueues
            /// the FUTURE legs (B') + FUTURE arcs (C) ahead of the icon, up to the forward stop (first
            /// full-loop closed orbit / first SOI change / end of data). PURELY ADDITIVE: nothing here
            /// touches <c>drewNonOrbitalLegRecordings</c> / <c>anyDrawn</c>, so the current element renders
            /// (and publishes ownership) exactly as today (the SAFEST Step 3 CRITICAL option (a)).
            ///
            /// CRITICAL sourcing: the forward geometry comes from
            /// <see cref="GhostMapPresence.ResolveEffectiveMapOrbitSegments(int,string,List{OrbitSegment},double,GhostPlaybackLogic.LoopUnitSet,out long)"/>
            /// (re-aim-resolved, == recorded by reference for faithful members), NOT raw
            /// <c>rec.OrbitSegments</c>, and the forward-arc cache keys on the re-aim window index so a
            /// synodic-window rollover re-samples.
            ///
            /// GAP-HOLD: when the Director is HOLDING / HIDING this ghost (re-aim trim gap or interior
            /// FlexibleSoi gap), it stamps no fresh seed / TracedPath, so
            /// <c>ShadowRenderDriver.IsDirectorTracking</c> is false for a ghost-bearing recording - draw NO
            /// forward range (matching the Director's own visibility, the plan's gap-hold edge case).
            /// </summary>
            private void DecideForwardWindowForRecording(
                GameScenes scene, int recordingIndex, Recording rec, LegPolylineSet set,
                double headUT, double currentUT, GhostPlaybackLogic.LoopUnitSet loopUnits,
                uint ghostPid, int drawFrame, BodySurfaceProvider surface,
                IReadOnlyList<Recording> committed, HashSet<string> suppressedIds,
                ref int frameForwardLegs, ref int frameForwardArcs, ref int frameForwardSkippedGapHold,
                bool boundaryOverlapSecondary = false)
            {
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) return;

                // CHAIN DEDUPE (playtest-4 chain-boundary fix): the run below is computed over the WHOLE
                // chain, so it must run ONCE per chain per frame. The first non-hidden member the walk
                // reaches runs it - for a looped chain that is exactly the active member (siblings are
                // renderHidden). A later visible member of the same chain (historical non-loop chains,
                // where every member passes the renderHidden gate) skips, which also prevents
                // double-enqueueing the same run legs/arcs.
                //
                // BOUNDARY-OVERLAP SECONDARY (launch->escape seam render fix): the secondary forward pass runs
                // for the SAME chain as the primary but at the EARLY-LAUNCH instance's headUT (a different
                // recorded-span phase), so it must NOT share the primary's chain-dedupe slot - it is a distinct
                // logical run. Skip the primary dedupe set entirely for the secondary; it is called at most once
                // per recording per frame from the walk, so it cannot double-run on its own.
                if (!boundaryOverlapSecondary
                    && rec.IsChainRecording && !chainRunProcessedThisFrame.Add(rec.ChainId))
                    return;

                // GAP-HOLD gate: a ghost-bearing recording the Director is NOT tracking this frame is held
                // / hidden in an interior gap (re-aim trim / FlexibleSoi). Draw no forward range. A pid-0
                // recording (no proto ghost - atmospheric-only ascent) has no Director hide concept; its
                // visibility is already governed by the renderHidden / static-skip gates above, so it falls
                // through. Reusing the same freshness predicate the icon-drive / line patches read keeps the
                // forward pass consistent with the current-element decision.
                //
                // The BOUNDARY-OVERLAP SECONDARY bypasses the gap-hold gate: its overlap-instance map ghost
                // (escape-conic ProtoVessel) is driven by the SEPARATE map-presence loop-shift path
                // (RunOverlapPerInstanceSweep), NOT the Director's shadow render, so IsDirectorTracking is
                // false for it even when it is fully visible. Applying the gate would wrongly suppress the
                // secondary's forward ascent + escape conic every frame. The secondary's visibility is already
                // governed by the DecideBoundaryOverlapSecondaryRender in-window decision at the call site.
                if (!boundaryOverlapSecondary
                    && ghostPid != 0
                    && !Parsek.MapRender.ShadowRenderDriver.IsDirectorTracking(ghostPid, drawFrame))
                {
                    frameForwardSkippedGapHold++;
                    ParsekLog.VerboseRateLimited(DriverTag, "fwd-gaphold." + rec.RecordingId,
                        string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "Forward pass skipped (Director gap-hold): rec={0} pid={1} headUT={2:F1}",
                            rec.RecordingId, ghostPid, headUT),
                        5.0);
                    return;
                }

                // CHAIN-AWARE RUN MEMBERSHIP (playtest-4 fix): a chain splits one logical flight across
                // multiple recordings at handoff seams, so the run window and the run legs/arcs must span
                // the chain - a launch segment with zero OrbitSegments inherits its window from the next
                // member's conics (ascent leg no longer draws alone), and after the handoff the previous
                // member's legs persist as run legs (the ascent leg no longer vanishes). Standalone
                // recordings collect as a single member, byte-identical to the pre-chain behaviour.
                CollectChainRunMembers(committed, rec, recordingIndex, chainRunMemberScratch, loopUnits);

                // Resolve per-member EFFECTIVE segments (CRITICAL sourcing: re-aim-resolved, == recorded
                // by reference for faithful members) + the per-member synodic window index for the arc
                // cache keys, and CONCATENATE into the chain-wide window source. Per-member coalesce
                // first (plan: gaps-between-same-body-segments must not split the forward chain).
                chainRunMemberSegsScratch.Clear();
                chainRunMemberReaimScratch.Clear();
                chainRunConcatScratch.Clear();
                // The chain's recorded data end (max member EndUT): lets the window keep the run
                // alive while the icon rides a TRAILING leg past the last conic (review MAJOR-1)
                // without letting STATIC recordings (headUT = live now, far past the data) paint
                // their full paths.
                double chainDataEndUT = double.NegativeInfinity;
                for (int mi = 0; mi < chainRunMemberScratch.Count; mi++)
                {
                    var member = chainRunMemberScratch[mi];
                    List<OrbitSegment> memberEffective = GhostMapPresence.ResolveEffectiveMapOrbitSegments(
                        member.index, member.rec.RecordingId, member.rec.OrbitSegments, currentUT,
                        loopUnits, out long memberReaimWindow);
                    List<OrbitSegment> memberCoalesced =
                        TrajectoryMath.CoalesceSameOrbitFragments(memberEffective);
                    chainRunMemberSegsScratch.Add(memberCoalesced);
                    chainRunMemberReaimScratch.Add(memberReaimWindow);
                    if (memberCoalesced != null)
                        chainRunConcatScratch.AddRange(memberCoalesced);
                    // Re-aim descent trigger (Clamp B, applied PER-MEMBER): cap a NON-descent transfer/owner
                    // member's EndUT CONTRIBUTION at its SHIFTED parking-conic end (deorbit+captureShift) before
                    // maxing, so the chain data end is clamped regardless of which member DRIVES the run. The
                    // re-aim shifts that member's destination parking conics ~|captureShift| EARLIER (PR #1177);
                    // leaving the window at the UNSHIFTED rec.EndUT lets ComputeForwardWindow's past-end branch
                    // fire once the loiter/descent icon passes the shifted conic and paint the member's own
                    // unshifted recorded approach tail as a spurious "second path". The OLD clamp (below) keyed on
                    // recordingIndex only, so when a DESCENT member drove the chain pass (its Descent window) the
                    // transfer member was a run member but never the recordingIndex and was never clamped - the
                    // 2026-06-21 C4 stray heliocentric line beside the descent. Byte-identical for non-descent
                    // members / non-re-aim chains (ClampChainDataEndForDescentTransfer returns the value unchanged).
                    double memberEndUTClamped = ClampChainDataEndForDescentTransfer(
                        member.rec.EndUT, member.index, loopUnits);
                    if (memberEndUTClamped > chainDataEndUT)
                        chainDataEndUT = memberEndUTClamped;
                }

                // Window source: members are StartUT-ordered and partition the recorded-UT axis, so the
                // concatenation is time-sorted. RE-coalesce the chain-wide list so a same-orbit coast
                // split ACROSS a member boundary (e.g. a full-loop parking orbit entered in one segment
                // and left in the next, where each fragment alone spans < one period) reads as ONE
                // segment for the full-loop stop test. Single member -> its own coalesced list,
                // byte-identical to the pre-chain behaviour.
                List<OrbitSegment> windowSegs = chainRunMemberScratch.Count <= 1
                    ? (chainRunMemberSegsScratch.Count > 0 ? chainRunMemberSegsScratch[0] : null)
                    : TrajectoryMath.CoalesceSameOrbitFragments(chainRunConcatScratch);

                ForwardRenderWindow.ForwardWindow window =
                    ForwardRenderWindow.ComputeForwardWindow(
                        windowSegs, headUT, ResolveBodyMu, chainDataEndUT);
                if (!window.HasForwardRange)
                    return; // icon ON a full-loop closed orbit / no element -> line clears (no run)

                // The render RUN spans [RunStartUT, StopUT]: RunStartUT reaches BACKWARD to the previous
                // boundary (or -inf = trajectory start), so PAST legs/arcs persist on screen as the icon
                // advances, and the whole line resets only at a boundary (ellipse entry / SOI change). The
                // single element the icon currently sits on is still excluded below (drawn by stock / the
                // head-gated pass), keeping the additive pass from double-drawing it.
                double winStart = window.RunStartUT;
                double winStop = window.StopUT;

                // C4 (re-aim descent "two lines next to each other during the loiter"): when the loiter head
                // sits in a gap, ComputeForwardWindow takes the OPEN-element branch and returns StopUT=Infinity
                // (the recorded trajectory continues forward), which the past-end chainDataEndUT clamp above does
                // NOT guard. So the STATIC forward run-leg pass below paints the descent-trigger TRANSFER member's
                // post-conic deorbit-tail legs (its recorded approach down to the seam) as a second line beside
                // the parking conic for the ENTIRE loiter. Cap winStop at the transfer member's SHIFTED conic end
                // PER chain-run member (so the clamp fires regardless of which member drives the run — mirroring
                // the chainDataEndUT per-member clamp), so the forward run never reaches those legs; the I1
                // head-gated primary pass is the only thing that sweeps the deorbit tail in near the trigger.
                // Byte-identical for non-descent members / non-re-aim chains (ClampChainDataEndForDescentTransfer
                // returns winStop unchanged), so an open-element non-descent run keeps StopUT=Infinity.
                for (int mi = 0; mi < chainRunMemberScratch.Count; mi++)
                    winStop = ClampChainDataEndForDescentTransfer(
                        winStop, chainRunMemberScratch[mi].index, loopUnits);

                // Body-fixed run legs hidden this decide pass (playtest-7 rule): only PAST body-fixed
                // legs hide (they would rotate-sweep into the inertial line behind the icon); FUTURE
                // body-fixed legs (the Duna landing descent) draw, connected by the seam bridges.
                int runLegsBodyFixedHidden = 0;

                // SEAM BRIDGE candidates (playtest 7, generalized): every drawn body-fixed-or-future
                // leg of this chain is bridge-eligible on BOTH sides; DecideSeamBridges below finds the
                // adjacent conics and applies the angle + signed-gap gates. The HEAD leg (drawn by the
                // head-gated pass) is a candidate when it is body-fixed (an anchorable head leg is
                // rotated onto the conic seam by the draw-time anchor, so it already connects).
                //
                // DESCENT TRIGGER: this head-leg block reads rec/set DIRECTLY (not chainRunMemberScratch),
                // so a descent member reaching the forward pass as `rec` (it does during its Descent window:
                // primaryRenders is true there) would add its body-fixed descent leg as a bridge candidate
                // here and DecideSeamBridges would bridge INTO/OUT OF it. A descent member must be fully out
                // of the run, so skip the head-leg block entirely for it - its descent line comes only from
                // its own trigger-gated primary pass.
                bridgeLegScratch.Clear();
                bool recIsDescentMember = IsDescentTriggerMember(recordingIndex, loopUnits);
                if (set.legs != null && !recIsDescentMember)
                {
                    for (int li = 0; li < set.legs.Length; li++)
                    {
                        var hl = set.legs[li];
                        if (!ShouldDrawLegAtHeadUT(hl.startUT, hl.endUT, headUT)) continue;
                        if (IsRunLegAnchorCandidate(rec.OrbitSegments, hl.bodyName, hl.startUT, hl.endUT))
                            break;
                        if (!WillLegDraw(hl.PointCount, true)) break;
                        bridgeLegScratch.Add(new BridgeLegCandidate
                        {
                            recordingId = rec.RecordingId,
                            rec = rec,
                            legIndex = li,
                            startUT = hl.startUT,
                            endUT = hl.endUT,
                            bodyName = hl.bodyName,
                        });
                        break;
                    }
                }

                for (int mi = 0; mi < chainRunMemberScratch.Count; mi++)
                {
                    var member = chainRunMemberScratch[mi];

                    // DESCENT TRIGGER (defence in depth): CollectChainRunMembers already excludes descent
                    // members, but a chain=none descent member reaching this pass as `rec` is added by the
                    // non-chain direct-add path (it is not a chain recording, so the chain-member exclusion
                    // loop is never entered for it). Skip it here too so its run LEGS (B'), run ARCS (C), and
                    // its participation in seam-bridge construction (the FUTURE-leg bridge candidates added
                    // below) are ALL suppressed - the descent member is fully out of the forward run, and its
                    // descent line is drawn solely by its own trigger-gated primary pass at the re-anchored
                    // rotation-aligned head. A non-descent member is byte-identical (guard false).
                    if (IsDescentTriggerMember(member.index, loopUnits))
                        continue;

                    // ---- (B') RUN LEGS: any non-orbital leg of this member overlapping the run window,
                    // EXCLUDING the current head leg (drawn by the head-gated pass of the visible member).
                    // Enqueued forward=true so they never flip the ownership signal.
                    LegPolylineSet memberSet = default(LegPolylineSet);
                    bool haveLegs;
                    if (ReferenceEquals(member.rec, rec))
                    {
                        memberSet = set;
                        haveLegs = set.legs != null;
                    }
                    else
                    {
                        // Hidden / sibling member: the walk never vetted it this frame (looped-chain
                        // visibility keeps exactly one member visible), so mirror the walk's
                        // RECORDING-level static gates here (suppression / debris / no-points) and build
                        // its leg cache on demand (content-hash gated, so steady-state cost is a dict hit).
                        if (ClassifyPolylineStaticSkip(member.rec, suppressedIds)
                            != PolylineStaticSkipReason.None)
                            continue; // legs AND arcs of an excluded member stay out of the run
                        RefreshForRecording(member.rec, surface, liveGapSampler);
                        haveLegs = polylineCache.TryGetValue(member.rec.RecordingId, out memberSet)
                            && memberSet.legs != null;
                    }

                    if (haveLegs)
                    {
                        for (int li = 0; li < memberSet.legs.Length; li++)
                        {
                            var leg = memberSet.legs[li];
                            if (!ShouldDrawForwardLeg(leg.startUT, leg.endUT, winStart, winStop, headUT))
                                continue;
                            // Past/future split (playtest-7 revision of the playtest-5 hide): a PAST
                            // body-fixed leg hides when inertial run geometry FOLLOWS it (it would
                            // rotate-sweep into that line behind the icon - the launch ascent case);
                            // a TERMINAL past body-fixed leg (the Duna landing trail, nothing after it
                            // in the run - playtest 11) stays visible, glued to its landing site and
                            // bridged to the orbit behind it. FUTURE legs always draw - anchored when
                            // possible, body-fixed otherwise - connected by the seam bridges.
                            bool anchorCandidate = IsRunLegAnchorCandidate(
                                member.rec.OrbitSegments, leg.bodyName, leg.startUT, leg.endUT);
                            bool isPast = leg.endUT <= headUT;
                            bool keptTerminalPast = false;
                            if (ShouldHideBodyFixedRunLeg(anchorCandidate, leg.endUT, headUT))
                            {
                                bool inertialFollows = false;
                                for (int fmi = 0; fmi < chainRunMemberSegsScratch.Count; fmi++)
                                {
                                    if (AnyAboveSurfaceConicStartsAtOrAfter(
                                            chainRunMemberSegsScratch[fmi], leg.endUT, winStop, surface))
                                    {
                                        inertialFollows = true;
                                        break;
                                    }
                                }
                                if (inertialFollows)
                                {
                                    runLegsBodyFixedHidden++;
                                    continue;
                                }
                                keptTerminalPast = true;
                            }
                            CelestialBody legBody = ResolveBodyByName(scene, leg.bodyName);
                            if (legBody == null) continue;
                            if (!WillLegDraw(leg.PointCount, true)) continue;
                            pendingDraws.Add(new PendingLegDraw
                            {
                                recordingId = member.rec.RecordingId,
                                legIndex = li,
                                body = legBody,
                                rec = member.rec,
                                ownedByTreatment = false, // forward legs always Driver-direct (no proto to own)
                                ghostPid = ghostPid,
                                forward = true,
                                // A kept terminal past leg CANNOT anchor (one-sided by construction) -
                                // it draws body-fixed; other past legs keep the anchor requirement.
                                requireConicAnchor = isPast && anchorCandidate,
                            });
                            frameForwardLegs++;

                            // FUTURE legs and kept TERMINAL past legs are bridge-eligible. Anchored
                            // future legs are harmless to include: the draw-time anchor puts them ON
                            // the conic seam, so their bridge degenerates into the arc's own (clipped)
                            // lead-in/tail.
                            if (!isPast || keptTerminalPast)
                            {
                                bridgeLegScratch.Add(new BridgeLegCandidate
                                {
                                    recordingId = member.rec.RecordingId,
                                    rec = member.rec,
                                    legIndex = li,
                                    startUT = leg.startUT,
                                    endUT = leg.endUT,
                                    bodyName = leg.bodyName,
                                });
                            }
                        }
                    }

                    // ---- (C) RUN ARCS: this member's StockConic segments overlapping the run
                    // (above-surface, EXCLUDING the one the icon sits on - stock draws that). Per-member
                    // selection against the chain-wide window; each member keeps its OWN forwardArcCache
                    // entry keyed on its own selected set + re-aim window. Reuse the per-Driver scratch
                    // buffer (clear-and-fill); arcIndices is consumed synchronously below, before the
                    // next member's selection refills it.
                    List<OrbitSegment> memberCoalesced = chainRunMemberSegsScratch[mi];
                    if (memberCoalesced == null || memberCoalesced.Count == 0) continue;
                    List<int> arcIndices = forwardArcIndexScratch;
                    SelectForwardArcSegmentIndices(
                        memberCoalesced, winStart, winStop, headUT, surface, arcIndices);
                    if (arcIndices.Count > 0)
                    {
                        // Key on the ACTUAL selected segment set (+ re-aim window), NOT on
                        // currentElementIndex: with past arcs included, the same currentElementIndex maps
                        // to different selections as the icon crosses element boundaries within a run
                        // (the excluded current arc changes), so an index-only key would serve a stale
                        // cached arc set. The selected indices fully determine the sampled geometry for a
                        // given re-aim window.
                        string cacheKey = BuildForwardArcKey(arcIndices, chainRunMemberReaimScratch[mi]);
                        // H1: the boundary-overlap secondary forward pass writes/reads under a namespaced key
                        // so it never overwrites the primary's same-recording arcs within one frame. The
                        // PendingForwardArcDraw stores the dictKey so the draw pass reads the right arc set.
                        string dictKey = ForwardArcDictKey(member.rec.RecordingId, boundaryOverlapSecondary);
                        RefreshForwardArcs(
                            scene, member.rec.RecordingId, memberCoalesced, arcIndices, cacheKey, dictKey);

                        if (forwardArcCache.TryGetValue(dictKey, out var fset)
                            && fset.arcs != null)
                        {
                            for (int a = 0; a < fset.arcs.Length; a++)
                            {
                                if (fset.arcs[a].vectorLine == null) continue;
                                pendingForwardArcs.Add(new PendingForwardArcDraw
                                {
                                    recordingId = dictKey,
                                    arcIndex = a,
                                    drawEndIndex = -1,
                                });
                                frameForwardArcs++;
                            }
                        }
                    }
                }

                // SEAM BRIDGES (playtest 7, generalized): for every bridge-eligible leg collected
                // above, find the adjacent conics on both sides and arm a bridge wherever a REAL gap
                // exists (angle in (min, max], previous end BEHIND the next start along the travel
                // direction - the maintainer's overshoot rule).
                int bridgesArmed = DecideSeamBridges(scene, surface, drawFrame);

                ParsekLog.VerboseRateLimited(DriverTag, "fwd-window." + rec.RecordingId,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Render run: rec={0} pid={1} curIdx={2} runStart={3:F1} stopUT={4:F1} reason={5} " +
                        "runLegs+={6} runArcs+={7} bodyFixedHidden={8} members={9} segs={10} headUT={11:F1} " +
                        "bridges+={12} bridgeLegs={13}",
                        rec.RecordingId, ghostPid, window.CurrentIndex, winStart, winStop, window.Reason,
                        frameForwardLegs, frameForwardArcs, runLegsBodyFixedHidden,
                        chainRunMemberScratch.Count,
                        windowSegs != null ? windowSegs.Count : 0, headUT,
                        bridgesArmed, bridgeLegScratch.Count),
                    2.0);
            }

            /// <summary>
            /// SEAM BRIDGE decide pass (playtest 7): for each bridge-eligible leg in
            /// <see cref="bridgeLegScratch"/> (the body-fixed head leg + every FUTURE run leg of the
            /// chain), examines BOTH sides: the conic ENDING at the leg's startUT (prev side) and the
            /// conic STARTING at the leg's endUT (next side), found across all chain members via the
            /// tested <see cref="IsBridgeAdjacentConic"/> rule (below-surface conics excluded - they are
            /// not drawn). Geometry source per side: the matching cached forward arc's inertial samples
            /// when one exists (its pending draw is then CLIPPED at the merge sample so bridge + arc
            /// never double-draw), else on-demand inertial samples from the conic itself
            /// (<see cref="GetBridgeConicSamples"/> - the CURRENT element the icon rides, drawn by
            /// stock, which cannot be clipped; the bridge converges into it). Gates per side: seam
            /// angle in (<see cref="BridgeMinAngleRadians"/>, <see cref="BridgeMaxAngleRadians"/>] and
            /// the signed-gap rule <see cref="IsSeamGapAhead"/> (no bridge when the previous element's
            /// end OVERSHOOTS the next element's start - the maintainer's overshoot rule). Returns the
            /// number of bridges armed.
            /// </summary>
            private int DecideSeamBridges(
                GameScenes scene, BodySurfaceProvider surface, int drawFrame)
            {
                int armed = 0;
                // Flat UT-span projection of the bridge candidates, index-aligned with bridgeLegScratch,
                // for the intervening-continuation-leg rule (launch-escape-seam fix). Built once.
                bridgeLegSpanScratch.Clear();
                for (int ci = 0; ci < bridgeLegScratch.Count; ci++)
                {
                    var c = bridgeLegScratch[ci];
                    bridgeLegSpanScratch.Add(new BridgeLegSpan
                    {
                        startUT = c.startUT,
                        endUT = c.endUT,
                        bodyName = c.bodyName,
                    });
                }
                BridgeLegSpan[] bridgeLegSpans = bridgeLegSpanScratch.ToArray();
                for (int ci = 0; ci < bridgeLegScratch.Count; ci++)
                {
                    var cand = bridgeLegScratch[ci];
                    if (!polylineCache.TryGetValue(cand.recordingId, out var candSet)
                        || candSet.legs == null
                        || cand.legIndex < 0 || cand.legIndex >= candSet.legs.Length)
                        continue;
                    var leg = candSet.legs[cand.legIndex];
                    if (leg.PointCount < 1) continue;
                    CelestialBody body = ResolveBodyByName(scene, cand.bodyName);
                    if (body == null) continue;

                    for (int side = 0; side < 2; side++)
                    {
                        bool atLegStart = side == 0;
                        double seamUT = atLegStart ? cand.startUT : cand.endUT;

                        // 1. Adjacent ABOVE-SURFACE conic across the chain members (closest seam wins).
                        OrbitSegment seg = default(OrbitSegment);
                        string segRecordingId = null;
                        double bestSeamDist = double.PositiveInfinity;
                        for (int mi = 0; mi < chainRunMemberScratch.Count; mi++)
                        {
                            var segs = chainRunMemberSegsScratch[mi];
                            if (segs == null) continue;
                            for (int si = 0; si < segs.Count; si++)
                            {
                                var s = segs[si];
                                if (s.endUT <= s.startUT) continue;
                                if (IsOrbitSegmentBelowSurface(s, surface)) continue;
                                // Full-loop closed orbits are RUN BOUNDARIES (playtest-8 star fix):
                                // they are never drawn by the run, so bridging into one both violates
                                // the stop-before-the-repeating-ellipse rule and (pre-fix) sampled a
                                // multi-revolution segment at arbitrary phases - the star-polygon
                                // artifact around Kerbin. The seam there needs no bridge anyway: the
                                // anchored leg ends exactly ON the ellipse seam.
                                if (ForwardRenderWindow.IsFullLoopClosedOrbit(s, ResolveBodyMu(s.bodyName)))
                                    continue;
                                if (!IsBridgeAdjacentConic(
                                        s.bodyName, s.startUT, s.endUT, cand.bodyName, seamUT,
                                        atLegStart, BridgeMaxSeamGapSeconds))
                                    continue;
                                double dist = atLegStart
                                    ? System.Math.Abs(seamUT - s.endUT)
                                    : System.Math.Abs(s.startUT - seamUT);
                                if (dist < bestSeamDist)
                                {
                                    bestSeamDist = dist;
                                    seg = s;
                                    segRecordingId = chainRunMemberScratch[mi].rec.RecordingId;
                                }
                            }
                        }
                        if (segRecordingId == null) continue;

                        // 1b. Intervening-ascent-leg skip (launch-escape-seam fix): a launch records
                        // across two consecutive body-fixed legs (pad ascent -> continuation ascent)
                        // feeding ONE escape conic. The pad-ascent leg's end is adjacent to that conic
                        // only in UT - the continuation leg sits between them - so a pad-leg->conic bridge
                        // would shortcut OVER the continuation leg (the confirmed ~26.7 deg bug). Only the
                        // body-fixed leg IMMEDIATELY adjacent to the conic (no intervening same-body leg
                        // between this leg's seam and the conic's seam) may bridge to it. The continuation
                        // leg draws its own leg->conic handoff, resolved by the near-meet (5 deg) gate.
                        double conicSeamUT = atLegStart ? seg.endUT : seg.startUT;
                        if (HasInterveningContinuationLeg(
                                bridgeLegSpans, ci, cand.bodyName, seamUT, conicSeamUT, atLegStart,
                                BridgeSeamSharedBoundaryToleranceSeconds,
                                out int interveningLegIdx, out double interveningLegSeamUT))
                        {
                            string interveningRecId = interveningLegIdx >= 0
                                && interveningLegIdx < bridgeLegScratch.Count
                                ? bridgeLegScratch[interveningLegIdx].recordingId : "?";
                            ParsekLog.VerboseRateLimited(DriverTag,
                                "bridge-interveningleg." + cand.recordingId + "." + cand.legIndex,
                                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                    "Seam bridge skipped (intermediate ascent leg - continuation leg {0} " +
                                    "precedes the conic): rec={1} leg={2} side={3} angleDeg=n/a " +
                                    "interveningLegStartUT={4:F1} conicStartUT={5:F1}",
                                    interveningRecId, cand.recordingId, cand.legIndex,
                                    atLegStart ? "start" : "end",
                                    interveningLegSeamUT, conicSeamUT),
                                5.0);
                            continue;
                        }

                        // 2. Conic-side geometry: prefer the matching cached forward arc (clippable);
                        //    fall back to on-demand sampling (the stock-drawn current element).
                        Vector3d seamRel, travelDir;
                        string cachedArcRec = null;
                        int cachedArcIdx = -1;
                        if (forwardArcCache.TryGetValue(segRecordingId, out var fset) && fset.arcs != null)
                        {
                            for (int a = 0; a < fset.arcs.Length; a++)
                            {
                                var arc = fset.arcs[a];
                                if (arc.bodyRelOffsets == null
                                    || arc.bodyRelOffsets.Length <= BridgeMergeSampleCount) continue;
                                if (!string.Equals(arc.bodyName, seg.bodyName, StringComparison.Ordinal))
                                    continue;
                                if (System.Math.Abs(arc.startUT - seg.startUT) > 0.5
                                    || System.Math.Abs(arc.endUT - seg.endUT) > 0.5) continue;
                                cachedArcRec = segRecordingId;
                                cachedArcIdx = a;
                                break;
                            }
                        }
                        if (cachedArcIdx >= 0)
                        {
                            var offs = forwardArcCache[cachedArcRec].arcs[cachedArcIdx].bodyRelOffsets;
                            int last = offs.Length - 1;
                            seamRel = atLegStart ? offs[last] : offs[0];
                            travelDir = atLegStart ? offs[last] - offs[last - 1] : offs[1] - offs[0];
                        }
                        else
                        {
                            var samples = GetBridgeConicSamples(segRecordingId, seg, body, tail: atLegStart);
                            if (samples == null) continue;
                            seamRel = atLegStart
                                ? samples[BridgeMergeSampleCount] : samples[0];
                            travelDir = atLegStart
                                ? samples[BridgeMergeSampleCount] - samples[BridgeMergeSampleCount - 1]
                                : samples[1] - samples[0];
                        }

                        // 3. Leg-side endpoint, body-fixed at the LIVE rotation (metre space - the same
                        //    frame as the conic samples; angles/signs are scale-invariant).
                        int pi = atLegStart ? 0 : leg.PointCount - 1;
                        Vector3d legRel = body.GetWorldSurfacePosition(
                            leg.lats[pi], leg.lons[pi], leg.alts[pi]) - body.position;

                        // 4. Gates: angle window + the signed-gap (overshoot) rule.
                        // Three bands: (a) > max -> no bridge (honest gap); (b) (5deg, max] -> the conic
                        // merge slice (the existing moderate-misalignment smoother); (c) (chord-floor,
                        // 5deg] -> a STRAIGHT CHORD (launch->escape seam render): the merge slice's fixed
                        // ~200-370 km bulge mutes itself below 5deg, but the launch-aligned ascent's
                        // forward-drawn end still lands a few degrees short of the inertial escape conic - a
                        // small but visible gap a zero-bulge chord can close; (d) <= chord-floor -> no
                        // bridge (the leg already meets the conic within a few km - anchored / closed gap -
                        // so a chord would be a degenerate near-zero-length line).
                        double angleRad = SeamBridgeAngleRad(legRel, seamRel);
                        SeamBridgeKind kind = ClassifySeamBridgeByAngle(angleRad);
                        if (kind == SeamBridgeKind.SkipAngleTooLarge)
                        {
                            if (!double.IsInfinity(angleRad))
                                ParsekLog.VerboseRateLimited(DriverTag,
                                    "bridge-angle." + cand.recordingId + "." + cand.legIndex,
                                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                        "Seam bridge skipped (angle too large): rec={0} leg={1} side={2} " +
                                        "angleDeg={3:F1} max={4:F1}",
                                        cand.recordingId, cand.legIndex, atLegStart ? "start" : "end",
                                        angleRad * (180.0 / System.Math.PI),
                                        BridgeMaxAngleRadians * (180.0 / System.Math.PI)),
                                    5.0);
                            continue;
                        }
                        if (kind == SeamBridgeKind.SkipMeetsConic)
                        {
                            // Anchored / exact meet: the leg endpoint already sits on the conic. Drawing
                            // anything here would be a degenerate near-zero-length line.
                            ParsekLog.VerboseRateLimited(DriverTag,
                                "bridge-nearmeet." + cand.recordingId + "." + cand.legIndex,
                                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                    "Seam bridge skipped (leg already meets conic): rec={0} leg={1} " +
                                    "side={2} angleDeg={3:F2} floor={4:F2}",
                                    cand.recordingId, cand.legIndex, atLegStart ? "start" : "end",
                                    angleRad * (180.0 / System.Math.PI),
                                    BridgeChordMinAngleRadians * (180.0 / System.Math.PI)),
                                5.0);
                            continue;
                        }
                        // Chord (small visible gap, zero-bulge straight connector) or the conic merge slice.
                        bool chord = kind == SeamBridgeKind.Chord;
                        bool gapAhead = atLegStart
                            ? IsSeamGapAhead(seamRel, legRel, travelDir)  // prev=conic end, next=leg start
                            : IsSeamGapAhead(legRel, seamRel, travelDir); // prev=leg end, next=conic start
                        if (!gapAhead)
                        {
                            ParsekLog.VerboseRateLimited(DriverTag,
                                "bridge-overshoot." + cand.recordingId + "." + cand.legIndex,
                                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                    "Seam bridge skipped (overshoot): rec={0} leg={1} side={2} " +
                                    "angleDeg={3:F2} - previous end passed the next start, no real gap",
                                    cand.recordingId, cand.legIndex, atLegStart ? "start" : "end",
                                    angleRad * (180.0 / System.Math.PI)),
                                5.0);
                            continue;
                        }

                        // 5. Arm. The cached arc's clip is RESOLVED here (the pending-list index) but
                        // APPLIED only when the bridge actually draws (review MINOR-2): bridges draw
                        // BEFORE arcs in the onPreCull pass, and a failed bridge leaves its arc
                        // unclipped so no one-frame hole opens at the merge sample.
                        // A chord ends exactly where the conic begins (its near endpoint), so the conic
                        // draws in full - no merge-sample clip. Only the merge slice REPLACES the conic's
                        // lead-in and therefore needs the clip.
                        int clipIdx = -1;
                        if (!chord && cachedArcIdx >= 0)
                        {
                            for (int p = 0; p < pendingForwardArcs.Count; p++)
                            {
                                if (pendingForwardArcs[p].arcIndex == cachedArcIdx
                                    && string.Equals(pendingForwardArcs[p].recordingId, cachedArcRec,
                                        StringComparison.Ordinal))
                                {
                                    clipIdx = p;
                                    break;
                                }
                            }
                        }
                        pendingBridges.Add(new PendingBridgeDraw
                        {
                            legRecordingId = cand.recordingId,
                            legIndex = cand.legIndex,
                            atLegStart = atLegStart,
                            arcRecordingId = cachedArcRec,
                            arcIndex = cachedArcIdx,
                            sampleKeyRecordingId = segRecordingId,
                            sampleSegment = seg,
                            clipArcPendingIndex = clipIdx,
                            chord = chord,
                        });
                        pendingBridgeFrame = drawFrame;
                        armed++;
                    }
                }
                return armed;
            }

            /// <summary>
            /// Re-samples the forward-arc VectorLines for one recording ONLY when the cache key
            /// (<see cref="BuildForwardArcKey"/>: selectedSegmentIndices|reaimWindowIndex) changed.
            /// The recorded conic geometry is static, but an element advance / re-aim window rollover swaps
            /// the geometry without changing the segment count, so the key is the load-bearing discriminator.
            /// On a key change every prior arc line is destroyed before the rebuild so no Vectrosity object
            /// leaks. Each selected segment is sampled via the shared <see cref="OrbitArcSampler"/> (the
            /// single copy of the arc math) and converted to ABSOLUTE scaled space via
            /// <c>ScaledSpace.LocalToScaledSpace</c> (the ARC pipeline, GhostOrbitLinePatch.cs:1091 - NOT
            /// the leg's surface-relative path), so the forward arc<->leg seam stays continuous.
            /// </summary>
            private void RefreshForwardArcs(
                GameScenes scene, string recordingId, List<OrbitSegment> segments,
                List<int> arcIndices, string cacheKey, string dictKey)
            {
                if (string.IsNullOrEmpty(recordingId) || segments == null || arcIndices == null)
                    return;

                // dictKey is the forwardArcCache dictionary key (== recordingId for the primary pass; a
                // namespaced key for the boundary-overlap secondary, review H1). recordingId stays the
                // geometry/VectorLine-name/log identity. Defensive: treat a null dictKey as the recording id.
                if (string.IsNullOrEmpty(dictKey)) dictKey = recordingId;

                if (forwardArcCache.TryGetValue(dictKey, out var existing)
                    && string.Equals(existing.cacheKey, cacheKey, StringComparison.Ordinal))
                {
                    // Cache hit: same element / body / re-aim window. Rotating-frame staleness
                    // (playtest 9): at low altitude KSP's world frame CO-ROTATES with the main body,
                    // so offsets captured once freeze against the live frame (arcs/bridges only
                    // updated at cache rebuilds). When Planetarium.InverseRotAngle drifted from the
                    // capture, RESAMPLE the offsets IN PLACE (same segments, same VectorLines - only
                    // the values refresh); in inertial-frame eras the angle does not move and this is
                    // the same cheap early-return as before.
                    double liveRot = Planetarium.InverseRotAngle;
                    if (HasFrameRotationDrift(existing.captureInverseRotAngle, liveRot))
                    {
                        int resampled = 0, faults = 0;
                        for (int k = 0; k < existing.arcs.Length; k++)
                        {
                            if (ResampleForwardArcOffsetsInPlace(scene, ref existing.arcs[k])) resampled++;
                            else faults++;
                        }
                        existing.captureInverseRotAngle = liveRot;
                        forwardArcCache[dictKey] = existing;
                        ParsekLog.VerboseRateLimited(DriverTag, "fwd-arc-rot-resample." + recordingId,
                            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "Forward-arc rotating-frame resample: rec={0} arcs={1} faults={2}",
                                recordingId, resampled, faults),
                            5.0);
                    }
                    return;
                }

                // Key changed: destroy the prior arc lines before rebuilding.
                if (forwardArcCache.TryGetValue(dictKey, out var stale))
                    DestroyForwardArcLines(stale.arcs);

                var arcs = new List<ForwardArc>(arcIndices.Count);
                int sampled = 0;
                int routedToStock = 0;
                int bodyMissing = 0;
                Vector3d[] buffer = forwardArcSampleScratch; // reused; fully consumed into each arc's rel[] below
                for (int k = 0; k < arcIndices.Count; k++)
                {
                    int segIdx = arcIndices[k];
                    if (segIdx < 0 || segIdx >= segments.Count) continue;
                    OrbitSegment seg = segments[segIdx];
                    CelestialBody body = ResolveBodyByName(scene, seg.bodyName);
                    if (body == null) { bodyMissing++; continue; }

                    Orbit orbit;
                    try
                    {
                        orbit = new Orbit(
                            seg.inclination, seg.eccentricity, seg.semiMajorAxis,
                            seg.longitudeOfAscendingNode, seg.argumentOfPeriapsis,
                            seg.meanAnomalyAtEpoch, seg.epoch, body);
                    }
                    catch (Exception)
                    {
                        routedToStock++;
                        continue;
                    }

                    // Forward arcs are STATIC geometry sampled from each segment's own recorded
                    // epoch/elements (frame-independent shape). The loop shift affects only the icon's
                    // drive UT, not the static forward geometry (plan: loop-shifted-ghost edge case), so the
                    // bounds are the segment's own recorded [startUT, endUT].
                    OrbitArcSampler.ArcSampleResult res =
                        OrbitArcSampler.SampleSegmentArc(orbit, seg.startUT, seg.endUT, buffer);
                    if (!res.Sampled)
                    {
                        routedToStock++; // degenerate / parabolic / out-of-validity -> no forward arc
                        continue;
                    }

                    // Capture the BODY-RELATIVE offsets (worldSample - body.position) ONCE. The sampler emits
                    // world positions; subtracting the live body centre leaves the inertial conic's shape
                    // about its body, which is frame-stable. Per-frame the draw re-projects these to scaled
                    // space (strobe-free), so they are NOT baked to absolute scaled space here.
                    Vector3d bodyPos = body.position;
                    int cnt = res.Count;
                    var rel = new Vector3d[cnt];
                    for (int i = 0; i < cnt; i++)
                        rel[i] = buffer[i] - bodyPos;

                    var arc = new ForwardArc
                    {
                        bodyName = seg.bodyName,
                        startUT = seg.startUT,
                        endUT = seg.endUT,
                        bodyRelOffsets = rel,
                        scratchScaledSpace = new Vector3[cnt],
                        // Name on dictKey so the secondary's VectorLines do not collide with the primary's
                        // (same recording id, two coexisting arc sets within one frame, review H1).
                        vectorLine = BuildForwardArcVectorLine(dictKey, sampled, cnt),
                        lastDrawnFrame = -1,
                        lineMode = MapLineUses3D() ? 1 : 2,
                        sourceSegment = seg,
                    };
                    if (arc.vectorLine == null) continue;
                    arcs.Add(arc);
                    sampled++;
                }

                forwardArcCache[dictKey] = new ForwardArcSet
                {
                    arcs = arcs.ToArray(),
                    cacheKey = cacheKey,
                    captureInverseRotAngle = Planetarium.InverseRotAngle,
                };

                ParsekLog.Verbose(DriverTag,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Forward-arc sample: rec={0} key={1} selected={2} sampled={3} routedToStock={4} bodyMissing={5}",
                        recordingId, cacheKey, arcIndices.Count, sampled, routedToStock, bodyMissing));
            }

            /// <summary>
            /// Draws one cached forward arc this frame (Step 3 C): re-projects the cached body-relative
            /// Kepler offsets to scaled space via the SAME strobe-free geometry the leg draw uses
            /// (<c>scaledBody.position + offset * invScale</c> - see the "warp-strobe fix" comment in
            /// <see cref="TryDrawLeg"/>), reactivates the line if a prior sweep hid it, runs <c>Draw3D</c> in
            /// the post-pan slot, and stamps <c>lastDrawnFrame</c>. Re-projecting from the frame-stable
            /// body-relative offsets every frame is strobe-free at all warps and can never go stale (unlike a
            /// once-baked absolute scaled-space arc, which would strobe like the leg's failed approach (a)).
            /// Mutates <paramref name="arc"/> in place (the caller writes it back into the cached array).
            /// Returns true when the arc drew.
            /// </summary>
            private bool DrawForwardArc(
                GameScenes scene, ref ForwardArc arc, int targetLayer, int drawFrame,
                int drawStartIndex = 0, int drawEndIndex = -1)
            {
                var line = arc.vectorLine;
                if (line == null || arc.bodyRelOffsets == null || arc.scratchScaledSpace == null)
                    return false;
                CelestialBody body = ResolveBodyByName(scene, arc.bodyName);
                if (body == null) return false;

                int m = arc.bodyRelOffsets.Length;
                // Bug 1: rebuild the arc line on a map-line mode flip (3D Draw3D <-> 2D Draw at the
                // far-zoom threshold). The arc line is built during sampling (not lazily here), so the
                // rebuild must happen at the draw site. Vectrosity's in-place swap leaves the 2D canvas
                // line non-rendering, so it is recreated like stock. See RebuildLineForMode.
                int arcWantMode = MapLineUses3D() ? 1 : 2;
                if (arc.lineMode != arcWantMode)
                {
                    line = RebuildLineForMode(line, m);
                    arc.vectorLine = line;
                    arc.lineMode = arcWantMode;
                    if (line == null) return false;
                }
                var scaledBody = body.scaledBody;
                Transform scaledXform = scaledBody != null ? scaledBody.transform : null;
                if (scaledXform != null)
                {
                    // Strobe-free: scaled body centre + body-relative inertial offset scaled down. Both terms
                    // share the live frame, so ScaledSpace.totalOffset cancels (never touched).
                    Vector3 bodyCentreScaled = scaledXform.position;
                    double invScale = ScaledSpace.InverseScaleFactor;
                    for (int i = 0; i < m; i++)
                        arc.scratchScaledSpace[i] = bodyCentreScaled + (Vector3)(arc.bodyRelOffsets[i] * invScale);
                }
                else
                {
                    // No scaled body (should not happen in map view): fall back to the absolute conversion
                    // (worldSample = body.position + offset). Strobes under warp, but at least renders.
                    Vector3d bodyPos = body.position;
                    for (int i = 0; i < m; i++)
                        arc.scratchScaledSpace[i] =
                            (Vector3)ScaledSpace.LocalToScaledSpace(bodyPos + arc.bodyRelOffsets[i]);
                }

                CopyLegIntoVectorLine(line, arc.scratchScaledSpace, 0);
                // SEAM BRIDGE clips (playtest 6/7): when this arc is a bridge's NEXT-side target, its
                // draw range starts at the merge sample (the bridge draws the rotation-unwound lead-in
                // instead); when it is a PREV-side source, the range ends at the tail merge sample (the
                // bridge draws the unwound tail). Either way bridge + arc never double-draw. Defaults
                // (0 / -1) draw the full arc.
                line.drawStart = drawStartIndex > 0 && drawStartIndex < m - 1 ? drawStartIndex : 0;
                line.drawEnd = drawEndIndex > line.drawStart && drawEndIndex < m ? drawEndIndex : m - 1;

                // Layer 31 always; world-transform zeroing only in 3D mode (2D canvas children must keep
                // their canvas placement or they vanish on zoom-out - Bug 1). See PrepareMapLineTransform.
                PrepareMapLineTransform(line, targetLayer);
                if (!line.active) line.active = true;
                // Bug 1 fix: draw in the stock map-line mode (Draw3D / 2D Draw past the far-zoom
                // threshold) so the forward arc never vanishes on zoom-out. See DrawMapLine.
                DrawMapLine(line);
                arc.lastDrawnFrame = drawFrame;
                return true;
            }
        }
    }
}
