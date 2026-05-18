using Xunit;
using UnityEngine;

namespace Parsek.Tests
{
    public class TrajectoryMathTests
    {
        // Shared helper for shortest-angle delta in degrees. Used by orbital tail
        // matching (LAN / argP / inclination); promoted to internal in the #612
        // wraparound follow-up so the math stays centralized.
        private const double Eps = 1e-12;

        [Fact]
        public void PureSlerp_ClampsTLikeUnitySlerp()
        {
            var from = new Quaternion(0f, 0f, 0f, 1f);
            var to = new Quaternion(0f, 0.7071068f, 0f, 0.7071068f);

            AssertQuaternionClose(from, TrajectoryMath.PureSlerp(from, to, -0.5f), 1e-6f);
            AssertQuaternionClose(to, TrajectoryMath.PureSlerp(from, to, 1.5f), 1e-6f);
        }

        [Fact]
        public void PureSlerp_UsesShortestPathForSignEquivalentEndpoint()
        {
            var from = new Quaternion(0f, 0f, 0f, 1f);
            var to = new Quaternion(0f, 0f, 0f, -1f);

            AssertQuaternionClose(from, TrajectoryMath.PureSlerp(from, to, 0.5f), 1e-6f);
        }

        // Regression guard for the "Rotation Slerp wraparound" investigation
        // (closed 2026-05-18 as a verified non-defect). The existing
        // PureSlerp_UsesShortestPathForSignEquivalentEndpoint test covers the
        // exact-antipodal case (dot = -1, q vs -q for identical orientation).
        // This test pins the near-antipodal case (dot ≈ -0.99, ~8° physical
        // rotation between endpoints stored with opposite sign): without the
        // canonical `if (dot < 0) negate to` pre-step inside PureSlerp, the
        // result at t=0.5 would land near the antipode of the expected short-
        // path midpoint instead. If a future refactor drops the sign-
        // correction, this test fails immediately rather than waiting for an
        // in-game playtest to surface the regression.
        [Fact]
        public void PureSlerp_NearAntipodalEndpoints_TakesShortPath()
        {
            // Two near-antipodal quaternions representing nearly the same
            // physical orientation (~8° apart). Compose `to` as the sign-
            // flipped form so dot(from, to) is large-negative.
            var from = UnitQuaternionAroundY(0f);
            var antipode = UnitQuaternionAroundY(8f);
            var to = new Quaternion(-antipode.x, -antipode.y, -antipode.z, -antipode.w);

            float dot = from.x * to.x + from.y * to.y + from.z * to.z + from.w * to.w;
            Assert.True(dot < -0.99f,
                $"Test precondition: dot must be near -1 (was {dot:F4}).");

            Quaternion mid = TrajectoryMath.PureSlerp(from, to, 0.5f);

            // Short-path midpoint = halfway between `from` and `antipode`
            // (the same-orientation form of `to`), i.e. ~4° rotation around Y.
            // The long-path output would land near ~176° around Y instead,
            // which we explicitly assert against below.
            var shortPathMid = UnitQuaternionAroundY(4f);
            AssertQuaternionCloseUpToSign(shortPathMid, mid, 5e-4f);

            // Long-path detector: the antipodal long-arc would produce a
            // quaternion oriented ~176° from `from`, which has a large
            // angular delta. Compute the angle between the result and the
            // expected short-path midpoint and assert it is small. This is
            // independent of the component-wise check above and catches
            // sign-correction regressions even if AssertQuaternionCloseUpToSign
            // is loosened in the future.
            float resultDotShort = mid.x * shortPathMid.x
                + mid.y * shortPathMid.y
                + mid.z * shortPathMid.z
                + mid.w * shortPathMid.w;
            float angleFromShortPathDegrees = (float)(2.0 * System.Math.Acos(
                System.Math.Min(1.0f, System.Math.Abs(resultDotShort))) * 180.0 / System.Math.PI);
            Assert.True(angleFromShortPathDegrees < 1.0f,
                $"Slerp result is {angleFromShortPathDegrees:F2}° from short-path midpoint; sign-correction likely missing.");
        }

