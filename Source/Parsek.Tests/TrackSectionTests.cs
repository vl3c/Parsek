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
        public void SegmentEnvironment_ValuesAreContiguous()
        {
            var values = Enum.GetValues(typeof(SegmentEnvironment)).Cast<int>().OrderBy(v => v).ToList();
            Assert.Equal(5, values.Count);
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
