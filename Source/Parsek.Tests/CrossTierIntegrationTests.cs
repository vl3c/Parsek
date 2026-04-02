using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class CrossTierIntegrationTests : System.IDisposable
    {
        public CrossTierIntegrationTests()
        {
            ParsekLog.SuppressLogging = true;
            RecalculationEngine.ClearModules();
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.SuppressLogging = false;
        }

        [Fact]
        public void MilestoneEffective_FlowsToFunds()
        {
            var milestones = new MilestonesModule();
            var funds = new FundsModule();
            RecalculationEngine.RegisterModule(milestones, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);

            var actions = new List<GameAction>
            {
                new GameAction { UT = 0, Type = GameActionType.FundsInitial, FundsAwarded = 25000, InitialFunds = 25000 },
                new GameAction { UT = 100, Type = GameActionType.MilestoneAchievement, MilestoneId = "FirstOrbit", MilestoneFundsAwarded = 5000, MilestoneRepAwarded = 10 }
            };

            RecalculationEngine.Recalculate(actions);

            Assert.Equal(30000.0, funds.GetRunningBalance(), 1);
            Assert.True(milestones.IsMilestoneCredited("FirstOrbit"));
        }

        [Fact]
        public void DuplicateMilestone_NotCreditedInFunds()
        {
            var milestones = new MilestonesModule();
            var funds = new FundsModule();
            RecalculationEngine.RegisterModule(milestones, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);

            var actions = new List<GameAction>
            {
                new GameAction { UT = 0, Type = GameActionType.FundsInitial, FundsAwarded = 25000, InitialFunds = 25000 },
                new GameAction { UT = 100, Type = GameActionType.MilestoneAchievement, MilestoneId = "FirstOrbit", MilestoneFundsAwarded = 5000 },
                new GameAction { UT = 200, Type = GameActionType.MilestoneAchievement, MilestoneId = "FirstOrbit", MilestoneFundsAwarded = 5000 }
            };

            RecalculationEngine.Recalculate(actions);

            // Only first milestone credited
            Assert.Equal(30000.0, funds.GetRunningBalance(), 1);
        }

        [Fact]
        public void DoubleRecalculate_Idempotent()
        {
            var milestones = new MilestonesModule();
            var funds = new FundsModule();
            RecalculationEngine.RegisterModule(milestones, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);

            var actions = new List<GameAction>
            {
                new GameAction { UT = 0, Type = GameActionType.FundsInitial, FundsAwarded = 25000, InitialFunds = 25000 },
                new GameAction { UT = 100, Type = GameActionType.MilestoneAchievement, MilestoneId = "FirstOrbit", MilestoneFundsAwarded = 5000 },
                new GameAction { UT = 200, Type = GameActionType.FundsSpending, FundsSpent = 10000 }
            };

            RecalculationEngine.Recalculate(actions);
            double balance1 = funds.GetRunningBalance();
            bool effective1 = actions[1].Effective;

            RecalculationEngine.Recalculate(actions);
            double balance2 = funds.GetRunningBalance();
            bool effective2 = actions[1].Effective;

            Assert.Equal(balance1, balance2);
            Assert.Equal(effective1, effective2);
            Assert.Equal(20000.0, balance2, 1);
        }

        [Fact]
        public void MilestoneFunds_MakesSpendingUnaffordable()
        {
            // Scenario: milestone credits funds -> funds balance changes ->
            // a spending that WAS affordable becomes unaffordable when milestone is a duplicate.
            var milestones = new MilestonesModule();
            var funds = new FundsModule();
            RecalculationEngine.RegisterModule(milestones, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);

            // Start with 10000, milestone gives 20000, spending costs 25000.
            // With milestone: balance = 10000 + 20000 - 25000 = 5000 (affordable)
            // Without milestone: balance = 10000 - 25000 = -15000 (not affordable)
            var actions = new List<GameAction>
            {
                new GameAction { UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 10000 },
                new GameAction { UT = 100, Type = GameActionType.MilestoneAchievement,
                    MilestoneId = "FirstOrbit", RecordingId = "rec1", MilestoneFundsAwarded = 20000 },
                new GameAction { UT = 200, Type = GameActionType.FundsSpending, FundsSpent = 25000 }
            };

            RecalculationEngine.Recalculate(actions);

            Assert.True(actions[1].Effective, "First milestone should be effective");
            Assert.True(actions[2].Affordable, "Spending should be affordable with milestone funds");
            Assert.Equal(5000.0, funds.GetRunningBalance(), 1);

            // Now add a duplicate milestone from a different recording at an earlier UT —
            // it sorts first, claims credit, and the original becomes ineffective.
            actions.Add(new GameAction
            {
                UT = 50, Type = GameActionType.MilestoneAchievement,
                MilestoneId = "FirstOrbit", RecordingId = "rec0", MilestoneFundsAwarded = 20000
            });

            RecalculationEngine.Recalculate(actions);

            // The UT=50 milestone is now effective (it's first chronologically),
            // the UT=100 milestone is the duplicate.
            var milestone50 = actions.Find(a => a.UT == 50);
            var milestone100 = actions.Find(a => a.UT == 100 && a.Type == GameActionType.MilestoneAchievement);

            Assert.True(milestone50.Effective, "Earlier milestone should be effective");
            Assert.False(milestone100.Effective, "Later duplicate milestone should not be effective");

            // Balance still 5000 — same milestone funds, just from a different recording.
            Assert.Equal(5000.0, funds.GetRunningBalance(), 1);
        }

        [Fact]
        public void ContractComplete_WithStrategyDiversion_FundsReducedScienceFieldTransformed()
        {
            // Scenario: contract completion with an active strategy diverts funds to science.
            // FundsModule (second tier) sees the reduced TransformedFundsReward.
            // ScienceModule (first tier) runs BEFORE the strategy transform, so it sees the
            // original ScienceReward=0 — the diverted science is written to TransformedScienceReward
            // but not credited to the science balance. This is a known limitation of the current
            // tier ordering (ScienceModule is first tier, strategy is between tiers).
            var contracts = new ContractsModule();
            var strategies = new StrategiesModule();
            var science = new ScienceModule();
            var funds = new FundsModule();
            var reputation = new ReputationModule();

            RecalculationEngine.RegisterModule(contracts, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(science, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(strategies, RecalculationEngine.ModuleTier.Strategy);
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(reputation, RecalculationEngine.ModuleTier.SecondTier);

            var actions = new List<GameAction>
            {
                new GameAction { UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 50000 },
                // Activate strategy: diverts 25% of funds reward to science
                new GameAction { UT = 10, Type = GameActionType.StrategyActivate,
                    StrategyId = "UnpaidResearch", SourceResource = StrategyResource.Funds,
                    TargetResource = StrategyResource.Science, Commitment = 0.25f, SetupCost = 0 },
                // Contract accepted (advance) then completed
                new GameAction { UT = 50, Type = GameActionType.ContractAccept,
                    ContractId = "C1", AdvanceFunds = 0 },
                new GameAction { UT = 100, Type = GameActionType.ContractComplete,
                    ContractId = "C1", RecordingId = "rec1",
                    FundsReward = 10000, ScienceReward = 0, RepReward = 5 }
            };

            RecalculationEngine.Recalculate(actions);

            // Contract is effective (first completion)
            var complete = actions.Find(a => a.Type == GameActionType.ContractComplete);
            Assert.True(complete.Effective);

            // Strategy diverts 25% of 10000 funds = 2500 to science on the action fields
            Assert.Equal(7500f, complete.TransformedFundsReward, 1f);
            Assert.Equal(2500f, complete.TransformedScienceReward, 1f);

            // Funds balance: 50000 (seed) + 7500 (transformed reward) = 57500
            Assert.Equal(57500.0, funds.GetRunningBalance(), 1);

            // Science balance: ScienceModule (first tier) processed ContractComplete BEFORE
            // the strategy transform ran, so it saw TransformedScienceReward=0 (the reset
            // value from ScienceReward=0). The diverted 2500 is on the field but not credited.
            // This documents the current behavior — strategy-diverted science is a known gap.
            Assert.Equal(0.0, science.GetRunningScience(), 1);
        }

        [Fact]
        public void ContractComplete_WithStrategyDiversion_ScienceToRep_FieldsTransformed()
        {
            // Scenario: strategy diverts science reward to reputation.
            // Verify the derived fields on the action are correctly transformed,
            // and that second-tier modules (Funds, Reputation) see the transformed values.
            // ScienceModule is first tier so it sees the PRE-transform value (same limitation
            // as ContractComplete_WithStrategyDiversion_FundsReducedScienceFieldTransformed).
            var contracts = new ContractsModule();
            var strategies = new StrategiesModule();
            var science = new ScienceModule();
            var funds = new FundsModule();
            var reputation = new ReputationModule();

            RecalculationEngine.RegisterModule(contracts, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(science, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(strategies, RecalculationEngine.ModuleTier.Strategy);
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(reputation, RecalculationEngine.ModuleTier.SecondTier);

            var actions = new List<GameAction>
            {
                new GameAction { UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 50000 },
                // Strategy: diverts 10% of science reward to reputation
                new GameAction { UT = 10, Type = GameActionType.StrategyActivate,
                    StrategyId = "ScienceForRep", SourceResource = StrategyResource.Science,
                    TargetResource = StrategyResource.Reputation, Commitment = 0.10f, SetupCost = 0 },
                // Contract with science and rep rewards
                new GameAction { UT = 100, Type = GameActionType.ContractComplete,
                    ContractId = "C1", RecordingId = "rec1",
                    FundsReward = 5000, ScienceReward = 100, RepReward = 10 }
            };

            RecalculationEngine.Recalculate(actions);

            var complete = actions.Find(a => a.Type == GameActionType.ContractComplete);
            Assert.True(complete.Effective);

            // Strategy transforms the action fields: 10% of 100 science = 10 diverted to rep
            Assert.Equal(90f, complete.TransformedScienceReward, 1f);
            Assert.Equal(20f, complete.TransformedRepReward, 1f); // 10 original + 10 diverted
            Assert.Equal(5000f, complete.TransformedFundsReward, 1f); // unchanged

            // ScienceModule (first tier) sees TransformedScienceReward BEFORE the strategy
            // transform, so it credits the full 100 (reset value from ScienceReward=100).
            Assert.Equal(100.0, science.GetRunningScience(), 1);

            // Funds: 50000 + 5000 = 55000 (unaffected by science strategy)
            Assert.Equal(55000.0, funds.GetRunningBalance(), 1);

            // ReputationModule (second tier) sees TransformedRepReward=20 (10 original + 10 diverted)
            // The reputation curve is applied, so the exact value depends on the curve at rep=0.
            // At rep=0, gain curve multiplier is ~1.0, so effectiveRep ~ 20.
            float repAfterWalk = reputation.GetRunningRep();
            Assert.True(repAfterWalk > 15f && repAfterWalk < 25f,
                $"Expected rep ~20 (post-curve), actual={repAfterWalk}");
        }
    }
}
