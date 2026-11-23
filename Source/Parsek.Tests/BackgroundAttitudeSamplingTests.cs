using Parsek;
using Xunit;

namespace Parsek.Tests
{
    public class BackgroundAttitudeSamplingTests
    {
        [Fact]
        public void ResolveBackgroundAttitudeMinSampleInterval_NormalSamplingUsesForegroundFloor()
        {
            float interval = BackgroundRecorder.ResolveBackgroundAttitudeMinSampleInterval(
                highFidelityActive: false,
                beyondRange: false,
                effectiveMotionMinSampleInterval: 2.0f,
                foregroundMinSampleInterval: 0.05f);

            Assert.Equal(0.05f, interval);
        }

        [Fact]
        public void ResolveBackgroundAttitudeMinSampleInterval_HighFidelityKeepsMotionFloorWhenMoreAggressive()
        {
            float interval = BackgroundRecorder.ResolveBackgroundAttitudeMinSampleInterval(
                highFidelityActive: true,
                beyondRange: false,
                effectiveMotionMinSampleInterval: 0.02f,
                foregroundMinSampleInterval: 0.05f);

            Assert.Equal(0.02f, interval);
        }

        [Fact]
        public void ResolveBackgroundAttitudeMinSampleInterval_BeyondRange_IsTheMotionFloor()
        {
            // A tumbling stage turns past the attitude threshold every frame: with the
            // foreground floor it would be sampled at the close-range rate all the way down.
            float interval = BackgroundRecorder.ResolveBackgroundAttitudeMinSampleInterval(
                highFidelityActive: false,
                beyondRange: true,
                effectiveMotionMinSampleInterval: 1.0f,
                foregroundMinSampleInterval: 0.2f);

            Assert.Equal(1.0f, interval);
        }

        [Fact]
        public void ResolveBackgroundAttitudeMinSampleInterval_BeyondRangeInHighFidelity_KeepsForegroundFloor()
        {
            float interval = BackgroundRecorder.ResolveBackgroundAttitudeMinSampleInterval(
                highFidelityActive: true,
                beyondRange: true,
                effectiveMotionMinSampleInterval: 1.0f,
                foregroundMinSampleInterval: 0.2f);

            Assert.Equal(0.2f, interval);
        }
    }
}
