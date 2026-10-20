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
        internal double TerminalOrbitEpoch;
        internal string TailFrameSource;
        internal bool UsedHistoricalBodyRotation;
        internal double HistoricalLongitude;
        // How the tail pair's velocity frame resolved (RecordedTailVelocityResolver), in
        // either use. Speeds and yaw are NaN when not computed.
        internal bool VelocityWasRotatingFrame;
        internal double VelocityFrameYawDegrees;
        internal double VelocityFrameResidual;
        internal double RecordedSpeed;
        internal double InertialSpeed;
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
                TerminalOrbitEpoch = double.NaN,
                TailFrameSource = null,
                UsedHistoricalBodyRotation = false,
                HistoricalLongitude = double.NaN,
                VelocityWasRotatingFrame = false,
                VelocityFrameYawDegrees = double.NaN,
                VelocityFrameResidual = double.NaN,
                RecordedSpeed = double.NaN,
                InertialSpeed = double.NaN,
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
                    out string frameSource,
                    out bool hasPreviousFrame,
                    out TrajectoryPoint previousFrame))
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

            // A terminal orbit captured from the live vessel at recording end carries
            // stock's own frame-correct elements, with an epoch at (or after) the last
            // physics frame. The tail state vector cannot beat it: the recorded velocity
            // is in the Unity world frame of the recording session, whose polar rotation
            // (Planetarium.InverseRotAngle) is not recorded and differs in any later
            // session, so a tail seed built against today's body rotation carries a
            // wrong flight-path angle.
            double terminalOrbitEpoch = ResolveTerminalOrbitEpoch(traj, bodyName);
            seed.TerminalOrbitEpoch = terminalOrbitEpoch;
            if (IsFinite(terminalOrbitEpoch)
                && candidate.ut <= terminalOrbitEpoch + TailDerivedOrbitFreshnessEpsilon)
            {
                seed.DeclineReason = "terminal-orbit-newer-than-tail";
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

            // The recorded vector alone cannot seed the orbit: it may be surface-relative,
            // and its world axes sit at a yaw about the spin axis that no later session
            // shares (LAB-7: sma 596266 in the recording session, ecc 0.9996 after a reload,
            // and a map ghost drawn retrograde at inc 179.8). Both uses resolve frame and
            // yaw from the pair; a tail that cannot be resolved yields to the stored segment
            // / terminal orbit seeds.
            if (!hasPreviousFrame)
            {
                seed.DeclineReason = "tail-velocity-single-frame";
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
                    bool historicalResolved = OrbitReseed.TryFromHistoricalLatLonAltAndRecordedVelocity(
                        reseeded,
                        body,
                        previousFrame,
                        candidate,
                        rotationPeriod,
                        initialRotation,
                        out OrbitReseed.HistoricalTailPairSeedInputs historical,
                        traj?.RecordingId);
                    CopyResolution(ref seed, historical.Resolution, historicalResolved);
                    if (!historicalResolved)
                    {
                        seed.DeclineReason = historical.FailureReason ?? RecordedTailVelocityResolver.DeclineNoFit;
                        return false;
                    }

                    seed.UsedHistoricalBodyRotation = true;
                    seed.HistoricalLongitude = historical.InertialLongitude;
                }
                else
                {
                    bool pairResolved = OrbitReseed.TryFromRecordedTailPair(
                        reseeded,
                        body,
                        previousFrame,
                        candidate,
                        out RecordedTailVelocityResolution resolution);
                    CopyResolution(ref seed, resolution, pairResolved);
                    if (!pairResolved)
                    {
                        seed.DeclineReason = resolution.DeclineReason ?? RecordedTailVelocityResolver.DeclineNoFit;
                        return false;
                    }
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

        private static void CopyResolution(
            ref TailDerivedOrbitSeed seed,
            RecordedTailVelocityResolution resolution,
            bool resolved)
        {
            seed.VelocityWasRotatingFrame = resolution.SampleWasRotatingFrame;
            seed.VelocityFrameYawDegrees = resolution.YawDegrees;
            seed.VelocityFrameResidual = resolution.Residual;
            seed.RecordedSpeed = resolution.RecordedSpeed;
            seed.InertialSpeed = resolved
                ? resolution.InertialVelocityYup.magnitude
                : double.NaN;
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
            return TryFindLatestCoastTrajectoryFrame(
                traj,
                bodyName,
                out frame,
                out frameSource,
                out _,
                out _);
        }

        /// <summary>
        /// The latest valid coast frame on <paramref name="bodyName"/>, plus the valid
        /// frame before it in the SAME frames list (same section and frame source, so no
        /// section boundary or scene seam sits between them), when one exists. A pair that
        /// straddles a rotating-frame flip inside the section fits neither velocity frame
        /// and is declined by <see cref="RecordedTailVelocityResolver.TryResolve"/>.
        /// </summary>
        internal static bool TryFindLatestCoastTrajectoryFrame(
            IPlaybackTrajectory traj,
            string bodyName,
            out TrajectoryPoint frame,
            out string frameSource,
            out bool hasPrevious,
            out TrajectoryPoint previous)
        {
            frame = default(TrajectoryPoint);
            frameSource = null;
            hasPrevious = false;
            previous = default(TrajectoryPoint);
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
                    if (!IsUsableCoastFrame(candidate, bodyName))
                        continue;

                    frame = candidate;
                    frameSource = source;
                    for (int j = i - 1; j >= 0; j--)
                    {
                        TrajectoryPoint earlier = framesList[j];
                        if (IsUsableCoastFrame(earlier, bodyName) && earlier.ut < candidate.ut)
                        {
                            previous = earlier;
                            hasPrevious = true;
                            break;
                        }
                    }

                    return true;
                }
            }

            return false;
        }

        private static bool IsUsableCoastFrame(TrajectoryPoint candidate, string bodyName)
        {
            if (string.IsNullOrEmpty(candidate.bodyName)
                || !string.Equals(candidate.bodyName, bodyName, StringComparison.Ordinal))
            {
                return false;
            }

            return IsFinite(candidate.ut)
                && IsFinite(candidate.latitude)
                && IsFinite(candidate.longitude)
                && IsFinite(candidate.altitude)
                && IsFinite(new Vector3d(
                    candidate.velocity.x,
                    candidate.velocity.y,
                    candidate.velocity.z));
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

        internal static double ResolveTerminalOrbitEpoch(
            IPlaybackTrajectory traj,
            string bodyName)
        {
            if (traj == null
                || string.IsNullOrEmpty(bodyName)
                || !string.Equals(traj.TerminalOrbitBody, bodyName, StringComparison.Ordinal)
                || !IsFinite(traj.TerminalOrbitSemiMajorAxis)
                || traj.TerminalOrbitSemiMajorAxis <= 0.0
                || !IsFinite(traj.TerminalOrbitEpoch))
            {
                return double.NaN;
            }

            return traj.TerminalOrbitEpoch;
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
