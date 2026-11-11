using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Audit finding R4 (replay-theory audit 2026-11-04): the flat Points list is the
    /// concatenation of the section frames, so inside a Relative section its entries are
    /// anchor-local metre offsets. A flat-points playback fallback (a section gap, a span
    /// no section covers) read them as lat/lon/alt and put the ghost inside the planet.
    /// </summary>
    public class FlatPointFrameDispatchTests
    {
        private static TrajectoryPoint Body(double ut, double lat, double lon, double alt, string body = "Kerbin")
        {
            return new TrajectoryPoint
            {
                ut = ut, latitude = lat, longitude = lon, altitude = alt,
                rotation = Quaternion.identity, bodyName = body,
                recordedGroundClearance = double.NaN,
            };
        }

        // Metre offsets along the anchor's local axes: values far outside lat/lon ranges.
        private static TrajectoryPoint Offset(double ut, double x, double y, double z)
        {
            return Body(ut, x, y, z);
        }

        private static TrackSection Section(
            ReferenceFrame frame, double start, double end,
            List<TrajectoryPoint> frames, List<TrajectoryPoint> shadows = null)
        {
            return new TrackSection
            {
                referenceFrame = frame,
                environment = SegmentEnvironment.ExoBallistic,
                startUT = start,
                endUT = end,
                frames = frames,
                bodyFixedFrames = shadows,
                anchorRecordingId = frame == ReferenceFrame.Relative ? "anchor" : null,
                checkpoints = new List<OrbitSegment>(),
            };
        }

        // Absolute [0,10], Relative [10,20] (offsets + shadows), gap, Absolute [30,40].
        private static List<TrackSection> GapAfterRelative(bool withShadows)
        {
            var relFrames = new List<TrajectoryPoint>
            {
                Offset(10, -812.5, 391.0, 1204.0),
                Offset(15, -640.0, 300.0, 1100.0),
                Offset(20, -501.25, 222.0, 998.0),
            };
            var shadows = withShadows
                ? new List<TrajectoryPoint>
                {
                    Body(10, -0.10, -74.50, 70000),
                    Body(15, -0.11, -74.40, 71000),
                    Body(20, -0.12, -74.30, 72000),
                }
                : null;
            return new List<TrackSection>
            {
                Section(ReferenceFrame.Absolute, 0, 10, new List<TrajectoryPoint>
                {
                    Body(0, -0.09, -74.60, 68000), Body(10, -0.10, -74.50, 70000),
                }),
                Section(ReferenceFrame.Relative, 10, 20, relFrames, shadows),
                Section(ReferenceFrame.Absolute, 30, 40, new List<TrajectoryPoint>
                {
                    Body(30, -0.14, -74.10, 75000), Body(40, -0.15, -74.00, 76000),
                }),
            };
        }

        private static List<TrajectoryPoint> Flat(List<TrackSection> sections)
        {
            var flat = new List<TrajectoryPoint>();
            TrajectoryTextSidecarCodec.RebuildPointsFromTrackSections(sections, flat);
            return flat;
        }

        [Fact]
        public void NoSections_IsBodyFixed()
        {
            TrajectoryPoint p = Offset(15, -640.0, 300.0, 1100.0);
            Assert.Equal(FlatPointFrame.BodyFixed, FlatPointFrameDispatch.Classify(null, p, out var fixedPt));
            Assert.Equal(p.latitude, fixedPt.latitude);
        }

        [Fact]
        public void RelativeFrame_WithShadow_ResolvesToTheShadow()
        {
            List<TrackSection> sections = GapAfterRelative(withShadows: true);
            Assert.Equal(FlatPointFrame.RelativeWithShadow,
                FlatPointFrameDispatch.Classify(sections, Offset(15, -640.0, 300.0, 1100.0), out var fixedPt));
            Assert.Equal(-0.11, fixedPt.latitude);
            Assert.Equal(71000, fixedPt.altitude);
        }

        [Fact]
        public void RelativeFrame_WithoutShadow_IsRefused()
        {
            List<TrackSection> sections = GapAfterRelative(withShadows: false);
            Assert.Equal(FlatPointFrame.RelativeNoShadow,
                FlatPointFrameDispatch.Classify(sections, Offset(15, -640.0, 300.0, 1100.0), out _));
        }

        [Fact]
        public void SharedBoundaryUT_AbsoluteNeighbourFrame_StaysBodyFixed()
        {
            // UT 10 is inside both the Absolute [0,10] and the Relative [10,20] span; the
            // Absolute section's own frame is not a Relative offset.
            List<TrackSection> sections = GapAfterRelative(withShadows: true);
            Assert.Equal(FlatPointFrame.BodyFixed,
                FlatPointFrameDispatch.Classify(sections, Body(10, -0.10, -74.50, 70000), out _));
            Assert.Equal(FlatPointFrame.RelativeWithShadow,
                FlatPointFrameDispatch.Classify(sections, Offset(10, -812.5, 391.0, 1204.0), out _));
        }

        [Fact]
        public void GapRead_AfterARelativeSection_DispatchesTheRelativeEndpoint()
        {
            List<TrackSection> sections = GapAfterRelative(withShadows: true);
            List<TrajectoryPoint> flat = Flat(sections);
            int cached = 0;
            Assert.True(TrajectoryMath.InterpolatePoints(flat, ref cached, 25.0, out var before, out var after, out _));
            // The pre-fix fallback fed these straight to GetWorldSurfacePosition.
            Assert.Equal(-501.25, before.latitude);
            Assert.Equal(30.0, after.ut);

            Assert.True(FlatPointFrameDispatch.TryDispatchEndpoints(sections, ref before, ref after, out int substituted));
            Assert.Equal(1, substituted);
            Assert.Equal(-0.12, before.latitude);
            Assert.Equal(72000, before.altitude);
            Assert.Equal(-0.14, after.latitude);
        }

        [Fact]
        public void GapRead_WithoutShadow_IsRefused()
        {
            List<TrackSection> sections = GapAfterRelative(withShadows: false);
            List<TrajectoryPoint> flat = Flat(sections);
            int cached = 0;
            TrajectoryMath.InterpolatePoints(flat, ref cached, 25.0, out var before, out var after, out _);
            Assert.False(FlatPointFrameDispatch.TryDispatchEndpoints(sections, ref before, ref after, out _));
        }

        [Fact]
        public void AbsoluteOnlyRead_IsUntouched()
        {
            List<TrackSection> sections = GapAfterRelative(withShadows: false);
            List<TrajectoryPoint> flat = Flat(sections);
            int cached = 0;
            TrajectoryMath.InterpolatePoints(flat, ref cached, 35.0, out var before, out var after, out _);
            Assert.True(FlatPointFrameDispatch.TryDispatchEndpoints(sections, ref before, ref after, out int substituted));
            Assert.Equal(0, substituted);
            Assert.Equal(-0.14, before.latitude);
        }

        [Fact]
        public void OpenRelativeSection_StillClassifies()
        {
            var sections = new List<TrackSection>
            {
                Section(ReferenceFrame.Relative, 10, 0 /* open: end < start */,
                    new List<TrajectoryPoint> { Offset(12, 50, 60, 70) },
                    new List<TrajectoryPoint> { Body(12, 1, 2, 3) }),
            };
            Assert.Equal(FlatPointFrame.RelativeWithShadow,
                FlatPointFrameDispatch.Classify(sections, Offset(12, 50, 60, 70), out _));
        }

        // Every flight-scene caller that hands the FLAT Points list to the point
        // interpolators must pass the sections, or the dispatch never runs.
        [Fact]
        public void FlatPointsCallSites_PassTheirSections()
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string src = ParsekFlightSourceFiles.ReadAll(Path.Combine(root, "Source", "Parsek"));
            var stripped = new StringBuilder();
            foreach (string line in src.Split('\n'))
            {
                int idx = line.IndexOf("//", StringComparison.Ordinal);
                stripped.Append(idx >= 0 ? line.Substring(0, idx) : line).Append('\n');
            }
            string code = stripped.ToString();

            var call = new Regex(
                @"(?:InterpolateAndPosition\(\s*[\w\.]+,|TryResolveInterpolatedWorldPosition\()\s*(?<pts>[\w\.\?]+\.Points|(?:host\.)?recording),",
                RegexOptions.Multiline);
            int seen = 0;
            foreach (Match m in call.Matches(code))
            {
                int end = code.IndexOf(';', m.Index);
                string text = code.Substring(m.Index, end - m.Index);
                Assert.True(text.Contains("TrackSections"),
                    "flat-points call site does not pass its TrackSections: " + text);
                seen++;
            }
            Assert.True(seen >= 5, "expected at least 5 flat-points call sites, saw " + seen);
        }
    }
}
