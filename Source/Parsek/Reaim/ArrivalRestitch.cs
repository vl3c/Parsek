using System;

namespace Parsek.Reaim
{
    /// <summary>
    /// PURE math for the S4 arrival re-stitch (docs/dev/plans/reaim-s4-arrival-restitch.md): the
    /// rigid rotation of a re-aim landing's recorded in-SOI arrival chain about the destination
    /// body's SPIN AXIS so the recorded approach connects to the re-aimed transfer's actual
    /// SOI-entry geometry, plus the matching descent-trigger congruence offset that keeps the
    /// touchdown at the RECORDED body-fixed site (the ratified product decision: the landing always
    /// takes place at the recorded site; the relocate-the-landing alternative is rejected).
    ///
    /// <para>The spin axis is the only site-preserving rotation axis: the body-fixed descent clip
    /// renders wherever the body's rotation puts the recorded lat/lon, so a spin-axis rotation of
    /// the approach by theta is exactly compensated by waiting theta/omega_rot longer for the site
    /// to rotate under the rotated deorbit point. Any other axis would change the reachable
    /// latitude, i.e. move the landing site. In KSP all bodies have zero axial tilt, so the spin
    /// axis equals the orbital reference-plane normal - which in the .xzy-unswizzled WORLD frame
    /// is +Y, NOT +Z (the PR #1196 .z-vs-.y trap; see the frame note on TryBearingAndLatitude) -
    /// and the rotation is applied as a LAN advance on the destination-bodied OrbitSegments
    /// (<see cref="ReaimSegmentAssembler.RotateLanForParkRephase"/>).</para>
    ///
    /// <para>All helpers are pure (no Unity) and xUnit-tested. The live entry-state extraction
    /// (KSP Orbit evaluation) stays in <see cref="ReaimPlaybackResolver"/>.</para>
    /// </summary>
    internal static class ArrivalRestitch
    {
        /// <summary>
        /// Entry directions steeper than this off the reference plane decline the re-stitch: the
        /// in-plane bearing that drives the spin-axis rotation becomes ill-conditioned near the
        /// pole, and a near-polar approach is outside the Supported near-equatorial landing profile
        /// anyway. Fail closed (no rotation, shipped behavior) rather than rotate on a noisy bearing.
        /// </summary>
        internal const double MaxEntryLatitudeDeg = 60.0;

        /// <summary>
        /// The signed spin-axis rotation (degrees, in (-180, 180]) that carries the RECORDED
        /// destination-relative SOI-entry direction onto the RE-AIMED transfer's actual
        /// destination-relative SOI-entry direction: the in-plane bearing difference
        /// <c>bearing(newEntry) - bearing(recordedEntry)</c> about the reference-plane normal of
        /// the .xzy-unswizzled (world) frame, which is <b>+Y</b> (= the zero-tilt body spin axis;
        /// prograde-positive bearing = atan2(-z, x) - see the frame note on the private helper).
        /// <paramref name="recordedLatitudeDeg"/> / <paramref name="newLatitudeDeg"/>
        /// report each direction's out-of-plane latitude (degrees; the residual a spin-axis
        /// rotation cannot and must not close - closing it would move the landing site).
        /// Returns NaN (decline, no rotation) when either vector is NaN / zero-projection or
        /// either latitude exceeds <see cref="MaxEntryLatitudeDeg"/>. Pure.
        /// </summary>
        internal static double ComputeRestitchRotationDeg(
            Vector3d recordedEntry, Vector3d newEntry,
            out double recordedLatitudeDeg, out double newLatitudeDeg)
        {
            recordedLatitudeDeg = double.NaN;
            newLatitudeDeg = double.NaN;
            if (!TryBearingAndLatitude(recordedEntry, out double bearingRec, out recordedLatitudeDeg)
                || !TryBearingAndLatitude(newEntry, out double bearingNew, out newLatitudeDeg))
                return double.NaN;
            if (Math.Abs(recordedLatitudeDeg) > MaxEntryLatitudeDeg
                || Math.Abs(newLatitudeDeg) > MaxEntryLatitudeDeg)
                return double.NaN; // near-polar entry: decline (fail closed to the shipped behavior)
            return Wrap180(bearingNew - bearingRec);
        }

