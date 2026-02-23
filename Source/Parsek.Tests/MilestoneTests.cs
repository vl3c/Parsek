using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class MilestoneTests
    {
        public MilestoneTests()
        {
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;
        }

        #region Milestone Serialization

        [Fact]
        public void Milestone_SerializationRoundtrip()
        {
            var original = new Milestone
            {
                MilestoneId = "abc123def456",
                StartUT = 0,
                EndUT = 17090.5,
                RecordingId = "rec789",
                Epoch = 2,
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
                        epoch = 2
                    },
                    new GameStateEvent
                    {
                        ut = 17080,
                        eventType = GameStateEventType.PartPurchased,
                        key = "mk1pod.v2",
                        detail = "cost=600",
                        epoch = 2
                    }
                }
            };

            var node = new ConfigNode("MILESTONE");
            original.SerializeInto(node);

            var restored = Milestone.DeserializeFrom(node);

            Assert.Equal("abc123def456", restored.MilestoneId);
            Assert.Equal(0, restored.StartUT);
            Assert.Equal(17090.5, restored.EndUT);
            Assert.Equal("rec789", restored.RecordingId);
            Assert.Equal(2u, restored.Epoch);
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
                Epoch = 0,
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
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            });

            var milestone = MilestoneStore.CreateMilestone("rec1", 200);

            Assert.NotNull(milestone);
            Assert.Equal(0, milestone.StartUT); // no previous milestone
            Assert.Equal(200, milestone.EndUT);
        }

        [Fact]
        public void MilestoneStore_CreateMilestone_CopiesEventsInRange()
        {
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            });
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2",
                detail = "cost=600"
            });

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
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            });
            var m1 = MilestoneStore.CreateMilestone("rec1", 100);
            Assert.NotNull(m1);

            // Add event at UT 150 — should be in second milestone, not first
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 150,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2",
                detail = "cost=600"
            });

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
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            });
            var m1 = MilestoneStore.CreateMilestone("rec1", 100);

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 150,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2",
                detail = "cost=600"
            });
            var m2 = MilestoneStore.CreateMilestone("rec2", 200);

            Assert.NotNull(m1);
            Assert.NotNull(m2);
            Assert.Equal(0, m1.StartUT);
            Assert.Equal(100, m1.EndUT);
            Assert.Equal(100, m2.StartUT); // starts where first ended
            Assert.Equal(200, m2.EndUT);
        }

        [Fact]
        public void MilestoneStore_CreateMilestone_FiltersCurrentEpochOnly()
        {
            // Add event at epoch 0
            MilestoneStore.CurrentEpoch = 0;
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            });

            // Switch to epoch 1 (simulating revert)
            MilestoneStore.CurrentEpoch = 1;
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 60,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2",
                detail = "cost=600"
            });

            var milestone = MilestoneStore.CreateMilestone("rec1", 100);

            Assert.NotNull(milestone);
            // Should only have the epoch=1 event
            Assert.Single(milestone.Events);
            Assert.Equal(GameStateEventType.PartPurchased, milestone.Events[0].eventType);
        }

        [Fact]
        public void MilestoneStore_CreateMilestone_ExcludesRawResourceEvents()
        {
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            });
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 51,
                eventType = GameStateEventType.FundsChanged,
                key = "TechCost",
                valueBefore = 10000,
                valueAfter = 9995
            });
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 52,
                eventType = GameStateEventType.ScienceChanged,
                key = "TechCost",
                valueBefore = 100,
                valueAfter = 95
            });
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 53,
                eventType = GameStateEventType.ReputationChanged,
                key = "Unknown",
                valueBefore = 50,
                valueAfter = 55
            });

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
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 80,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2",
                detail = "cost=600"
            });
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            });

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
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            });
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 60,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2",
                detail = "cost=600"
            });

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
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            });

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
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            });
            MilestoneStore.CreateMilestone("rec1", 100);

            // No new events after UT 100
            var milestone = MilestoneStore.FlushPendingEvents(200);

            Assert.Null(milestone);
        }

        [Fact]
        public void FlushPendingEvents_OnlyCapturesNewEvents()
        {
            // First milestone covers UT 0-100
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                detail = "cost=5"
            });
            MilestoneStore.CreateMilestone("rec1", 100);

            // New event at UT 150
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 150,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2",
                detail = "cost=600"
            });

            var milestone = MilestoneStore.FlushPendingEvents(200);

            Assert.NotNull(milestone);
            Assert.Single(milestone.Events);
            Assert.Equal(GameStateEventType.PartPurchased, milestone.Events[0].eventType);
        }

        #endregion

        #region GameStateEvent Epoch Field

        [Fact]
        public void GameStateEvent_EpochField_SerializationRoundtrip()
        {
            var original = new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry",
                epoch = 3
            };

            var node = new ConfigNode("GAME_STATE_EVENT");
            original.SerializeInto(node);

            var restored = GameStateEvent.DeserializeFrom(node);
            Assert.Equal(3u, restored.epoch);
        }

        [Fact]
        public void GameStateEvent_EpochField_DefaultsToZero()
        {
            // Old events without epoch field should default to 0
            var node = new ConfigNode("GAME_STATE_EVENT");
            node.AddValue("ut", "100");
            node.AddValue("type", "6"); // TechResearched

            var restored = GameStateEvent.DeserializeFrom(node);
            Assert.Equal(0u, restored.epoch);
        }

        [Fact]
        public void GameStateStore_AddEvent_StampsCurrentEpoch()
        {
            GameStateStore.ResetForTesting();
            MilestoneStore.CurrentEpoch = 5;

            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.TechResearched,
                key = "test"
            });

            Assert.Equal(5u, GameStateStore.Events[0].epoch);

            // Restore
            MilestoneStore.CurrentEpoch = 0;
        }

        #endregion
    }
}
