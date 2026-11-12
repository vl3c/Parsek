using System;
using System.Collections.Generic;
using System.Globalization;
using KscActionExpectation = Parsek.KscActionExpectationClassifier.KscActionExpectation;

namespace Parsek
{
    internal static partial class LedgerOrchestrator
    {
        /// <summary>
        /// Called when a KSC spending action occurs outside of a flight recording session.
        /// Converts the event directly to a GameAction and adds to the ledger, then recalculates.
        /// Used for tech unlocks, facility upgrades, and kerbal hires at KSC.
        /// </summary>
        /// <param name="evt">The GameStateEvent captured by GameStateRecorder.</param>
        internal static void OnKscSpending(GameStateEvent evt)
        {
            OnKscSpending(evt, null);
        }

        /// <summary>
        /// <see cref="OnKscSpending(GameStateEvent)"/> with the row tagged to
        /// <paramref name="recordingId"/>: a milestone a recovery completed after the recovered
        /// recording was committed (QUICKLOAD-UNTAGGED-RECOVERY-MILESTONE-SURVIVES-F9). Null
        /// writes the untagged KSC row exactly as before.
        /// </summary>
        internal static void OnKscSpending(GameStateEvent evt, string recordingId)
        {
            Initialize();

            GameAction action;
            if (!TryAddKscSpendingAction(evt, recordingId, out action))
                return;

            // Phase B (plan: fix-ledger-lump-sum-reconciliation.md): KSC-side ledger writes
            // were bypassing the commit-time reconciliation hook used by recording commits.
            // Key-match this action against the nearest FundsChanged/ScienceChanged/
            // ReputationChanged event in GameStateStore (paired by KSP TransactionReasons,
            // scoped to untransformed action types only) and log WARN on missing event or
            // delta mismatch. Surfaces missing earning channels (strategy, world first, etc.)
            // as they happen without triggering false positives on curve-/strategy-
            // transformed rewards.
            var deferHost = ResolveKscDeferHost();
            if (ShouldDeferKscReconcileOneFrame(action, deferHost != null))
            {
                GameAction deferredAction = action;
                double deferredUt = evt.ut;
                ParsekLog.Verbose(Tag,
                    $"OnKscSpending: deferring the reconcile and recalc one frame for {action.Type} - " +
                    "stock charges it after this handler returns");
                deferHost(() =>
                {
                    ReconcileKscAction(GameStateStore.Events, Ledger.Actions, deferredAction, deferredUt);
                    RecalculateAndPatchForLiveTimelineEvent(
                        ResolveDeferredLiveEventUT(deferredUt, TryReadNowUTForDeferredRecalc(), "ksc-spending"),
                        "ksc-spending");
                });
                return;
            }

            ReconcileKscAction(GameStateStore.Events, Ledger.Actions, action, evt.ut);

            // THE MILESTONE ROW'S RECALC IS DEFERRED ONE FRAME. Everything else on this
            // door recalculates inline, exactly as before.
            //
            // Decompiled ordering: ProgressNode.Complete() raises OnProgressComplete
            // BEFORE every subclass calls AwardProgressStandard (CelestialBodyOrbit,
            // CelestialBodyReturn, CelestialBodyLanding), so this whole handler runs while
            // the reward is still unapplied. GameStateRecorder.OnProgressComplete
            // therefore emits the event with BuildMilestoneDetail(0, 0, 0) and the
            // AwardProgressPatch postfix fills the real amounts in place afterwards
            // (EnrichPendingMilestoneRewards). An INLINE recalc here runs inside that
            // window: the seed's own ensure would read Reputation.Instance BEFORE the
            // award landed, take the live-pool branch on the PRE-award figure, and the
            // enriched row - stamped inside that figure - would be zeroed by the walk for
            // good. Deferring one frame puts the read after both the award and the
            // enrichment.
            //
            // Same host and same fallback as the strategy door: if no frame host exists
            // the recalc runs inline rather than being lost, and if the defer never fires
            // (scene exit) the row still stands in the ledger and the next natural recalc
            // picks it up. The seed simply stays uncaptured until then, which is the safe
            // direction - an uncaptured seed patches nothing.
            ScheduleKscSpendingRecalc(action.Type, evt.ut, "ksc-spending");
        }

