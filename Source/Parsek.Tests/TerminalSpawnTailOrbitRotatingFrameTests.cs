using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;
using TwoBodyOrbit = Parsek.BallisticExtrapolator.TwoBodyOrbit;

namespace Parsek.Tests
{
    /// <summary>
    /// TERMINAL-SPAWN-TAIL-ORBIT-SURFACE-VELOCITY (LAB-7 runs 2026-10-17_2308, _2318_a2 and
    /// 2026-10-18_0026). A recorded <c>TrajectoryPoint.velocity</c> is a Unity world vector:
    /// surface-relative while the world frame co-rotates with the body (below the
    /// inverse-rotation altitude), and expressed in world axes whose yaw about the spin axis
    /// depends on session history. Seeding a spawn orbit from it as if it were inertial and in
    /// this session's axes refused a 92.5 x 107 km lab twice: sma 596266 / ecc 0.1622 in the
    /// recording session, and ecc 0.9996 after a reload once only the magnitude was corrected.
    ///
    /// These cells drive <see cref="RecordedTailVelocityResolver"/> with LAB-7's own recorded
    /// tail (run 2026-10-18_0026, recording 9a633382...), rebuilt at several spawn-session
    /// yaws, plus synthetic tails recorded at several record-session yaws, so a frame or yaw
    /// mistake changes the derived orbit and fails them. Elements come from Parsek's own pure
    /// two-body solver, the verdict from the pure spawn-safety decision. The Unity-bound
    /// wiring is the in-game cell
    /// <c>RuntimeTests.TerminalOrbitFromTail_SurfaceRelativeLowOrbitTail_DerivesInertialOrbit</c>.
    /// </summary>
    public class TerminalSpawnTailOrbitRotatingFrameTests
    {
        // Stock Kerbin.
        private const double KerbinRadius = 600000.0;
        private const double KerbinMu = 3.5316e12;
        private const double KerbinRotationPeriod = 21549.425;
        private const double KerbinAtmosphereDepth = 70000.0;

        // The live vessel's stock orbit (KSP.log FinalizeIndividualRecording orbit[...]).
        private const double Lab7StockSma = 699745.8;
        private const double Lab7StockEcc = 0.0104;

        // LAB-7 run 2026-10-18_0026, the two last frames of the post-off-rails coast
        // (alt ~93 km: co-rotating world frame), verbatim from the .prec.
        private static readonly TrajectoryPoint Lab7TailPrevious = Point(
            3603.6371992490303, 0.035418600818591404, 143.24227504449607, 92975.675679183914,
            -1346.17859f, -3.260317f, 1567.64148f);
        private static readonly TrajectoryPoint Lab7Tail = Point(
            3606.6571992490276, 0.034604062892553709, 143.75824437126903, 92950.6151695021,
            -1360.13708f, -3.27388883f, 1555.66357f);

        // The same recording's frames before it went on rails (alt ~102 km: inertial).
        private static readonly TrajectoryPoint Lab7HeadPrevious = Point(
            396.27153320300755, -0.085024487764743459, -31.62762898862206, 102205.9996079948,
            558.0175f, 1.06258845f, -2168.006f);
        private static readonly TrajectoryPoint Lab7Head = Point(
            397.03153320300686, -0.084956339393823119, -31.501512115159233, 102222.57465538604,
            563.2737f, 1.07066262f, -2166.5918f);

        // ---------- the LAB-7 failure shape ----------