        // Pure helper: unit quaternion for a `degrees` rotation around the
        // y-axis. Kept local to TrajectoryMathTests so the Slerp regression
        // tests do not depend on UnityEngine.Quaternion.AngleAxis (which is a
        // Unity ECall that the xUnit JIT verifier rejects).
        private static Quaternion UnitQuaternionAroundY(float degrees)
        {
            float halfRad = degrees * (float)(System.Math.PI / 180.0) * 0.5f;
            float s = (float)System.Math.Sin(halfRad);
            float c = (float)System.Math.Cos(halfRad);
            return new Quaternion(0f, s, 0f, c);
        }

        private static void AssertQuaternionCloseUpToSign(
            Quaternion expected, Quaternion actual, float tolerance)
        {
            // Unit quaternions q and -q represent the same physical rotation;
            // the sign-corrected Slerp may return either depending on the
            // caller's stored endpoint hemisphere. Accept both forms.
            float dot = expected.x * actual.x + expected.y * actual.y
                + expected.z * actual.z + expected.w * actual.w;
            Quaternion candidate = dot >= 0f
                ? actual
                : new Quaternion(-actual.x, -actual.y, -actual.z, -actual.w);
            AssertQuaternionClose(expected, candidate, tolerance);
        }

        private static void AssertQuaternionClose(Quaternion expected, Quaternion actual, float tolerance)
        {
            Assert.InRange(System.Math.Abs(expected.x - actual.x), 0, tolerance);
            Assert.InRange(System.Math.Abs(expected.y - actual.y), 0, tolerance);
            Assert.InRange(System.Math.Abs(expected.z - actual.z), 0, tolerance);
            Assert.InRange(System.Math.Abs(expected.w - actual.w), 0, tolerance);
        }

        private static void AssertVectorClose(Vector3d expected, Vector3d actual, double tolerance)
        {
            Assert.InRange(System.Math.Abs(expected.x - actual.x), 0.0, tolerance);
            Assert.InRange(System.Math.Abs(expected.y - actual.y), 0.0, tolerance);
            Assert.InRange(System.Math.Abs(expected.z - actual.z), 0.0, tolerance);
        }

        [Fact]
        public void AngularDeltaDegrees_SimpleDelta_ReturnsAbsoluteDifference()
        {
            Assert.Equal(5.0, TrajectoryMath.AngularDeltaDegrees(45.0, 50.0), 12);
            Assert.Equal(5.0, TrajectoryMath.AngularDeltaDegrees(50.0, 45.0), 12);
        }

        [Fact]
        public void AngularDeltaDegrees_ZeroDelta_ReturnsZero()
        {
            Assert.Equal(0.0, TrajectoryMath.AngularDeltaDegrees(123.4, 123.4), 12);
        }

        [Fact]
        public void AngularDeltaDegrees_AcrossWraparound_ReturnsShortPath()
        {
            // 359.997 -> 0.002 wraps across the boundary; raw Abs(a-b) = 359.995,
            // wrapped delta = 0.005.
            Assert.Equal(0.005, TrajectoryMath.AngularDeltaDegrees(359.997, 0.002), 9);
            Assert.Equal(0.005, TrajectoryMath.AngularDeltaDegrees(0.002, 359.997), 9);
        }

        [Fact]
        public void AngularDeltaDegrees_TrueLongPath_RejectsCorrectly()
        {
            // 359.5 vs 0.5 wraps to a 1.0 deg short delta — the helper picks the short
            // path, not the literal 359.0 difference. This is the behaviour callers
            // expect for stable-orbit tail matching.
            Assert.Equal(1.0, TrajectoryMath.AngularDeltaDegrees(359.5, 0.5), 9);
        }

        [Fact]
        public void AngularDeltaDegrees_HalfTurn_Returns180()
        {
            // Antipodes — both directions are equally long; helper returns the
            // canonical positive 180.
            Assert.Equal(180.0, TrajectoryMath.AngularDeltaDegrees(0.0, 180.0), 9);
            Assert.Equal(180.0, TrajectoryMath.AngularDeltaDegrees(90.0, 270.0), 9);
        }

