using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// State of the pending tree slot in <see cref="RecordingStore"/>.
    /// Determines how <see cref="ParsekScenario.OnLoad"/> dispatches the pending tree
    /// after revert detection runs.
    /// </summary>
    public enum PendingTreeState
    {
        /// <summary>
        /// Tree has been through <c>FinalizeTreeRecordings</c>: terminal state set,
        /// terminal orbit / position captured, snapshots preserved for the merge dialog.
        /// Legacy behavior — this is what <see cref="RecordingStore.StashPendingTree"/>
        /// produced before the quickload-resume redesign.
        /// </summary>
        Finalized = 0,

        /// <summary>
        /// Tree is still "in-flight": recorder field references were torn down because
        /// the scene is unloading, but no terminal state was set. OnLoad decides based
        /// on revert detection whether to finalize-then-commit (true revert) or
        /// restore-and-resume (quickload / vessel switch).
        /// See <c>docs/dev/plans/quickload-resume-recording.md</c>.
        /// </summary>
        Limbo = 1,

        /// <summary>
        /// Tree was pre-transitioned for a vessel switch that triggers a FLIGHT→FLIGHT
        /// scene reload (e.g. clicking an unloaded vessel in the tracking station).
        /// At stash time the recorder was flushed, the previous active recording's
        /// vessel PID was inserted into <c>BackgroundMap</c>, and
        /// <c>ActiveRecordingId</c> was nulled — exactly the state the in-session
        /// <c>OnVesselSwitchComplete</c> path produces. The OnLoad dispatch routes
        /// this state through the dedicated vessel-switch restore coroutine, which
        /// reinstalls the tree and either promotes the new active vessel from
        /// <c>BackgroundMap</c> (round-trip) or leaves the recorder null (outsider).
        /// See bug #266 in <c>docs/dev/todo-and-known-bugs.md</c>.
        /// </summary>
        LimboVesselSwitch = 2,
    }

    /// <summary>
    /// Static holder for recording data that survives scene changes.
    /// Static fields persist across scene loads within a KSP session.
    /// Save/load persistence is handled separately by ParsekScenario.
    /// </summary>
    public static class RecordingStore
    {
        public const int CurrentRecordingFormatVersion = 0;
        // v0: initial release format

        // When true, suppresses logging calls (for unit testing outside Unity)
        internal static bool SuppressLogging;

        // PID of the active vessel at scene entry. Used by SpawnVesselOrChainTip to
        // bypass PID dedup statelessly — if a recording's VesselPersistentId matches
        // this, the existing real vessel is the player's reverted/active vessel, not
        // a previously-spawned endpoint. Static so it survives Recording object recreation.
        internal static uint SceneEntryActiveVesselPid;

        // Rewind state is now encapsulated in RewindContext.
        // These delegate properties preserve API compatibility for callers
        // that read IsRewinding/RewindUT/etc. through RecordingStore.
        internal static bool IsRewinding => RewindContext.IsRewinding;
        internal static double RewindUT => RewindContext.RewindUT;
        internal static double RewindAdjustedUT => RewindContext.RewindAdjustedUT;
        internal static BudgetSummary RewindReserved => RewindContext.RewindReserved;
        internal static double RewindBaselineFunds => RewindContext.RewindBaselineFunds;
        internal static double RewindBaselineScience => RewindContext.RewindBaselineScience;
        internal static float RewindBaselineRep => RewindContext.RewindBaselineRep;

        // True while the deferred UT adjustment coroutine hasn't run yet.
        // KSC spawn must not use Planetarium.GetUniversalTime() while this is set —
        // it still reflects the pre-rewind future UT until the coroutine fires.
        internal static bool RewindUTAdjustmentPending;

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

        // Set true by StashPendingTree during OnSceneChangeRequested.
        // Checked by ParsekScenario.OnLoad to distinguish a freshly-stashed pending
        // (from the current revert — should show dialog) from a stale pending left
        // over from a previous flight (should be discarded per #64).
        internal static bool PendingStashedThisTransition;

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
        // State of the pending tree slot. Finalized = legacy behavior (tree has terminal
        // state, snapshots, ready to commit). Limbo = new quickload-resume path: tree is
        // still "in-flight", untriaged, waiting for OnLoad to decide revert-vs-quickload.
        // See docs/dev/plans/quickload-resume-recording.md.
        private static PendingTreeState pendingTreeState = PendingTreeState.Finalized;

        public static IReadOnlyList<Recording> CommittedRecordings => committedRecordings;
        public static List<RecordingTree> CommittedTrees => committedTrees;
        public static bool HasPendingTree => pendingTree != null;
        public static RecordingTree PendingTree => pendingTree;
        public static PendingTreeState PendingTreeStateValue => pendingTreeState;

        /// <summary>
        /// Creates a Recording from raw flight data, trimming leading stationary points
        /// and retiming events. Returns null if too short (fewer than 2 points after trim).
        /// This is the factory replacement for StashPending — it builds the Recording
        /// without storing it in any pending slot.
        /// </summary>
        public static Recording CreateRecordingFromFlightData(
            List<TrajectoryPoint> points, string vesselName,
            List<OrbitSegment> orbitSegments = null,
            string recordingId = null,
            int? recordingFormatVersion = null,
            List<PartEvent> partEvents = null,
            List<FlagEvent> flagEvents = null,
            List<SegmentEvent> segmentEvents = null,
            List<TrackSection> trackSections = null)
        {
            if (points == null || points.Count < 2)
            {
                Log($"[Parsek] Recording too short for '{vesselName}' ({points?.Count ?? 0} points, need >= 2) — discarded");
                return null;
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
                    return null;
                }
                // Remove orbit segments that end before the new start
                if (orbitSegments != null)
                    orbitSegments.RemoveAll(s => s.endUT <= trimUT);
                // Retime part events from the trimmed window to the new start
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
                // Retime flag events the same way
                if (flagEvents != null)
                {
                    for (int i = 0; i < flagEvents.Count; i++)
                    {
                        if (flagEvents[i].ut < trimUT)
                        {
                            var e = flagEvents[i];
                            e.ut = trimUT;
                            flagEvents[i] = e;
                        }
                    }
                }
            }

            var rec = new Recording
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
                FlagEvents = flagEvents != null
                    ? new List<FlagEvent>(flagEvents)
                    : new List<FlagEvent>(),
                SegmentEvents = segmentEvents != null
                    ? new List<SegmentEvent>(segmentEvents)
                    : new List<SegmentEvent>(),
                TrackSections = trackSections != null
                    ? new List<TrackSection>(trackSections)
                    : new List<TrackSection>(),
                VesselName = vesselName
            };

            Log($"[Parsek] Created recording: {points.Count} points, " +
                $"{rec.OrbitSegments.Count} orbit segments from {vesselName}");
            ParsekLog.Verbose("RecordingStore", $"CreateRecordingFromFlightData: {rec.DebugName}");
            return rec;
        }

        /// <summary>
        /// Commits an already-built Recording to the flat committed list.
        /// Replaces the StashPending/CommitPending two-step — no pending slot involved.
        /// Sets FilesDirty, adds to committedRecordings, flushes to disk, commits science,
        /// captures baseline, and creates a milestone.
        /// </summary>
        public static void CommitRecordingDirect(Recording rec)
        {
            if (rec == null)
            {
                ParsekLog.Warn("RecordingStore", "CommitRecordingDirect called with null recording");
                return;
            }

            rec.FilesDirty = true;
            committedRecordings.Add(rec);
            Log($"[Parsek] Committed recording from {rec.VesselName} " +
                $"({rec.Points.Count} points). Total committed: {committedRecordings.Count}");
            ParsekLog.Verbose("RecordingStore", $"CommitRecordingDirect: {rec.DebugName}");

            // Flush to disk immediately to close the crash window.
            FlushDirtyFiles(committedRecordings);

            // Commit pending science subjects before clearing
            GameStateStore.CommitScienceSubjects(GameStateRecorder.PendingScienceSubjects);
            GameStateRecorder.PendingScienceSubjects.Clear();

            // Capture a game state baseline at each commit (single funnel point)
            GameStateStore.CaptureBaselineIfNeeded();

            // Create a milestone bundling game state events since the previous milestone
            MilestoneStore.CreateMilestone(rec.RecordingId, rec.EndUT);
        }

        /// <summary>
        /// Adds a recording to the internal committed list without flushing or side effects.
        /// For production code paths (e.g., undock continuation) that need direct list access
        /// after CommittedRecordings became IReadOnlyList.
        /// </summary>
        internal static void AddCommittedInternal(Recording rec)
        {
            committedRecordings.Add(rec);
        }

        /// <summary>
        /// Removes a recording from the internal committed list.
        /// For production code that needs mutation after CommittedRecordings became IReadOnlyList.
        /// </summary>
        internal static bool RemoveCommittedInternal(Recording rec)
        {
            return committedRecordings.Remove(rec);
        }

        /// <summary>
        /// Clears all recordings from the internal committed list.
        /// For production code that needs mutation after CommittedRecordings became IReadOnlyList.
        /// </summary>
        internal static void ClearCommittedInternal()
        {
            committedRecordings.Clear();
        }

        public static void ClearCommitted()
        {
            int count = committedRecordings.Count;
            for (int i = 0; i < committedRecordings.Count; i++)
                DeleteRecordingFiles(committedRecordings[i]);
            committedRecordings.Clear();
            committedTrees.Clear();
            GameStateRecorder.PendingScienceSubjects.Clear();
            Log($"[Parsek] Cleared {count} committed recordings and all trees");
        }

        public static void Clear()
        {
            pendingTree = null;
            pendingTreeState = PendingTreeState.Finalized;
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

            ApplySessionMergeToRecordings(tree);
            AutoGroupTreeRecordings(tree);
            AdoptOrphanedRecordingsIntoTreeGroup(tree);
            FinalizeTreeCommit(tree);
        }

        /// <summary>
        /// Merges overlapping data sources into the tree's recordings in-place.
        /// Strategy: start from the original recording (preserves ALL fields by default),
        /// then overwrite only the merge-produced fields.
        /// </summary>
        private static void ApplySessionMergeToRecordings(RecordingTree tree)
        {
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
        }

        /// <summary>
        /// Auto-groups tree recordings under a unique group name.
        /// Debris recordings get a "Debris" subgroup under the main group.
        /// Uses GenerateUniqueGroupName to avoid merging multiple launches of the same vessel
        /// into one group (bug #104).
        /// </summary>
        private static void AutoGroupTreeRecordings(RecordingTree tree)
        {
            if (string.IsNullOrEmpty(tree.TreeName) || tree.Recordings.Count <= 1)
                return;

            string groupName = GenerateUniqueGroupName(tree.TreeName);
            int debrisCount = 0;
            string debrisGroupName = null;

            foreach (var rec in tree.Recordings.Values)
            {
                if (rec.IsDebris)
                {
                    // Create debris subgroup on first debris recording
                    if (debrisGroupName == null)
                    {
                        debrisGroupName = groupName + " / Debris";
                        GroupHierarchyStore.SetGroupParent(debrisGroupName, groupName);
                    }
                    if (rec.RecordingGroups == null)
                        rec.RecordingGroups = new List<string>();
                    if (!rec.RecordingGroups.Contains(debrisGroupName))
                        rec.RecordingGroups.Add(debrisGroupName);
                    debrisCount++;
                }
                else
                {
                    if (rec.RecordingGroups == null)
                        rec.RecordingGroups = new List<string>();
                    if (!rec.RecordingGroups.Contains(groupName))
                        rec.RecordingGroups.Add(groupName);
                }
            }

            int stageCount = tree.Recordings.Count - debrisCount;
            if (debrisCount > 0)
                ParsekLog.Info("RecordingStore",
                    $"Auto-grouped {stageCount} stage(s) under '{groupName}', {debrisCount} debris under '{debrisGroupName}'");
            else
                ParsekLog.Info("RecordingStore",
                    $"Auto-grouped {tree.Recordings.Count} recordings under '{groupName}'");
        }

        /// <summary>
        /// Adopts orphaned committed recordings that belong to this tree but were
        /// committed as standalone before the tree (e.g., split segments committed
        /// via deferred merge dialog before the tree revert/commit).
        /// Matches by TreeId or by vessel PID + overlapping time range.
        /// </summary>
        private static void AdoptOrphanedRecordingsIntoTreeGroup(RecordingTree tree)
        {
            if (string.IsNullOrEmpty(tree.TreeName))
                return;

            string adoptGroupName = null;
            foreach (var rec in tree.Recordings.Values)
            {
                if (!rec.IsDebris && rec.RecordingGroups != null && rec.RecordingGroups.Count > 0)
                {
                    adoptGroupName = rec.RecordingGroups[0];
                    break;
                }
            }

            if (adoptGroupName == null)
                return;

            // Collect tree vessel PIDs and time range for matching
            var treePids = new HashSet<uint>();
            double treeStartUT = double.MaxValue, treeEndUT = 0;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec.VesselPersistentId != 0)
                    treePids.Add(rec.VesselPersistentId);
                if (rec.StartUT < treeStartUT) treeStartUT = rec.StartUT;
                if (rec.EndUT > treeEndUT) treeEndUT = rec.EndUT;
            }

            int adopted = 0;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var cr = committedRecordings[i];
                if (cr.RecordingGroups != null && cr.RecordingGroups.Count > 0) continue;

                bool match = cr.TreeId == tree.Id;
                if (!match && treePids.Contains(cr.VesselPersistentId)
                    && cr.StartUT >= treeStartUT - 60 && cr.EndUT <= treeEndUT + 60)
                    match = true;

                if (!match) continue;

                string targetGroup = cr.IsDebris
                    ? adoptGroupName + " / Debris"
                    : adoptGroupName;
                // Ensure debris subgroup has parent relationship
                if (cr.IsDebris)
                    GroupHierarchyStore.SetGroupParent(targetGroup, adoptGroupName);
                cr.RecordingGroups = new List<string> { targetGroup };
                adopted++;
                ParsekLog.Info("RecordingStore",
                    $"Adopted orphaned recording '{cr.VesselName}' (id={cr.RecordingId}) into group '{targetGroup}'");
            }
            if (adopted > 0)
                ParsekLog.Info("RecordingStore",
                    $"Adopted {adopted} orphaned recording(s) into tree group '{adoptGroupName}'");
        }

        /// <summary>
        /// Adds tree recordings to committed list, flushes to disk, rebuilds background map,
        /// commits science subjects, captures baseline, and creates a milestone.
        /// </summary>
        private static void FinalizeTreeCommit(RecordingTree tree)
        {
            // Add all tree recordings to committedRecordings (enables ghost playback).
            // Skip recordings already present (chain segments committed mid-flight
            // by CommitRecordingDirect).
            foreach (var rec in tree.Recordings.Values)
            {
                rec.FilesDirty = true;
                if (committedRecordings.Contains(rec)) continue;
                committedRecordings.Add(rec);
            }

            // Flush to disk immediately to close the crash window.
            // If RunOptimizationPass runs after this, it will re-dirty modified
            // recordings and flush again with the final optimized state.
            FlushDirtyFiles(committedRecordings);

            // Ensure OwnedVesselPids is populated (covers runtime-created trees
            // that never went through RecordingTree.Load)
            tree.RebuildBackgroundMap();

            committedTrees.Add(tree);

            // Commit pending science subjects before clearing
            GameStateStore.CommitScienceSubjects(GameStateRecorder.PendingScienceSubjects);
            GameStateRecorder.PendingScienceSubjects.Clear();

            Log($"[Parsek] Committed tree '{tree.TreeName}' ({tree.Recordings.Count} recordings). " +
                $"Total committed: {committedRecordings.Count} recordings, {committedTrees.Count} trees");
            foreach (var rec in tree.Recordings.Values)
                ParsekLog.Verbose("RecordingStore", $"CommitTree: child {rec.DebugName}");

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
        /// Stashes a tree as pending with <see cref="PendingTreeState.Finalized"/>.
        /// Legacy behavior — the tree has already been through
        /// <c>FinalizeTreeRecordings</c> and is ready for the merge dialog or auto-commit.
        /// </summary>
        public static void StashPendingTree(RecordingTree tree)
            => StashPendingTree(tree, PendingTreeState.Finalized);

        /// <summary>
        /// Stashes a tree as pending with an explicit state.
        /// <see cref="PendingTreeState.Finalized"/> is the legacy path (terminal state set,
        /// snapshots preserved). <see cref="PendingTreeState.Limbo"/> is the quickload-resume
        /// path — tree is still in-flight, recorder was torn down without finalization, and
        /// OnLoad will decide whether to restore-and-resume or finalize-then-commit.
        /// </summary>
        public static void StashPendingTree(RecordingTree tree, PendingTreeState state)
        {
            if (pendingTree != null)
            {
                ParsekLog.Warn("RecordingStore",
                    $"StashPendingTree({state}): overwriting existing pending tree " +
                    $"'{pendingTree.TreeName}' (state={pendingTreeState}) with '{tree?.TreeName ?? "<null>"}'");
            }
            pendingTree = tree;
            pendingTreeState = state;
            if (tree != null)
            {
                PendingStashedThisTransition = true;
                Log($"[Parsek] Stashed pending tree '{tree.TreeName}' ({tree.Recordings.Count} recordings, state={state})");
                Recording activeStashRec = null;
                if (!string.IsNullOrEmpty(tree.ActiveRecordingId))
                    tree.Recordings.TryGetValue(tree.ActiveRecordingId, out activeStashRec);
                if (activeStashRec != null)
                    ParsekLog.Verbose("RecordingStore",
                        $"StashPendingTree: active {activeStashRec.DebugName}");
            }
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
            pendingTreeState = PendingTreeState.Finalized;
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
            Log($"[Parsek] Discarded pending tree '{pendingTree.TreeName}' (state={pendingTreeState})");
            pendingTree = null;
            pendingTreeState = PendingTreeState.Finalized;
        }

        /// <summary>
        /// Marks the pending tree's state as Finalized after the revert-detection dispatch
        /// has run FinalizeTreeRecordings on a previously-Limbo tree. Called by
        /// ParsekScenario.OnLoad on the Limbo + isRevert path before the auto-commit /
        /// merge dialog flow runs.
        /// </summary>
        internal static void MarkPendingTreeFinalized()
        {
            if (pendingTree == null) return;
            if (pendingTreeState == PendingTreeState.Finalized) return;
            pendingTreeState = PendingTreeState.Finalized;
            ParsekLog.Info("RecordingStore",
                $"Pending tree '{pendingTree.TreeName}' transitioned Limbo → Finalized");
        }

        /// <summary>
        /// Removes the pending tree from the slot WITHOUT deleting its sidecar files or
        /// clearing pending science subjects. Returns the tree so the caller can take
        /// ownership (typically to re-install as the active tree during quickload-resume).
        /// Unlike <see cref="DiscardPendingTree"/>, this is non-destructive — the caller
        /// is responsible for the tree's lifetime after popping.
        /// </summary>
        internal static RecordingTree PopPendingTree()
        {
            var tree = pendingTree;
            pendingTree = null;
            pendingTreeState = PendingTreeState.Finalized;
            if (tree != null)
            {
                Log($"[Parsek] Popped pending tree '{tree.TreeName}' (caller takes ownership, files preserved)");
            }
            return tree;
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
            CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
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

        // ─── Recording optimization ─────────────────────────────────────────

        /// <summary>
        /// Runs the optimization pass: find merge candidates among committed recordings,
        /// execute merges, re-index chains, then split multi-environment recordings at
        /// environment boundaries. Called on save load after migrations.
        /// </summary>
        internal static void RunOptimizationPass()
        {
            var recordings = committedRecordings;
            if (recordings == null || recordings.Count == 0)
            {
                ParsekLog.Verbose("RecordingStore", "Optimization pass: skipped (no recordings)");
                return;
            }

            int mergeCount = 0;
            const int maxMergesPerPass = 50;
            // Iterate merge passes until no more candidates (merging may create new adjacent pairs)
            bool changed = true;
            while (changed && mergeCount < maxMergesPerPass)
            {
                changed = false;
                var candidates = RecordingOptimizer.FindMergeCandidates(recordings);
                if (candidates.Count == 0) break;

                // Process first candidate only per pass (indices shift after removal)
                var (idxA, idxB) = candidates[0];
                var target = recordings[idxA];
                var absorbed = recordings[idxB];

                string absorbedId = RecordingOptimizer.MergeInto(target, absorbed);
                target.FilesDirty = true;
                string chainId = target.ChainId;

                // Remove absorbed recording from committed list
                recordings.RemoveAt(idxB);

                // Delete absorbed recording's sidecar files
                try { DeleteRecordingFiles(absorbed); }
                catch (System.Exception ex)
                {
                    ParsekLog.Warn("RecordingStore",
                        $"Optimization: failed to delete files for merged recording {absorbedId}: {ex.Message}");
                }

                // Re-index the chain
                if (!string.IsNullOrEmpty(chainId))
                    RecordingOptimizer.ReindexChain(recordings, chainId);

                mergeCount++;
                changed = true;
            }

            if (mergeCount >= maxMergesPerPass)
                ParsekLog.Warn("RecordingStore",
                    $"Optimization pass: hit merge cap ({maxMergesPerPass}), some candidates may remain");
            else if (mergeCount > 0)
                ParsekLog.Info("RecordingStore", $"Optimization pass: merged {mergeCount} segment pair(s)");
            else
                ParsekLog.Verbose("RecordingStore", "Optimization pass: no merge candidates found");

            // Split pass: break multi-environment recordings at environment boundaries.
            // Each split produces two recordings sharing a ChainId for UI grouping.
            // Uses CanAutoSplitIgnoringGhostTriggers — ghosting triggers don't block
            // optimizer splits because both halves inherit the GhostVisualSnapshot and
            // part events are correctly partitioned by SplitAtSection.
            int splitCount = 0;
            const int maxSplitsPerPass = 50;
            bool splitChanged = true;
            while (splitChanged && splitCount < maxSplitsPerPass)
            {
                splitChanged = false;
                var splitCandidates = RecordingOptimizer.FindSplitCandidatesForOptimizer(recordings);
                if (splitCandidates.Count == 0) break;

                var (recIdx, secIdx) = splitCandidates[0];
                var original = recordings[recIdx];

                var second = RecordingOptimizer.SplitAtSection(original, secIdx);

                // Assign identity
                second.RecordingId = Guid.NewGuid().ToString("N");
                if (string.IsNullOrEmpty(original.ChainId))
                    original.ChainId = Guid.NewGuid().ToString("N");
                second.ChainId = original.ChainId;
                second.TreeId = original.TreeId;
                second.VesselName = original.VesselName;
                second.VesselPersistentId = original.VesselPersistentId;
                second.PreLaunchFunds = original.PreLaunchFunds;
                second.PreLaunchScience = original.PreLaunchScience;
                second.PreLaunchReputation = original.PreLaunchReputation;
                second.RecordingGroups = original.RecordingGroups != null
                    ? new List<string>(original.RecordingGroups) : null;

                // Derive SegmentBodyName from trajectory points
                if (original.Points != null && original.Points.Count > 0)
                    original.SegmentBodyName = original.Points[0].bodyName;
                if (second.Points != null && second.Points.Count > 0)
                    second.SegmentBodyName = second.Points[0].bodyName;

                // BranchPoint linkage: ChildBranchPointId moves to last half.
                // This is safe because ChildBranchPointId is always set at recording
                // termination (CreateSplitBranch sets it at branchUT), so any optimizer
                // environment split is at an internal boundary before branchUT.
                //
                // NOTE: The parent BranchPoint's ChildRecordingIds still references
                // original.RecordingId (now the first chain segment). This is correct —
                // the first segment IS the direct child of that BP. The chain linkage
                // (shared ChainId) connects it to subsequent segments. Code that walks
                // from a BranchPoint to the chain tip must follow ChainId, not just
                // ChildRecordingIds.
                second.ChildBranchPointId = original.ChildBranchPointId;
                original.ChildBranchPointId = null;
                // Do NOT set second.ParentRecordingId — that field is for EVA linkage only

                // Update BranchPoint.ParentRecordingIds when ChildBranchPointId moves to new half
                if (!string.IsNullOrEmpty(second.ChildBranchPointId) && !string.IsNullOrEmpty(original.TreeId))
                {
                    for (int t = 0; t < committedTrees.Count; t++)
                    {
                        if (committedTrees[t].Id != original.TreeId) continue;
                        var tree = committedTrees[t];
                        if (tree.BranchPoints != null)
                        {
                            for (int b = 0; b < tree.BranchPoints.Count; b++)
                            {
                                if (tree.BranchPoints[b].Id == second.ChildBranchPointId
                                    && tree.BranchPoints[b].ParentRecordingIds != null)
                                {
                                    var parentIds = tree.BranchPoints[b].ParentRecordingIds;
                                    for (int p = 0; p < parentIds.Count; p++)
                                    {
                                        if (parentIds[p] == original.RecordingId)
                                        {
                                            parentIds[p] = second.RecordingId;
                                            ParsekLog.Verbose("RecordingStore",
                                                $"Split: updated BranchPoint '{second.ChildBranchPointId}' " +
                                                $"ParentRecordingIds: {original.RecordingId} → {second.RecordingId}");
                                            break;
                                        }
                                    }
                                    break;
                                }
                            }
                        }
                        break;
                    }
                }

                // Add to committed recordings (after original)
                recordings.Insert(recIdx + 1, second);

                // Update tree dict if applicable
                if (!string.IsNullOrEmpty(original.TreeId))
                {
                    for (int t = 0; t < committedTrees.Count; t++)
                    {
                        if (committedTrees[t].Id == original.TreeId)
                        {
                            committedTrees[t].Recordings[second.RecordingId] = second;
                            break;
                        }
                    }
                }

                original.FilesDirty = true;
                second.FilesDirty = true;

                // Reindex chain by StartUT
                RecordingOptimizer.ReindexChain(recordings, original.ChainId);

                splitCount++;
                splitChanged = true;
                ParsekLog.Info("RecordingStore",
                    $"Split recording '{original.VesselName}' at section {secIdx}" +
                    (!string.IsNullOrEmpty(original.TreeId) ? $" (tree={original.TreeId})" : "") +
                    $": '{original.SegmentPhase ?? "?"}' [{original.StartUT:F0}..{original.EndUT:F0}] + " +
                    $"'{second.SegmentPhase ?? "?"}' [{second.StartUT:F0}..{second.EndUT:F0}]");
            }

            if (splitCount >= maxSplitsPerPass)
                ParsekLog.Warn("RecordingStore",
                    $"Optimization pass: hit split cap ({maxSplitsPerPass}), some candidates may remain");
            else if (splitCount > 0)
                ParsekLog.Info("RecordingStore", $"Optimization pass: split {splitCount} recording(s)");

            // Boring tail trim pass: remove trailing idle tails from leaf recordings
            // so the real vessel spawns promptly instead of waiting through minutes of
            // ghost sitting motionless on the surface or coasting in orbit.
            // ORDERING: after splits (which may create new leaf recordings) and before
            // PopulateLoopSyncParentIndices (which uses list indices).
            int trimCount = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                if (RecordingOptimizer.TrimBoringTail(recordings[i], recordings))
                {
                    recordings[i].FilesDirty = true;
                    trimCount++;
                }
            }
            if (trimCount > 0)
                ParsekLog.Info("RecordingStore",
                    $"Optimization pass: trimmed boring tails from {trimCount} recording(s)");

            // Loop sync pass: link debris recordings to their parent recording
            // so debris ghosts replay in sync with the parent's loop cycle.
            PopulateLoopSyncParentIndices(recordings);

            // Rebuild BackgroundMap for trees that had structural changes (splits/merges).
            // BackgroundMap is a runtime-only field mapping PID → RecordingId; splits create
            // new recordings and merges remove them, invalidating the map.
            if (mergeCount > 0 || splitCount > 0)
            {
                for (int t = 0; t < committedTrees.Count; t++)
                    committedTrees[t].RebuildBackgroundMap();
            }

            // Flush all dirty recordings to disk so the crash window after
            // commit+optimize is closed (data no longer lives only in RAM).
            FlushDirtyFiles(recordings);
        }

        /// <summary>
        /// Saves all dirty recordings to disk immediately. Called after commit and
        /// after the optimization pass to close the crash window where data exists
        /// only in RAM. Failures are logged but non-fatal — OnSave will retry.
        /// </summary>
        private static void FlushDirtyFiles(List<Recording> recordings)
        {
            int saved = 0, failed = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                if (!recordings[i].FilesDirty) continue;
                if (SaveRecordingFiles(recordings[i]))
                    saved++;
                else
                    failed++;
            }
            if (saved > 0 || failed > 0)
                ParsekLog.Info("RecordingStore",
                    $"FlushDirtyFiles: saved {saved}, failed {failed}");
        }

        /// <summary>
        /// Test seam for #292: lets unit tests intercept GamePersistence.SaveGame calls
        /// without invoking the real KSP API. Defaults to the real call. Tests reset to
        /// null in Dispose to restore default behavior.
        /// </summary>
        internal static System.Func<string, string, SaveMode, string> SaveGameForTesting;

        /// <summary>
        /// Bug #292: refresh quicksave.sfs after a user-initiated tree merge so subsequent
        /// F9 quickloads see the merge result. Without this, F9 loads a stale quicksave
        /// from before the merge and silently drops the new recording IDs the merge added.
        ///
        /// MUST be called only from user-initiated merge entry points (currently:
        /// MergeDialog.cs "Merge to Timeline" button handler), NEVER from inside
        /// RunOptimizationPass or any OnLoad path — KSP's SaveGame triggers OnSave on
        /// every ScenarioModule, which would re-enter Parsek's OnSave during OnLoad and
        /// risk corrupting scenario state.
        ///
        /// Failure modes are non-fatal: if SaveGame returns null or throws, log a warning
        /// and continue. The merge still completes; the player loses only the quicksave
        /// refresh benefit until they manually F5 again.
        /// </summary>
        internal static void RefreshQuicksaveAfterMerge(string reason, int recordingCount)
        {
            var saveFn = SaveGameForTesting ?? GamePersistence.SaveGame;

            // Defense-in-depth: skip during loading scenes (no game state to save).
            // Tests bypass these guards by setting SaveGameForTesting.
            if (SaveGameForTesting == null)
            {
                if (HighLogic.LoadedScene == GameScenes.LOADING)
                {
                    ParsekLog.Verbose("Quicksave",
                        $"Refresh skipped (LOADING scene): reason={reason}");
                    return;
                }
                if (HighLogic.CurrentGame == null)
                {
                    ParsekLog.Verbose("Quicksave",
                        $"Refresh skipped (CurrentGame is null): reason={reason}");
                    return;
                }
            }

            try
            {
                string result = saveFn("quicksave", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                if (string.IsNullOrEmpty(result))
                {
                    ParsekLog.Warn("Quicksave",
                        $"GamePersistence.SaveGame returned null after {reason} — quicksave NOT refreshed");
                    return;
                }
                ParsekLog.Info("Quicksave",
                    $"Refreshed quicksave.sfs after {reason} (tree has {recordingCount} recording IDs)");
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn("Quicksave",
                    $"Exception refreshing quicksave after {reason}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// For each debris recording in a tree, finds the non-debris recording whose
        /// UT range covers the debris's StartUT and sets LoopSyncParentIdx to its
        /// committed index. This enables the engine to replay debris ghosts on the
        /// parent's loop clock.
        ///
        /// ORDERING: Must run AFTER all optimizer splits are complete — indices are
        /// into the final committed recordings list and would be stale if splits
        /// happened afterward.
        ///
        /// When the optimizer splits a parent, the split boundary point appears in
        /// both halves. The first match is used — both segments belong to the same
        /// vessel and loop with the same cycle, so either is correct.
        /// </summary>
        internal static void PopulateLoopSyncParentIndices(List<Recording> recordings)
        {
            if (recordings == null) return;

            int linked = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (!rec.IsDebris || string.IsNullOrEmpty(rec.TreeId))
                {
                    rec.LoopSyncParentIdx = -1;
                    continue;
                }

                // Find the non-debris recording in the same tree whose UT range covers this debris's start
                double debrisStart = rec.StartUT;
                int parentIdx = -1;
                for (int j = 0; j < recordings.Count; j++)
                {
                    if (j == i) continue;
                    var candidate = recordings[j];
                    if (candidate.IsDebris) continue;
                    if (candidate.TreeId != rec.TreeId) continue;
                    if (candidate.VesselPersistentId != rec.VesselPersistentId
                        && debrisStart >= candidate.StartUT && debrisStart <= candidate.EndUT)
                    {
                        parentIdx = j;
                        break;
                    }
                }

                rec.LoopSyncParentIdx = parentIdx;
                if (parentIdx >= 0) linked++;
            }

            if (linked > 0)
                ParsekLog.Info("RecordingStore",
                    $"Loop sync: linked {linked} debris recording(s) to parent recordings");
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
        /// Returns a group name that doesn't collide with existing group names.
        /// If baseName is not already used, returns it unchanged.
        /// Otherwise appends " (2)", " (3)", etc. until a unique name is found.
        /// </summary>
        internal static string GenerateUniqueGroupName(string baseName)
        {
            if (string.IsNullOrEmpty(baseName))
            {
                ParsekLog.Verbose("RecordingStore",
                    "GenerateUniqueGroupName: baseName is null/empty, returning 'Chain'");
                baseName = "Chain";
            }

            var existing = new HashSet<string>(GetGroupNames(), System.StringComparer.OrdinalIgnoreCase);
            if (!existing.Contains(baseName))
            {
                ParsekLog.Verbose("RecordingStore",
                    $"GenerateUniqueGroupName: '{baseName}' is unique, returning unchanged");
                return baseName;
            }

            for (int n = 2; n < 1000; n++)
            {
                string candidate = $"{baseName} ({n})";
                if (!existing.Contains(candidate))
                {
                    ParsekLog.Info("RecordingStore",
                        $"GenerateUniqueGroupName: '{baseName}' already exists, using '{candidate}'");
                    return candidate;
                }
            }

            // Safety fallback — should never happen in practice
            string fallback = $"{baseName} ({Guid.NewGuid().ToString("N").Substring(0, 6)})";
            ParsekLog.Warn("RecordingStore",
                $"GenerateUniqueGroupName: exhausted 999 candidates for '{baseName}', using fallback '{fallback}'");
            return fallback;
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
            committedRecordings.Clear();
            committedTrees.Clear();
            pendingTree = null;
            pendingTreeState = PendingTreeState.Finalized;
            SceneEntryActiveVesselPid = 0;
            RewindContext.ResetForTesting();
            RewindUTAdjustmentPending = false;
            GameStateRecorder.PendingScienceSubjects.Clear();
            PendingCleanupPids = null;
            PendingCleanupNames = null;
            PendingStashedThisTransition = false;
        }

        /// <summary>
        /// Adds a recording to the committed list with tree ownership enforced.
        /// If the recording has no TreeId, wraps it in a single-node RecordingTree.
        /// For unit tests only.
        /// </summary>
        internal static void AddRecordingWithTreeForTesting(Recording rec, string treeName = null)
        {
            if (rec.TreeId == null)
            {
                var tree = new RecordingTree
                {
                    Id = Guid.NewGuid().ToString("N"),
                    TreeName = treeName ?? rec.VesselName ?? "TestTree",
                    RootRecordingId = rec.RecordingId
                };
                rec.TreeId = tree.Id;
                tree.Recordings[rec.RecordingId] = rec;
                committedTrees.Add(tree);
            }
            committedRecordings.Add(rec);
        }

        /// <summary>
        /// Adds a tree directly to committed trees. For unit tests only.
        /// </summary>
        internal static void AddCommittedTreeForTesting(RecordingTree tree)
        {
            committedTrees.Add(tree);
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
        private static readonly string[] RecordingFileSuffixes = { ".prec", "_vessel.craft", "_ghost.craft" };

        /// <summary>
        /// Suffixes for recording files written by previous Parsek versions but no longer
        /// used. CleanOrphanFiles deletes any of these unconditionally — they are by
        /// definition stale (the format that wrote them no longer exists).
        /// </summary>
        private static readonly string[] LegacyRecordingFileSuffixes = { ".pcrf" };

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
        /// True if <paramref name="fileName"/> matches a legacy sidecar suffix that no
        /// longer corresponds to live Parsek code. Pure helper for orphan-cleanup
        /// (#260 follow-up — old saves can have .pcrf files left over from the dead
        /// ghost-geometry scaffolding; they have no current consumer and should be
        /// removed unconditionally rather than left as "unrecognized" forever).
        /// </summary>
        internal static bool IsLegacySidecarFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return false;
            for (int i = 0; i < LegacyRecordingFileSuffixes.Length; i++)
            {
                if (fileName.EndsWith(LegacyRecordingFileSuffixes[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
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
            int legacyCount = 0;
            int skippedUnrecognized = 0;
            for (int i = 0; i < files.Length; i++)
            {
                string fileName = Path.GetFileName(files[i]);
                string extractedId = ExtractRecordingIdFromFileName(fileName);
                if (extractedId == null)
                {
                    // Legacy sidecars (e.g. .pcrf from the removed ghost-geometry scaffolding,
                    // #260) have no current consumer — delete unconditionally.
                    if (IsLegacySidecarFile(fileName))
                    {
                        try
                        {
                            File.Delete(files[i]);
                            legacyCount++;
                            ParsekLog.Verbose("RecordingStore", $"Deleted legacy sidecar file: {fileName}");
                        }
                        catch (Exception ex)
                        {
                            ParsekLog.Warn("RecordingStore", $"Failed to delete legacy sidecar file '{fileName}': {ex.Message}");
                        }
                        continue;
                    }
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

            if (orphanCount > 0 || legacyCount > 0)
                ParsekLog.Info("RecordingStore",
                    $"Cleaned {orphanCount} orphaned recording file(s)" +
                    (legacyCount > 0 ? $", {legacyCount} legacy sidecar file(s)" : "") +
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
        /// Resets all playback state on committed recordings.
        /// Called during rewind to prepare all recordings for fresh replay.
        /// </summary>
        internal static (int recordingCount, int treeCount) ResetAllPlaybackState()
        {
            for (int i = 0; i < committedRecordings.Count; i++)
                ResetRecordingPlaybackFields(committedRecordings[i]);

            for (int i = 0; i < committedTrees.Count; i++)
            {
                committedTrees[i].ResourcesApplied = false;
                foreach (var rec in committedTrees[i].Recordings.Values)
                    ResetRecordingPlaybackFields(rec);
            }

            if (!SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Playback state reset: {committedRecordings.Count} recording(s), {committedTrees.Count} tree(s)");

            return (committedRecordings.Count, committedTrees.Count);
        }

        /// <summary>
        /// Rolls back continuation data appended after commit (bug #95).
        /// If a continuation boundary is set, truncates Points back to the boundary,
        /// restores pre-continuation snapshots, and marks file dirty. Called from all
        /// revert/rewind paths (ResetRecordingPlaybackFields, RestoreStandaloneMutableState,
        /// tree recording reset loop).
        /// </summary>
        internal static void RollbackContinuationData(Recording rec)
        {
            if (rec.ContinuationBoundaryIndex >= 0)
            {
                // Truncate continuation points (if any were added)
                if (rec.ContinuationBoundaryIndex < rec.Points.Count)
                {
                    int removeCount = rec.Points.Count - rec.ContinuationBoundaryIndex;
                    rec.Points.RemoveRange(rec.ContinuationBoundaryIndex, removeCount);
                    rec.FilesDirty = true;
                    if (!SuppressLogging)
                        ParsekLog.Verbose("Rewind",
                            $"Rolled back {removeCount} continuation point(s) for '{rec.VesselName}' " +
                            $"(boundary={rec.ContinuationBoundaryIndex}, id={rec.RecordingId})");
                }

                // Restore pre-continuation snapshots (may have been overwritten
                // by RefreshContinuationSnapshotCore even without new points)
                if (rec.PreContinuationVesselSnapshot != null)
                    rec.VesselSnapshot = rec.PreContinuationVesselSnapshot;
                if (rec.PreContinuationGhostSnapshot != null)
                    rec.GhostVisualSnapshot = rec.PreContinuationGhostSnapshot;
            }
            rec.ContinuationBoundaryIndex = -1;
            rec.PreContinuationVesselSnapshot = null;
            rec.PreContinuationGhostSnapshot = null;
        }

        private static void ResetRecordingPlaybackFields(Recording rec)
        {
            RollbackContinuationData(rec);

            // If the vessel had spawned, any terminal state change (Recovered/Destroyed)
            // was on the spawned real vessel, not the recording. Clear it so the recording
            // can spawn again after revert/rewind.
            if (rec.VesselSpawned && rec.TerminalStateValue.HasValue)
            {
                var ts = rec.TerminalStateValue.Value;
                if (ts == TerminalState.Recovered || ts == TerminalState.Destroyed)
                {
                    if (!SuppressLogging)
                        ParsekLog.Verbose("Rewind",
                            $"Clearing post-spawn terminal state {ts} for '{rec.VesselName}' (id={rec.RecordingId})");
                    rec.TerminalStateValue = null;
                }
            }

            rec.VesselSpawned = false;
            rec.VesselDestroyed = false;
            rec.SpawnAttempts = 0;
            rec.SpawnDeathCount = 0;
            rec.SpawnedVesselPersistentId = 0;
            rec.CollisionBlockCount = 0;
            rec.SpawnAbandoned = false;
            rec.WalkbackExhausted = false;
            rec.DuplicateBlockerRecovered = false;
            rec.LastAppliedResourceIndex = -1;

            rec.SceneExitSituation = -1;
        }

        /// <summary>
        /// Collects SpawnedVesselPersistentId values and vessel names from all committed
        /// recordings (standalone + tree) that currently have a spawned vessel.
        /// Must be called BEFORE ResetAllPlaybackState or RestoreStandaloneMutableState
        /// zeroes the PIDs.
        /// </summary>
        internal static (HashSet<uint> pids, HashSet<string> names) CollectSpawnedVesselInfo()
        {
            var pids = new HashSet<uint>();
            var names = new HashSet<string>();

            for (int i = 0; i < committedRecordings.Count; i++)
            {
                uint pid = committedRecordings[i].SpawnedVesselPersistentId;
                if (pid != 0)
                {
                    pids.Add(pid);
                    if (!string.IsNullOrEmpty(committedRecordings[i].VesselName))
                        names.Add(committedRecordings[i].VesselName);
                }
            }

            for (int i = 0; i < committedTrees.Count; i++)
            {
                foreach (var rec in committedTrees[i].Recordings.Values)
                {
                    if (rec.SpawnedVesselPersistentId != 0)
                    {
                        pids.Add(rec.SpawnedVesselPersistentId);
                        if (!string.IsNullOrEmpty(rec.VesselName))
                            names.Add(rec.VesselName);
                    }
                }
            }

            if (!SuppressLogging && pids.Count > 0)
                ParsekLog.Info("RecordingStore",
                    $"CollectSpawnedVesselInfo: {pids.Count} PID(s), {names.Count} name(s)");

            return (pids, names);
        }

        // One-shot cleanup data: populated in ParsekScenario.OnLoad revert/rewind path
        // (before spawn state reset), consumed and cleared in ParsekFlight.OnFlightReady.
        internal static HashSet<uint> PendingCleanupPids { get; set; }
        internal static HashSet<string> PendingCleanupNames { get; set; }

        // Destination scene from last OnSceneChangeRequested — consumed in OnLoad (#88)
        internal static GameScenes? PendingDestinationScene { get; set; }

        // Delegate property for RewindQuicksaveVesselPids — state now lives in RewindContext.
        internal static HashSet<uint> RewindQuicksaveVesselPids => RewindContext.RewindQuicksaveVesselPids;

        /// <summary>
        /// Collects vessel names from ALL committed recordings (regardless of spawn state).
        /// Used during rewind to strip every vessel matching a recording name from flightState —
        /// on rewind, any such vessel is from the future and incompatible with the rewound state.
        /// </summary>
        internal static HashSet<string> CollectAllRecordingVesselNames()
        {
            var names = new HashSet<string>();
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                if (!string.IsNullOrEmpty(committedRecordings[i].VesselName))
                    names.Add(committedRecordings[i].VesselName);
            }
            for (int i = 0; i < committedTrees.Count; i++)
            {
                foreach (var rec in committedTrees[i].Recordings.Values)
                {
                    if (!string.IsNullOrEmpty(rec.VesselName))
                        names.Add(rec.VesselName);
                }
            }
            if (!SuppressLogging && names.Count > 0)
                ParsekLog.Info("RecordingStore",
                    $"CollectAllRecordingVesselNames: {names.Count} unique name(s)");
            return names;
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
        /// Returns the recording that owns the rewind save for the given recording.
        /// For standalone recordings this is the recording itself; for tree branches
        /// it is the tree root (which captured the quicksave at launch).
        /// Returns null if no rewind save is available.
        /// </summary>
        internal static Recording GetRewindRecording(Recording rec)
        {
            return GetRewindRecording(rec, committedTrees);
        }

        /// <summary>
        /// Parameterized overload for testability.
        /// </summary>
        internal static Recording GetRewindRecording(Recording rec, List<RecordingTree> trees)
        {
            if (rec == null) return null;

            // Direct owner: recording has its own rewind save
            if (!string.IsNullOrEmpty(rec.RewindSaveFileName))
                return rec;

            // Tree branch: look up root recording's rewind save
            if (!string.IsNullOrEmpty(rec.TreeId) && trees != null)
            {
                for (int i = 0; i < trees.Count; i++)
                {
                    if (trees[i].Id == rec.TreeId)
                    {
                        Recording rootRec;
                        if (!string.IsNullOrEmpty(trees[i].RootRecordingId) &&
                            trees[i].Recordings.TryGetValue(trees[i].RootRecordingId, out rootRec) &&
                            !string.IsNullOrEmpty(rootRec.RewindSaveFileName))
                        {
                            return rootRec;
                        }
                        break; // Found tree but root has no save
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Convenience wrapper: returns the rewind save filename for a recording,
        /// resolving through the tree root if needed. Returns null if unavailable.
        /// </summary>
        internal static string GetRewindSaveFileName(Recording rec)
        {
            var owner = GetRewindRecording(rec);
            return owner?.RewindSaveFileName;
        }

        /// <summary>
        /// Checks whether the player can rewind to a given recording's launch point.
        /// </summary>
        internal static bool CanRewind(Recording rec, out string reason, bool isRecording)
        {
            if (IsRewinding)
            {
                reason = "Rewind already in progress";
                // Per-frame logging removed (was 3.7% of all log output); reason returned to caller
                return false;
            }

            // Resolve rewind save through tree root if needed
            string resolvedSave = GetRewindSaveFileName(rec);
            if (string.IsNullOrEmpty(resolvedSave))
            {
                reason = "No rewind save available";
                // Per-frame logging removed (was 3.7% of all log output); reason returned to caller
                return false;
            }

            if (isRecording)
            {
                reason = "Stop recording before rewinding";
                // Per-frame logging removed (was 3.7% of all log output); reason returned to caller
                return false;
            }

            if (HasPendingTree)
            {
                reason = "Merge or discard pending tree first";
                // Per-frame logging removed (was 3.7% of all log output); reason returned to caller
                return false;
            }

            // Verify the save file exists
            string savePath = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildRewindSaveRelativePath(resolvedSave));
            if (string.IsNullOrEmpty(savePath) || !File.Exists(savePath))
            {
                reason = "Rewind save file missing";
                // Per-frame logging removed (was 3.7% of all log output); reason returned to caller
                return false;
            }

            reason = "";
            // Per-frame logging removed; reason returned to caller
            return true;
        }

        /// <summary>
        /// Validates whether the player can fast-forward to a future recording's start.
        /// Subset of CanRewind — no save file required (FF advances UT, doesn't load a save).
        /// </summary>
        internal static bool CanFastForward(Recording rec, out string reason, bool isRecording)
        {
            if (IsRewinding)
            {
                reason = "Rewind already in progress";
                // Per-frame logging removed (was 20% of all log output); reason returned to caller
                return false;
            }

            if (rec == null || rec.Points.Count == 0)
            {
                reason = "Recording not available";
                // Per-frame logging removed (was 20% of all log output); reason returned to caller
                return false;
            }

            if (isRecording)
            {
                reason = "Stop recording before fast-forwarding";
                // Per-frame logging removed (was 20% of all log output); reason returned to caller
                return false;
            }

            if (HasPendingTree)
            {
                reason = "Merge or discard pending tree first";
                // Per-frame logging removed (was 20% of all log output); reason returned to caller
                return false;
            }

            // Timing check last — requires KSP runtime (Planetarium)
            double now = Planetarium.GetUniversalTime();
            if (now >= rec.StartUT)
            {
                reason = "Recording is not in the future";
                // Per-frame logging removed (was 20% of all log output); reason returned to caller
                return false;
            }

            reason = "";
            // Per-frame logging removed; reason returned to caller
            return true;
        }

        private static void ResetRewindFlags()
        {
            RewindContext.EndRewind();
        }

        /// <summary>
        /// Initiates a rewind to the given recording's launch point.
        /// Sets rewind flags, copies the save file to the root saves dir (KSP's LoadGame
        /// doesn't support subdirectory paths), loads it, then deletes the temp copy.
        /// </summary>
        internal static void InitiateRewind(Recording rec)
        {
            // Resolve the rewind save owner — may be the tree root for branch recordings.
            // All save-related fields (filename, resources, UT) come from the owner,
            // since the quicksave was captured at the owner's launch.
            var owner = GetRewindRecording(rec);
            if (owner == null)
            {
                if (!SuppressLogging)
                    ParsekLog.Error("Rewind",
                        $"InitiateRewind: no rewind owner for '{rec.VesselName}' (id={rec.RecordingId})");
                return;
            }

            bool isTreeBranch = owner != rec;
            if (isTreeBranch && !SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Rewind via tree root: branch '{rec.VesselName}' -> root '{owner.VesselName}' " +
                    $"save={owner.RewindSaveFileName}");

            var reserved = new BudgetSummary
            {
                reservedFunds = owner.RewindReservedFunds,
                reservedScience = owner.RewindReservedScience,
                reservedReputation = owner.RewindReservedRep
            };

            // Baseline resources from the owner's pre-launch snapshot.
            // The rewind save was captured at the same moment as PreLaunch values.
            RewindContext.BeginRewind(owner.StartUT, reserved,
                owner.PreLaunchFunds, owner.PreLaunchScience, owner.PreLaunchReputation);

            if (!SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Rewind initiated to UT {owner.StartUT} " +
                    $"(save: {owner.RewindSaveFileName})");

            string tempCopyName = null;
            try
            {
                string savesDir = Path.Combine(
                    KSPUtil.ApplicationRootPath ?? "",
                    "saves",
                    HighLogic.SaveFolder ?? "");

                string sourcePath = Path.Combine(savesDir,
                    RecordingPaths.BuildRewindSaveRelativePath(owner.RewindSaveFileName));
                tempCopyName = owner.RewindSaveFileName;
                string tempPath = Path.Combine(savesDir, tempCopyName + ".sfs");

                // Copy from Parsek/Saves/ to root saves dir
                File.Copy(sourcePath, tempPath, true);

                // Pre-process the save file before KSP parses it:
                // 1. Remove recorded vessel + any EVA child vessels (other vessels stay intact)
                // 2. Wind back UT by 10 seconds so the player can reach the pad before launch
                const double rewindLeadTime = 10.0;

                // Collect all vessel names to strip — use owner's identity since the
                // quicksave contains the owner's vessel (not the branch's).
                var stripNames = new HashSet<string> { owner.VesselName };
                if (!string.IsNullOrEmpty(owner.ChainId))
                {
                    // EVA child recordings have different vessel names (the kerbal's name)
                    // and would otherwise survive the strip
                    foreach (var committed in committedRecordings)
                    {
                        if (committed.ChainId == owner.ChainId &&
                            !string.IsNullOrEmpty(committed.EvaCrewName) &&
                            committed.VesselName != owner.VesselName)
                        {
                            stripNames.Add(committed.VesselName);
                        }
                    }
                    if (stripNames.Count > 1 && !SuppressLogging)
                        ParsekLog.Info("Rewind",
                            $"Rewind strip includes {stripNames.Count - 1} EVA child vessel name(s) from chain '{owner.ChainId}'");
                }
                // Collect spawned vessel PIDs for PID-based stripping (belt-and-suspenders
                // alongside name matching — catches renamed vessels or debris)
                var (stripPids, _) = CollectSpawnedVesselInfo();
                PreProcessRewindSave(tempPath, stripNames, stripPids, rewindLeadTime);

                Game game = GamePersistence.LoadGame(tempCopyName, HighLogic.SaveFolder, true, false);

                // Delete the temp copy (file already parsed into Game object)
                try { File.Delete(tempPath); }
                catch { }

                if (game == null)
                {
                    ResetRewindFlags();
                    if (!SuppressLogging)
                        ParsekLog.Error("Rewind",
                            $"Rewind failed: LoadGame returned null for save '{owner.RewindSaveFileName}'. " +
                            $"Flags reset: IsRewinding={IsRewinding}, RewindUT={RewindUT}, RewindAdjustedUT={RewindAdjustedUT}");
                    return;
                }

                // Capture the adjusted UT from the preprocessed save.
                RewindContext.SetAdjustedUT(game.flightState.universalTime);

                if (!SuppressLogging)
                    ParsekLog.Info("Rewind",
                        $"Rewind: adjustedUT={RewindAdjustedUT:F1}, " +
                        $"rewindUT={RewindUT:F1}, flags=[IsRewinding={IsRewinding}]");

                HighLogic.CurrentGame = game;
                HighLogic.LoadScene(GameScenes.SPACECENTER);

                if (!SuppressLogging)
                    ParsekLog.Info("Rewind",
                        $"Rewind: loading save '{owner.RewindSaveFileName}' into SpaceCenter");
            }
            catch (Exception ex)
            {
                ResetRewindFlags();

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
            PreProcessRewindSave(sfsPath, vesselNames, null, leadTime);
        }

        /// <summary>
        /// Extended overload that strips vessels by both name AND persistent ID.
        /// PID matching catches renamed vessels or debris that name matching misses.
        /// The .sfs ConfigNode reliably has "persistentId" as a text field.
        /// </summary>
        internal static void PreProcessRewindSave(
            string sfsPath, HashSet<string> vesselNames, HashSet<uint> vesselPids, double leadTime)
        {
            ConfigNode root = ConfigNode.Load(sfsPath);
            if (root == null)
                return;

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
            else if (!SuppressLogging)
            {
                ParsekLog.Warn("Rewind",
                    $"PreProcessRewindSave: missing or invalid UT value '{utStr ?? "(null)"}' in FLIGHTSTATE");
            }

            // Remove vessels matching name OR persistentId
            bool hasPids = vesselPids != null && vesselPids.Count > 0;
            int removedByName = 0;
            int removedByPid = 0;
            var vesselNodes = flightState.GetNodes("VESSEL");
            for (int i = vesselNodes.Length - 1; i >= 0; i--)
            {
                string name = Recording.ResolveLocalizedName(vesselNodes[i].GetValue("name"));
                if (vesselNames.Contains(name))
                {
                    flightState.RemoveNode(vesselNodes[i]);
                    removedByName++;
                    continue;
                }

                if (hasPids)
                {
                    string pidStr = vesselNodes[i].GetValue("persistentId");
                    uint pid;
                    if (pidStr != null
                        && uint.TryParse(pidStr, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out pid)
                        && vesselPids.Contains(pid))
                    {
                        flightState.RemoveNode(vesselNodes[i]);
                        removedByPid++;
                    }
                }
            }

            if (!SuppressLogging)
            {
                string namesStr = string.Join(", ", vesselNames);
                ParsekLog.Info("Rewind",
                    $"Stripped {removedByName + removedByPid} vessel(s) from save " +
                    $"({removedByName} by name [{namesStr}], {removedByPid} by PID)");
            }

            // Capture PIDs of surviving vessels in the quicksave.
            // Used by StripFuturePrelaunchVessels to whitelist known-good PRELAUNCH
            // vessels and strip only unknown ones that appeared after the rewind point.
            var survivingPids = new HashSet<uint>();
            var survivingNodes = flightState.GetNodes("VESSEL");
            for (int s = 0; s < survivingNodes.Length; s++)
            {
                uint spid;
                if (uint.TryParse(survivingNodes[s].GetValue("persistentId"), out spid))
                    survivingPids.Add(spid);
            }
            RewindContext.SetQuicksaveVesselPids(survivingPids.Count > 0 ? survivingPids : null);
            if (!SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Captured {survivingPids.Count} surviving vessel PID(s) from quicksave");

            root.Save(sfsPath);
        }

        #endregion

        #region Trajectory Serialization

        private static void SerializePoint(ConfigNode parent, TrajectoryPoint pt, CultureInfo ic)
        {
            ConfigNode ptNode = parent.AddNode("POINT");
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

        private static TrajectoryPoint DeserializePoint(ConfigNode ptNode, NumberStyles ns, CultureInfo ic)
        {
            var pt = new TrajectoryPoint();

            double.TryParse(ptNode.GetValue("ut"), ns, ic, out pt.ut);
            double.TryParse(ptNode.GetValue("lat"), ns, ic, out pt.latitude);
            double.TryParse(ptNode.GetValue("lon"), ns, ic, out pt.longitude);
            double.TryParse(ptNode.GetValue("alt"), ns, ic, out pt.altitude);

            float rx, ry, rz, rw;
            float.TryParse(ptNode.GetValue("rotX"), ns, ic, out rx);
            float.TryParse(ptNode.GetValue("rotY"), ns, ic, out ry);
            float.TryParse(ptNode.GetValue("rotZ"), ns, ic, out rz);
            float.TryParse(ptNode.GetValue("rotW"), ns, ic, out rw);
            pt.rotation = new Quaternion(rx, ry, rz, rw);

            pt.bodyName = ptNode.GetValue("body") ?? "Kerbin";

            float velX, velY, velZ;
            float.TryParse(ptNode.GetValue("velX"), ns, ic, out velX);
            float.TryParse(ptNode.GetValue("velY"), ns, ic, out velY);
            float.TryParse(ptNode.GetValue("velZ"), ns, ic, out velZ);
            pt.velocity = new Vector3(velX, velY, velZ);

            double funds;
            double.TryParse(ptNode.GetValue("funds"), ns, ic, out funds);
            pt.funds = funds;

            float science, rep;
            float.TryParse(ptNode.GetValue("science"), ns, ic, out science);
            float.TryParse(ptNode.GetValue("rep"), ns, ic, out rep);
            pt.science = science;
            pt.reputation = rep;

            return pt;
        }

        private static void SerializeOrbitSegment(ConfigNode parent, OrbitSegment seg, CultureInfo ic)
        {
            ConfigNode segNode = parent.AddNode("ORBIT_SEGMENT");
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

        private static OrbitSegment DeserializeOrbitSegment(ConfigNode segNode, NumberStyles ns, CultureInfo ic)
        {
            var seg = new OrbitSegment();

            double.TryParse(segNode.GetValue("startUT"), ns, ic, out seg.startUT);
            double.TryParse(segNode.GetValue("endUT"), ns, ic, out seg.endUT);
            double.TryParse(segNode.GetValue("inc"), ns, ic, out seg.inclination);
            double.TryParse(segNode.GetValue("ecc"), ns, ic, out seg.eccentricity);
            double.TryParse(segNode.GetValue("sma"), ns, ic, out seg.semiMajorAxis);
            double.TryParse(segNode.GetValue("lan"), ns, ic, out seg.longitudeOfAscendingNode);
            double.TryParse(segNode.GetValue("argPe"), ns, ic, out seg.argumentOfPeriapsis);
            double.TryParse(segNode.GetValue("mna"), ns, ic, out seg.meanAnomalyAtEpoch);
            double.TryParse(segNode.GetValue("epoch"), ns, ic, out seg.epoch);
            seg.bodyName = segNode.GetValue("body") ?? "Kerbin";

            float ofrX, ofrY, ofrZ, ofrW;
            if (float.TryParse(segNode.GetValue("ofrX"), ns, ic, out ofrX) &&
                float.TryParse(segNode.GetValue("ofrY"), ns, ic, out ofrY) &&
                float.TryParse(segNode.GetValue("ofrZ"), ns, ic, out ofrZ) &&
                float.TryParse(segNode.GetValue("ofrW"), ns, ic, out ofrW))
            {
                seg.orbitalFrameRotation = new Quaternion(ofrX, ofrY, ofrZ, ofrW);
            }

            float avX, avY, avZ;
            if (float.TryParse(segNode.GetValue("avX"), ns, ic, out avX) &&
                float.TryParse(segNode.GetValue("avY"), ns, ic, out avY) &&
                float.TryParse(segNode.GetValue("avZ"), ns, ic, out avZ))
            {
                seg.angularVelocity = new Vector3(avX, avY, avZ);
            }

            return seg;
        }

        internal static void SerializeTrajectoryInto(ConfigNode targetNode, Recording rec)
        {
            var ic = CultureInfo.InvariantCulture;
            for (int i = 0; i < rec.Points.Count; i++)
                SerializePoint(targetNode, rec.Points[i], ic);

            for (int s = 0; s < rec.OrbitSegments.Count; s++)
                SerializeOrbitSegment(targetNode, rec.OrbitSegments[s], ic);

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

            for (int fe = 0; fe < rec.FlagEvents.Count; fe++)
            {
                var evt = rec.FlagEvents[fe];
                ConfigNode feNode = targetNode.AddNode("FLAG_EVENT");
                feNode.AddValue("ut", evt.ut.ToString("R", ic));
                feNode.AddValue("name", evt.flagSiteName ?? "");
                feNode.AddValue("placedBy", evt.placedBy ?? "");
                feNode.AddValue("plaqueText", evt.plaqueText ?? "");
                feNode.AddValue("flagURL", evt.flagURL ?? "");
                feNode.AddValue("lat", evt.latitude.ToString("R", ic));
                feNode.AddValue("lon", evt.longitude.ToString("R", ic));
                feNode.AddValue("alt", evt.altitude.ToString("R", ic));
                feNode.AddValue("rotX", evt.rotX.ToString("R", ic));
                feNode.AddValue("rotY", evt.rotY.ToString("R", ic));
                feNode.AddValue("rotZ", evt.rotZ.ToString("R", ic));
                feNode.AddValue("rotW", evt.rotW.ToString("R", ic));
                feNode.AddValue("body", evt.bodyName ?? "Kerbin");
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
            DeserializeFlagEvents(sourceNode, rec);
            DeserializeSegmentEvents(sourceNode, rec.SegmentEvents);
            DeserializeTrackSections(sourceNode, rec.TrackSections);
        }

        /// <summary>
        /// Deserializes POINT nodes from a trajectory ConfigNode into the recording's Points list.
        /// </summary>
        internal static void DeserializePoints(ConfigNode sourceNode, Recording rec)
        {
            var ns = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            ConfigNode[] ptNodes = sourceNode.GetNodes("POINT");
            int parseFailCount = 0;
            for (int i = 0; i < ptNodes.Length; i++)
            {
                var ptNode = ptNodes[i];
                bool utOk = double.TryParse(ptNode.GetValue("ut"), ns, ic, out _);
                if (!utOk)
                    parseFailCount++;

                rec.Points.Add(DeserializePoint(ptNode, ns, ic));
            }
            if (parseFailCount > 0)
                Log($"[Parsek] WARNING: {parseFailCount}/{ptNodes.Length} trajectory points had unparseable UT in recording {rec.RecordingId}");
        }

        /// <summary>
        /// Deserializes ORBIT_SEGMENT nodes from a trajectory ConfigNode into the recording's OrbitSegments list.
        /// </summary>
        internal static void DeserializeOrbitSegments(ConfigNode sourceNode, Recording rec)
        {
            var ns = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            ConfigNode[] segNodes = sourceNode.GetNodes("ORBIT_SEGMENT");
            for (int s = 0; s < segNodes.Length; s++)
                rec.OrbitSegments.Add(DeserializeOrbitSegment(segNodes[s], ns, ic));

        }

        /// <summary>
        /// Deserializes PART_EVENT nodes from a trajectory ConfigNode into the recording's PartEvents list.
        /// </summary>
        internal static void DeserializePartEvents(ConfigNode sourceNode, Recording rec)
        {
            var ns = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            ConfigNode[] peNodes = sourceNode.GetNodes("PART_EVENT");
            for (int pe = 0; pe < peNodes.Length; pe++)
            {
                var peNode = peNodes[pe];
                var evt = new PartEvent();

                double.TryParse(peNode.GetValue("ut"), ns, ic, out evt.ut);
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
                if (float.TryParse(peNode.GetValue("value"), ns, ic, out val))
                    evt.value = val;
                int midx;
                if (int.TryParse(peNode.GetValue("midx"), NumberStyles.Integer, ic, out midx))
                    evt.moduleIndex = midx;

                rec.PartEvents.Add(evt);
            }
        }

        /// <summary>
        /// Deserializes FLAG_EVENT nodes from a trajectory ConfigNode into the recording's FlagEvents list.
        /// </summary>
        internal static void DeserializeFlagEvents(ConfigNode sourceNode, Recording rec)
        {
            var ns = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            ConfigNode[] feNodes = sourceNode.GetNodes("FLAG_EVENT");
            for (int fe = 0; fe < feNodes.Length; fe++)
            {
                var feNode = feNodes[fe];
                var evt = new FlagEvent();

                double.TryParse(feNode.GetValue("ut"), ns, ic, out evt.ut);
                evt.flagSiteName = feNode.GetValue("name") ?? "";
                evt.placedBy = feNode.GetValue("placedBy") ?? "";
                evt.plaqueText = feNode.GetValue("plaqueText") ?? "";
                evt.flagURL = feNode.GetValue("flagURL") ?? "";
                double.TryParse(feNode.GetValue("lat"), ns, ic, out evt.latitude);
                double.TryParse(feNode.GetValue("lon"), ns, ic, out evt.longitude);
                double.TryParse(feNode.GetValue("alt"), ns, ic, out evt.altitude);
                float.TryParse(feNode.GetValue("rotX"), ns, ic, out evt.rotX);
                float.TryParse(feNode.GetValue("rotY"), ns, ic, out evt.rotY);
                float.TryParse(feNode.GetValue("rotZ"), ns, ic, out evt.rotZ);
                float.TryParse(feNode.GetValue("rotW"), ns, ic, out evt.rotW);
                evt.bodyName = feNode.GetValue("body") ?? "Kerbin";

                rec.FlagEvents.Add(evt);
            }

        }

        /// <summary>
        /// Serializes SegmentEvent entries as SEGMENT_EVENT child nodes.
        /// </summary>
        internal static void SerializeSegmentEvents(ConfigNode parent, List<SegmentEvent> events)
        {
            if (events == null || events.Count == 0)
            {
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

            var ns = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;
            int skipped = 0;

            for (int i = 0; i < seNodes.Length; i++)
            {
                var seNode = seNodes[i];

                double ut;
                if (!double.TryParse(seNode.GetValue("ut"), ns, ic, out ut))
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

            if (skipped > 0)
                ParsekLog.Warn("RecordingStore", $"DeserializeSegmentEvents: {skipped}/{seNodes.Length} events skipped");
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

                // Altitude range: sparse — only write when tracked (non-NaN)
                if (!float.IsNaN(track.minAltitude))
                    tsNode.AddValue("minAlt", track.minAltitude.ToString("R", ic));
                if (!float.IsNaN(track.maxAltitude))
                    tsNode.AddValue("maxAlt", track.maxAltitude.ToString("R", ic));

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
                            SerializePoint(tsNode, frames[i], ic);
                    }
                }
                else if (track.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
                {
                    var checkpoints = track.checkpoints;
                    if (checkpoints != null)
                    {
                        for (int s = 0; s < checkpoints.Count; s++)
                            SerializeOrbitSegment(tsNode, checkpoints[s], ic);
                    }
                }

            }
        }

        /// <summary>
        /// Deserializes TRACK_SECTION ConfigNodes from the given parent into the tracks list.
        /// Unknown environment or reference frame values cause the entire section to be skipped
        /// with a warning (forward tolerance for future enum additions).
        /// </summary>
        internal static void DeserializeTrackSections(ConfigNode parent, List<TrackSection> tracks)
        {
            var ns = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            ConfigNode[] tsNodes = parent.GetNodes("TRACK_SECTION");
            if (tsNodes.Length == 0)
            {
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
                double.TryParse(tsNode.GetValue("startUT"), ns, ic, out section.startUT);
                double.TryParse(tsNode.GetValue("endUT"), ns, ic, out section.endUT);
                float.TryParse(tsNode.GetValue("sampleRate"), ns, ic, out section.sampleRateHz);

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
                if (float.TryParse(tsNode.GetValue("bdisc"), ns, ic, out bdisc))
                    section.boundaryDiscontinuityMeters = bdisc;

                // Altitude range: defaults to NaN when absent (legacy recordings)
                float minAlt, maxAlt;
                section.minAltitude = float.TryParse(tsNode.GetValue("minAlt"), ns, ic, out minAlt)
                    ? minAlt : float.NaN;
                section.maxAltitude = float.TryParse(tsNode.GetValue("maxAlt"), ns, ic, out maxAlt)
                    ? maxAlt : float.NaN;

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
                        section.frames.Add(DeserializePoint(ptNodes[i], ns, ic));
                }
                else if (section.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
                {
                    section.checkpoints = new List<OrbitSegment>();
                    ConfigNode[] segNodes = tsNode.GetNodes("ORBIT_SEGMENT");
                    for (int s = 0; s < segNodes.Length; s++)
                        section.checkpoints.Add(DeserializeOrbitSegment(segNodes[s], ns, ic));
                }

                // Initialize null lists to empty for frames that don't have nested data
                if (section.frames == null)
                    section.frames = new List<TrajectoryPoint>();
                if (section.checkpoints == null)
                    section.checkpoints = new List<OrbitSegment>();

                tracks.Add(section);
            }
        }

        #endregion

        #region Crew End States Serialization

        /// <summary>
        /// Serializes CrewEndStates dictionary into CREW_END_STATES ConfigNode children
        /// on the given parent node. Each entry becomes an ENTRY subnode with "name" and "state" keys.
        /// No-op if CrewEndStates is null or empty.
        /// </summary>
        internal static void SerializeCrewEndStates(ConfigNode parent, Recording rec)
        {
            if (rec.CrewEndStates == null || rec.CrewEndStates.Count == 0)
                return;

            ConfigNode cesNode = parent.AddNode("CREW_END_STATES");
            int count = 0;
            foreach (var kvp in rec.CrewEndStates)
            {
                ConfigNode entry = cesNode.AddNode("ENTRY");
                entry.AddValue("name", kvp.Key ?? "");
                entry.AddValue("state", ((int)kvp.Value).ToString(CultureInfo.InvariantCulture));
                count++;
            }
            ParsekLog.Verbose("RecordingStore",
                $"SerializeCrewEndStates: wrote {count} entries for recording={rec.RecordingId}");
        }

        /// <summary>
        /// Deserializes CrewEndStates from a CREW_END_STATES ConfigNode on the given parent.
        /// Sets rec.CrewEndStates to a new dictionary if entries are found, or leaves it null
        /// if the node is absent (backward compatible with legacy recordings).
        /// </summary>
        internal static void DeserializeCrewEndStates(ConfigNode parent, Recording rec)
        {
            ConfigNode cesNode = parent.GetNode("CREW_END_STATES");
            if (cesNode == null)
                return;

            ConfigNode[] entries = cesNode.GetNodes("ENTRY");
            if (entries.Length == 0)
                return;

            rec.CrewEndStates = new Dictionary<string, KerbalEndState>();
            int loaded = 0;
            int skipped = 0;

            for (int i = 0; i < entries.Length; i++)
            {
                string name = entries[i].GetValue("name");
                string stateStr = entries[i].GetValue("state");

                if (string.IsNullOrEmpty(name))
                {
                    skipped++;
                    continue;
                }

                int stateInt;
                if (!int.TryParse(stateStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out stateInt)
                    || !Enum.IsDefined(typeof(KerbalEndState), stateInt))
                {
                    skipped++;
                    continue;
                }

                rec.CrewEndStates[name] = (KerbalEndState)stateInt;
                loaded++;
            }

            ParsekLog.Verbose("RecordingStore",
                $"DeserializeCrewEndStates: loaded={loaded} skipped={skipped} for recording={rec.RecordingId}");
        }

        #endregion

        #region Resource Manifest Serialization

        /// <summary>
        /// Serializes StartResources and EndResources dictionaries into a RESOURCE_MANIFEST
        /// ConfigNode on the given parent. Each resource becomes a RESOURCE child node with
        /// name, startAmount, startMax, endAmount, endMax fields.
        /// No-op if both StartResources and EndResources are null or empty.
        /// </summary>
        internal static void SerializeResourceManifest(ConfigNode parent, Recording rec)
        {
            bool hasStart = rec.StartResources != null && rec.StartResources.Count > 0;
            bool hasEnd = rec.EndResources != null && rec.EndResources.Count > 0;
            if (!hasStart && !hasEnd)
                return;

            ConfigNode manifestNode = parent.AddNode("RESOURCE_MANIFEST");

            // Build merged key set from StartResources ∪ EndResources
            var keys = new HashSet<string>();
            if (hasStart)
                foreach (var k in rec.StartResources.Keys) keys.Add(k);
            if (hasEnd)
                foreach (var k in rec.EndResources.Keys) keys.Add(k);

            int count = 0;
            foreach (var name in keys)
            {
                ConfigNode resNode = manifestNode.AddNode("RESOURCE");
                resNode.AddValue("name", name);

                if (hasStart && rec.StartResources.TryGetValue(name, out var startRa))
                {
                    resNode.AddValue("startAmount", startRa.amount.ToString("R", CultureInfo.InvariantCulture));
                    resNode.AddValue("startMax", startRa.maxAmount.ToString("R", CultureInfo.InvariantCulture));
                }

                if (hasEnd && rec.EndResources.TryGetValue(name, out var endRa))
                {
                    resNode.AddValue("endAmount", endRa.amount.ToString("R", CultureInfo.InvariantCulture));
                    resNode.AddValue("endMax", endRa.maxAmount.ToString("R", CultureInfo.InvariantCulture));
                }

                count++;
            }

            ParsekLog.Verbose("RecordingStore",
                $"SerializeResourceManifest: wrote {count} resource(s) for recording={rec.RecordingId}");
        }

        /// <summary>
        /// Deserializes StartResources and EndResources from a RESOURCE_MANIFEST ConfigNode
        /// on the given parent. Sets the dictionaries if entries are found, or leaves them null
        /// if the node is absent (backward compatible with legacy recordings).
        /// </summary>
        internal static void DeserializeResourceManifest(ConfigNode parent, Recording rec)
        {
            ConfigNode manifestNode = parent.GetNode("RESOURCE_MANIFEST");
            if (manifestNode == null)
                return;

            ConfigNode[] resources = manifestNode.GetNodes("RESOURCE");
            if (resources.Length == 0)
                return;

            int loaded = 0;
            int skipped = 0;

            for (int i = 0; i < resources.Length; i++)
            {
                string name = resources[i].GetValue("name");
                if (string.IsNullOrEmpty(name))
                {
                    skipped++;
                    continue;
                }

                // Parse start fields (if present)
                string startAmountStr = resources[i].GetValue("startAmount");
                string startMaxStr = resources[i].GetValue("startMax");
                if (startAmountStr != null || startMaxStr != null)
                {
                    if (rec.StartResources == null)
                        rec.StartResources = new Dictionary<string, ResourceAmount>();

                    double startAmount = 0;
                    double startMax = 0;
                    if (startAmountStr != null)
                        double.TryParse(startAmountStr, NumberStyles.Float, CultureInfo.InvariantCulture, out startAmount);
                    if (startMaxStr != null)
                        double.TryParse(startMaxStr, NumberStyles.Float, CultureInfo.InvariantCulture, out startMax);

                    rec.StartResources[name] = new ResourceAmount { amount = startAmount, maxAmount = startMax };
                }

                // Parse end fields (if present)
                string endAmountStr = resources[i].GetValue("endAmount");
                string endMaxStr = resources[i].GetValue("endMax");
                if (endAmountStr != null || endMaxStr != null)
                {
                    if (rec.EndResources == null)
                        rec.EndResources = new Dictionary<string, ResourceAmount>();

                    double endAmount = 0;
                    double endMax = 0;
                    if (endAmountStr != null)
                        double.TryParse(endAmountStr, NumberStyles.Float, CultureInfo.InvariantCulture, out endAmount);
                    if (endMaxStr != null)
                        double.TryParse(endMaxStr, NumberStyles.Float, CultureInfo.InvariantCulture, out endMax);

                    rec.EndResources[name] = new ResourceAmount { amount = endAmount, maxAmount = endMax };
                }

                loaded++;
            }

            ParsekLog.Verbose("RecordingStore",
                $"DeserializeResourceManifest: loaded={loaded} skipped={skipped} for recording={rec.RecordingId}");
        }

        /// <summary>
        /// Serializes StartInventory and EndInventory dictionaries into an INVENTORY_MANIFEST
        /// ConfigNode on the given parent. Each item becomes an ITEM child node with
        /// name, startCount, startSlots, endCount, endSlots fields.
        /// No-op if both StartInventory and EndInventory are null or empty.
        /// </summary>
        internal static void SerializeInventoryManifest(ConfigNode parent, Recording rec)
        {
            bool hasStart = rec.StartInventory != null && rec.StartInventory.Count > 0;
            bool hasEnd = rec.EndInventory != null && rec.EndInventory.Count > 0;
            if (!hasStart && !hasEnd)
                return;

            ConfigNode manifestNode = parent.AddNode("INVENTORY_MANIFEST");

            // Build merged key set from StartInventory ∪ EndInventory
            var keys = new HashSet<string>();
            if (hasStart)
                foreach (var k in rec.StartInventory.Keys) keys.Add(k);
            if (hasEnd)
                foreach (var k in rec.EndInventory.Keys) keys.Add(k);

            int count = 0;
            foreach (var name in keys)
            {
                ConfigNode itemNode = manifestNode.AddNode("ITEM");
                itemNode.AddValue("name", name);

                if (hasStart && rec.StartInventory.TryGetValue(name, out var startItem))
                {
                    itemNode.AddValue("startCount", startItem.count.ToString(CultureInfo.InvariantCulture));
                    itemNode.AddValue("startSlots", startItem.slotsTaken.ToString(CultureInfo.InvariantCulture));
                }

                if (hasEnd && rec.EndInventory.TryGetValue(name, out var endItem))
                {
                    itemNode.AddValue("endCount", endItem.count.ToString(CultureInfo.InvariantCulture));
                    itemNode.AddValue("endSlots", endItem.slotsTaken.ToString(CultureInfo.InvariantCulture));
                }

                count++;
            }

            ParsekLog.Verbose("RecordingStore",
                $"SerializeInventoryManifest: wrote {count} item(s) for recording={rec.RecordingId}");
        }

        /// <summary>
        /// Deserializes StartInventory and EndInventory from an INVENTORY_MANIFEST ConfigNode
        /// on the given parent. Sets the dictionaries if entries are found, or leaves them null
        /// if the node is absent (backward compatible with legacy recordings).
        /// </summary>
        internal static void DeserializeInventoryManifest(ConfigNode parent, Recording rec)
        {
            ConfigNode manifestNode = parent.GetNode("INVENTORY_MANIFEST");
            if (manifestNode == null)
                return;

            ConfigNode[] items = manifestNode.GetNodes("ITEM");
            if (items.Length == 0)
                return;

            int loaded = 0;
            int skipped = 0;

            for (int i = 0; i < items.Length; i++)
            {
                string name = items[i].GetValue("name");
                if (string.IsNullOrEmpty(name))
                {
                    skipped++;
                    continue;
                }

                // Parse start fields (if present)
                string startCountStr = items[i].GetValue("startCount");
                string startSlotsStr = items[i].GetValue("startSlots");
                if (startCountStr != null || startSlotsStr != null)
                {
                    if (rec.StartInventory == null)
                        rec.StartInventory = new Dictionary<string, InventoryItem>();

                    int startCount = 0;
                    int startSlots = 0;
                    if (startCountStr != null)
                        int.TryParse(startCountStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out startCount);
                    if (startSlotsStr != null)
                        int.TryParse(startSlotsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out startSlots);

                    rec.StartInventory[name] = new InventoryItem { count = startCount, slotsTaken = startSlots };
                }

                // Parse end fields (if present)
                string endCountStr = items[i].GetValue("endCount");
                string endSlotsStr = items[i].GetValue("endSlots");
                if (endCountStr != null || endSlotsStr != null)
                {
                    if (rec.EndInventory == null)
                        rec.EndInventory = new Dictionary<string, InventoryItem>();

                    int endCount = 0;
                    int endSlots = 0;
                    if (endCountStr != null)
                        int.TryParse(endCountStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out endCount);
                    if (endSlotsStr != null)
                        int.TryParse(endSlotsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out endSlots);

                    rec.EndInventory[name] = new InventoryItem { count = endCount, slotsTaken = endSlots };
                }

                loaded++;
            }

            ParsekLog.Verbose("RecordingStore",
                $"DeserializeInventoryManifest: loaded={loaded} skipped={skipped} for recording={rec.RecordingId}");
        }

        /// <summary>
        /// Serializes StartCrew and EndCrew dictionaries into a CREW_MANIFEST
        /// ConfigNode on the given parent. Each trait becomes a TRAIT child node with
        /// name, startCount, endCount fields.
        /// No-op if both StartCrew and EndCrew are null or empty.
        /// </summary>
        internal static void SerializeCrewManifest(ConfigNode parent, Recording rec)
        {
            bool hasStart = rec.StartCrew != null && rec.StartCrew.Count > 0;
            bool hasEnd = rec.EndCrew != null && rec.EndCrew.Count > 0;
            if (!hasStart && !hasEnd)
                return;

            ConfigNode manifestNode = parent.AddNode("CREW_MANIFEST");

            // Build merged key set from StartCrew ∪ EndCrew
            var keys = new HashSet<string>();
            if (hasStart)
                foreach (var k in rec.StartCrew.Keys) keys.Add(k);
            if (hasEnd)
                foreach (var k in rec.EndCrew.Keys) keys.Add(k);

            int count = 0;
            foreach (var name in keys)
            {
                ConfigNode traitNode = manifestNode.AddNode("TRAIT");
                traitNode.AddValue("name", name);

                if (hasStart && rec.StartCrew.TryGetValue(name, out var startCount))
                {
                    traitNode.AddValue("startCount", startCount.ToString(CultureInfo.InvariantCulture));
                }

                if (hasEnd && rec.EndCrew.TryGetValue(name, out var endCount))
                {
                    traitNode.AddValue("endCount", endCount.ToString(CultureInfo.InvariantCulture));
                }

                count++;
            }

            ParsekLog.Verbose("RecordingStore",
                $"SerializeCrewManifest: wrote {count} trait(s) for recording={rec.RecordingId}");
        }

        /// <summary>
        /// Deserializes StartCrew and EndCrew from a CREW_MANIFEST ConfigNode
        /// on the given parent. Sets the dictionaries if entries are found, or leaves them null
        /// if the node is absent (backward compatible with legacy recordings).
        /// </summary>
        internal static void DeserializeCrewManifest(ConfigNode parent, Recording rec)
        {
            ConfigNode manifestNode = parent.GetNode("CREW_MANIFEST");
            if (manifestNode == null)
                return;

            ConfigNode[] traits = manifestNode.GetNodes("TRAIT");
            if (traits.Length == 0)
                return;

            int loaded = 0;
            int skipped = 0;

            for (int i = 0; i < traits.Length; i++)
            {
                string name = traits[i].GetValue("name");
                if (string.IsNullOrEmpty(name))
                {
                    skipped++;
                    continue;
                }

                // Parse start fields (if present)
                string startCountStr = traits[i].GetValue("startCount");
                if (startCountStr != null)
                {
                    if (rec.StartCrew == null)
                        rec.StartCrew = new Dictionary<string, int>();

                    int startCount = 0;
                    int.TryParse(startCountStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out startCount);
                    rec.StartCrew[name] = startCount;
                }

                // Parse end fields (if present)
                string endCountStr = traits[i].GetValue("endCount");
                if (endCountStr != null)
                {
                    if (rec.EndCrew == null)
                        rec.EndCrew = new Dictionary<string, int>();

                    int endCount = 0;
                    int.TryParse(endCountStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out endCount);
                    rec.EndCrew[name] = endCount;
                }

                loaded++;
            }

            ParsekLog.Verbose("RecordingStore",
                $"DeserializeCrewManifest: loaded={loaded} skipped={skipped} for recording={rec.RecordingId}");
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
                // Bug #278 follow-up (PR #177, defense-in-depth): do NOT delete an
                // existing _vessel.craft when in-memory VesselSnapshot is null. PR
                // #176's #278 fix routes FinalizePendingLimboTreeForRevert through
                // FinalizeIndividualRecording per leaf, which still hits the
                // defensive null at ParsekFlight.cs:5810 ("rec.VesselSnapshot = null"
                // when the vessel pid lookup fails) for vessel-gone debris. The
                // auto-unreserve-crew pass at ParsekScenario.cs:1131-1140 also nulls
                // the snapshot after the spawn window closes. Both leave the recording
                // with a transient in-memory null while the on-disk sidecar (written
                // earlier by PersistFinalizedRecording from PR #167's #280 fix) is
                // intact. The previous behavior — destructively delete the sidecar —
                // would race with these null-out sites and destroy persisted data on
                // the next OnSave. Stale-cleanup is the responsibility of explicit
                // recording-deletion paths (DeleteRecordingFiles), not of every save.

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

                rec.FilesDirty = false;
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

                DeserializeTrajectoryFrom(precNode, rec);

                // #288: eagerly populate TerminalOrbit cache from the last orbit segment if
                // the recording was loaded with empty cache fields. Without this, GhostMap
                // and other consumers see an empty TerminalOrbit cache and fail to create
                // map vessels for non-finalized recordings (forces the user to press W to
                // build the icon via WatchModeController, which reads OrbitSegments directly).
                if (string.IsNullOrEmpty(rec.TerminalOrbitBody)
                    && rec.OrbitSegments != null && rec.OrbitSegments.Count > 0)
                {
                    ParsekFlight.PopulateTerminalOrbitFromLastSegment(rec);
                    if (!string.IsNullOrEmpty(rec.TerminalOrbitBody))
                    {
                        Log($"[Parsek] Eager-populated TerminalOrbit for {rec.RecordingId} from last orbit segment (body={rec.TerminalOrbitBody}, sma={rec.TerminalOrbitSemiMajorAxis:F0})");
                    }
                }

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

                return true;
            }
            catch (Exception ex)
            {
                Log($"[Parsek] Failed to load recording files for {rec.RecordingId}: {ex.Message}");
                return false;
            }
        }

        private static void SafeWriteConfigNode(ConfigNode node, string path)
        {
            FileIOUtils.SafeWriteConfigNode(node, path, "RecordingStore");
        }

        #endregion
    }
}
