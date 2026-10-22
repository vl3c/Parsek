using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The drawdown guard's incremental stock pool reference (committed-value rule plan
    /// section 2.4, phase 2): one cell per review case (B1-B5, N1-N3, M1, M2), the reviewer's
    /// five sequences against the cumLeak model (R1-R5), the latent release bug of plan
    /// section 1, the no-reservation identity with today's guard, the save / load round trip
    /// and the row-writer stamp.
    ///
    /// <para>Each case drives the real walk (<see cref="RecalculationEngine.Recalculate"/> over
    /// registered modules, a cutoff walk at the patch's now) and then the pure core the way
    /// <c>KspStatePatcher.Patch*</c> does: <see cref="StockPoolReference.Measure"/> over the
    /// walk's per-row effects, a leak filed as a
    /// <see cref="GameActionType.StockPoolAdjustment"/> row in the test's ledger, the write
    /// target plus this patch's adjustment, then <see cref="StockPoolReference.Commit"/> with
    /// the value written. Where today's guard is wrong, the cell also asserts what
    /// <see cref="KspStatePatcher.ResolveFundsPatch"/> with no time-travel signal writes for
    /// the same inputs.</para>
    /// </summary>
    [Collection("Sequential")]
    public class StockPoolReferenceTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<GameAction> ledger = new List<GameAction>();
        private readonly FundsModule funds = new FundsModule();
        private readonly ScienceModule science = new ScienceModule();
        private readonly ReputationModule reputation = new ReputationModule();
        private readonly ContractsModule contracts = new ContractsModule();
        private StockPoolMeasurement lastMeasurement;

        public StockPoolReferenceTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = () => false;
            StockPoolReference.ResetForTesting();
            Ledger.ResetForTesting();
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
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private void Walk(double now)
        {
            RecalculationEngine.Recalculate(new List<GameAction>(ledger), now);
        }

        /// <summary>Files the measurement's adjustment into the test ledger, as the patcher does.</summary>
        private void File(StockPoolMeasurement m, StockPool pool, double now)
        {
            if (m.Adjustment == 0.0)
                return;
            GameAction row = StockPoolReference.BuildAdjustmentRow(pool, m.Adjustment, now);
            ledger.Add(row);
            m.AdjustmentRow = row;
        }

        /// <summary>One funds patch: walk at now, measure, file, write, commit.</summary>
        private double PatchFunds(double now, double live, bool authoritative = false)
        {
            Walk(now);
            var state = StockPoolReference.Funds;
            List<StockPoolRow> rows = StockPoolReference.BuildRows(StockPool.Funds, ledger, funds.GetRowEffects());
            double min = funds.GetProjectionMinBalance();
            StockPoolMeasurement m = StockPoolReference.Measure(
                state, rows, now, live, 0.0, authoritative, 0.01, min);
            File(m, StockPool.Funds, now);
            double write = m.GapAdopted
                ? live
                : StockPoolReference.ResolveCurrencyWriteTarget(
                    min, funds.GetRunningBalance(), live,
                    KspStatePatcher.ReadAllowNegativeCurrency(), m.Adjustment);
            StockPoolReference.Commit(state, rows, m, write, 0.0);
            lastMeasurement = m;
            return write;
        }

        /// <summary>One science patch; <paramref name="pendingAdjust"/> stands for the three
        /// pending adjusters (added to the write as the patcher's adjusters add them).</summary>
        private double PatchScience(double now, double live, double pendingAdjust = 0.0, bool authoritative = false)
        {
            Walk(now);
            var state = StockPoolReference.Science;
            List<StockPoolRow> rows = StockPoolReference.BuildRows(StockPool.Science, ledger, science.GetRowEffects());
            double min = science.GetProjectionMinBalance();
            StockPoolMeasurement m = StockPoolReference.Measure(
                state, rows, now, live, pendingAdjust, authoritative, 0.001, min + pendingAdjust);
            File(m, StockPool.Science, now);
            double write = m.GapAdopted
                ? live
                : StockPoolReference.ResolveCurrencyWriteTarget(
                    min + pendingAdjust, science.GetRunningScience() + pendingAdjust, live, false, m.Adjustment);
            StockPoolReference.Commit(state, rows, m, write, pendingAdjust);
            lastMeasurement = m;
            return write;
        }

        private double PatchReputation(double now, double live, bool authoritative = false)
        {
            Walk(now);
            var state = StockPoolReference.Reputation;
            List<StockPoolRow> rows = StockPoolReference.BuildRows(
                StockPool.Reputation, ledger, reputation.GetRowEffects());
            double running = reputation.GetRunningRep();
            StockPoolMeasurement m = StockPoolReference.Measure(
                state, rows, now, live, 0.0, authoritative, 0.01, running);
            File(m, StockPool.Reputation, now);
            double write = m.GapAdopted
                ? live
                : StockPoolReference.ResolveReputationWriteTarget(running, m.Adjustment);
            StockPoolReference.Commit(state, rows, m, write, 0.0);
            lastMeasurement = m;
            return write;
        }

        private double Adjusted(StockPool pool) => StockPoolReference.SumAdjustments(pool, ledger);

        /// <summary>What today's guard writes for funds with no time-travel signal.</summary>
        private static double OldGuardFunds(double live, double target, double running)
        {
            return KspStatePatcher.ResolveFundsPatch(live, target, running, false).EffectiveTarget;
        }

        private static GameAction FundsSeed(double funds) => new GameAction
        {
            ActionId = "seedF", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = (float)funds,
        };

        private static GameAction ScienceSeed(double sci) => new GameAction
        {
            ActionId = "seedS", UT = 0, Type = GameActionType.ScienceInitial, InitialScience = (float)sci,
        };

        private static GameAction RepSeed(double rep) => new GameAction
        {
            ActionId = "seedR", UT = 0, Type = GameActionType.ReputationInitial, InitialReputation = (float)rep,
        };

        private static GameAction Earn(string id, double ut, float funds) => new GameAction
        {
            ActionId = id, UT = ut, Type = GameActionType.FundsEarning, FundsAwarded = funds,
        };

        private static GameAction Spend(string id, double ut, float funds) => new GameAction
        {
            ActionId = id, UT = ut, Type = GameActionType.FundsSpending, FundsSpent = funds,
        };

        private static GameAction Upgrade(string id, double ut, float cost) => new GameAction
        {
            ActionId = id, UT = ut, Type = GameActionType.FacilityUpgrade, FacilityCost = cost,
        };

        private static GameAction Fail(string id, double ut, float penalty) => new GameAction
        {
            ActionId = id, UT = ut, Type = GameActionType.ContractFail, ContractId = id, FundsPenalty = penalty,
        };

        private static GameAction Complete(string id, double ut, float funds) => new GameAction
        {
            ActionId = id, UT = ut, Type = GameActionType.ContractComplete, ContractId = id, FundsReward = funds,
        };

        /// <summary>A reference that already patched the funds pool at its seed (no other rows).</summary>
        private void BaselineFunds(double pool)
        {
            ledger.Add(FundsSeed(pool));
            Assert.Equal(pool, PatchFunds(0.0, pool), 6);
            Assert.True(lastMeasurement.FreshBaseline);
            Assert.Equal(0.0, lastMeasurement.Adjustment, 6);
        }

        // ------------------------------------------------------------------
        // B1: a committed row crossing the clock in a plain recalculation
        // ------------------------------------------------------------------

        [Fact]
        public void B1_CommittedRowsCrossingTheClock_ApplyOnce_TodaysGuardHoldsThem()
        {
            // Committed future (another timeline's KSC rows): an earning, then a cost.
            ledger.Add(FundsSeed(1000));
            ledger.Add(Earn("e", 150, 500f));
            ledger.Add(Spend("c", 200, 300f));

            // now 100: running 1000, projected minimum 1000 (the earning covers the cost).
            Assert.Equal(1000.0, PatchFunds(100, 1000), 6);
            Assert.True(double.IsNaN(StockPoolReference.Funds.Counted["e"].Effect));

            // The clock passes both rows with no crossing signal; stock applied nothing.
            double write = PatchFunds(250, 1000);
            Assert.Equal(0, lastMeasurement.SteppedRows);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(1200.0, write, 6);
            Assert.Equal(0.0, Adjusted(StockPool.Funds), 6);

            // Today's guard reads running 1200 above live 1000 as an unmodelled spend and holds.
            Assert.Equal(1000.0, OldGuardFunds(1000, 1200, 1200), 6);
        }

        [Fact]
        public void B1_AReservedCostCrossingTheClock_IsNeitherRefundedNorChargedTwice()
        {
            BaselineFunds(1000);
            ledger.Add(Spend("c", 200, 300f));

            Assert.Equal(700.0, PatchFunds(100, 1000), 6);
            Assert.Equal(700.0, PatchFunds(250, 700), 6);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(0.0, Adjusted(StockPool.Funds), 6);
        }

        // ------------------------------------------------------------------
        // B2: stock's 0 clamp, read off the walk's own effect
        // ------------------------------------------------------------------

        [Fact]
        public void B2_FailClampedAtZero_ThenARecoveryInFlight_IsNoLeak_AndTheRecoveryIsKept()
        {
            BaselineFunds(1000);
            // In flight (patch deferred): a 3000 fail stock clamps at 0, then a 500 recovery.
            ledger.Add(Fail("f", 10, 3000f));
            ledger.Add(Earn("r", 20, 500f));
            double write = PatchFunds(30, live: 500);

            Assert.Equal(500.0, lastMeasurement.Expected, 6);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(500.0, write, 6);
            // The fail stepped by what the walk applied (the clamp forgave 2000).
            Assert.Equal(-1000.0, StockPoolReference.Funds.Counted["f"].Effect, 6);
        }

        [Fact]
        public void B2_FailWithNoCostAheadClampedAtZero_ThenAnEarning_IsNoLeak_AndTheEarningIsKept()
        {
            BaselineFunds(1000);
            ledger.Add(Fail("f", 10, 3000f));
            Assert.Equal(0.0, PatchFunds(15, live: 0), 6);
            Assert.False(lastMeasurement.LeakKept);

            ledger.Add(Earn("e", 20, 5000f));
            double write = PatchFunds(25, live: 5000);
            Assert.Equal(5000.0, lastMeasurement.Expected, 6);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(5000.0, write, 6);
        }

        [Fact]
        public void B2_AllowNegativeCurrency_StepsWithoutTheClamp()
        {
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = () => true;
            BaselineFunds(1000);
            ledger.Add(Fail("f", 10, 3000f));
            ledger.Add(Earn("r", 20, 500f));

            double write = PatchFunds(30, live: -1500);
            Assert.Equal(-1500.0, lastMeasurement.Expected, 6);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(-1500.0, write, 6);
        }

        // ------------------------------------------------------------------
        // B3: a kept leak, then repeated purchases (no money created)
        // ------------------------------------------------------------------

        [Fact]
        public void B3_KeptLeakThenRepeatedPurchases_CreatesNoMoney_AndKeepsTheReservation()
        {
            BaselineFunds(1000);
            // A committed 500 upgrade ahead: running 1000, minimum 500.
            ledger.Add(Upgrade("k", 1000, 500f));
            Assert.Equal(500.0, PatchFunds(100, 1000), 6);

            // An earning the ledger does not model: live 700. It becomes a ledger row.
            double write = PatchFunds(110, 700);
            Assert.True(lastMeasurement.LeakKept);
            Assert.Equal(200.0, lastMeasurement.Leak, 6);
            Assert.Equal(700.0, write, 6);
            Assert.Equal(200.0, Adjusted(StockPool.Funds), 6);
            GameAction adjustment = ledger.Single(a => a.Type == GameActionType.StockPoolAdjustment);
            Assert.Equal(110.0, adjustment.UT, 6);
            Assert.Null(adjustment.RecordingId);
            Assert.Equal(200.0, StockPoolReference.Funds.Counted[adjustment.ActionId].Stepped, 6);
            // Today's guard: running 1000 above live 700 holds the target, the earning is lost.
            Assert.Equal(500.0, OldGuardFunds(700, 500, 1000), 6);

            // Two 300 purchases stock charges (rows the ledger holds).
            ledger.Add(Spend("p1", 120, 300f));
            write = PatchFunds(120, 400);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(400.0, write, 6);

            ledger.Add(Spend("p2", 130, 300f));
            write = PatchFunds(130, 100);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(100.0, write, 6);
            Assert.Equal(200.0, Adjusted(StockPool.Funds), 6);
            // The reservation still holds: running 600, the upgrade ahead takes 500.
            Assert.Equal(600.0, funds.GetRunningBalance(), 6);
        }

        // ------------------------------------------------------------------
        // B4: reputation curved where stock curved it
        // ------------------------------------------------------------------

        [Fact]
        public void B4_ReputationGainFarFromTheSeed_IsCurvedAtThePoolValue()
        {
            ledger.Add(RepSeed(50));
            PatchReputation(0, 50);
            // An unmodelled gain takes live to 300: an adjustment of 250.
            Assert.Equal(300.0, PatchReputation(5, 300), 4);
            Assert.Equal(250.0, Adjusted(StockPool.Reputation), 4);

            // Stock curves a nominal 10 at its live 300; the walk, carrying the adjustment, too.
            float atLive = ReputationModule.ApplyReputationCurve(10f, 300f).actualDelta;
            float atSeed = ReputationModule.ApplyReputationCurve(10f, 50f).actualDelta;
            Assert.True(Math.Abs(atLive - atSeed) > 0.01f, "the curve must differ for the cell to mean anything");

            ledger.Add(new GameAction { ActionId = "g", UT = 10, Type = GameActionType.ReputationEarning, NominalRep = 10f });
            double write = PatchReputation(10, 300 + atLive);
            Assert.True(Math.Abs(lastMeasurement.Leak) < 1e-3, "leak=" + lastMeasurement.Leak);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(300 + atLive, write, 3);
        }

        [Fact]
        public void B4_PostCurveStoredSources_AreTakenAsStored()
        {
            foreach (var source in new[]
            {
                ReputationPenaltySource.KerbalDeath, ReputationPenaltySource.Strategy,
                ReputationPenaltySource.ContractDecline,
            })
            {
                StockPoolReference.ResetForTesting();
                ledger.Clear();
                ledger.Add(RepSeed(300));
                PatchReputation(0, 300);
                ledger.Add(new GameAction
                {
                    ActionId = "d", UT = 10, Type = GameActionType.ReputationPenalty,
                    NominalPenalty = 10f, RepPenaltySource = source,
                });
                PatchReputation(10, 290);
                Assert.Equal(290.0, lastMeasurement.Expected, 4);
                Assert.False(lastMeasurement.LeakKept, source.ToString());
            }
        }

        // ------------------------------------------------------------------
        // B5: a pending tech unlock is something stock applied
        // ------------------------------------------------------------------

        [Fact]
        public void B5_PendingTechUnlock_IsInTheExpectation_ThenItsRowLands()
        {
            ledger.Add(ScienceSeed(100));
            PatchScience(0, 100);

            // Stock debits 30 for a tech unlock; the row is still catching up (pending -30).
            double held = PatchScience(10, live: 70, pendingAdjust: -30);
            Assert.Equal(70.0, held, 6);
            Assert.Equal(70.0, lastMeasurement.Expected, 6);
            Assert.Equal(-30.0, lastMeasurement.PendingStep, 6);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(-30.0, StockPoolReference.Science.PendingAdjust, 6);

            // The ScienceSpending row lands and the pending amount goes back to 0.
            ledger.Add(new GameAction { ActionId = "t", UT = 10, Type = GameActionType.ScienceSpending, Cost = 30f });
            double write = PatchScience(12, live: 70, pendingAdjust: 0);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(70.0, write, 6);
            Assert.Equal(0.0, Adjusted(StockPool.Science), 6);
        }

        // ------------------------------------------------------------------
        // N1: an ordinary Discard's kept awards are stock-applied
        // ------------------------------------------------------------------

        [Fact]
        public void N1_OrdinaryDiscardKeptRows_AreStockApplied_NotKeptTwice()
        {
            BaselineFunds(1000);
            // Stock paid a 500 completion during the discarded flight; the Discard files the row.
            var kept = Complete("w", 50, 500f);
            Ledger.AddAction(kept, LedgerRowWriter.StockApplied);
            Assert.False(kept.ParsekAuthored);
            ledger.Add(kept);

            Assert.Equal(1500.0, PatchFunds(60, 1500), 6);
            Assert.False(lastMeasurement.LeakKept);

            // The same row stamped Parsek-authored would read as a leak and be paid twice.
            StockPoolReference.ResetForTesting();
            ledger.Clear();
            BaselineFunds(1000);
            var authored = Complete("w", 50, 500f);
            authored.ParsekAuthored = true;
            ledger.Add(authored);
            Assert.Equal(2000.0, PatchFunds(60, 1500), 6);
        }

        // ------------------------------------------------------------------
        // N2: a milestone row enriched in place is counted once
        // ------------------------------------------------------------------

        [Fact]
        public void N2_MilestoneRowEnrichedInPlace_IsCountedOnce()
        {
            BaselineFunds(1000);
            // Stock paid the milestone's 800 already; the row lands with its reward still 0.
            var milestone = new GameAction
            {
                ActionId = "m", UT = 20, Type = GameActionType.MilestoneAchievement, MilestoneFundsAwarded = 0f,
            };
            ledger.Add(milestone);
            Assert.Equal(1800.0, PatchFunds(30, 1800), 6);
            Assert.Equal(800.0, Adjusted(StockPool.Funds), 6);

            // EnrichPendingMilestoneRewards puts the reward onto the same row: the row steps
            // by its new effect and the adjustment is taken back, so the 800 is counted once.
            milestone.MilestoneFundsAwarded = 800f;
            double write = PatchFunds(40, 1800);
            Assert.Equal(1, lastMeasurement.SteppedRows);
            Assert.Equal(-800.0, lastMeasurement.Adjustment, 6);
            Assert.Equal(0.0, Adjusted(StockPool.Funds), 6);
            Assert.Equal(1800.0, write, 6);
            Assert.Equal(800.0, StockPoolReference.Funds.Counted["m"].Effect, 6);

            // And it stays put.
            Assert.Equal(1800.0, PatchFunds(50, 1800), 6);
            Assert.False(lastMeasurement.LeakKept);
        }

        // ------------------------------------------------------------------
        // N3: a mid-flight F5, then a later merge
        // ------------------------------------------------------------------

        [Fact]
        public void N3_MidFlightSave_ThenMerge_CountsTheDeferredAwardOnce()
        {
            BaselineFunds(1000);
            // In flight (patch deferred) stock pays 400; the player saves.
            var save = new ConfigNode("SCENARIO");
            StockPoolReference.SaveTo(save);

            StockPoolReference.LoadFrom(save, "QuickloadFlight");
            Assert.Equal(1000.0, StockPoolReference.Funds.LastWritten, 6);

            ledger.Add(Earn("a", 50, 400f));
            double write = PatchFunds(60, 1400);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(1400.0, write, 6);

            // Re-reading lastWritten from the loaded pool would count the award twice.
            StockPoolReference.LoadFrom(save, "QuickloadFlight");
            StockPoolReference.Funds.LastWritten = 1400;
            Assert.Equal(1000.0, PatchFunds(60, 1400), 6);
        }

        // ------------------------------------------------------------------
        // M1: a quicksave from before a merge, reloaded after it
        // ------------------------------------------------------------------

        [Fact]
        public void M1_QuicksaveFromBeforeTheMerge_DoesNotReadTheMergedRowAsInItsPool()
        {
            BaselineFunds(1000);
            // Stock paid 400 in flight; F5 before the merge.
            var beforeMerge = new ConfigNode("SCENARIO");
            StockPoolReference.SaveTo(beforeMerge);

            // The merge files the row and patches: the session's reference counts it.
            ledger.Add(Earn("w", 50, 400f));
            Assert.Equal(1400.0, PatchFunds(60, 1400), 6);
            Assert.Equal(400.0, StockPoolReference.Funds.Counted["w"].Effect, 6);

            // Load the F5: its pool is 1400 and its reference does not count the row.
            StockPoolReference.LoadFrom(beforeMerge, "QuickloadFlight");
            Assert.False(StockPoolReference.Funds.Counted.ContainsKey("w"));
            double write = PatchFunds(55, 1400);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(1400.0, write, 6);

            // A counted amount shared across saves (on the ledger row) would pay it twice.
            StockPoolReference.LoadFrom(beforeMerge, "QuickloadFlight");
            StockPoolReference.Funds.Counted["w"] = new CountedRow { Effect = 400, Stepped = 400 };
            Assert.Equal(1800.0, PatchFunds(55, 1400), 6);
        }

        // ------------------------------------------------------------------
        // M2: a counted row removed between patches is stepped back
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void M2_DuplicateRolloutRepair_RemovingACountedRow_IsSteppedBack(bool authoritative)
        {
            BaselineFunds(1000);
            // Stock charged one 100 rollout; the ledger holds it twice.
            var r2 = Spend("r2", 5, 100f);
            ledger.Add(Spend("r1", 5, 100f));
            ledger.Add(r2);
            Assert.Equal(900.0, PatchFunds(10, 900), 6);
            Assert.Equal(100.0, Adjusted(StockPool.Funds), 6);

            // RepairDuplicateRolloutActions removes the second row: its stepped part goes back
            // and the adjustment that compensated for it is taken back with it.
            ledger.Remove(r2);
            double write = PatchFunds(12, 900, authoritative: authoritative);
            Assert.Equal(1, lastMeasurement.RemovedRows);
            Assert.Equal(1, lastMeasurement.ReversedRows);
            Assert.Equal(-100.0, lastMeasurement.Adjustment, 6);
            Assert.Equal(0.0, Adjusted(StockPool.Funds), 6);
            Assert.Equal(900.0, write, 6);
            Assert.False(StockPoolReference.Funds.Counted.ContainsKey("r2"));

            // The next patch with nothing changed writes the same pool.
            Assert.Equal(900.0, PatchFunds(14, 900), 6);
            Assert.False(lastMeasurement.LeakKept);
        }

        // ------------------------------------------------------------------
        // R1-R5: the review of the cumLeak model (2026-10-22), red against a075d07
        // ------------------------------------------------------------------

        [Fact]
        public void R1_NegativeLeak_ThenAFailStockClampsAndAnEarning_KeepsTheWholeEarning()
        {
            // cumLeak -500, running 600, live 100: the walk clamped the fail at its running
            // 600 (no clamp) while stock clamped it at 100; the cumLeak model then wrote 800.
            BaselineFunds(600);
            Assert.Equal(100.0, PatchFunds(10, 100), 6);   // an unmodelled 500 spend
            Assert.Equal(-500.0, Adjusted(StockPool.Funds), 6);

            // Stock fails a 300 contract (100 -> 0, 200 forgiven), then pays a 1000 earning.
            ledger.Add(Fail("f", 20, 300f));
            Assert.Equal(0.0, PatchFunds(25, 0), 6);
            Assert.False(lastMeasurement.LeakKept);
            ledger.Add(Earn("e", 30, 1000f));
            double write = PatchFunds(35, 1000);

            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(1000.0, write, 6);
            Assert.Equal(-500.0, Adjusted(StockPool.Funds), 6);
        }

        [Fact]
        public void R1b_TheSameSequenceInOneMeasuredPatch_KeepsTheWholeEarning()
        {
            BaselineFunds(600);
            Assert.Equal(100.0, PatchFunds(10, 100), 6);
            ledger.Add(Fail("f", 20, 300f));
            ledger.Add(Earn("e", 30, 1000f));

            Assert.Equal(1000.0, PatchFunds(35, 1000), 6);
            Assert.False(lastMeasurement.LeakKept);
        }

        [Fact]
        public void R2_PositiveLeak_ThenAFailTheWalkWouldClamp_CreatesNoMoney()
        {
            // cumLeak +40k, running 10k, live 50k, a 30k fail: the walk clamped at its 10k
            // running (forgiving 20k stock never forgave) and the cumLeak model wrote 40k.
            BaselineFunds(10000);
            Assert.Equal(50000.0, PatchFunds(10, 50000), 6);
            Assert.Equal(40000.0, Adjusted(StockPool.Funds), 6);

            ledger.Add(Fail("f", 20, 30000f));
            double write = PatchFunds(25, 20000);

            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(20000.0, write, 6);
            Assert.Equal(-30000.0, StockPoolReference.Funds.Counted["f"].Effect, 6);
        }

        [Fact]
        public void R3_ReputationGain_IsCurvedAtThePoolValue_NotAtTheWalksOwn()
        {
            ledger.Add(RepSeed(0));
            PatchReputation(0, 0);
            Assert.Equal(500.0, PatchReputation(10, 500), 4);

            float atPool = ReputationModule.ApplyReputationCurve(50f, 500f).actualDelta;
            float atZero = ReputationModule.ApplyReputationCurve(50f, 0f).actualDelta;
            Assert.True(Math.Abs(atPool - atZero) > 1f, "the curve must differ for the cell to mean anything");

            ledger.Add(new GameAction { ActionId = "g", UT = 20, Type = GameActionType.ReputationEarning, NominalRep = 50f });
            double write = PatchReputation(25, 500 + atPool);

            Assert.False(lastMeasurement.LeakKept, "leak=" + lastMeasurement.Leak);
            Assert.Equal(500 + atPool, write, 3);
            Assert.Equal(500.0, Adjusted(StockPool.Reputation), 3);
        }

        [Fact]
        public void R4_ScienceTechInThePendingWindow_ThenItsRowLands_LeavesNoLeak()
        {
            // Science 30, a 25-cost tech unlocked with a patch while its row is pending: the
            // cumLeak model re-stepped the row through its own 0 clamp next to the pending
            // change and kept a leak of -20 for good.
            ledger.Add(ScienceSeed(30));
            PatchScience(0, 30);

            Assert.Equal(5.0, PatchScience(10, live: 5, pendingAdjust: -25), 6);
            Assert.False(lastMeasurement.LeakKept);

            ledger.Add(new GameAction { ActionId = "t", UT = 10, Type = GameActionType.ScienceSpending, Cost = 25f });
            double write = PatchScience(12, live: 5, pendingAdjust: 0);

            Assert.False(lastMeasurement.LeakKept, "leak=" + lastMeasurement.Leak);
            Assert.Equal(5.0, write, 6);
            Assert.Equal(0.0, Adjusted(StockPool.Science), 6);
            Assert.Equal(5.0, PatchScience(20, live: 5), 6);
            Assert.False(lastMeasurement.LeakKept);
        }

        [Fact]
        public void R5_StockFailAndTheSyntheticFailInOneMeasuredPatch_LeaveNoLastingLeak()
        {
            // Funds 150, an accepted contract with a 100 penalty and a deadline at 40.
            ledger.Add(FundsSeed(150));
            ledger.Add(new GameAction
            {
                ActionId = "acc", UT = 10, Type = GameActionType.ContractAccept, ContractId = "k",
                DeadlineUT = 40, FundsPenalty = 100f,
            });
            Assert.Equal(150.0, PatchFunds(20, 150), 6);

            // The deadline passes in flight: stock fails the contract (150 -> 50), its row is
            // not filed yet, and the cutoff walk injects the synthetic fail. The synthetic row
            // moves no pool, so the reference reads stock's charge as a leak, once.
            double write = PatchFunds(50, 50);
            Assert.Contains(logLines, l => l.Contains("[RecalcEngine]")
                && l.Contains("Walk withheld 1 injected row(s) from the stock pool modules")
                && l.Contains("contractId='k'"));
            Assert.Equal(150.0, funds.GetRunningBalance(), 6);
            Assert.Equal(50.0, write, 6);
            Assert.Equal(-100.0, Adjusted(StockPool.Funds), 6);

            // The real fail row lands: it steps by its effect and the adjustment is taken back.
            ledger.Add(Fail("k", 40, 100f));
            write = PatchFunds(60, 50);
            Assert.Equal(50.0, write, 6);
            Assert.Equal(0.0, Adjusted(StockPool.Funds), 6);

            // Nothing lasting: the next patch measures nothing and writes the same pool.
            Assert.Equal(50.0, PatchFunds(70, 50), 6);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(50.0, funds.GetRunningBalance(), 6);
        }

        [Fact]
        public void R5b_TheStockFailRowAfterTheDeadline_InOneMeasuredPatch_ChargesOnce()
        {
            // Stock files its fail row after the deadline: the cutoff walk injects a synthetic
            // fail at the deadline and also walks the real row. Only the real row moves the pool.
            ledger.Add(FundsSeed(150));
            ledger.Add(new GameAction
            {
                ActionId = "acc", UT = 10, Type = GameActionType.ContractAccept, ContractId = "k",
                DeadlineUT = 40, FundsPenalty = 100f,
            });
            Assert.Equal(150.0, PatchFunds(20, 150), 6);
            ledger.Add(Fail("k", 45, 100f));

            Assert.Equal(50.0, PatchFunds(50, 50), 6);
            Assert.False(lastMeasurement.LeakKept, "leak=" + lastMeasurement.Leak);
            Assert.Equal(50.0, PatchFunds(70, 50), 6);
            Assert.Equal(0.0, Adjusted(StockPool.Funds), 6);
        }

        [Fact]
        public void L5_ASyntheticFailStockNeverCharged_MovesNoPool()
        {
            // L5's shape: contract B is ledger-only (stock never had it), its deadline lapses;
            // the synthetic fail retires it in the contracts module and charges nothing.
            ledger.Add(FundsSeed(500000));
            ledger.Add(RepSeed(2));
            ledger.Add(new GameAction
            {
                ActionId = "accB", UT = 6, Type = GameActionType.ContractAccept, ContractId = "B",
                DeadlineUT = 100, FundsPenalty = 9000f, RepPenalty = 1f,
            });
            Assert.Equal(500000.0, PatchFunds(9, 500000), 6);
            PatchReputation(9, 2);

            Assert.Equal(500000.0, PatchFunds(348, 500000), 6);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(2.0, PatchReputation(348, 2), 4);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Contains(logLines, l => l.Contains("[Contracts]") && l.Contains("Fail: contractId='B'"));
            Assert.Contains(logLines, l => l.Contains("Walk withheld 1 injected row(s)")
                && l.Contains("fundsPenalty=9000") && l.Contains("repPenalty=1"));
        }

        // ------------------------------------------------------------------
        // The latent release bug (plan section 1)
        // ------------------------------------------------------------------

        [Fact]
        public void ReleasedReservation_ComesBack_TodaysGuardKeepsItAway()
        {
            BaselineFunds(1000);
            // A committed 300 cost ahead holds the pool at 700.
            var c = Spend("c", 200, 300f);
            ledger.Add(c);
            Assert.Equal(700.0, PatchFunds(100, 1000), 6);

            // The committed cost leaves the timeline (never stock-applied, so nothing to step back).
            ledger.Remove(c);
            double write = PatchFunds(110, 700);
            Assert.Equal(1, lastMeasurement.RemovedRows);
            Assert.Equal(0, lastMeasurement.ReversedRows);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(1000.0, write, 6);
            Assert.Equal(700.0, OldGuardFunds(700, 1000, 1000), 6);
        }

        [Fact]
        public void ContractCompletion_PastACommittedReserve_ReachesThePool()
        {
            const double R = 10000, S = 2000, W = 3000;
            BaselineFunds(R);
            ledger.Add(Upgrade("s", 500, (float)S));
            Assert.Equal(R - S, PatchFunds(100, R), 6);

            ledger.Add(Complete("w", 110, (float)W));
            double write = PatchFunds(120, R - S + W);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(R + W - S, write, 6);
        }

        // ------------------------------------------------------------------
        // No reservation: exactly today's guard
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(1000.0, 1000.0, 250.0)]
        [InlineData(1000.0, 1000.0, -250.0)]
        [InlineData(5000.0, 4200.0, 37.5)]
        [InlineData(5000.0, 4200.0, -837.5)]
        [InlineData(20.0, 5.0, 0.0)]
        public void NoReservation_IsTodaysGuard(double pool, double rowAmount, double untracked)
        {
            // From a pool the ledger agrees with, stock applies one row the ledger holds plus
            // an untracked amount; the target is the running value (no reservation).
            BaselineFunds(pool);
            ledger.Add(Earn("e", 10, (float)rowAmount));
            double running = pool + rowAmount;
            double live = running + untracked;

            double write = PatchFunds(20, live);
            Assert.Equal(OldGuardFunds(live, running, running), write, 3);
        }

        // ------------------------------------------------------------------
        // Fresh baseline, rows, the write target
        // ------------------------------------------------------------------

        [Fact]
        public void FreshBaseline_StartsFromLive_WithEveryRowCounted_AndFilesTheOlderBuildsGap()
        {
            // A save from an older build: the old guard kept the pool at 1234 while the
            // reservation-aware target is 990. The first patch files the 244 gap as one
            // adjustment row and leaves the pool where it is, today's behaviour for that save.
            ledger.Add(FundsSeed(1000));
            ledger.Add(Earn("a", 5, 100f));
            ledger.Add(Spend("b", 6, 40f));
            ledger.Add(Spend("f", 500, 70f));
            double write = PatchFunds(10, 1234);

            Assert.True(lastMeasurement.FreshBaseline);
            Assert.True(lastMeasurement.GapAdopted);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(244.0, lastMeasurement.AdoptedGap, 6);
            Assert.Equal(1234.0, write, 6);
            Assert.Equal(244.0, Adjusted(StockPool.Funds), 6);
            var state = StockPoolReference.Funds;
            Assert.True(state.Initialized);
            Assert.Equal(4, state.Counted.Count);
            Assert.Equal(0.0, state.Counted["a"].Stepped, 6);
            Assert.Equal(100.0, state.Counted["a"].Effect, 6);
            Assert.True(double.IsNaN(state.Counted["f"].Effect));
            Assert.Contains("adoptedGap=244", StockPoolReference.FormatMeasurement(lastMeasurement));

            // The next patch with nothing changed writes the same pool: the row carries the gap.
            Assert.Equal(1234.0, PatchFunds(20, 1234), 6);
            Assert.False(lastMeasurement.LeakKept);

            // A later committed cost still reserves from that pool.
            ledger.Add(Upgrade("u", 900, 200f));
            Assert.Equal(1034.0, PatchFunds(30, 1234), 6);
        }

        [Fact]
        public void FreshBaseline_AnOverdrawnTarget_KeepsThePool_NotZero()
        {
            ledger.Add(FundsSeed(300));
            ledger.Add(Spend("c", 900, 500f));
            double write = PatchFunds(10, 300);
            Assert.True(lastMeasurement.GapAdopted);
            Assert.Equal(500.0, Adjusted(StockPool.Funds), 6);
            Assert.Equal(300.0, write, 6);
            Assert.Equal(300.0, PatchFunds(20, 300), 6);
        }

        [Fact]
        public void FreshBaseline_Authoritative_WritesTheTarget_AdoptsNoGap()
        {
            ledger.Add(FundsSeed(890));
            ledger.Add(Earn("a", 5, 100f));
            double write = PatchFunds(10, 1234, authoritative: true);
            Assert.True(lastMeasurement.FreshBaseline);
            Assert.False(lastMeasurement.GapAdopted);
            Assert.Equal(990.0, write, 6);
            Assert.Equal(0.0, Adjusted(StockPool.Funds), 6);
        }

        [Fact]
        public void FreshBaseline_Reputation_KeepsTheLivePool()
        {
            ledger.Add(RepSeed(50));
            double write = PatchReputation(10, live: 55);
            Assert.True(lastMeasurement.GapAdopted);
            Assert.Equal(55.0, write, 6);
            Assert.Equal(5.0, Adjusted(StockPool.Reputation), 6);
            Assert.Equal(55.0, PatchReputation(20, live: 55), 4);
            Assert.False(lastMeasurement.LeakKept);
        }

        [Fact]
        public void FreshBaseline_NoGap_FilesNoRow()
        {
            ledger.Add(FundsSeed(1000));
            PatchFunds(10, 1000.004);
            Assert.True(lastMeasurement.GapAdopted);
            Assert.Equal(0.0, lastMeasurement.Adjustment, 6);
            Assert.DoesNotContain(ledger, a => a.Type == GameActionType.StockPoolAdjustment);
        }

        [Fact]
        public void Rows_SeedsAndOtherPoolsAreNotRows_UnwalkedRowsAreNaN()
        {
            var actions = new List<GameAction>
            {
                FundsSeed(5),
                new GameAction { ActionId = "sci", Type = GameActionType.ScienceEarning, EffectiveScience = 3f },
                new GameAction { ActionId = "fail", Type = GameActionType.ContractFail, FundsPenalty = 9f },
                Earn("e", 1, 10f),
                StockPoolReference.BuildAdjustmentRow(StockPool.Funds, 7, 2),
                StockPoolReference.BuildAdjustmentRow(StockPool.Science, 3, 2),
            };
            var effects = new Dictionary<string, double> { ["fail"] = 0.0, ["e"] = 10.0 };
            List<StockPoolRow> rows = StockPoolReference.BuildRows(StockPool.Funds, actions, effects);
            Assert.Equal(new[] { "fail", "e", actions[4].ActionId }, rows.Select(r => r.ActionId).ToArray());
            Assert.Equal(0.0, rows[0].Effect, 6);
            Assert.Equal(10.0, rows[1].Effect, 6);
            Assert.True(double.IsNaN(rows[2].Effect));
            Assert.Equal(actions[5].ActionId,
                StockPoolReference.BuildRows(StockPool.Science, actions, null).Single(r => r.ActionId != "sci").ActionId);
        }

        [Fact]
        public void AffectsPool_CoversEveryTypeWithADelta()
        {
            foreach (GameActionType type in Enum.GetValues(typeof(GameActionType)))
            {
                var a = new GameAction
                {
                    Type = type, FundsAwarded = 1, MilestoneFundsAwarded = 1, AdvanceFunds = 1,
                    TransformedFundsReward = 1, FundsPenalty = 1, FundsSpent = 1, FacilityCost = 1,
                    HireCost = 1, SetupCost = 1, EffectiveScience = 1, MilestoneScienceAwarded = 1,
                    TransformedScienceReward = 1, Cost = 1, ScienceAwarded = 1, SetupScienceCost = 1,
                    AdjustmentAmount = 1,
                };
                if (FundsModule.TryGetRowDelta(a, out double fd) && fd != 0.0)
                    Assert.True(StockPoolReference.IsPoolRow(StockPool.Funds, a), type.ToString());
                a.AdjustmentPool = StockPool.Science;
                if (ScienceModule.TryGetRowDelta(a, out double sd) && sd != 0.0)
                    Assert.True(StockPoolReference.IsPoolRow(StockPool.Science, a), type.ToString());
            }
        }

        [Fact]
        public void AdjustmentRow_IsAnUnclampedPostCurveRowOfItsPoolOnly()
        {
            ledger.Add(FundsSeed(100));
            ledger.Add(ScienceSeed(10));
            ledger.Add(RepSeed(400));
            ledger.Add(StockPoolReference.BuildAdjustmentRow(StockPool.Funds, -300, 5));
            ledger.Add(StockPoolReference.BuildAdjustmentRow(StockPool.Science, -40, 5));
            ledger.Add(StockPoolReference.BuildAdjustmentRow(StockPool.Reputation, 25, 5));
            Walk(10);

            // Not clamped at 0, and each moves its own pool only; reputation as stored.
            Assert.Equal(-200.0, funds.GetRunningBalance(), 6);
            Assert.Equal(-30.0, science.GetRunningScience(), 6);
            Assert.Equal(425.0, reputation.GetRunningRep(), 3);
            Assert.True(RecalculationEngine.IsEarningType(GameActionType.StockPoolAdjustment));
        }

        [Theory]
        [InlineData(500.0, 0.0, false, 500.0)]
        [InlineData(500.0, 200.0, false, 700.0)]
        [InlineData(-100.0, 200.0, false, 100.0)]
        [InlineData(-300.0, 200.0, false, 0.0)]
        [InlineData(500.0, -600.0, false, 0.0)]
        public void WriteTarget_AddsTheAdjustmentBeforeTheFloor(
            double unfloored, double adjustment, bool allowNegative, double expected)
        {
            Assert.Equal(expected,
                StockPoolReference.ResolveCurrencyWriteTarget(unfloored, unfloored, 0.0, allowNegative, adjustment), 6);
        }

        [Fact]
        public void WriteTarget_NoAdjustment_IsTodaysTarget()
        {
            foreach (double min in new[] { 500.0, 0.0, -100.0 })
            foreach (bool allowNeg in new[] { false, true })
            {
                double floored = min > 0 ? min : 0;
                Assert.Equal(
                    KspStatePatcher.ResolveNegativeCurrencyPatchTarget(floored, min, -50.0, -80.0, allowNeg),
                    StockPoolReference.ResolveCurrencyWriteTarget(min, -50.0, -80.0, allowNeg, 0.0), 6);
            }
        }

        // ------------------------------------------------------------------
        // Persistence
        // ------------------------------------------------------------------

        [Fact]
        public void SaveLoad_RoundTrip_KeepsEveryValue_AndOnlyMeaningfulRows()
        {
            var fundsState = StockPoolReference.Funds;
            fundsState.Initialized = true;
            fundsState.LastWritten = 1234.5678;
            fundsState.Counted["act_a"] = new CountedRow { Effect = 100.125, Stepped = 100.125 };
            fundsState.Counted["act_b"] = new CountedRow { Effect = -40, Stepped = 0 };
            fundsState.Counted["act_future"] = new CountedRow { Effect = double.NaN, Stepped = 0 };
            fundsState.Counted["act_zero"] = new CountedRow { Effect = 0, Stepped = 0 };
            var scienceState = StockPoolReference.Science;
            scienceState.Initialized = true;
            scienceState.LastWritten = 77.75;
            scienceState.PendingAdjust = -3.5;

            var node = new ConfigNode("SCENARIO");
            StockPoolReference.SaveTo(node);
            ConfigNode saved = node.GetNode(StockPoolReference.NodeName);
            Assert.NotNull(saved);
            Assert.Null(saved.GetNode(StockPoolReference.ReputationNodeName));
            ConfigNode savedFunds = saved.GetNode(StockPoolReference.FundsNodeName);
            Assert.Equal(3, savedFunds.GetValues(StockPoolReference.RowKey).Length);
            Assert.Null(savedFunds.GetValue("cumLeak"));
            Assert.Contains(logLines, l => l.Contains("[StockPoolReference]") && l.Contains("Saved: pools=2 rows=3"));

            StockPoolReference.ResetForTesting();
            StockPoolReference.LoadFrom(node, "Cold");

            fundsState = StockPoolReference.Funds;
            Assert.True(fundsState.Initialized);
            Assert.Equal(1234.5678, fundsState.LastWritten);
            Assert.Equal(3, fundsState.Counted.Count);
            Assert.Equal(100.125, fundsState.Counted["act_a"].Stepped);
            Assert.Equal(-40.0, fundsState.Counted["act_b"].Effect);
            Assert.True(double.IsNaN(fundsState.Counted["act_future"].Effect));
            Assert.Equal(-3.5, StockPoolReference.Science.PendingAdjust);
            Assert.False(StockPoolReference.Reputation.Initialized);
            Assert.Contains(logLines, l => l.Contains("[StockPoolReference]") && l.Contains("Loaded (Cold): pools=2 rows=3 badRows=0"));
        }

        [Fact]
        public void Load_WithoutSavedValues_StartsEveryPoolAfresh()
        {
            StockPoolReference.Funds.Initialized = true;

            StockPoolReference.LoadFrom(new ConfigNode("SCENARIO"), "Cold");

            Assert.False(StockPoolReference.Funds.Initialized);
            Assert.Contains(logLines, l => l.Contains("[StockPoolReference]") && l.Contains("no saved reference"));

            // The first patch then starts from the live pool and files the older build's gap.
            ledger.Add(FundsSeed(790));
            ledger.Add(Earn("e", 1, 10f));
            double write = PatchFunds(5, 900);
            Assert.True(lastMeasurement.FreshBaseline);
            Assert.True(lastMeasurement.GapAdopted);
            Assert.Equal(900.0, write, 6);
            Assert.Equal(100.0, Adjusted(StockPool.Funds), 6);
        }

        [Fact]
        public void Load_WithSavedValues_MeasuresAgainstThem_AdoptsNoGap()
        {
            // The same pool and target as the cell above, but the save carries a reference that
            // last wrote 900 with the row counted: the 100 above the target is a released
            // reservation and the patch writes the target.
            var source = StockPoolReference.Funds;
            source.Initialized = true;
            source.LastWritten = 900;
            source.Counted["e"] = new CountedRow { Effect = 10, Stepped = 0 };
            var node = new ConfigNode("SCENARIO");
            StockPoolReference.SaveTo(node);
            StockPoolReference.ResetForTesting();

            StockPoolReference.LoadFrom(node, "Cold");
            Assert.True(StockPoolReference.Funds.Initialized);

            ledger.Add(FundsSeed(790));
            ledger.Add(Earn("e", 1, 10f));
            double write = PatchFunds(5, 900);
            Assert.False(lastMeasurement.FreshBaseline);
            Assert.False(lastMeasurement.GapAdopted);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(800.0, write, 6);
        }

        [Fact]
        public void Load_UnreadableValuesOrRows_Warn()
        {
            var node = new ConfigNode("SCENARIO");
            ConfigNode root = node.AddNode(StockPoolReference.NodeName);
            ConfigNode fundsNode = root.AddNode(StockPoolReference.FundsNodeName);
            fundsNode.AddValue(StockPoolReference.LastWrittenKey, "garbage");
            ConfigNode rep = root.AddNode(StockPoolReference.ReputationNodeName);
            rep.AddValue(StockPoolReference.LastWrittenKey, "10");
            rep.AddValue(StockPoolReference.RowKey, "act_x notanumber 0");
            rep.AddValue(StockPoolReference.RowKey, "act_y 3 3");

            StockPoolReference.LoadFrom(node, "InSession");

            Assert.False(StockPoolReference.Funds.Initialized);
            Assert.True(StockPoolReference.Reputation.Initialized);
            Assert.Single(StockPoolReference.Reputation.Counted);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("Funds reference unreadable"));
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("badRows=1"));
        }

        [Fact]
        public void AdjustmentRow_SerializesAndSurvivesAColdReconcile()
        {
            GameAction row = StockPoolReference.BuildAdjustmentRow(StockPool.Reputation, -12.375, 4321.5);
            var parent = new ConfigNode("LEDGER");
            row.SerializeInto(parent);
            GameAction back = GameAction.DeserializeFrom(parent.GetNode("GAME_ACTION"));
            Assert.Equal(GameActionType.StockPoolAdjustment, back.Type);
            Assert.Equal(StockPool.Reputation, back.AdjustmentPool);
            Assert.Equal(-12.375, back.AdjustmentAmount);
            Assert.Equal(4321.5, back.UT);
            Assert.Equal(row.ActionId, back.ActionId);

            // Untagged and an earning: a reconcile at maxUT 0 (a cold load) keeps it.
            Ledger.AddAction(back, LedgerRowWriter.StockApplied);
            Ledger.Reconcile(new HashSet<string>(), 0.0);
            Assert.Contains(Ledger.Actions, a => a.ActionId == row.ActionId);
        }

        // ------------------------------------------------------------------
        // The row-writer stamp
        // ------------------------------------------------------------------

        [Fact]
        public void LedgerWriter_StampsTheRow_AndTheStampRoundTrips()
        {
            var authored = Earn("a", 1, 5f);
            var stock = Earn("s", 1, 5f);
            Ledger.AddAction(authored, LedgerRowWriter.ParsekAuthored);
            Ledger.AddActions(new[] { stock }, LedgerRowWriter.StockApplied);
            Assert.True(authored.ParsekAuthored);
            Assert.False(stock.ParsekAuthored);
            Assert.Contains(logLines, l => l.Contains("[Ledger]") && l.Contains("writer=ParsekAuthored"));

            var parent = new ConfigNode("LEDGER");
            authored.SerializeInto(parent);
            stock.SerializeInto(parent);
            ConfigNode[] nodes = parent.GetNodes("GAME_ACTION");
            Assert.Equal("True", nodes[0].GetValue(GameAction.ParsekAuthoredKey));
            Assert.Null(nodes[1].GetValue(GameAction.ParsekAuthoredKey));
            Assert.True(GameAction.DeserializeFrom(nodes[0]).ParsekAuthored);
            Assert.False(GameAction.DeserializeFrom(nodes[1]).ParsekAuthored);

            // A restore keeps each row's own stamp.
            Ledger.ResetForTesting();
            Ledger.RestoreActions(new[] { authored, stock });
            Assert.True(authored.ParsekAuthored);
            Assert.False(stock.ParsekAuthored);

            List<StockPoolRow> rows = StockPoolReference.BuildRows(StockPool.Funds, Ledger.Actions, null);
            Assert.False(rows.Single(r => r.ActionId == "a").StockApplied);
            Assert.True(rows.Single(r => r.ActionId == "s").StockApplied);
        }

        [Fact]
        public void ParsekAuthoredRows_AreNeverStepped()
        {
            BaselineFunds(1000);
            var row = Earn("p", 5, 300f);
            row.ParsekAuthored = true;
            ledger.Add(row);
            double write = PatchFunds(10, 1000);
            Assert.Equal(0, lastMeasurement.SteppedRows);
            Assert.Equal(1300.0, write, 6);
            Assert.Equal(0.0, StockPoolReference.Funds.Counted["p"].Stepped, 6);
        }

        [Fact]
        public void FormatMeasurement_NamesTheMode_AndIsInvariant()
        {
            var prior = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    System.Globalization.CultureInfo.GetCultureInfo("de-DE");
                BaselineFunds(1000.5);
                PatchFunds(1, 1200.25);
                string text = StockPoolReference.FormatMeasurement(lastMeasurement);
                Assert.Contains("mode=measured", text);
                Assert.Contains("leak=199.75", text);
                Assert.Contains("adjustment=199.75", text);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = prior;
            }
        }
    }
}
