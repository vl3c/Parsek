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
        /// <c>IsDirectorTracedPathActive</c> (8e S3a.1). Populated only on an ACTUAL draw (preserving
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

        // RecordingId -> per-recording FORWARD-ARC line set (Step 3 C, forward-trajectory-render plan).
        // Holds the sampled Vectrosity VectorLines for the orbit (StockConic) segments AHEAD of the icon up
        // to forwardStopUT, plus the cache key (currentElementIndex|bodyName|reaimWindowIndex) so a window
        // rollover / element advance re-samples. SEPARATE from polylineCache so the forward additive pass is
        // fully decoupled from the current-element leg draw + its ownership publish (the SAFEST ownership
        // approach in the plan's Step 3 CRITICAL bullet: forward draws NEVER touch
        // drewNonOrbitalLegRecordings). One VectorLine per forward segment.
        private static readonly Dictionary<string, ForwardArcSet> forwardArcCache =
            new Dictionary<string, ForwardArcSet>(StringComparer.Ordinal);

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
        /// Test-only accessor: returns the live cache dictionary so the
        /// xUnit suite can assert on cache contents after a refresh.
        /// </summary>
        internal static IReadOnlyDictionary<string, LegPolylineSet> CacheForTesting => polylineCache;

        /// <summary>
        /// Test-only accessor: returns the number of cached entries.
        /// </summary>
        internal static int CacheCountForTesting => polylineCache.Count;

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

            /// <summary>The (currentElementIndex|bodyName|reaimWindowIndex) key the arcs were sampled for.</summary>
            public string cacheKey;
        }

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
            Recording rec, BodySurfaceProvider surface = null)
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

            var legs = BuildLegsForRecording(rec, surface);
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
            // the legs so a reused RecordingId never inherits a stale wrong-aimed forward arc.
            if (forwardArcCache.TryGetValue(recordingId, out var fwd))
                DestroyForwardArcLines(fwd.arcs);
            forwardArcCache.Remove(recordingId);
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
            // Drop the marker hold cache on the same cross-save / test-reset lifecycle as the ownership
            // sets so a stale held on-line point never survives a save load or a scene switch.
            lastGoodOnLine.Clear();
            // Forward-arc cache (Step 3 C): destroy every arc's VectorLine + drop the dict on the same
            // cross-save / test-reset lifecycle as the leg cache.
            int fwdDropped = forwardArcCache.Count;
            foreach (var kvp in forwardArcCache)
                DestroyForwardArcLines(kvp.Value.arcs);
            forwardArcCache.Clear();
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
        /// Forward-arc cache key (Step 3 Cache bullet): a forward-arc VectorLine set is re-sampled only
        /// when the element the icon sits on, its body, or the re-aim synodic WINDOW changes. The recorded
        /// segment geometry is static, but a re-aim window rollover swaps the EFFECTIVE geometry without
        /// changing the recorded segment count, so <paramref name="reaimWindowIndex"/> (the
        /// <c>ResolveEffectiveMapOrbitSegments</c> out window index, also the
        /// <c>ShadowRenderDriver.BuildChainSignature</c> discriminator) MUST be part of the key or a stale
        /// wrong-aimed forward arc would survive the rollover. Pure; xUnit-testable.
        /// </summary>
        internal static string BuildForwardArcKey(
            int currentElementIndex, string bodyName, long reaimWindowIndex)
            => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}|{1}|{2}", currentElementIndex, bodyName ?? "?", reaimWindowIndex);

        /// <summary>
        /// Phase 8b.1 no-double-draw routing predicate (Driver side): should this recording's current
        /// non-orbital leg be drawn by the OWNED <c>TracedPathTreatment</c> instead of the Driver's
        /// direct <c>TryDrawLeg</c> call? True exactly when the Director owns the ghost's active leg as a
        /// fresh TracedPath this frame (<paramref name="directorOwnsTracedPath"/> =
        /// <c>ShadowRenderDriver.IsDirectorTracedPathActive(pid, frame)</c>). The Driver routes through
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
                    // TRANSIENT dropout (the dominant active-pan case): the leg matched the head but was
                    // not drawn this frame, so its scratch is stale. Instead of snapping to the body-fixed
                    // head, hold the last on-line position if it is still fresh + near in UT. This keeps the
                    // marker glued to the smoothly-redrawn line during a pan; a sustained stall expires the
                    // hold (frame-age / UT bound) and falls through.
                    if (TryHoldLastGood(recordingId, headUT, frame, out worldPos, out legIndex))
                    {
                        rideReason = MapRenderTrace.MarkerRideReason.HeldLastGood;
                        return true;
                    }
                    rideReason = MapRenderTrace.MarkerRideReason.FallbackLegNotDrawnThisFrame;
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
        internal static List<LegPolyline> BuildLegsForRecording(
            Recording rec, BodySurfaceProvider surface = null)
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
                    "Polyline build: rec={0} legs={1} (sectionPts={2} flatPts={3} skippedRelNoBodyFixed={4} excludedBelowSurfaceSegs={5})",
                    rec.RecordingId,
                    legs.Count, sectionPointCount, flatPointCount,
                    skippedRelativeWithoutBodyFixed, excludedBelowSurfaceSegments));

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
            int targetLayer, int drawFrame, string recordingId, int legIndex)
        {
            int m = leg.PointCount;
            if (m < 2) return false;

            // Inflate this leg's own VectorLine lazily. One line PER
            // leg: a single shared line drawn once per leg via
            // drawStart/drawEnd range slicing does NOT work, because
            // VectorLine.Draw3D() zeroes every vertex outside the
            // current window on each call, leaving only the last leg.
            if (leg.vectorLine == null)
                leg.vectorLine = BuildLegVectorLine(recordingId, legIndex, m);
            if (leg.vectorLine == null) return false;

            leg.vectorLine.rectTransform.gameObject.layer = targetLayer;

            // Stable scaled-space placement: zero the line's transform
            // every frame (matches OrbitRendererBase's REDRAW path,
            // which sets OrbitLine.rectTransform.position = zero before
            // each redraw). The points3 we feed are ABSOLUTE ScaledSpace
            // positions, so the line's GameObject transform must be the
            // identity; otherwise the mesh inherits its parent/canvas
            // transform and visibly drifts as the map camera pans
            // (it is not anchored in space). Position is the load-bearing
            // reset; rotation/scale are pinned defensively.
            var lineXform = leg.vectorLine.rectTransform;
            lineXform.position = Vector3.zero;
            lineXform.rotation = Quaternion.identity;
            lineXform.localScale = Vector3.one;

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
            if (scaledXform != null)
            {
                Vector3 bodyCentreScaled = scaledXform.position;
                double invScale = ScaledSpace.InverseScaleFactor;
                Vector3d bodyPos = body.position;
                for (int i = 0; i < m; i++)
                {
                    Vector3d world = body.GetWorldSurfacePosition(
                        leg.lats[i], leg.lons[i], leg.alts[i]);
                    leg.scratchScaledSpace[i] = bodyCentreScaled
                        + (Vector3)((world - bodyPos) * invScale);
                }
            }
            else
            {
                // No scaled body available (should not happen in map view): fall back to the
                // direct absolute path. Strobes under warp, but at least renders on the surface.
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
            TryAnchorLegToConicSeam(rec, leg, body, scaledXform);

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
            leg.vectorLine.Draw3D();
            leg.lastDrawnFrame = drawFrame;
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
            if (pointCount <= 0) return null;
            var points = new List<Vector3>(pointCount);
            for (int i = 0; i < pointCount; i++)
                points.Add(Vector3.zero);
            var line = new VectorLine(
                "ParsekGhostTrajectoryPolyline-" + recordingId + "-leg" + legIndex,
                points,
                5f,
                LineType.Continuous);
            ApplyStockOrbitLineStyle(line);
            return line;
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
        /// Constructs a fresh forward-arc <c>LineType.Continuous</c> VectorLine sized to hold exactly the
        /// arc's sample count (Step 3 C). One line PER forward segment (same Draw3D drawStart/drawEnd
        /// constraint as legs). Identical orbit-line style to the legs via
        /// <see cref="ApplyStockOrbitLineStyle"/> so the forward arc reads as one continuous line with the
        /// current stock arc and the legs.
        /// </summary>
        internal static VectorLine BuildForwardArcVectorLine(
            string recordingId, int arcIndex, int pointCount)
        {
            if (pointCount <= 0) return null;
            var points = new List<Vector3>(pointCount);
            for (int i = 0; i < pointCount; i++)
                points.Add(Vector3.zero);
            var line = new VectorLine(
                "ParsekGhostForwardArc-" + recordingId + "-arc" + arcIndex,
                points,
                5f,
                LineType.Continuous);
            ApplyStockOrbitLineStyle(line);
            return line;
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
            }
            private readonly List<PendingForwardArcDraw> pendingForwardArcs =
                new List<PendingForwardArcDraw>();

            // Per-Driver reusable scratch buffer for the forward-arc index selection (forward-render review
            // finding): SelectForwardArcSegmentIndices runs once per leg-bearing recording per frame on the
            // multi-ghost path, so the fill-into-buffer overload clears + refills this single list instead of
            // allocating a fresh List<int> each call. Single-threaded per Driver instance (LateUpdate decide
            // pass), so one shared scratch is safe; consumed immediately within DecideForwardWindowForRecording.
            private readonly List<int> forwardArcIndexScratch = new List<int>();

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
                    double headUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                        recordingIndex,
                        rec.StartUT,
                        rec.EndUT,
                        currentUT,
                        loopUnits,
                        out bool renderHidden);
                    if (renderHidden)
                    {
                        frameSkippedHidden++;
                        continue;
                    }

                    RefreshForRecording(rec, surface);
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
                    // pid-keyed (ShadowRenderDriver.IsDirectorTracedPathActive), the SAME predicate the
                    // icon-drive / orbit-line patches read to suppress the stock proto. Resolving here,
                    // outside the leg loop, keeps the per-leg routing cheap. pid 0 (no ghost) is never
                    // stamped by the shadow, so those recordings always take the Driver-direct path.
                    uint ghostPid = GhostMapPresence.GetGhostVesselPidForRecording(recordingIndex);
                    bool directorOwnsTracedPath =
                        Parsek.MapRender.ShadowRenderDriver.IsDirectorTracedPathActive(ghostPid, drawFrame);

                    bool anyDrawn = false;
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
                        if (!ShouldDrawLegAtHeadUT(leg.startUT, leg.endUT, headUT))
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

                        // Phase 8b routing decision (unchanged): when the Director owns this ghost's CURRENT
                        // leg as a TracedPath (directorOwnsTracedPath, the pid-keyed
                        // IsDirectorTracedPathActive), the OWNED TracedPathTreatment path is the structural
                        // owner of the leg; otherwise the Driver-direct path draws. Gate off / no fresh
                        // TracedPath intent -> directorOwnsTracedPath is false -> Driver-direct, byte-identical.
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
                            ghostPid = ghostPid
                        });
                        anyDrawn = true;
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
                    DecideForwardWindowForRecording(
                        scene, recordingIndex, rec, set, headUT, currentUT, loopUnits,
                        ghostPid, drawFrame, surface,
                        ref frameForwardLegs, ref frameForwardArcs, ref frameForwardSkippedGapHold);
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

                int drawn = 0;
                for (int i = 0; i < pendingDraws.Count; i++)
                {
                    var p = pendingDraws[i];
                    if (string.IsNullOrEmpty(p.recordingId)) continue;
                    if (!polylineCache.TryGetValue(p.recordingId, out var set) || set.legs == null) continue;
                    if (p.legIndex < 0 || p.legIndex >= set.legs.Length) continue;
                    var leg = set.legs[p.legIndex];
                    bool legDrawn = p.ownedByTreatment
                        ? Parsek.MapRender.TracedPathTreatment.TryDrawOwnedLeg(
                            ref leg, p.rec, p.body, pendingTargetLayer, frame, p.recordingId, p.legIndex, p.ghostPid)
                        : TryDrawLeg(
                            ref leg, p.rec, p.body, pendingTargetLayer, frame, p.recordingId, p.legIndex);
                    // Persist the lazily-inflated line + the lastDrawnFrame stamp back into the cached
                    // array (set.legs is the same array reference the dict holds).
                    set.legs[p.legIndex] = leg;
                    if (legDrawn) drawn++;

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
                                leg.startUT, leg.endUT));
                }
                pendingDraws.Clear();

                // FORWARD ARCS (Step 3 C): draw the future orbit arcs decided this frame in the same
                // post-pan slot as the legs. The arc geometry is already sampled (cache-key-gated in the
                // decide pass), so this just reactivates + Draw3D's the cached line. Forward arcs are a
                // separate additive surface - they never touch drewNonOrbitalLegRecordings, so the current
                // arc + icon (stock OrbitRenderer) render exactly as today.
                int fwdArcsDrawn = 0;
                GameScenes fwdScene = HighLogic.LoadedScene;
                for (int i = 0; i < pendingForwardArcs.Count; i++)
                {
                    var fp = pendingForwardArcs[i];
                    if (string.IsNullOrEmpty(fp.recordingId)) continue;
                    if (!forwardArcCache.TryGetValue(fp.recordingId, out var fset) || fset.arcs == null)
                        continue;
                    if (fp.arcIndex < 0 || fp.arcIndex >= fset.arcs.Length) continue;
                    var arc = fset.arcs[fp.arcIndex];
                    if (DrawForwardArc(fwdScene, ref arc, pendingTargetLayer, frame))
                        fwdArcsDrawn++;
                    fset.arcs[fp.arcIndex] = arc; // persist the lastDrawnFrame stamp (same array ref)

                    if (arc.vectorLine != null && arc.lastDrawnFrame == frame && MapRenderTrace.IsEnabled)
                        MapRenderTrace.EmitMarker(
                            MapRenderTrace.RenderSurface.Polyline, fp.recordingId,
                            Planetarium.GetUniversalTime(),
                            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "FWD-ARC scene={0} arc={1} body={2} startUT={3:F1} endUT={4:F1} layer={5}",
                                HighLogic.LoadedScene, fp.arcIndex,
                                string.IsNullOrEmpty(arc.bodyName) ? "<none>" : arc.bodyName,
                                arc.startUT, arc.endUT, pendingTargetLayer));
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
                RunForwardArcDeactivationSweep(frame);
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
                ref int frameForwardLegs, ref int frameForwardArcs, ref int frameForwardSkippedGapHold)
            {
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) return;

                // GAP-HOLD gate: a ghost-bearing recording the Director is NOT tracking this frame is held
                // / hidden in an interior gap (re-aim trim / FlexibleSoi). Draw no forward range. A pid-0
                // recording (no proto ghost - atmospheric-only ascent) has no Director hide concept; its
                // visibility is already governed by the renderHidden / static-skip gates above, so it falls
                // through. Reusing the same freshness predicate the icon-drive / line patches read keeps the
                // forward pass consistent with the current-element decision.
                if (ghostPid != 0
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

                // CRITICAL sourcing: re-aimed EFFECTIVE segments (== recorded by reference for faithful
                // members) + the synodic window index for the forward-arc cache key.
                List<OrbitSegment> effective = GhostMapPresence.ResolveEffectiveMapOrbitSegments(
                    recordingIndex, rec.RecordingId, rec.OrbitSegments, currentUT, loopUnits,
                    out long reaimWindowIndex);

                // Reuse CoalesceSameOrbitFragments so a fragmented same-body coast (recorder mode
                // transition) does not split the forward chain (plan: gaps-between-same-body-segments).
                List<OrbitSegment> coalesced = TrajectoryMath.CoalesceSameOrbitFragments(effective);

                ForwardRenderWindow.ForwardWindow window =
                    ForwardRenderWindow.ComputeForwardWindow(coalesced, headUT, ResolveBodyMu);
                if (!window.HasForwardRange)
                    return; // icon on a full-loop closed orbit / no element ahead -> nothing forward to draw

                double winStart = window.CurrentElementStartUT;
                double winStop = window.StopUT;

                // ---- (B') FUTURE LEGS: any non-orbital leg overlapping the forward window, EXCLUDING the
                // current head leg (drawn by the head-gated pass). Enqueued forward=true so they never flip
                // the ownership signal.
                if (set.legs != null)
                {
                    for (int li = 0; li < set.legs.Length; li++)
                    {
                        var leg = set.legs[li];
                        if (!ShouldDrawForwardLeg(leg.startUT, leg.endUT, winStart, winStop, headUT))
                            continue;
                        CelestialBody legBody = ResolveBodyByName(scene, leg.bodyName);
                        if (legBody == null) continue;
                        if (!WillLegDraw(leg.PointCount, true)) continue;
                        pendingDraws.Add(new PendingLegDraw
                        {
                            recordingId = rec.RecordingId,
                            legIndex = li,
                            body = legBody,
                            rec = rec,
                            ownedByTreatment = false, // forward legs always Driver-direct (no proto to own)
                            ghostPid = ghostPid,
                            forward = true,
                        });
                        frameForwardLegs++;
                    }
                }

                // ---- (C) FUTURE ARCS: forward StockConic segments (above-surface, not the current arc).
                // Reuse the per-Driver scratch buffer (clear-and-fill) to avoid a per-frame List<int> alloc
                // on the hot multi-ghost path. arcIndices is consumed synchronously below (RefreshForwardArcs
                // copies the geometry into the cache before the next recording's decide pass refills it).
                List<int> arcIndices = forwardArcIndexScratch;
                SelectForwardArcSegmentIndices(
                    coalesced, winStart, winStop, headUT, surface, arcIndices);
                if (arcIndices.Count > 0)
                {
                    string cacheKey = BuildForwardArcKey(
                        window.CurrentIndex,
                        window.CurrentIndex >= 0 && window.CurrentIndex < coalesced.Count
                            ? coalesced[window.CurrentIndex].bodyName : null,
                        reaimWindowIndex);
                    RefreshForwardArcs(scene, rec.RecordingId, coalesced, arcIndices, cacheKey);

                    if (forwardArcCache.TryGetValue(rec.RecordingId, out var fset) && fset.arcs != null)
                    {
                        for (int a = 0; a < fset.arcs.Length; a++)
                        {
                            if (fset.arcs[a].vectorLine == null) continue;
                            pendingForwardArcs.Add(new PendingForwardArcDraw
                            {
                                recordingId = rec.RecordingId,
                                arcIndex = a,
                            });
                            frameForwardArcs++;
                        }
                    }
                }

                ParsekLog.VerboseRateLimited(DriverTag, "fwd-window." + rec.RecordingId,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Forward window: rec={0} pid={1} curIdx={2} winStart={3:F1} stopUT={4:F1} reason={5} " +
                        "fwdLegs+={6} fwdArcs+={7} reaimWindow={8} segs={9}",
                        rec.RecordingId, ghostPid, window.CurrentIndex, winStart, winStop, window.Reason,
                        frameForwardLegs, frameForwardArcs, reaimWindowIndex,
                        coalesced != null ? coalesced.Count : 0),
                    2.0);
            }

            /// <summary>
            /// Re-samples the forward-arc VectorLines for one recording ONLY when the cache key
            /// (<see cref="BuildForwardArcKey"/>: currentElementIndex|bodyName|reaimWindowIndex) changed.
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
                List<int> arcIndices, string cacheKey)
            {
                if (string.IsNullOrEmpty(recordingId) || segments == null || arcIndices == null)
                    return;

                if (forwardArcCache.TryGetValue(recordingId, out var existing)
                    && string.Equals(existing.cacheKey, cacheKey, StringComparison.Ordinal))
                {
                    return; // cache hit: same element / body / re-aim window -> reuse the sampled arcs
                }

                // Key changed: destroy the prior arc lines before rebuilding.
                if (forwardArcCache.TryGetValue(recordingId, out var stale))
                    DestroyForwardArcLines(stale.arcs);

                var arcs = new List<ForwardArc>(arcIndices.Count);
                int sampled = 0;
                int routedToStock = 0;
                int bodyMissing = 0;
                var buffer = new Vector3d[ForwardArcSampleCount];
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
                        vectorLine = BuildForwardArcVectorLine(recordingId, sampled, cnt),
                        lastDrawnFrame = -1,
                    };
                    if (arc.vectorLine == null) continue;
                    arcs.Add(arc);
                    sampled++;
                }

                forwardArcCache[recordingId] = new ForwardArcSet
                {
                    arcs = arcs.ToArray(),
                    cacheKey = cacheKey,
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
            private bool DrawForwardArc(GameScenes scene, ref ForwardArc arc, int targetLayer, int drawFrame)
            {
                var line = arc.vectorLine;
                if (line == null || arc.bodyRelOffsets == null || arc.scratchScaledSpace == null)
                    return false;
                CelestialBody body = ResolveBodyByName(scene, arc.bodyName);
                if (body == null) return false;

                int m = arc.bodyRelOffsets.Length;
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
                line.drawStart = 0;
                line.drawEnd = m - 1;

                line.rectTransform.gameObject.layer = targetLayer;
                var xform = line.rectTransform;
                xform.position = Vector3.zero;
                xform.rotation = Quaternion.identity;
                xform.localScale = Vector3.one;
                if (!line.active) line.active = true;
                line.Draw3D();
                arc.lastDrawnFrame = drawFrame;
                return true;
            }
        }
    }
}
