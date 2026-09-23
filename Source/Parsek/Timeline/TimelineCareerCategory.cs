using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// The career subject a Timeline row belongs to, for the Timeline's Career view
    /// (one category at a time). <see cref="None"/> for every row outside the five
    /// categories: recording rows, legacy rows, earnings, hires, builds, recoveries,
    /// starting balances and the strategy currency-exchange legs.
    /// </summary>
    public enum TimelineCareerCategory
    {
        None = 0,
        Contracts,
        Strategies,
        Facilities,
        Milestones,
        Tech
    }

    /// <summary>
    /// Pure category and subject decisions for Timeline rows.
    ///
    /// <para>Classification reads the LEDGER ACTION TYPE, never the display
    /// <see cref="TimelineEntryType"/>: the strategy currency-exchange science legs
    /// (<see cref="GameActionType.StrategyScienceDebit"/> /
    /// <see cref="GameActionType.StrategyScienceCredit"/>) reuse the ScienceSpending /
    /// ScienceEarning display bucket, so a Tech filter keyed on the display type would
    /// pick them up. Tech is a <see cref="GameActionType.ScienceSpending"/> that names a
    /// tech node, and nothing else.</para>
    /// </summary>
    internal static class TimelineCareerCategories
    {
        /// <summary>The five categories in their button order (Career view's row 2).</summary>
        internal static readonly TimelineCareerCategory[] Ordered = new[]
        {
            TimelineCareerCategory.Contracts,
            TimelineCareerCategory.Strategies,
            TimelineCareerCategory.Facilities,
            TimelineCareerCategory.Milestones,
            TimelineCareerCategory.Tech,
        };

        /// <summary>The category of one ledger action, or None.</summary>
        internal static TimelineCareerCategory Classify(GameAction action)
        {
            if (action == null) return TimelineCareerCategory.None;
            switch (action.Type)
            {
                case GameActionType.ContractAccept:
                case GameActionType.ContractComplete:
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    return TimelineCareerCategory.Contracts;

                case GameActionType.StrategyActivate:
                case GameActionType.StrategyDeactivate:
                    return TimelineCareerCategory.Strategies;

                case GameActionType.FacilityUpgrade:
                case GameActionType.FacilityDestruction:
                case GameActionType.FacilityRepair:
                    return TimelineCareerCategory.Facilities;

                case GameActionType.MilestoneAchievement:
                    return TimelineCareerCategory.Milestones;

                case GameActionType.ScienceSpending:
                    // A science spend with no node is not a tech unlock.
                    return string.IsNullOrEmpty(action.NodeId)
                        ? TimelineCareerCategory.None
                        : TimelineCareerCategory.Tech;

                default:
                    return TimelineCareerCategory.None;
            }
        }

        /// <summary>
        /// The subject a row is about inside its category, the key a cross-link scrolls
        /// to: the contract id, strategy id, facility id (building ids fold to their
        /// facility, <c>SpaceCenter/LaunchPad/...</c> -> <c>LaunchPad</c>), milestone id
        /// or tech node id. Null for a None row or an action that carries no key.
        /// </summary>
        internal static string ResolveSubjectId(GameAction action, TimelineCareerCategory category)
        {
            if (action == null) return null;
            string id;
            switch (category)
            {
                case TimelineCareerCategory.Contracts: id = action.ContractId; break;
                case TimelineCareerCategory.Strategies: id = action.StrategyId; break;
                case TimelineCareerCategory.Facilities:
                    id = FacilityDisplayNames.FacilityIdForBuilding(action.FacilityId);
                    break;
                case TimelineCareerCategory.Milestones: id = action.MilestoneId; break;
                case TimelineCareerCategory.Tech: id = action.NodeId; break;
                default: id = null; break;
            }
            return string.IsNullOrEmpty(id) ? null : id;
        }

        /// <summary>
        /// Whether a category exists in a game mode. Career: all five. Science: Facilities,
        /// Milestones and Tech (no contracts or strategies there). Sandbox and the mission
        /// modes: none. An unknown mode (no game loaded, headless tests) gates nothing.
        /// Reads the GAME mode only, never the UI complexity mode: the Timeline shows the
        /// same controls in Basic and Advanced.
        /// </summary>
        internal static bool IsAvailableInMode(TimelineCareerCategory category, Game.Modes? mode)
        {
            if (category == TimelineCareerCategory.None) return false;
            if (!mode.HasValue) return true;
            switch (mode.Value)
            {
                case Game.Modes.CAREER:
                    return true;
                case Game.Modes.SCIENCE_SANDBOX:
                    return category == TimelineCareerCategory.Facilities
                        || category == TimelineCareerCategory.Milestones
                        || category == TimelineCareerCategory.Tech;
                default:
                    return false;
            }
        }

        /// <summary>The categories a mode shows, in button order.</summary>
        internal static List<TimelineCareerCategory> AvailableInMode(Game.Modes? mode)
        {
            var result = new List<TimelineCareerCategory>(Ordered.Length);
            for (int i = 0; i < Ordered.Length; i++)
                if (IsAvailableInMode(Ordered[i], mode)) result.Add(Ordered[i]);
            return result;
        }

        /// <summary>Whether the Career view exists at all in a mode (false in Sandbox).</summary>
        internal static bool AnyAvailableInMode(Game.Modes? mode)
            => AvailableInMode(mode).Count > 0;

        /// <summary>
        /// The category the Career button opens: the last one used when the mode still
        /// shows it, else the first the mode shows (Contracts in Career, Facilities in
        /// Science), else None (Sandbox).
        /// </summary>
        internal static TimelineCareerCategory ResolveRemembered(
            TimelineCareerCategory last, Game.Modes? mode)
        {
            if (IsAvailableInMode(last, mode)) return last;
            List<TimelineCareerCategory> available = AvailableInMode(mode);
            return available.Count > 0 ? available[0] : TimelineCareerCategory.None;
        }

        /// <summary>A lower-case game-mode token for logs and seam refusals.</summary>
        internal static string ModeToken(Game.Modes? mode)
        {
            if (!mode.HasValue) return "unknown";
            switch (mode.Value)
            {
                case Game.Modes.CAREER: return "career";
                case Game.Modes.SCIENCE_SANDBOX: return "science";
                case Game.Modes.SANDBOX: return "sandbox";
                case Game.Modes.MISSION: return "mission";
                case Game.Modes.MISSION_BUILDER: return "missionbuilder";
                default: return mode.Value.ToString().ToLowerInvariant();
            }
        }
    }
}
