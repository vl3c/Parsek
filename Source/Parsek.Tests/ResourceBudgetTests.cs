using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ResourceBudgetTests
    {
        public ResourceBudgetTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            ResourceBudget.Invalidate();
            ParsekLog.SuppressLogging = true;
        }

        private RecordingStore.Recording MakeRecording(
            double preLaunchFunds, double endFunds,
            double preLaunchScience = 0, double endScience = 0,
            float preLaunchRep = 0, float endRep = 0,
            int lastAppliedResIdx = -1)
        {
            var rec = new RecordingStore.Recording
            {
                PreLaunchFunds = preLaunchFunds,
                PreLaunchScience = preLaunchScience,
                PreLaunchReputation = preLaunchRep,
                LastAppliedResourceIndex = lastAppliedResIdx
            };

            // Point[0]: post-launch (vessel cost deducted)
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100,
                funds = preLaunchFunds - 5000, // 5000 launch cost
                science = (float)preLaunchScience,
                reputation = preLaunchRep,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            });

            // Point[last]: end state
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 200,
                funds = endFunds,
                science = (float)endScience,
                reputation = endRep,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            });

            return rec;
        }

        #region Recording Cost Calculations

        [Fact]
        public void CommittedFundsCost_LaunchDeduction()
        {
            // preLaunch=50000, end=35000 → total impact=15000, unplayed → cost=15000
            var rec = MakeRecording(50000, 35000);

            double cost = ResourceBudget.CommittedFundsCost(rec);

            Assert.Equal(15000, cost);
        }

        [Fact]
        public void CommittedFundsCost_MissionProfit()
        {
            // preLaunch=50000, end=60000 → total impact=-10000 (profit)
            var rec = MakeRecording(50000, 60000);

            double cost = ResourceBudget.CommittedFundsCost(rec);

            Assert.Equal(-10000, cost);
        }

        [Fact]
        public void CommittedFundsCost_ZeroPreLaunch()
        {
            // Backward compat: old recordings with no PreLaunch data
            var rec = MakeRecording(0, 0);

            double cost = ResourceBudget.CommittedFundsCost(rec);

            Assert.Equal(0, cost);
        }

        [Fact]
        public void FullyReplayed_NotDoubleCounted()
        {
            var rec = MakeRecording(50000, 35000, lastAppliedResIdx: 1); // fully applied (idx == last)

            double cost = ResourceBudget.CommittedFundsCost(rec);

            Assert.Equal(0, cost);
        }

        [Fact]
        public void TotalCommitted_MultipleRecordings()
        {
            var rec1 = MakeRecording(50000, 35000); // cost = 15000
            var rec2 = MakeRecording(30000, 25000); // cost = 5000

            var recordings = new List<RecordingStore.Recording> { rec1, rec2 };
            var budget = ResourceBudget.ComputeTotal(recordings, new List<Milestone>());

            Assert.Equal(20000, budget.reservedFunds);
        }

        [Fact]
        public void AvailableFunds_SubtractsCommitted()
        {
            var rec = MakeRecording(50000, 35000); // cost = 15000
            var recordings = new List<RecordingStore.Recording> { rec };
            var budget = ResourceBudget.ComputeTotal(recordings, new List<Milestone>());

            double currentFunds = 50000;
            double available = currentFunds - budget.reservedFunds;

            Assert.Equal(35000, available);
        }

        [Fact]
        public void PartiallyReplayed_OnlyRemainingReserved()
        {
            var rec = new RecordingStore.Recording
            {
                PreLaunchFunds = 50000,
                LastAppliedResourceIndex = 0 // point[0] already applied
            };

            // Point 0: funds after launch (45000)
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100,
                funds = 45000,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            });
            // Point 1: mid-flight (42000)
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 150,
                funds = 42000,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            });
            // Point 2: end (40000)
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 200,
                funds = 40000,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            });

            // totalImpact = 50000 - 40000 = 10000
            // alreadyApplied = 50000 - 45000 = 5000
            // remaining = 10000 - 5000 = 5000
            double cost = ResourceBudget.CommittedFundsCost(rec);

            Assert.Equal(5000, cost);
        }

        [Fact]
        public void CommittedScienceCost_Works()
        {
            var rec = MakeRecording(50000, 35000,
                preLaunchScience: 100, endScience: 80);

            double cost = ResourceBudget.CommittedScienceCost(rec);

            Assert.Equal(20, cost);
        }

        [Fact]
        public void CommittedReputationCost_Works()
        {
            var rec = MakeRecording(50000, 35000,
                preLaunchRep: 100, endRep: 90);

            double cost = ResourceBudget.CommittedReputationCost(rec);

            Assert.Equal(10, cost);
        }

        [Fact]
        public void CommittedFundsCost_NullRecording()
        {
            Assert.Equal(0, ResourceBudget.CommittedFundsCost(null));
        }

        [Fact]
        public void CommittedFundsCost_EmptyPoints()
        {
            var rec = new RecordingStore.Recording { PreLaunchFunds = 50000 };
            Assert.Equal(0, ResourceBudget.CommittedFundsCost(rec));
        }

        [Fact]
        public void CommittedFundsCost_SinglePoint()
        {
            var rec = new RecordingStore.Recording { PreLaunchFunds = 50000 };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100, funds = 45000, bodyName = "Kerbin",
                rotation = Quaternion.identity, velocity = Vector3.zero
            });
            // totalImpact = 50000 - 45000 = 5000, lastIdx = -1 → unplayed
            Assert.Equal(5000, ResourceBudget.CommittedFundsCost(rec));
        }

        [Fact]
        public void CommittedFundsCost_LastAppliedIndexAtCount()
        {
            // lastIdx == Points.Count (beyond last) → should return 0 (fully applied)
            var rec = MakeRecording(50000, 35000, lastAppliedResIdx: 2);
            Assert.Equal(0, ResourceBudget.CommittedFundsCost(rec));
        }

        [Fact]
        public void CommittedFundsCost_PartialReplayWithProfit()
        {
            var rec = new RecordingStore.Recording
            {
                PreLaunchFunds = 50000,
                LastAppliedResourceIndex = 0
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100, funds = 45000, bodyName = "Kerbin",
                rotation = Quaternion.identity, velocity = Vector3.zero
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 200, funds = 60000, bodyName = "Kerbin",
                rotation = Quaternion.identity, velocity = Vector3.zero
            });
            // totalImpact = 50000 - 60000 = -10000 (net profit)
            // alreadyApplied = 50000 - 45000 = 5000
            // remaining = -10000 - 5000 = -15000
            Assert.Equal(-15000, ResourceBudget.CommittedFundsCost(rec));
        }

        [Fact]
        public void CommittedScienceCost_NullRecording()
        {
            Assert.Equal(0, ResourceBudget.CommittedScienceCost(null));
        }

        [Fact]
        public void CommittedScienceCost_GainScience()
        {
            // Science gained during flight (rare but possible)
            var rec = MakeRecording(50000, 35000,
                preLaunchScience: 100, endScience: 120);
            Assert.Equal(-20, ResourceBudget.CommittedScienceCost(rec));
        }

        [Fact]
        public void CommittedReputationCost_NullRecording()
        {
            Assert.Equal(0, ResourceBudget.CommittedReputationCost(null));
        }

        [Fact]
        public void CommittedReputationCost_GainReputation()
        {
            var rec = MakeRecording(50000, 35000,
                preLaunchRep: 50, endRep: 70);
            Assert.Equal(-20, ResourceBudget.CommittedReputationCost(rec));
        }

        #endregion

        #region Milestone Cost Calculations

        [Fact]
        public void MilestoneCommittedCost_UnreplayedEvents()
        {
            var m = new Milestone
            {
                MilestoneId = "test1",
                Committed = true,
                LastReplayedEventIndex = -1, // nothing replayed
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 50,
                        eventType = GameStateEventType.TechResearched,
                        key = "basicRocketry",
                        detail = "cost=5"
                    },
                    new GameStateEvent
                    {
                        ut = 60,
                        eventType = GameStateEventType.PartPurchased,
                        key = "mk1pod.v2",
                        detail = "cost=600"
                    }
                }
            };

            double fundsCost = ResourceBudget.MilestoneCommittedFunds(m);
            double scienceCost = ResourceBudget.MilestoneCommittedScience(m);

            Assert.Equal(600, fundsCost);  // only PartPurchased has funds cost
            Assert.Equal(5, scienceCost);   // TechResearched has science cost
        }

        [Fact]
        public void MilestoneCommittedCost_AllReplayed()
        {
            var m = new Milestone
            {
                MilestoneId = "test2",
                Committed = true,
                LastReplayedEventIndex = 1, // all replayed (2 events, idx 0 and 1)
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 50,
                        eventType = GameStateEventType.TechResearched,
                        key = "basicRocketry",
                        detail = "cost=5"
                    },
                    new GameStateEvent
                    {
                        ut = 60,
                        eventType = GameStateEventType.PartPurchased,
                        key = "mk1pod.v2",
                        detail = "cost=600"
                    }
                }
            };

            double fundsCost = ResourceBudget.MilestoneCommittedFunds(m);
            double scienceCost = ResourceBudget.MilestoneCommittedScience(m);

            Assert.Equal(0, fundsCost);
            Assert.Equal(0, scienceCost);
        }

        [Fact]
        public void MilestoneCommittedCost_PartiallyReplayed()
        {
            var m = new Milestone
            {
                MilestoneId = "test3",
                Committed = true,
                LastReplayedEventIndex = 0, // first event replayed
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 50,
                        eventType = GameStateEventType.TechResearched,
                        key = "basicRocketry",
                        detail = "cost=5"
                    },
                    new GameStateEvent
                    {
                        ut = 60,
                        eventType = GameStateEventType.PartPurchased,
                        key = "mk1pod.v2",
                        detail = "cost=600"
                    }
                }
            };

            double fundsCost = ResourceBudget.MilestoneCommittedFunds(m);
            double scienceCost = ResourceBudget.MilestoneCommittedScience(m);

            Assert.Equal(600, fundsCost);  // only unreplayed PartPurchased
            Assert.Equal(0, scienceCost);   // TechResearched already replayed
        }

        [Fact]
        public void MilestoneCommittedFunds_NullMilestone()
        {
            Assert.Equal(0, ResourceBudget.MilestoneCommittedFunds(null));
        }

        [Fact]
        public void MilestoneCommittedScience_NullMilestone()
        {
            Assert.Equal(0, ResourceBudget.MilestoneCommittedScience(null));
        }

        [Fact]
        public void MilestoneCommittedFunds_EmptyEvents()
        {
            var m = new Milestone
            {
                MilestoneId = "empty",
                Committed = true,
                LastReplayedEventIndex = -1,
                Events = new List<GameStateEvent>()
            };
            Assert.Equal(0, ResourceBudget.MilestoneCommittedFunds(m));
        }

        [Fact]
        public void MilestoneCommittedFunds_ReplayIndexBeyondCount()
        {
            // LastReplayedEventIndex beyond events list — loop doesn't execute
            var m = new Milestone
            {
                MilestoneId = "oob",
                Committed = true,
                LastReplayedEventIndex = 5, // beyond the 1 event
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 50,
                        eventType = GameStateEventType.PartPurchased,
                        key = "mk1pod.v2",
                        detail = "cost=600"
                    }
                }
            };
            Assert.Equal(0, ResourceBudget.MilestoneCommittedFunds(m));
        }

        [Fact]
        public void MilestoneCommittedScience_IgnoresNonTechEvents()
        {
            var m = new Milestone
            {
                MilestoneId = "non-tech",
                Committed = true,
                LastReplayedEventIndex = -1,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 50,
                        eventType = GameStateEventType.PartPurchased,
                        key = "mk1pod.v2",
                        detail = "cost=600"
                    },
                    new GameStateEvent
                    {
                        ut = 60,
                        eventType = GameStateEventType.FacilityUpgraded,
                        key = "LaunchPad",
                        valueBefore = 0,
                        valueAfter = 1
                    }
                }
            };
            Assert.Equal(0, ResourceBudget.MilestoneCommittedScience(m));
        }

        [Fact]
        public void MilestoneCommittedFunds_FacilityUpgradedReturnsZero()
        {
            // FacilityUpgraded costs are not extracted (placeholder returns 0)
            var m = new Milestone
            {
                MilestoneId = "facility",
                Committed = true,
                LastReplayedEventIndex = -1,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 50,
                        eventType = GameStateEventType.FacilityUpgraded,
                        key = "LaunchPad",
                        valueBefore = 0,
                        valueAfter = 1
                    }
                }
            };
            Assert.Equal(0, ResourceBudget.MilestoneCommittedFunds(m));
        }

        [Fact]
        public void TotalCommitted_IncludesMilestones()
        {
            var rec = MakeRecording(50000, 35000); // recording cost = 15000
            var m = new Milestone
            {
                MilestoneId = "test4",
                Committed = true,
                LastReplayedEventIndex = -1,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 50,
                        eventType = GameStateEventType.PartPurchased,
                        key = "mk1pod.v2",
                        detail = "cost=600"
                    }
                }
            };

            var recordings = new List<RecordingStore.Recording> { rec };
            var milestones = new List<Milestone> { m };
            var budget = ResourceBudget.ComputeTotal(recordings, milestones);

            Assert.Equal(15600, budget.reservedFunds); // 15000 recording + 600 milestone
        }

        [Fact]
        public void TotalCommitted_UncommittedMilestoneExcluded()
        {
            var m = new Milestone
            {
                MilestoneId = "test5",
                Committed = false, // not committed
                LastReplayedEventIndex = -1,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 50,
                        eventType = GameStateEventType.PartPurchased,
                        key = "mk1pod.v2",
                        detail = "cost=600"
                    }
                }
            };

            var milestones = new List<Milestone> { m };
            var budget = ResourceBudget.ComputeTotal(
                new List<RecordingStore.Recording>(), milestones);

            Assert.Equal(0, budget.reservedFunds);
        }

        [Fact]
        public void ComputeTotal_BothNull()
        {
            var budget = ResourceBudget.ComputeTotal(null, null);
            Assert.Equal(0, budget.reservedFunds);
            Assert.Equal(0, budget.reservedScience);
            Assert.Equal(0, budget.reservedReputation);
        }

        [Fact]
        public void ComputeTotal_NullRecordingsNonNullMilestones()
        {
            var m = new Milestone
            {
                MilestoneId = "test-null-rec",
                Committed = true,
                LastReplayedEventIndex = -1,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 50,
                        eventType = GameStateEventType.PartPurchased,
                        key = "mk1pod.v2",
                        detail = "cost=600"
                    }
                }
            };
            var budget = ResourceBudget.ComputeTotal(null, new List<Milestone> { m });
            Assert.Equal(600, budget.reservedFunds);
        }

        [Fact]
        public void ComputeTotal_NonNullRecordingsNullMilestones()
        {
            var rec = MakeRecording(50000, 35000);
            var budget = ResourceBudget.ComputeTotal(
                new List<RecordingStore.Recording> { rec }, null);
            Assert.Equal(15000, budget.reservedFunds);
        }

        [Fact]
        public void ComputeTotal_AllThreeResourceTypes()
        {
            var rec = MakeRecording(
                preLaunchFunds: 50000, endFunds: 40000,
                preLaunchScience: 100, endScience: 85,
                preLaunchRep: 50, endRep: 45);
            var budget = ResourceBudget.ComputeTotal(
                new List<RecordingStore.Recording> { rec }, new List<Milestone>());
            Assert.Equal(10000, budget.reservedFunds);
            Assert.Equal(15, budget.reservedScience);
            Assert.Equal(5, budget.reservedReputation);
        }

        [Fact]
        public void ComputeTotal_ProfitsCancelCosts()
        {
            var rec1 = MakeRecording(50000, 40000); // cost 10000
            var rec2 = MakeRecording(50000, 60000); // profit -10000
            var budget = ResourceBudget.ComputeTotal(
                new List<RecordingStore.Recording> { rec1, rec2 }, new List<Milestone>());
            Assert.Equal(0, budget.reservedFunds);
        }

        #endregion

        #region ParseCostFromDetail

        [Fact]
        public void ParseCostFromDetail_SimpleCost()
        {
            Assert.Equal(600, ResourceBudget.ParseCostFromDetail("cost=600"));
        }

        [Fact]
        public void ParseCostFromDetail_WithOtherFields()
        {
            Assert.Equal(5, ResourceBudget.ParseCostFromDetail("cost=5;parts=solidBooster.sm.v2"));
        }

        [Fact]
        public void ParseCostFromDetail_Empty()
        {
            Assert.Equal(0, ResourceBudget.ParseCostFromDetail(""));
            Assert.Equal(0, ResourceBudget.ParseCostFromDetail(null));
        }

        [Fact]
        public void ParseCostFromDetail_NoCostField()
        {
            Assert.Equal(0, ResourceBudget.ParseCostFromDetail("type=SurveyContract"));
        }

        [Fact]
        public void ParseCostFromDetail_InvalidFloat()
        {
            Assert.Equal(0, ResourceBudget.ParseCostFromDetail("cost=abc"));
            Assert.Equal(0, ResourceBudget.ParseCostFromDetail("cost="));
            Assert.Equal(0, ResourceBudget.ParseCostFromDetail("cost=12.34.56"));
        }

        [Fact]
        public void ParseCostFromDetail_NegativeCost()
        {
            Assert.Equal(-500, ResourceBudget.ParseCostFromDetail("cost=-500"));
        }

        [Fact]
        public void ParseCostFromDetail_DecimalCost()
        {
            Assert.Equal(12.5, ResourceBudget.ParseCostFromDetail("cost=12.5"));
        }

        [Fact]
        public void ParseCostFromDetail_CaseSensitive()
        {
            // "cost=" is case-sensitive (Ordinal comparison)
            Assert.Equal(0, ResourceBudget.ParseCostFromDetail("Cost=600"));
            Assert.Equal(0, ResourceBudget.ParseCostFromDetail("COST=600"));
        }

        [Fact]
        public void ParseCostFromDetail_DuplicateCostField()
        {
            // First match wins
            Assert.Equal(100, ResourceBudget.ParseCostFromDetail("cost=100;cost=200"));
        }

        [Fact]
        public void ParseCostFromDetail_CostInMiddle()
        {
            Assert.Equal(300, ResourceBudget.ParseCostFromDetail("type=tech;cost=300;node=basic"));
        }

        #endregion

        #region PreLaunch Field Propagation

        [Fact]
        public void PreLaunchFields_SurviveApplyPersistenceArtifacts()
        {
            var source = new RecordingStore.Recording
            {
                RecordingId = "src1",
                PreLaunchFunds = 50000,
                PreLaunchScience = 100,
                PreLaunchReputation = 75
            };

            var target = new RecordingStore.Recording();
            target.ApplyPersistenceArtifactsFrom(source);

            Assert.Equal(50000, target.PreLaunchFunds);
            Assert.Equal(100, target.PreLaunchScience);
            Assert.Equal(75, target.PreLaunchReputation);
        }

        [Fact]
        public void PreLaunchFields_DefaultToZero()
        {
            var rec = new RecordingStore.Recording();

            Assert.Equal(0, rec.PreLaunchFunds);
            Assert.Equal(0, rec.PreLaunchScience);
            Assert.Equal(0, rec.PreLaunchReputation);
        }

        [Fact]
        public void PreLaunchFields_MetadataRoundTrip()
        {
            var source = new RecordingStore.Recording
            {
                RecordingId = "meta1",
                PreLaunchFunds = 45000.5,
                PreLaunchScience = 123.456,
                PreLaunchReputation = 67.89f
            };

            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            var loaded = new RecordingStore.Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(45000.5, loaded.PreLaunchFunds);
            Assert.Equal(123.456, loaded.PreLaunchScience, 3);
            Assert.Equal(67.89f, loaded.PreLaunchReputation, 0.01f);
        }

        [Fact]
        public void PreLaunchFields_MissingKeysDefaultToZero()
        {
            var node = new ConfigNode("RECORDING");
            // No preLaunch keys at all

            var loaded = new RecordingStore.Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(0, loaded.PreLaunchFunds);
            Assert.Equal(0, loaded.PreLaunchScience);
            Assert.Equal(0, loaded.PreLaunchReputation);
        }

        #endregion

        #region Decoupled Milestone Budget

        [Fact]
        public void MilestoneBudget_SurvivesRecordingDeletion()
        {
            // Recording + milestone both contribute to budget
            var rec = MakeRecording(50000, 35000); // recording cost = 15000
            var m = new Milestone
            {
                MilestoneId = "test-decouple",
                Committed = true,
                LastReplayedEventIndex = -1, // unreplayed (after revert)
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 50,
                        eventType = GameStateEventType.PartPurchased,
                        key = "mk1pod.v2",
                        detail = "cost=600"
                    }
                }
            };

            var recordings = new List<RecordingStore.Recording> { rec };
            var milestones = new List<Milestone> { m };

            // Both contribute
            var budget = ResourceBudget.ComputeTotal(recordings, milestones);
            Assert.Equal(15600, budget.reservedFunds); // 15000 + 600

            // Remove recording, milestone survives
            recordings.Clear();
            ResourceBudget.Invalidate();
            budget = ResourceBudget.ComputeTotal(recordings, milestones);
            Assert.Equal(600, budget.reservedFunds); // only milestone cost remains
        }

        [Fact]
        public void MilestoneBudget_FullyApplied_ZeroCost()
        {
            // During normal play, milestones are fully applied — no reservation
            var m = new Milestone
            {
                MilestoneId = "test-applied",
                Committed = true,
                LastReplayedEventIndex = 0, // fully applied (1 event, index 0)
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 50,
                        eventType = GameStateEventType.PartPurchased,
                        key = "mk1pod.v2",
                        detail = "cost=600"
                    }
                }
            };

            var milestones = new List<Milestone> { m };
            var budget = ResourceBudget.ComputeTotal(
                new List<RecordingStore.Recording>(), milestones);

            Assert.Equal(0, budget.reservedFunds);
        }

        #endregion

        #region Disabled Segment + Resource Budget (cross-feature)

        [Fact]
        public void DisabledRecording_StillReservesResources()
        {
            // PlaybackEnabled=false suppresses the ghost but the mission still happened —
            // resources must still be reserved.
            var rec = MakeRecording(50000, 35000); // cost = 15000
            rec.PlaybackEnabled = false;

            var recordings = new List<RecordingStore.Recording> { rec };
            var budget = ResourceBudget.ComputeTotal(recordings, new List<Milestone>());

            Assert.Equal(15000, budget.reservedFunds);
        }

        [Fact]
        public void DisabledChain_AllSegmentsStillReserve()
        {
            // A fully-disabled chain (all segments PlaybackEnabled=false) still reserves
            // resources for each segment independently.
            var seg0 = MakeRecording(50000, 45000); // cost = 5000 (launch)
            seg0.ChainId = "chain-test";
            seg0.ChainIndex = 0;
            seg0.PlaybackEnabled = false;

            var seg1 = MakeRecording(45000, 42000); // cost = 3000 (atmo segment)
            seg1.ChainId = "chain-test";
            seg1.ChainIndex = 1;
            seg1.PlaybackEnabled = false;

            var recordings = new List<RecordingStore.Recording> { seg0, seg1 };
            var budget = ResourceBudget.ComputeTotal(recordings, new List<Milestone>());

            Assert.Equal(8000, budget.reservedFunds); // 5000 + 3000
        }

        #endregion

        #region Chain Split PreLaunchFunds Independence

        [Fact]
        public void ChainSegments_IndependentPreLaunchFunds_NoDoubleCounting()
        {
            // Atmosphere auto-split creates segments with independent PreLaunchFunds:
            // seg0: PreLaunch=50000, end=45000 → cost 5000 (launch through atmo exit)
            // seg1: PreLaunch=45000, end=42000 → cost 3000 (exo segment)
            // Total should be 8000, not double-counted.
            var seg0 = MakeRecording(50000, 45000); // cost = 5000
            seg0.ChainId = "chain-split";
            seg0.ChainIndex = 0;
            seg0.SegmentPhase = "atmo";

            var seg1 = MakeRecording(45000, 42000); // cost = 3000
            seg1.ChainId = "chain-split";
            seg1.ChainIndex = 1;
            seg1.SegmentPhase = "exo";

            var recordings = new List<RecordingStore.Recording> { seg0, seg1 };
            var budget = ResourceBudget.ComputeTotal(recordings, new List<Milestone>());

            Assert.Equal(8000, budget.reservedFunds);
        }

        [Fact]
        public void ChainSegments_PartialReplay_IndependentTracking()
        {
            // Each segment tracks its own LastAppliedResourceIndex independently.
            // seg0 fully applied, seg1 not yet applied.
            var seg0 = MakeRecording(50000, 45000, lastAppliedResIdx: 1); // fully applied → cost 0
            seg0.ChainId = "chain-partial";
            seg0.ChainIndex = 0;

            var seg1 = MakeRecording(45000, 42000); // not applied → cost 3000
            seg1.ChainId = "chain-partial";
            seg1.ChainIndex = 1;

            var recordings = new List<RecordingStore.Recording> { seg0, seg1 };
            var budget = ResourceBudget.ComputeTotal(recordings, new List<Milestone>());

            Assert.Equal(3000, budget.reservedFunds); // only seg1's cost
        }

        [Fact]
        public void ChainSegments_ScienceAndReputation_Independent()
        {
            var seg0 = MakeRecording(50000, 45000,
                preLaunchScience: 100, endScience: 90,
                preLaunchRep: 50, endRep: 48);
            seg0.ChainId = "chain-sci";
            seg0.ChainIndex = 0;

            var seg1 = MakeRecording(45000, 42000,
                preLaunchScience: 90, endScience: 85,
                preLaunchRep: 48, endRep: 46);
            seg1.ChainId = "chain-sci";
            seg1.ChainIndex = 1;

            var recordings = new List<RecordingStore.Recording> { seg0, seg1 };
            var budget = ResourceBudget.ComputeTotal(recordings, new List<Milestone>());

            Assert.Equal(8000, budget.reservedFunds);   // 5000 + 3000
            Assert.Equal(15, budget.reservedScience);    // 10 + 5
            Assert.Equal(4, budget.reservedReputation);  // 2 + 2
        }

        #endregion

        #region Tree Resource Budget

        [Fact]
        public void TreeCommittedFundsCost_BasicCost()
        {
            // DeltaFunds=-5000 (spent money), ResourcesApplied=false -> cost=5000
            var tree = new RecordingTree
            {
                Id = "tree1",
                DeltaFunds = -5000,
                ResourcesApplied = false
            };
            Assert.Equal(5000, ResourceBudget.TreeCommittedFundsCost(tree));
        }

        [Fact]
        public void TreeCommittedFundsCost_Profit()
        {
            // DeltaFunds=+3000 (earned money), ResourcesApplied=false -> cost=-3000
            var tree = new RecordingTree
            {
                Id = "tree2",
                DeltaFunds = 3000,
                ResourcesApplied = false
            };
            Assert.Equal(-3000, ResourceBudget.TreeCommittedFundsCost(tree));
        }

        [Fact]
        public void TreeCommittedFundsCost_AlreadyApplied()
        {
            var tree = new RecordingTree
            {
                Id = "tree3",
                DeltaFunds = -5000,
                ResourcesApplied = true
            };
            Assert.Equal(0, ResourceBudget.TreeCommittedFundsCost(tree));
        }

        [Fact]
        public void TreeCommittedFundsCost_NullTree()
        {
            Assert.Equal(0, ResourceBudget.TreeCommittedFundsCost(null));
        }

        [Fact]
        public void TreeCommittedScienceCost_Works()
        {
            var tree = new RecordingTree
            {
                Id = "tree4",
                DeltaScience = -25.5,
                ResourcesApplied = false
            };
            Assert.Equal(25.5, ResourceBudget.TreeCommittedScienceCost(tree));
        }

        [Fact]
        public void TreeCommittedReputationCost_Works()
        {
            var tree = new RecordingTree
            {
                Id = "tree5",
                DeltaReputation = -10.25f,
                ResourcesApplied = false
            };
            Assert.Equal(10.25, ResourceBudget.TreeCommittedReputationCost(tree), 0.001);
        }

        [Fact]
        public void ComputeTotal_TreeRecordingsSkipped()
        {
            // Tree recordings (TreeId != null) are excluded from per-recording sum
            var rec = MakeRecording(50000, 35000); // cost would be 15000
            rec.TreeId = "some_tree";

            var recordings = new List<RecordingStore.Recording> { rec };
            var budget = ResourceBudget.ComputeTotal(recordings, new List<Milestone>());

            Assert.Equal(0, budget.reservedFunds); // skipped because TreeId != null
        }

        [Fact]
        public void ComputeTotal_TreeDeltaAdded()
        {
            // Tree-level delta added to budget
            var tree = new RecordingTree
            {
                Id = "tree_budget",
                DeltaFunds = -8000,
                DeltaScience = -15,
                DeltaReputation = -5f,
                ResourcesApplied = false
            };

            var trees = new List<RecordingTree> { tree };
            var budget = ResourceBudget.ComputeTotal(
                new List<RecordingStore.Recording>(),
                new List<Milestone>(),
                trees);

            Assert.Equal(8000, budget.reservedFunds);
            Assert.Equal(15, budget.reservedScience);
            Assert.Equal(5, budget.reservedReputation);
        }

        [Fact]
        public void ComputeTotal_MixedTreeAndStandalone()
        {
            // Standalone recording: cost 15000
            var rec = MakeRecording(50000, 35000);

            // Tree recording (should be skipped in per-recording sum)
            var treeRec = MakeRecording(50000, 40000);
            treeRec.TreeId = "tree_mixed";

            // Tree delta: cost 3000
            var tree = new RecordingTree
            {
                Id = "tree_mixed",
                DeltaFunds = -3000,
                ResourcesApplied = false
            };

            var recordings = new List<RecordingStore.Recording> { rec, treeRec };
            var trees = new List<RecordingTree> { tree };
            var budget = ResourceBudget.ComputeTotal(recordings, new List<Milestone>(), trees);

            Assert.Equal(18000, budget.reservedFunds); // 15000 standalone + 3000 tree
        }

        [Fact]
        public void ComputeTotal_TreeApplied_ZeroCost()
        {
            var tree = new RecordingTree
            {
                Id = "tree_applied",
                DeltaFunds = -5000,
                ResourcesApplied = true
            };

            var trees = new List<RecordingTree> { tree };
            var budget = ResourceBudget.ComputeTotal(
                new List<RecordingStore.Recording>(),
                new List<Milestone>(),
                trees);

            Assert.Equal(0, budget.reservedFunds);
        }

        [Fact]
        public void ComputeTotal_BackwardCompat_NullTrees()
        {
            // trees=null works same as before
            var rec = MakeRecording(50000, 35000);
            var budget = ResourceBudget.ComputeTotal(
                new List<RecordingStore.Recording> { rec },
                new List<Milestone>(),
                null);

            Assert.Equal(15000, budget.reservedFunds);
        }

        #endregion

        #region RecordingPaths

        [Fact]
        public void BuildMilestonesRelativePath_CorrectPath()
        {
            string path = RecordingPaths.BuildMilestonesRelativePath().Replace('\\', '/');
            Assert.Equal("Parsek/GameState/milestones.pgsm", path);
        }

        #endregion

        #region B3: ComputeTotal — multiple trees, mixed ResourcesApplied

        [Fact]
        public void ComputeTotal_MultipleTrees_MixedResourcesApplied()
        {
            // Tree A: applied, DeltaFunds=-3000 (should NOT contribute)
            var treeA = new RecordingTree
            {
                Id = "treeA",
                TreeName = "Tree A",
                DeltaFunds = -3000,
                ResourcesApplied = true
            };

            // Tree B: not applied, DeltaFunds=-7000 (should contribute)
            var treeB = new RecordingTree
            {
                Id = "treeB",
                TreeName = "Tree B",
                DeltaFunds = -7000,
                ResourcesApplied = false
            };

            var trees = new List<RecordingTree> { treeA, treeB };
            var budget = ResourceBudget.ComputeTotal(
                new List<RecordingStore.Recording>(),
                new List<Milestone>(),
                trees);

            // Only Tree B's cost (7000) should be reserved
            Assert.Equal(7000, budget.reservedFunds);
        }

        #endregion

        #region D4: All-terminal leaves — budget delta still counts

        [Fact]
        public void ComputeTotal_AllTerminalLeaves_DeltaStillCounts()
        {
            // Tree with all leaves being Destroyed/Recovered — resource delta should still count
            var tree = new RecordingTree
            {
                Id = "tree_terminal",
                TreeName = "All Terminal",
                DeltaFunds = -3000,
                ResourcesApplied = false,
                RootRecordingId = "root"
            };

            // All recordings have terminal states that make them non-spawnable
            tree.Recordings["root"] = new RecordingStore.Recording
            {
                RecordingId = "root",
                VesselName = "Root",
                ChildBranchPointId = "bp1"
            };
            tree.Recordings["leaf1"] = new RecordingStore.Recording
            {
                RecordingId = "leaf1",
                VesselName = "Destroyed",
                TerminalStateValue = TerminalState.Destroyed,
                ParentBranchPointId = "bp1"
            };
            tree.Recordings["leaf2"] = new RecordingStore.Recording
            {
                RecordingId = "leaf2",
                VesselName = "Recovered",
                TerminalStateValue = TerminalState.Recovered,
                ParentBranchPointId = "bp1"
            };

            // Verify no spawnable leaves
            Assert.Empty(tree.GetSpawnableLeaves());

            var trees = new List<RecordingTree> { tree };
            var budget = ResourceBudget.ComputeTotal(
                new List<RecordingStore.Recording>(),
                new List<Milestone>(),
                trees);

            // Tree's resource delta still counted even though no spawnable leaves
            Assert.Equal(3000, budget.reservedFunds);
        }

        #endregion

        #region ComputeTotalFullCost

        [Fact]
        public void ComputeTotalFullCost_EqualsComputeTotal_WhenNothingApplied()
        {
            // 3 recordings with LARI=-1 (nothing applied). Both methods should return identical results.
            var rec1 = MakeRecording(50000, 35000); // cost = 15000
            var rec2 = MakeRecording(30000, 25000); // cost = 5000
            var rec3 = MakeRecording(80000, 90000); // cost = -10000 (profit)

            var recordings = new List<RecordingStore.Recording> { rec1, rec2, rec3 };
            var milestones = new List<Milestone>();

            var total = ResourceBudget.ComputeTotal(recordings, milestones);
            ResourceBudget.Invalidate();
            var fullCost = ResourceBudget.ComputeTotalFullCost(recordings, milestones);

            Assert.Equal(total.reservedFunds, fullCost.reservedFunds);
            Assert.Equal(total.reservedScience, fullCost.reservedScience);
            Assert.Equal(total.reservedReputation, fullCost.reservedReputation);
        }

        [Fact]
        public void ComputeTotalFullCost_IgnoresLARI()
        {
            // Recording with PreLaunchFunds=100000, Points[last].funds=96000 (cost=4000).
            // Set LARI=Points.Count-1 (fully applied).
            // ComputeTotal funds cost for this recording = 0.
            // ComputeTotalFullCost funds cost = 4000.
            var rec = MakeRecording(100000, 96000, lastAppliedResIdx: 1);

            var recordings = new List<RecordingStore.Recording> { rec };
            var milestones = new List<Milestone>();

            var total = ResourceBudget.ComputeTotal(recordings, milestones);
            Assert.Equal(0, total.reservedFunds); // fully applied, cost = 0

            ResourceBudget.Invalidate();
            var fullCost = ResourceBudget.ComputeTotalFullCost(recordings, milestones);
            Assert.Equal(4000, fullCost.reservedFunds); // ignores LARI, full cost
        }

        [Fact]
        public void ComputeTotalFullCost_IgnoresTreeResourcesApplied()
        {
            // Tree with ResourcesApplied=true.
            // ComputeTotal returns 0 (already applied).
            // ComputeTotalFullCost returns full cost.
            var tree = new RecordingTree
            {
                Id = "tree_full",
                TreeName = "Full Tree",
                DeltaFunds = -5000,
                DeltaScience = -10,
                DeltaReputation = -3f,
                ResourcesApplied = true
            };

            var recordings = new List<RecordingStore.Recording>();
            var milestones = new List<Milestone>();
            var trees = new List<RecordingTree> { tree };

            var total = ResourceBudget.ComputeTotal(recordings, milestones, trees);
            Assert.Equal(0, total.reservedFunds); // already applied

            ResourceBudget.Invalidate();
            var fullCost = ResourceBudget.ComputeTotalFullCost(recordings, milestones, trees);
            Assert.Equal(5000, fullCost.reservedFunds);
            Assert.Equal(10, fullCost.reservedScience);
            Assert.Equal(3, fullCost.reservedReputation, 0.001);
        }

        [Fact]
        public void ComputeTotalFullCost_IgnoresMilestoneLastReplayedIndex()
        {
            // Milestone with LastReplayedEventIndex=max (all replayed).
            // ComputeTotal returns 0 (all events replayed).
            // ComputeTotalFullCost returns full cost (iterates from index 0).
            var m = new Milestone
            {
                MilestoneId = "full_milestone",
                Committed = true,
                LastReplayedEventIndex = 1, // all replayed (2 events)
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 50,
                        eventType = GameStateEventType.TechResearched,
                        key = "basicRocketry",
                        detail = "cost=5"
                    },
                    new GameStateEvent
                    {
                        ut = 60,
                        eventType = GameStateEventType.PartPurchased,
                        key = "mk1pod.v2",
                        detail = "cost=600"
                    }
                }
            };

            var recordings = new List<RecordingStore.Recording>();
            var milestones = new List<Milestone> { m };

            var total = ResourceBudget.ComputeTotal(recordings, milestones);
            Assert.Equal(0, total.reservedFunds); // all replayed
            Assert.Equal(0, total.reservedScience);

            ResourceBudget.Invalidate();
            var fullCost = ResourceBudget.ComputeTotalFullCost(recordings, milestones);
            Assert.Equal(600, fullCost.reservedFunds); // full cost (PartPurchased)
            Assert.Equal(5, fullCost.reservedScience);  // full cost (TechResearched)
        }

        [Fact]
        public void ResourceAdjustmentFormula_WorkedExample()
        {
            // From design doc worked example:
            // Recording A: PreLaunchFunds=80000, endpoint funds=84000 -> cost = -4000 (earned)
            // Recording B: PreLaunchFunds=74000, endpoint funds=72000 -> cost = 2000 (lost)
            // ComputeTotalFullCost with both -> reservedFunds = -4000 + 2000 = -2000
            var recA = new RecordingStore.Recording
            {
                PreLaunchFunds = 80000,
                LastAppliedResourceIndex = -1
            };
            recA.Points.Add(new TrajectoryPoint
            {
                ut = 100, funds = 75000, bodyName = "Kerbin",
                rotation = UnityEngine.Quaternion.identity,
                velocity = UnityEngine.Vector3.zero
            });
            recA.Points.Add(new TrajectoryPoint
            {
                ut = 200, funds = 84000, bodyName = "Kerbin",
                rotation = UnityEngine.Quaternion.identity,
                velocity = UnityEngine.Vector3.zero
            });

            var recB = new RecordingStore.Recording
            {
                PreLaunchFunds = 74000,
                LastAppliedResourceIndex = -1
            };
            recB.Points.Add(new TrajectoryPoint
            {
                ut = 250, funds = 74000, bodyName = "Kerbin",
                rotation = UnityEngine.Quaternion.identity,
                velocity = UnityEngine.Vector3.zero
            });
            recB.Points.Add(new TrajectoryPoint
            {
                ut = 350, funds = 72000, bodyName = "Kerbin",
                rotation = UnityEngine.Quaternion.identity,
                velocity = UnityEngine.Vector3.zero
            });

            var recordings = new List<RecordingStore.Recording> { recA, recB };
            var milestones = new List<Milestone>();

            var fullCost = ResourceBudget.ComputeTotalFullCost(recordings, milestones);

            // A: 80000 - 84000 = -4000 (earned)
            // B: 74000 - 72000 = 2000 (lost)
            // Total: -4000 + 2000 = -2000
            Assert.Equal(-2000, fullCost.reservedFunds);
        }

        #endregion
    }
}
