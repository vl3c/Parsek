using System;
using System.Collections.Generic;
using System.Globalization;
using KscActionExpectation = Parsek.KscActionExpectationClassifier.KscActionExpectation;

namespace Parsek
{
    internal static partial class LedgerOrchestrator
    {
        /// <summary>
        /// Test-only override for resource tracker availability checks used by the
        /// earnings reconciliation diagnostics. Null falls back to the live KSP
        /// singleton availability.
        /// </summary>
        internal static void SetResourceTrackingAvailabilityForTesting(
            bool? fundsTracked,
            bool? scienceTracked,
            bool? reputationTracked)
        {
            fundsTrackedOverrideForTesting = fundsTracked;
            scienceTrackedOverrideForTesting = scienceTracked;
            repTrackedOverrideForTesting = reputationTracked;
        }

        /// <summary>
        /// Pure reconciliation: sums dropped FundsChanged/ReputationChanged/ScienceChanged
        /// deltas against effective emitted FundsEarning/FundsSpending/ReputationEarning/
        /// ReputationPenalty/ScienceEarning/ScienceSpending actions in the same UT window
        /// and logs WARN on mismatch beyond tolerance. When <paramref name="recordingId"/>
        /// is supplied, only events tagged to that recording contribute to the store-side
        /// delta. Internal static for testability.
        /// <para>
        /// Note: vesselCost and science actions are already merged into <paramref name="newActions"/>
        /// by <see cref="OnRecordingCommitted"/> before this runs, so they are summed implicitly
        /// through the single <c>newActions</c> walk and do not need separate parameters.
        /// </para>
        /// <para>
        /// #440B: this method must run AFTER <see cref="RecalculateAndPatch"/> populates
        /// derived fields on the seeded actions. The switch reads post-walk values --
        /// TransformedFundsReward / TransformedScienceReward / EffectiveRep / EffectiveScience
        /// -- matching the rewind-path <see cref="ReconcilePostWalk"/> hook. Calling this
        /// before the walk reads zero for every derived field and warns spuriously on
        /// every contract. The Effective-gated cases (ContractComplete, MilestoneAchievement,
        /// ScienceEarning, FundsEarning) mirror the module-side skip on duplicates.
        /// </para>
        /// <para><b>Two row kinds the commit's new rows do not carry as-is.</b> The
        /// strategy door writes its rows straight through <c>Ledger.AddAction</c> from
        /// <see cref="OnStrategyCurrencyConversion"/>, so they are never in
        /// <c>newActions</c>, while the award they divert from (a contract reward diverted
        /// by Appreciation Campaign, a milestone scaled by Leadership Initiative) is
        /// captured as the NET pool move; the in-scope, in-window ones are folded in
        /// (<see cref="AppendInWindowStrategyConversionRows"/>). And a reputation row
        /// inside the reputation seed has its <c>EffectiveRep</c> zeroed by the walk (the
        /// seed already holds it) while its event is still on the store side, so its
        /// emitted leg is the amount stock moved the pool by
        /// (<see cref="EstimateInsideSeedRowRep"/>). Neither changes a ledger value.</para>
        /// </summary>
        internal static void ReconcileEarningsWindow(
            IReadOnlyList<GameStateEvent> events,
            List<GameAction> newActions,
            double startUT, double endUT,
            string recordingId = null)
        {
            GetResourceTrackingAvailability(
                out bool fundsTracked,
                out bool scienceTracked,
                out bool repTracked);
            if (!fundsTracked && !scienceTracked && !repTracked)
            {
                LogReconcileSkippedOnce("earnings-window", "Earnings reconcile",
                    fundsTracked, scienceTracked, repTracked);
                return;
            }

            // [ERS-exempt] The ledger slice is read raw on purpose: the question this
            // answers is "does a KerbalDeath row for this recording exist ANYWHERE in the
            // ledger", including one an effective-state walk would hide, because the
            // VesselLoss event it is a copy of is equally still in the store.
            var ledgerActions = Ledger.Actions;

            var storeDeltas = ComputeEarningsWindowStoreDeltas(
                events,
                startUT,
                endUT,
                recordingId,
                newActions,
                ledgerActions);
            LogEarningsWindowScopeSkipped(storeDeltas, startUT, endUT, recordingId);

            var emittedDeltas = ComputeEarningsWindowEmittedDeltas(
                newActions, events, ledgerActions, startUT, endUT, recordingId);
            LogEarningsWindowKerbalDeathPairing(
                storeDeltas, emittedDeltas, startUT, endUT, recordingId);
            LogEarningsWindowBlindSpotFolds(emittedDeltas, startUT, endUT, recordingId);

            if (scienceTracked)
            {
                storeDeltas.DroppedSciDelta = SumCommitWindowScienceDelta(
                    events,
                    emittedDeltas.EffectiveScienceActions,
                    startUT,
                    endUT,
                    recordingId,
                    storeDeltas.WindowScopedSciDelta);
            }

            LogEarningsWindowActionSummary(emittedDeltas, startUT, endUT);
            WarnOnEarningsWindowMismatches(
                storeDeltas,
                emittedDeltas,
                events,
                startUT,
                endUT,
                recordingId,
                fundsTracked,
                scienceTracked,
                repTracked);
        }

        private struct EarningsWindowStoreDeltas
        {
            public double DroppedFundsDelta;
            public double DroppedRepDelta;
            public double DroppedSciDelta;
            public double WindowScopedSciDelta;
            public int ScopeSkipped;
            public int KerbalDeathPairedSkipped;
        }

        internal struct EarningsWindowEmittedDeltas
        {
            public double EmittedFundsDelta;
            public double EmittedRepDelta;
            public double EmittedSciDelta;
            // Assigned once by ComputeEarningsWindowEmittedDeltas and treated read-only after return.
            public List<GameAction> EffectiveScienceActions;
            public int ContractAcceptCount;
            public int FacilityUpgradeCount;
            public int FacilityRepairCount;
            public int KerbalDeathPairedSkipped;
            public int InsideSeedRepRowsCounted;
            public int StrategyConversionRowsFolded;
        }

