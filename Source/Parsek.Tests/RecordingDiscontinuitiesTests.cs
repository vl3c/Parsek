using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling Q18 (2026-11-18): optimizer splits and Re-Fly cuts never land inside a
    /// cheat teleport or a time jump; the cut moves to the nearest point outside it.
    /// </summary>
    [Collection("Sequential")]
    public class RecordingDiscontinuitiesTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RecordingDiscontinuitiesTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        private static SegmentEvent TimeJump(double pre, double post)
        {
            return TimeJumpManager.CreateTimeJumpEvent(pre, post, 0, 0, 0, 0f, 0f, 0f);
        }

        private static TrackSection Section(double start, double end, bool seam = false,
            params double[] frameUTs)
        {
            var frames = new List<TrajectoryPoint>();
            foreach (double ut in frameUTs) frames.Add(new TrajectoryPoint { ut = ut });
            return new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = start,
                endUT = end,
                isBoundarySeam = seam,
                frames = frames,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
            };
        }

        [Fact]
        public void Collect_TimeJumpEventAndTeleportSeam()
        {
            var rec = new Recording { RecordingId = "r" };
            rec.SegmentEvents.Add(TimeJump(100.0, 500.0));
            rec.TrackSections.Add(Section(0.0, 620.0, false, 0.0, 600.0));
            rec.TrackSections.Add(Section(620.0, 620.0, true, 620.0));
            rec.TrackSections.Add(Section(620.0, 700.0, false, 620.0, 700.0));

            var windows = RecordingDiscontinuities.Collect(new[] { (rec.SegmentEvents, rec.TrackSections) });

            Assert.Equal(2, windows.Count);
            Assert.Contains(windows, w => w.Kind == RecordingDiscontinuities.TimeJumpKind
                && w.StartUT == 100.0 && w.EndUT == 500.0);
            Assert.Contains(windows, w => w.Kind == RecordingDiscontinuities.SeamKind
                && w.StartUT == 600.0 && w.EndUT == 620.0);
        }

        [Fact]
        public void Collect_SeamStartingALaterChainPiece_FindsTheSampleInTheEarlierPiece()
        {
            var head = new Recording { RecordingId = "h" };
            head.TrackSections.Add(Section(0.0, 50.0, false, 0.0, 40.0));
            var tip = new Recording { RecordingId = "t" };
            tip.TrackSections.Add(Section(50.0, 50.0, true, 50.0));

            var windows = RecordingDiscontinuities.Collect(new[] { (head.SegmentEvents, head.TrackSections), (tip.SegmentEvents, tip.TrackSections) });

            Assert.Single(windows);
            Assert.Equal(40.0, windows[0].StartUT);
            Assert.Equal(50.0, windows[0].EndUT);
        }

        [Theory]
        [InlineData(150.0, false, 100.0)] // nearer the start
        [InlineData(450.0, false, 500.0)] // nearer the end
        [InlineData(300.0, false, 100.0)] // tie goes to the start
        [InlineData(450.0, true, 100.0)]  // Re-Fly: always the start
        [InlineData(100.0, false, 100.0)] // an edge is outside
        [InlineData(50.0, false, 50.0)]   // outside
        public void MoveCutOutside_PicksTheEdge(double cut, bool earlierOnly, double expected)
        {
            var windows = new List<DiscontinuityWindow>
            {
                new DiscontinuityWindow { StartUT = 100.0, EndUT = 500.0, Kind = "time-jump" },
            };
            string movedOutOf;
            Assert.Equal(expected,
                RecordingDiscontinuities.MoveCutOutside(windows, cut, earlierOnly, out movedOutOf));
            Assert.Equal(expected != cut, movedOutOf != null);
        }

        [Fact]
        public void TryParseTimeJumpDetails_ReadsTheProducerFormat()
        {
            var e = TimeJump(12.5, 99.25);
            double pre, post;
            Assert.True(RecordingDiscontinuities.TryParseTimeJumpDetails(e.details, out pre, out post));
            Assert.Equal(12.5, pre);
            Assert.Equal(99.25, post);
            Assert.False(RecordingDiscontinuities.TryParseTimeJumpDetails("lat=1", out pre, out post));
            Assert.False(RecordingDiscontinuities.TryParseTimeJumpDetails(null, out pre, out post));
        }

        private static Recording ThreeSectionRecording(double jumpPre, double jumpPost)
        {
            var rec = new Recording { RecordingId = "rec_q18" };
            for (int ut = 0; ut <= 100; ut += 10)
                rec.Points.Add(new TrajectoryPoint { ut = ut });
            rec.TrackSections.Add(Section(0.0, 30.0, false, 0.0, 10.0, 20.0, 30.0));
            rec.TrackSections.Add(Section(30.0, 50.0, false, 30.0, 40.0, 50.0));
            rec.TrackSections.Add(Section(50.0, 100.0, false, 50.0, 60.0, 100.0));
            rec.SegmentEvents.Add(TimeJump(jumpPre, jumpPost));
            return rec;
        }

        [Fact]
        public void OptimizerSplit_InsideATimeJump_MovesToTheBoundaryAtTheEdge()
        {
            var rec = ThreeSectionRecording(30.0, 60.0);
            var windows = RecordingDiscontinuities.Collect(new[] { (rec.SegmentEvents, rec.TrackSections) });

            int placed;
            Assert.True(RecordingOptimizer.TryPlaceSplitOutsideDiscontinuity(rec, 2, windows, out placed));

            Assert.Equal(1, placed);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("Split moved out of time-jump") && l.Contains("sec=2->1"));
        }

        [Fact]
        public void OptimizerSplit_InsideATimeJumpWithNoBoundaryAtItsEdges_IsRefused()
        {
            var rec = ThreeSectionRecording(40.0, 60.0);
            var windows = RecordingDiscontinuities.Collect(new[] { (rec.SegmentEvents, rec.TrackSections) });

            int placed;
            Assert.False(RecordingOptimizer.TryPlaceSplitOutsideDiscontinuity(rec, 2, windows, out placed));
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("Split refused") && l.Contains("lies inside time-jump"));
        }

        [Fact]
        public void OptimizerSplit_OutsideEveryWindow_StaysPut()
        {
            var rec = ThreeSectionRecording(60.0, 80.0);
            var windows = RecordingDiscontinuities.Collect(new[] { (rec.SegmentEvents, rec.TrackSections) });

            int placed;
            Assert.True(RecordingOptimizer.TryPlaceSplitOutsideDiscontinuity(rec, 2, windows, out placed));
            Assert.Equal(2, placed);
        }
    }
}
