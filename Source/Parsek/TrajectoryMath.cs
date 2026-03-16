using System.Collections.Generic;
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
        /// Decides whether to record a trajectory point based on velocity changes
        /// and a max-interval backstop. Pure function for testability.
        /// </summary>
        internal static bool ShouldRecordPoint(
            Vector3 currentVelocity, Vector3 lastVelocity,
            double currentUT, double lastRecordedUT,
            float maxInterval, float velDirThreshold, float speedThreshold)
        {
            // Always record the first point
            if (lastRecordedUT < 0)
                return true;

            // Max interval backstop — always record after this long
            if (currentUT - lastRecordedUT >= maxInterval)
                return true;

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
                    return cachedIndex;
                }

                int nextIndex = cachedIndex + 1;
                if (nextIndex < points.Count - 1 &&
                    points[nextIndex].ut <= targetUT &&
                    points[nextIndex + 1].ut > targetUT)
                {
                    cachedIndex = nextIndex;
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

                        // Max range from first point (same body only)
                        if (body == body0)
                        {
                            double avgAlt = (rec.Points[0].altitude + pt.altitude) * 0.5;
                            double range = HaversineDistance(
                                lat0, lon0,
                                pt.latitude, pt.longitude,
                                bodyRadius + avgAlt);
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

        internal static double InterpolateAltitude(double altBefore, double altAfter, float t)
        {
            return altBefore + (altAfter - altBefore) * t;
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
    }
}
