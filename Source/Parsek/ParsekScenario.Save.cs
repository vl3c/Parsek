using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // The save path below OnSave: ghost-map vessel stripping, the tree and sidecar writes, the
    // pending and active tree nodes, game state and milestones.
    public partial class ParsekScenario
    {
        /// <summary>
        /// Removes every ghost map ProtoVessel from <paramref name="flightState"/> before stock writes
        /// it (audit R2). Called from <see cref="OnSave"/>'s <c>finally</c>, so it runs whether or not
        /// a save phase threw: stock builds the flight state from every live vessel (ghosts included)
        /// BEFORE it runs the scenario saves, and nothing on the load side can tell a serialized ghost
        /// from a player craft, so a skipped strip would leave ghosts in a flight state a later write
        /// could persist as real vessels.
        /// <para>A phase that threw still propagates (the strip never masks it): when
        /// <paramref name="phasesCompleted"/> is false a failing strip is logged and swallowed. When
        /// the phases completed, a failing strip rethrows so the save aborts rather than writing
        /// ghosts. Returns the number of ProtoVessels removed.</para>
        /// </summary>
        internal static int StripGhostMapVesselsFailClosed(
            FlightState flightState,
            string context,
            string savePhase,
            bool phasesCompleted)
        {
            if (flightState == null || GhostMapPresence.ghostMapVesselPids.Count == 0)
                return 0;
            try
            {
                int stripped = GhostMapPresence.StripFromSave(flightState);
                if (!phasesCompleted)
                {
                    ParsekLog.Warn("Scenario",
                        $"{context}: phase '{savePhase ?? "<none>"}' threw; stripped " +
                        $"{stripped.ToString(CultureInfo.InvariantCulture)} ghost ProtoVessel(s) anyway " +
                        "(fail-closed ghost strip)");
                }
                return stripped;
            }
            catch (Exception ex)
            {
                ParsekLog.Error("Scenario",
                    $"{context}: ghost ProtoVessel strip failed after phase '{savePhase ?? "<none>"}' " +
                    $"(phasesCompleted={phasesCompleted}): {ex.GetType().Name}: {ex.Message}" +
                    (phasesCompleted ? " - aborting the save so no ghost is written" : ""));
                if (phasesCompleted)
                    throw;
                return 0;
            }
        }

        /// <summary>
        /// Saves committed recording trees to RECORDING_TREE ConfigNodes and writes
        /// bulk data to external files for recordings whose data changed. Also writes
        /// the currently-active (in-flight) tree as a RECORDING_TREE with isActive=True
        /// when the save is taken during flight with an active recorder, so quickload
        /// can resume recording instead of finalizing the mission (see
        /// <c>docs/dev/plans/quickload-resume-recording.md</c>).
        /// </summary>
        internal static void SaveTreeRecordings(ConfigNode node)
        {
            node.RemoveNodes("RECORDING_TREE");
            var committedTrees = RecordingStore.CommittedTrees;
            ParsekLog.Info("Scenario", $"OnSave: saving {committedTrees.Count} committed tree(s)");
            int treeRecCount = 0;
            int treeTotalPoints = 0, treeTotalOrbitSegments = 0, treeTotalPartEvents = 0;
            int treeWithTrackSections = 0, treeWithSnapshots = 0, treeTotalBranchPoints = 0;
            // Lazily loaded carry-forward source (see the !treeFilesCurrent branch below):
            // parsed at most once per invocation, and not at all on the healthy path.
            bool carrySourceLoaded = false;
            ConfigNode carrySourceParsekNode = null;
            string carrySourcePath = null;
            for (int t = 0; t < committedTrees.Count; t++)
            {
                var tree = committedTrees[t];
                treeTotalBranchPoints += tree.BranchPoints.Count;

                bool treeFilesCurrent = true;
                foreach (var rec in tree.Recordings.Values)
                {
                    if (!EnsureRecordingFilesCurrentForSave(rec, CommittedTreeKind))
                        treeFilesCurrent = false;
                    treeRecCount++;
                    treeTotalPoints += rec.Points.Count;
                    treeTotalOrbitSegments += rec.OrbitSegments.Count;
                    treeTotalPartEvents += rec.PartEvents.Count;
                    if (rec.TrackSections != null && rec.TrackSections.Count > 0) treeWithTrackSections++;
                    if (rec.VesselSnapshot != null) treeWithSnapshots++;
                }

                if (!treeFilesCurrent)
                {
                    // A SAVE MUST NEVER DELETE A TREE IT CANNOT SERIALIZE. Skipping the
                    // AddNode here used to omit the whole tree from persistent.sfs - and
                    // because the skip condition (a recording whose sidecars are missing /
                    // unwritable) is STICKY, every subsequent save omitted it too: one
                    // unwritable recording permanently deleted an entire mission's metadata
                    // from the campaign save. Carry the tree's last-known-good node forward
                    // from the on-disk save instead, so the tree survives until the
                    // underlying sidecar fault is repaired or the player recovers it.
                    ParsekLog.Warn("Scenario",
                        $"OnSave: cannot write tree '{tree.TreeName}' id={tree.Id ?? "<none>"} " +
                        "from memory because at least one recording could not be written with " +
                        "current v0 sidecars");
                    // One ConfigNode.Load per OnSave at most, shared across every failing
                    // tree (the load is a full .sfs parse - doing it per tree turned a
                    // multi-tree fault into an O(trees) disk cost on every save).
                    if (!carrySourceLoaded)
                    {
                        carrySourceLoaded = true;
                        carrySourceParsekNode = TryLoadCarryForwardSourceParsekNode(out carrySourcePath);
                    }
                    if (TryCarryForwardCommittedTreeNodeFromLoadedSave(
                            node, carrySourceParsekNode, tree, out int carriedRecs, out int droppedRecs))
                    {
                        ParsekLog.Warn("Scenario",
                            $"OnSave: carried forward last-known-good RECORDING_TREE node for " +
                            $"'{tree.TreeName}' id={tree.Id ?? "<none>"} from '{carrySourcePath ?? "<none>"}' " +
                            $"(recordings kept={carriedRecs} dropped={droppedRecs}) so the unserializable " +
                            "tree is NOT dropped from this save. Investigate the sidecar fault above; " +
                            "the tree's metadata is preserved meanwhile");
                    }
                    else
                    {
                        ParsekLog.Error("Scenario",
                            $"OnSave: tree '{tree.TreeName}' id={tree.Id ?? "<none>"} could not be " +
                            "written from memory AND has no last-known-good node in the on-disk save " +
                            "to carry forward; it is being dropped from this save. Recover it from a " +
                            "backup or quicksave.sfs");
                    }
                    continue;
                }

                ConfigNode treeNode = node.AddNode("RECORDING_TREE");
                tree.Save(treeNode);
            }
            // GC-3: committed trees the schema gate rejected at load, written back verbatim.
            InertRecordingTrees.AppendTo(node, committedTrees);
            if (committedTrees.Count > 0)
                ParsekLog.Verbose("Scenario",
                    $"Saved {committedTrees.Count} trees ({treeRecCount} recordings): {treeTotalPoints} points, " +
                    $"{treeTotalOrbitSegments} orbit segments, {treeTotalPartEvents} part events, " +
                    $"{treeWithTrackSections} with track sections, {treeWithSnapshots} with snapshots, " +
                    $"{treeTotalBranchPoints} branch points");

            SaveActiveTreeIfAny(node);
            SavePendingTreeIfAny(node);

            // Zero-tree write over stranded sidecars is the "scenario load lost its tree
            // state" fingerprint; it is handled protectively (not just diagnosed) by
            // PreserveRecordingStateIfLoadFault, which OnSave invokes AFTER the mission
            // section so it can re-hydrate both trees and missions. See that method.
        }

        internal static bool EnsureRecordingFilesCurrentForSave(Recording rec, string treeKind)
        {
            if (rec == null)
                return false;

            if (RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting)
                return true;

            string currentReason;
            bool filesCurrent = RecordingSidecarStore.AreRecordingFilesCurrentForSave(rec, out currentReason);
            // A restore-attempt member shares its head with the committed copy, and the epoch
            // check alone cannot tell their bytes apart (the clone starts at the committed
            // copy's epoch): a committed rewrite at the same epoch must not read as current.
            if (filesCurrent
                && string.Equals(treeKind, ActiveTreeKind, StringComparison.Ordinal)
                && CommittedTreeRestoreAttempt.IsCommittedTreeRestoreAttemptRecordingId(rec.RecordingId)
                && !RecordingSidecarStore.TrajectoryHeadHoldsNamedBytes(rec, out string headReason))
            {
                filesCurrent = false;
                currentReason = headReason;
            }
            // A committed recording a restore attempt resumed in place: the attempt's save
            // wrote the head and kept these bytes as their hashed version. Current as named,
            // never rewritten from here (that would advance its epoch before any merge).
            if (!filesCurrent && !rec.FilesDirty && !rec.SidecarEpochAdvancePending
                && string.Equals(treeKind, CommittedTreeKind, StringComparison.Ordinal)
                && RecordingSidecarStore.AreRecordingFilesCurrentThroughNamedVersions(rec, out string versionReason))
            {
                ParsekLog.Verbose("Scenario",
                    $"OnSave: committed recording '{rec.VesselName ?? "<unnamed>"}' id={rec.RecordingId ?? "<none>"} " +
                    $"is current through its named sidecar version(s) (head: {currentReason ?? "<none>"}); not rewritten");
                return true;
            }
            // SidecarEpochAdvancePending: an out-of-band flush rewrote the content under a
            // preserved epoch; the skip-when-current fast path would keep that epoch
            // forever, so this save rewrites with the epoch advanced (the .sfs written
            // after this call then carries the same N+1).
            if (rec.FilesDirty || rec.SidecarEpochAdvancePending || !filesCurrent)
            {
                if (!RecordingStore.SuppressLogging && filesCurrent && !rec.FilesDirty)
                {
                    ParsekLog.Info("Scenario",
                        $"OnSave: advancing deferred sidecar epoch for recording='{rec.VesselName ?? "<unnamed>"}' " +
                        $"id={rec.RecordingId ?? "<none>"} treeKind={treeKind} epoch={rec.SidecarEpoch} " +
                        "(content was flushed out-of-band without advancing it)");
                }
                if (!RecordingStore.SuppressLogging && !filesCurrent)
                {
                    ParsekLog.Info("Scenario",
                        $"OnSave: rewriting sidecars for recording='{rec.VesselName ?? "<unnamed>"}' " +
                        $"id={rec.RecordingId ?? "<none>"} treeKind={treeKind} reason={currentReason ?? "<none>"}");

                    // R1-EMPTY-PROVISIONAL detection point 2. reason=trajectory-missing
                    // means the .prec does not exist at all, i.e. nothing has ever been
                    // recorded into this recording. When that recording IS the live
                    // Re-Fly session's provisional, no recorder is bound to it and the
                    // merge will have nothing to supersede the origin with. Catches the
                    // shape where the restore coroutine was never scheduled, so the
                    // give-up raise in RestoreActiveTreeFromPending never printed.
                    // Observation only: the save proceeds exactly as before.
                    var saveMarker = Instance?.ActiveReFlySessionMarker;
                    UnboundProvisionalRaise unbound =
                        ReFlyProvisionalBinding.EvaluateSidecarRewrite(
                            saveMarker, rec.RecordingId, currentReason);
                    if (unbound.ShouldRaise)
                    {
                        ParsekLog.Warn("ReFlySession",
                            $"outcome=unbound-refly-provisional reason={unbound.Reason} " +
                            $"sess={saveMarker.SessionId ?? "<no-id>"} " +
                            $"provisional={rec.RecordingId ?? "<none>"} " +
                            $"markerTree={saveMarker.TreeId ?? "<none>"} " +
                            $"origin={saveMarker.OriginChildRecordingId ?? "<none>"} " +
                            $"treeKind={treeKind} — this session's provisional is being " +
                            "saved with no trajectory sidecar, so nothing has recorded into " +
                            "it. If the session concludes in this state the merge writes 0 " +
                            "supersede rows and the origin branch stays effective");
                    }
                }

                if (!RecordingSidecarStore.SaveRecordingFiles(rec))
                {
                    ScenarioLog($"[Parsek Scenario] WARNING: File write failed for tree recording '{rec.VesselName}'");
                    return false;
                }
            }

            if (rec.FilesDirty || rec.SidecarLoadFailed)
            {
                ParsekLog.Warn("Scenario",
                    $"OnSave: recording '{rec.VesselName ?? "<unnamed>"}' id={rec.RecordingId ?? "<none>"} " +
                    $"treeKind={treeKind} remains unsafe to serialize " +
                    $"filesDirty={rec.FilesDirty} sidecarLoadFailed={rec.SidecarLoadFailed} " +
                    $"reason={rec.SidecarLoadFailureReason ?? "<none>"}");
                return false;
            }

            if (!RecordingSidecarStore.AreRecordingFilesCurrentForSave(rec, out currentReason))
            {
                ParsekLog.Warn("Scenario",
                    $"OnSave: recording '{rec.VesselName ?? "<unnamed>"}' id={rec.RecordingId ?? "<none>"} " +
                    $"treeKind={treeKind} still does not have current sidecars after save " +
                    $"reason={currentReason ?? "<none>"}");
                return false;
            }

            return true;
        }

        private static void SavePendingTreeIfAny(ConfigNode node)
        {
            if (PendingTreeSlot.HasPendingTree)
            {
                var pendingTree = PendingTreeSlot.PendingTree;
                var pendingState = PendingTreeSlot.PendingTreeStateValue;
                if (pendingTree != null)
                {
                    if (pendingState == PendingTreeState.Finalized)
                    {
                        SavePendingTreeNode(
                            node,
                            pendingTree,
                            preservedDuringActiveRestore: false);
                    }
                    else if (pendingState == PendingTreeState.Limbo
                        || pendingState == PendingTreeState.LimboVesselSwitch)
                    {
                        // Data-loss fix (Limbo-tree drop): a Limbo / LimboVesselSwitch tree is
                        // the quickload-resume stash — TryRestoreActiveTreeNode removed it from
                        // committedTrees and parked it here, deferring the re-commit to a later
                        // OnFlightReady. Previously ONLY Finalized pending trees were serialized,
                        // so any OnSave that ran in the resume window (autosave / scene-exit /
                        // exiting KSP before OnFlightReady fired) wrote a persistent.sfs with the
                        // tree missing. Its mission then dangled and the tree + immutable sidecars
                        // were purged as orphans on the next load. Serialize it with the SAME
                        // isActive marker SaveActiveTreeIfAny uses so it round-trips through
                        // TryRestoreActiveTreeNode straight back into the Limbo resume flow (the
                        // stash state is re-derived from ActiveRecordingId: non-null => Limbo,
                        // null => LimboVesselSwitch). If an active node was already written this
                        // save (unexpected — the live active tree and the Limbo stash are normally
                        // mutually exclusive), fall back to a plain committed node so the tree is
                        // still durable and loads as a committed mission rather than a lost one.
                        bool activeNodeAlreadyWritten = HasActiveTreeNode(node);
                        string markerKey = activeNodeAlreadyWritten ? null : "isActive";
                        string resumeRewindSave = activeNodeAlreadyWritten
                            ? null
                            : ResolveLimboResumeRewindSave(pendingTree);
                        ParsekLog.Info("Scenario",
                            $"SavePendingTreeIfAny: serializing Limbo pending tree '{pendingTree.TreeName}' " +
                            $"state={pendingState} as {(activeNodeAlreadyWritten ? "committed-node (active node already present)" : "isActive resume node")} " +
                            "to keep the quickload-resume window crash/exit-safe");
                        SavePendingTreeNode(
                            node,
                            pendingTree,
                            preservedDuringActiveRestore: false,
                            markerKey: markerKey,
                            resumeRewindSave: resumeRewindSave);
                    }
                    else
                    {
                        ParsekLog.Verbose("Scenario",
                            $"SavePendingTreeIfAny: skipped pending tree '{pendingTree.TreeName}' " +
                            $"state={pendingState} (only Finalized / Limbo pending trees are serialized)");
                    }
                }
            }

            var savedPending = PendingTreeSlot.SavedPendingTreeDuringActiveRestore;
            if (savedPending != null
                && !ReferenceEquals(savedPending, PendingTreeSlot.PendingTree))
            {
                SavePendingTreeNode(
                    node,
                    savedPending,
                    preservedDuringActiveRestore: true);
            }
        }

        private static void SavePendingTreeNode(
            ConfigNode node,
            RecordingTree pendingTree,
            bool preservedDuringActiveRestore,
            string markerKey = "isPending",
            string resumeRewindSave = null)
        {
            if (pendingTree == null)
                return;

            int pendingRecCount = 0;
            int pendingDirtyCount = 0;
            int pendingSavedCount = 0;
            int pendingSaveFailedCount = 0;
            int pendingCommittedOverlapCount = 0;
            int pendingSkippedCommittedDirtyCount = 0;
            bool pendingFilesCurrent = true;
            foreach (var rec in pendingTree.Recordings.Values)
            {
                if (rec == null)
                    continue;

                bool committedOverlap = RecordingStore.IsCommittedRecordingId(rec.RecordingId);
                if (committedOverlap)
                    pendingCommittedOverlapCount++;

                bool wasDirty = rec.FilesDirty;
                // Non-dirty overlaps are still listed in the pending tree node: they
                // reference already-durable committed sidecars. Only dirty overlap
                // writes are blocked because they would mutate committed history
                // before the player consents to Merge.
                if (wasDirty)
                {
                    pendingDirtyCount++;
                    if (committedOverlap)
                    {
                        pendingSkippedCommittedDirtyCount++;
                        ParsekLog.Warn("Scenario",
                            $"SavePendingTreeIfAny: skipped dirty sidecar save for committed-overlap " +
                            $"recording '{rec.RecordingId ?? "<no-id>"}' in pending tree '{pendingTree.TreeName}' " +
                            "to avoid mutating committed history before merge consent");
                        pendingFilesCurrent = false;
                        pendingRecCount++;
                        continue;
                    }
                }

                if (!EnsureRecordingFilesCurrentForSave(rec, "pending tree"))
                {
                    pendingFilesCurrent = false;
                    pendingSaveFailedCount++;
                }
                else if (wasDirty)
                {
                    pendingSavedCount++;
                }
                pendingRecCount++;
            }

            if (!pendingFilesCurrent)
            {
                ParsekLog.Warn("Scenario",
                    $"SavePendingTreeIfAny: skipped pending tree '{pendingTree.TreeName}' " +
                    "because at least one recording could not be written with current v0 sidecars");
                return;
            }

            ConfigNode treeNode = node.AddNode("RECORDING_TREE");
            pendingTree.Save(treeNode);
            // markerKey selects how the node round-trips on load: "isPending" (Finalized
            // pending tree -> TryRestorePendingTreeNode), "isActive" (Limbo resume tree ->
            // TryRestoreActiveTreeNode -> re-stash Limbo), or null/empty (plain committed
            // node -> LoadRecordingTrees). resumeRewindSave mirrors the live recorder rewind
            // hint onto the isActive node so the resume coroutine keeps its rewind affordance.
            if (!string.IsNullOrEmpty(markerKey))
                treeNode.AddValue(markerKey, "True");
            if (!string.IsNullOrEmpty(resumeRewindSave))
                treeNode.AddValue("resumeRewindSave", resumeRewindSave);
            if (preservedDuringActiveRestore)
                PendingTreeSlot.MarkSavedPendingTreeDuringActiveRestoreSerializedForSave("SavePendingTreeIfAny");
            else
                PendingTreeSlot.MarkPendingTreeSerializedForSave("SavePendingTreeIfAny");

            ParsekLog.Info("Scenario",
                $"OnSave: wrote {(string.IsNullOrEmpty(markerKey) ? "COMMITTED" : (markerKey == "isActive" ? "ACTIVE-RESUME" : "PENDING"))} tree '{pendingTree.TreeName}' " +
                (preservedDuringActiveRestore ? "preserved during active-tree restore " : "") +
                $"({pendingRecCount} recording(s), dirty={pendingDirtyCount}, saved={pendingSavedCount}, " +
                $"failed={pendingSaveFailedCount}, committedOverlap={pendingCommittedOverlapCount}, " +
                $"skippedCommittedDirty={pendingSkippedCommittedDirtyCount})");
        }

        /// <summary>
        /// True if any RECORDING_TREE node already written to <paramref name="node"/> carries
        /// the <c>isActive</c> marker. Used by <see cref="SavePendingTreeIfAny"/> to avoid
        /// writing a second active node when serializing a Limbo resume tree (only one active
        /// node round-trips through <see cref="TryRestoreActiveTreeNode"/>).
        /// </summary>
        internal static bool HasActiveTreeNode(ConfigNode node)
        {
            if (node == null)
                return false;
            var treeNodes = node.GetNodes("RECORDING_TREE");
            for (int t = 0; t < treeNodes.Length; t++)
                if (IsActiveTreeNode(treeNodes[t]))
                    return true;
            return false;
        }

        /// <summary>
        /// Resolves the rewind-save filename to mirror onto a serialized Limbo resume tree's
        /// isActive node (read back by <see cref="TryRestoreActiveTreeNode"/> as the resume
        /// hint). The live recorder is gone during the resume window, so the value comes from
        /// the tree root recording's <see cref="Recording.RewindSaveFileName"/> (copied there
        /// at stash time). Returns null when unavailable — a missing hint is benign.
        /// </summary>
        private static string ResolveLimboResumeRewindSave(RecordingTree tree)
        {
            if (tree == null || string.IsNullOrEmpty(tree.RootRecordingId) || tree.Recordings == null)
                return null;
            if (tree.Recordings.TryGetValue(tree.RootRecordingId, out var root) && root != null)
                return root.RewindSaveFileName;
            return null;
        }

        /// <summary>
        /// Tree ids that exist but are not yet committed at mission-reconcile time, so
        /// <see cref="MissionStore.PruneOrphans"/> must not treat their missions as orphans:
        /// the ids of any isActive / isPending RECORDING_TREE node in <paramref name="node"/>
        /// (restored into the pending slot LATER this OnLoad by <see cref="TryRestoreActiveTreeNode"/>
        /// / <see cref="TryRestorePendingTreeNode"/>), plus any already-stashed pending /
        /// saved-pending tree. Limbo-tree data-loss fix (second half). Also the committed trees
        /// kept inert (<see cref="InertRecordingTrees"/>, GC-3), which are never committed in this
        /// build; the cross-tree link reconcile defers while any of these is uncommitted.
        /// <para>
        /// If that later restore fails the schema gate (all recordings rejected), the protected
        /// mission dangles for one cycle, then the next save writes no node and the next load
        /// prunes it normally — self-healing, and still strictly better than deleting data.
        /// </para>
        /// </summary>
        internal static List<string> CollectParkedTreeIdsForMissionPrune(ConfigNode node)
        {
            var ids = new List<string>();
            if (node != null)
            {
                var treeNodes = node.GetNodes("RECORDING_TREE");
                for (int t = 0; t < treeNodes.Length; t++)
                {
                    if (!IsActiveTreeNode(treeNodes[t]) && !IsPendingTreeNode(treeNodes[t]))
                        continue;
                    string id = treeNodes[t].GetValue("id");
                    if (!string.IsNullOrEmpty(id))
                        ids.Add(id);
                }
            }
            if (PendingTreeSlot.HasPendingTree && !string.IsNullOrEmpty(PendingTreeSlot.PendingTree?.Id))
                ids.Add(PendingTreeSlot.PendingTree.Id);
            var savedPending = PendingTreeSlot.SavedPendingTreeDuringActiveRestore;
            if (savedPending != null && !string.IsNullOrEmpty(savedPending.Id))
                ids.Add(savedPending.Id);
            // GC-3: committed trees kept inert exist in the save too; their missions stay.
            int beforeInert = ids.Count;
            InertRecordingTrees.AddTreeIdsTo(ids);
            if (ids.Count > beforeInert)
                ParsekLog.Verbose("Scenario",
                    $"Mission prune: {(ids.Count - beforeInert).ToString(CultureInfo.InvariantCulture)} " +
                    "inert tree id(s) protected");
            return ids;
        }

        /// <summary>
        /// Outcome of <see cref="PlanActiveTreeSidecarSaves"/>: which active-tree
        /// recordings are sidecar-write candidates, the per-save observability
        /// counters, and whether any recording was skipped.
        /// <para>
        /// Observability (2026-04-09 debris-flow investigation, bug #280): the counters
        /// track how many of the iterated recordings were dirty vs saved, so future
        /// playtest logs can diagnose a FilesDirty-propagation gap. The outer
        /// <c>wrote ACTIVE tree ... (N recording(s))</c> log only reports the total
        /// iteration count, which once hid all debris recordings silently failing to save.
        /// </para>
        /// </summary>
        internal sealed class ActiveTreeSidecarSavePlan
        {
            /// <summary>Recordings whose sidecars may be written, in iteration order.</summary>
            internal readonly List<Recording> WriteCandidates = new List<Recording>();

            /// <summary>Parallel to <see cref="WriteCandidates"/>: each candidate's
            /// <c>FilesDirty</c> value as observed by the classify pass.</summary>
            internal readonly List<bool> WriteCandidateWasDirty = new List<bool>();

            internal int RecordingCount;
            internal int DirtyCount;
            internal int SkippedDegradedCount;
            internal int CommittedRestoreOverlapCount;

            /// <summary>
            /// False when at least one recording was skipped by the classify pass. The
            /// active-tree node is then NOT written, so no sidecar may be written either
            /// — that is the both-or-neither invariant.
            /// </summary>
            internal bool AllRecordingsWritable = true;
        }

        /// <summary>
        /// CLASSIFY pass for <see cref="SaveActiveTreeIfAny"/>: decides, for every
        /// recording in the active tree and WITHOUT writing anything, whether its
        /// sidecar may be written. One skip exists, a pure function of recording
        /// state: a hydration-failed recording with an empty trajectory payload.
        /// It means the whole active-tree node is skipped, so the caller must
        /// abandon every deferred sidecar write rather than orphan the ones it had
        /// already flushed. <c>internal static</c> for direct testability.
        /// </summary>
        internal static ActiveTreeSidecarSavePlan PlanActiveTreeSidecarSaves(RecordingTree activeTree)
        {
            var plan = new ActiveTreeSidecarSavePlan();
            if (activeTree == null || activeTree.Recordings == null)
                return plan;

            foreach (var rec in activeTree.Recordings.Values)
            {
                bool wasDirty = rec.FilesDirty;
                if (wasDirty)
                {
                    plan.DirtyCount++;
                    // A restore attempt's member that shares its id with the committed copy
                    // (a committed recording resumed in place) is written like any other: the
                    // write retains the committed bytes as a hashed version, the committed
                    // copy's node keeps naming them, and its save reads them there
                    // (RecordingSidecarStore.AreRecordingFilesCurrentThroughNamedVersions), so the
                    // committed history is unchanged until a merge replaces it.
                    if (CommittedTreeRestoreAttempt.IsCommittedTreeRestoreAttemptRecordingId(rec.RecordingId)
                        && !SwitchSegmentAttempts.IsMarkerOwnedSwitchSegmentRecordingId(rec.RecordingId))
                    {
                        plan.CommittedRestoreOverlapCount++;
                        ParsekLog.Verbose("Scenario",
                            $"SaveActiveTreeIfAny: writing restore-attempt recording '{rec.RecordingId ?? "<no-id>"}' " +
                            $"in active tree '{activeTree.TreeName}' over its committed copy's id " +
                            "(the committed bytes stay as their hashed version)");
                    }

                    // Switch-segment narrowing (segment-scoped-switch-fly-autorecord
                    // Phase D): marker-owned new segment recordings must be durable
                    // enough to survive F5/save/reload. They share id space with the
                    // committed-restore attempt only when both run concurrently
                    // (e.g. a switch/Fly under a #866 clone-restore), but the new
                    // segment id is a fresh-owned recording, not an original
                    // committed parent. Allow the dirty sidecar save through.
                    if (SwitchSegmentAttempts.IsMarkerOwnedSwitchSegmentRecordingId(rec.RecordingId))
                    {
                        var bypassSession = ParsekScenario.Instance?.ActiveSwitchSegmentSession;
                        ParsekLog.Verbose("Scenario",
                            $"SaveActiveTreeIfAny: dirty sidecar save bypassed for " +
                            $"reason=marker-owned-switch-segment recId={rec.RecordingId ?? "<no-id>"} " +
                            $"sessionId={(bypassSession != null ? bypassSession.SessionId.ToString("D", CultureInfo.InvariantCulture) : "<null>")}");
                    }

                    if (ShouldSkipActiveTreeEmptySidecarOverwrite(rec))
                    {
                        plan.SkippedDegradedCount++;
                        ParsekLog.Warn("Scenario",
                            $"SaveActiveTreeIfAny: skipped empty sidecar overwrite for hydration-failed " +
                            $"recording '{rec.RecordingId ?? "<no-id>"}' vessel='{rec.VesselName ?? "<no-name>"}' " +
                            $"reason={rec.SidecarLoadFailureReason ?? "<unknown>"}");
                        plan.AllRecordingsWritable = false;
                        continue;
                    }
                }

                plan.WriteCandidates.Add(rec);
                plan.WriteCandidateWasDirty.Add(wasDirty);
                plan.RecordingCount++;
            }

            return plan;
        }

        /// <summary>
        /// Writes the currently-active (in-flight) recording tree as an extra RECORDING_TREE
        /// ConfigNode marked with <c>isActive=True</c>, plus any recorder state needed to
        /// resume on quickload (chain id, boundary anchor UT, rewind save filename).
        /// <para>
        /// Guarded: only writes when <c>LoadedScene == FLIGHT</c>, <c>ParsekFlight.Instance</c>
        /// has a live active tree, and a recorder exists. Any other scene (KSC, TS, Main Menu)
        /// has no in-flight recording to preserve and would emit a stale stub.
        /// </para>
        /// </summary>
        private static void SaveActiveTreeIfAny(ConfigNode node)
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT) return;

            var flight = ParsekFlight.Instance;
            if (flight == null) return;

            var activeTree = flight.ActiveTreeForSerialization;
            if (activeTree == null) return;

            // Bug #266: the recorder may legitimately be null while activeTree is alive
            // — this is the "outsider" state, where the player switched (in-session or
            // via scene reload) to a vessel that has no recording context in the tree.
            // The tree is still tracking background vessels, so we MUST serialize it.
            // Without this branch, an F5 in outsider state would lose the tree on F9.
            var recorder = flight.ActiveRecorderForSerialization;

            // Flush recorder's buffered data into the active tree's current recording
            // before serializing. Otherwise the on-disk tree misses points/events that
            // were captured since the last chain boundary. Skip the flush in outsider
            // state — there is no recorder buffer to flush.
            if (recorder != null)
            {
                try
                {
                    flight.FlushRecorderIntoActiveTreeForSerialization();
                }
                catch (System.Exception ex)
                {
                    ParsekLog.Warn("Scenario",
                        $"SaveActiveTreeIfAny: flush failed ({ex.Message}) — active tree will be written with stale recorder data");
                }
            }
            else if (flight.ParkedSplitRecorderForSerialization != null)
            {
                // A deferred split parked the recorder (audit R6): its unflushed data goes into this
                // write only, so the deferred split still finds the tree it expects.
                ParkedSplitSaveOverlay overlay = null;
                try
                {
                    overlay = flight.BeginParkedSplitSaveOverlay();
                    WriteActiveTreeNode(
                        node, activeTree, flight.ParkedSplitRecorderForSerialization, rewindSourceParked: true);
                }
                finally
                {
                    overlay?.Restore();
                }
                return;
            }
            else
            {
                ParsekLog.Verbose("Scenario",
                    $"SaveActiveTreeIfAny: no live recorder for active tree '{activeTree.TreeName}' " +
                    $"(activeRecId={activeTree.ActiveRecordingId ?? "<null>"}) — outsider state (#266), " +
                    $"serializing tree without flush");
            }

            WriteActiveTreeNode(node, activeTree, recorder, rewindSourceParked: false);
        }

        /// <summary>
        /// The write half of <see cref="SaveActiveTreeIfAny"/>: sidecars (both-or-neither) and the
        /// <c>isActive</c> RECORDING_TREE node. <paramref name="rewindSource"/> is the recorder whose
        /// rewind save the node and the root carry: the live recorder, the recorder a deferred split
        /// parked (<paramref name="rewindSourceParked"/>; its stop moved the rewind save into its
        /// stop capture), or null in outsider state.
        /// </summary>
        private static void WriteActiveTreeNode(
            ConfigNode node,
            RecordingTree activeTree,
            FlightRecorder rewindSource,
            bool rewindSourceParked)
        {
            // Persist bulk data for active-tree recordings (same pattern as committed trees).
            int restoredFromCommittedTree = RestoreHydrationFailedRecordingsFromCommittedTree(
                activeTree,
                activeTree.ActiveRecordingId);
            if (restoredFromCommittedTree > 0)
            {
                ParsekLog.Warn("Scenario",
                    $"SaveActiveTreeIfAny: repaired {restoredFromCommittedTree} hydration-failed active-tree " +
                    "recording(s) from the committed tree before saving sidecars");
            }

            // Both-or-neither sidecar invariant (BUG-C latent secondary, 2026-06-07
            // career playtest): CLASSIFY every recording before writing ANY sidecar.
            // Both skip predicates below are pure functions of recording state, so
            // "will this tree node be written?" is knowable before the first write.
            // Writing sidecars inline used to let a legitimately-new marker-owned
            // switch-segment recording's sidecar land on disk and then have the whole
            // active-tree node skipped by a LATER committed-restore-overlap recording
            // in the same tree — a sidecar on disk with no metadata referencing it
            // (an orphan). Dictionary iteration order is unspecified, so which
            // recordings had already been written was luck, not design.
            ActiveTreeSidecarSavePlan sidecarPlan = PlanActiveTreeSidecarSaves(activeTree);
            int activeRecCount = sidecarPlan.RecordingCount;
            int activeDirtyCount = sidecarPlan.DirtyCount;
            int activeSavedCount = 0;
            int activeSaveFailedCount = 0;
            int activeSkippedDegradedCount = sidecarPlan.SkippedDegradedCount;
            int activeCommittedRestoreOverlapCount =
                sidecarPlan.CommittedRestoreOverlapCount;
            bool activeFilesCurrent = sidecarPlan.AllRecordingsWritable;

            // Single emit point for the per-save summary so the skip path and the
            // post-write path can never drift apart. Local function, so it reads the
            // counters at call time (0 saved / 0 failed on the skip path, by
            // construction: nothing was written).
            void LogActiveTreeSaveSummary()
            {
                ParsekLog.Info("Scenario",
                    $"SaveActiveTreeIfAny: iterated {activeRecCount} recording(s), " +
                    $"{activeDirtyCount} dirty, {activeSavedCount} saved, {activeSaveFailedCount} failed, " +
                    $"{activeSkippedDegradedCount} skippedDegraded, " +
                    $"{activeCommittedRestoreOverlapCount} committedRestoreOverlap");
            }

            if (!activeFilesCurrent)
            {
                LogActiveTreeSaveSummary();
                ParsekLog.Warn("Scenario",
                    $"SaveActiveTreeIfAny: skipped active tree '{activeTree.TreeName}' " +
                    "because at least one recording could not be written with current v0 sidecars; " +
                    $"outcome=both-or-neither deferredSidecarWrites={sidecarPlan.WriteCandidates.Count} " +
                    "(tree node NOT written, so no sidecar was written either)");
                return;
            }

            // WRITE pass. Every recording here passed the classify pass, so the tree
            // node WILL be written unless a sidecar write itself fails. A genuine I/O
            // failure mid-pass is the one residual case the invariant cannot cover: it
            // is only knowable by attempting the write, and a recording whose sidecar
            // could not be written is already not-current on disk either way.
            for (int i = 0; i < sidecarPlan.WriteCandidates.Count; i++)
            {
                Recording writeCandidate = sidecarPlan.WriteCandidates[i];
                if (!EnsureRecordingFilesCurrentForSave(writeCandidate, ActiveTreeKind))
                {
                    activeFilesCurrent = false;
                    activeSaveFailedCount++;
                }
                else if (sidecarPlan.WriteCandidateWasDirty[i])
                {
                    activeSavedCount++;
                }
            }

            LogActiveTreeSaveSummary();

            if (!activeFilesCurrent)
            {
                ParsekLog.Warn("Scenario",
                    $"SaveActiveTreeIfAny: skipped active tree '{activeTree.TreeName}' " +
                    "because at least one recording could not be written with current v0 sidecars");
                return;
            }

            // Quickload resume restores the recorder's rewind save hint from
            // resumeRewindSave, but the committed tree's Rewind button resolves through the
            // root recording. Mirror the live recorder rewind metadata onto the root before
            // serializing so an F5/F9 tree keeps its rewind affordance after merge.
            ParsekFlight.CopyRewindSaveToRoot(
                activeTree,
                rewindSource,
                logTag: "SaveActiveTreeIfAny");

            ConfigNode treeNode = node.AddNode("RECORDING_TREE");
            activeTree.Save(treeNode);
            treeNode.AddValue("isActive", "True");

            // Persist recorder state needed to resume after quickload.
            //
            // In outsider state (#266), there's no recorder and no live rewind save
            // filename to persist. The tree's root recording still has rewindSave from
            // the original launch (copied into rootRec.RewindSaveFileName at stash time).
            // A parked split recorder moved its rewind save into its stop capture.
            string resumeRewindSave = rewindSource?.RewindSaveFileName;
            if (string.IsNullOrEmpty(resumeRewindSave) && rewindSourceParked)
                resumeRewindSave = rewindSource.CaptureAtStop?.RewindSaveFileName;
            if (!string.IsNullOrEmpty(resumeRewindSave))
                treeNode.AddValue("resumeRewindSave", resumeRewindSave);

            ParsekLog.Info("Scenario",
                $"OnSave: wrote ACTIVE tree '{activeTree.TreeName}' ({activeRecCount} recording(s), " +
                $"activeRecId={activeTree.ActiveRecordingId ?? "<null>"}) for quickload resume");
        }

        /// <summary>
        /// Persists crew state, group hierarchy, game state events, baselines,
        /// milestones, and ledger data to the save node and external files.
        /// </summary>
        private static void PersistGameStateAndMilestones(ConfigNode node)
        {
            // Persist kerbal slots (new format) and crew replacements (backward compat)
            LedgerOrchestrator.Kerbals?.SaveSlots(node);
            CrewReservationManager.SaveCrewReplacements(node);

            // Persist group hierarchy and hidden groups
            GroupHierarchyStore.SaveInto(node);

            // Each game-state node is built once, written to its external file (kept as a
            // mirror for the offline tools) and carried in the save's GAME_STATE node, which a
            // cold load reads (design-load-restores-the-save.md 4.3).
            var eventsBuild = GameStateStore.BuildEventsNode(
                RecordingStore.CommittedTreeRestoreAttemptTreeId,
                RecordingStore.CommittedTreeRestoreAttemptReason);
            GameStateStore.WriteEventsNode(eventsBuild);
            node.AddValue("gameStateEventCount", GameStateStore.EventCount);

            var baselinesNode = new ConfigNode(BaselinesNodeName);
            foreach (var baseline in GameStateStore.Baselines)
            {
                ConfigNode baselineNode = GameStateStore.BuildBaselineNode(baseline);
                if (baselineNode == null)
                    continue;
                GameStateStore.WriteBaselineNode(baseline, baselineNode);
                baselinesNode.AddNode(baselineNode);
            }

            // Flush any uncaptured game state events into a milestone before saving.
            // Handles events that happened without a recording commit (e.g. tech
            // research in R&D without launching a flight). A committed-tree restore
            // attempt is special: same recording IDs are being reused by an unmerged
            // active clone. The event file save above filters those same-id attempt
            // tails from disk, but flushing a milestone here would make them durable
            // through milestones and advance the watermark before the player chooses
            // Merge. Defer the flush; the normal commit path will create the milestone
            // if the player accepts the segment.
            if (ShouldDeferPendingEventMilestoneFlushForSave())
            {
                ParsekLog.Warn("Scenario",
                    "OnSave: deferred pending event milestone flush while a committed-tree " +
                    "restore attempt is active; unmerged same-id attempt events remain memory-only");
            }
            else
            {
                ParsekLog.Verbose("Scenario", $"OnSave: flushing pending events at UT {Planetarium.GetUniversalTime():F0}");
                MilestoneStore.FlushPendingEvents(Planetarium.GetUniversalTime());
            }

            // Save milestones to external file + mutable state to .sfs
            ConfigNode milestonesNode = MilestoneStore.BuildMilestonesNode();
            MilestoneStore.WriteMilestonesNode(milestonesNode);
            MilestoneStore.SaveMutableState(node);

            // Save ledger to external file
            ConfigNode ledgerNode = Ledger.BuildSaveNode();
            LedgerOrchestrator.OnSave(ledgerNode);

            node.RemoveNodes(GameStateNodeName);
            node.AddNode(BuildGameStateNode(
                ledgerNode, eventsBuild.Root, milestonesNode, baselinesNode,
                GameStateRecorder.BuildPendingScienceNode(GameStateRecorder.PendingScienceSubjects),
                eventsBuild.AttemptEvents));
            ParsekLog.Verbose("Scenario",
                $"OnSave: wrote external game-state files and the GAME_STATE node (events={GameStateStore.EventCount}, " +
                $"milestones={MilestoneStore.MilestoneCount}, baselines={GameStateStore.BaselineCount}, " +
                $"ledgerRows={Ledger.Actions.Count}, pendingScience={GameStateRecorder.PendingScienceSubjects.Count}, " +
                $"attemptEvents={(eventsBuild.AttemptEvents != null ? eventsBuild.SkippedAttemptEvents : 0)}, " +
                $"attemptTree={RecordingStore.CommittedTreeRestoreAttemptTreeId ?? "<none>"})");
        }

        /// <summary>
        /// Returns true when an OnSave-time milestone flush should be deferred to
        /// preserve the #866 same-id committed-restore-tail invariant. Defers only
        /// when at least one pending event would persist into a milestone for a
        /// committed-tree-restore-overlap recording that is NOT marker-owned by
        /// the active <see cref="SwitchSegmentSession"/>. When all pending events
        /// for overlap ids are marker-owned new segment recordings (or there are
        /// no pending overlap-id events at all), the flush is allowed so the new
        /// segment's events survive F5/save/reload.
        ///
        /// <para>Segment-scoped-switch-fly-autorecord Phase D narrowing of the
        /// original predicate, which deferred unconditionally on any active
        /// committed-tree restore attempt and would have lost marker-owned new-id
        /// events across save/reload of an active switch/Fly segment.</para>
        /// </summary>
        internal static bool ShouldDeferPendingEventMilestoneFlushForSave()
        {
            if (!CommittedTreeRestoreAttempt.HasCommittedTreeRestoreAttempt)
                return false;

            // Phase D narrowing: enumerate pending events. Defer only if at
            // least one event belongs to a committed-tree-restore-overlap
            // recording that is NOT marker-owned. Untagged events and events
            // for unrelated ids are ignored — they were never gated by the
            // suppression contract in the first place.
            var events = GameStateStore.Events;
            if (events == null || events.Count == 0)
                return false;

            // LOW 15 (PR #876 review): the loop short-circuits on the first
            // non-marker-owned committed-overlap event via the `break` below,
            // so save-time cost is O(events-until-first-hit), not O(events).
            // Untagged + marker-owned + unrelated entries fall through to the
            // next iteration; the diagnostic counters under the loop summarize
            // the skip distribution so a saturated tail still leaves a trail
            // when the predicate genuinely needs to walk every event before
            // returning false.
            bool sawNonMarkerOwnedOverlap = false;
            int markerOwnedSkipped = 0;
            int unrelatedSkipped = 0;
            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (string.IsNullOrEmpty(evt.recordingId))
                {
                    unrelatedSkipped++;
                    continue;
                }

                if (SwitchSegmentAttempts.IsMarkerOwnedSwitchSegmentRecordingId(evt.recordingId))
                {
                    markerOwnedSkipped++;
                    continue;
                }

                if (CommittedTreeRestoreAttempt.IsCommittedTreeRestoreAttemptRecordingId(evt.recordingId))
                {
                    sawNonMarkerOwnedOverlap = true;
                    break;
                }

                unrelatedSkipped++;
            }

            if (!sawNonMarkerOwnedOverlap)
            {
                var session = ParsekScenario.Instance?.ActiveSwitchSegmentSession;
                ParsekLog.Verbose("Scenario",
                    $"ShouldDeferPendingEventMilestoneFlushForSave: not-deferred " +
                    $"reason=marker-owned-switch-segment markerOwned={markerOwnedSkipped} " +
                    $"unrelated={unrelatedSkipped} " +
                    $"sessionId={(session != null ? session.SessionId.ToString("D", CultureInfo.InvariantCulture) : "<null>")}");
            }

            return sawNonMarkerOwnedOverlap;
        }

        private static void SaveLedgerHeldWindow(ConfigNode node)
        {
            if (LedgerHeldWindow.IsSet && !IsOnLoadInProgress && !RecordingStore.RewindUTAdjustmentPending)
            {
                try
                {
                    if (Planetarium.fetch != null)
                        LedgerHeldWindow.ExpireIfClockPassed(Planetarium.GetUniversalTime(), "OnSave");
                }
                catch (Exception ex)
                {
                    ParsekLog.Verbose(LedgerHeldWindow.Tag,
                        "OnSave: clock unreadable (" + ex.GetType().Name + "), held-through UT kept as is");
                }
            }
            LedgerHeldWindow.SaveInto(node);
        }

        // CollectSurvivingPids was deleted with the pid-only ReconcileSpawnStateAfterStrip
        // overloads (#16): every production reconcile now collects (pid, launch-guid)
        // identities through CollectSurvivingVesselIdentities, because a bare craft-baked pid
        // cannot tell a preserved relaunch from the recording's own vessel.

        // ComputeSurvivorsFromProtoVesselPids was DELETED (#16). It had no production
        // caller and stated the stripped-pid subtraction contract at pid level; that contract
        // now lives — over (pid, launch-guid) identities, which is the shape both post-strip
        // reconciles actually use — in RewindInvoker.ReconcilePostStripSpawnState, and is
        // pinned by the ReconcilePostStrip_* cells in SpawnStateReconciliationTests.

        /// <summary>
        /// Returns true if OnSave should warn about a save folder mismatch.
        /// This detects when HighLogic.SaveFolder has changed since the scenario
        /// was loaded, which could cause file writes to target the wrong save.
        /// </summary>
        internal static bool IsSaveFolderMismatch(string scenarioFolder, string currentFolder)
        {
            return !string.IsNullOrEmpty(scenarioFolder) && currentFolder != scenarioFolder;
        }
    }
}
