using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Parsek
{
    internal enum TailSeedUse
    {
        Spawn,
        MapPresence
    }

    internal struct TailDerivedOrbitSeed
    {
        internal bool Accepted;
        internal string DeclineReason;
        internal string BodyName;
        internal double TailUT;
        internal double RotationDriftSeconds;
        internal double LatestStoredSegmentEndUT;
        internal string TailFrameSource;
        internal bool UsedHistoricalBodyRotation;
        internal double HistoricalLongitude;
        internal OrbitSegment Segment;
    }

    internal static class OrbitSeedResolver
    {
        internal const double TailDerivedOrbitFreshnessEpsilon = 1e-3;
        internal const double TailDerivedOrbitMaxRotationDriftSeconds = 30.0;

        internal delegate bool TailSeedResolverOverride(
            IPlaybackTrajectory traj,
            CelestialBody body,
            double currentUT,
            TailSeedUse use,
            out TailDerivedOrbitSeed seed);

        internal static TailSeedResolverOverride TailSeedResolverForTesting;

        internal static Func<CelestialBody, double> RotationPeriodForTesting;
        internal static Func<CelestialBody, double> InitialRotationForTesting;

        private static readonly FieldInfo InitialRotationField =
            typeof(CelestialBody).GetField(
                "initialRotation",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        internal static void ResetForTesting()
        {
            TailSeedResolverForTesting = null;
            RotationPeriodForTesting = null;
            InitialRotationForTesting = null;
        }

        internal static bool TryDeriveTailOrbitSeed(
            IPlaybackTrajectory traj,
            CelestialBody body,
            double currentUT,
            TailSeedUse use,
            out TailDerivedOrbitSeed seed)
        {
            if (TailSeedResolverForTesting != null)
                return TailSeedResolverForTesting(traj, body, currentUT, use, out seed);

            seed = new TailDerivedOrbitSeed
            {
                Accepted = false,
                DeclineReason = null,
                BodyName = null,
                TailUT = double.NaN,
                RotationDriftSeconds = double.NaN,
                LatestStoredSegmentEndUT = double.NaN,
                TailFrameSource = null,
                UsedHistoricalBodyRotation = false,
                HistoricalLongitude = double.NaN,
                Segment = default(OrbitSegment)
            };

            string bodyName = ResolveBodyName(body);
            if (traj == null || string.IsNullOrEmpty(bodyName))
            {
                seed.DeclineReason = "null-input";
                return false;
            }

            if (!TryFindLatestCoastTrajectoryFrame(
                    traj,
                    bodyName,
                    out TrajectoryPoint candidate,
                    out string frameSource))
            {
                seed.DeclineReason = "no-absolute-coast-tail";
                return false;
            }

            seed.TailUT = candidate.ut;
            seed.BodyName = bodyName;
            seed.TailFrameSource = frameSource;
            seed.RotationDriftSeconds = IsFinite(currentUT) && IsFinite(candidate.ut)
                ? currentUT - candidate.ut
                : double.NaN;

            double latestStoredSegmentEndUT =
                ResolveLatestStoredOrbitSegmentEndUT(traj, bodyName);
            seed.LatestStoredSegmentEndUT = latestStoredSegmentEndUT;
            if (IsFinite(latestStoredSegmentEndUT)
                && candidate.ut <= latestStoredSegmentEndUT + TailDerivedOrbitFreshnessEpsilon)
            {
                seed.DeclineReason = "segment-newer-than-tail";
                return false;
            }

            if (use == TailSeedUse.Spawn
                && IsFinite(currentUT)
                && Math.Abs(currentUT - candidate.ut) > TailDerivedOrbitMaxRotationDriftSeconds)
            {
                seed.DeclineReason = "rotation-drift-out-of-bounds";
                return false;
            }

            Vector3d recordedVelocity = new Vector3d(
                candidate.velocity.x,
                candidate.velocity.y,
                candidate.velocity.z);
            if (!IsFinite(recordedVelocity))
            {
                seed.DeclineReason = "non-finite-state-vector";
                return false;
            }

            if (use == TailSeedUse.Spawn && !IsFinite(body.position))
            {
                seed.DeclineReason = "non-finite-body-position";
                return false;
            }

            try
            {
                Orbit reseeded = new Orbit();
                if (use == TailSeedUse.MapPresence)
                {
                    if (!TryResolveRotationPeriod(body, out double rotationPeriod))
                    {
                        seed.DeclineReason = "historical-rotation-unavailable";
                        return false;
                    }

                    double initialRotation = ResolveInitialRotation(body);
                    if (!OrbitReseed.TryFromHistoricalLatLonAltAndRecordedVelocity(
                            reseeded,
                            body,
                            candidate.latitude,
                            candidate.longitude,
                            candidate.altitude,
                            recordedVelocity,
                            candidate.ut,
                            rotationPeriod,
                            initialRotation,
                            out double inertialLongitude,
                            out string historicalFailureReason))
                    {
                        seed.DeclineReason = historicalFailureReason ?? "historical-rotation-unavailable";
                        return false;
                    }

                    seed.UsedHistoricalBodyRotation = true;
                    seed.HistoricalLongitude = inertialLongitude;
                }
                else
                {
                    OrbitReseed.FromLatLonAltAndRecordedVelocity(
                        reseeded,
                        body,
                        candidate.latitude,
                        candidate.longitude,
                        candidate.altitude,
                        recordedVelocity,
                        candidate.ut);
                }

                if (!IsFiniteOrbitSeedElements(reseeded))
                {
                    seed.DeclineReason = "non-finite-elements";
                    return false;
                }

                seed.Segment = new OrbitSegment
                {
                    startUT = PlaybackTrajectoryBoundsResolver.ResolveGhostActivationStartUT(traj),
                    endUT = traj.EndUT,
                    inclination = reseeded.inclination,
                    eccentricity = reseeded.eccentricity,
                    semiMajorAxis = reseeded.semiMajorAxis,
                    longitudeOfAscendingNode = reseeded.LAN,
                    argumentOfPeriapsis = reseeded.argumentOfPeriapsis,
                    meanAnomalyAtEpoch = reseeded.meanAnomalyAtEpoch,
                    epoch = reseeded.epoch,
                    bodyName = bodyName
                };
                seed.Accepted = true;
                seed.DeclineReason = null;
                return true;
            }
            catch (Exception)
            {
                seed.DeclineReason = "exception";
                return false;
            }
        }

        internal static bool TryFindLatestCoastTrajectoryFrame(
            IPlaybackTrajectory traj,
            string bodyName,
            out TrajectoryPoint frame)
        {
            return TryFindLatestCoastTrajectoryFrame(
                traj,
                bodyName,
                out frame,
                out _);
        }

        internal static bool TryFindLatestCoastTrajectoryFrame(
            IPlaybackTrajectory traj,
            string bodyName,
            out TrajectoryPoint frame,
            out string frameSource)
        {
            frame = default(TrajectoryPoint);
            frameSource = null;
            if (traj?.TrackSections == null || string.IsNullOrEmpty(bodyName))
                return false;

            for (int s = traj.TrackSections.Count - 1; s >= 0; s--)
            {
                TrackSection section = traj.TrackSections[s];
                if (section.environment != SegmentEnvironment.ExoBallistic)
                    continue;

                List<TrajectoryPoint> framesList =
                    SelectAbsoluteFramesList(section, out string source);
                if (framesList == null || framesList.Count == 0)
                    continue;

                for (int i = framesList.Count - 1; i >= 0; i--)
                {
                    TrajectoryPoint candidate = framesList[i];
                    if (string.IsNullOrEmpty(candidate.bodyName)
                        || !string.Equals(candidate.bodyName, bodyName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!IsFinite(candidate.ut)
                        || !IsFinite(candidate.latitude)
                        || !IsFinite(candidate.longitude)
                        || !IsFinite(candidate.altitude)
                        || !IsFinite(new Vector3d(
                            candidate.velocity.x,
                            candidate.velocity.y,
                            candidate.velocity.z)))
                    {
                        continue;
                    }

                    frame = candidate;
                    frameSource = source;
                    return true;
                }
            }

            return false;
        }

        internal static double ResolveLatestStoredOrbitSegmentEndUT(
            IPlaybackTrajectory traj,
            string bodyName)
        {
            if (traj?.OrbitSegments == null || string.IsNullOrEmpty(bodyName))
                return double.NaN;

            double latest = double.NaN;
            for (int i = 0; i < traj.OrbitSegments.Count; i++)
            {
                OrbitSegment seg = traj.OrbitSegments[i];
                if (!string.Equals(seg.bodyName, bodyName, StringComparison.Ordinal))
                    continue;
                if (!IsFinite(seg.semiMajorAxis) || seg.semiMajorAxis <= 0.0)
                    continue;
                if (!IsFinite(seg.endUT))
                    continue;
                if (double.IsNaN(latest) || seg.endUT > latest)
                    latest = seg.endUT;
            }

            return latest;
        }

        internal static bool IsFiniteOrbitSeedElements(Orbit orbit)
        {
            if (orbit == null)
                return false;
            return IsFinite(orbit.inclination)
                && IsFinite(orbit.eccentricity)
                && orbit.eccentricity >= 0.0
                && IsFinite(orbit.semiMajorAxis)
                && Math.Abs(orbit.semiMajorAxis) > 0.0
                && IsFinite(orbit.LAN)
                && IsFinite(orbit.argumentOfPeriapsis)
                && IsFinite(orbit.meanAnomalyAtEpoch)
                && IsFinite(orbit.epoch);
        }

        internal static string ResolveBodyName(CelestialBody body)
        {
            if (object.ReferenceEquals(body, null))
                return null;
            if (!string.IsNullOrEmpty(body.bodyName))
                return body.bodyName;
            if (!string.IsNullOrEmpty(body.name))
                return body.name;
            return null;
        }

        private static List<TrajectoryPoint> SelectAbsoluteFramesList(
            TrackSection section,
            out string frameSource)
        {
            if (section.referenceFrame == ReferenceFrame.Absolute)
            {
                frameSource = "absolute";
                return section.frames;
            }

            if (section.referenceFrame == ReferenceFrame.Relative
                && section.bodyFixedFrames != null
                && section.bodyFixedFrames.Count > 0)
            {
                frameSource = "relative-absolute-shadow";
                return section.bodyFixedFrames;
            }

            frameSource = null;
            return null;
        }

        internal static bool TryResolveRotationPeriod(CelestialBody body, out double rotationPeriod)
        {
            rotationPeriod = double.NaN;
            if (object.ReferenceEquals(body, null))
                return false;

            rotationPeriod = RotationPeriodForTesting != null
                ? RotationPeriodForTesting(body)
                : body.rotationPeriod;
            return IsFinite(rotationPeriod) && Math.Abs(rotationPeriod) > double.Epsilon;
        }

        internal static double ResolveInitialRotation(CelestialBody body)
        {
            if (object.ReferenceEquals(body, null))
                return 0.0;

            double value = double.NaN;
            if (InitialRotationForTesting != null)
            {
                value = InitialRotationForTesting(body);
            }
            else if (InitialRotationField != null)
            {
                object raw = InitialRotationField.GetValue(body);
                if (raw is double doubleValue)
                    value = doubleValue;
                else if (raw is float floatValue)
                    value = floatValue;
            }

            return IsFinite(value) ? value : 0.0;
        }

        private static bool IsFinite(double value)
        {
            return !(double.IsNaN(value) || double.IsInfinity(value));
        }

        private static bool IsFinite(Vector3d value)
        {
            return !(double.IsNaN(value.x) || double.IsNaN(value.y) || double.IsNaN(value.z)
                || double.IsInfinity(value.x) || double.IsInfinity(value.y) || double.IsInfinity(value.z));
        }
    }
}
