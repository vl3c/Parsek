using System;
using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    // Pure state-vector geometry for the re-aim whole-chain synthesis (reaim-fix-plan.md P2, STEP 1/2):
    // the body-relative velocity / position reductions, the .xzy round-trip identity (a swizzle error
    // silently corrupts the synthesized orbit), and the SOI-sphere-crossing bisection. The live glue
    // (Orbit.UpdateFromStateVectors, CelestialBody.orbit, PatchedConics) stays in ReaimTransferSynthesizer
    // and is validated by the in-game canary; these pure pieces are the xUnit floor.
    public class ReaimChainGeometryTests
    {
        // ---- vRel / rRel reductions (the frame-agnostic subtraction the construction feeds UpdateFromStateVectors) ----

        [Fact]
        public void RelativeVelocity_SubtractsBodyVelocity()
        {
            // vRel = v_heliocentric - v_body. Both operands in the same frame -> a plain subtraction.
            var vHelio = new Vector3d(12000.0, -3000.0, 500.0);
            var vBody = new Vector3d(9000.0, -2500.0, 100.0);
            Vector3d vRel = ReaimChainGeometry.RelativeVelocity(vHelio, vBody);
            Assert.Equal(3000.0, vRel.x, 6);
            Assert.Equal(-500.0, vRel.y, 6);
            Assert.Equal(400.0, vRel.z, 6);
        }

        [Fact]
        public void RelativePosition_DiffersParentRelativePositions()
        {
            // rRel = r_transfer - r_body (both parent-relative in one frame).
            var rTransfer = new Vector3d(1.0e10, 2.0e10, -3.0e9);
            var rBody = new Vector3d(1.0e10 - 200000.0, 2.0e10 + 50000.0, -3.0e9);
            Vector3d rRel = ReaimChainGeometry.RelativePosition(rTransfer, rBody);
            Assert.Equal(200000.0, rRel.x, 3);
            Assert.Equal(-50000.0, rRel.y, 3);
            Assert.Equal(0.0, rRel.z, 3);
        }

        [Fact]
        public void RelativeVelocity_SameOperandsCancelToZero()
        {
            // Identical operands -> zero (the body sitting exactly on the transfer endpoint).
            var v = new Vector3d(7.0, 8.0, 9.0);
            Vector3d d = ReaimChainGeometry.RelativeVelocity(v, v);
            Assert.Equal(0.0, d.magnitude, 9);
        }

        // ---- .xzy round-trip identity (the load-bearing swizzle contract) ----

        [Fact]
        public void XzySwizzle_IsItsOwnInverse_RoundTripsToOriginal()
        {
            // The transfer build un-swizzles (.xzy) to difference in one world frame, then re-swizzles
            // (.xzy) for UpdateFromStateVectors. .xzy is its own inverse, so v.xzy.xzy == v EXACTLY. The
            // capture/escape construction mirrors this; this pins the invariance the whole chain depends on.
            var v = new Vector3d(123.456, -789.012, 345.678);
            Vector3d roundTrip = v.xzy.xzy;
            Assert.Equal(v.x, roundTrip.x, 12);
            Assert.Equal(v.y, roundTrip.y, 12);
            Assert.Equal(v.z, roundTrip.z, 12);
        }

        [Fact]
        public void XzySwizzle_SwapsYandZ()
        {
            // Documents the swizzle itself: .xzy maps (x,y,z) -> (x,z,y).
            var v = new Vector3d(1.0, 2.0, 3.0);
            Vector3d s = v.xzy;
            Assert.Equal(1.0, s.x, 12);
            Assert.Equal(3.0, s.y, 12);
            Assert.Equal(2.0, s.z, 12);
        }

        [Fact]
        public void XzySwizzle_ReductionInvariant_DiffEqualsSwizzledDiff()
        {
            // The reduction is frame-agnostic: (a - b) swizzled == a.xzy - b.xzy. This is why the live
            // construction can difference in EITHER frame as long as both operands match (the contract the
            // RelativePosition/RelativeVelocity helpers rely on).
            var a = new Vector3d(10.0, 20.0, 30.0);
            var b = new Vector3d(1.0, 2.0, 3.0);
            Vector3d diffThenSwizzle = ReaimChainGeometry.RelativePosition(a, b).xzy;
            Vector3d swizzleThenDiff = ReaimChainGeometry.RelativePosition(a.xzy, b.xzy);
            Assert.Equal(diffThenSwizzle.x, swizzleThenDiff.x, 9);
            Assert.Equal(diffThenSwizzle.y, swizzleThenDiff.y, 9);
            Assert.Equal(diffThenSwizzle.z, swizzleThenDiff.z, 9);
        }

        // ---- SOI-sphere-crossing bisection ----

        // A monotone distance model: distance(ut) = baseline + rate * (ut - t0). Crosses the SOI radius
        // exactly once, so the bisection has a unique root - the analytic crossing is known for the assert.
        private static Func<double, double> LinearDistance(double t0, double baseline, double ratePerSecond)
        {
            return ut => baseline + ratePerSecond * (ut - t0);
        }

        [Fact]
        public void TryBisectSoiCrossing_ConvergesToSoiShell_DescendingIntoSoi()
        {
            // Distance DECREASES into the SOI (a capture): outside at the early UT, inside at the late UT.
            // distance(ut) = 2e9 - 1e6*(ut-0). SOI = 1e9 -> crossing at ut = 1000.
            double soi = 1.0e9;
            Func<double, double> dist = LinearDistance(0.0, 2.0e9, -1.0e6);
            // inside (<= soi) sample at ut=1500 (dist 0.5e9), outside (> soi) at ut=500 (dist 1.5e9).
            bool ok = ReaimChainGeometry.TryBisectSoiCrossing(
                dist, soi, insideUT: 1500.0, outsideUT: 500.0, toleranceMeters: 1.0e4,
                out double crossing);
            Assert.True(ok);
            Assert.Equal(1000.0, crossing, 0); // within 0.5s of the analytic root at the 1e4 m tolerance
            Assert.True(Math.Abs(dist(crossing) - soi) <= 1.0e4);
        }

        [Fact]
        public void TryBisectSoiCrossing_ConvergesToSoiShell_AscendingOutOfSoi()
        {
            // Distance INCREASES out of the SOI (an escape): inside at the early UT, outside at the late UT.
            // distance(ut) = 0 + 1e6*(ut-0). SOI = 84000 km = 8.4e7 -> crossing at ut = 84.
            double soi = 8.4e7;
            Func<double, double> dist = LinearDistance(0.0, 0.0, 1.0e6);
            bool ok = ReaimChainGeometry.TryBisectSoiCrossing(
                dist, soi, insideUT: 0.0, outsideUT: 200.0, toleranceMeters: 1.0e3,
                out double crossing);
            Assert.True(ok);
            Assert.Equal(84.0, crossing, 1);
            Assert.True(Math.Abs(dist(crossing) - soi) <= 1.0e3);
        }

        [Fact]
        public void TryBisectSoiCrossing_NotAStraddle_FailsClosed()
        {
            // Both samples outside the SOI (no inside boundary) -> not a valid straddle -> false / NaN.
            double soi = 1.0e9;
            Func<double, double> dist = LinearDistance(0.0, 2.0e9, 1.0e6); // always > soi
            bool ok = ReaimChainGeometry.TryBisectSoiCrossing(
                dist, soi, insideUT: 100.0, outsideUT: 200.0, toleranceMeters: 1.0e4, out double crossing);
            Assert.False(ok);
            Assert.True(double.IsNaN(crossing));
        }

        [Fact]
        public void TryBisectSoiCrossing_WrongStraddleOrientation_FailsClosed()
        {
            // The "inside" sample is actually OUTSIDE and vice versa (caller passed the bracket reversed) ->
            // the inside/outside contract is violated -> fail closed rather than converge on the wrong root.
            double soi = 1.0e9;
            Func<double, double> dist = LinearDistance(0.0, 2.0e9, -1.0e6); // dist(1500)=0.5e9 inside, dist(500)=1.5e9 outside
            // Pass them swapped: claim 500 is inside (it is outside) and 1500 is outside (it is inside).
            bool ok = ReaimChainGeometry.TryBisectSoiCrossing(
                dist, soi, insideUT: 500.0, outsideUT: 1500.0, toleranceMeters: 1.0e4, out double crossing);
            Assert.False(ok);
            Assert.True(double.IsNaN(crossing));
        }

        [Fact]
        public void TryBisectSoiCrossing_DegenerateInputs_FailClosed()
        {
            Func<double, double> dist = LinearDistance(0.0, 2.0e9, -1.0e6);
            Assert.False(ReaimChainGeometry.TryBisectSoiCrossing(null, 1e9, 1500, 500, 1e4, out _));
            Assert.False(ReaimChainGeometry.TryBisectSoiCrossing(dist, 0.0, 1500, 500, 1e4, out _));
            Assert.False(ReaimChainGeometry.TryBisectSoiCrossing(dist, double.NaN, 1500, 500, 1e4, out _));
            Assert.False(ReaimChainGeometry.TryBisectSoiCrossing(dist, 1e9, double.NaN, 500, 1e4, out _));
            Assert.False(ReaimChainGeometry.TryBisectSoiCrossing(dist, 1e9, 1500, double.NaN, 1e4, out _));
            Assert.False(ReaimChainGeometry.TryBisectSoiCrossing(dist, 1e9, 1500, 500, 0.0, out _));
        }

        [Fact]
        public void TryBisectSoiCrossing_TightTolerance_StillTerminatesOnBracketCollapse()
        {
            // An impossibly tight tolerance (1e-9 m) forces the iteration cap; the bracket collapses to a UT
            // and the method returns true with the midpoint (converged on UT, not on the position tolerance).
            double soi = 1.0e9;
            Func<double, double> dist = LinearDistance(0.0, 2.0e9, -1.0e6);
            bool ok = ReaimChainGeometry.TryBisectSoiCrossing(
                dist, soi, insideUT: 1500.0, outsideUT: 500.0, toleranceMeters: 1.0e-9, out double crossing,
                maxIterations: 60);
            Assert.True(ok);
            // 60 bisections of a 1000s bracket -> < 1e-15 s residual, far inside any UT meaning.
            Assert.Equal(1000.0, crossing, 6);
        }
    }
}
