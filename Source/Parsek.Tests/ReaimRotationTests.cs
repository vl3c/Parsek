using System;
using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    // Pure rotation-math tests for the re-aim arrival-seam restitch (docs/dev/plans/
    // reaim-arrival-seam-restitch.md sections 4.2 / 4.3). These cover the frame-pure primitives that the
    // resolver feeds with KSP Zup state vectors; the live element read-back (UpdateFromStateVectors round
    // trip) is canary-tested in-game because it needs a live KSP Orbit.
    public class ReaimRotationTests
    {
        private static void AssertVecEqual(double[] expected, double[] actual, int precision = 9)
        {
            Assert.Equal(expected[0], actual[0], precision);
            Assert.Equal(expected[1], actual[1], precision);
            Assert.Equal(expected[2], actual[2], precision);
        }

        // ---- InboundAsymptoteDir ----

        [Fact]
        public void InboundAsymptoteDir_NullOrNonHyperbolic_ReturnsNull()
        {
            var e = new[] { 1.0, 0.0, 0.0 };
            var h = new[] { 0.0, 0.0, 1.0 };
            Assert.Null(ReaimRotation.InboundAsymptoteDir(null, h, 1.5));
            Assert.Null(ReaimRotation.InboundAsymptoteDir(e, null, 1.5));
            Assert.Null(ReaimRotation.InboundAsymptoteDir(e, h, 1.0));   // parabolic, no real asymptote
            Assert.Null(ReaimRotation.InboundAsymptoteDir(e, h, 0.5));   // elliptic
            Assert.Null(ReaimRotation.InboundAsymptoteDir(e, h, double.NaN));
        }

        [Fact]
        public void InboundAsymptoteDir_ReturnsUnitVectorInOrbitPlane()
        {
            // Periapsis along +x, plane normal along +z => q_hat = h_hat x e_hat = +y. The asymptote must
            // be a unit vector lying in the x-y plane (perpendicular to the +z normal).
            var e = new[] { 2.0, 0.0, 0.0 };   // periapsis direction +x (magnitude is normalized away)
            var h = new[] { 0.0, 0.0, 3.0 };   // plane normal +z
            double ecc = 1.5;
            var s = ReaimRotation.InboundAsymptoteDir(e, h, ecc);
            Assert.NotNull(s);
            Assert.Equal(1.0, ReaimRotation.Magnitude(s), 9);        // unit
            Assert.Equal(0.0, s[2], 9);                              // in the x-y plane (no z)
            // Components: radial = sqrt(1 - 1/ecc^2) on e_hat(+x), tangential = ecc - 1/ecc on q_hat(+y).
            double radial = Math.Sqrt(1.0 - 1.0 / (ecc * ecc));
            double tangential = ecc - 1.0 / ecc;
            double norm = Math.Sqrt(radial * radial + tangential * tangential);
            Assert.Equal(radial / norm, s[0], 9);
            Assert.Equal(tangential / norm, s[1], 9);
        }

        [Fact]
        public void InboundAsymptoteDir_HighEccLimit_TendsToQHat()
        {
            // As ecc -> infinity the asymptote tends to q_hat (the tangential coefficient ecc - 1/ecc
            // dominates the radial sqrt(1 - 1/ecc^2) -> 1). With e=+x, h=+z, q_hat=+y, so s -> +y.
            var e = new[] { 1.0, 0.0, 0.0 };
            var h = new[] { 0.0, 0.0, 1.0 };
            var s = ReaimRotation.InboundAsymptoteDir(e, h, 1.0e6);
            Assert.NotNull(s);
            Assert.True(s[1] > 0.9999, "high-ecc asymptote should be nearly +q_hat (+y)");
            Assert.True(Math.Abs(s[0]) < 1.0e-3, "radial component vanishes at high ecc");
        }

        [Fact]
        public void InboundAsymptoteDir_JustAboveOne_FiniteAndNormalized()
        {
            var e = new[] { 0.0, 1.0, 0.0 };
            var h = new[] { 1.0, 0.0, 0.0 };
            var s = ReaimRotation.InboundAsymptoteDir(e, h, 1.0001);
            Assert.NotNull(s);
            Assert.False(double.IsNaN(s[0]) || double.IsNaN(s[1]) || double.IsNaN(s[2]));
            Assert.Equal(1.0, ReaimRotation.Magnitude(s), 9);
        }

        // ---- RotationFrameToFrame ----

        [Fact]
        public void RotationFrameToFrame_IdentityWhenFramesEqual()
        {
            var s = new[] { 1.0, 0.0, 0.0 };
            var h = new[] { 0.0, 0.0, 1.0 };
            var r = ReaimRotation.RotationFrameToFrame(s, h, s, h);
            Assert.NotNull(r);
            // Identity to round-off.
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    Assert.Equal(i == j ? 1.0 : 0.0, r[i, j], 9);
        }

        [Fact]
        public void RotationFrameToFrame_IsOrthonormalProperRotation()
        {
            var sFrom = new[] { 1.0, 0.0, 0.0 };
            var hFrom = new[] { 0.0, 0.0, 1.0 };
            var sTo = new[] { 0.0, 1.0, 0.0 };
            var hTo = new[] { 1.0, 0.0, 0.0 };
            var r = ReaimRotation.RotationFrameToFrame(sFrom, hFrom, sTo, hTo);
            Assert.NotNull(r);
            AssertOrthonormalProper(r);
        }

        [Fact]
        public void RotationFrameToFrame_MapsBothAxesExactly()
        {
            // R must carry sFrom -> sTo AND hFrom -> hTo (full-frame match, not direction-only).
            var sFrom = new[] { 1.0, 0.0, 0.0 };
            var hFrom = new[] { 0.0, 0.0, 1.0 };
            var sTo = new[] { 0.0, 1.0, 0.0 };
            var hTo = new[] { -1.0, 0.0, 0.0 };
            var r = ReaimRotation.RotationFrameToFrame(sFrom, hFrom, sTo, hTo);
            Assert.NotNull(r);
            AssertVecEqual(ReaimRotation.Normalize(sTo), ReaimRotation.RotateVector(r, sFrom), 9);
            AssertVecEqual(ReaimRotation.Normalize(hTo), ReaimRotation.RotateVector(r, hFrom), 9);
        }

        [Fact]
        public void RotationFrameToFrame_NonPerpendicularInputs_StillOrthonormal()
        {
            // s not exactly perpendicular to h: the frame-builder re-orthonormalizes, so R stays a proper
            // rotation (the recorded asymptote may carry tiny numeric non-perpendicularity).
            var sFrom = new[] { 1.0, 0.0, 0.05 };
            var hFrom = new[] { 0.0, 0.0, 1.0 };
            var sTo = new[] { 0.0, 1.0, 0.03 };
            var hTo = new[] { 1.0, 0.0, 0.0 };
            var r = ReaimRotation.RotationFrameToFrame(sFrom, hFrom, sTo, hTo);
            Assert.NotNull(r);
            AssertOrthonormalProper(r);
        }

        [Fact]
        public void RotationFrameToFrame_DegenerateInput_ReturnsNull()
        {
            var zero = new[] { 0.0, 0.0, 0.0 };
            var h = new[] { 0.0, 0.0, 1.0 };
            var s = new[] { 1.0, 0.0, 0.0 };
            Assert.Null(ReaimRotation.RotationFrameToFrame(zero, h, s, h));
            Assert.Null(ReaimRotation.RotationFrameToFrame(s, zero, s, h));
            Assert.Null(ReaimRotation.RotationFrameToFrame(s, h, zero, h));
            Assert.Null(ReaimRotation.RotationFrameToFrame(s, h, s, zero));
        }

        // ---- RotateVector ----

        [Fact]
        public void RotateVector_NullR_ReturnsCopyUnchanged()
        {
            var v = new[] { 3.0, -2.0, 7.0 };
            var rotated = ReaimRotation.RotateVector(null, v);
            AssertVecEqual(v, rotated, 12);
            // Must be a copy, not the same array (mutating the result must not corrupt v).
            rotated[0] = 99.0;
            Assert.Equal(3.0, v[0], 12);
        }

        [Fact]
        public void RotateVector_IdentityR_VectorUnchanged()
        {
            var v = new[] { 1.0, 2.0, 3.0 };
            var rotated = ReaimRotation.RotateVector(ReaimRotation.Identity(), v);
            AssertVecEqual(v, rotated, 12);
        }

        [Fact]
        public void RotateVector_KnownRotation_RotatesCorrectly()
        {
            // 90-degree rotation about +z: maps +x -> +y.
            var r = new double[,]
            {
                { 0, -1, 0 },
                { 1, 0, 0 },
                { 0, 0, 1 }
            };
            var rotated = ReaimRotation.RotateVector(r, new[] { 1.0, 0.0, 0.0 });
            AssertVecEqual(new[] { 0.0, 1.0, 0.0 }, rotated, 12);
        }

        // ---- No-op identity round-trip (the load-bearing guard) ----

        [Fact]
        public void RotationFrameToFrame_SameFrame_RotateVector_IsNoOp()
        {
            // R == identity (frames equal) must leave any vector byte-identical (the no-op guard the
            // resolver relies on: R == identity => byte-identical segment, no spurious re-aim).
            var s = new[] { 0.3, 0.4, 0.86602540378 };
            var h = ReaimRotation.Normalize(ReaimRotation.Cross(s, new[] { 0.0, 0.0, 1.0 }));
            var r = ReaimRotation.RotationFrameToFrame(s, h, s, h);
            Assert.NotNull(r);
            var v = new[] { 12.0, -5.0, 8.0 };
            AssertVecEqual(v, ReaimRotation.RotateVector(r, v), 9);
        }

        [Fact]
        public void RotationAngleRadians_IdentityIsZero_NinetyDegreeIsHalfPi()
        {
            Assert.Equal(0.0, ReaimRotation.RotationAngleRadians(ReaimRotation.Identity()), 9);
            var r = new double[,]
            {
                { 0, -1, 0 },
                { 1, 0, 0 },
                { 0, 0, 1 }
            };
            Assert.Equal(Math.PI / 2.0, ReaimRotation.RotationAngleRadians(r), 9);
            Assert.True(double.IsNaN(ReaimRotation.RotationAngleRadians(null)));
        }

        // ---- helpers ----

        private static void AssertOrthonormalProper(double[,] r)
        {
            // Columns unit length and mutually orthogonal; determinant +1.
            double[][] cols =
            {
                new[] { r[0, 0], r[1, 0], r[2, 0] },
                new[] { r[0, 1], r[1, 1], r[2, 1] },
                new[] { r[0, 2], r[1, 2], r[2, 2] }
            };
            for (int i = 0; i < 3; i++)
                Assert.Equal(1.0, ReaimRotation.Magnitude(cols[i]), 9);
            Assert.Equal(0.0, ReaimRotation.Dot(cols[0], cols[1]), 9);
            Assert.Equal(0.0, ReaimRotation.Dot(cols[0], cols[2]), 9);
            Assert.Equal(0.0, ReaimRotation.Dot(cols[1], cols[2]), 9);
            double det =
                r[0, 0] * (r[1, 1] * r[2, 2] - r[1, 2] * r[2, 1])
                - r[0, 1] * (r[1, 0] * r[2, 2] - r[1, 2] * r[2, 0])
                + r[0, 2] * (r[1, 0] * r[2, 1] - r[1, 1] * r[2, 0]);
            Assert.Equal(1.0, det, 9);
        }
    }
}
