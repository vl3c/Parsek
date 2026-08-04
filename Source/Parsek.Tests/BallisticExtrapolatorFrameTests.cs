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
    /// No shared static state is touched (the Kepler core neither logs nor reads statics),
    /// so these tests deliberately carry no <c>[Collection("Sequential")]</c>.
    /// </para>
    /// </summary>
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
