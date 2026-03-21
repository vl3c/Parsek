using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Static holder for recording data that survives scene changes.
    /// Static fields persist across scene loads within a KSP session.
    /// Save/load persistence is handled separately by ParsekScenario.
    /// </summary>
    public static class RecordingStore
    {
        public const int CurrentRecordingFormatVersion = 7;
        // v7: Added TerrainHeightAtEnd for surface spawn terrain correction
        // v6: Added SegmentEvents, TrackSections, ControllerInfo, extended BranchPoint types

        // When true, suppresses logging calls (for unit testing outside Unity)
        internal static bool SuppressLogging;

        // Rewind flags (survive scene change via static fields)
        internal static bool IsRewinding;
        internal static double RewindUT;
        internal static double RewindAdjustedUT;
        internal static ResourceBudget.BudgetSummary RewindReserved;

        // Baseline resource values from the rewind-target recording's PreLaunch snapshot.
        // Used by the deferred coroutine to compute absolute-target resource corrections
        // (idempotent regardless of what Funding.OnLoad restores from the save).
        internal static double RewindBaselineFunds;
        internal static double RewindBaselineScience;
        internal static float RewindBaselineRep;

        private const string LegacyPrefix = "[Parsek] ";

        static void Log(string message)
        {
            if (SuppressLogging) return;

            string clean = message ?? "(empty)";
            if (clean.StartsWith(LegacyPrefix, StringComparison.Ordinal))
                clean = clean.Substring(LegacyPrefix.Length);

            if (clean.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase) ||
                clean.StartsWith("WARN:", StringComparison.OrdinalIgnoreCase))
            {
                int idx = clean.IndexOf(':');
                string trimmed = idx >= 0 ? clean.Substring(idx + 1).TrimStart() : clean;
                ParsekLog.Warn("RecordingStore", trimmed);
                return;
            }

            ParsekLog.Info("RecordingStore", clean);
        }

        /// <summary>
        /// Determines the recommended merge action based on vessel state.
        /// </summary>
        public static MergeDefault GetRecommendedAction(bool destroyed, bool hasSnapshot)
        {
            if (destroyed || !hasSnapshot)
                return MergeDefault.GhostOnly;
            return MergeDefault.Persist;
        }

        // Just-finished recording awaiting user decision (merge or discard)
        private static Recording pendingRecording;

        // Merged to timeline — these auto-playback during flight.
        //
        // POLICY: Individual recording deletion is not supported.
        // Recordings can only be committed (at merge dialog) or discarded (before commit).
        // The entire timeline can be wiped, but individual recordings cannot be removed
        // after commit. This prevents time paradoxes (orphaned vessels, broken chains,
        // inconsistent ghost playback).
        // Future: timeline wipe from current UT forward (clear future, keep past).
        private static List<Recording> committedRecordings = new List<Recording>();

        // Committed recording trees (parallel storage — tree recordings also appear in committedRecordings)
        private static List<RecordingTree> committedTrees = new List<RecordingTree>();

        // Pending tree awaiting merge dialog (Task 8) or auto-commit
        private static RecordingTree pendingTree;

        public static bool HasPending => pendingRecording != null;
        public static Recording Pending => pendingRecording;
        public static List<Recording> CommittedRecordings => committedRecordings;
        public static List<RecordingTree> CommittedTrees => committedTrees;
        public static bool HasPendingTree => pendingTree != null;
        public static RecordingTree PendingTree => pendingTree;

        public static void StashPending(List<TrajectoryPoint> points, string vesselName,
            List<OrbitSegment> orbitSegments = null,
            string recordingId = null,
            int? recordingFormatVersion = null,
            List<PartEvent> partEvents = null,
            List<SegmentEvent> segmentEvents = null,
            List<TrackSection> trackSections = null)
        {
            if (points == null || points.Count < 2)
            {
                Log($"[Parsek] Recording too short for '{vesselName}' ({points?.Count ?? 0} points, need >= 2) — discarded");
                return;
            }

            // Trim leading stationary points (vessel sitting on pad/runway before launch).
            // This prevents the ghost from overlapping the real vessel at the start position.
            // Also trims any orbit segments and part events that fall before the new start.
            int firstMoving = TrajectoryMath.FindFirstMovingPoint(points);
            if (firstMoving > 0)
            {
                double trimUT = points[firstMoving].ut;
                Log($"[Parsek] Trimmed {firstMoving} leading stationary points for '{vesselName}' " +
                    $"(alt delta < 1m, speed < 5 m/s, new startUT={trimUT:F1})");
                points = points.GetRange(firstMoving, points.Count - firstMoving);
                if (points.Count < 2)
                {
                    Log($"[Parsek] Recording too short after trimming for '{vesselName}' ({points.Count} points) — discarded");
                    return;
                }
                // Remove orbit segments that end before the new start
                if (orbitSegments != null)
                    orbitSegments.RemoveAll(s => s.endUT <= trimUT);
                // Retime part events from the trimmed window to the new start so their
                // visual effects (shroud jettison, engine ignition, etc.) are applied
                // at the beginning of playback rather than being lost.
                if (partEvents != null)
                {
                    for (int i = 0; i < partEvents.Count; i++)
                    {
                        if (partEvents[i].ut < trimUT)
                        {
                            var e = partEvents[i];
                            e.ut = trimUT;
                            partEvents[i] = e;
                        }
                    }
                }
            }

            if (pendingRecording != null)
            {
                ParsekLog.Warn("RecordingStore",
                    $"StashPending: overwriting unresolved pending from '{pendingRecording.VesselName}' " +
                    $"with new pending from '{vesselName}' — discarding old pending");
                ParsekScenario.UnreserveCrewInSnapshot(pendingRecording.VesselSnapshot);
                DiscardPending();
            }

            pendingRecording = new Recording
            {
                RecordingId = string.IsNullOrEmpty(recordingId) ? Guid.NewGuid().ToString("N") : recordingId,
                RecordingFormatVersion = recordingFormatVersion ?? CurrentRecordingFormatVersion,
                Points = new List<TrajectoryPoint>(points),
                OrbitSegments = orbitSegments != null
                    ? new List<OrbitSegment>(orbitSegments)
                    : new List<OrbitSegment>(),
                PartEvents = partEvents != null
                    ? new List<PartEvent>(partEvents)
                    : new List<PartEvent>(),
                SegmentEvents = segmentEvents != null
                    ? new List<SegmentEvent>(segmentEvents)
                    : new List<SegmentEvent>(),
                TrackSections = trackSections != null
                    ? new List<TrackSection>(trackSections)
                    : new List<TrackSection>(),
                VesselName = vesselName
            };

            Log($"[Parsek] Stashed pending recording: {points.Count} points, " +
                $"{pendingRecording.OrbitSegments.Count} orbit segments from {vesselName}");
        }

        public static void CommitPending()
        {
            if (pendingRecording == null)
            {
                ParsekLog.Verbose("RecordingStore", "CommitPending called with no pending recording");
                return;
            }

            committedRecordings.Add(pendingRecording);
            Log($"[Parsek] Committed recording from {pendingRecording.VesselName} " +
                $"({pendingRecording.Points.Count} points). Total committed: {committedRecordings.Count}");

            // Commit pending science subjects before clearing
            GameStateStore.CommitScienceSubjects(GameStateRecorder.PendingScienceSubjects);
            GameStateRecorder.PendingScienceSubjects.Clear();

            string recordingId = pendingRecording.RecordingId;
            double endUT = pendingRecording.EndUT;
            pendingRecording = null;

            ResourceBudget.Invalidate();

            // Capture a game state baseline at each commit (single funnel point)
            GameStateStore.CaptureBaselineIfNeeded();

            // Create a milestone bundling game state events since the previous milestone
            MilestoneStore.CreateMilestone(recordingId, endUT);
        }

        public static void DiscardPending()
        {
            if (pendingRecording == null)
            {
                ParsekLog.Verbose("RecordingStore", "DiscardPending called with no pending recording");
                return;
            }

            DeleteRecordingFiles(pendingRecording);
            GameStateRecorder.PendingScienceSubjects.Clear();

            Log($"[Parsek] Discarded pending recording from {pendingRecording.VesselName}");
            pendingRecording = null;
            ResourceBudget.Invalidate();
        }

        public static void ClearCommitted()
        {
            int count = committedRecordings.Count;
            for (int i = 0; i < committedRecordings.Count; i++)
                DeleteRecordingFiles(committedRecordings[i]);
            committedRecordings.Clear();
            committedTrees.Clear();
            GameStateRecorder.PendingScienceSubjects.Clear();
            ResourceBudget.Invalidate();
            Log($"[Parsek] Cleared {count} committed recordings and all trees");
        }

        public static void Clear()
        {
            if (pendingRecording != null)
                DeleteRecordingFiles(pendingRecording);
            pendingRecording = null;
            pendingTree = null;
            ClearCommitted();
            Log("[Parsek] All recordings cleared");
        }

        /// <summary>
        /// Commits a recording tree directly to the timeline.
        /// Adds all recordings to CommittedRecordings (for ghost playback)
        /// and the tree itself to CommittedTrees (for tree-specific queries).
        /// </summary>
        public static void CommitTree(RecordingTree tree)
        {
            if (tree == null) return;

            // Duplicate guard: skip if tree with same ID already committed
            for (int i = 0; i < committedTrees.Count; i++)
            {
                if (committedTrees[i].Id == tree.Id)
                {
                    Log($"[Parsek] WARNING: Tree '{tree.Id}' already committed — skipping duplicate");
                    GameStateRecorder.PendingScienceSubjects.Clear();
                    return;
                }
            }

            // Merge overlapping data sources before committing.
            // Strategy: start from the original recording (preserves ALL fields by default),
            // then overwrite only the merge-produced fields. This ensures new Recording fields
            // added in the future are preserved without requiring an explicit copy here.
            var mergedRecordings = SessionMerger.MergeTree(tree);
            foreach (var kvp in mergedRecordings)
            {
                Recording original;
                if (tree.Recordings.TryGetValue(kvp.Key, out original))
                {
                    Recording merged = kvp.Value;

                    // Overwrite only the merge-produced fields on the original
                    original.TrackSections = merged.TrackSections;
                    original.PartEvents = merged.PartEvents;
                    original.SegmentEvents = merged.SegmentEvents;
                    original.Points = merged.Points;
                    original.OrbitSegments = merged.OrbitSegments;
                    ParsekLog.Verbose("Merger",
                        $"CommitTree: merged recording '{kvp.Key}' in-place " +
                        $"(sections={original.TrackSections?.Count ?? 0} events={original.PartEvents?.Count ?? 0})");
                }
            }

            // Auto-group: assign all tree recordings to a group named after the tree
            // so they appear collapsed in the recordings window instead of as separate entries.
            if (!string.IsNullOrEmpty(tree.TreeName) && tree.Recordings.Count > 1)
            {
                string groupName = tree.TreeName;
                foreach (var rec in tree.Recordings.Values)
                {
                    if (rec.RecordingGroups == null)
                        rec.RecordingGroups = new List<string>();
                    if (!rec.RecordingGroups.Contains(groupName))
                        rec.RecordingGroups.Add(groupName);
                }
                ParsekLog.Info("RecordingStore",
                    $"Auto-grouped {tree.Recordings.Count} recordings under '{groupName}'");
            }

            // Add all tree recordings to committedRecordings (enables ghost playback)
            foreach (var rec in tree.Recordings.Values)
            {
                committedRecordings.Add(rec);
            }

            // Ensure OwnedVesselPids is populated (covers runtime-created trees
            // that never went through RecordingTree.Load)
            tree.RebuildBackgroundMap();

            committedTrees.Add(tree);

            // Commit pending science subjects before clearing
            GameStateStore.CommitScienceSubjects(GameStateRecorder.PendingScienceSubjects);
            GameStateRecorder.PendingScienceSubjects.Clear();

            Log($"[Parsek] Committed tree '{tree.TreeName}' ({tree.Recordings.Count} recordings). " +
                $"Total committed: {committedRecordings.Count} recordings, {committedTrees.Count} trees");

            ResourceBudget.Invalidate();

            // Capture a game state baseline at each commit
            GameStateStore.CaptureBaselineIfNeeded();

            // Create a milestone bundling game state events since the previous milestone
            double endUT = 0;
            foreach (var rec in tree.Recordings.Values)
            {
                double recEnd = rec.EndUT;
                if (recEnd > endUT) endUT = recEnd;
            }
            MilestoneStore.CreateMilestone(tree.Id, endUT);
        }

        /// <summary>
        /// Stashes a finalized tree as pending (for merge dialog on revert).
        /// </summary>
        public static void StashPendingTree(RecordingTree tree)
        {
            pendingTree = tree;
            if (tree != null)
                Log($"[Parsek] Stashed pending tree '{tree.TreeName}' ({tree.Recordings.Count} recordings)");
        }

        /// <summary>
        /// Commits the pending tree to the timeline.
        /// </summary>
        public static void CommitPendingTree()
        {
            if (pendingTree == null)
            {
                ParsekLog.Verbose("RecordingStore", "CommitPendingTree called with no pending tree");
                return;
            }

            CommitTree(pendingTree);
            pendingTree = null;
        }

        /// <summary>
        /// Discards the pending tree and cleans up its recording files.
        /// </summary>
        public static void DiscardPendingTree()
        {
            if (pendingTree == null)
            {
                ParsekLog.Verbose("RecordingStore", "DiscardPendingTree called with no pending tree");
                return;
            }

            foreach (var rec in pendingTree.Recordings.Values)
                DeleteRecordingFiles(rec);
            GameStateRecorder.PendingScienceSubjects.Clear();
            Log($"[Parsek] Discarded pending tree '{pendingTree.TreeName}'");
            pendingTree = null;
            ResourceBudget.Invalidate();
        }

        /// <summary>
        /// Returns true if this recording is a mid-chain segment (not the last in its chain).
        /// Mid-chain ghosts should hold at their final position instead of being despawned.
        /// </summary>
        internal static bool IsChainMidSegment(Recording rec)
        {
            if (string.IsNullOrEmpty(rec.ChainId) || rec.ChainIndex < 0) return false;
            // Branch > 0 segments are parallel continuations (ghost-only); they despawn normally
            if (rec.ChainBranch > 0) return false;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var other = committedRecordings[i];
                if (other.ChainId == rec.ChainId && other.ChainBranch == 0 && other.ChainIndex > rec.ChainIndex)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the EndUT of the last segment in this recording's chain.
        /// Returns rec.EndUT if the recording is not part of a chain.
        /// </summary>
        internal static double GetChainEndUT(Recording rec)
        {
            if (string.IsNullOrEmpty(rec.ChainId) || rec.ChainIndex < 0) return rec.EndUT;
            double maxEnd = rec.EndUT;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var other = committedRecordings[i];
                // Only branch 0 determines the chain's end (primary path)
                if (other.ChainId == rec.ChainId && other.ChainBranch == 0 && other.EndUT > maxEnd)
                    maxEnd = other.EndUT;
            }
            return maxEnd;
        }

        /// <summary>
        /// Returns all committed recordings with the given chainId, sorted by ChainIndex.
        /// Returns null if chainId is null/empty or no matches found.
        /// </summary>
        internal static List<Recording> GetChainRecordings(string chainId)
        {
            if (string.IsNullOrEmpty(chainId)) return null;

            List<Recording> chain = null;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                if (committedRecordings[i].ChainId == chainId)
                {
                    if (chain == null) chain = new List<Recording>();
                    chain.Add(committedRecordings[i]);
                }
            }

            if (chain != null && chain.Count > 1)
            {
                chain.Sort((a, b) =>
                {
                    int branchCmp = a.ChainBranch.CompareTo(b.ChainBranch);
                    return branchCmp != 0 ? branchCmp : a.ChainIndex.CompareTo(b.ChainIndex);
                });
            }

            return chain;
        }

        /// <summary>
        /// Removes all committed recordings with the given chainId, deleting their files.
        /// Call only when no timeline ghosts are active (e.g. from merge dialog before playback).
        /// </summary>
        internal static void RemoveChainRecordings(string chainId)
        {
            if (string.IsNullOrEmpty(chainId)) return;

            for (int i = committedRecordings.Count - 1; i >= 0; i--)
            {
                if (committedRecordings[i].ChainId == chainId)
                {
                    DeleteRecordingFiles(committedRecordings[i]);
                    Log($"[Parsek] Removed chain recording: {committedRecordings[i].VesselName} (chain={chainId}, idx={committedRecordings[i].ChainIndex})");
                    committedRecordings.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Validates chain integrity among committed recordings.
        /// Chains with gaps, duplicate indices, or non-monotonic StartUT are degraded
        /// to standalone recordings (ChainId/ChainIndex cleared).
        /// </summary>
        internal static void ValidateChains()
        {
            // Group by (ChainId, ChainBranch)
            var branches = new Dictionary<string, List<Recording>>();
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (string.IsNullOrEmpty(rec.ChainId)) continue;
                string key = rec.ChainId + ":" + rec.ChainBranch;
                List<Recording> list;
                if (!branches.TryGetValue(key, out list))
                {
                    list = new List<Recording>();
                    branches[key] = list;
                }
                list.Add(rec);
            }

            ParsekLog.Verbose("RecordingStore", $"Validating chains: {branches.Count} branch group(s)");

            // Track which chainIds are invalid so we degrade all branches together
            var invalidChains = new HashSet<string>();

            foreach (var kvp in branches)
            {
                var list = kvp.Value;
                list.Sort((a, b) => a.ChainIndex.CompareTo(b.ChainIndex));

                string chainId = list[0].ChainId;
                int branch = list[0].ChainBranch;
                bool valid = true;

                if (branch == 0)
                {
                    // Branch 0: indices must be 0..N-1 with no gaps or duplicates
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i].ChainIndex != i)
                        {
                            valid = false;
                            Log($"[Parsek] Chain validation FAILED for chain={chainId} branch={branch}: expected index {i}, got {list[i].ChainIndex}");
                            break;
                        }
                    }
                }
                else
                {
                    // Branch > 0: indices must be contiguous and non-decreasing (don't need to start at 0)
                    for (int i = 1; i < list.Count; i++)
                    {
                        if (list[i].ChainIndex != list[i - 1].ChainIndex + 1)
                        {
                            valid = false;
                            Log($"[Parsek] Chain validation FAILED for chain={chainId} branch={branch}: non-contiguous index at {list[i].ChainIndex}");
                            break;
                        }
                    }
                }

                // Check StartUT is monotonically non-decreasing within branch
                if (valid)
                {
                    for (int i = 1; i < list.Count; i++)
                    {
                        if (list[i].StartUT < list[i - 1].StartUT)
                        {
                            valid = false;
                            Log($"[Parsek] Chain validation FAILED for chain={chainId} branch={branch}: non-monotonic StartUT at index {list[i].ChainIndex}");
                            break;
                        }
                    }
                }

                if (!valid)
                    invalidChains.Add(chainId);
            }

            // Degrade all recordings belonging to invalid chains
            if (invalidChains.Count == 0)
                ParsekLog.Verbose("RecordingStore", "All chains validated OK");

            if (invalidChains.Count > 0)
            {
                for (int i = 0; i < committedRecordings.Count; i++)
                {
                    var rec = committedRecordings[i];
                    if (!string.IsNullOrEmpty(rec.ChainId) && invalidChains.Contains(rec.ChainId))
                    {
                        Log($"[Parsek]   Degrading recording '{rec.VesselName}' " +
                            $"(id={rec.RecordingId}, idx={rec.ChainIndex}, branch={rec.ChainBranch}) to standalone");
                        rec.ChainId = null;
                        rec.ChainIndex = -1;
                        rec.ChainBranch = 0;
                    }
                }
                foreach (var chainId in invalidChains)
                    Log($"[Parsek] Degraded invalid chain {chainId} to standalone");
            }
        }

        /// <summary>
        /// Removes a single committed recording by index, deleting its external files.
        /// If the recording is part of a chain, all remaining chain siblings are degraded to standalone.
        /// Does NOT handle ghost cleanup or crew unreservation — caller must do that first.
        /// </summary>
        internal static void RemoveRecordingAt(int index)
        {
            if (index < 0 || index >= committedRecordings.Count)
            {
                ParsekLog.Warn("RecordingStore", $"RemoveRecordingAt called with invalid index={index} (count={committedRecordings.Count})");
                return;
            }

            var rec = committedRecordings[index];

            // If part of a chain, degrade remaining chain siblings to standalone
            if (!string.IsNullOrEmpty(rec.ChainId))
            {
                string chainId = rec.ChainId;
                for (int i = 0; i < committedRecordings.Count; i++)
                {
                    if (i == index) continue;
                    var other = committedRecordings[i];
                    if (other.ChainId == chainId)
                    {
                        Log($"[Parsek]   Degrading recording '{other.VesselName}' " +
                            $"(id={other.RecordingId}, idx={other.ChainIndex}, branch={other.ChainBranch}) to standalone");
                        other.ChainId = null;
                        other.ChainIndex = -1;
                        other.ChainBranch = 0;
                    }
                }
            }

            DeleteRecordingFiles(rec);
            committedRecordings.RemoveAt(index);
            Log($"[Parsek] Removed recording '{rec.VesselName}' (id={rec.RecordingId}) at index {index}");
        }

        /// <summary>
        /// Deletes a recording from the committed list, cleans up sidecar files, and
        /// unreserves crew. Use when there are no active ghosts (e.g. KSC scene).
        /// In flight scene, use ParsekFlight.DeleteRecording instead (handles ghost cleanup).
        /// </summary>
        public static void DeleteRecordingFull(int index)
        {
            if (index < 0 || index >= committedRecordings.Count)
            {
                ParsekLog.Warn("RecordingStore", $"DeleteRecordingFull: invalid index={index} (count={committedRecordings.Count})");
                return;
            }
            var rec = committedRecordings[index];
            ParsekLog.Info("RecordingStore", $"DeleteRecordingFull: deleting '{rec.VesselName}' at index {index}");
            ParsekScenario.UnreserveCrewInSnapshot(rec.VesselSnapshot);
            RemoveRecordingAt(index);
        }

        /// <summary>
        /// Returns true if any branch-0 enabled segment in the chain has LoopPlayback set.
        /// </summary>
        internal static bool IsChainLooping(string chainId)
        {
            if (string.IsNullOrEmpty(chainId)) return false;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (rec.ChainId == chainId && rec.ChainBranch == 0 &&
                    rec.PlaybackEnabled && rec.LoopPlayback)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true if all branch-0 segments in the chain have PlaybackEnabled == false.
        /// </summary>
        internal static bool IsChainFullyDisabled(string chainId)
        {
            if (string.IsNullOrEmpty(chainId)) return false;
            bool anyBranch0 = false;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (rec.ChainId == chainId && rec.ChainBranch == 0)
                {
                    anyBranch0 = true;
                    if (rec.PlaybackEnabled) return false;
                }
            }
            return anyBranch0; // false if no branch-0 segments found
        }

        /// <summary>
        /// Returns a human-readable phase label like "Kerbin atmo" or "exo".
        /// Returns empty string for untagged/legacy recordings.
        /// </summary>
        internal static string GetSegmentPhaseLabel(Recording rec)
        {
            if (string.IsNullOrEmpty(rec.SegmentPhase)) return "";
            if (!string.IsNullOrEmpty(rec.SegmentBodyName))
                return rec.SegmentBodyName + " " + rec.SegmentPhase;
            return rec.SegmentPhase;
        }

        // ─── Group management helpers ────────────────────────────────────────

        /// <summary>
        /// Returns true if the group name contains characters that would break ConfigNode serialization.
        /// </summary>
        internal static bool IsInvalidGroupName(string name)
        {
            return string.IsNullOrEmpty(name) ||
                   name.Contains("=") || name.Contains("{") || name.Contains("}") ||
                   name.Contains("\n") || name.Contains("\r");
        }

        /// <summary>
        /// Returns distinct group names across all committed recordings.
        /// </summary>
        public static List<string> GetGroupNames()
        {
            var names = new HashSet<string>();
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var groups = committedRecordings[i].RecordingGroups;
                if (groups == null) continue;
                for (int j = 0; j < groups.Count; j++)
                    names.Add(groups[j]);
            }
            var result = new List<string>(names);
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>
        /// Adds a recording to a group. No-op if already a member or index invalid.
        /// </summary>
        public static void AddRecordingToGroup(int index, string groupName)
        {
            if (index < 0 || index >= committedRecordings.Count || IsInvalidGroupName(groupName)) return;
            var rec = committedRecordings[index];
            if (rec.RecordingGroups == null)
                rec.RecordingGroups = new List<string>();
            if (!rec.RecordingGroups.Contains(groupName))
            {
                rec.RecordingGroups.Add(groupName);
                ParsekLog.Info("RecordingStore", $"Recording '{rec.VesselName}' added to group '{groupName}'");
            }
        }

        /// <summary>
        /// Removes a recording from a group. No-op if not a member or index invalid.
        /// </summary>
        public static void RemoveRecordingFromGroup(int index, string groupName)
        {
            if (index < 0 || index >= committedRecordings.Count || string.IsNullOrEmpty(groupName)) return;
            var rec = committedRecordings[index];
            if (rec.RecordingGroups != null && rec.RecordingGroups.Remove(groupName))
            {
                if (rec.RecordingGroups.Count == 0)
                    rec.RecordingGroups = null;
                ParsekLog.Info("RecordingStore", $"Recording '{rec.VesselName}' removed from group '{groupName}'");
            }
        }

        /// <summary>
        /// Returns indices of all recordings in a given chain. Single scan, reusable for batch ops.
        /// </summary>
        public static List<int> GetChainMemberIndices(string chainId)
        {
            var indices = new List<int>();
            if (string.IsNullOrEmpty(chainId)) return indices;
            for (int i = 0; i < committedRecordings.Count; i++)
                if (committedRecordings[i].ChainId == chainId)
                    indices.Add(i);
            return indices;
        }

        /// <summary>
        /// Adds all chain members to a group.
        /// </summary>
        public static void AddChainToGroup(string chainId, string groupName)
        {
            if (string.IsNullOrEmpty(chainId) || IsInvalidGroupName(groupName)) return;
            var members = GetChainMemberIndices(chainId);
            int count = 0;
            for (int i = 0; i < members.Count; i++)
            {
                var rec = committedRecordings[members[i]];
                if (rec.RecordingGroups == null)
                    rec.RecordingGroups = new List<string>();
                if (!rec.RecordingGroups.Contains(groupName))
                {
                    rec.RecordingGroups.Add(groupName);
                    count++;
                }
            }
            if (count > 0)
                ParsekLog.Info("RecordingStore", $"Chain '{chainId}': {count} members added to group '{groupName}'");
        }

        /// <summary>
        /// Removes all chain members from a group.
        /// </summary>
        public static void RemoveChainFromGroup(string chainId, string groupName)
        {
            if (string.IsNullOrEmpty(chainId) || string.IsNullOrEmpty(groupName)) return;
            var members = GetChainMemberIndices(chainId);
            int count = 0;
            for (int i = 0; i < members.Count; i++)
            {
                var rec = committedRecordings[members[i]];
                if (rec.RecordingGroups != null && rec.RecordingGroups.Remove(groupName))
                {
                    if (rec.RecordingGroups.Count == 0)
                        rec.RecordingGroups = null;
                    count++;
                }
            }
            if (count > 0)
                ParsekLog.Info("RecordingStore", $"Chain '{chainId}': {count} members removed from group '{groupName}'");
        }

        /// <summary>
        /// Renames a group across all committed recordings. Returns false if newName already exists.
        /// </summary>
        public static bool RenameGroup(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || oldName == newName)
                return false;

            // Check for collision
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var groups = committedRecordings[i].RecordingGroups;
                if (groups != null && groups.Contains(newName))
                {
                    ParsekLog.Warn("RecordingStore", $"RenameGroup: cannot rename '{oldName}' to '{newName}' — name already exists");
                    return false;
                }
            }

            int updated = 0;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var groups = committedRecordings[i].RecordingGroups;
                if (groups != null)
                {
                    int idx = groups.IndexOf(oldName);
                    if (idx >= 0)
                    {
                        groups[idx] = newName;
                        updated++;
                    }
                }
            }
            ParsekLog.Info("RecordingStore", $"RenameGroup: '{oldName}' → '{newName}' ({updated} recordings updated)");
            return true;
        }

        /// <summary>
        /// Removes a group from all committed recordings' group lists.
        /// Returns the number of recordings that were modified.
        /// </summary>
        public static int RemoveGroupFromAll(string groupName)
        {
            if (string.IsNullOrEmpty(groupName)) return 0;
            int updated = 0;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (rec.RecordingGroups != null && rec.RecordingGroups.Remove(groupName))
                {
                    if (rec.RecordingGroups.Count == 0)
                        rec.RecordingGroups = null;
                    updated++;
                }
            }
            ParsekLog.Info("RecordingStore", $"RemoveGroupFromAll: '{groupName}' removed from {updated} recordings");
            return updated;
        }

        /// <summary>
        /// Replaces a group tag with a parent group tag on all committed recordings.
        /// If parentGroup is null, the group tag is simply removed.
        /// </summary>
        public static int ReplaceGroupOnAll(string groupName, string parentGroup)
        {
            if (string.IsNullOrEmpty(groupName)) return 0;
            int updated = 0;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (rec.RecordingGroups == null) continue;
                int idx = rec.RecordingGroups.IndexOf(groupName);
                if (idx < 0) continue;
                if (parentGroup != null && !rec.RecordingGroups.Contains(parentGroup))
                    rec.RecordingGroups[idx] = parentGroup;
                else
                    rec.RecordingGroups.RemoveAt(idx);
                if (rec.RecordingGroups.Count == 0)
                    rec.RecordingGroups = null;
                updated++;
            }
            string dest = parentGroup ?? "(standalone)";
            ParsekLog.Info("RecordingStore", $"ReplaceGroupOnAll: '{groupName}' → '{dest}' on {updated} recordings");
            return updated;
        }

        /// <summary>
        /// Resets state without Unity logging. For unit tests only.
        /// </summary>
        internal static void ResetForTesting()
        {
            pendingRecording = null;
            committedRecordings.Clear();
            committedTrees.Clear();
            pendingTree = null;
            IsRewinding = false;
            RewindUT = 0;
            RewindAdjustedUT = 0;
            RewindReserved = default(ResourceBudget.BudgetSummary);
            RewindBaselineFunds = 0;
            RewindBaselineScience = 0;
            RewindBaselineRep = 0;
            GameStateRecorder.PendingScienceSubjects.Clear();
        }

        internal static void DeleteRecordingFiles(Recording rec)
        {
            if (rec == null)
            {
                ParsekLog.Verbose("RecordingStore", "DeleteRecordingFiles called with null recording");
                return;
            }
            if (!RecordingPaths.ValidateRecordingId(rec.RecordingId))
            {
                ParsekLog.Warn("RecordingStore", $"DeleteRecordingFiles skipped: invalid recording id '{rec.RecordingId}'");
                return;
            }

            ParsekLog.Verbose("RecordingStore",
                $"DeleteRecordingFiles: id={rec.RecordingId} vessel='{rec.VesselName}' rewindSave={rec.RewindSaveFileName ?? "(none)"}");

            DeleteFileIfExists(RecordingPaths.BuildTrajectoryRelativePath(rec.RecordingId));
            DeleteFileIfExists(RecordingPaths.BuildVesselSnapshotRelativePath(rec.RecordingId));
            DeleteFileIfExists(RecordingPaths.BuildGhostSnapshotRelativePath(rec.RecordingId));
            DeleteFileIfExists(RecordingPaths.BuildGhostGeometryRelativePath(rec.RecordingId));

            if (!string.IsNullOrEmpty(rec.RewindSaveFileName))
                DeleteFileIfExists(RecordingPaths.BuildRewindSaveRelativePath(rec.RewindSaveFileName));
        }

        private static void DeleteFileIfExists(string relativePath)
        {
            try
            {
                string absolutePath = RecordingPaths.ResolveSaveScopedPath(relativePath);
                if (!string.IsNullOrEmpty(absolutePath) && File.Exists(absolutePath))
                {
                    File.Delete(absolutePath);
                    ParsekLog.Verbose("RecordingStore", $"Deleted file: {relativePath}");
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("RecordingStore", $"Failed to delete file '{relativePath}': {ex.Message}");
            }
        }

        /// <summary>
        /// Known sidecar file suffixes for recording files. Used for orphan detection.
        /// </summary>
        private static readonly string[] RecordingFileSuffixes = { ".prec", "_vessel.craft", "_ghost.craft", ".pcrf" };

        /// <summary>
        /// Extracts the recording ID from a sidecar filename by stripping known suffixes.
        /// Returns null if the filename doesn't match any known suffix.
        /// </summary>
        internal static string ExtractRecordingIdFromFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return null;

            for (int i = 0; i < RecordingFileSuffixes.Length; i++)
            {
                if (fileName.EndsWith(RecordingFileSuffixes[i], StringComparison.OrdinalIgnoreCase))
                    return fileName.Substring(0, fileName.Length - RecordingFileSuffixes[i].Length);
            }
            return null;
        }

        /// <summary>
        /// Deletes orphaned sidecar files in the Parsek/Recordings/ directory that don't
        /// correspond to any known recording ID. Called after all recordings and trees are loaded.
        /// </summary>
        internal static void CleanOrphanFiles()
        {
            // Resolve without creating — don't create an empty directory just to scan it
            string root = KSPUtil.ApplicationRootPath ?? "";
            string saveFolder = HighLogic.SaveFolder ?? "";
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveFolder))
            {
                ParsekLog.Verbose("RecordingStore", "CleanOrphanFiles: no save context — skipping");
                return;
            }
            string recordingsDir = Path.GetFullPath(Path.Combine(root, "saves", saveFolder, "Parsek", "Recordings"));
            if (!Directory.Exists(recordingsDir))
            {
                ParsekLog.Verbose("RecordingStore", "CleanOrphanFiles: no recordings directory — skipping");
                return;
            }

            // Build set of known recording IDs from committed recordings + trees.
            // Note: pending recording is not included because this method is called
            // from ParsekScenario.OnLoad before any pending state is created.
            var knownIds = new HashSet<string>();
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                if (!string.IsNullOrEmpty(committedRecordings[i].RecordingId))
                    knownIds.Add(committedRecordings[i].RecordingId);
            }
            for (int t = 0; t < committedTrees.Count; t++)
            {
                foreach (var kvp in committedTrees[t].Recordings)
                {
                    if (!string.IsNullOrEmpty(kvp.Value.RecordingId))
                        knownIds.Add(kvp.Value.RecordingId);
                }
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(recordingsDir);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("RecordingStore", $"CleanOrphanFiles: failed to list directory: {ex.Message}");
                return;
            }

            ParsekLog.Verbose("RecordingStore",
                $"CleanOrphanFiles: scanning {files.Length} file(s) against {knownIds.Count} known recording ID(s)");

            int orphanCount = 0;
            int skippedUnrecognized = 0;
            for (int i = 0; i < files.Length; i++)
            {
                string fileName = Path.GetFileName(files[i]);
                string extractedId = ExtractRecordingIdFromFileName(fileName);
                if (extractedId == null)
                {
                    skippedUnrecognized++;
                    continue; // Not a recognized sidecar file — leave it alone
                }

                if (!knownIds.Contains(extractedId))
                {
                    try
                    {
                        File.Delete(files[i]);
                        orphanCount++;
                        ParsekLog.Verbose("RecordingStore", $"Deleted orphan file: {fileName} (id={extractedId})");
                    }
                    catch (Exception ex)
                    {
                        ParsekLog.Warn("RecordingStore", $"Failed to delete orphan file '{fileName}': {ex.Message}");
                    }
                }
            }

            if (orphanCount > 0)
                ParsekLog.Info("RecordingStore",
                    $"Cleaned {orphanCount} orphaned recording file(s)" +
                    (skippedUnrecognized > 0 ? $", skipped {skippedUnrecognized} unrecognized file(s)" : ""));
            else
                ParsekLog.Verbose("RecordingStore",
                    $"CleanOrphanFiles: no orphans found" +
                    (skippedUnrecognized > 0 ? $", skipped {skippedUnrecognized} unrecognized file(s)" : ""));
        }

        #region Rewind

        /// <summary>
        /// Returns true if the given vessel situation (as int) represents a stable state
        /// suitable for Commit Flight or rewind save capture.
        /// Stable: PRELAUNCH(4), LANDED(1), SPLASHED(2), ORBITING(32).
        /// Unstable: FLYING(8), SUB_ORBITAL(16), ESCAPING(64).
        /// </summary>
        internal static bool IsStableState(int situation)
        {
            switch (situation)
            {
                case 1:  // LANDED
                case 2:  // SPLASHED
                case 4:  // PRELAUNCH
                case 32: // ORBITING
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Resets all playback state on committed recordings (standalone and tree).
        /// Called during rewind to prepare all recordings for fresh replay.
        /// </summary>
        internal static (int standaloneCount, int treeCount) ResetAllPlaybackState()
        {
            int standaloneCount = 0;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                if (committedRecordings[i].TreeId == null)
                    standaloneCount++;
                ResetRecordingPlaybackFields(committedRecordings[i]);
            }

            for (int i = 0; i < committedTrees.Count; i++)
            {
                committedTrees[i].ResourcesApplied = false;
                foreach (var rec in committedTrees[i].Recordings.Values)
                    ResetRecordingPlaybackFields(rec);
            }

            ResourceBudget.Invalidate();

            if (!SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Playback state reset: {standaloneCount} standalone recording(s), {committedTrees.Count} tree(s)");

            return (standaloneCount, committedTrees.Count);
        }

        private static void ResetRecordingPlaybackFields(Recording rec)
        {
            rec.VesselSpawned = false;
            rec.SpawnAttempts = 0;
            rec.SpawnedVesselPersistentId = 0;
            rec.LastAppliedResourceIndex = -1;

            rec.SceneExitSituation = -1;
        }

        /// <summary>
        /// Marks all committed recordings, trees, and milestones as fully applied.
        /// Called after rewind resource adjustment to prevent double-application.
        /// </summary>
        internal static (int recCount, int treeCount) MarkAllFullyApplied()
        {
            int recCount = 0;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                if (committedRecordings[i].Points.Count > 0)
                {
                    committedRecordings[i].LastAppliedResourceIndex = committedRecordings[i].Points.Count - 1;
                    recCount++;
                }
            }

            int treeCount = 0;
            for (int i = 0; i < committedTrees.Count; i++)
            {
                if (!committedTrees[i].ResourcesApplied)
                {
                    committedTrees[i].ResourcesApplied = true;
                    treeCount++;
                }
            }

            var milestones = MilestoneStore.Milestones;
            int mileCount = 0;
            for (int i = 0; i < milestones.Count; i++)
            {
                if (milestones[i].Committed && milestones[i].Events.Count > 0)
                {
                    milestones[i].LastReplayedEventIndex = milestones[i].Events.Count - 1;
                    mileCount++;
                }
            }

            ResourceBudget.Invalidate();

            if (!SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Marked fully applied: {recCount} recording(s), {treeCount} tree(s), {mileCount} milestone(s)");

            return (recCount, treeCount);
        }

        /// <summary>
        /// Counts committed recordings whose StartUT is after the given UT.
        /// Used to display how many future recordings will replay as ghosts after rewind.
        /// </summary>
        internal static int CountFutureRecordings(double ut)
        {
            int count = 0;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                if (committedRecordings[i].StartUT > ut)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Checks whether the player can rewind to a given recording's launch point.
        /// </summary>
        internal static bool CanRewind(Recording rec, out string reason, bool isRecording)
        {
            if (IsRewinding)
            {
                reason = "Rewind already in progress";
                ParsekLog.Verbose("Store", $"CanRewind: blocked — {reason}");
                return false;
            }

            if (string.IsNullOrEmpty(rec.RewindSaveFileName))
            {
                reason = "No rewind save available";
                ParsekLog.Verbose("Store", $"CanRewind: blocked for '{rec.VesselName}' — {reason}");
                return false;
            }

            if (isRecording)
            {
                reason = "Stop recording before rewinding";
                ParsekLog.Verbose("Store", $"CanRewind: blocked — {reason}");
                return false;
            }

            if (HasPending)
            {
                reason = "Merge or discard pending recording first";
                ParsekLog.Verbose("Store", $"CanRewind: blocked — {reason}");
                return false;
            }

            if (HasPendingTree)
            {
                reason = "Merge or discard pending tree first";
                ParsekLog.Verbose("Store", $"CanRewind: blocked — {reason}");
                return false;
            }

            // Verify the save file exists
            string savePath = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildRewindSaveRelativePath(rec.RewindSaveFileName));
            if (string.IsNullOrEmpty(savePath) || !File.Exists(savePath))
            {
                reason = "Rewind save file missing";
                ParsekLog.Verbose("Store", $"CanRewind: blocked for '{rec.VesselName}' — {reason} (path={savePath ?? "null"})");
                return false;
            }

            reason = "";
            return true;
        }

        /// <summary>
        /// Initiates a rewind to the given recording's launch point.
        /// Sets rewind flags, copies the save file to the root saves dir (KSP's LoadGame
        /// doesn't support subdirectory paths), loads it, then deletes the temp copy.
        /// </summary>
        internal static void InitiateRewind(Recording rec)
        {
            IsRewinding = true;
            RewindUT = rec.StartUT;
            RewindReserved = new ResourceBudget.BudgetSummary
            {
                reservedFunds = rec.RewindReservedFunds,
                reservedScience = rec.RewindReservedScience,
                reservedReputation = rec.RewindReservedRep
            };

            // Baseline resources from the recording's pre-launch snapshot.
            // The rewind save was captured at the same moment as PreLaunch values.
            RewindBaselineFunds = rec.PreLaunchFunds;
            RewindBaselineScience = rec.PreLaunchScience;
            RewindBaselineRep = rec.PreLaunchReputation;

            if (!SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Rewind initiated to UT {rec.StartUT} " +
                    $"(save: {rec.RewindSaveFileName}, " +
                    $"baseline: funds={rec.PreLaunchFunds:F1}, sci={rec.PreLaunchScience:F1}, rep={rec.PreLaunchReputation:F1}, " +
                    $"reservedFunds: {rec.RewindReservedFunds:F1}, " +
                    $"reservedScience: {rec.RewindReservedScience:F1}, " +
                    $"reservedRep: {rec.RewindReservedRep:F1})");

            string tempCopyName = null;
            try
            {
                string savesDir = Path.Combine(
                    KSPUtil.ApplicationRootPath ?? "",
                    "saves",
                    HighLogic.SaveFolder ?? "");

                string sourcePath = Path.Combine(savesDir,
                    RecordingPaths.BuildRewindSaveRelativePath(rec.RewindSaveFileName));
                tempCopyName = rec.RewindSaveFileName;
                string tempPath = Path.Combine(savesDir, tempCopyName + ".sfs");

                // Copy from Parsek/Saves/ to root saves dir
                File.Copy(sourcePath, tempPath, true);

                // Pre-process the save file before KSP parses it:
                // 1. Remove recorded vessel + any EVA child vessels (other vessels stay intact)
                // 2. Wind back UT by 10 seconds so the player can reach the pad before launch
                const double rewindLeadTime = 10.0;

                // Collect all vessel names to strip in a single file I/O pass
                var stripNames = new HashSet<string> { rec.VesselName };
                if (!string.IsNullOrEmpty(rec.ChainId))
                {
                    // EVA child recordings have different vessel names (the kerbal's name)
                    // and would otherwise survive the strip
                    foreach (var committed in committedRecordings)
                    {
                        if (committed.ChainId == rec.ChainId &&
                            !string.IsNullOrEmpty(committed.EvaCrewName) &&
                            committed.VesselName != rec.VesselName)
                        {
                            stripNames.Add(committed.VesselName);
                        }
                    }
                    if (stripNames.Count > 1 && !SuppressLogging)
                        ParsekLog.Info("Rewind",
                            $"Rewind strip includes {stripNames.Count - 1} EVA child vessel name(s) from chain '{rec.ChainId}'");
                }
                PreProcessRewindSave(tempPath, stripNames, rewindLeadTime);

                Game game = GamePersistence.LoadGame(tempCopyName, HighLogic.SaveFolder, true, false);

                // Delete the temp copy (file already parsed into Game object)
                try { File.Delete(tempPath); }
                catch { }

                if (game == null)
                {
                    IsRewinding = false;
                    RewindUT = 0;
                    RewindAdjustedUT = 0;
                    RewindReserved = default(ResourceBudget.BudgetSummary);
                    RewindBaselineFunds = 0;
                    RewindBaselineScience = 0;
                    RewindBaselineRep = 0;
                    if (!SuppressLogging)
                        ParsekLog.Error("Rewind",
                            $"Rewind failed: LoadGame returned null for save '{rec.RewindSaveFileName}'. " +
                            $"Flags reset: IsRewinding={IsRewinding}, RewindUT={RewindUT}, RewindAdjustedUT={RewindAdjustedUT}");
                    return;
                }

                // Capture the adjusted UT from the preprocessed save.
                RewindAdjustedUT = game.flightState.universalTime;

                if (!SuppressLogging)
                    ParsekLog.Info("Rewind",
                        $"Rewind: adjustedUT={RewindAdjustedUT:F1}, " +
                        $"rewindUT={RewindUT:F1}, flags=[IsRewinding={IsRewinding}]");

                HighLogic.CurrentGame = game;
                HighLogic.LoadScene(GameScenes.SPACECENTER);

                if (!SuppressLogging)
                    ParsekLog.Info("Rewind",
                        $"Rewind: loading save '{rec.RewindSaveFileName}' into SpaceCenter");
            }
            catch (Exception ex)
            {
                IsRewinding = false;
                RewindUT = 0;
                RewindAdjustedUT = 0;
                RewindReserved = default(ResourceBudget.BudgetSummary);
                RewindBaselineFunds = 0;
                RewindBaselineScience = 0;
                RewindBaselineRep = 0;

                // Clean up temp copy on failure
                if (tempCopyName != null)
                {
                    try
                    {
                        string savesDir = Path.Combine(
                            KSPUtil.ApplicationRootPath ?? "",
                            "saves",
                            HighLogic.SaveFolder ?? "");
                        string tempPath = Path.Combine(savesDir, tempCopyName + ".sfs");
                        if (File.Exists(tempPath))
                            File.Delete(tempPath);
                    }
                    catch { }
                }

                if (!SuppressLogging)
                    ParsekLog.Error("Rewind",
                        $"Rewind failed: {ex.Message}. " +
                        $"Flags reset: IsRewinding={IsRewinding}, RewindUT={RewindUT}, RewindAdjustedUT={RewindAdjustedUT}");
            }
        }

        /// <summary>
        /// Modifies the temp rewind save file before KSP parses it:
        /// removes the recorded vessel and winds back UT so the player
        /// has time to reach the launch pad before the ghost appears.
        /// </summary>
        internal static void PreProcessRewindSave(string sfsPath, string vesselName, double leadTime)
        {
            PreProcessRewindSave(sfsPath, new HashSet<string> { vesselName }, leadTime);
        }

        internal static void PreProcessRewindSave(string sfsPath, HashSet<string> vesselNames, double leadTime)
        {
            ConfigNode root = ConfigNode.Load(sfsPath);
            if (root == null)
                return;

            // The file contents are directly inside the returned node (no GAME wrapper)
            ConfigNode gameNode = root.HasNode("GAME") ? root.GetNode("GAME") : root;

            ConfigNode flightState = gameNode.GetNode("FLIGHTSTATE");
            if (flightState == null)
            {
                if (!SuppressLogging)
                    ParsekLog.Warn("Rewind",
                        $"PreProcessRewindSave: no FLIGHTSTATE node in save '{sfsPath}'");
                root.Save(sfsPath);
                return;
            }
            {
                // Wind back UT
                string utStr = flightState.GetValue("UT");
                double ut;
                if (!string.IsNullOrEmpty(utStr) &&
                    double.TryParse(utStr, NumberStyles.Any, CultureInfo.InvariantCulture, out ut))
                {
                    double newUT = Math.Max(0, ut - leadTime);
                    flightState.SetValue("UT", newUT.ToString("R", CultureInfo.InvariantCulture));
                    if (!SuppressLogging)
                        ParsekLog.Info("Rewind",
                            $"UT adjusted: {ut:F1} → {newUT:F1} (lead time {leadTime}s)");
                }
                else
                {
                    if (!SuppressLogging)
                        ParsekLog.Warn("Rewind",
                            $"PreProcessRewindSave: missing or invalid UT value '{utStr ?? "(null)"}' in FLIGHTSTATE");
                }

                // Remove vessels matching any of the target names
                int removed = 0;
                var vesselNodes = flightState.GetNodes("VESSEL");
                for (int i = vesselNodes.Length - 1; i >= 0; i--)
                {
                    string name = vesselNodes[i].GetValue("name");
                    if (vesselNames.Contains(name))
                    {
                        flightState.RemoveNode(vesselNodes[i]);
                        removed++;
                    }
                }
                if (!SuppressLogging)
                {
                    string namesStr = string.Join(", ", vesselNames);
                    ParsekLog.Info("Rewind",
                        $"Stripped {removed} vessel(s) matching [{namesStr}] from save");
                }
            }

            root.Save(sfsPath);
        }

        #endregion

        #region Trajectory Serialization

        internal static void SerializeTrajectoryInto(ConfigNode targetNode, Recording rec)
        {
            var ic = CultureInfo.InvariantCulture;
            for (int i = 0; i < rec.Points.Count; i++)
            {
                var pt = rec.Points[i];
                ConfigNode ptNode = targetNode.AddNode("POINT");
                ptNode.AddValue("ut", pt.ut.ToString("R", ic));
                ptNode.AddValue("lat", pt.latitude.ToString("R", ic));
                ptNode.AddValue("lon", pt.longitude.ToString("R", ic));
                ptNode.AddValue("alt", pt.altitude.ToString("R", ic));
                ptNode.AddValue("rotX", pt.rotation.x.ToString("R", ic));
                ptNode.AddValue("rotY", pt.rotation.y.ToString("R", ic));
                ptNode.AddValue("rotZ", pt.rotation.z.ToString("R", ic));
                ptNode.AddValue("rotW", pt.rotation.w.ToString("R", ic));
                ptNode.AddValue("body", pt.bodyName);
                ptNode.AddValue("velX", pt.velocity.x.ToString("R", ic));
                ptNode.AddValue("velY", pt.velocity.y.ToString("R", ic));
                ptNode.AddValue("velZ", pt.velocity.z.ToString("R", ic));
                ptNode.AddValue("funds", pt.funds.ToString("R", ic));
                ptNode.AddValue("science", pt.science.ToString("R", ic));
                ptNode.AddValue("rep", pt.reputation.ToString("R", ic));
            }

            for (int s = 0; s < rec.OrbitSegments.Count; s++)
            {
                var seg = rec.OrbitSegments[s];
                ConfigNode segNode = targetNode.AddNode("ORBIT_SEGMENT");
                segNode.AddValue("startUT", seg.startUT.ToString("R", ic));
                segNode.AddValue("endUT", seg.endUT.ToString("R", ic));
                segNode.AddValue("inc", seg.inclination.ToString("R", ic));
                segNode.AddValue("ecc", seg.eccentricity.ToString("R", ic));
                segNode.AddValue("sma", seg.semiMajorAxis.ToString("R", ic));
                segNode.AddValue("lan", seg.longitudeOfAscendingNode.ToString("R", ic));
                segNode.AddValue("argPe", seg.argumentOfPeriapsis.ToString("R", ic));
                segNode.AddValue("mna", seg.meanAnomalyAtEpoch.ToString("R", ic));
                segNode.AddValue("epoch", seg.epoch.ToString("R", ic));
                segNode.AddValue("body", seg.bodyName);
                if (TrajectoryMath.HasOrbitalFrameRotation(seg))
                {
                    segNode.AddValue("ofrX", seg.orbitalFrameRotation.x.ToString("R", ic));
                    segNode.AddValue("ofrY", seg.orbitalFrameRotation.y.ToString("R", ic));
                    segNode.AddValue("ofrZ", seg.orbitalFrameRotation.z.ToString("R", ic));
                    segNode.AddValue("ofrW", seg.orbitalFrameRotation.w.ToString("R", ic));
                }
                if (TrajectoryMath.IsSpinning(seg))
                {
                    segNode.AddValue("avX", seg.angularVelocity.x.ToString("R", ic));
                    segNode.AddValue("avY", seg.angularVelocity.y.ToString("R", ic));
                    segNode.AddValue("avZ", seg.angularVelocity.z.ToString("R", ic));
                }
            }

            for (int pe = 0; pe < rec.PartEvents.Count; pe++)
            {
                var evt = rec.PartEvents[pe];
                ConfigNode evtNode = targetNode.AddNode("PART_EVENT");
                evtNode.AddValue("ut", evt.ut.ToString("R", ic));
                evtNode.AddValue("pid", evt.partPersistentId.ToString(ic));
                evtNode.AddValue("type", ((int)evt.eventType).ToString(ic));
                evtNode.AddValue("part", evt.partName ?? "");
                evtNode.AddValue("value", evt.value.ToString("R", ic));
                evtNode.AddValue("midx", evt.moduleIndex.ToString(ic));
            }

            SerializeSegmentEvents(targetNode, rec.SegmentEvents);

            // Serialize track sections (new recording system)
            if (rec.TrackSections != null && rec.TrackSections.Count > 0)
                SerializeTrackSections(targetNode, rec.TrackSections);
        }

        internal static void DeserializeTrajectoryFrom(ConfigNode sourceNode, Recording rec)
        {
            DeserializePoints(sourceNode, rec);
            DeserializeOrbitSegments(sourceNode, rec);
            DeserializePartEvents(sourceNode, rec);
            DeserializeSegmentEvents(sourceNode, rec.SegmentEvents);
            DeserializeTrackSections(sourceNode, rec.TrackSections);
        }

        /// <summary>
        /// Deserializes POINT nodes from a trajectory ConfigNode into the recording's Points list.
        /// </summary>
        internal static void DeserializePoints(ConfigNode sourceNode, Recording rec)
        {
            var inv = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            ConfigNode[] ptNodes = sourceNode.GetNodes("POINT");
            int parseFailCount = 0;
            for (int i = 0; i < ptNodes.Length; i++)
            {
                var ptNode = ptNodes[i];
                var pt = new TrajectoryPoint();

                bool utOk = double.TryParse(ptNode.GetValue("ut"), inv, ic, out pt.ut);
                double.TryParse(ptNode.GetValue("lat"), inv, ic, out pt.latitude);
                double.TryParse(ptNode.GetValue("lon"), inv, ic, out pt.longitude);
                double.TryParse(ptNode.GetValue("alt"), inv, ic, out pt.altitude);

                if (!utOk)
                    parseFailCount++;

                float rx, ry, rz, rw;
                float.TryParse(ptNode.GetValue("rotX"), inv, ic, out rx);
                float.TryParse(ptNode.GetValue("rotY"), inv, ic, out ry);
                float.TryParse(ptNode.GetValue("rotZ"), inv, ic, out rz);
                float.TryParse(ptNode.GetValue("rotW"), inv, ic, out rw);
                pt.rotation = new Quaternion(rx, ry, rz, rw);

                pt.bodyName = ptNode.GetValue("body") ?? "Kerbin";

                float velX, velY, velZ;
                float.TryParse(ptNode.GetValue("velX"), inv, ic, out velX);
                float.TryParse(ptNode.GetValue("velY"), inv, ic, out velY);
                float.TryParse(ptNode.GetValue("velZ"), inv, ic, out velZ);
                pt.velocity = new Vector3(velX, velY, velZ);

                double funds;
                double.TryParse(ptNode.GetValue("funds"), inv, ic, out funds);
                pt.funds = funds;

                float science, rep;
                float.TryParse(ptNode.GetValue("science"), inv, ic, out science);
                float.TryParse(ptNode.GetValue("rep"), inv, ic, out rep);
                pt.science = science;
                pt.reputation = rep;

                rec.Points.Add(pt);
            }
            if (parseFailCount > 0)
                Log($"[Parsek] WARNING: {parseFailCount}/{ptNodes.Length} trajectory points had unparseable UT in recording {rec.RecordingId}");
        }

        /// <summary>
        /// Deserializes ORBIT_SEGMENT nodes from a trajectory ConfigNode into the recording's OrbitSegments list.
        /// </summary>
        internal static void DeserializeOrbitSegments(ConfigNode sourceNode, Recording rec)
        {
            var inv = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            ConfigNode[] segNodes = sourceNode.GetNodes("ORBIT_SEGMENT");
            for (int s = 0; s < segNodes.Length; s++)
            {
                var segNode = segNodes[s];
                var seg = new OrbitSegment();

                double.TryParse(segNode.GetValue("startUT"), inv, ic, out seg.startUT);
                double.TryParse(segNode.GetValue("endUT"), inv, ic, out seg.endUT);
                double.TryParse(segNode.GetValue("inc"), inv, ic, out seg.inclination);
                double.TryParse(segNode.GetValue("ecc"), inv, ic, out seg.eccentricity);
                double.TryParse(segNode.GetValue("sma"), inv, ic, out seg.semiMajorAxis);
                double.TryParse(segNode.GetValue("lan"), inv, ic, out seg.longitudeOfAscendingNode);
                double.TryParse(segNode.GetValue("argPe"), inv, ic, out seg.argumentOfPeriapsis);
                double.TryParse(segNode.GetValue("mna"), inv, ic, out seg.meanAnomalyAtEpoch);
                double.TryParse(segNode.GetValue("epoch"), inv, ic, out seg.epoch);
                seg.bodyName = segNode.GetValue("body") ?? "Kerbin";

                float ofrX, ofrY, ofrZ, ofrW;
                if (float.TryParse(segNode.GetValue("ofrX"), inv, ic, out ofrX) &&
                    float.TryParse(segNode.GetValue("ofrY"), inv, ic, out ofrY) &&
                    float.TryParse(segNode.GetValue("ofrZ"), inv, ic, out ofrZ) &&
                    float.TryParse(segNode.GetValue("ofrW"), inv, ic, out ofrW))
                {
                    seg.orbitalFrameRotation = new Quaternion(ofrX, ofrY, ofrZ, ofrW);
                }

                float avX, avY, avZ;
                if (float.TryParse(segNode.GetValue("avX"), inv, ic, out avX) &&
                    float.TryParse(segNode.GetValue("avY"), inv, ic, out avY) &&
                    float.TryParse(segNode.GetValue("avZ"), inv, ic, out avZ))
                {
                    seg.angularVelocity = new UnityEngine.Vector3(avX, avY, avZ);
                }

                rec.OrbitSegments.Add(seg);
            }
        }

        /// <summary>
        /// Deserializes PART_EVENT nodes from a trajectory ConfigNode into the recording's PartEvents list.
        /// </summary>
        internal static void DeserializePartEvents(ConfigNode sourceNode, Recording rec)
        {
            var inv = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            ConfigNode[] peNodes = sourceNode.GetNodes("PART_EVENT");
            for (int pe = 0; pe < peNodes.Length; pe++)
            {
                var peNode = peNodes[pe];
                var evt = new PartEvent();

                double.TryParse(peNode.GetValue("ut"), inv, ic, out evt.ut);
                uint pid;
                if (uint.TryParse(peNode.GetValue("pid"), NumberStyles.Integer, ic, out pid))
                    evt.partPersistentId = pid;
                int typeInt;
                if (int.TryParse(peNode.GetValue("type"), NumberStyles.Integer, ic, out typeInt))
                {
                    if (Enum.IsDefined(typeof(PartEventType), typeInt))
                        evt.eventType = (PartEventType)typeInt;
                    else
                    {
                        Log($"[Recording] Skipping unknown PartEvent type={typeInt} in recording {rec.RecordingId}");
                        continue;
                    }
                }
                evt.partName = peNode.GetValue("part") ?? "";

                float val;
                if (float.TryParse(peNode.GetValue("value"), inv, ic, out val))
                    evt.value = val;
                int midx;
                if (int.TryParse(peNode.GetValue("midx"), NumberStyles.Integer, ic, out midx))
                    evt.moduleIndex = midx;

                rec.PartEvents.Add(evt);
            }
        }

        /// <summary>
        /// Serializes SegmentEvent entries as SEGMENT_EVENT child nodes.
        /// </summary>
        internal static void SerializeSegmentEvents(ConfigNode parent, List<SegmentEvent> events)
        {
            if (events == null || events.Count == 0)
            {
                Log("[Recording] SerializeSegmentEvents: 0 segment events");
                return;
            }

            var ic = CultureInfo.InvariantCulture;
            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                ConfigNode evtNode = parent.AddNode("SEGMENT_EVENT");
                evtNode.AddValue("ut", evt.ut.ToString("R", ic));
                evtNode.AddValue("type", ((int)evt.type).ToString(ic));
                if (!string.IsNullOrEmpty(evt.details))
                    evtNode.AddValue("details", evt.details);
            }

            Log($"[Recording] SerializeSegmentEvents: {events.Count} segment events serialized");
        }

        /// <summary>
        /// Deserializes SEGMENT_EVENT child nodes into the given list.
        /// Unknown type values are skipped with a warning log.
        /// Missing ut values cause the event to be skipped.
        /// </summary>
        internal static void DeserializeSegmentEvents(ConfigNode parent, List<SegmentEvent> events)
        {
            ConfigNode[] seNodes = parent.GetNodes("SEGMENT_EVENT");
            if (seNodes.Length == 0)
                return;

            var inv = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;
            int skipped = 0;

            for (int i = 0; i < seNodes.Length; i++)
            {
                var seNode = seNodes[i];

                double ut;
                if (!double.TryParse(seNode.GetValue("ut"), inv, ic, out ut))
                {
                    Log("[Recording] WARNING: Skipping SEGMENT_EVENT with missing or unparseable ut");
                    skipped++;
                    continue;
                }

                int typeInt;
                if (!int.TryParse(seNode.GetValue("type"), NumberStyles.Integer, ic, out typeInt))
                {
                    Log("[Recording] WARNING: Skipping SEGMENT_EVENT with unparseable type");
                    skipped++;
                    continue;
                }
                if (!Enum.IsDefined(typeof(SegmentEventType), typeInt))
                {
                    Log($"[Recording] WARNING: Skipping SEGMENT_EVENT with unknown type={typeInt}");
                    skipped++;
                    continue;
                }

                var evt = new SegmentEvent
                {
                    ut = ut,
                    type = (SegmentEventType)typeInt,
                    details = seNode.GetValue("details")
                };
                events.Add(evt);
            }

            Log($"[Recording] DeserializeSegmentEvents: {events.Count} deserialized, {skipped} skipped (of {seNodes.Length} total)");
        }

        /// <summary>
        /// Serializes TrackSection list into TRACK_SECTION ConfigNodes under the given parent.
        /// Each section carries its own environment classification, reference frame, and nested
        /// trajectory data (POINT nodes for Absolute/Relative, ORBIT_SEGMENT nodes for OrbitalCheckpoint).
        /// </summary>
        internal static void SerializeTrackSections(ConfigNode parent, List<TrackSection> tracks)
        {
            if (tracks == null || tracks.Count == 0)
            {
                ParsekLog.Verbose("RecordingStore", "SerializeTrackSections: 0 track sections to serialize");
                return;
            }

            var ic = CultureInfo.InvariantCulture;

            for (int t = 0; t < tracks.Count; t++)
            {
                var track = tracks[t];
                ConfigNode tsNode = parent.AddNode("TRACK_SECTION");

                tsNode.AddValue("env", ((int)track.environment).ToString(ic));
                tsNode.AddValue("ref", ((int)track.referenceFrame).ToString(ic));
                tsNode.AddValue("startUT", track.startUT.ToString("R", ic));
                tsNode.AddValue("endUT", track.endUT.ToString("R", ic));
                tsNode.AddValue("sampleRate", track.sampleRateHz.ToString("R", ic));

                // Source: sparse — only write when not Active (default)
                if (track.source != TrackSectionSource.Active)
                {
                    tsNode.AddValue("src", ((int)track.source).ToString(ic));
                    ParsekLog.Verbose("RecordingStore",
                        $"SerializeTrackSections: [{t}] writing source={track.source} (non-default)");
                }

                // Boundary discontinuity: sparse — only write when > 0
                if (track.boundaryDiscontinuityMeters > 0f)
                    tsNode.AddValue("bdisc", track.boundaryDiscontinuityMeters.ToString("R", ic));

                if (track.anchorVesselId != 0)
                    tsNode.AddValue("anchorPid", track.anchorVesselId.ToString(ic));

                // Nested trajectory data depends on reference frame
                if (track.referenceFrame == ReferenceFrame.Absolute ||
                    track.referenceFrame == ReferenceFrame.Relative)
                {
                    var frames = track.frames;
                    if (frames != null)
                    {
                        for (int i = 0; i < frames.Count; i++)
                        {
                            var pt = frames[i];
                            ConfigNode ptNode = tsNode.AddNode("POINT");
                            ptNode.AddValue("ut", pt.ut.ToString("R", ic));
                            ptNode.AddValue("lat", pt.latitude.ToString("R", ic));
                            ptNode.AddValue("lon", pt.longitude.ToString("R", ic));
                            ptNode.AddValue("alt", pt.altitude.ToString("R", ic));
                            ptNode.AddValue("rotX", pt.rotation.x.ToString("R", ic));
                            ptNode.AddValue("rotY", pt.rotation.y.ToString("R", ic));
                            ptNode.AddValue("rotZ", pt.rotation.z.ToString("R", ic));
                            ptNode.AddValue("rotW", pt.rotation.w.ToString("R", ic));
                            ptNode.AddValue("body", pt.bodyName);
                            ptNode.AddValue("velX", pt.velocity.x.ToString("R", ic));
                            ptNode.AddValue("velY", pt.velocity.y.ToString("R", ic));
                            ptNode.AddValue("velZ", pt.velocity.z.ToString("R", ic));
                            ptNode.AddValue("funds", pt.funds.ToString("R", ic));
                            ptNode.AddValue("science", pt.science.ToString("R", ic));
                            ptNode.AddValue("rep", pt.reputation.ToString("R", ic));
                        }
                    }
                }
                else if (track.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
                {
                    var checkpoints = track.checkpoints;
                    if (checkpoints != null)
                    {
                        for (int s = 0; s < checkpoints.Count; s++)
                        {
                            var seg = checkpoints[s];
                            ConfigNode segNode = tsNode.AddNode("ORBIT_SEGMENT");
                            segNode.AddValue("startUT", seg.startUT.ToString("R", ic));
                            segNode.AddValue("endUT", seg.endUT.ToString("R", ic));
                            segNode.AddValue("inc", seg.inclination.ToString("R", ic));
                            segNode.AddValue("ecc", seg.eccentricity.ToString("R", ic));
                            segNode.AddValue("sma", seg.semiMajorAxis.ToString("R", ic));
                            segNode.AddValue("lan", seg.longitudeOfAscendingNode.ToString("R", ic));
                            segNode.AddValue("argPe", seg.argumentOfPeriapsis.ToString("R", ic));
                            segNode.AddValue("mna", seg.meanAnomalyAtEpoch.ToString("R", ic));
                            segNode.AddValue("epoch", seg.epoch.ToString("R", ic));
                            segNode.AddValue("body", seg.bodyName);
                            if (TrajectoryMath.HasOrbitalFrameRotation(seg))
                            {
                                segNode.AddValue("ofrX", seg.orbitalFrameRotation.x.ToString("R", ic));
                                segNode.AddValue("ofrY", seg.orbitalFrameRotation.y.ToString("R", ic));
                                segNode.AddValue("ofrZ", seg.orbitalFrameRotation.z.ToString("R", ic));
                                segNode.AddValue("ofrW", seg.orbitalFrameRotation.w.ToString("R", ic));
                            }
                            if (TrajectoryMath.IsSpinning(seg))
                            {
                                segNode.AddValue("avX", seg.angularVelocity.x.ToString("R", ic));
                                segNode.AddValue("avY", seg.angularVelocity.y.ToString("R", ic));
                                segNode.AddValue("avZ", seg.angularVelocity.z.ToString("R", ic));
                            }
                        }
                    }
                }

                int frameCount = track.frames?.Count ?? 0;
                int checkpointCount = track.checkpoints?.Count ?? 0;
                ParsekLog.Verbose("RecordingStore",
                    $"SerializeTrackSections: [{t}] env={track.environment} ref={track.referenceFrame} " +
                    $"frames={frameCount} checkpoints={checkpointCount}");
            }

            ParsekLog.Info("RecordingStore",
                $"SerializeTrackSections: serialized {tracks.Count} track section(s)");
        }

        /// <summary>
        /// Deserializes TRACK_SECTION ConfigNodes from the given parent into the tracks list.
        /// Unknown environment or reference frame values cause the entire section to be skipped
        /// with a warning (forward tolerance for future enum additions).
        /// </summary>
        internal static void DeserializeTrackSections(ConfigNode parent, List<TrackSection> tracks)
        {
            var inv = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            ConfigNode[] tsNodes = parent.GetNodes("TRACK_SECTION");
            if (tsNodes.Length == 0)
            {
                ParsekLog.Verbose("RecordingStore", "DeserializeTrackSections: no TRACK_SECTION nodes found");
                return;
            }

            for (int t = 0; t < tsNodes.Length; t++)
            {
                var tsNode = tsNodes[t];
                var section = new TrackSection();

                // Parse environment enum (skip section if unknown)
                int envInt;
                if (!int.TryParse(tsNode.GetValue("env"), NumberStyles.Integer, ic, out envInt))
                {
                    ParsekLog.Warn("RecordingStore",
                        $"DeserializeTrackSections: section [{t}] has unparseable env — skipping");
                    continue;
                }
                if (!Enum.IsDefined(typeof(SegmentEnvironment), envInt))
                {
                    ParsekLog.Warn("RecordingStore",
                        $"DeserializeTrackSections: section [{t}] has unknown env={envInt} — skipping");
                    continue;
                }
                section.environment = (SegmentEnvironment)envInt;

                // Parse reference frame enum (skip section if unknown)
                int refInt;
                if (!int.TryParse(tsNode.GetValue("ref"), NumberStyles.Integer, ic, out refInt))
                {
                    ParsekLog.Warn("RecordingStore",
                        $"DeserializeTrackSections: section [{t}] has unparseable ref — skipping");
                    continue;
                }
                if (!Enum.IsDefined(typeof(ReferenceFrame), refInt))
                {
                    ParsekLog.Warn("RecordingStore",
                        $"DeserializeTrackSections: section [{t}] has unknown ref={refInt} — skipping");
                    continue;
                }
                section.referenceFrame = (ReferenceFrame)refInt;

                // Parse scalar fields
                double.TryParse(tsNode.GetValue("startUT"), inv, ic, out section.startUT);
                double.TryParse(tsNode.GetValue("endUT"), inv, ic, out section.endUT);
                float.TryParse(tsNode.GetValue("sampleRate"), inv, ic, out section.sampleRateHz);

                // Source: defaults to Active (0) when absent — backward compatible
                string srcStr = tsNode.GetValue("src");
                if (srcStr != null)
                {
                    int srcInt;
                    if (int.TryParse(srcStr, NumberStyles.Integer, ic, out srcInt))
                    {
                        if (Enum.IsDefined(typeof(TrackSectionSource), srcInt))
                        {
                            section.source = (TrackSectionSource)srcInt;
                            ParsekLog.Verbose("RecordingStore",
                                $"DeserializeTrackSections: [{t}] loaded source={section.source}");
                        }
                        else
                        {
                            ParsekLog.Warn("RecordingStore",
                                $"DeserializeTrackSections: [{t}] unknown TrackSectionSource value={srcInt}, defaulting to Active");
                        }
                    }
                }
                // else: absent key — defaults to Active (struct default = 0)

                // Boundary discontinuity: defaults to 0 when absent — backward compatible
                float bdisc;
                if (float.TryParse(tsNode.GetValue("bdisc"), inv, ic, out bdisc))
                    section.boundaryDiscontinuityMeters = bdisc;

                uint anchorPid;
                if (uint.TryParse(tsNode.GetValue("anchorPid"), NumberStyles.Integer, ic, out anchorPid))
                    section.anchorVesselId = anchorPid;

                // Parse nested trajectory data based on reference frame
                if (section.referenceFrame == ReferenceFrame.Absolute ||
                    section.referenceFrame == ReferenceFrame.Relative)
                {
                    section.frames = new List<TrajectoryPoint>();
                    ConfigNode[] ptNodes = tsNode.GetNodes("POINT");
                    for (int i = 0; i < ptNodes.Length; i++)
                    {
                        var ptNode = ptNodes[i];
                        var pt = new TrajectoryPoint();

                        double.TryParse(ptNode.GetValue("ut"), inv, ic, out pt.ut);
                        double.TryParse(ptNode.GetValue("lat"), inv, ic, out pt.latitude);
                        double.TryParse(ptNode.GetValue("lon"), inv, ic, out pt.longitude);
                        double.TryParse(ptNode.GetValue("alt"), inv, ic, out pt.altitude);

                        float rx, ry, rz, rw;
                        float.TryParse(ptNode.GetValue("rotX"), inv, ic, out rx);
                        float.TryParse(ptNode.GetValue("rotY"), inv, ic, out ry);
                        float.TryParse(ptNode.GetValue("rotZ"), inv, ic, out rz);
                        float.TryParse(ptNode.GetValue("rotW"), inv, ic, out rw);
                        pt.rotation = new Quaternion(rx, ry, rz, rw);

                        pt.bodyName = ptNode.GetValue("body") ?? "Kerbin";

                        float velX, velY, velZ;
                        float.TryParse(ptNode.GetValue("velX"), inv, ic, out velX);
                        float.TryParse(ptNode.GetValue("velY"), inv, ic, out velY);
                        float.TryParse(ptNode.GetValue("velZ"), inv, ic, out velZ);
                        pt.velocity = new Vector3(velX, velY, velZ);

                        double funds;
                        double.TryParse(ptNode.GetValue("funds"), inv, ic, out funds);
                        pt.funds = funds;

                        float science, rep;
                        float.TryParse(ptNode.GetValue("science"), inv, ic, out science);
                        float.TryParse(ptNode.GetValue("rep"), inv, ic, out rep);
                        pt.science = science;
                        pt.reputation = rep;

                        section.frames.Add(pt);
                    }
                }
                else if (section.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
                {
                    section.checkpoints = new List<OrbitSegment>();
                    ConfigNode[] segNodes = tsNode.GetNodes("ORBIT_SEGMENT");
                    for (int s = 0; s < segNodes.Length; s++)
                    {
                        var segNode = segNodes[s];
                        var seg = new OrbitSegment();

                        double.TryParse(segNode.GetValue("startUT"), inv, ic, out seg.startUT);
                        double.TryParse(segNode.GetValue("endUT"), inv, ic, out seg.endUT);
                        double.TryParse(segNode.GetValue("inc"), inv, ic, out seg.inclination);
                        double.TryParse(segNode.GetValue("ecc"), inv, ic, out seg.eccentricity);
                        double.TryParse(segNode.GetValue("sma"), inv, ic, out seg.semiMajorAxis);
                        double.TryParse(segNode.GetValue("lan"), inv, ic, out seg.longitudeOfAscendingNode);
                        double.TryParse(segNode.GetValue("argPe"), inv, ic, out seg.argumentOfPeriapsis);
                        double.TryParse(segNode.GetValue("mna"), inv, ic, out seg.meanAnomalyAtEpoch);
                        double.TryParse(segNode.GetValue("epoch"), inv, ic, out seg.epoch);
                        seg.bodyName = segNode.GetValue("body") ?? "Kerbin";

                        float ofrX, ofrY, ofrZ, ofrW;
                        if (float.TryParse(segNode.GetValue("ofrX"), inv, ic, out ofrX) &&
                            float.TryParse(segNode.GetValue("ofrY"), inv, ic, out ofrY) &&
                            float.TryParse(segNode.GetValue("ofrZ"), inv, ic, out ofrZ) &&
                            float.TryParse(segNode.GetValue("ofrW"), inv, ic, out ofrW))
                        {
                            seg.orbitalFrameRotation = new Quaternion(ofrX, ofrY, ofrZ, ofrW);
                        }

                        float avX, avY, avZ;
                        if (float.TryParse(segNode.GetValue("avX"), inv, ic, out avX) &&
                            float.TryParse(segNode.GetValue("avY"), inv, ic, out avY) &&
                            float.TryParse(segNode.GetValue("avZ"), inv, ic, out avZ))
                        {
                            seg.angularVelocity = new Vector3(avX, avY, avZ);
                        }

                        section.checkpoints.Add(seg);
                    }
                }

                // Initialize null lists to empty for frames that don't have nested data
                if (section.frames == null)
                    section.frames = new List<TrajectoryPoint>();
                if (section.checkpoints == null)
                    section.checkpoints = new List<OrbitSegment>();

                tracks.Add(section);

                int frameCount = section.frames.Count;
                int checkpointCount = section.checkpoints.Count;
                ParsekLog.Verbose("RecordingStore",
                    $"DeserializeTrackSections: [{t}] env={section.environment} ref={section.referenceFrame} " +
                    $"frames={frameCount} checkpoints={checkpointCount}");
            }

            ParsekLog.Info("RecordingStore",
                $"DeserializeTrackSections: deserialized {tracks.Count} track section(s) from {tsNodes.Length} node(s)");
        }

        #endregion

        #region Recording File I/O

        internal static bool SaveRecordingFiles(Recording rec)
        {
            if (rec == null)
            {
                Log("[Parsek] WARNING: SaveRecordingFiles called with null recording");
                return false;
            }
            if (!RecordingPaths.ValidateRecordingId(rec.RecordingId))
            {
                Log($"[Parsek] WARNING: SaveRecordingFiles rejected invalid recording id '{rec.RecordingId}'");
                return false;
            }

            try
            {
                string dir = RecordingPaths.EnsureRecordingsDirectory();
                if (dir == null)
                {
                    Log($"[Parsek] WARNING: SaveRecordingFiles could not resolve recordings directory for {rec.RecordingId}");
                    return false;
                }

                // Save .prec trajectory file
                var precNode = new ConfigNode("PARSEK_RECORDING");
                precNode.AddValue("version", rec.RecordingFormatVersion);
                precNode.AddValue("recordingId", rec.RecordingId);
                SerializeTrajectoryInto(precNode, rec);

                string precPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildTrajectoryRelativePath(rec.RecordingId));
                if (string.IsNullOrEmpty(precPath))
                {
                    Log($"[Parsek] WARNING: SaveRecordingFiles could not resolve trajectory path for {rec.RecordingId}");
                    return false;
                }
                SafeWriteConfigNode(precNode, precPath);

                // Save _vessel.craft (always rewrite — snapshot can be mutated by spawn offset)
                string vesselPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildVesselSnapshotRelativePath(rec.RecordingId));
                if (string.IsNullOrEmpty(vesselPath))
                {
                    Log($"[Parsek] WARNING: SaveRecordingFiles could not resolve vessel snapshot path for {rec.RecordingId}");
                    return false;
                }
                if (rec.VesselSnapshot != null)
                {
                    SafeWriteConfigNode(rec.VesselSnapshot, vesselPath);
                }
                else if (File.Exists(vesselPath))
                {
                    try
                    {
                        File.Delete(vesselPath);
                    }
                    catch (Exception ex)
                    {
                        Log($"[Parsek] WARNING: Failed deleting stale vessel snapshot '{vesselPath}': {ex.Message}");
                    }
                }

                // Save _ghost.craft (write once — immutable after creation)
                if (rec.GhostVisualSnapshot != null)
                {
                    string ghostPath = RecordingPaths.ResolveSaveScopedPath(
                        RecordingPaths.BuildGhostSnapshotRelativePath(rec.RecordingId));
                    if (string.IsNullOrEmpty(ghostPath))
                    {
                        Log($"[Parsek] WARNING: SaveRecordingFiles could not resolve ghost snapshot path for {rec.RecordingId}");
                    }
                    else if (!File.Exists(ghostPath))
                        SafeWriteConfigNode(rec.GhostVisualSnapshot, ghostPath);
                }

                Log($"[Parsek] Saved recording files for {rec.RecordingId}: points={rec.Points.Count}, " +
                    $"orbitSegments={rec.OrbitSegments.Count}, partEvents={rec.PartEvents.Count}, " +
                    $"hasVesselSnapshot={rec.VesselSnapshot != null}, hasGhostSnapshot={rec.GhostVisualSnapshot != null}");

                return true;
            }
            catch (Exception ex)
            {
                Log($"[Parsek] Failed to save recording files for {rec.RecordingId}: {ex.Message}");
                return false;
            }
        }

        internal static bool LoadRecordingFiles(Recording rec)
        {
            if (rec == null)
            {
                Log("[Parsek] WARNING: LoadRecordingFiles called with null recording");
                return false;
            }
            if (!RecordingPaths.ValidateRecordingId(rec.RecordingId))
            {
                Log($"[Parsek] WARNING: LoadRecordingFiles rejected invalid recording id '{rec.RecordingId}'");
                return false;
            }

            try
            {
                // Load .prec trajectory file
                // ConfigNode.Save writes the node's contents (values + children),
                // and ConfigNode.Load returns a node containing those contents directly.
                string precPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildTrajectoryRelativePath(rec.RecordingId));
                if (string.IsNullOrEmpty(precPath) || !File.Exists(precPath))
                {
                    Log($"[Parsek] Trajectory file missing for {rec.RecordingId} — recording degraded (0 points)");
                    return false;
                }

                var precNode = ConfigNode.Load(precPath);
                if (precNode == null)
                {
                    Log($"[Parsek] Invalid trajectory file for {rec.RecordingId} — failed to parse");
                    return false;
                }

                // Validate recordingId inside file matches
                string fileId = precNode.GetValue("recordingId");
                if (fileId != null && fileId != rec.RecordingId)
                {
                    Log($"[Parsek] Recording ID mismatch in {rec.RecordingId}.prec: file says '{fileId}' — skipping");
                    return false;
                }

                // Sync format version from .prec file (authoritative for data format).
                // Prevents double-migration when .sfs metadata is stale (e.g., quicksave
                // made before an in-flight v4→v5 migration updated the persistent save).
                SyncVersionFromPrecFile(precNode, rec);

                DeserializeTrajectoryFrom(precNode, rec);

                // Load _vessel.craft — ConfigNode.Load returns the snapshot directly
                string vesselPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildVesselSnapshotRelativePath(rec.RecordingId));
                if (!string.IsNullOrEmpty(vesselPath) && File.Exists(vesselPath))
                {
                    var vesselNode = ConfigNode.Load(vesselPath);
                    if (vesselNode != null)
                        rec.VesselSnapshot = vesselNode;
                }

                // Load _ghost.craft
                string ghostPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildGhostSnapshotRelativePath(rec.RecordingId));
                if (!string.IsNullOrEmpty(ghostPath) && File.Exists(ghostPath))
                {
                    var ghostNode = ConfigNode.Load(ghostPath);
                    if (ghostNode != null)
                        rec.GhostVisualSnapshot = ghostNode;
                }

                // Backward compat: if no ghost snapshot, fall back to vessel snapshot
                if (rec.GhostVisualSnapshot == null && rec.VesselSnapshot != null)
                    rec.GhostVisualSnapshot = rec.VesselSnapshot.CreateCopy();

                Log($"[Parsek] Loaded recording files for {rec.RecordingId}: points={rec.Points.Count}, " +
                    $"orbitSegments={rec.OrbitSegments.Count}, partEvents={rec.PartEvents.Count}, " +
                    $"hasVesselSnapshot={rec.VesselSnapshot != null}, hasGhostSnapshot={rec.GhostVisualSnapshot != null}");

                return true;
            }
            catch (Exception ex)
            {
                Log($"[Parsek] Failed to load recording files for {rec.RecordingId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Syncs RecordingFormatVersion from the .prec file's version field.
        /// The .prec file is authoritative for data format; if its version is higher
        /// than the .sfs metadata, the recording's version is updated to match.
        /// </summary>
        internal static void SyncVersionFromPrecFile(ConfigNode precNode, Recording rec)
        {
            string fileVersion = precNode.GetValue("version");
            if (fileVersion == null) return;

            int precVersion;
            if (int.TryParse(fileVersion, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out precVersion)
                && precVersion > rec.RecordingFormatVersion)
            {
                Log($"Version sync: {rec.RecordingId} .sfs says v{rec.RecordingFormatVersion} but .prec says v{precVersion} — updating");
                rec.RecordingFormatVersion = precVersion;
            }
        }

        /// <summary>
        /// Migrates a v4 recording's rotation data from world-space to surface-relative (v5).
        /// Must be called when CelestialBody data is available (flight scene).
        /// KSP's world frame co-rotates with the parent body, so bodyTransform.rotation
        /// represents axial tilt only (time-invariant). Migration is simply
        /// surfaceRelRot = Inverse(bodyRot) * worldRot — no time delta needed.
        /// </summary>
        internal static bool MigrateV4ToV5(Recording rec)
        {
            if (rec == null || rec.RecordingFormatVersion >= 5 || rec.Points.Count == 0)
                return false;

            string lastBody = null;
            CelestialBody body = null;
            Quaternion invBodyRot = Quaternion.identity;

            int converted = 0;
            for (int i = 0; i < rec.Points.Count; i++)
            {
                var pt = rec.Points[i];

                // Cache body lookup
                if (pt.bodyName != lastBody)
                {
                    body = FlightGlobals.Bodies.Find(b => b.name == pt.bodyName);
                    lastBody = pt.bodyName;
                    if (body != null)
                    {
                        // KSP's world frame co-rotates with the body surface.
                        // body.bodyTransform.rotation represents axial tilt only,
                        // NOT spin — so it's essentially constant over time.
                        // surfaceRelRot = Inverse(bodyRot) * worldRot. No time delta needed.
                        invBodyRot = Quaternion.Inverse(body.bodyTransform.rotation);
                    }
                }

                if (body == null) continue;

                pt.rotation = invBodyRot * pt.rotation;
                rec.Points[i] = pt;
                converted++;
            }

            if (converted == 0) return false;

            rec.RecordingFormatVersion = 5;

            // Save the migrated .prec file
            bool saved = SaveRecordingFiles(rec);
            Log($"Migrated recording {rec.RecordingId} from v4→v5: {converted} points converted, saved={saved}");
            return saved;
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
    }
}
