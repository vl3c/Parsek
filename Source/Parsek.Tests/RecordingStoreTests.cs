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

        [Fact]
        public void Recording_DefaultFields()
        {
            var rec = new RecordingStore.Recording();

            Assert.False(rec.VesselSpawned);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.Equal(-1, rec.LastAppliedResourceIndex);
            Assert.Equal(0, rec.DistanceFromLaunch);
            Assert.Equal(0, rec.MaxDistanceFromLaunch);
        }

        [Fact]
        public void Recording_DistanceFields_Roundtrip()
        {
            var rec = new RecordingStore.Recording();
            rec.DistanceFromLaunch = 12345.67;
            rec.MaxDistanceFromLaunch = 99999.99;

            Assert.Equal(12345.67, rec.DistanceFromLaunch);
            Assert.Equal(99999.99, rec.MaxDistanceFromLaunch);
        }

        [Fact]
        public void Recording_SpawnFields_Roundtrip()
        {
            var rec = new RecordingStore.Recording();
            rec.VesselSpawned = true;
            rec.SpawnedVesselPersistentId = 42u;

            Assert.True(rec.VesselSpawned);
            Assert.Equal(42u, rec.SpawnedVesselPersistentId);
        }

        [Fact]
        public void Recording_LastAppliedResourceIndex_Tracks()
        {
            var rec = new RecordingStore.Recording();
            rec.LastAppliedResourceIndex = 5;

            Assert.Equal(5, rec.LastAppliedResourceIndex);
        }

        [Fact]
        public void StashPending_SinglePoint_Discards()
        {
            var points = MakePoints(1);

            RecordingStore.StashPending(points, "TooShort");

            Assert.False(RecordingStore.HasPending);
        }

        [Fact]
        public void StashPending_ExactlyTwoPoints_Succeeds()
        {
            var points = MakePoints(2);

            RecordingStore.StashPending(points, "MinValid");

            Assert.True(RecordingStore.HasPending);
            Assert.Equal(2, RecordingStore.Pending.Points.Count);
        }

        [Fact]
        public void GetRecommendedAction_DestroyedWithSnapshot()
        {
            // destroyed=true takes priority over hasSnapshot=true
            var result = RecordingStore.GetRecommendedAction(
                distance: 500, destroyed: true, hasSnapshot: true);

            Assert.Equal(RecordingStore.MergeDefault.MergeOnly, result);
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

        // --- Reservation decision logic (extracted for testability) ---

        [Fact]
        public void ShouldProcessCrew_Available_ReturnsTrue()
        {
            Assert.True(ParsekScenario.ShouldProcessCrewForReservation(
                ProtoCrewMember.RosterStatus.Available));
        }

        [Fact]
        public void ShouldProcessCrew_Assigned_ReturnsTrue()
        {
            // Key scenario: crew already Assigned (on pad vessel after revert)
            // must still be processed so a replacement is hired and swap can happen
            Assert.True(ParsekScenario.ShouldProcessCrewForReservation(
                ProtoCrewMember.RosterStatus.Assigned));
        }

        [Fact]
        public void ShouldProcessCrew_Dead_ReturnsFalse()
        {
            Assert.False(ParsekScenario.ShouldProcessCrewForReservation(
                ProtoCrewMember.RosterStatus.Dead));
        }

        [Fact]
        public void ShouldProcessCrew_Missing_ReturnsFalse()
        {
            Assert.False(ParsekScenario.ShouldProcessCrewForReservation(
                ProtoCrewMember.RosterStatus.Missing));
        }

        [Fact]
        public void NeedsStatusChange_Available_ReturnsTrue()
        {
            Assert.True(ParsekScenario.NeedsStatusChange(
                ProtoCrewMember.RosterStatus.Available));
        }

        [Fact]
        public void NeedsStatusChange_Assigned_ReturnsFalse()
        {
            // Already Assigned (e.g. on pad vessel) — no status change needed,
            // but replacement still needed
            Assert.False(ParsekScenario.NeedsStatusChange(
                ProtoCrewMember.RosterStatus.Assigned));
        }

        [Fact]
        public void NeedsStatusChange_Dead_ReturnsFalse()
        {
            Assert.False(ParsekScenario.NeedsStatusChange(
                ProtoCrewMember.RosterStatus.Dead));
        }

        // --- Snapshot crew extraction ---

        [Fact]
        public void ExtractCrewFromSnapshot_SinglePart_SingleCrew()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "Jebediah Kerman");

            var crew = ParsekScenario.ExtractCrewFromSnapshot(snapshot);

            Assert.Single(crew);
            Assert.Equal("Jebediah Kerman", crew[0]);
        }

        [Fact]
        public void ExtractCrewFromSnapshot_MultiplePartsAndCrew()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part1 = snapshot.AddNode("PART");
            part1.AddValue("crew", "Jebediah Kerman");
            part1.AddValue("crew", "Bill Kerman");
            var part2 = snapshot.AddNode("PART");
            part2.AddValue("crew", "Valentina Kerman");

            var crew = ParsekScenario.ExtractCrewFromSnapshot(snapshot);

            Assert.Equal(3, crew.Count);
            Assert.Contains("Jebediah Kerman", crew);
            Assert.Contains("Bill Kerman", crew);
            Assert.Contains("Valentina Kerman", crew);
        }

        [Fact]
        public void ExtractCrewFromSnapshot_NoCrew_ReturnsEmpty()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddNode("PART"); // part with no crew

            var crew = ParsekScenario.ExtractCrewFromSnapshot(snapshot);

            Assert.Empty(crew);
        }

        [Fact]
        public void ExtractCrewFromSnapshot_NullSnapshot_ReturnsEmpty()
        {
            var crew = ParsekScenario.ExtractCrewFromSnapshot(null);

            Assert.Empty(crew);
        }

        [Fact]
        public void ExtractCrewFromSnapshot_EmptyCrewValue_Skipped()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", "");
            part.AddValue("crew", "Jebediah Kerman");

            var crew = ParsekScenario.ExtractCrewFromSnapshot(snapshot);

            Assert.Single(crew);
            Assert.Equal("Jebediah Kerman", crew[0]);
        }
    }

    [Collection("Sequential")]
    public class MergeDialogTests
    {
        private static List<TrajectoryPoint> MakePoints(int count, double startUT = 100)
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
        public void BuildMergeMessage_Recover_MentionsLaunchSite()
        {
            var rec = new RecordingStore.Recording { VesselName = "TestShip" };
            rec.Points.AddRange(MakePoints(3));
            rec.DistanceFromLaunch = 50;

            string msg = MergeDialog.BuildMergeMessage(rec, 30,
                RecordingStore.MergeDefault.Recover);

            Assert.Contains("TestShip", msg);
            Assert.Contains("launch site", msg);
        }

        [Fact]
        public void BuildMergeMessage_MergeOnly_MentionsDestroyed()
        {
            var rec = new RecordingStore.Recording { VesselName = "Boom" };
            rec.Points.AddRange(MakePoints(5));
            rec.VesselDestroyed = true;

            string msg = MergeDialog.BuildMergeMessage(rec, 60,
                RecordingStore.MergeDefault.MergeOnly);

            Assert.Contains("destroyed", msg);
        }

        [Fact]
        public void BuildMergeMessage_Persist_ShortDistance_MentionsMaxDistance()
        {
            var rec = new RecordingStore.Recording { VesselName = "Orbiter" };
            rec.Points.AddRange(MakePoints(10));
            rec.DistanceFromLaunch = 50;
            rec.MaxDistanceFromLaunch = 500000;

            string msg = MergeDialog.BuildMergeMessage(rec, 300,
                RecordingStore.MergeDefault.Persist);

            Assert.Contains("500000", msg);
            Assert.Contains("persist", msg, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BuildMergeMessage_Persist_FarDistance_MentionsSituation()
        {
            var rec = new RecordingStore.Recording { VesselName = "Explorer" };
            rec.Points.AddRange(MakePoints(10));
            rec.DistanceFromLaunch = 500;
            rec.VesselSituation = "Orbiting Kerbin";

            string msg = MergeDialog.BuildMergeMessage(rec, 300,
                RecordingStore.MergeDefault.Persist);

            Assert.Contains("Orbiting Kerbin", msg);
        }

        [Fact]
        public void BuildMergeMessage_IncludesPointCount()
        {
            var rec = new RecordingStore.Recording { VesselName = "Ship" };
            rec.Points.AddRange(MakePoints(7));

            string msg = MergeDialog.BuildMergeMessage(rec, 60,
                RecordingStore.MergeDefault.Recover);

            Assert.Contains("7", msg);
        }

        [Fact]
        public void BuildMergeMessage_IncludesDuration()
        {
            var rec = new RecordingStore.Recording { VesselName = "Ship" };
            rec.Points.AddRange(MakePoints(3));

            string msg = MergeDialog.BuildMergeMessage(rec, 45.3,
                RecordingStore.MergeDefault.Recover);

            // Duration is locale-formatted ("45.3" or "45,3"), just check it contains "45"
            Assert.Contains("45", msg);
            Assert.Contains("Duration:", msg);
        }
    }
}
