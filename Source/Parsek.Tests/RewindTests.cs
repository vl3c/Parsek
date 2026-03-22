using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RewindTests
    {
        public RewindTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void IsStableState_StableStates()
        {
            Assert.True(RecordingStore.IsStableState(1));   // LANDED
            Assert.True(RecordingStore.IsStableState(2));   // SPLASHED
            Assert.True(RecordingStore.IsStableState(4));   // PRELAUNCH
            Assert.True(RecordingStore.IsStableState(32));  // ORBITING
        }

        [Fact]
        public void IsStableState_UnstableStates()
        {
            Assert.False(RecordingStore.IsStableState(8));   // FLYING
            Assert.False(RecordingStore.IsStableState(16));  // SUB_ORBITAL
            Assert.False(RecordingStore.IsStableState(64));  // ESCAPING
            Assert.False(RecordingStore.IsStableState(128)); // DOCKED
            Assert.False(RecordingStore.IsStableState(0));   // unknown
        }

        [Fact]
        public void CanRewind_NoRewindSave_ReturnsFalse()
        {
            var rec = new Recording();
            string reason;
            Assert.False(RecordingStore.CanRewind(rec, out reason, isRecording: false));
            Assert.Equal("No rewind save available", reason);
        }

        [Fact]
        public void CanRewind_AlreadyRewinding_ReturnsFalse()
        {
            var rec = new Recording { RewindSaveFileName = "parsek_rw_abc123" };
            RecordingStore.IsRewinding = true;
            string reason;
            Assert.False(RecordingStore.CanRewind(rec, out reason, isRecording: false));
            Assert.Equal("Rewind already in progress", reason);
        }

        [Fact]
        public void CanRewind_Recording_ReturnsFalse()
        {
            var rec = new Recording { RewindSaveFileName = "parsek_rw_abc123" };
            string reason;
            Assert.False(RecordingStore.CanRewind(rec, out reason, isRecording: true));
            Assert.Equal("Stop recording before rewinding", reason);
        }

        [Fact]
        public void ResetAllPlaybackState_ResetsRecordings()
        {
            var rec = new Recording
            {
                VesselSpawned = true,
                SpawnAttempts = 3,
                SpawnedVesselPersistentId = 42,
                LastAppliedResourceIndex = 5,

                SceneExitSituation = 4
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100 });
            rec.Points.Add(new TrajectoryPoint { ut = 200 });
            RecordingStore.CommittedRecordings.Add(rec);

            var (standaloneCount, treeCount) = RecordingStore.ResetAllPlaybackState();

            Assert.Equal(1, standaloneCount);
            Assert.Equal(0, treeCount);
            Assert.False(rec.VesselSpawned);
            Assert.Equal(0, rec.SpawnAttempts);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.Equal(-1, rec.LastAppliedResourceIndex);

            Assert.Equal(-1, rec.SceneExitSituation);
        }

        [Fact]
        public void MarkAllFullyApplied_SetsCorrectIndices()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100 });
            rec.Points.Add(new TrajectoryPoint { ut = 200 });
            rec.Points.Add(new TrajectoryPoint { ut = 300 });
            RecordingStore.CommittedRecordings.Add(rec);

            var (recCount, treeCount) = RecordingStore.MarkAllFullyApplied();

            Assert.Equal(1, recCount);
            Assert.Equal(0, treeCount);
            Assert.Equal(2, rec.LastAppliedResourceIndex); // Points.Count - 1
        }

        [Fact]
        public void CountFutureRecordings_CountsCorrectly()
        {
            var rec1 = new Recording();
            rec1.Points.Add(new TrajectoryPoint { ut = 100 });
            rec1.Points.Add(new TrajectoryPoint { ut = 200 });

            var rec2 = new Recording();
            rec2.Points.Add(new TrajectoryPoint { ut = 300 });
            rec2.Points.Add(new TrajectoryPoint { ut = 400 });

            var rec3 = new Recording();
            rec3.Points.Add(new TrajectoryPoint { ut = 500 });
            rec3.Points.Add(new TrajectoryPoint { ut = 600 });

            RecordingStore.CommittedRecordings.Add(rec1);
            RecordingStore.CommittedRecordings.Add(rec2);
            RecordingStore.CommittedRecordings.Add(rec3);

            Assert.Equal(2, RecordingStore.CountFutureRecordings(200));
            Assert.Equal(1, RecordingStore.CountFutureRecordings(400));
            Assert.Equal(0, RecordingStore.CountFutureRecordings(600));
            Assert.Equal(3, RecordingStore.CountFutureRecordings(0));
        }

        [Fact]
        public void RewindFields_InRecording_DefaultToEmpty()
        {
            var rec = new Recording();
            Assert.Null(rec.RewindSaveFileName);
            Assert.Equal(0.0, rec.RewindReservedFunds);
            Assert.Equal(0.0, rec.RewindReservedScience);
            Assert.Equal(0f, rec.RewindReservedRep);
        }

        [Fact]
        public void ApplyPersistenceArtifactsFrom_CopiesRewindFields()
        {
            var source = new Recording
            {
                RewindSaveFileName = "parsek_rw_test01",
                RewindReservedFunds = 1000.0,
                RewindReservedScience = 50.0,
                RewindReservedRep = 5.0f
            };

            var target = new Recording();
            target.ApplyPersistenceArtifactsFrom(source);

            Assert.Equal("parsek_rw_test01", target.RewindSaveFileName);
            Assert.Equal(1000.0, target.RewindReservedFunds);
            Assert.Equal(50.0, target.RewindReservedScience);
            Assert.Equal(5.0f, target.RewindReservedRep);
        }

        [Fact]
        public void ResetForTesting_ClearsRewindFlags()
        {
            RecordingStore.IsRewinding = true;
            RecordingStore.RewindUT = 12345.0;
            RecordingStore.RewindReserved = new ResourceBudget.BudgetSummary
            {
                reservedFunds = 100, reservedScience = 10, reservedReputation = 5
            };

            RecordingStore.ResetForTesting();

            Assert.False(RecordingStore.IsRewinding);
            Assert.Equal(0.0, RecordingStore.RewindUT);
            Assert.Equal(0.0, RecordingStore.RewindReserved.reservedFunds);
        }

        [Fact]
        public void ResetAllPlaybackState_ClearsPostSpawnRecoveredTerminalState()
        {
            var rec = new Recording
            {
                VesselName = "TestVessel",
                VesselSpawned = true,
                SpawnedVesselPersistentId = 100,
                TerminalStateValue = TerminalState.Recovered
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100 });
            RecordingStore.CommittedRecordings.Add(rec);

            RecordingStore.ResetAllPlaybackState();

            Assert.Null(rec.TerminalStateValue);
            Assert.False(rec.VesselSpawned);
        }

        [Fact]
        public void ResetAllPlaybackState_ClearsPostSpawnDestroyedTerminalState()
        {
            var rec = new Recording
            {
                VesselName = "TestVessel",
                VesselSpawned = true,
                SpawnedVesselPersistentId = 200,
                TerminalStateValue = TerminalState.Destroyed
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100 });
            RecordingStore.CommittedRecordings.Add(rec);

            RecordingStore.ResetAllPlaybackState();

            Assert.Null(rec.TerminalStateValue);
        }

        [Fact]
        public void ResetAllPlaybackState_PreservesRecordingTimeTerminalState()
        {
            // Recording committed as ghost-only (destroyed during recording, never spawned)
            var rec = new Recording
            {
                VesselName = "TestVessel",
                VesselSpawned = false,
                TerminalStateValue = TerminalState.Destroyed
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100 });
            RecordingStore.CommittedRecordings.Add(rec);

            RecordingStore.ResetAllPlaybackState();

            // Terminal state should persist — it was set during recording, not post-spawn
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
        }

        [Fact]
        public void ResetAllPlaybackState_PreservesSituationBasedTerminalState()
        {
            // Spawned recording with Landed terminal state (set at commit, not suppressing)
            var rec = new Recording
            {
                VesselName = "TestVessel",
                VesselSpawned = true,
                TerminalStateValue = TerminalState.Landed
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100 });
            RecordingStore.CommittedRecordings.Add(rec);

            RecordingStore.ResetAllPlaybackState();

            // Situation-based states (Landed, Orbiting, etc.) are preserved —
            // only Recovered/Destroyed are cleared as post-spawn lifecycle events
            Assert.Equal(TerminalState.Landed, rec.TerminalStateValue);
        }

        [Fact]
        public void CollectSpawnedVesselInfo_NoSpawnedRecordings_ReturnsEmpty()
        {
            var rec = new Recording { VesselName = "Rocket", SpawnedVesselPersistentId = 0 };
            RecordingStore.CommittedRecordings.Add(rec);

            var (pids, names) = RecordingStore.CollectSpawnedVesselInfo();

            Assert.Empty(pids);
            Assert.Empty(names);
        }

        [Fact]
        public void CollectSpawnedVesselInfo_ReturnsNonZeroPidsAndNames()
        {
            RecordingStore.CommittedRecordings.Add(new Recording
                { VesselName = "Rocket", SpawnedVesselPersistentId = 42 });
            RecordingStore.CommittedRecordings.Add(new Recording
                { VesselName = "Shuttle", SpawnedVesselPersistentId = 99 });

            var (pids, names) = RecordingStore.CollectSpawnedVesselInfo();

            Assert.Equal(2, pids.Count);
            Assert.Contains(42u, pids);
            Assert.Contains(99u, pids);
            Assert.Equal(2, names.Count);
            Assert.Contains("Rocket", names);
            Assert.Contains("Shuttle", names);
        }

        [Fact]
        public void CollectSpawnedVesselInfo_SkipsZeroPids()
        {
            RecordingStore.CommittedRecordings.Add(new Recording
                { VesselName = "Rocket", SpawnedVesselPersistentId = 42 });
            RecordingStore.CommittedRecordings.Add(new Recording
                { VesselName = "Shuttle", SpawnedVesselPersistentId = 0 });

            var (pids, names) = RecordingStore.CollectSpawnedVesselInfo();

            Assert.Single(pids);
            Assert.Contains(42u, pids);
            Assert.Single(names);
            Assert.Contains("Rocket", names);
        }

        [Fact]
        public void CollectSpawnedVesselInfo_DeduplicatesSameName()
        {
            RecordingStore.CommittedRecordings.Add(new Recording
                { VesselName = "Rocket", SpawnedVesselPersistentId = 42 });
            RecordingStore.CommittedRecordings.Add(new Recording
                { VesselName = "Rocket", SpawnedVesselPersistentId = 99 });

            var (pids, names) = RecordingStore.CollectSpawnedVesselInfo();

            Assert.Equal(2, pids.Count);
            Assert.Single(names); // "Rocket" deduplicated
        }
    }
}
