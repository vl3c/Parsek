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
        public void ContractComplete_WithStrategyActive_TransformsAreIdentityAfterPhaseE1_5()
        {
            // #439 Phase A (Option B): StrategiesModule.TransformContractReward is now
            // an identity no-op because KSP's CurrencyModifierQuery already transformed
            // the reward before the FundsChanged event fired (which is what the
            // recorder reads via contract.FundsCompletion). Applying Commitment a
            // second time would double-divert. See
            // docs/dev/plans/fix-439-strategy-lifecycle-capture.md section 3.5.
            //
            // This test pins the invariant at the cross-tier level: with an active
            // strategy, TransformedFundsReward stays equal to FundsReward, and the
            // downstream FundsModule credits the raw reward.
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
                new GameAction { UT = 10, Type = GameActionType.StrategyActivate,
                    StrategyId = "UnpaidResearch", SourceResource = StrategyResource.Funds,
                    TargetResource = StrategyResource.Science, Commitment = 0.25f, SetupCost = 0 },
                new GameAction { UT = 50, Type = GameActionType.ContractAccept,
                    ContractId = "C1", AdvanceFunds = 0 },
                new GameAction { UT = 100, Type = GameActionType.ContractComplete,
                    ContractId = "C1", RecordingId = "rec1",
                    FundsReward = 10000, ScienceReward = 0, RepReward = 5 }
            };

            RecalculationEngine.Recalculate(actions);

            var complete = actions.Find(a => a.Type == GameActionType.ContractComplete);
            Assert.True(complete.Effective);

            // Identity: no diversion applied; transformed == raw.
            Assert.Equal(10000f, complete.TransformedFundsReward, 1f);
            Assert.Equal(0f, complete.TransformedScienceReward, 1f);

            // Funds balance: 50000 (seed) + 10000 (raw/identity reward) = 60000.
            Assert.Equal(60000.0, funds.GetRunningBalance(), 1);

            // Science unchanged — ScienceReward was 0.
            Assert.Equal(0.0, science.GetRunningScience(), 1);

            // Strategy still registered for slot accounting and display.
            Assert.True(strategies.IsStrategyActive("UnpaidResearch"));
        }

        [Fact]
        public void ContractComplete_WithStrategyActive_ScienceReward_StillIdentity()
        {
            // #439 Phase A follow-up: even a science-bearing reward stays identity
            // under an active science-diverting strategy — double-transform averted.
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
                new GameAction { UT = 10, Type = GameActionType.StrategyActivate,
                    StrategyId = "ScienceForRep", SourceResource = StrategyResource.Science,
                    TargetResource = StrategyResource.Reputation, Commitment = 0.10f, SetupCost = 0 },
                new GameAction { UT = 100, Type = GameActionType.ContractComplete,
                    ContractId = "C1", RecordingId = "rec1",
                    FundsReward = 5000, ScienceReward = 100, RepReward = 10 }
            };

            RecalculationEngine.Recalculate(actions);

            var complete = actions.Find(a => a.Type == GameActionType.ContractComplete);
            Assert.True(complete.Effective);

            // Identity transform invariants.
            Assert.Equal(100f, complete.TransformedScienceReward, 1f);
            Assert.Equal(10f, complete.TransformedRepReward, 1f);
            Assert.Equal(5000f, complete.TransformedFundsReward, 1f);

            Assert.Equal(100.0, science.GetRunningScience(), 1);
            Assert.Equal(55000.0, funds.GetRunningBalance(), 1);

            // ReputationModule applies its curve to the raw rep reward of 10. Around
            // rep=0 the curve multiplier is ~1.0, so the final rep sits in [5, 15].
            float repAfterWalk = reputation.GetRunningRep();
            Assert.True(repAfterWalk > 5f && repAfterWalk < 15f,
                $"Expected rep ~10 (post-curve, identity transform), actual={repAfterWalk}");
        }
    }
}
