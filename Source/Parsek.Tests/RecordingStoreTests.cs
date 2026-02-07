using System.Collections.Generic;
using Xunit;
using UnityEngine;

namespace Parsek.Tests
{
    public class RecordingStoreTests
    {
        public RecordingStoreTests()
        {
            // Suppress Debug.Log calls outside Unity
            RecordingStore.SuppressLogging = true;
            // Start each test with a clean slate
            RecordingStore.ResetForTesting();
        }

        private List<ParsekSpike.TrajectoryPoint> MakePoints(int count, double startUT = 100)
        {
            var points = new List<ParsekSpike.TrajectoryPoint>();
            for (int i = 0; i < count; i++)
            {
                points.Add(new ParsekSpike.TrajectoryPoint
                {
                    ut = startUT + i * 10,
                    latitude = 0,
                    longitude = 0,
                    altitude = 100,
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero,
                    bodyName = "Kerbin"
                });
            }
            return points;
        }

        [Fact]
        public void StashPending_WithPoints_SetsPending()
        {
            var points = MakePoints(5);

            RecordingStore.StashPending(points, "TestVessel");

            Assert.True(RecordingStore.HasPending);
            Assert.Equal(5, RecordingStore.Pending.Points.Count);
            Assert.Equal("TestVessel", RecordingStore.Pending.VesselName);
        }

        [Fact]
        public void StashPending_EmptyList_DoesNotStash()
        {
            RecordingStore.StashPending(new List<ParsekSpike.TrajectoryPoint>(), "Empty");

            Assert.False(RecordingStore.HasPending);
        }

        [Fact]
        public void StashPending_NullList_DoesNotStash()
        {
            RecordingStore.StashPending(null, "Null");

            Assert.False(RecordingStore.HasPending);
        }

        [Fact]
        public void CommitPending_MovesPendingToCommitted()
        {
            RecordingStore.StashPending(MakePoints(3), "Ship");

            RecordingStore.CommitPending();

            Assert.False(RecordingStore.HasPending);
            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("Ship", RecordingStore.CommittedRecordings[0].VesselName);
        }

        [Fact]
        public void CommitPending_NoPending_DoesNothing()
        {
            // Should not crash
            RecordingStore.CommitPending();

            Assert.False(RecordingStore.HasPending);
            Assert.Empty(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void DiscardPending_ClearsPending()
        {
            RecordingStore.StashPending(MakePoints(3), "Ship");

            RecordingStore.DiscardPending();

            Assert.False(RecordingStore.HasPending);
            Assert.Empty(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void Clear_ClearsEverything()
        {
            // Stash + commit one
            RecordingStore.StashPending(MakePoints(3), "First");
            RecordingStore.CommitPending();
            // Stash another (pending)
            RecordingStore.StashPending(MakePoints(2), "Second");

            RecordingStore.Clear();

            Assert.False(RecordingStore.HasPending);
            Assert.Empty(RecordingStore.CommittedRecordings);
        }

        [Fact]
        public void Recording_StartUT_EndUT_Computed()
        {
            RecordingStore.StashPending(MakePoints(5, startUT: 200), "Ship");

            var rec = RecordingStore.Pending;
            Assert.Equal(200, rec.StartUT);
            Assert.Equal(240, rec.EndUT); // 200 + 4*10
        }

        [Fact]
        public void Recording_StartUT_EndUT_EmptyPoints()
        {
            var rec = new RecordingStore.Recording();
            Assert.Equal(0, rec.StartUT);
            Assert.Equal(0, rec.EndUT);
        }

        [Fact]
        public void Recording_VesselSnapshot_StoredAndRetrieved()
        {
            RecordingStore.StashPending(MakePoints(3), "Ship");

            var node = new ConfigNode("VESSEL");
            node.AddValue("name", "TestVessel");
            RecordingStore.Pending.VesselSnapshot = node;

            Assert.NotNull(RecordingStore.Pending.VesselSnapshot);
            Assert.Equal("TestVessel", RecordingStore.Pending.VesselSnapshot.GetValue("name"));
        }

        [Fact]
        public void Recording_DistanceAndDestroyed_DefaultValues()
        {
            var rec = new RecordingStore.Recording();

            Assert.Equal(0, rec.DistanceFromLaunch);
            Assert.False(rec.VesselDestroyed);
            Assert.Null(rec.VesselSnapshot);
            Assert.Null(rec.VesselSituation);
        }
    }
}
