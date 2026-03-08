using System;
using System.Collections.Generic;

namespace Parsek
{
    internal enum ReplayDecision
    {
        Skip,
        Act,
        Fail
    }

    internal static class ActionReplay
    {
        /// <summary>
        /// Returns true if the given event type is one that ActionReplay can replay.
        /// </summary>
        internal static bool IsReplayableEvent(GameStateEventType type)
        {
            switch (type)
            {
                case GameStateEventType.TechResearched:
                case GameStateEventType.PartPurchased:
                case GameStateEventType.FacilityUpgraded:
                case GameStateEventType.CrewHired:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Iterates committed milestones and replays any unreplayed replayable events.
        /// Sets suppression flags around the replay loop to prevent recording replayed
        /// actions as new game state events and to bypass blocking Harmony patches.
        /// </summary>
        internal static void ReplayCommittedActions(IReadOnlyList<Milestone> milestones)
        {
            if (milestones == null || milestones.Count == 0) return;

            // Count unreplayed replayable events across all committed milestones
            int totalActions = 0;
            int milestoneCount = 0;
            for (int i = 0; i < milestones.Count; i++)
            {
                var m = milestones[i];
                if (!m.Committed) continue;

                int unreplayed = 0;
                for (int j = m.LastReplayedEventIndex + 1; j < m.Events.Count; j++)
                {
                    if (IsReplayableEvent(m.Events[j].eventType))
                        unreplayed++;
                }

                if (unreplayed > 0)
                {
                    totalActions += unreplayed;
                    milestoneCount++;
                }
            }

            if (totalActions == 0) return;

            ParsekLog.Info("ActionReplay",
                $"Replaying {totalActions} unreplayed actions from {milestoneCount} milestones");

            int techCount = 0;
            int partCount = 0;
            int facilityCount = 0;
            int crewCount = 0;
            int skipCount = 0;
            int failCount = 0;

            GameStateRecorder.SuppressActionReplay = true;
            GameStateRecorder.SuppressBlockingPatches = true;
            GameStateRecorder.SuppressCrewEvents = true;
            try
            {
                for (int i = 0; i < milestones.Count; i++)
                {
                    var m = milestones[i];
                    if (!m.Committed) continue;

                    for (int j = m.LastReplayedEventIndex + 1; j < m.Events.Count; j++)
                    {
                        var evt = m.Events[j];
                        if (!IsReplayableEvent(evt.eventType)) continue;

                        // Phase 0: all event types are placeholders that skip
                        switch (evt.eventType)
                        {
                            case GameStateEventType.TechResearched:
                            {
                                bool wasSkipped;
                                if (ReplayTechUnlock(evt, out wasSkipped))
                                {
                                    if (wasSkipped) skipCount++;
                                    else techCount++;
                                }
                                else
                                {
                                    if (wasSkipped) skipCount++;
                                    else failCount++;
                                }
                                break;
                            }
                            case GameStateEventType.PartPurchased:
                                ParsekLog.Verbose("ActionReplay",
                                    $"Skipping PartPurchased '{evt.key}' (placeholder)");
                                skipCount++;
                                break;
                            case GameStateEventType.FacilityUpgraded:
                                ParsekLog.Verbose("ActionReplay",
                                    $"Skipping FacilityUpgraded '{evt.key}' (placeholder)");
                                skipCount++;
                                break;
                            case GameStateEventType.CrewHired:
                                ParsekLog.Verbose("ActionReplay",
                                    $"Skipping CrewHired '{evt.key}' (placeholder)");
                                skipCount++;
                                break;
                        }
                    }
                }
            }
            finally
            {
                GameStateRecorder.SuppressActionReplay = false;
                GameStateRecorder.SuppressBlockingPatches = false;
                GameStateRecorder.SuppressCrewEvents = false;
            }

            ParsekLog.Info("ActionReplay",
                $"Replay complete: {techCount} tech, {partCount} parts, {facilityCount} facilities, {crewCount} crew ({skipCount} skipped, {failCount} failed)");
        }

        /// <summary>
        /// Unlocks a tech node programmatically WITHOUT deducting science.
        /// Uses ProtoTechNode + UnlockProtoTechNode to bypass RDTech.UnlockTech
        /// which would deduct science (already handled by budget deduction).
        /// </summary>
        internal static bool ReplayTechUnlock(GameStateEvent e, out bool skipped)
        {
            skipped = false;
            string techId = e.key;

            var decision = DecideTechReplay(techId,
                ResearchAndDevelopment.Instance != null
                && ResearchAndDevelopment.GetTechnologyState(techId) == RDTech.State.Available);

            if (decision == ReplayDecision.Skip)
            {
                skipped = true;
                ParsekLog.Info("ActionReplay",
                    $"Tech unlock: '{techId}' — already researched, skipping");
                return true;
            }

            if (decision == ReplayDecision.Fail)
            {
                ParsekLog.Warn("ActionReplay", "Tech unlock: empty techId — FAILED");
                return false;
            }

            if (ResearchAndDevelopment.Instance == null)
            {
                ParsekLog.Warn("ActionReplay",
                    $"Tech unlock: '{techId}' — R&D instance null, FAILED");
                return false;
            }

            try
            {
                var protoNode = new ProtoTechNode
                {
                    techID = techId,
                    state = RDTech.State.Available,
                    partsPurchased = new List<AvailablePart>()
                };

                string partsStr = ParseDetailField(e.detail, "parts");
                if (!string.IsNullOrEmpty(partsStr))
                {
                    var partNames = partsStr.Split(',');
                    for (int i = 0; i < partNames.Length; i++)
                    {
                        string partName = partNames[i].Trim();
                        if (string.IsNullOrEmpty(partName)) continue;
                        var partInfo = PartLoader.getPartInfoByName(partName);
                        if (partInfo != null)
                            protoNode.partsPurchased.Add(partInfo);
                        else
                            ParsekLog.Verbose("ActionReplay",
                                $"Tech unlock: part '{partName}' not found in PartLoader — skipped");
                    }
                }

                ResearchAndDevelopment.Instance.UnlockProtoTechNode(protoNode);
                ParsekLog.Info("ActionReplay",
                    $"Tech unlock: '{techId}' — success ({protoNode.partsPurchased.Count} parts)");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("ActionReplay",
                    $"Tech unlock: '{techId}' — FAILED: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Pure decision logic for tech replay — testable without KSP runtime.
        /// </summary>
        internal static ReplayDecision DecideTechReplay(string techId, bool isAlreadyResearched)
        {
            if (string.IsNullOrEmpty(techId)) return ReplayDecision.Fail;
            if (isAlreadyResearched) return ReplayDecision.Skip;
            return ReplayDecision.Act;
        }

        /// <summary>
        /// Parses a named field from a semicolon-delimited key=value detail string.
        /// Example: ParseDetailField("cost=5;parts=a,b", "parts") returns "a,b".
        /// Returns null if not found or detail is empty.
        /// </summary>
        internal static string ParseDetailField(string detail, string fieldName)
        {
            if (string.IsNullOrEmpty(detail)) return null;
            var pairs = detail.Split(';');
            for (int i = 0; i < pairs.Length; i++)
            {
                int eq = pairs[i].IndexOf('=');
                if (eq < 0) continue;
                if (pairs[i].Substring(0, eq).Trim() == fieldName)
                    return pairs[i].Substring(eq + 1).Trim();
            }
            return null;
        }
    }
}
