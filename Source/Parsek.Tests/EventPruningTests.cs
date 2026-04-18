using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class EventPruningTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public EventPruningTests()
        {
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        [Fact]
        public void PruneProcessedEvents_RemovesOldEpochEvents()
        {
            // Add events with epoch 0
            MilestoneStore.CurrentEpoch = 0;
            var evt1 = new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.TechResearched,
                key = "basicRocketry"
            };
            GameStateStore.AddEvent(ref evt1);
            var evt2 = new GameStateEvent
            {
                ut = 200,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod.v2"
            };
            GameStateStore.AddEvent(ref evt2);

            // Switch to epoch 1 — old-epoch events should be pruned
            MilestoneStore.CurrentEpoch = 1;

            int pruned = GameStateStore.PruneProcessedEvents();

            Assert.Equal(2, pruned);
            Assert.Equal(0, GameStateStore.EventCount);
        }

        [Fact]
        public void PruneProcessedEvents_RemovesEventsAtOrBelowThreshold()
        {
            MilestoneStore.CurrentEpoch = 0;

            // Add events at UT 50, 100, 150
            var evt1 = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "tech1"
            };
            GameStateStore.AddEvent(ref evt1);
            var evt2 = new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.TechResearched,
                key = "tech2"
            };
            GameStateStore.AddEvent(ref evt2);
            var evt3 = new GameStateEvent
            {
                ut = 150,
                eventType = GameStateEventType.TechResearched,
                key = "tech3"
            };
            GameStateStore.AddEvent(ref evt3);

            // Create a milestone covering up to UT 100
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m1",
                StartUT = 0,
                EndUT = 100,
                Epoch = 0,
                Committed = true,
                Events = new List<GameStateEvent>()
            });

            int pruned = GameStateStore.PruneProcessedEvents();

            // UT 50 and 100 should be pruned (ut <= threshold of 100)
            Assert.Equal(2, pruned);
            Assert.Equal(1, GameStateStore.EventCount);
            // Remaining event should be at UT 150
            Assert.Equal(150, GameStateStore.Events[0].ut);
        }

        [Fact]
        public void PruneProcessedEvents_PreservesEventsAboveThreshold()
        {
            MilestoneStore.CurrentEpoch = 0;

            // Add events above the milestone threshold
            var evt1 = new GameStateEvent
            {
                ut = 200,
                eventType = GameStateEventType.ContractAccepted,
                key = "contract1"
            };
            GameStateStore.AddEvent(ref evt1);
            var evt2 = new GameStateEvent
            {
                ut = 300,
                eventType = GameStateEventType.ContractCompleted,
                key = "contract2"
            };
            GameStateStore.AddEvent(ref evt2);

            // Milestone ends at UT 100 — both events are above threshold
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m1",
                StartUT = 0,
                EndUT = 100,
                Epoch = 0,
                Committed = true,
                Events = new List<GameStateEvent>()
            });

            int pruned = GameStateStore.PruneProcessedEvents();

            Assert.Equal(0, pruned);
            Assert.Equal(2, GameStateStore.EventCount);
        }

        [Fact]
        public void PruneProcessedEvents_EmptyStore_NoOp()
        {
            MilestoneStore.CurrentEpoch = 0;

            int pruned = GameStateStore.PruneProcessedEvents();

            Assert.Equal(0, pruned);
            Assert.Equal(0, GameStateStore.EventCount);
        }

        [Fact]
        public void PruneProcessedEvents_ReturnsCorrectCount()
        {
            MilestoneStore.CurrentEpoch = 0;

            // Add 5 events, milestone covers UT 0-300, so 3 at or below threshold
            var evt1 = new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.TechResearched,
                key = "t1"
            };
            GameStateStore.AddEvent(ref evt1);
            var evt2 = new GameStateEvent
            {
                ut = 200,
                eventType = GameStateEventType.TechResearched,
                key = "t2"
            };
            GameStateStore.AddEvent(ref evt2);
            var evt3 = new GameStateEvent
            {
                ut = 300,
                eventType = GameStateEventType.TechResearched,
                key = "t3"
            };
            GameStateStore.AddEvent(ref evt3);
            var evt4 = new GameStateEvent
            {
                ut = 400,
                eventType = GameStateEventType.TechResearched,
                key = "t4"
            };
            GameStateStore.AddEvent(ref evt4);
            var evt5 = new GameStateEvent
            {
                ut = 500,
                eventType = GameStateEventType.TechResearched,
                key = "t5"
            };
            GameStateStore.AddEvent(ref evt5);

            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m1",
                StartUT = 0,
                EndUT = 300,
                Epoch = 0,
                Committed = true,
                Events = new List<GameStateEvent>()
            });

            int pruned = GameStateStore.PruneProcessedEvents();

            Assert.Equal(3, pruned);
            Assert.Equal(2, GameStateStore.EventCount);
        }

        [Fact]
        public void GetLatestCommittedEndUT_NoMilestones_ReturnsZero()
        {
            MilestoneStore.CurrentEpoch = 0;

            double result = MilestoneStore.GetLatestCommittedEndUT();

            Assert.Equal(0, result);
        }

        [Fact]
        public void GetLatestCommittedEndUT_ReturnsHighestEndUT()
        {
            MilestoneStore.CurrentEpoch = 0;

            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m1",
                StartUT = 0,
                EndUT = 100,
                Epoch = 0,
                Committed = true,
                Events = new List<GameStateEvent>()
            });
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m2",
                StartUT = 100,
                EndUT = 500,
                Epoch = 0,
                Committed = true,
                Events = new List<GameStateEvent>()
            });
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m3",
                StartUT = 500,
                EndUT = 300,
                Epoch = 0,
                Committed = true,
                Events = new List<GameStateEvent>()
            });

            double result = MilestoneStore.GetLatestCommittedEndUT();

            Assert.Equal(500, result);
        }

        [Fact]
        public void GetLatestCommittedEndUT_IgnoresOtherEpochs()
        {
            MilestoneStore.CurrentEpoch = 1;

            // Milestone in epoch 0 — should be ignored
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m1",
                StartUT = 0,
                EndUT = 9999,
                Epoch = 0,
                Committed = true,
                Events = new List<GameStateEvent>()
            });
            // Milestone in current epoch 1
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m2",
                StartUT = 0,
                EndUT = 200,
                Epoch = 1,
                Committed = true,
                Events = new List<GameStateEvent>()
            });

            double result = MilestoneStore.GetLatestCommittedEndUT();

            Assert.Equal(200, result);
        }

        [Fact]
        public void PruneProcessedEvents_LogsWhenEventsPruned()
        {
            ParsekLog.SuppressLogging = false;
            MilestoneStore.CurrentEpoch = 0;

            var evt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "tech1"
            };
            GameStateStore.AddEvent(ref evt);

            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m1",
                StartUT = 0,
                EndUT = 100,
                Epoch = 0,
                Committed = true,
                Events = new List<GameStateEvent>()
            });

            GameStateStore.PruneProcessedEvents();

            Assert.Contains(logLines, l =>
                l.Contains("[GameStateStore]") && l.Contains("PruneProcessedEvents"));
        }

        [Fact]
        public void PruneProcessedEvents_MixedEpochsAndThreshold()
        {
            // Event in old epoch (epoch 0) — should be pruned
            MilestoneStore.CurrentEpoch = 0;
            var oldEpochEvt = new GameStateEvent
            {
                ut = 999,
                eventType = GameStateEventType.TechResearched,
                key = "old_epoch_tech"
            };
            GameStateStore.AddEvent(ref oldEpochEvt);

            // Switch to epoch 1, add events at various UTs
            MilestoneStore.CurrentEpoch = 1;
            var belowEvt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "below_threshold"
            };
            GameStateStore.AddEvent(ref belowEvt);
            var aboveEvt = new GameStateEvent
            {
                ut = 200,
                eventType = GameStateEventType.TechResearched,
                key = "above_threshold"
            };
            GameStateStore.AddEvent(ref aboveEvt);

            // Milestone in epoch 1 up to UT 100
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m1",
                StartUT = 0,
                EndUT = 100,
                Epoch = 1,
                Committed = true,
                Events = new List<GameStateEvent>()
            });

            int pruned = GameStateStore.PruneProcessedEvents();

            // old epoch event (epoch 0) + below threshold event (ut 50) = 2 pruned
            Assert.Equal(2, pruned);
            Assert.Equal(1, GameStateStore.EventCount);
            Assert.Equal("above_threshold", GameStateStore.Events[0].key);
        }
    }
}
