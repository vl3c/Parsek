using System;
using System.Collections.Generic;
using System.Globalization;
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
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private const float MilestoneCompactionEpsilon = 0.0001f;
        private const double MilestoneCompactionUtToleranceSeconds = 0.1;

        /// <summary>
        /// Constructs the full timeline entry list from committed recordings,
        /// ledger actions, and legacy milestones. Accepts data sources as parameters
        /// for testability — does not read global state directly.
        /// </summary>
        internal static List<TimelineEntry> Build(
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<GameAction> ledgerActions,
            IReadOnlyList<Milestone> milestones,
            Func<GameStateEvent, bool> isLegacyEventVisible,
            Game.Modes? currentMode = null)
        {
            // Build recording-id to vessel-name lookup once, shared by both collectors
            var vesselNameById = new Dictionary<string, string>();
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var r = committedRecordings[i];
                if (!string.IsNullOrEmpty(r.RecordingId))
                    vesselNameById[r.RecordingId] = r.VesselName;
            }

            // Bug #228: build set of (parentRecordingId, startUT) pairs from EVA recordings
            // so we can filter out crew reassignment noise at EVA branch times.
            var evaBranchKeys = BuildEvaBranchKeys(committedRecordings);
            var legacyDuplicateKeys = BuildLegacyDuplicateKeys(ledgerActions);

            var entries = new List<TimelineEntry>();

            int recordingCount = CollectRecordingEntries(committedRecordings, vesselNameById, entries);
            int actionCount = CollectGameActionEntries(ledgerActions, vesselNameById, evaBranchKeys, currentMode, entries);
            int legacyCount = CollectLegacyEntries(
                milestones,
                isLegacyEventVisible ?? (_ => true),
                legacyDuplicateKeys,
                entries);

            entries.Sort((a, b) => a.UT.CompareTo(b.UT));
            int compactedMilestoneRows = CompactAdjacentMilestoneEntries(entries);

            ParsekLog.Verbose("Timeline",
                compactedMilestoneRows > 0
                    ? $"Build complete: {entries.Count} entries after compacting {compactedMilestoneRows} milestone row(s) " +
                      $"({recordingCount} recording, {actionCount} action, {legacyCount} legacy before compaction)"
                    : $"Build complete: {entries.Count} entries ({recordingCount} recording, {actionCount} action, {legacyCount} legacy)");

            return entries;
        }

        internal static int CompactAdjacentMilestoneEntries(List<TimelineEntry> entries)
        {
            if (entries == null || entries.Count < 2)
                return 0;

            int compactedRows = 0;

            for (int i = 0; i < entries.Count - 1; i++)
            {
                TimelineEntry anchor = entries[i];
                if (!CanCompactMilestoneEntry(anchor))
                    continue;

                float mergedFunds = anchor.MilestoneFundsAwarded;
                float mergedRep = anchor.MilestoneRepAwarded;
                float mergedScience = anchor.MilestoneScienceAwarded;
                bool mergedEffective = anchor.IsEffective;
                SignificanceTier mergedTier = anchor.Tier;
                string mergedRecordingId = anchor.RecordingId;
                string mergedVesselName = anchor.VesselName;
                var removalIndices = new List<int>();

                for (int j = i + 1; j < entries.Count; j++)
                {
                    TimelineEntry candidate = entries[j];
                    if (candidate.UT - anchor.UT > MilestoneCompactionUtToleranceSeconds)
                        break;

                    if (!CanCompactMilestonePair(anchor, candidate))
                        continue;

                    float nextFunds = mergedFunds;
                    float nextRep = mergedRep;
                    float nextScience = mergedScience;
                    if (!TryMergeMilestoneReward(ref nextFunds, candidate.MilestoneFundsAwarded) ||
                        !TryMergeMilestoneReward(ref nextRep, candidate.MilestoneRepAwarded) ||
                        !TryMergeMilestoneReward(ref nextScience, candidate.MilestoneScienceAwarded))
                        continue;

                    mergedFunds = nextFunds;
                    mergedRep = nextRep;
                    mergedScience = nextScience;
                    mergedEffective |= candidate.IsEffective;
                    if ((int)candidate.Tier < (int)mergedTier)
                    mergedTier = candidate.Tier;
                    mergedRecordingId = MergeCompactedMetadata(mergedRecordingId, candidate.RecordingId);
                    mergedVesselName = MergeCompactedMetadata(mergedVesselName, candidate.VesselName);
                    removalIndices.Add(j);
                }

                if (removalIndices.Count == 0)
                    continue;

                anchor.MilestoneFundsAwarded = mergedFunds;
                anchor.MilestoneRepAwarded = mergedRep;
                anchor.MilestoneScienceAwarded = mergedScience;
                anchor.DisplayText = TimelineEntryDisplay.GetMilestoneAchievementText(
                    anchor.MilestoneId,
                    mergedFunds,
                    mergedRep,
                    mergedScience);
                anchor.IsEffective = mergedEffective;
                anchor.Tier = mergedTier;
                anchor.RecordingId = mergedRecordingId;
                anchor.VesselName = mergedVesselName;

                for (int removal = removalIndices.Count - 1; removal >= 0; removal--)
                    entries.RemoveAt(removalIndices[removal]);
                compactedRows += removalIndices.Count;
            }

            return compactedRows;
        }

        // ---- Recording Collector ----

        private static int CollectRecordingEntries(
            IReadOnlyList<Recording> recordings,
            Dictionary<string, string> vesselNameById,
            List<TimelineEntry> entries)
        {
            int count = 0;
            int hiddenSkipped = 0;
            int crewDeathCount = 0;

            int debrisSkipped = 0;
            int treeChildSkipped = 0;
            int chainChildSkipped = 0;
            int destroyedSkipped = 0;
            int midChainSkipped = 0;

            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec.Hidden) { hiddenSkipped++; continue; }
                if (rec.IsDebris) { debrisSkipped++; continue; }

                // EVA detection: EvaCrewName set, or vessel name matches a crew end state
                // (standalone EVA recordings may not have EvaCrewName set)
                bool isEva = !string.IsNullOrEmpty(rec.EvaCrewName) ||
                    (rec.CrewEndStates != null && rec.CrewEndStates.Count == 1 &&
                     rec.CrewEndStates.ContainsKey(rec.VesselName));
                string parentVesselName = null;
                if (isEva && !string.IsNullOrEmpty(rec.ParentRecordingId))
                    vesselNameById.TryGetValue(rec.ParentRecordingId, out parentVesselName);

                // Classification flags
                bool isTreeChild = !string.IsNullOrEmpty(rec.ParentBranchPointId) && !isEva;
                bool isChainChild = rec.ChainIndex > 0;
                bool isDestroyedTerminal = rec.TerminalStateValue == TerminalState.Destroyed;
                bool isMidChain = IsChainMidSegment(rec, recordings);

                // Tree continuation: parent has ChildBranchPointId → same-PID child exists
                // (EVA/staging/breakup). The parent should not spawn; only the leaf spawns.
                // Conversely, a tree child that IS the leaf for its vessel should spawn. (#227)
                bool hasSamePidContinuation = HasSamePidTreeContinuation(rec, recordings);
                bool isTreeLeaf = isTreeChild && !hasSamePidContinuation
                    && string.IsNullOrEmpty(rec.ChildBranchPointId);

                // RecordingStart — only for true launches and EVAs.
                // Skip: optimizer-split segments (ChainIndex > 0) and tree branch children
                // (ParentBranchPointId set — created by staging/decouple/breakup, not player launch).
                // EVA recordings with a parent branch point are still shown (as "EVA:" entries).
                if (isChainChild)
                    chainChildSkipped++;
                else if (isTreeChild)
                    treeChildSkipped++;

                if (TryAddRecordingStartEntry(
                    rec,
                    i,
                    isEva,
                    isChainChild,
                    isTreeChild,
                    parentVesselName,
                    recordings,
                    entries))
                    count++;

                // Separation — tree-child split point. Two flavours:
                //
                //   * UnfinishedFlightSeparation (T1, default-visible) —
                //     terminal=Destroyed/Crashed AND a matching RP exists.
                //     Renders with a Fly button in the timeline so the
                //     player can re-fly directly from here.
                //
                //   * Separation (T2, detail-only) — formerly-UF rows
                //     whose RP was reaped on merge. Just label and GoTo
                //     button; no Fly action.
                //
                // The choice is rebuilt on every cache refresh, so a UF
                // entry morphs into a regular Separation the next rebuild
                // after the player merges.
                //
                // Debris children (uncontrolled fragments after breakup,
                // <see cref="Recording.IsDebris"/> set in BackgroundRecorder
                // when the new vessel has no controller) are SKIPPED
                // entirely. The player asked us to keep these out of the
                // timeline — they make the list too long without telling
                // the player anything they didn't already know from
                // watching the rocket break apart.
                if (TryAddSeparationEntry(rec, i, isTreeChild, entries))
                    count++;

                // VesselSpawn at EndUT — vessel materializes after ghost playback.
                // Skip: mid-chain segments, destroyed terminals (can't spawn a destroyed
                // vessel), mid-tree segments with a same-PID continuation (#227), and
                // tree children that aren't the effective vessel leaf.
                // Bug #433: do NOT gate on rec.PlaybackEnabled. The visibility toggle
                // is visual-only; the vessel still spawns at ghost-end in-world, so
                // the timeline must still show it.
                bool suppressTreeSpawn = hasSamePidContinuation || (isTreeChild && !isTreeLeaf);
                bool suppressSupersededSpawn =
                    !string.IsNullOrEmpty(rec.TerminalSpawnSupersededByRecordingId);
                if (isMidChain) midChainSkipped++;
                else if (isDestroyedTerminal) destroyedSkipped++;

                if (TryAddVesselSpawnEntry(
                    rec,
                    i,
                    isEva,
                    parentVesselName,
                    isMidChain,
                    isDestroyedTerminal,
                    suppressTreeSpawn,
                    suppressSupersededSpawn,
                    entries))
                    count++;

                // CrewDeath — one entry per dead kerbal (bug #229).
                // Uses CrewEndStates populated by KerbalsModule at commit time.
                // Placed at rec.EndUT because CrewEndStates has no per-kerbal death
                // timestamp — usually correct (death = vessel destruction at EndUT),
                // but inaccurate if crew died mid-recording (e.g., decoupled crew cabin).
                int addedCrewDeaths = AddCrewDeathEntries(rec, entries);
                count += addedCrewDeaths;
                crewDeathCount += addedCrewDeaths;
            }

            ParsekLog.Verbose("Timeline",
                $"Recording collector: {count} entries from {recordings.Count} recordings " +
                $"(hidden={hiddenSkipped} debris={debrisSkipped} treeChild={treeChildSkipped} " +
                $"chainChild={chainChildSkipped} destroyed={destroyedSkipped} " +
                $"midChain={midChainSkipped} crewDeath={crewDeathCount})");

            return count;
        }

        private static bool TryAddRecordingStartEntry(
            Recording rec,
            int recordingIndex,
            bool isEva,
            bool isChainChild,
            bool isTreeChild,
            string parentVesselName,
            IReadOnlyList<Recording> recordings,
            List<TimelineEntry> entries)
        {
            if (isChainChild || isTreeChild)
                return false;

            double duration = rec.EndUT - rec.StartUT;
            if (!string.IsNullOrEmpty(rec.ChainId))
                duration = GetChainDuration(rec.ChainId, recordings);

            var startType = TimelineEntryType.RecordingStart;
            string displayText = TimelineEntryDisplay.GetRecordingStartText(
                rec.VesselName,
                duration,
                isEva,
                parentVesselName,
                rec.StartBodyName,
                rec.StartBiome,
                rec.LaunchSiteName);
            entries.Add(new TimelineEntry
            {
                UT = rec.StartUT,
                Type = startType,
                DisplayText = displayText,
                Source = TimelineSource.Recording,
                Tier = TimelineEntryDisplay.GetTier(startType),
                DisplayColor = Color.white,
                RecordingId = rec.RecordingId,
                VesselName = rec.VesselName
            });

            ParsekLog.Verbose("Timeline",
                $"  +Start #{recordingIndex} '{rec.VesselName}' UT={rec.StartUT:F1} " +
                $"isEva={isEva} chainIdx={rec.ChainIndex} terminal={rec.TerminalStateValue} " +
                $"text=\"{displayText}\"");

            return true;
        }

        private static bool TryAddSeparationEntry(
            Recording rec,
            int recordingIndex,
            bool isTreeChild,
            List<TimelineEntry> entries)
        {
            if (!isTreeChild || rec.IsDebris)
                return false;

            bool isUf = EffectiveState.IsUnfinishedFlight(rec);
            var sepType = isUf
                ? TimelineEntryType.UnfinishedFlightSeparation
                : TimelineEntryType.Separation;
            string displayText = isUf
                ? TimelineEntryDisplay.GetUnfinishedFlightSeparationText(rec.VesselName)
                : TimelineEntryDisplay.GetSeparationText(rec.VesselName);
            entries.Add(new TimelineEntry
            {
                UT = rec.StartUT,
                Type = sepType,
                DisplayText = displayText,
                Source = TimelineSource.Recording,
                Tier = TimelineEntryDisplay.GetTier(sepType),
                DisplayColor = Color.white,
                RecordingId = rec.RecordingId,
                VesselName = rec.VesselName,
            });

            ParsekLog.Verbose("Timeline",
                $"  +{(isUf ? "UF-Separation" : "Separation")} #{recordingIndex} '{rec.VesselName}' " +
                $"UT={rec.StartUT:F1} terminal={rec.TerminalStateValue} text=\"{displayText}\"");

            return true;
        }

        private static bool TryAddVesselSpawnEntry(
            Recording rec,
            int recordingIndex,
            bool isEva,
            string parentVesselName,
            bool isMidChain,
            bool isDestroyedTerminal,
            bool suppressTreeSpawn,
            bool suppressSupersededSpawn,
            List<TimelineEntry> entries)
        {
            if (isMidChain || isDestroyedTerminal || suppressTreeSpawn || suppressSupersededSpawn)
                return false;

            var spawnType = TimelineEntryType.VesselSpawn;
            string displayText = TimelineEntryDisplay.GetVesselSpawnText(
                rec.VesselName,
                rec.TerminalStateValue,
                rec.VesselSituation,
                isEva,
                parentVesselName,
                rec.TerminalOrbitBody,
                rec.SegmentBodyName,
                rec.EndBiome);
            entries.Add(new TimelineEntry
            {
                UT = rec.EndUT,
                Type = spawnType,
                DisplayText = displayText,
                Source = TimelineSource.Recording,
                Tier = TimelineEntryDisplay.GetTier(spawnType),
                DisplayColor = Color.white,
                RecordingId = rec.RecordingId,
                VesselName = rec.VesselName
            });

            ParsekLog.Verbose("Timeline",
                $"  +Spawn #{recordingIndex} '{rec.VesselName}' UT={rec.EndUT:F1} " +
                $"terminal={rec.TerminalStateValue} sit=\"{rec.VesselSituation}\" " +
                $"text=\"{displayText}\"");

            return true;
        }

        private static int AddCrewDeathEntries(Recording rec, List<TimelineEntry> entries)
        {
            if (rec.CrewEndStates == null)
                return 0;

            int count = 0;
            foreach (var kvp in rec.CrewEndStates)
            {
                if (kvp.Value != KerbalEndState.Dead)
                    continue;

                var deathType = TimelineEntryType.CrewDeath;
                string deathText = TimelineEntryDisplay.GetCrewDeathText(kvp.Key, rec.VesselName);
                entries.Add(new TimelineEntry
                {
                    UT = rec.EndUT,
                    Type = deathType,
                    DisplayText = deathText,
                    Source = TimelineSource.Recording,
                    Tier = TimelineEntryDisplay.GetTier(deathType),
                    DisplayColor = new Color(1f, 0.4f, 0.4f), // red-ish
                    RecordingId = rec.RecordingId,
                    VesselName = rec.VesselName
                });
                count++;
            }

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

        /// <summary>
        /// Returns true if the recording has a ChildBranchPointId and another recording
        /// in the list is a child of that branch point with the same VesselPersistentId.
        /// This means the vessel continues in a tree child — the parent is a mid-tree
        /// segment and should not produce a spawn entry. (#227)
        /// Flat-list equivalent of <see cref="GhostPlaybackLogic.IsEffectiveLeafForVessel"/>
        /// (inverted sense: true here = NOT effective leaf).
        /// </summary>
        internal static bool HasSamePidTreeContinuation(Recording rec, IReadOnlyList<Recording> recordings)
        {
            if (string.IsNullOrEmpty(rec.ChildBranchPointId)) return false;
            if (rec.VesselPersistentId == 0) return false;
            for (int i = 0; i < recordings.Count; i++)
            {
                var other = recordings[i];
                if (other.ParentBranchPointId == rec.ChildBranchPointId
                    && other.VesselPersistentId == rec.VesselPersistentId)
                    return true;
            }
            return false;
        }

        // ---- Game Action Collector ----

        /// <summary>
        /// Builds a HashSet of (parentRecordingId, startUT) keys from EVA recordings.
        /// When a kerbal EVAs, KSP reshuffles the remaining crew — those KerbalAssignment
        /// actions at the EVA's startUT on the parent recording are noise (bug #228).
        /// Extracted for testability.
        /// </summary>
        internal static HashSet<string> BuildEvaBranchKeys(IReadOnlyList<Recording> recordings)
        {
            var keys = new HashSet<string>();
            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (string.IsNullOrEmpty(rec.EvaCrewName)) continue;
                if (string.IsNullOrEmpty(rec.ParentRecordingId)) continue;
                keys.Add(EncodeEvaBranchKey(rec.ParentRecordingId, rec.StartUT));
            }
            return keys;
        }

        /// <summary>
        /// Encodes a (recordingId, ut) pair as a string key for HashSet lookup.
        /// Uses round-trip format for UT to avoid floating-point matching issues.
        /// </summary>
        internal static string EncodeEvaBranchKey(string recordingId, double ut)
        {
            return string.Concat(recordingId, ":", ut.ToString("R", IC));
        }

        private static HashSet<string> BuildLegacyDuplicateKeys(IReadOnlyList<GameAction> ledgerActions)
        {
            var keys = new HashSet<string>();
            for (int i = 0; i < ledgerActions.Count; i++)
            {
                // Only effective actions replace the legacy audit row in the Details UI.
                if (!ledgerActions[i].Effective)
                    continue;

                string duplicateKey = GetLegacyDuplicateKey(ledgerActions[i]);
                if (duplicateKey == null)
                    continue;

                keys.Add(duplicateKey);
            }
            return keys;
        }

        private static string GetLegacyDuplicateKey(GameAction action)
        {
            switch (action.Type)
            {
                case GameActionType.MilestoneAchievement:
                    return EncodeLegacyDuplicateKey(
                        GameStateEventType.MilestoneAchieved,
                        action.UT,
                        action.MilestoneId);

                case GameActionType.StrategyActivate:
                    return EncodeLegacyDuplicateKey(
                        GameStateEventType.StrategyActivated,
                        action.UT,
                        action.StrategyId);

                case GameActionType.StrategyDeactivate:
                    return EncodeLegacyDuplicateKey(
                        GameStateEventType.StrategyDeactivated,
                        action.UT,
                        action.StrategyId);

                default:
                    return null;
            }
        }

        private static string EncodeLegacyDuplicateKey(
            GameStateEventType eventType,
            double ut,
            string key)
        {
            return string.Concat(
                ((int)eventType).ToString(IC),
                ":",
                ut.ToString("R", IC),
                ":",
                key ?? string.Empty);
        }

        private static int CollectGameActionEntries(
            IReadOnlyList<GameAction> ledgerActions,
            Dictionary<string, string> vesselNameById,
            HashSet<string> evaBranchKeys,
            Game.Modes? currentMode,
            List<TimelineEntry> entries)
        {
            int count = 0;
            int evaReassignSkipped = 0;
            int modeSeedSkipped = 0;
            int hireCostSuffixSuppressed = 0;
            for (int i = 0; i < ledgerActions.Count; i++)
            {
                var action = ledgerActions[i];

                if (!IsInitialResourceSeedVisibleInMode(action.Type, currentMode))
                {
                    modeSeedSkipped++;
                    continue;
                }

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

                // Bug #228: skip crew reassignment at EVA branch time — KSP auto-reshuffles
                // remaining crew when someone EVAs, creating noise KerbalAssignment actions.
                if (action.Type == GameActionType.KerbalAssignment &&
                    !string.IsNullOrEmpty(action.RecordingId) &&
                    evaBranchKeys.Contains(EncodeEvaBranchKey(action.RecordingId, action.UT)))
                {
                    evaReassignSkipped++;
                    continue;
                }

                string displayText = TimelineEntryDisplay.GetGameActionText(action, vesselName, currentMode);
                if (action.Type == GameActionType.KerbalHire &&
                    action.HireCost > 0f &&
                    !GameActionDisplay.ShouldShowFundsForKerbalHire(action, currentMode))
                {
                    hireCostSuffixSuppressed++;
                }

                entries.Add(new TimelineEntry
                {
                    UT = action.UT,
                    Type = entryType,
                    DisplayText = displayText,
                    Source = TimelineSource.GameAction,
                    Tier = tier,
                    DisplayColor = GameActionDisplay.GetColor(action.Type),
                    RecordingId = action.RecordingId,
                    VesselName = vesselName,
                    IsEffective = action.Effective,
                    IsPlayerAction = TimelineEntryDisplay.IsPlayerAction(entryType),
                    MilestoneId = action.MilestoneId,
                    MilestoneFundsAwarded = action.MilestoneFundsAwarded,
                    MilestoneRepAwarded = action.MilestoneRepAwarded,
                    MilestoneScienceAwarded = action.MilestoneScienceAwarded
                });
                count++;
            }

            if (evaReassignSkipped > 0)
                ParsekLog.Verbose("Timeline",
                    $"Filtered {evaReassignSkipped} KerbalAssignment action(s) at EVA branch time(s)");

            if (modeSeedSkipped > 0)
                ParsekLog.Verbose("Timeline",
                    $"Filtered {modeSeedSkipped} mode-inapplicable initial resource action(s)");

            if (hireCostSuffixSuppressed > 0)
                ParsekLog.Verbose("Timeline",
                    $"Filtered {hireCostSuffixSuppressed} kerbal-hire funds suffix(es) in mode {FormatModeForLog(currentMode)}");

            return count;
        }

        private static string FormatModeForLog(Game.Modes? currentMode)
        {
            return currentMode.HasValue ? currentMode.Value.ToString() : "(unknown)";
        }

        internal static bool IsInitialResourceSeedVisibleInMode(GameActionType type, Game.Modes? currentMode)
        {
            if (!IsInitialResourceSeed(type))
                return true;
            if (!currentMode.HasValue)
                return true;

            switch (currentMode.Value)
            {
                case Game.Modes.SCIENCE_SANDBOX:
                    return type == GameActionType.ScienceInitial;

                case Game.Modes.SANDBOX:
                case Game.Modes.MISSION_BUILDER:
                case Game.Modes.MISSION:
                    return false;

                default:
                    return true;
            }
        }

        private static bool IsInitialResourceSeed(GameActionType type)
        {
            return type == GameActionType.FundsInitial
                || type == GameActionType.ScienceInitial
                || type == GameActionType.ReputationInitial;
        }

        private static bool CanCompactMilestoneEntry(TimelineEntry entry)
        {
            return entry != null
                && entry.Source == TimelineSource.GameAction
                && entry.Type == TimelineEntryType.MilestoneAchievement
                && !string.IsNullOrEmpty(entry.MilestoneId);
        }

        private static bool CanCompactMilestonePair(TimelineEntry anchor, TimelineEntry candidate)
        {
            return CanCompactMilestoneEntry(candidate)
                && Math.Abs(candidate.UT - anchor.UT) <= MilestoneCompactionUtToleranceSeconds
                && string.Equals(candidate.MilestoneId, anchor.MilestoneId, StringComparison.Ordinal);
        }

        private static bool TryMergeMilestoneReward(ref float aggregate, float candidate)
        {
            if (Mathf.Abs(candidate) <= MilestoneCompactionEpsilon)
                return true;

            if (Mathf.Abs(aggregate) <= MilestoneCompactionEpsilon)
            {
                aggregate = candidate;
                return true;
            }

            return Mathf.Abs(aggregate - candidate) <= MilestoneCompactionEpsilon;
        }

        private static string MergeCompactedMetadata(string current, string candidate)
        {
            if (string.IsNullOrEmpty(current))
                return candidate;
            if (string.IsNullOrEmpty(candidate))
                return current;
            return string.Equals(current, candidate, StringComparison.Ordinal)
                ? current
                : null;
        }

        // ---- Legacy Collector ----

        private static int CollectLegacyEntries(
            IReadOnlyList<Milestone> milestones,
            Func<GameStateEvent, bool> isLegacyEventVisible,
            HashSet<string> legacyDuplicateKeys,
            List<TimelineEntry> entries)
        {
            int count = 0;
            int duplicateSkipped = 0;

            for (int i = 0; i < milestones.Count; i++)
            {
                var m = milestones[i];
                if (!m.Committed) continue;

                for (int j = 0; j < m.Events.Count; j++)
                {
                    var e = m.Events[j];
                    if (!isLegacyEventVisible(e)) continue;
                    if (GameStateStore.IsMilestoneFilteredEvent(e.eventType)) continue;
                    if (legacyDuplicateKeys.Contains(EncodeLegacyDuplicateKey(e.eventType, e.ut, e.key)))
                    {
                        duplicateSkipped++;
                        continue;
                    }

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

            if (duplicateSkipped > 0)
                ParsekLog.Verbose("Timeline",
                    $"Filtered {duplicateSkipped} duplicate legacy milestone/strategy event(s)");

            return count;
        }
    }
}
