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
        /// Per-body atmosphere geometry needed to decide whether an OrbitSegment
        /// is degenerate (its drawn conic plunges below atmosphere/surface so the
        /// orbit line is unreliable there). Injected into the pure builder via
        /// <see cref="BodyAtmosphereProvider"/> so the builder never calls
        /// FlightGlobals (the Driver populates it from the live bodies).
        /// </summary>
        internal struct BodyAtmosphereInfo
        {
            /// <summary>Atmosphere top above the body radius (metres). 0 for an
            /// airless body.</summary>
            public double atmosphereDepth;

            /// <summary>Body radius (metres).</summary>
            public double radius;
        }

        /// <summary>
        /// Seam (FIX #27): resolves per-body atmosphere geometry by body name.
        /// Returns false when the body is unknown, in which case the orbital
        /// cover keeps EVERY segment (byte-identical to the pre-fix behaviour, so
        /// a recording with no degenerate/below-atmosphere segments is
        /// unaffected). The Driver supplies a FlightGlobals-backed lookup; the
        /// xUnit builder tests pass null (no exclusion) or a synthetic provider.
        /// </summary>
        internal delegate bool BodyAtmosphereProvider(string bodyName, out BodyAtmosphereInfo info);

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
            Recording rec, BodyAtmosphereProvider atmosphere = null)
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

            var legs = BuildLegsForRecording(rec, atmosphere);
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
            Recording rec, BodyAtmosphereProvider atmosphere = null)
        {
            var legs = new List<LegPolyline>();
            if (rec == null) return legs;

            var orbitalIntervals = ComputeOrbitalCoverIntervals(rec.OrbitSegments, atmosphere);

            // FIX #27: count + report the degenerate below-atmosphere segments
            // the cover now excludes (one-shot, build-time). When any are
            // excluded the descent samples they used to drop merge into the
            // descent leg, so log the resulting leg span window for diagnosis.
            int excludedBelowAtmosphereSegments = 0;
            if (atmosphere != null && rec.OrbitSegments != null)
            {
                for (int i = 0; i < rec.OrbitSegments.Count; i++)
                {
                    var seg = rec.OrbitSegments[i];
                    if (seg.endUT <= seg.startUT) continue;
                    if (IsOrbitSegmentBelowAtmosphere(seg, atmosphere))
                        excludedBelowAtmosphereSegments++;
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
                    "Polyline build: rec={0} legs={1} (sectionPts={2} flatPts={3} skippedRelNoBodyFixed={4} excludedBelowAtmoSegs={5})",
                    rec.RecordingId,
                    legs.Count, sectionPointCount, flatPointCount,
                    skippedRelativeWithoutBodyFixed, excludedBelowAtmosphereSegments));

            // FIX #27 one-shot: when the cover excluded degenerate
            // below-atmosphere segments, the descent samples they used to drop
            // now merge into a leg. Report that leg's span (the last leg, which
            // is the descent tail) so a coverage hole is diagnosable from the log.
            if (excludedBelowAtmosphereSegments > 0 && legs.Count > 0)
            {
                var descentLeg = legs[legs.Count - 1];
                ParsekLog.Verbose(Tag,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "excluded {0} below-atmosphere orbit segments from cover rec={1} -> descent leg [{2:F1},{3:F1}]",
                        excludedBelowAtmosphereSegments, rec.RecordingId,
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
            for (int i = 0; i < m; i++)
            {
                var p = sampled[i];
                lats[i] = p.latitude;
                lons[i] = p.longitude;
                alts[i] = p.altitude;
            }
            return new LegPolyline
            {
                lats = lats,
                lons = lons,
                alts = alts,
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
        /// Pure (FIX #27): is this OrbitSegment degenerate, i.e. does its drawn
        /// Keplerian conic plunge below the body's atmosphere top so the orbit
        /// line is unreliable across it? True when the segment's periapsis
        /// altitude is below the body's atmosphere depth.
        ///
        /// Periapsis radius is <c>sma * (1 - ecc)</c>, which is valid for the
        /// hyperbolic case too (sma &lt; 0, ecc &gt; 1 give a positive periapsis
        /// radius). Periapsis altitude = that minus the body radius. The check
        /// mirrors the orbit-line's reliability boundary: the orbit-line patch
        /// suppresses the line wherever the ghost's CURRENT altitude is below
        /// <c>body.atmosphereDepth</c>, and a segment whose periapsis is below
        /// that depth draws a conic that dives into / under the atmosphere (for
        /// the Duna arrival the final segments have periapsis well BELOW the
        /// surface), so the orbit line cannot reliably trace it and the polyline
        /// must own it instead.
        ///
        /// Returns false (segment kept as orbit-owned) when the provider is null
        /// or the body is unknown, so a normal in-space orbit segment (periapsis
        /// well above atmosphere) is UNCHANGED. Gated strictly on periapsis below
        /// atmosphere, so an ordinary parking / transfer orbit is never excluded.
        /// </summary>
        internal static bool IsOrbitSegmentBelowAtmosphere(
            OrbitSegment segment, BodyAtmosphereProvider atmosphere)
        {
            if (atmosphere == null) return false;
            if (string.IsNullOrEmpty(segment.bodyName)) return false;
            if (!atmosphere(segment.bodyName, out BodyAtmosphereInfo info)) return false;
            // Airless bodies have atmosphereDepth 0: the boundary is the surface,
            // so a segment with periapsis below the surface is still degenerate.
            double periapsisRadius = segment.semiMajorAxis * (1.0 - segment.eccentricity);
            double periapsisAltitude = periapsisRadius - info.radius;
            return periapsisAltitude < info.atmosphereDepth;
        }

        /// <summary>
        /// Computes the union of every OrbitSegment's [startUT, endUT]
        /// interval. Points whose UT falls inside the union are dropped
        /// from the polyline at filter time (the orbit-arc covers them).
        ///
        /// FIX #27: a degenerate below-atmosphere segment (see
        /// <see cref="IsOrbitSegmentBelowAtmosphere"/>) is EXCLUDED from the
        /// cover so the polyline picks up the descent samples the unreliable
        /// orbit line abandons there, tiling the two surfaces without a gap. When
        /// <paramref name="atmosphere"/> is null (the xUnit builder default) no
        /// segment is excluded, so a recording with no degenerate segments is
        /// byte-identical to the pre-fix behaviour.
        /// </summary>
        internal static List<(double startUT, double endUT)> ComputeOrbitalCoverIntervals(
            List<OrbitSegment> segments, BodyAtmosphereProvider atmosphere = null)
        {
            var intervals = new List<(double, double)>();
            if (segments == null || segments.Count == 0) return intervals;
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                if (s.endUT <= s.startUT) continue;
                if (IsOrbitSegmentBelowAtmosphere(s, atmosphere)) continue;
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
                GameEvents.onLevelWasLoaded.Add(OnLevelWasLoaded);
                ParsekLog.Verbose(DriverTag,
                    "GhostTrajectoryPolylineRenderer.Driver awake (DDOL singleton)");
            }

            void OnDestroy()
            {
                if (instance == this)
                {
                    instance = null;
                    GameEvents.onGameStateLoad.Remove(OnGameStateLoad);
                    GameEvents.onLevelWasLoaded.Remove(OnLevelWasLoaded);
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
            /// </summary>
            private void OnLevelWasLoaded(GameScenes scene)
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

                // FIX #27: per-body atmosphere provider for the cover exclusion.
                // Built once per frame from the per-scene body map (so the pure
                // builder never calls FlightGlobals). The map is rebuilt lazily
                // per scene inside ResolveBodyByName; ensure it is populated here
                // so RefreshForRecording can resolve below-atmosphere segments.
                EnsureBodyMap(scene);
                BodyAtmosphereProvider atmosphere = ResolveBodyAtmosphere;

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

                    RefreshForRecording(rec, atmosphere);
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
                        ParsekLog.VerboseRateLimited(DriverTag, "polyline.head." + rec.RecordingId,
                            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "Polyline head: rec={0} legs={1} headUT={2:F1} activeLeg={3} drawn={4} " +
                                "firstLeg=[{5:F1},{6:F1}] lastLeg=[{7:F1},{8:F1}] body0={9} bodyN={10}",
                                rec.RecordingId, set.legs.Length, headUT, activeLeg, anyDrawn,
                                set.legs[0].startUT, set.legs[0].endUT,
                                set.legs[set.legs.Length - 1].startUT, set.legs[set.legs.Length - 1].endUT,
                                set.legs[0].bodyName ?? "(null)", set.legs[set.legs.Length - 1].bodyName ?? "(null)"),
                            2.0);
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
            /// FIX #27 atmosphere seam (a <see cref="BodyAtmosphereProvider"/>):
            /// resolves a body's atmosphere depth + radius from the per-scene
            /// body map for the pure cover-exclusion builder. Returns false for
            /// an unknown body so the builder keeps every segment (byte-identical
            /// to the pre-fix path). An airless body reports atmosphereDepth 0, so
            /// the boundary is its surface.
            /// </summary>
            private bool ResolveBodyAtmosphere(string bodyName, out BodyAtmosphereInfo info)
            {
                info = default(BodyAtmosphereInfo);
                if (string.IsNullOrEmpty(bodyName)) return false;
                if (!bodyByName.TryGetValue(bodyName, out var body) || body == null)
                    return false;
                info = new BodyAtmosphereInfo
                {
                    atmosphereDepth = body.atmosphere ? body.atmosphereDepth : 0.0,
                    radius = body.Radius
                };
                return true;
            }
        }
    }
}
