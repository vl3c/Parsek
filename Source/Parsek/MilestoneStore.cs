using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Parsek
{
    internal static class MilestoneStore
    {
        private static List<Milestone> milestones = new List<Milestone>();
        private static bool initialLoadDone = false;
        private static string lastSaveFolder = null;

        internal static bool SuppressLogging = false;

        internal static IReadOnlyList<Milestone> Milestones => milestones;
        internal static int MilestoneCount => milestones.Count;

        internal static uint CurrentEpoch { get; set; }

        internal static Milestone CreateMilestone(string recordingId, double currentUT)
        {
            double startUT = 0;
            for (int i = 0; i < milestones.Count; i++)
            {
                if (milestones[i].EndUT > startUT)
                    startUT = milestones[i].EndUT;
            }

            var events = GameStateStore.Events;
            uint epoch = CurrentEpoch;

            var filtered = new List<GameStateEvent>();
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (e.epoch != epoch) continue;
                if (e.ut <= startUT || e.ut > currentUT) continue;
                if (GameStateStore.IsResourceEvent(e.eventType)) continue;
                filtered.Add(e);
            }

            if (filtered.Count == 0)
            {
                Log($"[Parsek] No game state events for milestone (epoch={epoch}, UT {startUT:F0}-{currentUT:F0}) — skipped");
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
            Log($"[Parsek] Milestone created: {filtered.Count} events, UT {startUT:F0}-{currentUT:F0}, epoch={epoch}");
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
            return CreateMilestone(null, currentUT);
        }

        #region File I/O

        internal static bool SaveMilestoneFile()
        {
            string path = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildMilestonesRelativePath());
            if (path == null)
            {
                Log("[Parsek] WARNING: Cannot resolve milestones path");
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

                Log($"[Parsek] Saved {milestones.Count} milestones to {path}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"[Parsek] WARNING: Failed to save milestones: {ex.Message}");
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
            }

            if (initialLoadDone)
                return true;

            initialLoadDone = true;
            milestones.Clear();

            string path = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildMilestonesRelativePath());
            if (path == null || !File.Exists(path))
            {
                Log("[Parsek] No milestones file found — starting fresh");
                return true;
            }

            try
            {
                ConfigNode rootNode = ConfigNode.Load(path);
                if (rootNode == null)
                {
                    Log("[Parsek] WARNING: Failed to parse milestones file");
                    return false;
                }

                ConfigNode[] milestoneNodes = rootNode.GetNodes("MILESTONE");
                if (milestoneNodes != null)
                {
                    for (int i = 0; i < milestoneNodes.Length; i++)
                        milestones.Add(Milestone.DeserializeFrom(milestoneNodes[i]));
                }

                Log($"[Parsek] Loaded {milestones.Count} milestones");
                return true;
            }
            catch (Exception ex)
            {
                Log($"[Parsek] WARNING: Failed to load milestones: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Restores mutable milestone state (LastReplayedEventIndex) from the .sfs
        /// scenario node. When resetUnmatched is true (revert path), milestones not
        /// found in the saved state are reset to -1 (unreplayed) — these are milestones
        /// created after the launch quicksave that need to replay from scratch.
        /// </summary>
        internal static void RestoreMutableState(ConfigNode scenarioNode, bool resetUnmatched = false)
        {
            if (scenarioNode == null) return;

            ConfigNode[] stateNodes = scenarioNode.GetNodes("MILESTONE_STATE");

            var stateMap = new Dictionary<string, int>();
            if (stateNodes != null)
            {
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
            }

            for (int i = 0; i < milestones.Count; i++)
            {
                int idx;
                if (stateMap.TryGetValue(milestones[i].MilestoneId, out idx))
                    milestones[i].LastReplayedEventIndex = idx;
                else if (resetUnmatched)
                    milestones[i].LastReplayedEventIndex = -1;
            }
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
        }

        private static void SafeWriteConfigNode(ConfigNode node, string path)
        {
            string tmpPath = path + ".tmp";
            node.Save(tmpPath);
            if (File.Exists(path))
                File.Delete(path);
            File.Move(tmpPath, path);
        }

        #endregion

        #region Removal

        internal static void ClearAll()
        {
            milestones.Clear();
            Log("[Parsek] All milestones cleared");
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

        private static void Log(string msg)
        {
            if (!SuppressLogging)
                Debug.Log(msg);
        }
    }
}
