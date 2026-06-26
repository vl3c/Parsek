using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    internal static partial class GhostPlaybackLogic
    {
        #region Watch Mode Decisions

        /// <summary>
        /// Determines whether the watch mode should auto-exit because the ghost
        /// exceeded the camera cutoff distance.
        /// </summary>
        internal static bool ShouldExitWatchForCutoff(double ghostDistanceMeters, float cutoffKm)
        {
            return ghostDistanceMeters >= cutoffKm * 1000.0;
        }

        /// <summary>
        /// Determines whether a ghost is within the camera cutoff distance
        /// (eligible for Watch button). Only checks distance against the
        /// user-configurable cutoff — zone is irrelevant because watch mode
        /// moves the camera to the ghost (T39).
        /// </summary>
        internal static bool IsWithinWatchRange(double distanceMeters, float cutoffKm)
        {
            return distanceMeters < cutoffKm * 1000.0;
        }

        /// <summary>
        /// Resolves the effective watch target for a source recording by walking the
        /// preferred continuation lineage until an active ghost is found.
        /// Returns the source index itself when its ghost is still active, otherwise
        /// follows chain/tree continuation rules through inactive intermediates.
        /// Used by aggregate UI affordances that should continue tracking the live
        /// vessel after watch auto-follow hands off to a descendant segment.
        /// </summary>
        internal static int ResolveEffectiveWatchTargetIndex(
            int sourceIndex,
            IReadOnlyList<Recording> committed,
            IReadOnlyList<RecordingTree> trees,
            Func<int, bool> isGhostActive)
        {
            return ResolveEffectiveWatchTargetIndex(
                sourceIndex,
                committed,
                trees,
                isGhostActive,
                new HashSet<int>(),
                depth: 0);
        }

        private static int ResolveEffectiveWatchTargetIndex(
            int sourceIndex,
            IReadOnlyList<Recording> committed,
            IReadOnlyList<RecordingTree> trees,
            Func<int, bool> isGhostActive,
            HashSet<int> visited,
            int depth)
        {
            const int MaxRecursionDepth = 16;
            if (committed == null
                || visited == null
                || sourceIndex < 0
                || sourceIndex >= committed.Count
                || depth > MaxRecursionDepth
                || !visited.Add(sourceIndex))
            {
                return -1;
            }

            if (isGhostActive != null && isGhostActive(sourceIndex))
                return sourceIndex;

            Recording currentRec = committed[sourceIndex];
            if (currentRec == null)
                return -1;

            int nextChainIndex = FindImmediateChainContinuationIndex(currentRec, committed);
            if (nextChainIndex >= 0)
            {
                int resolvedChainIndex = ResolveEffectiveWatchTargetIndex(
                    nextChainIndex,
                    committed,
                    trees,
                    isGhostActive,
                    visited,
                    depth + 1);
                if (resolvedChainIndex >= 0)
                    return resolvedChainIndex;
            }

            return ResolveEffectiveTreeWatchTargetIndex(
                currentRec,
                committed,
                trees,
                isGhostActive,
                visited,
                depth);
        }

        private static int FindImmediateChainContinuationIndex(
            Recording currentRec,
            IReadOnlyList<Recording> committed)
        {
            if (currentRec == null
                || committed == null
                || string.IsNullOrEmpty(currentRec.ChainId)
                || currentRec.ChainIndex < 0
                || currentRec.ChainBranch != 0)
            {
                return -1;
            }

            int nextChainIndex = currentRec.ChainIndex + 1;
            for (int i = 0; i < committed.Count; i++)
            {
                Recording candidate = committed[i];
                if (candidate == null)
                    continue;

                if (candidate.ChainId == currentRec.ChainId
                    && candidate.ChainBranch == 0
                    && candidate.ChainIndex == nextChainIndex)
                {
                    return i;
                }
            }

            return -1;
        }

        private static int ResolveEffectiveTreeWatchTargetIndex(
            Recording currentRec,
            IReadOnlyList<Recording> committed,
            IReadOnlyList<RecordingTree> trees,
            Func<int, bool> isGhostActive,
            HashSet<int> visited,
            int depth)
        {
            if (currentRec == null
                || committed == null
                || trees == null
                || string.IsNullOrEmpty(currentRec.ChildBranchPointId)
                || !currentRec.IsTreeRecording)
            {
                return -1;
            }

            RecordingTree tree = FindTreeById(trees, currentRec.TreeId);
            BranchPoint branchPoint = FindBranchPointById(tree, currentRec.ChildBranchPointId);
            if (branchPoint == null)
                return -1;

            bool pidMatchFound = false;
            for (int i = 0; i < branchPoint.ChildRecordingIds.Count; i++)
            {
                int childIndex = FindRecordingIndexById(committed, branchPoint.ChildRecordingIds[i]);
                if (childIndex < 0)
                    continue;

                Recording child = committed[childIndex];
                if (child == null || child.VesselPersistentId != currentRec.VesselPersistentId)
                    continue;

                pidMatchFound = true;
                int resolvedChildIndex = ResolveEffectiveWatchTargetIndex(
                    childIndex,
                    committed,
                    trees,
                    isGhostActive,
                    visited,
                    depth + 1);
                if (resolvedChildIndex >= 0)
                    return resolvedChildIndex;
            }

            // Mirror watch auto-follow: once a same-PID continuation exists, do not
            // fall through to other branches if that lineage has no active target yet.
            if (pidMatchFound || branchPoint.Type == BranchPointType.Breakup)
                return -1;

            for (int i = 0; i < branchPoint.ChildRecordingIds.Count; i++)
            {
                int childIndex = FindRecordingIndexById(committed, branchPoint.ChildRecordingIds[i]);
                if (childIndex < 0)
                    continue;

                Recording child = committed[childIndex];
                if (child == null || child.IsDebris)
                    continue;

                // Mirror FindNextWatchTarget exactly for non-PID fallback:
                // only an immediate active non-debris child is a valid target.
                if (isGhostActive != null && isGhostActive(childIndex))
                    return childIndex;
            }

            return -1;
        }

        /// <summary>
        /// Bug #382: result of advancing a group's watch-rotation cursor. Returned by
        /// <see cref="AdvanceGroupWatchCursor"/>. When <see cref="NextRecordingId"/> is
        /// null there are no eligible descendants in the group (button should be
        /// disabled). When <see cref="IsToggleOff"/> is true the only eligible
        /// descendant is the one currently being watched, and the caller should call
        /// ExitWatchMode rather than re-enter the same target.
        /// </summary>
        internal readonly struct GroupWatchAdvanceResult
        {
            public readonly string NextRecordingId;   // null == empty eligible set
            public readonly int Position;             // 1-based index in eligible list (0 when NextRecordingId is null)
            public readonly int TotalEligible;        // count of eligible descendants
            public readonly bool IsToggleOff;         // single-entry rotation where that entry IS currently watched
            public readonly bool IsWrap;              // advance wrapped past the end of the eligible list

            public GroupWatchAdvanceResult(string nextId, int pos, int total, bool toggleOff, bool wrap)
            {
                NextRecordingId = nextId;
                Position = pos;
                TotalEligible = total;
                IsToggleOff = toggleOff;
                IsWrap = wrap;
            }

            public static GroupWatchAdvanceResult Empty => new GroupWatchAdvanceResult(null, 0, 0, false, false);
        }

        /// <summary>
        /// Bug #382: advances the rotation cursor for a group's W button. Builds a
        /// stable eligible list from <paramref name="descendants"/> (sorted by
        /// <c>StartUT</c> ascending, with <c>RecordingId</c> ordinal ascending as
        /// a deterministic tiebreaker), locates <paramref name="cursorRecordingId"/>
        /// in that list, and advances one step forward (wrapping) to the first entry
        /// whose <c>RecordingId</c> differs from <paramref name="currentlyWatchedRecId"/>.
        ///
        /// The <paramref name="isEligible"/> predicate is the single source of truth
        /// for "watchable" — callers are expected to fold
        /// <c>hasGhost &amp;&amp; sameBody &amp;&amp; inRange &amp;&amp; !IsDebris</c>
        /// (plus any non-null RecordingId filter they need) into it.
        ///
        /// If every eligible entry equals <paramref name="currentlyWatchedRecId"/>
        /// (only possible when the rotation reduces to a single entry and that entry
        /// is already the watched one), the result has
        /// <see cref="GroupWatchAdvanceResult.IsToggleOff"/> = true and
        /// <see cref="GroupWatchAdvanceResult.NextRecordingId"/> set to the watched
        /// id, so the caller can detect the identity and invoke ExitWatchMode.
        /// </summary>
        internal static GroupWatchAdvanceResult AdvanceGroupWatchCursor(
            HashSet<int> descendants,
            IReadOnlyList<Recording> committed,
            Func<int, bool> isEligible,
            string cursorRecordingId,
            string currentlyWatchedRecId)
        {
            if (descendants == null || committed == null || isEligible == null || descendants.Count == 0)
                return GroupWatchAdvanceResult.Empty;

            // 1. Build eligible list. Only Recording refs are needed from here on;
            // the UI re-resolves index by RecordingId after the call.
            var eligible = new List<Recording>(descendants.Count);
            foreach (int idx in descendants)
            {
                if (idx < 0 || idx >= committed.Count) continue;
                var rec = committed[idx];
                if (rec == null) continue;
                if (string.IsNullOrEmpty(rec.RecordingId)) continue;
                if (!isEligible(idx)) continue;
                eligible.Add(rec);
            }

            if (eligible.Count == 0)
                return GroupWatchAdvanceResult.Empty;

            // 2. Stable sort: StartUT asc, then RecordingId ordinal asc.
            eligible.Sort((a, b) =>
            {
                int c = a.StartUT.CompareTo(b.StartUT);
                return c != 0 ? c : string.CompareOrdinal(a.RecordingId, b.RecordingId);
            });
            int count = eligible.Count;

            // 3. Locate cursor by RecordingId. -1 means "before-first".
            int cursorPos = -1;
            if (!string.IsNullOrEmpty(cursorRecordingId))
            {
                for (int i = 0; i < count; i++)
                {
                    if (eligible[i].RecordingId == cursorRecordingId)
                    {
                        cursorPos = i;
                        break;
                    }
                }
            }

            // 4. Walk forward, wrapping, skipping the currently-watched id.
            for (int step = 1; step <= count; step++)
            {
                // (((cursorPos + step) % count) + count) % count handles the
                // cursorPos=-1 "before-first" sentinel: with step=1 the first
                // probe is 0 (first eligible entry). The double-mod is
                // defensive against any future negative inputs.
                int probe = ((cursorPos + step) % count + count) % count;
                var candidate = eligible[probe];
                if (candidate.RecordingId != currentlyWatchedRecId)
                {
                    // stepping wrapped past end: probe index is at or before cursorPos.
                    bool wrap = cursorPos >= 0 && probe <= cursorPos;
                    return new GroupWatchAdvanceResult(candidate.RecordingId, probe + 1, count, toggleOff: false, wrap: wrap);
                }
            }

            // 5. All eligible entries equal currentlyWatchedRecId → single-entry rotation
            //    that IS watched. Return the watched id with IsToggleOff = true.
            //    Because step 1's filter rejects rows with null/empty RecordingId, every
            //    candidate.RecordingId in the loop is a non-null string. If
            //    currentlyWatchedRecId is null, every iteration's inequality check is true
            //    and the loop returns on the first probe. So this trailing return only
            //    fires when currentlyWatchedRecId is a non-null string equal to every
            //    eligible entry — guaranteed-safe toggle-off.
            return new GroupWatchAdvanceResult(currentlyWatchedRecId, 1, count, toggleOff: true, wrap: false);
        }

        internal static int FindRecordingIndexById(
            IReadOnlyList<Recording> committed,
            string recordingId)
        {
            if (committed == null || string.IsNullOrEmpty(recordingId))
                return -1;

            for (int i = 0; i < committed.Count; i++)
            {
                Recording candidate = committed[i];
                if (candidate != null && candidate.RecordingId == recordingId)
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Resolves the slot index of the chain continuation for the given
        /// slot, or -1 if none. Used by
        /// <see cref="ChainHandoffLogic"/> via the engine callback wired in
        /// <see cref="ParsekPlaybackPolicy"/> to coordinate the chain-seam
        /// handoff between a chain HEAD and its continuation.
        ///
        /// <para>Resolution shape mirrors <see cref="FindNextWatchTarget"/>'s
        /// Case 1 chain-next lookup: same ChainId, ChainBranch == 0, ChainIndex
        /// == current + 1; result is supersede-walked through
        /// <see cref="ResolveSupersedeIndex"/> so a continuation that was
        /// superseded by a fork resolves to the fork's index.</para>
        ///
        /// <para>Returns -1 on any of: invalid <paramref name="slotIndex"/>;
        /// the slot's recording has no ChainId or is on a parallel branch
        /// (ChainBranch &gt; 0); no candidate matches the next index;
        /// supersede resolution lands back on the same slot (defensive,
        /// indicates a cycle or self-supersede edge).</para>
        /// </summary>
        internal static int ResolveChainNextSlotIndex(
            int slotIndex,
            IReadOnlyList<Recording> committed,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (committed == null || slotIndex < 0 || slotIndex >= committed.Count)
                return -1;
            Recording current = committed[slotIndex];
            if (current == null
                || string.IsNullOrEmpty(current.ChainId)
                || current.ChainIndex < 0
                || current.ChainBranch != 0)
                return -1;
            int nextChainIndex = current.ChainIndex + 1;
            // Ordinal string compare here is deliberately stricter than
            // FindNextWatchTarget's plain `==` operator (same effect for
            // System.String today, but Ordinal is unambiguous about the
            // contract — chain ids are opaque guid-like tokens, never
            // locale-sensitive). Same convention as
            // EffectiveState.ResolveChainTerminalRecording.
            int firstMatchIdx = -1;
            for (int j = 0; j < committed.Count; j++)
            {
                Recording candidate = committed[j];
                if (candidate == null) continue;
                if (!string.Equals(candidate.ChainId, current.ChainId, StringComparison.Ordinal))
                    continue;
                if (candidate.ChainBranch != 0) continue;
                if (candidate.ChainIndex != nextChainIndex) continue;
                if (firstMatchIdx < 0)
                {
                    firstMatchIdx = j;
                    continue;
                }
                // Two committed recordings sharing (ChainId, ChainBranch=0,
                // ChainIndex+1) is a data anomaly — chain coordinates are
                // supposed to be a 1:1 identifier within a tree. Warn the
                // first time it shows up per (chain, index) so the recorder
                // bug or merge conflict that produced the duplicate gets a
                // breadcrumb in KSP.log without spamming on every frame.
                ParsekLog.WarnRateLimited(
                    "ChainHandoff",
                    "duplicate-chain-next-" + current.ChainId + "-" + nextChainIndex.ToString(CultureInfo.InvariantCulture),
                    "ResolveChainNextSlotIndex: duplicate branch-0 successor at chainId=" + current.ChainId
                        + " chainIndex=" + nextChainIndex.ToString(CultureInfo.InvariantCulture)
                        + " firstMatch=" + firstMatchIdx.ToString(CultureInfo.InvariantCulture)
                        + " duplicateMatch=" + j.ToString(CultureInfo.InvariantCulture)
                        + " — picking firstMatch; recorder produced a chain-index collision",
                    30.0);
                break;
            }
            if (firstMatchIdx < 0) return -1;
            int resolvedIdx = ResolveSupersedeIndex(firstMatchIdx, committed, supersedes);
            if (resolvedIdx < 0 || resolvedIdx == slotIndex)
                return -1;
            return resolvedIdx;
        }

        // Re-fly forks attach the canonical post-rewind continuation under a
        // tree branch point and emit a RECORDING_SUPERSEDES row pointing the
        // pre-rewind chain-next at the fork. The chain-next slot is then
        // skipped from playback (skip=superseded-by-relation) while the fork
        // (which is NOT a chain member of the rewind origin) carries the live
        // ghost. Watch-target search has to follow that supersede edge or it
        // sees only the inactive chain slot and returns -1.
        internal static int ResolveSupersedeIndex(
            int candidateIdx,
            IReadOnlyList<Recording> committed,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (committed == null || candidateIdx < 0 || candidateIdx >= committed.Count)
                return candidateIdx;
            if (supersedes == null || supersedes.Count == 0)
                return candidateIdx;
            Recording rec = committed[candidateIdx];
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return candidateIdx;
            string effectiveId = EffectiveState.EffectiveRecordingId(rec.RecordingId, supersedes);
            if (string.IsNullOrEmpty(effectiveId)
                || string.Equals(effectiveId, rec.RecordingId, StringComparison.Ordinal))
            {
                return candidateIdx;
            }
            int resolvedIdx = FindRecordingIndexById(committed, effectiveId);
            return resolvedIdx >= 0 ? resolvedIdx : candidateIdx;
        }

        /// <summary>
        /// Searches committed recordings for the next watch target after the current
        /// recording completes. Handles chain continuation (same chainId, next index)
        /// and tree branching (childBranchPointId → child with same vessel PID).
        /// When <paramref name="supersedes"/> is non-null, each chain-next / tree-child
        /// candidate is resolved through the supersede graph before the activity check,
        /// so a re-fly fork still produces a watch target after the original chain
        /// slot was superseded out of playback.
        /// </summary>
        /// <param name="currentRec">The recording that just completed.</param>
        /// <param name="committed">All committed recordings.</param>
        /// <param name="trees">All committed trees (for branch point lookup).</param>
        /// <param name="isGhostActive">Predicate: is there an active ghost at index j?</param>
        /// <param name="supersedes">RecordingSupersede relations (nullable; live callers pass <c>ParsekScenario.RecordingSupersedes</c>).</param>
        /// <param name="depth">Internal recursion depth.</param>
        /// <returns>Index into committed, or -1 if no target found.</returns>
        internal static int FindNextWatchTarget(
            Recording currentRec,
            IReadOnlyList<Recording> committed,
            IReadOnlyList<RecordingTree> trees,
            Func<int, bool> isGhostActive,
            IReadOnlyList<RecordingSupersedeRelation> supersedes = null,
            int depth = 0)
        {
            const int MaxRecursionDepth = 10;
            if (currentRec == null || committed == null || depth > MaxRecursionDepth) return -1;

            // Case 1: Chain continuation (same chainId, next chainIndex, branch 0)
            if (!string.IsNullOrEmpty(currentRec.ChainId) && currentRec.ChainIndex >= 0
                && currentRec.ChainBranch == 0)
            {
                int nextChainIndex = currentRec.ChainIndex + 1;
                for (int j = 0; j < committed.Count; j++)
                {
                    var candidate = committed[j];
                    if (candidate.ChainId == currentRec.ChainId
                        && candidate.ChainBranch == 0
                        && candidate.ChainIndex == nextChainIndex)
                    {
                        int resolvedIdx = ResolveSupersedeIndex(j, committed, supersedes);
                        if (resolvedIdx >= 0 && isGhostActive(resolvedIdx))
                            return resolvedIdx;
                    }
                }
            }

            // Case 2: Tree branching via ChildBranchPointId
            if (!string.IsNullOrEmpty(currentRec.ChildBranchPointId)
                && currentRec.IsTreeRecording
                && trees != null)
            {
                BranchPoint bp = null;
                for (int t = 0; t < trees.Count; t++)
                {
                    var tree = trees[t];
                    if (tree.Id != currentRec.TreeId) continue;
                    for (int b = 0; b < tree.BranchPoints.Count; b++)
                    {
                        if (tree.BranchPoints[b].Id == currentRec.ChildBranchPointId)
                        {
                            bp = tree.BranchPoints[b];
                            break;
                        }
                    }
                    break;
                }

                if (bp != null)
                {
                    int fallbackIdx = -1;
                    bool pidMatchFound = false;
                    bool allowDifferentPidFallback = bp.Type != BranchPointType.Breakup;
                    bool blockedDifferentPidActiveChildFound = false;
                    for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                    {
                        string childId = bp.ChildRecordingIds[c];
                        for (int j = 0; j < committed.Count; j++)
                        {
                            if (committed[j].RecordingId != childId) continue;

                            int resolvedIdx = ResolveSupersedeIndex(j, committed, supersedes);
                            if (resolvedIdx < 0 || resolvedIdx >= committed.Count)
                                continue;
                            Recording resolved = committed[resolvedIdx];
                            if (resolved == null) continue;

                            // PID-match keys off the *resolved* recording so we
                            // ask "does the fork represent the same vessel as
                            // currentRec?" rather than "did the now-stale
                            // chain slot start with the same vessel?". In-place
                            // re-fly continuations inherit the origin's PID;
                            // the new-recording re-fly path may carry a different
                            // PID, and only the resolved id reflects what is
                            // actually rendering.
                            bool isPidMatch = resolved.VesselPersistentId == currentRec.VesselPersistentId;

                            if (isGhostActive(resolvedIdx))
                            {
                                // Prefer child with same vessel PID (same vessel continues)
                                if (isPidMatch)
                                    return resolvedIdx;

                                // Bug #321: breakup/crash watch recovery should stay with
                                // the preserved live vessel context unless the same vessel
                                // actually continues. Non-breakup branches may still fall
                                // back to the first active non-debris child.
                                if (allowDifferentPidFallback
                                    && !resolved.IsDebris
                                    && fallbackIdx < 0)
                                {
                                    fallbackIdx = resolvedIdx;
                                }
                                else if (!allowDifferentPidFallback)
                                {
                                    blockedDifferentPidActiveChildFound = true;
                                }
                            }
                            else if (isPidMatch)
                            {
                                // #158: PID-matched continuation has no ghost (boundary seed
                                // with insufficient data). Recursively descend through its
                                // children to find a deeper target with an active ghost.
                                pidMatchFound = true;
                                int deeper = FindNextWatchTarget(
                                    resolved, committed, trees, isGhostActive, supersedes, depth + 1);
                                if (deeper >= 0)
                                    return deeper;
                            }
                        }
                    }
                    // #158: If we found the PID-matched continuation but it (and its
                    // descendants) have no ghost, don't fall through to debris — there's
                    // no good target. The watch hold timer will expire naturally.
                    if (pidMatchFound)
                        return -1;
                    if (!allowDifferentPidFallback && blockedDifferentPidActiveChildFound)
                    {
                        ParsekLog.VerboseRateLimited("Watch",
                            $"breakup-watch-no-fallback-{currentRec.RecordingId}",
                            $"FindNextWatchTarget: breakup branch {bp.Id} for rec '{currentRec.VesselName}' " +
                            "has no same-PID continuation — preserving live vessel context");
                    }
                    if (fallbackIdx >= 0)
                        return fallbackIdx;
                }
            }

            return -1;
        }

        /// <summary>
        /// Returns the earliest UT at which a preferred watch continuation could become
        /// ghost-active, even if it is not active yet. This is used to extend the
        /// watch-end hold timer for quickload-resumed branches whose continuation data
        /// starts later than the parent branch boundary. When <paramref name="supersedes"/>
        /// is non-null, candidates are resolved through the supersede graph before the
        /// activity / activation-UT check so re-fly forks extend the hold timer until
        /// the canonical fork ghost becomes active.
        /// </summary>
        internal static bool TryGetPendingWatchActivationUT(
            Recording currentRec,
            IReadOnlyList<Recording> committed,
            IReadOnlyList<RecordingTree> trees,
            Func<int, bool> isGhostActive,
            out double activationUT,
            IReadOnlyList<RecordingSupersedeRelation> supersedes = null,
            int depth = 0)
        {
            activationUT = double.NaN;
            const int MaxRecursionDepth = 10;
            if (currentRec == null || committed == null || depth > MaxRecursionDepth)
                return false;

            // Case 1: Chain continuation.
            if (!string.IsNullOrEmpty(currentRec.ChainId)
                && currentRec.ChainIndex >= 0
                && currentRec.ChainBranch == 0)
            {
                int nextChainIndex = currentRec.ChainIndex + 1;
                for (int j = 0; j < committed.Count; j++)
                {
                    var candidate = committed[j];
                    if (candidate.ChainId != currentRec.ChainId
                        || candidate.ChainBranch != 0
                        || candidate.ChainIndex != nextChainIndex)
                    {
                        continue;
                    }

                    int resolvedIdx = ResolveSupersedeIndex(j, committed, supersedes);
                    if (resolvedIdx < 0 || resolvedIdx >= committed.Count)
                        continue;
                    Recording resolved = committed[resolvedIdx];
                    if (resolved == null)
                        continue;

                    if (isGhostActive != null && isGhostActive(resolvedIdx))
                        return false;

                    return resolved.TryGetGhostActivationStartUT(out activationUT);
                }
            }

            // Case 2: Tree branching via ChildBranchPointId. Mirror FindNextWatchTarget:
            // prefer same-PID continuation, otherwise allow non-debris fallback on
            // non-breakup branches.
            if (!string.IsNullOrEmpty(currentRec.ChildBranchPointId)
                && currentRec.IsTreeRecording
                && trees != null)
            {
                BranchPoint bp = null;
                for (int t = 0; t < trees.Count; t++)
                {
                    var tree = trees[t];
                    if (tree.Id != currentRec.TreeId)
                        continue;

                    for (int b = 0; b < tree.BranchPoints.Count; b++)
                    {
                        if (tree.BranchPoints[b].Id == currentRec.ChildBranchPointId)
                        {
                            bp = tree.BranchPoints[b];
                            break;
                        }
                    }
                    break;
                }

                if (bp != null)
                {
                    double samePidActivationUT = double.NaN;
                    double fallbackActivationUT = double.NaN;
                    bool sawSamePidContinuation = false;
                    bool sawActiveFallback = false;
                    bool allowDifferentPidFallback = bp.Type != BranchPointType.Breakup;
                    for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                    {
                        string childId = bp.ChildRecordingIds[c];
                        for (int j = 0; j < committed.Count; j++)
                        {
                            var raw = committed[j];
                            if (raw.RecordingId != childId)
                            {
                                continue;
                            }

                            int resolvedIdx = ResolveSupersedeIndex(j, committed, supersedes);
                            if (resolvedIdx < 0 || resolvedIdx >= committed.Count)
                                continue;
                            Recording candidate = committed[resolvedIdx];
                            if (candidate == null)
                                continue;

                            bool isPidMatch = candidate.VesselPersistentId == currentRec.VesselPersistentId;
                            bool isAllowedFallback = allowDifferentPidFallback && !candidate.IsDebris;
                            if (!isPidMatch && !isAllowedFallback)
                                continue;

                            if (isPidMatch)
                            {
                                sawSamePidContinuation = true;

                                if (isGhostActive != null && isGhostActive(resolvedIdx))
                                    return false;

                                if (candidate.TryGetGhostActivationStartUT(out double candidateActivationUT))
                                    samePidActivationUT = MinPendingActivationUT(samePidActivationUT, candidateActivationUT);

                                if (TryGetPendingWatchActivationUT(
                                        candidate, committed, trees, isGhostActive, out double deeperActivationUT, supersedes, depth + 1))
                                {
                                    samePidActivationUT = MinPendingActivationUT(samePidActivationUT, deeperActivationUT);
                                }
                                continue;
                            }

                            if (isGhostActive != null && isGhostActive(resolvedIdx))
                            {
                                sawActiveFallback = true;
                                continue;
                            }

                            if (candidate.TryGetGhostActivationStartUT(out double candidateActivationFallbackUT))
                                fallbackActivationUT = MinPendingActivationUT(fallbackActivationUT, candidateActivationFallbackUT);
                        }
                    }

                    if (sawSamePidContinuation && !double.IsNaN(samePidActivationUT))
                    {
                        activationUT = samePidActivationUT;
                        return true;
                    }
                    if (sawSamePidContinuation)
                        return false;
                    if (sawActiveFallback)
                        return false;
                    if (!double.IsNaN(fallbackActivationUT))
                    {
                        activationUT = fallbackActivationUT;
                        return true;
                    }
                }
            }

            return false;
        }

        internal static float ComputePendingWatchHoldSeconds(
            float baseHoldSeconds,
            double currentUT,
            double continuationActivationUT,
            float warpRate)
        {
            if (baseHoldSeconds < 0f)
                baseHoldSeconds = 0f;

            if (double.IsNaN(continuationActivationUT) || continuationActivationUT <= currentUT)
                return baseHoldSeconds;

            // #369: harden against NaN warp rate — Mathf.Ceil((x / NaN) + grace) is
            // NaN and Mathf.Clamp on NaN silently falls through to the base hold.
            float effectiveWarpRate = (!float.IsNaN(warpRate) && warpRate > 0.01f) ? warpRate : 1f;
            float requiredSeconds = Mathf.Ceil((float)((continuationActivationUT - currentUT) / effectiveWarpRate)
                + WatchMode.PendingPostActivationGraceSeconds);
            return Mathf.Clamp(Mathf.Max(baseHoldSeconds, requiredSeconds), baseHoldSeconds, WatchMode.MaxPendingHoldSeconds);
        }

        internal static void ComputePendingWatchHoldWindow(
            float baseHoldSeconds,
            float currentRealtime,
            double currentUT,
            double continuationActivationUT,
            float warpRate,
            out float holdUntilRealTime,
            out float holdMaxRealTime)
        {
            float holdSeconds = ComputePendingWatchHoldSeconds(
                baseHoldSeconds,
                currentUT,
                continuationActivationUT,
                warpRate);

            holdUntilRealTime = currentRealtime + holdSeconds;
            if (!double.IsNaN(continuationActivationUT) && continuationActivationUT > currentUT)
            {
                holdMaxRealTime = currentRealtime + WatchMode.MaxPendingHoldSeconds;
                holdUntilRealTime = Mathf.Min(holdUntilRealTime, holdMaxRealTime);
            }
            else
            {
                holdMaxRealTime = holdUntilRealTime;
            }
        }

        private static double MinPendingActivationUT(double currentBest, double candidate)
        {
            if (double.IsNaN(candidate))
                return currentBest;
            if (double.IsNaN(currentBest))
                return candidate;
            return Math.Min(currentBest, candidate);
        }

        #endregion
    }
}
