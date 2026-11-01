using System;
using System.Collections.Generic;
using System.Globalization;
using KscActionExpectation = Parsek.KscActionExpectationClassifier.KscActionExpectation;

namespace Parsek
{
    internal static partial class LedgerOrchestrator
    {
        /// <summary>
        /// Pure helper for the live KSC tech-unlock race: when KSP has already applied a
        /// recent <c>ScienceChanged(RnDTechResearch)</c> debit but the matching
        /// <c>TechResearched -> ScienceSpending</c> action has not landed in the ledger yet,
        /// this returns the unmatched science amount that would otherwise be refunded by
        /// <see cref="KspStatePatcher.PatchScience"/>.
        /// </summary>
        internal static double ComputePendingRecentKscTechResearchScienceDebit(
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> ledgerActions,
            double nowUt)
        {
            double observedDebit = 0.0;
            if (events != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    var evt = events[i];
                    if (evt.eventType != GameStateEventType.ScienceChanged)
                        continue;
                    if (!string.Equals(evt.key, TechResearchScienceReasonKey, StringComparison.Ordinal))
                        continue;
                    if (evt.valueAfter >= evt.valueBefore)
                        continue;
                    if (Math.Abs(evt.ut - nowUt) > KscReconcileEpsilonSeconds)
                        continue;

                    observedDebit += evt.valueBefore - evt.valueAfter;
                }
            }

            if (observedDebit <= 0.1)
                return 0.0;

            double committedDebit = 0.0;
            if (ledgerActions != null)
            {
                for (int i = 0; i < ledgerActions.Count; i++)
                {
                    var action = ledgerActions[i];
                    if (action == null)
                        continue;
                    if (action.Type != GameActionType.ScienceSpending)
                        continue;
                    if (!string.IsNullOrEmpty(action.RecordingId))
                        continue;
                    if (Math.Abs(LedgerHeldWindow.CaptureMomentUT(action) - nowUt) > KscReconcileEpsilonSeconds)
                        continue;

                    committedDebit += action.Cost;
                }
            }

            double pendingDebit = observedDebit - committedDebit;
            return pendingDebit > 0.1 ? pendingDebit : 0.0;
        }

        /// <summary>
        /// Live wrapper over <see cref="ComputePendingRecentKscTechResearchScienceDebit"/>.
        /// Uses the current GameStateStore / ledger state and the affordability "now UT"
        /// seam so both production and tests evaluate the same recent-action window.
        /// </summary>
        internal static double GetPendingRecentKscTechResearchScienceDebit()
        {
            return ComputePendingRecentKscTechResearchScienceDebit(
                GameStateStore.Events,
                Ledger.Actions,
                GetRawClockUT());
        }

