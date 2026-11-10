using System;
using System.Collections.Generic;
using System.Globalization;
using KscActionExpectation = Parsek.KscActionExpectationClassifier.KscActionExpectation;

namespace Parsek
{
    internal static partial class LedgerOrchestrator
    {
        /// <summary>
        /// Removes actions from the candidate list that already exist in the ledger.
        /// Matches on Type + occurrence UT (within epsilon) + key field (SubjectId, NodeId,
        /// FacilityId, MilestoneId, or ContractId depending on type). Science dedup compares
        /// the capture moment, read from StartUT: a recording-commit ScienceEarning row is
        /// dated at its capture UT with StartUT = capture and EndUT = recording end, a row an
        /// older build filed is dated at the recording end with the capture only in StartUT,
        /// and a direct KSC science row stores UT == StartUT == EndUT, so StartUT is the one
        /// field every shape agrees on.
        /// This prevents double-adding KSC events that were written to the ledger in real-time
        /// via OnKscSpending but also fall within a recording's time range.
        /// KerbalAssignment rows match on their (RecordingId, KerbalName) key alone, with
        /// no UT window (see the comment in the loop).
        /// A ScienceEarning candidate that carries a capture identity
        /// (<see cref="GameAction.HasScienceCaptureIdentity"/>) matches ONLY a row with
        /// the same <see cref="GameAction.ActionId"/> (or an earlier candidate with that id
        /// in the same batch), never by subject + capture moment: stock fires one callback
        /// per data item, so two canisters of one subject recovered together, or two
        /// deployed stations sending one subject in one frame, are two awards at the same
        /// instant and both stay (SCIENCE-SAME-SUBJECT-SAME-INSTANT-ROW-DROPPED). Every
        /// re-file of a capture is a conversion of the same pending subject, so it carries
        /// the filed row's id. A candidate without a capture identity (no production
        /// producer builds one today) keeps the legacy subject + capture-moment match.
        /// Every other candidate converted from a captured game-state event
        /// (<see cref="GameAction.HasEventCaptureIdentity"/>) matches only a row with the same
        /// <see cref="GameAction.CaptureSequence"/> and key (design 3.15 step 3: a row
        /// duplicate is one capture filed twice; an event duplicate, two captures with equal
        /// economic keys, keeps both rows for the replay to decide). The type + UT + key
        /// match above stays the fallback for a pair where either side names no capture: a
        /// row from an older save, or a row built without an event (vessel cost, crew,
        /// death penalty, recovery rows).
        /// </summary>
        internal static List<GameAction> DeduplicateAgainstLedger(List<GameAction> candidates)
        {
            return DeduplicateAgainst(Ledger.Actions, candidates);
        }

        /// <summary>
        /// <see cref="DeduplicateAgainstLedger"/> against an explicit row list: the commit
        /// row preview dedups each recording against the ledger plus the rows the same
        /// tree commit filed before it, without adding anything.
        /// </summary>
        internal static List<GameAction> DeduplicateAgainst(
            IReadOnlyList<GameAction> existing, List<GameAction> candidates)
        {
            if (existing.Count == 0 && !HasScienceCaptureIdentityCandidate(candidates))
                return candidates;

            var result = new List<GameAction>(candidates.Count);
            HashSet<string> batchCaptureIds = null;
            int captureIdMatches = 0;
            int captureIdBatchRepeats = 0;
            int eventCaptureMatches = 0;
            int legacyKeyMatches = 0;
            int eventDuplicatesKept = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];

                if (IsScienceCaptureIdentified(c))
                {
                    DedupScienceCaptureCandidate(existing, c, result,
                        ref batchCaptureIds, ref captureIdMatches, ref captureIdBatchRepeats);
                    continue;
                }

                DedupEventOrEconomicCandidate(existing, c, result,
                    ref eventCaptureMatches, ref legacyKeyMatches, ref eventDuplicatesKept);
            }

