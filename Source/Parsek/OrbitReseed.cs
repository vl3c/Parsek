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
        /// Y-up world velocity.
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
            if (dst == null || body == null)
            {
                inertialLongitude = double.NaN;
                failureReason = "null-input";
                return false;
            }

            if (!TryComputeHistoricalSurfaceInputs(
                    lat,
                    lon,
                    alt,
                    recordedVelWorldYup,
                    ut,
                    rotationPeriod,
                    initialRotationDeg,
                    (historicalLat, historicalLon, historicalAlt) =>
                        body.GetRelSurfacePosition(historicalLat, historicalLon, historicalAlt),
                    out StateVectorInputs inputs,
                    out inertialLongitude,
                    out failureReason))
            {
                return false;
            }

            dst.UpdateFromStateVectors(
                inputs.PositionForUpdate,
                inputs.VelocityForUpdate,
                body,
                ut);
            ParsekLog.Verbose("OrbitReseed",
                string.Format(CultureInfo.InvariantCulture,
                    "TryFromHistoricalLatLonAltAndRecordedVelocity: body={0} ut={1:F2} " +
                    "lat={2:F4} lon={3:F4} inertialLon={4:F4} alt={5:F1} |vel|={6:F2}",
                    body?.name ?? "(null)",
                    ut,
                    lat,
                    lon,
                    inertialLongitude,
                    alt,
                    recordedVelWorldYup.magnitude));
            return true;
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
