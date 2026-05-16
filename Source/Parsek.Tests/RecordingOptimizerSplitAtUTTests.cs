using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for <see cref="RecordingOptimizer.SplitAtUT"/> — the arbitrary-UT
    /// split helper used by the Re-Fly supersede-identity orchestrator. Covers
    /// pre-condition guards, the orbit-segment-straddle guard, the v13 debris
    /// bodyFixedFrames sample-count guard, the boundary-seam override warning,
    /// and the alignment-vs-synthetic-insert branch in step 4.
    /// </summary>
    [Collection("Sequential")]
    public class RecordingOptimizerSplitAtUTTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;

        public RecordingOptimizerSplitAtUTTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
        }

        #region Fixture helpers

        private static TrajectoryPoint PointAt(double ut, double altitude = 50000.0,
            string body = "Kerbin")
        {
            return new TrajectoryPoint
            {
                ut = ut,
                altitude = altitude,
                latitude = 0.0,
                longitude = 0.0,
                bodyName = body,
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            };
        }

        /// <summary>
        /// Builds a minimal Recording spanning [startUT, endUT] with a single
        /// TrackSection (Atmospheric, Absolute frame). Used for the precondition
        /// and "splitUT in middle of single section" cases. When midUT is supplied
        /// a third point is inserted at midUT so SplitAtSection's interpolation
        /// branch (which calls UnityEngine.Quaternion.Slerp — not callable outside
        /// the Unity runtime) is bypassed.
        /// </summary>
        private static Recording MakeSimpleRecording(double startUT, double endUT,
            string recordingId = "rec-test", double midUT = double.NaN)
        {
            var rec = new Recording
            {
                RecordingId = recordingId,
            };
            rec.Points.Add(PointAt(startUT));
            if (!double.IsNaN(midUT))
                rec.Points.Add(PointAt(midUT));
            rec.Points.Add(PointAt(endUT));
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = startUT,
                endUT = endUT,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                frames = !double.IsNaN(midUT)
                    ? new List<TrajectoryPoint>
                    {
                        PointAt(startUT),
                        PointAt(midUT),
                        PointAt(endUT),
                    }
                    : new List<TrajectoryPoint>
                    {
                        PointAt(startUT),
                        PointAt(endUT),
                    },
            });
            return rec;
        }

        /// <summary>
        /// Builds a Recording with two TrackSections joined at midUT. Used for
        /// "splitUT aligns to existing boundary" cases.
        /// </summary>
        private static Recording MakeRecordingWithBoundary(double startUT, double midUT,
            double endUT, string recordingId = "rec-twosection")
        {
            var rec = new Recording
            {
                RecordingId = recordingId,
            };
            rec.Points.Add(PointAt(startUT));
            rec.Points.Add(PointAt(midUT));
            rec.Points.Add(PointAt(endUT));
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = startUT,
                endUT = midUT,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                frames = new List<TrajectoryPoint>
                {
                    PointAt(startUT),
                    PointAt(midUT),
                },
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = midUT,
                endUT = endUT,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                frames = new List<TrajectoryPoint>
                {
                    PointAt(midUT),
                    PointAt(endUT),
                },
            });
            return rec;
        }

        #endregion

        #region Precondition guards

        [Fact]
        public void SplitAtUT_OriginSpansSplitUT_SplitsHeadAndTail()
        {
            // Recording [8, 53], splitUT 34. Single section in the middle ->
            // synthetic boundary insert path. HEAD becomes [8,34], TIP becomes [34,53].
            // midUT=34 supplies a point at exactly splitUT so the boundary
            // interpolation branch (Unity-runtime-only Slerp) is bypassed.
            var rec = MakeSimpleRecording(8.0, 53.0, "rec-canonical", midUT: 34.0);

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            // HEAD retains original id; TIP is the returned half (no id assigned yet).
            Assert.Equal("rec-canonical", rec.RecordingId);
            Assert.Equal(8.0, rec.StartUT);
            Assert.Equal(34.0, rec.EndUT);
            Assert.Equal(34.0, tip.StartUT);
            Assert.Equal(53.0, tip.EndUT);

            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("SplitAtUT: split rec-canonical")
                && l.Contains("syntheticBoundaryInserted=true"));
        }

        [Fact]
        public void SplitAtUT_OriginEntirelyPreSplitUT_ReturnsNull()
        {
            var rec = MakeSimpleRecording(8.0, 30.0, "rec-pre");

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.Null(tip);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("precondition violation")
                && l.Contains("rec-pre")
                && l.Contains("do not strictly span"));
        }

        [Fact]
        public void SplitAtUT_OriginEntirelyPostSplitUT_ReturnsNull()
        {
            var rec = MakeSimpleRecording(40.0, 53.0, "rec-post");

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.Null(tip);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("precondition violation")
                && l.Contains("rec-post")
                && l.Contains("do not strictly span"));
        }

        [Fact]
        public void SplitAtUT_NaNSplitUT_ReturnsNull()
        {
            var rec = MakeSimpleRecording(8.0, 53.0, "rec-nan");

            var tip = RecordingOptimizer.SplitAtUT(rec, double.NaN);

            Assert.Null(tip);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("precondition violation")
                && l.Contains("splitUT is NaN")
                && l.Contains("rec-nan"));
        }

        #endregion

        #region Orbit-segment straddle guard

        [Fact]
        public void SplitAtUT_OrbitSegmentStraddlesSplitUT_ReturnsNull()
        {
            // Build a recording where an OrbitSegment straddles splitUT.
            var rec = new Recording
            {
                RecordingId = "rec-orbit-straddle",
            };
            rec.Points.Add(PointAt(8.0));
            rec.Points.Add(PointAt(53.0));
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 8.0,
                endUT = 53.0,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                frames = new List<TrajectoryPoint>
                {
                    PointAt(8.0),
                    PointAt(53.0),
                },
            });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 20.0,
                endUT = 50.0,
                bodyName = "Kerbin",
                inclination = 0.0,
                eccentricity = 0.0,
                semiMajorAxis = 700000.0,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = 20.0,
                isPredicted = false,
                orbitalFrameRotation = Quaternion.identity,
                angularVelocity = Vector3.zero,
            });

            // Snapshot pre-call state for byte-identical-on-failure assertion.
            int sectionsBefore = rec.TrackSections.Count;
            int pointsBefore = rec.Points.Count;
            int orbitSegmentsBefore = rec.OrbitSegments.Count;

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.Null(tip);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("orbit-segment straddle")
                && l.Contains("rec-orbit-straddle")
                && l.Contains("OrbitSegment[0]"));

            // Mutation-ordering invariant: failed guard must leave original alone.
            Assert.Equal(sectionsBefore, rec.TrackSections.Count);
            Assert.Equal(pointsBefore, rec.Points.Count);
            Assert.Equal(orbitSegmentsBefore, rec.OrbitSegments.Count);
        }

        #endregion

        #region Boundary alignment branches

        [Fact]
        public void SplitAtUT_SplitUTAlignsToTrackSectionBoundary_NoSyntheticInsert()
        {
            // Two-section recording joined at midUT=34. splitUT exactly at midUT
            // (within epsilon) should reuse the existing boundary — no new
            // section inserted.
            var rec = MakeRecordingWithBoundary(8.0, 34.0, 53.0, "rec-aligned");
            int sectionsBefore = rec.TrackSections.Count;

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            // Sum of section counts equals pre-call count: no synthetic insert.
            Assert.Equal(sectionsBefore, rec.TrackSections.Count + tip.TrackSections.Count);
            Assert.Single(rec.TrackSections);
            Assert.Single(tip.TrackSections);

            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("SplitAtUT: split rec-aligned")
                && l.Contains("syntheticBoundaryInserted=false"));
        }

        [Fact]
        public void SplitAtUT_SplitUTMidSection_InsertsSyntheticBoundary()
        {
            // Single section [8, 53], split at 34 (mid). Should clone into
            // [8, 34] head + [34, 53] tail, covering the full range.
            // midUT=34 avoids the boundary-interpolation Slerp path.
            var rec = MakeSimpleRecording(8.0, 53.0, "rec-mid", midUT: 34.0);

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            Assert.Single(rec.TrackSections);
            Assert.Single(tip.TrackSections);

            // Combined coverage equals the original [8, 53] range.
            Assert.Equal(8.0, rec.TrackSections[0].startUT);
            Assert.Equal(34.0, rec.TrackSections[0].endUT);
            Assert.Equal(34.0, tip.TrackSections[0].startUT);
            Assert.Equal(53.0, tip.TrackSections[0].endUT);

            // Synthetic boundary metadata: tail's boundaryDiscontinuityMeters == 0,
            // both halves have isBoundarySeam=false.
            Assert.False(rec.TrackSections[0].isBoundarySeam);
            Assert.False(tip.TrackSections[0].isBoundarySeam);
            Assert.Equal(0f, tip.TrackSections[0].boundaryDiscontinuityMeters);

            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("SplitAtUT: split rec-mid")
                && l.Contains("syntheticBoundaryInserted=true"));
        }

        #endregion

        #region Boundary-seam override

        [Fact]
        public void SplitAtUT_BoundarySeamOnStraddlingSection_LogsOverride()
        {
            // Single section flagged isBoundarySeam=true. SplitAtUT must override
            // the seam protection (logging a Warn) and proceed with the split.
            // Point at splitUT=34 avoids the boundary-interpolation Slerp path.
            var rec = new Recording
            {
                RecordingId = "rec-seam",
            };
            rec.Points.Add(PointAt(8.0));
            rec.Points.Add(PointAt(34.0));
            rec.Points.Add(PointAt(53.0));
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 8.0,
                endUT = 53.0,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                isBoundarySeam = true,
                frames = new List<TrajectoryPoint>
                {
                    PointAt(8.0),
                    PointAt(34.0),
                    PointAt(53.0),
                },
            });

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("seam protection")
                && l.Contains("rec-seam")
                && l.Contains("one-off override for re-fly commit"));

            // Both halves of the synthetic split MUST have isBoundarySeam=false.
            Assert.False(rec.TrackSections[0].isBoundarySeam);
            Assert.False(tip.TrackSections[0].isBoundarySeam);
        }

        #endregion

        #region v13 debris bodyFixedFrames guard

        [Fact]
        public void SplitAtUT_V13TailBodyFixedFramesUnderMinimum_ReturnsNull()
        {
            // Section straddling splitUT with bodyFixedFrames such that the
            // post-split tail ends up with only 1 sample (under the v13 minimum
            // of 2). SplitAtUT must return null AND leave original untouched.
            var rec = new Recording
            {
                RecordingId = "rec-bf-undermin",
                IsDebris = true,
                DebrisParentRecordingId = "parent-rec",
            };
            rec.Points.Add(PointAt(8.0));
            rec.Points.Add(PointAt(53.0));
            var sectionFrames = new List<TrajectoryPoint>
            {
                PointAt(8.0),
                PointAt(20.0),
                PointAt(53.0),
            };
            // bodyFixedFrames: 3 samples pre-rewind, 1 sample post-rewind -> tail
            // would have 1 sample, head would have 3. Tail fails the minimum.
            var bodyFixedFrames = new List<TrajectoryPoint>
            {
                PointAt(10.0),
                PointAt(20.0),
                PointAt(30.0),
                PointAt(40.0), // only this one lands on tail (UT >= 34)
            };
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Relative,
                anchorRecordingId = "parent-rec",
                startUT = 8.0,
                endUT = 53.0,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                frames = sectionFrames,
                bodyFixedFrames = bodyFixedFrames,
            });

            // Capture pre-call structural snapshot for byte-identical assertion.
            int sectionCountBefore = rec.TrackSections.Count;
            double sectionEndUtBefore = rec.TrackSections[0].endUT;
            int framesCountBefore = rec.TrackSections[0].frames.Count;
            int bodyFixedCountBefore = rec.TrackSections[0].bodyFixedFrames.Count;
            int pointsCountBefore = rec.Points.Count;

            // splitUT = 41 -> tail bodyFixedFrames would have 0 samples (all are <41);
            // use splitUT=35 to keep 40 on tail (1 sample, below 2-sample minimum).
            var tip = RecordingOptimizer.SplitAtUT(rec, 35.0);

            Assert.Null(tip);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("v13 debris contract")
                && l.Contains("bodyFixedFrames")
                && l.Contains("rec-bf-undermin"));

            // Mutation-ordering invariant: original.TrackSections must be
            // structurally identical to its pre-call state.
            Assert.Equal(sectionCountBefore, rec.TrackSections.Count);
            Assert.Equal(sectionEndUtBefore, rec.TrackSections[0].endUT);
            Assert.Equal(framesCountBefore, rec.TrackSections[0].frames.Count);
            Assert.Equal(bodyFixedCountBefore, rec.TrackSections[0].bodyFixedFrames.Count);
            Assert.Equal(pointsCountBefore, rec.Points.Count);
        }

        #endregion
    }
}
