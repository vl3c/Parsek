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

        private static void AssertQuaternionClose(Quaternion expected, Quaternion actual, float tolerance)
        {
            Assert.InRange(System.Math.Abs(expected.x - actual.x), 0, tolerance);
            Assert.InRange(System.Math.Abs(expected.y - actual.y), 0, tolerance);
            Assert.InRange(System.Math.Abs(expected.z - actual.z), 0, tolerance);
            Assert.InRange(System.Math.Abs(expected.w - actual.w), 0, tolerance);
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
    }
}
