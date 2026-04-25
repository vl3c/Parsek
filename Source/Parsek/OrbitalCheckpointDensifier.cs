using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    internal delegate bool TryResolveOrbitalCheckpointBody(
        string bodyName,
        out double bodyRadius,
        out double gravParameter);

    internal delegate bool TryBuildOrbitalCheckpointPoint(
        OrbitSegment segment,
        double ut,
        double bodyRadius,
        double gravParameter,
        out TrajectoryPoint point);

    internal struct OrbitalCheckpointDensifyResult
    {
        public int AddedPoints;
        public int ExistingPoints;
        public int FinalPoints;
        public int SegmentCount;
        public double AnomalySpanDegrees;
        public string Reason;
        public bool Capped;
    }

    internal static class OrbitalCheckpointDensifier
    {
        internal const double MinDensifyDurationSeconds = 600.0;
        internal const double TrueAnomalyStepDegrees = 5.0;
        internal const int MaxAddedPointsPerSection = 360;
        internal const string PolicyDescription = "trueAnomalyStep=5deg minDuration=600s maxAdded=360 endpoints=true";

        private const double TwoPi = 2.0 * Math.PI;
        private const double DuplicateUtTolerance = 0.001;
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        internal static int DensifyRecording(Recording rec)
        {
            return DensifyRecording(
                rec,
                TryResolveKspBodyConstants,
                TryBuildKspOrbitalCheckpointPoint,
                syncFlatTrajectory: true,
                logDecisions: true);
        }

        internal static int DensifyRecording(
            Recording rec,
            TryResolveOrbitalCheckpointBody resolveBody,
            TryBuildOrbitalCheckpointPoint buildPoint,
            bool syncFlatTrajectory,
            bool logDecisions)
        {
            if (rec == null || rec.TrackSections == null || rec.TrackSections.Count == 0)
                return 0;

            int totalAdded = 0;
            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                TrackSection section = rec.TrackSections[i];
                if (section.referenceFrame != ReferenceFrame.OrbitalCheckpoint)
                    continue;

                OrbitalCheckpointDensifyResult result = DensifySection(
                    ref section,
                    resolveBody,
                    buildPoint);
                rec.TrackSections[i] = section;
                totalAdded += result.AddedPoints;

                if (logDecisions)
                    LogDecision(rec, i, section, result);
            }

            if (totalAdded > 0)
            {
                if (syncFlatTrajectory)
                    RecordingStore.TrySyncFlatTrajectoryFromTrackSections(rec, allowRelativeSections: true);
                rec.CachedStats = null;
                rec.CachedStatsPointCount = 0;
            }

            return totalAdded;
        }

        internal static OrbitalCheckpointDensifyResult DensifySection(
            ref TrackSection section,
            TryResolveOrbitalCheckpointBody resolveBody,
            TryBuildOrbitalCheckpointPoint buildPoint)
        {
            if (section.frames == null)
                section.frames = new List<TrajectoryPoint>();

            var result = new OrbitalCheckpointDensifyResult
            {
                ExistingPoints = section.frames.Count,
                FinalPoints = section.frames.Count,
                SegmentCount = section.checkpoints != null ? section.checkpoints.Count : 0,
                Reason = "not-eligible"
            };

            if (section.referenceFrame != ReferenceFrame.OrbitalCheckpoint)
            {
                result.Reason = "not-orbital-checkpoint";
                return result;
            }

            if (section.checkpoints == null || section.checkpoints.Count == 0)
            {
                result.Reason = "no-orbit-segments";
                return result;
            }

            if (resolveBody == null || buildPoint == null)
            {
                result.Reason = "missing-callback";
                return result;
            }

            for (int s = 0; s < section.checkpoints.Count; s++)
            {
                OrbitSegment segment = section.checkpoints[s];
                double startUT = Math.Max(section.startUT, segment.startUT);
                double endUT = Math.Min(section.endUT, segment.endUT);
                double duration = endUT - startUT;
                if (duration < MinDensifyDurationSeconds)
                {
                    if (result.Reason == "not-eligible")
                        result.Reason = "below-duration-threshold";
                    continue;
                }

                if (segment.eccentricity < 0.0 || segment.eccentricity >= 1.0 || segment.semiMajorAxis <= 0.0)
                {
                    result.Reason = "unsupported-orbit";
                    continue;
                }

                if (!resolveBody(segment.bodyName, out double bodyRadius, out double gravParameter)
                    || bodyRadius <= 0.0
                    || gravParameter <= 0.0)
                {
                    result.Reason = "missing-body";
                    continue;
                }

                List<double> sampleUTs = BuildTrueAnomalySampleUTs(
                    segment,
                    startUT,
                    endUT,
                    gravParameter,
                    out double anomalySpanDegrees,
                    out bool capped);
                result.AnomalySpanDegrees += anomalySpanDegrees;
                result.Capped = result.Capped || capped;

                for (int p = 0; p < sampleUTs.Count; p++)
                {
                    if (result.AddedPoints >= MaxAddedPointsPerSection)
                    {
                        result.Capped = true;
                        break;
                    }

                    double sampleUT = sampleUTs[p];
                    if (HasPointAtUT(section.frames, sampleUT))
                        continue;

                    if (!buildPoint(segment, sampleUT, bodyRadius, gravParameter, out TrajectoryPoint point))
                    {
                        result.Reason = "sample-build-failed";
                        continue;
                    }

                    point.ut = sampleUT;
                    if (string.IsNullOrEmpty(point.bodyName))
                        point.bodyName = segment.bodyName;
                    // OrbitalCheckpoint section frames are the persistence marker for derived samples.
                    section.frames.Add(point);
                    UpdateAltitudeRange(ref section, point.altitude);
                    result.AddedPoints++;
                }
            }

            if (result.AddedPoints > 0)
            {
                section.frames.Sort((a, b) => a.ut.CompareTo(b.ut));
                double duration = section.endUT - section.startUT;
                if (duration > 0.0)
                    section.sampleRateHz = (float)(section.frames.Count / duration);
                result.Reason = "densified";
            }

            result.FinalPoints = section.frames.Count;
            return result;
        }

        private static List<double> BuildTrueAnomalySampleUTs(
            OrbitSegment segment,
            double startUT,
            double endUT,
            double gravParameter,
            out double anomalySpanDegrees,
            out bool capped)
        {
            capped = false;
            anomalySpanDegrees = 0.0;
            var samples = new List<double>();
            if (endUT <= startUT || segment.semiMajorAxis <= 0.0 || gravParameter <= 0.0)
                return samples;

            double meanMotion = Math.Sqrt(gravParameter / (segment.semiMajorAxis * segment.semiMajorAxis * segment.semiMajorAxis));
            if (double.IsNaN(meanMotion) || double.IsInfinity(meanMotion) || meanMotion <= 0.0)
                return samples;

            double epochMeanAnomaly = NormalizeRadians(segment.meanAnomalyAtEpoch);
            double startMeanRaw = epochMeanAnomaly + meanMotion * (startUT - segment.epoch);
            double endMeanRaw = epochMeanAnomaly + meanMotion * (endUT - segment.epoch);
            double startNu = TrueAnomalyUnwrapped(startMeanRaw, segment.eccentricity);
            double endNu = TrueAnomalyUnwrapped(endMeanRaw, segment.eccentricity);
            if (endNu <= startNu)
                return samples;

            double step = TrueAnomalyStepDegrees * Math.PI / 180.0;
            anomalySpanDegrees = (endNu - startNu) * 180.0 / Math.PI;
            int firstStep = (int)Math.Floor(startNu / step) + 1;
            int lastStep = (int)Math.Floor(endNu / step);
            int candidateCount = Math.Max(0, lastStep - firstStep + 1);
            int stride = 1;
            int interiorLimit = Math.Max(0, MaxAddedPointsPerSection - 2);
            if (candidateCount > interiorLimit && interiorLimit > 0)
            {
                stride = (int)Math.Ceiling(candidateCount / (double)interiorLimit);
                capped = true;
            }

            samples.Add(startUT);
            for (int k = firstStep; k <= lastStep; k += stride)
            {
                double targetNu = k * step;
                double sampleUT = UTAtTrueAnomaly(segment, targetNu, meanMotion, epochMeanAnomaly);
                if (sampleUT <= startUT + DuplicateUtTolerance || sampleUT >= endUT - DuplicateUtTolerance)
                    continue;
                samples.Add(sampleUT);
            }
            samples.Add(endUT);

            return samples;
        }

        private static double TrueAnomalyUnwrapped(double meanAnomalyRaw, double eccentricity)
        {
            double orbitIndex = Math.Floor(meanAnomalyRaw / TwoPi);
            double meanAnomaly = NormalizeRadians(meanAnomalyRaw);
            double eccentricAnomaly = SolveKepler(meanAnomaly, eccentricity);
            double trueAnomaly = TrueAnomalyFromEccentricAnomaly(eccentricAnomaly, eccentricity);
            return orbitIndex * TwoPi + trueAnomaly;
        }

        private static double UTAtTrueAnomaly(
            OrbitSegment segment,
            double trueAnomalyUnwrapped,
            double meanMotion,
            double epochMeanAnomaly)
        {
            double orbitIndex = Math.Floor(trueAnomalyUnwrapped / TwoPi);
            double trueAnomaly = NormalizeRadians(trueAnomalyUnwrapped);
            double eccentricAnomaly = EccentricAnomalyFromTrueAnomaly(trueAnomaly, segment.eccentricity);
            double meanAnomaly = NormalizeRadians(eccentricAnomaly - segment.eccentricity * Math.Sin(eccentricAnomaly));
            double rawMeanAnomaly = orbitIndex * TwoPi + meanAnomaly;
            return segment.epoch + (rawMeanAnomaly - epochMeanAnomaly) / meanMotion;
        }

        private static double EccentricAnomalyFromTrueAnomaly(double trueAnomaly, double eccentricity)
        {
            double sinHalf = Math.Sin(trueAnomaly * 0.5);
            double cosHalf = Math.Cos(trueAnomaly * 0.5);
            double eccentricAnomaly = 2.0 * Math.Atan2(
                Math.Sqrt(1.0 - eccentricity) * sinHalf,
                Math.Sqrt(1.0 + eccentricity) * cosHalf);
            return NormalizeRadians(eccentricAnomaly);
        }

        private static double TrueAnomalyFromEccentricAnomaly(double eccentricAnomaly, double eccentricity)
        {
            double sinHalfNu = Math.Sqrt(1.0 + eccentricity) * Math.Sin(eccentricAnomaly * 0.5);
            double cosHalfNu = Math.Sqrt(1.0 - eccentricity) * Math.Cos(eccentricAnomaly * 0.5);
            return NormalizeRadians(2.0 * Math.Atan2(sinHalfNu, cosHalfNu));
        }

        private static double SolveKepler(double meanAnomaly, double eccentricity)
        {
            double eccentricAnomaly = meanAnomaly;
            for (int i = 0; i < 12; i++)
            {
                double denominator = 1.0 - eccentricity * Math.Cos(eccentricAnomaly);
                if (Math.Abs(denominator) < 1e-12)
                    break;
                double delta = (eccentricAnomaly - eccentricity * Math.Sin(eccentricAnomaly) - meanAnomaly) / denominator;
                eccentricAnomaly -= delta;
                if (Math.Abs(delta) < 1e-12)
                    break;
            }
            return eccentricAnomaly;
        }

        private static double NormalizeRadians(double value)
        {
            value %= TwoPi;
            if (value < 0.0)
                value += TwoPi;
            return value;
        }

        private static bool HasPointAtUT(List<TrajectoryPoint> frames, double ut)
        {
            for (int i = 0; i < frames.Count; i++)
            {
                if (Math.Abs(frames[i].ut - ut) <= DuplicateUtTolerance)
                    return true;
            }
            return false;
        }

        private static void UpdateAltitudeRange(ref TrackSection section, double altitude)
        {
            if (double.IsNaN(altitude) || double.IsInfinity(altitude))
                return;

            float alt = (float)altitude;
            section.minAltitude = float.IsNaN(section.minAltitude) ? alt : Math.Min(section.minAltitude, alt);
            section.maxAltitude = float.IsNaN(section.maxAltitude) ? alt : Math.Max(section.maxAltitude, alt);
        }

        private static void LogDecision(
            Recording rec,
            int sectionIndex,
            TrackSection section,
            OrbitalCheckpointDensifyResult result)
        {
            ParsekLog.Info("Recorder",
                string.Format(ic,
                    "OrbitalCheckpoint densified: rec={0} section[{1}] UT={2:F1}-{3:F1} addedPoints={4} density={5} segments={6} existingPoints={7} finalPoints={8} anomalySpanDeg={9:F1} reason={10} capped={11}",
                    rec?.RecordingId ?? "(null)",
                    sectionIndex,
                    section.startUT,
                    section.endUT,
                    result.AddedPoints,
                    PolicyDescription,
                    result.SegmentCount,
                    result.ExistingPoints,
                    result.FinalPoints,
                    result.AnomalySpanDegrees,
                    result.Reason ?? "(none)",
                    result.Capped));
        }

        private static bool TryResolveKspBodyConstants(
            string bodyName,
            out double bodyRadius,
            out double gravParameter)
        {
            bodyRadius = 0.0;
            gravParameter = 0.0;
            CelestialBody body = FindBody(bodyName);
            if (body == null)
                return false;

            bodyRadius = body.Radius;
            gravParameter = body.gravParameter;
            return bodyRadius > 0.0 && gravParameter > 0.0;
        }

        private static bool TryBuildKspOrbitalCheckpointPoint(
            OrbitSegment segment,
            double ut,
            double bodyRadius,
            double gravParameter,
            out TrajectoryPoint point)
        {
            point = default(TrajectoryPoint);
            CelestialBody body = FindBody(segment.bodyName);
            if (body == null)
                return false;

            try
            {
                var orbit = new Orbit(
                    segment.inclination,
                    segment.eccentricity,
                    segment.semiMajorAxis,
                    segment.longitudeOfAscendingNode,
                    segment.argumentOfPeriapsis,
                    segment.meanAnomalyAtEpoch,
                    segment.epoch,
                    body);

                Vector3d worldPos = orbit.getPositionAtUT(ut);
                if (!IsFinite(worldPos))
                    return false;

                double latitude = body.GetLatitude(worldPos);
                double longitude = body.GetLongitude(worldPos);
                double altitude = body.GetAltitude(worldPos);
                if (altitude < 0.0)
                    altitude = 0.0;

                Vector3 velocity = orbit.getOrbitalVelocityAtUT(ut);
                point = new TrajectoryPoint
                {
                    ut = ut,
                    latitude = latitude,
                    longitude = longitude,
                    altitude = altitude,
                    rotation = Quaternion.identity,
                    velocity = velocity,
                    bodyName = body.name,
                    funds = Funding.Instance != null ? Funding.Instance.Funds : 0.0,
                    science = ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0f,
                    reputation = Reputation.Instance != null ? Reputation.CurrentRep : 0f
                };
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Recorder",
                    string.Format(ic,
                        "OrbitalCheckpoint densify sample failed: body={0} UT={1:F1} err={2}",
                        segment.bodyName ?? "(null)",
                        ut,
                        ex.Message));
                return false;
            }
        }

        private static CelestialBody FindBody(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName))
                return null;
            CelestialBody body = FlightGlobals.GetBodyByName(bodyName);
            if (body != null)
                return body;
            return FlightGlobals.Bodies != null
                ? FlightGlobals.Bodies.Find(b => b != null && b.name == bodyName)
                : null;
        }

        private static bool IsFinite(Vector3d value)
        {
            return !(double.IsNaN(value.x) || double.IsNaN(value.y) || double.IsNaN(value.z)
                || double.IsInfinity(value.x) || double.IsInfinity(value.y) || double.IsInfinity(value.z));
        }
    }
}
