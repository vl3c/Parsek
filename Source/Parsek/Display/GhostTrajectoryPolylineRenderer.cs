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
    /// <see cref="LegPolyline.scratchScaledSpace"/> + the shared
    /// <c>VectorLine.points3</c> keeps the hot path zero-alloc. The 200-point
    /// per-leg cap (§1.3) keeps the per-frame GetWorldSurfacePosition cost
    /// well within budget.
    ///
    /// Commit 1 ships data structures + pure builder + cache lifecycle
    /// helpers. The Vectrosity Driver MonoBehaviour and the per-frame walk
    /// over CommittedRecordings land in commit 2.
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

        // RecordingId -> per-recording leg set. Atmospheric-only recordings
        // have pid=0 so PID is NOT a usable key; RecordingId (string) is.
        // Matches the pattern MapMarkerRenderer.stickyMarkers uses.
        private static readonly Dictionary<string, LegPolylineSet> polylineCache =
            new Dictionary<string, LegPolylineSet>(StringComparer.Ordinal);

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
            /// with <see cref="lons"/> / <see cref="alts"/>. The Driver
            /// converts each (lat, lon, alt) triple to a world position per
            /// frame via the live <c>CelestialBody.GetWorldSurfacePosition</c>
            /// (same call ParsekTrackingStation.cs:1199 uses for the
            /// atmospheric marker), so the polyline lands exactly where a
            /// marker would. No body-local conversion is cached -- caching a
            /// body-fixed <c>GetRelSurfacePosition</c> and adding
            /// <c>body.position</c> would ignore the body's live rotation and
            /// drift from the marker / orbit arcs.
            /// </summary>
            public double[] lats;

            /// <summary>M recorded body-fixed longitudes (degrees).</summary>
            public double[] lons;

            /// <summary>M recorded body-fixed altitudes (metres above body radius).</summary>
            public double[] alts;

            /// <summary>
            /// M-element scratch buffer for per-frame ScaledSpace output
            /// (zero-alloc hot path; the Driver copies into the shared
            /// VectorLine.points3 at this leg's stable offset).
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
            /// Stable per-leg offset into the recording's shared
            /// LegPolylineSet.vectorLine.points3 array (commit 2). Commit 1
            /// fills this with the cumulative sum so the per-leg slice is
            /// known without consulting the Vectrosity object.
            /// </summary>
            public int pointsStartIdx;

            /// <summary>Number of points in this leg (M).</summary>
            public int PointCount => lats != null ? lats.Length : 0;
        }

        /// <summary>
        /// One recording's complete polyline data: an array of body-coherent
        /// legs, the cache invariant hash, and the shared Vectrosity
        /// VectorLine. Stored by RecordingId in <see cref="polylineCache"/>.
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

            /// <summary>
            /// Total points packed across all legs into the shared
            /// VectorLine's points3 array.
            /// </summary>
            public int totalPointCount;

            /// <summary>
            /// Shared <c>LineType.Continuous</c> Vectrosity VectorLine
            /// holding every leg's points packed at stable per-leg
            /// offsets. Each leg is drawn as a ranged slice via
            /// <c>drawStart</c> / <c>drawEnd</c> per frame, matching the
            /// pattern <c>GhostOrbitArcPatch.UpdateSpline</c> uses at
            /// <c>GhostOrbitLinePatch.cs:427</c>. Null until the first
            /// Driver tick builds it.
            /// </summary>
            public VectorLine vectorLine;
        }

        /// <summary>
        /// Refreshes the cache for one recording. Recomputes the cheap
        /// content hash; rebuilds the legs and resets the leg offsets only
        /// when the hash changed. When the hash flips, the previous shared
        /// VectorLine (if any) is destroyed via <see cref="VectorLine.Destroy(ref VectorLine)"/>
        /// before the rebuild so no Vectrosity GameObjects leak.
        ///
        /// The fresh VectorLine itself is constructed lazily on the next
        /// Driver LateUpdate: building it here would create a Vectrosity
        /// GameObject from xUnit (no Unity GameObject backing), which the
        /// unit-test surface forbids. The cache entry's <c>vectorLine</c>
        /// field is null after a refresh; the Driver inflates it on first
        /// use.
        /// </summary>
        internal static void RefreshForRecording(Recording rec)
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

            // Stale Vectrosity object on a rebuild: destroy before
            // overwriting so the GameObject does not leak.
            if (polylineCache.TryGetValue(id, out var stale) && stale.vectorLine != null)
            {
                var staleLine = stale.vectorLine;
                VectorLine.Destroy(ref staleLine);
            }

            var legs = BuildLegsForRecording(rec);
            int totalPoints = 0;
            var legArray = new LegPolyline[legs.Count];
            for (int i = 0; i < legs.Count; i++)
            {
                var leg = legs[i];
                leg.pointsStartIdx = totalPoints;
                totalPoints += leg.PointCount;
                legArray[i] = leg;
            }

            polylineCache[id] = new LegPolylineSet
            {
                legs = legArray,
                contentHash = hash,
                totalPointCount = totalPoints,
                vectorLine = null
            };

            ParsekLog.Verbose(Tag,
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Polyline cache refresh: rec={0} legs={1} totalPoints={2} hash={3:X}",
                    id, legArray.Length, totalPoints, hash));
        }

        /// <summary>
        /// Releases the cache entry for a single recording (chain handoff,
        /// supersede, delete). Destroys the entry's shared Vectrosity
        /// VectorLine via <see cref="VectorLine.Destroy(ref VectorLine)"/>
        /// before dropping the dict entry so the Vectrosity GameObject
        /// does not leak.
        /// </summary>
        internal static void ReleaseForRecording(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return;
            if (polylineCache.TryGetValue(recordingId, out var set) && set.vectorLine != null)
            {
                var line = set.vectorLine;
                VectorLine.Destroy(ref line);
            }
            if (polylineCache.Remove(recordingId))
            {
                ParsekLog.Verbose(Tag,
                    "Polyline cache release: rec=" + recordingId);
            }
        }

        /// <summary>
        /// Drops the entire cache. Iterates the cached entries to call
        /// <see cref="VectorLine.Destroy(ref VectorLine)"/> on each shared
        /// VectorLine BEFORE dropping the dict, otherwise the Vectrosity
        /// GameObjects leak. Called from
        /// <c>GhostMapPresence.RemoveAllGhostVessels</c>, the
        /// <c>useGhostTrajectoryPolyline</c> setting OFF path, and the
        /// Driver's <c>onGameStateLoad</c> handler (cross-save invariant
        /// flush per §1.4 / §6).
        /// </summary>
        internal static void Clear()
        {
            if (polylineCache.Count == 0) return;
            int dropped = polylineCache.Count;
            foreach (var kvp in polylineCache)
            {
                if (kvp.Value.vectorLine != null)
                {
                    var line = kvp.Value.vectorLine;
                    VectorLine.Destroy(ref line);
                }
            }
            polylineCache.Clear();
            ParsekLog.Verbose(Tag,
                "Polyline cache clear: dropped=" + dropped);
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
        /// Pure leg-construction (§1.3). Walks the recording's TrackSections
        /// dispatching per-section by referenceFrame, then folds in the
        /// pre-first / post-last flat Recording.Points as body-grouped
        /// fallback legs. Points whose UT falls inside any OrbitSegment
        /// interval are dropped (the orbit-arc covers them).
        ///
        /// Per-section policy:
        /// - Absolute: walk section.frames (body-fixed lat/lon/alt).
        /// - Relative with non-null bodyFixedFrames: walk section.bodyFixedFrames.
        /// - Relative WITHOUT bodyFixedFrames: SKIP the leg entirely;
        ///   reading section.frames[i].latitude/longitude/altitude as
        ///   lat/lon/alt would place the leg deep inside the planet (the
        ///   CLAUDE.md RELATIVE-frame footgun: those fields are metre
        ///   offsets along the anchor's local x/y/z, NOT lat/lon/alt).
        /// </summary>
        internal static List<LegPolyline> BuildLegsForRecording(Recording rec)
        {
            var legs = new List<LegPolyline>();
            if (rec == null) return legs;

            var orbitalIntervals = ComputeOrbitalCoverIntervals(rec.OrbitSegments);

            int skippedRelativeWithoutBodyFixed = 0;
            int builtAbsoluteSections = 0;
            int builtRelativeSections = 0;
            int builtFallbackLegs = 0;

            // (1) Walk TrackSections in order.
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
                    if (filtered.Count < 2) continue;

                    string bodyName = ResolveBodyForPoints(filtered);
                    if (string.IsNullOrEmpty(bodyName)) continue;

                    legs.Add(BuildLegFromBodyFixedPoints(filtered, bodyName));
                    if (section.referenceFrame == ReferenceFrame.Absolute)
                        builtAbsoluteSections++;
                    else
                        builtRelativeSections++;
                }
            }

            // (2) Walk Recording.Points entries OUTSIDE every section's
            //     [startUT, endUT] range (flat Absolute fallback).
            if (rec.Points != null && rec.Points.Count > 0)
            {
                var outsidePoints = new List<TrajectoryPoint>(rec.Points.Count);
                for (int i = 0; i < rec.Points.Count; i++)
                {
                    var p = rec.Points[i];
                    if (IsInsideAnySection(p.ut, rec.TrackSections)) continue;
                    if (IsInsideAnyOrbitalInterval(p.ut, orbitalIntervals)) continue;
                    outsidePoints.Add(p);
                }
                foreach (var bodyRun in GroupConsecutiveByBody(outsidePoints))
                {
                    if (bodyRun.Count < 2) continue;
                    string bodyName = bodyRun[0].bodyName;
                    if (string.IsNullOrEmpty(bodyName)) continue;
                    legs.Add(BuildLegFromBodyFixedPoints(bodyRun, bodyName));
                    builtFallbackLegs++;
                }
            }

            ParsekLog.Verbose(Tag,
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Polyline build: rec={0} legs={1} (absSection={2} relSection={3} fallback={4} skippedRelNoBodyFixed={5})",
                    rec.RecordingId,
                    legs.Count, builtAbsoluteSections, builtRelativeSections,
                    builtFallbackLegs, skippedRelativeWithoutBodyFixed));

            return legs;
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
                endUT = sampled[m - 1].ut,
                pointsStartIdx = 0
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
        /// Computes the union of every OrbitSegment's [startUT, endUT]
        /// interval. Points whose UT falls inside the union are dropped
        /// from the polyline at filter time (the orbit-arc covers them).
        /// </summary>
        internal static List<(double startUT, double endUT)> ComputeOrbitalCoverIntervals(
            List<OrbitSegment> segments)
        {
            var intervals = new List<(double, double)>();
            if (segments == null || segments.Count == 0) return intervals;
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                if (s.endUT <= s.startUT) continue;
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
        /// Groups a flat point list into consecutive-same-body sublists so
        /// each emitted leg is body-coherent. The flat
        /// <c>Recording.Points</c> list can span multiple bodies across an
        /// SOI crossing that occurred outside any TrackSection.
        /// </summary>
        internal static List<List<TrajectoryPoint>> GroupConsecutiveByBody(
            List<TrajectoryPoint> points)
        {
            var groups = new List<List<TrajectoryPoint>>();
            if (points == null || points.Count == 0) return groups;
            string currentBody = null;
            List<TrajectoryPoint> current = null;
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                if (current == null
                    || !string.Equals(p.bodyName ?? "", currentBody ?? "", StringComparison.Ordinal))
                {
                    if (current != null && current.Count > 0) groups.Add(current);
                    current = new List<TrajectoryPoint>();
                    currentBody = p.bodyName;
                }
                current.Add(p);
            }
            if (current != null && current.Count > 0) groups.Add(current);
            return groups;
        }

        private static string ResolveBodyForPoints(List<TrajectoryPoint> points)
        {
            if (points == null) return null;
            for (int i = 0; i < points.Count; i++)
            {
                if (!string.IsNullOrEmpty(points[i].bodyName))
                    return points[i].bodyName;
            }
            return null;
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
        /// (hash-gated), and submits each recording's shared VectorLine
        /// once per leg (range-sliced via <c>drawStart</c>/<c>drawEnd</c>).
        ///
        /// Single instance for the AppDomain lifetime; lives across scene
        /// transitions via <see cref="MonoBehaviour"/>+
        /// <c>DontDestroyOnLoad</c>, matching the
        /// <c>TestRunnerShortcut.cs:51-59</c> repo precedent. KSC scene is
        /// out of scope for v1 (§1.1); the LateUpdate scene gate skips any
        /// scene other than TRACKSTATION / FLIGHT.
        /// </summary>
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
                // Scene gate: v1 ships TRACKSTATION + FLIGHT only (§1.1).
                var scene = HighLogic.LoadedScene;
                if (scene != GameScenes.TRACKSTATION && scene != GameScenes.FLIGHT)
                    return;

                if (!MapView.MapIsEnabled) return;
                var settings = ParsekSettings.Current;
                if (settings == null) return;
                if (!settings.useGhostTrajectoryPolyline) return;

                // Pull the per-frame filter inputs ONCE, outside the loop.
                var suppressed = GhostMapPresence.CachedTrackingStationSuppressedIds;
                int targetLayer = MapView.Draw3DLines ? 24 : 31;
                double currentUT = Planetarium.GetUniversalTime();

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
                for (int recordingIndex = 0; recordingIndex < committed.Count; recordingIndex++)
                {
                    var rec = committed[recordingIndex];
                    if (rec == null) continue;
                    if (suppressed != null && suppressed.Contains(rec.RecordingId))
                    {
                        frameSkippedSuppressed++;
                        continue;
                    }

                    // STATIC visibility filter only (MAJOR fix). The polyline
                    // is a full-path bridge drawn for the whole recording
                    // regardless of where the playback head currently is, so
                    // it must NOT inherit the per-head-UT gates
                    // (OrbitSegmentActive / NativeIconActive) that
                    // ClassifyAtmosphericMarkerSkip applies -- those would
                    // blink the entire polyline out whenever the head enters
                    // an orbital phase or while an un-suppressed ghost
                    // ProtoVessel exists. Keep only the recording-level static
                    // subset: debris / no-trajectory / suppression.
                    var staticSkip = ClassifyPolylineStaticSkip(rec, suppressed);
                    if (staticSkip != PolylineStaticSkipReason.None)
                    {
                        frameSkippedStatic++;
                        continue;
                    }

                    // renderHidden gate (loop-unit visibility): hide the
                    // polyline for a loop unit the marker pass is hiding too.
                    GhostPlaybackLogic.ResolveTrackingStationSampleUT(
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

                    RefreshForRecording(rec);
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

                    // Inflate VectorLine lazily.
                    if (set.vectorLine == null)
                    {
                        set.vectorLine = BuildSharedVectorLine(rec.RecordingId, set.totalPointCount);
                        polylineCache[rec.RecordingId] = set;
                    }
                    if (set.vectorLine == null) continue;

                    set.vectorLine.rectTransform.gameObject.layer = targetLayer;

                    bool anyDrawn = false;
                    for (int li = 0; li < set.legs.Length; li++)
                    {
                        var leg = set.legs[li];
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
                        // CRITICAL geometry: convert each recorded body-fixed
                        // (lat, lon, alt) to a LIVE world position via the same
                        // CelestialBody.GetWorldSurfacePosition call the
                        // atmospheric-marker resolver uses
                        // (ParsekTrackingStation.cs:1199), so the polyline
                        // lands exactly where a marker would. This is zero-alloc:
                        // GetWorldSurfacePosition / LocalToScaledSpace return
                        // value types and the result is written into the
                        // pre-allocated scratch buffer.
                        for (int i = 0; i < m; i++)
                        {
                            Vector3d world = body.GetWorldSurfacePosition(
                                leg.lats[i], leg.lons[i], leg.alts[i]);
                            leg.scratchScaledSpace[i] =
                                (Vector3)ScaledSpace.LocalToScaledSpace(world);
                        }

                        CopyLegIntoVectorLine(set.vectorLine, leg.scratchScaledSpace,
                            leg.pointsStartIdx);
                        set.vectorLine.drawStart = leg.pointsStartIdx;
                        set.vectorLine.drawEnd = leg.pointsStartIdx + m - 1;
                        set.vectorLine.Draw3D();
                        anyDrawn = true;
                    }
                    if (anyDrawn) frameDrawn++;
                }

                ParsekLog.VerboseRateLimited(DriverTag, "polyline.frame.summary",
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Polyline frame: scene={0} drawn={1} suppressed={2} hidden={3} staticSkip={4} noLegs={5} noBody={6} cached={7}",
                        scene, frameDrawn, frameSkippedSuppressed, frameSkippedHidden,
                        frameSkippedStatic, frameSkippedNoLegs, frameSkippedNoBody,
                        polylineCache.Count),
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
            /// Constructs a fresh shared <c>LineType.Continuous</c>
            /// VectorLine sized to hold every leg's points packed
            /// consecutively. Uses <c>MapView.DottedLinesMaterial</c> for
            /// the dashed style (verified as a public static stock
            /// property; closes OQ#1 / §2.3), falling back to
            /// <c>MapView.OrbitLinesMaterial</c> when the dotted material
            /// is unavailable. Width matches the stock orbit-arc width
            /// (5f). A per-line colour overlay via
            /// <see cref="VectorLine.SetColor(Color32)"/> tints the
            /// polyline so it reads as distinct from the Keplerian arcs
            /// without mutating the shared material's colour (which
            /// would dim every stock orbit line).
            /// </summary>
            private static VectorLine BuildSharedVectorLine(
                string recordingId, int totalPoints)
            {
                if (totalPoints <= 0) return null;
                var points = new List<Vector3>(totalPoints);
                for (int i = 0; i < totalPoints; i++)
                    points.Add(Vector3.zero);
                var line = new VectorLine(
                    "ParsekGhostTrajectoryPolyline-" + recordingId,
                    points,
                    5f,
                    LineType.Continuous);
                Material dashedMat = MapView.DottedLinesMaterial;
                Material orbitMat = MapView.OrbitLinesMaterial;
                Material chosen = dashedMat != null ? dashedMat : orbitMat;
                if (chosen != null)
                {
                    line.texture = chosen.mainTexture;
                    line.material = chosen;
                }
                line.continuousTexture = true;
                line.UpdateImmediate = true;
                line.SetColor(new Color32(180, 220, 255, 160));
                return line;
            }

            /// <summary>
            /// Copies a leg's scratch <c>Vector3[]</c> into the shared
            /// VectorLine's <c>points3</c> list at the given stable
            /// offset.
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
                if (bodyMapScene != scene || bodyByName.Count == 0)
                {
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
                return bodyByName.TryGetValue(name, out var body) ? body : null;
            }
        }
    }
}
