using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class DebrisRelativeRecorderPolicyTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public DebrisRelativeRecorderPolicyTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void NormalizeParentAnchoredRelativeRecording_StaleRelativeTail_ClampsSectionExplicitEndAndFlatPoint()
        {
            var rec = MakeParentAnchoredDebris();
            rec.ExplicitEndUT = 140.0;
            rec.Points.Add(Point(100.0));
            rec.Points.Add(Point(110.0));
            rec.Points.Add(Point(140.0));
            rec.TrackSections.Add(RelativeSection(100.0, 140.0, 100.0, 110.0));

            var result = DebrisRelativeRecorderPolicy.NormalizeParentAnchoredRelativeRecording(
                rec,
                "unit-stale-tail");

            Assert.True(result.Mutated);
            Assert.Equal(1, result.ClampedSections);
            Assert.Equal(1, result.TrimmedFlatPoints);
            Assert.Equal(110.0, rec.TrackSections[0].endUT);
            Assert.Equal(110.0, rec.ExplicitEndUT);
            Assert.Equal(new[] { 100.0, 110.0 }, rec.Points.Select(p => p.ut).ToArray());
            Assert.True(rec.FilesDirty);
            Assert.Contains(logLines, line =>
                line.Contains("[Parsek][WARN][BgRecorder]")
                && line.Contains("ParentAnchoredDebrisTailNormalize")
                && line.Contains("context=unit-stale-tail")
                && line.Contains("clamped=1")
                && line.Contains("flatTrimmed=1"));
        }

        [Fact]
        public void NormalizeParentAnchoredRelativeRecording_SingleRelativeFrame_ClampsToSampleUT()
        {
            var rec = MakeParentAnchoredDebris();
            rec.ExplicitEndUT = 140.0;
            rec.Points.Add(Point(100.0));
            rec.TrackSections.Add(RelativeSection(100.0, 140.0, 100.0));

            var result = DebrisRelativeRecorderPolicy.NormalizeParentAnchoredRelativeRecording(
                rec,
                "unit-single-frame");

            Assert.True(result.Mutated);
            Assert.Equal(1, result.ClampedSections);
            Assert.Equal(100.0, rec.TrackSections[0].endUT);
            Assert.Equal(100.0, rec.ExplicitEndUT);
        }

        [Fact]
        public void NormalizeParentAnchoredRelativeRecording_EmptyRelativeSection_DropsAndClearsTailMetadata()
        {
            var rec = MakeParentAnchoredDebris();
            rec.ExplicitEndUT = 140.0;
            rec.Points.Add(Point(140.0));
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                environment = SegmentEnvironment.Atmospheric,
                source = TrackSectionSource.Background,
                startUT = 100.0,
                endUT = 140.0,
                anchorRecordingId = "parent-rec",
                frames = new List<TrajectoryPoint>(),
                absoluteFrames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>()
            });

            var result = DebrisRelativeRecorderPolicy.NormalizeParentAnchoredRelativeRecording(
                rec,
                "unit-empty-relative");

            Assert.True(result.Mutated);
            Assert.Equal(1, result.DroppedSections);
            Assert.Empty(rec.TrackSections);
            Assert.Empty(rec.Points);
            Assert.True(double.IsNaN(rec.ExplicitEndUT));
        }

        [Fact]
        public void NormalizeParentAnchoredRelativeRecording_RelativeCheckpoint_ClampsToCheckpointEndUT()
        {
            var rec = MakeParentAnchoredDebris();
            rec.ExplicitEndUT = 140.0;
            rec.Points.Add(Point(140.0));
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                environment = SegmentEnvironment.Atmospheric,
                source = TrackSectionSource.Background,
                startUT = 100.0,
                endUT = 140.0,
                anchorRecordingId = "parent-rec",
                frames = new List<TrajectoryPoint>(),
                absoluteFrames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        startUT = 100.0,
                        endUT = 120.0,
                        bodyName = "Kerbin",
                        isPredicted = false
                    }
                }
            });

            var result = DebrisRelativeRecorderPolicy.NormalizeParentAnchoredRelativeRecording(
                rec,
                "unit-checkpoint");

            Assert.True(result.Mutated);
            Assert.Equal(1, result.ClampedSections);
            Assert.Equal(120.0, rec.TrackSections[0].endUT);
            Assert.Equal(120.0, rec.ExplicitEndUT);
        }

        [Fact]
        public void NormalizeParentAnchoredRelativeRecording_LegacyDebris_DoesNotMutate()
        {
            var rec = MakeParentAnchoredDebris();
            rec.DebrisParentRecordingId = null;
            rec.ExplicitEndUT = 140.0;
            rec.TrackSections.Add(RelativeSection(100.0, 140.0, 100.0, 110.0));

            var result = DebrisRelativeRecorderPolicy.NormalizeParentAnchoredRelativeRecording(
                rec,
                "unit-legacy");

            Assert.False(result.Mutated);
            Assert.Equal(140.0, rec.TrackSections[0].endUT);
            Assert.Equal(140.0, rec.ExplicitEndUT);
        }

        [Fact]
        public void NormalizeParentAnchoredRelativeRecording_LaterAbsoluteBoundarySection_AllowsExplicitEndUT()
        {
            var rec = MakeParentAnchoredDebris();
            rec.ExplicitEndUT = 140.0;
            rec.Points.Add(Point(100.0));
            rec.Points.Add(Point(110.0));
            rec.Points.Add(Point(140.0));
            rec.TrackSections.Add(RelativeSection(100.0, 140.0, 100.0, 110.0));
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                environment = SegmentEnvironment.SurfaceStationary,
                source = TrackSectionSource.Background,
                startUT = 140.0,
                endUT = 140.0,
                frames = new List<TrajectoryPoint> { Point(140.0) },
                checkpoints = new List<OrbitSegment>()
            });

            var result = DebrisRelativeRecorderPolicy.NormalizeParentAnchoredRelativeRecording(
                rec,
                "unit-absolute-boundary");

            Assert.True(result.Mutated);
            Assert.Equal(1, result.ClampedSections);
            Assert.Equal(0, result.TrimmedFlatPoints);
            Assert.Equal(110.0, rec.TrackSections[0].endUT);
            Assert.Equal(140.0, rec.TrackSections[1].endUT);
            Assert.Equal(140.0, rec.ExplicitEndUT);
            Assert.Equal(new[] { 100.0, 110.0, 140.0 }, rec.Points.Select(p => p.ut).ToArray());
        }

        private static Recording MakeParentAnchoredDebris()
        {
            return new Recording
            {
                RecordingId = "debris-rec",
                VesselName = "Debris",
                IsDebris = true,
                DebrisParentRecordingId = "parent-rec",
                VesselPersistentId = 42u
            };
        }

        private static TrackSection RelativeSection(
            double startUT,
            double endUT,
            params double[] frameUTs)
        {
            var frames = frameUTs.Select(Point).ToList();
            return new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                environment = SegmentEnvironment.Atmospheric,
                source = TrackSectionSource.Background,
                startUT = startUT,
                endUT = endUT,
                anchorRecordingId = "parent-rec",
                frames = frames,
                absoluteFrames = frameUTs.Select(Point).ToList(),
                checkpoints = new List<OrbitSegment>()
            };
        }

        private static TrajectoryPoint Point(double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                bodyName = "Kerbin"
            };
        }
    }
}
