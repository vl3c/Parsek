using System;
using System.Collections.Generic;

namespace Parsek
{
    public static partial class RecordingStore
    {
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
    }
}
