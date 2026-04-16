using System;
using System.Collections.Generic;
using System.IO;

namespace Parsek
{
    internal static class MilestoneStore
    {
        private static List<Milestone> milestones = new List<Milestone>();
        private static bool initialLoadDone = false;
        private static string lastSaveFolder = null;

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "?";
            return id.Length <= 8 ? id : id.Substring(0, 8);
        }

        internal static IReadOnlyList<Milestone> Milestones => milestones;
        internal static int MilestoneCount => milestones.Count;

        internal static uint CurrentEpoch { get; set; }

        /// <summary>
        /// Returns the highest EndUT among all milestones in the current epoch.
        /// Returns 0 if no milestones exist for the current epoch.
        /// Used by GameStateStore.PruneProcessedEvents to determine the threshold
        /// below which events have been consumed.
        /// </summary>
        internal static double GetLatestCommittedEndUT()
        {
            uint epoch = CurrentEpoch;
            double latest = 0;
            for (int i = 0; i < milestones.Count; i++)
            {
                if (milestones[i].Epoch == epoch && milestones[i].Committed && milestones[i].EndUT > latest)
                    latest = milestones[i].EndUT;
            }
            return latest;
        }

        internal static Milestone CreateMilestone(string recordingId, double currentUT)
        {
            double startUT = 0;
            uint epoch = CurrentEpoch;
            for (int i = 0; i < milestones.Count; i++)
            {
                if (milestones[i].Epoch == epoch && milestones[i].EndUT > startUT)
                    startUT = milestones[i].EndUT;
            }

            ParsekLog.Verbose("MilestoneStore",
                $"CreateMilestone: scanning events epoch={epoch}, window UT {startUT:F0}-{currentUT:F0}, recordingId={recordingId ?? "(flush)"}");

            var events = GameStateStore.Events;

            var filtered = new List<GameStateEvent>();
            int skippedEpoch = 0, skippedWindow = 0, skippedResource = 0;
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (e.epoch != epoch) { skippedEpoch++; continue; }
                if (e.ut <= startUT || e.ut > currentUT) { skippedWindow++; continue; }
                if (GameStateStore.IsMilestoneFilteredEvent(e.eventType)) { skippedResource++; continue; }
                filtered.Add(e);
            }

            ParsekLog.Verbose("MilestoneStore",
                $"CreateMilestone: {events.Count} total events, {filtered.Count} matched, " +
                $"skipped: epoch={skippedEpoch}, window={skippedWindow}, resource={skippedResource}");

            if (filtered.Count == 0)
            {
                ParsekLog.Verbose("MilestoneStore",
                    $"No game state events for milestone (epoch={epoch}, UT {startUT:F0}-{currentUT:F0}) — skipped");
                return null;
            }

            filtered.Sort((a, b) => a.ut.CompareTo(b.ut));

            var milestone = new Milestone
            {
                MilestoneId = Guid.NewGuid().ToString("N"),
                StartUT = startUT,
                EndUT = currentUT,
                RecordingId = recordingId ?? "",
                Epoch = epoch,
                Events = filtered,
                Committed = true,
                LastReplayedEventIndex = filtered.Count - 1
            };

            milestones.Add(milestone);
            ResourceBudget.Invalidate();
            ParsekLog.Info("MilestoneStore",
                $"Milestone created: id={ShortId(milestone.MilestoneId)}, {filtered.Count} events, " +
                $"UT {startUT:F0}-{currentUT:F0}, epoch={epoch}");

            for (int i = 0; i < filtered.Count; i++)
            {
                ParsekLog.Verbose("MilestoneStore",
                    $"  event[{i}]: {filtered[i].eventType} key='{filtered[i].key}' ut={filtered[i].ut:F1}");
            }

