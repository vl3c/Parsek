using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
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
                return -1;

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
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Sanitize a quaternion by replacing NaN/Infinity with safe values
        /// and normalizing. Returns identity if magnitude is near-zero.
        /// </summary>
        internal static Quaternion SanitizeQuaternion(Quaternion q)
        {
            if (float.IsNaN(q.x) || float.IsInfinity(q.x)) q.x = 0;
            if (float.IsNaN(q.y) || float.IsInfinity(q.y)) q.y = 0;
            if (float.IsNaN(q.z) || float.IsInfinity(q.z)) q.z = 0;
            if (float.IsNaN(q.w) || float.IsInfinity(q.w)) q.w = 1;

            float magnitude = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (float.IsNaN(magnitude) || float.IsInfinity(magnitude) || magnitude < 0.001f)
            {
                return Quaternion.identity;
            }

            return new Quaternion(q.x / magnitude, q.y / magnitude, q.z / magnitude, q.w / magnitude);
        }
    }
}
