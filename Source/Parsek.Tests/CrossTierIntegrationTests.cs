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
    }
}
