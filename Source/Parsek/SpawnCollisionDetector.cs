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

            var parts = new List<(Vector3 localPos, float halfExtent)>();
            for (int i = 0; i < partNodes.Length; i++)
            {
                string posStr = partNodes[i].GetValue("pos");
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

            if (parts.Count == 0)
            {
                ParsekLog.Verbose(Tag, "ComputeVesselBounds: no parseable PART positions — returning fallback 2m bounds");
                return new Bounds(Vector3.zero, Vector3.one * FallbackBoundsSize);
            }

            ParsekLog.Verbose(Tag, $"ComputeVesselBounds: parsed {parts.Count}/{partNodes.Length} parts");
            return ComputeBoundsFromParts(parts);
        }

        /// <summary>
        /// Pure AABB overlap test with world-space offsets and padding.
        /// Offsets bounds a/b to world positions aCenter/bCenter, expands each
        /// by padding on all sides, then checks axis-aligned overlap on all three axes.
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
            bool anyOverlap = false;
            float closestDist = float.MaxValue;
            string blockerName = null;

            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                Vessel other = FlightGlobals.Vessels[i];
                if (!other.loaded) continue;

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
                        blockerName = other.vesselName;
                    }

                    ParsekLog.Info(Tag,
                        $"Spawn blocked: overlaps with {other.vesselName} at {dist.ToString("F1", IC)}m");
                }
                else
                {
                    ParsekLog.Verbose(Tag,
                        $"No overlap with {other.vesselName} at {dist.ToString("F1", IC)}m");
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
            float closestDist = float.MaxValue;
            string closestName = null;

            for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
            {
                Vessel other = FlightGlobals.Vessels[i];
                if (!other.loaded) continue;

                float dist = (float)Vector3d.Distance(spawnWorldPos, other.GetWorldPos3D());
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestName = other.vesselName;
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
