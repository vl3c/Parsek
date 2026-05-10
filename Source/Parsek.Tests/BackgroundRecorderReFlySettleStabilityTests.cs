using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class BackgroundRecorderReFlySettleStabilityTests : System.IDisposable
    {
        public BackgroundRecorderReFlySettleStabilityTests()
        {
            ReFlySettleStabilityTracker.Reset();
            Patches.PhysicsFramePatch.ActiveRecorder = null;
            FlightRecorder.FrameCountProviderForTesting = () => 10;
        }

        public void Dispose()
        {
            ReFlySettleStabilityTracker.Reset();
            Patches.PhysicsFramePatch.ActiveRecorder = null;
            FlightRecorder.FrameCountProviderForTesting = null;
        }

        [Fact]
        public void ShouldSuppressParentDebrisForReFlySettle_FalseWithoutParent()
        {
            Assert.False(BackgroundRecorder.ShouldSuppressParentDebrisForReFlySettle(null, frame: 10));
            Assert.False(BackgroundRecorder.ShouldSuppressParentDebrisForReFlySettle(
                new Recording { RecordingId = "rec-debris" },
                frame: 10));
        }

        [Fact]
        public void ShouldSuppressParentDebrisForReFlySettle_UsesSharedClearHoldPredicate()
        {
            ReFlySettleStabilityTracker.RecordSettleCleared("rec-parent", frame: 100);
            var debris = new Recording
            {
                RecordingId = "rec-debris",
                IsDebris = true,
                DebrisParentRecordingId = "rec-parent"
            };

            Assert.True(BackgroundRecorder.ShouldSuppressParentDebrisForReFlySettle(
                debris,
                frame: 100 + FlightRecorder.StabilitySettleClearHoldFrames));
            Assert.False(BackgroundRecorder.ShouldSuppressParentDebrisForReFlySettle(
                debris,
                frame: 101 + FlightRecorder.StabilitySettleClearHoldFrames));
        }

        [Fact]
        public void ShouldSuppressParentDebrisForReFlySettle_UsesSharedActiveSettlePredicate()
        {
            var focus = new FlightRecorder();
            focus.ActivateReFlyPostLoadSettleForTesting("session-1", "rec-parent");
            Patches.PhysicsFramePatch.ActiveRecorder = focus;
            var debris = new Recording
            {
                RecordingId = "rec-debris",
                IsDebris = true,
                DebrisParentRecordingId = "rec-parent"
            };

            Assert.True(BackgroundRecorder.ShouldSuppressParentDebrisForReFlySettle(
                debris,
                frame: 10));
        }
    }
}