        /// <summary>
        /// The batch form of <see cref="OnKscSpending"/>, for several KSC rows that one stock
        /// call produced at the same moment (the per-building FacilityRepair rows of one
        /// <c>SpaceCenterBuilding.RepairFacility</c>, see <see cref="FacilityRepairCapture"/>).
        /// Every row reaches the ledger BEFORE any is reconciled, so each row's
        /// <see cref="ReconcileKscAction"/> sums all of them against stock's single paired
        /// resource event (one FundsChanged(StructureRepair) debit for the whole facility)
        /// instead of warning on every partial sum; then one recalc runs.
        /// </summary>
        internal static void OnKscSpendingBatch(IReadOnlyList<GameStateEvent> events, string reason)
        {
            if (events == null || events.Count == 0)
                return;
            if (events.Count == 1)
            {
                OnKscSpending(events[0]);
                return;
            }

            Initialize();

            var added = new List<GameAction>(events.Count);
            var addedUts = new List<double>(events.Count);
            double maxUt = double.MinValue;
            for (int i = 0; i < events.Count; i++)
            {
                GameAction action;
                if (!TryAddKscSpendingAction(events[i], null, out action))
                    continue;
                added.Add(action);
                addedUts.Add(events[i].ut);
                if (events[i].ut > maxUt)
                    maxUt = events[i].ut;
            }

            if (added.Count == 0)
                return;

            for (int i = 0; i < added.Count; i++)
                ReconcileKscAction(GameStateStore.Events, Ledger.Actions, added[i], addedUts[i]);

            ParsekLog.Info(Tag,
                $"KSC spending batch recorded: reason={reason ?? "(none)"} " +
                $"rows={added.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"events={events.Count.ToString(CultureInfo.InvariantCulture)}");

            // One recalc for the batch. A row type that needs the one-frame defer (see
            // ShouldDeferKscRecalcOneFrame) defers the batch's recalc too.
            GameActionType deferKey = added[0].Type;
            for (int i = 0; i < added.Count; i++)
            {
                if (added[i].Type == GameActionType.MilestoneAchievement)
                {
                    deferKey = added[i].Type;
                    break;
                }
            }
            ScheduleKscSpendingRecalc(deferKey, maxUt, "ksc-spending");
        }

        /// <summary>
        /// Converts one KSC event, sequences and stamps it, and appends it to the ledger.
        /// False (nothing added) when the event type produces no action.
        /// </summary>
        private static bool TryAddKscSpendingAction(GameStateEvent evt, string recordingId, out GameAction action)
        {
            action = GameStateEventConverter.ConvertEvent(evt, recordingId);
            if (action == null)
            {
                ParsekLog.Verbose(Tag,
                    $"OnKscSpending: event type {evt.eventType} produced no action, skipping");
                return false;
            }

            // Assign a sequence number so multiple KSC events at the same UT have
            // deterministic ordering in the recalculation sort. (See also
            // OnVesselRolloutSpending, which follows this same pattern.)
            kscSequenceCounter++;
            action.Sequence = kscSequenceCounter;

            // A Space Center row made while the clock is behind the ledger held-through UT
            // (a Rewind-to-Launch lead window) is dated at it, after the held rows the loaded
            // world already shows (todo REWIND-LEAD-WINDOW-NEW-ACTION-SORTS-BEFORE-HELD-ROW).
            // A row tagged to a recording keeps its UT. The reconcile below pairs the row with
            // its event by capture moment, so it still finds the event at the raw clock.
            LedgerHeldWindow.StampKscOriginRow(action, "OnKscSpending");

            // THE DOOR STAMPS, IT DOES NOT SEED. Reviewed 2026-09-15 and corrected: an
            // earlier cut ensured the seed here, before the row reached the ledger. That
            // is unsafe for two of the four sources this door carries, because the live
            // pool is not yet the post-award pool when the event fires. Decompiled
            // ProgressNode.Complete() raises OnProgressComplete BEFORE every subclass
            // calls AwardProgressStandard (CelestialBodyOrbit, CelestialBodyReturn,
            // CelestialBodyLanding), and Reputation.AddReputation raises
            // OnCurrencyModified before it assigns `rep` - so a first-capture seed read
            // here would be the PRE-award pool, the row would be stamped inside a value
            // that does not contain it, and the walk would come out short by the award.
            // Contract rows and StrategyActivate are ordered the other way, but the door
            // cannot tell them apart from the action alone.
            //
            // So the seed is created where it always was: the tail recalc below. This call
            // only reads whether a seed EXISTS yet and stamps the row under the same rule
            // the commit path uses - no seed means NotYetCaptured, i.e. "a later live pool
            // will already have taken this award". If that later capture is a career-start
            // branch instead, RestampInsideSeedRowsAgainstCareerStartSeed flips the row
            // back out at the seed's own creation site. Non-reputation KSC rows (tech,
            // facility, hire) are not stamped at all.
            StampLiveWriteRowAgainstSeed(action, "ksc " + action.Type);

            Ledger.AddAction(action, LedgerRowWriter.StockApplied);

            ParsekLog.Info(Tag,
                $"KSC spending recorded: type={action.Type}, UT={evt.ut:F1}, " +
                $"key={evt.key ?? "(none)"}");

            return true;
        }

        /// <summary>
        /// The recalc tail of the KSC door: inline, or deferred one frame for a row type
        /// <see cref="ShouldDeferKscRecalcOneFrame"/> names (see the comment in
        /// <see cref="OnKscSpending"/>).
        /// </summary>
        private static void ScheduleKscSpendingRecalc(GameActionType type, double eventUt, string reason)
        {
            var deferHost = ResolveKscDeferHost();
            if (ShouldDeferKscRecalcOneFrame(type, deferHost != null))
            {
                double deferredUt = eventUt;
                ParsekLog.Verbose(Tag,
                    $"OnKscSpending: deferring the recalc one frame for {type} - " +
                    "the reward is applied after this handler returns");
                deferHost(() => RecalculateAndPatchForLiveTimelineEvent(
                    ResolveDeferredLiveEventUT(deferredUt, TryReadNowUTForDeferredRecalc(), reason),
                    reason));
            }
            else
            {
                RecalculateAndPatchForLiveTimelineEvent(eventUt, reason);
            }
        }

        /// <summary>
        /// The cutoff a deferred KSC recalc walks to: the event's UT, or the clock when it
        /// has moved past the event before the deferred frame ran. Rows filed in between
        /// (a contract accepted the frame after a hire) are already in stock; cutting at the
        /// stale event UT would read them as future and patch them back out of stock
        /// (QL-1: a just-accepted contract removed by PatchContracts). A clock behind the
        /// event (a rewind) or unreadable keeps the event UT.
        /// </summary>
        internal static double ResolveDeferredLiveEventUT(double eventUt, double nowUt, string reason)
        {
            if (double.IsNaN(nowUt) || double.IsInfinity(nowUt) || !(nowUt > eventUt))
                return eventUt;
            ParsekLog.Verbose(Tag,
                $"Deferred live-event recalc: reason={reason ?? "(none)"} " +
                $"eventUT={eventUt.ToString("R", CultureInfo.InvariantCulture)} " +
                $"nowUT={nowUt.ToString("R", CultureInfo.InvariantCulture)} -> cutoff at now");
            return nowUt;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static double TryReadNowUTForDeferredRecalc()
        {
            try
            {
                return GetNowUT();
            }
            catch (Exception)
            {
                return double.NaN;
            }
        }

        private static Action<Action> ResolveKscDeferHost()
        {
            return DeferOneFrameForTesting
                ?? (WarpToTimeConsumer.Instance != null
                    ? (Action<Action>)WarpToTimeConsumer.RunNextFrame
                    : null);
        }

        /// <summary>
        /// Pure: true when <see cref="OnKscSpending"/> must postpone its tail recalc to the
        /// next frame instead of running it inline.
        ///
        /// <para>
        /// Two row types qualify, each because of a decompiled ordering rather than a
        /// preference: the KerbalHire row (see <see cref="ShouldDeferKscReconcileOneFrame"/>)
        /// and the milestone row. <c>ProgressNode.Complete()</c> raises
        /// <c>OnProgressComplete</c> before the subclass calls
        /// <c>AwardProgressStandard</c>, so a milestone row reaches the ledger carrying
        /// zero rewards and is enriched in place moments later; a recalc inside that
        /// window reads pools that have not taken the award. Contract outcomes and
        /// StrategyActivate carry their real amounts at the door and are unaffected, as
        /// are the tech and facility rows (their flown lanes log no guard clamp at the action).
        /// </para>
        ///
        /// <para>
        /// With no frame host the answer is false: running the recalc inline is worse than
        /// correct but losing it entirely is worse still, and the seed guard
        /// (<see cref="HasUnenrichedMilestoneRow"/>) is the belt that covers that braces.
        /// </para>
        /// </summary>
        internal static bool ShouldDeferKscRecalcOneFrame(GameActionType type, bool hasFrameHost)
        {
            return hasFrameHost
                && (type == GameActionType.MilestoneAchievement
                    || ShouldDeferKscReconcileOneFrame(type, hasFrameHost));
        }

        /// <summary>
        /// True when the KSC door must also postpone the row's reconcile (and with it the
        /// recalc) to the frame host's next frame: the KerbalHire row. Decompiled
        /// <c>KerbalRoster.HireApplicant</c> raises <c>OnCrewmemberHired</c>, and stock
        /// Funding charges the hire from its own listener on that event, which runs after
        /// Parsek's. Inside the handler the live pool has not been charged and no
        /// FundsChanged(CrewRecruited) exists yet, so an inline recalc either clamps the
        /// drawdown or, with committed-future rows authorising it, patches the charge in
        /// and stock then charges it a second time (HIRE-RECALC-BEFORE-STOCK-DEBIT). Also the
        /// KerbalDismissal row, for a different reason: decompiled
        /// <c>KerbalRoster.SackAvailable</c> makes the kerbal an applicant FIRST and then
        /// raises <c>OnCrewmemberSacked</c>, so the roster is already right when the handler
        /// runs and an inline pass would only find him an applicant. The deferral keeps the
        /// reconcile and the recalc (its roster pass and the stock-screen refresh it raises)
        /// out of stock's <c>SackAvailable</c> call and its listener dispatch, after which the
        /// Astronaut Complex rebuilds its lists, the same frame shape as the hire.
        /// </summary>
        internal static bool ShouldDeferKscReconcileOneFrame(GameActionType type, bool hasFrameHost)
        {
            return (type == GameActionType.KerbalHire || type == GameActionType.KerbalDismissal)
                && hasFrameHost;
        }

        /// <summary>
        /// The row form of <see cref="ShouldDeferKscReconcileOneFrame(GameActionType, bool)"/>:
        /// also a part purchase or part upgrade row. Stock's <c>Funding</c> registers its
        /// <c>OnPartPurchased</c> and <c>OnPartUpgradePurchased</c> listeners in the same
        /// <c>OnAwake</c> as its hire listener, and <c>EventData.Fire</c> runs the listeners
        /// last-added first (KSP 1.12.5 decompiled), so Parsek's handler runs before the
        /// <c>RnDPartPurchase</c> charge exactly as it does before the hire's
        /// (todo PART-UPGRADE-PURCHASE-UNRESERVED).
        /// </summary>
        internal static bool ShouldDeferKscReconcileOneFrame(GameAction action, bool hasFrameHost)
        {
            if (action == null || !hasFrameHost) return false;
            if (ShouldDeferKscReconcileOneFrame(action.Type, hasFrameHost)) return true;
            return action.Type == GameActionType.FundsSpending
                && (action.FundsSpendingSource == FundsSpendingSource.Other
                    || action.FundsSpendingSource == FundsSpendingSource.PartUpgrade);
        }


        /// <summary>
        /// The QUERY-FAMILY strategy door (STRATEGY-SCIENCE-CONVERSION-LEAK /
        /// STRATEGY-FUNDS-YIELD-DRIFT). Called by
        /// <c>GameStateRecorder.OnCurrencyModified</c> with the legs
        /// <see cref="StrategyConversionCapture.EvaluateLegs"/> selected out of a stock
        /// <c>CurrencyModifierQuery</c> that a <c>CurrencyConverter</c> /
        /// <c>CurrencyOperation</c> effect mutated in place.
        ///
        /// <para>Writes DIRECT rows at once, even when a flight recorder is live - the one
        /// structural difference from <see cref="OnKscSpending"/>'s
        /// <c>ShouldForwardDirectLedgerEvent</c>-gated siblings: this family leaves NO
        /// reason-keyed GameStateEvent behind, so there is no stored event for the
        /// commit-time <c>ConvertEvents</c> path to convert later - deferring would
        /// simply lose it.</para>
        ///
        /// <para>The rows carry the recording tag the recorder gives the transaction they
        /// divert from (<see cref="GameStateRecorder.ResolveStrategyConversionRecordingTag"/>:
        /// the live recording, else the recovery-scope owner of a progress reward, else
        /// none at the Space Center). A converted share has no meaning apart from its
        /// award, so it follows the award through every timeline operation: an F9 retires
        /// the tagged rows after the quicksave with the award's event, a Re-Fly merge
        /// tombstones them with the award's row, a revert prunes them with the reverted
        /// flight, and a non-rewind discard re-homes them untagged exactly as it re-homes
        /// the award (<see cref="PreserveIrreversibleLiveGameplayOnDiscard"/>)
        /// (STRATEGY-CONVERSION-ROWS-OUTLIVE-THEIR-AWARD).</para>
        ///
        /// <para>REPUTATION CREDIT legs land as a NOMINAL
        /// <c>ReputationEarning</c> row (<c>ReputationSource.Strategy</c>). The query
        /// delta is PRE-curve, and that is exactly why the nominal arm is the right one:
        /// stock's <c>Reputation.OnCurrenciesModified</c> passes that very expression to
        /// <c>addReputation_granular</c>, and <c>ReputationModule.ApplyReputationCurve</c>
        /// mirrors that routine line by line, so the reconstruction re-derives the pool
        /// movement at its OWN running rep instead of copying a number measured against a
        /// different one. This is the OPPOSITE contract from the exchanger family, whose
        /// rep leg is read POST-curve off the <c>ReputationChanged</c> event and therefore
        /// bypasses the curve (<c>ConvertStrategyExchangeReputation</c> ->
        /// <c>ReputationModule.ProcessRepPenalty</c>'s no-recurve arm). Do not merge the
        /// two: each is faithful only through its own arm.</para>
        ///
        /// <para>REPUTATION DEBIT legs land as a NOMINAL <c>ReputationPenalty</c> row
        /// (<c>ReputationPenaltySource.StrategyConverter</c>) - the mirror image of the
        /// credit arm, and nominal for the identical reason. Decompiled
        /// <c>Reputation.AddReputation</c> applies the transaction's own amount and the
        /// converter's diversion as TWO separate <c>addReputation_granular</c> calls, so
        /// two nominal rows reproduce exactly what stock did. Do NOT route this through
        /// <c>ReputationPenaltySource.Strategy</c>: that member is the EXCHANGER family's
        /// post-curve capture and bypasses the curve.</para>
        ///
        /// <para><b>ORDERING CAVEAT on "reproduce exactly".</b> The curve is
        /// STATE-DEPENDENT, so the two rows only reproduce stock when the walk applies
        /// them in stock's order. That is GUARANTEED for an EARNING-type transaction:
        /// <c>RecalculationEngine.SortActions</c>' earnings-before-spendings secondary
        /// key puts the transaction's own credit ahead of the converter's debit at the
        /// shared UT, which is the order stock used. It is NOT guaranteed when BOTH rows
        /// are non-earning - a <c>ContractFail</c> penalty diverted by a reputation
        /// converter at the same UT - because the secondary key ties and the
        /// <c>Sequence</c> tertiary then applies the CONVERTER row first, the reverse of
        /// stock. The resulting error is bounded and small: ~1e-4 for a -10 nominal
        /// pair, an order of magnitude under the reputation guard's 0.01 epsilon, so it
        /// cannot clamp. Recorded as a KNOWN BOUNDED IMPRECISION in the walk's ordering,
        /// not a defect in this row shape - a fix would be a sort-key change, and one
        /// should not be made without a measurement showing the pair actually occurs
        /// live.</para>
        ///
        /// <para>NEGATIVE funds legs land as a nominal <c>FundsSpending</c> row sourced
        /// <see cref="FundsSpendingSource.StrategyConverter"/>, and nonzero-input funds legs
        /// of either sign reach this method only under a NOMINAL-channel reason (see
        /// <c>StrategyConversionCapture.IsNominalChannelFundsReason</c>) - the reasons
        /// where Parsek's funds row recorded a CONFIGURED GROSS amount (contract rewards,
        /// advances and penalties, milestones, part and upgrade purchases, repairs) and
        /// this row is the missing diverted half. Funds carry no curve, so the row is
        /// exact in any walk order.</para>
        /// </summary>
        internal static void OnStrategyCurrencyConversion(
            double ut,
            IReadOnlyList<StrategyConversionLeg> legs,
            string reason,
            string conversionCause = null)
        {
            if (legs == null || legs.Count == 0)
                return;

            Initialize();

            // The rows belong to whatever owns the transaction they divert from: the same
            // tag the recorder gives that transaction's own award event (STRATEGY-CONVERSION-
            // ROWS-OUTLIVE-THEIR-AWARD). Untagged, they outlived an award an F9, a Re-Fly
            // merge or a revert took back.
            string recordingId = GameStateRecorder.ResolveStrategyConversionRecordingTag(reason, ut);

            int written = 0, skipped = 0;
            for (int i = 0; i < legs.Count; i++)
            {
                var leg = legs[i];
                GameAction action = BuildStrategyConversionAction(ut, leg, reason, recordingId);
                if (action == null)
                {
                    skipped++;
                    continue;
                }
                // Design 3.15 step 7: the award this leg was converted from, when a scope named it
                // (a leg split between several cause rows names its own).
                string legCause = !string.IsNullOrEmpty(leg.Cause) ? leg.Cause : conversionCause;
                action.ConversionCause = string.IsNullOrEmpty(legCause) ? null : legCause;

                kscSequenceCounter++;
                action.Sequence = kscSequenceCounter;
                // Dated like the KSC door's rows (an untagged leg inside a Rewind-to-Launch
                // lead window moves to the held-through UT with the row that caused it, so
                // the leg still binds to its cause).
                LedgerHeldWindow.StampKscOriginRow(action, "OnStrategyCurrencyConversion");
                // Same contract as the OnKscSpending door: stamp only, never seed. See
                // StampLiveWriteRowAgainstSeed for the event-ordering reason.
                StampLiveWriteRowAgainstSeed(action, "strategy conversion " + action.Type);
                Ledger.AddAction(action, LedgerRowWriter.StockApplied);
                written++;

                ParsekLog.Info(Tag,
                    $"Strategy conversion recorded: type={action.Type}, " +
                    $"currency={leg.Currency}, delta={leg.Delta.ToString("R", CultureInfo.InvariantCulture)}, " +
                    $"UT={ut.ToString("F1", CultureInfo.InvariantCulture)}, reason={reason ?? "(none)"}, " +
                    $"recordingId={(string.IsNullOrEmpty(recordingId) ? "(none)" : recordingId)}");

                ReconcileKscAction(GameStateStore.Events, Ledger.Actions, action, ut);
            }

            ParsekLog.Verbose(Tag,
                $"OnStrategyCurrencyConversion: legs={legs.Count.ToString(CultureInfo.InvariantCulture)}, " +
                $"written={written.ToString(CultureInfo.InvariantCulture)}, " +
                $"skipped={skipped.ToString(CultureInfo.InvariantCulture)}, " +
                $"reason={reason ?? "(none)"}, " +
                $"recordingId={(string.IsNullOrEmpty(recordingId) ? "(none)" : recordingId)}");

            // THE ROWS ARE ALREADY IN THE LEDGER at this point, and that ordering is the
            // whole safety story: only the recalc moves. This handler runs from INSIDE the
            // CurrencyModifierQuery, before KSP has applied every leg to its pools, so a
            // recalc here patches against a live pool that is transiently missing the
            // output leg and the funds uplift guard clamps it down once. Defer one frame
            // (WarpToTimeConsumer.RunNextFrame, the repo's one-frame defer host) so the
            // query has finished applying; if the defer never runs, the rows still stand
            // and the next natural recalc picks them up.
            var dispatch = StrategyConversionCapture.DecideRecalcDispatch(
                written, WarpToTimeConsumer.Instance != null);
            ParsekLog.Verbose(Tag,
                "OnStrategyCurrencyConversion recalc dispatch=" + dispatch +
                " written=" + written.ToString(CultureInfo.InvariantCulture));

            if (dispatch == StrategyConversionRecalcDispatch.Deferred)
            {
                double deferredUt = ut;
                WarpToTimeConsumer.RunNextFrame(
                    () => RecalculateAndPatchForLiveTimelineEvent(deferredUt, "strategy-conversion"));
            }
            else if (dispatch == StrategyConversionRecalcDispatch.Immediate)
            {
                RecalculateAndPatchForLiveTimelineEvent(ut, "strategy-conversion");
            }
        }

        /// <summary>
        /// Live-only income (design 3.15 step 9): files the rows
        /// <see cref="LiveOnlyIncome.BuildRows"/> built for an earning stock applied under a
        /// reason no ledger channel converts, at the earning's own UT, untagged, as stock-applied
        /// rows, so the next update steps them instead of filing a pool adjustment at its own
        /// time. A time-travel load to before a row drops it
        /// (<see cref="LiveOnlyIncome.DropAfterTimeTravelLoad"/>). Recalculates one frame later,
        /// as the conversion door does: the query is still applying when this runs.
        /// </summary>
        internal static void OnLiveOnlyIncome(double ut, IList<GameAction> rows, string reason)
        {
            if (rows == null || rows.Count == 0)
                return;

            Initialize();
            for (int i = 0; i < rows.Count; i++)
            {
                GameAction action = rows[i];
                if (action == null)
                    continue;
                kscSequenceCounter++;
                action.Sequence = kscSequenceCounter;
                LedgerHeldWindow.StampKscOriginRow(action, "OnLiveOnlyIncome");
                StampLiveWriteRowAgainstSeed(action, "live-only income " + action.Type);
                Ledger.AddAction(action, LedgerRowWriter.StockApplied);
                ParsekLog.Info(LiveOnlyIncome.Tag,
                    $"Live-only income filed: type={action.Type} {LiveOnlyIncome.FormatAmount(action)} " +
                    $"reason={reason ?? "(none)"} ut={ut.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"id={action.ActionId} (kept on this timeline; a rewind to before it drops it)");
            }

            var dispatch = StrategyConversionCapture.DecideRecalcDispatch(
                rows.Count, WarpToTimeConsumer.Instance != null);
            if (dispatch == StrategyConversionRecalcDispatch.Deferred)
            {
                double deferredUt = ut;
                WarpToTimeConsumer.RunNextFrame(
                    () => RecalculateAndPatchForLiveTimelineEvent(deferredUt, "live-only-income"));
            }
            else if (dispatch == StrategyConversionRecalcDispatch.Immediate)
            {
                RecalculateAndPatchForLiveTimelineEvent(ut, "live-only-income");
            }
        }

        /// <summary>
        /// Pure row-shape mapper for <see cref="OnStrategyCurrencyConversion"/>. EVERY
        /// leg the scoping rule admits now returns a row - science either direction,
        /// funds either direction, reputation either direction. The one null is a funds
        /// leg of magnitude exactly zero, which
        /// <see cref="StrategyConversionCapture.EvaluateLegs"/>' MinCaptureMagnitude
        /// filter makes unreachable; it is logged rather than silently shaped into a
        /// zero-value row. Internal static for testability.
        /// </summary>
        internal static GameAction BuildStrategyConversionAction(
            double ut,
            StrategyConversionLeg leg,
            string reason,
            string recordingId = null)
        {
            string tag = string.IsNullOrEmpty(recordingId) ? null : recordingId;
            switch (leg.Currency)
            {
                case StrategyConversionCurrency.Science:
                    return BuildStrategyScienceConversionAction(ut, leg, tag);

                case StrategyConversionCurrency.Funds:
                    return BuildStrategyFundsConversionAction(ut, leg, reason, tag);

                case StrategyConversionCurrency.Reputation:
                default:
                    return BuildStrategyReputationConversionAction(ut, leg, reason, tag);
            }
        }

        /// <summary>
        /// The science arm of <see cref="BuildStrategyConversionAction"/>: a debit leg is a
        /// <c>StrategyScienceDebit</c> of the magnitude, anything else a <c>StrategyScienceCredit</c>.
        /// <paramref name="tag"/> is the row's recording id (null for an untagged row).
        /// </summary>
        internal static GameAction BuildStrategyScienceConversionAction(
            double ut,
            StrategyConversionLeg leg,
            string tag)
        {
            if (leg.Delta < 0.0)
            {
                return new GameAction
                {
                    UT = ut,
                    Type = GameActionType.StrategyScienceDebit,
                    RecordingId = tag,
                    Cost = (float)(-leg.Delta),
                    ConversionSource = StrategyConversionSource.Converter
                };
            }
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.StrategyScienceCredit,
                RecordingId = tag,
                ScienceAwarded = (float)leg.Delta
            };
        }

        /// <summary>
        /// The funds arm of <see cref="BuildStrategyConversionAction"/>: a debit leg is a
        /// <c>FundsSpending</c> (StrategyConverter source) of the magnitude, a zero leg is logged
        /// and returns null, and a credit leg is a <c>FundsEarning</c> (Strategy source).
        /// </summary>
        internal static GameAction BuildStrategyFundsConversionAction(
            double ut,
            StrategyConversionLeg leg,
            string reason,
            string tag)
        {
            if (leg.Delta < 0.0)
            {
                // THE DEBIT ARM. A funds-INPUT converter (Appreciation Campaign
                // funds -> reputation, Outsourced R&D funds -> science) or a
                // funds-DOWN CurrencyOperation (Leadership Initiative's
                // 1.00..0.25 multiplier on contract gains) diverting part of an
                // ordinary transaction out of the pool.
                //
                // REACHED ONLY UNDER A NOMINAL-CHANNEL REASON. The leg exists at
                // all only because StrategyConversionCapture.EvaluateLegs either
                // saw a zero funds input (no transaction of its own) or matched
                // StrategyConversionCapture.IsNominalChannelFundsReason - and on
                // those reasons the ordinary funds row recorded the CONFIGURED
                // GROSS amount (contract.FundsCompletion / FundsAdvance / the
                // penalties, the AwardProgress arguments, a part's entryCost, a
                // building's RepairCost), never an observed delta. So this row is
                // the missing second half of the pool move rather than a second
                // count of the first half; under the event-derived reasons
                // (VesselRollout, StructureConstruction, StrategyOutput) no leg is
                // emitted and no row is written.
                //
                // NO CURVE ANYWHERE, which is what makes this simpler than its
                // reputation sibling: Funding.AddFunds moves the pool by the raw
                // value both times (funds += value, then funds += effect delta),
                // so FundsModule.ProcessFundsSpending subtracting the magnitude
                // reproduces stock exactly, in any order, with no state
                // dependence at all.
                //
                // FundsSpent carries the POSITIVE magnitude - the sign is in the
                // action type - and the row carries the award's tag like its
                // siblings on this path.
                ParsekLog.Info(Tag,
                    $"Strategy conversion: funds DEBIT captured nominal=" +
                    $"{leg.Delta.ToString("R", CultureInfo.InvariantCulture)} at " +
                    $"UT={ut.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"(reason={reason ?? "(none)"}) - the ordinary channel carries " +
                    "this transaction's CONFIGURED GROSS amount, so this row is the " +
                    "diverted half of the pool movement");
                return new GameAction
                {
                    UT = ut,
                    Type = GameActionType.FundsSpending,
                    RecordingId = tag,
                    FundsSpent = (float)(-leg.Delta),
                    FundsSpendingSource = FundsSpendingSource.StrategyConverter
                };
            }
            if (leg.Delta == 0.0)
            {
                // Unreachable through EvaluateLegs (MinCaptureMagnitude filters
                // it), and a zero-magnitude row would be pure noise either way.
                ParsekLog.Warn(Tag,
                    $"Strategy conversion: zero funds delta at " +
                    $"UT={ut.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"(reason={reason ?? "(none)"}) - not captured");
                return null;
            }
            // THE CREDIT ARM, and it now serves TWO populations that share a row
            // shape. (1) The zero-input cross-currency YIELD a converter pays into
            // funds (Fundraising Campaign reputation -> funds, Patents Licensing
            // science -> funds): no transaction of its own, captured under the
            // historical zero-input rule. (2) A NONZERO-input positive delta under
            // a nominal-channel reason - stock's Leadership Initiative scales
            // MILESTONE funds gains 1.00..2.50 under Progression, where
            // MilestoneAchievement carries the AwardProgress argument and nothing
            // records the uplift. Both are FundsEarning/Strategy: the source names
            // the query-family door, and the two reconcile identically (the KSC
            // classifier skips every FundsEarning, and the post-walk reconciler
            // skips FundsEarningSource.Strategy explicitly), so no discriminator
            // is owed between them.
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsEarning,
                RecordingId = tag,
                FundsAwarded = (float)leg.Delta,
                FundsSource = FundsEarningSource.Strategy
            };
        }

        /// <summary>
        /// The reputation arm (and the default) of <see cref="BuildStrategyConversionAction"/>:
        /// a positive leg is a nominal <c>ReputationEarning</c> (Strategy source), anything else a
        /// nominal <c>ReputationPenalty</c> (StrategyConverter source) of the magnitude.
        /// </summary>
        internal static GameAction BuildStrategyReputationConversionAction(
            double ut,
            StrategyConversionLeg leg,
            string reason,
            string tag)
        {
            // THE CREDIT ARM. The query delta IS the curve's own input argument:
            // stock's Reputation.OnCurrenciesModified calls
            // addReputation_granular(query.GetEffectDelta(Currency.Reputation)),
            // and StrategyConversionQuery.DeltaReputation is read from that exact
            // expression on that exact event. So a NOMINAL ReputationEarning row
            // is the faithful shape: ReputationModule.ProcessRepEarning re-runs
            // ApplyReputationCurve - a line-by-line mirror of
            // addReputation_granular - at the RECONSTRUCTION's own running rep,
            // reproducing the pool movement rather than approximating it.
            // Corroborated by the live pair measured on run 2026-08-18_2140:
            // pre-curve 0.33540129661560059 -> pool move 0.33515101671218872,
            // which the curve reproduces to ~1e-7.
            //
            // NO DOUBLE-COUNT, and the reason is the MECHANISM rather than a
            // scoping rule. StrategyConversionCapture.EvaluateLegs emits a
            // reputation leg for ANY nonzero delta, input or not (it stopped
            // gating on InputReputation == 0 when the DEBIT arm was measured -
            // that gate excluded every stock reputation-INPUT converter). What
            // keeps this row safe is that stock applies the transaction's own
            // amount and the effect delta as TWO separate addReputation_granular
            // calls, and every ordinary channel (TransformedRepReward,
            // MilestoneRepAwarded, the reason-keyed exchanger door) records a
            // CONFIGURED NOMINAL - the first call - never an observed pool delta.
            // Pinned by StrategyConversionCaptureTests
            // .NonZeroInputReputation_IsCaptured_UnlikeFunds, which states the
            // reputation-vs-funds asymmetry as one cell. The invariant to
            // re-check before adding any reputation channel is that no channel
            // starts recording a POST-modifier amount.
            //
            // Tagged like its science / funds siblings on this path: it follows
            // the award it was converted from.
            if (leg.Delta > 0.0)
            {
                ParsekLog.Info(Tag,
                    $"Strategy conversion: reputation CREDIT captured pre-curve " +
                    $"nominal={leg.Delta.ToString("R", CultureInfo.InvariantCulture)} at " +
                    $"UT={ut.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"(reason={reason ?? "(none)"}) - ReputationModule applies KSP's " +
                    "granular curve to it at the reconstruction's own running rep");
                return new GameAction
                {
                    UT = ut,
                    Type = GameActionType.ReputationEarning,
                    RecordingId = tag,
                    NominalRep = (float)leg.Delta,
                    RepSource = ReputationSource.Strategy
                };
            }

            // THE DEBIT ARM. Symmetric with the credit arm above and nominal for
            // the identical reason: decompiled Reputation.AddReputation moves the
            // pool TWICE - addReputation_granular(input) first, then
            // addReputation_granular(GetEffectDelta(Reputation)) from
            // Reputation.OnCurrenciesModified against the already-moved pool - so
            // this leg is the curve's own input argument for the SECOND of those
            // two calls, and ReputationModule.ProcessRepPenalty's ordinary arm
            // re-derives it at the reconstruction's running rep.
            //
            // ReputationPenaltySource.StrategyConverter, NOT ...Strategy: that one
            // is the EXCHANGER family's post-curve capture and takes a no-recurve
            // shortcut in ProcessRepPenalty, which would apply this pre-curve
            // magnitude as if it were already effective. The two mechanisms share a
            // word and nothing else.
            //
            // NominalPenalty carries the POSITIVE magnitude - the sign is in the
            // action type - and the row is tagged like its siblings on this path.
            //
            // MEASURED live, model-free, on run
            // 2026-08-20_2052_L3-strategy-currency-conversion: Fundraising Campaign
            // at the stock default Factor 0.05 diverted 1.0000572204589844
            // reputation out of a 20-point ContractReward award (the same award
            // under the cfg-excluded VesselRecovery reason moved the pool
            // 19.999963760375977; under ContractReward it moved 18.999906539916992),
            // and before this arm existed no ledger row carried any of it.
            ParsekLog.Info(Tag,
                $"Strategy conversion: reputation DEBIT captured pre-curve " +
                $"nominal={leg.Delta.ToString("R", CultureInfo.InvariantCulture)} at " +
                $"UT={ut.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"(reason={reason ?? "(none)"}) - ReputationModule applies KSP's " +
                "granular curve to it at the reconstruction's own running rep");
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ReputationPenalty,
                RecordingId = tag,
                NominalPenalty = (float)(-leg.Delta),
                RepPenaltySource = ReputationPenaltySource.StrategyConverter
            };
        }

        /// <summary>
        /// Pure: true for every row shape <see cref="BuildStrategyConversionAction"/>
        /// writes. <c>FundsEarning</c> / <c>FundsEarningSource.Strategy</c> is shared on
        /// purpose with the exchanger family's funds output (Bail-Out Grant), so that row
        /// matches too; nothing on disk tells the two apart.
        /// </summary>
        internal static bool IsStrategyConversionRow(GameAction action)
        {
            if (action == null)
                return false;
            switch (action.Type)
            {
                case GameActionType.StrategyScienceDebit:
                    return action.ConversionSource == StrategyConversionSource.Converter;
                case GameActionType.StrategyScienceCredit:
                    return true;
                case GameActionType.FundsSpending:
                    return action.FundsSpendingSource == FundsSpendingSource.StrategyConverter;
                case GameActionType.FundsEarning:
                    return action.FundsSource == FundsEarningSource.Strategy;
                case GameActionType.ReputationEarning:
                    return action.RepSource == ReputationSource.Strategy;
                case GameActionType.ReputationPenalty:
                    return action.RepPenaltySource == ReputationPenaltySource.StrategyConverter;
                default:
                    return false;
            }
        }

        /// <summary>
        /// True for the irreversible live-gameplay terminal events that KSP applies the
        /// moment they happen and cannot be cleanly undone without a quicksave reload:
        /// contract terminal outcomes, world-record / progress milestone achievements, and
        /// a KSC building collapse (stock <c>DestructibleBuilding.AddDamage</c> saves the
        /// persistent game right after <c>Demolish()</c>, so a discard that reloads no
        /// quicksave leaves the building destroyed). These must survive a recording discard
        /// (see <see cref="PreserveIrreversibleLiveGameplayOnDiscard"/>).
        ///
        /// <para>Science EARNINGS are handled separately (they ride
        /// <c>PendingScienceSubjects</c>, not a GameStateEvent) - see step 2 of
        /// <see cref="PreserveIrreversibleLiveGameplayOnDiscard"/>. A science DEBIT from a
        /// stock strategy currency exchange is NOT covered by this type-only overload
        /// because <see cref="GameStateEventType.ScienceChanged"/> as a whole is not
        /// irreversible (most of its reasons are); the key-aware
        /// <see cref="IsIrreversibleLiveGameplayEvent(GameStateEvent)"/> overload picks
        /// out the one reason that is.</para>
        /// </summary>
        internal static bool IsIrreversibleLiveGameplayEvent(GameStateEventType type)
        {
            return type == GameStateEventType.ContractCompleted
                // A parameter's reward is paid into the pools the moment it completes.
                || type == GameStateEventType.ContractParameterCompleted
                || type == GameStateEventType.ContractFailed
                || type == GameStateEventType.ContractCancelled
                || type == GameStateEventType.MilestoneAchieved
                || type == GameStateEventType.BuildingDestroyed
                // Lab science is banked in the R&D pool the moment the lab pays it out.
                || type == GameStateEventType.ScienceLabCredited;
        }

        /// <summary>
        /// Key-aware form of <see cref="IsIrreversibleLiveGameplayEvent(GameStateEventType)"/>,
        /// used by <see cref="PreserveIrreversibleLiveGameplayOnDiscard"/>. Adds two
        /// events beyond the type-only set. The first is a NEGATIVE
        /// <see cref="GameStateEventType.ScienceChanged"/> keyed
        /// <c>TransactionReasons.StrategyInput</c> - the science INPUT leg of a stock
        /// CurrencyExchanger (Patents Licensing; STRATEGY-SCIENCE-CONVERSION-LEAK).
        ///
        /// <para>Without this the leak re-opens through the DISCARD door: a flight-tagged
        /// exchange's debit is only converted at commit, so discarding the recording on a
        /// NON-rewind path (which has no quicksave to roll KSP back) drops the debit while
        /// KSP keeps the science removed - the ledger runs high by the traded amount
        /// exactly as it did before the fix. Re-homing it as an untagged
        /// <see cref="GameActionType.StrategyScienceDebit"/> preserves what KSP already
        /// applied, the same way a completed contract's terminal row is re-homed.</para>
        ///
        /// <para>Sign gate: only the debit direction is preserved. A non-negative
        /// StrategyInput delta is not something KSP took away, and
        /// <c>ConvertStrategyExchangeScience</c> returns null for it anyway.</para>
        ///
        /// <para>The second is a FREE <see cref="GameStateEventType.TechResearched"/> (detail field
        /// <c>free=1</c>, read by exact key like the converter reads details; todo
        /// FREE-TECH-NODE-LOST-AFTER-REWIND): stock's <c>UnlockProtoTechNode</c> (an anomaly's
        /// tech gift) unlocks the node in flight and KSP keeps it after a non-rewind discard,
        /// so the discard re-homes it as an untagged zero-cost ScienceSpending row; without
        /// it the next tech patch re-locks the node. A PAID TechResearched stays out: research
        /// happens at the Space Center, written untagged at once, never on a flight.</para>
        /// </summary>
        internal static bool IsIrreversibleLiveGameplayEvent(GameStateEvent e)
        {
            if (IsIrreversibleLiveGameplayEvent(e.eventType))
                return true;

            if (IsFreeTechUnlockEvent(e))
                return true;

            return e.eventType == GameStateEventType.ScienceChanged
                && string.Equals(
                    e.key,
                    GameStateEventConverter.StrategyInputReasonKey,
                    StringComparison.Ordinal)
                && e.valueAfter < e.valueBefore;
        }

        /// <summary>
        /// True for a TechResearched event the free-unlock capture filed: its detail carries
        /// the field <c>free=1</c> (exact key match through
        /// <see cref="GameStateEventDisplay.ExtractDetailField"/>, so <c>free=1</c> inside
        /// another field's value does not count).
        /// </summary>
        internal static bool IsFreeTechUnlockEvent(GameStateEvent e)
        {
            return e.eventType == GameStateEventType.TechResearched
                && string.Equals(
                    GameStateEventDisplay.ExtractDetailField(e.detail, GameStateRecorder.FreeTechUnlockDetailKey),
                    "1",
                    StringComparison.Ordinal);
        }

        /// <summary>
        /// Preserves irreversible live-gameplay economy when a recording is DISCARDED on a
        /// NON-rewind path (which has no quicksave to roll KSP back). The player flew live,
        /// so KSP already applied these irreversibly; they must survive into the ledger or
        /// the ledger diverges from KSP — re-listing a completed contract as active
        /// (double-listing in the in-progress tab + a re-completable duplicate reward + a
        /// silent fund drop), or un-crediting collected science / achieved milestones.
        ///
        /// Re-homes the discarded recordings' tagged irreversible terminal events
        /// (ContractCompleted/ParameterCompleted/Failed/Cancelled, MilestoneAchieved,
        /// BuildingDestroyed, ScienceLabCredited, a stock strategy currency exchange's
        /// ScienceChanged(StrategyInput) debit, and a free TechResearched unlock as a
        /// zero-cost ScienceSpending row) and their collected
        /// science subjects into DIRECT ledger actions (recordingId cleared), so the ledger
        /// keeps what KSP applied while the discard still drops the ghost / trajectory. The
        /// contract / milestone reward (funds / rep / science) rides the re-homed action
        /// itself (ConvertContractCompleted / ConvertMilestoneAchieved read it from the event
        /// detail), so one re-homed row preserves both the lifecycle state and the reward —
        /// no separate FundsChanged / ReputationChanged re-home is needed (that would
        /// double-count). The
        /// caller purges the now-redundant tagged store events afterward (the two purging
        /// cores) or leaves them orphaned (AutoDiscardActiveTreeCore — economy-safe: they
        /// are not recalc inputs and ConvertEvents' cross-recording scope-skip prevents a
        /// different recording's commit from re-crediting them). The direct ledger actions
        /// are the truth either way. NO recalc here: the re-homed actions are durable in the
        /// ledger and are applied to KSP by the caller's post-discard recalc, or — for the
        /// scoped switch-segment dispositions that defer it — by the next natural recalc
        /// (scene switch / commit / load). That deferral is safe: KSP's live state already
        /// reflects what the player did, and whenever a recalc runs the ledger (now holding
        /// the completion) keeps it consistent. The re-homed actions are stamped at PAST UTs,
        /// so they rely on the no-future-actions full-replay branch of
        /// <see cref="RecalculateAndPatchForCurrentTimelineIfFutureActions"/> to be patched in.
        ///
        /// Re-fly / rewind discards must NOT call this: they revert KSP via a quicksave
        /// reload, so preserving would diverge the other way. The wired non-rewind cores
        /// only ever run a genuine non-rewind discard (re-fly-scoped discards are handled
        /// earlier by <c>TryDiscardActiveReFlyAttempt</c>), and the discarded id set is only
        /// ever the discarded tree's own ids.
        ///
        /// Internal static for testability.
        /// </summary>
        internal static void PreserveIrreversibleLiveGameplayOnDiscard(
            ICollection<string> discardedRecordingIds, string reason)
        {
            if (discardedRecordingIds == null || discardedRecordingIds.Count == 0)
                return;

            Initialize();

            var idSet = discardedRecordingIds as HashSet<string>
                ?? new HashSet<string>(discardedRecordingIds);

            // A discarded recording's pending lab recovery no longer vetoes any idle check.
            InFlightRecoveryRequest.ClearLabRecoveryMarks(idSet, "discard: " + reason);

            // 1. Tagged irreversible terminal events from the discarded recordings ->
            //    direct ledger actions (ConvertEvent with recordingId=null clears the tag).
            var rehomed = new List<GameAction>();
            CollectDiscardedIrreversibleEventActions(idSet, rehomed,
                out int contractEvents, out int milestoneEvents, out int strategyExchangeEvents,
                out int buildingEvents, out int labEvents, out int freeTechEvents);

            // 2. Collected science subjects from the discarded recordings -> direct
            //    ScienceEarning actions (cleared recordingId + null owner => untagged-in-
            //    window conversion). Science is irreversible too (banked in the R&D archive).
            List<GameAction> scienceActions = ConvertDiscardedScienceSubjects(
                idSet, rehomed, out int scienceSubjectCount);

            // 2b. Strategy conversion rows the discarded flights wrote live, tagged to them.
            //     They are already ledger rows, and KSP applied them as irreversibly as the
            //     awards they were converted from, so they stay; only the tag naming a
            //     recording this discard deletes goes, the same shape as the re-homed awards.
            int conversionRowsUntagged = ClearStrategyConversionTagsForDiscard(idSet, reason);

            if (rehomed.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"PreserveIrreversibleLiveGameplayOnDiscard ({reason}): nothing to re-home " +
                    $"for {idSet.Count.ToString(CultureInfo.InvariantCulture)} discarded id(s) " +
                    $"(strategyConversionRowsUntagged={conversionRowsUntagged.ToString(CultureInfo.InvariantCulture)})");
                return;
            }

            // 3. Dedup against the ledger, append, mirror the committed-science cache.
            int deduped = AppendRehomedDirectActions(rehomed, scienceActions != null);

            // Scoped-remove the re-homed science subjects from the static pending list so
            // the discard's own clear/teardown does not leave them stale or re-process them.
            // Same scoped contract the discard cores use directly (see
            // RemovePendingScienceSubjectsForRecordings): a DIFFERENT live recording's
            // still-uncommitted science stays pending.
            int subjectsRemoved = RemovePendingScienceSubjectsForRecordings(idSet, reason);

            ParsekLog.Info(Tag,
                $"PreserveIrreversibleLiveGameplayOnDiscard ({reason}): re-homed " +
                $"{(rehomed.Count - deduped).ToString(CultureInfo.InvariantCulture)} direct action(s) " +
                $"(contract={contractEvents.ToString(CultureInfo.InvariantCulture)}, " +
                $"milestone={milestoneEvents.ToString(CultureInfo.InvariantCulture)}, " +
                $"strategyExchange={strategyExchangeEvents.ToString(CultureInfo.InvariantCulture)}, " +
                $"building={buildingEvents.ToString(CultureInfo.InvariantCulture)}, " +
                $"lab={labEvents.ToString(CultureInfo.InvariantCulture)}, " +
                $"freeTech={freeTechEvents.ToString(CultureInfo.InvariantCulture)}, " +
                $"science={scienceSubjectCount.ToString(CultureInfo.InvariantCulture)}, " +
                $"deduped={deduped.ToString(CultureInfo.InvariantCulture)}, " +
                $"subjectsRemoved={subjectsRemoved.ToString(CultureInfo.InvariantCulture)}, " +
                $"strategyConversionRowsUntagged={conversionRowsUntagged.ToString(CultureInfo.InvariantCulture)}) " +
                $"for {idSet.Count.ToString(CultureInfo.InvariantCulture)} discarded id(s)");
        }

