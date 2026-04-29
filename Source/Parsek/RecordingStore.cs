using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        /// Default behavior — this is what <see cref="RecordingStore.StashPendingTree"/>
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
        public const int LaunchToLaunchLoopIntervalFormatVersion = 4;
        public const int PredictedOrbitSegmentFormatVersion = 5;
        public const int RelativeLocalFrameFormatVersion = 6;
        public const int RelativeAbsoluteShadowFormatVersion = 7;
        public const int BoundarySeamFlagFormatVersion = 8;
        public const int CurrentRecordingFormatVersion = BoundarySeamFlagFormatVersion;

        /// <summary>
        /// Top-level group name for ghost-only recordings created via the Gloops Flight Recorder.
        /// </summary>
        internal const string GloopsGroupName = "Gloops - Ghosts Only";

        /// <summary>
        /// Pre-PR-328 group name. Recordings loaded from disk carrying this string are
        /// transparently renamed to <see cref="GloopsGroupName"/> on load (see
        /// <c>RecordingTree.LoadRecordingFrom</c>) and the file is marked dirty so the
        /// rename persists on the next save.
        /// </summary>
        internal const string LegacyGloopsGroupName = "Gloops Flight Recordings - Ghosts Only";

        /// <summary>
        /// Amount of pre-launch setup time rewind-to-launch restores before the launch UT.
        /// </summary>
        internal const double RewindToLaunchLeadTimeSeconds = 15.0;
        // v0: initial release format
        // v1: track sections become authoritative on disk when present; flat lists rebuild on load
        // v2: binary .prec sidecars with header dispatch, exact scalar storage, and file-level string tables
        // v3: binary .prec sparse point defaults for stable body/career fields, still exact on load
        // v4: loopIntervalSeconds serialized as launch-to-launch period; older saves stored post-cycle gap
        // v5: OrbitSegment.isPredicted serialized in text and binary trajectory codecs
        // v6: RELATIVE TrackSection points store anchor-local offsets and anchor-local rotation
        // v7: RELATIVE TrackSections also store absolute planet-relative shadow points
        // v8: TrackSection.isBoundarySeam flag for the Producer-C no-payload boundary seam — mandatory
        //     binary bump because TrajectorySidecarBinary.WriteTrackSections is positional; legacy v7
        //     readers see a default-false flag (`v < 8` skips the byte). See
        //     docs/dev/plans/optimizer-persistence-split.md §5.3.

        internal static bool UsesRelativeLocalFrameContract(int recordingFormatVersion)
        {
            return recordingFormatVersion >= RelativeLocalFrameFormatVersion;
        }

        internal static string DescribeRelativeFrameContract(int recordingFormatVersion)
        {
            return UsesRelativeLocalFrameContract(recordingFormatVersion)
                ? "anchor-local"
                : "legacy-world";
        }

        // When true, suppresses logging calls (for unit testing outside Unity)
        internal static bool SuppressLogging;
        internal static bool? WriteReadableSidecarMirrorsOverrideForTesting;

        // Rewind-to-Staging Phase 1 (design section 9): batch counter for the
        // one-shot legacy migration log. Each RecordingTree.LoadRecordingFrom pass
        // that promotes a legacy `committed = True/False` bool to MergeState tri-state
        // bumps this counter; the scenario load emits a single Info line with the total.
        internal static int LegacyMergeStateMigrationCount;
        // Flag: one-shot log has been emitted for the current session. Flipped on first
        // emission; reset by ResetForTesting and by EmitLegacyMergeStateMigrationLogOnce.
        private static bool legacyMergeStateMigrationLogEmitted;

        internal static void BumpLegacyMergeStateMigrationCounterForTesting()
        {
            LegacyMergeStateMigrationCount++;
        }

        /// <summary>
        /// Emits the one-shot <c>[Recording] Legacy migration:</c> Info log summarising
        /// how many recordings were promoted from the binary <c>committed</c> bool to
        /// the <see cref="Parsek.MergeState"/> tri-state this session. Idempotent: a
        /// second call is a no-op. Counter is NOT reset so repeated loads within a
        /// session (e.g. tests asserting idempotence) do not double-count.
        /// </summary>
        internal static void EmitLegacyMergeStateMigrationLogOnce()
        {
            if (legacyMergeStateMigrationLogEmitted) return;
            if (LegacyMergeStateMigrationCount <= 0) return;
            ParsekLog.Info("Recording",
                $"Legacy migration: {LegacyMergeStateMigrationCount} recordings mapped from committed-bool to MergeState tri-state");
            legacyMergeStateMigrationLogEmitted = true;
        }

        internal static void ResetLegacyMergeStateMigrationForTesting()
        {
            LegacyMergeStateMigrationCount = 0;
            legacyMergeStateMigrationLogEmitted = false;
        }
        // Auto-assigned-standalone-group tracking has two storage locations:
        //   1. Recording.AutoAssignedStandaloneGroupName (authoritative, persisted
        //      via RecordingTree save/load as `autoAssignedStandaloneGroup`).
        //   2. autoAssignedStandaloneGroupsByRecordingId (static, in-memory only).
        // The dict exists so test recordings without a persisted field (built via
        // RecordingBuilder / direct .Add calls) still resolve through
        // TryGetAutoAssignedStandaloneGroup. Invariant: every mutation goes through
        // MarkAutoAssignedStandaloneGroup / ClearAutoAssignedStandaloneGroup so the
        // field and dict stay aligned. Do NOT write to either directly from outside
        // these helpers (Recording.cs field deserialization is the one exception
        // and is reconciled by TryGet's first branch).
        private static readonly Dictionary<string, string> autoAssignedStandaloneGroupsByRecordingId =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // PID of the active vessel at scene entry. Used by SpawnVesselOrChainTip to
        // bypass PID dedup statelessly — if a recording's VesselPersistentId matches
        // this, the existing real vessel is the player's reverted/active vessel, not
        // a previously-spawned endpoint. Static so it survives Recording object recreation.
        internal static uint SceneEntryActiveVesselPid;

        // During launch-point rewind, scope the #226 duplicate-source bypass to the
        // recording whose rewind was actually requested. Other scene-entry vessels may
        // also match committed recordings, but they are not the replay target.
        internal static uint RewindReplayTargetSourcePid;
        internal static string RewindReplayTargetRecordingId;

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

        internal static void Log(string message)
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

        internal static bool ConfigNodesEquivalent(ConfigNode expected, ConfigNode actual)
        {
            if (ReferenceEquals(expected, actual))
                return true;
            if (expected == null || actual == null)
                return false;
            if (!string.Equals(expected.name, actual.name, StringComparison.Ordinal))
                return false;
            if (expected.values.Count != actual.values.Count)
                return false;
            if (expected.nodes.Count != actual.nodes.Count)
                return false;

            for (int i = 0; i < expected.values.Count; i++)
            {
                if (!string.Equals(expected.values[i].name, actual.values[i].name, StringComparison.Ordinal) ||
                    !string.Equals(expected.values[i].value, actual.values[i].value, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            for (int i = 0; i < expected.nodes.Count; i++)
            {
                if (!ConfigNodesEquivalent(expected.nodes[i], actual.nodes[i]))
                    return false;
            }

            return true;
        }

        internal static GhostSnapshotMode DetermineGhostSnapshotMode(Recording rec)
        {
            if (rec == null)
                return GhostSnapshotMode.Unspecified;

            if (rec.GhostVisualSnapshot == null)
            {
                return rec.VesselSnapshot != null
                    ? GhostSnapshotMode.AliasVessel
                    : GhostSnapshotMode.Unspecified;
            }

            if (rec.VesselSnapshot == null)
                return GhostSnapshotMode.Separate;

            return ConfigNodesEquivalent(rec.VesselSnapshot, rec.GhostVisualSnapshot)
                ? GhostSnapshotMode.AliasVessel
                : GhostSnapshotMode.Separate;
        }

        internal static GhostSnapshotMode GetExpectedGhostSnapshotMode(Recording rec)
        {
            if (rec == null)
                return GhostSnapshotMode.Unspecified;

            return rec.GhostSnapshotMode != GhostSnapshotMode.Unspecified
                ? rec.GhostSnapshotMode
                : DetermineGhostSnapshotMode(rec);
        }

        internal static GhostSnapshotMode ParseGhostSnapshotMode(string modeValue)
        {
            if (string.IsNullOrEmpty(modeValue))
                return GhostSnapshotMode.Unspecified;

            GhostSnapshotMode parsed;
            if (Enum.TryParse(modeValue, ignoreCase: false, result: out parsed) &&
                Enum.IsDefined(typeof(GhostSnapshotMode), parsed))
            {
                return parsed;
            }

            if (!SuppressLogging)
            {
                ParsekLog.Warn("RecordingStore",
                    $"LoadRecordingFiles: invalid ghostSnapshotMode '{modeValue}', treating as Unspecified");
            }
            return GhostSnapshotMode.Unspecified;
        }

        // Set true by StashPendingTree during OnSceneChangeRequested.
        // Checked by ParsekScenario.OnLoad to distinguish a freshly-stashed pending
        // from the current scene transition (keep it long enough for revert-vs-
        // quickload-vs-non-flight dispatch) from a stale pending left over from a
        // previous flight (discard per #64).
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
        internal static string CleanOrphanFilesDirectoryOverrideForTesting;

        public static IReadOnlyList<Recording> CommittedRecordings => committedRecordings;
        public static List<RecordingTree> CommittedTrees => committedTrees;
        public static bool HasPendingTree => pendingTree != null;
        public static RecordingTree PendingTree => pendingTree;
        public static PendingTreeState PendingTreeStateValue => pendingTreeState;

        // Phase 2 (Rewind-to-Staging): state-version counter consumed by
        // <see cref="EffectiveState"/> to invalidate the ERS cache. Every code
        // path that mutates <see cref="committedRecordings"/> MUST call
        // <see cref="BumpStateVersion"/>; the mutating internal helpers below
        // already do so, so callers that route through them get the bump for
        // free.
        internal static int StateVersion;

        /// <summary>
        /// Bumps <see cref="StateVersion"/>. Called whenever
        /// <see cref="committedRecordings"/> is mutated so the
        /// <see cref="EffectiveState"/> ERS cache knows to rebuild.
        /// </summary>
        internal static void BumpStateVersion()
        {
            unchecked { StateVersion++; }
        }

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
                    ? Recording.DeepCopyTrackSections(trackSections)
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
            BumpStateVersion();
            Log($"[Parsek] Committed recording from {rec.VesselName} " +
                $"({rec.Points.Count} points). Total committed: {committedRecordings.Count}");
            ParsekLog.Verbose("RecordingStore", $"CommitRecordingDirect: {rec.DebugName}");

            // Flush to disk immediately to close the crash window.
            FlushDirtyFiles(committedRecordings);

            // Capture a game state baseline at each commit (single funnel point)
            GameStateStore.CaptureBaselineIfNeeded();

            // Create a milestone bundling game state events since the previous milestone
            MilestoneStore.CreateMilestone(rec.RecordingId, rec.EndUT);
        }

        /// <summary>
        /// Commits a ghost-only Gloops recording: adds to committed list, flushes to disk,
        /// but skips game-state side effects (science subjects, milestones, baselines) since
        /// ghost-only recordings do not affect game state.
        /// </summary>
        internal static void CommitGloopsRecording(Recording rec)
        {
            if (rec == null)
            {
                ParsekLog.Warn("RecordingStore", "CommitGloopsRecording called with null recording");
                return;
            }

            rec.IsGhostOnly = true;
            rec.FilesDirty = true;

            // Assign to Gloops group
            if (rec.RecordingGroups == null)
                rec.RecordingGroups = new List<string>();
            if (!rec.RecordingGroups.Contains(GloopsGroupName))
                rec.RecordingGroups.Add(GloopsGroupName);

            committedRecordings.Add(rec);
            BumpStateVersion();
            FlushDirtyFiles(committedRecordings);

            ParsekLog.Info("RecordingStore",
                $"Committed Gloops ghost-only recording \"{rec.VesselName}\" " +
                $"({rec.Points.Count} points, id={rec.RecordingId}). " +
                $"Total committed: {committedRecordings.Count}");
        }

        /// <summary>
        /// Adds a recording to the internal committed list without flushing or side effects.
        /// For production code paths (e.g., undock continuation) that need direct list access
        /// after CommittedRecordings became IReadOnlyList.
        /// </summary>
        internal static void AddCommittedInternal(Recording rec)
        {
            committedRecordings.Add(rec);
            BumpStateVersion();
        }

        /// <summary>
        /// Phase 6 of Rewind-to-Staging (design §6.3 step 4 phase 1): adds a
        /// provisional re-fly recording to the committed list in the same
        /// synchronous block as the <see cref="ReFlySessionMarker"/> write.
        /// Bumps <see cref="StateVersion"/> so the ERS cache invalidates
        /// immediately; no disk flush (the provisional is durable via the
        /// scenario save, not via sidecar files, until it is merged).
        /// </summary>
        internal static void AddProvisional(Recording rec)
        {
            if (rec == null)
            {
                ParsekLog.Warn("RecordingStore", "AddProvisional called with null recording");
                return;
            }
            committedRecordings.Add(rec);
            BumpStateVersion();
            ParsekLog.Verbose("RecordingStore",
                $"AddProvisional: rec={rec.RecordingId} state={rec.MergeState} " +
                $"supersedeTarget={rec.SupersedeTargetId ?? "<none>"} " +
                $"total={committedRecordings.Count}");
        }

        /// <summary>
        /// Removes a recording from the internal committed list.
        /// For production code that needs mutation after CommittedRecordings became IReadOnlyList.
        /// </summary>
        internal static bool RemoveCommittedInternal(Recording rec)
        {
            bool removed = committedRecordings.Remove(rec);
            if (removed)
                BumpStateVersion();
            return removed;
        }

        /// <summary>
        /// Removes a committed tree by id, including its recordings from the flat
        /// committed list, so a live restore can take ownership without duplicate-id
        /// collisions or "still committed" semantics.
        /// </summary>
        internal static bool RemoveCommittedTreeById(string treeId, string logContext = null)
        {
            if (string.IsNullOrEmpty(treeId))
                return false;

            bool removed = false;
            for (int i = committedTrees.Count - 1; i >= 0; i--)
            {
                if (committedTrees[i].Id != treeId)
                    continue;

                RecordingTree stale = committedTrees[i];
                foreach (Recording rec in stale.Recordings.Values)
                    RemoveCommittedInternal(rec);

                committedTrees.RemoveAt(i);
                removed = true;
                ParsekLog.Info("RecordingStore",
                    $"{logContext ?? "RemoveCommittedTreeById"}: removed committed tree " +
                    $"'{stale.TreeName}' (id={treeId}, {stale.Recordings.Count} recording(s))");
            }

            return removed;
        }

        /// <summary>
        /// Clears all recordings from the internal committed list.
        /// For production code that needs mutation after CommittedRecordings became IReadOnlyList.
        /// </summary>
        internal static void ClearCommittedInternal()
        {
            committedRecordings.Clear();
            BumpStateVersion();
        }

        /// <summary>
        /// Phase 6 of Rewind-to-Staging (design §6.4 reconciliation table):
        /// adds a tree to <see cref="committedTrees"/> without re-running
        /// <see cref="FinalizeTreeCommit"/>'s full commit pipeline (no session
        /// merge, no group assignment, no milestone creation). The bundle
        /// restore path uses this to re-install pre-load trees verbatim after
        /// a scene reload wiped the parallel list; the trees' recordings are
        /// re-installed via <see cref="AddCommittedInternal"/> in the same
        /// restore pass.
        /// </summary>
        internal static void AddCommittedTreeInternal(RecordingTree tree)
        {
            if (tree == null) return;
            committedTrees.Add(tree);
            BumpStateVersion();
        }

        /// <summary>
        /// Phase 6 of Rewind-to-Staging: companion to <see cref="AddCommittedTreeInternal"/>.
        /// Clears <see cref="committedTrees"/> without touching the parallel
        /// <see cref="committedRecordings"/> list — the bundle-restore caller
        /// manages both lists in lockstep.
        /// </summary>
        internal static void ClearCommittedTreesInternal()
        {
            committedTrees.Clear();
            BumpStateVersion();
        }

        public static void ClearCommitted()
        {
            int count = committedRecordings.Count;
            for (int i = 0; i < committedRecordings.Count; i++)
                DeleteRecordingFiles(committedRecordings[i]);
            committedRecordings.Clear();
            committedTrees.Clear();
            ClearRewindReplayTargetScope();
            BumpStateVersion();
            GroupHierarchyStore.PruneUnusedHierarchyEntriesFromCommittedRecordings("clear-committed");
            GameStateRecorder.PendingScienceSubjects.Clear();
            Log($"[Parsek] Cleared {count} committed recordings and all trees");
        }

        public static void Clear()
        {
            pendingTree = null;
            pendingTreeState = PendingTreeState.Finalized;
            ClearCommitted();
            ClearRewindReplayTargetScope();
            Log("[Parsek] All recordings cleared");
        }

        internal static void SetRewindReplayTargetScope(Recording owner)
        {
            // NOTE: This scope must survive the committed-list rebuild inside
            // ParsekScenario.OnLoad; only real commit/discard/clear/reset paths
            // should clear it, or #565's replay-target filter loses its owner.
            RewindReplayTargetSourcePid = owner != null ? owner.VesselPersistentId : 0u;
            RewindReplayTargetRecordingId = owner != null ? owner.RecordingId : null;
            if (RewindReplayTargetSourcePid != 0)
            {
                ParsekLog.Verbose("Rewind",
                    $"Rewind replay duplicate scope armed: rec={RewindReplayTargetRecordingId ?? "<null>"} " +
                    $"sourcePid={RewindReplayTargetSourcePid.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        internal static void ClearRewindReplayTargetScope()
        {
            RewindReplayTargetSourcePid = 0;
            RewindReplayTargetRecordingId = null;
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
                    ClearRewindReplayTargetScope();
                    return;
                }
            }

            ApplySessionMergeToRecordings(tree);
            ApplyRewindProvisionalMergeStates(tree);
            PromoteNormalStagingRewindPoints(tree);
            AutoGroupTreeRecordings(tree);
            AdoptOrphanedRecordingsIntoTreeGroup(tree);
            MarkSupersededTerminalSpawnsForContinuedSources(tree);
            FinalizeTreeCommit(tree);
            ClearRewindReplayTargetScope();
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
        /// A child under a Rewind Point that qualifies as an unfinished flight
        /// is committed, but its rewind slot stays open until a later successful
        /// re-fly or explicit Seal closes it. Legacy/default recordings are
        /// born Immutable, so stamp that precise shape during the normal tree
        /// commit path.
        /// </summary>
        private static void ApplyRewindProvisionalMergeStates(RecordingTree tree)
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
                return;
            if (tree.BranchPoints == null || tree.BranchPoints.Count == 0)
                return;

            int promoted = 0;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec == null) continue;
                if (rec.MergeState != MergeState.Immutable) continue;
                if (!UnfinishedFlightClassifier.IsUnfinishedFlightCandidateShape(rec, tree)) continue;

                RewindPoint rp;
                int slotListIndex;
                string slotRejectReason;
                if (!UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                        rec, out rp, out slotListIndex, out slotRejectReason))
                {
                    ParsekLog.Verbose("UnfinishedFlights",
                        $"CommitTree: RP child rec={rec.RecordingId ?? "<no-id>"} " +
                        $"vessel='{rec.VesselName ?? "<unnamed>"}' not promoted reason={slotRejectReason} " +
                        $"parentBp={rec.ParentBranchPointId ?? "<none>"} childBp={rec.ChildBranchPointId ?? "<none>"}");
                    continue;
                }

                if (rp.ChildSlots == null || slotListIndex < 0 || slotListIndex >= rp.ChildSlots.Count)
                    continue;

                var slot = rp.ChildSlots[slotListIndex];
                string qualifyReason;
                if (!UnfinishedFlightClassifier.TryQualify(
                        rec, slot, rp, true, out qualifyReason, tree))
                {
                    ParsekLog.Verbose("UnfinishedFlights",
                        $"CommitTree: RP child rec={rec.RecordingId ?? "<no-id>"} " +
                        $"vessel='{rec.VesselName ?? "<unnamed>"}' not promoted reason={qualifyReason} " +
                        $"rp={rp.RewindPointId ?? "<no-rp>"} slot={slotListIndex}");
                    continue;
                }

                rec.MergeState = MergeState.CommittedProvisional;
                rec.FilesDirty = true;
                promoted++;
                ParsekLog.Info("UnfinishedFlights",
                    $"CommitTree promoted rec={rec.RecordingId ?? "<no-id>"} " +
                    $"vessel='{rec.VesselName ?? "<unnamed>"}' slot={slotListIndex} " +
                    $"rp={rp.RewindPointId ?? "<no-rp>"} reason={qualifyReason} " +
                    $"to CommittedProvisional");
            }

            if (promoted > 0)
                BumpStateVersion();
        }

        private static BranchPoint FindBranchPointById(RecordingTree tree, string branchPointId)
        {
            if (tree == null || string.IsNullOrEmpty(branchPointId) || tree.BranchPoints == null)
                return null;

            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                var bp = tree.BranchPoints[i];
                if (bp == null) continue;
                if (string.Equals(bp.Id, branchPointId, StringComparison.Ordinal))
                    return bp;
            }

            return null;
        }

        private static bool HasRewindPointSlotForRecording(
            Recording rec,
            string rewindPointId,
            out string rejectReason)
        {
            rejectReason = null;

            if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
            {
                rejectReason = "recording-id-missing";
                return false;
            }

            if (string.IsNullOrEmpty(rewindPointId))
            {
                rejectReason = "rewind-point-id-missing";
                return false;
            }

            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario) || scenario.RewindPoints == null)
            {
                rejectReason = "scenario-rewind-points-unavailable";
                return false;
            }

            IReadOnlyList<RecordingSupersedeRelation> supersedes = scenario.RecordingSupersedes
                ?? (IReadOnlyList<RecordingSupersedeRelation>)Array.Empty<RecordingSupersedeRelation>();
            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                var rp = scenario.RewindPoints[i];
                if (rp == null) continue;
                if (!string.Equals(rp.RewindPointId, rewindPointId, StringComparison.Ordinal))
                    continue;

                int slotListIndex = EffectiveState.ResolveRewindPointSlotIndexForRecording(rp, rec, supersedes);
                if (slotListIndex >= 0)
                    return true;

                rejectReason = "rewind-point-slot-not-found";
                return false;
            }

            rejectReason = "rewind-point-not-found";
            return false;
        }

        /// <summary>
        /// Rewind Points captured during ordinary staging are born
        /// SessionProvisional with no CreatingSessionId. They must survive the
        /// scene load that presents the merge dialog, but once the owning tree
        /// is accepted they are persistent timeline artifacts and the reaper
        /// may delete them when every slot resolves Immutable.
        /// </summary>
        private static void PromoteNormalStagingRewindPoints(RecordingTree tree)
        {
            if (tree == null || tree.BranchPoints == null || tree.BranchPoints.Count == 0)
                return;

            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario) || scenario.RewindPoints == null)
                return;

            HashSet<string> treeRpIds = null;
            for (int b = 0; b < tree.BranchPoints.Count; b++)
            {
                var bp = tree.BranchPoints[b];
                if (bp == null || string.IsNullOrEmpty(bp.RewindPointId)) continue;
                if (treeRpIds == null)
                    treeRpIds = new HashSet<string>(StringComparer.Ordinal);
                treeRpIds.Add(bp.RewindPointId);
            }
            if (treeRpIds == null || treeRpIds.Count == 0)
                return;

            int promoted = 0;
            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                var rp = scenario.RewindPoints[i];
                if (rp == null) continue;
                if (!rp.SessionProvisional) continue;
                if (!string.IsNullOrEmpty(rp.CreatingSessionId)) continue;
                if (string.IsNullOrEmpty(rp.RewindPointId)) continue;
                if (!treeRpIds.Contains(rp.RewindPointId)) continue;

                rp.SessionProvisional = false;
                rp.CreatingSessionId = null;
                promoted++;
                ParsekLog.Info("Rewind",
                    $"CommitTree: promoted normal staging rp={rp.RewindPointId} " +
                    $"tree={tree.Id ?? "<no-tree>"} to persistent");
            }

            if (promoted > 0)
                ParsekLog.Info("Rewind",
                    $"CommitTree: promoted {promoted.ToString(CultureInfo.InvariantCulture)} normal staging RP(s)");
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
            int crewCount = 0;
            string debrisGroupName = null;
            string crewGroupName = null;
            tree.AutoGeneratedRootGroupName = groupName;
            tree.AutoGeneratedDebrisGroupName = null;
            tree.AutoGeneratedCrewGroupName = null;

            foreach (var rec in tree.Recordings.Values)
            {
                string targetGroup = groupName;
                if (rec.IsDebris)
                {
                    // Create debris subgroup on first debris recording
                    if (debrisGroupName == null)
                    {
                        debrisGroupName = groupName + " / Debris";
                        GroupHierarchyStore.SetGroupParent(debrisGroupName, groupName);
                    }
                    targetGroup = debrisGroupName;
                    tree.AutoGeneratedDebrisGroupName = debrisGroupName;
                    debrisCount++;
                }
                else if (!string.IsNullOrEmpty(rec.EvaCrewName))
                {
                    // EVA recordings belong under a dedicated crew subgroup for the mission.
                    if (crewGroupName == null)
                    {
                        crewGroupName = groupName + " / Crew";
                        GroupHierarchyStore.SetGroupParent(crewGroupName, groupName);
                    }
                    targetGroup = crewGroupName;
                    tree.AutoGeneratedCrewGroupName = crewGroupName;
                    crewCount++;
                }

                if (rec.RecordingGroups == null)
                    rec.RecordingGroups = new List<string>();
                if (!rec.RecordingGroups.Contains(targetGroup))
                    rec.RecordingGroups.Add(targetGroup);
                ClearAutoAssignedStandaloneGroup(rec);
            }

            int mainCount = tree.Recordings.Count - debrisCount - crewCount;
            if (debrisCount > 0 || crewCount > 0)
            {
                var details = new List<string>();
                if (mainCount > 0)
                    details.Add($"{mainCount} main under '{groupName}'");
                if (crewCount > 0)
                    details.Add($"{crewCount} crew under '{crewGroupName}'");
                if (debrisCount > 0)
                    details.Add($"{debrisCount} debris under '{debrisGroupName}'");
                ParsekLog.Info("RecordingStore",
                    $"Auto-grouped {string.Join(", ", details)}");
            }
            else
            {
                ParsekLog.Info("RecordingStore",
                    $"Auto-grouped {tree.Recordings.Count} recordings under '{groupName}'");
            }
        }

        private static string ResolveTreeRootGroupName(RecordingTree tree)
        {
            if (tree == null) return null;

            foreach (var rec in tree.Recordings.Values)
            {
                if (rec.RecordingGroups == null || rec.RecordingGroups.Count == 0)
                    continue;

                string groupName = rec.RecordingGroups[0];
                string parent;
                while (GroupHierarchyStore.TryGetGroupParent(groupName, out parent))
                    groupName = parent;
                return groupName;
            }

            return null;
        }

        internal static bool IsAutoGeneratedTreeGroup(string groupName)
        {
            if (string.IsNullOrEmpty(groupName))
                return false;

            for (int i = 0; i < committedTrees.Count; i++)
            {
                var tree = committedTrees[i];
                EnsureAutoGeneratedTreeGroups(tree);
                if (string.IsNullOrEmpty(tree.AutoGeneratedRootGroupName))
                    continue;

                if (string.Equals(groupName, tree.AutoGeneratedRootGroupName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(groupName, tree.AutoGeneratedDebrisGroupName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(groupName, tree.AutoGeneratedCrewGroupName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        internal static bool IsPermanentRootGroup(string groupName)
        {
            return string.Equals(groupName, GloopsGroupName, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsPermanentGroup(string groupName)
        {
            return IsPermanentRootGroup(groupName)
                || IsAutoGeneratedTreeGroup(groupName);
        }

        private static void EnsureAutoGeneratedTreeGroups(RecordingTree tree)
        {
            if (tree == null || !string.IsNullOrEmpty(tree.AutoGeneratedRootGroupName))
                return;

            string rootGroupName, debrisGroupName, crewGroupName;
            if (TryInferAutoGeneratedTreeGroups(tree,
                out rootGroupName, out debrisGroupName, out crewGroupName))
            {
                tree.AutoGeneratedRootGroupName = rootGroupName;
                tree.AutoGeneratedDebrisGroupName = debrisGroupName;
                tree.AutoGeneratedCrewGroupName = crewGroupName;
                // Best-effort migration from pre-#265 saves: RecordingGroups[0] is
                // assumed to be the user's primary group. If the user reordered
                // groups before the fix landed, the inference can pick a wrong
                // primary — log loud so a misinference is easy to spot in KSP.log.
                ParsekLog.Info("RecordingStore",
                    $"EnsureAutoGeneratedTreeGroups: inferred groups for tree '{tree.TreeName}' " +
                    $"(root='{rootGroupName ?? "null"}', debris='{debrisGroupName ?? "null"}', " +
                    $"crew='{crewGroupName ?? "null"}') from first-group heuristic");
            }
        }

        private static bool TryInferAutoGeneratedTreeGroups(RecordingTree tree,
            out string rootGroupName, out string debrisGroupName, out string crewGroupName)
        {
            rootGroupName = null;
            debrisGroupName = null;
            crewGroupName = null;

            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
                return false;

            Recording rootRec;
            if (!string.IsNullOrEmpty(tree.RootRecordingId)
                && tree.Recordings.TryGetValue(tree.RootRecordingId, out rootRec))
            {
                rootGroupName = GetPrimaryGroup(rootRec);
            }

            foreach (var rec in tree.Recordings.Values)
            {
                string primaryGroup = GetPrimaryGroup(rec);
                if (string.IsNullOrEmpty(primaryGroup))
                    continue;

                if (string.IsNullOrEmpty(rootGroupName)
                    && !rec.IsDebris
                    && string.IsNullOrEmpty(rec.EvaCrewName))
                {
                    rootGroupName = primaryGroup;
                }

                if (rec.IsDebris
                    && string.IsNullOrEmpty(debrisGroupName)
                    && !string.Equals(primaryGroup, rootGroupName, StringComparison.OrdinalIgnoreCase))
                    debrisGroupName = primaryGroup;

                if (!string.IsNullOrEmpty(rec.EvaCrewName)
                    && string.IsNullOrEmpty(crewGroupName)
                    && !string.Equals(primaryGroup, rootGroupName, StringComparison.OrdinalIgnoreCase))
                    crewGroupName = primaryGroup;
            }

            return !string.IsNullOrEmpty(rootGroupName);
        }

        private static string GetPrimaryGroup(Recording rec)
        {
            if (rec == null || rec.RecordingGroups == null || rec.RecordingGroups.Count == 0)
                return null;
            return rec.RecordingGroups[0];
        }

        private static string ResolveAdoptedTreeGroup(string rootGroupName, Recording rec)
        {
            if (string.IsNullOrEmpty(rootGroupName) || rec == null)
                return rootGroupName;

            if (rec.IsDebris)
            {
                string debrisGroupName = rootGroupName + " / Debris";
                GroupHierarchyStore.SetGroupParent(debrisGroupName, rootGroupName);
                return debrisGroupName;
            }

            if (!string.IsNullOrEmpty(rec.EvaCrewName))
            {
                string crewGroupName = rootGroupName + " / Crew";
                GroupHierarchyStore.SetGroupParent(crewGroupName, rootGroupName);
                return crewGroupName;
            }

            return rootGroupName;
        }

        private static bool GroupBelongsToRootHierarchy(string groupName, string rootGroupName)
        {
            if (string.IsNullOrEmpty(groupName) || string.IsNullOrEmpty(rootGroupName))
                return false;

            string current = groupName;
            while (!string.IsNullOrEmpty(current))
            {
                if (string.Equals(current, rootGroupName, StringComparison.OrdinalIgnoreCase))
                    return true;

                string parent;
                if (!GroupHierarchyStore.TryGetGroupParent(current, out parent))
                    break;
                current = parent;
            }

            return false;
        }

        internal static void MarkAutoAssignedStandaloneGroup(Recording rec, string groupName)
        {
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId) || IsInvalidGroupName(groupName))
                return;

            rec.AutoAssignedStandaloneGroupName = groupName;
            autoAssignedStandaloneGroupsByRecordingId[rec.RecordingId] = groupName;
        }

        /// <summary>
        /// Clears the auto-assigned-standalone-group marker. Must be called from
        /// every group-mutation entry point (AddRecordingToGroup, RemoveRecordingFromGroup,
        /// AddChainToGroup, RemoveChainFromGroup, RenameGroup, RemoveGroupFromAll,
        /// ReplaceGroupOnAll). If a new group-mutation path is added, call this here
        /// too — otherwise a manual edit will silently get re-adopted on the next
        /// tree commit (bug #265 / #376).
        /// </summary>
        private static void ClearAutoAssignedStandaloneGroup(Recording rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return;

            rec.AutoAssignedStandaloneGroupName = null;
            autoAssignedStandaloneGroupsByRecordingId.Remove(rec.RecordingId);
        }

        private static bool TryGetAutoAssignedStandaloneGroup(Recording rec, out string groupName)
        {
            groupName = null;
            if (rec == null)
                return false;

            if (!string.IsNullOrEmpty(rec.AutoAssignedStandaloneGroupName))
            {
                groupName = rec.AutoAssignedStandaloneGroupName;
                if (!string.IsNullOrEmpty(rec.RecordingId))
                    autoAssignedStandaloneGroupsByRecordingId[rec.RecordingId] = groupName;
                return true;
            }

            if (string.IsNullOrEmpty(rec.RecordingId))
                return false;

            return autoAssignedStandaloneGroupsByRecordingId.TryGetValue(rec.RecordingId, out groupName);
        }

        private static bool ShouldAdoptRecordingIntoTreeGroup(Recording rec, string rootGroupName)
        {
            if (rec == null)
                return false;

            if (rec.RecordingGroups == null || rec.RecordingGroups.Count == 0)
                return true;

            for (int i = 0; i < rec.RecordingGroups.Count; i++)
            {
                if (GroupBelongsToRootHierarchy(rec.RecordingGroups[i], rootGroupName))
                    return false;
            }

            if (rec.RecordingGroups.Count != 1 || GroupHierarchyStore.HasGroupParent(rec.RecordingGroups[0]))
                return false;

            string autoGroupName;
            if (!TryGetAutoAssignedStandaloneGroup(rec, out autoGroupName))
                return false;

            return string.Equals(rec.RecordingGroups[0], autoGroupName, StringComparison.Ordinal);
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

            string rootGroupName = ResolveTreeRootGroupName(tree);
            if (rootGroupName == null)
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

                bool match = cr.TreeId == tree.Id;
                if (!match && treePids.Contains(cr.VesselPersistentId)
                    && cr.StartUT >= treeStartUT - 60 && cr.EndUT <= treeEndUT + 60)
                    match = true;

                if (!match) continue;
                if (!ShouldAdoptRecordingIntoTreeGroup(cr, rootGroupName)) continue;

                string targetGroup = ResolveAdoptedTreeGroup(rootGroupName, cr);
                cr.RecordingGroups = new List<string> { targetGroup };
                ClearAutoAssignedStandaloneGroup(cr);
                adopted++;
                ParsekLog.Info("RecordingStore",
                    $"Adopted orphaned recording '{cr.VesselName}' (id={cr.RecordingId}) into group '{targetGroup}'");
            }
            if (adopted > 0)
                ParsekLog.Info("RecordingStore",
                    $"Adopted {adopted} orphaned recording(s) into tree group '{rootGroupName}'");
        }

        /// <summary>
        /// When a newly committed tree continues a vessel that was previously
        /// materialized from another recording, the older recording is an intermediate
        /// endpoint. Its ghost should still play, but its terminal real-vessel spawn
        /// must be suppressed so the later continuation owns the final vessel spawn.
        /// </summary>
        internal static int MarkSupersededTerminalSpawnsForContinuedSources(
            RecordingTree tree,
            string logContext = "CommitTree")
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
                return 0;
            if (committedRecordings == null || committedRecordings.Count == 0)
                return 0;

            double treeStartUT = GetTreeStartUT(tree);
            int marked = 0;
            foreach (Recording continued in tree.Recordings.Values)
            {
                if (continued == null || continued.VesselPersistentId == 0)
                    continue;
                if (string.IsNullOrEmpty(continued.RecordingId))
                    continue;

                for (int i = 0; i < committedRecordings.Count; i++)
                {
                    Recording prior = committedRecordings[i];
                    if (prior == null || ReferenceEquals(prior, continued))
                        continue;
                    if (string.Equals(prior.RecordingId, continued.RecordingId, StringComparison.Ordinal))
                        continue;
                    if (!string.IsNullOrEmpty(prior.TerminalSpawnSupersededByRecordingId))
                        continue;
                    if (!ShouldMarkSupersededTerminalSpawn(
                            prior,
                            continued,
                            treeStartUT,
                            out string matchReason))
                        continue;

                    prior.TerminalSpawnSupersededByRecordingId = continued.RecordingId;
                    prior.FilesDirty = true;
                    marked++;
                    ParsekLog.Info("RecordingStore",
                        string.Format(CultureInfo.InvariantCulture,
                            "{0}: terminal spawn for recording '{1}' vessel='{2}' " +
                            "superseded by continuation '{3}' vesselPid={4} reason={5}",
                            logContext ?? "MarkSupersededTerminalSpawns",
                            prior.RecordingId ?? "<no-id>",
                            prior.VesselName ?? "<unnamed>",
                            continued.RecordingId,
                            continued.VesselPersistentId,
                            matchReason ?? "unknown"));
                }
            }

            if (marked > 0)
                BumpStateVersion();
            return marked;
        }

        internal static int MarkSupersededTerminalSpawnsForCommittedContinuations(
            string logContext = "CommittedContinuationRepair")
        {
            if (committedTrees == null || committedTrees.Count == 0)
                return 0;

            int marked = 0;
            for (int i = 0; i < committedTrees.Count; i++)
                marked += MarkSupersededTerminalSpawnsForContinuedSources(
                    committedTrees[i],
                    logContext);

            return marked;
        }

        private static bool ShouldMarkSupersededTerminalSpawn(
            Recording prior,
            Recording continued,
            double continuationTreeStartUT,
            out string reason)
        {
            reason = null;
            if (prior == null || continued == null)
                return false;
            if (continued.VesselPersistentId == 0)
                return false;
            if (prior.SpawnedVesselPersistentId == 0)
                return false;
            if (prior.EndUT > continued.EndUT + 1e-3)
                return false;

            if (prior.SpawnedVesselPersistentId == continued.VesselPersistentId)
            {
                reason = "spawned-pid-match";
                return true;
            }

            if (string.Equals(prior.TreeId, continued.TreeId, StringComparison.Ordinal))
                return false;
            if (continued.TreeOrder <= 0)
                return false;
            if (double.IsNaN(continuationTreeStartUT))
                return false;
            if (continuationTreeStartUT > prior.EndUT + 1e-3)
                return false;
            if (prior.EndUT > continued.StartUT + 1e-3)
                return false;
            if (!string.Equals(prior.VesselName, continued.VesselName, StringComparison.Ordinal))
                return false;

            reason = "same-name-overlapping-continuation-tree";
            return true;
        }

        private static double GetTreeStartUT(RecordingTree tree)
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
                return double.NaN;

            double min = double.PositiveInfinity;
            foreach (Recording rec in tree.Recordings.Values)
            {
                if (rec == null)
                    continue;
                double startUT = rec.StartUT;
                if (startUT < min)
                    min = startUT;
            }

            return double.IsPositiveInfinity(min) ? double.NaN : min;
        }

        /// <summary>
        /// Adds tree recordings to committed list, flushes to disk, rebuilds background map,
        /// captures baseline, and creates a milestone.
        /// </summary>
        private static void FinalizeTreeCommit(RecordingTree tree)
        {
            // Add all tree recordings to committedRecordings (enables ghost playback).
            // Skip recordings already present (chain segments committed mid-flight
            // by CommitRecordingDirect).
            int addedFromTree = 0;
            foreach (var rec in tree.Recordings.Values)
            {
                rec.FilesDirty = true;
                if (committedRecordings.Contains(rec)) continue;
                committedRecordings.Add(rec);
                addedFromTree++;
            }
            if (addedFromTree > 0)
                BumpStateVersion();

            // Flush to disk immediately to close the crash window.
            // If RunOptimizationPass runs after this, it will re-dirty modified
            // recordings and flush again with the final optimized state.
            FlushDirtyFiles(committedRecordings);

            // Ensure RecordedVesselPids is populated (covers runtime-created trees
            // that never went through RecordingTree.Load)
            tree.RebuildBackgroundMap();

            committedTrees.Add(tree);

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
        /// #431: also purges every <see cref="GameStateEvent"/> tagged with one of the tree's
        /// recording ids — both from the live store and from any milestone the flush-on-save
        /// path may have already moved them into. Contract snapshots orphaned by the purge
        /// (i.e. whose accept event was among the purged set) are removed too.
        /// </summary>
        public static void DiscardPendingTree()
        {
            if (pendingTree == null)
            {
                ParsekLog.Verbose("RecordingStore", "DiscardPendingTree called with no pending tree");
                return;
            }

            // Phase 11 of Rewind-to-Staging (design §3.5 invariant 7 / §6.10):
            // tree discard is the ONLY purge path for RewindPoints, supersede
            // relations, and ledger tombstones whose endpoints tie back to
            // the discarded tree. Runs BEFORE the recording list mutations
            // below so the purge can still resolve ids -> Recording /
            // GameAction for in-tree classification.
            TreeDiscardPurge.PurgeTree(pendingTree.Id);

            // #431: collect every recording id in the tree and purge tagged events first.
            // Runs before file deletion so a later failure in DeleteRecordingFiles still
            // leaves the event store in the correct post-discard shape.
            var idsToPurge = new HashSet<string>();
            foreach (var rec in pendingTree.Recordings.Values)
                if (!string.IsNullOrEmpty(rec.RecordingId))
                    idsToPurge.Add(rec.RecordingId);
            if (idsToPurge.Count > 0)
                GameStateStore.PurgeEventsForRecordings(idsToPurge, $"DiscardPendingTree '{pendingTree.TreeName}'");

            foreach (var rec in pendingTree.Recordings.Values)
                DeleteRecordingFiles(rec);
            GameStateRecorder.PendingScienceSubjects.Clear();
            Log($"[Parsek] Discarded pending tree '{pendingTree.TreeName}' (state={pendingTreeState})");
            pendingTree = null;
            pendingTreeState = PendingTreeState.Finalized;
            ClearRewindReplayTargetScope();
        }

        /// <summary>
        /// #434: "soft" unstash for the revert path. Clears the pending-tree slot WITHOUT
        /// deleting sidecar files or purging tagged events. Rationale: KSP's revert preserves
        /// persistent.sfs and sidecar files on both revert-to-launch (cache-driven, never
        /// touches disk) and revert-to-VAB/SPH (persistent.sfs rewritten with prelaunch state,
        /// but sidecars untouched). A player who F5'd during the flight and later F9s that
        /// quicksave must find the Parsek recording files still there; the ACTIVE_TREE node
        /// in that quicksave's persistent.sfs references sidecar files by recording id, which
        /// would dangle if we deleted them.
        ///
        /// Event-store staleness after revert is handled by the recording-id visibility
        /// filter: tagged events stay hidden until a matching quickload restores the
        /// active tree. <see cref="CleanOrphanFiles"/> at the next cold-start reclaims
        /// sidecars no quicksave still references.
        ///
        /// Contrast with <see cref="DiscardPendingTree"/>, which runs the full #431 purge +
        /// file deletion for the merge-dialog Discard button's explicit "throw it away" choice.
        /// </summary>
        public static void UnstashPendingTreeOnRevert()
        {
            if (pendingTree == null)
            {
                ParsekLog.Verbose("RecordingStore", "UnstashPendingTreeOnRevert called with no pending tree");
                // P2 fix: clear science subjects even when there's no pending tree — the
                // in-flight recorder may have captured subjects that were never stashed.
                int strayBefore = GameStateRecorder.PendingScienceSubjects.Count;
                if (strayBefore > 0)
                {
                    GameStateRecorder.PendingScienceSubjects.Clear();
                    ParsekLog.Info("RecordingStore",
                        $"UnstashPendingTreeOnRevert: cleared {strayBefore} in-flight science subject(s) even with no pending tree");
                }
                return;
            }

            string treeName = pendingTree.TreeName;
            var prevState = pendingTreeState;
            int recCount = pendingTree.Recordings.Count;

            // P2 fix: GameStateRecorder.PendingScienceSubjects is a static in-memory list
            // that is neither epoch-tagged nor serialized. If we don't clear it here, the
            // next committed recording's NotifyLedgerTreeCommitted will flush these stale
            // subjects onto an unrelated mission. F9-from-flight-quicksave semantics don't
            // lose anything — the list wasn't in the quicksave anyway, so no regression vs.
            // the pre-#434 discard path (which cleared this list unconditionally).
            int subjectsCleared = GameStateRecorder.PendingScienceSubjects.Count;
            GameStateRecorder.PendingScienceSubjects.Clear();

            pendingTree = null;
            pendingTreeState = PendingTreeState.Finalized;
            Log($"[Parsek] Unstashed pending tree '{treeName}' on revert " +
                $"(was state={prevState}, {recCount} recording(s), {subjectsCleared} pending science subject(s) cleared): " +
                "sidecar files preserved for F9-from-flight-quicksave; " +
                "events stay in-memory and on-disk, filtered by recording-id visibility");
        }

        internal static bool IsPendingRecordingId(string recordingId)
        {
            return !string.IsNullOrEmpty(recordingId)
                && pendingTree != null
                && pendingTree.Recordings != null
                && pendingTree.Recordings.ContainsKey(recordingId);
        }

        /// <summary>
        /// True when <paramref name="recordingId"/> belongs to the current live timeline:
        /// a committed recording that survives ERS supersede/session-suppression filtering,
        /// the pending tree, or the active in-flight tree being recorded/resumed.
        /// This replaces the old epoch gate for "is this branch still live?" decisions.
        /// </summary>
        internal static bool IsCurrentTimelineRecordingId(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return false;
            if (IsPendingRecordingId(recordingId) || ParsekFlight.IsActiveTreeRecordingId(recordingId))
                return true;

            var ers = EffectiveState.ComputeERS();
            for (int i = 0; i < ers.Count; i++)
            {
                if (string.Equals(ers[i].RecordingId, recordingId, StringComparison.Ordinal))
                    return true;
            }

            return false;
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

            int removed = 0;
            for (int i = committedRecordings.Count - 1; i >= 0; i--)
            {
                if (committedRecordings[i].ChainId == chainId)
                {
                    DeleteRecordingFiles(committedRecordings[i]);
                    Log($"[Parsek] Removed chain recording: {committedRecordings[i].VesselName} (chain={chainId}, idx={committedRecordings[i].ChainIndex})");
                    committedRecordings.RemoveAt(i);
                    removed++;
                }
            }
            if (removed > 0)
                BumpStateVersion();
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
            BumpStateVersion();
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
        /// Returns true if any branch-0 segment in the chain has LoopPlayback set.
        /// Note: deliberately does not check PlaybackEnabled (bug #433). Whether the
        /// chain is looping is a career-state property — it determines if the vessel
        /// spawns at chain tip. Hiding the ghost visual must not change that answer.
        /// </summary>
        internal static bool IsChainLooping(string chainId)
        {
            if (string.IsNullOrEmpty(chainId)) return false;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (rec.ChainId == chainId && rec.ChainBranch == 0 &&
                    rec.LoopPlayback)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns a human-readable phase label like "Kerbin atmo" or "exo".
        /// Returns empty string for untagged/legacy recordings.
        /// </summary>
        internal static string GetSegmentPhaseLabel(Recording rec)
        {
            if (ShouldSuppressEvaBoundaryPhaseLabel(rec))
            {
                string body = rec.SegmentBodyName;
                if (string.IsNullOrEmpty(body) && rec.Points != null && rec.Points.Count > 0)
                    body = rec.Points[rec.Points.Count - 1].bodyName;
                if (string.IsNullOrEmpty(body))
                    body = rec.StartBodyName;
                return body ?? "";
            }

            if (string.IsNullOrEmpty(rec.SegmentPhase)) return "";
            if (!string.IsNullOrEmpty(rec.SegmentBodyName))
                return rec.SegmentBodyName + " " + rec.SegmentPhase;
            return rec.SegmentPhase;
        }

        internal static bool ShouldSuppressEvaBoundaryPhaseLabel(Recording rec)
        {
            if (rec == null
                || string.IsNullOrEmpty(rec.EvaCrewName)
                || rec.TrackSections == null
                || rec.TrackSections.Count < 2)
            {
                return false;
            }

            bool sawAtmo = false;
            bool sawSurface = false;
            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                int envClass = RecordingOptimizer.SplitEnvironmentClass(rec.TrackSections[i].environment);
                switch (envClass)
                {
                    case 0:
                        sawAtmo = true;
                        break;
                    case 2:
                        sawSurface = true;
                        break;
                    default:
                        return false;
                }
            }

            return sawAtmo && sawSurface;
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

            int mergeCount = RunOptimizationMergePass(recordings);
            int splitCount = RunOptimizationSplitPass(recordings);
            TrimBoringTailsForOptimization(recordings);

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

        private static int RunOptimizationMergePass(List<Recording> recordings)
        {
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
                UpdateTreeStateAfterOptimizationMerge(target, absorbed);

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

            return mergeCount;
        }

        private static int RunOptimizationSplitPass(List<Recording> recordings)
        {
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
                second.CreatingSessionId = original.CreatingSessionId;
                second.ProvisionalForRpId = original.ProvisionalForRpId;
                second.SupersedeTargetId = original.SupersedeTargetId;

                // Derive SegmentBodyName from trajectory points
                if (original.Points != null && original.Points.Count > 0)
                    original.SegmentBodyName = original.Points[0].bodyName;
                if (second.Points != null && second.Points.Count > 0)
                    second.SegmentBodyName = second.Points[0].bodyName;

                // BranchPoint linkage: ChildBranchPointId moves to the half whose
                // time range owns the branch point. Older code always moved it to
                // the second half, assuming every optimizer split precedes branchUT.
                // Re-Fly atmo/exo splits can happen after a staging branch; moving
                // that branch would make a BP at UT 116 point at a segment starting
                // around UT 170 and corrupt parent-chain topology.
                //
                // NOTE: The parent BranchPoint's ChildRecordingIds still references
                // original.RecordingId (now the first chain segment). This is correct —
                // the first segment IS the direct child of that BP. The chain linkage
                // (shared ChainId) connects it to subsequent segments. Code that walks
                // from a BranchPoint to the chain tip must follow ChainId, not just
                // ChildRecordingIds.
                string movedChildBranchPointId = null;
                bool childBranchPointMovesToSecond = ShouldMoveChildBranchPointToSplitSecondHalf(
                    original.TreeId,
                    original.ChildBranchPointId,
                    second.StartUT);
                if (childBranchPointMovesToSecond)
                {
                    movedChildBranchPointId = original.ChildBranchPointId;
                    second.ChildBranchPointId = original.ChildBranchPointId;
                    original.ChildBranchPointId = null;
                }
                else
                {
                    second.ChildBranchPointId = null;
                }
                // Do NOT set second.ParentRecordingId — that field is for EVA linkage only

                // Update BranchPoint.ParentRecordingIds when ChildBranchPointId moves to new half
                if (!string.IsNullOrEmpty(movedChildBranchPointId) && !string.IsNullOrEmpty(original.TreeId))
                {
                    for (int t = 0; t < committedTrees.Count; t++)
                    {
                        if (committedTrees[t].Id != original.TreeId) continue;
                        var tree = committedTrees[t];
                        if (tree.BranchPoints != null)
                        {
                            for (int b = 0; b < tree.BranchPoints.Count; b++)
                            {
                                if (tree.BranchPoints[b].Id == movedChildBranchPointId
                                    && tree.BranchPoints[b].ParentRecordingIds != null)
                                {
                                    var parentIds = tree.BranchPoints[b].ParentRecordingIds;
                                    for (int p = 0; p < parentIds.Count; p++)
                                    {
                                        if (parentIds[p] == original.RecordingId)
                                        {
                                            parentIds[p] = second.RecordingId;
                                            ParsekLog.Verbose("RecordingStore",
                                                $"Split: updated BranchPoint '{movedChildBranchPointId}' " +
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
                            committedTrees[t].AddOrReplaceRecording(second);
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

            return splitCount;
        }

        internal static bool ShouldMoveChildBranchPointToSplitSecondHalf(
            string treeId,
            string childBranchPointId,
            double secondStartUT)
        {
            // Optimizer-only helper: RunOptimizationPass operates on committed
            // trees, so this intentionally does not inspect PendingTree. If a
            // future pending-tree split path appears, add an explicit tree
            // parameter rather than broadening this committed-tree contract.
            if (string.IsNullOrEmpty(treeId) || string.IsNullOrEmpty(childBranchPointId))
                return false;
            if (double.IsNaN(secondStartUT) || double.IsInfinity(secondStartUT))
                return false;

            const double eps = 0.0001;
            for (int t = 0; t < committedTrees.Count; t++)
            {
                var tree = committedTrees[t];
                if (tree == null || !string.Equals(tree.Id, treeId, StringComparison.Ordinal))
                    continue;
                if (tree.BranchPoints == null)
                    return false;
                for (int b = 0; b < tree.BranchPoints.Count; b++)
                {
                    var bp = tree.BranchPoints[b];
                    if (bp == null || !string.Equals(bp.Id, childBranchPointId, StringComparison.Ordinal))
                        continue;
                    return bp.UT >= secondStartUT - eps;
                }
                return false;
            }

            return false;
        }

        private static void TrimBoringTailsForOptimization(List<Recording> recordings)
        {
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

        private static void UpdateTreeStateAfterOptimizationMerge(Recording target, Recording absorbed)
        {
            string treeId = target != null && !string.IsNullOrEmpty(target.TreeId)
                ? target.TreeId
                : absorbed?.TreeId;
            if (string.IsNullOrEmpty(treeId) || absorbed == null)
                return;

            for (int t = 0; t < committedTrees.Count; t++)
            {
                var tree = committedTrees[t];
                if (tree.Id != treeId)
                    continue;

                tree.Recordings.Remove(absorbed.RecordingId);
                if (target != null)
                    tree.AddOrReplaceRecording(target);

                if (tree.RootRecordingId == absorbed.RecordingId && target != null)
                {
                    // Remap ledger actions tagged with the absorbed root id — otherwise
                    // Phase A LegacyMigration synthetics (and any other actions still
                    // tagged with the absorbed recording id) are orphaned on the next
                    // Ledger.Reconcile because the absorbed recording is about to be
                    // removed from the committed-recordings set. Handles round-2 P2
                    // from PR #347 external review.
                    int remapped = Ledger.RetagActionsForRecordingRewrite(
                        absorbed.RecordingId, target.RecordingId);
                    if (remapped > 0)
                        ParsekLog.Info("RecordingStore",
                            $"Optimization merge: retagged {remapped} ledger action(s) from " +
                            $"absorbed root '{absorbed.RecordingId}' to new root '{target.RecordingId}' " +
                            $"(tree id='{tree.Id}')");
                    tree.RootRecordingId = target.RecordingId;
                }
                if (tree.ActiveRecordingId == absorbed.RecordingId && target != null)
                    tree.ActiveRecordingId = target.RecordingId;

                if (!string.IsNullOrEmpty(absorbed.ChildBranchPointId) && tree.BranchPoints != null)
                {
                    for (int b = 0; b < tree.BranchPoints.Count; b++)
                    {
                        if (tree.BranchPoints[b].Id != absorbed.ChildBranchPointId
                            || tree.BranchPoints[b].ParentRecordingIds == null)
                        {
                            continue;
                        }

                        var parentIds = tree.BranchPoints[b].ParentRecordingIds;
                        for (int p = 0; p < parentIds.Count; p++)
                        {
                            if (parentIds[p] == absorbed.RecordingId && target != null)
                            {
                                parentIds[p] = target.RecordingId;
                                ParsekLog.Verbose("RecordingStore",
                                    $"Merge: updated BranchPoint '{absorbed.ChildBranchPointId}' " +
                                    $"ParentRecordingIds: {absorbed.RecordingId} → {target.RecordingId}");
                            }
                        }
                        break;
                    }
                }

                return;
            }
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
                ClearAutoAssignedStandaloneGroup(rec);
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
                ClearAutoAssignedStandaloneGroup(rec);
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
                    ClearAutoAssignedStandaloneGroup(rec);
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
                    ClearAutoAssignedStandaloneGroup(rec);
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

            if (IsPermanentRootGroup(oldName))
            {
                ParsekLog.Warn("RecordingStore", $"RenameGroup: cannot rename permanent root group '{oldName}'");
                return false;
            }

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
                        ClearAutoAssignedStandaloneGroup(committedRecordings[i]);
                        updated++;
                    }
                }
            }
            RenameAutoGeneratedTreeGroups(oldName, newName);
            ParsekLog.Info("RecordingStore", $"RenameGroup: '{oldName}' → '{newName}' ({updated} recordings updated)");
            return true;
        }

        private static void RenameAutoGeneratedTreeGroups(string oldName, string newName)
        {
            for (int i = 0; i < committedTrees.Count; i++)
            {
                var tree = committedTrees[i];
                EnsureAutoGeneratedTreeGroups(tree);

                if (string.Equals(tree.AutoGeneratedRootGroupName, oldName, StringComparison.OrdinalIgnoreCase))
                    tree.AutoGeneratedRootGroupName = newName;
                if (string.Equals(tree.AutoGeneratedDebrisGroupName, oldName, StringComparison.OrdinalIgnoreCase))
                    tree.AutoGeneratedDebrisGroupName = newName;
                if (string.Equals(tree.AutoGeneratedCrewGroupName, oldName, StringComparison.OrdinalIgnoreCase))
                    tree.AutoGeneratedCrewGroupName = newName;
            }
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
                    ClearAutoAssignedStandaloneGroup(rec);
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
                ClearAutoAssignedStandaloneGroup(rec);
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
            BumpStateVersion();
            autoAssignedStandaloneGroupsByRecordingId.Clear();
            pendingTree = null;
            pendingTreeState = PendingTreeState.Finalized;
            CleanOrphanFilesDirectoryOverrideForTesting = null;
            WriteReadableSidecarMirrorsOverrideForTesting = null;
            SceneEntryActiveVesselPid = 0;
            ClearRewindReplayTargetScope();
            RewindContext.ResetForTesting();
            RewindUTAdjustmentPending = false;
            GameStateRecorder.PendingScienceSubjects.Clear();
            PendingCleanupPids = null;
            PendingCleanupNames = null;
            PendingStashedThisTransition = false;
            ResetLegacyMergeStateMigrationForTesting();
        }

        /// <summary>
        /// #431: sets <see cref="PendingTreeStateValue"/> directly. For unit tests only —
        /// production always transitions state via <see cref="StashPendingTree"/>,
        /// <see cref="CommitPendingTree"/>, <see cref="DiscardPendingTree"/>, or the limbo-stash
        /// paths. Used by the LimboVesselSwitch tag-fallback test.
        /// </summary>
        internal static void SetPendingTreeStateForTesting(PendingTreeState state)
        {
            pendingTreeState = state;
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
                tree.AddOrReplaceRecording(rec);
                committedTrees.Add(tree);
            }
            committedRecordings.Add(rec);
            BumpStateVersion();
        }

        internal static void MarkAutoAssignedStandaloneGroupForTesting(Recording rec, string groupName)
        {
            MarkAutoAssignedStandaloneGroup(rec, groupName);
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
            // .pann annotation sidecar (design doc §17.3.1): regenerable cache,
            // but a stale file left behind after a recording is deleted could
            // be mis-cached against a future same-id recovery. Belongs in the
            // delete-path AND in RecordingFileSuffixes for orphan cleanup.
            DeleteFileIfExists(RecordingPaths.BuildAnnotationsRelativePath(rec.RecordingId));
            DeleteFileIfExists(RecordingPaths.BuildReadableTrajectoryMirrorRelativePath(rec.RecordingId));
            DeleteFileIfExists(RecordingPaths.BuildReadableVesselSnapshotMirrorRelativePath(rec.RecordingId));
            DeleteFileIfExists(RecordingPaths.BuildReadableGhostSnapshotMirrorRelativePath(rec.RecordingId));

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
        private static readonly string[] RecordingFileSuffixes =
        {
            ".prec",
            "_vessel.craft",
            "_ghost.craft",
            ".prec.txt",
            "_vessel.craft.txt",
            "_ghost.craft.txt",
            ".pann",
        };

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

        internal static bool IsTransientSidecarArtifactFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return false;

            for (int i = 0; i < RecordingFileSuffixes.Length; i++)
            {
                string suffix = RecordingFileSuffixes[i];
                if (fileName.EndsWith(suffix + ".tmp", StringComparison.OrdinalIgnoreCase)
                    || fileName.IndexOf(suffix + ".stage.", StringComparison.OrdinalIgnoreCase) >= 0
                    || fileName.IndexOf(suffix + ".bak.", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Builds the set of all recording IDs that are currently known to the store
        /// (committed recordings, committed trees, and the pending tree). Used by
        /// <see cref="CleanOrphanFiles"/> to decide which sidecar files to keep.
        /// Extracted as <c>internal static</c> for direct testability (#290).
        /// </summary>
        internal static HashSet<string> BuildKnownRecordingIds()
        {
            return BuildKnownRecordingIds(out _);
        }

        /// <summary>
        /// Overload that also reports how many IDs came from the pending tree
        /// (for diagnostic logging in <see cref="CleanOrphanFiles"/>).
        /// </summary>
        internal static HashSet<string> BuildKnownRecordingIds(out int pendingTreeIdCount)
        {
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
            pendingTreeIdCount = 0;
            if (pendingTree != null)
            {
                foreach (var kvp in pendingTree.Recordings)
                {
                    if (!string.IsNullOrEmpty(kvp.Value.RecordingId))
                    {
                        knownIds.Add(kvp.Value.RecordingId);
                        pendingTreeIdCount++;
                    }
                }
            }
            return knownIds;
        }

        /// <summary>
        /// Deletes orphaned sidecar files in the Parsek/Recordings/ directory that don't
        /// correspond to any known recording ID. Called after all recordings and trees are loaded.
        /// </summary>
        internal static void CleanOrphanFiles()
        {
            // Resolve without creating — don't create an empty directory just to scan it
            string recordingsDir;
            if (!string.IsNullOrEmpty(CleanOrphanFilesDirectoryOverrideForTesting))
            {
                recordingsDir = Path.GetFullPath(CleanOrphanFilesDirectoryOverrideForTesting);
            }
            else
            {
                string root = KSPUtil.ApplicationRootPath ?? "";
                string saveFolder = HighLogic.SaveFolder ?? "";
                if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveFolder))
                {
                    ParsekLog.Verbose("RecordingStore", "CleanOrphanFiles: no save context — skipping");
                    return;
                }
                recordingsDir = Path.GetFullPath(Path.Combine(root, "saves", saveFolder, "Parsek", "Recordings"));
            }
            if (!Directory.Exists(recordingsDir))
            {
                ParsekLog.Verbose("RecordingStore", "CleanOrphanFiles: no recordings directory — skipping");
                return;
            }

            // Build set of known recording IDs from committed + pending state.
            // On cold-start resume, TryRestoreActiveTreeNode stashes the active
            // tree into pendingTree BEFORE this method runs. Without including
            // pendingTree, branch recordings (debris, EVA) would be deleted as
            // orphans, silently degrading to 0 points on the next cold start (#290).
            var knownIds = BuildKnownRecordingIds(out int pendingTreeIds);

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
                $"CleanOrphanFiles: scanning {files.Length} file(s) against {knownIds.Count} known recording ID(s)" +
                (pendingTreeIds > 0 ? $" (incl. {pendingTreeIds} from pending tree)" : ""));

            int orphanCount = 0;
            int legacyCount = 0;
            int transientCount = 0;
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
                    if (IsTransientSidecarArtifactFile(fileName))
                    {
                        try
                        {
                            File.Delete(files[i]);
                            transientCount++;
                            ParsekLog.Verbose("RecordingStore", $"Deleted transient sidecar artifact: {fileName}");
                        }
                        catch (Exception ex)
                        {
                            ParsekLog.Warn("RecordingStore",
                                $"Failed to delete transient sidecar artifact '{fileName}': {ex.Message}");
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

            if (orphanCount > 0 || legacyCount > 0 || transientCount > 0)
                ParsekLog.Info("RecordingStore",
                    $"Cleaned {orphanCount} orphaned recording file(s)" +
                    (legacyCount > 0 ? $", {legacyCount} legacy sidecar file(s)" : "") +
                    (transientCount > 0 ? $", {transientCount} transient sidecar artifact(s)" : "") +
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
            MarkSupersededTerminalSpawnsForCommittedContinuations("ResetAllPlaybackState");

            for (int i = 0; i < committedRecordings.Count; i++)
                ResetRecordingPlaybackFields(committedRecordings[i]);

            for (int i = 0; i < committedTrees.Count; i++)
            {
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
        /// revert/rewind paths (ResetRecordingPlaybackFields,
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
            // SpawnSuppressedByRewind is cleared here so a subsequent rewind starts
            // from a clean slate. ParsekScenario.HandleRewindOnLoad re-marks the
            // active/source recording AFTER ResetAllPlaybackState fires.
            if (rec.SpawnSuppressedByRewind && !SuppressLogging)
                ParsekLog.Verbose("Rewind",
                    $"SpawnSuppressedByRewind reset: \"{rec.VesselName}\" id={rec.RecordingId} " +
                    $"reason={rec.SpawnSuppressedByRewindReason ?? "<none>"}");
            rec.SpawnSuppressedByRewind = false;
            rec.SpawnSuppressedByRewindReason = null;
            rec.SpawnSuppressedByRewindUT = double.NaN;

            rec.SceneExitSituation = -1;
        }

        /// <summary>
        /// Collects SpawnedVesselPersistentId values and vessel names from all committed
        /// recordings (standalone + tree) that currently have a spawned vessel.
        /// Must be called BEFORE ResetAllPlaybackState
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
        /// Tree-scoped mirror of the tree half of <see cref="MarkAllFullyApplied"/>:
        /// advances each recording's <c>LastAppliedResourceIndex</c> to the last point
        /// for recordings with non-empty <c>Points</c>. Does NOT touch
        /// <see cref="MilestoneStore.Milestones"/> — that global mutation is specifically
        /// what callers need to avoid when marking a single tree fully applied (see plan
        /// Phase C, `docs/dev/done/plans/fix-ledger-lump-sum-reconciliation.md`). Returns the
        /// number of recordings whose applied index was advanced. Safe to call with a
        /// null tree (returns 0).
        /// </summary>
        internal static int MarkTreeAsApplied(RecordingTree tree)
        {
            if (tree == null) return 0;

            int advanced = 0;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec == null) continue;
                if (rec.Points != null && rec.Points.Count > 0)
                {
                    rec.LastAppliedResourceIndex = rec.Points.Count - 1;
                    advanced++;
                }
            }

            if (!SuppressLogging)
                ParsekLog.Verbose("RecordingStore",
                    $"MarkTreeAsApplied: tree id='{tree.Id}' name='{tree.TreeName}' " +
                    $"recordingsAdvanced={advanced}");

            return advanced;
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
                int advanced = 0;
                foreach (var rec in committedTrees[i].Recordings.Values)
                {
                    if (rec == null || rec.Points.Count == 0)
                        continue;

                    rec.LastAppliedResourceIndex = rec.Points.Count - 1;
                    advanced++;
                }
                if (advanced > 0)
                    treeCount++;
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
            string resolvedSave = GetRewindSaveFileName(rec);
            if (!CanRewindPreFileCheck(resolvedSave, out reason, isRecording))
                return false;

            string savePath = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildRewindSaveRelativePath(resolvedSave));
            bool saveExists = !string.IsNullOrEmpty(savePath) && File.Exists(savePath);
            return CanRewindWithResolvedSaveState(resolvedSave, saveExists, out reason, isRecording);
        }

        /// <summary>
        /// Testable core of <see cref="CanRewind"/> once tree-root save resolution and file
        /// existence have already been computed by the caller.
        /// </summary>
        internal static bool CanRewindWithResolvedSaveState(
            string resolvedSave, bool saveExists, out string reason, bool isRecording)
        {
            if (!CanRewindPreFileCheck(resolvedSave, out reason, isRecording))
                return false;

            if (!saveExists)
            {
                reason = "Rewind save file missing";
                // Per-frame logging removed (was 3.7% of all log output); reason returned to caller
                return false;
            }

            reason = "";
            // Per-frame logging removed; reason returned to caller
            return true;
        }

        private static bool CanRewindPreFileCheck(string resolvedSave, out string reason, bool isRecording)
        {
            if (IsRewinding)
            {
                reason = "Rewind already in progress";
                // Per-frame logging removed (was 3.7% of all log output); reason returned to caller
                return false;
            }

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
            if (!CanFastForwardPreRuntime(rec, out reason, isRecording))
                return false;

            return CanFastForwardAtUT(rec, Planetarium.GetUniversalTime(), out reason, isRecording);
        }

        /// <summary>
        /// Testable core of <see cref="CanFastForward"/> with the current UT supplied by the caller.
        /// </summary>
        internal static bool CanFastForwardAtUT(Recording rec, double now, out string reason, bool isRecording)
        {
            if (!CanFastForwardPreRuntime(rec, out reason, isRecording))
                return false;

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

        private static bool CanFastForwardPreRuntime(Recording rec, out string reason, bool isRecording)
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

            reason = "";
            // Per-frame logging removed; reason returned to caller
            return true;
        }

        private static void ResetRewindFlags()
        {
            RewindContext.EndRewind();
            ClearRewindReplayTargetScope();
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

            BeginRewindForOwner(owner);

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
                // 2. Wind back UT by the rewind-to-launch lead time so the player can
                //    regain control on the pad before launch.

                var stripNames = BuildRewindStripNames(owner);
                // Collect spawned vessel PIDs for PID-based stripping (belt-and-suspenders
                // alongside name matching — catches renamed vessels or debris)
                var (stripPids, _) = CollectSpawnedVesselInfo();
                PreProcessRewindSave(tempPath, stripNames, stripPids, RewindToLaunchLeadTimeSeconds);

                Game game = GamePersistence.LoadGame(tempCopyName, HighLogic.SaveFolder, true, false);

                // Delete the temp copy (file already parsed into Game object)
                TryDeleteFileQuietly(tempPath);

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

                DeleteTemporaryRewindSaveCopy(tempCopyName);

                if (!SuppressLogging)
                    ParsekLog.Error("Rewind",
                        $"Rewind failed: {ex.Message}. " +
                        $"Flags reset: IsRewinding={IsRewinding}, RewindUT={RewindUT}, RewindAdjustedUT={RewindAdjustedUT}");
            }
        }

        private static void BeginRewindForOwner(Recording owner)
        {
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
            SetRewindReplayTargetScope(owner);

            if (!SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Rewind initiated to UT {owner.StartUT} " +
                    $"(save: {owner.RewindSaveFileName})");
        }

        private static HashSet<string> BuildRewindStripNames(Recording owner)
        {
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

            return stripNames;
        }

        private static void DeleteTemporaryRewindSaveCopy(string tempCopyName)
        {
            // Clean up temp copy on failure
            if (tempCopyName == null)
                return;

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

        private static void TryDeleteFileQuietly(string path)
        {
            try { File.Delete(path); }
            catch { }
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

        internal static bool HasTrackSectionPayloadMatchingFlatTrajectory(
            Recording rec,
            bool allowRelativeSections = false)
        {
            return TrajectoryTextSidecarCodec.HasTrackSectionPayloadMatchingFlatTrajectory(
                rec, allowRelativeSections);
        }

        internal static bool FlatTrajectoryExtendsTrackSectionPayload(
            Recording rec,
            List<TrackSection> tracks,
            bool allowRelativeSections = false)
        {
            return TrajectoryTextSidecarCodec.FlatTrajectoryExtendsTrackSectionPayload(
                rec, tracks, allowRelativeSections);
        }

        internal static bool TrySyncFlatTrajectoryFromTrackSectionsPreservingFlatTail(
            Recording target,
            Recording source,
            List<TrackSection> tailReferenceTracks,
            bool allowRelativeSections = false)
        {
            return TrajectoryTextSidecarCodec.TrySyncFlatTrajectoryFromTrackSectionsPreservingFlatTail(
                target, source, tailReferenceTracks, allowRelativeSections);
        }

        internal static bool ShouldWriteSectionAuthoritativeTrajectory(Recording rec)
        {
            return TrajectoryTextSidecarCodec.ShouldWriteSectionAuthoritativeTrajectory(rec);
        }

        private static int GetTrajectoryFormatVersion(ConfigNode sourceNode)
        {
            return TrajectoryTextSidecarCodec.GetTrajectoryFormatVersion(sourceNode);
        }

        internal static int RebuildPointsFromTrackSections(List<TrackSection> tracks, List<TrajectoryPoint> points)
        {
            return TrajectoryTextSidecarCodec.RebuildPointsFromTrackSections(tracks, points);
        }

        internal static int AppendPointsFromTrackSections(List<TrackSection> tracks, List<TrajectoryPoint> points)
        {
            return TrajectoryTextSidecarCodec.AppendPointsFromTrackSections(tracks, points);
        }

        internal static bool ContainsRelativeTrackSections(List<TrackSection> tracks)
        {
            return TrajectoryTextSidecarCodec.ContainsRelativeTrackSections(tracks);
        }

        internal static bool HasCompleteTrackSectionPayloadForFlatSync(
            List<TrackSection> tracks,
            bool allowRelativeSections = false)
        {
            return TrajectoryTextSidecarCodec.HasCompleteTrackSectionPayloadForFlatSync(
                tracks, allowRelativeSections);
        }

        internal static bool TrySyncFlatTrajectoryFromTrackSections(
            Recording rec,
            bool allowRelativeSections = false)
        {
            return TrajectoryTextSidecarCodec.TrySyncFlatTrajectoryFromTrackSections(
                rec, allowRelativeSections);
        }

        internal static bool TryHealMalformedFlatFallbackTrajectoryFromTrackSections(
            Recording rec,
            bool allowRelativeSections = false)
        {
            return TrajectoryTextSidecarCodec.TryHealMalformedFlatFallbackTrajectoryFromTrackSections(
                rec, allowRelativeSections);
        }

        internal static int RebuildOrbitSegmentsFromTrackSections(
            List<TrackSection> tracks,
            List<OrbitSegment> orbitSegments)
        {
            return TrajectoryTextSidecarCodec.RebuildOrbitSegmentsFromTrackSections(
                tracks, orbitSegments);
        }

        internal static int AppendOrbitSegmentsFromTrackSections(
            List<TrackSection> tracks,
            List<OrbitSegment> orbitSegments)
        {
            return TrajectoryTextSidecarCodec.AppendOrbitSegmentsFromTrackSections(
                tracks, orbitSegments);
        }

        internal static void SerializeTrajectoryInto(ConfigNode targetNode, Recording rec)
        {
            TrajectoryTextSidecarCodec.SerializeTrajectoryInto(targetNode, rec);
        }

        internal static void DeserializeTrajectoryFrom(ConfigNode sourceNode, Recording rec)
        {
            TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(sourceNode, rec);
        }

        internal static void DeserializePoints(ConfigNode sourceNode, Recording rec)
        {
            TrajectoryTextSidecarCodec.DeserializePoints(sourceNode, rec);
        }

        internal static void DeserializeOrbitSegments(ConfigNode sourceNode, Recording rec)
        {
            TrajectoryTextSidecarCodec.DeserializeOrbitSegments(sourceNode, rec);
        }

        internal static void DeserializePartEvents(ConfigNode sourceNode, Recording rec)
        {
            TrajectoryTextSidecarCodec.DeserializePartEvents(sourceNode, rec);
        }

        internal static void DeserializeFlagEvents(ConfigNode sourceNode, Recording rec)
        {
            TrajectoryTextSidecarCodec.DeserializeFlagEvents(sourceNode, rec);
        }

        internal static void SerializeSegmentEvents(ConfigNode parent, List<SegmentEvent> events)
        {
            TrajectoryTextSidecarCodec.SerializeSegmentEvents(parent, events);
        }

        internal static void DeserializeSegmentEvents(ConfigNode parent, List<SegmentEvent> events)
        {
            TrajectoryTextSidecarCodec.DeserializeSegmentEvents(parent, events);
        }

        internal static void SerializeTrackSections(
            ConfigNode parent,
            List<TrackSection> tracks,
            int recordingFormatVersion = CurrentRecordingFormatVersion)
        {
            TrajectoryTextSidecarCodec.SerializeTrackSections(parent, tracks, recordingFormatVersion);
        }

        internal static void DeserializeTrackSections(ConfigNode parent, List<TrackSection> tracks)
        {
            TrajectoryTextSidecarCodec.DeserializeTrackSections(parent, tracks);
        }

        #endregion

        #region Crew End States Serialization

        internal static void SerializeCrewEndStates(ConfigNode parent, Recording rec)
        {
            RecordingManifestCodec.SerializeCrewEndStates(parent, rec);
        }

        internal static void DeserializeCrewEndStates(ConfigNode parent, Recording rec)
        {
            RecordingManifestCodec.DeserializeCrewEndStates(parent, rec);
        }

        #endregion

        #region Resource Manifest Serialization

        internal static void SerializeResourceManifest(ConfigNode parent, Recording rec)
        {
            RecordingManifestCodec.SerializeResourceManifest(parent, rec);
        }

        internal static void DeserializeResourceManifest(ConfigNode parent, Recording rec)
        {
            RecordingManifestCodec.DeserializeResourceManifest(parent, rec);
        }

        internal static void SerializeInventoryManifest(ConfigNode parent, Recording rec)
        {
            RecordingManifestCodec.SerializeInventoryManifest(parent, rec);
        }

        internal static void DeserializeInventoryManifest(ConfigNode parent, Recording rec)
        {
            RecordingManifestCodec.DeserializeInventoryManifest(parent, rec);
        }

        internal static void SerializeCrewManifest(ConfigNode parent, Recording rec)
        {
            RecordingManifestCodec.SerializeCrewManifest(parent, rec);
        }

        internal static void DeserializeCrewManifest(ConfigNode parent, Recording rec)
        {
            RecordingManifestCodec.DeserializeCrewManifest(parent, rec);
        }

        #endregion

        #region Recording File I/O

        // Compatibility wrappers during the sidecar owner split. Delete after
        // call sites can move to RecordingSidecarStore directly.
        internal static void ClearSidecarLoadFailure(Recording rec)
        {
            RecordingSidecarStore.ClearSidecarLoadFailure(rec);
        }

        internal static void MarkSidecarLoadFailure(Recording rec, string reason)
        {
            RecordingSidecarStore.MarkSidecarLoadFailure(rec, reason);
        }

        private static string FormatPathForSidecarLog(string path)
        {
            return string.IsNullOrEmpty(path) ? "<null>" : path;
        }

        internal static bool SaveRecordingFiles(Recording rec, bool incrementEpoch = true)
        {
            return RecordingSidecarStore.SaveRecordingFiles(rec, incrementEpoch);
        }

        internal static void ReconcileReadableSidecarMirrorsForKnownRecordings()
        {
            RecordingSidecarStore.ReconcileReadableSidecarMirrorsForKnownRecordings(
                committedRecordings, pendingTree);
        }

        internal static bool LoadRecordingFiles(Recording rec)
        {
            return RecordingSidecarStore.LoadRecordingFiles(rec);
        }

        /// <summary>
        /// Bug #270: Returns true if the sidecar file's epoch doesn't match the
        /// recording's expected epoch (loaded from .sfs). When true, the caller
        /// should skip trajectory deserialization — the .prec is from a different
        /// save point and its data would be inconsistent with the .sfs metadata.
        /// Backward compat: if the .sfs epoch is 0 (old save without epoch),
        /// validation is skipped and the sidecar is always accepted.
        /// </summary>
        internal static bool ShouldSkipStaleSidecar(Recording rec, int fileEpoch)
        {
            return RecordingSidecarStore.ShouldSkipStaleSidecar(rec, fileEpoch);
        }

        internal static void WriteTrajectorySidecar(string path, Recording rec, int sidecarEpoch)
        {
            NormalizeRecordingFormatVersionForPredictedSegments(rec);

            if (rec != null && rec.RecordingFormatVersion >= 2)
            {
                TrajectorySidecarBinary.Write(path, rec, sidecarEpoch);
                return;
            }

            var precNode = new ConfigNode("PARSEK_RECORDING");
            precNode.AddValue("version", rec?.RecordingFormatVersion ?? 0);
            if (rec != null && !string.IsNullOrEmpty(rec.RecordingId))
                precNode.AddValue("recordingId", rec.RecordingId);
            precNode.AddValue("sidecarEpoch", sidecarEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SerializeTrajectoryInto(precNode, rec);
            SafeWriteConfigNode(precNode, path);
        }

        internal static void NormalizeRecordingFormatVersionForPredictedSegments(Recording rec)
        {
            if (rec == null
                || rec.RecordingFormatVersion < 2
                || rec.RecordingFormatVersion >= PredictedOrbitSegmentFormatVersion)
                return;

            int predictedCheckpointCount;
            int predictedOrbitSegmentCount = CountPredictedOrbitSegments(rec, out predictedCheckpointCount);
            if (predictedOrbitSegmentCount == 0 && predictedCheckpointCount == 0)
                return;

            int originalVersion = rec.RecordingFormatVersion;
            rec.RecordingFormatVersion = PredictedOrbitSegmentFormatVersion;
            if (!SuppressLogging)
            {
                ParsekLog.Warn("RecordingStore",
                    $"NormalizeRecordingFormatVersionForPredictedSegments: recording={rec.RecordingId} " +
                    $"version={originalVersion}->{rec.RecordingFormatVersion} " +
                    $"predictedOrbitSegments={predictedOrbitSegmentCount} " +
                    $"predictedCheckpoints={predictedCheckpointCount}");
            }
        }

        internal static int CountPredictedOrbitSegments(Recording rec, out int predictedCheckpointCount)
        {
            int predictedOrbitSegmentCount = 0;
            predictedCheckpointCount = 0;

            if (rec?.OrbitSegments != null)
            {
                for (int i = 0; i < rec.OrbitSegments.Count; i++)
                {
                    if (rec.OrbitSegments[i].isPredicted)
                        predictedOrbitSegmentCount++;
                }
            }

            if (rec?.TrackSections == null)
                return predictedOrbitSegmentCount;

            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                List<OrbitSegment> checkpoints = rec.TrackSections[i].checkpoints;
                if (checkpoints == null)
                    continue;

                for (int j = 0; j < checkpoints.Count; j++)
                {
                    if (checkpoints[j].isPredicted)
                        predictedCheckpointCount++;
                }
            }

            return predictedOrbitSegmentCount;
        }

        internal static bool TryProbeTrajectorySidecar(string path, out TrajectorySidecarProbe probe)
        {
            probe = default(TrajectorySidecarProbe);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            if (TrajectorySidecarBinary.HasBinaryMagic(path))
            {
                bool binaryProbeOk = TrajectorySidecarBinary.TryProbe(path, out probe);
                if (binaryProbeOk && !SuppressLogging)
                {
                    ParsekLog.Verbose("RecordingStore",
                        $"TryProbeTrajectorySidecar: encoding={probe.Encoding} version={probe.FormatVersion} " +
                        $"recording={probe.RecordingId} sidecarEpoch={probe.SidecarEpoch}");
                    if (!probe.Supported)
                    {
                        ParsekLog.Warn("RecordingStore",
                            $"TryProbeTrajectorySidecar: unsupported binary trajectory version {probe.FormatVersion} " +
                            $"for recording={probe.RecordingId}");
                    }
                }

                return binaryProbeOk;
            }

            var precNode = ConfigNode.Load(path);
            if (precNode == null)
                return false;

            int sidecarEpoch = 0;
            string fileEpochStr = precNode.GetValue("sidecarEpoch");
            if (fileEpochStr != null)
            {
                int.TryParse(fileEpochStr, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out sidecarEpoch);
            }

            probe = new TrajectorySidecarProbe
            {
                Success = true,
                Supported = true,
                Encoding = TrajectorySidecarEncoding.TextConfigNode,
                FormatVersion = GetTrajectoryFormatVersion(precNode),
                SidecarEpoch = sidecarEpoch,
                RecordingId = precNode.GetValue("recordingId"),
                LegacyNode = precNode
            };

            if (!SuppressLogging)
            {
                ParsekLog.Verbose("RecordingStore",
                    $"TryProbeTrajectorySidecar: encoding=TextConfigNode version={probe.FormatVersion} " +
                    $"recording={probe.RecordingId} sidecarEpoch={probe.SidecarEpoch}");
            }

            return true;
        }

        internal static void DeserializeTrajectorySidecar(string path, TrajectorySidecarProbe probe, Recording rec)
        {
            if (probe.Encoding == TrajectorySidecarEncoding.BinaryV2 ||
                probe.Encoding == TrajectorySidecarEncoding.BinaryV3)
            {
                TrajectorySidecarBinary.Read(path, rec, probe);
                return;
            }

            DeserializeTrajectoryFrom(probe.LegacyNode, rec);
        }

        internal static bool LoadTrajectorySidecarForTesting(string path, Recording rec)
        {
            TrajectorySidecarProbe probe;
            if (!TryProbeTrajectorySidecar(path, out probe) || !probe.Supported)
                return false;

            DeserializeTrajectorySidecar(path, probe, rec);
            return true;
        }

        internal static bool SaveRecordingFilesToPathsForTesting(
            Recording rec, string precPath, string vesselPath, string ghostPath, bool incrementEpoch = true)
        {
            return RecordingSidecarStore.SaveRecordingFilesToPathsForTesting(
                rec, precPath, vesselPath, ghostPath, incrementEpoch);
        }

        internal static bool ReconcileReadableSidecarMirrorsToPathsForTesting(
            Recording rec, string precPath, string vesselPath, string ghostPath)
        {
            return RecordingSidecarStore.ReconcileReadableSidecarMirrorsToPathsForTesting(
                rec, precPath, vesselPath, ghostPath);
        }

        internal static bool LoadRecordingFilesFromPathsForTesting(
            Recording rec, string precPath, string vesselPath, string ghostPath)
        {
            return RecordingSidecarStore.LoadRecordingFilesFromPathsForTesting(
                rec, precPath, vesselPath, ghostPath);
        }

        // Kept on RecordingStore while wrappers remain the public test surface.
        // Move with the load owner after codec/call-site ownership settles.
        internal enum SnapshotSidecarLoadState
        {
            Missing = 0,
            Loaded = 1,
            Invalid = 2,
            Unsupported = 3
        }

        internal struct SnapshotSidecarLoadSummary
        {
            public SnapshotSidecarLoadState VesselState;
            public SnapshotSidecarLoadState GhostState;
            public string FailureReason;
        }

        internal static bool TryProbeSnapshotSidecar(string path, out SnapshotSidecarProbe probe)
        {
            probe = default(SnapshotSidecarProbe);
            bool probeOk = SnapshotSidecarCodec.TryProbe(path, out probe);
            if (probeOk && !SuppressLogging)
            {
                ParsekLog.Verbose("RecordingStore",
                    $"TryProbeSnapshotSidecar: path='{FormatPathForSidecarLog(path)}' " +
                    SnapshotSidecarCodec.DescribeProbe(probe));
                if (!probe.Supported)
                {
                    ParsekLog.Warn("RecordingStore",
                        $"TryProbeSnapshotSidecar: unsupported snapshot sidecar " +
                        $"path='{FormatPathForSidecarLog(path)}' " +
                        SnapshotSidecarCodec.DescribeProbe(probe));
                }
            }
            else if (!probeOk && !SuppressLogging)
            {
                ParsekLog.Warn("RecordingStore",
                    $"TryProbeSnapshotSidecar: failed path='{FormatPathForSidecarLog(path)}' " +
                    SnapshotSidecarCodec.DescribeProbe(probe));
            }

            return probeOk;
        }

        internal static bool TryLoadSnapshotSidecar(string path, out ConfigNode node, out SnapshotSidecarProbe probe)
        {
            return SnapshotSidecarCodec.TryLoad(path, out node, out probe);
        }

        internal static bool LoadSnapshotSidecarForTesting(string path, out ConfigNode node)
        {
            SnapshotSidecarProbe probe;
            if (!TryLoadSnapshotSidecar(path, out node, out probe) || !probe.Supported)
                return false;

            return node != null;
        }

        internal static void WriteSnapshotSidecarForTesting(string path, ConfigNode node)
        {
            WriteSnapshotSidecar(path, node);
        }

        internal static string GetTrajectorySidecarEncodingLabel(int recordingFormatVersion)
        {
            if (recordingFormatVersion >= 3)
                return "BinaryV3";
            if (recordingFormatVersion >= 2)
                return "BinaryV2";
            return "TextConfigNode";
        }

        private static void SafeWriteConfigNode(ConfigNode node, string path)
        {
            FileIOUtils.SafeWriteConfigNode(node, path, "RecordingStore");
        }

        private static void WriteSnapshotSidecar(string path, ConfigNode node)
        {
            SnapshotSidecarCodec.Write(path, node);
        }

        internal static void NormalizeRecordingFormatVersionAfterLegacyLoopMigration(Recording rec)
        {
            RecordingSidecarStore.NormalizeRecordingFormatVersionAfterLegacyLoopMigration(rec);
        }

        /// <summary>
        /// #565 / #567: encodes the narrow contract between a trajectory sidecar's on-disk
        /// binary-encoding version (<c>probe.FormatVersion</c>) and the in-memory recording's
        /// semantic version (<c>rec.RecordingFormatVersion</c>) for a current-format committed
        /// recording. The two values can differ ONLY by the v3 -&gt; v4 metadata-only
        /// migration: <see cref="LaunchToLaunchLoopIntervalFormatVersion"/> changed the
        /// meaning of <c>loopIntervalSeconds</c> without altering binary layout, so a v3
        /// sidecar with a v4 in-memory recording is correct on disk and only the in-memory
        /// state was promoted (see
        /// <see cref="NormalizeRecordingFormatVersionAfterLegacyLoopMigration"/>).
        /// Every other lag is rejected: v5 added serialized
        /// <c>OrbitSegment.isPredicted</c>, v6 changed RELATIVE TrackSection point
        /// semantics, and v7 added RELATIVE absolute shadow points, so a v3 sidecar
        /// paired with a v5+ recording indicates stale or incomplete trajectory data
        /// on disk that the runtime test must catch.
        /// </summary>
        internal static bool IsAcceptableSidecarVersionLag(int probeFormatVersion, int recordingFormatVersion)
        {
            return RecordingSidecarStore.IsAcceptableSidecarVersionLag(probeFormatVersion, recordingFormatVersion);
        }

        /// <summary>
        /// Bug #585 follow-up: a recording whose sidecar load failed (most often
        /// bug #270's stale-sidecar-epoch mitigation on a Re-Fly quicksave) sits
        /// in memory with empty trajectory + null snapshots, while the on-disk
        /// .prec still holds the original mission's data. If the recorder never
        /// rebinds (any non-active recording in the loaded tree), writing the
        /// empty in-memory state back to disk would clobber the original .prec
        /// — permanently destroying user data. PR #558 fixed this for the
        /// active recording (recorder rebind repopulates it) but did nothing
        /// for siblings; the playtest at <c>logs/2026-04-25_2210_refly-bugs/</c>
        /// caught a sibling launch recording (22c28f04…) being overwritten with
        /// <c>points=0 orbitSegments=0 trackSections=0 wroteVessel=False</c> on
        /// scene exit. This guard returns true when both flags are set and the
        /// in-memory state has no recorded data: the saver must skip the write
        /// to preserve the on-disk .prec.
        /// </summary>
        internal static bool ShouldSkipSaveToPreserveStaleSidecar(Recording rec)
        {
            return RecordingSidecarStore.ShouldSkipSaveToPreserveStaleSidecar(rec);
        }

        internal static SnapshotSidecarLoadSummary LoadSnapshotSidecarsFromPaths(Recording rec, string vesselPath, string ghostPath)
        {
            return RecordingSidecarStore.LoadSnapshotSidecarsFromPaths(rec, vesselPath, ghostPath);
        }

        #endregion
    }
}
