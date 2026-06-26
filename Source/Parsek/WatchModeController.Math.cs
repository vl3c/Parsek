using UnityEngine;

namespace Parsek
{
    internal partial class WatchModeController
    {
        // === Horizon-locked camera mode — pure static methods ===

        /// <summary>
        /// Determines whether the camera should auto-select horizon-locked mode
        /// based on altitude relative to the body's atmosphere or surface threshold.
        /// </summary>
        internal static bool ShouldAutoHorizonLock(bool hasAtmosphere, double atmosphereDepth, double altitude)
        {
            return DistanceThresholds.GhostFlight.ShouldAutoHorizonLock(
                hasAtmosphere, atmosphereDepth, altitude);
        }

        /// <summary>
        /// Atmospheric watch mode should follow surface-relative prograde.
        /// Outside the atmosphere, preserve the existing playback/inertial heading.
        /// </summary>
        internal static bool ShouldUseSurfaceRelativeWatchHeading(
            bool hasAtmosphere, double atmosphereDepth, double altitude)
        {
            return hasAtmosphere && altitude < atmosphereDepth;
        }

        /// <summary>
        /// Converts playback velocity to a body-relative velocity suitable for
        /// horizon-lock heading decisions. Playback samples are recorded in KSP's
        /// world/orbital frame, so surface-relative consumers must subtract the
        /// body's rotating-frame velocity at the ghost position.
        /// </summary>
        internal static Vector3 ComputeSurfaceRelativeVelocity(
            Vector3 playbackVelocity, Vector3 rotatingFrameVelocity)
        {
            return playbackVelocity - rotatingFrameVelocity;
        }

        /// <summary>
        /// Computes the forward vector used by horizon-locked watch mode after
        /// converting playback velocity to the rotating body's surface frame.
        /// Returns the effective heading velocity and the fallback source for diagnostics/tests.
        /// </summary>
        internal static (Vector3 forward, Vector3 horizonVelocity, Vector3 headingVelocity,
            HorizonForwardSource source) ComputeWatchHorizonForward(
                Vector3 up, Vector3 playbackVelocity, Vector3 rotatingFrameVelocity,
                Vector3 lastForward)
        {
            Vector3 headingVelocity = ComputeSurfaceRelativeVelocity(
                playbackVelocity, rotatingFrameVelocity);
            Vector3 horizonVelocity = Vector3.ProjectOnPlane(headingVelocity, up);
            Vector3 forward = horizonVelocity;
            HorizonForwardSource source = HorizonForwardSource.ProjectedVelocity;
            if (forward.sqrMagnitude < 0.01f)
            {
                forward = Vector3.ProjectOnPlane(lastForward, up);
                if (forward.sqrMagnitude < 0.0001f)
                {
                    forward = Vector3.Cross(up, Vector3.right);
                    if (forward.sqrMagnitude < 0.0001f)
                        forward = Vector3.Cross(up, Vector3.forward);
                    source = HorizonForwardSource.ArbitraryPerpendicularFallback;
                }
                else
                {
                    source = HorizonForwardSource.LastForwardFallback;
                }
            }
            forward.Normalize();
            return (forward, horizonVelocity, headingVelocity, source);
        }

        /// <summary>
        /// Computes the full horizon-lock basis used by watch mode, including the
        /// atmospheric decision for whether surface-relative heading should be used.
        /// </summary>
        internal static (Vector3 forward, Vector3 horizonVelocity, Vector3 headingVelocity,
            Vector3 appliedFrameVelocity, HorizonForwardSource source)
            ComputeWatchHorizonBasis(
                bool hasAtmosphere, double atmosphereDepth, double altitude,
                Vector3 up, Vector3 playbackVelocity, Vector3 rotatingFrameVelocity,
                Vector3 lastForward)
        {
            Vector3 appliedFrameVelocity = ShouldUseSurfaceRelativeWatchHeading(
                hasAtmosphere, atmosphereDepth, altitude)
                ? rotatingFrameVelocity
                : Vector3.zero;
            var (forward, horizonVelocity, headingVelocity, source) =
                ComputeWatchHorizonForward(
                    up, playbackVelocity, appliedFrameVelocity, lastForward);
            return (forward, horizonVelocity, headingVelocity, appliedFrameVelocity, source);
        }

        /// <summary>
        /// Computes the horizon-plane forward direction from velocity and up vector.
        /// Projects velocity onto the horizon plane (perpendicular to up). Falls back
        /// to lastForward when velocity is near zero, then to an arbitrary perpendicular.
        /// Pure vector math — testable outside Unity runtime.
        /// </summary>
        internal static Vector3 ComputeHorizonForward(Vector3 up, Vector3 velocity, Vector3 lastForward)
        {
            var (forward, _, _, _) = ComputeWatchHorizonForward(
                up, velocity, Vector3.zero, lastForward);
            return forward;
        }