        /// <summary>
        /// The residual angle (degrees, in (-180, 180]) between the re-aimed transfer's
        /// destination-relative entry VELOCITY bearing and the recorded entry velocity bearing
        /// after the <paramref name="rotationDeg"/> re-stitch rotation:
        /// <c>bearing(newVel) - bearing(recordedVel) - rotationDeg</c>, wrapped. Diagnostic only
        /// (measure-first: logged per window, never an input). NaN on degenerate input. Pure.
        /// </summary>
        internal static double VelocityBearingResidualDeg(
            Vector3d recordedEntryVel, Vector3d newEntryVel, double rotationDeg)
        {
            if (double.IsNaN(rotationDeg) || double.IsInfinity(rotationDeg)
                || !TryBearingAndLatitude(recordedEntryVel, out double bearingRec, out _)
                || !TryBearingAndLatitude(newEntryVel, out double bearingNew, out _))
                return double.NaN;
            return Wrap180(bearingNew - bearingRec - rotationDeg);
        }

        /// <summary>
        /// The descent-trigger congruence offset for a re-stitch rotation of
        /// <paramref name="rotationDeg"/>: the extra body-rotation time that puts the recorded
        /// body-fixed landing site under the rotated deorbit point,
        /// <c>rotationDeg / 360 * rotationPeriod</c> normalized into [0, rotationPeriod).
        /// The offset shifts ONLY the trigger congruence
        /// (<see cref="DescentTrigger.ComputeRotationAlignedTriggerUT"/>); the descent head stays
        /// anchored at the recorded deorbit UT, so the clip and the touchdown lat/lon are untouched
        /// (the recorded-site invariant). Returns 0 (shipped behavior) on NaN / infinite inputs or
        /// a non-positive rotation period. Pure.
        /// </summary>
        internal static double SiteAlignOffsetSeconds(double rotationDeg, double rotationPeriod)
        {
            if (double.IsNaN(rotationDeg) || double.IsInfinity(rotationDeg)
                || double.IsNaN(rotationPeriod) || double.IsInfinity(rotationPeriod)
                || rotationPeriod <= 0.0)
                return 0.0;
            double offset = rotationDeg / 360.0 * rotationPeriod;
            offset %= rotationPeriod;
            if (offset < 0.0)
                offset += rotationPeriod;
            return offset;
        }

        /// <summary>
        /// In-plane bearing and out-of-plane latitude (degrees) of a direction in the
        /// .xzy-unswizzled frame - which is KSP's WORLD frame, whose reference-plane normal (world
        /// up, and with zero axial tilt every body's spin axis) is <b>+Y, NOT +Z</b>. This is the
        /// same .z-vs-.y world-frame trap the plane-tilt achievability gate hit and fixed in
        /// PR #1196 (see <see cref="ReaimTransferSynthesizer"/>'s AchievablePlaneInclinationDegrees
        /// frame note): using z as "up" here would read the in-plane angle as "latitude" and
        /// quantize every real bearing toward 0 / 180. In-plane components are x and z; the
        /// PROGRADE-positive bearing is <c>atan2(-z, x)</c>, calibrated against the shipped
        /// park-rephase pairing (for a prograde orbit <c>Cross(r, v)</c> points +Y and a LAN
        /// advance of +D moves the position +D in this sense - the same sense the body spin
        /// advances a surface site, so the <see cref="SiteAlignOffsetSeconds"/> pairing holds).
        /// Latitude = <c>asin(y/|v|)</c>. False on NaN / infinite components or a degenerate
        /// (near-zero) in-plane projection. Pure.
        /// </summary>
        private static bool TryBearingAndLatitude(Vector3d v, out double bearingDeg, out double latitudeDeg)
        {
            bearingDeg = double.NaN;
            latitudeDeg = double.NaN;
            if (double.IsNaN(v.x) || double.IsNaN(v.y) || double.IsNaN(v.z)
                || double.IsInfinity(v.x) || double.IsInfinity(v.y) || double.IsInfinity(v.z))
                return false;
            double planar = Math.Sqrt(v.x * v.x + v.z * v.z);
            double mag = Math.Sqrt(planar * planar + v.y * v.y);
            if (!(planar > 0.0) || !(mag > 0.0))
                return false; // zero / axis-aligned vector: no bearing
            bearingDeg = Math.Atan2(-v.z, v.x) * 180.0 / Math.PI;
            latitudeDeg = Math.Asin(v.y / mag) * 180.0 / Math.PI;
            return true;
        }

        private static double Wrap180(double deg)
        {
            double d = deg % 360.0;
            if (d > 180.0) d -= 360.0;
            if (d <= -180.0) d += 360.0;
            return d;
        }
    }
}

