using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class FastForwardTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public FastForwardTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Recording MakeFutureRecording()
        {
            var rec = new Recording { VesselName = "TestVessel" };
            // Points with UTs far in the future (won't collide with Planetarium default of 0)
            rec.Points.Add(new TrajectoryPoint { ut = 999999 });
            rec.Points.Add(new TrajectoryPoint { ut = 1000099 });
            return rec;
        }

        private List<TrajectoryPoint> MakePoints(int count, double startUT = 100)
        {
            var points = new List<TrajectoryPoint>();
            for (int i = 0; i < count; i++)
            {
                points.Add(new TrajectoryPoint
                {
                    ut = startUT + i * 10,
                    latitude = 0, longitude = 0, altitude = 100,
                    velocity = new Vector3(10, 50, 0)
                });
            }
            return points;
        }

        [Fact]
        public void CanFastForward_AlreadyRewinding_ReturnsFalse()
        {
            var rec = MakeFutureRecording();
            RewindContext.BeginRewind(0, default(BudgetSummary), 0, 0, 0);
            string reason;
            Assert.False(RecordingStore.CanFastForward(rec, out reason, isRecording: false));
            Assert.Equal("Rewind already in progress", reason);
            // Bug #117: blocked-path VERBOSE logs removed (per-frame UI spam).
            // Reason is conveyed via the out parameter, not log output.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[RecordingStore]") && l.Contains("CanFastForward") && l.Contains("blocked"));
        }

        [Fact]
        public void CanFastForward_NullRecording_ReturnsFalse()
        {
            string reason;
            Assert.False(RecordingStore.CanFastForward(null, out reason, isRecording: false));
            Assert.Equal("Recording not available", reason);
        }

        [Fact]
        public void CanFastForward_EmptyPoints_ReturnsFalse()
        {
            var rec = new Recording { VesselName = "Empty" };
            string reason;
            Assert.False(RecordingStore.CanFastForward(rec, out reason, isRecording: false));
            Assert.Equal("Recording not available", reason);
        }

        [Fact]
        public void CanFastForward_IsRecording_ReturnsFalse()
        {
            var rec = MakeFutureRecording();
            string reason;
            Assert.False(RecordingStore.CanFastForward(rec, out reason, isRecording: true));
            Assert.Equal("Stop recording before fast-forwarding", reason);
        }

        [Fact]
        public void CanFastForward_HasPending_ReturnsFalse()
        {
            var rec = MakeFutureRecording();
            RecordingStore.StashPending(MakePoints(3), "PendingVessel");
            Assert.True(RecordingStore.HasPending);

            string reason;
            Assert.False(RecordingStore.CanFastForward(rec, out reason, isRecording: false));
            Assert.Equal("Merge or discard pending recording first", reason);
        }

        [Fact]
        public void CanFastForward_HasPendingTree_ReturnsFalse()
        {
            var rec = MakeFutureRecording();
            RecordingStore.StashPendingTree(new RecordingTree());
            Assert.True(RecordingStore.HasPendingTree);

            string reason;
            Assert.False(RecordingStore.CanFastForward(rec, out reason, isRecording: false));
            Assert.Equal("Merge or discard pending tree first", reason);
        }

        [Fact]
        public void CanFastForward_NoSaveFile_PassesPreRuntimeGuards()
        {
            // Key difference from CanRewind: FF does NOT require a save file.
            // Verify it passes all non-runtime guards (IsRewinding, null, isRecording,
            // HasPending, HasPendingTree). The Planetarium.GetUniversalTime() timing
            // check requires KSP runtime and can't be tested here.
            var rec = MakeFutureRecording();
            Assert.Null(rec.RewindSaveFileName);
            Assert.False(RewindContext.IsRewinding);
            Assert.False(RecordingStore.HasPending);
            Assert.False(RecordingStore.HasPendingTree);
            // If we get past all testable guards, the next call would be
            // Planetarium.GetUniversalTime() which throws in unit tests.
            // The fact that it throws (not returns false) proves all prior guards passed.
            string reason;
            Assert.Throws<System.NullReferenceException>(() =>
                RecordingStore.CanFastForward(rec, out reason, isRecording: false));
        }
    }
}
