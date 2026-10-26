using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// RECORD-BAND-REEARNED-AFTER-REWIND-PAID-AGAIN / LEDGER-EVENT-IDENTITY step 4 (design 7.8,
    /// 7.9, section 3.15 step 4): each threshold of a stock world-record node pays once per
    /// career, earliest crossing in walk order first. A record row names the thresholds it
    /// crossed (stock's interval) and the reward of each; an older row takes consecutive
    /// positions after the band stock had when it was captured. The live mirror raises
    /// stock's band to the committed count at the clock, add-only.
    /// </summary>
    [Collection("Sequential")]
    public class RecordThresholdOncePerCareerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RecordThresholdOncePerCareerTests()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            LedgerOrchestrator.SetResourceTrackingAvailabilityForTesting(true, true, true);
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            CommittedMilestoneReach.ResetForTesting();
            GameStateRecorder.PendingMilestoneEventById.Clear();
            GameStateRecorder.IsReplayingActions = false;
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            CommittedMilestoneReach.ResetForTesting();
            GameStateRecorder.PendingMilestoneEventById.Clear();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekScenario.CurrentTimelineUTProviderForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Helpers
        // ================================================================

        private sealed class Walk
        {
            internal readonly MilestonesModule Milestones = new MilestonesModule();
            internal readonly ScienceModule Science = new ScienceModule();
            internal readonly FundsModule Funds = new FundsModule();
            internal readonly ReputationModule Rep = new ReputationModule();
        }

        // Production tiers (LedgerOrchestrator.Initialize), the subset a milestone touches.
        private static Walk Register(PreLedgerProgressSeed seed = null)
        {
            var w = new Walk();
            if (seed != null)
                w.Milestones.SetPreLedgerProgressSeed(seed);
            RecalculationEngine.RegisterModule(w.Milestones, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(w.Science, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(w.Funds, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.RegisterModule(w.Rep, RecalculationEngine.ModuleTier.SecondTier);
            return w;
        }

        private static List<GameAction> Seeds()
        {
            return new List<GameAction>
            {
                new GameAction { UT = 0, Type = GameActionType.FundsInitial, InitialFunds = 100000f },
                new GameAction { UT = 0, Type = GameActionType.ScienceInitial, InitialScience = 0f },
                new GameAction { UT = 0, Type = GameActionType.ReputationInitial, InitialReputation = 0f },
            };
        }

        /// <summary>A row that names its thresholds; each threshold i pays i*1000 funds,
        /// i*0.5 rep and i*0.25 science, and the totals are the sum.</summary>
        private static GameAction Named(string id, double ut, long cseq, string recordingId, params int[] indices)
        {
            var band = indices.Select(i => new RecordThresholdCredit(i, i * 1000f, i * 0.5f, i * 0.25f)).ToList();
            return new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = ut,
                CaptureSequence = cseq,
                RecordingId = recordingId,
                MilestoneId = id,
                MilestoneFundsAwarded = band.Sum(c => c.Funds),
                MilestoneRepAwarded = band.Sum(c => c.Rep),
                MilestoneScienceAwarded = band.Sum(c => c.Science),
                MilestoneRecordThresholds = band.Count,
                MilestoneRecordBand = band
            };
        }

        /// <summary>A row with an explicit list (re-payments, custom rewards).</summary>
        private static GameAction Band(string id, double ut, long cseq, string recordingId,
            params RecordThresholdCredit[] band)
        {
            return new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = ut,
                CaptureSequence = cseq,
                RecordingId = recordingId,
                MilestoneId = id,
                MilestoneFundsAwarded = band.Sum(c => c.Funds),
                MilestoneRepAwarded = band.Sum(c => c.Rep),
                MilestoneScienceAwarded = band.Sum(c => c.Science),
                MilestoneRecordThresholds = band.Length,
                MilestoneRecordBand = band.ToList()
            };
        }

        /// <summary>A row written before the field: only a count and the summed totals.</summary>
        private static GameAction Legacy(string id, double ut, long cseq, string recordingId, int thresholds,
            float funds, float rep = 0f, float sci = 0f)
        {
            return new GameAction
            {
                Type = GameActionType.MilestoneAchievement,
                UT = ut,
                CaptureSequence = cseq,
                RecordingId = recordingId,
                MilestoneId = id,
                MilestoneFundsAwarded = funds,
                MilestoneRepAwarded = rep,
                MilestoneScienceAwarded = sci,
                MilestoneRecordThresholds = thresholds
            };
        }

        private static double Funds(Walk w) => w.Funds.GetRunningBalance() - 100000.0;

        private static void SetField<T>(object instance, string name, T value)
        {
            FieldInfo field = instance.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            field.SetValue(instance, value);
        }

        private static T GetField<T>(object instance, string name)
        {
            FieldInfo field = instance.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            return (T)field.GetValue(instance);
        }

        private static FieldInfo ReachedField =>
            typeof(ProgressNode).GetField("reached", BindingFlags.NonPublic | BindingFlags.Instance);

        private static FieldInfo CompleteField =>
            typeof(ProgressNode).GetField("complete", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>Puts a stock record node in the live shape stock holds after paying
        /// <paramref name="paid"/> thresholds (the band the record patch would build).</summary>
        private static void SetLiveBand(ProgressNode node, int paid)
        {
            Assert.True(KspStatePatcher.TryComputeRepeatableRecordState(node, paid, out var state));
            ReachedField.SetValue(node, state.Reached);
            CompleteField.SetValue(node, state.Complete);
            SetField(node, "record", state.Record);
            SetField(node, "rewardThreshold", state.RewardThreshold);
            SetField(node, "rewardInterval", state.RewardInterval);
        }

        private static int LivePaid(ProgressNode node)
        {
            Assert.True(KspStatePatcher.TryReadRepeatableRecordImpliedPaidCount(node, out int paid));
            return paid;
        }

        // ================================================================
        // The re-earn after a rewind
        // ================================================================

        // catches: a threshold the committed flight crossed at UT 700 paid again to a flight
        // that re-crosses it at UT 580 after a rewind (the todo's hole), or the credit staying
        // on the committed row instead of moving to the earlier crossing.
        [Fact]
        public void ReEarnAfterRewind_TheEarlierCrossingIsCreditedAndTheCommittedRowEarnsNothingForIt()
        {
            var w = Register();
            var actions = Seeds();
            // The committed flight: 500 m, 2 km and 7 km at UT 300, then 22 km at UT 700.
            var lowBands = Named("RecordsAltitude", 300.0, 10, "rec-flight1", 1, 2, 3);
            var committed22km = Named("RecordsAltitude", 700.0, 11, "rec-flight1b", 4);
            // Rewind to UT 500; the new flight crosses 22 km at UT 580 (captured later).
            var reclimb = Named("RecordsAltitude", 580.0, 20, "rec-flight2", 4);
            actions.Add(lowBands);
            actions.Add(committed22km);
            actions.Add(reclimb);

            RecalculationEngine.Recalculate(actions);

            Assert.True(lowBands.Effective);
            Assert.True(reclimb.Effective);
            Assert.False(reclimb.RecordPartlyCredited);
            Assert.False(committed22km.Effective);
            Assert.Equal(GameActionNotCountedReason.RecordThresholdsAlreadyCredited, committed22km.NotCountedReason);
            Assert.Equal(4, w.Milestones.GetEffectiveMilestoneCount("RecordsAltitude"));
            // 1000 + 2000 + 3000 for the low bands and 4000 once for 22 km.
            Assert.Equal(10000.0, Funds(w), 3);
            Assert.Equal(0.25 * (1 + 2 + 3 + 4), w.Science.GetRunningScience(), 3);
            Assert.Contains(logLines, l => l.Contains("[Milestones]")
                && l.Contains("Record row 'RecordsAltitude' zeroed at UT=700.0")
                && l.Contains("thresholds=[4] new=[-] source=named"));
        }

        // catches: the reverse order (no rewind) changing anything - the earlier committed row
        // keeps its threshold and the later duplicate earns nothing.
        [Fact]
        public void ReEarnLaterThanTheCommittedCrossing_TheCommittedRowKeepsIt()
        {
            var w = Register();
            var actions = Seeds();
            var committed = Named("RecordsSpeed", 200.0, 10, "rec-a", 1, 2);
            var later = Named("RecordsSpeed", 900.0, 30, "rec-b", 2);
            actions.Add(later);
            actions.Add(committed);

            RecalculationEngine.Recalculate(actions);

            Assert.True(committed.Effective);
            Assert.False(committed.RecordPartlyCredited);
            Assert.False(later.Effective);
            Assert.Equal(3000.0, Funds(w), 3);
            Assert.Equal(2, w.Milestones.GetEffectiveMilestoneCount("RecordsSpeed"));
        }

        // ================================================================
        // Partial overlap
        // ================================================================

        // catches: a partly re-crossed row paid in full, or zeroed in full, or the split
        // applied to one currency only.
        [Fact]
        public void PartialOverlap_ARowEarnsOnlyItsNewThresholds_InEveryCurrency()
        {
            var w = Register();
            var actions = Seeds();
            // Committed at UT 700: thresholds 2, 3 and 4. After a rewind a flight crosses 2 and
            // 3 at UT 580.
            var committed = Named("RecordsAltitude", 700.0, 10, "rec-flight1", 2, 3, 4);
            var reclimb = Named("RecordsAltitude", 580.0, 20, "rec-flight2", 2, 3);
            actions.Add(committed);
            actions.Add(reclimb);

            RecalculationEngine.Recalculate(actions);

            Assert.True(reclimb.Effective);
            Assert.False(reclimb.RecordPartlyCredited);
            Assert.True(committed.Effective);
            Assert.True(committed.RecordPartlyCredited);
            Assert.Equal(3, committed.RecordThresholdsTotal);
            Assert.Equal(1, committed.RecordThresholdsCredited);
            Assert.Equal(4000f, committed.CreditedMilestoneFunds);
            Assert.Equal(2.0f, committed.CreditedMilestoneRep);
            Assert.Equal(1.0f, committed.CreditedMilestoneScience);
            // The captured totals stay as stored.
            Assert.Equal(9000f, committed.MilestoneFundsAwarded);

            Assert.Equal(5000.0 + 4000.0, Funds(w), 3);
            Assert.Equal(1.25 + 1.0, w.Science.GetRunningScience(), 3);
            // Reputation goes through the curve; the credited nominal is what feeds it.
            Assert.True(committed.EffectiveRep > 0f);
            Assert.Equal(3, w.Milestones.GetEffectiveMilestoneCount("RecordsAltitude"));
            Assert.Contains(logLines, l => l.Contains("[Milestones]")
                && l.Contains("Record row 'RecordsAltitude' partly credited at UT=700.0: 1 of 3 threshold(s) new")
                && l.Contains("credited funds=4000"));

            // The Timeline hover names it.
            Assert.Equal("Partly counted: 2 of 3 record steps were already reached earlier on timeline, "
                + "so only the rest paid.", TimelineRowHover.RecordPartlyCounted(committed));
            Assert.Null(TimelineRowHover.RecordPartlyCounted(reclimb));
        }

        // catches: the row-level reconcilers comparing a partly credited row's captured totals
        // against what the walk credits, or the funds row delta reading the captured totals.
        [Fact]
        public void PartialOverlap_RowDeltasReadTheCreditedShare()
        {
            Register();
            var actions = Seeds();
            var committed = Named("RecordsAltitude", 700.0, 10, "rec-flight1", 2, 3);
            var reclimb = Named("RecordsAltitude", 580.0, 20, "rec-flight2", 2);
            actions.Add(committed);
            actions.Add(reclimb);
            RecalculationEngine.Recalculate(actions);

            Assert.True(FundsModule.TryGetRowDelta(committed, out double delta));
            Assert.Equal(3000.0, delta, 3);
            var exp = PostWalkActionReconciler.ClassifyPostWalk(committed);
            Assert.False(exp.Reconcile);
        }

        // ================================================================
        // Rows written before the field (no named thresholds)
        // ================================================================

        // catches: older rows of a career never rewound being re-ordered, split or zeroed.
        [Fact]
        public void LegacyRows_NeverRewound_TakeConsecutivePositionsAndPayInFull()
        {
            var w = Register(PreLedgerProgressSeed.CreateCaptured(
                null, new[] { new KeyValuePair<string, int>("RecordsDistance", 1) }));
            var actions = Seeds();
            var a = Legacy("RecordsDistance", 100.0, 0, "rec-a", 2, 3000f);
            var b = Legacy("RecordsDistance", 200.0, 0, "rec-b", 1, 2500f);
            var completion = Legacy("RecordsDistance", 210.0, 0, "rec-b", 0, 0f);
            actions.Add(b);
            actions.Add(completion);
            actions.Add(a);

            RecalculationEngine.Recalculate(actions);

            Assert.True(a.Effective && b.Effective && completion.Effective);
            Assert.False(a.RecordPartlyCredited || b.RecordPartlyCredited);
            Assert.Equal(4, w.Milestones.GetEffectiveMilestoneCount("RecordsDistance"));
            Assert.Equal(5500.0, Funds(w), 3);
            Assert.Contains(logLines, l => l.Contains("thresholds=[2,3] new=[2,3] source=derived(base=1)"));
            Assert.Contains(logLines, l => l.Contains("thresholds=[4] new=[4] source=derived(base=3)"));
        }

        // catches: an older committed row (no capture number) re-crossed after a rewind by a
        // named row paying in full again - the older row's positions must come from the band
        // stock had when it was captured, not from the walk-earlier newer row - or the zeroed
        // part priced as an even share when the named row says what those thresholds paid.
        [Fact]
        public void LegacyRow_ReCrossedByANamedRowAfterARewind_KeepsItsTotalLessTheNamedRewards()
        {
            var w = Register();
            var actions = Seeds();
            // Written by an older build: four thresholds for 8000 funds, 2 rep, at UT 700.
            var legacy = Legacy("RecordsAltitude", 700.0, 0, "rec-old", 4, 8000f, rep: 2f, sci: 1f);
            // After a rewind a flight on this build crosses 1 and 2 at UT 580.
            var reclimb = Named("RecordsAltitude", 580.0, 40, "rec-new", 1, 2);
            actions.Add(legacy);
            actions.Add(reclimb);

            RecalculationEngine.Recalculate(actions);

            Assert.True(legacy.Effective);
            Assert.True(legacy.RecordPartlyCredited);
            // 8000 less what the re-climb was paid for 1 and 2 (1000 + 2000), likewise rep
            // (0.5 + 1) and science (0.25 + 0.5).
            Assert.Equal(5000f, legacy.CreditedMilestoneFunds);
            Assert.Equal(0.5f, legacy.CreditedMilestoneRep);
            Assert.Equal(0.25f, legacy.CreditedMilestoneScience);
            Assert.Equal(3000.0 + 5000.0, Funds(w), 3);
            Assert.Equal(4, w.Milestones.GetEffectiveMilestoneCount("RecordsAltitude"));
            Assert.Contains(logLines, l => l.Contains("thresholds=[1,2,3,4] new=[3,4] source=derived(base=0)"
                + " zeroed=[CapturedLaterWalkedEarlier,CapturedLaterWalkedEarlier]"));
        }

        // catches: a partly re-crossed older row whose overlap is with another older row priced
        // as anything but the even share (no named reward exists for the zeroed positions).
        [Fact]
        public void LegacyRow_ReCrossedByAnotherLegacyRow_SplitsItsRewardEvenly()
        {
            var w = Register();
            var actions = Seeds();
            var committed = Legacy("RecordsSpeed", 700.0, 10, "rec-flight1", 4, 8000f, rep: 2f);
            var reclimb = Legacy("RecordsSpeed", 580.0, 20, "rec-flight2", 2, 3000f);
            actions.Add(committed);
            actions.Add(reclimb);

            RecalculationEngine.Recalculate(actions);

            Assert.True(committed.RecordPartlyCredited);
            Assert.Equal(4000f, committed.CreditedMilestoneFunds);
            Assert.Equal(1f, committed.CreditedMilestoneRep);
            Assert.Equal(3000.0 + 4000.0, Funds(w), 3);
            Assert.Equal(4, w.Milestones.GetEffectiveMilestoneCount("RecordsSpeed"));
        }

        // ================================================================
        // One timeline: an overlap without rewind evidence is never a duplicate
        // ================================================================

        // catches: an older row's guessed positions blocking a named row captured after it.
        // One timeline: a scope seeded by an older build (its list "-") paid intervals 1 and 3,
        // another scope on this build paid 2 in between. Stock paid 6000 and holds a band of 3.
        [Fact]
        public void DerivedGuess_NeverBlocksANamedRowCapturedAfterIt()
        {
            var w = Register();
            var actions = Seeds();
            var derived = Legacy("RecordsAltitude", 100.0, 10, "", 2, 4000f);
            var named = Named("RecordsAltitude", 200.0, 20, "rec-b", 2);
            actions.Add(derived);
            actions.Add(named);

            RecalculationEngine.Recalculate(actions);

            Assert.True(derived.Effective && named.Effective);
            Assert.False(derived.RecordPartlyCredited || named.RecordPartlyCredited);
            Assert.Equal(6000.0, Funds(w), 3);
            // At least the band stock holds, so the patch never lowers it.
            Assert.Equal(3, w.Milestones.GetEffectiveMilestoneCount("RecordsAltitude"));
        }

        // catches: stock's re-payment of a held interval, made in a second scope of the same
        // session, zeroed as a repeat of the first scope's crossing. One timeline: stock paid
        // 4000 + 10000 + 5000.
        [Fact]
        public void RepayInAnotherScope_OnOneTimeline_IsCreditedAndAddsNoThreshold()
        {
            var w = Register();
            var actions = Seeds();
            var first = Band("RecordsAltitude", 100.0, 10, "rec-a",
                new RecordThresholdCredit(4, 4000f, 0f, 0f), new RecordThresholdCredit(5, 10000f, 0f, 0f));
            var repay = Band("RecordsAltitude", 200.0, 20, "rec-b",
                new RecordThresholdCredit(5, 5000f, 0f, 0f, repay: true));
            actions.Add(first);
            actions.Add(repay);

            RecalculationEngine.Recalculate(actions);

            Assert.True(first.Effective && repay.Effective);
            Assert.False(repay.RecordPartlyCredited);
            Assert.Equal(19000.0, Funds(w), 3);
            Assert.Equal(2, w.Milestones.GetEffectiveMilestoneCount("RecordsAltitude"));
        }

        // catches: a re-payment credited although the crossing of its own timeline was zeroed
        // as a re-crossing, or zeroed although its crossing counts.
        [Fact]
        public void Repay_RidesTheCrossingOfItsOwnTimeline()
        {
            var w = Register();
            var actions = Seeds();
            // The committed timeline: 5 crossed at UT 700, re-paid at UT 710 in another scope.
            var committedCrossing = Band("RecordsAltitude", 700.0, 10, "rec-1",
                new RecordThresholdCredit(5, 5000f, 0f, 0f));
            var committedRepay = Band("RecordsAltitude", 710.0, 11, "rec-1b",
                new RecordThresholdCredit(5, 5000f, 0f, 0f, repay: true));
            // After a rewind: crossed again at UT 580, re-paid at UT 590.
            var reclimb = Band("RecordsAltitude", 580.0, 20, "rec-2",
                new RecordThresholdCredit(5, 5000f, 0f, 0f));
            var reclimbRepay = Band("RecordsAltitude", 590.0, 21, "rec-2b",
                new RecordThresholdCredit(5, 700f, 0f, 0f, repay: true));
            actions.AddRange(new[] { committedCrossing, committedRepay, reclimb, reclimbRepay });

            RecalculationEngine.Recalculate(actions);

            Assert.True(reclimb.Effective && reclimbRepay.Effective);
            Assert.False(committedCrossing.Effective);
            Assert.False(committedRepay.Effective);
            Assert.Equal(GameActionNotCountedReason.RecordThresholdsAlreadyCredited, committedRepay.NotCountedReason);
            Assert.Equal(5700.0, Funds(w), 3);
            Assert.Equal(1, w.Milestones.GetEffectiveMilestoneCount("RecordsAltitude"));
            Assert.Contains(logLines, l => l.Contains("Record row 'RecordsAltitude' zeroed at UT=710.0")
                && l.Contains("thresholds=[5r]") && l.Contains("zeroed=[RepayOfZeroedCrossing]"));
        }

        // catches: two rows captured with numbers but before the field (the step 1-3 builds)
        // ignoring capture order: the committed row captured first stood at the bottom of the
        // band, so the re-climb captured later re-crossed it in full.
        [Fact]
        public void LegacyRows_WithCaptureNumbers_UseCaptureOrderForTheBand()
        {
            var w = Register();
            var actions = Seeds();
            var committed = Legacy("RecordsSpeed", 700.0, 10, "rec-flight1", 3, 6000f);
            var reclimb = Legacy("RecordsSpeed", 580.0, 20, "rec-flight2", 3, 6000f);
            actions.Add(committed);
            actions.Add(reclimb);

            RecalculationEngine.Recalculate(actions);

            Assert.True(reclimb.Effective);
            Assert.False(committed.Effective);
            Assert.Equal(GameActionNotCountedReason.RecordThresholdsAlreadyCredited, committed.NotCountedReason);
            Assert.Equal(6000.0, Funds(w), 3);
            Assert.Equal(3, w.Milestones.GetEffectiveMilestoneCount("RecordsSpeed"));
        }

        // The accepted limit: two rows from an older save (no capture number) carry no
        // capture order, so they keep walk order and both pay, as before step 4.
        [Fact]
        public void LegacyRows_WithoutCaptureNumbers_KeepWalkOrder()
        {
            var w = Register();
            var actions = Seeds();
            var committed = Legacy("RecordsSpeed", 700.0, 0, "rec-flight1", 3, 6000f);
            var reclimb = Legacy("RecordsSpeed", 580.0, -1, "rec-flight2", 3, 6000f);
            actions.Add(committed);
            actions.Add(reclimb);

            RecalculationEngine.Recalculate(actions);

            Assert.True(reclimb.Effective && committed.Effective);
            Assert.Equal(12000.0, Funds(w), 3);
            Assert.Equal(6, w.Milestones.GetEffectiveMilestoneCount("RecordsSpeed"));
        }

        [Theory]
        [InlineData(5L, 7L, true)]
        [InlineData(7L, 5L, false)]
        [InlineData(0L, 5L, true)]
        [InlineData(-1L, 5L, true)]
        [InlineData(5L, 0L, false)]
        [InlineData(5L, -1L, false)]
        [InlineData(0L, 0L, true)]
        [InlineData(-1L, 0L, true)]
        public void CapturedBefore_NumberedRowsCompare_AnOlderBuildsRowCameFirst(long earlier, long later, bool expected)
        {
            Assert.Equal(expected, RecordThresholdBook.CapturedBefore(earlier, later));
        }

        // ================================================================
        // A career never rewound walks exactly as before step 4
        // ================================================================

        // catches: any change to the totals, the counted flags or the band count of a career
        // with no rewind, and any overlap on ONE timeline read as a duplicate. The generator
        // simulates stock on one timeline: each node's band only advances; a scope is one
        // (node, recording) pair whose row sits at its first award's UT and capture number;
        // scopes interleave; an older build's scope names no thresholds (no list, or a list
        // that went incomplete, "-"), a newer one names them; stock re-pays its last interval
        // every frame while the live node instance has paid it (a scene load stops that); the
        // oldest rows carry no capture number. Whatever the mix, every row counts in full, the
        // walk pays exactly what stock paid, and the node's count is never below stock's band.
        // derivedRepays=false keeps re-payments out of rows that name no thresholds (an older
        // build folded them into its count, which reads as owned crossings and clamps the
        // captured seed), so the seed capture check below stays exact and sensitive.
        [Theory]
        [InlineData(20261026, true)]
        [InlineData(1, true)]
        [InlineData(7, true)]
        [InlineData(42, true)]
        [InlineData(2024, true)]
        [InlineData(99991, true)]
        [InlineData(20261026, false)]
        [InlineData(1, false)]
        [InlineData(7, false)]
        [InlineData(42, false)]
        [InlineData(2024, false)]
        [InlineData(99991, false)]
        public void NeverRewoundCareer_WalksExactlyWhatStockPaid(int randomSeed, bool derivedRepays)
        {
            var rng = new Random(randomSeed);
            // More intervals than stock's 5 (a modded RecordSplit) so crossings span the build
            // phases below; the book does not depend on the number.
            const int split = 8;
            string[] ids = { "RecordsAltitude", "RecordsSpeed", "RecordsDistance", "RecordsDepth" };
            var seedCounts = new Dictionary<string, int>
                { { "RecordsAltitude", 1 }, { "RecordsSpeed", 2 }, { "RecordsDistance", 1 }, { "RecordsDepth", 1 } };
            var band = ids.ToDictionary(id => id, id => seedCounts.TryGetValue(id, out int s) ? s : 0);
            var instancePaid = ids.ToDictionary(id => id, id => new HashSet<int>());
            var openScopes = ids.ToDictionary(id => id, id => new List<GameAction>());
            // Recordings committed in flight that keep their id: their next award opens a new
            // scope (a new seed event, its own row) under the same recording id.
            var continuingIds = ids.ToDictionary(id => id, id => new List<string>());
            var rowStep = new Dictionary<GameAction, int>();
            var rowRepays = new Dictionary<GameAction, int>();
            // Rows started after the node's crossing number split - 3 stay pending (events
            // only) for the seed capture below, so pending continuations hold crossings.
            var pendingFromStep = ids.ToDictionary(id => id, id => int.MaxValue);
            var derivedHoldsRepay = new HashSet<string>();
            var career = new List<GameAction>();
            double stockFunds = 0.0;
            double ut = 50.0;
            long cseq = 0;
            int recordingCounter = 0;

            for (int step = 0; step < 200; step++)
            {
                ut += 1.0 + rng.Next(0, 20);
                int roll = rng.Next(16);
                string id = ids[rng.Next(ids.Length)];
                if (roll == 0)
                {
                    // A one-shot world-first in between.
                    career.Add(new GameAction
                    {
                        Type = GameActionType.MilestoneAchievement, UT = ut, CaptureSequence = ++cseq,
                        MilestoneId = "Body" + step + "/Landing", MilestoneFundsAwarded = 700f, MilestoneRepAwarded = 3f
                    });
                    continue;
                }
                if (roll == 1)
                {
                    // A scene load: new node instances (no re-payment until a new crossing) and
                    // every scope of the node closes.
                    instancePaid[id].Clear();
                    openScopes[id].Clear();
                    continue;
                }
                if (roll == 2)
                {
                    openScopes[id].Clear();   // a recording ends
                    continue;
                }
                if (roll == 3 || roll == 4)
                {
                    // An in-flight commit: the scope's row is final, the recording continues.
                    if (openScopes[id].Count > 0)
                    {
                        GameAction committedScope = openScopes[id][rng.Next(openScopes[id].Count)];
                        openScopes[id].Remove(committedScope);
                        continuingIds[id].Add(committedScope.RecordingId);
                    }
                    continue;
                }

                // An award: the next crossing, else a re-payment of the held last interval.
                int interval;
                bool repay;
                if (band[id] < split)
                {
                    interval = band[id] + 1;
                    repay = false;
                    band[id] = interval;
                    if (interval == split - 4)
                        pendingFromStep[id] = step;
                }
                else if (instancePaid[id].Contains(split))
                {
                    interval = split;
                    repay = true;
                }
                else
                {
                    continue;
                }
                instancePaid[id].Add(interval);
                float funds = repay ? 300f + rng.Next(0, 50) : 1000f * interval + rng.Next(0, 300);
                float repAward = 0.5f * interval;
                float sciAward = 0.25f * interval;
                stockFunds += funds;

                // Which scope pays: an open one (interleaved) or a new one.
                List<GameAction> open = openScopes[id];
                GameAction row = open.Count > 0 && rng.Next(3) > 0 ? open[rng.Next(open.Count)] : null;
                if (repay && !derivedRepays && row != null && row.MilestoneRecordBand == null)
                    row = null;
                if (row == null)
                {
                    // Phases: an old save (no capture number, no list), the step 1-3 builds
                    // (numbered, no list), then this build (named, or "-" from its first
                    // award when the interval was not read). Scopes stay open across phases,
                    // so an older build's scope keeps folding after the upgrade.
                    int phase = step < 15 ? 0 : step < 35 ? 1 : 2;
                    long rowCseq = phase == 0 ? 0 : ++cseq;
                    int build = phase < 2 ? 0 : (rng.Next(3) == 0 ? 0 : 1);
                    if (repay && !derivedRepays)
                    {
                        if (phase < 2)
                        {
                            stockFunds -= funds;   // not paid: stock held no live crossing here
                            instancePaid[id].Clear();
                            continue;
                        }
                        build = 1;
                    }
                    string recordingId;
                    if (continuingIds[id].Count > 0 && rng.Next(4) > 0)
                    {
                        recordingId = continuingIds[id][0];
                        continuingIds[id].RemoveAt(0);
                    }
                    else
                    {
                        recordingId = "rec-" + (++recordingCounter);
                    }
                    row = new GameAction
                    {
                        Type = GameActionType.MilestoneAchievement, UT = ut, CaptureSequence = rowCseq,
                        RecordingId = recordingId, MilestoneId = id,
                        MilestoneFundsAwarded = funds, MilestoneRepAwarded = repAward,
                        MilestoneScienceAwarded = sciAward, MilestoneRecordThresholds = 1,
                        MilestoneRecordBand = build == 0 ? null : new List<RecordThresholdCredit>
                            { new RecordThresholdCredit(interval, funds, repAward, sciAward, repay) }
                    };
                    open.Add(row);
                    career.Add(row);
                    rowStep[row] = step;
                    rowRepays[row] = 0;
                }
                else
                {
                    row.MilestoneFundsAwarded += funds;
                    row.MilestoneRepAwarded += repAward;
                    row.MilestoneScienceAwarded += sciAward;
                    row.MilestoneRecordThresholds += 1;
                    // An award whose interval was not read leaves the list incomplete for good.
                    bool unread = rng.Next(8) == 0 && (derivedRepays || !(rowRepays[row] > 0 || repay));
                    row.MilestoneRecordBand = row.MilestoneRecordBand != null && !unread
                        ? RecordThresholdBand.Fold(row.MilestoneRecordBand, interval, funds, repAward, sciAward, repay)
                        : null;
                }
                if (repay)
                    rowRepays[row]++;
                if (repay && row.MilestoneRecordBand == null)
                    derivedHoldsRepay.Add(id);
            }

            // The reference: each record row its own once-ever id, counted in full - every
            // award stock paid on this one timeline.
            var reference = career.Select(a => new GameAction
            {
                Type = a.Type, UT = a.UT, CaptureSequence = a.CaptureSequence, RecordingId = a.RecordingId,
                MilestoneId = MilestonesModule.IsRepeatableWorldRecordMilestone(a.MilestoneId)
                    ? "ref-" + a.ActionId : a.MilestoneId,
                MilestoneFundsAwarded = a.MilestoneFundsAwarded,
                MilestoneRepAwarded = a.MilestoneRepAwarded,
                MilestoneScienceAwarded = a.MilestoneScienceAwarded
            }).ToList();
            double oneShotFunds = career.Where(a => !MilestonesModule.IsRepeatableWorldRecordMilestone(a.MilestoneId))
                .Sum(a => (double)a.MilestoneFundsAwarded);

            var seed = PreLedgerProgressSeed.CreateCaptured(null, seedCounts);
            var w = Register(seed);
            var actions = Seeds();
            actions.AddRange(career);
            RecalculationEngine.Recalculate(actions);
            double walkFunds = Funds(w);
            double science = w.Science.GetRunningScience();
            float rep = w.Rep.GetRunningRep();

            RecalculationEngine.ClearModules();
            var r = Register();
            var refActions = Seeds();
            refActions.AddRange(reference);
            RecalculationEngine.Recalculate(refActions);

            Assert.All(career, a => Assert.True(a.Effective));
            Assert.All(career, a => Assert.False(a.RecordPartlyCredited));
            Assert.Equal(stockFunds + oneShotFunds, walkFunds, 1);
            Assert.Equal(r.Funds.GetRunningBalance() - 100000.0, walkFunds, 3);
            Assert.Equal(r.Science.GetRunningScience(), science, 3);
            Assert.Equal((double)r.Rep.GetRunningRep(), (double)rep, 3);
            foreach (string id in ids)
            {
                int count = w.Milestones.GetEffectiveMilestoneCount(id);
                Assert.True(count >= band[id], id + " count " + count + " below stock's band " + band[id]);
                // An older build folded re-payments into its count, the one way above the band.
                if (!derivedHoldsRepay.Contains(id))
                    Assert.Equal(band[id], count);
            }
            Assert.DoesNotContain(logLines, l => l.Contains("[Milestones]")
                && (l.Contains("partly credited") || l.Contains("zeroed at")));

            // The seed captured from this career: the live band, the rows committed so far
            // (each with its own event still in the store) and the newest scopes as pending
            // events only, some of them continuations in a committed scope. It must give back
            // the pre-ledger count, and a walk from it must still pay what stock paid.
            var committedRows = new List<GameAction>();
            var storeEvents = new List<GameStateEvent>();
            foreach (GameAction a in career)
            {
                if (!MilestonesModule.IsRepeatableWorldRecordMilestone(a.MilestoneId))
                    continue;
                var evt = new GameStateEvent
                {
                    ut = a.UT, captureSequence = a.CaptureSequence, key = a.MilestoneId,
                    eventType = GameStateEventType.MilestoneAchieved, recordingId = a.RecordingId,
                    detail = GameStateRecorder.BuildMilestoneDetail(a.MilestoneFundsAwarded, a.MilestoneRepAwarded,
                        a.MilestoneScienceAwarded, a.MilestoneRecordThresholds,
                        a.MilestoneRecordBand != null ? RecordThresholdBand.Format(a.MilestoneRecordBand)
                            : (a.CaptureSequence > 0 ? RecordThresholdBand.IncompleteToken : null))
                };
                storeEvents.Add(evt);
                if (rowStep[a] <= pendingFromStep[a.MilestoneId])
                    committedRows.Add(a);
            }
            var live = ids.Select(id => new PreLedgerProgressSeed.NodeObservation
            {
                QualifiedId = id, IsRepeatableRecord = true, ImpliedPaidCountKnown = true, ImpliedPaidCount = band[id]
            }).ToArray();
            var captured = PreLedgerProgressSeed.Capture(live, committedRows, storeEvents, out _);
            foreach (string id in ids)
            {
                // An older build's row counts every award, so each re-payment folded into a
                // row that names no thresholds reads as one more owned crossing.
                int foldedRepays = career.Where(a => a.MilestoneId == id && a.MilestoneRecordBand == null
                    && rowRepays.ContainsKey(a)).Sum(a => rowRepays[a]);
                int seeded = seedCounts.TryGetValue(id, out int s) ? s : 0;
                Assert.Equal(Math.Max(0, seeded - foldedRepays), captured.GetRecordPaidCount(id));
            }

            RecalculationEngine.ClearModules();
            var fromCaptured = Register(captured);
            var again = Seeds();
            again.AddRange(career);
            RecalculationEngine.Recalculate(again);
            Assert.All(career, a => Assert.True(a.Effective));
            Assert.Equal(stockFunds + oneShotFunds, Funds(fromCaptured), 1);
            foreach (string id in ids)
                Assert.True(fromCaptured.Milestones.GetEffectiveMilestoneCount(id) >= band[id], id);
        }

        // ================================================================
        // The pre-ledger seed owns only what the ledger does not
        // ================================================================

        private static PreLedgerProgressSeed.NodeObservation LiveRecord(string id, int paid)
        {
            return new PreLedgerProgressSeed.NodeObservation
            {
                QualifiedId = id, IsRepeatableRecord = true, ImpliedPaidCountKnown = true, ImpliedPaidCount = paid
            };
        }

        private static GameStateEvent RecordEventOf(GameAction row, double ut, long cseq)
        {
            return new GameStateEvent
            {
                ut = ut,
                captureSequence = cseq,
                eventType = GameStateEventType.MilestoneAchieved,
                key = row.MilestoneId,
                recordingId = row.RecordingId ?? "",
                detail = GameStateRecorder.BuildMilestoneDetail(row.MilestoneFundsAwarded, row.MilestoneRepAwarded,
                    row.MilestoneScienceAwarded, row.MilestoneRecordThresholds,
                    RecordThresholdBand.Format(row.MilestoneRecordBand))
            };
        }

        // catches: a pending event in a scope that already has a committed row taken as that
        // row's own event (the in-flight-commit continuation: a recording that keeps its id
        // past a commit starts a new seed event). The seed then claimed interval 1 and the
        // walk zeroed the committed row's real crossing of it: 5000 paid against stock's 6000.
        [Fact]
        public void SeedCapture_AContinuationEventInACommittedScope_IsPending()
        {
            var committed = Named("RecordsAltitude", 100.0, 10, "rec1", 1, 2);
            var continuation = Named("RecordsAltitude", 300.0, 30, "rec1", 3);
            var events = new[]
            {
                RecordEventOf(committed, 100.0, 10),      // the committed row's own event
                RecordEventOf(continuation, 300.0, 30)    // pending: commits as its own row
            };
            Assert.Contains(";band=3:3000:1.5:0.75", events[1].detail);

            PreLedgerProgressSeed.CaptureStats stats;
            var seed = PreLedgerProgressSeed.Capture(new[] { LiveRecord("RecordsAltitude", 3) },
                new[] { committed }, events, out stats);
            Assert.Equal(0, seed.GetRecordPaidCount("RecordsAltitude"));
            Assert.Equal(1, stats.RecordsOwnedByLedger);

            // The walk over both rows pays what stock paid and rebuilds stock's band.
            var w = Register(seed);
            var actions = Seeds();
            actions.Add(committed);
            actions.Add(GameStateEventConverter.ConvertMilestoneAchieved(events[1], "rec1"));
            RecalculationEngine.Recalculate(actions);
            Assert.True(committed.Effective);
            Assert.False(committed.RecordPartlyCredited);
            Assert.Equal(6000.0, Funds(w), 3);
            Assert.Equal(3, w.Milestones.GetEffectiveMilestoneCount("RecordsAltitude"));
        }

        // catches: the own-row match ignoring the capture number (a row and its own event at
        // different UTs counted twice) or matching rows of another scope.
        [Fact]
        public void SeedCapture_ARowsOwnEventMatchesByCaptureNumberWithinItsScope()
        {
            var row = Named("RecordsAltitude", 100.0, 10, "rec1", 1, 2);
            var own = RecordEventOf(row, 100.4, 10);
            var otherScope = RecordEventOf(Named("RecordsAltitude", 100.0, 10, "rec2", 3), 100.0, 11);
            Assert.True(PreLedgerProgressSeed.IsOwnRowOf(new[] { row }, own));
            Assert.False(PreLedgerProgressSeed.IsOwnRowOf(new[] { row }, RecordEventOf(row, 100.0, 11)));
            // No capture number on one side: the coalescing's UT match.
            var unnumbered = RecordEventOf(row, 100.05, 0);
            Assert.True(PreLedgerProgressSeed.IsOwnRowOf(new[] { row }, unnumbered));

            var seed = PreLedgerProgressSeed.Capture(new[] { LiveRecord("RecordsAltitude", 5) },
                new[] { row }, new[] { own, otherScope }, out _);
            // 5 paid live; the ledger owns 1, 2 (row) and 3 (other scope's pending event).
            Assert.Equal(2, seed.GetRecordPaidCount("RecordsAltitude"));
        }

        // catches: the seed subtracting re-payments as crossings, so seed + the walk's count
        // reads below the live band and the band patch lowers it (a modded record maximum).
        [Fact]
        public void SeedCapture_ARePaymentOwnsNoCrossing()
        {
            var crossing = Band("RecordsAltitude", 100.0, 10, "rec-a",
                new RecordThresholdCredit(4, 4000f, 0f, 0f), new RecordThresholdCredit(5, 10000f, 0f, 0f));
            var repay = Band("RecordsAltitude", 200.0, 20, "rec-b",
                new RecordThresholdCredit(5, 5000f, 0f, 0f, repay: true));

            var seed = PreLedgerProgressSeed.Capture(new[] { LiveRecord("RecordsAltitude", 5) },
                new[] { crossing, repay }, new GameStateEvent[0], out _);
            Assert.Equal(3, seed.GetRecordPaidCount("RecordsAltitude"));

            var w = Register(seed);
            var actions = Seeds();
            actions.Add(crossing);
            actions.Add(repay);
            RecalculationEngine.Recalculate(actions);
            Assert.Equal(5, w.Milestones.GetEffectiveMilestoneCount("RecordsAltitude"));
            Assert.Equal(19000.0, Funds(w), 3);
        }

        // ================================================================
        // The band patch rebuilds from the distinct credited count
        // ================================================================

        // catches: the band rebuilt from the SUM of the rows' thresholds (a re-crossed band
        // counted twice opens thresholds nobody was paid for, or completes the node early).
        [Fact]
        public void BandPatch_RebuildsFromTheDistinctCreditedThresholds()
        {
            var w = Register();
            var actions = Seeds();
            actions.Add(Named("RecordsAltitude", 300.0, 10, "rec-flight1", 1, 2));
            actions.Add(Named("RecordsAltitude", 700.0, 11, "rec-flight1b", 3));
            actions.Add(Named("RecordsAltitude", 580.0, 20, "rec-flight2", 3));
            RecalculationEngine.Recalculate(actions);

            int effective = w.Milestones.GetEffectiveMilestoneCount("RecordsAltitude");
            Assert.Equal(3, effective);

            var node = new KSPAchievements.RecordsAltitude();
            SetLiveBand(node, 4);
            Assert.True(KspStatePatcher.PatchRepeatableRecordNode(node, effective, "RecordsAltitude",
                authoritativeRepeatableRecordState: true));
            Assert.Equal(3, LivePaid(node));
            Assert.True(KspStatePatcher.TryComputeRepeatableRecordState(node, 3, out var three));
            Assert.Equal(three.Record, GetField<double>(node, "record"), 6);
        }

        // ================================================================
        // Capture: the row names the threshold stock pays
        // ================================================================

        // catches: the recorder losing the interval stock pays, folding two awards of one
        // interval as two thresholds, or the list surviving an award whose interval was not
        // read.
        [Fact]
        public void Capture_NamesEachThresholdFromStocksInterval_OnTheEventAndTheLedgerRow()
        {
            LedgerOrchestrator.Initialize();
            var node = new KSPAchievements.RecordsAltitude();
            SetField(node, "rewardInterval", 2);
            GameStateRecorder.EmitStandaloneProgressReward(node, 2400.0, 0.5f, 0.0, ut: 44.5);
            SetField(node, "rewardInterval", 3);
            GameStateRecorder.EmitStandaloneProgressReward(node, 4800.0, 1f, 0.25, ut: 60.0);
            // Stock re-pays its last interval every frame in the rounding gap: one threshold.
            GameStateRecorder.EmitStandaloneProgressReward(node, 4800.0, 1f, 0.0, ut: 61.0);

            var evt = GameStateStore.Events.Single(e =>
                e.eventType == GameStateEventType.MilestoneAchieved && e.key == "RecordsAltitude");
            Assert.Contains("thresholds=3", evt.detail);
            Assert.Contains(";band=2:2400:0.5:0,3:9600:2:0.25", evt.detail);

            var row = Ledger.Actions.Single(a =>
                a.Type == GameActionType.MilestoneAchievement && a.MilestoneId == "RecordsAltitude");
            Assert.Equal(3, row.MilestoneRecordThresholds);
            Assert.NotNull(row.MilestoneRecordBand);
            Assert.Equal(new[] { 2, 3 }, row.MilestoneRecordBand.Select(c => c.Index).ToArray());
            Assert.Equal(9600f, row.MilestoneRecordBand[1].Funds);

            var converted = GameStateEventConverter.ConvertMilestoneAchieved(evt, "rec");
            Assert.Equal(new[] { 2, 3 }, converted.MilestoneRecordBand.Select(c => c.Index).ToArray());

            Assert.Contains(logLines, l => l.Contains("[GameStateRecorder]")
                && l.Contains("MilestoneAchieved (standalone) 'RecordsAltitude'") && l.Contains("threshold=2"));

            // An award whose interval cannot be read marks the scope's list incomplete for good.
            SetField(node, "rewardInterval", int.MaxValue);
            GameStateRecorder.EmitStandaloneProgressReward(node, 4800.0, 1f, 0.0, ut: 70.0);
            SetField(node, "rewardInterval", 4);
            GameStateRecorder.EmitStandaloneProgressReward(node, 4800.0, 1f, 0.0, ut: 80.0);
            evt = GameStateStore.Events.Single(e =>
                e.eventType == GameStateEventType.MilestoneAchieved && e.key == "RecordsAltitude");
            Assert.Contains(";band=-", evt.detail);
            Assert.Null(GameStateEventConverter.ParseMilestoneRecordBand(evt.detail));
            Assert.Null(row.MilestoneRecordBand);
            Assert.Equal(5, row.MilestoneRecordThresholds);
        }

        // catches: a second scope's award of an interval the live node already paid captured as
        // a crossing (the walk would then zero stock's real re-payment), or a fresh node
        // instance's first award of it captured as a re-payment.
        [Fact]
        public void Capture_MarksAnIntervalTheLiveNodeAlreadyPaidAsARepay()
        {
            try
            {
                var node = new KSPAchievements.RecordsAltitude();
                SetField(node, "rewardInterval", 5);
                GameStateRecorder.TagResolverForTesting = () => "rec-a";
                GameStateRecorder.EmitStandaloneProgressReward(node, 10000.0, 0f, 0.0, ut: 100.0);
                GameStateRecorder.EmitStandaloneProgressReward(node, 10000.0, 0f, 0.0, ut: 101.0);
                GameStateRecorder.TagResolverForTesting = () => "rec-b";
                GameStateRecorder.EmitStandaloneProgressReward(node, 5000.0, 0f, 0.0, ut: 200.0);
                // A scene load replaces the node: its first award of 5 is a crossing again.
                var reloaded = new KSPAchievements.RecordsAltitude();
                SetField(reloaded, "rewardInterval", 5);
                GameStateRecorder.TagResolverForTesting = () => "rec-c";
                GameStateRecorder.EmitStandaloneProgressReward(reloaded, 10000.0, 0f, 0.0, ut: 300.0);

                var events = GameStateStore.Events.Where(e =>
                    e.eventType == GameStateEventType.MilestoneAchieved && e.key == "RecordsAltitude").ToList();
                Assert.Contains(";band=5:20000:0:0", events.Single(e => e.recordingId == "rec-a").detail);
                Assert.Contains(";band=5r:5000:0:0", events.Single(e => e.recordingId == "rec-b").detail);
                Assert.Contains(";band=5:10000:0:0", events.Single(e => e.recordingId == "rec-c").detail);
                Assert.Contains(logLines, l => l.Contains("MilestoneAchieved (standalone) 'RecordsAltitude'")
                    && l.Contains("threshold=5 repay"));
            }
            finally
            {
                GameStateRecorder.TagResolverForTesting = null;
            }
        }

        [Fact]
        public void Capture_AStandInNodeWithoutTheIntervalMarksTheSeedIncomplete()
        {
            var node = new ProgressNode("RecordsSpeed", false);
            GameStateRecorder.EmitStandaloneProgressReward(node, 4800.0, 1f, 0.0, ut: 30.0);

            var evt = GameStateStore.Events.Single(e => e.key == "RecordsSpeed");
            Assert.Contains(";band=-", evt.detail);
            Assert.Null(GameStateEventConverter.ConvertMilestoneAchieved(evt, "rec").MilestoneRecordBand);
            Assert.Equal(0, GameStateRecorder.ReadRecordRewardInterval(node));
        }

        // ================================================================
        // Serialization
        // ================================================================

        [Fact]
        public void Serialization_RoundTripsTheBand_AndWritesNothingWithoutOne()
        {
            var de = new CultureInfo("de-DE");
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = de;
                var row = Named("RecordsAltitude", 44.5, 3, "rec", 2, 3);
                row.MilestoneRecordBand[0] = new RecordThresholdCredit(2, 2400.5f, 0.5f, 0f);
                var parent = new ConfigNode("ROOT");
                row.SerializeInto(parent);
                ConfigNode node = parent.GetNode("GAME_ACTION");
                Assert.Equal("2:2400.5:0.5:0,3:3000:1.5:0.75", node.GetValue(GameAction.MilestoneRecordBandKey));

                GameAction back = GameAction.DeserializeFrom(node);
                Assert.Equal(new[] { 2, 3 }, back.MilestoneRecordBand.Select(c => c.Index).ToArray());
                Assert.Equal(2400.5f, back.MilestoneRecordBand[0].Funds);
                Assert.Equal(0.75f, back.MilestoneRecordBand[1].Science);
                Assert.False(back.MilestoneRecordBand[1].Repay);

                // A re-payment keeps its kind through the text form.
                var repayRow = Band("RecordsAltitude", 44.5, 3, "rec",
                    new RecordThresholdCredit(5, 5000.5f, 0f, 0f, repay: true));
                var repayParent = new ConfigNode("ROOT");
                repayRow.SerializeInto(repayParent);
                ConfigNode repayNode = repayParent.GetNode("GAME_ACTION");
                Assert.Equal("5r:5000.5:0:0", repayNode.GetValue(GameAction.MilestoneRecordBandKey));
                Assert.True(GameAction.DeserializeFrom(repayNode).MilestoneRecordBand[0].Repay);

                var legacy = Legacy("RecordsAltitude", 44.5, 3, "rec", 2, 100f);
                var legacyParent = new ConfigNode("ROOT");
                legacy.SerializeInto(legacyParent);
                ConfigNode legacyNode = legacyParent.GetNode("GAME_ACTION");
                Assert.Null(legacyNode.GetValue(GameAction.MilestoneRecordBandKey));
                Assert.Null(GameAction.DeserializeFrom(legacyNode).MilestoneRecordBand);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void Serialization_AMalformedBandReadsAsDerivedAndSaysSo()
        {
            var row = Legacy("RecordsAltitude", 44.5, 3, "rec", 2, 100f);
            var parent = new ConfigNode("ROOT");
            row.SerializeInto(parent);
            ConfigNode node = parent.GetNode("GAME_ACTION");
            node.AddValue(GameAction.MilestoneRecordBandKey, "0:1:2:3");

            Assert.Null(GameAction.DeserializeFrom(node).MilestoneRecordBand);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains(GameAction.MilestoneRecordBandKey)
                && l.Contains("0:1:2:3"));
        }

        // ================================================================
        // The live mirror
        // ================================================================

        private static GameAction CompletionRow(string id, double ut)
        {
            return Legacy(id, ut, 0, "rec-flight1", 0, 0f);
        }

        // catches: the committed count summing re-crossed thresholds, reading rows past the
        // clock, forgetting the seed, or counting a Re-Fly origin's rows.
        [Fact]
        public void CommittedRecordCounts_AreTheSeedPlusDistinctThresholdsAtTheClock()
        {
            var actions = new List<GameAction>
            {
                Named("RecordsAltitude", 300.0, 10, "rec-flight1", 1, 2),
                Named("RecordsAltitude", 580.0, 20, "rec-flight2", 2, 3),
                Named("RecordsAltitude", 700.0, 11, "rec-flight1b", 4),
                Named("RecordsSpeed", 400.0, 12, "rec-origin", 1),
                CompletionRow("RecordsDistance", 100.0),
            };
            var seed = new Dictionary<string, int> { { "RecordsAltitude", 1 } };

            Dictionary<string, int> at600 = CommittedMilestoneReach.CollectCommittedRecordCountsAtOrBefore(
                actions, 600.0, new HashSet<string> { "rec-origin" }, seed, out int excluded);
            Assert.Equal(3, at600["RecordsAltitude"]);
            Assert.False(at600.ContainsKey("RecordsSpeed"));
            Assert.False(at600.ContainsKey("RecordsDistance"));
            Assert.Equal(1, excluded);

            Dictionary<string, int> at700 = CommittedMilestoneReach.CollectCommittedRecordCountsAtOrBefore(
                actions, 700.0, null, null, out excluded);
            Assert.Equal(4, at700["RecordsAltitude"]);
            Assert.Equal(1, at700["RecordsSpeed"]);
            Assert.Equal(0, excluded);

            Assert.Empty(CommittedMilestoneReach.CollectCommittedRecordCountsAtOrBefore(
                actions, 299.0, null, seed, out _));
        }

        private sealed class RecordTree
        {
            internal readonly ProgressTree Root = new ProgressTree();
            internal readonly KSPAchievements.RecordsAltitude Altitude = new KSPAchievements.RecordsAltitude();
            internal readonly KSPAchievements.RecordsSpeed Speed = new KSPAchievements.RecordsSpeed();

            internal RecordTree()
            {
                Root.AddNode(new ProgressNode("FirstLaunch", false));
                Root.AddNode(Altitude);
                Root.AddNode(Speed);
            }
        }

        // catches: the mirror not raising a band the rewind lowered (stock then pays the
        // re-climb), paying or completing through stock, or raising before the clock reaches
        // the committed row.
        [Fact]
        public void Mirror_RaisesTheBandToTheCommittedCountAtTheClock()
        {
            var t = new RecordTree();
            SetLiveBand(t.Altitude, 1);
            CommittedMilestoneReach.ProgressTreeProviderForTesting = () => t.Root;
            var actions = new List<GameAction>
            {
                Named("RecordsAltitude", 300.0, 10, "rec-flight1", 1, 2),
                Named("RecordsAltitude", 700.0, 11, "rec-flight1b", 3, 4),
            };

            // Before the 700 row: the committed band at the clock is 2.
            Assert.Equal(1, CommittedMilestoneReach.Apply(actions, 650.0, "test"));
            Assert.Equal(2, LivePaid(t.Altitude));
            Assert.Contains(logLines, l => l.Contains("[CommittedMilestones]")
                && l.Contains("Apply: marked 0 stock progress node(s) complete without reward and raised 1 record band(s)")
                && l.Contains("records=[RecordsAltitude:1->2]"));

            Assert.Equal(1, CommittedMilestoneReach.Apply(actions, 700.0, "test"));
            Assert.Equal(4, LivePaid(t.Altitude));
            Assert.False(t.Altitude.IsComplete);
            Assert.True(KspStatePatcher.TryComputeRepeatableRecordState(t.Altitude, 4, out var four));
            Assert.Equal(four.RewardThreshold, GetField<double>(t.Altitude, "rewardThreshold"), 6);

            // Nothing left to raise.
            Assert.Equal(0, CommittedMilestoneReach.Apply(actions, 900.0, "test"));
        }

        // catches: the mirror lowering a band the live flight earned beyond the committed one.
        [Fact]
        public void Mirror_IsAddOnly_ABandAboveTheCommittedCountIsLeftAlone()
        {
            var t = new RecordTree();
            SetLiveBand(t.Altitude, 4);
            double recordBefore = GetField<double>(t.Altitude, "record");
            CommittedMilestoneReach.ProgressTreeProviderForTesting = () => t.Root;
            var actions = new List<GameAction> { Named("RecordsAltitude", 300.0, 10, "rec-flight1", 1, 2) };

            Assert.Equal(0, CommittedMilestoneReach.Apply(actions, 650.0, "test"));

            Assert.Equal(4, LivePaid(t.Altitude));
            Assert.Equal(recordBefore, GetField<double>(t.Altitude, "record"), 6);
            Assert.Contains(logLines, l => l.Contains("[CommittedMilestones]")
                && l.Contains("recordBandsRaised=0") && l.Contains("recordBandsAtOrAbove=1"));
        }

        // catches: the mirror raising the band from a Re-Fly origin's rows, which a merge
        // tombstones - the attempt must stay able to earn those thresholds itself.
        [Fact]
        public void Mirror_DuringAReFly_LeavesTheOriginsThresholdsEarnable()
        {
            var t = new RecordTree();
            CommittedMilestoneReach.ProgressTreeProviderForTesting = () => t.Root;
            CommittedMilestoneReach.SuppressedRecordingIdsProviderForTesting =
                () => new HashSet<string> { "rec-origin" };
            var actions = new List<GameAction>
            {
                Named("RecordsAltitude", 300.0, 10, "rec-other", 1),
                Named("RecordsAltitude", 400.0, 11, "rec-origin", 2, 3),
                Named("RecordsSpeed", 400.0, 12, "rec-origin", 1, 2),
            };

            Assert.Equal(1, CommittedMilestoneReach.Apply(actions, 900.0, "test"));

            Assert.Equal(1, LivePaid(t.Altitude));
            Assert.Equal(0, LivePaid(t.Speed));
            Assert.Contains(logLines, l => l.Contains("[CommittedMilestones]")
                && l.Contains("reFlySuppressedRows=2"));
        }

        // catches: the seed missing from the committed count (the mirror would then never
        // raise a pre-ledger band) or the seed alone raising a band with no committed row.
        [Fact]
        public void Mirror_CountsThePreLedgerSeed()
        {
            var t = new RecordTree();
            CommittedMilestoneReach.ProgressTreeProviderForTesting = () => t.Root;
            CommittedMilestoneReach.SeedRecordCountsProviderForTesting =
                () => new Dictionary<string, int> { { "RecordsAltitude", 2 }, { "RecordsSpeed", 3 } };
            var actions = new List<GameAction> { Named("RecordsAltitude", 300.0, 10, "rec-flight1", 3) };

            Assert.Equal(1, CommittedMilestoneReach.Apply(actions, 650.0, "test"));

            Assert.Equal(3, LivePaid(t.Altitude));
            Assert.Equal(0, LivePaid(t.Speed));
        }

        // catches: the deferred recalculation (a warp under a live recorder) not raising the
        // band the warp crossed, which is the path a re-climb under a recorder takes.
        [Fact]
        public void TimeJumpRecalc_DeferredByALiveRecorder_RaisesTheCrossedRecordBand()
        {
            var t = new RecordTree();
            CommittedMilestoneReach.ProgressTreeProviderForTesting = () => t.Root;
            GameStateRecorder.HasLiveRecorderProviderForTesting = () => true;
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => true;
            LedgerOrchestrator.Initialize();
            Ledger.AddAction(Named("RecordsAltitude", 100.0, 10, "rec-flight1", 1, 2, 3));
            Ledger.AddAction(Named("RecordsAltitude", 1500.0, 11, "rec-flight1", 4));

            LedgerOrchestrator.RecalculateAndPatchForTimeJump(900.0);

            Assert.Equal(3, LivePaid(t.Altitude));
            Assert.Contains(logLines, l => l.Contains("[CommittedMilestones]")
                && l.Contains("reason=deferred-patch") && l.Contains("records=[RecordsAltitude:0->3]"));
        }

        // ================================================================
        // The flight clock cursor fires on record rows
        // ================================================================

        // catches: the cursor waiting only for once-ever rows, so a 1x re-climb past a committed
        // record row is paid by stock.
        [Fact]
        public void ClockCursor_FiresWhenTheClockPassesACommittedRecordRow()
        {
            var t = new RecordTree();
            CommittedMilestoneReach.ProgressTreeProviderForTesting = () => t.Root;
            LedgerOrchestrator.Initialize();
            Ledger.AddAction(CompletionRow("RecordsSpeed", 90.0));
            Ledger.AddAction(Named("RecordsAltitude", 100.0, 10, "rec-flight1", 1, 2));
            var cursor = new CommittedMilestoneClockCursor("flight");

            Assert.False(cursor.Advance(45.0));
            Assert.False(cursor.Advance(95.0));   // a completion row carries no threshold
            Assert.Equal(0, LivePaid(t.Altitude));
            Assert.True(cursor.Advance(100.5));

            Assert.Equal(2, LivePaid(t.Altitude));
            Assert.Contains(logLines, l => l.Contains("[CommittedMilestones]")
                && l.Contains("reason=flight-clock") && l.Contains("records=[RecordsAltitude:0->2]"));
        }

        // ================================================================
        // Timeline hover
        // ================================================================

        [Fact]
        public void TimelineHover_NamesTheFullyRecrossedRecord()
        {
            Assert.Equal("Not counted: this record was already set that far earlier on timeline, so no reward was paid.",
                TimelineRowHover.NotCounted(GameActionNotCountedReason.RecordThresholdsAlreadyCredited));
        }
    }
}
