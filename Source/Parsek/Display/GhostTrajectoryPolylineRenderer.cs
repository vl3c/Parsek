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
        /// Recordings whose non-orbital polyline leg is being drawn THIS frame
        /// (head-UT inside a leg). Published for <c>GhostMapPresence</c> so it can
        /// hide that ghost's proto-vessel orbit LINE while the polyline owns the
        /// phase (otherwise the lingering orbit and the polyline overlap, and the
        /// orbit churns under warp). Cleared at the top of every <c>LateUpdate</c>,
        /// so it is empty whenever the polyline is not actively drawing (feature
        /// off, not in map view, other scene) and the stock orbit behaviour is
        /// left untouched. The orbit updater is throttled (~0.5 s), far slower
        /// than this per-frame publish, so no double-buffering is needed.
        /// </summary>
        private static readonly HashSet<string> activeLegRecordings =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// True when the trajectory polyline is currently drawing a non-orbital
        /// leg for <paramref name="recordingId"/> (see
        /// <see cref="activeLegRecordings"/>). Read by <c>GhostMapPresence</c> to
        /// suppress the overlapping proto-vessel orbit line for that phase.
        /// </summary>
        internal static bool IsRenderingNonOrbitalLeg(string recordingId)
            => recordingId != null && activeLegRecordings.Contains(recordingId);

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
            /// triple is converted ONCE to a scaled-body-LOCAL position
            /// (<see cref="localScaled"/>) via <c>CelestialBody.GetWorldSurfacePosition</c>
            /// (the same call ParsekTrackingStation.cs:1199 uses for the
            /// atmospheric marker, so the polyline lands exactly where a marker
            /// would), then re-projected through the render-stable
            /// <c>body.scaledBody.transform</c> each frame. See
            /// <see cref="localScaled"/> for why the per-frame
            /// <c>GetWorldSurfacePosition</c> call was removed.
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
            /// M scaled-body-LOCAL positions (in <c>body.scaledBody.transform</c>
            /// local space), captured ONCE on the first draw from
            /// <c>scaledBody.transform.InverseTransformPoint(LocalToScaledSpace(GetWorldSurfacePosition(...)))</c>.
            /// Null until that first capture (and reset to null whenever the leg
            /// cache is rebuilt, e.g. on a scene change, so it recaptures against
            /// the new scene's scaled body).
            /// <para>
            /// Why this exists: calling <c>GetWorldSurfacePosition</c> every frame
            /// produced a per-frame two-position jitter under time warp that grew
            /// with the warp multiplier. <c>GetWorldSurfacePosition</c> resolves
            /// through <c>BodyFrame</c> (decompiled: <c>BodyFrame.LocalToWorld(...) +
            /// position</c>), which KSP updates on the physics/warp cadence, so under
            /// warp consecutive RENDER frames sampled body orientations ~one warp
            /// step apart and oscillated between them. The body CENTRE (position) and
            /// the lat/lon direction are stable; only the orientation jittered. The
            /// scaled planet you see in the map (<c>scaledBody.transform</c>) rotates
            /// smoothly per render frame, so re-projecting a body-fixed local point
            /// through it each frame keeps the polyline glued to the rendered surface
            /// with zero jitter while still following the body's rotation. The
            /// one-time capture still uses <c>GetWorldSurfacePosition</c> so the
            /// position is exactly correct; any BodyFrame-vs-scaledBody discrepancy at
            /// capture time is a fixed sub-degree offset, not a per-frame oscillation.
            /// </para>
            /// </summary>
            public Vector3[] localScaled;

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
            if (polylineCache.Count == 0) return;
            int dropped = polylineCache.Count;
            foreach (var kvp in polylineCache)
                DestroyLegLines(kvp.Value.legs);
            polylineCache.Clear();
            ParsekLog.Verbose(Tag,
                "Polyline cache clear: dropped=" + dropped);
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
            if (beforeIdx < 0 || afterIdx < 0) return false; // only vacuum maneuvers between two orbits

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

            // Residual proves the pin (should be ~0; nonzero only from the seam radius mismatch, not the
            // 600-1200 km longitude-lift error). Rate-limited per rec.
            float residStart = Vector3.Distance(leg.scratchScaledSpace[0], cBefore) * ScaledSpace.ScaleFactor / 1000f;
            float residEnd = Vector3.Distance(leg.scratchScaledSpace[m - 1], cAfter) * ScaledSpace.ScaleFactor / 1000f;
            ParsekLog.VerboseRateLimited(Tag, "polyline.anchor." + rec.RecordingId,
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Anchor leg: rec={0} leg=[{1:F1},{2:F1}] body={3} before=seg{4} after=seg{5} residualStart={6:F0}km residualEnd={7:F0}km",
                    rec.RecordingId, leg.startUT, leg.endUT, leg.bodyName ?? "(null)",
                    beforeIdx, afterIdx, residStart, residEnd),
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
            worldPos = Vector3.zero;
            if (string.IsNullOrEmpty(recordingId)) return false;
            if (!polylineCache.TryGetValue(recordingId, out var set) || set.legs == null) return false;

            int frame = Time.frameCount;
            for (int li = 0; li < set.legs.Length; li++)
            {
                var leg = set.legs[li];
                if (headUT < leg.startUT || headUT > leg.endUT) continue;
                int m = leg.PointCount;
                if (m < 2 || leg.scratchScaledSpace == null
                    || leg.recordedUTs == null || leg.recordedUTs.Length != m
                    || leg.lastDrawnFrame != frame)
                    return false; // not drawn this frame -> scratch is stale -> keep the body-fixed head

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
                return true;
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
        // at default execution order 0). The Driver publishes activeLegRecordings in
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
            }

            void LateUpdate()
            {
                // Publish-set for GhostMapPresence orbit suppression: clear FIRST,
                // before any early return, so it reflects only recordings whose
                // non-orbital leg actually draws this frame (empty when the
                // polyline is off / not in map view / wrong scene).
                activeLegRecordings.Clear();

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
                        int m = leg.PointCount;
                        if (m < 2) continue;

                        // Inflate this leg's own VectorLine lazily. One line PER
                        // leg: a single shared line drawn once per leg via
                        // drawStart/drawEnd range slicing does NOT work, because
                        // VectorLine.Draw3D() zeroes every vertex outside the
                        // current window on each call, leaving only the last leg.
                        if (leg.vectorLine == null)
                            leg.vectorLine = BuildLegVectorLine(rec.RecordingId, li, m);
                        if (leg.vectorLine == null) continue;

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

                        // CRITICAL geometry. The points must follow the body's
                        // rotation (a launch path stays glued to its surface site as
                        // the planet spins), but calling GetWorldSurfacePosition every
                        // frame jittered under time warp: it resolves through BodyFrame
                        // (BodyFrame.LocalToWorld(...) + position), which KSP updates on
                        // the physics/warp cadence, so consecutive render frames sampled
                        // orientations ~one warp step apart and oscillated between two
                        // positions (gap proportional to the warp multiplier, zero at
                        // 1x). Instead: capture each point ONCE in the scaled planet's
                        // LOCAL frame (via KSP's own GetWorldSurfacePosition, so the
                        // position is exactly right), then re-project through the
                        // render-stable body.scaledBody.transform each frame. The scaled
                        // planet in the map rotates smoothly per render frame (no
                        // BodyFrame jitter), so the line follows the body's spin without
                        // oscillating. Falls back to the live per-frame path only when
                        // the scaled body is not available (the points then jitter under
                        // warp exactly as before, but at least render).
                        var scaledBody = body.scaledBody;
                        Transform scaledXform = scaledBody != null ? scaledBody.transform : null;
                        if (scaledXform != null)
                        {
                            // (Re)capture the scaled-body-LOCAL positions from the
                            // accurate live surface position whenever the leg is fresh
                            // OR whenever warp is at the 1x baseline (where there is no
                            // BodyFrame jitter, so the capture is exact). Under time warp
                            // we FREEZE the captured local positions and only re-project
                            // them through the smooth scaledBody transform below, which is
                            // what removes the jitter. At 1x the round-trip
                            // TransformPoint(InverseTransformPoint(x)) == x, so behaviour
                            // is identical to the old direct path; under warp the frozen
                            // body-fixed locals stay glued to the spinning planet.
                            bool lowWarp = TimeWarp.CurrentRate <= 1.0001f;
                            if (leg.localScaled == null
                                || leg.localScaled.Length != m
                                || lowWarp)
                            {
                                if (leg.localScaled == null || leg.localScaled.Length != m)
                                    leg.localScaled = new Vector3[m];
                                for (int i = 0; i < m; i++)
                                {
                                    Vector3d world = body.GetWorldSurfacePosition(
                                        leg.lats[i], leg.lons[i], leg.alts[i]);
                                    Vector3 worldScaled =
                                        (Vector3)ScaledSpace.LocalToScaledSpace(world);
                                    leg.localScaled[i] =
                                        scaledXform.InverseTransformPoint(worldScaled);
                                }
                            }
                            for (int i = 0; i < m; i++)
                                leg.scratchScaledSpace[i] =
                                    scaledXform.TransformPoint(leg.localScaled[i]);
                        }
                        else
                        {
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
                        set.legs[li] = leg;
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
                        string activeLegInfo = "activeLeg=none";
                        if (activeLeg >= 0)
                        {
                            var al = set.legs[activeLeg];
                            int mAl = al.PointCount;
                            CelestialBody alBody = ResolveBodyByName(scene, al.bodyName);
                            if (alBody != null && mAl >= 1)
                                activeLegInfo = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                    "DRAWN-leg{0}=[{1:F1},{2:F1}] span={3:F0}s body={4} pts={5} lon0={6:F1} lonN={7:F1} alt0={8:F0} altN={9:F0}",
                                    activeLeg, al.startUT, al.endUT, al.endUT - al.startUT, al.bodyName ?? "(null)", mAl,
                                    LegPointBodyRelLonDeg(alBody, al.lats[0], al.lons[0], al.alts[0]),
                                    LegPointBodyRelLonDeg(alBody, al.lats[mAl - 1], al.lons[mAl - 1], al.alts[mAl - 1]),
                                    al.alts[0], al.alts[mAl - 1]);
                            else
                                activeLegInfo = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                    "DRAWN-leg{0}=[{1:F1},{2:F1}] span={3:F0}s body={4} pts={5} (no body/pts)",
                                    activeLeg, al.startUT, al.endUT, al.endUT - al.startUT, al.bodyName ?? "(null)", mAl);
                        }

                        ParsekLog.VerboseRateLimited(DriverTag, "polyline.head." + rec.RecordingId,
                            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "Polyline head: rec={0} legs={1} headUT={2:F1} activeLeg={3} drawn={4} {5} " +
                                "firstLeg=[{6:F1},{7:F1}] lastLeg=[{8:F1},{9:F1}] body0={10} bodyN={11}",
                                rec.RecordingId, set.legs.Length, headUT, activeLeg, anyDrawn, activeLegInfo,
                                set.legs[0].startUT, set.legs[0].endUT,
                                set.legs[set.legs.Length - 1].startUT, set.legs[set.legs.Length - 1].endUT,
                                set.legs[0].bodyName ?? "(null)", set.legs[set.legs.Length - 1].bodyName ?? "(null)"),
                            2.0);

                        // CHANGE-based companion to the rate-limited head log: a discrete event whenever the
                        // active leg, its body, or the drawn state flips. The polyline's part of the SOI-exit
                        // blink (active leg jumps a Kerbin escape leg -> the Sun transfer leg, or drawn toggles
                        // on/off in a head-in-gap frame) shows as alternating MapTraj-style lines instead of
                        // being hidden in the 2s rate-limited samples.
                        string activeLegBody = activeLeg >= 0
                            ? (set.legs[activeLeg].bodyName ?? "(null)") : "none";
                        ParsekLog.VerboseOnChange(DriverTag, "polyline-active." + rec.RecordingId,
                            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "{0}|{1}|{2}", activeLeg, activeLegBody, anyDrawn),
                            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "Polyline active-leg CHANGED: rec={0} headUT={1:F1} activeLeg={2} body={3} " +
                                "drawn={4} legs={5} {6}",
                                rec.RecordingId, headUT, activeLeg, activeLegBody, anyDrawn, set.legs.Length,
                                activeLegInfo));
                    }

                    if (anyDrawn)
                    {
                        frameDrawn++;
                        // Tell GhostMapPresence the polyline owns this recording's
                        // current phase so it hides the overlapping orbit line.
                        activeLegRecordings.Add(rec.RecordingId);
                    }
                }

                // Deactivation sweep: hide any cached leg line NOT drawn this
                // frame. Covers recording-level skips (suppressed / static /
                // renderHidden, which continue before the per-leg draw), per-leg
                // skips (body missing / fewer than 2 points), and recordings
                // removed from CommittedRecordings entirely (e.g. user delete).
                // Draw3D() is one-shot, so a line stays visible until explicitly
                // deactivated. Only flips lines that are currently active, so the
                // steady state where everything draws is a cheap scan.
                int frameDeactivated = 0;
                foreach (var kvp in polylineCache)
                {
                    var legs = kvp.Value.legs;
                    if (legs == null) continue;
                    for (int i = 0; i < legs.Length; i++)
                    {
                        var line = legs[i].vectorLine;
                        if (line != null &&
                            ShouldDeactivateLeg(line.active, legs[i].lastDrawnFrame, drawFrame))
                        {
                            line.active = false;
                            frameDeactivated++;
                        }
                    }
                }

                ParsekLog.VerboseRateLimited(DriverTag, "polyline.frame.summary",
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Polyline frame: scene={0} drawn={1} suppressed={2} hidden={3} staticSkip={4} noLegs={5} noBody={6} headUtGated={7} deactivated={8} cached={9}",
                        scene, frameDrawn, frameSkippedSuppressed, frameSkippedHidden,
                        frameSkippedStatic, frameSkippedNoLegs, frameSkippedNoBody,
                        frameLegsHeadUtGated, frameDeactivated, polylineCache.Count),
                    5.0);
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
            private static VectorLine BuildLegVectorLine(
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
                // Match the stock map orbit line exactly: a SOLID continuous line
                // via MapView.OrbitLinesMaterial (NOT the dotted/dashed material),
                // the same 5f width and the same distance/direction fade, so the
                // ghost's non-orbital path reads as one unbroken orbit-style line
                // with no dashes, gaps, or interruptions. Mirrors
                // OrbitRendererBase.MakeLine. _FadeStrength / _FadeSign are global
                // GameSettings values set on the SHARED material (idempotent:
                // stock sets the same values every time it makes an orbit line),
                // so this does not disturb real orbit lines.
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
                // EXACT stock vessel orbit-line colour so the polyline is
                // indistinguishable from the ghost's own orbit arcs: KSP's
                // OrbitRenderer seeds an unfocused vessel with
                // SetColor(new Color(0.71,0.71,0.71,1)) and draws the line at
                // orbitColor = nodeColor * 0.5 (alpha preserved) with lineOpacity
                // 1 (OrbitRenderer.GetOrbitColour / OrbitRendererBase), i.e. the
                // mid-grey below. Per-line vertex colour, so the shared
                // OrbitLinesMaterial is left untouched.
                Color stockNode = new Color(0.71f, 0.71f, 0.71f, 1f);
                Color stockOrbit = stockNode * 0.5f;
                stockOrbit.a = stockNode.a;
                line.SetColor(stockOrbit);
                return line;
            }

            /// <summary>
            /// Copies a leg's scratch <c>Vector3[]</c> into the leg's own
            /// VectorLine's <c>points3</c> list starting at the given offset
            /// (0 for per-leg lines).
            /// </summary>
            private static void CopyLegIntoVectorLine(
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
        }
    }
}
