using System;

namespace Parsek
{
    internal static class RecordingEndpointResolver
    {
        private const double EndpointEpsilon = 1e-6;

        internal static bool TryGetPreferredEndpointBodyName(Recording rec, out string bodyName)
        {
            bodyName = null;
            if (rec == null)
                return false;

            if (TryGetPersistedEndpointDecision(rec, out _, out bodyName))
                return true;

            return TryComputeEndpointDecisionFromData(rec, out _, out bodyName);
        }

        internal static bool RefreshEndpointDecision(Recording rec)
        {
            if (rec == null)
                return false;

            if (!TryComputeEndpointDecisionFromData(rec, out RecordingEndpointPhase phase, out string bodyName))
            {
                bool hadPersistedDecision = rec.EndpointPhase != RecordingEndpointPhase.Unknown
                    || !string.IsNullOrEmpty(rec.EndpointBodyName);
                rec.EndpointPhase = RecordingEndpointPhase.Unknown;
                rec.EndpointBodyName = null;
                return hadPersistedDecision;
            }

            bool changed = rec.EndpointPhase != phase
                || !string.Equals(rec.EndpointBodyName, bodyName, StringComparison.Ordinal);
            rec.EndpointPhase = phase;
            rec.EndpointBodyName = bodyName;
            return changed;
        }

        internal static bool BackfillEndpointDecision(Recording rec)
        {
            if (rec == null)
                return false;

            if (TryGetPersistedEndpointDecision(rec, out _, out _))
                return false;

            return RefreshEndpointDecision(rec);
        }

        internal static bool ShouldUseOrbitEndpoint(IPlaybackTrajectory traj)
        {
            if (traj?.OrbitSegments == null || traj.OrbitSegments.Count == 0)
                return false;

            if (TryGetPersistedEndpointDecision(traj, out RecordingEndpointPhase phase, out _))
                return phase == RecordingEndpointPhase.OrbitSegment;

            if (TryGetTerminalOrbitAlignedOrbitDecision(traj, out _))
                return true;

            return ShouldUseOrbitEndpointByHeuristic(traj);
        }

        internal static bool TryGetOrbitEndpointUT(IPlaybackTrajectory traj, out double ut)
        {
            ut = 0.0;
            if (traj?.OrbitSegments == null || traj.OrbitSegments.Count == 0)
                return false;

            if (!ShouldUseOrbitEndpoint(traj))
                return false;

            ut = traj.OrbitSegments[traj.OrbitSegments.Count - 1].endUT;
            return true;
        }

        internal static bool TryGetOrbitEndpointCoordinates(
            IPlaybackTrajectory traj,
            out string bodyName,
            out double latitude,
            out double longitude,
            out double altitude)
        {
            bodyName = null;
            latitude = 0.0;
            longitude = 0.0;
            altitude = 0.0;

            if (!TryGetOrbitEndpointUT(traj, out double endpointUT))
                return false;

            OrbitSegment segment = traj.OrbitSegments[traj.OrbitSegments.Count - 1];
            string persistedBody = null;
            if (TryGetPersistedEndpointDecision(traj, out RecordingEndpointPhase phase, out persistedBody)
                && phase == RecordingEndpointPhase.OrbitSegment
                && !string.IsNullOrEmpty(segment.bodyName)
                && !string.Equals(segment.bodyName, persistedBody, StringComparison.Ordinal))
            {
                return false;
            }

            bodyName = !string.IsNullOrEmpty(segment.bodyName)
                ? segment.bodyName
                : !string.IsNullOrEmpty(persistedBody) ? persistedBody : "Kerbin";

            CelestialBody body = FlightGlobals.GetBodyByName(bodyName);
            if (body == null)
                return false;

            (latitude, longitude, altitude) = GhostExtender.PropagateOrbital(
                segment.inclination,
                segment.eccentricity,
                segment.semiMajorAxis,
                segment.longitudeOfAscendingNode,
                segment.argumentOfPeriapsis,
                segment.meanAnomalyAtEpoch,
                segment.epoch,
                body.Radius,
                body.gravParameter,
                endpointUT);
            return true;
        }

