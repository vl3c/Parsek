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
        /// </summary>
        internal static double ComputePendingUncommittedStrategyScienceDebit(
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> ledgerActions,
            Func<GameStateEvent, bool> visibilityFilter = null)
        {
            double observedDebit = SumObservedStrategyInputScienceDebits(events, visibilityFilter);

            if (observedDebit <= 0.1)
                return 0.0;

            double committedDebit = SumCommittedExchangerStrategyScienceDebits(ledgerActions);

            double pendingDebit = observedDebit - committedDebit;
            return pendingDebit > 0.1 ? pendingDebit : 0.0;
        }

        /// <summary>
        /// The OBSERVED side of the exchanger pending family: stored
        /// ScienceChanged(StrategyInput) debits that pass the visibility gate.
        /// </summary>
        private static double SumObservedStrategyInputScienceDebits(
            IReadOnlyList<GameStateEvent> events,
            Func<GameStateEvent, bool> visibilityFilter)
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
        /// Live wrapper over <see cref="ComputePendingUncommittedStrategyScienceDebit"/>:
        /// the current GameStateStore / ledger state behind the current-timeline
        /// visibility gate. The patch-target adjuster and the drawdown-guard
        /// discriminator both read it.
        /// </summary>
        internal static double GetPendingUncommittedStrategyScienceDebit()
        {
            return ComputePendingUncommittedStrategyScienceDebit(
                GameStateStore.Events,
                Ledger.Actions,
                GameStateStore.IsEventVisibleToCurrentTimeline);
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
            var probe = RecalculationEngine.ProbeModuleAtCutoff<ScienceModule>(moduleSet, actions, nowUT);
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
            var probe = RecalculationEngine.ProbeModuleAtCutoff<FundsModule>(moduleSet, actions, nowUT);
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
