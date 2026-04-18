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
                TransformedFundsReward = fundsReward,
                TransformedScienceReward = scienceReward,
                TransformedRepReward = repReward,
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
        public void BasicTransform_WithinWindow_IdentityAfterPhaseE1_5()
        {
            // #439 Phase A (Option B): TransformContractReward is now an identity no-op
            // because KSP's CurrencyModifierQuery already transformed the reward before
            // the FundsChanged / ReputationChanged / ScienceChanged events fired, which
            // is what feeds ContractComplete's reward fields. Applying Commitment a
            // second time would double-divert. See plan section 3.5 for the full
            // rationale. activeStrategies is still populated for slot-accounting and
            // Actions window display.
            var activate = MakeActivate("UnpaidResearch", 300.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.10f, setupCost: 5f);
            module.ProcessAction(activate);
            Assert.True(module.IsStrategyActive("UnpaidResearch"));

            // UT=400: inside window — identity, transformed fields equal raw rewards.
            var contract1 = MakeContractComplete("c1", 400.0,
                fundsReward: 40000f, repReward: 50f, scienceReward: 0f);
            module.ProcessAction(contract1);

            Assert.Equal(40000f, contract1.TransformedFundsReward);
            Assert.Equal(50f, contract1.TransformedRepReward);
            Assert.Equal(0f, contract1.TransformedScienceReward);

            // UT=600: still identity.
            var contract2 = MakeContractComplete("c2", 600.0,
                fundsReward: 30000f, repReward: 30f, scienceReward: 0f);
            module.ProcessAction(contract2);

            Assert.Equal(30000f, contract2.TransformedFundsReward);
            Assert.Equal(30f, contract2.TransformedRepReward);
            Assert.Equal(0f, contract2.TransformedScienceReward);

            // UT=700: deactivate — slot frees, but contract rewards remain identity.
            var deactivate = MakeDeactivate("UnpaidResearch", 700.0);
            module.ProcessAction(deactivate);
            Assert.False(module.IsStrategyActive("UnpaidResearch"));

            var contract3 = MakeContractComplete("c3", 800.0,
                fundsReward: 20000f, repReward: 40f, scienceReward: 0f);
            module.ProcessAction(contract3);

            Assert.Equal(20000f, contract3.TransformedFundsReward);
            Assert.Equal(40f, contract3.TransformedRepReward);
            Assert.Equal(0f, contract3.TransformedScienceReward);
        }

        // ================================================================
        // Design doc 11.6 — Scenario 2: Retroactive commit outside window
        // ================================================================

        [Fact]
        public void RetroactiveCommit_OutsideWindow()
        {
            // #439 Phase A: identity no-op regardless of whether the contract falls
            // inside or outside the strategy activation window.
            var activate = MakeActivate("UnpaidResearch", 200.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.10f);
            module.ProcessAction(activate);

            var contract = MakeContractComplete("orbit-mun", 100.0,
                fundsReward: 50000f, repReward: 50f, scienceReward: 0f);
            module.ProcessAction(contract);

            Assert.Equal(50000f, contract.TransformedFundsReward);
            Assert.Equal(50f, contract.TransformedRepReward);
            Assert.Equal(0f, contract.TransformedScienceReward);
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

        // #439 Phase A: TransformContractReward is an identity no-op — KSP's
        // CurrencyModifierQuery already transformed the reward before our event-side
        // capture saw it. The tests below pin the identity invariant for the three
        // resource source axes and the min/max commitment values.

        [Fact]
        public void Transform_FundsSource_IsIdentityNoOp()
        {
            module.ProcessAction(MakeActivate("FundsToRep", 100.0,
                StrategyResource.Funds, StrategyResource.Reputation,
                commitment: 0.20f));

            var contract = MakeContractComplete("c1", 200.0,
                fundsReward: 10000f, repReward: 10f, scienceReward: 5f);
            module.ProcessAction(contract);

            Assert.Equal(10000f, contract.TransformedFundsReward);
            Assert.Equal(10f, contract.TransformedRepReward);
            Assert.Equal(5f, contract.TransformedScienceReward);
        }

        [Fact]
        public void Transform_ScienceSource_IsIdentityNoOp()
        {
            module.ProcessAction(MakeActivate("SciToFunds", 100.0,
                StrategyResource.Science, StrategyResource.Funds,
                commitment: 0.25f));

            var contract = MakeContractComplete("c1", 200.0,
                fundsReward: 1000f, repReward: 0f, scienceReward: 40f);
            module.ProcessAction(contract);

            Assert.Equal(1000f, contract.TransformedFundsReward);
            Assert.Equal(0f, contract.TransformedRepReward);
            Assert.Equal(40f, contract.TransformedScienceReward);
        }

        [Fact]
        public void Transform_ReputationSource_IsIdentityNoOp()
        {
            module.ProcessAction(MakeActivate("RepToFunds", 100.0,
                StrategyResource.Reputation, StrategyResource.Funds,
                commitment: 0.15f));

            var contract = MakeContractComplete("c1", 200.0,
                fundsReward: 5000f, repReward: 100f, scienceReward: 0f);
            module.ProcessAction(contract);

            Assert.Equal(5000f, contract.TransformedFundsReward);
            Assert.Equal(100f, contract.TransformedRepReward);
            Assert.Equal(0f, contract.TransformedScienceReward);
        }

        // ================================================================
        // Transform tests — commitment percentage (identity invariant)
        // ================================================================

        [Fact]
        public void Transform_MinCommitment_StillIdentity()
        {
            module.ProcessAction(MakeActivate("MinCommit", 100.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.01f));

            var contract = MakeContractComplete("c1", 200.0,
                repReward: 100f, scienceReward: 0f);
            module.ProcessAction(contract);

            Assert.Equal(100f, contract.TransformedRepReward);
            Assert.Equal(0f, contract.TransformedScienceReward);
        }

        [Fact]
        public void Transform_MaxCommitment_StillIdentity()
        {
            module.ProcessAction(MakeActivate("MaxCommit", 100.0,
                StrategyResource.Funds, StrategyResource.Science,
                commitment: 0.25f));

            var contract = MakeContractComplete("c1", 200.0,
                fundsReward: 40000f, scienceReward: 0f);
            module.ProcessAction(contract);

            Assert.Equal(40000f, contract.TransformedFundsReward);
            Assert.Equal(0f, contract.TransformedScienceReward);
        }

        [Fact]
        public void TransformContractReward_IsIdentityNoOp_AfterPhaseE1_5()
        {
            // Pin the Phase A invariant: even with an active strategy whose source
            // resource matches the reward field, TransformedFundsReward stays equal to
            // FundsReward. Plan section 3.5 option B — KSP pre-transforms rewards
            // through the modifier query before the FundsChanged event fires, so the
            // raw-side double-transform this module used to do would double-divert.
            module.ProcessAction(MakeActivate("UnpaidResearch", 100.0,
                StrategyResource.Funds, StrategyResource.Science,
                commitment: 0.25f));

            var contract = MakeContractComplete("c1", 200.0,
                fundsReward: 80000f, repReward: 10f, scienceReward: 0f);
            module.ProcessAction(contract);

            Assert.Equal(contract.FundsReward, contract.TransformedFundsReward);
            Assert.Equal(contract.RepReward, contract.TransformedRepReward);
            Assert.Equal(contract.ScienceReward, contract.TransformedScienceReward);
            Assert.Contains(logLines, l =>
                l.Contains("[Strategies]") &&
                l.Contains("identity no-op") &&
                l.Contains("CurrencyModifierQuery"));
        }

        // ================================================================
        // Transform tests — non-effective contracts not transformed
        // ================================================================

        [Fact]
        public void Transform_NonEffectiveContract_NotTransformed()
        {
            // #439 Phase A: identity no-op applies to both effective and non-effective
            // contracts; the module no longer short-circuits on Effective=false because
            // there is nothing to short-circuit against.
            module.ProcessAction(MakeActivate("UnpaidResearch", 100.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.10f));

            var contract = MakeContractComplete("c1", 200.0,
                fundsReward: 10000f, repReward: 50f, scienceReward: 0f,
                effective: false);
            module.ProcessAction(contract);

            Assert.Equal(10000f, contract.TransformedFundsReward);
            Assert.Equal(50f, contract.TransformedRepReward);
            Assert.Equal(0f, contract.TransformedScienceReward);
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

            Assert.Equal(10000f, contract.TransformedFundsReward);
            Assert.Equal(50f, contract.TransformedRepReward);
            Assert.Equal(10f, contract.TransformedScienceReward);
        }

        // ================================================================
        // Transform tests — contract before activation UT not transformed
        // ================================================================

        [Fact]
        public void Transform_ContractBeforeActivationUT_NotTransformed()
        {
            // #439 Phase A: identity no-op applies regardless of UT relationship
            // between strategy and contract.
            module.ProcessAction(MakeActivate("LateStrat", 500.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.10f));

            var contract = MakeContractComplete("c1", 300.0,
                repReward: 100f, scienceReward: 0f);
            module.ProcessAction(contract);

            Assert.Equal(100f, contract.TransformedRepReward);
            Assert.Equal(0f, contract.TransformedScienceReward);
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
        public void Transform_MultipleStrategies_IdentityNoOp()
        {
            // #439 Phase A: multiple active strategies still yield identity transformed
            // fields. activeStrategies is populated for slot accounting, but the reward
            // transform itself stays at zero applications.
            module.SetMaxSlots(3);

            module.ProcessAction(MakeActivate("RepToSci", 100.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.10f));
            module.ProcessAction(MakeActivate("FundsToRep", 100.0,
                StrategyResource.Funds, StrategyResource.Reputation,
                commitment: 0.20f));

            var contract = MakeContractComplete("c1", 200.0,
                fundsReward: 10000f, repReward: 100f, scienceReward: 0f);
            module.ProcessAction(contract);

            Assert.Equal(10000f, contract.TransformedFundsReward);
            Assert.Equal(100f, contract.TransformedRepReward);
            Assert.Equal(0f, contract.TransformedScienceReward);
            Assert.Equal(2, module.GetActiveStrategyCount());
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
        public void Transform_LogsIdentityNoOp()
        {
            // #439 Phase A: the diversion log was replaced by a single VERBOSE line
            // per call announcing the identity no-op. Pin it here so future readers
            // looking for the old diversion text in the log find the migration note.
            module.ProcessAction(MakeActivate("RepToSci", 100.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.10f));

            var contract = MakeContractComplete("c1", 200.0,
                repReward: 50f, scienceReward: 0f);
            module.ProcessAction(contract);

            Assert.Contains(logLines, l =>
                l.Contains("[Strategies]") &&
                l.Contains("identity no-op") &&
                l.Contains("c1"));
        }

        [Fact]
        public void Activate_Duplicate_LogsWarning()
        {
            module.ProcessAction(MakeActivate("S1", 100.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.10f));

            logLines.Clear();

            module.ProcessAction(MakeActivate("S1", 200.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.20f));

            Assert.Contains(logLines, l =>
                l.Contains("[Strategies]") &&
                l.Contains("already active") &&
                l.Contains("S1"));
        }

        [Fact]
        public void Transform_NonEffective_LogsIdentityNoOp()
        {
            // #439 Phase A: the not-effective skip line was replaced with the single
            // identity-no-op line. Transformed fields still match raw values.
            module.ProcessAction(MakeActivate("RepToSci", 100.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.10f));

            logLines.Clear();

            var contract = MakeContractComplete("c1", 200.0,
                repReward: 50f, scienceReward: 0f, effective: false);
            module.ProcessAction(contract);

            Assert.Contains(logLines, l =>
                l.Contains("[Strategies]") &&
                l.Contains("identity no-op") &&
                l.Contains("c1"));
        }

        [Fact]
        public void Transform_NoActiveStrategies_LogsIdentityNoOp()
        {
            // #439 Phase A: even without active strategies, the module logs the
            // identity-no-op line so the call is observable.
            var contract = MakeContractComplete("c1", 200.0,
                fundsReward: 10000f, repReward: 50f, scienceReward: 0f);
            module.ProcessAction(contract);

            Assert.Contains(logLines, l =>
                l.Contains("[Strategies]") &&
                l.Contains("identity no-op") &&
                l.Contains("c1") &&
                l.Contains("activeStrategies=0"));
        }

        [Fact]
        public void SetMaxSlots_LogsNewValue()
        {
            logLines.Clear();

            module.SetMaxSlots(3);

            Assert.Contains(logLines, l =>
                l.Contains("[Strategies]") &&
                l.Contains("SetMaxSlots") &&
                l.Contains("maxSlots=3"));
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
        public void Integration_EngineDispatchesStrategyActions_IdentityRewards()
        {
            // #439 Phase A: activate/deactivate flow still dispatches through the engine,
            // and the module still tracks active strategies for slot accounting. Rewards
            // on ContractComplete stay identity in both windows because KSP pre-applies
            // the transform through the modifier query.
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

            Assert.Equal(50f, actions[1].TransformedRepReward);
            Assert.Equal(0f, actions[1].TransformedScienceReward);
            Assert.Equal(40f, actions[3].TransformedRepReward);
            Assert.Equal(0f, actions[3].TransformedScienceReward);
        }

        // ================================================================
        // Edge cases
        // ================================================================

        [Fact]
        public void Activate_SameStrategyTwice_OverwritesPrevious()
        {
            // #439 Phase A: overwriting semantics still hold for the activeStrategies
            // dict (used for slot accounting and display). Transformed reward fields
            // stay identity regardless of commitment.
            module.ProcessAction(MakeActivate("S1", 100.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.10f));

            module.ProcessAction(MakeActivate("S1", 200.0,
                StrategyResource.Reputation, StrategyResource.Science,
                commitment: 0.20f));

            Assert.Equal(1, module.GetActiveStrategyCount());
            Assert.Contains(logLines, l =>
                l.Contains("[Strategies]") && l.Contains("already active") && l.Contains("S1"));

            var contract = MakeContractComplete("c1", 300.0, repReward: 100f, scienceReward: 0f);
            module.ProcessAction(contract);

            Assert.Equal(100f, contract.TransformedRepReward);
            Assert.Equal(0f, contract.TransformedScienceReward);
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
            Assert.Equal(10000f, contract.TransformedFundsReward);
            Assert.Equal(0f, contract.TransformedRepReward);
            Assert.Equal(0f, contract.TransformedScienceReward);
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
