using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Design 3.15 step 5 (owner ruling 2026-10-26), the take-back half: when stock pays live
    /// for an identity the timeline already credited, because a rewind made stock forget the
    /// earlier credit, the next update takes the payment back, logs one Warn naming the
    /// identity, and files nothing as a <see cref="GameActionType.StockPoolAdjustment"/>. Only
    /// rows first captured by this build (the take-back era) and only where a timeline break
    /// separates the repeat from its witness; a career never rewound ends exactly as before.
    ///
    /// <para>Each walk case drives the real modules (<see cref="RecalculationEngine.Recalculate"/>
    /// at the patch's now) and then the pure pool core the way <c>KspStatePatcher.Patch*</c>
    /// does (<see cref="StockPoolReference.Measure"/> over the walk's effects and the modules'
    /// take-back deltas, a leak filed in the test's ledger, the write, the commit).</para>
    /// </summary>
    [Collection("Sequential")]
    public class StockShadowTakeBackTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<GameAction> ledger = new List<GameAction>();
        private readonly MilestonesModule milestones = new MilestonesModule();
        private readonly ContractsModule contracts = new ContractsModule();
        private readonly ScienceModule science = new ScienceModule();
        private readonly FundsModule funds = new FundsModule();
        private readonly ReputationModule reputation = new ReputationModule();
        private StockPoolMeasurement lastMeasurement;

        public StockShadowTakeBackTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = () => false;
            StockPoolReference.ResetForTesting();
            Ledger.ResetForTesting();
            CommittedMilestoneReach.ResetForTesting();
            RecalculationEngine.ClearModules();
            RecalculationEngine.RegisterModule(milestones, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(contracts, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(science, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(reputation, RecalculationEngine.ModuleTier.SecondTier);
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = null;
            StockPoolReference.ResetForTesting();
            CommittedMilestoneReach.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Harness
        // ------------------------------------------------------------------

        private HashSet<string> IdsInLedger() => new HashSet<string>(
            ledger.Where(a => a != null && !string.IsNullOrEmpty(a.ActionId)).Select(a => a.ActionId),
            StringComparer.Ordinal);

        private void Walk(double now) => RecalculationEngine.Recalculate(ledger.ToList(), now);

        private void File(StockPoolMeasurement m, StockPool pool, double now)
        {
            if (m.Adjustment == 0.0)
                return;
            GameAction row = StockPoolReference.BuildAdjustmentRow(pool, m.Adjustment, now);
            ledger.Add(row);
            m.AdjustmentRow = row;
        }

        private double PatchFunds(double now, double live, bool authoritative = false)
        {
            Walk(now);
            var state = StockPoolReference.Funds;
            List<StockPoolRow> rows = StockPoolReference.BuildRows(
                StockPool.Funds, ledger, funds.GetRowEffects(), funds.GetTakeBackDeltas());
            double min = funds.GetProjectionMinBalance();
            StockPoolMeasurement m = StockPoolReference.Measure(
                state, rows, now, live, 0.0, authoritative, 0.01, min, IdsInLedger(), clampAtZero: true);
            File(m, StockPool.Funds, now);
            KspStatePatcher.EmitTakeBacks(StockPool.Funds, m, ledger);
            double write = m.GapAdopted
                ? live
                : StockPoolReference.ResolveCurrencyWriteTarget(min, funds.GetRunningBalance(), live, false, m.Adjustment);
            StockPoolReference.Commit(state, rows, m, write, 0.0);
            lastMeasurement = m;
            return write;
        }

        private double PatchScience(double now, double live, bool authoritative = false)
        {
            Walk(now);
            var state = StockPoolReference.Science;
            List<StockPoolRow> rows = StockPoolReference.BuildRows(
                StockPool.Science, ledger, science.GetRowEffects(), science.GetTakeBackDeltas());
            double min = science.GetProjectionMinBalance();
            StockPoolMeasurement m = StockPoolReference.Measure(
                state, rows, now, live, 0.0, authoritative, 0.001, min, IdsInLedger(), clampAtZero: true);
            File(m, StockPool.Science, now);
            KspStatePatcher.EmitTakeBacks(StockPool.Science, m, ledger);
            double write = m.GapAdopted
                ? live
                : StockPoolReference.ResolveCurrencyWriteTarget(min, science.GetRunningScience(), live, false, m.Adjustment);
            StockPoolReference.Commit(state, rows, m, write, 0.0);
            lastMeasurement = m;
            return write;
        }

        private double PatchReputation(double now, double live, bool authoritative = false)
        {
            Walk(now);
            var state = StockPoolReference.Reputation;
            List<StockPoolRow> rows = StockPoolReference.BuildRows(
                StockPool.Reputation, ledger, reputation.GetRowEffects(), reputation.GetTakeBackDeltas());
            double running = reputation.GetRunningRep();
            StockPoolMeasurement m = StockPoolReference.Measure(
                state, rows, now, live, 0.0, authoritative, 0.01, running, IdsInLedger());
            File(m, StockPool.Reputation, now);
            KspStatePatcher.EmitTakeBacks(StockPool.Reputation, m, ledger);
            double write = m.GapAdopted ? live : StockPoolReference.ResolveReputationWriteTarget(running, m.Adjustment);
            StockPoolReference.Commit(state, rows, m, write, 0.0);
            lastMeasurement = m;
            return write;
        }

        private int AdjustmentRows => ledger.Count(a => a.Type == GameActionType.StockPoolAdjustment);

        private double Adjusted(StockPool pool) => StockPoolReference.SumAdjustments(pool, ledger);

        private void Seeds(double f = 1000, double s = 0, double r = 0)
        {
            ledger.Add(new GameAction { ActionId = "seedF", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = (float)f });
            ledger.Add(new GameAction { ActionId = "seedS", UT = 0, Type = GameActionType.ScienceInitial, InitialScience = (float)s });
            ledger.Add(new GameAction { ActionId = "seedR", UT = 0, Type = GameActionType.ReputationInitial, InitialReputation = (float)r });
        }

        private static GameAction Milestone(string actionId, string id, double ut, long cseq,
            float f, float r, float s) => new GameAction
        {
            ActionId = actionId, UT = ut, CaptureSequence = cseq, Type = GameActionType.MilestoneAchievement,
            MilestoneId = id, MilestoneFundsAwarded = f, MilestoneRepAwarded = r, MilestoneScienceAwarded = s,
        };

        private static GameAction Science(string actionId, string subject, double ut, long cseq,
            float awarded, float cap) => new GameAction
        {
            ActionId = actionId, UT = ut, CaptureSequence = cseq, Type = GameActionType.ScienceEarning,
            SubjectId = subject, ScienceAwarded = awarded, SubjectMaxValue = cap,
        };

        private static GameAction Record(string actionId, string id, double ut, long cseq, params int[] indices)
        {
            var band = indices.Select(i => new RecordThresholdCredit(i, i * 1000f, i * 0.5f, i * 0.25f)).ToList();
            return new GameAction
            {
                ActionId = actionId, UT = ut, CaptureSequence = cseq, Type = GameActionType.MilestoneAchievement,
                MilestoneId = id,
                MilestoneFundsAwarded = band.Sum(c => c.Funds),
                MilestoneRepAwarded = band.Sum(c => c.Rep),
                MilestoneScienceAwarded = band.Sum(c => c.Science),
                MilestoneRecordThresholds = band.Count,
                MilestoneRecordBand = band,
            };
        }

        /// <summary>A record row with an explicit band and award count (folded re-payments).</summary>
        private static GameAction RecordBand(string actionId, string id, double ut, long cseq, int awards,
            params RecordThresholdCredit[] band) => new GameAction
        {
            ActionId = actionId, UT = ut, CaptureSequence = cseq, Type = GameActionType.MilestoneAchievement,
            MilestoneId = id,
            MilestoneFundsAwarded = band.Sum(c => c.Funds),
            MilestoneRepAwarded = band.Sum(c => c.Rep),
            MilestoneScienceAwarded = band.Sum(c => c.Science),
            MilestoneRecordThresholds = awards,
            MilestoneRecordBand = band.ToList(),
        };

        private static GameAction Accept(string id, double ut, long cseq) => new GameAction
        {
            ActionId = "acc-" + id, UT = ut, CaptureSequence = cseq, Type = GameActionType.ContractAccept,
            ContractId = id,
        };

        private static GameAction ContractDone(string actionId, string id, double ut, long cseq, float f) => new GameAction
        {
            ActionId = actionId, UT = ut, CaptureSequence = cseq, Type = GameActionType.ContractComplete,
            ContractId = id, FundsReward = f,
        };

        private static GameAction Parameter(string actionId, string id, string param, double ut, long cseq,
            float f, float s, float r) => new GameAction
        {
            ActionId = actionId, UT = ut, CaptureSequence = cseq, Type = GameActionType.ContractParameterComplete,
            ContractId = id, ContractParameterId = param, FundsReward = f, ScienceReward = s, RepReward = r,
        };

        /// <summary>Era from capture 1, and a rewind captured at high water
        /// <paramref name="highWater"/> that loaded the state of UT <paramref name="loadedUT"/>.</summary>
        private static void Rewound(long highWater, double loadedUT, long eraStart = 1)
        {
            StockShadowTakeBack.SetEligibleFromForTesting(eraStart);
            StockShadowTakeBack.AddBreakForTesting(highWater, loadedUT, "PlainRewind");
        }

        private IEnumerable<string> TakeBackWarns => logLines.Where(l =>
            l.Contains("[WARN]") && l.Contains("[" + StockShadowTakeBack.Tag + "]") && l.Contains("Took back"));

        // ------------------------------------------------------------------
        // Pure rules
        // ------------------------------------------------------------------

        [Fact]
        public void IsIdentityReason_IsTrueForTheIdentityRulesOnly()
        {
            foreach (GameActionNotCountedReason r in Enum.GetValues(typeof(GameActionNotCountedReason)))
            {
                bool expected = r != GameActionNotCountedReason.None
                    && r != GameActionNotCountedReason.FacilityAlreadyIntact
                    && r != GameActionNotCountedReason.PurchaseAlreadyOwned;
                Assert.Equal(expected, StockShadowTakeBack.IsIdentityReason(r));
            }
        }

        [Fact]
        public void Era_OnlyRowsCapturedAtOrAboveTheEraStart_AndNeverParsekAuthored()
        {
            Assert.False(StockShadowTakeBack.IsInEra(5, 0));
            Assert.False(StockShadowTakeBack.IsInEra(4, 5));
            Assert.True(StockShadowTakeBack.IsInEra(5, 5));
            Assert.False(StockShadowTakeBack.IsInEra(CaptureSequence.Unsequenced, 5));
            Assert.False(StockShadowTakeBack.IsInEra(0, 5));

            StockShadowTakeBack.SetEligibleFromForTesting(5);
            Assert.True(StockShadowTakeBack.IsEligibleRow(new GameAction { CaptureSequence = 7 }));
            Assert.False(StockShadowTakeBack.IsEligibleRow(new GameAction { CaptureSequence = 4 }));
            Assert.False(StockShadowTakeBack.IsEligibleRow(StockPoolReference.BuildAdjustmentRow(StockPool.Funds, 5.0, 10.0)));
            Assert.False(StockShadowTakeBack.IsEligibleRow(null));
        }

        [Fact]
        public void IsWitnessForgotten_NeedsABreakAfterTheWitnessCaptureBeforeTheRepeatThatLoadedAnEarlierState()
        {
            var breaks = new List<TimelineBreak> { new TimelineBreak { CaptureHighWater = 10, UT = 50, Kind = "t" } };
            var witness = IdentityWitness.At(100, 8, "w");

            Assert.True(StockShadowTakeBack.IsWitnessForgotten(witness, 12, breaks));
            // The repeat was captured before the break: stock still held the witness.
            Assert.False(StockShadowTakeBack.IsWitnessForgotten(witness, 9, breaks));
            // The witness was captured after the break.
            Assert.False(StockShadowTakeBack.IsWitnessForgotten(IdentityWitness.At(100, 11, "w"), 12, breaks));
            // The break loaded a state that already held the witness (loaded UT after it).
            Assert.False(StockShadowTakeBack.IsWitnessForgotten(IdentityWitness.At(40, 8, "w"), 12, breaks));
            // The pre-ledger seed is in every state a rewind can load.
            Assert.False(StockShadowTakeBack.IsWitnessForgotten(IdentityWitness.PreLedgerSeed("w"), 12, breaks));
            // No break: never.
            Assert.False(StockShadowTakeBack.IsWitnessForgotten(witness, 12, new List<TimelineBreak>()));
            // A repeat with no capture number is older than the era rule allows anyway.
            Assert.False(StockShadowTakeBack.IsWitnessForgotten(witness, 0, breaks));
        }

        [Fact]
        public void ShadowCurrencyEffect_ClampsATakenBackPenaltyAtZero_UnlessNegativeCurrencyIsAllowed()
        {
            Assert.Equal(500.0, StockShadowTakeBack.ShadowCurrencyEffect(100, 0, 500, false), 6);
            // A 300 penalty stock charged again from a shadow pool of 100: stock stops at 0.
            Assert.Equal(-100.0, StockShadowTakeBack.ShadowCurrencyEffect(100, 0, -300, false), 6);
            Assert.Equal(-300.0, StockShadowTakeBack.ShadowCurrencyEffect(100, 0, -300, true), 6);
            // Already below 0 through the walk's own effect: the take-back adds nothing more.
            Assert.Equal(-150.0, StockShadowTakeBack.ShadowCurrencyEffect(100, -150, -300, false), 6);
        }

        [Fact]
        public void ResolveShortfall_IsThePartBelowZero_CappedByTheTakeBack()
        {
            Assert.Equal(0.0, StockShadowTakeBack.ResolveShortfall(500, 10));
            Assert.Equal(200.0, StockShadowTakeBack.ResolveShortfall(500, -200));
            Assert.Equal(500.0, StockShadowTakeBack.ResolveShortfall(500, -900));
            Assert.Equal(0.0, StockShadowTakeBack.ResolveShortfall(-500, -900));
            Assert.Equal(0.0, StockShadowTakeBack.ResolveShortfall(0, -900));
        }

        [Fact]
        public void IsTimeTravelLoad_IsEveryLoadThatKeepsTheLedgerAndLoadsAnEarlierStockState()
        {
            var expected = new HashSet<LoadKind>
            {
                LoadKind.PlainRewind, LoadKind.ReFlyStart, LoadKind.DiscardReFly,
                LoadKind.StockRevert, LoadKind.QuickloadFlight,
            };
            foreach (LoadKind k in Enum.GetValues(typeof(LoadKind)))
                Assert.Equal(expected.Contains(k), StockShadowTakeBack.IsTimeTravelLoad(k));
        }

        [Fact]
        public void NoteLoad_RecordsOneBreakPerTimeTravel_AndLogsIt()
        {
            Assert.False(StockShadowTakeBack.NoteLoad(LoadKind.Cold, 50.0, 10));
            Assert.False(StockShadowTakeBack.NoteLoad(LoadKind.InSessionOther, 50.0, 10));
            Assert.Empty(StockShadowTakeBack.Breaks);

            Assert.True(StockShadowTakeBack.NoteLoad(LoadKind.PlainRewind, 50.0, 10));
            Assert.False(StockShadowTakeBack.NoteLoad(LoadKind.PlainRewind, 50.0, 10)); // identical
            Assert.True(StockShadowTakeBack.NoteLoad(LoadKind.StockRevert, 50.0, 14));
            Assert.Equal(2, StockShadowTakeBack.Breaks.Count);
            Assert.Equal("StockRevert", StockShadowTakeBack.Breaks[1].Kind);
            Assert.Contains(logLines, l => l.Contains("[TakeBack]") && l.Contains("Timeline break recorded")
                && l.Contains("kind=PlainRewind") && l.Contains("loadedUT=50") && l.Contains("captureHighWater=10"));

            Assert.False(StockShadowTakeBack.NoteLoad(LoadKind.ReFlyStart, double.NaN, 20));
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("[TakeBack]")
                && l.Contains("Timeline break not recorded for load kind=ReFlyStart"));
        }

        [Fact]
        public void CaptureSequence_StartsTheEraAtItsFirstIssue_Once_AndLogsIt()
        {
            Assert.Equal(0, StockShadowTakeBack.EligibleFromCaptureSequence);
            long first = CaptureSequence.Next();
            CaptureSequence.Next();
            Assert.Equal(first, StockShadowTakeBack.EligibleFromCaptureSequence);
            Assert.Single(logLines.Where(l => l.Contains("[TakeBack]") && l.Contains("Take-back era starts at capture")));
            Assert.Equal(first + 1, CaptureSequence.CurrentHighWater());
        }

        [Fact]
        public void EraAndBreaks_RoundTripThroughTheLedgerNode_AndAnOlderLedgerHasNeither()
        {
            StockShadowTakeBack.SetEligibleFromForTesting(42);
            StockShadowTakeBack.AddBreakForTesting(50, 123.25, "PlainRewind");
            StockShadowTakeBack.AddBreakForTesting(77, 99.5, "StockRevert");
            var node = new ConfigNode("LEDGER");
            StockShadowTakeBack.SaveInto(node);

            StockShadowTakeBack.ResetForTesting();
            StockShadowTakeBack.LoadFrom(node, "test");
            Assert.Equal(42, StockShadowTakeBack.EligibleFromCaptureSequence);
            Assert.Equal(2, StockShadowTakeBack.Breaks.Count);
            Assert.Equal(50, StockShadowTakeBack.Breaks[0].CaptureHighWater);
            Assert.Equal(123.25, StockShadowTakeBack.Breaks[0].UT);
            Assert.Equal("StockRevert", StockShadowTakeBack.Breaks[1].Kind);

            StockShadowTakeBack.LoadFrom(new ConfigNode("LEDGER"), "older");
            Assert.Equal(0, StockShadowTakeBack.EligibleFromCaptureSequence);
            Assert.Empty(StockShadowTakeBack.Breaks);

            var bad = new ConfigNode("LEDGER");
            bad.AddValue(StockShadowTakeBack.TimelineBreakKey, "x y");
            bad.AddValue(StockShadowTakeBack.TimelineBreakKey, "5 60 PlainRewind");
            StockShadowTakeBack.LoadFrom(bad, "bad");
            Assert.Single(StockShadowTakeBack.Breaks);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("badBreaks=1"));
        }

        [Fact]
        public void EraAndBreaks_SurviveTheRealLedgerSaveAndLoad()
        {
            StockShadowTakeBack.SetEligibleFromForTesting(3);
            StockShadowTakeBack.AddBreakForTesting(9, 77.0, "ReFlyStart");
            ConfigNode node = Ledger.BuildSaveNode();
            StockShadowTakeBack.ResetForTesting();
            Assert.True(Ledger.LoadFromNode(node));
            Assert.Equal(3, StockShadowTakeBack.EligibleFromCaptureSequence);
            Assert.Single(StockShadowTakeBack.Breaks);
            Assert.Equal(77.0, StockShadowTakeBack.Breaks[0].UT);
        }

        [Fact]
        public void FormatTakeBack_NamesTheIdentityTheRowAndTheWitness()
        {
            var row = Milestone("b", "Mun/Landing", 120, 20, 5000, 0, 0);
            row.NotCountedReason = GameActionNotCountedReason.MilestoneAlreadyAchieved;
            row.TakeBackWitness = "milestone 'Mun/Landing' at ut=100 cseq=10";
            string line = StockShadowTakeBack.FormatTakeBack(StockPool.Funds, 5000, row, false);
            Assert.StartsWith("Took back 5000 Funds stock paid again for milestone 'Mun/Landing' (row b ut=120 cseq=20 "
                + "reason=MilestoneAlreadyAchieved; already credited by milestone 'Mun/Landing' at ut=100 cseq=10; "
                + "MilestoneAchievement/one-shot key=[body-qualified progress node id]", line);
            Assert.DoesNotContain("authoritative", line);
            Assert.EndsWith("; applied by the authoritative write)",
                StockShadowTakeBack.FormatTakeBack(StockPool.Funds, 5000, row, true));
        }

        [Fact]
        public void StockShadowStart_IsTheReferencesLastWrite_PlusRowsStockPaidSinceThatWereCapturedBefore()
        {
            var start = new StockShadowStart(StockPool.Funds);
            start.Reset(null);
            Assert.False(start.Known);
            Assert.Equal(123.0, start.Before(20, 123.0), 6); // no reference: the walk's value

            var state = new StockPoolReferenceState(StockPool.Funds)
            {
                Initialized = true, LastWritten = 7000.0, LastPatchUT = 70.0,
            };
            state.Counted["walked"] = new CountedRow { Effect = 10000.0 };
            state.Counted["future"] = new CountedRow { Effect = double.NaN };
            start.Reset(state);
            Assert.True(start.Known);
            start.Note(new GameAction { ActionId = "walked", UT = 60, CaptureSequence = 9,
                Type = GameActionType.FundsEarning }, 10000.0);          // in LastWritten
            start.Note(new GameAction { ActionId = "future", UT = 100, CaptureSequence = 10,
                Type = GameActionType.FundsEarning }, -3000.0);          // stock never applied it
            start.Note(new GameAction { ActionId = "live", UT = 110, CaptureSequence = 15,
                Type = GameActionType.FundsEarning }, 500.0);            // paid live since
            start.Note(new GameAction { ActionId = "later", UT = 115, CaptureSequence = 25,
                Type = GameActionType.FundsEarning }, 900.0);            // captured after the repeat
            start.Note(new GameAction { ActionId = "parsek", UT = 112, CaptureSequence = 16,
                Type = GameActionType.FundsEarning, ParsekAuthored = true }, 50.0); // the write's
            start.Note(new GameAction { ActionId = "seed", UT = 0, CaptureSequence = 1,
                Type = GameActionType.FundsInitial }, 1000.0);           // not a pool row
            Assert.Equal(2, start.AppliedCount);
            Assert.Equal(7500.0, start.Before(20, 0.0), 6);
            Assert.Equal(8400.0, start.Before(30, 0.0), 6);
        }

        /// <summary>
        /// The review's reputation input: a committed row (y) crossed the clock at a measured
        /// patch after the rewind and the write put it into stock's pool; the repeat's curve
        /// runs at that reputation, not at the walk's minus every forgotten row.
        /// </summary>
        [Fact]
        public void ReputationShadow_StartsFromThePoolTheLastWriteLeft_NotFromZero()
        {
            Rewound(10, 50);
            Seeds(0, 0, 0);
            ledger.Add(Milestone("y", "Minmus/Flyby", 60, 9, 0, 300, 0));
            ledger.Add(Milestone("a", "Mun/Landing", 100, 10, 0, 40, 0));
            Assert.Equal(0.0, PatchReputation(50, 0, authoritative: true), 4);
            double afterY = PatchReputation(70, 0);
            Assert.Equal((double)ReputationModule.ApplyReputationCurve(300f, 0f).newRep, afterY, 3);

            GameAction repeat = Milestone("b", "Mun/Landing", 120, 20, 0, 40, 0);
            ledger.Add(repeat);
            float stockPaid = ReputationModule.ApplyReputationCurve(40f, (float)afterY).actualDelta;
            double write = PatchReputation(130, afterY + stockPaid);

            Assert.Equal((double)stockPaid, lastMeasurement.TakenBackTotal, 3);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(0, AdjustmentRows);
            Walk(130);
            Assert.Equal((double)reputation.GetRunningRep(), write, 3);
        }

        /// <summary>
        /// The review's funds input: the measured write after the rewind left stock's pool at
        /// the projected minimum (7000, the committed fail reserved). A repeat fail stock charged
        /// live is given back in full from there, not clamped at a shadow of 0.
        /// </summary>
        [Fact]
        public void PenaltyGiveBack_StartsFromThePoolTheLastWriteLeft_NotClampedAtZero()
        {
            Rewound(10, 50);
            Seeds(0, 0, 0);
            ledger.Add(Milestone("y", "Minmus/Flyby", 60, 9, 10000, 0, 0));
            ledger.Add(Accept("c1", 10, 5));
            ledger.Add(new GameAction
            {
                ActionId = "f1", UT = 100, CaptureSequence = 10, Type = GameActionType.ContractFail,
                ContractId = "c1", FundsPenalty = 3000,
            });
            Assert.Equal(0.0, PatchFunds(50, 0, authoritative: true), 6);
            Assert.Equal(7000.0, PatchFunds(70, 0), 6);

            GameAction repeat = new GameAction
            {
                ActionId = "f2", UT = 120, CaptureSequence = 20, Type = GameActionType.ContractFail,
                ContractId = "c1", FundsPenalty = 3000,
            };
            ledger.Add(repeat);
            double write = PatchFunds(130, 4000);

            Assert.Equal(-3000f, repeat.TakeBackFunds);
            Assert.Equal(-3000.0, lastMeasurement.TakenBackTotal, 6);
            Assert.Equal(0, AdjustmentRows);
            Assert.Equal(7000.0, write, 6);
        }

        // ------------------------------------------------------------------
        // Never rewound: identical to before
        // ------------------------------------------------------------------

        /// <summary>A career with a repeat of every identity kind, each paid by stock by its own
        /// rule (no rewind): the run with the take-back era on and the run with it off end in
        /// the same pools, the same adjustments, and no row carries a take-back.</summary>
        private List<double> NeverRewoundCareer(bool eraOn)
        {
            ledger.Clear();
            logLines.Clear();
            StockPoolReference.ResetForTesting();
            StockShadowTakeBack.ResetForTesting();
            if (eraOn)
                StockShadowTakeBack.SetEligibleFromForTesting(1);

            Seeds(1000, 0, 0);
            var writes = new List<double>();
            writes.Add(PatchFunds(1, 1000));
            ledger.Add(Milestone("a", "Mun/Landing", 100, 10, 5000, 0, 0));
            ledger.Add(Accept("k", 110, 11));
            ledger.Add(ContractDone("kc", "k", 150, 13, 800));
            // Stock's own repeats (a mod parameter that pays after completion, a duplicate
            // milestone event): stock paid them, the walk does not count them.
            ledger.Add(Parameter("kp", "k", "0", 160, 14, 300, 0, 0));
            ledger.Add(Milestone("a2", "Mun/Landing", 170, 15, 5000, 0, 0));
            writes.Add(PatchFunds(200, 1000 + 5000 + 800 + 300 + 5000));
            writes.Add(Adjusted(StockPool.Funds));
            writes.Add(AdjustmentRows);
            Assert.All(ledger, a => Assert.False(a.HasTakeBack));
            Assert.Empty(TakeBackWarns);
            return writes;
        }

        [Fact]
        public void NeverRewound_EveryRepeatStockPaidByItsOwnRule_EndsExactlyAsBefore()
        {
            List<double> before = NeverRewoundCareer(eraOn: false);
            List<double> after = NeverRewoundCareer(eraOn: true);
            Assert.Equal(before, after);
            // As before: the repeats stock paid are kept (the reference keeps the payout).
            Assert.Equal(5300.0, before[2], 6);
        }

        [Fact]
        public void NeverRewound_ScienceOverTheCap_IsKeptAsBefore()
        {
            StockShadowTakeBack.SetEligibleFromForTesting(1);
            Seeds(0, 0, 0);
            ledger.Add(Science("s1", "crew@KerbinSrfLanded", 100, 10, 8, 10));
            ledger.Add(Science("s2", "crew@KerbinSrfLanded", 120, 12, 8, 10));
            PatchScience(130, 16);
            Assert.All(ledger, a => Assert.False(a.HasTakeBack));
            Assert.Equal(6.0, Adjusted(StockPool.Science), 3);
        }

        // ------------------------------------------------------------------
        // Rewind duplicates: taken back
        // ------------------------------------------------------------------

        /// <summary>
        /// The committed timeline earned Mun/Landing at UT 100 (captured 10). The player rewinds
        /// to UT 50 (break at high water 10), the reference re-baselines on the loaded state, and
        /// a new flight earns Mun/Landing again at UT 120 (captured 20): stock pays it again.
        /// </summary>
        private void WorldFirstRewind(float f, float r, float s)
        {
            Rewound(10, 50);
            Seeds(1000, 0, 0);
            ledger.Add(Milestone("a", "Mun/Landing", 100, 10, f, r, s));
            ledger.Add(Milestone("b", "Mun/Landing", 120, 20, f, r, s));
        }

        [Fact]
        public void Rewind_WorldFirstPaidAgain_FundsAreTakenBack_NoAdjustmentRow_OneWarn()
        {
            WorldFirstRewind(5000, 0, 0);
            ledger.RemoveAt(ledger.Count - 1);
            GameAction repeat = Milestone("b", "Mun/Landing", 120, 20, 5000, 0, 0);
            // After the rewind: stock's loaded state is the seed; the committed row is ahead.
            Assert.Equal(1000.0, PatchFunds(50, 1000, authoritative: true), 6);

            ledger.Add(repeat);
            double write = PatchFunds(130, 1000 + 5000);

            Assert.Equal(GameActionNotCountedReason.MilestoneAlreadyAchieved, repeat.NotCountedReason);
            Assert.Equal(5000f, repeat.TakeBackFunds);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(0, AdjustmentRows);
            Assert.Equal(6000.0, write, 6); // credited once
            Assert.Single(lastMeasurement.TakenBack);
            Assert.Equal(5000.0, lastMeasurement.TakenBackTotal, 6);
            string warn = Assert.Single(TakeBackWarns);
            Assert.Contains("Took back 5000 Funds stock paid again for milestone 'Mun/Landing'", warn);
            Assert.Contains("already credited by milestone 'Mun/Landing' at ut=100 cseq=10", warn);

            // The next update keeps it: the row was stepped once.
            Assert.Equal(6000.0, PatchFunds(140, 6000), 6);
            Assert.Empty(lastMeasurement.TakenBack);
            Assert.Equal(0, AdjustmentRows);
        }

        /// <summary>A scene change: the scenario saves the reference and the next scene's OnLoad reads it back.</summary>
        private static void SceneChange(string kind = "InSessionOther")
        {
            var node = new ConfigNode("SCENARIO");
            StockPoolReference.SaveTo(node);
            StockPoolReference.ResetForTesting();
            StockPoolReference.LoadFrom(node, kind);
        }

        [Fact]
        public void TakeBack_SurvivesEverySceneChange_FiresOnce_NoLeakRow()
        {
            // RW-20 (TAKE-BACK-REFIRES-AFTER-EVERY-LOAD): the repeat's walk effect is 0, so its
            // counted row is {0, 0}. The save used to drop such a row; after the load the repeat
            // read as new and was taken back again, the missing payment filed as a leak.
            WorldFirstRewind(38000, 0, 0);
            GameAction repeat = ledger[ledger.Count - 1];
            ledger.RemoveAt(ledger.Count - 1);
            Assert.Equal(1000.0, PatchFunds(50, 1000, authoritative: true), 6);
            ledger.Add(repeat);
            Assert.Equal(39000.0, PatchFunds(130, 1000 + 38000), 6);
            Assert.Single(lastMeasurement.TakenBack);

            double pool = 39000.0;
            for (int i = 0; i < 6; i++)
            {
                SceneChange();
                Assert.True(StockPoolReference.Funds.Counted.ContainsKey("b"));
                pool = PatchFunds(140 + i, pool);
                Assert.Equal(39000.0, pool, 6);
                Assert.Empty(lastMeasurement.TakenBack);
                Assert.False(lastMeasurement.LeakKept);
            }
            Assert.Equal(0, AdjustmentRows);
            Assert.Single(TakeBackWarns);
            Assert.Contains(logLines, l => l.Contains("[StockPoolReference]") && l.Contains("takenBackRows=1"));
        }

        [Fact]
        public void TakeBack_SurvivesSceneChanges_InEveryPool()
        {
            WorldFirstRewind(38000, 4, 2);
            GameAction repeat = ledger[ledger.Count - 1];
            ledger.RemoveAt(ledger.Count - 1);
            PatchFunds(50, 1000, authoritative: true);
            PatchScience(50, 0, authoritative: true);
            PatchReputation(50, 0, authoritative: true);
            ledger.Add(repeat);
            float repPaid = ReputationModule.ApplyReputationCurve(4f, 0f).actualDelta;
            double f = PatchFunds(130, 39000);
            double s = PatchScience(130, 2);
            double r = PatchReputation(130, repPaid);
            Assert.Equal(3, TakeBackWarns.Count());

            for (int i = 0; i < 3; i++)
            {
                SceneChange();
                Assert.Equal(f, PatchFunds(140 + i, f), 6);
                Assert.Empty(lastMeasurement.TakenBack);
                Assert.Equal(s, PatchScience(140 + i, s), 6);
                Assert.Empty(lastMeasurement.TakenBack);
                Assert.Equal(r, PatchReputation(140 + i, r), 4);
                Assert.Empty(lastMeasurement.TakenBack);
            }
            Assert.Equal(0, AdjustmentRows);
            Assert.Equal(3, TakeBackWarns.Count());
        }

        [Fact]
        public void TakeBack_ARepeatPaidBeforeASceneChange_IsStillTakenBackOnceAfterIt()
        {
            // Mirror: stock paid the repeat, the scene changed before any patch counted it. The
            // first take-back must still fire after the load, and only once.
            WorldFirstRewind(38000, 0, 0);
            GameAction repeat = ledger[ledger.Count - 1];
            ledger.RemoveAt(ledger.Count - 1);
            PatchFunds(50, 1000, authoritative: true);
            ledger.Add(repeat);
            SceneChange();
            Assert.Equal(39000.0, PatchFunds(130, 39000), 6);
            Assert.Single(lastMeasurement.TakenBack);
            SceneChange();
            Assert.Equal(39000.0, PatchFunds(140, 39000), 6);
            Assert.Empty(lastMeasurement.TakenBack);
            Assert.Equal(0, AdjustmentRows);
            Assert.Single(TakeBackWarns);
        }

        [Fact]
        public void TakeBack_ACountedRowLostAnotherWay_IsRefusedNotTakenBackTwice_AndWarns()
        {
            // The hard check: the reference keeps its own record of what it took back, apart from
            // the counted rows, so a counted row lost any other way (a row that left the
            // effective ledger and came back, a re-init) cannot take the same payment twice.
            WorldFirstRewind(38000, 0, 0);
            GameAction repeat = ledger[ledger.Count - 1];
            ledger.RemoveAt(ledger.Count - 1);
            PatchFunds(50, 1000, authoritative: true);
            ledger.Add(repeat);
            PatchFunds(130, 39000);
            Assert.Equal(38000.0, StockPoolReference.Funds.TakenBack["b"], 6);

            StockPoolReference.Funds.Counted.Remove("b");
            Assert.Equal(39000.0, PatchFunds(140, 39000), 6);
            Assert.Empty(lastMeasurement.TakenBack);
            Assert.Single(lastMeasurement.TakeBackRepeatsRefused);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(0, AdjustmentRows);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("[" + StockShadowTakeBack.Tag + "]")
                && l.Contains("Refused a second take-back of 38000 Funds") && l.Contains("row b"));

            // An authoritative patch refuses it the same way.
            StockPoolReference.Funds.Counted.Remove("b");
            Assert.Equal(39000.0, PatchFunds(150, 39000, authoritative: true), 6);
            Assert.Empty(lastMeasurement.TakenBack);
            Assert.Single(lastMeasurement.TakeBackRepeatsRefused);
            Assert.Single(TakeBackWarns);
        }

        [Fact]
        public void TakeBackFoundUnpaid_IsTheSignatureOfALostMemory_NotOfAFirstTakeBack()
        {
            WorldFirstRewind(38000, 0, 0);
            GameAction repeat = ledger[ledger.Count - 1];
            ledger.RemoveAt(ledger.Count - 1);
            PatchFunds(50, 1000, authoritative: true);
            ledger.Add(repeat);
            PatchFunds(130, 39000);
            Assert.False(StockPoolReference.IsTakeBackFoundUnpaid(lastMeasurement, 0.01));

            // The pre-fix state after a load: neither the counted row nor the taken-back fact.
            StockPoolReference.Funds.Counted.Remove("b");
            StockPoolReference.Funds.TakenBack.Remove("b");
            double write = PatchFunds(140, 39000);
            Assert.True(lastMeasurement.LeakKept);
            Assert.Equal(-38000.0, lastMeasurement.Leak, 6);
            Assert.True(StockPoolReference.IsTakeBackFoundUnpaid(lastMeasurement, 0.01));
            Assert.Equal(1000.0, write, 6); // the RW-20 loss this signature names
        }

        [Fact]
        public void TakeBack_ARowGoneFromTheLedger_IsForgottenAtTheNextCommit()
        {
            WorldFirstRewind(38000, 0, 0);
            GameAction repeat = ledger[ledger.Count - 1];
            ledger.RemoveAt(ledger.Count - 1);
            PatchFunds(50, 1000, authoritative: true);
            ledger.Add(repeat);
            PatchFunds(130, 39000);
            Assert.Single(StockPoolReference.Funds.TakenBack);

            ledger.Remove(repeat);
            PatchFunds(140, 39000);
            Assert.Empty(StockPoolReference.Funds.TakenBack);
            Assert.Equal(0, AdjustmentRows); // its stepped part was 0: nothing to carry
        }

        [Fact]
        public void TakeBack_ARewindToASaveFromBeforeIt_ForgetsIt_WithTheCountedRows()
        {
            // The memory is the save's: a load of a save from before the take-back carries
            // neither the counted row nor the taken-back fact.
            WorldFirstRewind(38000, 0, 0);
            GameAction repeat = ledger[ledger.Count - 1];
            ledger.RemoveAt(ledger.Count - 1);
            PatchFunds(50, 1000, authoritative: true);
            var before = new ConfigNode("SCENARIO");
            StockPoolReference.SaveTo(before);
            ledger.Add(repeat);
            PatchFunds(130, 39000);
            Assert.Single(StockPoolReference.Funds.TakenBack);

            StockPoolReference.LoadFrom(before, "PlainRewind");
            Assert.Empty(StockPoolReference.Funds.TakenBack);
            Assert.False(StockPoolReference.Funds.Counted.ContainsKey("b"));
        }

        [Fact]
        public void Rewind_WorldFirstPaidAgain_WithoutTakeBack_WouldBePaidTwice()
        {
            // The same career as the cell above, before the era: the leak is kept.
            WorldFirstRewind(5000, 0, 0);
            StockShadowTakeBack.SetEligibleFromForTesting(1000);
            GameAction repeat = ledger[ledger.Count - 1];
            ledger.RemoveAt(ledger.Count - 1);
            PatchFunds(50, 1000, authoritative: true);
            ledger.Add(repeat);
            double write = PatchFunds(130, 6000);
            Assert.False(repeat.HasTakeBack);
            Assert.Equal(11000.0, write, 6);
            Assert.Equal(1, AdjustmentRows);
            Assert.Empty(TakeBackWarns);
        }

        [Fact]
        public void Rewind_WorldFirstPaidAgain_ScienceIsTakenBack()
        {
            WorldFirstRewind(0, 0, 4);
            GameAction repeat = ledger[ledger.Count - 1];
            ledger.RemoveAt(ledger.Count - 1);
            Assert.Equal(0.0, PatchScience(50, 0, authoritative: true), 6);
            ledger.Add(repeat);
            double write = PatchScience(130, 4);
            Assert.Equal(4.0, write, 6);
            Assert.Equal(0, AdjustmentRows);
            Assert.Contains(TakeBackWarns, l => l.Contains("Took back 4 Science"));
        }

        [Fact]
        public void Rewind_WorldFirstPaidAgain_ReputationIsTakenBackThroughTheCurve()
        {
            WorldFirstRewind(0, 40, 0);
            GameAction repeat = ledger[ledger.Count - 1];
            ledger.RemoveAt(ledger.Count - 1);
            Assert.Equal(0.0, PatchReputation(50, 0, authoritative: true), 6);
            ledger.Add(repeat);

            // Stock paid the repeat through its curve at its own reputation (0: the committed
            // credit crossed the clock while the patch was deferred, stock never had it).
            float stockPaid = ReputationModule.ApplyReputationCurve(40f, 0f).actualDelta;
            double write = PatchReputation(130, stockPaid);

            Walk(130);
            Assert.Equal((double)reputation.GetRunningRep(), write, 4);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(0, AdjustmentRows);
            Assert.Equal((double)stockPaid, lastMeasurement.TakenBackTotal, 4);
            Assert.Contains(TakeBackWarns, l => l.Contains("Reputation stock paid again for milestone 'Mun/Landing'"));
        }

        [Fact]
        public void Rewind_ReputationTakeBack_WhenStockAlreadyHeldEarlierCredit_UsesTheCurveAtThatReputation()
        {
            // An earlier row captured after the break (stock paid it live) raises stock's own
            // reputation; the repeat's curve runs at that higher value.
            Rewound(10, 50);
            Seeds(0, 0, 200);
            ledger.Add(Milestone("a", "Mun/Landing", 100, 10, 0, 40, 0));
            Assert.Equal(200.0, PatchReputation(50, 200, authoritative: true), 4);
            // Earned live after the rewind, before the committed row's UT: in stock and walk alike.
            ledger.Add(Milestone("m", "Minmus/Landing", 60, 15, 0, 60, 0));
            ledger.Add(Milestone("b", "Mun/Landing", 120, 20, 0, 40, 0));
            float afterM = ReputationModule.ApplyReputationCurve(60f, 200f).newRep;
            float live = ReputationModule.ApplyReputationCurve(40f, afterM).newRep;

            double write = PatchReputation(130, live);
            Walk(130);
            Assert.Equal((double)reputation.GetRunningRep(), write, 4);
            Assert.False(lastMeasurement.LeakKept);
            Assert.Equal(0, AdjustmentRows);
        }

        [Fact]
        public void Rewind_ScienceOverTheCap_ThePartTheForgottenCreditCoversIsTakenBack()
        {
            Rewound(10, 50);
            Seeds(0, 0, 0);
            ledger.Add(Science("s1", "crew@MunSrfLanded", 100, 10, 8, 10));
            Assert.Equal(0.0, PatchScience(50, 0, authoritative: true), 6);
            // Stock's loaded subject has no science: it pays the full 8 again.
            GameAction repeat = Science("s2", "crew@MunSrfLanded", 120, 20, 8, 10);
            ledger.Add(repeat);
            double write = PatchScience(130, 8);

            Assert.Equal(2.0, (double)repeat.EffectiveScience, 3);
            Assert.Equal(6.0, (double)repeat.TakeBackScience, 3);
            Assert.Equal(10.0, write, 3); // the subject's cap, once
            Assert.Equal(0, AdjustmentRows);
            Assert.Contains(TakeBackWarns, l => l.Contains("Took back 6 Science stock paid again for science subject 'crew@MunSrfLanded'"));
        }

        [Fact]
        public void Rewind_RecordThresholdPaidAgain_ThatThresholdIsTakenBack()
        {
            Rewound(10, 50);
            Seeds(0, 0, 0);
            ledger.Add(Record("r1", "RecordsAltitude", 100, 10, 1, 2));
            Assert.Equal(0.0, PatchFunds(50, 0, authoritative: true), 6);
            // Stock's band was lowered by the rewind: it pays thresholds 1, 2 again and 3 new.
            GameAction repeat = Record("r2", "RecordsAltitude", 120, 20, 1, 2, 3);
            ledger.Add(repeat);
            double write = PatchFunds(130, 6000);

            Assert.Equal(3000f, repeat.TakeBackFunds);
            Assert.Equal(6000.0, write, 6); // 1000 + 2000 + 3000, each once
            Assert.Equal(0, AdjustmentRows);
            Assert.Contains(TakeBackWarns, l => l.Contains("Took back 3000 Funds") && l.Contains("RecordsAltitude"));
        }

        [Fact]
        public void RecordThreshold_PaidAsNewTwice_IsTakenBack_WithoutARecordedBreak()
        {
            // The second first payment of an interval is step 4's own rewind evidence: stock's
            // band was lowered between the two payments, which one timeline never does.
            StockShadowTakeBack.SetEligibleFromForTesting(1);
            Seeds(0, 0, 0);
            ledger.Add(Record("r1", "RecordsAltitude", 100, 10, 1, 2));
            Assert.Equal(0.0, PatchFunds(50, 0, authoritative: true), 6);
            GameAction repeat = Record("r2", "RecordsAltitude", 120, 20, 1, 2, 3);
            ledger.Add(repeat);
            Assert.Equal(6000.0, PatchFunds(130, 6000), 6);
            Assert.Equal(3000f, repeat.TakeBackFunds);
            Assert.Contains("threshold 1 first paid by cseq=10", repeat.TakeBackWitness);
            Assert.Equal(0, AdjustmentRows);
        }

        [Fact]
        public void RecordRePayment_IsNeverTakenBack_EvenWhenItsCrossingWas()
        {
            Rewound(10, 50);
            Seeds(0, 0, 0);
            ledger.Add(Record("r1", "RecordsAltitude", 100, 10, 1, 2));
            PatchFunds(50, 0, authoritative: true);
            GameAction second = Record("r2", "RecordsAltitude", 120, 20, 1, 2);
            // Stock's loop then re-pays interval 2 on that timeline (a re-payment, "2r").
            GameAction repay = RecordBand("r3", "RecordsAltitude", 125, 30, 1,
                new RecordThresholdCredit(2, 2000f, 0f, 0f, repay: true));
            ledger.Add(second);
            ledger.Add(repay);
            double write = PatchFunds(130, 5000);

            Assert.Equal(3000f, second.TakeBackFunds);
            Assert.False(repay.HasTakeBack);
            Assert.Equal(GameActionNotCountedReason.RecordThresholdsAlreadyCredited, repay.NotCountedReason);
            // The re-payment stock paid on its own timeline is kept, as before step 5.
            Assert.Equal(1, AdjustmentRows);
            Assert.Equal(2000.0, Adjusted(StockPool.Funds), 6);
            Assert.Equal(5000.0, write, 6);
        }

        [Fact]
        public void RecordRePayments_FoldedIntoTheTopEntry_OnlyItsOnePaymentIsTakenBack()
        {
            Rewound(10, 50);
            Seeds(0, 0, 0);
            ledger.Add(Record("r1", "RecordsAltitude", 100, 10, 1, 2));
            PatchFunds(50, 0, authoritative: true);
            // Four awards: interval 1 once, interval 2 first paid then re-paid twice, all folded
            // into the entry for 2 (6000 = three payments of 2000).
            GameAction repeat = RecordBand("r2", "RecordsAltitude", 120, 20, 4,
                new RecordThresholdCredit(1, 1000f, 0f, 0f),
                new RecordThresholdCredit(2, 6000f, 0f, 0f));
            ledger.Add(repeat);
            PatchFunds(130, 7000);

            Assert.Equal(3000f, repeat.TakeBackFunds); // 1000 + 6000 / 3
            Assert.Equal(1, AdjustmentRows);           // the two re-payments, kept
            Assert.Equal(4000.0, Adjusted(StockPool.Funds), 6);
            Assert.Contains(TakeBackWarns, l => l.Contains("Took back 3000 Funds"));
        }

        [Fact]
        public void RecordRow_ZeroedBecauseALaterCaptureWalkedEarlier_IsNotTakenBack()
        {
            // The new flight crosses the thresholds BEFORE the committed row's UT: the new row is
            // the credit, the committed one was paid on its own timeline.
            Rewound(10, 50);
            Seeds(0, 0, 0);
            GameAction committed = Record("r1", "RecordsAltitude", 100, 10, 1, 2);
            ledger.Add(committed);
            PatchFunds(50, 0, authoritative: true);
            GameAction earlier = Record("r2", "RecordsAltitude", 80, 20, 1, 2);
            ledger.Add(earlier);
            PatchFunds(130, 3000);

            Assert.Equal(GameActionNotCountedReason.RecordThresholdsAlreadyCredited, committed.NotCountedReason);
            Assert.True(earlier.Effective);
            Assert.False(committed.HasTakeBack);
            Assert.False(earlier.HasTakeBack);
            Assert.Equal(0, AdjustmentRows);
            Assert.Empty(TakeBackWarns);
        }

        [Fact]
        public void RecordThreshold_AtOrBelowThePreLedgerSeed_IsNotTakenBack()
        {
            StockShadowTakeBack.SetEligibleFromForTesting(1);
            milestones.SetPreLedgerProgressSeed(PreLedgerProgressSeed.CreateCaptured(
                null, new[] { new KeyValuePair<string, int>("RecordsAltitude", 2) }));
            Seeds(0, 0, 0);
            GameAction row = Record("r1", "RecordsAltitude", 100, 10, 1, 2, 3);
            ledger.Add(row);
            Walk(130);
            Assert.True(row.RecordPartlyCredited);
            Assert.False(row.HasTakeBack);
        }

        [Fact]
        public void ResolveRecordTakeBack_DividesOnlyTheTopEntry_AndSkipsAnInconsistentCount()
        {
            var book = new RecordThresholdBook();
            book.Reset(null);
            book.Credit(Record("r1", "RecordsAltitude", 100, 10, 1, 2, 3));
            // Five awards over three intervals: 1 and 2 once, 3 first paid then re-paid twice,
            // folded into its entry (9000 = three payments of 3000).
            GameAction row = RecordBand("r2", "RecordsAltitude", 120, 20, 5,
                new RecordThresholdCredit(1, 1000f, 1f, 0f),
                new RecordThresholdCredit(2, 2000f, 2f, 0f),
                new RecordThresholdCredit(3, 9000f, 6f, 3f));
            RecordThresholdBook.RowCredit credit = book.Credit(row);
            Assert.Equal(new[] { 1, 2, 3 }, credit.SecondFirstPayments.Select(p => p.Position).ToArray());
            Assert.All(credit.SecondFirstPayments, p => Assert.Equal(10L, p.HolderCaptureSequence));

            MilestonesModule.RecordThresholdTakeBack t =
                MilestonesModule.ResolveRecordTakeBack(row, credit, out string skip);
            Assert.Null(skip);
            Assert.Equal(6000f, t.Funds); // 1000 + 2000 + 9000 / 3
            Assert.Equal(5f, t.Rep);      // 1 + 2 + 6 / 3
            Assert.Equal(1f, t.Science);

            // Fewer awards than named intervals: the row is inconsistent, nothing is taken back.
            row.MilestoneRecordThresholds = 2;
            t = MilestonesModule.ResolveRecordTakeBack(row, credit, out skip);
            Assert.NotNull(skip);
            Assert.Equal(0f, t.Funds);
        }

        [Fact]
        public void Rewind_ParameterAfterTheContractCompleted_IsTakenBack_InFundsScienceAndRep()
        {
            Rewound(13, 50);
            Seeds(0, 0, 0);
            ledger.Add(Accept("k", 20, 5));
            ledger.Add(ContractDone("kc", "k", 100, 13, 800));
            PatchFunds(50, 0, authoritative: true);
            PatchScience(50, 0, authoritative: true);
            PatchReputation(50, 0, authoritative: true);

            // The rewound contract is open again in stock: a parameter pays.
            GameAction repeat = Parameter("kp", "k", "0", 120, 20, 300, 2, 5);
            ledger.Add(repeat);
            float repPaid = ReputationModule.ApplyReputationCurve(5f, 0f).actualDelta;

            Assert.Equal(800.0, PatchFunds(130, 300), 6);
            Assert.Equal(GameActionNotCountedReason.ContractParameterAfterCompletion, repeat.NotCountedReason);
            Assert.Equal(0.0, PatchScience(130, 2), 6);
            Walk(130);
            Assert.Equal((double)reputation.GetRunningRep(), PatchReputation(130, repPaid), 4);
            Assert.Equal(0, AdjustmentRows);
            Assert.Equal(3, TakeBackWarns.Count(l => l.Contains("parameter '0' of contract 'k'")));
        }

        [Fact]
        public void Rewind_ContractCompletedAgain_IsTakenBack()
        {
            Rewound(13, 50);
            Seeds(0, 0, 0);
            ledger.Add(Accept("k", 20, 5));
            ledger.Add(ContractDone("kc", "k", 100, 13, 800));
            PatchFunds(50, 0, authoritative: true);
            GameAction repeat = ContractDone("kc2", "k", 120, 20, 800);
            ledger.Add(repeat);
            Assert.Equal(800.0, PatchFunds(130, 800), 6);
            Assert.Equal(800f, repeat.TakeBackFunds);
            Assert.Equal(0, AdjustmentRows);
        }

        // ------------------------------------------------------------------
        // Exemptions
        // ------------------------------------------------------------------

        [Fact]
        public void OldRow_CapturedBeforeTheEra_IsNeverTakenBack()
        {
            WorldFirstRewind(5000, 0, 0);
            StockShadowTakeBack.SetEligibleFromForTesting(21); // the repeat (20) predates the era
            GameAction repeat = ledger[ledger.Count - 1];
            ledger.RemoveAt(ledger.Count - 1);
            PatchFunds(50, 1000, authoritative: true);
            ledger.Add(repeat);
            PatchFunds(130, 6000);
            Assert.False(repeat.HasTakeBack);
            Assert.Empty(TakeBackWarns);
            Assert.Equal(1, AdjustmentRows); // kept as before
        }

        [Fact]
        public void Repeat_WhoseWitnessTheLoadedStateHeld_IsNotTakenBack()
        {
            // The rewind loaded UT 110, after the committed credit at 100: stock held it, so a
            // repeat stock pays is stock's own rule and is kept as before.
            Rewound(10, 110);
            Seeds(1000, 0, 0);
            ledger.Add(Milestone("a", "Mun/Landing", 100, 10, 5000, 0, 0));
            PatchFunds(110, 6000, authoritative: true);
            GameAction repeat = Milestone("b", "Mun/Landing", 120, 20, 5000, 0, 0);
            ledger.Add(repeat);
            PatchFunds(130, 11000);
            Assert.False(repeat.HasTakeBack);
            Assert.Empty(TakeBackWarns);
        }

        [Fact]
        public void EarlierReEarning_CreditsTheNewRow_AndTakesNothingBack()
        {
            // The new flight earns the node BEFORE the committed row's UT: the new row is the
            // credit, the committed one the repeat, and stock paid only the new one.
            Rewound(10, 50);
            Seeds(1000, 0, 0);
            GameAction committed = Milestone("a", "Mun/Landing", 100, 10, 5000, 0, 0);
            ledger.Add(committed);
            PatchFunds(50, 1000, authoritative: true);
            ledger.Add(Milestone("b", "Mun/Landing", 80, 20, 5000, 0, 0));
            Assert.Equal(6000.0, PatchFunds(90, 6000), 6);
            Assert.Equal(6000.0, PatchFunds(130, 6000), 6);
            Assert.False(committed.HasTakeBack);
            Assert.Equal(0, AdjustmentRows);
            Assert.Empty(TakeBackWarns);
        }

        [Fact]
        public void PenaltyChargedAgain_ForAContractTheTimelineEnded_IsGivenBack()
        {
            Rewound(13, 50);
            Seeds(1000, 0, 0);
            ledger.Add(Accept("k", 20, 5));
            ledger.Add(new GameAction
            {
                ActionId = "kf", UT = 100, CaptureSequence = 13, Type = GameActionType.ContractFail,
                ContractId = "k", FundsPenalty = 300,
            });
            // The committed fail's penalty is reserved at the rewind's write.
            Assert.Equal(700.0, PatchFunds(50, 1000, authoritative: true), 6);
            GameAction repeat = new GameAction
            {
                ActionId = "kf2", UT = 120, CaptureSequence = 20, Type = GameActionType.ContractCancel,
                ContractId = "k", FundsPenalty = 300,
            };
            ledger.Add(repeat);
            // Stock charged the cancel's 300 live; the walk charges the fail's 300 once.
            Assert.Equal(700.0, PatchFunds(130, 400), 6);
            Assert.Equal(-300f, repeat.TakeBackFunds);
            Assert.Equal(0, AdjustmentRows);
            Assert.Contains(TakeBackWarns, l => l.Contains("Took back -300 Funds"));
        }

        [Fact]
        public void CompletionPastTheWalksDeadline_IsADeadlineDisagreement_NeverTakenBack()
        {
            // A break after the accept and before the deadline: stock still holds the contract
            // and its own deadline, so a completion it paid past the walk's deadline is not a
            // forgotten outcome.
            Rewound(30, 150);
            Seeds(0, 0, 0);
            ledger.Add(new GameAction
            {
                ActionId = "acc-k", UT = 10, CaptureSequence = 5, Type = GameActionType.ContractAccept,
                ContractId = "k", DeadlineUT = 200,
            });
            GameAction late = ContractDone("kc", "k", 250, 40, 800);
            ledger.Add(late);
            Walk(300);
            Assert.Equal(GameActionNotCountedReason.ContractDeadlinePassed, late.NotCountedReason);
            Assert.False(late.HasTakeBack);
        }

        [Fact]
        public void CompletionPastTheDeadline_AfterARecordedDeadlineFail_IsNeverTakenBack()
        {
            // Stock recorded the fail past the walk's deadline; a completion after it is paid
            // by a stock whose own deadline is later than the walk's, never because a rewind
            // made it forget the fail.
            Rewound(30, 150);
            Seeds(0, 0, 0);
            ledger.Add(new GameAction
            {
                ActionId = "acc-k", UT = 10, CaptureSequence = 5, Type = GameActionType.ContractAccept,
                ContractId = "k", DeadlineUT = 200,
            });
            ledger.Add(new GameAction
            {
                ActionId = "kf", UT = 210, CaptureSequence = 10, Type = GameActionType.ContractFail,
                ContractId = "k", FundsPenalty = 300,
            });
            GameAction late = ContractDone("kc", "k", 250, 40, 800);
            ledger.Add(late);
            Walk(300);
            Assert.Equal(GameActionNotCountedReason.ContractDeadlinePassed, late.NotCountedReason);
            Assert.False(late.HasTakeBack);
        }

        [Fact]
        public void RepeatFailAfterAnUnrecordedDeadlineFail_IsNeverTakenBack()
        {
            // The fail that ended the contract at its deadline carries no capture of this build
            // (here an older build's row): it is no witness, so a later repeat penalty is a
            // deadline disagreement and is kept.
            Rewound(30, 150);
            Seeds(1000, 0, 0);
            ledger.Add(new GameAction
            {
                ActionId = "acc-k", UT = 10, CaptureSequence = 5, Type = GameActionType.ContractAccept,
                ContractId = "k", DeadlineUT = 200,
            });
            ledger.Add(new GameAction
            {
                ActionId = "kf", UT = 210, CaptureSequence = 0, Type = GameActionType.ContractFail,
                ContractId = "k", FundsPenalty = 300,
            });
            GameAction repeat = new GameAction
            {
                ActionId = "kf2", UT = 250, CaptureSequence = 40, Type = GameActionType.ContractFail,
                ContractId = "k", FundsPenalty = 300,
            };
            ledger.Add(repeat);
            Walk(300);
            Assert.Equal(GameActionNotCountedReason.ContractOutcomeAfterEnd, repeat.NotCountedReason);
            Assert.False(repeat.HasTakeBack);
        }

        [Fact]
        public void LedgerLoadWithAnUnresolvedPath_ClearsTheEraAndTheBreaks()
        {
            string savedRoot = RecordingPaths.SaveRootOverrideForTesting;
            RecordingPaths.SaveRootOverrideForTesting = null;
            try
            {
                StockShadowTakeBack.SetEligibleFromForTesting(7);
                StockShadowTakeBack.AddBreakForTesting(10, 50);
                LedgerOrchestrator.OnLoad();
                Assert.Equal(0L, StockShadowTakeBack.EligibleFromCaptureSequence);
                Assert.Empty(StockShadowTakeBack.Breaks);
            }
            finally
            {
                RecordingPaths.SaveRootOverrideForTesting = savedRoot;
                LedgerOrchestrator.ResetForTesting();
            }
        }

        // ------------------------------------------------------------------
        // Shortfall
        // ------------------------------------------------------------------

        [Fact]
        public void Shortfall_FundsAndScienceWarnAsTheSameDebt()
        {
            var m = new StockPoolMeasurement();
            m.TakenBack.Add(new KeyValuePair<string, double>("b", 500));
            m.TakenBackTotal = 500;
            Assert.Equal(0.0, KspStatePatcher.EmitTakeBackShortfall("Funds", m, 10));
            Assert.Equal(200.0, KspStatePatcher.EmitTakeBackShortfall("Funds", m, -200));
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("PatchFunds: take-back shortfall 200 of 500")
                && l.Contains("a debt, design 3.13 part 4"));
            Assert.Equal(300.0, KspStatePatcher.NoteScienceTakeBackShortfall(m, -300));
            // Design 3.15 step 6: the science shortfall is the same debt as funds (a tech spent
            // from the duplicate is charged into debt, never left researched for free).
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("PatchScience: take-back shortfall 300 of 500")
                && l.Contains("later gains repay it first") && l.Contains("a debt, design 3.13 part 4"));
            Assert.DoesNotContain(logLines, l => l.Contains("the 0 floor forgives it"));
            Assert.Equal(0.0, KspStatePatcher.EmitTakeBackShortfall("Funds", new StockPoolMeasurement(), -200));
        }

        // ------------------------------------------------------------------
        // Identity registry: the stock side
        // ------------------------------------------------------------------

        [Fact]
        public void Registry_EveryIdentityReasonBelongsToATakeBackEntry()
        {
            var reasonOwners = new Dictionary<GameActionNotCountedReason, string[]>
            {
                { GameActionNotCountedReason.MilestoneAlreadyAchieved, new[] { "MilestoneAchievement/one-shot" } },
                { GameActionNotCountedReason.RecordThresholdsAlreadyCredited, new[] { "MilestoneAchievement/world-record" } },
                { GameActionNotCountedReason.ContractAlreadyCompleted, new[] { "ContractComplete", "ContractParameterComplete" } },
                { GameActionNotCountedReason.ContractDeadlinePassed, new[] { "ContractComplete", "ContractParameterComplete" } },
                { GameActionNotCountedReason.ContractAlreadyResolved, new[] { "ContractComplete", "ContractParameterComplete" } },
                { GameActionNotCountedReason.ContractOutcomeAfterEnd, new[] { "ContractFail", "ContractCancel" } },
                { GameActionNotCountedReason.ContractParameterAfterCompletion, new[] { "ContractParameterComplete" } },
                { GameActionNotCountedReason.ConversionCauseNotCounted, new[]
                    {
                        "StrategyScienceDebit/Converter", "StrategyScienceCredit", "FundsSpending/StrategyConverter",
                        "FundsEarning/Strategy", "ReputationEarning/Strategy", "ReputationPenalty/StrategyConverter",
                    } },
                { GameActionNotCountedReason.SurveyBodyAlreadyCredited, new[] { "ScienceLabEarning/survey" } },
                { GameActionNotCountedReason.ProducerSliceAlreadyCredited, new[] { "ScienceEarning/continuous" } },
            };
            foreach (GameActionNotCountedReason r in Enum.GetValues(typeof(GameActionNotCountedReason)))
            {
                if (!StockShadowTakeBack.IsIdentityReason(r))
                    continue;
                Assert.True(reasonOwners.ContainsKey(r), "identity reason with no owner in this gate: " + r);
                foreach (string name in reasonOwners[r])
                {
                    var entry = LedgerEventIdentityRegistry.Entries.Single(e => e.Name == name);
                    Assert.True(entry.TakesBackStockRepeat, name + " must take back a stock repeat");
                }
            }
            // Science's cap is a take-back without a not-counted reason.
            Assert.True(LedgerEventIdentityRegistry.Entries.Single(e => e.Name == "ScienceEarning/subject").TakesBackStockRepeat);
            // Nothing else takes back (never a purchase, a floor, a strategy share); the six
            // conversion-leg entries take back only the share of a duplicate cause, the survey
            // and the producer slice only a repeat stock paid after forgetting (3.15 step 7).
            Assert.Equal(15, LedgerEventIdentityRegistry.Entries.Count(e => e.TakesBackStockRepeat));
        }

        [Fact]
        public void Registry_TheMirroredEntriesAreExactlyTheKindsTheMirrorActsOn()
        {
            var mirrored = LedgerEventIdentityRegistry.Entries.Where(e => e.StockMirror != null)
                .Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(new List<string>
            {
                "ContractParameterComplete",
                "MilestoneAchievement/one-shot",
                "MilestoneAchievement/world-record",
                "ResourceScanUnlocked",
                // The survey's stock gate is the body's scan, which the same survey's
                // ResourceScanUnlocked row mirrors (3.15 step 7).
                "ScienceEarning/subject",
                "ScienceLabEarning/survey",
            }, mirrored);

            Assert.True(CommittedMilestoneReach.IsMirroredRow(Milestone("a", "Mun/Landing", 1, 1, 1, 0, 0)));
            Assert.True(CommittedMilestoneReach.IsMirroredRow(Record("r", "RecordsAltitude", 1, 1, 1)));
            Assert.True(CommittedMilestoneReach.IsMirroredRow(Science("s", "x", 1, 1, 1, 2)));
            Assert.True(CommittedMilestoneReach.IsMirroredRow(Parameter("p", "k", "0", 1, 1, 0, 0, 0)));
            Assert.True(CommittedMilestoneReach.IsMirroredRow(new GameAction
            {
                Type = GameActionType.ResourceScanUnlocked, ScanBodyName = "Mun",
            }));
            Assert.False(CommittedMilestoneReach.IsMirroredRow(ContractDone("c", "k", 1, 1, 1)));
            Assert.False(CommittedMilestoneReach.IsMirroredRow(new GameAction { Type = GameActionType.ScienceSpending }));
            Assert.False(CommittedMilestoneReach.IsMirroredRow(new GameAction { Type = GameActionType.FacilityUpgrade }));
        }
    }

    /// <summary>
    /// Design 3.15 step 5, the prevention half: the add-only live mirror generalised from
    /// world-firsts and record bands to every registered kind whose stock state records
    /// "already paid" (subject science, contract parameters, resource scans), and the flight
    /// clock cursor firing on any of them.
    /// </summary>
    [Collection("Sequential")]
    public class CommittedMirrorPreventionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CommittedMirrorPreventionTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            CommittedMilestoneReach.ResetForTesting();
            CommittedMilestoneReach.SuppressedRecordingIdsProviderForTesting = () => null;
        }

        public void Dispose()
        {
            CommittedMilestoneReach.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static GameAction Sci(string subject, double ut, float awarded, float cap, string rec = null) => new GameAction
        {
            Type = GameActionType.ScienceEarning, UT = ut, SubjectId = subject, ScienceAwarded = awarded,
            SubjectMaxValue = cap, RecordingId = rec,
        };

        private static GameAction Param(string contract, string param, double ut, string rec = null) => new GameAction
        {
            Type = GameActionType.ContractParameterComplete, UT = ut, ContractId = contract,
            ContractParameterId = param, RecordingId = rec,
        };

        private static GameAction Scan(string body, double ut) => new GameAction
        {
            Type = GameActionType.ResourceScanUnlocked, UT = ut, ScanBodyName = body,
        };

        [Fact]
        public void NextRowUTAfter_FiresOnEveryMirroredKind()
        {
            var rows = new List<GameAction>
            {
                Sci("a@b", 30, 1, 5), Param("k", "0", 40), Scan("Mun", 50),
                new GameAction { Type = GameActionType.FundsEarning, UT = 20 },
                new GameAction { Type = GameActionType.ContractComplete, UT = 25, ContractId = "k" },
            };
            Assert.Equal(30.0, CommittedMilestoneReach.NextRowUTAfter(rows, 10));
            Assert.Equal(40.0, CommittedMilestoneReach.NextRowUTAfter(rows, 30));
            Assert.Equal(50.0, CommittedMilestoneReach.NextRowUTAfter(rows, 40));
            Assert.True(double.IsPositiveInfinity(CommittedMilestoneReach.NextRowUTAfter(rows, 50)));
        }

        [Fact]
        public void CollectSubjectScience_CapsInWalkOrder_StopsAtTheClock_AndSkipsTheReFlySubtree()
        {
            var rows = new List<GameAction>
            {
                Sci("a@b", 10, 6, 10), Sci("a@b", 20, 6, 10), Sci("a@b", 40, 6, 10),
                Sci("c@d", 15, 3, 10, rec: "refly"),
            };
            var got = CommittedMilestoneReach.CollectCommittedSubjectScienceAtOrBefore(
                rows, 30, new HashSet<string> { "refly" }, out int excluded);
            Assert.Equal(10.0, got["a@b"], 3);
            Assert.False(got.ContainsKey("c@d"));
            Assert.Equal(1, excluded);
            Assert.Empty(CommittedMilestoneReach.CollectCommittedSubjectScienceAtOrBefore(rows, double.NaN, null, out _));
        }

        [Fact]
        public void CollectParameters_OnePerParameter_EarliestFirst_AtOrBeforeTheClock()
        {
            var rows = new List<GameAction>
            {
                Param("k", "1", 25), Param("k", "0", 20), Param("k", "0", 22), Param("k", "2", 60),
                Param("j", "0", 21, rec: "refly"), new GameAction { Type = GameActionType.ContractParameterComplete, UT = 5 },
            };
            var got = CommittedMilestoneReach.CollectCommittedParametersAtOrBefore(
                rows, 30, new HashSet<string> { "refly" }, out int excluded);
            Assert.Equal(new[] { "k/0@20", "k/1@25" },
                got.Select(c => c.ContractId + "/" + c.ParameterId + "@" + c.UT).ToArray());
            Assert.Equal(1, excluded);
        }

        [Fact]
        public void CollectParameters_SkipsARowTheWalkDidNotCredit()
        {
            GameAction zeroed = Param("k", "0", 20);
            zeroed.Effective = false;
            zeroed.NotCountedReason = GameActionNotCountedReason.ContractParameterAfterCompletion;
            GameAction credited = Param("k", "0", 24);
            GameAction onlyZeroed = Param("j", "0", 22);
            onlyZeroed.Effective = false;
            var got = CommittedMilestoneReach.CollectCommittedParametersAtOrBefore(
                new List<GameAction> { zeroed, credited, onlyZeroed }, 30, null, out int excluded);
            Assert.Equal(new[] { "k/0@24" },
                got.Select(c => c.ContractId + "/" + c.ParameterId + "@" + c.UT).ToArray());
            Assert.Equal(0, excluded);
        }

        [Fact]
        public void Apply_MirrorsParametersScansAndSubjectScience_AndLogsTheCounts()
        {
            IReadOnlyList<CreditedContractParameter> seenParams = null;
            IReadOnlyList<GameAction> seenScans = null;
            CommittedMilestoneReach.ContractParameterMirrorForTesting = (p, clock) => { seenParams = p; return p.Count; };
            CommittedMilestoneReach.ResourceScanMirrorForTesting = (r, clock) => { seenScans = r; return 1; };
            var rows = new List<GameAction>
            {
                Sci("a@b", 10, 4, 10), Param("k", "0", 20), Scan("Mun", 30), Scan("Minmus", 90),
            };

            int changed = CommittedMilestoneReach.Apply(rows, 50, "test");

            Assert.Equal(2, changed);
            Assert.Single(seenParams);
            Assert.Equal("Mun", Assert.Single(seenScans).ScanBodyName);
            Assert.True(CommittedMilestoneReach.TryGetMirroredSubjectScience("a@b", out double sci));
            Assert.Equal(4.0, sci, 3);
            Assert.Contains(logLines, l => l.Contains("[CommittedMilestones]")
                && l.Contains("completed 1 contract parameter(s) without reward")
                && l.Contains("unlocked 1 resource scan(s)") && l.Contains("subjectsMirrored=1"));

            // A later pass replaces the mirror (never merges); a patch that is not deferred clears it.
            CommittedMilestoneReach.Apply(new List<GameAction>(), 50, "test");
            Assert.False(CommittedMilestoneReach.TryGetMirroredSubjectScience("a@b", out _));
            CommittedMilestoneReach.Apply(rows, 50, "test");
            CommittedMilestoneReach.ClearSubjectScienceMirror("test");
            Assert.Equal(0, CommittedMilestoneReach.MirroredSubjectCount);
            Assert.Contains(logLines, l => l.Contains("Subject science mirror cleared (test)"));
        }

        [Fact]
        public void Apply_WithoutAClock_MirrorsNothing()
        {
            int calls = 0;
            CommittedMilestoneReach.ContractParameterMirrorForTesting = (p, clock) => { calls++; return 0; };
            CommittedMilestoneReach.Apply(new List<GameAction> { Param("k", "0", 20), Sci("a@b", 10, 4, 10) },
                double.NaN, "test");
            Assert.Equal(0, calls);
            Assert.Equal(0, CommittedMilestoneReach.MirroredSubjectCount);
        }

        [Fact]
        public void LiveParameterAndScanMirrors_SkipWithoutTheirStockSystems()
        {
            Assert.Equal(0, KspStatePatcher.MirrorCommittedContractParameters(
                new List<CreditedContractParameter> { new CreditedContractParameter { ContractId = "k", ParameterId = "0", UT = 1 } }, 5));
            Assert.Equal(0, KspStatePatcher.MirrorCommittedResourceScans(new List<GameAction> { Scan("Mun", 1) }, 5));
            Assert.Contains(logLines, l => l.Contains("MirrorCommittedContractParameters: Unity calls suppressed"));
            Assert.Contains(logLines, l => l.Contains("MirrorCommittedResourceScans: Unity calls suppressed"));
        }

        [Fact]
        public void ScienceSubjectPatch_ReadsTheLargerOfTheWalkAndTheMirror()
        {
            LedgerOrchestrator.Initialize();
            LedgerOrchestrator.Science.ProcessEarning(Sci("a@b", 5, 2, 10));
            Assert.True(Patches.ScienceSubjectPatch.TryResolveCommittedScience("a@b", out float v));
            Assert.Equal(2f, v);

            // The last walk stopped at the rewind; the clock has since passed a committed row.
            CommittedMilestoneReach.Apply(new List<GameAction> { Sci("a@b", 5, 2, 10), Sci("a@b", 40, 6, 10) },
                50, "deferred-patch");
            Assert.True(Patches.ScienceSubjectPatch.TryResolveCommittedScience("a@b", out v));
            Assert.Equal(8f, v);
            Assert.True(Patches.ScienceSubjectPatch.TryResolveCommittedScience("x@y", out v) == false);

            // The mirror never lowers the walk's value.
            CommittedMilestoneReach.Apply(new List<GameAction> { Sci("a@b", 5, 1, 10) }, 50, "deferred-patch");
            Assert.True(Patches.ScienceSubjectPatch.TryResolveCommittedScience("a@b", out v));
            Assert.Equal(2f, v);
        }
    }
}