        internal static bool TryGetRecordingEndpointCoordinates(
            Recording rec,
            out string bodyName,
            out double latitude,
            out double longitude,
            out double altitude)
        {
            bodyName = null;
            latitude = 0.0;
            longitude = 0.0;
            altitude = 0.0;

            if (rec == null)
                return false;

            RecordingEndpointPhase phase;
            string resolvedBodyName;
            if (TryGetPersistedEndpointDecision(rec, out phase, out resolvedBodyName)
                && TryGetCoordinatesForPhase(
                    rec,
                    phase,
                    resolvedBodyName,
                    out bodyName,
                    out latitude,
                    out longitude,
                    out altitude))
            {
                return true;
            }

            return TryComputeEndpointDecisionFromData(rec, out phase, out resolvedBodyName)
                && TryGetCoordinatesForPhase(
                    rec,
                    phase,
                    resolvedBodyName,
                    out bodyName,
                    out latitude,
                    out longitude,
                    out altitude);
        }

        internal static string GetPreferredEndpointBodyName(Recording rec)
        {
            return TryGetPreferredEndpointBodyName(rec, out string bodyName)
                ? bodyName
                : "Kerbin";
        }

        internal static bool TryGetPersistedEndpointDecision(
            IPlaybackTrajectory traj,
            out RecordingEndpointPhase phase,
            out string bodyName)
        {
            phase = RecordingEndpointPhase.Unknown;
            bodyName = null;

            if (traj == null)
                return false;

            if (traj.EndpointPhase == RecordingEndpointPhase.Unknown
                || string.IsNullOrEmpty(traj.EndpointBodyName))
            {
                return false;
            }

            phase = traj.EndpointPhase;
            bodyName = traj.EndpointBodyName;
            return true;
        }

        private static bool TryComputeEndpointDecisionFromData(
            Recording rec,
            out RecordingEndpointPhase phase,
            out string bodyName)
        {
            phase = RecordingEndpointPhase.Unknown;
            bodyName = null;

            if (rec == null)
                return false;

            if (rec.TerminalPosition.HasValue && !string.IsNullOrEmpty(rec.TerminalPosition.Value.body))
            {
                phase = RecordingEndpointPhase.TerminalPosition;
                bodyName = rec.TerminalPosition.Value.body;
                return true;
            }

            if (TryGetTerminalOrbitAlignedOrbitDecision(rec, out bodyName))
            {
                phase = RecordingEndpointPhase.OrbitSegment;
                return true;
            }

            if (ShouldUseOrbitEndpointByHeuristic(rec))
            {
                string orbitBody = rec.OrbitSegments[rec.OrbitSegments.Count - 1].bodyName;
                if (!string.IsNullOrEmpty(orbitBody))
                {
                    phase = RecordingEndpointPhase.OrbitSegment;
                    bodyName = orbitBody;
                    return true;
                }
            }

            if (rec.Points != null && rec.Points.Count > 0)
            {
                string pointBody = rec.Points[rec.Points.Count - 1].bodyName;
                if (!string.IsNullOrEmpty(pointBody))
                {
                    phase = RecordingEndpointPhase.TrajectoryPoint;
                    bodyName = pointBody;
                    return true;
                }
            }

            if (rec.SurfacePos.HasValue && !string.IsNullOrEmpty(rec.SurfacePos.Value.body))
            {
                phase = RecordingEndpointPhase.SurfacePosition;
                bodyName = rec.SurfacePos.Value.body;
                return true;
            }

            return false;
        }

