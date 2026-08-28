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
    /// observed-minus-ingested window never drains.
    ///
    /// <para>TWO PRE-FIX SHAPES, and the fix has to tell them apart. Both carry the same
    /// POPULATION (one observed event, no row); they differ in whether the reconstruction
    /// already runs high by the un-ingested take.</para>
    ///
    /// <list type="bullet">
    /// <item><b>Shape A</b> - <c>preStrategyRunning == live</c>. THE FILED DEFECT: the
    /// pending amount drags only the guard's discriminator below live, producing a
    /// permanent no-op <c>GUARDED DRAWDOWN clamped</c> WARN + toast. 15 of the 18 science
    /// clamps in <c>logs/2026-08-19_0002_strategy-multi-live</c> are this shape; the two
    /// measured pairs are pinned below verbatim.</item>
    /// <item><b>Shape B</b> - <c>preStrategyRunning == live + take</c>, the DOCUMENTED
    /// <c>C2Career</c> shape (<c>C2CareerLedgerReplayTests</c> pins
    /// <c>recon - save = +108.84171851920314</c>). SILENT on main, because the pending
    /// amount is doing real work: it pulls both target and discriminator onto live.
    /// Cancelling it outright trips <c>IsGuardableUplift</c> instead - the same forbidden
    /// unbounded-WARN class in the mirror direction.</item>
    /// </list>
    ///
    /// <para>So the fix is two pieces: a load sweep
    /// (<c>ComputeUnmatchableStrategyScienceDebitBaseline</c>) measuring how much observed
    /// debit can never be ingested, and a per-patch resolver
    /// (<c>ResolveEffectiveUnmatchableStrategyScienceBaseline</c>) withholding that residual
    /// only as far as the reconstruction does not already carry it. The cells below drive
    /// BOTH shapes all the way through <c>KspStatePatcher.ResolveSciencePoolPatch</c>, which
    /// is where the clamp actually fires.</para>
    ///
    /// <para>The pre-fix LEDGER shape is not synthesized - the fixture ledger at
    /// <c>Fixtures/C2Career/Parsek/GameState/ledger.pgld</c> is real frozen pre-fix data
    /// carrying the exchange's FUNDS credit and no science row at all.</para>
    /// </summary>
    [Collection("Sequential")]
    public class StrategyPrefixHoldbackTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // The measured researchIPsellout exchange, as the recorder stored it.
        private const double ExchangeUT = 8599.8755;
        private const double ScienceBefore = 750.0;
        private const double ScienceAfter = 641.15828148;
        private const double ExchangeTake = ScienceBefore - ScienceAfter;   // 108.84171852

        // SHAPE A, verbatim from logs/2026-08-19_0002_strategy-multi-live. Both measured
        // pairs are here; the WARN prints the PENDING-ADJUSTED running balance, so the raw
        // pre-strategy basis is that plus the take - and it equals live, which is what makes
        // the clamp a pure no-op (wouldBeTarget == clampedTo == live in the log line).
        private const double ShapeALiveA = 203.32925415039063;
        private const double ShapeAWarnRunningA = 94.487645842134953;   // 8 occurrences
        private const double ShapeALiveB = 237.81199645996094;
        private const double ShapeAWarnRunningB = 128.97038815170527;   // 7 occurrences

        // SHAPE B, from the C2Career fixture pins.
        private const double ShapeBLive = 641.790283203125;             // SaveScience
        private const double C2ReconDelta = 108.84171851920314;         // recon - save

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

        private static List<GameStateEvent> OnePreFixOrphan()
        {
            return new List<GameStateEvent>
            {
                StrategyInputScienceDebit(ExchangeUT, ScienceBefore, ScienceAfter)
            };
        }

        private static double SweepBaseline(
            IReadOnlyList<GameStateEvent> events, IReadOnlyList<GameAction> ledger)
        {
            return LedgerOrchestrator.ComputeUnmatchableStrategyScienceDebitBaseline(
                events, ledger, null, LedgerOrchestrator.CanStrategyScienceDebitRowStillLand);
        }

        /// <summary>
        /// The whole PatchScience composition for the strategy leg, evaluated purely:
        /// resolve the effective baseline against the overhang, derive the pending amount,
        /// apply it to BOTH the target and the discriminator exactly as PatchScience does,
        /// and return the resulting clamp decision. <c>loadBaseline = 0</c> reproduces main.
        /// </summary>
        private static KspStatePatcher.SciencePoolPatchDecision DriveSciencePatch(
            double live, double preStrategyRunning, double rawTarget,
            IReadOnlyList<GameStateEvent> events, IReadOnlyList<GameAction> ledger,
            double loadBaseline)
        {
            double effectiveBaseline =
                LedgerOrchestrator.ResolveEffectiveUnmatchableStrategyScienceBaseline(
                    loadBaseline, preStrategyRunning, live);
            double pending = LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit(
                events, ledger, null, effectiveBaseline);

            // AdjustSciencePatchTargetForPendingStrategyScienceDebit's arithmetic: inert
            // when the target already sits at/below live, else target - pending floored at
            // live.
            double target = rawTarget;
            if (target > live && pending > 0.0)
            {
                target -= pending;
                if (target < live)
                    target = live;
            }

            double running = preStrategyRunning - pending;
            return KspStatePatcher.ResolveSciencePoolPatch(
                (float)live, target, running, authoritativeReduction: false);
        }

        /// <summary>
        /// The REAL frozen pre-fix ledger: 68 rows including the exchange's
        /// <c>FundsEarning</c>/<c>FundsEarningSource.Strategy</c> credit and no science row
        /// of any kind at the exchange UT.
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
        // The defect, reproduced on the pre-fix population
        // ================================================================

        [Fact]
        public void PreFixShapedStore_WithNoBound_HoldsBackTheFullExchangeOnEveryRecalc()
        {
            // The real pre-fix ledger + the exchange event KSP stored beside it. No
            // StrategyScienceDebit row exists and none can ever be written for it, so the
            // observed-minus-ingested gap is the whole take and it never drains: two
            // successive evaluations (two recalcs) report the identical hold-back.
            var ledger = LoadPreFixFixtureLedger();
            var events = OnePreFixOrphan();

            double first = LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit(
                events, ledger);
            double second = LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit(
                events, ledger);

            Assert.Equal(ExchangeTake, first, 6);
            Assert.Equal(first, second, 6);
        }

        // ================================================================
        // SHAPE A - the filed defect, on the two MEASURED log pairs
        // ================================================================

        [Theory]
        [InlineData(ShapeALiveA, ShapeAWarnRunningA)]
        [InlineData(ShapeALiveB, ShapeAWarnRunningB)]
        public void ShapeA_TheMeasuredWarnIsAPureNoOpClamp_AndTheBoundRemovesIt(
            double live, double warnRunning)
        {
            // Both measured pairs share the signature that identifies the filed defect:
            // live - (pending-adjusted running) is the exchange take to four places, so the
            // raw pre-strategy basis equals live and the reconstruction is NOT running high.
            Assert.Equal(108.8416, live - warnRunning, 4);
            double preStrategyRunning = warnRunning + (live - warnRunning);
            Assert.Equal(live, preStrategyRunning, 9);

            var events = OnePreFixOrphan();
            var ledger = new List<GameAction>();

            // MAIN (no bound): the target adjuster is inert (target already at live) and the
            // discriminator alone is dragged down, so the guard fires an UP clamp whose
            // effective target is live - wouldBeTarget == clampedTo == live, exactly the
            // no-op WARN the log repeats 15 times.
            var onMain = DriveSciencePatch(
                live, preStrategyRunning, rawTarget: live, events, ledger, loadBaseline: 0.0);
            Assert.True(onMain.Clamped);
            Assert.Equal(KspStatePatcher.ClampDirection.Up, onMain.Direction);
            Assert.Equal(live, onMain.EffectiveTarget, 6);
            Assert.False(onMain.ShouldWrite);

            // WITH the fix: the sweep measures the residual, the overhang is 0 so all of it
            // is withheld, the pending amount is 0 and there is no clamp to emit.
            double baseline = SweepBaseline(events, ledger);
            Assert.Equal(ExchangeTake, baseline, 6);

            var onBranch = DriveSciencePatch(
                live, preStrategyRunning, rawTarget: live, events, ledger, loadBaseline: baseline);
            Assert.False(onBranch.Clamped);
            Assert.Equal(KspStatePatcher.ClampDirection.None, onBranch.Direction);
            Assert.False(onBranch.ShouldWrite);
        }

        // ================================================================
        // SHAPE B - must stay silent (the review's Finding 2)
        // ================================================================

        [Fact]
        public void ShapeB_TheC2Shape_IsSilentOnMain_AndStaysSilentOnTheBranch()
        {
            // The reconstruction runs high by exactly the un-ingested take, so the pending
            // amount is load-bearing: it pulls the target and the discriminator onto live
            // and nothing clamps. That must be true on both sides of the fix.
            double preStrategyRunning = ShapeBLive + C2ReconDelta;
            var events = OnePreFixOrphan();
            var ledger = new List<GameAction>();

            var onMain = DriveSciencePatch(
                ShapeBLive, preStrategyRunning, rawTarget: preStrategyRunning,
                events, ledger, loadBaseline: 0.0);
            Assert.False(onMain.Clamped);
            Assert.Equal(KspStatePatcher.ClampDirection.None, onMain.Direction);

            double baseline = SweepBaseline(events, ledger);
            Assert.Equal(ExchangeTake, baseline, 6);

            var onBranch = DriveSciencePatch(
                ShapeBLive, preStrategyRunning, rawTarget: preStrategyRunning,
                events, ledger, loadBaseline: baseline);
            Assert.False(onBranch.Clamped);
            Assert.Equal(KspStatePatcher.ClampDirection.None, onBranch.Direction);
            Assert.Equal(onMain.EffectiveTarget, onBranch.EffectiveTarget, 6);
            Assert.Equal((double)onMain.Delta, (double)onBranch.Delta, 6);
        }

        [Fact]
        public void ShapeB_ApplyingTheWholeBaselineUnconditionally_WouldTripAnUpliftClamp()
        {
            // THE REJECTED DESIGN, pinned so it cannot come back. A population-only bound
            // (withhold the residual regardless of the reconstruction's overhang) zeroes the
            // pending amount on shape B, the target overshoots live by the take,
            // IsGuardableUplift fires, and the player gets "Held your science at the spent
            // value" on every recalc - the forbidden class, mirrored.
            double preStrategyRunning = ShapeBLive + C2ReconDelta;

            double pendingUnderRejectedDesign =
                LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit(
                    OnePreFixOrphan(), new List<GameAction>(), null, ExchangeTake);
            Assert.Equal(0.0, pendingUnderRejectedDesign, 6);

            var rejected = KspStatePatcher.ResolveSciencePoolPatch(
                (float)ShapeBLive, preStrategyRunning, preStrategyRunning,
                authoritativeReduction: false);
            Assert.True(rejected.Clamped);
            Assert.Equal(KspStatePatcher.ClampDirection.Down, rejected.Direction);
        }

        // ================================================================
        // The overhang resolver, stated directly
        // ================================================================

        [Fact]
        public void Resolver_NoOverhang_WithholdsTheWholeResidual()
        {
            Assert.Equal(ExchangeTake,
                LedgerOrchestrator.ResolveEffectiveUnmatchableStrategyScienceBaseline(
                    ExchangeTake, preStrategyRunning: 500.0, currentLiveScience: 500.0),
                6);
        }

        [Fact]
        public void Resolver_OverhangEqualsTheResidual_WithholdsNothing()
        {
            Assert.Equal(0.0,
                LedgerOrchestrator.ResolveEffectiveUnmatchableStrategyScienceBaseline(
                    ExchangeTake,
                    preStrategyRunning: 500.0 + ExchangeTake,
                    currentLiveScience: 500.0),
                6);
        }

        [Fact]
        public void Resolver_PartialOverhang_LandsTheDiscriminatorExactlyOnLive()
        {
            // The general case: whatever fraction of the residual the reconstruction already
            // carries is left in place, and the rest is withheld - so the adjusted
            // discriminator comes out at live rather than on either side of it.
            const double Live = 500.0;
            double half = ExchangeTake / 2.0;
            double preStrategyRunning = Live + half;

            double effective = LedgerOrchestrator.ResolveEffectiveUnmatchableStrategyScienceBaseline(
                ExchangeTake, preStrategyRunning, Live);
            Assert.Equal(half, effective, 6);

            double pending = LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit(
                OnePreFixOrphan(), new List<GameAction>(), null, effective);
            Assert.Equal(Live, preStrategyRunning - pending, 6);
        }

        [Fact]
        public void Resolver_OverhangLargerThanTheResidual_WithholdsNothing_SoAGenuineLeakStillClamps()
        {
            // Safety direction: the resolver can only ever SHRINK the pending amount. An
            // over-count bigger than the measured residual leaves the pending amount at its
            // main value, so a genuine unmodelled spend still reaches the guard.
            Assert.Equal(0.0,
                LedgerOrchestrator.ResolveEffectiveUnmatchableStrategyScienceBaseline(
                    ExchangeTake,
                    preStrategyRunning: 500.0 + ExchangeTake + 400.0,
                    currentLiveScience: 500.0),
                6);
        }

        [Fact]
        public void Resolver_RunningBelowLive_WithholdsTheWholeResidual_AndTheDrawdownStillClamps()
        {
            // A genuine missing-earning drawdown: withholding the phantom raises the
            // discriminator, but it is still below live, so the guard still reports - with a
            // number that no longer double-counts the phantom.
            const double Live = 500.0;
            const double PreStrategyRunning = 400.0;

            double effective = LedgerOrchestrator.ResolveEffectiveUnmatchableStrategyScienceBaseline(
                ExchangeTake, PreStrategyRunning, Live);
            Assert.Equal(ExchangeTake, effective, 6);

            var decision = KspStatePatcher.ResolveSciencePoolPatch(
                (float)Live, Live, PreStrategyRunning, authoritativeReduction: false);
            Assert.True(decision.Clamped);
            Assert.Equal(KspStatePatcher.ClampDirection.Up, decision.Direction);
        }

        [Fact]
        public void Resolver_ZeroBaseline_IsAlwaysZero()
        {
            Assert.Equal(0.0,
                LedgerOrchestrator.ResolveEffectiveUnmatchableStrategyScienceBaseline(
                    0.0, preStrategyRunning: 900.0, currentLiveScience: 100.0),
                6);
        }

        // ================================================================
        // The load sweep: what it measures
        // ================================================================

        [Fact]
        public void LoadSweep_MeasuresThePreFixExchangeAsUnmatchable()
        {
            Assert.Equal(ExchangeTake, SweepBaseline(OnePreFixOrphan(), LoadPreFixFixtureLedger()), 6);
        }

        [Fact]
        public void PostFixSave_WhoseRowAlreadyLanded_MeasuresAZeroBaseline()
        {
            // IDEMPOTENCE, half one: an already-correct save must not be "repaired". The
            // matched event/row pair cancels INSIDE the sweep, so the residual is 0 and the
            // pending helper is byte-identical to its pre-fix-fix behaviour.
            var events = OnePreFixOrphan();
            var ledger = new List<GameAction> { ExchangerDebitRow(ExchangeUT, ExchangeTake) };

            double baseline = SweepBaseline(events, ledger);
            Assert.Equal(0.0, baseline, 6);

            Assert.Equal(0.0,
                LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit(
                    events, ledger, null, baseline),
                6);
        }

        [Fact]
        public void TheSweepIsIdempotent_ReRunningItNeverAccumulates()
        {
            // IDEMPOTENCE, half two: the sweep is re-run on EVERY load, so running it again
            // over the same store must report the same residual (never twice the take), and
            // applying that residual must floor at zero rather than flipping the adjustment
            // into a negative amount.
            var ledger = LoadPreFixFixtureLedger();
            var events = OnePreFixOrphan();

            double first = SweepBaseline(events, ledger);
            double second = SweepBaseline(events, ledger);
            Assert.Equal(ExchangeTake, first, 6);
            Assert.Equal(first, second, 6);

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
            // The live-recorder race the helper exists for, on a save that also carries the
            // pre-fix orphan. The sweep classifies only the orphan; the fresh exchange -
            // tagged to a recording that has not committed - is still reachable, so its
            // hold-back survives at full magnitude.
            const double FreshTake = 50.0;
            RecordingStore.AddRecordingWithTreeForTesting(
                new Recording { RecordingId = "rec-committed", VesselName = "Old" });

            var events = new List<GameStateEvent>
            {
                StrategyInputScienceDebit(ExchangeUT, ScienceBefore, ScienceAfter, "rec-committed"),
                StrategyInputScienceDebit(9000.0, 400.0, 400.0 - FreshTake, "rec-in-flight")
            };
            var ledger = new List<GameAction>();

            double baseline = SweepBaseline(events, ledger);
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
            double baseline = SweepBaseline(events, new List<GameAction>());

            // ...then the commit lands the row and the adjustment drains, exactly as it does
            // on a save with no pre-fix history.
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
            // coming, so the sweep must classify it reachable and measure nothing - a plain
            // "older than this session" age gate would cancel it and re-open the clamp in
            // the other direction.
            var events = new List<GameStateEvent>
            {
                StrategyInputScienceDebit(ExchangeUT, ScienceBefore, ScienceAfter, "rec-in-flight")
            };

            double baseline = SweepBaseline(events, new List<GameAction>());
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
        public void DiscardRehomedUntaggedRow_LeavesTheLiveHoldBackIntact()
        {
            // STRATEGY-SCIENCE-CONVERSION-LEAK's symmetry rule: both sides are the SAME
            // population, which is what lets a non-rewind discard's re-homed UNTAGGED row
            // (PreserveIrreversibleLiveGameplayOnDiscard) net out. Modelled the way the
            // product actually leaves it: the discard purge removes the discarded
            // recording's tagged event, so only the untagged row survives.
            //
            // The row lands on the committed side of BOTH the sweep and the pending helper,
            // so it cancels in the residual (baseline = take - 20) and again in the pending
            // algebra - which is exactly why the outcome is the in-flight exchange's own
            // hold-back and nothing else. A bound that filtered the observed side on tag
            // instead would have subtracted the re-homed row once too often.
            const double RehomedTake = 20.0;
            const double InFlightTake = 30.0;
            RecordingStore.AddRecordingWithTreeForTesting(
                new Recording { RecordingId = "rec-committed", VesselName = "Old" });

            var events = new List<GameStateEvent>
            {
                StrategyInputScienceDebit(ExchangeUT, ScienceBefore, ScienceAfter, "rec-committed"),
                StrategyInputScienceDebit(9200.0, 200.0, 200.0 - InFlightTake, "rec-in-flight")
            };
            var ledger = new List<GameAction>
            {
                ExchangerDebitRow(9100.0, RehomedTake)      // re-homed UNTAGGED row
            };

            double baseline = SweepBaseline(events, ledger);
            Assert.Equal(ExchangeTake - RehomedTake, baseline, 6);

            Assert.Equal(InFlightTake,
                LedgerOrchestrator.ComputePendingUncommittedStrategyScienceDebit(
                    events, ledger, null, baseline),
                6);
        }

        [Fact]
        public void ConverterSourcedRows_DoNotShrinkTheBaseline_Either()
        {
            // The committed side's ConversionSource == Exchanger filter is load-bearing (a
            // converter row has no observed counterpart at all - the query family mutates
            // the CurrencyModifierQuery in place and the resulting ScienceChanged carries
            // the ORIGINAL reason). The sweep reuses that exact committed predicate, so a
            // converter row cannot shrink the residual and leave a converter-sized phantom
            // hold-back behind.
            var events = OnePreFixOrphan();
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

            double baseline = SweepBaseline(events, ledger);
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
            // (GameStateStore.IsEventVisibleToCurrentTimeline in production). If it did not,
            // a retired timeline's orphan would inflate the residual and cancel a LIVE
            // hold-back of the same size.
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
        public void TheSweepRefusesANullReachabilityPredicate()
        {
            // Not optional on purpose: a null would read as "nothing is reachable", which
            // fail-opens toward maximum cancellation.
            Assert.Throws<ArgumentNullException>(() =>
                LedgerOrchestrator.ComputeUnmatchableStrategyScienceDebitBaseline(
                    OnePreFixOrphan(), new List<GameAction>(), null, null));
        }

        [Fact]
        public void TheBoundDefaultsToZero_SoEveryPreExistingCallerIsUnchanged()
        {
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
            // The KSC door writes an ownerless exchange's row SYNCHRONOUSLY inside the same
            // call that stores the event, so an untagged event that still has no row by load
            // time never will.
            Assert.False(LedgerOrchestrator.CanStrategyScienceDebitRowStillLand(
                StrategyInputScienceDebit(ExchangeUT, ScienceBefore, ScienceAfter)));
        }

        [Fact]
        public void CanRowStillLand_TaggedToACommittedRecording_IsUnreachable()
        {
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
        public void PreFixSave_ShapeA_PatchTargetIsNoLongerHeldBack_OnceTheLoadSweepHasRun()
        {
            // The live wrapper reads GameStateStore / Ledger / the load-time baseline, so
            // this is the production path minus OnKspLoad's I/O. Shape A: the reconstruction
            // agrees with live, so the overhang is 0 and the whole residual is withheld.
            RecordingStore.AddRecordingWithTreeForTesting(
                new Recording { RecordingId = "rec-prefix", VesselName = "Exchanger" });

            var evt = StrategyInputScienceDebit(
                ExchangeUT, ScienceBefore, ScienceAfter, "rec-prefix");
            GameStateStore.AddEvent(ref evt);

            const double Live = ShapeALiveA;

            // BEFORE the sweep: the target is dragged down toward live on every recalc.
            double unbounded = KspStatePatcher.AdjustSciencePatchTargetForPendingStrategyScienceDebit(
                targetScience: Live + 200.0, currentScience: (float)Live,
                preStrategyRunning: Live);
            Assert.Equal(Live + 200.0 - ExchangeTake, unbounded, 4);
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
                targetScience: Live + 200.0, currentScience: (float)Live,
                preStrategyRunning: Live);
            Assert.Equal(Live + 200.0, bounded, 4);
            Assert.Equal(0.0,
                LedgerOrchestrator.GetPendingUncommittedStrategyScienceDebit(Live, Live), 6);
        }

        [Fact]
        public void PreFixSave_ShapeB_KeepsItsHoldBack_AfterTheLoadSweep()
        {
            // The mirror of the cell above, through the same live wrapper: with the
            // reconstruction running high by the take, the sweep still measures the residual
            // but the resolver withholds none of it, so the hold-back is unchanged.
            RecordingStore.AddRecordingWithTreeForTesting(
                new Recording { RecordingId = "rec-prefix", VesselName = "Exchanger" });

            var evt = StrategyInputScienceDebit(
                ExchangeUT, ScienceBefore, ScienceAfter, "rec-prefix");
            GameStateStore.AddEvent(ref evt);

            LedgerOrchestrator.SetUnmatchableStrategyScienceDebitBaselineForTesting(ExchangeTake);

            double preStrategyRunning = ShapeBLive + ExchangeTake;
            Assert.Equal(ExchangeTake,
                LedgerOrchestrator.GetPendingUncommittedStrategyScienceDebit(
                    preStrategyRunning, ShapeBLive),
                6);

            double target = KspStatePatcher.AdjustSciencePatchTargetForPendingStrategyScienceDebit(
                targetScience: preStrategyRunning, currentScience: (float)ShapeBLive,
                preStrategyRunning: preStrategyRunning);
            Assert.Equal(ShapeBLive, target, 4);
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
        public void FormatterSanity_TheMeasuredTakeMatchesTheC2ReconDivergence()
        {
            // Guards the constants against a typo silently weakening every cell: the event's
            // take and the C2 fixture's pinned recon divergence are the same quantity.
            Assert.Equal("108.84171852", ExchangeTake.ToString("F8", IC));
            Assert.Equal(ExchangeTake, C2ReconDelta, 6);
        }
    }
}
