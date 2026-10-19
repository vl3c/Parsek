using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using UnityEngine;
using Xunit;
using TwoBodyOrbit = Parsek.BallisticExtrapolator.TwoBodyOrbit;

namespace Parsek.Tests
{
    /// <summary>
    /// MAP-TAIL-SEED-RECORDED-VELOCITY-FRAME (LAB-7 run 2026-10-18_0217): the map-presence
    /// endpoint-tail seed and the orbit-raise gap glide lift a recorded sample to its
    /// HISTORICAL inertial position (longitude + body rotation phase at the recorded UT) and
    /// used to pair it with the raw recorded velocity, which is surface-relative below the
    /// inverse-rotation altitude and sits in the recording session's world yaw. LAB-7's lab
    /// (stock sma 699745.8 / ecc 0.0104 / inc 0.09) was drawn as a ghost at sma 596266,
    /// ecc 0.9566, inc 179.8. These cells drive the pure core
    /// <see cref="OrbitReseed.TryComputeHistoricalTailPairInputs"/> with LAB-7's recorded
    /// pair at several body phases, a synthetic inertial pair, and the V20 fixture's real
    /// 351 km inertial tail; the lookup is KSP's <c>CelestialBody.GetRelSurfacePosition</c>
    /// (decompiled: <c>Planetarium.SphericalVector(lat, lon).xzy * (Radius + alt)</c>).
    /// </summary>
    [Collection("Sequential")]
    public class MapPresenceTailSeedFrameTests : IDisposable
    {
        private const double KerbinRadius = 600000.0;
        private const double KerbinMu = 3.5316e12;
        private const double KerbinRotationPeriod = 21549.425;
        private const double KerbinInverseRotThreshold = 100000.0;

        private static readonly TrajectoryPoint Lab7TailPrevious = TerminalSpawnTailOrbitRotatingFrameTests.Lab7TailPrevious;
        private static readonly TrajectoryPoint Lab7Tail = TerminalSpawnTailOrbitRotatingFrameTests.Lab7Tail;

        // harness fixture kerbin-return-recorded, recording 6d171d14..., the last two POINTs
        // of its final ExoBallistic Absolute section (351 km: inertial), verbatim from the
        // .prec.txt; the parked craft's stock orbit is sma 3670509.76 / ecc 0.80931 / inc 16.44.
        private static readonly TrajectoryPoint V20TailPrevious = Point(
            60393891.394154139, 16.000597853950808, 142.06308933892177, 347767.89425330644,
            92.61711f, 482.434753f, -2499.792f);
        private static readonly TrajectoryPoint V20Tail = Point(
            60393894.394154631, 16.027172545488039, 142.43144009569394, 351461.95857613836,
            102.450081f, 479.193573f, -2494.23828f);

        private readonly List<string> logLines = new List<string>();

        public MapPresenceTailSeedFrameTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            OrbitSeedResolver.ResetForTesting();
        }

