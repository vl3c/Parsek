using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Design 3.15 step 7 (owner rulings 2026-10-26): the orbital survey bonus pays once per
    /// body, earliest first (stock pays it once per body, decompiled ModuleOrbitalSurveyor), and
    /// a continuous producer's time slice the committed timeline already holds earns nothing.
    /// Both judge a repeat only when a rewind separated it from its holder, so a career never
    /// rewound walks exactly as before.
    /// </summary>
    [Collection("Sequential")]
    public class OncePerBodyAndSliceTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<GameAction> ledger = new List<GameAction>();
        private readonly MilestonesModule milestones = new MilestonesModule();
        private readonly ContractsModule contracts = new ContractsModule();
        private readonly ScienceModule science = new ScienceModule();
        private readonly FundsModule funds = new FundsModule();
        private readonly ReputationModule reputation = new ReputationModule();

        public OncePerBodyAndSliceTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            KspStatePatcher.AllowNegativeCurrencyProviderForTesting = () => false;
            StockPoolReference.ResetForTesting();
            StockShadowTakeBack.ResetForTesting();
            ConversionCauseScope.ResetForTesting();
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
            StockShadowTakeBack.ResetForTesting();
            ConversionCauseScope.ResetForTesting();
            CommittedMilestoneReach.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private void Seeds(double f = 0, double s = 0, double r = 0)
        {
            ledger.Add(new GameAction { ActionId = "seedF", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = (float)f });
            ledger.Add(new GameAction { ActionId = "seedS", UT = 0, Type = GameActionType.ScienceInitial, InitialScience = (float)s });
            ledger.Add(new GameAction { ActionId = "seedR", UT = 0, Type = GameActionType.ReputationInitial, InitialReputation = (float)r });
        }

        private void Walk(double? now = null)
        {
            if (now.HasValue) RecalculationEngine.Recalculate(ledger.ToList(), now.Value);
            else RecalculationEngine.Recalculate(ledger.ToList());
        }

        private static GameAction Survey(string id, string body, double ut, long cseq, float sci = 12f)
            => new GameAction
            {
                ActionId = id, UT = ut, CaptureSequence = cseq, Type = GameActionType.ScienceLabEarning,
                ScanBodyName = body, ScienceAwarded = sci,
            };

        private static GameAction Slice(string id, string producer, string subject, double start, double end,
            long cseq, float sci, double? ut = null)
            => new GameAction
            {
                ActionId = id, UT = ut ?? end, CaptureSequence = cseq, Type = GameActionType.ScienceEarning,
                SubjectId = subject, ScienceAwarded = sci, SubjectMaxValue = 1000f,
                ProducerId = producer, SliceStartUT = start, SliceEndUT = end,
            };

        private static TimelineBreak Break(long highWater, double ut)
        {
            var list = new List<TimelineBreak>();
            StockShadowTakeBack.ResetForTesting();
            StockShadowTakeBack.AddBreakForTesting(highWater, ut);
            list.AddRange(StockShadowTakeBack.Breaks);
            StockShadowTakeBack.ResetForTesting();
            return list[0];
        }

        private double PatchScience(double now, double live, bool authoritative = false)
        {
            Walk(now);
            var state = StockPoolReference.Science;
            List<StockPoolRow> rows = StockPoolReference.BuildRows(
                StockPool.Science, ledger, science.GetRowEffects(), science.GetTakeBackDeltas());
            double min = science.GetProjectionMinBalance();
            StockPoolMeasurement m = StockPoolReference.Measure(
                state, rows, now, live, 0.0, authoritative, 0.001, min,
                new HashSet<string>(ledger.Select(a => a.ActionId), StringComparer.Ordinal), clampAtZero: true);
            if (m.Adjustment != 0.0)
            {
                GameAction row = StockPoolReference.BuildAdjustmentRow(StockPool.Science, m.Adjustment, now);
                ledger.Add(row);
                m.AdjustmentRow = row;
            }
            KspStatePatcher.EmitTakeBacks(StockPool.Science, m, ledger);
            double write = m.GapAdopted
                ? live
                : StockPoolReference.ResolveCurrencyWriteTarget(min, science.GetRunningScience(), live, false, m.Adjustment);
            StockPoolReference.Commit(state, rows, m, write, 0.0);
            return write;
        }

        // ==================================================================
        // Orbital survey: once per body, earliest first
        // ==================================================================

        // catches: the decision judging a repeat stock paid by its own rule, or missing either
        // rewind case.
        [Fact]
        public void DecideSurveyRepeat_JudgesOnlyARewindsRepeat()
        {
            var holder = Survey("h", "Mun", 100, 10);
            Assert.Equal(ScienceModule.SurveyRepeat.Credit, ScienceModule.DecideSurveyRepeat(null, Survey("r", "Mun", 200, 20), null));
            // Same timeline (holder captured first, no break): stock's own double payment, kept.
            Assert.Equal(ScienceModule.SurveyRepeat.Credit,
                ScienceModule.DecideSurveyRepeat(holder, Survey("r", "Mun", 200, 20), new List<TimelineBreak>()));
            // The holder captured after the row: a rewind separated them.
            Assert.Equal(ScienceModule.SurveyRepeat.HolderCapturedAfter,
                ScienceModule.DecideSurveyRepeat(Survey("h2", "Mun", 100, 30), Survey("r", "Mun", 200, 20), null));
            // A break that loaded a state before the holder, captured between them.
            Assert.Equal(ScienceModule.SurveyRepeat.HolderForgotten,
                ScienceModule.DecideSurveyRepeat(holder, Survey("r", "Mun", 200, 20), new List<TimelineBreak> { Break(15, 50) }));
            // The break loaded a state after the holder: stock still held it, its own repeat.
            Assert.Equal(ScienceModule.SurveyRepeat.Credit,
                ScienceModule.DecideSurveyRepeat(holder, Survey("r", "Mun", 200, 20), new List<TimelineBreak> { Break(15, 150) }));
            // No capture numbers (an older save): never judged.
            Assert.Equal(ScienceModule.SurveyRepeat.Credit,
                ScienceModule.DecideSurveyRepeat(Survey("h", "Mun", 100, 0), Survey("r", "Mun", 200, 0), null));
        }

        [Fact]
        public void IsOrbitalSurveyRow_OnlyASurveyLabRow()
        {
            Assert.True(ScienceModule.IsOrbitalSurveyRow(Survey("s", "Mun", 1, 1)));
            Assert.False(ScienceModule.IsOrbitalSurveyRow(new GameAction { Type = GameActionType.ScienceLabEarning, ScienceAwarded = 1 }));
            Assert.False(ScienceModule.IsOrbitalSurveyRow(new GameAction
            {
                Type = GameActionType.ScienceLabEarning, ScanBodyName = "Mun", IncomeReason = "Cheating",
            }));
            Assert.False(ScienceModule.IsOrbitalSurveyRow(new GameAction { Type = GameActionType.ResourceScanUnlocked, ScanBodyName = "Mun" }));
        }

        // catches: both surveys of a body credited after a rewind, the later one on the
        // timeline keeping the bonus, or the not-counted row still moving the projection.
        [Fact]
        public void Rewind_TheEarlierSurveyOnTheTimelineKeepsTheBonus()
        {
            Seeds();
            GameAction committed = Survey("c", "Mun", 1000, 10);
            GameAction rewound = Survey("r", "Mun", 200, 50);
            ledger.Add(committed);
            ledger.Add(rewound);
            ledger.Add(Survey("m", "Minmus", 300, 51, 8f));
            Walk();

            Assert.True(rewound.Effective);
            Assert.False(committed.Effective);
            Assert.Equal(GameActionNotCountedReason.SurveyBodyAlreadyCredited, committed.NotCountedReason);
            Assert.Equal(0f, committed.EffectiveScience);
            Assert.False(committed.HasTakeBack);
            Assert.Equal(20.0, science.GetRunningScience(), 4);
            double delta;
            Assert.False(ScienceModule.TryGetRowDelta(committed, out delta));
            Assert.True(ScienceModule.TryGetRowDelta(rewound, out delta));
            Assert.Equal(12.0, delta, 4);
            Assert.Contains(logLines, l => l.Contains("[INFO][ScienceModule]") && l.Contains("Orbital survey of 'Mun' id=c")
                && l.Contains("not counted") && l.Contains("held by id=r") && l.Contains("captured after this row"));
            Assert.Equal("Not counted: this body was already surveyed earlier on timeline, so no bonus was paid.",
                TimelineRowHover.NotCounted(committed.NotCountedReason));
        }

        // catches: stock's own double payment on one timeline (a second survey transmitted
        // before the first completed) reduced retroactively.
        [Fact]
        public void OneTimeline_ADoubleSurvey_IsKeptAsStockPaidIt()
        {
            Seeds();
            GameAction first = Survey("a", "Mun", 100, 10);
            GameAction race = Survey("b", "Mun", 100.5, 11);
            ledger.Add(first);
            ledger.Add(race);
            Walk();
            Assert.True(first.Effective);
            Assert.True(race.Effective);
            Assert.Equal(24.0, science.GetRunningScience(), 4);
            Assert.Contains(logLines, l => l.Contains("Orbital survey of 'Mun' id=b credited again") && l.Contains("kept as captured"));
        }

        // catches: stock's repeat after a rewind kept (the bonus paid twice) or adopted as a
        // pool adjustment instead of taken back.
        [Fact]
        public void Rewind_StockPaysTheSurveyAgain_TheNextUpdateTakesItBack()
        {
            StockShadowTakeBack.SetEligibleFromForTesting(1);
            StockShadowTakeBack.AddBreakForTesting(13, 50, "PlainRewind");
            Seeds();
            ledger.Add(Survey("a", "Mun", 100, 10));
            PatchScience(50, 0, authoritative: true);

            // After the rewind the clock passes UT 100 and the mirror misses: stock pays again.
            GameAction again = Survey("b", "Mun", 120, 20);
            ledger.Add(again);
            Assert.Equal(12.0, PatchScience(130, 12), 4);
            Assert.False(again.Effective);
            Assert.Equal(GameActionNotCountedReason.SurveyBodyAlreadyCredited, again.NotCountedReason);
            Assert.Equal(12f, again.TakeBackScience);
            Assert.Equal(0, ledger.Count(x => x.Type == GameActionType.StockPoolAdjustment));
            Assert.Contains(logLines, l => l.Contains("[WARN][TakeBack]") && l.Contains("orbital survey of 'Mun'")
                && l.Contains("SurveyBodyAlreadyCredited"));
            Assert.Contains(logLines, l => l.Contains("stock forgot that scan after a rewind and paid again")
                && l.Contains("taking back 12"));
        }

        // catches: a strategy's cut of a duplicate survey kept after the survey was zeroed.
        [Fact]
        public void ARepeatedSurvey_TakesItsConversionLegWithIt()
        {
            Seeds();
            string cause = new ConversionCause { Type = GameActionType.ScienceLabEarning, SourcePool = StockPool.Science, Key = "Mun" }.Format();
            ledger.Add(Survey("c", "Mun", 1000, 10));
            ledger.Add(new GameAction
            {
                ActionId = "leg", UT = 1000, CaptureSequence = 11, Type = GameActionType.StrategyScienceDebit,
                Cost = 3f, ConversionSource = StrategyConversionSource.Converter, ConversionCause = cause,
            });
            ledger.Add(Survey("r", "Mun", 200, 50));
            Walk();
            GameAction leg = ledger.Single(a => a.ActionId == "leg");
            Assert.Equal(0f, leg.ConversionShare);
            Assert.Equal(GameActionNotCountedReason.ConversionCauseNotCounted, leg.NotCountedReason);
            Assert.Equal(12.0, science.GetRunningScience(), 4);
        }

        // catches: any never-rewound career whose surveys walk differently from lab payouts of
        // the same amounts (credited as captured), races included.
        [Fact]
        public void NeverRewound_SurveyCareers_EndExactlyAsBefore_Randomized()
        {
            string[] bodies = { "Mun", "Minmus", "Duna", "Ike" };
            for (int seed = 1; seed <= 40; seed++)
            {
                var rng = new Random(seed);
                var surveyed = new List<GameAction>();
                var asLab = new List<GameAction>();
                double ut = 10;
                long cseq = 1;
                surveyed.Add(new GameAction { UT = 0, Type = GameActionType.ScienceInitial, InitialScience = 5f });
                asLab.Add(new GameAction { UT = 0, Type = GameActionType.ScienceInitial, InitialScience = 5f });
                int n = rng.Next(1, 12);
                for (int i = 0; i < n; i++)
                {
                    ut += rng.Next(0, 3) == 0 ? 0.25 : rng.Next(1, 500);
                    float sci = (float)Math.Round(rng.NextDouble() * 30, 2);
                    string body = bodies[rng.Next(bodies.Length)];
                    surveyed.Add(Survey("s" + i, body, ut, cseq, sci));
                    asLab.Add(new GameAction
                    {
                        ActionId = "s" + i, UT = ut, CaptureSequence = cseq, Type = GameActionType.ScienceLabEarning, ScienceAwarded = sci,
                    });
                    cseq++;
                }
                RecalculationEngine.Recalculate(asLab);
                double expected = science.GetRunningScience();
                double expectedAvail = science.GetAvailableScience();
                RecalculationEngine.Recalculate(surveyed);
                Assert.True(expected == science.GetRunningScience(), "science differs for seed " + seed);
                Assert.True(expectedAvail == science.GetAvailableScience(), "available science differs for seed " + seed);
                Assert.All(surveyed.Where(ScienceModule.IsOrbitalSurveyRow), r => Assert.True(r.Effective));
            }
        }

        // ==================================================================
        // Continuous producers: a slice the timeline already holds earns nothing
        // ==================================================================

        [Fact]
        public void Intervals_UnionSubtractAndLength()
        {
            var parts = new List<KeyValuePair<double, double>>
            {
                new KeyValuePair<double, double>(150, 200),
                new KeyValuePair<double, double>(100, 160),
                new KeyValuePair<double, double>(300, 400),
            };
            List<KeyValuePair<double, double>> u = ProducerSliceBook.Union(parts);
            Assert.Equal(2, u.Count);
            Assert.Equal(100, u[0].Key);
            Assert.Equal(200, u[0].Value);
            Assert.Equal(200.0, ProducerSliceBook.Length(u));
            List<KeyValuePair<double, double>> rest = ProducerSliceBook.Subtract(50, 350, u);
            Assert.Equal(new[] { 50.0, 200.0 }, rest.Select(p => p.Key).ToArray());
            Assert.Equal(new[] { 100.0, 300.0 }, rest.Select(p => p.Value).ToArray());
            Assert.Empty(ProducerSliceBook.Subtract(120, 180, u));
            Assert.Empty(ProducerSliceBook.Union(null));
        }

        [Fact]
        public void IsProducerSlice_NeedsAProducerAndAPositiveFiniteSlice()
        {
            Assert.True(ProducerSliceBook.IsProducerSlice(Slice("a", "p", "s", 0, 10, 1, 1)));
            Assert.False(ProducerSliceBook.IsProducerSlice(Slice("a", null, "s", 0, 10, 1, 1)));
            Assert.False(ProducerSliceBook.IsProducerSlice(Slice("a", "p", "s", 10, 10, 1, 1)));
            Assert.False(ProducerSliceBook.IsProducerSlice(Slice("a", "p", "s", double.NaN, 10, 1, 1)));
            Assert.False(ProducerSliceBook.IsProducerSlice(Slice("a", "p", "s", 0, double.PositiveInfinity, 1, 1)));
            var lab = Slice("a", "p", "s", 0, 10, 1, 1);
            lab.Type = GameActionType.ScienceLabEarning;
            Assert.False(ProducerSliceBook.IsProducerSlice(lab));
        }

        // catches: the book judging an overlap on one timeline, another producer or subject,
        // or a holder the rewind did not separate.
        [Fact]
        public void Book_CountsOnlyARewindsOverlap_OfTheSameProducerAndSubject()
        {
            var book = new ProducerSliceBook();
            GameAction held = Slice("h", "p1", "goo", 100, 200, 10, 4);
            book.Hold(held, book.Decide(held, null));

            // Same timeline (held captured first, no break): kept whole.
            ProducerSliceDecision same = book.Decide(Slice("x", "p1", "goo", 150, 250, 20, 4), null);
            Assert.Equal(0.0, same.Covered);
            Assert.Equal(1.0, same.CreditedFraction);

            // Another producer or subject: never.
            Assert.Equal(0.0, book.Decide(Slice("x", "p2", "goo", 100, 200, 5, 4), null).Covered);
            Assert.Equal(0.0, book.Decide(Slice("x", "p1", "ore", 100, 200, 5, 4), null).Covered);

            // Held captured after the row: half the slice is the holder's.
            ProducerSliceDecision earlier = book.Decide(Slice("x", "p1", "goo", 150, 250, 5, 4), null);
            Assert.Equal(50.0, earlier.Covered);
            Assert.Equal(0.0, earlier.Forgotten);
            Assert.Equal(0.5, earlier.CreditedFraction, 6);
            Assert.Same(held, earlier.Holder);

            // A break that forgot the holder: covered and forgotten.
            ProducerSliceDecision forgot = book.Decide(Slice("x", "p1", "goo", 150, 250, 20, 4),
                new List<TimelineBreak> { Break(15, 50) });
            Assert.Equal(50.0, forgot.Covered);
            Assert.Equal(50.0, forgot.Forgotten);
            Assert.Equal(0.5, forgot.ForgottenFraction, 6);

            // Not a slice: credit in full.
            Assert.Equal(1.0, book.Decide(new GameAction { Type = GameActionType.ScienceEarning }, null).CreditedFraction);
        }

        // catches: a repeated survey or slice sealing a Re-Fly slot when stock's payment of it
        // was taken back, or leaving the seal when stock's payment is kept (3.15 item 10).
        [Fact]
        public void ReFlySeal_ATakenBackRepeatStaysOut_AKeptRepeatSeals()
        {
            GameAction survey = Survey("s", "Mun", 300, 5);
            survey.Effective = false;
            survey.NotCountedReason = GameActionNotCountedReason.SurveyBodyAlreadyCredited;
            Assert.Null(ReplayCredit.NotCountedLabel(survey, scienceWalked: true));   // payment kept
            survey.TakeBackScience = survey.ScienceAwarded;
            Assert.Equal("SurveyBodyAlreadyCredited", ReplayCredit.NotCountedLabel(survey, scienceWalked: true));

            GameAction slice = Slice("p", "station", "goo@Mun", 100, 200, 5, 4);
            slice.Effective = false;
            slice.NotCountedReason = GameActionNotCountedReason.ProducerSliceAlreadyCredited;
            Assert.Null(ReplayCredit.NotCountedLabel(slice, scienceWalked: true));
            slice.TakeBackScience = 4f;
            Assert.Equal("ProducerSliceAlreadyCredited", ReplayCredit.NotCountedLabel(slice, scienceWalked: true));
        }

        // catches: a credited part recorded twice, so a third slice loses time no row holds.
        [Fact]
        public void Book_HoldsOnlyTheCreditedParts()
        {
            var book = new ProducerSliceBook();
            GameAction a = Slice("a", "p", "goo", 100, 200, 30, 4);
            book.Hold(a, book.Decide(a, null));
            GameAction b = Slice("b", "p", "goo", 150, 300, 10, 6);   // a captured after b
            ProducerSliceDecision db = book.Decide(b, null);
            Assert.Equal(50.0, db.Covered);
            book.Hold(b, db);
            // c is captured between a and b: a counts against it (captured after), b does not
            // (captured before, same timeline), so only [100, 200) is covered.
            GameAction c = Slice("c", "p", "goo", 100, 300, 20, 8);
            ProducerSliceDecision dc = book.Decide(c, null);
            Assert.Equal(100.0, dc.Covered);
            // d comes after a break that forgets b (its UT is after the break) but not a: only
            // the part b was credited, [200, 300), counts against d, not the [150, 200) a held.
            GameAction d = Slice("d", "p", "goo", 100, 300, 40, 8);
            ProducerSliceDecision dd = book.Decide(d, new List<TimelineBreak> { Break(35, 250) });
            Assert.Equal(100.0, dd.Covered);
            Assert.Equal(100.0, dd.Forgotten);
            Assert.Same(b, dd.Holder);
        }

        // catches: the walk paying a slice the committed timeline already holds, or zeroing a
        // partly held one.
        [Fact]
        public void Rewind_ASliceHeldInPart_EarnsTheRest_AndAWholeOneNothing()
        {
            Seeds();
            GameAction rewound = Slice("r", "station", "goo@Mun", 100, 200, 50, 4);
            GameAction committedPart = Slice("cp", "station", "goo@Mun", 150, 250, 10, 4);
            GameAction committedWhole = Slice("cw", "station", "goo@Mun", 100, 200, 11, 6, ut: 260);
            ledger.Add(committedPart);
            ledger.Add(committedWhole);
            ledger.Add(rewound);
            Walk();

            Assert.Equal(4f, rewound.EffectiveScience);
            Assert.True(committedPart.Effective);
            Assert.Equal(2f, committedPart.EffectiveScience);
            Assert.False(committedWhole.Effective);
            Assert.Equal(GameActionNotCountedReason.ProducerSliceAlreadyCredited, committedWhole.NotCountedReason);
            Assert.Equal(6.0, science.GetRunningScience(), 4);
            Assert.Contains(logLines, l => l.Contains("[INFO][ScienceModule]") && l.Contains("id=cp")
                && l.Contains("counted in part") && l.Contains("50s of 100s") && l.Contains("held by id=r"));
            Assert.Contains(logLines, l => l.Contains("id=cw") && l.Contains("not counted: the whole slice"));
            Assert.Equal("Not counted: this time was already counted for the same experiment, so no science was paid.",
                TimelineRowHover.NotCounted(committedWhole.NotCountedReason));
        }

        // catches: a producer paying a forgotten slice again kept, or the take-back covering a
        // part the rewind did not make stock forget.
        [Fact]
        public void Rewind_TheProducerPaysAForgottenSliceAgain_ItsPartIsTakenBack()
        {
            StockShadowTakeBack.SetEligibleFromForTesting(1);
            StockShadowTakeBack.AddBreakForTesting(13, 120, "PlainRewind");
            Seeds();
            ledger.Add(Slice("a", "station", "goo@Mun", 100, 200, 10, 4));
            PatchScience(120, 4, authoritative: true);

            GameAction again = Slice("b", "station", "goo@Mun", 150, 250, 20, 4);
            ledger.Add(again);
            Walk(300);
            Assert.True(again.Effective);
            Assert.Equal(2f, again.EffectiveScience);
            Assert.Equal(2f, again.TakeBackScience);
            Assert.Contains("producer 'station' subject 'goo@Mun'", again.TakeBackWitness);

            GameAction whole = Slice("c", "station", "goo@Mun", 100, 200, 21, 4, ut: 260);
            ledger.Add(whole);
            Walk(300);
            Assert.False(whole.Effective);
            Assert.Equal(4f, whole.TakeBackScience);
        }

        // catches: a slice row before the take-back era taken back.
        [Fact]
        public void ASliceBeforeTheEra_IsReducedButNeverTakenBack()
        {
            StockShadowTakeBack.SetEligibleFromForTesting(30);
            StockShadowTakeBack.AddBreakForTesting(13, 120, "PlainRewind");
            Seeds();
            ledger.Add(Slice("a", "station", "goo@Mun", 100, 200, 10, 4));
            GameAction again = Slice("b", "station", "goo@Mun", 150, 250, 20, 4);
            ledger.Add(again);
            Walk();
            Assert.Equal(2f, again.EffectiveScience);
            Assert.False(again.HasTakeBack);
        }

        // catches: a never-rewound producer career (slices in time order, overlaps included)
        // walking differently from the same rows without a producer.
        [Fact]
        public void NeverRewound_ProducerCareers_EndExactlyAsBefore_Randomized()
        {
            string[] subjects = { "goo@Mun", "seis@Mun", "goo@Minmus" };
            for (int seed = 1; seed <= 40; seed++)
            {
                var rng = new Random(seed);
                var withProducer = new List<GameAction>();
                var plain = new List<GameAction>();
                withProducer.Add(new GameAction { UT = 0, Type = GameActionType.ScienceInitial, InitialScience = 0f });
                plain.Add(new GameAction { UT = 0, Type = GameActionType.ScienceInitial, InitialScience = 0f });
                double t = 0;
                long cseq = 1;
                int n = rng.Next(1, 15);
                for (int i = 0; i < n; i++)
                {
                    double start = t - (rng.Next(0, 4) == 0 ? rng.Next(1, 50) : 0); // occasional overlap
                    double end = t + rng.Next(10, 300);
                    t = end;
                    float sci = (float)Math.Round(rng.NextDouble() * 5, 3);
                    string subject = subjects[rng.Next(subjects.Length)];
                    GameAction p = Slice("p" + i, "station-" + rng.Next(2), subject, start, end, cseq, sci);
                    p.SubjectMaxValue = 40f;
                    withProducer.Add(p);
                    plain.Add(new GameAction
                    {
                        ActionId = "p" + i, UT = end, CaptureSequence = cseq, Type = GameActionType.ScienceEarning,
                        SubjectId = subject, ScienceAwarded = sci, SubjectMaxValue = 40f,
                    });
                    cseq++;
                }
                RecalculationEngine.Recalculate(plain);
                double expected = science.GetRunningScience();
                RecalculationEngine.Recalculate(withProducer);
                Assert.True(expected == science.GetRunningScience(), "science differs for seed " + seed);
                Assert.All(withProducer.Where(ProducerSliceBook.IsProducerSlice), r => Assert.True(r.Effective));
            }
        }

        [Fact]
        public void ProducerFields_RoundTrip_AndAreAbsentWhenUnset()
        {
            var root = new ConfigNode("ROOT");
            Slice("a", "station-1", "goo@Mun", 100.25, 200.5, 7, 3).SerializeInto(root);
            ConfigNode node = root.GetNode("GAME_ACTION");
            Assert.Equal("station-1", node.GetValue(GameAction.ProducerIdKey));
            GameAction back = GameAction.DeserializeFrom(node);
            Assert.Equal("station-1", back.ProducerId);
            Assert.Equal(100.25, back.SliceStartUT);
            Assert.Equal(200.5, back.SliceEndUT);

            var plain = new ConfigNode("ROOT");
            new GameAction { UT = 1, Type = GameActionType.ScienceEarning, SubjectId = "x", ScienceAwarded = 1 }.SerializeInto(plain);
            ConfigNode pn = plain.GetNode("GAME_ACTION");
            Assert.Null(pn.GetValue(GameAction.ProducerIdKey));
            Assert.Null(pn.GetValue(GameAction.SliceStartUTKey));
            GameAction pb = GameAction.DeserializeFrom(pn);
            Assert.Null(pb.ProducerId);
            Assert.True(double.IsNaN(pb.SliceStartUT));
        }

        [Fact]
        public void Registry_SurveyAndSliceAreEnforced_WithTheirVariants()
        {
            Assert.Equal("continuous", LedgerEventIdentityRegistry.VariantOf(Slice("a", "p", "s", 0, 1, 1, 1)));
            Assert.Equal("subject", LedgerEventIdentityRegistry.VariantOf(new GameAction { Type = GameActionType.ScienceEarning }));
            Assert.Equal("survey", LedgerEventIdentityRegistry.VariantOf(Survey("s", "Mun", 1, 1)));
            var survey = LedgerEventIdentityRegistry.Entries.Single(e => e.Name == "ScienceLabEarning/survey");
            Assert.True(survey.EnforcedToday);
            Assert.True(survey.TakesBackStockRepeat);
            Assert.NotNull(survey.StockMirror);
            var slice = LedgerEventIdentityRegistry.Entries.Single(e => e.Name == "ScienceEarning/continuous");
            Assert.Equal(LedgerIdentityRule.OncePerSlice, slice.Rule);
            Assert.True(slice.EnforcedToday);
            Assert.Contains("no capture channel", slice.EnforcedBy);
            Assert.True(StockShadowTakeBack.IsIdentityReason(GameActionNotCountedReason.SurveyBodyAlreadyCredited));
            Assert.True(StockShadowTakeBack.IsIdentityReason(GameActionNotCountedReason.ProducerSliceAlreadyCredited));
        }
    }
}
