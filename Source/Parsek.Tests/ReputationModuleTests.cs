using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ReputationModuleTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly ReputationModule module;

        public ReputationModuleTests()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            module = new ReputationModule();
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Helpers
        // ================================================================

        private static GameAction MakeRepEarning(
            float nominalRep, double ut, string recordingId = null)
        {
            return new GameAction
            {
                Type = GameActionType.ReputationEarning,
                UT = ut,
                RecordingId = recordingId,
                NominalRep = nominalRep,
                RepSource = ReputationSource.Other
            };
        }

        private static GameAction MakeRepPenalty(
            float nominalPenalty, double ut, string recordingId = null)
        {
            return new GameAction
            {
                Type = GameActionType.ReputationPenalty,
                UT = ut,
                RecordingId = recordingId,
                NominalPenalty = nominalPenalty,
                RepPenaltySource = ReputationPenaltySource.Other
            };
        }

        private static GameAction MakeMilestone(
            string milestoneId, double ut, string recordingId,
            float repAwarded, bool effective = true)
        {
            return new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = ut,
                RecordingId = recordingId,
                MilestoneId = milestoneId,
                MilestoneRepAwarded = repAwarded,
                Effective = effective
            };
        }

        private static GameAction MakeContractComplete(
            string contractId, double ut, string recordingId,
            float repReward, bool effective = true)
        {
            return new GameAction
            {
                Type = GameActionType.ContractComplete,
                UT = ut,
                RecordingId = recordingId,
                ContractId = contractId,
                RepReward = repReward,
                TransformedRepReward = repReward,
                Effective = effective
            };
        }

        private static GameAction MakeContractFail(
            string contractId, double ut, float repPenalty)
        {
            return new GameAction
            {
                Type = GameActionType.ContractFail,
                UT = ut,
                ContractId = contractId,
                RepPenalty = repPenalty
            };
        }

        private static GameAction MakeContractCancel(
            string contractId, double ut, float repPenalty)
        {
            return new GameAction
            {
                Type = GameActionType.ContractCancel,
                UT = ut,
                ContractId = contractId,
                RepPenalty = repPenalty
            };
        }

        // ================================================================
        // ApplyReputationCurve — core algorithm tests
        // ================================================================

        [Fact]
        public void ApplyReputationCurve_ZeroNominal_NoChange()
        {
            var result = ReputationModule.ApplyReputationCurve(0f, 500f);

            Assert.Equal(0f, result.actualDelta);
            Assert.Equal(500f, result.newRep);
        }

        [Fact]
        public void ApplyReputationCurve_PositiveNominal_GainCurve()
        {
            // At rep=0, addition curve multiplier ~1.0
            // Nominal +50 should yield effective close to 50
            var result = ReputationModule.ApplyReputationCurve(50f, 0f);

            Assert.True(result.actualDelta > 0f, "Gain should be positive");
            // At rep=0, multiplier is ~0.999, so expect result close to 50
            Assert.InRange(result.actualDelta, 45f, 55f);
            Assert.True(result.newRep > 0f, "Rep should increase");
        }

        [Fact]
        public void ApplyReputationCurve_NegativeNominal_LossCurve()
        {
            // At rep=0, subtraction curve multiplier ~1.0
            // Nominal -50 should yield effective close to -50
            var result = ReputationModule.ApplyReputationCurve(-50f, 0f);

            Assert.True(result.actualDelta < 0f, "Loss should be negative");
            // At rep=0, multiplier is ~1.0, so expect result close to -50
            Assert.InRange(result.actualDelta, -55f, -45f);
            Assert.True(result.newRep < 0f, "Rep should decrease");
        }

        [Fact]
        public void GainCurve_AtHighRep_DiminishedReturns()
        {
            // At rep=900, addition curve multiplier should be close to 0
            var result = ReputationModule.ApplyReputationCurve(50f, 900f);

            // Gain should be heavily diminished at high rep
            Assert.True(result.actualDelta > 0f, "Still positive gain");
            Assert.True(result.actualDelta < 10f,
                $"Gain at rep=900 should be heavily diminished, was {result.actualDelta:F2}");
        }

        [Fact]
        public void GainCurve_AtNegativeRep_AmplifiedReturns()
        {
            // At rep=-900, addition curve multiplier should be close to 2.0
            var result = ReputationModule.ApplyReputationCurve(50f, -900f);

            // Gain should be amplified at very low rep
            Assert.True(result.actualDelta > 50f,
                $"Gain at rep=-900 should exceed nominal, was {result.actualDelta:F2}");
        }

        [Fact]
        public void LossCurve_AtHighRep_AmplifiedLoss()
        {
            // At rep=900, subtraction curve multiplier ~2.0
            // So a -50 penalty should produce actual loss larger than 50
            var result = ReputationModule.ApplyReputationCurve(-50f, 900f);

            Assert.True(result.actualDelta < 0f, "Still negative loss");
            Assert.True(result.actualDelta < -50f,
                $"Loss at rep=900 should exceed nominal, was {result.actualDelta:F2}");
        }

        [Fact]
        public void LossCurve_AtNegativeRep_DiminishedLoss()
        {
            // At rep=-900, subtraction curve multiplier should be close to 0
            var result = ReputationModule.ApplyReputationCurve(-50f, -900f);

            Assert.True(result.actualDelta < 0f, "Still negative");
            Assert.True(result.actualDelta > -10f,
                $"Loss at rep=-900 should be diminished, was {result.actualDelta:F2}");
        }

        [Fact]
        public void ApplyReputationCurve_FractionalNominal()
        {
            // Nominal 0.5 — no integer steps, just one fractional step
            var result = ReputationModule.ApplyReputationCurve(0.5f, 0f);

            Assert.True(result.actualDelta > 0f);
            Assert.InRange(result.actualDelta, 0.3f, 0.7f);
        }

        [Fact]
        public void ApplyReputationCurve_LargeNominal_ManySteps()
        {
            // 200 nominal at rep=0 — should produce moderate gain (curve diminishes as rep grows)
            var result = ReputationModule.ApplyReputationCurve(200f, 0f);

            Assert.True(result.actualDelta > 100f, "Large gain should still be significant");
            Assert.True(result.actualDelta < 200f,
                $"200 nominal should be less than 200 effective due to diminishing curve, was {result.actualDelta:F2}");
        }

        // ================================================================
        // Curve evaluator sanity checks
        // ================================================================

        [Fact]
        public void AdditionCurve_AtZeroTime_ReturnsNearOne()
        {
            // time=0 corresponds to rep=0: multiplier should be ~1.0
            float mult = ReputationModule.EvaluateAdditionCurve(0f);
            Assert.InRange(mult, 0.95f, 1.05f);
        }

        [Fact]
        public void AdditionCurve_AtPositiveOne_ReturnsNearZero()
        {
            // time=1.0 corresponds to rep=1000: multiplier should be ~0.0
            float mult = ReputationModule.EvaluateAdditionCurve(1.0f);
            Assert.InRange(mult, -0.05f, 0.05f);
        }

        [Fact]
        public void AdditionCurve_AtNegativeOne_ReturnsNearTwo()
        {
            // time=-1.0 corresponds to rep=-1000: multiplier should be ~2.0
            float mult = ReputationModule.EvaluateAdditionCurve(-1.0f);
            Assert.InRange(mult, 1.9f, 2.1f);
        }

        [Fact]
        public void SubtractionCurve_AtZeroTime_ReturnsNearOne()
        {
            // time=0 corresponds to rep=0: multiplier should be ~1.0
            float mult = ReputationModule.EvaluateSubtractionCurve(0f);
            Assert.InRange(mult, 0.95f, 1.05f);
        }

        [Fact]
        public void SubtractionCurve_AtPositiveOne_ReturnsNearTwo()
        {
            // time=1.0 corresponds to rep=1000: multiplier should be ~2.0
            float mult = ReputationModule.EvaluateSubtractionCurve(1.0f);
            Assert.InRange(mult, 1.9f, 2.1f);
        }

        [Fact]
        public void SubtractionCurve_AtNegativeOne_ReturnsNearZero()
        {
            // time=-1.0 corresponds to rep=-1000: multiplier should be ~0.0
            float mult = ReputationModule.EvaluateSubtractionCurve(-1.0f);
            Assert.InRange(mult, -0.1f, 0.1f);
        }

        // ================================================================
        // CubicHermite direct tests
        // ================================================================

        [Fact]
        public void CubicHermite_AtEndpoints()
        {
            // At t=0, should return p0
            Assert.Equal(5f, ReputationModule.CubicHermite(0f, 5f, 1f, 10f, 1f));
            // At t=1, should return p1
            Assert.Equal(10f, ReputationModule.CubicHermite(1f, 5f, 1f, 10f, 1f));
        }

        // ================================================================
        // ProcessAction — ReputationEarning
        // ================================================================

        [Fact]
        public void ProcessAction_RepEarning_SetsEffectiveRep()
        {
            var action = MakeRepEarning(50f, 1000.0, "recA");

            module.ProcessAction(action);

            Assert.True(action.EffectiveRep > 0f, "EffectiveRep should be positive");
            Assert.InRange(action.EffectiveRep, 45f, 55f);
            Assert.InRange(module.GetRunningRep(), 45f, 55f);
        }

        // ================================================================
        // ProcessAction — ReputationPenalty
        // ================================================================

        [Fact]
        public void ProcessAction_RepPenalty_SetsNegativeEffectiveRep()
        {
            var action = MakeRepPenalty(30f, 1000.0, "recA");

            module.ProcessAction(action);

            Assert.True(action.EffectiveRep < 0f, "EffectiveRep should be negative");
            Assert.InRange(action.EffectiveRep, -35f, -25f);
            Assert.InRange(module.GetRunningRep(), -35f, -25f);
        }

        // ================================================================
        // ProcessAction — Milestone rep (Effective flag)
        // ================================================================

        [Fact]
        public void ProcessAction_Milestone_EffectiveTrue_AppliesGainCurve()
        {
            var action = MakeMilestone("FirstOrbit", 1000.0, "recA",
                repAwarded: 15f, effective: true);

            module.ProcessAction(action);

            Assert.True(action.EffectiveRep > 0f);
            Assert.InRange(action.EffectiveRep, 13f, 17f);
            Assert.True(module.GetRunningRep() > 0f);
        }

        [Fact]
        public void ProcessAction_Milestone_EffectiveFalse_ZeroRep()
        {
            var action = MakeMilestone("FirstOrbit", 1000.0, "recA",
                repAwarded: 15f, effective: false);

            module.ProcessAction(action);

            Assert.Equal(0f, action.EffectiveRep);
            Assert.Equal(0f, module.GetRunningRep());
        }

        [Fact]
        public void ProcessAction_Milestone_ZeroRepAwarded_NoChange()
        {
            var action = MakeMilestone("FirstOrbit", 1000.0, "recA",
                repAwarded: 0f, effective: true);

            module.ProcessAction(action);

            Assert.Equal(0f, module.GetRunningRep());
        }

        // ================================================================
        // ProcessAction — Contract complete rep (Effective flag)
        // ================================================================

        [Fact]
        public void ProcessAction_ContractComplete_EffectiveTrue_AppliesGainCurve()
        {
            var action = MakeContractComplete("contract1", 1000.0, "recA",
                repReward: 50f, effective: true);

            module.ProcessAction(action);

            Assert.True(action.EffectiveRep > 0f);
            Assert.InRange(action.EffectiveRep, 45f, 55f);
        }

        [Fact]
        public void ProcessAction_ContractComplete_EffectiveFalse_ZeroRep()
        {
            var action = MakeContractComplete("contract1", 1000.0, "recA",
                repReward: 50f, effective: false);

            module.ProcessAction(action);

            Assert.Equal(0f, action.EffectiveRep);
            Assert.Equal(0f, module.GetRunningRep());
        }

        // ================================================================
        // ProcessAction — Contract fail/cancel rep penalties
        // ================================================================

        [Fact]
        public void ProcessAction_ContractFail_AppliesLossCurve()
        {
            var action = MakeContractFail("contract1", 2000.0, 20f);

            module.ProcessAction(action);

            Assert.True(action.EffectiveRep < 0f, "Contract fail should reduce rep");
            Assert.InRange(action.EffectiveRep, -25f, -15f);
        }

        [Fact]
        public void ProcessAction_ContractCancel_AppliesLossCurve()
        {
            var action = MakeContractCancel("contract1", 2000.0, 10f);

            module.ProcessAction(action);

            Assert.True(action.EffectiveRep < 0f, "Contract cancel should reduce rep");
            Assert.InRange(action.EffectiveRep, -15f, -5f);
        }

        [Fact]
        public void ProcessAction_ContractFail_ZeroPenalty_NoChange()
        {
            var action = MakeContractFail("contract1", 2000.0, 0f);

            module.ProcessAction(action);

            Assert.Equal(0f, module.GetRunningRep());
        }

        // ================================================================
        // ProcessAction — ignores non-rep action types
        // ================================================================

        [Theory]
        [InlineData(GameActionType.ScienceEarning)]
        [InlineData(GameActionType.ScienceSpending)]
        [InlineData(GameActionType.FundsEarning)]
        [InlineData(GameActionType.FundsSpending)]
        [InlineData(GameActionType.ContractAccept)]
        [InlineData(GameActionType.KerbalAssignment)]
        [InlineData(GameActionType.KerbalHire)]
        [InlineData(GameActionType.FacilityUpgrade)]
        [InlineData(GameActionType.StrategyActivate)]
        [InlineData(GameActionType.FundsInitial)]
        public void ProcessAction_IgnoresNonRepActions(GameActionType type)
        {
            var action = new GameAction { Type = type, UT = 1000.0 };

            module.ProcessAction(action);

            Assert.Equal(0f, module.GetRunningRep());
        }

        // ================================================================
        // Reset clears running rep
        // ================================================================

        [Fact]
        public void Reset_ClearsRunningRep()
        {
            // Build up some rep
            module.ProcessAction(MakeRepEarning(50f, 1000.0));
            Assert.True(module.GetRunningRep() > 0f);

            module.Reset();

            Assert.Equal(0f, module.GetRunningRep());
        }

        // ================================================================
        // Design doc 6.7 — Retroactive reordering scenario
        // ================================================================

        [Fact]
        public void DesignDoc_6_7_RetroactiveReorderingChangesEffectiveRepPerEvent()
        {
            // Design doc 6.7 scenario with simplified gain curve assumption:
            // effectiveGain = nominal * (1 - currentRep / 1000)
            //
            // Since we use the real curve (not simplified), exact values differ,
            // but the key invariants hold:
            //   - Reordering changes per-event effective values
            //   - Total rep may shift slightly due to nonlinear curve
            //   - First-chronological milestone gets credit

            // Simulate Step 1 + 2 from design doc:
            // Recording A at UT=1000: contract +50 rep, milestone "First Mun Landing" +15 rep
            var milestoneA = MakeMilestone("FirstMunLanding", 1000.0, "recA",
                repAwarded: 15f, effective: true);
            var contractA = MakeContractComplete("contractA", 1000.0, "recA",
                repReward: 50f, effective: true);

            // Walk: contract first (earnings before milestones at same UT? Use UT ordering)
            module.Reset();
            module.ProcessAction(contractA);
            module.ProcessAction(milestoneA);

            float repAfterStep1 = module.GetRunningRep();
            float contractAEffective = contractA.EffectiveRep;
            float milestoneAEffective = milestoneA.EffectiveRep;

            Assert.True(contractAEffective > 0f);
            Assert.True(milestoneAEffective > 0f);

            // Step 3: Rewind. Recording B at UT=700 achieves "First Mun Landing."
            // Recalculate in UT order: B milestone at UT=700, then A contract + A milestone at UT=1000
            var milestoneB = MakeMilestone("FirstMunLanding", 700.0, "recB",
                repAwarded: 15f, effective: true);
            var milestoneA2 = MakeMilestone("FirstMunLanding", 1000.0, "recA",
                repAwarded: 15f, effective: false); // duplicate, zeroed by first-tier
            var contractA2 = MakeContractComplete("contractA", 1000.0, "recA",
                repReward: 50f, effective: true);

            module.Reset();
            module.ProcessAction(milestoneB);     // UT=700
            module.ProcessAction(contractA2);     // UT=1000
            module.ProcessAction(milestoneA2);    // UT=1000, not effective

            float repAfterStep3 = module.GetRunningRep();

            // Key assertions from design doc:
            // 1. B's milestone effective rep is different from A's was (different running rep at evaluation)
            Assert.True(milestoneB.EffectiveRep > 0f,
                "B's milestone should have positive effective rep");
            // 2. A's duplicate milestone contributes zero
            Assert.Equal(0f, milestoneA2.EffectiveRep);
            // 3. Contract still contributes, but at different running rep
            Assert.True(contractA2.EffectiveRep > 0f,
                "Contract should still contribute");
            // 4. Totals are close but may differ slightly due to nonlinear curve reordering
            // The design doc notes this is expected and harmless
            Assert.InRange(repAfterStep3, repAfterStep1 - 5f, repAfterStep1 + 5f);
        }

        // ================================================================
        // Sequence of multiple events — running rep accumulates
        // ================================================================

        [Fact]
        public void MultipleEvents_RunningRepAccumulates()
        {
            var earn1 = MakeRepEarning(30f, 1000.0);
            var earn2 = MakeRepEarning(20f, 2000.0);
            var penalty = MakeRepPenalty(10f, 3000.0);

            module.ProcessAction(earn1);
            module.ProcessAction(earn2);
            module.ProcessAction(penalty);

            // Running rep should be earn1 + earn2 - penalty
            float finalRep = module.GetRunningRep();
            Assert.True(finalRep > 0f, "Net should be positive after gains exceed penalty");
            // Verify accumulated from curve
            float expectedApprox = earn1.EffectiveRep + earn2.EffectiveRep + penalty.EffectiveRep;
            Assert.InRange(finalRep, expectedApprox - 0.1f, expectedApprox + 0.1f);
        }

        // ================================================================
        // Integration with RecalculationEngine
        // ================================================================

        [Fact]
        public void Integration_EngineDispatchesRepActionsToModule()
        {
            RecalculationEngine.RegisterModule(module, RecalculationEngine.ModuleTier.SecondTier);

            var actions = new List<GameAction>
            {
                MakeRepEarning(50f, 2000.0, "recA"),
                MakeRepEarning(25f, 1000.0, "recB") // earlier UT, sorted first
            };

            RecalculationEngine.Recalculate(actions);

            // recB (UT=1000) processed first at rep=0
            // recA (UT=2000) processed second at higher rep — diminished gain
            Assert.True(actions[1].EffectiveRep > 0f); // recB
            Assert.True(actions[0].EffectiveRep > 0f); // recA
            // recB at rep=0 should have higher multiplier than recA at rep>0
            Assert.True(module.GetRunningRep() > 0f);
        }

        // ================================================================
        // Log assertion tests
        // ================================================================

        [Fact]
        public void RepEarning_LogsEffective()
        {
            module.ProcessAction(MakeRepEarning(50f, 1000.0, "recA"));

            Assert.Contains(logLines, l =>
                l.Contains("[Reputation]") &&
                l.Contains("RepEarning") &&
                l.Contains("nominal=50") &&
                l.Contains("effective="));
        }

        [Fact]
        public void RepPenalty_LogsEffective()
        {
            module.ProcessAction(MakeRepPenalty(30f, 1000.0, "recA"));

            Assert.Contains(logLines, l =>
                l.Contains("[Reputation]") &&
                l.Contains("RepPenalty") &&
                l.Contains("nominalPenalty=30") &&
                l.Contains("effective="));
        }

        [Fact]
        public void MilestoneRep_Effective_LogsDetails()
        {
            module.ProcessAction(MakeMilestone("FirstOrbit", 1000.0, "recA",
                repAwarded: 15f, effective: true));

            Assert.Contains(logLines, l =>
                l.Contains("[Reputation]") &&
                l.Contains("Milestone rep") &&
                l.Contains("FirstOrbit") &&
                l.Contains("nominal=15"));
        }

        [Fact]
        public void MilestoneRep_NotEffective_LogsSkipped()
        {
            module.ProcessAction(MakeMilestone("FirstOrbit", 1000.0, "recA",
                repAwarded: 15f, effective: false));

            Assert.Contains(logLines, l =>
                l.Contains("[Reputation]") &&
                l.Contains("Milestone rep skipped") &&
                l.Contains("not effective"));
        }

        [Fact]
        public void ContractComplete_NotEffective_LogsSkipped()
        {
            module.ProcessAction(MakeContractComplete("c1", 1000.0, "recA",
                repReward: 50f, effective: false));

            Assert.Contains(logLines, l =>
                l.Contains("[Reputation]") &&
                l.Contains("Contract complete rep skipped") &&
                l.Contains("not effective"));
        }

        [Fact]
        public void Reset_LogsPreviousValue()
        {
            module.ProcessAction(MakeRepEarning(50f, 1000.0));
            logLines.Clear();

            module.Reset();

            Assert.Contains(logLines, l =>
                l.Contains("[Reputation]") &&
                l.Contains("Reset") &&
                l.Contains("-> 0"));
        }

        // ================================================================
        // ReputationInitial seeding tests (D19)
        // ================================================================

        [Fact]
        public void ReputationInitial_SeedsRunningRep()
        {
            var module = new ReputationModule();
            module.Reset();
            module.ProcessAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ReputationInitial,
                InitialReputation = 150f
            });

            Assert.Equal(150f, module.GetRunningRep(), 0.01f);
        }

        [Fact]
        public void ReputationInitial_PlusEarnings_Accumulates()
        {
            var module = new ReputationModule();
            module.Reset();
            module.ProcessAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ReputationInitial,
                InitialReputation = 100f
            });
            module.ProcessAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.ReputationEarning,
                NominalRep = 10f,
                RepSource = ReputationSource.Other
            });

            // With seed of 100, gain curve at 100/1000=0.1 should give multiplier ~1.0
            // Exact value depends on curve, but rep should be > 100
            Assert.True(module.GetRunningRep() > 100f);
        }

        // ================================================================
        // HasSeed tests
        // ================================================================

        [Fact]
        public void HasSeed_FalseBeforeAnyAction()
        {
            Assert.False(module.HasSeed);
        }

        [Fact]
        public void HasSeed_TrueAfterReputationInitial()
        {
            module.ProcessAction(new GameAction
            {
                UT = 0,
                Type = GameActionType.ReputationInitial,
                InitialReputation = 50f
            });
            Assert.True(module.HasSeed);
        }

        [Fact]
        public void HasSeed_FalseAfterReset()
        {
            module.ProcessAction(new GameAction
            {
                UT = 0,
                Type = GameActionType.ReputationInitial,
                InitialReputation = 50f
            });
            Assert.True(module.HasSeed);
            module.Reset();
            Assert.False(module.HasSeed);
        }

        [Fact]
        public void HasSeed_FalseWithOnlyEarnings()
        {
            module.ProcessAction(new GameAction
            {
                UT = 10,
                Type = GameActionType.ReputationEarning,
                NominalRep = 10f,
                RepSource = ReputationSource.Other
            });
            Assert.False(module.HasSeed);
        }

        // ================================================================
        // Bug #593 (P2 follow-up) — milestone-rep rate limiting
        // ================================================================

        [Fact]
        public void MilestoneRep_SameActionRecalculated_RateLimitedToOneLine()
        {
            // Bug #593 (P2 follow-up): the rate-limit key for the
            // "Milestone rep" line is the stable GameAction.ActionId so a
            // recalc loop walking the SAME action collapses to one line per
            // window.
            var action = MakeMilestone("RecordsSpeed", 50.0, "rec-A",
                repAwarded: 5f);
            for (int i = 0; i < 100; i++)
            {
                module.ProcessAction(action);
            }

            int lines = logLines.Count(l =>
                l.Contains("[Reputation]") &&
                l.Contains("Milestone rep at UT=") &&
                l.Contains("RecordsSpeed") &&
                l.Contains("rec-A"));
            Assert.Equal(1, lines);
        }

        [Fact]
        public void MilestoneRep_DistinctActionsSamePair_LogSeparately()
        {
            // Bug #593 (P2 follow-up): distinct grants sharing
            // (milestoneId, recordingId) but with different UT or reward
            // are separate effective hits and must each log on first walk.
            var grantA = MakeMilestone("RecordsSpeed", 50.0, "rec-A",
                repAwarded: 5f);
            var grantB = MakeMilestone("RecordsSpeed", 80.0, "rec-A",
                repAwarded: 5f);
            var grantC = MakeMilestone("RecordsSpeed", 80.0, "rec-A",
                repAwarded: 8f);
            Assert.NotEqual(grantA.ActionId, grantB.ActionId);
            Assert.NotEqual(grantB.ActionId, grantC.ActionId);
            Assert.NotEqual(grantA.ActionId, grantC.ActionId);

            module.ProcessAction(grantA);
            module.ProcessAction(grantB);
            module.ProcessAction(grantC);

            int lines = logLines.Count(l =>
                l.Contains("[Reputation]") &&
                l.Contains("Milestone rep at UT=") &&
                l.Contains("RecordsSpeed") &&
                l.Contains("rec-A"));
            Assert.Equal(3, lines);
        }

        [Fact]
        public void MilestoneRep_NullRecordingId_StillKeysOnActionId()
        {
            // Standalone/KSC-path repeatable record milestones can have a
            // null RecordingId. The earlier (milestoneId, recordingId)-keyed
            // gate would collapse two distinct null-recording grants into
            // one line; the ActionId-keyed gate must keep them separate.
            var grant1 = MakeMilestone("RecordsAltitude", 50.0, null,
                repAwarded: 4f);
            var grant2 = MakeMilestone("RecordsAltitude", 75.0, null,
                repAwarded: 6f);
            Assert.NotEqual(grant1.ActionId, grant2.ActionId);

            module.ProcessAction(grant1);
            module.ProcessAction(grant2);

            int lines = logLines.Count(l =>
                l.Contains("[Reputation]") &&
                l.Contains("Milestone rep at UT=") &&
                l.Contains("RecordsAltitude"));
            Assert.Equal(2, lines);
        }
    }
}
