using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

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
        private const string LegacyPrefix = "[Parsek] ";

        private const double ResourceCoalesceEpsilon = 0.1; // seconds

        internal static IReadOnlyList<GameStateEvent> Events => events;
        internal static IReadOnlyList<ContractSnapshot> ContractSnapshots => contractSnapshots;
        internal static IReadOnlyList<GameStateBaseline> Baselines => baselines;
        internal static int EventCount => events.Count;
        internal static int BaselineCount => baselines.Count;

        #region Event Management

        internal static void AddEvent(GameStateEvent e)
        {
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
        }

        private static bool IsResourceEvent(GameStateEventType type)
        {
            return type == GameStateEventType.FundsChanged ||
                   type == GameStateEventType.ScienceChanged ||
                   type == GameStateEventType.ReputationChanged;
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
                    return;
                }
            }

            contractSnapshots.Add(new ContractSnapshot
            {
                contractGuid = guid,
                contractNode = contractNode
            });
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
            events.Clear();
            contractSnapshots.Clear();
        }

        #endregion

        #region Baseline Management

        internal static void AddBaseline(GameStateBaseline baseline)
        {
            if (baseline == null) return;
            baselines.Add(baseline);
            Log($"[Parsek] Game state baseline captured at UT {baseline.ut:F0}");
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
                var baseline = GameStateBaseline.CaptureCurrentState();
                AddBaseline(baseline);
            }
            catch (Exception ex)
            {
                Log($"[Parsek] WARNING: Failed to capture baseline: {ex.Message}");
            }
        }

        internal static void ClearBaselines()
        {
            baselines.Clear();
        }

        #endregion

        #region File I/O

        internal static bool SaveEventFile()
        {
            string path = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildGameStateEventsRelativePath());
            if (path == null)
            {
                Log("[Parsek] WARNING: Cannot resolve game state events path");
                return false;
            }

            try
            {
                string dir = RecordingPaths.EnsureGameStateDirectory();
                if (string.IsNullOrEmpty(dir))
                {
                    Log("[Parsek] WARNING: EnsureGameStateDirectory returned null during SaveEventFile");
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

                Log($"[Parsek] Saved {events.Count} game state events, " +
                    $"{contractSnapshots.Count} contract snapshots to {path}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"[Parsek] WARNING: Failed to save game state events: {ex.Message}");
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
            }

            if (initialLoadDone)
                return true;

            initialLoadDone = true;
            events.Clear();
            contractSnapshots.Clear();

            string path = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildGameStateEventsRelativePath());
            if (path == null || !File.Exists(path))
            {
                Log("[Parsek] No game state events file found — starting fresh");
                return true;
            }

            try
            {
                ConfigNode rootNode = ConfigNode.Load(path);
                if (rootNode == null)
                {
                    Log("[Parsek] WARNING: Failed to parse game state events file");
                    return false;
                }

                int version = 1;
                string versionStr = rootNode.GetValue("version");
                if (!string.IsNullOrEmpty(versionStr) && !int.TryParse(versionStr, out version))
                {
                    Log($"[Parsek] WARNING: Invalid game state events version '{versionStr}'");
                    version = 1;
                }
                if (version != 1)
                {
                    Log($"[Parsek] WARNING: Unsupported game state events version={version} (expected 1)");
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

                Log($"[Parsek] Loaded {events.Count} game state events, " +
                    $"{contractSnapshots.Count} contract snapshots");
                return true;
            }
            catch (Exception ex)
            {
                Log($"[Parsek] WARNING: Failed to load game state events: {ex.Message}");
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
                Log("[Parsek] WARNING: Cannot resolve baseline path");
                return false;
            }

            try
            {
                string dir = RecordingPaths.EnsureGameStateDirectory();
                if (string.IsNullOrEmpty(dir))
                {
                    Log("[Parsek] WARNING: EnsureGameStateDirectory returned null during SaveBaseline");
                    return false;
                }

                var rootNode = new ConfigNode("PARSEK_BASELINE");
                baseline.SerializeInto(rootNode);

                SafeWriteConfigNode(rootNode, path);

                Log($"[Parsek] Saved baseline at UT {baseline.ut:F0} to {path}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"[Parsek] WARNING: Failed to save baseline: {ex.Message}");
                return false;
            }
        }

        internal static bool LoadBaselines()
        {
            baselines.Clear();

            string dir = RecordingPaths.ResolveGameStateDirectory();
            if (dir == null || !Directory.Exists(dir))
                return true;

            try
            {
                string[] files = Directory.GetFiles(dir, "baseline_*.pgsb");
                foreach (string file in files)
                {
                    ConfigNode rootNode = ConfigNode.Load(file);
                    if (rootNode != null)
                    {
                        var baseline = GameStateBaseline.DeserializeFrom(rootNode);
                        baselines.Add(baseline);
                    }
                }

                // Sort baselines by UT
                baselines.Sort((a, b) => a.ut.CompareTo(b.ut));

                Log($"[Parsek] Loaded {baselines.Count} baselines");
                return true;
            }
            catch (Exception ex)
            {
                Log($"[Parsek] WARNING: Failed to load baselines: {ex.Message}");
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
                    Log($"[Parsek] WARNING: Failed to delete existing file '{path}': {ex.Message}");
                    throw;
                }
            }

            try
            {
                File.Move(tmpPath, path);
            }
            catch (Exception ex)
            {
                Log($"[Parsek] WARNING: Failed to move temp file '{tmpPath}' to '{path}': {ex.Message}");
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

        private static void Log(string msg)
        {
            if (SuppressLogging) return;

            string clean = msg ?? "(empty)";
            if (clean.StartsWith(LegacyPrefix, StringComparison.Ordinal))
                clean = clean.Substring(LegacyPrefix.Length);

            if (clean.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase) ||
                clean.StartsWith("WARN:", StringComparison.OrdinalIgnoreCase))
            {
                int idx = clean.IndexOf(':');
                string trimmed = idx >= 0 ? clean.Substring(idx + 1).TrimStart() : clean;
                ParsekLog.Warn("GameStateStore", trimmed);
                return;
            }

            ParsekLog.Info("GameStateStore", clean);
        }
    }
}
