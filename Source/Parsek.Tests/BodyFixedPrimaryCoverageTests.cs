using System.Collections.Generic;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class BodyFixedPrimaryCoverageTests
    {
        [Fact]
        public void CoversPlaybackUT_RelativeTwoFrameCoverage_ReturnsTrue()
        {
            TrackSection section = RelativeSectionWithBodyFixedFrames(100.0, 110.0);

            bool covers = ParsekFlight.BodyFixedPrimaryCoversPlaybackUT(
                section,
                105.0,
                out double firstUT,
                out double lastUT);

            Assert.True(covers);
            Assert.Equal(100.0, firstUT);
            Assert.Equal(110.0, lastUT);
        }

        [Fact]
        public void CoversPlaybackUT_SingleFrame_ReturnsFalse()
        {
            var section = new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                bodyFixedFrames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 105.0 },
                },
            };

            Assert.False(ParsekFlight.BodyFixedPrimaryCoversPlaybackUT(
                section,
                105.0,
                out _,
                out _));
        }

        [Theory]
        [InlineData(99.0)]
        [InlineData(111.0)]
        public void CoversPlaybackUT_OutOfBodyFixedRange_ReturnsFalse(double playbackUT)
        {
            TrackSection section = RelativeSectionWithBodyFixedFrames(100.0, 110.0);
            section.startUT = 90.0;
            section.endUT = 120.0;

            Assert.False(ParsekFlight.BodyFixedPrimaryCoversPlaybackUT(
                section,
                playbackUT,
                out _,
                out _));
        }

        [Fact]
        public void CoversPlaybackUT_AbsoluteSection_ReturnsFalse()
        {
            TrackSection section = RelativeSectionWithBodyFixedFrames(100.0, 110.0);
            section.referenceFrame = ReferenceFrame.Absolute;

            Assert.False(ParsekFlight.BodyFixedPrimaryCoversPlaybackUT(
                section,
                105.0,
                out _,
                out _));
        }

        [Fact]
        public void CoversPlaybackUT_V13DualSurfaceFixture_CoversRelativeSection()
        {
            var fixture = DebrisFrameContractRecordingFixture.Create();

            bool covers = ParsekFlight.BodyFixedPrimaryCoversPlaybackUT(
                fixture.RelativeSection,
                playbackUT: 15.0,
                out double firstUT,
                out double lastUT);

            Assert.True(covers);
            Assert.Equal(fixture.RelativeSection.bodyFixedFrames[0].ut, firstUT);
            Assert.Equal(
                fixture.RelativeSection.bodyFixedFrames[fixture.RelativeSection.bodyFixedFrames.Count - 1].ut,
                lastUT);
        }

        [Fact]
        public void CoversPlaybackUT_V13DualSurfaceFixture_AbsoluteSectionRejected()
        {
            var fixture = DebrisFrameContractRecordingFixture.Create();

            // Absolute section has no bodyFixedFrames — the gate must reject it.
            Assert.False(ParsekFlight.BodyFixedPrimaryCoversPlaybackUT(
                fixture.AbsoluteSection,
                playbackUT: 5.0,
                out _,
                out _));
        }

        private static TrackSection RelativeSectionWithBodyFixedFrames(
            double firstUT,
            double lastUT)
        {
            return new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = firstUT,
                endUT = lastUT,
                bodyFixedFrames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = firstUT },
                    new TrajectoryPoint { ut = lastUT },
                },
            };
        }
    }
}