        /// <summary>
        /// Pure helper for the LIVE-RECORDER strategy currency-exchange race - the third
        /// member of the pending family beside
        /// <see cref="ComputePendingRecentKscTechResearchScienceDebit"/> and
        /// <see cref="ComputePendingRecentKscScienceCredit"/>.
        ///
        /// <para>When a stock CurrencyExchanger fires while a flight recorder owns the
        /// event (Patents Licensing converting science earned mid-flight), KSP debits the
        /// pool IMMEDIATELY but the matching
        /// <see cref="GameActionType.StrategyScienceDebit"/> row only lands at recording
        /// COMMIT. In between, the reconstruction's running balance sits ABOVE live and
        /// the drawdown guard fires "GUARDED UPLIFT clamped" (WARN + a player
        /// ScreenMessage) on every recalc for the rest of the flight, and the rewind
        /// read-back's byte-identity against the patch discriminator breaks.</para>
        ///
        /// <para>SHAPE DEVIATION from the two siblings, deliberate: they window on
        /// <c>|evt.ut - nowUt| &lt;= KscReconcileEpsilonSeconds</c> because their races
        /// close within a frame (a KSC door writes its row synchronously). This race stays
        /// open until commit, which can be an hour of flight later, so a recent-UT window
        /// would never see it. The window here is instead
        /// "observed-but-not-yet-ingested": every stored StrategyInput science debit minus
        /// every <see cref="GameActionType.StrategyScienceDebit"/> already in the ledger.
        /// It therefore drains to zero the moment the commit lands the row.</para>
        ///
        /// <para>BOTH SIDES ARE THE SAME POPULATION (tagged and untagged alike), for the
        /// same reason <see cref="ComputePendingRecentKscScienceCredit"/> does not filter
        /// its committed side on <c>RecordingId</c>: an ownerless KSC-door exchange writes
        /// its row synchronously, so it appears on both sides and cancels to zero - the
        /// recording-TAGGED exchange is the only population that can produce a nonzero
        /// result. Keeping the populations symmetric is also what makes the unbounded
        /// window safe across a DISCARD: a non-rewind discard re-homes the debit as an
        /// UNTAGGED row (<see cref="PreserveIrreversibleLiveGameplayOnDiscard"/>), which
        /// stays on the committed side and cancels the orphaned event.</para>
        ///
        /// <param name="visibilityFilter">Optional per-event gate. Production passes
        /// <c>GameStateStore.IsEventVisibleToCurrentTimeline</c> so a retired timeline's
        /// events cannot hold a phantom adjustment open; tests pass null.</param>
        /// <param name="unmatchableBaseline">The load-time unmatchable residual measured by
        /// <see cref="ComputeUnmatchableStrategyScienceDebitBaseline"/>: the part of the
        /// observed population that no future row can EVER ingest, because it was written
        /// before the capture fix (PR #1483) existed. Subtracted from the observed side so
        /// a pre-fix save's orphaned event stops holding the patch target back on every
        /// recalc for the rest of the save's life (STRATEGY-PREFIX-HOLDBACK-PERMANENT).
        /// Defaults to 0, so every pre-existing caller and every unit cell keeps the
        /// original unbounded netting.</param>
        /// </summary>
        internal static double ComputePendingUncommittedStrategyScienceDebit(
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> ledgerActions,
            Func<GameStateEvent, bool> visibilityFilter = null,
            double unmatchableBaseline = 0.0)
        {
            double observedDebit = SumObservedStrategyInputScienceDebits(
                events, visibilityFilter, null);

            // STRATEGY-PREFIX-HOLDBACK-PERMANENT. Bound the OBSERVED side by the residual
            // that was already proven unmatchable when the save loaded. The drawdown guard
            // is untouched (it is correct; the pending adjustment is what overstays) and
            // the committed side keeps its exchanger-only filter, so the observed /
            // committed symmetry the unbounded window depends on survives: the baseline is
            // itself a NET of the SAME two populations, so an ownerless KSC exchange and a
            // discard's re-homed untagged row still cancel exactly as before. See
            // ComputeUnmatchableStrategyScienceDebitBaseline.
            if (unmatchableBaseline > 0.0)
            {
                observedDebit -= unmatchableBaseline;
                if (observedDebit < 0.0)
                    observedDebit = 0.0;
            }

            if (observedDebit <= 0.1)
                return 0.0;

            double committedDebit = SumCommittedExchangerStrategyScienceDebits(ledgerActions);

            double pendingDebit = observedDebit - committedDebit;
            return pendingDebit > 0.1 ? pendingDebit : 0.0;
        }

