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
        /// Use this for state vectors emitted by <see cref="FlightRecorder"/> —
        /// the recorder stores <c>TrajectoryPoint.velocity</c> in Y-up Unity
        /// world axes, body-relative inertial in steady-state operation (with
        /// brief Krakensbane shifts compensated by <c>+ GetFrameVelocity()</c>).
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
