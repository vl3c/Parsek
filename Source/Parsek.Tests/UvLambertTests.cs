using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    // Phase 1 of re-aim: the universal-variable Lambert solver, validated against the canonical
    // textbook case (Curtis "Orbital Mechanics for Engineering Students", Example 5.2) plus round-trip
    // and degeneracy guards. Units are km / km^3 s^-2 / s here (the solver is unit-agnostic so long as
    // inputs are consistent). Each test states the regression it guards.
    public class UvLambertTests
    {
        private const double MuEarthKm = 398600.0; // km^3/s^2

        [Fact]
        public void Solve_CurtisExample5_2_MatchesTextbookVelocities()
        {
            // Curtis Example 5.2 (THE canonical Lambert test): r1=[5000,10000,2100],
            // r2=[-14600,2500,7000] km, tof=3600 s, prograde -> v1=[-5.9925,1.9254,3.2456],
            // v2=[-3.3125,-4.1966,-0.38529] km/s. Guards the core solve against a known answer.
            var r1 = new Vector3d(5000.0, 10000.0, 2100.0);
            var r2 = new Vector3d(-14600.0, 2500.0, 7000.0);
            bool ok = UvLambert.Solve(MuEarthKm, r1, r2, 3600.0, prograde: true,
                out Vector3d v1, out Vector3d v2);

            Assert.True(ok);
            Assert.Equal(-5.9925, v1.x, 2);
            Assert.Equal(1.9254, v1.y, 2);
            Assert.Equal(3.2456, v1.z, 2);
            Assert.Equal(-3.3125, v2.x, 2);
            Assert.Equal(-4.1966, v2.y, 2);
            Assert.Equal(-0.38529, v2.z, 2);
        }

        [Fact]
        public void Solve_SolvedVelocity_PropagatesBackToTarget()
        {
            // Round-trip: integrate r1 + v1 forward by tof under two-body gravity (RK4) and confirm it
            // lands on r2. Guards that the solved departure velocity actually reaches the target.
            var r1 = new Vector3d(7000.0, 0.0, 0.0);
            var r2 = new Vector3d(0.0, 8000.0, 1000.0);
            double tof = 2000.0;
            bool ok = UvLambert.Solve(MuEarthKm, r1, r2, tof, prograde: true,
                out Vector3d v1, out _);
            Assert.True(ok);

            Vector3d endPos = PropagateTwoBody(r1, v1, MuEarthKm, tof);
            double missKm = (endPos - r2).magnitude;
            Assert.True(missKm < 1.0, $"propagated miss {missKm} km should be < 1 km");
        }

        [Fact]
        public void Solve_PrModeVsRetrograde_DifferConsistently()
        {
            // Prograde and retrograde are distinct valid solutions; both should solve and differ.
            var r1 = new Vector3d(7000.0, 0.0, 0.0);
            var r2 = new Vector3d(0.0, 7000.0, 0.0);
            bool p = UvLambert.Solve(MuEarthKm, r1, r2, 1800.0, true, out Vector3d vp, out _);
            bool r = UvLambert.Solve(MuEarthKm, r1, r2, 1800.0, false, out Vector3d vr, out _);
            Assert.True(p);
            Assert.True(r);
            Assert.True((vp - vr).magnitude > 0.1);
        }

        [Fact]
        public void Solve_CollinearEndpoints_ReturnsFalse()
        {
            // ~180-degree transfer (r2 antiparallel to r1): plane undefined -> bail (caller skips).
            var r1 = new Vector3d(7000.0, 0.0, 0.0);
            var r2 = new Vector3d(-8000.0, 0.0, 0.0);
            Assert.False(UvLambert.Solve(MuEarthKm, r1, r2, 3600.0, true, out _, out _));
            // ~0-degree transfer (same direction).
            var r2b = new Vector3d(8000.0, 0.0, 0.0);
            Assert.False(UvLambert.Solve(MuEarthKm, r1, r2b, 3600.0, true, out _, out _));
        }

        [Fact]
        public void Solve_DegenerateInputs_ReturnFalse()
        {
            var r1 = new Vector3d(7000.0, 0.0, 0.0);
            var r2 = new Vector3d(0.0, 8000.0, 0.0);
            Assert.False(UvLambert.Solve(0.0, r1, r2, 3600.0, true, out _, out _));        // mu<=0
            Assert.False(UvLambert.Solve(MuEarthKm, r1, r2, 0.0, true, out _, out _));      // tof<=0
            Assert.False(UvLambert.Solve(MuEarthKm, Vector3d.zero, r2, 3600.0, true, out _, out _)); // |r1|=0
            Assert.False(UvLambert.Solve(double.NaN, r1, r2, 3600.0, true, out _, out _));  // NaN mu
        }

        [Theory]
        [InlineData(0.0, 0.5)]
        [InlineData(1.5, 0.44054)]  // C(1.5) = (1-cos(sqrt 1.5))/1.5
        [InlineData(-1.5, 0.56571)] // hyperbolic branch: (cosh(sqrt 1.5)-1)/1.5
        public void StumpffC_AtKnownPoints(double z, double expectedC)
        {
            Assert.Equal(expectedC, UvLambert.StumpffC(z), 4);
        }

        [Fact]
        public void Stumpff_SeriesLimitsAtZero()
        {
            Assert.Equal(0.5, UvLambert.StumpffC(0.0), 9);
            Assert.Equal(1.0 / 6.0, UvLambert.StumpffS(0.0), 9);
            // Continuity across z=0 (tiny positive/negative match the limit).
            Assert.Equal(0.5, UvLambert.StumpffC(1e-12), 6);
            Assert.Equal(0.5, UvLambert.StumpffC(-1e-12), 6);
        }

        // Simple RK4 two-body propagator for the round-trip test (test-only, not production).
        private static Vector3d PropagateTwoBody(Vector3d r0, Vector3d v0, double mu, double tof)
        {
            Vector3d r = r0, v = v0;
            int steps = 20000;
            double dt = tof / steps;
            for (int i = 0; i < steps; i++)
            {
                Vector3d k1r = v, k1v = Accel(r, mu);
                Vector3d k2r = v + 0.5 * dt * k1v, k2v = Accel(r + 0.5 * dt * k1r, mu);
                Vector3d k3r = v + 0.5 * dt * k2v, k3v = Accel(r + 0.5 * dt * k2r, mu);
                Vector3d k4r = v + dt * k3v, k4v = Accel(r + dt * k3r, mu);
                r += (dt / 6.0) * (k1r + 2.0 * k2r + 2.0 * k3r + k4r);
                v += (dt / 6.0) * (k1v + 2.0 * k2v + 2.0 * k3v + k4v);
            }
            return r;
        }

        private static Vector3d Accel(Vector3d r, double mu)
        {
            double m = r.magnitude;
            return (-mu / (m * m * m)) * r;
        }
    }
}
