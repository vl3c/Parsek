using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Builds a sorted list of <see cref="TimelineEntry"/> from all data sources.
    /// Read-only query — never modifies the source data.
    /// Three collectors (recording, game action, legacy) produce entries that are
    /// merged and sorted by UT for chronological display.
    /// </summary>
    internal static class TimelineBuilder
    {
        /// <summary>
        /// Constructs the full timeline entry list from committed recordings,
        /// ledger actions, and legacy milestones. Accepts data sources as parameters
        /// for testability — does not read global state directly.
        /// </summary>
        internal static List<TimelineEntry> Build(
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<GameAction> ledgerActions,
            IReadOnlyList<Milestone> milestones,
            uint currentEpoch)
        {
            var entries = new List<TimelineEntry>();

            int recordingCount = CollectRecordingEntries(committedRecordings, entries);
            int actionCount = CollectGameActionEntries(committedRecordings, ledgerActions, entries);
            int legacyCount = CollectLegacyEntries(milestones, currentEpoch, entries);

            var sorted = entries.OrderBy(e => e.UT).ToList();

            ParsekLog.Verbose("Timeline",
                $"Build complete: {sorted.Count} entries ({recordingCount} recording, {actionCount} action, {legacyCount} legacy)");

            return sorted;
        }

        // ---- Recording Collector ----

        private static int CollectRecordingEntries(
            IReadOnlyList<Recording> recordings,
            List<TimelineEntry> entries)
        {
            int count = 0;
            int hiddenSkipped = 0;

            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec.Hidden) { hiddenSkipped++; continue; }
                if (rec.IsDebris) continue;

                // RecordingStart — duration is from this recording, or full chain span
                double duration = rec.EndUT - rec.StartUT;
                if (!string.IsNullOrEmpty(rec.ChainId))
                    duration = GetChainDuration(rec.ChainId, recordings);

                var startType = TimelineEntryType.RecordingStart;
                entries.Add(new TimelineEntry
                {
                    UT = rec.StartUT,
                    Type = startType,
                    DisplayText = TimelineEntryDisplay.GetRecordingStartText(rec.VesselName, duration),
                    Source = TimelineSource.Recording,
                    Tier = TimelineEntryDisplay.GetTier(startType),
                    DisplayColor = Color.white,
                    RecordingId = rec.RecordingId,
                    VesselName = rec.VesselName
                });
                count++;

                // VesselSpawn at EndUT — vessel materializes after ghost playback
                // Only if playback enabled and not a mid-chain segment
                if (rec.PlaybackEnabled && !IsChainMidSegment(rec, recordings))
                {
                    var spawnType = TimelineEntryType.VesselSpawn;
                    entries.Add(new TimelineEntry
                    {
                        UT = rec.EndUT,
                        Type = spawnType,
                        DisplayText = TimelineEntryDisplay.GetVesselSpawnText(rec.VesselName, rec.TerminalStateValue, rec.VesselSituation),
                        Source = TimelineSource.Recording,
                        Tier = TimelineEntryDisplay.GetTier(spawnType),
                        DisplayColor = Color.white,
                        RecordingId = rec.RecordingId,
                        VesselName = rec.VesselName
                    });
                    count++;
                }
            }

            if (hiddenSkipped > 0)
                ParsekLog.Verbose("Timeline",
                    $"Recording collector: {count} entries, {hiddenSkipped} hidden skipped");

            return count;
        }

        /// <summary>
        /// Returns the total duration of a chain (max EndUT - min StartUT across branch 0 members).
        /// Falls back to 0 if no matching members found.
        /// </summary>
        private static double GetChainDuration(string chainId, IReadOnlyList<Recording> recordings)
        {
            double minStart = double.MaxValue;
            double maxEnd = double.MinValue;
            for (int i = 0; i < recordings.Count; i++)
            {
                var r = recordings[i];
                if (r.ChainId != chainId || r.ChainBranch != 0) continue;
                if (r.StartUT < minStart) minStart = r.StartUT;
                if (r.EndUT > maxEnd) maxEnd = r.EndUT;
            }
            return minStart < double.MaxValue ? maxEnd - minStart : 0;
        }

        /// <summary>
        /// Replicates <see cref="RecordingStore.IsChainMidSegment"/> logic locally
        /// using the passed-in recording list instead of the static committed list.
        /// Returns true if the recording is a mid-chain segment (not the last in its chain).
        /// </summary>
        private static bool IsChainMidSegment(Recording rec, IReadOnlyList<Recording> recordings)
        {
            if (string.IsNullOrEmpty(rec.ChainId) || rec.ChainIndex < 0) return false;
            if (rec.ChainBranch > 0) return false;
            for (int i = 0; i < recordings.Count; i++)
            {
                var other = recordings[i];
                if (other.ChainId == rec.ChainId && other.ChainBranch == 0 && other.ChainIndex > rec.ChainIndex)
                    return true;
            }
            return false;
        }

        // ---- Game Action Collector ----

        private static int CollectGameActionEntries(
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<GameAction> ledgerActions,
            List<TimelineEntry> entries)
        {
            // Build recording-id to vessel-name lookup
            var vesselNamesByRecordingId = new Dictionary<string, string>();
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (!string.IsNullOrEmpty(rec.RecordingId))
                    vesselNamesByRecordingId[rec.RecordingId] = rec.VesselName;
            }

            int count = 0;
            for (int i = 0; i < ledgerActions.Count; i++)
            {
                var action = ledgerActions[i];
                var entryType = TimelineEntryDisplay.MapGameActionType(action.Type);
                var tier = TimelineEntryDisplay.GetTier(entryType);

                // Demote T1 to T2 if not effective
                if (!action.Effective && tier == SignificanceTier.T1)
                    tier = SignificanceTier.T2;

                // Resolve vessel name from recording ID
                string vesselName = null;
                if (!string.IsNullOrEmpty(action.RecordingId))
                    vesselNamesByRecordingId.TryGetValue(action.RecordingId, out vesselName);

                entries.Add(new TimelineEntry
                {
                    UT = action.UT,
                    Type = entryType,
                    DisplayText = TimelineEntryDisplay.GetGameActionText(action, vesselName),
                    Source = TimelineSource.GameAction,
                    Tier = tier,
                    DisplayColor = GameActionDisplay.GetColor(action.Type),
                    RecordingId = action.RecordingId,
                    VesselName = vesselName,
                    IsEffective = action.Effective
                });
                count++;
            }

            return count;
        }

        // ---- Legacy Collector ----

        private static int CollectLegacyEntries(
            IReadOnlyList<Milestone> milestones,
            uint currentEpoch,
            List<TimelineEntry> entries)
        {
            int count = 0;

            for (int i = 0; i < milestones.Count; i++)
            {
                var m = milestones[i];
                if (!m.Committed || m.Epoch != currentEpoch) continue;

                for (int j = 0; j < m.Events.Count; j++)
                {
                    var e = m.Events[j];
                    if (GameStateStore.IsMilestoneFilteredEvent(e.eventType)) continue;

                    string category = GameStateEventDisplay.GetDisplayCategory(e.eventType);
                    string description = GameStateEventDisplay.GetDisplayDescription(e);
                    string displayText = $"{category}: {description}";

                    entries.Add(new TimelineEntry
                    {
                        UT = e.ut,
                        Type = TimelineEntryType.LegacyEvent,
                        DisplayText = displayText,
                        Source = TimelineSource.Legacy,
                        Tier = SignificanceTier.T2,
                        DisplayColor = Color.white
                    });
                    count++;
                }
            }

            return count;
        }
    }
}
