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
    ///
    /// Pure static — no KSP state access.
    /// </summary>
    internal static class GameStateEventConverter
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private const string Tag = "GameStateEventConverter";

        /// <summary>
        /// Converts a list of GameStateEvents into GameActions, filtering to the given UT range
        /// and skipping non-convertible event types. Returns only non-null conversions.
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

            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];

                if (evt.ut < startUT || evt.ut > endUT)
                {
                    outOfRange++;
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
                }
            }

            ParsekLog.Info(Tag,
                $"ConvertEvents: converted={converted}, skipped={skipped}, outOfRange={outOfRange}, " +
                $"total={events.Count}, recordingId={recordingId ?? "(none)"}");

            return result;
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

                // Skipped event types — no GameAction equivalent
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
        /// Each subject becomes a ScienceEarning action with subjectId, scienceAwarded, and subjectMaxValue.
        /// </summary>
        /// <param name="subjects">Science subjects to convert.</param>
        /// <param name="recordingId">Recording that produced these subjects.</param>
        /// <param name="ut">Universal time to assign to all generated actions.</param>
        internal static List<GameAction> ConvertScienceSubjects(
            IReadOnlyList<PendingScienceSubject> subjects, string recordingId, double ut)
        {
            var result = new List<GameAction>();

            if (subjects == null || subjects.Count == 0)
            {
                ParsekLog.Verbose(Tag,
                    "ConvertScienceSubjects: empty or null subjects list, returning 0 actions");
                return result;
            }

            int sequence = 1;

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

                result.Add(new GameAction
                {
                    UT = ut,
                    Type = GameActionType.ScienceEarning,
                    RecordingId = recordingId,
                    SubjectId = subj.subjectId,
                    ScienceAwarded = subj.science,
                    SubjectMaxValue = subj.subjectMaxValue,
                    Sequence = sequence++
                });
            }

            ParsekLog.Info(Tag,
                $"ConvertScienceSubjects: converted={result.Count} from {subjects.Count} subjects, " +
                $"recordingId={recordingId ?? "(none)"}");

            return result;
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
        private static GameAction ConvertPartPurchased(GameStateEvent evt, string recordingId)
        {
            float cost = 0f;
            string costStr = ExtractDetail(evt.detail, "cost");
            if (costStr != null)
                float.TryParse(costStr, NumberStyles.Float, IC, out cost);

            return new GameAction
            {
                UT = evt.ut,
                Type = GameActionType.FundsSpending,
                RecordingId = recordingId,
                FundsSpent = cost,
                FundsSpendingSource = FundsSpendingSource.Other
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

        /// <summary>ContractAccepted -> ContractAccept (contractId=key, title from detail).</summary>
        private static GameAction ConvertContractAccepted(GameStateEvent evt, string recordingId)
        {
            // Contract accepted events store the title directly in detail (not key=value format)
            // per GameStateRecorder.OnContractAccepted
            return new GameAction
            {
                UT = evt.ut,
                Type = GameActionType.ContractAccept,
                RecordingId = recordingId,
                ContractId = evt.key,
                ContractTitle = evt.detail
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
        /// MilestoneAchieved -> MilestoneAchievement (milestoneId=key, funds/rep from detail).
        /// Funds and rep rewards may be 0 if not available from the ProgressNode.
        /// </summary>
        internal static GameAction ConvertMilestoneAchieved(GameStateEvent evt, string recordingId)
        {
            float fundsAwarded = 0f;
            float repAwarded = 0f;

            string fundsStr = ExtractDetail(evt.detail, "funds");
            if (fundsStr != null)
                float.TryParse(fundsStr, NumberStyles.Float, IC, out fundsAwarded);

            string repStr = ExtractDetail(evt.detail, "rep");
            if (repStr != null)
                float.TryParse(repStr, NumberStyles.Float, IC, out repAwarded);

            return new GameAction
            {
                UT = evt.ut,
                Type = GameActionType.MilestoneAchievement,
                RecordingId = recordingId,
                MilestoneId = evt.key,
                MilestoneFundsAwarded = fundsAwarded,
                MilestoneRepAwarded = repAwarded
            };
        }

        // ================================================================
        // Deferred: Kerbal Rescue action generation (D6)
        // ================================================================
        // KerbalRescue actions would be generated here when a recording detects
        // docking with or EVA pickup of a stranded kerbal. This requires:
        //   1. Recording system integration to detect docking events with stranded vessels
        //   2. Cross-referencing the docked vessel's crew against known stranded kerbals
        //   3. A new GameActionType.KerbalRescue and corresponding event type
        // Scaffolded — requires recording system integration to detect docking
        // with stranded kerbals. See deferred item D6.

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
    }
}