        [Theory]
        [InlineData(0.0)]
        [InlineData(57.3)]
        [InlineData(-120.0)]
        [InlineData(179.9)]
        public void Lab7RecordedTail_ResolvesToTheStockOrbitAtAnySpawnYaw(double spawnYawDegrees)
        {
            Assert.True(TryResolvePair(Lab7TailPrevious, Lab7Tail, spawnYawDegrees,
                out RecordedTailVelocityResolution r, out Vector3d tailPos));

            Assert.True(r.SampleWasRotatingFrame, "a 93 km active-vessel sample is surface-relative");
            Assert.InRange(r.Residual, 0.0, 0.1);           // measured 0.014 m/s
            Assert.InRange(r.OtherResidual, 150.0, 260.0);  // the inertial reading misses by ~omega*r
            Assert.Equal(2066.42, r.RecordedSpeed, 2);

            TwoBodyOrbit orbit = Solve(tailPos, r.InertialVelocityYup);
            Assert.InRange(orbit.SemiMajorAxis, Lab7StockSma - 200.0, Lab7StockSma + 200.0);
            Assert.InRange(orbit.Eccentricity, Lab7StockEcc - 0.001, Lab7StockEcc + 0.001);
            // Prograde equatorial (stock inc 0.09 deg); a yaw left in the velocity tilts it.
            Assert.InRange(orbit.Inclination * 180.0 / Math.PI, 0.0, 0.5);

            var decision = EvaluateSpawn(orbit, Lab7Tail.altitude);
            Assert.Equal(TerminalOrbitSpawnSafetyAction.SpawnNow, decision.Action);
        }

