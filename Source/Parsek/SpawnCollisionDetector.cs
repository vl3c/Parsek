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

        /// <summary>
        /// Default exclusion radius in meters around the KSC launch pad.
        /// The pad structure extends ~50-80m; 150m provides safety margin
        /// against collisions with pad infrastructure. (#170)
        /// </summary>
        internal const double DefaultKscExclusionRadiusMeters = 150.0;

        /// <summary>
        /// Pure decision: is the given lat/lon within the KSC launch pad exclusion zone?
        /// Uses great-circle surface distance approximation (flat-Earth for short distances).
        /// Only meaningful on the home world (Kerbin) — caller must check body.isHomeWorld.
        /// </summary>
        /// <param name="latitude">Spawn latitude in degrees.</param>
        /// <param name="longitude">Spawn longitude in degrees.</param>
        /// <param name="bodyRadius">Radius of the body in meters (e.g. 600000 for Kerbin).</param>
        /// <param name="exclusionRadiusMeters">Exclusion radius in meters.</param>
        /// <returns>True if the position is within the exclusion zone.</returns>
        internal static bool IsWithinKscExclusionZone(
            double latitude, double longitude, double bodyRadius, double exclusionRadiusMeters)
        {
            double dLat = (latitude - KscPadLatitude) * Math.PI / 180.0;
            double dLon = (longitude - KscPadLongitude) * Math.PI / 180.0;
            double avgLat = (latitude + KscPadLatitude) * 0.5 * Math.PI / 180.0;

            // Flat-Earth approximation: valid for distances << body radius
            double dx = dLon * Math.Cos(avgLat) * bodyRadius;
            double dy = dLat * bodyRadius;
            double distanceMeters = Math.Sqrt(dx * dx + dy * dy);

            bool withinZone = distanceMeters < exclusionRadiusMeters;

            ParsekLog.Verbose(Tag,
                string.Format(IC,
                    "IsWithinKscExclusionZone: lat={0} lon={1} distance={2}m radius={3}m → {4}",
                    latitude.ToString("F4", IC),
                    longitude.ToString("F4", IC),
                    distanceMeters.ToString("F1", IC),
                    exclusionRadiusMeters.ToString("F0", IC),
                    withinZone));

            return withinZone;
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
        /// Checks whether the spawn bounding box (at spawnWorldPos) overlaps with
        /// any loaded vessel's approximate bounding box. Returns the closest
        /// overlapping vessel's info, or overlap=false if no overlap found.
        /// </summary>
        internal static (bool overlap, float closestDistance, string blockerName) CheckOverlapAgainstLoadedVessels(
            Vector3d spawnWorldPos, Bounds spawnBounds, float padding)
        {
            ParsekLog.Verbose(Tag,
                $"CheckOverlapAgainstLoadedVessels: checking spawn at ({spawnWorldPos.x.ToString("F0", IC)},{spawnWorldPos.y.ToString("F0", IC)},{spawnWorldPos.z.ToString("F0", IC)}) " +
                $"padding={padding.ToString("F1", IC)}m against {FlightGlobals.Vessels.Count} vessels");

            bool anyOverlap = false;
            float closestDist = float.MaxValue;
            string blockerName = null;

            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                Vessel other = FlightGlobals.Vessels[i];
                if (!other.loaded) continue;

                // Skip player's own vessel — shouldn't block spawns
                if (other == FlightGlobals.ActiveVessel) continue;

                // Skip non-significant vessel types that shouldn't block spawns
                if (ShouldSkipVesselType(other.vesselType))
                {
                    ParsekLog.Verbose(Tag,
                        string.Format(IC, "Skipping {0} vessel {1} in overlap check",
                            other.vesselType, Recording.ResolveLocalizedName(other.vesselName)));
                    continue;
                }

                Vector3d otherPos = other.GetWorldPos3D();
                float dist = (float)Vector3d.Distance(spawnWorldPos, otherPos);

                // Approximate other vessel bounds as a default-sized cube
                Bounds otherBounds = new Bounds(Vector3.zero, Vector3.one * FallbackBoundsSize);

                if (BoundsOverlap(spawnBounds, spawnWorldPos, otherBounds, otherPos, padding))
                {
                    anyOverlap = true;
                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        blockerName = Recording.ResolveLocalizedName(other.vesselName);
                    }

                    ParsekLog.VerboseRateLimited(Tag,
                        "overlap-" + other.persistentId,
                        $"Spawn overlaps with {Recording.ResolveLocalizedName(other.vesselName)} (pid={other.persistentId}) at {dist.ToString("F1", IC)}m");
                }
                else
                {
                    ParsekLog.Verbose(Tag,
                        $"No overlap with {Recording.ResolveLocalizedName(other.vesselName)} at {dist.ToString("F1", IC)}m");
                }
            }

            if (!anyOverlap)
            {
                ParsekLog.Verbose(Tag, "No spawn overlap detected against loaded vessels");
            }

            return (anyOverlap, closestDist, blockerName);
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
