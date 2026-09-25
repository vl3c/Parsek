using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Display helpers for GameAction — category labels, descriptions, and colors
    /// for rendering ledger actions in the Actions window.
    /// Analogous to <see cref="GameStateEventDisplay"/> for GameStateEvents.
    /// </summary>
    internal static class GameActionDisplay
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>Human-readable description of the action.</summary>
        internal static string GetDescription(GameAction action, Game.Modes? currentMode)
        {
            if (action == null)
                return "";

            switch (action.Type)
            {
                case GameActionType.ScienceEarning:
                    return string.Format(IC, "{0} +{1:0.#} sci",
                        action.SubjectId ?? "unknown", action.ScienceAwarded);

                case GameActionType.ScienceSpending:
                    return string.Format(IC, "Tech: {0} -{1:0.#} sci",
                        action.NodeId ?? "unknown", action.Cost);

                case GameActionType.StrategyScienceDebit:
                    return string.Format(IC, "Strategy exchange -{0:0.#} sci", action.Cost);

                case GameActionType.StrategyScienceCredit:
                    return string.Format(IC, "Strategy exchange +{0:0.#} sci", action.ScienceAwarded);

                case GameActionType.FundsEarning:
                    return string.Format(IC, "{0} +{1:0}",
                        FormatFundsSource(action.FundsSource), action.FundsAwarded);

                case GameActionType.FundsSpending:
                {
                    string label = string.Format(IC, "{0} -{1:0}",
                        FormatFundsSpendingSource(action.FundsSpendingSource), action.FundsSpent);
                    if (IsUnclaimedRolloutAction(action))
                        label += CancelledRolloutSuffix;
                    return label;
                }

                case GameActionType.FundsInitial:
                    return string.Format(IC, "Starting funds: {0:0}", action.InitialFunds);

                case GameActionType.ReputationEarning:
                    return string.Format(IC, "{0} +{1:0.#} rep",
                        FormatRepSource(action.RepSource), action.NominalRep);

                case GameActionType.ReputationPenalty:
                    return string.Format(IC, "{0} -{1:0.#} rep",
                        FormatRepPenaltySource(action.RepPenaltySource), action.NominalPenalty);

                case GameActionType.MilestoneAchievement:
                {
                    string desc = action.MilestoneId ?? "unknown";
                    if (action.MilestoneFundsAwarded != 0)
                        desc += string.Format(IC, " +{0:0} funds", action.MilestoneFundsAwarded);
                    if (action.MilestoneRepAwarded != 0)
                        desc += string.Format(IC, " +{0:0.#} rep", action.MilestoneRepAwarded);
                    if (action.MilestoneScienceAwarded != 0)
                        desc += string.Format(IC, " +{0:0.#} sci", action.MilestoneScienceAwarded);
                    return desc;
                }

                case GameActionType.ContractAccept:
                case GameActionType.ContractComplete:
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                {
                    ContractNameSource ignored;
                    return GetContractDescription(action,
                        ResolveContractDisplayName(action, null, out ignored));
                }

                case GameActionType.KerbalAssignment:
                    return string.Format(IC, "{0} ({1})",
                        action.KerbalName ?? "unknown", action.KerbalRole ?? "unknown");

                case GameActionType.KerbalHire:
                    return GetKerbalHireDescription(action, currentMode);

                case GameActionType.KerbalRescue:
                    return "Rescue: " + (action.KerbalName ?? "unknown");

                case GameActionType.KerbalRecovered:
                    return "Recovered: " + (action.KerbalName ?? "unknown");

                case GameActionType.KerbalStandIn:
                    return string.Format("Stand-in: {0} for {1}",
                        action.KerbalName ?? "unknown", action.ReplacesKerbal ?? "unknown");

                case GameActionType.FacilityUpgrade:
                    return string.Format(IC, "Upgrade {0} \u2192 Lv.{1} -{2:0}",
                        FacilityDisplayNames.ResolveBuildingDisplayName(action.FacilityId),
                        action.ToLevel, action.FacilityCost);

                case GameActionType.FacilityDestruction:
                    return FacilityDisplayNames.ResolveBuildingDisplayName(action.FacilityId) + " destroyed";

                case GameActionType.FacilityRepair:
                    return string.Format(IC, "Repair {0} -{1:0}",
                        FacilityDisplayNames.ResolveBuildingDisplayName(action.FacilityId),
                        action.FacilityCost);

                case GameActionType.StrategyActivate:
                    return string.Format(IC, "Activate: {0} ({1:P0} {2}\u2192{3})",
                        action.StrategyId ?? "unknown", action.Commitment,
                        action.SourceResource, action.TargetResource);

                case GameActionType.StrategyDeactivate:
                    return "Deactivate: " + (action.StrategyId ?? "unknown");

                default:
                    return action.Type.ToString();
            }
        }

        /// <summary>Where a contract row's name came from (for the Timeline's summary log).</summary>
        internal enum ContractNameSource
        {
            /// <summary>The action's own <see cref="GameAction.ContractTitle"/>.</summary>
            OwnTitle,
            /// <summary>The title of the same contract's accept action in the ledger passed in.</summary>
            AcceptTitle,
            /// <summary>No title anywhere; the contract type (own, else the accept's), humanized.</summary>
            ContractType,
            /// <summary>Only the contract id: its accept is outside the ledger (pre-Parsek or tombstoned).</summary>
            IdFallback
        }

        /// <summary>
        /// Contract id -> that contract's accept action, over the ledger the caller shows
        /// (the Timeline passes <c>EffectiveState.ComputeELS()</c>, so a tombstoned accept
        /// is absent and its outcome rows fall back). Ledger rows converted before outcome
        /// actions carried their own title find their name here. An accept with a title
        /// wins over one without.
        /// </summary>
        internal static Dictionary<string, GameAction> BuildContractAcceptIndex(
            IReadOnlyList<GameAction> actions)
        {
            var index = new Dictionary<string, GameAction>(StringComparer.Ordinal);
            if (actions == null) return index;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a == null || a.Type != GameActionType.ContractAccept) continue;
                if (string.IsNullOrEmpty(a.ContractId)) continue;
                GameAction existing;
                if (index.TryGetValue(a.ContractId, out existing)
                    && !string.IsNullOrEmpty(existing.ContractTitle))
                    continue;
                index[a.ContractId] = a;
            }
            return index;
        }

        /// <summary>
        /// The name a contract row shows, never "unknown": the action's own title, else
        /// the title of the contract's accept in <paramref name="acceptIndex"/> (may be
        /// null), else the humanized contract type, else <see cref="FormatContractIdFallback"/>.
        /// </summary>
        internal static string ResolveContractDisplayName(
            GameAction action,
            IReadOnlyDictionary<string, GameAction> acceptIndex,
            out ContractNameSource source)
        {
            if (action != null && !string.IsNullOrEmpty(action.ContractTitle))
            {
                source = ContractNameSource.OwnTitle;
                return action.ContractTitle;
            }

            GameAction accept = null;
            if (action != null && acceptIndex != null && !string.IsNullOrEmpty(action.ContractId))
                acceptIndex.TryGetValue(action.ContractId, out accept);

            if (accept != null && !string.IsNullOrEmpty(accept.ContractTitle))
            {
                source = ContractNameSource.AcceptTitle;
                return accept.ContractTitle;
            }

            string type = action != null && !string.IsNullOrEmpty(action.ContractType)
                ? action.ContractType
                : accept?.ContractType;
            if (!string.IsNullOrEmpty(type))
            {
                source = ContractNameSource.ContractType;
                // Stock class names already end in "Contract" (SatelliteContract), so the
                // suffix is dropped before " contract" is appended once.
                string baseType = type.EndsWith("Contract", StringComparison.Ordinal) && type.Length > "Contract".Length
                    ? type.Substring(0, type.Length - "Contract".Length)
                    : type;
                return CareerStateWindowUI.SpaceBeforeCapitals(baseType) + " contract";
            }

            source = ContractNameSource.IdFallback;
            return FormatContractIdFallback(action?.ContractId);
        }

        /// <summary>
        /// Readable text for a contract known only by its id: <c>Contract 7a726c83</c> (the
        /// first block of the stock GUID, enough to tell two contracts apart in one list).
        /// </summary>
        internal static string FormatContractIdFallback(string contractId)
        {
            if (string.IsNullOrEmpty(contractId)) return "Unnamed contract";
            string shortId = contractId;
            int dash = shortId.IndexOf('-');
            if (dash > 0) shortId = shortId.Substring(0, dash);
            if (shortId.Length > 8) shortId = shortId.Substring(0, 8);
            return "Contract " + shortId;
        }

        /// <summary>
        /// Every contract accept in <paramref name="actions"/>, grouped by contract id in
        /// list order, for <see cref="FindAcceptForOutcome"/>. Unlike
        /// <see cref="BuildContractAcceptIndex"/> (one titled accept per id, for the name)
        /// it keeps each re-accept, whose deadline is its own.
        /// </summary>
        internal static Dictionary<string, List<GameAction>> BuildContractAcceptHistory(
            IReadOnlyList<GameAction> actions)
        {
            var history = new Dictionary<string, List<GameAction>>(StringComparer.Ordinal);
            if (actions == null) return history;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a == null || a.Type != GameActionType.ContractAccept) continue;
                if (string.IsNullOrEmpty(a.ContractId)) continue;
                List<GameAction> accepts;
                if (!history.TryGetValue(a.ContractId, out accepts))
                {
                    accepts = new List<GameAction>();
                    history[a.ContractId] = accepts;
                }
                accepts.Add(a);
            }
            return history;
        }

        /// <summary>
        /// The accept an outcome row closes: the latest accept of the same contract at or
        /// before the outcome's UT. Null when the ledger holds none.
        /// </summary>
        internal static GameAction FindAcceptForOutcome(
            IReadOnlyDictionary<string, List<GameAction>> acceptHistory, GameAction outcome)
        {
            if (acceptHistory == null || outcome == null || string.IsNullOrEmpty(outcome.ContractId))
                return null;
            List<GameAction> accepts;
            if (!acceptHistory.TryGetValue(outcome.ContractId, out accepts)) return null;
            GameAction best = null;
            for (int i = 0; i < accepts.Count; i++)
            {
                if (accepts[i].UT <= outcome.UT && (best == null || accepts[i].UT >= best.UT))
                    best = accepts[i];
            }
            return best;
        }

        /// <summary>
        /// Pure: true when a <c>ContractFail</c> row is the contract's deadline running
        /// out (stock's <c>DeadlineExpired</c>, which fires the same <c>onFailed</c>
        /// event), judged against the accept it closes with the ledger's own test
        /// (<see cref="ContractsModule.IsDeadlineExpiryFail"/>). A fail with no accept on
        /// the ledger, or before its deadline, is a failure.
        /// </summary>
        internal static bool IsExpiredContractFail(GameAction fail, GameAction accept)
        {
            if (fail == null || accept == null || fail.Type != GameActionType.ContractFail)
                return false;
            return ContractsModule.IsDeadlineExpiryFail(fail.UT, accept.DeadlineUT, accept.UT);
        }

        /// <summary>Row text for the four contract action types, given the resolved name.</summary>
        internal static string GetContractDescription(GameAction action, string displayName)
        {
            return GetContractDescription(action, displayName, null);
        }

        /// <summary>
        /// Row text for the four contract action types, given the resolved name and the
        /// accept the row closes (may be null): a fail at or after that accept's deadline
        /// reads <c>Expired: name</c>, any other fail <c>Fail: name</c>.
        /// </summary>
        internal static string GetContractDescription(GameAction action, string displayName,
                                                      GameAction closedAccept)
        {
            if (action == null) return "";
            switch (action.Type)
            {
                case GameActionType.ContractAccept:
                    return "Accept: " + displayName;

                case GameActionType.ContractComplete:
                {
                    string desc = "Complete: " + displayName;
                    if (action.FundsReward != 0)
                        desc += string.Format(IC, " +{0:0} funds", action.FundsReward);
                    return desc;
                }

                case GameActionType.ContractFail:
                    return (IsExpiredContractFail(action, closedAccept) ? "Expired: " : "Fail: ")
                        + displayName;

                case GameActionType.ContractCancel:
                    return "Cancel: " + displayName;

                default:
                    return displayName;
            }
        }

        internal static bool IsContractActionType(GameActionType type)
        {
            return type == GameActionType.ContractAccept
                || type == GameActionType.ContractComplete
                || type == GameActionType.ContractFail
                || type == GameActionType.ContractCancel;
        }

        internal static string GetKerbalHireDescription(GameAction action, Game.Modes? currentMode)
        {
            string kerbalName = action?.KerbalName ?? "unknown";
            if (!ShouldShowFundsForKerbalHire(action, currentMode))
                return "Hire: " + kerbalName;

            return string.Format(IC, "Hire: {0} -{1:0} funds", kerbalName, action.HireCost);
        }

        internal static bool ShouldShowFundsForKerbalHire(GameAction action, Game.Modes? currentMode)
        {
            if (action == null || action.HireCost <= 0f)
                return false;

            if (!currentMode.HasValue)
                return true;

            switch (currentMode.Value)
            {
                case Game.Modes.SANDBOX:
                case Game.Modes.SCIENCE_SANDBOX:
                case Game.Modes.MISSION_BUILDER:
                case Game.Modes.MISSION:
                    return false;

                default:
                    return true;
            }
        }

        /// <summary>Color for the action type: green for earnings, red for spending, white for neutral.</summary>
        internal static Color GetColor(GameActionType type)
        {
            switch (type)
            {
                // Earnings — green
                case GameActionType.ScienceEarning:
                case GameActionType.StrategyScienceCredit:
                case GameActionType.FundsEarning:
                case GameActionType.ReputationEarning:
                case GameActionType.MilestoneAchievement:
                case GameActionType.ContractComplete:
                case GameActionType.KerbalRescue:
                    return new Color(0.5f, 1f, 0.5f);

                // Spending / penalties — red
                case GameActionType.ScienceSpending:
                case GameActionType.StrategyScienceDebit:
                case GameActionType.FundsSpending:
                case GameActionType.ReputationPenalty:
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                case GameActionType.KerbalHire:
                case GameActionType.FacilityUpgrade:
                case GameActionType.FacilityRepair:
                case GameActionType.FacilityDestruction:
                    return new Color(1f, 0.5f, 0.5f);

                // Neutral — white
                default:
                    return Color.white;
            }
        }

        // ---- Formatting helpers ----

        /// <summary>
        /// Bug #452: a <see cref="FundsSpendingSource.VesselBuild"/> action that has no
        /// owning recording but carries the <c>"rollout:"</c> dedup tag set by
        /// <see cref="LedgerOrchestrator.OnVesselRolloutSpending"/> represents a rollout
        /// the player never adopted by launching+committing a recording (typically a
        /// rolled-out vessel cancelled before launch). We render those with a
        /// <c>"(cancelled rollout)"</c> suffix so they're visibly distinguishable from
        /// adopted (recording-tagged) build costs in the Actions / Ledger UI.
        /// </summary>
        internal static bool IsUnclaimedRolloutAction(GameAction action)
        {
            if (action == null) return false;
            if (action.Type != GameActionType.FundsSpending) return false;
            // VesselBuild check is defensive — currently the only "rollout:" producer is
            // OnVesselRolloutSpending, but pin the contract to prevent future misclassification.
            if (action.FundsSpendingSource != FundsSpendingSource.VesselBuild) return false;
            if (!string.IsNullOrEmpty(action.RecordingId)) return false;
            if (string.IsNullOrEmpty(action.DedupKey)) return false;
            return action.DedupKey.StartsWith(LedgerOrchestrator.RolloutDedupPrefix, StringComparison.Ordinal);
        }

        /// <summary>
        /// Suffix appended to <see cref="FundsSpending"/>(VesselBuild) entries that
        /// match <see cref="IsUnclaimedRolloutAction"/>. Shared between the Actions
        /// renderer (<see cref="GetDescription"/>) and the Timeline renderer
        /// (<see cref="TimelineEntryDisplay.GetGameActionText"/>) so both views agree.
        /// </summary>
        internal const string CancelledRolloutSuffix = " (cancelled rollout)";

        private static string FormatFundsSource(FundsEarningSource source)
        {
            switch (source)
            {
                case FundsEarningSource.ContractComplete: return "Contract";
                case FundsEarningSource.ContractAdvance:  return "Advance";
                case FundsEarningSource.Recovery:         return "Recovery";
                case FundsEarningSource.Milestone:        return "Milestone";
                case FundsEarningSource.LegacyMigration:  return "Legacy migration";
                case FundsEarningSource.Strategy:         return "Strategy";
                default:                                  return "Funds";
            }
        }

        private static string FormatFundsSpendingSource(FundsSpendingSource source)
        {
            switch (source)
            {
                case FundsSpendingSource.VesselBuild:     return "Vessel build";
                case FundsSpendingSource.FacilityUpgrade: return "Facility upgrade";
                case FundsSpendingSource.FacilityRepair:  return "Facility repair";
                case FundsSpendingSource.KerbalHire:      return "Hire";
                case FundsSpendingSource.ContractPenalty:  return "Contract penalty";
                case FundsSpendingSource.Strategy:        return "Strategy";
                case FundsSpendingSource.StrategyConverter:
                    return "Strategy converter";
                case FundsSpendingSource.Other:           return "Part";
                default:                                  return "Expense";
            }
        }

        private static string FormatRepSource(ReputationSource source)
        {
            switch (source)
            {
                case ReputationSource.ContractComplete: return "Contract";
                case ReputationSource.Milestone:        return "Milestone";
                case ReputationSource.Strategy:         return "Strategy";
                default:                                return "Rep";
            }
        }

        private static string FormatRepPenaltySource(ReputationPenaltySource source)
        {
            switch (source)
            {
                case ReputationPenaltySource.ContractFail:    return "Contract fail";
                case ReputationPenaltySource.ContractDecline: return "Contract decline";
                case ReputationPenaltySource.KerbalDeath:     return "Kerbal death";
                case ReputationPenaltySource.Strategy:        return "Strategy";
                case ReputationPenaltySource.StrategyConverter:
                    return "Strategy converter";
                default:                                      return "Penalty";
            }
        }
    }
}
