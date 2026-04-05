using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Display helpers for timeline entries — tier classification, display text generation,
    /// and type mapping. Analogous to <see cref="GameActionDisplay"/> for game actions.
    /// </summary>
    internal static class TimelineEntryDisplay
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Returns the default significance tier for the given entry type.
        /// T1 = Overview (mission structure), T2 = Detail (resource transactions).
        /// </summary>
        internal static SignificanceTier GetTier(TimelineEntryType type)
        {
            switch (type)
            {
                case TimelineEntryType.RecordingStart:
                case TimelineEntryType.RecordingEnd:
                case TimelineEntryType.VesselSpawn:
                case TimelineEntryType.MilestoneAchievement:
                case TimelineEntryType.ContractComplete:
                case TimelineEntryType.ContractFail:
                case TimelineEntryType.FacilityUpgrade:
                case TimelineEntryType.FacilityDestruction:
                case TimelineEntryType.KerbalHire:
                case TimelineEntryType.GhostChainWindow:
                case TimelineEntryType.FundsInitial:
                case TimelineEntryType.ScienceInitial:
                case TimelineEntryType.ReputationInitial:
                    return SignificanceTier.T1;

                default:
                    return SignificanceTier.T2;
            }
        }

        /// <summary>Display text for a recording start event.</summary>
        internal static string GetRecordingStartText(string vesselName)
        {
            return $"Launch: {vesselName}";
        }

        /// <summary>
        /// Display text for a recording end event.
        /// Maps each terminal state to human-readable text with vessel name.
        /// Null terminal state produces "End: {vesselName}".
        /// </summary>
        internal static string GetRecordingEndText(string vesselName, TerminalState? state)
        {
            if (state == null)
                return $"End: {vesselName}";

            switch (state.Value)
            {
                case TerminalState.Orbiting:
                    return $"Orbiting: {vesselName}";
                case TerminalState.Landed:
                    return $"Landed: {vesselName}";
                case TerminalState.Splashed:
                    return $"Splashed: {vesselName}";
                case TerminalState.SubOrbital:
                    return $"Sub-orbital: {vesselName}";
                case TerminalState.Destroyed:
                    return $"Destroyed: {vesselName}";
                case TerminalState.Recovered:
                    return $"Recovered: {vesselName}";
                case TerminalState.Docked:
                    return $"Docked: {vesselName}";
                case TerminalState.Boarded:
                    return $"Boarded: {vesselName}";
                default:
                    return $"End: {vesselName}";
            }
        }

        /// <summary>Display text for a vessel spawn event.</summary>
        internal static string GetVesselSpawnText(string vesselName)
        {
            return $"Spawn: {vesselName}";
        }

        /// <summary>
        /// Display text for a ghost chain window entry.
        /// Shows vessel name and the UT range during which the ghost is active.
        /// </summary>
        internal static string GetGhostChainWindowText(string vesselName, double startUT, double endUT)
        {
            return string.Format(IC, "{0}: ghost UT {1:F0}\u2013{2:F0}", vesselName, startUT, endUT);
        }

        /// <summary>
        /// Display text for a game action timeline entry.
        /// Delegates to <see cref="GameActionDisplay.GetDescription"/> for most types,
        /// with custom handling for ScienceInitial and ReputationInitial.
        /// </summary>
        internal static string GetGameActionText(GameAction action)
        {
            if (action == null)
                return "";

            switch (action.Type)
            {
                case GameActionType.ScienceInitial:
                    return string.Format(IC, "Starting science: {0:F1}", action.InitialScience);

                case GameActionType.ReputationInitial:
                    return string.Format(IC, "Starting reputation: {0:F0}", action.InitialReputation);

                default:
                    return GameActionDisplay.GetDescription(action);
            }
        }

        /// <summary>
        /// Maps a <see cref="GameActionType"/> to the corresponding <see cref="TimelineEntryType"/>.
        /// The mapping is 1:1 for all 23 game action types. Unknown types log a warning
        /// and return <see cref="TimelineEntryType.LegacyEvent"/>.
        /// </summary>
        internal static TimelineEntryType MapGameActionType(GameActionType type)
        {
            switch (type)
            {
                case GameActionType.ScienceEarning:       return TimelineEntryType.ScienceEarning;
                case GameActionType.ScienceSpending:      return TimelineEntryType.ScienceSpending;
                case GameActionType.FundsEarning:         return TimelineEntryType.FundsEarning;
                case GameActionType.FundsSpending:        return TimelineEntryType.FundsSpending;
                case GameActionType.ReputationEarning:    return TimelineEntryType.ReputationEarning;
                case GameActionType.ReputationPenalty:     return TimelineEntryType.ReputationPenalty;
                case GameActionType.MilestoneAchievement: return TimelineEntryType.MilestoneAchievement;
                case GameActionType.ContractAccept:       return TimelineEntryType.ContractAccept;
                case GameActionType.ContractComplete:     return TimelineEntryType.ContractComplete;
                case GameActionType.ContractFail:         return TimelineEntryType.ContractFail;
                case GameActionType.ContractCancel:       return TimelineEntryType.ContractCancel;
                case GameActionType.KerbalAssignment:     return TimelineEntryType.KerbalAssignment;
                case GameActionType.KerbalHire:           return TimelineEntryType.KerbalHire;
                case GameActionType.KerbalRescue:         return TimelineEntryType.KerbalRescue;
                case GameActionType.KerbalStandIn:        return TimelineEntryType.KerbalStandIn;
                case GameActionType.FacilityUpgrade:      return TimelineEntryType.FacilityUpgrade;
                case GameActionType.FacilityDestruction:  return TimelineEntryType.FacilityDestruction;
                case GameActionType.FacilityRepair:       return TimelineEntryType.FacilityRepair;
                case GameActionType.StrategyActivate:     return TimelineEntryType.StrategyActivate;
                case GameActionType.StrategyDeactivate:   return TimelineEntryType.StrategyDeactivate;
                case GameActionType.FundsInitial:         return TimelineEntryType.FundsInitial;
                case GameActionType.ScienceInitial:       return TimelineEntryType.ScienceInitial;
                case GameActionType.ReputationInitial:    return TimelineEntryType.ReputationInitial;

                default:
                    ParsekLog.Warn("TimelineEntryDisplay",
                        $"Unknown GameActionType '{type}' — mapping to LegacyEvent");
                    return TimelineEntryType.LegacyEvent;
            }
        }
    }
}
