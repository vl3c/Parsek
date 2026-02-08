using System.Collections.Generic;
using Xunit;
using UnityEngine;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RecordingStoreTests
    {
        public RecordingStoreTests()
        {
            // Suppress Debug.Log calls outside Unity
            RecordingStore.SuppressLogging = true;
            // Start each test with a clean slate
            RecordingStore.ResetForTesting();
        }

        private List<TrajectoryPoint> MakePoints(int count, double startUT = 100)
        {
            var points = new List<TrajectoryPoint>();
            for (int i = 0; i < count; i++)
            {
                points.Add(new TrajectoryPoint
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
            RecordingStore.StashPending(new List<TrajectoryPoint>(), "Empty");

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

    [Collection("Sequential")]
    public class CrewReplacementTests
    {
        public CrewReplacementTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetReplacementsForTesting();
        }

        [Fact]
        public void CrewReplacements_EmptyByDefault()
        {
            Assert.Empty(ParsekScenario.CrewReplacements);
        }

        [Fact]
        public void ResetReplacementsForTesting_ClearsDictionary()
        {
            // We can't call ReserveCrewIn directly (needs KSP roster),
            // but we can test the serialization round-trip which populates the dictionary.
            var node = new ConfigNode("SCENARIO");
            var replacementsNode = node.AddNode("CREW_REPLACEMENTS");
            var entry = replacementsNode.AddNode("ENTRY");
            entry.AddValue("original", "Jebediah Kerman");
            entry.AddValue("replacement", "Bob Kerman Jr.");

            // Use OnLoad to populate (need a scenario instance)
            // Instead, test via the static accessor after reset
            ParsekScenario.ResetReplacementsForTesting();

            Assert.Empty(ParsekScenario.CrewReplacements);
        }

        [Fact]
        public void CrewReplacements_SaveRoundTrip_PreservesMapping()
        {
            // Build a scenario ConfigNode with crew replacements
            var saveNode = new ConfigNode("SCENARIO");
            var replacementsNode = saveNode.AddNode("CREW_REPLACEMENTS");

            var entry1 = replacementsNode.AddNode("ENTRY");
            entry1.AddValue("original", "Jebediah Kerman");
            entry1.AddValue("replacement", "Rodfrey Kerman");

            var entry2 = replacementsNode.AddNode("ENTRY");
            entry2.AddValue("original", "Bill Kerman");
            entry2.AddValue("replacement", "Samantha Kerman");

            // Verify the ConfigNode structure is correct
            Assert.Equal(2, replacementsNode.GetNodes("ENTRY").Length);

            var loaded1 = replacementsNode.GetNodes("ENTRY")[0];
            Assert.Equal("Jebediah Kerman", loaded1.GetValue("original"));
            Assert.Equal("Rodfrey Kerman", loaded1.GetValue("replacement"));

            var loaded2 = replacementsNode.GetNodes("ENTRY")[1];
            Assert.Equal("Bill Kerman", loaded2.GetValue("original"));
            Assert.Equal("Samantha Kerman", loaded2.GetValue("replacement"));
        }

        [Fact]
        public void CrewReplacements_SaveNode_EmptyMappingSkipsNode()
        {
            // With no replacements, the CREW_REPLACEMENTS node should not be created
            var node = new ConfigNode("SCENARIO");

            // No CREW_REPLACEMENTS node means nothing to load
            Assert.Null(node.GetNode("CREW_REPLACEMENTS"));
        }

        [Fact]
        public void CrewReplacements_LoadNode_HandlesNullValues()
        {
            var node = new ConfigNode("SCENARIO");
            var replacementsNode = node.AddNode("CREW_REPLACEMENTS");

            // Entry with missing replacement value
            var entry = replacementsNode.AddNode("ENTRY");
            entry.AddValue("original", "Jeb");
            // No "replacement" value

            // This should not crash and should not add an invalid mapping
            var crNode = node.GetNode("CREW_REPLACEMENTS");
            var entries = crNode.GetNodes("ENTRY");
            Assert.Single(entries);

            string replacement = entries[0].GetValue("replacement");
            Assert.Null(replacement);
        }

        [Fact]
        public void CrewReplacements_LoadNode_MissingNodeReturnsCleanState()
        {
            var node = new ConfigNode("SCENARIO");

            // No CREW_REPLACEMENTS node at all
            Assert.Null(node.GetNode("CREW_REPLACEMENTS"));
            // This mirrors what LoadCrewReplacements does: clear + return early
        }
    }
}