        [Fact]
        public void AngularDeltaDegrees_InclinationRange_SafeForCommonValues()
        {
            // Inclination is in [0, 180] and the wrap-correction branch should never
            // fire for that range; verify the helper still produces correct deltas.
            Assert.Equal(5.0, TrajectoryMath.AngularDeltaDegrees(28.5, 33.5), 12);
            Assert.Equal(0.005, TrajectoryMath.AngularDeltaDegrees(1.250, 1.255), 9);
            Assert.Equal(45.0, TrajectoryMath.AngularDeltaDegrees(45.0, 90.0), 12);
        }

        [Fact]
        public void AngularDeltaDegrees_AnglesOutsideZeroTo360_NormalizesCorrectly()
        {
            // Inputs outside [0, 360) are valid (KSP can hand back small negatives or
            // values >360 from numerical drift); the helper must still produce the
            // shortest positive delta.
            Assert.Equal(5.0, TrajectoryMath.AngularDeltaDegrees(-2.5, 2.5), 9);
            Assert.Equal(5.0, TrajectoryMath.AngularDeltaDegrees(362.5, 357.5), 9);
        }

        [Fact]
        public void AngularDeltaDegrees_ResultAlwaysNonNegative()
        {
            // The helper returns a magnitude, never a signed delta — callers compare
            // against an epsilon and don't need direction.
            Assert.True(TrajectoryMath.AngularDeltaDegrees(10.0, 20.0) >= 0.0 - Eps);
            Assert.True(TrajectoryMath.AngularDeltaDegrees(20.0, 10.0) >= 0.0 - Eps);
            Assert.True(TrajectoryMath.AngularDeltaDegrees(359.0, 1.0) >= 0.0 - Eps);
        }

        [Fact]
        public void TryInterpolateWorldHermite_ConstantVelocityMatchesLerp()
        {
            bool ok = TrajectoryMath.TryInterpolateWorldHermite(
                new Vector3d(0.0, 0.0, 0.0),
                new Vector3(10f, 0f, 0f),
                new Vector3d(10.0, 0.0, 0.0),
                new Vector3(10f, 0f, 0f),
                1.0,
                0.5f,
                10.0,
                out Vector3d result,
                out double deviationMeters,
                out string reason);

            Assert.True(ok, reason);
            Assert.Equal("applied", reason);
            Assert.Equal(0.0, deviationMeters, 6);
            AssertVectorClose(new Vector3d(5.0, 0.0, 0.0), result, 0.0001);
        }

        [Fact]
        public void TryInterpolateWorldHermite_UsesEndpointVelocities()
        {
            bool ok = TrajectoryMath.TryInterpolateWorldHermite(
                new Vector3d(0.0, 0.0, 0.0),
                new Vector3(5f, 0f, 0f),
                new Vector3d(10.0, 0.0, 0.0),
                new Vector3(15f, 0f, 0f),
                1.0,
                0.5f,
                10.0,
                out Vector3d result,
                out double deviationMeters,
                out string reason);

            Assert.True(ok, reason);
            Assert.Equal("applied", reason);
            Assert.Equal(1.25, deviationMeters, 6);
            AssertVectorClose(new Vector3d(3.75, 0.0, 0.0), result, 0.0001);
        }

        [Fact]
        public void TryInterpolateWorldHermite_RejectsLargeBow()
        {
            bool ok = TrajectoryMath.TryInterpolateWorldHermite(
                new Vector3d(0.0, 0.0, 0.0),
                new Vector3(10f, 100f, 0f),
                new Vector3d(10.0, 0.0, 0.0),
                new Vector3(10f, -100f, 0f),
                1.0,
                0.5f,
                5.0,
                out _,
                out double deviationMeters,
                out string reason);

            Assert.False(ok);
            Assert.Equal("deviation-too-large", reason);
            Assert.True(deviationMeters > 5.0);
        }
    }
}
