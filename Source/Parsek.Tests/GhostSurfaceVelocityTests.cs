using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The ghost surface-velocity contract: ground speed comes from body-fixed recorded
    /// POSITIONS, never from the recorded velocity vector minus the body's rotating-frame
    /// velocity. The recorded vector is already surface-relative for a landed or low vessel, so
    /// that subtraction invented ~175 m/s on a parked Kerbin ghost (wheels spinning, reentry
    /// speed, and a 45 m/s liftoff read by watch mode as ~181 m/s heading west).
    /// </summary>
    public class GhostSurfaceVelocityTests
    {
        // Kerbin: R = 600 km, sidereal day 21549.425 s -> ~174.94 m/s at the equator.
        private const double KerbinRadius = 600000.0;
        private const double KerbinOmega = 2.0 * Math.PI / 21549.425;

        // GS-7 run 2026-10-12_1856, the logged liftoff frame (alt=211m):
        // playbackVel=(-37.6,-0.1,25.7) bodyVel=(-98.2,0.0,-144.9) headingVel=(60.6,-0.1,170.5)
        private static readonly Vector3 Gs7RecordedVelocity = new Vector3(-37.6f, -0.1f, 25.7f);
        private static readonly Vector3 Gs7BodyFrameVelocity = new Vector3(-98.2f, 0.0f, -144.9f);

        #region TryFromBracketPositions

        [Fact]
        public void TryFromBracketPositions_TwoBodyFixedSamples_GiveTheChordVelocity()
        {
            var before = new Vector3d(KerbinRadius, 0, 0);
            var after = new Vector3d(KerbinRadius, 0, 45.0);

            Assert.True(GhostSurfaceVelocity.TryFromBracketPositions(before, after, 1.0, out Vector3 v));

            Assert.Equal(45.0, (double)v.magnitude, 3);
            Assert.Equal(45.0, (double)v.z, 3);
        }

        [Fact]
        public void TryFromBracketPositions_ScalesByTheBracketDuration()
        {
            var before = new Vector3d(0, 0, 0);
            var after = new Vector3d(30.0, 0, 40.0); // 50 m in 5 s

            Assert.True(GhostSurfaceVelocity.TryFromBracketPositions(before, after, 5.0, out Vector3 v));

            Assert.Equal(10.0, (double)v.magnitude, 3);
        }

        [Fact]
        public void TryFromBracketPositions_ParkedSamples_AreAKnownZero()
        {
            var pos = new Vector3d(KerbinRadius, 12.5, -3.0);

            Assert.True(GhostSurfaceVelocity.TryFromBracketPositions(pos, pos, 3.0, out Vector3 v));

            Assert.Equal(0f, v.magnitude);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(0.00001)]
        [InlineData(GhostSurfaceVelocity.MaxBracketSeconds + 0.001)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void TryFromBracketPositions_UnusableDt_IsRefused(double dt)
        {
            var before = new Vector3d(0, 0, 0);
            var after = new Vector3d(10, 0, 0);

            Assert.False(GhostSurfaceVelocity.TryFromBracketPositions(before, after, dt, out Vector3 v));
            Assert.Equal(Vector3.zero, v);
        }

        [Fact]
        public void TryFromBracketPositions_AtTheMaxBracket_IsAccepted()
        {
            Assert.True(GhostSurfaceVelocity.TryFromBracketPositions(
                new Vector3d(0, 0, 0), new Vector3d(60, 0, 0),
                GhostSurfaceVelocity.MaxBracketSeconds, out Vector3 v));
            Assert.Equal(1.0, (double)v.magnitude, 4);
        }

        [Fact]
        public void TryFromBracketPositions_NaNPosition_IsRefused()
        {
            Assert.False(GhostSurfaceVelocity.TryFromBracketPositions(
                new Vector3d(double.NaN, 0, 0), new Vector3d(10, 0, 0), 1.0, out Vector3 v));
            Assert.Equal(Vector3.zero, v);
        }

        #endregion

        #region FindBracketIndex / SelectRelativeSectionBodyFixedFrames

        private static List<TrajectoryPoint> Frames(params double[] uts)
        {
            var list = new List<TrajectoryPoint>();
            foreach (double ut in uts)
                list.Add(new TrajectoryPoint { ut = ut, bodyName = "Kerbin" });
            return list;
        }

        [Theory]
        [InlineData(100.0, 0)]
        [InlineData(105.0, 0)]
        [InlineData(110.0, 1)]
        [InlineData(115.0, 1)]
        [InlineData(129.9, 2)]
        [InlineData(130.0, 2)]
        public void FindBracketIndex_InsideCoverage_ReturnsTheStartSample(double ut, int expected)
        {
            Assert.Equal(expected, GhostSurfaceVelocity.FindBracketIndex(Frames(100, 110, 120, 130), ut));
        }

        [Theory]
        [InlineData(99.9)]
        [InlineData(130.1)]
        [InlineData(double.NaN)]
        public void FindBracketIndex_OutsideCoverage_IsMinusOne(double ut)
        {
            Assert.Equal(-1, GhostSurfaceVelocity.FindBracketIndex(Frames(100, 110, 120, 130), ut));
        }

        [Fact]
        public void FindBracketIndex_FewerThanTwoSamples_IsMinusOne()
        {
            Assert.Equal(-1, GhostSurfaceVelocity.FindBracketIndex(null, 100));
            Assert.Equal(-1, GhostSurfaceVelocity.FindBracketIndex(Frames(100), 100));
        }

        [Fact]
        public void SelectRelativeSectionBodyFixedFrames_RelativeWithoutBodyFixed_IsNull()
        {
            // RELATIVE frames hold anchor-local metre offsets; they must never be read as
            // body-fixed lat/lon/alt, so a section with no shadow frames yields nothing.
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                frames = Frames(100, 110),
                bodyFixedFrames = null,
            };
            Assert.Null(GhostSurfaceVelocity.SelectRelativeSectionBodyFixedFrames(section));

            section.bodyFixedFrames = Frames(100);
            Assert.Null(GhostSurfaceVelocity.SelectRelativeSectionBodyFixedFrames(section));
        }

        [Fact]
        public void SelectRelativeSectionBodyFixedFrames_RelativeWithBodyFixed_ReturnsThem()
        {
            var bodyFixed = Frames(100, 110);
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                frames = Frames(100, 110),
                bodyFixedFrames = bodyFixed,
            };
            Assert.Same(bodyFixed, GhostSurfaceVelocity.SelectRelativeSectionBodyFixedFrames(section));
        }

        [Fact]
        public void SelectRelativeSectionBodyFixedFrames_AbsoluteSection_IsNull()
        {
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                frames = Frames(100, 110),
                bodyFixedFrames = Frames(100, 110),
            };
            Assert.Null(GhostSurfaceVelocity.SelectRelativeSectionBodyFixedFrames(section));
        }

        #endregion

        #region InterpolationResult / GhostPlaybackState carriage

        [Fact]
        public void InterpolationResult_Constructor_LeavesSurfaceVelocityUnknown()
        {
            var r = new InterpolationResult(new Vector3(1, 2, 3), "Kerbin", 100.0);
            Assert.Equal(GhostSurfaceVelocitySource.None, r.surfaceVelocitySource);
            Assert.Equal(Vector3.zero, r.surfaceVelocity);
            Assert.Equal(GhostSurfaceVelocitySource.None, InterpolationResult.Zero.surfaceVelocitySource);
        }

        [Fact]
        public void InterpolationResult_WithSurfaceVelocity_None_ForcesZero()
        {
            var r = new InterpolationResult(Vector3.zero, "Kerbin", 0.0)
                .WithSurfaceVelocity(new Vector3(5, 0, 0), GhostSurfaceVelocitySource.None);
            Assert.Equal(Vector3.zero, r.surfaceVelocity);
        }

        [Fact]
        public void SetInterpolated_CopiesSurfaceVelocity_AndClearResetsIt()
        {
            var state = new GhostPlaybackState();
            state.SetInterpolated(new InterpolationResult(new Vector3(9, 0, 0), "Kerbin", 50.0)
                .WithSurfaceVelocity(new Vector3(4, 0, 0), GhostSurfaceVelocitySource.Positions));

            Assert.Equal(new Vector3(9, 0, 0), state.lastInterpolatedVelocity);
            Assert.Equal(new Vector3(4, 0, 0), state.lastInterpolatedSurfaceVelocity);
            Assert.Equal(GhostSurfaceVelocitySource.Positions, state.lastInterpolatedSurfaceVelocitySource);

            state.ClearInterpolatedVelocities();
            Assert.Equal(Vector3.zero, state.lastInterpolatedVelocity);
            Assert.Equal(Vector3.zero, state.lastInterpolatedSurfaceVelocity);
            Assert.Equal(GhostSurfaceVelocitySource.None, state.lastInterpolatedSurfaceVelocitySource);
        }

        [Fact]
        public void Usable_UnknownSource_IsZero()
        {
            Assert.Equal(Vector3.zero,
                GhostSurfaceVelocity.Usable(new Vector3(300, 0, 0), GhostSurfaceVelocitySource.None));
            Assert.Equal(new Vector3(300, 0, 0),
                GhostSurfaceVelocity.Usable(new Vector3(300, 0, 0), GhostSurfaceVelocitySource.Positions));
        }

        [Fact]
        public void FromOrbitVelocity_SwizzlesZupToWorld_ThenRemovesBodyRotation()
        {
            // Zup (x, y, z) -> world (x, z, y).
            Vector3 surface = GhostSurfaceVelocity.FromOrbitVelocity(
                new Vector3d(100.0, 2200.0, 5.0), new Vector3d(0.0, 0.0, 175.0));

            Assert.Equal(100.0, (double)surface.x, 3);
            Assert.Equal(5.0, (double)surface.y, 3);
            Assert.Equal(2025.0, (double)surface.z, 3);
        }

        [Theory]
        [InlineData(0, "none")]
        [InlineData(1, "positions")]
        [InlineData(2, "surfaceHold")]
        [InlineData(3, "orbit")]
        public void SourceToken_IsGrepStable(int source, string token)
        {
            Assert.Equal(token, GhostSurfaceVelocity.SourceToken((GhostSurfaceVelocitySource)source));
        }

        #endregion

        #region Wheels

        [Fact]
        public void Wheels_ParkedRoverAtKsc_HoldStill()
        {
            // A rover parked on Kerbin's equator: body-fixed samples do not move, so the surface
            // velocity is a measured zero. The old reader subtracted getRFrmVel (~175 m/s here)
            // from an already-surface-relative recorded vector and spun the wheels at highway speed.
            var ghostPos = new Vector3d(KerbinRadius, 0, 0);
            var bodyPos = new Vector3d(0, 0, 0);

            Assert.True(GhostPlaybackLogic.TryComputeWheelGroundInputs(
                ghostPos, bodyPos, Vector3.zero, GhostSurfaceVelocitySource.Positions,
                out Vector3 up, out Vector3 horizontal));

            Assert.True(horizontal.magnitude < 1e-3f,
                "a parked rover's ground speed must read ~0, got " +
                horizontal.magnitude.ToString("R", CultureInfo.InvariantCulture) + " m/s");
            Vector3 roll = GhostPlaybackLogic.ComputeWheelRollForward(new Vector3(0f, 1f, 0f), up);
            float deg = GhostPlaybackLogic.ComputeWheelSpinDeltaDegrees(horizontal, roll, 0.4f, 1.0);
            Assert.Equal(0f, deg);
        }

        [Fact]
        public void Wheels_DrivingRover_SpinAtTheSurfaceSpeed()
        {
            var ghostPos = new Vector3d(KerbinRadius, 0, 0);
            var surface = new Vector3(0f, 0f, 8f); // 8 m/s along the surface

            Assert.True(GhostPlaybackLogic.TryComputeWheelGroundInputs(
                ghostPos, Vector3d.zero, surface, GhostSurfaceVelocitySource.Positions,
                out Vector3 up, out Vector3 horizontal));

            Assert.Equal(8.0, (double)horizontal.magnitude, 3);
            Vector3 roll = GhostPlaybackLogic.ComputeWheelRollForward(new Vector3(0f, 1f, 0f), up);
            float deg = GhostPlaybackLogic.ComputeWheelSpinDeltaDegrees(horizontal, roll, 0.4f, 1.0);
            Assert.Equal(20.0 * Mathf.Rad2Deg, (double)Mathf.Abs(deg), 1);
        }

        [Fact]
        public void Wheels_UnknownSurfaceVelocity_DeclineInsteadOfInventing()
        {
            Assert.False(GhostPlaybackLogic.TryComputeWheelGroundInputs(
                new Vector3d(KerbinRadius, 0, 0), Vector3d.zero,
                new Vector3(175f, 0f, 0f), GhostSurfaceVelocitySource.None,
                out _, out Vector3 horizontal));
            Assert.Equal(Vector3.zero, horizontal);
        }

        [Fact]
        public void Wheels_GhostAtBodyCentre_Decline()
        {
            Assert.False(GhostPlaybackLogic.TryComputeWheelGroundInputs(
                Vector3d.zero, Vector3d.zero, new Vector3(5f, 0f, 0f),
                GhostSurfaceVelocitySource.Positions, out _, out _));
        }

        [Fact]
        public void Wheels_DiagnosticLine_IsInvariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string line = GhostPlaybackLogic.FormatWheelGroundSpeedLine(
                    "Rover", "rec-1", 0.01234f, "positions");
                Assert.Equal("Wheel ground speed for \"Rover\" rec=rec-1 speed=0.01 source=positions", line);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        #endregion

        #region Reentry

        [Fact]
        public void Reentry_PadGhost_ReadsNoSpeed_AndBuildsNoFx()
        {
            // A ghost on the pad: measured zero surface velocity. The old reader produced
            // |recorded - getRFrmVel| ~ 175 m/s here (and up to 175 m/s of error at any speed
            // below 70 km), which fed mach and the FX direction.
            Vector3 surface = GhostSurfaceVelocity.Usable(Vector3.zero, GhostSurfaceVelocitySource.Positions);
            Assert.Equal(0f, surface.magnitude);
            Assert.False(GhostPlaybackLogic.ShouldBuildLazyReentryFx(
                pendingFlag: true, bodyName: "Kerbin",
                bodyHasAtmosphere: true, altitudeMeters: 80.0, atmosphereDepthMeters: 70000.0,
                surfaceSpeedMetersPerSecond: surface.magnitude,
                speedFloorMetersPerSecond: TrajectoryMath.ReentryPotentialSpeedFloor));
        }

        #endregion

        #region Watch horizon

        [Fact]
        public void WatchHorizon_Gs7Liftoff_Heads45MetersPerSecond_Not181()
        {
            // At 211 m the recorded vector IS the surface velocity (the world co-rotates there),
            // so the position-derived surface velocity equals it. The old basis subtracted bodyVel
            // and headed ~181 m/s the wrong way.
            Vector3 up = Vector3.up;
            var (forward, horizonVelocity, headingVelocity, headingFrame, source) =
                ParsekFlight.ComputeWatchHorizonBasis(
                    hasAtmosphere: true, atmosphereDepth: 70000, inverseRotThresholdAltitude: 100000, altitude: 211,
                    up, Gs7RecordedVelocity, GhostSurfaceVelocitySource.Positions,
                    Gs7RecordedVelocity, Gs7BodyFrameVelocity, Vector3.forward);

            Assert.Equal(WatchModeController.HeadingFrameSurface, headingFrame);
            Assert.Equal(HorizonForwardSource.ProjectedVelocity, source);
            Assert.InRange((double)headingVelocity.magnitude, 45.4, 45.7);
            Assert.True(horizonVelocity.magnitude < 50f,
                "liftoff horizon speed must be ~45 m/s, got " +
                horizonVelocity.magnitude.ToString("R", CultureInfo.InvariantCulture));
            Assert.True(Vector3.Dot(forward, Gs7RecordedVelocity.normalized) > 0.99f,
                "forward must follow the recorded ground track, got " + forward);
        }

        [Fact]
        public void WatchHorizon_InAtmosphere_UnknownSurfaceVelocity_FallsBackToLastForward()
        {
            Vector3 up = Vector3.up;
            var (forward, horizonVelocity, headingVelocity, headingFrame, source) =
                ParsekFlight.ComputeWatchHorizonBasis(
                    hasAtmosphere: true, atmosphereDepth: 70000, inverseRotThresholdAltitude: 100000, altitude: 10000,
                    up, new Vector3(500f, 0f, 0f), GhostSurfaceVelocitySource.None,
                    new Vector3(120f, 0f, 0f), new Vector3(200f, 0f, 0f), Vector3.forward);

            Assert.Equal(WatchModeController.HeadingFrameSurfaceUnknown, headingFrame);
            Assert.Equal(HorizonForwardSource.LastForwardFallback, source);
            Assert.Equal(Vector3.zero, headingVelocity);
            Assert.True(Vector3.Dot(forward, Vector3.forward) > 0.99f, "got " + forward);
        }

        [Fact]
        public void WatchHorizon_AboveAtmosphere_HeadsInertial_SurfacePlusBodyRotation()
        {
            Vector3 up = Vector3.up;
            var (forward, _, headingVelocity, headingFrame, _) =
                ParsekFlight.ComputeWatchHorizonBasis(
                    hasAtmosphere: true, atmosphereDepth: 70000, inverseRotThresholdAltitude: 100000, altitude: 120000,
                    up, new Vector3(0f, 0f, 2100f), GhostSurfaceVelocitySource.Positions,
                    new Vector3(-999f, 0f, 0f), new Vector3(0f, 0f, 175f), Vector3.right);

            Assert.Equal(WatchModeController.HeadingFrameInertialFromSurface, headingFrame);
            Assert.Equal(2275.0, (double)headingVelocity.z, 2);
            Assert.True(Vector3.Dot(forward, Vector3.forward) > 0.99f, "got " + forward);
        }

        [Fact]
        public void WatchHorizon_AboveAtmosphere_UnknownSurfaceVelocity_KeepsPlayback()
        {
            Vector3 up = Vector3.up;
            var (forward, _, headingVelocity, headingFrame, _) =
                ParsekFlight.ComputeWatchHorizonBasis(
                    hasAtmosphere: true, atmosphereDepth: 70000, inverseRotThresholdAltitude: 100000, altitude: 120000,
                    up, Vector3.zero, GhostSurfaceVelocitySource.None,
                    new Vector3(120f, 0f, 0f), new Vector3(200f, 0f, 0f), Vector3.forward);

            Assert.Equal(WatchModeController.HeadingFramePlayback, headingFrame);
            Assert.Equal(new Vector3(120f, 0f, 0f), headingVelocity);
            Assert.True(Vector3.Dot(forward, Vector3.right) > 0.99f, "got " + forward);
        }

        // Airless bodies (review of PR #2152): no atmosphere, but below the body's
        // inverse-rotation altitude the world co-rotates and the ground track is what matters.
        // The spin term (about 9 m/s on the Mun) must not swing a rover's heading east.
        [Fact]
        public void WatchHorizon_AirlessBelowInverseRotation_DrivingRover_FollowsTheGroundTrack()
        {
            Vector3 up = Vector3.up;
            var (forward, _, headingVelocity, headingFrame, source) =
                ParsekFlight.ComputeWatchHorizonBasis(
                    hasAtmosphere: false, atmosphereDepth: 0, inverseRotThresholdAltitude: 15000, altitude: 5,
                    up, new Vector3(0f, 0f, 3f), GhostSurfaceVelocitySource.Positions,
                    new Vector3(0f, 0f, 3f), new Vector3(9f, 0f, 0f), Vector3.right);

            Assert.Equal(WatchModeController.HeadingFrameSurface, headingFrame);
            Assert.Equal(HorizonForwardSource.ProjectedVelocity, source);
            Assert.Equal(new Vector3(0f, 0f, 3f), headingVelocity);
            Assert.True(Vector3.Dot(forward, Vector3.forward) > 0.99f, "got " + forward);
        }

        [Fact]
        public void WatchHorizon_AirlessBelowInverseRotation_ParkedRover_KeepsLastForward()
        {
            Vector3 up = Vector3.up;
            var (forward, _, _, headingFrame, source) =
                ParsekFlight.ComputeWatchHorizonBasis(
                    hasAtmosphere: false, atmosphereDepth: 0, inverseRotThresholdAltitude: 15000, altitude: 5,
                    up, Vector3.zero, GhostSurfaceVelocitySource.SurfaceHold,
                    Vector3.zero, new Vector3(9f, 0f, 0f), Vector3.forward);

            Assert.Equal(WatchModeController.HeadingFrameSurface, headingFrame);
            Assert.Equal(HorizonForwardSource.LastForwardFallback, source);
            Assert.True(Vector3.Dot(forward, Vector3.forward) > 0.99f, "got " + forward);
        }

        [Fact]
        public void WatchHorizon_AirlessAboveInverseRotation_HeadsInertial()
        {
            Vector3 up = Vector3.up;
            var (_, _, headingVelocity, headingFrame, _) =
                ParsekFlight.ComputeWatchHorizonBasis(
                    hasAtmosphere: false, atmosphereDepth: 0, inverseRotThresholdAltitude: 15000, altitude: 30000,
                    up, new Vector3(0f, 0f, 500f), GhostSurfaceVelocitySource.Positions,
                    Vector3.zero, new Vector3(9f, 0f, 0f), Vector3.right);

            Assert.Equal(WatchModeController.HeadingFrameInertialFromSurface, headingFrame);
            Assert.Equal(new Vector3(9f, 0f, 500f), headingVelocity);
        }

        #endregion

        #region Stock-read surface speed

        [Fact]
        public void TryRecordedSurfaceSpeed_AgreeingPair_InterpolatesTheRecordedSpeeds()
        {
            // A reentry bracket: 1120 -> 1070 m/s over 0.5 s, chord 1095 (its mean).
            Assert.True(GhostSurfaceVelocity.TryRecordedSurfaceSpeed(1095.0, 1120.0, 1070.0, 0.2, out float speed));
            Assert.Equal(1110.0f, speed, 0.01f);
            Assert.True(GhostSurfaceVelocity.TryRecordedSurfaceSpeed(1095.0, 1120.0, 1070.0, 0.0, out speed));
            Assert.Equal(1120.0f, speed, 0.01f);
            Assert.True(GhostSurfaceVelocity.TryRecordedSurfaceSpeed(1095.0, 1120.0, 1070.0, 1.0, out speed));
            Assert.Equal(1070.0f, speed, 0.01f);
        }

        [Fact]
        public void TryRecordedSurfaceSpeed_ReadsBackAtMostOneBracket_AndNeverBelowZero()
        {
            // Below 0 reads the bracket's own trend back (stock's smoothing lag at a bracket start).
            Assert.True(GhostSurfaceVelocity.TryRecordedSurfaceSpeed(1095.0, 1120.0, 1070.0, -0.18, out float speed));
            Assert.Equal(1129.0f, speed, 0.01f);
            Assert.True(GhostSurfaceVelocity.TryRecordedSurfaceSpeed(1095.0, 1120.0, 1070.0, -5.0, out speed));
            Assert.Equal(1170.0f, speed, 0.01f);
            Assert.True(GhostSurfaceVelocity.TryRecordedSurfaceSpeed(1095.0, 1120.0, 1070.0, 1.5, out speed));
            Assert.Equal(1070.0f, speed, 0.01f);
            Assert.True(GhostSurfaceVelocity.TryRecordedSurfaceSpeed(2.0, 1.0, 3.0, -1.0, out speed));
            Assert.Equal(0f, speed);
        }

        [Fact]
        public void StockSpeedFraction_ReadsFourAndAHalfPhysicsFramesBack()
        {
            // 10 frames at 0.02 s: the mean is 0.09 s back; in a 0.5 s bracket that is 0.18.
            Assert.Equal(0.32, GhostSurfaceVelocity.StockSpeedFraction(0.5, 0.5, 0.02), 6);
            Assert.Equal(-0.18, GhostSurfaceVelocity.StockSpeedFraction(0.0, 0.5, 0.02), 6);
            // A bracket shorter than the lag reads back at most one bracket.
            Assert.Equal(-1.0, GhostSurfaceVelocity.StockSpeedFraction(0.0, 0.02, 0.02), 6);
            Assert.Equal(0.7, GhostSurfaceVelocity.StockSpeedFraction(0.7, 0.5, 0.0), 6);
        }

        [Theory]
        [InlineData(double.NaN, 0.5, 0.02)]
        [InlineData(0.5, double.NaN, 0.02)]
        [InlineData(0.5, 0.5, double.NaN)]
        [InlineData(0.5, 0.0, 0.02)]
        [InlineData(0.5, 0.5, -0.02)]
        public void StockSpeedFraction_UnusableInputs_ReturnTheFraction(double t, double bracket, double dt)
        {
            double got = GhostSurfaceVelocity.StockSpeedFraction(t, bracket, dt);
            if (double.IsNaN(t)) Assert.True(double.IsNaN(got));
            else Assert.Equal(t, got);
        }

        [Fact]
        public void StockSpeed_AtTheRigsMach375Mark_MatchesStocksReading()
        {
            // Flight 3 (2026-11-28_0203): about 100 m/s per s of deceleration at m375. A 0.5 s
            // bracket 1120 -> 1070 m/s whose start the ghost plays: the chord reads 1095, stock's
            // ten-frame mean about 1129 (the speed 0.09 s earlier).
            double fraction = GhostSurfaceVelocity.StockSpeedFraction(0.0, 0.5, 0.02);
            Assert.True(GhostSurfaceVelocity.TryRecordedSurfaceSpeed(1095.0, 1120.0, 1070.0, fraction, out float speed));
            Assert.Equal(1129.0f, speed, 0.01f);
        }

        [Theory]
        // An inertial pair over an eastbound track: the body's rotation (Kerbin ~175 m/s) sits in
        // the recorded magnitudes and not in the body-fixed chord.
        [InlineData(1095.0, 1295.0, 1245.0)]
        [InlineData(1095.0, 920.0, 870.0)]
        // Just past the 1.5 % band.
        [InlineData(1000.0, 1016.0, 1016.0)]
        // Just past the 2 m/s floor at low speed.
        [InlineData(10.0, 12.5, 12.5)]
        public void TryRecordedSurfaceSpeed_DisagreeingPair_KeepsTheChord(double chord, double before, double after)
        {
            Assert.False(GhostSurfaceVelocity.TryRecordedSurfaceSpeed(chord, before, after, 0.5, out float speed));
            Assert.Equal(0f, speed);
        }

        [Theory]
        [InlineData(1000.0, 1014.0, 1014.0)]
        [InlineData(10.0, 11.9, 11.9)]
        [InlineData(0.0, 0.0, 0.0)]
        public void TryRecordedSurfaceSpeed_InsideTheBand_IsAccepted(double chord, double before, double after)
        {
            Assert.True(GhostSurfaceVelocity.TryRecordedSurfaceSpeed(chord, before, after, 0.5, out _));
        }

        [Theory]
        [InlineData(double.NaN, 1.0, 1.0, 0.5)]
        [InlineData(1.0, double.NaN, 1.0, 0.5)]
        [InlineData(1.0, 1.0, double.PositiveInfinity, 0.5)]
        [InlineData(1.0, 1.0, 1.0, double.NaN)]
        [InlineData(-1.0, 1.0, 1.0, 0.5)]
        [InlineData(1.0, -1.0, 1.0, 0.5)]
        public void TryRecordedSurfaceSpeed_UnusableInputs_AreRefused(double chord, double before, double after, double t)
        {
            Assert.False(GhostSurfaceVelocity.TryRecordedSurfaceSpeed(chord, before, after, t, out _));
        }

        [Fact]
        public void SpeedForAeroFx_UsesTheStockSpeedOnlyForItsOwnChord()
        {
            var chord = new Vector3(0f, -600f, 900f);
            float got = GhostSurfaceVelocity.SpeedForAeroFx(chord, GhostSurfaceVelocitySource.Positions,
                1110f, chord, out bool used);
            Assert.True(used);
            Assert.Equal(1110f, got);

            // Another writer replaced the surface velocity: the stock speed no longer applies.
            var other = new Vector3(0f, -500f, 800f);
            got = GhostSurfaceVelocity.SpeedForAeroFx(other, GhostSurfaceVelocitySource.Positions,
                1110f, chord, out used);
            Assert.False(used);
            Assert.Equal(other.magnitude, got, 0.01f);
        }

        [Fact]
        public void SpeedForAeroFx_NoStockSpeed_OrOtherSource_ReadsTheChord()
        {
            var chord = new Vector3(0f, -600f, 900f);
            Assert.Equal(chord.magnitude, GhostSurfaceVelocity.SpeedForAeroFx(chord,
                GhostSurfaceVelocitySource.Positions, float.NaN, chord, out bool used), 0.01f);
            Assert.False(used);
            Assert.Equal(chord.magnitude, GhostSurfaceVelocity.SpeedForAeroFx(chord,
                GhostSurfaceVelocitySource.Orbit, 1110f, chord, out used), 0.01f);
            Assert.False(used);
            Assert.Equal(0f, GhostSurfaceVelocity.SpeedForAeroFx(chord,
                GhostSurfaceVelocitySource.None, 1110f, chord, out used));
            Assert.False(used);
        }

        [Fact]
        public void InterpolationResult_SurfaceVelocityChange_DropsTheStockSpeed()
        {
            var r = new InterpolationResult(Vector3.zero, "Kerbin", 20000.0);
            Assert.True(float.IsNaN(r.stockSurfaceSpeed));
            r = r.WithSurfaceVelocity(new Vector3(1f, 0f, 0f), GhostSurfaceVelocitySource.Positions)
                .WithStockSurfaceSpeed(5f);
            Assert.Equal(5f, r.stockSurfaceSpeed);
            r = r.WithSurfaceVelocity(new Vector3(2f, 0f, 0f), GhostSurfaceVelocitySource.Positions);
            Assert.True(float.IsNaN(r.stockSurfaceSpeed));
            Assert.True(float.IsNaN(InterpolationResult.Zero.stockSurfaceSpeed));
        }

        [Fact]
        public void PlaybackState_SetInterpolated_CarriesTheStockSpeedWithItsChord()
        {
            var state = new GhostPlaybackState();
            Assert.True(float.IsNaN(state.lastInterpolatedStockSurfaceSpeed));
            var chord = new Vector3(0f, -600f, 900f);
            state.SetInterpolated(new InterpolationResult(Vector3.zero, "Kerbin", 20000.0)
                .WithSurfaceVelocity(chord, GhostSurfaceVelocitySource.Positions)
                .WithStockSurfaceSpeed(1110f));
            Assert.Equal(1110f, state.lastInterpolatedStockSurfaceSpeed);
            Assert.Equal(chord, state.lastInterpolatedStockSpeedChord);
            state.ClearInterpolatedVelocities();
            Assert.True(float.IsNaN(state.lastInterpolatedStockSurfaceSpeed));
            Assert.Equal(Vector3.zero, state.lastInterpolatedStockSpeedChord);
        }

        #endregion

    }
}
