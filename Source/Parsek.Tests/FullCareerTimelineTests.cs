using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class FullCareerTimelineTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public FullCareerTimelineTests()
        {
            LedgerOrchestrator.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            LedgerOrchestrator.Initialize();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Helper — builds the full Mun landing career timeline
        // ================================================================

        private static List<GameAction> BuildMunLandingTimeline()
        {
            return new List<GameAction>
            {
                // UT=0: Seed initial balances
                new GameAction
                {
                    UT = 0, Type = GameActionType.FundsInitial,
                    FundsAwarded = 25000, InitialFunds = 25000
                },
                new GameAction
                {
                    UT = 0, Type = GameActionType.ScienceInitial,
                    InitialScience = 0
                },
                new GameAction
                {
                    UT = 0, Type = GameActionType.ReputationInitial,
                    InitialReputation = 0
                },

                // UT=100: Accept explore-mun contract (advance 2000 funds)
                new GameAction
                {
                    UT = 100, Type = GameActionType.ContractAccept,
                    ContractId = "explore-mun", AdvanceFunds = 2000,
                    FundsReward = 8000, RepReward = 15, ScienceReward = 5,
                    DeadlineUT = float.NaN
                },

                // UT=200: Spend 5000 funds on vessel build
                new GameAction
                {
                    UT = 200, Type = GameActionType.FundsSpending,
                    FundsSpendingSource = FundsSpendingSource.VesselBuild,
                    FundsSpent = 5000, RecordingId = "rec-1"
                },

                // UT=300: FirstLaunch milestone
                new GameAction
                {
                    UT = 300, Type = GameActionType.MilestoneAchievement,
                    MilestoneId = "FirstLaunch",
                    MilestoneFundsAwarded = 500, MilestoneRepAwarded = 5,
                    RecordingId = "rec-1"
                },

                // UT=500: Crew report at launchpad
                new GameAction
                {
                    UT = 500, Type = GameActionType.ScienceEarning,
                    SubjectId = "crewReport@KerbinSrfLandedLaunchpad",
                    ScienceAwarded = 5, SubjectMaxValue = 5,
                    RecordingId = "rec-1"
                },

                // UT=1000: Crew report in low Kerbin space
                new GameAction
                {
                    UT = 1000, Type = GameActionType.ScienceEarning,
                    SubjectId = "crewReport@KerbinInSpaceLow",
                    ScienceAwarded = 8, SubjectMaxValue = 8,
                    RecordingId = "rec-1"
                },

                // UT=1000: FirstKerbinOrbit milestone
                new GameAction
                {
                    UT = 1000, Type = GameActionType.MilestoneAchievement,
                    MilestoneId = "FirstKerbinOrbit",
                    MilestoneFundsAwarded = 1000, MilestoneRepAwarded = 10,
                    RecordingId = "rec-1"
                },

                // UT=3000: Temperature scan in high Mun space
                new GameAction
                {
                    UT = 3000, Type = GameActionType.ScienceEarning,
                    SubjectId = "temperatureScan@MunInSpaceHigh",
                    ScienceAwarded = 12, SubjectMaxValue = 20,
                    RecordingId = "rec-1"
                },

                // UT=3000: FirstMunFlyby milestone
                new GameAction
                {
                    UT = 3000, Type = GameActionType.MilestoneAchievement,
                    MilestoneId = "FirstMunFlyby",
                    MilestoneFundsAwarded = 2000, MilestoneRepAwarded = 15,
                    RecordingId = "rec-1"
                },

                // UT=5000: Crew report on Mun surface
                new GameAction
                {
                    UT = 5000, Type = GameActionType.ScienceEarning,
                    SubjectId = "crewReport@MunSrfLandedMidlands",
                    ScienceAwarded = 30, SubjectMaxValue = 40,
                    RecordingId = "rec-1"
                },

                // UT=5000: FirstMunLanding milestone
                new GameAction
                {
                    UT = 5000, Type = GameActionType.MilestoneAchievement,
                    MilestoneId = "FirstMunLanding",
                    MilestoneFundsAwarded = 5000, MilestoneRepAwarded = 25,
                    RecordingId = "rec-1"
                },

                // UT=8000: Complete explore-mun contract
                new GameAction
                {
                    UT = 8000, Type = GameActionType.ContractComplete,
                    ContractId = "explore-mun",
                    FundsReward = 8000, RepReward = 15, ScienceReward = 5,
                    RecordingId = "rec-1"
                },

                // UT=8000: Recovery funds
                new GameAction
                {
                    UT = 8000, Type = GameActionType.FundsEarning,
                    FundsSource = FundsEarningSource.Recovery,
                    FundsAwarded = 1500, RecordingId = "rec-1"
                },

                // UT=10000: Tech tree unlocks
                new GameAction
                {
                    UT = 10000, Type = GameActionType.ScienceSpending,
                    NodeId = "basicRocketry", Cost = 5, Sequence = 0
                },
                new GameAction
                {
                    UT = 10000, Type = GameActionType.ScienceSpending,
                    NodeId = "engineering101", Cost = 5, Sequence = 1
                },

                // UT=12000: Facility upgrade
                new GameAction
                {
                    UT = 12000, Type = GameActionType.FacilityUpgrade,
                    FacilityId = "TrackingStation", ToLevel = 2,
                    FacilityCost = 300
                }
            };
        }

        // ================================================================
        // Test 1: Full Mun landing career timeline
        // ================================================================

        [Fact]
        public void FullMunLandingTimeline()
        {
            // Arrange: configure module slots
            LedgerOrchestrator.Contracts.SetMaxSlots(7);
            LedgerOrchestrator.Strategies.SetMaxSlots(3);

            var actions = BuildMunLandingTimeline();
            Ledger.AddActions(actions);

            // Act: recalculate (same pattern as LedgerOrchestrator.RecalculateAndPatch but without KSP patching)
            var actionsCopy = new List<GameAction>(Ledger.Actions);
            RecalculationEngine.Recalculate(actionsCopy);

            // Assert: Science balance
            // 0 (seed) + 5 + 8 + 12 + 30 (experiments) + 5 (contract reward) - 5 - 5 (spendings) = 50
            Assert.Equal(50.0, LedgerOrchestrator.Science.GetRunningScience(), 1);

            // Assert: Available science = totalEffectiveEarnings - totalCommittedSpendings
            // totalEffectiveEarnings = 0 + 5 + 8 + 12 + 30 + 5 = 60
            // totalCommittedSpendings = 5 + 5 = 10
            // available = 60 - 10 = 50
            Assert.Equal(50.0, LedgerOrchestrator.Science.GetAvailableScience(), 1);

            // Assert: Funds balance
            // 25000 (seed) + 2000 (advance) - 5000 (vessel) + 500 + 1000 + 2000 + 5000 (milestones)
            // + 8000 (contract) + 1500 (recovery) - 300 (facility) = 39700
            Assert.Equal(39700.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);

            // Assert: Initial funds preserved
            Assert.Equal(25000.0, LedgerOrchestrator.Funds.GetInitialFunds(), 1);

            // Assert: All milestones credited
            Assert.True(LedgerOrchestrator.Milestones.IsMilestoneCredited("FirstLaunch"));
            Assert.True(LedgerOrchestrator.Milestones.IsMilestoneCredited("FirstKerbinOrbit"));
            Assert.True(LedgerOrchestrator.Milestones.IsMilestoneCredited("FirstMunFlyby"));
            Assert.True(LedgerOrchestrator.Milestones.IsMilestoneCredited("FirstMunLanding"));
            Assert.Equal(4, LedgerOrchestrator.Milestones.GetCreditedCount());

            // Assert: Contract completed and credited
            Assert.True(LedgerOrchestrator.Contracts.IsContractCredited("explore-mun"));
            // After completion, active count should be 0 (accepted then completed)
            Assert.Equal(0, LedgerOrchestrator.Contracts.GetActiveContractCount());

            // Assert: Facility at level 2
            Assert.Equal(2, LedgerOrchestrator.Facilities.GetFacilityLevel("TrackingStation"));
            Assert.False(LedgerOrchestrator.Facilities.IsFacilityDestroyed("TrackingStation"));

            // Assert: Reputation is positive and > 50
            // Milestone rep: 5 + 10 + 15 + 25 = 55 nominal, plus contract rep = 15 nominal = 70 total nominal
            // At low rep the gain curve multiplier is ~1.0, so effective rep should be close to 70
            float rep = LedgerOrchestrator.Reputation.GetRunningRep();
            Assert.True(rep > 0, $"Expected positive reputation, got {rep}");
            Assert.True(rep > 50, $"Expected reputation > 50, got {rep}");

            // Assert: Per-subject credited totals
            Assert.Equal(5.0, LedgerOrchestrator.Science.GetSubjectCredited("crewReport@KerbinSrfLandedLaunchpad"), 1);
            Assert.Equal(8.0, LedgerOrchestrator.Science.GetSubjectCredited("crewReport@KerbinInSpaceLow"), 1);
            Assert.Equal(12.0, LedgerOrchestrator.Science.GetSubjectCredited("temperatureScan@MunInSpaceHigh"), 1);
            Assert.Equal(30.0, LedgerOrchestrator.Science.GetSubjectCredited("crewReport@MunSrfLandedMidlands"), 1);

            // Assert: Science spendings were affordable
            var scienceSpendings = actionsCopy
                .Where(a => a.Type == GameActionType.ScienceSpending)
                .ToList();
            Assert.Equal(2, scienceSpendings.Count);
            Assert.All(scienceSpendings, a => Assert.True(a.Affordable,
                $"Science spending for {a.NodeId} should be affordable"));

            // Assert: Vessel build spending was affordable
            var vesselSpending = actionsCopy
                .First(a => a.Type == GameActionType.FundsSpending);
            Assert.True(vesselSpending.Affordable);

            // Assert: Recalculation logged completion
            Assert.Contains(logLines, l =>
                l.Contains("[RecalcEngine]") && l.Contains("Recalculate complete"));
        }

        // ================================================================
        // Test 2: Retroactive commit recomputes priority
        // ================================================================

        [Fact]
        public void RetroactiveCommit_RecomputesPriority()
        {
            // Arrange: set up the timeline with rec-1 science at UT=500
            LedgerOrchestrator.Contracts.SetMaxSlots(7);
            LedgerOrchestrator.Strategies.SetMaxSlots(3);

            var actions = BuildMunLandingTimeline();

            // Add a second recording (rec-2) at UT=200 that collects the same science subject
            // as rec-1's UT=500 action ("crewReport@KerbinSrfLandedLaunchpad", max=5)
            var retroactiveAction = new GameAction
            {
                UT = 200, Type = GameActionType.ScienceEarning,
                SubjectId = "crewReport@KerbinSrfLandedLaunchpad",
                ScienceAwarded = 5, SubjectMaxValue = 5,
                RecordingId = "rec-2"
            };
            actions.Add(retroactiveAction);

            Ledger.AddActions(actions);
            var actionsCopy = new List<GameAction>(Ledger.Actions);
            RecalculationEngine.Recalculate(actionsCopy);

            // Find the two competing actions by recordingId
            var rec2Action = actionsCopy
                .First(a => a.Type == GameActionType.ScienceEarning
                    && a.SubjectId == "crewReport@KerbinSrfLandedLaunchpad"
                    && a.RecordingId == "rec-2");
            var rec1Action = actionsCopy
                .First(a => a.Type == GameActionType.ScienceEarning
                    && a.SubjectId == "crewReport@KerbinSrfLandedLaunchpad"
                    && a.RecordingId == "rec-1");

            // Assert: rec-2 (UT=200) gets full credit since it's earlier
            Assert.Equal(5.0f, rec2Action.EffectiveScience, (float)0.1);

            // Assert: rec-1 (UT=500) is capped to 0 — subject already at max
            Assert.Equal(0.0f, rec1Action.EffectiveScience, (float)0.1);

            // Assert: Total credited for subject is still 5 (max)
            Assert.Equal(5.0, LedgerOrchestrator.Science.GetSubjectCredited("crewReport@KerbinSrfLandedLaunchpad"), 1);

            // Assert: Science balance is unchanged from the original timeline
            // because the same 5 science was earned — just by a different recording
            // 0 + 5 (rec-2) + 0 (rec-1 capped) + 8 + 12 + 30 + 5 (contract) - 5 - 5 = 50
            Assert.Equal(50.0, LedgerOrchestrator.Science.GetRunningScience(), 1);
        }

        // ================================================================
        // Test 3: Double recalculate is idempotent
        // ================================================================

        [Fact]
        public void DoubleRecalculate_IsIdempotent()
        {
            // Arrange
            LedgerOrchestrator.Contracts.SetMaxSlots(7);
            LedgerOrchestrator.Strategies.SetMaxSlots(3);

            var actions = BuildMunLandingTimeline();
            Ledger.AddActions(actions);

            // Act: first recalculation
            var actionsCopy = new List<GameAction>(Ledger.Actions);
            RecalculationEngine.Recalculate(actionsCopy);

            double scienceBalance1 = LedgerOrchestrator.Science.GetRunningScience();
            double fundsBalance1 = LedgerOrchestrator.Funds.GetRunningBalance();
            float repBalance1 = LedgerOrchestrator.Reputation.GetRunningRep();
            int milestoneCount1 = LedgerOrchestrator.Milestones.GetCreditedCount();
            bool contractCredited1 = LedgerOrchestrator.Contracts.IsContractCredited("explore-mun");
            int facilityLevel1 = LedgerOrchestrator.Facilities.GetFacilityLevel("TrackingStation");
            double availableScience1 = LedgerOrchestrator.Science.GetAvailableScience();
            double availableFunds1 = LedgerOrchestrator.Funds.GetAvailableFunds();

            // Capture per-action effective flags from first run
            var effective1 = actionsCopy.Select(a => a.Effective).ToList();
            var effectiveScience1 = actionsCopy.Select(a => a.EffectiveScience).ToList();
            var affordable1 = actionsCopy.Select(a => a.Affordable).ToList();

            // Act: second recalculation on the same actions
            RecalculationEngine.Recalculate(actionsCopy);

            // Assert: module-level state is identical
            Assert.Equal(scienceBalance1, LedgerOrchestrator.Science.GetRunningScience());
            Assert.Equal(fundsBalance1, LedgerOrchestrator.Funds.GetRunningBalance());
            Assert.Equal(repBalance1, LedgerOrchestrator.Reputation.GetRunningRep());
            Assert.Equal(milestoneCount1, LedgerOrchestrator.Milestones.GetCreditedCount());
            Assert.Equal(contractCredited1, LedgerOrchestrator.Contracts.IsContractCredited("explore-mun"));
            Assert.Equal(facilityLevel1, LedgerOrchestrator.Facilities.GetFacilityLevel("TrackingStation"));
            Assert.Equal(availableScience1, LedgerOrchestrator.Science.GetAvailableScience());
            Assert.Equal(availableFunds1, LedgerOrchestrator.Funds.GetAvailableFunds());

            // Assert: per-action derived fields are identical
            for (int i = 0; i < actionsCopy.Count; i++)
            {
                Assert.Equal(effective1[i], actionsCopy[i].Effective);
                Assert.Equal(effectiveScience1[i], actionsCopy[i].EffectiveScience);
                Assert.Equal(affordable1[i], actionsCopy[i].Affordable);
            }
        }
    }
}
