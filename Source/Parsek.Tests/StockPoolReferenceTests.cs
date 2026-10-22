using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The drawdown guard's incremental stock pool reference (committed-value rule plan
    /// section 2.4, phase 2): one cell per review case (B1-B5, N1-N3, M1, M2), the latent
    /// release bug of plan section 1, the no-reservation identity with today's guard, the
    /// save / load round trip and the row-writer stamp.
    ///
    /// <para>Each case drives the pure core the way <c>KspStatePatcher.Patch*</c> does
    /// (<see cref="StockPoolReference.Measure"/>, then the write target with the measured kept
    /// leak, then <see cref="StockPoolReference.Commit"/> with the value written), with the
    /// walk's running value and projected minimum given by hand. Where today's guard is wrong,
    /// the cell also asserts what <see cref="KspStatePatcher.ResolveFundsPatch"/> with no
    /// time-travel signal writes for the same inputs, so the cell reads red against the old
    /// guard.</para>
    /// </summary>
    [Collection("Sequential")]
    public class StockPoolReferenceTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public StockPoolReferenceTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            StockPoolReference.ResetForTesting();
            Ledger.ResetForTesting();
        }

        public void Dispose()
        {
            StockPoolReference.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private StockPoolMeasurement lastMeasurement;

        /// <summary>One funds / science patch: measure, write max(0, unfloored + cumLeak), commit.</summary>
        private double PatchCurrency(
            StockPoolReferenceState state, IEnumerable<GameAction> ledger, double now, double live,
            double unflooredTarget, double running, bool authoritative = false,
            bool allowNegative = false, double pendingAdjust = 0.0)
        {
            List<StockPoolRow> rows = StockPoolReference.BuildRows(state.Pool, ledger);
            StockPoolMeasurement m = StockPoolReference.Measure(
                state, rows, now, live, pendingAdjust, allowNegative, authoritative, 0.01,
                unflooredTarget + pendingAdjust);
            double write = m.GapAdopted
                ? live
                : StockPoolReference.ResolveCurrencyWriteTarget(
                    unflooredTarget, running, live, allowNegative, m.CumLeakAfter);
            StockPoolReference.Commit(state, rows, m, write, pendingAdjust);
            lastMeasurement = m;
            return write;
        }

        private double PatchReputation(
            StockPoolReferenceState state, IEnumerable<GameAction> ledger, double now, double live,
            double running, bool authoritative = false)
        {
            List<StockPoolRow> rows = StockPoolReference.BuildRows(StockPool.Reputation, ledger);
            StockPoolMeasurement m = StockPoolReference.Measure(
                state, rows, now, live, 0.0, false, authoritative, 0.01, running);
            double write = m.GapAdopted
                ? live
                : StockPoolReference.ResolveReputationWriteTarget(running, m.CumLeakAfter);
            StockPoolReference.Commit(state, rows, m, write, 0.0);
            lastMeasurement = m;
            return write;
        }

        /// <summary>What today's guard writes for funds with no time-travel signal.</summary>
        private static double OldGuardFunds(double live, double target, double running)
        {
            return KspStatePatcher.ResolveFundsPatch(live, target, running, false).EffectiveTarget;
        }

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
            ActionId = id, UT = ut, Type = GameActionType.ContractFail, FundsPenalty = penalty,
        };

        private static GameAction Complete(string id, double ut, float funds) => new GameAction
        {
            ActionId = id, UT = ut, Type = GameActionType.ContractComplete, TransformedFundsReward = funds,
        };

        /// <summary>
        /// The walk's running balance and no-projection target for <paramref name="ledger"/>
        /// over a 1000 seed, through a real <see cref="FundsModule"/> (stock's 0 clamp included).
        /// </summary>
        private static double WalkFunds(IEnumerable<GameAction> ledger, double seed)
        {
            var funds = new FundsModule();
            funds.Reset();
            var rows = new List<GameAction>
            {
                new GameAction { ActionId = "seed", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = (float)seed },
            };
            rows.AddRange(ledger);
            funds.PrePass(rows);
            foreach (var a in rows)
            {
                a.Effective = true;
                funds.ProcessAction(a);
            }
            Assert.Equal(funds.GetRunningBalance(), funds.GetProjectionMinBalance(), 6);
            return funds.GetRunningBalance();
        }

        /// <summary>A reference that already patched the pool to <paramref name="pool"/> (no rows).</summary>
        private double Baseline(StockPoolReferenceState state, double pool)
        {
            double write = PatchCurrency(state, new GameAction[0], 0.0, pool, pool, pool);
            Assert.True(lastMeasurement.FreshBaseline);
            return write;
        }

        // ------------------------------------------------------------------
        // B1: a committed row crossing the clock in a plain recalculation
        // ------------------------------------------------------------------

        [Fact]
        public void B1_CommittedRowsCrossingTheClock_ApplyOnce_TodaysGuardHoldsThem()
        {
            var state = StockPoolReference.Funds;
            // Committed future (another timeline's KSC rows): an earning, then a cost.
            var ledger = new List<GameAction> { Earn("e", 150, 500f), Spend("c", 200, 300f) };

            // now 100: running 1000, projected minimum 1000 (the earning covers the cost).
            Assert.Equal(1000.0, PatchCurrency(state, ledger, 100, 1000, 1000, 1000), 6);

            // The clock passes both rows with no crossing signal; stock applied nothing.
            double write = PatchCurrency(state, ledger, 250, 1000, 1200, 1200);
            Assert.Equal(0, lastMeasurement.SteppedRows);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(1200.0, write, 6);

            // Today's guard reads running 1200 above live 1000 as an unmodelled spend and holds.
            Assert.Equal(1000.0, OldGuardFunds(1000, 1200, 1200), 6);
        }

        [Fact]
        public void B1_AReservedCostCrossingTheClock_IsNeitherRefundedNorChargedTwice()
        {
            var state = StockPoolReference.Funds;
            Baseline(state, 1000);
            var ledger = new List<GameAction> { Spend("c", 200, 300f) };

            Assert.Equal(700.0, PatchCurrency(state, ledger, 100, 1000, 700, 1000), 6);
            Assert.Equal(700.0, PatchCurrency(state, ledger, 250, 700, 700, 700), 6);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(0.0, state.CumLeak, 6);
        }

        // ------------------------------------------------------------------
        // B2: stock's 0 clamp, stepped where stock applied it
        // ------------------------------------------------------------------

        [Fact]
        public void B2_FailClampedAtZero_ThenARecoveryInFlight_IsNoLeak_AndTheRecoveryIsKept()
        {
            var state = StockPoolReference.Funds;
            Baseline(state, 1000);

            // In flight (patch deferred): a 3000 fail stock clamps at 0, then a 500 recovery.
            var ledger = new List<GameAction> { Fail("f", 10, 3000f), Earn("r", 20, 500f) };
            // The walk applies stock's clamp too: 0 after the fail, 500 after the recovery.
            double walked = WalkFunds(ledger, 1000);
            Assert.Equal(500.0, walked, 6);
            double write = PatchCurrency(state, ledger, 30, live: 500, unflooredTarget: walked, running: walked);

            Assert.Equal(500.0, lastMeasurement.Expected, 6);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(0.0, state.CumLeak, 6);
            Assert.Equal(500.0, write, 6);
            // Without the walk's clamp the target was max(0, -1500) = 0: the recovery taken back.
            Assert.Equal(0.0, StockPoolReference.ResolveCurrencyWriteTarget(-1500, -1500, 500, false, 0.0), 6);
        }

        [Fact]
        public void B2_FailWithNoCostAheadClampedAtZero_ThenAnEarning_IsNoLeak_AndTheEarningIsKept()
        {
            var state = StockPoolReference.Funds;
            Baseline(state, 1000);
            var ledger = new List<GameAction> { Fail("f", 10, 3000f) };

            double walked = WalkFunds(ledger, 1000);
            Assert.Equal(0.0, walked, 6);
            Assert.Equal(0.0, PatchCurrency(state, ledger, 15, live: 0, unflooredTarget: walked, running: walked), 6);
            Assert.False(lastMeasurement.LeakKept);

            ledger.Add(Earn("e", 20, 5000f));
            walked = WalkFunds(ledger, 1000);
            Assert.Equal(5000.0, walked, 6);
            double write = PatchCurrency(state, ledger, 25, live: 5000, unflooredTarget: walked, running: walked);
            Assert.Equal(5000.0, lastMeasurement.Expected, 6);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(5000.0, write, 6);
        }

        [Fact]
        public void B2_AllowNegativeCurrency_StepsWithoutTheClamp()
        {
            var state = StockPoolReference.Funds;
            Baseline(state, 1000);
            var ledger = new List<GameAction> { Fail("f", 10, 3000f), Earn("r", 20, 500f) };

            double write = PatchCurrency(state, ledger, 30, live: -1500, unflooredTarget: -1500,
                running: -1500, allowNegative: true);
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
            var state = StockPoolReference.Funds;
            Baseline(state, 1000);
            // A committed 500 upgrade ahead: running 1000, minimum 500.
            var ledger = new List<GameAction> { Upgrade("k", 1000, 500f) };
            Assert.Equal(500.0, PatchCurrency(state, ledger, 100, 1000, 500, 1000), 6);

            // An earning the ledger does not model: live 700.
            double write = PatchCurrency(state, ledger, 110, 700, 500, 1000);
            Assert.True(lastMeasurement.LeakKept);
            Assert.Equal(200.0, lastMeasurement.Leak, 6);
            Assert.Equal(700.0, write, 6);
            // Today's guard: running 1000 above live 700 holds the target, the earning is lost.
            Assert.Equal(500.0, OldGuardFunds(700, 500, 1000), 6);

            // Two 300 purchases stock charges (rows the ledger holds).
            ledger.Add(Spend("p1", 120, 300f));
            write = PatchCurrency(state, ledger, 120, 400, 200, 700);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(400.0, write, 6);

            ledger.Add(Spend("p2", 130, 300f));
            write = PatchCurrency(state, ledger, 130, 100, -100, 400);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(100.0, write, 6);
            Assert.Equal(200.0, state.CumLeak, 6);

            // Adding the leak AFTER the floor would have written 200 here: money stock never had.
            Assert.Equal(200.0, Math.Max(0.0, -100.0) + 200.0, 6);
        }

        // ------------------------------------------------------------------
        // B4: reputation at the reference's own value, post-curve rows as stored
        // ------------------------------------------------------------------

        [Fact]
        public void B4_ReputationGainFarFromTheWalkValue_IsCurvedAtTheReference()
        {
            var state = StockPoolReference.Reputation;
            PatchReputation(state, new GameAction[0], 0, 50, 50);
            // An unmodelled gain takes live to 300: a kept leak of 250.
            Assert.Equal(300.0, PatchReputation(state, new GameAction[0], 5, 300, 50), 4);
            Assert.Equal(250.0, state.CumLeak, 4);

            // Stock curves a nominal 10 at its live 300; the walk curves it at 50.
            float atLive = ReputationModule.ApplyReputationCurve(10f, 300f).actualDelta;
            float atWalk = ReputationModule.ApplyReputationCurve(10f, 50f).actualDelta;
            Assert.True(Math.Abs(atLive - atWalk) > 0.01f, "the curve must differ for the cell to mean anything");

            var ledger = new List<GameAction>
            {
                new GameAction { ActionId = "g", UT = 10, Type = GameActionType.ReputationEarning, NominalRep = 10f },
            };
            double write = PatchReputation(state, ledger, 10, 300 + atLive, 50 + atWalk);
            Assert.True(Math.Abs(lastMeasurement.Leak) < 1e-3, "leak=" + lastMeasurement.Leak);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(50 + atWalk + 250.0, write, 3);
        }

        [Fact]
        public void B4_PostCurveStoredSources_AreTakenAsStored()
        {
            var state = StockPoolReference.Reputation;
            PatchReputation(state, new GameAction[0], 0, 300, 300);
            float curved = ReputationModule.ApplyReputationCurve(-10f, 300f).actualDelta;
            Assert.True(Math.Abs(curved + 10f) > 0.01f, "a re-curve would differ from the stored amount");

            foreach (var source in new[]
            {
                ReputationPenaltySource.KerbalDeath, ReputationPenaltySource.Strategy,
                ReputationPenaltySource.ContractDecline,
            })
            {
                StockPoolReference.ResetForTesting();
                state = StockPoolReference.Reputation;
                PatchReputation(state, new GameAction[0], 0, 300, 300);
                var ledger = new List<GameAction>
                {
                    new GameAction
                    {
                        ActionId = "d", UT = 10, Type = GameActionType.ReputationPenalty,
                        NominalPenalty = 10f, RepPenaltySource = source,
                    },
                };
                PatchReputation(state, ledger, 10, 290, 290);
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
            var state = StockPoolReference.Science;
            Baseline(state, 100);

            // Stock debits 30 for a tech unlock; the row is still catching up (pending -30). The
            // walk still reads 100; the pending tech-unlock adjuster holds the write at 70.
            double held = PatchCurrency(state, new GameAction[0], 10, live: 70, unflooredTarget: 100 - 30,
                running: 100, pendingAdjust: -30);
            Assert.Equal(70.0, held, 6);
            Assert.Equal(70.0, lastMeasurement.Expected, 6);
            Assert.Equal(-30.0, lastMeasurement.PendingStep, 6);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(-30.0, state.PendingAdjust, 6);

            // The ScienceSpending row lands and the pending amount goes back to 0.
            var ledger = new List<GameAction>
            {
                new GameAction { ActionId = "t", UT = 10, Type = GameActionType.ScienceSpending, Cost = 30f },
            };
            double write = PatchCurrency(state, ledger, 12, live: 70, unflooredTarget: 70, running: 70,
                pendingAdjust: 0);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(70.0, write, 6);
            Assert.Equal(0.0, state.CumLeak, 6);
        }

        // ------------------------------------------------------------------
        // N1: an ordinary Discard's kept awards are stock-applied
        // ------------------------------------------------------------------

        [Fact]
        public void N1_OrdinaryDiscardKeptRows_AreStockApplied_NotKeptTwice()
        {
            var state = StockPoolReference.Funds;
            Baseline(state, 1000);
            // Stock paid a 500 completion during the discarded flight; the Discard files the row.
            var kept = Complete("w", 50, 500f);
            Ledger.AddAction(kept, LedgerRowWriter.StockApplied);
            Assert.False(kept.ParsekAuthored);

            double write = PatchCurrency(state, Ledger.Actions, 60, 1500, 1500, 1500);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(1500.0, write, 6);

            // The same row stamped Parsek-authored would read as a leak and be paid twice.
            StockPoolReference.ResetForTesting();
            state = StockPoolReference.Funds;
            Baseline(state, 1000);
            var authored = Complete("w", 50, 500f);
            authored.ParsekAuthored = true;
            Assert.Equal(2000.0, PatchCurrency(state, new[] { authored }, 60, 1500, 1500, 1500), 6);
        }

        // ------------------------------------------------------------------
        // N2: a milestone row enriched in place is counted once
        // ------------------------------------------------------------------

        [Fact]
        public void N2_MilestoneRowEnrichedInPlace_IsCountedOnce()
        {
            var state = StockPoolReference.Funds;
            Baseline(state, 1000);
            // Stock paid the milestone's 800 already; the row lands with its reward still 0.
            var milestone = new GameAction
            {
                ActionId = "m", UT = 20, Type = GameActionType.MilestoneAchievement, MilestoneFundsAwarded = 0f,
            };
            var ledger = new List<GameAction> { milestone };
            Assert.Equal(1800.0, PatchCurrency(state, ledger, 30, 1800, 1000, 1000), 6);
            Assert.Equal(800.0, state.CumLeak, 6);

            // EnrichPendingMilestoneRewards puts the reward onto the same row.
            milestone.MilestoneFundsAwarded = 800f;
            double write = PatchCurrency(state, ledger, 40, 1800, 1800, 1800);
            Assert.Equal(1, lastMeasurement.SteppedRows);
            Assert.Equal(0.0, state.CumLeak, 6);
            Assert.Equal(1800.0, write, 6);
            Assert.Equal(800.0, state.Counted["m"].Amount, 6);
        }

        // ------------------------------------------------------------------
        // N3: a mid-flight F5, then a later merge
        // ------------------------------------------------------------------

        [Fact]
        public void N3_MidFlightSave_ThenMerge_CountsTheDeferredAwardOnce()
        {
            var state = StockPoolReference.Funds;
            Baseline(state, 1000);
            // In flight (patch deferred) stock pays 400; the player saves.
            var save = new ConfigNode("SCENARIO");
            StockPoolReference.SaveTo(save);

            StockPoolReference.LoadFrom(save, "QuickloadFlight");
            state = StockPoolReference.Funds;
            Assert.Equal(1000.0, state.LastWritten, 6);

            var ledger = new List<GameAction> { Earn("a", 50, 400f) };
            double write = PatchCurrency(state, ledger, 60, 1400, 1400, 1400);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(1400.0, write, 6);

            // Re-reading lastWritten from the loaded pool would count the award twice.
            StockPoolReference.LoadFrom(save, "QuickloadFlight");
            state = StockPoolReference.Funds;
            state.LastWritten = 1400;
            Assert.Equal(1000.0, PatchCurrency(state, ledger, 60, 1400, 1400, 1400), 6);
        }

        // ------------------------------------------------------------------
        // M1: a quicksave from before a merge, reloaded after it
        // ------------------------------------------------------------------

        [Fact]
        public void M1_QuicksaveFromBeforeTheMerge_DoesNotReadTheMergedRowAsInItsPool()
        {
            var state = StockPoolReference.Funds;
            Baseline(state, 1000);
            // Stock paid 400 in flight; F5 before the merge.
            var beforeMerge = new ConfigNode("SCENARIO");
            StockPoolReference.SaveTo(beforeMerge);

            // The merge files the row and patches: the session's reference counts it.
            var ledger = new List<GameAction> { Earn("w", 50, 400f) };
            Assert.Equal(1400.0, PatchCurrency(state, ledger, 60, 1400, 1400, 1400), 6);
            Assert.Equal(400.0, state.Counted["w"].Amount, 6);

            // Load the F5: its pool is 1400 and its reference does not count the row.
            StockPoolReference.LoadFrom(beforeMerge, "QuickloadFlight");
            state = StockPoolReference.Funds;
            Assert.False(state.Counted.ContainsKey("w"));
            double write = PatchCurrency(state, ledger, 55, 1400, 1400, 1400);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(1400.0, write, 6);

            // A counted amount shared across saves (on the ledger row) would pay it twice.
            StockPoolReference.LoadFrom(beforeMerge, "QuickloadFlight");
            state = StockPoolReference.Funds;
            state.Counted["w"] = new CountedRow { Amount = 400, Stepped = 400 };
            Assert.Equal(1800.0, PatchCurrency(state, ledger, 55, 1400, 1400, 1400), 6);
        }

        // ------------------------------------------------------------------
        // M2: a counted row removed between patches is stepped back
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void M2_DuplicateRolloutRepair_RemovingACountedRow_IsSteppedBack(bool authoritative)
        {
            var state = StockPoolReference.Funds;
            Baseline(state, 1000);
            // Stock charged one 100 rollout; the ledger holds it twice.
            var ledger = new List<GameAction> { Spend("r1", 5, 100f), Spend("r2", 5, 100f) };
            Assert.Equal(900.0, PatchCurrency(state, ledger, 10, 900, 800, 800), 6);
            Assert.Equal(100.0, state.CumLeak, 6);

            // RepairDuplicateRolloutActions removes the second row.
            ledger.RemoveAt(1);
            double write = PatchCurrency(state, ledger, 12, 900, 900, 900, authoritative: authoritative);
            Assert.Equal(1, lastMeasurement.RemovedRows);
            Assert.Equal(1, lastMeasurement.ReversedRows);
            Assert.Equal(0.0, state.CumLeak, 6);
            Assert.Equal(900.0, write, 6);
            Assert.False(state.Counted.ContainsKey("r2"));
        }

        // ------------------------------------------------------------------
        // The latent release bug (plan section 1)
        // ------------------------------------------------------------------

        [Fact]
        public void ReleasedReservation_ComesBack_TodaysGuardKeepsItAway()
        {
            var state = StockPoolReference.Funds;
            Baseline(state, 1000);
            // A committed 300 cost ahead holds the pool at 700.
            var ledger = new List<GameAction> { Spend("c", 200, 300f) };
            Assert.Equal(700.0, PatchCurrency(state, ledger, 100, 1000, 700, 1000), 6);

            // The committed cost leaves the timeline (never stock-applied, so nothing to step back).
            ledger.Clear();
            double write = PatchCurrency(state, ledger, 110, 700, 1000, 1000);
            Assert.Equal(1, lastMeasurement.RemovedRows);
            Assert.Equal(0, lastMeasurement.ReversedRows);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(1000.0, write, 6);
            Assert.Equal(700.0, OldGuardFunds(700, 1000, 1000), 6);
        }

        [Fact]
        public void ReleasedReservation_ContractCompletion_PlanSection1Numbers()
        {
            // Running R, reserve S (committed cost) + P (open contract): live R - S - P. Stock
            // pays the completion W: the commit's target is R + W - S and nothing fights it.
            const double R = 10000, S = 2000, P = 1000, W = 3000;
            var state = StockPoolReference.Funds;
            Baseline(state, R);
            var ledger = new List<GameAction> { Upgrade("s", 500, (float)S) };
            Assert.Equal(R - S - P, PatchCurrency(state, ledger, 100, R, R - S - P, R), 6);

            ledger.Add(Complete("w", 110, (float)W));
            double write = PatchCurrency(state, ledger, 120, R - S - P + W, R + W - S, R + W);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(R + W - S, write, 6);
            Assert.Equal(R - S - P + W, OldGuardFunds(R - S - P + W, R + W - S, R + W), 6);
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
            var state = StockPoolReference.Funds;
            Baseline(state, pool);
            var ledger = new List<GameAction> { Earn("e", 10, (float)rowAmount) };
            double running = pool + rowAmount;
            double live = running + untracked;

            double write = PatchCurrency(state, ledger, 20, live, running, running);
            Assert.Equal(OldGuardFunds(live, running, running), write, 3);
        }

        // ------------------------------------------------------------------
        // Fresh baseline, rows, the write target
        // ------------------------------------------------------------------

        [Fact]
        public void FreshBaseline_StartsFromLive_WithEveryRowCounted_AndKeepsTheOlderBuildsGap()
        {
            // A save from an older build: the old guard kept the pool at 1234 while the
            // reservation-aware target is 990. The first patch takes the 244 gap as the kept
            // leak and leaves the pool where it is, today's behaviour for that save.
            var state = StockPoolReference.Funds;
            var ledger = new List<GameAction> { Earn("a", 5, 100f), Spend("b", 6, 40f), Spend("f", 500, 70f) };
            double write = PatchCurrency(state, ledger, 10, 1234, 990, 1060);

            Assert.True(lastMeasurement.FreshBaseline);
            Assert.True(lastMeasurement.GapAdopted);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(244.0, lastMeasurement.AdoptedGap, 6);
            Assert.Equal(1234.0, write, 6);
            Assert.Equal(244.0, state.CumLeak, 6);
            Assert.True(state.Initialized);
            Assert.Equal(3, state.Counted.Count);
            Assert.All(state.Counted.Values, c => Assert.Equal(0.0, c.Stepped, 6));
            Assert.Equal(-70.0, state.Counted["f"].Amount, 6);
            Assert.Contains("adoptedGap=244", StockPoolReference.FormatMeasurement(lastMeasurement));

            // The next patch with nothing changed writes the same pool: the gap stays kept.
            Assert.Equal(1234.0, PatchCurrency(state, ledger, 20, 1234, 990, 1060), 6);
            Assert.False(lastMeasurement.LeakKept);

            // A later committed cost still reserves from that pool (the reservation is not
            // swallowed by the adopted gap).
            ledger.Add(Upgrade("u", 900, 200f));
            Assert.Equal(1034.0, PatchCurrency(state, ledger, 30, 1234, 790, 1060), 6);
        }

        [Fact]
        public void FreshBaseline_AnOverdrawnTarget_KeepsThePool_NotZero()
        {
            // The leak-free target is below 0 (an older save whose committed future overdraws
            // the walk); the pool the older build left stays, not max(0, target).
            var state = StockPoolReference.Funds;
            double write = PatchCurrency(state, new[] { Spend("c", 900, 500f) }, 10, 300, -200, 300);
            Assert.True(lastMeasurement.GapAdopted);
            Assert.Equal(500.0, state.CumLeak, 6);
            Assert.Equal(300.0, write, 6);
        }

        [Fact]
        public void FreshBaseline_Authoritative_WritesTheTarget_AdoptsNoGap()
        {
            var state = StockPoolReference.Funds;
            double write = PatchCurrency(state, new[] { Earn("a", 5, 100f) }, 10, 1234, 990, 990,
                authoritative: true);
            Assert.True(lastMeasurement.FreshBaseline);
            Assert.False(lastMeasurement.GapAdopted);
            Assert.Equal(990.0, write, 6);
            Assert.Equal(0.0, state.CumLeak, 6);
        }

        [Fact]
        public void FreshBaseline_Reputation_KeepsTheLivePool()
        {
            var state = StockPoolReference.Reputation;
            double write = PatchReputation(state, new GameAction[0], 10, live: 55, running: 50);
            Assert.True(lastMeasurement.GapAdopted);
            Assert.Equal(55.0, write, 6);
            Assert.Equal(5.0, state.CumLeak, 6);
            // A later gain the ledger holds lands on top of the kept gap.
            Assert.Equal(60.0, PatchReputation(state, new GameAction[0], 20, live: 55, running: 55), 6);
        }

        [Fact]
        public void Rows_SeedsAndOtherPoolsAreNotRows_IneffectiveRowsCountZero()
        {
            var actions = new List<GameAction>
            {
                new GameAction { ActionId = "seed", Type = GameActionType.FundsInitial, InitialFunds = 5f },
                new GameAction { ActionId = "sci", Type = GameActionType.ScienceEarning, EffectiveScience = 3f },
                new GameAction { ActionId = "fail", Type = GameActionType.ContractFail, FundsPenalty = 9f, Effective = false },
                Earn("e", 1, 10f),
            };
            List<StockPoolRow> rows = StockPoolReference.BuildRows(StockPool.Funds, actions);
            Assert.Equal(new[] { "fail", "e" }, rows.Select(r => r.ActionId).ToArray());
            Assert.Equal(0.0, rows[0].Amount, 6);
            Assert.Equal(10.0, rows[1].Amount, 6);
        }

        [Fact]
        public void AffectsPool_CoversEveryTypeWithADelta()
        {
            foreach (GameActionType type in Enum.GetValues(typeof(GameActionType)))
            {
                var a = new GameAction
                {
                    Type = type, FundsAwarded = 1, MilestoneFundsAwarded = 1, AdvanceFunds = 1,
                    TransformedFundsReward = 1, FundsSpent = 1, FundsPenalty = 1, FacilityCost = 1,
                    HireCost = 1, SetupCost = 1, EffectiveScience = 1, MilestoneScienceAwarded = 1,
                    TransformedScienceReward = 1, Cost = 1, ScienceAwarded = 1, SetupScienceCost = 1,
                };
                if (FundsModule.TryGetRowDelta(a, out double fd) && fd != 0.0)
                    Assert.True(FundsModule.AffectsFunds(type), type.ToString());
                if (ScienceModule.TryGetRowDelta(a, out double sd) && sd != 0.0)
                    Assert.True(ScienceModule.AffectsScience(type), type.ToString());
            }
        }

        [Theory]
        [InlineData(500.0, 0.0, false, 500.0)]
        [InlineData(500.0, 200.0, false, 700.0)]
        [InlineData(-100.0, 200.0, false, 100.0)]
        [InlineData(-300.0, 200.0, false, 0.0)]
        [InlineData(500.0, -600.0, false, 0.0)]
        public void WriteTarget_AddsTheLeakBeforeTheFloor(
            double unfloored, double cumLeak, bool allowNegative, double expected)
        {
            Assert.Equal(expected,
                StockPoolReference.ResolveCurrencyWriteTarget(unfloored, unfloored, 0.0, allowNegative, cumLeak), 6);
        }

        [Fact]
        public void WriteTarget_NoLeak_IsTodaysTarget()
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
        public void SaveLoad_RoundTrip_KeepsEveryValue_AndOnlyNonZeroRows()
        {
            var funds = StockPoolReference.Funds;
            funds.Initialized = true;
            funds.LastWritten = 1234.5678;
            funds.CumLeak = -12.25;
            funds.Counted["act_a"] = new CountedRow { Amount = 100.125, Stepped = 100.125 };
            funds.Counted["act_b"] = new CountedRow { Amount = -40, Stepped = 0 };
            funds.Counted["act_zero"] = new CountedRow { Amount = 0, Stepped = 0 };
            var science = StockPoolReference.Science;
            science.Initialized = true;
            science.LastWritten = 77.75;
            science.PendingAdjust = -3.5;

            var node = new ConfigNode("SCENARIO");
            StockPoolReference.SaveTo(node);
            ConfigNode saved = node.GetNode(StockPoolReference.NodeName);
            Assert.NotNull(saved);
            Assert.Null(saved.GetNode(StockPoolReference.ReputationNodeName));
            Assert.Equal(2, saved.GetNode(StockPoolReference.FundsNodeName).GetValues(StockPoolReference.RowKey).Length);
            Assert.Contains(logLines, l => l.Contains("[StockPoolReference]") && l.Contains("Saved: pools=2 rows=2"));

            StockPoolReference.ResetForTesting();
            StockPoolReference.LoadFrom(node, "Cold");

            funds = StockPoolReference.Funds;
            Assert.True(funds.Initialized);
            Assert.Equal(1234.5678, funds.LastWritten);
            Assert.Equal(-12.25, funds.CumLeak);
            Assert.Equal(2, funds.Counted.Count);
            Assert.Equal(100.125, funds.Counted["act_a"].Stepped);
            Assert.Equal(-40.0, funds.Counted["act_b"].Amount);
            Assert.Equal(-3.5, StockPoolReference.Science.PendingAdjust);
            Assert.False(StockPoolReference.Reputation.Initialized);
            Assert.Contains(logLines, l => l.Contains("[StockPoolReference]") && l.Contains("Loaded (Cold): pools=2 rows=2 badRows=0"));
        }

        [Fact]
        public void Load_WithoutSavedValues_StartsEveryPoolAfresh()
        {
            StockPoolReference.Funds.Initialized = true;
            StockPoolReference.Funds.CumLeak = 50;

            StockPoolReference.LoadFrom(new ConfigNode("SCENARIO"), "Cold");

            Assert.False(StockPoolReference.Funds.Initialized);
            Assert.Equal(0.0, StockPoolReference.CumLeakOf(StockPool.Funds), 6);
            Assert.Contains(logLines, l => l.Contains("[StockPoolReference]") && l.Contains("no saved reference"));

            // The first patch then starts from the live pool and keeps the older build's gap.
            double write = PatchCurrency(StockPoolReference.Funds, new[] { Earn("e", 1, 10f) }, 5, 900, 800, 800);
            Assert.True(lastMeasurement.FreshBaseline);
            Assert.True(lastMeasurement.GapAdopted);
            Assert.Equal(900.0, write, 6);
            Assert.Equal(100.0, StockPoolReference.CumLeakOf(StockPool.Funds), 6);
        }

        [Fact]
        public void Load_WithSavedValues_MeasuresAgainstThem_AdoptsNoGap()
        {
            // The same pool and target as the cell above, but the save carries a reference that
            // last wrote 900 with nothing kept: the 100 above the target is a released
            // reservation and the patch writes the target.
            var source = StockPoolReference.Funds;
            source.Initialized = true;
            source.LastWritten = 900;
            source.CumLeak = 0;
            source.Counted["e"] = new CountedRow { Amount = 10, Stepped = 0 };
            var node = new ConfigNode("SCENARIO");
            StockPoolReference.SaveTo(node);
            StockPoolReference.ResetForTesting();

            StockPoolReference.LoadFrom(node, "Cold");
            Assert.True(StockPoolReference.Funds.Initialized);

            double write = PatchCurrency(StockPoolReference.Funds, new[] { Earn("e", 1, 10f) }, 5, 900, 800, 800);
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
            ConfigNode funds = root.AddNode(StockPoolReference.FundsNodeName);
            funds.AddValue(StockPoolReference.LastWrittenKey, "garbage");
            funds.AddValue(StockPoolReference.CumLeakKey, "0");
            ConfigNode rep = root.AddNode(StockPoolReference.ReputationNodeName);
            rep.AddValue(StockPoolReference.LastWrittenKey, "10");
            rep.AddValue(StockPoolReference.CumLeakKey, "2");
            rep.AddValue(StockPoolReference.RowKey, "act_x notanumber 0");
            rep.AddValue(StockPoolReference.RowKey, "act_y 3 3");

            StockPoolReference.LoadFrom(node, "InSession");

            Assert.False(StockPoolReference.Funds.Initialized);
            Assert.True(StockPoolReference.Reputation.Initialized);
            Assert.Single(StockPoolReference.Reputation.Counted);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("Funds reference unreadable"));
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("badRows=1"));
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

            List<StockPoolRow> rows = StockPoolReference.BuildRows(StockPool.Funds, Ledger.Actions);
            Assert.False(rows.Single(r => r.ActionId == "a").StockApplied);
            Assert.True(rows.Single(r => r.ActionId == "s").StockApplied);
        }

        [Fact]
        public void ParsekAuthoredRows_AreNeverStepped()
        {
            var state = StockPoolReference.Funds;
            Baseline(state, 1000);
            var row = Earn("p", 5, 300f);
            row.ParsekAuthored = true;
            double write = PatchCurrency(state, new[] { row }, 10, 1000, 1300, 1300);
            Assert.Equal(0, lastMeasurement.SteppedRows);
            Assert.Equal(1300.0, write, 6);
            Assert.Equal(0.0, state.Counted["p"].Stepped, 6);
        }

        [Fact]
        public void FormatMeasurement_NamesTheMode_AndIsInvariant()
        {
            var prior = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    System.Globalization.CultureInfo.GetCultureInfo("de-DE");
                var state = StockPoolReference.Funds;
                Baseline(state, 1000.5);
                PatchCurrency(state, new GameAction[0], 1, 1200.25, 1000.5, 1000.5);
                string text = StockPoolReference.FormatMeasurement(lastMeasurement);
                Assert.Contains("mode=measured", text);
                Assert.Contains("leak=199.75", text);
                Assert.Contains("cumLeak=0->199.75", text);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = prior;
            }
        }
    }
}
