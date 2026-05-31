using System.Globalization;

namespace Parsek
{
    public enum RenderingZone
    {
        // NOTE: "Physics" here is the rendering full-fidelity tier, NOT KSP's
        // physics-load bubble. Its boundary is GhostFlight.FullFidelityRangeMeters
        // (5 km), deliberately larger than DistanceThresholds.PhysicsBubbleMeters
        // (2.3 km) so engine plumes / smoke stay visible past the physics bubble.
        Physics = 0,   // 0 - 5 km: full mesh + part events + engine/RCS/reentry FX
        Visual = 1,    // 5 - 120 km: coarse mesh silhouette, no FX, no part events
        Beyond = 2     // 120 km+: no mesh, position tracked for map view only
    }

    internal static class RenderingZoneManager
    {
        // Zone boundary distances (km converted to meters for consistency)
        internal const double FullFidelityRadius = DistanceThresholds.GhostFlight.FullFidelityRangeMeters;
        internal const double VisualRangeRadius = DistanceThresholds.GhostVisualRangeMeters;

        // Looped ghost spawn thresholds (tighter than full-timeline)
        internal const double LoopFullFidelityRadius = DistanceThresholds.GhostFlight.LoopFullFidelityMeters;
        internal const double LoopSimplifiedRadius = DistanceThresholds.GhostFlight.LoopSimplifiedMeters;
        // Beyond 50km: looped ghosts not spawned
        // Beyond 120km: no ghosts rendered at all

        /// <summary>
        /// Classifies a ghost's rendering zone based on distance from the player vessel.
        /// </summary>
        internal static RenderingZone ClassifyDistance(double distanceMeters)
        {
            if (distanceMeters < FullFidelityRadius) return RenderingZone.Physics;
            if (distanceMeters < VisualRangeRadius) return RenderingZone.Visual;
            return RenderingZone.Beyond;
        }

        /// <summary>
        /// Classifies distance with a narrow Physics/Visual hysteresis band.
        /// The outward transition still happens at the full-fidelity boundary,
        /// but a ghost already in Visual must move clearly back inside before
        /// full-fidelity renderers are restored.
        /// </summary>
        internal static RenderingZone ClassifyDistance(double distanceMeters, RenderingZone previousZone)
        {
            RenderingZone statelessZone = ClassifyDistance(distanceMeters);
            if (previousZone == RenderingZone.Visual
                && statelessZone == RenderingZone.Physics
                && distanceMeters >= DistanceThresholds.GhostFlight.FullFidelityRestoreMeters)
            {
                return RenderingZone.Visual;
            }

            return statelessZone;
        }

        /// <summary>
        /// Determines whether a looped ghost should be spawned at the given distance.
        /// Looped ghosts have tighter spawn thresholds than full-timeline ghosts.
        /// Returns (shouldSpawn, simplified) where simplified=true means no part events.
        /// </summary>
        internal static (bool shouldSpawn, bool simplified) ShouldSpawnLoopedGhostAtDistance(
            double distanceMeters)
        {
            if (distanceMeters < LoopFullFidelityRadius)
                return (true, false);   // Full fidelity
            if (distanceMeters < LoopSimplifiedRadius)
                return (true, true);    // Simplified (no part events)
            return (false, false);       // Too far, don't spawn
        }

        /// <summary>
        /// Determines whether a full-timeline ghost should render part events
        /// at the given distance. Part events only fire inside the full-fidelity range.
        /// </summary>
        internal static bool ShouldRenderPartEvents(double distanceMeters)
        {
            return distanceMeters < FullFidelityRadius;
        }

        /// <summary>
        /// Determines whether a ghost mesh should be visible at the given distance.
        /// Meshes visible in Physics and Visual zones, hidden Beyond.
        /// </summary>
        internal static bool ShouldRenderMesh(double distanceMeters)
        {
            return distanceMeters < VisualRangeRadius;
        }

        /// <summary>
        /// Logs a zone transition for a ghost. Called when a ghost's zone changes.
        /// </summary>
        internal static void LogZoneTransition(string ghostId, RenderingZone oldZone, RenderingZone newZone, double distanceMeters)
        {
            string distStr = FormatDistanceForLog(distanceMeters);
            ParsekLog.VerboseRateLimited("Zone", "zone-transition",
                $"Zone transition: ghost={ghostId} {oldZone}->{newZone} dist={distStr}");
        }

        internal static string FormatDistanceForLog(double distanceMeters)
        {
            if (double.IsNaN(distanceMeters)
                || double.IsInfinity(distanceMeters)
                || distanceMeters < 0.0
                || distanceMeters == double.MaxValue)
            {
                return "unresolved";
            }

            return distanceMeters.ToString("F0", CultureInfo.InvariantCulture) + "m";
        }

        /// <summary>
        /// Logs a looped ghost spawn/suppress decision.
        /// </summary>
        internal static void LogLoopedGhostSpawnDecision(string ghostId, double distanceMeters, bool shouldSpawn, bool simplified)
        {
            string distStr = FormatDistanceForLog(distanceMeters);
            string rateLimitKey = "loop-suppress-" + ghostId;
            if (!shouldSpawn)
            {
                ParsekLog.VerboseRateLimited("Zone", rateLimitKey,
                    $"Looped ghost suppressed: ghost={ghostId} dist={distStr} (beyond {LoopSimplifiedRadius.ToString("F0", CultureInfo.InvariantCulture)}m threshold)", 30.0);
            }
            else if (simplified)
            {
                ParsekLog.VerboseRateLimited("Zone", rateLimitKey,
                    $"Looped ghost spawned simplified: ghost={ghostId} dist={distStr} (no part events)", 30.0);
            }
            else
            {
                ParsekLog.VerboseRateLimited("Zone", rateLimitKey,
                    $"Looped ghost spawned full fidelity: ghost={ghostId} dist={distStr}", 30.0);
            }
        }
    }
}
