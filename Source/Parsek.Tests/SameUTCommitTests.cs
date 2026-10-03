using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// todo SECTION-DUPLICATE-UT-SAMPLES: one track section holds one sample per UT.
    /// </summary>
    [Collection("Sequential")]
    public class SameUTCommitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly FlightRecorder recorder;

        public SameUTCommitTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            recorder = new FlightRecorder();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static TrajectoryPoint Pt(double ut, double alt = 938.9, float speed = 175f, byte flags = 0)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = -0.0972,
                longitude = -74.5573,
                altitude = alt,
                rotation = new Quaternion(0f, 0f, 0f, 1f),
                velocity = new Vector3(speed, 0f, 0f),
                bodyName = "Kerbin",
                recordedGroundClearance = double.NaN,
                flags = flags,
            };
        }

        [Fact]
        public void Classify_NoReferenceOrNewUT_Appends()
        {
            Assert.Equal(FlightRecorder.SameUTCommitDisposition.Append,
                FlightRecorder.ClassifySameUTCommit(false, default(TrajectoryPoint), Pt(48.5)));
            Assert.Equal(FlightRecorder.SameUTCommitDisposition.Append,
                FlightRecorder.ClassifySameUTCommit(true, Pt(48.48), Pt(48.5)));
        }

        [Fact]
        public void Classify_IdenticalApartFromFlags_Merges()
        {
            Assert.Equal(FlightRecorder.SameUTCommitDisposition.MergeIntoIdentical,
                FlightRecorder.ClassifySameUTCommit(true, Pt(48.5), Pt(48.5, flags: 1)));
        }

        [Fact]
        public void Classify_DifferingSample_Replaces()
        {
            Assert.Equal(FlightRecorder.SameUTCommitDisposition.ReplaceLast,
                FlightRecorder.ClassifySameUTCommit(true, Pt(35.18, speed: 175f), Pt(35.18, speed: 0f)));
        }

        [Fact]
        public void Equivalent_TreatsNaNClearancesAsEqual_AndSeesEveryField()
        {
            Assert.True(FlightRecorder.AreTrajectoryPointsEquivalent(Pt(1.0), Pt(1.0)));
            TrajectoryPoint b = Pt(1.0);
            b.recordedGroundClearance = 2.0;
            Assert.False(FlightRecorder.AreTrajectoryPointsEquivalent(Pt(1.0), b));
            b = Pt(1.0);
            b.rotation = new Quaternion(0f, 0.1f, 0f, 0.995f);
            Assert.False(FlightRecorder.AreTrajectoryPointsEquivalent(Pt(1.0), b));
            b = Pt(1.0);
            b.bodyName = "Mun";
            Assert.False(FlightRecorder.AreTrajectoryPointsEquivalent(Pt(1.0), b));
            b = Pt(1.0);
            b.funds = 10;
            Assert.False(FlightRecorder.AreTrajectoryPointsEquivalent(Pt(1.0), b));
        }

        [Fact]
        public void DescribeDifference_NamesTheChangedFields()
        {
            string d = FlightRecorder.DescribeTrajectoryPointDifference(
                Pt(35.18, speed: 175f), Pt(35.18, speed: 0f));
            Assert.Equal("speed(175.00->0.00)", d);
            Assert.Equal("(none)", FlightRecorder.DescribeTrajectoryPointDifference(Pt(1.0), Pt(1.0)));
        }

        [Fact]
        public void IdenticalPairInOneSection_KeepsOnePoint()
        {
            // PWR-1 2026-10-02_2122: section 2 ended with two byte-identical points at the
            // booster decouple, UT 48.50.
            recorder.StartNewTrackSection(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, 40.0);
            recorder.CommitRecordedPointForTesting(Pt(48.0));
            recorder.CommitRecordedPointForTesting(Pt(48.5));
            recorder.CommitRecordedPointForTesting(Pt(48.5));

            Assert.Equal(new[] { 48.0, 48.5 }, Uts(recorder.CurrentTrackSectionForTesting.frames));
            Assert.Equal(new[] { 48.0, 48.5 }, Uts(recorder.Recording));
            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("Same-UT sample identical to the section's last point, not appended: ut=48.5")
                && l.Contains("section=open"));
        }

        [Fact]
        public void IdenticalPair_StructuralFlagOnTheSecond_IsKept()
        {
            recorder.StartNewTrackSection(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, 40.0);
            recorder.CommitRecordedPointForTesting(Pt(48.5));
            recorder.CommitRecordedPointForTesting(Pt(48.5, flags: 1));

            Assert.Single(recorder.CurrentTrackSectionForTesting.frames);
            Assert.Equal(1, recorder.CurrentTrackSectionForTesting.frames[0].flags);
            Assert.Equal(1, recorder.Recording[recorder.Recording.Count - 1].flags);
            Assert.Contains(logLines, l => l.Contains("flags=0->1"));
        }

        [Fact]
        public void DifferingPair_LaterSampleReplacesEarlier_KeepingFlags()
        {
            // PWR-3 2026-10-02_2108 clamp release: a packed sample (obt_velocity 175 m/s)
            // then the off-rails boundary sample (unpacked, 0 m/s) at UT 35.18.
            recorder.StartNewTrackSection(SegmentEnvironment.SurfaceStationary, ReferenceFrame.Absolute, 35.0);
            recorder.CommitRecordedPointForTesting(Pt(35.0, speed: 175f));
            recorder.CommitRecordedPointForTesting(Pt(35.18, speed: 175f, flags: 1));
            recorder.CommitRecordedPointForTesting(Pt(35.18, speed: 0f));

            List<TrajectoryPoint> frames = recorder.CurrentTrackSectionForTesting.frames;
            Assert.Equal(new[] { 35.0, 35.18 }, Uts(frames));
            Assert.Equal(0f, frames[1].velocity.magnitude);
            Assert.Equal(1, frames[1].flags);
            Assert.Equal(new[] { 35.0, 35.18 }, Uts(recorder.Recording));
            Assert.Equal(0f, recorder.Recording[1].velocity.magnitude);
            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("Same-UT sample replaced the section's last point: ut=35.18")
                && l.Contains("differs=speed(175.00->0.00)")
                && l.Contains("flags=1"));
        }

        [Fact]
        public void NewSectionFirstFrame_AtTheSeamUT_StillAppends()
        {
            // The flat list's equal-UT seam point belongs to the closed section; the new
            // section needs its own first frame.
            recorder.StartNewTrackSection(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, 40.0);
            recorder.CommitRecordedPointForTesting(Pt(48.5));
            recorder.CloseCurrentTrackSection(48.5);
            recorder.StartNewTrackSection(SegmentEnvironment.Atmospheric, ReferenceFrame.Absolute, 48.5);
            recorder.CommitRecordedPointForTesting(Pt(48.5));

            Assert.Single(recorder.CurrentTrackSectionForTesting.frames);
            Assert.Equal(new[] { 48.5, 48.5 }, Uts(recorder.Recording));
        }

        [Fact]
        public void NoOpenSection_DedupesAgainstTheFlatList()
        {
            recorder.CommitRecordedPointForTesting(Pt(10.0));
            recorder.CommitRecordedPointForTesting(Pt(10.0));
            Assert.Single(recorder.Recording);
            Assert.Contains(logLines, l => l.Contains("section=none"));
        }

        private static double[] Uts(List<TrajectoryPoint> points)
        {
            var uts = new double[points.Count];
            for (int i = 0; i < points.Count; i++)
                uts[i] = points[i].ut;
            return uts;
        }
    }
}
