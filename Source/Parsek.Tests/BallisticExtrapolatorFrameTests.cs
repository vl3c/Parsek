using System;
using Xunit;
using TwoBodyOrbit = Parsek.BallisticExtrapolator.TwoBodyOrbit;

namespace Parsek.Tests
{
    /// <summary>
    /// Closed-form FRAME pins on the Kepler core inside <see cref="BallisticExtrapolator"/>:
    /// the element-to-inertial map that <c>TwoBodyOrbit.GetStateAtUT</c> applies, stated as
    /// geometry rather than as pinned implementation numbers.
    /// <para>
    /// These are the headless half of the frame-calibration instrument for
    /// docs/dev/todo-and-known-bugs.md "BallisticExtrapolator frame mismatches". THEY PIN
    /// THE CONTRACT, NOT THE DEFECT: every statement here holds for a correct
    /// element-to-state map, so all of them pass before the four pinned finalizer sites are
    /// touched and must keep passing after. Nothing in this file asserts the current
    /// (wrong) behaviour of any consumer site, and nothing here depends on the swizzle
    /// convention the in-game <c>IncompleteBallistic</c> frame probes exist to measure -
    /// that measurement is not derivable on paper, which is precisely why it is not here.
    /// </para>
    /// <para>
    /// Orbits are built through <see cref="TwoBodyOrbit.TryCreateFromSegment"/>, the
    /// production degrees-in entry point the finalizer reaches via <c>TryPropagate</c>, so
    /// an exact <c>inclination = 0</c> stays exactly zero through the degrees-to-radians
    /// conversion instead of arriving as a 1e-17 residue from a state-vector round trip.
    /// Tolerances are RELATIVE to the quantity compared, matching
    /// <see cref="BallisticExtrapolatorKeplerTests"/>.
    /// </para>
    /// <para>
    /// SINCE THE 2026-08-04_2142 CALIBRATION the file also carries the SITE-1 cells at the
    /// bottom, which DO encode the swizzle convention - it is measured now, not assumed, and
    /// one of them reproduces the in-game probe's own numbers closed form. Everything above
    /// them is unchanged and still convention-free.
    /// </para>
    /// <para>
    /// SHARED STATIC STATE, since Finding A (2026-08-05, branch <c>twobody-element-frame</c>):
    /// <c>TryCreateFromSegment</c> reads the process-wide <c>Planetarium.Zup</c> to shift a
    /// segment's KSP-native LAN into stock <c>Orbit</c>'s state frame (see
    /// <c>BallisticExtrapolator.GetStockElementFrameZupAngleRadians</c>). Nothing here installs a
    /// frame - the headless default is declined and the boundary is the identity, which is what
    /// keeps every closed-form statement below convention-free - but a sibling class that DOES
    /// install one must not run concurrently with these cells, hence
    /// <c>[Collection("Sequential")]</c>.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class BallisticExtrapolatorFrameTests
    {
        private const double KerbinMu = 3.5316e12;

        // ------------------------------------------------------------------
        // Inclination 0: the orbit plane IS the xy-plane
        // ------------------------------------------------------------------

        /// <summary>
        /// The defining property of the reference plane. <c>RotateFromPerifocal</c> builds
        /// the z component as <c>sin(argPe) sin(inc) x + cos(argPe) sin(inc) y</c>, so at
        /// <c>inc == 0</c> both coefficients are EXACTLY zero for every LAN / argPe /
        /// eccentricity / epoch — hence the exact comparison rather than a tolerance. If a
        /// future rewrite of the rotation fuses the terms differently and leaks a residue,
        /// this is the cell that says so.
        /// </summary>
        [Theory]
        [InlineData(700000.0, 0.0, 0.0, 0.0)]
        [InlineData(700000.0, 0.0, 40.0, 0.0)]
        [InlineData(900000.0, 0.3, 128.0, 77.0)]
        [InlineData(2500000.0, 0.85, 311.0, 249.0)]
        public void ZeroInclination_PositionAndVelocityStayExactlyInTheXYPlane(
            double semiMajorAxis, double eccentricity, double lanDegrees, double argPeDegrees)
        {
            TwoBodyOrbit orbit = BuildOrbit(
                semiMajorAxis, eccentricity, inclinationDegrees: 0.0,
                lanDegrees: lanDegrees, argPeDegrees: argPeDegrees,
                meanAnomalyAtEpoch: 0.4, epoch: 1000.0);

            for (int i = 0; i <= 32; i++)
            {
                double ut = orbit.Epoch + orbit.Period * (i / 32.0);
                orbit.GetStateAtUT(ut, out Vector3d position, out Vector3d velocity);

                Assert.True(position.z == 0.0,
                    $"equatorial position left the xy-plane at sample {i}: z={Format(position.z)}");
                Assert.True(velocity.z == 0.0,
                    $"equatorial velocity left the xy-plane at sample {i}: z={Format(velocity.z)}");

                // Anti-vacuity: an all-zero state would satisfy the two assertions above.
                Assert.True(Mag(position) > 0.0 && Mag(velocity) > 0.0,
                    $"degenerate state at sample {i}: |r|={Format(Mag(position))} |v|={Format(Mag(velocity))}");
            }
        }

        /// <summary>
        /// The same statement read off the angular momentum: an equatorial orbit's normal is
        /// the z axis itself, and PROGRADE (h.z &gt; 0) rather than merely parallel to it.
        /// </summary>
        [Theory]
        [InlineData(700000.0, 0.0)]
        [InlineData(900000.0, 0.3)]
        [InlineData(2500000.0, 0.85)]
        public void ZeroInclination_OrbitNormalIsExactlyThePositiveZAxis(
            double semiMajorAxis, double eccentricity)
        {
            TwoBodyOrbit orbit = BuildOrbit(
                semiMajorAxis, eccentricity, inclinationDegrees: 0.0,
                lanDegrees: 128.0, argPeDegrees: 77.0,
                meanAnomalyAtEpoch: 1.9, epoch: 500.0);

            for (int i = 0; i <= 16; i++)
            {
                double ut = orbit.Epoch + orbit.Period * (i / 16.0);
                orbit.GetStateAtUT(ut, out Vector3d position, out Vector3d velocity);

                Vector3d normal = Cross(position, velocity);
                double magnitude = Mag(normal);
                Assert.True(magnitude > 0.0, $"degenerate angular momentum at sample {i}");

                Assert.True(normal.x == 0.0 && normal.y == 0.0,
                    $"equatorial orbit normal left the z axis at sample {i}: "
                    + $"({Format(normal.x)}, {Format(normal.y)}, {Format(normal.z)})");
                Assert.True(normal.z > 0.0,
                    $"equatorial orbit normal is retrograde at sample {i}: z={Format(normal.z)}");
            }
        }

        // ------------------------------------------------------------------
        // Inclination 90: the orbit plane CONTAINS the z axis
        // ------------------------------------------------------------------

        /// <summary>
        /// A polar orbit's plane contains the z axis, so its normal is perpendicular to z —
        /// i.e. the normal lies IN the xy-plane. Stated as a relative bound rather than an
        /// exact zero because <c>90 * DegToRad</c> is not exactly pi/2 in binary, so
        /// <c>cos(inc)</c> is a ~6e-17 residue rather than 0.
        /// </summary>
        [Theory]
        [InlineData(700000.0, 0.0, 0.0, 0.0)]
        [InlineData(700000.0, 0.0, 40.0, 0.0)]
        [InlineData(900000.0, 0.3, 128.0, 77.0)]
        [InlineData(2500000.0, 0.85, 311.0, 249.0)]
        public void NinetyDegreeInclination_OrbitNormalLiesInTheXYPlane(
            double semiMajorAxis, double eccentricity, double lanDegrees, double argPeDegrees)
        {
            TwoBodyOrbit orbit = BuildOrbit(
                semiMajorAxis, eccentricity, inclinationDegrees: 90.0,
                lanDegrees: lanDegrees, argPeDegrees: argPeDegrees,
                meanAnomalyAtEpoch: 0.4, epoch: 1000.0);

            for (int i = 0; i <= 32; i++)
            {
                double ut = orbit.Epoch + orbit.Period * (i / 32.0);
                orbit.GetStateAtUT(ut, out Vector3d position, out Vector3d velocity);

                Vector3d normal = Cross(position, velocity);
                double magnitude = Mag(normal);
                Assert.True(magnitude > 0.0, $"degenerate angular momentum at sample {i}");

                double outOfPlane = Math.Abs(normal.z) / magnitude;
                Assert.True(outOfPlane <= 1e-12,
                    $"polar orbit normal has a z component at sample {i}: "
                    + $"|h.z|/|h|={Format(outOfPlane)} > 1e-12");

                // Textbook closed form for the angular-momentum direction,
                // h / |h| = (sin i sin LAN, -sin i cos LAN, cos i), evaluated at i = 90.
                // Asserting the DIRECTION rather than restating "z is small" is what makes
                // this cell discriminating: a normal that drifted within the xy-plane would
                // still satisfy the out-of-plane bound above.
                double lan = lanDegrees * (Math.PI / 180.0);
                double alignment =
                    ((normal.x * Math.Sin(lan)) - (normal.y * Math.Cos(lan))) / magnitude;
                Assert.True(Math.Abs(alignment - 1.0) <= 1e-12,
                    $"polar orbit normal is not along (sin LAN, -cos LAN, 0) at sample {i}: "
                    + $"alignment={Format(alignment)}");
            }
        }

        /// <summary>
        /// The dual statement in position space: a polar orbit sweeps the full z range, so
        /// the plane genuinely contains the z axis instead of merely being steeply tilted.
        /// Without this, the normal cells above would also pass for an orbit whose plane is
        /// perpendicular to the equator but whose motion never leaves it.
        /// </summary>
        [Theory]
        [InlineData(700000.0, 0.0)]
        [InlineData(900000.0, 0.3)]
        public void NinetyDegreeInclination_PositionSweepsTheFullZExtent(
            double semiMajorAxis, double eccentricity)
        {
            TwoBodyOrbit orbit = BuildOrbit(
                semiMajorAxis, eccentricity, inclinationDegrees: 90.0,
                lanDegrees: 128.0, argPeDegrees: 0.0,
                meanAnomalyAtEpoch: 0.0, epoch: 0.0);

            double maxZ = double.NegativeInfinity;
            double minZ = double.PositiveInfinity;
            double maxRadius = 0.0;
            for (int i = 0; i <= 64; i++)
            {
                double ut = orbit.Epoch + orbit.Period * (i / 64.0);
                Vector3d position = orbit.GetPositionAtUT(ut);
                if (position.z > maxZ) maxZ = position.z;
                if (position.z < minZ) minZ = position.z;
                double radius = Mag(position);
                if (radius > maxRadius) maxRadius = radius;
            }

            // With argPe = 0 the periapsis sits on the node line (z = 0) and apoapsis is
            // half a revolution later, also on it; the extremes in z are the quarter points,
            // so the sweep is a large fraction of the orbit's own scale either way.
            Assert.True(maxZ > 0.25 * maxRadius,
                $"polar orbit never climbed above the equator: maxZ={Format(maxZ)} "
                + $"vs maxRadius={Format(maxRadius)}");
            Assert.True(minZ < -0.25 * maxRadius,
                $"polar orbit never dropped below the equator: minZ={Format(minZ)} "
                + $"vs maxRadius={Format(maxRadius)}");
        }

        // ------------------------------------------------------------------
        // SITE 1: the CALIBRATED Zup <-> KSP-world axis map
        //
        // These cells arrived with the fix (measurement run 2026-08-04_2142) and, unlike
        // everything above, they DO encode the swizzle convention - because it is now
        // measured rather than assumed. They are pure: the production call site
        // (ResolveBodyFixedSurfaceCoordinates) needs a live CelestialBody, so the seam
        // pinned here is the map itself plus the two GetApproximateLatitudeLongitude
        // copies that read the same frame.
        // ------------------------------------------------------------------

        /// <summary>
        /// The map: y and z swap, x is untouched — and, being a transposition, it is its own
        /// inverse, which is why one helper serves both directions.
        /// </summary>
        [Theory]
        [InlineData(1.0, 2.0, 3.0)]
        [InlineData(-496283.195, 337326.756, -1018.081)]
        [InlineData(0.0, 700000.0, 0.0)]
        public void SwizzleZupBodyRelativeToWorld_SwapsYAndZAndIsItsOwnInverse(
            double x, double y, double z)
        {
            var zup = new Vector3d(x, y, z);

            Vector3d world = IncompleteBallisticSceneExitFinalizer.SwizzleZupBodyRelativeToWorld(zup);

            Assert.Equal(zup.x, world.x);
            Assert.Equal(zup.z, world.y);
            Assert.Equal(zup.y, world.z);

            Vector3d roundTrip = IncompleteBallisticSceneExitFinalizer.SwizzleZupBodyRelativeToWorld(world);
            Assert.Equal(zup.x, roundTrip.x);
            Assert.Equal(zup.y, roundTrip.y);
            Assert.Equal(zup.z, roundTrip.z);
        }

        /// <summary>
        /// The identity the site-1 fix rests on: reading the SWIZZLED vector with the world
        /// frame's polar axis (y) gives the same geodetic latitude as reading the raw Zup
        /// vector with the extrapolator's polar axis (z) — i.e. after the unswizzle,
        /// <c>CelestialBody.GetLatitude</c> and the extrapolator's own
        /// <c>GetApproximateLatitudeLongitude</c> are asking the same question.
        /// <para>
        /// The longitude half is asserted as the SWIZZLE's own identity
        /// (<c>atan2(world.z, world.x) == atan2(zup.y, zup.x)</c>), NOT as a claim about
        /// <c>CelestialBody.GetLongitude</c>, whose zero meridian and per-UT body rotation
        /// the finalizer de-rotates separately.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(-496283.195, 337326.756, -1018.081)]
        [InlineData(700000.0, 0.0, 0.0)]
        [InlineData(120000.0, -350000.0, 480000.0)]
        [InlineData(-30000.0, 20000.0, -690000.0)]
        public void Site1_WorldFrameLatitudeOfTheSwizzledVectorEqualsTheZupLatitude(
            double x, double y, double z)
        {
            var zup = new Vector3d(x, y, z);
            Vector3d world = IncompleteBallisticSceneExitFinalizer.SwizzleZupBodyRelativeToWorld(zup);

            BallisticExtrapolator.GetApproximateLatitudeLongitude(
                zup, out double zupLatitude, out double zupLongitude);

            double radius = Mag(world);
            double worldLatitude = Math.Asin(world.y / radius) * (180.0 / Math.PI);
            double worldLongitude = Math.Atan2(world.z, world.x) * (180.0 / Math.PI);

            Assert.True(Math.Abs(worldLatitude - zupLatitude) <= 1e-12,
                $"world-frame latitude {Format(worldLatitude)} != Zup latitude {Format(zupLatitude)}");
            Assert.True(Math.Abs(worldLongitude - zupLongitude) <= 1e-12,
                $"world-frame longitude {Format(worldLongitude)} != Zup longitude {Format(zupLongitude)}");
        }

        /// <summary>
        /// The two <c>GetApproximateLatitudeLongitude</c> copies — one in
        /// <see cref="BallisticExtrapolator"/>, one in
        /// <see cref="IncompleteBallisticSceneExitFinalizer"/> (the catch-path fallback of
        /// the fixed site-1 resolver) — must agree with each other, and therefore with the
        /// calibrated site-1 reading pinned above. They are separate literal copies, so
        /// nothing but a test keeps them in step.
        /// </summary>
        [Theory]
        [InlineData(-496283.195, 337326.756, -1018.081)]
        [InlineData(700000.0, 0.0, 0.0)]
        [InlineData(120000.0, -350000.0, 480000.0)]
        [InlineData(-30000.0, 20000.0, -690000.0)]
        public void TheTwoApproximateLatLonResolversAgreeOnTheSameVector(
            double x, double y, double z)
        {
            var zup = new Vector3d(x, y, z);

            BallisticExtrapolator.GetApproximateLatitudeLongitude(
                zup, out double extrapolatorLatitude, out double extrapolatorLongitude);
            IncompleteBallisticSceneExitFinalizer.GetApproximateLatitudeLongitude(
                zup, out double finalizerLatitude, out double finalizerLongitude);

            Assert.True(Math.Abs(extrapolatorLatitude - finalizerLatitude) <= 1e-12,
                $"latitude copies disagree: {Format(extrapolatorLatitude)} vs {Format(finalizerLatitude)}");
            Assert.True(Math.Abs(extrapolatorLongitude - finalizerLongitude) <= 1e-12,
                $"longitude copies disagree: {Format(extrapolatorLongitude)} vs {Format(finalizerLongitude)}");
        }

        /// <summary>
        /// THE MEASUREMENT ITSELF, reproduced closed form. The in-game site-1 probe (run
        /// <c>2026-08-04_2142</c>) reported, for a live vessel whose true latitude was
        /// <c>-0.097208</c>:
        /// <code>
        /// Site1FrameProbe: measured lat=34.204133 ... vesselLat=-0.097208 ... zup=(-496283.195,337326.756,-1018.081)
        /// </code>
        /// Both numbers fall out of the same vector read on two different polar axes: the
        /// y-polar (unswizzled, i.e. the DEFECT) reading is the 34.204133 that was measured,
        /// and the z-polar (calibrated) reading is the vessel's own latitude. That is the
        /// whole calibration, and it is why <c>.xzy</c> is not a guess. A future change that
        /// flips the convention back makes this cell print the two readings side by side.
        /// </summary>
        [Fact]
        public void Site1_TheInGameProbeReadingIsReproducedByTheTwoPolarAxisReadings()
        {
            var measuredZup = new Vector3d(-496283.195, 337326.756, -1018.081);
            const double measuredWrongLatitude = 34.204133;
            const double measuredVesselLatitude = -0.097208;

            double radius = Mag(measuredZup);
            double yPolarLatitude = Math.Asin(measuredZup.y / radius) * (180.0 / Math.PI);

            BallisticExtrapolator.GetApproximateLatitudeLongitude(
                measuredZup, out double zPolarLatitude, out _);

            Assert.True(Math.Abs(yPolarLatitude - measuredWrongLatitude) < 1e-4,
                $"the y-polar (pre-fix) reading no longer reproduces the measured probe latitude: "
                + $"{Format(yPolarLatitude)} vs measured {Format(measuredWrongLatitude)}");
            Assert.True(Math.Abs(zPolarLatitude - measuredVesselLatitude) < 1e-4,
                $"the z-polar (calibrated) reading no longer reproduces the vessel's true latitude: "
                + $"{Format(zPolarLatitude)} vs measured {Format(measuredVesselLatitude)}");

            // Anti-vacuity: the two readings are the axis swap, not two ways of saying the
            // same thing. 34 degrees of latitude error is what shipped before the fix.
            Assert.True(Math.Abs(yPolarLatitude - zPolarLatitude) > 30.0,
                "the two polar-axis readings collapsed together; this fixture no longer "
                + "discriminates the axis swap it exists to pin");
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static TwoBodyOrbit BuildOrbit(
            double semiMajorAxis,
            double eccentricity,
            double inclinationDegrees,
            double lanDegrees,
            double argPeDegrees,
            double meanAnomalyAtEpoch,
            double epoch)
        {
            var segment = new OrbitSegment
            {
                startUT = epoch,
                endUT = epoch + 1.0,
                bodyName = "Kerbin",
                semiMajorAxis = semiMajorAxis,
                eccentricity = eccentricity,
                inclination = inclinationDegrees,
                longitudeOfAscendingNode = lanDegrees,
                argumentOfPeriapsis = argPeDegrees,
                meanAnomalyAtEpoch = meanAnomalyAtEpoch,
                epoch = epoch,
            };

            Assert.True(
                TwoBodyOrbit.TryCreateFromSegment(segment, KerbinMu, out TwoBodyOrbit orbit),
                "TryCreateFromSegment rejected a well-formed element set");
            return orbit;
        }

        private static string Format(double value)
        {
            return value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static double Mag(Vector3d v)
        {
            return Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        }

        private static Vector3d Cross(Vector3d a, Vector3d b)
        {
            return new Vector3d(
                a.y * b.z - a.z * b.y,
                a.z * b.x - a.x * b.z,
                a.x * b.y - a.y * b.x);
        }
    }
}
