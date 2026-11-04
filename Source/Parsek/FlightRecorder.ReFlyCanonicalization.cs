using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    public partial class FlightRecorder
    {
        // A Re-Fly recording's points and orbit segments are recorded raw: no recording-frame
        // offset applies to them. This keeps the one line the recorder writes when a Re-Fly
        // recording's orbit has a non-finite orbital velocity at startUT; its wording is fixed
        // by the no-log-text-change rule, so it still names the canonicalization step.
        internal static void LogReFlyOrbitSegmentVelocityNonFinite(
            string recordingId,
            Vessel vessel,
            double startUT,
            string subsystem,
            string context)
        {
            if (string.IsNullOrEmpty(recordingId)
                || vessel == null
                || vessel.orbit == null
                || vessel.mainBody == null)
            {
                return;
            }

            Vector3d rawWorld;
            Vector3d velocity;
            try
            {
                rawWorld = vessel.orbit.getPositionAtUT(startUT);
                velocity = vessel.orbit.getOrbitalVelocityAtUT(startUT);
            }
            catch
            {
                return;
            }

            if (!IsFiniteVector3d(rawWorld))
                return;
            if (!IsFiniteVector3d(velocity))
            {
                ParsekLog.Verbose(
                    string.IsNullOrEmpty(subsystem) ? "Recorder" : subsystem,
                    string.Format(CultureInfo.InvariantCulture,
                        "TryCanonicalizeReFlyRecordingOrbitSegment skipped: rec={0} context={1} reason=orbital-velocity-non-finite startUT={2:F2}",
                        recordingId ?? "(null)",
                        context ?? "(null)",
                        startUT));
            }
        }

        internal static bool TryResolveAbsolutePointWorldForRelativeOffset(
            TrajectoryPoint point,
            Vessel fallbackVessel,
            out Vector3d worldPos)
        {
            worldPos = Vector3d.zero;
            CelestialBody body = FindBodyByNameForRecorder(point.bodyName);
            if (body == null)
                body = fallbackVessel?.mainBody;
            if (body == null)
                return false;

            try
            {
                worldPos = body.GetWorldSurfacePosition(
                    point.latitude,
                    point.longitude,
                    point.altitude);
                return IsFiniteVector3d(worldPos);
            }
            catch
            {
                return false;
            }
        }

        internal static Quaternion ComputeRelativeLocalRotationFromAbsolutePointRotation(
            Quaternion surfaceRelativeRotation,
            Quaternion bodyWorldRotation,
            Quaternion anchorWorldRotation)
        {
            Quaternion focusWorldRotation = TrajectoryMath.PureMultiply(
                TrajectoryMath.SanitizeQuaternion(bodyWorldRotation),
                TrajectoryMath.SanitizeQuaternion(surfaceRelativeRotation));
            return TrajectoryMath.ComputeRelativeLocalRotation(
                focusWorldRotation,
                anchorWorldRotation);
        }

        internal static bool TryResolveAbsolutePointWorldRotationForRelativeOffset(
            TrajectoryPoint point,
            Vessel fallbackVessel,
            out Quaternion worldRotation)
        {
            worldRotation = fallbackVessel?.transform != null
                ? fallbackVessel.transform.rotation
                : Quaternion.identity;
            CelestialBody body = FindBodyByNameForRecorder(point.bodyName);
            if (body == null)
                body = fallbackVessel?.mainBody;
            if (body?.bodyTransform == null)
                return fallbackVessel?.transform != null;

            try
            {
                worldRotation = TrajectoryMath.PureMultiply(
                    body.bodyTransform.rotation,
                    TrajectoryMath.SanitizeQuaternion(point.rotation));
                return true;
            }
            catch
            {
                return fallbackVessel?.transform != null;
            }
        }
    }
}
