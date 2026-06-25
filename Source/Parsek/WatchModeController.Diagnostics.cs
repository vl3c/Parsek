using System;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    internal partial class WatchModeController
    {
        internal static string FormatWatchDistanceForLogs(double distanceMeters)
        {
            if (double.IsNaN(distanceMeters) || double.IsInfinity(distanceMeters) || distanceMeters < 0)
                return "?";
            return distanceMeters < 1000.0
                ? distanceMeters.ToString("F0", CultureInfo.InvariantCulture) + "m"
                : (distanceMeters / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + "km";
        }

        internal static string FormatVector3ForLogs(Vector3 value)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "({0:F1},{1:F1},{2:F1})", value.x, value.y, value.z);
        }

        // Rate-limit key is the recording id so two distinct chain transfers
        // do not collide when the committed-list index slot is reused (the
        // index can shift across deletes / supersede swaps in the same session).
        internal static void LogAutoFollowDeferred(int nextIndex, string recordingId)
        {
            string key = string.IsNullOrEmpty(recordingId)
                ? $"auto-follow-deferred-idx-{nextIndex}"
                : $"auto-follow-deferred-{recordingId}";
            ParsekLog.VerboseRateLimited(
                "CameraFollow",
                key,
                $"Auto-follow target #{nextIndex} has no active ghost - deferring transfer");
        }

        internal static bool IsWithinWatchEntryRange(double distanceMeters)
        {
            return IsFiniteWatchDistance(distanceMeters)
                && distanceMeters < WatchEnterCutoffMeters;
        }

        internal static bool IsWithinWatchExitRange(double distanceMeters)
        {
            return IsFiniteWatchDistance(distanceMeters)
                && distanceMeters < WatchExitCutoffMeters;
        }

        internal static bool ShouldExitWatchForDistance(double distanceMeters)
        {
            return IsFiniteWatchDistance(distanceMeters)
                && distanceMeters >= WatchExitCutoffMeters;
        }

        internal static bool ShouldForceWatchedFullFidelityAtDistance(
            bool isWatchedGhost,
            double distanceMeters)
        {
            return isWatchedGhost
                && IsFiniteWatchDistance(distanceMeters)
                && !ShouldExitWatchForDistance(distanceMeters);
        }

        internal static double ResolveWatchCutoffDistance(
            double activeVesselDistanceMeters,
            double renderDistanceMeters,
            bool includeRenderDistance)
        {
            bool activeValid = IsFiniteWatchDistance(activeVesselDistanceMeters);
            bool renderValid = IsFiniteWatchDistance(renderDistanceMeters);

            if (!includeRenderDistance)
                return activeValid ? activeVesselDistanceMeters : renderDistanceMeters;
            if (activeValid && renderValid)
                return Math.Max(activeVesselDistanceMeters, renderDistanceMeters);
            if (activeValid)
                return activeVesselDistanceMeters;
            return renderDistanceMeters;
        }

        // Pure predicate for the cutoff debounce: returns true when the
        // watched ghost has been over the exit cutoff for enough consecutive
        // frames that we should actually exit watch mode. Counter
        // increments per cutoff-tripped frame and resets to 0 on any
        // within-range frame, so a single-frame false positive (e.g.
        // FloatingOrigin / Krakensbane frame-seam staleness during warp)
        // never reaches threshold. Real cutoff crossings exit after
        // WatchExitCutoffDebounceFrames frames (~50 ms at 60 fps).
        internal static bool ShouldExitWatchAfterCutoffDebounce(int consecutiveFrames)
        {
            return consecutiveFrames >= WatchExitCutoffDebounceFrames;
        }

        private static bool IsFiniteWatchDistance(double distanceMeters)
        {
            return !double.IsNaN(distanceMeters)
                && !double.IsInfinity(distanceMeters)
                && distanceMeters >= 0.0;
        }

        internal static bool IsFiniteVector3(Vector3 value)
        {
            return !float.IsNaN(value.x)
                && !float.IsNaN(value.y)
                && !float.IsNaN(value.z)
                && !float.IsInfinity(value.x)
                && !float.IsInfinity(value.y)
                && !float.IsInfinity(value.z);
        }

        internal static Vector3 OrbitDirectionFromAngles(float pitch, float heading)
        {
            float pitchRad = pitch * Mathf.Deg2Rad;
            float headingRad = heading * Mathf.Deg2Rad;
            return new Vector3(
                Mathf.Sin(headingRad) * Mathf.Cos(pitchRad),
                Mathf.Sin(pitchRad),
                Mathf.Cos(headingRad) * Mathf.Cos(pitchRad));
        }

        internal static string FormatRotationBasisForLogs(Quaternion rotation)
        {
            Vector3 forward = RotateVectorByQuaternion(rotation, Vector3.forward);
            Vector3 up = RotateVectorByQuaternion(rotation, Vector3.up);
            Vector3 right = RotateVectorByQuaternion(rotation, Vector3.right);
            return
                $"fwd={FormatVector3ForLogs(forward)} " +
                $"up={FormatVector3ForLogs(up)} " +
                $"right={FormatVector3ForLogs(right)}";
        }

        internal static (bool lastMapViewEnabled, bool pendingMapFocusRestore)
            InitializeMapFocusRestoreState(bool mapViewEnabled)
        {
            return (mapViewEnabled, mapViewEnabled);
        }

        internal static (bool lastMapViewEnabled, bool pendingMapFocusRestore, bool shouldAttemptRestore)
            AdvanceMapFocusRestoreState(
                bool lastMapViewEnabled,
                bool pendingMapFocusRestore,
                bool mapViewEnabled)
        {
            if (!mapViewEnabled)
                return (false, false, false);

            if (!lastMapViewEnabled)
                pendingMapFocusRestore = true;

            return (true, pendingMapFocusRestore, pendingMapFocusRestore);
        }

        internal static bool CanRestoreMapFocus(
            uint ghostPid,
            bool hasGhostVessel,
            bool hasMapObject,
            bool hasPlanetariumCamera)
        {
            return ClassifyMapFocusRestore(
                ghostPid,
                hasGhostVessel,
                hasMapObject,
                hasPlanetariumCamera) == "ready";
        }

        internal static string ClassifyMapFocusRestore(
            uint ghostPid,
            bool hasGhostVessel,
            bool hasMapObject,
            bool hasPlanetariumCamera)
        {
            if (ghostPid == 0)
                return "no-ghost-pid";
            if (!hasGhostVessel)
                return "ghost-vessel-missing";
            if (!hasMapObject)
                return "map-object-missing";
            if (!hasPlanetariumCamera)
                return "planetarium-camera-missing";
            return "ready";
        }

        internal static string BuildMapFocusRestoreDecisionMessage(
            int recordingIndex,
            uint ghostPid,
            bool hasGhostVessel,
            bool hasMapObject,
            bool hasOrbitRenderer,
            bool hasPlanetariumCamera,
            string reason)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Map focus restore decision: rec=#{0} ghostPid={1} hasGhostVessel={2} " +
                "mapObj={3} orbitRenderer={4} planetariumCamera={5} reason={6}",
                recordingIndex,
                ghostPid,
                hasGhostVessel,
                hasMapObject,
                hasOrbitRenderer,
                hasPlanetariumCamera,
                string.IsNullOrEmpty(reason) ? "(none)" : reason);
        }

        internal static string ClassifyWatchCameraInfrastructure(
            bool hasFlightCamera,
            bool hasTransform,
            bool hasParent)
        {
            if (!hasFlightCamera)
                return "flight-camera-missing";
            if (!hasTransform)
                return "camera-transform-missing";
            if (!hasParent)
                return "camera-parent-missing";
            return "ready";
        }

        internal static string BuildWatchCameraInfrastructureMessage(
            int recordingIndex,
            string recordingId,
            string reason,
            string vesselName = null,
            long cycleIndex = -1,
            string scene = null,
            bool hasState = false,
            bool hasGhost = false,
            bool hasCameraPivot = false)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "Watch camera infrastructure unavailable: rec=#{0} id={1} vessel=\"{2}\" " +
                "cycle={3} scene={4} targetState[state={5} ghost={6} pivot={7}] reason={8}",
                recordingIndex,
                string.IsNullOrEmpty(recordingId) ? "(none)" : recordingId,
                vesselName ?? "?",
                cycleIndex,
                string.IsNullOrEmpty(scene) ? "n/a" : scene,
                hasState,
                hasGhost,
                hasCameraPivot,
                string.IsNullOrEmpty(reason) ? "(none)" : reason);
        }
    }
}
