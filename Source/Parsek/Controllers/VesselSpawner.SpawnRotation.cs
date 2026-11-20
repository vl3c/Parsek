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

        /// <summary>
        /// The body name a spawn of <paramref name="rec"/> places the vessel in. A spawn on
        /// the recorded terminal orbit (<see cref="ShouldUseRecordedTerminalOrbitSpawnState"/>)
        /// reads the recording's preferred endpoint body first, as the end-of-recording spawn
        /// does: the last trajectory point can sit in another sphere of influence than the
        /// terminal orbit (tree mode adds no point at an on-rails sphere change), and the
        /// orbit build refuses a body that is not the orbit's. Any other spawn reads the
        /// point's body first (<see cref="ResolveSpawnRotationBody"/>). Falls back to the
        /// other source when the preferred one has no body.
        /// </summary>
        internal static string ResolveSpawnBodyName(Recording rec, TrajectoryPoint? point)
        {
            string pointBody = point.HasValue && !string.IsNullOrEmpty(point.Value.bodyName)
                ? point.Value.bodyName
                : null;
            string endpointBody = null;
            if (rec != null)
                RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out endpointBody);
            if (string.IsNullOrEmpty(endpointBody))
                endpointBody = null;

            if (ShouldUseRecordedTerminalOrbitSpawnState(rec))
            {
                if (endpointBody != null
                    && pointBody != null
                    && !string.Equals(endpointBody, pointBody, StringComparison.Ordinal))
                {
                    ParsekLog.VerboseRateLimited("Spawner",
                        "spawn-body-endpoint-over-point." + (rec?.RecordingId ?? "(null)"),
                        string.Format(CultureInfo.InvariantCulture,
                            "Spawn body for orbit-route rec={0}: endpoint body {1} over last point body {2}",
                            rec?.RecordingId ?? "(null)",
                            endpointBody,
                            pointBody),
                        minIntervalSeconds: 30.0);
                }
                return endpointBody ?? pointBody;
            }

            return pointBody ?? endpointBody;
        }

        /// <summary>
        /// The live body a chain-tip or Ready-reference spawn at <paramref name="ut"/> places
        /// the vessel in: <see cref="ResolveSpawnBodyName"/>, then, for an escape trajectory,
        /// the sphere its path has reached by then (<see cref="ResolveEscapeSpawnBody"/>).
        /// </summary>
        internal static CelestialBody ResolveSpawnBody(Recording rec, TrajectoryPoint? point, double ut)
        {
            string bodyName = ResolveSpawnBodyName(rec, point);
            CelestialBody body = string.IsNullOrEmpty(bodyName)
                ? null
                : FlightGlobals.GetBodyByName(bodyName);
            return ResolveEscapeSpawnBody(rec, body, ut);
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
