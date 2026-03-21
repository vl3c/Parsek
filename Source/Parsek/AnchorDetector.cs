using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Detects when the focused vessel is near a pre-existing persistent vessel
    /// that could serve as a RELATIVE reference frame anchor.
    /// A "pre-existing" vessel is one NOT part of the current recording tree --
    /// it exists independently in the game state (stations, bases, other missions).
    /// </summary>
    internal static class AnchorDetector
    {
        // Transition thresholds (meters)
        internal const double RelativeEntryDistance = 2300.0;   // Enter RELATIVE at physics bubble edge
        internal const double RelativeExitDistance = 2500.0;    // Exit with 200m hysteresis to prevent toggling
        internal const double DockingApproachDistance = 200.0;  // High-priority RELATIVE for docking precision

        /// <summary>
        /// Scans for the nearest pre-existing vessel within RELATIVE range.
        /// Returns the anchor vessel PID and distance, or (0, MaxValue) if none found.
        ///
        /// Pure static method -- vessel list and tree membership injected for testability.
        /// </summary>
        /// <param name="focusedVesselPid">PID of the focused vessel</param>
        /// <param name="focusedPosition">World position of focused vessel</param>
        /// <param name="vesselInfos">List of (pid, worldPosition) for all loaded vessels</param>
        /// <param name="treeVesselPids">Set of PIDs that are part of the current recording tree (excluded from anchor candidates)</param>
        /// <returns>(anchorPid, distance) -- anchorPid=0 if no anchor found</returns>
        internal static (uint anchorPid, double distance) FindNearestAnchor(
            uint focusedVesselPid,
            Vector3d focusedPosition,
            List<(uint pid, Vector3d position)> vesselInfos,
            HashSet<uint> treeVesselPids)
        {
            uint bestPid = 0;
            double bestDistance = double.MaxValue;

            for (int i = 0; i < vesselInfos.Count; i++)
            {
                uint pid = vesselInfos[i].pid;
                if (pid == focusedVesselPid) continue;
                if (treeVesselPids != null && treeVesselPids.Contains(pid)) continue;

                double dist = Vector3d.Distance(focusedPosition, vesselInfos[i].position);
                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    bestPid = pid;
                }
            }

            if (bestPid != 0)
            {
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Verbose("Anchor",
                    $"FindNearestAnchor: anchorPid={bestPid} dist={bestDistance.ToString("F1", ic)}m " +
                    $"focusedPid={focusedVesselPid} candidates={vesselInfos.Count}");
            }

            return (bestPid, bestDistance);
        }

        /// <summary>
        /// Determines whether the focused vessel should be in RELATIVE reference frame
        /// based on anchor detection and hysteresis.
        /// </summary>
        internal static bool ShouldUseRelativeFrame(
            double anchorDistance,
            bool currentlyRelative)
        {
            if (!currentlyRelative)
            {
                // Not currently relative -- enter at physics bubble edge
                bool enter = anchorDistance < RelativeEntryDistance;
                if (enter)
                {
                    var ic = CultureInfo.InvariantCulture;
                    ParsekLog.Info("Anchor",
                        $"RELATIVE entry: dist={anchorDistance.ToString("F1", ic)}m < {RelativeEntryDistance.ToString("F0", ic)}m");
                }
                return enter;
            }
            else
            {
                // Currently relative -- exit with hysteresis
                bool stay = anchorDistance < RelativeExitDistance;
                if (!stay)
                {
                    var ic = CultureInfo.InvariantCulture;
                    ParsekLog.Info("Anchor",
                        $"RELATIVE exit: dist={anchorDistance.ToString("F1", ic)}m >= {RelativeExitDistance.ToString("F0", ic)}m");
                }
                return stay;
            }
        }

        /// <summary>
        /// Returns true if the anchor is within docking approach range (&lt;200m).
        /// Used for logging/diagnostics -- the sample rate is already handled
        /// by ProximityRateSelector.
        /// </summary>
        internal static bool IsInDockingApproach(double anchorDistance)
        {
            bool inRange = anchorDistance < DockingApproachDistance;
            if (inRange)
            {
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Verbose("Anchor",
                    $"Docking approach detected: dist={anchorDistance.ToString("F1", ic)}m < {DockingApproachDistance.ToString("F0", ic)}m");
            }
            return inRange;
        }
    }
}
