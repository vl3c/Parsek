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
        //  Item 6: UpdateRecordingsForTerminalEvent skips committed
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void UpdateRecordingsForTerminalEvent_SkipsCommittedRecordings()
        {
            // Setup: committed recording with matching vessel name
            var rec = MakeCommittedRecording("MyRocket", 12345);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

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
        public void UpdateRecordingsForTerminalEvent_DoesNotAffectNonCommittedRecordings()
        {
            // With standalone pending removed, UpdateRecordingsForTerminalEvent
            // only operates on committed recordings. Verify it does not crash
            // when no matching committed recording exists.
            bool updated = ParsekScenario.UpdateRecordingsForTerminalEvent(
                "MyRocket", TerminalState.Recovered, 18000.0);

            Assert.False(updated);
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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

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
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            RecordingStore.ResetAllPlaybackState();

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec,
                isActiveChainMember: false,
                isChainLooping: false);

            Assert.True(needsSpawn, $"Should be spawn-eligible after reset, but got: {reason}");
            Assert.NotNull(rec.VesselSnapshot);
        }
    }
}