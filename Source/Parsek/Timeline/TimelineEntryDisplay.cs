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
                case TimelineEntryType.CrewDeath:
                case TimelineEntryType.UnfinishedFlightSeparation:
                case TimelineEntryType.MilestoneAchievement:
                case TimelineEntryType.ContractComplete:
                case TimelineEntryType.ContractFail:
                case TimelineEntryType.FacilityUpgrade:
                case TimelineEntryType.FacilityDestruction:
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

        /// <summary>Display text for a recording start event, including mission duration and location.</summary>
        internal static string GetRecordingStartText(string vesselName, double durationSeconds,
            bool isEva, string parentVesselName,
            string startBodyName = null, string startBiome = null, string launchSiteName = null)
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

            // Location context: prefer launch site name over biome for launches
            // "from Launch Pad on Kerbin" > "at KSC on Kerbin" > "on Kerbin"
            string locationName = !string.IsNullOrEmpty(launchSiteName) ? launchSiteName : startBiome;
            if (!string.IsNullOrEmpty(locationName) && !string.IsNullOrEmpty(startBodyName))
            {
                string prep = !string.IsNullOrEmpty(launchSiteName) ? "from" : "at";
                sb.Append($" {prep} {locationName} on {startBodyName}");
            }
            else if (!string.IsNullOrEmpty(startBodyName))
                sb.Append($" on {startBodyName}");

            if (!string.IsNullOrEmpty(duration))
                sb.Append($" (MET {duration})");

            return sb.ToString();
        }

        /// <summary>
        /// Formats a duration in seconds as a human-readable string showing only non-zero components.
        /// E.g., 90061 seconds → "1d, 1h, 1m, 1s". Returns empty string for zero or negative.
        /// </summary>
        internal static string FormatDuration(double seconds)
            => ParsekTimeFormat.FormatDurationFull(seconds);

        /// <summary>
        /// Display text for an Unfinished-Flight separation event:
        /// "Separation of Unfinished Flight: Booster Y" — the row keeps a
        /// Play button so the player can re-fly the destroyed sibling
        /// directly from the timeline. Once the player merges (or
        /// otherwise finalizes) the re-flight, the recording is no longer
        /// an Unfinished Flight and TimelineBuilder emits a plain
        /// <see cref="GetSeparationText"/> entry instead.
        /// </summary>
        internal static string GetUnfinishedFlightSeparationText(string vesselName)
        {
            string name = string.IsNullOrEmpty(vesselName) ? "<unnamed>" : vesselName;
            return $"Separation of Unfinished Flight: {name}";
        }

        /// <summary>
        /// Display text for a regular separation event (post-merge or any
        /// non-UF tree-child): "Separation: Booster Y" — same shape the
        /// table uses for "Launch: Vessel Name", just for the staging
        /// split that produced this child.
        /// </summary>
        internal static string GetSeparationText(string vesselName)
        {
            string name = string.IsNullOrEmpty(vesselName) ? "<unnamed>" : vesselName;
            return $"Separation: {name}";
        }

        /// <summary>
        /// Display text for a crew death event: "Lost: Bob Kerman (Vessel Name)".
        /// </summary>
        internal static string GetCrewDeathText(string kerbalName, string vesselName)
        {
            if (string.IsNullOrEmpty(vesselName))
                return $"Lost: {kerbalName ?? "unknown"}";
            return $"Lost: {kerbalName ?? "unknown"} ({vesselName})";
        }

        /// <summary>
        /// Display text for a vessel spawn event with situation context.
        /// Uses VesselSituation if available ("Orbiting Kerbin", "Landed on Mun"),
        /// falls back to terminal state name, falls back to no parenthetical.
        /// </summary>
        internal static string GetVesselSpawnText(string vesselName, TerminalState? state,
            string vesselSituation, bool isEva, string parentVesselName,
            string terminalOrbitBody, string segmentBodyName, string endBiome = null)
        {
            // Boarded EVA: "Board: Jeb (Mun Lander)" - kerbal returned to parent vessel
            if (isEva && state == TerminalState.Boarded && !string.IsNullOrEmpty(parentVesselName))
                return $"Board: {vesselName} ({parentVesselName})";

            // Prefer the full situation string (includes body name)
            // Inject biome if available: "Landed Mun" -> "Landed at Midlands on Mun"
            if (!string.IsNullOrEmpty(vesselSituation))
            {
                if (!string.IsNullOrEmpty(endBiome))
                    return $"Spawn: {vesselName} ({InjectBiomeIntoSituation(vesselSituation, endBiome)})";
                return $"Spawn: {vesselName} ({vesselSituation})";
            }

            // Fall back to terminal state with body context
            string stateText = FormatTerminalState(state);
            if (!string.IsNullOrEmpty(stateText))
            {
                string body = !string.IsNullOrEmpty(terminalOrbitBody) ? terminalOrbitBody : segmentBodyName;
                if (!string.IsNullOrEmpty(body))
                {
                    bool usesOn = state == TerminalState.Landed || state == TerminalState.Splashed;
                    if (usesOn && !string.IsNullOrEmpty(endBiome))
                        return $"Spawn: {vesselName} ({stateText} at {endBiome} on {body})";
                    return usesOn
                        ? $"Spawn: {vesselName} ({stateText} on {body})"
                        : $"Spawn: {vesselName} ({stateText} {body})";
                }
                return $"Spawn: {vesselName} ({stateText})";
            }

            return $"Spawn: {vesselName}";
        }

        /// <summary>
        /// Injects biome into a VesselSituation string like "Landed Mun" -> "Landed at Midlands on Mun".
        /// Only applies to surface situations (Landed, Splashed, Prelaunch). Orbital situations pass through unchanged.
        /// </summary>
        internal static string InjectBiomeIntoSituation(string vesselSituation, string biome)
        {
            if (string.IsNullOrEmpty(vesselSituation) || string.IsNullOrEmpty(biome))
                return vesselSituation;

            // VesselSituation format is "{situation} {bodyName}" e.g. "Landed Mun", "ORBITING Kerbin"
            // For surface situations, insert "at {biome} on" before body
            int spaceIdx = vesselSituation.IndexOf(' ');
            if (spaceIdx < 0) return vesselSituation;

            string sit = vesselSituation.Substring(0, spaceIdx);
            string body = vesselSituation.Substring(spaceIdx + 1);

            // Only inject biome for surface situations
            string sitUpper = sit.ToUpperInvariant();
            if (sitUpper == "LANDED" || sitUpper == "SPLASHED" || sitUpper == "PRELAUNCH")
                return $"{sit} at {biome} on {body}";

            return vesselSituation;
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
        internal static string GetGameActionText(GameAction action, string vesselName, Game.Modes? currentMode)
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
                    return GetMilestoneAchievementText(
                        action.MilestoneId,
                        action.MilestoneFundsAwarded,
                        action.MilestoneRepAwarded,
                        action.MilestoneScienceAwarded);

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
                    string label = string.Format(IC, "{0} -{1:0}", source, action.FundsSpent);
                    // Bug #452: distinguish unclaimed cancelled-rollout entries from
                    // adopted (recording-tagged) build costs in the timeline view.
                    if (GameActionDisplay.IsUnclaimedRolloutAction(action))
                        label += GameActionDisplay.CancelledRolloutSuffix;
                    return label;
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

                case GameActionType.KerbalHire:
                    return GameActionDisplay.GetKerbalHireDescription(action, currentMode);

                default:
                    return GameActionDisplay.GetDescription(action, currentMode);
            }
        }

        internal static string GetMilestoneAchievementText(
            string milestoneId,
            float fundsAwarded,
            float repAwarded,
            float scienceAwarded)
        {
            string desc = "Milestone: " + HumanizeMilestoneId(milestoneId);
            if (fundsAwarded != 0)
                desc += string.Format(IC, " +{0:0} funds", fundsAwarded);
            if (repAwarded != 0)
                desc += string.Format(IC, " +{0:0.#} rep", repAwarded);
            if (scienceAwarded != 0)
                desc += string.Format(IC, " +{0:0.#} sci", scienceAwarded);
            return desc;
        }

        internal static string HumanizeMilestoneId(string milestoneId)
        {
            string rawId = milestoneId ?? "unknown";
            string[] parts = rawId.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                string humanized = InsertSpacesBeforeUppercase(parts[i]);
                if (humanized.Length > 0)
                    humanized = char.ToUpper(humanized[0]) + humanized.Substring(1);
                parts[i] = humanized;
            }

            return string.Join(" - ", parts);
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