        /// <summary>
        /// Step 1 of <see cref="PreserveIrreversibleLiveGameplayOnDiscard"/>: converts each
        /// store event tagged to a discarded, never-committed id that is irreversible
        /// (<see cref="IsIrreversibleLiveGameplayEvent(GameStateEvent)"/>) into a direct row
        /// (recording tag cleared) appended to <paramref name="rehomed"/>, counting the rows
        /// per event kind. An event that converts to no row is skipped uncounted.
        /// </summary>
        internal static void CollectDiscardedIrreversibleEventActions(
            HashSet<string> idSet, List<GameAction> rehomed,
            out int contractEvents, out int milestoneEvents, out int strategyExchangeEvents,
            out int buildingEvents, out int labEvents, out int freeTechEvents)
        {
            contractEvents = 0; milestoneEvents = 0; strategyExchangeEvents = 0; buildingEvents = 0; labEvents = 0; freeTechEvents = 0;
            var storeEvents = GameStateStore.Events;
            for (int i = 0; i < storeEvents.Count; i++)
            {
                var e = storeEvents[i];
                if (string.IsNullOrEmpty(e.recordingId) || !idSet.Contains(e.recordingId))
                    continue;
                // Never re-home committed history: a committed recording's terminal events
                // are already ledger actions. (DiscardPendingTree pre-excludes committed
                // ids; AutoDiscardActiveTreeCore's committed-restore-clone callers can pass
                // committed ids, so guard here too — dedup would also catch it, but this
                // keeps committed history out of the re-home input entirely.)
                if (RecordingStore.IsCommittedRecordingId(e.recordingId))
                    continue;
                if (!IsIrreversibleLiveGameplayEvent(e))
                    continue;

                var action = GameStateEventConverter.ConvertEvent(e, recordingId: null);
                if (action == null)
                    continue;
                rehomed.Add(action);
                if (e.eventType == GameStateEventType.MilestoneAchieved) milestoneEvents++;
                else if (e.eventType == GameStateEventType.ScienceChanged) strategyExchangeEvents++;
                else if (e.eventType == GameStateEventType.BuildingDestroyed) buildingEvents++;
                else if (e.eventType == GameStateEventType.ScienceLabCredited) labEvents++;
                else if (e.eventType == GameStateEventType.TechResearched) freeTechEvents++;
                else contractEvents++;
            }
        }

