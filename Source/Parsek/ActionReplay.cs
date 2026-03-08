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
                                ParsekLog.Verbose("ActionReplay",
                                    $"Skipping TechResearched '{evt.key}' (placeholder)");
                                skipCount++;
                                break;
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
    }
}
