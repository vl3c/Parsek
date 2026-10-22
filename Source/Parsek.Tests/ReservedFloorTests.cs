using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Rule 4 of the committed-value rule (plan section 2.3, phase 3; design 3.13): a loss with
    /// a committed cost captured ahead of it stops at the reserved floor, the rest is a debt
    /// charged from the room above max(floor, 0). "Captured ahead" is the coordinator's
    /// definition (2026-10-22): a cost row with a later UT and a lower
    /// <see cref="GameAction.CaptureSequence"/>, so a career that never rewinds stays stock.
    ///
    /// <para>The walk cells run the real <see cref="RecalculationEngine.Recalculate"/> over
    /// registered modules; the patch cells then run the stock pool reference the way
    /// <c>KspStatePatcher.Patch*</c> does (as <see cref="StockPoolReferenceTests"/>): the
    /// reference steps a row by its effect BEFORE the settle and any debt charge.</para>
    /// </summary>
    [Collection("Sequential")]
    public class ReservedFloorTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<GameAction> ledger = new List<GameAction>();
        private readonly FundsModule funds = new FundsModule();
        private readonly ScienceModule science = new ScienceModule();
        private readonly ReputationModule reputation = new ReputationModule();
        private readonly ContractsModule contracts = new ContractsModule();
        private StockPoolMeasurement lastMeasurement;

        public ReservedFloorTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = () => false;
            StockPoolReference.ResetForTesting();
            Ledger.ResetForTesting();
            GameStateStore.ResetForTesting();
            CaptureSequence.ResetForTesting();
            RecalculationEngine.ClearModules();
            RecalculationEngine.RegisterModule(science, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(contracts, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(reputation, RecalculationEngine.ModuleTier.SecondTier);
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = null;
            StockPoolReference.ResetForTesting();
            Ledger.ResetForTesting();
            GameStateStore.ResetForTesting();
            CaptureSequence.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Rows
        // ------------------------------------------------------------------

        private static GameAction S(GameAction a, long seq)
        {
            a.CaptureSequence = seq;
            return a;
        }

        private static GameAction FundsSeed(double funds, long seq = 1) => S(new GameAction
        {
            ActionId = "seedF", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = (float)funds,
        }, seq);

        private static GameAction RepSeed(double rep, long seq = 1) => S(new GameAction
        {
            ActionId = "seedR", UT = 0, Type = GameActionType.ReputationInitial, InitialReputation = (float)rep,
        }, seq);

        private static GameAction Earn(string id, double ut, float amount, long seq) => S(new GameAction
        {
            ActionId = id, UT = ut, Type = GameActionType.FundsEarning, FundsAwarded = amount,
        }, seq);

        private static GameAction Spend(string id, double ut, float amount, long seq) => S(new GameAction
        {
            ActionId = id, UT = ut, Type = GameActionType.FundsSpending, FundsSpent = amount,
        }, seq);

        private static GameAction Upgrade(string id, double ut, float cost, long seq) => S(new GameAction
        {
            ActionId = id, UT = ut, Type = GameActionType.FacilityUpgrade, FacilityCost = cost,
        }, seq);

        private static GameAction Fail(string id, double ut, float penalty, long seq) => S(new GameAction
        {
            ActionId = id, UT = ut, Type = GameActionType.ContractFail, ContractId = id, FundsPenalty = penalty,
        }, seq);

        private static GameAction Death(string id, double ut, float applied, long seq) => S(new GameAction
        {
            ActionId = id, UT = ut, Type = GameActionType.ReputationPenalty,
            RepPenaltySource = ReputationPenaltySource.KerbalDeath, NominalPenalty = applied,
        }, seq);

        private static GameAction RepGain(string id, double ut, float nominal, long seq) => S(new GameAction
        {
            ActionId = id, UT = ut, Type = GameActionType.ReputationEarning, NominalRep = nominal,
        }, seq);

        private static GameAction Strategy(string id, double ut, float repCost, long seq) => S(new GameAction
        {
            ActionId = id, UT = ut, Type = GameActionType.StrategyActivate, StrategyId = id,
            SetupReputationCost = repCost,
        }, seq);

        // ------------------------------------------------------------------
        // Patch harness (as StockPoolReferenceTests)
        // ------------------------------------------------------------------

        private HashSet<string> IdsInLedger() => new HashSet<string>(
            ledger.Where(a => a != null && !string.IsNullOrEmpty(a.ActionId)).Select(a => a.ActionId),
            StringComparer.Ordinal);

        private void File(StockPoolMeasurement m, StockPool pool, double now)
        {
            if (m.Adjustment == 0.0)
                return;
            GameAction row = StockPoolReference.BuildAdjustmentRow(pool, m.Adjustment, now);
            ledger.Add(row);
            m.AdjustmentRow = row;
        }

        private double PatchFunds(double now, double live)
        {
            RecalculationEngine.Recalculate(new List<GameAction>(ledger), now);
            var state = StockPoolReference.Funds;
            List<StockPoolRow> rows = StockPoolReference.BuildRows(StockPool.Funds, ledger, funds.GetRowEffects());
            double min = funds.GetProjectionMinBalance();
            StockPoolMeasurement m = StockPoolReference.Measure(
                state, rows, now, live, 0.0, false, 0.01, min, IdsInLedger(), clampAtZero: true);
            File(m, StockPool.Funds, now);
            double write = m.GapAdopted
                ? live
                : StockPoolReference.ResolveCurrencyWriteTarget(
                    min, funds.GetRunningBalance(), live, false, m.Adjustment);
            StockPoolReference.Commit(state, rows, m, write, 0.0);
            lastMeasurement = m;
            return write;
        }

        private double PatchReputation(double now, double live)
        {
            RecalculationEngine.Recalculate(new List<GameAction>(ledger), now);
            var state = StockPoolReference.Reputation;
            List<StockPoolRow> rows = StockPoolReference.BuildRows(
                StockPool.Reputation, ledger, reputation.GetRowEffects());
            double running = reputation.GetRunningRep();
            StockPoolMeasurement m = StockPoolReference.Measure(
                state, rows, now, live, 0.0, false, 0.01, running, IdsInLedger());
            File(m, StockPool.Reputation, now);
            double write = m.GapAdopted
                ? live
                : StockPoolReference.ResolveReputationWriteTarget(running, m.Adjustment);
            StockPoolReference.Commit(state, rows, m, write, 0.0);
            lastMeasurement = m;
            return write;
        }

        private double Adjusted(StockPool pool) => StockPoolReference.SumAdjustments(pool, ledger);

        private bool AnySettle() => logLines.Any(l => l.Contains("Floor settle:"));

        // ------------------------------------------------------------------
        // ReservedFloorPlan (pure)
        // ------------------------------------------------------------------

        private static ReservedFloorRow Row(GameAction a, double delta, bool cost = false,
            bool floorLimited = false, bool synthetic = false) => new ReservedFloorRow
        {
            Action = a, Delta = delta, IsCost = cost, FloorLimited = floorLimited, Synthetic = synthetic,
        };

        [Fact]
        public void Plan_ACostCapturedBeforeTheLoss_FloorsAtWhatItNeeds()
        {
            var loss = Fail("f", 20, 50f, seq: 5);
            var gain = Earn("g", 30, 20f, seq: 6);
            var cost = Upgrade("u", 50, 100f, seq: 3);
            var plan = ReservedFloorPlan.Build(StockPool.Funds, new[]
            {
                Row(loss, -50), Row(gain, 20), Row(cost, -100, cost: true),
            });

            double floor;
            Assert.True(plan.TryGetFloor(loss, loss.CaptureSequence, out floor));
            // need = -min(+20, +20 - 100) = 80
            Assert.Equal(80.0, floor, 9);
        }

        [Fact]
        public void Plan_ACostCapturedAfterTheLoss_GivesNoFloor_TheNoRewindCareer()
        {
            var loss = Fail("f", 20, 50f, seq: 5);
            var cost = Upgrade("u", 50, 100f, seq: 6);
            var plan = ReservedFloorPlan.Build(StockPool.Funds, new[] { Row(loss, -50), Row(cost, -100, cost: true) });

            double floor;
            Assert.False(plan.TryGetFloor(loss, loss.CaptureSequence, out floor));
        }

        [Fact]
        public void Plan_RowsWithoutASequence_AreAheadOfNothing_AndNothingIsAheadOfThem()
        {
            var loss = Fail("f", 20, 50f, seq: 0);
            var cost = Upgrade("u", 50, 100f, seq: 3);
            var plan = ReservedFloorPlan.Build(StockPool.Funds, new[] { Row(loss, -50), Row(cost, -100, cost: true) });
            double floor;
            Assert.False(plan.TryGetFloor(loss, loss.CaptureSequence, out floor));

            var loss2 = Fail("f2", 20, 50f, seq: 9);
            var unsequencedCost = Upgrade("u2", 50, 100f, seq: 0);
            var plan2 = ReservedFloorPlan.Build(StockPool.Funds, new[] { Row(loss2, -50), Row(unsequencedCost, -100, cost: true) });
            Assert.False(plan2.TryGetFloor(loss2, loss2.CaptureSequence, out floor));
        }

        [Fact]
        public void Plan_TheWindowEndsAtTheLastCost_AndLeavesOutInjectedAndFloorLimitedRows()
        {
            var loss = Fail("f", 20, 50f, seq: 9);
            var synthetic = Fail("syn", 30, 1000f, seq: 0);
            var death = Death("d", 35, 1000f, seq: 1);
            var gain = Earn("g", 40, 20f, seq: 2);
            var cost = Upgrade("u", 50, 100f, seq: 3);
            var afterLastCost = Fail("late", 60, 500f, seq: 4);
            var plan = ReservedFloorPlan.Build(StockPool.Funds, new[]
            {
                Row(loss, -50), Row(synthetic, -1000, synthetic: true), Row(death, -1000, floorLimited: true),
                Row(gain, 20), Row(cost, -100, cost: true), Row(afterLastCost, -500),
            });
            double floor;
            Assert.True(plan.TryGetFloor(loss, loss.CaptureSequence, out floor));
            Assert.Equal(80.0, floor, 9);
        }

        [Fact]
        public void Plan_ACostAtTheSameUT_IsNotAhead_AndANeedCoveredByLaterGainsIsZero()
        {
            var loss = Fail("f", 20, 50f, seq: 9);
            var sameUt = Upgrade("u", 20, 100f, seq: 3);
            var plan = ReservedFloorPlan.Build(StockPool.Funds, new[] { Row(loss, -50), Row(sameUt, -100, cost: true) });
            double floor;
            Assert.False(plan.TryGetFloor(loss, 9, out floor));

            var gain = Earn("g", 30, 500f, seq: 2);
            var later = Upgrade("u2", 50, 100f, seq: 3);
            var plan2 = ReservedFloorPlan.Build(StockPool.Funds, new[] { Row(loss, -50), Row(gain, 500), Row(later, -100, cost: true) });
            Assert.True(plan2.TryGetFloor(loss, 9, out floor));
            Assert.Equal(0.0, floor, 9);
        }

        [Fact]
        public void Plan_RepairsAreCostRows_AnIntactRepairIsNot()
        {
            var repair = new GameAction
            {
                ActionId = "r", UT = 10, Type = GameActionType.FacilityRepair, FacilityCost = 5000f, Effective = true,
            };
            double delta; bool isCost, floorLimited;
            funds.ClassifyFloorRow(repair, out delta, out isCost, out floorLimited);
            Assert.True(isCost);
            Assert.Equal(-5000.0, delta, 6);

            repair.Effective = false;
            funds.ClassifyFloorRow(repair, out delta, out isCost, out floorLimited);
            Assert.False(isCost);

            var death = Death("d", 10, 5f, 1);
            death.EffectiveRep = -5f;
            reputation.ClassifyFloorRow(death, out delta, out isCost, out floorLimited);
            Assert.True(floorLimited);
            Assert.False(isCost);
            var strategy = Strategy("s", 10, 40f, 2);
            strategy.EffectiveRep = -40f;
            reputation.ClassifyFloorRow(strategy, out delta, out isCost, out floorLimited);
            Assert.True(isCost);
        }

        [Fact]
        public void PreCheck_OnlyACostCapturedAheadOfALaterUTLoss()
        {
            // Normal play: every row captured in UT order.
            Assert.False(ReservedFloorPlan.HasCostCapturedAheadOfALaterLoss(new List<GameAction>
            {
                Fail("f", 10, 1f, 2), Earn("e", 20, 1f, 3), Spend("s", 30, 1f, 4),
            }));
            // The rewind shape: the committed spend was captured first.
            Assert.True(ReservedFloorPlan.HasCostCapturedAheadOfALaterLoss(new List<GameAction>
            {
                Spend("s", 30, 1f, 2), Fail("f", 10, 1f, 3), Earn("e", 20, 1f, 4),
            }));
            // An older save: no sequences at all.
            Assert.False(ReservedFloorPlan.HasCostCapturedAheadOfALaterLoss(new List<GameAction>
            {
                Spend("s", 30, 1f, 0), Fail("f", 10, 1f, 0),
            }));
        }

        [Theory]
        [InlineData(-50.0, -50.0, 80.0, 50.0)]   // the whole loss below the floor
        [InlineData(70.0, -50.0, 80.0, 10.0)]    // the part below the floor
        [InlineData(90.0, -50.0, 80.0, 0.0)]     // stays above
        [InlineData(10.0, 20.0, 80.0, 0.0)]      // a gain is never settled
        public void Settle_TakesBackThePartBelowTheFloor(double after, double delta, double floor, double expected)
        {
            Assert.Equal(expected, ReservedFloorSettle.SettleBelowFloor(after, delta, floor), 9);
        }

        [Theory]
        [InlineData(110.0, 30.0, true, 100.0, 10.0)]   // room above the floor
        [InlineData(110.0, 30.0, true, -5.0, 30.0)]    // above max(floor, 0)
        [InlineData(10.0, 30.0, false, 0.0, 10.0)]     // no floor: above 0
        [InlineData(-10.0, 30.0, false, 0.0, 0.0)]     // below 0: nothing
        [InlineData(500.0, 0.0, false, 0.0, 0.0)]      // no debt
        public void Charge_IsFromTheRoomAboveMaxOfFloorAndZero(
            double running, double debt, bool hasFloor, double floor, double expected)
        {
            Assert.Equal(expected, ReservedFloorSettle.ChargeFromRoom(running, debt, hasFloor, floor), 9);
        }

        // ------------------------------------------------------------------
        // The counterexample (coordinator, 2026-10-22): funds
        // ------------------------------------------------------------------

        [Fact]
        public void NoRewind_FailPastZero_ThenEarning_ThenAPurchaseCapturedLast_IsExactlyStock()
        {
            ledger.Add(FundsSeed(1000));
            Assert.Equal(1000.0, PatchFunds(0, 1000), 6);
            ledger.Add(Fail("f", 10, 3000f, seq: 2));
            Assert.Equal(0.0, PatchFunds(11, 0), 6);
            ledger.Add(Earn("e", 20, 500f, seq: 3));
            Assert.Equal(500.0, PatchFunds(21, 500), 6);
            ledger.Add(Spend("s", 30, 400f, seq: 4));
            Assert.Equal(100.0, PatchFunds(31, 100), 6);

            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(0.0, Adjusted(StockPool.Funds), 6);
            Assert.Equal(0.0, funds.GetOutstandingDebt(), 6);
            Assert.Equal(100.0, funds.GetRunningBalance(), 6);
            Assert.False(AnySettle());
            Assert.DoesNotContain(logLines, l => l.Contains("Reserved floor plans built"));

            // The production shape too: a full walk with no future rows.
            RecalculationEngine.Recalculate(new List<GameAction>(ledger));
            Assert.Equal(100.0, funds.GetRunningBalance(), 6);
            Assert.Equal(100.0, funds.GetAvailableFunds(), 6);
            Assert.Equal(2000.0, funds.GetForgivenBelowZero(), 6);
        }

        [Fact]
        public void RewindShape_ThePurchaseCapturedBeforeTheFail_FloorsAndCarriesADebt()
        {
            ledger.Add(FundsSeed(1000));
            Assert.Equal(1000.0, PatchFunds(0, 1000), 6);
            // The committed future purchase (captured before the rewind): the pool holds 600.
            ledger.Add(Spend("s", 30, 400f, seq: 2));
            Assert.Equal(600.0, PatchFunds(1, 1000), 6);

            // The post-rewind fail: stock 600 -> 0; the walk holds the 400 the purchase needs.
            ledger.Add(Fail("f", 10, 3000f, seq: 3));
            Assert.Equal(0.0, PatchFunds(11, 0), 6);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(400.0, funds.GetRunningBalance(), 6);
            Assert.Equal(2400.0, funds.GetOutstandingDebt(), 6);

            // The earning: 100 of it is room above what the purchase needs, charged to the debt.
            ledger.Add(Earn("e", 20, 500f, seq: 4));
            Assert.Equal(0.0, PatchFunds(21, 500), 6);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(1900.0, funds.GetOutstandingDebt(), 6);

            // The purchase crosses the clock: paid, the debt stays.
            Assert.Equal(0.0, PatchFunds(31, 0), 6);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(1900.0, funds.GetOutstandingDebt(), 6);

            // Later gains repay it before the pool rises.
            ledger.Add(Earn("e2", 40, 1000f, seq: 5));
            Assert.Equal(0.0, PatchFunds(41, 1000), 6);
            Assert.Equal(900.0, funds.GetOutstandingDebt(), 6);
            ledger.Add(Earn("e3", 50, 1500f, seq: 6));
            Assert.Equal(600.0, PatchFunds(51, 1500), 6);
            Assert.Equal(0.0, funds.GetOutstandingDebt(), 6);
            Assert.Equal(0.0, Adjusted(StockPool.Funds), 6);
            Assert.True(AnySettle());
            Assert.Contains(logLines, l => l.Contains("Reserved floor plans built"));
        }

        // ------------------------------------------------------------------
        // The coordinator's worked check (L1.2, funds)
        // ------------------------------------------------------------------

        [Fact]
        public void L12_FailBelowTheFloor_IsADebt_ALaterEarningRepaysIt_NoAdjustmentRows()
        {
            // Running 120k, a committed 100k upgrade at UT 500 (day 50): the pool holds 20k.
            ledger.Add(FundsSeed(120000));
            Assert.Equal(120000.0, PatchFunds(0, 120000), 6);
            ledger.Add(Upgrade("up", 500, 100000f, seq: 2));
            Assert.Equal(20000.0, PatchFunds(100, 120000), 6);

            // A post-rewind contract fail of 50k at UT 200 (day 20): stock 20k -> 0.
            ledger.Add(Fail("f", 200, 50000f, seq: 3));
            Assert.Equal(0.0, PatchFunds(210, 0), 6);
            Assert.Equal(0.0, lastMeasurement.Adjustment, 6);
            Assert.Equal(100000.0, funds.GetRunningBalance(), 6);
            Assert.Equal(30000.0, funds.GetOutstandingDebt(), 6);
            Assert.Contains(logLines, l => l.Contains("[Funds]") && l.Contains("Floor settle: ContractFail id=f")
                && l.Contains("heldBack=30000") && l.Contains("debt=30000"));
            Assert.Contains(logLines, l => l.Contains("Floor walk: pool=Funds") && l.Contains("debt=30000"));

            // A later 10k earning: stock's pool 10k, no leak, the target stays 0 (the write
            // lowers the pool to 0: the debt being repaid).
            ledger.Add(Earn("e1", 300, 10000f, seq: 4));
            Assert.Equal(0.0, PatchFunds(310, 10000), 6);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(0.0, Adjusted(StockPool.Funds), 6);
            Assert.Equal(20000.0, funds.GetOutstandingDebt(), 6);
            // The reference stepped the earning by what stock applied, before the charge.
            Assert.Equal(10000.0, StockPoolReference.Funds.Counted["e1"].Effect, 6);

            // The upgrade is paid at day 50.
            Assert.Equal(0.0, PatchFunds(510, 0), 6);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(0.0, funds.GetRunningBalance(), 6);
            Assert.Equal(20000.0, funds.GetOutstandingDebt(), 6);

            // After 30k of gains in all the debt is 0, and later gains raise the pool.
            ledger.Add(Earn("e2", 600, 20000f, seq: 5));
            Assert.Equal(0.0, PatchFunds(610, 20000), 6);
            Assert.Equal(0.0, funds.GetOutstandingDebt(), 6);
            ledger.Add(Earn("e3", 700, 5000f, seq: 6));
            Assert.Equal(5000.0, PatchFunds(710, 5000), 6);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(0.0, Adjusted(StockPool.Funds), 6);
        }

        [Fact]
        public void L12_TheFailAndTheEarningInOneMeasuredPatch_GiveTheSameNumbers()
        {
            ledger.Add(FundsSeed(120000));
            Assert.Equal(120000.0, PatchFunds(0, 120000), 6);
            ledger.Add(Upgrade("up", 500, 100000f, seq: 2));
            Assert.Equal(20000.0, PatchFunds(100, 120000), 6);
            ledger.Add(Fail("f", 200, 50000f, seq: 3));
            ledger.Add(Earn("e1", 300, 10000f, seq: 4));
            // Stock: 20k -> 0 (clamp forgives 30k) -> 10k.
            Assert.Equal(0.0, PatchFunds(310, 10000), 6);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(30000.0, lastMeasurement.ClampForgiven, 6);
            Assert.Equal(0.0, Adjusted(StockPool.Funds), 6);
            Assert.Equal(20000.0, funds.GetOutstandingDebt(), 6);
        }

        [Fact]
        public void L12_ThroughRecalculateAlone_TheWalkHoldsTheFloor()
        {
            var actions = new List<GameAction>
            {
                FundsSeed(120000), Upgrade("up", 500, 100000f, 2), Fail("f", 200, 50000f, 3),
            };
            RecalculationEngine.Recalculate(actions, 250);
            Assert.Equal(100000.0, funds.GetRunningBalance(), 6);
            Assert.Equal(30000.0, funds.GetOutstandingDebt(), 6);
            Assert.Equal(0.0, funds.GetAvailableFunds(), 6);

            // Past the upgrade (no cutoff, the production shape once nothing lies ahead).
            actions.Add(Earn("e", 600, 10000f, 4));
            RecalculationEngine.Recalculate(actions);
            Assert.Equal(0.0, funds.GetRunningBalance(), 6);
            Assert.Equal(20000.0, funds.GetOutstandingDebt(), 6);
            Assert.Equal(0.0, funds.GetAvailableFunds(), 6);
        }

        [Fact]
        public void APreRewindLoss_AndACostCapturedAfterItInTheOriginalTimeline_GetNoFloor()
        {
            // The original timeline: a fail stock clamped at 0, then a purchase captured
            // later. Both committed; a rewind before them keeps them as they were.
            var actions = new List<GameAction>
            {
                FundsSeed(1000), Fail("f", 10, 3000f, 2), Earn("e", 20, 500f, 3), Spend("s", 30, 400f, 4),
            };
            RecalculationEngine.Recalculate(actions, 5);
            RecalculationEngine.Recalculate(actions);
            Assert.Equal(100.0, funds.GetRunningBalance(), 6);
            Assert.Equal(0.0, funds.GetOutstandingDebt(), 6);
            Assert.False(AnySettle());
        }

        // ------------------------------------------------------------------
        // Cost rows, an overcommitted timeline, the debt bound
        // ------------------------------------------------------------------

        [Fact]
        public void CostRows_AreNeverSettled_AnOvercommittedTimelineRunsBelowZero_RoomIsAboveZero()
        {
            // Seed 50k, a committed 100k upgrade (already overcommitted), a post-rewind 10k fail.
            var actions = new List<GameAction>
            {
                FundsSeed(50000), Upgrade("up", 500, 100000f, 2), Fail("f", 200, 10000f, 3),
            };
            RecalculationEngine.Recalculate(actions);
            // The fail stops at the floor (100k needed, 40k left: 10k held back); the upgrade
            // is paid in full.
            Assert.Equal(-50000.0, funds.GetRunningBalance(), 6);
            Assert.Equal(10000.0, funds.GetOutstandingDebt(), 6);
            Assert.Equal(0.0, funds.GetAvailableFunds(), 6);
            Assert.DoesNotContain(logLines, l => l.Contains("Floor settle: FacilityUpgrade"));

            // Gains first fill the overcommitted shortfall, then repay the debt above 0.
            actions.Add(Earn("e1", 600, 55000f, 4));
            RecalculationEngine.Recalculate(actions);
            Assert.Equal(0.0, funds.GetRunningBalance(), 6);
            Assert.Equal(5000.0, funds.GetOutstandingDebt(), 6);
            actions.Add(Earn("e2", 700, 6000f, 5));
            RecalculationEngine.Recalculate(actions);
            Assert.Equal(1000.0, funds.GetRunningBalance(), 6);
            Assert.Equal(0.0, funds.GetOutstandingDebt(), 6);
        }

        [Fact]
        public void TheDebt_IsChargedAboveWhatCostsCapturedBeforeIt_Need_EvenOnAnOlderCommittedGain()
        {
            // A committed 20k earning at UT 400 captured before the committed 100k upgrade at
            // UT 500, and a post-rewind 50k fail at UT 200.
            var actions = new List<GameAction>
            {
                FundsSeed(120000), Earn("e", 400, 20000f, 2), Upgrade("up", 500, 100000f, 3),
                Fail("f", 200, 50000f, 4),
            };
            RecalculationEngine.Recalculate(actions);
            // need at the fail = 80k: 70k -> 80k, debt 10k; the committed earning cannot repay
            // it (the upgrade still needs all 100k), so the upgrade is paid and the debt stays.
            Assert.Equal(0.0, funds.GetRunningBalance(), 6);
            Assert.Equal(10000.0, funds.GetOutstandingDebt(), 6);
        }

        // ------------------------------------------------------------------
        // The single planned walk
        // ------------------------------------------------------------------

        [Fact]
        public void ProbeModuleAtCutoff_WalkBalancesOnClones_AndRecalculate_Agree()
        {
            var actions = new List<GameAction>
            {
                FundsSeed(120000), RepSeed(60), Upgrade("up", 500, 100000f, 2), Strategy("st", 500, 40f, 3),
                Fail("f", 200, 50000f, 4), Death("d", 210, 30f, 5), Earn("e", 300, 10000f, 6),
            };

            RecalculationEngine.Recalculate(actions, 310);
            double running = funds.GetRunningBalance();
            double available = funds.GetAvailableFunds();
            double debt = funds.GetOutstandingDebt();
            double rep = reputation.GetRunningRep();

            var probe = RecalculationEngine.ProbeModuleAtCutoff<FundsModule>(actions, 310);
            Assert.Equal(running, probe.GetRunningBalance(), 6);
            Assert.Equal(available, probe.GetAvailableFunds(), 6);
            Assert.Equal(debt, probe.GetOutstandingDebt(), 6);
            var repProbe = RecalculationEngine.ProbeModuleAtCutoff<ReputationModule>(actions, 310);
            Assert.Equal(rep, repProbe.GetRunningRep(), 4);
            Assert.Equal(100000.0, running, 6);
            Assert.Equal(20000.0, debt, 6);

            RecalculationEngine.Recalculate(actions);
            double fullFunds = funds.GetRunningBalance();
            double fullRep = reputation.GetRunningRep();
            var balances = RecalculationEngine.WalkBalancesOnClones(actions);
            var last = balances.Samples[balances.Samples.Count - 1];
            Assert.Equal(fullFunds, last.Funds, 6);
            Assert.Equal(fullRep, last.Reputation, 4);
            // The probe walks change no registered module.
            Assert.Equal(fullFunds, funds.GetRunningBalance(), 6);
        }

        // ------------------------------------------------------------------
        // Reputation (L1.4 and the counterexample's reputation form)
        // ------------------------------------------------------------------

        [Fact]
        public void L14_DeathsBeforeACommittedStrategy_StopAtTheFloor_TheDebtIsRepaidByGains()
        {
            ledger.Add(RepSeed(60));
            Assert.Equal(60.0, PatchReputation(0, 60), 4);
            ledger.Add(Strategy("st", 500, 40f, seq: 2));
            Assert.Equal(60.0, PatchReputation(100, 60), 4);

            // Deaths of 30 at day 20 (stock: 60 -> 30, pre-curved).
            ledger.Add(Death("d", 200, 30f, seq: 3));
            double write = PatchReputation(210, 30);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(0.0, Adjusted(StockPool.Reputation), 6);
            // Reputation holds at about 40 with a debt of about 10. The floor comes from the
            // debt-free walk's curve (the strategy's cost at 30), so it is a few tenths above
            // the 40 nominal: the documented curve shortfall.
            Assert.InRange(write, 40.0, 41.0);
            Assert.InRange(reputation.GetOutstandingDebt(), 10.0, 11.0);

            // The strategy is paid at day 50: reputation about 0 (it may dip that shortfall below).
            write = PatchReputation(510, write);
            Assert.InRange(write, -1.0, 0.5);
            double debt = reputation.GetOutstandingDebt();
            Assert.InRange(debt, 10.0, 11.0);

            // The next gains repay the debt before reputation rises.
            ledger.Add(RepGain("g", 600, 10f, seq: 4));
            double live = write + ReputationModule.ApplyReputationCurve(10f, (float)write).actualDelta;
            write = PatchReputation(610, live);
            Assert.False(lastMeasurement.LeakKept);
            Assert.InRange(write, -1.0, 0.5);
            Assert.InRange(reputation.GetOutstandingDebt(), 0.0, 1.5);
        }

        [Fact]
        public void Reputation_NoRewind_DeathBelowZeroThenALaterStrategy_IsExactlyStock()
        {
            var sequenced = new List<GameAction>
            {
                RepSeed(10), Death("d", 10, 30f, 2), RepGain("g", 20, 50f, 3), Strategy("st", 30, 20f, 4),
            };
            RecalculationEngine.Recalculate(sequenced);
            float withSequences = reputation.GetRunningRep();
            Assert.Equal(0.0, reputation.GetOutstandingDebt(), 6);

            foreach (var a in sequenced) a.CaptureSequence = 0;
            RecalculationEngine.Recalculate(sequenced);
            Assert.Equal(reputation.GetRunningRep(), withSequences);
            Assert.False(AnySettle());
        }

        [Fact]
        public void Reputation_RewindShape_TheStrategyCapturedBeforeTheDeath_FloorsAndDebts()
        {
            var actions = new List<GameAction>
            {
                RepSeed(50), Strategy("st", 30, 20f, 2), Death("d", 10, 40f, 3), RepGain("g", 40, 50f, 4),
            };
            RecalculationEngine.Recalculate(actions, 15);
            // need at the death: the strategy's ~20 with nothing in between -> 50 - 40 is held at
            // ~20 and the ~10 below it is a debt.
            Assert.InRange(reputation.GetRunningRep(), 19.0, 21.5);
            Assert.InRange(reputation.GetOutstandingDebt(), 9.0, 11.0);
            Assert.True(AnySettle());

            RecalculationEngine.Recalculate(actions);
            // The strategy is paid (rep ~0), then the gain repays the debt before rep rises.
            Assert.Equal(0.0, reputation.GetOutstandingDebt(), 3);
            Assert.InRange(reputation.GetRunningRep(), 37.0, 42.0);
        }

        // ------------------------------------------------------------------
        // Identity: a career that never rewinds walks exactly as phase 2
        // ------------------------------------------------------------------

        [Fact]
        public void NoRewindCareers_WithCaptureOrderEqualToUTOrder_WalkExactlyAsWithoutSequences()
        {
            var rng = new Random(4242);
            for (int trial = 0; trial < 60; trial++)
            {
                var rows = new List<GameAction> { FundsSeed(rng.Next(0, 5000)), RepSeed(rng.Next(-50, 100)) };
                double ut = 0;
                for (int i = 0; i < 12; i++)
                {
                    ut += rng.Next(1, 20);
                    string id = "r" + trial + "_" + i;
                    long seq = i + 2;
                    switch (rng.Next(6))
                    {
                        case 0: rows.Add(Fail(id, ut, rng.Next(100, 6000), seq)); break;
                        case 1: rows.Add(Earn(id, ut, rng.Next(100, 3000), seq)); break;
                        case 2: rows.Add(Spend(id, ut, rng.Next(100, 2000), seq)); break;
                        case 3: rows.Add(Death(id, ut, rng.Next(1, 40), seq)); break;
                        case 4: rows.Add(RepGain(id, ut, rng.Next(1, 40), seq)); break;
                        default: rows.Add(Strategy(id, ut, rng.Next(1, 30), seq)); break;
                    }
                }

                RecalculationEngine.Recalculate(rows);
                double f = funds.GetRunningBalance(), fa = funds.GetAvailableFunds();
                float r = reputation.GetRunningRep();
                var effects = funds.GetRowEffects().ToDictionary(kv => kv.Key, kv => kv.Value);
                Assert.Equal(0.0, funds.GetOutstandingDebt(), 9);
                Assert.Equal(0.0, reputation.GetOutstandingDebt(), 9);

                foreach (var a in rows) a.CaptureSequence = 0;
                RecalculationEngine.Recalculate(rows);
                Assert.Equal(f, funds.GetRunningBalance(), 9);
                Assert.Equal(fa, funds.GetAvailableFunds(), 9);
                Assert.Equal(r, reputation.GetRunningRep());
                foreach (var kv in funds.GetRowEffects())
                    Assert.Equal(kv.Value, effects[kv.Key], 9);
            }
            Assert.False(AnySettle());
        }

        // ------------------------------------------------------------------
        // The capture sequence
        // ------------------------------------------------------------------

        [Fact]
        public void CaptureSequence_EventsAreStampedAtCapture_RowsCarryTheirEventsValue()
        {
            var e1 = new GameStateEvent { ut = 10, eventType = GameStateEventType.ContractFailed, key = "c1" };
            GameStateStore.AddEvent(ref e1);
            var e2 = new GameStateEvent { ut = 5, eventType = GameStateEventType.ContractFailed, key = "c2" };
            GameStateStore.AddEvent(ref e2);
            Assert.True(e1.captureSequence > 0);
            Assert.True(e2.captureSequence > e1.captureSequence);

            GameAction row = GameStateEventConverter.ConvertEvent(GameStateStore.Events[0], null);
            Assert.NotNull(row);
            Assert.Equal(e1.captureSequence, row.CaptureSequence);

            // A row written directly is captured at write; a converted row keeps its value.
            var direct = new GameAction { UT = 1, Type = GameActionType.FundsSpending, FundsSpent = 1f };
            Ledger.AddAction(direct, LedgerRowWriter.StockApplied);
            Assert.True(direct.CaptureSequence > e2.captureSequence);
            Ledger.AddAction(row, LedgerRowWriter.StockApplied);
            Assert.Equal(e1.captureSequence, row.CaptureSequence);
        }

        [Fact]
        public void CaptureSequence_NeverReissuesALowerNumber_AndABatchIsStampedInUTOrder()
        {
            Ledger.AddAction(S(new GameAction { UT = 900, Type = GameActionType.FacilityUpgrade, FacilityCost = 1f }, 500),
                LedgerRowWriter.StockApplied);
            Assert.Equal(501, CaptureSequence.Next());

            var late = new GameAction { UT = 300, Type = GameActionType.FundsEarning, FundsAwarded = 1f };
            var early = new GameAction { UT = 100, Type = GameActionType.FundsEarning, FundsAwarded = 1f };
            Ledger.AddActions(new[] { late, early }, LedgerRowWriter.StockApplied);
            Assert.True(early.CaptureSequence > 501);
            Assert.True(late.CaptureSequence > early.CaptureSequence);
        }

        [Fact]
        public void CaptureSequence_RoundTripsOnTheRowAndTheEvent_AbsentIsNone()
        {
            var a = new GameAction { UT = 1, Type = GameActionType.FundsEarning, FundsAwarded = 2f, CaptureSequence = 77 };
            var parent = new ConfigNode("LEDGER");
            a.SerializeInto(parent);
            ConfigNode node = parent.GetNode("GAME_ACTION");
            Assert.Equal("77", node.GetValue("cseq"));
            Assert.Equal(77, GameAction.DeserializeFrom(node).CaptureSequence);
            node.RemoveValue("cseq");
            Assert.Equal(0, GameAction.DeserializeFrom(node).CaptureSequence);

            var e = new GameStateEvent { ut = 3, eventType = GameStateEventType.ContractFailed, captureSequence = 12 };
            var en = new ConfigNode("GAME_STATE_EVENT");
            e.SerializeInto(en);
            Assert.Equal("12", en.GetValue("cseq"));
            Assert.Equal(12, GameStateEvent.DeserializeFrom(en).captureSequence);
            var unstamped = new GameStateEvent { ut = 3, eventType = GameStateEventType.ContractFailed };
            var un = new ConfigNode("GAME_STATE_EVENT");
            unstamped.SerializeInto(un);
            Assert.Null(un.GetValue("cseq"));
        }

        [Fact]
        public void KerbalDeathPenalty_CarriesTheVesselLossEventsCaptureOrder()
        {
            var loss = new GameStateEvent
            {
                ut = 50, eventType = GameStateEventType.ReputationChanged,
                key = KerbalDeathRepPenalty.VesselLossEventKey, valueBefore = 60, valueAfter = 50,
                recordingId = "rec", captureSequence = 42,
            };
            var decision = KerbalDeathRepPenalty.Decide(
                new[] { KerbalEndState.Dead }, new[] { loss }, "rec", 50);
            Assert.True(decision.Emit);
            Assert.Equal(42, decision.CaptureSequence);
        }
    }
}
