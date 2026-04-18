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
        /// True iff <paramref name="rec"/> is an Unfinished Flight per design §3.1.
        /// Phase 2 formulation: <c>MergeState == Immutable</c> AND the terminal
        /// indicates a crash (see <see cref="IsTerminalCrashed"/>) AND the parent
        /// BranchPoint carries a non-null <c>RewindPointId</c>.
        /// The design's final form (§3.1) further requires <c>CommittedProvisional</c>
        /// AND <c>TerminalKind in {BGCrash, Crashed}</c> — Phase 2 uses the
        /// simpler formula because <c>CommittedProvisional</c> / <c>TerminalKind</c>
        /// semantics will be wired in later phases; the classifier is centralized
        /// here so the upgrade lands in one place.
        /// </summary>
        public static bool IsUnfinishedFlight(Recording rec)
        {
            if (rec == null) return false;

            string recId = rec.RecordingId ?? "<no-id>";

            if (rec.MergeState != MergeState.Immutable)
            {
                ParsekLog.Verbose("UnfinishedFlights",
                    $"IsUnfinishedFlight=false rec={recId} reason=mergeState:{rec.MergeState}");
                return false;
            }
            if (!IsTerminalCrashed(rec))
            {
                ParsekLog.Verbose("UnfinishedFlights",
                    $"IsUnfinishedFlight=false rec={recId} reason=notCrashed:{rec.TerminalStateValue}");
                return false;
            }
            if (string.IsNullOrEmpty(rec.ParentBranchPointId))
            {
                ParsekLog.Verbose("UnfinishedFlights",
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
                ParsekLog.Verbose("UnfinishedFlights",
                    $"IsUnfinishedFlight=false rec={recId} reason=noScenario");
                return false;
            }

            string bpId = rec.ParentBranchPointId;
            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                var rp = scenario.RewindPoints[i];
                if (rp == null) continue;
                if (string.Equals(rp.BranchPointId, bpId, StringComparison.Ordinal))
                {
                    // The parent BP has an RP — and RewindPoint existence IS the
                    // design §5.4 "BranchPoint.RewindPointId != null" check,
                    // since BranchPoint.RewindPointId is backfilled from the
                    // persisted RewindPoint list.
                    ParsekLog.Verbose("UnfinishedFlights",
                        $"IsUnfinishedFlight=true rec={recId} bp={bpId} rp={rp.RewindPointId}");
                    return true;
                }
            }

            ParsekLog.Verbose("UnfinishedFlights",
                $"IsUnfinishedFlight=false rec={recId} reason=noMatchingRP bp={bpId} rpCount={scenario.RewindPoints.Count}");
            return false;
        }

        /// <summary>
        /// Classifies a recording's terminal state as a crash / destroyed
        /// outcome (design §3.1 <c>TerminalKind in {BGCrash, Crashed}</c>).
        /// Phase 2 derivation: maps <see cref="TerminalState.Destroyed"/>
        /// through this helper; future phases will extend to BG-crash once
        /// the <c>TerminalKind</c> abstraction exists.
        /// </summary>
        public static bool IsTerminalCrashed(Recording rec)
        {
            if (rec == null) return false;
            if (!rec.TerminalStateValue.HasValue) return false;
            return rec.TerminalStateValue.Value == TerminalState.Destroyed;
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
        /// BranchPoint of type <c>Dock</c> / <c>Board</c> whose
        /// <c>ParentRecordingIds</c> contains any recording NOT already in
        /// the suppressed set (mixed-parent halt).
        /// Returns an empty set when <paramref name="marker"/> is null.
        /// The origin recording is always included.
        /// </summary>
        public static IReadOnlyCollection<string> ComputeSessionSuppressedSubtree(ReFlySessionMarker marker)
        {
            return ComputeSessionSuppressedSubtreeInternal(marker) ?? (IReadOnlyCollection<string>)Array.Empty<string>();
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

                while (queue.Count > 0)
                {
                    string currentId = queue.Dequeue();
                    if (!recById.TryGetValue(currentId, out var currentRec))
                        continue;

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

                        // Mixed-parent halt: only stop at Dock / Board merges whose
                        // ParentRecordingIds contain an outside parent.
                        if (IsMergeBranchPoint(bp) && HasOutsideParent(bp, result))
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
                    $"(childrenAdded={childrenAdded} mixedParentHalts={mixedParentHalts})");

                return suppressionCache;
            }
        }

        private static BranchPoint LookupBranchPoint(string treeId, string branchPointId)
        {
            if (string.IsNullOrEmpty(branchPointId)) return null;

            var trees = RecordingStore.CommittedTrees;
            if (trees == null) return null;

            for (int i = 0; i < trees.Count; i++)
            {
                var tree = trees[i];
                if (tree == null) continue;
                if (!string.IsNullOrEmpty(treeId) && tree.Id != treeId) continue;
                var bp = FindBranchPointInTree(tree, branchPointId);
                if (bp != null) return bp;
            }

            // Fallback: treeId may be null/mismatched (legacy recordings) — scan all trees.
            if (!string.IsNullOrEmpty(treeId))
            {
                for (int i = 0; i < trees.Count; i++)
                {
                    var tree = trees[i];
                    if (tree == null) continue;
                    var bp = FindBranchPointInTree(tree, branchPointId);
                    if (bp != null) return bp;
                }
            }

            return null;
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

        private static bool IsMergeBranchPoint(BranchPoint bp)
        {
            if (bp == null) return false;
            return bp.Type == BranchPointType.Dock || bp.Type == BranchPointType.Board;
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
