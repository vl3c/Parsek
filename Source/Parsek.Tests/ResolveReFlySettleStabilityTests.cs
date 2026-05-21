using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ResolveReFlySettleStabilityTests : System.IDisposable
    {
        public ResolveReFlySettleStabilityTests()
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
        public void True_DuringActiveSettleForSameRecording()
        {
            var focus = new FlightRecorder();
            focus.ActivateReFlyPostLoadSettleForTesting("session-1", "rec-focus");
            Patches.PhysicsFramePatch.ActiveRecorder = focus;
            var rec = new Recording { RecordingId = "rec-focus" };

            bool result = ParsekFlight.ResolveReFlySettleStabilityForTesting(
                rec, frame: 10, out string anchorRecordingId, out string reason);

            Assert.True(result);
            Assert.Equal("rec-focus", anchorRecordingId);
            Assert.Equal("settle-active", reason);
        }

        [Fact]
        public void True_DuringActiveSettleForDebrisParent()
        {
            var focus = new FlightRecorder();
            focus.ActivateReFlyPostLoadSettleForTesting("session-1", "rec-parent");
            Patches.PhysicsFramePatch.ActiveRecorder = focus;
            var debris = new Recording
            {
                RecordingId = "rec-debris",
                IsDebris = true,
                ParentAnchorRecordingId = "rec-parent"
            };

            bool result = ParsekFlight.ResolveReFlySettleStabilityForTesting(
                debris, frame: 10, out string anchorRecordingId, out string reason);

            Assert.True(result);
            Assert.Equal("rec-parent", anchorRecordingId);
            Assert.Equal("settle-active", reason);
        }

        [Fact]
        public void True_DuringBoundedClearHold()
        {
            ReFlySettleStabilityTracker.RecordSettleCleared("rec-focus", frame: 100);
            var rec = new Recording { RecordingId = "rec-focus" };

            Assert.True(ParsekFlight.ResolveReFlySettleStabilityForTesting(
                rec,
                frame: 100 + FlightRecorder.StabilitySettleClearHoldFrames,
                out string anchorRecordingId,
                out string reason));
            Assert.Equal("rec-focus", anchorRecordingId);
            Assert.Equal("clear-hold", reason);

            Assert.False(ParsekFlight.ResolveReFlySettleStabilityForTesting(
                rec,
                frame: 101 + FlightRecorder.StabilitySettleClearHoldFrames,
                out _,
                out _));
        }

        [Fact]
        public void True_WhenAnotherRecordingClearsBeforeFirstHoldExpires()
        {
            ReFlySettleStabilityTracker.RecordSettleCleared("rec-first", frame: 100);
            ReFlySettleStabilityTracker.RecordSettleCleared("rec-second", frame: 120);
            var first = new Recording { RecordingId = "rec-first" };

            bool result = ParsekFlight.ResolveReFlySettleStabilityForTesting(
                first,
                frame: 100 + FlightRecorder.StabilitySettleClearHoldFrames,
                out string anchorRecordingId,
                out string reason);

            Assert.True(result);
            Assert.Equal("rec-first", anchorRecordingId);
            Assert.Equal("clear-hold", reason);
        }

        [Fact]
        public void True_WhenShiftExtendsRecentClearHold()
        {
            ReFlySettleStabilityTracker.RecordSettleCleared("rec-focus", frame: 100);
            ReFlySettleStabilityTracker.RecordFloatingOriginShift(
                new Vector3d(1.0, 0.0, 0.0),
                new Vector3d(0.0, 2.0, 0.0),
                frame: 100 + FlightRecorder.StabilitySettleClearHoldFrames,
                realtimeSinceStartup: 1.0f);
            var rec = new Recording { RecordingId = "rec-focus" };

            bool result = ParsekFlight.ResolveReFlySettleStabilityForTesting(
                rec,
                frame: 100
                    + FlightRecorder.StabilitySettleClearHoldFrames
                    + FlightRecorder.StabilityExtensionFramesAfterShift,
                out string anchorRecordingId,
                out string reason);

            Assert.True(result);
            Assert.Equal("rec-focus", anchorRecordingId);
            Assert.Equal("extension-window", reason);

            Assert.False(ParsekFlight.ResolveReFlySettleStabilityForTesting(
                rec,
                frame: 101
                    + FlightRecorder.StabilitySettleClearHoldFrames
                    + FlightRecorder.StabilityExtensionFramesAfterShift,
                out _,
                out _));
        }

        [Fact]
        public void False_WhenShiftFiresAfterClearHoldWindow()
        {
            ReFlySettleStabilityTracker.RecordSettleCleared("rec-focus", frame: 100);
            ReFlySettleStabilityTracker.RecordFloatingOriginShift(
                new Vector3d(1.0, 0.0, 0.0),
                new Vector3d(0.0, 2.0, 0.0),
                frame: 101 + FlightRecorder.StabilitySettleClearHoldFrames,
                realtimeSinceStartup: 1.0f);
            var rec = new Recording { RecordingId = "rec-focus" };

            Assert.False(ParsekFlight.ResolveReFlySettleStabilityForTesting(
                rec,
                frame: 102 + FlightRecorder.StabilitySettleClearHoldFrames,
                out _,
                out _));
        }
    }
}
