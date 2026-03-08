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
        public const int CurrentRecordingFormatVersion = 4;
        public const int CurrentGhostGeometryVersion = 1;

        // When true, suppresses logging calls (for unit testing outside Unity)
        internal static bool SuppressLogging;

        // Rewind flags (survive scene change via static fields)
        internal static bool IsRewinding;
        internal static double RewindUT;
        internal static ResourceBudget.BudgetSummary RewindReserved;

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
        /// Recommended merge action based on vessel state after recording.
        /// </summary>
        public enum MergeDefault
        {
            GhostOnly,  // Vessel destroyed or snapshot missing — merge recording only
            Persist      // Vessel intact with snapshot — respawn where it ended up
        }

        public class Recording
        {
            public string RecordingId = Guid.NewGuid().ToString("N");
            public int RecordingFormatVersion = CurrentRecordingFormatVersion;
            public int GhostGeometryVersion = CurrentGhostGeometryVersion;
            public List<TrajectoryPoint> Points = new List<TrajectoryPoint>();
            public List<OrbitSegment> OrbitSegments = new List<OrbitSegment>();
            public List<PartEvent> PartEvents = new List<PartEvent>();
            public bool LoopPlayback;
            public double LoopPauseSeconds = 10.0;

            // Atmosphere segment metadata
            public string SegmentPhase;      // "atmo" or "exo" (null = untagged/legacy)
            public string SegmentBodyName;   // body name at split point (e.g., "Kerbin", "Duna")
            public bool PlaybackEnabled = true;  // false = skip ghost during playback

            // EVA child recording linkage
            public string ParentRecordingId;
            public string EvaCrewName;

            // Chain linkage (multi-segment recording chains)
            public string ChainId;       // null = standalone; shared GUID for chain members
            public int ChainIndex = -1;  // -1 = not chained; 0-based position within chain
            public int ChainBranch;      // 0 = primary path; >0 = parallel continuation (ghost-only, no spawn)
            public string VesselName = "";
            public string GhostGeometryRelativePath;
            public bool GhostGeometryAvailable;
            public string GhostGeometryCaptureError;
            public string GhostGeometryCaptureStrategy = "stub_v1";
            public string GhostGeometryProbeStatus = "uninitialized";

            // --- Tree linkage (null for legacy/standalone recordings) ---
            public string TreeId;                          // null = standalone (pre-tree recording)
            public uint VesselPersistentId;                // 0 = not set

            // --- Terminal state ---
            public TerminalState? TerminalStateValue;      // null = not yet terminated (still recording or legacy)

            // Terminal orbit (for Orbiting/SubOrbital terminal state)
            // Stored as Keplerian elements to avoid runtime Orbit object dependency in tests.
            public double TerminalOrbitInclination;
            public double TerminalOrbitEccentricity;
            public double TerminalOrbitSemiMajorAxis;
            public double TerminalOrbitLAN;
            public double TerminalOrbitArgumentOfPeriapsis;
            public double TerminalOrbitMeanAnomalyAtEpoch;
            public double TerminalOrbitEpoch;
            public string TerminalOrbitBody;

            // Terminal surface position (for Landed/Splashed terminal state)
            public SurfacePosition? TerminalPosition;      // null if not landed/splashed

            // Background recording: surface position for landed/splashed vessels
            public SurfacePosition? SurfacePos;            // null if not a background landed vessel

            // Branch linkage
            public string ParentBranchPointId;             // null for root recording
            public string ChildBranchPointId;              // null for leaf recordings

            // Explicit UT range for recordings that may have no trajectory points
            // (background-only recordings). When Points.Count > 0, these are ignored
            // in favor of Points[0].ut / Points[last].ut.
            // Default is double.NaN (not set). 0.0 is a valid KSP UT.
            public double ExplicitStartUT = double.NaN;
            public double ExplicitEndUT = double.NaN;

            // Cached recording statistics (transient, recomputed on demand).
            // Tracks point count at cache time so continuation (which appends
            // points after commit) automatically invalidates the cache.
            internal RecordingStats? CachedStats;
            internal int CachedStatsPointCount;

            // Pre-launch resource snapshot (captured before recording starts)
            public double PreLaunchFunds;
            public double PreLaunchScience;
            public float PreLaunchReputation;

            // Rewind save (quicksave captured at recording start, stored in Parsek/Saves/)
            public string RewindSaveFileName;
            public double RewindReservedFunds;
            public double RewindReservedScience;
            public float RewindReservedRep;

            // Tracks which point's resource deltas have been applied during playback.
            // -1 means no resources applied yet (start from point 0's delta).
            public int LastAppliedResourceIndex = -1;

            // Vessel persistence fields (transient — only needed between revert and merge dialog)
            public ConfigNode VesselSnapshot;       // ProtoVessel as ConfigNode (null if destroyed)
            public ConfigNode GhostVisualSnapshot;  // Snapshot used for ghost visuals (prefer recording-start state)
            public double DistanceFromLaunch;       // Meters from launch position
            public bool VesselDestroyed;            // Vessel was destroyed before revert
            public string VesselSituation;          // "Orbiting Kerbin", "Landed on Mun", etc.
            public double MaxDistanceFromLaunch;     // Peak distance reached during recording
            public bool VesselSpawned;              // True after deferred RespawnVessel has fired
            public bool TakenControl;               // True after player took control of ghost mid-playback
            public uint SpawnedVesselPersistentId;  // persistentId of spawned vessel (0 = not yet spawned)
            public int SpawnAttempts;               // Number of failed spawn attempts (give up after 3)
            public int SceneExitSituation = -1;     // Vessel.Situations at scene exit (-1 = still in flight/unknown)

            public double StartUT => Points.Count > 0 ? Points[0].ut :
                                     !double.IsNaN(ExplicitStartUT) ? ExplicitStartUT : 0.0;
            public double EndUT => Points.Count > 0 ? Points[Points.Count - 1].ut :
                                   !double.IsNaN(ExplicitEndUT) ? ExplicitEndUT : 0.0;

            /// <summary>
            /// Copies persistence/capture artifacts from a stop-time captured recording.
            /// Intentionally does NOT copy Points/OrbitSegments/VesselName, which are
            /// set by StashPending from the current recorder buffers.
            /// </summary>
            public void ApplyPersistenceArtifactsFrom(Recording source)
            {
                if (source == null) return;

                VesselSnapshot = source.VesselSnapshot != null
                    ? source.VesselSnapshot.CreateCopy()
                    : null;
                GhostVisualSnapshot = source.GhostVisualSnapshot != null
                    ? source.GhostVisualSnapshot.CreateCopy()
                    : null;
                RecordingId = source.RecordingId;
                DistanceFromLaunch = source.DistanceFromLaunch;
                VesselDestroyed = source.VesselDestroyed;
                VesselSituation = source.VesselSituation;
                MaxDistanceFromLaunch = source.MaxDistanceFromLaunch;
                GhostGeometryRelativePath = source.GhostGeometryRelativePath;
                GhostGeometryAvailable = source.GhostGeometryAvailable;
                GhostGeometryCaptureError = source.GhostGeometryCaptureError;
                GhostGeometryCaptureStrategy = source.GhostGeometryCaptureStrategy;
                GhostGeometryProbeStatus = source.GhostGeometryProbeStatus;
                RecordingFormatVersion = source.RecordingFormatVersion;
                GhostGeometryVersion = source.GhostGeometryVersion;
                ParentRecordingId = source.ParentRecordingId;
                EvaCrewName = source.EvaCrewName;
                ChainId = source.ChainId;
                ChainIndex = source.ChainIndex;
                ChainBranch = source.ChainBranch;
                LoopPlayback = source.LoopPlayback;
                LoopPauseSeconds = source.LoopPauseSeconds;
                PreLaunchFunds = source.PreLaunchFunds;
                PreLaunchScience = source.PreLaunchScience;
                PreLaunchReputation = source.PreLaunchReputation;
                RewindSaveFileName = source.RewindSaveFileName;
                RewindReservedFunds = source.RewindReservedFunds;
                RewindReservedScience = source.RewindReservedScience;
                RewindReservedRep = source.RewindReservedRep;
                SegmentPhase = source.SegmentPhase;
                SegmentBodyName = source.SegmentBodyName;
                PlaybackEnabled = source.PlaybackEnabled;
                TreeId = source.TreeId;
                VesselPersistentId = source.VesselPersistentId;
                TerminalStateValue = source.TerminalStateValue;
                TerminalOrbitInclination = source.TerminalOrbitInclination;
                TerminalOrbitEccentricity = source.TerminalOrbitEccentricity;
                TerminalOrbitSemiMajorAxis = source.TerminalOrbitSemiMajorAxis;
                TerminalOrbitLAN = source.TerminalOrbitLAN;
                TerminalOrbitArgumentOfPeriapsis = source.TerminalOrbitArgumentOfPeriapsis;
                TerminalOrbitMeanAnomalyAtEpoch = source.TerminalOrbitMeanAnomalyAtEpoch;
                TerminalOrbitEpoch = source.TerminalOrbitEpoch;
                TerminalOrbitBody = source.TerminalOrbitBody;
                TerminalPosition = source.TerminalPosition;
                SurfacePos = source.SurfacePos;
                ParentBranchPointId = source.ParentBranchPointId;
                ChildBranchPointId = source.ChildBranchPointId;
                ExplicitStartUT = source.ExplicitStartUT;
                ExplicitEndUT = source.ExplicitEndUT;
            }
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

        // Merged to timeline — these auto-playback during flight
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
            int? ghostGeometryVersion = null,
            List<PartEvent> partEvents = null)
        {
            if (points == null || points.Count < 2)
            {
                Log($"[Parsek] Recording too short for '{vesselName}' ({points?.Count ?? 0} points, need >= 2) — discarded");
                return;
            }

            pendingRecording = new Recording
            {
                RecordingId = string.IsNullOrEmpty(recordingId) ? Guid.NewGuid().ToString("N") : recordingId,
                RecordingFormatVersion = recordingFormatVersion ?? CurrentRecordingFormatVersion,
                GhostGeometryVersion = ghostGeometryVersion ?? CurrentGhostGeometryVersion,
                Points = new List<TrajectoryPoint>(points),
                OrbitSegments = orbitSegments != null
                    ? new List<OrbitSegment>(orbitSegments)
                    : new List<OrbitSegment>(),
                PartEvents = partEvents != null
                    ? new List<PartEvent>(partEvents)
                    : new List<PartEvent>(),
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

            // Delete rewind save file if present
            if (!string.IsNullOrEmpty(pendingRecording.RewindSaveFileName))
            {
                DeleteFileIfExists(RecordingPaths.BuildRewindSaveRelativePath(pendingRecording.RewindSaveFileName));
                Log($"[Parsek] Deleted rewind save for discarded recording: {pendingRecording.RewindSaveFileName}");
            }

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
                    return;
                }
            }

            // Add all tree recordings to committedRecordings (enables ghost playback)
            foreach (var rec in tree.Recordings.Values)
            {
                committedRecordings.Add(rec);
            }

            committedTrees.Add(tree);
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

            // Delete rewind save for root recording if present
            foreach (var rec in pendingTree.Recordings.Values)
            {
                if (!string.IsNullOrEmpty(rec.RewindSaveFileName))
                {
                    DeleteFileIfExists(RecordingPaths.BuildRewindSaveRelativePath(rec.RewindSaveFileName));
                    Log($"[Parsek] Deleted rewind save for discarded tree: {rec.RewindSaveFileName}");
                    break; // only root owns the rewind save
                }
            }

            foreach (var rec in pendingTree.Recordings.Values)
                DeleteRecordingFiles(rec);
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
            RewindReserved = default(ResourceBudget.BudgetSummary);
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
                    File.Delete(absolutePath);
            }
            catch (Exception ex)
            {
                Log($"[Parsek] WARNING: Failed to delete file '{relativePath}': {ex.Message}");
            }
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
            rec.TakenControl = false;
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
                return false;
            }

            if (string.IsNullOrEmpty(rec.RewindSaveFileName))
            {
                reason = "No rewind save available";
                return false;
            }

            if (isRecording)
            {
                reason = "Stop recording before rewinding";
                return false;
            }

            if (HasPending)
            {
                reason = "Merge or discard pending recording first";
                return false;
            }

            if (HasPendingTree)
            {
                reason = "Merge or discard pending tree first";
                return false;
            }

            // Verify the save file exists
            string savePath = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildRewindSaveRelativePath(rec.RewindSaveFileName));
            if (string.IsNullOrEmpty(savePath) || !File.Exists(savePath))
            {
                reason = "Rewind save file missing";
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

            if (!SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Rewind initiated to UT {rec.StartUT} (save: {rec.RewindSaveFileName})");

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
                // 1. Remove only the recorded vessel (other vessels stay intact)
                // 2. Wind back UT by 10 seconds so the player can reach the pad before launch
                const double rewindLeadTime = 10.0;
                PreProcessRewindSave(tempPath, rec.VesselName, rewindLeadTime);

                Game game = GamePersistence.LoadGame(tempCopyName, HighLogic.SaveFolder, true, false);

                // Delete the temp copy (file already parsed into Game object)
                try { File.Delete(tempPath); }
                catch { }

                if (game == null)
                {
                    IsRewinding = false;
                    RewindUT = 0;
                    RewindReserved = default(ResourceBudget.BudgetSummary);
                    if (!SuppressLogging)
                        ParsekLog.Error("Rewind",
                            $"Rewind failed: LoadGame returned null for save '{rec.RewindSaveFileName}'");
                    return;
                }

                // Load into SpaceCenter — player can enter buildings or launch a new vessel
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
                RewindReserved = default(ResourceBudget.BudgetSummary);

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
                    ParsekLog.Error("Rewind", $"Rewind failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Modifies the temp rewind save file before KSP parses it:
        /// removes the recorded vessel and winds back UT so the player
        /// has time to reach the launch pad before the ghost appears.
        /// </summary>
        private static void PreProcessRewindSave(string sfsPath, string vesselName, double leadTime)
        {
            ConfigNode root = ConfigNode.Load(sfsPath);
            if (root == null)
                return;

            // The file contents are directly inside the returned node (no GAME wrapper)
            ConfigNode gameNode = root.HasNode("GAME") ? root.GetNode("GAME") : root;

            ConfigNode flightState = gameNode.GetNode("FLIGHTSTATE");
            if (flightState != null)
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

                // Remove only the recorded vessel — all other vessels stay intact
                int removed = 0;
                var vesselNodes = flightState.GetNodes("VESSEL");
                for (int i = vesselNodes.Length - 1; i >= 0; i--)
                {
                    string name = vesselNodes[i].GetValue("name");
                    if (name == vesselName)
                    {
                        flightState.RemoveNode(vesselNodes[i]);
                        removed++;
                    }
                }
                if (!SuppressLogging)
                    ParsekLog.Info("Rewind",
                        $"Stripped {removed} vessel(s) named '{vesselName}' from save");
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
        }

        internal static void DeserializeTrajectoryFrom(ConfigNode sourceNode, Recording rec)
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

                rec.OrbitSegments.Add(seg);
            }

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
                        Log($"[Parsek] WARNING: Unknown PartEvent type id '{typeInt}' in recording {rec.RecordingId}");
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
                precNode.AddValue("version", CurrentRecordingFormatVersion);
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
