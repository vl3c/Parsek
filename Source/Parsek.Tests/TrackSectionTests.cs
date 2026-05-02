using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    public class TrackSectionTests
    {
        #region SegmentEnvironment enum values

        [Fact]
        public void SegmentEnvironment_Atmospheric_IsZero()
        {
            Assert.Equal(0, (int)SegmentEnvironment.Atmospheric);
        }

        [Fact]
        public void SegmentEnvironment_ExoPropulsive_IsOne()
        {
            Assert.Equal(1, (int)SegmentEnvironment.ExoPropulsive);
        }

        [Fact]
        public void SegmentEnvironment_ExoBallistic_IsTwo()
        {
            Assert.Equal(2, (int)SegmentEnvironment.ExoBallistic);
        }

        [Fact]
        public void SegmentEnvironment_SurfaceMobile_IsThree()
        {
            Assert.Equal(3, (int)SegmentEnvironment.SurfaceMobile);
        }

        [Fact]
        public void SegmentEnvironment_SurfaceStationary_IsFour()
        {
            Assert.Equal(4, (int)SegmentEnvironment.SurfaceStationary);
        }

        [Fact]
        public void SegmentEnvironment_Approach_IsFive()
        {
            Assert.Equal(5, (int)SegmentEnvironment.Approach);
        }

        [Fact]
        public void SegmentEnvironment_ValuesAreContiguous()
        {
            var values = Enum.GetValues(typeof(SegmentEnvironment)).Cast<int>().OrderBy(v => v).ToList();
            Assert.Equal(6, values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                Assert.Equal(i, values[i]);
            }
        }

        #endregion

        #region ReferenceFrame enum values

        [Fact]
        public void ReferenceFrame_Absolute_IsZero()
        {
            Assert.Equal(0, (int)ReferenceFrame.Absolute);
        }

        [Fact]
        public void ReferenceFrame_Relative_IsOne()
        {
            Assert.Equal(1, (int)ReferenceFrame.Relative);
        }

        [Fact]
        public void ReferenceFrame_OrbitalCheckpoint_IsTwo()
        {
            Assert.Equal(2, (int)ReferenceFrame.OrbitalCheckpoint);
        }

        [Fact]
        public void ReferenceFrame_ValuesAreContiguous()
        {
            var values = Enum.GetValues(typeof(ReferenceFrame)).Cast<int>().OrderBy(v => v).ToList();
            Assert.Equal(3, values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                Assert.Equal(i, values[i]);
            }
        }

        #endregion

        #region TrackSection default state

        [Fact]
        public void TrackSection_Default_EnvironmentIsAtmospheric()
        {
            var section = new TrackSection();
            Assert.Equal(SegmentEnvironment.Atmospheric, section.environment);
        }

        [Fact]
        public void TrackSection_Default_ReferenceFrameIsAbsolute()
        {
            var section = new TrackSection();
            Assert.Equal(ReferenceFrame.Absolute, section.referenceFrame);
        }

        [Fact]
        public void TrackSection_Default_AnchorVesselIdIsZero()
        {
            var section = new TrackSection();
            Assert.Equal(0u, section.anchorVesselId);
        }

        [Fact]
        public void TrackSection_Default_FramesIsNull()
        {
            var section = new TrackSection();
            Assert.Null(section.frames);
        }

        [Fact]
        public void TrackSection_Default_CheckpointsIsNull()
        {
            var section = new TrackSection();
            Assert.Null(section.checkpoints);
        }

        [Fact]
        public void TrackSection_Default_SampleRateHzIsZero()
        {
            var section = new TrackSection();
            Assert.Equal(0f, section.sampleRateHz);
        }

        [Fact]
        public void TrackSection_Default_SourceIsActive()
        {
            var section = new TrackSection();
            Assert.Equal(TrackSectionSource.Active, section.source);
        }

        #endregion

        #region OrbitalCheckpoint renderability

        [Fact]
        public void HasRenderableCheckpointTrackSection_WithFrames_ReturnsTrue()
        {
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 10.0 }
                }
            };

            Assert.True(ParsekFlight.HasRenderableCheckpointTrackSection(section));
        }

        [Fact]
        public void HasRenderableCheckpointTrackSection_WithCheckpointsOnly_ReturnsTrue()
        {
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                checkpoints = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 10.0, endUT = 20.0 }
                }
            };

            Assert.True(ParsekFlight.HasRenderableCheckpointTrackSection(section));
        }

        [Fact]
        public void HasRenderableCheckpointTrackSection_WithNoPayload_ReturnsFalse()
        {
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint
            };

            Assert.False(ParsekFlight.HasRenderableCheckpointTrackSection(section));
        }

        [Fact]
        public void HasRenderableCheckpointTrackSection_AbsoluteWithFrames_ReturnsFalse()
        {
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 10.0 }
                }
            };

            Assert.False(ParsekFlight.HasRenderableCheckpointTrackSection(section));
        }

        #endregion

        #region Re-Fly dense absolute playback frames

        [Fact]
        public void ShouldUseDenseReFlyAbsolutePlaybackFrames_DenseAbsoluteSectionBracket_ReturnsTrue()
        {
            var sectionFrames = new List<TrajectoryPoint>
            {
                Point(100.0),
                Point(106.0),
                Point(109.0),
            };
            var sections = new List<TrackSection>
            {
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.Absolute,
                    startUT = 100.0,
                    endUT = 110.0,
                    frames = sectionFrames,
                },
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.Relative,
                    startUT = 110.0,
                    endUT = 120.0,
                    frames = new List<TrajectoryPoint> { Point(110.0), Point(120.0) },
                },
            };
            var flatPoints = new List<TrajectoryPoint>();
            for (int i = 0; i < 10; i++)
                flatPoints.Add(Point(100.0 + i));
            flatPoints.Add(Point(110.0));
            flatPoints.Add(Point(111.0));

            bool result = ParsekFlight.ShouldUseDenseReFlyAbsolutePlaybackFrames(
                sections,
                0,
                sections[0],
                sectionFrames,
                flatPoints,
                playbackUT: 105.5,
                out int denseFrameCount,
                out double sectionBracketSeconds,
                out double denseBracketSeconds,
                out string reason);

            Assert.True(result);
            Assert.Equal("dense-bracket-tighter", reason);
            Assert.Equal(10, denseFrameCount);
            Assert.Equal(6.0, sectionBracketSeconds);
            Assert.Equal(1.0, denseBracketSeconds);
        }

        [Fact]
        public void ShouldUseDenseReFlyAbsolutePlaybackFrames_RelativeSection_ReturnsFalse()
        {
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 110.0,
                frames = new List<TrajectoryPoint> { Point(100.0), Point(110.0) },
            };

            bool result = ParsekFlight.ShouldUseDenseReFlyAbsolutePlaybackFrames(
                new List<TrackSection> { section },
                0,
                section,
                section.frames,
                new List<TrajectoryPoint> { Point(100.0), Point(105.0), Point(110.0) },
                playbackUT: 105.0,
                out _,
                out _,
                out _,
                out string reason);

            Assert.False(result);
            Assert.Equal("not-absolute", reason);
        }

        [Fact]
        public void ShouldUseDenseReFlyAbsolutePlaybackFrames_DenseBracketNotTighter_ReturnsFalse()
        {
            var sectionFrames = new List<TrajectoryPoint>
            {
                Point(100.0),
                Point(105.0),
                Point(110.0),
            };
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 110.0,
                frames = sectionFrames,
            };
            var flatPoints = new List<TrajectoryPoint>
            {
                Point(100.0),
                Point(105.0),
                Point(110.0),
            };

            bool result = ParsekFlight.ShouldUseDenseReFlyAbsolutePlaybackFrames(
                new List<TrackSection> { section },
                0,
                section,
                sectionFrames,
                flatPoints,
                playbackUT: 102.5,
                out int denseFrameCount,
                out double sectionBracketSeconds,
                out double denseBracketSeconds,
                out string reason);

            Assert.False(result);
            Assert.Equal("dense-not-more-populated", reason);
            Assert.Equal(3, denseFrameCount);
            Assert.True(double.IsNaN(sectionBracketSeconds));
            Assert.True(double.IsNaN(denseBracketSeconds));
        }

        [Fact]
        public void ShouldUseDenseReFlyAbsolutePlaybackFrames_ExcludesBoundaryPointsOwnedByNextSection()
        {
            var sectionFrames = new List<TrajectoryPoint>
            {
                Point(100.0),
                Point(106.0),
            };
            var sections = new List<TrackSection>
            {
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.Absolute,
                    startUT = 100.0,
                    endUT = 110.0,
                    frames = sectionFrames,
                },
                new TrackSection
                {
                    referenceFrame = ReferenceFrame.Relative,
                    startUT = 110.0,
                    endUT = 120.0,
                    frames = new List<TrajectoryPoint> { Point(110.0), Point(120.0) },
                },
            };
            var flatPoints = new List<TrajectoryPoint>
            {
                Point(100.0),
                Point(103.0),
                Point(106.0),
                Point(110.0),
                Point(111.0),
            };

            bool result = ParsekFlight.ShouldUseDenseReFlyAbsolutePlaybackFrames(
                sections,
                0,
                sections[0],
                sectionFrames,
                flatPoints,
                playbackUT: 104.0,
                out int denseFrameCount,
                out _,
                out _,
                out string reason);

            Assert.True(result);
            Assert.Equal("dense-bracket-tighter", reason);
            Assert.Equal(3, denseFrameCount);
        }

        private static TrajectoryPoint Point(double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                bodyName = "Kerbin",
            };
        }

        #endregion

        #region ToString

        [Fact]
        public void TrackSection_ToString_ProducesReadableOutput()
        {
            var section = new TrackSection
            {
                environment = SegmentEnvironment.ExoPropulsive,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 1000.5,
                endUT = 2000.75,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 1000.5 },
                    new TrajectoryPoint { ut = 1500.0 },
                    new TrajectoryPoint { ut = 2000.75 }
                },
                source = TrackSectionSource.Background
            };

            var result = section.ToString();

            Assert.Contains("env=ExoPropulsive", result);
            Assert.Contains("ref=Absolute", result);
            Assert.Contains("1000.50", result);
            Assert.Contains("2000.75", result);
            Assert.Contains("frames=3", result);
            Assert.Contains("checkpoints=0", result);
            Assert.Contains("src=Background", result);
        }

        [Fact]
        public void TrackSection_ToString_NullLists_ReportsZeroCounts()
        {
            var section = new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 500.0,
                endUT = 600.0
            };

            // frames and checkpoints are null (struct default)
            var result = section.ToString();

            Assert.Contains("frames=0", result);
            Assert.Contains("checkpoints=0", result);
            Assert.Contains("env=ExoBallistic", result);
            Assert.Contains("ref=OrbitalCheckpoint", result);
        }

        [Fact]
        public void TrackSection_ToString_WithCheckpoints_ReportsCheckpointCount()
        {
            var section = new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 100.0,
                endUT = 200.0,
                checkpoints = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 100, endUT = 150 },
                    new OrbitSegment { startUT = 150, endUT = 200 }
                }
            };

            var result = section.ToString();

            Assert.Contains("checkpoints=2", result);
            Assert.Contains("frames=0", result);
        }

        #endregion
    }
}
