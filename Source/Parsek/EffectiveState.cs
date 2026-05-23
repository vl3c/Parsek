using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal enum TimelineInactiveReason
    {
        None,
        SupersededByRelation,
        RewindRetired,
    }

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
        // Runtime suppression uses the marker origin. Merge-time append can
        // walk from SupersedeTargetId for one commit, so the root participates
        // in the single-slot cache discriminator.
        private static string suppressionCacheRootOverride;
        private static HashSet<string> suppressionCache;

        // Cache for the cascade overload of ComputeRewindRetiredRecordingIds
        // when called with the live store + scenario lists. Per-frame
        // consumers (RecordingsTableUI per-row, ParsekKSC.Update per-rec) hit
        // this path with the same inputs every frame; without the cache, each
        // call recomputes the fixed-point closure over the full recordings
        // list and would also re-emit the Verbose cascade log on every call.
        // Same versioning shape as the ERS cache (StateVersion +
        // SupersedeStateVersion) because every retirement-list mutation site
        // (EnsureRewindRetirementsForRollback, LoadTimeSweep,
        // TreeDiscardPurge) bumps SupersedeStateVersion alongside the write.
        private static int retiredCacheStoreVersion = int.MinValue;
        private static int retiredCacheSupersedeVersion = int.MinValue;
        private static HashSet<string> retiredCache;

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
                suppressionCacheRootOverride = null;
                suppressionCache = null;

                retiredCacheStoreVersion = int.MinValue;
                retiredCacheSupersedeVersion = int.MinValue;
                retiredCache = null;
            }

            SupersedeCommit.ResetWorldActionSafetyCacheForTesting();
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
        /// Composite chain-then-supersede walker. From <paramref name="originRecordingId"/>,
        /// hops alternately through chain links (via <see cref="ResolveChainTerminalRecording"/>)
        /// and supersede edges (via <paramref name="supersedes"/>) until neither hop
        /// advances. Cycle-safe via a single <see cref="HashSet{T}"/> shared across BOTH
        /// hop kinds — a cross-edge cycle (e.g., chain hop A-&gt;B plus supersede hop
        /// B-&gt;A) is rejected by the visited guard, logs a Warn under the
        /// <c>[Supersede]</c> tag, and returns the last-visited id. Null / empty origin
        /// returns null.
        /// <para>
        /// Used by callers that must follow a slot's logical tip through both
        /// chain splits (e.g., rewind-UT split into HEAD + TIP) AND subsequent
        /// supersede rewrites (e.g., supersede TIP -&gt; fork): <see cref="ChildSlot.EffectiveRecordingId"/>,
        /// the <c>ResolveRewindPointSlotIndexForRecording</c> comparison hop, and the
        /// <c>IsInSupersedeForwardTrail</c> BFS chain extension. Pure-supersede readers
        /// (<see cref="IsVisible"/>, <see cref="IsSupersededByRelation"/>,
        /// <see cref="ComputeERS"/>) stay on the id-local <see cref="EffectiveRecordingId(string, IReadOnlyList{RecordingSupersedeRelation})"/>
        /// walker because chain membership alone does NOT hide a recording.
        /// </para>
        /// </summary>
        internal static string EffectiveTipRecordingId(
            string originRecordingId,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
            => EffectiveTipRecordingId(originRecordingId, supersedes, recById: null);

        /// <summary>
        /// Hot-loop overload of
        /// <see cref="EffectiveTipRecordingId(string, IReadOnlyList{RecordingSupersedeRelation})"/>
        /// taking a pre-built id → recording index. The walker's chain-hop
        /// branch does a <see cref="FindRecordingById"/> call on every
        /// iteration; without the index that's an O(N) linear scan of
        /// <see cref="RecordingStore.CommittedRecordings"/>. Batch callers
        /// (e.g. <see cref="ResolveRewindPointSlotIndexForRecording"/>
        /// iterating <c>rp.ChildSlots</c>) build the index once before the
        /// loop and pass it in. Pass <c>null</c> to fall back to the linear
        /// scan (single-shot callers, tests, the legacy overload above).
        /// Pass 5 review M2.
        /// </summary>
        internal static string EffectiveTipRecordingId(
            string originRecordingId,
            IReadOnlyList<RecordingSupersedeRelation> supersedes,
            IReadOnlyDictionary<string, Recording> recById)
        {
            if (string.IsNullOrEmpty(originRecordingId))
                return null;

            var visited = new HashSet<string>(StringComparer.Ordinal);
            string current = originRecordingId;
            visited.Add(current);

            while (true)
            {
                // Chain hop: only fires when current is a chain member with a
                // resolvable tip distinct from itself. Uses the existing tree
                // scan in ResolveChainTerminalRecording.
                //
                // Pass 6 review M1 — mid-chain supersede invariant:
                // This walker hops chain-tip BEFORE checking the supersede
                // table. For a chain {A0, A1, A2} with a supersede edge
                // anchored at the chain TIP (A2 → B), the walker correctly
                // hops A0 → A2 → B. But for a supersede edge anchored at a
                // NON-tip chain member (e.g. A1 → B), the walker would hop
                // A0 → A2 and exit (A2 has no supersede edge), silently
                // routing past B. This is currently safe because every
                // splitter-produced supersede targets the chain TIP by
                // design: RecordingTreeSplitter.SplitOriginAtRewindUT
                // creates HEAD/TIP as chain siblings with TIP at the higher
                // index, and AppendRelations writes the row at TIP. The
                // assumption holds for nested Re-Fly because each Re-Fly
                // creates a fresh chain with TIP at the highest ChainIndex.
                //
                // If a future feature introduces a mid-chain supersede
                // (e.g. retro-active history surgery, branch-edit), the
                // walker must be reworked to enumerate supersede edges on
                // every chain member, not just the tip — see the test
                // EffectiveTipRecordingId_MidChainSupersede_AssumptionHolds
                // for a concrete fixture showing the current (correct-by-
                // assumption) behavior.
                Recording currentRec = LookupRecordingId(current, recById);
                if (currentRec != null && !string.IsNullOrEmpty(currentRec.ChainId))
                {
                    Recording chainTip = ResolveChainTerminalRecording(currentRec);
                    if (chainTip != null
                        && !string.IsNullOrEmpty(chainTip.RecordingId)
                        && !string.Equals(chainTip.RecordingId, current, StringComparison.Ordinal))
                    {
                        string nextChain = chainTip.RecordingId;
                        if (!visited.Add(nextChain))
                        {
                            ParsekLog.Warn("Supersede",
                                $"EffectiveTipRecordingId: cycle detected at chain-hop from {current} to {nextChain}; returning last-visited={current}");
                            return current;
                        }
                        current = nextChain;
                        continue;
                    }
                }

                // Supersede hop: scan supersedes for OldRecordingId == current.
                string next = null;
                if (supersedes != null)
                {
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
                }

                if (string.IsNullOrEmpty(next))
                    return current; // neither hop advanced; current is the tip

                if (!visited.Add(next))
                {
                    ParsekLog.Warn("Supersede",
                        $"EffectiveTipRecordingId: cycle detected at supersede-hop from {current} to {next}; returning last-visited={current}");
                    return current;
                }
                current = next;
            }
        }

        /// <summary>
        /// Looks up a recording in <see cref="RecordingStore.CommittedRecordings"/>
        /// by id (ordinal compare). Returns null on null/empty id, or when no
        /// recording matches. Used by <see cref="EffectiveTipRecordingId"/> to
        /// resolve the chain hop without taking a dependency on
        /// <c>SupersedeCommit.BuildCommittedRecordingIndex</c>.
        /// </summary>
        private static Recording FindRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return null;
            var source = RecordingStore.CommittedRecordings;
            if (source == null)
                return null;
            for (int i = 0; i < source.Count; i++)
            {
                var rec = source[i];
                if (rec == null) continue;
                if (string.Equals(rec.RecordingId, recordingId, StringComparison.Ordinal))
                    return rec;
            }
            return null;
        }

        /// <summary>
        /// Raw committed lookup by id, exposed so the open/closed read in
        /// <see cref="UnfinishedFlightClassifier.IsSlotEffectiveTipOpen"/> can
        /// inspect a slot's effective tip MergeState (CommittedProvisional =
        /// open, Immutable = closed) without the classifier itself touching
        /// <c>RecordingStore.CommittedRecordings</c> (which the ERS/ELS grep
        /// gate would flag). The open/closed signal must see NotCommitted /
        /// CommittedProvisional states that ERS would filter out. Scans the flat
        /// committed list first, then falls back to the committed trees so a
        /// chain-tip recording that lives in a committed tree (the same source
        /// <see cref="ResolveChainTerminalRecording"/> walks) resolves even when
        /// it has not been mirrored into the flat list.
        /// </summary>
        internal static Recording FindCommittedRecordingByIdRaw(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            var direct = FindRecordingById(recordingId);
            if (direct != null) return direct;

            var trees = RecordingStore.CommittedTrees;
            if (trees != null)
            {
                for (int t = 0; t < trees.Count; t++)
                {
                    var tree = trees[t];
                    if (tree?.Recordings == null) continue;
                    if (tree.Recordings.TryGetValue(recordingId, out var rec) && rec != null)
                        return rec;
                }
            }
            return null;
        }

        /// <summary>
        /// Pass 5 review M2: dict-aware lookup. Returns
        /// <paramref name="recById"/>[<paramref name="recordingId"/>] when
        /// the index is present, falling back to
        /// <see cref="FindRecordingById"/>'s linear scan otherwise. Batches
        /// of walker calls (the hot path in
        /// <see cref="ResolveRewindPointSlotIndexForRecording"/>) build the
        /// index once and pass it through to amortize lookups across the
        /// slot loop.
        /// </summary>
        private static Recording LookupRecordingId(
            string recordingId,
            IReadOnlyDictionary<string, Recording> recById)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            if (recById != null && recById.TryGetValue(recordingId, out var rec))
                return rec;
            return FindRecordingById(recordingId);
        }

        /// <summary>
        /// Pass 5 review M2: builds the id → recording index used by the
        /// walker / forward-trail dict overloads. Equivalent to
        /// <see cref="SupersedeCommit"/>'s internal builder but lives here
        /// so EffectiveState's hot batch callers (in particular
        /// <see cref="ResolveRewindPointSlotIndexForRecording"/>) don't need
        /// to cross-reference another file's private helper. Returns an
        /// empty dictionary on null store.
        /// </summary>
        internal static Dictionary<string, Recording> BuildRecordingIdIndex()
        {
            var index = new Dictionary<string, Recording>(StringComparer.Ordinal);
            var source = RecordingStore.CommittedRecordings;
            if (source == null) return index;
            for (int i = 0; i < source.Count; i++)
            {
                var rec = source[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;
                if (!index.ContainsKey(rec.RecordingId))
                    index.Add(rec.RecordingId, rec);
            }
            return index;
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

        internal static int ResolveRewindPointSlotIndexForRecording(
            RewindPoint rp,
            Recording rec,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (rp == null || rp.ChildSlots == null || rec == null)
                return -1;
            if (string.IsNullOrEmpty(rec.RecordingId))
                return -1;

            var effectiveSupersedes = supersedes ?? Array.Empty<RecordingSupersedeRelation>();
            // Pass 5 review M2: build the id → recording index once before
            // the slot loop. Both EffectiveTipRecordingId and
            // IsInSupersedeForwardTrail iterate FindRecordingById in their
            // chain-hop branches; the dict overload amortizes those scans
            // across every slot iteration.
            Dictionary<string, Recording> recById = null;
            for (int i = 0; i < rp.ChildSlots.Count; i++)
            {
                var slot = rp.ChildSlots[i];
                if (slot == null) continue;
                string origin = slot.OriginChildRecordingId;
                if (string.Equals(origin, rec.RecordingId, StringComparison.Ordinal))
                    return i;
                string effective = EffectiveRecordingId(origin, effectiveSupersedes);
                if (string.Equals(effective, rec.RecordingId, StringComparison.Ordinal))
                    return i;
                // Composite tip match: follows the chain-then-supersede walker so
                // a fork can match its slot's origin even when a rewind-UT split
                // separates the slot's origin id (HEAD) from the supersede target
                // (TIP) via a chain hop before the supersede edge to fork.
                if (recById == null) recById = BuildRecordingIdIndex();
                string compositeEffective = EffectiveTipRecordingId(origin, effectiveSupersedes, recById);
                if (!string.IsNullOrEmpty(compositeEffective)
                    && !string.Equals(compositeEffective, effective, StringComparison.Ordinal)
                    && string.Equals(compositeEffective, rec.RecordingId, StringComparison.Ordinal))
                    return i;
                if (IsInSupersedeForwardTrail(origin, rec.RecordingId, effectiveSupersedes, recById))
                    return i;
            }

            return -1;
        }

        private static bool IsInSupersedeForwardTrail(
            string originRecordingId,
            string targetRecordingId,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
            => IsInSupersedeForwardTrail(originRecordingId, targetRecordingId, supersedes, recById: null);

        /// <summary>
        /// Pass 5 review M2: dict-aware overload, mirrors
        /// <see cref="EffectiveTipRecordingId(string, IReadOnlyList{RecordingSupersedeRelation}, IReadOnlyDictionary{string, Recording})"/>.
        /// The BFS frontier in this method also does
        /// <see cref="FindRecordingById"/> on every dequeue for the chain-hop
        /// branch — pre-built index amortizes the same linear scan across
        /// the search.
        /// </summary>
        private static bool IsInSupersedeForwardTrail(
            string originRecordingId,
            string targetRecordingId,
            IReadOnlyList<RecordingSupersedeRelation> supersedes,
            IReadOnlyDictionary<string, Recording> recById)
        {
            // Needed for hybrid legacy-star plus new-linear graphs where a
            // mid-chain recording is still part of the slot even when it is no
            // longer the EffectiveRecordingId tip. Now extended with chain hops
            // so the BFS frontier includes both supersede children AND the
            // chain tip of each dequeued node — required after rewind-UT splits
            // separate the slot's origin (HEAD) from the supersede edge target
            // (TIP) via a chain link.
            if (string.IsNullOrEmpty(originRecordingId)
                || string.IsNullOrEmpty(targetRecordingId))
                return false;

            bool hasSupersedes = supersedes != null && supersedes.Count > 0;

            var visited = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            visited.Add(originRecordingId);
            queue.Enqueue(originRecordingId);

            while (queue.Count > 0)
            {
                string current = queue.Dequeue();

                // Chain hop: before walking supersede edges, enqueue current's
                // chain tip if it's a distinct recording id. Visited guard
                // prevents revisiting; reaching the target via chain alone
                // counts as in-trail.
                Recording currentRec = LookupRecordingId(current, recById);
                if (currentRec != null && !string.IsNullOrEmpty(currentRec.ChainId))
                {
                    Recording chainTip = ResolveChainTerminalRecording(currentRec);
                    if (chainTip != null
                        && !string.IsNullOrEmpty(chainTip.RecordingId)
                        && !string.Equals(chainTip.RecordingId, current, StringComparison.Ordinal))
                    {
                        string nextChain = chainTip.RecordingId;
                        if (string.Equals(nextChain, targetRecordingId, StringComparison.Ordinal))
                            return true;
                        if (visited.Add(nextChain))
                            queue.Enqueue(nextChain);
                    }
                }

                if (!hasSupersedes) continue;

                for (int i = 0; i < supersedes.Count; i++)
                {
                    var rel = supersedes[i];
                    if (rel == null) continue;
                    if (!string.Equals(rel.OldRecordingId, current, StringComparison.Ordinal))
                        continue;
                    string next = rel.NewRecordingId;
                    if (string.IsNullOrEmpty(next)) continue;
                    if (string.Equals(next, targetRecordingId, StringComparison.Ordinal))
                        return true;
                    if (visited.Add(next))
                        queue.Enqueue(next);
                }
            }

            return false;
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
        /// Computes the subset of <paramref name="recordings"/> replaced by an
        /// explicit supersede relation. This is the raw-index compatibility
        /// helper for systems that still iterate <see cref="RecordingStore.CommittedRecordings"/>
        /// but must suppress old recordings consistently with ERS.
        /// </summary>
        internal static HashSet<string> ComputeSupersededRecordingIdsByRelation(
            IReadOnlyList<Recording> recordings,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (recordings == null || recordings.Count == 0)
                return result;
            if (supersedes == null || supersedes.Count == 0)
                return result;

            var liveIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                liveIds.Add(rec.RecordingId);
            }

            for (int i = 0; i < supersedes.Count; i++)
            {
                var rel = supersedes[i];
                if (rel == null || string.IsNullOrEmpty(rel.OldRecordingId))
                    continue;
                if (string.IsNullOrEmpty(rel.NewRecordingId))
                    continue;
                if (!liveIds.Contains(rel.OldRecordingId))
                    continue;
                if (string.Equals(rel.OldRecordingId, rel.NewRecordingId, StringComparison.Ordinal))
                    continue;
                result.Add(rel.OldRecordingId);
            }

            return result;
        }

        internal static HashSet<string> ComputeRewindRetiredRecordingIds(
            IReadOnlyList<RecordingRewindRetirement> retirements)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (retirements == null || retirements.Count == 0)
                return result;

            for (int i = 0; i < retirements.Count; i++)
            {
                var retirement = retirements[i];
                if (retirement == null || string.IsNullOrEmpty(retirement.RecordingId))
                    continue;
                result.Add(retirement.RecordingId);
            }

            return result;
        }

        /// <summary>
        /// Cascade overload: returns the retired id set plus every recording
        /// reachable by two edges. (1) Parent-anchor: any recording whose
        /// <see cref="Recording.ParentAnchorRecordingId"/> resolves
        /// (transitively) to a retired id. (2) Rolled-back-fork chain
        /// continuation: a rolled-back Re-Fly retires only its fork's chain
        /// HEAD (the dropped supersede relation's <c>NewRecordingId</c>), so a
        /// higher-<see cref="Recording.ChainIndex"/> member sharing the head's
        /// <see cref="Recording.ChainId"/> AND
        /// <see cref="Recording.ProvisionalForRpId"/> is part of the same
        /// rolled-back fork and is retired too (otherwise it survives and renders
        /// as a duplicate ghost alongside the restored original). The
        /// ProvisionalForRpId match scopes the chain edge so a legitimate
        /// committed chain that merely shares a ChainId is never over-retired.
        /// Fixed-point closure walks both edges until no more are added; bounded
        /// by the recording count and acyclic by the parent-anchor contract
        /// (a child cannot be its own ancestor) and the strictly-increasing
        /// ChainIndex direction of the chain edge.
        ///
        /// <para>Visibility consumers (ERS, timeline-inactive map, ghost
        /// playback skip-state, KSC marker gate, tracking-station spawn
        /// suppression, recordings-table inactive predicate) must use this
        /// overload so that parent-anchored debris of a retired recording
        /// inherits the retirement and does not render as an orphan ghost
        /// alongside the restored recording's own debris.</para>
        ///
        /// <para>Retirement-writing paths
        /// (<c>EnsureRewindRetirementsForRollback</c>) keep using the raw
        /// per-retirement overload: their working set deduplicates rows being
        /// written, not visibility-derived cascade ids.</para>
        /// </summary>
        internal static HashSet<string> ComputeRewindRetiredRecordingIds(
            IReadOnlyList<Recording> recordings,
            IReadOnlyList<RecordingRewindRetirement> retirements)
        {
            // Per-frame consumers (RecordingsTableUI per-row,
            // ParsekKSC.Update per-rec, GhostMapPresence) call this every
            // frame with the live store + scenario lists. Fast-path the
            // call so each per-frame loop pays the cascade closure cost
            // once across the (StateVersion, SupersedeStateVersion) window
            // instead of N times per frame. Pure-function tests (which
            // construct ad-hoc lists) miss reference equality and run the
            // compute path directly, which keeps the testable contract honest.
            var scenario = ParsekScenario.Instance;
            // Prefix difference is deliberate and matches the file-wide
            // convention (see ComputeERS): object.ReferenceEquals(null, x)
            // for the Unity-null bypass (Object overloads ==), bare
            // ReferenceEquals(a, b) for pure reference identity.
            bool isLiveStoreCall = !object.ReferenceEquals(null, scenario)
                && ReferenceEquals(recordings, RecordingStore.CommittedRecordings)
                && ReferenceEquals(retirements, scenario.RecordingRewindRetirements);
            int storeVersion = 0;
            int supersedeVersion = 0;
            if (isLiveStoreCall)
            {
                // Capture versions BEFORE compute and stamp the cache with
                // those same captured values after compute, matching the
                // ERS cache contract above. If we re-read at store time, a
                // bump that interleaved with compute would stamp the cache
                // with the post-bump version while the result reflects
                // pre-bump inputs, a stale entry that would survive until
                // the next bump.
                storeVersion = RecordingStore.StateVersion;
                supersedeVersion = scenario.SupersedeStateVersion;
                lock (syncRoot)
                {
                    if (retiredCache != null
                        && retiredCacheStoreVersion == storeVersion
                        && retiredCacheSupersedeVersion == supersedeVersion)
                    {
                        return retiredCache;
                    }
                }
            }

            var result = ComputeRewindRetiredRecordingIdsUncached(recordings, retirements);

            if (isLiveStoreCall)
            {
                lock (syncRoot)
                {
                    retiredCache = result;
                    retiredCacheStoreVersion = storeVersion;
                    retiredCacheSupersedeVersion = supersedeVersion;
                }
            }
            return result;
        }

        private static HashSet<string> ComputeRewindRetiredRecordingIdsUncached(
            IReadOnlyList<Recording> recordings,
            IReadOnlyList<RecordingRewindRetirement> retirements)
        {
            // Seed: the raw per-retirement set. This allocation is NOT
            // memoized by the caller's cache (only the cascade-expanded
            // result HashSet is); the seed scan is O(retirements) and runs
            // on every cache miss, which is fine because misses only happen
            // when StateVersion / SupersedeStateVersion actually changed.
            var result = ComputeRewindRetiredRecordingIds(retirements);
            int seedCount = result.Count;
            if (seedCount == 0 || recordings == null || recordings.Count == 0)
                return result;

            // Dropped-fork chain lookup. A rolled-back Re-Fly drops the fork's
            // supersede relation and retires only the relation's NewRecordingId,
            // which names the fork CHAIN HEAD. The head's chain continuations
            // (the post-seam tail that carries the predicted orbit / later
            // segments) are never named, so without this they survive and render
            // as a duplicate ghost alongside the restored original. Key the seed
            // dropped-fork heads by (ChainId, ProvisionalForRpId) -> minimum
            // ChainIndex; a higher-index member sharing that key is part of the
            // same rolled-back provisional fork. The ProvisionalForRpId match is
            // the load-bearing guard: it scopes the cascade to recordings that
            // are provisional-for-the-same-rolled-back-RP, so a legitimate
            // committed chain that merely shares a ChainId (or a kept origin-split
            // HEAD) is never over-retired.
            var seedForkChains = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec == null
                    || string.IsNullOrEmpty(rec.RecordingId)
                    || !result.Contains(rec.RecordingId)
                    || string.IsNullOrEmpty(rec.ChainId)
                    || string.IsNullOrEmpty(rec.ProvisionalForRpId))
                    continue;
                string key = rec.ChainId + "|" + rec.ProvisionalForRpId;
                if (!seedForkChains.TryGetValue(key, out int minIndex) || rec.ChainIndex < minIndex)
                    seedForkChains[key] = rec.ChainIndex;
            }

            int parentAnchorAdded = 0;
            int chainContinuationAdded = 0;
            int cascadeAdded;
            do
            {
                cascadeAdded = 0;
                for (int i = 0; i < recordings.Count; i++)
                {
                    var rec = recordings[i];
                    if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                        continue;
                    if (result.Contains(rec.RecordingId))
                        continue;

                    // Parent-anchor edge: debris of a retired recording inherits
                    // the retirement.
                    if (!string.IsNullOrEmpty(rec.ParentAnchorRecordingId)
                        && result.Contains(rec.ParentAnchorRecordingId))
                    {
                        result.Add(rec.RecordingId);
                        cascadeAdded++;
                        parentAnchorAdded++;
                        continue;
                    }

                    // Chain-continuation edge: a higher-index continuation of a
                    // rolled-back re-fly fork head, sharing the fork's RP.
                    if (!string.IsNullOrEmpty(rec.ChainId)
                        && !string.IsNullOrEmpty(rec.ProvisionalForRpId)
                        && seedForkChains.TryGetValue(
                            rec.ChainId + "|" + rec.ProvisionalForRpId, out int seedIndex)
                        && rec.ChainIndex > seedIndex)
                    {
                        result.Add(rec.RecordingId);
                        cascadeAdded++;
                        chainContinuationAdded++;
                    }
                }
            }
            while (cascadeAdded > 0);

            int totalAdded = result.Count - seedCount;
            if (totalAdded > 0)
            {
                // Log fires only on cache miss for live-store callers
                // (above) or on every call for ad-hoc/test callers; both
                // paths naturally rate-limit because the work itself is
                // gated on the same condition. Stable per-frame repeats
                // never reach this site.
                ParsekLog.Verbose("ERS",
                    $"Rewind-retirement cascade: seed={seedCount.ToString(CultureInfo.InvariantCulture)} " +
                    $"parentAnchorAdded={parentAnchorAdded.ToString(CultureInfo.InvariantCulture)} " +
                    $"chainContinuationAdded={chainContinuationAdded.ToString(CultureInfo.InvariantCulture)} " +
                    $"finalRetired={result.Count.ToString(CultureInfo.InvariantCulture)} " +
                    $"recordingsScanned={recordings.Count.ToString(CultureInfo.InvariantCulture)} " +
                    "(parent-anchored descendants and rolled-back-fork chain continuations hidden via cascade)");
            }
            return result;
        }

        internal static Dictionary<string, TimelineInactiveReason> ComputeTimelineInactiveRecordingIds(
            IReadOnlyList<Recording> recordings,
            IReadOnlyList<RecordingSupersedeRelation> supersedes,
            IReadOnlyList<RecordingRewindRetirement> retirements)
        {
            var result = new Dictionary<string, TimelineInactiveReason>(StringComparer.Ordinal);
            var superseded = ComputeSupersededRecordingIdsByRelation(recordings, supersedes);
            foreach (string id in superseded)
                result[id] = TimelineInactiveReason.SupersededByRelation;

            var retired = ComputeRewindRetiredRecordingIds(recordings, retirements);
            foreach (string id in retired)
                result[id] = TimelineInactiveReason.RewindRetired;

            return result;
        }

        internal static bool IsRewindRetired(
            Recording rec,
            IReadOnlyList<RecordingRewindRetirement> retirements)
        {
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return false;
            if (retirements == null || retirements.Count == 0)
                return false;

            for (int i = 0; i < retirements.Count; i++)
            {
                var retirement = retirements[i];
                if (retirement == null || string.IsNullOrEmpty(retirement.RecordingId))
                    continue;
                if (string.Equals(retirement.RecordingId, rec.RecordingId, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Cascade overload: true when <paramref name="rec"/> is directly
        /// retired OR is a parent-anchored descendant of a retired recording.
        /// Use at every visibility site that has access to the recordings
        /// list; orphan debris of a retired re-fly fork would otherwise
        /// render alongside the restored recording's own debris.
        /// </summary>
        internal static bool IsRewindRetired(
            Recording rec,
            IReadOnlyList<Recording> recordings,
            IReadOnlyList<RecordingRewindRetirement> retirements)
        {
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return false;
            if (retirements == null || retirements.Count == 0)
                return false;
            var retired = ComputeRewindRetiredRecordingIds(recordings, retirements);
            return retired.Contains(rec.RecordingId);
        }

        /// <summary>
        /// True iff <paramref name="rec"/> is an OPEN Unfinished Flight:
        /// committed visible, mapped to a RewindPoint child slot, with a
        /// qualifying terminal shape per <see cref="UnfinishedFlightClassifier"/>,
        /// AND the slot's effective tip MergeState is CommittedProvisional
        /// (open). A sealed / concluded tip (Immutable) is closed and returns
        /// false.
        /// </summary>
        public static bool IsUnfinishedFlight(Recording rec)
        {
            RewindPoint rp;
            int slotListIndex;
            return TryResolveUnfinishedFlight(rec, out rp, out slotListIndex);
        }

        /// <summary>
        /// Consumer-facing wrapper used by every site that asks "is this
        /// recording the row that represents an unfinished flight in the
        /// UI?" — the STASH group (<see cref="UnfinishedFlightsGroup.ComputeMembers"/>),
        /// the legacy-rewind R-button suppression, the timeline separation
        /// marker (<see cref="Parsek.Timeline.TimelineBuilder"/>), the
        /// group-picker drop-target gate, the hide-checkbox refusal, and
        /// the timeline-watch L-button gate.
        /// <para>
        /// Slot-anchor dedupe: every recording that resolves to the same
        /// (RewindPoint, slot) tuple via the chain-and-supersede walker in
        /// <see cref="ResolveRewindPointSlotIndexForRecording"/> passes the
        /// raw classifier — that lookup is load-bearing for
        /// <see cref="RewindPointReaper.IsReapEligible"/>, which feeds the
        /// classifier one slot's chain-tip recording at a time and must
        /// keep getting <c>true</c> so the rewind point stays alive
        /// (pinned by
        /// <c>RewindPointReaperTests.Reap_ImmutableDestroyedChainTipSlot_KeepsRpAlive</c>).
        /// The consumer-facing predicate has the opposite need: emit one
        /// row per logical flight, not one row per chain or supersede peer.
        /// We pick a single anchor recording per slot — the slot's
        /// <c>OriginChildRecordingId</c> when that recording is itself
        /// ERS-visible, otherwise the chain-and-supersede-walked
        /// <see cref="EffectiveTipRecordingId"/> from the origin. Every
        /// other peer that maps to the same slot suppresses. This covers
        /// both classical optimizer-split chain shapes (HEAD + TIP in the
        /// same ChainId, slot.Origin == HEAD wins) and re-fly supersede
        /// shapes where the supersede target has no ChainId (slot.Origin
        /// still wins; the supersede target suppresses). Asymmetric chain
        /// shapes where only the chain TIP carries BP linkage and the HEAD
        /// fails Raw (pinned by
        /// <c>ChainContinuationWhoseHeadFailsRaw_StillSurfacesAsUnfinishedFlight</c>)
        /// are unaffected: HEAD fails Raw and never enters this dedupe;
        /// whichever recording the rewind point lists as <c>slot.Origin</c>
        /// (the BP-linked TIP in the fixture) becomes the anchor directly
        /// because it's ERS-visible.
        /// </para>
        /// <para>
        /// Malformed-slot safety: if the slot's origin id points at a
        /// recording that no longer exists in <see cref="RecordingStore"/>
        /// (or whose effective tip walker collapses to a hidden /
        /// NotCommitted target), the anchor cannot be resolved to a
        /// visible recording and we admit <paramref name="rec"/> rather
        /// than silently suppress every peer. Better to surface a
        /// (possibly duplicate) row than to make the slot disappear
        /// entirely from the UI.
        /// </para>
        /// <para>
        /// Perf TODO: the wrapper does an O(supersedes) walk per call (the
        /// <see cref="EffectiveTipRecordingId"/> fallback), and
        /// <see cref="IsChainMemberOfUnfinishedFlight"/> calls this wrapper
        /// inside its own O(ERS) scan, so chain-aware UI rendering is
        /// O(N·supersedes) per row, O(N²·supersedes) per frame in the
        /// worst case. Acceptable at expected save sizes (≤ a few hundred
        /// committed recordings); memoize on
        /// (<see cref="RecordingStore.StateVersion"/>,
        /// <see cref="ParsekScenario.SupersedeStateVersion"/>) if it ever
        /// shows up in a profile.
        /// </para>
        /// </summary>
        internal static bool TryResolveUnfinishedFlight(
            Recording rec,
            out RewindPoint rp,
            out int slotListIndex)
        {
            if (!TryResolveUnfinishedFlightRaw(rec, out rp, out slotListIndex))
                return false;

            // Slot-anchor dedupe: choose one row per (RP, slot) regardless
            // of how many ERS-visible recordings map to it. Anchor =
            // slot.OriginChildRecordingId when that recording is itself
            // ERS-visible (it IS the launch / origin row), else the
            // chain+supersede walker tip from that origin (the visible
            // effective head when the origin has been superseded).
            var slot = rp != null && rp.ChildSlots != null
                && slotListIndex >= 0 && slotListIndex < rp.ChildSlots.Count
                ? rp.ChildSlots[slotListIndex]
                : null;
            string origin = slot?.OriginChildRecordingId;
            if (string.IsNullOrEmpty(origin))
                return true; // origin-less slot — no anchor to dedupe against

            var supersedes = ParsekScenario.Instance?.RecordingSupersedes
                ?? (IReadOnlyList<RecordingSupersedeRelation>)Array.Empty<RecordingSupersedeRelation>();

            string anchorId = IsRecordingIdErsVisible(origin, supersedes)
                ? origin
                : EffectiveTipRecordingId(origin, supersedes);

            // Admit when rec IS the anchor, or when the anchor cannot be
            // resolved to a visible recording at all (malformed slot —
            // origin id has no matching recording, or the walker tip is
            // hidden / NotCommitted). Suppressing all peers in that case
            // would silently hide the entire slot from the UI, which is
            // worse than showing a (possibly duplicate) row.
            if (string.IsNullOrEmpty(anchorId)
                || string.Equals(rec.RecordingId, anchorId, StringComparison.Ordinal))
                return true;

            if (!IsRecordingIdErsVisible(anchorId, supersedes))
            {
                ParsekLog.VerboseRateLimited("UnfinishedFlights",
                    $"slotAnchorUnresolved-{rec.RecordingId ?? "<no-id>"}",
                    $"IsUnfinishedFlight=true rec={rec.RecordingId ?? "<no-id>"} " +
                    $"reason=slotAnchorUnresolved rp={rp?.RewindPointId ?? "<no-rp>"} " +
                    $"slot={slotListIndex} slotOrigin={origin ?? "<no-origin>"} " +
                    $"anchorRec={anchorId} (admitted; anchor not ERS-visible)");
                return true;
            }

            ParsekLog.VerboseRateLimited("UnfinishedFlights",
                $"slotPeerAnchored-{rec.RecordingId ?? "<no-id>"}",
                $"IsUnfinishedFlight=false rec={rec.RecordingId ?? "<no-id>"} " +
                $"reason=slotPeerAnchored rp={rp?.RewindPointId ?? "<no-rp>"} " +
                $"slot={slotListIndex} slotOrigin={origin} anchorRec={anchorId}");
            rp = null;
            slotListIndex = -1;
            return false;
        }

        private static bool IsRecordingIdErsVisible(
            string recordingId,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (string.IsNullOrEmpty(recordingId)) return false;
            var source = RecordingStore.CommittedRecordings;
            if (source == null) return false;
            for (int i = 0; i < source.Count; i++)
            {
                var candidate = source[i];
                if (candidate == null) continue;
                if (!string.Equals(candidate.RecordingId, recordingId, StringComparison.Ordinal))
                    continue;
                return IsVisible(candidate, supersedes);
            }
            return false;
        }

        private static bool TryResolveUnfinishedFlightRaw(
            Recording rec,
            out RewindPoint rp,
            out int slotListIndex)
        {
            rp = null;
            slotListIndex = -1;
            if (rec == null) return false;

            string recId = rec.RecordingId ?? "<no-id>";

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

            string resolveReason;
            if (!UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                    rec, out rp, out slotListIndex, out resolveReason))
            {
                ParsekLog.VerboseRateLimited("UnfinishedFlights",
                    $"{resolveReason}-{recId}",
                    $"IsUnfinishedFlight=false rec={recId} reason={resolveReason} " +
                    $"parentBp={rec.ParentBranchPointId ?? "<none>"} childBp={rec.ChildBranchPointId ?? "<none>"} " +
                    $"rpCount={scenario.RewindPoints.Count}");
                return false;
            }

            var slot = rp.ChildSlots[slotListIndex];
            string qualifyReason;
            bool qualifies = UnfinishedFlightClassifier.TryQualify(
                rec, slot, rp, out qualifyReason);
            if (!qualifies)
            {
                rp = null;
                slotListIndex = -1;
                return false;
            }

            // Open/closed filter: a slot is an OPEN unfinished flight only when
            // its effective chain+supersede tip is CommittedProvisional. A
            // sealed / concluded tip (Immutable) is closed and must not surface
            // as a UF row, even though its terminal shape still qualifies. This
            // filter runs BEFORE the anchor-dedupe admit-on-unresolved fallback
            // in TryResolveUnfinishedFlight so an Immutable peer cannot slip
            // through the malformed-slot path (collapse-seal-into-mergestate
            // plan §7.7).
            if (!UnfinishedFlightClassifier.IsSlotEffectiveTipOpen(slot, rec))
            {
                ParsekLog.VerboseRateLimited("UnfinishedFlights",
                    $"sealedTipClosed-{recId}",
                    $"IsUnfinishedFlight=false rec={recId} reason=sealedTipClosed " +
                    $"rp={rp.RewindPointId ?? "<no-rp>"} slot={slotListIndex}");
                rp = null;
                slotListIndex = -1;
                return false;
            }

            return true;
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
            IReadOnlyList<RecordingRewindRetirement> retirements = hasScenario ? scenario.RecordingRewindRetirements : null;
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

                var suppressed = ComputeSubtreeClosureInternal(
                    marker, marker?.OriginChildRecordingId);
                var source = RecordingStore.CommittedRecordings;
                var retiredIds = ComputeRewindRetiredRecordingIds(source, retirements);
                var result = new List<Recording>(source.Count);
                int raw = source.Count;
                int skippedNotCommitted = 0;
                int skippedSuperseded = 0;
                int skippedRewindRetired = 0;
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
                    if (!string.IsNullOrEmpty(rec.RecordingId)
                        && retiredIds.Contains(rec.RecordingId))
                    {
                        skippedRewindRetired++;
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
                    $"skippedRewindRetired={skippedRewindRetired} skippedSuppressed={skippedSuppressed} " +
                    $"marker={(marker != null ? marker.SessionId ?? "<no-id>" : "none")})");

                return ersCache;
            }
        }

        /// <summary>
        /// Computes the Effective Ledger Set per design §3.2: ledger actions
        /// whose <see cref="GameAction.ActionId"/> is NOT in any
        /// <see cref="LedgerTombstone"/>. There is no generic recording-level
        /// filter here; broad supersede retirement is expressed by explicit
        /// tombstone rows written only for reviewed eligible action types.
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
            if (marker == null)
                return Array.Empty<string>();
            var cached = ComputeSubtreeClosureInternal(marker, marker.OriginChildRecordingId);
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
            var closure = ComputeSubtreeClosureInternal(marker, marker.OriginChildRecordingId);
            return closure != null && closure.Contains(rec.RecordingId);
        }

        // Internal closure computation that returns the cached HashSet directly
        // so ComputeERS can do O(1) membership checks.
        internal static HashSet<string> ComputeSubtreeClosureInternal(
            ReFlySessionMarker marker, string rootOverride)
        {
            if (marker == null || string.IsNullOrEmpty(rootOverride))
                return null;

            int storeVersion = RecordingStore.StateVersion;

            lock (syncRoot)
            {
                if (suppressionCache != null
                    && ReferenceEquals(suppressionCacheMarkerIdentity, marker)
                    && suppressionCacheStoreVersion == storeVersion
                    && string.Equals(
                        suppressionCacheRootOverride,
                        rootOverride,
                        StringComparison.Ordinal))
                {
                    return suppressionCache;
                }

                var result = new HashSet<string>(StringComparer.Ordinal);
                result.Add(rootOverride);

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
                //
                // Same-PID-only BP-children gate (bug fix-refly-suppress-side-off,
                // 2026-04-27): the BP-children walk only enqueues children whose
                // VesselPersistentId matches the dequeued recording's
                // VesselPersistentId. The re-fly only re-records the SAME-PID
                // linear continuation (e.g. upper-stage U after staging is the
                // same PID as the pre-staging vessel). Side-off branches
                // (different PID — e.g. lower stage L after a Decouple BP) are
                // separate physical vessels with their own ChainId / lineage and
                // are NOT being re-flown by this session. Including them in the
                // closure suppressed every previous side-off ghost during a re-fly
                // (visible symptom: the previous L booster vanished from flight,
                // map view, and ghost playback while re-flying U). The new flight
                // will produce its OWN side-off recordings if it stages, and
                // those will supersede the old ones at that future moment via
                // the standard supersede-row path. PID 0 (legacy / unset) on
                // EITHER side falls back to the prior wide-walk behavior — when
                // one PID is unknown we cannot tell side-off from same-vessel
                // continuation, so we admit the child rather than silently
                // dropping a possibly-legitimate continuation from the closure
                // (and from supersede / tombstone generation). Only the fully
                // known asymmetric case (both PIDs nonzero and different) is
                // confidently a side-off.
                //
                // Cross-chain same-PID peers (different ChainId, same PID) are
                // still picked up by EnqueuePidPeerSiblings. Same-chain segments
                // (HEAD/TIP from SplitAtSection) are still picked up by
                // EnqueueChainSiblings — those are by-construction same physical
                // vessel and the chain identity guarantees the same PID.
                var queue = new Queue<string>();
                queue.Enqueue(rootOverride);

                int mixedParentHalts = 0;
                int childrenAdded = 0;
                int siblingsAdded = 0;
                int pidPeersAdded = 0;
                int sideOffSkips = 0;
                int debrisAdded = 0;
                int debrisAnchorOnlySkips = 0;

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

                    // Cross-chain same-PID peer expansion: in-place re-fly that
                    // crosses staging spawns sibling chains for the SAME physical
                    // vessel (same VesselPersistentId) but in DIFFERENT ChainIds.
                    // Example from KSP.log 2026-04-26: origin f3f1f2e6 (chain
                    // 301a95f0) re-flew the probe; staging produced new chain
                    // 0a83aee0 (correctly picked up by EnqueueChainSiblings) AND
                    // a destroyed sibling 29f1d9a8 in chain 73e8a066 (PID
                    // 2450432355 == origin's PID). Without this walk the
                    // destroyed sibling never enters the closure, AppendRelations
                    // sees subtreeCount=2 with both members chain-skipped, and
                    // 0 supersede rows are written → the destroyed run stays
                    // visible in mission list / ghost playback. Gated on
                    // StartUT > marker.InvokedUT so pre-rewind PID history is
                    // not collapsed into the closure.
                    EnqueuePidPeerSiblings(
                        currentRec, recById, queue, result, marker, ref pidPeersAdded);

                    // Debris-children expansion (v12 `Recording.ParentAnchorRecordingId`):
                    // Breakup BPs do not register a back-pointer through
                    // `Recording.ChildBranchPointId` on the parent (that field
                    // is single-slot and a destroyed parent commonly has many
                    // breakup BPs), and even when it is set the same-PID gate
                    // below would cull every debris row because debris is born
                    // as a fresh KSP-assigned VesselPersistentId. Use the v12
                    // direct ownership link to admit debris children whose
                    // ParentBranchPointId resolves to a Breakup BP that this
                    // recording owns — the topology side of the dual-purpose
                    // ParentAnchorRecordingId field. Anchor-only matches
                    // (background splits where the field is re-pointed to the
                    // parent continuation as a sampling anchor) are gated out
                    // here and left to the existing same-PID side-off filter.
                    EnqueueDebrisChildren(
                        currentRec, recById, queue, result,
                        ref debrisAdded, ref debrisAnchorOnlySkips);

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

                        // Same-PID-only gate: skip side-off branches (different
                        // VesselPersistentId from the current dequeued recording).
                        // See block comment above for the design rationale.
                        // Gate is bypassed whenever EITHER side's PID is 0
                        // (unknown / unset — legacy data or freshly seeded
                        // pre-PID records). With one side unknown we can't
                        // tell side-off from same-vessel continuation, so we
                        // preserve the prior wide-walk behavior and admit the
                        // child. The fully-known case (both PIDs nonzero and
                        // different) is the only one we can confidently call a
                        // side-off.
                        if (recById.TryGetValue(childId, out var childRec)
                            && childRec != null
                            && currentRec.VesselPersistentId != 0
                            && childRec.VesselPersistentId != 0
                            && childRec.VesselPersistentId != currentRec.VesselPersistentId)
                        {
                            sideOffSkips++;
                            ParsekLog.Verbose("ReFlySession",
                                $"SessionSuppressedSubtree: skipped side-off child={childId} " +
                                $"childPid={childRec.VesselPersistentId} parent={currentRec.RecordingId} " +
                                $"parentPid={currentRec.VesselPersistentId} bp={bp.Id} type={bp.Type}");
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
                suppressionCacheRootOverride = rootOverride;

                ParsekLog.Verbose("ReFlySession",
                    $"SessionSuppressedSubtree: {result.Count} recording(s) closed from root={rootOverride} " +
                    $"(childrenAdded={childrenAdded} siblingsAdded={siblingsAdded} pidPeersAdded={pidPeersAdded} " +
                    $"debrisAdded={debrisAdded} debrisAnchorOnlySkips={debrisAnchorOnlySkips} " +
                    $"mixedParentHalts={mixedParentHalts} sideOffSkips={sideOffSkips})");

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

                // Bug fix-refly-abandon-and-fork-persist §Bug1 secondary
                // defense: a NotCommitted recording is by contract a
                // session-provisional that has not yet been committed; it
                // should never appear as a supersede source. Reaching here
                // means RewindInvoker.ReapPriorProvisionalsForRp or
                // LoadTimeSweep failed to remove an orphan from this tree's
                // Recordings dict. Skip the candidate with a Warn so the
                // closure does not enqueue it and AppendRelations does not
                // write an invalid row.
                if (cand.MergeState == MergeState.NotCommitted)
                {
                    ParsekLog.Warn("Supersede",
                        $"EnqueueChainSiblings: skipped NotCommitted peer " +
                        $"rec={cand.RecordingId} chain={cand.ChainId} " +
                        $"chainBranch={cand.ChainBranch.ToString(CultureInfo.InvariantCulture)} " +
                        $"tree={cand.TreeId} sess={cand.CreatingSessionId ?? "<none>"} " +
                        $"(should have been reaped by AtomicMarkerWrite's " +
                        $"ReapPriorProvisionalsForRp or by LoadTimeSweep — investigate)");
                    continue;
                }

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

        // Same-PID peer expansion across ChainId boundaries. EnqueueChainSiblings
        // only crosses the SplitAtSection boundary (HEAD↔TIP within one chain).
        // In-place re-fly that crosses staging additionally creates entirely
        // separate chains for the same physical vessel — those are the
        // recordings that belong in the suppressed closure but escape both the
        // ChainId walk and the BranchPoint walk because they have neither a
        // parent BP linking back to origin nor a shared ChainId. Match by
        // (TreeId, VesselPersistentId) and gate strictly on StartUT >
        // InvokedUT-epsilon so prior-flight history for the same physical
        // vessel — which legitimately predates the rewind point — is never
        // collapsed into the closure.
        //
        // Epsilon picked to absorb float rounding from `Recording.StartUT`
        // (which derives from sampler-stamped UTs) without admitting any real
        // pre-rewind sample. The recorder samples at ≥10 Hz; 0.05 s is half a
        // typical sample interval, so a recording whose first sample is at
        // marker.InvokedUT exactly is treated as part of the closure (it was
        // started by the re-fly resume), while one stamped 0.1+ s before is
        // treated as prior history.
        internal const double PidPeerStartUtEpsilonSeconds = 0.05;

        private static void EnqueuePidPeerSiblings(
            Recording rec,
            Dictionary<string, Recording> recById,
            Queue<string> queue,
            HashSet<string> result,
            ReFlySessionMarker marker,
            ref int pidPeersAdded)
        {
            if (rec == null) return;
            if (rec.VesselPersistentId == 0) return;
            if (string.IsNullOrEmpty(rec.TreeId)) return;
            if (marker == null) return;
            if (double.IsNaN(marker.InvokedUT) || double.IsInfinity(marker.InvokedUT)) return;

            double minStart = marker.InvokedUT - PidPeerStartUtEpsilonSeconds;
            foreach (var cand in recById.Values)
            {
                if (cand == null) continue;
                if (ReferenceEquals(cand, rec)) continue;
                if (string.IsNullOrEmpty(cand.RecordingId)) continue;
                if (cand.VesselPersistentId != rec.VesselPersistentId) continue;
                if (!string.Equals(cand.TreeId, rec.TreeId, StringComparison.Ordinal)) continue;
                if (result.Contains(cand.RecordingId)) continue;

                // Pre-rewind history guard: same physical vessel, same tree, but
                // started before the rewind point — that recording is the prior
                // life of the vessel and must not be superseded. Only post-rewind
                // peers belong in the closure.
                double candStart = cand.StartUT;
                if (double.IsNaN(candStart) || candStart < minStart) continue;

                // Bug fix-refly-abandon-and-fork-persist §Bug1 secondary
                // defense: identical to the EnqueueChainSiblings guard.
                // Reaching here with a NotCommitted candidate means the
                // marker-write-time reap failed and a prior abandoned
                // provisional's still in the store. Skip-with-Warn rather
                // than fail loud so a release build still ships a coherent
                // ERS, but the Warn surfaces the underlying leak.
                if (cand.MergeState == MergeState.NotCommitted)
                {
                    ParsekLog.Warn("Supersede",
                        $"EnqueuePidPeerSiblings: skipped NotCommitted peer " +
                        $"rec={cand.RecordingId} pid={cand.VesselPersistentId} " +
                        $"tree={cand.TreeId} sess={cand.CreatingSessionId ?? "<none>"} " +
                        $"(should have been reaped by AtomicMarkerWrite's " +
                        $"ReapPriorProvisionalsForRp or by LoadTimeSweep — investigate)");
                    continue;
                }

                result.Add(cand.RecordingId);
                pidPeersAdded++;
                queue.Enqueue(cand.RecordingId);
            }
        }

        // Debris-children expansion via the v12 `Recording.ParentAnchorRecordingId`
        // direct ownership link. Bypasses both the missing-back-pointer gap on
        // destroyed parents (multi-breakup parents cannot fit N BPs into the
        // single-slot `Recording.ChildBranchPointId`) and the same-PID side-off
        // filter (debris is always a fresh KSP-assigned VesselPersistentId by
        // construction). Legacy v11 debris with no `ParentAnchorRecordingId`
        // are not reachable through this edge — accepted under the project's
        // pre-1.0 no-backward-compat-for-old-recordings policy.
        //
        // TreeId is required to match. Debris is always recorded into its
        // parent's tree at breakup time, so a candidate whose TreeId differs
        // from the dequeued parent's would be a corrupted or hand-spliced
        // link and we refuse to cross tree boundaries silently — matching
        // the contract of EnqueueChainSiblings / EnqueuePidPeerSiblings.
        //
        // Topology gate: `ParentAnchorRecordingId` does double duty. The
        // focused-vessel breakup path (`ParsekFlight.CreateBreakupChildRecording`)
        // sets it to the actual breakup parent — a topology edge — while the
        // background-split path (`BackgroundRecorder.RegisterChildRecordingsFromSplit`,
        // anchored at lines 724-741) deliberately re-points it to the parent
        // continuation as a relative-sampling anchor so playback can resolve
        // post-split debris frames against an open parent recording. Walking
        // the anchor link as topology would pull non-PID side-off debris into
        // the closure when the parent continuation is re-flown, defeating the
        // same-PID side-off gate added in `fix-refly-suppress-side-off`.
        // Require the candidate's `ParentBranchPointId` to resolve to a
        // `Breakup` BP whose parents include the dequeued recording: the
        // breakup case satisfies it (`ParentBranchPointId == breakupBp.Id`,
        // `breakupBp.ParentRecordingIds[0] == parentRec.RecordingId`); the
        // background-split anchor case fails it because the split BP's parent
        // is the pre-split recording, not the continuation, and the BP type
        // is typically Decouple rather than Breakup.
        private static void EnqueueDebrisChildren(
            Recording rec,
            Dictionary<string, Recording> recById,
            Queue<string> queue,
            HashSet<string> result,
            ref int debrisAdded,
            ref int debrisAnchorOnlySkips)
        {
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) return;
            if (string.IsNullOrEmpty(rec.TreeId)) return;

            foreach (var cand in recById.Values)
            {
                if (cand == null) continue;
                // KEEP debris-only: this walker is the supersede-closure expansion
                // for breakup-debris children. Controlled-decoupled children
                // (extension of the parent-anchor contract) also carry
                // ParentAnchorRecordingId, but admitting them here would silently
                // change ERS / ELS closure shapes for re-fly. The BP-type-Breakup
                // gate at the bp.Type check below already fences out the BG-split
                // anchor double-duty case; the `!cand.IsDebris` skip is a stricter
                // filter that also excludes controlled-decoupled children. If a
                // future feature needs controlled children in closure expansion,
                // it is a separate design decision (which BP types qualify, how
                // to fence the anchor double-duty, etc.) - do not collapse this
                // gate as a side effect.
                if (!cand.IsDebris) continue;
                if (string.IsNullOrEmpty(cand.RecordingId)) continue;
                if (string.IsNullOrEmpty(cand.ParentAnchorRecordingId)) continue;
                if (!string.Equals(cand.ParentAnchorRecordingId, rec.RecordingId, StringComparison.Ordinal)) continue;
                if (!string.Equals(cand.TreeId, rec.TreeId, StringComparison.Ordinal)) continue;
                if (result.Contains(cand.RecordingId)) continue;

                if (string.IsNullOrEmpty(cand.ParentBranchPointId))
                {
                    debrisAnchorOnlySkips++;
                    continue;
                }
                BranchPoint bp = LookupBranchPoint(cand.TreeId, cand.ParentBranchPointId);
                // Focused-vessel split debris is admitted for both the Breakup
                // (crash / overstress) and JointBreak (intentional decouple) cases:
                // ParsekFlight.CreateBreakupChildRecording wires both kinds through the
                // same coalescer path, setting the debris' ParentAnchorRecordingId and
                // ParentBranchPointId to the breakup parent + BP. The two now differ only
                // in BP type (a foreground decouple records JointBreak/DECOUPLE), so the
                // type gate must accept both or multi-staging debris would silently drop
                // out of the supersede closure. The background-split anchor case is still
                // fenced out by the topology check below (its BP's parent is the pre-split
                // recording, not the continuation the debris is anchored to), so the
                // anchor double-duty noted on this method's contract stays excluded.
                if (bp == null
                    || (bp.Type != BranchPointType.Breakup && bp.Type != BranchPointType.JointBreak))
                {
                    debrisAnchorOnlySkips++;
                    continue;
                }
                if (bp.ParentRecordingIds == null
                    || !bp.ParentRecordingIds.Contains(rec.RecordingId))
                {
                    debrisAnchorOnlySkips++;
                    continue;
                }

                result.Add(cand.RecordingId);
                debrisAdded++;
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
