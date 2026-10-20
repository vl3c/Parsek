using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Wrappers around <see cref="Orbit.UpdateFromStateVectors"/> that apply the
    /// frame transforms KSP's API requires but the API surface does not enforce.
    ///
    /// Per decompiled <c>Orbit.UpdateFromStateVectors</c> (Assembly-CSharp), the
    /// <c>pos</c> argument must be RELATIVE to the reference body and YZ-flipped
    /// (i.e. expressed in <c>Planetarium.Zup</c> local coordinates), and the
    /// <c>vel</c> argument must be in the same Zup-local frame. KSP's own
    /// <c>OrbitFromStateVectors</c> wrapper does
    /// <c>UpdateFromStateVectors((pos - body.position).xzy, vel.xzy, body, UT)</c>;
    /// callers that pass world-absolute Y-up vectors directly produce
    /// structurally-valid but physically-wrong orbit elements (sma, periapsis and
    /// apoapsis come out of bands the safety gate rejects).
    ///
    /// Several helpers because the natural input shapes differ in their velocity
    /// frame and the right transform is per-shape, not per-call-site:
    /// <list type="bullet">
    ///   <item><see cref="FromLatLonAltAndRecordedVelocity"/> — body-fixed
    ///     lat/lon/alt plus a recorder-frame velocity. The recorder
    ///     (<see cref="FlightRecorder.SampleCurrentVelocity"/>) emits Y-up Unity
    ///     world axes from both its packed (<c>v.obt_velocity</c>) and unpacked
    ///     (<c>rb_velocityD + Krakensbane.GetFrameVelocity()</c>) branches, so
    ///     the velocity needs <c>.xzy</c>.</item>
    ///   <item><see cref="TryFromRecordedTailPair"/> - two consecutive recorded
    ///     samples whose velocity frame is RESOLVED from the samples themselves
    ///     (see "Recorded velocity frame" below) instead of trusted.</item>
    ///   <item><see cref="TryFromHistoricalLatLonAltAndRecordedVelocity"/> - the same pair,
    ///     placed at the tail's historical inertial position (body rotation phase at the
    ///     tail UT) for the map-presence seeds.</item>
    ///   <item><see cref="ResolveRecordedSampleSeedVelocity"/> - one recorded sample's velocity
    ///     resolved into this session's axes from it and its neighbour, for seeds whose
    ///     position is rebuilt in this session (state-vector map ghosts, spawner state-vector
    ///     seeds); <see cref="ResolveSameSessionRecordedVelocity"/> for seeds that run in the
    ///     recording's own session (the scene-exit finalizer).</item>
    ///   <item><see cref="FromWorldPosAndZupVelocity"/> — world-absolute Y-up
    ///     position plus a velocity already in Zup-local (e.g. the output of
    ///     <c>Orbit.getOrbitalVelocityAtUT</c>). The velocity is passed through;
    ///     applying <c>.xzy</c> to it would double-flip and corrupt the orbit's
    ///     orientation.</item>
    /// </list>
    ///
    /// Sites that take a caller-supplied velocity whose frame depends on the
    /// caller (e.g. <c>VesselSpawner.SpawnFromSnapshot</c> internals) should be
    /// audited per-call-site before being routed through one of these helpers.
    ///
    /// Recorded velocity frame. <c>TrajectoryPoint.velocity</c> is a Unity WORLD vector
    /// as captured, and neither half of that is stable:
    /// (1) the unpacked branch (<c>rb_velocityD + Krakensbane.GetFrameVelocity()</c>) is
    /// SURFACE-RELATIVE while the world frame co-rotates with the body (active vessel
    /// below <c>body.inverseRotThresholdAltitude</c>, decompiled
    /// <c>OrbitPhysicsManager.CheckReferenceFrame</c>; stock adds the frame term back in
    /// <c>OrbitDriver.TrackRigidbody</c>), and inertial otherwise (and when packed);
    /// (2) the world axes sit at a yaw about the spin axis (world Y) against the body
    /// that depends on session history: <c>CelestialBody.CBUpdate</c> freezes
    /// <c>directRotAngle</c> in the rotating frame and <c>Planetarium.InverseRotAngle</c>
    /// in the inertial one, and neither is recorded. Positions are stored body-fixed
    /// (lat/lon/alt), so pairing a stored velocity with a position rebuilt in another
    /// session rotates the velocity by an unknown angle (LAB-7 run 2026-10-18_0026: ecc
    /// 0.9996). <see cref="RecordedTailVelocityResolver"/> recovers both unknowns from two
    /// consecutive samples: the chord of their body-fixed positions is the surface-relative
    /// velocity in the rebuild frame, and the one yaw that maps the recorded vectors onto
    /// it (or onto it plus the frame velocity) names both the frame and the angle.
    /// </summary>
    internal static class OrbitReseed
    {
        internal struct StateVectorInputs
        {
            internal readonly Vector3d PositionForUpdate;
            internal readonly Vector3d VelocityForUpdate;

            internal StateVectorInputs(Vector3d positionForUpdate, Vector3d velocityForUpdate)
            {
                PositionForUpdate = positionForUpdate;
                VelocityForUpdate = velocityForUpdate;
            }
        }

        internal static StateVectorInputs ComputeRecordedVelocityInputs(
            Vector3d worldPosYup,
            Vector3d bodyPositionYup,
            Vector3d recordedVelWorldYup)
        {
            Vector3d bodyRelPos = worldPosYup - bodyPositionYup;
            return new StateVectorInputs(bodyRelPos.xzy, recordedVelWorldYup.xzy);
        }

        /// <summary>
        /// Pure: was a recorded sample at <paramref name="sampleAltitude"/> taken in the
        /// body's co-rotating frame (surface-relative velocity)? Mirrors stock's
        /// <c>OrbitPhysicsManager.CheckReferenceFrame</c> rule: the body rotates and the
        /// altitude is strictly below its <c>inverseRotThresholdAltitude</c>.
        /// </summary>
        internal static bool IsRecordedVelocityInRotatingFrame(
            bool bodyRotates,
            double inverseRotThresholdAltitude,
            double sampleAltitude)
        {
            return bodyRotates
                && IsFinite(inverseRotThresholdAltitude)
                && inverseRotThresholdAltitude > 0.0
                && IsFinite(sampleAltitude)
                && sampleAltitude < inverseRotThresholdAltitude;
        }

        /// <summary>
        /// Pure: the velocity of the body's co-rotating frame at a body-relative Y-up
        /// world position, i.e. stock <c>CelestialBody.getRFrmVel</c>:
        /// <c>Cross(angularVelocity, worldPos - body.position)</c> with
        /// <c>angularVelocity = Vector3d.down * 2*pi / rotationPeriod</c>
        /// (decompiled <c>CelestialBody</c> rotation setup). Zero when the period or
        /// the position is unusable.
        /// </summary>
        internal static Vector3d ComputeRotatingFrameVelocityYup(
            Vector3d bodyRelPosYup,
            double rotationPeriod)
        {
            if (!IsFinite(rotationPeriod)
                || System.Math.Abs(rotationPeriod) <= double.Epsilon
                || !IsFinite(bodyRelPosYup))
            {
                return Vector3d.zero;
            }

            Vector3d angularVelocity = Vector3d.down * (System.Math.PI * 2.0 / rotationPeriod);
            return Vector3d.Cross(angularVelocity, bodyRelPosYup);
        }

        internal static StateVectorInputs ComputeZupVelocityInputs(
            Vector3d worldPosYup,
            Vector3d bodyPositionYup,
            Vector3d velAlreadyZup)
        {
            Vector3d bodyRelPos = worldPosYup - bodyPositionYup;
            return new StateVectorInputs(bodyRelPos.xzy, velAlreadyZup);
        }

        /// <summary>
        /// Pure: lift a body-fixed lat/lon/alt into the body's orientation at
        /// <paramref name="phaseUT"/>: the longitude gains the body's rotation phase
        /// <c>initialRotation + phaseUT * 360 / rotationPeriod</c> (stock
        /// <c>CelestialBody.rotationAngle</c>) and the injected lookup (live:
        /// <c>CelestialBody.GetRelSurfacePosition</c>) maps it to body-relative Y-up axes.
        /// Unlike <see cref="TrajectoryMath.FrameTransform.LiftToInertial"/> this includes
        /// <c>initialRotation</c>: the result seeds an absolute orbit with no round trip
        /// through a lowering step in which that term would cancel.
        /// </summary>
        internal static bool TryLiftToHistoricalSurfacePosition(
            double lat,
            double lon,
            double alt,
            double phaseUT,
            double rotationPeriod,
            double initialRotationDeg,
            System.Func<double, double, double, Vector3d> getBodyRelativeSurfacePositionYup,
            out Vector3d bodyRelHistoricalYup,
            out double inertialLongitude,
            out string failureReason)
        {
            bodyRelHistoricalYup = Vector3d.zero;
            inertialLongitude = double.NaN;
            failureReason = null;

            if (!IsFinite(rotationPeriod) || System.Math.Abs(rotationPeriod) <= double.Epsilon)
            {
                failureReason = "historical-rotation-unavailable";
                return false;
            }
            if (!IsFinite(phaseUT) || !IsFinite(lat) || !IsFinite(lon) || !IsFinite(alt))
            {
                failureReason = "non-finite-state-vector";
                return false;
            }
            if (getBodyRelativeSurfacePositionYup == null)
            {
                failureReason = "historical-surface-position-unavailable";
                return false;
            }

            double phaseDeg = initialRotationDeg + (phaseUT * 360.0) / rotationPeriod;
            inertialLongitude = WrapLongitudeDegrees(lon + phaseDeg);
            bodyRelHistoricalYup = getBodyRelativeSurfacePositionYup(lat, inertialLongitude, alt);
            if (!IsFinite(bodyRelHistoricalYup))
            {
                failureReason = "historical-surface-position-non-finite";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Outcome of <see cref="TryComputeHistoricalTailPairInputs"/>.
        /// </summary>
        internal struct HistoricalTailPairSeedInputs
        {
            // Position and velocity for Orbit.UpdateFromStateVectors (Zup-swizzled).
            internal StateVectorInputs Inputs;
            // The tail's longitude lifted by the body rotation phase at the tail UT.
            internal double InertialLongitude;
            internal double SeedEpochUT;
            // The stock reference-frame rule at the tail altitude: the resolver's tie prior.
            internal bool AltitudeRuleRotating;
            internal RecordedTailVelocityResolution Resolution;
            internal string FailureReason;
        }

        /// <summary>
        /// Pure core of the map-presence seeds that place a recorded coast sample at its
        /// HISTORICAL inertial position (the endpoint-tail seed and the orbit-raise gap glide).
        /// The recorded velocity is not trusted (see the class summary): both samples of the
        /// pair are lifted into ONE body orientation, the tail's rotation phase, so the chord
        /// between them is the surface-relative velocity in that frame, and
        /// <see cref="RecordedTailVelocityResolver.TryResolve"/> fits the recorded vectors'
        /// frame and world yaw against it. Its inertial velocity is then already expressed in
        /// the lifted frame the tail position is seeded in. This equals resolving in any other
        /// body orientation and rotating the result about the spin axis by the angle between
        /// that orientation's tail position and the lifted one, because the resolver commutes
        /// with a rotation about Y. The orbit epoch is <paramref name="epochUT"/> (the tail UT,
        /// or the tail UT plus a loop shift for the gap glide). Unity-free: the surface lookup
        /// is injected.
        /// </summary>
        internal static bool TryComputeHistoricalTailPairInputs(
            TrajectoryPoint previous,
            TrajectoryPoint tail,
            double epochUT,
            double rotationPeriod,
            double initialRotationDeg,
            bool bodyRotates,
            double inverseRotThresholdAltitude,
            System.Func<double, double, double, Vector3d> getBodyRelativeSurfacePositionYup,
            out HistoricalTailPairSeedInputs result)
        {
            Vector3d previousVel = new Vector3d(previous.velocity.x, previous.velocity.y, previous.velocity.z);
            Vector3d tailVel = new Vector3d(tail.velocity.x, tail.velocity.y, tail.velocity.z);
            result = new HistoricalTailPairSeedInputs
            {
                Inputs = default(StateVectorInputs),
                InertialLongitude = double.NaN,
                SeedEpochUT = double.NaN,
                AltitudeRuleRotating = IsRecordedVelocityInRotatingFrame(
                    bodyRotates, inverseRotThresholdAltitude, tail.altitude),
                Resolution = new RecordedTailVelocityResolution
                {
                    Resolved = false,
                    DeclineReason = null,
                    SampleWasRotatingFrame = false,
                    YawDegrees = double.NaN,
                    Residual = double.NaN,
                    OtherResidual = double.NaN,
                    RecordedSpeed = IsFinite(tailVel) ? tailVel.magnitude : double.NaN,
                    InertialVelocityYup = Vector3d.zero
                },
                FailureReason = null
            };

            if (!IsFinite(epochUT))
            {
                result.FailureReason = "non-finite-epoch";
                return false;
            }
            if (!IsFinite(previous.ut) || !IsFinite(tail.ut)
                || !IsFinite(previousVel) || !IsFinite(tailVel))
            {
                result.FailureReason = "non-finite-state-vector";
                return false;
            }

            // Both samples at the TAIL's phase: one body orientation, so their chord is a
            // body-fixed (surface-relative) displacement, which is what the resolver models.
            if (!TryLiftToHistoricalSurfacePosition(
                    tail.latitude, tail.longitude, tail.altitude, tail.ut,
                    rotationPeriod, initialRotationDeg, getBodyRelativeSurfacePositionYup,
                    out Vector3d tailPosYup, out double inertialLongitude, out string liftFailure)
                || !TryLiftToHistoricalSurfacePosition(
                    previous.latitude, previous.longitude, previous.altitude, tail.ut,
                    rotationPeriod, initialRotationDeg, getBodyRelativeSurfacePositionYup,
                    out Vector3d previousPosYup, out _, out liftFailure))
            {
                result.FailureReason = liftFailure;
                return false;
            }
            result.InertialLongitude = inertialLongitude;

            bool resolved = RecordedTailVelocityResolver.TryResolve(
                previousPosYup,
                previous.ut,
                previousVel,
                tailPosYup,
                tail.ut,
                tailVel,
                bodyRotates ? rotationPeriod : double.NaN,
                result.AltitudeRuleRotating,
                out RecordedTailVelocityResolution resolution);
            result.Resolution = resolution;
            if (!resolved)
            {
                result.FailureReason = resolution.DeclineReason ?? RecordedTailVelocityResolver.DeclineNoFit;
                return false;
            }

            result.Inputs = new StateVectorInputs(tailPosYup.xzy, resolution.InertialVelocityYup.xzy);
            result.SeedEpochUT = epochUT;
            return true;
        }

        /// <summary>
        /// Pure: the inertial velocity of ONE sample of a recorded pair, in the frame the
        /// positions are given in, with the vector's frame and world yaw resolved by
        /// <see cref="RecordedTailVelocityResolver.TryResolve"/> rather than trusted.
        /// <paramref name="sampleIsTail"/> picks the sample whose recorded vector is carried:
        /// the resolver's yaw belongs to the tail, so an inertial reading carries the earlier
        /// sample's vector by the spin over the whole pair gap further
        /// (<see cref="RecordedTailVelocityResolver.InertialYawDriftRadians"/>). A rotating
        /// reading adds the frame velocity at <paramref name="seedPosYup"/>, the body-relative
        /// position the caller seeds at (the sample's own position, or a spawn position
        /// derived from it). False, with the resolver's decline reason, when the pair cannot
        /// be resolved.
        /// </summary>
        internal static bool TryResolveRecordedPairVelocityAtSample(
            TrajectoryPoint previous,
            TrajectoryPoint tail,
            bool sampleIsTail,
            Vector3d previousPosYup,
            Vector3d tailPosYup,
            Vector3d seedPosYup,
            double rotationPeriod,
            bool bodyRotates,
            double inverseRotThresholdAltitude,
            out RecordedTailVelocityResolution resolution,
            out Vector3d inertialVelYup)
        {
            inertialVelYup = Vector3d.zero;
            Vector3d previousVel = new Vector3d(previous.velocity.x, previous.velocity.y, previous.velocity.z);
            Vector3d tailVel = new Vector3d(tail.velocity.x, tail.velocity.y, tail.velocity.z);
            double period = bodyRotates ? rotationPeriod : double.NaN;
            bool altitudeRuleRotating = IsRecordedVelocityInRotatingFrame(
                bodyRotates, inverseRotThresholdAltitude, tail.altitude);

            if (!RecordedTailVelocityResolver.TryResolve(
                    previousPosYup,
                    previous.ut,
                    previousVel,
                    tailPosYup,
                    tail.ut,
                    tailVel,
                    period,
                    altitudeRuleRotating,
                    out resolution))
            {
                return false;
            }

            if (!IsFinite(seedPosYup))
            {
                resolution.Resolved = false;
                resolution.DeclineReason = RecordedTailVelocityResolver.DeclineNonFinite;
                return false;
            }

            double yaw = resolution.YawDegrees * (System.Math.PI / 180.0);
            if (!sampleIsTail && !resolution.SampleWasRotatingFrame)
                yaw += RecordedTailVelocityResolver.InertialYawDriftRadians(period, tail.ut - previous.ut);

            Vector3d sampleVel = sampleIsTail ? tailVel : previousVel;
            Vector3d rotated = RecordedTailVelocityResolver.RotateAboutY(sampleVel, yaw);
            inertialVelYup = resolution.SampleWasRotatingFrame
                ? rotated + ComputeRotatingFrameVelocityYup(seedPosYup, period)
                : rotated;
            return true;
        }

        internal const string SeedVelocitySourcePair = "pair-resolved";
        internal const string SeedVelocitySourceRecorded = "recorded-unresolved";
        internal const string SeedVelocitySourceAltitudeRule = "altitude-rule";
        internal const string DeclinePairRelativeSection = "pair-relative-section";

        /// <summary>
        /// Outcome of <see cref="ResolveRecordedSampleSeedVelocity"/> and
        /// <see cref="ResolveSameSessionRecordedVelocity"/>.
        /// </summary>
        internal struct RecordedSampleSeedVelocity
        {
            // The Y-up velocity to seed with: the resolved inertial vector, or the recorded
            // one when the site declined (Source tells which).
            internal Vector3d VelocityYup;
            internal Vector3d RecordedVelocityYup;
            internal string Source;
            internal string DeclineReason;
            internal bool SampleWasRotatingFrame;
            internal bool AltitudeRuleRotating;
            internal double PairPreviousUT;
            internal RecordedTailVelocityResolution Resolution;
        }

        /// <summary>
        /// Pure: the two samples of <paramref name="points"/> a single recorded sample's
        /// velocity frame is resolved from: <see cref="GhostMapPresence.TrySelectGapGlidePair"/>
        /// (the sample and its same-section, same-body neighbour, preferring the one before
        /// it), refused when the sample's track section is Relative, whose lat/lon/alt fields
        /// are anchor-local metres rather than body-fixed coordinates. The sample must be an
        /// element of <paramref name="points"/> (an interpolated point is not found).
        /// </summary>
        internal static bool TrySelectBodyFixedVelocityPair(
            List<TrajectoryPoint> points,
            List<TrackSection> sections,
            TrajectoryPoint sample,
            out TrajectoryPoint previous,
            out TrajectoryPoint tail,
            out bool sampleIsTail,
            out string declineReason)
        {
            previous = default(TrajectoryPoint);
            tail = default(TrajectoryPoint);
            sampleIsTail = false;
            if (sections != null && sections.Count > 0)
            {
                int sectionIndex = TrajectoryMath.FindTrackSectionForUT(sections, sample.ut);
                if (sectionIndex >= 0 && sections[sectionIndex].referenceFrame == ReferenceFrame.Relative)
                {
                    declineReason = DeclinePairRelativeSection;
                    return false;
                }
            }

            if (!GhostMapPresence.TrySelectGapGlidePair(
                    points, sections, sample, out previous, out tail, out declineReason))
            {
                return false;
            }

            sampleIsTail = tail.ut == sample.ut;
            return true;
        }

        /// <summary>
        /// Pure core of the state-vector seeds that pair a recorded sample's velocity with a
        /// position rebuilt in THIS session (map state-vector ghosts, the spawner's endpoint
        /// state-vector repair and its no-override <c>SpawnAtPosition</c> callers). The
        /// recorded vector's frame and world yaw are resolved from the sample and its
        /// neighbour (<see cref="TrySelectBodyFixedVelocityPair"/>,
        /// <see cref="TryResolveRecordedPairVelocityAtSample"/>), both positions from the
        /// injected lookup (live: <c>GetWorldSurfacePosition - body.position</c>, this
        /// session's axes). The world yaw of a single sample is unknowable from that sample
        /// once the session has changed, so with no pair, a Relative section or no fit the
        /// recorded vector is returned unchanged (<see cref="SeedVelocitySourceRecorded"/>),
        /// which is what every such site seeded before.
        /// </summary>
        internal static RecordedSampleSeedVelocity ResolveRecordedSampleSeedVelocity(
            List<TrajectoryPoint> points,
            List<TrackSection> sections,
            TrajectoryPoint sample,
            Vector3d seedPosYup,
            System.Func<double, double, double, Vector3d> getBodyRelativeSurfacePositionYup,
            double rotationPeriod,
            bool bodyRotates,
            double inverseRotThresholdAltitude)
        {
            Vector3d recorded = new Vector3d(sample.velocity.x, sample.velocity.y, sample.velocity.z);
            var result = new RecordedSampleSeedVelocity
            {
                VelocityYup = recorded,
                RecordedVelocityYup = recorded,
                Source = SeedVelocitySourceRecorded,
                DeclineReason = null,
                SampleWasRotatingFrame = false,
                AltitudeRuleRotating = IsRecordedVelocityInRotatingFrame(
                    bodyRotates, inverseRotThresholdAltitude, sample.altitude),
                PairPreviousUT = double.NaN,
                Resolution = new RecordedTailVelocityResolution
                {
                    YawDegrees = double.NaN,
                    Residual = double.NaN,
                    OtherResidual = double.NaN,
                    RecordedSpeed = recorded.magnitude
                }
            };

            if (getBodyRelativeSurfacePositionYup == null)
            {
                result.DeclineReason = "surface-position-unavailable";
                return result;
            }
            if (!TrySelectBodyFixedVelocityPair(
                    points, sections, sample,
                    out TrajectoryPoint previous, out TrajectoryPoint tail, out bool sampleIsTail,
                    out string pairReason))
            {
                result.DeclineReason = pairReason;
                return result;
            }
            result.PairPreviousUT = previous.ut;

            Vector3d previousPos = getBodyRelativeSurfacePositionYup(
                previous.latitude, previous.longitude, previous.altitude);
            Vector3d tailPos = getBodyRelativeSurfacePositionYup(
                tail.latitude, tail.longitude, tail.altitude);
            bool resolved = TryResolveRecordedPairVelocityAtSample(
                previous,
                tail,
                sampleIsTail,
                previousPos,
                tailPos,
                seedPosYup,
                rotationPeriod,
                bodyRotates,
                inverseRotThresholdAltitude,
                out RecordedTailVelocityResolution resolution,
                out Vector3d inertialVel);
            result.Resolution = resolution;
            if (!resolved)
            {
                result.DeclineReason = resolution.DeclineReason ?? RecordedTailVelocityResolver.DeclineNoFit;
                return result;
            }

            result.VelocityYup = inertialVel;
            result.Source = SeedVelocitySourcePair;
            result.SampleWasRotatingFrame = resolution.SampleWasRotatingFrame;
            return result;
        }

        /// <summary>
        /// Pure core of the seeds that run in the SAME session as the recording they read
        /// (the scene-exit finalizer's predicted-tail reseed and its recovery start): the
        /// world yaw between the recorded vector and this session's rebuilt position is zero
        /// there, so only the frame is unknown. With a usable pair
        /// (<paramref name="previous"/>, same list and body) the resolver decides it from the
        /// data, which also catches a vessel recorded inertial below the threshold because
        /// the ACTIVE vessel was above it; otherwise the stock altitude rule
        /// (<see cref="IsRecordedVelocityInRotatingFrame"/>) decides: below the
        /// inverse-rotation altitude the vector is surface-relative and the frame velocity at
        /// <paramref name="samplePosYup"/> is added.
        /// </summary>
        internal static RecordedSampleSeedVelocity ResolveSameSessionRecordedVelocity(
            TrajectoryPoint? previous,
            TrajectoryPoint sample,
            Vector3d previousPosYup,
            Vector3d samplePosYup,
            double rotationPeriod,
            bool bodyRotates,
            double inverseRotThresholdAltitude)
        {
            Vector3d recorded = new Vector3d(sample.velocity.x, sample.velocity.y, sample.velocity.z);
            bool altitudeRuleRotating = IsRecordedVelocityInRotatingFrame(
                bodyRotates, inverseRotThresholdAltitude, sample.altitude);
            var result = new RecordedSampleSeedVelocity
            {
                VelocityYup = recorded,
                RecordedVelocityYup = recorded,
                Source = SeedVelocitySourceAltitudeRule,
                DeclineReason = null,
                SampleWasRotatingFrame = altitudeRuleRotating,
                AltitudeRuleRotating = altitudeRuleRotating,
                PairPreviousUT = double.NaN,
                Resolution = new RecordedTailVelocityResolution
                {
                    YawDegrees = double.NaN,
                    Residual = double.NaN,
                    OtherResidual = double.NaN,
                    RecordedSpeed = recorded.magnitude
                }
            };

            if (previous.HasValue)
            {
                result.PairPreviousUT = previous.Value.ut;
                if (TryResolveRecordedPairVelocityAtSample(
                        previous.Value,
                        sample,
                        sampleIsTail: true,
                        previousPosYup,
                        samplePosYup,
                        samplePosYup,
                        rotationPeriod,
                        bodyRotates,
                        inverseRotThresholdAltitude,
                        out RecordedTailVelocityResolution resolution,
                        out Vector3d inertialVel))
                {
                    result.Resolution = resolution;
                    result.VelocityYup = inertialVel;
                    result.Source = SeedVelocitySourcePair;
                    result.SampleWasRotatingFrame = resolution.SampleWasRotatingFrame;
                    return result;
                }

                result.Resolution = resolution;
                result.DeclineReason = resolution.DeclineReason ?? RecordedTailVelocityResolver.DeclineNoFit;
            }
            else
            {
                result.DeclineReason = "pair-single-sample";
            }

            if (altitudeRuleRotating)
            {
                result.VelocityYup = recorded + ComputeRotatingFrameVelocityYup(
                    samplePosYup, bodyRotates ? rotationPeriod : double.NaN);
            }
            return result;
        }

        /// <summary>
        /// Pure: the invariant-culture Verbose line describing a resolved or declined seed
        /// velocity (<see cref="RecordedSampleSeedVelocity"/>).
        /// </summary>
        internal static string FormatRecordedSampleSeedVelocityLine(
            string prefix,
            string site,
            string recordingId,
            string bodyName,
            TrajectoryPoint sample,
            RecordedSampleSeedVelocity v)
        {
            RecordedTailVelocityResolution r = v.Resolution;
            string velFrame = v.Source == SeedVelocitySourceRecorded
                ? "(unresolved)"
                : (v.SampleWasRotatingFrame ? "rotating" : "inertial");
            return string.Format(CultureInfo.InvariantCulture,
                "{0}: site={1} rec={2} body={3} sampleUT={4:F2} prevUT={5:F2} alt={6:F1} source={7} " +
                "decline={8} velFrame={9} altitudeRule={10} yaw={11:F3}deg residual={12:F3} " +
                "otherResidual={13:F3} |recordedVel|={14:F2} |seedVel|={15:F2}",
                prefix ?? "(null)",
                site ?? "(null)",
                recordingId ?? "(null)",
                bodyName ?? "(null)",
                sample.ut,
                v.PairPreviousUT,
                sample.altitude,
                v.Source ?? "(null)",
                v.DeclineReason ?? "(none)",
                velFrame,
                v.AltitudeRuleRotating ? "rotating" : "inertial",
                r.YawDegrees,
                r.Residual,
                r.OtherResidual,
                v.RecordedVelocityYup.magnitude,
                v.VelocityYup.magnitude);
        }

        /// <summary>
        /// Pure: the Verbose line <see cref="TryFromHistoricalLatLonAltAndRecordedVelocityWithEpoch"/>
        /// writes. The accepted prefix
        /// <c>TryFromHistoricalLatLonAltAndRecordedVelocityWithEpoch: body=</c> is a required
        /// log token of harness lanes V20M / V20T, so a decline writes a different prefix
        /// (<c>... declined: body=</c>) that cannot satisfy it.
        /// </summary>
        internal static string FormatHistoricalTailPairSeedLine(
            string bodyName,
            TrajectoryPoint previous,
            TrajectoryPoint tail,
            double epochUT,
            bool accepted,
            HistoricalTailPairSeedInputs result,
            double semiMajorAxis,
            double eccentricity,
            double inclination)
        {
            RecordedTailVelocityResolution r = result.Resolution;
            return string.Format(CultureInfo.InvariantCulture,
                "TryFromHistoricalLatLonAltAndRecordedVelocityWithEpoch{0}: body={1} recordedUT={2:F2} " +
                "epochUT={3:F2} loopShift={4:F2} lat={5:F4} lon={6:F4} inertialLon={7:F4} alt={8:F1} " +
                "|vel|={9:F2} prevUT={10:F2} resolved={11} decline={12} velFrame={13} altitudeRule={14} " +
                "yaw={15:F3}deg residual={16:F3} otherResidual={17:F3} |inertialVel|={18:F2} " +
                "sma={19:F1} ecc={20:F6} inc={21:F4}",
                accepted ? "" : " declined",
                bodyName ?? "(null)",
                tail.ut,
                epochUT,
                epochUT - tail.ut,
                tail.latitude,
                tail.longitude,
                result.InertialLongitude,
                tail.altitude,
                r.RecordedSpeed,
                previous.ut,
                accepted,
                result.FailureReason ?? "(none)",
                r.Resolved ? (r.SampleWasRotatingFrame ? "rotating" : "inertial") : "(unresolved)",
                result.AltitudeRuleRotating ? "rotating" : "inertial",
                r.YawDegrees,
                r.Residual,
                r.OtherResidual,
                r.Resolved ? r.InertialVelocityYup.magnitude : double.NaN,
                semiMajorAxis,
                eccentricity,
                inclination);
        }

        /// <summary>
        /// Build orbit elements at <paramref name="ut"/> from a body-fixed
        /// lat/lon/alt position and a recorder-frame Y-up world velocity.
        /// </summary>
        /// <remarks>
        /// The velocity is passed through unchanged, so it is right only when the
        /// recorded vector is inertial AND the world axes it was captured in match this
        /// session's; neither is guaranteed (see the class summary).
        /// <see cref="TryFromRecordedTailPair"/> resolves both from two samples.
        /// </remarks>
        internal static void FromLatLonAltAndRecordedVelocity(
            Orbit dst,
            CelestialBody body,
            double lat,
            double lon,
            double alt,
            Vector3d recordedVelWorldYup,
            double ut)
        {
            Vector3d worldPos = body.GetWorldSurfacePosition(lat, lon, alt);
            Vector3d bodyRelPos = worldPos - body.position;
            StateVectorInputs inputs = ComputeRecordedVelocityInputs(
                worldPos,
                body.position,
                recordedVelWorldYup);
            dst.UpdateFromStateVectors(
                inputs.PositionForUpdate,
                inputs.VelocityForUpdate,
                body,
                ut);
            // Magnitudes captured here are the diagnostic that would have made the
            // original frame-mismatch bug visible in the log: |worldPos| reflects
            // the body.position offset, |bodyRelPos| should track Radius+altitude.
            // If a future regression silently drops the (pos − body.position) step
            // again, the gap between these two magnitudes will show it.
            ParsekLog.Verbose("OrbitReseed",
                string.Format(CultureInfo.InvariantCulture,
                    "FromLatLonAltAndRecordedVelocity: body={0} ut={1:F2} " +
                    "lat={2:F4} lon={3:F4} alt={4:F1} " +
                    "|worldPos|={5:F1} |bodyRelPos|={6:F1} |bodyPos|={7:F1} |vel|={8:F2}",
                    body?.name ?? "(null)",
                    ut,
                    lat,
                    lon,
                    alt,
                    worldPos.magnitude,
                    bodyRelPos.magnitude,
                    body != null ? body.position.magnitude : 0.0,
                    recordedVelWorldYup.magnitude));
        }

        /// <summary>
        /// Unity wrapper over <see cref="ResolveRecordedSampleSeedVelocity"/>: positions are
        /// rebuilt with this session's <c>GetWorldSurfacePosition</c>, so the returned
        /// velocity is in the same world axes as <paramref name="seedWorldPosYup"/> (a
        /// world-absolute Y-up position the caller seeds at).
        /// </summary>
        internal static RecordedSampleSeedVelocity ResolveRecordedSampleSeedVelocityInSession(
            CelestialBody body,
            List<TrajectoryPoint> points,
            List<TrackSection> sections,
            TrajectoryPoint sample,
            Vector3d seedWorldPosYup)
        {
            if (body == null)
            {
                return ResolveRecordedSampleSeedVelocity(
                    points, sections, sample, Vector3d.zero, null, double.NaN, false, double.NaN);
            }

            Vector3d bodyPos = body.position;
            return ResolveRecordedSampleSeedVelocity(
                points,
                sections,
                sample,
                seedWorldPosYup - bodyPos,
                (lat, lon, alt) => body.GetWorldSurfacePosition(lat, lon, alt) - bodyPos,
                body.rotationPeriod,
                body.rotates,
                body.inverseRotThresholdAltitude);
        }

        /// <summary>
        /// Build orbit elements at <paramref name="sample"/>'s UT from a recorded sample
        /// whose recording ran in THIS session (the scene-exit finalizer), with the velocity
        /// frame resolved by <see cref="ResolveSameSessionRecordedVelocity"/>: from the pair
        /// when <paramref name="previous"/> resolves, else by the stock altitude rule.
        /// Position as <see cref="FromLatLonAltAndRecordedVelocity"/>.
        /// </summary>
        internal static RecordedSampleSeedVelocity FromLatLonAltAndSameSessionRecordedVelocity(
            Orbit dst,
            CelestialBody body,
            TrajectoryPoint? previous,
            TrajectoryPoint sample,
            string context,
            string recordingId)
        {
            Vector3d samplePos = body.GetWorldSurfacePosition(
                sample.latitude, sample.longitude, sample.altitude) - body.position;
            Vector3d previousPos = previous.HasValue
                ? body.GetWorldSurfacePosition(
                    previous.Value.latitude, previous.Value.longitude, previous.Value.altitude) - body.position
                : Vector3d.zero;
            RecordedSampleSeedVelocity v = ResolveSameSessionRecordedVelocity(
                previous,
                sample,
                previousPos,
                samplePos,
                body.rotationPeriod,
                body.rotates,
                body.inverseRotThresholdAltitude);

            dst.UpdateFromStateVectors(samplePos.xzy, v.VelocityYup.xzy, body, sample.ut);
            // The scene-exit finalizer also runs from the finalization cache refresh, so the
            // same sample can be reseeded on every refresh tick.
            ParsekLog.VerboseRateLimited("OrbitReseed",
                string.Format(CultureInfo.InvariantCulture,
                    "same-session-velocity-{0}-{1}-{2:F2}", context ?? "(null)", recordingId ?? "(null)", sample.ut),
                FormatRecordedSampleSeedVelocityLine(
                    "FromLatLonAltAndSameSessionRecordedVelocity",
                    context,
                    recordingId,
                    body.name,
                    sample,
                    v),
                30.0);
            return v;
        }

        /// <summary>
        /// Build orbit elements at <paramref name="tail"/>'s UT from the tail sample and
        /// the coast sample before it, with the velocity frame and the world yaw resolved
        /// by <see cref="RecordedTailVelocityResolver.TryResolve"/> rather than trusted.
        /// Both positions are rebuilt with this session's <c>GetWorldSurfacePosition</c>,
        /// so the seeded state is in this session's world axes. Leaves
        /// <paramref name="dst"/> untouched and returns false when the pair cannot be
        /// resolved (<see cref="RecordedTailVelocityResolution.DeclineReason"/>).
        /// </summary>
        internal static bool TryFromRecordedTailPair(
            Orbit dst,
            CelestialBody body,
            TrajectoryPoint previous,
            TrajectoryPoint tail,
            out RecordedTailVelocityResolution resolution)
        {
            Vector3d previousPos = body.GetWorldSurfacePosition(
                previous.latitude, previous.longitude, previous.altitude) - body.position;
            Vector3d tailPos = body.GetWorldSurfacePosition(
                tail.latitude, tail.longitude, tail.altitude) - body.position;
            bool altitudeRuleRotating = IsRecordedVelocityInRotatingFrame(
                body.rotates,
                body.inverseRotThresholdAltitude,
                tail.altitude);

            bool resolved = RecordedTailVelocityResolver.TryResolve(
                previousPos,
                previous.ut,
                new Vector3d(previous.velocity.x, previous.velocity.y, previous.velocity.z),
                tailPos,
                tail.ut,
                new Vector3d(tail.velocity.x, tail.velocity.y, tail.velocity.z),
                body.rotates ? body.rotationPeriod : double.NaN,
                altitudeRuleRotating,
                out resolution);

            ParsekLog.Verbose("OrbitReseed",
                string.Format(CultureInfo.InvariantCulture,
                    "TryFromRecordedTailPair: body={0} prevUT={1:F2} tailUT={2:F2} alt={3:F1} " +
                    "resolved={4} decline={5} velFrame={6} altitudeRule={7} yaw={8:F3}deg " +
                    "residual={9:F3} otherResidual={10:F3} |recordedVel|={11:F2} |inertialVel|={12:F2}",
                    body.name ?? "(null)",
                    previous.ut,
                    tail.ut,
                    tail.altitude,
                    resolved,
                    resolution.DeclineReason ?? "(none)",
                    resolution.SampleWasRotatingFrame ? "rotating" : "inertial",
                    altitudeRuleRotating ? "rotating" : "inertial",
                    resolution.YawDegrees,
                    resolution.Residual,
                    resolution.OtherResidual,
                    resolution.RecordedSpeed,
                    resolution.InertialVelocityYup.magnitude));
            if (!resolved)
                return false;

            dst.UpdateFromStateVectors(
                tailPos.xzy,
                resolution.InertialVelocityYup.xzy,
                body,
                tail.ut);
            return true;
        }

        /// <summary>
        /// Build orbit elements at <paramref name="ut"/> from a world-absolute
        /// Y-up position and a velocity that is ALREADY in Zup-local frame.
        /// </summary>
        /// <remarks>
        /// Use this when the velocity comes from
        /// <c>Orbit.getOrbitalVelocityAtUT</c> or any other API that already
        /// returns Zup. Do NOT use this for recorder-stored velocities — they
        /// are Y-up Unity world; route those through
        /// <see cref="FromLatLonAltAndRecordedVelocity"/> instead.
        /// </remarks>
        internal static void FromWorldPosAndZupVelocity(
            Orbit dst,
            CelestialBody body,
            Vector3d worldPosYup,
            Vector3d velAlreadyZup,
            double ut)
        {
            Vector3d bodyRelPos = worldPosYup - body.position;
            StateVectorInputs inputs = ComputeZupVelocityInputs(
                worldPosYup,
                body.position,
                velAlreadyZup);
            dst.UpdateFromStateVectors(
                inputs.PositionForUpdate,
                inputs.VelocityForUpdate,
                body,
                ut);
            ParsekLog.Verbose("OrbitReseed",
                string.Format(CultureInfo.InvariantCulture,
                    "FromWorldPosAndZupVelocity: body={0} ut={1:F2} " +
                    "|worldPos|={2:F1} |bodyRelPos|={3:F1} |bodyPos|={4:F1} |vel|={5:F2}",
                    body?.name ?? "(null)",
                    ut,
                    worldPosYup.magnitude,
                    bodyRelPos.magnitude,
                    body != null ? body.position.magnitude : 0.0,
                    velAlreadyZup.magnitude));
        }

        /// <summary>
        /// Build orbit elements from a world-absolute Y-up position and a
        /// recorder-frame Y-up world velocity.
        /// </summary>
        internal static void FromWorldPosAndRecordedVelocity(
            Orbit dst,
            CelestialBody body,
            Vector3d worldPosYup,
            Vector3d recordedVelWorldYup,
            double ut)
        {
            Vector3d bodyRelPos = worldPosYup - body.position;
            StateVectorInputs inputs = ComputeRecordedVelocityInputs(
                worldPosYup,
                body.position,
                recordedVelWorldYup);
            dst.UpdateFromStateVectors(
                inputs.PositionForUpdate,
                inputs.VelocityForUpdate,
                body,
                ut);
            ParsekLog.Verbose("OrbitReseed",
                string.Format(CultureInfo.InvariantCulture,
                    "FromWorldPosAndRecordedVelocity: body={0} ut={1:F2} " +
                    "|worldPos|={2:F1} |bodyRelPos|={3:F1} |bodyPos|={4:F1} |vel|={5:F2}",
                    body?.name ?? "(null)",
                    ut,
                    worldPosYup.magnitude,
                    bodyRelPos.magnitude,
                    body != null ? body.position.magnitude : 0.0,
                    recordedVelWorldYup.magnitude));
        }

        /// <summary>
        /// Build orbit elements at <paramref name="tail"/>'s UT from the tail sample and the
        /// coast sample before it, placed at the tail's HISTORICAL inertial position (the
        /// body rotation phase at the tail UT), with the velocity frame and world yaw
        /// resolved from the pair (<see cref="TryComputeHistoricalTailPairInputs"/>). The
        /// orbit epoch is the tail UT; for the loop-shifted gap glide use
        /// <see cref="TryFromHistoricalLatLonAltAndRecordedVelocityWithEpoch"/>.
        /// </summary>
        internal static bool TryFromHistoricalLatLonAltAndRecordedVelocity(
            Orbit dst,
            CelestialBody body,
            TrajectoryPoint previous,
            TrajectoryPoint tail,
            double rotationPeriod,
            double initialRotationDeg,
            out HistoricalTailPairSeedInputs result,
            string recordingId = null)
        {
            return TryFromHistoricalLatLonAltAndRecordedVelocityWithEpoch(
                dst,
                body,
                previous,
                tail,
                tail.ut,
                rotationPeriod,
                initialRotationDeg,
                out result,
                recordingId);
        }

        /// <summary>
        /// As <see cref="TryFromHistoricalLatLonAltAndRecordedVelocity"/>, but the orbit EPOCH
        /// is the separate <paramref name="epochUT"/>: the orbit-raise gap glide places the
        /// sample at its recorded inertial position (rotation phase at the recorded UT) and
        /// seeds at <c>recordedUT + loopEpochShiftSeconds == liveUT</c> so the loop phase is
        /// kept. Leaves <paramref name="dst"/> untouched and returns false on a decline
        /// (<see cref="HistoricalTailPairSeedInputs.FailureReason"/>); both outcomes are
        /// logged (<see cref="FormatHistoricalTailPairSeedLine"/>).
        /// </summary>
        internal static bool TryFromHistoricalLatLonAltAndRecordedVelocityWithEpoch(
            Orbit dst,
            CelestialBody body,
            TrajectoryPoint previous,
            TrajectoryPoint tail,
            double epochUT,
            double rotationPeriod,
            double initialRotationDeg,
            out HistoricalTailPairSeedInputs result,
            string recordingId = null)
        {
            if (dst == null || body == null)
            {
                result = new HistoricalTailPairSeedInputs
                {
                    InertialLongitude = double.NaN,
                    SeedEpochUT = double.NaN,
                    FailureReason = "null-input"
                };
                return false;
            }

            bool ok = TryComputeHistoricalTailPairInputs(
                previous,
                tail,
                epochUT,
                rotationPeriod,
                initialRotationDeg,
                body.rotates,
                body.inverseRotThresholdAltitude,
                (historicalLat, historicalLon, historicalAlt) =>
                    body.GetRelSurfacePosition(historicalLat, historicalLon, historicalAlt),
                out result);
            if (ok)
            {
                dst.UpdateFromStateVectors(
                    result.Inputs.PositionForUpdate,
                    result.Inputs.VelocityForUpdate,
                    body,
                    result.SeedEpochUT);
            }

            LogHistoricalTailPairSeedLine(
                ok,
                recordingId,
                body.name,
                result.FailureReason,
                FormatHistoricalTailPairSeedLine(
                    body.name,
                    previous,
                    tail,
                    epochUT,
                    ok,
                    result,
                    ok ? dst.semiMajorAxis : double.NaN,
                    ok ? dst.eccentricity : double.NaN,
                    ok ? dst.inclination : double.NaN));
            return ok;
        }

        internal const double HistoricalTailPairDeclineLogIntervalSeconds = 5.0;

        /// <summary>
        /// Pure: the rate-limit key of a declined historical tail-pair seed line, per
        /// recording (the body when no recording id is known) and decline reason, so a
        /// changed reason still prints at once.
        /// </summary>
        internal static string HistoricalTailPairDeclineRateKey(
            string recordingId,
            string bodyName,
            string failureReason)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "historical-tail-pair-declined-{0}-{1}",
                string.IsNullOrEmpty(recordingId) ? "body:" + (bodyName ?? "(null)") : recordingId,
                failureReason ?? "(none)");
        }

        /// <summary>
        /// Writes the <see cref="FormatHistoricalTailPairSeedLine"/> line. An accepted seed is
        /// plain Verbose (harness lanes V20M / V20T count its prefix); a decline is rate-limited
        /// (<see cref="HistoricalTailPairDeclineRateKey"/>) because the gap glide re-seeds every
        /// frame and the tracking-station refresh at 4 Hz, so a standing decline would flood.
        /// </summary>
        internal static void LogHistoricalTailPairSeedLine(
            bool accepted,
            string recordingId,
            string bodyName,
            string failureReason,
            string line)
        {
            if (accepted)
            {
                ParsekLog.Verbose("OrbitReseed", line);
                return;
            }

            ParsekLog.VerboseRateLimited("OrbitReseed",
                HistoricalTailPairDeclineRateKey(recordingId, bodyName, failureReason),
                line,
                HistoricalTailPairDeclineLogIntervalSeconds);
        }

        /// <summary>
        /// Pure predicate: does the reseeded orbit carry any non-finite Kepler element?
        /// The failure shape this guards: a landed endpoint with EXACTLY zero velocity
        /// reseeds to h=0 / ecc=1, and <c>Orbit.UpdateFromStateVectors</c> then computes
        /// SMA = -semiLatusRectum/(ecc^2-1) = 0/0 = NaN. A float-residue near-zero
        /// velocity instead yields a finite ~r/2 SMA, so the NaN appears run-to-run
        /// flakily. Returns the offending element names (comma-joined) for logging.
        /// </summary>
        internal static bool HasNonFiniteOrbitElement(
            double semiMajorAxis,
            double eccentricity,
            double inclination,
            double argumentOfPeriapsis,
            double lan,
            double meanAnomalyAtEpoch,
            double epoch,
            out string nonFiniteElements)
        {
            var offenders = new System.Collections.Generic.List<string>(7);
            if (!IsFinite(semiMajorAxis)) offenders.Add("SMA");
            if (!IsFinite(eccentricity)) offenders.Add("ECC");
            if (!IsFinite(inclination)) offenders.Add("INC");
            if (!IsFinite(argumentOfPeriapsis)) offenders.Add("LPE");
            if (!IsFinite(lan)) offenders.Add("LAN");
            if (!IsFinite(meanAnomalyAtEpoch)) offenders.Add("MNA");
            if (!IsFinite(epoch)) offenders.Add("EPH");

            nonFiniteElements = offenders.Count > 0 ? string.Join(",", offenders) : null;
            return offenders.Count > 0;
        }

        /// <summary>
        /// Orbit-taking convenience wrapper over the pure element predicate.
        /// A null orbit counts as non-finite (nothing usable to serialize).
        /// </summary>
        internal static bool HasNonFiniteOrbitElement(Orbit orbit, out string nonFiniteElements)
        {
            if (orbit == null)
            {
                nonFiniteElements = "(null orbit)";
                return true;
            }

            return HasNonFiniteOrbitElement(
                orbit.semiMajorAxis,
                orbit.eccentricity,
                orbit.inclination,
                orbit.argumentOfPeriapsis,
                orbit.LAN,
                orbit.meanAnomalyAtEpoch,
                orbit.epoch,
                out nonFiniteElements);
        }

        internal static double WrapLongitudeDegrees(double longitude)
        {
            if (!IsFinite(longitude))
                return longitude;

            longitude %= 360.0;
            if (longitude <= -180.0)
                longitude += 360.0;
            if (longitude > 180.0)
                longitude -= 360.0;
            return longitude;
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
