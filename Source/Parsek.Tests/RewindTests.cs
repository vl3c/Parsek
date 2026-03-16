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
    }
}
