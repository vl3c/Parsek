// Non-orbital map polyline renderer (design plan
// docs/dev/plans/map-trajectory-polyline.md v6).
// Commit 1 contains pure data + builder + cache helpers only; the Driver
// MonoBehaviour (which performs the raw RecordingStore walk and earns the
// [ERS-exempt] file marker + scripts/ers-els-audit-allowlist.txt entry)
// lands in commit 2.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek.Display
{
    /// <summary>
    /// Per-recording non-orbital map polyline renderer (design plan
    /// docs/dev/plans/map-trajectory-polyline.md v6).
    ///
    /// Bridges the gap between successive ghost orbit-line arcs by drawing a
    /// polyline through the recorded trajectory points for atmospheric /
    /// propulsive / surface phases that have no Keplerian arc. The cache
    /// stores body-LOCAL Vector3d arrays per leg so the per-frame hot path
    /// is just (body.position + cached_offset) + ScaledSpace transform with
    /// zero allocation.
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
            /// M body-LOCAL points (recorded-time spherical -> Cartesian).
            /// Per-frame hot path adds CelestialBody.position to each entry
            /// to reach world-space.
            /// </summary>
            public Vector3d[] bodyLocalPoints;

            /// <summary>
            /// M-element scratch buffer for per-frame ScaledSpace output
            /// (zero-alloc hot path; commit 2 copies into the shared
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
        }

        /// <summary>
        /// One recording's complete polyline data: an array of body-coherent
        /// legs plus the cache invariant hash and (commit 2) the shared
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
            /// VectorLine's points3 array. Pre-computed at cache-build time
            /// so commit 2 can allocate points3 once.
            /// </summary>
            public int totalPointCount;
        }

        /// <summary>
        /// Refreshes the cache for one recording. Recomputes the cheap
        /// content hash; rebuilds the legs and resets the leg offsets only
        /// when the hash changed. Pure with respect to Vectrosity (commit 2
        /// extends this to also rebuild the shared VectorLine).
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

            var legs = BuildLegsForRecording(rec);
            int totalPoints = 0;
            var legArray = new LegPolyline[legs.Count];
            for (int i = 0; i < legs.Count; i++)
            {
                var leg = legs[i];
                leg.pointsStartIdx = totalPoints;
                totalPoints += leg.bodyLocalPoints.Length;
                legArray[i] = leg;
            }

            polylineCache[id] = new LegPolylineSet
            {
                legs = legArray,
                contentHash = hash,
                totalPointCount = totalPoints
            };

            ParsekLog.Verbose(Tag,
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Polyline cache refresh: rec={0} legs={1} totalPoints={2} hash={3:X}",
                    id, legArray.Length, totalPoints, hash));
        }

        /// <summary>
        /// Releases the cache entry for a single recording (chain handoff,
        /// supersede, delete). Commit 2 also calls VectorLine.Destroy here
        /// before dropping the entry; commit 1 is a plain dictionary remove
        /// because no Vectrosity object exists yet.
        /// </summary>
        internal static void ReleaseForRecording(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return;
            if (polylineCache.Remove(recordingId))
            {
                ParsekLog.Verbose(Tag,
                    "Polyline cache release: rec=" + recordingId);
            }
        }

        /// <summary>
        /// Drops the entire cache. Called from RemoveAllGhostVessels and
        /// the useGhostTrajectoryPolyline setting OFF path. Commit 2 also
        /// calls VectorLine.Destroy per entry before clearing.
        /// </summary>
        internal static void Clear()
        {
            if (polylineCache.Count == 0) return;
            int dropped = polylineCache.Count;
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
        /// </summary>
        internal static LegPolyline BuildLegFromBodyFixedPoints(
            List<TrajectoryPoint> points, string bodyName)
        {
            var sampled = DownsamplePreservingEndpoints(points, MaxPolylinePointsPerLeg);
            int m = sampled.Count;
            var bodyLocal = new Vector3d[m];
            for (int i = 0; i < m; i++)
            {
                var p = sampled[i];
                bodyLocal[i] = SphericalToBodyLocal(p.latitude, p.longitude, p.altitude);
            }
            return new LegPolyline
            {
                bodyLocalPoints = bodyLocal,
                scratchScaledSpace = new Vector3[m],
                bodyName = bodyName,
                startUT = sampled[0].ut,
                endUT = sampled[m - 1].ut,
                pointsStartIdx = 0
            };
        }

        /// <summary>
        /// Test-friendly pure-arithmetic stand-in for
        /// <c>CelestialBody.GetRelSurfacePosition(lat, lon, alt)</c>. KSP's
        /// runtime call is wired in at the Driver level (commit 2); the
        /// commit 1 builder routes through this helper so the unit tests
        /// (which cannot stand up a real CelestialBody) can assert on the
        /// cached arrays.
        ///
        /// Returns a body-local Cartesian (BodyFrame x/y/z in metres) using
        /// the same spherical contract KSP's resolver uses (lat in [-90,90]
        /// degrees, lon in degrees, alt in metres above body radius). Body
        /// radius is unknown at this layer so the helper returns the alt-
        /// only Cartesian unit-vector scaling and the Driver supplies the
        /// real conversion at frame time via body.GetRelSurfacePosition.
        ///
        /// In production code the Driver builds bodyLocalPoints via
        /// CelestialBody.GetRelSurfacePosition (commit 2). The xUnit suite
        /// asserts on the deterministic outputs of this pure helper.
        /// </summary>
        internal static Vector3d SphericalToBodyLocal(double latDeg, double lonDeg, double alt)
        {
            const double Deg2Rad = System.Math.PI / 180.0;
            double phi = latDeg * Deg2Rad;
            double lam = lonDeg * Deg2Rad;
            double cosPhi = System.Math.Cos(phi);
            double sinPhi = System.Math.Sin(phi);
            double cosLam = System.Math.Cos(lam);
            double sinLam = System.Math.Sin(lam);
            // r is the recorded body-relative magnitude. KSP's real
            // GetRelSurfacePosition uses (body.Radius + alt); since this is a
            // unit-test fixture, we collapse the body radius to a constant
            // PlanetRadiusMetres = 600,000 (Kerbin's radius) so test
            // assertions can be deterministic without a live CelestialBody.
            // The Driver (commit 2) ignores this helper at runtime.
            const double PlanetRadiusMetres = 600000.0;
            double r = PlanetRadiusMetres + alt;
            return new Vector3d(r * cosPhi * cosLam, r * sinPhi, r * cosPhi * sinLam);
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
    }
}
