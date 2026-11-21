using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// The committed-list optimization pass (RecordingStore services plan, service G): merges
    /// adjacent chain segments, splits multi-environment recordings at environment
    /// boundaries, trims boring tails, links loop-sync debris and flushes dirty sidecars.
    /// <see cref="RecordingOptimizer"/> stays the pure core. The pass reads the committed list
    /// through <see cref="RecordingStore.CommittedRecordings"/> and changes its structure only
    /// through the store's primitives (<see cref="RecordingStore.RemoveCommittedAt"/>,
    /// <see cref="RecordingStore.InsertCommittedAt"/>), which bump the state version and raise
    /// the committed-list notifications.
    /// </summary>
    internal static class CommittedListOptimizer
    {
        // ─── Recording optimization ─────────────────────────────────────────

        /// <summary>
        /// The route sources <paramref name="tree"/> carries unchanged into a commit
        /// (<see cref="TreeCommitService.CollectUnchangedRouteSourceIds(RecordingTree, ICollection{string}, Func{Recording, bool})"/>
        /// over the ids a committed or dormant supply route names, with the route store's captured-ref
        /// match for a source the tree carries without a committed copy). This type already reads that
        /// source immutability set (design 4.7) for the pass below; the tree commit asks here so
        /// it does not read the route store itself. Never null.
        /// </summary>
        internal static HashSet<string> CollectUnchangedRouteSourceIds(RecordingTree tree)
        {
            HashSet<string> routeSourceIds = Logistics.RouteStore.CollectSourceRecordingIds();
            return TreeCommitService.CollectUnchangedRouteSourceIds(
                tree, routeSourceIds, Logistics.RouteStore.RecordingMatchesCapturedSourceRef);
        }

        /// <summary>
        /// Runs the optimization pass: find merge candidates among committed recordings,
        /// execute merges, re-index chains, then split multi-environment recordings at
        /// environment boundaries. Called on a COLD load (an in-session load such as F9 or a
        /// revert does not run it), after every tree commit and right after a Re-Fly merge
        /// finishes.
        ///
        /// Structural changes (a merge removes the absorbed recording mid-list, a split
        /// inserts the second half mid-list) bump <see cref="RecordingStore.StateVersion"/> at the
        /// mutation so the <see cref="EffectiveState"/> caches rebuild even if a later step
        /// of the pass throws, and raise the <c>CommittedRecording*</c> notifications
        /// (RecordingStore.CommittedListNotifications.cs) so index-keyed live state
        /// reindexes in step.
        /// </summary>
        internal static void RunOptimizationPass()
        {
            var recordings = RecordingStore.CommittedRecordings;
            if (recordings == null || recordings.Count == 0)
            {
                ParsekLog.Verbose("RecordingStore", "Optimization pass: skipped (no recordings)");
                return;
            }

            // A recording a supply route sources from is never rewritten here: a merge drops
            // its id, a split or a trim moves its EndUT, and RouteStore.RevalidateSources would
            // pause the route for good (design 4.7, source immutability).
            HashSet<string> routeSourceIds = Logistics.RouteStore.CollectSourceRecordingIds();
            // Owner RULE 3: a committed tree whose live copy is being flown is held the same way
            // until that copy is committed or discarded.
            routeSourceIds = TreeCommitService.HoldLiveCopiesForOptimizer(
                routeSourceIds, RecordingStore.CommittedTrees, PendingTreeSlot.PendingTree,
                PendingTreeSlot.SavedPendingTreeDuringActiveRestore,
                CommittedTreeRestoreAttempt.CommittedTreeRestoreAttemptTreeId);

            int mergeCount = RunOptimizationMergePass(recordings, routeSourceIds);
            int splitCount = RunOptimizationSplitPass(recordings, routeSourceIds);
            // After the split: a split gives the tail the recording's end (terminal state and
            // snapshot), so the spawn stamp goes with it; this also heals saves split earlier.
            RecordingOptimizer.MoveChainSpawnStampsToTip(
                recordings, ParsekScenario.Instance?.RecordingSupersedes);
            if (mergeCount > 0 || splitCount > 0)
            {
                ParsekLog.Verbose("RecordingStore",
                    $"Optimization pass: committed list restructured (merges={mergeCount} splits={splitCount}) - " +
                    $"StateVersion now {RecordingStore.StateVersion}");
            }
            TrimBoringTailsForOptimization(recordings, routeSourceIds);

            // Loop sync pass: link debris recordings to their parent recording
            // so debris ghosts replay in sync with the parent's loop cycle.
            PopulateLoopSyncParentIndices(recordings);

            // A split leg copies the post-apply name and a merge drops a leg id; classify the
            // new legs and keep every covered leg on its decision (vessel names design 7.4).
            VesselNameStore.ApplyAll("optimizer");

            // Rebuild BackgroundMap for trees that had structural changes (splits/merges).
            // BackgroundMap is a runtime-only field mapping PID → RecordingId; splits create
            // new recordings and merges remove them, invalidating the map.
            if (mergeCount > 0 || splitCount > 0)
            {
                for (int t = 0; t < RecordingStore.CommittedTrees.Count; t++)
                    RecordingStore.CommittedTrees[t].RebuildBackgroundMap();
            }

            // Flush all dirty recordings to disk so the crash window after
            // commit+optimize is closed (data no longer lives only in RAM).
            RecordingStore.FlushDirtyCommittedFiles();
        }

        private static int RunOptimizationMergePass(
            IReadOnlyList<Recording> recordings, HashSet<string> routeSourceIds)
        {
            int mergeCount = 0;
            HashSet<string> heldForRoutes = null;
            Dictionary<string, string> heldByLiveReFlyMerge = CollectLiveReFlyMergeHeldIds();
            // Every merge removes a recording, so the list size bounds the merges; the bound
            // only guards against a pair offered again without being merged.
            int maxMergesPerPass = recordings.Count + 1;
            // Iterate merge passes until no more candidates (merging may create new adjacent pairs)
            bool changed = true;
            while (changed && mergeCount < maxMergesPerPass)
            {
                changed = false;
                var candidates = RecordingOptimizer.FindMergeCandidates(recordings, heldByLiveReFlyMerge);
                if (candidates.Count == 0) break;

                // Process one candidate per pass (indices shift after removal): the first one
                // that touches no route source recording.
                int chosenMerge = ChooseMergeCandidateIndex(
                    candidates, recordings, routeSourceIds, ref heldForRoutes);
                if (chosenMerge < 0) break;
                var (idxA, idxB) = candidates[chosenMerge];
                var target = recordings[idxA];
                var absorbed = recordings[idxB];

                string absorbedId = RecordingOptimizer.MergeInto(target, absorbed);
                if (absorbedId == null)
                {
                    // A refused merge changes nothing: the absorbed recording, its ledger
                    // tags and its files all stay. Stop this pass; the refusal is a
                    // pair CanAutoMerge should not have offered.
                    ParsekLog.Warn("RecordingStore",
                        $"Optimization: merge of '{absorbed.RecordingId}' into '{target.RecordingId}' " +
                        "refused - nothing removed; stopping the merge pass");
                    break;
                }
                target.FilesDirty = true;
                string chainId = target.ChainId;
                RetagLedgerActionsAfterOptimizationMerge(target, absorbed);
                // MergeInto stamped the absorbed terminal onto the target.
                InFlightRecoveryRequest.MoveAwaitingStockPayout(
                    absorbed.RecordingId, target.RecordingId, "optimization-merge");
                UpdateTreeStateAfterOptimizationMerge(target, absorbed);
                RepointAnchorsAfterOptimizationMerge(recordings, target, absorbed);

                // Remove absorbed recording from committed list
                RecordingStore.RemoveCommittedAt(idxB, target);

                // Delete absorbed recording's sidecar files
                try { RecordingFileJanitor.DeleteRecordingFiles(absorbed); }
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
                    $"Optimization pass: hit merge safety bound ({maxMergesPerPass}), some candidates may remain");
            else if (mergeCount > 0)
                ParsekLog.Info("RecordingStore", $"Optimization pass: merged {mergeCount} segment pair(s)");
            else
                ParsekLog.Verbose("RecordingStore", "Optimization pass: no merge candidates found");

            if (heldForRoutes != null)
                ParsekLog.Verbose("RecordingStore",
                    $"Optimization pass: merge held for {heldForRoutes.Count} route source recording(s) " +
                    $"[{FormatSortedIds(heldForRoutes)}] - a supply route's source recordings stay whole");

            return mergeCount;
        }

        /// <summary>
        /// The recordings a live Re-Fly session holds out of the merge pass (id -> why): the
        /// marker's supersede target (TIP once the split ran) and its origin (HEAD), and while
        /// a merge journal is active also the provisional. Their supersede rows do not exist
        /// yet when the cold OnLoad's pass runs ahead of the journal finisher (a crash between
        /// the split save and Durable Save #1), so a HEAD + TIP pair would be glued back. The
        /// provisional alone is left out without a journal: a live session's own commit pass
        /// may merge the attempt, which <c>MergeDialog.ResolveOptimizedRecordingSurvivor</c>
        /// follows (an in-place continuation's provisional is the origin, likewise). Read here
        /// and handed to <see cref="RecordingOptimizer.CanAutoMerge"/>, which must not name
        /// Rewind types. Empty, never null.
        /// </summary>
        internal static Dictionary<string, string> CollectLiveReFlyMergeHeldIds()
        {
            var held = new Dictionary<string, string>(StringComparer.Ordinal);
            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario)) return held;
            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null) return held;
            bool journalActive = scenario.ActiveMergeJournal != null;
            string suffix = " sess=" + (marker.SessionId ?? "<no-id>")
                + " journal=" + (journalActive ? "active" : "none");
            string provisional = marker.ActiveReFlyRecordingId;
            if (journalActive && !string.IsNullOrEmpty(provisional))
                held[provisional] = "merge journal provisional" + suffix;
            string origin = marker.OriginChildRecordingId;
            if (!string.IsNullOrEmpty(origin)
                && (journalActive || !string.Equals(origin, provisional, StringComparison.Ordinal)))
                held[origin] = "marker origin" + suffix;
            if (!string.IsNullOrEmpty(marker.SupersedeTargetId))
                held[marker.SupersedeTargetId] = "marker supersede target" + suffix;
            return held;
        }

        /// <summary>
        /// The absorbed recording's id disappears with the merge, so every other recording's
        /// reference to it moves to the merge target, which now covers the absorbed time range:
        /// a Relative section's <see cref="TrackSection.anchorRecordingId"/> and a
        /// <see cref="Recording.ParentAnchorRecordingId"/>. Touched recordings are marked dirty
        /// (the section anchor lives in the trajectory sidecar). One summary line when anything
        /// moved.
        /// </summary>
        internal static void RepointAnchorsAfterOptimizationMerge(
            IReadOnlyList<Recording> recordings, Recording target, Recording absorbed)
        {
            string absorbedId = absorbed?.RecordingId;
            string targetId = target?.RecordingId;
            if (recordings == null || string.IsNullOrEmpty(absorbedId) || string.IsNullOrEmpty(targetId))
                return;
            int sections = 0;
            int parentAnchors = 0;
            for (int r = 0; r < recordings.Count; r++)
            {
                var rec = recordings[r];
                if (rec == null || ReferenceEquals(rec, absorbed)) continue;
                if (string.Equals(rec.ParentAnchorRecordingId, absorbedId, StringComparison.Ordinal))
                {
                    rec.ParentAnchorRecordingId = targetId;
                    parentAnchors++;
                }
                if (rec.TrackSections == null) continue;
                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    var s = rec.TrackSections[i];
                    if (!string.Equals(s.anchorRecordingId, absorbedId, StringComparison.Ordinal))
                        continue;
                    s.anchorRecordingId = targetId;
                    rec.TrackSections[i] = s;
                    rec.FilesDirty = true;
                    sections++;
                }
            }
            if (sections > 0 || parentAnchors > 0)
            {
                ParsekLog.Info("RecordingStore",
                    $"Optimization: anchors re-pointed {absorbedId} -> {targetId} " +
                    $"(sections={sections.ToString(CultureInfo.InvariantCulture)} " +
                    $"parentAnchors={parentAnchors.ToString(CultureInfo.InvariantCulture)})");
            }
        }

        /// <summary>
        /// Picks the merge candidate to apply this iteration: the first pair whose two
        /// recordings no supply route sources from (<paramref name="routeSourceIds"/>). The
        /// recording ids of every pair passed over for that reason are added to
        /// <paramref name="heldForRoutes"/> (allocated on first use) for the pass summary.
        /// Returns -1 when every candidate touches a route source. Pure read over the inputs.
        /// </summary>
        internal static int ChooseMergeCandidateIndex(
            IReadOnlyList<(int, int)> candidates,
            IReadOnlyList<Recording> recordings,
            HashSet<string> routeSourceIds,
            ref HashSet<string> heldForRoutes)
        {
            if (candidates == null) return -1;
            for (int c = 0; c < candidates.Count; c++)
            {
                Recording a = ResolveCandidate(recordings, candidates[c].Item1);
                Recording b = ResolveCandidate(recordings, candidates[c].Item2);
                bool aHeld = IsRouteSource(a, routeSourceIds);
                bool bHeld = IsRouteSource(b, routeSourceIds);
                if (!aHeld && !bHeld)
                    return c;
                if (heldForRoutes == null)
                    heldForRoutes = new HashSet<string>(StringComparer.Ordinal);
                if (aHeld) heldForRoutes.Add(a.RecordingId);
                if (bHeld) heldForRoutes.Add(b.RecordingId);
            }
            return -1;
        }

        private static Recording ResolveCandidate(IReadOnlyList<Recording> recordings, int index)
            => recordings != null && index >= 0 && index < recordings.Count ? recordings[index] : null;

        private static bool IsRouteSource(Recording rec, HashSet<string> routeSourceIds)
            => rec != null
               && routeSourceIds != null
               && !string.IsNullOrEmpty(rec.RecordingId)
               && routeSourceIds.Contains(rec.RecordingId);

        private static string FormatSortedIds(HashSet<string> ids)
        {
            var sorted = new List<string>(ids);
            sorted.Sort(StringComparer.Ordinal);
            return string.Join(", ", sorted);
        }

        private static int RunOptimizationSplitPass(
            IReadOnlyList<Recording> recordings, HashSet<string> routeSourceIds)
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
            // Owner ruling Q19: a mission is split in one go, not 50 cuts per load. Every cut
            // consumes a section boundary, so the section count bounds the splits; the bound
            // only guards against a candidate offered again without being split.
            int maxSplitsPerPass = ComputeSplitSafetyBound(recordings);
            var splitReasons = new List<RecordingOptimizer.SplitBoundaryReason>();
            string deferredActiveReFlyId =
                ParsekScenario.Instance?.ActiveReFlySessionMarker?.ActiveReFlyRecordingId;
            int deferredCandidatesObservedTotal = 0;
            HashSet<string> heldForRoutes = null;
            bool splitChanged = true;
            while (splitChanged && splitCount < maxSplitsPerPass)
            {
                splitChanged = false;
                splitReasons.Clear();
                var splitCandidates = RecordingOptimizer.FindSplitCandidatesForOptimizer(recordings, splitReasons);
                if (splitCandidates.Count == 0) break;

                int chosen = ChooseSplitCandidateIndex(
                    splitCandidates, recordings, deferredActiveReFlyId,
                    routeSourceIds, ref heldForRoutes,
                    out int deferredCandidatesThisIter);

                deferredCandidatesObservedTotal += deferredCandidatesThisIter;
                if (chosen < 0)
                    break;

                var (recIdx, secIdx) = splitCandidates[chosen];
                var original = recordings[recIdx];

                var second = RecordingOptimizer.SplitAtSection(original, secIdx);

                CopySplitIdentityFields(original, second);
                AssignSplitRoles(original, second, splitReasons[chosen]);

                RetagLedgerActionsAfterOptimizationSplit(original, second);
                // SplitAtSection moved the terminal state onto the second half.
                InFlightRecoveryRequest.MoveAwaitingStockPayout(
                    original.RecordingId, second.RecordingId, "optimization-split");

                DeriveSplitSegmentBodyNames(original, second);

                // BranchPoint linkage: ChildBranchPointId moves to the half whose
                // time range owns the branch point (a Re-Fly atmo/exo split can fall
                // after a staging branch, which must stay on the first half).
                //
                // The parent BranchPoint's ChildRecordingIds still references
                // original.RecordingId (now the first chain segment), which IS the direct
                // child of that BP; the shared ChainId connects the later segments.
                bool childBranchPointMovesToSecond = RecordingStore.ShouldMoveChildBranchPointToSplitSecondHalf(
                    original.TreeId,
                    original.ChildBranchPointId,
                    second.StartUT);
                AssignSplitChildBranchPoint(original, second, childBranchPointMovesToSecond);
                // second.ParentRecordingId stays the original's own parent link (copied by
                // SplitAtSection); it is not a link to the original.

                // Every branch point that names the original as a parent at or after the cut,
                // and every child recording linked to it from then on, now belongs to the
                // second half (SplitParentLinks): the moved ChildBranchPointId, a decouple the
                // vessel flew on past, a dock, an EVA.
                SplitParentLinks.RepointToTail(
                    RecordingStore.FindCommittedTreeById(original.TreeId), original.RecordingId, second,
                    second.StartUT, null, "Optimization split");

                // Add to committed recordings (after original)
                RecordingStore.InsertCommittedAt(recIdx + 1, second);

                // Update tree dict if applicable
                AddToCommittedTree(original.TreeId, second);

                // The vessel flies on in the later half, so a tree whose active recording was
                // the original now has the tail as its active recording (the mirror of the
                // merge's ActiveRecordingId move). Left on the head, a later resume of the tree
                // recorded onto the head past its end (todo COMMITTED-RESTORE-APPENDS-TO-ROOT).
                MoveActiveRecordingToSplitTail(
                    RecordingStore.FindCommittedTreeById(original.TreeId), original, second);

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
                    $"Optimization pass: hit split safety bound ({maxSplitsPerPass}), some candidates may remain");
            else if (splitCount > 0)
                ParsekLog.Info("RecordingStore", $"Optimization pass: split {splitCount} recording(s)");

            if (deferredCandidatesObservedTotal > 0
                && !string.IsNullOrEmpty(deferredActiveReFlyId))
            {
                ParsekLog.Info("RecordingStore",
                    $"Optimization pass: deferred split for active Re-Fly recording " +
                    $"id={deferredActiveReFlyId} candidatesObserved={deferredCandidatesObservedTotal}");
            }

            if (heldForRoutes != null)
                ParsekLog.Verbose("RecordingStore",
                    $"Optimization pass: split held for {heldForRoutes.Count} route source recording(s) " +
                    $"[{FormatSortedIds(heldForRoutes)}] - a supply route's source recordings stay whole");

            return splitCount;
        }

        /// <summary>
        /// The split loop's safety bound (ruling Q19): the committed list's section count plus
        /// its recording count, never under 50 (the old per-pass cap).
        /// </summary>
        internal static int ComputeSplitSafetyBound(IReadOnlyList<Recording> recordings)
        {
            int sections = 0;
            if (recordings != null)
                for (int i = 0; i < recordings.Count; i++)
                    sections += recordings[i]?.TrackSections?.Count ?? 0;
            return Math.Max(50, sections + (recordings?.Count ?? 0));
        }

        /// <summary>
        /// Names the two rows a version-2 split made (<see cref="Recording.SegmentRole"/>): the
        /// first half's role is read again from its own content, and an unclassified space
        /// head of a version-3 orbit-kind cut is named by the cut
        /// (<see cref="RecordingOptimizer.ResolveHeadRoleAfterSplit"/>); the second half gets the
        /// role of the cut that started it. Version-1 and EVA recordings
        /// get no role. Logs the pair.
        /// </summary>
        internal static void AssignSplitRoles(
            Recording original, Recording second, RecordingOptimizer.SplitBoundaryReason reason)
        {
            if (!RecordingOptimizer.UsesCurrentSplitRules(original)) return;
            original.SegmentRole = RecordingOptimizer.ResolveHeadRoleAfterSplit(original, reason);
            second.SegmentRole = RecordingOptimizer.ResolveCutRole(reason, second);
            ParsekLog.Verbose("Optimizer",
                $"Split roles: '{original.RecordingId}'={original.SegmentRole ?? "<none>"} " +
                $"'{second.RecordingId}'={second.SegmentRole ?? "<none>"} (cut {reason})");
        }

        /// <summary>
        /// Picks the split candidate to apply this iteration. With no live Re-Fly defer id
        /// and no route source ids, the first candidate (index 0) is chosen. Otherwise walks
        /// the candidate list, skipping any whose recording id equals the deferred Re-Fly id
        /// (counting them in <paramref name="deferredObserved"/>) and any whose recording a
        /// supply route sources from (<paramref name="routeSourceIds"/>, may be null; adding
        /// its id to <paramref name="heldForRoutes"/>, allocated on first use), and returns
        /// the first remaining candidate's index. Returns -1 when every remaining candidate
        /// is deferred or held. Pure read over the inputs.
        /// </summary>
        internal static int ChooseSplitCandidateIndex(
            IReadOnlyList<(int, int)> splitCandidates,
            IReadOnlyList<Recording> recordings,
            string deferredActiveReFlyId,
            HashSet<string> routeSourceIds,
            ref HashSet<string> heldForRoutes,
            out int deferredObserved)
        {
            deferredObserved = 0;
            if (string.IsNullOrEmpty(deferredActiveReFlyId)
                && (routeSourceIds == null || routeSourceIds.Count == 0))
            {
                return 0;
            }

            for (int c = 0; c < splitCandidates.Count; c++)
            {
                int candIdx = splitCandidates[c].Item1;
                if (candIdx < 0 || candIdx >= recordings.Count)
                    continue;
                var candRec = recordings[candIdx];
                if (IsRouteSource(candRec, routeSourceIds))
                {
                    if (heldForRoutes == null)
                        heldForRoutes = new HashSet<string>(StringComparer.Ordinal);
                    heldForRoutes.Add(candRec.RecordingId);
                    continue;
                }
                if (candRec != null
                    && !string.IsNullOrEmpty(deferredActiveReFlyId)
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
        internal static void CopySplitIdentityFields(Recording original, Recording second)
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
            second.SplitRuleVersion = original.SplitRuleVersion;
        }

        /// <summary>
        /// The trim pass. A recording a supply route sources from (<paramref name="routeSourceIds"/>)
        /// keeps its tail and is counted under the <c>route-source</c> skip category; null means
        /// no recording is held.
        /// </summary>
        internal static void TrimBoringTailsForOptimization(
            IReadOnlyList<Recording> recordings, HashSet<string> routeSourceIds = null)
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
            // Mirror of the split and merge holds: a superseded recording is not played and its
            // supersede row names its whole range, and a recording a live Re-Fly names (its
            // provisional, the origin, the supersede target) belongs to the merge in flight.
            var scenario = ParsekScenario.Instance;
            IReadOnlyList<RecordingSupersedeRelation> supersedes =
                !object.ReferenceEquals(null, scenario) ? scenario.RecordingSupersedes : null;
            string activeReFlyId = !object.ReferenceEquals(null, scenario)
                ? scenario.ActiveReFlySessionMarker?.ActiveReFlyRecordingId
                : null;
            Dictionary<string, string> heldByLiveReFlyMerge = CollectLiveReFlyMergeHeldIds();
            for (int i = 0; i < recordings.Count; i++)
            {
                double endBeforeTrim = recordings[i] != null ? recordings[i].EndUT : double.NaN;
                bool trimmed;
                string skipCategory;
                string heldBy = DescribeTrimHold(recordings[i], supersedes, activeReFlyId, heldByLiveReFlyMerge);
                if (IsRouteSource(recordings[i], routeSourceIds))
                {
                    trimmed = false;
                    skipCategory = "route-source";
                }
                else if (heldBy != null)
                {
                    trimmed = false;
                    skipCategory = heldBy;
                }
                else
                {
                    trimmed = RecordingOptimizer.TrimBoringTailInternal(
                        recordings[i],
                        recordings,
                        RecordingOptimizer.DefaultTailBufferSeconds,
                        logSkipReason: false,
                        skipCategory: out skipCategory);
                }
                if (trimmed)
                {
                    recordings[i].FilesDirty = true;
                    trimCount++;
                    // The trim keeps the snapshot captured at the old end: note that moment,
                    // so a spawn from it also takes out the route cargo of the trimmed tail.
                    ChainTipRouteCargo.NoteTrimmedSnapshotCapture(recordings[i], endBeforeTrim);
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
                string counts = FormatTrimSkipCounts(skipCounts, out int totalSkipped);
                ParsekLog.Verbose("RecordingStore",
                    $"Optimization pass: TrimBoringTail skipped {totalSkipped} recording(s) — " +
                    counts);
            }
        }

        /// <summary>
        /// The trim hold for <paramref name="rec"/>, or null: <c>superseded</c> when a supersede
        /// row names it (<see cref="RecordingOptimizer.IsSupersededForOptimizer"/>), <c>active-refly</c>
        /// when it is the live Re-Fly's provisional or a recording the live session or merge
        /// journal names (<paramref name="heldByLiveReFlyMerge"/>, built by
        /// <see cref="CollectLiveReFlyMergeHeldIds"/>; may be null).
        /// </summary>
        internal static string DescribeTrimHold(
            Recording rec,
            IReadOnlyList<RecordingSupersedeRelation> supersedes,
            string activeReFlyId,
            IReadOnlyDictionary<string, string> heldByLiveReFlyMerge)
        {
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) return null;
            if (RecordingOptimizer.IsSupersededForOptimizer(rec, supersedes))
                return "superseded";
            if (!string.IsNullOrEmpty(activeReFlyId)
                && string.Equals(rec.RecordingId, activeReFlyId, StringComparison.Ordinal))
                return "active-refly";
            if (heldByLiveReFlyMerge != null && heldByLiveReFlyMerge.ContainsKey(rec.RecordingId))
                return "active-refly";
            return null;
        }

        /// <summary>
        /// The per-category part of the boring-tail skip summary: <c>name=count</c> pairs
        /// joined by <c>", "</c>, by descending count, then ordinal name as tie-break, so
        /// equal-count categories do not reorder run to run (List&lt;T&gt;.Sort is not
        /// stable). <paramref name="totalSkipped"/> is the sum of the counts.
        /// </summary>
        internal static string FormatTrimSkipCounts(Dictionary<string, int> skipCounts, out int totalSkipped)
        {
            totalSkipped = 0;
            foreach (var n in skipCounts.Values) totalSkipped += n;
            var ordered = new List<KeyValuePair<string, int>>(skipCounts);
            ordered.Sort((a, b) =>
            {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
            var parts = new List<string>(ordered.Count);
            foreach (var kv in ordered) parts.Add($"{kv.Key}={kv.Value}");
            return string.Join(", ", parts);
        }

        /// <summary>
        /// Optimizer merge: every ledger row tagged with the absorbed recording moves to
        /// the target, which now covers the absorbed time range. The absorbed recording
        /// leaves the committed list, so a row left on its id is orphaned and the next
        /// load's <see cref="Ledger.Reconcile"/> prunes it, ActionId and all. This used
        /// to run only for an absorbed tree ROOT; any later segment can carry rows too
        /// (its own earnings, or the rows <see cref="RetagLedgerActionsAfterOptimizationSplit"/>
        /// moved onto it), which is the mirror of the split retag. The captured events
        /// (<see cref="GameStateStore.RetagEventsForRecording"/>) and the pending science
        /// subjects (<see cref="GameStateRecorder.RetagPendingScienceForRecording"/>) move
        /// with them (OPTIMIZER-MERGE-RETAGS-LEDGER-ROWS-ONLY).
        /// </summary>
        internal static void RetagLedgerActionsAfterOptimizationMerge(Recording target, Recording absorbed)
        {
            if (target == null || absorbed == null) return;
            int remapped = Ledger.RetagActionsForRecordingRewrite(
                absorbed.RecordingId, target.RecordingId);
            if (remapped > 0)
                ParsekLog.Info("RecordingStore",
                    $"Optimization merge: retagged {remapped} ledger action(s) from " +
                    $"absorbed '{absorbed.RecordingId}' to target '{target.RecordingId}'" +
                    (!string.IsNullOrEmpty(target.TreeId) ? $" (tree id='{target.TreeId}')" : ""));

            // The captured events and pending science subjects carry the same id (the split's
            // three carriers): at a fresh commit this pass runs before the converter, which reads
            // the tag as ownership, and id-keyed readers drop an event left on a removed id.
            if (string.IsNullOrEmpty(absorbed.RecordingId) || string.IsNullOrEmpty(target.RecordingId))
                return;
            int eventsRetagged = GameStateStore.RetagEventsForRecording(
                absorbed.RecordingId, target.RecordingId);
            int scienceRetagged = GameStateRecorder.RetagPendingScienceForRecording(
                absorbed.RecordingId, target.RecordingId);
            if (eventsRetagged > 0 || scienceRetagged > 0)
                ParsekLog.Info("RecordingStore",
                    $"Optimization merge: retagged {eventsRetagged.ToString(CultureInfo.InvariantCulture)} event(s) " +
                    $"and {scienceRetagged.ToString(CultureInfo.InvariantCulture)} pending science subject(s) " +
                    $"from absorbed '{absorbed.RecordingId}' to target '{target.RecordingId}'");
        }

        /// <summary>
        /// Optimizer split: moves everything attributed to the second half onto it, at
        /// the second half's first section start (which
        /// <see cref="RecordingOptimizer.SplitAtSection"/> sets to its split UT). Three
        /// carriers hold the original id, and all three are partitioned at the same cut:
        /// the ledger rows (<see cref="Ledger.RetagActionsForSplitSecondHalf"/>, the same
        /// predicate as the Re-Fly split's step 2.9), the captured events
        /// (<see cref="GameStateStore.RetagEventsForSplitSecondHalf"/>) and the pending
        /// science subjects (<see cref="GameStateRecorder.RetagPendingScienceForSplitSecondHalf"/>).
        /// The last two matter at a fresh commit, where this pass runs BEFORE
        /// <c>LedgerOrchestrator.NotifyLedgerTreeCommitted</c> converts the flight's events
        /// into rows: they are the rows-to-be, and the converter reads their tag as
        /// ownership (RETAG-ON-SPLIT-MISSES-LATER-ROWS).
        /// </summary>
        internal static void RetagLedgerActionsAfterOptimizationSplit(Recording original, Recording second)
        {
            if (original == null || second == null) return;
            double splitUT = ResolveSplitSecondHalfUT(second);
            int deathIntervalsByEndUT;
            int retagged = Ledger.RetagActionsForSplitSecondHalf(
                original.RecordingId, second.RecordingId, splitUT, out deathIntervalsByEndUT);
            if (retagged > 0)
                ParsekLog.Info("RecordingStore",
                    $"Optimization split: retagged {retagged} ledger action(s) from " +
                    $"'{original.RecordingId}' to second half '{second.RecordingId}' " +
                    $"(deathIntervalsByEndUT={deathIntervalsByEndUT})");

            Ledger.AddSeedPendingRecordingForSplit(original.RecordingId, second.RecordingId);
            int eventsRetagged = GameStateStore.RetagEventsForSplitSecondHalf(
                original.RecordingId, second.RecordingId, splitUT);
            int scienceRetagged = GameStateRecorder.RetagPendingScienceForSplitSecondHalf(
                original.RecordingId, second.RecordingId, splitUT);
            if (eventsRetagged > 0 || scienceRetagged > 0)
                ParsekLog.Info("RecordingStore",
                    $"Optimization split: retagged {eventsRetagged.ToString(CultureInfo.InvariantCulture)} event(s) " +
                    $"and {scienceRetagged.ToString(CultureInfo.InvariantCulture)} pending science subject(s) " +
                    $"from '{original.RecordingId}' to second half '{second.RecordingId}' " +
                    $"(splitUT={splitUT.ToString("R", CultureInfo.InvariantCulture)})");
        }

        /// <summary>
        /// The cut a split's ledger, event and science retags partition at: the second
        /// half's first section start (which <see cref="RecordingOptimizer.SplitAtSection"/>
        /// sets to its split UT), else its <see cref="Recording.StartUT"/>.
        /// </summary>
        internal static double ResolveSplitSecondHalfUT(Recording second)
        {
            return second.TrackSections != null && second.TrackSections.Count > 0
                ? second.TrackSections[0].startUT
                : second.StartUT;
        }

        /// <summary>
        /// Derives each split half's <see cref="Recording.SegmentBodyName"/> from its first
        /// trajectory point; a half with no points keeps the name it has.
        /// </summary>
        internal static void DeriveSplitSegmentBodyNames(Recording original, Recording second)
        {
            if (original.Points != null && original.Points.Count > 0)
                original.SegmentBodyName = original.Points[0].bodyName;
            if (second.Points != null && second.Points.Count > 0)
                second.SegmentBodyName = second.Points[0].bodyName;
        }

        /// <summary>
        /// Puts the original's <see cref="Recording.ChildBranchPointId"/> on the half whose
        /// time range owns it: moved to the second half (and cleared on the first) when
        /// <paramref name="movesToSecond"/>, else the second half carries none.
        /// </summary>
        internal static void AssignSplitChildBranchPoint(Recording original, Recording second, bool movesToSecond)
        {
            if (movesToSecond)
            {
                second.ChildBranchPointId = original.ChildBranchPointId;
                original.ChildBranchPointId = null;
            }
            else
            {
                second.ChildBranchPointId = null;
            }
        }

        /// <summary>
        /// Adds (or replaces) <paramref name="rec"/> in the first committed tree whose id is
        /// <paramref name="treeId"/>; a null or empty id, or no such tree, changes nothing.
        /// </summary>
        internal static void AddToCommittedTree(string treeId, Recording rec)
        {
            if (!string.IsNullOrEmpty(treeId))
            {
                for (int t = 0; t < RecordingStore.CommittedTrees.Count; t++)
                {
                    if (RecordingStore.CommittedTrees[t].Id == treeId)
                    {
                        RecordingStore.CommittedTrees[t].AddOrReplaceRecording(rec);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// After a split of <paramref name="original"/> into itself (the earlier half) and
        /// <paramref name="second"/> (the later half): when the tree's active recording is the
        /// original, it becomes the second half, where the vessel's flight continues. Returns
        /// true when it moved.
        /// </summary>
        internal static bool MoveActiveRecordingToSplitTail(
            RecordingTree tree, Recording original, Recording second)
        {
            if (tree == null || original == null || second == null
                || string.IsNullOrEmpty(original.RecordingId)
                || string.IsNullOrEmpty(second.RecordingId))
                return false;
            if (!string.Equals(tree.ActiveRecordingId, original.RecordingId, StringComparison.Ordinal))
                return false;
            tree.ActiveRecordingId = second.RecordingId;
            ParsekLog.Info("RecordingStore",
                $"Optimization split: tree={tree.Id} active recording moved from the head " +
                $"'{original.RecordingId}' to the split tail '{second.RecordingId}'");
            return true;
        }

        /// <summary>
        /// Owner RULE 3: folds a committed split piece back into the live recording of a
        /// committed vessel flown again (the reverse of the optimizer split that cut it off while
        /// the live copy held the recording whole; <c>TreeCommitService.TryUnionFlownAgainCopy</c>).
        /// The optimizer merge's own steps: the ledger rows, events and pending science tagged to
        /// <paramref name="absorbed"/> are retagged to <paramref name="survivor"/>, an awaiting
        /// stock payout moves, every Relative section anchor and parent anchor naming it is
        /// re-pointed, the tree drops it and its branch points and children name the survivor,
        /// and it leaves the committed list through <see cref="RecordingStore.RemoveCommittedAt"/>
        /// (the index notifications fire). Its sidecar files are left to the orphan collector.
        /// </summary>
        internal static void ReabsorbSplitPieceIntoLiveCopy(Recording survivor, Recording absorbed, string context)
        {
            if (survivor == null || absorbed == null || ReferenceEquals(survivor, absorbed))
                return;
            var recordings = RecordingStore.CommittedRecordings;
            int index = -1;
            Recording mergeTarget = survivor;
            for (int i = 0; i < recordings.Count; i++)
            {
                if (ReferenceEquals(recordings[i], absorbed))
                    index = i;
                else if (recordings[i] != null
                         && string.Equals(recordings[i].RecordingId, survivor.RecordingId, StringComparison.Ordinal))
                    mergeTarget = recordings[i];
            }

            // The piece was the chain tip, where later writes land (a Seal, the spawn stamp
            // MoveChainSpawnStampsToTip moves there, settings set on its row). They go onto the
            // committed copy of the survivor's id: the commit's replace step then hands the
            // committed copy's player settings and runtime fields to the live recording.
            string carried = TreeCommitService.CarryTipOwnedStateOnFold(absorbed, mergeTarget);

            RetagLedgerActionsAfterOptimizationMerge(survivor, absorbed);
            InFlightRecoveryRequest.MoveAwaitingStockPayout(
                absorbed.RecordingId, survivor.RecordingId, context);
            RepointAnchorsAfterOptimizationMerge(recordings, survivor, absorbed);
            UpdateTreeStateAfterOptimizationMerge(survivor, absorbed);
            if (index >= 0)
                RecordingStore.RemoveCommittedAt(index, mergeTarget);
            ReReadSurvivorRoleAfterFold(survivor, mergeTarget);
            ParsekLog.Info("RecordingStore",
                $"CommitTree: re-absorbed split piece '{absorbed.RecordingId}' into '{survivor.RecordingId}' " +
                $"(tree={survivor.TreeId ?? "<none>"} " +
                $"pieceSpan=[{absorbed.StartUT.ToString("F1", CultureInfo.InvariantCulture)}," +
                $"{absorbed.EndUT.ToString("F1", CultureInfo.InvariantCulture)}] " +
                $"liveSpan=[{survivor.StartUT.ToString("F1", CultureInfo.InvariantCulture)}," +
                $"{survivor.EndUT.ToString("F1", CultureInfo.InvariantCulture)}] " +
                $"removedFromCommittedList={index >= 0} carried=[{carried}] context={context ?? "<none>"}) - " +
                "the live recording covers it");
        }

        /// <summary>
        /// After a fold the survivor holds the piece's span too, so a version-2 row role it
        /// carried can contradict its content (a reentry row that now ends back in space): the
        /// role is read again from the content (<see cref="RecordingOptimizer.ResolveHeadRoleAfterCut"/>),
        /// on the live recording and on the committed copy of its id. The next optimization pass
        /// re-cuts the survivor where the version-2 rules cut, and its merge guard
        /// (<see cref="RecordingOptimizer.WouldSplitRulesCutJoin"/>) never re-joins such a cut.
        /// One Info line when the role changed.
        /// </summary>
        internal static void ReReadSurvivorRoleAfterFold(Recording survivor, Recording mergeTarget)
        {
            if (survivor == null || !RecordingOptimizer.UsesCurrentSplitRules(survivor)) return;
            string before = survivor.SegmentRole;
            string after = RecordingOptimizer.ResolveHeadRoleAfterCut(survivor);
            survivor.SegmentRole = after;
            if (mergeTarget != null && !ReferenceEquals(mergeTarget, survivor))
                mergeTarget.SegmentRole = after;
            if (!string.Equals(before, after, StringComparison.Ordinal))
                ParsekLog.Info("RecordingStore",
                    $"CommitTree: '{survivor.RecordingId}' role re-read after the fold: " +
                    $"{before ?? "<none>"} -> {after ?? "<none>"}");
        }

        /// <summary>
        /// Why a covered split piece must NOT be folded into a re-flown live recording, or null
        /// when it may be: a supersede row names it (either side: the fold would leave the row
        /// pointing at nothing), or a supply route sources from it (the optimizer merge holds
        /// route sources the same way, <see cref="ChooseMergeCandidateIndex"/>), or the piece and
        /// <paramref name="survivor"/> (the live recording it would fold into; may be null) were
        /// cut under different split rule versions (owner ruling Q5: the merge pass never joins
        /// those either, <see cref="RecordingOptimizer.SplitRulesKeepApart"/>).
        /// </summary>
        internal static string SplitPieceFoldRefusal(Recording piece, Recording survivor = null)
        {
            if (piece == null || string.IsNullOrEmpty(piece.RecordingId))
                return null;
            if (survivor != null)
            {
                int pieceVersion = RecordingOptimizer.EffectiveSplitRuleVersion(piece);
                int survivorVersion = RecordingOptimizer.EffectiveSplitRuleVersion(survivor);
                if (pieceVersion != survivorVersion)
                    return "it was cut under split rules v" + pieceVersion.ToString(CultureInfo.InvariantCulture)
                        + " and the live recording carries v" + survivorVersion.ToString(CultureInfo.InvariantCulture);
            }
            var supersedes = ParsekScenario.Instance?.RecordingSupersedes;
            if (supersedes != null)
            {
                for (int i = 0; i < supersedes.Count; i++)
                {
                    var row = supersedes[i];
                    if (row == null) continue;
                    if (string.Equals(row.OldRecordingId, piece.RecordingId, StringComparison.Ordinal)
                        || string.Equals(row.NewRecordingId, piece.RecordingId, StringComparison.Ordinal))
                        return "a supersede row names it (relation=" + (row.RelationId ?? "<no-id>") + ")";
                }
            }
            if (IsRouteSource(piece, Logistics.RouteStore.CollectSourceRecordingIds()))
                return "a supply route sources from it (route source)";
            return null;
        }

        internal static void UpdateTreeStateAfterOptimizationMerge(Recording target, Recording absorbed)
        {
            string treeId = target != null && !string.IsNullOrEmpty(target.TreeId)
                ? target.TreeId
                : absorbed?.TreeId;
            if (string.IsNullOrEmpty(treeId) || absorbed == null)
                return;

            for (int t = 0; t < RecordingStore.CommittedTrees.Count; t++)
            {
                var tree = RecordingStore.CommittedTrees[t];
                if (tree.Id != treeId)
                    continue;

                tree.Recordings.Remove(absorbed.RecordingId);
                if (target != null)
                    tree.AddOrReplaceRecording(target);

                // Ledger rows tagged with the absorbed id were already retagged by
                // RetagLedgerActionsAfterOptimizationMerge, root or not.
                if (tree.RootRecordingId == absorbed.RecordingId && target != null)
                    tree.RootRecordingId = target.RecordingId;
                if (tree.ActiveRecordingId == absorbed.RecordingId && target != null)
                    tree.ActiveRecordingId = target.RecordingId;

                // The merge reverses a split: every branch point naming the absorbed
                // recording as a parent (its ChildBranchPointId, a decouple flown past, a
                // dock, an EVA) and every child linked to it now names the survivor.
                if (target != null)
                    SplitParentLinks.RepointToSurvivor(
                        tree, absorbed.RecordingId, target.RecordingId, "Optimization merge");

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
        internal static void PopulateLoopSyncParentIndices(IReadOnlyList<Recording> recordings)
        {
            if (recordings == null) return;

            int linked = 0;
            int linkedByAnchor = 0;
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

                string via;
                int parentIdx = FindLoopSyncParentIndex(recordings, i, out via);
                rec.LoopSyncParentIdx = parentIdx;
                if (parentIdx >= 0)
                {
                    linked++;
                    if (via != "covering") linkedByAnchor++;
                }
            }

            if (linked > 0)
                ParsekLog.Info("RecordingStore",
                    $"Loop sync: linked {linked} debris recording(s) to parent recordings " +
                    $"(byParentAnchor={linkedByAnchor.ToString(CultureInfo.InvariantCulture)})");
        }

        /// <summary>
        /// The committed index of the recording debris <c>recordings[debrisIdx]</c> loops with,
        /// or -1. The debris's own parent (<see cref="Recording.ParentAnchorRecordingId"/>) wins
        /// when it covers the debris's start (<paramref name="via"/> = <c>parent-anchor</c>), then
        /// the piece of the parent's chain that covers it (an optimizer split moved the debris's
        /// birth onto another piece; <c>parent-anchor-chain</c>); otherwise the first non-debris
        /// recording of the same tree, of another vessel, whose range covers the debris's start
        /// (<c>covering</c>, the rule for debris with no anchor).
        /// </summary>
        internal static int FindLoopSyncParentIndex(
            IReadOnlyList<Recording> recordings, int debrisIdx, out string via)
        {
            via = null;
            var rec = recordings[debrisIdx];
            double debrisStart = rec.StartUT;
            Func<Recording, bool> covers = c =>
                debrisStart >= c.StartUT && debrisStart <= c.EndUT;

            if (!string.IsNullOrEmpty(rec.ParentAnchorRecordingId))
            {
                int anchorIdx = RecordingStore.IndexOfRecordingId(recordings, rec.ParentAnchorRecordingId);
                Recording anchor = anchorIdx >= 0 ? recordings[anchorIdx] : null;
                if (anchor != null && anchorIdx != debrisIdx && !anchor.IsDebris)
                {
                    if (covers(anchor))
                    {
                        via = "parent-anchor";
                        return anchorIdx;
                    }
                    if (!string.IsNullOrEmpty(anchor.ChainId))
                    {
                        for (int j = 0; j < recordings.Count; j++)
                        {
                            var piece = recordings[j];
                            if (j == debrisIdx || piece == null || piece.IsDebris) continue;
                            if (!string.Equals(piece.ChainId, anchor.ChainId, StringComparison.Ordinal)) continue;
                            if (!covers(piece)) continue;
                            via = "parent-anchor-chain";
                            return j;
                        }
                    }
                }
            }

            for (int j = 0; j < recordings.Count; j++)
            {
                if (j == debrisIdx) continue;
                var candidate = recordings[j];
                if (candidate.IsDebris) continue;
                if (candidate.TreeId != rec.TreeId) continue;
                if (candidate.VesselPersistentId != rec.VesselPersistentId && covers(candidate))
                {
                    via = "covering";
                    return j;
                }
            }
            return -1;
        }
    }
}