        private static EarningsWindowStoreDeltas ComputeEarningsWindowStoreDeltas(
            IReadOnlyList<GameStateEvent> events,
            double startUT,
            double endUT,
            string recordingId,
            IReadOnlyList<GameAction> emittedActions,
            IReadOnlyList<GameAction> ledgerActions)
        {
            var deltas = new EarningsWindowStoreDeltas();
            if (events != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    var e = events[i];
                    bool scoped = EventMatchesRecordingScope(e, recordingId);

                    // BUG-A: mirror ConvertEvents' tag-ownership window rule. An event
                    // tagged to the committing recording is summed even past endUT, so
                    // the store-side delta stays comparable to the emitted-side actions,
                    // which now include those late tagged captures (e.g. Mun milestone
                    // rewards earned after an on-rails warp). Without this the reconciler
                    // would fire a false "missing earning channel" WARN on exactly the
                    // scenario the converters were fixed to handle. The lower bound and
                    // cross-recording scope filtering are unchanged.
                    bool ownedByTag = !string.IsNullOrEmpty(recordingId) && scoped;
                    if (e.ut < startUT || (e.ut > endUT && !ownedByTag)) continue;
                    if (!scoped)
                    {
                        deltas.ScopeSkipped++;
                        continue;
                    }
                    if (IsKerbalDeathPairedVesselLossEvent(e, emittedActions, ledgerActions))
                    {
                        // Reconciled BY CONSTRUCTION, so neither side counts it. The
                        // ReputationPenalty(KerbalDeath) row IS a copy of this event
                        // (CreateKerbalDeathRepPenaltyActions reads its UT and its
                        // magnitude off it), and on a RE-commit of the same recording the
                        // row is dropped by DeduplicateAgainstLedger because the ledger
                        // already holds it - leaving the event on the store side with
                        // nothing to face it, which is the -10 vs 0 WARN this excludes.
                        // An event with NO paired row anywhere (the producer refused: no
                        // dead crew) is NOT excluded and still WARNs, and neither is one
                        // whose row disagrees in magnitude - "by construction" is a claim
                        // about one measurement, so the numbers have to match.
                        deltas.KerbalDeathPairedSkipped++;
                        continue;
                    }
                    switch (e.eventType)
                    {
                        case GameStateEventType.FundsChanged:
                            deltas.DroppedFundsDelta += (e.valueAfter - e.valueBefore);
                            break;
                        case GameStateEventType.ReputationChanged:
                            deltas.DroppedRepDelta += (e.valueAfter - e.valueBefore);
                            break;
                        case GameStateEventType.ScienceChanged:
                            deltas.WindowScopedSciDelta += (e.valueAfter - e.valueBefore);
                            break;
                    }
                }
            }

            return deltas;
        }

