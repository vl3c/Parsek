using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class StrategiesModuleTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly StrategiesModule module;

        public StrategiesModuleTests()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            module = new StrategiesModule();
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Helpers — build strategy actions
        // ================================================================

        private static GameAction MakeActivate(
            string strategyId, double ut,
            StrategyResource source = StrategyResource.Reputation,
            StrategyResource target = StrategyResource.Science,
            float commitment = 0.10f,
            float setupCost = 5f)
        {
            return new GameAction
            {
                Type = GameActionType.StrategyActivate,
                UT = ut,
                StrategyId = strategyId,
                SourceResource = source,
                TargetResource = target,
                Commitment = commitment,
                SetupCost = setupCost
            };
        }

        private static GameAction MakeDeactivate(string strategyId, double ut)
        {
            return new GameAction
            {
                Type = GameActionType.StrategyDeactivate,
                UT = ut,
                StrategyId = strategyId
            };
        }

        private static GameAction MakeContractComplete(
            string contractId, double ut,
            float fundsReward = 0f,
            float repReward = 0f,
            float scienceReward = 0f,
            bool effective = true)
        {
            return new GameAction
            {
                Type = GameActionType.ContractComplete,
                UT = ut,
                ContractId = contractId,
                FundsReward = fundsReward,
                RepReward = repReward,
                ScienceReward = scienceReward,
                Effective = effective
            };
        }

        /// <summary>
        /// Helper for float assertions — casts to double to avoid xUnit overload ambiguity.
        /// </summary>
        private static void AssertFloatEqual(float expected, float actual, int precision = 2)
        {
            Assert.Equal((double)expected, (double)actual, precision);
        }

        // ================================================================
        // Design doc 11.6 — Scenario 1: Basic transform within active window
        // ================================================================

        [Fact]
        public void BasicTransform_WithinWindow()
        {
            // Strategy "Unpaid Research" activated at UT=300 (10% REP->SCIENCE, setup cost: -5 rep).
            // UT=400: Contract reward +40k funds, +50 rep. -> transformed: +45 rep, +5 science.
            // UT=600: Contract reward +30k funds, +30 rep. -> transformed: +27 rep, +3 science.
            // UT=700: Strategy deactivated.
            // UT=800: Contract reward +20k funds, +40 rep. (no transform - outside window)

            var activate = MakeActivate("UnpaidResearch", 300.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.10f, setupCost: 5f);
            module.ProcessAction(activate);

            // UT=400: inside window
            var contract1 = MakeContractComplete("c1", 400.0,
                fundsReward: 40000f, repReward: 50f, scienceReward: 0f);
            module.ProcessAction(contract1);

            Assert.Equal(40000f, contract1.FundsReward);
            AssertFloatEqual(45f, contract1.RepReward);
            AssertFloatEqual(5f, contract1.ScienceReward);

            // UT=600: inside window
            var contract2 = MakeContractComplete("c2", 600.0,
                fundsReward: 30000f, repReward: 30f, scienceReward: 0f);
            module.ProcessAction(contract2);

            Assert.Equal(30000f, contract2.FundsReward);
            AssertFloatEqual(27f, contract2.RepReward);
            AssertFloatEqual(3f, contract2.ScienceReward);

            // UT=700: deactivate
            var deactivate = MakeDeactivate("UnpaidResearch", 700.0);
            module.ProcessAction(deactivate);

            // UT=800: outside window (strategy deactivated)
            var contract3 = MakeContractComplete("c3", 800.0,
                fundsReward: 20000f, repReward: 40f, scienceReward: 0f);
            module.ProcessAction(contract3);

            Assert.Equal(20000f, contract3.FundsReward);
            Assert.Equal(40f, contract3.RepReward);
            Assert.Equal(0f, contract3.ScienceReward);
        }

        // ================================================================
        // Design doc 11.6 — Scenario 2: Retroactive commit outside window
        // ================================================================

        [Fact]
        public void RetroactiveCommit_OutsideWindow()
        {
            // Strategy active UT=200 to UT=600.
            // Contract completed at UT=100 (BEFORE window). No transform.
            // The contract's Effective flag was set by ContractsModule in first tier.

            var activate = MakeActivate("UnpaidResearch", 200.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.10f);
            module.ProcessAction(activate);

            // UT=100: before activation window, effective=true
            var contract = MakeContractComplete("orbit-mun", 100.0,
                fundsReward: 50000f, repReward: 50f, scienceReward: 0f);
            module.ProcessAction(contract);

            // No transform — contract UT < strategy ActivateUT
            Assert.Equal(50000f, contract.FundsReward);
            Assert.Equal(50f, contract.RepReward);
            Assert.Equal(0f, contract.ScienceReward);
        }

        // ================================================================
        // Design doc 11.6 — Scenario 3: Reservation blocks new strategy
        // ================================================================

        [Fact]
        public void ReservationBlocksNewStrategy()
        {
            // Admin building: 1 slot.
            // Strategy A activated at UT=1000. Reserved from UT=0.
            // GetAvailableSlots() should return 0.
            module.SetMaxSlots(1);

            var activate = MakeActivate("StrategyA", 1000.0);
            module.ProcessAction(activate);

            Assert.Equal(1, module.GetActiveStrategyCount());
            Assert.Equal(0, module.GetAvailableSlots());
        }

        // ================================================================
        // Unit tests — Activate and deactivate
        // ================================================================

        [Fact]
        public void Activate_AddsStrategy()
        {
            var action = MakeActivate("PatentsLicensing", 500.0,
                StrategyResource.Science, StrategyResource.Funds,
                commitment: 0.15f, setupCost: 10f);

            module.ProcessAction(action);

            Assert.True(module.IsStrategyActive("PatentsLicensing"));
            Assert.Equal(1, module.GetActiveStrategyCount());
        }

        [Fact]
        public void Deactivate_RemovesStrategy()
        {
            module.ProcessAction(MakeActivate("StratA", 100.0));
            Assert.True(module.IsStrategyActive("StratA"));

            module.ProcessAction(MakeDeactivate("StratA", 200.0));
            Assert.False(module.IsStrategyActive("StratA"));
            Assert.Equal(0, module.GetActiveStrategyCount());
        }

        [Fact]
        public void Deactivate_NonexistentStrategy_Ignored()
        {
            // Should not throw, just warn
            module.ProcessAction(MakeDeactivate("DoesNotExist", 100.0));

            Assert.Equal(0, module.GetActiveStrategyCount());
            Assert.Contains(logLines, l =>
                l.Contains("[Strategies]") &&
                l.Contains("not currently active"));
        }

        // ================================================================
        // Unit tests — Slot counting
        // ================================================================

        [Fact]
        public void SlotCounting_MultipleStrategies()
        {
            module.SetMaxSlots(3);

            module.ProcessAction(MakeActivate("S1", 100.0));
            module.ProcessAction(MakeActivate("S2", 200.0));

            Assert.Equal(2, module.GetActiveStrategyCount());
            Assert.Equal(1, module.GetAvailableSlots());

            module.ProcessAction(MakeActivate("S3", 300.0));
            Assert.Equal(3, module.GetActiveStrategyCount());
            Assert.Equal(0, module.GetAvailableSlots());
        }

        [Fact]
        public void SlotCounting_DeactivateFreesSlot()
        {
            module.SetMaxSlots(2);

            module.ProcessAction(MakeActivate("S1", 100.0));
            module.ProcessAction(MakeActivate("S2", 200.0));
            Assert.Equal(0, module.GetAvailableSlots());

            module.ProcessAction(MakeDeactivate("S1", 300.0));
            Assert.Equal(1, module.GetAvailableSlots());
            Assert.Equal(1, module.GetActiveStrategyCount());
        }

        // ================================================================
        // Unit tests — Reset clears state
        // ================================================================

        [Fact]
        public void Reset_ClearsAllStrategies()
        {
            module.ProcessAction(MakeActivate("S1", 100.0));
            module.ProcessAction(MakeActivate("S2", 200.0));
            Assert.Equal(2, module.GetActiveStrategyCount());

            module.Reset();

            Assert.Equal(0, module.GetActiveStrategyCount());
            Assert.False(module.IsStrategyActive("S1"));
            Assert.False(module.IsStrategyActive("S2"));
        }

        // ================================================================
        // Unit tests — Ignore non-strategy actions
        // ================================================================

        [Theory]
        [InlineData(GameActionType.ScienceEarning)]
        [InlineData(GameActionType.ScienceSpending)]
        [InlineData(GameActionType.FundsEarning)]
        [InlineData(GameActionType.FundsSpending)]
        [InlineData(GameActionType.MilestoneAchievement)]
        [InlineData(GameActionType.ContractAccept)]
        [InlineData(GameActionType.ReputationEarning)]
        [InlineData(GameActionType.ReputationPenalty)]
        [InlineData(GameActionType.KerbalAssignment)]
        [InlineData(GameActionType.KerbalHire)]
        [InlineData(GameActionType.FacilityUpgrade)]
        [InlineData(GameActionType.FundsInitial)]
        public void ProcessAction_IgnoresNonStrategyNonContractActions(GameActionType type)
        {
            var action = new GameAction { Type = type, UT = 1000.0 };

            module.ProcessAction(action);

            Assert.Equal(0, module.GetActiveStrategyCount());
        }

        // ================================================================
        // Transform tests — source/target resource mapping
        // ================================================================

        [Fact]
        public void Transform_FundsToReputation()
        {
            // Strategy: 20% of Funds -> Reputation
            module.ProcessAction(MakeActivate("FundsToRep", 100.0,
                StrategyResource.Funds, StrategyResource.Reputation,
                commitment: 0.20f));

            var contract = MakeContractComplete("c1", 200.0,
                fundsReward: 10000f, repReward: 10f, scienceReward: 5f);
            module.ProcessAction(contract);

            // 20% of 10000 funds diverted = 2000
            AssertFloatEqual(8000f, contract.FundsReward);
            AssertFloatEqual(2010f, contract.RepReward);  // 10 + 2000
            AssertFloatEqual(5f, contract.ScienceReward);  // unchanged
        }

        [Fact]
        public void Transform_ScienceToFunds()
        {
            // Strategy: 25% of Science -> Funds
            module.ProcessAction(MakeActivate("SciToFunds", 100.0,
                StrategyResource.Science, StrategyResource.Funds,
                commitment: 0.25f));

            var contract = MakeContractComplete("c1", 200.0,
                fundsReward: 1000f, repReward: 0f, scienceReward: 40f);
            module.ProcessAction(contract);

            // 25% of 40 science diverted = 10
            AssertFloatEqual(1010f, contract.FundsReward);   // 1000 + 10
            AssertFloatEqual(0f, contract.RepReward);
            AssertFloatEqual(30f, contract.ScienceReward);   // 40 - 10
        }

        [Fact]
        public void Transform_ReputationToFunds()
        {
            // Strategy: 15% of Reputation -> Funds
            module.ProcessAction(MakeActivate("RepToFunds", 100.0,
                StrategyResource.Reputation, StrategyResource.Funds,
                commitment: 0.15f));

            var contract = MakeContractComplete("c1", 200.0,
                fundsReward: 5000f, repReward: 100f, scienceReward: 0f);
            module.ProcessAction(contract);

            // 15% of 100 rep diverted = 15
            AssertFloatEqual(5015f, contract.FundsReward);   // 5000 + 15
            AssertFloatEqual(85f, contract.RepReward);        // 100 - 15
            AssertFloatEqual(0f, contract.ScienceReward);
        }

        // ================================================================
        // Transform tests — commitment percentage
        // ================================================================

        [Fact]
        public void Transform_MinCommitment_OnePercent()
        {
            module.ProcessAction(MakeActivate("MinCommit", 100.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.01f));

            var contract = MakeContractComplete("c1", 200.0,
                repReward: 100f, scienceReward: 0f);
            module.ProcessAction(contract);

            AssertFloatEqual(99f, contract.RepReward);
            AssertFloatEqual(1f, contract.ScienceReward);
        }

        [Fact]
        public void Transform_MaxCommitment_TwentyFivePercent()
        {
            module.ProcessAction(MakeActivate("MaxCommit", 100.0,
                StrategyResource.Funds, StrategyResource.Science,
                commitment: 0.25f));

            var contract = MakeContractComplete("c1", 200.0,
                fundsReward: 40000f, scienceReward: 0f);
            module.ProcessAction(contract);

            AssertFloatEqual(30000f, contract.FundsReward);
            AssertFloatEqual(10000f, contract.ScienceReward);
        }

        // ================================================================
        // Transform tests — non-effective contracts not transformed
        // ================================================================

        [Fact]
        public void Transform_NonEffectiveContract_NotTransformed()
        {
            module.ProcessAction(MakeActivate("UnpaidResearch", 100.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.10f));

            // Non-effective (duplicate completion zeroed by ContractsModule)
            var contract = MakeContractComplete("c1", 200.0,
                fundsReward: 10000f, repReward: 50f, scienceReward: 0f,
                effective: false);
            module.ProcessAction(contract);

            // No transform
            Assert.Equal(10000f, contract.FundsReward);
            Assert.Equal(50f, contract.RepReward);
            Assert.Equal(0f, contract.ScienceReward);
        }

        // ================================================================
        // Transform tests — no active strategies means no transform
        // ================================================================

        [Fact]
        public void Transform_NoActiveStrategies_NoTransform()
        {
            var contract = MakeContractComplete("c1", 200.0,
                fundsReward: 10000f, repReward: 50f, scienceReward: 10f);
            module.ProcessAction(contract);

            Assert.Equal(10000f, contract.FundsReward);
            Assert.Equal(50f, contract.RepReward);
            Assert.Equal(10f, contract.ScienceReward);
        }

        // ================================================================
        // Transform tests — contract before activation UT not transformed
        // ================================================================

        [Fact]
        public void Transform_ContractBeforeActivationUT_NotTransformed()
        {
            // Strategy activates at UT=500
            module.ProcessAction(MakeActivate("LateStrat", 500.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.10f));

            // Contract at UT=300 (before activation)
            var contract = MakeContractComplete("c1", 300.0,
                repReward: 100f, scienceReward: 0f);
            module.ProcessAction(contract);

            Assert.Equal(100f, contract.RepReward);
            Assert.Equal(0f, contract.ScienceReward);
        }

        // ================================================================
        // Transform tests — milestone actions not transformed
        // ================================================================

        [Fact]
        public void Transform_MilestoneNotTransformed()
        {
            // Strategies only transform contract rewards, not milestones
            module.ProcessAction(MakeActivate("S1", 100.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.10f));

            var milestone = new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = 200.0,
                MilestoneId = "FirstOrbit",
                MilestoneFundsAwarded = 10000f,
                MilestoneRepAwarded = 15f,
                Effective = true
            };
            module.ProcessAction(milestone);

            // Milestone funds/rep unchanged (module ignores non-ContractComplete)
            Assert.Equal(10000f, milestone.MilestoneFundsAwarded);
            Assert.Equal(15f, milestone.MilestoneRepAwarded);
        }

        // ================================================================
        // Transform tests — multiple active strategies
        // ================================================================

        [Fact]
        public void Transform_MultipleStrategies_BothApply()
        {
            module.SetMaxSlots(3);

            // Strategy 1: 10% Rep -> Science
            module.ProcessAction(MakeActivate("RepToSci", 100.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.10f));

            // Strategy 2: 20% Funds -> Reputation
            module.ProcessAction(MakeActivate("FundsToRep", 100.0,
                StrategyResource.Funds, StrategyResource.Reputation,
                commitment: 0.20f));

            var contract = MakeContractComplete("c1", 200.0,
                fundsReward: 10000f, repReward: 100f, scienceReward: 0f);
            module.ProcessAction(contract);

            // Both strategies apply. Since they touch different source resources,
            // order does not matter:
            // Rep -> Sci: 10% of 100 = 10 diverted. Rep = 90, Sci = 10.
            // Funds -> Rep: 20% of 10000 = 2000 diverted. Funds = 8000, Rep = 90 + 2000 = 2090.
            AssertFloatEqual(8000f, contract.FundsReward);
            AssertFloatEqual(2090f, contract.RepReward);
            AssertFloatEqual(10f, contract.ScienceReward);
        }

        // ================================================================
        // Log assertion tests
        // ================================================================

        [Fact]
        public void Activate_LogsStrategyId()
        {
            module.ProcessAction(MakeActivate("UnpaidResearch", 300.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.10f, setupCost: 5f));

            Assert.Contains(logLines, l =>
                l.Contains("[Strategies]") &&
                l.Contains("Activate") &&
                l.Contains("UnpaidResearch"));
        }

        [Fact]
        public void Deactivate_LogsStrategyId()
        {
            module.ProcessAction(MakeActivate("S1", 100.0));
            module.ProcessAction(MakeDeactivate("S1", 200.0));

            Assert.Contains(logLines, l =>
                l.Contains("[Strategies]") &&
                l.Contains("Deactivate") &&
                l.Contains("S1"));
        }

        [Fact]
        public void Transform_LogsDiversionDetails()
        {
            module.ProcessAction(MakeActivate("RepToSci", 100.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.10f));

            var contract = MakeContractComplete("c1", 200.0,
                repReward: 50f, scienceReward: 0f);
            module.ProcessAction(contract);

            Assert.Contains(logLines, l =>
                l.Contains("[Strategies]") &&
                l.Contains("Transform") &&
                l.Contains("RepToSci") &&
                l.Contains("diverted"));
        }

        [Fact]
        public void Reset_LogsClearedCount()
        {
            module.ProcessAction(MakeActivate("S1", 100.0));
            module.ProcessAction(MakeActivate("S2", 200.0));

            logLines.Clear();
            module.Reset();

            Assert.Contains(logLines, l =>
                l.Contains("[Strategies]") &&
                l.Contains("Reset") &&
                l.Contains("cleared 2"));
        }

        // ================================================================
        // Integration — works with RecalculationEngine
        // ================================================================

        [Fact]
        public void Integration_EngineDispatchesStrategyActions()
        {
            RecalculationEngine.RegisterModule(module, RecalculationEngine.ModuleTier.Strategy);

            var actions = new List<GameAction>
            {
                MakeActivate("UnpaidResearch", 300.0,
                    StrategyResource.Reputation, StrategyResource.Science,
                    commitment: 0.10f, setupCost: 5f),
                MakeContractComplete("c1", 400.0,
                    fundsReward: 40000f, repReward: 50f, scienceReward: 0f),
                MakeDeactivate("UnpaidResearch", 700.0),
                MakeContractComplete("c2", 800.0,
                    fundsReward: 20000f, repReward: 40f, scienceReward: 0f)
            };

            RecalculationEngine.Recalculate(actions);

            // c1 at UT=400 inside strategy window: rep 50->45, sci 0->5
            AssertFloatEqual(45f, actions[1].RepReward);
            AssertFloatEqual(5f, actions[1].ScienceReward);

            // c2 at UT=800 outside strategy window: no transform
            Assert.Equal(40f, actions[3].RepReward);
            Assert.Equal(0f, actions[3].ScienceReward);
        }

        // ================================================================
        // Edge cases
        // ================================================================

        [Fact]
        public void Activate_SameStrategyTwice_OverwritesPrevious()
        {
            module.ProcessAction(MakeActivate("S1", 100.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.10f));

            // Activate again with different commitment — overwrites
            module.ProcessAction(MakeActivate("S1", 200.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.20f));

            Assert.Equal(1, module.GetActiveStrategyCount());

            // Verify the new commitment applies
            var contract = MakeContractComplete("c1", 300.0, repReward: 100f, scienceReward: 0f);
            module.ProcessAction(contract);

            AssertFloatEqual(80f, contract.RepReward);    // 20% diverted
            AssertFloatEqual(20f, contract.ScienceReward);
        }

        [Fact]
        public void Transform_ZeroReward_NoDivision()
        {
            module.ProcessAction(MakeActivate("S1", 100.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.10f));

            var contract = MakeContractComplete("c1", 200.0,
                fundsReward: 10000f, repReward: 0f, scienceReward: 0f);
            module.ProcessAction(contract);

            // 10% of 0 rep = 0 diverted, no change
            Assert.Equal(10000f, contract.FundsReward);
            Assert.Equal(0f, contract.RepReward);
            Assert.Equal(0f, contract.ScienceReward);
        }

        [Fact]
        public void IsStrategyActive_False_BeforeActivation()
        {
            Assert.False(module.IsStrategyActive("NonexistentStrategy"));
        }

        [Fact]
        public void GetAvailableSlots_DefaultMaxSlots()
        {
            // Default maxSlots is 1 (Admin building level 1)
            Assert.Equal(1, module.GetAvailableSlots());

            module.ProcessAction(MakeActivate("S1", 100.0));
            Assert.Equal(0, module.GetAvailableSlots());
        }
    }
}
