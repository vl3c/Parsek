using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the held-ghost-until-spawn logic (#96).
    /// DecideHeldGhostAction is a pure static method: no Unity, no side effects.
    /// </summary>
    public class HeldGhostSpawnTests
    {
        private static Recording MakeRecording(string id, bool spawned = false)
        {
            return new Recording
            {
                RecordingId = id,
                VesselSpawned = spawned,
            };
        }

        #region DecideHeldGhostAction

        [Fact]
        public void DecideAction_NegativeIndex_ReturnsInvalidIndex()
        {
            var committed = new List<Recording> { MakeRecording("rec-1") };
            var info = new HeldGhostInfo { holdStartTime = 0f, recordingId = "rec-1" };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                -1, info, committed, currentTime: 1f, timeoutSeconds: 5f);

            Assert.Equal(HeldGhostAction.InvalidIndex, action);
        }

        [Fact]
        public void DecideAction_IndexBeyondList_ReturnsInvalidIndex()
        {
            var committed = new List<Recording> { MakeRecording("rec-1") };
            var info = new HeldGhostInfo { holdStartTime = 0f, recordingId = "rec-1" };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                5, info, committed, currentTime: 1f, timeoutSeconds: 5f);

            Assert.Equal(HeldGhostAction.InvalidIndex, action);
        }

        [Fact]
        public void DecideAction_RecordingIdMismatch_ReturnsInvalidIndex()
        {
            var committed = new List<Recording> { MakeRecording("rec-2") };
            var info = new HeldGhostInfo { holdStartTime = 0f, recordingId = "rec-1" };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                0, info, committed, currentTime: 1f, timeoutSeconds: 5f);

            Assert.Equal(HeldGhostAction.InvalidIndex, action);
        }

        [Fact]
        public void DecideAction_AlreadySpawned_ReturnsReleaseSpawned()
        {
            var committed = new List<Recording> { MakeRecording("rec-1", spawned: true) };
            var info = new HeldGhostInfo { holdStartTime = 0f, recordingId = "rec-1" };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                0, info, committed, currentTime: 1f, timeoutSeconds: 5f);

            Assert.Equal(HeldGhostAction.ReleaseSpawned, action);
        }

        [Fact]
        public void DecideAction_SupersededByRelation_ReturnsReleaseSuperseded()
        {
            var committed = new List<Recording> { MakeRecording("rec-1") };
            var info = new HeldGhostInfo { holdStartTime = 0f, recordingId = "rec-1" };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                0,
                info,
                committed,
                currentTime: 1f,
                timeoutSeconds: 5f,
                relationSupersededIds: new HashSet<string> { "rec-1" });

            Assert.Equal(HeldGhostAction.ReleaseSupersededByRelation, action);
        }

        [Fact]
        public void DecideAction_Timeout_ReturnsTimeout()
        {
            var committed = new List<Recording> { MakeRecording("rec-1") };
            var info = new HeldGhostInfo { holdStartTime = 0f, recordingId = "rec-1" };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                0, info, committed, currentTime: 5f, timeoutSeconds: 5f);

            Assert.Equal(HeldGhostAction.Timeout, action);
        }

        [Fact]
        public void DecideAction_PastTimeout_ReturnsTimeout()
        {
            var committed = new List<Recording> { MakeRecording("rec-1") };
            var info = new HeldGhostInfo { holdStartTime = 0f, recordingId = "rec-1" };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                0, info, committed, currentTime: 10f, timeoutSeconds: 5f);

            Assert.Equal(HeldGhostAction.Timeout, action);
        }

        [Fact]
        public void DecideAction_NotSpawnedNotTimedOut_ReturnsRetrySpawn()
        {
            var committed = new List<Recording> { MakeRecording("rec-1") };
            var info = new HeldGhostInfo { holdStartTime = 0f, recordingId = "rec-1" };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                0, info, committed, currentTime: 2f, timeoutSeconds: 5f);

            Assert.Equal(HeldGhostAction.RetrySpawn, action);
        }

        [Fact]
        public void DecideAction_JustBeforeTimeout_ReturnsRetrySpawn()
        {
            var committed = new List<Recording> { MakeRecording("rec-1") };
            var info = new HeldGhostInfo { holdStartTime = 0f, recordingId = "rec-1" };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                0, info, committed, currentTime: 4.999f, timeoutSeconds: 5f);

            Assert.Equal(HeldGhostAction.RetrySpawn, action);
        }

        [Fact]
        public void DecideAction_SpawnedTakesPriorityOverTimeout()
        {
            // Even if timeout is exceeded, if already spawned, return ReleaseSpawned
            var committed = new List<Recording> { MakeRecording("rec-1", spawned: true) };
            var info = new HeldGhostInfo { holdStartTime = 0f, recordingId = "rec-1" };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                0, info, committed, currentTime: 100f, timeoutSeconds: 5f);

            Assert.Equal(HeldGhostAction.ReleaseSpawned, action);
        }

        [Fact]
        public void DecideAction_MultipleRecordings_CorrectIndexLookup()
        {
            var committed = new List<Recording>
            {
                MakeRecording("rec-0"),
                MakeRecording("rec-1"),
                MakeRecording("rec-2", spawned: true),
            };
            var info = new HeldGhostInfo { holdStartTime = 0f, recordingId = "rec-2" };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                2, info, committed, currentTime: 1f, timeoutSeconds: 5f);

            Assert.Equal(HeldGhostAction.ReleaseSpawned, action);
        }

        [Fact]
        public void DecideAction_MultipleRecordings_IndexMismatchAfterDelete()
        {
            // After a recording is deleted, indices shift. The held ghost's index
            // may now point to a different recording.
            var committed = new List<Recording>
            {
                MakeRecording("rec-0"),
                MakeRecording("rec-2"),  // was at index 2, now at index 1 after delete
            };
            var info = new HeldGhostInfo { holdStartTime = 0f, recordingId = "rec-1" };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                1, info, committed, currentTime: 1f, timeoutSeconds: 5f);

            Assert.Equal(HeldGhostAction.InvalidIndex, action);
        }

        [Fact]
        public void DecideAction_ZeroTimeout_ImmediateTimeout()
        {
            var committed = new List<Recording> { MakeRecording("rec-1") };
            var info = new HeldGhostInfo { holdStartTime = 0f, recordingId = "rec-1" };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                0, info, committed, currentTime: 0f, timeoutSeconds: 0f);

            Assert.Equal(HeldGhostAction.Timeout, action);
        }

        [Fact]
        public void DecideAction_NonZeroHoldStartTime()
        {
            var committed = new List<Recording> { MakeRecording("rec-1") };
            var info = new HeldGhostInfo { holdStartTime = 100f, recordingId = "rec-1" };

            // 2 seconds elapsed, not timed out
            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                0, info, committed, currentTime: 102f, timeoutSeconds: 5f);
            Assert.Equal(HeldGhostAction.RetrySpawn, action);

            // 5 seconds elapsed, timed out
            action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                0, info, committed, currentTime: 105f, timeoutSeconds: 5f);
            Assert.Equal(HeldGhostAction.Timeout, action);
        }

        [Fact]
        public void DecideAction_RecentRetry_ReturnsHold()
        {
            // When last retry was too recent (< retryIntervalSeconds), return Hold
            var committed = new List<Recording> { MakeRecording("rec-1") };
            var info = new HeldGhostInfo
            {
                holdStartTime = 0f,
                lastRetryTime = 1.5f,
                recordingId = "rec-1"
            };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                0, info, committed, currentTime: 2.0f, timeoutSeconds: 5f,
                retryIntervalSeconds: 1.0f);

            Assert.Equal(HeldGhostAction.Hold, action);
        }

        [Fact]
        public void DecideAction_RetryIntervalElapsed_ReturnsRetrySpawn()
        {
            // When retry interval has elapsed, allow retry
            var committed = new List<Recording> { MakeRecording("rec-1") };
            var info = new HeldGhostInfo
            {
                holdStartTime = 0f,
                lastRetryTime = 1.0f,
                recordingId = "rec-1"
            };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                0, info, committed, currentTime: 2.5f, timeoutSeconds: 5f,
                retryIntervalSeconds: 1.0f);

            Assert.Equal(HeldGhostAction.RetrySpawn, action);
        }

        [Fact]
        public void DecideAction_FirstRetry_NoLastRetryTime_ReturnsRetrySpawn()
        {
            // First retry: lastRetryTime defaults to 0, so sinceLast >= interval
            var committed = new List<Recording> { MakeRecording("rec-1") };
            var info = new HeldGhostInfo
            {
                holdStartTime = 0f,
                lastRetryTime = 0f,
                recordingId = "rec-1"
            };

            var action = ParsekPlaybackPolicy.DecideHeldGhostAction(
                0, info, committed, currentTime: 1.0f, timeoutSeconds: 5f,
                retryIntervalSeconds: 1.0f);

            Assert.Equal(HeldGhostAction.RetrySpawn, action);
        }

        #endregion

        #region HeldGhostInfo struct

        [Fact]
        public void HeldGhostInfo_DefaultValues()
        {
            var info = new HeldGhostInfo();
            Assert.Equal(0f, info.holdStartTime);
            Assert.Equal(0f, info.lastRetryTime);
            Assert.Null(info.recordingId);
            Assert.Null(info.vesselName);
        }

        #endregion

        #region HeldGhostAction enum

        [Fact]
        public void HeldGhostAction_HasExpectedValues()
        {
            // Verify all enum values exist (compile-time check, but good documentation)
            Assert.Equal(0, (int)HeldGhostAction.Hold);
            Assert.Equal(1, (int)HeldGhostAction.RetrySpawn);
            Assert.Equal(2, (int)HeldGhostAction.ReleaseSpawned);
            Assert.Equal(3, (int)HeldGhostAction.ReleaseSupersededByRelation);
            Assert.Equal(4, (int)HeldGhostAction.Timeout);
            Assert.Equal(5, (int)HeldGhostAction.InvalidIndex);
        }

        #endregion

        #region Timeout constant

        [Fact]
        public void HeldGhostTimeoutSeconds_IsFiveSeconds()
        {
            Assert.Equal(5.0f, ParsekPlaybackPolicy.HeldGhostTimeoutSeconds);
        }

        #endregion
    }
}
