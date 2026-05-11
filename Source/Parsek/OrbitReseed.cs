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
            dst.UpdateFromStateVectors(
                bodyRelPos.xzy,
                recordedVelWorldYup.xzy,
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
            dst.UpdateFromStateVectors(
                bodyRelPos.xzy,
                velAlreadyZup,
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
        /// Build orbit elements at <paramref name="ut"/> from a world-absolute
        /// Y-up position and recorder-frame Y-up world velocity.
        /// </summary>
        /// <remarks>
        /// Use this when the position has already been resolved to world space
        /// before this call (for example map-presence state-vector fallback), but
        /// the velocity is still <see cref="TrajectoryPoint.velocity"/> captured
        /// by the recorder.
        /// </remarks>
        internal static void FromWorldPosAndRecordedVelocity(
            Orbit dst,
            CelestialBody body,
            Vector3d worldPosYup,
            Vector3d recordedVelWorldYup,
            double ut)
        {
            Vector3d bodyRelPos = worldPosYup - body.position;
            dst.UpdateFromStateVectors(
                bodyRelPos.xzy,
                recordedVelWorldYup.xzy,
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
    }
}
