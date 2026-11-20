using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek.InGameTests;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="FlightGhostPosition"/>, the pure core of the
    /// <c>FlightGhostPosition</c> in-game category (test-gap Batch 3).
    /// </summary>
    public class FlightGhostPositionTests
    {
        private static TrajectoryPoint Pt(double ut, double lat, double lon, double alt,
            string body = "Kerbin")
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                bodyName = body,
                recordedGroundClearance = double.NaN,
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

        private static List<TrajectoryPoint> Abs(double a, double b) =>
            new List<TrajectoryPoint> { Pt(a, 0, 0, 1000), Pt(b, 0, 0.01, 1100) };

        // Anchor-local offsets: metres, deliberately outside latitude / longitude range.
        private static List<TrajectoryPoint> Offsets(double a, double b) =>
            new List<TrajectoryPoint> { Pt(a, 250, -400, 30), Pt(b, 240, -390, 31) };

        // ---------------------------------------------------------------
        //  Classification
        // ---------------------------------------------------------------

        [Fact]
        public void AbsoluteSection_IsAbsoluteKind_AndReadsItsFrames()
        {
            var frames = Abs(10, 20);
            var sections = new List<TrackSection> { Section(ReferenceFrame.Absolute, 10, 20, frames) };
            Assert.True(FlightGhostPosition.TryClassify(sections, null, false, false, 15,
                out FlightGhostPosition.SurfaceKind kind, out IList<TrajectoryPoint> got, out string source));
            Assert.Equal(FlightGhostPosition.SurfaceKind.Absolute, kind);
            Assert.Same(frames, got);
            Assert.Equal("absolute-frames", source);
        }

        [Fact]
        public void NoSections_FlatPointsAreAbsolute()
        {
            var flat = Abs(10, 20);
            Assert.True(FlightGhostPosition.TryClassify(new List<TrackSection>(), flat, false, false, 15,
                out FlightGhostPosition.SurfaceKind kind, out IList<TrajectoryPoint> got, out string source));
            Assert.Equal(FlightGhostPosition.SurfaceKind.Absolute, kind);
            Assert.Same(flat, got);
            Assert.Equal("flat-points", source);
        }

        [Fact]
        public void ParentAnchoredRelative_ReadsTheBodyFixedShadow_NeverTheOffsets()
        {
            var shadow = Abs(10, 20);
            var sections = new List<TrackSection>
            {
                Section(ReferenceFrame.Relative, 10, 20, Offsets(10, 20), shadow),
            };
            Assert.True(FlightGhostPosition.TryClassify(sections, null, true, false, 15,
                out FlightGhostPosition.SurfaceKind kind, out IList<TrajectoryPoint> got, out string source));
            Assert.Equal(FlightGhostPosition.SurfaceKind.ParentAnchored, kind);
            Assert.Same(shadow, got);
            Assert.Equal("relative-body-fixed", source);
        }

        [Fact]
        public void ParentAnchoredRelative_InLoopPlayback_StillReadsTheShadow()
        {
            // Loop-anchored debris falls back to the shadow when the live anchor cannot resolve,
            // and the ordinary parent-anchored path renders it first; the cell compares it.
            var shadow = Abs(10, 20);
            var sections = new List<TrackSection>
            {
                Section(ReferenceFrame.Relative, 10, 20, Offsets(10, 20), shadow),
            };
            Assert.True(FlightGhostPosition.TryClassify(sections, null, true, true, 15,
                out FlightGhostPosition.SurfaceKind kind, out _, out _));
            Assert.Equal(FlightGhostPosition.SurfaceKind.ParentAnchored, kind);
        }

        [Fact]
        public void AnchorLocalRelative_NonLoop_ComparesAgainstTheShadow()
        {
            var shadow = Abs(10, 20);
            var sections = new List<TrackSection>
            {
                Section(ReferenceFrame.Relative, 10, 20, Offsets(10, 20), shadow),
            };
            Assert.True(FlightGhostPosition.TryClassify(sections, null, false, false, 15,
                out FlightGhostPosition.SurfaceKind kind, out IList<TrajectoryPoint> got, out string source));
            Assert.Equal(FlightGhostPosition.SurfaceKind.RelativeAnchor, kind);
            Assert.Same(shadow, got);
            Assert.Equal("relative-anchor-shadow", source);
        }

        [Fact]
        public void AnchorLocalRelative_InLoopPlayback_IsUnsupported_WithItsKindKept()
        {
            var sections = new List<TrackSection>
            {
                Section(ReferenceFrame.Relative, 10, 20, Offsets(10, 20), Abs(10, 20)),
            };
            Assert.False(FlightGhostPosition.TryClassify(sections, null, false, true, 15,
                out FlightGhostPosition.SurfaceKind kind, out IList<TrajectoryPoint> got, out string source));
            Assert.Equal(FlightGhostPosition.SurfaceKind.RelativeAnchor, kind);
            Assert.Null(got);
            Assert.Equal("relative-loop-live-anchor", source);
        }

        [Fact]
        public void RelativeWithoutCoveringShadow_IsUnsupported_WithItsKindKept()
        {
            var sections = new List<TrackSection>
            {
                Section(ReferenceFrame.Relative, 10, 30, Offsets(10, 30), Abs(10, 20)),
            };
            Assert.False(FlightGhostPosition.TryClassify(sections, null, true, false, 25,
                out FlightGhostPosition.SurfaceKind kind, out _, out string source));
            Assert.Equal(FlightGhostPosition.SurfaceKind.ParentAnchored, kind);
            Assert.Equal("relative-no-body-fixed", source);
        }

        [Fact]
        public void OrbitalCheckpoint_IsUnsupported_AndCountsAsAbsolute()
        {
            var sections = new List<TrackSection> { Section(ReferenceFrame.OrbitalCheckpoint, 10, 20) };
            Assert.False(FlightGhostPosition.TryClassify(sections, null, false, false, 15,
                out FlightGhostPosition.SurfaceKind kind, out _, out string source));
            Assert.Equal(FlightGhostPosition.SurfaceKind.Absolute, kind);
            Assert.Equal("orbital-checkpoint", source);
        }

        [Fact]
        public void UtOutsideEverySection_IsUnsupported()
        {
            var sections = new List<TrackSection> { Section(ReferenceFrame.Absolute, 10, 20, Abs(10, 20)) };
            Assert.False(FlightGhostPosition.TryClassify(sections, null, false, false, 25,
                out _, out _, out string source));
            Assert.Equal("no-section-at-ut", source);
        }

        [Fact]
        public void SectionSwitch_PicksTheKindAtTheUt()
        {
            var shadow = Abs(20, 30);
            var sections = new List<TrackSection>
            {
                Section(ReferenceFrame.Absolute, 10, 20, Abs(10, 20)),
                Section(ReferenceFrame.Relative, 20, 30, Offsets(20, 30), shadow),
            };
            Assert.True(FlightGhostPosition.TryClassify(sections, null, true, false, 19.9,
                out FlightGhostPosition.SurfaceKind before, out _, out _));
            Assert.True(FlightGhostPosition.TryClassify(sections, null, true, false, 20.0,
                out FlightGhostPosition.SurfaceKind after, out IList<TrajectoryPoint> got, out _));
            Assert.Equal(FlightGhostPosition.SurfaceKind.Absolute, before);
            Assert.Equal(FlightGhostPosition.SurfaceKind.ParentAnchored, after);
            Assert.Same(shadow, got);
        }

        // ---------------------------------------------------------------
        //  Tolerance discrimination
        // ---------------------------------------------------------------

        [Fact]
        public void Signal_HasAFloor_AndGrowsWithSpeed()
        {
            Assert.Equal(FlightGhostPosition.MinDiscriminatedErrorMeters,
                FlightGhostPosition.DiscriminationSignalMeters(0.0));
            Assert.Equal(FlightGhostPosition.MinDiscriminatedErrorMeters,
                FlightGhostPosition.DiscriminationSignalMeters(double.NaN));
            Assert.Equal(2200.0, FlightGhostPosition.DiscriminationSignalMeters(2200.0), 6);
        }

        [Fact]
        public void ToleranceResolves_TheOrdinaryFlightTolerance()
        {
            // 3 m floor + 0.05 m grid + one 20 ms frame of 2200 m/s orbital motion = 47 m,
            // against a 2200 m signal.
            double tol = RouteGhostPlacement.HorizontalToleranceMeters(0.05, 2200.0, 0.02);
            Assert.True(FlightGhostPosition.ToleranceResolves(tol, 2200.0));
            // A slow ghost: 3 m + grid against the 50 m floor.
            Assert.True(FlightGhostPosition.ToleranceResolves(
                RouteGhostPlacement.HorizontalToleranceMeters(0.02, 1.0, 0.02), 1.0));
        }

        [Fact]
        public void ToleranceThatSwallowsTheSignal_DoesNotResolve()
        {
            Assert.False(FlightGhostPosition.ToleranceResolves(20.0, 0.0));
            Assert.False(FlightGhostPosition.ToleranceResolves(double.NaN, 100.0));
        }

        [Fact]
        public void FrameMotion_IsSpeedTimesClampedFrame()
        {
            Assert.Equal(6.0, FlightGhostPosition.FrameMotionMeters(300.0, 0.02), 9);
            Assert.Equal(30.0, FlightGhostPosition.FrameMotionMeters(300.0, 5.0), 9);
            Assert.Equal(0.0, FlightGhostPosition.FrameMotionMeters(-3.0, 0.02));
            Assert.Equal(0.0, FlightGhostPosition.FrameMotionMeters(300.0, double.NaN));
        }

        [Fact]
        public void InsideRecording_IsInclusive_AndRejectsNaN()
        {
            Assert.True(FlightGhostPosition.InsideRecording(10, 10, 20));
            Assert.True(FlightGhostPosition.InsideRecording(20, 10, 20));
            Assert.False(FlightGhostPosition.InsideRecording(20.001, 10, 20));
            Assert.False(FlightGhostPosition.InsideRecording(double.NaN, 10, 20));
        }

        [Fact]
        public void Outcome_FailBeatsSkip_SkipWhenNothingCompared()
        {
            Assert.Equal(FlightGhostPosition.CellOutcome.Fail, FlightGhostPosition.DecideOutcome(0, 1));
            Assert.Equal(FlightGhostPosition.CellOutcome.Fail, FlightGhostPosition.DecideOutcome(30, 1));
            Assert.Equal(FlightGhostPosition.CellOutcome.Skip, FlightGhostPosition.DecideOutcome(0, 0));
            Assert.Equal(FlightGhostPosition.CellOutcome.Pass, FlightGhostPosition.DecideOutcome(1, 0));
        }

        // ---------------------------------------------------------------
        //  Formatting (the lanes pin these lines)
        // ---------------------------------------------------------------

        [Fact]
        public void Summary_IsInvariantCulture_AndCarriesEveryCounter()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                string line = FlightGhostPosition.FormatSummary(
                    FlightGhostPosition.SurfaceKind.ParentAnchored, "FLIGHT", 120, 5, 30, 30, 0,
                    12, 1, 2, 3, 4, 5, 1.25, "Kerbin");
                Assert.Equal(
                    "FlightGhostPosition summary: kind=parent-anchored scene=FLIGHT frames=120 ghosts=5 "
                    + "compared=30 ok=30 bad=0 otherKind=12 outsideWindow=1 hidden=2 unsupported=3 "
                    + "unresolved=4 notLive=5 worstHorizM=1.25 bodies=[Kerbin]", line);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void SampleLine_IsInvariantCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var v = new RouteGhostPlacement.Verdict
                {
                    Ok = true,
                    Reason = "ok",
                    HorizontalMeters = 0.5,
                    HorizontalToleranceMeters = 9.25,
                    VerticalDeltaMeters = -0.75,
                    VerticalMinMeters = 990.0,
                    VerticalMaxMeters = 1010.0,
                };
                string line = FlightGhostPosition.FormatSampleLine(FlightGhostPosition.SurfaceKind.Absolute,
                    3, "abc", "Kerbal X", "Kerbin", "absolute-frames", "live", 75.5, 75.5, v);
                Assert.Equal(
                    "FlightGhostPosition: kind=absolute ghost=3 rec=abc vessel='Kerbal X' body=Kerbin "
                    + "source=absolute-frames clock=live ut=75.50 playbackUT=75.50 horizM=0.50 tolM=9.25 "
                    + "dAltM=-0.75 altWindow=[990.0,1010.0] verdict=ok", line);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void KindLabels_AreStable()
        {
            Assert.Equal("absolute", FlightGhostPosition.KindLabel(FlightGhostPosition.SurfaceKind.Absolute));
            Assert.Equal("parent-anchored",
                FlightGhostPosition.KindLabel(FlightGhostPosition.SurfaceKind.ParentAnchored));
            Assert.Equal("relative-anchor",
                FlightGhostPosition.KindLabel(FlightGhostPosition.SurfaceKind.RelativeAnchor));
        }
    }
}
