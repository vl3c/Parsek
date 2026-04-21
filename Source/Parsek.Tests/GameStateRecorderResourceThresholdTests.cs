using Xunit;

namespace Parsek.Tests
{
    public class GameStateRecorderResourceThresholdTests
    {
        [Theory]
        [InlineData(0.9999995f)]
        [InlineData(-0.9999995f)]
        public void IsReputationDeltaBelowThreshold_StockRoundedOnePointZero_ReturnsFalse(float delta)
        {
            Assert.False(GameStateRecorder.IsReputationDeltaBelowThreshold(delta));
        }

        [Fact]
        public void IsReputationDeltaBelowThreshold_CumulativeFloatSubtractionShape_ReturnsFalse()
        {
            float oldReputation = 127.001f;
            float newReputation = oldReputation + 0.9999995f;
            float delta = newReputation - oldReputation;

            Assert.True(delta < 1.0f);
            Assert.False(GameStateRecorder.IsReputationDeltaBelowThreshold(delta));
        }

        [Theory]
        [InlineData(0.998f)]
        [InlineData(-0.998f)]
        public void IsReputationDeltaBelowThreshold_ClearSubThresholdNoise_ReturnsTrue(float delta)
        {
            Assert.True(GameStateRecorder.IsReputationDeltaBelowThreshold(delta));
        }

        [Theory]
        [InlineData(0.6)]
        [InlineData(-0.6)]
        public void IsScienceDeltaBelowThreshold_RealSubOnePointScience_ReturnsFalse(double delta)
        {
            Assert.False(GameStateRecorder.IsScienceDeltaBelowThreshold(delta));
        }

        [Theory]
        [InlineData(0.0005)]
        [InlineData(-0.0005)]
        public void IsScienceDeltaBelowThreshold_ClearNoise_ReturnsTrue(double delta)
        {
            Assert.True(GameStateRecorder.IsScienceDeltaBelowThreshold(delta));
        }

        [Fact]
        public void ShouldUseRecentScienceChangeCapture_MatchingDeltaWithinWindow_ReturnsTrue()
        {
            var capture = new GameStateRecorder.RecentScienceChangeCapture
            {
                Ut = 88.7,
                ReasonKey = "ScienceTransmission",
                Delta = 1.512f,
                RecordingId = "",
                Valid = true
            };

            Assert.True(GameStateRecorder.ShouldUseRecentScienceChangeCapture(capture, 1.5f, 88.8, ""));
        }

        [Fact]
        public void ShouldUseRecentScienceChangeCapture_ExpiredOrMismatchedCapture_ReturnsFalse()
        {
            var capture = new GameStateRecorder.RecentScienceChangeCapture
            {
                Ut = 88.7,
                ReasonKey = "ScienceTransmission",
                Delta = 1.512f,
                RecordingId = "",
                Valid = true
            };

            Assert.False(GameStateRecorder.ShouldUseRecentScienceChangeCapture(capture, 0.6f, 88.8, ""));
            Assert.False(GameStateRecorder.ShouldUseRecentScienceChangeCapture(capture, 1.5f, 94.0, ""));
        }

        [Theory]
        [InlineData("ScienceTransmission", true)]
        [InlineData("VesselRecovery", true)]
        [InlineData("Progression", false)]
        [InlineData("ContractReward", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsScienceSubjectReasonKey_OnlySubjectScienceReasonsReturnTrue(
            string reasonKey,
            bool expected)
        {
            Assert.Equal(expected, GameStateRecorder.IsScienceSubjectReasonKey(reasonKey));
        }

        [Fact]
        public void ShouldUseRecentScienceChangeCapture_UnrelatedPositiveScienceReason_ReturnsFalse()
        {
            var capture = new GameStateRecorder.RecentScienceChangeCapture
            {
                Ut = 88.7,
                ReasonKey = "Progression",
                Delta = 1.5f,
                RecordingId = "",
                Valid = true
            };

            Assert.False(GameStateRecorder.ShouldUseRecentScienceChangeCapture(capture, 1.5f, 88.8, ""));
        }

        [Fact]
        public void ShouldUseRecentScienceChangeCapture_OtherRecording_ReturnsFalse()
        {
            var capture = new GameStateRecorder.RecentScienceChangeCapture
            {
                Ut = 88.7,
                ReasonKey = "VesselRecovery",
                Delta = 1.5f,
                RecordingId = "rec-old",
                Valid = true
            };

            Assert.False(GameStateRecorder.ShouldUseRecentScienceChangeCapture(
                capture,
                1.5f,
                88.8,
                "rec-new"));
        }
    }
}
