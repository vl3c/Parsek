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
    internal static partial class GhostTrajectoryPolylineRenderer
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

            /// <summary>
            /// Body gravitational parameter (GM, m^3/s^2). Zero / NaN = unknown, which is
            /// what an older test provider that only fills <see cref="radius"/> leaves here.
            /// Only the PREDICTED-tail re-admission
            /// (<see cref="IsPredictedTailOrbitOwnedConic"/>) reads it, and it fails closed
            /// on an unknown value, so a provider that does not fill it keeps the pre-2026-09-08
            /// behaviour exactly.
            /// </summary>
            public double gravParameter;
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

        // Reusable scratch buffer for OrbitArcSampler.SampleSegmentArc output (body-LOCAL points). The
        // sampler fills it transiently and RefreshForwardArcs copies the body-relative offsets out into each
        // arc's own array before the next sample, so a single shared buffer is safe (the map render loop is
        // single-threaded; RefreshForwardArcs runs sequentially per recording). Avoids a per-cache-miss
        // 180-element allocation on the multi-ghost map walk.
        private static readonly Vector3d[] forwardArcSampleScratch = new Vector3d[ForwardArcSampleCount];

        private static readonly Dictionary<string, BridgeLineEntry> bridgeLineByRecording =
            new Dictionary<string, BridgeLineEntry>(StringComparer.Ordinal);

        private static readonly Dictionary<string, BridgeConicSamples> bridgeConicSampleCache =
            new Dictionary<string, BridgeConicSamples>(StringComparer.Ordinal);

        /// <summary>Cap for <see cref="bridgeConicSampleCache"/>; on overflow the whole cache is
        /// dropped (entries are cheap to resample).</summary>
        private const int BridgeConicSampleCacheCap = 32;

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
        /// Did the Driver's ownership/paint PUBLISH SURFACE actually run on <paramref name="frame"/>?
        /// I.e. did the decide walk reach its epilogue - past the TRACKSTATION/FLIGHT scene gate, past
        /// <c>MapView.MapIsEnabled</c>, and past the controller-not-yet-awake defers - so that
        /// <see cref="drewNonOrbitalLegRecordings"/> and
        /// <c>RenderCompositionRecorder.NoteOwnershipPublish</c> both reflect THIS frame?
        ///
        /// <para>NOT a new per-frame signal: it reads the EXISTING <c>pendingDrawsFrame</c> stamp, which
        /// the walk writes in its epilogue, after every early return, precisely so
        /// <c>OnMapCameraPreCull</c> can ask "did this LateUpdate run to completion?" - the identical
        /// question, one slot later in the same frame.</para>
        ///
        /// <para>ORDERING, stated exactly because it is easy to get backwards: the stamp is written
        /// BEFORE <c>RenderCompositionRecorder.NoteOwnershipPublish</c> (~50 lines above it), with no
        /// early return in between. That does not weaken the reuse, because THE PROBE'S ACTUAL INPUT -
        /// <see cref="drewNonOrbitalLegRecordings"/> - is populated DURING the per-recording walk, i.e.
        /// before the stamp. The recorder publish below it is the M-A7
        /// manifest's own diff, which the probe never reads. A true stamp therefore means the walk got
        /// past the gates and populated the sets this frame, which is exactly the question asked.</para>
        ///
        /// <para>Its consumer is the map-render probe's <c>line-blink</c> TracedPath-handoff exemption
        /// (<c>MapRenderTrace.ResolveTracedPathHandoffExempt</c>), which needs to distinguish "the
        /// polyline covered nothing" from "nobody asked it to": in a map-CLOSED flight lane the coverage
        /// bits are false because this surface never ran, not because the map went dark. FALSE when no
        /// Driver exists - truthfully, since a Driver that does not exist published nothing; that case
        /// is unreachable from the probe in practice, the Driver being an <c>Instantly</c>/<c>once</c>
        /// DDOL singleton created long before any ghost map vessel does.</para>
        ///
        /// <para>A FALSE here is an ABSENCE, never a proof that nothing was on screen: the walk also
        /// misses its epilogue on the controller-not-yet-awake defers and on any exception escaping the
        /// walk body, both reachable with the map OPEN. The consumer therefore measures map-closedness
        /// separately and positively rather than inferring it from this bit.</para>
        /// </summary>
        internal static bool DidOwnershipPublishRunOnFrame(int frame)
        {
            Driver d = Driver.Instance;
            return d != null && d.PendingDrawsFrame == frame;
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
        /// Test-only counter of actual polyline (re)builds that reach the build
        /// log site. Lets a cache-invalidation test assert a rebuild happened
        /// without depending on the now rate-limited build log line.
        /// </summary>
        internal static int BuildInvocationCountForTesting;

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
            // Test-only rebuild counter shares the cross-save / test-reset lifecycle of the caches below.
            BuildInvocationCountForTesting = 0;
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

        /// <summary>Minimum span (seconds) of a predicted TAIL conic worth sampling into a leg.</summary>
        internal const double PredictedTailFillMinSeconds = 1.0;

        /// <summary>Max synthetic points sampled per predicted TAIL conic (endpoints included).</summary>
        internal const int PredictedTailFillMaxPointsPerSegment = 48;

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
            // GUI-P1: the player's per-recording playback tick box is OFF, so this
            // recording draws nothing anywhere - the polyline included. The polyline is a
            // separate draw authority from the ProtoVessel, so retiring the proto does not
            // retire the line: it needs its own arm of the same gate.
            PlaybackDisabled,
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
            if (GhostMapPresence.IsMapPresenceHiddenByPlaybackToggle(rec))
                return PolylineStaticSkipReason.PlaybackDisabled;
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

            /// <summary>READ-ONLY view of the walk-completed stamp for
            /// <see cref="DidOwnershipPublishRunOnFrame"/> (the enclosing type cannot reach a nested
            /// type's private field). Same question <c>OnMapCameraPreCull</c> asks one slot later:
            /// did THIS frame's LateUpdate reach its epilogue, where the ownership publish lives?
            /// No setter - the stamp stays single-writer inside the walk.</summary>
            internal int PendingDrawsFrame => pendingDrawsFrame;

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
            /// rebuilds the committed store; a same-RecordingId
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

                // S9 game-mode gate: no ghost polylines in a mission / scenario game.
                if (ParsekGameModeGate.IsInertForCurrentGame) return;

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
                        // GUI-P1: one line per recording when the playback tick box starts
                        // hiding its line, not once per frame (the frame aggregate stays in
                        // frameSkippedStatic).
                        if (staticSkip == PolylineStaticSkipReason.PlaybackDisabled)
                            GhostMapPresence.LogMapPresencePlaybackSuppressedOnChange(
                                "polyline", rec.RecordingId, rec.VesselName);
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
                            // PAINTED set (V1-REPLAY-LINE-BLINK): a current-element leg drawing is also a
                            // paint. See the forward-pass note below for why the PAINTED set is broader
                            // than the OWNERSHIP set and what reads it.
                            GhostMapPresence.NotePaintedRecordingLine(rec.RecordingId);
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
                    // PAINTED-vs-OWNING (V1-REPLAY-LINE-BLINK): the forward passes below publish the
                    // per-recording PAINTED bit themselves, keyed on the MEMBER each leg / arc belongs to
                    // (see the enqueue sites in DecideForwardWindowForRecording). That bit is DELIBERATELY
                    // broader than drewNonOrbitalLegRecordings: ownership answers "is the HEAD inside a
                    // drawn CURRENT leg" - the right question for hiding the proto conic, and left exactly
                    // as it is - while the run legs + forward arcs this pass enqueues keep the ghost's
                    // trajectory on screen even when the head sits BETWEEN legs
                    // (ride=fallback-head-outside-legs). The map-render probe's line-blink guard needs the
                    // broader question, "did the map actually go dark for this ghost", and reading
                    // ownership for it raised on inter-leg gaps where 4 run legs + 2 arcs were painted the
                    // whole time (measured, run 2026-08-01_1551).
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

                // M-A7 render-composition OWNERSHIP capture (capture point 3, truth half): the
                // actual-draw publish set. The recorder keeps its OWN previous set and runs the same
                // pure DiffDrawnSets, so it never shares this tracing-gated diff's state. Instant
                // no-op when the manifest env gate is unarmed - the recorder reads the UT itself,
                // BEHIND that gate, so the unarmed path pays no Unity call for this line.
                Parsek.MapRender.RenderCompositionRecorder.NoteOwnershipPublish(
                    drewNonOrbitalLegRecordings);
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
            // internal (not private) so the M-A7 render-composition manifest can export it in the
            // CONSTANTS header block; the Python verifier's ratified table pins the value.
            internal const double TangentSeamConicSampleDtSeconds = 1.0;

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

                // M-A7 render-composition SEAM capture: record EVERY evaluation (not just the
                // once-per-onset discontinuous ones) - RC-QUAL's trend record needs the PASSING angles
                // too. Instant no-op when the manifest env gate is unarmed.
                Parsek.MapRender.RenderCompositionRecorder.NoteSeamTangent(
                    p.ghostPid, p.recordingId, p.legIndex, Planetarium.GetUniversalTime(),
                    continuous, angleRad,
                    Parsek.MapRender.CrossMemberSeamStitcher.TangentToleranceRadians);

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
                entry.legRecordingId = bridge.legRecordingId;
                entry.legIndex = bridge.legIndex;
                entry.atLegStart = bridge.atLegStart;
                entry.bodyName = bodyName;

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
                    radius = body.Radius,
                    // PREDICTED-TAIL: the span-above-surface test needs GM to propagate the
                    // conic's mean anomaly to its own endpoints. Unset (a provider that fills
                    // only the radius) fails that test closed, keeping the pre-fix behaviour.
                    gravParameter = body.gravParameter
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
                            // PAINTED publish (V1-REPLAY-LINE-BLINK), keyed on the MEMBER whose leg this
                            // is - not on the recording that happens to be driving the chain run. The run
                            // spans every chain member, so crediting the driver would both over-credit it
                            // with siblings' geometry and leave the siblings unmarked. See the set's doc
                            // comment in GhostMapPresence for what this signal does and does not promise.
                            if (MapRenderTrace.IsEnabled)
                                GhostMapPresence.NotePaintedRecordingLine(member.rec.RecordingId);

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
                                // PAINTED publish, keyed on the MEMBER (dictKey is namespaced for the
                                // boundary-overlap secondary, so it is NOT a bare recording id).
                                if (MapRenderTrace.IsEnabled)
                                    GhostMapPresence.NotePaintedRecordingLine(member.rec.RecordingId);
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
                                if (IsSegmentExcludedFromOrbitOwnership(s, surface)) continue;
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
