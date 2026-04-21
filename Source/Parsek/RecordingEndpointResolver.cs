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

        internal static string GetPreferredEndpointBodyName(Recording rec)
            => TryGetPreferredEndpointBodyName(rec, out string bodyName) ? bodyName : "Kerbin";

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

            if (traj.Points != null && traj.Points.Count > 0)
            {
                string pointBody = traj.Points[traj.Points.Count - 1].bodyName;
                if (!string.IsNullOrEmpty(pointBody))
                {
                    bodyName = pointBody;
                    return true;
                }
            }

            if (traj.SurfacePos.HasValue && !string.IsNullOrEmpty(traj.SurfacePos.Value.body))
            {
                bodyName = traj.SurfacePos.Value.body;
                return true;
            }

            return false;
        }
        internal static bool RefreshEndpointDecision(
            Recording rec,
            string context = null,
            bool logDecision = true)
        {
            if (rec == null)
                return false;

            RecordingEndpointPhase phaseBefore = rec.EndpointPhase;
            string bodyBefore = rec.EndpointBodyName;

            if (!TryComputeEndpointDecisionFromData(rec, out RecordingEndpointPhase phase, out string bodyName))
            {
                bool hadPersistedDecision = rec.EndpointPhase != RecordingEndpointPhase.Unknown
                    || !string.IsNullOrEmpty(rec.EndpointBodyName);
                rec.EndpointPhase = RecordingEndpointPhase.Unknown;
                rec.EndpointBodyName = null;
                if (logDecision)
                {
                    LogEndpointDecision(
                        "RefreshEndpointDecision",
                        context,
                        rec,
                        phaseBefore,
                        bodyBefore,
                        rec.EndpointPhase,
                        rec.EndpointBodyName,
                        hadPersistedDecision,
                        resolved: false);
                }
                return hadPersistedDecision;
            }

            bool changed = rec.EndpointPhase != phase
                || !string.Equals(rec.EndpointBodyName, bodyName, StringComparison.Ordinal);
            rec.EndpointPhase = phase;
            rec.EndpointBodyName = bodyName;
            if (logDecision)
            {
                LogEndpointDecision(
                    "RefreshEndpointDecision",
                    context,
                    rec,
                    phaseBefore,
                    bodyBefore,
                    rec.EndpointPhase,
                    rec.EndpointBodyName,
                    changed,
                    resolved: true);
            }
            return changed;
        }

        internal static bool BackfillEndpointDecision(Recording rec, string context = null)
        {
            if (rec == null)
                return false;

            RecordingEndpointPhase phaseBefore = rec.EndpointPhase;
            string bodyBefore = rec.EndpointBodyName;
            if (TryGetPersistedEndpointDecision(rec, out _, out _))
            {
                LogEndpointDecision(
                    "BackfillEndpointDecision",
                    context,
                    rec,
                    phaseBefore,
                    bodyBefore,
                    rec.EndpointPhase,
                    rec.EndpointBodyName,
                    changed: false,
                    resolved: true,
                    skippedPersisted: true);
                return false;
            }

            if (TryGetLegacyTerminalOrbitBackfillDecision(rec, out RecordingEndpointPhase legacyPhase, out string legacyBodyName))
            {
                bool legacyChanged = rec.EndpointPhase != legacyPhase
                    || !string.Equals(rec.EndpointBodyName, legacyBodyName, StringComparison.Ordinal);
                rec.EndpointPhase = legacyPhase;
                rec.EndpointBodyName = legacyBodyName;
                LogEndpointDecision(
                    "BackfillEndpointDecision",
                    context,
                    rec,
                    phaseBefore,
                    bodyBefore,
                    rec.EndpointPhase,
                    rec.EndpointBodyName,
                    legacyChanged,
                    resolved: true);
                return legacyChanged;
            }

            bool changed = RefreshEndpointDecision(rec, context, logDecision: false);
            LogEndpointDecision(
                "BackfillEndpointDecision",
                context,
                rec,
                phaseBefore,
                bodyBefore,
                rec.EndpointPhase,
                rec.EndpointBodyName,
                changed,
                resolved: rec.EndpointPhase != RecordingEndpointPhase.Unknown
                    || !string.IsNullOrEmpty(rec.EndpointBodyName));
            return changed;
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

        internal static bool TryGetEndpointAlignedOrbitSeed(
            IPlaybackTrajectory traj,
            out double inclination,
            out double eccentricity,
            out double semiMajorAxis,
            out double lan,
            out double argumentOfPeriapsis,
            out double meanAnomalyAtEpoch,
            out double epoch,
            out string bodyName)
        {
            inclination = 0.0;
            eccentricity = 0.0;
            semiMajorAxis = 0.0;
            lan = 0.0;
            argumentOfPeriapsis = 0.0;
            meanAnomalyAtEpoch = 0.0;
            epoch = 0.0;
            bodyName = null;

            if (traj == null)
                return false;

            if (!TryGetPreferredEndpointBodyName(traj, out string endpointBody))
                return false;

            bool endpointUsesOrbitSegment = ShouldUseOrbitEndpoint(traj);

            bool TryGetLastMatchingSegment(out double segInclination,
                out double segEccentricity,
                out double segSemiMajorAxis,
                out double segLan,
                out double segArgumentOfPeriapsis,
                out double segMeanAnomalyAtEpoch,
                out double segEpoch,
                out string segBodyName)
            {
                segInclination = 0.0;
                segEccentricity = 0.0;
                segSemiMajorAxis = 0.0;
                segLan = 0.0;
                segArgumentOfPeriapsis = 0.0;
                segMeanAnomalyAtEpoch = 0.0;
                segEpoch = 0.0;
                segBodyName = null;

                if (traj.OrbitSegments == null)
                    return false;

                for (int i = traj.OrbitSegments.Count - 1; i >= 0; i--)
                {
                    OrbitSegment seg = traj.OrbitSegments[i];
                    if (!string.Equals(seg.bodyName, endpointBody, StringComparison.Ordinal)
                        || seg.semiMajorAxis <= 0.0)
                    {
                        continue;
                    }

                    segInclination = seg.inclination;
                    segEccentricity = seg.eccentricity;
                    segSemiMajorAxis = seg.semiMajorAxis;
                    segLan = seg.longitudeOfAscendingNode;
                    segArgumentOfPeriapsis = seg.argumentOfPeriapsis;
                    segMeanAnomalyAtEpoch = seg.meanAnomalyAtEpoch;
                    segEpoch = seg.epoch;
                    segBodyName = seg.bodyName;
                    return true;
                }

                return false;
            }

            bool hasMatchingTerminalOrbit = CanUseTerminalOrbitSeedForEndpoint(traj, endpointBody);

            if (endpointUsesOrbitSegment)
            {
                if (TryGetLastMatchingSegment(
                    out inclination,
                    out eccentricity,
                    out semiMajorAxis,
                    out lan,
                    out argumentOfPeriapsis,
                    out meanAnomalyAtEpoch,
                    out epoch,
                    out bodyName))
                {
                    return true;
                }

                if (!hasMatchingTerminalOrbit)
                    return false;
            }

            if (hasMatchingTerminalOrbit)
            {
                inclination = traj.TerminalOrbitInclination;
                eccentricity = traj.TerminalOrbitEccentricity;
                semiMajorAxis = traj.TerminalOrbitSemiMajorAxis;
                lan = traj.TerminalOrbitLAN;
                argumentOfPeriapsis = traj.TerminalOrbitArgumentOfPeriapsis;
                meanAnomalyAtEpoch = traj.TerminalOrbitMeanAnomalyAtEpoch;
                epoch = traj.TerminalOrbitEpoch;
                bodyName = traj.TerminalOrbitBody;
                return true;
            }

            return TryGetLastMatchingSegment(
                out inclination,
                out eccentricity,
                out semiMajorAxis,
                out lan,
                out argumentOfPeriapsis,
                out meanAnomalyAtEpoch,
                out epoch,
                out bodyName);
        }

        private static bool CanUseTerminalOrbitSeedForEndpoint(
            IPlaybackTrajectory traj,
            string endpointBody)
        {
            if (!HasRecordedTerminalOrbit(traj)
                || string.IsNullOrEmpty(endpointBody)
                || !string.Equals(traj.TerminalOrbitBody, endpointBody, StringComparison.Ordinal))
            {
                return false;
            }

            if (!traj.TerminalStateValue.HasValue)
                return true;

            switch (traj.TerminalStateValue.Value)
            {
                case TerminalState.Orbiting:
                case TerminalState.SubOrbital:
                case TerminalState.Docked:
                    return true;
                default:
                    return false;
            }
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
                : persistedBody;
            if (string.IsNullOrEmpty(bodyName))
                return false;

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
                            : resolvedBodyName;
                        if (string.IsNullOrEmpty(bodyName))
                            return false;
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
                            : resolvedBodyName;
                        if (string.IsNullOrEmpty(bodyName))
                            return false;
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
                            : resolvedBodyName;
                        if (string.IsNullOrEmpty(bodyName))
                            return false;
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

        private static bool TryGetLegacyTerminalOrbitBackfillDecision(
            Recording rec,
            out RecordingEndpointPhase phase,
            out string bodyName)
        {
            phase = RecordingEndpointPhase.Unknown;
            bodyName = null;
            if (rec?.OrbitSegments == null || rec.OrbitSegments.Count == 0)
                return false;

            if (!HasRecordedTerminalOrbit(rec)
                || !rec.TerminalStateValue.HasValue)
            {
                return false;
            }

            TerminalState terminalState = rec.TerminalStateValue.Value;
            if (terminalState != TerminalState.Orbiting
                && terminalState != TerminalState.SubOrbital
                && terminalState != TerminalState.Docked)
            {
                return false;
            }

            OrbitSegment lastSegment = rec.OrbitSegments[rec.OrbitSegments.Count - 1];
            if (string.IsNullOrEmpty(lastSegment.bodyName)
                || !string.Equals(lastSegment.bodyName, rec.TerminalOrbitBody, StringComparison.Ordinal))
            {
                return false;
            }

            if (rec.Points != null && rec.Points.Count > 0)
            {
                TrajectoryPoint lastPoint = rec.Points[rec.Points.Count - 1];
                if (!string.IsNullOrEmpty(lastPoint.bodyName)
                    && !string.Equals(lastPoint.bodyName, rec.TerminalOrbitBody, StringComparison.Ordinal)
                    && lastSegment.endUT <= lastPoint.ut + EndpointEpsilon)
                {
                    ParsekLog.Verbose("EndpointDecision",
                        "TryGetLegacyTerminalOrbitBackfillDecision: backfilling endpoint phase from terminal orbit " +
                        $"terminalBody={rec.TerminalOrbitBody} pointBody={lastPoint.bodyName} " +
                        $"segmentEndUT={lastSegment.endUT:F3} pointUT={lastPoint.ut:F3}");
                    phase = RecordingEndpointPhase.OrbitSegment;
                    bodyName = rec.TerminalOrbitBody;
                    return true;
                }
            }

            return false;
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

            if (traj.Points != null && traj.Points.Count > 0)
            {
                TrajectoryPoint lastPoint = traj.Points[traj.Points.Count - 1];
                if (!string.IsNullOrEmpty(lastPoint.bodyName)
                    && !string.Equals(lastPoint.bodyName, traj.TerminalOrbitBody, StringComparison.Ordinal)
                    && lastSegment.endUT <= lastPoint.ut + EndpointEpsilon)
                {
                    ParsekLog.Verbose("EndpointDecision",
                        "TryGetTerminalOrbitAlignedOrbitDecision: rejected terminal-orbit match " +
                        $"terminalBody={traj.TerminalOrbitBody} pointBody={lastPoint.bodyName} " +
                        $"segmentEndUT={lastSegment.endUT:F3} pointUT={lastPoint.ut:F3}");
                    return false;
                }
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

        private static void LogEndpointDecision(
            string action,
            string context,
            Recording rec,
            RecordingEndpointPhase phaseBefore,
            string bodyBefore,
            RecordingEndpointPhase phaseAfter,
            string bodyAfter,
            bool changed,
            bool resolved,
            bool skippedPersisted = false)
        {
            string safeContext = string.IsNullOrEmpty(context) ? "unspecified" : context;
            string beforeBody = string.IsNullOrEmpty(bodyBefore) ? "(none)" : bodyBefore;
            string afterBody = string.IsNullOrEmpty(bodyAfter) ? "(none)" : bodyAfter;
            string status;
            if (skippedPersisted)
                status = "kept-persisted";
            else if (!resolved)
                status = "cleared";
            else
                status = "resolved";

            ParsekLog.Verbose("EndpointDecision",
                $"{action}: context={safeContext} rec={rec.RecordingId} " +
                $"before=({phaseBefore},{beforeBody}) after=({phaseAfter},{afterBody}) " +
                $"status={status} changed={changed}");
        }
    }
}
