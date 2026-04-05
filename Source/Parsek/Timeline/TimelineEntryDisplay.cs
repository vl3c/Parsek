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
                case TimelineEntryType.VesselSpawn:
                case TimelineEntryType.MilestoneAchievement:
                case TimelineEntryType.ContractComplete:
                case TimelineEntryType.ContractFail:
                case TimelineEntryType.FacilityUpgrade:
                case TimelineEntryType.FacilityDestruction:
                case TimelineEntryType.KerbalHire:
                case TimelineEntryType.FundsInitial:
                case TimelineEntryType.ScienceInitial:
                case TimelineEntryType.ReputationInitial:
                    return SignificanceTier.T1;

                default:
                    return SignificanceTier.T2;
            }
        }

        /// <summary>Display text for a recording start event, including mission duration.</summary>
        internal static string GetRecordingStartText(string vesselName, double durationSeconds, bool isEva)
        {
            string prefix = isEva ? "EVA" : "Launch";
            string duration = FormatDuration(durationSeconds);
            if (string.IsNullOrEmpty(duration))
                return $"{prefix}: {vesselName}";
            return $"{prefix}: {vesselName} (MET {duration})";
        }

        /// <summary>
        /// Formats a duration in seconds as a human-readable string showing only non-zero components.
        /// E.g., 90061 seconds → "1d, 1h, 1m, 1s". Returns empty string for zero or negative.
        /// </summary>
        internal static string FormatDuration(double seconds)
        {
            if (seconds <= 0) return "";

            // KSP uses 6-hour days, 426-day years (Kerbin calendar)
            int totalSeconds = (int)seconds;
            int s = totalSeconds % 60;
            int totalMinutes = totalSeconds / 60;
            int m = totalMinutes % 60;
            int totalHours = totalMinutes / 60;
            int h = totalHours % 6;       // KSP day = 6 hours
            int totalDays = totalHours / 6;
            int d = totalDays % 426;       // KSP year = 426 days
            int y = totalDays / 426;

            var parts = new System.Collections.Generic.List<string>(5);
            if (y > 0) parts.Add($"{y}y");
            if (d > 0) parts.Add($"{d}d");
            if (h > 0) parts.Add($"{h}h");
            if (m > 0) parts.Add($"{m}m");
            if (s > 0) parts.Add($"{s}s");

            return parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "";
        }

        /// <summary>
        /// Display text for a vessel spawn event with situation context.
        /// Uses VesselSituation if available ("Orbiting Kerbin", "Landed on Mun"),
        /// falls back to terminal state name, falls back to no parenthetical.
        /// </summary>
        internal static string GetVesselSpawnText(string vesselName, TerminalState? state, string vesselSituation)
        {
            // Prefer the full situation string (includes body name)
            if (!string.IsNullOrEmpty(vesselSituation))
                return $"Spawn: {vesselName} ({vesselSituation})";

            // Fall back to terminal state name
            string stateText = FormatTerminalState(state);
            if (!string.IsNullOrEmpty(stateText))
                return $"Spawn: {vesselName} ({stateText})";

            return $"Spawn: {vesselName}";
        }

        /// <summary>Maps a terminal state to human-readable text. Null returns empty string.</summary>
        internal static string FormatTerminalState(TerminalState? state)
        {
            if (state == null) return "";
            switch (state.Value)
            {
                case TerminalState.Orbiting:   return "Orbiting";
                case TerminalState.Landed:     return "Landed";
                case TerminalState.Splashed:   return "Splashed";
                case TerminalState.SubOrbital: return "Sub-orbital";
                case TerminalState.Destroyed:  return "Destroyed";
                case TerminalState.Recovered:  return "Recovered";
                case TerminalState.Docked:     return "Docked";
                case TerminalState.Boarded:    return "Boarded";
                default:                       return "";
            }
        }

        /// <summary>
        /// Humanizes a KSP science subject ID.
        /// "crewReport@KerbinSrfLaunchpad" → "Crew Report @ Kerbin Srf Launchpad"
        /// </summary>
        internal static string HumanizeSubjectId(string subjectId)
        {
            if (string.IsNullOrEmpty(subjectId)) return subjectId;

            // Split at @ into experiment and location
            int atIdx = subjectId.IndexOf('@');
            string experiment = atIdx >= 0 ? subjectId.Substring(0, atIdx) : subjectId;
            string location = atIdx >= 0 ? subjectId.Substring(atIdx + 1) : "";

            // Insert spaces before uppercase letters (camelCase → spaced)
            experiment = InsertSpacesBeforeUppercase(experiment);
            location = InsertSpacesBeforeUppercase(location);

            // Capitalize first letter
            if (experiment.Length > 0)
                experiment = char.ToUpper(experiment[0]) + experiment.Substring(1);

            // Clean up redundant KSP situation prefixes
            // "Srf Landed" → "Landed", "Srf Splashed" → "Splashed" (Srf is redundant with these)
            location = location.Replace("Srf Landed ", "Landed ");
            location = location.Replace("Srf Splashed ", "Splashed ");
            // "Srf" without Landed/Splashed (e.g. "KerbinSrf" → "Kerbin Srf") — strip standalone Srf
            location = location.Replace(" Srf ", " ");
            if (location.EndsWith(" Srf"))
                location = location.Substring(0, location.Length - 4);

            if (string.IsNullOrEmpty(location))
                return experiment;
            return $"{experiment} @ {location}";
        }

        private static string InsertSpacesBeforeUppercase(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new System.Text.StringBuilder(s.Length + 8);
            sb.Append(s[0]);
            for (int i = 1; i < s.Length; i++)
            {
                if (char.IsUpper(s[i]) && !char.IsUpper(s[i - 1]))
                    sb.Append(' ');
                sb.Append(s[i]);
            }
            return sb.ToString();
        }


        /// <summary>
        /// Display text for a game action timeline entry.
        /// Custom handling for science subjects (humanized), kerbal assignments (with vessel),
        /// ScienceInitial, and ReputationInitial. All others delegate to GameActionDisplay.
        /// </summary>
        internal static string GetGameActionText(GameAction action, string vesselName)
        {
            if (action == null)
                return "";

            switch (action.Type)
            {
                case GameActionType.ScienceInitial:
                    return string.Format(IC, "Starting science: {0:F1}", action.InitialScience);

                case GameActionType.ReputationInitial:
                    return string.Format(IC, "Starting reputation: {0:F0}", action.InitialReputation);

                case GameActionType.MilestoneAchievement:
                {
                    string rawId = action.MilestoneId ?? "unknown";
                    // KSP milestone IDs use "/" as body separator: "Kerbin/Splashdown" → "Kerbin - Splashdown"
                    rawId = rawId.Replace("/", " - ");
                    string mid = InsertSpacesBeforeUppercase(rawId);
                    if (mid.Length > 0) mid = char.ToUpper(mid[0]) + mid.Substring(1);
                    string desc = "Milestone: " + mid;
                    if (action.MilestoneFundsAwarded != 0)
                        desc += string.Format(IC, " +{0:0} funds", action.MilestoneFundsAwarded);
                    if (action.MilestoneRepAwarded != 0)
                        desc += string.Format(IC, " +{0:0.#} rep", action.MilestoneRepAwarded);
                    return desc;
                }

                case GameActionType.ScienceEarning:
                    return string.Format(IC, "{0} +{1:0.#} sci",
                        HumanizeSubjectId(action.SubjectId ?? "unknown"), action.ScienceAwarded);

                case GameActionType.KerbalAssignment:
                {
                    string crew = string.Format(IC, "{0} ({1})",
                        action.KerbalName ?? "unknown", action.KerbalRole ?? "unknown");
                    if (!string.IsNullOrEmpty(vesselName))
                        crew += $" on {vesselName}";
                    return crew;
                }

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
                    ParsekLog.Warn("Timeline",
                        $"Unknown GameActionType '{type}' — mapping to LegacyEvent");
                    return TimelineEntryType.LegacyEvent;
            }
        }
    }
}
