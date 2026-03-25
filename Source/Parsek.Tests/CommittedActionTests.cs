using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class CommittedActionTests : System.IDisposable
    {
        public CommittedActionTests()
        {
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
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

        #region GetCommittedTechIds

        [Fact]
        public void GetCommittedTechIds_EmptyWhenNoMilestones()
        {
            var ids = MilestoneStore.GetCommittedTechIds();
            Assert.Empty(ids);
        }

        [Fact]
        public void GetCommittedTechIds_ReturnsUnreplayedTech()
        {
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m1",
                Committed = true,
                LastReplayedEventIndex = -1,
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

            var ids = MilestoneStore.GetCommittedTechIds();
            Assert.Single(ids);
            Assert.Contains("basicRocketry", ids);
        }

        [Fact]
        public void GetCommittedTechIds_ExcludesReplayedTech()
        {
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m1",
                Committed = true,
                LastReplayedEventIndex = 0, // event at index 0 is replayed
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 50,
                        eventType = GameStateEventType.TechResearched,
                        key = "basicRocketry"
                    }
                }
            });

            var ids = MilestoneStore.GetCommittedTechIds();
            Assert.Empty(ids);
        }

        [Fact]
        public void GetCommittedTechIds_ExcludesUncommittedMilestones()
        {
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m1",
                Committed = false,
                LastReplayedEventIndex = -1,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 50,
                        eventType = GameStateEventType.TechResearched,
                        key = "basicRocketry"
                    }
                }
            });

            var ids = MilestoneStore.GetCommittedTechIds();
            Assert.Empty(ids);
        }

        [Fact]
        public void GetCommittedTechIds_MultipleMilestones()
        {
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m1",
                Committed = true,
                LastReplayedEventIndex = -1,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        eventType = GameStateEventType.TechResearched,
                        key = "basicRocketry"
                    }
                }
            });
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m2",
                Committed = true,
                LastReplayedEventIndex = -1,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        eventType = GameStateEventType.TechResearched,
                        key = "generalRocketry"
                    },
                    new GameStateEvent
                    {
                        eventType = GameStateEventType.PartPurchased,
                        key = "mk1pod.v2"
                    }
                }
            });

            var ids = MilestoneStore.GetCommittedTechIds();
            Assert.Equal(2, ids.Count);
            Assert.Contains("basicRocketry", ids);
            Assert.Contains("generalRocketry", ids);
        }

        [Fact]
        public void GetCommittedTechIds_PartialReplay_OnlyUnreplayedReturned()
        {
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m1",
                Committed = true,
                LastReplayedEventIndex = 0, // first event replayed
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        eventType = GameStateEventType.TechResearched,
                        key = "basicRocketry" // replayed (index 0)
                    },
                    new GameStateEvent
                    {
                        eventType = GameStateEventType.TechResearched,
                        key = "generalRocketry" // unreplayed (index 1)
                    }
                }
            });

            var ids = MilestoneStore.GetCommittedTechIds();
            Assert.Single(ids);
            Assert.Contains("generalRocketry", ids);
        }

        #endregion

        #region GetCommittedFacilityUpgrades

        [Fact]
        public void GetCommittedFacilityUpgrades_EmptyWhenNoMilestones()
        {
            var ids = MilestoneStore.GetCommittedFacilityUpgrades();
            Assert.Empty(ids);
        }

        [Fact]
        public void GetCommittedFacilityUpgrades_ReturnsUnreplayedUpgrades()
        {
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m1",
                Committed = true,
                LastReplayedEventIndex = -1,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        eventType = GameStateEventType.FacilityUpgraded,
                        key = "SpaceCenter/LaunchPad"
                    }
                }
            });

            var ids = MilestoneStore.GetCommittedFacilityUpgrades();
            Assert.Single(ids);
            Assert.Contains("SpaceCenter/LaunchPad", ids);
        }

        [Fact]
        public void GetCommittedFacilityUpgrades_ExcludesReplayed()
        {
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m1",
                Committed = true,
                LastReplayedEventIndex = 0,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        eventType = GameStateEventType.FacilityUpgraded,
                        key = "SpaceCenter/LaunchPad"
                    }
                }
            });

            var ids = MilestoneStore.GetCommittedFacilityUpgrades();
            Assert.Empty(ids);
        }

        #endregion

        #region FindCommittedEvent

        [Fact]
        public void FindCommittedEvent_ReturnsMatch()
        {
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m1",
                Committed = true,
                LastReplayedEventIndex = -1,
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

            var ev = MilestoneStore.FindCommittedEvent(
                GameStateEventType.TechResearched, "basicRocketry");
            Assert.True(ev.HasValue);
            Assert.Equal(50, ev.Value.ut);
            Assert.Equal("cost=5", ev.Value.detail);
        }

        [Fact]
        public void FindCommittedEvent_ReturnsNull_WhenNotFound()
        {
            var ev = MilestoneStore.FindCommittedEvent(
                GameStateEventType.TechResearched, "nonExistent");
            Assert.False(ev.HasValue);
        }

        [Fact]
        public void FindCommittedEvent_SkipsReplayedEvents()
        {
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m1",
                Committed = true,
                LastReplayedEventIndex = 0,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 50,
                        eventType = GameStateEventType.TechResearched,
                        key = "basicRocketry"
                    }
                }
            });

            var ev = MilestoneStore.FindCommittedEvent(
                GameStateEventType.TechResearched, "basicRocketry");
            Assert.False(ev.HasValue);
        }

        [Fact]
        public void FindCommittedEvent_SkipsUncommittedMilestones()
        {
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m1",
                Committed = false,
                LastReplayedEventIndex = -1,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 50,
                        eventType = GameStateEventType.TechResearched,
                        key = "basicRocketry"
                    }
                }
            });

            var ev = MilestoneStore.FindCommittedEvent(
                GameStateEventType.TechResearched, "basicRocketry");
            Assert.False(ev.HasValue);
        }

        [Fact]
        public void FindCommittedEvent_FacilityUpgrade()
        {
            MilestoneStore.AddMilestoneForTesting(new Milestone
            {
                MilestoneId = "m1",
                Committed = true,
                LastReplayedEventIndex = -1,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 80,
                        eventType = GameStateEventType.FacilityUpgraded,
                        key = "SpaceCenter/LaunchPad",
                        valueBefore = 0,
                        valueAfter = 0.5
                    }
                }
            });

            var ev = MilestoneStore.FindCommittedEvent(
                GameStateEventType.FacilityUpgraded, "SpaceCenter/LaunchPad");
            Assert.True(ev.HasValue);
            Assert.Equal(80, ev.Value.ut);
        }

        #endregion
    }
}
