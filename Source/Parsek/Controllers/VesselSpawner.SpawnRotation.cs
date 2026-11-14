using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Parsek
{
    // Spawn rotation: the preferred surface-relative rotation frame for a spawn and the body it is resolved against.
    public static partial class VesselSpawner
    {
        internal static bool IsSurfaceTerminal(TerminalState? terminalState)
        {
            return terminalState == TerminalState.Landed
                || terminalState == TerminalState.Splashed;
        }

        internal static bool TryGetPreferredSpawnRotationFrame(
            Recording rec,
            TrajectoryPoint? fallbackPoint,
            out string bodyName,
            out Quaternion surfaceRelativeRotation,
            out string source)
        {
            bodyName = null;
            surfaceRelativeRotation = Quaternion.identity;
            source = null;

            if (rec == null || !IsSurfaceTerminal(rec.TerminalStateValue))
                return false;

            if (rec.TerminalPosition.HasValue)
            {
                SurfacePosition terminalPose = rec.TerminalPosition.Value;
                if (terminalPose.HasRecordedRotation)
                {
                    bodyName = terminalPose.body;
                    if (string.IsNullOrEmpty(bodyName))
                        RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out bodyName);
                    if (!string.IsNullOrEmpty(bodyName))
                    {
                        surfaceRelativeRotation = TrajectoryMath.SanitizeQuaternion(terminalPose.rotation);
                        source = "terminal surface pose";
                        return true;
                    }
                }
            }

            if (fallbackPoint.HasValue)
            {
                bodyName = fallbackPoint.Value.bodyName;
                if (string.IsNullOrEmpty(bodyName))
                    RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out bodyName);
                if (!string.IsNullOrEmpty(bodyName))
                {
                    surfaceRelativeRotation = TrajectoryMath.SanitizeQuaternion(fallbackPoint.Value.rotation);
                    source = "last trajectory point";
                    return true;
                }
            }

            return false;
        }

        internal static bool TryResolvePreferredSpawnRotation(
            Recording rec,
            TrajectoryPoint? fallbackPoint,
            out string bodyName,
            out Quaternion? bodyRotation,
            out Quaternion surfaceRelativeRotation,
            out string source)
        {
            bodyName = null;
            bodyRotation = null;
            surfaceRelativeRotation = Quaternion.identity;
            source = null;

            if (!TryGetPreferredSpawnRotationFrame(
                rec, fallbackPoint, out bodyName, out surfaceRelativeRotation, out source))
            {
                return false;
            }

            if (LiveVesselSnapshot.TryResolveBodyByName(bodyName, out CelestialBody body)
                && body != null
                && body.bodyTransform != null)
            {
                bodyRotation = body.bodyTransform.rotation;
            }

            return true;
        }

        internal static bool TryApplyPreferredSpawnRotation(
            ConfigNode vesselNode,
            Recording rec,
            TrajectoryPoint? fallbackPoint,
            string context)
        {
            if (!TryResolvePreferredSpawnRotation(
                rec, fallbackPoint,
                out string bodyName,
                out Quaternion? bodyRotation,
                out Quaternion surfaceRelativeRotation,
                out string source))
            {
                return false;
            }

            return VesselSnapshotOps.TryApplySpawnRotationFromSurfaceRelative(
                vesselNode,
                bodyName,
                bodyRotation,
                surfaceRelativeRotation,
                $"{context}, source={source}");
        }

        internal static CelestialBody ResolveSpawnRotationBody(
            Recording rec,
            TrajectoryPoint? point = null)
        {
            string bodyName = null;
            if (point.HasValue && !string.IsNullOrEmpty(point.Value.bodyName))
                bodyName = point.Value.bodyName;
            if (string.IsNullOrEmpty(bodyName))
                RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out bodyName);

            return string.IsNullOrEmpty(bodyName)
                ? null
                : FlightGlobals.GetBodyByName(bodyName);
        }
    }
}