        public void Dispose()
        {
            OrbitSeedResolver.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ---------- the LAB-7 failure shape ----------

        [Theory]
        [InlineData(0.0)]
        [InlineData(90.0)]
        [InlineData(-137.5)]
        [InlineData(211.3)]
        public void Lab7RecordedTail_MapSeedIsTheStockOrbitAtAnyBodyPhase(double initialRotationDeg)
        {
            Assert.True(Compute(Lab7TailPrevious, Lab7Tail, Lab7Tail.ut, initialRotationDeg,
                out OrbitReseed.HistoricalTailPairSeedInputs result), result.FailureReason);

            Assert.True(result.AltitudeRuleRotating);
            Assert.True(result.Resolution.SampleWasRotatingFrame, "a 93 km active-vessel sample is surface-relative");
            Assert.InRange(result.Resolution.Residual, 0.0, 0.1);
            double phase = initialRotationDeg + Lab7Tail.ut * 360.0 / KerbinRotationPeriod;
            Assert.Equal(OrbitReseed.WrapLongitudeDegrees(Lab7Tail.longitude + phase), result.InertialLongitude, 9);
            Assert.Equal(Lab7Tail.ut, result.SeedEpochUT, 9);

            TwoBodyOrbit orbit = SolveZup(result.Inputs);
            Assert.InRange(orbit.SemiMajorAxis,
                TerminalSpawnTailOrbitRotatingFrameTests.Lab7StockSma - 200.0,
                TerminalSpawnTailOrbitRotatingFrameTests.Lab7StockSma + 200.0);
            Assert.InRange(orbit.Eccentricity,
                TerminalSpawnTailOrbitRotatingFrameTests.Lab7StockEcc - 0.001,
                TerminalSpawnTailOrbitRotatingFrameTests.Lab7StockEcc + 0.001);
            // Prograde equatorial (stock inc 0.09 deg); the bug drew it at 179.8.
            Assert.InRange(orbit.Inclination * 180.0 / Math.PI, 0.0, 1.0);
        }

        [Fact]
        public void Lab7RecordedTail_RawVectorAtTheLiftedPosition_IsNotTheStockOrbit()
        {
            // The pre-fix map seed: lifted position, raw recorded vector. |v| alone fixes the
            // logged sma 596266 at every phase; the orbit's shape and sense depend on the phase.
            Vector3d raw = Vel(Lab7Tail);
            foreach (double initialRotationDeg in new[] { 0.0, 90.0, -137.5, 211.3 })
            {
                double phase = initialRotationDeg + Lab7Tail.ut * 360.0 / KerbinRotationPeriod;
                Vector3d lifted = Surface(Lab7Tail.latitude, Lab7Tail.longitude + phase, Lab7Tail.altitude);
                Assert.True(TwoBodyOrbit.TryCreate(lifted.xzy, raw.xzy, KerbinMu, 0.0, out TwoBodyOrbit orbit));
                Assert.Equal(596266.0, orbit.SemiMajorAxis, 0);
                Assert.True(orbit.Eccentricity > 0.1, $"phase {initialRotationDeg}: ecc={orbit.Eccentricity:F4}");
            }

            // At Kerbin's own initialRotation (90 deg) it is the ghost run 2026-10-18_0217 drew:
            // `Created ghost vessel ... sma=596266 ... inc=179.8014`, `tailEcc=0.956639`.
            double kerbinPhase = 90.0 + Lab7Tail.ut * 360.0 / KerbinRotationPeriod;
            Vector3d kerbinLifted = Surface(Lab7Tail.latitude, Lab7Tail.longitude + kerbinPhase, Lab7Tail.altitude);
            Assert.True(TwoBodyOrbit.TryCreate(kerbinLifted.xzy, raw.xzy, KerbinMu, 0.0, out TwoBodyOrbit logged));
            Assert.InRange(logged.Eccentricity, 0.9560, 0.9575);
            Assert.InRange(logged.Inclination * 180.0 / Math.PI, 179.7, 179.9);
        }

        [Theory]
        [InlineData(0.0, 0.0)]
        [InlineData(90.0, 57.3)]
        [InlineData(-137.5, -120.0)]
        [InlineData(211.3, 179.9)]
        public void Lab7RecordedTail_EqualsResolvingTodayAndRotatingToTheLift(
            double initialRotationDeg, double todayYawDegrees)
        {
            // The two-step form: resolve with positions rebuilt in another body orientation
            // ("today", a yaw about the spin axis), then rotate the inertial velocity by the
            // azimuth from today's tail position to the lifted one. The core resolves in the
            // lifted orientation directly; the resolver commutes with a yaw, so they agree.
            Assert.True(Compute(Lab7TailPrevious, Lab7Tail, Lab7Tail.ut, initialRotationDeg,
                out OrbitReseed.HistoricalTailPairSeedInputs result), result.FailureReason);

            double todayYaw = todayYawDegrees * Math.PI / 180.0;
            Vector3d todayTail = RecordedTailVelocityResolver.RotateAboutY(
                Surface(Lab7Tail.latitude, Lab7Tail.longitude, Lab7Tail.altitude), todayYaw);
            Vector3d todayPrevious = RecordedTailVelocityResolver.RotateAboutY(
                Surface(Lab7TailPrevious.latitude, Lab7TailPrevious.longitude, Lab7TailPrevious.altitude), todayYaw);
            Assert.True(RecordedTailVelocityResolver.TryResolve(
                todayPrevious, Lab7TailPrevious.ut, Vel(Lab7TailPrevious),
                todayTail, Lab7Tail.ut, Vel(Lab7Tail),
                KerbinRotationPeriod, preferRotatingOnTie: true, out RecordedTailVelocityResolution today));

            Vector3d liftedTail = result.Inputs.PositionForUpdate.xzy;
            double liftAngle = Math.Atan2(liftedTail.z, liftedTail.x) - Math.Atan2(todayTail.z, todayTail.x);
            Vector3d rotated = RecordedTailVelocityResolver.RotateAboutY(today.InertialVelocityYup, liftAngle);

            Assert.InRange((RecordedTailVelocityResolver.RotateAboutY(todayTail, liftAngle) - liftedTail).magnitude, 0.0, 1e-6);
            Assert.InRange((rotated - result.Inputs.VelocityForUpdate.xzy).magnitude, 0.0, 1e-6);
        }

        // ---------- inertial tails above the threshold ----------

        [Theory]
        [InlineData(0.0, 0.0)]
        [InlineData(37.0, 90.0)]
        [InlineData(-170.0, -45.0)]
        public void SyntheticInertialTail_AboveThreshold_MapSeedRecoversTheOrbit(
            double recordYawDegrees, double initialRotationDeg)
        {
            // Truth: a 30 deg inclined, slightly eccentric state at 150 km (inertial frame),
            // body-fixed axes at the tail UT; the previous sample 2 s earlier on the straight
            // inertial path sits Omega*dt further round in body-fixed axes at ITS UT.
            const double tailUT = 5000.0;
            const double dt = 2.0;
            Vector3d posBf = Surface(12.0, 40.0, 150000.0);
            Vector3d radialBf = posBf.normalized;
            Vector3d eastBf = Vector3d.Cross(new Vector3d(0, 1, 0), radialBf).normalized * -1.0;
            Vector3d northBf = Vector3d.Cross(radialBf, eastBf).normalized;
            Vector3d inertialBf = eastBf * 2000.0 + northBf * 1150.0 + radialBf * 40.0;
            Vector3d previousBf = RecordedTailVelocityResolver.RotateAboutY(
                posBf - inertialBf * dt,
                RecordedTailVelocityResolver.InertialYawDriftRadians(KerbinRotationPeriod, dt));
            Vector3d recorded = RecordedTailVelocityResolver.RotateAboutY(
                inertialBf, -recordYawDegrees * Math.PI / 180.0);

            TrajectoryPoint tail = FromBodyFixed(tailUT, posBf, recorded);
            TrajectoryPoint previous = FromBodyFixed(tailUT - dt, previousBf, recorded);

            Assert.True(Compute(previous, tail, tailUT, initialRotationDeg,
                out OrbitReseed.HistoricalTailPairSeedInputs result), result.FailureReason);
            Assert.False(result.AltitudeRuleRotating);
            Assert.False(result.Resolution.SampleWasRotatingFrame);

            double liftRad = (initialRotationDeg + tailUT * 360.0 / KerbinRotationPeriod) * Math.PI / 180.0;
            Vector3d expectedPos = RecordedTailVelocityResolver.RotateAboutY(posBf, liftRad);
            Vector3d expectedVel = RecordedTailVelocityResolver.RotateAboutY(inertialBf, liftRad);
            Assert.InRange((result.Inputs.PositionForUpdate.xzy - expectedPos).magnitude, 0.0, 1e-3);
            Assert.InRange((result.Inputs.VelocityForUpdate.xzy - expectedVel).magnitude, 0.0, 0.5);

            TwoBodyOrbit truth = SolveYup(expectedPos, expectedVel);
            TwoBodyOrbit seeded = SolveZup(result.Inputs);
            Assert.InRange(Math.Abs(seeded.SemiMajorAxis - truth.SemiMajorAxis), 0.0, 500.0);
            Assert.InRange(Math.Abs(seeded.Eccentricity - truth.Eccentricity), 0.0, 0.001);
            Assert.InRange(Math.Abs(seeded.Inclination - truth.Inclination) * 180.0 / Math.PI, 0.0, 0.05);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-118.4)]
        [InlineData(250.0)]
        public void V20RecordedTail_AboveThreshold_MapSeedIsTheParkedOrbit(double initialRotationDeg)
        {
            Assert.True(Compute(V20TailPrevious, V20Tail, V20Tail.ut, initialRotationDeg,
                out OrbitReseed.HistoricalTailPairSeedInputs result), result.FailureReason);
            Assert.False(result.Resolution.SampleWasRotatingFrame, "a 351 km sample is inertial");
            Assert.InRange(result.Resolution.Residual, 0.0, 0.5);
            // Inertial vectors keep their magnitude exactly.
            Assert.Equal(Vel(V20Tail).magnitude, result.Inputs.VelocityForUpdate.magnitude, 6);

            TwoBodyOrbit orbit = SolveZup(result.Inputs);
            Assert.InRange(orbit.SemiMajorAxis, 3670509.76 - 2000.0, 3670509.76 + 2000.0);
            Assert.InRange(orbit.Eccentricity, 0.80931 - 0.001, 0.80931 + 0.001);
            Assert.InRange(orbit.Inclination * 180.0 / Math.PI, 16.44 - 0.1, 16.44 + 0.1);
        }

