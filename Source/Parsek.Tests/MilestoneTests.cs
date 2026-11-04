using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class MilestoneTests : System.IDisposable
    {
        public MilestoneTests()
        {
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static void AddCommittedRecording(string recordingId)
        {
            RecordingStore.AddCommittedInternal(new Recording
            {
                RecordingId = recordingId,
                VesselName = recordingId
            });
        }

        private static Milestone MakeCommittedMilestone(
            string id,
            double startUT,
            double endUT,
            params GameStateEvent[] events)
        {
            return new Milestone
            {
                MilestoneId = id,
                StartUT = startUT,
                EndUT = endUT,
                Committed = true,
                LastReplayedEventIndex = (events?.Length ?? 0) - 1,
                Events = events != null ? new List<GameStateEvent>(events) : new List<GameStateEvent>()
            };
        }

        #region Milestone Serialization

        [Fact]
        public void Milestone_SerializationRoundtrip_OmitsLegacyEpochFields()
        {
            var original = new Milestone
            {
                MilestoneId = "abc123def456",
                StartUT = 0,
                EndUT = 17090.5,
                RecordingId = "rec789",
                Committed = true,
                LastReplayedEventIndex = 3,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 17050,
                        eventType = GameStateEventType.TechResearched,
                        key = "basicRocketry",
                        detail = "cost=5",
                    },
                    new GameStateEvent
                    {
                        ut = 17080,
                        eventType = GameStateEventType.PartPurchased,
                        key = "mk1pod.v2",
                        detail = "cost=600",
                    }
                }
            };

            var node = new ConfigNode("MILESTONE");
            original.SerializeInto(node);

            Assert.Null(node.GetValue("epoch"));
            foreach (var eventNode in node.GetNodes("GAME_STATE_EVENT"))
                Assert.Null(eventNode.GetValue("epoch"));

            var restored = Milestone.DeserializeFrom(node);

            Assert.Equal("abc123def456", restored.MilestoneId);
            Assert.Equal(0, restored.StartUT);
            Assert.Equal(17090.5, restored.EndUT);
            Assert.Equal("rec789", restored.RecordingId);
            Assert.True(restored.Committed);
            Assert.Equal(3, restored.LastReplayedEventIndex);
            Assert.Equal(2, restored.Events.Count);
            Assert.Equal(GameStateEventType.TechResearched, restored.Events[0].eventType);
            Assert.Equal("basicRocketry", restored.Events[0].key);
            Assert.Equal("cost=5", restored.Events[0].detail);
            Assert.Equal(GameStateEventType.PartPurchased, restored.Events[1].eventType);
            Assert.Equal("mk1pod.v2", restored.Events[1].key);
        }

        [Fact]
        public void Milestone_EmptyEvents_RoundtripsCleanly()
        {
            var original = new Milestone
            {
                MilestoneId = "empty1",
                StartUT = 100,
                EndUT = 200,
                RecordingId = "rec1",
                Committed = false,
                LastReplayedEventIndex = -1
            };

            var node = new ConfigNode("MILESTONE");
            original.SerializeInto(node);

            var restored = Milestone.DeserializeFrom(node);

            Assert.Equal("empty1", restored.MilestoneId);
            Assert.Empty(restored.Events);
            Assert.False(restored.Committed);
            Assert.Equal(-1, restored.LastReplayedEventIndex);
        }

        #endregion
        #region MilestoneStore.CreateMilestone

        [Fact]
        public void MilestoneStore_CreateMilestone_SetsCorrectUTRange()
        {
            // Add a semantic event in range
            var techEvt = new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            };
            GameStateStore.AddEvent(ref techEvt);

            var milestone = MilestoneStore.CreateMilestone("rec1", 200);

            Assert.NotNull(milestone);
            Assert.Equal(0, milestone.StartUT); // no previous milestone
            Assert.Equal(200, milestone.EndUT);
        }

        [Fact]
        public void MilestoneStore_CreateMilestone_CopiesEventsInRange()
        {
            var techEvt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            };
            GameStateStore.AddEvent(ref techEvt);
            var partEvt = new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2",
                detail = "cost=600"
            };
            GameStateStore.AddEvent(ref partEvt);

            var milestone = MilestoneStore.CreateMilestone("rec1", 200);

            Assert.NotNull(milestone);
            Assert.Equal(2, milestone.Events.Count);
            Assert.Equal(GameStateEventType.TechResearched, milestone.Events[0].eventType);
            Assert.Equal(GameStateEventType.PartPurchased, milestone.Events[1].eventType);
        }

        [Fact]
        public void MilestoneStore_CreateMilestone_ExcludesOutOfRangeEvents()
        {
            // First milestone covers UT 0-100
            var techEvt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            };
            GameStateStore.AddEvent(ref techEvt);
            var m1 = MilestoneStore.CreateMilestone("rec1", 100);
            Assert.NotNull(m1);

            // Add event at UT 150 — should be in second milestone, not first
            var partEvt = new GameStateEvent
            {
                ut = 150,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2",
                detail = "cost=600"
            };
            GameStateStore.AddEvent(ref partEvt);

            var m2 = MilestoneStore.CreateMilestone("rec2", 200);

            Assert.NotNull(m2);
            Assert.Single(m2.Events);
            Assert.Equal(GameStateEventType.PartPurchased, m2.Events[0].eventType);
            Assert.Equal(100, m2.StartUT); // starts where m1 ended
        }

        [Fact]
        public void MilestoneStore_CreateMilestone_SkipsEmpty()
        {
            // No events at all
            var milestone = MilestoneStore.CreateMilestone("rec1", 100);

            Assert.Null(milestone);
            Assert.Equal(0, MilestoneStore.MilestoneCount);
        }

        [Fact]
        public void MilestoneStore_MultipleSequential()
        {
            var techEvt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            };
            GameStateStore.AddEvent(ref techEvt);
            var m1 = MilestoneStore.CreateMilestone("rec1", 100);

            var partEvt = new GameStateEvent
            {
                ut = 150,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2",
                detail = "cost=600"
            };
            GameStateStore.AddEvent(ref partEvt);
            var m2 = MilestoneStore.CreateMilestone("rec2", 200);

            Assert.NotNull(m1);
            Assert.NotNull(m2);
            Assert.Equal(0, m1.StartUT);
            Assert.Equal(100, m1.EndUT);
            Assert.Equal(100, m2.StartUT); // starts where first ended
            Assert.Equal(200, m2.EndUT);
        }

        [Fact]
        public void MilestoneStore_CreateMilestone_FiltersHiddenRecordingBranches()
        {
            AddCommittedRecording("live-rec");

            var techEvt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5",
                recordingId = "old-rec"
            };
            GameStateStore.AddEvent(ref techEvt);

            var partEvt = new GameStateEvent
            {
                ut = 60,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2",
                detail = "cost=600",
                recordingId = "live-rec"
            };
            GameStateStore.AddEvent(ref partEvt);

            var milestone = MilestoneStore.CreateMilestone("rec1", 100);

            Assert.NotNull(milestone);
            Assert.Single(milestone.Events);
            Assert.Equal(GameStateEventType.PartPurchased, milestone.Events[0].eventType);
        }

        [Fact]
        public void MilestoneStore_CreateMilestone_ExcludesRawResourceEvents()
        {
            var techEvt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            };
            GameStateStore.AddEvent(ref techEvt);
            var fundsEvt = new GameStateEvent
            {
                ut = 51,
                eventType = GameStateEventType.FundsChanged,
                key = "TechCost",
                valueBefore = 10000,
                valueAfter = 9995
            };
            GameStateStore.AddEvent(ref fundsEvt);
            var scienceEvt = new GameStateEvent
            {
                ut = 52,
                eventType = GameStateEventType.ScienceChanged,
                key = "TechCost",
                valueBefore = 100,
                valueAfter = 95
            };
            GameStateStore.AddEvent(ref scienceEvt);
            var repEvt = new GameStateEvent
            {
                ut = 53,
                eventType = GameStateEventType.ReputationChanged,
                key = "Unknown",
                valueBefore = 50,
                valueAfter = 55
            };
            GameStateStore.AddEvent(ref repEvt);

            var milestone = MilestoneStore.CreateMilestone("rec1", 100);

            Assert.NotNull(milestone);
            // Only the TechResearched event — all 3 resource events excluded
            Assert.Single(milestone.Events);
            Assert.Equal(GameStateEventType.TechResearched, milestone.Events[0].eventType);
        }

        [Fact]
        public void MilestoneStore_CreateMilestone_SortsEventsByUT()
        {
            // Add events out of order
            var partEvt = new GameStateEvent
            {
                ut = 80,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2",
                detail = "cost=600"
            };
            GameStateStore.AddEvent(ref partEvt);
            var techEvt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            };
            GameStateStore.AddEvent(ref techEvt);

            var milestone = MilestoneStore.CreateMilestone("rec1", 100);

            Assert.NotNull(milestone);
            Assert.Equal(2, milestone.Events.Count);
            Assert.True(milestone.Events[0].ut <= milestone.Events[1].ut,
                "Events should be sorted by UT");
            Assert.Equal(GameStateEventType.TechResearched, milestone.Events[0].eventType);
            Assert.Equal(GameStateEventType.PartPurchased, milestone.Events[1].eventType);
        }

        [Fact]
        public void Milestone_LastReplayedIndex_InitializedToEnd()
        {
            var techEvt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            };
            GameStateStore.AddEvent(ref techEvt);
            var partEvt = new GameStateEvent
            {
                ut = 60,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2",
                detail = "cost=600"
            };
            GameStateStore.AddEvent(ref partEvt);

            var milestone = MilestoneStore.CreateMilestone("rec1", 100);

            Assert.NotNull(milestone);
            // New milestones during normal play should be fully applied
            Assert.Equal(milestone.Events.Count - 1, milestone.LastReplayedEventIndex);
        }

        #endregion

        #region Mutable State

        [Fact]
        public void MilestoneStore_SaveLoadMutableState_RoundTrip()
        {
            // Create a milestone manually
            var m = new Milestone
            {
                MilestoneId = "test123",
                StartUT = 0,
                EndUT = 100,
                LastReplayedEventIndex = 5,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent { ut = 50, eventType = GameStateEventType.TechResearched }
                }
            };
            MilestoneStore.AddMilestoneForTesting(m);

            // Save mutable state
            var node = new ConfigNode("SCENARIO");
            MilestoneStore.SaveMutableState(node);

            // Modify the index
            m.LastReplayedEventIndex = 999;

            // Restore it
            MilestoneStore.RestoreMutableState(node);

            Assert.Equal(5, MilestoneStore.Milestones[0].LastReplayedEventIndex);
        }

        [Fact]
        public void MilestoneStore_SaveLoadMutableState_UnknownId_Ignored()
        {
            var m = new Milestone
            {
                MilestoneId = "known-id",
                StartUT = 0,
                EndUT = 100,
                LastReplayedEventIndex = 3,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent { ut = 50, eventType = GameStateEventType.TechResearched }
                }
            };
            MilestoneStore.AddMilestoneForTesting(m);

            // Build a scenario node with a DIFFERENT milestone ID
            var node = new ConfigNode("SCENARIO");
            var stateNode = node.AddNode("MILESTONE_STATE");
            stateNode.AddValue("id", "unknown-id");
            stateNode.AddValue("lastReplayedIdx", "7");

            MilestoneStore.RestoreMutableState(node);

            // Should be unchanged since the ID didn't match
            Assert.Equal(3, MilestoneStore.Milestones[0].LastReplayedEventIndex);
        }

        [Fact]
        public void MilestoneStore_RestoreMutableState_ResetUnmatched()
        {
            // Milestone created after launch quicksave (not in saved state)
            var m = new Milestone
            {
                MilestoneId = "new-milestone",
                StartUT = 0,
                EndUT = 100,
                LastReplayedEventIndex = 2, // fully applied during normal play
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent { ut = 50, eventType = GameStateEventType.TechResearched },
                    new GameStateEvent { ut = 60, eventType = GameStateEventType.PartPurchased },
                    new GameStateEvent { ut = 70, eventType = GameStateEventType.TechResearched }
                }
            };
            MilestoneStore.AddMilestoneForTesting(m);

            // Empty scenario node (simulates revert to a save before milestone existed)
            var node = new ConfigNode("SCENARIO");

            MilestoneStore.RestoreMutableState(node, resetUnmatched: true);

            // Should be reset to -1 (unreplayed) since this milestone wasn't in the save
            Assert.Equal(-1, MilestoneStore.Milestones[0].LastReplayedEventIndex);
        }

        [Fact]
        public void MilestoneStore_RestoreMutableState_NoResetWithoutFlag()
        {
            var m = new Milestone
            {
                MilestoneId = "new-milestone",
                StartUT = 0,
                EndUT = 100,
                LastReplayedEventIndex = 2,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent { ut = 50, eventType = GameStateEventType.TechResearched },
                    new GameStateEvent { ut = 60, eventType = GameStateEventType.PartPurchased },
                    new GameStateEvent { ut = 70, eventType = GameStateEventType.TechResearched }
                }
            };
            MilestoneStore.AddMilestoneForTesting(m);

            // Empty scenario node — without resetUnmatched, index should stay unchanged
            var node = new ConfigNode("SCENARIO");

            MilestoneStore.RestoreMutableState(node);

            Assert.Equal(2, MilestoneStore.Milestones[0].LastReplayedEventIndex);
        }

        #endregion

        #region FlushPendingEvents

        [Fact]
        public void FlushPendingEvents_CapturesOrphanedEvents()
        {
            // Events that happened without a recording commit
            var techEvt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            };
            GameStateStore.AddEvent(ref techEvt);

            var milestone = MilestoneStore.FlushPendingEvents(100);

            Assert.NotNull(milestone);
            Assert.Single(milestone.Events);
            Assert.Equal(GameStateEventType.TechResearched, milestone.Events[0].eventType);
            Assert.Equal("", milestone.RecordingId); // no recording association
        }

        [Fact]
        public void FlushPendingEvents_NoOpWhenNoNewEvents()
        {
            // Create a milestone that covers UT 0-100
            var techEvt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            };
            GameStateStore.AddEvent(ref techEvt);
            MilestoneStore.CreateMilestone("rec1", 100);

            // No new events after UT 100
            var milestone = MilestoneStore.FlushPendingEvents(200);

            Assert.Null(milestone);
        }

        [Fact]
        public void FlushPendingEvents_OnlyCapturesNewEvents()
        {
            // First milestone covers UT 0-100
            var techEvt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            };
            GameStateStore.AddEvent(ref techEvt);
            MilestoneStore.CreateMilestone("rec1", 100);

            // New event at UT 150
            var partEvt = new GameStateEvent
            {
                ut = 150,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2",
                detail = "cost=600"
            };
            GameStateStore.AddEvent(ref partEvt);

            var milestone = MilestoneStore.FlushPendingEvents(200);

            Assert.NotNull(milestone);
            Assert.Single(milestone.Events);
            Assert.Equal(GameStateEventType.PartPurchased, milestone.Events[0].eventType);
        }

        #endregion

        #region Visibility-Aware StartUT Watermark

        [Fact]
        public void CreateMilestone_StartUT_IgnoresHiddenMilestones()
        {
            AddCommittedRecording("live-rec");
            MilestoneStore.AddMilestoneForTesting(
                MakeCommittedMilestone(
                    "hidden-ms",
                    0,
                    300,
                    new GameStateEvent
                    {
                        ut = 200,
                        eventType = GameStateEventType.TechResearched,
                        key = "oldTech",
                        detail = "cost=10",
                        recordingId = "old-rec"
                    }));

            var newPartEvt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod",
                detail = "cost=600",
                recordingId = "live-rec"
            };
            GameStateStore.AddEvent(ref newPartEvt);

            var m = MilestoneStore.CreateMilestone("rec2", 100);
            Assert.NotNull(m);
            Assert.Single(m.Events);
            Assert.Equal("mk1pod", m.Events[0].key);
        }

        [Fact]
        public void CreateMilestone_StartUT_UsesLatestVisibleEventInPartiallyHiddenMilestone()
        {
            AddCommittedRecording("live-rec");
            MilestoneStore.AddMilestoneForTesting(
                MakeCommittedMilestone(
                    "mixed-ms",
                    0,
                    300,
                    new GameStateEvent
                    {
                        ut = 100,
                        eventType = GameStateEventType.TechResearched,
                        key = "visible-tech",
                        detail = "cost=5",
                        recordingId = "live-rec"
                    },
                    new GameStateEvent
                    {
                        ut = 250,
                        eventType = GameStateEventType.TechResearched,
                        key = "hidden-tech",
                        detail = "cost=10",
                        recordingId = "old-rec"
                    }));

            var newPartEvt = new GameStateEvent
            {
                ut = 150,
                eventType = GameStateEventType.PartPurchased,
                key = "visible-gap-part",
                detail = "cost=600",
                recordingId = "live-rec"
            };
            GameStateStore.AddEvent(ref newPartEvt);

            var m = MilestoneStore.CreateMilestone("rec2", 200);
            Assert.NotNull(m);
            Assert.Equal(100, m.StartUT);
            Assert.Single(m.Events);
            Assert.Equal("visible-gap-part", m.Events[0].key);
        }

        [Fact]
        public void CreateMilestone_StartUT_UsesVisibleMilestones()
        {
            var tech1Evt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "tech1",
                detail = "cost=5"
            };
            GameStateStore.AddEvent(ref tech1Evt);
            MilestoneStore.CreateMilestone("rec1", 100);

            var part1Evt = new GameStateEvent
            {
                ut = 150,
                eventType = GameStateEventType.PartPurchased,
                key = "part1",
                detail = "cost=300"
            };
            GameStateStore.AddEvent(ref part1Evt);
            MilestoneStore.CreateMilestone("rec2", 200);

            var dupEvt = new GameStateEvent
            {
                ut = 80,
                eventType = GameStateEventType.TechResearched,
                key = "duplicateTech",
                detail = "cost=10"
            };
            GameStateStore.AddEvent(ref dupEvt);
            var m = MilestoneStore.CreateMilestone(null, 250);
            // Should be null — no new events after UT 200 watermark
            Assert.Null(m);
        }

        #endregion

        #region GetPendingEventCount

        [Fact]
        public void GetPendingEventCount_CountsVisibleCommittedEvents()
        {
            var techEvt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            };
            GameStateStore.AddEvent(ref techEvt);
            var partEvt = new GameStateEvent
            {
                ut = 60,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2",
                detail = "cost=600"
            };
            GameStateStore.AddEvent(ref partEvt);
            MilestoneStore.CreateMilestone("rec1", 100);

            Assert.Equal(2, MilestoneStore.GetPendingEventCount());
        }

        [Fact]
        public void GetPendingEventCount_ExcludesHiddenMilestones()
        {
            AddCommittedRecording("live-rec");
            MilestoneStore.AddMilestoneForTesting(
                MakeCommittedMilestone(
                    "hidden-ms",
                    0,
                    100,
                    new GameStateEvent
                    {
                        ut = 50,
                        eventType = GameStateEventType.TechResearched,
                        key = "basicRocketry",
                        detail = "cost=5",
                        recordingId = "old-rec"
                    }));
            MilestoneStore.AddMilestoneForTesting(
                MakeCommittedMilestone(
                    "visible-ms",
                    0,
                    100,
                    new GameStateEvent
                    {
                        ut = 50,
                        eventType = GameStateEventType.PartPurchased,
                        key = "mk1pod.v2",
                        detail = "cost=600",
                        recordingId = "live-rec"
                    }));

            Assert.Equal(1, MilestoneStore.GetPendingEventCount());
        }

        [Fact]
        public void GetPendingEventCount_ExcludesUncommittedMilestones()
        {
            // The badge counts pending CAREER work. A provisional milestone's events are
            // not career work yet (the tree carrying them has not been merged), so
            // counting them would show the player work that may never land.
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "provisional-ms",
                StartUT = 0,
                EndUT = 100,
                Committed = false,
                LastReplayedEventIndex = 0,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 50,
                        eventType = GameStateEventType.TechResearched,
                        key = "basicRocketry",
                        detail = "cost=5"
                    }
                }
            });
            MilestoneStore.AddMilestoneForTesting(
                MakeCommittedMilestone(
                    "committed-ms",
                    0,
                    100,
                    new GameStateEvent
                    {
                        ut = 60,
                        eventType = GameStateEventType.PartPurchased,
                        key = "mk1pod.v2",
                        detail = "cost=600"
                    }));

            Assert.Equal(1, MilestoneStore.GetPendingEventCount());
        }

        [Fact]
        public void GetPendingEventCount_ZeroWhenEmpty()
        {
            Assert.Equal(0, MilestoneStore.GetPendingEventCount());
        }

        [Fact]
        public void GetPendingEventCount_ExcludesFilteredEvents()
        {
            var techEvt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            };
            GameStateStore.AddEvent(ref techEvt);
            // CrewStatusChanged should be filtered from milestones by CreateMilestone,
            // but even if present in a deserialized milestone it should not be counted
            MilestoneStore.CreateMilestone("rec1", 100);

            // Manually inject a CrewStatusChanged event into the milestone
            // (simulating a milestone deserialized from an older save)
            MilestoneStore.Milestones[0].Events.Add(new GameStateEvent
            {
                ut = 55,
                eventType = GameStateEventType.CrewStatusChanged,
                key = "Jeb Kerman",
                detail = "from=Available;to=Assigned"
            });

            // Only 1 counted — the injected CrewStatusChanged is filtered
            Assert.Equal(1, MilestoneStore.GetPendingEventCount());
        }

        #endregion
    }
}
