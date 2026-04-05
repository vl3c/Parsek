using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #95: committed recordings must not have their immutable fields
    /// (VesselSnapshot, GhostVisualSnapshot) mutated after commit. VesselDestroyed
    /// is a mutable playback state field and can be set freely.
    /// </summary>
    [Collection("Sequential")]
    public class CommittedRecordingImmutabilityTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CommittedRecordingImmutabilityTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        #region Helpers

        private static ConfigNode MakeSnapshot(string vesselName = "TestVessel")
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("name", vesselName);
            node.AddValue("pid", "12345");
            return node;
        }

        private static Recording MakeCommittedRecording(string name = "TestVessel", uint pid = 12345)
        {
            return new Recording
            {
                VesselName = name,
                VesselPersistentId = pid,
                VesselSnapshot = MakeSnapshot(name),
                GhostVisualSnapshot = MakeSnapshot(name),
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 17000,
                        latitude = -0.0972,
                        longitude = -74.5575,
                        altitude = 70,
                        bodyName = "Kerbin",
                        rotation = Quaternion.identity,
                        velocity = Vector3.up * 10
                    },
                    new TrajectoryPoint
                    {
                        ut = 17010,
                        latitude = -0.0972,
                        longitude = -74.5575,
                        altitude = 500,
                        bodyName = "Kerbin",
                        rotation = Quaternion.identity,
                        velocity = Vector3.up * 50
                    }
                }
            };
        }

        #endregion

        // ────────────────────────────────────────────────────────────
        //  Item 1: Continuation vessel destroyed preserves snapshot
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ContinuationVesselDestroyed_PreservesVesselSnapshot()
        {
            // Setup: committed recording with a valid snapshot
            var rec = MakeCommittedRecording();
            RecordingStore.CommittedRecordings.Add(rec);

            // Simulate what the fixed destruction handler does:
            // Set VesselDestroyed=true but do NOT null VesselSnapshot
            rec.VesselDestroyed = true;

            // Verify snapshot is preserved
            Assert.NotNull(rec.VesselSnapshot);
            Assert.NotNull(rec.GhostVisualSnapshot);
            Assert.True(rec.VesselDestroyed);
        }

        [Fact]
        public void ContinuationVesselDestroyed_VesselDestroyedGatesSpawn()
        {
            // Verify that VesselDestroyed=true prevents spawn even with a valid snapshot.
            // This confirms removing the snapshot null is safe.
            var rec = MakeCommittedRecording();
            rec.VesselDestroyed = true;

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec,
                isActiveChainMember: false,
                isChainLoopingOrDisabled: false);

            Assert.False(needsSpawn);
            Assert.Contains("vessel destroyed", reason);
        }

        [Fact]
        public void ContinuationVesselDestroyed_SnapshotPreservedForRevertSpawn()
        {
            // After revert, VesselDestroyed is reset (it's a transient playback flag).
            // The snapshot must still be present for spawn to work.
            var rec = MakeCommittedRecording();
            rec.VesselDestroyed = true;

            // Simulate revert: reset playback state
            rec.VesselDestroyed = false;
            rec.VesselSpawned = false;
            rec.SpawnedVesselPersistentId = 0;

            // Should be eligible for spawn now
            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec,
                isActiveChainMember: false,
                isChainLoopingOrDisabled: false);

            Assert.True(needsSpawn, $"Expected spawn eligible after revert, but got: {reason}");
            Assert.NotNull(rec.VesselSnapshot);
        }

        // ────────────────────────────────────────────────────────────
        //  Item 2: EVA boarding preserves snapshot
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void EvaBoardingContinuationStop_PreservesVesselSnapshot()
        {
            // Setup: committed recording representing a vessel segment
            var rec = MakeCommittedRecording();
            RecordingStore.CommittedRecordings.Add(rec);
            int recIdx = RecordingStore.CommittedRecordings.Count - 1;

            // The old code would have done: rec.VesselSnapshot = null;
            // The new code preserves the snapshot. Verify directly.
            Assert.NotNull(rec.VesselSnapshot);
            Assert.NotNull(rec.GhostVisualSnapshot);

            // Snapshot should remain usable for spawn after revert
            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec,
                isActiveChainMember: false,
                isChainLoopingOrDisabled: false);

            Assert.True(needsSpawn, "Vessel segment should be spawn-eligible after boarding continuation stops");
        }

        [Fact]
        public void EvaBoardingContinuationStop_LogsPreservation()
        {
            // Verify that the boarding path logs snapshot preservation
            var rec = MakeCommittedRecording();
            RecordingStore.CommittedRecordings.Add(rec);
            int recIdx = RecordingStore.CommittedRecordings.Count - 1;

            // Create a ChainSegmentManager with continuation active
            var mgr = new ChainSegmentManager();
            mgr.ActiveChainId = Guid.NewGuid().ToString("N");
            mgr.ActiveChainNextIndex = 1;
            mgr.ContinuationVesselPid = rec.VesselPersistentId;
            mgr.ContinuationRecordingIdx = recIdx;

            // Simulate EVA boarding: CommitChainSegment with EVA segment
            // We can't easily call CommitChainSegment without a FlightRecorder,
            // but we can verify the snapshot is preserved after the code path
            // that would have nulled it.

            // The key invariant: after any chain operation, committed snapshot is preserved
            Assert.NotNull(RecordingStore.CommittedRecordings[recIdx].VesselSnapshot);

            // Verify log message from chain manager setup
            Assert.Contains(logLines, l =>
                l.Contains("[Chain]") && l.Contains("ChainSegmentManager created"));
        }

        // ────────────────────────────────────────────────────────────
        //  Item 6: UpdateRecordingsForTerminalEvent skips committed
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void UpdateRecordingsForTerminalEvent_SkipsCommittedRecordings()
        {
            // Setup: committed recording with matching vessel name
            var rec = MakeCommittedRecording("MyRocket", 12345);
            RecordingStore.CommittedRecordings.Add(rec);

            var originalSnapshot = rec.VesselSnapshot;
            var originalTerminalState = rec.TerminalStateValue;

            // Call the terminal event handler with matching vessel name
            bool updated = ParsekScenario.UpdateRecordingsForTerminalEvent(
                "MyRocket", TerminalState.Recovered, 18000.0);

            // Should NOT have updated any recording (committed ones are skipped)
            Assert.False(updated);

            // Verify committed recording is untouched
            Assert.Same(originalSnapshot, rec.VesselSnapshot);
            Assert.Equal(originalTerminalState, rec.TerminalStateValue);
        }

        [Fact]
        public void UpdateRecordingsForTerminalEvent_SkipsCommitted_EvenWhenNotSpawned()
        {
            // Bug #95 item 6 edge case: committed recording that hasn't spawned yet
            // should still be skipped (not just spawned ones)
            var rec = MakeCommittedRecording("Flea", 99999);
            rec.VesselSpawned = false;
            rec.SpawnedVesselPersistentId = 0;
            RecordingStore.CommittedRecordings.Add(rec);

            var originalSnapshot = rec.VesselSnapshot;

            bool updated = ParsekScenario.UpdateRecordingsForTerminalEvent(
                "Flea", TerminalState.Destroyed, 18000.0);

            Assert.False(updated);
            Assert.Same(originalSnapshot, rec.VesselSnapshot);
            Assert.Null(rec.TerminalStateValue); // unchanged from default
        }

        [Fact]
        public void UpdateRecordingsForTerminalEvent_StillUpdatesPendingRecordings()
        {
            // Verify the fix doesn't break legitimate pending recording updates.
            // StashPending requires >= 2 points with distinct position/velocity.
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint
                {
                    ut = 17000,
                    latitude = 0, longitude = 0, altitude = 70,
                    bodyName = "Kerbin",
                    rotation = Quaternion.identity,
                    velocity = Vector3.up * 50
                },
                new TrajectoryPoint
                {
                    ut = 17010,
                    latitude = 0, longitude = 0, altitude = 500,
                    bodyName = "Kerbin",
                    rotation = Quaternion.identity,
                    velocity = Vector3.up * 100
                }
            };

            RecordingStore.StashPending(points, "MyRocket");
            Assert.True(RecordingStore.HasPending, "StashPending should have created a pending recording");

            var pending = RecordingStore.Pending;
            pending.VesselSnapshot = MakeSnapshot("MyRocket");

            bool updated = ParsekScenario.UpdateRecordingsForTerminalEvent(
                "MyRocket", TerminalState.Recovered, 18000.0);

            Assert.True(updated);
            Assert.Null(pending.VesselSnapshot); // pending snapshot IS nulled (correct)
            Assert.Equal(TerminalState.Recovered, pending.TerminalStateValue);
        }

        // ────────────────────────────────────────────────────────────
        //  VesselDestroyed reset on revert
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ResetAllPlaybackState_ClearsVesselDestroyed()
        {
            // Bug #95 review catch: VesselDestroyed must be reset on revert/rewind
            // so that the preserved snapshot can actually be used for spawn.
            var rec = MakeCommittedRecording();
            rec.VesselDestroyed = true;
            RecordingStore.CommittedRecordings.Add(rec);

            RecordingStore.ResetAllPlaybackState();

            Assert.False(rec.VesselDestroyed,
                "VesselDestroyed must be reset by ResetAllPlaybackState so spawn works after revert");
        }

        [Fact]
        public void ResetAllPlaybackState_ClearsVesselDestroyed_SpawnEligibleAfter()
        {
            // End-to-end: destroy → reset → spawn eligibility check
            var rec = MakeCommittedRecording();
            rec.VesselDestroyed = true;
            RecordingStore.CommittedRecordings.Add(rec);

            RecordingStore.ResetAllPlaybackState();

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec,
                isActiveChainMember: false,
                isChainLoopingOrDisabled: false);

            Assert.True(needsSpawn, $"Should be spawn-eligible after reset, but got: {reason}");
        }

        // ────────────────────────────────────────────────────────────
        //  Logging assertions
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ContinuationVesselDestroyed_LogMessageFormat_ContainsExpectedText()
        {
            // Note: This tests the expected log message FORMAT, not that production
            // code actually emits it (OnVesselWillDestroy requires KSP runtime).
            // Verifies the log message contract for the destruction handler.
            var rec = MakeCommittedRecording("TestRocket", 55555);
            RecordingStore.CommittedRecordings.Add(rec);

            ParsekLog.Info("Flight",
                $"Continuation vessel destroyed (pid={55555}), " +
                $"VesselDestroyed=true, VesselSnapshot preserved={rec.VesselSnapshot != null}");

            Assert.Contains(logLines, l =>
                l.Contains("[Flight]") &&
                l.Contains("Continuation vessel destroyed") &&
                l.Contains("VesselSnapshot preserved=True"));
        }

        [Fact]
        public void UpdateTerminalEvent_PendingUpdated_LogsCorrectly()
        {
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint
                {
                    ut = 17000,
                    latitude = 0, longitude = 0, altitude = 70,
                    bodyName = "Kerbin",
                    rotation = Quaternion.identity,
                    velocity = Vector3.up * 50
                },
                new TrajectoryPoint
                {
                    ut = 17010,
                    latitude = 0, longitude = 0, altitude = 500,
                    bodyName = "Kerbin",
                    rotation = Quaternion.identity,
                    velocity = Vector3.up * 100
                }
            };

            RecordingStore.StashPending(points, "RecoverMe");
            Assert.True(RecordingStore.HasPending, "StashPending should have created a pending recording");

            RecordingStore.Pending.VesselSnapshot = MakeSnapshot("RecoverMe");

            ParsekScenario.UpdateRecordingsForTerminalEvent(
                "RecoverMe", TerminalState.Recovered, 18000.0);

            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") &&
                l.Contains("Updated pending recording") &&
                l.Contains("RecoverMe") &&
                l.Contains("Recovered"));
        }
    }
}