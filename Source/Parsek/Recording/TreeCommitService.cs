using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Tree commit (RecordingStore services plan, service L): commits a recording or a whole
    /// tree to the committed timeline (<see cref="CommitTree"/>, <see cref="CommitPendingTree"/>,
    /// <see cref="CommitRecordingDirect"/>): the session merge, the rewind-provisional merge
    /// states, the staging rewind-point promotion, the tree group assignment, the replace-in-place
    /// of an already committed recording or tree with its live runtime fields preserved, the
    /// active Re-Fly tree union, and the terminal-spawn supersede marks a continuation sets on
    /// older recordings. It changes the committed lists only through the store's primitives
    /// (<see cref="RecordingStore.AddCommittedInternal"/>, <see cref="RecordingStore.ReplaceCommittedAt"/>
    /// and their unbumped batch forms, <see cref="RecordingStore.ReplaceCommittedTreeAt"/>);
    /// <see cref="SessionMerger"/> stays its merge core. No state.
    /// </summary>
    internal static partial class TreeCommitService
    {
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
            RecordingStore.AddCommittedInternal(rec);
            RecordingStore.Log($"[Parsek] Committed recording from {rec.VesselName} " +
                $"({rec.Points.Count} points). Total committed: {RecordingStore.CommittedRecordings.Count}");
            ParsekLog.Verbose("RecordingStore", $"CommitRecordingDirect: {rec.DebugName}");

            // Flush to disk immediately to close the crash window.
            RecordingStore.FlushDirtyCommittedFiles();

            // Capture a game state baseline at each commit (single funnel point)
            GameStateStore.CaptureBaselineIfNeeded();

            // Create a milestone bundling game state events since the previous milestone
            MilestoneStore.CreateMilestone(rec.RecordingId, rec.EndUT);
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
            CommitTreeCore(tree);
            // A commit (a merge, a Re-Fly union swapping older recordings in by reference, a
            // replace commit of a copy-on-write clone) can put back older names on legs a
            // vessel name decision covers; re-apply them (design-vessel-and-mission-names.md 7.4).
            VesselNameStore.ApplyAll("commit-tree");
        }

        private static void CommitTreeCore(RecordingTree tree)
        {

            // First-commit guard data (collapse-seal-into-mergestate plan §4.1):
            // snapshot recording ids already committed AS PART OF A TREE, taken at
            // the LITERAL TOP of CommitTree, BEFORE the union/replace path
            // (TryUnionActiveReFlyTreeIntoCommitted mutates committedTrees[i].Recordings)
            // and BEFORE FinalizeTreeCommit swaps. ApplyRewindProvisionalMergeStates
            // uses this (plus supersede-fork identity) so it never re-derives an
            // already-concluded recording's MergeState. Keyed on committed-TREE
            // membership, NOT the flat committedRecordings list (which is polluted
            // mid-flight by CommitRecordingDirect).
            var alreadyCommittedRecordingIds = CollectCommittedTreeRecordingIds(RecordingStore.CommittedTrees);

            int replaceCommittedTreeIndex = -1;
            for (int i = 0; i < RecordingStore.CommittedTrees.Count; i++)
            {
                if (RecordingStore.CommittedTrees[i].Id == tree.Id)
                {
                    if (ReferenceEquals(RecordingStore.CommittedTrees[i], tree))
                    {
                        RecordingStore.Log($"[Parsek] WARNING: Tree '{tree.Id}' already committed — skipping duplicate");
                        // Scoped-drop only THIS already-committed tree's own pending science
                        // subjects (the helper skips committed ids, so this is a no-op in the
                        // common case), PRESERVING a DIFFERENT live recording's still-
                        // uncommitted tagged science. A blanket PendingScienceSubjects.Clear()
                        // here wiped the WHOLE list, and the post-CommitTree
                        // NotifyLedgerTreeCommitted then ran on an empty list and silently lost
                        // the other recording's science. Mirrors the scoped discard cores
                        // (LedgerOrchestrator.RemovePendingScienceSubjectsForRecordings).
                        LedgerOrchestrator.RemovePendingScienceSubjectsForRecordings(
                            tree.Recordings?.Keys,
                            $"CommitTree duplicate-skip (reference-equal) tree '{tree.Id}'");
                        SceneEntryVesselScope.ClearRewindReplayTargetScope();
                        return;
                    }

                    if (!ShouldReplaceCommittedTree(RecordingStore.CommittedTrees[i], tree, out var replaceReason))
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
                                RecordingStore.CommittedTrees[i], tree, liveMarker,
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
                            tree = RecordingStore.CommittedTrees[i];
                            replaceCommittedTreeIndex = i;
                            break;
                        }

                        RecordingStore.Log($"[Parsek] WARNING: Tree '{tree.Id}' already committed — skipping duplicate");
                        ParsekLog.Verbose("RecordingStore",
                            $"CommitTree: duplicate tree id='{tree.Id}' skipped reason={replaceReason}");
                        // Scoped-drop only the incoming duplicate tree's own pending science
                        // subjects (the helper skips committed ids), PRESERVING a DIFFERENT
                        // live recording's still-uncommitted tagged science. See the
                        // reference-equal branch above: a blanket Clear() here let the
                        // post-CommitTree NotifyLedgerTreeCommitted run on an empty list and
                        // silently drop another recording's science.
                        LedgerOrchestrator.RemovePendingScienceSubjectsForRecordings(
                            tree.Recordings?.Keys,
                            $"CommitTree duplicate-skip (reason={replaceReason}) tree '{tree.Id}'");
                        SceneEntryVesselScope.ClearRewindReplayTargetScope();
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
                    CopyAutoGeneratedGroupNames(RecordingStore.CommittedTrees[i], tree);
                    ParsekLog.Warn("RecordingStore",
                        $"Tree '{tree.Id}' already committed with a different topology; " +
                        $"updating committed tree (oldRecordings={RecordingStore.CommittedTrees[i].Recordings.Count}, " +
                        $"newRecordings={tree.Recordings.Count}, reason={replaceReason}) " +
                        $"preservedRootGroup='{tree.AutoGeneratedRootGroupName ?? "<none>"}'");
                    break;
                }
            }

            ApplySessionMergeToRecordings(tree);
            ApplyRewindProvisionalMergeStates(tree, alreadyCommittedRecordingIds);
            PromoteNormalStagingRewindPoints(tree);
            RecordingStore.AutoGroupTreeRecordings(tree);
            RecordingStore.AdoptOrphanedRecordingsIntoTreeGroup(tree);
            MarkSupersededTerminalSpawnsForContinuedSources(tree);
            FinalizeTreeCommit(tree, replaceCommittedTreeIndex);
            CommittedTreeRestoreAttempt.ClearCommittedTreeRestoreAttemptForTree(tree.Id, "CommitTree accepted restored active tree");
            SceneEntryVesselScope.ClearRewindReplayTargetScope();
        }

        /// <summary>
        /// The ids of every recording already committed AS PART OF A TREE (the first-commit
        /// guard of <see cref="ApplyRewindProvisionalMergeStates"/>): keyed on committed-tree
        /// membership, never the flat committed list. Null trees, trees without recordings and
        /// empty ids are skipped. Pure.
        /// </summary>
        internal static HashSet<string> CollectCommittedTreeRecordingIds(IReadOnlyList<RecordingTree> trees)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int ci = 0; ci < trees.Count; ci++)
            {
                var ctSnapshot = trees[ci];
                if (ctSnapshot?.Recordings == null) continue;
                foreach (var committedId in ctSnapshot.Recordings.Keys)
                    if (!string.IsNullOrEmpty(committedId))
                        ids.Add(committedId);
            }
            return ids;
        }

        /// <summary>
        /// A topology-update commit keeps the committed tree's group identity: the three
        /// auto-generated group names copy from <paramref name="existingTree"/> onto
        /// <paramref name="tree"/>, null included.
        /// </summary>
        internal static void CopyAutoGeneratedGroupNames(RecordingTree existingTree, RecordingTree tree)
        {
            tree.AutoGeneratedRootGroupName = existingTree.AutoGeneratedRootGroupName;
            tree.AutoGeneratedDebrisGroupName = existingTree.AutoGeneratedDebrisGroupName;
            tree.AutoGeneratedCrewGroupName = existingTree.AutoGeneratedCrewGroupName;
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
        /// Test seam: re-runs the slot-driven MergeState promotion + clobber
        /// guard (collapse-seal-into-mergestate) against the already-committed
        /// trees, exactly as a later CommitTree would. Builds the committed-TREE
        /// membership snapshot from the current committed trees so the
        /// first-commit guard treats already-committed recordings as concluded.
        /// </summary>
        internal static void ApplyRewindProvisionalMergeStatesForTesting(RecordingTree tree)
        {
            var snapshot = new HashSet<string>(StringComparer.Ordinal);
            for (int ci = 0; ci < RecordingStore.CommittedTrees.Count; ci++)
            {
                var ct = RecordingStore.CommittedTrees[ci];
                if (ct?.Recordings == null) continue;
                foreach (var id in ct.Recordings.Keys)
                    if (!string.IsNullOrEmpty(id))
                        snapshot.Add(id);
            }
            ApplyRewindProvisionalMergeStates(tree, snapshot);
        }

        /// <summary>
        /// A child under a Rewind Point that qualifies as an unfinished flight
        /// is committed, but its rewind slot stays open until a later successful
        /// re-fly or explicit Seal closes it. Legacy/default recordings are born
        /// Immutable, so stamp that precise shape during the normal tree commit
        /// path. An EVA kerbal never qualifies (Re-Fly is for vessel separations
        /// only), so it is never promoted.
        /// </summary>
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
            var supersedeForkIds = CollectSupersedeForkIds(supersedes);

            // First-commit guard: a recording is "already concluded as canon" (its
            // MergeState must NOT be re-derived) if it was already committed as part of a
            // tree, or it is a supersede fork. Open-UF tips committed mid-flight via
            // CommitRecordingDirect are in neither set on their tree's first commit, so
            // they ARE demoted here.
            var committedSnapshot = alreadyCommittedRecordingIds
                ?? new HashSet<string>(StringComparer.Ordinal);

            int promoted = 0;
            int tipsPromoted = 0;
            List<GameAction> pendingRows = null;
            bool pendingRowsBuilt = false;
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
                if (!pendingRowsBuilt)
                {
                    pendingRows = BuildPendingCommitRowsForPromotion(tree);
                    pendingRowsBuilt = true;
                }
                string qualifyReason;
                bool qualifies;
                using (SupersedeCommit.UsePendingCommitRows(pendingRows))
                    qualifies = UnfinishedFlightClassifier.TryQualify(
                        rec, slot, rp, out qualifyReason, tree);
                if (!qualifies)
                {
                    ParsekLog.Verbose("UnfinishedFlights",
                        $"CommitTree: RP child rec={rec.RecordingId ?? "<no-id>"} " +
                        $"vessel='{rec.VesselName ?? "<unnamed>"}' not promoted reason={qualifyReason} " +
                        $"rp={rp.RewindPointId ?? "<no-rp>"} slot={slotListIndex}");
                    continue;
                }

                // Demote the qualifying HEAD on first commit. The first-commit guard
                // keeps a manually-sealed HEAD==tip (Immutable) from being re-opened on a
                // later re-commit (plan §4.1).
                if (IsFirstCommitForPromotion(rec.RecordingId, committedSnapshot, supersedeForkIds))
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
                // REFLY-QUALIFY-AND-TIP-WALKS-DISAGREE: resolved with the PENDING
                // tree as context rather than through slot.EffectiveRecordingId.
                // The tip walk now hops VesselSwitchContinuation branch points, and
                // reading those needs the owning tree, which is not in
                // CommittedTrees yet at this point in CommitTree. Same id the
                // slot-facing surface returns once the tree is committed.
                string tipId = EffectiveState.EffectiveTipRecordingId(
                    slot.OriginChildRecordingId, supersedes, recById: null, treeContext: tree);
                if (!string.IsNullOrEmpty(tipId)
                    && !string.Equals(tipId, rec.RecordingId, StringComparison.Ordinal)
                    && tree.Recordings.TryGetValue(tipId, out var tipRec)
                    && tipRec != null
                    && tipRec.MergeState == MergeState.Immutable
                    && IsFirstCommitForPromotion(tipId, committedSnapshot, supersedeForkIds))
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

            if (promoted > 0 || tipsPromoted > 0)
                RecordingStore.BumpStateVersion();
        }

        /// <summary>
        /// The fork ids (<c>NewRecordingId</c>) of every supersede row: a fork's MergeState comes
        /// from SupersedeCommit, never from promotion. Null rows and empty ids are skipped. Pure.
        /// </summary>
        internal static HashSet<string> CollectSupersedeForkIds(
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            var supersedeForkIds = new HashSet<string>(StringComparer.Ordinal);
            for (int r = 0; r < supersedes.Count; r++)
            {
                string forkId = supersedes[r]?.NewRecordingId;
                if (!string.IsNullOrEmpty(forkId)) supersedeForkIds.Add(forkId);
            }
            return supersedeForkIds;
        }

        /// <summary>
        /// The first-commit guard of <see cref="ApplyRewindProvisionalMergeStates"/>: a
        /// recording's MergeState may be re-derived only when it has an id, was not already
        /// committed as part of a tree, and is not a supersede fork. Pure.
        /// </summary>
        internal static bool IsFirstCommitForPromotion(
            string id, HashSet<string> alreadyCommittedRecordingIds, HashSet<string> supersedeForkIds)
        {
            return !string.IsNullOrEmpty(id)
                && !alreadyCommittedRecordingIds.Contains(id)
                && !supersedeForkIds.Contains(id);
        }

        /// <summary>
        /// The rows <c>LedgerOrchestrator.NotifyLedgerTreeCommitted</c> will file for
        /// <paramref name="tree"/> (its pure preview), so promotion reads the same retire
        /// set the slot has once they land. Null when the build fails: promotion then
        /// reads the ledger alone, and the load-time value-seal sweep concludes the slot.
        /// </summary>
        private static List<GameAction> BuildPendingCommitRowsForPromotion(RecordingTree tree)
        {
            try
            {
                var preview = LedgerOrchestrator.BuildTreeCommitRowsPreview(tree);
                ParsekLog.Verbose("UnfinishedFlights",
                    $"CommitTree: promotion reads pending commit rows tree={tree?.Id ?? "<no-id>"} " +
                    $"rows={(preview?.Rows.Count ?? 0).ToString(CultureInfo.InvariantCulture)}");
                return preview?.Rows;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("UnfinishedFlights",
                    $"CommitTree: pending commit row build failed tree={tree?.Id ?? "<no-id>"} " +
                    $"({ex.GetType().Name}: {ex.Message}); promotion reads the ledger alone");
                return null;
            }
        }

        internal static BranchPoint FindBranchPointById(RecordingTree tree, string branchPointId)
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

            HashSet<string> treeRpIds = CollectBranchPointRewindPointIds(tree);
            if (treeRpIds == null || treeRpIds.Count == 0)
                return;

            int promoted = 0;
            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                var rp = scenario.RewindPoints[i];
                if (!IsNormalStagingRewindPointOfTree(rp, treeRpIds)) continue;

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
        /// The rewind point ids the tree's branch points name, or null when none names one
        /// (a null tree or branch point list, null branch points and empty ids name none).
        /// Pure.
        /// </summary>
        internal static HashSet<string> CollectBranchPointRewindPointIds(RecordingTree tree)
        {
            HashSet<string> treeRpIds = null;
            if (tree?.BranchPoints == null) return treeRpIds;
            for (int b = 0; b < tree.BranchPoints.Count; b++)
            {
                var bp = tree.BranchPoints[b];
                if (bp == null || string.IsNullOrEmpty(bp.RewindPointId)) continue;
                if (treeRpIds == null)
                    treeRpIds = new HashSet<string>(StringComparer.Ordinal);
                treeRpIds.Add(bp.RewindPointId);
            }
            return treeRpIds;
        }

        /// <summary>
        /// True for a normal staging rewind point the accepted tree owns: still
        /// session-provisional, no creating Re-Fly session, an id, and named by one of the
        /// tree's branch points. Pure.
        /// </summary>
        internal static bool IsNormalStagingRewindPointOfTree(RewindPoint rp, HashSet<string> treeRpIds)
        {
            if (rp == null) return false;
            if (!rp.SessionProvisional) return false;
            if (!string.IsNullOrEmpty(rp.CreatingSessionId)) return false;
            if (string.IsNullOrEmpty(rp.RewindPointId)) return false;
            if (!treeRpIds.Contains(rp.RewindPointId)) return false;
            return true;
        }

        /// <summary>
        /// Adds tree recordings to committed list, flushes to disk, rebuilds background map,
        /// captures baseline, and creates a milestone.
        /// </summary>
        internal static void FinalizeTreeCommit(RecordingTree tree, int replaceCommittedTreeIndex = -1)
        {
            // Add all tree recordings to committedRecordings (enables ghost playback).
            // Skip recordings already present (chain segments committed mid-flight
            // by CommitRecordingDirect).
            int addedFromTree;
            int replacedFromTree;
            int preservedRewindSaves;
            int preservedRuntimeFields;
            // Live-vessel PID set for the stale-spawn-stamp guard in
            // PreserveLiveRuntimeFieldsOnReplace. Built once per commit (the set
            // is the same for every recording in the tree). null when no
            // populated vessel set is available, in which case the guard stays
            // inert and preserves spawn state as before (drop only on evidence).
            Dictionary<uint, string> liveVesselGuidsByPid;
            ICollection<uint> liveVesselPids = CollectLiveVesselPidsForReplaceGate(out liveVesselGuidsByPid);
            MergeTreeRecordingsIntoCommitted(
                tree, liveVesselPids, liveVesselGuidsByPid,
                out addedFromTree, out replacedFromTree, out preservedRewindSaves, out preservedRuntimeFields);
            if (addedFromTree > 0 || replacedFromTree > 0)
                RecordingStore.BumpStateVersion();

            // Flush to disk immediately to close the crash window.
            // If RunOptimizationPass runs after this, it will re-dirty modified
            // recordings and flush again with the final optimized state.
            RecordingStore.FlushDirtyCommittedFiles();

            // Ensure RecordedVesselPids is populated (covers runtime-created trees
            // that never went through RecordingTree.Load)
            tree.RebuildBackgroundMap();

            bool updatedCommittedTree = IsCommittedTreeIndex(
                replaceCommittedTreeIndex, RecordingStore.CommittedTrees.Count);
            if (updatedCommittedTree)
            {
                RecordingStore.ReplaceCommittedTreeAt(replaceCommittedTreeIndex, tree);
                RecordingStore.BumpStateVersion();
            }
            else
            {
                RecordingStore.AppendCommittedTreeUnbumped(tree);
            }

            string commitVerb = updatedCommittedTree ? "Updated committed tree" : "Committed tree";
            RecordingStore.Log($"[Parsek] {commitVerb} '{tree.TreeName}' ({tree.Recordings.Count} recordings). " +
                $"Total committed: {RecordingStore.CommittedRecordings.Count} recordings, {RecordingStore.CommittedTrees.Count} trees");
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
            double endUT = ComputeTreeEndUT(tree);
            MilestoneStore.CreateMilestone(tree.Id, endUT);
        }

        /// <summary>
        /// Puts every recording of <paramref name="tree"/> into the committed list, marked
        /// dirty: one already committed under its id (or as the same instance) is replaced in
        /// place when it is another instance, carrying the live runtime fields over from the
        /// committed copy; any other is appended unbumped. Counts what it added, replaced and
        /// carried.
        /// </summary>
        internal static void MergeTreeRecordingsIntoCommitted(
            RecordingTree tree,
            ICollection<uint> liveVesselPids,
            Dictionary<uint, string> liveVesselGuidsByPid,
            out int addedFromTree,
            out int replacedFromTree,
            out int preservedRewindSaves,
            out int preservedRuntimeFields)
        {
            addedFromTree = 0;
            replacedFromTree = 0;
            preservedRewindSaves = 0;
            preservedRuntimeFields = 0;
            foreach (var rec in tree.Recordings.Values)
            {
                rec.FilesDirty = true;
                int existingIndex = FindCommittedRecordingIndex(rec);
                if (existingIndex >= 0)
                {
                    if (!ReferenceEquals(RecordingStore.CommittedRecordings[existingIndex], rec))
                    {
                        var existing = RecordingStore.CommittedRecordings[existingIndex];
                        bool savePreserved;
                        int otherPreserved;
                        PreserveLiveRuntimeFieldsOnReplace(
                            existing, rec, out savePreserved, out otherPreserved,
                            liveVesselPids, liveVesselGuidsByPid);
                        if (savePreserved) preservedRewindSaves++;
                        preservedRuntimeFields += otherPreserved;
                        RecordingStore.ReplaceCommittedAt(existingIndex, rec);
                        replacedFromTree++;
                    }
                    continue;
                }

                RecordingStore.AppendCommittedUnbumped(rec);
                addedFromTree++;
            }
        }

        /// <summary>True when <paramref name="index"/> names an existing committed tree slot.</summary>
        internal static bool IsCommittedTreeIndex(int index, int count)
        {
            return index >= 0 && index < count;
        }

        /// <summary>The latest recording end UT of the tree; 0 when none ends after 0.</summary>
        internal static double ComputeTreeEndUT(RecordingTree tree)
        {
            double endUT = 0;
            foreach (var rec in tree.Recordings.Values)
            {
                double recEnd = rec.EndUT;
                if (recEnd > endUT) endUT = recEnd;
            }
            return endUT;
        }

        internal static int FindCommittedRecordingIndex(Recording rec)
        {
            if (rec == null) return -1;

            if (!string.IsNullOrEmpty(rec.RecordingId))
            {
                for (int i = 0; i < RecordingStore.CommittedRecordings.Count; i++)
                {
                    var existing = RecordingStore.CommittedRecordings[i];
                    if (existing != null &&
                        string.Equals(existing.RecordingId, rec.RecordingId, StringComparison.Ordinal))
                    {
                        return i;
                    }
                }
            }

            for (int i = 0; i < RecordingStore.CommittedRecordings.Count; i++)
            {
                if (ReferenceEquals(RecordingStore.CommittedRecordings[i], rec))
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
        private static HashSet<uint> CollectLiveVesselPidsForReplaceGate(
            out Dictionary<uint, string> guidsByPid)
        {
            HashSet<uint> pids = null;
            guidsByPid = null;
            try
            {
                var vessels = FlightGlobals.Vessels;
                if (vessels != null)
                {
                    pids = new HashSet<uint>();
                    guidsByPid = new Dictionary<uint, string>();
                    for (int i = 0; i < vessels.Count; i++)
                    {
                        var v = vessels[i];
                        if (v != null && v.persistentId != 0u)
                        {
                            pids.Add(v.persistentId);
                            AddLiveVesselGuidForReplaceGate(guidsByPid, v.persistentId,
                                v.id != Guid.Empty ? v.id.ToString("N") : null);
                        }
                    }
                }

                var protoVessels = HighLogic.CurrentGame?.flightState?.protoVessels;
                if (protoVessels != null)
                {
                    if (pids == null) pids = new HashSet<uint>();
                    if (guidsByPid == null) guidsByPid = new Dictionary<uint, string>();
                    for (int i = 0; i < protoVessels.Count; i++)
                    {
                        var pv = protoVessels[i];
                        if (pv != null && pv.persistentId != 0u)
                        {
                            pids.Add(pv.persistentId);
                            AddLiveVesselGuidForReplaceGate(guidsByPid, pv.persistentId,
                                VesselLaunchIdentity.ReadLaunchGuid(pv));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("RecordingStore",
                    $"CollectLiveVesselPidsForReplaceGate: failed to collect live PIDs " +
                    $"({ex.GetType().Name}); spawn-stamp guard inert for this commit");
                guidsByPid = null;
                return null;
            }

            if (pids == null || pids.Count == 0)
            {
                guidsByPid = null;
                return null;
            }
            return pids;
        }

        /// <summary>
        /// Records one live vessel's launch guid for the replace gate. Two sources disagreeing on
        /// one pid (the loaded vessel and a stale proto mirror) record the guid as unknown, which
        /// keeps the gate on its pid-only behavior for that pid.
        /// </summary>
        internal static void AddLiveVesselGuidForReplaceGate(
            Dictionary<uint, string> guidsByPid, uint pid, string guid)
        {
            if (guidsByPid == null || pid == 0u) return;
            string normalized = VesselLaunchIdentity.NormalizeGuid(guid);
            string prior;
            if (!guidsByPid.TryGetValue(pid, out prior))
            {
                guidsByPid[pid] = normalized;
                return;
            }
            if (prior == null) return;
            if (normalized == null || !string.Equals(prior, normalized, StringComparison.OrdinalIgnoreCase))
                guidsByPid[pid] = null;
        }

        /// <summary>
        /// Pure stale-spawn-stamp test for <see cref="PreserveLiveRuntimeFieldsOnReplace"/>: the
        /// recording's spawn pid names no vessel in <paramref name="liveVesselPids"/>, or it is an
        /// adoption stamp (spawn pid == recorded pid, the craft-baked pid) and the live vessel
        /// with that pid is a conclusively different launch with no link to the recording's
        /// guid and the recording's end snapshot names another launch
        /// (<see cref="VesselLaunchIdentity.SpawnClaimStillHeld"/>): a relaunch of the same
        /// craft does not keep an older launch's stamp alive. A null set means liveness is
        /// unknown (never stale); an unknown live or snapshot guid keeps the stamp.
        /// </summary>
        internal static bool IsSpawnStampStaleForReplace(
            Recording existing,
            ICollection<uint> liveVesselPids,
            IDictionary<uint, string> liveVesselGuidsByPid,
            out bool heldByOtherLaunch,
            Func<Recording, string> snapshotGuidReader = null)
        {
            heldByOtherLaunch = false;
            if (existing == null || liveVesselPids == null || existing.SpawnedVesselPersistentId == 0u)
                return false;
            uint spawnedPid = existing.SpawnedVesselPersistentId;
            if (!liveVesselPids.Contains(spawnedPid))
                return true;
            string liveGuid = null;
            if (liveVesselGuidsByPid != null)
                liveVesselGuidsByPid.TryGetValue(spawnedPid, out liveGuid);
            if (VesselLaunchIdentity.SpawnClaimStillHeld(existing, spawnedPid, liveGuid, snapshotGuidReader))
                return false;
            heldByOtherLaunch = true;
            return true;
        }

        /// <summary>True for a terminal after which the recorded vessel no longer exists.</summary>
        internal static bool EndsWithNoVessel(TerminalState? terminal)
        {
            return terminal == TerminalState.Recovered
                || terminal == TerminalState.Destroyed
                || terminal == TerminalState.Disassembled;
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
            ICollection<uint> liveVesselPids = null,
            IDictionary<uint, string> liveVesselGuidsByPid = null)
        {
            savePreserved = false;
            otherPreserved = 0;
            if (existing == null || incoming == null) return;

            savePreserved = PreserveRewindLaunchFieldsOnReplace(existing, incoming, ref otherPreserved);

            // A deferred epoch advance from an out-of-band flush of the replaced object
            // must not be lost with it, or the next OnSave would skip the rewrite and keep
            // the preserved epoch for good. Not counted: it is bookkeeping, not a field
            // the pending tree lost, and carrying it costs at most one extra rewrite.
            if (existing.SidecarEpochAdvancePending)
                incoming.SidecarEpochAdvancePending = true;

            // Playback resource cursor.
            if (incoming.LastAppliedResourceIndex == -1
                && existing.LastAppliedResourceIndex != -1)
            {
                incoming.LastAppliedResourceIndex = existing.LastAppliedResourceIndex;
                otherPreserved++;
            }

            PreserveSpawnClaimOnReplace(
                existing, incoming, liveVesselPids, liveVesselGuidsByPid, ref otherPreserved);
            PreserveSpawnCountersOnReplace(existing, incoming, ref otherPreserved);
            PreserveTerminalSpawnSafetyOnReplace(existing, incoming, ref otherPreserved);
            PreserveRewindSuppressionOnReplace(existing, incoming, ref otherPreserved);
            PreserveFinalStateDiagnosticsOnReplace(existing, incoming, ref otherPreserved);
        }

        /// <summary>
        /// The launch save filename and the rewind-reserved resource ledger (#573) of
        /// <see cref="PreserveLiveRuntimeFieldsOnReplace"/>: each copies only when the
        /// replacement is at its default. Returns whether the save filename was copied; every
        /// other copy increments <paramref name="otherPreserved"/>.
        /// </summary>
        internal static bool PreserveRewindLaunchFieldsOnReplace(
            Recording existing, Recording incoming, ref int otherPreserved)
        {
            bool savePreserved = false;
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
            return savePreserved;
        }

        /// <summary>
        /// The spawn claim (<c>VesselSpawned</c> + <c>SpawnedVesselPersistentId</c>, #264) of
        /// <see cref="PreserveLiveRuntimeFieldsOnReplace"/>, behind the vesselless-terminal and
        /// stale-spawn-stamp guards.
        /// </summary>
        internal static void PreserveSpawnClaimOnReplace(
            Recording existing, Recording incoming,
            ICollection<uint> liveVesselPids,
            IDictionary<uint, string> liveVesselGuidsByPid,
            ref int otherPreserved)
        {
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
            // An adoption stamp held by another launch of the craft (same baked pid, a
            // conclusively different launch guid) is stale too.
            bool stampHeldByOtherLaunch;
            bool existingSpawnPidIsStale = IsSpawnStampStaleForReplace(
                existing, liveVesselPids, liveVesselGuidsByPid, out stampHeldByOtherLaunch);
            // A replacement whose own terminal leaves no vessel (Recovered, Destroyed,
            // Disassembled) is never represented by a live spawned vessel, so it never
            // inherits a spawn claim. An in-flight Recover of a resumed spawned vessel
            // commits Recovered at the Space Center BEFORE stock removes the vessel, so the
            // stale guard above still sees the pid live; re-installing the claim there made
            // the next flight's spawn-death pass read the recovery as a death.
            bool incomingEndsVesselless = EndsWithNoVessel(incoming.TerminalStateValue);
            if (incomingEndsVesselless)
            {
                if (copyVesselSpawned || copySpawnedPid)
                    ParsekLog.Info("RecordingStore",
                        $"PreserveLiveRuntimeFieldsOnReplace: spawn stamp " +
                        $"pid={existing.SpawnedVesselPersistentId} not re-installed for recording " +
                        $"\"{incoming.VesselName}\" id={incoming.RecordingId ?? "<none>"}: its terminal " +
                        $"{incoming.TerminalStateValue} leaves no vessel");
            }
            else if (existingSpawnPidIsStale)
            {
                // Log only when the suppression actually prevented a copy, so a
                // recording that already carries its own spawn claim does not
                // produce a false-alarm "dropped" line.
                if (copyVesselSpawned || copySpawnedPid)
                    ParsekLog.Info("RecordingStore",
                        $"PreserveLiveRuntimeFieldsOnReplace: dropped stale spawn stamp " +
                        $"pid={existing.SpawnedVesselPersistentId} for recording " +
                        $"\"{incoming.VesselName}\" id={incoming.RecordingId ?? "<none>"} " +
                        (stampHeldByOtherLaunch
                            ? "(adoption stamp now held by another launch of the craft): "
                            : "(vessel no longer live): ") +
                        $"not re-installed on replacement " +
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
        }

        /// <summary>
        /// The terminal-spawn supersede mark and the spawn attempt / collision / abandon /
        /// walkback / duplicate-blocker / death counters and flags (#264) of
        /// <see cref="PreserveLiveRuntimeFieldsOnReplace"/>.
        /// </summary>
        internal static void PreserveSpawnCountersOnReplace(
            Recording existing, Recording incoming, ref int otherPreserved)
        {
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
        }

        /// <summary>
        /// The terminal-spawn safety state (<c>TerminalSpawnSafety*</c>,
        /// <c>TerminalSpawnNextAttemptUT</c>) of <see cref="PreserveLiveRuntimeFieldsOnReplace"/>.
        /// </summary>
        internal static void PreserveTerminalSpawnSafetyOnReplace(
            Recording existing, Recording incoming, ref int otherPreserved)
        {
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
        }

        /// <summary>
        /// The Plain-Rewind-to-Launch spawn suppression metadata (#573 / #589) of
        /// <see cref="PreserveLiveRuntimeFieldsOnReplace"/>.
        /// </summary>
        internal static void PreserveRewindSuppressionOnReplace(
            Recording existing, Recording incoming, ref int otherPreserved)
        {
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
        }

        /// <summary>
        /// The diagnostic peaks and final-state metadata (situation, distances, destroyed flag)
        /// of <see cref="PreserveLiveRuntimeFieldsOnReplace"/>. A Destroyed replacement never
        /// takes back a cleared situation.
        /// </summary>
        internal static void PreserveFinalStateDiagnosticsOnReplace(
            Recording existing, Recording incoming, ref int otherPreserved)
        {
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
        internal static bool TryUnionActiveReFlyTreeIntoCommitted(
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

            addedRecs = UnionRecordingsInto(existing, incoming);
            addedBps = UnionBranchPointsInto(existing, incoming);
            AdoptMissingRootRecordingId(existing, incoming);
            activeIdSwapped = AdoptIncomingActiveRecordingId(existing, incoming);

            // RebuildBackgroundMap: the recordings dict just changed.
            existing.RebuildBackgroundMap();
            return true;
        }

        /// <summary>
        /// Union of the recordings: adds incoming-only ids, overwrites a shared id with the
        /// incoming instance, keeps existing-only ids. Skips empty keys and null values.
        /// Returns how many ids were added.
        /// </summary>
        internal static int UnionRecordingsInto(RecordingTree existing, RecordingTree incoming)
        {
            int addedRecs = 0;
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
            return addedRecs;
        }

        /// <summary>
        /// Union of the branch points by id: appends each incoming branch point whose id the
        /// existing list does not hold (creating the list when it is null). Returns how many
        /// were appended.
        /// </summary>
        internal static int UnionBranchPointsInto(RecordingTree existing, RecordingTree incoming)
        {
            int addedBps = 0;
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
            return addedBps;
        }

        /// <summary>Keeps the existing root id when set; else adopts a set incoming one.</summary>
        internal static void AdoptMissingRootRecordingId(RecordingTree existing, RecordingTree incoming)
        {
            // Root id: keep existing if set; else adopt incoming.
            if (string.IsNullOrEmpty(existing.RootRecordingId)
                && !string.IsNullOrEmpty(incoming.RootRecordingId))
            {
                existing.RootRecordingId = incoming.RootRecordingId;
            }
        }

        /// <summary>
        /// Adopts a set incoming active id that differs from the existing one; returns whether
        /// it did.
        /// </summary>
        internal static bool AdoptIncomingActiveRecordingId(RecordingTree existing, RecordingTree incoming)
        {
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
                return true;
            }
            return false;
        }

        /// <summary>
        /// Internal shim that lets <c>MergeJournalOrchestrator.
        /// MigrateActiveReFlyForkIntoCommittedTree</c> call the
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
        /// helper inherits from the callee.</para>
        /// </summary>
        internal static bool UnionActiveReFlyTreeIntoCommittedForMerge(
            RecordingTree existing, RecordingTree incoming, ReFlySessionMarker marker,
            out int addedRecs, out int addedBps)
        {
            return TryUnionActiveReFlyTreeIntoCommitted(
                existing, incoming, marker, out addedRecs, out addedBps, out _);
        }

        internal static bool ShouldReplaceCommittedTree(
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

        internal static int CountNewRecordingIds(RecordingTree existing, RecordingTree incoming)
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

        internal static int CountMissingRecordingIds(RecordingTree existing, RecordingTree incoming)
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

        internal static int CountNewBranchPointIds(RecordingTree existing, RecordingTree incoming)
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

        internal static int CountMissingBranchPointIds(RecordingTree existing, RecordingTree incoming)
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

        /// <summary>
        /// Commits the pending tree to the timeline.
        /// </summary>
        public static void CommitPendingTree()
        {
            if (PendingTreeSlot.PendingTree == null)
            {
                ParsekLog.Verbose("RecordingStore", "CommitPendingTree called with no pending tree");
                return;
            }

            CommitTree(PendingTreeSlot.PendingTree);
            PendingTreeSlot.ClearSlot();
        }
    }
}
