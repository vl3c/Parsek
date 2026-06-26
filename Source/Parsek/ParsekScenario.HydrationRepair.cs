using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    public partial class ParsekScenario : ScenarioModule
    {
        internal static bool ShouldKeepPendingTreeAfterHydrationFailure(
            RecordingTree loadedTree,
            int staleEpochHydrationFailures)
        {
            return staleEpochHydrationFailures > 0
                && loadedTree != null
                && RecordingStore.HasPendingTree
                && RecordingStore.PendingTree != null
                && RecordingStore.PendingTree.Id == loadedTree.Id;
        }

        internal static int RestoreHydrationFailedRecordingsFromPendingTree(RecordingTree loadedTree)
        {
            if (loadedTree == null
                || !RecordingStore.HasPendingTree
                || RecordingStore.PendingTree == null
                || RecordingStore.PendingTree.Id != loadedTree.Id)
            {
                return 0;
            }

            var pendingTree = RecordingStore.PendingTree;
            var failedIds = new List<string>();
            foreach (var kvp in loadedTree.Recordings)
            {
                if (kvp.Value != null && kvp.Value.SidecarLoadFailed)
                    failedIds.Add(kvp.Key);
            }

            if (failedIds.Count == 0)
                return 0;

            int restored = 0;
            int snapshotOnlyRestored = 0;
            for (int i = 0; i < failedIds.Count; i++)
            {
                string recordingId = failedIds[i];
                Recording loadedRec;
                if (!loadedTree.Recordings.TryGetValue(recordingId, out loadedRec) || loadedRec == null)
                    continue;

                Recording pendingRec;
                if (!pendingTree.Recordings.TryGetValue(recordingId, out pendingRec) || pendingRec == null)
                    continue;

                bool snapshotFailure = IsSnapshotHydrationFailure(loadedRec.SidecarLoadFailureReason);
                if (TryRestoreSnapshotStateFromPendingRecording(loadedRec, pendingRec))
                {
                    restored++;
                    snapshotOnlyRestored++;
                    continue;
                }
                if (snapshotFailure)
                    continue;

                Recording restoredRec = Recording.DeepClone(pendingRec);
                RecordingStore.ClearSidecarLoadFailure(restoredRec);
                restoredRec.MarkFilesDirty();
                loadedTree.AddOrReplaceRecording(restoredRec);
                restored++;
            }

            if (restored > 0)
            {
                loadedTree.RebuildBackgroundMap();
                ParsekLog.Warn("Scenario",
                    $"TryRestoreActiveTreeNode: restored {restored} hydration-failed recording(s) " +
                    $"from matching pending tree '{pendingTree.TreeName}' into '{loadedTree.TreeName}'" +
                    (snapshotOnlyRestored > 0
                        ? $" ({snapshotOnlyRestored} snapshot-only, {restored - snapshotOnlyRestored} full)"
                        : ""));
            }

            return restored;
        }

        internal static int RestoreHydrationFailedRecordingsFromCommittedTree(
            RecordingTree loadedTree,
            string activeRecordingId = null)
        {
            if (loadedTree == null || string.IsNullOrEmpty(loadedTree.Id))
                return 0;

            RecordingTree committedTree = FindCommittedTreeById(loadedTree.Id, exclude: loadedTree);
            if (committedTree == null)
                return 0;

            int restored = 0;
            var restoreIds = new List<string>();
            foreach (var kvp in loadedTree.Recordings)
            {
                Recording loadedRec = kvp.Value;
                if (loadedRec == null)
                    continue;

                Recording sourceRec;
                if (!committedTree.Recordings.TryGetValue(kvp.Key, out sourceRec) || sourceRec == null)
                    continue;

                if (!ShouldRestoreHydrationFailureFromCommittedRecording(
                        loadedRec, sourceRec, activeRecordingId))
                    continue;

                restoreIds.Add(kvp.Key);
            }

            for (int i = 0; i < restoreIds.Count; i++)
            {
                string recordingId = restoreIds[i];
                Recording sourceRec;
                if (!committedTree.Recordings.TryGetValue(recordingId, out sourceRec) || sourceRec == null)
                    continue;

                Recording loadedRec;
                if (!loadedTree.Recordings.TryGetValue(recordingId, out loadedRec) || loadedRec == null)
                    continue;

                RestoreCommittedSidecarPayloadIntoActiveTreeRecording(loadedRec, sourceRec);
                restored++;
            }

            if (restored > 0)
            {
                loadedTree.RebuildBackgroundMap();
                ParsekLog.Warn("Scenario",
                    $"RestoreHydrationFailedRecordingsFromCommittedTree: restored {restored} active-tree " +
                    $"recording(s) from committed tree '{committedTree.TreeName}' (id={committedTree.Id})");
            }

            return restored;
        }

        /// <summary>
        /// Load-time topology repair for orphaned optimizer-split first halves.
        ///
        /// <para>
        /// Diagnosed from a 2026-05-02 playtest: a same-vessel chain successor
        /// with <c>ChainIndex=1</c> appeared in the committed tree, but the
        /// matching first-half predecessor had <c>ChainId=null</c> and
        /// <c>ChainIndex=-1</c>. Playback then treats the successor as a fresh
        /// ghost on the chain boundary — engines/FX rebuild and the seamless
        /// handoff is broken. The bad shape is already on disk before any
        /// session that loads it, and
        /// <see cref="SpliceMissingCommittedRecordingsIntoLoadedTree"/> faithfully
        /// copies the broken committed state, so a load-time self-heal is the
        /// right scope: fix the topology, mark dirty, and let the next OnSave
        /// persist the corrected shape.
        /// </para>
        ///
        /// <para>
        /// Conservative criteria: a successor must have
        /// <c>ChainBranch == 0</c>, <c>ChainIndex == 1</c>, a non-empty
        /// <c>ChainId</c>, a non-zero <c>VesselPersistentId</c>, a finite
        /// <c>StartUT</c>, and a non-negative <c>TreeOrder</c>. A candidate
        /// predecessor must live in the same tree (the tree's
        /// <c>Id</c> must be set), share the successor's
        /// <c>VesselPersistentId</c>, have an empty <c>ChainId</c> AND
        /// <c>ChainIndex &lt; 0</c> (so we never overwrite an existing chain
        /// assignment), carry a non-negative <c>TreeOrder</c> strictly less
        /// than the successor's, and end within
        /// <see cref="ChainPredecessorRepairUTEpsilon"/> of the successor's
        /// start. Recordings without an assigned <c>TreeOrder</c> on either
        /// side are rejected: the load-time
        /// <see cref="RecordingTree.RebuildBackgroundMap"/> path always
        /// assigns one before this repair can run, so an unset value at this
        /// point is a sign the data has not been normalized and the safe
        /// move is to skip rather than guess at chain ordering.
        /// </para>
        ///
        /// <para>
        /// Out of scope (gate keeps the repair narrow): orphaned
        /// <c>ChainBranch &gt; 0</c> branches; chains where the surviving
        /// successor's <c>ChainIndex</c> is anything other than <c>1</c>
        /// (e.g. a length-3 chain that lost both <c>[0]</c> and <c>[1]</c>
        /// — only the trailing-most index-1 successor's predecessor can be
        /// rebuilt by this pass).
        /// </para>
        ///
        /// <para>
        /// On match the repair sets the predecessor's <c>ChainId</c>,
        /// <c>ChainIndex = 0</c>, and <c>ChainBranch</c> from the successor
        /// and calls <see cref="Recording.MarkFilesDirty"/> so the next
        /// OnSave rewrites the predecessor's <c>.sfs</c> with the corrected
        /// shape. The repair does NOT itself rebuild the tree's background
        /// map; callers that batch repair with other tree mutations should
        /// rebuild once at the end of their batch (the splice path already
        /// does), and standalone callers must rebuild themselves when the
        /// returned count is non-zero — chain field changes affect
        /// <see cref="RecordingTree.IsBackgroundMapEligible"/>. Tie-break on
        /// equal <c>EndUT</c> gaps: smaller <c>TreeOrder</c> first, then
        /// ordinal <c>RecordingId</c> — fully deterministic.
        /// </para>
        ///
        /// <para>
        /// Idempotent: a second call on a tree the first call already
        /// healed finds no candidate (every former orphan now has
        /// <c>ChainId</c> set, which the candidate gate rejects) and
        /// returns 0.
        /// </para>
        /// </summary>
        internal static int RepairMissingContiguousChainPredecessors(
            RecordingTree tree,
            string context)
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
                return 0;

            int considered = 0;
            int repaired = 0;
            HashSet<string> reservedPredecessorIds = null;

            foreach (var kvp in tree.Recordings)
            {
                Recording successor = kvp.Value;
                if (!IsRepairableMissingChainPredecessorSuccessor(successor))
                    continue;

                considered++;
                Recording predecessor = FindMissingContiguousChainPredecessor(
                    tree,
                    successor,
                    reservedPredecessorIds);
                if (predecessor == null)
                    continue;

                double predecessorEndUT = predecessor.EndUT;
                double gapMs = Math.Abs(predecessorEndUT - successor.StartUT) * 1000.0;

                predecessor.ChainId = successor.ChainId;
                predecessor.ChainIndex = successor.ChainIndex - 1;
                predecessor.ChainBranch = successor.ChainBranch;
                predecessor.MarkFilesDirty();
                if (reservedPredecessorIds == null)
                    reservedPredecessorIds = new HashSet<string>(StringComparer.Ordinal);
                reservedPredecessorIds.Add(predecessor.RecordingId);
                repaired++;

                // Logged BEFORE rebuild so a future log post-mortem sees the
                // exact boundary-UT pair and gap that drove the decision.
                // predecessor.ChainIndex is omitted from the line because
                // the gate forces it to -1 going in and 0 coming out, so
                // the field is tautological; predecessorEndUT + gapMs are
                // the actually-diagnostic numbers when reviewing borderline
                // matches near the 1ms epsilon.
                ParsekLog.Info("Scenario", string.Format(
                    CultureInfo.InvariantCulture,
                    "RepairMissingChainPredecessor: context={0} tree={1} predecessor={2} successor={3} " +
                    "chain={4} predecessorEndUT={5:R} successorStartUT={6:R} gapMs={7:F4} vesselPid={8}",
                    context ?? "unknown",
                    tree.Id ?? "",
                    predecessor.RecordingId ?? "",
                    successor.RecordingId ?? "",
                    successor.ChainId ?? "",
                    predecessorEndUT,
                    successor.StartUT,
                    gapMs,
                    successor.VesselPersistentId));
            }

            if (considered > 0 || repaired > 0)
            {
                ParsekLog.Verbose("Scenario", string.Format(
                    CultureInfo.InvariantCulture,
                    "RepairMissingChainPredecessor summary: context={0} tree={1} considered={2} repaired={3}",
                    context ?? "unknown",
                    tree.Id ?? "",
                    considered,
                    repaired));
            }

            return repaired;
        }

        private static bool IsRepairableMissingChainPredecessorSuccessor(Recording successor)
        {
            return successor != null
                && !string.IsNullOrEmpty(successor.RecordingId)
                && !string.IsNullOrEmpty(successor.ChainId)
                && successor.ChainBranch == 0
                && successor.ChainIndex == 1
                && successor.VesselPersistentId != 0
                && successor.TreeOrder >= 0
                && IsFiniteChainBoundaryUT(successor.StartUT);
        }

        private static Recording FindMissingContiguousChainPredecessor(
            RecordingTree tree,
            Recording successor,
            HashSet<string> reservedPredecessorIds)
        {
            Recording best = null;
            double bestGap = double.MaxValue;
            // Note on outer-loop determinism: tree.Recordings is a Dictionary
            // and its enumeration order is implementation-defined. With the
            // gate's ChainBranch==0 / ChainIndex==1 / non-zero VesselPersistentId
            // requirements, real-world chains have at most one matching
            // successor per (vesselPid, branch) pair, so successor processing
            // order is effectively unique. reservedPredecessorIds prevents
            // a single predecessor being claimed by multiple successors in
            // any pathological multi-successor case.
            foreach (var candidateKvp in tree.Recordings)
            {
                Recording candidate = candidateKvp.Value;
                if (candidate == null || ReferenceEquals(candidate, successor))
                    continue;
                if (string.IsNullOrEmpty(candidate.RecordingId))
                    continue;
                if (reservedPredecessorIds != null
                    && reservedPredecessorIds.Contains(candidate.RecordingId))
                    continue;
                if (!string.IsNullOrEmpty(candidate.ChainId) || candidate.ChainIndex >= 0)
                    continue;
                if (candidate.VesselPersistentId == 0
                    || candidate.VesselPersistentId != successor.VesselPersistentId)
                    continue;
                if (!SameTreeForChainRepair(candidate, successor, tree))
                    continue;
                // Strict TreeOrder gate: both sides must be set (>= 0) and
                // candidate must come strictly before successor. The
                // successor-side check is in
                // IsRepairableMissingChainPredecessorSuccessor; this is the
                // candidate-side counterpart.
                if (candidate.TreeOrder < 0 || candidate.TreeOrder >= successor.TreeOrder)
                    continue;

                double candidateEndUT = candidate.EndUT;
                if (!IsFiniteChainBoundaryUT(candidateEndUT))
                    continue;

                double gap = Math.Abs(candidateEndUT - successor.StartUT);
                if (gap > ChainPredecessorRepairUTEpsilon)
                    continue;

                if (best == null
                    || CompareChainPredecessorCandidates(candidate, gap, best, bestGap) < 0)
                {
                    best = candidate;
                    bestGap = gap;
                }
            }

            return best;
        }

        private static int CompareChainPredecessorCandidates(
            Recording a, double aGap,
            Recording b, double bGap)
        {
            // Smaller boundary-UT gap wins.
            int gapCmp = aGap.CompareTo(bGap);
            if (gapCmp != 0)
                return gapCmp;
            // Tie on gap: lower TreeOrder wins. Both sides have TreeOrder >= 0
            // by the find-loop's strict gate, so no unset-ordering coercion
            // is needed here.
            int orderCmp = a.TreeOrder.CompareTo(b.TreeOrder);
            if (orderCmp != 0)
                return orderCmp;
            // Final tie-break: ordinal RecordingId (always non-empty here —
            // the find-loop guard rejects empty ids).
            return string.CompareOrdinal(a.RecordingId, b.RecordingId);
        }

        private static bool SameTreeForChainRepair(
            Recording candidate, Recording successor, RecordingTree tree)
        {
            // Fail-closed when the tree has no Id: treating two un-treed
            // recordings as same-tree would be a false positive in repair
            // gating. In practice every loaded tree has a non-empty Id by
            // construction; this guard exists so a future code path that
            // somehow constructs a tree without one cannot accidentally
            // pull untreed orphan recordings into a chain.
            string treeId = tree != null ? tree.Id : null;
            if (string.IsNullOrEmpty(treeId))
                return false;
            return string.Equals(candidate.TreeId, treeId, StringComparison.Ordinal)
                && string.Equals(successor.TreeId, treeId, StringComparison.Ordinal);
        }

        private static bool IsFiniteChainBoundaryUT(double ut)
        {
            return !double.IsNaN(ut) && !double.IsInfinity(ut);
        }

        /// <summary>
        /// Bug #601: Re-Fly load preserves post-RP merge tree mutations.
        ///
        /// <para>
        /// The Rewind Point's frozen <c>.sfs</c> snapshots the recording tree at the
        /// moment the RP was authored. If <c>RecordingOptimizer.SplitAtSection</c>
        /// (or any other tree-shape mutation) ran AFTER RP creation but BEFORE the
        /// player invoked Re-Fly, the in-memory <see cref="RecordingStore.CommittedTrees"/>
        /// has post-mutation recording IDs (and updated BranchPoint parent refs)
        /// that the loaded RP <c>.sfs</c> does NOT know about. Their <c>.prec</c>
        /// sidecars remain on disk but are orphaned because the loaded tree's
        /// <c>RECORDING_TREE</c> ConfigNode doesn't list them.
        /// </para>
        ///
        /// <para>
        /// This helper splices any recording present in the in-memory committed
        /// tree but missing from the loaded tree into the loaded tree as a
        /// deep-cloned, files-dirty copy, so the next <c>OnSave</c> rewrites the
        /// <c>.sfs</c> + <c>.prec</c> with fresh sidecar epochs and the correct
        /// merged shape. For recordings whose ID exists in BOTH the loaded and
        /// committed trees the helper additionally REFRESHES the structural
        /// fields of the loaded copy from the committed copy, since
        /// <c>SplitAtSection</c> mutates the original recording in place — it
        /// truncates the trajectory, moves the terminal payload to the new second
        /// half, and reassigns the original recording's <c>ChildBranchPointId</c>
        /// to the second half. Without that refresh, the loaded copy would keep
        /// the pre-split full trajectory + the old child link while the committed
        /// BP's parent list named the new second half — a referential mismatch
        /// (P1 review of PR #575). The <paramref name="activeRecordingId"/>, when
        /// supplied, identifies the recording the recorder will rebind to once
        /// <c>onFlightReady</c> fires; that recording still gets the structural
        /// refresh (it is precisely the one most likely to be the stale post-split
        /// first half — the splice runs BEFORE recorder rebind, so there is no
        /// in-flight recorder state to lose), but the refresh runs in
        /// recorder-state-preserving mode so transient flags
        /// <see cref="Recording.FilesDirty"/>,
        /// <see cref="Recording.SidecarLoadFailed"/>,
        /// <see cref="Recording.SidecarLoadFailureReason"/>,
        /// <see cref="Recording.ContinuationBoundaryIndex"/>,
        /// <see cref="Recording.PreContinuationVesselSnapshot"/>, and
        /// <see cref="Recording.PreContinuationGhostSnapshot"/> are NOT clobbered.
        /// The committed tree never carries those transient flags (DeepClone +
        /// the [NonSerialized] attribute strip them on copy), so non-active
        /// recordings cannot lose live state by being refreshed; the
        /// preserve-mode is only needed when subsequent code paths between this
        /// splice and the recorder's first sample have set those flags on the
        /// active recording (e.g. the load-time hydration mitigation may set
        /// <see cref="Recording.SidecarLoadFailed"/>). BranchPoints follow the
        /// same rule: any committed-tree-only BP is cloned in, and any loaded
        /// BP whose Id matches a committed BP gets its
        /// <c>ParentRecordingIds</c> / <c>ChildRecordingIds</c> overwritten
        /// from the committed copy (the post-merge truth).
        /// </para>
        ///
        /// <para>
        /// MUST be called before <see cref="RecordingStore.RemoveCommittedTreeById"/>,
        /// otherwise the in-memory committed copy (the splice source) is gone.
        /// Returns the number of recordings spliced. Always logs a structured
        /// <see cref="ParsekLog.Info"/> line so the decision is auditable even when
        /// the splice count is zero.
        /// </para>
        /// </summary>
        internal static int SpliceMissingCommittedRecordingsIntoLoadedTree(
            RecordingTree loadedTree,
            string activeRecordingId = null)
        {
            if (loadedTree == null || string.IsNullOrEmpty(loadedTree.Id))
                return 0;

            RecordingTree committedTree = FindCommittedTreeById(loadedTree.Id, exclude: loadedTree);
            if (committedTree == null)
            {
                ParsekLog.Verbose("Scenario",
                    $"SpliceMissingCommittedRecordings: tree id={loadedTree.Id} has no in-memory " +
                    $"committed counterpart — nothing to splice");
                return 0;
            }

            int loadedBefore = loadedTree.Recordings != null ? loadedTree.Recordings.Count : 0;
            int committedCount = committedTree.Recordings != null ? committedTree.Recordings.Count : 0;

            int splicedRecordings = 0;
            int refreshedRecordings = 0;
            int refreshedRecordingsFull = 0;
            int refreshedRecordingsRecorderStatePreserved = 0;
            int splicedBranchPoints = 0;
            int updatedBranchPoints = 0;
            var splicedRecordingIds = new List<string>();
            var refreshedRecordingIds = new List<string>();

            if (committedTree.Recordings != null && committedTree.Recordings.Count > 0)
            {
                foreach (var kvp in committedTree.Recordings)
                {
                    string recId = kvp.Key;
                    Recording committedRec = kvp.Value;
                    if (string.IsNullOrEmpty(recId) || committedRec == null)
                        continue;

                    Recording loadedRec = null;
                    bool loadedHasId = loadedTree.Recordings != null
                        && loadedTree.Recordings.TryGetValue(recId, out loadedRec)
                        && loadedRec != null;

                    if (!loadedHasId)
                    {
                        Recording clone = Recording.DeepClone(committedRec);
                        RecordingStore.ClearSidecarLoadFailure(clone);
                        // Mark dirty so the next OnSave rewrites the .sfs with the
                        // spliced shape AND advances the .prec sidecar epoch in
                        // lockstep — otherwise the committed-but-not-loaded recording
                        // would resurface as a "stale-sidecar-epoch" warning on a
                        // future scene reload (bug #270's mismatch detector).
                        clone.MarkFilesDirty();
                        loadedTree.AddOrReplaceRecording(clone);
                        splicedRecordings++;
                        splicedRecordingIds.Add(recId);
                        continue;
                    }

                    // Same-ID refresh path (P1 review of PR #575 + follow-up).
                    // The committed copy is the post-merge truth (post-
                    // SplitAtSection: truncated trajectory, moved terminal
                    // payload, reassigned ChildBranchPointId). The loaded copy
                    // is the pre-merge .sfs snapshot (full trajectory, original
                    // child link). Without refreshing, the loaded copy would
                    // internally disagree with the committed BP parent lists
                    // that the BP loop below overwrites onto the loaded BPs
                    // (e.g. parent BP's ParentRecordingIds names the new exo
                    // half but the original recording's ChildBranchPointId
                    // still points at the parent BP).
                    //
                    // The active recording is NOT skipped any more (the initial
                    // PR #575 follow-up did skip it — the reviewer rejected
                    // that because the active recording is precisely the one
                    // most likely to be a stale post-split first half kept by
                    // the RP's frozen .sfs). At splice time the recorder has
                    // not yet bound to the active recording — TryRestoreActiveTreeNode
                    // runs in OnLoad, the splice runs immediately after sidecar
                    // hydration, then the tree is stashed as pending-Limbo and
                    // the recorder rebind only fires on the deferred onFlightReady
                    // pass. There is therefore no in-flight recorder-owned
                    // payload state in the active recording to lose. The
                    // structural refresh always runs; the active id is forwarded
                    // into the helper so it can switch to recorder-state-
                    // preserving mode for the small set of [NonSerialized]
                    // flags that load-time mitigation paths may have already
                    // set on the active recording (FilesDirty / SidecarLoadFailed /
                    // SidecarLoadFailureReason / ContinuationBoundaryIndex /
                    // PreContinuationVesselSnapshot / PreContinuationGhostSnapshot /
                    // PreReFlyAnchor* snapshots).
                    bool isActive = !string.IsNullOrEmpty(activeRecordingId)
                        && string.Equals(recId, activeRecordingId, StringComparison.Ordinal);

                    if (RefreshLoadedRecordingFromCommittedSplit(
                            loadedRec, committedRec, preserveRecorderOwnedState: isActive))
                    {
                        refreshedRecordings++;
                        refreshedRecordingIds.Add(recId);
                        if (isActive)
                            refreshedRecordingsRecorderStatePreserved++;
                        else
                            refreshedRecordingsFull++;
                    }
                }
            }

            if (committedTree.BranchPoints != null && committedTree.BranchPoints.Count > 0)
            {
                if (loadedTree.BranchPoints == null)
                    loadedTree.BranchPoints = new List<BranchPoint>();

                var loadedBpIndex = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < loadedTree.BranchPoints.Count; i++)
                {
                    BranchPoint loadedBp = loadedTree.BranchPoints[i];
                    if (loadedBp != null && !string.IsNullOrEmpty(loadedBp.Id))
                        loadedBpIndex[loadedBp.Id] = i;
                }

                for (int b = 0; b < committedTree.BranchPoints.Count; b++)
                {
                    BranchPoint committedBp = committedTree.BranchPoints[b];
                    if (committedBp == null || string.IsNullOrEmpty(committedBp.Id))
                        continue;

                    if (!loadedBpIndex.TryGetValue(committedBp.Id, out int existingIdx))
                    {
                        // Brand-new BranchPoint authored after the RP snapshot.
                        BranchPoint clonedBp = CloneBranchPoint(committedBp);
                        loadedTree.BranchPoints.Add(clonedBp);
                        splicedBranchPoints++;
                        continue;
                    }

                    // Existing BP — overwrite parent/child id lists if the post-merge
                    // committed version diverges from the .sfs version. This is what
                    // catches the "Split: updated BranchPoint ParentRecordingIds:
                    // X -> Y" case where the parent BP id is unchanged but its
                    // ParentRecordingIds was rewritten to point at the new split
                    // half. Copying the lists also covers any future BP-edit path
                    // that doesn't change the BP id.
                    BranchPoint loadedBp = loadedTree.BranchPoints[existingIdx];
                    if (loadedBp == null) continue;
                    bool listsDiverged =
                        !StringListsEqual(loadedBp.ParentRecordingIds, committedBp.ParentRecordingIds)
                        || !StringListsEqual(loadedBp.ChildRecordingIds, committedBp.ChildRecordingIds);
                    if (listsDiverged)
                    {
                        loadedBp.ParentRecordingIds = committedBp.ParentRecordingIds != null
                            ? new List<string>(committedBp.ParentRecordingIds)
                            : new List<string>();
                        loadedBp.ChildRecordingIds = committedBp.ChildRecordingIds != null
                            ? new List<string>(committedBp.ChildRecordingIds)
                            : new List<string>();
                        updatedBranchPoints++;
                    }
                }
            }

            int repairedChainPredecessors = RepairMissingContiguousChainPredecessors(
                loadedTree, "SpliceMissingCommittedRecordings");

            int loadedAfter = loadedTree.Recordings != null ? loadedTree.Recordings.Count : 0;

            if (splicedRecordings > 0
                || refreshedRecordings > 0
                || repairedChainPredecessors > 0
                || splicedBranchPoints > 0
                || updatedBranchPoints > 0)
            {
                loadedTree.RebuildBackgroundMap();
                ParsekLog.Info("Scenario",
                    $"SpliceMissingCommittedRecordings: tree '{loadedTree.TreeName}' (id={loadedTree.Id}) " +
                    $"loadedBefore={loadedBefore} committed={committedCount} after={loadedAfter} " +
                    $"splicedRecordings={splicedRecordings} " +
                    $"refreshedRecordings={refreshedRecordings} " +
                    $"(full={refreshedRecordingsFull} " +
                    $"recorderStatePreserved={refreshedRecordingsRecorderStatePreserved}) " +
                    $"repairedChainPredecessors={repairedChainPredecessors} " +
                    $"splicedBranchPoints={splicedBranchPoints} " +
                    $"updatedBranchPoints={updatedBranchPoints} " +
                    $"source=committed-tree-in-memory");
                if (splicedRecordings > 0 || refreshedRecordings > 0)
                {
                    ParsekLog.Verbose("Scenario",
                        $"SpliceMissingCommittedRecordings: " +
                        $"splicedIds=[{string.Join(",", splicedRecordingIds)}] " +
                        $"refreshedIds=[{string.Join(",", refreshedRecordingIds)}]");
                }
            }
            else
            {
                ParsekLog.Verbose("Scenario",
                    $"SpliceMissingCommittedRecordings: tree '{loadedTree.TreeName}' (id={loadedTree.Id}) " +
                    $"loaded={loadedBefore} committed={committedCount} — already in sync, nothing to splice");
            }

            return splicedRecordings;
        }

        /// <summary>
        /// Refreshes the structural fields of a same-ID loaded recording from
        /// its committed-tree counterpart (the post-merge truth) when
        /// <c>SplitAtSection</c> has mutated the recording in place after the
        /// RP <c>.sfs</c> was authored. Mirrors the field-set pattern of
        /// <see cref="RestoreCommittedSidecarPayloadIntoActiveTreeRecording"/>
        /// — overwrites trajectory + terminal-state + child-link fields while
        /// preserving the loaded copy's identity (RecordingId, TreeId,
        /// TreeOrder, MergeState, CreatingSessionId, supersede/provisional refs).
        /// The loaded recording is marked <c>FilesDirty</c> so the next
        /// <c>OnSave</c> rewrites the <c>.sfs</c> + <c>.prec</c> with the
        /// post-split shape and a fresh sidecar epoch.
        ///
        /// <para>
        /// When <paramref name="preserveRecorderOwnedState"/> is <c>true</c>
        /// (passed for the active recording — see the helper's caller for the
        /// full load-order rationale) the refresh additionally preserves the
        /// set of <c>[NonSerialized]</c> flags any load-time mitigation may
        /// have already set on the loaded copy: <c>FilesDirty</c>,
        /// <c>SidecarLoadFailed</c>, <c>SidecarLoadFailureReason</c>,
        /// <c>ContinuationBoundaryIndex</c>, <c>PreContinuationVesselSnapshot</c>,
        /// <c>PreContinuationGhostSnapshot</c>, and <c>PreReFlyAnchor*</c>
        /// snapshots. The preserve-mode exists because
        /// the structural overwrite happens to land on the same recording the
        /// recorder will later rebind to, and downstream save paths look at
        /// <c>FilesDirty</c> / <c>SidecarLoadFailed</c> to decide whether to
        /// rewrite the <c>.prec</c> or repair from a donor — losing those
        /// flags would silently disable those paths for the active recording.
        /// </para>
        /// </summary>
        /// <returns>
        /// <c>true</c> if the committed copy diverged from the loaded copy in a
        /// split-relevant structural field and a refresh was applied;
        /// <c>false</c> if the loaded copy already matched (no-op).
        /// </returns>
        private static bool RefreshLoadedRecordingFromCommittedSplit(
            Recording loadedRec,
            Recording committedRec,
            bool preserveRecorderOwnedState)
        {
            if (loadedRec == null || committedRec == null)
                return false;

            // Detect divergence on split-relevant fields. SplitAtSection
            // mutates point count / last-point UT (truncation), TerminalStateValue
            // (moved to second half — first half ends up null), TerminalOrbitBody
            // (cleared on first half), OrbitSegments count, TrackSections count,
            // ChildBranchPointId (reassigned to second half), and EndBiome (cleared
            // and recomputed). If none of these diverge the loaded copy already
            // matches the committed shape and the refresh is a no-op.
            bool diverged =
                CountOrNull(loadedRec.Points) != CountOrNull(committedRec.Points)
                || CountOrNull(loadedRec.OrbitSegments) != CountOrNull(committedRec.OrbitSegments)
                || CountOrNull(loadedRec.TrackSections) != CountOrNull(committedRec.TrackSections)
                || !string.Equals(
                    loadedRec.ChildBranchPointId,
                    committedRec.ChildBranchPointId,
                    StringComparison.Ordinal)
                || !Nullable.Equals(loadedRec.TerminalStateValue, committedRec.TerminalStateValue)
                || !string.Equals(
                    loadedRec.TerminalOrbitBody,
                    committedRec.TerminalOrbitBody,
                    StringComparison.Ordinal)
                || LastPointUTOrNaN(loadedRec) != LastPointUTOrNaN(committedRec)
                // MergeState is the open/closed source of truth after
                // collapse-seal-into-mergestate. A sibling slot can be promoted
                // to CommittedProvisional (open) AFTER RP creation, while the
                // RP-frozen loaded snapshot still has it Immutable; that
                // divergence alone must trigger the refresh, or the open slot
                // would silently seal when a different slot's re-fly re-commits.
                // A NotCommitted loaded copy is a LIVE in-flight recording and is
                // never overwritten (see the mergeState pick below), so it does
                // not count as a MergeState divergence here.
                || (loadedRec.MergeState != committedRec.MergeState
                    && loadedRec.MergeState != MergeState.NotCommitted);

            if (!diverged)
                return false;

            // Preserve identity + transient flight state owned by the loaded
            // recording. These fields tag the recording within its tree shape
            // and are NOT what SplitAtSection rewrites; clobbering them would
            // re-parent the recording or lose mutations made between load and
            // splice. Mirror RestoreCommittedSidecarPayloadIntoActiveTreeRecording.
            string recordingId = loadedRec.RecordingId;
            string treeId = loadedRec.TreeId;
            int treeOrder = loadedRec.TreeOrder;
            // MergeState is open/closed state, NOT identity: for a CONCLUDED
            // loaded copy (CommittedProvisional / Immutable) take the committed
            // (post-merge truth) value, not the stale RP-frozen loaded one, so
            // an open sibling slot (e.g. a capsule promoted to CommittedProvisional
            // after RP creation) is not silently sealed when an unrelated slot's
            // re-fly re-commits the tree. But PRESERVE a NotCommitted loaded copy:
            // that is a live in-flight recording whose committed copy would
            // wrongly seal/conclude it.
            MergeState mergeState = loadedRec.MergeState == MergeState.NotCommitted
                ? loadedRec.MergeState
                : committedRec.MergeState;
            string creatingSessionId = loadedRec.CreatingSessionId;
            string supersedeTargetId = loadedRec.SupersedeTargetId;
            string provisionalForRpId = loadedRec.ProvisionalForRpId;
            string switchSegmentSessionId = loadedRec.SwitchSegmentSessionId;

            // Recorder-owned [NonSerialized] flags. Snapshotted before the
            // overwrite so the active-refresh path can put them back. The
            // committed copy never carries them (DeepClone resets the flags
            // to their defaults), so a full refresh ends with them all
            // cleared / defaulted; preserve-mode reapplies the snapshot.
            // Audit anchor: the [NonSerialized] flag set in Recording.cs is
            // {FilesDirty, SidecarLoadFailed, SidecarLoadFailureReason,
            // ContinuationBoundaryIndex, PreContinuationVesselSnapshot,
            // PreContinuationGhostSnapshot, PreReFlyAnchor*}. Add to this
            // preserve-list when any new [NonSerialized] flag tracking
            // per-session live state is added to Recording.
            bool savedFilesDirty = loadedRec.FilesDirty;
            bool savedSidecarLoadFailed = loadedRec.SidecarLoadFailed;
            string savedSidecarLoadFailureReason = loadedRec.SidecarLoadFailureReason;
            int savedContinuationBoundaryIndex = loadedRec.ContinuationBoundaryIndex;
            ConfigNode savedPreContinuationVesselSnapshot = loadedRec.PreContinuationVesselSnapshot;
            ConfigNode savedPreContinuationGhostSnapshot = loadedRec.PreContinuationGhostSnapshot;
            string savedPreReFlyAnchorSessionId = loadedRec.PreReFlyAnchorSessionId;
            List<TrajectoryPoint> savedPreReFlyAnchorPoints = loadedRec.PreReFlyAnchorPoints;
            List<OrbitSegment> savedPreReFlyAnchorOrbitSegments = loadedRec.PreReFlyAnchorOrbitSegments;
            List<TrackSection> savedPreReFlyAnchorTrackSections = loadedRec.PreReFlyAnchorTrackSections;

            Recording sourceClone = Recording.DeepClone(committedRec);
            loadedRec.ApplyPersistenceArtifactsFrom(sourceClone);
            loadedRec.CopyStartLocationFrom(sourceClone);
            loadedRec.VesselName = sourceClone.VesselName;
            loadedRec.Points = sourceClone.Points ?? new List<TrajectoryPoint>();
            loadedRec.OrbitSegments = sourceClone.OrbitSegments ?? new List<OrbitSegment>();
            loadedRec.PartEvents = sourceClone.PartEvents ?? new List<PartEvent>();
            loadedRec.FlagEvents = sourceClone.FlagEvents ?? new List<FlagEvent>();
            loadedRec.SegmentEvents = sourceClone.SegmentEvents ?? new List<SegmentEvent>();
            loadedRec.TrackSections = sourceClone.TrackSections ?? new List<TrackSection>();
            loadedRec.Controllers = sourceClone.Controllers;
            loadedRec.CrewEndStates = sourceClone.CrewEndStates != null
                ? new Dictionary<string, KerbalEndState>(sourceClone.CrewEndStates)
                : null;
            loadedRec.SpawnSuppressedByRewind = sourceClone.SpawnSuppressedByRewind;
            loadedRec.SpawnSuppressedByRewindReason = sourceClone.SpawnSuppressedByRewindReason;
            loadedRec.SpawnSuppressedByRewindUT = sourceClone.SpawnSuppressedByRewindUT;
            loadedRec.SidecarEpoch = sourceClone.SidecarEpoch;
            RecordingStore.ClearSidecarLoadFailure(loadedRec);
            // Mark dirty so the next OnSave rewrites the .sfs with the refreshed
            // shape + advances the .prec sidecar epoch in lockstep (same
            // contract as the missing-id splice path above).
            loadedRec.MarkFilesDirty();

            loadedRec.RecordingId = recordingId;
            loadedRec.TreeId = treeId;
            loadedRec.TreeOrder = treeOrder;
            loadedRec.MergeState = mergeState;
            loadedRec.CreatingSessionId = creatingSessionId;
            loadedRec.SupersedeTargetId = supersedeTargetId;
            loadedRec.ProvisionalForRpId = provisionalForRpId;
            loadedRec.SwitchSegmentSessionId = switchSegmentSessionId;

            if (preserveRecorderOwnedState)
            {
                // Restore the recorder-owned flag snapshot. FilesDirty is OR-ed
                // with the freshly-marked-dirty value because either being
                // true means the next OnSave must rewrite the sidecar — we
                // don't want a previously-dirty state to be downgraded just
                // because the structural overwrite is canonical-by-itself.
                loadedRec.FilesDirty = savedFilesDirty || loadedRec.FilesDirty;
                loadedRec.SidecarLoadFailed = savedSidecarLoadFailed;
                loadedRec.SidecarLoadFailureReason = savedSidecarLoadFailureReason;
                loadedRec.ContinuationBoundaryIndex = savedContinuationBoundaryIndex;
                loadedRec.PreContinuationVesselSnapshot = savedPreContinuationVesselSnapshot;
                loadedRec.PreContinuationGhostSnapshot = savedPreContinuationGhostSnapshot;
                loadedRec.PreReFlyAnchorSessionId = savedPreReFlyAnchorSessionId;
                loadedRec.PreReFlyAnchorPoints = savedPreReFlyAnchorPoints;
                loadedRec.PreReFlyAnchorOrbitSegments = savedPreReFlyAnchorOrbitSegments;
                loadedRec.PreReFlyAnchorTrackSections = savedPreReFlyAnchorTrackSections;
            }

            return true;
        }

        private static int CountOrNull<T>(List<T> list) => list != null ? list.Count : 0;

        private static double LastPointUTOrNaN(Recording rec)
        {
            if (rec == null || rec.Points == null || rec.Points.Count == 0)
                return double.NaN;
            return rec.Points[rec.Points.Count - 1].ut;
        }

        private static BranchPoint CloneBranchPoint(BranchPoint source)
        {
            if (source == null) return null;
            var clone = new BranchPoint
            {
                Id = source.Id,
                UT = source.UT,
                Type = source.Type,
                ParentRecordingIds = source.ParentRecordingIds != null
                    ? new List<string>(source.ParentRecordingIds)
                    : new List<string>(),
                ChildRecordingIds = source.ChildRecordingIds != null
                    ? new List<string>(source.ChildRecordingIds)
                    : new List<string>(),
                SplitCause = source.SplitCause,
                DecouplerPartId = source.DecouplerPartId,
                BreakupCause = source.BreakupCause,
                BreakupDuration = source.BreakupDuration,
                DebrisCount = source.DebrisCount,
                CoalesceWindow = source.CoalesceWindow,
                MergeCause = source.MergeCause,
                TargetVesselPersistentId = source.TargetVesselPersistentId,
                TerminalCause = source.TerminalCause,
                RewindPointId = source.RewindPointId,
            };
            return clone;
        }

        private static bool StringListsEqual(List<string> a, List<string> b)
        {
            if (ReferenceEquals(a, b)) return true;
            int aCount = a != null ? a.Count : 0;
            int bCount = b != null ? b.Count : 0;
            if (aCount != bCount) return false;
            for (int i = 0; i < aCount; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private static RecordingTree FindCommittedTreeById(string treeId, RecordingTree exclude = null)
        {
            if (string.IsNullOrEmpty(treeId))
                return null;

            var trees = RecordingStore.CommittedTrees;
            if (trees == null)
                return null;

            for (int i = 0; i < trees.Count; i++)
            {
                RecordingTree tree = trees[i];
                if (tree == null || ReferenceEquals(tree, exclude))
                    continue;
                if (string.Equals(tree.Id, treeId, StringComparison.Ordinal))
                    return tree;
            }

            return null;
        }

        private static bool ShouldRestoreHydrationFailureFromCommittedRecording(
            Recording loadedRec,
            Recording sourceRec,
            string activeRecordingId)
        {
            if (loadedRec == null || sourceRec == null)
                return false;

            if (!string.IsNullOrEmpty(activeRecordingId)
                && string.Equals(
                    loadedRec.RecordingId,
                    activeRecordingId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!HasTrajectoryPayload(sourceRec))
                return false;

            // Repair must be scoped to records that explicitly failed to
            // hydrate from sidecar (PR #572 P2 review). Without this gate, any
            // dirty active-tree record with empty trajectory lists would match
            // — including legitimate metadata-only / snapshot-only edits where
            // the trajectory hasn't been seeded yet — and the committed copy
            // would silently overwrite the in-memory mutation. Snapshot-only
            // hydration failures route through the pending-tree salvage path
            // (`TryRestoreSnapshotStateFromPendingRecording`) and are excluded
            // here so that snapshot/trajectory recoveries cannot cross-pollute.
            return loadedRec.SidecarLoadFailed
                && !IsSnapshotHydrationFailure(loadedRec.SidecarLoadFailureReason)
                && IsTrajectoryPayloadEmpty(loadedRec);
        }

        private static void RestoreCommittedSidecarPayloadIntoActiveTreeRecording(
            Recording target,
            Recording source)
        {
            if (target == null || source == null)
                return;

            string recordingId = target.RecordingId;
            string treeId = target.TreeId;
            int treeOrder = target.TreeOrder;
            // MergeState is open/closed state, NOT identity. Same rule as
            // RefreshLoadedRecordingFromCommittedSplit (collapse-seal-into-mergestate):
            // for a CONCLUDED target (CommittedProvisional / Immutable) take the
            // committed (source) post-merge truth so a stale Immutable does not
            // seal an open sibling slot; but PRESERVE a NotCommitted target,
            // which is a live in-flight recording the committed copy must not
            // seal.
            MergeState mergeState = target.MergeState == MergeState.NotCommitted
                ? target.MergeState
                : source.MergeState;
            string creatingSessionId = target.CreatingSessionId;
            string supersedeTargetId = target.SupersedeTargetId;
            string provisionalForRpId = target.ProvisionalForRpId;
            string switchSegmentSessionId = target.SwitchSegmentSessionId;

            Recording sourceClone = Recording.DeepClone(source);
            target.ApplyPersistenceArtifactsFrom(sourceClone);
            target.CopyStartLocationFrom(sourceClone);
            target.VesselName = sourceClone.VesselName;
            target.Points = sourceClone.Points ?? new List<TrajectoryPoint>();
            target.OrbitSegments = sourceClone.OrbitSegments ?? new List<OrbitSegment>();
            target.PartEvents = sourceClone.PartEvents ?? new List<PartEvent>();
            target.FlagEvents = sourceClone.FlagEvents ?? new List<FlagEvent>();
            target.SegmentEvents = sourceClone.SegmentEvents ?? new List<SegmentEvent>();
            target.TrackSections = sourceClone.TrackSections ?? new List<TrackSection>();
            target.Controllers = sourceClone.Controllers;
            // PR #572 P2 review follow-up: ApplyPersistenceArtifactsFrom copies
            // `CrewEndStatesResolved` but NOT the `CrewEndStates` dictionary
            // itself; without this explicit copy, a source with populated crew
            // end-states would repair the target into `resolved=true` with a
            // null/stale dict, and the safety-net population path skips
            // already-resolved records on the next save (loss persisted).
            // Mirror DeepClone's CrewEndStates copy.
            target.CrewEndStates = sourceClone.CrewEndStates != null
                ? new Dictionary<string, KerbalEndState>(sourceClone.CrewEndStates)
                : null;
            // PR #572 P2 review follow-up: SpawnSuppressedByRewind / Reason / UT
            // are persisted (#573 / #589 active/source recording protection)
            // but ApplyPersistenceArtifactsFrom doesn't copy them. Mirror
            // DeepClone so the repair preserves rewind-strip-protection scope
            // alongside the trajectory data.
            target.SpawnSuppressedByRewind = sourceClone.SpawnSuppressedByRewind;
            target.SpawnSuppressedByRewindReason = sourceClone.SpawnSuppressedByRewindReason;
            target.SpawnSuppressedByRewindUT = sourceClone.SpawnSuppressedByRewindUT;
            target.FilesDirty = false;
            target.SidecarEpoch = sourceClone.SidecarEpoch;
            RecordingStore.ClearSidecarLoadFailure(target);

            target.RecordingId = recordingId;
            target.TreeId = treeId;
            target.TreeOrder = treeOrder;
            target.MergeState = mergeState;
            target.CreatingSessionId = creatingSessionId;
            target.SupersedeTargetId = supersedeTargetId;
            target.ProvisionalForRpId = provisionalForRpId;
            target.SwitchSegmentSessionId = switchSegmentSessionId;

            // PR #572 second-order data-loss companion: the trajectory just
            // copied over came from a committed recording that, in the
            // Re-Fly in-place-continuation case, was committed mid-flight
            // with TerminalStateValue=none. The immediately-following
            // FinalizeTreeRecordings on scene exit would otherwise treat
            // `vessel pid=… not found on scene exit` + last-point altitude<50m
            // as evidence of a fresh landing and stamp Landed/Splashed onto
            // a recording that actually represents a stripped Re-Fly origin
            // vessel. The transient marker tells the finalize path to skip
            // the surface inference for this recording on this frame —
            // [NonSerialized] so a fresh session never sees it.
            target.RestoredFromCommittedTreeThisFrame = true;
        }

        private static bool ShouldSkipActiveTreeEmptySidecarOverwrite(Recording rec)
        {
            return rec != null
                && rec.SidecarLoadFailed
                && !IsSnapshotHydrationFailure(rec.SidecarLoadFailureReason)
                && IsTrajectoryPayloadEmpty(rec);
        }

        private static bool HasTrajectoryPayload(Recording rec)
        {
            if (rec == null)
                return false;

            return (rec.Points != null && rec.Points.Count > 0)
                || (rec.OrbitSegments != null && rec.OrbitSegments.Count > 0)
                || (rec.TrackSections != null && rec.TrackSections.Count > 0)
                || (rec.PartEvents != null && rec.PartEvents.Count > 0)
                || (rec.FlagEvents != null && rec.FlagEvents.Count > 0)
                || (rec.SegmentEvents != null && rec.SegmentEvents.Count > 0);
        }

        private static bool IsTrajectoryPayloadEmpty(Recording rec)
        {
            return !HasTrajectoryPayload(rec);
        }

        private static bool TryRestoreSnapshotStateFromPendingRecording(Recording loadedRec, Recording pendingRec)
        {
            if (loadedRec == null || pendingRec == null)
                return false;

            if (!IsSnapshotHydrationFailure(loadedRec.SidecarLoadFailureReason))
                return false;

            bool restoredAny = false;
            if (loadedRec.VesselSnapshot == null)
            {
                if (pendingRec.VesselSnapshot != null)
                {
                    loadedRec.VesselSnapshot = pendingRec.VesselSnapshot.CreateCopy();
                    restoredAny = true;
                }
                else if (pendingRec.GhostSnapshotMode == GhostSnapshotMode.AliasVessel
                    && pendingRec.GhostVisualSnapshot != null)
                {
                    loadedRec.VesselSnapshot = pendingRec.GhostVisualSnapshot.CreateCopy();
                    restoredAny = true;
                }
            }

            GhostSnapshotMode restoredMode = pendingRec.GhostSnapshotMode != GhostSnapshotMode.Unspecified
                ? pendingRec.GhostSnapshotMode
                : loadedRec.GhostSnapshotMode;

            if (loadedRec.GhostVisualSnapshot == null)
            {
                if (pendingRec.GhostVisualSnapshot != null)
                {
                    loadedRec.GhostVisualSnapshot = pendingRec.GhostVisualSnapshot.CreateCopy();
                    restoredAny = true;
                }
                else if (restoredMode == GhostSnapshotMode.AliasVessel)
                {
                    ConfigNode aliasSource = loadedRec.VesselSnapshot ?? pendingRec.VesselSnapshot;
                    if (aliasSource != null)
                    {
                        loadedRec.GhostVisualSnapshot = aliasSource.CreateCopy();
                        restoredAny = true;
                    }
                }
            }

            if (!restoredAny)
                return false;

            loadedRec.GhostSnapshotMode = restoredMode;

            if (loadedRec.VesselSnapshot != null
                && loadedRec.GhostSnapshotMode == GhostSnapshotMode.AliasVessel)
            {
                loadedRec.GhostVisualSnapshot = loadedRec.VesselSnapshot.CreateCopy();
            }

            if (!HasCoherentSnapshotState(loadedRec))
                return false;

            RecordingStore.ClearSidecarLoadFailure(loadedRec);
            loadedRec.MarkFilesDirty();
            return true;
        }

        private static bool IsSnapshotHydrationFailure(string reason)
        {
            return reason == "snapshot-vessel-invalid"
                || reason == "snapshot-vessel-unsupported"
                || reason == "snapshot-ghost-invalid"
                || reason == "snapshot-ghost-unsupported";
        }

        private static bool HasCoherentSnapshotState(Recording rec)
        {
            if (rec == null)
                return false;

            GhostSnapshotMode mode = rec.GhostSnapshotMode != GhostSnapshotMode.Unspecified
                ? rec.GhostSnapshotMode
                : RecordingStore.DetermineGhostSnapshotMode(rec);
            if (mode == GhostSnapshotMode.AliasVessel)
            {
                return rec.VesselSnapshot != null
                    && rec.GhostVisualSnapshot != null
                    && RecordingStore.ConfigNodesEquivalent(rec.VesselSnapshot, rec.GhostVisualSnapshot);
            }
            if (mode == GhostSnapshotMode.Separate)
                return rec.GhostVisualSnapshot != null;
            return rec.VesselSnapshot != null || rec.GhostVisualSnapshot != null;
        }
    }
}
