using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Regression for the ghost-map icon teleport across a parking -> loiter orbit RAISE.
    ///
    /// Recording 61e9177193444e329247d0e8288cf91e (logs/2026-06-01_1110_icon-teleport-after-fix):
    /// the vessel climbs from a low Kerbin parking orbit (sma 671928, ecc 0.088, recorded UT
    /// 52569494.7-52569969.5) into a higher circular loiter orbit (sma 731230, ecc 0.0013, UT
    /// 52570174.2-...). Between them is a ~205 s raise arc (UT 52569969.5-52570174.2) that has
    /// NO covering OrbitSegment but IS captured as ~84 body-fixed (Absolute, ref=0) trajectory
    /// POINTs at ~131 km, body=Kerbin.
    ///
    /// Bug: TrajectoryMath.FindOrbitSegmentOrSameBodyCarry carried the PREVIOUS (parking)
    /// segment forward across the same-body gap, so the dispatcher kept driving the icon off the
    /// stale parking orbit, froze it past-window for ~35 s, then teleported it ~1318 km onto the
    /// loiter orbit (seam jump 399713 m) once UT entered the loiter segment. The point/state-vector
    /// fallback (which would glide the recorded ascent) was gated behind !seg.HasValue and never ran.
    ///
    /// Fix: GhostMapPresence.ShouldDriveGapFromPoints routes the icon onto the recorded body-fixed
    /// POINTs across a NON-orbit-equivalent same-body gap, so it glides the raise arc instead of
    /// freezing + snapping. The equivalent-orbit carry case (capture burn between two same orbits)
    /// stays on the carry path.
    /// </summary>
    [Collection("Sequential")]
    public class GhostMapOrbitRaiseGapTests : IDisposable
    {
        // Recorded UT structure (rounded from the .prec dump).
        private const double ParkingStartUT = 52569494.7;
        private const double ParkingEndUT = 52569969.494702443;   // seg#1 endUT (gap start)
        private const double LoiterStartUT = 52570174.174735993;  // seg#2 startUT (gap end)
        private const double LoiterEndUT = 53150125.0;
        private const double ParkingSma = 671928.40970866173;
        private const double ParkingEcc = 0.088283234082374554;
        private const double LoiterSma = 731229.57633187377;
        private const double LoiterEcc = 0.0013137142817968353;

        public GhostMapOrbitRaiseGapTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static List<OrbitSegment> BuildParkingLoiterSegments()
        {
            return new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    startUT = ParkingStartUT, endUT = ParkingEndUT,
                    semiMajorAxis = ParkingSma, eccentricity = ParkingEcc,
                    inclination = 0.18870260431072694,
                    longitudeOfAscendingNode = 335.69895266376579,
                    argumentOfPeriapsis = 152.90421069401148,
                    bodyName = "Kerbin",
                },
                new OrbitSegment
                {
                    startUT = LoiterStartUT, endUT = LoiterEndUT,
                    semiMajorAxis = LoiterSma, eccentricity = LoiterEcc,
                    inclination = 0.18903449190718044,
                    longitudeOfAscendingNode = 335.64572740348439,
                    argumentOfPeriapsis = 241.34144799953822,
                    bodyName = "Kerbin",
                },
            };
        }

        /// <summary>
        /// Build the recorded ascent: body-fixed Absolute points at ~131 km whose lat/lon track the
        /// real raise arc (lat -0.094 -> +0.017, lon 33.3 -> 65.0 over the gap), bracketed by the
        /// parking-orbit and loiter-orbit endpoints. ~84 in-gap samples at ~2.4 s spacing.
        /// </summary>
        private static MockTrajectory BuildRecording(ReferenceFrame gapFrame = ReferenceFrame.Absolute)
        {
            var traj = new MockTrajectory
            {
                RecordingId = "orbit-raise-gap",
                VesselName = "RaiseTester",
                OrbitSegments = BuildParkingLoiterSegments(),
                StartUTOverride = ParkingStartUT,
                EndUTOverride = LoiterEndUT,
            };

            // Points: a coarse parking sample, dense gap samples, a coarse loiter sample.
            var points = new List<TrajectoryPoint>();
            points.Add(MakePoint(ParkingEndUT, -0.0940870293026434, 33.33922852313182, 131165.32404627802));

            const int gapSamples = 84;
            for (int i = 1; i <= gapSamples; i++)
            {
                double f = (double)i / (gapSamples + 1);
                double ut = ParkingEndUT + f * (LoiterStartUT - ParkingEndUT);
                double lat = -0.0940870293026434 + f * (0.01735396213266869 - (-0.0940870293026434));
                double lon = 33.33922852313182 + f * (65.0419383682048 - 33.33922852313182);
                double alt = 131165.32404627802 + f * (131764.85360325896 - 131165.32404627802);
                points.Add(MakePoint(ut, lat, lon, alt));
            }
            points.Add(MakePoint(LoiterStartUT, 0.01735396213266869, 65.0419383682048, 131764.85360325896));
            traj.Points = points;

            // Track sections: one Absolute (or caller-chosen frame) section spanning the gap, one
            // covering the loiter segment. HasRecordedTrackCoverageAtUT / IsInRelativeFrame read these.
            traj.TrackSections = new List<TrackSection>
            {
                new TrackSection
                {
                    environment = SegmentEnvironment.ExoBallistic,
                    referenceFrame = gapFrame,
                    startUT = ParkingEndUT,
                    endUT = LoiterStartUT,
                    frames = new List<TrajectoryPoint>(),
                },
                new TrackSection
                {
                    environment = SegmentEnvironment.ExoBallistic,
                    referenceFrame = ReferenceFrame.Absolute,
                    startUT = LoiterStartUT,
                    endUT = LoiterEndUT,
                    frames = new List<TrajectoryPoint>(),
                },
            };

            return traj;
        }

        private static TrajectoryPoint MakePoint(double ut, double lat, double lon, double alt)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            };
        }

        // Sphere-projection of a body-fixed (lat, lon, alt) sample to an approximate world position.
        // Faithful to the icon's relative motion (monotone in geodesic distance) without needing a
        // live CelestialBody. Used only to measure CONTINUITY of the icon track across the gap.
        private static Vector3d WorldPos(double latDeg, double lonDeg, double altMetres)
        {
            const double KerbinRadius = 600000.0;
            double r = KerbinRadius + altMetres;
            double lat = latDeg * Math.PI / 180.0;
            double lon = lonDeg * Math.PI / 180.0;
            return new Vector3d(
                r * Math.Cos(lat) * Math.Cos(lon),
                r * Math.Sin(lat),
                r * Math.Cos(lat) * Math.Sin(lon));
        }

        // === Pure predicate: ShouldDriveGapFromPoints decision matrix ===

        [Fact]
        public void ShouldDriveGapFromPoints_TrueInsideParkingToLoiterRaiseGap()
        {
            var traj = BuildRecording();
            var segs = traj.OrbitSegments;

            double midGap = (ParkingEndUT + LoiterStartUT) / 2.0;
            Assert.True(GhostMapPresence.ShouldDriveGapFromPoints(segs, traj, midGap),
                "Mid-gap UT in a non-equivalent same-body raise with Absolute point coverage must drive from points.");
            // A few sample points along the gap.
            Assert.True(GhostMapPresence.ShouldDriveGapFromPoints(segs, traj, ParkingEndUT + 10.0));
            Assert.True(GhostMapPresence.ShouldDriveGapFromPoints(segs, traj, LoiterStartUT - 10.0));
        }

        [Fact]
        public void ShouldDriveGapFromPoints_FalseInsideASegment()
        {
            var traj = BuildRecording();
            var segs = traj.OrbitSegments;

            // UT inside the parking segment and inside the loiter segment: no gap.
            Assert.False(GhostMapPresence.ShouldDriveGapFromPoints(segs, traj, ParkingStartUT + 100.0));
            Assert.False(GhostMapPresence.ShouldDriveGapFromPoints(segs, traj, LoiterStartUT + 100.0));
        }

        [Fact]
        public void ShouldDriveGapFromPoints_FalseWithoutPointCoverage()
        {
            var traj = BuildRecording();
            traj.Points = new List<TrajectoryPoint>();   // no points to drive from
            traj.TrackSections = new List<TrackSection>(); // no track coverage
            double midGap = (ParkingEndUT + LoiterStartUT) / 2.0;
            Assert.False(GhostMapPresence.ShouldDriveGapFromPoints(traj.OrbitSegments, traj, midGap));
        }

        [Fact]
        public void ShouldDriveGapFromPoints_FalseInRelativeFrame()
        {
            // Relative gap section: lat/lon/alt are anchor-local metre offsets, not geographic
            // coordinates; the state-vector positioner would put the icon inside the planet.
            var traj = BuildRecording(gapFrame: ReferenceFrame.Relative);
            double midGap = (ParkingEndUT + LoiterStartUT) / 2.0;
            Assert.False(GhostMapPresence.ShouldDriveGapFromPoints(traj.OrbitSegments, traj, midGap));
        }

        [Fact]
        public void ShouldDriveGapFromPoints_FalseInOrbitalCheckpointFrame()
        {
            // OrbitalCheckpoint gap section: the flat Recording.Points list is sparse / empty
            // across an on-rails coast (the section is a Keplerian bridge, not a per-frame
            // body-fixed sample stream), so bracketing it would strand the icon on a stale
            // clamped flat point. The Absolute-frame requirement rejects it -- the gap glide
            // only drives from points inside an Absolute (atmospheric / maneuver) section.
            var traj = BuildRecording(gapFrame: ReferenceFrame.OrbitalCheckpoint);
            double midGap = (ParkingEndUT + LoiterStartUT) / 2.0;
            Assert.False(GhostMapPresence.ShouldDriveGapFromPoints(traj.OrbitSegments, traj, midGap),
                "OrbitalCheckpoint coast gap must stay on the carry path, not the flat-points glide.");
        }

        [Fact]
        public void ShouldDriveGapFromPoints_FalseForOrbitEquivalentCarryGap()
        {
            // The carry was BUILT for an equivalent-orbit gap (capture burn between two identical
            // orbits). That case must stay on the carry, not the points glide.
            var traj = BuildRecording();
            var segs = traj.OrbitSegments;
            // Make the second segment orbit-equivalent to the first (same parking elements).
            var second = segs[1];
            second.semiMajorAxis = ParkingSma;
            second.eccentricity = ParkingEcc;
            second.inclination = segs[0].inclination;
            second.longitudeOfAscendingNode = segs[0].longitudeOfAscendingNode;
            second.argumentOfPeriapsis = segs[0].argumentOfPeriapsis;
            segs[1] = second;

            double midGap = (ParkingEndUT + LoiterStartUT) / 2.0;
            Assert.True(TrajectoryMath.AreOrbitSegmentsEquivalentForMapDisplay(segs[0], segs[1]));
            Assert.False(GhostMapPresence.ShouldDriveGapFromPoints(segs, traj, midGap),
                "Equivalent-orbit same-body gap must stay on the carry path, not the points glide.");
        }

        // === Continuity: the icon must track the recorded ascent across the gap (no freeze, no 400km snap) ===

        // This walks the gap at the same cadence the per-tick map refresh would, and compares the
        // icon track produced by:
        //   (a) the FIXED path: ShouldDriveGapFromPoints -> BracketPointAtUT -> recorded lat/lon/alt
        //   (b) the BUGGY path: FindOrbitSegmentOrSameBodyCarry, which carries the parking segment
        //       across the whole gap (the icon is frozen at the parking-orbit phase) and then jumps
        //       to the loiter orbit at the seam.
        // It asserts (a) is continuous (each step << 50 km) and that (b) exhibits the >100 km seam
        // jump the fix removes.
        //
        // SCOPE: this is a PREDICATE + CONTINUITY premise test. It proves that (1)
        // ShouldDriveGapFromPoints fires across the raise gap, (2) the recorded points form a
        // continuous track while the carried segment teleports. It does NOT exercise the live
        // dispatcher wiring (the two branches in ParsekPlaybackPolicy.CheckPendingMapVessels and
        // GhostMapPresence.RefreshTrackingStationGhosts that actually route the ghost onto the
        // points): disabling those branches leaves this test green because it calls the predicate
        // and the positioner inputs directly rather than the dispatcher. The dispatcher wiring +
        // the real GetWorldSurfacePosition resolution are guarded by the Unity-runtime in-game
        // tests RuntimeTests.GhostMapIconGlidesAcrossRaiseGap_Flight /
        // _TrackingStation (which xUnit cannot run because they need a live CelestialBody and
        // Orbit.UpdateFromUT), plus playtest.
        [Fact]
        public void IconGlidesRecordedAscentAcrossGap_NoFreezeNoSnap()
        {
            var traj = BuildRecording();
            var segs = traj.OrbitSegments;

            // Walk the points-covered window: from the gap-start boundary point (parking endpoint,
            // first recorded sample) through the gap-end boundary point (loiter entry), at ~5 s cadence.
            // Every frame here is a real gap frame the fix governs.
            var sampleUts = new List<double>();
            for (double ut = ParkingEndUT; ut <= LoiterStartUT; ut += 5.0)
                sampleUts.Add(ut);
            sampleUts.Add(LoiterStartUT);

            // --- (a) FIXED path: drive from recorded points whenever ShouldDriveGapFromPoints is true. ---
            int cachedIndex = -1;
            var fixedTrack = new List<Vector3d>();
            int gapFramesDrivenFromPoints = 0;
            foreach (double ut in sampleUts)
            {
                bool carry = TrajectoryMath.FindOrbitSegmentOrSameBodyCarry(segs, ut).HasValue;
                bool drivesFromPoints = carry && GhostMapPresence.ShouldDriveGapFromPoints(segs, traj, ut);
                TrajectoryPoint? pt = TrajectoryMath.BracketPointAtUT(traj.Points, ut, ref cachedIndex);
                Assert.True(pt.HasValue, $"Expected a bracketing recorded point at UT {ut:F1}");
                fixedTrack.Add(WorldPos(pt.Value.latitude, pt.Value.longitude, pt.Value.altitude));
                if (drivesFromPoints) gapFramesDrivenFromPoints++;
            }

            Assert.True(gapFramesDrivenFromPoints >= 30,
                $"Expected the fix to drive most of the ~205 s gap from points; drove {gapFramesDrivenFromPoints} frames.");

            double maxFixedStep = MaxConsecutiveStep(fixedTrack);
            Assert.True(maxFixedStep < 50000.0,
                $"Fixed icon track must be continuous across the gap (max step {maxFixedStep:F0} m, expected < 50 km).");

            // --- (b) BUGGY carry path: the parking segment is carried across the whole gap, so the
            // icon is pinned at the parking-orbit endpoint phase, then jumps to the loiter endpoint at
            // the seam. Model the resulting icon track by the carried segment's identity. ---
            Vector3d parkingEndpoint = WorldPos(-0.0940870293026434, 33.33922852313182, 131165.32404627802);
            Vector3d loiterEntry = WorldPos(0.01735396213266869, 65.0419383682048, 131764.85360325896);
            var buggyTrack = new List<Vector3d>();
            foreach (double ut in sampleUts)
            {
                OrbitSegment? carried = TrajectoryMath.FindOrbitSegmentOrSameBodyCarry(segs, ut);
                // Carry returns the parking segment for the whole gap (proves the freeze), then the
                // loiter segment once UT enters it (proves the snap). Map each to its endpoint phase.
                bool onLoiter = carried.HasValue
                    && Math.Abs(carried.Value.semiMajorAxis - LoiterSma) < 1.0;
                buggyTrack.Add(onLoiter ? loiterEntry : parkingEndpoint);
            }

            double maxBuggyStep = MaxConsecutiveStep(buggyTrack);
            Assert.True(maxBuggyStep > 100000.0,
                $"The buggy same-body carry must exhibit the teleport seam (max step {maxBuggyStep:F0} m, expected > 100 km). "
                + "If this assertion fails the carry no longer freezes+snaps and the regression model is stale.");

            // The fix's max step must be at least an order of magnitude smaller than the buggy seam.
            Assert.True(maxFixedStep * 10 < maxBuggyStep,
                $"Fixed track step ({maxFixedStep:F0} m) must be far smaller than the buggy seam ({maxBuggyStep:F0} m).");
        }

        private static double MaxConsecutiveStep(List<Vector3d> track)
        {
            double max = 0;
            for (int i = 1; i < track.Count; i++)
            {
                double d = (track[i] - track[i - 1]).magnitude;
                if (d > max) max = d;
            }
            return max;
        }
    }
}