        /// <summary>
        /// Step 2 of <see cref="PreserveIrreversibleLiveGameplayOnDiscard"/>: converts the
        /// pending science subjects tagged to a discarded, never-committed id (copies with the
        /// tag cleared) into direct ScienceEarning rows over their capture span widened by one
        /// second each side, appends them to <paramref name="rehomed"/> and returns them; null
        /// when no subject matched. <paramref name="scienceSubjectCount"/> counts the matched
        /// subjects. The pending list itself is left unchanged.
        /// </summary>
        internal static List<GameAction> ConvertDiscardedScienceSubjects(
            HashSet<string> idSet, List<GameAction> rehomed, out int scienceSubjectCount)
        {
            scienceSubjectCount = 0;
            double minUT = double.MaxValue, maxUT = double.MinValue;
            var clearedSubjects = new List<PendingScienceSubject>();
            var pending = GameStateRecorder.PendingScienceSubjects;
            for (int i = 0; i < pending.Count; i++)
            {
                var s = pending[i];
                if (string.IsNullOrEmpty(s.recordingId) || !idSet.Contains(s.recordingId))
                    continue;
                if (RecordingStore.IsCommittedRecordingId(s.recordingId))
                    continue; // committed science is already a ledger action
                var cleared = s;
                cleared.recordingId = "";
                clearedSubjects.Add(cleared);
                scienceSubjectCount++;
                if (s.captureUT < minUT) minUT = s.captureUT;
                if (s.captureUT > maxUT) maxUT = s.captureUT;
            }
            List<GameAction> scienceActions = null;
            if (clearedSubjects.Count > 0)
            {
                scienceActions = GameStateEventConverter.ConvertScienceSubjects(
                    clearedSubjects, recordingId: null, minUT - 1.0, maxUT + 1.0);
                rehomed.AddRange(scienceActions);
            }
            return scienceActions;
        }

