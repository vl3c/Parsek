using System;
using Xunit;
using TwoBodyOrbit = Parsek.BallisticExtrapolator.TwoBodyOrbit;

namespace Parsek.Tests
{
    /// <summary>
    /// Closed-form validation of the Kepler core inside <see cref="BallisticExtrapolator"/>
    /// (the nested <c>TwoBodyOrbit</c> struct: element recovery from a state vector,
    /// mean motion, Kepler-equation propagation, and the perifocal-to-inertial rotation).
    /// <para>
    /// Every assertion here compares against textbook two-body closed form rather than
    /// against a pinned number produced by the implementation itself, and every tolerance
    /// is RELATIVE to the quantity being compared so the contract holds at any orbit scale.
    /// </para>
    /// <para>
    /// Orbits are built independently of the product code: a perifocal state vector is
    /// rotated into the inertial frame by three elementary rotations (Rz(argPe), Rx(inc),
    /// Rz(lan)) written out separately here, so recovering the elements through
    /// <c>TryCreate</c> and propagating back through <c>GetStateAtUT</c> genuinely
    /// cross-checks the fused rotation matrix the implementation uses.
    /// </para>
    /// <para>
    /// No shared static state is touched (the Kepler core neither logs nor reads statics),
    /// so these tests deliberately carry no <c>[Collection("Sequential")]</c>.
    /// </para>
    /// </summary>
    public class BallisticExtrapolatorKeplerTests
    {
        // Stock Kerbin / Sun-scale gravitational parameters, used only to keep the
        // numbers in a realistic regime; nothing below depends on the exact values.
        private const double KerbinMu = 3.5316e12;
        private const double KerbinRadius = 600000.0;
        private const double SunMu = 1.1723328e18;

        private const double TwoPi = Math.PI * 2.0;

        // ------------------------------------------------------------------
        // Circular orbits
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(700000.0, 0.0, 0.0, 0.0)]
        [InlineData(2500000.0, 0.0, 0.0, 0.0)]
        [InlineData(900000.0, 0.6, 1.1, 0.4)]
        [InlineData(1200000.0, Math.PI / 2.0, 2.7, 5.9)]
        public void CircularOrbit_RadiusIsConstantOverAFullPeriod(
            double a, double inclination, double lan, double argPe)
        {
            TwoBodyOrbit orbit = BuildOrbit(a, 0.0, inclination, lan, argPe, trueAnomaly: 0.0, epoch: 1000.0, mu: KerbinMu);

            for (int i = 0; i <= 32; i++)
            {
                double ut = orbit.Epoch + orbit.Period * (i / 32.0);
                double radius = Mag(orbit.GetPositionAtUT(ut));
                AssertRelative(a, radius, 1e-9, $"radius at sample {i}");
            }
        }

        [Theory]
        [InlineData(700000.0, KerbinMu)]
        [InlineData(13599840256.0, SunMu)]
        public void CircularOrbit_PeriodMatchesClosedForm(double a, double mu)
        {
            TwoBodyOrbit orbit = BuildOrbit(a, 0.0, 0.0, 0.0, 0.0, trueAnomaly: 0.0, epoch: 0.0, mu: mu);

            double expected = TwoPi * Math.Sqrt((a * a * a) / mu);
            AssertRelative(expected, orbit.Period, 1e-12, "period");
        }

        [Theory]
        [InlineData(700000.0, KerbinMu)]
        [InlineData(13599840256.0, SunMu)]
        public void CircularOrbit_MeanMotionMatchesClosedForm(double a, double mu)
        {
            TwoBodyOrbit orbit = BuildOrbit(a, 0.0, 0.0, 0.0, 0.0, trueAnomaly: 0.0, epoch: 0.0, mu: mu);

            double expected = Math.Sqrt(mu / (a * a * a));
            AssertRelative(expected, orbit.GetMeanMotion(), 1e-12, "mean motion");

            // Mean motion and period are the same statement twice; they must agree.
            AssertRelative(TwoPi / orbit.Period, orbit.GetMeanMotion(), 1e-12, "n vs 2pi/P");
        }

