using System;
using System.Globalization;

namespace Parsek.InGameTests
{
    // P6 on-camera landing acceptance (docs/dev/plans/reaim-destination-arrival-alignment.md Phase 6,
    // unblocked by the S4 arrival re-stitch, docs/dev/plans/reaim-s4-arrival-restitch.md): the
    // landing-coincidence canary. The S4 composition pairs two rotations that the pure xUnit suite can
    // only test by CONVENTION: the assembler rotates the arrival chain by a LAN advance of theta (the
    // KSP Orbit orientation contract), and the descent trigger waits theta/360*T_rot longer so the
    // BODY'S OWN spin carries the recorded body-fixed landing site under the rotated deorbit point.
    // Whether those two rotations share axis AND sense on live KSP (Unity's left-handed world, the
    // .xzy swizzle, CelestialBody.angularVelocity's direction) is exactly what a headless test cannot
    // prove - a sign error would put the site 2*theta away at the handoff. This canary measures BOTH
    // rotations with live KSP primitives and asserts they coincide:
    //
    //  - the LAN-advance world rotation: two live Orbits around the home body differing ONLY by
    //    LAN += theta, evaluated at the same UT (KSP's own Orbit math - the same contract
    //    RotateLanForParkRephase feeds the render path);
    //  - the body-spin world rotation over the S4 offset: the site's body-centered offset vector
    //    turned by CelestialBody.angularVelocity * offset (KSP's own spin truth).
    //
    // If the production pairing is correct the two signed advances match (the rotated deorbit point
    // and the site COINCIDE at the trigger); the touchdown lat/lon itself is untouched by
    // construction (body-fixed data never rotates - asserted headlessly in ArrivalRestitchTests).
    public class ReaimLandingCoincidenceInGameTest
    {
        private const double ThetaDeg = 30.0;   // a representative S4 re-stitch rotation
        private const double SiteLatDeg = 0.0;  // equatorial site (the Supported profile)
        private const double SiteLonDeg = 15.0;

