using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Parsek
{
    internal static class GameStateStore
    {
        private static List<GameStateEvent> events = new List<GameStateEvent>();
        private static List<ContractSnapshot> contractSnapshots = new List<ContractSnapshot>();
        private static List<GameStateBaseline> baselines = new List<GameStateBaseline>();

        private static bool initialLoadDone = false;
        private static string lastSaveFolder = null;

        internal static bool SuppressLogging = false;

        private const double ResourceCoalesceEpsilon = 0.1; // seconds

        internal static IReadOnlyList<GameStateEvent> Events => events;
        internal static IReadOnlyList<ContractSnapshot> ContractSnapshots => contractSnapshots;
        internal static IReadOnlyList<GameStateBaseline> Baselines => baselines;
        internal static int EventCount => events.Count;
        internal static int BaselineCount => baselines.Count;

        #region Event Management

        internal static void AddEvent(GameStateEvent e)
        {
            // Stamp current epoch for branch isolation
            e.epoch = MilestoneStore.CurrentEpoch;

            // Resource coalescing: if this is a resource event and the last event
            // of the same type is within the epsilon window, update it instead
            if (IsResourceEvent(e.eventType) && events.Count > 0)
            {
                for (int i = events.Count - 1; i >= 0; i--)
                {
                    var existing = events[i];
                    if (existing.eventType == e.eventType &&
                        Math.Abs(existing.ut - e.ut) <= ResourceCoalesceEpsilon)
                    {
                        // Update the existing event's valueAfter
                        existing.valueAfter = e.valueAfter;
                        events[i] = existing;
                        ParsekLog.VerboseRateLimited("GameStateStore", "resource-coalesce",
                            $"Coalesced {e.eventType} event at ut={e.ut:F2}");
                        return;
                    }
                    // Stop searching once we pass the epsilon window
                    if (e.ut - existing.ut > ResourceCoalesceEpsilon)
                        break;
                }
            }

            events.Add(e);
            ParsekLog.Verbose("GameStateStore",
                $"AddEvent: {e.eventType} key='{e.key}' epoch={e.epoch} ut={e.ut:F1} (total={events.Count})");
        }

        internal static bool IsResourceEvent(GameStateEventType type)
        {
            return type == GameStateEventType.FundsChanged ||
                   type == GameStateEventType.ScienceChanged ||
                   type == GameStateEventType.ReputationChanged;
        }

        /// <summary>
        /// Events that should be excluded from milestones and the Actions window.
        /// Resource events are summarized by the budget; CrewStatusChanged is KSP
        /// internal bookkeeping (Available↔Assigned) and not a player action.
        /// </summary>
        internal static bool IsMilestoneFilteredEvent(GameStateEventType type)
        {
            return IsResourceEvent(type) ||
                   type == GameStateEventType.CrewStatusChanged;
        }

        internal static void AddContractSnapshot(string guid, ConfigNode contractNode)
        {
            if (string.IsNullOrEmpty(guid) || contractNode == null)
            {
                ParsekLog.Verbose("GameStateStore", $"AddContractSnapshot skipped: guid={guid ?? "null"}, node={contractNode != null}");
                return;
            }

            // Replace existing snapshot for same GUID (contract re-accepted after failure)
            for (int i = 0; i < contractSnapshots.Count; i++)
            {
                if (contractSnapshots[i].contractGuid == guid)
                {
                    contractSnapshots[i] = new ContractSnapshot
                    {
                        contractGuid = guid,
                        contractNode = contractNode
                    };
                    ParsekLog.Verbose("GameStateStore", $"Replaced existing contract snapshot for guid={guid}");
                    return;
                }
            }

            contractSnapshots.Add(new ContractSnapshot
            {
                contractGuid = guid,
                contractNode = contractNode
            });
            ParsekLog.Verbose("GameStateStore", $"Added contract snapshot for guid={guid} (total={contractSnapshots.Count})");
        }

        internal static ConfigNode GetContractSnapshot(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;

            for (int i = 0; i < contractSnapshots.Count; i++)
            {
                if (contractSnapshots[i].contractGuid == guid)
                    return contractSnapshots[i].contractNode;
            }
            return null;
        }

        internal static void ClearEvents()
        {
            int eventCount = events.Count;
            int snapCount = contractSnapshots.Count;
            events.Clear();
            contractSnapshots.Clear();
            ParsekLog.Info("GameStateStore", $"Cleared {eventCount} events and {snapCount} contract snapshots");
        }

        #endregion

        #region Baseline Management

        internal static void AddBaseline(GameStateBaseline baseline)
        {
            if (baseline == null) return;
            baselines.Add(baseline);
            ParsekLog.Info("GameStateStore", $"Game state baseline captured at UT {baseline.ut:F0} (total={baselines.Count})");
        }

        /// <summary>
        /// Captures a baseline if none exist or if a new one is warranted.
        /// Called from RecordingStore.CommitPending() as the single funnel point.
        /// Silently skipped in test environments (SuppressLogging = true).
        /// </summary>
        internal static void CaptureBaselineIfNeeded()
        {
            // Skip in test environments where Unity/KSP APIs aren't available
            if (SuppressLogging) return;

            try
            {
                ParsekLog.Verbose("GameStateStore", "CaptureBaselineIfNeeded: capturing baseline...");
                var baseline = GameStateBaseline.CaptureCurrentState();
                AddBaseline(baseline);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("GameStateStore", $"Failed to capture baseline: {ex.Message}");
            }
        }

        internal static void ClearBaselines()
        {
            int count = baselines.Count;
            baselines.Clear();
            ParsekLog.Verbose("GameStateStore", $"Cleared {count} baselines");
        }

        #endregion

        #region File I/O

        internal static bool SaveEventFile()
        {
            string path = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildGameStateEventsRelativePath());
            if (path == null)
            {
                ParsekLog.Warn("GameStateStore", "Cannot resolve game state events path — save skipped");
                return false;
            }

            try
            {
                string dir = RecordingPaths.EnsureGameStateDirectory();
                if (string.IsNullOrEmpty(dir))
                {
                    ParsekLog.Warn("GameStateStore", "EnsureGameStateDirectory returned null during SaveEventFile");
                    return false;
                }

                // Events are stored in insertion (capture) order, not UT order.
                // After reverts, events from an abandoned future branch precede
                // events from the new branch — UT-sorting would interleave them
                // and corrupt facility/building cache seeding.

                var rootNode = new ConfigNode("PARSEK_GAME_STATE");
                rootNode.AddValue("version", 1);

                foreach (var e in events)
                {
                    ConfigNode eventNode = rootNode.AddNode("GAME_STATE_EVENT");
                    e.SerializeInto(eventNode);
                }

                foreach (var snap in contractSnapshots)
                    snap.SerializeInto(rootNode);

                SafeWriteConfigNode(rootNode, path);

                ParsekLog.Info("GameStateStore",
                    $"Saved {events.Count} game state events, {contractSnapshots.Count} contract snapshots to {path}");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("GameStateStore", $"Failed to save game state events: {ex.Message}");
                return false;
            }
        }

        internal static bool LoadEventFile()
        {
            string currentSave = HighLogic.SaveFolder;
            if (currentSave != lastSaveFolder)
            {
                initialLoadDone = false;
                lastSaveFolder = currentSave;
                ParsekLog.Verbose("GameStateStore", $"Save folder changed to '{currentSave}' — resetting event load state");
            }

            if (initialLoadDone)
            {
                ParsekLog.Verbose("GameStateStore", "LoadEventFile: already loaded, skipping");
                return true;
            }

            initialLoadDone = true;
            events.Clear();
            contractSnapshots.Clear();

            string path = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildGameStateEventsRelativePath());
            if (path == null || !File.Exists(path))
            {
                ParsekLog.Info("GameStateStore", "No game state events file found — starting fresh");
                return true;
            }

            try
            {
                ConfigNode rootNode = ConfigNode.Load(path);
                if (rootNode == null)
                {
                    ParsekLog.Warn("GameStateStore", "Failed to parse game state events file");
                    return false;
                }

                int version = 1;
                string versionStr = rootNode.GetValue("version");
                if (!string.IsNullOrEmpty(versionStr) && !int.TryParse(versionStr, out version))
                {
                    ParsekLog.Warn("GameStateStore", $"Invalid game state events version '{versionStr}'");
                    version = 1;
                }
                if (version != 1)
                {
                    ParsekLog.Warn("GameStateStore", $"Unsupported game state events version={version} (expected 1)");
                }

                // ConfigNode.Load returns the file contents directly
                ConfigNode[] eventNodes = rootNode.GetNodes("GAME_STATE_EVENT");
                if (eventNodes != null)
                {
                    foreach (var en in eventNodes)
                        events.Add(GameStateEvent.DeserializeFrom(en));
                }

                ConfigNode[] snapNodes = rootNode.GetNodes("CONTRACT_SNAPSHOT");
                if (snapNodes != null)
                {
                    foreach (var sn in snapNodes)
                        contractSnapshots.Add(ContractSnapshot.DeserializeFrom(sn));
                }

                ParsekLog.Info("GameStateStore",
                    $"Loaded {events.Count} game state events, {contractSnapshots.Count} contract snapshots from {path}");

                // Log event type distribution for diagnostics
                if (events.Count > 0)
                {
                    var typeCounts = new Dictionary<GameStateEventType, int>();
                    for (int i = 0; i < events.Count; i++)
                    {
                        var type = events[i].eventType;
                        if (typeCounts.ContainsKey(type))
                            typeCounts[type]++;
                        else
                            typeCounts[type] = 1;
                    }

                    var parts = new List<string>();
                    foreach (var kvp in typeCounts)
                        parts.Add($"{kvp.Key}={kvp.Value}");
                    ParsekLog.Verbose("GameStateStore", $"Event type distribution: {string.Join(", ", parts)}");
                }

                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("GameStateStore", $"Failed to load game state events: {ex.Message}");
                return false;
            }
        }

        internal static bool SaveBaseline(GameStateBaseline baseline)
        {
            if (baseline == null) return false;

            string relativePath = RecordingPaths.BuildBaselineRelativePath(baseline.ut);
            string path = RecordingPaths.ResolveSaveScopedPath(relativePath);
            if (path == null)
            {
                ParsekLog.Warn("GameStateStore", "Cannot resolve baseline path — save skipped");
                return false;
            }

            try
            {
                string dir = RecordingPaths.EnsureGameStateDirectory();
                if (string.IsNullOrEmpty(dir))
                {
                    ParsekLog.Warn("GameStateStore", "EnsureGameStateDirectory returned null during SaveBaseline");
                    return false;
                }

                var rootNode = new ConfigNode("PARSEK_BASELINE");
                baseline.SerializeInto(rootNode);

                SafeWriteConfigNode(rootNode, path);

                ParsekLog.Info("GameStateStore", $"Saved baseline at UT {baseline.ut:F0} to {path}");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("GameStateStore", $"Failed to save baseline: {ex.Message}");
                return false;
            }
        }

        internal static bool LoadBaselines()
        {
            baselines.Clear();

            string dir = RecordingPaths.ResolveGameStateDirectory();
            if (dir == null || !Directory.Exists(dir))
            {
                ParsekLog.Verbose("GameStateStore", "No game state directory found — no baselines to load");
                return true;
            }

            try
            {
                string[] files = Directory.GetFiles(dir, "baseline_*.pgsb");
                ParsekLog.Verbose("GameStateStore", $"Found {files.Length} baseline files in {dir}");

                foreach (string file in files)
                {
                    ConfigNode rootNode = ConfigNode.Load(file);
                    if (rootNode != null)
                    {
                        var baseline = GameStateBaseline.DeserializeFrom(rootNode);
                        baselines.Add(baseline);
                    }
                    else
                    {
                        ParsekLog.Warn("GameStateStore", $"Failed to parse baseline file '{file}'");
                    }
                }

                // Sort baselines by UT
                baselines.Sort((a, b) => a.ut.CompareTo(b.ut));

                ParsekLog.Info("GameStateStore", $"Loaded {baselines.Count} baselines");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("GameStateStore", $"Failed to load baselines: {ex.Message}");
                return false;
            }
        }

        private static void SafeWriteConfigNode(ConfigNode node, string path)
        {
            string tmpPath = path + ".tmp";
            node.Save(tmpPath);
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("GameStateStore", $"Failed to delete existing file '{path}': {ex.Message}");
                    throw;
                }
            }

            try
            {
                File.Move(tmpPath, path);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("GameStateStore", $"Failed to move temp file '{tmpPath}' to '{path}': {ex.Message}");
                throw;
            }
        }

        #endregion

        #region Testing Support

        internal static void ResetForTesting()
        {
            events.Clear();
            contractSnapshots.Clear();
            baselines.Clear();
            initialLoadDone = false;
            lastSaveFolder = null;
        }

        #endregion
    }
}
