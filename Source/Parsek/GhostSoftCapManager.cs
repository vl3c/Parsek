using System.Collections.Generic;

namespace Parsek
{
    public enum GhostCapAction
    {
        None = 0,                // No action needed
        ReduceFidelity = 1,      // Reduce part rendering (fewer mesh parts)
        SimplifyToOrbitLine = 2, // Replace mesh with orbit line only
        Despawn = 3              // Remove ghost entirely
    }

    public enum GhostPriority
    {
        LoopedOldest = 0,     // Lowest priority — oldest looped ghosts despawned first
        LoopedRecent = 1,     // Recent looped ghosts
        BackgroundDebris = 2, // Background/debris recordings
        FullTimeline = 3      // Highest priority — full-timeline recordings kept longest
    }

    internal static class GhostSoftCapManager
    {
        // Default thresholds (configurable via settings)
        internal static int Zone1ReduceThreshold = 8;
        internal static int Zone1DespawnThreshold = 15;
        internal static int Zone2SimplifyThreshold = 20;

        /// <summary>
        /// Determines priority for a ghost recording. Lower priority = despawned first.
        /// Pure static for testability.
        /// </summary>
        internal static GhostPriority ClassifyPriority(Recording rec, int loopCycleIndex)
        {
            if (rec.LoopPlayback)
            {
                var priority = loopCycleIndex > 5
                    ? GhostPriority.LoopedOldest
                    : GhostPriority.LoopedRecent;
                ParsekLog.Verbose("SoftCap",
                    $"ClassifyPriority: looped recording '{rec.VesselName}' cycleIndex={loopCycleIndex} → {priority}");
                return priority;
            }
            if (rec.IsDebris)
            {
                ParsekLog.Verbose("SoftCap",
                    $"ClassifyPriority: debris recording '{rec.VesselName}' → BackgroundDebris");
                return GhostPriority.BackgroundDebris;
            }
            ParsekLog.Verbose("SoftCap",
                $"ClassifyPriority: recording '{rec.VesselName}' → FullTimeline");
            return GhostPriority.FullTimeline;
        }

        /// <summary>
        /// Evaluates soft cap status for a set of ghosts.
        /// Returns an action per ghost (indexed by recording index).
        /// Pure static for testability.
        /// </summary>
        /// <param name="zone1Count">Number of ghosts currently in Zone 1</param>
        /// <param name="zone2Count">Number of ghosts currently in Zone 2</param>
        /// <param name="zone1Ghosts">Ghost info list for Zone 1 (recording index, priority)</param>
        /// <param name="zone2Ghosts">Ghost info list for Zone 2 (recording index, priority)</param>
        internal static Dictionary<int, GhostCapAction> EvaluateCaps(
            int zone1Count, int zone2Count,
            List<(int recordingIndex, GhostPriority priority)> zone1Ghosts,
            List<(int recordingIndex, GhostPriority priority)> zone2Ghosts)
        {
            var actions = new Dictionary<int, GhostCapAction>();

            // Zone 2 simplification
            if (zone2Count > Zone2SimplifyThreshold)
            {
                ParsekLog.Info("SoftCap",
                    $"Zone 2 simplify threshold exceeded: count={zone2Count} threshold={Zone2SimplifyThreshold}");
                for (int i = 0; i < zone2Ghosts.Count; i++)
                {
                    actions[zone2Ghosts[i].recordingIndex] = GhostCapAction.SimplifyToOrbitLine;
                    ParsekLog.Verbose("SoftCap",
                        $"Zone 2 ghost idx={zone2Ghosts[i].recordingIndex} priority={zone2Ghosts[i].priority} → SimplifyToOrbitLine");
                }
            }

            // Zone 1 fidelity reduction (between reduce and despawn thresholds)
            if (zone1Count > Zone1ReduceThreshold && zone1Count <= Zone1DespawnThreshold)
            {
                ParsekLog.Info("SoftCap",
                    $"Zone 1 reduce threshold exceeded: count={zone1Count} threshold={Zone1ReduceThreshold}");
                var sorted = new List<(int idx, GhostPriority pri)>(zone1Ghosts);
                sorted.Sort((a, b) => a.pri.CompareTo(b.pri));
                int toReduce = zone1Count - Zone1ReduceThreshold;
                for (int i = 0; i < toReduce && i < sorted.Count; i++)
                {
                    actions[sorted[i].idx] = GhostCapAction.ReduceFidelity;
                    ParsekLog.Verbose("SoftCap",
                        $"Zone 1 ghost idx={sorted[i].idx} priority={sorted[i].pri} → ReduceFidelity");
                }
            }

            // Zone 1 despawning (above despawn threshold)
            if (zone1Count > Zone1DespawnThreshold)
            {
                ParsekLog.Info("SoftCap",
                    $"Zone 1 despawn threshold exceeded: count={zone1Count} threshold={Zone1DespawnThreshold}");
                var sorted = new List<(int idx, GhostPriority pri)>(zone1Ghosts);
                sorted.Sort((a, b) => a.pri.CompareTo(b.pri));
                int toDespawn = zone1Count - Zone1DespawnThreshold;
                for (int i = 0; i < toDespawn && i < sorted.Count; i++)
                {
                    actions[sorted[i].idx] = GhostCapAction.Despawn;
                    ParsekLog.Verbose("SoftCap",
                        $"Zone 1 ghost idx={sorted[i].idx} priority={sorted[i].pri} → Despawn");
                }
                // Also reduce fidelity for remaining low-priority ghosts
                for (int i = toDespawn; i < sorted.Count && i < zone1Count - Zone1ReduceThreshold; i++)
                {
                    if (!actions.ContainsKey(sorted[i].idx))
                    {
                        actions[sorted[i].idx] = GhostCapAction.ReduceFidelity;
                        ParsekLog.Verbose("SoftCap",
                            $"Zone 1 ghost idx={sorted[i].idx} priority={sorted[i].pri} → ReduceFidelity (overflow)");
                    }
                }
            }

            return actions;
        }

        /// <summary>
        /// Applies threshold settings from ParsekSettings to static fields.
        /// Called at scene load to sync persisted settings with runtime thresholds.
        /// Pure static for testability.
        /// </summary>
        internal static void ApplySettings(int zone1Reduce, int zone1Despawn, int zone2Simplify)
        {
            Zone1ReduceThreshold = zone1Reduce;
            Zone1DespawnThreshold = zone1Despawn;
            Zone2SimplifyThreshold = zone2Simplify;
            ParsekLog.Info("SoftCap",
                $"Thresholds applied: zone1Reduce={zone1Reduce} zone1Despawn={zone1Despawn} zone2Simplify={zone2Simplify}");
        }

        /// <summary>
        /// Resets thresholds to defaults. For testing.
        /// </summary>
        internal static void ResetThresholds()
        {
            Zone1ReduceThreshold = 8;
            Zone1DespawnThreshold = 15;
            Zone2SimplifyThreshold = 20;
        }
    }
}