        /// <summary>
        /// Step 3 of the discard re-home: dedups <paramref name="rehomed"/> against the
        /// ledger (defensive: a re-homed event already present is credited once), assigns
        /// KSC sequence numbers, appends the survivors as direct rows and, when
        /// <paramref name="hasScience"/>, mirrors the committed-science cache for every
        /// surviving re-homed ScienceEarning so the direct science is treated as committed
        /// (subjectId-deduped, max-not-additive). NO recalc: the caller's post-discard
        /// recalc credits these. Returns the number dropped by the dedup.
        /// </summary>
        private static int AppendRehomedDirectActions(List<GameAction> rehomed, bool hasScience)
        {
            int beforeDedup = rehomed.Count;
            List<GameAction> kept = DeduplicateAgainstLedger(rehomed);
            for (int i = 0; i < kept.Count; i++)
                kept[i].Sequence = AllocateKscSequence();
            Ledger.AddActions(kept, LedgerRowWriter.StockApplied);

            if (hasScience)
            {
                var survivingScience = new List<GameAction>();
                for (int i = 0; i < kept.Count; i++)
                    if (kept[i].Type == GameActionType.ScienceEarning)
                        survivingScience.Add(kept[i]);
                if (survivingScience.Count > 0)
                    GameStateStore.CommitScienceActions(survivingScience);
            }
            return beforeDedup - kept.Count;
        }

