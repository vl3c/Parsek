using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Design 3.15 step 7 (owner ruling 2026-10-26): a strategy conversion leg captured inside
    /// stock's currency-modifier query while a known award pays names that award as its cause,
    /// and the walk scales the leg by the share of the cause it counts. A zeroed duplicate
    /// takes its cut with it (and, after a rewind, stock's duplicate conversion is taken
    /// back); a leg with no positive evidence of a cause stays as captured; a career whose
    /// causes all count walks exactly as before.
    /// </summary>
    [Collection("Sequential")]
    public class ConversionLegFollowsCauseTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<GameAction> ledger = new List<GameAction>();
        private readonly MilestonesModule milestones = new MilestonesModule();
        private readonly ContractsModule contracts = new ContractsModule();
        private readonly ScienceModule science = new ScienceModule();
        private readonly FundsModule funds = new FundsModule();
        private readonly ReputationModule reputation = new ReputationModule();

        public ConversionLegFollowsCauseTests()
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
            RegisterModules();
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

        private void RegisterModules()
        {
            RecalculationEngine.ClearModules();
            RecalculationEngine.RegisterModule(milestones, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(contracts, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(science, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(reputation, RecalculationEngine.ModuleTier.SecondTier);
        }

        // ------------------------------------------------------------------
        // Builders
        // ------------------------------------------------------------------

        private void Seeds(double f = 0, double s = 0, double r = 0)
        {
            ledger.Add(new GameAction { ActionId = "seedF", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = (float)f });
            ledger.Add(new GameAction { ActionId = "seedS", UT = 0, Type = GameActionType.ScienceInitial, InitialScience = (float)s });
            ledger.Add(new GameAction { ActionId = "seedR", UT = 0, Type = GameActionType.ReputationInitial, InitialReputation = (float)r });
        }

        private static GameAction Milestone(string actionId, string id, double ut, long cseq, float f, float r = 0f, float s = 0f)
            => new GameAction
            {
                ActionId = actionId, UT = ut, CaptureSequence = cseq, Type = GameActionType.MilestoneAchievement,
                MilestoneId = id, MilestoneFundsAwarded = f, MilestoneRepAwarded = r, MilestoneScienceAwarded = s,
            };

        private static GameAction Record(string actionId, string id, double ut, long cseq, string rec, params int[] indices)
        {
            var band = indices.Select(i => new RecordThresholdCredit(i, i * 1000f, i * 0.5f, i * 0.25f)).ToList();
            return new GameAction
            {
                ActionId = actionId, UT = ut, CaptureSequence = cseq, Type = GameActionType.MilestoneAchievement,
                MilestoneId = id, RecordingId = rec,
                MilestoneFundsAwarded = band.Sum(c => c.Funds),
                MilestoneRepAwarded = band.Sum(c => c.Rep),
                MilestoneScienceAwarded = band.Sum(c => c.Science),
                MilestoneRecordThresholds = band.Count,
                MilestoneRecordBand = band,
            };
        }

        private static GameAction Subject(string actionId, string subject, double ut, long cseq, float awarded, float cap)
            => new GameAction
            {
                ActionId = actionId, UT = ut, CaptureSequence = cseq, Type = GameActionType.ScienceEarning,
                SubjectId = subject, ScienceAwarded = awarded, SubjectMaxValue = cap,
            };

        private static GameAction Accept(string id, double ut, long cseq) => new GameAction
        {
            ActionId = "acc-" + id, UT = ut, CaptureSequence = cseq, Type = GameActionType.ContractAccept, ContractId = id,
        };

        private static GameAction ContractDone(string actionId, string id, double ut, long cseq, float f) => new GameAction
        {
            ActionId = actionId, UT = ut, CaptureSequence = cseq, Type = GameActionType.ContractComplete,
            ContractId = id, FundsReward = f,
        };

        private static string Cause(GameActionType type, StockPool pool, string key)
            => new ConversionCause { Type = type, SourcePool = pool, Key = key }.Format();

        /// <summary>Appreciation Campaign's shape: funds diverted out, nominal reputation in.</summary>
        private static GameAction FundsDebitLeg(string actionId, double ut, long cseq, float spent, string cause)
            => new GameAction
            {
                ActionId = actionId, UT = ut, CaptureSequence = cseq, Type = GameActionType.FundsSpending,
                FundsSpent = spent, FundsSpendingSource = FundsSpendingSource.StrategyConverter, ConversionCause = cause,
            };

        private static GameAction RepCreditLeg(string actionId, double ut, long cseq, float nominal, string cause)
            => new GameAction
            {
                ActionId = actionId, UT = ut, CaptureSequence = cseq, Type = GameActionType.ReputationEarning,
                NominalRep = nominal, RepSource = ReputationSource.Strategy, ConversionCause = cause,
            };

        /// <summary>Patents Licensing's shape: science diverted out, funds in.</summary>
        private static GameAction ScienceDebitLeg(string actionId, double ut, long cseq, float cost, string cause)
            => new GameAction
            {
                ActionId = actionId, UT = ut, CaptureSequence = cseq, Type = GameActionType.StrategyScienceDebit,
                Cost = cost, ConversionSource = StrategyConversionSource.Converter, ConversionCause = cause,
            };

        private static GameAction FundsCreditLeg(string actionId, double ut, long cseq, float awarded, string cause)
            => new GameAction
            {
                ActionId = actionId, UT = ut, CaptureSequence = cseq, Type = GameActionType.FundsEarning,
                FundsAwarded = awarded, FundsSource = FundsEarningSource.Strategy, ConversionCause = cause,
            };

        private void Walk(double? now = null)
        {
            if (now.HasValue) RecalculationEngine.Recalculate(ledger.ToList(), now.Value);
            else RecalculationEngine.Recalculate(ledger.ToList());
        }

        private bool Rewalked => logLines.Any(l => l.Contains("Conversion legs follow their cause: walking again"));

        // ------------------------------------------------------------------
        // The cause text
        // ------------------------------------------------------------------

        [Fact]
        public void Cause_FormatsAndParses_RoundTrip()
        {
            string text = Cause(GameActionType.ContractParameterComplete, StockPool.Science, "guid-1/0.2");
            Assert.Equal("ContractParameterComplete|Science|guid-1/0.2", text);
            ConversionCause parsed;
            Assert.True(ConversionCause.TryParse(text, out parsed));
            Assert.Equal(GameActionType.ContractParameterComplete, parsed.Type);
            Assert.Equal(StockPool.Science, parsed.SourcePool);
            Assert.Equal("guid-1/0.2", parsed.Key);
            // A key may itself hold the separator.
            Assert.True(ConversionCause.TryParse("ScienceEarning|Science|a|b", out parsed));
            Assert.Equal("a|b", parsed.Key);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("MilestoneAchievement")]
        [InlineData("MilestoneAchievement|Funds|")]
        [InlineData("|Funds|x")]
        [InlineData("Nope|Funds|x")]
        [InlineData("MilestoneAchievement|Gold|x")]
        [InlineData("FundsEarning|Funds|x")]
        public void Cause_Malformed_DoesNotParse(string raw)
        {
            ConversionCause parsed;
            Assert.False(ConversionCause.TryParse(raw, out parsed));
        }

        [Fact]
        public void KeyOf_NamesEachCauseRowTheWayTheScopeDoes()
        {
            Assert.Equal("Mun/Landing", ConversionCause.KeyOf(Milestone("m", "Mun/Landing", 1, 1, 1)));
            Assert.Equal("k", ConversionCause.KeyOf(ContractDone("c", "k", 1, 1, 1)));
            Assert.Equal("k/0.1", ConversionCause.KeyOf(new GameAction
            { Type = GameActionType.ContractParameterComplete, ContractId = "k", ContractParameterId = "0.1" }));
            Assert.Equal("k", ConversionCause.KeyOf(new GameAction { Type = GameActionType.ContractFail, ContractId = "k" }));
            Assert.Equal("crewReport@KerbinSrfLanded", ConversionCause.KeyOf(Subject("s", "crewReport@KerbinSrfLanded", 1, 1, 1, 5)));
            Assert.Equal("Mun", ConversionCause.KeyOf(new GameAction { Type = GameActionType.ScienceLabEarning, ScanBodyName = "Mun" }));
            Assert.Null(ConversionCause.KeyOf(new GameAction { Type = GameActionType.ScienceLabEarning }));
            Assert.Null(ConversionCause.KeyOf(new GameAction { Type = GameActionType.FundsEarning }));
            Assert.Null(ConversionCause.ParameterKey("k", null));
        }

        // ------------------------------------------------------------------
        // The capture scope
        // ------------------------------------------------------------------

        [Fact]
        public void Scope_InnermostAwardWithAMatchingReason_IsTheCause()
        {
            int outer = ConversionCauseScope.Open(GameActionType.ScienceEarning, "subj", "ScienceTransmission", "VesselRecovery");
            int inner = ConversionCauseScope.Open(GameActionType.ContractComplete, "k", "ContractReward");
            ConversionCause cause;
            Assert.True(ConversionCauseScope.TryResolve("ContractReward", StockPool.Funds, out cause));
            Assert.Equal("ContractComplete|Funds|k", cause.Format());
            // The innermost award does not pay with the subject's reason: no positive evidence.
            Assert.False(ConversionCauseScope.TryResolve("ScienceTransmission", StockPool.Science, out cause));
            ConversionCauseScope.Close(inner);
            Assert.True(ConversionCauseScope.TryResolve("ScienceTransmission", StockPool.Science, out cause));
            Assert.Equal("ScienceEarning|Science|subj", cause.Format());
            ConversionCauseScope.Close(outer);
            Assert.Equal(0, ConversionCauseScope.Depth);
            Assert.False(ConversionCauseScope.TryResolve("ScienceTransmission", StockPool.Science, out cause));
        }

        [Fact]
        public void Scope_WithNoKey_BlocksTheOuterAward()
        {
            int outer = ConversionCauseScope.Open(GameActionType.ContractComplete, "k", "ContractReward");
            int inner = ConversionCauseScope.Open(GameActionType.ContractParameterComplete, null, "ContractReward");
            ConversionCause cause;
            Assert.False(ConversionCauseScope.TryResolve("ContractReward", StockPool.Funds, out cause));
            ConversionCauseScope.Close(inner);
            ConversionCauseScope.Close(outer);
        }

        [Fact]
        public void Scope_ClosingAnOuterToken_ClosesALeakedInnerOne_AndWarns()
        {
            int outer = ConversionCauseScope.Open(GameActionType.ContractComplete, "k", "ContractReward");
            ConversionCauseScope.Open(GameActionType.MilestoneAchievement, "m", "Progression");
            ConversionCauseScope.Close(outer);
            Assert.Equal(0, ConversionCauseScope.Depth);
            Assert.Contains(logLines, l => l.Contains("[WARN][ConversionCause]") && l.Contains("1 inner scope(s) still open"));
            ConversionCauseScope.Close(0);
            ConversionCauseScope.Close(5);
            Assert.Equal(0, ConversionCauseScope.Depth);
        }

        [Fact]
        public void SourcePool_IsTheQuerysOneInputCurrency()
        {
            StockPool pool;
            Assert.True(ConversionCauseScope.TryGetSourcePool(new StrategyConversionQuery { InputFunds = 100 }, out pool));
            Assert.Equal(StockPool.Funds, pool);
            Assert.True(ConversionCauseScope.TryGetSourcePool(new StrategyConversionQuery { InputReputation = 3 }, out pool));
            Assert.Equal(StockPool.Reputation, pool);
            Assert.True(ConversionCauseScope.TryGetSourcePool(new StrategyConversionQuery { InputScience = 2, DeltaFunds = 5 }, out pool));
            Assert.Equal(StockPool.Science, pool);
            Assert.False(ConversionCauseScope.TryGetSourcePool(new StrategyConversionQuery(), out pool));
            Assert.False(ConversionCauseScope.TryGetSourcePool(new StrategyConversionQuery { InputFunds = 1, InputScience = 1 }, out pool));
        }

        [Fact]
        public void Recorder_ResolvesTheCauseFromTheOpenScope_OrNone()
        {
            var q = new StrategyConversionQuery { InputFunds = 5000, DeltaFunds = -1000, DeltaReputation = 10, Reason = "Progression" };
            Assert.Null(GameStateRecorder.ResolveConversionCause(q));
            Assert.Contains(logLines, l => l.Contains("inside no matching award scope"));

            int token = ConversionCauseScope.Open(GameActionType.MilestoneAchievement, "Mun/Landing", "Progression");
            Assert.Equal("MilestoneAchievement|Funds|Mun/Landing", GameStateRecorder.ResolveConversionCause(q));
            Assert.Contains(logLines, l => l.Contains("legs name their cause MilestoneAchievement|Funds|Mun/Landing"));

            // A mod paying with a strategy reason inside the award: no positive evidence.
            q.Reason = "StrategyOutput";
            Assert.Null(GameStateRecorder.ResolveConversionCause(q));
            q.Reason = "Progression";
            q.InputScience = 2;
            Assert.Null(GameStateRecorder.ResolveConversionCause(q));
            Assert.Contains(logLines, l => l.Contains("no single input currency"));
            ConversionCauseScope.Close(token);
        }

        // ------------------------------------------------------------------
        // The Harmony scopes resolve against stock and open / close
        // ------------------------------------------------------------------

        [Fact]
        public void ScopePatches_ResolveTheirStockTargets()
        {
            Assert.NotNull(ConversionCauseScopeTargets.Contract("AwardCompletion"));
            Assert.NotNull(ConversionCauseScopeTargets.Contract("PenalizeFailure"));
            Assert.NotNull(ConversionCauseScopeTargets.Contract("PenalizeCancellation"));
            Assert.NotNull(ConversionCauseScopeTargets.ParameterAwardCompletion());
            Assert.NotNull(DeployedScienceSubmitScopePatch.ResolveTargetMethodForTesting());
            Assert.NotNull(HarmonyLib.AccessTools.Method(typeof(ProgressNode), "AwardProgress",
                new[] { typeof(string), typeof(float), typeof(float), typeof(float), typeof(CelestialBody) }));
            Assert.NotNull(HarmonyLib.AccessTools.Method(typeof(ModuleOrbitalSurveyor), "CompleteSurvey"));
        }

        [Fact]
        public void SciencePatch_OpensTheSubjectScope_AndTheFinalizerClosesIt_EvenOnAThrow()
        {
            var subject = (ScienceSubject)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(ScienceSubject));
            subject.id = "crewReport@MunSrfLandedMidlands";
            var prefix = typeof(ScienceSubjectConversionCauseScopePatch).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            var finalizer = typeof(ScienceSubjectConversionCauseScopePatch).GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic);
            var args = new object[] { subject, 0 };
            prefix.Invoke(null, args);
            int token = (int)args[1];
            Assert.True(token > 0);
            ConversionCause cause;
            Assert.True(ConversionCauseScope.TryResolve("VesselRecovery", StockPool.Science, out cause));
            Assert.Equal("ScienceEarning|Science|crewReport@MunSrfLandedMidlands", cause.Format());
            var thrown = new InvalidOperationException("stock threw");
            Assert.Same(thrown, finalizer.Invoke(null, new object[] { thrown, token }));
            Assert.Equal(0, ConversionCauseScope.Depth);
        }

        // ------------------------------------------------------------------
        // Pure share and binding
        // ------------------------------------------------------------------

        [Fact]
        public void CauseShare_ByOutcome()
        {
            var counted = Milestone("m", "Mun/Landing", 1, 1, 5000);
            Assert.Equal(1f, ConversionLegBinding.CauseShare(counted, StockPool.Funds));
            counted.Effective = false;
            Assert.Equal(0f, ConversionLegBinding.CauseShare(counted, StockPool.Funds));

            var record = Record("r", "RecordsAltitude", 1, 1, "rec", 1, 2, 3);
            record.RecordPartlyCredited = true;
            record.RecordCreditedFunds = 3000f;
            Assert.Equal(0.5f, ConversionLegBinding.CauseShare(record, StockPool.Funds));

            var subject = Subject("s", "x", 1, 1, 8f, 10f);
            subject.EffectiveScience = 2f;
            Assert.Equal(0.25f, ConversionLegBinding.CauseShare(subject, StockPool.Science));
            subject.EffectiveScience = 7.99995f;
            Assert.Equal(1f, ConversionLegBinding.CauseShare(subject, StockPool.Science));

            Assert.Equal(1f, ConversionLegBinding.CauseShare(ContractDone("c", "k", 1, 1, 100), StockPool.Funds));
            Assert.Equal(1f, ConversionLegBinding.CauseShare(null, StockPool.Funds));
        }

        [Fact]
        public void TakeBackFraction_IsTheCausesOwnTakeBackOverItsCapture()
        {
            var dup = Milestone("m", "Mun/Landing", 1, 1, 5000, 10);
            Assert.Equal(0f, ConversionLegBinding.CauseTakeBackFraction(dup, StockPool.Funds));
            dup.TakeBackFunds = 5000f;
            Assert.Equal(1f, ConversionLegBinding.CauseTakeBackFraction(dup, StockPool.Funds));
            Assert.Equal(0f, ConversionLegBinding.CauseTakeBackFraction(dup, StockPool.Reputation));
            var fail = new GameAction { Type = GameActionType.ContractFail, ContractId = "k", FundsPenalty = 400, TakeBackFunds = -400 };
            Assert.Equal(1f, ConversionLegBinding.CauseTakeBackFraction(fail, StockPool.Funds));
            var subject = Subject("s", "x", 1, 1, 8f, 10f);
            subject.TakeBackScience = 2f;
            Assert.Equal(0.25f, ConversionLegBinding.CauseTakeBackFraction(subject, StockPool.Science));
        }

        [Fact]
        public void FindCause_NearestCapture_AmbiguousTie_WindowAndFoldedRecord()
        {
            var cause = new ConversionCause { Type = GameActionType.MilestoneAchievement, SourcePool = StockPool.Funds, Key = "Mun/Landing" };
            var leg = FundsDebitLeg("l", 100, 21, 10, cause.Format());
            var near = Milestone("a", "Mun/Landing", 100, 20, 1);
            var far = Milestone("b", "Mun/Landing", 100.5, 40, 1);
            var outOfWindow = Milestone("c", "Mun/Landing", 102, 22, 1);
            string outcome;
            Assert.Same(near, ConversionLegBinding.FindCause(leg, cause, new[] { far, near, outOfWindow }, out outcome));
            Assert.Equal("bound", outcome);

            var twin = Milestone("d", "Mun/Landing", 100, 22, 1);
            Assert.Null(ConversionLegBinding.FindCause(leg, cause, new[] { near, twin }, out outcome));
            Assert.Equal("ambiguous-cause", outcome);

            Assert.Null(ConversionLegBinding.FindCause(leg, cause, new[] { outOfWindow }, out outcome));
            Assert.Equal("no-cause-row", outcome);

            // A world record folds later breaks into its seed row: the latest row of the leg's
            // recording at or before the leg is its cause.
            var recCause = new ConversionCause { Type = GameActionType.MilestoneAchievement, SourcePool = StockPool.Funds, Key = "RecordsAltitude" };
            var recLeg = FundsDebitLeg("rl", 300, 90, 10, recCause.Format());
            recLeg.RecordingId = "rec";
            var seed = Record("r1", "RecordsAltitude", 120, 30, "rec", 1, 2);
            var other = Record("r2", "RecordsAltitude", 150, 31, "other", 1);
            Assert.Same(seed, ConversionLegBinding.FindCause(recLeg, recCause, new[] { seed, other }, out outcome));
            Assert.Equal("bound-folded-record", outcome);
        }

        // ------------------------------------------------------------------
        // The walk
        // ------------------------------------------------------------------

        // catches: a counted cause rescaling its legs, or the leg pass walking twice when no
        // leg moves.
        [Fact]
        public void CountedCause_LegsWalkAsCaptured_InOneWalk()
        {
            Seeds();
            string c = Cause(GameActionType.MilestoneAchievement, StockPool.Funds, "Mun/Landing");
            ledger.Add(FundsDebitLeg("l1", 100, 9, 1000, c));
            ledger.Add(RepCreditLeg("l2", 100, 9, 10, c));
            ledger.Add(Milestone("a", "Mun/Landing", 100, 10, 5000));
            Walk();
            Assert.Equal(4000.0, funds.GetRunningBalance(), 6);
            Assert.Equal((double)ReputationModule.ApplyReputationCurve(10f, 0f).actualDelta, reputation.GetRunningRep(), 5);
            Assert.False(Rewalked);
            Assert.Contains(logLines, l => l.Contains("[ConversionLeg]") && l.Contains("bound=2") && l.Contains("rescaled=0"));
        }

        // catches: a duplicate award's strategy cut kept after the award itself is zeroed (the
        // hand-checked career's "strategy cut kept from a zeroed parameter").
        [Fact]
        public void DuplicateCause_TakesItsLegsWithIt()
        {
            Seeds();
            string c = Cause(GameActionType.MilestoneAchievement, StockPool.Funds, "Mun/Landing");
            ledger.Add(Milestone("a", "Mun/Landing", 100, 10, 5000));
            ledger.Add(FundsDebitLeg("la1", 100, 11, 1000, c));
            ledger.Add(RepCreditLeg("la2", 100, 11, 10, c));
            // A later timeline reaches the milestone again (captured after the committed one).
            ledger.Add(Milestone("b", "Mun/Landing", 50, 20, 5000));
            GameAction lb1 = FundsDebitLeg("lb1", 50, 19, 1000, c);
            GameAction lb2 = RepCreditLeg("lb2", 50, 19, 10, c);
            ledger.Add(lb1);
            ledger.Add(lb2);
            Walk();

            GameAction a = ledger.Single(x => x.ActionId == "a");
            GameAction b = ledger.Single(x => x.ActionId == "b");
            GameAction la1 = ledger.Single(x => x.ActionId == "la1");
            // The earlier UT wins the identity; the committed row at 100 is the duplicate.
            Assert.True(b.Effective);
            Assert.False(a.Effective);
            Assert.False(la1.Effective);
            Assert.Equal(GameActionNotCountedReason.ConversionCauseNotCounted, la1.NotCountedReason);
            Assert.Equal(0f, la1.ConversionShare);
            Assert.True(lb1.Effective);
            Assert.Equal(1f, lb1.ConversionShare);
            Assert.Equal(4000.0, funds.GetRunningBalance(), 6);
            Assert.Equal((double)ReputationModule.ApplyReputationCurve(10f, 0f).actualDelta, reputation.GetRunningRep(), 5);
            Assert.True(Rewalked);
            Assert.Contains(logLines, l => l.Contains("[ConversionLeg]") && l.Contains("id=la1")
                && l.Contains("follows MilestoneAchievement 'Mun/Landing'") && l.Contains("share=0"));
            Assert.Equal("Not counted: the reward it came from was not counted, so nothing was converted.",
                TimelineRowHover.NotCounted(la1.NotCountedReason));
        }

        // catches: a leg whose cause is unknown (a mod paying with a strategy reason) being cut.
        [Fact]
        public void LegWithNoCauseOrNoCauseRow_StaysAsCaptured()
        {
            Seeds();
            ledger.Add(Milestone("a", "Mun/Landing", 50, 10, 5000));
            ledger.Add(Milestone("b", "Mun/Landing", 100, 20, 5000));
            // No named cause: a mod's strategy payment.
            ledger.Add(FundsDebitLeg("free", 100, 21, 300, null));
            // Names a cause whose row is not in the ledger (a flight not yet committed).
            ledger.Add(FundsDebitLeg("orphan", 100, 22, 200,
                Cause(GameActionType.ContractComplete, StockPool.Funds, "missing")));
            Walk();
            Assert.Equal(5000.0 - 300 - 200, funds.GetRunningBalance(), 6);
            Assert.Contains(logLines, l => l.Contains("id=orphan") && l.Contains("no-cause-row") && l.Contains("applied as captured"));
            Assert.False(Rewalked);
        }

        // catches: a partly credited world record converting its whole captured payout.
        [Fact]
        public void PartlyCreditedRecord_ScalesItsLegsByTheCreditedShare()
        {
            Seeds();
            string c = Cause(GameActionType.MilestoneAchievement, StockPool.Funds, "RecordsAltitude");
            ledger.Add(Record("r1", "RecordsAltitude", 50, 30, "recA", 1, 2));
            // A committed flight captured before the rewind crossed thresholds 1..4.
            ledger.Add(Record("r2", "RecordsAltitude", 100, 10, "recB", 1, 2, 3, 4));
            GameAction leg = FundsDebitLeg("lr2", 100, 11, 1000, c);
            leg.RecordingId = "recB";
            ledger.Add(leg);
            Walk();
            GameAction r2 = ledger.Single(x => x.ActionId == "r2");
            Assert.True(r2.RecordPartlyCredited);
            float expected = r2.RecordCreditedFunds / r2.MilestoneFundsAwarded;
            Assert.Equal(expected, leg.ConversionShare);
            Assert.True(leg.Effective);
            Assert.Equal(3000.0 + r2.RecordCreditedFunds - 1000.0 * expected, funds.GetRunningBalance(), 3);
        }

        // catches: a capped science subject converting the science the cap left out.
        [Fact]
        public void CappedSubject_ScalesItsScienceLegs()
        {
            Seeds(0, 0, 0);
            string c = Cause(GameActionType.ScienceEarning, StockPool.Science, "goo@Kerbin");
            ledger.Add(Subject("s1", "goo@Kerbin", 50, 20, 6f, 8f));
            ledger.Add(Subject("s2", "goo@Kerbin", 100, 10, 4f, 8f));
            GameAction debit = ScienceDebitLeg("d2", 100, 11, 1f, c);
            GameAction credit = FundsCreditLeg("f2", 100, 11, 500f, c);
            ledger.Add(debit);
            ledger.Add(credit);
            Walk();
            Assert.Equal(0.5f, debit.ConversionShare);
            Assert.Equal(0.5f, credit.ConversionShare);
            Assert.Equal(8.0 - 0.5, science.GetRunningScience(), 4);
            Assert.Equal(250.0, funds.GetRunningBalance(), 4);
        }

        // catches: a not-counted conversion leg sealing a Re-Fly slot when its cause's payment
        // was taken back, or leaving the seal when stock's leg payment is kept (3.15 item 10).
        [Fact]
        public void ReFlySeal_ALegTakenBackWithItsCauseStaysOut_AKeptLegSeals()
        {
            string c = Cause(GameActionType.MilestoneAchievement, StockPool.Funds, "Mun/Landing");
            GameAction taken = FundsCreditLeg("t", 120, 21, 500f, c);
            taken.Effective = false;
            taken.NotCountedReason = GameActionNotCountedReason.ConversionCauseNotCounted;
            taken.TakeBackFunds = 500f;
            Assert.Equal("ConversionCauseNotCounted", ReplayCredit.NotCountedLabel(taken, scienceWalked: false));

            GameAction kept = FundsCreditLeg("k", 120, 22, 500f, c);
            kept.Effective = false;
            kept.NotCountedReason = GameActionNotCountedReason.ConversionCauseNotCounted;
            Assert.Null(ReplayCredit.NotCountedLabel(kept, scienceWalked: false));
        }

        // catches: the probe / balance walks doubling their samples on the second walk.
        [Fact]
        public void BalanceWalk_OnARewalk_KeepsOneSamplePerRow()
        {
            Seeds();
            string c = Cause(GameActionType.MilestoneAchievement, StockPool.Funds, "Mun/Landing");
            ledger.Add(Milestone("a", "Mun/Landing", 100, 10, 5000));
            ledger.Add(FundsDebitLeg("la1", 100, 11, 1000, c));
            ledger.Add(Milestone("b", "Mun/Landing", 50, 20, 5000));
            var walk = RecalculationEngine.WalkBalancesOnClones(ledger);
            Assert.Equal(ledger.Count, walk.Samples.Count);
            Assert.Equal(5000.0, walk.Samples.Last().Funds, 6);
            Assert.False(walk.EffectiveByActionId["la1"]);
        }

        // ------------------------------------------------------------------
        // Take-back (design 3.15 step 5) of a duplicate conversion
        // ------------------------------------------------------------------

        private double PatchFunds(double now, double live, bool authoritative = false)
        {
            Walk(now);
            var state = StockPoolReference.Funds;
            List<StockPoolRow> rows = StockPoolReference.BuildRows(
                StockPool.Funds, ledger, funds.GetRowEffects(), funds.GetTakeBackDeltas());
            double min = funds.GetProjectionMinBalance();
            StockPoolMeasurement m = StockPoolReference.Measure(
                state, rows, now, live, 0.0, authoritative, 0.01, min,
                new HashSet<string>(ledger.Select(a => a.ActionId), StringComparer.Ordinal), clampAtZero: true);
            if (m.Adjustment != 0.0)
            {
                GameAction row = StockPoolReference.BuildAdjustmentRow(StockPool.Funds, m.Adjustment, now);
                ledger.Add(row);
                m.AdjustmentRow = row;
            }
            KspStatePatcher.EmitTakeBacks(StockPool.Funds, m, ledger);
            double write = m.GapAdopted
                ? live
                : StockPoolReference.ResolveCurrencyWriteTarget(min, funds.GetRunningBalance(), live, false, m.Adjustment);
            StockPoolReference.Commit(state, rows, m, write, 0.0);
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
                state, rows, now, live, 0.0, authoritative, 0.01, running,
                new HashSet<string>(ledger.Select(a => a.ActionId), StringComparer.Ordinal));
            if (m.Adjustment != 0.0)
            {
                GameAction row = StockPoolReference.BuildAdjustmentRow(StockPool.Reputation, m.Adjustment, now);
                ledger.Add(row);
                m.AdjustmentRow = row;
            }
            KspStatePatcher.EmitTakeBacks(StockPool.Reputation, m, ledger);
            double write = m.GapAdopted ? live : StockPoolReference.ResolveReputationWriteTarget(running, m.Adjustment);
            StockPoolReference.Commit(state, rows, m, write, 0.0);
            return write;
        }

        // catches: stock's duplicate conversion after a rewind kept (the cut charged twice) or
        // filed as a pool adjustment.
        [Fact]
        public void Rewind_DuplicateAwardsConversion_IsTakenBack_InBothCurrencies()
        {
            StockShadowTakeBack.SetEligibleFromForTesting(1);
            StockShadowTakeBack.AddBreakForTesting(13, 50, "PlainRewind");
            Seeds(0, 0, 0);
            string c = Cause(GameActionType.MilestoneAchievement, StockPool.Funds, "Mun/Landing");
            ledger.Add(Milestone("a", "Mun/Landing", 100, 10, 5000));
            ledger.Add(FundsDebitLeg("la1", 100, 11, 1000, c));
            ledger.Add(RepCreditLeg("la2", 100, 12, 10, c));
            PatchFunds(50, 0, authoritative: true);
            PatchReputation(50, 0, authoritative: true);

            // After the rewind stock pays the milestone again, and the strategy converts again.
            GameAction b = Milestone("b", "Mun/Landing", 120, 20, 5000);
            GameAction lb1 = FundsDebitLeg("lb1", 120, 21, 1000, c);
            GameAction lb2 = RepCreditLeg("lb2", 120, 22, 10, c);
            ledger.Add(b);
            ledger.Add(lb1);
            ledger.Add(lb2);
            float stockRep = ReputationModule.ApplyReputationCurve(10f, 0f).actualDelta;

            Assert.Equal(4000.0, PatchFunds(130, 4000), 6);
            Assert.Equal(GameActionNotCountedReason.ConversionCauseNotCounted, lb1.NotCountedReason);
            Assert.Equal(-1000f, lb1.TakeBackFunds);
            Assert.Equal(10f, lb2.TakeBackRep);
            // Stock's pool after the rewind holds only the duplicate conversion; the committed
            // leg at UT 100 crosses the clock and the write applies it.
            double repWrite = PatchReputation(130, stockRep);
            Assert.Equal((double)stockRep, repWrite, 4);
            Assert.Equal(0, ledger.Count(x => x.Type == GameActionType.StockPoolAdjustment));
            Assert.Contains(logLines, l => l.Contains("[WARN][TakeBack]") && l.Contains("Took back -1000 Funds")
                && l.Contains("FundsSpending conversion of MilestoneAchievement|Funds|Mun/Landing")
                && l.Contains("ConversionCauseNotCounted"));
            Assert.Contains(logLines, l => l.Contains("[WARN][TakeBack]") && l.Contains("Reputation")
                && l.Contains("ReputationEarning conversion of"));
        }

        // catches: a leg captured before the take-back era being taken back.
        [Fact]
        public void LegBeforeTheEra_IsZeroedButNeverTakenBack()
        {
            StockShadowTakeBack.SetEligibleFromForTesting(21);
            StockShadowTakeBack.AddBreakForTesting(13, 50, "PlainRewind");
            Seeds(0, 0, 0);
            string c = Cause(GameActionType.MilestoneAchievement, StockPool.Funds, "Mun/Landing");
            ledger.Add(Milestone("a", "Mun/Landing", 100, 10, 5000));
            GameAction b = Milestone("b", "Mun/Landing", 120, 22, 5000);
            GameAction lb1 = FundsDebitLeg("lb1", 120, 20, 1000, c);
            ledger.Add(b);
            ledger.Add(lb1);
            Walk();
            Assert.Equal(0f, lb1.ConversionShare);
            Assert.False(lb1.HasTakeBack);
        }

        // ------------------------------------------------------------------
        // Never-rewound careers walk exactly as before (randomized)
        // ------------------------------------------------------------------

        private static List<GameAction> RandomCareer(int seed, bool withCauses)
        {
            var rng = new Random(seed);
            var rows = new List<GameAction>
            {
                new GameAction { ActionId = "seedF", UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 20000f },
                new GameAction { ActionId = "seedS", UT = 0, Type = GameActionType.ScienceInitial, InitialScience = 5f },
                new GameAction { ActionId = "seedR", UT = 0, Type = GameActionType.ReputationInitial, InitialReputation = 3f },
            };
            double ut = 10;
            long cseq = 1;
            int recordTop = 0;
            int n = 20 + rng.Next(30);
            for (int i = 0; i < n; i++)
            {
                ut += 1 + rng.Next(50) + rng.NextDouble();
                GameAction cause;
                StockPool pool;
                switch (rng.Next(4))
                {
                    case 0:
                        cause = Milestone("m" + i, "Body" + i + "/Landing", ut, cseq++,
                            (float)(rng.Next(1, 90) * 100), (float)rng.Next(0, 20), (float)rng.Next(0, 5));
                        pool = rng.Next(2) == 0 ? StockPool.Funds : StockPool.Reputation;
                        break;
                    case 1:
                        rows.Add(Accept("k" + i, ut - 0.5, cseq++));
                        cause = ContractDone("c" + i, "k" + i, ut, cseq++, (float)(rng.Next(1, 50) * 100));
                        pool = StockPool.Funds;
                        break;
                    case 2:
                        cause = Subject("s" + i, "subj" + i, ut, cseq++, (float)(1 + rng.Next(20)), 30f);
                        pool = StockPool.Science;
                        break;
                    default:
                        recordTop++;
                        cause = Record("r" + i, "RecordsAltitude", ut, cseq++, "rec" + i, recordTop);
                        pool = StockPool.Funds;
                        break;
                }
                rows.Add(cause);
                if (rng.Next(3) == 0)
                    continue;
                string text = withCauses ? Cause(cause.Type, pool, ConversionCause.KeyOf(cause)) : null;
                // Legs are captured inside the award, before or after its row.
                long legSeq = rng.Next(2) == 0 ? cause.CaptureSequence - 1 : cseq++;
                switch (rng.Next(3))
                {
                    case 0:
                        rows.Add(FundsDebitLeg("ld" + i, ut, legSeq, (float)(rng.Next(1, 20) * 10), text));
                        rows.Add(RepCreditLeg("lr" + i, ut, legSeq, (float)rng.Next(1, 6), text));
                        break;
                    case 1:
                        rows.Add(ScienceDebitLeg("ls" + i, ut, legSeq, (float)rng.Next(1, 4), text));
                        rows.Add(FundsCreditLeg("lf" + i, ut, legSeq, (float)(rng.Next(1, 30) * 10), text));
                        break;
                    default:
                        rows.Add(new GameAction
                        {
                            ActionId = "lp" + i, UT = ut, CaptureSequence = legSeq, Type = GameActionType.ReputationPenalty,
                            NominalPenalty = (float)rng.Next(1, 4), RepPenaltySource = ReputationPenaltySource.StrategyConverter,
                            ConversionCause = text,
                        });
                        rows.Add(new GameAction
                        {
                            ActionId = "lc" + i, UT = ut, CaptureSequence = legSeq, Type = GameActionType.StrategyScienceCredit,
                            ScienceAwarded = (float)rng.Next(1, 4), ConversionCause = text,
                        });
                        break;
                }
            }
            return rows;
        }

        // catches: any never-rewound career ending differently once its legs name their causes.
        [Fact]
        public void NeverRewoundCareers_EndExactlyAsBefore_Randomized()
        {
            for (int seed = 1; seed <= 60; seed++)
            {
                RecalculationEngine.Recalculate(RandomCareer(seed, withCauses: false));
                double f0 = funds.GetRunningBalance(), s0 = science.GetRunningScience(), r0 = reputation.GetRunningRep();
                double fa0 = funds.GetAvailableFunds(), sa0 = science.GetAvailableScience();

                logLines.Clear();
                List<GameAction> caused = RandomCareer(seed, withCauses: true);
                RecalculationEngine.Recalculate(caused);
                Assert.True(f0 == funds.GetRunningBalance(), "funds differ for seed " + seed);
                Assert.True(s0 == science.GetRunningScience(), "science differs for seed " + seed);
                Assert.True(r0 == reputation.GetRunningRep(), "reputation differs for seed " + seed);
                Assert.True(fa0 == funds.GetAvailableFunds(), "available funds differ for seed " + seed);
                Assert.True(sa0 == science.GetAvailableScience(), "available science differs for seed " + seed);
                Assert.False(Rewalked, "seed " + seed + " walked twice");
                Assert.All(caused.Where(ConversionLegBinding.IsCausedLeg), l => Assert.Equal(1f, l.ConversionShare));
            }
        }

        // ------------------------------------------------------------------
        // Serialization and registry
        // ------------------------------------------------------------------

        [Fact]
        public void ConversionCauseAndIncomeReason_RoundTrip_AndAreAbsentWhenUnset()
        {
            var leg = FundsDebitLeg("l", 100, 7, 50, Cause(GameActionType.ContractComplete, StockPool.Funds, "g"));
            var root = new ConfigNode("ROOT");
            leg.SerializeInto(root);
            ConfigNode node = root.GetNode("GAME_ACTION");
            Assert.Equal("ContractComplete|Funds|g", node.GetValue(GameAction.ConversionCauseKey));
            Assert.Null(node.GetValue(GameAction.IncomeReasonKey));
            Assert.Equal("ContractComplete|Funds|g", GameAction.DeserializeFrom(node).ConversionCause);

            var plain = new ConfigNode("ROOT");
            new GameAction { UT = 1, Type = GameActionType.FundsSpending, FundsSpent = 1 }.SerializeInto(plain);
            Assert.Null(plain.GetNode("GAME_ACTION").GetValue(GameAction.ConversionCauseKey));
            Assert.Null(GameAction.DeserializeFrom(plain.GetNode("GAME_ACTION")).ConversionCause);
        }

        [Fact]
        public void Registry_LegEntriesFollowTheirCause_Enforced_AndTakeBack()
        {
            foreach (string name in new[]
            {
                "StrategyScienceDebit/Converter", "StrategyScienceCredit", "FundsSpending/StrategyConverter",
                "FundsEarning/Strategy", "ReputationEarning/Strategy", "ReputationPenalty/StrategyConverter",
            })
            {
                var e = LedgerEventIdentityRegistry.Entries.Single(x => x.Name == name);
                Assert.Equal(LedgerIdentityRule.FollowsCause, e.Rule);
                Assert.True(e.EnforcedToday, name);
                Assert.True(e.TakesBackStockRepeat, name);
                Assert.Contains("ConversionLegBinding", e.EnforcedBy);
            }
            Assert.True(StockShadowTakeBack.IsIdentityReason(GameActionNotCountedReason.ConversionCauseNotCounted));
        }

        // catches: the recorder door capturing legs without naming the cause it resolved.
        [Fact]
        public void Wiring_RecorderPassesTheResolvedCauseToTheLegWriter()
        {
            string path = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "Source", "Parsek", "GameStateRecorder.cs"));
            Assert.True(File.Exists(path), "source file not found for scan: " + path);
            string recorder = File.ReadAllText(path);
            int door = recorder.IndexOf("void OnCurrencyModified(", StringComparison.Ordinal);
            int resolve = recorder.IndexOf("string conversionCause = ResolveConversionCause(snapshot);", door, StringComparison.Ordinal);
            int write = recorder.IndexOf(
                "LedgerOrchestrator.OnStrategyCurrencyConversion(ut, legs, snapshot.Reason, conversionCause);",
                door, StringComparison.Ordinal);
            Assert.True(door >= 0 && resolve > door && write > resolve,
                "OnCurrencyModified must resolve the cause and hand it to the leg writer");
        }
    }
}
