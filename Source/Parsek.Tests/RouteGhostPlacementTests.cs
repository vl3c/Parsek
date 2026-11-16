using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek.InGameTests;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="RouteGhostPlacement"/>, the pure core of the
    /// <c>RouteGhostPlacement</c> in-game cell (route render lanes H59 / V27M, audit B3).
    /// </summary>
    public class RouteGhostPlacementTests
    {
        private const double KerbinRadius = 600000.0;

        private static TrajectoryPoint Pt(double ut, double lat, double lon, double alt,
            string body = "Kerbin", double clearance = double.NaN, float vx = 0f)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                bodyName = body,
                recordedGroundClearance = clearance,
                velocity = new Vector3(vx, 0f, 0f),
                rotation = Quaternion.identity,
            };
        }

        private static TrackSection Section(ReferenceFrame frame, double start, double end,
            List<TrajectoryPoint> frames = null, List<TrajectoryPoint> bodyFixed = null)
        {
            return new TrackSection
            {
                referenceFrame = frame,
                startUT = start,
                endUT = end,
                frames = frames,
                bodyFixedFrames = bodyFixed,
            };
        }

        // ---------------------------------------------------------------
        //  Surface selection
        // ---------------------------------------------------------------

        [Fact]
        public void NoSections_ReadsTheFlatPoints()
        {
            var flat = new List<TrajectoryPoint> { Pt(10, 0, 0, 70), Pt(20, 0, 0.001, 70) };
            Assert.True(RouteGhostPlacement.TrySelectSurface(new List<TrackSection>(), flat, 15,
                out IList<TrajectoryPoint> frames, out string source));
            Assert.Same(flat, frames);
            Assert.Equal("flat-points", source);
        }

        [Fact]
        public void NoSections_OnePoint_IsUnsupported()
        {
            var flat = new List<TrajectoryPoint> { Pt(10, 0, 0, 70) };
            Assert.False(RouteGhostPlacement.TrySelectSurface(null, flat, 10, out _, out string source));
            Assert.Equal("no-points", source);
        }

        [Fact]
        public void AbsoluteSection_ReadsItsFrames_NeverTheFlatList()
        {
            var sectionFrames = new List<TrajectoryPoint> { Pt(10, 1, 1, 70), Pt(20, 1, 1.001, 70) };
            // A flat list that would read as a point deep inside the planet if used.
            var flat = new List<TrajectoryPoint> { Pt(10, 12.5, -40.0, -3.0), Pt(20, 13.0, -41.0, -3.0) };
            var sections = new List<TrackSection> { Section(ReferenceFrame.Absolute, 10, 20, sectionFrames) };
            Assert.True(RouteGhostPlacement.TrySelectSurface(sections, flat, 15,
                out IList<TrajectoryPoint> frames, out string source));
            Assert.Same(sectionFrames, frames);
            Assert.Equal("absolute-frames", source);
        }

        [Fact]
        public void RelativeSection_ReadsTheBodyFixedShadow_NotTheAnchorOffsets()
        {
            var offsets = new List<TrajectoryPoint> { Pt(10, 12.5, -40.0, -3.0), Pt(20, 13.0, -41.0, -3.0) };
            var shadow = new List<TrajectoryPoint> { Pt(10, 1, 1, 70), Pt(20, 1, 1.001, 70) };
            var sections = new List<TrackSection>
            {
                Section(ReferenceFrame.Relative, 10, 20, offsets, shadow),
            };
            Assert.True(RouteGhostPlacement.TrySelectSurface(sections, null, 12,
                out IList<TrajectoryPoint> frames, out string source));
            Assert.Same(shadow, frames);
            Assert.Equal("relative-body-fixed", source);
        }

        [Theory]
        [InlineData(false, 15.0)] // no shadow at all
        [InlineData(true, 19.5)]  // shadow ends before the UT
        public void RelativeSection_WithoutCoveringShadow_IsUnsupported(bool hasShadow, double ut)
        {
            var offsets = new List<TrajectoryPoint> { Pt(10, 5, 5, 5), Pt(20, 6, 6, 6) };
            var shadow = hasShadow
                ? new List<TrajectoryPoint> { Pt(10, 1, 1, 70), Pt(18, 1, 1.001, 70) }
                : null;
            var sections = new List<TrackSection> { Section(ReferenceFrame.Relative, 10, 20, offsets, shadow) };
            Assert.False(RouteGhostPlacement.TrySelectSurface(sections, null, ut, out _, out string source));
            Assert.Equal("relative-no-body-fixed", source);
        }

        [Fact]
        public void OrbitalCheckpointSection_IsUnsupported()
        {
            var sections = new List<TrackSection> { Section(ReferenceFrame.OrbitalCheckpoint, 10, 20) };
            Assert.False(RouteGhostPlacement.TrySelectSurface(sections, null, 15, out _, out string source));
            Assert.Equal("orbital-checkpoint", source);
        }

        [Fact]
        public void UtOutsideEverySection_IsUnsupported()
        {
            var sections = new List<TrackSection>
            {
                Section(ReferenceFrame.Absolute, 10, 20, new List<TrajectoryPoint> { Pt(10, 0, 0, 0), Pt(20, 0, 0, 0) }),
            };
            Assert.False(RouteGhostPlacement.TrySelectSurface(sections, null, 25, out _, out string source));
            Assert.Equal("no-section-at-ut", source);
        }

        [Fact]
        public void FindSectionIndex_SharedBoundaryGoesToTheLaterSection_LastEndInclusive()
        {
            var sections = new List<TrackSection>
            {
                Section(ReferenceFrame.Absolute, 10, 20),
                Section(ReferenceFrame.Relative, 20, 30),
            };
            Assert.Equal(0, RouteGhostPlacement.FindSectionIndex(sections, 10));
            Assert.Equal(1, RouteGhostPlacement.FindSectionIndex(sections, 20));
            Assert.Equal(1, RouteGhostPlacement.FindSectionIndex(sections, 30));
            Assert.Equal(-1, RouteGhostPlacement.FindSectionIndex(sections, 30.001));
            Assert.Equal(-1, RouteGhostPlacement.FindSectionIndex(sections, 9.999));
        }

        // ---------------------------------------------------------------
        //  Interpolation
        // ---------------------------------------------------------------

        [Fact]
        public void Interpolate_LerpsBetweenTheBracketingSamples()
        {
            var frames = new List<TrajectoryPoint>
            {
                Pt(10, 0.0, -74.0, 70.0, clearance: 5.0, vx: 2f),
                Pt(20, 0.1, -74.2, 80.0, clearance: 6.0, vx: 4f),
                Pt(30, 0.3, -74.6, 90.0, clearance: 7.0, vx: 6f),
            };
            Assert.True(RouteGhostPlacement.TryInterpolate(frames, 25,
                out RouteGhostPlacement.BodyFixedSample s, out string reason));
            Assert.Equal("ok", reason);
            Assert.Equal("Kerbin", s.Body);
            Assert.Equal(0.2, s.Latitude, 9);
            Assert.Equal(-74.4, s.Longitude, 9);
            Assert.Equal(85.0, s.Altitude, 9);
            Assert.Equal(6.5, s.Clearance, 9);
            Assert.Equal(20.0, s.BeforeUT);
            Assert.Equal(30.0, s.AfterUT);
            Assert.Equal(5.0, s.SpeedMps, 6);
        }

        [Fact]
        public void Interpolate_AtExactSampleUts_ReturnsTheSample()
        {
            var frames = new List<TrajectoryPoint> { Pt(10, 1, 2, 3), Pt(20, 4, 5, 6) };
            Assert.True(RouteGhostPlacement.TryInterpolate(frames, 10, out var a, out _));
            Assert.Equal(1.0, a.Latitude, 12);
            Assert.True(RouteGhostPlacement.TryInterpolate(frames, 20, out var b, out _));
            Assert.Equal(4.0, b.Latitude, 12);
            Assert.Equal(5.0, b.Longitude, 12);
        }

        [Fact]
        public void Interpolate_ClearanceIsNaNUnlessBothBracketsCarryOne()
        {
            var frames = new List<TrajectoryPoint> { Pt(10, 0, 0, 70, clearance: 5.0), Pt(20, 0, 0, 70) };
            Assert.True(RouteGhostPlacement.TryInterpolate(frames, 15, out var s, out _));
            Assert.True(double.IsNaN(s.Clearance));
        }

        [Fact]
        public void Interpolate_CrossesTheAntimeridianTheShortWay()
        {
            var frames = new List<TrajectoryPoint> { Pt(0, 0, 179.0, 0), Pt(10, 0, -179.0, 0) };
            Assert.True(RouteGhostPlacement.TryInterpolate(frames, 5, out var s, out _));
            Assert.Equal(180.0, s.Longitude, 9);
            Assert.True(RouteGhostPlacement.TryInterpolate(frames, 7.5, out var s2, out _));
            Assert.Equal(-179.5, s2.Longitude, 9);
        }

        [Theory]
        [InlineData(9.0, "before-first-frame")]
        [InlineData(21.0, "after-last-frame")]
        public void Interpolate_OutsideTheSamples_Refuses(double ut, string expected)
        {
            var frames = new List<TrajectoryPoint> { Pt(10, 0, 0, 0), Pt(20, 0, 0, 0) };
            Assert.False(RouteGhostPlacement.TryInterpolate(frames, ut, out _, out string reason));
            Assert.Equal(expected, reason);
        }

        [Fact]
        public void Interpolate_BodyChangeBetweenBrackets_Refuses()
        {
            var frames = new List<TrajectoryPoint> { Pt(10, 0, 0, 0, "Kerbin"), Pt(20, 0, 0, 0, "Mun") };
            Assert.False(RouteGhostPlacement.TryInterpolate(frames, 15, out _, out string reason));
            Assert.Equal("body-change", reason);
        }

        [Fact]
        public void Interpolate_TooFewFrames_Refuses()
        {
            Assert.False(RouteGhostPlacement.TryInterpolate(new List<TrajectoryPoint> { Pt(10, 0, 0, 0) }, 10,
                out _, out string reason));
            Assert.Equal("too-few-frames", reason);
            Assert.False(RouteGhostPlacement.TryInterpolate(null, 10, out _, out reason));
            Assert.Equal("too-few-frames", reason);
        }

        // ---------------------------------------------------------------
        //  Geometry and tolerances
        // ---------------------------------------------------------------

        [Theory]
        [InlineData(190.0, -170.0)]
        [InlineData(-190.0, 170.0)]
        [InlineData(180.0, 180.0)]
        [InlineData(-180.0, 180.0)]
        [InlineData(540.0, 180.0)]
        [InlineData(45.0, 45.0)]
        public void WrapDegrees_IntoHalfOpenRange(double input, double expected)
        {
            Assert.Equal(expected, RouteGhostPlacement.WrapDegrees(input), 9);
        }

        [Fact]
        public void GreatCircle_OneDegreeOfLatitudeOnKerbin()
        {
            double d = RouteGhostPlacement.GreatCircleMeters(0, 0, 1, 0, KerbinRadius);
            Assert.Equal(2.0 * Math.PI * KerbinRadius / 360.0, d, 3);
        }

        [Fact]
        public void GreatCircle_MetreScaleAndAcrossTheAntimeridian()
        {
            // 1e-4 deg of longitude at the equator, the rover fixture's sample spacing scale.
            double d = RouteGhostPlacement.GreatCircleMeters(0, -74.7243, 0, -74.7242, KerbinRadius);
            Assert.Equal(KerbinRadius * 1e-4 * Math.PI / 180.0, d, 6);
            double wrap = RouteGhostPlacement.GreatCircleMeters(0, 179.9999, 0, -179.9999, KerbinRadius);
            Assert.Equal(KerbinRadius * 2e-4 * Math.PI / 180.0, wrap, 6);
            Assert.Equal(0.0, RouteGhostPlacement.GreatCircleMeters(10, 20, 10, 20, KerbinRadius), 9);
        }

        [Fact]
        public void HorizontalTolerance_FloorPlusGridPlusOneFrameOfMotion()
        {
            Assert.Equal(3.0, RouteGhostPlacement.HorizontalToleranceMeters(0, 0, 0), 12);
            Assert.Equal(3.0 + 0.25 + 10.0 * 0.02,
                RouteGhostPlacement.HorizontalToleranceMeters(0.25, 10.0, 0.02), 12);
            // A long frame (a hitch) is capped so a stall cannot widen the gate without bound.
            Assert.Equal(3.0 + 100.0 * RouteGhostPlacement.MaxFrameSeconds,
                RouteGhostPlacement.HorizontalToleranceMeters(0, 100.0, 5.0), 12);
            // Non-finite / negative inputs contribute nothing.
            Assert.Equal(3.0, RouteGhostPlacement.HorizontalToleranceMeters(double.NaN, -5, double.NaN), 12);
        }

        [Fact]
        public void VerticalWindow_SpansRecordedAndTerrainPlusClearance_AndTheClampLift()
        {
            RouteGhostPlacement.VerticalWindow(70.0, 64.0, 5.5, out double min, out double max);
            // candidates: recorded 70, phase7 69.5, clamp top 69 -> [69.5 - 10, 70 + 10]
            Assert.Equal(59.5, min, 9);
            Assert.Equal(80.0, max, 9);

            RouteGhostPlacement.VerticalWindow(70.0, 90.0, double.NaN, out min, out max);
            // terrain risen above the recorded altitude: the clamp may lift to terrain + 5
            Assert.Equal(60.0, min, 9);
            Assert.Equal(105.0, max, 9);

            RouteGhostPlacement.VerticalWindow(70.0, double.NaN, 5.5, out min, out max);
            Assert.Equal(60.0, min, 9);
            Assert.Equal(80.0, max, 9);
        }

        // ---------------------------------------------------------------
        //  Verdict
        // ---------------------------------------------------------------

        [Fact]
        public void Compare_OnTheRecordedPlace_IsOk()
        {
            var v = RouteGhostPlacement.Compare(0.4, 3.2, 71.0, 60.0, 80.0, 70.0, 0.0);
            Assert.True(v.Ok);
            Assert.Equal("ok", v.Reason);
            Assert.Equal(1.0, v.VerticalDeltaMeters, 9);
        }

        [Fact]
        public void Compare_OneCycleOff_IsHorizontal()
        {
            var v = RouteGhostPlacement.Compare(140.0, 3.2, 71.0, 60.0, 80.0, 70.0, 0.0);
            Assert.False(v.Ok);
            Assert.Equal("horizontal", v.Reason);
        }

        [Fact]
        public void Compare_InsideThePlanet_IsVertical()
        {
            var v = RouteGhostPlacement.Compare(1.0, 3.2, -5000.0, 60.0, 80.0, 70.0, 0.0);
            Assert.False(v.Ok);
            Assert.Equal("vertical", v.Reason);
        }

        [Fact]
        public void Compare_RenderAndDeliveryClocksDisagree_IsClockMismatch_EvenWhenPlacementAgrees()
        {
            var v = RouteGhostPlacement.Compare(0.0, 3.2, 70.0, 60.0, 80.0, 70.0, 0.5);
            Assert.False(v.Ok);
            Assert.Equal("clock-mismatch", v.Reason);
            Assert.True(RouteGhostPlacement.Compare(0.0, 3.2, 70.0, 60.0, 80.0, 70.0, 1e-9).Ok);
        }

        [Fact]
        public void Compare_NonFinite_IsNeverOk()
        {
            Assert.Equal("non-finite",
                RouteGhostPlacement.Compare(double.NaN, 3.2, 70.0, 60.0, 80.0, 70.0, 0.0).Reason);
            Assert.Equal("non-finite",
                RouteGhostPlacement.Compare(1.0, 3.2, double.PositiveInfinity, 60.0, 80.0, 70.0, 0.0).Reason);
            Assert.Equal("clock-mismatch",
                RouteGhostPlacement.Compare(1.0, 3.2, 70.0, 60.0, 80.0, 70.0, double.NaN).Reason);
        }

        // ---------------------------------------------------------------
        //  Log lines (the lanes pin the summary; invariant culture)
        // ---------------------------------------------------------------

        [Fact]
        public void FormatSummary_IsInvariantAndCarriesEveryCounter()
        {
            CultureInfo prior = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string line = RouteGhostPlacement.FormatSummary("FLIGHT", 1, 42, 30, 30, 0, 3, 0, 0, 0,
                    0.123, "Kerbin");
                Assert.Equal(
                    "RouteGhostPlacement summary: scene=FLIGHT routes=1 frames=42 compared=30 ok=30 bad=0 "
                    + "outsideWindow=3 hidden=0 unsupported=0 notLive=0 worstHorizM=0.12 bodies=[Kerbin]",
                    line);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prior;
            }
        }

        [Fact]
        public void FormatSampleLine_IsInvariant()
        {
            CultureInfo prior = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var v = RouteGhostPlacement.Compare(0.5, 3.25, 71.0, 60.0, 80.0, 70.0, 0.0);
                string line = RouteGhostPlacement.FormatSampleLine("R", 3, "abc", "B", "Kerbin",
                    "absolute-frames", 1610.5, 490.25, 12, v);
                Assert.Contains("ut=1610.50 loopUT=490.25 cycle=12 horizM=0.50 tolM=3.25 dAltM=1.00", line);
                Assert.Contains("altWindow=[60.0,80.0]", line);
                Assert.EndsWith("verdict=ok", line);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prior;
            }
        }
    }
}