        [Theory]
        [InlineData(700000.0, 0.0, 0.0, 0.0)]
        [InlineData(2500000.0, 0.9, 4.2, 1.3)]
        public void CircularOrbit_SpeedMatchesVisVivaAndVelocityIsPerpendicularToRadius(
            double a, double inclination, double lan, double argPe)
        {
            TwoBodyOrbit orbit = BuildOrbit(a, 0.0, inclination, lan, argPe, trueAnomaly: 0.0, epoch: 500.0, mu: KerbinMu);

            double expectedSpeed = Math.Sqrt(KerbinMu / a);
            for (int i = 0; i <= 24; i++)
            {
                double ut = orbit.Epoch + orbit.Period * (i / 24.0);
                orbit.GetStateAtUT(ut, out Vector3d position, out Vector3d velocity);

                AssertRelative(expectedSpeed, Mag(velocity), 1e-9, $"speed at sample {i}");

                // cos(angle between r and v) must be zero on a circle.
                double cosAngle = Dot(position, velocity) / (Mag(position) * Mag(velocity));
                Assert.True(
                    Math.Abs(cosAngle) < 1e-9,
                    $"r.v not perpendicular at sample {i}: cos={cosAngle}");
            }
        }

        // ------------------------------------------------------------------
        // Eccentric orbits
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(0.001)]
        [InlineData(0.05)]
        [InlineData(0.3)]
        [InlineData(0.7)]
        [InlineData(0.9)]
        public void EccentricOrbit_ApsisRadiiMatchClosedForm(double e)
        {
            const double a = 4000000.0;
            TwoBodyOrbit orbit = BuildOrbit(a, e, 0.35, 1.9, 2.4, trueAnomaly: 0.0, epoch: 250.0, mu: KerbinMu);

            AssertRelative(a * (1.0 - e), orbit.PeriapsisRadius, 1e-9, "PeriapsisRadius field");
            AssertRelative(a * (1.0 + e), orbit.ApoapsisRadius, 1e-9, "ApoapsisRadius field");

            // The orbit was constructed at periapsis, so M = 0 at the epoch and
            // M = pi half a period later.
            double periapsisRadius = Mag(orbit.GetPositionAtUT(orbit.Epoch));
            double apoapsisRadius = Mag(orbit.GetPositionAtUT(orbit.Epoch + orbit.Period * 0.5));

            AssertRelative(a * (1.0 - e), periapsisRadius, 1e-9, "propagated periapsis radius");
            AssertRelative(a * (1.0 + e), apoapsisRadius, 1e-9, "propagated apoapsis radius");
        }

        [Theory]
        [InlineData(0.001)]
        [InlineData(0.3)]
        [InlineData(0.7)]
        [InlineData(0.9)]
        public void EccentricOrbit_MeanAnomalyAdvancesLinearlyWithTime(double e)
        {
            const double a = 4000000.0;
            TwoBodyOrbit orbit = BuildOrbit(a, e, 0.35, 1.9, 2.4, trueAnomaly: 1.2, epoch: 250.0, mu: KerbinMu);

            double n = orbit.GetMeanMotion();
            double m0 = orbit.GetMeanAnomalyAtUT(orbit.Epoch);

            // Sweep well past a full period so the wrap in NormalizeAngle is exercised.
            for (int i = 1; i <= 40; i++)
            {
                double dt = orbit.Period * (i / 10.0);
                double expected = NormalizeAngle(m0 + n * dt);
                double actual = orbit.GetMeanAnomalyAtUT(orbit.Epoch + dt);

                Assert.True(
                    AngleDifference(expected, actual) < 1e-8,
                    $"M not linear in t at step {i}: expected {expected}, got {actual}");
            }

            // And backwards in time, where the raw sum goes negative before normalizing.
            double back = orbit.GetMeanAnomalyAtUT(orbit.Epoch - orbit.Period * 0.25);
            Assert.True(
                AngleDifference(NormalizeAngle(m0 - n * orbit.Period * 0.25), back) < 1e-8,
                $"M not linear backwards in t: got {back}");
            Assert.InRange(back, 0.0, TwoPi);
        }

        [Theory]
        [InlineData(0.001)]
        [InlineData(0.05)]
        [InlineData(0.3)]
        [InlineData(0.7)]
        [InlineData(0.9)]
        public void EccentricOrbit_KeplerEquationSolutionIsSelfConsistent(double e)
        {
            const double a = 4000000.0;
            TwoBodyOrbit orbit = BuildOrbit(a, e, 0.35, 1.9, 2.4, trueAnomaly: 0.0, epoch: 250.0, mu: KerbinMu);

            // Sample away from the apsides, where recovering E from the radius alone
            // is ill-conditioned (cos E is stationary there).
            for (int i = 1; i < 24; i++)
            {
                double fraction = i / 24.0;
                if (Math.Abs(fraction - 0.5) < 1e-12)
                    continue;

                double ut = orbit.Epoch + orbit.Period * fraction;
                orbit.GetStateAtUT(ut, out Vector3d position, out Vector3d velocity);

                // r = a(1 - e cos E) inverted for E, with the branch chosen by the
                // sign of the radial rate: E in (0, pi) while climbing to apoapsis.
                double radius = Mag(position);
                double cosE = Clamp((1.0 - radius / a) / e, -1.0, 1.0);
                double eccentricAnomaly = Math.Acos(cosE);
                if (Dot(position, velocity) < 0.0)
                    eccentricAnomaly = TwoPi - eccentricAnomaly;

                double meanFromKepler = NormalizeAngle(eccentricAnomaly - e * Math.Sin(eccentricAnomaly));
                double meanFromOrbit = orbit.GetMeanAnomalyAtUT(ut);

                Assert.True(
                    AngleDifference(meanFromKepler, meanFromOrbit) < 1e-7,
                    $"M = E - e sin E violated at fraction {fraction.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}: "
                    + $"Kepler {meanFromKepler}, orbit {meanFromOrbit}");
            }
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(0.001)]
        [InlineData(0.3)]
        [InlineData(0.7)]
        [InlineData(0.9)]
        public void EccentricOrbit_EnergyAndAngularMomentumAreConservedOverOnePeriod(double e)
        {
            const double a = 4000000.0;
            TwoBodyOrbit orbit = BuildOrbit(a, e, 0.35, 1.9, 2.4, trueAnomaly: 0.9, epoch: 250.0, mu: KerbinMu);

            double expectedEnergy = -KerbinMu / (2.0 * a);
            double expectedMomentum = Math.Sqrt(KerbinMu * a * (1.0 - e * e));

            for (int i = 0; i <= 32; i++)
            {
                double ut = orbit.Epoch + orbit.Period * (i / 32.0);
                orbit.GetStateAtUT(ut, out Vector3d position, out Vector3d velocity);

                double speedSquared = Dot(velocity, velocity);
                double energy = 0.5 * speedSquared - KerbinMu / Mag(position);
                AssertRelative(expectedEnergy, energy, 1e-8, $"specific energy at sample {i}");

                double momentum = Mag(Cross(position, velocity));
                AssertRelative(expectedMomentum, momentum, 1e-8, $"angular momentum at sample {i}");
            }
        }

        [Theory]
        [InlineData(0.001)]
        [InlineData(0.3)]
        [InlineData(0.7)]
        [InlineData(0.9)]
        public void EccentricOrbit_RadiusMatchesTheConicEquation(double e)
        {
            // Equatorial with argPe = 0 so the true anomaly is simply atan2(y, x)
            // and the conic equation can be checked without re-deriving a frame.
            const double a = 4000000.0;
            TwoBodyOrbit orbit = BuildOrbit(a, e, 0.0, 0.0, 0.0, trueAnomaly: 0.0, epoch: 0.0, mu: KerbinMu);

            double semiLatusRectum = a * (1.0 - e * e);
            for (int i = 0; i <= 32; i++)
            {
                double ut = orbit.Epoch + orbit.Period * (i / 32.0);
                Vector3d position = orbit.GetPositionAtUT(ut);

                double trueAnomaly = Math.Atan2(position.y, position.x);
                double expectedRadius = semiLatusRectum / (1.0 + e * Math.Cos(trueAnomaly));

                AssertRelative(expectedRadius, Mag(position), 1e-8, $"conic radius at sample {i}");
                Assert.True(Math.Abs(position.z) < 1e-6, $"equatorial orbit left the plane: z={position.z}");
            }
        }

        // ------------------------------------------------------------------
        // Element recovery and propagation consistency
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(4000000.0, 0.0, 0.0, 0.0, 0.0, 0.0)]
        [InlineData(4000000.0, 0.35, 0.0, 0.0, 0.0, 1.7)]
        [InlineData(4000000.0, 0.35, 0.6, 2.1, 0.8, 1.7)]
        [InlineData(9000000.0, 0.82, 1.4, 5.5, 3.9, 4.4)]
        [InlineData(9000000.0, 0.82, Math.PI / 2.0, 0.3, 2.2, 0.6)]
        public void TryCreate_RecoversClassicalElementsFromTheStateVector(
            double a, double e, double inclination, double lan, double argPe, double trueAnomaly)
        {
            TwoBodyOrbit orbit = BuildOrbit(a, e, inclination, lan, argPe, trueAnomaly, epoch: 4242.0, mu: KerbinMu);

            AssertRelative(a, orbit.SemiMajorAxis, 1e-9, "semi-major axis");
            Assert.True(Math.Abs(orbit.Eccentricity - e) < 1e-9, $"eccentricity: expected {e}, got {orbit.Eccentricity}");
            Assert.True(
                Math.Abs(orbit.Inclination - inclination) < 1e-9,
                $"inclination: expected {inclination}, got {orbit.Inclination}");
            Assert.True(orbit.IsElliptic);
            Assert.Equal(4242.0, orbit.Epoch);

            // LAN and argPe are only defined when the node line / eccentricity vector
            // exist; the degenerate cases are covered by the geometric assertions below.
            if (inclination > 1e-6)
            {
                Assert.True(
                    AngleDifference(lan, orbit.LongitudeOfAscendingNode) < 1e-8,
                    $"LAN: expected {lan}, got {orbit.LongitudeOfAscendingNode}");
            }

            if (e > 1e-6)
            {
                Assert.True(
                    AngleDifference(argPe, orbit.ArgumentOfPeriapsis) < 1e-8,
                    $"argPe: expected {argPe}, got {orbit.ArgumentOfPeriapsis}");
            }
        }

        [Theory]
        [InlineData(4000000.0, 0.0, 0.0, 0.0, 0.0, 0.0)]
        [InlineData(4000000.0, 0.35, 0.6, 2.1, 0.8, 1.7)]
        [InlineData(9000000.0, 0.82, 1.4, 5.5, 3.9, 4.4)]
        [InlineData(9000000.0, 0.82, Math.PI / 2.0, 0.3, 2.2, 0.6)]
        public void GetStateAtUT_AtEpochReproducesTheConstructionStateVector(
            double a, double e, double inclination, double lan, double argPe, double trueAnomaly)
        {
            BuildState(a, e, inclination, lan, argPe, trueAnomaly, KerbinMu,
                out Vector3d expectedPosition, out Vector3d expectedVelocity);

            Assert.True(TwoBodyOrbit.TryCreate(expectedPosition, expectedVelocity, KerbinMu, 4242.0, out TwoBodyOrbit orbit));
            orbit.GetStateAtUT(4242.0, out Vector3d actualPosition, out Vector3d actualVelocity);

            AssertVectorRelative(expectedPosition, actualPosition, 1e-8, "position at epoch");
            AssertVectorRelative(expectedVelocity, actualVelocity, 1e-8, "velocity at epoch");
        }

        [Fact]
        public void GetPositionAtUT_AgreesExactlyWithGetStateAtUT()
        {
            TwoBodyOrbit orbit = BuildOrbit(4000000.0, 0.45, 0.7, 2.2, 1.1, trueAnomaly: 0.5, epoch: 90.0, mu: KerbinMu);

            for (int i = 0; i <= 16; i++)
            {
                double ut = orbit.Epoch + orbit.Period * (i / 16.0);
                orbit.GetStateAtUT(ut, out Vector3d statePosition, out _);
                Vector3d position = orbit.GetPositionAtUT(ut);

                // Same code path, so this must be bit-identical, not merely close.
                Assert.Equal(statePosition.x, position.x);
                Assert.Equal(statePosition.y, position.y);
                Assert.Equal(statePosition.z, position.z);
            }
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(0.3)]
        [InlineData(0.9)]
        public void GetStateAtUT_IsPeriodicWithThePeriod(double e)
        {
            TwoBodyOrbit orbit = BuildOrbit(4000000.0, e, 0.7, 2.2, 1.1, trueAnomaly: 0.5, epoch: 90.0, mu: KerbinMu);

            for (int i = 0; i < 8; i++)
            {
                double ut = orbit.Epoch + orbit.Period * (i / 8.0);
                orbit.GetStateAtUT(ut, out Vector3d position, out Vector3d velocity);
                orbit.GetStateAtUT(ut + orbit.Period, out Vector3d laterPosition, out Vector3d laterVelocity);

                AssertVectorRelative(position, laterPosition, 1e-8, $"position periodicity at sample {i}");
                AssertVectorRelative(velocity, laterVelocity, 1e-8, $"velocity periodicity at sample {i}");
            }
        }

        // ------------------------------------------------------------------
        // Edge behavior the implementation explicitly claims
        // ------------------------------------------------------------------

        [Fact]
        public void NearCircularOrbit_StaysEllipticAndKeepsItsApsisSeparation()
        {
            // e small but comfortably above the implementation's OrbitEpsilon (1e-9),
            // where the eccentricity-vector branch of TryCreate is still taken.
            const double a = 4000000.0;
            const double e = 1e-6;
            TwoBodyOrbit orbit = BuildOrbit(a, e, 0.4, 1.0, 0.0, trueAnomaly: 0.0, epoch: 0.0, mu: KerbinMu);

            Assert.True(orbit.IsElliptic);
            AssertRelative(a * (1.0 - e), orbit.PeriapsisRadius, 1e-9, "near-circular periapsis");
            AssertRelative(a * (1.0 + e), orbit.ApoapsisRadius, 1e-9, "near-circular apoapsis");

            double periapsisRadius = Mag(orbit.GetPositionAtUT(orbit.Epoch));
            double apoapsisRadius = Mag(orbit.GetPositionAtUT(orbit.Epoch + orbit.Period * 0.5));

            // The 2 a e apsis separation must survive the round trip, not be lost to
            // the near-circular degeneracy.
            AssertRelative(2.0 * a * e, apoapsisRadius - periapsisRadius, 1e-5, "apsis separation");
        }

        [Theory]
        [InlineData(1.2)]
        [InlineData(1.5)]
        [InlineData(3.0)]
        public void HyperbolicOrbit_ExposesOpenOrbitElements(double e)
        {
            const double periapsisRadius = 800000.0;
            double a = periapsisRadius / (1.0 - e); // negative for e > 1

            TwoBodyOrbit orbit = BuildOrbit(a, e, 0.5, 1.2, 0.9, trueAnomaly: 0.0, epoch: 1000.0, mu: KerbinMu);

            Assert.False(orbit.IsElliptic);
            Assert.True(orbit.SemiMajorAxis < 0.0, $"hyperbolic sma must be negative, got {orbit.SemiMajorAxis}");
            AssertRelative(a, orbit.SemiMajorAxis, 1e-9, "hyperbolic semi-major axis");
            AssertRelative(periapsisRadius, orbit.PeriapsisRadius, 1e-8, "hyperbolic periapsis radius");
            Assert.True(double.IsPositiveInfinity(orbit.ApoapsisRadius), "hyperbolic apoapsis must be +inf");
            Assert.True(double.IsPositiveInfinity(orbit.Period), "hyperbolic period must be +inf");

            // Mean motion for the open case is sqrt(mu / |a|^3).
            AssertRelative(Math.Sqrt(KerbinMu / (-a * -a * -a)), orbit.GetMeanMotion(), 1e-12, "hyperbolic mean motion");
        }

        [Theory]
        [InlineData(1.2)]
        [InlineData(3.0)]
        public void HyperbolicOrbit_ConservesEnergyAndAngularMomentumAlongTheArc(double e)
        {
            const double periapsisRadius = 800000.0;
            double a = periapsisRadius / (1.0 - e);

            TwoBodyOrbit orbit = BuildOrbit(a, e, 0.5, 1.2, 0.9, trueAnomaly: 0.0, epoch: 1000.0, mu: KerbinMu);

            double expectedEnergy = -KerbinMu / (2.0 * a); // positive on a hyperbola
            Assert.True(expectedEnergy > 0.0);
            double expectedMomentum = Math.Sqrt(KerbinMu * a * (1.0 - e * e));

            double previousRadius = 0.0;
            for (int i = 0; i <= 20; i++)
            {
                double ut = orbit.Epoch + i * 60.0;
                orbit.GetStateAtUT(ut, out Vector3d position, out Vector3d velocity);

                double energy = 0.5 * Dot(velocity, velocity) - KerbinMu / Mag(position);
                AssertRelative(expectedEnergy, energy, 1e-8, $"hyperbolic energy at sample {i}");
                AssertRelative(expectedMomentum, Mag(Cross(position, velocity)), 1e-8, $"hyperbolic momentum at sample {i}");

                double radius = Mag(position);
                Assert.True(radius > previousRadius, $"outbound hyperbola must climb: sample {i} radius {radius}");
                previousRadius = radius;
            }
        }

        [Fact]
        public void HyperbolicOrbit_MeanAnomalyIsUnboundedRatherThanWrapped()
        {
            const double e = 1.5;
            const double periapsisRadius = 800000.0;
            double a = periapsisRadius / (1.0 - e);

            TwoBodyOrbit orbit = BuildOrbit(a, e, 0.5, 1.2, 0.9, trueAnomaly: 0.0, epoch: 1000.0, mu: KerbinMu);

            double n = orbit.GetMeanMotion();
            double m0 = orbit.GetMeanAnomalyAtUT(orbit.Epoch);
            Assert.True(Math.Abs(m0) < 1e-9, $"constructed at periapsis, so M0 must be ~0, got {m0}");

            // Far enough out that a wrapped elliptic-style normalization would fold it.
            double dt = 10.0 * TwoPi / n;
            double late = orbit.GetMeanAnomalyAtUT(orbit.Epoch + dt);
            Assert.True(late > TwoPi, $"hyperbolic M must grow without wrapping, got {late}");
            AssertRelative(m0 + n * dt, late, 1e-12, "hyperbolic mean anomaly");

            double early = orbit.GetMeanAnomalyAtUT(orbit.Epoch - dt);
            Assert.True(early < 0.0, $"hyperbolic M must go negative before periapsis, got {early}");
        }

        [Fact]
        public void HyperbolicOrbit_StateAtEpochReproducesTheConstructionStateVector()
        {
            const double e = 1.5;
            const double periapsisRadius = 800000.0;
            double a = periapsisRadius / (1.0 - e);

            BuildState(a, e, 0.5, 1.2, 0.9, trueAnomaly: 0.4, mu: KerbinMu,
                position: out Vector3d expectedPosition, velocity: out Vector3d expectedVelocity);

            Assert.True(TwoBodyOrbit.TryCreate(expectedPosition, expectedVelocity, KerbinMu, 1000.0, out TwoBodyOrbit orbit));
            orbit.GetStateAtUT(1000.0, out Vector3d actualPosition, out Vector3d actualVelocity);

            AssertVectorRelative(expectedPosition, actualPosition, 1e-8, "hyperbolic position at epoch");
            AssertVectorRelative(expectedVelocity, actualVelocity, 1e-8, "hyperbolic velocity at epoch");
        }

        [Fact]
        public void ParabolicStateVector_IsRejectedRatherThanPropagated()
        {
            // Escape speed exactly: specific energy is zero, the semi-major axis is
            // undefined, and TryCreate must refuse instead of dividing by ~0.
            var position = new Vector3d(800000.0, 0.0, 0.0);
            double escapeSpeed = Math.Sqrt(2.0 * KerbinMu / 800000.0);
            var velocity = new Vector3d(0.0, escapeSpeed, 0.0);

            Assert.False(TwoBodyOrbit.TryCreate(position, velocity, KerbinMu, 0.0, out _));
        }

        [Fact]
        public void DegenerateStateVectors_AreRejected()
        {
            // Zero position.
            Assert.False(TwoBodyOrbit.TryCreate(Vector3d.zero, new Vector3d(0.0, 2000.0, 0.0), KerbinMu, 0.0, out _));

            // Zero velocity.
            Assert.False(TwoBodyOrbit.TryCreate(new Vector3d(KerbinRadius, 0.0, 0.0), Vector3d.zero, KerbinMu, 0.0, out _));

            // Purely radial: no angular momentum, so no orbital plane exists.
            Assert.False(TwoBodyOrbit.TryCreate(
                new Vector3d(KerbinRadius, 0.0, 0.0),
                new Vector3d(1500.0, 0.0, 0.0),
                KerbinMu,
                0.0,
                out _));
        }

        [Fact]
        public void GetMeanMotionAndMeanAnomaly_DegradeToZeroWithoutAGravitationalParameter()
        {
            var orbit = new TwoBodyOrbit
            {
                GravitationalParameter = 0.0,
                SemiMajorAxis = 4000000.0,
                Eccentricity = 0.1,
                MeanAnomalyAtEpoch = 1.0,
                Epoch = 0.0,
                IsElliptic = true
            };

            Assert.Equal(0.0, orbit.GetMeanMotion());
            Assert.Equal(0.0, orbit.GetMeanAnomalyAtUT(5000.0));

            var nanOrbit = new TwoBodyOrbit
            {
                GravitationalParameter = KerbinMu,
                SemiMajorAxis = double.NaN,
                IsElliptic = true
            };
            Assert.Equal(0.0, nanOrbit.GetMeanMotion());

            var infiniteOrbit = new TwoBodyOrbit
            {
                GravitationalParameter = KerbinMu,
                SemiMajorAxis = double.PositiveInfinity,
                IsElliptic = true
            };
            Assert.Equal(0.0, infiniteOrbit.GetMeanMotion());
        }

        // ------------------------------------------------------------------
        // Element-sourced construction (TryCreateFromSegment)
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(0.0)]
        [InlineData(0.4)]
        [InlineData(0.95)]
        public void TryCreateFromSegment_DerivesPeriodAndApsesFromTheElements(double e)
        {
            const double a = 4000000.0;
            var segment = new OrbitSegment
            {
                inclination = 0.4,
                eccentricity = e,
                semiMajorAxis = a,
                longitudeOfAscendingNode = 1.1,
                argumentOfPeriapsis = 0.7,
                meanAnomalyAtEpoch = 0.0,
                epoch = 100.0
            };

            Assert.True(TwoBodyOrbit.TryCreateFromSegment(segment, KerbinMu, out TwoBodyOrbit orbit));

            Assert.True(orbit.IsElliptic);
            AssertRelative(a * (1.0 - e), orbit.PeriapsisRadius, 1e-12, "segment periapsis");
            AssertRelative(a * (1.0 + e), orbit.ApoapsisRadius, 1e-12, "segment apoapsis");
            AssertRelative(TwoPi * Math.Sqrt((a * a * a) / KerbinMu), orbit.Period, 1e-12, "segment period");
            AssertRelative(Math.Sqrt(KerbinMu / (a * a * a)), orbit.GetMeanMotion(), 1e-12, "segment mean motion");
        }

        [Fact]
        public void TryCreateFromSegment_RejectsUnusableElements()
        {
            var segment = new OrbitSegment
            {
                eccentricity = 0.2,
                semiMajorAxis = double.NaN,
                epoch = 0.0
            };
            Assert.False(TwoBodyOrbit.TryCreateFromSegment(segment, KerbinMu, out _));

            segment.semiMajorAxis = double.PositiveInfinity;
            Assert.False(TwoBodyOrbit.TryCreateFromSegment(segment, KerbinMu, out _));

            segment.semiMajorAxis = 4000000.0;
            Assert.False(TwoBodyOrbit.TryCreateFromSegment(segment, 0.0, out _));
        }

        [Fact]
        public void TwoBodyOrbit_ConvertsDegreeSegmentElementsToTheRadiansCore()
        {
            // Pins the boundary unit contract: OrbitSegment stores KSP-native DEGREES
            // for inc/LAN/argPe and TryCreateFromSegment converts into the
            // radians-internal core. A polar orbit is segment inclination = 90, and
            // the core must hold pi/2. If the conversion were dropped, 90 would be
            // read as radians and the orbit normal would leave the polar plane.
            var segment = new OrbitSegment
            {
                inclination = 90.0,
                eccentricity = 0.0,
                semiMajorAxis = 4000000.0,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = 0.0
            };

            Assert.True(TwoBodyOrbit.TryCreateFromSegment(segment, KerbinMu, out TwoBodyOrbit orbit));
            Assert.True(
                Math.Abs(orbit.Inclination - Math.PI / 2.0) < 1e-9,
                $"core inclination must be pi/2 radians for a 90-degree segment, got {orbit.Inclination}");

            orbit.GetStateAtUT(orbit.Period * 0.125, out Vector3d position, out Vector3d velocity);

            Vector3d momentum = Cross(position, velocity);
            double cosInclination = momentum.z / Mag(momentum);
            Assert.True(
                Math.Abs(cosInclination) < 1e-9,
                $"polar orbit normal must lie in the equatorial plane, got cos(i)={cosInclination}");

            // Round-tripping the state through TryCreate must give back the
            // radians-internal pi/2, not 90.
            Assert.True(TwoBodyOrbit.TryCreate(position, velocity, KerbinMu, 0.0, out TwoBodyOrbit recovered));
            Assert.True(
                Math.Abs(recovered.Inclination - Math.PI / 2.0) < 1e-9,
                $"expected pi/2 radians, got {recovered.Inclination}");
        }

        // ------------------------------------------------------------------
        // Construction helpers (independent of the product rotation code)
        // ------------------------------------------------------------------

        private static TwoBodyOrbit BuildOrbit(
            double a, double e, double inclination, double lan, double argPe,
            double trueAnomaly, double epoch, double mu)
        {
            BuildState(a, e, inclination, lan, argPe, trueAnomaly, mu, out Vector3d position, out Vector3d velocity);
            Assert.True(
                TwoBodyOrbit.TryCreate(position, velocity, mu, epoch, out TwoBodyOrbit orbit),
                "TryCreate rejected a well-formed state vector");
            return orbit;
        }

        /// <summary>
        /// Closed-form perifocal state at the given true anomaly, rotated into the
        /// inertial frame by three elementary rotations applied in sequence.
        /// </summary>
        private static void BuildState(
            double a, double e, double inclination, double lan, double argPe,
            double trueAnomaly, double mu,
            out Vector3d position, out Vector3d velocity)
        {
            double semiLatusRectum = a * (1.0 - e * e);
            double radius = semiLatusRectum / (1.0 + e * Math.Cos(trueAnomaly));
            double velocityScale = Math.Sqrt(mu / semiLatusRectum);

            var perifocalPosition = new Vector3d(
                radius * Math.Cos(trueAnomaly),
                radius * Math.Sin(trueAnomaly),
                0.0);
            var perifocalVelocity = new Vector3d(
                -velocityScale * Math.Sin(trueAnomaly),
                velocityScale * (e + Math.Cos(trueAnomaly)),
                0.0);

            position = RotateZ(RotateX(RotateZ(perifocalPosition, argPe), inclination), lan);
            velocity = RotateZ(RotateX(RotateZ(perifocalVelocity, argPe), inclination), lan);
        }

        private static Vector3d RotateZ(Vector3d v, double angle)
        {
            double c = Math.Cos(angle);
            double s = Math.Sin(angle);
            return new Vector3d(v.x * c - v.y * s, v.x * s + v.y * c, v.z);
        }

        private static Vector3d RotateX(Vector3d v, double angle)
        {
            double c = Math.Cos(angle);
            double s = Math.Sin(angle);
            return new Vector3d(v.x, v.y * c - v.z * s, v.y * s + v.z * c);
        }

        // ------------------------------------------------------------------
        // Assertion / math helpers
        // ------------------------------------------------------------------

        private static void AssertRelative(double expected, double actual, double relativeTolerance, string what)
        {
            double scale = Math.Max(Math.Abs(expected), 1e-300);
            double relativeError = Math.Abs(actual - expected) / scale;
            Assert.True(
                relativeError <= relativeTolerance,
                $"{what}: expected {Format(expected)}, got {Format(actual)}, relative error {Format(relativeError)}"
                + $" > {Format(relativeTolerance)}");
        }

        private static void AssertVectorRelative(Vector3d expected, Vector3d actual, double relativeTolerance, string what)
        {
            double scale = Math.Max(Mag(expected), 1e-300);
            var difference = new Vector3d(actual.x - expected.x, actual.y - expected.y, actual.z - expected.z);
            double relativeError = Mag(difference) / scale;
            Assert.True(
                relativeError <= relativeTolerance,
                $"{what}: expected ({Format(expected.x)}, {Format(expected.y)}, {Format(expected.z)}), "
                + $"got ({Format(actual.x)}, {Format(actual.y)}, {Format(actual.z)}), "
                + $"relative error {Format(relativeError)} > {Format(relativeTolerance)}");
        }

        private static string Format(double value)
        {
            return value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static double Mag(Vector3d v)
        {
            return Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        }

        private static double Dot(Vector3d a, Vector3d b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z;
        }

        private static Vector3d Cross(Vector3d a, Vector3d b)
        {
            return new Vector3d(
                a.y * b.z - a.z * b.y,
                a.z * b.x - a.x * b.z,
                a.x * b.y - a.y * b.x);
        }

        private static double Clamp(double value, double min, double max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        private static double NormalizeAngle(double angle)
        {
            double wrapped = angle % TwoPi;
            return wrapped < 0.0 ? wrapped + TwoPi : wrapped;
        }

        /// <summary>Smallest absolute separation between two wrapped angles.</summary>
        private static double AngleDifference(double a, double b)
        {
            double difference = Math.Abs(NormalizeAngle(a) - NormalizeAngle(b));
            return Math.Min(difference, TwoPi - difference);
        }
    }
}