        /// <summary>
        /// The committed-tree restore attempt's half of
        /// <see cref="PreserveIrreversibleLiveGameplayOnDiscard"/>. While a committed tree is
        /// resumed as a clone, new game-state events and science subjects are tagged with the
        /// COMMITTED recording ids, which the discard re-home skips as committed history. The
        /// ones after the attempt's per-recording cutoff (<paramref name="attemptEventCutoffs"/>,
        /// the recording's end UT at arm time, the same split the attempt's save suppression
        /// and its tail purge use) are the clone's own. When a LIVE discard drops the attempt,
        /// KSP keeps what the player did in it (a finished contract, a milestone, banked
        /// science), so its irreversible events (<see cref="IsIrreversibleLiveGameplayEvent(GameStateEvent)"/>)
        /// and its science subjects become direct ledger rows here, BEFORE the tail purge
        /// removes them; the re-homed science subjects leave the pending list. Committed
        /// history (at or before a cutoff) is never touched. Rollback paths (a rewind, a
        /// load) must not call this: their reload reverts KSP, so a re-homed row would credit
        /// what KSP no longer has. Internal static for testability.
        /// </summary>
        internal static void PreserveIrreversibleRestoreAttemptTailsOnDiscard(
            IReadOnlyDictionary<string, double> attemptEventCutoffs, string reason)
        {
            if (attemptEventCutoffs == null || attemptEventCutoffs.Count == 0)
                return;

            Initialize();

            var rehomed = new List<GameAction>();
            int contractEvents = 0, milestoneEvents = 0, otherEvents = 0;
            var storeEvents = GameStateStore.Events;
            for (int i = 0; i < storeEvents.Count; i++)
            {
                var e = storeEvents[i];
                if (string.IsNullOrEmpty(e.recordingId)
                    || !attemptEventCutoffs.TryGetValue(e.recordingId, out double cutoffUT)
                    || !(e.ut > cutoffUT))
                {
                    continue;
                }
                if (!IsIrreversibleLiveGameplayEvent(e))
                    continue;

                var action = GameStateEventConverter.ConvertEvent(e, recordingId: null);
                if (action == null)
                    continue;
                rehomed.Add(action);
                if (e.eventType == GameStateEventType.MilestoneAchieved) milestoneEvents++;
                else if (e.eventType == GameStateEventType.ContractCompleted
                    || e.eventType == GameStateEventType.ContractParameterCompleted
                    || e.eventType == GameStateEventType.ContractFailed
                    || e.eventType == GameStateEventType.ContractCancelled) contractEvents++;
                else otherEvents++;
            }

            double minUT = double.MaxValue, maxUT = double.MinValue;
            var clearedSubjects = new List<PendingScienceSubject>();
            var pending = GameStateRecorder.PendingScienceSubjects;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var s = pending[i];
                if (string.IsNullOrEmpty(s.recordingId)
                    || !attemptEventCutoffs.TryGetValue(s.recordingId, out double cutoffUT)
                    || !(s.captureUT > cutoffUT))
                {
                    continue;
                }
                var cleared = s;
                cleared.recordingId = "";
                clearedSubjects.Add(cleared);
                pending.RemoveAt(i);
                if (s.captureUT < minUT) minUT = s.captureUT;
                if (s.captureUT > maxUT) maxUT = s.captureUT;
            }
            List<GameAction> scienceActions = null;
            if (clearedSubjects.Count > 0)
            {
                clearedSubjects.Reverse();
                scienceActions = GameStateEventConverter.ConvertScienceSubjects(
                    clearedSubjects, recordingId: null, minUT - 1.0, maxUT + 1.0);
                rehomed.AddRange(scienceActions);
            }

