using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Pure static methods for computing vessel bounding boxes and detecting
    /// spawn-time overlap with loaded vessels. Used by the chain-tip spawn path
    /// to prevent physics explosions from overlapping vessel placement.
    /// </summary>
    internal static class SpawnCollisionDetector
    {
        private const string Tag = "SpawnCollision";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Returns true for vessel types that should be excluded from spawn collision
        /// and proximity checks: Debris, EVA, Flag, SpaceObject.
        /// </summary>
        internal static bool ShouldSkipVesselType(VesselType type)
        {
            return type == VesselType.Debris ||
                   type == VesselType.EVA ||
                   type == VesselType.Flag ||
                   type == VesselType.SpaceObject;
        }

        /// <summary>Default half-extent per part when actual size is unknown.</summary>
        private const float DefaultPartHalfExtent = 1.25f;

        /// <summary>Fallback bounds size (full side length) when snapshot has no parts.</summary>
        private const float FallbackBoundsSize = 2f;

        // ────────────────────────────────────────────────────────────
        //  Pure methods (unit-testable)
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Computes an axis-aligned bounding box that encloses all parts.
        /// Each part contributes a cube of side 2*halfExtent centered at localPos.
        /// Empty list returns zero-size bounds at origin.
        /// </summary>
        internal static Bounds ComputeBoundsFromParts(List<(Vector3 localPos, float halfExtent)> parts)
        {
            if (parts == null || parts.Count == 0)
            {
                ParsekLog.Verbose(Tag, "ComputeBoundsFromParts: empty part list — returning zero bounds");
                return new Bounds(Vector3.zero, Vector3.zero);
            }

            var first = parts[0];
            Bounds bounds = new Bounds(first.localPos, Vector3.one * first.halfExtent * 2f);

            for (int i = 1; i < parts.Count; i++)
            {
                var part = parts[i];
                Bounds partBounds = new Bounds(part.localPos, Vector3.one * part.halfExtent * 2f);
                bounds.Encapsulate(partBounds);
            }

            ParsekLog.Verbose(Tag,
                $"ComputeBoundsFromParts: {parts.Count} parts — center=({bounds.center.x.ToString("F2", IC)},{bounds.center.y.ToString("F2", IC)},{bounds.center.z.ToString("F2", IC)}) " +
                $"size=({bounds.size.x.ToString("F2", IC)},{bounds.size.y.ToString("F2", IC)},{bounds.size.z.ToString("F2", IC)})");

            return bounds;
        }

        /// <summary>
        /// Reads PART subnodes from a vessel snapshot ConfigNode and computes
        /// an enclosing bounding box. Each PART must have a pos=x,y,z value.
        /// Uses <see cref="DefaultPartHalfExtent"/> per part. Returns a 2m
        /// fallback bounds if snapshot is null or has no parseable parts.
        /// </summary>
        internal static Bounds ComputeVesselBounds(ConfigNode vesselSnapshot)
        {
            if (vesselSnapshot == null)
            {
                ParsekLog.Verbose(Tag, "ComputeVesselBounds: null snapshot — returning fallback 2m bounds");
                return new Bounds(Vector3.zero, Vector3.one * FallbackBoundsSize);
            }

            ConfigNode[] partNodes = vesselSnapshot.GetNodes("PART");
            if (partNodes == null || partNodes.Length == 0)
            {
                ParsekLog.Verbose(Tag, "ComputeVesselBounds: no PART subnodes — returning fallback 2m bounds");
                return new Bounds(Vector3.zero, Vector3.one * FallbackBoundsSize);
            }

            var parts = ParsePartPositions(partNodes);

            if (parts.Count == 0)
            {
                ParsekLog.Verbose(Tag, "ComputeVesselBounds: no parseable PART positions — returning fallback 2m bounds");
                return new Bounds(Vector3.zero, Vector3.one * FallbackBoundsSize);
            }

            ParsekLog.Verbose(Tag, $"ComputeVesselBounds: parsed {parts.Count}/{partNodes.Length} parts");
            return ComputeBoundsFromParts(parts);
        }

        /// <summary>
        /// Parses PART ConfigNodes to extract local positions. Each PART must have a
        /// pos=x,y,z value. Returns a list of (localPos, halfExtent) tuples.
        /// Parts with missing or unparseable positions are skipped.
        /// </summary>
        internal static List<(Vector3 localPos, float halfExtent)> ParsePartPositions(ConfigNode[] partNodes)
        {
            var parts = new List<(Vector3 localPos, float halfExtent)>();
            for (int i = 0; i < partNodes.Length; i++)
            {
                string posStr = partNodes[i].GetValue("pos")
                    ?? partNodes[i].GetValue("position");
                if (string.IsNullOrEmpty(posStr))
                    continue;

                string[] components = posStr.Split(',');
                if (components.Length < 3)
                    continue;

                if (!float.TryParse(components[0].Trim(), NumberStyles.Float, IC, out float x))
                    continue;
                if (!float.TryParse(components[1].Trim(), NumberStyles.Float, IC, out float y))
                    continue;
                if (!float.TryParse(components[2].Trim(), NumberStyles.Float, IC, out float z))
                    continue;

                parts.Add((new Vector3(x, y, z), DefaultPartHalfExtent));
            }

            ParsekLog.Verbose(Tag, $"ParsePartPositions: parsed {parts.Count}/{partNodes.Length} parts from snapshot");
            return parts;
        }

        /// <summary>
        /// Pure AABB overlap test with world-space offsets and padding.
        /// Offsets bounds a/b to world positions aCenter/bCenter, expands each
        /// by padding on all sides, then checks axis-aligned overlap on all three axes.
        /// NOTE: padding is applied to BOTH bounds, so effective clearance is 2*padding.
        /// This is intentional for safety margin.
        /// </summary>
        internal static bool BoundsOverlap(Bounds a, Vector3d aCenter, Bounds b, Vector3d bCenter, float padding)
        {
            // Compute world-space min/max for each bounds, expanded by padding
            double aMinX = aCenter.x + a.min.x - padding;
            double aMaxX = aCenter.x + a.max.x + padding;
            double aMinY = aCenter.y + a.min.y - padding;
            double aMaxY = aCenter.y + a.max.y + padding;
            double aMinZ = aCenter.z + a.min.z - padding;
            double aMaxZ = aCenter.z + a.max.z + padding;

            double bMinX = bCenter.x + b.min.x - padding;
            double bMaxX = bCenter.x + b.max.x + padding;
            double bMinY = bCenter.y + b.min.y - padding;
            double bMaxY = bCenter.y + b.max.y + padding;
            double bMinZ = bCenter.z + b.min.z - padding;
            double bMaxZ = bCenter.z + b.max.z + padding;

            // AABB overlap: must overlap on ALL three axes
            bool overlapX = aMinX <= bMaxX && aMaxX >= bMinX;
            bool overlapY = aMinY <= bMaxY && aMaxY >= bMinY;
            bool overlapZ = aMinZ <= bMaxZ && aMaxZ >= bMinZ;

            return overlapX && overlapY && overlapZ;
        }

        // ────────────────────────────────────────────────────────────
        //  KSC exclusion zone (pure, unit-testable)
        // ────────────────────────────────────────────────────────────

        /// <summary>KSC launch pad latitude in degrees.</summary>
        internal const double KscPadLatitude = -0.0972;

        /// <summary>KSC launch pad longitude in degrees.</summary>
        internal const double KscPadLongitude = -74.5575;

        /// <summary>KSC runway start (west threshold) latitude in degrees.</summary>
        internal const double KscRunwayLatitude = -0.0502;

        /// <summary>KSC runway start (west threshold) longitude in degrees.</summary>
        internal const double KscRunwayLongitude = -74.7300;

        /// <summary>
        /// Default exclusion radius in meters around KSC infrastructure points.
        /// 50m covers the immediate pad/runway structure without blocking
        /// legitimate nearby spawns. (#170)
        /// </summary>
        internal const double DefaultKscExclusionRadiusMeters = 50.0;

        /// <summary>
        /// Pure decision: is the given lat/lon within any KSC exclusion zone?
        /// Checks launch pad and runway start point.
        /// Uses flat-Earth surface distance approximation (valid for short distances).
        /// Only meaningful on the home world (Kerbin) — caller must check body.isHomeWorld.
        /// </summary>
        internal static bool IsWithinKscExclusionZone(
            double latitude, double longitude, double bodyRadius, double exclusionRadiusMeters)
        {
            double padDist = SurfaceDistance(latitude, longitude, KscPadLatitude, KscPadLongitude, bodyRadius);
            double runwayDist = SurfaceDistance(latitude, longitude, KscRunwayLatitude, KscRunwayLongitude, bodyRadius);

            bool withinPad = padDist < exclusionRadiusMeters;
            bool withinRunway = runwayDist < exclusionRadiusMeters;
            bool withinZone = withinPad || withinRunway;

            string nearest = withinPad ? "pad" : withinRunway ? "runway" : "none";
            ParsekLog.Verbose(Tag,
                string.Format(IC,
                    "IsWithinKscExclusionZone: lat={0} lon={1} padDist={2}m runwayDist={3}m radius={4}m nearest={5} → {6}",
                    latitude.ToString("F4", IC),
                    longitude.ToString("F4", IC),
                    padDist.ToString("F1", IC),
                    runwayDist.ToString("F1", IC),
                    exclusionRadiusMeters.ToString("F0", IC),
                    nearest,
                    withinZone));

            return withinZone;
        }

        /// <summary>
        /// Flat-Earth surface distance between two lat/lon points on a sphere.
        /// Valid for distances much smaller than body radius.
        /// </summary>
        internal static double SurfaceDistance(
            double lat1, double lon1, double lat2, double lon2, double bodyRadius)
        {
            double dLat = (lat1 - lat2) * Math.PI / 180.0;
            double dLon = (lon1 - lon2) * Math.PI / 180.0;
            double avgLat = (lat1 + lat2) * 0.5 * Math.PI / 180.0;
            double dx = dLon * Math.Cos(avgLat) * bodyRadius;
            double dy = dLat * bodyRadius;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // ────────────────────────────────────────────────────────────
        //  Trajectory walkback (pure, unit-testable)
        // ────────────────────────────────────────────────────────────

        /// <summary>Default timeout in seconds before walkback triggers.</summary>
        internal const double DefaultWalkbackTimeoutSeconds = 5.0;

        /// <summary>Default movement threshold in meters — blocker must move less than this for walkback.</summary>
        internal const float DefaultMovementThresholdMeters = 1.0f;

        /// <summary>
        /// Pure decision: should we trigger trajectory walkback?
        /// Returns true when the spawn has been blocked for at least <paramref name="timeoutSeconds"/>
        /// AND the blocking vessel has moved less than <paramref name="movementThreshold"/> meters,
        /// indicating an immovable vessel deadlock.
        /// </summary>
        internal static bool ShouldTriggerWalkback(
            double blockedSinceUT,
            double currentUT,
            double timeoutSeconds,
            float blockerDistanceChangeMeters,
            float movementThreshold)
        {
            double elapsed = currentUT - blockedSinceUT;
            bool timeoutReached = elapsed >= timeoutSeconds;
            bool blockerStationary = blockerDistanceChangeMeters < movementThreshold;

            ParsekLog.Verbose(Tag,
                string.Format(IC,
                    "ShouldTriggerWalkback: elapsed={0}s timeout={1}s blockerMoved={2}m threshold={3}m → timeout={4} stationary={5} → {6}",
                    elapsed.ToString("F2", IC),
                    timeoutSeconds.ToString("F1", IC),
                    blockerDistanceChangeMeters.ToString("F2", IC),
                    movementThreshold.ToString("F2", IC),
                    timeoutReached, blockerStationary,
                    timeoutReached && blockerStationary));

            return timeoutReached && blockerStationary;
        }

        /// <summary>
        /// Walk backward along a trajectory to find the latest point where the spawn
        /// bounding box does NOT overlap with any blocker. Used to resolve immovable
        /// vessel deadlocks by spawning at an earlier trajectory position.
        /// </summary>
        /// <param name="points">Trajectory points to walk backward through.</param>
        /// <param name="spawnBounds">Bounding box of the vessel to spawn (local-space).</param>
        /// <param name="padding">Padding in meters added to each side of the AABB.</param>
        /// <param name="pointToWorldPos">Converts a TrajectoryPoint to world-space position.
        /// At runtime: CelestialBody.GetWorldSurfacePosition. In tests: SimplePointToWorldPos.</param>
        /// <param name="isOverlapping">Returns true if a vessel at the given world position overlaps
        /// with any blocker. At runtime: CheckOverlapAgainstLoadedVessels. In tests: geometric check.</param>
        /// <returns>Index of the latest non-overlapping point, or -1 if the entire trajectory overlaps
        /// (or if the trajectory is null/empty).</returns>
        internal static int WalkbackAlongTrajectory(
            List<TrajectoryPoint> points,
            Bounds spawnBounds,
            float padding,
            Func<TrajectoryPoint, Vector3d> pointToWorldPos,
            Func<Vector3d, bool> isOverlapping)
        {
            if (points == null || points.Count == 0)
            {
                ParsekLog.Verbose(Tag, "WalkbackAlongTrajectory: null or empty trajectory — returning -1");
                return -1;
            }

            ParsekLog.Verbose(Tag,
                string.Format(IC,
                    "WalkbackAlongTrajectory: walking back from index {0} (UT={1})",
                    (points.Count - 1),
                    points[points.Count - 1].ut.ToString("F2", IC)));

            for (int i = points.Count - 1; i >= 0; i--)
            {
                Vector3d worldPos = pointToWorldPos(points[i]);
                bool overlaps = isOverlapping(worldPos);

                ParsekLog.Verbose(Tag,
                    string.Format(IC,
                        "WalkbackAlongTrajectory: index={0} UT={1} pos=({2},{3},{4}) overlaps={5}",
                        i,
                        points[i].ut.ToString("F2", IC),
                        worldPos.x.ToString("F1", IC),
                        worldPos.y.ToString("F1", IC),
                        worldPos.z.ToString("F1", IC),
                        overlaps));

                if (!overlaps)
                {
                    ParsekLog.Info(Tag,
                        string.Format(IC,
                            "WalkbackAlongTrajectory: found valid position at index {0} (UT={1})",
                            i, points[i].ut.ToString("F2", IC)));
                    return i;
                }
            }

            ParsekLog.Warn(Tag,
                "WalkbackAlongTrajectory: entire trajectory overlaps — returning -1 (manual placement fallback)");
            return -1;
        }

        /// <summary>Default walkback sub-step size in metres for the subdivided variant. (#264)</summary>
        internal const float DefaultWalkbackStepMeters = 1.5f;

        /// <summary>
        /// Maximum sub-steps generated from a single trajectory segment. Clamps against
        /// pathological orbital trajectories with multi-km adaptive-sample gaps (tens of
        /// km between points would otherwise yield 30k+ sub-steps per segment, burning
        /// main-thread time on the per-sub-step CheckOverlapAgainstLoadedVessels call).
        /// Normal EVA-scale segments are well below this cap (100 m / 1.5 m step = 67).
        /// </summary>
        internal const int MaxSubStepsPerSegment = 500;

        /// <summary>
        /// Walk backward along a trajectory, subdividing each segment with linear lat/lon/alt
        /// interpolation at a fixed metric step size, and return the first non-overlapping
        /// candidate found while walking outward from the last point. (#264)
        ///
        /// Distance between consecutive points is measured with <see cref="SurfaceDistance"/>
        /// (flat-Earth dx/dy approximation). At EVA scales (points ≤ tens of metres apart,
        /// trajectories ≤ 1 km end-to-end) the flat approximation is accurate to well under 1% —
        /// more than precise enough for step-count sizing on a 1.5 m granularity.
        ///
        /// Unlike the older point-granularity <see cref="WalkbackAlongTrajectory"/>, this
        /// variant subdivides between points so a walkback on a fast-moving trajectory advances
        /// in 1-2 m increments rather than potentially skipping 10-50 m per step. Used by
        /// VesselSpawner for end-of-recording spawns and now by VesselGhoster for primary
        /// chain-tip walkback, with the point-granularity helper retained only as a fallback
        /// when body resolution is unavailable.
        /// </summary>
        /// <param name="points">Trajectory points to walk backward through.</param>
        /// <param name="bodyRadius">Body radius in metres (for <see cref="SurfaceDistance"/>).</param>
        /// <param name="stepMeters">Sub-step size in metres (e.g. <see cref="DefaultWalkbackStepMeters"/>).</param>
        /// <param name="latLonAltToWorldPos">Converts a (lat, lon, alt) tuple to a world-space position.
        /// At runtime: <c>(lat,lon,alt) =&gt; body.GetWorldSurfacePosition(lat,lon,alt)</c>. In tests:
        /// a simple sphere projection.</param>
        /// <param name="isOverlapping">Returns true if a vessel at the given world position overlaps
        /// with any blocker. At runtime: calls <see cref="CheckOverlapAgainstLoadedVessels"/>.
        /// In tests: a geometric predicate.</param>
        /// <returns>
        /// <c>(found=true, lat, lon, alt, worldPos)</c> on the first non-overlapping sub-step
        /// encountered while walking outward from the last trajectory point;
        /// <c>(found=false, 0, 0, 0, <see cref="Vector3d.zero"/>)</c> if the entire trajectory overlaps
        /// or the input is null/empty.
        /// </returns>
        internal static (bool found, double lat, double lon, double alt, Vector3d worldPos)
            WalkbackAlongTrajectorySubdivided(
                List<TrajectoryPoint> points,
                double bodyRadius,
                float stepMeters,
                Func<double, double, double, Vector3d> latLonAltToWorldPos,
                Func<Vector3d, bool> isOverlapping)
        {
            var result = WalkbackAlongTrajectorySubdividedDetailed(
                points,
                bodyRadius,
                stepMeters,
                latLonAltToWorldPos,
                isOverlapping);

            return result.found
                ? (true, result.point.latitude, result.point.longitude, result.point.altitude, result.worldPos)
                : (false, 0, 0, 0, Vector3d.zero);
        }

        internal static (bool found, TrajectoryPoint point, Vector3d worldPos)
            WalkbackAlongTrajectorySubdividedDetailed(
                List<TrajectoryPoint> points,
                double bodyRadius,
                float stepMeters,
                Func<double, double, double, Vector3d> latLonAltToWorldPos,
                Func<Vector3d, bool> isOverlapping)
        {
            if (points == null || points.Count == 0)
            {
                ParsekLog.Verbose(Tag, "WalkbackSubdivided: null or empty trajectory — returning (false, …)");
                return (false, default, Vector3d.zero);
            }

            if (stepMeters <= 0f)
            {
                ParsekLog.Warn(Tag,
                    string.Format(IC, "WalkbackSubdivided: invalid stepMeters={0} — clamping to {1}",
                        stepMeters.ToString("F2", IC), DefaultWalkbackStepMeters.ToString("F2", IC)));
                stepMeters = DefaultWalkbackStepMeters;
            }

            // Base case: try the last point exactly. The caller already confirmed overlap at
            // this position, but the test suite may call this helper directly with a cleared
            // last point, so this is a safe base case.
            int last = points.Count - 1;
            var lastPt = points[last];
            Vector3d lastWorldPos = latLonAltToWorldPos(lastPt.latitude, lastPt.longitude, lastPt.altitude);
            if (!isOverlapping(lastWorldPos))
            {
                ParsekLog.Info(Tag,
                    string.Format(IC,
                        "WalkbackSubdivided: last point clear at index {0} (UT={1})",
                        last, lastPt.ut.ToString("F2", IC)));
                return (true, lastPt, lastWorldPos);
            }

            ParsekLog.Verbose(Tag,
                string.Format(IC,
                    "WalkbackSubdivided: walking back from index {0} (UT={1}) with step={2}m",
                    last, lastPt.ut.ToString("F2", IC), stepMeters.ToString("F2", IC)));

            int totalSubStepsChecked = 0;
            for (int i = last; i >= 1; i--)
            {
                var segEnd = points[i];       // later in time (index i)
                var segStart = points[i - 1]; // earlier in time (index i-1)
                double dMeters = SurfaceDistance(segStart.latitude, segStart.longitude,
                    segEnd.latitude, segEnd.longitude, bodyRadius);
                int nRaw = Math.Max(1, (int)Math.Ceiling(dMeters / stepMeters));
                int n = Math.Min(nRaw, MaxSubStepsPerSegment);
                if (n < nRaw)
                {
                    // Rate-limited: a pathological 20-segment orbital trajectory would
                    // otherwise burst 20 warnings per walkback attempt. Shared key across
                    // all walkback calls — the warning is diagnostic ("you have overly
                    // sparse trajectory sampling"), one line every few seconds suffices.
                    ParsekLog.WarnRateLimited(Tag, "walkback-subdivided-clamp",
                        string.Format(IC,
                            "WalkbackSubdivided: segment [{0}↔{1}] d={2}m requested n={3} > max {4} — clamping (effective step = {5}m)",
                            i - 1, i, dMeters.ToString("F2", IC), nRaw, MaxSubStepsPerSegment,
                            (dMeters / n).ToString("F2", IC)));
                }

                ParsekLog.Verbose(Tag,
                    string.Format(IC,
                        "WalkbackSubdivided: segment [{0}↔{1}] d={2}m n={3} stepping back",
                        i - 1, i, dMeters.ToString("F2", IC), n));

                for (int k = 1; k <= n; k++)
                {
                    double t = (double)k / (double)n;
                    double lat = segEnd.latitude + (segStart.latitude - segEnd.latitude) * t;
                    double lon = segEnd.longitude + (segStart.longitude - segEnd.longitude) * t;
                    double alt = segEnd.altitude + (segStart.altitude - segEnd.altitude) * t;
                    Vector3d worldPos = latLonAltToWorldPos(lat, lon, alt);
                    totalSubStepsChecked++;
                    bool overlaps = isOverlapping(worldPos);

                    if (!overlaps)
                    {
                        TrajectoryPoint interpolatedPoint = InterpolateTrajectoryPoint(segEnd, segStart, (float)t);
                        ParsekLog.Info(Tag,
                            string.Format(IC,
                                "WalkbackSubdivided: cleared at segment [{0}↔{1}] step {2}/{3} lat={4} lon={5} alt={6} (total sub-steps checked={7})",
                                i - 1, i, k, n,
                                interpolatedPoint.latitude.ToString("F6", IC),
                                interpolatedPoint.longitude.ToString("F6", IC),
                                interpolatedPoint.altitude.ToString("F1", IC),
                                totalSubStepsChecked));
                        return (true, interpolatedPoint, worldPos);
                    }

                    // Rate-limit per-sub-step overlap log spam: 1 in 10 candidates
                    if ((totalSubStepsChecked % 10) == 0)
                    {
                        ParsekLog.VerboseRateLimited(Tag, "walkback-substep-overlap",
                            string.Format(IC,
                                "WalkbackSubdivided: overlapping at sub-step {0}/{1} on segment [{2}↔{3}] (total={4})",
                                k, n, i - 1, i, totalSubStepsChecked));
                    }
                }
            }

            ParsekLog.Warn(Tag,
                string.Format(IC,
                    "WalkbackSubdivided: entire trajectory overlaps after {0} sub-steps — returning (false, …) (walkback exhausted)",
                    totalSubStepsChecked));
            return (false, default, Vector3d.zero);
        }

        private static TrajectoryPoint InterpolateTrajectoryPoint(
            TrajectoryPoint later,
            TrajectoryPoint earlier,
            float t)
        {
            t = Clamp01Managed(t);

            string bodyName;
            if (string.Equals(later.bodyName, earlier.bodyName, StringComparison.Ordinal))
            {
                bodyName = later.bodyName;
            }
            else if (t < 0.5f)
            {
                bodyName = string.IsNullOrEmpty(later.bodyName) ? earlier.bodyName : later.bodyName;
            }
            else
            {
                bodyName = string.IsNullOrEmpty(earlier.bodyName) ? later.bodyName : earlier.bodyName;
            }

            if (!string.Equals(later.bodyName, earlier.bodyName, StringComparison.Ordinal))
            {
                ParsekLog.Verbose(Tag,
                    string.Format(IC,
                        "WalkbackSubdivided: body transition later='{0}' earlier='{1}' t={2:F2} -> using '{3}'",
                        string.IsNullOrEmpty(later.bodyName) ? "(none)" : later.bodyName,
                        string.IsNullOrEmpty(earlier.bodyName) ? "(none)" : earlier.bodyName,
                        t,
                        string.IsNullOrEmpty(bodyName) ? "(none)" : bodyName));
            }

            return new TrajectoryPoint
            {
                ut = later.ut + (earlier.ut - later.ut) * t,
                latitude = later.latitude + (earlier.latitude - later.latitude) * t,
                longitude = later.longitude + (earlier.longitude - later.longitude) * t,
                altitude = later.altitude + (earlier.altitude - later.altitude) * t,
                rotation = SlerpQuaternionManaged(later.rotation, earlier.rotation, t),
                velocity = new Vector3(
                    LerpFloat(later.velocity.x, earlier.velocity.x, t),
                    LerpFloat(later.velocity.y, earlier.velocity.y, t),
                    LerpFloat(later.velocity.z, earlier.velocity.z, t)),
                bodyName = bodyName,
                funds = later.funds + (earlier.funds - later.funds) * t,
                science = later.science + (earlier.science - later.science) * t,
                reputation = later.reputation + (earlier.reputation - later.reputation) * t,
                // Phase 7: lerp clearance through walkback (NaN propagates).
                recordedGroundClearance = later.recordedGroundClearance
                    + (earlier.recordedGroundClearance - later.recordedGroundClearance) * t,
            };
        }

        private static float Clamp01Managed(float value)
        {
            if (float.IsNaN(value) || value <= 0f)
                return 0f;
            if (value >= 1f)
                return 1f;
            return value;
        }

        private static float LerpFloat(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        private static Quaternion SlerpQuaternionManaged(Quaternion from, Quaternion to, float t)
        {
            from = TrajectoryMath.SanitizeQuaternion(from);
            to = TrajectoryMath.SanitizeQuaternion(to);

            double dot =
                (from.x * to.x) +
                (from.y * to.y) +
                (from.z * to.z) +
                (from.w * to.w);

            if (dot < 0.0)
            {
                to = new Quaternion(-to.x, -to.y, -to.z, -to.w);
                dot = -dot;
            }

            if (dot > 0.9995)
            {
                return TrajectoryMath.SanitizeQuaternion(new Quaternion(
                    LerpFloat(from.x, to.x, t),
                    LerpFloat(from.y, to.y, t),
                    LerpFloat(from.z, to.z, t),
                    LerpFloat(from.w, to.w, t)));
            }

            dot = Math.Max(-1.0, Math.Min(1.0, dot));
            double theta0 = Math.Acos(dot);
            double sinTheta0 = Math.Sin(theta0);
            if (Math.Abs(sinTheta0) < 1e-8)
                return from;

            double theta = theta0 * t;
            double scaleFrom = Math.Sin(theta0 - theta) / sinTheta0;
            double scaleTo = Math.Sin(theta) / sinTheta0;

            return TrajectoryMath.SanitizeQuaternion(new Quaternion(
                (float)((scaleFrom * from.x) + (scaleTo * to.x)),
                (float)((scaleFrom * from.y) + (scaleTo * to.y)),
                (float)((scaleFrom * from.z) + (scaleTo * to.z)),
                (float)((scaleFrom * from.w) + (scaleTo * to.w))));
        }

        /// <summary>
        /// Pure helper for testing: converts a TrajectoryPoint's lat/lon/alt to a simple
        /// Cartesian position on a sphere of the given radius. Uses standard geographic
        /// to Cartesian conversion: x = (R+alt)*cos(lat)*cos(lon), y = (R+alt)*sin(lat),
        /// z = (R+alt)*cos(lat)*sin(lon).
        /// At runtime, the actual conversion uses CelestialBody.GetWorldSurfacePosition.
        /// </summary>
        internal static Vector3d SimplePointToWorldPos(TrajectoryPoint pt, double bodyRadius)
        {
            double r = bodyRadius + pt.altitude;
            double latRad = pt.latitude * Math.PI / 180.0;
            double lonRad = pt.longitude * Math.PI / 180.0;

            double x = r * Math.Cos(latRad) * Math.Cos(lonRad);
            double y = r * Math.Sin(latRad);
            double z = r * Math.Cos(latRad) * Math.Sin(lonRad);

            return new Vector3d(x, y, z);
        }

        // ────────────────────────────────────────────────────────────
        //  KSP-runtime methods (not unit-testable)
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Layer mask for part-collider overlap checks. Matches KSP's own
        /// <c>LayerUtil.DefaultEquivalent = 0x820001</c> (bits 0, 17, 23) — the
        /// authoritative "part-bearing layers" mask used inside
        /// <c>Vessel.CheckGroundCollision</c>, verified against the decompiled
        /// KSP source. Using KSP's own constant guarantees we'll see every layer
        /// the game considers a part-ownable surface.
        ///
        /// <para>We do NOT widen to include terrain (layer 15) or building
        /// layers — they aren't part-ownable, and the downstream
        /// <see cref="FlightGlobals.GetPartUpwardsCached"/> filter would strip
        /// them anyway. But narrowing the mask keeps the overlap query cheaper
        /// when spawning near cluttered KSC scenery.</para>
        /// </summary>
        private static int PartOverlapLayerMask
        {
            get { return LayerUtil.DefaultEquivalent; }
        }

        /// <summary>
        /// Checks whether a vessel spawned at <paramref name="spawnWorldPos"/> with
        /// the given local <paramref name="spawnBounds"/> would overlap any real
        /// part collider of a loaded vessel. Uses <see cref="Physics.OverlapBox"/>
        /// so the other vessel's actual part geometry is considered — no more
        /// 2 m-cube approximation.
        ///
        /// <para>Hit filtering: each overlapping collider is resolved to its owning
        /// <see cref="Part"/> via <see cref="FlightGlobals.GetPartUpwardsCached"/>
        /// (same helper KSP uses in <c>Vessel.CheckGroundCollision</c>). Hits
        /// without a Part owner (terrain, buildings, runway mesh colliders) are
        /// ignored. Hits on the active vessel / exempt vessel / skipped vessel
        /// types are filtered per the existing semantics.</para>
        /// </summary>
        internal static (bool overlap, float closestDistance, string blockerName, Vessel blockerVessel) CheckOverlapAgainstLoadedVessels(
            Vector3d spawnWorldPos, Bounds spawnBounds, float padding, bool skipActiveVessel = true,
            uint exemptVesselPid = 0)
        {
            // Compute the surface-aligned rotation for the spawn box. spawnBounds is in
            // vessel-local space, where the vessel's local Y axis points along the surface
            // normal (a landed vessel sits "up" from the body). On a curved body that Y
            // axis is tilted relative to world-Y. FromToRotation maps Vector3.up to the
            // actual local up at the spawn position, so the OverlapBox's extents are
            // oriented correctly. For the previous Quaternion.identity the error was small
            // for vessels near KSC (world-Y is nearly parallel to Kerbin-up there), but
            // grew for spawns on the far side of a body. Review finding from PR #225.
            Vector3 upAxis = Vector3.up;
            for (int bi = 0; FlightGlobals.Bodies != null && bi < FlightGlobals.Bodies.Count; bi++)
            {
                CelestialBody b = FlightGlobals.Bodies[bi];
                if (b == null) continue;
                double distSq = (spawnWorldPos - b.position).sqrMagnitude;
                double r = b.Radius;
                // The closest body whose center-to-spawn distance is within its
                // sphere of influence radius is "the body we're spawning near".
                // For our use case — landed/splashed spawns near the surface —
                // testing against 1.5x body radius is enough to pick the correct one.
                if (distSq < (r * 1.5) * (r * 1.5))
                {
                    upAxis = (Vector3)FlightGlobals.getUpAxis(b, (Vector3d)spawnWorldPos);
                    break;
                }
            }
            Quaternion surfaceRot = Quaternion.FromToRotation(Vector3.up, upAxis);
            Vector3 worldCenter = (Vector3)spawnWorldPos + surfaceRot * spawnBounds.center;
            Vector3 halfExtents = spawnBounds.extents + Vector3.one * padding;

            // Rate-limited: walkback calls this once per sub-step (potentially hundreds of
            // calls per spawn attempt). A single diagnostic line per second is enough to
            // confirm the method is being called and see the latest parameters.
            ParsekLog.VerboseRateLimited(Tag, "overlap-check-entry",
                $"CheckOverlapAgainstLoadedVessels: Physics.OverlapBox at " +
                $"({worldCenter.x.ToString("F0", IC)},{worldCenter.y.ToString("F0", IC)},{worldCenter.z.ToString("F0", IC)}) " +
                $"halfExtents=({halfExtents.x.ToString("F1", IC)},{halfExtents.y.ToString("F1", IC)},{halfExtents.z.ToString("F1", IC)}) " +
                $"padding={padding.ToString("F1", IC)}m, skipActiveVessel={skipActiveVessel}" +
                (exemptVesselPid != 0 ? $", exemptPid={exemptVesselPid} (T57)" : ""));

            Collider[] hits = Physics.OverlapBox(worldCenter, halfExtents, surfaceRot,
                PartOverlapLayerMask, QueryTriggerInteraction.Ignore);

            if (hits == null || hits.Length == 0)
            {
                // Happy path, no log -- walkback's per-substep summary is enough.
                return (false, float.MaxValue, null, null);
            }

            bool anyOverlap = false;
            float closestDist = float.MaxValue;
            string blockerName = null;
            Vessel blockerVessel = null;
            int totalHits = hits.Length;
            int nonPartHits = 0;
            int filteredHits = 0;
            var reportedPids = new HashSet<uint>();

            for (int i = 0; i < hits.Length; i++)
            {
                Collider hit = hits[i];
                if (hit == null) continue;

                Part part = FlightGlobals.GetPartUpwardsCached(hit.gameObject);
                if (part == null)
                {
                    nonPartHits++;
                    continue;
                }

                Vessel other = part.vessel;
                if (other == null)
                {
                    nonPartHits++;
                    continue;
                }

                if (!other.loaded) { filteredHits++; continue; }
                if (GhostMapPresence.IsGhostMapVessel(other.persistentId)) { filteredHits++; continue; }
                if (skipActiveVessel && other == FlightGlobals.ActiveVessel) { filteredHits++; continue; }
                if (exemptVesselPid != 0 && other.persistentId == exemptVesselPid)
                {
                    // Rate-limited per-vessel: walkback substeps hitting the same exempt
                    // parent vessel would otherwise log once per substep.
                    if (reportedPids.Add(other.persistentId))
                        ParsekLog.VerboseRateLimited(Tag, "overlap-exempt-" + other.persistentId,
                            $"Skipping exempt parent vessel '{Recording.ResolveLocalizedName(other.vesselName)}' " +
                            $"(pid={other.persistentId}) for EVA collision check (T57)");
                    filteredHits++;
                    continue;
                }
                if (ShouldSkipVesselType(other.vesselType))
                {
                    if (reportedPids.Add(other.persistentId))
                        ParsekLog.VerboseRateLimited(Tag, "overlap-skiptype-" + other.persistentId,
                            string.Format(IC, "Skipping {0} vessel '{1}' in overlap check",
                                other.vesselType, Recording.ResolveLocalizedName(other.vesselName)));
                    filteredHits++;
                    continue;
                }

                anyOverlap = true;
                Vector3d otherPos = other.GetWorldPos3D();
                float dist = (float)Vector3d.Distance(spawnWorldPos, otherPos);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    blockerName = Recording.ResolveLocalizedName(other.vesselName);
                    blockerVessel = other;
                }

                if (reportedPids.Add(other.persistentId))
                {
                    ParsekLog.VerboseRateLimited(Tag,
                        "overlap-" + other.persistentId,
                        $"Spawn overlaps with {Recording.ResolveLocalizedName(other.vesselName)} " +
                        $"(pid={other.persistentId}) via part '{part.partInfo?.name ?? "?"}' at {dist.ToString("F1", IC)}m");
                }
            }

            // Rate-limited summary: same key spans all walkback substeps so we get at most
            // one summary per second even when walkback is hammering this call.
            ParsekLog.VerboseRateLimited(Tag, "overlap-check-summary",
                $"OverlapBox summary: {totalHits} total hits, {nonPartHits} non-part (terrain/building), " +
                $"{filteredHits} filtered vessels, {(anyOverlap ? "BLOCKED by " + blockerName : "CLEAR")}");

            return (anyOverlap, closestDist, blockerName, blockerVessel);
        }

        /// <summary>
        /// Checks whether any loaded vessel is within the warning radius (200m per design).
        /// Returns the closest vessel's info. This is a softer check than bounding-box
        /// overlap — it triggers a UI warning but does not block the spawn.
        /// </summary>
        internal static (bool withinWarning, float distance, string vesselName) CheckWarningProximity(
            Vector3d spawnWorldPos, float warningRadius)
        {
            ParsekLog.Verbose(Tag,
                $"CheckWarningProximity: checking spawn at ({spawnWorldPos.x.ToString("F0", IC)},{spawnWorldPos.y.ToString("F0", IC)},{spawnWorldPos.z.ToString("F0", IC)}) " +
                $"warningRadius={warningRadius.ToString("F0", IC)}m");

            float closestDist = float.MaxValue;
            string closestName = null;

            var activeVessel = FlightGlobals.ActiveVessel;
            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                Vessel other = FlightGlobals.Vessels[i];
                if (!other.loaded) continue;
                if (GhostMapPresence.IsGhostMapVessel(other.persistentId)) continue;
                if (other == activeVessel) continue;

                // Skip non-significant vessel types (same filter as overlap check)
                if (ShouldSkipVesselType(other.vesselType))
                {
                    ParsekLog.Verbose(Tag,
                        string.Format(IC, "Skipping {0} vessel {1} in proximity check",
                            other.vesselType, Recording.ResolveLocalizedName(other.vesselName)));
                    continue;
                }

                float dist = (float)Vector3d.Distance(spawnWorldPos, other.GetWorldPos3D());
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestName = Recording.ResolveLocalizedName(other.vesselName);
                }
            }

            bool withinWarning = closestDist <= warningRadius;

            if (withinWarning)
            {
                ParsekLog.Info(Tag,
                    $"Warning proximity: closest vessel={closestName} at {closestDist.ToString("F1", IC)}m (radius={warningRadius.ToString("F0", IC)}m)");
            }
            else
            {
                ParsekLog.Verbose(Tag,
                    $"No vessel within warning radius {warningRadius.ToString("F0", IC)}m " +
                    $"(closest={closestName ?? "none"} at {(closestName != null ? closestDist.ToString("F1", IC) + "m" : "N/A")})");
            }

            return (withinWarning, closestDist, closestName);
        }
    }
}
