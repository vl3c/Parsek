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

        internal static bool TryGetExplicitEndpointBodyName(Recording rec, out string bodyName)
        {
            bodyName = null;
            if (rec == null)
                return false;

            RecordingEndpointPhase phase;
            if (TryGetPersistedEndpointDecision(rec, out phase, out bodyName)
                || TryComputeEndpointDecisionFromData(rec, out phase, out bodyName))
            {
                return phase != RecordingEndpointPhase.OrbitSegment
                    && !string.IsNullOrEmpty(bodyName);
            }

            bodyName = null;
            return false;
        }

        internal static bool TryGetPreferredEndpointBodyName(IPlaybackTrajectory traj, out string bodyName)
        {
            bodyName = null;
            if (traj == null)
                return false;

            if (TryGetPersistedEndpointDecision(traj, out _, out bodyName))
                return true;

            if (TryGetTerminalOrbitAlignedOrbitDecision(traj, out bodyName))
                return true;

            if (ShouldUseOrbitEndpointByHeuristic(traj)
                && traj.OrbitSegments != null
                && traj.OrbitSegments.Count > 0)
            {
                OrbitSegment lastSegment = traj.OrbitSegments[traj.OrbitSegments.Count - 1];
                if (!string.IsNullOrEmpty(lastSegment.bodyName))
                {
                    bodyName = lastSegment.bodyName;
                    return true;
                }
            }

            if (ShouldUseOrbitEndpoint(rec))
            {
                string orbitBody = rec.OrbitSegments[rec.OrbitSegments.Count - 1].bodyName;
                if (!string.IsNullOrEmpty(orbitBody))
                {
                    bodyName = orbitBody;
                    return true;
                }
            }

            if (rec.Points != null && rec.Points.Count > 0)
            {
                string pointBody = rec.Points[rec.Points.Count - 1].bodyName;
                if (!string.IsNullOrEmpty(pointBody))
                {
                    bodyName = pointBody;
                    return true;
                }
            }

            if (rec.SurfacePos.HasValue && !string.IsNullOrEmpty(rec.SurfacePos.Value.body))
            {
                bodyName = rec.SurfacePos.Value.body;
                return true;
            }

            return false;
        }

        internal static bool ShouldUseOrbitEndpoint(IPlaybackTrajectory traj)
        {
            if (traj?.OrbitSegments == null || traj.OrbitSegments.Count == 0)
                return false;

            if (traj.Points == null || traj.Points.Count == 0)
                return true;

            double pointEndUT = traj.Points[traj.Points.Count - 1].ut;
            double orbitEndUT = traj.OrbitSegments[traj.OrbitSegments.Count - 1].endUT;
            return orbitEndUT > pointEndUT + EndpointEpsilon;
        }

        internal static bool TryGetOrbitEndpointUT(IPlaybackTrajectory traj, out double ut)
        {
            ut = 0.0;
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
            bodyName = string.IsNullOrEmpty(segment.bodyName) ? "Kerbin" : segment.bodyName;

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

            if (rec.TerminalPosition.HasValue)
            {
                var terminal = rec.TerminalPosition.Value;
                bodyName = string.IsNullOrEmpty(terminal.body) ? "Kerbin" : terminal.body;
                latitude = terminal.latitude;
                longitude = terminal.longitude;
                altitude = terminal.altitude;
                return true;
            }

            if (TryGetOrbitEndpointCoordinates(rec, out bodyName, out latitude, out longitude, out altitude))
                return true;

            if (rec.Points != null && rec.Points.Count > 0)
            {
                var lastPoint = rec.Points[rec.Points.Count - 1];
                bodyName = string.IsNullOrEmpty(lastPoint.bodyName) ? "Kerbin" : lastPoint.bodyName;
                latitude = lastPoint.latitude;
                longitude = lastPoint.longitude;
                altitude = lastPoint.altitude;
                return true;
            }

            if (rec.SurfacePos.HasValue)
            {
                var surface = rec.SurfacePos.Value;
                bodyName = string.IsNullOrEmpty(surface.body) ? "Kerbin" : surface.body;
                latitude = surface.latitude;
                longitude = surface.longitude;
                altitude = surface.altitude;
                return true;
            }

            return false;
        }

        internal static string GetPreferredEndpointBodyName(Recording rec)
        {
            return TryGetPreferredEndpointBodyName(rec, out string bodyName)
                ? bodyName
                : "Kerbin";
        }
    }
}
