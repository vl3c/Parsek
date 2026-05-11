using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    internal enum OrbitSegmentValidationMode
    {
        PreserveLegacyBehavior,
        ValidateAndLog
    }

    internal enum OrbitRejectionReason
    {
        None,
        NonFiniteElements,
        InvalidEccentricity,
        BelowMinSma,
        MissingBody,
        OrbitConstructionFailed
    }

    internal enum OrbitWorldPositionFailureReason
    {
        None,
        NoSegment,
        SegmentRejected,
        MissingBody,
        OrbitConstructionFailed,
        OrbitPositionFailed,
        NonFinitePositionBeforeBodyApi,
        BodyApiFailed,
        NonFinitePositionAfterClamp
    }

    internal struct OrbitPlacementResult
    {
        public Orbit Orbit;
        public CelestialBody Body;
        public Vector3d RawWorldPosition;
        public Vector3d AppliedOffset;
        public Vector3d WorldPosition;
        public Vector3d Velocity;
        public double Altitude;
        public bool TerrainClamped;
    }

    internal static class OrbitResolution
    {
        internal const double MinValidSmaMeters = 1.0;

        internal static bool IsFiniteDouble(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        internal static bool IsFiniteVector3d(Vector3d value)
        {
            return IsFiniteDouble(value.x)
                && IsFiniteDouble(value.y)
                && IsFiniteDouble(value.z);
        }

        internal static bool IsFiniteVector3(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        internal static bool IsFiniteQuaternion(Quaternion value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z)
                && !float.IsNaN(value.w) && !float.IsInfinity(value.w);
        }

        internal static bool IsFiniteOrbitSegment(OrbitSegment segment)
        {
            return IsFiniteDouble(segment.inclination)
                && IsFiniteDouble(segment.eccentricity)
                && IsFiniteDouble(segment.semiMajorAxis)
                && IsFiniteDouble(segment.longitudeOfAscendingNode)
                && IsFiniteDouble(segment.argumentOfPeriapsis)
                && IsFiniteDouble(segment.meanAnomalyAtEpoch)
                && IsFiniteDouble(segment.epoch);
        }

        internal static bool TryValidateOrbitSegment(
            OrbitSegment segment,
            Func<string, CelestialBody> bodyResolver,
            OrbitSegmentValidationMode mode,
            string recordingId,
            string context,
            out CelestialBody body,
            out OrbitRejectionReason reason)
        {
            body = null;
            reason = OrbitRejectionReason.None;

            if (!IsFiniteOrbitSegment(segment))
            {
                reason = OrbitRejectionReason.NonFiniteElements;
                LogOrbitSegmentRejected(segment, recordingId, context, reason, mode);
                return false;
            }

            if (segment.eccentricity < 0.0)
            {
                reason = OrbitRejectionReason.InvalidEccentricity;
                LogOrbitSegmentRejected(segment, recordingId, context, reason, mode);
                return false;
            }

            double absSma = Math.Abs(segment.semiMajorAxis);
            if (!IsFiniteDouble(absSma) || absSma < MinValidSmaMeters)
            {
                reason = OrbitRejectionReason.BelowMinSma;
                LogOrbitSegmentRejected(segment, recordingId, context, reason, mode);
                return false;
            }

            if (bodyResolver == null || string.IsNullOrEmpty(segment.bodyName))
            {
                reason = OrbitRejectionReason.MissingBody;
                LogOrbitSegmentRejected(segment, recordingId, context, reason, mode);
                return false;
            }

            try
            {
                body = bodyResolver(segment.bodyName);
            }
            catch
            {
                body = null;
            }

            if (object.ReferenceEquals(body, null))
            {
                reason = OrbitRejectionReason.MissingBody;
                LogOrbitSegmentRejected(segment, recordingId, context, reason, mode);
                return false;
            }

            return true;
        }

        internal static bool TryValidateOrbitElements(
            double inclination,
            double eccentricity,
            double semiMajorAxis,
            double longitudeOfAscendingNode,
            double argumentOfPeriapsis,
            double meanAnomalyAtEpoch,
            double epoch,
            string bodyName,
            Func<string, CelestialBody> bodyResolver,
            OrbitSegmentValidationMode mode,
            string recordingId,
            string context,
            out CelestialBody body,
            out OrbitRejectionReason reason)
        {
            var segment = new OrbitSegment
            {
                inclination = inclination,
                eccentricity = eccentricity,
                semiMajorAxis = semiMajorAxis,
                longitudeOfAscendingNode = longitudeOfAscendingNode,
                argumentOfPeriapsis = argumentOfPeriapsis,
                meanAnomalyAtEpoch = meanAnomalyAtEpoch,
                epoch = epoch,
                bodyName = bodyName
            };

            return TryValidateOrbitSegment(
                segment,
                bodyResolver,
                mode,
                recordingId,
                context,
                out body,
                out reason);
        }

        internal static bool TryCreateOrbitFromSegment(
            OrbitSegment segment,
            Func<string, CelestialBody> bodyResolver,
            OrbitSegmentValidationMode mode,
            string recordingId,
            string context,
            out Orbit orbit,
            out CelestialBody body,
            out OrbitRejectionReason reason)
        {
            orbit = null;
            if (!TryValidateOrbitSegment(
                    segment, bodyResolver, mode, recordingId, context, out body, out reason))
            {
                return false;
            }

            try
            {
                orbit = new Orbit(
                    segment.inclination,
                    segment.eccentricity,
                    segment.semiMajorAxis,
                    segment.longitudeOfAscendingNode,
                    segment.argumentOfPeriapsis,
                    segment.meanAnomalyAtEpoch,
                    segment.epoch,
                    body);
            }
            catch
            {
                orbit = null;
                reason = OrbitRejectionReason.OrbitConstructionFailed;
                LogOrbitSegmentRejected(segment, recordingId, context, reason, mode);
                return false;
            }

            return true;
        }

        internal static bool TryResolveOrbitFromSegment(
            OrbitSegment segment,
            long cacheKey,
            IDictionary<long, Orbit> orbitCache,
            Func<string, CelestialBody> bodyResolver,
            OrbitSegmentValidationMode mode,
            string recordingId,
            string context,
            out Orbit orbit,
            out CelestialBody body,
            out OrbitRejectionReason reason)
        {
            orbit = null;
            body = null;
            reason = OrbitRejectionReason.None;

            if (!TryValidateOrbitSegment(
                    segment, bodyResolver, mode, recordingId, context, out body, out reason))
            {
                return false;
            }

            if (orbitCache != null
                && orbitCache.TryGetValue(cacheKey, out orbit)
                && IsCachedOrbitForSegment(orbit, segment, body))
            {
                return true;
            }

            try
            {
                orbit = new Orbit(
                    segment.inclination,
                    segment.eccentricity,
                    segment.semiMajorAxis,
                    segment.longitudeOfAscendingNode,
                    segment.argumentOfPeriapsis,
                    segment.meanAnomalyAtEpoch,
                    segment.epoch,
                    body);
            }
            catch
            {
                orbit = null;
                reason = OrbitRejectionReason.OrbitConstructionFailed;
                LogOrbitSegmentRejected(segment, recordingId, context, reason, mode);
                return false;
            }

            if (orbitCache != null)
                orbitCache[cacheKey] = orbit;
            return true;
        }

        private static bool IsCachedOrbitForSegment(
            Orbit orbit,
            OrbitSegment segment,
            CelestialBody body)
        {
            if (orbit == null)
                return false;

            return object.ReferenceEquals(orbit.referenceBody, body)
                && orbit.inclination == segment.inclination
                && orbit.eccentricity == segment.eccentricity
                && orbit.semiMajorAxis == segment.semiMajorAxis
                && orbit.LAN == segment.longitudeOfAscendingNode
                && orbit.argumentOfPeriapsis == segment.argumentOfPeriapsis
                && orbit.meanAnomalyAtEpoch == segment.meanAnomalyAtEpoch
                && orbit.epoch == segment.epoch;
        }

        internal static bool TryComputeOrbitWorldPositionCore(
            Vector3d rawWorldPosition,
            Vector3d offset,
            CelestialBody body,
            bool clampToSurface,
            Func<Vector3d, double> altitudeResolver,
            Func<Vector3d, double> latitudeResolver,
            Func<Vector3d, double> longitudeResolver,
            Func<double, double, Vector3d> surfacePositionResolver,
            out OrbitPlacementResult placement,
            out OrbitWorldPositionFailureReason failureReason)
        {
            placement = default;
            failureReason = OrbitWorldPositionFailureReason.None;

            Vector3d worldPosition = rawWorldPosition + offset;
            if (!IsFiniteVector3d(worldPosition))
            {
                failureReason = OrbitWorldPositionFailureReason.NonFinitePositionBeforeBodyApi;
                return false;
            }

            double altitude = double.NaN;
            bool terrainClamped = false;
            if (clampToSurface)
            {
                try
                {
                    altitude = altitudeResolver != null
                        ? altitudeResolver(worldPosition)
                        : body.GetAltitude(worldPosition);
                    if (!IsFiniteDouble(altitude))
                    {
                        failureReason = OrbitWorldPositionFailureReason.BodyApiFailed;
                        return false;
                    }

                    if (altitude < 0.0)
                    {
                        double lat = latitudeResolver != null
                            ? latitudeResolver(worldPosition)
                            : body.GetLatitude(worldPosition);
                        double lon = longitudeResolver != null
                            ? longitudeResolver(worldPosition)
                            : body.GetLongitude(worldPosition);
                        if (!IsFiniteDouble(lat) || !IsFiniteDouble(lon))
                        {
                            failureReason = OrbitWorldPositionFailureReason.BodyApiFailed;
                            return false;
                        }

                        worldPosition = surfacePositionResolver != null
                            ? surfacePositionResolver(lat, lon)
                            : body.GetWorldSurfacePosition(lat, lon, 0.0);
                        terrainClamped = true;
                        altitude = 0.0;
                    }
                }
                catch
                {
                    failureReason = OrbitWorldPositionFailureReason.BodyApiFailed;
                    return false;
                }

                if (!IsFiniteVector3d(worldPosition))
                {
                    failureReason = OrbitWorldPositionFailureReason.NonFinitePositionAfterClamp;
                    return false;
                }
            }

            placement = new OrbitPlacementResult
            {
                Body = body,
                RawWorldPosition = rawWorldPosition,
                AppliedOffset = offset,
                WorldPosition = worldPosition,
                Altitude = altitude,
                TerrainClamped = terrainClamped
            };
            return true;
        }

        internal static bool TryComputeOrbitWorldPosition(
            Orbit orbit,
            CelestialBody body,
            double ut,
            Vector3d offset,
            bool clampToSurface,
            out OrbitPlacementResult placement,
            out OrbitWorldPositionFailureReason failureReason)
        {
            placement = default;
            failureReason = OrbitWorldPositionFailureReason.None;

            if (orbit == null || body == null)
            {
                failureReason = OrbitWorldPositionFailureReason.MissingBody;
                return false;
            }

            Vector3d rawWorldPosition;
            try
            {
                rawWorldPosition = orbit.getPositionAtUT(ut);
            }
            catch
            {
                failureReason = OrbitWorldPositionFailureReason.OrbitPositionFailed;
                return false;
            }

            if (!IsFiniteVector3d(rawWorldPosition))
            {
                failureReason = OrbitWorldPositionFailureReason.OrbitPositionFailed;
                return false;
            }

            if (!TryComputeOrbitWorldPositionCore(
                    rawWorldPosition,
                    offset,
                    body,
                    clampToSurface,
                    null,
                    null,
                    null,
                    null,
                    out placement,
                    out failureReason))
            {
                return false;
            }

            placement.Orbit = orbit;
            placement.Body = body;
            try
            {
                placement.Velocity = orbit.getOrbitalVelocityAtUT(ut);
            }
            catch
            {
                placement.Velocity = Vector3d.zero;
            }
            if (!IsFiniteVector3d(placement.Velocity))
                placement.Velocity = Vector3d.zero;

            return true;
        }

        internal static bool TryResolveOrbitWorldPosition(
            List<OrbitSegment> segments,
            double targetUT,
            long orbitCacheBase,
            IDictionary<long, Orbit> orbitCache,
            Func<string, CelestialBody> bodyResolver,
            OrbitSegmentValidationMode mode,
            string recordingId,
            string context,
            bool clampToSurface,
            out OrbitPlacementResult placement,
            out OrbitWorldPositionFailureReason failureReason)
        {
            placement = default;
            failureReason = OrbitWorldPositionFailureReason.NoSegment;
            if (segments == null || segments.Count == 0)
                return false;

            bool sawRejectedSegment = false;
            for (int i = 0; i < segments.Count; i++)
            {
                OrbitSegment segment = segments[i];
                if (!IsSegmentActiveAtUT(segment, targetUT, i == segments.Count - 1))
                    continue;

                if (!TryResolveOrbitFromSegment(
                        segment,
                        orbitCacheBase + i,
                        orbitCache,
                        bodyResolver,
                        mode,
                        recordingId,
                        context,
                        out Orbit orbit,
                        out CelestialBody body,
                        out _))
                {
                    sawRejectedSegment = true;
                    failureReason = OrbitWorldPositionFailureReason.SegmentRejected;
                    continue;
                }

                if (TryComputeOrbitWorldPosition(
                    orbit,
                    body,
                    targetUT,
                    Vector3d.zero,
                    clampToSurface,
                    out placement,
                    out failureReason))
                {
                    return true;
                }

                sawRejectedSegment = true;
            }

            if (sawRejectedSegment && failureReason == OrbitWorldPositionFailureReason.NoSegment)
                failureReason = OrbitWorldPositionFailureReason.SegmentRejected;
            return false;
        }

        internal static bool IsSegmentActiveAtUT(OrbitSegment segment, double targetUT, bool isFinalSegment)
        {
            // Match TrajectoryMath.FindOrbitSegment: segment boundaries are half-open
            // except for the final segment, whose end UT is inclusive.
            return isFinalSegment
                ? targetUT >= segment.startUT && targetUT <= segment.endUT
                : targetUT >= segment.startUT && targetUT < segment.endUT;
        }

        internal static bool ShouldRejectLegacySurfaceOrbitSegment(
            IPlaybackTrajectory traj,
            OrbitSegment segment,
            double targetUT,
            string recordingId,
            string context)
        {
            if (!TryClassifyLegacySurfaceOrbitSegment(
                    traj, targetUT, out string reason))
            {
                return false;
            }

            string safeRecordingId = !string.IsNullOrEmpty(recordingId)
                ? recordingId
                : traj?.RecordingId;
            LogLegacySurfaceOrbitSegmentRejected(
                segment,
                safeRecordingId,
                context,
                reason,
                targetUT);
            return true;
        }

        internal static bool TryClassifyLegacySurfaceOrbitSegment(
            IPlaybackTrajectory traj,
            double targetUT,
            out string reason)
        {
            reason = null;
            if (traj == null)
                return false;

            if (TryGetTrackSectionEnvironmentAtUT(
                    traj.TrackSections,
                    targetUT,
                    out SegmentEnvironment activeEnvironment)
                && IsNonKeplerianLegacyOrbitEnvironment(activeEnvironment))
            {
                reason = ToLegacyEnvironmentReason(activeEnvironment) + "-track-section";
                return true;
            }

            if (HasOnlyNonKeplerianTrackSections(traj))
            {
                reason = "non-keplerian-track-sections";
                return true;
            }

            bool hasTrajectoryPoints = traj.Points != null && traj.Points.Count > 0;
            bool hasKeplerianSections = HasKeplerianTrackSections(traj);
            bool hasOnlyLowAltitudePoints = HasOnlyLegacySurfaceJunkAltitudePoints(traj);
            if (traj.SurfacePos.HasValue && !hasKeplerianSections)
            {
                reason = "surface-position";
                return true;
            }

            if (IsSurfaceTerminalState(traj.TerminalStateValue)
                && !hasKeplerianSections
                && (!hasTrajectoryPoints || hasOnlyLowAltitudePoints))
            {
                reason = "surface-terminal-state";
                return true;
            }

            if (traj is Recording recording
                && IsSurfaceStartSituation(recording.StartSituation)
                && !hasKeplerianSections
                && (!hasTrajectoryPoints || hasOnlyLowAltitudePoints))
            {
                reason = "surface-start-situation";
                return true;
            }

            return false;
        }

        private static bool TryGetTrackSectionEnvironmentAtUT(
            List<TrackSection> sections,
            double targetUT,
            out SegmentEnvironment environment)
        {
            environment = default(SegmentEnvironment);
            int sectionIdx = TrajectoryMath.FindTrackSectionForUT(sections, targetUT);
            if (sectionIdx < 0)
                return false;

            environment = sections[sectionIdx].environment;
            return true;
        }

        private static bool HasOnlyNonKeplerianTrackSections(IPlaybackTrajectory traj)
        {
            if (traj?.TrackSections == null || traj.TrackSections.Count == 0)
                return false;

            for (int i = 0; i < traj.TrackSections.Count; i++)
            {
                if (!IsNonKeplerianLegacyOrbitEnvironment(traj.TrackSections[i].environment))
                    return false;
            }

            return true;
        }

        private static bool HasKeplerianTrackSections(IPlaybackTrajectory traj)
        {
            if (traj?.TrackSections == null)
                return false;

            for (int i = 0; i < traj.TrackSections.Count; i++)
            {
                if (!IsNonKeplerianLegacyOrbitEnvironment(traj.TrackSections[i].environment))
                    return true;
            }

            return false;
        }

        private static bool IsNonKeplerianLegacyOrbitEnvironment(SegmentEnvironment environment)
        {
            return EnvironmentDetector.IsSurfaceEnvironment(environment)
                || environment == SegmentEnvironment.Atmospheric
                || environment == SegmentEnvironment.Approach;
        }

        private static string ToLegacyEnvironmentReason(SegmentEnvironment environment)
        {
            if (EnvironmentDetector.IsSurfaceEnvironment(environment))
                return "surface";
            if (environment == SegmentEnvironment.Atmospheric)
                return "atmospheric";
            if (environment == SegmentEnvironment.Approach)
                return "approach";
            return "non-keplerian";
        }

        private const double LegacySurfaceJunkPointAltitudeCeilingMeters = 5000.0;

        private static bool HasOnlyLegacySurfaceJunkAltitudePoints(IPlaybackTrajectory traj)
        {
            if (traj?.Points == null || traj.Points.Count == 0)
                return false;

            for (int i = 0; i < traj.Points.Count; i++)
            {
                double altitude = traj.Points[i].altitude;
                if (!IsFiniteDouble(altitude)
                    || altitude > LegacySurfaceJunkPointAltitudeCeilingMeters)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsSurfaceTerminalState(TerminalState? terminalState)
        {
            return terminalState == TerminalState.Landed
                || terminalState == TerminalState.Splashed
                || terminalState == TerminalState.Recovered
                || terminalState == TerminalState.Boarded;
        }

        private static bool IsSurfaceStartSituation(string startSituation)
        {
            if (string.IsNullOrEmpty(startSituation))
                return false;

            return string.Equals(startSituation, "LANDED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(startSituation, "Landed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(startSituation, "SPLASHED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(startSituation, "Splashed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(startSituation, "PRELAUNCH", StringComparison.OrdinalIgnoreCase)
                || string.Equals(startSituation, "Prelaunch", StringComparison.OrdinalIgnoreCase);
        }

        private static void LogLegacySurfaceOrbitSegmentRejected(
            OrbitSegment segment,
            string recordingId,
            string context,
            string reason,
            double targetUT)
        {
            string safeRecordingId = string.IsNullOrEmpty(recordingId) ? "<none>" : recordingId;
            string safeContext = string.IsNullOrEmpty(context) ? "unknown" : context;
            string key = "legacy-surface-orbit-reject-" + safeRecordingId + "-" + safeContext;
            ParsekLog.VerboseRateLimited(
                "Playback",
                key,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Legacy surface orbit segment rejected: key={0} context={1} reason={2} rec={3} body={4} |sma|={5:F0} targetUT={6:F1} segmentUT={7:F1}-{8:F1}",
                    key,
                    safeContext,
                    reason ?? "(none)",
                    safeRecordingId,
                    segment.bodyName ?? "(null)",
                    Math.Abs(segment.semiMajorAxis),
                    targetUT,
                    segment.startUT,
                    segment.endUT),
                5.0);
        }

        internal static void LogOrbitSegmentRejected(
            OrbitSegment segment,
            string recordingId,
            string context,
            OrbitRejectionReason reason,
            OrbitSegmentValidationMode mode)
        {
            if (mode != OrbitSegmentValidationMode.ValidateAndLog)
                return;

            string safeRecordingId = string.IsNullOrEmpty(recordingId) ? "<none>" : recordingId;
            string safeContext = string.IsNullOrEmpty(context) ? "unknown" : context;
            string key = "orbit-resolver-reject-" + safeRecordingId + "-" + safeContext;
            ParsekLog.VerboseRateLimited(
                "Playback",
                key,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Orbit segment rejected by resolver: key={0} rec={1} context={2} |sma|={3:F0} epoch={4:F1} body={5} reason={6}",
                    key,
                    safeRecordingId,
                    safeContext,
                    Math.Abs(segment.semiMajorAxis),
                    segment.epoch,
                    segment.bodyName ?? "(null)",
                    ToLogToken(reason)),
                5.0);
        }

        internal static string ToLogToken(OrbitRejectionReason reason)
        {
            switch (reason)
            {
                case OrbitRejectionReason.NonFiniteElements:
                    return "non-finite-elements";
                case OrbitRejectionReason.InvalidEccentricity:
                    return "invalid-eccentricity";
                case OrbitRejectionReason.BelowMinSma:
                    return "below-min-sma";
                case OrbitRejectionReason.MissingBody:
                    return "missing-body";
                case OrbitRejectionReason.OrbitConstructionFailed:
                    return "orbit-construction-failed";
                default:
                    return "none";
            }
        }
    }
}