        // ---------- declines ----------

        [Fact]
        public void MapPresenceTailSeed_SingleFrameTail_DeclinesSoTheStoredSeedsAnswer()
        {
            CelestialBody kerbin = TestBodyRegistry.CreateBody("Kerbin", KerbinRadius, KerbinMu);
            OrbitSeedResolver.RotationPeriodForTesting = _ => KerbinRotationPeriod;
            var rec = new Recording { RecordingId = "map-tail-single-frame", ExplicitEndUT = Lab7Tail.ut };
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = Lab7Tail.ut,
                endUT = Lab7Tail.ut,
                frames = new List<TrajectoryPoint> { Lab7Tail }
            });

            Assert.False(OrbitSeedResolver.TryDeriveTailOrbitSeed(
                rec, kerbin, Lab7Tail.ut + 5000.0, TailSeedUse.MapPresence, out TailDerivedOrbitSeed seed));
            Assert.Equal("tail-velocity-single-frame", seed.DeclineReason);
            Assert.False(seed.UsedHistoricalBodyRotation);
        }

        [Fact]
        public void HistoricalTailPair_RecordedVectorsThatFitNeitherFrame_Decline()
        {
            TrajectoryPoint previous = Lab7TailPrevious;
            TrajectoryPoint tail = Lab7Tail;
            previous.velocity *= 1.3f;
            tail.velocity *= 1.3f;
            Assert.False(Compute(previous, tail, tail.ut, 0.0, out OrbitReseed.HistoricalTailPairSeedInputs result));
            Assert.Equal(RecordedTailVelocityResolver.DeclineNoFit, result.FailureReason);
            Assert.False(result.Resolution.Resolved);
        }

        [Fact]
        public void HistoricalTailPair_PairGapOutOfRange_Declines()
        {
            TrajectoryPoint previous = Lab7TailPrevious;
            previous.ut = Lab7Tail.ut - 31.0;
            Assert.False(Compute(previous, Lab7Tail, Lab7Tail.ut, 0.0, out OrbitReseed.HistoricalTailPairSeedInputs result));
            Assert.Equal(RecordedTailVelocityResolver.DeclinePairGap, result.FailureReason);
        }

        [Fact]
        public void HistoricalTailPair_NonFiniteEpochOrRotation_Declines()
        {
            Assert.False(OrbitReseed.TryComputeHistoricalTailPairInputs(
                Lab7TailPrevious, Lab7Tail, double.NaN, KerbinRotationPeriod, 0.0, true,
                KerbinInverseRotThreshold, Surface, out var nanEpoch));
            Assert.Equal("non-finite-epoch", nanEpoch.FailureReason);

            Assert.False(OrbitReseed.TryComputeHistoricalTailPairInputs(
                Lab7TailPrevious, Lab7Tail, Lab7Tail.ut, 0.0, 0.0, true,
                KerbinInverseRotThreshold, Surface, out var zeroPeriod));
            Assert.Equal("historical-rotation-unavailable", zeroPeriod.FailureReason);

            Assert.False(OrbitReseed.TryComputeHistoricalTailPairInputs(
                Lab7TailPrevious, Lab7Tail, Lab7Tail.ut, KerbinRotationPeriod, 0.0, true,
                KerbinInverseRotThreshold, null, out var noLookup));
            Assert.Equal("historical-surface-position-unavailable", noLookup.FailureReason);
        }

        // ---------- the lift and the epoch ----------

        [Fact]
        public void HistoricalTailPair_LiftsBothSamplesAtTheTailPhaseAndSeedsAtTheShiftedEpoch()
        {
            const double loopShift = 1.002e9;
            var queriedLongitudes = new List<double>();
            Func<double, double, double, Vector3d> lookup = (lat, lon, alt) =>
            {
                queriedLongitudes.Add(lon);
                return Surface(lat, lon, alt);
            };

            Assert.True(OrbitReseed.TryComputeHistoricalTailPairInputs(
                Lab7TailPrevious, Lab7Tail, Lab7Tail.ut + loopShift, KerbinRotationPeriod, 15.0, true,
                KerbinInverseRotThreshold, lookup, out var result), result.FailureReason);

            // Rotation phase at the TAIL's recorded UT, not the shifted one, for both samples.
            double phase = 15.0 + Lab7Tail.ut * 360.0 / KerbinRotationPeriod;
            Assert.Equal(2, queriedLongitudes.Count);
            Assert.Equal(OrbitReseed.WrapLongitudeDegrees(Lab7Tail.longitude + phase), queriedLongitudes[0], 9);
            Assert.Equal(OrbitReseed.WrapLongitudeDegrees(Lab7TailPrevious.longitude + phase), queriedLongitudes[1], 9);
            Assert.Equal(Lab7Tail.ut + loopShift, result.SeedEpochUT, 3);
        }

        [Fact]
        public void TryLiftToHistoricalSurfacePosition_AppliesInitialRotationAndPhase()
        {
            Assert.True(OrbitReseed.TryLiftToHistoricalSurfacePosition(
                1.0, 170.0, 10.0, 10.0, 40.0, 20.0,
                (lat, lon, alt) =>
                {
                    Assert.Equal(1.0, lat);
                    Assert.Equal(-80.0, lon, 8);
                    Assert.Equal(10.0, alt);
                    return new Vector3d(7, 8, 9);
                },
                out Vector3d position, out double inertialLongitude, out string failure));
            Assert.Null(failure);
            Assert.Equal(-80.0, inertialLongitude, 8);
            Assert.Equal(7.0, position.x);
            Assert.Equal(8.0, position.y);
            Assert.Equal(9.0, position.z);

            Assert.False(OrbitReseed.TryLiftToHistoricalSurfacePosition(
                1.0, 2.0, 3.0, 10.0, double.NaN, 0.0, (lat, lon, alt) => Vector3d.zero,
                out _, out _, out string nanFailure));
            Assert.Equal("historical-rotation-unavailable", nanFailure);
        }

        // ---------- log line ----------

        [Fact]
        public void SeedLine_IsInvariantAndKeepsThePinnedPrefixOnlyWhenAccepted()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.True(Compute(Lab7TailPrevious, Lab7Tail, Lab7Tail.ut, 90.0,
                    out OrbitReseed.HistoricalTailPairSeedInputs accepted), accepted.FailureReason);
                string acceptedLine = OrbitReseed.FormatHistoricalTailPairSeedLine(
                    "Kerbin", Lab7TailPrevious, Lab7Tail, Lab7Tail.ut, true, accepted,
                    699737.9, 0.0103, 0.09);
                Assert.StartsWith("TryFromHistoricalLatLonAltAndRecordedVelocityWithEpoch: body=Kerbin ", acceptedLine);
                Assert.Contains("recordedUT=3606.66 ", acceptedLine);
                Assert.Contains("resolved=True decline=(none) velFrame=rotating altitudeRule=rotating", acceptedLine);
                Assert.Contains("sma=699737.9 ecc=0.010300 inc=0.0900", acceptedLine);

                TrajectoryPoint single = Lab7TailPrevious;
                single.ut = Lab7Tail.ut - 31.0;
                Assert.False(Compute(single, Lab7Tail, Lab7Tail.ut, 90.0,
                    out OrbitReseed.HistoricalTailPairSeedInputs declined));
                string declinedLine = OrbitReseed.FormatHistoricalTailPairSeedLine(
                    "Kerbin", single, Lab7Tail, Lab7Tail.ut, false, declined,
                    double.NaN, double.NaN, double.NaN);
                Assert.StartsWith("TryFromHistoricalLatLonAltAndRecordedVelocityWithEpoch declined: body=Kerbin ", declinedLine);
                Assert.DoesNotContain("WithEpoch: body=", declinedLine);
                Assert.Contains("resolved=False decline=" + RecordedTailVelocityResolver.DeclinePairGap
                    + " velFrame=(unresolved)", declinedLine);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        // ---------- gap glide pair selection ----------

        [Fact]
        public void GapGlidePair_PrefersTheSampleBeforeInTheSameSection()
        {
            List<TrajectoryPoint> points = Track(10, 11, 12, 13);
            List<TrackSection> sections = Sections((10, 13.5));
            Assert.True(GhostMapPresence.TrySelectGapGlidePair(
                points, sections, points[2], out TrajectoryPoint previous, out TrajectoryPoint tail, out string reason), reason);
            Assert.Equal(11.0, previous.ut);
            Assert.Equal(12.0, tail.ut);
        }

        [Fact]
        public void GapGlidePair_AtASectionStart_PairsWithTheSampleAfter()
        {
            List<TrajectoryPoint> points = Track(10, 11, 12, 13);
            List<TrackSection> sections = Sections((10, 12), (12, 13.5));
            Assert.True(GhostMapPresence.TrySelectGapGlidePair(
                points, sections, points[2], out TrajectoryPoint previous, out TrajectoryPoint tail, out string reason), reason);
            Assert.Equal(12.0, previous.ut);
            Assert.Equal(13.0, tail.ut);
        }

        [Fact]
        public void GapGlidePair_ALoneSampleOrAnotherBody_HasNoPair()
        {
            List<TrajectoryPoint> points = Track(10, 11, 12, 13);
            List<TrackSection> sections = Sections((10, 11.5), (11.5, 12.5), (12.5, 13.5));
            Assert.False(GhostMapPresence.TrySelectGapGlidePair(
                points, sections, points[2], out _, out _, out string lone));
            Assert.Equal(GhostMapPresence.GapGlidePairSingleSample, lone);

            List<TrajectoryPoint> mixed = Track(10, 11, 12);
            TrajectoryPoint mun = mixed[0];
            mun.bodyName = "Mun";
            mixed[0] = mun;
            mun = mixed[2];
            mun.bodyName = "Mun";
            mixed[2] = mun;
            Assert.False(GhostMapPresence.TrySelectGapGlidePair(
                mixed, Sections((10, 12.5)), mixed[1], out _, out _, out string otherBody));
            Assert.Equal(GhostMapPresence.GapGlidePairSingleSample, otherBody);
        }

        [Fact]
        public void GapGlidePair_APointNotInTheList_Declines()
        {
            List<TrajectoryPoint> points = Track(10, 11, 12);
            TrajectoryPoint stray = points[1];
            stray.ut = 11.5;
            Assert.False(GhostMapPresence.TrySelectGapGlidePair(
                points, Sections((10, 12.5)), stray, out _, out _, out string reason));
            Assert.Equal(GhostMapPresence.GapGlidePairPointNotFound, reason);
            Assert.False(GhostMapPresence.TrySelectGapGlidePair(
                null, null, stray, out _, out _, out string empty));
            Assert.Equal(GhostMapPresence.GapGlidePairPointNotFound, empty);
        }

        // ---------- helpers ----------

        private static bool Compute(
            TrajectoryPoint previous, TrajectoryPoint tail, double epochUT, double initialRotationDeg,
            out OrbitReseed.HistoricalTailPairSeedInputs result)
        {
            return OrbitReseed.TryComputeHistoricalTailPairInputs(
                previous, tail, epochUT, KerbinRotationPeriod, initialRotationDeg,
                bodyRotates: true, inverseRotThresholdAltitude: KerbinInverseRotThreshold,
                getBodyRelativeSurfacePositionYup: Surface, out result);
        }

        // KSP's CelestialBody.GetRelSurfacePosition on a spherical Kerbin.
        private static Vector3d Surface(double lat, double lon, double alt)
        {
            double r = KerbinRadius + alt;
            double la = lat * Math.PI / 180.0;
            double lo = lon * Math.PI / 180.0;
            return new Vector3d(r * Math.Cos(la) * Math.Cos(lo), r * Math.Sin(la), r * Math.Cos(la) * Math.Sin(lo));
        }

        private static TrajectoryPoint FromBodyFixed(double ut, Vector3d posBf, Vector3d recordedVel)
        {
            double r = posBf.magnitude;
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = Math.Asin(posBf.y / r) * 180.0 / Math.PI,
                longitude = Math.Atan2(posBf.z, posBf.x) * 180.0 / Math.PI,
                altitude = r - KerbinRadius,
                rotation = Quaternion.identity,
                velocity = new Vector3((float)recordedVel.x, (float)recordedVel.y, (float)recordedVel.z),
                bodyName = "Kerbin"
            };
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

        private static List<TrajectoryPoint> Track(params double[] uts)
        {
            var points = new List<TrajectoryPoint>();
            foreach (double ut in uts)
                points.Add(Point(ut, 0.0, ut, 90000.0, 0f, 0f, 2200f));
            return points;
        }

        private static List<TrackSection> Sections(params (double start, double end)[] spans)
        {
            var sections = new List<TrackSection>();
            foreach (var span in spans)
            {
                sections.Add(new TrackSection
                {
                    environment = SegmentEnvironment.ExoBallistic,
                    referenceFrame = ReferenceFrame.Absolute,
                    startUT = span.start,
                    endUT = span.end
                });
            }
            return sections;
        }

        private static TwoBodyOrbit SolveZup(OrbitReseed.StateVectorInputs inputs)
        {
            Assert.True(TwoBodyOrbit.TryCreate(inputs.PositionForUpdate, inputs.VelocityForUpdate, KerbinMu, 0.0,
                out TwoBodyOrbit orbit), "two-body solve failed");
            return orbit;
        }

        private static TwoBodyOrbit SolveYup(Vector3d posYup, Vector3d velYup)
        {
            Assert.True(TwoBodyOrbit.TryCreate(posYup.xzy, velYup.xzy, KerbinMu, 0.0, out TwoBodyOrbit orbit),
                "two-body solve failed");
            return orbit;
        }
    }
}
