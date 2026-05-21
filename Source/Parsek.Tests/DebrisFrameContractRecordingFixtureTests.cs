using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    public class DebrisFrameContractRecordingFixtureTests
    {
        [Fact]
        public void Fixture_ProvidesMixedDualSurfaceDebrisRecording()
        {
            var fixture = DebrisFrameContractRecordingFixture.Create();

            Assert.Equal(ReferenceFrame.Absolute, fixture.AbsoluteSection.referenceFrame);
            Assert.Equal(ReferenceFrame.Relative, fixture.RelativeSection.referenceFrame);
            Assert.NotEmpty(fixture.RelativeSection.frames);
            Assert.NotEmpty(fixture.RelativeSection.bodyFixedFrames);
            Assert.True(fixture.Debris.IsDebris);
            Assert.Equal(fixture.Parent.RecordingId, fixture.Debris.ParentAnchorRecordingId);
        }

        [Fact]
        public void Fixture_ProvidesLoopAnchoredParentVariant()
        {
            var fixture = DebrisFrameContractRecordingFixture.Create(loopAnchoredParent: true);

            Assert.NotEqual(0u, fixture.Parent.LoopAnchorVesselId);
            Assert.Equal(ReferenceFrame.Relative, fixture.Parent.TrackSections[0].referenceFrame);
            Assert.Equal(
                fixture.Parent.LoopAnchorVesselId,
                fixture.Parent.TrackSections[0].anchorVesselId);
        }
    }
}
