using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ActionReplayTests
    {
        private readonly List<string> logLines = new List<string>();

        public ActionReplayTests()
        {
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            // Ensure flags start clean
            GameStateRecorder.SuppressActionReplay = false;
            GameStateRecorder.SuppressBlockingPatches = false;
            GameStateRecorder.SuppressCrewEvents = false;
        }

        private void Cleanup()
        {
            ParsekLog.TestSinkForTesting = null;
            ParsekLog.VerboseOverrideForTesting = null;
            GameStateRecorder.SuppressActionReplay = false;
            GameStateRecorder.SuppressBlockingPatches = false;
            GameStateRecorder.SuppressCrewEvents = false;
        }

        #region IsReplayableEvent

        [Fact]
        public void IsReplayableEvent_TechResearched_ReturnsTrue()
        {
            Assert.True(ActionReplay.IsReplayableEvent(GameStateEventType.TechResearched));
        }

        [Fact]
        public void IsReplayableEvent_PartPurchased_ReturnsTrue()
        {
            Assert.True(ActionReplay.IsReplayableEvent(GameStateEventType.PartPurchased));
        }

        [Fact]
        public void IsReplayableEvent_FacilityUpgraded_ReturnsTrue()
        {
            Assert.True(ActionReplay.IsReplayableEvent(GameStateEventType.FacilityUpgraded));
        }

        [Fact]
        public void IsReplayableEvent_CrewHired_ReturnsTrue()
        {
            Assert.True(ActionReplay.IsReplayableEvent(GameStateEventType.CrewHired));
        }

        [Fact]
        public void IsReplayableEvent_FundsChanged_ReturnsFalse()
        {
            Assert.False(ActionReplay.IsReplayableEvent(GameStateEventType.FundsChanged));
        }

        [Fact]
        public void IsReplayableEvent_ContractAccepted_ReturnsFalse()
        {
            Assert.False(ActionReplay.IsReplayableEvent(GameStateEventType.ContractAccepted));
        }

        #endregion

        #region ReplayCommittedActions

        [Fact]
        public void ReplayCommittedActions_EmptyList_NoOp()
        {
            try
            {
                ActionReplay.ReplayCommittedActions(new List<Milestone>());
                // Should not crash and should not log anything
                Assert.Empty(logLines);
            }
            finally
            {
                Cleanup();
            }
        }

        [Fact]
        public void ReplayCommittedActions_FullyReplayed_NoOp()
        {
            try
            {
                var milestone = new Milestone
                {
                    MilestoneId = "test-fully-replayed",
                    Committed = true,
                    Events = new List<GameStateEvent>
                    {
                        new GameStateEvent
                        {
                            ut = 100,
                            eventType = GameStateEventType.TechResearched,
                            key = "basicRocketry"
                        },
                        new GameStateEvent
                        {
                            ut = 200,
                            eventType = GameStateEventType.PartPurchased,
                            key = "mk1pod.v2"
                        }
                    },
                    LastReplayedEventIndex = 1 // Events.Count - 1
                };

                var milestones = new List<Milestone> { milestone };
                ActionReplay.ReplayCommittedActions(milestones);

                // No unreplayed actions — should return early without logging
                Assert.Empty(logLines);
            }
            finally
            {
                Cleanup();
            }
        }

        [Fact]
        public void ReplayCommittedActions_UncommittedMilestone_Skipped()
        {
            try
            {
                var milestone = new Milestone
                {
                    MilestoneId = "test-uncommitted",
                    Committed = false,
                    Events = new List<GameStateEvent>
                    {
                        new GameStateEvent
                        {
                            ut = 100,
                            eventType = GameStateEventType.TechResearched,
                            key = "basicRocketry"
                        }
                    },
                    LastReplayedEventIndex = -1
                };

                var milestones = new List<Milestone> { milestone };
                ActionReplay.ReplayCommittedActions(milestones);

                // Uncommitted milestone — 0 actions, no log output
                Assert.Empty(logLines);
            }
            finally
            {
                Cleanup();
            }
        }

        [Fact]
        public void ReplayCommittedActions_CountsReplayableEvents()
        {
            try
            {
                var milestone = new Milestone
                {
                    MilestoneId = "test-counts",
                    Committed = true,
                    Events = new List<GameStateEvent>
                    {
                        new GameStateEvent
                        {
                            ut = 100,
                            eventType = GameStateEventType.TechResearched,
                            key = "basicRocketry"
                        },
                        new GameStateEvent
                        {
                            ut = 200,
                            eventType = GameStateEventType.TechResearched,
                            key = "stability"
                        },
                        new GameStateEvent
                        {
                            ut = 300,
                            eventType = GameStateEventType.PartPurchased,
                            key = "mk1pod.v2"
                        },
                        new GameStateEvent
                        {
                            ut = 400,
                            eventType = GameStateEventType.FundsChanged,
                            key = "ContractReward"
                        }
                    },
                    LastReplayedEventIndex = -1
                };

                var milestones = new List<Milestone> { milestone };
                ActionReplay.ReplayCommittedActions(milestones);

                // Should log "3 unreplayed actions" (2 tech + 1 part, FundsChanged is not replayable)
                bool foundActionCount = false;
                foreach (var line in logLines)
                {
                    if (line.Contains("3 unreplayed actions"))
                    {
                        foundActionCount = true;
                        break;
                    }
                }
                Assert.True(foundActionCount,
                    $"Expected log containing '3 unreplayed actions'. Log lines:\n{string.Join("\n", logLines)}");
            }
            finally
            {
                Cleanup();
            }
        }

        [Fact]
        public void ReplayCommittedActions_NullList_NoOp()
        {
            ActionReplay.ReplayCommittedActions(null);
            // No crash = success
        }

        [Fact]
        public void ReplayCommittedActions_PartiallyReplayed_CountsOnlyUnreplayed()
        {
            try
            {
                var milestone = new Milestone
                {
                    MilestoneId = "test-partial",
                    Committed = true,
                    Events = new List<GameStateEvent>
                    {
                        // [0] already replayed
                        new GameStateEvent
                        {
                            ut = 100,
                            eventType = GameStateEventType.TechResearched,
                            key = "basicRocketry"
                        },
                        // [1] already replayed
                        new GameStateEvent
                        {
                            ut = 200,
                            eventType = GameStateEventType.FundsChanged,
                            key = "ContractReward"
                        },
                        // [2] already replayed
                        new GameStateEvent
                        {
                            ut = 300,
                            eventType = GameStateEventType.PartPurchased,
                            key = "mk1pod.v2"
                        },
                        // [3] unreplayed, replayable
                        new GameStateEvent
                        {
                            ut = 400,
                            eventType = GameStateEventType.TechResearched,
                            key = "stability"
                        },
                        // [4] unreplayed, not replayable
                        new GameStateEvent
                        {
                            ut = 500,
                            eventType = GameStateEventType.ContractAccepted,
                            key = "SatelliteContract"
                        }
                    },
                    LastReplayedEventIndex = 2
                };

                var milestones = new List<Milestone> { milestone };
                ActionReplay.ReplayCommittedActions(milestones);

                // Events [3] and [4] are unreplayed; only [3] is replayable
                bool foundActionCount = false;
                foreach (var line in logLines)
                {
                    if (line.Contains("1 unreplayed actions"))
                    {
                        foundActionCount = true;
                        break;
                    }
                }
                Assert.True(foundActionCount,
                    $"Expected log containing '1 unreplayed actions'. Log lines:\n{string.Join("\n", logLines)}");
            }
            finally
            {
                Cleanup();
            }
        }

        [Fact]
        public void ReplayCommittedActions_FlagsRestoredAfterCall()
        {
            try
            {
                var milestone = new Milestone
                {
                    MilestoneId = "test-flags",
                    Committed = true,
                    Events = new List<GameStateEvent>
                    {
                        new GameStateEvent
                        {
                            ut = 100,
                            eventType = GameStateEventType.TechResearched,
                            key = "basicRocketry"
                        }
                    },
                    LastReplayedEventIndex = -1
                };

                var milestones = new List<Milestone> { milestone };
                ActionReplay.ReplayCommittedActions(milestones);

                // All suppress flags must be false after the call
                Assert.False(GameStateRecorder.SuppressActionReplay,
                    "SuppressActionReplay should be false after replay");
                Assert.False(GameStateRecorder.SuppressBlockingPatches,
                    "SuppressBlockingPatches should be false after replay");
                Assert.False(GameStateRecorder.SuppressCrewEvents,
                    "SuppressCrewEvents should be false after replay");
            }
            finally
            {
                Cleanup();
            }
        }

        #endregion
    }
}
