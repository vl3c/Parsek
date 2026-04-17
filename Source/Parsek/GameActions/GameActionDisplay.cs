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

        /// <summary>Short category label for the action type.</summary>
        internal static string GetCategory(GameActionType type)
        {
            switch (type)
            {
                case GameActionType.ScienceEarning:
                case GameActionType.ScienceSpending:
                    return "Science";

                case GameActionType.FundsEarning:
                case GameActionType.FundsSpending:
                case GameActionType.FundsInitial:
                    return "Funds";

                case GameActionType.ReputationEarning:
                case GameActionType.ReputationPenalty:
                    return "Rep";

                case GameActionType.MilestoneAchievement:
                    return "Milestone";

                case GameActionType.ContractAccept:
                case GameActionType.ContractComplete:
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    return "Contract";

                case GameActionType.KerbalAssignment:
                case GameActionType.KerbalHire:
                case GameActionType.KerbalRescue:
                case GameActionType.KerbalStandIn:
                    return "Kerbal";

                case GameActionType.FacilityUpgrade:
                case GameActionType.FacilityDestruction:
                case GameActionType.FacilityRepair:
                    return "Facility";

                case GameActionType.StrategyActivate:
                case GameActionType.StrategyDeactivate:
                    return "Strategy";

                default:
                    return "Action";
            }
        }

        /// <summary>Human-readable description of the action.</summary>
        internal static string GetDescription(GameAction action)
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

                case GameActionType.FundsEarning:
                    return string.Format(IC, "{0} +{1:0}",
                        FormatFundsSource(action.FundsSource), action.FundsAwarded);

                case GameActionType.FundsSpending:
                    return string.Format(IC, "{0} -{1:0}",
                        FormatFundsSpendingSource(action.FundsSpendingSource), action.FundsSpent);

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
                    return "Accept: " + (action.ContractTitle ?? action.ContractType ?? "unknown");

                case GameActionType.ContractComplete:
                {
                    string title = action.ContractTitle ?? action.ContractType ?? "unknown";
                    string desc = "Complete: " + title;
                    if (action.FundsReward != 0)
                        desc += string.Format(IC, " +{0:0} funds", action.FundsReward);
                    return desc;
                }

                case GameActionType.ContractFail:
                    return "Fail: " + (action.ContractTitle ?? action.ContractType ?? "unknown");

                case GameActionType.ContractCancel:
                    return "Cancel: " + (action.ContractTitle ?? action.ContractType ?? "unknown");

                case GameActionType.KerbalAssignment:
                    return string.Format(IC, "{0} ({1})",
                        action.KerbalName ?? "unknown", action.KerbalRole ?? "unknown");

                case GameActionType.KerbalHire:
                    return string.Format(IC, "Hire: {0} -{1:0} funds",
                        action.KerbalName ?? "unknown", action.HireCost);

                case GameActionType.KerbalRescue:
                    return "Rescue: " + (action.KerbalName ?? "unknown");

                case GameActionType.KerbalStandIn:
                    return string.Format("Stand-in: {0} for {1}",
                        action.KerbalName ?? "unknown", action.ReplacesKerbal ?? "unknown");

                case GameActionType.FacilityUpgrade:
                    return string.Format(IC, "Upgrade {0} \u2192 Lv.{1} -{2:0}",
                        action.FacilityId ?? "unknown", action.ToLevel, action.FacilityCost);

                case GameActionType.FacilityDestruction:
                    return (action.FacilityId ?? "unknown") + " destroyed";

                case GameActionType.FacilityRepair:
                    return string.Format(IC, "Repair {0} -{1:0}",
                        action.FacilityId ?? "unknown", action.FacilityCost);

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

        /// <summary>Color for the action type: green for earnings, red for spending, white for neutral.</summary>
        internal static Color GetColor(GameActionType type)
        {
            switch (type)
            {
                // Earnings — green
                case GameActionType.ScienceEarning:
                case GameActionType.FundsEarning:
                case GameActionType.ReputationEarning:
                case GameActionType.MilestoneAchievement:
                case GameActionType.ContractComplete:
                case GameActionType.KerbalRescue:
                    return new Color(0.5f, 1f, 0.5f);

                // Spending / penalties — red
                case GameActionType.ScienceSpending:
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

        private static string FormatFundsSource(FundsEarningSource source)
        {
            switch (source)
            {
                case FundsEarningSource.ContractComplete: return "Contract";
                case FundsEarningSource.ContractAdvance:  return "Advance";
                case FundsEarningSource.Recovery:         return "Recovery";
                case FundsEarningSource.Milestone:        return "Milestone";
                case FundsEarningSource.LegacyMigration:  return "Legacy migration";
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
                default:                                  return "Expense";
            }
        }

        private static string FormatRepSource(ReputationSource source)
        {
            switch (source)
            {
                case ReputationSource.ContractComplete: return "Contract";
                case ReputationSource.Milestone:        return "Milestone";
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
                default:                                      return "Penalty";
            }
        }
    }
}