            if (result.Count < candidates.Count || eventDuplicatesKept > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"DeduplicateAgainstLedger: removed {candidates.Count - result.Count} duplicates " +
                    $"from {candidates.Count} candidates " +
                    $"(scienceCaptureIdMatches={captureIdMatches.ToString(CultureInfo.InvariantCulture)} " +
                    $"scienceCaptureIdBatchRepeats={captureIdBatchRepeats.ToString(CultureInfo.InvariantCulture)} " +
                    $"eventCaptureMatches={eventCaptureMatches.ToString(CultureInfo.InvariantCulture)} " +
                    $"legacyKeyMatches={legacyKeyMatches.ToString(CultureInfo.InvariantCulture)} " +
                    $"eventDuplicatesKept={eventDuplicatesKept.ToString(CultureInfo.InvariantCulture)})");
            }

            return result;
        }

        /// <summary>
        /// The science-capture arm of <see cref="DeduplicateAgainst"/> for one candidate
        /// whose ActionId is a capture identity: drop a repeat of an id already seen in
        /// this batch, drop an id the existing rows already hold, else keep it.
        /// </summary>
        internal static void DedupScienceCaptureCandidate(
            IReadOnlyList<GameAction> existing,
            GameAction c,
            List<GameAction> result,
            ref HashSet<string> batchCaptureIds,
            ref int captureIdMatches,
            ref int captureIdBatchRepeats)
        {
            bool isDuplicate = false;

            // One capture converts to one row. A second candidate with the same id
            // in this batch is the same capture presented twice, not an award.
            if (batchCaptureIds == null)
                batchCaptureIds = new HashSet<string>(StringComparer.Ordinal);
            if (!batchCaptureIds.Add(c.ActionId))
            {
                captureIdBatchRepeats++;
                return;
            }

            for (int j = 0; j < existing.Count; j++)
            {
                if (string.Equals(existing[j].ActionId, c.ActionId, StringComparison.Ordinal))
                {
                    isDuplicate = true;
                    captureIdMatches++;
                    break;
                }
            }

            if (!isDuplicate)
                result.Add(c);
        }

        /// <summary>
        /// The event-capture and economic arm of <see cref="DeduplicateAgainst"/> for one
        /// candidate without a science capture identity: drop it when an existing row names
        /// the same capture, or (where either side names no capture) matches it
        /// economically; else keep it, logging a same-key row of another capture.
        /// </summary>
        internal static void DedupEventOrEconomicCandidate(
            IReadOnlyList<GameAction> existing,
            GameAction c,
            List<GameAction> result,
            ref int eventCaptureMatches,
            ref int legacyKeyMatches,
            ref int eventDuplicatesKept)
        {
            bool isDuplicate = false;

            bool candidateHasEventCapture = IsEventCaptureIdentified(c);
            GameAction keptAgainst = null;
            for (int j = 0; j < existing.Count; j++)
            {
                var e = existing[j];
                if (e.Type != c.Type) continue;
                // Design 3.15 step 3: a candidate converted from a captured event and a
                // row that names a capture are one row only when they name the SAME
                // capture (a re-commit, a re-home or a direct KSC row of that event).
                // Different captures with equal economic keys are two events - two
                // recordings of one tree crossing one record node inside 0.1 s, or a
                // committed future row and a new flight's at the same instant - and
                // both stay; the replay decides which one counts. A row without a
                // capture number (an older save) keeps the economic match below.
                if (candidateHasEventCapture && e.CaptureSequence > 0)
                {
                    if (e.CaptureSequence == c.CaptureSequence
                        && GetActionKey(e) == GetActionKey(c))
                    {
                        isDuplicate = true;
                        eventCaptureMatches++;
                        break;
                    }
                    if (keptAgainst == null
                        && LegacyEconomicKeyMatches(e, c))
                        keptAgainst = e;
                    continue;
                }

                if (LegacyEconomicKeyMatches(e, c))
                {
                    isDuplicate = true;
                    legacyKeyMatches++;
                    break;
                }
            }

            if (!isDuplicate)
            {
                result.Add(c);
                if (keptAgainst != null)
                {
                    eventDuplicatesKept++;
                    ParsekLog.Verbose(Tag,
                        $"DeduplicateAgainstLedger: kept {c.Type} key='{GetActionKey(c)}' " +
                        $"ut={c.UT.ToString("R", CultureInfo.InvariantCulture)} " +
                        $"cseq={c.CaptureSequence.ToString(CultureInfo.InvariantCulture)} as a second event: " +
                        $"row {keptAgainst.ActionId ?? "(none)"} " +
                        $"ut={keptAgainst.UT.ToString("R", CultureInfo.InvariantCulture)} has the same key " +
                        $"but capture cseq={keptAgainst.CaptureSequence.ToString(CultureInfo.InvariantCulture)}");
                }
            }
        }

        /// <summary>
        /// The economic match of the ledger dedup, used for a pair where either side names
        /// no capture: same type, occurrence UT within 0.1 s (no window for a
        /// KerbalAssignment) and the same <see cref="GetActionKey"/>. Pure.
        /// </summary>
        internal static bool LegacyEconomicKeyMatches(GameAction existing, GameAction candidate)
        {
            if (existing == null || candidate == null || existing.Type != candidate.Type)
                return false;
            // A KerbalAssignment row's identity is (RecordingId, KerbalName) alone,
            // the same identity MigrateKerbalAssignments preserves ActionIds by. Its
            // UT is NOT part of it: RecordingTreeSplitter's step 2.9 retags a death
            // row to TIP with the ORIGIN's UT, so a later re-commit of TIP (every
            // tree commit re-commits every recording of the tree) would otherwise
            // file a second, fresh-id, untombstoned death row next to the retired
            // one (TOMBSTONED-DEATH-RESURRECTS-ON-RELOAD-AFTER-A-RP-SPLIT, the
            // commit-side mirror of cause (a)). The same key also drops an
            // in-session re-commit whose CONTENT changed (a new end state or
            // window for the same recording and kerbal): the stored row stays
            // until the next load, where MigrateKerbalAssignments repairs its
            // content under the same ActionId.
            if (candidate.Type != GameActionType.KerbalAssignment
                && System.Math.Abs(GetDedupOccurrenceUt(existing) - GetDedupOccurrenceUt(candidate)) > 0.1)
                return false;
            return GetActionKey(existing) == GetActionKey(candidate);
        }

        /// <summary>
        /// Pure: a candidate that names the game-state event it was converted from
        /// (<see cref="GameAction.HasEventCaptureIdentity"/> with a positive
        /// <see cref="GameAction.CaptureSequence"/>). Science rows keep their own capture
        /// identity (<see cref="IsScienceCaptureIdentified"/>).
        /// </summary>
        internal static bool IsEventCaptureIdentified(GameAction action)
        {
            return action != null
                && action.HasEventCaptureIdentity
                && action.CaptureSequence > 0;
        }

        /// <summary>
        /// Pure: a ScienceEarning candidate whose ActionId is a capture identity
        /// (see <see cref="GameAction.HasScienceCaptureIdentity"/>).
        /// </summary>
        internal static bool IsScienceCaptureIdentified(GameAction action)
        {
            return action != null
                && action.Type == GameActionType.ScienceEarning
                && action.HasScienceCaptureIdentity
                && !string.IsNullOrEmpty(action.ActionId);
        }

        private static bool HasScienceCaptureIdentityCandidate(List<GameAction> candidates)
        {
            if (candidates == null) return false;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (IsScienceCaptureIdentified(candidates[i]))
                    return true;
            }
            return false;
        }

        internal static double GetDedupOccurrenceUt(GameAction action)
        {
            return GetDedupOccurrenceUt(action, out _);
        }

        /// <summary>
        /// <see cref="GetDedupOccurrenceUt(GameAction)"/>, also reporting whether the answer came
        /// from a single-precision field (a science row's <c>StartUT</c>), so a caller comparing
        /// it against a double UT can round that UT the same way.
        /// </summary>
        internal static double GetDedupOccurrenceUt(GameAction action, out bool singlePrecision)
        {
            singlePrecision = false;
            if (action.Type == GameActionType.ScienceEarning)
            {
                bool hasFiniteStartUt =
                    !float.IsNaN(action.StartUT) &&
                    !float.IsInfinity(action.StartUT);

                // StartUT defaults to zero on old/synthetic rows. Treat it as populated only
                // when the science row has an explicit window, or for legacy UT=0 migrations.
                bool hasExplicitScienceWindow = action.StartUT > 0f || action.EndUT > 0f;
                bool isLegacyZeroUtSynthetic = System.Math.Abs(action.UT) <= 0.1;
                if (hasFiniteStartUt && (hasExplicitScienceWindow || isLegacyZeroUtSynthetic))
                {
                    singlePrecision = true;
                    return action.StartUT;
                }
            }

            return action.UT;
        }

        /// <summary>Returns the type-specific key field for deduplication matching.</summary>
        internal static string GetActionKey(GameAction a)
        {
            switch (a.Type)
            {
                case GameActionType.ScienceEarning: return a.SubjectId ?? "";
                case GameActionType.ScienceSpending: return a.NodeId ?? "";
                // StrategyScienceDebit carries no id field of its own (the ScienceChanged
                // event's key is the transaction reason, and no strategy identity is
                // available at that seam). Falling through to the default "" would make
                // two exchanges inside the 0.1 s dedup window collapse into one - and the
                // KSC clock is FROZEN, so same-UT pairs are the normal case, not an edge
                // case. The amount is the only available disambiguator. The funds leg
                // (RecordingId alone, above) carries the un-disambiguated version of this
                // same limitation and is deliberately left as-is.
                //
                // TWO HONEST LIMITATIONS of this key, both recorded as residuals on the
                // STRATEGY-SCIENCE-CONVERSION-LEAK entry in docs/dev/todo-and-known-bugs.md:
                //   (1) It disambiguates by AMOUNT, so two exchanges of the SAME amount at
                //       the same frozen KSC UT still collapse into one and one debit is
                //       lost. Distinct amounts survive (pinned by a dedup cell). No
                //       ordinal is invented to close this: an ordinal would have to be
                //       stable across save/load and re-derivable at every producer, and
                //       nothing at this seam can supply one.
                //   (2) It governs the DEDUP path only (DeduplicateAgainstLedger, used by
                //       the discard re-home and the recovery migrations). The direct KSC
                //       door - OnKscSpending straight from GameStateRecorder - performs no
                //       dedup at all, so a stock double-fire of the same ScienceChanged
                //       event would write two rows regardless of this key.
                case GameActionType.StrategyScienceDebit:
                    return (a.RecordingId ?? "") + ":" +
                           a.Cost.ToString("R", CultureInfo.InvariantCulture);
                // Same shape, same two limitations, for the OUTPUT direction.
                case GameActionType.StrategyScienceCredit:
                    return (a.RecordingId ?? "") + ":" +
                           a.ScienceAwarded.ToString("R", CultureInfo.InvariantCulture);
                // Science lab credit: no subject, so the method and the amount are the only
                // disambiguators between two labs paying out at one UT. An orbital survey's
                // bonus also names the surveyed body (absent on a lab row, so a lab key keeps
                // its shape).
                case GameActionType.ScienceLabEarning:
                    return (a.RecordingId ?? "") + ":" + (int)a.Method + ":" +
                           a.ScienceAwarded.ToString("R", CultureInfo.InvariantCulture) +
                           (string.IsNullOrEmpty(a.ScanBodyName) ? "" : ":survey=" + a.ScanBodyName);
                // The query-family reputation OUTPUT leg. Same shape and same two honest
                // limitations as the science rows above: no strategy identity is available
                // at that seam and the KSC clock is FROZEN, so the pre-curve NominalRep is
                // the only disambiguator between two conversions at one UT. Non-strategy
                // reputation earnings keep the historical "" key (they fall through to the
                // default below), so this narrows nothing that was previously unique -
                // BuildStrategyConversionAction is the only production site that builds a
                // ReputationEarning at all, and its rows do not currently reach
                // DeduplicateAgainstLedger; the key exists so a future re-home path cannot
                // silently collapse them.
                case GameActionType.ReputationEarning
                        when a.RepSource == ReputationSource.Strategy:
                    return (a.RecordingId ?? "") + ":" +
                           a.NominalRep.ToString("R", CultureInfo.InvariantCulture);
                // The query-family reputation DEBIT leg, same shape and same standing as
                // its credit sibling directly above. Non-converter reputation penalties
                // keep the historical "" key (they fall through to the default), so this
                // narrows nothing that was previously unique.
                case GameActionType.ReputationPenalty
                        when a.RepPenaltySource == ReputationPenaltySource.StrategyConverter:
                    return (a.RecordingId ?? "") + ":" +
                           a.NominalPenalty.ToString("R", CultureInfo.InvariantCulture);
                // The kerbal-death leg. Keyed by recording + source rather than left on
                // the historical "" so it cannot collide with a DIFFERENT penalty that
                // happens to share its UT - a contract failing BECAUSE the crewed vessel
                // was destroyed lands within the 0.1 s dedup window, and with the shared
                // "" key one of the two rows would be silently dropped as a duplicate.
                // Two kerbal-death rows for the same recording still collapse, which is
                // the dedup this door exists for (a re-commit of the same recording).
                // Non-death penalties keep the "" key, so this narrows nothing that was
                // previously unique.
                case GameActionType.ReputationPenalty
                        when a.RepPenaltySource == ReputationPenaltySource.KerbalDeath:
                    return (a.RecordingId ?? "") + ":KerbalDeath";
                // The declined-contract leg (KSP-SETTINGS-AUDIT S2), keyed like the
                // query-family debit: recording + source + magnitude, so it cannot collapse
                // onto an unrelated penalty sharing its UT under the historical "" key,
                // while a re-commit of the same captured event still dedups.
                case GameActionType.ReputationPenalty
                        when a.RepPenaltySource == ReputationPenaltySource.ContractDecline:
                    return (a.RecordingId ?? "") + ":ContractDecline:" +
                           a.NominalPenalty.ToString("R", CultureInfo.InvariantCulture);
                // The strategy funds OUTPUT leg (query-family yield, Bail-Out Grant). Since
                // a conversion in flight is tagged to the flight, the bare RecordingId key
                // would collide with that recording's own funds earning at the same UT (a
                // recovery payout under Patents Licensing) and the commit's dedup would drop
                // the payout. Source plus magnitude keeps the two apart.
                case GameActionType.FundsEarning
                        when a.FundsSource == FundsEarningSource.Strategy:
                    return (a.RecordingId ?? "") + ":Strategy:" +
                           a.FundsAwarded.ToString("R", CultureInfo.InvariantCulture);
                case GameActionType.FundsEarning: return a.RecordingId ?? "";
                // FundsSpending: RecordingId alone collides when multiple KSC part
                // purchases share a null/empty RecordingId. DedupKey is the part name
                // (populated by ConvertPartPurchased) so each part disambiguates.
                // VesselBuild spending has RecordingId set and DedupKey null, so the
                // composite key stays unique there.
                // The query-family funds DEBIT leg, keyed like its reputation and science
                // siblings above: RecordingId (always null here) plus the magnitude. The
                // general FundsSpending key below is RecordingId + DedupKey, and this door
                // populates NEITHER - every converter debit would collapse onto the single
                // key ":" and onto the exchanger's own StrategyInput spending, which
                // carries the same two nulls. Discriminating by source rather than
                // widening the general key leaves every other spending row's key
                // untouched.
                case GameActionType.FundsSpending
                        when a.FundsSpendingSource == FundsSpendingSource.StrategyConverter:
                    return (a.RecordingId ?? "") + ":" +
                           a.FundsSpent.ToString("R", CultureInfo.InvariantCulture);
                // A part upgrade's key is the upgrade name; the source keeps it apart from a
                // part purchase that happens to share the name.
                case GameActionType.FundsSpending
                        when a.FundsSpendingSource == FundsSpendingSource.PartUpgrade:
                    return (a.RecordingId ?? "") + ":upgrade:" + (a.DedupKey ?? "");
                case GameActionType.FundsSpending: return (a.RecordingId ?? "") + ":" + (a.DedupKey ?? "");
                case GameActionType.MilestoneAchievement: return a.MilestoneId ?? "";
                case GameActionType.FacilityUpgrade:
                case GameActionType.FacilityDestruction:
                case GameActionType.FacilityRepair: return a.FacilityId ?? "";
                case GameActionType.ContractAccept:
                case GameActionType.ContractComplete:
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel: return a.ContractId ?? "";
                // Per (contract, parameter): two parameters of one contract paid in the same
                // instant are two rows, and the contract's own completion never matches one.
                case GameActionType.ContractParameterComplete:
                    return (a.ContractId ?? "") + "|" + (a.ContractParameterId ?? "");
                case GameActionType.KerbalHire: return a.KerbalName ?? "";
                // A dismissal: the kerbal name, like the hire (the UT and the capture number
                // tell two dismissals of one kerbal apart).
                case GameActionType.KerbalDismissal: return a.KerbalName ?? "";
                // KerbalExperience: the kerbal name, mirroring KerbalHire. Falling through
                // to the default "" would make one recovery's crew collapse into a single
                // row - every crew member's XP row shares the recovery's UT exactly (one
                // ArchiveFlightLog pass, one Planetarium.GetUniversalTime() read), so
                // Type + UT alone matches every one of them against every other. The name
                // is the only per-row identity at that seam and it is always populated
                // (ConvertExperienceGained refuses a nameless event). Pinned by the
                // two-crew cell in LedgerRecoveryKerbalExperienceTests.
                case GameActionType.KerbalExperience: return a.KerbalName ?? "";
                // KerbalAssignment: per (recording, kerbal). Falling through to "" made
                // every assignment row collide with every other one inside the 0.1 s
                // window, whatever the kerbal or recording (KERBAL-ASSIGNMENT-DEDUP-KEY-IS-EMPTY,
                // measured on RF-12S: a re-fly provisional's crew rows at UT 131.9 were
                // dropped against the PRIOR re-fly's rows at 131.88, so a re-fly that killed
                // them again lost its own death row; two split children starting at one UT
                // lost the second child's crew the same way). A re-commit of the SAME
                // recording still dedups. The only producer that sends this type through a
                // dedup is OnRecordingCommitted step 3c; MigrateKerbalAssignments compares
                // whole per-recording row sets and never passes through here.
                case GameActionType.KerbalAssignment:
                    return (a.RecordingId ?? "") + "|" + (a.KerbalName ?? "");
                // KerbalRecovered: per (owner recording, kerbal), like KerbalAssignment,
                // so a two-crew recovery keeps both rows and the same recovery event
                // delivered twice collapses to one.
                case GameActionType.KerbalRecovered:
                    return (a.RecordingId ?? "") + "|" + (a.KerbalName ?? "");
                // VesselRecovered: one row per owner recording; the same recovery
                // delivered twice collapses to one, two owning trees keep both.
                case GameActionType.VesselRecovered:
                    return a.RecordingId ?? "";
                // ResourceScanUnlocked: per (body, biome). The default "" would collapse
                // two bodies or two biomes unlocked at the same UT into one row.
                case GameActionType.ResourceScanUnlocked:
                    return (a.ScanBodyName ?? "") + "|" + (a.ScanBiomeName ?? "");
                default: return "";
            }
        }


        /// <summary>
        /// Creates FundsSpending (VesselBuild) and FundsEarning (Recovery) actions
        /// from the recording's resource data. Looks up the recording by ID from
        /// CommittedRecordings (it has already been committed before this is called).
        /// </summary>
        /// <param name="recordingId">Recording ID to look up.</param>
        /// <param name="startUT">Start UT of the recording.</param>
        /// <param name="endUT">End UT of the recording.</param>
        /// <returns>List of 0–2 actions (build cost and/or recovery).</returns>
        internal static List<GameAction> CreateVesselCostActions(string recordingId, double startUT, double endUT)
        {
            var rec = FindRecordingById(recordingId);
            GameAction adoptionCandidate;
            LedgerRolloutAdoption.RolloutAdoptionContext adoptionContext;
            bool consumesPayout;
            var result = BuildVesselCostActions(
                rec, recordingId, startUT, endUT, Ledger.Actions,
                out adoptionCandidate, out adoptionContext, out consumesPayout);
            if (adoptionCandidate != null)
                LedgerRolloutAdoption.ApplyRolloutAdoption(
                    adoptionCandidate, recordingId, startUT, adoptionContext);
            if (consumesPayout)
                InFlightRecoveryRequest.TryConsumeAwaitingStockPayout(recordingId);
            return result;
        }

        /// <summary>
        /// The read-only body of <see cref="CreateVesselCostActions"/>: the build cost and
        /// recovery rows of <paramref name="rec"/>, plus the rollout row the recording
        /// would adopt (<paramref name="adoptionCandidate"/>, unchanged) and whether the
        /// recovery branch read a pending stock-payout mark
        /// (<paramref name="consumesAwaitingStockPayout"/>, left in place). The caller
        /// applies both.
        /// </summary>
        internal static List<GameAction> BuildVesselCostActions(
            Recording rec,
            string recordingId,
            double startUT,
            double endUT,
            IReadOnlyList<GameAction> ledgerActions,
            out GameAction adoptionCandidate,
            out LedgerRolloutAdoption.RolloutAdoptionContext adoptionContext,
            out bool consumesAwaitingStockPayout)
        {
            var result = new List<GameAction>();
            adoptionCandidate = null;
            adoptionContext = default(LedgerRolloutAdoption.RolloutAdoptionContext);
            consumesAwaitingStockPayout = false;

            if (rec == null)
            {
                ParsekLog.Verbose(Tag,
                    $"CreateVesselCostActions: recording '{recordingId}' not found in CommittedRecordings — skipping");
                return result;
            }

            if (rec.Points.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    $"CreateVesselCostActions: recording '{recordingId}' has no points — skipping");
                return result;
            }

            // Vessel build cost = PreLaunchFunds - first point's funds
            // (KSP deducts vessel cost before the first frame is recorded)
            double firstFunds = rec.Points[0].funds;
            double buildCost = rec.PreLaunchFunds - firstFunds;

            // #445: adoption gate, see AddVesselBuildCostActions.
            if (CanRecordingAdoptRolloutAction(rec))
            {
                adoptionCandidate = LedgerRolloutAdoption.FindRolloutAdoptionCandidate(
                    recordingId, startUT, rec, ledgerActions, out adoptionContext);
            }

            AddVesselBuildCostActions(result, recordingId, startUT, rec, firstFunds, buildCost, adoptionCandidate);
            consumesAwaitingStockPayout = AddVesselRecoveryCostActions(result, recordingId, endUT, rec);

            return result;
        }

        private static void AddVesselBuildCostActions(
            List<GameAction> result,
            string recordingId,
            double startUT,
            Recording rec,
            double firstFunds,
            double buildCost,
            GameAction adopted)
        {
            // #445: KSP fires TransactionReasons.VesselRollout BEFORE PreLaunchFunds is
            // captured (rollout deducts at editor->launchpad transition; CapturePreLaunchResources
            // runs later in the FLIGHT scene). So in the normal pad-start record flow the
            // rollout cost has already been written to the ledger by OnVesselRolloutSpending
            // and buildCost above is ~0. Adopt that rollout action onto this recording so the
            // ledger associates the cost with the trip instead of leaving it null-tagged.
            //
            // Claim gate: only recordings that STARTED from a launch-ready prelaunch state
            // are allowed to adopt. Without this, a user who presses Record after liftoff
            // (or later in ascent) would incorrectly steal the launch cost onto a mid-flight
            // recording, and deleting that recording would wrongly remove the real vessel
            // build cost from the ledger.
            // The caller found the row (FindRolloutAdoptionCandidate) and adopts it.
            if (buildCost > 0)
            {
                if (adopted != null)
                {
                    // Rare case: rollout cost was written and there's still a positive
                    // delta between PreLaunchFunds and the first frame (e.g. another
                    // KSP-side deduction landed during launch). Emit the residual on top
                    // of the adopted rollout action so neither cost goes missing.
                    ParsekLog.Verbose(Tag,
                        $"CreateVesselCostActions: residual buildCost={buildCost:F0} on top of " +
                        $"adopted rollout (rolloutCost={adopted.FundsSpent:F0}) for '{recordingId}'");
                }

                result.Add(new GameAction
                {
                    UT = startUT,
                    Type = GameActionType.FundsSpending,
                    RecordingId = recordingId,
                    FundsSpent = (float)buildCost,
                    FundsSpendingSource = FundsSpendingSource.VesselBuild
                });

                ParsekLog.Verbose(Tag,
                    $"CreateVesselCostActions: vessel build cost={buildCost:F0} " +
                    $"(preLaunch={rec.PreLaunchFunds:F0}, first={firstFunds:F0}) for '{recordingId}'");
            }
            else if (adopted == null)
            {
                ParsekLog.Verbose(Tag,
                    $"CreateVesselCostActions: zero build cost and no rollout adoption " +
                    $"(preLaunch={rec.PreLaunchFunds:F0}, first={firstFunds:F0}) for '{recordingId}'");
            }
        }

        /// <returns>True when the recovery branch read the pending stock-payout mark the
        /// caller then consumes.</returns>
        private static bool AddVesselRecoveryCostActions(
            List<GameAction> result,
            string recordingId,
            double endUT,
            Recording rec)
        {
            bool readsAwaitingPayout = false;
            // Recovery funds: prefer the paired FundsChanged(VesselRecovery) event near
            // the recording end UT. This covers recoveries that happen after the last
            // recorded point but before commit outside FLIGHT (e.g. post-flight KSC
            // recovery). Fall back to the legacy "last point minus penultimate point"
            // heuristic only when no paired funds event is present.
            if (rec.TerminalStateValue == TerminalState.Recovered)
            {
                RecoveredVesselIdentity recoveryIdentity =
                    RecoveredVesselIdentity.FromRawName(rec.VesselName);
                if (TryFindRecoveryFundsEvent(
                        GameStateStore.Events,
                        endUT,
                        skipConsumed: false,
                        recoveryIdentity,
                        out GameStateEvent pairedRecoveryEvent,
                        out string pairedDedupKey))
                {
                    AddPairedRecoveryFundsAction(
                        result, recordingId, pairedRecoveryEvent, pairedDedupKey);
                }
                else if (InFlightRecoveryRequest.IsAwaitingStockPayout(recordingId))
                {
                    readsAwaitingPayout = true;
                    // Stamped Recovered by the in-flight Recover request: stock pays at the
                    // Space Center AFTER this commit, and the #444 path
                    // (OnVesselRecoveryFunds) writes that payout when onVesselRecovered
                    // fires. The last-two-points heuristic would read an in-flight funds
                    // change (a contract paid on landing) as a second recovery payout.
                    ParsekLog.Verbose(Tag,
                        $"CreateVesselCostActions: '{recordingId}' recovered in flight - " +
                        "payout not yet paid, the stock recovery writes it (no heuristic row)");
                }
                else if (rec.Points.Count >= 2)
                {
                    AddHeuristicRecoveryFundsAction(result, recordingId, endUT, rec);
                }
            }
            return readsAwaitingPayout;
        }

        /// <summary>
        /// <see cref="AddVesselRecoveryCostActions"/>'s paired-event branch: a positive
        /// funds delta on the paired <c>FundsChanged(VesselRecovery)</c> event adds a
        /// Recovery <see cref="GameActionType.FundsEarning"/> row at the event's UT, keyed
        /// by its dedup key; a non-positive one is logged and skipped.
        /// </summary>
        internal static void AddPairedRecoveryFundsAction(
            List<GameAction> result, string recordingId,
            GameStateEvent pairedRecoveryEvent, string pairedDedupKey)
        {
            double pairedRecoveryAmount =
                pairedRecoveryEvent.valueAfter - pairedRecoveryEvent.valueBefore;
            if (pairedRecoveryAmount > 0)
            {
                result.Add(new GameAction
                {
                    UT = pairedRecoveryEvent.ut,
                    Type = GameActionType.FundsEarning,
                    RecordingId = recordingId,
                    FundsAwarded = (float)pairedRecoveryAmount,
                    FundsSource = FundsEarningSource.Recovery,
                    DedupKey = pairedDedupKey
                });

                ParsekLog.Verbose(Tag,
                    $"CreateVesselCostActions: paired recovery funds={pairedRecoveryAmount:F0} " +
                    $"(before={pairedRecoveryEvent.valueBefore:F0}, after={pairedRecoveryEvent.valueAfter:F0}) " +
                    $"for '{recordingId}'");
            }
            else
            {
                ParsekLog.Verbose(Tag,
                    $"CreateVesselCostActions: paired recovery event for '{recordingId}' " +
                    $"at ut={pairedRecoveryEvent.ut.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"has non-positive delta {pairedRecoveryAmount.ToString("F1", CultureInfo.InvariantCulture)} — skipping");
            }
        }

        /// <summary>
        /// <see cref="AddVesselRecoveryCostActions"/>'s fallback: with no paired event and
        /// no in-flight payout pending, a positive change between the recording's last two
        /// points' funds adds a Recovery <see cref="GameActionType.FundsEarning"/> row at
        /// <paramref name="endUT"/>. The caller has checked there are at least two points.
        /// </summary>
        internal static void AddHeuristicRecoveryFundsAction(
            List<GameAction> result, string recordingId, double endUT, Recording rec)
        {
            double lastFunds = rec.Points[rec.Points.Count - 1].funds;
            double penultimateFunds = rec.Points[rec.Points.Count - 2].funds;
            double recoveryAmount = lastFunds - penultimateFunds;

            if (recoveryAmount > 0)
            {
                result.Add(new GameAction
                {
                    UT = endUT,
                    Type = GameActionType.FundsEarning,
                    RecordingId = recordingId,
                    FundsAwarded = (float)recoveryAmount,
                    FundsSource = FundsEarningSource.Recovery
                });

                ParsekLog.Verbose(Tag,
                    $"CreateVesselCostActions: recovery funds={recoveryAmount:F0} " +
                    $"(last={lastFunds:F0}, penultimate={penultimateFunds:F0}) for '{recordingId}'");
            }
        }

        /// <summary>
        /// Creates KerbalAssignment actions from the recording's vessel crew data.
        /// Each crew member in the vessel snapshot becomes a KerbalAssignment action
        /// recording their role, mission start/end UT, and end state.
        /// This populates the ledger for future full KerbalsModule integration.
        /// </summary>
        internal static List<GameAction> CreateKerbalAssignmentActions(
            string recordingId, double startUT, double endUT)
        {
            var rec = FindRecordingById(recordingId);
            if (rec == null) return new List<GameAction>();

            if (NeedsCrewEndStatePopulation(rec))
                KerbalsModule.PopulateCrewEndStates(rec);

            return BuildKerbalAssignmentActions(rec, recordingId, startUT, endUT);
        }

        /// <summary>
        /// The read-only body of <see cref="CreateKerbalAssignmentActions"/>: reads
        /// <paramref name="rec"/>'s crew and crew end states as they are (the caller
        /// populates the end states, on the recording or on a copy).
        /// </summary>
        internal static List<GameAction> BuildKerbalAssignmentActions(
            Recording rec, string recordingId, double startUT, double endUT)
        {
            var result = new List<GameAction>();
            if (rec == null) return result;

            // Extract crew from vessel snapshot (same source KerbalsModule.ProcessAction uses)
            var crew = ExtractCrewFromRecording(rec);
            if (crew == null || crew.Count == 0) return result;

            for (int i = 0; i < crew.Count; i++)
            {
                var member = crew[i];
                result.Add(new GameAction
                {
                    UT = startUT,
                    Type = GameActionType.KerbalAssignment,
                    RecordingId = recordingId,
                    KerbalName = member.Name,
                    KerbalRole = member.Role,
                    StartUT = (float)startUT,
                    EndUT = (float)endUT,
                    KerbalEndStateField = member.EndState,
                    Sequence = i + 1
                });
            }

            if (result.Count > 0)
            {
                ParsekLog.Info(Tag,
                    $"CreateKerbalAssignmentActions: {result.Count} crew members from '{recordingId}'");
            }

            return result;
        }

        /// <summary>
        /// Creates the kerbal-death <see cref="GameActionType.ReputationPenalty"/> row
        /// for a recording, or none. AT MOST ONE row per recording no matter how many
        /// crew died: stock applies one <c>VesselLoss</c> reputation hit per vessel
        /// loss, not one per kerbal.
        ///
        /// <para>
        /// SEPARATE FROM <see cref="CreateKerbalAssignmentActions"/> ON PURPOSE. That
        /// method is also the source of truth for <see cref="MigrateKerbalAssignments"/>,
        /// which compares its output against the ledger's KerbalAssignment slice and then
        /// calls <c>Ledger.ReplaceActionsForRecording(KerbalAssignment, ...)</c> with the
        /// whole returned list. A foreign row in that list would (a) make
        /// <c>KerbalAssignmentActionsMatch</c> fail on every load and (b) be RE-INSERTED
        /// on every load while only KerbalAssignment rows are removed - an unbounded
        /// duplicate leak. Keeping the penalty in its own producer leaves that migration
        /// exactly as it was.
        /// </para>
        ///
        /// <para>
        /// The decision itself is pure (<see cref="KerbalDeathRepPenalty.Decide"/>); this
        /// wrapper supplies the recording's crew end states and the captured event store,
        /// and owns the logging.
        /// </para>
        /// </summary>
        /// <param name="recordingId">Recording to derive the row for.</param>
        /// <param name="startUT">Recording start UT (unused by the decision; kept for call-site symmetry).</param>
        /// <param name="endUT">Recording end UT - the reference point a death lands at.</param>
        /// <param name="repSeedOrigin">
        /// Where the career's reputation seed came from as of THIS commit, established
        /// before this producer ran. It decides
        /// <see cref="GameAction.InsideReputationSeed"/> on the emitted row - the one
        /// question the module cannot answer for itself, because game UT does not order
        /// events across a rewind.
        /// </param>
        internal static List<GameAction> CreateKerbalDeathRepPenaltyActions(
            string recordingId, double startUT, double endUT,
            ReputationSeedOrigin repSeedOrigin)
        {
            var rec = FindRecordingById(recordingId);
            if (rec == null) return new List<GameAction>();

            if (NeedsCrewEndStatePopulation(rec))
                KerbalsModule.PopulateCrewEndStates(rec);

            return BuildKerbalDeathRepPenaltyActions(rec, recordingId, startUT, endUT, repSeedOrigin);
        }

        /// <summary>
        /// The read-only body of <see cref="CreateKerbalDeathRepPenaltyActions"/>: reads
        /// <paramref name="rec"/>'s crew end states as they are.
        /// </summary>
        internal static List<GameAction> BuildKerbalDeathRepPenaltyActions(
            Recording rec, string recordingId, double startUT, double endUT,
            ReputationSeedOrigin repSeedOrigin)
        {
            var result = new List<GameAction>();
            if (rec == null) return result;

            var crew = ExtractCrewFromRecording(rec);
            if (crew == null || crew.Count == 0) return result;

            var endStates = new List<KerbalEndState>(crew.Count);
            for (int i = 0; i < crew.Count; i++)
                endStates.Add(crew[i].EndState);

            var decision = KerbalDeathRepPenalty.Decide(
                endStates, GameStateStore.Events, recordingId, endUT);

            if (decision.DeadCount == 0)
                return result;

            if (!decision.Emit)
            {
                // Named refusal, not a silent one: a fixture-injected recording, a
                // suppressed event, and a sub-threshold penalty all land here, and the
                // reason is the only way to tell them apart from a producer defect.
                ParsekLog.Info(Tag,
                    $"KerbalDeath rep penalty: {decision.Reason} for recording='{recordingId}' " +
                    $"(dead={decision.DeadCount.ToString(CultureInfo.InvariantCulture)}) - no row");
                return result;
            }

            if (decision.CandidateCount > 1)
            {
                ParsekLog.Info(Tag,
                    $"KerbalDeath rep penalty: {decision.CandidateCount.ToString(CultureInfo.InvariantCulture)} " +
                    $"VesselLoss events scoped to recording='{recordingId}' - took the one nearest " +
                    $"endUT={endUT.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"(ut={decision.UT.ToString("R", CultureInfo.InvariantCulture)})");
            }

            bool insideSeed = ReputationSeedMembership.IsInsideReputationSeed(repSeedOrigin);

            result.Add(new GameAction
            {
                UT = decision.UT,
                Type = GameActionType.ReputationPenalty,
                RecordingId = recordingId,
                RepPenaltySource = ReputationPenaltySource.KerbalDeath,
                NominalPenalty = decision.Magnitude,
                InsideReputationSeed = insideSeed,
                CaptureSequence = decision.CaptureSequence
            });

            // A CANDIDATE, not a decision: this producer runs on every commit of the
            // recording, and on a re-commit DeduplicateAgainstLedger drops the row it
            // returns because the ledger already carries one. The line would otherwise
            // read like a fresh penalty was filed each time. The truth for "did a row
            // land" is OnRecordingCommitted's "Committed recording ... dedup=N" summary.
            ParsekLog.Info(Tag,
                $"KerbalDeath rep penalty: recording='{recordingId}' " +
                $"applied={decision.Magnitude.ToString("R", CultureInfo.InvariantCulture)} " +
                $"ut={decision.UT.ToString("R", CultureInfo.InvariantCulture)} " +
                $"dead={decision.DeadCount.ToString(CultureInfo.InvariantCulture)} " +
                $"repSeedOrigin={repSeedOrigin} insideRepSeed={insideSeed} " +
                "-> ReputationPenalty(KerbalDeath) candidate (dedup decides)");

            return result;
        }

        /// <summary>
        /// Holds extracted crew member info for KerbalAssignment action creation.
        /// </summary>
        internal struct CrewInfo
        {
            public string Name;
            public string Role;
            public KerbalEndState EndState;
        }

        /// <summary>
        /// The kerbal a crew row names. <paramref name="ownerName"/> is the seat's kerbal after
        /// the stand-in reverse map (the slot owner), <paramref name="seatedName"/> the one the
        /// snapshot seated. A death is filed under the kerbal who was seated, so a stand-in's
        /// crash never kills the slot owner he stood in for; every other end state keeps the
        /// slot owner. Pure.
        /// </summary>
        internal static string ResolveCrewRowName(
            string ownerName, string seatedName, KerbalEndState endState)
        {
            if (endState != KerbalEndState.Dead
                || string.IsNullOrEmpty(seatedName)
                || string.Equals(ownerName, seatedName, StringComparison.Ordinal))
                return ownerName;
            ParsekLog.Verbose(Tag,
                $"Crew row: stand-in '{seatedName}' died in the seat of '{ownerName}' - " +
                "the death is filed under the stand-in, the slot owner keeps his slot");
            return seatedName;
        }

        /// <summary>
        /// Extracts crew members from a recording's vessel snapshot data,
        /// mirroring the crew extraction logic in KerbalsModule.ProcessAction.
        /// Uses GhostVisualSnapshot (recording-start state) as the primary source
        /// for crew names (same as PopulateCrewEndStates), falls back to VesselSnapshot.
        /// Stand-in names are reverse-mapped back to original slot owners so
        /// KerbalAssignment actions use the same identity as CrewEndStates.
        /// Roles come from KerbalsModule.FindTraitForKerbal (roster, tourist rows, baselines;
        /// a tourist reads "Tourist").
        /// End states come from rec.CrewEndStates if populated.
        /// </summary>
        internal static List<CrewInfo> ExtractCrewFromRecording(Recording rec)
        {
            var result = new List<CrewInfo>();
            if (rec == null) return result;

            // Prefer GhostVisualSnapshot (recording-start crew), fall back to VesselSnapshot
            var snapshot = rec.GhostVisualSnapshot ?? rec.VesselSnapshot;
            var names = CrewReservationManager.ExtractCrewFromSnapshot(snapshot);
            // EVA fallback: snapshot crew extraction returns empty for EVA vessels
            if (names.Count == 0 && !string.IsNullOrEmpty(rec.EvaCrewName))
                names.Add(rec.EvaCrewName);
            if (names.Count == 0) return result;

            // Match PopulateCrewEndStates: later recordings may already contain stand-in
            // names after a prior commit swapped live vessel crew.
            var seatedNames = new List<string>(names);
            KerbalsModule.ReverseMapCrewNames(
                names,
                CrewReservationManager.CrewReplacements,
                null);
            // The recorder's snapshots already carry the owner in a stand-in's seat
            // (TryBackupSnapshot reverse-maps them), so who sat there is read from the
            // seat record the reverse map left in the snapshot.
            for (int i = 0; i < seatedNames.Count; i++)
            {
                if (!string.Equals(seatedNames[i], names[i], StringComparison.Ordinal))
                    continue;
                string standIn = KerbalsModule.ReadSeatedStandIn(rec.GhostVisualSnapshot, names[i])
                    ?? KerbalsModule.ReadSeatedStandIn(rec.VesselSnapshot, names[i]);
                if (!string.IsNullOrEmpty(standIn))
                    seatedNames[i] = standIn;
            }

            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];

                KerbalEndState endState = KerbalEndState.Unknown;
                if (rec.CrewEndStates != null)
                    rec.CrewEndStates.TryGetValue(name, out endState);

                // A stand-in who died in the seat is the one who died (design section 9.11:
                // the slot owner keeps his slot and the chain continues). Every other end
                // state keeps the slot owner's name, so the owner's hold reads as before.
                name = ResolveCrewRowName(name, seatedNames[i], endState);

                // A tourist keeps his row (role "Tourist"): it is the durable record of the
                // passenger seat, and the kerbal-death count reads the same crew list.
                string role = KerbalsModule.FindTraitForKerbal(name);

                result.Add(new CrewInfo
                {
                    Name = name,
                    Role = role,
                    EndState = endState
                });
            }

            return result;
        }

        internal static bool NeedsCrewEndStatePopulation(Recording rec)
        {
            if (rec == null || rec.CrewEndStatesResolved || rec.CrewEndStates != null)
                return false;

            if (rec.VesselSnapshot != null
                || !string.IsNullOrEmpty(rec.EvaCrewName)
                || KerbalsModule.ShouldUseGhostOnlyChainHandoffEndState(rec))
                return true;

            // A DESTROYED vessel has no end-of-recording VesselSnapshot, so the
            // recording-start GhostVisualSnapshot is the only crew source left.
            // PopulateCrewEndStates and ExtractCrewFromRecording both already
            // prefer GhostVisualSnapshot; without this admission the one class
            // of recording where the end state matters most (crew died with the
            // vessel) never got populated and its ledger rows defaulted to
            // Unknown instead of Dead. Admitted ONLY for terminal states whose
            // inference does not consult the end-of-recording crew set
            // (Destroyed -> Dead, Recovered -> Recovered): for intact terminal
            // states InferCrewEndState reads "absent from the end snapshot" as
            // EVA'd-and-lost Dead, which with no VesselSnapshot at all would
            // falsely kill live crew on ghost-only stable chain tips (pinned by
            // CreateKerbalAssignmentActions_GhostOnlyStableChainTip_DoesNotForceRecovered).
            if (rec.GhostVisualSnapshot != null
                && (rec.TerminalStateValue == TerminalState.Destroyed
                    || rec.TerminalStateValue == TerminalState.Recovered))
            {
                // One-shot per recording: every caller populates immediately
                // after a true result, and population always sets
                // CrewEndStates or CrewEndStatesResolved (persisted). Crewless
                // ghost snapshots (destroyed debris) log at Verbose so the
                // first recalc over a pre-fix save does not flood Info with
                // one line per debris recording.
                bool hasCrew = CrewReservationManager
                    .ExtractCrewFromSnapshot(rec.GhostVisualSnapshot).Count > 0;
                string message =
                    $"NeedsCrewEndStatePopulation: recording='{rec.RecordingId}' " +
                    $"admitted via ghost-visual-only crew source (no vessel snapshot, " +
                    $"terminalState={rec.TerminalStateValue.Value}, hasCrew={hasCrew})";
                if (hasCrew)
                    ParsekLog.Info(Tag, message);
                else
                    ParsekLog.Verbose(Tag, message);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Finds a committed recording by its ID. Returns null if not found.
        /// </summary>
        internal static Recording FindRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;

            var recordings = RecordingStore.CommittedRecordings;
            if (recordings == null) return null;

            for (int i = 0; i < recordings.Count; i++)
            {
                if (recordings[i].RecordingId == recordingId)
                    return recordings[i];
            }

            return null;
        }
    }
}
