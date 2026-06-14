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
        internal sealed class RewindSupersedeRollbackResult
        {
            public readonly List<RecordingSupersedeRelation> DroppedRelations =
                new List<RecordingSupersedeRelation>();
            public readonly HashSet<string> RestoredRecordingIds =
                new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> RetiredForkRecordingIds =
                new HashSet<string>(StringComparer.Ordinal);

            /// <summary>
            /// Forks whose <see cref="Recording.MergeState"/> is
            /// <see cref="MergeState.Immutable"/> (canon) and whose supersede
            /// relation was therefore preserved across this parent rewind. The
            /// canon fork stays in the timeline; its priorTip stays superseded.
            /// </summary>
            public readonly HashSet<string> SkippedImmutableForkRecordingIds =
                new HashSet<string>(StringComparer.Ordinal);

            /// <summary>
            /// Immutable preservation candidates (Pass 1) that were demoted to
            /// drops in Pass 2 because their <c>OldRecordingId</c> was itself
            /// being retired in this same batch. The canon fork has nothing to
            /// be canon over (its priorTip is gone), so the chain must collapse
            /// to preserve the no-double-materialization invariant.
            /// </summary>
            public readonly HashSet<string> DemotedImmutablePreservationIds =
                new HashSet<string>(StringComparer.Ordinal);

            /// <summary>
            /// Forks (typically Immutable) whose incoming supersede relation was
            /// dropped because the user explicitly self-rewound them — i.e.
            /// <c>rel.NewRecordingId == owner.RecordingId</c>. The classifier
            /// forces the drop regardless of MergeState (the user is
            /// undoing this canon, not preserving it). Carried through the
            /// rollback result so <c>EnsureRewindRetirementsForRollback</c>'s
            /// defensive Immutable guard recognizes the intent and lets the
            /// retirement proceed (with reason
            /// <c>RecordingRewindRetirement.SelfRewoundCanonReason</c>).
            /// </summary>
            public readonly HashSet<string> ForcedSelfRewindDropIds =
                new HashSet<string>(StringComparer.Ordinal);

            public int DroppedRelationCount => DroppedRelations.Count;
            public int SkippedImmutableForkCount => SkippedImmutableForkRecordingIds.Count;
            public int DemotedImmutablePreservationCount => DemotedImmutablePreservationIds.Count;
            public int ForcedSelfRewindDropCount => ForcedSelfRewindDropIds.Count;
        }

        public const int CurrentRecordingFormatVersion = 1;
        // Schema generation discriminator. Bumped on every clean-slate schema
        // reset; recordings/sidecars carrying a different generation are rejected
        // on load (reasons "generation-older" / "generation-newer") so a loader
        // never sees a shape it was not built for. Pre-1.0 dev: backwards
        // compatibility is explicitly NOT a goal, so each bump deletes the
        // tolerance seams that only existed to read the prior generation.
        //
        // Generation 2 landed the parent-anchor contract extension to
        // controlled-decoupled children (the on-disk truth table widened to
        // admit the previously-unreachable row IsDebris=false,
        // ParentAnchorRecordingId=non-null).
        //
        // Generation 3 is the clean-slate reset that retired the last batch of
        // pre-reset compatibility seams: the legacy v5 world-offset RELATIVE
        // contract, the committed-bool to MergeState migration, the Phase-F
        // tree-resource residual seam, the legacy rewind-suppression marker
        // normalizer, and the no-op format-version contract-upgrade helpers.
        // Generation 2 and older recordings are rejected with reason
        // "generation-older".
        //
        // Generation 4 renamed the parent-anchor ConfigNode key from
        // "debrisParentRecordingId" to "parentAnchorRecordingId" (the
        // DebrisParentRecordingId field renamed to ParentAnchorRecordingId).
        // Generation 3 and older recordings carry the old key and are rejected
        // with reason "generation-older".
        public const int CurrentRecordingSchemaGeneration = 4;

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
        // v0 reset: current post-redesign private-development schema. A separate
        // RecordingSchemaGeneration discriminator rejects old internal saves that
        // also defaulted to recordingFormatVersion=0.

        internal static bool IsRecordingSchemaCompatible(
            int recordingFormatVersion,
            int recordingSchemaGeneration,
            out string reason)
        {
            if (recordingSchemaGeneration == 0)
            {
                reason = "generation-missing";
                return false;
            }

            if (recordingSchemaGeneration < CurrentRecordingSchemaGeneration)
            {
                reason = "generation-older";
                return false;
            }

            if (recordingSchemaGeneration > CurrentRecordingSchemaGeneration)
            {
                reason = "generation-newer";
                return false;
            }

            if (recordingFormatVersion != CurrentRecordingFormatVersion)
            {
                reason = "format-version-mismatch";
                return false;
            }

            reason = null;
            return true;
        }

        // When true, suppresses logging calls (for unit testing outside Unity)
        internal static bool SuppressLogging;
        internal static bool? WriteReadableSidecarMirrorsOverrideForTesting;
        internal static Func<double> CurrentUniversalTimeForRewindRetirementOverrideForTesting;

        // PID of the active vessel at scene entry. Used by SpawnVesselOrChainTip to
        // bypass PID dedup statelessly — if a recording's VesselPersistentId matches
        // this, the existing real vessel is the player's reverted/active vessel, not
        // a previously-spawned endpoint. Static so it survives Recording object recreation.
        internal static uint SceneEntryActiveVesselPid;

        // PID of the vessel that rolled out fresh from the VAB/SPH this scene
        // (set only for NEW_FROM_FILE / NEW_FROM_CRAFT_NODE startups; 0 otherwise).
        // Read by CrewReservationManager.SwapReservedCrewInFlight to suppress Pass-2
        // orphan crew placement when the active vessel is this fresh launch — a new
        // mission has no orphaned reserved crew to reclaim, and reclaiming would
        // mis-seat stand-ins via KSP's craft-stable part persistentId reuse. Static
        // so the chain-commit / merge call sites (which have no ParsekFlight handle)
        // can consult it, mirroring SceneEntryActiveVesselPid.
        internal static uint SceneEntryFreshRolloutVesselPid;

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
        private static bool suppressNextTreeSceneExitCommit;
        private static string suppressNextTreeSceneExitCommitReason;
        private static bool suppressNextActiveTreeRestore;
        private static string suppressNextActiveTreeRestoreReason;

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
        // True only for a Finalized pending tree known to be represented by an
        // isPending=True save node; every pending-slot ownership/state change resets it.
        private static bool pendingTreeSerializedForSave;
        // Active-tree quickload resume temporarily uses pendingTree as a Limbo
        // handoff slot. If the same save also carries a finalized isPending=True
        // tree, keep it here until the active tree is popped back into flight.
        // Invariant: this side slot only holds a saved Finalized pending tree
        // awaiting OnLoad -> PromoteSavedPendingTreeAfterActiveRestore; normal
        // stash/commit/pop/unstash lifecycle methods intentionally leave it alone.
        private static RecordingTree savedPendingTreeDuringActiveRestore;
        private static bool savedPendingTreeDuringActiveRestoreSerializedForSave;

        // One-shot context for the committed-spawned-vessel restore path. That
        // path keeps the original committed tree durable and gives the live
        // flight a copy-on-write clone. While the clone is unmerged, dirty
        // overlap sidecars must not be written over committed history, and
        // Discard must remove same-id game-state event tails authored after the
        // committed recording's original end UT.
        private static string committedTreeRestoreAttemptTreeId;
        private static string committedTreeRestoreAttemptReason;
        private static HashSet<string> committedTreeRestoreAttemptRecordingIds;
        private static Dictionary<string, double> committedTreeRestoreAttemptEventCutoffs;
        internal static string CleanOrphanFilesDirectoryOverrideForTesting;

        /// <summary>
        /// Test-only escape hatch: when true, save-time sidecar-currency checks treat
        /// every recording as already-current so in-memory test fixtures can exercise
        /// the metadata-only `ParsekScenario.SaveTreeRecordings` path without first
        /// writing real `.prec` / `.craft` files. Production always leaves this false
        /// — the strict gate is what blocks a metadata save from outliving its sidecars.
        /// </summary>
        internal static bool SkipSidecarCurrencyCheckForTesting;

        public static IReadOnlyList<Recording> CommittedRecordings => committedRecordings;
        public static List<RecordingTree> CommittedTrees => committedTrees;
        public static bool HasPendingTree => pendingTree != null;
        public static RecordingTree PendingTree => pendingTree;
        public static PendingTreeState PendingTreeStateValue => pendingTreeState;
        internal static bool PendingTreeSerializedForSave => pendingTreeSerializedForSave;
        internal static bool HasSavedPendingTreeDuringActiveRestore =>
            savedPendingTreeDuringActiveRestore != null;
        internal static RecordingTree SavedPendingTreeDuringActiveRestore =>
            savedPendingTreeDuringActiveRestore;
        internal static bool SavedPendingTreeDuringActiveRestoreSerializedForSave =>
            savedPendingTreeDuringActiveRestoreSerializedForSave;
        internal static bool HasCommittedTreeRestoreAttempt =>
            committedTreeRestoreAttemptTreeId != null;
        internal static bool HasCommittedTreeRestoreAttemptForTesting =>
            HasCommittedTreeRestoreAttempt;

        /// <summary>
        /// Slot identifier returned by <see cref="TryResolveTreeById"/> so
        /// callers know whether the resolved tree came from the pending, active
        /// (live activeTree on ParsekFlight), or committed slot. The slots are
        /// walked in priority order: a live in-FLIGHT clone-restore wrapper
        /// sits in the active slot and must beat the original committed copy.
        /// </summary>
        internal enum TreeSlotSource
        {
            None = 0,
            Pending = 1,
            Active = 2,
            Committed = 3,
        }

        /// <summary>
        /// M3 (PR #876 round-5 review): shared tree lookup used by both
        /// <see cref="SceneExitInterceptor.TryResolveSessionTreeForDialog"/>
        /// and <see cref="MergeDialog.ShowPreSwitchDecisionDialog"/>. The two
        /// callers used to walk the same slots with diverging logic — the
        /// pre-switch dialog stopped after pending+active and ignored
        /// CommittedTrees, while SceneExit walked all three. This helper is
        /// the canonical resolver.
        ///
        /// <para>Priority order: Pending (sealed-but-not-yet-committed) →
        /// Active (live activeTree on <see cref="ParsekFlight"/>, including
        /// in-FLIGHT clone-restore wrappers) → Committed (terminal storage).
        /// The active slot wins over committed when a clone-restore is mid-
        /// flight, so the segment-bearing clone is dialog-ed instead of the
        /// original committed tree. Invariant: no live
        /// <see cref="SwitchSegmentSession"/> should ever share its TreeId
        /// with a non-clone committed tree.</para>
        /// </summary>
        internal static bool TryResolveTreeById(
            string treeId,
            out RecordingTree tree,
            out TreeSlotSource sourceSlot)
        {
            tree = null;
            sourceSlot = TreeSlotSource.None;
            if (string.IsNullOrEmpty(treeId))
                return false;

            if (pendingTree != null
                && string.Equals(pendingTree.Id, treeId, System.StringComparison.Ordinal))
            {
                tree = pendingTree;
                sourceSlot = TreeSlotSource.Pending;
                return true;
            }

            var flight = ParsekFlight.Instance;
            if (flight != null
                && flight.ActiveTreeForSerialization != null
                && string.Equals(
                    flight.ActiveTreeForSerialization.Id,
                    treeId,
                    System.StringComparison.Ordinal))
            {
                tree = flight.ActiveTreeForSerialization;
                sourceSlot = TreeSlotSource.Active;
                return true;
            }

            if (committedTrees != null)
            {
                for (int i = 0; i < committedTrees.Count; i++)
                {
                    var t = committedTrees[i];
                    if (t != null
                        && string.Equals(t.Id, treeId, System.StringComparison.Ordinal))
                    {
                        tree = t;
                        sourceSlot = TreeSlotSource.Committed;
                        return true;
                    }
                }
            }

            return false;
        }

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
        /// Inserts <paramref name="toInsert"/> into the committed list at the
        /// slot immediately after the first recording whose
        /// <see cref="Recording.RecordingId"/> matches
        /// <paramref name="afterRecordingId"/>. If no match is found,
        /// <paramref name="toInsert"/> is appended at the end. Used by
        /// <see cref="RecordingTreeSplitter"/> to insert TIP right after HEAD
        /// (mirrors the optimizer split's <c>recordings.Insert(recIdx+1, ...)</c>
        /// pattern in <see cref="RunOptimizationSplitPass"/>). Bumps
        /// <see cref="StateVersion"/>.
        /// </summary>
        internal static void InsertCommittedAfter(string afterRecordingId, Recording toInsert)
        {
            if (toInsert == null) return;
            int insertAt = committedRecordings.Count;
            if (!string.IsNullOrEmpty(afterRecordingId))
            {
                for (int i = 0; i < committedRecordings.Count; i++)
                {
                    if (string.Equals(
                            committedRecordings[i].RecordingId,
                            afterRecordingId,
                            StringComparison.Ordinal))
                    {
                        insertAt = i + 1;
                        break;
                    }
                }
            }
            committedRecordings.Insert(insertAt, toInsert);
            BumpStateVersion();
        }

        /// <summary>
        /// Removes the first committed recording whose
        /// <see cref="Recording.RecordingId"/> equals
        /// <paramref name="recordingId"/>. Returns true when a row was
        /// removed. Used by <see cref="RecordingTreeSplitter.RollBackInMemory"/>
        /// during snapshot rollback when only the id is known (the TIP
        /// reference may have been mutated in earlier ledger steps).
        /// Bumps <see cref="StateVersion"/> on success.
        /// </summary>
        internal static bool RemoveCommittedById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return false;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                if (string.Equals(
                        committedRecordings[i].RecordingId,
                        recordingId,
                        StringComparison.Ordinal))
                {
                    committedRecordings.RemoveAt(i);
                    BumpStateVersion();
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Replaces the first committed recording whose
        /// <see cref="Recording.RecordingId"/> equals
        /// <paramref name="replacement"/>.<see cref="Recording.RecordingId"/>
        /// with the supplied reference. No-op when the id is not found.
        /// Used by <see cref="RecordingTreeSplitter.RollBackInMemory"/> to
        /// swap origin's reference back to a pre-split <see cref="Recording.DeepClone"/>
        /// after <see cref="RecordingOptimizer.SplitAtUT"/> trimmed the
        /// in-place mutable. Bumps <see cref="StateVersion"/> on success.
        /// </summary>
        internal static bool ReplaceCommittedReference(Recording replacement)
        {
            if (replacement == null || string.IsNullOrEmpty(replacement.RecordingId)) return false;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                if (string.Equals(
                        committedRecordings[i].RecordingId,
                        replacement.RecordingId,
                        StringComparison.Ordinal))
                {
                    committedRecordings[i] = replacement;
                    BumpStateVersion();
                    return true;
                }
            }
            return false;
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
            ClearCommittedTreeRestoreAttempt("ClearCommitted");
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
            pendingTreeSerializedForSave = false;
            savedPendingTreeDuringActiveRestore = null;
            savedPendingTreeDuringActiveRestoreSerializedForSave = false;
            ClearCommittedTreeRestoreAttempt("Clear");
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
        /// Commits <paramref name="tree"/> into <see cref="CommittedTrees"/>.
        /// Adds all recordings to <see cref="CommittedRecordings"/> (for ghost
        /// playback) and the tree itself to <see cref="CommittedTrees"/> (for
        /// tree-specific queries).
        ///
        /// <para><b>Active-Re-Fly union side-effect (Bug fix-refly-abandon-and-fork-persist
        /// §Bug2a)</b>: when a live <c>ParsekScenario.Instance?.ActiveReFlySessionMarker</c>
        /// references the same tree id as <paramref name="tree"/>, the
        /// duplicate-tree path takes a UNION rather than the strict skip-as-
        /// duplicate. That changes the contract for any caller other than the
        /// merge orchestrator: if a future code path commits a deliberately-
        /// pruned tree (e.g. trim-scope=ActiveRecOnly view) while a Re-Fly
        /// session is active, this method will merge it into the committed
        /// copy instead of rejecting it. New callers that rely on the strict-
        /// skip semantics MUST either (a) clear the live marker first, or
        /// (b) confirm their use case wants the union semantics.</para>
        /// </summary>
        public static void CommitTree(RecordingTree tree)
        {
            if (tree == null) return;

            // First-commit guard data (collapse-seal-into-mergestate plan §4.1):
            // snapshot recording ids already committed AS PART OF A TREE, taken at
            // the LITERAL TOP of CommitTree, BEFORE the union/replace path
            // (TryUnionActiveReFlyTreeIntoCommitted mutates committedTrees[i].Recordings)
            // and BEFORE FinalizeTreeCommit swaps. ApplyRewindProvisionalMergeStates
            // uses this (plus supersede-fork identity) so it never re-derives an
            // already-concluded recording's MergeState. Keyed on committed-TREE
            // membership, NOT the flat committedRecordings list (which is polluted
            // mid-flight by CommitRecordingDirect).
            var alreadyCommittedRecordingIds = new HashSet<string>(StringComparer.Ordinal);
            for (int ci = 0; ci < committedTrees.Count; ci++)
            {
                var ctSnapshot = committedTrees[ci];
                if (ctSnapshot?.Recordings == null) continue;
                foreach (var committedId in ctSnapshot.Recordings.Keys)
                    if (!string.IsNullOrEmpty(committedId))
                        alreadyCommittedRecordingIds.Add(committedId);
            }

            int replaceCommittedTreeIndex = -1;
            for (int i = 0; i < committedTrees.Count; i++)
            {
                if (committedTrees[i].Id == tree.Id)
                {
                    if (ReferenceEquals(committedTrees[i], tree))
                    {
                        Log($"[Parsek] WARNING: Tree '{tree.Id}' already committed — skipping duplicate");
                        GameStateRecorder.PendingScienceSubjects.Clear();
                        ClearRewindReplayTargetScope();
                        return;
                    }

                    if (!ShouldReplaceCommittedTree(committedTrees[i], tree, out var replaceReason))
                    {
                        // Bug fix-refly-abandon-and-fork-persist §Bug2a: when
                        // an active Re-Fly session references this tree id,
                        // the incoming tree is by construction a partial
                        // view (the active session may have pruned BPs
                        // / recordings via trim-scope=ActiveRecOnly). The
                        // strict ShouldReplaceCommittedTree gate then
                        // rejects the merge as "incoming-missing-existing-
                        // ids" and the session's fork — which lives only
                        // in the incoming/active tree — is silently
                        // dropped at OnSave time. Union the incoming's new
                        // recordings/BPs into the existing committed tree
                        // instead, then reassign `tree` to that merged
                        // object so the downstream helpers
                        // (ApplySessionMergeToRecordings through
                        // MarkSupersededTerminalSpawnsForContinuedSources)
                        // operate on the canonical post-union view.
                        var liveMarker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
                        if (liveMarker != null
                            && string.Equals(liveMarker.TreeId, tree.Id, StringComparison.Ordinal)
                            && TryUnionActiveReFlyTreeIntoCommitted(
                                committedTrees[i], tree, liveMarker,
                                out int addedRecs, out int addedBps, out bool activeIdSwapped))
                        {
                            ParsekLog.Info("RecordingStore",
                                $"CommitTree: unioned active-Re-Fly incoming tree id='{tree.Id}' " +
                                $"into existing committed tree " +
                                $"(strictGateReason={replaceReason}, " +
                                $"addedRecordings={addedRecs} " +
                                $"addedBranchPoints={addedBps} " +
                                $"activeRecordingIdSwapped={activeIdSwapped} " +
                                $"sess={liveMarker.SessionId ?? "<no-id>"})");
                            // Reassign so downstream helpers see the
                            // canonical merged tree, and route through the
                            // "updated committed tree" branch of
                            // FinalizeTreeCommit by pointing
                            // replaceCommittedTreeIndex at the existing slot.
                            // The strict-replace branch below would copy
                            // group identity from existing → incoming here;
                            // we don't need to because `tree` IS the
                            // existing committed tree after the reassign.
                            tree = committedTrees[i];
                            replaceCommittedTreeIndex = i;
                            break;
                        }

                        Log($"[Parsek] WARNING: Tree '{tree.Id}' already committed — skipping duplicate");
                        ParsekLog.Verbose("RecordingStore",
                            $"CommitTree: duplicate tree id='{tree.Id}' skipped reason={replaceReason}");
                        GameStateRecorder.PendingScienceSubjects.Clear();
                        ClearRewindReplayTargetScope();
                        return;
                    }

                    replaceCommittedTreeIndex = i;
                    // Preserve the existing committed tree's group identity on a
                    // topology-update commit (e.g. Re-Fly merge). Without this,
                    // AutoGroupTreeRecordings sees the incoming tree's empty
                    // AutoGenerated*GroupName fields and routes through
                    // GenerateUniqueGroupName, which appends a "#2", "#3", …
                    // suffix because the previous commit already added "Kerbal X"
                    // to a committed recording — every Re-Fly merge would then
                    // create a fresh "Kerbal X #N" tree-row group instead of
                    // staying in the original mission's group.
                    var existingTree = committedTrees[i];
                    tree.AutoGeneratedRootGroupName = existingTree.AutoGeneratedRootGroupName;
                    tree.AutoGeneratedDebrisGroupName = existingTree.AutoGeneratedDebrisGroupName;
                    tree.AutoGeneratedCrewGroupName = existingTree.AutoGeneratedCrewGroupName;
                    ParsekLog.Warn("RecordingStore",
                        $"Tree '{tree.Id}' already committed with a different topology; " +
                        $"updating committed tree (oldRecordings={committedTrees[i].Recordings.Count}, " +
                        $"newRecordings={tree.Recordings.Count}, reason={replaceReason}) " +
                        $"preservedRootGroup='{tree.AutoGeneratedRootGroupName ?? "<none>"}'");
                    break;
                }
            }

            ApplySessionMergeToRecordings(tree);
            ApplyRewindProvisionalMergeStates(tree, alreadyCommittedRecordingIds);
            PromoteNormalStagingRewindPoints(tree);
            AutoGroupTreeRecordings(tree);
            AdoptOrphanedRecordingsIntoTreeGroup(tree);
            MarkSupersededTerminalSpawnsForContinuedSources(tree);
            FinalizeTreeCommit(tree, replaceCommittedTreeIndex);
            ClearCommittedTreeRestoreAttemptForTree(tree.Id, "CommitTree accepted restored active tree");
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
        /// re-fly or explicit Seal closes it. Stable surface EVA side-branches
        /// are the exception: when the only classifier reason is stranded EVA
        /// and the terminal is safe, the commit closes the slot immediately.
        /// Legacy/default recordings are born Immutable, so stamp that precise
        /// shape during the normal tree commit path.
        /// </summary>
        /// <summary>
        /// Test seam: re-runs the slot-driven MergeState promotion + clobber
        /// guard (collapse-seal-into-mergestate) against the already-committed
        /// trees, exactly as a later CommitTree would. Builds the committed-TREE
        /// membership snapshot from the current committed trees so the
        /// first-commit guard treats already-committed recordings as concluded.
        /// </summary>
        internal static void ApplyRewindProvisionalMergeStatesForTesting(RecordingTree tree)
        {
            var snapshot = new HashSet<string>(StringComparer.Ordinal);
            for (int ci = 0; ci < committedTrees.Count; ci++)
            {
                var ct = committedTrees[ci];
                if (ct?.Recordings == null) continue;
                foreach (var id in ct.Recordings.Keys)
                    if (!string.IsNullOrEmpty(id))
                        snapshot.Add(id);
            }
            ApplyRewindProvisionalMergeStates(tree, snapshot);
        }

        private static void ApplyRewindProvisionalMergeStates(
            RecordingTree tree, HashSet<string> alreadyCommittedRecordingIds)
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
                return;
            if (tree.BranchPoints == null || tree.BranchPoints.Count == 0)
                return;

            var scenario = ParsekScenario.Instance;
            IReadOnlyList<RecordingSupersedeRelation> supersedes =
                !object.ReferenceEquals(null, scenario) && scenario.RecordingSupersedes != null
                    ? scenario.RecordingSupersedes
                    : (IReadOnlyList<RecordingSupersedeRelation>)Array.Empty<RecordingSupersedeRelation>();

            // Supersede forks (NewRecordingId) get their MergeState authoritatively from
            // SupersedeCommit, never from promotion (plan §4.1). Skipping them also covers
            // non-in-place forks that never enter a committed tree.
            var supersedeForkIds = new HashSet<string>(StringComparer.Ordinal);
            for (int r = 0; r < supersedes.Count; r++)
            {
                string forkId = supersedes[r]?.NewRecordingId;
                if (!string.IsNullOrEmpty(forkId)) supersedeForkIds.Add(forkId);
            }

            // First-commit guard: a recording is "already concluded as canon" (its
            // MergeState must NOT be re-derived) if it was already committed as part of a
            // tree, or it is a supersede fork. Open-UF tips committed mid-flight via
            // CommitRecordingDirect are in neither set on their tree's first commit, so
            // they ARE demoted here.
            var committedSnapshot = alreadyCommittedRecordingIds
                ?? new HashSet<string>(StringComparer.Ordinal);
            Func<string, bool> isFirstCommit = id =>
                !string.IsNullOrEmpty(id)
                && !committedSnapshot.Contains(id)
                && !supersedeForkIds.Contains(id);

            int promoted = 0;
            int tipsPromoted = 0;
            int autoSealed = 0;
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
                        rec, slot, rp, out qualifyReason, tree))
                {
                    ParsekLog.Verbose("UnfinishedFlights",
                        $"CommitTree: RP child rec={rec.RecordingId ?? "<no-id>"} " +
                        $"vessel='{rec.VesselName ?? "<unnamed>"}' not promoted reason={qualifyReason} " +
                        $"rp={rp.RewindPointId ?? "<no-rp>"} slot={slotListIndex}");
                    continue;
                }

                if (ShouldAutoSealStableEvaCommitSlot(rec, qualifyReason, tree))
                {
                    if (AutoSealStableEvaCommitSlot(rec, rp, slot, slotListIndex, tree, qualifyReason))
                    {
                        autoSealed++;
                        continue;
                    }
                }

                // Demote the qualifying HEAD on first commit. The first-commit guard
                // keeps a manually-sealed HEAD==tip (Immutable) from being re-opened on a
                // later re-commit (plan §4.1).
                if (isFirstCommit(rec.RecordingId))
                {
                    rec.MergeState = MergeState.CommittedProvisional;
                    rec.FilesDirty = true;
                    promoted++;
                    ParsekLog.Info("UnfinishedFlights",
                        $"CommitTree promoted rec={rec.RecordingId ?? "<no-id>"} " +
                        $"vessel='{rec.VesselName ?? "<unnamed>"}' slot={slotListIndex} " +
                        $"rp={rp.RewindPointId ?? "<no-rp>"} reason={qualifyReason} " +
                        $"to CommittedProvisional");
                }
                else
                {
                    ParsekLog.Verbose("UnfinishedFlights",
                        $"CommitTree: rec={rec.RecordingId ?? "<no-id>"} already committed/fork — " +
                        $"not re-deriving MergeState (slot={slotListIndex} rp={rp.RewindPointId ?? "<no-rp>"})");
                }

                // Reach the slot's effective chain TIP. A continuation crash tip is born
                // Immutable and may not itself resolve to the RP (no branch link), so the
                // HEAD-driven demotion above misses it; the reaper's legacy
                // Immutable-qualifies workaround currently compensates. Demote the tip so
                // open/closed can be read directly from MergeState (plan §4.2). Tips are
                // disjoint across slots, so this cannot cross-close another slot.
                string tipId = slot.EffectiveRecordingId(supersedes);
                if (!string.IsNullOrEmpty(tipId)
                    && !string.Equals(tipId, rec.RecordingId, StringComparison.Ordinal)
                    && tree.Recordings.TryGetValue(tipId, out var tipRec)
                    && tipRec != null
                    && tipRec.MergeState == MergeState.Immutable
                    && isFirstCommit(tipId))
                {
                    tipRec.MergeState = MergeState.CommittedProvisional;
                    tipRec.FilesDirty = true;
                    tipsPromoted++;
                    ParsekLog.Info("UnfinishedFlights",
                        $"CommitTree promoted chain-tip rec={tipId} (head={rec.RecordingId ?? "<no-id>"}) " +
                        $"slot={slotListIndex} rp={rp.RewindPointId ?? "<no-rp>"} reason={qualifyReason} " +
                        $"to CommittedProvisional");
                }
            }

            if (promoted > 0 || tipsPromoted > 0 || autoSealed > 0)
                BumpStateVersion();
        }

        private static bool ShouldAutoSealStableEvaCommitSlot(
            Recording rec,
            string qualifyReason,
            RecordingTree tree)
        {
            if (!string.Equals(qualifyReason, "strandedEva", StringComparison.Ordinal))
                return false;

            Recording tip = EffectiveState.ResolveChainTerminalRecording(rec, tree);
            if (tip == null || string.IsNullOrEmpty(tip.EvaCrewName)
                || !tip.TerminalStateValue.HasValue)
                return false;

            return tip.TerminalStateValue.Value == TerminalState.Landed
                || tip.TerminalStateValue.Value == TerminalState.Splashed;
        }

        private static bool AutoSealStableEvaCommitSlot(
            Recording rec,
            RewindPoint rp,
            ChildSlot slot,
            int slotListIndex,
            RecordingTree tree,
            string qualifyReason)
        {
            if (slot == null)
                return false;

            Recording tip = EffectiveState.ResolveChainTerminalRecording(rec, tree);
            string terminal = tip?.TerminalStateValue.HasValue == true
                ? tip.TerminalStateValue.Value.ToString()
                : "<none>";

            // A stable-EVA conclusion is closed by leaving the slot's effective
            // tip Immutable (its born state). Open/closed is read from the tip
            // MergeState (the single source of truth), so closing the slot
            // means NOT demoting the first-commit tip to CommittedProvisional.
            // The caller skips the CP demotion when this returns true. No slot
            // bit and no state-version bump are needed: the tip never changes
            // state, so no consumer's cached open/closed view goes stale.
            ParsekLog.Info("UnfinishedFlights",
                $"CommitTree auto-sealed stable EVA slot={slotListIndex} " +
                $"rec={rec?.RecordingId ?? "<no-id>"} vessel='{rec?.VesselName ?? "<unnamed>"}' " +
                $"rp={rp?.RewindPointId ?? "<no-rp>"} terminal={terminal} reason={qualifyReason} " +
                $"(tip left Immutable = concluded)");
            return true;
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

        private static void AutoGroupTreeRecordings(RecordingTree tree)
        {
            RecordingGroupStore.AutoGroupTreeRecordings(tree, committedRecordings);
        }

        internal static bool IsAutoGeneratedTreeGroup(string groupName)
        {
            return RecordingGroupStore.IsAutoGeneratedTreeGroup(groupName, committedTrees);
        }

        internal static bool IsPermanentRootGroup(string groupName)
        {
            return RecordingGroupStore.IsPermanentRootGroup(groupName);
        }

        internal static bool IsPermanentGroup(string groupName)
        {
            return RecordingGroupStore.IsPermanentGroup(groupName, committedTrees);
        }

        internal static void MarkAutoAssignedStandaloneGroup(Recording rec, string groupName)
        {
            RecordingGroupStore.MarkAutoAssignedStandaloneGroup(rec, groupName);
        }

        private static void AdoptOrphanedRecordingsIntoTreeGroup(RecordingTree tree)
        {
            RecordingGroupStore.AdoptOrphanedRecordingsIntoTreeGroup(tree, committedRecordings);
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

        /// <summary>
        /// Dock-merge terminal-spawn supersession (phantom-rover fix). When a dock/board
        /// merge absorbs a Parsek-spawned or adopted vessel that has a committed terminal
        /// leaf (e.g. a landed rover a logistics transport docked into), that leaf's live
        /// vessel disappears from FlightGlobals (its pid is absorbed into the surviving
        /// merged vessel) WITHOUT dying. The pid-equality / name+UT branches of
        /// <see cref="ShouldMarkSupersededTerminalSpawn"/> never link the two because the
        /// merged continuation carries the SURVIVOR's pid, not the absorbed one. Left
        /// unmarked, the per-frame spawn-death check resets the leaf for re-spawn and
        /// KSCSpawn later materialises a duplicate "out of thin air" at the runway.
        ///
        /// Mark the absorbed leaf's terminal spawn superseded by the merged continuation so
        /// neither the flight nor the KSC spawn path re-materialises it, and clear its spawn
        /// state so the spawn-death loop goes quiet.
        ///
        /// Identity is keyed on the absorbed live vessel's pid (<paramref name="absorbedPid"/>,
        /// the dock branch point's TargetVesselPersistentId):
        ///   - a genuine Parsek spawn carries a KSP-unique spawn pid
        ///     (SpawnedVesselPersistentId != VesselPersistentId) -> pid-only match, collision-free;
        ///   - an adopted / originally-recorded leaf carries the craft-baked VesselPersistentId,
        ///     which a relaunch of the same craft reuses, so that route is guid-gated against the
        ///     absorbed vessel's live launch guid (#976-class).
        /// The VesselPersistentId route is durable: it does NOT depend on the spawn-death check
        /// having not yet zeroed SpawnedVesselPersistentId earlier in the same frame.
        ///
        /// Two accepted limitations:
        ///   - When the absorbed vessel's launch guid is unknown (e.g. snapshot guid backfill
        ///     failed) the baked-pid route falls back to pid-only, so several committed leaves
        ///     that share the same craft-baked pid could all be superseded by one dock. This is
        ///     benign over-suppression of historical duplicates and only arises in the abnormal
        ///     no-guid state; with guids present the gate disambiguates to the one launch.
        ///   - The unique-spawn-pid route is durable only while the merge runs before the same-
        ///     frame spawn-death reset (the normal one-frame-deferred dock ordering). The
        ///     reported bug is the adoption / baked-pid case, which is durable regardless.
        /// </summary>
        internal static int MarkTerminalSpawnSupersededByDockMerge(
            uint absorbedPid,
            string absorbedLaunchGuid,
            uint mergedPid,
            string mergedContinuationRecordingId,
            string logContext = "DockMerge")
        {
            // 0 = no resolvable target; == merged means the "target" survived AS the merged
            // vessel (its terminal spawn is owned by the live merged continuation, not lost).
            if (absorbedPid == 0 || absorbedPid == mergedPid)
                return 0;
            if (string.IsNullOrEmpty(mergedContinuationRecordingId))
                return 0;
            if (committedRecordings == null || committedRecordings.Count == 0)
                return 0;

            int marked = 0;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                Recording prior = committedRecordings[i];
                if (prior == null || string.IsNullOrEmpty(prior.RecordingId))
                    continue;
                if (string.Equals(prior.RecordingId, mergedContinuationRecordingId,
                        StringComparison.Ordinal))
                    continue;
                if (!string.IsNullOrEmpty(prior.TerminalSpawnSupersededByRecordingId))
                    continue;

                bool uniqueSpawnMatch = prior.SpawnedVesselPersistentId != 0
                    && prior.SpawnedVesselPersistentId == absorbedPid
                    && prior.SpawnedVesselPersistentId != prior.VesselPersistentId;
                bool bakedPidMatch = prior.VesselPersistentId != 0
                    && prior.VesselPersistentId == absorbedPid;
                if (!uniqueSpawnMatch && !bakedPidMatch)
                    continue;

                // Guid-gate the craft-baked-pid identity (reusable across relaunches of the
                // same craft); a unique spawn pid cannot collide so it stays pid-only. A
                // null/unknown guid is not conclusive -> falls back to pid-only.
                if (!uniqueSpawnMatch
                    && VesselLaunchIdentity.GuidsConclusivelyDiffer(
                        prior.RecordedVesselGuid, absorbedLaunchGuid))
                    continue;

                prior.TerminalSpawnSupersededByRecordingId = mergedContinuationRecordingId;
                prior.VesselSpawned = false;
                prior.SpawnedVesselPersistentId = 0;
                prior.FilesDirty = true;
                marked++;
                ParsekLog.Info("RecordingStore",
                    string.Format(CultureInfo.InvariantCulture,
                        "{0}: terminal spawn for recording '{1}' vessel='{2}' superseded by " +
                        "dock-merge continuation '{3}' absorbedPid={4} mergedPid={5} " +
                        "match={6} guid={7}",
                        logContext ?? "DockMerge",
                        prior.RecordingId,
                        prior.VesselName ?? "<unnamed>",
                        mergedContinuationRecordingId,
                        absorbedPid,
                        mergedPid,
                        uniqueSpawnMatch ? "spawn-pid" : "baked-pid",
                        absorbedLaunchGuid ?? "<unknown>"));
            }

            if (marked > 0)
                BumpStateVersion();
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

            // #976-class: an adoption-stamped prior carries SpawnedVesselPersistentId == its
            // craft-baked VesselPersistentId, which a relaunch of the same craft reuses as its own
            // VesselPersistentId, so a bare pid match would mark an unrelated later launch as
            // superseding this prior's terminal spawn. Guid-disambiguate only the adoption-stamp
            // case (real spawns use a KSP-unique spawn pid that cannot collide). A relaunch then
            // falls through to the name+UT-contiguity branch below, which rejects it (the relaunch's
            // tree starts after prior ends). Null/unknown guid keeps today's pid-only behavior.
            bool spawnedPidMatch = prior.SpawnedVesselPersistentId == continued.VesselPersistentId;
            bool adoptionRelaunchCollision = spawnedPidMatch
                && prior.SpawnedVesselPersistentId == prior.VesselPersistentId
                && VesselLaunchIdentity.GuidsConclusivelyDiffer(
                    prior.RecordedVesselGuid, continued.RecordedVesselGuid);
            if (spawnedPidMatch && !adoptionRelaunchCollision)
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
        private static void FinalizeTreeCommit(RecordingTree tree, int replaceCommittedTreeIndex = -1)
        {
            // Add all tree recordings to committedRecordings (enables ghost playback).
            // Skip recordings already present (chain segments committed mid-flight
            // by CommitRecordingDirect).
            int addedFromTree = 0;
            int replacedFromTree = 0;
            int preservedRewindSaves = 0;
            int preservedRuntimeFields = 0;
            // Live-vessel PID set for the stale-spawn-stamp guard in
            // PreserveLiveRuntimeFieldsOnReplace. Built once per commit (the set
            // is the same for every recording in the tree). null when no
            // populated vessel set is available, in which case the guard stays
            // inert and preserves spawn state as before (drop only on evidence).
            ICollection<uint> liveVesselPids = CollectLiveVesselPidsForReplaceGate();
            foreach (var rec in tree.Recordings.Values)
            {
                rec.FilesDirty = true;
                int existingIndex = FindCommittedRecordingIndex(rec);
                if (existingIndex >= 0)
                {
                    if (!ReferenceEquals(committedRecordings[existingIndex], rec))
                    {
                        var existing = committedRecordings[existingIndex];
                        bool savePreserved;
                        int otherPreserved;
                        PreserveLiveRuntimeFieldsOnReplace(
                            existing, rec, out savePreserved, out otherPreserved,
                            liveVesselPids);
                        if (savePreserved) preservedRewindSaves++;
                        preservedRuntimeFields += otherPreserved;
                        committedRecordings[existingIndex] = rec;
                        replacedFromTree++;
                    }
                    continue;
                }

                committedRecordings.Add(rec);
                addedFromTree++;
            }
            if (addedFromTree > 0 || replacedFromTree > 0)
                BumpStateVersion();

            // Flush to disk immediately to close the crash window.
            // If RunOptimizationPass runs after this, it will re-dirty modified
            // recordings and flush again with the final optimized state.
            FlushDirtyFiles(committedRecordings);

            // Ensure RecordedVesselPids is populated (covers runtime-created trees
            // that never went through RecordingTree.Load)
            tree.RebuildBackgroundMap();

            bool updatedCommittedTree = replaceCommittedTreeIndex >= 0
                && replaceCommittedTreeIndex < committedTrees.Count;
            if (updatedCommittedTree)
            {
                committedTrees[replaceCommittedTreeIndex] = tree;
                BumpStateVersion();
            }
            else
            {
                committedTrees.Add(tree);
            }

            string commitVerb = updatedCommittedTree ? "Updated committed tree" : "Committed tree";
            Log($"[Parsek] {commitVerb} '{tree.TreeName}' ({tree.Recordings.Count} recordings). " +
                $"Total committed: {committedRecordings.Count} recordings, {committedTrees.Count} trees");
            if (updatedCommittedTree)
                ParsekLog.Verbose("RecordingStore",
                    $"CommitTree: replaced committed tree index={replaceCommittedTreeIndex} " +
                    $"addedRecordings={addedFromTree} replacedRecordings={replacedFromTree} " +
                    $"preservedRewindSaves={preservedRewindSaves} " +
                    $"preservedRuntimeFields={preservedRuntimeFields}");
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

        private static int FindCommittedRecordingIndex(Recording rec)
        {
            if (rec == null) return -1;

            if (!string.IsNullOrEmpty(rec.RecordingId))
            {
                for (int i = 0; i < committedRecordings.Count; i++)
                {
                    var existing = committedRecordings[i];
                    if (existing != null &&
                        string.Equals(existing.RecordingId, rec.RecordingId, StringComparison.Ordinal))
                    {
                        return i;
                    }
                }
            }

            for (int i = 0; i < committedRecordings.Count; i++)
            {
                if (ReferenceEquals(committedRecordings[i], rec))
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Builds the live-vessel PID set the stale-spawn-stamp guard in
        /// <see cref="PreserveLiveRuntimeFieldsOnReplace"/> consults: the union
        /// of live loaded vessels (<c>FlightGlobals.Vessels</c>) and the
        /// save-shape mirror (<c>HighLogic.CurrentGame.flightState.protoVessels</c>).
        /// The union avoids false "vessel gone" verdicts for both loaded
        /// in-flight vessels (which may not yet be mirrored into
        /// <c>protoVessels</c>) and unloaded background vessels (absent from
        /// <c>FlightGlobals.Vessels</c>).
        ///
        /// <para>Returns <c>null</c> when no populated vessel set is available
        /// (no game / scene without vessels / collection failure). The guard
        /// treats <c>null</c> as "liveness unknown" and preserves spawn state
        /// exactly as before: a stale stamp is dropped only on positive
        /// evidence the vessel is gone, never on missing context. An empty
        /// collected set is normalized to <c>null</c> for the same reason (an
        /// empty set during a transition window must not reset every spawned
        /// recording).</para>
        /// </summary>
        private static HashSet<uint> CollectLiveVesselPidsForReplaceGate()
        {
            HashSet<uint> pids = null;
            try
            {
                var vessels = FlightGlobals.Vessels;
                if (vessels != null)
                {
                    pids = new HashSet<uint>();
                    for (int i = 0; i < vessels.Count; i++)
                    {
                        var v = vessels[i];
                        if (v != null && v.persistentId != 0u)
                            pids.Add(v.persistentId);
                    }
                }

                var protoVessels = HighLogic.CurrentGame?.flightState?.protoVessels;
                if (protoVessels != null)
                {
                    if (pids == null) pids = new HashSet<uint>();
                    for (int i = 0; i < protoVessels.Count; i++)
                    {
                        var pv = protoVessels[i];
                        if (pv != null && pv.persistentId != 0u)
                            pids.Add(pv.persistentId);
                    }
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("RecordingStore",
                    $"CollectLiveVesselPidsForReplaceGate: failed to collect live PIDs " +
                    $"({ex.GetType().Name}); spawn-stamp guard inert for this commit");
                return null;
            }

            return (pids != null && pids.Count > 0) ? pids : null;
        }

        /// <summary>
        /// Topology-update commits replace <c>committedRecordings[i]</c> wholesale
        /// with the pending tree's instance. The pending-tree path during a
        /// Re-Fly merge does not propagate runtime / spawn-state fields the
        /// live committed instance carries, so without this helper a merge
        /// silently drops:
        /// <list type="bullet">
        ///   <item>The launch-row Rewind save filename + the rewind-reserved
        ///         resource ledger (#573).</item>
        ///   <item>Spawn-state cluster from #264 (`VesselSpawned`,
        ///         `SpawnedVesselPersistentId`, `TerminalSpawnSupersededByRecordingId`,
        ///         spawn attempt / collision / death / abandon / walkback /
        ///         duplicate-blocker counters and flags).</item>
        ///   <item>Terminal-spawn safety state (`TerminalSpawnSafety*`).</item>
        ///   <item>Plain-Rewind-to-Launch suppression metadata
        ///         (`SpawnSuppressedByRewind*`, #573 / #589).</item>
        ///   <item>Diagnostic peaks: `MaxDistanceFromLaunch`,
        ///         `LastAppliedResourceIndex`, `SceneExitSituation`,
        ///         `VesselDestroyed`, `DistanceFromLaunch`, `VesselSituation`.</item>
        /// </list>
        /// Each field copies from <paramref name="existing"/> to
        /// <paramref name="incoming"/> only when the incoming value is at its
        /// default (null/empty/0/-1/NaN/false), so legitimate updates from the
        /// pending tree (e.g. a spawn flag the merge produced) win over the
        /// stale prior value.
        ///
        /// <para><paramref name="liveVesselPids"/> is the stale-spawn-stamp
        /// guard. When supplied (non-null) and <paramref name="existing"/>'s
        /// <c>SpawnedVesselPersistentId</c> names no vessel in the set, the
        /// spawn claim (<c>VesselSpawned</c> + <c>SpawnedVesselPersistentId</c>)
        /// is NOT re-installed onto the replacement, which keeps a Re-Fly merge
        /// from re-stamping a stripped vessel's PID. A stale stamp would
        /// otherwise poison the pid-keyed committed-tree matcher
        /// (<c>ParsekFlight.TryFindCommittedTreeForSpawnedVessel</c>'s
        /// <c>spawnedMatch</c> branch) when a later relaunch of the same craft
        /// recycles the craft-baked PID. Mirrors
        /// <c>ParsekScenario.ReconcileSpawnStateAfterStrip</c>. <c>null</c>
        /// disables the guard (preserve as before) for contexts that cannot
        /// determine liveness.</para>
        /// </summary>
        internal static void PreserveLiveRuntimeFieldsOnReplace(
            Recording existing, Recording incoming,
            out bool savePreserved, out int otherPreserved,
            ICollection<uint> liveVesselPids = null)
        {
            savePreserved = false;
            otherPreserved = 0;
            if (existing == null || incoming == null) return;

            // Launch save filename + reserved resource ledger.
            if (string.IsNullOrEmpty(incoming.RewindSaveFileName)
                && !string.IsNullOrEmpty(existing.RewindSaveFileName))
            {
                incoming.RewindSaveFileName = existing.RewindSaveFileName;
                savePreserved = true;
            }
            if (incoming.RewindReservedFunds == 0
                && existing.RewindReservedFunds != 0)
            {
                incoming.RewindReservedFunds = existing.RewindReservedFunds;
                otherPreserved++;
            }
            if (incoming.RewindReservedScience == 0
                && existing.RewindReservedScience != 0)
            {
                incoming.RewindReservedScience = existing.RewindReservedScience;
                otherPreserved++;
            }
            if (incoming.RewindReservedRep == 0f
                && existing.RewindReservedRep != 0f)
            {
                incoming.RewindReservedRep = existing.RewindReservedRep;
                otherPreserved++;
            }

            // Playback resource cursor.
            if (incoming.LastAppliedResourceIndex == -1
                && existing.LastAppliedResourceIndex != -1)
            {
                incoming.LastAppliedResourceIndex = existing.LastAppliedResourceIndex;
                otherPreserved++;
            }

            // Spawn-state cluster (#264).
            // Each field is preserved only when incoming is at its default for it
            // (the pending tree lost the field) and existing carries a value to
            // propagate. Computed once so the stale-stamp guard's log and the
            // copies below share one source of truth.
            bool copyVesselSpawned = !incoming.VesselSpawned && existing.VesselSpawned;
            bool copySpawnedPid =
                incoming.SpawnedVesselPersistentId == 0u
                && existing.SpawnedVesselPersistentId != 0u;

            // Stale-spawn-stamp guard: when a live-PID set is supplied and the
            // existing recording's spawned PID no longer names any live or
            // save-shape vessel, do NOT re-install the spawn claim. The
            // replacement keeps its default spawn state (VesselSpawned=false,
            // SpawnedVesselPersistentId=0) so the pid-keyed committed-tree
            // matcher cannot mistake a recycled craft pid for this recording.
            bool existingSpawnPidIsStale =
                liveVesselPids != null
                && existing.SpawnedVesselPersistentId != 0u
                && !liveVesselPids.Contains(existing.SpawnedVesselPersistentId);
            if (existingSpawnPidIsStale)
            {
                // Log only when the suppression actually prevented a copy, so a
                // recording that already carries its own spawn claim does not
                // produce a false-alarm "dropped" line.
                if (copyVesselSpawned || copySpawnedPid)
                    ParsekLog.Info("RecordingStore",
                        $"PreserveLiveRuntimeFieldsOnReplace: dropped stale spawn stamp " +
                        $"pid={existing.SpawnedVesselPersistentId} for recording " +
                        $"\"{incoming.VesselName}\" id={incoming.RecordingId ?? "<none>"} " +
                        $"(vessel no longer live): not re-installed on replacement " +
                        $"(incoming VesselSpawned={incoming.VesselSpawned} " +
                        $"spawnedPid={incoming.SpawnedVesselPersistentId})");
            }
            else
            {
                if (copyVesselSpawned)
                {
                    incoming.VesselSpawned = true;
                    otherPreserved++;
                }
                if (copySpawnedPid)
                {
                    incoming.SpawnedVesselPersistentId = existing.SpawnedVesselPersistentId;
                    otherPreserved++;
                }
            }
            if (string.IsNullOrEmpty(incoming.TerminalSpawnSupersededByRecordingId)
                && !string.IsNullOrEmpty(existing.TerminalSpawnSupersededByRecordingId))
            {
                incoming.TerminalSpawnSupersededByRecordingId =
                    existing.TerminalSpawnSupersededByRecordingId;
                otherPreserved++;
            }
            if (incoming.SpawnAttempts == 0 && existing.SpawnAttempts != 0)
            {
                incoming.SpawnAttempts = existing.SpawnAttempts;
                otherPreserved++;
            }
            if (incoming.CollisionBlockCount == 0 && existing.CollisionBlockCount != 0)
            {
                incoming.CollisionBlockCount = existing.CollisionBlockCount;
                otherPreserved++;
            }
            if (!incoming.SpawnAbandoned && existing.SpawnAbandoned)
            {
                incoming.SpawnAbandoned = true;
                otherPreserved++;
            }
            if (!incoming.WalkbackExhausted && existing.WalkbackExhausted)
            {
                incoming.WalkbackExhausted = true;
                otherPreserved++;
            }
            if (!incoming.DuplicateBlockerRecovered && existing.DuplicateBlockerRecovered)
            {
                incoming.DuplicateBlockerRecovered = true;
                otherPreserved++;
            }
            if (incoming.SpawnDeathCount == 0 && existing.SpawnDeathCount != 0)
            {
                incoming.SpawnDeathCount = existing.SpawnDeathCount;
                otherPreserved++;
            }

            // Terminal-spawn safety state.
            if (!incoming.TerminalSpawnSafetyDeferred && existing.TerminalSpawnSafetyDeferred)
            {
                incoming.TerminalSpawnSafetyDeferred = true;
                otherPreserved++;
            }
            if (!incoming.TerminalSpawnCannotSpawnSafely
                && existing.TerminalSpawnCannotSpawnSafely)
            {
                incoming.TerminalSpawnCannotSpawnSafely = true;
                otherPreserved++;
            }
            if (string.IsNullOrEmpty(incoming.TerminalSpawnSafetyReasonCode)
                && !string.IsNullOrEmpty(existing.TerminalSpawnSafetyReasonCode))
            {
                incoming.TerminalSpawnSafetyReasonCode = existing.TerminalSpawnSafetyReasonCode;
                otherPreserved++;
            }
            if (string.IsNullOrEmpty(incoming.TerminalSpawnSafetyReason)
                && !string.IsNullOrEmpty(existing.TerminalSpawnSafetyReason))
            {
                incoming.TerminalSpawnSafetyReason = existing.TerminalSpawnSafetyReason;
                otherPreserved++;
            }
            if (double.IsNaN(incoming.TerminalSpawnSafetyDecisionUT)
                && !double.IsNaN(existing.TerminalSpawnSafetyDecisionUT))
            {
                incoming.TerminalSpawnSafetyDecisionUT = existing.TerminalSpawnSafetyDecisionUT;
                otherPreserved++;
            }
            if (double.IsNaN(incoming.TerminalSpawnNextAttemptUT)
                && !double.IsNaN(existing.TerminalSpawnNextAttemptUT))
            {
                incoming.TerminalSpawnNextAttemptUT = existing.TerminalSpawnNextAttemptUT;
                otherPreserved++;
            }

            // Plain-Rewind-to-Launch suppression metadata (#573 / #589).
            if (!incoming.SpawnSuppressedByRewind && existing.SpawnSuppressedByRewind)
            {
                incoming.SpawnSuppressedByRewind = true;
                otherPreserved++;
            }
            if (string.IsNullOrEmpty(incoming.SpawnSuppressedByRewindReason)
                && !string.IsNullOrEmpty(existing.SpawnSuppressedByRewindReason))
            {
                incoming.SpawnSuppressedByRewindReason = existing.SpawnSuppressedByRewindReason;
                otherPreserved++;
            }
            if (double.IsNaN(incoming.SpawnSuppressedByRewindUT)
                && !double.IsNaN(existing.SpawnSuppressedByRewindUT))
            {
                incoming.SpawnSuppressedByRewindUT = existing.SpawnSuppressedByRewindUT;
                otherPreserved++;
            }

            // Diagnostic peaks / final-state metadata.
            // SceneExitSituation: do not resurrect a stale pre-destruction value when
            // the incoming recording carries a Destroyed terminal. MarkDestroyedAtTerminal
            // intentionally clears the field to -1, and treating that as "incoming
            // hasn't set it" would copy stale "Landed/Orbiting" data back over the
            // sealed Destroyed verdict on every commit/replace cycle.
            if (incoming.SceneExitSituation == -1
                && existing.SceneExitSituation != -1
                && !incoming.VesselDestroyed)
            {
                incoming.SceneExitSituation = existing.SceneExitSituation;
                otherPreserved++;
            }
            if (incoming.MaxDistanceFromLaunch == 0
                && existing.MaxDistanceFromLaunch != 0)
            {
                incoming.MaxDistanceFromLaunch = existing.MaxDistanceFromLaunch;
                otherPreserved++;
            }
            if (incoming.DistanceFromLaunch == 0 && existing.DistanceFromLaunch != 0)
            {
                incoming.DistanceFromLaunch = existing.DistanceFromLaunch;
                otherPreserved++;
            }
            if (!incoming.VesselDestroyed && existing.VesselDestroyed)
            {
                incoming.VesselDestroyed = true;
                otherPreserved++;
            }
            // VesselSituation: same rationale as SceneExitSituation above —
            // MarkDestroyedAtTerminal clears the human-readable string and a commit
            // cycle would otherwise copy "Landed on Kerbin" back from the existing
            // pre-destruction recording.
            if (string.IsNullOrEmpty(incoming.VesselSituation)
                && !string.IsNullOrEmpty(existing.VesselSituation)
                && !incoming.VesselDestroyed)
            {
                incoming.VesselSituation = existing.VesselSituation;
                otherPreserved++;
            }
        }

        /// <summary>
        /// Bug fix-refly-abandon-and-fork-persist §Bug2a: union the active-
        /// Re-Fly incoming tree's NEW recordings and branch points into the
        /// existing committed tree IN PLACE. Used by <see cref="CommitTree"/>
        /// when <see cref="ShouldReplaceCommittedTree"/> rejected the
        /// incoming as "not richer" / "missing-existing-ids" but the live
        /// Re-Fly session marker (<paramref name="marker"/>) names this
        /// tree — that combination means the incoming is a legitimate
        /// active-session partial view (trim-scope=ActiveRecOnly may have
        /// pruned BPs/recordings) and we want to keep the pre-existing
        /// content alongside the session's new fork.
        ///
        /// <para><b>Union semantics</b>:
        /// <list type="bullet">
        ///   <item><description>
        ///     Recordings keyed by <c>RecordingId</c>: incoming-only ids
        ///     are added to <paramref name="existing"/>; ids present in
        ///     both have the incoming value swapped in (the active session
        ///     authored the more recent state); existing-only ids are
        ///     kept untouched (the pre-Re-Fly debris / pre-rewind chain
        ///     content that the session never touched).
        ///     <para><b>Overwrite-loss caveat</b>: the shared-id swap is
        ///     by-reference. Any mutations the existing committed
        ///     Recording received between the active session's fork
        ///     (snapshot capture) and this commit — e.g. rename, group
        ///     reassignment, ledger-driven field updates — are silently
        ///     replaced with the active session's older view. For the
        ///     Re-Fly contract this is fine (the active session is the
        ///     authority on its own recordings), but DO NOT add fields
        ///     to <c>Recording</c> that are user-mutable outside the
        ///     active session without revisiting this overwrite policy.</para>
        ///   </description></item>
        ///   <item><description>
        ///     <c>BranchPoints</c> keyed by <c>Id</c>: same union shape.
        ///   </description></item>
        ///   <item><description>
        ///     <c>RootRecordingId</c>: keep existing if non-null, else
        ///     adopt incoming.
        ///   </description></item>
        ///   <item><description>
        ///     <c>ActiveRecordingId</c>: when the live marker is present,
        ///     prefer the incoming value (the active session is the
        ///     authority on what "active recording" means right now);
        ///     report whether the swap happened via the out parameter.
        ///   </description></item>
        /// </list>
        /// </para>
        ///
        /// Returns true on success. Returns false (with all out params
        /// zeroed) only when inputs are null or fundamentally
        /// inconsistent — the strict-replace fallback below handles
        /// duplicate-skip semantics in that case.
        /// </summary>
        private static bool TryUnionActiveReFlyTreeIntoCommitted(
            RecordingTree existing,
            RecordingTree incoming,
            ReFlySessionMarker marker,
            out int addedRecs,
            out int addedBps,
            out bool activeIdSwapped)
        {
            addedRecs = 0;
            addedBps = 0;
            activeIdSwapped = false;
            if (existing == null || incoming == null || marker == null) return false;
            if (existing.Recordings == null || incoming.Recordings == null) return false;

            // Union recordings: add incoming-only, overwrite shared, keep
            // existing-only.
            foreach (var kvp in incoming.Recordings)
            {
                if (string.IsNullOrEmpty(kvp.Key) || kvp.Value == null) continue;
                if (existing.Recordings.ContainsKey(kvp.Key))
                {
                    if (!ReferenceEquals(existing.Recordings[kvp.Key], kvp.Value))
                        existing.Recordings[kvp.Key] = kvp.Value;
                }
                else
                {
                    existing.Recordings.Add(kvp.Key, kvp.Value);
                    addedRecs++;
                }
            }

            // Union branch points (by Id).
            if (incoming.BranchPoints != null)
            {
                if (existing.BranchPoints == null)
                    existing.BranchPoints = new List<BranchPoint>();
                var existingBpIds = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < existing.BranchPoints.Count; i++)
                {
                    var bp = existing.BranchPoints[i];
                    if (bp != null && !string.IsNullOrEmpty(bp.Id))
                        existingBpIds.Add(bp.Id);
                }
                for (int i = 0; i < incoming.BranchPoints.Count; i++)
                {
                    var bp = incoming.BranchPoints[i];
                    if (bp == null || string.IsNullOrEmpty(bp.Id)) continue;
                    if (!existingBpIds.Contains(bp.Id))
                    {
                        existing.BranchPoints.Add(bp);
                        addedBps++;
                    }
                }
            }

            // Root id: keep existing if set; else adopt incoming.
            if (string.IsNullOrEmpty(existing.RootRecordingId)
                && !string.IsNullOrEmpty(incoming.RootRecordingId))
            {
                existing.RootRecordingId = incoming.RootRecordingId;
            }

            // Active id: prefer incoming when an active marker exists. The
            // marker is the authority on "what the user is doing now". The
            // splitter's Step 2.12 promotion may have already promoted
            // incoming.ActiveRecordingId from HEAD to TIP; the migrate
            // helper (§Bug2b) may further promote it to the fork. Either
            // way the incoming value is what we want.
            if (!string.IsNullOrEmpty(incoming.ActiveRecordingId)
                && !string.Equals(existing.ActiveRecordingId,
                                  incoming.ActiveRecordingId,
                                  StringComparison.Ordinal))
            {
                existing.ActiveRecordingId = incoming.ActiveRecordingId;
                activeIdSwapped = true;
            }

            // RebuildBackgroundMap: the recordings dict just changed.
            existing.RebuildBackgroundMap();
            return true;
        }

        /// <summary>
        /// Internal shim that lets <c>MergeJournalOrchestrator.
        /// MigrateActiveReFlyForkIntoCommittedTree</c> call the private
        /// union helper. The same union logic is used by
        /// <see cref="CommitTree"/>'s active-Re-Fly path and by the
        /// merge journal's new <c>TreeMerge</c> phase; centralizing the
        /// semantics in <see cref="TryUnionActiveReFlyTreeIntoCommitted"/>
        /// keeps the two call sites consistent. Returns whether the helper
        /// produced a successful union; the shim drops the
        /// <c>activeIdSwapped</c> out param because the merge-phase caller
        /// reads <c>committedTree.ActiveRecordingId</c> directly to build
        /// its log line.
        ///
        /// <para><b>Only caller</b>:
        /// <c>MergeJournalOrchestrator.MigrateActiveReFlyForkIntoCommittedTree</c>
        /// at the <c>TreeMerge</c> phase. If you're adding a third caller
        /// or generalizing the union semantics, audit both call sites for
        /// the "marker present + tree id matches" precondition this
        /// helper inherits from the private callee.</para>
        /// </summary>
        internal static bool UnionActiveReFlyTreeIntoCommittedForMerge(
            RecordingTree existing, RecordingTree incoming, ReFlySessionMarker marker,
            out int addedRecs, out int addedBps)
        {
            return TryUnionActiveReFlyTreeIntoCommitted(
                existing, incoming, marker, out addedRecs, out addedBps, out _);
        }

        private static bool ShouldReplaceCommittedTree(
            RecordingTree existing,
            RecordingTree incoming,
            out string reason)
        {
            reason = "no-topology-change";
            if (existing == null || incoming == null)
                return false;

            int existingRecordingCount = existing.Recordings?.Count ?? 0;
            int incomingRecordingCount = incoming.Recordings?.Count ?? 0;
            int existingBranchCount = existing.BranchPoints?.Count ?? 0;
            int incomingBranchCount = incoming.BranchPoints?.Count ?? 0;
            if (incomingRecordingCount < existingRecordingCount ||
                incomingBranchCount < existingBranchCount)
            {
                reason =
                    $"incoming-not-richer oldRecordings={existingRecordingCount} " +
                    $"newRecordings={incomingRecordingCount} oldBranchPoints={existingBranchCount} " +
                    $"newBranchPoints={incomingBranchCount}";
                return false;
            }

            int missingRecordingIds = CountMissingRecordingIds(existing, incoming);
            int missingBranchPointIds = CountMissingBranchPointIds(existing, incoming);
            if (missingRecordingIds > 0 || missingBranchPointIds > 0)
            {
                reason =
                    $"incoming-missing-existing-ids missingRecordingIds={missingRecordingIds} " +
                    $"missingBranchPointIds={missingBranchPointIds}";
                return false;
            }

            int newRecordingIds = CountNewRecordingIds(existing, incoming);
            int newBranchPointIds = CountNewBranchPointIds(existing, incoming);
            bool rootChanged = !string.Equals(
                existing.RootRecordingId,
                incoming.RootRecordingId,
                StringComparison.Ordinal);
            bool activeChanged = !string.Equals(
                existing.ActiveRecordingId,
                incoming.ActiveRecordingId,
                StringComparison.Ordinal);
            bool recordingTopologyChanged = HasRecordingTopologyDifference(existing, incoming);
            bool branchTopologyChanged = HasBranchPointTopologyDifference(existing, incoming);
            bool recordingPayloadChanged = HasRecordingPayloadDifference(existing, incoming);

            bool replace =
                newRecordingIds > 0 ||
                newBranchPointIds > 0 ||
                rootChanged ||
                activeChanged ||
                recordingTopologyChanged ||
                branchTopologyChanged ||
                recordingPayloadChanged;

            reason =
                $"newRecordingIds={newRecordingIds} newBranchPointIds={newBranchPointIds} " +
                $"rootChanged={rootChanged} activeChanged={activeChanged} " +
                $"recordingTopologyChanged={recordingTopologyChanged} " +
                $"branchTopologyChanged={branchTopologyChanged} " +
                $"recordingPayloadChanged={recordingPayloadChanged}";
            return replace;
        }

        private static int CountNewRecordingIds(RecordingTree existing, RecordingTree incoming)
        {
            if (existing?.Recordings == null || incoming?.Recordings == null)
                return 0;

            int count = 0;
            foreach (var id in incoming.Recordings.Keys)
            {
                if (!existing.Recordings.ContainsKey(id))
                    count++;
            }

            return count;
        }

        private static int CountMissingRecordingIds(RecordingTree existing, RecordingTree incoming)
        {
            if (existing?.Recordings == null || incoming?.Recordings == null)
                return 0;

            int count = 0;
            foreach (var id in existing.Recordings.Keys)
            {
                if (!incoming.Recordings.ContainsKey(id))
                    count++;
            }

            return count;
        }

        private static int CountNewBranchPointIds(RecordingTree existing, RecordingTree incoming)
        {
            if (existing?.BranchPoints == null || incoming?.BranchPoints == null)
                return 0;

            var existingIds = new HashSet<string>(
                existing.BranchPoints
                    .Where(bp => bp != null && !string.IsNullOrEmpty(bp.Id))
                    .Select(bp => bp.Id),
                StringComparer.Ordinal);

            int count = 0;
            foreach (var bp in incoming.BranchPoints)
            {
                if (bp != null &&
                    !string.IsNullOrEmpty(bp.Id) &&
                    !existingIds.Contains(bp.Id))
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountMissingBranchPointIds(RecordingTree existing, RecordingTree incoming)
        {
            if (existing?.BranchPoints == null || incoming?.BranchPoints == null)
                return 0;

            var incomingIds = new HashSet<string>(
                incoming.BranchPoints
                    .Where(bp => bp != null && !string.IsNullOrEmpty(bp.Id))
                    .Select(bp => bp.Id),
                StringComparer.Ordinal);

            int count = 0;
            foreach (var bp in existing.BranchPoints)
            {
                if (bp != null &&
                    !string.IsNullOrEmpty(bp.Id) &&
                    !incomingIds.Contains(bp.Id))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool HasRecordingTopologyDifference(
            RecordingTree existing,
            RecordingTree incoming)
        {
            if (existing?.Recordings == null || incoming?.Recordings == null)
                return false;

            foreach (var kvp in incoming.Recordings)
            {
                Recording incomingRec = kvp.Value;
                if (incomingRec == null ||
                    !existing.Recordings.TryGetValue(kvp.Key, out var existingRec) ||
                    existingRec == null)
                {
                    continue;
                }

                if (!string.Equals(
                        existingRec.ParentBranchPointId,
                        incomingRec.ParentBranchPointId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        existingRec.ChildBranchPointId,
                        incomingRec.ChildBranchPointId,
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasRecordingPayloadDifference(
            RecordingTree existing,
            RecordingTree incoming)
        {
            if (existing?.Recordings == null || incoming?.Recordings == null)
                return false;

            foreach (var kvp in incoming.Recordings)
            {
                Recording incomingRec = kvp.Value;
                if (incomingRec == null ||
                    !existing.Recordings.TryGetValue(kvp.Key, out var existingRec) ||
                    existingRec == null)
                {
                    continue;
                }

                if (HasRecordingPayloadDifference(existingRec, incomingRec))
                    return true;
            }

            return false;
        }

        private static bool HasRecordingPayloadDifference(Recording existing, Recording incoming)
        {
            return CountOf(existing.Points) != CountOf(incoming.Points) ||
                CountOf(existing.OrbitSegments) != CountOf(incoming.OrbitSegments) ||
                CountOf(existing.PartEvents) != CountOf(incoming.PartEvents) ||
                CountOf(existing.FlagEvents) != CountOf(incoming.FlagEvents) ||
                CountOf(existing.SegmentEvents) != CountOf(incoming.SegmentEvents) ||
                CountOf(existing.TrackSections) != CountOf(incoming.TrackSections) ||
                !SameDouble(existing.StartUT, incoming.StartUT) ||
                !SameDouble(existing.EndUT, incoming.EndUT) ||
                existing.VesselPersistentId != incoming.VesselPersistentId ||
                existing.SpawnedVesselPersistentId != incoming.SpawnedVesselPersistentId ||
                existing.VesselSpawned != incoming.VesselSpawned ||
                existing.TerminalStateValue != incoming.TerminalStateValue ||
                existing.EndpointPhase != incoming.EndpointPhase ||
                !string.Equals(existing.EndpointBodyName, incoming.EndpointBodyName, StringComparison.Ordinal) ||
                !string.Equals(existing.TerminalOrbitBody, incoming.TerminalOrbitBody, StringComparison.Ordinal);
        }

        private static int CountOf<T>(ICollection<T> items)
        {
            return items?.Count ?? 0;
        }

        private static bool SameDouble(double left, double right)
        {
            if (double.IsNaN(left) && double.IsNaN(right))
                return true;

            return left.Equals(right);
        }

        private static bool HasBranchPointTopologyDifference(
            RecordingTree existing,
            RecordingTree incoming)
        {
            if (existing?.BranchPoints == null || incoming?.BranchPoints == null)
                return false;

            var existingById = existing.BranchPoints
                .Where(bp => bp != null && !string.IsNullOrEmpty(bp.Id))
                .GroupBy(bp => bp.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            foreach (var incomingBp in incoming.BranchPoints)
            {
                if (incomingBp == null ||
                    string.IsNullOrEmpty(incomingBp.Id) ||
                    !existingById.TryGetValue(incomingBp.Id, out var existingBp))
                {
                    continue;
                }

                if (!SameStringSet(existingBp.ParentRecordingIds, incomingBp.ParentRecordingIds) ||
                    !SameStringSet(existingBp.ChildRecordingIds, incomingBp.ChildRecordingIds))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SameStringSet(List<string> left, List<string> right)
        {
            if (left == null || left.Count == 0)
                return right == null || right.Count == 0;
            if (right == null || left.Count != right.Count)
                return false;

            var set = new HashSet<string>(left, StringComparer.Ordinal);
            return set.SetEquals(right);
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
            pendingTreeSerializedForSave = false;
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

        internal static void MarkPendingTreeSerializedForSave(string context)
        {
            if (pendingTree == null)
            {
                ParsekLog.Verbose("RecordingStore",
                    $"MarkPendingTreeSerializedForSave skipped: no pending tree context={context ?? "<none>"}");
                return;
            }

            pendingTreeSerializedForSave = true;
            ParsekLog.Verbose("RecordingStore",
                $"Pending tree '{pendingTree.TreeName}' marked as serialized " +
                $"(state={pendingTreeState}, context={context ?? "<none>"})");
        }

        internal static void RestorePendingTreeFromSave(RecordingTree tree)
        {
            if (tree == null)
            {
                ParsekLog.Verbose("RecordingStore",
                    "RestorePendingTreeFromSave called with null tree");
                return;
            }

            if (pendingTree != null)
            {
                ParsekLog.Warn("RecordingStore",
                    $"RestorePendingTreeFromSave: overwriting existing pending tree " +
                    $"'{pendingTree.TreeName}' (state={pendingTreeState}, serialized={pendingTreeSerializedForSave}) " +
                    $"with saved pending tree '{tree.TreeName}'");
            }

            pendingTree = tree;
            // This path reinstalls saved isPending nodes (Finalized pending trees) only.
            // Limbo resume trees take the isActive marker instead and round-trip through
            // TryRestoreActiveTreeNode / StashPendingTree, never here, so reinstating as
            // Finalized is correct. Does not arm PendingStashedThisTransition.
            pendingTreeState = PendingTreeState.Finalized;
            PendingStashedThisTransition = false;
            pendingTreeSerializedForSave = true;
            Log($"[Parsek] Restored pending tree '{tree.TreeName}' from save " +
                $"({tree.Recordings.Count} recordings, state=Finalized, stashedThisTransition=False)");
        }

        internal static void PreservePendingTreeFromSaveDuringActiveRestore(RecordingTree tree)
        {
            if (tree == null)
            {
                ParsekLog.Verbose("RecordingStore",
                    "PreservePendingTreeFromSaveDuringActiveRestore called with null tree");
                return;
            }

            if (savedPendingTreeDuringActiveRestore != null)
            {
                ParsekLog.Warn("RecordingStore",
                    $"PreservePendingTreeFromSaveDuringActiveRestore: overwriting saved pending tree " +
                    $"'{savedPendingTreeDuringActiveRestore.TreeName}' with '{tree.TreeName}'");
            }

            savedPendingTreeDuringActiveRestore = tree;
            savedPendingTreeDuringActiveRestoreSerializedForSave = true;
            Log($"[Parsek] Preserved saved pending tree '{tree.TreeName}' while active-tree restore owns " +
                $"the pending-Limbo slot ({tree.Recordings.Count} recordings)");
        }

        internal static bool MarkSavedPendingTreeDuringActiveRestoreSerializedForSave(string context)
        {
            if (savedPendingTreeDuringActiveRestore == null)
            {
                ParsekLog.Verbose("RecordingStore",
                    $"MarkSavedPendingTreeDuringActiveRestoreSerializedForSave skipped: no saved pending tree context={context ?? "<none>"}");
                return false;
            }

            savedPendingTreeDuringActiveRestoreSerializedForSave = true;
            ParsekLog.Verbose("RecordingStore",
                $"Saved pending tree '{savedPendingTreeDuringActiveRestore.TreeName}' preserved during active restore " +
                $"marked as serialized (context={context ?? "<none>"})");
            return true;
        }

        internal static bool PromoteSavedPendingTreeAfterActiveRestore(string context)
        {
            if (savedPendingTreeDuringActiveRestore == null)
                return false;

            if (pendingTree != null)
            {
                ParsekLog.Warn("RecordingStore",
                    $"PromoteSavedPendingTreeAfterActiveRestore: cannot promote saved pending tree " +
                    $"'{savedPendingTreeDuringActiveRestore.TreeName}' because pending slot is occupied by " +
                    $"'{pendingTree.TreeName}' (state={pendingTreeState}, context={context ?? "<none>"})");
                return false;
            }

            pendingTree = savedPendingTreeDuringActiveRestore;
            pendingTreeState = PendingTreeState.Finalized;
            pendingTreeSerializedForSave = savedPendingTreeDuringActiveRestoreSerializedForSave;
            PendingStashedThisTransition = false;

            savedPendingTreeDuringActiveRestore = null;
            savedPendingTreeDuringActiveRestoreSerializedForSave = false;

            Log($"[Parsek] Promoted saved pending tree '{pendingTree.TreeName}' after active-tree restore " +
                $"({pendingTree.Recordings.Count} recordings, serialized={pendingTreeSerializedForSave}, " +
                $"context={context ?? "<none>"})");
            return true;
        }

        internal static void ArmCommittedTreeRestoreAttempt(RecordingTree tree, string reason)
        {
            if (tree == null || string.IsNullOrEmpty(tree.Id))
            {
                ParsekLog.Warn("RecordingStore",
                    $"ArmCommittedTreeRestoreAttempt skipped: tree={(tree == null ? "<null>" : "<no-id>")} " +
                    $"reason={reason ?? "<none>"}");
                return;
            }

            if (!string.IsNullOrEmpty(committedTreeRestoreAttemptTreeId))
            {
                ParsekLog.Warn("RecordingStore",
                    $"ArmCommittedTreeRestoreAttempt: replacing stale context " +
                    $"tree={committedTreeRestoreAttemptTreeId} " +
                    $"reason={committedTreeRestoreAttemptReason ?? "<none>"}");
            }

            committedTreeRestoreAttemptTreeId = tree.Id;
            committedTreeRestoreAttemptReason = reason ?? "<unspecified>";
            committedTreeRestoreAttemptRecordingIds =
                BuildCommittedTreeRestoreRecordingIds(tree);
            committedTreeRestoreAttemptEventCutoffs =
                BuildCommittedTreeRestoreEventCutoffs(tree);
            ParsekLog.Info("RecordingStore",
                $"Armed committed-tree restore attempt for '{tree.TreeName ?? "<unnamed>"}' " +
                $"(id={tree.Id}, recordings={tree.Recordings?.Count ?? 0}, " +
                $"cutoffs={committedTreeRestoreAttemptEventCutoffs.Count}, " +
                $"reason={committedTreeRestoreAttemptReason})");
        }

        internal static void ClearCommittedTreeRestoreAttempt(string reason)
        {
            if (string.IsNullOrEmpty(committedTreeRestoreAttemptTreeId))
                return;

            ParsekLog.Verbose("RecordingStore",
                $"Cleared committed-tree restore attempt " +
                $"tree={committedTreeRestoreAttemptTreeId} " +
                $"reason={reason ?? "<none>"} " +
                $"armedReason={committedTreeRestoreAttemptReason ?? "<none>"}");
            committedTreeRestoreAttemptTreeId = null;
            committedTreeRestoreAttemptReason = null;
            committedTreeRestoreAttemptRecordingIds = null;
            committedTreeRestoreAttemptEventCutoffs = null;
        }

        private static void ClearCommittedTreeRestoreAttemptForTree(string treeId, string reason)
        {
            if (string.IsNullOrEmpty(committedTreeRestoreAttemptTreeId))
                return;
            if (!string.Equals(committedTreeRestoreAttemptTreeId, treeId, StringComparison.Ordinal))
                return;

            ClearCommittedTreeRestoreAttempt(reason);
        }

        /// <summary>
        /// True when <paramref name="treeId"/> is the tree currently armed as a
        /// committed-tree restore attempt (a copy-on-write clone of a committed
        /// tree is the live active tree). Used by the in-flight discard helper to
        /// detect a live committed clone BEFORE the discard clears the attempt,
        /// so it can tear the clone down (the committed original survives in
        /// committedTrees) instead of leaving it to strand.
        /// </summary>
        internal static bool IsCommittedTreeRestoreAttemptTree(string treeId)
        {
            return !string.IsNullOrEmpty(treeId)
                && string.Equals(committedTreeRestoreAttemptTreeId, treeId,
                    StringComparison.Ordinal);
        }

        internal static bool IsCommittedTreeRestoreAttemptRecordingId(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)
                || committedTreeRestoreAttemptRecordingIds == null)
                return false;

            return committedTreeRestoreAttemptRecordingIds.Contains(recordingId);
        }

        internal static bool ShouldSuppressCommittedTreeRestoreAttemptEventPersistence(
            GameStateEvent evt)
        {
            string recordingId = evt.recordingId;
            if (string.IsNullOrEmpty(recordingId)
                || string.IsNullOrEmpty(committedTreeRestoreAttemptTreeId))
            {
                return false;
            }

            // Switch-segment narrowing (segment-scoped-switch-fly-autorecord Phase D):
            // a marker-owned new recording id created by SwitchSegmentSession is real
            // pending state and must survive F5/save/reload, even when a #866
            // committed-tree restore attempt is concurrently armed. Bypass before the
            // existing same-id cutoff / pending-only checks so the suppression contract
            // for original committed recording ids remains unchanged.
            if (IsMarkerOwnedSwitchSegmentRecordingId(recordingId))
            {
                var session = ParsekScenario.Instance?.ActiveSwitchSegmentSession;
                ParsekLog.Verbose("RecordingStore",
                    $"event persistence not-suppressed reason=marker-owned-switch-segment " +
                    $"recId={recordingId} " +
                    $"sessionId={(session != null ? session.SessionId.ToString("D", CultureInfo.InvariantCulture) : "<null>")}");
                return false;
            }

            if (committedTreeRestoreAttemptEventCutoffs != null
                && committedTreeRestoreAttemptEventCutoffs.TryGetValue(
                    recordingId,
                    out double cutoffUT))
            {
                return evt.ut > cutoffUT;
            }

            return IsPendingOnlyCommittedTreeRestoreAttemptRecordingId(recordingId);
        }

        /// <summary>
        /// Returns true when <paramref name="recordingId"/> identifies a recording
        /// owned by the currently active <see cref="SwitchSegmentSession"/> — i.e.
        /// the recording's <see cref="Recording.SwitchSegmentSessionId"/> matches
        /// the live session's <c>SessionId</c>. Used by #866 suppression / save
        /// sites to exempt marker-owned new segment recording ids from same-id
        /// committed-tree restore-attempt suppression. Returns false when no
        /// session is armed, no matching recording is found, or the recording's
        /// stamp belongs to a different / no session.
        ///
        /// <para>Looks up the recording in committed storage, then the pending
        /// tree, then the active tree (via <see cref="ParsekFlight.Instance"/>).
        /// Pure ownership query: no Unity globals and no mutation of suppression
        /// state. <c>[ERS-exempt]</c> rationale: this is a metadata-only
        /// ownership predicate that walks the raw committed list to match by
        /// recording id — exactly the kind of structural query the allowlist
        /// already grants <c>RecordingStore.cs</c>.</para>
        /// </summary>
        internal static bool IsMarkerOwnedSwitchSegmentRecordingId(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return false;

            var session = ParsekScenario.Instance?.ActiveSwitchSegmentSession;
            if (session == null)
                return false;

            Recording rec = FindRecordingByIdAcrossStores(recordingId);
            if (rec == null || string.IsNullOrEmpty(rec.SwitchSegmentSessionId))
                return false;

            string activeSessionId = session.SessionId.ToString(
                "D",
                CultureInfo.InvariantCulture);
            return string.Equals(
                rec.SwitchSegmentSessionId,
                activeSessionId,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Looks up a Recording by id across committed storage, the pending tree,
        /// the saved-pending-during-active-restore tree, and the active tree.
        /// Returns null if no matching recording is found anywhere. Used only by
        /// the switch-segment ownership predicate.
        /// </summary>
        private static Recording FindRecordingByIdAcrossStores(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return null;

            Recording rec = TryFindCommittedRecordingById(recordingId);
            if (rec != null)
                return rec;

            if (pendingTree?.Recordings != null
                && pendingTree.Recordings.TryGetValue(recordingId, out rec))
            {
                return rec;
            }

            if (savedPendingTreeDuringActiveRestore?.Recordings != null
                && savedPendingTreeDuringActiveRestore.Recordings.TryGetValue(
                    recordingId,
                    out rec))
            {
                return rec;
            }

            var activeTree = ParsekFlight.Instance?.ActiveTreeForSerialization;
            if (activeTree?.Recordings != null
                && activeTree.Recordings.TryGetValue(recordingId, out rec))
            {
                return rec;
            }

            return null;
        }

        private static bool IsPendingOnlyCommittedTreeRestoreAttemptRecordingId(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)
                || string.IsNullOrEmpty(committedTreeRestoreAttemptTreeId))
            {
                return false;
            }

            if (committedTreeRestoreAttemptRecordingIds != null
                && committedTreeRestoreAttemptRecordingIds.Contains(recordingId))
            {
                return false;
            }

            if (IsCommittedRecordingId(recordingId))
                return false;

            return TreeContainsRecordingId(
                    pendingTree,
                    committedTreeRestoreAttemptTreeId,
                    recordingId)
                || TreeContainsRecordingId(
                    savedPendingTreeDuringActiveRestore,
                    committedTreeRestoreAttemptTreeId,
                    recordingId)
                || ParsekFlight.IsActiveTreeRecordingIdForTree(
                    recordingId,
                    committedTreeRestoreAttemptTreeId);
        }

        private static bool TreeContainsRecordingId(
            RecordingTree tree,
            string treeId,
            string recordingId)
        {
            return tree != null
                && !string.IsNullOrEmpty(treeId)
                && string.Equals(tree.Id, treeId, StringComparison.Ordinal)
                && tree.Recordings != null
                && tree.Recordings.ContainsKey(recordingId);
        }

        private static HashSet<string> BuildCommittedTreeRestoreRecordingIds(RecordingTree tree)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (tree?.Recordings == null)
                return result;

            foreach (Recording rec in tree.Recordings.Values)
            {
                if (!string.IsNullOrEmpty(rec?.RecordingId))
                    result.Add(rec.RecordingId);
            }

            return result;
        }

        private static Dictionary<string, double> BuildCommittedTreeRestoreEventCutoffs(
            RecordingTree tree)
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            if (tree?.Recordings == null)
                return result;

            foreach (Recording rec in tree.Recordings.Values)
            {
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (!rec.HasActualTrajectoryBounds && double.IsNaN(rec.ExplicitEndUT))
                    continue;

                double cutoff = rec.EndUT;
                if (double.IsNaN(cutoff) || double.IsInfinity(cutoff))
                    continue;

                result[rec.RecordingId] = cutoff;
            }

            return result;
        }

        private static bool PendingTreeMatchesCommittedTreeRestoreAttempt()
        {
            return pendingTree != null
                && !string.IsNullOrEmpty(committedTreeRestoreAttemptTreeId)
                && string.Equals(
                    pendingTree.Id,
                    committedTreeRestoreAttemptTreeId,
                    StringComparison.Ordinal);
        }

        private static int PurgeCommittedTreeRestoreAttemptEventTailsForPendingDiscard()
        {
            if (!PendingTreeMatchesCommittedTreeRestoreAttempt()
                || committedTreeRestoreAttemptEventCutoffs == null
                || committedTreeRestoreAttemptEventCutoffs.Count == 0)
            {
                return 0;
            }

            int purged = 0;
            foreach (var kvp in committedTreeRestoreAttemptEventCutoffs)
            {
                purged += GameStateStore.PurgeEventsForRecordingAfterUT(
                    kvp.Key,
                    kvp.Value,
                    $"DiscardPendingTree committed-spawned-vessel attempt '{pendingTree.TreeName}'");
            }

            if (purged > 0)
            {
                ParsekLog.Warn("RecordingStore",
                    $"DiscardPendingTree: purged {purged} same-id game-state event tail(s) " +
                    $"for committed-tree restore attempt tree={committedTreeRestoreAttemptTreeId} " +
                    $"armedReason={committedTreeRestoreAttemptReason ?? "<none>"}");
            }

            return purged;
        }

        /// <summary>
        /// Arms a one-shot guard for scene transitions that intentionally throw
        /// away the active flight's in-memory tree. Discard Re-Fly uses this
        /// before loading the origin RP's save: the loaded save already contains
        /// the pre-Re-Fly state, so the outgoing scene must not auto-stash the
        /// discarded attempt as a pending merge tree.
        /// </summary>
        internal static void ArmNextTreeSceneExitCommitSuppression(string reason)
        {
            suppressNextTreeSceneExitCommit = true;
            suppressNextTreeSceneExitCommitReason =
                string.IsNullOrEmpty(reason) ? "<unspecified>" : reason;
            ParsekLog.Info("RecordingStore",
                $"Armed next tree scene-exit commit suppression reason='{suppressNextTreeSceneExitCommitReason}'");
        }

        internal static bool TryConsumeNextTreeSceneExitCommitSuppression(
            GameScenes destinationScene,
            out string reason)
        {
            reason = null;
            if (!suppressNextTreeSceneExitCommit)
                return false;

            reason = suppressNextTreeSceneExitCommitReason ?? "<unspecified>";
            suppressNextTreeSceneExitCommit = false;
            suppressNextTreeSceneExitCommitReason = null;
            ParsekLog.Info("RecordingStore",
                $"Consumed tree scene-exit commit suppression reason='{reason}' dest={destinationScene}");
            return true;
        }

        /// <summary>
        /// Read-only peek (no consume) of the tree-scene-exit-commit
        /// suppression flag. Production callers (the
        /// <c>HighLogic.LoadScene</c> prefix in
        /// <c>SceneExitInterceptor</c>) check this to detect that a
        /// transition is already owned by Discard Re-Fly so they can
        /// bypass without stealing the flag from
        /// <c>FinalizeTreeOnSceneChange</c>'s consume contract.
        /// </summary>
        internal static bool IsNextTreeSceneExitCommitSuppressionArmed
            => suppressNextTreeSceneExitCommit;

        /// <summary>
        /// Arms a one-shot guard for the next saved active-tree restore pass.
        /// Discard Re-Fly uses this after loading the origin RP save: the save can
        /// still contain an isActive tree for quickload resume, but this load is an
        /// intentional reset back to the RP, not a resume or merge candidate.
        /// </summary>
        internal static void ArmNextActiveTreeRestoreSuppression(string reason)
        {
            suppressNextActiveTreeRestore = true;
            suppressNextActiveTreeRestoreReason =
                string.IsNullOrEmpty(reason) ? "<unspecified>" : reason;
            ParsekLog.Info("RecordingStore",
                $"Armed next active-tree restore suppression reason='{suppressNextActiveTreeRestoreReason}'");
        }

        internal static bool TryConsumeNextActiveTreeRestoreSuppression(
            string context,
            out string reason)
        {
            reason = null;
            if (!suppressNextActiveTreeRestore)
                return false;

            reason = suppressNextActiveTreeRestoreReason ?? "<unspecified>";
            suppressNextActiveTreeRestore = false;
            suppressNextActiveTreeRestoreReason = null;
            ParsekLog.Info("RecordingStore",
                $"Consumed active-tree restore suppression reason='{reason}' context={context ?? "<none>"}");
            return true;
        }

        internal static bool NextActiveTreeRestoreSuppressionArmedForTesting
            => suppressNextActiveTreeRestore;

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
            pendingTreeSerializedForSave = false;
        }

        /// <summary>
        /// Discards the pending tree and cleans up its recording files.
        /// #431: also purges every <see cref="GameStateEvent"/> tagged with one of the
        /// tree's pending-only recording ids — both from the live store and from any
        /// milestone the flush-on-save path may have already moved them into. Contract
        /// snapshots orphaned by the purge (i.e. whose accept event was among the
        /// purged set) are removed too. Recording ids still present in committed
        /// history are preserved.
        /// </summary>
        public static void DiscardPendingTree()
        {
            if (pendingTree == null)
            {
                ParsekLog.Verbose("RecordingStore", "DiscardPendingTree called with no pending tree");
                return;
            }

            bool pendingMatchesCommittedRestoreAttempt =
                PendingTreeMatchesCommittedTreeRestoreAttempt();
            int purgedSameIdAttemptEventTails =
                PurgeCommittedTreeRestoreAttemptEventTailsForPendingDiscard();

            // #431: purge tagged events for pending-only recording IDs first. A pending
            // tree can intentionally reference a committed recording ID; those events,
            // milestone entries, contract snapshots, and sidecars belong to committed
            // history and must survive discard.
            var idsToPurge = new HashSet<string>();
            int skippedCommittedEventPurges = 0;
            foreach (var rec in pendingTree.Recordings.Values)
            {
                string recordingId = rec?.RecordingId;
                if (string.IsNullOrEmpty(recordingId))
                    continue;

                if (IsCommittedRecordingId(recordingId))
                {
                    skippedCommittedEventPurges++;
                    continue;
                }

                idsToPurge.Add(recordingId);
            }

            // Phase 11 of Rewind-to-Staging (design §3.5 invariant 7 / §6.10):
            // tree discard is the ONLY purge path for RewindPoints, supersede
            // relations, and ledger tombstones whose endpoints tie back to
            // the discarded tree. Use the actual pending tree instance and the
            // pending-only recording id set so same-id committed trees and
            // committed-overlap recordings are preserved.
            TreeDiscardPurge.PurgeTree(pendingTree, idsToPurge);
            if (idsToPurge.Count > 0)
                GameStateStore.PurgeEventsForRecordings(idsToPurge, $"DiscardPendingTree '{pendingTree.TreeName}'");
            if (skippedCommittedEventPurges > 0)
            {
                ParsekLog.Warn("RecordingStore",
                    $"DiscardPendingTree: skipped destructive event/milestone purge for " +
                    $"{skippedCommittedEventPurges} committed-overlap recording ID(s)" +
                    (pendingMatchesCommittedRestoreAttempt
                        ? $"; purged same-id attempt tails={purgedSameIdAttemptEventTails}"
                        : ""));
            }

            int skippedCommittedDeletes = 0;
            foreach (var rec in pendingTree.Recordings.Values)
            {
                if (IsCommittedRecordingId(rec?.RecordingId))
                {
                    skippedCommittedDeletes++;
                    continue;
                }
                DeleteRecordingFiles(rec);
            }
            if (skippedCommittedDeletes > 0)
            {
                ParsekLog.Warn("RecordingStore",
                    $"DiscardPendingTree: skipped deleting {skippedCommittedDeletes} recording sidecar set(s) " +
                    "because the recording ID still exists in committed history");
            }
            GameStateRecorder.PendingScienceSubjects.Clear();
            Log($"[Parsek] Discarded pending tree '{pendingTree.TreeName}' (state={pendingTreeState})");
            pendingTree = null;
            pendingTreeState = PendingTreeState.Finalized;
            pendingTreeSerializedForSave = false;
            if (pendingMatchesCommittedRestoreAttempt)
                ClearCommittedTreeRestoreAttempt("DiscardPendingTree abandoned restored active copy");
            ClearRewindReplayTargetScope();
        }

        /// <summary>
        /// Disposition path returned by <see cref="TryDiscardActiveSwitchSegmentAttempt"/>.
        /// Drives the caller's follow-up cleanup (clone-drop vs nothing) per
        /// plan §"Final Disposition After Scoped Discard". Bug 2 (post-#876
        /// playtest 2026-05-17) widened the scoped sweep to the topological
        /// subtree, so the historic second-dialog "remaining pending changes"
        /// branch is no longer used — the scoped Discard now removes the
        /// entire segment subtree on its own.
        /// </summary>
        internal enum SwitchSegmentDiscardDisposition
        {
            /// <summary>No active session was armed; nothing was discarded.</summary>
            NoActiveSession = 0,

            /// <summary>The segment was added inside a committed-tree restore
            /// clone. After pruning the segment subtree, the entire
            /// active/pending clone wrapper is dropped and the committed-tree
            /// restore attempt cleared.</summary>
            CommittedRestoreClone = 1,

            /// <summary>The segment was added to a non-committed pending
            /// tree. After pruning the segment subtree, the pruned pending
            /// tree remains in the pending slot.</summary>
            PendingTreePrune = 2,
        }

        /// <summary>
        /// Scene-exit Discard hook for an armed <see cref="SwitchSegmentSession"/>.
        /// Mirrors <see cref="MergeDialog.TryDiscardActiveReFlyAttempt"/>'s
        /// placement: <see cref="MergeDialog.MergeDiscard"/> calls this BEFORE
        /// falling back to the whole-pending-tree
        /// <see cref="DiscardPendingTree"/> path, so committed mission history
        /// is preserved when the only new pending work is the switch/Fly
        /// segment itself.
        ///
        /// <para>On success, removes the session's segment recording + every
        /// descendant in the segment subtree (regardless of
        /// <see cref="Recording.SwitchSegmentSessionId"/> stamp — debris from
        /// a Breakup-during-segment, EVA children, dock children all in
        /// scope) + their branch points + their game-state events + their
        /// sidecar files + the persisted <see cref="SwitchSegmentSession"/>
        /// marker. Preserves all committed recording IDs, their sidecars,
        /// their game-state events at or before the switch UT, and any
        /// pre-existing pending recordings the session did not author.</para>
        ///
        /// <para>Returns the disposition path so the caller can branch on
        /// clone-restore (drop the clone) vs pending-tree (segment subtree
        /// pruned in place). Returns
        /// <see cref="SwitchSegmentDiscardDisposition.NoActiveSession"/> when
        /// no session is armed; the caller should fall through to the regular
        /// whole-pending-tree discard.</para>
        ///
        /// <para>[ERS-exempt] rationale: this is a topology cleanup helper
        /// that mirrors the structural-id collection already done by
        /// <see cref="MergeDialog.CollectReFlyAttemptOwnedRecordingIds"/>;
        /// the surviving committed timeline is reconstructed via the normal
        /// <see cref="EffectiveState"/> pipeline after the helper returns.</para>
        /// </summary>
        internal static SwitchSegmentDiscardDisposition TryDiscardActiveSwitchSegmentAttempt(
            out string reason)
        {
            reason = null;
            var scenario = ParsekScenario.Instance;
            var session = scenario?.ActiveSwitchSegmentSession;
            if (session == null)
            {
                reason = "no-active-session";
                ParsekLog.Verbose("SwitchSegment",
                    $"TryDiscardActiveSwitchSegmentAttempt: {reason}");
                return SwitchSegmentDiscardDisposition.NoActiveSession;
            }

            string sessionIdStr = session.SessionId.ToString("D", CultureInfo.InvariantCulture);
            bool isCommittedRestoreClone =
                !string.IsNullOrEmpty(committedTreeRestoreAttemptTreeId)
                && (string.Equals(committedTreeRestoreAttemptTreeId, session.TreeId,
                    StringComparison.Ordinal)
                    || string.Equals(committedTreeRestoreAttemptTreeId, session.CommittedTreeId,
                        StringComparison.Ordinal));

            // Locate the tree the marker-owned recordings actually live in.
            // The session.TreeId may point at the pending tree, the active
            // (in-flight) tree, or a saved-pending-during-active-restore tree.
            RecordingTree segmentTree = FindSegmentTreeForSession(session);
            if (segmentTree == null)
            {
                reason = "session-tree-missing";
                ParsekLog.Warn("SwitchSegment",
                    $"TryDiscardActiveSwitchSegmentAttempt refused: " +
                    $"sessionId={sessionIdStr} treeId={session.TreeId ?? "<null>"} " +
                    $"reason={reason}");
                return SwitchSegmentDiscardDisposition.NoActiveSession;
            }

            // Bug 2 (post-#876 playtest 2026-05-17): collect the TOPOLOGICAL
            // subtree rooted at session.ActiveSegmentRecordingId. Sweeps all
            // descendants regardless of SwitchSegmentSessionId stamp — debris
            // from a Breakup-during-segment, EVA children, dock children. The
            // marker-only filter the original implementation used left debris
            // behind and tripped a second dialog; the second-dialog flow has
            // been deleted in favor of this broader sweep.
            var ownedIds = CollectSwitchSegmentSubtreeRecordingIds(segmentTree, session);
            int ownedCount = ownedIds.Count;

            // Snapshot session-authored branch point ids = current BPs minus
            // PreSessionBranchPointIds baseline. Mirrors the ReFly approach.
            HashSet<string> sessionAuthoredBpIds = CollectSessionAuthoredBranchPointIds(
                segmentTree, session);

            int purgedEvents = 0;
            int deletedSidecars = 0;
            if (ownedIds.Count > 0)
            {
                purgedEvents = GameStateStore.PurgeEventsForRecordings(
                    ownedIds,
                    $"SwitchSegment scoped discard sess={sessionIdStr}");

                foreach (string id in ownedIds)
                {
                    if (string.IsNullOrEmpty(id))
                        continue;
                    Recording rec;
                    if (segmentTree.Recordings != null
                        && segmentTree.Recordings.TryGetValue(id, out rec)
                        && rec != null)
                    {
                        DeleteRecordingFiles(rec);
                        deletedSidecars++;
                    }
                }
            }

            int removedRecordings = RemoveRecordingIdsFromTree(segmentTree, ownedIds);
            int removedBranchPoints = RemoveSessionAuthoredBranchPointsFromTree(
                segmentTree, sessionAuthoredBpIds);

            // Repair parent recording's ChildBranchPointId if it now points at
            // a removed BP. Mirrors PruneSessionCreatedBranchPoints.
            if (segmentTree.Recordings != null && sessionAuthoredBpIds.Count > 0)
            {
                foreach (var rec in segmentTree.Recordings.Values)
                {
                    if (rec == null) continue;
                    if (!string.IsNullOrEmpty(rec.ParentBranchPointId)
                        && sessionAuthoredBpIds.Contains(rec.ParentBranchPointId))
                    {
                        rec.ParentBranchPointId = null;
                        rec.MarkFilesDirty();
                    }
                    if (!string.IsNullOrEmpty(rec.ChildBranchPointId)
                        && sessionAuthoredBpIds.Contains(rec.ChildBranchPointId))
                    {
                        rec.ChildBranchPointId = null;
                        rec.MarkFilesDirty();
                    }
                }
            }

            // Repair ActiveRecordingId if it pointed at a removed segment.
            if (!string.IsNullOrEmpty(segmentTree.ActiveRecordingId)
                && ownedIds.Contains(segmentTree.ActiveRecordingId))
            {
                string priorActive = segmentTree.ActiveRecordingId;
                segmentTree.ActiveRecordingId = !string.IsNullOrEmpty(session.ParentRecordingId)
                    && segmentTree.Recordings != null
                    && segmentTree.Recordings.ContainsKey(session.ParentRecordingId)
                        ? session.ParentRecordingId
                        : null;
                ParsekLog.Info("SwitchSegment",
                    $"TryDiscardActiveSwitchSegmentAttempt: tree.ActiveRecordingId " +
                    $"reset from '{priorActive}' to '{segmentTree.ActiveRecordingId ?? "<null>"}' " +
                    $"sessionId={sessionIdStr}");
            }

            ParsekLog.Info("SwitchSegment",
                $"TryDiscardActiveSwitchSegmentAttempt pruned: " +
                $"sessionId={sessionIdStr} " +
                $"intentId={session.IntentId.ToString("D", CultureInfo.InvariantCulture)} " +
                $"entryReason={session.EntryReason} " +
                $"treeId={segmentTree.Id ?? "<null>"} " +
                $"ownedRecordingIds={ownedCount} " +
                $"removedRecordings={removedRecordings} " +
                $"removedBranchPoints={removedBranchPoints} " +
                $"purgedEvents={purgedEvents} " +
                $"deletedSidecars={deletedSidecars}");

            scenario.ClearSwitchSegmentSession("scoped-discard");

            // Final disposition.
            if (isCommittedRestoreClone)
            {
                // Drop the active clone wrapper if it lives in the pending
                // slot for this committed-tree restore. The original
                // committed tree remains untouched in committedTrees.
                bool droppedPending = false;
                if (pendingTree != null
                    && string.Equals(pendingTree.Id, committedTreeRestoreAttemptTreeId,
                        StringComparison.Ordinal))
                {
                    pendingTree = null;
                    pendingTreeState = PendingTreeState.Finalized;
                    pendingTreeSerializedForSave = false;
                    droppedPending = true;
                }

                ClearCommittedTreeRestoreAttempt(
                    "switch-segment-scoped-discard committed-restore-clone");
                GameStateRecorder.PendingScienceSubjects.Clear();
                ClearRewindReplayTargetScope();

                reason = "scoped-discard-success";
                ParsekLog.Info("SwitchSegment",
                    $"TryDiscardActiveSwitchSegmentAttempt disposition=committed-restore-clone " +
                    $"sessionId={sessionIdStr} droppedPendingClone={droppedPending} " +
                    $"committedTreeId={committedTreeRestoreAttemptTreeId ?? "<null>"}");
                return SwitchSegmentDiscardDisposition.CommittedRestoreClone;
            }

            reason = "scoped-discard-success";
            ParsekLog.Info("SwitchSegment",
                $"TryDiscardActiveSwitchSegmentAttempt disposition=pending-tree-prune " +
                $"sessionId={sessionIdStr} " +
                $"prunedTreeId={segmentTree.Id ?? "<null>"} " +
                $"prunedTreeRemainingRecordings={(segmentTree.Recordings?.Count ?? 0)}");
            return SwitchSegmentDiscardDisposition.PendingTreePrune;
        }

        /// <summary>
        /// Classifies the armed <see cref="SwitchSegmentSession"/>'s segment as a
        /// no-op (safe to auto-discard) or not, and reports the discard
        /// <see cref="SwitchSegmentDisposition"/>. The caller (ParsekFlight) MUST
        /// have flushed the live recorder into the active tree first
        /// (<see cref="ParsekFlight.FlushRecorderIntoActiveTreeForSerialization"/>)
        /// so the segment recording carries its in-flight payload.
        ///
        /// <para>Returns false (keep) — with a diagnostic <paramref name="reason"/>
        /// — when there is no session, the session tree / segment cannot be
        /// resolved, the segment is not the live active recording (so the flush
        /// did not populate it), or the pure
        /// <see cref="SwitchSegmentNoOpClassifier.IsNoOpSegment"/> predicate keeps
        /// it. Re-Fly / merge-journal guards live in the ParsekFlight wrapper.</para>
        /// </summary>
        internal static bool TryClassifyActiveSwitchSegmentNoOp(
            out string reason, out SwitchSegmentDisposition disposition)
        {
            reason = null;
            disposition = SwitchSegmentDisposition.None;

            var scenario = ParsekScenario.Instance;
            var session = object.ReferenceEquals(null, scenario)
                ? null
                : scenario.ActiveSwitchSegmentSession;
            if (session == null)
            {
                reason = "no-session";
                return false;
            }

            RecordingTree tree = FindSegmentTreeForSession(session);
            if (tree == null || tree.Recordings == null)
            {
                reason = "session-tree-missing";
                return false;
            }

            string segId = session.ActiveSegmentRecordingId;
            if (string.IsNullOrEmpty(segId)
                || !tree.Recordings.TryGetValue(segId, out Recording segment)
                || segment == null)
            {
                reason = "segment-missing";
                return false;
            }

            // The flush only populates activeTree.ActiveRecordingId. If the
            // segment is not the active recording its payload may be stale /
            // empty (e.g. a torn-down tree after a mid-segment destroy), so we
            // cannot evaluate it — keep, conservatively.
            if (!string.Equals(tree.ActiveRecordingId, segId, StringComparison.Ordinal))
            {
                reason = "segment-not-active-recording";
                return false;
            }

            // Descendants: the subtree set includes the segment itself, so a count
            // > 1 means dock / undock / EVA / decouple / breakup children exist.
            // Count 0 = segment absent from the walk (cannot evaluate) -> keep.
            var subtreeIds = CollectSwitchSegmentSubtreeRecordingIds(tree, session);
            if (subtreeIds.Count == 0)
            {
                reason = "segment-absent-from-subtree";
                return false;
            }
            bool hasDescendants = subtreeIds.Count > 1;

            // Disposition for the scene-exit teardown choice.
            bool isCommittedClone =
                !string.IsNullOrEmpty(committedTreeRestoreAttemptTreeId)
                && (string.Equals(committedTreeRestoreAttemptTreeId, session.TreeId,
                        StringComparison.Ordinal)
                    || string.Equals(committedTreeRestoreAttemptTreeId, session.CommittedTreeId,
                        StringComparison.Ordinal));
            if (isCommittedClone)
                disposition = SwitchSegmentDisposition.CommittedRestoreClone;
            else if (tree.Recordings.Count == subtreeIds.Count)
                disposition = SwitchSegmentDisposition.Standalone;
            else
                disposition = SwitchSegmentDisposition.BgMemberOrMixed;

            bool noOp = SwitchSegmentNoOpClassifier.IsNoOpSegment(
                segment, hasDescendants, out string keepReason);
            reason = noOp ? "no-op" : keepReason;

            ParsekLog.Verbose("SwitchSegment",
                $"TryClassifyActiveSwitchSegmentNoOp: sessionId={session.SessionId:D} " +
                $"segId={segId} disposition={disposition} subtreeCount={subtreeIds.Count} " +
                $"hasDescendants={hasDescendants} noOp={noOp} reason={reason ?? "<none>"}");

            return noOp;
        }

        /// <summary>
        /// Locates the in-memory tree that owns the active switch-segment
        /// session. Prefers the pending tree (where scene-exit Stash usually
        /// puts the active tree) and falls back to the live active tree, the
        /// saved-pending-during-active-restore tree, then committed trees.
        /// Returns null when the session's TreeId resolves to no in-memory
        /// tree (treated as a degenerate state by the caller).
        /// </summary>
        private static RecordingTree FindSegmentTreeForSession(SwitchSegmentSession session)
        {
            if (session == null || string.IsNullOrEmpty(session.TreeId))
                return null;

            if (pendingTree != null
                && string.Equals(pendingTree.Id, session.TreeId, StringComparison.Ordinal))
                return pendingTree;

            if (savedPendingTreeDuringActiveRestore != null
                && string.Equals(savedPendingTreeDuringActiveRestore.Id, session.TreeId,
                    StringComparison.Ordinal))
                return savedPendingTreeDuringActiveRestore;

            var activeTree = ParsekFlight.Instance?.ActiveTreeForSerialization;
            if (activeTree != null
                && string.Equals(activeTree.Id, session.TreeId, StringComparison.Ordinal))
                return activeTree;

            if (committedTrees != null)
            {
                for (int i = 0; i < committedTrees.Count; i++)
                {
                    var t = committedTrees[i];
                    if (t != null
                        && string.Equals(t.Id, session.TreeId, StringComparison.Ordinal))
                        return t;
                }
            }

            return null;
        }

        /// <summary>
        /// Cycle / pathological-fanout safety cap on the descendant-walk loop
        /// in <see cref="CollectSwitchSegmentSubtreeRecordingIds"/>. The
        /// queue-driven walk visits each branch-point edge once, with this
        /// cap guarding against a corrupted branch-point graph that forms a
        /// cycle or fans out wider than expected. A healthy production tree
        /// terminates in O(tree depth) iterations; reaching the cap means
        /// something is wrong, so the walk breaks and logs a Warn with the
        /// session id and the partial collection size so the failure leaves a
        /// diagnostic trail. Phase F review fix (1c): replaces a bare 1024
        /// literal that broke silently.
        /// </summary>
        internal const int SwitchSegmentRecordingTreeWalkMaxIterations = 1024;

        /// <summary>
        /// Bug 2 (post-#876 playtest 2026-05-17): collect the topological
        /// subtree rooted at <see cref="SwitchSegmentSession.ActiveSegmentRecordingId"/>
        /// in <paramref name="tree"/>. The closure walks every recording
        /// reachable via <see cref="Recording.ChildBranchPointId"/> →
        /// <see cref="BranchPoint.ChildRecordingIds"/>, regardless of the
        /// child's <see cref="Recording.SwitchSegmentSessionId"/> stamp. This
        /// is the load-bearing semantic difference from the original
        /// marker-only filter: debris from a Breakup-during-segment, EVA
        /// children, dock children — all in scope when topologically
        /// descended from the segment recording.
        ///
        /// <para>Pre-segment recordings in OTHER trees stay handled by the
        /// regular per-tree dialog (this function only walks the segment's
        /// own tree).</para>
        ///
        /// <para>Replaces the deleted second-dialog flow: the scoped Discard
        /// now sweeps the full subtree on its own and the secondary
        /// whole-pending-tree dialog is no longer needed.</para>
        /// </summary>
        internal static HashSet<string> CollectSwitchSegmentSubtreeRecordingIds(
            RecordingTree tree, SwitchSegmentSession session)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (tree?.Recordings == null || session == null)
                return ids;

            if (string.IsNullOrEmpty(session.ActiveSegmentRecordingId)
                || !tree.Recordings.ContainsKey(session.ActiveSegmentRecordingId))
            {
                return ids;
            }

            string sessionIdStr = session.SessionId.ToString("D", CultureInfo.InvariantCulture);

            // BFS over branch-point children rooted at the segment recording.
            var queue = new Queue<string>();
            queue.Enqueue(session.ActiveSegmentRecordingId);
            int safety = 0;
            while (queue.Count > 0)
            {
                if (++safety > SwitchSegmentRecordingTreeWalkMaxIterations)
                {
                    // Cycle or pathological fanout — log Warn so the partial
                    // collection that the caller is about to act on is at
                    // least traceable in KSP.log. Phase F review fix (1c).
                    ParsekLog.Warn("SwitchSegment",
                        $"CollectSubtree: iteration cap reached, breaking walk: " +
                        $"sessionId={sessionIdStr} " +
                        $"treeId={tree.Id ?? "<null>"} cap={SwitchSegmentRecordingTreeWalkMaxIterations} " +
                        $"collectedSoFar={ids.Count}");
                    break;
                }

                string id = queue.Dequeue();
                if (string.IsNullOrEmpty(id) || !ids.Add(id))
                    continue;
                if (!tree.Recordings.TryGetValue(id, out Recording rec) || rec == null)
                    continue;
                if (string.IsNullOrEmpty(rec.ChildBranchPointId))
                    continue;
                BranchPoint bp = FindSwitchSegmentBranchPointById(tree, rec.ChildBranchPointId);
                if (bp?.ChildRecordingIds == null)
                    continue;
                for (int i = 0; i < bp.ChildRecordingIds.Count; i++)
                {
                    string childId = bp.ChildRecordingIds[i];
                    if (string.IsNullOrEmpty(childId)) continue;
                    if (ids.Contains(childId)) continue;
                    queue.Enqueue(childId);
                }
            }

            return ids;
        }

        private static BranchPoint FindSwitchSegmentBranchPointById(
            RecordingTree tree, string bpId)
        {
            if (tree?.BranchPoints == null || string.IsNullOrEmpty(bpId))
                return null;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                var bp = tree.BranchPoints[i];
                if (bp != null && string.Equals(bp.Id, bpId, StringComparison.Ordinal))
                    return bp;
            }
            return null;
        }

        /// <summary>
        /// Collects branch-point IDs authored by the active session: every
        /// current BP not in <see cref="SwitchSegmentSession.PreSessionBranchPointIds"/>.
        /// Mirrors <c>MergeDialog.PruneSessionCreatedBranchPoints</c>: a null
        /// baseline is treated as "unknown" and skipped (returns empty), a
        /// present-but-empty baseline means every current BP is session-authored.
        /// </summary>
        private static HashSet<string> CollectSessionAuthoredBranchPointIds(
            RecordingTree tree, SwitchSegmentSession session)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (tree?.BranchPoints == null || session == null
                || session.PreSessionBranchPointIds == null)
                return ids;

            var preSessionIds = new HashSet<string>(
                session.PreSessionBranchPointIds, StringComparer.Ordinal);
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                var bp = tree.BranchPoints[i];
                if (bp == null || string.IsNullOrEmpty(bp.Id)) continue;
                if (preSessionIds.Contains(bp.Id)) continue;
                ids.Add(bp.Id);
            }
            return ids;
        }

        private static int RemoveRecordingIdsFromTree(
            RecordingTree tree, HashSet<string> ids)
        {
            if (tree?.Recordings == null || ids == null || ids.Count == 0)
                return 0;
            int removed = 0;
            foreach (string id in ids)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (tree.Recordings.Remove(id))
                    removed++;
            }
            // Scrub remaining branch-point parent/child id refs that point at
            // a removed recording.
            if (tree.BranchPoints != null)
            {
                for (int i = 0; i < tree.BranchPoints.Count; i++)
                {
                    var bp = tree.BranchPoints[i];
                    if (bp == null) continue;
                    ScrubRecordingIdsFromList(bp.ParentRecordingIds, ids);
                    ScrubRecordingIdsFromList(bp.ChildRecordingIds, ids);
                }
            }
            return removed;
        }

        private static void ScrubRecordingIdsFromList(
            List<string> list, HashSet<string> idsToRemove)
        {
            if (list == null || idsToRemove == null || idsToRemove.Count == 0)
                return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (idsToRemove.Contains(list[i]))
                    list.RemoveAt(i);
            }
        }

        private static int RemoveSessionAuthoredBranchPointsFromTree(
            RecordingTree tree, HashSet<string> sessionAuthoredBpIds)
        {
            if (tree?.BranchPoints == null
                || sessionAuthoredBpIds == null || sessionAuthoredBpIds.Count == 0)
                return 0;
            int removed = 0;
            for (int i = tree.BranchPoints.Count - 1; i >= 0; i--)
            {
                var bp = tree.BranchPoints[i];
                if (bp == null || string.IsNullOrEmpty(bp.Id)) continue;
                if (!sessionAuthoredBpIds.Contains(bp.Id)) continue;
                tree.BranchPoints.RemoveAt(i);
                removed++;
            }
            return removed;
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
        /// Contrast with <see cref="DiscardPendingTree"/>, which runs the #431 purge +
        /// file deletion for pending-only ids on the merge-dialog Discard button's
        /// explicit "throw it away" choice.
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
            pendingTreeSerializedForSave = false;
            Log($"[Parsek] Unstashed pending tree '{treeName}' on revert " +
                $"(was state={prevState}, {recCount} recording(s), {subjectsCleared} pending science subject(s) cleared): " +
                "sidecar files preserved for F9-from-flight-quicksave; " +
                "events stay in-memory and on-disk, filtered by recording-id visibility");
        }

        internal static bool IsPendingRecordingId(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return false;
            if (pendingTree != null
                && pendingTree.Recordings != null
                && pendingTree.Recordings.ContainsKey(recordingId))
                return true;
            return savedPendingTreeDuringActiveRestore != null
                && savedPendingTreeDuringActiveRestore.Recordings != null
                && savedPendingTreeDuringActiveRestore.Recordings.ContainsKey(recordingId);
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
            pendingTreeSerializedForSave = false;
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
            pendingTreeSerializedForSave = false;
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
        /// Returns the index in <c>committedRecordings</c> of the immediate chain predecessor of
        /// <paramref name="rec"/> on branch 0 (the recording with the same ChainId, ChainBranch=0,
        /// and ChainIndex = rec.ChainIndex - 1), or -1 if no such predecessor exists, if rec is
        /// not on branch 0, if rec is a chain head (ChainIndex &lt;= 0), or if rec is not part of a
        /// chain at all. Used by the playback flag builder to detect chain-seam first-spawns
        /// (see <c>TrajectoryPlaybackFlags.isChainSeamSuccessor</c>).
        /// </summary>
        internal static int GetChainPredecessorIndex(Recording rec)
        {
            if (rec == null) return -1;
            if (string.IsNullOrEmpty(rec.ChainId)) return -1;
            if (rec.ChainBranch != 0) return -1;
            if (rec.ChainIndex <= 0) return -1;
            int expectedPredecessorIndex = rec.ChainIndex - 1;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var other = committedRecordings[i];
                if (other.ChainId == rec.ChainId
                    && other.ChainBranch == 0
                    && other.ChainIndex == expectedPredecessorIndex)
                {
                    return i;
                }
            }
            return -1;
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
        /// Removes every committed recording tagged with the given Re-Fly session,
        /// including optimizer-created split tails that inherited the session tag
        /// via <see cref="RunOptimizationSplitPass"/>. Removes in descending-index
        /// order so the chain-degrade pass in <see cref="RemoveRecordingAt"/>
        /// only sees siblings that are also about to be deleted; the chain id on
        /// the remaining ones is nulled out as a transient effect, then those
        /// entries are themselves removed in the next iteration.
        ///
        /// <para>Match policy: when <paramref name="sessionId"/> is provided
        /// (the production case), match on <see cref="Recording.CreatingSessionId"/>
        /// only. <see cref="Recording.ProvisionalForRpId"/> survives a successful
        /// merge as durable metadata on the committed Re-Fly recording, so
        /// matching on it after a different session's failed merge would
        /// incorrectly delete a prior durable attempt that shares the same
        /// rewind point. The RP fallback fires only when the caller has no
        /// session id at all (legacy / pre-tagging save sweep).</para>
        ///
        /// <para>After flat-list removal, each <see cref="committedTrees"/> entry
        /// gets the matched ids stripped from its <c>Recordings</c> dictionary,
        /// stale <c>ActiveRecordingId</c> / <c>RootRecordingId</c> are reset to
        /// <paramref name="fallbackActiveRecordingId"/> when present (or nulled),
        /// branch-point endpoint lists are scrubbed and empty BPs are dropped,
        /// and the tree's background map is rebuilt — otherwise
        /// <c>SaveTreeRecordings</c> would still serialise the orphan into
        /// <c>RECORDING_TREE</c>, defeating the rollback.</para>
        /// </summary>
        internal static int RemoveSessionProvisionalRecordings(
            string sessionId, string rewindPointId,
            string fallbackActiveRecordingId = null)
        {
            if (string.IsNullOrEmpty(sessionId) && string.IsNullOrEmpty(rewindPointId))
                return 0;

            bool useSessionMatch = !string.IsNullOrEmpty(sessionId);
            var matches = new List<int>();
            var matchedIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (rec == null) continue;
                bool match;
                if (useSessionMatch)
                {
                    match = string.Equals(
                        rec.CreatingSessionId, sessionId, StringComparison.Ordinal);
                }
                else
                {
                    match = !string.IsNullOrEmpty(rewindPointId)
                        && string.Equals(
                            rec.ProvisionalForRpId, rewindPointId, StringComparison.Ordinal);
                }
                if (match)
                {
                    matches.Add(i);
                    if (!string.IsNullOrEmpty(rec.RecordingId))
                        matchedIds.Add(rec.RecordingId);
                }
            }

            if (matches.Count == 0)
                return 0;

            for (int m = matches.Count - 1; m >= 0; m--)
            {
                int idx = matches[m];
                if (idx < 0 || idx >= committedRecordings.Count) continue;
                var rec = committedRecordings[idx];
                if (rec == null) continue;
                ParsekLog.Verbose("RecordingStore",
                    $"RemoveSessionProvisionalRecordings: removing rec='{rec.VesselName ?? "<no-name>"}' " +
                    $"id={rec.RecordingId ?? "<no-id>"} chainId={rec.ChainId ?? "<none>"} " +
                    $"matchedBy={(useSessionMatch ? "session" : "rp")}");
                CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                RemoveRecordingAt(idx);
            }

            int prunedFromTrees = PruneTaggedRecordingsFromCommittedTrees(
                matchedIds, fallbackActiveRecordingId);
            if (prunedFromTrees > 0)
            {
                ParsekLog.Info("RecordingStore",
                    $"RemoveSessionProvisionalRecordings: pruned {prunedFromTrees} stale " +
                    $"entry/entries from committed-tree dictionaries " +
                    $"(sess={sessionId ?? "<none>"} rp={rewindPointId ?? "<none>"})");
            }

            return matches.Count;
        }

        /// <summary>
        /// Strips the given recording ids from every committed tree's
        /// <c>Recordings</c> dictionary, scrubs the same ids out of every
        /// surviving branch point's <c>ParentRecordingIds</c> /
        /// <c>ChildRecordingIds</c> lists, drops branch points whose entire
        /// parent OR child endpoint set went empty (a BP with zero parents OR
        /// zero children can no longer connect anything), clears surviving
        /// recordings' <c>ParentBranchPointId</c> / <c>ChildBranchPointId</c>
        /// values that pointed at the dropped BPs, resets stale
        /// <c>ActiveRecordingId</c> AND <c>RootRecordingId</c> to
        /// <paramref name="fallbackActiveRecordingId"/> when present (or
        /// nulled), and rebuilds the affected tree's background map. Returns
        /// the total number of dictionary entries removed across all trees.
        /// Without this pass, <c>SaveTreeRecordings</c> serialises branch
        /// points (independently from Recordings) so a Recordings-only sweep
        /// still leaves dangling topology edges to deleted Re-Fly fragments
        /// on disk.
        /// </summary>
        private static int PruneTaggedRecordingsFromCommittedTrees(
            HashSet<string> ids, string fallbackActiveRecordingId)
        {
            if (ids == null || ids.Count == 0) return 0;
            int prunedTotal = 0;
            int scrubbedRefsTotal = 0;
            int clearedRecordingBpRefsTotal = 0;
            int droppedBpsTotal = 0;
            for (int t = 0; t < committedTrees.Count; t++)
            {
                var tree = committedTrees[t];
                if (tree?.Recordings == null) continue;

                int prunedHere = 0;
                foreach (var id in ids)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    if (tree.Recordings.Remove(id))
                        prunedHere++;
                }
                if (prunedHere == 0) continue;

                bool useFallback =
                    !string.IsNullOrEmpty(fallbackActiveRecordingId)
                    && tree.Recordings.ContainsKey(fallbackActiveRecordingId);
                if (!string.IsNullOrEmpty(tree.ActiveRecordingId)
                    && !tree.Recordings.ContainsKey(tree.ActiveRecordingId))
                {
                    string oldActive = tree.ActiveRecordingId;
                    tree.ActiveRecordingId = useFallback
                        ? fallbackActiveRecordingId
                        : null;
                    ParsekLog.Info("RecordingStore",
                        $"PruneTaggedRecordingsFromCommittedTrees: reset stale " +
                        $"tree.ActiveRecordingId from '{oldActive}' to " +
                        $"'{tree.ActiveRecordingId ?? "<null>"}' on tree={tree.Id ?? "<none>"}");
                }
                if (!string.IsNullOrEmpty(tree.RootRecordingId)
                    && !tree.Recordings.ContainsKey(tree.RootRecordingId))
                {
                    string oldRoot = tree.RootRecordingId;
                    tree.RootRecordingId = useFallback
                        ? fallbackActiveRecordingId
                        : null;
                    ParsekLog.Warn("RecordingStore",
                        $"PruneTaggedRecordingsFromCommittedTrees: reset stale " +
                        $"tree.RootRecordingId from '{oldRoot}' to " +
                        $"'{tree.RootRecordingId ?? "<null>"}' on tree={tree.Id ?? "<none>"} " +
                        "(rollback removed the prior root — verify the resulting tree is consistent)");
                }

                int scrubbedRefs = ScrubBranchPointEndpointsAndDropEmpty(tree, ids,
                    out HashSet<string> droppedBranchPointIds);
                int droppedBpsHere = droppedBranchPointIds?.Count ?? 0;
                int clearedRecordingBpRefs = 0;
                if (droppedBpsHere > 0)
                {
                    foreach (var rec in tree.Recordings.Values)
                    {
                        if (rec == null) continue;
                        if (!string.IsNullOrEmpty(rec.ParentBranchPointId)
                            && droppedBranchPointIds.Contains(rec.ParentBranchPointId))
                        {
                            rec.ParentBranchPointId = null;
                            clearedRecordingBpRefs++;
                        }
                        if (!string.IsNullOrEmpty(rec.ChildBranchPointId)
                            && droppedBranchPointIds.Contains(rec.ChildBranchPointId))
                        {
                            rec.ChildBranchPointId = null;
                            clearedRecordingBpRefs++;
                        }
                    }
                    ParsekLog.Verbose("RecordingStore",
                        $"PruneTaggedRecordingsFromCommittedTrees: dropped " +
                        $"{droppedBpsHere} branch point(s) with empty endpoint sets " +
                        $"on tree={tree.Id ?? "<none>"} " +
                        $"ids=[{string.Join(",", droppedBranchPointIds)}]");
                }

                tree.RebuildBackgroundMap();
                prunedTotal += prunedHere;
                scrubbedRefsTotal += scrubbedRefs;
                clearedRecordingBpRefsTotal += clearedRecordingBpRefs;
                droppedBpsTotal += droppedBpsHere;
                ParsekLog.Verbose("RecordingStore",
                    $"PruneTaggedRecordingsFromCommittedTrees: tree={tree.Id ?? "<none>"} " +
                    $"prunedRecordings={prunedHere} scrubbedBranchPointRefs={scrubbedRefs} " +
                    $"droppedBranchPoints={droppedBpsHere} " +
                    $"clearedRecordingBpRefs={clearedRecordingBpRefs}");
            }
            if (prunedTotal > 0 || scrubbedRefsTotal > 0
                || clearedRecordingBpRefsTotal > 0 || droppedBpsTotal > 0)
            {
                ParsekLog.Verbose("RecordingStore",
                    $"PruneTaggedRecordingsFromCommittedTrees totals: " +
                    $"prunedRecordings={prunedTotal} " +
                    $"scrubbedBranchPointRefs={scrubbedRefsTotal} " +
                    $"droppedBranchPoints={droppedBpsTotal} " +
                    $"clearedRecordingBpRefs={clearedRecordingBpRefsTotal}");
            }
            return prunedTotal;
        }

        /// <summary>
        /// Walks <paramref name="tree"/>'s <c>BranchPoints</c> and removes any
        /// occurrences of <paramref name="ids"/> from each branch point's
        /// <c>ParentRecordingIds</c> / <c>ChildRecordingIds</c> lists. A BP is
        /// dropped from the tree when EITHER its parent or its child endpoint
        /// set went empty, since a BP with zero endpoints on either side can
        /// no longer connect anything; the dropped ids are surfaced via
        /// <paramref name="droppedBranchPointIds"/> so the caller can clear
        /// surviving recordings' BP back-references. Returns the number of
        /// id occurrences scrubbed out of endpoint lists.
        /// </summary>
        private static int ScrubBranchPointEndpointsAndDropEmpty(
            RecordingTree tree,
            HashSet<string> ids,
            out HashSet<string> droppedBranchPointIds)
        {
            droppedBranchPointIds = null;
            if (tree?.BranchPoints == null || ids == null || ids.Count == 0)
                return 0;

            int scrubbed = 0;
            for (int i = tree.BranchPoints.Count - 1; i >= 0; i--)
            {
                var bp = tree.BranchPoints[i];
                if (bp == null) continue;
                if (bp.ParentRecordingIds != null)
                {
                    for (int p = bp.ParentRecordingIds.Count - 1; p >= 0; p--)
                    {
                        if (ids.Contains(bp.ParentRecordingIds[p]))
                        {
                            bp.ParentRecordingIds.RemoveAt(p);
                            scrubbed++;
                        }
                    }
                }
                if (bp.ChildRecordingIds != null)
                {
                    for (int c = bp.ChildRecordingIds.Count - 1; c >= 0; c--)
                    {
                        if (ids.Contains(bp.ChildRecordingIds[c]))
                        {
                            bp.ChildRecordingIds.RemoveAt(c);
                            scrubbed++;
                        }
                    }
                }

                bool emptyParents = bp.ParentRecordingIds == null
                    || bp.ParentRecordingIds.Count == 0;
                bool emptyChildren = bp.ChildRecordingIds == null
                    || bp.ChildRecordingIds.Count == 0;
                if (emptyParents || emptyChildren)
                {
                    if (droppedBranchPointIds == null)
                        droppedBranchPointIds = new HashSet<string>(StringComparer.Ordinal);
                    if (!string.IsNullOrEmpty(bp.Id))
                        droppedBranchPointIds.Add(bp.Id);
                    tree.BranchPoints.RemoveAt(i);
                }
            }
            return scrubbed;
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
            if (rec == null) return "";
            return GetSegmentPhaseLabel(rec, GetSegmentBodyDisplayLabel(rec));
        }

        internal static string GetSegmentPhaseLabel(Recording rec, string displayBody)
        {
            if (rec == null) return "";
            if (ShouldSuppressEvaBoundaryPhaseLabel(rec))
            {
                return displayBody ?? "";
            }

            if (string.IsNullOrEmpty(rec.SegmentPhase)) return "";
            if (!string.IsNullOrEmpty(displayBody))
                return displayBody + " " + rec.SegmentPhase;
            return rec.SegmentPhase;
        }

        internal static string GetSegmentBodyDisplayLabel(Recording rec)
        {
            if (rec == null) return "";

            int pointCount = rec.Points != null ? rec.Points.Count : 0;
            int trackSectionCount = rec.TrackSections != null ? rec.TrackSections.Count : 0;
            string lastPointBodyName = pointCount > 0 ? rec.Points[pointCount - 1].bodyName : null;
            if (rec.SegmentBodyDisplayLabelCacheValid
                && rec.SegmentBodyDisplayLabelCachePointCount == pointCount
                && rec.SegmentBodyDisplayLabelCacheTrackSectionCount == trackSectionCount
                && rec.SegmentBodyDisplayLabelCacheSegmentBodyName == rec.SegmentBodyName
                && rec.SegmentBodyDisplayLabelCacheStartBodyName == rec.StartBodyName
                && rec.SegmentBodyDisplayLabelCacheLastPointBodyName == lastPointBodyName)
            {
                return rec.SegmentBodyDisplayLabelCache ?? "";
            }

            string bodyPath;
            string result;
            if (TryBuildBodyPathLabel(rec.Points, out bodyPath))
            {
                result = bodyPath;
            }
            else if (TryBuildBodyPathLabel(rec.TrackSections, out bodyPath))
            {
                result = bodyPath;
            }
            else
            {
                string body = rec.SegmentBodyName;
                if (string.IsNullOrEmpty(body))
                    body = lastPointBodyName;
                if (string.IsNullOrEmpty(body))
                    body = rec.StartBodyName;
                result = body ?? "";
            }

            rec.SegmentBodyDisplayLabelCacheValid = true;
            rec.SegmentBodyDisplayLabelCache = result;
            rec.SegmentBodyDisplayLabelCachePointCount = pointCount;
            rec.SegmentBodyDisplayLabelCacheTrackSectionCount = trackSectionCount;
            rec.SegmentBodyDisplayLabelCacheSegmentBodyName = rec.SegmentBodyName;
            rec.SegmentBodyDisplayLabelCacheStartBodyName = rec.StartBodyName;
            rec.SegmentBodyDisplayLabelCacheLastPointBodyName = lastPointBodyName;
            return result;
        }

        private static bool TryBuildBodyPathLabel(List<TrajectoryPoint> points, out string label)
        {
            label = null;
            if (points == null || points.Count == 0)
                return false;

            var bodies = new List<string>();
            for (int i = 0; i < points.Count; i++)
                AppendBodyTransition(bodies, points[i].bodyName);

            return TryFormatBodyPathLabel(bodies, out label);
        }

        private static bool TryBuildBodyPathLabel(List<TrackSection> sections, out string label)
        {
            label = null;
            if (sections == null || sections.Count == 0)
                return false;

            var bodies = new List<string>();
            for (int i = 0; i < sections.Count; i++)
            {
                TrackSection section = sections[i];
                if (section.frames != null && section.frames.Count > 0)
                {
                    AppendBodyTransitions(bodies, section.frames);
                }
                else if (section.bodyFixedFrames != null && section.bodyFixedFrames.Count > 0)
                {
                    AppendBodyTransitions(bodies, section.bodyFixedFrames);
                }
                else if (section.checkpoints != null)
                {
                    for (int j = 0; j < section.checkpoints.Count; j++)
                        AppendBodyTransition(bodies, section.checkpoints[j].bodyName);
                }
            }

            return TryFormatBodyPathLabel(bodies, out label);
        }

        private static void AppendBodyTransitions(List<string> bodies, List<TrajectoryPoint> points)
        {
            if (points == null)
                return;
            for (int i = 0; i < points.Count; i++)
                AppendBodyTransition(bodies, points[i].bodyName);
        }

        private static void AppendBodyTransition(List<string> bodies, string bodyName)
        {
            if (bodies == null || string.IsNullOrEmpty(bodyName))
                return;
            if (bodies.Count == 0 || bodies[bodies.Count - 1] != bodyName)
                bodies.Add(bodyName);
        }

        private static bool TryFormatBodyPathLabel(List<string> bodies, out string label)
        {
            label = null;
            if (bodies == null || bodies.Count < 2)
                return false;
            label = string.Join(" -> ", bodies.ToArray());
            return true;
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
            //
            // Re-Fly defer: while a Re-Fly session marker is live, the active
            // provisional recording is the supersede target the merge orchestrator
            // is about to write rows for. Splitting it here would null out
            // TerminalStateValue on the head (RecordingOptimizer.cs:897) and trip
            // SupersedeCommit.ValidateSupersedeTarget's "null TerminalState"
            // invariant. Skip just that recording id this pass — other recordings
            // in the same tree still split normally — and let the next
            // optimization pass after the marker clears do the split.
            int splitCount = 0;
            const int maxSplitsPerPass = 50;
            string deferredActiveReFlyId =
                ParsekScenario.Instance?.ActiveReFlySessionMarker?.ActiveReFlyRecordingId;
            int deferredCandidatesObservedTotal = 0;
            bool splitChanged = true;
            while (splitChanged && splitCount < maxSplitsPerPass)
            {
                splitChanged = false;
                var splitCandidates = RecordingOptimizer.FindSplitCandidatesForOptimizer(recordings);
                if (splitCandidates.Count == 0) break;

                int chosen = ChooseSplitCandidateIndex(
                    splitCandidates, recordings, deferredActiveReFlyId,
                    out int deferredCandidatesThisIter);

                deferredCandidatesObservedTotal += deferredCandidatesThisIter;
                if (chosen < 0)
                    break;

                var (recIdx, secIdx) = splitCandidates[chosen];
                var original = recordings[recIdx];

                var second = RecordingOptimizer.SplitAtSection(original, secIdx);

                CopySplitIdentityFields(original, second);

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
                    RetargetMovedBranchPointParent(
                        original.TreeId, movedChildBranchPointId,
                        original.RecordingId, second.RecordingId);
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

            if (deferredCandidatesObservedTotal > 0
                && !string.IsNullOrEmpty(deferredActiveReFlyId))
            {
                ParsekLog.Info("RecordingStore",
                    $"Optimization pass: deferred split for active Re-Fly recording " +
                    $"id={deferredActiveReFlyId} candidatesObserved={deferredCandidatesObservedTotal}");
            }

            return splitCount;
        }

        /// <summary>
        /// Picks the split candidate to apply this iteration. With no live Re-Fly defer id,
        /// the first candidate (index 0) is chosen. Otherwise walks the candidate list,
        /// skipping any whose recording id equals the deferred Re-Fly id (counting them in
        /// <paramref name="deferredObserved"/>), and returns the first non-deferred candidate's
        /// index. Returns -1 when every remaining candidate is deferred. Pure read over the
        /// inputs.
        /// </summary>
        internal static int ChooseSplitCandidateIndex(
            IReadOnlyList<(int, int)> splitCandidates,
            IReadOnlyList<Recording> recordings,
            string deferredActiveReFlyId,
            out int deferredObserved)
        {
            deferredObserved = 0;
            if (string.IsNullOrEmpty(deferredActiveReFlyId))
            {
                return 0;
            }

            for (int c = 0; c < splitCandidates.Count; c++)
            {
                int candIdx = splitCandidates[c].Item1;
                if (candIdx < 0 || candIdx >= recordings.Count)
                    continue;
                var candRec = recordings[candIdx];
                if (candRec != null
                    && string.Equals(
                        candRec.RecordingId,
                        deferredActiveReFlyId,
                        StringComparison.Ordinal))
                {
                    deferredObserved++;
                    continue;
                }
                return c;
            }

            return -1;
        }

        /// <summary>
        /// Copies the identity / lineage fields from the original recording onto the
        /// second half produced by an optimizer split (assigns a fresh RecordingId,
        /// deep-copies RecordingGroups, and carries over chain / tree / vessel / pre-launch
        /// / session / supersede / switch-segment fields). Straight-line field copy.
        /// </summary>
        private static void CopySplitIdentityFields(Recording original, Recording second)
        {
            // Assign identity
            second.RecordingId = Guid.NewGuid().ToString("N");
            if (string.IsNullOrEmpty(original.ChainId))
                original.ChainId = Guid.NewGuid().ToString("N");
            second.ChainId = original.ChainId;
            second.TreeId = original.TreeId;
            second.VesselName = original.VesselName;
            second.VesselPersistentId = original.VesselPersistentId;
            second.RecordedVesselGuid = original.RecordedVesselGuid; // same launch as the split source
            second.PreLaunchFunds = original.PreLaunchFunds;
            second.PreLaunchScience = original.PreLaunchScience;
            second.PreLaunchReputation = original.PreLaunchReputation;
            second.RecordingGroups = original.RecordingGroups != null
                ? new List<string>(original.RecordingGroups) : null;
            second.CreatingSessionId = original.CreatingSessionId;
            second.ProvisionalForRpId = original.ProvisionalForRpId;
            // NOTE: same pattern as RecordingTreeSplitter Pass 6 M3 fix.
            // Safe here because the optimizer auto-split only runs on
            // already-committed recordings where original.SupersedeTargetId
            // is null (the field is transient on NotCommitted provisionals
            // only). If a future change ever calls the optimizer on a
            // NotCommitted provisional, this inheritance would silently
            // carry a phantom id onto `second` until LoadTimeSweep scrubs
            // it on next load — null it explicitly in that case (mirror
            // RecordingTreeSplitter.cs's `tip.SupersedeTargetId = null;`).
            second.SupersedeTargetId = original.SupersedeTargetId;
            second.SwitchSegmentSessionId = original.SwitchSegmentSessionId;
        }

        /// <summary>
        /// Retargets the moved child BranchPoint's ParentRecordingIds entry from the original
        /// recording id to the second-half recording id after an optimizer split moved the
        /// branch point to the second half. Mutates the matching committed tree's branch point.
        /// Caller gates entry on a non-empty moved branch-point id and tree id.
        /// </summary>
        private static void RetargetMovedBranchPointParent(
            string treeId,
            string movedChildBranchPointId,
            string oldRecordingId,
            string newRecordingId)
        {
            for (int t = 0; t < committedTrees.Count; t++)
            {
                if (committedTrees[t].Id != treeId) continue;
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
                                if (parentIds[p] == oldRecordingId)
                                {
                                    parentIds[p] = newRecordingId;
                                    ParsekLog.Verbose("RecordingStore",
                                        $"Split: updated BranchPoint '{movedChildBranchPointId}' " +
                                        $"ParentRecordingIds: {oldRecordingId} → {newRecordingId}");
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
            //
            // Logging: per-recording skip-reason verbose lines are suppressed and
            // aggregated into a single summary at the end of the pass. A save with
            // hundreds of recordings would otherwise emit hundreds of identical
            // "skipped (too-short)" lines per scenario load.
            int trimCount = 0;
            Dictionary<string, int> skipCounts = null;
            for (int i = 0; i < recordings.Count; i++)
            {
                bool trimmed = RecordingOptimizer.TrimBoringTailInternal(
                    recordings[i],
                    recordings,
                    RecordingOptimizer.DefaultTailBufferSeconds,
                    logSkipReason: false,
                    skipCategory: out string skipCategory);
                if (trimmed)
                {
                    recordings[i].FilesDirty = true;
                    trimCount++;
                }
                else if (!string.IsNullOrEmpty(skipCategory))
                {
                    if (skipCounts == null)
                        skipCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                    skipCounts[skipCategory] = skipCounts.TryGetValue(skipCategory, out int prev) ? prev + 1 : 1;
                }
            }
            if (trimCount > 0)
                ParsekLog.Info("RecordingStore",
                    $"Optimization pass: trimmed boring tails from {trimCount} recording(s)");
            if (skipCounts != null && skipCounts.Count > 0)
            {
                int totalSkipped = 0;
                foreach (var n in skipCounts.Values) totalSkipped += n;
                var ordered = new List<KeyValuePair<string, int>>(skipCounts);
                // Descending count, then ordinal name as tie-break, so equal-count
                // categories don't reorder run-to-run (List<T>.Sort is not stable).
                ordered.Sort((a, b) =>
                {
                    int c = b.Value.CompareTo(a.Value);
                    return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
                });
                var parts = new List<string>(ordered.Count);
                foreach (var kv in ordered) parts.Add($"{kv.Key}={kv.Value}");
                ParsekLog.Verbose("RecordingStore",
                    $"Optimization pass: TrimBoringTail skipped {totalSkipped} recording(s) — " +
                    string.Join(", ", parts));
            }
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
        /// Refreshes persistent.sfs and quicksave.sfs after an explicit pending-tree
        /// discard when the discarded tree had already been serialized as
        /// <c>isPending=True</c>. This removes stale pending metadata from save files
        /// without committing the discarded tree.
        /// </summary>
        internal static void RefreshSaveAndQuicksaveAfterDiscard(
            string reason,
            int discardedRecordingCount)
        {
            var saveFn = SaveGameForTesting ?? GamePersistence.SaveGame;

            if (SaveGameForTesting == null)
            {
                if (HighLogic.LoadedScene == GameScenes.LOADING)
                {
                    ParsekLog.Verbose("Quicksave",
                        $"Discard save refresh skipped (LOADING scene): reason={reason}");
                    return;
                }
                if (HighLogic.CurrentGame == null)
                {
                    ParsekLog.Verbose("Quicksave",
                        $"Discard save refresh skipped (CurrentGame is null): reason={reason}");
                    return;
                }
            }

            RefreshSaveAfterDiscard(saveFn, "persistent", "persistent.sfs", reason, discardedRecordingCount);
            RefreshSaveAfterDiscard(saveFn, "quicksave", "quicksave.sfs", reason, discardedRecordingCount);
        }

        private static void RefreshSaveAfterDiscard(
            System.Func<string, string, SaveMode, string> saveFn,
            string saveName,
            string displayName,
            string reason,
            int discardedRecordingCount)
        {
            try
            {
                string result = saveFn(saveName, HighLogic.SaveFolder, SaveMode.OVERWRITE);
                if (string.IsNullOrEmpty(result))
                {
                    ParsekLog.Warn("Quicksave",
                        $"GamePersistence.SaveGame returned null refreshing {displayName} " +
                        $"after {reason} — {displayName} NOT refreshed");
                    return;
                }
                ParsekLog.Info("Quicksave",
                    $"Refreshed {displayName} after {reason} " +
                    $"(discarded tree had {discardedRecordingCount} recording IDs)");
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn("Quicksave",
                    $"Exception refreshing {displayName} after {reason}: {ex.GetType().Name}: {ex.Message}");
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
                // KEEP debris-only: this is a fast-skip for non-debris (they don't
                // need a loop-sync parent index). Controlled-decoupled children
                // (extension of the parent-anchor contract) carry IsDebris=false
                // and correctly fall into this skip; they are not loop-synced to
                // a non-debris parent.
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
            return RecordingGroupStore.IsInvalidGroupName(name);
        }

        /// <summary>
        /// Returns distinct group names across all committed recordings.
        /// </summary>
        public static List<string> GetGroupNames()
        {
            return RecordingGroupStore.GetGroupNames(committedRecordings);
        }

        /// <summary>
        /// Returns a group name that doesn't collide with existing group names.
        /// If baseName is not already used, returns it unchanged.
        /// Otherwise appends " (2)", " (3)", etc. until a unique name is found.
        /// </summary>
        internal static string GenerateUniqueGroupName(string baseName)
        {
            return RecordingGroupStore.GenerateUniqueGroupName(baseName, committedRecordings);
        }

        /// <summary>
        /// Adds a recording to a group. No-op if already a member or index invalid.
        /// </summary>
        public static void AddRecordingToGroup(int index, string groupName)
        {
            RecordingGroupStore.AddRecordingToGroup(index, groupName, committedRecordings);
        }

        /// <summary>
        /// Removes a recording from a group. No-op if not a member or index invalid.
        /// </summary>
        public static void RemoveRecordingFromGroup(int index, string groupName)
        {
            RecordingGroupStore.RemoveRecordingFromGroup(index, groupName, committedRecordings);
        }

        /// <summary>
        /// Returns indices of all recordings in a given chain. Single scan, reusable for batch ops.
        /// </summary>
        public static List<int> GetChainMemberIndices(string chainId)
        {
            return RecordingGroupStore.GetChainMemberIndices(chainId, committedRecordings);
        }

        /// <summary>
        /// Adds all chain members to a group.
        /// </summary>
        public static void AddChainToGroup(string chainId, string groupName)
        {
            RecordingGroupStore.AddChainToGroup(chainId, groupName, committedRecordings);
        }

        /// <summary>
        /// Removes all chain members from a group.
        /// </summary>
        public static void RemoveChainFromGroup(string chainId, string groupName)
        {
            RecordingGroupStore.RemoveChainFromGroup(chainId, groupName, committedRecordings);
        }

        /// <summary>
        /// Renames a group across all committed recordings. Returns false if newName already exists.
        /// </summary>
        public static bool RenameGroup(string oldName, string newName)
        {
            return RecordingGroupStore.RenameGroup(oldName, newName, committedRecordings, committedTrees);
        }

        /// <summary>
        /// Removes a group from all committed recordings' group lists.
        /// Returns the number of recordings that were modified.
        /// </summary>
        public static int RemoveGroupFromAll(string groupName)
        {
            return RecordingGroupStore.RemoveGroupFromAll(groupName, committedRecordings);
        }

        /// <summary>
        /// Replaces a group tag with a parent group tag on all committed recordings.
        /// If parentGroup is null, the group tag is simply removed.
        /// </summary>
        public static int ReplaceGroupOnAll(string groupName, string parentGroup)
        {
            return RecordingGroupStore.ReplaceGroupOnAll(groupName, parentGroup, committedRecordings);
        }

        // Test seam: lets unit tests simulate the live-Unity-runtime context that
        // <see cref="ResetForTesting"/>'s data-loss guard checks. Production code never
        // sets this; xUnit tests in <c>RecordingStoreResetGuardTests</c> use it to verify
        // the guard hard-fails when invoked alongside committed data inside an in-game
        // test runner. <see cref="Application.isPlaying"/> is false outside Unity play
        // mode (xUnit, command-line dotnet test) so the guard is otherwise harmless to
        // existing xUnit callers that happen to add data before resetting.
        internal static Func<bool> ApplicationIsPlayingForTesting;

        // <see cref="UnityEngine.Application.isPlaying"/> is an extern (ECall) — fine
        // at runtime inside KSP, but throws SecurityException when xUnit runs the
        // assembly outside the Unity player. The JIT verifies the call before the
        // surrounding try/catch frame is established when the access lives in the
        // same method as the catch, so the exception escapes. Isolating the read in
        // its own non-inlined method delays JIT verification until first call, where
        // the catch around the call site can swallow the exception. Treating "throws"
        // as "not in play mode" preserves existing xUnit <c>ResetForTesting()</c>
        // callers (RecordingStoreTests, etc.) without forcing every one of them to
        // set <see cref="ApplicationIsPlayingForTesting"/>.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool ReadUnityApplicationIsPlayingCore()
        {
            return Application.isPlaying;
        }

        private static bool TryReadUnityApplicationIsPlaying()
        {
            try { return ReadUnityApplicationIsPlayingCore(); }
            catch (System.Security.SecurityException) { return false; }
            catch (System.TypeInitializationException) { return false; }
        }

        /// <summary>
        /// Resets state without Unity logging. For unit tests only.
        /// <para>
        /// Hard-fails when invoked while Unity is in play mode AND the store still
        /// holds real committed data — the in-game test runner (Ctrl+Shift+T) shares
        /// the static <see cref="committedRecordings"/> / <see cref="committedTrees"/>
        /// list with the player's live save, so a stray <c>ResetForTesting()</c>
        /// inside an in-game test would silently delete everything the player had
        /// just recorded (observed in production 2026-05-01: 5 committed recordings
        /// wiped by <c>PersistenceSplitOptimizerTest</c>). xUnit tests run with
        /// <see cref="Application.isPlaying"/> = <c>false</c> and remain unaffected.
        /// In-game tests must wrap mutations in
        /// <see cref="RecordingStoreTestSnapshot.Capture"/> / <see cref="RecordingStoreTestSnapshot.Restore"/>
        /// instead of calling this method.
        /// </para>
        /// </summary>
        internal static void ResetForTesting()
            => ResetForTestingInternal(allowWipingLiveSaveData: false);

        /// <summary>
        /// Clears stale <c>LoopPlayback=true</c> on every <c>IsDebris=true</c>
        /// recording in <see cref="committedRecordings"/>. Parent-anchored
        /// debris rides its parent's loop clock (see
        /// <c>GhostPlaybackEngine.TryUpdateLoopSyncedDebris</c>) so its own
        /// <c>LoopPlayback</c> flag has no effect at the engine boundary.
        /// Pre-PR #966 saves may carry a stale <c>true</c> here from before
        /// the per-row toggle was hidden; clearing at load time keeps the
        /// Timeline-tab <c>L</c> button consistent with the Recordings-tab
        /// hide. Returns the count cleared (zero on subsequent loads once
        /// the sweep has run, so this is idempotent).
        /// </summary>
        internal static int SanitizeDebrisLoopPlayback()
        {
            int cleared = 0;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (rec == null) continue;
                if (rec.IsDebris && rec.LoopPlayback)
                {
                    rec.LoopPlayback = false;
                    cleared++;
                }
            }
            if (cleared > 0)
            {
                ParsekLog.Warn("RecordingStore",
                    $"SanitizeDebrisLoopPlayback: cleared LoopPlayback on {cleared} debris recording(s) " +
                    "(stale flag from pre-PR #966 save; debris rides parent loop clock)");
            }
            return cleared;
        }

        /// <summary>
        /// Wipe-and-reset variant for the in-game test runner's batch FLIGHT
        /// baseline restore flow. The next operation after this call is a
        /// <c>QuickloadResumeHelpers.TriggerQuickload</c> from the baseline
        /// save slot, which restores the player's clean pre-batch state from
        /// disk via <c>OnLoad</c>. The in-memory wipe is therefore a
        /// transient about-to-be-overwritten step, not a destructive
        /// player-save mutation; the live-save guard exists to prevent the
        /// PersistenceSplitOptimizerTest 2026-05-01 class of bug where a
        /// test wiped state without restoring it. Documented at
        /// <see cref="ParsekScenario.PrepareForIsolatedBatchFlightBaselineRestore"/>
        /// — that's the only caller. Bypassing the guard from anywhere else
        /// re-opens the production bug.
        /// </summary>
        internal static void ResetForBatchFlightBaselineRestoreBypassingGuard()
            => ResetForTestingInternal(allowWipingLiveSaveData: true);

        private static void ResetForTestingInternal(bool allowWipingLiveSaveData)
        {
            if (!allowWipingLiveSaveData)
            {
                bool isPlaying;
                if (ApplicationIsPlayingForTesting != null)
                {
                    isPlaying = ApplicationIsPlayingForTesting();
                }
                else
                {
                    isPlaying = TryReadUnityApplicationIsPlaying();
                }
                if (isPlaying)
                {
                    int recCount = committedRecordings.Count;
                    int treeCount = committedTrees.Count;
                    bool hasPending = pendingTree != null;
                    bool hasSavedPendingDuringActiveRestore =
                        savedPendingTreeDuringActiveRestore != null;
                    if (recCount > 0
                        || treeCount > 0
                        || hasPending
                        || hasSavedPendingDuringActiveRestore)
                    {
                        string msg =
                            $"ResetForTesting blocked: refusing to wipe live save data " +
                            $"(committedRecordings={recCount}, committedTrees={treeCount}, " +
                            $"hasPendingTree={hasPending}, " +
                            $"hasSavedPendingDuringActiveRestore={hasSavedPendingDuringActiveRestore}). " +
                            $"The in-game test runner shares " +
                            $"static state with the player's save — call " +
                            $"RecordingStoreTestSnapshot.Capture()/Restore() around the " +
                            $"test body instead of ResetForTesting(). Production bug source: " +
                            $"PersistenceSplitOptimizerTest 2026-05-01.";
                        ParsekLog.Error("RecordingStore", msg);
                        throw new InvalidOperationException(msg);
                    }
                }
            }

            committedRecordings.Clear();
            committedTrees.Clear();
            BumpStateVersion();
            RecordingGroupStore.ResetForTesting();
            pendingTree = null;
            pendingTreeState = PendingTreeState.Finalized;
            pendingTreeSerializedForSave = false;
            savedPendingTreeDuringActiveRestore = null;
            savedPendingTreeDuringActiveRestoreSerializedForSave = false;
            committedTreeRestoreAttemptTreeId = null;
            committedTreeRestoreAttemptReason = null;
            committedTreeRestoreAttemptRecordingIds = null;
            committedTreeRestoreAttemptEventCutoffs = null;
            CleanOrphanFilesDirectoryOverrideForTesting = null;
            SkipSidecarCurrencyCheckForTesting = false;
            WriteReadableSidecarMirrorsOverrideForTesting = null;
            CurrentUniversalTimeForRewindRetirementOverrideForTesting = null;
            SceneEntryActiveVesselPid = 0;
            SceneEntryFreshRolloutVesselPid = 0;
            ClearRewindReplayTargetScope();
            RewindContext.ResetForTesting();
            RewindUTAdjustmentPending = false;
            GameStateRecorder.PendingScienceSubjects.Clear();
            PendingCleanupPids = null;
            PendingCleanupNames = null;
            PendingRevertPreExistingPids = null;
            PendingStashedThisTransition = false;
            suppressNextTreeSceneExitCommit = false;
            suppressNextTreeSceneExitCommitReason = null;
            suppressNextActiveTreeRestore = false;
            suppressNextActiveTreeRestoreReason = null;
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
            pendingTreeSerializedForSave = false;
        }

        /// <summary>
        /// In-place restore of the four core fields a <see cref="RecordingStoreTestSnapshot"/>
        /// captures: committed recordings, committed trees, pending tree slot + state,
        /// saved pending tree preserved during active restore, and the
        /// auto-assigned-standalone-group dict. Bypasses the
        /// <see cref="ResetForTesting"/> guard because the call site knows it's about to
        /// re-install the very data the guard exists to protect. Used only by
        /// <see cref="RecordingStoreTestSnapshot.Restore"/>.
        /// </summary>
        internal static void RestoreFromSnapshotForTesting(
            List<Recording> snapshotRecordings,
            List<RecordingTree> snapshotTrees,
            RecordingTree snapshotPendingTree,
            PendingTreeState snapshotPendingState,
            RecordingTree snapshotSavedPendingTreeDuringActiveRestore,
            bool snapshotSavedPendingTreeDuringActiveRestoreSerializedForSave,
            Dictionary<string, string> snapshotAutoAssignedGroups)
        {
            committedRecordings.Clear();
            if (snapshotRecordings != null)
                committedRecordings.AddRange(snapshotRecordings);
            committedTrees.Clear();
            if (snapshotTrees != null)
                committedTrees.AddRange(snapshotTrees);
            pendingTree = snapshotPendingTree;
            pendingTreeState = snapshotPendingState;
            pendingTreeSerializedForSave = false;
            savedPendingTreeDuringActiveRestore = snapshotSavedPendingTreeDuringActiveRestore;
            savedPendingTreeDuringActiveRestoreSerializedForSave =
                snapshotSavedPendingTreeDuringActiveRestoreSerializedForSave;
            RecordingGroupStore.RestoreAutoAssignedStandaloneGroupsForTesting(snapshotAutoAssignedGroups);
            BumpStateVersion();
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
            RecordingGroupStore.MarkAutoAssignedStandaloneGroup(rec, groupName);
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
        /// Builds the recording view used by load/save cleanup passes that
        /// reason over raw committed rows, while also protecting the pending
        /// tree during deferred-merge windows. LoadTimeSweep uses it for
        /// supersede endpoint existence; GroupHierarchyStore uses it for live
        /// group collection. Intentionally excludes the committed-tree
        /// dictionary: zombie cleanup removes rows from the flat committed
        /// list, and the same sweep must not see those rows again through the
        /// parallel tree store. The same exclusion keeps group pruning aligned
        /// to the raw committed list plus the deferred pending tree, without
        /// letting stale tree-only copies protect old hierarchy entries.
        /// </summary>
        internal static List<Recording> BuildKnownRecordingsForCleanup()
        {
            var recordings = new List<Recording>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < committedRecordings.Count; i++)
                AddKnownRecordingForCleanup(recordings, seenIds, committedRecordings[i]);

            if (pendingTree != null && pendingTree.Recordings != null)
            {
                foreach (var kvp in pendingTree.Recordings)
                    AddKnownRecordingForCleanup(recordings, seenIds, kvp.Value);
            }

            return recordings;
        }

        private static void AddKnownRecordingForCleanup(
            List<Recording> recordings,
            HashSet<string> seenIds,
            Recording rec)
        {
            if (rec == null)
                return;

            if (string.IsNullOrEmpty(rec.RecordingId))
            {
                recordings.Add(rec);
                return;
            }

            if (seenIds.Add(rec.RecordingId))
                recordings.Add(rec);
        }

        internal static HashSet<string> BuildKnownRecordingIdsForCleanup()
        {
            var knownIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < committedRecordings.Count; i++)
                AddKnownRecordingId(knownIds, committedRecordings[i]);

            AddKnownTreeRecordingIds(knownIds, pendingTree);
            AddKnownTreeRecordingIds(knownIds, savedPendingTreeDuringActiveRestore);
            return knownIds;
        }

        private static void AddKnownRecordingId(HashSet<string> knownIds, Recording rec)
        {
            if (!string.IsNullOrEmpty(rec?.RecordingId))
                knownIds.Add(rec.RecordingId);
        }

        private static int AddKnownTreeRecordingIds(HashSet<string> knownIds, RecordingTree tree)
        {
            int count = 0;
            if (tree == null || tree.Recordings == null)
                return 0;

            foreach (var kvp in tree.Recordings)
            {
                if (!string.IsNullOrEmpty(kvp.Value?.RecordingId))
                {
                    AddKnownRecordingId(knownIds, kvp.Value);
                    count++;
                }
            }

            return count;
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
            var knownIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < committedRecordings.Count; i++)
                AddKnownRecordingId(knownIds, committedRecordings[i]);

            for (int t = 0; t < committedTrees.Count; t++)
            {
                var treeRecordings = committedTrees[t]?.Recordings;
                if (treeRecordings == null) continue;
                foreach (var kvp in treeRecordings)
                    AddKnownRecordingId(knownIds, kvp.Value);
            }

            pendingTreeIdCount = 0;
            pendingTreeIdCount += AddKnownTreeRecordingIds(knownIds, pendingTree);
            pendingTreeIdCount += AddKnownTreeRecordingIds(
                knownIds, savedPendingTreeDuringActiveRestore);
            return knownIds;
        }

        /// <summary>
        /// Resolves the Parsek/Recordings/ directory path for the currently-active save
        /// (or the test override). Returns null if no save context is available or the
        /// directory does not exist. Does not create the directory.
        /// </summary>
        internal static string ResolveRecordingsDirectoryForCurrentSave()
        {
            if (!string.IsNullOrEmpty(CleanOrphanFilesDirectoryOverrideForTesting))
                return Path.GetFullPath(CleanOrphanFilesDirectoryOverrideForTesting);
            string root;
            string saveFolder;
            try
            {
                root = KSPUtil.ApplicationRootPath ?? "";
                saveFolder = HighLogic.SaveFolder ?? "";
            }
            catch
            {
                // Unity bindings unavailable (e.g. unit-test host) — no save context.
                return null;
            }
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveFolder))
                return null;
            return Path.GetFullPath(Path.Combine(root, "saves", saveFolder, "Parsek", "Recordings"));
        }

        /// <summary>
        /// Returns the set of distinct recording IDs whose sidecar files (.prec,
        /// _vessel.craft, _ghost.craft, .pann, plus the .txt readable variants) exist
        /// in the recordings directory. Excludes legacy / transient artifacts (.pcrf,
        /// .tmp / .stage / .bak suffixes) — only IDs corresponding to "live" sidecar
        /// files are returned. Used by both <see cref="CleanOrphanFiles"/>'s safety
        /// guard and <c>ParsekScenario.OnSave</c>'s stranded-sidecar warn.
        /// </summary>
        internal static HashSet<string> CollectSidecarIdsOnDisk()
        {
            var ids = new HashSet<string>();
            string dir = ResolveRecordingsDirectoryForCurrentSave();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return ids;
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { return ids; }
            AddSidecarIdsFromFiles(files, ids);
            return ids;
        }

        private static void AddSidecarIdsFromFiles(string[] files, HashSet<string> ids)
        {
            for (int i = 0; i < files.Length; i++)
            {
                string fileName = Path.GetFileName(files[i]);
                if (IsTransientSidecarArtifactFile(fileName) || IsLegacySidecarFile(fileName))
                    continue;
                string id = ExtractRecordingIdFromFileName(fileName);
                if (!string.IsNullOrEmpty(id))
                    ids.Add(id);
            }
        }

        /// <summary>
        /// Deletes orphaned sidecar files in the Parsek/Recordings/ directory that don't
        /// correspond to any known recording ID. Called after all recordings and trees are loaded.
        /// </summary>
        internal static void CleanOrphanFiles()
        {
            string recordingsDir = ResolveRecordingsDirectoryForCurrentSave();
            if (string.IsNullOrEmpty(recordingsDir))
            {
                ParsekLog.Verbose("RecordingStore", "CleanOrphanFiles: no save context — skipping");
                return;
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
            // If the save also has a finalized isPending=True tree, it is held in
            // savedPendingTreeDuringActiveRestore and counted here too so active
            // quickload resume cannot orphan the unrelated pending sidecars.
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

            // Safety guard: refuse to delete when scenario reports zero known recording IDs
            // but the disk has live sidecar-shaped recording IDs. This is the "load lost its
            // tree state" pattern — deleting now turns a recoverable accident (the .sfs tree
            // metadata may still live in quicksave.sfs or a .bak) into permanent bulk-data
            // loss. The OnSave-side warn pairs with this guard to flag the originating fault.
            if (knownIds.Count == 0)
            {
                var diskIds = new HashSet<string>();
                AddSidecarIdsFromFiles(files, diskIds);
                if (diskIds.Count > 0)
                {
                    ParsekLog.Warn("RecordingStore",
                        $"CleanOrphanFiles: REFUSING to delete — scenario reports 0 known recording IDs " +
                        $"but disk has {diskIds.Count} sidecar-shaped recording ID(s) ({files.Length} file(s) total). " +
                        $"This usually means the scenario load lost its tree state. Sidecars preserved so the " +
                        $"save can be restored from quicksave.sfs or a backup. Investigate before next save.");
                    return;
                }
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
                    // Data-loss fix: orphaned RECORDING sidecars (.prec / craft / .pann with a
                    // real recording id) are MOVED to a quarantine subfolder, never hard-deleted.
                    // A sidecar can be "orphaned" not only by genuine garbage but by a transient
                    // state bug that drops a still-referenced tree (e.g. a Limbo quickload-resume
                    // tree that fell out of persistent.sfs — see the SavePendingTreeIfAny fix).
                    // Deleting then was a one-way destruction of immutable recorded data; quarantine
                    // de-clutters the active set while keeping the bulk data fully recoverable.
                    // Legacy (.pcrf) and transient (.tmp/.stage/.bak) artifacts above are still
                    // hard-deleted — they are by definition junk, not recorded data.
                    if (QuarantineOrphanRecordingFile(files[i], recordingsDir, fileName, extractedId))
                        orphanCount++;
                }
            }

            if (orphanCount > 0 || legacyCount > 0 || transientCount > 0)
                ParsekLog.Info("RecordingStore",
                    $"Cleaned orphan files: quarantined {orphanCount} orphaned recording file(s)" +
                    (legacyCount > 0 ? $", deleted {legacyCount} legacy sidecar file(s)" : "") +
                    (transientCount > 0 ? $", deleted {transientCount} transient sidecar artifact(s)" : "") +
                    (skippedUnrecognized > 0 ? $", skipped {skippedUnrecognized} unrecognized file(s)" : ""));
            else
                ParsekLog.Verbose("RecordingStore",
                    $"CleanOrphanFiles: no orphans found" +
                    (skippedUnrecognized > 0 ? $", skipped {skippedUnrecognized} unrecognized file(s)" : ""));
        }

        /// <summary>
        /// Subfolder under Parsek/Recordings/ where orphaned recording sidecars are parked by
        /// <see cref="CleanOrphanFiles"/> instead of being deleted. Top-level-only directory
        /// scans (Directory.GetFiles) never descend into it, so quarantined files are not
        /// re-scanned or double-counted.
        /// </summary>
        internal const string OrphanQuarantineDirName = "_quarantine";

        /// <summary>
        /// Moves an orphaned recording sidecar into the <see cref="OrphanQuarantineDirName"/>
        /// subfolder of <paramref name="recordingsDir"/>. Non-destructive: the immutable bulk
        /// data is preserved and recoverable. If a same-named file already sits in quarantine
        /// (e.g. a prior sweep of the same id), the existing copy is kept and the new one is
        /// suffixed so nothing is overwritten. Returns true if the file was moved.
        /// </summary>
        private static bool QuarantineOrphanRecordingFile(
            string filePath, string recordingsDir, string fileName, string extractedId)
        {
            try
            {
                string quarantineDir = Path.Combine(recordingsDir, OrphanQuarantineDirName);
                Directory.CreateDirectory(quarantineDir);
                string dest = Path.Combine(quarantineDir, fileName);
                if (File.Exists(dest))
                {
                    // Preserve the earlier quarantined copy; park this one alongside it.
                    string suffixed = Path.Combine(
                        quarantineDir,
                        fileName + ".dup" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    dest = suffixed;
                }
                File.Move(filePath, dest);
                ParsekLog.Verbose("RecordingStore",
                    $"Quarantined orphan recording file: {fileName} (id={extractedId}) -> {OrphanQuarantineDirName}/");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("RecordingStore",
                    $"Failed to quarantine orphan recording file '{fileName}': {ex.Message} (left in place, NOT deleted)");
                return false;
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
            TerminalOrbitSpawnSafety.Clear(rec);
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

        // BUG-H: pids of vessels present in the revert-target launch/prelaunch quicksave
        // (FlightDriver.PreLaunchState / PostInitState). A vessel whose pid is in this set
        // pre-existed the reverted launch and must NEVER be stripped/recovered — it belongs
        // to an unrelated mission, not the reverted flight. Populated in the OnLoad revert
        // path, consumed and cleared in ParsekFlight.OnFlightReady (belt-and-suspenders
        // cleanup). Null when there is no active revert scope.
        internal static HashSet<uint> PendingRevertPreExistingPids { get; set; }

        /// <summary>
        /// Flat list of every committed recording (standalone + tree members), for the
        /// launch-identity-aware vessel-strip / cleanup predicate (BUG-H). Mirrors
        /// <see cref="CollectSpawnedVesselInfo"/>'s iteration. This is a raw read of the committed
        /// store because the consumer is a physical pid+Guid identity correlation (does this live
        /// vessel belong to a recording's launch?), not a supersede-aware recording-set walk — the
        /// same trust scope as <see cref="CollectSpawnedVesselInfo"/> / <see cref="CollectAllRecordingVesselNames"/>.
        /// </summary>
        internal static List<Recording> CollectAllCommittedRecordings()
        {
            var list = new List<Recording>(committedRecordings.Count);
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                if (committedRecordings[i] != null)
                    list.Add(committedRecordings[i]);
            }
            for (int i = 0; i < committedTrees.Count; i++)
            {
                foreach (var rec in committedTrees[i].Recordings.Values)
                {
                    if (rec != null)
                        list.Add(rec);
                }
            }
            return list;
        }

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

            var scenario = ParsekScenario.Instance;
            // Cascade overload: a parent-anchored debris child whose parent
            // was retired must also refuse fast-forward; without this, the
            // child stays interactive in the recordings table even though
            // its trajectory belongs to a retired re-fly fork.
            if (EffectiveState.IsRewindRetired(
                    rec,
                    CommittedRecordings,
                    object.ReferenceEquals(null, scenario) ? null : scenario.RecordingRewindRetirements))
            {
                reason = "Recording was rewound out of the active timeline";
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

            // Refuse rewind during an active re-fly merge journal. The supersede drop
            // below mutates the same RecordingSupersedes list the merge journal is in
            // the middle of writing; running both concurrently could leave the ledger
            // half-committed across the LoadGame boundary. Half-fix is worse than
            // no-fix — let MergeJournalOrchestrator.RunFinisher resolve the journal
            // on the next OnLoad first, then the user can rewind.
            var scenarioForJournalCheck = ParsekScenario.Instance;
            if (scenarioForJournalCheck?.ActiveMergeJournal != null
                && scenarioForJournalCheck.ActiveMergeJournal.Phase != MergeJournal.Phases.Complete)
            {
                if (!SuppressLogging)
                    ParsekLog.Error("Rewind",
                        $"Cannot rewind during an active re-fly merge — finish the merge first " +
                        $"(journalPhase={scenarioForJournalCheck.ActiveMergeJournal.Phase})");
                ParsekLog.ScreenMessage("Cannot rewind during an active re-fly merge", 3f);
                return;
            }

            BeginRewindForOwner(owner);

            // Shared load: pre-process (strip + lead-time windback) keyed by the owner, and
            // drop supersede relations rewound out of existence for the owner's tree.
            ExecuteRewindSaveLoad(
                owner.RewindSaveFileName,
                preProcessOwner: owner,
                dropSupersedeOwner: owner,
                messageLabel: "Rewind");
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
                    $"Rewind-to-Launch initiated to UT {owner.StartUT} " +
                    $"(save: {owner.RewindSaveFileName}). Plain launch rewind via parsek_rw_* quicksave; " +
                    $"this is NOT a Re-Fly (no RewindPoint / ReFlySessionMarker / MergeJournal)");
        }

        /// <summary>
        /// Shared load mechanics for both rewind entry points (<see cref="InitiateRewind"/>
        /// and <see cref="InitiateRewindToCareerStart"/>). Copies the Parsek/Saves snapshot to
        /// the root saves dir (KSP's LoadGame cannot read subdirectory paths), optionally
        /// pre-processes it, loads it via <see cref="GamePersistence.LoadGame"/>, deletes the
        /// temp copy, captures the adjusted UT, optionally drops supersede relations rewound
        /// out of existence, swaps in the loaded game, and loads the Space Center. On any
        /// failure resets the rewind flags and removes the temp copy. Returns true when the
        /// scene load was initiated.
        ///
        /// <para>Callers own the precondition gates (merge-journal refusal, supersede-count
        /// refusal) and the rewind-context setup (<see cref="BeginRewindForOwner"/> vs the
        /// UT-0 reset) BEFORE calling this; this helper is purely the mechanical save-copy /
        /// load / scene-swap wrapper that the two entry points used to duplicate.</para>
        /// </summary>
        /// <param name="saveFileName">Parsek/Saves snapshot base name (no .sfs extension).</param>
        /// <param name="preProcessOwner">When non-null, runs <see cref="PreProcessRewindSave"/>
        /// with that owner's strip set plus the rewind-to-launch lead-time windback. Null skips
        /// pre-processing (the pristine career-start snapshot, which must not be wound back
        /// below UT 0).</param>
        /// <param name="dropSupersedeOwner">When non-null, drops supersede relations rewound
        /// out of existence for that owner's tree once the adjusted UT is known. Null skips the
        /// drop (the career-start path refuses up front when any supersede relation exists).</param>
        /// <param name="messageLabel">Message-text prefix for the adjusted-UT / failure logs
        /// ("Rewind" vs "Warp-to-game-start"); the subsystem tag stays "Rewind" either way.</param>
        private static bool ExecuteRewindSaveLoad(
            string saveFileName,
            Recording preProcessOwner,
            Recording dropSupersedeOwner,
            string messageLabel)
        {
            string tempCopyName = null;
            try
            {
                string savesDir = Path.Combine(
                    KSPUtil.ApplicationRootPath ?? "",
                    "saves",
                    HighLogic.SaveFolder ?? "");

                string sourcePath = Path.Combine(savesDir,
                    RecordingPaths.BuildRewindSaveRelativePath(saveFileName));
                tempCopyName = saveFileName;
                string tempPath = Path.Combine(savesDir, tempCopyName + ".sfs");

                // Copy from Parsek/Saves/ to the root saves dir so LoadGame can find it.
                File.Copy(sourcePath, tempPath, true);

                if (preProcessOwner != null)
                {
                    // Pre-process the save file before KSP parses it:
                    // 1. Remove recorded vessel + any EVA child vessels (other vessels stay intact)
                    // 2. Wind back UT by the rewind-to-launch lead time so the player can
                    //    regain control on the pad before launch.
                    var stripNames = BuildRewindStripNames(preProcessOwner);
                    // Collect spawned vessel PIDs for PID-based stripping (belt-and-suspenders
                    // alongside name matching, catches renamed vessels or debris)
                    var (stripPids, _) = CollectSpawnedVesselInfo();
                    PreProcessRewindSave(tempPath, stripNames, stripPids, RewindToLaunchLeadTimeSeconds);
                }

                Game game = GamePersistence.LoadGame(tempCopyName, HighLogic.SaveFolder, true, false);

                // Delete the temp copy (file already parsed into Game object)
                TryDeleteFileQuietly(tempPath);

                if (game == null)
                {
                    ResetRewindFlags();
                    if (!SuppressLogging)
                        ParsekLog.Error("Rewind",
                            $"{messageLabel} failed: LoadGame returned null for save '{saveFileName}'. " +
                            $"Flags reset: IsRewinding={IsRewinding}, RewindUT={RewindUT}, RewindAdjustedUT={RewindAdjustedUT}");
                    return false;
                }

                // Capture the adjusted UT from the (possibly preprocessed) save.
                RewindContext.SetAdjustedUT(game.flightState.universalTime);

                if (!SuppressLogging)
                    ParsekLog.Info("Rewind",
                        $"{messageLabel}: adjustedUT={RewindAdjustedUT:F1}, " +
                        $"rewindUT={RewindUT:F1}, flags=[IsRewinding={IsRewinding}]");

                if (dropSupersedeOwner != null)
                {
                    // Drop supersede relations whose forks are entirely in the rewound-out
                    // future. Without this, a rewound-to-launch source recording stays
                    // suppressed by `reason=superseded-by-relation` after re-launch even
                    // though the user has rewound past the moment the forks were created.
                    // Walk the whole rewound owner's tree so branch recordings (e.g. an
                    // upper-stage Probe) are unsuppressed too, not just the owner. Runs
                    // AFTER SetAdjustedUT so the comparison `fork.StartUT >= rewindAdjustedUT`
                    // uses the post-load UT.
                    int droppedSupersedes = DropSupersedesRewoundOutOfExistence(dropSupersedeOwner, RewindAdjustedUT);
                    if (droppedSupersedes > 0 && !SuppressLogging)
                        ParsekLog.Info("Rewind",
                            $"Dropped {droppedSupersedes} supersede relation(s) rewound out of existence " +
                            $"(rewindUT={RewindAdjustedUT:F1} owner='{dropSupersedeOwner.VesselName}')");
                }

                HighLogic.CurrentGame = game;
                HighLogic.LoadScene(GameScenes.SPACECENTER);

                if (!SuppressLogging)
                    ParsekLog.Info("Rewind",
                        $"{messageLabel}: loading save '{saveFileName}' into SpaceCenter");
                return true;
            }
            catch (Exception ex)
            {
                ResetRewindFlags();

                DeleteTemporaryRewindSaveCopy(tempCopyName);

                if (!SuppressLogging)
                    ParsekLog.Error("Rewind",
                        $"{messageLabel} failed: {ex.Message}. " +
                        $"Flags reset: IsRewinding={IsRewinding}, RewindUT={RewindUT}, RewindAdjustedUT={RewindAdjustedUT}");
                return false;
            }
        }

        /// <summary>
        /// Resets the game to its career-start snapshot (UT 0): reloads the pristine
        /// snapshot captured at career creation, restoring initial resources/facilities/clock
        /// while KEEPING the in-memory recordings as future ghosts (the OnLoad rewind branch
        /// skips the .sfs recording reload). Used by "Warp to time" for targets at/before the
        /// first launch so Year 1 / Day 1 is a true reset rather than landing at the earliest
        /// launch. Mirrors <see cref="InitiateRewind"/> minus the strip / lead-time windback
        /// (the snapshot is pristine) and minus owner-specific budget / replay scope.
        ///
        /// <para>Refuses when re-fly supersede relations exist: a UT-0 reset would otherwise
        /// leave superseded originals hidden. The caller selects the earliest-launch rewind
        /// path instead in that case (its owner-keyed supersede drop is the tested route).</para>
        /// </summary>
        internal static bool InitiateRewindToCareerStart(string saveFileName)
        {
            if (string.IsNullOrEmpty(saveFileName))
            {
                if (!SuppressLogging)
                    ParsekLog.Error("Rewind", "InitiateRewindToCareerStart: null/empty save name");
                return false;
            }

            var scenario = ParsekScenario.Instance;
            if (scenario?.ActiveMergeJournal != null
                && scenario.ActiveMergeJournal.Phase != MergeJournal.Phases.Complete)
            {
                if (!SuppressLogging)
                    ParsekLog.Error("Rewind",
                        $"Cannot warp to game start during an active re-fly merge " +
                        $"(journalPhase={scenario.ActiveMergeJournal.Phase})");
                ParsekLog.ScreenMessage("Cannot warp to game start during an active re-fly merge", 3f);
                return false;
            }

            int supersedeCount = scenario?.RecordingSupersedes?.Count ?? 0;
            if (supersedeCount > 0)
            {
                if (!SuppressLogging)
                    ParsekLog.Warn("Rewind",
                        $"InitiateRewindToCareerStart refused: {supersedeCount} supersede relation(s) present " +
                        "(UT-0 reset would hide superseded originals); caller should use earliest-launch rewind");
                return false;
            }

            // UT-0 reset: no owner, no reserved budget, no baseline. ApplyRewindResourceAdjustment
            // recalcs the ledger at the adjusted UT (~0), which restores pristine career resources.
            RewindContext.BeginRewind(0.0, default(BudgetSummary), 0, 0, 0f);
            RewindContext.SetQuicksaveVesselPids(null);   // no PreProcessRewindSave -> no whitelist
            ClearRewindReplayTargetScope();               // no specific owner to replay-scope
            if (!SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Warp-to-game-start initiated to UT 0 (snapshot: {saveFileName}). Keeps in-memory " +
                    "recordings as future ghosts; NOT a Re-Fly.");

            // Pristine snapshot: no PreProcessRewindSave (no future vessels to strip, and it
            // must NOT be wound back below UT 0) and no supersede drop (refused above when any
            // supersede relation exists).
            return ExecuteRewindSaveLoad(
                saveFileName,
                preProcessOwner: null,
                dropSupersedeOwner: null,
                messageLabel: "Warp-to-game-start");
        }

        /// <summary>
        /// Looks up a committed recording by id. Returns null when the id is null/empty
        /// or no committed recording matches. O(N); used by post-LoadScene rewind
        /// hooks where the owner reference doesn't survive but its id does
        /// (<see cref="RewindReplayTargetRecordingId"/>).
        /// </summary>
        internal static Recording TryFindCommittedRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (rec == null) continue;
                if (string.Equals(rec.RecordingId, recordingId, StringComparison.Ordinal))
                    return rec;
            }
            return null;
        }

        internal static bool IsCommittedRecordingId(string recordingId)
        {
            return TryFindCommittedRecordingById(recordingId) != null;
        }

        /// <summary>
        /// Resolves a committed recording id to its display fields for the Logistics
        /// window (H5: names instead of 8-char GUID fragments). Returns false when the
        /// id is null/empty or not in the committed store; on success
        /// <paramref name="recordingName"/> is the recording's display name
        /// ("Untitled" when the vessel name is empty, matching the Recordings table),
        /// <paramref name="treeName"/> is the owning tree/mission name (null for a
        /// standalone recording with no tree), and <paramref name="treeOrder"/> is the
        /// 0-based persisted position within the tree (-1 when unassigned).
        /// <para>
        /// This is the literal-free by-id accessor the Logistics window calls so it
        /// never references <c>committedRecordings</c> directly and the ERS/ELS grep
        /// gate stays green: the raw-list reads stay behind
        /// <see cref="TryFindCommittedRecordingById"/> and
        /// <see cref="TryResolveTreeById"/> in this already-allowlisted file. Reading
        /// <see cref="RecordingTree.TreeName"/> for the mission name does not touch any
        /// Missions file (MissionGroupLink keeps TreeName synced with the main mission
        /// name).
        /// </para>
        /// </summary>
        internal static bool TryResolveRecordingDisplayInfo(
            string recordingId,
            out string recordingName,
            out string treeName,
            out int treeOrder)
        {
            recordingName = null;
            treeName = null;
            treeOrder = -1;

            Recording rec = TryFindCommittedRecordingById(recordingId);
            if (rec == null)
            {
                ParsekLog.Verbose("RecordingStore",
                    $"Logistics display resolve id={Shorten(recordingId)} resolved=false (not committed)");
                return false;
            }

            recordingName = string.IsNullOrEmpty(rec.VesselName) ? "Untitled" : rec.VesselName;
            treeOrder = rec.TreeOrder;

            if (!string.IsNullOrEmpty(rec.TreeId)
                && TryResolveTreeById(rec.TreeId, out RecordingTree tree, out _)
                && tree != null)
            {
                treeName = tree.TreeName;
            }

            ParsekLog.Verbose("RecordingStore",
                $"Logistics display resolve id={Shorten(recordingId)} resolved=true "
                + $"name='{recordingName}' tree='{treeName ?? "<none>"}' order={treeOrder.ToString(CultureInfo.InvariantCulture)}");
            return true;
        }

        private static string Shorten(string id)
        {
            if (string.IsNullOrEmpty(id)) return "<none>";
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }

        /// <summary>
        /// Re-applies the rewind-time supersede drop after <see cref="ParsekScenario.OnLoad"/>
        /// has reloaded <see cref="ParsekScenario.RecordingSupersedes"/> from the scenario
        /// node. Without this, the in-memory drop performed in <see cref="InitiateRewind"/>
        /// is reverted by KSP's scenario-state restoration across the LoadScene boundary,
        /// leaving the rewound source's branch ghosts (e.g. an upper-stage Probe at #7)
        /// suppressed via <c>reason=superseded-by-relation</c> for the rest of the
        /// session.
        ///
        /// <para>
        /// Mechanism: the rewound owner's id is preserved across LoadScene in the static
        /// <see cref="RewindReplayTargetRecordingId"/> (set by
        /// <see cref="SetRewindReplayTargetScope"/> inside <see cref="BeginRewindForOwner"/>).
        /// After load, we resolve the owner from committed state and re-run the drop
        /// against the freshly-loaded supersede list using
        /// <see cref="RewindContext.RewindAdjustedUT"/> as the threshold.
        /// </para>
        ///
        /// <para>
        /// Callers: <see cref="ParsekScenario.OnLoad"/>, immediately after
        /// <see cref="ParsekScenario.LoadRewindStagingState"/>, gated on
        /// <see cref="RewindContext.IsRewinding"/>.
        /// </para>
        ///
        /// Returns the number of relations dropped on this re-apply pass.
        /// </summary>
        internal static int ReapplyRewindSupersedeDropAfterLoad()
        {
            if (!RewindContext.IsRewinding) return 0;
            string ownerId = RewindReplayTargetRecordingId;
            if (string.IsNullOrEmpty(ownerId)) return 0;
            Recording owner = TryFindCommittedRecordingById(ownerId);
            if (owner == null)
            {
                if (!SuppressLogging)
                    ParsekLog.Verbose("Rewind",
                        $"ReapplyRewindSupersedeDropAfterLoad: skipped — owner id '{ownerId}' "
                        + "not found in committed recordings post-load (recording deleted?).");
                return 0;
            }
            int dropped = DropSupersedesRewoundOutOfExistence(owner, RewindContext.RewindAdjustedUT);
            if (dropped > 0 && !SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Re-applied supersede drop after LoadScene: dropped {dropped} relation(s) "
                    + $"(rewindUT={RewindContext.RewindAdjustedUT:F1} owner='{owner.VesselName}')");
            return dropped;
        }

        /// <summary>
        /// At rewind time, drops <see cref="RecordingSupersedeRelation"/> rows whose
        /// fork (<c>NewRecordingId</c>) starts at or after <paramref name="rewindAdjustedUT"/>
        /// AND whose source (<c>OldRecordingId</c>) belongs to the rewound owner's tree.
        /// Walks the entire tree of the rewound owner so branch recordings (e.g. an
        /// upper-stage Probe at index #7) are also unsuppressed — without this, only
        /// the owner's row drops and the branch ghosts stay
        /// <c>reason=superseded-by-relation</c> after the user re-launches.
        ///
        /// Pure-static: takes the supersede list as a parameter so it's directly unit
        /// testable without Unity. Returns the number of relations dropped.
        ///
        /// Multi-generational chains (A → B → C with A as the rewound owner) collapse
        /// correctly: <paramref name="ownerTreeRecordings"/> includes B (and B's
        /// StartUT >= rewindUT, so B is in <c>rewoundOutOldIds</c>), so both A→B
        /// and B→C drop.
        /// </summary>
        internal static int DropSupersedesRewoundOutOfExistencePure(
            Recording owner,
            double rewindAdjustedUT,
            IReadOnlyList<Recording> ownerTreeRecordings,
            IReadOnlyDictionary<string, Recording> liveRecordingsById,
            List<RecordingSupersedeRelation> supersedes)
        {
            return DropSupersedesRewoundOutOfExistenceDetailedPure(
                owner,
                rewindAdjustedUT,
                ownerTreeRecordings,
                liveRecordingsById,
                supersedes).DroppedRelationCount;
        }

        internal static RewindSupersedeRollbackResult DropSupersedesRewoundOutOfExistenceDetailedPure(
            Recording owner,
            double rewindAdjustedUT,
            IReadOnlyList<Recording> ownerTreeRecordings,
            IReadOnlyDictionary<string, Recording> liveRecordingsById,
            List<RecordingSupersedeRelation> supersedes)
        {
            var result = new RewindSupersedeRollbackResult();
            if (owner == null || supersedes == null || supersedes.Count == 0)
                return result;

            // Build set of recording ids that are rewound out: the owner itself plus
            // every recording in the owner's tree whose StartUT >= rewindAdjustedUT.
            var rewoundOutOldIds = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(owner.RecordingId))
                rewoundOutOldIds.Add(owner.RecordingId);
            if (ownerTreeRecordings != null)
            {
                for (int i = 0; i < ownerTreeRecordings.Count; i++)
                {
                    var treeRec = ownerTreeRecordings[i];
                    if (treeRec == null || string.IsNullOrEmpty(treeRec.RecordingId))
                        continue;
                    if (treeRec.StartUT >= rewindAdjustedUT)
                        rewoundOutOldIds.Add(treeRec.RecordingId);
                }
            }

            // Two-pass classification of in-scope supersede relations:
            //
            //   Pass 1 — for every relation whose OldRecordingId belongs to the
            //     rewound subtree AND whose fork is "rewound out" (preferred
            //     signal: Recording.StartUT; orphan fallback: rel.UT — the merge
            //     time recorded on the relation itself, so one-sided orphan rows
            //     don't silently keep suppressing OldRecordingId after rewind):
            //       - if the fork's MergeState is Immutable → tentatively
            //         preserve (canon fork survives parent rewind);
            //       - otherwise → drop + retire (existing behaviour).
            //
            //   Pass 2 — demote any tentative preservation whose OldRecordingId
            //     is itself being retired in this same batch. The canon fork's
            //     priorTip is gone, so the canon has no live source to be canon
            //     over; preserving it would leave A and C both visible alongside
            //     the restored A — the same double-materialization regression
            //     that PR #776/#777 fixed (see
            //     docs/dev/plans/fix-watch-double-probe-ghost-after-rewind.md).
            //
            // Coupling hazard: ParsekScenario.MarkRewoundTreeRecordingsAsGhostOnly
            // currently scopes SpawnSuppressedByRewind to the active/source
            // recording only (per #589). If that scope ever expands to cover
            // same-tree future recordings, the preserved canon fork would be
            // silently re-suppressed at spawn time even though this predicate
            // kept its supersede relation. Watch that helper during review of
            // any future Rewind-scope changes.
            var pendingDrops = new List<RecordingSupersedeRelation>();
            var pendingImmutablePreservations = new List<RecordingSupersedeRelation>();
            for (int i = 0; i < supersedes.Count; i++)
            {
                var rel = supersedes[i];
                if (rel == null || string.IsNullOrEmpty(rel.OldRecordingId)) continue;
                if (string.IsNullOrEmpty(rel.NewRecordingId)) continue;

                // Two distinct in-scope cases:
                //
                //   (a) "Outgoing": rel.OldRecordingId is in the rewound subtree
                //       (parent rewind on A drops every A→fork relation). The
                //       canonical case: parent A is rewound, all A's forks must
                //       go (or preserve, if Immutable).
                //
                //   (b) "Incoming, self-rewind on canon": rel.NewRecordingId IS
                //       the rewind owner. The user clicked Rewind on the canon
                //       fork itself — they want to undo this canon recording.
                //       The incoming relation (priorTip → canon) must drop so
                //       priorTip becomes visible again. Without this, A→B(Imm)
                //       with self-rewind on B leaves A→B intact, B's recording
                //       is rewound away but A stays hidden by the orphaned
                //       relation.
                //
                // Cases (a) and (b) overlap in the parent-rewind subtree case
                // (both Old and New are in rewoundOutOldIds). We distinguish
                // case (b) explicitly here so it bypasses the Immutable
                // preservation branch — self-rewind on a canon fork must drop
                // the relation regardless of MergeState, because the user is
                // explicitly undoing this canon. Outgoing relations from the
                // canon-fork-as-owner already drop via case (a) — see
                // Rollback_OwnerIsImmutableFork_RewindOnSelfStillDrops.
                bool oldInScope = rewoundOutOldIds.Contains(rel.OldRecordingId);
                bool newIsSelfRewind = !string.IsNullOrEmpty(owner.RecordingId)
                    && string.Equals(rel.NewRecordingId, owner.RecordingId, StringComparison.Ordinal);
                if (!oldInScope && !newIsSelfRewind) continue;

                Recording newRec = null;
                if (liveRecordingsById != null)
                    liveRecordingsById.TryGetValue(rel.NewRecordingId, out newRec);
                double effectiveForkUT = newRec != null ? newRec.StartUT : rel.UT;
                if (effectiveForkUT < rewindAdjustedUT) continue;

                // Self-rewind on the canon (case b): force drop. The Immutable
                // preservation contract applies only to parent rewind — when
                // the user explicitly rewinds the canon fork itself, the
                // canon is being undone, not preserved.
                //
                // Track the forced-drop new-id so EnsureRewindRetirementsForRollback's
                // defensive Immutable guard can let this retirement proceed
                // (instead of re-inserting the relation we just chose to drop).
                if (newIsSelfRewind)
                {
                    pendingDrops.Add(rel);
                    if (!string.IsNullOrEmpty(rel.NewRecordingId))
                        result.ForcedSelfRewindDropIds.Add(rel.NewRecordingId);
                    continue;
                }

                // Orphan-fallback (newRec == null) cannot read MergeState — drop
                // as today; the fork is gone anyway and the relation would
                // otherwise dangle and silently suppress OldRecordingId.
                if (newRec != null && newRec.MergeState == MergeState.Immutable)
                    pendingImmutablePreservations.Add(rel);
                else
                    pendingDrops.Add(rel);
            }

            // Pass 2 — demote preservations whose priorTip is itself retired,
            // iterating to a fixpoint so cascades propagate.
            //
            // pendingRetiredNewIds = { rel.NewRecordingId | rel ∈ pendingDrops }
            // (the New of each drop is the fork that gets retired; the Old gets
            // restored, so checking against Olds would be the wrong direction).
            //
            // Cascade example: A → B(Provisional) → C(Immutable) → D(Immutable),
            // rewind past A's start.
            //   Initial: pendingDrops=[A→B], pendingImmutablePreservations=[B→C, C→D].
            //   Pass 2 iter 1: B in pendingRetiredNewIds={B} → B→C demotes;
            //     pendingRetiredNewIds becomes {B,C}.
            //   Pass 2 iter 2: C now in pendingRetiredNewIds → C→D demotes too;
            //     pendingRetiredNewIds becomes {B,C,D}.
            //   Pass 2 iter 3: no more preservations → terminate.
            // Without the fixpoint loop, C→D would survive the demotion pass
            // and D would render as canon alongside the restored A — the
            // double-materialization regression PR #776/#777 fixes.
            if (pendingImmutablePreservations.Count > 0)
            {
                var pendingRetiredNewIds = new HashSet<string>(
                    StringComparer.Ordinal);
                for (int i = 0; i < pendingDrops.Count; i++)
                {
                    string newId = pendingDrops[i].NewRecordingId;
                    if (!string.IsNullOrEmpty(newId))
                        pendingRetiredNewIds.Add(newId);
                }

                bool changedThisIteration = true;
                while (changedThisIteration)
                {
                    changedThisIteration = false;
                    for (int i = pendingImmutablePreservations.Count - 1; i >= 0; i--)
                    {
                        var rel = pendingImmutablePreservations[i];
                        if (!string.IsNullOrEmpty(rel.OldRecordingId)
                            && pendingRetiredNewIds.Contains(rel.OldRecordingId))
                        {
                            pendingImmutablePreservations.RemoveAt(i);
                            pendingDrops.Add(rel);
                            if (!string.IsNullOrEmpty(rel.NewRecordingId))
                            {
                                result.DemotedImmutablePreservationIds.Add(rel.NewRecordingId);
                                // Adding the demoted New to the retired set
                                // makes the cascade transitive: a later
                                // preservation candidate whose Old is this
                                // demoted New will also demote on the next
                                // iteration.
                                pendingRetiredNewIds.Add(rel.NewRecordingId);
                            }
                            changedThisIteration = true;
                        }
                    }
                }

                // Anything still in pendingImmutablePreservations is a
                // confirmed canon preservation.
                for (int i = 0; i < pendingImmutablePreservations.Count; i++)
                {
                    string newId = pendingImmutablePreservations[i].NewRecordingId;
                    if (!string.IsNullOrEmpty(newId))
                        result.SkippedImmutableForkRecordingIds.Add(newId);
                }
            }

            if (pendingDrops.Count == 0)
                return result;

            for (int i = 0; i < pendingDrops.Count; i++)
            {
                var rel = pendingDrops[i];
                result.DroppedRelations.Add(rel);
                if (!string.IsNullOrEmpty(rel.NewRecordingId))
                    result.RetiredForkRecordingIds.Add(rel.NewRecordingId);
                if (!string.IsNullOrEmpty(rel.OldRecordingId))
                    result.RestoredRecordingIds.Add(rel.OldRecordingId);
                supersedes.Remove(pendingDrops[i]);
            }

            foreach (string retiredId in result.RetiredForkRecordingIds)
                result.RestoredRecordingIds.Remove(retiredId);

            // Also prune any preserved canon ids — a recording can be both
            // the Old of a dropped relation (added to RestoredRecordingIds
            // by the apply loop above) AND the New of a preserved Immutable
            // relation (in SkippedImmutableForkRecordingIds, hence the canon
            // head). Example: A → B(Imm) → C(Prov) → D(Imm) rewind past A.
            // B preserves as canon (A→B kept) but is also the priorTip of
            // the dropped B→C, so it lands in RestoredRecordingIds. The
            // live caller's Pass 2 (PR #807 old-side retirement) iterates
            // RestoredRecordingIds and would retire B — leaving NO canon
            // head visible (A hidden by surviving A→B, B retired by old-side
            // pass). Canon preservation must win: anything in
            // SkippedImmutableForkRecordingIds is the canon and stays
            // visible, never a candidate for old-side retirement.
            foreach (string preservedId in result.SkippedImmutableForkRecordingIds)
                result.RestoredRecordingIds.Remove(preservedId);

            return result;
        }

        /// <summary>
        /// Live entry point that resolves the owner's tree + live-recordings dict from
        /// the static <see cref="committedTrees"/> + <see cref="committedRecordings"/>
        /// and the <see cref="ParsekScenario.Instance.RecordingSupersedes"/> list, then
        /// delegates to <see cref="DropSupersedesRewoundOutOfExistencePure"/>.
        /// </summary>
        internal static int DropSupersedesRewoundOutOfExistence(
            Recording owner, double rewindAdjustedUT)
        {
            if (owner == null) return 0;
            var scenario = ParsekScenario.Instance;
            if (scenario?.RecordingSupersedes == null
                || scenario.RecordingSupersedes.Count == 0)
                return 0;

            // Resolve the owner's tree by id. If the owner is a pre-tree-mode
            // standalone recording (null/empty TreeId — see `Recording.TreeId`'s
            // "null = standalone (pre-tree recording)" doc), there is no tree to
            // walk and the drop falls back to owner-only matching. Log it so the
            // degradation is visible in playtest diagnostics rather than silently
            // missing branch supersedes.
            RecordingTree ownerTree = null;
            if (string.IsNullOrEmpty(owner.TreeId))
            {
                if (!SuppressLogging)
                    ParsekLog.Verbose("Rewind",
                        $"DropSupersedesRewoundOutOfExistence: owner '{owner.VesselName}' (id={owner.RecordingId}) "
                        + "has no TreeId; falling back to owner-only supersede drop "
                        + "(no tree-walk for branch recordings).");
            }
            else
            {
                for (int t = 0; t < committedTrees.Count; t++)
                {
                    if (committedTrees[t] == null) continue;
                    if (string.Equals(committedTrees[t].Id, owner.TreeId, StringComparison.Ordinal))
                    {
                        ownerTree = committedTrees[t];
                        break;
                    }
                }
            }

            List<Recording> ownerTreeRecordings = null;
            if (ownerTree?.Recordings != null && ownerTree.Recordings.Count > 0)
            {
                ownerTreeRecordings = new List<Recording>(ownerTree.Recordings.Count);
                foreach (var kvp in ownerTree.Recordings)
                {
                    if (kvp.Value != null)
                        ownerTreeRecordings.Add(kvp.Value);
                }
            }

            // Live-recordings dict is committed-only by design. Re-Fly forks
            // commit before Rewind is reachable (the merge-journal precondition
            // above refuses Rewind during an in-flight commit), so the committed
            // list is the authoritative "currently visible" set for supersede
            // compute. Pending/uncommitted recordings aren't yet superseding
            // anything, so excluding them is the right behavior.
            var liveById = new Dictionary<string, Recording>(StringComparer.Ordinal);
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var liveRec = committedRecordings[i];
                if (liveRec != null && !string.IsNullOrEmpty(liveRec.RecordingId))
                    liveById[liveRec.RecordingId] = liveRec;
            }

            RewindSupersedeRollbackResult rollback = DropSupersedesRewoundOutOfExistenceDetailedPure(
                owner,
                rewindAdjustedUT,
                ownerTreeRecordings,
                liveById,
                scenario.RecordingSupersedes);
            int dropped = rollback.DroppedRelationCount;
            RewindRetirementCounts retirementCounts = EnsureRewindRetirementsForRollback(
                scenario,
                rollback,
                rewindAdjustedUT,
                liveById,
                owner.RecordingId);
            int retired = retirementCounts.ForksAdded;
            int retiredOldSides = retirementCounts.OldSidesAdded;
            int skippedNonImmutableOldSides = retirementCounts.OldSidesSkippedNonImmutable;
            int restoredCount = rollback.RestoredRecordingIds.Count;

            // Invalidate the EffectiveState ERS cache. EffectiveState.ComputeERS
            // keys its cache on ParsekScenario.SupersedeStateVersion — without the
            // bump, any cached effective view stays stale until a later load or
            // unrelated mutation. Every other production supersede mutation
            // (SupersedeCommit) bumps this counter; the rewind drop must too.
            //
            // SkippedImmutableForkCount alone (no drops, no retirements) does NOT
            // require a cache bump: RecordingSupersedes and
            // RecordingRewindRetirements are both unchanged, so cached ERS stays
            // correct. The summary log still fires so the audit trail records
            // what the predicate decided.
            //
            // Invariant: the Pass-1 defense path in EnsureRewindRetirementsForRollback
            // (Immutable id in RetiredForkRecordingIds without explicit demotion)
            // performs a remove-then-restore round-trip on RecordingSupersedes —
            // the apply-drops loop above already removed the relation, and the
            // defense re-adds the same instance. The supersede list ends up
            // byte-identical to its pre-call state for that relation, so no
            // cache bump is strictly required for the defense alone. If a
            // future change relaxes the apply-drops loop's removal so the
            // defense becomes a net-add, this guard needs to grow a defense-
            // path counter (or track scenario.RecordingSupersedes.Count
            // changes explicitly) to keep ERS coherent.
            int skippedImmutable = rollback.SkippedImmutableForkCount;
            int demotedImmutable = rollback.DemotedImmutablePreservationCount;
            if (dropped > 0 || retired > 0 || retiredOldSides > 0)
                scenario.BumpSupersedeStateVersion();

            if ((dropped > 0 || retired > 0 || retiredOldSides > 0
                    || skippedImmutable > 0 || demotedImmutable > 0
                    || skippedNonImmutableOldSides > 0)
                && !SuppressLogging)
            {
                ParsekLog.Info("Rewind",
                    $"Rewind supersede rollback: dropped={dropped.ToString(CultureInfo.InvariantCulture)} " +
                    $"retiredForks={retired.ToString(CultureInfo.InvariantCulture)} " +
                    $"retiredOldSides={retiredOldSides.ToString(CultureInfo.InvariantCulture)} " +
                    $"skippedNonImmutableOldSides={skippedNonImmutableOldSides.ToString(CultureInfo.InvariantCulture)} " +
                    $"restored={restoredCount.ToString(CultureInfo.InvariantCulture)} " +
                    $"skippedImmutable={skippedImmutable.ToString(CultureInfo.InvariantCulture)} " +
                    $"demotedImmutable={demotedImmutable.ToString(CultureInfo.InvariantCulture)} " +
                    $"rewindUT={rewindAdjustedUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                    $"owner='{owner.VesselName}'");

                // Per-canon-fork preserve / demote audit lines. Bounded by the
                // number of preserved/demoted forks in this batch (rare event,
                // typically 0–2 entries) so no rate-limit needed.
                foreach (string preservedId in rollback.SkippedImmutableForkRecordingIds)
                {
                    ParsekLog.Info("Rewind",
                        $"Preserved canon fork across parent rewind: rec={preservedId} " +
                        $"mergeState=Immutable rewindUT={rewindAdjustedUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                        $"owner='{owner.VesselName}'");
                }
                foreach (string demotedId in rollback.DemotedImmutablePreservationIds)
                {
                    ParsekLog.Info("Rewind",
                        $"Demoted Immutable preservation to drop (priorTip retired): rec={demotedId} " +
                        $"rewindUT={rewindAdjustedUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                        $"owner='{owner.VesselName}'");
                }
            }
            return dropped;
        }

        internal struct RewindRetirementCounts
        {
            public int ForksAdded;
            public int OldSidesAdded;
            // Pass-2 old-side retirements skipped because the dropped supersede's
            // fork was not a non-self-rewound Immutable. The priorTip stays
            // visible so spawn-at-endpoint replays it. See
            // docs/dev/plans/fix-tree-rewind-supersede-old-side.md.
            public int OldSidesSkippedNonImmutable;
        }

        /// <summary>
        /// Returns true when at least one dropped relation targeting
        /// <paramref name="oldSideId"/> as its OldRecordingId has a fork
        /// (NewRecordingId) resolving to an <see cref="MergeState.Immutable"/>
        /// recording AND was not flagged as a forced self-rewind drop. That
        /// configuration represents a permanent (canon) supersede the rewind
        /// erased, so the priorTip stays retired. Any other configuration —
        /// non-Immutable fork, forced self-rewound canon, or missing fork —
        /// returns false so the priorTip can become visible again.
        ///
        /// Internal (not private) so the truth-table xUnit covers each branch
        /// without going through the full live entry point.
        /// </summary>
        internal static bool AnyDroppedRelationRetiresPriorTipPermanently(
            string oldSideId,
            RewindSupersedeRollbackResult rollback,
            IReadOnlyDictionary<string, Recording> liveRecordingsById)
        {
            if (string.IsNullOrEmpty(oldSideId)
                || rollback?.DroppedRelations == null
                || rollback.DroppedRelations.Count == 0)
                return false;

            for (int i = 0; i < rollback.DroppedRelations.Count; i++)
            {
                var rel = rollback.DroppedRelations[i];
                if (rel == null
                    || string.IsNullOrEmpty(rel.NewRecordingId)
                    || !string.Equals(rel.OldRecordingId, oldSideId, StringComparison.Ordinal))
                    continue;

                // Forced self-rewind on the canon means the user explicitly
                // undid the canon recording; the priorTip should become the
                // new visible state, not stay retired.
                if (rollback.ForcedSelfRewindDropIds != null
                    && rollback.ForcedSelfRewindDropIds.Contains(rel.NewRecordingId))
                    continue;

                Recording forkRec;
                if (liveRecordingsById != null
                    && liveRecordingsById.TryGetValue(rel.NewRecordingId, out forkRec)
                    && forkRec != null
                    && forkRec.MergeState == MergeState.Immutable)
                {
                    return true;
                }
            }
            return false;
        }

        private static RewindRetirementCounts EnsureRewindRetirementsForRollback(
            ParsekScenario scenario,
            RewindSupersedeRollbackResult rollback,
            double rewindAdjustedUT,
            IReadOnlyDictionary<string, Recording> liveRecordingsById,
            string ownerRecordingId)
        {
            var counts = default(RewindRetirementCounts);
            if (object.ReferenceEquals(null, scenario) || rollback == null)
                return counts;

            bool hasForks = rollback.RetiredForkRecordingIds.Count > 0;
            bool hasOldSides = rollback.RestoredRecordingIds.Count > 0;
            if (!hasForks && !hasOldSides)
                return counts;

            if (scenario.RecordingRewindRetirements == null)
                scenario.RecordingRewindRetirements = new List<RecordingRewindRetirement>();

            // Local scratch — we mutate this set in both passes so a fork retired
            // in pass 1 is not re-retired as an old side in pass 2 (and a previous
            // crash-recovery / re-apply iteration that already wrote a row for the
            // same id is a no-op). ComputeRewindRetiredRecordingIds returns a
            // fresh HashSet, so mutating it doesn't leak back into the scenario.
            var seenRetiredIds = EffectiveState.ComputeRewindRetiredRecordingIds(scenario.RecordingRewindRetirements);

            // Pass 1 — fork side (NewRecordingId of every dropped supersede).
            // These are the Re-Fly result recordings that need to disappear after
            // the rewind. Retire when the fork's StartUT >= rewindAdjustedUT (so a
            // fork that started exactly at the rewind boundary still retires; the
            // owner case is handled by the OldRecordingId never matching the
            // owner here).
            foreach (string retiredId in rollback.RetiredForkRecordingIds)
            {
                if (string.IsNullOrEmpty(retiredId) || seenRetiredIds.Contains(retiredId))
                    continue;

                Recording retiredRec = null;
                if (liveRecordingsById == null
                    || !liveRecordingsById.TryGetValue(retiredId, out retiredRec)
                    || retiredRec == null
                    || retiredRec.StartUT < rewindAdjustedUT)
                {
                    continue;
                }

                // Look up the source relation up front so the Immutable defense
                // path (next) can re-insert it into RecordingSupersedes.
                // Without that re-insert, a maintainer-error path that fed an
                // Immutable id into RetiredForkRecordingIds would leave the
                // relation already removed from supersedes (the upstream apply
                // loop mutates the live list), the canon fork un-retired by
                // this defense, and the priorTip un-superseded — i.e. exactly
                // the double-materialization regression that PR #776/#777
                // existed to prevent. Restoring the relation here re-supersedes
                // the priorTip so only the canon fork is visible.
                RecordingSupersedeRelation sourceRel = null;
                for (int i = 0; i < rollback.DroppedRelations.Count; i++)
                {
                    var rel = rollback.DroppedRelations[i];
                    if (rel != null && string.Equals(rel.NewRecordingId, retiredId, StringComparison.Ordinal))
                    {
                        sourceRel = rel;
                        break;
                    }
                }

                // Defence-in-depth: the predicate-classifier upstream already
                // routes Immutable forks into preservations (or, when their
                // priorTip is itself retired in the same batch, demotes them
                // explicitly; or, when the user self-rewinds the canon fork,
                // forces the drop explicitly). Refuse to write a retirement
                // for an Immutable recording when the upstream classifier
                // did NOT explicitly mark this id with one of those intents
                // — that path indicates a maintainer-error code path
                // populated RetiredForkRecordingIds with a canon id.
                // Intentionally-retired Immutable ids must retire normally:
                //   - Demoted (priorTip retired in same batch): the canon
                //     has nothing to be canon over.
                //   - Self-rewound (user rewinds canon itself): the user is
                //     explicitly undoing this canon recording.
                // Either case, preserving the relation would re-introduce
                // the double-materialization regression OR silently undo the
                // user's intent.
                bool wasExplicitlyDemoted =
                    rollback.DemotedImmutablePreservationIds.Contains(retiredId);
                bool wasForcedSelfRewindDrop =
                    rollback.ForcedSelfRewindDropIds.Contains(retiredId);
                bool wasIntentionallyDropped =
                    wasExplicitlyDemoted || wasForcedSelfRewindDrop;
                if (retiredRec.MergeState == MergeState.Immutable
                    && !wasIntentionallyDropped)
                {
                    bool restoredSupersedeLink = false;
                    if (sourceRel != null
                        && scenario.RecordingSupersedes != null
                        && !scenario.RecordingSupersedes.Contains(sourceRel))
                    {
                        scenario.RecordingSupersedes.Add(sourceRel);
                        restoredSupersedeLink = true;

                        // Pass 2 (old-side, below) iterates RestoredRecordingIds
                        // and would otherwise write a RewoundOutOldSideReason
                        // retirement for sourceRel.OldRecordingId. With the
                        // relation re-inserted here, the priorTip is logically
                        // superseded again — retiring it would be a redundant
                        // row that adds noise to the audit log and bumps the
                        // cache version for nothing. Remove it from the set so
                        // the old-side pass sees the correct semantic state.
                        if (!string.IsNullOrEmpty(sourceRel.OldRecordingId))
                            rollback.RestoredRecordingIds.Remove(sourceRel.OldRecordingId);
                    }
                    if (!SuppressLogging)
                        ParsekLog.Warn("Rewind",
                            $"Skipping retirement for Immutable canon recording rec={retiredId} " +
                            (restoredSupersedeLink
                                ? $"and re-inserting supersede relation {sourceRel.RelationId ?? "<no-id>"} (priorTip stays superseded) "
                                : "(no source relation to restore — priorTip may render alongside canon, investigate) ") +
                            $"— predicate-classifier should have preserved or explicitly demoted this. " +
                            $"Investigate the upstream Pass 1/Pass 2 logic.");
                    continue;
                }

                // Intentionally-retired Immutable forks carry a distinct
                // Reason so LoadTimeSweep's legacy-Immutable sweep can tell
                // intentional retirements apart from pre-fix bad state.
                // Without these tags, a legitimate Pass-2 demotion or
                // self-rewind retirement saved to disk would be undone on
                // next load (sweep would remove the retirement and
                // reconstruct the priorTip → canon supersede relation,
                // making the canon visible again — silently undoing the
                // user's rewind action on every load).
                string retirementReason;
                if (wasForcedSelfRewindDrop)
                    retirementReason = RecordingRewindRetirement.SelfRewoundCanonReason;
                else if (wasExplicitlyDemoted)
                    retirementReason = RecordingRewindRetirement.DemotedCanonReason;
                else
                    retirementReason = RecordingRewindRetirement.DefaultReason;
                var retirement = new RecordingRewindRetirement
                {
                    RetirementId = "rrt_" + Guid.NewGuid().ToString("N"),
                    RecordingId = retiredId,
                    RestoredRecordingId = sourceRel?.OldRecordingId,
                    SourceSupersedeRelationId = sourceRel?.RelationId,
                    RewindUT = rewindAdjustedUT,
                    CreatedUT = CurrentUniversalTimeForRewindRetirement(),
                    CreatedRealTime = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    Reason = retirementReason,
                };
                scenario.RecordingRewindRetirements.Add(retirement);
                seenRetiredIds.Add(retiredId);
                counts.ForksAdded++;

                if (!SuppressLogging)
                    ParsekLog.Info("Rewind",
                        $"Retired rewound-out fork rec={retiredId} " +
                        $"restored={retirement.RestoredRecordingId ?? "<none>"} " +
                        $"sourceRel={retirement.SourceSupersedeRelationId ?? "<none>"} " +
                        $"rewindUT={rewindAdjustedUT.ToString("F1", CultureInfo.InvariantCulture)}");
            }

            // Pass 2 — old side (OldRecordingId of every dropped supersede that
            // was not also a fork). When the rewind lands BEFORE every recording
            // in the rewound subtree, the originals that were superseded by the
            // (now-retired) fork are themselves "rewound out of existence" only
            // when the supersede was a *permanent* (canon Immutable) replacement.
            // For non-canon supersedes — CommittedProvisional, NotCommitted,
            // or orphan-fallback drops — the priorTip stays visible: the
            // supersede was a tentative re-fly attempt the rewind rolled back,
            // and spawn-at-endpoint replays the original ghost as the active
            // vessel reaches its endpoint (see
            // docs/dev/plans/fix-tree-rewind-supersede-old-side.md, and
            // logs/2026-05-13_2335_kerbal-x-booster-ghost-missing for the
            // reproduction). Use STRICT `>` against rewindAdjustedUT so
            // recordings whose StartUT equals the rewind boundary stay visible
            // (the "rewind to launch" pattern modeled by
            // DetailedRollback_MultiGenerationalChain_RetiresForksAndRestoresOnlyOrigin).
            // Skip the owner explicitly: the owner's StartUT can exceed
            // rewindAdjustedUT due to RewindToLaunchLeadTimeSeconds, but the
            // owner is the recording the rewind lands AT, not future content to
            // hide.
            foreach (string oldSideId in rollback.RestoredRecordingIds)
            {
                if (string.IsNullOrEmpty(oldSideId) || seenRetiredIds.Contains(oldSideId))
                    continue;

                if (!string.IsNullOrEmpty(ownerRecordingId)
                    && string.Equals(oldSideId, ownerRecordingId, StringComparison.Ordinal))
                {
                    if (!SuppressLogging)
                        ParsekLog.Verbose("Rewind",
                            $"Old-side retirement skipped for owner rec={oldSideId} " +
                            $"(rewind target — owner stays visible)");
                    continue;
                }

                Recording oldSideRec = null;
                if (liveRecordingsById == null
                    || !liveRecordingsById.TryGetValue(oldSideId, out oldSideRec)
                    || oldSideRec == null
                    || oldSideRec.StartUT <= rewindAdjustedUT)
                {
                    continue;
                }

                // The priorTip only stays permanently retired when at least
                // one dropped relation targeting it represented a permanent
                // (canon Immutable, non-self-rewound) supersede. For
                // non-canon supersedes the rewind rolls them back: the
                // priorTip is the restored canonical state.
                if (!AnyDroppedRelationRetiresPriorTipPermanently(
                        oldSideId, rollback, liveRecordingsById))
                {
                    counts.OldSidesSkippedNonImmutable++;
                    if (!SuppressLogging)
                        ParsekLog.Verbose("Rewind",
                            $"Old-side retirement skipped for rec={oldSideId} " +
                            $"reason=fork-non-immutable " +
                            $"rewindUT={rewindAdjustedUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                            $"(provisional re-fly rolled back; priorTip stays visible for spawn-at-endpoint)");
                    continue;
                }

                var retirement = new RecordingRewindRetirement
                {
                    RetirementId = "rrt_" + Guid.NewGuid().ToString("N"),
                    RecordingId = oldSideId,
                    // Old-side rows have no "thing they were superseded by" to
                    // restore — they ARE the original. LoadTimeSweep +
                    // TreeDiscardPurge gate on null RestoredRecordingId.
                    RestoredRecordingId = null,
                    // An old-side row can be the OldRecordingId of multiple
                    // dropped relations (fan-in into a single fork); picking one
                    // is misleading. Null says "no single source rel".
                    SourceSupersedeRelationId = null,
                    RewindUT = rewindAdjustedUT,
                    CreatedUT = CurrentUniversalTimeForRewindRetirement(),
                    CreatedRealTime = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    Reason = RecordingRewindRetirement.RewoundOutOldSideReason,
                };
                scenario.RecordingRewindRetirements.Add(retirement);
                seenRetiredIds.Add(oldSideId);
                counts.OldSidesAdded++;

                // Verbose per-row: a wide rewound subtree can produce many
                // old-side retirements in one rollback; the summary line
                // (`retiredOldSides=N` in the rollback summary) carries the
                // count at INFO. Per CLAUDE.md batch-counting convention.
                if (!SuppressLogging)
                    ParsekLog.Verbose("Rewind",
                        $"Retired rewound-out old-side rec={oldSideId} " +
                        $"startUT={oldSideRec.StartUT.ToString("F1", CultureInfo.InvariantCulture)} " +
                        $"rewindUT={rewindAdjustedUT.ToString("F1", CultureInfo.InvariantCulture)}");
            }

            return counts;
        }

        private static double CurrentUniversalTimeForRewindRetirement()
        {
            try
            {
                if (CurrentUniversalTimeForRewindRetirementOverrideForTesting != null)
                    return CurrentUniversalTimeForRewindRetirementOverrideForTesting();
                return Planetarium.GetUniversalTime();
            }
            catch (Exception ex)
            {
                double fallback = RewindContext.RewindAdjustedUT;
                if (RewindContext.IsRewinding && IsFinite(fallback))
                {
                    if (!SuppressLogging)
                        ParsekLog.Verbose("Rewind",
                            "Retirement createdUT fallback to rewindAdjustedUT=" +
                            fallback.ToString("F1", CultureInfo.InvariantCulture) +
                            $" after UT read failed ({ex.GetType().Name})");
                    return fallback;
                }

                if (!SuppressLogging)
                    ParsekLog.Verbose("Rewind",
                        $"Retirement createdUT unavailable; writing NaN after UT read failed ({ex.GetType().Name})");
                return double.NaN;
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
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

        internal static int DropNonMonotonicTrajectoryPoints(List<TrajectoryPoint> points)
        {
            return TrajectoryTextSidecarCodec.DropNonMonotonicTrajectoryPoints(points);
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

        internal static TrackSection BuildOpenOnRailsCheckpointSection(double startUT)
        {
            return OrbitSegmentCheckpointBridge.BuildOpenCheckpointSection(startUT);
        }

        internal static TrackSection BuildClosedOnRailsCheckpointSection(OrbitSegment segment)
        {
            return OrbitSegmentCheckpointBridge.BuildClosedCheckpointSection(segment);
        }

        internal static bool TryAppendClosedOnRailsCheckpointSection(
            Recording rec,
            OrbitSegment segment,
            bool markDirty,
            out string skipReason)
        {
            return OrbitSegmentCheckpointBridge.TryAppendClosedCheckpointSection(
                rec, segment, markDirty, out skipReason);
        }

        internal static OrbitSegmentCheckpointBridgeStats EnsureCheckpointSectionsForTopLevelOrbitSegments(
            Recording rec,
            bool markDirty,
            string context)
        {
            OrbitSegmentCheckpointBridgeStats stats =
                OrbitSegmentCheckpointBridge.EnsureCheckpointSectionsForTopLevelOrbitSegments(
                    rec, markDirty);

            if (stats.Changed && !SuppressLogging)
            {
                ParsekLog.Verbose("RecordingStore",
                    $"EnsureCheckpointSectionsForTopLevelOrbitSegments: recording={rec?.RecordingId} " +
                    $"context={context} added={stats.Added} skippedExisting={stats.SkippedExisting} " +
                    $"skippedInvalid={stats.SkippedInvalid} skippedPredicted={stats.SkippedPredicted} " +
                    $"skippedAfterPredicted={stats.SkippedAfterPredicted} " +
                    $"skippedCovered={stats.SkippedCovered} clipped={stats.Clipped}");
            }

            return stats;
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

        internal static void SerializeRouteProofMetadata(ConfigNode parent, Recording rec)
        {
            RouteProofCodec.SerializeRouteProofMetadata(parent, rec);
        }

        internal static void DeserializeRouteProofMetadata(ConfigNode parent, Recording rec)
        {
            RouteProofCodec.DeserializeRouteProofMetadata(parent, rec);
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

        internal static bool AreRecordingFilesCurrentForSave(Recording rec, out string reason)
        {
            return RecordingSidecarStore.AreRecordingFilesCurrentForSave(rec, out reason);
        }

        internal static bool AreRecordingFilesCurrentAtPathsForTesting(
            Recording rec, string precPath, string vesselPath, string ghostPath, out string reason)
        {
            return RecordingSidecarStore.AreRecordingFilesCurrentAtPaths(
                rec, precPath, vesselPath, ghostPath, out reason);
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
            if (rec != null)
            {
                rec.RecordingFormatVersion = CurrentRecordingFormatVersion;
                rec.RecordingSchemaGeneration = CurrentRecordingSchemaGeneration;
                EnsureCheckpointSectionsForTopLevelOrbitSegments(
                    rec,
                    markDirty: false,
                    context: "WriteTrajectorySidecar");
            }

            TrajectorySidecarBinary.Write(path, rec, sidecarEpoch);
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
            return TryProbeTrajectorySidecar(path, out probe, quietOnSuccess: false);
        }

        /// <summary>
        /// Probes a trajectory sidecar. When <paramref name="quietOnSuccess"/> is
        /// true, the routine Verbose summary line on a successful supported probe
        /// is suppressed; Warn lines for unsupported sidecars still fire because
        /// callers always want to see those (corruption, schema drift, pre-reset
        /// files). Use the quiet form from diagnostic-only preflights that run
        /// many times per save (e.g. trajectory-shrinkage warning).
        /// </summary>
        internal static bool TryProbeTrajectorySidecar(string path, out TrajectorySidecarProbe probe, bool quietOnSuccess)
        {
            probe = default(TrajectorySidecarProbe);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            if (TrajectorySidecarBinary.HasBinaryMagic(path))
            {
                bool binaryProbeOk = TrajectorySidecarBinary.TryProbe(path, out probe);
                if (binaryProbeOk && !SuppressLogging)
                {
                    if (!quietOnSuccess)
                    {
                        ParsekLog.Verbose("RecordingStore",
                            $"TryProbeTrajectorySidecar: encoding={probe.Encoding} magic={probe.MagicTag ?? "<none>"} " +
                            $"version={probe.FormatVersion} generation={probe.SchemaGeneration} " +
                            $"recording={probe.RecordingId} sidecarEpoch={probe.SidecarEpoch}");
                    }
                    if (!probe.Supported)
                    {
                        ParsekLog.Warn("RecordingStore",
                            $"TryProbeTrajectorySidecar: unsupported binary trajectory " +
                            $"reason={probe.FailureReason ?? "unknown"} version={probe.FormatVersion} " +
                            $"generation={probe.SchemaGeneration} magic={probe.MagicTag ?? "<none>"} " +
                            $"for recording={probe.RecordingId ?? "<none>"}");
                    }
                }

                return binaryProbeOk;
            }

            if (TrajectorySidecarBinary.HasPreResetBinaryMagic(path))
            {
                probe = TrajectorySidecarBinary.BuildPreResetMagicProbe();
                if (!SuppressLogging)
                {
                    ParsekLog.Warn("RecordingStore",
                        $"TryProbeTrajectorySidecar: unsupported pre-reset binary trajectory " +
                        $"reason={probe.FailureReason} magic={probe.MagicTag}");
                }
                return true;
            }

            probe = new TrajectorySidecarProbe
            {
                Success = true,
                Supported = false,
                Encoding = TrajectorySidecarEncoding.TextConfigNode,
                FormatVersion = -1,
                SchemaGeneration = 0,
                SidecarEpoch = 0,
                RecordingId = null,
                MagicTag = "<text>",
                LegacyNode = null,
                FailureReason = "text-sidecar-unsupported"
            };

            if (!SuppressLogging)
            {
                ParsekLog.Warn("RecordingStore",
                    $"TryProbeTrajectorySidecar: unsupported text trajectory sidecar " +
                    $"reason={probe.FailureReason} path='{path}'");
            }

            return true;
        }

        internal static void DeserializeTrajectorySidecar(string path, TrajectorySidecarProbe probe, Recording rec)
        {
            if (probe.Encoding == TrajectorySidecarEncoding.BinaryV0)
            {
                TrajectorySidecarBinary.Read(path, rec, probe);
                return;
            }

            throw new InvalidOperationException("Text trajectory sidecar loading is not supported after the v0 reset.");
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
            return "BinaryV0";
        }

        private static void SafeWriteConfigNode(ConfigNode node, string path)
        {
            FileIOUtils.SafeWriteConfigNode(node, path, "RecordingStore");
        }

        private static void WriteSnapshotSidecar(string path, ConfigNode node)
        {
            SnapshotSidecarCodec.Write(path, node);
        }

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

        /// <summary>
        /// Re-hydrates a recording's vessel snapshot from its <c>_vessel.craft</c>
        /// sidecar when the transient in-memory copy was dropped. Used by the
        /// terminal-spawn path so a spawnable leaf (e.g. an orbital payload) can
        /// still materialize after its snapshot was nulled in-session. No-op when
        /// already loaded; quiet when the sidecar is absent or unresolvable.
        /// </summary>
        internal static bool TryHydrateVesselSnapshotFromSidecar(Recording rec)
        {
            return RecordingSidecarStore.TryHydrateVesselSnapshotFromSidecar(rec);
        }

        internal static bool TryHydrateVesselSnapshotFromPath(Recording rec, string vesselPath)
        {
            return RecordingSidecarStore.TryHydrateVesselSnapshotFromPath(rec, vesselPath);
        }

        #endregion
    }
}
