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
    /// Two helpers because the two natural input shapes differ in their velocity
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

        internal static bool TryComputeHistoricalSurfaceInputs(
            double lat,
            double lon,
            double alt,
            Vector3d recordedVelWorldYup,
            double recordedUT,
            double rotationPeriod,
            double initialRotationDeg,
            System.Func<double, double, double, Vector3d> getBodyRelativeSurfacePositionYup,
            out StateVectorInputs inputs,
            out double inertialLongitude,
            out string failureReason)
        {
            inputs = default(StateVectorInputs);
            inertialLongitude = double.NaN;
            failureReason = null;

            if (!IsFinite(rotationPeriod) || System.Math.Abs(rotationPeriod) <= double.Epsilon)
            {
                failureReason = "historical-rotation-unavailable";
                return false;
            }
            if (!IsFinite(recordedUT)
                || !IsFinite(lat)
                || !IsFinite(lon)
                || !IsFinite(alt)
                || !IsFinite(recordedVelWorldYup))
            {
                failureReason = "non-finite-state-vector";
                return false;
            }
            if (getBodyRelativeSurfacePositionYup == null)
            {
                failureReason = "historical-surface-position-unavailable";
                return false;
            }

            double phaseDeg = initialRotationDeg + (recordedUT * 360.0) / rotationPeriod;
            inertialLongitude = WrapLongitudeDegrees(lon + phaseDeg);
            Vector3d bodyRelHistoricalYup = getBodyRelativeSurfacePositionYup(lat, inertialLongitude, alt);
            if (!IsFinite(bodyRelHistoricalYup))
            {
                failureReason = "historical-surface-position-non-finite";
                return false;
            }

            inputs = new StateVectorInputs(bodyRelHistoricalYup.xzy, recordedVelWorldYup.xzy);
            return true;
        }

        /// <summary>
        /// Pure epoch-split resolver for the gap glide: reconstruct the inertial seed inputs at
        /// <paramref name="recordedUT"/> (the rotation-phase UT) but report the SEPARATE
        /// <paramref name="epochUT"/> the orbit must be seeded at (== recordedUT + loop shift).
        /// Unity-free (the surface lookup is injected) so the recordedUT-position / epochUT-seed
        /// split is directly testable. The wrapper
        /// <see cref="TryFromHistoricalLatLonAltAndRecordedVelocityWithEpoch"/> calls this then
        /// applies <paramref name="seedEpochUT"/> to <c>Orbit.UpdateFromStateVectors</c>.
        /// </summary>
        internal static bool TryComputeHistoricalSurfaceInputsWithEpoch(
            double lat,
            double lon,
            double alt,
            Vector3d recordedVelWorldYup,
            double recordedUT,
            double epochUT,
            double rotationPeriod,
            double initialRotationDeg,
            System.Func<double, double, double, Vector3d> getBodyRelativeSurfacePositionYup,
            out StateVectorInputs inputs,
            out double inertialLongitude,
            out double seedEpochUT,
            out string failureReason)
        {
            inputs = default(StateVectorInputs);
            inertialLongitude = double.NaN;
            seedEpochUT = double.NaN;
            failureReason = null;

            if (!IsFinite(epochUT))
            {
                failureReason = "non-finite-epoch";
                return false;
            }

            if (!TryComputeHistoricalSurfaceInputs(
                    lat,
                    lon,
                    alt,
                    recordedVelWorldYup,
                    recordedUT,
                    rotationPeriod,
                    initialRotationDeg,
                    getBodyRelativeSurfacePositionYup,
                    out inputs,
                    out inertialLongitude,
                    out failureReason))
            {
                return false;
            }

            seedEpochUT = epochUT;
            return true;
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
        /// Build orbit elements from a historical body-relative surface
        /// position reconstructed at <paramref name="ut"/> and a recorder-frame
        /// Y-up world velocity. The orbit epoch is seeded at <paramref name="ut"/>
        /// (no loop shift); for the loop-shifted gap-glide variant use
        /// <see cref="TryFromHistoricalLatLonAltAndRecordedVelocityWithEpoch"/>.
        /// </summary>
        internal static bool TryFromHistoricalLatLonAltAndRecordedVelocity(
            Orbit dst,
            CelestialBody body,
            double lat,
            double lon,
            double alt,
            Vector3d recordedVelWorldYup,
            double ut,
            double rotationPeriod,
            double initialRotationDeg,
            out double inertialLongitude,
            out string failureReason)
        {
            // Single-UT overload: reconstruct the inertial position at the recorded
            // UT and seed the orbit epoch at the SAME UT. Delegating with
            // epochUT == ut keeps this byte-identical to the original implementation
            // for its OrbitSeedResolver MapPresence caller.
            return TryFromHistoricalLatLonAltAndRecordedVelocityWithEpoch(
                dst,
                body,
                lat,
                lon,
                alt,
                recordedVelWorldYup,
                ut,
                ut,
                rotationPeriod,
                initialRotationDeg,
                out inertialLongitude,
                out failureReason);
        }

        /// <summary>
        /// Build orbit elements from a historical body-relative surface position
        /// reconstructed at <paramref name="recordedUT"/> (the rotation-phase UT),
        /// but seed the orbit EPOCH at a SEPARATE <paramref name="epochUT"/>.
        /// </summary>
        /// <remarks>
        /// The orbit-raise gap glide reconstructs the recorded inertial POSITION at
        /// the point's recorded UT (so it is consistent with the surrounding inertial
        /// orbit segments rather than rotated by live body rotation), but must seed
        /// the orbit epoch at <c>recordedUT + loopEpochShiftSeconds == liveUT</c> so
        /// the loop phase is preserved exactly as the body-fixed
        /// <see cref="FromWorldPosAndRecordedVelocity"/>(ut + shift) call does. The
        /// velocity is a frame no-op pass-through (<c>.xzy</c> only); the POSITION is
        /// the only term whose frame changes (body-fixed surface position lifted into
        /// the inertial frame via the recorded rotation phase). Unlike
        /// <see cref="TrajectoryMath.FrameTransform.LiftToInertial"/> this DOES include
        /// <c>initialRotation</c> (that helper omits it because it round-trips through
        /// LowerFromInertialToWorld where the term cancels; here there is no round-trip,
        /// the single ABSOLUTE seed needs the full rotation phase).
        /// </remarks>
        internal static bool TryFromHistoricalLatLonAltAndRecordedVelocityWithEpoch(
            Orbit dst,
            CelestialBody body,
            double lat,
            double lon,
            double alt,
            Vector3d recordedVelWorldYup,
            double recordedUT,
            double epochUT,
            double rotationPeriod,
            double initialRotationDeg,
            out double inertialLongitude,
            out string failureReason)
        {
            if (dst == null || body == null)
            {
                inertialLongitude = double.NaN;
                failureReason = "null-input";
                return false;
            }

            if (!TryComputeHistoricalSurfaceInputsWithEpoch(
                    lat,
                    lon,
                    alt,
                    recordedVelWorldYup,
                    recordedUT,
                    epochUT,
                    rotationPeriod,
                    initialRotationDeg,
                    (historicalLat, historicalLon, historicalAlt) =>
                        body.GetRelSurfacePosition(historicalLat, historicalLon, historicalAlt),
                    out StateVectorInputs inputs,
                    out inertialLongitude,
                    out double seedEpochUT,
                    out failureReason))
            {
                return false;
            }

            dst.UpdateFromStateVectors(
                inputs.PositionForUpdate,
                inputs.VelocityForUpdate,
                body,
                seedEpochUT);
            ParsekLog.Verbose("OrbitReseed",
                string.Format(CultureInfo.InvariantCulture,
                    "TryFromHistoricalLatLonAltAndRecordedVelocityWithEpoch: body={0} recordedUT={1:F2} " +
                    "epochUT={2:F2} loopShift={3:F2} lat={4:F4} lon={5:F4} inertialLon={6:F4} alt={7:F1} |vel|={8:F2}",
                    body?.name ?? "(null)",
                    recordedUT,
                    epochUT,
                    epochUT - recordedUT,
                    lat,
                    lon,
                    inertialLongitude,
                    alt,
                    recordedVelWorldYup.magnitude));
            return true;
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
