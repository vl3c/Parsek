using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Phase 2 of Rewind-to-Staging (design doc sections 3.1 / 3.2 / 3.3).
    ///
    /// Central helper for the two derived, read-only sets every subsystem routes
    /// its "is this in effect?" questions through:
    ///
    /// <list type="bullet">
    ///   <item><description><c>ERS</c> (Effective Recording Set): committed, visible (not superseded), not <c>NotCommitted</c>, minus the active re-fly's <c>SessionSuppressedSubtree</c>.</description></item>
    ///   <item><description><c>ELS</c> (Effective Ledger Set): <see cref="GameActions.Ledger.Actions"/> minus any action whose <see cref="GameAction.ActionId"/> appears in <see cref="ParsekScenario.LedgerTombstones"/>.</description></item>
    /// </list>
    ///
    /// Read-only. Never mutates the source collections. Values are cached and
    /// invalidated via version counters on the three source surfaces:
    /// <see cref="RecordingStore.StateVersion"/>,
    /// <see cref="ParsekScenario.SupersedeStateVersion"/> /
    /// <see cref="ParsekScenario.TombstoneStateVersion"/>, and
    /// <see cref="Ledger.StateVersion"/>.
    /// Also invalidated by identity of the active <see cref="ReFlySessionMarker"/> —
    /// switching marker identity (or null -> non-null) changes the
    /// <see cref="SessionSuppressedSubtree(ReFlySessionMarker)"/> closure.
    /// </summary>
    public static class EffectiveState
    {
        private static readonly object syncRoot = new object();

        private static int ersCacheStoreVersion = int.MinValue;
        private static int ersCacheSupersedeVersion = int.MinValue;
        private static object ersCacheMarkerIdentity;
        private static IReadOnlyList<Recording> ersCache;

        private static int elsCacheLedgerVersion = int.MinValue;
        private static int elsCacheTombstoneVersion = int.MinValue;
        private static IReadOnlyList<GameAction> elsCache;

        private static object suppressionCacheMarkerIdentity;
        private static int suppressionCacheStoreVersion = int.MinValue;
        private static HashSet<string> suppressionCache;

        /// <summary>Resets all caches. For unit tests only.</summary>
        internal static void ResetCachesForTesting()
        {
            lock (syncRoot)
            {
                ersCacheStoreVersion = int.MinValue;
                ersCacheSupersedeVersion = int.MinValue;
                ersCacheMarkerIdentity = null;
                ersCache = null;

                elsCacheLedgerVersion = int.MinValue;
                elsCacheTombstoneVersion = int.MinValue;
                elsCache = null;

                suppressionCacheMarkerIdentity = null;
                suppressionCacheStoreVersion = int.MinValue;
                suppressionCache = null;
            }
        }

        /// <summary>
        /// Walks forward through <paramref name="supersedes"/> starting at
        /// <paramref name="originRecordingId"/>. For each current id, looks
        /// for a supersede relation with <c>OldRecordingId == current</c>; if
        /// found, advances to <c>NewRecordingId</c>. Returns the last id
        /// reached when no relation matches (the "orphan endpoint" in design
        /// §5.2: the final <c>NewRecordingId</c> IS the effective id).
        /// Cycle guard via visited HashSet: A -> B -> A logs Warn tagged
        /// <c>[Supersede]</c> and returns the last-visited id. Null / empty
        /// origin returns null. Null / empty supersede list is treated as
        /// chain-length 0 (origin is already effective).
        /// </summary>
        // TODO(phase >=3): halt walk at cross-tree boundaries once cross-tree supersedes are producible (design §5.2).
        // Tracked in docs/dev/todo-and-known-bugs.md (Phase 3+ work).
        public static string EffectiveRecordingId(
            string originRecordingId,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (string.IsNullOrEmpty(originRecordingId))
                return null;

            if (supersedes == null || supersedes.Count == 0)
                return originRecordingId;

            var visited = new HashSet<string>(StringComparer.Ordinal);
            string current = originRecordingId;
            visited.Add(current);

            while (true)
            {
                string next = null;
                for (int i = 0; i < supersedes.Count; i++)
                {
                    var rel = supersedes[i];
                    if (rel == null) continue;
                    if (string.Equals(rel.OldRecordingId, current, StringComparison.Ordinal))
                    {
                        next = rel.NewRecordingId;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(next))
                    return current; // orphan endpoint / chain terminus

                if (visited.Contains(next))
                {
                    ParsekLog.Warn("Supersede",
                        $"EffectiveRecordingId: cycle detected in supersede chain starting from {originRecordingId} " +
                        $"at current={current} next={next}; returning last-visited={current}");
                    return current;
                }

                visited.Add(next);
                current = next;
            }
        }

        /// <summary>
        /// True iff <paramref name="rec"/> is visible per design §3.1:
        /// <c>MergeState != NotCommitted</c> AND walking forward from
        /// <c>rec.RecordingId</c> via <paramref name="supersedes"/> lands on
        /// <c>rec.RecordingId</c> itself (i.e. nothing supersedes it).
        /// Null / empty inputs are safe-false / safe-pass-through.
        /// </summary>
        public static bool IsVisible(Recording rec, IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (rec == null) return false;
            if (rec.MergeState == MergeState.NotCommitted) return false;
            if (string.IsNullOrEmpty(rec.RecordingId)) return false;

            if (supersedes == null || supersedes.Count == 0)
                return true;

            string effective = EffectiveRecordingId(rec.RecordingId, supersedes);
            return string.Equals(effective, rec.RecordingId, StringComparison.Ordinal);
        }

        /// <summary>
        /// True iff <paramref name="rec"/> is replaced by an explicit supersede
        /// relation. Unlike <see cref="IsVisible"/>, this does not filter
        /// NotCommitted rows; raw-indexed UI and ghost systems use it to hide
        /// old committed timeline entries without changing their index space.
        /// </summary>
        public static bool IsSupersededByRelation(
            Recording rec,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (rec == null) return false;
            if (string.IsNullOrEmpty(rec.RecordingId)) return false;
            if (supersedes == null || supersedes.Count == 0) return false;

            string effective = EffectiveRecordingId(rec.RecordingId, supersedes);
            return !string.Equals(effective, rec.RecordingId, StringComparison.Ordinal);
        }

        /// <summary>
        /// True iff <paramref name="rec"/> is an Unfinished Flight per design §3.1.
        /// Current formulation: the recording is committed-visible
        /// (<c>Immutable</c> legacy/original child or <c>CommittedProvisional</c>
        /// live rewind slot), the terminal indicates a crash (see
        /// <see cref="IsTerminalCrashed"/>), and the parent BranchPoint carries
        /// a RewindPoint.
        /// </summary>
        public static bool IsUnfinishedFlight(Recording rec)
        {
            if (rec == null) return false;

            string recId = rec.RecordingId ?? "<no-id>";

            if (rec.MergeState != MergeState.Immutable
                && rec.MergeState != MergeState.CommittedProvisional)
            {
                ParsekLog.VerboseRateLimited("UnfinishedFlights",
                    $"mergeState-{recId}",
                    $"IsUnfinishedFlight=false rec={recId} reason=mergeState:{rec.MergeState}");
                return false;
            }
            // Chain-walk for the terminal check: merge-time SplitAtSection
            // splits a single live recording at env boundaries (atmo→exo) into
            // chained segments. The chain HEAD carries the parentBranchPointId
            // (→ RewindPoint link), the chain TIP carries the terminal state.
            // Checking the head's own terminal would always read null for a
            // multi-segment chain and silently exclude destroyed siblings
            // from Unfinished Flights. Walk forward and use the tip.
            Recording terminalRec = ResolveChainTerminalRecording(rec);
            if (!IsTerminalCrashed(terminalRec))
            {
                ParsekLog.VerboseRateLimited("UnfinishedFlights",
                    $"notCrashed-{recId}",
                    $"IsUnfinishedFlight=false rec={recId} reason=notCrashed:{terminalRec.TerminalStateValue} " +
                    $"(tip={(ReferenceEquals(terminalRec, rec) ? "self" : terminalRec.RecordingId ?? "<no-id>")})");
                return false;
            }
            // Accept either the branch-parent link (`ParentBranchPointId`
            // for the split's new children) or the branch-child link
            // (`ChildBranchPointId` for the surviving active parent).
            // Breakup RPs include the active parent as a controllable
            // output, so it must be able to resolve to the RP too when it
            // later crashes.
            string parentBpId = rec.ParentBranchPointId;
            string childBpId = rec.ChildBranchPointId;
            if (string.IsNullOrEmpty(parentBpId) && string.IsNullOrEmpty(childBpId))
            {
                ParsekLog.VerboseRateLimited("UnfinishedFlights",
                    $"noParentBp-{recId}",
                    $"IsUnfinishedFlight=false rec={recId} reason=noParentBp");
                return false;
            }

            var scenario = ParsekScenario.Instance;
            // Use object.ReferenceEquals to bypass Unity.Object's overloaded
            // == null, which returns true for MonoBehaviour instances whose
            // native side hasn't been created (unit-test fixtures that new up
            // a ParsekScenario without going through Unity's AddComponent).
            if (object.ReferenceEquals(null, scenario) || scenario.RewindPoints == null)
            {
                ParsekLog.VerboseRateLimited("UnfinishedFlights",
                    $"noScenario-{recId}",
                    $"IsUnfinishedFlight=false rec={recId} reason=noScenario");
                return false;
            }

            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                var rp = scenario.RewindPoints[i];
                if (rp == null) continue;
                bool matchesParent = !string.IsNullOrEmpty(parentBpId)
                    && string.Equals(rp.BranchPointId, parentBpId, StringComparison.Ordinal);
                bool matchesChild = !string.IsNullOrEmpty(childBpId)
                    && string.Equals(rp.BranchPointId, childBpId, StringComparison.Ordinal);
                if (matchesParent || matchesChild)
                {
                    // The parent BP has an RP — and RewindPoint existence IS the
                    // design §5.4 "BranchPoint.RewindPointId != null" check,
                    // since BranchPoint.RewindPointId is backfilled from the
                    // persisted RewindPoint list.
                    string matchedBp = matchesParent ? parentBpId : childBpId;
                    string side = matchesParent ? "parent" : "active-parent-child";
                    ParsekLog.VerboseRateLimited("UnfinishedFlights",
                        $"match-{recId}",
                        $"IsUnfinishedFlight=true rec={recId} bp={matchedBp} side={side} rp={rp.RewindPointId}");
                    return true;
                }
            }

            ParsekLog.VerboseRateLimited("UnfinishedFlights",
                $"noMatchingRP-{recId}",
                $"IsUnfinishedFlight=false rec={recId} reason=noMatchingRP " +
                $"parentBp={parentBpId ?? "<none>"} childBp={childBpId ?? "<none>"} " +
                $"rpCount={scenario.RewindPoints.Count}");
            return false;
        }

        /// <summary>
        /// Classifies a recording's terminal state as a crash / destroyed
        /// outcome (design §3.1 <c>TerminalKind in {BGCrash, Crashed}</c>).
        /// Delegates to <see cref="TerminalKindClassifier.Classify"/> so both
        /// the Unfinished-Flight predicate here and the re-fly merge rule in
        /// <see cref="SupersedeCommit.CommitSupersede"/> share the same
        /// classifier (design §6.6 step 2).
        /// </summary>
        public static bool IsTerminalCrashed(Recording rec)
        {
            return TerminalKindClassifier.Classify(rec) == TerminalKind.Crashed;
        }

        /// <summary>
        /// True iff <paramref name="rec"/> is part of a chain whose head
        /// (the segment carrying the <c>ParentBranchPointId</c> link to a
        /// RewindPoint) qualifies as an Unfinished Flight. Used by the
        /// recordings-table row rendering to suppress the legacy
        /// rewind-to-launch <c>R</c> button on chain continuations: a booster
        /// that re-flies via its chain-head Rewind-to-Staging button should
        /// NOT also offer a chain-continuation row another R that silently
        /// rewinds the entire mission to the pad. The chain head itself also
        /// trips this check, which is fine — its new Rewind-to-Staging button
        /// already takes over, so the legacy branch would never run.
        /// </summary>
        public static bool IsChainMemberOfUnfinishedFlight(Recording rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.ChainId)) return false;

            var ers = ComputeERS();
            if (ers == null) return false;

            for (int i = 0; i < ers.Count; i++)
            {
                var candidate = ers[i];
                if (candidate == null) continue;
                if (!string.Equals(candidate.ChainId, rec.ChainId, StringComparison.Ordinal)) continue;
                if (candidate.ChainBranch != rec.ChainBranch) continue;
                if (IsUnfinishedFlight(candidate))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Resolves the recording whose <c>TerminalStateValue</c> actually
        /// represents the ending of <paramref name="rec"/>'s vessel. For a
        /// chained recording, merge-time <c>Optimizer.SplitAtSection</c>
        /// splits a single live recording at environment boundaries (atmo→exo,
        /// etc.) into two or more chained segments: the HEAD keeps the
        /// parent-branch-point link back to the Rewind Point, and the TIP
        /// carries the terminal state. Callers asking "did this vessel end
        /// crashed?" must consult the tip, not the head. Non-chained
        /// recordings return themselves.
        ///
        /// <para>
        /// The tip is chosen as the recording with the same
        /// <c>ChainId</c> / <c>ChainBranch</c> and the largest
        /// <c>ChainIndex</c> found in the same committed tree. Chain members
        /// live in the same <see cref="RecordingTree"/> so the scan is
        /// bounded by the tree's recording dictionary.
        /// </para>
        /// </summary>
        internal static Recording ResolveChainTerminalRecording(Recording rec)
            => ResolveChainTerminalRecording(rec, null);

        /// <summary>
        /// Resolves the chain terminal using an optional tree context. Merge
        /// dialogs run before the pending tree is committed, so callers there
        /// must not depend on <see cref="RecordingStore.CommittedTrees"/>.
        /// </summary>
        internal static Recording ResolveChainTerminalRecording(
            Recording rec,
            RecordingTree treeContext)
        {
            if (rec == null) return null;
            if (string.IsNullOrEmpty(rec.ChainId)) return rec;

            RecordingTree owningTree = null;
            if (treeContext != null && treeContext.Recordings != null)
            {
                bool sameTreeId = !string.IsNullOrEmpty(rec.TreeId)
                    && string.Equals(treeContext.Id, rec.TreeId, StringComparison.Ordinal);
                bool containsRecording = !string.IsNullOrEmpty(rec.RecordingId)
                    && treeContext.Recordings.ContainsKey(rec.RecordingId);
                if (sameTreeId || containsRecording)
                    owningTree = treeContext;
            }

            if (owningTree == null)
            {
                var trees = RecordingStore.CommittedTrees;
                if (trees == null) return rec;

                for (int i = 0; i < trees.Count; i++)
                {
                    var tree = trees[i];
                    if (tree == null) continue;
                    if (!string.IsNullOrEmpty(rec.TreeId) && !string.Equals(tree.Id, rec.TreeId, StringComparison.Ordinal))
                        continue;
                    if (tree.Recordings != null
                        && !string.IsNullOrEmpty(rec.RecordingId)
                        && tree.Recordings.ContainsKey(rec.RecordingId))
                    {
                        owningTree = tree;
                        break;
                    }
                }
            }

            if (owningTree?.Recordings == null) return rec;

            Recording tip = rec;
            int maxIdx = rec.ChainIndex;
            foreach (var candidate in owningTree.Recordings.Values)
            {
                if (candidate == null || ReferenceEquals(candidate, rec)) continue;
                if (!string.Equals(candidate.ChainId, rec.ChainId, StringComparison.Ordinal)) continue;
                if (candidate.ChainBranch != rec.ChainBranch) continue;
                if (candidate.ChainIndex <= maxIdx) continue;
                tip = candidate;
                maxIdx = candidate.ChainIndex;
            }
            return tip;
        }

        /// <summary>
        /// Computes the Effective Recording Set per design §3.1: committed
        /// recordings that are visible (not superseded), not <c>NotCommitted</c>,
        /// minus the active re-fly's <c>SessionSuppressedSubtree</c>.
        /// Cached; rebuilds only when the underlying
        /// <see cref="RecordingStore.StateVersion"/>,
        /// <see cref="ParsekScenario.SupersedeStateVersion"/>, or active
        /// <see cref="ReFlySessionMarker"/> identity change.
        /// </summary>
        public static IReadOnlyList<Recording> ComputeERS()
        {
            var scenario = ParsekScenario.Instance;
            bool hasScenario = !object.ReferenceEquals(null, scenario);
            IReadOnlyList<RecordingSupersedeRelation> supersedes = hasScenario ? scenario.RecordingSupersedes : null;
            ReFlySessionMarker marker = hasScenario ? scenario.ActiveReFlySessionMarker : null;
            int storeVersion = RecordingStore.StateVersion;
            int supersedeVersion = hasScenario ? scenario.SupersedeStateVersion : 0;

            lock (syncRoot)
            {
                if (ersCache != null
                    && ersCacheStoreVersion == storeVersion
                    && ersCacheSupersedeVersion == supersedeVersion
                    && ReferenceEquals(ersCacheMarkerIdentity, marker))
                {
                    return ersCache;
                }

                var suppressed = ComputeSessionSuppressedSubtreeInternal(marker);
                var source = RecordingStore.CommittedRecordings;
                var result = new List<Recording>(source.Count);
                int raw = source.Count;
                int skippedNotCommitted = 0;
                int skippedSuperseded = 0;
                int skippedSuppressed = 0;

                for (int i = 0; i < source.Count; i++)
                {
                    var rec = source[i];
                    if (rec == null) continue;
                    if (rec.MergeState == MergeState.NotCommitted)
                    {
                        skippedNotCommitted++;
                        continue;
                    }
                    if (!IsVisible(rec, supersedes))
                    {
                        skippedSuperseded++;
                        continue;
                    }
                    if (suppressed != null && !string.IsNullOrEmpty(rec.RecordingId)
                        && suppressed.Contains(rec.RecordingId))
                    {
                        skippedSuppressed++;
                        continue;
                    }
                    result.Add(rec);
                }

                ersCache = result;
                ersCacheStoreVersion = storeVersion;
                ersCacheSupersedeVersion = supersedeVersion;
                ersCacheMarkerIdentity = marker;

                ParsekLog.Verbose("ERS",
                    $"Rebuilt: {result.Count} entries from {raw} committed " +
                    $"(skippedNotCommitted={skippedNotCommitted} skippedSuperseded={skippedSuperseded} " +
                    $"skippedSuppressed={skippedSuppressed} marker={(marker != null ? marker.SessionId ?? "<no-id>" : "none")})");

                return ersCache;
            }
        }

        /// <summary>
        /// Computes the Effective Ledger Set per design §3.2: ledger actions
        /// whose <see cref="GameAction.ActionId"/> is NOT in any
        /// <see cref="LedgerTombstone"/>. No recording-level filter (v1
        /// narrow supersede scope; contract / milestone / funds actions from
        /// superseded recordings remain in ELS).
        /// Cached; rebuilds only when the underlying
        /// <see cref="Ledger.StateVersion"/> or
        /// <see cref="ParsekScenario.TombstoneStateVersion"/> change.
        /// </summary>
        public static IReadOnlyList<GameAction> ComputeELS()
        {
            var scenario = ParsekScenario.Instance;
            bool hasScenario = !object.ReferenceEquals(null, scenario);
            var tombstones = hasScenario ? scenario.LedgerTombstones : null;
            int ledgerVersion = Ledger.StateVersion;
            int tombstoneVersion = hasScenario ? scenario.TombstoneStateVersion : 0;

            lock (syncRoot)
            {
                if (elsCache != null
                    && elsCacheLedgerVersion == ledgerVersion
                    && elsCacheTombstoneVersion == tombstoneVersion)
                {
                    return elsCache;
                }

                // Collect tombstoned ActionIds into a set (ordinal compare).
                HashSet<string> retiredIds = null;
                int tombCount = 0;
                if (tombstones != null && tombstones.Count > 0)
                {
                    retiredIds = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < tombstones.Count; i++)
                    {
                        var t = tombstones[i];
                        if (t == null) continue;
                        if (string.IsNullOrEmpty(t.ActionId)) continue;
                        retiredIds.Add(t.ActionId);
                        tombCount++;
                    }
                }

                var source = Ledger.Actions;
                var result = new List<GameAction>(source.Count);
                int raw = source.Count;
                int skippedTombstoned = 0;

                for (int i = 0; i < source.Count; i++)
                {
                    var a = source[i];
                    if (a == null) continue;
                    if (retiredIds != null && !string.IsNullOrEmpty(a.ActionId)
                        && retiredIds.Contains(a.ActionId))
                    {
                        skippedTombstoned++;
                        continue;
                    }
                    result.Add(a);
                }

                elsCache = result;
                elsCacheLedgerVersion = ledgerVersion;
                elsCacheTombstoneVersion = tombstoneVersion;

                ParsekLog.Verbose("ELS",
                    $"Rebuilt: {result.Count} entries from {raw} actions " +
                    $"(skippedTombstoned={skippedTombstoned} tombstonesTotal={tombCount})");

                return elsCache;
            }
        }

        /// <summary>
        /// Forward-only, merge-guarded closure per design §3.3: starting at
        /// <paramref name="marker"/>'s <c>OriginChildRecordingId</c>, follow
        /// each committed recording's <c>ChildBranchPointId</c> to the
        /// referenced BranchPoint's <c>ChildRecordingIds</c>. Halt at any
        /// BranchPoint whose <c>ParentRecordingIds</c> contains any recording
        /// NOT already in the suppressed set (mixed-parent halt; type-agnostic).
        /// Returns an empty set when <paramref name="marker"/> is null.
        /// The origin recording is always included.
        /// <para>
        /// The returned collection is a DEFENSIVE COPY of the cached closure; callers
        /// may freely iterate without disturbing cross-call cache consistency. The
        /// copy cost is one HashSet allocation per call (acceptable — call frequency
        /// is at most once per ghost-frame, gated by marker presence).
        /// </para>
        /// </summary>
        public static IReadOnlyCollection<string> ComputeSessionSuppressedSubtree(ReFlySessionMarker marker)
        {
            var cached = ComputeSessionSuppressedSubtreeInternal(marker);
            if (cached == null) return Array.Empty<string>();
            // Defensive copy: callers get an isolated snapshot so mutations (if they
            // ever happened) cannot corrupt the shared cache.
            return new HashSet<string>(cached, StringComparer.Ordinal);
        }

        /// <summary>
        /// True iff <paramref name="marker"/> is non-null AND
        /// <paramref name="rec"/>'s RecordingId appears in the cached
        /// <c>SessionSuppressedSubtree</c> closure.
        /// </summary>
        public static bool IsInSessionSuppressedSubtree(Recording rec, ReFlySessionMarker marker)
        {
            if (rec == null) return false;
            if (marker == null) return false;
            if (string.IsNullOrEmpty(rec.RecordingId)) return false;
            var closure = ComputeSessionSuppressedSubtreeInternal(marker);
            return closure != null && closure.Contains(rec.RecordingId);
        }

        // Internal closure computation that returns the cached HashSet directly
        // so ComputeERS can do O(1) membership checks.
        private static HashSet<string> ComputeSessionSuppressedSubtreeInternal(ReFlySessionMarker marker)
        {
            if (marker == null || string.IsNullOrEmpty(marker.OriginChildRecordingId))
                return null;

            int storeVersion = RecordingStore.StateVersion;

            lock (syncRoot)
            {
                if (suppressionCache != null
                    && ReferenceEquals(suppressionCacheMarkerIdentity, marker)
                    && suppressionCacheStoreVersion == storeVersion)
                {
                    return suppressionCache;
                }

                var result = new HashSet<string>(StringComparer.Ordinal);
                result.Add(marker.OriginChildRecordingId);

                // Build a recording-id -> Recording lookup so the walk is O(N)
                // rather than O(N^2) per committed recording.
                var source = RecordingStore.CommittedRecordings;
                var recById = new Dictionary<string, Recording>(StringComparer.Ordinal);
                for (int i = 0; i < source.Count; i++)
                {
                    var r = source[i];
                    if (r != null && !string.IsNullOrEmpty(r.RecordingId))
                        recById[r.RecordingId] = r;
                }

                // BranchPoint lookup via RewindPoint-persisted branch points is not
                // available directly; BranchPoints are referenced from Recording.
                // The closure walks committed recordings and inspects each's
                // ChildBranchPointId, which — after Phase 1 — is expected to be a
                // weak-link id to the tree's BranchPoint node.
                // Phase 2 implementation: BranchPoints live on RecordingTree; look
                // them up via each recording's tree.
                var queue = new Queue<string>();
                queue.Enqueue(marker.OriginChildRecordingId);

                int mixedParentHalts = 0;
                int childrenAdded = 0;
                int siblingsAdded = 0;

                while (queue.Count > 0)
                {
                    string currentId = queue.Dequeue();
                    if (!recById.TryGetValue(currentId, out var currentRec))
                        continue;

                    // Chain-sibling expansion: merge-time SplitAtSection
                    // splits one live recording into ChainId-linked siblings
                    // where the HEAD keeps the parent-branch-point link
                    // (and `ChildBranchPointId = null`) while the TIP carries
                    // the terminal state (and the moved `ChildBranchPointId`).
                    // Walking only via `ChildBranchPointId` would dequeue the
                    // HEAD, hit the early-return below, and never enqueue the
                    // TIP — leaving a stale orphan after re-fly merge. Run
                    // chain expansion on every dequeued member, BEFORE the
                    // null-`ChildBranchPointId` early-return, so a HEAD with
                    // no BP descendants still propagates to its siblings.
                    EnqueueChainSiblings(currentRec, recById, queue, result, ref siblingsAdded);

                    if (string.IsNullOrEmpty(currentRec.ChildBranchPointId))
                        continue;

                    // Look up the BranchPoint via the tree the recording belongs to.
                    BranchPoint bp = LookupBranchPoint(currentRec.TreeId, currentRec.ChildBranchPointId);
                    if (bp == null) continue;
                    if (bp.ChildRecordingIds == null) continue;

                    for (int ci = 0; ci < bp.ChildRecordingIds.Count; ci++)
                    {
                        string childId = bp.ChildRecordingIds[ci];
                        if (string.IsNullOrEmpty(childId)) continue;
                        if (result.Contains(childId)) continue;

                        // Any BranchPoint with a parent outside the suppressed set halts the walk
                        // (dock/board merges are the common case but this is type-agnostic per design §3.3).
                        if (HasOutsideParent(bp, result))
                        {
                            mixedParentHalts++;
                            ParsekLog.Verbose("ReFlySession",
                                $"SessionSuppressedSubtree: mixed-parent halt at bp={bp.Id} type={bp.Type} " +
                                $"child={childId} parents=[{string.Join(",", bp.ParentRecordingIds ?? new List<string>())}]");
                            continue;
                        }

                        result.Add(childId);
                        childrenAdded++;
                        queue.Enqueue(childId);
                    }
                }

                suppressionCache = result;
                suppressionCacheMarkerIdentity = marker;
                suppressionCacheStoreVersion = storeVersion;

                ParsekLog.Verbose("ReFlySession",
                    $"SessionSuppressedSubtree: {result.Count} recording(s) closed from origin={marker.OriginChildRecordingId} " +
                    $"(childrenAdded={childrenAdded} siblingsAdded={siblingsAdded} mixedParentHalts={mixedParentHalts})");

                return suppressionCache;
            }
        }

        private static BranchPoint LookupBranchPoint(string treeId, string branchPointId)
        {
            if (string.IsNullOrEmpty(branchPointId)) return null;

            var trees = RecordingStore.CommittedTrees;
            if (trees == null) return null;

            // Single pass: prefer the tree whose Id matches treeId; otherwise remember
            // the first match in any other tree as a fallback for legacy recordings
            // (treeId == null) or mismatched/stale treeId values.
            BranchPoint fallback = null;
            for (int i = 0; i < trees.Count; i++)
            {
                var tree = trees[i];
                if (tree == null) continue;
                var bp = FindBranchPointInTree(tree, branchPointId);
                if (bp == null) continue;
                if (!string.IsNullOrEmpty(treeId) && string.Equals(tree.Id, treeId, StringComparison.Ordinal))
                    return bp; // short-circuit: preferred tree match
                if (fallback == null) fallback = bp;
            }

            return fallback;
        }

        private static BranchPoint FindBranchPointInTree(RecordingTree tree, string branchPointId)
        {
            if (tree == null || tree.BranchPoints == null) return null;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                var bp = tree.BranchPoints[i];
                if (bp != null && string.Equals(bp.Id, branchPointId, StringComparison.Ordinal))
                    return bp;
            }
            return null;
        }

        /// <summary>
        /// Adds every committed recording sharing <c>TreeId</c>,
        /// <c>ChainId</c>, AND <c>ChainBranch</c> with <paramref name="rec"/>
        /// to <paramref name="result"/> and enqueues them for further BP
        /// walking. Idempotent via the <paramref name="result"/> HashSet.
        ///
        /// <para>
        /// Same-tree / same-chain / same-branch is the canonical "this is the
        /// same vessel continued at an env boundary" predicate — see
        /// <see cref="IsChainMemberOfUnfinishedFlight"/> and
        /// <see cref="ResolveChainTerminalRecording"/>, which use the same
        /// keys (the terminal-chain resolver scopes by owning tree explicitly;
        /// this helper enforces it via the <see cref="Recording.TreeId"/>
        /// field). The owning-tree gate is defense-in-depth: chain segments
        /// produced by <see cref="RecordingOptimizer.SplitAtSection"/> always
        /// share <c>TreeId</c> by construction, but if a future clone path,
        /// import, or legacy save ever produces colliding <c>ChainId</c>s
        /// across trees, starting a re-fly in one tree must not pull a
        /// different tree's recordings into the suppressed closure (those
        /// recordings would also be supersede-rowed and tombstone-scanned).
        /// Different <c>ChainBranch</c> values stay independent (parallel
        /// ghost-only continuations).
        /// </para>
        ///
        /// <para>
        /// The cache invalidation that wraps the closure builder
        /// (<c>RecordingStore.StateVersion</c>, see line 528) covers chain
        /// siblings automatically — they live in
        /// <c>RecordingStore.CommittedRecordings</c>, the same source the
        /// builder iterates to populate <paramref name="recById"/>.
        /// </para>
        /// </summary>
        private static void EnqueueChainSiblings(
            Recording rec,
            Dictionary<string, Recording> recById,
            Queue<string> queue,
            HashSet<string> result,
            ref int siblingsAdded)
        {
            if (rec == null || string.IsNullOrEmpty(rec.ChainId)) return;
            // Legacy / orphaned recordings without a TreeId: refuse to
            // chain-expand. We cannot prove the candidate is in the same
            // tree, and a false-positive crosses tree boundaries silently.
            if (string.IsNullOrEmpty(rec.TreeId)) return;

            foreach (var cand in recById.Values)
            {
                if (cand == null) continue;
                if (ReferenceEquals(cand, rec)) continue;
                if (string.IsNullOrEmpty(cand.RecordingId)) continue;
                if (!string.Equals(cand.TreeId, rec.TreeId, StringComparison.Ordinal)) continue;
                if (!string.Equals(cand.ChainId, rec.ChainId, StringComparison.Ordinal)) continue;
                if (cand.ChainBranch != rec.ChainBranch) continue;
                if (result.Contains(cand.RecordingId)) continue;

                result.Add(cand.RecordingId);
                siblingsAdded++;
                // Enqueue so the BP walk runs on this member too — covers
                // a TIP that received the moved `ChildBranchPointId` from
                // RecordingStore.cs:2018-2019, multi-segment chains, and
                // the "origin is itself a TIP" symmetric case (HashSet
                // dedup prevents revisits).
                queue.Enqueue(cand.RecordingId);
            }
        }

        private static bool HasOutsideParent(BranchPoint bp, HashSet<string> suppressed)
        {
            if (bp == null || bp.ParentRecordingIds == null) return false;
            for (int i = 0; i < bp.ParentRecordingIds.Count; i++)
            {
                string pid = bp.ParentRecordingIds[i];
                if (string.IsNullOrEmpty(pid)) continue;
                if (!suppressed.Contains(pid)) return true;
            }
            return false;
        }
    }
}
