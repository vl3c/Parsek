using System.Collections.Generic;
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
            // Build recording-id to vessel-name lookup once, shared by both collectors
            var vesselNameById = new Dictionary<string, string>();
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var r = committedRecordings[i];
                if (!string.IsNullOrEmpty(r.RecordingId))
                    vesselNameById[r.RecordingId] = r.VesselName;
            }

            var entries = new List<TimelineEntry>();

            int recordingCount = CollectRecordingEntries(committedRecordings, vesselNameById, entries);
            int actionCount = CollectGameActionEntries(ledgerActions, vesselNameById, entries);
            int legacyCount = CollectLegacyEntries(milestones, currentEpoch, entries);

            entries.Sort((a, b) => a.UT.CompareTo(b.UT));

            ParsekLog.Verbose("Timeline",
                $"Build complete: {entries.Count} entries ({recordingCount} recording, {actionCount} action, {legacyCount} legacy)");

            return entries;
        }

        // ---- Recording Collector ----

        private static int CollectRecordingEntries(
            IReadOnlyList<Recording> recordings,
            Dictionary<string, string> vesselNameById,
            List<TimelineEntry> entries)
        {
            int count = 0;
            int hiddenSkipped = 0;

            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec.Hidden) { hiddenSkipped++; continue; }
                if (rec.IsDebris) continue;

                // EVA detection: EvaCrewName set, or vessel name matches a crew end state
                // (standalone EVA recordings may not have EvaCrewName set)
                bool isEva = !string.IsNullOrEmpty(rec.EvaCrewName) ||
                    (rec.CrewEndStates != null && rec.CrewEndStates.Count == 1 &&
                     rec.CrewEndStates.ContainsKey(rec.VesselName));
                string parentVesselName = null;
                if (isEva && !string.IsNullOrEmpty(rec.ParentRecordingId))
                    vesselNameById.TryGetValue(rec.ParentRecordingId, out parentVesselName);

                // RecordingStart — only for true launches and EVAs.
                // Skip: optimizer-split segments (ChainIndex > 0) and tree branch children
                // (ParentBranchPointId set — created by staging/decouple/breakup, not player launch).
                // EVA recordings with a parent branch point are still shown (as "EVA:" entries).
                bool isTreeChild = !string.IsNullOrEmpty(rec.ParentBranchPointId) && !isEva;
                bool isChainChild = rec.ChainIndex > 0;
                if (!isChainChild && !isTreeChild)
                {
                    double duration = rec.EndUT - rec.StartUT;
                    if (!string.IsNullOrEmpty(rec.ChainId))
                        duration = GetChainDuration(rec.ChainId, recordings);

                    var startType = TimelineEntryType.RecordingStart;
                    entries.Add(new TimelineEntry
                    {
                        UT = rec.StartUT,
                        Type = startType,
                        DisplayText = TimelineEntryDisplay.GetRecordingStartText(rec.VesselName, duration, isEva, parentVesselName, rec.StartBodyName, rec.StartBiome, rec.LaunchSiteName),
                        Source = TimelineSource.Recording,
                        Tier = TimelineEntryDisplay.GetTier(startType),
                        DisplayColor = Color.white,
                        RecordingId = rec.RecordingId,
                        VesselName = rec.VesselName
                    });
                    count++;
                }

                // VesselSpawn at EndUT — vessel materializes after ghost playback.
                // Skip: disabled playback, mid-chain segments, destroyed terminals (can't spawn
                // a destroyed vessel), and tree children that aren't the effective vessel leaf.
                bool isDestroyedTerminal = rec.TerminalStateValue == TerminalState.Destroyed;
                if (rec.PlaybackEnabled && !IsChainMidSegment(rec, recordings)
                    && !isDestroyedTerminal && !isTreeChild)
                {
                    var spawnType = TimelineEntryType.VesselSpawn;
                    entries.Add(new TimelineEntry
                    {
                        UT = rec.EndUT,
                        Type = spawnType,
                        DisplayText = TimelineEntryDisplay.GetVesselSpawnText(rec.VesselName, rec.TerminalStateValue, rec.VesselSituation, isEva, parentVesselName, rec.TerminalOrbitBody, rec.SegmentBodyName, rec.EndBiome),
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
            IReadOnlyList<GameAction> ledgerActions,
            Dictionary<string, string> vesselNameById,
            List<TimelineEntry> entries)
        {
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
                    vesselNameById.TryGetValue(action.RecordingId, out vesselName);

                // Skip EVA self-assignment: kerbal assigned to their own EVA vessel
                if (action.Type == GameActionType.KerbalAssignment &&
                    !string.IsNullOrEmpty(action.KerbalName) &&
                    !string.IsNullOrEmpty(vesselName) &&
                    vesselName == action.KerbalName)
                    continue;

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
                    IsEffective = action.Effective,
                    IsPlayerAction = TimelineEntryDisplay.IsPlayerAction(entryType)
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
