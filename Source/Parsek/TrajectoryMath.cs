using System;
using System.Collections.Generic;
using Parsek.Rendering;
using UnityEngine;

namespace Parsek
{
    internal struct RecordingStats
    {
        public double maxAltitude;
        public double maxSpeed;
        public double distanceTravelled;
        public int pointCount;
        public int orbitSegmentCount;
        public int partEventCount;
        public string primaryBody;
        public double maxRange;
    }

    /// <summary>
    /// Pure static math functions for trajectory recording and playback.
    /// </summary>
    public static class TrajectoryMath
    {
        /// <summary>
        /// Decides whether to record a trajectory point based on velocity changes,
        /// a min-interval floor, and a max-interval backstop. Pure function for testability.
        ///
        /// Gate order:
        ///   1. First point (lastRecordedUT &lt; 0) — always record
        ///   2. Max-interval backstop — always record after this long (overrides floor for
        ///      degenerate configs where minInterval &gt; maxInterval)
        ///   3. Min-interval floor — never record inside this window, regardless of velocity gates
        ///   4. Velocity direction / speed gates — opportunistic
        ///
        /// The min-interval floor caps worst-case sample rate during slow/jittery motion
        /// (EVA on surface, slow rovers, hovering aircraft) where the velocity gates can
        /// otherwise fire on every physics frame. Set minInterval = 0 to disable the floor.
        /// </summary>
        internal static bool ShouldRecordPoint(
            Vector3 currentVelocity, Vector3 lastVelocity,
            double currentUT, double lastRecordedUT,
            float minInterval, float maxInterval,
            float velDirThreshold, float speedThreshold)
        {
            // Always record the first point
            if (lastRecordedUT < 0)
                return true;

            double elapsed = currentUT - lastRecordedUT;

            // Max interval backstop — always record after this long.
            // Checked BEFORE the min floor so a degenerate config (minInterval > maxInterval)
            // still produces samples instead of starving the recording.
            if (elapsed >= maxInterval)
                return true;

            // Min interval floor — never record inside this window
            if (elapsed < minInterval)
                return false;

            float currentSpeed = currentVelocity.magnitude;
            float lastSpeed = lastVelocity.magnitude;

            // Velocity direction change (guard against zero vectors to avoid NaN)
            if (currentSpeed > 0.1f && lastSpeed > 0.1f)
            {
                float angle = Vector3.Angle(currentVelocity, lastVelocity);
                if (angle > velDirThreshold)
                    return true;
            }

            // Speed change (relative to last speed, with floor to avoid div-by-near-zero)
            float speedDelta = Mathf.Abs(currentSpeed - lastSpeed);
            float reference = Mathf.Max(lastSpeed, 0.1f);
            if (speedDelta / reference > speedThreshold)
                return true;

            return false;
        }

        /// <summary>
        /// Returns the index of the first point where the vessel has meaningfully moved
        /// from its initial position (altitude changed by >= altThreshold meters, or speed
        /// exceeds speedThreshold m/s). Returns 0 if the vessel is moving from the start
        /// or the list is too short.
        /// </summary>
        internal static int FindFirstMovingPoint(List<TrajectoryPoint> points,
            double altThreshold = 1.0, float speedThreshold = 5.0f)
        {
            if (points == null || points.Count < 2) return 0;
            double startAlt = points[0].altitude;
            for (int i = 0; i < points.Count; i++)
            {
                if (System.Math.Abs(points[i].altitude - startAlt) >= altThreshold)
                {
                    ParsekLog.Verbose("TrajectoryMath",
                        $"FindFirstMovingPoint: altitude trigger at index {i} " +
                        $"(alt={points[i].altitude:F1}, startAlt={startAlt:F1}, delta={System.Math.Abs(points[i].altitude - startAlt):F1})");
                    return i;
                }
                if (points[i].velocity.magnitude >= speedThreshold)
                {
                    ParsekLog.Verbose("TrajectoryMath",
                        $"FindFirstMovingPoint: speed trigger at index {i} " +
                        $"(speed={points[i].velocity.magnitude:F1}m/s, threshold={speedThreshold:F1})");
                    return i;
                }
            }
            // Vessel never moved significantly — keep all points
            ParsekLog.Verbose("TrajectoryMath",
                $"FindFirstMovingPoint: vessel never moved significantly across {points.Count} points, keeping all");
            return 0;
        }

        /// <summary>
        /// Find an orbit segment that covers the given UT. Returns null if none match.
        /// Linear scan — the list is tiny (typically 0-3 segments per recording).
        /// </summary>
        internal static OrbitSegment? FindOrbitSegment(List<OrbitSegment> segments, double ut)
        {
            if (segments == null) return null;
            for (int i = 0; i < segments.Count; i++)
            {
                bool inRange = (i == segments.Count - 1)
                    ? (ut >= segments[i].startUT && ut <= segments[i].endUT)
                    : (ut >= segments[i].startUT && ut < segments[i].endUT);
                if (inRange)
                    return segments[i];
            }
            return null;
        }