            if (rehomed.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"PreserveIrreversibleRestoreAttemptTailsOnDiscard ({reason}): nothing to re-home " +
                    $"after the cutoffs of {attemptEventCutoffs.Count.ToString(CultureInfo.InvariantCulture)} " +
                    "committed recording(s)");
                return;
            }

            int deduped = AppendRehomedDirectActions(rehomed, scienceActions != null);
            ParsekLog.Info(Tag,
                $"PreserveIrreversibleRestoreAttemptTailsOnDiscard ({reason}): re-homed " +
                $"{(rehomed.Count - deduped).ToString(CultureInfo.InvariantCulture)} direct action(s) " +
                $"from the discarded restore attempt (contract={contractEvents.ToString(CultureInfo.InvariantCulture)}, " +
                $"milestone={milestoneEvents.ToString(CultureInfo.InvariantCulture)}, " +
                $"other={otherEvents.ToString(CultureInfo.InvariantCulture)}, " +
                $"science={clearedSubjects.Count.ToString(CultureInfo.InvariantCulture)}, " +
                $"deduped={deduped.ToString(CultureInfo.InvariantCulture)}) " +
                $"for {attemptEventCutoffs.Count.ToString(CultureInfo.InvariantCulture)} committed recording(s)");
        }

        /// <summary>
        /// Clears the recording tag on every strategy conversion row
        /// (<see cref="IsStrategyConversionRow"/>) tagged to a discarded, never-committed
        /// recording, keeping the row. Committed ids are skipped for the same reason
        /// <see cref="PreserveIrreversibleLiveGameplayOnDiscard"/> skips their events: their
        /// rows are committed history. Returns the number untagged.
        /// </summary>
        internal static int ClearStrategyConversionTagsForDiscard(
            ICollection<string> discardedRecordingIds, string reason)
        {
            if (discardedRecordingIds == null || discardedRecordingIds.Count == 0)
                return 0;

            int cleared = 0;
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a == null || string.IsNullOrEmpty(a.RecordingId))
                    continue;
                if (!discardedRecordingIds.Contains(a.RecordingId))
                    continue;
                if (!IsStrategyConversionRow(a))
                    continue;
                if (RecordingStore.IsCommittedRecordingId(a.RecordingId))
                    continue;
                a.RecordingId = null;
                cleared++;
            }

            if (cleared > 0)
            {
                Ledger.BumpStateVersion();
                ParsekLog.Info(Tag,
                    $"ClearStrategyConversionTagsForDiscard ({reason}): untagged " +
                    $"{cleared.ToString(CultureInfo.InvariantCulture)} strategy conversion row(s) " +
                    "of discarded recordings - rows kept, like the awards they were converted from");
            }
            return cleared;
        }

        /// <summary>
        /// Pure: whether a stock revert drops <paramref name="action"/> as part of the
        /// reverted flight. Untagged rows are <see cref="Ledger.PruneOrphanActionsAfterUT"/>'s;
        /// this covers the strategy conversion rows the flight wrote live tagged to its own
        /// never-committed recordings, which the revert discards with their awards.
        /// Same UT boundary as the orphan prune.
        /// </summary>
        internal static bool IsRevertedFlightStrategyConversionRow(
            GameAction action, double cutoffUT, bool inclusive, Func<string, bool> isCommittedRecordingId)
        {
            if (action == null || string.IsNullOrEmpty(action.RecordingId))
                return false;
            if (!IsStrategyConversionRow(action))
                return false;
            if (isCommittedRecordingId != null && isCommittedRecordingId(action.RecordingId))
                return false;
            return inclusive ? action.UT >= cutoffUT : action.UT > cutoffUT;
        }

        /// <summary>
        /// Revert-path companion of <see cref="Ledger.PruneOrphanActionsAfterUT"/>: removes the
        /// strategy conversion rows of the reverted flight
        /// (<see cref="IsRevertedFlightStrategyConversionRow"/>). Returns the number removed.
        /// </summary>
        internal static int PruneRevertedFlightStrategyConversionRows(double cutoffUT, bool inclusive)
        {
            int removed = 0;
            var actions = Ledger.Actions;
            for (int i = actions.Count - 1; i >= 0; i--)
            {
                if (!IsRevertedFlightStrategyConversionRow(
                        actions[i], cutoffUT, inclusive, RecordingStore.IsCommittedRecordingId))
                {
                    continue;
                }
                Ledger.RemoveActionAt(i);
                removed++;
            }
            if (removed > 0)
            {
                ParsekLog.Info(Tag,
                    $"Revert: pruned {removed.ToString(CultureInfo.InvariantCulture)} strategy conversion " +
                    $"row(s) of the reverted flight {(inclusive ? "at/after" : "after")} UT " +
                    $"{cutoffUT.ToString("R", CultureInfo.InvariantCulture)}");
            }
            return removed;
        }

        /// <summary>
        /// Scoped removal of pending science subjects tagged with any id in
        /// <paramref name="recordingIds"/> from the static
        /// <see cref="GameStateRecorder.PendingScienceSubjects"/> list. UNLIKE a blanket
        /// <c>PendingScienceSubjects.Clear()</c>, this leaves UNRELATED subjects pending —
        /// a DIFFERENT live recording's still-uncommitted science, and untagged KSC captures.
        /// A blanket clear in a non-rewind discard core silently dropped another recording's
        /// uncommitted science. This mirrors the scoped commit contract
        /// (<see cref="NotifyLedgerTreeCommitted"/>'s "preserved unrelated
        /// PendingScienceSubjects") and the scoped removal inside
        /// <see cref="PreserveIrreversibleLiveGameplayOnDiscard"/>. Committed-id subjects are
        /// skipped (committed science is already a ledger action). Internal static for
        /// direct testability.
        /// </summary>
        /// <returns>The number of subjects removed.</returns>
        internal static int RemovePendingScienceSubjectsForRecordings(
            ICollection<string> recordingIds, string reason)
        {
            if (recordingIds == null || recordingIds.Count == 0)
                return 0;

            var idSet = recordingIds as HashSet<string> ?? new HashSet<string>(recordingIds);
            var pending = GameStateRecorder.PendingScienceSubjects;
            int removed = 0;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                string rid = pending[i].recordingId ?? "";
                if (string.IsNullOrEmpty(rid) || !idSet.Contains(rid))
                    continue;
                if (RecordingStore.IsCommittedRecordingId(rid))
                    continue; // committed science is already a ledger action
                pending.RemoveAt(i);
                removed++;
            }

            if (removed > 0)
                ParsekLog.Verbose(Tag,
                    $"RemovePendingScienceSubjectsForRecordings ({reason}): removed " +
                    $"{removed.ToString(CultureInfo.InvariantCulture)} scoped pending subject(s), " +
                    $"{pending.Count.ToString(CultureInfo.InvariantCulture)} unrelated retained");
            return removed;
        }

        /// <summary>
        /// Captures unowned stock science payouts directly into the ledger. Science
        /// collected while a recording is live stays in <see cref="GameStateRecorder.PendingScienceSubjects"/>
        /// and flows through <see cref="OnRecordingCommitted"/>; this path is for KSC /
        /// pre-recording callbacks where no future recording commit can own the subject.
        /// </summary>
        /// <param name="launchGuid">
        /// KERBAL-XP-RECOVERY-PICK-IS-NAME-AND-UT-ONLY stage 1: the recovering vessel's live
        /// launch guid, when the caller's <c>ProtoVessel</c> supplies one. Null (the default,
        /// and what every non-recovery caller passes) makes the recovery candidate filter
        /// inert, preserving the historical name+UT pick.
        /// </param>
        /// <returns>
        /// True when the subject was handled by the direct path, including duplicate
        /// suppression. False means the caller should keep the subject pending.
        /// </returns>
        internal static bool TryRecordKscScienceSubject(
            PendingScienceSubject subject,
            string vesselName,
            string launchGuid = null,
            uint vesselPersistentId = 0)
        {
            Initialize();

            if (string.IsNullOrEmpty(subject.subjectId))
            {
                ParsekLog.Warn(Tag,
                    "TryRecordKscScienceSubject: empty subjectId — retaining pending subject");
                return false;
            }

            if (subject.science <= 0f)
            {
                ParsekLog.Verbose(Tag,
                    $"TryRecordKscScienceSubject: non-positive science for subject='{subject.subjectId}' " +
                    $"science={subject.science.ToString("R", CultureInfo.InvariantCulture)} — retaining pending subject");
                return false;
            }

            string recordingId = ResolveKscScienceRecordingId(
                subject, vesselName, launchGuid, vesselPersistentId);
            var routedSubject = subject;
            routedSubject.recordingId = recordingId ?? "";

            var scienceActions = GameStateEventConverter.ConvertScienceSubjects(
                new[] { routedSubject },
                recordingId,
                subject.captureUT,
                subject.captureUT);

            if (scienceActions.Count == 0)
            {
                ParsekLog.Warn(Tag,
                    $"TryRecordKscScienceSubject: converter produced no action for subject='{subject.subjectId}' " +
                    $"ut={subject.captureUT.ToString("F1", CultureInfo.InvariantCulture)} — retaining pending subject");
                return false;
            }

            int beforeDedup = scienceActions.Count;
            scienceActions = DeduplicateAgainstLedger(scienceActions);
            if (scienceActions.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"TryRecordKscScienceSubject: duplicate direct ScienceEarning suppressed for " +
                    $"subject='{subject.subjectId}' ut={subject.captureUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"deduped={beforeDedup}");
                return true;
            }

            for (int i = 0; i < scienceActions.Count; i++)
                scienceActions[i].Sequence = AllocateKscSequence();

            Ledger.AddActions(scienceActions, LedgerRowWriter.StockApplied);
            GameStateStore.CommitScienceActions(scienceActions);

            // Verbose: paired Info "Science subject captured" already fires per subject; this row carries the direct-path detail without amplifying multi-subject recoveries.
            ParsekLog.Verbose(Tag,
                $"KSC science recorded: subject='{subject.subjectId}' " +
                $"science={subject.science.ToString("R", CultureInfo.InvariantCulture)} " +
                $"method={scienceActions[0].Method} ut={subject.captureUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"recordingId={recordingId ?? "(none)"} vessel='{vesselName ?? ""}'");

            RecalculateAndPatchForLiveTimelineEvent(subject.captureUT, "ksc-science");
            return true;
        }

        private static string ResolveKscScienceRecordingId(
            PendingScienceSubject subject,
            string vesselName,
            string launchGuid = null,
            uint vesselPersistentId = 0)
        {
            // Operator ruling 2026-09-26: deployed-experiment science is always untagged.
            // Stock submits it with xmitScalar 1, which reads as VesselRecovery, so without
            // this guard a named vessel would send it through the recovery picker.
            if (GameStateRecorder.IsDeployedScienceSubjectId(subject.subjectId))
            {
                ParsekLog.Verbose(Tag,
                    $"ResolveKscScienceRecordingId: deployed-experiment subject='{subject.subjectId}' " +
                    "- untagged by ruling, recovery picker skipped");
                return null;
            }

            if (!string.Equals(
                    subject.reasonKey ?? "",
                    VesselRecoveryReasonKey,
                    StringComparison.Ordinal))
            {
                return null;
            }

            if (string.IsNullOrEmpty(vesselName))
            {
                ParsekLog.Verbose(Tag,
                    $"ResolveKscScienceRecordingId: recovery science subject='{subject.subjectId}' " +
                    "has no vessel name — using null recording owner");
                return null;
            }

            return PickRecoveryRecordingId(
                RecoveredVesselIdentity.FromRawName(vesselName, launchGuid, vesselPersistentId),
                subject.captureUT);
        }

        /// <summary>
        /// #445: Called from <see cref="GameStateRecorder.OnFundsChanged"/> when KSP fires a
        /// <c>TransactionReasons.VesselRollout</c> deduction (player launches a vessel from
        /// VAB/SPH onto the launchpad/runway). Synthesizes a
        /// <see cref="GameActionType.FundsSpending"/> with
        /// <see cref="FundsSpendingSource.VesselBuild"/> at the moment of the deduction so
        /// the ledger tracks the cost even when the player cancels the rollout without ever
        /// starting a recording. The <see cref="GameAction.DedupKey"/> is set to a
        /// <c>"rollout:"</c>-prefixed context key so a subsequent recording from the same
        /// vessel/site can claim the action via <see cref="TryAdoptRolloutAction"/>
        /// instead of double-charging.
        /// </summary>
        /// <param name="ut">Universal time of the rollout transaction.</param>
        /// <param name="cost">Positive funds amount KSP deducted (rollout cost).</param>
        internal static void OnVesselRolloutSpending(double ut, double cost)
        {
            var context = LedgerRolloutAdoption.ResolveCurrentRolloutAdoptionContext();

            Initialize();

            LedgerRolloutAdoption.RecordVesselRolloutSpending(
                ut,
                cost,
                context,
                AllocateKscSequence,
                (action, actionUt) => ReconcileKscAction(GameStateStore.Events, Ledger.Actions, action, actionUt),
                () => RecalculateAndPatchForLiveTimelineEvent(ut, "vessel-rollout"));
        }

        /// <summary>
        /// Test seam for rollout adoption context. Production calls the two-argument
        /// overload, which resolves the current live vessel/site/launch guid from KSP state.
        /// </summary>
        internal static void OnVesselRolloutSpending(
            double ut,
            double cost,
            uint vesselPersistentId,
            string vesselName,
            string launchSiteName,
            string vesselGuid = null)
        {
            var context = LedgerRolloutAdoption.CreateRolloutAdoptionContext(
                vesselPersistentId,
                vesselName,
                launchSiteName,
                vesselGuid);

            Initialize();

            LedgerRolloutAdoption.RecordVesselRolloutSpending(
                ut,
                cost,
                context,
                AllocateKscSequence,
                (action, actionUt) => ReconcileKscAction(GameStateStore.Events, Ledger.Actions, action, actionUt),
                () => RecalculateAndPatchForLiveTimelineEvent(ut, "vessel-rollout"));
        }

        /// <summary>
        /// #445: Searches the ledger for an unclaimed rollout-tagged
        /// <see cref="FundsSpendingSource.VesselBuild"/> action within
        /// <see cref="RolloutAdoptionWindowSeconds"/> game seconds before
        /// <paramref name="startUT"/>. If found, transfers ownership to
        /// <paramref name="recordingId"/> by setting
        /// <see cref="GameAction.RecordingId"/> and clearing
        /// <see cref="GameAction.DedupKey"/>. Returns the adopted action or <c>null</c>.
        /// <para>
        /// Adoption avoids double-charging when a recording starts from a vessel that has
        /// already incurred the rollout deduction at the launchpad. When multiple
        /// unclaimed rollouts sit within the window, the search runs LIFO — but only
        /// among actions whose stored rollout context matches the recording's current
        /// vessel/site (prefer pid, else vessel name + launch site). This keeps a
        /// stranded cancelled rollout from vessel A from being stolen by an unrelated
        /// prelaunch recording B that happens to start nearby in the same 30-minute window.
        /// </para>
        /// </summary>
        internal static GameAction TryAdoptRolloutAction(string recordingId, double startUT, Recording rec)
        {
            return LedgerRolloutAdoption.TryAdoptRolloutAction(recordingId, startUT, rec, Ledger.Actions);
        }

        /// <summary>
        /// Pure eligibility gate for rollout adoption. Only recordings that started from a
        /// launch-ready PRELAUNCH state are allowed to claim a prior VesselRollout charge.
        /// This deliberately excludes recordings started after liftoff (Flying/Sub-orbital/
        /// Orbiting) so a late Record click cannot pull the vessel build cost into a
        /// mid-flight recording and later refund it on delete/discard.
        /// </summary>
        internal static bool CanRecordingAdoptRolloutAction(Recording rec)
        {
            return LedgerRolloutAdoption.CanRecordingAdoptRolloutAction(rec);
        }
    }
}