        private static bool TryGetCoordinatesForPhase(
            Recording rec,
            RecordingEndpointPhase phase,
            string resolvedBodyName,
            out string bodyName,
            out double latitude,
            out double longitude,
            out double altitude)
        {
            bodyName = null;
            latitude = 0.0;
            longitude = 0.0;
            altitude = 0.0;

            switch (phase)
            {
                case RecordingEndpointPhase.TerminalPosition:
                    if (rec.TerminalPosition.HasValue)
                    {
                        var terminal = rec.TerminalPosition.Value;
                        bodyName = !string.IsNullOrEmpty(terminal.body)
                            ? terminal.body
                            : string.IsNullOrEmpty(resolvedBodyName) ? "Kerbin" : resolvedBodyName;
                        latitude = terminal.latitude;
                        longitude = terminal.longitude;
                        altitude = terminal.altitude;
                        return true;
                    }
                    break;

                case RecordingEndpointPhase.OrbitSegment:
                    if (TryGetOrbitEndpointCoordinates(rec, out bodyName, out latitude, out longitude, out altitude))
                        return true;
                    break;

                case RecordingEndpointPhase.TrajectoryPoint:
                    if (rec.Points != null && rec.Points.Count > 0)
                    {
                        var lastPoint = rec.Points[rec.Points.Count - 1];
                        bodyName = !string.IsNullOrEmpty(lastPoint.bodyName)
                            ? lastPoint.bodyName
                            : string.IsNullOrEmpty(resolvedBodyName) ? "Kerbin" : resolvedBodyName;
                        latitude = lastPoint.latitude;
                        longitude = lastPoint.longitude;
                        altitude = lastPoint.altitude;
                        return true;
                    }
                    break;

                case RecordingEndpointPhase.SurfacePosition:
                    if (rec.SurfacePos.HasValue)
                    {
                        var surface = rec.SurfacePos.Value;
                        bodyName = !string.IsNullOrEmpty(surface.body)
                            ? surface.body
                            : string.IsNullOrEmpty(resolvedBodyName) ? "Kerbin" : resolvedBodyName;
                        latitude = surface.latitude;
                        longitude = surface.longitude;
                        altitude = surface.altitude;
                        return true;
                    }
                    break;
            }

            return false;
        }

        private static bool ShouldUseOrbitEndpointByHeuristic(IPlaybackTrajectory traj)
        {
            if (traj?.OrbitSegments == null || traj.OrbitSegments.Count == 0)
                return false;

            if (traj.Points == null || traj.Points.Count == 0)
                return true;

            double pointEndUT = traj.Points[traj.Points.Count - 1].ut;
            double orbitEndUT = traj.OrbitSegments[traj.OrbitSegments.Count - 1].endUT;
            return orbitEndUT > pointEndUT + EndpointEpsilon;
        }

        private static bool TryGetTerminalOrbitAlignedOrbitDecision(
            IPlaybackTrajectory traj,
            out string bodyName)
        {
            bodyName = null;
            if (traj?.OrbitSegments == null || traj.OrbitSegments.Count == 0)
                return false;

            if (!HasRecordedTerminalOrbit(traj))
                return false;

            if (traj.TerminalStateValue.HasValue)
            {
                TerminalState terminalState = traj.TerminalStateValue.Value;
                if (terminalState != TerminalState.Orbiting
                    && terminalState != TerminalState.SubOrbital
                    && terminalState != TerminalState.Docked)
                {
                    return false;
                }
            }

            OrbitSegment lastSegment = traj.OrbitSegments[traj.OrbitSegments.Count - 1];
            if (string.IsNullOrEmpty(lastSegment.bodyName)
                || !string.Equals(lastSegment.bodyName, traj.TerminalOrbitBody, StringComparison.Ordinal))
            {
                return false;
            }

            bodyName = traj.TerminalOrbitBody;
            return true;
        }

        private static bool HasRecordedTerminalOrbit(IPlaybackTrajectory traj)
        {
            return traj != null
                && !string.IsNullOrEmpty(traj.TerminalOrbitBody)
                && traj.TerminalOrbitSemiMajorAxis > 0.0;
        }
    }
}