        [InGameTest(Category = "Periodicity", Scene = GameScenes.FLIGHT,
            Description = "S4/P6 landing coincidence: the LAN-advance rotation (the re-stitched "
                + "arrival render) and the body-spin rotation over the S4 trigger offset share axis "
                + "and sense on live KSP, so the rotated deorbit point meets the recorded body-fixed "
                + "site at the trigger")]
        public void S4Restitch_RotatedDeorbitPoint_MeetsRecordedSiteAtTrigger()
        {
            var ic = CultureInfo.InvariantCulture;
            CelestialBody body = FlightGlobals.GetHomeBody();
            if (body == null || body.rotationPeriod <= 0.0)
            {
                InGameAssert.Skip("no home body / degenerate rotation period");
                return;
            }
            double trot = body.rotationPeriod;

            // The production offset for theta (the value the descent trigger congruence shifts by).
            double offset = Parsek.Reaim.ArrivalRestitch.SiteAlignOffsetSeconds(ThetaDeg, trot);
            InGameAssert.ApproxEqual(ThetaDeg / 360.0 * trot, offset, 1e-6 * trot,
                "SiteAlignOffsetSeconds must be the proportional rotation wait");

            // --- Rotation 1: the LAN advance in WORLD frame (KSP's own Orbit math). Two circular
            // equatorial orbits differing only by LAN = theta, same UT: the world-frame angle from
            // the first position to the second IS the world rotation the S4 assembler applies.
            double now = Planetarium.GetUniversalTime();
            double sma = body.Radius * 2.0;
            Orbit lan0 = new Orbit(0.0, 0.0, sma, 0.0, 0.0, 0.0, now, body);
            Orbit lanT = new Orbit(0.0, 0.0, sma, ThetaDeg, 0.0, 0.0, now, body);
            // World offsets: vessel world pos = body pos + getRelativePositionAtUT(ut).xzy.
            Vector3d w0 = lan0.getRelativePositionAtUT(now).xzy;
            Vector3d wT = lanT.getRelativePositionAtUT(now).xzy;

            // --- Rotation 2: the body spin over `offset` seconds (KSP's own angular velocity).
            Vector3d spin = body.angularVelocity;
            if (spin.magnitude <= 0.0)
            {
                InGameAssert.Skip("body reports zero angular velocity");
                return;
            }
            Vector3d axis = spin.normalized;
            Vector3d site0 = body.GetWorldSurfacePosition(SiteLatDeg, SiteLonDeg, 0.0) - body.position;
            double spinRad = spin.magnitude * offset;
            // Rodrigues rotation of the site offset about the body's own spin axis (right-hand sense
            // of angularVelocity - KSP's spin truth), by the spin swept over the S4 offset.
            Vector3d siteAtTrigger = site0 * Math.Cos(spinRad)
                + Vector3d.Cross(axis, site0) * Math.Sin(spinRad)
                + axis * (Vector3d.Dot(axis, site0) * (1.0 - Math.Cos(spinRad)));

            // Signed advances about the SAME axis: if the production pairing is correct they match
            // (same sense, same magnitude); a sign error reads as advance vs -advance (2*theta apart).
            double lanAdvanceDeg = SignedAngleAboutAxis(w0, wT, axis);
            double siteAdvanceDeg = SignedAngleAboutAxis(site0, siteAtTrigger, axis);

            // Coincidence distance at the parking radius (reporting: what the seam would look like).
            double residualDeg = Math.Abs(lanAdvanceDeg - siteAdvanceDeg);
            double coincidenceMeters = 2.0 * sma * Math.Sin(residualDeg * Math.PI / 360.0);
            ParsekLog.Info("TestRunner", string.Format(ic,
                "S4 landing coincidence: theta={0:F2}deg offset={1:F1}s Trot={2:F1}s "
                + "lanAdvance={3:F4}deg siteAdvance={4:F4}deg residual={5:F4}deg (~{6:F0} m at r={7:F0} m) "
                + "spinAxis=({8:F3},{9:F3},{10:F3})",
                ThetaDeg, offset, trot, lanAdvanceDeg, siteAdvanceDeg, residualDeg, coincidenceMeters,
                sma, axis.x, axis.y, axis.z));

            InGameAssert.ApproxEqual(ThetaDeg, Math.Abs(lanAdvanceDeg), 0.05,
                "the LAN advance magnitude must be theta");
            InGameAssert.ApproxEqual(lanAdvanceDeg, siteAdvanceDeg, 0.05,
                "the LAN-advance rotation and the body-spin rotation over the S4 offset must share "
                + "axis AND sense: the rotated deorbit point meets the recorded site at the trigger "
                + "(a sign error here would land the handoff 2*theta apart)");

            // And the trigger congruence itself (the production entry point) honors the offset.
            double entry = now + 123.0;
            double deorbit = now - 5.0 * trot - 100.0;
            double trigger = Parsek.Reaim.DescentTrigger.ComputeRotationAlignedTriggerUT(
                entry, deorbit, trot, offset);
            double congruence = (((trigger - deorbit - offset) % trot) + trot) % trot;
            InGameAssert.IsTrue(Math.Min(congruence, trot - congruence) < 1e-3,
                "the trigger must be congruent to deorbit + offset (mod T_rot)");

            // FRAME VALIDATION of the theta computation itself (the class of bug the headless suite
            // cannot see: it encodes the same frame convention as the helper). Feed the pure
            // ComputeRestitchRotationDeg exactly the vector class the resolver feeds it -
            // .xzy-unswizzled body-relative positions from live KSP Orbits - for two orbits differing
            // ONLY by LAN = +ThetaDeg, and require it to read back +ThetaDeg. A wrong pole (the
            // PR #1196 .z-vs-.y trap) reads ~0 / ~180 / NaN here instead.
            Vector3d entryLikeA = lan0.getRelativePositionAtUT(now).xzy;
            Vector3d entryLikeB = lanT.getRelativePositionAtUT(now).xzy;
            double measuredTheta = Parsek.Reaim.ArrivalRestitch.ComputeRestitchRotationDeg(
                entryLikeA, entryLikeB, out double latA, out double latB);
            ParsekLog.Info("TestRunner", string.Format(ic,
                "S4 frame validation: ComputeRestitchRotationDeg on live LAN+{0:F1} orbit pair = {1:F4}deg "
                + "(latA={2:F3} latB={3:F3})", ThetaDeg, measuredTheta, latA, latB));
            InGameAssert.IsFalse(double.IsNaN(measuredTheta),
                "the pure re-stitch rotation must accept live equatorial entry directions (a NaN here "
                + "means the latitude gate is reading the wrong axis - the .z-vs-.y frame trap)");
            InGameAssert.ApproxEqual(ThetaDeg, measuredTheta, 0.05,
                "the pure re-stitch rotation must read a live LAN=+theta orbit pair as +theta "
                + "(same frame and sense as the production resolver inputs)");
            InGameAssert.IsTrue(Math.Abs(latA) < 1.0 && Math.Abs(latB) < 1.0,
                "equatorial entry directions must read near-zero latitude (a large value here means "
                + "the pole axis is wrong)");
        }

        private static double SignedAngleAboutAxis(Vector3d from, Vector3d to, Vector3d axis)
        {
            Vector3d f = Vector3d.Exclude(axis, from).normalized;
            Vector3d t = Vector3d.Exclude(axis, to).normalized;
            double dot = Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(f, t)));
            double ang = Math.Acos(dot) * 180.0 / Math.PI;
            double sign = Vector3d.Dot(Vector3d.Cross(f, t), axis) >= 0.0 ? 1.0 : -1.0;
            return ang * sign;
        }
    }
}
