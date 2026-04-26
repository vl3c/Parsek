using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Converts recorded <see cref="GameStateEvent"/> data into <see cref="GameAction"/> entries
    /// for the ledger timeline. Each convertible event type maps to a specific GameActionType;
    /// events that are purely informational (resource deltas, status changes) are skipped.
    ///
    /// Also converts <see cref="PendingScienceSubject"/> entries into ScienceEarning actions.
    /// ContractAccepted backfill may consult the stored contract snapshot when older event
    /// detail rows predate newer fields.
    /// </summary>
    internal static class GameStateEventConverter
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private const string Tag = "GameStateEventConverter";

        /// <summary>
        /// Converts a list of GameStateEvents into GameActions, filtering to the given UT range
        /// and skipping non-convertible event types. When <paramref name="recordingId"/> is
        /// supplied, only events tagged to that recording are converted. Returns only non-null
        /// conversions.
        /// </summary>
        /// <param name="events">Source events (may contain non-convertible types).</param>
        /// <param name="recordingId">Recording that produced these events.</param>
        /// <param name="startUT">Inclusive lower bound on event UT.</param>
        /// <param name="endUT">Inclusive upper bound on event UT.</param>
        internal static List<GameAction> ConvertEvents(
            IReadOnlyList<GameStateEvent> events, string recordingId, double startUT, double endUT)
        {
            var result = new List<GameAction>();

            if (events == null || events.Count == 0)
            {
                ParsekLog.Verbose(Tag, "ConvertEvents: empty or null events list, returning 0 actions");
                return result;
            }

            int skipped = 0;
            int outOfRange = 0;
            int converted = 0;
            int sequence = 1;
            var skippedByType = new Dictionary<GameStateEventType, int>();

            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];

                if (evt.ut < startUT || evt.ut > endUT)
                {
                    outOfRange++;
                    continue;
                }

                if (!EventMatchesRecordingScope(evt, recordingId))
                {
                    skipped++;
                    IncrementEventTypeCount(skippedByType, evt.eventType);
                    continue;
                }

                var action = ConvertEvent(evt, recordingId);
                if (action != null)
                {
                    action.Sequence = sequence++;
                    result.Add(action);
                    converted++;
                }
                else
                {
                    skipped++;
                    IncrementEventTypeCount(skippedByType, evt.eventType);
                }
            }

            ParsekLog.Info(Tag,
                FormatConvertEventsSummary(
                    converted,
                    skipped,
                    outOfRange,
                    events.Count,
                    recordingId,
                    skippedByType));

            return result;
        }

        internal static string FormatConvertEventsSummary(
            int converted,
            int skipped,
            int outOfRange,
            int total,
            string recordingId,
            IDictionary<GameStateEventType, int> skippedByType)
        {
            return string.Format(IC,
                "ConvertEvents: converted={0}, skipped={1}, outOfRange={2}, total={3}, recordingId={4}, skippedByType={5}",
                converted,
                skipped,
                outOfRange,
                total,
                recordingId ?? "(none)",
                FormatEventTypeCounts(skippedByType));
        }

        internal static string FormatEventTypeCounts(IDictionary<GameStateEventType, int> counts)
        {
            if (counts == null || counts.Count == 0)
                return "(none)";

            var keys = new List<GameStateEventType>(counts.Keys);
            keys.Sort((left, right) => string.CompareOrdinal(left.ToString(), right.ToString()));
            var parts = new List<string>(keys.Count);
            foreach (var key in keys)
            {
                int count = counts[key];
                if (count <= 0)
                    continue;
                parts.Add(key + ":" + count.ToString(IC));
            }

            return parts.Count == 0 ? "(none)" : string.Join(",", parts);
        }

        private static void IncrementEventTypeCount(
            IDictionary<GameStateEventType, int> counts,
            GameStateEventType eventType)
        {
            int current;
            counts.TryGetValue(eventType, out current);
            counts[eventType] = current + 1;
        }

        // Mirrored in LedgerOrchestrator.EventMatchesRecordingScope; keep the two in sync.
        private static bool EventMatchesRecordingScope(GameStateEvent evt, string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return true;

            string eventRecordingId = evt.recordingId ?? "";
            return string.Equals(eventRecordingId, recordingId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Converts a single GameStateEvent to a GameAction.
        /// Returns null for event types that have no GameAction equivalent
        /// (FundsChanged, ScienceChanged, ReputationChanged, CrewStatusChanged,
        /// CrewRemoved, ContractOffered, ContractDeclined, FacilityDowngraded).
        /// </summary>
        internal static GameAction ConvertEvent(GameStateEvent evt, string recordingId)
        {
            switch (evt.eventType)
            {
                case GameStateEventType.TechResearched:
                    return ConvertTechResearched(evt, recordingId);

                case GameStateEventType.PartPurchased:
                    return ConvertPartPurchased(evt, recordingId);

                case GameStateEventType.FacilityUpgraded:
                    return ConvertFacilityUpgraded(evt, recordingId);

                case GameStateEventType.BuildingDestroyed:
                    return ConvertBuildingDestroyed(evt, recordingId);

                case GameStateEventType.BuildingRepaired:
                    return ConvertBuildingRepaired(evt, recordingId);

                case GameStateEventType.CrewHired:
                    return ConvertCrewHired(evt, recordingId);

                case GameStateEventType.ContractAccepted:
                    return ConvertContractAccepted(evt, recordingId);

                case GameStateEventType.ContractCompleted:
                    return ConvertContractCompleted(evt, recordingId);

                case GameStateEventType.ContractFailed:
                    return ConvertContractFailed(evt, recordingId);

                case GameStateEventType.ContractCancelled:
                    return ConvertContractCancelled(evt, recordingId);

                case GameStateEventType.MilestoneAchieved:
                    return ConvertMilestoneAchieved(evt, recordingId);

                case GameStateEventType.KerbalRescued:
                    return ConvertKerbalRescued(evt, recordingId);

                case GameStateEventType.StrategyActivated:
                    return ConvertStrategyActivated(evt, recordingId);

                case GameStateEventType.StrategyDeactivated:
                    return ConvertStrategyDeactivated(evt, recordingId);

                // Skipped event types — no GameAction equivalent.
                //
                // DO NOT try to "fix" this by re-emitting FundsChanged/ScienceChanged/
                // ReputationChanged as FundsEarning/ScienceEarning/ReputationEarning.
                // The earning values already flow through dedicated channels:
                //   - Recovery        via LedgerOrchestrator.CreateVesselCostActions
                //   - ContractReward  via ConvertContractCompleted (reads detail)
                //   - Milestone       via ConvertMilestoneAchieved (reads detail)
                //   - ScienceEarning  via ConvertScienceSubjects (PendingScienceSubjects)
                // Re-emitting from the Changed events would double-count against every
                // one of those channels at the same UT, and GetActionKey for FundsEarning
                // keys off RecordingId alone so dedup would NOT save us.
                //
                // #394 reconciliation: LedgerOrchestrator.ReconcileEarningsWindow runs at
                // commit time and WARNs if these dropped deltas disagree with the effective
                // emitted actions — so regressions in any channel surface loudly without
                // needing to re-emit here.
                case GameStateEventType.FundsChanged:
                case GameStateEventType.ScienceChanged:
                case GameStateEventType.ReputationChanged:
                case GameStateEventType.CrewStatusChanged:
                case GameStateEventType.CrewRemoved:
                case GameStateEventType.ContractOffered:
                case GameStateEventType.ContractDeclined:
                case GameStateEventType.FacilityDowngraded:
                    return null;

                default:
                    ParsekLog.Warn(Tag,
                        $"Unknown event type {evt.eventType} — skipping");
                    return null;
            }
        }

        /// <summary>
        /// Converts a list of PendingScienceSubjects into ScienceEarning GameActions.
        /// Each subject becomes a ScienceEarning action anchored at commit/end UT while
        /// also carrying the subject's observed science window for reconciliation.
        /// </summary>
        /// <param name="subjects">Science subjects to convert.</param>
        /// <param name="recordingId">Recording that produced these subjects.</param>
        /// <param name="startUT">Owning recording start UT.</param>
        /// <param name="endUT">Owning recording end/commit UT.</param>
        internal static List<GameAction> ConvertScienceSubjects(
            IReadOnlyList<PendingScienceSubject> subjects,
            string recordingId,
            double startUT,
            double endUT)
        {
            var result = new List<GameAction>();

            if (subjects == null || subjects.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    "ConvertScienceSubjects: empty or null subjects list, returning 0 actions");
                return result;
            }

            int sequence = 1;
            int taggedMatches = 0;
            int untaggedInWindow = 0;
            int skippedCrossRecording = 0;
            int skippedOutsideWindow = 0;

            for (int i = 0; i < subjects.Count; i++)
            {
                var subj = subjects[i];

                if (string.IsNullOrEmpty(subj.subjectId))
                {
                    ParsekLog.Warn(Tag,
                        $"ConvertScienceSubjects: skipping subject at index {i} — empty subjectId");
                    continue;
                }

                if (subj.science <= 0f)
                {
                    ParsekLog.Verbose(Tag,
                        $"ConvertScienceSubjects: skipping subject '{subj.subjectId}' — " +
                        $"non-positive science ({subj.science.ToString("R", IC)})");
                    continue;
                }

                if (!TryResolveScienceSubjectStartUt(
                        subj,
                        recordingId,
                        startUT,
                        endUT,
                        out double resolvedStartUt,
                        out bool matchedViaUntaggedWindow,
                        out bool skippedDueToCrossRecording))
                {
                    if (skippedDueToCrossRecording)
                        skippedCrossRecording++;
                    else
                        skippedOutsideWindow++;
                    continue;
                }

                if (matchedViaUntaggedWindow)
                    untaggedInWindow++;
                else
                    taggedMatches++;

                result.Add(new GameAction
                {
                    UT = endUT,
                    Type = GameActionType.ScienceEarning,
                    RecordingId = recordingId,
                    SubjectId = subj.subjectId,
                    ScienceAwarded = subj.science,
                    Method = ResolveScienceMethod(subj.reasonKey),
                    SubjectMaxValue = subj.subjectMaxValue,
                    StartUT = (float)resolvedStartUt,
                    EndUT = (float)endUT,
                    Sequence = sequence++
                });
            }

            ParsekLog.Info(Tag,
                $"ConvertScienceSubjects: converted={result.Count} from {subjects.Count} subjects, " +
                $"recordingId={recordingId ?? "(none)"}, tagged={taggedMatches}, " +
                $"untaggedInWindow={untaggedInWindow}, skippedCrossRecording={skippedCrossRecording}, " +
                $"skippedOutsideWindow={skippedOutsideWindow}");

            return result;
        }

        private static bool TryResolveScienceSubjectStartUt(
            PendingScienceSubject subject,
            string recordingId,
            double defaultStartUT,
            double endUT,
            out double resolvedStartUt,
            out bool matchedViaUntaggedWindow,
            out bool skippedDueToCrossRecording)
        {
            resolvedStartUt = defaultStartUT;
            matchedViaUntaggedWindow = false;
            skippedDueToCrossRecording = false;

            string subjectRecordingId = subject.recordingId ?? "";
            string ownerRecordingId = recordingId ?? "";
            if (!string.IsNullOrEmpty(subjectRecordingId))
            {
                if (!string.Equals(subjectRecordingId, ownerRecordingId, StringComparison.Ordinal))
                {
                    skippedDueToCrossRecording = true;
                    return false;
                }

                if (!TryResolveTaggedScienceWindowStart(subject, defaultStartUT, endUT, out resolvedStartUt))
                    return false;

                return true;
            }

            double captureUt = subject.captureUT;
            if (!IsScienceCaptureWithinRecordingWindow(captureUt, defaultStartUT, endUT))
                return false;

            matchedViaUntaggedWindow = true;
            resolvedStartUt = captureUt;
            return true;
        }

        private static bool TryResolveTaggedScienceWindowStart(
            PendingScienceSubject subject,
            double defaultStartUT,
            double endUT,
            out double resolvedStartUt)
        {
            resolvedStartUt = defaultStartUT;
            double captureUt = subject.captureUT;
            if (double.IsNaN(captureUt) || double.IsInfinity(captureUt))
                return false;
            if (captureUt < 0.0)
                return false;
            if (captureUt == 0.0 && defaultStartUT > 0.0)
                return true;
            if (!IsScienceCaptureWithinRecordingWindow(captureUt, defaultStartUT, endUT))
                return false;

            resolvedStartUt = captureUt;
            return true;
        }

        private static bool IsScienceCaptureWithinRecordingWindow(
            double captureUt,
            double startUT,
            double endUT)
        {
            if (double.IsNaN(captureUt) || double.IsInfinity(captureUt))
                return false;
            if (captureUt < 0.0)
                return false;
            if (captureUt < startUT)
                return false;
            if (captureUt > endUT)
                return false;

            return true;
        }

        private static ScienceMethod ResolveScienceMethod(string reasonKey)
        {
            if (string.Equals(reasonKey, "VesselRecovery", StringComparison.Ordinal))
                return ScienceMethod.Recovered;

            return ScienceMethod.Transmitted;
        }

        // ================================================================
        // Per-type conversion helpers
        // ================================================================

        /// <summary>TechResearched -> ScienceSpending (nodeId=key, cost from detail).</summary>
        private static GameAction ConvertTechResearched(GameStateEvent evt, string recordingId)
        {
            float cost = 0f;
            string costStr = ExtractDetail(evt.detail, "cost");
            if (costStr != null)
                float.TryParse(costStr, NumberStyles.Float, IC, out cost);

            return new GameAction
            {
                UT = evt.ut,
                Type = GameActionType.ScienceSpending,
                RecordingId = recordingId,
                NodeId = evt.key,
                Cost = cost
            };
        }

        /// <summary>PartPurchased -> FundsSpending (part name=key, cost from detail).</summary>
        /// <remarks>
        /// #451: `cost=` is the authoritative charged amount. Post-#451 events may also
        /// carry `entryCost=` for the raw stock unlock price, but bypass=true careers
        /// persist `cost=0;entryCost=<raw>` and must reload as free purchases. Fall back
        /// to `entryCost=` only when `cost=` is absent.
        /// </remarks>
        private static GameAction ConvertPartPurchased(GameStateEvent evt, string recordingId)
        {
            float cost = 0f;
            string costStr = ExtractDetail(evt.detail, "cost")
                          ?? ExtractDetail(evt.detail, "entryCost");
            if (costStr != null)
                float.TryParse(costStr, NumberStyles.Float, IC, out cost);

            // DedupKey uses the part name so multiple part purchases at KSC (where
            // RecordingId is null) do not collide under GetActionKey. See #F in
            // career-earnings-bundle plan. Commit-path purchases have RecordingId set,
            // so DedupKey provides additional disambiguation there too (cheap).
            return new GameAction
            {
                UT = evt.ut,
                Type = GameActionType.FundsSpending,
                RecordingId = recordingId,
                FundsSpent = cost,
                FundsSpendingSource = FundsSpendingSource.Other,
                DedupKey = evt.key ?? ""
            };
        }

        /// <summary>
        /// FacilityUpgraded -> FacilityUpgrade (facilityId=key, cost from detail, level from valueAfter).
        /// </summary>
        private static GameAction ConvertFacilityUpgraded(GameStateEvent evt, string recordingId)
        {
            float cost = 0f;
            string costStr = ExtractDetail(evt.detail, "cost");
            if (costStr != null)
                float.TryParse(costStr, NumberStyles.Float, IC, out cost);

            // KSP facility levels are normalized 0-1; convert using max level 2 (3 tiers: 0, 0.5, 1.0)
            int toLevel = (int)Math.Round(evt.valueAfter * 2);
            if (toLevel < 1) toLevel = 1;

            return new GameAction
            {
                UT = evt.ut,
                Type = GameActionType.FacilityUpgrade,
                RecordingId = recordingId,
                FacilityId = evt.key,
                ToLevel = toLevel,
                FacilityCost = cost
            };
        }

        /// <summary>BuildingDestroyed -> FacilityDestruction (buildingId=key).</summary>
        private static GameAction ConvertBuildingDestroyed(GameStateEvent evt, string recordingId)
        {
            return new GameAction
            {
                UT = evt.ut,
                Type = GameActionType.FacilityDestruction,
                RecordingId = recordingId,
                FacilityId = evt.key
            };
        }

        /// <summary>BuildingRepaired -> FacilityRepair (buildingId=key, cost from detail).</summary>
        private static GameAction ConvertBuildingRepaired(GameStateEvent evt, string recordingId)
        {
            float cost = 0f;
            string costStr = ExtractDetail(evt.detail, "cost");
            if (costStr != null)
                float.TryParse(costStr, NumberStyles.Float, IC, out cost);

            return new GameAction
            {
                UT = evt.ut,
                Type = GameActionType.FacilityRepair,
                RecordingId = recordingId,
                FacilityId = evt.key,
                FacilityCost = cost
            };
        }

        /// <summary>CrewHired -> KerbalHire (name=key, cost/trait from detail).</summary>
        private static GameAction ConvertCrewHired(GameStateEvent evt, string recordingId)
        {
            string trait = ExtractDetail(evt.detail, "trait");

            float hireCost = 0f;
            string costStr = ExtractDetail(evt.detail, "cost");
            if (costStr != null)
                float.TryParse(costStr, NumberStyles.Float, IC, out hireCost);

            return new GameAction
            {
                UT = evt.ut,
                Type = GameActionType.KerbalHire,
                RecordingId = recordingId,
                KerbalName = evt.key,
                KerbalRole = trait,
                HireCost = hireCost
            };
        }

        /// <summary>
        /// ContractAccepted -> ContractAccept (contractId=key, title/type/deadline/advance/penalties from detail).
        /// New format (v4): "title=...;deadline=...;type=...;funds=...;failFunds=...;failRep=..."
        /// v3 (no type= key): backward compatible via snapshot fallback.
        /// v2 (no funds= key): backward compatible, advance defaults to 0.
        /// Legacy (v1): plain title string (no semicolons). Backward compatible.
        /// </summary>
        private static GameAction ConvertContractAccepted(GameStateEvent evt, string recordingId)
        {
            string title;
            string contractType = null;
            float deadlineUT = float.NaN;
            float advanceFunds = 0f;
            float fundsPenalty = 0f;
            float repPenalty = 0f;
            bool structured = evt.detail != null && evt.detail.Contains(";");
            string typeSource = "missing";

            // Detect structured vs legacy format by checking for semicolons
            if (structured)
            {
                // Structured format: extract fields
                title = ExtractDetail(evt.detail, "title") ?? "";
                contractType = ExtractDetail(evt.detail, "type");
                if (!string.IsNullOrEmpty(contractType))
                    typeSource = "detail";

                string deadlineStr = ExtractDetail(evt.detail, "deadline");
                if (deadlineStr != null && deadlineStr != "NaN")
                    float.TryParse(deadlineStr, NumberStyles.Float, IC, out deadlineUT);
                // else remains NaN

                // v3: contract advance payment. Older detail strings omit this key — leave at 0.
                string advanceStr = ExtractDetail(evt.detail, "funds");
                if (advanceStr != null)
                    float.TryParse(advanceStr, NumberStyles.Float, IC, out advanceFunds);

                string failFundsStr = ExtractDetail(evt.detail, "failFunds");
                if (failFundsStr != null)
                    float.TryParse(failFundsStr, NumberStyles.Float, IC, out fundsPenalty);

                string failRepStr = ExtractDetail(evt.detail, "failRep");
                if (failRepStr != null)
                    float.TryParse(failRepStr, NumberStyles.Float, IC, out repPenalty);
            }
            else
            {
                // Legacy format: entire detail is the title
                title = evt.detail ?? "";
            }

            if (string.IsNullOrEmpty(contractType))
            {
                ConfigNode snapshot = GameStateStore.GetContractSnapshot(evt.key);
                if (snapshot != null)
                {
                    contractType = snapshot.GetValue("type");
                    if (!string.IsNullOrEmpty(contractType))
                        typeSource = "snapshot";
                }
            }

            ParsekLog.Verbose(Tag,
                $"ConvertContractAccepted: {(structured ? "structured" : "legacy")} format " +
                $"contractId='{evt.key}' title='{title}' type='{contractType ?? ""}' " +
                $"typeSource={typeSource} deadline={deadlineUT} advance={advanceFunds} " +
                $"failFunds={fundsPenalty} failRep={repPenalty}");

            return new GameAction
            {
                UT = evt.ut,
                Type = GameActionType.ContractAccept,
                RecordingId = recordingId,
                ContractId = evt.key,
                ContractTitle = title,
                ContractType = contractType,
                DeadlineUT = deadlineUT,
                AdvanceFunds = advanceFunds,
                FundsPenalty = fundsPenalty,
                RepPenalty = repPenalty
            };
        }

        /// <summary>ContractCompleted -> ContractComplete (contractId=key, rewards from detail).</summary>
        private static GameAction ConvertContractCompleted(GameStateEvent evt, string recordingId)
        {
            float fundsReward = 0f;
            float repReward = 0f;
            float sciReward = 0f;

            string fundsStr = ExtractDetail(evt.detail, "fundsReward");
            if (fundsStr != null)
                float.TryParse(fundsStr, NumberStyles.Float, IC, out fundsReward);

            string repStr = ExtractDetail(evt.detail, "repReward");
            if (repStr != null)
                float.TryParse(repStr, NumberStyles.Float, IC, out repReward);

            string sciStr = ExtractDetail(evt.detail, "sciReward");
            if (sciStr != null)
                float.TryParse(sciStr, NumberStyles.Float, IC, out sciReward);

            if (fundsReward == 0f && repReward == 0f && sciReward == 0f)
                ParsekLog.Warn(Tag,
                    $"ConvertContractCompleted: all rewards are 0 for contract '{evt.key}' — detail may lack reward fields");

            return new GameAction
            {
                UT = evt.ut,
                Type = GameActionType.ContractComplete,
                RecordingId = recordingId,
                ContractId = evt.key,
                FundsReward = fundsReward,
                RepReward = repReward,
                ScienceReward = sciReward
            };
        }

        /// <summary>ContractFailed -> ContractFail (contractId=key, penalties from detail).</summary>
        private static GameAction ConvertContractFailed(GameStateEvent evt, string recordingId)
        {
            float fundsPenalty = 0f;
            float repPenalty = 0f;

            string fundsStr = ExtractDetail(evt.detail, "fundsPenalty");
            if (fundsStr != null)
                float.TryParse(fundsStr, NumberStyles.Float, IC, out fundsPenalty);

            string repStr = ExtractDetail(evt.detail, "repPenalty");
            if (repStr != null)
                float.TryParse(repStr, NumberStyles.Float, IC, out repPenalty);

            return new GameAction
            {
                UT = evt.ut,
                Type = GameActionType.ContractFail,
                RecordingId = recordingId,
                ContractId = evt.key,
                FundsPenalty = fundsPenalty,
                RepPenalty = repPenalty
            };
        }

        /// <summary>ContractCancelled -> ContractCancel (contractId=key, penalties from detail).</summary>
        private static GameAction ConvertContractCancelled(GameStateEvent evt, string recordingId)
        {
            float fundsPenalty = 0f;
            float repPenalty = 0f;

            string fundsStr = ExtractDetail(evt.detail, "fundsPenalty");
            if (fundsStr != null)
                float.TryParse(fundsStr, NumberStyles.Float, IC, out fundsPenalty);

            string repStr = ExtractDetail(evt.detail, "repPenalty");
            if (repStr != null)
                float.TryParse(repStr, NumberStyles.Float, IC, out repPenalty);

            return new GameAction
            {
                UT = evt.ut,
                Type = GameActionType.ContractCancel,
                RecordingId = recordingId,
                ContractId = evt.key,
                FundsPenalty = fundsPenalty,
                RepPenalty = repPenalty
            };
        }

        /// <summary>
        /// MilestoneAchieved -> MilestoneAchievement (milestoneId=key, funds/rep/sci from detail).
        /// Rewards may be 0 if not available from the ProgressNode.
        /// </summary>
        internal static GameAction ConvertMilestoneAchieved(GameStateEvent evt, string recordingId)
        {
            float fundsAwarded = 0f;
            float repAwarded = 0f;
            float sciAwarded = 0f;

            string fundsStr = ExtractDetail(evt.detail, "funds");
            if (fundsStr != null)
                float.TryParse(fundsStr, NumberStyles.Float, IC, out fundsAwarded);

            string repStr = ExtractDetail(evt.detail, "rep");
            if (repStr != null)
                float.TryParse(repStr, NumberStyles.Float, IC, out repAwarded);

            // Milestone science reward (e.g. Kerbin/Science FirstLaunch grants ~2 sci).
            // Previously dropped at convert time — ScienceModule.ProcessMilestoneScienceReward
            // now consumes this on the effective (first-reached) copy of the action.
            string sciStr = ExtractDetail(evt.detail, "sci");
            if (sciStr != null)
                float.TryParse(sciStr, NumberStyles.Float, IC, out sciAwarded);

            return new GameAction
            {
                UT = evt.ut,
                Type = GameActionType.MilestoneAchievement,
                RecordingId = recordingId,
                MilestoneId = evt.key,
                MilestoneFundsAwarded = fundsAwarded,
                MilestoneRepAwarded = repAwarded,
                MilestoneScienceAwarded = sciAwarded
            };
        }

        /// <summary>KerbalRescued -> KerbalRescue (name=key, trait from detail).</summary>
        private static GameAction ConvertKerbalRescued(GameStateEvent evt, string recordingId)
        {
            string trait = ExtractDetail(evt.detail, "trait");

            return new GameAction
            {
                UT = evt.ut,
                Type = GameActionType.KerbalRescue,
                RecordingId = recordingId,
                KerbalName = evt.key,
                KerbalRole = trait
            };
        }

        /// <summary>
        /// StrategyActivated -> StrategyActivate. <c>StrategyId</c> comes from
        /// <c>evt.key</c> (strategy config Name). <c>Commitment</c> parses the
        /// <c>factor</c> detail field; <c>SourceResource</c> / <c>TargetResource</c>
        /// parse the recorder-emitted <c>source</c> / <c>target</c> detail fields so
        /// the Actions/Timeline/Career State UI preserves the real strategy flow.
        /// Setup costs parse from <c>setupFunds</c>, <c>setupSci</c>, and
        /// <c>setupRep</c> so the action carries all three setup resources through
        /// conversion, save/load, recalculation, and KSC reconciliation.
        /// Internal static for testability.
        /// </summary>
        internal static GameAction ConvertStrategyActivated(GameStateEvent evt, string recordingId)
        {
            float factor = ParseDetailFloat(evt.detail, "factor");
            StrategyResource sourceResource = ParseStrategyResource(
                ExtractDetail(evt.detail, "source"),
                StrategyResource.Funds);
            StrategyResource targetResource = ParseStrategyResource(
                ExtractDetail(evt.detail, "target"),
                StrategyResource.Funds);
            float setupFunds = ParseDetailFloat(evt.detail, "setupFunds");
            float setupSci = ParseDetailFloat(evt.detail, "setupSci");
            float setupRep = ParseDetailFloat(evt.detail, "setupRep");

            return new GameAction
            {
                UT = evt.ut,
                Type = GameActionType.StrategyActivate,
                RecordingId = recordingId,
                StrategyId = evt.key,
                SourceResource = sourceResource,
                TargetResource = targetResource,
                Commitment = factor,
                SetupCost = setupFunds,
                SetupScienceCost = setupSci,
                SetupReputationCost = setupRep
            };
        }

        /// <summary>
        /// StrategyDeactivated -> StrategyDeactivate (#439 Phase A). Carries only the
        /// StrategyId; the deactivate action has no resource flow and the classifier
        /// maps it to <see cref="LedgerOrchestrator.KscReconcileClass.NoResourceImpact"/>.
        /// Internal static for testability.
        /// </summary>
        internal static GameAction ConvertStrategyDeactivated(GameStateEvent evt, string recordingId)
        {
            return new GameAction
            {
                UT = evt.ut,
                Type = GameActionType.StrategyDeactivate,
                RecordingId = recordingId,
                StrategyId = evt.key
            };
        }

        // ================================================================
        // Detail extraction helper
        // ================================================================

        /// <summary>
        /// Extracts a named field from a semicolon-separated detail string.
        /// Format: "key1=value1;key2=value2;...". Returns null if the key is not found.
        /// Delegates to <see cref="GameStateEventDisplay.ExtractDetailField"/> which handles
        /// the parsing logic.
        /// </summary>
        private static string ExtractDetail(string detail, string key)
        {
            return GameStateEventDisplay.ExtractDetailField(detail, key);
        }

        private static float ParseDetailFloat(string detail, string key)
        {
            float value = 0f;
            string raw = ExtractDetail(detail, key);
            if (raw != null)
                float.TryParse(raw, NumberStyles.Float, IC, out value);
            return value;
        }

        private static StrategyResource ParseStrategyResource(
            string rawValue,
            StrategyResource fallback)
        {
            StrategyResource resource;
            if (string.IsNullOrEmpty(rawValue))
                return fallback;

            if (Enum.TryParse(rawValue, true, out resource)
                && Enum.IsDefined(typeof(StrategyResource), resource))
                return resource;

            int numericValue;
            if (int.TryParse(rawValue, NumberStyles.Integer, IC, out numericValue)
                && Enum.IsDefined(typeof(StrategyResource), numericValue))
                return (StrategyResource)numericValue;

            return fallback;
        }
    }
}
