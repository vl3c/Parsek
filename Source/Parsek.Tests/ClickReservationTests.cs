using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek.Patches;
using Xunit;
using Contract = Contracts.Contract;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 4 of the committed-value rule (plan section 2.5, design 3.13 rule 3): the click
    /// predicates. A purchase is refused when its cost is above what the pool can spend now
    /// (funds and science: the target less the open contracts' fail penalties; reputation:
    /// the running value less what the committed costs ahead need, less the penalties), and a
    /// Mission Control Accept / Decline / Cancel when it would leave the spendable amount
    /// below <c>min(0, spendable)</c>. Everything is inert while no committed cost lies ahead
    /// (V's ruling, plan 2.2), and every mark reads the decision its backstop reads.
    /// </summary>
    [Collection("Sequential")]
    public class ClickReservationTests : IDisposable
    {
        private const double Now = 1000.0;
        private readonly List<string> logLines = new List<string>();
        private readonly FundsModule funds = new FundsModule();
        private readonly ScienceModule science = new ScienceModule();
        private readonly ReputationModule reputation = new ReputationModule();
        private readonly ContractsModule contracts = new ContractsModule();
        private int dialogCount;
        private string dialogAction;
        private string dialogReason;
        private string dialogDetail;
        private double liveFunds = double.NaN;
        private double liveScience = double.NaN;
        private double liveRep = double.NaN;
        private readonly List<OpenContractPenalty> open = new List<OpenContractPenalty>();

        public ClickReservationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            GameStateStore.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            StockPoolReference.ResetForTesting();
            ClickReservationGate.ResetForTesting();
            StrategyReservationGate.ResetForTesting();
            StockUiPartPurchase.ResetForTesting();
            LaunchReservation.ResetForTesting();
            KerbalHirePatch.HireCostProviderForTesting = null;
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = () => false;
            RecalculationEngine.ClearModules();
            RecalculationEngine.RegisterModule(science, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(contracts, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(reputation, RecalculationEngine.ModuleTier.SecondTier);
            CommittedFutureIndexCache.NowUtProviderForTesting = () => Now;
            ClickReservationGate.LivePoolProviderForTesting = pool =>
                pool == StockPool.Funds ? liveFunds : pool == StockPool.Science ? liveScience : liveRep;
            ClickReservationGate.OpenContractsProviderForTesting = () => open;
            ClickReservationGate.RepLossDeclinedProviderForTesting = () => 1.0;
            CommittedActionDialog.TestHookForTesting = (action, reason, detail) =>
            {
                dialogCount++;
                dialogAction = action;
                dialogReason = reason;
                dialogDetail = detail;
            };
        }

        public void Dispose()
        {
            CommittedActionDialog.TestHookForTesting = null;
            RecalculationEngine.ClearModules();
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = null;
            KerbalHirePatch.HireCostProviderForTesting = null;
            ClickReservationGate.ResetForTesting();
            StrategyReservationGate.ResetForTesting();
            StockUiPartPurchase.ResetForTesting();
            LaunchReservation.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            StockPoolReference.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------ rows

        private static GameAction FundsSeed(double amount) => new GameAction
        {
            ActionId = "seedF", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = (float)amount,
        };

        private static GameAction RepSeed(double amount) => new GameAction
        {
            ActionId = "seedR", UT = 0, Type = GameActionType.ReputationInitial, InitialReputation = (float)amount,
        };

        private static GameAction Upgrade(string id, double ut, float cost) => new GameAction
        {
            ActionId = id, UT = ut, Type = GameActionType.FacilityUpgrade, FacilityCost = cost,
        };

        private static GameAction Earn(string id, double ut, float amount) => new GameAction
        {
            ActionId = id, UT = ut, Type = GameActionType.FundsEarning, FundsAwarded = amount,
        };

        private static GameAction Strategy(string id, double ut, float repCost) => new GameAction
        {
            ActionId = id, UT = ut, Type = GameActionType.StrategyActivate, StrategyId = id,
            SetupReputationCost = repCost,
        };

        private static GameAction Complete(string contractKey, double ut) => new GameAction
        {
            ActionId = "complete-" + contractKey, UT = ut, Type = GameActionType.ContractComplete, ContractId = contractKey,
        };

        /// <summary>The L1.2 ledger: 120k, a committed 100k upgrade at day 50 (UT 5000).</summary>
        private void L12Ledger()
        {
            Ledger.AddAction(FundsSeed(120000));
            Ledger.AddAction(Upgrade("up", 5000, 100000));
            liveFunds = 20000; // the pool patch writes the target, 120k - 100k
        }

        private static ClickReservationSnapshot Snap(double fundsSpendable, bool fundsAhead,
            double repSpendable = 0, bool repAhead = false)
        {
            var s = ClickReservationSnapshot.Inert(Now);
            s.Funds = new ClickPoolReserve
            {
                Pool = StockPool.Funds, CostAhead = fundsAhead, Spendable = fundsSpendable, Live = double.NaN,
            };
            s.Reputation = new ClickPoolReserve
            {
                Pool = StockPool.Reputation, CostAhead = repAhead, Spendable = repSpendable, Live = double.NaN,
            };
            return s;
        }

        // ------------------------------------------------------------------ the pure model

        [Fact]
        public void Compose_FundsSpendableIsTheTargetLessTheOpenPenalties_WhenACostLiesAhead()
        {
            var r = ClickReservation.Compose(StockPool.Funds, true, 20000, 0, 20000, 50000);
            Assert.Equal(-30000.0, r.Spendable, 6);
            Assert.Equal(50000.0, r.OpenRes, 6);
        }

        [Fact]
        public void Compose_NoCostAhead_CountsNoOpenPenalty()
        {
            var r = ClickReservation.Compose(StockPool.Funds, false, 20000, 0, 20000, 50000);
            Assert.Equal(0.0, r.OpenRes, 6);
            Assert.Equal(20000.0, r.Spendable, 6);
        }

        [Fact]
        public void Compose_TheTargetIsReadNoHigherThanTheLivePool_AndNoLowerThanZero()
        {
            Assert.Equal(15000.0, ClickReservation.Compose(StockPool.Funds, true, 20000, 0, 15000, 0).Spendable, 6);
            Assert.Equal(0.0, ClickReservation.Compose(StockPool.Funds, true, -5000, 0, double.NaN, 0).Spendable, 6);
        }

        [Fact]
        public void Compose_ReputationIsRunningLessNeedLessPenalties()
        {
            var r = ClickReservation.Compose(StockPool.Reputation, true, 60, 40, 60, 5);
            Assert.Equal(15.0, r.Spendable, 6);
            // A negative running value stays negative (reputation has no floor at 0).
            Assert.Equal(-50.0, ClickReservation.Compose(StockPool.Reputation, true, -10, 40, double.NaN, 0).Spendable, 6);
        }

        [Fact]
        public void EvaluatePurchase_L12_AnyPurchaseIsRefused_WhileThePenaltyAndTheUpgradeNeedTheMoney()
        {
            // L1.2: 120k, a 50k open penalty, a committed 100k upgrade: spendable -30k.
            var snap = ClickReservationSnapshot.Inert(Now);
            snap.Funds = ClickReservation.Compose(StockPool.Funds, true, 20000, 0, 20000, 50000);
            var r = ClickReservation.EvaluatePurchase(snap, StockPool.Funds, 10000);
            Assert.True(r.Refused);
            Assert.Equal(-30000.0, r.Spendable, 6);
        }

        [Fact]
        public void EvaluatePurchase_IsInert_WithNoCostAhead_WhateverThePenalties()
        {
            var snap = ClickReservationSnapshot.Inert(Now);
            snap.Funds = ClickReservation.Compose(StockPool.Funds, false, 20000, 0, 20000, 1e9);
            Assert.False(ClickReservation.EvaluatePurchase(snap, StockPool.Funds, 19000).Refused);
            Assert.False(ClickReservation.EvaluatePurchase(ClickReservationSnapshot.Inert(Now), StockPool.Reputation, 1e6).Refused);
        }

        [Fact]
        public void EvaluatePurchase_APlainShortageStaysStocks()
        {
            var snap = ClickReservationSnapshot.Inert(Now);
            snap.Funds = ClickReservation.Compose(StockPool.Funds, true, 20000, 0, 20000, 50000);
            // Stock refuses 25k itself (live 20k): Parsek does not.
            Assert.False(ClickReservation.EvaluatePurchase(snap, StockPool.Funds, 25000).Refused);
        }

        [Fact]
        public void EvaluatePurchase_AllowsWhatTheSpendableCovers()
        {
            var snap = ClickReservationSnapshot.Inert(Now);
            snap.Funds = ClickReservation.Compose(StockPool.Funds, true, 80000, 0, 80000, 50000);
            Assert.False(ClickReservation.EvaluatePurchase(snap, StockPool.Funds, 30000).Refused);
            Assert.True(ClickReservation.EvaluatePurchase(snap, StockPool.Funds, 30001).Refused);
        }

        [Fact]
        public void EvaluatePurchase_AStrategyIsRefusedByAnyOfItsPools()
        {
            var snap = Snap(100000, true, 5, true);
            var r = ClickReservation.EvaluatePurchase(snap, 1000, 0, 10);
            Assert.True(r.Refused);
            Assert.Equal(StockPool.Reputation, r.Pool);
            Assert.False(ClickReservation.EvaluatePurchase(snap, 1000, 0, 5).Refused);
        }

        [Fact]
        public void InverseLerp_IsUnitysClampedForm()
        {
            Assert.Equal(0.5, ClickReservation.InverseLerp(100, 300, 200), 9);
            Assert.Equal(0.0, ClickReservation.InverseLerp(100, 300, 50), 9);
            Assert.Equal(1.0, ClickReservation.InverseLerp(100, 300, 900), 9);
            Assert.Equal(0.0, ClickReservation.InverseLerp(100, 100, 900), 9);
            // No deadline (0): stock's cancel penalty is the advance.
            Assert.Equal(0.0, ClickReservation.InverseLerp(100, 0, 900), 9);
        }

        [Fact]
        public void EffectOf_Accept_PaysTheAdvanceAndReservesThePenalties()
        {
            var m = new ContractMoney { FundsAdvance = 5000, FundsFailure = 30000, ReputationFailure = 10 };
            var e = ClickReservation.EffectOf(ContractClick.Accept, m, Now, 1, 0, false);
            Assert.Equal(5000.0, e.FundsDelta, 6);
            Assert.Equal(30000.0, e.FundsAddedReserve, 6);
            Assert.Equal(ClickReservation.CurvedLoss(10, 0), e.ReputationAddedReserve, 6);
            Assert.True(e.ReputationAddedReserve > 9.5 && e.ReputationAddedReserve < 10.5);
        }

        [Fact]
        public void EffectOf_Cancel_ChargesTheStockLerpAndReleasesItsOwnReserve()
        {
            var m = new ContractMoney
            {
                FundsAdvance = 5000, FundsFailure = 25000, ReputationFailure = 21, DateAccepted = 0, DateDeadline = 2000,
            };
            var e = ClickReservation.EffectOf(ContractClick.Cancel, m, Now, 1, 0, true);
            // t = 0.5: funds Lerp(5k, 25k) = 15k; reputation Lerp(1, 21) = 11 (curved).
            Assert.Equal(-15000.0, e.FundsDelta, 6);
            Assert.Equal(-25000.0, e.FundsAddedReserve, 6);
            Assert.Equal(-ClickReservation.CurvedLoss(11, 0), e.ReputationDelta, 6);
            Assert.Equal(-ClickReservation.CurvedLoss(21, 0), e.ReputationAddedReserve, 6);
        }

        [Fact]
        public void EffectOf_Decline_ChargesRepLossDeclined()
        {
            var e = ClickReservation.EffectOf(ContractClick.Decline, default(ContractMoney), Now, 3, 0, false);
            Assert.Equal(-ClickReservation.CurvedLoss(3, 0), e.ReputationDelta, 6);
            Assert.Equal(0.0, e.FundsDelta, 6);
        }

        [Fact]
        public void CurvedLoss_IsStocksCurveAtTheCurrentReputation()
        {
            Assert.Equal(-ReputationModule.ApplyReputationCurve(-20f, 500f).actualDelta, ClickReservation.CurvedLoss(20, 500), 5);
            Assert.True(ClickReservation.CurvedLoss(20, 500) > 20.0); // losses amplified at high reputation
            Assert.Equal(0.0, ClickReservation.CurvedLoss(0, 500), 9);
        }

        [Fact]
        public void EvaluateContractClick_AcceptThatDigsIntoTheReserveIsRefused()
        {
            var accept = ClickReservation.EffectOf(ContractClick.Accept,
                new ContractMoney { FundsAdvance = 5000, FundsFailure = 30000 }, Now, 1, 0, false);
            var r = ClickReservation.EvaluateContractClick(Snap(20000, true), accept);
            Assert.True(r.Refused);
            Assert.Equal(StockPool.Funds, r.Pool);
            Assert.Equal(5000.0, r.Shortfall, 6); // 20k + 5k - 30k = -5k, below min(0, 20k)
            var small = ClickReservation.EffectOf(ContractClick.Accept,
                new ContractMoney { FundsAdvance = 5000, FundsFailure = 10000 }, Now, 1, 0, false);
            Assert.False(ClickReservation.EvaluateContractClick(Snap(20000, true), small).Refused);
        }

        [Fact]
        public void EvaluateContractClick_AClickThatImprovesAShortPoolIsNeverRefused()
        {
            // Spendable -30k (L1.2). Cancelling the open contract releases its 50k reserve
            // and charges at most its fail penalty: the pool improves.
            var m = new ContractMoney { FundsAdvance = 10000, FundsFailure = 50000, DateAccepted = 0, DateDeadline = 4000 };
            var cancel = ClickReservation.EffectOf(ContractClick.Cancel, m, Now, 0, 0, true);
            Assert.False(ClickReservation.EvaluateContractClick(Snap(-30000, true), cancel).Refused);
            // An Accept whose advance pays its own reserve leaves a short pool where it was.
            var even = ClickReservation.EffectOf(ContractClick.Accept,
                new ContractMoney { FundsAdvance = 10000, FundsFailure = 10000 }, Now, 0, 0, false);
            Assert.False(ClickReservation.EvaluateContractClick(Snap(-30000, true), even).Refused);
            // One that adds more than it pays is refused.
            var worse = ClickReservation.EffectOf(ContractClick.Accept,
                new ContractMoney { FundsAdvance = 10000, FundsFailure = 20000 }, Now, 0, 0, false);
            Assert.True(ClickReservation.EvaluateContractClick(Snap(-30000, true), worse).Refused);
        }

        [Fact]
        public void EvaluateContractClick_ACancelWithoutTheReleaseCanBeRefused()
        {
            // A Cancel whose own reserve was not counted (no cost ahead for funds when it was
            // built is not this case): a large cancel penalty on a pool with little room.
            var m = new ContractMoney { FundsAdvance = 10000, FundsFailure = 50000, DateAccepted = 0, DateDeadline = 1000 };
            var cancel = ClickReservation.EffectOf(ContractClick.Cancel, m, Now, 0, 0, false);
            Assert.True(ClickReservation.EvaluateContractClick(Snap(20000, true), cancel).Refused);
        }

        [Fact]
        public void EvaluateContractClick_IsInert_WithNoCostAhead()
        {
            var worse = ClickReservation.EffectOf(ContractClick.Accept,
                new ContractMoney { FundsAdvance = 0, FundsFailure = 1e9, ReputationFailure = 1e6 }, Now, 1e6, 0, false);
            Assert.False(ClickReservation.EvaluateContractClick(Snap(-30000, false, -50, false), worse).Refused);
        }

        [Fact]
        public void EvaluateContractClick_ADeclineIsRefusedOnlyByReputation()
        {
            var decline = ClickReservation.EffectOf(ContractClick.Decline, default(ContractMoney), Now, 5, 0, false);
            var r = ClickReservation.EvaluateContractClick(Snap(-30000, true, 2, true), decline);
            Assert.True(r.Refused);
            Assert.Equal(StockPool.Reputation, r.Pool);
            Assert.False(ClickReservation.EvaluateContractClick(Snap(-30000, true, 2, false), decline).Refused);
        }

        [Fact]
        public void NeedOfRowsAfter_IsTheDeepestDipUpToTheLastCost()
        {
            var a = new GameAction();
            var rows = new List<ReservedFloorRow>
            {
                new ReservedFloorRow { Action = a, Delta = 10 },
                new ReservedFloorRow { Action = a, Delta = -40, IsCost = true },
                new ReservedFloorRow { Action = a, Delta = -30, FloorLimited = true }, // left out
                new ReservedFloorRow { Action = a, Delta = -25, Synthetic = true },    // left out
                new ReservedFloorRow { Action = a, Delta = -5, IsCost = true },
                new ReservedFloorRow { Action = a, Delta = -100 },                     // after the last cost
            };
            int costRows;
            Assert.Equal(35.0, ReservedFloorPlan.NeedOfRowsAfter(rows, out costRows), 9);
            Assert.Equal(2, costRows);
            Assert.Equal(0.0, ReservedFloorPlan.NeedOfRowsAfter(new List<ReservedFloorRow>
            {
                new ReservedFloorRow { Action = a, Delta = -50 },
            }, out costRows), 9);
            Assert.Equal(0, costRows);
        }

        [Fact]
        public void ReasonText_IsOneHouseStyleSentence()
        {
            var t = ReservationExplanation.ClickReserve();
            Assert.Equal("Reserved for later costs", t.Title);
            Assert.Equal("Reserved for later costs and contract penalties, blocked by timeline.", t.Body);
            Assert.Equal("Needs 100,000 funds, 0 free.",
                ReservationExplanation.ClickReservePurchaseDetail(StockPool.Funds, 100000, -30000));
            Assert.Equal("Short by 5.5 reputation.", ReservationExplanation.ClickReserveShortDetail(StockPool.Reputation, 5.5));
            Assert.True(t.Body.All(c => c < 128));
        }

        // ------------------------------------------------------------------ the gate over the ledger

        [Fact]
        public void Gate_NoCostAhead_IsInertAndRunsNoProbe()
        {
            Ledger.AddAction(FundsSeed(120000));
            Ledger.AddAction(Earn("e", 500, 1000));
            open.Add(new OpenContractPenalty { Key = "y", Funds = 50000, ReputationNominal = 10 });
            liveFunds = 121000;
            var snap = ClickReservationGate.Current();
            Assert.False(snap.AnyCostAhead);
            Assert.Equal(0, ClickReservationGate.ProbeCount);
            Assert.False(ClickReservationGate.EvaluatePurchaseNow(StockPool.Funds, 120000, "test").Refused);
        }

        [Fact]
        public void Gate_APastCostIsNotAhead()
        {
            Ledger.AddAction(FundsSeed(120000));
            Ledger.AddAction(Upgrade("up", 500, 100000));
            liveFunds = 20000;
            open.Add(new OpenContractPenalty { Key = "y", Funds = 50000 });
            Assert.False(ClickReservationGate.Current().AnyCostAhead);
            Assert.Equal(0, ClickReservationGate.ProbeCount);
        }

        [Fact]
        public void Gate_L12_ThePurchaseIsRefusedAndTheReasonNamesTheNumbers()
        {
            L12Ledger();
            open.Add(new OpenContractPenalty { Key = "y", Funds = 50000, ReputationNominal = 0 });
            var snap = ClickReservationGate.Current();
            Assert.True(snap.Funds.CostAhead);
            Assert.Equal(20000.0, snap.Funds.Target, 3);
            Assert.Equal(50000.0, snap.Funds.OpenRes, 3);
            Assert.Equal(-30000.0, snap.Funds.Spendable, 3);
            Assert.Equal(1, snap.OpenContractsCounted);

            var r = ClickReservationGate.EvaluatePurchaseNow(StockPool.Funds, 10000, "test upgrade");
            Assert.True(r.Refused);
            Assert.Contains(logLines, l => l.Contains("[ClickReserve]") && l.Contains("Purchase refused")
                && l.Contains("test upgrade"));
            Assert.Equal("Reserved for later costs and contract penalties, blocked by timeline. Needs 10,000 funds, 0 free.",
                ClickReservationGate.PurchaseReason(r));
        }

        [Fact]
        public void Gate_WithoutAnOpenContract_TheTargetIsSpendable()
        {
            L12Ledger();
            Assert.False(ClickReservationGate.EvaluatePurchaseNow(StockPool.Funds, 20000, "t").Refused);
        }

        [Fact]
        public void Gate_AContractWithACommittedOutcomeIsNotCountedTwice()
        {
            L12Ledger();
            Ledger.AddAction(Complete("y", 3000));
            open.Add(new OpenContractPenalty { Key = "y", Funds = 50000 });
            open.Add(new OpenContractPenalty { Key = "z", Funds = 7000 });
            var snap = ClickReservationGate.Current();
            Assert.Equal(7000.0, snap.Funds.OpenRes, 3);
            Assert.Equal(1, snap.OpenContractsCounted);
            Assert.True(snap.CountedContractKeys.Contains("z"));
            Assert.False(snap.CountedContractKeys.Contains("y"));
        }

        [Fact]
        public void Gate_ReputationNeedComesFromTheCommittedStrategyAhead()
        {
            Ledger.AddAction(RepSeed(60));
            Ledger.AddAction(Strategy("s", 5000, 40));
            liveRep = 60;
            var snap = ClickReservationGate.Current();
            Assert.True(snap.Reputation.CostAhead);
            Assert.False(snap.Funds.CostAhead);
            Assert.InRange(snap.Reputation.Need, 39.0, 42.0);
            Assert.InRange(snap.Reputation.Spendable, 18.0, 21.0);
            Assert.True(ClickReservationGate.EvaluatePurchaseNow(0, 0, 25, "strategy").Refused);
            Assert.False(ClickReservationGate.EvaluatePurchaseNow(0, 0, 15, "strategy").Refused);
            // A funds purchase is not refused: no funds cost lies ahead.
            Assert.False(ClickReservationGate.EvaluatePurchaseNow(StockPool.Funds, 1e9, "funds").Refused);
        }

        [Fact]
        public void Gate_TheProbeIsCachedUntilTheClockReachesTheNextRow()
        {
            L12Ledger();
            ClickReservationGate.Current();
            ClickReservationGate.Current();
            Assert.Equal(1, ClickReservationGate.ProbeCount);
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 4999.0;
            ClickReservationGate.Current();
            Assert.Equal(1, ClickReservationGate.ProbeCount);
            Ledger.AddAction(Earn("later", 6000, 5));
            ClickReservationGate.Current();
            Assert.Equal(2, ClickReservationGate.ProbeCount);
        }

        [Fact]
        public void Gate_ReplayBypassesEveryDecision()
        {
            L12Ledger();
            open.Add(new OpenContractPenalty { Key = "y", Funds = 50000 });
            GameStateRecorder.IsReplayingActions = true;
            try
            {
                Assert.False(ClickReservationGate.EvaluatePurchaseNow(StockPool.Funds, 10000, "t").Refused);
            }
            finally
            {
                GameStateRecorder.IsReplayingActions = false;
            }
        }

        // ------------------------------------------------------------------ Mission Control

        private static CommittedFutureIndex EmptyIndex() =>
            CommittedFutureIndex.Build(new GameAction[0], id => false, id => null, null);

        private static ContractReserveInput Input(ClickReservationSnapshot snap, ContractMoney money) =>
            ClickReservationGate.InputFor(money, snap, 1.0);

        [Fact]
        public void McDecide_ARefusedAcceptIsAContractReserveBlock_NeverMarked()
        {
            var money = new ContractMoney { FundsAdvance = 5000, FundsFailure = 30000 };
            var d = MissionControlStockAnnotation.Decide(EmptyIndex(), Now, "c1", Contract.State.Offered,
                ReservationExplanation.DefaultDateFormatter, null, false, double.PositiveInfinity,
                Input(Snap(20000, true), money));
            Assert.Equal(StockUiDecorationKind.ContractReserve, d.Kind);
            Assert.True(d.Blocked);
            Assert.False(d.Marked);
            Assert.True(MissionControlStockAnnotation.BlocksAccept(d));
            Assert.False(MissionControlStockAnnotation.BlocksDecline(d));
            Assert.Equal(MissionControlStockAnnotation.SlotDetailHeading, MissionControlStockAnnotation.DetailHeadingFor(d));
            Assert.StartsWith(ReservationExplanation.ClickReserve().Body, d.Why);
            Assert.Contains("Short by 5,000 funds.", d.Why);
        }

        [Fact]
        public void McDecide_IsUnchangedWithNoCostAheadOrNoInput()
        {
            var money = new ContractMoney { FundsAdvance = 0, FundsFailure = 1e9, ReputationFailure = 1e5 };
            foreach (var input in new[] { null, Input(ClickReservationSnapshot.Inert(Now), money) })
            {
                foreach (var state in new[] { Contract.State.Offered, Contract.State.Active })
                {
                    var d = MissionControlStockAnnotation.Decide(EmptyIndex(), Now, "c1", state,
                        ReservationExplanation.DefaultDateFormatter, null, false, double.PositiveInfinity, input);
                    Assert.Equal(StockUiDecorationKind.None, d.Kind);
                    Assert.False(d.Blocked);
                    Assert.Equal(StockUiReserveButtons.None, d.ReserveButtons);
                }
            }
        }

        [Fact]
        public void McDecide_ACommittedAcceptKeepsPrecedence()
        {
            var index = CommittedFutureIndex.Build(new[]
            {
                new GameAction { UT = 3000, Type = GameActionType.ContractAccept, ContractId = "c1" },
            }, id => false, id => null, null);
            var d = MissionControlStockAnnotation.Decide(index, Now, "c1", Contract.State.Offered,
                ReservationExplanation.DefaultDateFormatter, null, false, double.PositiveInfinity,
                Input(Snap(20000, true), new ContractMoney { FundsFailure = 1e9 }));
            Assert.Equal(StockUiDecorationKind.ContractAccept, d.Kind);
            Assert.Equal(StockUiReserveButtons.None, d.ReserveButtons);
        }

        [Fact]
        public void McDecide_AutoAcceptContractsAreLeftAlone()
        {
            var d = MissionControlStockAnnotation.Decide(EmptyIndex(), Now, "c1", Contract.State.Offered,
                ReservationExplanation.DefaultDateFormatter, null, true, double.PositiveInfinity,
                Input(Snap(20000, true), new ContractMoney { FundsFailure = 1e9 }));
            Assert.False(d.Blocked);
        }

        [Fact]
        public void McDecide_ARefusedDeclineAlone_GreysDeclineOnly()
        {
            var d = MissionControlStockAnnotation.Decide(EmptyIndex(), Now, "c1", Contract.State.Offered,
                ReservationExplanation.DefaultDateFormatter, null, false, double.PositiveInfinity,
                Input(Snap(1e9, false, 0.5, true), default(ContractMoney)));
            Assert.Equal(StockUiDecorationKind.ContractReserve, d.Kind);
            Assert.False(MissionControlStockAnnotation.BlocksAccept(d));
            Assert.True(MissionControlStockAnnotation.BlocksDecline(d));
            Assert.Equal(MissionControlStockAnnotation.DeclineDetailHeading, MissionControlStockAnnotation.DetailHeadingFor(d));
            bool accept, decline, cancel;
            MissionControlStockAnnotation.GreyedButtons(d, out accept, out decline, out cancel);
            Assert.False(accept);
            Assert.True(decline);
            Assert.False(cancel);
        }

        [Fact]
        public void McDecide_ARefusedCancelGreysCancel()
        {
            var m = new ContractMoney { FundsAdvance = 10000, FundsFailure = 50000, DateAccepted = 0, DateDeadline = 1000 };
            var d = MissionControlStockAnnotation.Decide(EmptyIndex(), Now, "c1", Contract.State.Active,
                ReservationExplanation.DefaultDateFormatter, null, false, double.PositiveInfinity, Input(Snap(20000, true), m));
            Assert.Equal(StockUiDecorationKind.ContractReserve, d.Kind);
            Assert.True(MissionControlStockAnnotation.BlocksCancel(d));
            Assert.Equal(MissionControlStockAnnotation.CancelDetailHeading, MissionControlStockAnnotation.DetailHeadingFor(d));
        }

        [Fact]
        public void McDecide_ASlotBlockGainsARefusedDecline()
        {
            var index = CommittedFutureIndex.Build(new[]
            {
                new GameAction { UT = 5000, Type = GameActionType.ContractAccept, ContractId = "c", RecordingId = "rec" },
            }, id => id == "rec", id => id == "rec" ? "Mun Lander 3" : null, null);
            var slots = ContractSlotReservation.Forecast(index, new[] { "a" }, 2, Now);
            Assert.True(slots.BlocksNewAcceptNow);
            var d = MissionControlStockAnnotation.Decide(index, Now, "c1", Contract.State.Offered,
                ReservationExplanation.DefaultDateFormatter, slots, false, double.PositiveInfinity,
                Input(Snap(1e9, false, 0.5, true), default(ContractMoney)));
            Assert.Equal(StockUiDecorationKind.ContractSlot, d.Kind);
            Assert.True(MissionControlStockAnnotation.BlocksAccept(d));
            Assert.True(MissionControlStockAnnotation.BlocksDecline(d));
            Assert.Equal(MissionControlStockAnnotation.DetailHeading, MissionControlStockAnnotation.DetailHeadingFor(d));
        }

        [Fact]
        public void McBackstops_PairWithTheGreyedButtons_OverAGrid()
        {
            // Pairing rule: each backstop refuses exactly when the decision greys its button.
            var moneys = new[]
            {
                new ContractMoney { FundsAdvance = 0, FundsFailure = 0 },
                new ContractMoney { FundsAdvance = 5000, FundsFailure = 30000, ReputationFailure = 10 },
                new ContractMoney { FundsAdvance = 10000, FundsFailure = 50000, ReputationFailure = 30, DateAccepted = 0, DateDeadline = 4000 },
            };
            var snaps = new[]
            {
                Snap(20000, true, 3, true), Snap(-30000, true, -5, true), Snap(1e6, false, 0.5, true),
                Snap(0, false), ClickReservationSnapshot.Inert(Now),
            };
            int cells = 0, refusals = 0;
            foreach (var m in moneys)
            {
                foreach (var s in snaps)
                {
                    var input = Input(s, m);
                    var offered = MissionControlStockAnnotation.Decide(EmptyIndex(), Now, "k", Contract.State.Offered,
                        ReservationExplanation.DefaultDateFormatter, null, false, double.PositiveInfinity, input);
                    var active = MissionControlStockAnnotation.Decide(EmptyIndex(), Now, "k", Contract.State.Active,
                        ReservationExplanation.DefaultDateFormatter, null, false, double.PositiveInfinity, input);
                    dialogCount = 0;
                    bool acceptAllowed = ContractAcceptPatch.ShouldAllowAccept("k", "K", Contract.State.Offered, false, false,
                        double.PositiveInfinity, input);
                    bool declineAllowed = ContractDeclinePatch.ShouldAllowDecline("k", "K", input);
                    bool cancelAllowed = ContractCancelPatch.ShouldAllowCancel("k", "K", input);
                    Assert.Equal(MissionControlStockAnnotation.BlocksAccept(offered), !acceptAllowed);
                    Assert.Equal(MissionControlStockAnnotation.BlocksDecline(offered), !declineAllowed);
                    Assert.Equal(MissionControlStockAnnotation.BlocksCancel(active), !cancelAllowed);
                    int refused = (acceptAllowed ? 0 : 1) + (declineAllowed ? 0 : 1) + (cancelAllowed ? 0 : 1);
                    Assert.Equal(refused, dialogCount);
                    refusals += refused;
                    cells++;
                }
            }
            Assert.Equal(15, cells);
            Assert.True(refusals > 3, "the grid must exercise refusals, not only allows");
        }

        [Fact]
        public void McAcceptBackstop_ShowsTheSameReasonAsThePanel()
        {
            var money = new ContractMoney { FundsAdvance = 5000, FundsFailure = 30000 };
            var input = Input(Snap(20000, true), money);
            var d = MissionControlStockAnnotation.Decide(EmptyIndex(), Now, "k", Contract.State.Offered,
                ReservationExplanation.DefaultDateFormatter, null, false, double.PositiveInfinity, input);
            Assert.False(ContractAcceptPatch.ShouldAllowAccept("k", "Orbit Kerbin", Contract.State.Offered, false, false,
                double.PositiveInfinity, input));
            Assert.Equal("Cannot accept \"Orbit Kerbin\"", dialogAction);
            Assert.Equal(d.Why, dialogReason);
            Assert.Contains(logLines, l => l.Contains("[ContractAcceptPatch]") && l.Contains("reserved for later costs"));
            var detail = MissionControlStockAnnotation.ComposeDetailText("stock text", d);
            Assert.Contains(MissionControlStockAnnotation.SlotDetailHeading, detail);
            Assert.Contains(d.Why, detail);
        }

        [Fact]
        public void McFilterCanAccept_ReadsTheReserveToo()
        {
            var d = MissionControlStockAnnotation.Decide(EmptyIndex(), Now, "k", Contract.State.Offered,
                ReservationExplanation.DefaultDateFormatter, null, false, double.PositiveInfinity,
                Input(Snap(20000, true), new ContractMoney { FundsAdvance = 5000, FundsFailure = 30000 }));
            Assert.False(ContractConfiguratorCompat.FilterCanAccept(true, d, false));
            Assert.True(ContractConfiguratorCompat.FilterCanAccept(true, d, true));
        }

        // ------------------------------------------------------------------ purchases

        [Fact]
        public void LayerPurchase_MarksAndBlocks_AndACommittedBlockKeepsItsReason()
        {
            var refused = new PurchaseRefusal { Refused = true, Pool = StockPool.Funds, Cost = 10000, Spendable = -30000 };
            var d = ClickReservationGate.LayerPurchase(new StockUiDecoration { Id = "f" }, refused);
            Assert.Equal(StockUiDecorationKind.ContractReserve, d.Kind);
            Assert.True(d.Marked);
            Assert.True(d.Blocked);
            Assert.Equal(ClickReservationGate.PurchaseReason(refused), d.Why);
            var committed = new StockUiDecoration
            {
                Id = "f", Kind = StockUiDecorationKind.FacilityUpgrade, Marked = true, Blocked = true, Why = "x",
            };
            Assert.Equal("x", ClickReservationGate.LayerPurchase(committed, refused).Why);
            Assert.False(ClickReservationGate.LayerPurchase(new StockUiDecoration(), default(PurchaseRefusal)).Blocked);
        }

        [Fact]
        public void FacilityUpgradeBackstop_RefusesForTheReserve_AndOpensNoScope()
        {
            L12Ledger();
            open.Add(new OpenContractPenalty { Key = "y", Funds = 50000 });
            bool allowed = FacilityUpgradeSpendPatch.BlockOrOpenScope("SpaceCenter/Administration", null,
                "SpaceCenter/Administration", true, 20000, 10000);
            Assert.False(allowed);
            Assert.Equal(1, dialogCount);
            Assert.Equal(ReservationExplanation.ClickReserve().Body, dialogReason);
            Assert.Equal("Needs 10,000 funds, 0 free.", dialogDetail);
        }

        [Fact]
        public void FacilityUpgradeBackstop_IsInertWithNoCostAhead()
        {
            Ledger.AddAction(FundsSeed(120000));
            liveFunds = 120000;
            open.Add(new OpenContractPenalty { Key = "y", Funds = 500000 });
            bool allowed = FacilityUpgradeSpendPatch.BlockOrOpenScope("SpaceCenter/Administration", null,
                "SpaceCenter/Administration", true, 120000, 10000);
            Assert.True(allowed);
            Assert.Equal(0, dialogCount);
            FacilityUpgradeCapture.EndScope("test");
        }

        [Fact]
        public void HireBackstopAndMark_ReadOneDecision()
        {
            L12Ledger();
            open.Add(new OpenContractPenalty { Key = "y", Funds = 50000 });
            KerbalHirePatch.HireCostProviderForTesting = () => 2000;
            var r = KerbalHirePatch.EvaluateHireReserveNow();
            Assert.True(r.Refused);
            var mark = ClickReservationGate.LayerPurchase(new StockUiDecoration
            {
                Screen = StockUiScreen.AstronautComplex, Tab = StockUiDecorationQuery.AstronautApplicantsTab, Id = "Bob",
            }, r);
            var row = StockUiAstronautDecoration.Decide(mark, StockUiDecorationQuery.AstronautApplicantsTab, "Pilot", true, null);
            Assert.True(row.DisableButton);
            Assert.Equal(mark.Why, row.DisabledCaption);
            Assert.Null(row.Label); // the applicant's trait line stays stock's
            Assert.True(KerbalHirePatch.TryRefuseHireReserve("Bob"));
            Assert.Equal("Cannot hire \"Bob\"", dialogAction);

            open.Clear();
            ClickReservationGate.Invalidate();
            Assert.False(KerbalHirePatch.EvaluateHireReserveNow().Refused);
            Assert.False(KerbalHirePatch.TryRefuseHireReserve("Bob"));
        }

        [Fact]
        public void PartPurchaseLayer_BlocksWithTheSharedReason()
        {
            var refused = new PurchaseRefusal { Refused = true, Pool = StockPool.Funds, Cost = 800, Spendable = 100 };
            var d = StockUiPartPurchase.LayerReserve(new StockUiPartPurchase.Decision { PartName = "p", UT = double.NaN }, refused);
            Assert.True(d.Blocked);
            Assert.Equal("Reserved for later costs and contract penalties, blocked by timeline. Needs 800 funds, 100 free.", d.Why);
            var committed = new StockUiPartPurchase.Decision { PartName = "p", Blocked = true, Why = "c", UT = 5 };
            Assert.Equal("c", StockUiPartPurchase.LayerReserve(committed, refused).Why);
        }

        [Fact]
        public void StrategySetup_IsRefusedByTheReputationReserve_InsideTheOneActivationDecision()
        {
            Ledger.AddAction(RepSeed(60));
            Ledger.AddAction(Strategy("committed", 5000, 40));
            liveRep = 60;
            StrategyReservationGate.ActiveStrategyIdsProviderForTesting = () => new List<string>();
            StrategyReservationGate.SlotLimitProviderForTesting = () => 5;
            StrategyReservationGate.StockCanBeActivatedProviderForTesting = id => true;
            StrategyReservationGate.SetupCostsProviderForTesting = id => id == "big"
                ? new double[] { 0, 0, 30 } : new double[] { 0, 0, 5 };
            ReservationText text;
            Assert.True(StrategyReservationGate.TryRefuseActivation("big", true, out text));
            Assert.Equal(ReservationExplanation.ClickReserve().Title, text.Title);
            Assert.StartsWith(ReservationExplanation.ClickReserve().Body, text.Body);
            Assert.False(StrategyReservationGate.TryRefuseActivation("small", true, out text));
            // Stock refusing first: stock's verdict stands, no Parsek refusal.
            Assert.False(StrategyReservationGate.TryRefuseActivation("big", false, out text));
        }

        [Fact]
        public void StrategySetup_IsNotRefusedWithNoCostAhead()
        {
            Ledger.AddAction(RepSeed(60));
            liveRep = 60;
            StrategyReservationGate.ActiveStrategyIdsProviderForTesting = () => new List<string>();
            StrategyReservationGate.SlotLimitProviderForTesting = () => 5;
            StrategyReservationGate.SetupCostsProviderForTesting = id => new double[] { 1e9, 1e9, 1e9 };
            open.Add(new OpenContractPenalty { Key = "y", Funds = 1e9, ReputationNominal = 1e3 });
            ReservationText text;
            Assert.False(StrategyReservationGate.TryRefuseActivation("any", true, out text));
        }

        // ------------------------------------------------------------------ launch

        [Fact]
        public void Launch_TheStockTestFailsWithTheReason_OnlyWhenRefused()
        {
            var refused = new PurchaseRefusal { Refused = true, Pool = StockPool.Funds, Cost = 12000, Spendable = 3000 };
            Assert.False(LaunchReservation.LaunchTestResult(true, refused));
            Assert.True(LaunchReservation.LaunchTestResult(true, default(PurchaseRefusal)));
            Assert.False(LaunchReservation.LaunchTestResult(false, default(PurchaseRefusal)));
            Assert.Equal("Not enough funds.\n\nReserved for later costs and contract penalties, blocked by timeline. "
                + "Needs 12,000 funds, 3,000 free.", LaunchReservation.ComposeWarningDescription("Not enough funds.", refused));
            Assert.Equal("stock", LaunchReservation.ComposeWarningDescription("stock", default(PurchaseRefusal)));
        }

        [Fact]
        public void Launch_TheButtonIsGreyedWhileRefused_AndGivenBackOnlyByParsek()
        {
            Assert.False(LaunchReservation.LaunchButtonInteractable(true, true, false).Value);
            Assert.True(LaunchReservation.LaunchButtonInteractable(true, false, true).Value);
            Assert.False(LaunchReservation.LaunchButtonInteractable(false, false, true).Value);
            Assert.Null(LaunchReservation.LaunchButtonInteractable(true, false, false));
            Assert.Null(LaunchReservation.LaunchButtonInteractable(false, true, false));
        }

        [Fact]
        public void Launch_TheMarkAndTheBackstopReadOneDecision()
        {
            L12Ledger();
            open.Add(new OpenContractPenalty { Key = "y", Funds = 50000 });
            var r = LaunchReservation.EvaluateLaunchNow(15000);
            Assert.True(r.Refused);
            Assert.False(LaunchReservation.LaunchTestResult(true, r));
            Assert.False(LaunchReservation.LaunchButtonInteractable(true, r.Refused, false).Value);
            open.Clear();
            ClickReservationGate.Invalidate();
            var ok = LaunchReservation.EvaluateLaunchNow(15000);
            Assert.False(ok.Refused);
            Assert.True(LaunchReservation.LaunchTestResult(true, ok));
        }

        // ------------------------------------------------------------------ tooltip

        [Fact]
        public void Tooltip_ReservedIncludesTheOpenPenalties_TotalUnchanged()
        {
            string withOpen = CurrencyReservationOverlay.BuildTooltipFromLedger(120000, 20000, 20000, 20000, "N0", 0, 50000);
            Assert.Equal("Total: 120,000\nReserved: 150,000", withOpen);
            string without = CurrencyReservationOverlay.BuildTooltipFromLedger(120000, 20000, 20000, 20000, "N0");
            Assert.Equal("Total: 120,000\nReserved: 100,000", without);
            Assert.Equal(without, CurrencyReservationOverlay.BuildTooltipFromLedger(120000, 20000, 20000, 20000, "N0", 0, 0));
        }
    }
}