        /// <summary>
        /// The OBSERVED side of the exchanger pending family, factored out so the pending
        /// helper and the load-time baseline sweep below evaluate the SAME four predicates
        /// (ScienceChanged / StrategyInput key / debit sign / visibility) over the same
        /// store. A second hand-written copy would be a second thing to keep in step.
        /// </summary>
        /// <param name="extraGate">Optional additional per-event gate applied after the
        /// four shared predicates. The baseline sweep passes "this event can no longer
        /// gain a row"; the pending helper passes null.</param>
        private static double SumObservedStrategyInputScienceDebits(
            IReadOnlyList<GameStateEvent> events,
            Func<GameStateEvent, bool> visibilityFilter,
            Func<GameStateEvent, bool> extraGate)
        {
            double observedDebit = 0.0;
            if (events == null)
                return observedDebit;

            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (evt.eventType != GameStateEventType.ScienceChanged)
                    continue;
                if (!string.Equals(
                        evt.key,
                        GameStateEventConverter.StrategyInputReasonKey,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                if (evt.valueAfter >= evt.valueBefore)
                    continue;
                if (visibilityFilter != null && !visibilityFilter(evt))
                    continue;
                if (extraGate != null && !extraGate(evt))
                    continue;

                observedDebit += evt.valueBefore - evt.valueAfter;
            }

            return observedDebit;
        }

        /// <summary>
        /// The COMMITTED side of the exchanger pending family, factored out for the same
        /// reason as <see cref="SumObservedStrategyInputScienceDebits"/>.
        ///
        /// <para>EXCHANGER rows only. The observed side is built from stored
        /// ScienceChanged(StrategyInput) events, which the QUERY family never produces (it
        /// mutates a CurrencyModifierQuery in place and the resulting ScienceChanged
        /// carries the ORIGINAL reason). A converter row therefore has no observed
        /// counterpart, and counting it here would subtract it from a genuine exchanger
        /// pending amount - under-adjusting the patch target by exactly the converter's
        /// take and re-opening the clamp+toast this helper exists to prevent. Converter
        /// rows need no pending adjustment of their own: the door writes them
        /// synchronously, so they are never "still catching up".</para>
        ///
        /// <para>Tagged and untagged rows alike, deliberately: the population must mirror
        /// the observed side's, which is what makes the unbounded window safe across a
        /// non-rewind discard (<c>PreserveIrreversibleLiveGameplayOnDiscard</c> re-homes
        /// the debit as an UNTAGGED row, which stays here and cancels the orphaned
        /// event).</para>
        /// </summary>
        private static double SumCommittedExchangerStrategyScienceDebits(
            IReadOnlyList<GameAction> ledgerActions)
        {
            double committedDebit = 0.0;
            if (ledgerActions == null)
                return committedDebit;

            for (int i = 0; i < ledgerActions.Count; i++)
            {
                var action = ledgerActions[i];
                if (action == null)
                    continue;
                if (action.Type != GameActionType.StrategyScienceDebit)
                    continue;
                if (action.ConversionSource != StrategyConversionSource.Exchanger)
                    continue;

                committedDebit += action.Cost;
            }

            return committedDebit;
        }

        /// <summary>
        /// STRATEGY-PREFIX-HOLDBACK-PERMANENT: the one-time load-sweep reconciliation that
        /// bounds <see cref="ComputePendingUncommittedStrategyScienceDebit"/>'s observed
        /// side.
        ///
        /// <para>THE DEFECT. The pending helper's window is deliberately unbounded in UT -
        /// the race it covers (KSP debits the pool at the exchange, the matching
        /// <see cref="GameActionType.StrategyScienceDebit"/> row lands at recording COMMIT)
        /// stays open for as long as the flight lasts, so a recent-UT window like its two
        /// siblings' would never see it. On a save whose exchange predates the capture fix
        /// the stored <c>ScienceChanged(StrategyInput)</c> event exists but the row never
        /// will, so the un-ingested gap is permanent: the adjustment holds the patch target
        /// back by the exchange's full take on EVERY recalc and the drawdown guard clamps
        /// (WARN + player toast) each time, forever. Measured live at exactly 108.84
        /// science on the test career's original <c>researchIPsellout</c> exchange
        /// (<c>logs/2026-08-19_0002_strategy-multi-live</c>).</para>
        ///
        /// <para>THE BOUND. Only two writers ever produce a
        /// <see cref="GameActionType.StrategyScienceDebit"/> exchanger row: the live KSC
        /// door (<c>OnKscSpending</c>, synchronous with the event) and the commit-time
        /// converter (<c>ConvertStrategyExchangeScience</c>, scoped to the committing
        /// recording's id). So an observed event can still gain a row if and only if it is
        /// TAGGED to a recording that has not committed yet. Every other visible observed
        /// event - untagged, or tagged to an already-committed recording - has had its one
        /// chance. This sweep sums exactly those, nets them against the committed side, and
        /// returns the unmatchable residual.</para>
        ///
        /// <para>WHY THIS SHAPE AND NOT A NARROWER PREDICATE. The observed and committed
        /// populations must stay symmetric w.r.t. <c>RecordingId</c> - that symmetry is
        /// what lets an ownerless KSC-door exchange cancel itself and lets a non-rewind
        /// discard's re-homed UNTAGGED row cancel its orphaned event
        /// (<c>PreserveIrreversibleLiveGameplayOnDiscard</c>). Filtering the observed side
        /// on tag WITHOUT mirroring the committed side would break exactly that. Returning
        /// a NET residual instead keeps both populations intact: a matched pair cancels
        /// inside this sweep and contributes nothing, so on an already-correct (post-fix)
        /// save the residual is 0 and the pending helper is byte-identical to before -
        /// which is also what makes re-running the sweep on every load idempotent.</para>
        ///
        /// <para>ONE QUALIFICATION on that symmetry, so it is not read as unconditional:
        /// when the residual is nonzero the committed side appears in BOTH terms of the
        /// pending algebra and cancels out of it, so an exchanger ROW paired with a
        /// STILL-REACHABLE event would be subtracted twice. That pairing cannot occur on a
        /// well-formed save - a row exists only after its recording committed or the KSC
        /// door ran, and either makes the event unreachable - so it is reachable only via a
        /// torn write (a ledger that landed the row while the store kept an uncommitted
        /// recording id). The direction is under-holding-back, i.e. toward the guard
        /// clamping rather than toward a wrong pool value.</para>
        ///
        /// <para>The result is DETERMINISTIC from the on-disk state, so it is recomputed
        /// from scratch at every load rather than persisted - no schema change, and no
        /// stale marker to reconcile. It is deliberately NOT a retro-fill: nothing is
        /// written to the ledger, so the reconstruction is untouched and
        /// <c>LedgerLoadMigration.IsMigrationConvertibleEvent</c>'s "pre-fix history stays
        /// pre-fix" rule is not disturbed.</para>
        /// </summary>
        /// <param name="rowStillReachable">REQUIRED per-event predicate: true when a future
        /// row can still ingest this event. Production passes
        /// <see cref="CanStrategyScienceDebitRowStillLand"/>; tests pass their own. Not
        /// optional on purpose - a null would read as "nothing is reachable", which
        /// fail-opens toward maximum cancellation.</param>
        internal static double ComputeUnmatchableStrategyScienceDebitBaseline(
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> ledgerActions,
            Func<GameStateEvent, bool> visibilityFilter,
            Func<GameStateEvent, bool> rowStillReachable)
        {
            if (rowStillReachable == null)
                throw new ArgumentNullException(nameof(rowStillReachable));

            double unmatchableObserved = SumObservedStrategyInputScienceDebits(
                events, visibilityFilter, e => !rowStillReachable(e));

            if (unmatchableObserved <= 0.1)
                return 0.0;

            double committedDebit = SumCommittedExchangerStrategyScienceDebits(ledgerActions);

            double residual = unmatchableObserved - committedDebit;
            return residual > 0.1 ? residual : 0.0;
        }

        /// <summary>
        /// THE SECOND HALF OF THE FIX, and the one that keeps it symmetric (Finding 2 of the
        /// `strategy-prefix-holdback` review). The load sweep above is POPULATION-based: it
        /// answers "how much observed debit can never be ingested". It cannot answer "is the
        /// reconstruction ALREADY running high by that amount", and the two pre-fix saves in
        /// the wild differ on exactly that:
        ///
        /// <list type="bullet">
        /// <item><b>Shape A</b> - the reconstruction agrees with the live pool
        /// (<c>preStrategyRunning == live</c>). 15 of the 18 science clamps in
        /// <c>logs/2026-08-19_0002_strategy-multi-live</c> are this shape (e.g.
        /// <c>running=94.487645842134953 live=203.32925415039063
        /// wouldBeTarget=203.32925415039063</c>: <c>live - running</c> is the exchange take
        /// to the digit and <c>wouldBeTarget == clampedTo == live</c>, i.e. a pure NO-OP
        /// WARN). Here the pending adjustment invents the discrepancy it then reports, and
        /// the target subtraction is inert because
        /// <c>AdjustSciencePatchTargetForPendingStrategyScienceDebit</c> early-returns when
        /// the target is already at/below live. THE FILED DEFECT.</item>
        /// <item><b>Shape B</b> - the reconstruction runs high by exactly the un-ingested
        /// take (<c>preStrategyRunning == live + take</c>), because the ledger holds the
        /// exchange's FUNDS credit and no science debit row. This is the DOCUMENTED
        /// <c>C2Career</c> fixture shape (<c>C2CareerLedgerReplayTests</c> pins
        /// <c>recon - save = +108.84171851920314</c>). Here the pending adjustment is doing
        /// REAL work: it pulls both the target and the discriminator down onto live, and the
        /// save is SILENT. Cancelling it outright would make the target overshoot live by
        /// the take, trip <c>IsGuardableUplift</c>, and produce a permanent
        /// <c>GUARDED UPLIFT clamped</c> WARN plus the "Held your science at the spent value"
        /// toast - re-creating the forbidden class in the mirror direction, on the very save
        /// the fixture suite documents.</item>
        /// </list>
        ///
        /// <para>So the unmatchable residual is WITHHELD ONLY TO THE EXTENT THE
        /// RECONSTRUCTION DOES NOT ALREADY CARRY IT: subtract the measured overhang
        /// (<c>preStrategyRunning - live</c>, floored at 0) from the baseline and clamp to
        /// [0, baseline]. Shape A has overhang 0 -> the whole residual is withheld -> pending
        /// 0 -> no clamp. Shape B has overhang == take -> nothing is withheld -> pending ==
        /// take -> byte-identical to main. Anything in between lands the discriminator
        /// exactly ON live rather than on either side of it.</para>
        ///
        /// <para>SAFETY DIRECTION: this can only ever SHRINK the pending adjustment, never
        /// grow it, so it cannot mask a leak. A genuine over-count larger than the residual
        /// (<c>overhang &gt; baseline</c>) withholds nothing and clamps exactly as main does;
        /// a genuine missing-earning DRAWDOWN (<c>overhang &lt; 0</c>) still reads below live
        /// after the withhold and still clamps - with a discriminator that no longer
        /// double-counts the phantom, so the reported number is the honest one.</para>
        /// </summary>
        /// <param name="preStrategyRunning">The running science balance with the two SIBLING
        /// pending adjusters already folded in and the strategy one not yet applied - i.e.
        /// the exact value the strategy debit is about to be subtracted from. Using the
        /// same basis on both sides is what makes the target adjuster and the guard
        /// discriminator resolve the identical pending amount.</param>
        internal static double ResolveEffectiveUnmatchableStrategyScienceBaseline(
            double unmatchableBaseline, double preStrategyRunning, double currentLiveScience)
        {
            if (unmatchableBaseline <= 0.0)
                return 0.0;

            double overhang = preStrategyRunning - currentLiveScience;
            if (overhang <= 0.0)
                return unmatchableBaseline;

            double withheld = unmatchableBaseline - overhang;
            return withheld > 0.0 ? withheld : 0.0;
        }

        /// <summary>
        /// Production reachability predicate for
        /// <see cref="ComputeUnmatchableStrategyScienceDebitBaseline"/>: an observed
        /// <c>ScienceChanged(StrategyInput)</c> debit can still gain its
        /// <see cref="GameActionType.StrategyScienceDebit"/> row only while it is tagged to
        /// a recording whose COMMIT has not run yet (the commit-time converter is the only
        /// writer left for it - the KSC door writes its row synchronously and is long
        /// past). Untagged, or tagged to an already-committed recording, means the row is
        /// unrecoverable.
        ///
        /// <para>Deliberately expressed as "not committed" rather than "is the live active
        /// tree": at <see cref="OnKspLoad"/> time <c>ParsekFlight.Instance</c> may not be up
        /// yet, and a mid-flight save's in-flight recording must not be mis-read as
        /// unreachable and have its genuine hold-back cancelled. A by-id committed lookup
        /// is available and correct at every load order.</para>
        /// </summary>
        internal static bool CanStrategyScienceDebitRowStillLand(GameStateEvent evt)
        {
            string recordingId = evt.recordingId;
            if (string.IsNullOrEmpty(recordingId))
                return false;
            return !RecordingStore.IsCommittedRecordingId(recordingId);
        }

        /// <summary>
        /// Re-measures <see cref="unmatchableStrategyScienceDebitBaseline"/> from the
        /// just-loaded store + ledger. Called once per <see cref="OnKspLoad"/>, after the
        /// ledger has been reconciled / repaired / migrated and before the load recalc, so
        /// the sweep sees the same rows the recalc is about to walk.
        /// </summary>
        private static void CaptureUnmatchableStrategyScienceDebitBaselineOnLoad()
        {
            double previous = unmatchableStrategyScienceDebitBaseline;
            unmatchableStrategyScienceDebitBaseline = ComputeUnmatchableStrategyScienceDebitBaseline(
                GameStateStore.Events,
                Ledger.Actions,
                GameStateStore.IsEventVisibleToCurrentTimeline,
                CanStrategyScienceDebitRowStillLand);

            if (unmatchableStrategyScienceDebitBaseline > 0.0)
            {
                ParsekLog.Info(Tag,
                    "Unmatchable strategy-exchange science baseline: " +
                    unmatchableStrategyScienceDebitBaseline.ToString("R", CultureInfo.InvariantCulture) +
                    " (was " + previous.ToString("R", CultureInfo.InvariantCulture) + "). " +
                    "This save carries stored ScienceChanged(StrategyInput) debit(s) that no " +
                    "future StrategyScienceDebit row can ingest (pre-capture-fix exchange); " +
                    "the pending adjustment is bounded by this amount so the drawdown guard " +
                    "stops clamping on every recalc. See STRATEGY-PREFIX-HOLDBACK-PERMANENT.");
            }
            else
            {
                ParsekLog.Verbose(Tag,
                    "Unmatchable strategy-exchange science baseline: 0 (was " +
                    previous.ToString("R", CultureInfo.InvariantCulture) +
                    ") - every observed strategy-exchange science debit is either still " +
                    "committable or already ingested.");
            }
        }

        /// <summary>Test seam for the load-time baseline (production sets it in OnKspLoad).</summary>
        internal static void SetUnmatchableStrategyScienceDebitBaselineForTesting(double value)
        {
            unmatchableStrategyScienceDebitBaseline = value;
        }

        /// <summary>Test-only reader paired with the setter above.</summary>
        internal static double GetUnmatchableStrategyScienceDebitBaselineForTesting()
        {
            return unmatchableStrategyScienceDebitBaseline;
        }

        /// <summary>
        /// Live wrapper over <see cref="ComputePendingUncommittedStrategyScienceDebit"/>.
        /// Passes the current GameStateStore / ledger state, the current-timeline
        /// visibility gate, and the load-time unmatchable baseline resolved against the
        /// reconstruction's ACTUAL overhang
        /// (<see cref="ResolveEffectiveUnmatchableStrategyScienceBaseline"/>).
        ///
        /// <para>Both live consumers - the patch-target adjuster and the drawdown-guard
        /// discriminator - must call this with the SAME two numbers so they resolve the
        /// identical pending amount; <c>KspStatePatcher.PatchScience</c> computes
        /// <paramref name="preStrategyRunning"/> once via
        /// <c>ComputePreStrategyPendingAdjustedRunningScience</c> and hands it to both.
        /// There is deliberately NO parameterless overload: one would silently apply the
        /// whole baseline and re-open the shape-B uplift this resolver exists to
        /// prevent.</para>
        /// </summary>
        internal static double GetPendingUncommittedStrategyScienceDebit(
            double preStrategyRunning, double currentLiveScience)
        {
            double effectiveBaseline = ResolveEffectiveUnmatchableStrategyScienceBaseline(
                unmatchableStrategyScienceDebitBaseline, preStrategyRunning, currentLiveScience);

            return ComputePendingUncommittedStrategyScienceDebit(
                GameStateStore.Events,
                Ledger.Actions,
                GameStateStore.IsEventVisibleToCurrentTimeline,
                effectiveBaseline);
        }

        /// <summary>
        /// Pure helper for the live KSC science-earning race (mirror of
        /// <see cref="ComputePendingRecentKscTechResearchScienceDebit"/>): when KSP has
        /// already applied a recent <c>ScienceChanged</c> credit (VesselRecovery /
        /// ScienceTransmission) but the matching <c>ScienceEarning</c> action has not landed
        /// in the ledger yet, this returns the un-ingested credit amount that
        /// <see cref="KspStatePatcher.PatchScience"/> would otherwise claw back, transiently
        /// zeroing the pool until the next recalc. Both untagged and recording-tagged
        /// <c>ScienceEarning</c> actions inside the window count as ingested: a recovered
        /// vessel's earning is recording-tagged once <c>PickRecoveryRecordingId</c> matches a
        /// committed recording, so (unlike the debit helper) the committed side does NOT
        /// filter on <c>RecordingId</c> — only the UT window gates the match.
        ///
        /// <para>QUERY-FAMILY GROSS-UP, and it is load-bearing rather than a refinement.
        /// The two sides of the subtraction must measure the SAME quantity: the observed
        /// side is a stored <c>ScienceChanged</c> delta and the committed side is the
        /// earning's <c>ScienceAwarded</c>, i.e. the GROSS subject value. A
        /// <c>Strategies.Effects.CurrencyConverter</c> mutates the
        /// <c>CurrencyModifierQuery</c> in place, so the <c>ScienceChanged</c> KSP fires is
        /// already NET of the take while the take now lives in the ledger as its own
        /// converter-sourced <see cref="GameActionType.StrategyScienceDebit"/> row. Left
        /// uncorrected the pending credit comes out exactly one take short, the
        /// pending-adjusted running balance sits one take BELOW live, and
        /// <see cref="KspStatePatcher.PatchScience"/>'s guard emits a GUARDED DRAWDOWN
        /// clamp on EVERY award-coupled conversion at KSC / TS. Folding the in-window
        /// converter legs back in (add the take, subtract a converter YIELD) restores the
        /// gross-vs-gross comparison and closes the mirrored uplift risk on
        /// <see cref="GameActionType.StrategyScienceCredit"/> at the same time. Deferring
        /// the door's recalc does NOT make this moot: the basis mismatch is structural and
        /// persists after everything has settled.</para>
        ///
        /// <para>Exchanger-sourced debits are deliberately NOT folded in: that family
        /// moves science under its own <c>StrategyInput</c> reason key, which is not a
        /// subject reason key, so it never contributes to the observed side in the first
        /// place and is netted by
        /// <see cref="ComputePendingUncommittedStrategyScienceDebit"/> instead. Credits
        /// carry no source discriminator (only the debit serializes one) and are written
        /// by the query-family door alone, so every in-window credit is a converter
        /// yield.</para>
        /// </summary>
        internal static double ComputePendingRecentKscScienceCredit(
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> ledgerActions,
            double nowUt)
        {
            double observedNetCredit = 0.0;
            if (events != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    var evt = events[i];
                    if (evt.eventType != GameStateEventType.ScienceChanged)
                        continue;
                    if (!GameStateRecorder.IsScienceSubjectReasonKey(evt.key))
                        continue;
                    if (evt.valueAfter <= evt.valueBefore)
                        continue;
                    if (Math.Abs(evt.ut - nowUt) > KscReconcileEpsilonSeconds)
                        continue;

                    observedNetCredit += evt.valueAfter - evt.valueBefore;
                }
            }

            // No observed subject credit means there is nothing to gross up. Checked
            // BEFORE the converter fold below so an in-window converter leg can never
            // invent a pending credit out of an award that was never observed.
            if (observedNetCredit <= 0.1)
                return 0.0;

            // Both sides use the raw KSP-credited amount: observedCredit from the
            // ScienceChanged event deltas above (grossed back up through the in-window
            // converter legs), committedCredit from each earning's ScienceAwarded (the
            // immutable awarded value, not the post-cap effective
            // science). The gap is therefore internally consistent, and the caller clamps
            // the held target to the live KSP pool, so a subject that the cross-recording
            // cap later trims can never inflate science above what KSP actually credited;
            // it only briefly holds the pre-ingest value until the next recalc.
            double committedCredit = 0.0;
            double converterTake = 0.0;
            double converterYield = 0.0;
            if (ledgerActions != null)
            {
                for (int i = 0; i < ledgerActions.Count; i++)
                {
                    var action = ledgerActions[i];
                    if (action == null)
                        continue;
                    if (Math.Abs(LedgerHeldWindow.CaptureMomentUT(action) - nowUt) > KscReconcileEpsilonSeconds)
                        continue;

                    // Pool units: the observed side is a post-ScienceGainMultiplier
                    // ScienceChanged delta, so the subject-unit award is scaled by the
                    // multiplier frozen on the row at capture.
                    if (action.Type == GameActionType.ScienceEarning)
                        committedCredit += action.GetScienceAwardedPoolCredit();
                    // The lab credit is observed under the same subject reason keys.
                    else if (action.Type == GameActionType.ScienceLabEarning)
                        committedCredit += action.ScienceAwarded;
                    else if (action.Type == GameActionType.StrategyScienceDebit
                             && action.ConversionSource == StrategyConversionSource.Converter)
                        converterTake += action.Cost;
                    else if (action.Type == GameActionType.StrategyScienceCredit)
                        converterYield += action.ScienceAwarded;
                }
            }

            double observedCredit = observedNetCredit + converterTake - converterYield;
            if (converterTake > 0.0 || converterYield > 0.0)
            {
                ParsekLog.Verbose(Tag,
                    "PendingRecentKscScienceCredit: grossed the observed NET credit " +
                    observedNetCredit.ToString("R", CultureInfo.InvariantCulture) +
                    " up through the in-window converter legs (take=" +
                    converterTake.ToString("R", CultureInfo.InvariantCulture) +
                    ", yield=" + converterYield.ToString("R", CultureInfo.InvariantCulture) +
                    ") -> " + observedCredit.ToString("R", CultureInfo.InvariantCulture));
            }

            if (observedCredit <= 0.1)
                return 0.0;

            double pendingCredit = observedCredit - committedCredit;
            return pendingCredit > 0.1 ? pendingCredit : 0.0;
        }