            return milestone;
        }

        /// <summary>
        /// Captures any game state events that occurred after the last milestone's
        /// EndUT into a new milestone. Called from ParsekScenario.OnSave to ensure
        /// events are preserved even if the player never commits a recording.
        /// Returns the created milestone, or null if no new events exist.
        /// </summary>
        internal static Milestone FlushPendingEvents(double currentUT)
        {
            ParsekLog.Verbose("MilestoneStore", $"FlushPendingEvents: currentUT={currentUT:F0}");
            return CreateMilestone(null, currentUT);
        }

        #region File I/O

        internal static bool SaveMilestoneFile()
        {
            string path = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildMilestonesRelativePath());
            if (path == null)
            {
                ParsekLog.Warn("MilestoneStore", "Cannot resolve milestones path — save skipped");
                return false;
            }

            try
            {
                RecordingPaths.EnsureGameStateDirectory();

                var rootNode = new ConfigNode("PARSEK_MILESTONES");
                rootNode.AddValue("version", 1);

                for (int i = 0; i < milestones.Count; i++)
                {
                    ConfigNode milestoneNode = rootNode.AddNode("MILESTONE");
                    milestones[i].SerializeInto(milestoneNode);
                }

                SafeWriteConfigNode(rootNode, path);

                ParsekLog.Info("MilestoneStore", $"Saved {milestones.Count} milestones to {path}");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("MilestoneStore", $"Failed to save milestones: {ex.Message}");
                return false;
            }
        }

        internal static bool LoadMilestoneFile()
        {
            string currentSave = HighLogic.SaveFolder;
            if (currentSave != lastSaveFolder)
            {
                initialLoadDone = false;
                lastSaveFolder = currentSave;
                ParsekLog.Verbose("MilestoneStore", $"Save folder changed to '{currentSave}' — resetting milestone load state");
            }

            if (initialLoadDone)
            {
                ParsekLog.Verbose("MilestoneStore", "LoadMilestoneFile: already loaded, skipping");
                return true;
            }

            initialLoadDone = true;
            milestones.Clear();

            string path = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildMilestonesRelativePath());
            if (path == null || !File.Exists(path))
            {
                ParsekLog.Info("MilestoneStore", "No milestones file found — starting fresh");
                return true;
            }

            try
            {
                ConfigNode rootNode = ConfigNode.Load(path);
                if (rootNode == null)
                {
                    ParsekLog.Warn("MilestoneStore", "Failed to parse milestones file");
                    return false;
                }

                ConfigNode[] milestoneNodes = rootNode.GetNodes("MILESTONE");
                if (milestoneNodes != null)
                {
                    for (int i = 0; i < milestoneNodes.Length; i++)
                        milestones.Add(Milestone.DeserializeFrom(milestoneNodes[i]));
                }

                ParsekLog.Info("MilestoneStore", $"Loaded {milestones.Count} milestones from {path}");
                for (int i = 0; i < milestones.Count; i++)
                {
                    ParsekLog.Verbose("MilestoneStore",
                        $"  milestone[{i}]: id={ShortId(milestones[i].MilestoneId)}, " +
                        $"epoch={milestones[i].Epoch}, events={milestones[i].Events.Count}, " +
                        $"committed={milestones[i].Committed}, lastReplayedIdx={milestones[i].LastReplayedEventIndex}");
                }
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("MilestoneStore", $"Failed to load milestones: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Builds a milestone-id-to-lastReplayedIndex map from MILESTONE_STATE ConfigNodes.
        /// Pure function — operates only on the provided nodes.
        /// </summary>
        internal static Dictionary<string, int> BuildStateMap(ConfigNode[] stateNodes)
        {
            var stateMap = new Dictionary<string, int>();
            if (stateNodes == null) return stateMap;

            for (int i = 0; i < stateNodes.Length; i++)
            {
                string id = stateNodes[i].GetValue("id");
                string idxStr = stateNodes[i].GetValue("lastReplayedIdx");
                if (id != null && idxStr != null)
                {
                    int idx;
                    if (int.TryParse(idxStr, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out idx))
                    {
                        stateMap[id] = idx;
                    }
                }
            }

            ParsekLog.Verbose("MilestoneStore",
                $"BuildStateMap: parsed {stateMap.Count} entries from {stateNodes.Length} MILESTONE_STATE nodes");
            return stateMap;
        }

        /// <summary>
        /// Restores mutable milestone state (LastReplayedEventIndex) from the .sfs
        /// scenario node. When resetUnmatched is true (revert path), milestones not
        /// found in the saved state are reset to -1 (unreplayed) — these are milestones
        /// created after the launch quicksave that need to replay from scratch.
        /// </summary>
        internal static void RestoreMutableState(ConfigNode scenarioNode, bool resetUnmatched = false)
        {
            if (scenarioNode == null)
            {
                ParsekLog.Verbose("MilestoneStore", "RestoreMutableState: scenarioNode is null — no-op");
                return;
            }

            ConfigNode[] stateNodes = scenarioNode.GetNodes("MILESTONE_STATE");
            var stateMap = BuildStateMap(stateNodes);

            ParsekLog.Verbose("MilestoneStore",
                $"RestoreMutableState: {stateMap.Count} saved states, {milestones.Count} milestones, resetUnmatched={resetUnmatched}");

            int matched = 0, reset = 0, unchanged = 0;
            for (int i = 0; i < milestones.Count; i++)
            {
                int idx;
                if (stateMap.TryGetValue(milestones[i].MilestoneId, out idx))
                {
                    int oldIdx = milestones[i].LastReplayedEventIndex;
                    milestones[i].LastReplayedEventIndex = idx;
                    matched++;
                    if (oldIdx != idx)
                    {
                        ParsekLog.Verbose("MilestoneStore",
                            $"  milestone {ShortId(milestones[i].MilestoneId)}: lastReplayedIdx {oldIdx} → {idx}");
                    }
                }
                else if (resetUnmatched)
                {
                    int oldIdx = milestones[i].LastReplayedEventIndex;
                    milestones[i].LastReplayedEventIndex = -1;
                    reset++;
                    ParsekLog.Verbose("MilestoneStore",
                        $"  milestone {ShortId(milestones[i].MilestoneId)}: unmatched, reset lastReplayedIdx {oldIdx} → -1");
                }
                else
                {
                    unchanged++;
                }
            }

            ParsekLog.Info("MilestoneStore",
                $"RestoreMutableState complete: matched={matched}, reset={reset}, unchanged={unchanged}");
        }

        internal static void SaveMutableState(ConfigNode scenarioNode)
        {
            if (scenarioNode == null) return;

            scenarioNode.RemoveNodes("MILESTONE_STATE");

            var ic = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 0; i < milestones.Count; i++)
            {
                ConfigNode stateNode = scenarioNode.AddNode("MILESTONE_STATE");
                stateNode.AddValue("id", milestones[i].MilestoneId ?? "");
                stateNode.AddValue("lastReplayedIdx",
                    milestones[i].LastReplayedEventIndex.ToString(ic));
            }

            ParsekLog.Verbose("MilestoneStore", $"SaveMutableState: wrote {milestones.Count} MILESTONE_STATE nodes");
        }

        private static void SafeWriteConfigNode(ConfigNode node, string path)
        {
            FileIOUtils.SafeWriteConfigNode(node, path, "MilestoneStore");
        }

        #endregion

        #region Removal

        internal static void ClearAll()
        {
            int count = milestones.Count;
            milestones.Clear();
            ParsekLog.Info("MilestoneStore", $"All milestones cleared (was {count})");
        }

        #endregion

        #region Testing Support

        internal static void ResetForTesting()
        {
            milestones.Clear();
            initialLoadDone = false;
            lastSaveFolder = null;
            CurrentEpoch = 0;
        }

        internal static void AddMilestoneForTesting(Milestone m)
        {
            milestones.Add(m);
        }

        #endregion

        /// <summary>
        /// Counts non-resource events across all committed milestones in the current epoch.
        /// Used for the Actions button badge in the main window.
        /// </summary>
        internal static int GetPendingEventCount()
        {
            int count = 0;
            uint epoch = CurrentEpoch;
            for (int i = 0; i < milestones.Count; i++)
            {
                var m = milestones[i];
                if (!m.Committed || m.Epoch != epoch) continue;
                for (int j = 0; j < m.Events.Count; j++)
                {
                    if (!GameStateStore.IsMilestoneFilteredEvent(m.Events[j].eventType))
                        count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Removes a specific event from its milestone. Adjusts LastReplayedEventIndex
        /// if the removed event was at or before it. Also removes from GameStateStore.
        /// </summary>
        internal static bool RemoveCommittedEvent(GameStateEvent target)
        {
            uint epoch = CurrentEpoch;
            for (int i = 0; i < milestones.Count; i++)
            {
                var m = milestones[i];
                if (!m.Committed || m.Epoch != epoch) continue;
                for (int j = 0; j < m.Events.Count; j++)
                {
                    var e = m.Events[j];
                    if (e.ut == target.ut && e.eventType == target.eventType &&
                        e.key == target.key)
                    {
                        m.Events.RemoveAt(j);
                        if (j <= m.LastReplayedEventIndex)
                            m.LastReplayedEventIndex--;
                        ParsekLog.Info("MilestoneStore",
                            $"Removed event from milestone {ShortId(m.MilestoneId)}: {target.eventType} key='{target.key}' ut={target.ut:F1}");

                        // Also remove from the event store
                        GameStateStore.RemoveEvent(target);
                        return true;
                    }
                }
            }
            ParsekLog.Verbose("MilestoneStore",
                $"RemoveCommittedEvent: no match for {target.eventType} key='{target.key}' ut={target.ut:F1} in epoch={CurrentEpoch}");
            return false;
        }

        #region Committed Action Queries

        /// <summary>
        /// Returns tech IDs that are committed but not yet replayed.
        /// Used by TechResearchPatch to block duplicate research.
        /// </summary>
        internal static HashSet<string> GetCommittedTechIds()
        {
            var result = new HashSet<string>();
            for (int i = 0; i < milestones.Count; i++)
            {
                var m = milestones[i];
                if (!m.Committed) continue;
                for (int j = m.LastReplayedEventIndex + 1; j < m.Events.Count; j++)
                {
                    if (m.Events[j].eventType == GameStateEventType.TechResearched
                        && !string.IsNullOrEmpty(m.Events[j].key))
                        result.Add(m.Events[j].key);
                }
            }

            if (result.Count > 0)
                ParsekLog.Verbose("MilestoneStore", $"GetCommittedTechIds: {result.Count} committed tech(s): [{string.Join(", ", result)}]");

            return result;
        }

        /// <summary>
        /// Returns facility IDs that have committed-but-unreplayed upgrade events.
        /// Used by FacilityUpgradePatch to block duplicate upgrades.
        /// </summary>
        internal static HashSet<string> GetCommittedFacilityUpgrades()
        {
            var result = new HashSet<string>();
            for (int i = 0; i < milestones.Count; i++)
            {
                var m = milestones[i];
                if (!m.Committed) continue;
                for (int j = m.LastReplayedEventIndex + 1; j < m.Events.Count; j++)
                {
                    if (m.Events[j].eventType == GameStateEventType.FacilityUpgraded
                        && !string.IsNullOrEmpty(m.Events[j].key))
                        result.Add(m.Events[j].key);
                }
            }

            if (result.Count > 0)
                ParsekLog.Verbose("MilestoneStore", $"GetCommittedFacilityUpgrades: {result.Count} committed facility(ies): [{string.Join(", ", result)}]");

            return result;
        }

        /// <summary>
        /// Finds the first unreplayed committed event matching the given type and key.
        /// Returns null if not found. Used for blocking dialog messages.
        /// </summary>
        internal static GameStateEvent? FindCommittedEvent(GameStateEventType type, string key)
        {
            for (int i = 0; i < milestones.Count; i++)
            {
                var m = milestones[i];
                if (!m.Committed) continue;
                for (int j = m.LastReplayedEventIndex + 1; j < m.Events.Count; j++)
                {
                    if (m.Events[j].eventType == type && m.Events[j].key == key)
                    {
                        ParsekLog.Verbose("MilestoneStore",
                            $"FindCommittedEvent: found {type} key='{key}' in milestone {ShortId(m.MilestoneId)} event[{j}] ut={m.Events[j].ut:F0}");
                        return m.Events[j];
                    }
                }
            }

            ParsekLog.Verbose("MilestoneStore", $"FindCommittedEvent: no match for {type} key='{key}'");
            return null;
        }

        #endregion
    }
}
