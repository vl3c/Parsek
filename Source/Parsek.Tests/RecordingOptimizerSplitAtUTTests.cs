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
    /// pre-condition guards, OrbitSegment tail-cloning across the split (Task A7
    /// removed the prior orbit-segment-straddle guard in favour of struct-copy
    /// tail clones inside <see cref="RecordingOptimizer.SplitAtSection"/>), the
    /// v13 debris bodyFixedFrames sample-count guard, the boundary-seam override
    /// warning, and the alignment-vs-synthetic-insert branch in step 4.
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
            // Pass 3 Test gap 2: byte-equivalence on precondition guards.
            int sectionCountBefore = rec.TrackSections.Count;
            double sectionEndUtBefore = rec.TrackSections[0].endUT;
            int pointsCountBefore = rec.Points.Count;

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.Null(tip);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("precondition violation")
                && l.Contains("rec-pre")
                && l.Contains("do not strictly span"));
            // The precondition fires BEFORE the Ensure call, so no mutation
            // is possible — but pin the byte-identical contract anyway so a
            // future refactor that reorders the guards trips this test.
            Assert.Equal(sectionCountBefore, rec.TrackSections.Count);
            Assert.Equal(sectionEndUtBefore, rec.TrackSections[0].endUT);
            Assert.Equal(pointsCountBefore, rec.Points.Count);
        }

        [Fact]
        public void SplitAtUT_OriginEntirelyPostSplitUT_ReturnsNull()
        {
            var rec = MakeSimpleRecording(40.0, 53.0, "rec-post");
            int sectionCountBefore = rec.TrackSections.Count;
            double sectionStartUtBefore = rec.TrackSections[0].startUT;
            int pointsCountBefore = rec.Points.Count;

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.Null(tip);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("precondition violation")
                && l.Contains("rec-post")
                && l.Contains("do not strictly span"));
            Assert.Equal(sectionCountBefore, rec.TrackSections.Count);
            Assert.Equal(sectionStartUtBefore, rec.TrackSections[0].startUT);
            Assert.Equal(pointsCountBefore, rec.Points.Count);
        }

        [Fact]
        public void SplitAtUT_NaNSplitUT_ReturnsNull()
        {
            var rec = MakeSimpleRecording(8.0, 53.0, "rec-nan");
            int sectionCountBefore = rec.TrackSections.Count;
            int pointsCountBefore = rec.Points.Count;
            double sectionEndUtBefore = rec.TrackSections[0].endUT;

            var tip = RecordingOptimizer.SplitAtUT(rec, double.NaN);

            Assert.Null(tip);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("precondition violation")
                && l.Contains("splitUT is NaN")
                && l.Contains("rec-nan"));
            Assert.Equal(sectionCountBefore, rec.TrackSections.Count);
            Assert.Equal(pointsCountBefore, rec.Points.Count);
            Assert.Equal(sectionEndUtBefore, rec.TrackSections[0].endUT);
        }

        [Fact]
        public void SplitAtUT_NullOriginalRecording_ReturnsNullWithoutCrash()
        {
            // The null-original precondition is the topmost guard; trivially
            // byte-identical because there's no recording to mutate.
            var tip = RecordingOptimizer.SplitAtUT(null, 34.0);

            Assert.Null(tip);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("precondition violation")
                && l.Contains("original recording is null"));
        }

        #endregion

        #region OrbitSegment tail-clone across split UT

        [Fact]
        public void SplitAtUT_OrbitSegmentStraddlesSplitUT_TailClonesIntoTip()
        {
            // Build a recording where an OrbitSegment straddles splitUT.
            // After Task A7, the prior straddle guard is gone; SplitAtSection
            // now HEAD-trims the straddler's endUT down to splitUT AND
            // tail-clones the post-split portion into TIP at startUT=splitUT.
            //
            // Setup notes:
            //  - TrackSection.frames is null so HasCompleteTrackSectionPayloadForFlatSync
            //    fails and SplitAtSection's downstream TrySyncFlat does not rebuild
            //    OrbitSegments from sections (which would undo the partition under test).
            //  - OrbitSegment.isPredicted=true so the EnsureCheckpoint bridge skips
            //    creating an OrbitalCheckpoint section, which would otherwise be
            //    inserted at startUT=20 and then re-sorted in front of the synthetic
            //    [34, 53] tail section — invalidating sectionIndex and routing
            //    SplitAtSection into the boundary-interpolation Slerp path that
            //    requires the Unity runtime.
            var rec = new Recording
            {
                RecordingId = "rec-orbit-straddle",
            };
            rec.Points.Add(PointAt(8.0));
            rec.Points.Add(PointAt(34.0));
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
                frames = null,
            });
            var orbitalRot = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f);
            var angVel = new Vector3(0.01f, 0.02f, 0.03f);
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 20.0,
                endUT = 50.0,
                bodyName = "Kerbin",
                inclination = 12.5,
                eccentricity = 0.123,
                semiMajorAxis = 700000.0,
                longitudeOfAscendingNode = 45.0,
                argumentOfPeriapsis = 90.0,
                meanAnomalyAtEpoch = 1.5,
                epoch = 20.0,
                isPredicted = true,
                orbitalFrameRotation = orbitalRot,
                angularVelocity = angVel,
            });

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            // Split now succeeds: returns non-null TIP.
            Assert.NotNull(tip);

            // HEAD retains a single head-trimmed segment [20, 34].
            Assert.Single(rec.OrbitSegments);
            Assert.Equal(20.0, rec.OrbitSegments[0].startUT);
            Assert.Equal(34.0, rec.OrbitSegments[0].endUT);

            // TIP has the tail-clone [34, 50] with identical Kepler elements.
            Assert.Single(tip.OrbitSegments);
            var tipSeg = tip.OrbitSegments[0];
            Assert.Equal(34.0, tipSeg.startUT);
            Assert.Equal(50.0, tipSeg.endUT);
            Assert.Equal("Kerbin", tipSeg.bodyName);
            Assert.Equal(12.5, tipSeg.inclination);
            Assert.Equal(0.123, tipSeg.eccentricity);
            Assert.Equal(700000.0, tipSeg.semiMajorAxis);
            Assert.Equal(45.0, tipSeg.longitudeOfAscendingNode);
            Assert.Equal(90.0, tipSeg.argumentOfPeriapsis);
            Assert.Equal(1.5, tipSeg.meanAnomalyAtEpoch);
            Assert.Equal(20.0, tipSeg.epoch);
            Assert.True(tipSeg.isPredicted);
            Assert.Equal(orbitalRot, tipSeg.orbitalFrameRotation);
            Assert.Equal(angVel, tipSeg.angularVelocity);

            // Verbose partition log emitted.
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("partitioned OrbitSegments")
                && l.Contains("tail-clones=1"));
        }

        [Fact]
        public void SplitAtUT_MultipleOrbitSegmentsAcrossSplitUT_PartitionsCorrectly()
        {
            // Three segments: one entirely pre-split [5, 15], one straddling
            // [20, 50], one entirely post-split [55, 80]. After split at 34:
            //   HEAD.OrbitSegments == [ [5,15], [20,34] ]
            //   TIP.OrbitSegments  == [ [34,50], [55,80] ]  (UT-ascending)
            // See _TailClonesIntoTip for the null-frames rationale.
            var rec = new Recording
            {
                RecordingId = "rec-orbit-multi",
            };
            // Recording must strictly span [5, 80] (precondition); add Points and a
            // single covering TrackSection (without per-section frames) so HasComplete
            // fails and TrySyncFlat does not rebuild OrbitSegments from sections.
            rec.Points.Add(PointAt(5.0));
            rec.Points.Add(PointAt(34.0));
            rec.Points.Add(PointAt(80.0));
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 5.0,
                endUT = 80.0,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                frames = null,
            });
            // isPredicted=true: EnsureCheckpoint skips creating OC sections that would
            // otherwise be re-sorted in front of the synthetic boundary section and
            // route SplitAtSection into the boundary-interpolation Slerp path.
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 5.0, endUT = 15.0, bodyName = "Kerbin",
                inclination = 0.0, eccentricity = 0.0, semiMajorAxis = 700000.0,
                longitudeOfAscendingNode = 0.0, argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0, epoch = 5.0,
                isPredicted = true,
                orbitalFrameRotation = Quaternion.identity,
                angularVelocity = Vector3.zero,
            });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 20.0, endUT = 50.0, bodyName = "Kerbin",
                inclination = 0.0, eccentricity = 0.0, semiMajorAxis = 700000.0,
                longitudeOfAscendingNode = 0.0, argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0, epoch = 20.0,
                isPredicted = true,
                orbitalFrameRotation = Quaternion.identity,
                angularVelocity = Vector3.zero,
            });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 55.0, endUT = 80.0, bodyName = "Kerbin",
                inclination = 0.0, eccentricity = 0.0, semiMajorAxis = 700000.0,
                longitudeOfAscendingNode = 0.0, argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0, epoch = 55.0,
                isPredicted = true,
                orbitalFrameRotation = Quaternion.identity,
                angularVelocity = Vector3.zero,
            });

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);

            // HEAD: [5,15] untouched + [20,34] head-trimmed.
            Assert.Equal(2, rec.OrbitSegments.Count);
            Assert.Equal(5.0, rec.OrbitSegments[0].startUT);
            Assert.Equal(15.0, rec.OrbitSegments[0].endUT);
            Assert.Equal(20.0, rec.OrbitSegments[1].startUT);
            Assert.Equal(34.0, rec.OrbitSegments[1].endUT);

            // TIP: [34,50] tail-clone + [55,80] wholly moved, UT-ascending.
            Assert.Equal(2, tip.OrbitSegments.Count);
            Assert.Equal(34.0, tip.OrbitSegments[0].startUT);
            Assert.Equal(50.0, tip.OrbitSegments[0].endUT);
            Assert.Equal(55.0, tip.OrbitSegments[1].startUT);
            Assert.Equal(80.0, tip.OrbitSegments[1].endUT);

            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("partitioned OrbitSegments")
                && l.Contains("whole moved=1")
                && l.Contains("tail-clones=1"));
        }

        [Fact]
        public void SplitAtUT_OrbitSegmentStraddlesSplitUT_PreservesIsPredictedFlag()
        {
            // The isPredicted flag is part of the struct; value-copy must preserve it.
            // See _TailClonesIntoTip for the null-frames rationale.
            var rec = new Recording
            {
                RecordingId = "rec-orbit-predicted",
            };
            rec.Points.Add(PointAt(8.0));
            rec.Points.Add(PointAt(34.0));
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
                frames = null,
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
                isPredicted = true,
                orbitalFrameRotation = Quaternion.identity,
                angularVelocity = Vector3.zero,
            });

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            Assert.Single(tip.OrbitSegments);
            Assert.True(tip.OrbitSegments[0].isPredicted);
            // HEAD-side trimmed segment preserves the flag too.
            Assert.Single(rec.OrbitSegments);
            Assert.True(rec.OrbitSegments[0].isPredicted);
        }

        [Fact]
        public void SplitAtUT_TailClonesHyperbolicOrbitSegment_PreservesKeplerElements()
        {
            // Pass 5 review L4: SplitAtSection's tail-clone is a struct
            // value-copy with adjusted startUT — Kepler elements describe
            // the whole conic regardless of eccentricity, so the same
            // value-copy is correct for elliptical, parabolic, and
            // hyperbolic. This test pins the hyperbolic case (escape
            // trajectory, e > 1, a < 0 by Kepler convention) so a future
            // refactor that adds anything non-value-copy (orbit-state
            // propagation, epoch renormalization, semi-major-axis
            // normalization) is caught immediately.
            //
            // Fixture mirrors the elliptical happy-path: null frames so
            // TrySyncFlat doesn't rebuild, isPredicted=true so Ensure
            // doesn't materialize a checkpoint section.
            var rec = new Recording { RecordingId = "rec-orbit-hyperbolic" };
            rec.Points.Add(PointAt(8.0));
            rec.Points.Add(PointAt(34.0));
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
                frames = null,
            });
            // e=1.5 > 1 → hyperbolic; a<0 by Kepler convention for
            // hyperbolic orbits. semi-major-axis as negative makes the
            // ellipse formula degenerate — only Kepler-conic-handling code
            // should accept this struct without normalization.
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 20.0,
                endUT = 50.0,
                bodyName = "Kerbin",
                inclination = 30.0,
                eccentricity = 1.5,
                semiMajorAxis = -800000.0,
                longitudeOfAscendingNode = 60.0,
                argumentOfPeriapsis = 120.0,
                meanAnomalyAtEpoch = -0.7,
                epoch = 20.0,
                isPredicted = true,
            });

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            Assert.Single(rec.OrbitSegments);
            Assert.Equal(20.0, rec.OrbitSegments[0].startUT);
            Assert.Equal(34.0, rec.OrbitSegments[0].endUT);

            // TIP carries an identical Kepler-element copy with startUT
            // bumped to splitUT. Negative semi-major-axis preserved.
            Assert.Single(tip.OrbitSegments);
            var tipSeg = tip.OrbitSegments[0];
            Assert.Equal(34.0, tipSeg.startUT);
            Assert.Equal(50.0, tipSeg.endUT);
            Assert.Equal("Kerbin", tipSeg.bodyName);
            Assert.Equal(30.0, tipSeg.inclination);
            Assert.Equal(1.5, tipSeg.eccentricity);
            Assert.Equal(-800000.0, tipSeg.semiMajorAxis);
            Assert.Equal(60.0, tipSeg.longitudeOfAscendingNode);
            Assert.Equal(120.0, tipSeg.argumentOfPeriapsis);
            Assert.Equal(-0.7, tipSeg.meanAnomalyAtEpoch);
            Assert.Equal(20.0, tipSeg.epoch);
            Assert.True(tipSeg.isPredicted);
        }

        [Fact]
        public void SplitAtUT_TailClonesParabolicOrbitSegment_PreservesKeplerElements()
        {
            // Pass 5 review L4: e=1.0 exactly is the parabolic edge case.
            // Some Kepler implementations special-case e==1 because the
            // ellipse formula has a singularity there. The tail-clone is
            // pure struct value-copy so the parabolic markers (e=1.0,
            // whatever the codebase chooses to store for semiMajorAxis in
            // the parabolic case — typically a sentinel like double.PositiveInfinity
            // or a very large value) must round-trip identically.
            //
            // Most KSP saves never see e=1.0 exactly in stock; mods like
            // Trajectories / Principia / RealSolarSystem do produce them
            // during transfer planning. Lock the contract now.
            var rec = new Recording { RecordingId = "rec-orbit-parabolic" };
            rec.Points.Add(PointAt(8.0));
            rec.Points.Add(PointAt(34.0));
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
                frames = null,
            });
            // Parabolic markers chosen to surface any "normalize to ellipse"
            // bug: e=1.0 exactly, semiMajorAxis = a large finite value
            // (real codebases may use infinity, NaN, or a sentinel — the
            // test just locks whatever the splitter sees).
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 20.0,
                endUT = 50.0,
                bodyName = "Kerbin",
                inclination = 0.0,
                eccentricity = 1.0,
                semiMajorAxis = 1e15, // very large; parabolic sentinel
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = 20.0,
                isPredicted = true,
            });

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            Assert.Single(rec.OrbitSegments);

            Assert.Single(tip.OrbitSegments);
            var tipSeg = tip.OrbitSegments[0];
            Assert.Equal(34.0, tipSeg.startUT);
            Assert.Equal(50.0, tipSeg.endUT);
            Assert.Equal(1.0, tipSeg.eccentricity);
            Assert.Equal(1e15, tipSeg.semiMajorAxis);
            Assert.Equal(0.0, tipSeg.inclination);
            Assert.Equal(20.0, tipSeg.epoch);
        }

        #endregion

        #region Straddle-section checkpoints partition

        /// <summary>
        /// Builds a Recording with a single OrbitalCheckpoint TrackSection
        /// (frames=null so flat-trajectory rebuild paths are quiescent) spanning
        /// [startUT, endUT] with the supplied <paramref name="checkpoints"/>
        /// list. Includes a Point at exactly splitUT so SplitAtSection's
        /// boundary-interpolation branch (Unity-runtime-only Slerp) is bypassed.
        /// Top-level Recording.OrbitSegments is also seeded with the same
        /// isPredicted=true segments so SplitAtSection's step 7 partition pass
        /// behaves symmetrically with the per-section partition under test.
        /// </summary>
        private static Recording MakeRecordingWithSectionCheckpoints(double startUT,
            double endUT, double splitUT, List<OrbitSegment> checkpoints,
            string recordingId = "rec-section-cp")
        {
            var rec = new Recording { RecordingId = recordingId };
            rec.Points.Add(PointAt(startUT));
            rec.Points.Add(PointAt(splitUT));
            rec.Points.Add(PointAt(endUT));
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = startUT,
                endUT = endUT,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                frames = null,
                checkpoints = checkpoints,
            });
            return rec;
        }

        private static OrbitSegment MakeCheckpoint(double startUT, double endUT,
            double inclination = 0.0, double eccentricity = 0.0,
            string bodyName = "Kerbin", bool isPredicted = true)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                bodyName = bodyName,
                inclination = inclination,
                eccentricity = eccentricity,
                semiMajorAxis = 700000.0,
                longitudeOfAscendingNode = 0.0,
                argumentOfPeriapsis = 0.0,
                meanAnomalyAtEpoch = 0.0,
                epoch = startUT,
                isPredicted = isPredicted,
                orbitalFrameRotation = Quaternion.identity,
                angularVelocity = Vector3.zero,
            };
        }

        [Fact]
        public void SplitAtUT_StraddleSectionCheckpointsPartitionedByUT()
        {
            // Single TrackSection [10, 50] with two checkpoints:
            //   cp0 [10, 30] — entirely pre-split.
            //   cp1 [20, 40] — straddles splitUT=34.
            // splitUT=34 falls inside the section -> synthetic boundary insert
            // path partitions the section's checkpoints list.
            //
            // Expected:
            //   headSection.checkpoints = [ [10,30], [20,34] (head-trimmed straddler) ]
            //   tailSection.checkpoints = [ [34,40] (tail-cloned straddler) ]
            var checkpoints = new List<OrbitSegment>
            {
                MakeCheckpoint(10.0, 30.0, inclination: 11.0, eccentricity: 0.11),
                MakeCheckpoint(20.0, 40.0, inclination: 22.0, eccentricity: 0.22),
            };
            var rec = MakeRecordingWithSectionCheckpoints(10.0, 50.0, 34.0,
                checkpoints, "rec-cp-straddle");
            // Mirror the per-section checkpoints into the top-level
            // OrbitSegments list — SplitAtSection's step 7 will partition this
            // list symmetrically with the per-section partition under test.
            rec.OrbitSegments.Add(MakeCheckpoint(10.0, 30.0, 11.0, 0.11));
            rec.OrbitSegments.Add(MakeCheckpoint(20.0, 40.0, 22.0, 0.22));

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);

            // HEAD: single section [10,34] with two checkpoints.
            Assert.Single(rec.TrackSections);
            var headSection = rec.TrackSections[0];
            Assert.Equal(10.0, headSection.startUT);
            Assert.Equal(34.0, headSection.endUT);
            Assert.NotNull(headSection.checkpoints);
            Assert.Equal(2, headSection.checkpoints.Count);
            // [10,30] preserved verbatim (pre-split).
            Assert.Equal(10.0, headSection.checkpoints[0].startUT);
            Assert.Equal(30.0, headSection.checkpoints[0].endUT);
            Assert.Equal(11.0, headSection.checkpoints[0].inclination);
            Assert.Equal(0.11, headSection.checkpoints[0].eccentricity);
            Assert.True(headSection.checkpoints[0].isPredicted);
            Assert.Equal("Kerbin", headSection.checkpoints[0].bodyName);
            // [20,40] head-trimmed to [20,34]; Kepler elements unchanged.
            Assert.Equal(20.0, headSection.checkpoints[1].startUT);
            Assert.Equal(34.0, headSection.checkpoints[1].endUT);
            Assert.Equal(22.0, headSection.checkpoints[1].inclination);
            Assert.Equal(0.22, headSection.checkpoints[1].eccentricity);
            Assert.True(headSection.checkpoints[1].isPredicted);
            Assert.Equal("Kerbin", headSection.checkpoints[1].bodyName);

            // TIP: single section [34,50] with one tail-cloned checkpoint.
            Assert.Single(tip.TrackSections);
            var tailSection = tip.TrackSections[0];
            Assert.Equal(34.0, tailSection.startUT);
            Assert.Equal(50.0, tailSection.endUT);
            Assert.NotNull(tailSection.checkpoints);
            Assert.Single(tailSection.checkpoints);
            // [20,40] tail-cloned to [34,40]; Kepler elements unchanged.
            Assert.Equal(34.0, tailSection.checkpoints[0].startUT);
            Assert.Equal(40.0, tailSection.checkpoints[0].endUT);
            Assert.Equal(22.0, tailSection.checkpoints[0].inclination);
            Assert.Equal(0.22, tailSection.checkpoints[0].eccentricity);
            Assert.True(tailSection.checkpoints[0].isPredicted);
            Assert.Equal("Kerbin", tailSection.checkpoints[0].bodyName);
        }

        [Fact]
        public void SplitAtUT_StraddleSectionCheckpointsPartition_PreservesNonStraddlingCheckpoints()
        {
            // Three checkpoints in a single straddling TrackSection [5, 80]:
            //   cp0 [5,15]   — entirely pre-split, no straddle.
            //   cp1 [20,50]  — straddles splitUT=34.
            //   cp2 [55,80]  — entirely post-split, no straddle.
            //
            // Expected:
            //   headSection.checkpoints = [ [5,15], [20,34] (trimmed) ]
            //   tailSection.checkpoints = [ [34,50] (tail-clone), [55,80] ]
            // All UT-ascending within each list.
            var checkpoints = new List<OrbitSegment>
            {
                MakeCheckpoint(5.0, 15.0, inclination: 11.0, eccentricity: 0.11),
                MakeCheckpoint(20.0, 50.0, inclination: 22.0, eccentricity: 0.22),
                MakeCheckpoint(55.0, 80.0, inclination: 33.0, eccentricity: 0.33),
            };
            var rec = MakeRecordingWithSectionCheckpoints(5.0, 80.0, 34.0,
                checkpoints, "rec-cp-three");
            // Mirror at the top level so SplitAtSection's step 7 partition runs
            // symmetrically — keeps the recording self-consistent.
            rec.OrbitSegments.Add(MakeCheckpoint(5.0, 15.0, 11.0, 0.11));
            rec.OrbitSegments.Add(MakeCheckpoint(20.0, 50.0, 22.0, 0.22));
            rec.OrbitSegments.Add(MakeCheckpoint(55.0, 80.0, 33.0, 0.33));

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);

            // HEAD section checkpoints: [5,15] verbatim + [20,34] head-trimmed.
            Assert.Single(rec.TrackSections);
            var headSection = rec.TrackSections[0];
            Assert.NotNull(headSection.checkpoints);
            Assert.Equal(2, headSection.checkpoints.Count);
            Assert.Equal(5.0, headSection.checkpoints[0].startUT);
            Assert.Equal(15.0, headSection.checkpoints[0].endUT);
            Assert.Equal(11.0, headSection.checkpoints[0].inclination);
            Assert.Equal(20.0, headSection.checkpoints[1].startUT);
            Assert.Equal(34.0, headSection.checkpoints[1].endUT);
            Assert.Equal(22.0, headSection.checkpoints[1].inclination);
            // UT-ascending order.
            Assert.True(headSection.checkpoints[0].startUT < headSection.checkpoints[1].startUT);

            // TIP section checkpoints: [34,50] tail-clone + [55,80] verbatim.
            Assert.Single(tip.TrackSections);
            var tailSection = tip.TrackSections[0];
            Assert.NotNull(tailSection.checkpoints);
            Assert.Equal(2, tailSection.checkpoints.Count);
            Assert.Equal(34.0, tailSection.checkpoints[0].startUT);
            Assert.Equal(50.0, tailSection.checkpoints[0].endUT);
            Assert.Equal(22.0, tailSection.checkpoints[0].inclination);
            Assert.Equal(55.0, tailSection.checkpoints[1].startUT);
            Assert.Equal(80.0, tailSection.checkpoints[1].endUT);
            Assert.Equal(33.0, tailSection.checkpoints[1].inclination);
            // UT-ascending order.
            Assert.True(tailSection.checkpoints[0].startUT < tailSection.checkpoints[1].startUT);
        }

        [Fact]
        public void SplitAtUT_StraddleSectionWithNullCheckpoints_PreservesNullOnBothHalves()
        {
            // A straddling section with checkpoints=null must yield head+tail
            // sections both with checkpoints=null. No spurious empty List<>
            // allocation on either side.
            var rec = new Recording { RecordingId = "rec-cp-null" };
            // Point at splitUT=34 bypasses SplitAtSection's boundary-interpolation
            // Slerp path (Unity-runtime-only), mirroring the other tests here.
            rec.Points.Add(PointAt(8.0));
            rec.Points.Add(PointAt(34.0));
            rec.Points.Add(PointAt(53.0));
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 8.0,
                endUT = 53.0,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                frames = null,
                checkpoints = null,
            });

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            Assert.Single(rec.TrackSections);
            Assert.Single(tip.TrackSections);
            Assert.Null(rec.TrackSections[0].checkpoints);
            Assert.Null(tip.TrackSections[0].checkpoints);
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
                ParentAnchorRecordingId = "parent-rec",
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

        #region Defensive guards (Pass 2 review Opus-H1 / Opus-H2)

        [Fact]
        public void SplitAtUT_SplitUTPastAllSectionStartUTs_ReturnsNullWithoutCrash()
        {
            // Pass 2 review Opus-H1: the prior gap-fallback fell through with
            // sectionIndex = TrackSections.Count, which SplitAtSection's first
            // line dereferenced as `original.TrackSections[Count].startUT` —
            // ArgumentOutOfRangeException. The fix returns null with a Warn
            // so callers (the splitter) fall back to whole-recording supersede
            // instead of the merge crashing.
            //
            // Fixture: recording's flat Points extend to UT 53 but the only
            // TrackSection covers [8..30]. splitUT=40 satisfies the
            // strict-span pre-condition (8 < 40 < 53) but no TrackSection
            // straddles or follows it.
            var rec = new Recording { RecordingId = "rec-no-section-after-splitUT" };
            rec.Points.Add(PointAt(8.0));
            rec.Points.Add(PointAt(20.0));
            rec.Points.Add(PointAt(30.0));
            rec.Points.Add(PointAt(53.0));
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 8.0,
                endUT = 30.0,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                frames = new List<TrajectoryPoint>
                {
                    PointAt(8.0), PointAt(30.0),
                },
            });
            rec.ExplicitStartUT = 8.0;
            rec.ExplicitEndUT = 53.0;

            int sectionCountBefore = rec.TrackSections.Count;
            double sectionEndUtBefore = rec.TrackSections[0].endUT;
            int pointsCountBefore = rec.Points.Count;

            Recording tip = RecordingOptimizer.SplitAtUT(rec, 40.0);

            Assert.Null(tip);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("defensive guard")
                && l.Contains("past every")
                && l.Contains("rec-no-section-after-splitUT"));

            // Byte-identical contract: no mutation since neither Ensure (no
            // OrbitSegments) nor the synthetic-insert path ran.
            Assert.Equal(sectionCountBefore, rec.TrackSections.Count);
            Assert.Equal(sectionEndUtBefore, rec.TrackSections[0].endUT);
            Assert.Equal(pointsCountBefore, rec.Points.Count);
        }

        [Fact]
        public void SplitAtUT_NullTrackSectionsList_ReturnsNullWithoutCrash()
        {
            // Pass 2 review Opus-H1: structurally pathological but the prior
            // code would have crashed with NullReferenceException (or
            // ArgumentOutOfRangeException on SplitAtSection's index-0 read of
            // a null list).
            var rec = new Recording { RecordingId = "rec-null-sections" };
            rec.Points.Add(PointAt(8.0));
            rec.Points.Add(PointAt(53.0));
            rec.TrackSections = null;
            rec.ExplicitStartUT = 8.0;
            rec.ExplicitEndUT = 53.0;

            Recording tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.Null(tip);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("defensive guard")
                && l.Contains("null TrackSections"));
            Assert.Null(rec.TrackSections);
            Assert.Equal(2, rec.Points.Count);
        }

        [Fact]
        public void SplitAtUT_EnsureMutatedThenGapFallbackHits_RestoresPreEnsureState()
        {
            // Pass 2 review Opus-H2: when Ensure has run + mutated (added a
            // checkpoint section from a top-level OrbitSegment) AND a
            // downstream guard returns null (here: the Opus-H1 past-every-
            // startUT case), the byte-identical contract requires the
            // checkpoint section + CachedStats / OrbitSegments to be
            // restored.
            //
            // Fixture: recording has one TrackSection at [8..30], one
            // top-level OrbitSegment at [5..7] that Ensure will materialize
            // into a checkpoint TrackSection. After Ensure, TrackSections
            // are [checkpoint[5..7], section[8..30]] (sorted). splitUT=40
            // is past every TrackSection startUT → Opus-H1 null return →
            // restore must run.
            var rec = new Recording { RecordingId = "rec-ensure-then-gap" };
            rec.Points.Add(PointAt(8.0));
            rec.Points.Add(PointAt(53.0));
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 8.0,
                endUT = 30.0,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                frames = new List<TrajectoryPoint> { PointAt(8.0), PointAt(30.0) },
            });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 5.0,
                endUT = 7.0,
                bodyName = "Kerbin",
                semiMajorAxis = 700_000.0,
                eccentricity = 0.1,
                inclination = 0.5,
                isPredicted = false,
            });
            rec.ExplicitStartUT = 8.0;
            rec.ExplicitEndUT = 53.0;

            int trackSectionsBefore = rec.TrackSections.Count;
            int orbitSegmentsBefore = rec.OrbitSegments.Count;
            // CachedStats is null by default — no pre-existing cache to restore.

            Recording tip = RecordingOptimizer.SplitAtUT(rec, 40.0);

            Assert.Null(tip);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("defensive guard"));
            // The restoration log fires only when ensureStats.Changed was true.
            // The orbit at [5..7] is non-overlapping with section [8..30], so
            // Ensure adds a checkpoint section -> stats.Changed = true ->
            // restoration fires.
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("restored pre-Ensure snapshot"));

            // Byte-identical: track sections + orbit segments back to
            // pre-Ensure counts.
            Assert.Equal(trackSectionsBefore, rec.TrackSections.Count);
            Assert.Equal(orbitSegmentsBefore, rec.OrbitSegments.Count);
            Assert.Equal(30.0, rec.TrackSections[0].endUT);
        }

        #endregion

        #region Robotic pose seeds across the split (M4)

        private static PartEvent RoboticEvent(
            double ut, PartEventType type, float value, uint pid = 100000, int moduleIndex = 0)
        {
            return new PartEvent
            {
                ut = ut,
                partPersistentId = pid,
                eventType = type,
                partName = "servoHinge",
                value = value,
                moduleIndex = moduleIndex,
            };
        }

        [Fact]
        public void SplitAtUT_RoboticPoseBeforeSplit_TipGetsMotionStoppedSeedAtSplitUT()
        {
            // A hinge that swung to 42 degrees and parked BEFORE the cut. Without a
            // seed the tail's ghost renders the prefab pose (arm folded) forever,
            // because the tail owns no robotic event of its own.
            var rec = MakeSimpleRecording(8.0, 53.0, "rec-robotic", midUT: 34.0);
            rec.PartEvents.Add(RoboticEvent(10.0, PartEventType.RoboticMotionStarted, 10f));
            rec.PartEvents.Add(RoboticEvent(20.0, PartEventType.RoboticPositionSample, 30f));
            rec.PartEvents.Add(RoboticEvent(30.0, PartEventType.RoboticMotionStopped, 42f));

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            // Latest-state-wins reducer: ONE seed carrying the last sampled pose.
            Assert.Single(tip.PartEvents);
            PartEvent seed = tip.PartEvents[0];
            Assert.Equal(PartEventType.RoboticMotionStopped, seed.eventType);
            Assert.Equal(34.0, seed.ut);
            Assert.Equal(42f, seed.value);
            Assert.Equal(100000u, seed.partPersistentId);
            Assert.Equal(0, seed.moduleIndex);
            // The head keeps its three real events verbatim.
            Assert.Equal(3, rec.PartEvents.Count);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("transient state seed event(s)")
                && l.Contains("robotics=1"));
        }

        [Fact]
        public void SplitAtUT_RoboticMidStrokeAtSplit_TipSeedCarriesLastSampledPose()
        {
            // Still travelling at the cut (last event is a position sample, not a stop).
            // The seed parks the servo at the sampled pose; the tail's own samples
            // re-arm the motion from there.
            var rec = MakeSimpleRecording(8.0, 53.0, "rec-robotic-midstroke", midUT: 34.0);
            rec.PartEvents.Add(RoboticEvent(10.0, PartEventType.RoboticMotionStarted, 5f));
            rec.PartEvents.Add(RoboticEvent(33.0, PartEventType.RoboticPositionSample, 17.5f));
            rec.PartEvents.Add(RoboticEvent(40.0, PartEventType.RoboticPositionSample, 25f));

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            // Seed first, then the post-split real sample that partitioned across.
            Assert.Equal(2, tip.PartEvents.Count);
            Assert.Equal(PartEventType.RoboticMotionStopped, tip.PartEvents[0].eventType);
            Assert.Equal(34.0, tip.PartEvents[0].ut);
            Assert.Equal(17.5f, tip.PartEvents[0].value);
            Assert.Equal(PartEventType.RoboticPositionSample, tip.PartEvents[1].eventType);
            Assert.Equal(40.0, tip.PartEvents[1].ut);
        }

        [Fact]
        public void SplitAtUT_TwoServosOnOneVessel_EachModuleIndexGetsItsOwnSeed()
        {
            // Robotic keys are module-scoped (EncodeEngineKey), so two servos on the
            // same part must not collapse into one seed the way the pid-keyed
            // deployable / light families do.
            var rec = MakeSimpleRecording(8.0, 53.0, "rec-robotic-two", midUT: 34.0);
            rec.PartEvents.Add(RoboticEvent(10.0, PartEventType.RoboticMotionStopped, 11f, moduleIndex: 0));
            rec.PartEvents.Add(RoboticEvent(12.0, PartEventType.RoboticMotionStopped, 22f, moduleIndex: 1));
            rec.PartEvents.Add(RoboticEvent(14.0, PartEventType.RoboticMotionStopped, 33f, pid: 101111, moduleIndex: 0));

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            Assert.Equal(3, tip.PartEvents.Count);
            Assert.All(tip.PartEvents, e =>
            {
                Assert.Equal(PartEventType.RoboticMotionStopped, e.eventType);
                Assert.Equal(34.0, e.ut);
            });
            Assert.Contains(tip.PartEvents, e => e.partPersistentId == 100000u && e.moduleIndex == 0 && e.value == 11f);
            Assert.Contains(tip.PartEvents, e => e.partPersistentId == 100000u && e.moduleIndex == 1 && e.value == 22f);
            Assert.Contains(tip.PartEvents, e => e.partPersistentId == 101111u && e.moduleIndex == 0 && e.value == 33f);
        }

        [Fact]
        public void SplitAtUT_RealRoboticEventAtBoundary_SeedSuppressedByDedupe()
        {
            // A real robotic event already sitting at splitUT carries its own pose;
            // inserting a seed for the same key would double-apply.
            var rec = MakeSimpleRecording(8.0, 53.0, "rec-robotic-boundary", midUT: 34.0);
            rec.PartEvents.Add(RoboticEvent(10.0, PartEventType.RoboticMotionStopped, 42f));
            rec.PartEvents.Add(RoboticEvent(34.0, PartEventType.RoboticMotionStarted, 42f));

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            Assert.Single(tip.PartEvents);
            Assert.Equal(PartEventType.RoboticMotionStarted, tip.PartEvents[0].eventType);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("transient state seed event(s) at split UT")
                && l.Contains("skippedExistingBoundary=1"));
        }

        [Fact]
        public void SplitAtUT_RoboticPlusPermanentSeed_PermanentSeedsStillPrecedeTransient()
        {
            // ForwardPermanentStateEvents inserts at the front AFTER the transient
            // insertion, so the ordering contract is permanent -> transient. The
            // robotic seed must not disturb it.
            var rec = MakeSimpleRecording(8.0, 53.0, "rec-robotic-order", midUT: 34.0);
            rec.PartEvents.Add(new PartEvent
            {
                ut = 12.0,
                partPersistentId = 102222,
                eventType = PartEventType.ShroudJettisoned,
                partName = "shroud",
            });
            rec.PartEvents.Add(RoboticEvent(20.0, PartEventType.RoboticMotionStopped, 42f));

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            Assert.Equal(2, tip.PartEvents.Count);
            Assert.Equal(PartEventType.ShroudJettisoned, tip.PartEvents[0].eventType);
            Assert.Equal(PartEventType.RoboticMotionStopped, tip.PartEvents[1].eventType);
        }

        [Fact]
        public void BuildTransientStateSeeds_RoboticEventsAfterSplitUT_Ignored()
        {
            // Direct reducer cell: only events at or before splitUT feed the seed.
            var events = new List<PartEvent>
            {
                RoboticEvent(10.0, PartEventType.RoboticMotionStopped, 42f),
                RoboticEvent(50.0, PartEventType.RoboticMotionStopped, 99f),
            };

            List<PartEvent> seeds = RecordingOptimizer.BuildTransientStateSeeds(events, 34.0);

            Assert.Single(seeds);
            Assert.Equal(42f, seeds[0].value);
            Assert.Equal(34.0, seeds[0].ut);
        }

        [Fact]
        public void BuildTransientStateSeeds_RoboticZeroPose_StillEmitsSeed()
        {
            // Unlike the AppendActiveStateSeeds families, a robotic seed is emitted
            // unconditionally: value 0 is a real pose (fully retracted), and the tail
            // may need it to undo a prefab pose that is NOT zero.
            var events = new List<PartEvent>
            {
                RoboticEvent(10.0, PartEventType.RoboticMotionStarted, 30f),
                RoboticEvent(20.0, PartEventType.RoboticMotionStopped, 0f),
            };

            List<PartEvent> seeds = RecordingOptimizer.BuildTransientStateSeeds(events, 34.0);

            Assert.Single(seeds);
            Assert.Equal(PartEventType.RoboticMotionStopped, seeds[0].eventType);
            Assert.Equal(0f, seeds[0].value);
        }

        [Fact]
        public void BuildTransientStateSeeds_RoboticAndEngineSameKey_BothFamiliesSeeded()
        {
            // Family ids must not collide: the engine reducer and the robotic reducer
            // both key by EncodeEngineKey, so a part with an engine and a servo at the
            // same module index must produce two independent seeds.
            var events = new List<PartEvent>
            {
                new PartEvent
                {
                    ut = 10.0,
                    partPersistentId = 100000,
                    eventType = PartEventType.EngineIgnited,
                    partName = "engine",
                    value = 0.8f,
                    moduleIndex = 0,
                },
                RoboticEvent(11.0, PartEventType.RoboticMotionStopped, 42f),
            };

            List<PartEvent> seeds = RecordingOptimizer.BuildTransientStateSeeds(events, 34.0);

            Assert.Equal(2, seeds.Count);
            Assert.Contains(seeds, s => s.eventType == PartEventType.EngineIgnited && s.value == 0.8f);
            Assert.Contains(seeds, s => s.eventType == PartEventType.RoboticMotionStopped && s.value == 42f);
        }

        #endregion

        #region Inactive-direction reversible seeds (post-M1 stale-baseline correction)

        private static PartEvent VisualEvent(
            double ut, PartEventType type, uint pid = 100000, float value = 0f)
        {
            return new PartEvent
            {
                ut = ut,
                partPersistentId = pid,
                eventType = type,
                partName = "reversiblePart",
                value = value,
            };
        }

        /// <summary>
        /// The exact spaceplane scenario the M1 baseline regressed: gear DOWN in the
        /// launch-time snapshot the split TIP inherits, retracted during the head span,
        /// then a routine env-boundary split. Pre-fix the TIP got no seed at all and its
        /// ghost rendered gear down for its whole span; the seed is the only thing that can
        /// override a stale baseline.
        /// </summary>
        [Theory]
        [InlineData(PartEventType.GearDeployed, PartEventType.GearRetracted)]
        [InlineData(PartEventType.DeployableExtended, PartEventType.DeployableRetracted)]
        [InlineData(PartEventType.CargoBayOpened, PartEventType.CargoBayClosed)]
        [InlineData(PartEventType.LightOn, PartEventType.LightOff)]
        [InlineData(PartEventType.LightBlinkEnabled, PartEventType.LightBlinkDisabled)]
        [InlineData(PartEventType.ParachuteSemiDeployed, PartEventType.ParachuteCut)]
        public void BuildTransientStateSeeds_ActiveThenInactiveBeforeSplit_SeedsInactiveDirection(
            PartEventType onEvent, PartEventType offEvent)
        {
            var events = new List<PartEvent>
            {
                VisualEvent(10.0, onEvent),
                VisualEvent(20.0, offEvent),
            };

            List<PartEvent> seeds = RecordingOptimizer.BuildTransientStateSeeds(events, 34.0);

            PartEvent seed = Assert.Single(seeds);
            Assert.Equal(offEvent, seed.eventType);
            Assert.Equal(34.0, seed.ut);
            Assert.Equal(100000u, seed.partPersistentId);
        }

        [Theory]
        [InlineData(PartEventType.GearRetracted)]
        [InlineData(PartEventType.DeployableRetracted)]
        [InlineData(PartEventType.CargoBayClosed)]
        [InlineData(PartEventType.LightOff)]
        [InlineData(PartEventType.ParachuteCut)]
        public void BuildTransientStateSeeds_InactiveOnlyBeforeSplit_StillSeeds(
            PartEventType offEvent)
        {
            // No paired "on" event in the head span: the inactive state is still the
            // observed truth at the cut, and still the only correction available for a
            // stale launch-time baseline that says the opposite.
            var events = new List<PartEvent> { VisualEvent(20.0, offEvent) };

            List<PartEvent> seeds = RecordingOptimizer.BuildTransientStateSeeds(events, 34.0);

            PartEvent seed = Assert.Single(seeds);
            Assert.Equal(offEvent, seed.eventType);
        }

        [Fact]
        public void BuildTransientStateSeeds_InactiveSeedsSurviveIntoTheTip()
        {
            // End-to-end through SplitAtUT rather than the reducer: the TIP's PartEvents
            // must actually carry the retract at its start UT.
            var rec = MakeSimpleRecording(8.0, 53.0, "rec-gear-retract", midUT: 34.0);
            rec.PartEvents.Add(VisualEvent(10.0, PartEventType.GearDeployed));
            rec.PartEvents.Add(VisualEvent(20.0, PartEventType.GearRetracted));

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            PartEvent seed = Assert.Single(tip.PartEvents);
            Assert.Equal(PartEventType.GearRetracted, seed.eventType);
            Assert.Equal(34.0, seed.ut);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("visualStatesOff=1"));
        }

        [Fact]
        public void BuildTransientStateSeeds_ParachuteDestroyed_NoDuplicateAgainstPermanentForward()
        {
            // ParachuteDestroyed is in IsPermanentVisualStateEvent, so
            // ForwardPermanentStateEvents copies it verbatim — and it runs AFTER the
            // transient insertion, so the boundary dedupe cannot see the forwarded copy.
            // The inactive emitter therefore has to skip it itself or the TIP gets two.
            var rec = MakeSimpleRecording(8.0, 53.0, "rec-chute-destroyed", midUT: 34.0);
            rec.PartEvents.Add(VisualEvent(10.0, PartEventType.ParachuteDeployed));
            rec.PartEvents.Add(VisualEvent(20.0, PartEventType.ParachuteDestroyed));

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            Assert.Single(tip.PartEvents);
            Assert.Equal(PartEventType.ParachuteDestroyed, tip.PartEvents[0].eventType);
        }

        /// <summary>
        /// MERGED-BEHAVIOR CELL (bidirectional seed emission x the four-state parachute
        /// machine). Neither branch could state this alone: the four-state machine gave
        /// Repacked its own event type and its own cap-restoring pose, and bidirectional
        /// emission is what makes an INACTIVE terminal state reach the tail as a seed at
        /// all. Composed, the pair has to carry the terminal state ACROSS the cut with its
        /// identity intact — a repacked chute and a cut chute are both "not flying" and
        /// were both once seeded as nothing, but they render differently, so collapsing
        /// them to one inactive type (or to no seed) is now a visible defect on the TIP.
        ///
        /// Deployed -> Cut -> Repacked is the ordering that makes it discriminating: it is
        /// the full EVA repack story, and every intermediate state is one the reducer must
        /// discard in favour of the last.
        /// </summary>
        [Fact]
        public void SplitAtUT_HeadEndsRepacked_TipSeedsRepacked_AndRendersStowedWithCap()
        {
            var rec = MakeSimpleRecording(8.0, 53.0, "rec-chute-repacked", midUT: 34.0);
            rec.PartEvents.Add(VisualEvent(10.0, PartEventType.ParachuteDeployed));
            rec.PartEvents.Add(VisualEvent(20.0, PartEventType.ParachuteCut));
            rec.PartEvents.Add(VisualEvent(25.0, PartEventType.ParachuteRepacked));

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            PartEvent seed = Assert.Single(tip.PartEvents);
            // The seed TYPE is the terminal state verbatim, not a shared inactive token.
            Assert.Equal(PartEventType.ParachuteRepacked, seed.eventType);
            Assert.Equal(34.0, seed.ut);
            // ...and the type the seed carries is the one that puts the cap BACK, which is
            // what "stowed with cap" means at the transform level. Asserting the render
            // decision off the SEED (rather than restating the table) is the whole point:
            // it closes the loop from the optimizer's choice to the ghost's pose.
            Assert.True(GhostPlaybackLogic.TryResolveParachuteCapActive(
                seed.eventType, out bool capActive));
            Assert.True(capActive);
        }

        /// <summary>
        /// The CUT-terminal variant of the cell above, and the reason that one is not
        /// vacuous: same reducer, same emitter, same head prefix — only the last event
        /// differs — and the tail must render the OPPOSITE cap state. If the merge had
        /// collapsed the parachute family to a single inactive seed type, these two cells
        /// would disagree with each other rather than both passing.
        /// </summary>
        [Fact]
        public void SplitAtUT_HeadEndsCut_TipSeedsCut_AndRendersWithCapHidden()
        {
            var rec = MakeSimpleRecording(8.0, 53.0, "rec-chute-cut", midUT: 34.0);
            rec.PartEvents.Add(VisualEvent(10.0, PartEventType.ParachuteDeployed));
            rec.PartEvents.Add(VisualEvent(20.0, PartEventType.ParachuteCut));

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            PartEvent seed = Assert.Single(tip.PartEvents);
            Assert.Equal(PartEventType.ParachuteCut, seed.eventType);
            Assert.Equal(34.0, seed.ut);
            Assert.True(GhostPlaybackLogic.TryResolveParachuteCapActive(
                seed.eventType, out bool capActive));
            Assert.False(capActive);
        }

        /// <summary>
        /// A repack UNDOES a cut for seeding purposes, so the two must not both survive the
        /// reducer: the parachute family is latest-state-wins on ONE key, and a TIP holding
        /// both a Cut and a Repacked seed would apply them in list order and land on
        /// whichever happened to be last. Guards the family-9 collapse itself rather than
        /// the type carried out of it.
        /// </summary>
        [Fact]
        public void BuildTransientStateSeeds_CutThenRepacked_CollapsesToOneRepackedSeed()
        {
            var events = new List<PartEvent>
            {
                VisualEvent(10.0, PartEventType.ParachuteDeployed),
                VisualEvent(20.0, PartEventType.ParachuteCut),
                VisualEvent(25.0, PartEventType.ParachuteRepacked),
            };

            List<PartEvent> seeds = RecordingOptimizer.BuildTransientStateSeeds(events, 34.0);

            PartEvent seed = Assert.Single(seeds);
            Assert.Equal(PartEventType.ParachuteRepacked, seed.eventType);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("visualStatesOff=1"));
        }

        /// <summary>
        /// The other half of the composition: family-9 (parachute, main's ParachuteRepacked)
        /// and family-10 (robotics, this branch's servo poses) must keep DISTINCT ids, or the
        /// boundary dedupe would let a chute event at the split suppress a servo seed for the
        /// same part — the two families are keyed differently (parachutes pid-collapsed,
        /// robotics module-scoped), so a collision is silent rather than loud.
        /// </summary>
        [Fact]
        public void SplitAtUT_ParachuteAndRoboticSeeds_BothSurviveOnTheSamePart()
        {
            var rec = MakeSimpleRecording(8.0, 53.0, "rec-chute-and-servo", midUT: 34.0);
            rec.PartEvents.Add(VisualEvent(20.0, PartEventType.ParachuteRepacked));
            rec.PartEvents.Add(VisualEvent(25.0, PartEventType.RoboticPositionSample, value: 37.5f));

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            Assert.Equal(2, tip.PartEvents.Count);
            Assert.Contains(tip.PartEvents, e => e.eventType == PartEventType.ParachuteRepacked);
            PartEvent servo = Assert.Single(
                tip.PartEvents.Where(e => e.eventType == PartEventType.RoboticMotionStopped));
            Assert.Equal(37.5f, servo.value);
        }

        [Fact]
        public void BuildTransientStateSeeds_BlinkRateOnly_NoBlinkDisabledSeed()
        {
            // A bare LightBlinkRate synthesises an inactive blink state, but nothing in
            // the head span ever SAID blink was off. Emitting LightBlinkDisabled would
            // turn a snapshot-blinking lamp solid off the back of a rate change.
            var events = new List<PartEvent>
            {
                VisualEvent(20.0, PartEventType.LightBlinkRate, value: 2.5f),
            };

            List<PartEvent> seeds = RecordingOptimizer.BuildTransientStateSeeds(events, 34.0);

            Assert.Empty(seeds);
        }

        [Fact]
        public void BuildTransientStateSeeds_BlinkDisabledThenRate_SeedsDisabledWithTheRate()
        {
            // Once an explicit LightBlinkDisabled has been seen the state IS known, so a
            // later rate change keeps the disabled direction and carries the new rate.
            var events = new List<PartEvent>
            {
                VisualEvent(10.0, PartEventType.LightBlinkDisabled),
                VisualEvent(20.0, PartEventType.LightBlinkRate, value: 2.5f),
            };

            List<PartEvent> seeds = RecordingOptimizer.BuildTransientStateSeeds(events, 34.0);

            PartEvent seed = Assert.Single(seeds);
            Assert.Equal(PartEventType.LightBlinkDisabled, seed.eventType);
            Assert.Equal(2.5f, seed.value);
        }

        [Fact]
        public void BuildTransientStateSeeds_ThermalCold_StaysUnseeded()
        {
            // Heat is the one reversible family deliberately left active-only: cold is
            // what the spawn pass lays down anyway and the M1 parser reads no heat key,
            // so there is no stale baseline for a cold seed to correct.
            var events = new List<PartEvent>
            {
                VisualEvent(10.0, PartEventType.ThermalAnimationHot),
                VisualEvent(20.0, PartEventType.ThermalAnimationCold),
            };

            List<PartEvent> seeds = RecordingOptimizer.BuildTransientStateSeeds(events, 34.0);

            Assert.Empty(seeds);
        }

        [Fact]
        public void BuildTransientStateSeeds_InactiveSeedSuppressedByMatchingBoundaryEvent()
        {
            // The dedupe machinery already in place must swallow the inactive seed when
            // the TIP's own first event says the same thing at the same UT.
            var rec = MakeSimpleRecording(8.0, 53.0, "rec-gear-boundary", midUT: 34.0);
            rec.PartEvents.Add(VisualEvent(10.0, PartEventType.GearDeployed));
            rec.PartEvents.Add(VisualEvent(20.0, PartEventType.GearRetracted));
            rec.PartEvents.Add(VisualEvent(34.0, PartEventType.GearRetracted));

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            Assert.Single(tip.PartEvents);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("skippedExistingBoundary=1"));
        }

        [Fact]
        public void BuildTransientStateSeeds_InactiveSeedPrecedesTheTipsOwnLaterRedeploy()
        {
            // A TIP that re-deploys LATER in its own span still needs the retracted seed
            // at its start, and the seed must sit before the real event so the replay
            // walks retracted -> deployed rather than the reverse.
            var rec = MakeSimpleRecording(8.0, 53.0, "rec-gear-redeploy", midUT: 34.0);
            rec.PartEvents.Add(VisualEvent(10.0, PartEventType.GearDeployed));
            rec.PartEvents.Add(VisualEvent(20.0, PartEventType.GearRetracted));
            rec.PartEvents.Add(VisualEvent(40.0, PartEventType.GearDeployed));

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            Assert.Equal(2, tip.PartEvents.Count);
            Assert.Equal(PartEventType.GearRetracted, tip.PartEvents[0].eventType);
            Assert.Equal(34.0, tip.PartEvents[0].ut);
            Assert.Equal(PartEventType.GearDeployed, tip.PartEvents[1].eventType);
            Assert.Equal(40.0, tip.PartEvents[1].ut);
        }

        [Fact]
        public void BuildTransientStateSeeds_OpposingEventExactlyAtTheCutWinsInTheReducer()
        {
            // An event AT splitUT is inside the reducer's window (SplitSeedTimeEpsilon),
            // so the seed it produces already agrees with it — and the boundary dedupe
            // then drops the duplicate. The TIP keeps exactly the real event.
            var rec = MakeSimpleRecording(8.0, 53.0, "rec-gear-at-cut", midUT: 34.0);
            rec.PartEvents.Add(VisualEvent(10.0, PartEventType.GearDeployed));
            rec.PartEvents.Add(VisualEvent(20.0, PartEventType.GearRetracted));
            rec.PartEvents.Add(VisualEvent(34.0, PartEventType.GearDeployed));

            var tip = RecordingOptimizer.SplitAtUT(rec, 34.0);

            Assert.NotNull(tip);
            PartEvent only = Assert.Single(tip.PartEvents);
            Assert.Equal(PartEventType.GearDeployed, only.eventType);
            Assert.Equal(34.0, only.ut);
        }

        #endregion
    }
}