        private static void LogEarningsWindowScopeSkipped(
            EarningsWindowStoreDeltas storeDeltas,
            double startUT,
            double endUT,
            string recordingId)
        {
            if (storeDeltas.ScopeSkipped > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"ReconcileEarningsWindow: skipped {storeDeltas.ScopeSkipped} event(s) tagged to other recordings " +
                    $"(scope='{recordingId}', window=[{FormatFixed1(startUT)},{FormatFixed1(endUT)}])");
            }
        }

        /// <summary>
        /// True for a <see cref="GameActionType.ReputationPenalty"/> row written by
        /// <see cref="CreateKerbalDeathRepPenaltyActions"/>. Pure.
        /// </summary>
        internal static bool IsKerbalDeathRepPenaltyRow(GameAction a)
        {
            return a != null
                && a.Type == GameActionType.ReputationPenalty
                && a.RepPenaltySource == ReputationPenaltySource.KerbalDeath;
        }

        /// <summary>
        /// True when <paramref name="actions"/> holds a kerbal-death reputation penalty
        /// row scoped to <paramref name="recordingId"/> AND carrying
        /// <paramref name="appliedMagnitude"/> within
        /// <see cref="EarningsWindowRepTolerance"/>. Pure; an empty id never matches.
        /// <para>
        /// THE MAGNITUDE IS PART OF THE IDENTITY. The producer copies the VesselLoss
        /// event's own magnitude onto the row, so an agreeing pair is one measurement
        /// seen twice and excluding both sides is exact. A row that does NOT agree is a
        /// different quantity - a producer defect, a hand-edited ledger, a second loss
        /// under the same tag - and a shape-only match would have silently cancelled
        /// exactly the discrepancy this reconciliation exists to surface.
        /// </para>
        /// </summary>
        internal static bool HasKerbalDeathRepPenaltyRowForRecording(
            IReadOnlyList<GameAction> actions, string recordingId, double appliedMagnitude)
        {
            if (actions == null || string.IsNullOrEmpty(recordingId)) return false;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (!IsKerbalDeathRepPenaltyRow(a)) continue;
                if (!string.Equals(a.RecordingId ?? "", recordingId, StringComparison.Ordinal))
                    continue;
                if (Math.Abs(a.NominalPenalty - appliedMagnitude) > EarningsWindowRepTolerance)
                    continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// The mirror of <see cref="HasKerbalDeathRepPenaltyRowForRecording"/> read from
        /// the ROW's side: true when <paramref name="events"/> holds the recording-scoped
        /// VesselLoss capture this kerbal-death penalty row was copied from, magnitudes
        /// agreeing within <see cref="EarningsWindowRepTolerance"/>. Only such a row is
        /// excluded from the emitted side of <see cref="ReconcileEarningsWindow"/>; a
        /// magnitude-drifted one stays counted so the pair it does not form still WARNs.
        /// Pure.
        /// </summary>
        internal static bool IsMagnitudePairedKerbalDeathRepPenaltyRow(
            GameAction row, IReadOnlyList<GameStateEvent> events)
        {
            if (!IsKerbalDeathRepPenaltyRow(row)) return false;
            string tag = row.RecordingId ?? "";
            if (string.IsNullOrEmpty(tag)) return false;
            if (events == null) return false;

            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (!KerbalDeathRepPenalty.IsRecordingScopedVesselLoss(e, tag)) continue;
                double applied = e.valueBefore - e.valueAfter;
                if (Math.Abs(row.NominalPenalty - applied) > EarningsWindowRepTolerance)
                    continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// True when <paramref name="evt"/> is the recording-scoped VesselLoss reputation
        /// capture that a kerbal-death <see cref="GameActionType.ReputationPenalty"/> row
        /// was copied from, with that row present either in the commit's emitted list
        /// (first commit) or already in the ledger (re-commit, where
        /// <see cref="DeduplicateAgainstLedger"/> removes the fresh candidate). Such a
        /// pair reconciles by construction and is excluded from both sides of
        /// <see cref="ReconcileEarningsWindow"/>. An unpaired VesselLoss event - the
        /// producer refused because no crew died - returns false and still counts, so the
        /// "missing earning channel" WARN it deserves still fires. So does an event whose
        /// magnitude and row's magnitude DISAGREE: the two are then not one measurement
        /// seen twice, and both sides keep counting. Pure.
        /// </summary>
        internal static bool IsKerbalDeathPairedVesselLossEvent(
            GameStateEvent evt,
            IReadOnlyList<GameAction> emittedActions,
            IReadOnlyList<GameAction> ledgerActions)
        {
            string tag = evt.recordingId ?? "";
            if (string.IsNullOrEmpty(tag)) return false;
            // Same predicate the producer selects candidates with, so the two cannot drift:
            // ReputationChanged + VesselLoss key + exact tag + a positive drop.
            if (!KerbalDeathRepPenalty.IsRecordingScopedVesselLoss(evt, tag)) return false;
            double applied = evt.valueBefore - evt.valueAfter;
            return HasKerbalDeathRepPenaltyRowForRecording(emittedActions, tag, applied)
                || HasKerbalDeathRepPenaltyRowForRecording(ledgerActions, tag, applied);
        }

        private static void LogEarningsWindowKerbalDeathPairing(
            EarningsWindowStoreDeltas storeDeltas,
            EarningsWindowEmittedDeltas emittedDeltas,
            double startUT,
            double endUT,
            string recordingId)
        {
            if (storeDeltas.KerbalDeathPairedSkipped == 0
                && emittedDeltas.KerbalDeathPairedSkipped == 0)
            {
                return;
            }

            ParsekLog.Verbose(Tag,
                $"ReconcileEarningsWindow: excluded {storeDeltas.KerbalDeathPairedSkipped.ToString(CultureInfo.InvariantCulture)} " +
                $"paired VesselLoss event(s) and {emittedDeltas.KerbalDeathPairedSkipped.ToString(CultureInfo.InvariantCulture)} " +
                $"KerbalDeath penalty row(s) from the rep comparison (self-paired by construction) " +
                $"(scope='{recordingId ?? "(none)"}', window=[{FormatFixed1(startUT)},{FormatFixed1(endUT)}])");
        }

        private static EarningsWindowEmittedDeltas ComputeEarningsWindowEmittedDeltas(
            List<GameAction> newActions,
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> ledgerActions,
            double startUT,
            double endUT,
            string recordingId)
        {
            var deltas = new EarningsWindowEmittedDeltas
            {
                EffectiveScienceActions = new List<GameAction>()
            };

            // newActions already contains vesselCostActions + scienceActions (merged by
            // OnRecordingCommitted before this runs). The strategy conversion door files
            // its rows straight into the ledger, never into newActions, so the in-window
            // ones that share the store side's scope are folded in after them: the store
            // event they divert from is NET of them.
            var walk = new List<GameAction>();
            if (newActions != null)
                walk.AddRange(newActions);
            int folded = AppendInWindowStrategyConversionRows(
                walk, newActions, ledgerActions, startUT, endUT, recordingId);
            deltas.StrategyConversionRowsFolded = folded;

            if (walk.Count > 0)
            {
                for (int i = 0; i < walk.Count; i++)
                {
                    var a = walk[i];
                    if (a == null) continue;
                    AccumulateEmittedEarningsRow(
                        ref deltas, a, events, startUT, endUT, recordingId);
                }
            }

            return deltas;
        }

        /// <summary>
        /// One row of <see cref="ComputeEarningsWindowEmittedDeltas"/>: add what the row
        /// emitted to the funds, reputation and science totals (a row inside the reputation
        /// seed counts the amount stock moved the pool by) and to the per-type counters.
        /// </summary>
        internal static void AccumulateEmittedEarningsRow(
            ref EarningsWindowEmittedDeltas deltas,
            GameAction a,
            IReadOnlyList<GameStateEvent> events,
            double startUT,
            double endUT,
            string recordingId)
        {
            // A reputation row inside the reputation seed is already in the seed
            // value, so the walk zeroes its EffectiveRep; the store still sees
            // its event. Count the amount stock moved the pool by instead.
            bool insideSeedRep = ReputationSeedMembership.ShouldSkipAsInsideSeed(
                a.Type, a.InsideReputationSeed);
            float repLeg = insideSeedRep
                ? EstimateInsideSeedRowRep(a, events, startUT, endUT, recordingId)
                : a.EffectiveRep;
            if (insideSeedRep) deltas.InsideSeedRepRowsCounted++;
            switch (a.Type)
            {
                case GameActionType.FundsEarning:
                    // #440B: preemptive parity with FundsModule, which skips
                    // crediting when !Effective. No TransformedFundsAwarded
                    // exists today; read raw FundsAwarded.
                    if (!a.Effective) break;
                    deltas.EmittedFundsDelta += a.FundsAwarded;
                    break;
                case GameActionType.FundsSpending:
                    deltas.EmittedFundsDelta -= a.FundsSpent;
                    break;
                case GameActionType.ReputationEarning:
                    // #440B: EffectiveRep is curve-applied by ReputationModule
                    // (ApplyReputationCurve). Mirrors ReconcilePostWalk.
                    deltas.EmittedRepDelta += repLeg;
                    break;
                case GameActionType.ReputationPenalty:
                    // The kerbal-death leg is excluded on BOTH sides (see
                    // ComputeEarningsWindowStoreDeltas): the row is a copy of the
                    // VesselLoss event, so counting it here while a re-commit
                    // drops the row would make first commit and re-commit
                    // disagree. Every other penalty source stays - and so does a
                    // kerbal-death row whose magnitude does NOT agree with the
                    // event it claims to copy, because that pair does not cancel
                    // and the discrepancy is exactly what this walk is for.
                    if (IsMagnitudePairedKerbalDeathRepPenaltyRow(a, events))
                    {
                        deltas.KerbalDeathPairedSkipped++;
                        break;
                    }
                    // #440B: EffectiveRep is signed negative for penalties
                    // (ReputationModule.ProcessContractPenaltyRep). No unary minus.
                    deltas.EmittedRepDelta += repLeg;
                    break;
                case GameActionType.ContractComplete:
                // A parameter's own reward is the other half of the ContractReward
                // store deltas; same fields, same gate.
                case GameActionType.ContractParameterComplete:
                    // #440B: read post-walk Transformed* / EffectiveRep. Gate on
                    // Effective to skip duplicate completions (module skips credit).
                    if (!a.Effective) break;
                    deltas.EmittedFundsDelta += a.TransformedFundsReward;
                    deltas.EmittedRepDelta += repLeg;
                    deltas.EmittedSciDelta += a.TransformedScienceReward;
                    break;
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    // #440B: funds penalty has no transform today; keep raw.
                    // Rep penalty: EffectiveRep is already signed negative.
                    // Gate on Effective: a fail / cancel of an already-resolved
                    // contract charges nothing (ContractsModule).
                    if (!a.Effective) break;
                    deltas.EmittedFundsDelta -= a.FundsPenalty;
                    deltas.EmittedRepDelta += repLeg;
                    break;
                case GameActionType.ContractAccept:
                    // #438 gap #1: without this case the advance payment's
                    // FundsChanged(ContractAdvance) delta had no emitted-side
                    // counterpart and every accepted contract whose UT fell
                    // inside a commit window produced a spurious funds WARN.
                    deltas.EmittedFundsDelta += a.AdvanceFunds;
                    deltas.ContractAcceptCount++;
                    break;
                case GameActionType.FacilityUpgrade:
                    // #438 gap #6: symmetric to the KSC-side ReconcileKscAction
                    // path -- a facility spend inside a recording's commit window
                    // must subtract from the emitted delta to match the store's
                    // negative FundsChanged.
                    deltas.EmittedFundsDelta -= a.FacilityCost;
                    deltas.FacilityUpgradeCount++;
                    break;
                case GameActionType.FacilityRepair:
                    // A repair of a building the walk already has intact is free
                    // (FundsModule.ShouldChargeFacilityRepair).
                    if (!a.Effective) break;
                    deltas.EmittedFundsDelta -= a.FacilityCost;
                    deltas.FacilityRepairCount++;
                    break;
                case GameActionType.MilestoneAchievement:
                    // #440B: rep leg reads EffectiveRep (curve-applied). Funds/Sci
                    // legs keep raw MilestoneFundsAwarded / MilestoneScienceAwarded
                    // (no transform today). Gate on Effective for duplicate milestones.
                    // A partly credited world-record row emits only its new
                    // thresholds' share, like a zeroed duplicate emits nothing.
                    if (!a.Effective) break;
                    deltas.EmittedFundsDelta += a.CreditedMilestoneFunds;
                    deltas.EmittedRepDelta += repLeg;
                    deltas.EmittedSciDelta += a.CreditedMilestoneScience;
                    break;
                case GameActionType.ScienceEarning:
                    // #440B: EffectiveScience is post-subject-cap (ScienceModule).
                    // At cap, EffectiveScience=0 while ScienceAwarded != 0 --
                    // this silences a false-positive WARN on capped subjects.
                    if (!a.Effective) break;
                    deltas.EmittedSciDelta += a.EffectiveScience;
                    deltas.EffectiveScienceActions.Add(a);
                    break;
                case GameActionType.ScienceSpending:
                    deltas.EmittedSciDelta -= a.Cost;
                    break;
                case GameActionType.StrategyScienceDebit:
                    // ComputeEarningsWindowStoreDeltas sums every in-window
                    // ScienceChanged delta that passes its RECORDING-SCOPE filter
                    // (EventMatchesRecordingScope) regardless of the event's
                    // reason key, so without this emitted-side arm a currency
                    // exchange landing inside a flight commit window fires a false
                    // "missing earning channel" science WARN.
                    deltas.EmittedSciDelta -= a.Cost;
                    break;
                case GameActionType.StrategyScienceCredit:
                    // Mirror of the debit arm above for the OUTPUT direction.
                    // (Query-family rows are written UNTAGGED and land in the
                    // ledger immediately rather than in a commit's newActions,
                    // so this arm bites only if a future producer ever emits a
                    // tagged one - it is here so the type is never silently
                    // absent from the emitted side.)
                    deltas.EmittedSciDelta += a.ScienceAwarded;
                    break;
                case GameActionType.ScienceLabEarning:
                    // The lab's subject-less credit: its ScienceChanged
                    // (ScienceTransmission / VesselRecovery) is on the store side,
                    // and before this row existed it fired the "missing earning
                    // channel" WARN on every lab transmission.
                    deltas.EmittedSciDelta += a.ScienceAwarded;
                    break;
            }
        }

        /// <summary>
        /// Appends to <paramref name="walk"/> every strategy conversion row
        /// (<see cref="IsStrategyConversionRow"/>) in <paramref name="ledgerActions"/> that
        /// the store side of <see cref="ReconcileEarningsWindow"/> sees the event of, and
        /// that <paramref name="newActions"/> does not already carry. The conversion door
        /// (<see cref="OnStrategyCurrencyConversion"/>) writes these rows straight into the
        /// ledger, tagged like the award they divert from, so they are never in a commit's
        /// new rows, while the award's captured event is the NET pool move. The scope and
        /// window rule is the store side's own (<c>ComputeEarningsWindowStoreDeltas</c>):
        /// with a recording id, only rows carrying that tag, and a tagged row counts past
        /// <paramref name="endUT"/>; with none, every row in the window. Returns how many
        /// rows it appended. Pure over its inputs.
        /// </summary>
        internal static int AppendInWindowStrategyConversionRows(
            List<GameAction> walk,
            IReadOnlyList<GameAction> newActions,
            IReadOnlyList<GameAction> ledgerActions,
            double startUT,
            double endUT,
            string recordingId)
        {
            if (walk == null || ledgerActions == null)
                return 0;

            int folded = 0;
            for (int i = 0; i < ledgerActions.Count; i++)
            {
                var a = ledgerActions[i];
                if (a == null || !IsStrategyConversionRow(a)) continue;
                if (IsSameRowIn(newActions, a)) continue;

                bool scoped = string.IsNullOrEmpty(recordingId)
                    || string.Equals(a.RecordingId ?? "", recordingId, StringComparison.Ordinal);
                if (!scoped) continue;
                bool ownedByTag = !string.IsNullOrEmpty(recordingId);
                if (a.UT < startUT || (a.UT > endUT && !ownedByTag)) continue;

                walk.Add(a);
                folded++;
            }
            return folded;
        }

        private static bool IsSameRowIn(IReadOnlyList<GameAction> actions, GameAction row)
        {
            if (actions == null) return false;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (ReferenceEquals(a, row)) return true;
                if (a != null && !string.IsNullOrEmpty(row.ActionId)
                    && string.Equals(a.ActionId, row.ActionId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The reputation amount a row inside the reputation seed moved stock's pool by,
        /// for the emitted side of <see cref="ReconcileEarningsWindow"/>. The walk zeroes
        /// such a row's <c>EffectiveRep</c> (the seed value already holds it) while the
        /// store side still sums the row's captured event, so reading <c>EffectiveRep</c>
        /// made every such row (a world-record bonus filed in the first commit of a save
        /// Parsek met mid-career) read 0. The row's nominal leg
        /// (<see cref="ResolveInsideSeedRepNominal"/>) is put through
        /// <see cref="ReputationModule.ApplyReputationCurve"/> at the store's own pool
        /// level at the row's UT (<see cref="ResolveStoreRepPoolAtUT"/>), which is how
        /// stock moved the pool. A pre-curved source is returned as is.
        /// </summary>
        internal static float EstimateInsideSeedRowRep(
            GameAction a,
            IReadOnlyList<GameStateEvent> events,
            double startUT,
            double endUT,
            string recordingId)
        {
            float nominal = ResolveInsideSeedRepNominal(a, out bool preCurved);
            if (nominal == 0f)
                return 0f;
            if (preCurved)
                return nominal;
            double pool = ResolveStoreRepPoolAtUT(events, a.UT, startUT, endUT, recordingId);
            return ReputationModule.ApplyReputationCurve(nominal, (float)pool).actualDelta;
        }

        /// <summary>
        /// The signed reputation leg <c>ReputationModule</c> would put through the curve
        /// for <paramref name="a"/> if it were outside the seed, mirroring each per-type
        /// arm there (including its <c>Effective</c> gates and the three pre-curved
        /// penalty sources, reported through <paramref name="preCurved"/>). Zero for a
        /// row type that moves no reputation. Pure.
        /// </summary>
        internal static float ResolveInsideSeedRepNominal(GameAction a, out bool preCurved)
        {
            preCurved = false;
            if (a == null)
                return 0f;
            switch (a.Type)
            {
                case GameActionType.ReputationEarning:
                    return a.NominalRep * a.ConversionShare;
                case GameActionType.ReputationPenalty:
                    if (a.RepPenaltySource == ReputationPenaltySource.Strategy
                        || a.RepPenaltySource == ReputationPenaltySource.KerbalDeath
                        || a.RepPenaltySource == ReputationPenaltySource.ContractDecline)
                    {
                        preCurved = true;
                        return -a.NominalPenalty;
                    }
                    return -a.NominalPenalty * a.ConversionShare;
                case GameActionType.MilestoneAchievement:
                    return a.Effective ? a.CreditedMilestoneRep : 0f;
                case GameActionType.ContractComplete:
                case GameActionType.ContractParameterComplete:
                    return a.Effective ? a.TransformedRepReward : 0f;
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    return a.Effective ? -a.RepPenalty : 0f;
                case GameActionType.StrategyActivate:
                    return -a.SetupReputationCost;
                default:
                    return 0f;
            }
        }

        /// <summary>
        /// Stock's reputation pool just before a change at <paramref name="ut"/>, read off
        /// the store side's in-window, in-scope <c>ReputationChanged</c> events: the
        /// <c>valueBefore</c> of the earliest event at that UT, else the
        /// <c>valueAfter</c> of the latest event before it, else the <c>valueBefore</c> of
        /// the window's first event, else 0 (the curve's multiplier there is 1). Pure.
        /// </summary>
        internal static double ResolveStoreRepPoolAtUT(
            IReadOnlyList<GameStateEvent> events,
            double ut,
            double startUT,
            double endUT,
            string recordingId)
        {
            const double sameUtEpsilon = 1e-6;
            bool haveAt = false, haveBefore = false, haveFirst = false;
            double atUt = double.MaxValue, atValue = 0.0;
            double beforeUt = double.MinValue, beforeValue = 0.0;
            double firstUt = double.MaxValue, firstValue = 0.0;
            if (events != null)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    var e = events[i];
                    if (e.eventType != GameStateEventType.ReputationChanged) continue;
                    bool scoped = EventMatchesRecordingScope(e, recordingId);
                    if (!scoped) continue;
                    bool ownedByTag = !string.IsNullOrEmpty(recordingId);
                    if (e.ut < startUT || (e.ut > endUT && !ownedByTag)) continue;

                    if (e.ut < firstUt)
                    {
                        firstUt = e.ut;
                        firstValue = e.valueBefore;
                        haveFirst = true;
                    }
                    if (Math.Abs(e.ut - ut) <= sameUtEpsilon)
                    {
                        if (!haveAt || e.ut < atUt)
                        {
                            atUt = e.ut;
                            atValue = e.valueBefore;
                            haveAt = true;
                        }
                    }
                    else if (e.ut < ut && e.ut >= beforeUt)
                    {
                        beforeUt = e.ut;
                        beforeValue = e.valueAfter;
                        haveBefore = true;
                    }
                }
            }
            if (haveAt) return atValue;
            if (haveBefore) return beforeValue;
            if (haveFirst) return firstValue;
            return 0.0;
        }

        private static void LogEarningsWindowBlindSpotFolds(
            EarningsWindowEmittedDeltas emittedDeltas,
            double startUT,
            double endUT,
            string recordingId)
        {
            if (emittedDeltas.InsideSeedRepRowsCounted == 0
                && emittedDeltas.StrategyConversionRowsFolded == 0)
            {
                return;
            }

            ParsekLog.Verbose(Tag,
                $"ReconcileEarningsWindow: counted {emittedDeltas.InsideSeedRepRowsCounted.ToString(CultureInfo.InvariantCulture)} " +
                $"inside-seed reputation row(s) at stock's curve and folded " +
                $"{emittedDeltas.StrategyConversionRowsFolded.ToString(CultureInfo.InvariantCulture)} " +
                $"ledger strategy conversion row(s) into the emitted side " +
                $"(scope='{recordingId ?? "(none)"}', window=[{FormatFixed1(startUT)},{FormatFixed1(endUT)}])");
        }

        private static void LogEarningsWindowActionSummary(
            EarningsWindowEmittedDeltas emittedDeltas,
            double startUT,
            double endUT)
        {
            // #438: single Verbose summary covering the three newly-handled action
            // types so their contribution to emittedFundsDelta is visible in KSP.log
            // without emitting one log line per loop iteration.
            if (emittedDeltas.ContractAcceptCount > 0
                || emittedDeltas.FacilityUpgradeCount > 0
                || emittedDeltas.FacilityRepairCount > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"ReconcileEarningsWindow: summed {emittedDeltas.ContractAcceptCount} ContractAccept, " +
                    $"{emittedDeltas.FacilityUpgradeCount} FacilityUpgrade, {emittedDeltas.FacilityRepairCount} FacilityRepair " +
                    $"into emittedFundsDelta window=[{FormatFixed1(startUT)},{FormatFixed1(endUT)}]");
            }
        }

        private static void WarnOnEarningsWindowMismatches(
            EarningsWindowStoreDeltas storeDeltas,
            EarningsWindowEmittedDeltas emittedDeltas,
            IReadOnlyList<GameStateEvent> events,
            double startUT,
            double endUT,
            string recordingId,
            bool fundsTracked,
            bool scienceTracked,
            bool repTracked)
        {
            const double fundsTol = 1.0;   // 1 funds tolerance for rounding
            const double repTol = EarningsWindowRepTolerance;
            const double sciTol = 0.1f;

            if (fundsTracked && Math.Abs(storeDeltas.DroppedFundsDelta - emittedDeltas.EmittedFundsDelta) > fundsTol)
            {
                ParsekLog.Warn(Tag,
                    $"Earnings reconciliation (funds): store delta={FormatFixed1(storeDeltas.DroppedFundsDelta)} vs " +
                    $"ledger emitted delta={FormatFixed1(emittedDeltas.EmittedFundsDelta)} — missing earning channel? " +
                    $"window=[{FormatFixed1(startUT)},{FormatFixed1(endUT)}]");
            }
            if (repTracked && Math.Abs(storeDeltas.DroppedRepDelta - emittedDeltas.EmittedRepDelta) > repTol)
            {
                ParsekLog.Warn(Tag,
                    $"Earnings reconciliation (rep): store delta={FormatFixed1(storeDeltas.DroppedRepDelta)} vs " +
                    $"ledger emitted delta={FormatFixed1(emittedDeltas.EmittedRepDelta)} — missing earning channel? " +
                    $"window=[{FormatFixed1(startUT)},{FormatFixed1(endUT)}]");
            }
            if (scienceTracked && Math.Abs(storeDeltas.DroppedSciDelta - emittedDeltas.EmittedSciDelta) > sciTol)
            {
                string message =
                    $"Earnings reconciliation (sci): store delta={FormatFixed1(storeDeltas.DroppedSciDelta)} vs " +
                    $"ledger emitted delta={FormatFixed1(emittedDeltas.EmittedSciDelta)} — missing earning channel? " +
                    $"window=[{FormatFixed1(startUT)},{FormatFixed1(endUT)}]";
                string warnKey = string.Format(
                    CultureInfo.InvariantCulture,
                    "commit:sci:{0}:{1:F3}:{2:F3}:{3:F3}:{4:F3}",
                    recordingId ?? "",
                    startUT,
                    endUT,
                    storeDeltas.DroppedSciDelta,
                    emittedDeltas.EmittedSciDelta);
                if (LogReconcileWarnOnce(warnKey, message))
                {
                    LogScienceCommitReconcileDumpOnce(
                        warnKey,
                        events,
                        emittedDeltas.EffectiveScienceActions,
                        startUT,
                        endUT,
                        recordingId);
                }
            }
        }

        private static double SumCommitWindowScienceDelta(
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> effectiveScienceActions,
            double startUT,
            double endUT,
            string recordingId,
            double windowScopedSciDelta)
        {
            if (events == null || events.Count == 0)
                return 0.0;
            if (effectiveScienceActions == null || effectiveScienceActions.Count == 0)
                return windowScopedSciDelta;

            double matchedSciDelta = windowScopedSciDelta;
            int extendedCount = 0;
            int widenedUntaggedCount = 0;

            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (evt.eventType != GameStateEventType.ScienceChanged)
                    continue;
                if (evt.ut >= startUT)
                    continue;
                if (evt.ut > endUT)
                    continue;
                if (!string.IsNullOrEmpty(evt.recordingId ?? ""))
                    continue;
                if (!DoesScienceEventMatchAnyCommitAction(
                        evt,
                        effectiveScienceActions,
                        startUT,
                        endUT,
                        recordingId,
                        out bool matchedViaUntaggedWindow))
                {
                    continue;
                }

                matchedSciDelta += (evt.valueAfter - evt.valueBefore);
                extendedCount++;
                if (matchedViaUntaggedWindow)
                    widenedUntaggedCount++;
            }

            if (extendedCount > 0 && widenedUntaggedCount > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"ReconcileEarningsWindow: extended science delta with {extendedCount} " +
                    $"including {widenedUntaggedCount} untagged pre-recording event(s) " +
                    $"window=[{FormatFixed1(startUT)},{FormatFixed1(endUT)}] scope='{recordingId ?? "(none)"}'");
            }

            return matchedSciDelta;
        }

        private static bool DoesScienceEventMatchAnyCommitAction(
            GameStateEvent evt,
            IReadOnlyList<GameAction> effectiveScienceActions,
            double startUT,
            double endUT,
            string recordingId,
            out bool matchedViaUntaggedWindow)
        {
            matchedViaUntaggedWindow = false;

            if (effectiveScienceActions == null || effectiveScienceActions.Count == 0)
                return evt.ut >= startUT && evt.ut <= endUT && EventMatchesRecordingScope(evt, recordingId);

            for (int i = 0; i < effectiveScienceActions.Count; i++)
            {
                var action = effectiveScienceActions[i];
                if (action == null)
                    continue;
                if (!TryGetPersistedScienceActionWindow(
                        action,
                        out double actionStartUt,
                        out double actionEndUt,
                        out bool ignoredCollapsedPersistedSpan))
                {
                    actionStartUt = action.StartUT;
                    actionEndUt = action.EndUT;
                }
                if (evt.ut < actionStartUt || evt.ut > actionEndUt)
                    continue;
                if (!string.Equals(
                        evt.key ?? "",
                        GetScienceChangedReasonKey(action),
                        StringComparison.Ordinal))
                {
                    continue;
                }
                if (!DoesScienceEventMatchActionScope(
                        evt,
                        action,
                        actionStartUt,
                        actionEndUt,
                        out bool viaUntaggedWindow))
                    continue;

                matchedViaUntaggedWindow = viaUntaggedWindow;
                return true;
            }

            return false;
        }

        internal static bool LogReconcileWarnOnce(string warnKey, string message)
        {
            if (string.IsNullOrEmpty(warnKey) || emittedReconcileWarnKeys.Add(warnKey))
            {
                ParsekLog.Warn(Tag, message);
                return true;
            }

            return false;
        }

        private static void LogScienceCommitReconcileDumpOnce(
            string dumpKey,
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> effectiveScienceActions,
            double startUT,
            double endUT,
            string recordingId)
        {
            if (events == null || events.Count == 0)
                return;
            if (!emittedScienceReconcileDumpKeys.Add("commit-dump:" + (dumpKey ?? "")))
                return;

            double dumpStartUt = startUT - 5.0;
            double dumpEndUt = endUT + 5.0;
            var lines = new List<string>();
            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (evt.eventType != GameStateEventType.ScienceChanged)
                    continue;
                if (evt.ut < dumpStartUt || evt.ut > dumpEndUt)
                    continue;

                bool matched = DoesScienceEventMatchAnyCommitAction(
                    evt,
                    effectiveScienceActions,
                    startUT,
                    endUT,
                    recordingId,
                    out bool viaUntaggedWindow);

                lines.Add(FormatScienceEventForReconcileDump(evt, matched, viaUntaggedWindow));
            }

            string detail = lines.Count == 0
                ? "(no ScienceChanged events in dump window)"
                : string.Join(" | ", lines.ToArray());
            ParsekLog.Error(Tag,
                $"Science reconcile dump (commit): window=[{FormatFixed1(startUT)},{FormatFixed1(endUT)}] " +
                $"scope='{recordingId ?? "(none)"}' events={detail}");
        }

        /// <summary>
        /// Pure classifier: maps a KSC-path <see cref="GameAction"/> to its reconciliation
        /// class + expected paired resource event. Used by <see cref="ReconcileKscAction"/>.
        /// <para>
        /// Event keys are <c>TransactionReasons.ToString()</c> as written by
        /// <see cref="GameStateRecorder"/>'s <c>OnFundsChanged</c> / <c>OnScienceChanged</c> /
        /// <c>OnReputationChanged</c> handlers (see their <c>key = reason.ToString()</c>
        /// lines).
        /// </para>
        /// </summary>
        internal static KscActionExpectation ClassifyAction(GameAction action)
        {
            return KscActionExpectationClassifier.ClassifyAction(action);
        }

        /// <summary>Compatibility facade for <see cref="KscActionReconciler.ReconcileKscAction"/>.</summary>
        internal static void ReconcileKscAction(
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> ledgerActions,
            GameAction action,
            double ut)
        {
            KscActionReconciler.ReconcileKscAction(events, ledgerActions, action, ut);
        }

        internal static void ReconcilePostWalk(
            IReadOnlyList<GameStateEvent> events,
            IReadOnlyList<GameAction> actions,
            double? utCutoff)
        {
            PostWalkActionReconciler.ReconcilePostWalk(events, actions, utCutoff);
        }

        internal static bool TryGetPersistedScienceActionWindow(
            GameAction action,
            out double startUt,
            out double endUt,
            out bool collapsedPersistedSpan)
        {
            startUt = 0.0;
            endUt = 0.0;
            collapsedPersistedSpan = false;

            if (action == null)
                return false;
            if (float.IsNaN(action.StartUT) || float.IsNaN(action.EndUT))
                return false;

            if (action.EndUT > action.StartUT)
            {
                startUt = action.StartUT;
                endUt = action.EndUT;
                return true;
            }

            if (action.EndUT == action.StartUT && action.EndUT > 0f)
            {
                collapsedPersistedSpan = true;
                startUt = GetCollapsedScienceWindowStart(action.StartUT, action.UT);
                endUt = action.UT;
                if (endUt < startUt)
                    endUt = startUt;
                return true;
            }

            return false;
        }

        private static double GetCollapsedScienceWindowStart(float collapsedStartUt, double actionUt)
        {
            double halfWidth = GetScienceReconcileCollapsedHalfWidth(collapsedStartUt);
            double startUt = collapsedStartUt - halfWidth;
            if (double.IsNaN(startUt) || double.IsInfinity(startUt))
                startUt = collapsedStartUt;
            if (actionUt < startUt)
                startUt = actionUt;
            return startUt;
        }

        private static double GetScienceReconcileCollapsedHalfWidth(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return 0.0;

            int bits = BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
            float previous = BitConverter.ToSingle(BitConverter.GetBytes(bits - 1), 0);
            float next = BitConverter.ToSingle(BitConverter.GetBytes(bits + 1), 0);
            double lower = float.IsNaN(previous) || float.IsInfinity(previous)
                ? 0.0
                : Math.Abs((double)value - previous);
            double upper = float.IsNaN(next) || float.IsInfinity(next)
                ? 0.0
                : Math.Abs(next - (double)value);

            double width = 0.0;
            if (lower > 0.0 && upper > 0.0)
                width = Math.Min(lower, upper);
            else
                width = Math.Max(lower, upper);

            return width * 0.5;
        }

        // Mirrored in GameStateEventConverter.EventMatchesRecordingScope; keep the two in sync.
        private static bool EventMatchesRecordingScope(GameStateEvent evt, string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return true;

            string eventRecordingId = evt.recordingId ?? "";
            return string.Equals(eventRecordingId, recordingId, StringComparison.Ordinal);
        }

        internal static bool DoesScienceEventMatchActionScope(
            GameStateEvent evt,
            GameAction action,
            double actionStartUt,
            double actionEndUt,
            out bool matchedViaUntaggedWindow)
        {
            matchedViaUntaggedWindow = false;

            string actionRecordingId = action?.RecordingId ?? "";
            if (string.IsNullOrEmpty(actionRecordingId))
                return true;

            string eventRecordingId = evt.recordingId ?? "";
            if (string.Equals(eventRecordingId, actionRecordingId, StringComparison.Ordinal))
                return true;

            if (!string.IsNullOrEmpty(eventRecordingId))
                return false;

            if (evt.ut < actionStartUt || evt.ut > actionEndUt)
                return false;

            matchedViaUntaggedWindow = true;
            return true;
        }

        internal static string GetScienceChangedReasonKey(GameAction action)
        {
            if (action != null && action.Method == ScienceMethod.Recovered)
                return VesselRecoveryReasonKey;

            return "ScienceTransmission";
        }

        internal static string FormatScienceEventForReconcileDump(
            GameStateEvent evt,
            bool matchedScope,
            bool matchedViaUntaggedWindow)
        {
            string tag = evt.recordingId ?? "";
            string matchLabel = matchedScope ? "match" : "skip";
            string untaggedLabel = matchedViaUntaggedWindow ? ", untagged-window" : "";
            return string.Format(
                CultureInfo.InvariantCulture,
                "ut={0:F1} key='{1}' delta={2:F1} tag='{3}' [{4}{5}]",
                evt.ut,
                evt.key ?? "",
                evt.valueAfter - evt.valueBefore,
                tag,
                matchLabel,
                untaggedLabel);
        }
        internal static void GetResourceTrackingAvailability(
            out bool fundsTracked,
            out bool scienceTracked,
            out bool repTracked)
        {
            fundsTracked = fundsTrackedOverrideForTesting ?? Funding.Instance != null;
            scienceTracked = scienceTrackedOverrideForTesting
                ?? ResearchAndDevelopment.Instance != null;
            repTracked = repTrackedOverrideForTesting ?? global::Reputation.Instance != null;
        }

        internal static void LogReconcileSkippedOnce(
            string scopeKey,
            string scopeLabel,
            bool fundsTracked,
            bool scienceTracked,
            bool repTracked)
        {
            ParsekLog.VerboseRateLimited(
                Tag,
                $"reconcile-skip:{scopeKey}:{fundsTracked}:{scienceTracked}:{repTracked}",
                $"{scopeLabel} skipped: sandbox / tracker unavailable " +
                $"(funds={fundsTracked.ToString().ToLowerInvariant()} " +
                $"sci={scienceTracked.ToString().ToLowerInvariant()} " +
                $"rep={repTracked.ToString().ToLowerInvariant()})",
                OneShotReconcileSkipLogIntervalSeconds);
        }

        internal static bool TryRegisterPostWalkDumpKey(string dumpKey)
        {
            return emittedScienceReconcileDumpKeys.Add("postwalk-dump:" + (dumpKey ?? ""));
        }

        internal static string FormatFixed1(double value)
        {
            return value.ToString("F1", CultureInfo.InvariantCulture);
        }

        internal static string FormatFixed3(double value)
        {
            return value.ToString("F3", CultureInfo.InvariantCulture);
        }
    }
}
