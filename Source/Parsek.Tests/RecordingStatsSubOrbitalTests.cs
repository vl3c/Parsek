using System;
using System.Collections.Generic;
using System.IO;
using Parsek.Tests.Analyzer;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// RECORDINGS-STATS-DEBRIS-MAXSPD-IMPLAUSIBLE. A debris row on the
    /// <c>interbody-route-recorded</c> host read MaxSpd 318.4 km/s: its predicted impact
    /// tail is an e=0.99977 orbit segment whose periapsis radius is 69.7 m, and vis-viva at
    /// the periapsis ran away. Its siblings read Dist ~1000 km: the pair across the
    /// Relative-to-Absolute section gap measured anchor-local metres as degrees.
    /// </summary>
    [Collection("Sequential")]
    public class RecordingStatsSubOrbitalTests : IDisposable
    {
        private const double KerbinRadius = 600000.0;
        private const double KerbinGm = 3.5316e12;

        // The measured elements of recording fd43bae4's predicted impact segment.
        private const double MeasuredImpactSma = 302932.66785623581;
        private const double MeasuredImpactEcc = 0.99976998246206417;

        private readonly List<string> logLines = new List<string>();

        public RecordingStatsSubOrbitalTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private static Func<string, double[]> KerbinLookup => name =>
            name == "Kerbin" ? new double[] { KerbinRadius, KerbinGm } : null;

        private static double SurfaceSpeed(double sma)
            => Math.Sqrt(KerbinGm * (2.0 / KerbinRadius - 1.0 / sma));

        // --- TryComputeSegmentMaxSpeed ---------------------------------------

        [Fact]
        public void SegmentMaxSpeed_SubOrbitalPeriapsisInsideBody_ClampsAtSurface()
        {
            double periRadius = MeasuredImpactSma * (1.0 - MeasuredImpactEcc);
            double oldPeriSpeed = Math.Sqrt(KerbinGm * (2.0 / periRadius - 1.0 / MeasuredImpactSma));
            // The old candidate: vis-viva at a periapsis 69.7 m from the body centre.
            Assert.True(periRadius < 100.0, "periapsis radius " + periRadius);
            Assert.True(oldPeriSpeed > 300000.0, "old periapsis speed " + oldPeriSpeed);

            Assert.True(TrajectoryMath.TryComputeSegmentMaxSpeed(
                MeasuredImpactSma, MeasuredImpactEcc, KerbinRadius, KerbinGm, out double speed));
            Assert.Equal(SurfaceSpeed(MeasuredImpactSma), speed, 6);
            Assert.InRange(speed, 300.0, 400.0);
        }

        [Fact]
        public void SegmentMaxSpeed_RealOrbit_IsThePlainPeriapsisSpeed()
        {
            const double sma = 700000.0, ecc = 0.01;
            double periRadius = sma * (1.0 - ecc);
            double expected = Math.Sqrt(KerbinGm * (2.0 / periRadius - 1.0 / sma));

            Assert.True(TrajectoryMath.TryComputeSegmentMaxSpeed(
                sma, ecc, KerbinRadius, KerbinGm, out double speed));
            Assert.Equal(expected, speed);
        }

        [Fact]
        public void SegmentMaxSpeed_PeriapsisExactlyAtSurface_Unchanged()
        {
            // The boundary: periapsis on the surface is not clamped (nothing to clamp).
            const double sma = 650000.0;
            double ecc = 1.0 - KerbinRadius / sma;
            Assert.True(TrajectoryMath.TryComputeSegmentMaxSpeed(
                sma, ecc, KerbinRadius, KerbinGm, out double speed));
            Assert.Equal(SurfaceSpeed(sma), speed, 6);
        }

        [Fact]
        public void SegmentMaxSpeed_EllipseWhollyInsideBody_NoCandidate()
        {
            Assert.False(TrajectoryMath.TryComputeSegmentMaxSpeed(
                200000.0, 0.5, KerbinRadius, KerbinGm, out double speed));
            Assert.Equal(0.0, speed);
        }

        [Theory]
        [InlineData(-700000.0, 1.5)]  // hyperbola: unchanged (never a candidate here)
        [InlineData(700000.0, 1.0)]   // parabolic
        [InlineData(0.0, 0.1)]
        [InlineData(double.NaN, 0.1)]
        [InlineData(700000.0, double.NaN)]
        public void SegmentMaxSpeed_DegenerateElements_NoCandidate(double sma, double ecc)
        {
            Assert.False(TrajectoryMath.TryComputeSegmentMaxSpeed(
                sma, ecc, KerbinRadius, KerbinGm, out _));
        }

        // --- AccumulateOrbitSegmentStats ---------------------------------------

        [Fact]
        public void Accumulate_SubOrbitalImpactSegment_SpeedAndDistanceBounded()
        {
            var stats = new RecordingStats { maxSpeed = 259.1 };
            var segments = new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    startUT = 42874109.950000823, endUT = 42874109.970000826,
                    semiMajorAxis = MeasuredImpactSma, eccentricity = MeasuredImpactEcc,
                    bodyName = "Kerbin", isPredicted = true
                }
            };

            TrajectoryMath.AccumulateOrbitSegmentStats(segments, KerbinLookup, ref stats);

            double surfaceSpeed = SurfaceSpeed(MeasuredImpactSma);
            Assert.Equal(surfaceSpeed, stats.maxSpeed, 6);
            // Mean speed is capped at the arc's max speed: the old sqrt(gm/sma) was ~3414 m/s.
            double duration = segments[0].endUT - segments[0].startUT;
            Assert.Equal(surfaceSpeed * duration, stats.distanceTravelled, 6);
        }

        [Fact]
        public void Accumulate_RealOrbit_SpeedAndDistanceUnchanged()
        {
            var stats = new RecordingStats();
            var segments = new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    startUT = 200, endUT = 2700,
                    semiMajorAxis = 700000, eccentricity = 0.05,
                    bodyName = "Kerbin"
                }
            };

            TrajectoryMath.AccumulateOrbitSegmentStats(segments, KerbinLookup, ref stats);

            double periRadius = 700000 * 0.95;
            Assert.Equal(Math.Sqrt(KerbinGm * (2.0 / periRadius - 1.0 / 700000)), stats.maxSpeed);
            Assert.Equal(Math.Sqrt(KerbinGm / 700000) * 2500.0, stats.distanceTravelled);
        }

        // --- pair travel frame -------------------------------------------------

        private static TrackSection Section(ReferenceFrame frame, double startUT, double endUT)
        {
            return new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = frame,
                startUT = startUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>()
            };
        }

        [Fact]
        public void PairFrame_RelativeToAbsoluteAcrossGap_NotATravelStep()
        {
            // The measured hand-off: Relative [61.45, 67.392], a 0.06 s gap, Absolute from 67.452.
            var sections = new List<TrackSection>
            {
                Section(ReferenceFrame.Relative, 61.452489257, 67.392489493),
                Section(ReferenceFrame.Absolute, 67.452489495, 109.950000823)
            };

            Assert.False(TrajectoryMath.TryResolvePairTravelFrame(
                sections, 67.392489493, 67.452489495, out _));
        }

        [Fact]
        public void PairFrame_SameSection_ReturnsThatFrame()
        {
            var sections = new List<TrackSection>
            {
                Section(ReferenceFrame.Relative, 0, 10),
                Section(ReferenceFrame.Absolute, 20, 30)
            };

            Assert.True(TrajectoryMath.TryResolvePairTravelFrame(sections, 1, 2, out var rel));
            Assert.Equal(ReferenceFrame.Relative, rel);
            // The last Relative sample sits on the section's exclusive end, before a gap.
            Assert.True(TrajectoryMath.TryResolvePairTravelFrame(sections, 9, 10, out rel));
            Assert.Equal(ReferenceFrame.Relative, rel);
            Assert.True(TrajectoryMath.TryResolvePairTravelFrame(sections, 21, 22, out var abs));
            Assert.Equal(ReferenceFrame.Absolute, abs);
        }

        [Fact]
        public void PairFrame_AbsoluteSectionsAcrossGap_StillMeasured()
        {
            var sections = new List<TrackSection>
            {
                Section(ReferenceFrame.Absolute, 0, 10),
                Section(ReferenceFrame.OrbitalCheckpoint, 20, 30)
            };

            Assert.True(TrajectoryMath.TryResolvePairTravelFrame(sections, 10, 20, out _));
        }

        [Fact]
        public void PairFrame_ZeroElapsedTime_NotATravelStep()
        {
            Assert.False(TrajectoryMath.TryResolvePairTravelFrame(
                new List<TrackSection>(), 5, 5, out _));
        }

        [Fact]
        public void PairFrame_NoSections_ReadsAbsolute()
        {
            Assert.True(TrajectoryMath.TryResolvePairTravelFrame(null, 1, 2, out var frame));
            Assert.Equal(ReferenceFrame.Absolute, frame);
        }

        // --- RECORDING-STATS-FRAME-LOOKUP-NO-EPSILON ----------------------------

        [Fact]
        public void PointFrame_RelativeSampleAHairPastEndBeforeGap_ReadsRelative()
        {
            var sections = new List<TrackSection>
            {
                Section(ReferenceFrame.Relative, 0, 10),
                Section(ReferenceFrame.Absolute, 20, 30)
            };
            double ut = 10 + 1e-10;

            Assert.Equal(-1, TrajectoryMath.FindTrackSectionForUT(sections, ut));
            Assert.Equal(0, TrajectoryMath.FindTrackSectionForUTWithBoundaryEpsilon(sections, ut));
            Assert.Equal(ReferenceFrame.Relative,
                TrajectoryMath.ResolvePointFrameForStats(sections, ut));
            Assert.True(TrajectoryMath.TryResolvePairTravelFrame(sections, 9, ut, out var frame));
            Assert.Equal(ReferenceFrame.Relative, frame);
        }

        [Fact]
        public void PointFrame_JustOutsideTheEpsilon_StaysUnresolved()
        {
            var sections = new List<TrackSection>
            {
                Section(ReferenceFrame.Relative, 0, 10),
                Section(ReferenceFrame.Absolute, 20, 30)
            };
            double ut = 10 + 1e-8;
            Assert.True(ut - 10 > TrajectoryMath.SectionBoundaryEpsilonSeconds);

            Assert.Equal(-1, TrajectoryMath.FindTrackSectionForUTWithBoundaryEpsilon(sections, ut));
            Assert.Equal(ReferenceFrame.Absolute,
                TrajectoryMath.ResolvePointFrameForStats(sections, ut));
            Assert.False(TrajectoryMath.TryResolvePairTravelFrame(sections, 9, ut, out _));
        }

        [Theory]
        [InlineData(ReferenceFrame.Relative, ReferenceFrame.Absolute)]
        [InlineData(ReferenceFrame.Absolute, ReferenceFrame.Relative)]
        public void PointFrame_SharedContiguousBoundary_ReadsTheSectionPlaybackDispatches(
            ReferenceFrame earlier, ReferenceFrame later)
        {
            var sections = new List<TrackSection>
            {
                Section(earlier, 0, 10),
                Section(later, 10, 20)
            };

            // Playback's own section dispatch (strict lookup) picks the later section: the
            // earlier section's end is exclusive.
            int playbackIdx = TrajectoryMath.FindTrackSectionForUT(sections, 10);
            Assert.Equal(1, playbackIdx);
            Assert.Equal(playbackIdx,
                TrajectoryMath.FindTrackSectionForUTWithBoundaryEpsilon(sections, 10));
            Assert.Equal(later, TrajectoryMath.ResolvePointFrameForStats(sections, 10));
        }

        [Fact]
        public void PointFrame_WithinEpsilonOfBothSidesOfAGap_StartMatchWins()
        {
            // The UT is the earlier section's exact end AND within the tolerance of the
            // later section's start. The anchor resolver takes the start match; the old stats
            // fallback took the exact end match and read the Absolute section instead.
            var sections = new List<TrackSection>
            {
                Section(ReferenceFrame.Absolute, 0, 10),
                Section(ReferenceFrame.Relative, 10 + 5e-10, 20)
            };

            Assert.Equal(-1, TrajectoryMath.FindTrackSectionForUT(sections, 10));
            Assert.Equal(1, TrajectoryMath.FindTrackSectionForUTWithBoundaryEpsilon(sections, 10));
            Assert.Equal(ReferenceFrame.Relative,
                TrajectoryMath.ResolvePointFrameForStats(sections, 10));
        }

        [Fact]
        public void EpsilonLookup_NonFiniteUtOrNoSections_Unresolved()
        {
            var sections = new List<TrackSection> { Section(ReferenceFrame.Relative, 0, 10) };
            Assert.Equal(-1, TrajectoryMath.FindTrackSectionForUTWithBoundaryEpsilon(sections, double.NaN));
            Assert.Equal(-1, TrajectoryMath.FindTrackSectionForUTWithBoundaryEpsilon(null, 5));
            Assert.Equal(-1, TrajectoryMath.FindTrackSectionForUTWithBoundaryEpsilon(
                new List<TrackSection>(), 5));
        }

        [Fact]
        public void ComputeStats_RelativeSampleAHairPastEnd_MeasuredAsMetresForDistanceAndRange()
        {
            var rec = new Recording();
            var p0 = new TrajectoryPoint { ut = 100, latitude = 0, longitude = 0, altitude = 0, bodyName = "Kerbin" };
            var p1 = new TrajectoryPoint { ut = 106 + 1e-10, latitude = 30, longitude = 40, altitude = 0, bodyName = "Kerbin" };
            rec.Points.AddRange(new[] { p0, p1 });
            var rel = Section(ReferenceFrame.Relative, 100, 106);
            rel.frames.AddRange(new[] { p0, p1 });
            rec.TrackSections.Add(rel);
            rec.TrackSections.Add(Section(ReferenceFrame.Absolute, 107, 110));

            var stats = TrajectoryMath.ComputeStats(rec, KerbinLookup);

            // |(30, 40, 0)| = 50 m. Read as degrees the range was thousands of km.
            Assert.Equal(50.0, stats.distanceTravelled, 9);
            Assert.Equal(50.0, stats.maxRange, 9);
            Assert.Contains(logLines, l => l.Contains("[TrajectoryMath]")
                && l.Contains("ComputeStats complete") && l.Contains("skippedFrameChangePairs=0"));
        }

        [Fact]
        public void ComputeStats_RelativeToAbsoluteGap_DoesNotMeasureMetresAsDegrees()
        {
            var rec = new Recording();
            // Relative: anchor-local metres. Absolute: body-fixed degrees.
            var p0 = new TrajectoryPoint { ut = 100, latitude = 1.1, longitude = -15.6, altitude = 0.6, bodyName = "Kerbin", velocity = new Vector3(10, 0, 0) };
            var p1 = new TrajectoryPoint { ut = 106, latitude = 69.4, longitude = -545.6, altitude = 25.1, bodyName = "Kerbin", velocity = new Vector3(10, 0, 0) };
            var p2 = new TrajectoryPoint { ut = 106.06, latitude = -0.094, longitude = -74.464, altitude = 4950.8, bodyName = "Kerbin", velocity = new Vector3(10, 0, 0) };
            var p3 = new TrajectoryPoint { ut = 107, latitude = -0.094, longitude = -74.464, altitude = 5000.8, bodyName = "Kerbin", velocity = new Vector3(10, 0, 0) };
            rec.Points.AddRange(new[] { p0, p1, p2, p3 });
            var rel = Section(ReferenceFrame.Relative, 100, 106);
            rel.frames.AddRange(new[] { p0, p1 });
            var abs = Section(ReferenceFrame.Absolute, 106.06, 107);
            abs.frames.AddRange(new[] { p2, p3 });
            rec.TrackSections.Add(rel);
            rec.TrackSections.Add(abs);

            var stats = TrajectoryMath.ComputeStats(rec, KerbinLookup);

            // Relative step: |(68.3, -530.0, 24.5)| ~ 535 m; Absolute step: 50 m altitude.
            Assert.InRange(stats.distanceTravelled, 580.0, 590.0);
            Assert.Contains(logLines, l => l.Contains("[TrajectoryMath]")
                && l.Contains("ComputeStats complete") && l.Contains("skippedFrameChangePairs=1"));
        }

        // --- the committed fixture -------------------------------------------

        private static Recording LoadFixtureRecording(string recordingId)
        {
            string saveDir = Path.Combine(SyntheticRecordingTests.ResolveProjectRoot(),
                "harness", "fixtures", "saves", "interbody-route-recorded");
            Assert.True(Directory.Exists(saveDir), "fixture save not found at " + saveDir);
            var model = SaveDirectoryLoader.Load(saveDir, _ => null);
            foreach (Recording rec in model.Recordings)
                if (rec != null && rec.RecordingId == recordingId)
                    return rec;
            return null;
        }

        /// <summary>
        /// Row #27 of the census capture: 54 points, one predicted impact segment, MaxAlt
        /// 5.8 km. MaxSpd read 318.4 km/s; the point speeds peak at 259.1 m/s and the
        /// impact-orbit speed at sea level is 337.6 m/s.
        /// </summary>
        [Fact]
        public void Fixture_InterbodyDebrisImpactRow_HasAPlausibleMaxSpeed()
        {
            Recording rec = LoadFixtureRecording("fd43bae4daf24db998848bc83e1436d8");
            Assert.NotNull(rec);
            Assert.Equal("Kerbal X Debris", rec.VesselName);
            Assert.Equal(54, rec.Points.Count);
            Assert.Single(rec.OrbitSegments);
            OrbitSegment seg = rec.OrbitSegments[0];
            Assert.True(seg.semiMajorAxis * (1.0 - seg.eccentricity) < KerbinRadius,
                "the fixture's impact segment no longer dips below the surface");

            var stats = TrajectoryMath.ComputeStats(rec, KerbinLookup);

            Assert.InRange(stats.maxSpeed, 250.0, 400.0);
            Assert.InRange(stats.distanceTravelled, 1000.0, 3000.0);
        }

        /// <summary>
        /// A sibling debris row with the same Relative-then-Absolute shape and no orbit
        /// segment: Dist read ~1025 km for a 48 s fall, all from the one pair across the
        /// 0.06 s section gap.
        /// </summary>
        [Fact]
        public void Fixture_InterbodyDebrisSiblingRow_HasAPlausibleDistance()
        {
            Recording rec = LoadFixtureRecording("395e8cbb5f1045538ccc21a83ece5ea2");
            Assert.NotNull(rec);
            Assert.Empty(rec.OrbitSegments);
            Assert.Equal(2, rec.TrackSections.Count);
            Assert.Equal(ReferenceFrame.Relative, rec.TrackSections[0].referenceFrame);
            Assert.Equal(ReferenceFrame.Absolute, rec.TrackSections[1].referenceFrame);

            var stats = TrajectoryMath.ComputeStats(rec, KerbinLookup);

            Assert.InRange(stats.distanceTravelled, 500.0, 3000.0);
            Assert.InRange(stats.maxSpeed, 250.0, 300.0);
        }
    }
}
