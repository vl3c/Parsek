using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase D tests: explicit UT-cutoff parameter on <see cref="LedgerOrchestrator.RecalculateAndPatch"/>
    /// and <see cref="RecalculationEngine.Recalculate"/>.
    ///
    /// The rewind path calls RecalculateAndPatch twice (synchronous + deferred coroutine). The
    /// cutoff must be passed as an explicit argument both times because the coroutine resumes
    /// AFTER <see cref="RewindContext.EndRewind"/> has cleared the global. These tests pin that
    /// RecalculateAndPatch consults no rewind globals — only its argument.
    /// </summary>
    [Collection("Sequential")]
    public class RewindUtCutoffTests : IDisposable
    {
        private const string Tag = "[LedgerOrchestrator]";
        private const string EngineTag = "[RecalcEngine]";
        private readonly List<string> logLines = new List<string>();

        public RewindUtCutoffTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RewindContext.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Helpers — build actions
        // ================================================================

        private static GameAction FundsSeed(float amount)
        {
            return new GameAction
            {
                UT = 0.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = amount
            };
        }

        private static GameAction ScienceSeed(float amount)
        {
            return new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ScienceInitial,
                InitialScience = amount
            };
        }

        private static GameAction RepSeed(float amount)
        {
            return new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ReputationInitial,
                InitialReputation = amount
            };
        }

        private static GameAction Milestone(double ut, string id, float funds, float rep = 0f, float sci = 0f)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = id,
                MilestoneFundsAwarded = funds,
                MilestoneRepAwarded = rep,
                MilestoneScienceAwarded = sci,
                RecordingId = "rec-" + id
            };
        }

        private static GameAction ContractComplete(double ut, string contractId,
            float funds, float rep = 0f, float sci = 0f)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ContractComplete,
                ContractId = contractId,
                FundsReward = funds,
                RepReward = rep,
                ScienceReward = sci,
                RecordingId = "rec-" + contractId
            };
        }

        private static GameAction ContractAccept(double ut, string contractId, float advance)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ContractAccept,
                ContractId = contractId,
                AdvanceFunds = advance,
                DeadlineUT = float.NaN,
                RecordingId = "rec-" + contractId
            };
        }

        private static GameAction ContractAcceptWithDeadline(double ut, string contractId,
            float advance, float deadlineUt, float fundsPenalty, float repPenalty = 0f)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ContractAccept,
                ContractId = contractId,
                AdvanceFunds = advance,
                DeadlineUT = deadlineUt,
                FundsPenalty = fundsPenalty,
                RepPenalty = repPenalty,
                RecordingId = "rec-" + contractId
            };
        }

        private static GameAction ContractFail(double ut, string contractId, float penalty)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ContractFail,
                ContractId = contractId,
                FundsPenalty = penalty,
                RecordingId = "rec-" + contractId
            };
        }

        private static GameAction ContractCancel(double ut, string contractId, float penalty)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ContractCancel,
                ContractId = contractId,
                FundsPenalty = penalty,
                RecordingId = "rec-" + contractId
            };
        }

        private static GameAction FundsEarning(double ut, float amount, string recordingId = "rec-earn")
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsEarning,
                FundsAwarded = amount,
                FundsSource = FundsEarningSource.Other,
                RecordingId = recordingId
            };
        }

        private static GameAction FundsSpending(double ut, float amount, string recordingId = "rec-spend")
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsSpending,
                FundsSpent = amount,
                FundsSpendingSource = FundsSpendingSource.Other,
                RecordingId = recordingId
            };
        }

        private static GameAction ScienceEarning(double ut, float amount, string subjectId)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ScienceEarning,
                SubjectId = subjectId,
                ScienceAwarded = amount,
                SubjectMaxValue = amount * 3f,
                RecordingId = "rec-" + subjectId
            };
        }

        private static GameAction ReputationEarning(double ut, float amount, string recordingId = "rec-rep")
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ReputationEarning,
                NominalRep = amount,
                RepSource = ReputationSource.Other,
                RecordingId = recordingId
            };
        }

        private static GameAction FacilityUpgrade(double ut, string facilityId, float cost, int toLevel = 2)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.FacilityUpgrade,
                FacilityId = facilityId,
                ToLevel = toLevel,
                FacilityCost = cost
            };
        }

        private static GameAction KerbalHire(double ut, string name, float cost)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.KerbalHire,
                KerbalName = name,
                KerbalRole = "Pilot",
                HireCost = cost
            };
        }

        private static GameAction KerbalAssignment(double ut, string name, double startUt, double endUt)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.KerbalAssignment,
                KerbalName = name,
                KerbalRole = "Pilot",
                StartUT = (float)startUt,
                EndUT = (float)endUt,
                RecordingId = "rec-asn-" + name
            };
        }

        private static GameAction StrategyActivate(double ut, string id, float setupCost,
            StrategyResource src = StrategyResource.Funds,
            StrategyResource tgt = StrategyResource.Science,
            float commitment = 0.1f)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.StrategyActivate,
                StrategyId = id,
                SourceResource = src,
                TargetResource = tgt,
                Commitment = commitment,
                SetupCost = setupCost
            };
        }

        private static void AddAll(params GameAction[] actions)
        {
            for (int i = 0; i < actions.Length; i++)
                Ledger.AddAction(actions[i]);
        }

        private static bool ShouldUseCurrentUtCutoffForPostRewindFlightLoad(
            bool isRevert = false,
            bool loadedSceneIsFlight = true,
            bool planetariumReady = true,
            bool hasPendingTree = false,
            ParsekScenario.ActiveTreeRestoreMode restoreMode = ParsekScenario.ActiveTreeRestoreMode.None,
            bool hasLiveRecorder = false,
            bool hasActiveUncommittedTree = false,
            bool hasFutureLedgerActions = true)
        {
            return ParsekScenario.ShouldUseCurrentUtCutoffForPostRewindFlightLoad(
                isRevert,
                loadedSceneIsFlight,
                planetariumReady,
                hasPendingTree,
                restoreMode,
                hasLiveRecorder,
                hasActiveUncommittedTree,
                hasFutureLedgerActions);
        }

        private static void AssertLogHasCutoffSummary(List<string> lines, int actionsTotal,
            int actionsAfterCutoff, string cutoffLabel)
        {
            Assert.Contains(lines, l =>
                l.Contains(Tag)
                && l.Contains("RecalculateAndPatch: ")
                && l.Contains("actionsTotal=" + actionsTotal)
                && l.Contains("actionsAfterCutoff=" + actionsAfterCutoff)
                && l.Contains("cutoffUT=" + cutoffLabel));
        }

        // ================================================================
        // Happy-path per action type
        // ================================================================

        [Fact]
        public void Milestone_CutoffFiltersLaterMilestone()
        {
            AddAll(
                FundsSeed(10000f),
                Milestone(200.0, "FirstLaunch", 500f),
                Milestone(500.0, "FirstOrbit", 700f));

            LedgerOrchestrator.RecalculateAndPatch(300.0);

            Assert.Equal(10500.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
            AssertLogHasCutoffSummary(logLines, 3, 2, "300");
        }

        [Fact]
        public void ContractComplete_CutoffFiltersLaterCompletion()
        {
            AddAll(
                FundsSeed(10000f),
                ContractComplete(200.0, "c1", 400f),
                ContractComplete(500.0, "c2", 900f));

            LedgerOrchestrator.RecalculateAndPatch(300.0);

            Assert.Equal(10400.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
        }

        [Fact]
        public void FundsEarning_CutoffFiltersLater()
        {
            AddAll(
                FundsSeed(5000f),
                FundsEarning(100.0, 300f),
                FundsEarning(600.0, 1000f));

            LedgerOrchestrator.RecalculateAndPatch(300.0);

            Assert.Equal(5300.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
        }

        [Fact]
        public void FundsSpending_CutoffFiltersLaterSpending()
        {
            AddAll(
                FundsSeed(10000f),
                FundsSpending(100.0, 200f),
                FundsSpending(500.0, 3000f));

            LedgerOrchestrator.RecalculateAndPatch(300.0);

            // Only the UT=100 spending (200) should have been deducted.
            Assert.Equal(9800.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
            // And the pre-pass "totalCommittedSpendings" must exclude the UT=500 spending.
            Assert.Equal(200.0, LedgerOrchestrator.Funds.GetTotalCommittedSpendings(), 1);
        }

        [Fact]
        public void ScienceEarning_CutoffFiltersLater()
        {
            AddAll(
                ScienceSeed(0f),
                ScienceEarning(200.0, 10f, "crewReport@KerbinSrfLanded"),
                ScienceEarning(500.0, 50f, "crewReport@MunSrfLanded"));

            LedgerOrchestrator.RecalculateAndPatch(300.0);

            Assert.Equal(10.0, LedgerOrchestrator.Science.GetAvailableScience(), 3);
        }

        [Fact]
        public void ReputationEarning_CutoffFiltersLater()
        {
            AddAll(
                RepSeed(5f),
                ReputationEarning(200.0, 3f),
                ReputationEarning(500.0, 20f));

            LedgerOrchestrator.RecalculateAndPatch(300.0);

            // ReputationModule applies a curve; compare relative to a full walk.
            double clamped = LedgerOrchestrator.Reputation.GetRunningRep();

            // Re-run with a past-all cutoff to get the full-walk baseline.
            LedgerOrchestrator.RecalculateAndPatch(10000.0);
            double full = LedgerOrchestrator.Reputation.GetRunningRep();

            Assert.True(clamped < full,
                $"cutoff-300 rep ({clamped}) must be smaller than full-walk rep ({full})");
        }

        [Fact]
        public void ContractAccept_AdvanceCutoff()
        {
            AddAll(
                FundsSeed(1000f),
                ContractAccept(200.0, "c1", 50f),
                ContractAccept(500.0, "c2", 400f));

            LedgerOrchestrator.RecalculateAndPatch(300.0);

            Assert.Equal(1050.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
        }

        [Fact]
        public void ContractFail_PenaltyCutoff()
        {
            AddAll(
                FundsSeed(10000f),
                ContractFail(200.0, "c1", 100f),
                ContractFail(500.0, "c2", 5000f));

            LedgerOrchestrator.RecalculateAndPatch(300.0);

            // Only the UT=200 penalty (100) hits the running balance.
            Assert.Equal(9900.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
        }

        [Fact]
        public void ContractCancel_PenaltyCutoff()
        {
            AddAll(
                FundsSeed(10000f),
                ContractCancel(200.0, "c1", 50f),
                ContractCancel(500.0, "c2", 2500f));

            LedgerOrchestrator.RecalculateAndPatch(300.0);

            Assert.Equal(9950.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
        }

        [Fact]
        public void FacilityUpgrade_CutoffFiltersLater()
        {
            AddAll(
                FundsSeed(100000f),
                FacilityUpgrade(200.0, "LaunchPad", 1000f),
                FacilityUpgrade(500.0, "R&D", 50000f));

            LedgerOrchestrator.RecalculateAndPatch(300.0);

            Assert.Equal(99000.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
            Assert.Equal(1000.0, LedgerOrchestrator.Funds.GetTotalCommittedSpendings(), 1);
        }

        [Fact]
        public void KerbalHire_CutoffFiltersLater()
        {
            AddAll(
                FundsSeed(10000f),
                KerbalHire(200.0, "Bob Kerman", 100f),
                KerbalHire(500.0, "Jeb Kerman", 5000f));

            LedgerOrchestrator.RecalculateAndPatch(300.0);

            Assert.Equal(9900.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
        }

        [Fact]
        public void StrategyActivate_CutoffFiltersLater()
        {
            AddAll(
                FundsSeed(10000f),
                StrategyActivate(200.0, "EarlyBird", 200f),
                StrategyActivate(500.0, "AppreciationCampaign", 3000f));

            LedgerOrchestrator.RecalculateAndPatch(300.0);

            Assert.Equal(9800.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
        }

        [Fact]
        public void KerbalAssignment_IsFilteredDespiteNoResourceImpact()
        {
            // KerbalAssignment has no direct funds/science/rep impact, but still participates
            // in module dispatch. Prove the filter excludes it by using a test module and
            // calling the engine directly.
            var capture = new CaptureModule();
            RecalculationEngine.ClearModules();
            RecalculationEngine.RegisterModule(capture, RecalculationEngine.ModuleTier.FirstTier);

            var actions = new List<GameAction>
            {
                FundsSeed(1000f),
                KerbalAssignment(200.0, "Bob", 100.0, 300.0),
                KerbalAssignment(500.0, "Bill", 400.0, 600.0)
            };

            RecalculationEngine.Recalculate(actions, 300.0);

            // Three actions are sent to each module per dispatch iteration. The seed at UT=0
            // always passes, the UT=200 assignment passes (<=300), the UT=500 assignment is
            // filtered out. ContractsModule PrePass is off because we only registered the
            // capture module — so dispatched count equals effective action count.
            int assignmentDispatches = 0;
            for (int i = 0; i < capture.ProcessedActions.Count; i++)
            {
                if (capture.ProcessedActions[i].Type == GameActionType.KerbalAssignment)
                    assignmentDispatches++;
            }
            Assert.Equal(1, assignmentDispatches);

            RecalculationEngine.ClearModules();
        }

        // ================================================================
        // Edge cases
        // ================================================================

        [Fact]
        public void NullCutoff_WalksEverything()
        {
            AddAll(
                FundsSeed(1000f),
                Milestone(100.0, "M1", 200f),
                Milestone(500.0, "M2", 400f),
                Milestone(10000.0, "M3", 800f));

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Equal(2400.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
            AssertLogHasCutoffSummary(logLines, 4, 4, "null");
        }

        [Fact]
        public void CutoffZero_OnlySeedsSurvive()
        {
            AddAll(
                FundsSeed(1000f),
                Milestone(1.0, "Earliest", 50f),
                Milestone(100.0, "Mid", 200f),
                Milestone(500.0, "Late", 400f));

            LedgerOrchestrator.RecalculateAndPatch(0.0);

            // Only the seed (UT=0) contributes; all milestones are UT > 0.
            Assert.Equal(1000.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
            AssertLogHasCutoffSummary(logLines, 4, 1, "0");
        }

        [Fact]
        public void CutoffZero_DistinctFromNull()
        {
            AddAll(
                FundsSeed(1000f),
                Milestone(100.0, "Only", 200f));

            // null — includes everything
            LedgerOrchestrator.RecalculateAndPatch();
            double nullBalance = LedgerOrchestrator.Funds.GetRunningBalance();

            // 0.0 — excludes the UT=100 milestone
            LedgerOrchestrator.RecalculateAndPatch(0.0);
            double zeroBalance = LedgerOrchestrator.Funds.GetRunningBalance();

            Assert.Equal(1200.0, nullBalance, 1);
            Assert.Equal(1000.0, zeroBalance, 1);
        }

        [Fact]
        public void CutoffNegative_OnlySeedsSurvive()
        {
            AddAll(
                FundsSeed(2500f),
                Milestone(0.0, "AtZero", 50f),
                Milestone(100.0, "Later", 100f));

            LedgerOrchestrator.RecalculateAndPatch(-1.0);

            // Seed is UT=0 — included (seeds always survive). Non-seed UT=0 milestone is
            // excluded because 0 > -1. UT=100 is excluded.
            Assert.Equal(2500.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
            AssertLogHasCutoffSummary(logLines, 3, 1, "-1");
        }

        [Fact]
        public void CutoffPastAll_MatchesNullBehavior()
        {
            AddAll(
                FundsSeed(1000f),
                Milestone(100.0, "A", 200f),
                Milestone(500.0, "B", 400f));

            LedgerOrchestrator.RecalculateAndPatch();
            double nullBalance = LedgerOrchestrator.Funds.GetRunningBalance();

            LedgerOrchestrator.RecalculateAndPatch(999999.0);
            double pastAllBalance = LedgerOrchestrator.Funds.GetRunningBalance();

            Assert.Equal(nullBalance, pastAllBalance, 1);
            // But the log line still shows the cutoff value — callers can observe whether
            // a cutoff was supplied even when the result matches the unfiltered walk.
            AssertLogHasCutoffSummary(logLines, 3, 3, "999999");
        }

        // ================================================================
        // Seeds always included
        // ================================================================

        [Fact]
        public void FundsSeed_SurvivesNegativeCutoff()
        {
            AddAll(FundsSeed(10000f));

            LedgerOrchestrator.RecalculateAndPatch(-100.0);

            Assert.Equal(10000.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
        }

        [Fact]
        public void ScienceSeed_SurvivesNegativeCutoff()
        {
            AddAll(ScienceSeed(42f));

            LedgerOrchestrator.RecalculateAndPatch(-100.0);

            Assert.Equal(42.0, LedgerOrchestrator.Science.GetAvailableScience(), 3);
        }

        [Fact]
        public void ReputationSeed_SurvivesNegativeCutoff()
        {
            AddAll(RepSeed(17f));

            LedgerOrchestrator.RecalculateAndPatch(-100.0);

            Assert.Equal(17.0, LedgerOrchestrator.Reputation.GetRunningRep(), 1);
        }

        // ================================================================
        // Two-pass regression (the bug plan v2 caught)
        // ================================================================

        [Fact]
        public void TwoPass_SameCutoff_IdenticalResult()
        {
            AddAll(
                FundsSeed(5000f),
                Milestone(500.0, "PostRewind", 1000f));

            // First call: cutoff=200 — milestone at UT=500 is filtered out.
            LedgerOrchestrator.RecalculateAndPatch(200.0);
            double first = LedgerOrchestrator.Funds.GetRunningBalance();
            AssertLogHasCutoffSummary(logLines, 2, 1, "200");

            // Clear only the log buffer — do NOT touch RewindContext or the ledger.
            logLines.Clear();

            // Second call with the same cutoff must produce the same result and same log.
            // This pins that RecalculateAndPatch consults no rewind globals.
            LedgerOrchestrator.RecalculateAndPatch(200.0);
            double second = LedgerOrchestrator.Funds.GetRunningBalance();
            AssertLogHasCutoffSummary(logLines, 2, 1, "200");

            Assert.Equal(first, second, 5);
            Assert.Equal(5000.0, second, 1);
        }

        [Fact]
        public void TwoPass_NoRewindContextState_SecondCallUnaffected()
        {
            AddAll(
                FundsSeed(5000f),
                Milestone(500.0, "PostRewind", 1000f));

            // Simulate the production ordering: the synchronous call happens while
            // RewindContext.RewindAdjustedUT is still set; EndRewind clears it; then the
            // deferred coroutine resumes and calls RecalculateAndPatch again with its
            // own captured local. The captured local is the only thing the second call
            // should depend on.
            RewindContext.BeginRewind(500.0, default(BudgetSummary), 0, 0, 0);
            RewindContext.SetAdjustedUT(200.0);
            LedgerOrchestrator.RecalculateAndPatch(RewindContext.RewindAdjustedUT);
            double first = LedgerOrchestrator.Funds.GetRunningBalance();

            RewindContext.EndRewind();
            Assert.Equal(0.0, RewindContext.RewindAdjustedUT);

            // The deferred coroutine's captured local is still 200.
            double capturedLocal = 200.0;
            LedgerOrchestrator.RecalculateAndPatch(capturedLocal);
            double second = LedgerOrchestrator.Funds.GetRunningBalance();

            Assert.Equal(first, second, 5);
            Assert.Equal(5000.0, second, 1);
        }

        [Fact]
        public void PostRewindFlightLoadFollowup_AfterRewind_UsesCurrentUtCutoff()
        {
            AddAll(
                FundsSeed(10000f),
                ContractAccept(500.0, "future-contract", 400f),
                Milestone(500.0, "future-funds", 1000f));

            RewindContext.BeginRewind(500.0, default(BudgetSummary), 0, 0, 0);
            RewindContext.SetAdjustedUT(200.0);

            LedgerOrchestrator.RecalculateAndPatch(RewindContext.RewindAdjustedUT);
            double firstBalance = LedgerOrchestrator.Funds.GetRunningBalance();
            int firstActiveContracts = LedgerOrchestrator.Contracts.GetActiveContractCount();

            RewindContext.EndRewind();
            logLines.Clear();

            ParsekScenario.RecalculateAndPatchForPostRewindFlightLoad(200.0);

            AssertLogHasCutoffSummary(logLines, 3, 1, "200");
            Assert.Equal(firstBalance, LedgerOrchestrator.Funds.GetRunningBalance(), 5);
            Assert.Equal(10000.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
            Assert.Equal(firstActiveContracts, LedgerOrchestrator.Contracts.GetActiveContractCount());
            Assert.Equal(0, LedgerOrchestrator.Contracts.GetActiveContractCount());
        }

        [Fact]
        public void PostRewindFlightLoadCurrentUtCutoffDecision_HappyPath_ReturnsTrue()
        {
            Assert.True(ShouldUseCurrentUtCutoffForPostRewindFlightLoad());
        }

        [Fact]
        public void PostRewindFlightLoadCurrentUtCutoffDecision_RejectsRevert()
        {
            Assert.False(ShouldUseCurrentUtCutoffForPostRewindFlightLoad(isRevert: true));
        }

        [Fact]
        public void PostRewindFlightLoadCurrentUtCutoffDecision_RejectsNonFlightScene()
        {
            Assert.False(ShouldUseCurrentUtCutoffForPostRewindFlightLoad(loadedSceneIsFlight: false));
        }

        [Fact]
        public void PostRewindFlightLoadCurrentUtCutoffDecision_RejectsPlanetariumNotReady()
        {
            Assert.False(ShouldUseCurrentUtCutoffForPostRewindFlightLoad(planetariumReady: false));
        }

        [Fact]
        public void PostRewindFlightLoadCurrentUtCutoffDecision_RejectsPendingTree()
        {
            Assert.False(ShouldUseCurrentUtCutoffForPostRewindFlightLoad(hasPendingTree: true));
        }

        [Fact]
        public void PostRewindFlightLoadCurrentUtCutoffDecision_RejectsQuickloadRestore()
        {
            Assert.False(ShouldUseCurrentUtCutoffForPostRewindFlightLoad(
                restoreMode: ParsekScenario.ActiveTreeRestoreMode.Quickload));
        }

        [Fact]
        public void PostRewindFlightLoadCurrentUtCutoffDecision_RejectsVesselSwitchRestore()
        {
            Assert.False(ShouldUseCurrentUtCutoffForPostRewindFlightLoad(
                restoreMode: ParsekScenario.ActiveTreeRestoreMode.VesselSwitch));
        }

        [Fact]
        public void PostRewindFlightLoadCurrentUtCutoffDecision_RejectsLiveRecorder()
        {
            Assert.False(ShouldUseCurrentUtCutoffForPostRewindFlightLoad(hasLiveRecorder: true));
        }

        [Fact]
        public void PostRewindFlightLoadCurrentUtCutoffDecision_RejectsActiveUncommittedTree()
        {
            Assert.False(ShouldUseCurrentUtCutoffForPostRewindFlightLoad(
                hasActiveUncommittedTree: true));
        }

        [Fact]
        public void PostRewindFlightLoadCurrentUtCutoffDecision_RejectsMissingFutureLedgerActions()
        {
            Assert.False(ShouldUseCurrentUtCutoffForPostRewindFlightLoad(
                hasFutureLedgerActions: false));
        }

        // ================================================================
        // Mixed-type filter test
        // ================================================================

        [Fact]
        public void Mixed_PrePassAndWalkBothFiltered()
        {
            // seed + earning@100 + spending@300 + earning@500; cutoff=400 drops the earning@500.
            // Expected final: 10000 + 500 - 200 = 10300.
            AddAll(
                FundsSeed(10000f),
                FundsEarning(100.0, 500f, "rec-a"),
                FundsSpending(300.0, 200f, "rec-b"),
                FundsEarning(500.0, 9000f, "rec-c"));

            LedgerOrchestrator.RecalculateAndPatch(400.0);

            Assert.Equal(10300.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
            Assert.Equal(200.0, LedgerOrchestrator.Funds.GetTotalCommittedSpendings(), 1);
            Assert.Equal(500.0, LedgerOrchestrator.Funds.GetTotalEarnings(), 1);
        }

        [Fact]
        public void Mixed_SpendingAfterCutoffReservesProjectedHeadroom()
        {
            // Visible/current aggregate fields still respect the cutoff, but available funds
            // are cashflow-projected through future spendings so the player cannot spend
            // resources already needed later in the committed timeline.
            AddAll(
                FundsSeed(1000f),
                FundsSpending(100.0, 50f),
                FundsSpending(1000.0, 900f));

            LedgerOrchestrator.RecalculateAndPatch(500.0);

            Assert.Equal(50.0, LedgerOrchestrator.Funds.GetTotalCommittedSpendings(), 1);
            Assert.Equal(50.0, LedgerOrchestrator.Funds.GetAvailableFunds(), 1);
        }

        [Fact]
        public void CutoffProjection_DoesNotLeakFutureContractLogs()
        {
            AddAll(
                FundsSeed(1000f),
                ContractAccept(100.0, "future-contract", 50f));

            LedgerOrchestrator.RecalculateAndPatch(50.0);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Contracts]")
                && l.Contains("future-contract"));
        }

        // ================================================================
        // Log content assertions
        // ================================================================

        [Fact]
        public void Log_AlwaysIncludesCutoffFields_ForNullCall()
        {
            AddAll(FundsSeed(1000f), Milestone(100.0, "M1", 50f));

            LedgerOrchestrator.RecalculateAndPatch();

            AssertLogHasCutoffSummary(logLines, 2, 2, "null");
        }

        [Fact]
        public void Log_AlwaysIncludesCutoffFields_ForNumericCall()
        {
            AddAll(FundsSeed(1000f),
                Milestone(100.0, "M1", 50f),
                Milestone(500.0, "M2", 75f));

            LedgerOrchestrator.RecalculateAndPatch(250.0);

            AssertLogHasCutoffSummary(logLines, 3, 2, "250");
        }

        [Fact]
        public void Log_RecalcEngineAlsoLogsCutoff()
        {
            var capture = new CaptureModule();
            RecalculationEngine.ClearModules();
            RecalculationEngine.RegisterModule(capture, RecalculationEngine.ModuleTier.FirstTier);

            var actions = new List<GameAction>
            {
                FundsSeed(1000f),
                Milestone(100.0, "M1", 50f)
            };

            RecalculationEngine.Recalculate(actions, 50.0);

            Assert.Contains(logLines, l =>
                l.Contains(EngineTag)
                && l.Contains("Recalculate complete")
                && l.Contains("actionsTotal=2")
                && l.Contains("actionsAfterCutoff=1")
                && l.Contains("cutoffUT=50")
                && l.Contains("filteredOut=1"));

            RecalculationEngine.ClearModules();
        }

        // ================================================================
        // Direct engine API: seed-survival regardless of type
        // ================================================================

        [Fact]
        public void DirectEngine_AllThreeSeedTypesSurviveCutoff()
        {
            var capture = new CaptureModule();
            RecalculationEngine.ClearModules();
            RecalculationEngine.RegisterModule(capture, RecalculationEngine.ModuleTier.FirstTier);

            var actions = new List<GameAction>
            {
                FundsSeed(1000f),
                ScienceSeed(50f),
                RepSeed(5f),
                Milestone(100.0, "M1", 10f)
            };

            RecalculationEngine.Recalculate(actions, -1.0);

            // All 3 seeds survive; the milestone is filtered out.
            int fundsSeed = 0, sciSeed = 0, repSeed = 0, milestones = 0;
            for (int i = 0; i < capture.ProcessedActions.Count; i++)
            {
                var a = capture.ProcessedActions[i];
                if (a.Type == GameActionType.FundsInitial) fundsSeed++;
                if (a.Type == GameActionType.ScienceInitial) sciSeed++;
                if (a.Type == GameActionType.ReputationInitial) repSeed++;
                if (a.Type == GameActionType.MilestoneAchievement) milestones++;
            }
            Assert.Equal(1, fundsSeed);
            Assert.Equal(1, sciSeed);
            Assert.Equal(1, repSeed);
            Assert.Equal(0, milestones);

            RecalculationEngine.ClearModules();
        }

        [Fact]
        public void DirectEngine_CutoffProjection_UsesProjectionCloneInsteadOfRegisteredModule()
        {
            var counting = new CountingModule();
            RecalculationEngine.ClearModules();
            RecalculationEngine.RegisterModule(counting, RecalculationEngine.ModuleTier.FirstTier);

            var actions = new List<GameAction>
            {
                FundsSeed(1000f),
                Milestone(100.0, "future-only", 50f)
            };

            RecalculationEngine.Recalculate(actions, 50.0);

            Assert.Equal(1, counting.ResetCalls);
            Assert.Equal(1, counting.ProcessCalls);
            Assert.Equal(1, counting.PostWalkCalls);
            Assert.Equal(0, counting.FutureProcessCalls);

            RecalculationEngine.ClearModules();
        }

        // ================================================================
        // Deadline-driven ContractsModule.PrePass synthesis under cutoff
        // (Phase D round 2 — #436 follow-up)
        // ================================================================

        [Fact]
        public void DeadlineExpiredBeforeCutoff_SyntheticFailFires()
        {
            // Accept at UT=100 with DeadlineUT=300, advance=200, fundsPenalty=500.
            // A later real action exists at UT=500 (so "last action UT" without a cutoff
            // would be 500). Cutoff=400 filters the UT=500 action out of the list; the
            // remaining "last action UT" is 100, which is BEFORE the deadline of 300.
            // Without the walkNowUT plumbing, the synthetic fail would never fire — the
            // deadline would appear to be in the future relative to the filtered list's
            // tail. With the cutoff passed as walkNowUT=400, the deadline IS in the past
            // and the synthetic ContractFail is injected.
            //
            // Expected balances:
            //   seed   10000
            //   +advance  +200 (ContractAccept at UT=100)
            //   -penalty  -500 (synthetic ContractFail at deadlineUT=300)
            //           = 9700
            AddAll(
                FundsSeed(10000f),
                ContractAcceptWithDeadline(100.0, "c-late-fail",
                    advance: 200f, deadlineUt: 300f, fundsPenalty: 500f),
                Milestone(500.0, "PostCutoffMilestone", 9999f));

            LedgerOrchestrator.RecalculateAndPatch(400.0);

            Assert.Equal(9700.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);

            // Slot released — active contract count should be 0.
            Assert.Equal(0, LedgerOrchestrator.Contracts.GetActiveContractCount());

            // The synthetic fail log should mention the cutoff-sourced now.
            Assert.Contains(logLines, l =>
                l.Contains("[Contracts]")
                && l.Contains("injected synthetic ContractFail")
                && l.Contains("c-late-fail")
                && l.Contains("source=cutoff"));
        }

        [Fact]
        public void DeadlineNotYetExpiredUnderCutoff_NoSyntheticFail()
        {
            // Accept at UT=100 with DeadlineUT=700. Cutoff=400. Deadline is in the future
            // relative to the cutoff — no synthetic fail, contract stays active.
            AddAll(
                FundsSeed(10000f),
                ContractAcceptWithDeadline(100.0, "c-still-active",
                    advance: 200f, deadlineUt: 700f, fundsPenalty: 500f));

            LedgerOrchestrator.RecalculateAndPatch(400.0);

            // Seed + advance, no penalty yet.
            Assert.Equal(10200.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);

            // Contract is still active.
            Assert.Equal(1, LedgerOrchestrator.Contracts.GetActiveContractCount());

            // No synthetic fail log for this contract.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Contracts]")
                && l.Contains("injected synthetic ContractFail")
                && l.Contains("c-still-active"));
        }

        [Fact]
        public void DeadlineExpiredButCutoffIsEarlier_NoSyntheticFail()
        {
            // Accept at UT=100 with DeadlineUT=500. Cutoff=300 — rewind UT is BEFORE the
            // deadline, so from the ledger's perspective the deadline has not yet expired.
            // Contract stays active after the walk.
            AddAll(
                FundsSeed(10000f),
                ContractAcceptWithDeadline(100.0, "c-pre-deadline-rewind",
                    advance: 200f, deadlineUt: 500f, fundsPenalty: 500f));

            LedgerOrchestrator.RecalculateAndPatch(300.0);

            Assert.Equal(10200.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
            Assert.Equal(1, LedgerOrchestrator.Contracts.GetActiveContractCount());
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Contracts]")
                && l.Contains("injected synthetic ContractFail")
                && l.Contains("c-pre-deadline-rewind"));
        }

        [Fact]
        public void NullCutoff_DeadlineSynthPreservesLastActionUtHeuristic()
        {
            // Accept at UT=100 with DeadlineUT=300. A later real action exists at UT=500.
            // With cutoff=null, the fallback heuristic ("last-action UT") wins — 500 > 300,
            // so the synthetic fail still fires just like before Phase D. This pins that
            // non-rewind callers are not regressed by the new parameter.
            AddAll(
                FundsSeed(10000f),
                ContractAcceptWithDeadline(100.0, "c-legacy-path",
                    advance: 200f, deadlineUt: 300f, fundsPenalty: 500f),
                Milestone(500.0, "PinsLastActionUT", 50f));

            LedgerOrchestrator.RecalculateAndPatch();

            // seed + advance + milestone funds - penalty = 10000 + 200 + 50 - 500 = 9750
            Assert.Equal(9750.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
            Assert.Equal(0, LedgerOrchestrator.Contracts.GetActiveContractCount());

            // Log should show the heuristic source, not the cutoff source.
            Assert.Contains(logLines, l =>
                l.Contains("[Contracts]")
                && l.Contains("injected synthetic ContractFail")
                && l.Contains("c-legacy-path")
                && l.Contains("source=lastActionUT"));
        }

        [Fact]
        public void ExplicitFailTakesPrecedence_NoDoubleSynthesisUnderCutoff()
        {
            // Ledger already has an explicit ContractFail at UT=200 for a contract
            // accepted at UT=100 with DeadlineUT=400. Cutoff=300. The explicit fail
            // resolves the contract before the cutoff, so ContractsModule.PrePass must
            // NOT also synthesize a second fail at the deadline — the tracked dict
            // removes the contract on the first resolution, and since both the accept
            // and the explicit fail are within the cutoff, tracked[] ends up empty.
            AddAll(
                FundsSeed(10000f),
                ContractAcceptWithDeadline(100.0, "c-explicit-fail",
                    advance: 200f, deadlineUt: 400f, fundsPenalty: 500f),
                ContractFail(200.0, "c-explicit-fail", penalty: 500f));

            LedgerOrchestrator.RecalculateAndPatch(300.0);

            // seed + advance - single penalty = 10000 + 200 - 500 = 9700
            // (not 9200 — that would be the double-fail bug)
            Assert.Equal(9700.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);

            // No synthetic injection log for this contract.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Contracts]")
                && l.Contains("injected synthetic ContractFail")
                && l.Contains("c-explicit-fail"));
        }

        // ================================================================
        // Affordability helpers scope to Planetarium UT cutoff
        // (Phase D round 3 — #436 follow-up)
        //
        // CanAfford{Science,Funds}Spending must walk visible state with
        // cutoff = Planetarium.GetUniversalTime(), then reserve the minimum projected
        // future cashflow balance. Future earnings do not inflate present spendability;
        // future spendings only reduce present spendability when the future cashflow
        // minimum dips below the current balance.
        //
        // Planetarium throws NRE in xUnit's Unity-static-free harness, so these
        // tests drive LedgerOrchestrator.NowUtProviderForTesting instead.
        // ================================================================

        [Fact]
        public void CanAffordScienceSpending_PostRewindFutureEarningFiltered()
        {
            // Seed = 100 sci at UT=0. Future earning at UT=500 adds +50.
            // Simulated "now" = UT=200. Cutoff should drop the UT=500 earning,
            // so 100 sci is available; cost=120 is NOT affordable.
            AddAll(
                ScienceSeed(100f),
                ScienceEarning(500.0, 50f, "rec-future"));

            LedgerOrchestrator.NowUtProviderForTesting = () => 200.0;

            bool affordable = LedgerOrchestrator.CanAffordScienceSpending(120f);

            Assert.False(affordable);

            // Log assertion pins both the affordability result and the cutoff source.
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("CanAffordScienceSpending")
                && l.Contains("affordable=False")
                && l.Contains("cutoffUT=200"));
        }

        [Fact]
        public void CanAffordScienceSpending_PastEarningStillCounts()
        {
            // Same seed + earning, but "now" = UT=600 (earning is in the past).
            // Walk at cutoff=600 includes the +50, giving 150 sci; cost=120 IS affordable.
            AddAll(
                ScienceSeed(100f),
                ScienceEarning(500.0, 50f, "rec-past"));

            LedgerOrchestrator.NowUtProviderForTesting = () => 600.0;

            bool affordable = LedgerOrchestrator.CanAffordScienceSpending(120f);

            Assert.True(affordable);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("CanAffordScienceSpending")
                && l.Contains("affordable=True")
                && l.Contains("cutoffUT=600"));
        }

        [Fact]
        public void CanAffordFundsSpending_PostRewindFutureEarningFiltered()
        {
            // Seed = 1000 funds at UT=0. Future earning at UT=500 adds +500.
            // Simulated "now" = UT=200 — cutoff drops the future earning, so
            // 1000 funds is available; cost=1200 is NOT affordable.
            AddAll(
                FundsSeed(1000f),
                FundsEarning(500.0, 500f, "rec-future-funds"));

            LedgerOrchestrator.NowUtProviderForTesting = () => 200.0;

            bool affordable = LedgerOrchestrator.CanAffordFundsSpending(1200f);

            Assert.False(affordable);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("CanAffordFundsSpending")
                && l.Contains("affordable=False")
                && l.Contains("cutoffUT=200"));
        }

        [Fact]
        public void CanAffordFundsSpending_PastEarningStillCounts()
        {
            AddAll(
                FundsSeed(1000f),
                FundsEarning(500.0, 500f, "rec-past-funds"));

            LedgerOrchestrator.NowUtProviderForTesting = () => 600.0;

            bool affordable = LedgerOrchestrator.CanAffordFundsSpending(1200f);

            Assert.True(affordable);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("CanAffordFundsSpending")
                && l.Contains("affordable=True")
                && l.Contains("cutoffUT=600"));
        }

        [Fact]
        public void CanAffordScienceSpending_FutureSpendingReservesCurrentHeadroom()
        {
            // Seed = 100 sci at UT=0. Future spending at UT=500 consumes 80.
            // "Now" = UT=200. Current visible science is 100, but the projected
            // future cashflow minimum is 20, so a cost=50 unlock is blocked.
            AddAll(
                ScienceSeed(100f),
                new GameAction
                {
                    UT = 500.0,
                    Type = GameActionType.ScienceSpending,
                    NodeId = "future-node",
                    Cost = 80f,
                    RecordingId = "rec-future-spend"
                });

            LedgerOrchestrator.NowUtProviderForTesting = () => 200.0;

            bool affordable = LedgerOrchestrator.CanAffordScienceSpending(50f);

            Assert.False(affordable);
            Assert.Equal(20.0, LedgerOrchestrator.Science.GetAvailableScience(), 1);
        }

        [Fact]
        public void CanAffordScienceSpending_FutureEarningBeforeFutureSpendingPreservesCurrentHeadroom()
        {
            // Seed = 100 sci. Future earning at UT=300 covers a future spending at UT=500,
            // so the minimum projected balance never drops below the current 100.
            AddAll(
                ScienceSeed(100f),
                ScienceEarning(300.0, 80f, "future-cover"),
                new GameAction
                {
                    UT = 500.0,
                    Type = GameActionType.ScienceSpending,
                    NodeId = "future-node",
                    Cost = 80f,
                    RecordingId = "rec-future-spend"
                });

            LedgerOrchestrator.NowUtProviderForTesting = () => 200.0;

            bool affordable = LedgerOrchestrator.CanAffordScienceSpending(100f);

            Assert.True(affordable);
            Assert.Equal(100.0, LedgerOrchestrator.Science.GetAvailableScience(), 1);
        }

        [Fact]
        public void CanAffordFundsSpending_FutureSpendingReservesCurrentHeadroom()
        {
            AddAll(
                FundsSeed(1000f),
                FundsSpending(500.0, 800f, "future-build"));

            LedgerOrchestrator.NowUtProviderForTesting = () => 200.0;

            bool affordable = LedgerOrchestrator.CanAffordFundsSpending(500f);

            Assert.False(affordable);
            Assert.Equal(200.0, LedgerOrchestrator.Funds.GetAvailableFunds(), 1);
        }

        [Fact]
        public void CanAffordFundsSpending_FutureEarningBeforeFutureSpendingPreservesCurrentHeadroom()
        {
            AddAll(
                FundsSeed(1000f),
                FundsEarning(300.0, 800f, "future-funds-cover"),
                FundsSpending(500.0, 800f, "future-build"));

            LedgerOrchestrator.NowUtProviderForTesting = () => 200.0;

            bool affordable = LedgerOrchestrator.CanAffordFundsSpending(1000f);

            Assert.True(affordable);
            Assert.Equal(1000.0, LedgerOrchestrator.Funds.GetAvailableFunds(), 1);
        }

        [Fact]
        public void CanAffordFundsSpending_FutureDeadlinePenaltyAfterLastActionReservesCurrentHeadroom()
        {
            AddAll(
                FundsSeed(1000f),
                ContractAcceptWithDeadline(
                    100.0,
                    "deadline-only-penalty",
                    advance: 0f,
                    deadlineUt: 800f,
                    fundsPenalty: 700f));

            LedgerOrchestrator.NowUtProviderForTesting = () => 200.0;

            bool affordable = LedgerOrchestrator.CanAffordFundsSpending(400f);

            Assert.False(affordable);
            Assert.Equal(300.0, LedgerOrchestrator.Funds.GetAvailableFunds(), 1);
        }

        [Fact]
        public void CanAffordScienceSpending_NullSeam_FallsBackToPlanetarium()
        {
            // When no test seam is installed the helper calls Planetarium directly.
            // In the xUnit harness that throws NRE (Unity statics uninitialized).
            // Pin this contract so a future change that silently swallows the NRE
            // (try/catch around the Planetarium call) is caught — the spec forbids
            // wrapping Planetarium access in defensive error handling.
            AddAll(ScienceSeed(100f));

            LedgerOrchestrator.NowUtProviderForTesting = null;

            Assert.Throws<System.NullReferenceException>(() =>
                LedgerOrchestrator.CanAffordScienceSpending(50f));
        }

        [Fact]
        public void BaselineZeroScienceAndRep_AreSeededBeforeFutureTimelineValues()
        {
            GameStateStore.AddBaseline(new GameStateBaseline
            {
                ut = 0.0,
                funds = 25000.0,
                science = 0.0,
                reputation = 0f
            });

            AddAll(
                FundsSpending(36.54, 3805f, "rollout"),
                Milestone(72.96, "future-rep", 4800f, rep: 1f),
                ScienceEarning(99.93, 7.728f, "future-science"),
                new GameAction
                {
                    UT = 124.43,
                    Type = GameActionType.ScienceSpending,
                    NodeId = "future-node",
                    Cost = 5f
                });

            LedgerOrchestrator.RecalculateAndPatch(49.42);

            Assert.Equal(21195.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
            Assert.Equal(0.0, LedgerOrchestrator.Science.GetRunningScience(), 3);
            Assert.InRange(LedgerOrchestrator.Reputation.GetRunningRep(), -0.001f, 0.001f);

            Assert.Contains(Ledger.Actions, a =>
                a.Type == GameActionType.ScienceInitial && a.InitialScience == 0f);
            Assert.Contains(Ledger.Actions, a =>
                a.Type == GameActionType.ReputationInitial && a.InitialReputation == 0f);
        }

        [Fact]
        public void ZeroFundsInitialBaseline_DoesNotSealFundsBeforeFundingLoads()
        {
            GameStateStore.AddBaseline(new GameStateBaseline
            {
                ut = 0.0,
                funds = 0.0,
                science = 0.0,
                reputation = 0f
            });

            LedgerOrchestrator.RecalculateAndPatch(0.0);

            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.FundsInitial);
            Assert.Contains(Ledger.Actions, a =>
                a.Type == GameActionType.ScienceInitial && a.InitialScience == 0f);
            Assert.Contains(Ledger.Actions, a =>
                a.Type == GameActionType.ReputationInitial && a.InitialReputation == 0f);
        }

        [Fact]
        public void ExistingZeroFundsSeed_IsRepairedFromInitialBaseline()
        {
            AddAll(FundsSeed(0f));
            GameStateStore.AddBaseline(new GameStateBaseline
            {
                ut = 0.0,
                funds = 25000.0,
                science = 0.0,
                reputation = 0f
            });

            LedgerOrchestrator.RecalculateAndPatch(0.0);

            Assert.Contains(Ledger.Actions, a =>
                a.Type == GameActionType.FundsInitial && a.InitialFunds == 25000f);
            Assert.Equal(25000.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
        }

        [Fact]
        public void LaterBaselineWithTimelineActions_IsNotPromotedToInitialSeed()
        {
            GameStateStore.AddBaseline(new GameStateBaseline
            {
                ut = 108.97,
                funds = 42795.0,
                science = 11.04,
                reputation = 1f
            });

            AddAll(
                ScienceEarning(99.93, 7.728f, "future-science"),
                Milestone(108.97, "future-rep", 0f, rep: 1f));

            LedgerOrchestrator.RecalculateAndPatch(49.42);

            Assert.Contains(Ledger.Actions, a =>
                a.Type == GameActionType.ScienceInitial && a.InitialScience == 0f);
            Assert.Contains(Ledger.Actions, a =>
                a.Type == GameActionType.ReputationInitial && a.InitialReputation == 0f);
            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.ScienceInitial && Math.Abs(a.InitialScience - 11.04f) < 0.001f);
            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.ReputationInitial && Math.Abs(a.InitialReputation - 1f) < 0.001f);
        }

        // ================================================================
        // Test support
        // ================================================================

        /// <summary>
        /// Test-only module that records every <see cref="GameAction"/> dispatched to it
        /// so tests can assert on the filtered set without coupling to any production module.
        /// </summary>
        private sealed class CaptureModule : IResourceModule
        {
            public readonly List<GameAction> ProcessedActions = new List<GameAction>();

            public void Reset() { ProcessedActions.Clear(); }
            public bool PrePass(List<GameAction> actions, double? walkNowUT = null) { return false; }
            public void ProcessAction(GameAction action) { ProcessedActions.Add(action); }
            public void PostWalk() { }
        }

        private sealed class CountingModule : IResourceModule, IProjectionCloneableModule
        {
            public int ResetCalls;
            public int ProcessCalls;
            public int FutureProcessCalls;
            public int PostWalkCalls;

            public void Reset()
            {
                ResetCalls++;
            }

            public bool PrePass(List<GameAction> actions, double? walkNowUT = null) { return false; }

            public void ProcessAction(GameAction action)
            {
                ProcessCalls++;
                if (action != null && action.UT > 50.0)
                    FutureProcessCalls++;
            }

            public void PostWalk()
            {
                PostWalkCalls++;
            }

            public IResourceModule CreateProjectionClone()
            {
                return new CountingModule();
            }
        }
    }
}