        /// <summary>
        /// Computes the horizon-aligned rotation for the camera proxy.
        /// Wraps ComputeHorizonForward with Quaternion.LookRotation.
        /// </summary>
        internal static (Quaternion rotation, Vector3 forward) ComputeHorizonRotation(
            Vector3 up, Vector3 velocity, Vector3 lastForward)
        {
            Vector3 forward = ComputeHorizonForward(up, velocity, lastForward);
            return (Quaternion.LookRotation(forward, up), forward);
        }

        /// <summary>
        /// Compensates camPitch/camHdg when switching the camera target between
        /// transforms with different rotations, preventing a visual snap.
        /// Decomposes the current camera orbit direction from the old frame
        /// into equivalent angles in the new frame.
        /// </summary>
        internal static (float pitch, float hdg) CompensateCameraAngles(
            Quaternion oldTargetRot, Quaternion newTargetRot, float pitch, float hdg)
        {
            // Convert current pitch/hdg to a direction vector in world space
            // FlightCamera: pitch = elevation (degrees), hdg = azimuth (degrees)
            // Camera orbits at (hdg, pitch) relative to the target's local frame
            float pitchRad = pitch * Mathf.Deg2Rad;
            float hdgRad = hdg * Mathf.Deg2Rad;

            // Spherical to local direction (KSP convention: Y=up, Z=forward, X=right)
            Vector3 localDir = new Vector3(
                Mathf.Sin(hdgRad) * Mathf.Cos(pitchRad),
                Mathf.Sin(pitchRad),
                Mathf.Cos(hdgRad) * Mathf.Cos(pitchRad));

            // Transform to world space via old target, then back to new target's local space
            Vector3 worldDir = RotateVectorByQuaternion(oldTargetRot, localDir);
            Vector3 newLocalDir = InverseRotateVectorByQuaternion(newTargetRot, worldDir);

            // Decompose back to pitch/hdg
            float newPitch = Mathf.Asin(Mathf.Clamp(newLocalDir.y, -1f, 1f)) * Mathf.Rad2Deg;
            float newHdg = Mathf.Atan2(newLocalDir.x, newLocalDir.z) * Mathf.Rad2Deg;

            return (newPitch, newHdg);
        }

        internal static (float pitch, float hdg) DecomposeOrbitDirectionInTargetFrame(
            Vector3 worldOrbitDirection,
            Quaternion targetRotation)
        {
            Vector3 normalizedDirection = worldOrbitDirection.normalized;
            Vector3 localDir = InverseRotateVectorByQuaternion(targetRotation, normalizedDirection);
            float pitch = Mathf.Asin(Mathf.Clamp(localDir.y, -1f, 1f)) * Mathf.Rad2Deg;
            float hdg = Mathf.Atan2(localDir.x, localDir.z) * Mathf.Rad2Deg;
            return (pitch, hdg);
        }

        internal static Vector3 RotateVectorByQuaternion(Quaternion rotation, Vector3 direction)
        {
            Quaternion normalized = NormalizeQuaternion(rotation);
            Vector3 axis = new Vector3(normalized.x, normalized.y, normalized.z);
            float scalar = normalized.w;
            return 2f * Vector3.Dot(axis, direction) * axis
                + (scalar * scalar - Vector3.Dot(axis, axis)) * direction
                + 2f * scalar * Vector3.Cross(axis, direction);
        }

        internal static Vector3 InverseRotateVectorByQuaternion(Quaternion rotation, Vector3 direction)
        {
            Quaternion normalized = NormalizeQuaternion(rotation);
            Quaternion inverse = new Quaternion(
                -normalized.x,
                -normalized.y,
                -normalized.z,
                normalized.w);
            return RotateVectorByQuaternion(inverse, direction);
        }

        internal static Quaternion NormalizeQuaternion(Quaternion rotation)
        {
            float sqrMagnitude =
                rotation.x * rotation.x
                + rotation.y * rotation.y
                + rotation.z * rotation.z
                + rotation.w * rotation.w;
            if (sqrMagnitude <= 1e-12f
                || float.IsNaN(sqrMagnitude)
                || float.IsInfinity(sqrMagnitude))
            {
                return new Quaternion(0f, 0f, 0f, 1f);
            }

            float inverseMagnitude = 1f / Mathf.Sqrt(sqrMagnitude);
            return new Quaternion(
                rotation.x * inverseMagnitude,
                rotation.y * inverseMagnitude,
                rotation.z * inverseMagnitude,
                rotation.w * inverseMagnitude);
        }
    }
}
