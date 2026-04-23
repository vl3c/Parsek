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
            RecordingStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
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
            double endUT,
            string recordingId = "")
        {
            return new Milestone
            {
                MilestoneId = id,
                StartUT = 0,
                EndUT = endUT,
                Epoch = 0,
                Committed = true,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = endUT,
                        eventType = GameStateEventType.TechResearched,
                        key = id + "-seed",
                        recordingId = recordingId ?? ""
                    }
                }
            };
        }

        [Fact]
        public void PruneProcessedEvents_PreservesHiddenOldBranchEvents()
        {
            AddCommittedRecording("live-rec");

            var hiddenOldBranch = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "old-branch-tech",
                recordingId = "old-rec"
            };
            GameStateStore.AddEvent(ref hiddenOldBranch);
            var liveEvt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.PartPurchased,
                key = "live-part",
                recordingId = "live-rec"
            };
            GameStateStore.AddEvent(ref liveEvt);

            MilestoneStore.AddMilestoneForTesting(
                MakeCommittedMilestone("m1", endUT: 100, recordingId: "live-rec"));

            int pruned = GameStateStore.PruneProcessedEvents();

            Assert.Equal(1, pruned);
            Assert.Equal(1, GameStateStore.EventCount);
            Assert.Equal("old-branch-tech", GameStateStore.Events[0].key);
        }

        [Fact]
        public void PruneProcessedEvents_RemovesEventsAtOrBelowThreshold()
        {
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
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 100,
                        eventType = GameStateEventType.TechResearched,
                        key = "m1-seed"
                    }
                }
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
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 100,
                        eventType = GameStateEventType.TechResearched,
                        key = "m1-seed"
                    }
                }
            });

            int pruned = GameStateStore.PruneProcessedEvents();

            Assert.Equal(0, pruned);
            Assert.Equal(2, GameStateStore.EventCount);
        }

        [Fact]
        public void PruneProcessedEvents_EmptyStore_NoOp()
        {
            int pruned = GameStateStore.PruneProcessedEvents();

            Assert.Equal(0, pruned);
            Assert.Equal(0, GameStateStore.EventCount);
        }

        [Fact]
        public void PruneProcessedEvents_ReturnsCorrectCount()
        {
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
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 300,
                        eventType = GameStateEventType.TechResearched,
                        key = "m1-seed"
                    }
                }
            });

            int pruned = GameStateStore.PruneProcessedEvents();

            Assert.Equal(3, pruned);
            Assert.Equal(2, GameStateStore.EventCount);
        }

        [Fact]
        public void GetLatestCommittedEndUT_NoMilestones_ReturnsZero()
        {
            double result = MilestoneStore.GetLatestCommittedEndUT();

            Assert.Equal(0, result);
        }

        [Fact]
        public void GetLatestCommittedEndUT_ReturnsHighestEndUT()
        {
            MilestoneStore.AddMilestoneForTesting(MakeCommittedMilestone("m1", endUT: 100));
            MilestoneStore.AddMilestoneForTesting(MakeCommittedMilestone("m2", endUT: 500));
            MilestoneStore.AddMilestoneForTesting(MakeCommittedMilestone("m3", endUT: 300));

            double result = MilestoneStore.GetLatestCommittedEndUT();

            Assert.Equal(500, result);
        }

        [Fact]
        public void GetLatestCommittedEndUT_IgnoresHiddenMilestones()
        {
            AddCommittedRecording("live-rec");
            MilestoneStore.AddMilestoneForTesting(
                MakeCommittedMilestone("hidden", endUT: 9999, recordingId: "old-rec"));
            MilestoneStore.AddMilestoneForTesting(
                MakeCommittedMilestone("visible", endUT: 200, recordingId: "live-rec"));

            double result = MilestoneStore.GetLatestCommittedEndUT();

            Assert.Equal(200, result);
        }

        [Fact]
        public void GetLatestCommittedEndUT_PartiallyHiddenMilestone_UsesLatestVisibleEventUT()
        {
            AddCommittedRecording("live-rec");
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "mixed",
                StartUT = 0,
                EndUT = 300,
                Epoch = 0,
                Committed = true,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 100,
                        eventType = GameStateEventType.TechResearched,
                        key = "live-tech",
                        recordingId = "live-rec"
                    },
                    new GameStateEvent
                    {
                        ut = 250,
                        eventType = GameStateEventType.TechResearched,
                        key = "hidden-tech",
                        recordingId = "old-rec"
                    }
                }
            });

            double result = MilestoneStore.GetLatestCommittedEndUT();

            Assert.Equal(100, result);
        }

        [Fact]
        public void PruneProcessedEvents_LogsWhenEventsPruned()
        {
            ParsekLog.SuppressLogging = false;

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
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 100,
                        eventType = GameStateEventType.TechResearched,
                        key = "m1-seed"
                    }
                }
            });

            GameStateStore.PruneProcessedEvents();

            Assert.Contains(logLines, l =>
                l.Contains("[GameStateStore]") && l.Contains("PruneProcessedEvents"));
        }

        [Fact]
        public void PruneProcessedEvents_MixedVisibilityAndThreshold()
        {
            AddCommittedRecording("live-rec");

            var hiddenEvt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "old-branch-tech",
                recordingId = "old-rec"
            };
            GameStateStore.AddEvent(ref hiddenEvt);

            var belowEvt = new GameStateEvent
            {
                ut = 50,
                eventType = GameStateEventType.TechResearched,
                key = "below_threshold",
                recordingId = "live-rec"
            };
            GameStateStore.AddEvent(ref belowEvt);
            var aboveEvt = new GameStateEvent
            {
                ut = 200,
                eventType = GameStateEventType.TechResearched,
                key = "above_threshold",
                recordingId = "live-rec"
            };
            GameStateStore.AddEvent(ref aboveEvt);

            MilestoneStore.AddMilestoneForTesting(
                MakeCommittedMilestone("m1", endUT: 100, recordingId: "live-rec"));

            int pruned = GameStateStore.PruneProcessedEvents();

            Assert.Equal(1, pruned);
            Assert.Equal(2, GameStateStore.EventCount);
            Assert.Contains(GameStateStore.Events, e => e.key == "old-branch-tech");
            Assert.Contains(GameStateStore.Events, e => e.key == "above_threshold");
        }
    }
}
