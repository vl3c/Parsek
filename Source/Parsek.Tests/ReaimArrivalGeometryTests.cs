using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    // Pure arrival-v_inf geometry tests for the re-aim arrival-seam SOI-timing objective (docs/dev/plans/
    // reaim-arrival-seam-timing.md). These cover the frame-pure primitives the resolver feeds with KSP Zup
    // state vectors (asymptote direction, |v_inf| magnitude, vis-viva sma, the eccentricity vector, the
    // full v_inf vector, the mismatch objective, and the decline-to-faithful gate). The live element
    // read-back + transfer sampling (ReaimArrivalVInf) is canary-tested in-game because it needs a live KSP
    // Orbit. The byte-identical destination-leg regression (the non-regression guard) is at the bottom.
    public class ReaimArrivalGeometryTests
    {
        private static void AssertVecEqual(double[] expected, double[] actual, int precision = 9)
        {
            Assert.Equal(expected[0], actual[0], precision);
            Assert.Equal(expected[1], actual[1], precision);
            Assert.Equal(expected[2], actual[2], precision);
        }

        // ---- InboundAsymptoteDir (salvaged from #983; re-covered here) ----

        [Fact]
        public void InboundAsymptoteDir_NullOrNonHyperbolic_ReturnsNull()
        {
            var e = new[] { 1.0, 0.0, 0.0 };
            var h = new[] { 0.0, 0.0, 1.0 };
            Assert.Null(ReaimArrivalGeometry.InboundAsymptoteDir(null, h, 1.5));
            Assert.Null(ReaimArrivalGeometry.InboundAsymptoteDir(e, null, 1.5));
            Assert.Null(ReaimArrivalGeometry.InboundAsymptoteDir(e, h, 1.0));   // parabolic, no real asymptote
            Assert.Null(ReaimArrivalGeometry.InboundAsymptoteDir(e, h, 0.5));   // elliptic
            Assert.Null(ReaimArrivalGeometry.InboundAsymptoteDir(e, h, double.NaN));
        }

        [Fact]
        public void InboundAsymptoteDir_KnownConic_ReturnsUnitVectorInPlane()
        {
            // Periapsis along +x, plane normal along +z => q_hat = h_hat x e_hat = +y. The asymptote is a
            // unit vector in the x-y plane with the analytic radial/tangential split.
            var e = new[] { 2.0, 0.0, 0.0 };   // periapsis +x (magnitude normalized away)
            var h = new[] { 0.0, 0.0, 3.0 };   // plane normal +z
            double ecc = 1.5;
            var s = ReaimArrivalGeometry.InboundAsymptoteDir(e, h, ecc);
            Assert.NotNull(s);
            Assert.Equal(1.0, ReaimArrivalGeometry.Magnitude(s), 9);
            Assert.Equal(0.0, s[2], 9);
            double radial = Math.Sqrt(1.0 - 1.0 / (ecc * ecc));
            double tangential = ecc - 1.0 / ecc;
            double norm = Math.Sqrt(radial * radial + tangential * tangential);
            Assert.Equal(radial / norm, s[0], 9);
            Assert.Equal(tangential / norm, s[1], 9);
        }

        [Fact]
        public void InboundAsymptoteDir_HighEccLimit_TendsToQHat()
        {
            var e = new[] { 1.0, 0.0, 0.0 };
            var h = new[] { 0.0, 0.0, 1.0 };
            var s = ReaimArrivalGeometry.InboundAsymptoteDir(e, h, 1.0e6);
            Assert.NotNull(s);
            Assert.True(s[1] > 0.9999, "high-ecc asymptote should be nearly +q_hat (+y)");
            Assert.True(Math.Abs(s[0]) < 1.0e-3, "radial component vanishes at high ecc");
        }

        // ---- HyperbolicExcessSpeed: |v_inf| = sqrt(mu / (-sma)) ----

        [Fact]
        public void HyperbolicExcessSpeed_KnownHyperbola_MatchesFormula()
        {
            // mu = 1e11, sma = -2.5e8 (hyperbolic, sma < 0) => |v_inf| = sqrt(1e11 / 2.5e8) = sqrt(400) = 20.
            double mag = ReaimArrivalGeometry.HyperbolicExcessSpeed(1.0e11, -2.5e8);
            Assert.Equal(20.0, mag, 6);
        }

        [Fact]
        public void HyperbolicExcessSpeed_NonHyperbolicOrDegenerate_ReturnsNaN()
        {
            Assert.True(double.IsNaN(ReaimArrivalGeometry.HyperbolicExcessSpeed(1.0e11, 2.5e8)));  // sma > 0 (ellipse)
            Assert.True(double.IsNaN(ReaimArrivalGeometry.HyperbolicExcessSpeed(1.0e11, 0.0)));    // sma == 0
            Assert.True(double.IsNaN(ReaimArrivalGeometry.HyperbolicExcessSpeed(0.0, -2.5e8)));    // mu == 0
            Assert.True(double.IsNaN(ReaimArrivalGeometry.HyperbolicExcessSpeed(double.NaN, -2.5e8)));
            Assert.True(double.IsNaN(ReaimArrivalGeometry.HyperbolicExcessSpeed(1.0e11, double.NaN)));
        }

        // ---- SemiMajorAxisFromState: vis-viva 1/a = 2/r - v^2/mu ----

        [Fact]
        public void SemiMajorAxisFromState_KnownHyperbolicState_NegativeSma()
        {
            // mu = 1e11. r = 1e6 along +x, choose v^2 so 1/a = 2/r - v^2/mu is negative (hyperbolic).
            // 2/r = 2e-6. Pick v = 600 along +y => v^2 = 3.6e5, v^2/mu = 3.6e-6. 1/a = 2e-6 - 3.6e-6 = -1.6e-6
            // => a = -625000.
            var r = new[] { 1.0e6, 0.0, 0.0 };
            var v = new[] { 0.0, 600.0, 0.0 };
            double sma = ReaimArrivalGeometry.SemiMajorAxisFromState(r, v, 1.0e11);
            Assert.Equal(-625000.0, sma, 0);
            Assert.True(sma < 0.0, "hyperbolic state => negative sma");
        }

        [Fact]
        public void SemiMajorAxisFromState_KnownEllipticState_PositiveSma()
        {
            // Circular orbit at r => v^2 = mu/r, 1/a = 2/r - 1/r = 1/r => a = r (positive).
            double mu = 1.0e11, r = 1.0e6;
            double vCirc = Math.Sqrt(mu / r);
            var rVec = new[] { r, 0.0, 0.0 };
            var vVec = new[] { 0.0, vCirc, 0.0 };
            double sma = ReaimArrivalGeometry.SemiMajorAxisFromState(rVec, vVec, mu);
            Assert.Equal(r, sma, 0);
        }

        [Fact]
        public void SemiMajorAxisFromState_Degenerate_ReturnsNaN()
        {
            var r = new[] { 0.0, 0.0, 0.0 };
            var v = new[] { 0.0, 600.0, 0.0 };
            Assert.True(double.IsNaN(ReaimArrivalGeometry.SemiMajorAxisFromState(r, v, 1.0e11)));   // r == 0
            Assert.True(double.IsNaN(ReaimArrivalGeometry.SemiMajorAxisFromState(
                new[] { 1.0e6, 0.0, 0.0 }, v, 0.0)));                                                // mu == 0
            Assert.True(double.IsNaN(ReaimArrivalGeometry.SemiMajorAxisFromState(null, v, 1.0e11)));
        }

        // ---- EccentricityVectorFromState (candidate (e) from (r_rel, v_rel)) ----

        [Fact]
        public void EccentricityVectorFromState_PeriapsisState_PointsAlongRadial()
        {
            // At periapsis, r and v are perpendicular and the eccentricity vector points along +r (toward
            // periapsis). Build a hyperbolic periapsis state: r = rp * x_hat, v = vp * y_hat with vp chosen
            // so e > 1. mu = 1e11, rp = 1e6. For e: e = rp*vp^2/mu - 1 (periapsis). Pick vp = 600 =>
            // e = 1e6*3.6e5/1e11 - 1 = 3.6 - 1 = 2.6, along +x.
            double mu = 1.0e11, rp = 1.0e6, vp = 600.0;
            var r = new[] { rp, 0.0, 0.0 };
            var v = new[] { 0.0, vp, 0.0 };
            double[] eVec = ReaimArrivalGeometry.EccentricityVectorFromState(r, v, mu);
            Assert.NotNull(eVec);
            double ecc = ReaimArrivalGeometry.Magnitude(eVec);
            Assert.Equal(2.6, ecc, 6);
            // Direction along +x (periapsis), no y/z.
            var eHat = ReaimArrivalGeometry.Normalize(eVec);
            AssertVecEqual(new[] { 1.0, 0.0, 0.0 }, eHat, 6);
        }

        [Fact]
        public void EccentricityVectorFromState_Degenerate_ReturnsNull()
        {
            var r = new[] { 0.0, 0.0, 0.0 };
            var v = new[] { 0.0, 600.0, 0.0 };
            Assert.Null(ReaimArrivalGeometry.EccentricityVectorFromState(r, v, 1.0e11)); // r == 0
            Assert.Null(ReaimArrivalGeometry.EccentricityVectorFromState(
                new[] { 1.0e6, 0.0, 0.0 }, v, 0.0));                                     // mu == 0
        }

        // ---- InboundVInfVector: magnitude * direction ----

        [Fact]
        public void InboundVInfVector_KnownHyperbola_MagnitudeTimesAsymptote()
        {
            // Periapsis-state hyperbola from the EccVector test: e = 2.6 along +x, plane normal +z. sma via
            // vis-viva: 1/a = 2/rp - vp^2/mu = 2e-6 - 3.6e-6 = -1.6e-6 => a = -625000. |v_inf| = sqrt(mu/-a)
            // = sqrt(1e11/625000) = sqrt(160000) = 400.
            double mu = 1.0e11, rp = 1.0e6, vp = 600.0;
            var r = new[] { rp, 0.0, 0.0 };
            var v = new[] { 0.0, vp, 0.0 };
            double[] eVec = ReaimArrivalGeometry.EccentricityVectorFromState(r, v, mu);
            double[] hVec = ReaimArrivalGeometry.Cross(r, v); // +z direction
            double sma = ReaimArrivalGeometry.SemiMajorAxisFromState(r, v, mu);

            double[] vInf = ReaimArrivalGeometry.InboundVInfVector(eVec, hVec, sma, mu);
            Assert.NotNull(vInf);
            double mag = ReaimArrivalGeometry.Magnitude(vInf);
            Assert.Equal(400.0, mag, 3);

            // Direction must match the analytic asymptote built from (e, h).
            double ecc = ReaimArrivalGeometry.Magnitude(eVec);
            double[] dir = ReaimArrivalGeometry.InboundAsymptoteDir(eVec, hVec, ecc);
            var vInfHat = ReaimArrivalGeometry.Normalize(vInf);
            AssertVecEqual(dir, vInfHat, 9);
        }

        [Fact]
        public void InboundVInfVector_NonHyperbolic_ReturnsNull()
        {
            // Circular (elliptic) state => no inbound asymptote, no excess speed => null.
            double mu = 1.0e11, r = 1.0e6;
            double vCirc = Math.Sqrt(mu / r);
            var rVec = new[] { r, 0.0, 0.0 };
            var vVec = new[] { 0.0, vCirc, 0.0 };
            double[] eVec = ReaimArrivalGeometry.EccentricityVectorFromState(rVec, vVec, mu);
            double[] hVec = ReaimArrivalGeometry.Cross(rVec, vVec);
            double sma = ReaimArrivalGeometry.SemiMajorAxisFromState(rVec, vVec, mu);
            Assert.Null(ReaimArrivalGeometry.InboundVInfVector(eVec, hVec, sma, mu));
        }

        // ---- VInfMismatch objective: minimized when candidate matches recorded ----

        [Fact]
        public void VInfMismatch_ZeroWhenIdentical_GrowsWithDifference()
        {
            var rec = new[] { 700.0, 100.0, 0.0 };
            Assert.Equal(0.0, ReaimArrivalGeometry.VInfMismatch(new[] { 700.0, 100.0, 0.0 }, rec), 9);
            // A worse MAGNITUDE (same direction, larger speed) scores higher.
            double worseMag = ReaimArrivalGeometry.VInfMismatch(new[] { 770.0, 110.0, 0.0 }, rec);
            Assert.True(worseMag > 0.0);
            // A worse DIRECTION (same magnitude, rotated) scores higher than perfect.
            double recMag = ReaimArrivalGeometry.Magnitude(rec);
            // Rotate rec 10 deg in the x-y plane, same magnitude.
            double ang = 10.0 * Math.PI / 180.0;
            var rotated = new[]
            {
                rec[0] * Math.Cos(ang) - rec[1] * Math.Sin(ang),
                rec[0] * Math.Sin(ang) + rec[1] * Math.Cos(ang),
                0.0
            };
            double worseDir = ReaimArrivalGeometry.VInfMismatch(rotated, rec);
            Assert.True(worseDir > 0.0);
        }

        [Fact]
        public void VInfMismatch_PicksClosestCandidate()
        {
            // The objective is the selection score: among candidates, the one closest to recorded wins.
            var rec = new[] { 700.0, 0.0, 0.0 };
            var near = new[] { 710.0, 5.0, 0.0 };
            var far = new[] { 600.0, 80.0, 0.0 };
            double mNear = ReaimArrivalGeometry.VInfMismatch(near, rec);
            double mFar = ReaimArrivalGeometry.VInfMismatch(far, rec);
            Assert.True(mNear < mFar, "the candidate nearer the recorded v_inf must score lower");
        }

        [Fact]
        public void VInfMismatch_NullInput_ReturnsNaN()
        {
            Assert.True(double.IsNaN(ReaimArrivalGeometry.VInfMismatch(null, new[] { 1.0, 0.0, 0.0 })));
            Assert.True(double.IsNaN(ReaimArrivalGeometry.VInfMismatch(new[] { 1.0, 0.0, 0.0 }, null)));
        }

        [Fact]
        public void AngleBetweenDegrees_KnownAngles()
        {
            Assert.Equal(0.0, ReaimArrivalGeometry.AngleBetweenDegrees(
                new[] { 1.0, 0.0, 0.0 }, new[] { 2.0, 0.0, 0.0 }), 6);
            Assert.Equal(90.0, ReaimArrivalGeometry.AngleBetweenDegrees(
                new[] { 1.0, 0.0, 0.0 }, new[] { 0.0, 3.0, 0.0 }), 6);
            Assert.Equal(180.0, ReaimArrivalGeometry.AngleBetweenDegrees(
                new[] { 1.0, 0.0, 0.0 }, new[] { -5.0, 0.0, 0.0 }), 6);
            Assert.True(double.IsNaN(ReaimArrivalGeometry.AngleBetweenDegrees(
                new[] { 0.0, 0.0, 0.0 }, new[] { 1.0, 0.0, 0.0 })));
        }

        // ---- Decline-to-faithful gate ----

        [Fact]
        public void AcceptChosenOverFaithful_AcceptsWhenStrictlyBetterAndUnderTolerance()
        {
            // chosen seam 1e7, faithful seam 3e7, SOI 5e7, 0.25*SOI = 1.25e7. chosen < faithful AND
            // chosen < 1.25e7 => accept.
            Assert.True(ReaimArrivalGeometry.AcceptChosenOverFaithful(1.0e7, 3.0e7, 5.0e7, 0.25));
        }

        [Fact]
        public void AcceptChosenOverFaithful_DeclinesWhenNotStrictlyBetter()
        {
            // chosen seam equals faithful => not strictly better => decline.
            Assert.False(ReaimArrivalGeometry.AcceptChosenOverFaithful(2.0e7, 2.0e7, 5.0e8, 0.25));
            // chosen seam larger than faithful => decline.
            Assert.False(ReaimArrivalGeometry.AcceptChosenOverFaithful(3.0e7, 2.0e7, 5.0e8, 0.25));
        }

        [Fact]
        public void AcceptChosenOverFaithful_DeclinesWhenOverTolerance()
        {
            // chosen seam 2e7 < faithful 4e7 (strictly better), but SOI 5e7 => 0.25*SOI = 1.25e7, and
            // 2e7 > 1.25e7 => over tolerance => decline.
            Assert.False(ReaimArrivalGeometry.AcceptChosenOverFaithful(2.0e7, 4.0e7, 5.0e7, 0.25));
        }

        [Fact]
        public void AcceptChosenOverFaithful_NonFiniteInputs_FailClosed()
        {
            Assert.False(ReaimArrivalGeometry.AcceptChosenOverFaithful(double.NaN, 3.0e7, 5.0e7, 0.25));
            Assert.False(ReaimArrivalGeometry.AcceptChosenOverFaithful(1.0e7, double.NaN, 5.0e7, 0.25));
            Assert.False(ReaimArrivalGeometry.AcceptChosenOverFaithful(1.0e7, 3.0e7, 0.0, 0.25));
            Assert.False(ReaimArrivalGeometry.AcceptChosenOverFaithful(1.0e7, 3.0e7, 5.0e7, 0.0));
        }

        // ---- Destination-leg byte-identical regression (the non-regression guard) ----
        //
        // The timing path and the faithful path BOTH assemble the window via ReplaceHeliocentricLeg; the
        // ONLY difference is which transfer OrbitSegment replaces the heliocentric leg. The recorded arrival
        // / capture / descent segments (the destination leg) must pass through byte-identical regardless of
        // which transfer is chosen. This pins that invariant: assemble a window with a "timed" transfer and
        // again with a "faithful" transfer, and assert every non-common-ancestor segment is byte-identical.

        private static OrbitSegment Seg(string body, double startUT, double endUT, double sma,
            double ecc = 0.1, double inc = 5.0, double lan = 30.0, double argPe = 40.0,
            double mEp = 1.0, double epoch = 0.0, bool predicted = false)
        {
            return new OrbitSegment
            {
                startUT = startUT, endUT = endUT,
                inclination = inc, eccentricity = ecc, semiMajorAxis = sma,
                longitudeOfAscendingNode = lan, argumentOfPeriapsis = argPe,
                meanAnomalyAtEpoch = mEp, epoch = epoch,
                bodyName = body, isPredicted = predicted
            };
        }

        private static void AssertSegBitIdentical(OrbitSegment a, OrbitSegment b)
        {
            Assert.Equal(a.startUT, b.startUT);
            Assert.Equal(a.endUT, b.endUT);
            Assert.Equal(a.inclination, b.inclination);
            Assert.Equal(a.eccentricity, b.eccentricity);
            Assert.Equal(a.semiMajorAxis, b.semiMajorAxis);
            Assert.Equal(a.longitudeOfAscendingNode, b.longitudeOfAscendingNode);
            Assert.Equal(a.argumentOfPeriapsis, b.argumentOfPeriapsis);
            Assert.Equal(a.meanAnomalyAtEpoch, b.meanAnomalyAtEpoch);
            Assert.Equal(a.epoch, b.epoch);
            Assert.Equal(a.bodyName, b.bodyName);
            Assert.Equal(a.isPredicted, b.isPredicted);
            Assert.Equal(a.orbitalFrameRotation, b.orbitalFrameRotation);
            Assert.Equal(a.angularVelocity, b.angularVelocity);
        }

        [Fact]
        public void DestinationLeg_ByteIdentical_BetweenTimedAndFaithfulTransfer()
        {
            const string ancestor = "Sun", launch = "Kerbin", target = "Duna";
            double depUT = 1000.0, arrUT = 5000.0;
            var member = new List<OrbitSegment>
            {
                Seg(launch, 0.0, 1000.0, 9.0e6),        // parking orbit (launch body, pre-departure)
                Seg(ancestor, 1000.0, 5000.0, 2.0e10),  // heliocentric leg (the only thing replaced)
                Seg(target, 5000.0, 6000.0, -3.0e7, ecc: 1.4),  // arrival hyperbola (destination leg)
                Seg(target, 6000.0, 9000.0, 8.0e5, ecc: 0.05),  // capture / loiter (destination leg)
                Seg(target, 9000.0, 9500.0, 6.0e5, ecc: 0.0),   // low-orbit descent (destination leg)
            };

            // Two DIFFERENT transfer segments (the timed pick vs the faithful pick differ only here).
            var timedTransfer = Seg(ancestor, 0.0, 0.0, 2.05e10, ecc: 0.12, inc: 0.4, lan: 12.0, argPe: 88.0);
            var faithfulTransfer = Seg(ancestor, 0.0, 0.0, 2.00e10, ecc: 0.10, inc: 0.6, lan: 20.0, argPe: 90.0);

            List<OrbitSegment> timed = ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                member, timedTransfer, ancestor, depUT, arrUT, double.NaN, double.NaN);
            List<OrbitSegment> faithful = ReaimSegmentAssembler.ReplaceHeliocentricLeg(
                member, faithfulTransfer, ancestor, depUT, arrUT, double.NaN, double.NaN);

            Assert.NotNull(timed);
            Assert.NotNull(faithful);
            Assert.Equal(timed.Count, faithful.Count);

            // Every NON-ancestor (destination + launch) segment must be byte-identical between the two
            // assembled lists. The only segment allowed to differ is the heliocentric (ancestor) transfer.
            int destChecked = 0;
            for (int i = 0; i < timed.Count; i++)
            {
                if (timed[i].bodyName == ancestor)
                {
                    Assert.Equal(ancestor, faithful[i].bodyName);
                    continue; // the transfer slot is allowed to differ
                }
                AssertSegBitIdentical(timed[i], faithful[i]);
                destChecked++;
            }
            Assert.True(destChecked >= 4, "must have checked the parking + 3 destination-leg segments");
        }
    }
}
