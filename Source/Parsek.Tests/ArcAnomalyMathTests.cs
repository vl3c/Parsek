using System;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for <see cref="ArcAnomalyMath"/>, the pure anomaly/radius math behind the ghost
    /// orbit-line arc clip (<c>GhostOrbitArcPatch</c>). These cover the elliptical-vs-hyperbolic
    /// branching that the surrounding KSP-Orbit-coupled clip cannot exercise off the Unity runtime:
    /// the periapsis-wraparound correction (elliptical only) and the endpoint conic-radius formula
    /// (cos for ellipse, cosh for hyperbola). The live Orbit-helper integration (EccentricAnomalyAtUT
    /// / getPositionFromEccAnomalyWithSemiMinorAxis on a real hyperbolic Orbit) is covered by the
    /// in-game test <c>HyperbolicArcClipBoundsLineToSegmentWindow</c>.
    /// </summary>
    public class ArcAnomalyMathTests
    {
        // --- NeedsPeriapsisWraparound: elliptical periapsis-crossing detection ---

        [Fact]
        public void NeedsPeriapsisWraparound_TrueWhenFromTrueAnomalyExceedsTo()
        {
            // Arc runs from late in one revolution (large V) to early in the next (small V): it
            // crosses periapsis (V=0), so the eccentric-anomaly range must be rebased.
            Assert.True(ArcAnomalyMath.NeedsPeriapsisWraparound(5.0, 0.3));
        }

        [Fact]
        public void NeedsPeriapsisWraparound_FalseWhenMonotonicIncreasing()
        {
            // A normal forward arc (V increases) does not cross periapsis.
            Assert.False(ArcAnomalyMath.NeedsPeriapsisWraparound(0.3, 5.0));
        }

        [Fact]
        public void NeedsPeriapsisWraparound_FalseWhenEqual()
        {
            Assert.False(ArcAnomalyMath.NeedsPeriapsisWraparound(2.0, 2.0));
        }

        // --- ApplyPeriapsisWraparound: E -> -(2pi - E) rebase ---

        [Fact]
        public void ApplyPeriapsisWraparound_RebasesNegativeAcrossPeriapsis()
        {
            // E just shy of 2pi becomes a small negative angle, making the range that ends near a
            // small positive E monotonically increasing through E=0.
            double e = (Math.PI * 2.0) - 0.5; // ~5.783
            double rebased = ArcAnomalyMath.ApplyPeriapsisWraparound(e);
            Assert.Equal(-0.5, rebased, 9);
            Assert.True(rebased < 0.0);
        }

        // --- EndpointRadius: elliptical cos branch ---

        [Fact]
        public void EndpointRadius_EllipticalAtPeriapsisIsPeR()
        {
            // At periapsis (E=0): r = sma*(1 - ecc) = PeR.
            double sma = 700000.0, ecc = 0.2;
            double r = ArcAnomalyMath.EndpointRadius(sma, ecc, 0.0, hyperbolic: false);
            Assert.Equal(sma * (1.0 - ecc), r, 6);
        }

        [Fact]
        public void EndpointRadius_EllipticalAtApoapsisIsApR()
        {
            // At apoapsis (E=pi): r = sma*(1 + ecc) = ApR.
            double sma = 700000.0, ecc = 0.2;
            double r = ArcAnomalyMath.EndpointRadius(sma, ecc, Math.PI, hyperbolic: false);
            Assert.Equal(sma * (1.0 + ecc), r, 6);
        }

        // --- EndpointRadius: hyperbolic cosh branch ---

        [Fact]
        public void EndpointRadius_HyperbolicAtPeriapsisIsPositivePeR()
        {
            // Hyperbola: sma < 0. At periapsis (H=0): r = sma*(1 - ecc*cosh(0)) = sma*(1-ecc),
            // and with sma<0, ecc>1 this is positive (= |sma|*(ecc-1) = PeR).
            double sma = -3818300.0, ecc = 1.1916;
            double r = ArcAnomalyMath.EndpointRadius(sma, ecc, 0.0, hyperbolic: true);
            double expected = sma * (1.0 - ecc); // = |sma|*(ecc-1)
            Assert.Equal(expected, r, 3);
            Assert.True(r > 0.0, $"hyperbolic periapsis radius must be positive (r={r})");
        }

        [Fact]
        public void EndpointRadius_HyperbolicGrowsWithAnomalyMagnitude()
        {
            // Moving away from periapsis along a hyperbola (|H| increasing) the radius grows
            // without bound — the cosh form must reflect that, unlike the bounded elliptical cos.
            double sma = -3818300.0, ecc = 1.1916;
            double rNear = ArcAnomalyMath.EndpointRadius(sma, ecc, 0.5, hyperbolic: true);
            double rFar = ArcAnomalyMath.EndpointRadius(sma, ecc, 2.0, hyperbolic: true);
            Assert.True(rFar > rNear,
                $"hyperbolic radius must grow with |H| (rNear={rNear}, rFar={rFar})");
        }

        [Fact]
        public void EndpointRadius_HyperbolicAndEllipticalDifferAtSameAnomaly()
        {
            // Same numeric anomaly, opposite branch: cos vs cosh diverge for non-zero anomaly, so
            // the flag genuinely selects different math (guards against a copy/paste that forgot to
            // switch the trig function).
            double sma = -3818300.0, ecc = 1.1916, anomaly = 1.0;
            double rHyp = ArcAnomalyMath.EndpointRadius(sma, ecc, anomaly, hyperbolic: true);
            double rEll = ArcAnomalyMath.EndpointRadius(sma, ecc, anomaly, hyperbolic: false);
            Assert.NotEqual(rEll, rHyp, 3);
        }
    }
}