        /// <summary>
        /// Live wrapper over <see cref="ComputePendingRecentKscScienceCredit"/>.
        /// Uses the current GameStateStore / ledger state and the affordability "now UT"
        /// seam so both production and tests evaluate the same recent-action window.
        /// </summary>
        internal static double GetPendingRecentKscScienceCredit()
        {
            return ComputePendingRecentKscScienceCredit(
                GameStateStore.Events,
                Ledger.Actions,
                GetRawClockUT());
        }

        /// <summary>
        /// Every action id the ledger itself holds, effective or not (physical identity, not
        /// ERS / ELS: the stock pool reference asks whether a counted row is GONE from the
        /// ledger or only outside the effective ledger, committed-value rule plan 2.4).
        /// </summary>
        internal static HashSet<string> CollectLedgerActionIds()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                string id = actions[i]?.ActionId;
                if (!string.IsNullOrEmpty(id))
                    ids.Add(id);
            }
            return ids;
        }

        /// <summary>
        /// Pure affordability reconciliation (BUG-G): the spendable amount is what the pool
        /// patch writes, the reservation-aware target floored at 0. Anything stock applied
        /// that no other row holds is a <see cref="GameActionType.StockPoolAdjustment"/> row
        /// the probe's walk already carries, so an earning the ledger does not model stays
        /// spendable and a genuine reservation still lowers the amount.
        /// </summary>
        /// <param name="unflooredAvailable">The probe's projected minimum balance
        /// (<c>GetProjectionMinBalance</c>), before the 0 floor.</param>
        internal static double ComputeEffectiveAffordable(double unflooredAvailable)
        {
            return unflooredAvailable > 0.0 ? unflooredAvailable : 0.0;
        }

        /// <summary>
        /// Checks whether a science spending of the given cost is affordable under the
        /// current ledger reservation, reconciled with the live KSP pool so the gate
        /// never contradicts the "Keep what you earned" drawdown guard (BUG-G).
        /// Returns true if the effective (guard-consistent) available science >= cost.
        /// Used by the RDTech.ResearchTech / UnlockTech patches to block unfunded tech
        /// unlocks.
        /// </summary>
        /// <remarks>
        /// The walk is scoped to <c>Planetarium.GetUniversalTime()</c> as its UT cutoff so
        /// post-rewind future actions on the persisted ledger don't leak into affordability
        /// ("what's the state right now?" → right now = Planetarium's current UT, not the
        /// ledger's last action). It is a read-only probe
        /// (<see cref="RecalculationEngine.ProbeModuleAtCutoff{T}"/>): it walks clones of
        /// the modules, so a UI that asks (the R&amp;D Research mark) or a refused click
        /// never moves the live modules (a cutoff walk of the live kerbals module drops
        /// every hold a future committed flight owns). When the live R&D singleton exists
        /// (real KSP) the reservation-aware available is reconciled against live via
        /// <see cref="ComputeEffectiveAffordable"/>; with no singleton (xUnit) the gate
        /// keeps the pure-ledger contract.
        /// </remarks>
        internal static bool CanAffordScienceSpending(float cost)
        {
            double effectiveAvailable;
            return CanAffordScienceSpending(cost, out effectiveAvailable);
        }

        /// <summary><see cref="CanAffordScienceSpending(float)"/>, also giving the effective
        /// (guard-consistent) available science the decision compared against
        /// (+inf when there is no science module, which always affords). The R&amp;D
        /// Research refusal names it.</summary>
        internal static bool CanAffordScienceSpending(float cost, out double effectiveAvailable)
        {
            effectiveAvailable = double.PositiveInfinity;
            Initialize();
            if (scienceModule == null) return true;

            // A read-only cutoff walk over module clones at now: post-rewind future actions
            // don't leak into affordability, and no live module changes.
            // Phase 9 of Rewind-to-Staging (design §3.2): route through ELS so
            // any explicitly tombstoned action is excluded from the affordability
            // probe's walk.
            var actions = new System.Collections.Generic.List<GameAction>(EffectiveState.ComputeELS());
            double nowUT = GetNowUT();
            var probe = RecalculationEngine.ProbeModuleAtCutoff<ScienceModule>(actions, nowUT);
            if (probe == null)
            {
                ParsekLog.Warn(Tag, "CanAffordScienceSpending: science module probe unavailable - allowing");
                return true;
            }

            double available = probe.GetAvailableScience();

            // BUG-G: the spendable amount is the patch's own write (the probe's walk carries
            // the stock pool reference's adjustment rows). Stock's CurrencyModifierQuery
            // still backstops live affordability after the gate allows.
            effectiveAvailable = ComputeEffectiveAffordable(probe.GetProjectionMinBalance());

            bool affordable = effectiveAvailable >= (double)cost;

            ParsekLog.Verbose(Tag,
                $"CanAffordScienceSpending: cost={cost:F1}, available={available:F1}, " +
                $"effectiveAvailable={effectiveAvailable:F1}, affordable={affordable}, cutoffUT={nowUT:R}");

            return affordable;
        }

        /// <summary>
        /// Checks whether a funds spending of the given cost is affordable under the
        /// current ledger reservation. Returns true if available funds >= cost.
        /// Not yet wired to FacilityUpgradePatch — scaffolding for future use.
        /// </summary>
        /// <remarks>
        /// Same UT-cutoff rule as <see cref="CanAffordScienceSpending"/>: the walk is
        /// scoped to <c>Planetarium.GetUniversalTime()</c> so post-rewind future actions
        /// never fabricate or withhold present-day affordability.
        /// </remarks>
        internal static bool CanAffordFundsSpending(float cost)
        {
            double effectiveAvailable;
            return CanAffordFundsSpending(cost, out effectiveAvailable);
        }

        /// <summary><see cref="CanAffordFundsSpending(float)"/>, also giving the effective
        /// available funds it compared against (+inf when there is no funds module).</summary>
        internal static bool CanAffordFundsSpending(float cost, out double effectiveAvailable)
        {
            effectiveAvailable = double.PositiveInfinity;
            Initialize();
            if (fundsModule == null) return true;

            // A read-only cutoff walk over module clones at now (see CanAffordScienceSpending).
            // Phase 9 of Rewind-to-Staging (design §3.2): route through ELS so
            // any explicitly tombstoned action is excluded from the affordability
            // probe's walk.
            var actions = new System.Collections.Generic.List<GameAction>(EffectiveState.ComputeELS());
            double nowUT = GetNowUT();
            var probe = RecalculationEngine.ProbeModuleAtCutoff<FundsModule>(actions, nowUT);
            if (probe == null)
            {
                ParsekLog.Warn(Tag, "CanAffordFundsSpending: funds module probe unavailable - allowing");
                return true;
            }

            double available = probe.GetAvailableFunds();

            // BUG-G: same rule as CanAffordScienceSpending.
            effectiveAvailable = ComputeEffectiveAffordable(probe.GetProjectionMinBalance());

            bool affordable = effectiveAvailable >= (double)cost;

            ParsekLog.Verbose(Tag,
                $"CanAffordFundsSpending: cost={cost:F1}, available={available:F1}, " +
                $"effectiveAvailable={effectiveAvailable:F1}, affordable={affordable}, cutoffUT={nowUT:R}");

            return affordable;
        }
    }
}
