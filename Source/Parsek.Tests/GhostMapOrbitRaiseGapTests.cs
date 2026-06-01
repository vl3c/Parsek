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

        [Fact]
        public void ShouldDriveGapFromPoints_FalseForCrossBodyGap()
        {
            // A cross-body gap (an SOI crossing, e.g. Kerbin -> Mun) is handled by the OrbitalCheckpoint
            // state-vector path, not the same-body points glide. The bracketing segments differ in body,
            // so the predicate must reject it even though the gap has Absolute point coverage and the
            // orbits are non-equivalent. Without the same-body guard this returns true (TryFindOrbitSegmentGap
            // has no body check), so this is the regression guard for that guard.
            var traj = BuildRecording();
            var segs = traj.OrbitSegments;
            var second = segs[1];
            second.bodyName = "Mun";   // cross-body next segment
            segs[1] = second;

            double midGap = (ParkingEndUT + LoiterStartUT) / 2.0;
            Assert.False(GhostMapPresence.ShouldDriveGapFromPoints(segs, traj, midGap),
                "Cross-body gap must stay off the same-body points glide (SOI crossing handled elsewhere).");
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

        #region Loop-shift body-fixed LAN compensation (ApplyLoopShiftLanRotation)

        // The SECOND parking->loiter teleport (logs/2026-06-01_1638_diverge-check): with the
        // gap-glide gliding the raise correctly, the loiter ORBIT LINE still initialized the icon
        // ~97 deg off the raise end. The GhostIconDiverge diagnostic measured orbitLon (line) 161.78
        // vs transformLon (raise end) 64.94, lonDiv 96.84 deg, at a loop shift of 845,044,936 s. That
        // 96.84 deg decomposes into Kerbin's rotation over the shift (96.62 deg, the bug) plus 0.27 deg
        // of legitimate forward orbital motion. The looped inertial orbit's body-fixed projection at
        // the live clock is rotated by the body's rotation over the shift relative to the recording,
        // while the body-fixed gap-glide points stay surface-locked, so they desync by that rotation.
        // ApplyLoopShiftLanRotation rotates the node back by it to restore the recorded body-fixed
        // appearance.

        private const double KerbinSiderealRotationPeriod = 21549.425; // s (KSP Kerbin.rotationPeriod)

        [Fact]
        public void LanRotation_NoLoopShift_ReturnsRecordedLanUnchanged()
        {
            // shift == 0 (non-looped recording): byte-identical, never rotate.
            double lan = 335.6457276854552;
            Assert.Equal(lan, GhostMapPresence.ApplyLoopShiftLanRotation(
                lan, loopEpochShiftSeconds: 0.0, KerbinSiderealRotationPeriod));
        }

        [Fact]
        public void LanRotation_NonPositiveRotationPeriod_ReturnsRecordedLanUnchanged()
        {
            // A non-rotating / unknown body cannot desync; identity guard.
            double lan = 200.0;
            Assert.Equal(lan, GhostMapPresence.ApplyLoopShiftLanRotation(
                lan, loopEpochShiftSeconds: 845044935.6, bodyRotationPeriodSeconds: 0.0));
        }

        [Fact]
        public void LanRotation_MatchesMeasuredSeam_RemovesBodyRotationOverShift()
        {
            // Reproduce the captured seam: recorded loiter LAN 335.6457, shift 845,044,935.6 s.
            // Kerbin turns 845044935.6 / 21549.425 = 39214.2684 rev -> 0.2684 rev = 96.62 deg over the
            // shift, so the node must rotate back 96.62 deg. The diagnostic measured the orbit line
            // 96.84 deg ahead of the raise end (96.62 body-rotation + 0.27 legitimate forward motion).
            double recordedLan = 335.6457276854552;
            double shift = 845044935.6;
            double bodyRotDeg = (shift / KerbinSiderealRotationPeriod) * 360.0 % 360.0;
            Assert.InRange(bodyRotDeg, 96.5, 96.75); // ~96.62 deg

            double seeded = GhostMapPresence.ApplyLoopShiftLanRotation(
                recordedLan, shift, KerbinSiderealRotationPeriod);

            // Seeded LAN must be the recorded LAN rotated back by exactly the body rotation, mod 360.
            double expected = (recordedLan - bodyRotDeg) % 360.0;
            if (expected < 0.0) expected += 360.0;
            Assert.Equal(expected, seeded, 6);

            // The applied rotation (recorded - seeded, normalized) must equal the body rotation, which
            // is what cancels the inertial-vs-body-fixed desync at the seam.
            double appliedRotation = ((recordedLan - seeded) % 360.0 + 360.0) % 360.0;
            Assert.Equal(bodyRotDeg, appliedRotation, 6);
        }

        [Fact]
        public void LanRotation_ResultAlwaysNormalizedTo0_360()
        {
            // A range of shifts / LANs (including ones that drive the raw result negative or > 360)
            // must always normalize into [0, 360).
            foreach (double lan in new[] { 0.0, 45.0, 200.0, 335.6457, 359.9 })
            foreach (double shift in new[] { 1000.0, 845044935.6, -845044935.6, 11393871.0, 21549.425 * 5450 })
            {
                double r = GhostMapPresence.ApplyLoopShiftLanRotation(lan, shift, KerbinSiderealRotationPeriod);
                Assert.InRange(r, 0.0, 360.0);
                Assert.True(r < 360.0, $"LAN {r} must be < 360 (lan={lan} shift={shift})");
            }
        }

        [Fact]
        public void LanRotation_WholeBodyRotation_IsIdentityModulo360()
        {
            // A shift of an exact whole number of body rotations leaves the body-fixed appearance
            // unchanged, so the seeded LAN equals the recorded LAN (mod 360).
            double recordedLan = 123.456;
            double wholeRotShift = KerbinSiderealRotationPeriod * 7.0; // exactly 7 rotations
            double seeded = GhostMapPresence.ApplyLoopShiftLanRotation(
                recordedLan, wholeRotShift, KerbinSiderealRotationPeriod);
            Assert.Equal(recordedLan, seeded, 6);
        }

        #endregion
    }
}