        [Fact]
        public void Lab7RecordedTail_SingleVectorSeedDependsOnTheSessionYaw()
        {
            // What the spawn path did before: the tail's recorded vector paired with the
            // rebuilt position, read as inertial and in this session's axes. Its orbit changes
            // with the spawn session's yaw, and at the recording session's own yaw
            // (257.18 deg in this model, fitted from the pair) it is the refused 596 km
            // ellipse the 2026-10-17_2308 log printed.
            Vector3d recorded = Vel(Lab7Tail);
            var eccentricities = new List<double>();
            foreach (double spawnYaw in new[] { 0.0, 90.0, 180.0, 257.1783 })
            {
                Vector3d pos = Rebuild(Lab7Tail, spawnYaw);
                TwoBodyOrbit orbit = Solve(pos, recorded);
                Assert.Equal(596266.0, orbit.SemiMajorAxis, 0);   // vis-viva: |v| alone
                eccentricities.Add(orbit.Eccentricity);
            }

            Assert.True(eccentricities[0] > 0.5, $"ecc at yaw 0 = {eccentricities[0]:F4}");
            Assert.InRange(eccentricities[3], 0.1617, 0.1627);   // log: ecc=0.1622
            var refused = EvaluateSpawn(Solve(Rebuild(Lab7Tail, 257.1783), recorded), Lab7Tail.altitude);
            Assert.Equal(TerminalOrbitSpawnSafetyAction.CannotSpawnSafely, refused.Action);
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonPeriapsisBelowSafeAltitude, refused.ReasonCode);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-75.0)]
        public void Lab7RecordedHead_AboveTheThreshold_ResolvesAsInertial(double spawnYawDegrees)
        {
            Assert.True(TryResolvePair(Lab7HeadPrevious, Lab7Head, spawnYawDegrees,
                out RecordedTailVelocityResolution r, out Vector3d tailPos));

            Assert.False(r.SampleWasRotatingFrame, "a 102 km sample is inertial");
            Assert.InRange(r.Residual, 0.0, 0.1);           // measured 0.001 m/s
            Assert.InRange(r.OtherResidual, 150.0, 260.0);
            // Inertial vectors keep their magnitude exactly.
            Assert.Equal(Vel(Lab7Head).magnitude, r.InertialVelocityYup.magnitude, 6);

            TwoBodyOrbit orbit = Solve(tailPos, r.InertialVelocityYup);
            Assert.InRange(orbit.SemiMajorAxis, Lab7StockSma - 200.0, Lab7StockSma + 200.0);
            Assert.InRange(orbit.Eccentricity, Lab7StockEcc - 0.001, Lab7StockEcc + 0.001);
        }

        // ---------- synthetic: record yaw x spawn yaw x frame ----------

        [Theory]
        [InlineData(true, 0.0, 0.0)]
        [InlineData(true, 123.0, 0.0)]
        [InlineData(true, -40.0, 200.0)]
        [InlineData(false, 0.0, 0.0)]
        [InlineData(false, 37.0, -90.0)]
        [InlineData(false, -170.0, 15.0)]
        public void SyntheticTail_RecoversTheInertialVelocity(
            bool recordedInRotatingFrame, double recordYawDegrees, double spawnYawDegrees)
        {
            // Truth in body-fixed axes: a 30 deg inclined, slightly eccentric orbit point at
            // 95 km with some radial rate.
            double r = KerbinRadius + 95000.0;
            Vector3d posBf = BodyFixed(12.0, 40.0, 95000.0);
            Vector3d radialBf = posBf.normalized;
            Vector3d eastBf = Vector3d.Cross(new Vector3d(0, 1, 0), radialBf).normalized * -1.0;
            Vector3d northBf = Vector3d.Cross(radialBf, eastBf).normalized;
            Vector3d inertialBf = eastBf * 2000.0 + northBf * 900.0 + radialBf * 35.0;

            Vector3d spinBf = OrbitReseed.ComputeRotatingFrameVelocityYup(posBf, KerbinRotationPeriod);
            Vector3d surfaceBf = inertialBf - spinBf;
            Vector3d previousBf = posBf - surfaceBf * 1.0;
            Vector3d recordedBf = recordedInRotatingFrame ? surfaceBf : inertialBf;
            Vector3d recorded = RecordedTailVelocityResolver.RotateAboutY(
                recordedBf, -recordYawDegrees * Math.PI / 180.0);

            double spawnYaw = spawnYawDegrees * Math.PI / 180.0;
            Vector3d tailPos = RecordedTailVelocityResolver.RotateAboutY(posBf, spawnYaw);
            Vector3d previousPos = RecordedTailVelocityResolver.RotateAboutY(previousBf, spawnYaw);

            Assert.True(RecordedTailVelocityResolver.TryResolve(
                previousPos, 101.0, recorded, tailPos, 102.0, recorded,
                KerbinRotationPeriod, preferRotatingOnTie: false, out RecordedTailVelocityResolution res),
                res.DeclineReason);
            Assert.Equal(recordedInRotatingFrame, res.SampleWasRotatingFrame);

            Vector3d expected = RecordedTailVelocityResolver.RotateAboutY(inertialBf, spawnYaw);
            Assert.InRange((res.InertialVelocityYup - expected).magnitude, 0.0, 0.5);
            Assert.InRange(Math.Abs(r - tailPos.magnitude), 0.0, 1e-6);
        }

        // ---------- declines ----------

        [Fact]
        public void ResolverDeclines_PairGapOutOfRange()
        {
            Vector3d v = new Vector3d(2000, 0, 0);
            Vector3d p = new Vector3d(0, 0, 690000);
            Assert.False(RecordedTailVelocityResolver.TryResolve(
                p - v * 31.0, 0.0, v, p, 31.0, v, KerbinRotationPeriod, false, out var tooLong));
            Assert.Equal(RecordedTailVelocityResolver.DeclinePairGap, tooLong.DeclineReason);
            Assert.False(RecordedTailVelocityResolver.TryResolve(
                p, 5.0, v, p, 5.0, v, KerbinRotationPeriod, false, out var zero));
            Assert.Equal(RecordedTailVelocityResolver.DeclinePairGap, zero.DeclineReason);
        }

        [Fact]
        public void ResolverDeclines_NonFiniteInput()
        {
            Vector3d v = new Vector3d(2000, 0, 0);
            Assert.False(RecordedTailVelocityResolver.TryResolve(
                Vector3d.zero, 0.0, v, new Vector3d(double.NaN, 0, 0), 1.0, v,
                KerbinRotationPeriod, false, out var r));
            Assert.Equal(RecordedTailVelocityResolver.DeclineNonFinite, r.DeclineReason);
        }

        [Fact]
        public void ResolverDeclines_VelocityAlongTheSpinAxisHasNoAzimuth()
        {
            // Over the pole, moving along world Y: no XZ azimuth to fit on either side.
            Vector3d p = new Vector3d(0, 690000, 0);
            Vector3d v = new Vector3d(0, 2200, 0);
            Assert.False(RecordedTailVelocityResolver.TryResolve(
                p - v, 0.0, v, p, 1.0, v, KerbinRotationPeriod, false, out var r));
            Assert.Equal(RecordedTailVelocityResolver.DeclineAzimuthUndefined, r.DeclineReason);
        }

        [Fact]
        public void ResolverDeclines_RecordedVectorsThatFitNeitherFrame()
        {
            // The recorded speed is 30% off the chord in both readings (a packed / mixed /
            // corrupt sample): the tail must yield to the stored seeds, not guess.
            Vector3d tailPos = Rebuild(Lab7Tail, 0.0);
            Vector3d prevPos = Rebuild(Lab7TailPrevious, 0.0);
            Assert.False(RecordedTailVelocityResolver.TryResolve(
                prevPos, Lab7TailPrevious.ut, Vel(Lab7TailPrevious) * 1.3,
                tailPos, Lab7Tail.ut, Vel(Lab7Tail) * 1.3,
                KerbinRotationPeriod, false, out var r));
            Assert.Equal(RecordedTailVelocityResolver.DeclineNoFit, r.DeclineReason);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ResolverTie_FollowsThePrior(bool prior)
        {
            // No usable rotation period: both readings are the same fit.
            Vector3d v = new Vector3d(1500, 0, 1500);
            Vector3d p = new Vector3d(690000, 0, 0);
            Assert.True(RecordedTailVelocityResolver.TryResolve(
                p - v, 0.0, v, p, 1.0, v, double.NaN, prior, out var r));
            Assert.Equal(prior, r.SampleWasRotatingFrame);
        }

        // ---------- primitives ----------

        [Fact]
        public void RotateAboutY_KeepsMagnitudeAndSpinComponentAndAddsTheYaw()
        {
            Vector3d v = new Vector3d(3.0, -7.0, 4.0);
            Vector3d rotated = RecordedTailVelocityResolver.RotateAboutY(v, 0.7);
            Assert.Equal(v.magnitude, rotated.magnitude, 12);
            Assert.Equal(v.y, rotated.y, 12);
            Assert.Equal(Math.Atan2(v.z, v.x) + 0.7, Math.Atan2(rotated.z, rotated.x), 12);
        }

        [Theory]
        [InlineData(true, 100000.0, 92950.7, true)]
        [InlineData(true, 100000.0, 100000.0, false)]   // stock rule is strict <
        [InlineData(true, 100000.0, 203587.0, false)]
        [InlineData(false, 100000.0, 92950.7, false)]
        [InlineData(true, 0.0, 92950.7, false)]
        [InlineData(true, double.NaN, 92950.7, false)]
        [InlineData(true, 100000.0, double.NaN, false)]
        public void IsRecordedVelocityInRotatingFrame_FollowsStockReferenceFrameRule(
            bool bodyRotates, double threshold, double altitude, bool expected)
        {
            Assert.Equal(
                expected,
                OrbitReseed.IsRecordedVelocityInRotatingFrame(bodyRotates, threshold, altitude));
        }

        [Fact]
        public void RotatingFrameVelocity_IsStockGetRFrmVel()
        {
            double r = KerbinRadius + 92950.7;
            Vector3d frameVel = OrbitReseed.ComputeRotatingFrameVelocityYup(
                new Vector3d(r, 0.0, 0.0), KerbinRotationPeriod);
            double omegaR = 2.0 * Math.PI / KerbinRotationPeriod * r;
            Assert.Equal(202.04, omegaR, 2);
            AssertVector(new Vector3d(0.0, 0.0, omegaR), frameVel, 6);
            AssertVector(Vector3d.zero,
                OrbitReseed.ComputeRotatingFrameVelocityYup(new Vector3d(0.0, r, 0.0), KerbinRotationPeriod), 9);
            AssertVector(Vector3d.zero,
                OrbitReseed.ComputeRotatingFrameVelocityYup(new Vector3d(r, 0.0, 0.0), 0.0), 9);
        }

        // ---------- helpers ----------

        private static bool TryResolvePair(
            TrajectoryPoint previous, TrajectoryPoint tail, double spawnYawDegrees,
            out RecordedTailVelocityResolution resolution, out Vector3d tailPos)
        {
            tailPos = Rebuild(tail, spawnYawDegrees);
            Vector3d previousPos = Rebuild(previous, spawnYawDegrees);
            return RecordedTailVelocityResolver.TryResolve(
                previousPos, previous.ut, Vel(previous),
                tailPos, tail.ut, Vel(tail),
                KerbinRotationPeriod, preferRotatingOnTie: false, out resolution);
        }

        // Body-fixed lat/lon/alt to Y-up axes (spin axis +Y), the handedness KSP's surface
        // positions have: LAB-7's recorded vectors fit it by a pure yaw to 0.014 m/s.
        private static Vector3d BodyFixed(double lat, double lon, double alt)
        {
            double r = KerbinRadius + alt;
            double la = lat * Math.PI / 180.0;
            double lo = lon * Math.PI / 180.0;
            return new Vector3d(r * Math.Cos(la) * Math.Cos(lo), r * Math.Sin(la), r * Math.Cos(la) * Math.Sin(lo));
        }

        // A spawn session's GetWorldSurfacePosition - body.position: body-fixed axes at
        // that session's yaw.
        private static Vector3d Rebuild(TrajectoryPoint p, double spawnYawDegrees)
        {
            return RecordedTailVelocityResolver.RotateAboutY(
                BodyFixed(p.latitude, p.longitude, p.altitude), spawnYawDegrees * Math.PI / 180.0);
        }

        private static Vector3d Vel(TrajectoryPoint p)
        {
            return new Vector3d(p.velocity.x, p.velocity.y, p.velocity.z);
        }

        private static TrajectoryPoint Point(
            double ut, double lat, double lon, double alt, float vx, float vy, float vz)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                rotation = Quaternion.identity,
                velocity = new Vector3(vx, vy, vz),
                bodyName = "Kerbin"
            };
        }

        // Y-up position / velocity -> Zup, as OrbitReseed hands them to the orbit.
        private static TwoBodyOrbit Solve(Vector3d posYup, Vector3d velYup)
        {
            Assert.True(TwoBodyOrbit.TryCreate(posYup.xzy, velYup.xzy, KerbinMu, 0.0, out TwoBodyOrbit orbit),
                "two-body solve failed");
            return orbit;
        }

        private static TerminalOrbitSpawnSafetyDecision EvaluateSpawn(TwoBodyOrbit orbit, double altitude)
        {
            return TerminalOrbitSpawnSafety.Evaluate(
                currentAltitude: altitude,
                atmosphereDepth: KerbinAtmosphereDepth,
                safetyMargin: TerminalOrbitSpawnSafety.DefaultSafetyMarginMeters,
                periapsisAltitude: orbit.PeriapsisRadius - KerbinRadius,
                apoapsisAltitude: orbit.ApoapsisRadius - KerbinRadius);
        }

        private static void AssertVector(Vector3d expected, Vector3d actual, int precision)
        {
            Assert.Equal(expected.x, actual.x, precision);
            Assert.Equal(expected.y, actual.y, precision);
            Assert.Equal(expected.z, actual.z, precision);
        }
    }
}
