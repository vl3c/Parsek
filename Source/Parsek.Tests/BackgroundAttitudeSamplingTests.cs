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
                effectiveMotionMinSampleInterval: 2.0f,
                foregroundMinSampleInterval: 0.05f);

            Assert.Equal(0.05f, interval);
        }

        [Fact]
        public void ResolveBackgroundAttitudeMinSampleInterval_HighFidelityKeepsForegroundFloor()
        {
            float interval = BackgroundRecorder.ResolveBackgroundAttitudeMinSampleInterval(
                highFidelityActive: true,
                effectiveMotionMinSampleInterval: 0.5f,
                foregroundMinSampleInterval: 0.05f);

            Assert.Equal(0.05f, interval);
        }
    }
}
