using System.Collections.Generic;
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

        // KSP stock strategy ID → human-readable name
        private static readonly Dictionary<string, string> StrategyNames = new Dictionary<string, string>
        {
            { "AggressiveNeg", "Aggressive Negotiations" },
            { "AppreciationCamp", "Appreciation Campaign" },
            { "FundraisingCamp", "Fundraising Campaign" },
            { "OutreachProg", "Outreach Program" },
            { "PatentsLic", "Patents Licensing" },
            { "RecoveryTransp", "Recovery Transponder" },
            { "UnpaidInterns", "Unpaid Research Program" }
        };

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

        /// <summary>
        /// Returns true if this game action type is a deliberate player action (KSC decisions).
        /// Returns false for gameplay events (consequences of flight, milestones, etc.).
        /// </summary>
        internal static bool IsPlayerAction(TimelineEntryType type)
        {
            switch (type)
            {
                case TimelineEntryType.ScienceSpending:      // tech unlock
                case TimelineEntryType.FundsSpending:        // vessel build, facility, hire
                case TimelineEntryType.ContractAccept:       // accepted contract
                case TimelineEntryType.ContractCancel:       // cancelled contract
                case TimelineEntryType.KerbalHire:           // hired kerbal
                case TimelineEntryType.FacilityUpgrade:      // upgraded facility
                case TimelineEntryType.FacilityRepair:       // repaired facility
                case TimelineEntryType.StrategyActivate:     // activated strategy
                case TimelineEntryType.StrategyDeactivate:   // deactivated strategy
                case TimelineEntryType.FundsInitial:         // career seed
                case TimelineEntryType.ScienceInitial:       // career seed
                case TimelineEntryType.ReputationInitial:    // career seed
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>Display text for a recording start event, including mission duration.</summary>
        internal static string GetRecordingStartText(string vesselName, double durationSeconds,
            bool isEva, string parentVesselName)
        {
            string prefix = isEva ? "EVA" : "Launch";
            string duration = FormatDuration(durationSeconds);

            var sb = new System.Text.StringBuilder();
            sb.Append(prefix);
            sb.Append(": ");
            sb.Append(vesselName);

            // EVA shows source vessel: "EVA: Jeb from Mun Lander"
            if (isEva && !string.IsNullOrEmpty(parentVesselName))
                sb.Append($" from {parentVesselName}");

            if (!string.IsNullOrEmpty(duration))
                sb.Append($" (MET {duration})");

            return sb.ToString();
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
        internal static string GetVesselSpawnText(string vesselName, TerminalState? state,
            string vesselSituation, bool isEva, string parentVesselName, string terminalOrbitBody)
        {
            // Boarded EVA: "Board: Jeb (Mun Lander)" — kerbal returned to parent vessel
            if (isEva && state == TerminalState.Boarded && !string.IsNullOrEmpty(parentVesselName))
                return $"Board: {vesselName} ({parentVesselName})";

            // Prefer the full situation string (includes body name)
            if (!string.IsNullOrEmpty(vesselSituation))
                return $"Spawn: {vesselName} ({vesselSituation})";

            // Fall back to terminal state with body context for orbital states
            string stateText = FormatTerminalState(state);
            if (!string.IsNullOrEmpty(stateText))
            {
                if (!string.IsNullOrEmpty(terminalOrbitBody) &&
                    (state == TerminalState.Orbiting || state == TerminalState.SubOrbital))
                    return $"Spawn: {vesselName} ({stateText} {terminalOrbitBody})";
                return $"Spawn: {vesselName} ({stateText})";
            }

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

        /// <summary>Maps KSP strategy ID to human name, falls back to camelCase split.</summary>
        internal static string HumanizeStrategyId(string strategyId)
        {
            if (string.IsNullOrEmpty(strategyId)) return "unknown";
            string name;
            if (StrategyNames.TryGetValue(strategyId, out name)) return name;
            // Fallback for modded strategies: camelCase split + capitalize
            name = InsertSpacesBeforeUppercase(strategyId);
            if (name.Length > 0) name = char.ToUpper(name[0]) + name.Substring(1);
            return name;
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

                case GameActionType.StrategyActivate:
                {
                    string sid = HumanizeStrategyId(action.StrategyId);
                    return string.Format(IC, "Activate: {0} ({1:P0} {2}\u2192{3})",
                        sid, action.Commitment, action.SourceResource, action.TargetResource);
                }

                case GameActionType.StrategyDeactivate:
                    return "Deactivate: " + HumanizeStrategyId(action.StrategyId);

                case GameActionType.FundsSpending:
                {
                    string source;
                    switch (action.FundsSpendingSource)
                    {
                        case FundsSpendingSource.VesselBuild:     source = "Build"; break;
                        case FundsSpendingSource.FacilityUpgrade: source = "Facility upgrade"; break;
                        case FundsSpendingSource.FacilityRepair:  source = "Facility repair"; break;
                        case FundsSpendingSource.KerbalHire:      source = "Hire"; break;
                        case FundsSpendingSource.ContractPenalty:  source = "Contract penalty"; break;
                        case FundsSpendingSource.Strategy:        source = "Strategy"; break;
                        default:                                  source = "Expense"; break;
                    }
                    return string.Format(IC, "{0} -{1:0}", source, action.FundsSpent);
                }

                case GameActionType.ScienceSpending:
                {
                    string node = InsertSpacesBeforeUppercase(action.NodeId ?? "unknown");
                    if (node.Length > 0) node = char.ToUpper(node[0]) + node.Substring(1);
                    return string.Format(IC, "Tech: {0} -{1:0.#} sci", node, action.Cost);
                }

                case GameActionType.ScienceEarning:
                    return string.Format(IC, "{0} +{1:0.#} sci",
                        HumanizeSubjectId(action.SubjectId ?? "unknown"), action.ScienceAwarded);

                case GameActionType.KerbalAssignment:
                {
                    string crew = string.Format(IC, "Assign: {0} ({1})",
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
