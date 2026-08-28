using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// STRATEGY-PREFIX-HOLDBACK-PERMANENT: on a save whose strategy currency exchange
    /// predates the capture fix (PR #1483), the stored
    /// <c>ScienceChanged(StrategyInput)</c> event exists but the matching
    /// <see cref="GameActionType.StrategyScienceDebit"/> row never will, so
    /// <c>ComputePendingUncommittedStrategyScienceDebit</c>'s deliberately UNBOUNDED
    /// observed-minus-ingested window never drains. The pending adjustment holds the
    /// patch target back by the exchange's full take on every recalc and the drawdown
    /// guard clamps (WARN + player toast) each time, forever.
    ///
    /// <para>The numbers here are the LIVE-MEASURED ones: the test career's original
    /// <c>researchIPsellout</c> exchange moved 108.84171852 science at ut=8599.8755
    /// (collected snapshot <c>logs/2026-08-19_0002_strategy-multi-live</c>; same
    /// 108.84171851920314 the C2Career fixture pins as its science divergence). The
    /// pre-fix LEDGER shape is not synthesized - the fixture ledger at
    /// <c>Fixtures/C2Career/Parsek/GameState/ledger.pgld</c> is real frozen pre-fix data
    /// carrying the exchange's FUNDS credit and no science row of any kind, which is
    /// exactly the population the sweep has to classify.</para>
    ///
    /// <para>The fix is a one-time LOAD SWEEP
    /// (<c>ComputeUnmatchableStrategyScienceDebitBaseline</c>) that measures the residual
    /// no future row can ever ingest and bounds the observed side by it. These cells pin
    /// the release, the fresh-event holdback that must survive it, and the idempotence /
    /// symmetry properties the shipped STRATEGY-* scoping rules depend on.</para>
    /// </summary>
    [Collection("Sequential")]
    public class StrategyPrefixHoldbackTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // The measured researchIPsellout exchange (see class summary).
        private const double ExchangeUT = 8599.8755;
        private const double ScienceBefore = 750.0;
        private const double ScienceAfter = 641.15828148;
        private const double ExchangeTake = ScienceBefore - ScienceAfter;   // 108.84171852

        private readonly List<string> logLines = new List<string>();

        public StrategyPrefixHoldbackTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ================================================================
        // Fixture / builders
        // ================================================================

        private static GameStateEvent StrategyInputScienceDebit(
            double ut, double before, double after, string recordingId = null)
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.ScienceChanged,
                key = GameStateEventConverter.StrategyInputReasonKey,
                recordingId = recordingId,
                valueBefore = before,
                valueAfter = after
            };
        }

        private static GameAction ExchangerDebitRow(double ut, double cost, string recordingId = null)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.StrategyScienceDebit,
                RecordingId = recordingId,
                Cost = (float)cost,
                ConversionSource = StrategyConversionSource.Exchanger
            };
        }

        /// <summary>
        /// The REAL frozen pre-fix ledger: 68 rows including the exchange's
        /// <c>FundsEarning</c>/<c>FundsEarningSource.Strategy</c> credit and no science
        /// row of any kind at the exchange UT. Loading it here (rather than forging three
        /// rows) is what makes the sweep's classification a statement about production
        /// data instead of about the builder.
        /// </summary>
        private static List<GameAction> LoadPreFixFixtureLedger()
        {
            string root = SyntheticRecordingTests.ResolveProjectRoot();
            string path = Path.Combine(
                root, "Source", "Parsek.Tests", "Fixtures", "C2Career",
                "Parsek", "GameState", "ledger.pgld");
            Assert.True(File.Exists(path), $"pre-fix fixture ledger not found at '{path}'");
            Assert.True(Ledger.LoadFromFile(path), "Ledger.LoadFromFile failed on the C2Career fixture");
            var actions = new List<GameAction>(Ledger.Actions);
            Assert.DoesNotContain(actions, a => a.Type == GameActionType.StrategyScienceDebit);
            return actions;
        }

        // ================================================================
        // The defect, reproduced on the pre-fix shape
        // ================================================================

        [Fact]
        public void PreFixShapedStore_WithNoBound_HoldsBackTheFullExchangeOnEveryRecalc()
        {
            // The real pre-fix ledger + the exchange event KSP stored beside it. No
            // StrategyScienceDebit row exists and none can ever be written for it, so the
            // observed-minus-ingested gap is the whole take and it never drains: two
            // successive evaluations (two recalcs) report the identical hold-back.
            var ledger = LoadPreFixFixtureLedger();
            var events = new List<GameStateEvent>
            {
                StrategyInputScienceDebit(ExchangeUT, ScienceBefore, ScienceAfter)
            };

            double first = LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit(
                events, ledger);
            double second = LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit(
                events, ledger);

            Assert.Equal(ExchangeTake, first, 6);
            Assert.Equal(first, second, 6);
        }

        [Fact]
        public void PreFixHoldback_MakesTheDrawdownGuardClampOnEveryRecalc_AndTheBoundStopsIt()
        {
            // The SYMPTOM, stated through the pure patch decision. Once the pool and the
            // reconstruction agree (the steady state of any settled save), a phantom
            // pending debit drags the guard's discriminator - the pending-adjusted running
            // balance - that far BELOW live, which is precisely IsGuardableDrawdown: WARN
            // "GUARDED DRAWDOWN clamped" plus a player toast, on every recalc, forever.
            // The clamp itself is correct and untouched here; the pending adjustment is
            // what overstays.
            const double Live = 641.790283;      // KSP's own pool
            double phantomTarget = Live - ExchangeTake;

            var clampedDecision = KspStatePatcher.ResolveSciencePoolPatch(
                (float)Live, phantomTarget, phantomTarget, authoritativeReduction: false);
            Assert.True(clampedDecision.Clamped);
            Assert.Equal(KspStatePatcher.ClampDirection.Up, clampedDecision.Direction);
            Assert.Equal(Live, clampedDecision.EffectiveTarget, 4);   // live values preserved

            // With the bound the pending amount is zero, so nothing drags the
            // discriminator and there is no clamp to emit.
            var boundedDecision = KspStatePatcher.ResolveSciencePoolPatch(
                (float)Live, Live, Live, authoritativeReduction: false);
            Assert.False(boundedDecision.Clamped);
            Assert.Equal(KspStatePatcher.ClampDirection.None, boundedDecision.Direction);
        }

        // ================================================================
        // The load sweep: what it measures
        // ================================================================

        [Fact]
        public void LoadSweep_MeasuresThePreFixExchangeAsUnmatchable()
        {
            var ledger = LoadPreFixFixtureLedger();
            var events = new List<GameStateEvent>
            {
                StrategyInputScienceDebit(ExchangeUT, ScienceBefore, ScienceAfter)
            };

            double baseline = LedgerOrchestrator.ComputeUnmatchableStrategyScienceDebitBaseline(
                events, ledger, null, LedgerOrchestrator.CanStrategyScienceDebitRowStillLand);

            Assert.Equal(ExchangeTake, baseline, 6);
        }

        [Fact]
        public void PreFixShapedStore_AfterTheBound_ProducesZeroPending()
        {
            var ledger = LoadPreFixFixtureLedger();
            var events = new List<GameStateEvent>
            {
                StrategyInputScienceDebit(ExchangeUT, ScienceBefore, ScienceAfter)
            };

            double baseline = LedgerOrchestrator.ComputeUnmatchableStrategyScienceDebitBaseline(
                events, ledger, null, LedgerOrchestrator.CanStrategyScienceDebitRowStillLand);

            double pending = LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit(
                events, ledger, null, baseline);

            Assert.Equal(0.0, pending, 6);
        }

        [Fact]
        public void PostFixSave_WhoseRowAlreadyLanded_MeasuresAZeroBaseline()
        {
            // IDEMPOTENCE, half one: an already-correct save must not be "repaired". The
            // matched event/row pair cancels INSIDE the sweep, so the residual is 0 and
            // the pending helper is byte-identical to its pre-fix-fix behaviour.
            var events = new List<GameStateEvent>
            {
                StrategyInputScienceDebit(ExchangeUT, ScienceBefore, ScienceAfter)
            };
            var ledger = new List<GameAction>
            {
                ExchangerDebitRow(ExchangeUT, ExchangeTake)
            };

            double baseline = LedgerOrchestrator.ComputeUnmatchableStrategyScienceDebitBaseline(
                events, ledger, null, LedgerOrchestrator.CanStrategyScienceDebitRowStillLand);
            Assert.Equal(0.0, baseline, 6);

            Assert.Equal(0.0,
                LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit(
                    events, ledger, null, baseline),
                6);
        }

        [Fact]
        public void TheSweepIsIdempotent_ReRunningItNeverAccumulates()
        {
            // IDEMPOTENCE, half two: the sweep is re-run on EVERY load, so running it
            // again over the same store must report the same residual (never twice the
            // take), and applying that residual must floor at zero rather than flipping
            // the adjustment into a phantom UPLIFT in the other direction.
            var ledger = LoadPreFixFixtureLedger();
            var events = new List<GameStateEvent>
            {
                StrategyInputScienceDebit(ExchangeUT, ScienceBefore, ScienceAfter)
            };

            double first = LedgerOrchestrator.ComputeUnmatchableStrategyScienceDebitBaseline(
                events, ledger, null, LedgerOrchestrator.CanStrategyScienceDebitRowStillLand);
            double second = LedgerOrchestrator.ComputeUnmatchableStrategyScienceDebitBaseline(
                events, ledger, null, LedgerOrchestrator.CanStrategyScienceDebitRowStillLand);

            Assert.Equal(ExchangeTake, first, 6);
            Assert.Equal(first, second, 6);

            // Deliberately over-applied (twice the measured residual): the observed side
            // floors at zero, and the helper returns 0 rather than a negative adjustment.
            Assert.Equal(0.0,
                LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit(
                    events, ledger, null, first * 2.0),
                6);
        }

        // ================================================================
        // What the bound must NOT eat
        // ================================================================

        [Fact]
        public void FreshInSessionExchange_StillProducesTheFullHoldBack_OnTopOfTheBound()
        {
            // The live-recorder race the helper exists for, on a save that also carries
            // the pre-fix orphan. The sweep classifies only the orphan; the fresh
            // exchange - tagged to a recording that has not committed - is still
            // reachable, so its hold-back survives at full magnitude.
            const double FreshTake = 50.0;
            RecordingStore.AddRecordingWithTreeForTesting(
                new Recording { RecordingId = "rec-committed", VesselName = "Old" });

            var events = new List<GameStateEvent>
            {
                StrategyInputScienceDebit(ExchangeUT, ScienceBefore, ScienceAfter, "rec-committed"),
                StrategyInputScienceDebit(9000.0, 400.0, 400.0 - FreshTake, "rec-in-flight")
            };
            var ledger = new List<GameAction>();

            double baseline = LedgerOrchestrator.ComputeUnmatchableStrategyScienceDebitBaseline(
                events, ledger, null, LedgerOrchestrator.CanStrategyScienceDebitRowStillLand);
            Assert.Equal(ExchangeTake, baseline, 6);

            Assert.Equal(FreshTake,
                LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit(
                    events, ledger, null, baseline),
                6);
        }

        [Fact]
        public void FreshExchangeDrainsOnceItsCommitLandsTheRow_WithTheBoundApplied()
        {
            const double FreshTake = 50.0;
            RecordingStore.AddRecordingWithTreeForTesting(
                new Recording { RecordingId = "rec-committed", VesselName = "Old" });

            var events = new List<GameStateEvent>
            {
                StrategyInputScienceDebit(ExchangeUT, ScienceBefore, ScienceAfter, "rec-committed"),
                StrategyInputScienceDebit(9000.0, 400.0, 400.0 - FreshTake, "rec-in-flight")
            };

            // Baseline is measured at LOAD, before the fresh recording commits.
            double baseline = LedgerOrchestrator.ComputeUnmatchableStrategyScienceDebitBaseline(
                events, new List<GameAction>(), null,
                LedgerOrchestrator.CanStrategyScienceDebitRowStillLand);

            // ...then the commit lands the row and the adjustment drains, exactly as it
            // does on a save with no pre-fix history.
            var ledgerAfterCommit = new List<GameAction>
            {
                ExchangerDebitRow(9000.0, FreshTake, "rec-in-flight")
            };

            Assert.Equal(0.0,
                LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit(
                    events, ledgerAfterCommit, null, baseline),
                6);
        }

        [Fact]
        public void InFlightRecordingSpanningASaveLoad_IsNotSweptIntoTheBaseline()
        {
            // A mid-flight save reloaded: the exchange fired, the event is tagged, and the
            // recording that owns it has still not committed. Its row is genuinely still
            // coming, so the sweep must classify it reachable and measure nothing - a
            // plain "older than this session" age gate would cancel it and re-open the
            // clamp in the other direction.
            var events = new List<GameStateEvent>
            {
                StrategyInputScienceDebit(ExchangeUT, ScienceBefore, ScienceAfter, "rec-in-flight")
            };

            double baseline = LedgerOrchestrator.ComputeUnmatchableStrategyScienceDebitBaseline(
                events, new List<GameAction>(), null,
                LedgerOrchestrator.CanStrategyScienceDebitRowStillLand);
            Assert.Equal(0.0, baseline, 6);

            Assert.Equal(ExchangeTake,
                LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit(
                    events, new List<GameAction>(), null, baseline),
                6);
        }

        // ================================================================
        // Composition with the shipped STRATEGY-* scoping rules
        // ================================================================

        [Fact]
        public void DiscardRehomedUntaggedRow_StillCancelsItsOrphanedEvent_UnderTheBound()
        {
            // STRATEGY-SCIENCE-CONVERSION-LEAK's symmetry rule: both sides are the SAME
            // population (tagged and untagged alike), which is what lets a non-rewind
            // discard's re-homed UNTAGGED row (PreserveIrreversibleLiveGameplayOnDiscard)
            // cancel its orphaned event. The bound is a NET of those same two populations
            // rather than a tag filter on one of them, so the cancellation survives:
            // orphan+row contribute 0 to the residual and 0 to the pending amount, and the
            // genuinely in-flight exchange is reported at full magnitude.
            const double OrphanTake = 20.0;
            const double InFlightTake = 30.0;
            RecordingStore.AddRecordingWithTreeForTesting(
                new Recording { RecordingId = "rec-committed", VesselName = "Old" });

            var events = new List<GameStateEvent>
            {
                StrategyInputScienceDebit(ExchangeUT, ScienceBefore, ScienceAfter, "rec-committed"),
                StrategyInputScienceDebit(9100.0, 300.0, 300.0 - OrphanTake),          // discard orphan
                StrategyInputScienceDebit(9200.0, 200.0, 200.0 - InFlightTake, "rec-in-flight")
            };
            var ledger = new List<GameAction>
            {
                ExchangerDebitRow(9100.0, OrphanTake)      // re-homed UNTAGGED row
            };

            double baseline = LedgerOrchestrator.ComputeUnmatchableStrategyScienceDebitBaseline(
                events, ledger, null, LedgerOrchestrator.CanStrategyScienceDebitRowStillLand);
            Assert.Equal(ExchangeTake, baseline, 6);

            Assert.Equal(InFlightTake,
                LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit(
                    events, ledger, null, baseline),
                6);
        }

        [Fact]
        public void ConverterSourcedRows_DoNotShrinkTheBaseline_Either()
        {
            // The committed side's ConversionSource == Exchanger filter is load-bearing
            // (a converter row has no observed counterpart at all - the query family
            // mutates the CurrencyModifierQuery in place and the resulting ScienceChanged
            // carries the ORIGINAL reason). The sweep reuses that exact committed
            // predicate, so a converter row cannot shrink the residual and leave a
            // converter-sized phantom hold-back behind.
            var events = new List<GameStateEvent>
            {
                StrategyInputScienceDebit(ExchangeUT, ScienceBefore, ScienceAfter)
            };
            var ledger = new List<GameAction>
            {
                new GameAction
                {
                    UT = ExchangeUT,
                    Type = GameActionType.StrategyScienceDebit,
                    Cost = 3.0f,
                    ConversionSource = StrategyConversionSource.Converter
                }
            };

            double baseline = LedgerOrchestrator.ComputeUnmatchableStrategyScienceDebitBaseline(
                events, ledger, null, LedgerOrchestrator.CanStrategyScienceDebitRowStillLand);
            Assert.Equal(ExchangeTake, baseline, 6);

            Assert.Equal(0.0,
                LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit(
                    events, ledger, null, baseline),
                6);
        }

        [Fact]
        public void RetiredTimelineEvents_AreExcludedFromTheBaselineToo()
        {
            // The sweep runs the SAME visibility gate as the pending helper
            // (GameStateStore.IsEventVisibleToCurrentTimeline in production). If it did
            // not, a retired timeline's orphan would inflate the residual and cancel a
            // LIVE hold-back of the same size.
            var events = new List<GameStateEvent>
            {
                StrategyInputScienceDebit(ExchangeUT, ScienceBefore, ScienceAfter, "rec-retired")
            };

            Assert.Equal(0.0,
                LedgerOrchestrator.ComputeUnmatchableStrategyScienceDebitBaseline(
                    events,
                    new List<GameAction>(),
                    e => !string.Equals(e.recordingId, "rec-retired", StringComparison.Ordinal),
                    LedgerOrchestrator.CanStrategyScienceDebitRowStillLand),
                6);
        }

        [Fact]
        public void TheBoundDefaultsToZero_SoEveryPreExistingCallerIsUnchanged()
        {
            // The parameter is optional and defaults to 0, so the three-argument form
            // every existing cell and every non-load caller uses keeps the original
            // unbounded netting.
            var events = new List<GameStateEvent>
            {
                StrategyInputScienceDebit(ExchangeUT, ScienceBefore, ScienceAfter, "rec-flight")
            };

            Assert.Equal(
                LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit(events, null),
                LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit(
                    events, null, null, 0.0),
                6);
        }

        // ================================================================
        // The production reachability predicate
        // ================================================================

        [Fact]
        public void CanRowStillLand_UntaggedEvent_IsUnreachable()
        {
            // The KSC door writes an ownerless exchange's row SYNCHRONOUSLY inside the
            // same call that stores the event, so an untagged event that still has no row
            // by load time never will.
            Assert.False(LedgerOrchestrator.CanStrategyScienceDebitRowStillLand(
                StrategyInputScienceDebit(ExchangeUT, ScienceBefore, ScienceAfter)));
        }

        [Fact]
        public void CanRowStillLand_TaggedToACommittedRecording_IsUnreachable()
        {
            // The commit-time converter is the only writer left for a tagged event, and
            // that recording's commit has already run.
            RecordingStore.AddRecordingWithTreeForTesting(
                new Recording { RecordingId = "rec-committed", VesselName = "Old" });

            Assert.False(LedgerOrchestrator.CanStrategyScienceDebitRowStillLand(
                StrategyInputScienceDebit(ExchangeUT, ScienceBefore, ScienceAfter, "rec-committed")));
        }

        [Fact]
        public void CanRowStillLand_TaggedToAnUncommittedRecording_IsStillReachable()
        {
            Assert.True(LedgerOrchestrator.CanStrategyScienceDebitRowStillLand(
                StrategyInputScienceDebit(ExchangeUT, ScienceBefore, ScienceAfter, "rec-in-flight")));
        }

        // ================================================================
        // End to end through the live patch-target adjuster
        // ================================================================

        [Fact]
        public void PreFixSave_PatchTargetIsNoLongerHeldBack_OnceTheLoadSweepHasRun()
        {
            // The live wrapper reads GameStateStore / Ledger / the load-time baseline, so
            // this is the whole production path minus OnKspLoad's I/O. The recording is
            // registered committed (its commit ran long ago) and there is no row - the
            // pre-fix shape.
            RecordingStore.AddRecordingWithTreeForTesting(
                new Recording { RecordingId = "rec-prefix", VesselName = "Exchanger" });

            var evt = StrategyInputScienceDebit(
                ExchangeUT, ScienceBefore, ScienceAfter, "rec-prefix");
            GameStateStore.AddEvent(ref evt);

            // BEFORE the sweep: the target is dragged down to live on every recalc.
            double unbounded = KspStatePatcher.AdjustSciencePatchTargetForPendingStrategyScienceDebit(
                targetScience: ScienceBefore, currentScience: (float)ScienceAfter);
            Assert.Equal(ScienceAfter, unbounded, 4);
            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") &&
                l.Contains("holding back") &&
                l.Contains("pending strategy-exchange science"));

            // AFTER the sweep the residual is measured and the hold-back is released.
            double baseline = LedgerOrchestrator.ComputeUnmatchableStrategyScienceDebitBaseline(
                GameStateStore.Events,
                Ledger.Actions,
                GameStateStore.IsEventVisibleToCurrentTimeline,
                LedgerOrchestrator.CanStrategyScienceDebitRowStillLand);
            Assert.Equal(ExchangeTake, baseline, 6);
            LedgerOrchestrator.SetUnmatchableStrategyScienceDebitBaselineForTesting(baseline);

            double bounded = KspStatePatcher.AdjustSciencePatchTargetForPendingStrategyScienceDebit(
                targetScience: ScienceBefore, currentScience: (float)ScienceAfter);
            Assert.Equal(ScienceBefore, bounded, 4);
            Assert.Equal(0.0, LedgerOrchestrator.GetPendingUncommittedStrategyScienceDebit(), 6);
        }

        [Fact]
        public void ResetForTesting_ClearsTheBaseline()
        {
            LedgerOrchestrator.SetUnmatchableStrategyScienceDebitBaselineForTesting(42.0);
            Assert.Equal(42.0,
                LedgerOrchestrator.GetUnmatchableStrategyScienceDebitBaselineForTesting(), 6);

            LedgerOrchestrator.ResetForTesting();

            Assert.Equal(0.0,
                LedgerOrchestrator.GetUnmatchableStrategyScienceDebitBaselineForTesting(), 6);
        }

        [Fact]
        public void FormatterSanity_TheMeasuredTakeIsTheOneThePinsUse()
        {
            // Guards the constants above against a typo silently weakening every cell:
            // this is the same magnitude C2CareerLedgerReplayTests pins as the fixture's
            // science divergence.
            Assert.Equal("108.84171852", ExchangeTake.ToString("F8", IC));
        }
    }
}
