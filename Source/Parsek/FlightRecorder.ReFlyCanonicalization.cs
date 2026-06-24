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
        internal static bool TryCanonicalizeReFlyRecordingPoint(
            string recordingId,
            ref TrajectoryPoint point,
            string subsystem,
            string context)
        {
            if (string.IsNullOrEmpty(recordingId))
                return false;

            CelestialBody body = FindBodyByNameForRecorder(point.bodyName);
            if (body == null)
                return false;

            Vector3d rawWorld;
            try
            {
                rawWorld = body.GetWorldSurfacePosition(
                    point.latitude,
                    point.longitude,
                    point.altitude);
            }
            catch
            {
                return false;
            }

            if (!TryApplyReFlyRecordingFrameOffsetToWorld(
                    recordingId,
                    point.ut,
                    rawWorld,
                    out Vector3d canonicalWorld,
                    out Vector3d appliedOffset,
                    out string reason))
            {
                return false;
            }

            double latitude;
            double longitude;
            double altitude;
            try
            {
                latitude = body.GetLatitude(canonicalWorld);
                longitude = body.GetLongitude(canonicalWorld);
                altitude = body.GetAltitude(canonicalWorld);
            }
            catch
            {
                return false;
            }

            if (!IsFinite(latitude) || !IsFinite(longitude) || !IsFinite(altitude))
                return false;

            point.latitude = latitude;
            point.longitude = longitude;
            point.altitude = altitude;
            LogReFlyRecordingFrameCanonicalized(
                subsystem,
                recordingId,
                context,
                point.ut,
                appliedOffset,
                rawWorld,
                canonicalWorld,
                reason);
            return true;
        }

        internal static bool TryCanonicalizeReFlyRecordingOrbitSegment(
            string recordingId,
            Vessel vessel,
            double startUT,
            ref OrbitSegment segment,
            string subsystem,
            string context)
        {
            if (string.IsNullOrEmpty(recordingId)
                || vessel == null
                || vessel.orbit == null
                || vessel.mainBody == null)
            {
                return false;
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
                return false;
            }

            if (!IsFiniteVector3d(rawWorld))
                return false;
            if (!IsFiniteVector3d(velocity))
            {
                ParsekLog.Verbose(
                    string.IsNullOrEmpty(subsystem) ? "Recorder" : subsystem,
                    string.Format(CultureInfo.InvariantCulture,
                        "TryCanonicalizeReFlyRecordingOrbitSegment skipped: rec={0} context={1} reason=orbital-velocity-non-finite startUT={2:F2}",
                        recordingId ?? "(null)",
                        context ?? "(null)",
                        startUT));
                return false;
            }

            if (!TryApplyReFlyRecordingFrameOffsetToWorld(
                    recordingId,
                    startUT,
                    rawWorld,
                    out Vector3d canonicalWorld,
                    out Vector3d appliedOffset,
                    out string reason))
            {
                return false;
            }

            Orbit canonicalOrbit;
            try
            {
                canonicalOrbit = new Orbit();
                OrbitReseed.FromWorldPosAndZupVelocity(
                    canonicalOrbit,
                    vessel.mainBody,
                    canonicalWorld,
                    velocity,
                    startUT);
            }
            catch
            {
                return false;
            }

            OrbitSegment canonicalSegment = segment;
            canonicalSegment.inclination = canonicalOrbit.inclination;
            canonicalSegment.eccentricity = canonicalOrbit.eccentricity;
            canonicalSegment.semiMajorAxis = canonicalOrbit.semiMajorAxis;
            canonicalSegment.longitudeOfAscendingNode = canonicalOrbit.LAN;
            canonicalSegment.argumentOfPeriapsis = canonicalOrbit.argumentOfPeriapsis;
            canonicalSegment.meanAnomalyAtEpoch = canonicalOrbit.meanAnomalyAtEpoch;
            canonicalSegment.epoch = canonicalOrbit.epoch;
            canonicalSegment.bodyName = vessel.mainBody.name;

            if (!IsFinite(canonicalSegment.inclination)
                || !IsFinite(canonicalSegment.eccentricity)
                || !IsFinite(canonicalSegment.semiMajorAxis)
                || !IsFinite(canonicalSegment.longitudeOfAscendingNode)
                || !IsFinite(canonicalSegment.argumentOfPeriapsis)
                || !IsFinite(canonicalSegment.meanAnomalyAtEpoch)
                || !IsFinite(canonicalSegment.epoch))
            {
                return false;
            }

            segment = canonicalSegment;
            LogReFlyRecordingFrameCanonicalized(
                subsystem,
                recordingId,
                context,
                startUT,
                appliedOffset,
                rawWorld,
                canonicalWorld,
                reason);
            return true;
        }

        internal static bool TryApplyReFlyRecordingFrameOffsetToWorld(
            string recordingId,
            double currentUT,
            Vector3d rawWorld,
            out Vector3d canonicalWorld,
            out Vector3d appliedOffset,
            out string reason)
        {
            canonicalWorld = rawWorld;
            appliedOffset = Vector3d.zero;
            reason = null;

            if (string.IsNullOrEmpty(recordingId))
            {
                reason = "recording-id-missing";
                return false;
            }
            if (!IsFiniteVector3d(rawWorld))
            {
                reason = "raw-world-non-finite";
                return false;
            }
            if (!TryResolveReFlyRecordingFrameOffsetForRecording(
                    recordingId,
                    currentUT,
                    out appliedOffset,
                    out reason))
            {
                return false;
            }
            if (!IsFiniteVector3d(appliedOffset))
            {
                reason = "offset-non-finite";
                return false;
            }

            canonicalWorld = rawWorld - appliedOffset;
            if (!IsFiniteVector3d(canonicalWorld))
            {
                reason = "canonical-world-non-finite";
                return false;
            }

            return true;
        }

        private static bool TryResolveReFlyRecordingFrameOffsetForRecording(
            string recordingId,
            double currentUT,
            out Vector3d offset,
            out string reason)
        {
            offset = Vector3d.zero;
            reason = null;

            reason = "display-alignment-removed";
            return false;
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

        private static void LogReFlyRecordingFrameCanonicalized(
            string subsystem,
            string recordingId,
            string context,
            double ut,
            Vector3d appliedOffset,
            Vector3d rawWorld,
            Vector3d canonicalWorld,
            string reason)
        {
            var ic = CultureInfo.InvariantCulture;
            string logSubsystem = string.IsNullOrEmpty(subsystem) ? "Recorder" : subsystem;
            ParsekLog.VerboseRateLimited(
                logSubsystem,
                "refly-recording-frame-canonicalized|"
                    + (recordingId ?? "<no-rec>") + "|"
                    + (context ?? "<none>"),
                "Re-Fly recording frame canonicalized: rec="
                    + (recordingId ?? "<none>")
                    + " context=" + (context ?? "<none>")
                    + " ut=" + ut.ToString("F3", ic)
                    + " offsetMeters=" + appliedOffset.magnitude.ToString("F2", ic)
                    + " offset=(" + appliedOffset.x.ToString("F2", ic)
                    + "," + appliedOffset.y.ToString("F2", ic)
                    + "," + appliedOffset.z.ToString("F2", ic) + ")"
                    + " raw=(" + rawWorld.x.ToString("F2", ic)
                    + "," + rawWorld.y.ToString("F2", ic)
                    + "," + rawWorld.z.ToString("F2", ic) + ")"
                    + " canonical=(" + canonicalWorld.x.ToString("F2", ic)
                    + "," + canonicalWorld.y.ToString("F2", ic)
                    + "," + canonicalWorld.z.ToString("F2", ic) + ")"
                    + " reason=" + (reason ?? "<none>"),
                5.0);
        }
    }
}