        /// <summary>
        /// Phase 6 §7.5 / §7.7 shared helper. Evaluates a body-relative
        /// world position from an OrbitSegment list at the supplied UT.
        /// When <paramref name="ut"/> falls within a segment, propagates
        /// that segment's Kepler. When <paramref name="ut"/> is past the
        /// last segment's endUT (or before the first segment's startUT),
        /// falls back to the nearest endpoint segment so a partial last
        /// or first checkpoint doesn't silently produce a null result —
        /// this is the §7.7 BubbleEntry case where the candidate UT
        /// equals the Checkpoint section's endUT but the last sampled
        /// checkpoint's endUT is a hair below that, AND the §7.5 case
        /// where the boundary UT sits at the Checkpoint section's start
        /// or end UT but the first/last sampled checkpoint covers a
        /// slightly narrower range.
        ///
        /// <para>
        /// Returns <c>null</c> on any of: null/empty checkpoint list,
        /// null body resolver, body resolver returning null for the
        /// segment's bodyName, <c>Orbit.getPositionAtUT</c> throwing,
        /// or NaN/Inf result. Callers treat null as a fail-closed
        /// signal (HR-9 visible failure on the call site).
        /// </para>
        ///
        /// <para>
        /// Body resolution goes through the supplied delegate so
        /// xUnit can inject a fake <see cref="CelestialBody"/> via
        /// <see cref="Parsek.Rendering.AnchorPropagator.BodyResolverForTesting"/>
        /// or the equivalent test seam, while production passes a
        /// <c>FlightGlobals.Bodies</c> lookup.
        /// </para>
        /// </summary>
        internal static Vector3d? EvaluateOrbitSegmentAtUT(
            List<OrbitSegment> checkpoints,
            double ut,
            Func<string, CelestialBody> bodyResolver)
        {
            if (checkpoints == null || checkpoints.Count == 0) return null;
            if (bodyResolver == null) return null;

            OrbitSegment? maybeSeg = FindOrbitSegment(checkpoints, ut);
            if (!maybeSeg.HasValue)
            {
                // Endpoint fallback: pick the segment on the side of the
                // checkpoint range that the UT lies past. FindOrbitSegment
                // returns null only when ut < checkpoints[0].startUT OR
                // ut > checkpoints[Count-1].endUT, so the boundary check
                // disambiguates which endpoint to use.
                if (ut <= checkpoints[0].startUT)
                    maybeSeg = checkpoints[0];
                else
                    maybeSeg = checkpoints[checkpoints.Count - 1];
            }
            OrbitSegment seg = maybeSeg.Value;

            CelestialBody body = bodyResolver(seg.bodyName);
            if (object.ReferenceEquals(body, null)) return null;

            try
            {
                Orbit orbit = new Orbit(
                    seg.inclination, seg.eccentricity, seg.semiMajorAxis,
                    seg.longitudeOfAscendingNode, seg.argumentOfPeriapsis,
                    seg.meanAnomalyAtEpoch, seg.epoch, body);
                Vector3d pos = orbit.getPositionAtUT(ut);
                if (double.IsNaN(pos.x) || double.IsNaN(pos.y) || double.IsNaN(pos.z))
                    return null;
                return pos;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Pure: returns true when two orbit segments represent the same underlying orbit
        /// for map-display continuity purposes. Epoch and mean anomaly are intentionally
        /// ignored because the same orbit can be serialized at different times.
        /// </summary>
        internal static bool AreOrbitSegmentsEquivalentForMapDisplay(OrbitSegment a, OrbitSegment b)
        {
            if (string.IsNullOrEmpty(a.bodyName)
                || !string.Equals(a.bodyName, b.bodyName, System.StringComparison.Ordinal))
                return false;

            double smaTolerance = System.Math.Max(10.0, System.Math.Abs(a.semiMajorAxis) * 1e-6);
            const double EccTolerance = 1e-5;
            const double AngleToleranceDeg = 0.01;

            return System.Math.Abs(a.semiMajorAxis - b.semiMajorAxis) <= smaTolerance
                && System.Math.Abs(a.eccentricity - b.eccentricity) <= EccTolerance
                && AngularDeltaDegrees(a.inclination, b.inclination) <= AngleToleranceDeg
                && AngularDeltaDegrees(a.longitudeOfAscendingNode, b.longitudeOfAscendingNode) <= AngleToleranceDeg
                && AngularDeltaDegrees(a.argumentOfPeriapsis, b.argumentOfPeriapsis) <= AngleToleranceDeg;
        }

        /// <summary>
        /// Shortest angular distance in degrees between two angles, accounting for
        /// the 0/360 wraparound. Inputs may be in any range (degrees); the result
        /// is always in [0, 180]. Use this instead of raw <c>Math.Abs(a - b)</c>
        /// for orbital LAN / argument-of-periapsis / true-anomaly comparisons,
        /// since stable orbits routinely cross the 0/360 boundary and a literal
        /// difference produces a false ~360 deg mismatch.
        /// Example: <c>AngularDeltaDegrees(359.997, 0.002) == 0.005</c>.
        /// Inclination (range [0, 180]) is also safe — within that range the
        /// wrap-correction branch is never taken, but the helper keeps math
        /// centralized for all angle deltas.
        /// </summary>
        internal static double AngularDeltaDegrees(double a, double b)
        {
            double delta = System.Math.Abs(a - b) % 360.0;
            return delta > 180.0 ? 360.0 - delta : delta;
        }

        private static void ExpandEquivalentOrbitWindow(
            List<OrbitSegment> segments,
            int seedFirstIndex,
            int seedLastIndex,
            out double visibleStartUT,
            out double visibleEndUT,
            out int firstVisibleIndex,
            out int lastVisibleIndex)
        {
            firstVisibleIndex = seedFirstIndex;
            while (firstVisibleIndex > 0
                && AreOrbitSegmentsEquivalentForMapDisplay(
                    segments[firstVisibleIndex - 1], segments[firstVisibleIndex]))
            {
                firstVisibleIndex--;
            }

            lastVisibleIndex = seedLastIndex;
            while (lastVisibleIndex < segments.Count - 1
                && AreOrbitSegmentsEquivalentForMapDisplay(
                    segments[lastVisibleIndex], segments[lastVisibleIndex + 1]))
            {
                lastVisibleIndex++;
            }

            visibleStartUT = segments[firstVisibleIndex].startUT;
            visibleEndUT = segments[lastVisibleIndex].endUT;
        }

        /// <summary>
        /// Map-view policy helper: return the active orbit segment for the given UT plus
        /// the merged visible time bounds to use for map-line/icon continuity.
        /// Equivalent same-body segments are expanded into one continuous visible window,
        /// including brief gaps between them, so prerecorded same-SOI journeys render as
        /// one uninterrupted map line.
        /// </summary>
        internal static bool TryGetOrbitWindowForMapDisplay(
            List<OrbitSegment> segments,
            double ut,
            out OrbitSegment segment,
            out double visibleStartUT,
            out double visibleEndUT,
            out int firstVisibleIndex,
            out int lastVisibleIndex,
            out bool carriedAcrossGap)
        {
            segment = default(OrbitSegment);
            visibleStartUT = 0;
            visibleEndUT = 0;
            firstVisibleIndex = -1;
            lastVisibleIndex = -1;
            carriedAcrossGap = false;

            if (segments == null || segments.Count == 0)
                return false;

            for (int i = 0; i < segments.Count; i++)
            {
                bool inRange = (i == segments.Count - 1)
                    ? (ut >= segments[i].startUT && ut <= segments[i].endUT)
                    : (ut >= segments[i].startUT && ut < segments[i].endUT);
                if (!inRange)
                    continue;

                segment = segments[i];
                ExpandEquivalentOrbitWindow(
                    segments, i, i,
                    out visibleStartUT, out visibleEndUT,
                    out firstVisibleIndex, out lastVisibleIndex);
                return true;
            }

            if (segments.Count < 2)
                return false;

            int previousIndex = -1;
            for (int i = 0; i < segments.Count; i++)
            {
                OrbitSegment candidate = segments[i];
                if (candidate.endUT <= ut)
                {
                    previousIndex = i;
                    continue;
                }

                if (previousIndex < 0 || candidate.startUT <= ut)
                    return false;

                if (!AreOrbitSegmentsEquivalentForMapDisplay(segments[previousIndex], candidate))
                    return false;

                segment = segments[previousIndex];
                carriedAcrossGap = true;
                ExpandEquivalentOrbitWindow(
                    segments, previousIndex, i,
                    out visibleStartUT, out visibleEndUT,
                    out firstVisibleIndex, out lastVisibleIndex);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Map-view policy helper: return the active orbit segment for the given UT plus
        /// the visible time bounds to use for map-line/icon continuity.
        /// </summary>
        internal static bool TryGetOrbitSegmentForMapDisplay(
            List<OrbitSegment> segments, double ut,
            out OrbitSegment segment, out double visibleStartUT, out double visibleEndUT)
        {
            return TryGetOrbitWindowForMapDisplay(
                segments, ut,
                out segment, out visibleStartUT, out visibleEndUT,
                out _, out _, out _);
        }

        /// <summary>
        /// Map-view policy helper: return the active orbit segment for the given UT, or
        /// carry the immediately preceding segment across a gap when the next segment stays
        /// in the same SOI/body. This avoids fragmenting ghost orbit lines across brief
        /// off-rails sections during a continuous same-body journey.
        /// </summary>
        internal static OrbitSegment? FindOrbitSegmentForMapDisplay(List<OrbitSegment> segments, double ut)
        {
            return TryGetOrbitSegmentForMapDisplay(segments, ut,
                out OrbitSegment segment, out _, out _)
                ? (OrbitSegment?)segment
                : null;
        }

        /// <summary>
        /// Find the waypoint index for interpolation using cached lookup.
        /// Parameterized to work with any point list + cached index.
        /// </summary>
        internal static int FindWaypointIndex(List<TrajectoryPoint> points, ref int cachedIndex, double targetUT)
        {
            if (points.Count < 2)
            {
                ParsekLog.VerboseRateLimited("TrajectoryMath", "waypoint-too-few-points",
                    $"FindWaypointIndex skipped: points.Count={points.Count} targetUT={targetUT:F3}", 5.0);
                return -1;
            }

            if (targetUT < points[0].ut)
                return -1;

            if (targetUT >= points[points.Count - 1].ut)
                return points.Count - 2;

            // Try cached index first (common case: sequential playback)
            if (cachedIndex >= 0 && cachedIndex < points.Count - 1)
            {
                if (points[cachedIndex].ut <= targetUT &&
                    points[cachedIndex + 1].ut > targetUT)
                {
                    DiagnosticsState.health.waypointCacheHits++;
                    return cachedIndex;
                }

                int nextIndex = cachedIndex + 1;
                if (nextIndex < points.Count - 1 &&
                    points[nextIndex].ut <= targetUT &&
                    points[nextIndex + 1].ut > targetUT)
                {
                    cachedIndex = nextIndex;
                    DiagnosticsState.health.waypointCacheHits++;
                    return nextIndex;
                }
            }

            // Binary search fallback
            int low = 0;
            int high = points.Count - 2;

            while (low <= high)
            {
                int mid = (low + high) / 2;

                if (points[mid].ut <= targetUT && points[mid + 1].ut > targetUT)
                {
                    cachedIndex = mid;
                    DiagnosticsState.health.waypointCacheMisses++;
                    return mid;
                }
                else if (points[mid].ut > targetUT)
                {
                    high = mid - 1;
                }
                else
                {
                    low = mid + 1;
                }
            }

            // Linear fallback (shouldn't reach here)
            for (int i = 0; i < points.Count - 1; i++)
            {
                if (points[i].ut <= targetUT && points[i + 1].ut > targetUT)
                {
                    cachedIndex = i;
                    DiagnosticsState.health.waypointCacheMisses++;
                    ParsekLog.VerboseRateLimited("TrajectoryMath", "waypoint-linear-fallback-hit",
                        $"Linear fallback used for targetUT={targetUT:F3}, idx={i}", 5.0);
                    return i;
                }
            }

            ParsekLog.VerboseRateLimited("TrajectoryMath", "waypoint-index-not-found",
                $"No waypoint index found for targetUT={targetUT:F3}", 5.0);
            return -1;
        }

        /// <summary>
        /// Computes aggregate statistics for a recording.
        /// Pure function — uses bodyLookup callback for orbit segment calculations.
        /// bodyLookup("Kerbin") should return [radius, gravParameter] or null.
        /// </summary>
        internal static RecordingStats ComputeStats(
            Recording rec,
            System.Func<string, double[]> bodyLookup = null)
        {
            var stats = new RecordingStats();
            stats.pointCount = rec.Points.Count;
            stats.orbitSegmentCount = rec.OrbitSegments.Count;
            stats.partEventCount = rec.PartEvents.Count;

            if (rec.Points.Count == 0)
            {
                ParsekLog.VerboseRateLimited("TrajectoryMath", "compute-stats-empty",
                    "ComputeStats called with empty trajectory", 5.0);
                return stats;
            }

            var bodyCounts = new Dictionary<string, int>();
            double lat0 = rec.Points[0].latitude;
            double lon0 = rec.Points[0].longitude;
            string body0 = rec.Points[0].bodyName ?? "Kerbin";
            int firstPointSectionIdx = FindTrackSectionForUT(rec.TrackSections, rec.Points[0].ut);
            ReferenceFrame firstPointFrame = firstPointSectionIdx >= 0
                ? rec.TrackSections[firstPointSectionIdx].referenceFrame
                : ReferenceFrame.Absolute;

            ApplyTrackSectionAltitudeMetadata(rec.TrackSections, ref stats);

            for (int i = 0; i < rec.Points.Count; i++)
            {
                var pt = rec.Points[i];

                if (pt.altitude > stats.maxAltitude)
                    stats.maxAltitude = pt.altitude;

                float speed = pt.velocity.magnitude;
                if (speed > stats.maxSpeed)
                    stats.maxSpeed = speed;

                string body = pt.bodyName ?? "Kerbin";
                int count;
                bodyCounts.TryGetValue(body, out count);
                bodyCounts[body] = count + 1;

                if (bodyLookup != null)
                {
                    double[] bodyData = bodyLookup(body);
                    if (bodyData != null)
                    {
                        double bodyRadius = bodyData[0];

                        // Distance from previous point (same body only).
                        // Skip when both points fall inside an orbit segment
                        // to avoid double-counting distance already covered
                        // by the segment's mean-speed calculation.
                        if (i > 0 && (rec.Points[i - 1].bodyName ?? "Kerbin") == body)
                        {
                            var prev = rec.Points[i - 1];
                            double midUT = (prev.ut + pt.ut) * 0.5;
                            bool inOrbitSegment = FindOrbitSegment(rec.OrbitSegments, midUT) != null;
                            if (!inOrbitSegment)
                            {
                                int sectionIdx = FindTrackSectionForUT(rec.TrackSections, midUT);
                                ReferenceFrame frame = sectionIdx >= 0
                                    ? rec.TrackSections[sectionIdx].referenceFrame
                                    : ReferenceFrame.Absolute;

                                if (frame == ReferenceFrame.Relative)
                                {
                                    double dx = pt.latitude - prev.latitude;
                                    double dy = pt.longitude - prev.longitude;
                                    double dz = pt.altitude - prev.altitude;
                                    stats.distanceTravelled += System.Math.Sqrt(
                                        dx * dx + dy * dy + dz * dz);
                                }
                                else
                                {
                                    double avgAlt = (prev.altitude + pt.altitude) * 0.5;
                                    double surfaceDist = HaversineDistance(
                                        prev.latitude, prev.longitude,
                                        pt.latitude, pt.longitude,
                                        bodyRadius + avgAlt);
                                    double altDiff = System.Math.Abs(pt.altitude - prev.altitude);
                                    stats.distanceTravelled += System.Math.Sqrt(
                                        surfaceDist * surfaceDist + altDiff * altDiff);
                                }
                            }
                        }

                        // Max range from first point (same body only)
                        if (body == body0)
                        {
                            int pointSectionIdx = FindTrackSectionForUT(rec.TrackSections, pt.ut);
                            ReferenceFrame pointFrame = pointSectionIdx >= 0
                                ? rec.TrackSections[pointSectionIdx].referenceFrame
                                : ReferenceFrame.Absolute;
                            double range;
                            if (firstPointFrame == ReferenceFrame.Relative
                                && pointFrame == ReferenceFrame.Relative)
                            {
                                double dx = pt.latitude - lat0;
                                double dy = pt.longitude - lon0;
                                double dz = pt.altitude - rec.Points[0].altitude;
                                range = System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
                            }
                            else if (pointFrame == ReferenceFrame.Relative)
                            {
                                range = 0.0;
                            }
                            else
                            {
                                double avgAlt = (rec.Points[0].altitude + pt.altitude) * 0.5;
                                range = HaversineDistance(
                                    lat0, lon0,
                                    pt.latitude, pt.longitude,
                                    bodyRadius + avgAlt);
                            }
                            if (range > stats.maxRange)
                                stats.maxRange = range;
                        }
                    }
                }
            }

            AccumulateOrbitSegmentStats(rec.OrbitSegments, bodyLookup, ref stats);
            stats.primaryBody = DeterminePrimaryBody(bodyCounts);

            ParsekLog.Verbose("TrajectoryMath",
                $"ComputeStats complete: points={stats.pointCount} segments={stats.orbitSegmentCount} " +
                $"events={stats.partEventCount} maxAlt={stats.maxAltitude:F0} maxSpeed={stats.maxSpeed:F1} " +
                $"dist={stats.distanceTravelled:F0} range={stats.maxRange:F0} body={stats.primaryBody}");

            return stats;
        }

        private static void ApplyTrackSectionAltitudeMetadata(
            List<TrackSection> sections,
            ref RecordingStats stats)
        {
            if (sections == null)
                return;

            for (int i = 0; i < sections.Count; i++)
            {
                if (!float.IsNaN(sections[i].maxAltitude)
                    && sections[i].maxAltitude > stats.maxAltitude)
                {
                    stats.maxAltitude = sections[i].maxAltitude;
                }
            }
        }

        /// <summary>
        /// Accumulates orbit segment contributions into recording stats: apoapsis altitude,
        /// periapsis speed (vis-viva), and mean-speed distance for each segment.
        /// </summary>
        internal static void AccumulateOrbitSegmentStats(
            List<OrbitSegment> segments,
            System.Func<string, double[]> bodyLookup,
            ref RecordingStats stats)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                if (bodyLookup == null) continue;
                double[] bodyData = bodyLookup(seg.bodyName ?? "Kerbin");
                if (bodyData == null) continue;

                double bodyRadius = bodyData[0];
                double gm = bodyData[1];

                // Apoapsis altitude
                double apoRadius = seg.semiMajorAxis * (1.0 + seg.eccentricity);
                double apoAlt = apoRadius - bodyRadius;
                if (apoAlt > stats.maxAltitude)
                    stats.maxAltitude = apoAlt;

                // Periapsis speed (max orbital speed via vis-viva)
                double periRadius = seg.semiMajorAxis * (1.0 - seg.eccentricity);
                if (periRadius > 0 && seg.semiMajorAxis > 0)
                {
                    double periSpeed = System.Math.Sqrt(
                        gm * (2.0 / periRadius - 1.0 / seg.semiMajorAxis));
                    if (periSpeed > stats.maxSpeed)
                        stats.maxSpeed = periSpeed;
                }

                // Mean orbital speed * duration
                if (seg.semiMajorAxis > 0)
                {
                    double meanSpeed = System.Math.Sqrt(gm / seg.semiMajorAxis);
                    stats.distanceTravelled += meanSpeed * (seg.endUT - seg.startUT);
                }
            }
        }

        /// <summary>
        /// Returns the body name with the highest point count, or null if the dictionary is empty.
        /// </summary>
        internal static string DeterminePrimaryBody(Dictionary<string, int> bodyCounts)
        {
            string primaryBody = null;
            int maxCount = 0;
            foreach (var kvp in bodyCounts)
            {
                if (kvp.Value > maxCount)
                {
                    maxCount = kvp.Value;
                    primaryBody = kvp.Key;
                }
            }
            return primaryBody;
        }

        private static double HaversineDistance(
            double lat1Deg, double lon1Deg,
            double lat2Deg, double lon2Deg, double radius)
        {
            const double toRad = System.Math.PI / 180.0;
            double lat1 = lat1Deg * toRad;
            double lat2 = lat2Deg * toRad;
            double dlat = (lat2Deg - lat1Deg) * toRad;
            double dlon = (lon2Deg - lon1Deg) * toRad;
            double a = System.Math.Sin(dlat * 0.5) * System.Math.Sin(dlat * 0.5) +
                       System.Math.Cos(lat1) * System.Math.Cos(lat2) *
                       System.Math.Sin(dlon * 0.5) * System.Math.Sin(dlon * 0.5);
            double c = 2.0 * System.Math.Atan2(
                System.Math.Sqrt(a), System.Math.Sqrt(1.0 - a));
            return radius * c;
        }

        /// <summary>
        /// Performs point lookup and interpolation factor computation for trajectory playback.
        /// Shared core between flight and KSC interpolation paths.
        /// Returns true if a valid interpolation pair was found; false if targetUT is before
        /// the first point or the list is empty/null (caller should handle single-point fallback).
        /// When true, before/after/t are set for the caller to use with body-specific positioning.
        /// When false and the list is non-empty, before is set to points[0] for single-point fallback.
        /// </summary>
        internal static bool InterpolatePoints(
            List<TrajectoryPoint> points, ref int cachedIndex, double targetUT,
            out TrajectoryPoint before, out TrajectoryPoint after, out float t)
        {
            before = default;
            after = default;
            t = 0f;

            if (points == null || points.Count == 0)
                return false;

            int indexBefore = FindWaypointIndex(points, ref cachedIndex, targetUT);

            if (indexBefore < 0)
            {
                // Before recording start — caller should position at first point
                before = points[0];
                return false;
            }

            before = points[indexBefore];
            after = points[indexBefore + 1];

            double segmentDuration = after.ut - before.ut;
            if (segmentDuration <= 0.0001)
            {
                // Degenerate segment — treat as single point
                t = 0f;
                return true;
            }

            t = (float)((targetUT - before.ut) / segmentDuration);
            t = Mathf.Clamp01(t);
            return true;
        }

        /// <summary>
        /// Returns the nearest recorded TrajectoryPoint at the given UT, or null if the
        /// point list is empty/null or UT is before recording start. For UT past recording
        /// end, returns the last point. For mid-range UT, returns the lower bracket point.
        ///
        /// Uses the bracket point's recorded values directly (no interpolation) for
        /// orbit accuracy — same pattern as VesselSpawner. The bracket point represents
        /// the last sampled physics state, which produces a more physically correct orbit
        /// than interpolated values.
        /// </summary>
        internal static TrajectoryPoint? BracketPointAtUT(
            List<TrajectoryPoint> points, double ut, ref int cachedIndex)
        {
            if (points == null || points.Count == 0)
                return null;

            bool found = InterpolatePoints(points, ref cachedIndex, ut,
                out TrajectoryPoint before, out TrajectoryPoint after, out float t);

            if (!found)
            {
                // InterpolatePoints returns false for empty list or UT before start.
                // Empty already handled above, so this is UT before start → null.
                return null;
            }

            // Past end: t is clamped to 1 — return the upper bracket (last point).
            // Mid-range: return the lower bracket (most recent sampled state).
            return t >= 1f ? after : before;
        }

        internal static double InterpolateAltitude(double altBefore, double altAfter, float t)
        {
            return altBefore + (altAfter - altBefore) * t;
        }

        /// <summary>
        /// Phase 1 smoothing-spline math (design doc §6.1, §17.3.1). Pure
        /// uniform Catmull-Rom in (latitude, longitude, altitude) space — the
        /// same coordinate system as <see cref="TrajectoryPoint"/> for
        /// ABSOLUTE-frame body-fixed segments. <see cref="Evaluate"/> returns
        /// a <c>Vector3d(latDeg, lonDeg, altMetres)</c> that the caller hands
        /// to <c>body.GetWorldSurfacePosition</c>.
        ///
        /// <para>
        /// Longitude wrap at +/-180 deg is unwrapped before fitting and
        /// re-wrapped on evaluation — fitting through an unwrapped sequence
        /// avoids the "long way around" interpolation that would otherwise
        /// occur when consecutive samples straddle the antimeridian.
        /// </para>
        /// </summary>
        internal static class CatmullRomFit
        {
            /// <summary>
            /// Fits a uniform Catmull-Rom spline through the supplied samples'
            /// (lat, lon, alt) tuples keyed by sample UT. Rejects samples
            /// fewer than 4, non-monotonic UTs, or non-finite components and
            /// returns <c>default(SmoothingSpline)</c> with
            /// <see cref="SmoothingSpline.IsValid"/> = false plus a populated
            /// <paramref name="failureReason"/>.
            /// </summary>
            internal static SmoothingSpline Fit(
                IList<TrajectoryPoint> samples, double tension, out string failureReason)
            {
                failureReason = null;

                if (samples == null)
                {
                    failureReason = "samples list is null";
                    return default(SmoothingSpline);
                }
                if (samples.Count < 4)
                {
                    failureReason = $"need at least 4 samples; got {samples.Count}";
                    return default(SmoothingSpline);
                }

                int count = samples.Count;
                double[] knots = new double[count];
                float[] ctrlX = new float[count]; // latitude (deg)
                float[] ctrlY = new float[count]; // longitude (deg, unwrapped)
                float[] ctrlZ = new float[count]; // altitude (m)

                double prevUT = double.NegativeInfinity;
                double prevLon = double.NaN;
                for (int i = 0; i < count; i++)
                {
                    var p = samples[i];
                    if (!IsFinite(p.ut) || !IsFinite(p.latitude) || !IsFinite(p.longitude) || !IsFinite(p.altitude))
                    {
                        failureReason = $"sample {i} contains NaN or non-finite component (ut={p.ut} lat={p.latitude} lon={p.longitude} alt={p.altitude})";
                        return default(SmoothingSpline);
                    }
                    if (i > 0 && p.ut <= prevUT)
                    {
                        failureReason = $"non-monotonic UT at sample {i}: {p.ut} <= {prevUT}";
                        return default(SmoothingSpline);
                    }

                    double lon = p.longitude;
                    if (i > 0)
                    {
                        // Unwrap longitude: keep consecutive deltas within +/-180 deg so
                        // a sequence crossing the antimeridian fits as a continuous
                        // monotone progression instead of jumping by ~360 deg.
                        double delta = lon - prevLon;
                        if (delta > 180.0) lon -= 360.0;
                        else if (delta < -180.0) lon += 360.0;
                    }

                    knots[i] = p.ut;
                    ctrlX[i] = (float)p.latitude;
                    ctrlY[i] = (float)lon;
                    ctrlZ[i] = (float)p.altitude;

                    prevUT = p.ut;
                    prevLon = lon;
                }

                return new SmoothingSpline
                {
                    SplineType = 0, // Catmull-Rom
                    Tension = (float)tension,
                    KnotsUT = knots,
                    ControlsX = ctrlX,
                    ControlsY = ctrlY,
                    ControlsZ = ctrlZ,
                    FrameTag = 0, // body-fixed
                    IsValid = true,
                };
            }

            /// <summary>
            /// Evaluates the spline at <paramref name="ut"/>. Clamps to the
            /// endpoint sample (bit-exact at <c>ut == knots[0]</c> and
            /// <c>ut == knots[Last]</c>); never extrapolates. Returns
            /// <c>(latDeg, lonDeg, altMetres)</c> in body-fixed coordinates,
            /// re-wrapped to <c>[-180, 180]</c> for longitude.
            /// </summary>
            internal static Vector3d Evaluate(in SmoothingSpline spline, double ut)
            {
                if (!spline.IsValid || spline.KnotsUT == null || spline.KnotsUT.Length < 2)
                    return Vector3d.zero;

                int n = spline.KnotsUT.Length;

                // Endpoint clamps: return raw control values bit-exact so
                // anchor placement at section boundaries does not drift.
                if (ut <= spline.KnotsUT[0])
                {
                    return new Vector3d(
                        spline.ControlsX[0],
                        WrapLongitude(spline.ControlsY[0]),
                        spline.ControlsZ[0]);
                }
                if (ut >= spline.KnotsUT[n - 1])
                {
                    return new Vector3d(
                        spline.ControlsX[n - 1],
                        WrapLongitude(spline.ControlsY[n - 1]),
                        spline.ControlsZ[n - 1]);
                }

                // Locate the segment [i, i+1] containing ut via linear scan
                // (annotation tables are short — typical recording sections
                // hold tens to a few hundred samples).
                int i = 0;
                for (int k = 0; k < n - 1; k++)
                {
                    if (ut >= spline.KnotsUT[k] && ut < spline.KnotsUT[k + 1])
                    {
                        i = k;
                        break;
                    }
                }

                int i0 = i - 1; if (i0 < 0) i0 = 0;
                int i1 = i;
                int i2 = i + 1;
                int i3 = i + 2; if (i3 >= n) i3 = n - 1;

                double segDuration = spline.KnotsUT[i2] - spline.KnotsUT[i1];
                double t = segDuration > 0 ? (ut - spline.KnotsUT[i1]) / segDuration : 0.0;
                if (t < 0) t = 0; else if (t > 1) t = 1;

                float tension = spline.Tension;
                double x = CatmullRomScalar(spline.ControlsX[i0], spline.ControlsX[i1], spline.ControlsX[i2], spline.ControlsX[i3], t, tension);
                double y = CatmullRomScalar(spline.ControlsY[i0], spline.ControlsY[i1], spline.ControlsY[i2], spline.ControlsY[i3], t, tension);
                double z = CatmullRomScalar(spline.ControlsZ[i0], spline.ControlsZ[i1], spline.ControlsZ[i2], spline.ControlsZ[i3], t, tension);

                return new Vector3d(x, WrapLongitude(y), z);
            }

            // Standard uniform Catmull-Rom on a single segment with tangents
            // m1 = tension * (P2 - P0) and m2 = tension * (P3 - P1). For the
            // canonical Catmull-Rom curve, tension = 0.5 (so m1 = (P2-P0)/2).
            // Hermite basis: h00=2t^3-3t^2+1, h10=t^3-2t^2+t, h01=-2t^3+3t^2,
            // h11=t^3-t^2.
            private static double CatmullRomScalar(double p0, double p1, double p2, double p3, double t, double tension)
            {
                double t2 = t * t;
                double t3 = t2 * t;
                double m1 = tension * (p2 - p0);
                double m2 = tension * (p3 - p1);
                double h00 = 2.0 * t3 - 3.0 * t2 + 1.0;
                double h10 = t3 - 2.0 * t2 + t;
                double h01 = -2.0 * t3 + 3.0 * t2;
                double h11 = t3 - t2;
                return h00 * p1 + h10 * m1 + h01 * p2 + h11 * m2;
            }

            private static double WrapLongitude(double lonDeg)
            {
                // Map any longitude back into (-180, 180]. Robust against
                // multiple wraps if the unwrap accumulated more than +/-360.
                double wrapped = lonDeg % 360.0;
                if (wrapped > 180.0) wrapped -= 360.0;
                else if (wrapped <= -180.0) wrapped += 360.0;
                return wrapped;
            }

            private static bool IsFinite(double value)
            {
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }
        }

        /// <summary>
        /// Phase 4 frame transformation (design doc §6.2 Stage 2 frame table,
        /// §18 Phase 4, §26.1 HR-9). Lifts body-fixed (lat, lon, alt) at the
        /// recording UT into "inertial-longitude" coordinates by adding the
        /// body's sidereal rotation phase, and lowers the inverse at the
        /// playback UT by subtracting the playback-time phase before handing
        /// to <c>body.GetWorldSurfacePosition</c>.
        ///
        /// <para>
        /// Formulation B: longitude unwrap by body rotation phase. Reuses
        /// the existing <c>IncompleteBallisticSceneExitFinalizer</c> formula
        /// (<c>longitude offset = (ut - referenceUT) * 360 / rotationPeriod</c>).
        /// <c>body.initialRotation</c> is intentionally omitted — both Lift
        /// and Lower add/subtract the same offset, so it cancels.
        /// </para>
        ///
        /// <para>
        /// Pure functions (HR-3): same inputs → same outputs, no hidden state.
        /// Null body / non-finite or zero <c>rotationPeriod</c> degrade
        /// gracefully (HR-9): Lift returns body-fixed unchanged and emits a
        /// <c>Pipeline-Frame</c> Warn so the failure is visible in KSP.log.
        /// EXO sections on tidally-locked / anomalous bodies render as
        /// body-fixed in that case.
        /// </para>
        /// </summary>
        internal static class FrameTransform
        {
            /// <summary>Test seam: when set, returned in place of
            /// <c>body.rotationPeriod</c>. xUnit can't realistically construct
            /// fully-initialised <see cref="CelestialBody"/> instances, so
            /// tests inject a synthetic period via this hook. Production
            /// callers leave it null and read the live field. Reset in test
            /// Dispose via <see cref="ResetForTesting"/>.</summary>
            internal static System.Func<CelestialBody, double> RotationPeriodForTesting;

            /// <summary>Test seam: when set, returned in place of
            /// <c>body.GetWorldSurfacePosition(lat, lon, alt)</c>. xUnit can't
            /// drive the live PQS lookup, so tests inject a deterministic
            /// surface-to-world mapping via this hook. Reset in Dispose via
            /// <see cref="ResetForTesting"/>.</summary>
            internal static System.Func<CelestialBody, double, double, double, Vector3d> WorldSurfacePositionForTesting;

            /// <summary>Test-only: clears any injected seams.</summary>
            internal static void ResetForTesting()
            {
                RotationPeriodForTesting = null;
                WorldSurfacePositionForTesting = null;
            }

            /// <summary>
            /// Sidereal phase advance in degrees from <c>UT == 0</c>:
            /// <c>(ut * 360 / rotationPeriod)</c>. Returns <c>0</c> when the
            /// body is null or its rotation period is non-finite / zero —
            /// callers treat that as "no inertial lift needed" (HR-9).
            /// </summary>
            internal static double RotationAngleAtUT(CelestialBody body, double ut)
            {
                if (object.ReferenceEquals(body, null))
                    return 0.0;
                double period = ResolveRotationPeriod(body);
                if (double.IsNaN(period) || double.IsInfinity(period) || System.Math.Abs(period) <= double.Epsilon)
                    return 0.0;
                if (double.IsNaN(ut) || double.IsInfinity(ut))
                    return 0.0;
                return (ut * 360.0) / period;
            }

            /// <summary>
            /// Lifts body-fixed <c>(lat, lon, alt)</c> at <paramref name="recordedUT"/>
            /// to inertial-longitude <c>(lat, inertialLon, alt)</c>. Inertial
            /// longitude is wrapped to <c>(-180, 180]</c>. Null body or
            /// non-finite / zero rotation period is a no-op (returns the
            /// body-fixed input) and emits a <c>Pipeline-Frame</c> Warn (HR-9).
            /// </summary>
            internal static Vector3d LiftToInertial(double latDeg, double lonDeg, double altMeters,
                CelestialBody body, double recordedUT)
            {
                if (object.ReferenceEquals(body, null))
                {
                    ParsekLog.Warn("Pipeline-Frame",
                        $"LiftToInertial degraded to body-fixed: body=null recordedUT={recordedUT}");
                    return new Vector3d(latDeg, WrapLongitudeDegrees(lonDeg), altMeters);
                }
                double period = ResolveRotationPeriod(body);
                if (double.IsNaN(period) || double.IsInfinity(period) || System.Math.Abs(period) <= double.Epsilon)
                {
                    ParsekLog.Warn("Pipeline-Frame",
                        $"LiftToInertial degraded to body-fixed: body={body.bodyName} rotationPeriod={period} recordedUT={recordedUT}");
                    return new Vector3d(latDeg, WrapLongitudeDegrees(lonDeg), altMeters);
                }

                double phase = (recordedUT * 360.0) / period;
                double inertialLon = WrapLongitudeDegrees(lonDeg + phase);
                return new Vector3d(latDeg, inertialLon, altMeters);
            }

            /// <summary>
            /// Lowers <c>(lat, inertialLon, alt)</c> at <paramref name="playbackUT"/>
            /// back to a world position via <c>body.GetWorldSurfacePosition</c>.
            /// The inverse of <see cref="LiftToInertial"/>: subtracts the
            /// playback-time rotation phase from the inertial longitude (with
            /// wrap to <c>(-180, 180]</c>) before the surface lookup. Null
            /// body returns <c>Vector3d.zero</c> and emits a
            /// <c>Pipeline-Frame</c> Warn (HR-9).
            /// </summary>
            internal static Vector3d LowerFromInertialToWorld(double latDeg, double inertialLonDeg, double altMeters,
                CelestialBody body, double playbackUT)
            {
                if (object.ReferenceEquals(body, null))
                {
                    ParsekLog.Warn("Pipeline-Frame",
                        $"LowerFromInertialToWorld degraded to zero: body=null playbackUT={playbackUT}");
                    return Vector3d.zero;
                }

                double period = ResolveRotationPeriod(body);
                double phase = 0.0;
                if (!double.IsNaN(period) && !double.IsInfinity(period) && System.Math.Abs(period) > double.Epsilon
                    && !double.IsNaN(playbackUT) && !double.IsInfinity(playbackUT))
                {
                    phase = (playbackUT * 360.0) / period;
                }

                double bodyFixedLon = WrapLongitudeDegrees(inertialLonDeg - phase);
                return ResolveWorldSurfacePosition(body, latDeg, bodyFixedLon, altMeters);
            }

            /// <summary>
            /// Phase 4 frame-aware dispatch (design doc §6.2 Stage 2, §18 Phase
            /// 4, §26.1 HR-9). Resolves a smoothed <c>(lat, lon, alt)</c>
            /// spline sample to a world position based on the spline's
            /// <c>FrameTag</c> contract:
            /// <list type="bullet">
            ///   <item>Tag 0 (body-fixed) — straight <c>GetWorldSurfacePosition</c>.</item>
            ///   <item>Tag 1 (inertial-longitude) — re-lower via
            ///     <see cref="LowerFromInertialToWorld"/> at the playback UT.</item>
            ///   <item>Anything else — HR-9 visible failure: emits a
            ///     <c>Pipeline-Smoothing</c> Warn (gated by <paramref name="warnedKeys"/>
            ///     when supplied so a degenerate recording can't flood the log)
            ///     and returns NaN so the caller's outer guard falls back to
            ///     the legacy lerp.</item>
            /// </list>
            /// Extracted from <c>ParsekFlight.InterpolateAndPosition</c> so
            /// the unknown-tag branch can be exercised in xUnit without Unity.
            /// </summary>
            internal static Vector3d DispatchSplineWorldByFrameTag(byte frameTag,
                double latDeg, double lonDeg, double altMeters,
                CelestialBody body, double playbackUT,
                string recordingId, int sectionIndex,
                System.Collections.Generic.HashSet<string> warnedKeys = null)
            {
                switch (frameTag)
                {
                    case 0:
                        return ResolveWorldSurfacePosition(body, latDeg, lonDeg, altMeters);
                    case 1:
                        return LowerFromInertialToWorld(latDeg, lonDeg, altMeters, body, playbackUT);
                    default:
                    {
                        // HR-9: visible failure for an unrecognised tag.
                        // Emits Warn (not VerboseRateLimited) so a programmer
                        // error or a v1 .pann slipping past the gates surfaces
                        // in stock logs. The optional warnedKeys dedup gates
                        // a single (recordingId, sectionIndex) pair to one Warn
                        // per session — a degenerate recording with an unknown
                        // tag at every frame can't flood the log, but each
                        // distinct unknown-tag occurrence is still visible.
                        bool emit = true;
                        if (warnedKeys != null)
                        {
                            string key = recordingId + ":" + sectionIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            emit = warnedKeys.Add(key);
                        }
                        if (emit)
                        {
                            ParsekLog.Warn("Pipeline-Smoothing",
                                $"unknown frameTag={frameTag} recordingId={recordingId} sectionIndex={sectionIndex} -- falling back to legacy bracket interpolation");
                        }
                        return new Vector3d(double.NaN, double.NaN, double.NaN);
                    }
                }
            }

            private static double ResolveRotationPeriod(CelestialBody body)
            {
                var seam = RotationPeriodForTesting;
                if (seam != null)
                    return seam(body);
                return body.rotationPeriod;
            }

            private static Vector3d ResolveWorldSurfacePosition(CelestialBody body,
                double latDeg, double lonDeg, double altMeters)
            {
                var seam = WorldSurfacePositionForTesting;
                if (seam != null)
                    return seam(body, latDeg, lonDeg, altMeters);
                return body.GetWorldSurfacePosition(latDeg, lonDeg, altMeters);
            }

            private static double WrapLongitudeDegrees(double lonDeg)
            {
                // Match the existing CatmullRomFit.WrapLongitude contract so
                // body-fixed and inertial longitudes share a single canonical
                // (-180, 180] range.
                if (double.IsNaN(lonDeg) || double.IsInfinity(lonDeg))
                    return lonDeg;
                double wrapped = lonDeg % 360.0;
                if (wrapped > 180.0) wrapped -= 360.0;
                else if (wrapped <= -180.0) wrapped += 360.0;
                return wrapped;
            }
        }

        /// <summary>
        /// Sanitize a quaternion by replacing NaN/Infinity with safe values
        /// and normalizing. Returns identity if magnitude is near-zero.
        /// </summary>
        internal static Quaternion SanitizeQuaternion(Quaternion q)
        {
            bool hadBadComponent = false;
            if (float.IsNaN(q.x) || float.IsInfinity(q.x)) { q.x = 0; hadBadComponent = true; }
            if (float.IsNaN(q.y) || float.IsInfinity(q.y)) { q.y = 0; hadBadComponent = true; }
            if (float.IsNaN(q.z) || float.IsInfinity(q.z)) { q.z = 0; hadBadComponent = true; }
            if (float.IsNaN(q.w) || float.IsInfinity(q.w)) { q.w = 1; hadBadComponent = true; }

            if (hadBadComponent)
                ParsekLog.VerboseRateLimited("TrajectoryMath", "sanitize-quat",
                    "SanitizeQuaternion replaced NaN/Infinity component(s)");

            float magnitude = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (float.IsNaN(magnitude) || float.IsInfinity(magnitude) || magnitude < 0.001f)
            {
                ParsekLog.VerboseRateLimited("TrajectoryMath", "sanitize-quat-identity",
                    "SanitizeQuaternion returned identity (near-zero magnitude)");
                return Quaternion.identity;
            }

            return new Quaternion(q.x / magnitude, q.y / magnitude, q.z / magnitude, q.w / magnitude);
        }

        /// <summary>
        /// Canonicalizes quaternions for angle comparisons so sign-equivalent values
        /// (q and -q) compare as the same physical rotation.
        /// </summary>
        internal static Quaternion NormalizeQuaternionForComparison(Quaternion q)
        {
            Quaternion normalized = PureNormalize(SanitizeQuaternion(q));
            return normalized.w < 0f
                ? new Quaternion(-normalized.x, -normalized.y, -normalized.z, -normalized.w)
                : normalized;
        }

        /// <summary>
        /// Returns the physical angle between two rotations in degrees after sanitizing
        /// and canonicalizing sign-equivalent quaternions.
        /// </summary>
        internal static float ComputeQuaternionAngleDegrees(Quaternion from, Quaternion to)
        {
            return Quaternion.Angle(
                NormalizeQuaternionForComparison(from),
                NormalizeQuaternionForComparison(to));
        }

        /// <summary>Spin threshold in rad/s (matches PersistentRotation's threshold).</summary>
        internal const float SpinThreshold = 0.05f;

        /// <summary>
        /// Returns true if the segment has recorded orbital-frame rotation data.
        /// Default struct value (0,0,0,0) = no data.
        /// </summary>
        internal static bool HasOrbitalFrameRotation(OrbitSegment seg)
            => seg.orbitalFrameRotation.x != 0f || seg.orbitalFrameRotation.y != 0f
            || seg.orbitalFrameRotation.z != 0f || seg.orbitalFrameRotation.w != 0f;

        /// <summary>
        /// Returns true if the segment has spin data (angular velocity above threshold).
        /// </summary>
        internal static bool IsSpinning(OrbitSegment seg)
            => seg.angularVelocity.sqrMagnitude > SpinThreshold * SpinThreshold;

        /// <summary>
        /// Computes vessel rotation relative to the orbital velocity frame.
        /// Returns Inverse(orbFrame) * worldRotation.
        /// Returns identity if velocity is near-zero (degenerate frame).
        /// Falls back to LookRotation(velocity) without up hint if velocity
        /// and radialOut are near-parallel (dot > 0.99).
        /// Uses pure-math quaternion operations (no Unity native calls) for testability.
        /// </summary>
        internal static Quaternion ComputeOrbitalFrameRotation(
            Quaternion worldRotation, Vector3d orbitalVelocity, Vector3d radialOut)
        {
            if (orbitalVelocity.sqrMagnitude < 0.001)
            {
                ParsekLog.VerboseRateLimited("TrajectoryMath", "ofr-degenerate-velocity",
                    $"Orbital-frame rotation: degenerate velocity (sqrMag={orbitalVelocity.sqrMagnitude:F6}), using identity");
                return Quaternion.identity;
            }

            Vector3 velNorm = ((Vector3)orbitalVelocity).normalized;
            Vector3 radNorm = ((Vector3)radialOut).normalized;
            float dot = Vector3.Dot(velNorm, radNorm);

            Quaternion orbFrame;
            if (Mathf.Abs(dot) > 0.99f)
            {
                ParsekLog.VerboseRateLimited("TrajectoryMath", "ofr-near-parallel",
                    $"Orbital-frame rotation: velocity/radialOut near-parallel (dot={dot:F4}), frame approximated");
                orbFrame = PureLookRotation(velNorm, Vector3.up);
            }
            else
            {
                orbFrame = PureLookRotation(velNorm, radNorm);
            }

            return PureMultiply(PureInverse(orbFrame), worldRotation);
        }

        // --- Pure-math quaternion helpers (no Unity native calls) ---

        /// <summary>
        /// Pure-math LookRotation: builds a rotation from forward and up vectors.
        /// Equivalent to Quaternion.LookRotation but uses only managed code.
        /// </summary>
        internal static Quaternion PureLookRotation(Vector3 forward, Vector3 up)
        {
            forward = forward.normalized;
            if (forward.sqrMagnitude < 1e-6f) return Quaternion.identity;

            Vector3 right = Vector3.Cross(up, forward).normalized;
            if (right.sqrMagnitude < 1e-6f)
            {
                up = Mathf.Abs(forward.y) < 0.9f ? Vector3.up : Vector3.right;
                right = Vector3.Cross(up, forward).normalized;
            }
            up = Vector3.Cross(forward, right);

            float m00 = right.x, m01 = up.x, m02 = forward.x;
            float m10 = right.y, m11 = up.y, m12 = forward.y;
            float m20 = right.z, m21 = up.z, m22 = forward.z;

            float trace = m00 + m11 + m22;
            Quaternion q;
            if (trace > 0)
            {
                float s = Mathf.Sqrt(trace + 1f) * 2f;
                q = new Quaternion(
                    (m21 - m12) / s,
                    (m02 - m20) / s,
                    (m10 - m01) / s,
                    s / 4f);
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = Mathf.Sqrt(1f + m00 - m11 - m22) * 2f;
                q = new Quaternion(
                    s / 4f,
                    (m01 + m10) / s,
                    (m02 + m20) / s,
                    (m21 - m12) / s);
            }
            else if (m11 > m22)
            {
                float s = Mathf.Sqrt(1f + m11 - m00 - m22) * 2f;
                q = new Quaternion(
                    (m01 + m10) / s,
                    s / 4f,
                    (m12 + m21) / s,
                    (m02 - m20) / s);
            }
            else
            {
                float s = Mathf.Sqrt(1f + m22 - m00 - m11) * 2f;
                q = new Quaternion(
                    (m02 + m20) / s,
                    (m12 + m21) / s,
                    s / 4f,
                    (m10 - m01) / s);
            }
            return PureNormalize(q);
        }

        /// <summary>Pure-math quaternion inverse (conjugate / sqrMagnitude).</summary>
        internal static Quaternion PureInverse(Quaternion q)
        {
            float sqrMag = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (sqrMag < 1e-12f) return Quaternion.identity;
            float inv = 1f / sqrMag;
            return new Quaternion(-q.x * inv, -q.y * inv, -q.z * inv, q.w * inv);
        }

        /// <summary>Pure-math quaternion multiplication.</summary>
        internal static Quaternion PureMultiply(Quaternion a, Quaternion b)
        {
            return new Quaternion(
                a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
                a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
                a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
                a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
        }

        /// <summary>Pure-math quaternion normalization.</summary>
        internal static Quaternion PureNormalize(Quaternion q)
        {
            float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (mag < 1e-6f) return Quaternion.identity;
            return new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
        }

        /// <summary>
        /// Pure-math quaternion slerp equivalent to Quaternion.Slerp without Unity native calls.
        /// </summary>
        internal static Quaternion PureSlerp(Quaternion from, Quaternion to, float t)
        {
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;

            from = PureNormalize(SanitizeQuaternion(from));
            to = PureNormalize(SanitizeQuaternion(to));

            float dot =
                from.x * to.x +
                from.y * to.y +
                from.z * to.z +
                from.w * to.w;
            if (dot < 0f)
            {
                to = new Quaternion(-to.x, -to.y, -to.z, -to.w);
                dot = -dot;
            }

            if (dot > 0.9995f)
            {
                return PureNormalize(new Quaternion(
                    from.x + (to.x - from.x) * t,
                    from.y + (to.y - from.y) * t,
                    from.z + (to.z - from.z) * t,
                    from.w + (to.w - from.w) * t));
            }

            if (dot > 1f) dot = 1f;
            double theta0 = System.Math.Acos(dot);
            double theta = theta0 * t;
            double sinTheta = System.Math.Sin(theta);
            double sinTheta0 = System.Math.Sin(theta0);

            double s0 = System.Math.Cos(theta) - dot * sinTheta / sinTheta0;
            double s1 = sinTheta / sinTheta0;
            return PureNormalize(new Quaternion(
                (float)(from.x * s0 + to.x * s1),
                (float)(from.y * s0 + to.y * s1),
                (float)(from.z * s0 + to.z * s1),
                (float)(from.w * s0 + to.w * s1)));
        }

        /// <summary>Pure-math AngleAxis rotation.</summary>
        internal static Quaternion PureAngleAxis(float angleDeg, Vector3 axis)
        {
            float mag = axis.magnitude;
            if (mag < 1e-6f) return Quaternion.identity;
            axis = axis / mag;
            float halfRad = angleDeg * Mathf.Deg2Rad * 0.5f;
            float s = Mathf.Sin(halfRad);
            float c = Mathf.Cos(halfRad);
            return new Quaternion(axis.x * s, axis.y * s, axis.z * s, c);
        }

        /// <summary>Pure-math: rotate a vector by a quaternion (q * v * q^-1).</summary>
        internal static Vector3 PureRotateVector(Quaternion q, Vector3 v)
        {
            float qx = q.x, qy = q.y, qz = q.z, qw = q.w;
            float tx = 2f * (qy * v.z - qz * v.y);
            float ty = 2f * (qz * v.x - qx * v.z);
            float tz = 2f * (qx * v.y - qy * v.x);
            return new Vector3(
                v.x + qw * tx + (qy * tz - qz * ty),
                v.y + qw * ty + (qz * tx - qx * tz),
                v.z + qw * tz + (qx * ty - qy * tx));
        }

        /// <summary>
        /// Computes the position offset from an anchor vessel to the focused vessel
        /// in world-space coordinates for legacy v5-and-older RELATIVE sections.
        ///
        /// The returned (dx, dy, dz) vector is stored in the TrajectoryPoint's
        /// latitude/longitude/altitude fields when recording in RELATIVE frame.
        /// Pure static method for testability.
        /// </summary>
        internal static Vector3d ComputeRelativeOffset(Vector3d focusedPosition, Vector3d anchorPosition)
        {
            return focusedPosition - anchorPosition;
        }

        /// <summary>
        /// Computes world position from anchor position and a legacy world-space
        /// relative offset. Pure static for testability.
        /// </summary>
        internal static Vector3d ApplyRelativeOffset(Vector3d anchorWorldPos, double dx, double dy, double dz)
        {
            return new Vector3d(anchorWorldPos.x + dx, anchorWorldPos.y + dy, anchorWorldPos.z + dz);
        }

        /// <summary>
        /// Computes the anchor-local offset used by format-v6 RELATIVE sections.
        /// Pure static method for testability.
        /// </summary>
        internal static Vector3d ComputeRelativeLocalOffset(
            Vector3d focusedPosition,
            Vector3d anchorPosition,
            Quaternion anchorWorldRotation)
        {
            Vector3 worldOffset = (Vector3)(focusedPosition - anchorPosition);
            Quaternion inverseAnchor = PureInverse(PureNormalize(anchorWorldRotation));
            Vector3 localOffset = PureRotateVector(inverseAnchor, worldOffset);
            return new Vector3d(localOffset.x, localOffset.y, localOffset.z);
        }

        /// <summary>
        /// Computes world position from anchor position and a format-v6 anchor-local
        /// offset. Pure static for testability.
        /// </summary>
        internal static Vector3d ApplyRelativeLocalOffset(
            Vector3d anchorWorldPos,
            Quaternion anchorWorldRotation,
            double dx,
            double dy,
            double dz)
        {
            Vector3 localOffset = new Vector3((float)dx, (float)dy, (float)dz);
            Vector3 worldOffset = PureRotateVector(
                PureNormalize(anchorWorldRotation),
                localOffset);
            return anchorWorldPos + (Vector3d)worldOffset;
        }

        /// <summary>
        /// Computes the anchor-local rotation used by format-v6 RELATIVE sections.
        /// Pure static method for testability.
        /// </summary>
        internal static Quaternion ComputeRelativeLocalRotation(
            Quaternion focusWorldRotation,
            Quaternion anchorWorldRotation)
        {
            Quaternion anchorInverse = PureInverse(PureNormalize(anchorWorldRotation));
            return SanitizeQuaternion(PureMultiply(anchorInverse, focusWorldRotation));
        }

        /// <summary>
        /// Reconstructs world rotation from a format-v6 anchor-local RELATIVE rotation.
        /// Pure static for testability.
        /// </summary>
        internal static Quaternion ApplyRelativeLocalRotation(
            Quaternion anchorWorldRotation,
            Quaternion relativeLocalRotation)
        {
            return SanitizeQuaternion(
                PureMultiply(PureNormalize(anchorWorldRotation), relativeLocalRotation));
        }

        /// <summary>
        /// Resolves a RELATIVE-frame position to world space using the version-specific
        /// contract for the recording being played back.
        /// </summary>
        internal static Vector3d ResolveRelativePlaybackPosition(
            Vector3d anchorWorldPos,
            Quaternion anchorWorldRotation,
            double dx,
            double dy,
            double dz,
            int recordingFormatVersion)
        {
            return RecordingStore.UsesRelativeLocalFrameContract(recordingFormatVersion)
                ? ApplyRelativeLocalOffset(anchorWorldPos, anchorWorldRotation, dx, dy, dz)
                : ApplyRelativeOffset(anchorWorldPos, dx, dy, dz);
        }

        /// <summary>
        /// Resolves a RELATIVE-frame rotation to world space.
        /// Legacy v5-and-older RELATIVE sections stored <c>v.srfRelRotation</c> as the
        /// "relative" slot; v6 RELATIVE sections store <c>Inverse(anchor) * focus</c>.
        /// Both contracts reconstitute with the same <c>anchor * stored</c> formula —
        /// the semantic difference lives at sample time, not playback time — so this
        /// resolver takes no format-version parameter and is shared across v5 and v6.
        /// </summary>
        internal static Quaternion ResolveRelativePlaybackRotation(
            Quaternion anchorWorldRotation,
            Quaternion storedRelativeRotation)
        {
            return ApplyRelativeLocalRotation(
                anchorWorldRotation,
                SanitizeQuaternion(storedRelativeRotation));
        }

        /// <summary>
        /// Finds the TrackSection covering the given UT.
        /// Returns the index into the sections list, or -1 if none found.
        /// Linear scan — the list is typically small (a handful of sections per recording).
        /// Pure static for testability.
        /// </summary>
        internal static int FindTrackSectionForUT(List<TrackSection> sections, double ut)
        {
            if (sections == null) return -1;
            for (int i = 0; i < sections.Count; i++)
            {
                // Last section uses inclusive end; others use exclusive end
                bool inRange = (i == sections.Count - 1)
                    ? (ut >= sections[i].startUT && ut <= sections[i].endUT)
                    : (ut >= sections[i].startUT && ut < sections[i].endUT);
                if (inRange)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Returns true if the TrackSection covering the given UT has a surface environment
        /// (SurfaceMobile or SurfaceStationary). Surface vessels should not use orbit segment
        /// interpolation — their Keplerian orbit is a sub-surface path through the planet.
        /// </summary>
        internal static bool IsSurfaceAtUT(List<TrackSection> sections, double ut)
        {
            int idx = FindTrackSectionForUT(sections, ut);
            if (idx < 0) return false;
            var env = sections[idx].environment;
            return env == SegmentEnvironment.SurfaceMobile || env == SegmentEnvironment.SurfaceStationary;
        }

        /// <summary>
        /// Speed floor for reentry FX build. A trajectory whose peak recorded velocity
        /// magnitude is below this threshold and that has no orbit segments cannot ever
        /// produce reentry visuals — Mach 2.5 thermal FX requires ~675 m/s even in thin
        /// atmosphere on Kerbin, and real reentry on Laythe/Duna/Eve is higher still.
        /// 400 m/s is well under Mach 1.5 anywhere, giving a safe cutoff for stationary
        /// part showcases, EVA walks, slow rovers, and low-speed suborbital hops.
        /// </summary>
        internal const float ReentryPotentialSpeedFloor = 400f;

        /// <summary>
        /// Returns true if the trajectory could plausibly produce reentry visuals during
        /// playback. Used to gate the expensive per-spawn reentry FX build
        /// (<see cref="GhostVisualBuilder.TryBuildReentryFx"/>), which combines all ghost
        /// meshes, allocates a ParticleSystem, and clones glow materials — costs that
        /// multiply across every loop-cycle rebuild of every active ghost.
        ///
        /// Returns true if:
        ///   - the trajectory has any orbit segments (orbital ghosts always de-orbit at
        ///     high speed), OR
        ///   - any recorded trajectory point has velocity magnitude at or above
        ///     <see cref="ReentryPotentialSpeedFloor"/>.
        ///
        /// <b>Velocity frame:</b> <see cref="TrajectoryPoint.velocity"/> is the
        /// Krakensbane-corrected `rb_velocityD + Krakensbane.GetFrameVelocity()` captured
        /// at sample time. In KSP's body-co-rotating world frame this is effectively the
        /// vessel's inertial speed, so a landed/stationary vessel reads ≈0 and the 400 m/s
        /// floor safely excludes showcases, walks, rovers, and low-speed suborbital hops
        /// while preserving every supersonic / orbital trajectory. NaN components in a
        /// point fail the ≥ comparison harmlessly and do not throw.
        ///
        /// Pure function — no Unity dependencies, no side effects. O(n) in trajectory
        /// point count; called once per ghost build, not per frame.
        /// </summary>
        internal static bool HasReentryPotential(IPlaybackTrajectory traj)
        {
            if (traj == null) return false;

            // Orbital ghosts always re-enter at high speed on de-orbit.
            if (traj.HasOrbitSegments) return true;

            var points = traj.Points;
            if (points == null) return false;

            float floorSq = ReentryPotentialSpeedFloor * ReentryPotentialSpeedFloor;
            for (int i = 0; i < points.Count; i++)
            {
                // sqrMagnitude with a NaN component yields NaN; `NaN >= floorSq` is false,
                // so malformed points cannot produce a false positive.
                if (points[i].velocity.sqrMagnitude >= floorSq)
                    return true;
            }
            return false;
        }
    }
}
