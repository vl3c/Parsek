using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Phase 8 of Rewind-to-Staging (design §5.3 / §5.5 / §6.6 step 2-3 /
    /// §7.17 / §7.43 / §10.4): commits a re-fly session's supersede relations
    /// for the subtree rooted at the marker's supersede target, falling back
    /// to <c>OriginChildRecordingId</c> for legacy markers,
    /// and flips the provisional's <see cref="MergeState"/> according to
    /// the Unfinished Flights safety policy: safe retry slots stay
    /// <see cref="MergeState.CommittedProvisional"/>, while closed or
    /// career/world-changing outcomes become <see cref="MergeState.Immutable"/>.
    ///
    /// <para>
    /// This commit step is what "hides the origin subtree from ERS" — it
    /// appends one <see cref="RecordingSupersedeRelation"/> per recording in
    /// the closure, all pointing at the provisional's id. The closure is the
    /// same forward-only, merge-guarded walk used by the physical-visibility
    /// layer (<see cref="EffectiveState.ComputeSessionSuppressedSubtree"/>);
    /// after commit, the ERS cache invalidates on the
    /// <see cref="ParsekScenario.BumpSupersedeStateVersion"/> bump and the
    /// subtree flips from "session-suppressed" to "superseded" — same
    /// invisibility, different mechanism.
    /// </para>
    ///
    /// <para>
    /// Phase 8 does NOT write a <see cref="MergeJournal"/>; the journaled
    /// staged commit lands in Phase 10. If this call crashes mid-way, Phase
    /// 13's load-time sweep will clean up the dangling marker + partial
    /// supersede relations.
    /// </para>
    ///
    /// <para>
    /// <see cref="AppendRelations"/> applies a generalized pre-rewind
    /// carve-out covering BOTH pre-rewind debris AND post-split pre-rewind
    /// chain heads (see <see cref="IsPreRewindCarveOut"/>).
    /// </para>
    ///
    /// <para>
    /// As of Task A4 (Split-at-rewind-UT), the merge orchestrator now invokes
    /// <see cref="RecordingTreeSplitter.SplitOriginAtRewindUT"/> BEFORE
    /// <see cref="AppendRelations"/> to split a Re-Fly origin that spans the
    /// rewind UT into HEAD (kept visible) + TIP (superseded). After the split
    /// the marker's <see cref="ReFlySessionMarker.SupersedeTargetId"/> points
    /// at TIP, so the closure walk starts at TIP and naturally enqueues HEAD
    /// as a chain sibling. The carve-out predicate
    /// <see cref="IsPreRewindCarveOut"/> then filters HEAD out of the
    /// supersede write-set (reason
    /// <see cref="PreRewindCarveOutReason.PreRewindChainHead"/>) so HEAD's
    /// row remains in ERS / timeline / Watch / KSC. Debris carve-out
    /// (PR #858 / reason <see cref="PreRewindCarveOutReason.PreRewindDebris"/>)
    /// is preserved unchanged.
    /// </para>
    /// </summary>
    internal static class SupersedeCommit
    {
        /// <summary>
        /// Reason a recording was excluded from <see cref="AppendRelations"/>'s
        /// write-set by the pre-rewind carve-out filter. Set as the
        /// <c>out</c> parameter of <see cref="IsPreRewindCarveOut"/>.
        /// </summary>
        internal enum PreRewindCarveOutReason
        {
            /// <summary>The recording does not match the carve-out predicate.</summary>
            None = 0,
            /// <summary>
            /// Debris recording that physically separated strictly before the
            /// rewind point (the original PR #858 carve-out).
            /// </summary>
            PreRewindDebris = 1,
            /// <summary>
            /// Non-debris recording whose <c>EndUT</c> lies at or just past
            /// the rewind UT — the HEAD half of a post-split origin produced
            /// by <see cref="RecordingTreeSplitter.SplitOriginAtRewindUT"/>.
            /// </summary>
            PreRewindChainHead = 2,
        }

        private const string Tag = "Supersede";
        private const string SessionTag = "ReFlySession";
        private const string LedgerSwapTag = "LedgerSwap";
        private const char WorldActionCacheKeySeparator = '\u001f';

        private static readonly object worldActionSafetyCacheLock = new object();
        private static readonly Dictionary<string, WorldActionSafetyCacheEntry> worldActionSafetyCache
            = new Dictionary<string, WorldActionSafetyCacheEntry>(StringComparer.Ordinal);
        private static object worldActionSafetyCacheScenarioIdentity;
        private static int worldActionSafetyCacheLedgerVersion = int.MinValue;
        private static int worldActionSafetyCacheStoreVersion = int.MinValue;
        private static int worldActionSafetyCacheSupersedeVersion = int.MinValue;

        /// <summary>
        /// Idempotent: appends supersede relations for every id in the
        /// forward-only merge-guarded subtree closure of
        /// <paramref name="marker"/>, flips
        /// <paramref name="provisional"/>.<see cref="Recording.MergeState"/>,
        /// clears <see cref="Recording.SupersedeTargetId"/>, and clears
        /// <see cref="ParsekScenario.ActiveReFlySessionMarker"/>.
        ///
        /// <para>
        /// Retained as the synchronous legacy entry point for direct callers
        /// (Phase 8 tests, the <c>SessionMerger</c> advisory path, and the
        /// <see cref="InGameTests.MergeCrashedReFlyCreatesCPSupersedeTest"/> /
        /// <see cref="InGameTests.MergeLandedReFlyCreatesImmutableSupersedeTest"/>
        /// live-scene tests). Phase 10's <see cref="MergeJournalOrchestrator.RunMerge"/>
        /// invokes the decomposed helpers directly so it can insert journal
        /// phase markers between them.
        /// </para>
        /// </summary>
        internal static void CommitSupersede(ReFlySessionMarker marker, Recording provisional)
        {
            if (marker == null)
            {
                ParsekLog.Warn(Tag, "CommitSupersede: marker is null — nothing to commit");
                return;
            }
            if (provisional == null)
            {
                ParsekLog.Warn(Tag, "CommitSupersede: provisional is null — nothing to commit");
                return;
            }

            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario))
            {
                ParsekLog.Warn(Tag, "CommitSupersede: no ParsekScenario instance — nothing to commit");
                return;
            }

            PreflightMergeStateClassification(marker, provisional, scenario);

            if (scenario.RecordingSupersedes == null)
                scenario.RecordingSupersedes = new List<RecordingSupersedeRelation>();
            if (scenario.LedgerTombstones == null)
                scenario.LedgerTombstones = new List<LedgerTombstone>();

            IReadOnlyCollection<string> subtree = AppendRelations(marker, provisional, scenario);

            string newRecordingId = provisional.RecordingId;
            double ut = SafeNow();
            string nowIso = DateTime.UtcNow.ToString("o");

            // Tombstones run AFTER the supersede relations land (so the
            // relations describe "what's superseded" before the ELS recomputes)
            // and BEFORE the MergeState flip's version bump so a single ELS
            // rebuild covers both changes. The subtree is rooted at
            // SupersedeTargetId when present; earlier origin -> prior-tip
            // actions were already handled by the previous merge.
            CommitTombstones(marker, subtree, newRecordingId, ut, nowIso, scenario);

            FlipMergeStateAndClearTransient(marker, provisional, scenario, preserveMarker: false);
        }

        /// <summary>
        /// Phase 10 decomposed helper (design §6.6 step 3): compute the
        /// forward-only merge-guarded subtree closure rooted at the marker's
        /// supersede target (or origin child recording for legacy markers) and append one
        /// <see cref="RecordingSupersedeRelation"/> per descendant pointing at
        /// the provisional. Idempotent: pre-existing relations are skipped
        /// with a Verbose log. Returns the closure so the downstream
        /// tombstone scan reuses the same walk.
        /// </summary>
        internal static IReadOnlyCollection<string> AppendRelations(
            ReFlySessionMarker marker, Recording provisional, ParsekScenario scenario)
            => AppendRelations(marker, provisional, scenario, extraSelfSkipRecordingIds: null);

        /// <summary>
        /// Optimizer-split-aware overload. <paramref name="extraSelfSkipRecordingIds"/>
        /// names additional recording ids whose closure entry must be filtered
        /// out as "self-links" — used by the in-place-continuation branch of
        /// <see cref="MergeDialog.TryCommitReFlySupersede"/> when
        /// <see cref="RecordingStore.RunOptimizationPass"/> has split the
        /// in-place provisional into a chain HEAD + TIP. The caller passes the
        /// HEAD id here while passing the TIP as the <paramref name="provisional"/>
        /// parameter so <see cref="ValidateSupersedeTarget"/> sees the tip's
        /// non-null terminal state, but neither the head nor the tip ends up
        /// with a row pointing at the other. Both segments are part of the
        /// same in-place flight; superseding either would collapse the chain
        /// in ERS.
        /// </summary>
        internal static IReadOnlyCollection<string> AppendRelations(
            ReFlySessionMarker marker, Recording provisional, ParsekScenario scenario,
            IReadOnlyCollection<string> extraSelfSkipRecordingIds)
        {
            // Use ReferenceEquals to skip Unity's Object == null override,
            // which would treat a destroyed ScenarioModule as null even when
            // the test fixture installed a plain-CLR instance without a
            // Unity lifecycle. The outer CommitSupersede / RunMerge call sites
            // have already validated scenario before dispatching here.
            if (marker == null || provisional == null
                || object.ReferenceEquals(null, scenario))
                return new List<string>();

            string closureRoot = marker.SupersedeTargetId ?? marker.OriginChildRecordingId;
            IReadOnlyCollection<string> subtree =
                EffectiveState.ComputeSubtreeClosureInternal(marker, closureRoot);
            int subtreeCount = subtree?.Count ?? 0;
            string newRecordingId = provisional.RecordingId;
            string originId = marker.OriginChildRecordingId;

            double ut = SafeNow();
            string nowIso = DateTime.UtcNow.ToString("o");
            var ic = CultureInfo.InvariantCulture;

            // Invariant: a supersede target is by design a recording with at
            // least one trajectory point AND a non-null terminal state. A
            // placeholder (zero points) cannot validly replace a destroyed
            // origin -- the placeholder-redirect bug class shipped twice in
            // 2026-04 (items 5, 568). Catch it at commit time so the next
            // variant fails loud instead of silently poisoning ERS with a
            // zero-trajectory replacement.
            //
            // Optimizer-split chain-tip note: when the in-place continuation
            // path resolves the chain tip pre-call (see
            // MergeDialog.TryCommitReFlySupersede's chain-tip-resolve block),
            // the recording passed here is the TIP and validation against
            // its terminal payload passes naturally. The HEAD's
            // null-terminal post-split is filtered out via
            // extraSelfSkipRecordingIds so it never reaches the row-write
            // step.
            string invariantReason;
            if (!ValidateSupersedeTarget(provisional, out invariantReason))
            {
                ParsekLog.Warn(Tag,
                    $"AppendRelations invariant violation: provisional={provisional?.RecordingId ?? "<null>"} " +
                    $"reason={invariantReason} -- refusing to write supersede rows in this batch");
#if DEBUG
                throw new InvalidOperationException(
                    $"AppendRelations invariant violation: provisional={provisional?.RecordingId ?? "<null>"} reason={invariantReason}");
#else
                return new List<string>();
#endif
            }

            HashSet<string> extraSkip = null;
            if (extraSelfSkipRecordingIds != null && extraSelfSkipRecordingIds.Count > 0)
            {
                extraSkip = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in extraSelfSkipRecordingIds)
                {
                    if (!string.IsNullOrEmpty(id))
                        extraSkip.Add(id);
                }
            }

            // Pre-rewind carve-out filter (see IsPreRewindCarveOut). Two
            // cases share this filter:
            //   1. Pre-rewind debris (PR #858): debris recordings that
            //      physically separated before the rewind point are
            //      independent vessel histories that the re-fly does not
            //      redo.
            //   2. Pre-rewind chain head (Task A4, dormant until the
            //      split orchestrator lands): the HEAD half of an origin
            //      recording that was split at the rewind UT. HEAD covers
            //      the launch portion (kept visible), TIP covers the
            //      post-rewind portion (becomes the new supersede target).
            // Both are excluded from the supersede write-set AND from the
            // returned subtree so the downstream tombstone scan
            // (CommitSupersede / MergeJournalOrchestrator route this return
            // value into CommitTombstones) leaves their attributed ledger
            // actions alone too. The session-suppressed closure walk itself
            // is intentionally unchanged: closure inclusion still drives
            // PR #858's render carve-out and PR #860's watch-mode /
            // map-presence scoping while the active session is live; the
            // write-set filter is the commit-time policy decision.
            var preRewindCarveOutIds = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, Recording> recById = null;
            // Pass 4 follow-up micro-optimization: pre-resolve TIP once so
            // IsPreRewindCarveOut's chain-head case skips its per-call O(N)
            // store scan. TIP id is constant across every iteration (it's
            // marker.SupersedeTargetId), so a one-shot lookup at the top
            // amortizes correctly for any subtree size.
            Recording tipForCarveOut = !string.IsNullOrEmpty(marker.SupersedeTargetId)
                ? FindCommittedRecordingByIdForCarveOut(marker.SupersedeTargetId)
                : null;

            int added = 0;
            int skippedExisting = 0;
            int skippedSelfLink = 0;
            int skippedExtraSelfLink = 0;
            int skippedPreRewindCarveOut = 0;
            if (subtree != null)
            {
                foreach (string oldId in subtree)
                {
                    if (string.IsNullOrEmpty(oldId)) continue;
                    // In-place continuation guard: when the marker's
                    // OriginChildRecordingId == ActiveReFlyRecordingId, the
                    // subtree closure includes the origin itself, which is
                    // also the provisional. A row where old==new would form
                    // a 1-node cycle that poisons EffectiveRecordingId
                    // (`cycle detected` WARN every lookup) and pin the
                    // recording invisible to ERS. Skip the trivial self-link
                    // so callers (in particular the in-place-continuation
                    // branch of MergeDialog.TryCommitReFlySupersede) can
                    // safely invoke AppendRelations to write rows for the
                    // sibling/parent recordings without producing the cycle.
                    if (string.Equals(oldId, newRecordingId, StringComparison.Ordinal))
                    {
                        skippedSelfLink++;
                        ParsekLog.Verbose(Tag,
                            $"AppendRelations: skip self-link old={oldId} new={newRecordingId} " +
                            $"(in-place continuation; origin == provisional)");
                        continue;
                    }
                    // Optimizer-split chain-head guard: when the in-place
                    // provisional was split into HEAD + TIP, the caller
                    // passes the TIP as `provisional` so validation passes,
                    // and adds the HEAD's id here. Filter the closure entry
                    // for HEAD so HEAD does not end up with a row pointing
                    // at TIP — both halves of the in-place chain are part
                    // of the new flight; superseding either silently
                    // collapses ERS via EffectiveRecordingId redirect.
                    if (extraSkip != null && extraSkip.Contains(oldId))
                    {
                        skippedExtraSelfLink++;
                        ParsekLog.Verbose(Tag,
                            $"AppendRelations: skip extra-self-link old={oldId} new={newRecordingId} " +
                            $"(in-place continuation chain-head; tip is the supersede target)");
                        continue;
                    }
                    if (RelationExists(scenario.RecordingSupersedes, oldId, newRecordingId))
                    {
                        skippedExisting++;
                        ParsekLog.Verbose(Tag,
                            $"CommitSupersede: skip existing relation old={oldId} new={newRecordingId}");
                        continue;
                    }
                    // Pre-rewind carve-out is filtered LAST so the existing
                    // skip-counter contracts (self-link / extra-self-link /
                    // existing-relation) keep their meaning. Build the
                    // id->Recording index lazily; most batches are tiny and
                    // the closure already filtered out null entries.
                    //
                    // A `TryGetValue` miss (closure id not in
                    // `CommittedRecordings` — defensive, the closure walk
                    // builds itself from the same store) short-circuits the
                    // `&&` and falls through to row-write. That preserves the
                    // pre-fix default ("write the row") for the anomalous
                    // case where the row's Recording is gone but its id is
                    // still in the closure.
                    if (recById == null)
                        recById = BuildCommittedRecordingIndex();
                    Recording rec;
                    PreRewindCarveOutReason carveOutReason;
                    if (recById.TryGetValue(oldId, out rec)
                        && IsPreRewindCarveOut(rec, marker, tipForCarveOut, out carveOutReason))
                    {
                        skippedPreRewindCarveOut++;
                        preRewindCarveOutIds.Add(oldId);
                        ParsekLog.Verbose(Tag,
                            $"AppendRelations: skip pre-rewind {carveOutReason} old={oldId} new={newRecordingId} " +
                            $"(startUT={rec.StartUT.ToString("R", ic)} endUT={rec.EndUT.ToString("R", ic)} " +
                            $"rewindUT={marker.RewindPointUT.ToString("R", ic)} " +
                            $"debrisParent={rec.DebrisParentRecordingId ?? "<null>"})");
                        continue;
                    }
                    var rel = new RecordingSupersedeRelation
                    {
                        RelationId = "rsr_" + Guid.NewGuid().ToString("N"),
                        OldRecordingId = oldId,
                        NewRecordingId = newRecordingId,
                        UT = ut,
                        CreatedRealTime = nowIso,
                    };
                    scenario.RecordingSupersedes.Add(rel);
                    added++;
                    ParsekLog.Info(Tag,
                        $"rel={rel.RelationId} old={rel.OldRecordingId} new={rel.NewRecordingId}");
                }
            }

            ParsekLog.Info(Tag,
                $"Added {added.ToString(ic)} supersede relations for subtree rooted at {closureRoot ?? "<none>"} " +
                $"(origin={originId ?? "<none>"} subtreeCount={subtreeCount.ToString(ic)} " +
                $"skippedExisting={skippedExisting.ToString(ic)} " +
                $"skippedSelfLink={skippedSelfLink.ToString(ic)} " +
                $"skippedExtraSelfLink={skippedExtraSelfLink.ToString(ic)} " +
                $"skippedPreRewindCarveOut={skippedPreRewindCarveOut.ToString(ic)})");

            // Filter pre-rewind carve-out ids out of the returned subtree so
            // the downstream tombstone scope (CommitTombstones) leaves ledger
            // actions attributed to those recordings alone. The closure
            // itself remains unfiltered.
            if (preRewindCarveOutIds.Count == 0)
                return subtree ?? new List<string>();
            var filtered = new List<string>(subtree.Count - preRewindCarveOutIds.Count);
            foreach (var id in subtree)
                if (!preRewindCarveOutIds.Contains(id))
                    filtered.Add(id);
            return filtered;
        }

        /// <summary>
        /// Generalized pre-rewind carve-out predicate used by
        /// <see cref="AppendRelations"/>. Returns true when
        /// <paramref name="rec"/> must be excluded from the supersede
        /// write-set (and from the subtree fed to
        /// <c>CommitTombstones</c>). Two cases share this filter:
        ///
        /// <list type="bullet">
        /// <item><description>
        /// <b>Pre-rewind debris</b> (PR #858): a debris recording whose
        /// <see cref="Recording.StartUT"/> lies strictly before
        /// <see cref="ComputePreRewindCutoff"/> = <c>rewindUT - epsilon</c>.
        /// Such debris physically separated before the rewind point and
        /// represents an independent vessel history the re-fly does not
        /// redo. Requires a non-null
        /// <see cref="Recording.DebrisParentRecordingId"/>: legacy v11
        /// debris loaded without the v12 ownership link follows the
        /// unfiltered legacy path.
        /// </description></item>
        /// <item><description>
        /// <b>Pre-rewind chain head</b> (Task A4, dormant until the split
        /// orchestrator lands): a non-debris recording whose
        /// <see cref="Recording.EndUT"/> falls at or just past
        /// <c>marker.RewindPointUT + epsilon</c>. After
        /// <c>SplitOriginAtRewindUT</c> splits an origin recording at the
        /// rewind UT, the HEAD half has <c>EndUT == rewindUT</c> exactly;
        /// this case carves HEAD out of the closure so only the TIP half
        /// (and the subtree below it) receives supersede rows. The two
        /// predicates are <b>not</b> symmetric: debris tests the lower
        /// boundary (<c>StartUT &lt; rewindUT - epsilon</c>) while
        /// chain-head tests the upper boundary
        /// (<c>EndUT &lt;= rewindUT + epsilon</c>). Using
        /// <see cref="ComputePreRewindCutoff"/> for the chain-head test
        /// would fail HEAD's <c>EndUT == rewindUT</c> by exactly one
        /// epsilon and reintroduce the very bug Task A1-A8 exists to fix;
        /// the asymmetric inline cutoff is deliberate.
        /// </description></item>
        /// </list>
        ///
        /// <para>The debris case is checked first. A debris recording that
        /// also happens to satisfy the chain-head predicate is therefore
        /// reported with
        /// <see cref="PreRewindCarveOutReason.PreRewindDebris"/>.</para>
        ///
        /// <para>Closure semantics (PR #858 render carve-out and PR #860
        /// watch-mode / map-presence scoping) are unaffected — carved-out
        /// recordings stay in
        /// <see cref="EffectiveState.ComputeSessionSuppressedSubtree"/>
        /// during the active re-fly session, and only the supersede
        /// write-set is filtered at commit time.</para>
        /// </summary>
        internal static bool IsPreRewindCarveOut(
            Recording rec, ReFlySessionMarker marker,
            out PreRewindCarveOutReason reason)
        {
            return IsPreRewindCarveOut(rec, marker, preResolvedTip: null, out reason);
        }

        /// <summary>
        /// Hot-loop overload of <see cref="IsPreRewindCarveOut(Recording, ReFlySessionMarker, out PreRewindCarveOutReason)"/>
        /// taking a pre-resolved TIP recording so the chain-head case skips its
        /// per-call O(N) <see cref="RecordingStore.CommittedRecordings"/>
        /// linear scan. The TIP id is read from
        /// <see cref="ReFlySessionMarker.SupersedeTargetId"/> and is the
        /// same value across every iteration of <see cref="AppendRelations"/>'s
        /// subtree loop and
        /// <see cref="MergeJournalOrchestrator"/>'s <c>RebuildSubtree</c> loop,
        /// so callers hoist the resolution once. Pass <c>null</c> to fall back
        /// to the internal lookup (single-shot callers, tests, the
        /// <see cref="IsPreRewindDebris"/> wrapper).
        /// </summary>
        internal static bool IsPreRewindCarveOut(
            Recording rec, ReFlySessionMarker marker,
            Recording preResolvedTip,
            out PreRewindCarveOutReason reason)
        {
            reason = PreRewindCarveOutReason.None;
            if (rec == null || marker == null) return false;

            // Debris case (PR #858). Uses ComputePreRewindCutoff
            // = rewindUT - epsilon (StartUT < cutoff means "started
            // strictly before the rewind"; the epsilon absorbs
            // sampler jitter forward of the rewind point).
            double debrisCutoff = ComputePreRewindCutoff(marker);
            if (!double.IsNaN(debrisCutoff)
                && rec.IsDebris
                && !string.IsNullOrEmpty(rec.DebrisParentRecordingId)
                && rec.StartUT < debrisCutoff)
            {
                reason = PreRewindCarveOutReason.PreRewindDebris;
                return true;
            }

            // Chain-head case (Task A4, retargeted to chain-shape match in
            // Pass 4 after the id-match form regressed nested Re-Fly).
            //
            // Required for the carve-out to fire:
            //   1. Non-debris (the debris case above handles the lower
            //      boundary).
            //   2. rec.EndUT <= rewindUT + epsilon (upper-boundary cutoff —
            //      HEAD's EndUT is exactly rewindUT after the split).
            //   3. marker.SupersedeTargetId is set (after the split, this
            //      names TIP). NaN/empty disables this case.
            //   4. rec shares TIP's ChainId + ChainBranch.
            //   5. rec.ChainIndex < TIP.ChainIndex.
            //
            // Why chain-shape and NOT marker.OriginChildRecordingId:
            // OriginChildRecordingId is the SLOT'S stable origin id, set when
            // the slot was first created. For the first Re-Fly the slot's
            // origin and the split's HEAD happen to share the id (origin
            // becomes HEAD in place). For the SECOND Re-Fly on the same
            // slot, the marker still carries the slot's original
            // OriginChildRecordingId (= HEAD₁.id), but the split this pass
            // operates on starts at fork₁ (= HEAD₂). HEAD₂.RecordingId is
            // fork₁.id, not HEAD₁.id, so the id-match form failed to fire
            // and HEAD₂ silently got a supersede row → fork₁'s launch
            // portion vanished from the timeline. That's the bug class this
            // PR was created to fix — re-introduced by the over-tightening
            // attempt before Pass 4 corrected the predicate.
            //
            // Chain-shape derives correctly from split-time invariants: every
            // split the splitter produces creates HEAD/TIP as chain siblings
            // sharing a ChainId, with HEAD at the lower index. Pre-existing
            // chain members at lower ChainIndex (e.g. env-class siblings
            // from a prior optimizer split) also satisfy the predicate —
            // they're correctly carved out as part of the same pre-rewind
            // history.
            //
            // Unrelated closure entries (EVA recordings, fairing jettisons,
            // PID peers, BP-children of unrelated recordings) live on
            // different ChainIds and are correctly NOT carved out.
            //
            // Epsilon sign is OPPOSITE the debris case (lower boundary):
            // debris tests StartUT < rewindUT - epsilon; chain-head tests
            // EndUT <= rewindUT + epsilon. Do not unify via
            // ComputePreRewindCutoff — re-introducing the symmetry would
            // fail HEAD's EndUT == rewindUT by exactly one epsilon.
            double headCutoff =
                !double.IsNaN(marker.RewindPointUT) && marker.RewindPointUT > 0.0
                    ? marker.RewindPointUT + EffectiveState.PidPeerStartUtEpsilonSeconds
                    : double.NaN;
            if (!double.IsNaN(headCutoff)
                && !rec.IsDebris
                && !string.IsNullOrEmpty(marker.SupersedeTargetId)
                && !string.IsNullOrEmpty(rec.ChainId)
                && rec.EndUT <= headCutoff)
            {
                Recording tip = preResolvedTip
                    ?? FindCommittedRecordingByIdForCarveOut(marker.SupersedeTargetId);
                if (tip != null
                    && !string.IsNullOrEmpty(tip.ChainId)
                    && string.Equals(rec.ChainId, tip.ChainId, StringComparison.Ordinal)
                    && rec.ChainBranch == tip.ChainBranch
                    && rec.ChainIndex < tip.ChainIndex)
                {
                    reason = PreRewindCarveOutReason.PreRewindChainHead;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Linear-scan lookup of a recording by id from
        /// <see cref="RecordingStore.CommittedRecordings"/>, used by
        /// <see cref="IsPreRewindCarveOut"/>'s chain-head case to resolve TIP
        /// from <see cref="ReFlySessionMarker.SupersedeTargetId"/>. Kept
        /// private to this file so the lookup is a single small helper rather
        /// than another index-builder API. AppendRelations already builds its
        /// own id index for the closure-walk loop; for the typical subtree
        /// size (~10 entries) the O(N) lookup per IsPreRewindCarveOut call
        /// adds negligible cost. Returns null when not found or when the
        /// store is empty.
        /// </summary>
        private static Recording FindCommittedRecordingByIdForCarveOut(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            var src = RecordingStore.CommittedRecordings;
            if (src == null) return null;
            for (int i = 0; i < src.Count; i++)
            {
                var rec = src[i];
                if (rec == null) continue;
                if (string.Equals(rec.RecordingId, recordingId, StringComparison.Ordinal))
                    return rec;
            }
            return null;
        }

        /// <summary>
        /// Backward-compatibility wrapper around
        /// <see cref="IsPreRewindCarveOut"/>. Returns true only for the
        /// debris case (matching the pre-Task-A3 contract). The
        /// load-bearing logic lives in
        /// <see cref="IsPreRewindCarveOut"/>; this thin wrapper preserves
        /// the symbol's binary contract for the in-game tests at
        /// <c>InGameTests/MergeCrashedReFlyCreatesCPSupersedeTest.cs</c>
        /// and <c>InGameTests/MergeLandedReFlyCreatesImmutableSupersedeTest.cs</c>.
        /// </summary>
        internal static bool IsPreRewindDebris(Recording rec, ReFlySessionMarker marker)
        {
            return IsPreRewindCarveOut(rec, marker, out var reason)
                && reason == PreRewindCarveOutReason.PreRewindDebris;
        }

        /// <summary>
        /// Computes the cutoff UT below which a debris recording is treated
        /// as pre-rewind by the debris case of <see cref="IsPreRewindCarveOut"/>
        /// (and by its <see cref="IsPreRewindDebris"/> back-compat wrapper).
        /// Prefers <see cref="ReFlySessionMarker.RewindPointUT"/> (added in
        /// PR #858 as the stable, drift-immune <c>rp.UT</c> capture — see
        /// <c>RewindInvoker.AtomicMarkerWrite</c>) and falls back to
        /// <see cref="ReFlySessionMarker.InvokedUT"/> for legacy markers
        /// persisted before the field shipped. Both branches subtract
        /// <see cref="EffectiveState.PidPeerStartUtEpsilonSeconds"/> to
        /// absorb sampler-stamped float rounding on
        /// <see cref="Recording.StartUT"/> — same epsilon and rationale as
        /// <see cref="EffectiveState.EnqueuePidPeerSiblings"/>. Returns
        /// <c>double.NaN</c> only when neither field carries a usable
        /// (non-NaN, strictly positive) value; callers must treat NaN as
        /// "no cutoff available" and fall back to legacy behavior.
        ///
        /// <para>Note the asymmetric epsilon sign between cases of
        /// <see cref="IsPreRewindCarveOut"/>: the debris case calls
        /// <c>ComputePreRewindCutoff</c> for a LOWER-boundary cutoff
        /// (<c>rewindUT - epsilon</c>); the chain-head case uses its own
        /// inline UPPER-boundary cutoff (<c>rewindUT + epsilon</c>). Do
        /// not "unify" the two via this helper — re-introducing the
        /// epsilon sign symmetry would fail HEAD's <c>EndUT == rewindUT</c>
        /// by exactly one epsilon and reopen the bug Task A1-A8 exists to
        /// fix.</para>
        /// </summary>
        internal static double ComputePreRewindCutoff(ReFlySessionMarker marker)
        {
            if (marker == null) return double.NaN;
            if (!double.IsNaN(marker.RewindPointUT) && marker.RewindPointUT > 0.0)
                return marker.RewindPointUT - EffectiveState.PidPeerStartUtEpsilonSeconds;
            if (!double.IsNaN(marker.InvokedUT) && marker.InvokedUT > 0.0)
                return marker.InvokedUT - EffectiveState.PidPeerStartUtEpsilonSeconds;
            return double.NaN;
        }

        private static Dictionary<string, Recording> BuildCommittedRecordingIndex()
        {
            var index = new Dictionary<string, Recording>(StringComparer.Ordinal);
            var recordings = RecordingStore.CommittedRecordings;
            if (recordings == null) return index;
            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId)) continue;
                // Tolerate duplicate ids (defensive: stale store entries).
                // First-wins here vs. EffectiveState.ComputeSubtreeClosureInternal's
                // last-wins (`recById[id] = r`) is a cosmetic divergence — duplicate
                // ids in CommittedRecordings are pathological and either policy
                // resolves to "some entry"; we just need a stable Recording reference
                // to feed IsPreRewindDebris.
                if (!index.ContainsKey(rec.RecordingId))
                    index.Add(rec.RecordingId, rec);
            }
            return index;
        }

        /// <summary>
        /// Phase 10 decomposed helper (design §6.6 steps 2, 5, 6, 11): flip
        /// <paramref name="provisional"/>.<see cref="Recording.MergeState"/>
        /// to <see cref="MergeState.Immutable"/> or
        /// <see cref="MergeState.CommittedProvisional"/> based on terminal
        /// kind, clear the transient <see cref="Recording.SupersedeTargetId"/>,
        /// bump supersede state version so the ERS cache invalidates, and
        /// (unless <paramref name="preserveMarker"/> is set) clear
        /// <see cref="ParsekScenario.ActiveReFlySessionMarker"/>.
        ///
        /// <para>
        /// <paramref name="preserveMarker"/> is set by
        /// <see cref="MergeJournalOrchestrator.RunMerge"/> so the marker
        /// clear is deferred until AFTER Durable Save #1 (design §6.6
        /// step 11). Legacy <see cref="CommitSupersede"/> keeps the original
        /// synchronous behavior with <paramref name="preserveMarker"/> =
        /// <c>false</c>.
        /// </para>
        /// </summary>
        internal static void FlipMergeStateAndClearTransient(
            ReFlySessionMarker marker, Recording provisional, ParsekScenario scenario,
            bool preserveMarker)
        {
            if (marker == null || provisional == null
                || object.ReferenceEquals(null, scenario)) return;

            MergeStateClassification classification =
                ClassifyMergeStateOrThrow(
                    marker, provisional, scenario, logFallback: true,
                    isPrediction: false);

            provisional.MergeState = classification.NewState;
            // Re-Fly provisionals are created with PlaybackEnabled=false in
            // RewindInvoker.BuildProvisionalRecording so the in-flight session
            // does not also play the attempt back as a ghost. After merge, the
            // attempt is committed timeline data and must replay normally —
            // restore the default-on flag so the recording is visible during
            // any future Re-Fly of a sibling slot. Without this flip the
            // first Re-Fly's recording sits at PlaybackEnabled=false forever
            // (ghost playback skip reason=playback-disabled), and the second
            // Re-Fly of an adjacent vessel does not see it as a ghost.
            bool restoredPlayback = false;
            if (!provisional.PlaybackEnabled)
            {
                provisional.PlaybackEnabled = true;
                restoredPlayback = true;
            }
            ApplyAutoSealAfterSafetyClose(classification, provisional, scenario);

            string priorTarget = provisional.SupersedeTargetId;
            provisional.SupersedeTargetId = null;

            scenario.BumpSupersedeStateVersion();
            if (restoredPlayback)
            {
                ParsekLog.Verbose(Tag,
                    $"FlipMergeStateAndClearTransient: restored PlaybackEnabled=true on " +
                    $"provisional={provisional.RecordingId ?? "<no-id>"} (was suppressed during recording)");
            }

            ParsekLog.Info(Tag,
                $"provisional={provisional.RecordingId ?? "<no-id>"} mergeState={classification.NewState} terminalKind={classification.Kind} " +
                $"qualifies={classification.ClassifierQualifies} " +
                $"slot={(classification.ClassifierResolvedSlot ? classification.SlotListIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) : "<none>")} " +
                $"rp={(classification.ClassifierResolvedSlot ? classification.RewindPointId ?? "<no-rp>" : "<none>")} " +
                $"focusSlot={(classification.ClassifierResolvedSlot ? classification.FocusSlotIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) : "<none>")} " +
                $"focusSlotOverride={(classification.ClassifierResolvedSlot ? classification.SlotListIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) : "<none>")} " +
                $"classifierReason={classification.ClassifierReason ?? "<none>"} " +
                $"autoSeal={classification.AutoSealSlot} " +
                $"autoSealReason={classification.AutoSealReason ?? "<none>"} " +
                $"priorTarget={priorTarget ?? "<none>"}");

            if (preserveMarker) return;

            string sessionId = marker.SessionId;
            scenario.ActiveReFlySessionMarker = null;
            Parsek.Rendering.RenderSessionState.Clear("marker-cleared");
            scenario.BumpSupersedeStateVersion();
            // Drop any forced CanRevertToPostInit override now that the
            // session is committed. Normally this commit happens at scene
            // exit (KSC), so it's a no-op there; defensive against any future
            // call site that commits while still in flight.
            ReFlyRevertButtonGate.Apply("SupersedeCommit:marker-cleared");

            // Drop any captured pre-Re-Fly anchor snapshots now that the
            // session is committed. The snapshot was only needed to feed
            // resolver paths while the fork was being
            // populated; post-merge the fork's own data is final.
            ClearPreReFlyAnchorSnapshotsForSession(sessionId);

            ParsekLog.Info(SessionTag,
                $"End reason=merged sess={sessionId ?? "<no-id>"} provisional={provisional.RecordingId ?? "<no-id>"}");
        }

        internal static int ClearPreReFlyAnchorSnapshotsForSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return 0;
            int cleared = 0;
            var recordings = RecordingStore.CommittedRecordings;
            if (recordings != null)
            {
                for (int i = 0; i < recordings.Count; i++)
                {
                    var rec = recordings[i];
                    if (rec == null) continue;
                    if (!string.Equals(
                            rec.PreReFlyAnchorSessionId, sessionId, StringComparison.Ordinal))
                        continue;
                    rec.ClearPreReFlyAnchorTrajectory();
                    cleared++;
                }
            }
            if (cleared > 0)
            {
                ParsekLog.Verbose(Tag,
                    $"Cleared {cleared} pre-Re-Fly anchor snapshot(s) for sess={sessionId}");
            }
            return cleared;
        }

        /// <summary>
        /// Side-effect-free guard for call sites that will append supersede
        /// relations and tombstones before flipping the provisional state.
        /// Stable-leaf/EVA outcomes need a slot-aware verdict; if that lookup
        /// is impossible, fail before any durable merge mutations land.
        /// </summary>
        internal static void PreflightMergeStateClassification(
            ReFlySessionMarker marker, Recording provisional, ParsekScenario scenario)
        {
            if (marker == null || provisional == null
                || object.ReferenceEquals(null, scenario)) return;
            ClassifyMergeStateOrThrow(marker, provisional, scenario, logFallback: false);
        }

        internal static bool TryPredictReFlyMergeIsPermanent(
            ReFlySessionMarker marker,
            Recording provisional,
            ParsekScenario scenario,
            out bool isPermanent,
            out string reason)
        {
            isPermanent = false;
            reason = null;
            if (marker == null)
            {
                reason = "marker-null";
                return false;
            }
            if (provisional == null)
            {
                reason = "provisional-null";
                return false;
            }
            if (object.ReferenceEquals(null, scenario))
            {
                reason = "scenario-null";
                return false;
            }

            try
            {
                MergeStateClassification classification =
                    ClassifyMergeStateOrThrow(
                        marker, provisional, scenario, logFallback: false,
                        isPrediction: true);
                isPermanent = classification.NewState == MergeState.Immutable;
                reason = classification.ClassifierReason
                    ?? classification.Kind.ToString();
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.GetType().Name;
                return false;
            }
        }

        private struct MergeStateClassification
        {
            public TerminalKind Kind;
            public MergeState NewState;
            public string ClassifierReason;
            public bool ClassifierResolvedSlot;
            public bool ClassifierQualifies;
            public int SlotListIndex;
            public string RewindPointId;
            public int FocusSlotIndex;
            public RewindPoint RewindPoint;
            public ChildSlot Slot;
            public bool AutoSealSlot;
            public string AutoSealReason;
        }

        private enum ReFlyCloseReasonKind
        {
            None,
            ClassifierClosed,
            RecordingAction,
            UnsafeClassifierReason,
            StructuralMutation,
        }

        private struct ReFlyCloseReason
        {
            public ReFlyCloseReasonKind Kind;
            public string Detail;

            public bool HasValue => Kind != ReFlyCloseReasonKind.None;

            public string ToLogString()
            {
                switch (Kind)
                {
                    case ReFlyCloseReasonKind.ClassifierClosed:
                        return "classifierClosed:" + (Detail ?? "<none>");
                    case ReFlyCloseReasonKind.RecordingAction:
                        return "recordingAction:" + (Detail ?? "<none>");
                    case ReFlyCloseReasonKind.UnsafeClassifierReason:
                        return "unsafeClassifierReason:" + (Detail ?? "<none>");
                    case ReFlyCloseReasonKind.StructuralMutation:
                        return "structuralMutation:" + (Detail ?? "<none>");
                    default:
                        return null;
                }
            }
        }

        private struct WorldActionSafetyCacheEntry
        {
            public bool HasAction;
            public string ActionSummary;
        }

        private static MergeStateClassification ClassifyMergeStateOrThrow(
            ReFlySessionMarker marker,
            Recording provisional,
            ParsekScenario scenario,
            bool logFallback,
            bool isPrediction = false)
        {
            TerminalKind kind = TerminalKindClassifier.Classify(provisional);
            var result = new MergeStateClassification
            {
                Kind = kind,
                NewState = (kind == TerminalKind.Crashed)
                    ? MergeState.CommittedProvisional
                    : MergeState.Immutable,
                ClassifierReason = "fallback:" + kind,
                SlotListIndex = -1,
                FocusSlotIndex = -1,
            };

            RewindPoint rp;
            int slotListIndex;
            string slotRejectReason;
            if (TryResolveSlotForMergeClassification(
                    marker, provisional, scenario, out rp, out slotListIndex,
                    out slotRejectReason)
                && rp?.ChildSlots != null
                && slotListIndex >= 0
                && slotListIndex < rp.ChildSlots.Count)
            {
                var slot = rp.ChildSlots[slotListIndex];
                string classifierReason;
                // Treat the merge-time slot as the de-facto focus: the player
                // just Re-Flew this slot themselves, so a stable Orbiting /
                // SubOrbital terminal is a concluded outcome, not a "non-focus
                // unconcluded leaf" left over from background flight. Without
                // this override the classifier would compare against the
                // capture-time rp.FocusSlotIndex (whichever vessel happened to
                // be active when the RP was recorded) and keep the slot
                // re-flyable, blocking auto-seal. Natural / non-Re-Fly call
                // sites do not pass an override and continue to use the
                // static focus.
                bool classifierQualifies = UnfinishedFlightClassifier.TryQualify(
                    provisional, slot, rp, false, out classifierReason,
                    treeContext: null, allowNotCommitted: true,
                    focusSlotOverride: slotListIndex);

                ReFlyCloseReason closeReason;
                bool keepSlotOpen = ShouldKeepReFlySlotOpenAfterMerge(
                    provisional, marker, classifierQualifies, classifierReason,
                    out closeReason);
                result.NewState = keepSlotOpen
                    ? MergeState.CommittedProvisional
                    : MergeState.Immutable;
                result.ClassifierReason = classifierReason;
                result.ClassifierResolvedSlot = true;
                result.ClassifierQualifies = classifierQualifies;
                result.SlotListIndex = slotListIndex;
                result.RewindPointId = rp.RewindPointId;
                result.FocusSlotIndex = rp.FocusSlotIndex;
                result.RewindPoint = rp;
                result.Slot = slot;
                if (ShouldAutoSealReFlySlotAfterMerge(
                    provisional, classifierQualifies, closeReason))
                {
                    result.AutoSealSlot = true;
                    result.AutoSealReason = closeReason.ToLogString();
                }
                return result;
            }

            string slotLookupFailure =
                $"Site B-1 slot lookup failed for provisional={provisional.RecordingId ?? "<no-id>"} " +
                $"markerOrigin={marker.OriginChildRecordingId ?? "<none>"} " +
                $"markerTarget={marker.SupersedeTargetId ?? "<none>"} " +
                $"rp={marker.RewindPointId ?? "<none>"} reason={slotRejectReason ?? "slot-index-invalid"}; " +
                $"terminal={DescribeTerminalForLogs(provisional)}";
            if (!IsInPlaceContinuation(marker, provisional)
                && RequiresSlotAwareMergeClassification(provisional))
            {
                if (isPrediction)
                {
                    ParsekLog.Warn(Tag,
                        "Prediction fallback: stable-leaf slot lookup failed; using preview label. " +
                        slotLookupFailure);
                }
                else
                {
                    ParsekLog.Error(Tag,
                        slotLookupFailure +
                        "; aborting because stable-leaf classification cannot safely fall back");
                }
                throw new InvalidOperationException(slotLookupFailure);
            }

            if (logFallback)
            {
                if (IsInPlaceContinuation(marker, provisional))
                {
                    ParsekLog.Verbose(Tag,
                        slotLookupFailure +
                        "; in-place continuation: using v0.9 terminalKind classifier");
                }
                else
                {
                    ParsekLog.Error(Tag,
                        slotLookupFailure + "; falling back to v0.9 terminalKind classifier");
                }
            }

            return result;
        }

        internal static bool IsTerminalFailureReFlyOutcome(Recording rec)
        {
            Recording terminalRec = EffectiveState.ResolveChainTerminalRecording(rec) ?? rec;
            TerminalState? terminal = terminalRec?.TerminalStateValue;
            if (!terminal.HasValue) return false;
            if (terminal.Value == TerminalState.Destroyed)
                return true;
            return !string.IsNullOrEmpty(terminalRec.EvaCrewName)
                && terminal.Value != TerminalState.Boarded;
        }

        private static bool ShouldKeepReFlySlotOpenAfterMerge(
            Recording rec,
            ReFlySessionMarker marker,
            bool classifierQualifies,
            string classifierReason,
            out ReFlyCloseReason closeReason)
        {
            closeReason = default(ReFlyCloseReason);
            if (!classifierQualifies)
            {
                if (TryBuildRecordingActionCloseReason(
                        classifierReason, out closeReason))
                    return false;

                closeReason = new ReFlyCloseReason
                {
                    Kind = ReFlyCloseReasonKind.ClassifierClosed,
                    Detail = classifierReason,
                };
                return false;
            }

            string actionSummary;
            if (TryFindRetryBlockingWorldAction(rec, out actionSummary))
            {
                closeReason = new ReFlyCloseReason
                {
                    Kind = ReFlyCloseReasonKind.RecordingAction,
                    Detail = actionSummary,
                };
                return false;
            }

            if (IsTerminalFailureReFlyOutcome(rec))
                return true;

            // Structural-mutation seal: a Re-Fly that produced a sibling /
            // child vessel via decouple, stage, undock, joint break, etc. is
            // a concluded re-flight even when the chain tip itself is still
            // a "safe stable retry" leaf (Orbiting / SubOrbital non-focus or
            // a Stashed slot). Player intent, by playtest contract: any
            // shape change to the Re-Fly target during the session means
            // they want to keep the run, not retry — close the slot here.
            // Crashed / stranded-EVA outcomes return earlier above so the
            // existing terminal-failure retry path is preserved.
            string structuralDetail;
            if (HasReFlySessionStructuralMutation(rec, marker, out structuralDetail))
            {
                closeReason = new ReFlyCloseReason
                {
                    Kind = ReFlyCloseReasonKind.StructuralMutation,
                    Detail = structuralDetail,
                };
                return false;
            }

            if (!IsSafeStableRetryClassifierReason(classifierReason))
            {
                closeReason = new ReFlyCloseReason
                {
                    Kind = ReFlyCloseReasonKind.UnsafeClassifierReason,
                    Detail = classifierReason,
                };
                return false;
            }

            return true;
        }

        /// <summary>
        /// Returns true when the Re-Fly session created at least one
        /// structural-event branch point in the provisional's tree after
        /// the rewind point's UT — i.e. a decouple / stage / undock /
        /// joint-break / EVA produced a sibling vessel during this
        /// Re-Fly. Detection is on tree topology rather than
        /// <see cref="Recording.CreatingSessionId"/> because
        /// <c>CreateBreakupChildRecording</c> does not propagate the
        /// session id to debris / controlled-child recordings, so a
        /// session-tagged-recording scan would miss the most common
        /// structural events (visible in the playtest log as
        /// <c>Coalescer ProcessBreakupEvent: ... child created</c>).
        ///
        /// <para>
        /// The cutoff is the resolved rewind point's <c>UT</c>, NOT
        /// <c>marker.InvokedUT</c>. <c>InvokedUT</c> is the live UT at
        /// the moment the player clicked Re-Fly — typically much later
        /// than the rewind point's UT, because the RP quicksave throws
        /// the player back to an earlier saved state. A player who
        /// clicked Re-Fly at UT=300, was rewound to UT=100, and then
        /// decoupled at UT=150 must trip this gate; using
        /// <c>marker.InvokedUT</c> would incorrectly reject that
        /// branch point as "pre-rewind". If the marker's RP cannot be
        /// resolved on the live scenario (defensive: should not happen
        /// during a normal Re-Fly merge), the helper falls back to
        /// <c>marker.InvokedUT</c> so the gate at least catches the
        /// post-invocation tail.
        /// </para>
        ///
        /// <para>
        /// Non-structural branch-point types
        /// (<see cref="BranchPointType.Dock"/>,
        /// <see cref="BranchPointType.Board"/>,
        /// <see cref="BranchPointType.Launch"/>,
        /// <see cref="BranchPointType.Terminal"/>) are excluded — they
        /// either attach to a pre-existing vessel without creating a
        /// new one, or mark tree boundaries. Caller is expected to gate
        /// by terminal kind (crashed / EVA outcomes use the existing
        /// retry path before this check fires).
        /// </para>
        /// </summary>
        internal static bool HasReFlySessionStructuralMutation(
            Recording rec,
            ReFlySessionMarker marker,
            out string detail)
        {
            detail = null;
            if (!ScanReFlySessionStructuralMutations(
                    rec, marker,
                    out int matchedCount,
                    out int spliceExcludedCount,
                    out int lineageExcludedCount,
                    out string firstBpId,
                    out BranchPointType? firstBpType,
                    out double cutoffSource,
                    out string cutoffOriginLabel,
                    out int baselineCount,
                    out int lineageCount))
            {
                return false;
            }

            var ic = CultureInfo.InvariantCulture;
            detail = $"branchPoints={matchedCount.ToString(ic)}" +
                $" spliceExcluded={spliceExcludedCount.ToString(ic)}" +
                $" lineageExcluded={lineageExcludedCount.ToString(ic)}" +
                $" firstBp={firstBpId ?? "<no-id>"}" +
                $" firstType={(firstBpType.HasValue ? firstBpType.Value.ToString() : "<none>")}" +
                $" sinceUT={cutoffSource.ToString("F2", ic)}" +
                $" cutoffOrigin={cutoffOriginLabel}" +
                $" baseline={baselineCount.ToString(ic)}" +
                $" lineage={lineageCount.ToString(ic)}";
            return true;
        }

        /// <summary>
        /// Read-only typed accessor for the pre-transition merge dialog's
        /// auto-seal preview (<see cref="ReFlyAutoSealPreviewer"/>): returns
        /// the first structural <see cref="BranchPointType"/> encountered
        /// during the same scan that <see cref="HasReFlySessionStructuralMutation"/>
        /// runs. Returns false (and <c>default</c> for <paramref name="firstType"/>)
        /// when no structural mutation has occurred yet, when the marker
        /// pre-session baseline is missing, or when guard conditions match
        /// the production gate's early-return.
        /// </summary>
        internal static bool TryGetFirstReFlySessionStructuralMutationType(
            Recording rec,
            ReFlySessionMarker marker,
            out BranchPointType firstType)
        {
            if (ScanReFlySessionStructuralMutations(
                    rec, marker,
                    out _, out _, out _,
                    out _,
                    out BranchPointType? scannedFirstType,
                    out _, out _, out _, out _)
                && scannedFirstType.HasValue)
            {
                firstType = scannedFirstType.Value;
                return true;
            }
            firstType = default(BranchPointType);
            return false;
        }

        /// <summary>
        /// Shared scan body for <see cref="HasReFlySessionStructuralMutation"/>
        /// and <see cref="TryGetFirstReFlySessionStructuralMutationType"/>.
        /// Pure: walks tree topology and the marker baseline; no mutation.
        /// Returns false when the gate's early-returns trip; the out
        /// parameters are populated only on success.
        /// </summary>
        private static bool ScanReFlySessionStructuralMutations(
            Recording rec,
            ReFlySessionMarker marker,
            out int matchedCount,
            out int spliceExcludedCount,
            out int lineageExcludedCount,
            out string firstBpId,
            out BranchPointType? firstBpType,
            out double cutoffSource,
            out string cutoffOriginLabel,
            out int baselineCount,
            out int lineageCount)
        {
            matchedCount = 0;
            spliceExcludedCount = 0;
            lineageExcludedCount = 0;
            firstBpId = null;
            firstBpType = null;
            cutoffSource = 0.0;
            cutoffOriginLabel = null;
            baselineCount = 0;
            lineageCount = 0;

            if (rec == null || marker == null) return false;
            if (string.IsNullOrEmpty(marker.TreeId)) return false;
            if (string.IsNullOrEmpty(rec.TreeId)) return false;
            if (!string.Equals(rec.TreeId, marker.TreeId,
                    StringComparison.Ordinal))
                return false;

            // Session-local baseline: when the marker was created we
            // snapshotted every existing BranchPoint.Id in the tree.
            // Without that baseline, the load-time splice path
            // (`SpliceMissingCommittedRecordingsIntoLoadedTree`) re-grafts
            // pre-Re-Fly post-RP branch points back into the loaded tree
            // and they look identical to session-authored ones — so the
            // gate would auto-seal a stashed slot the player never
            // mutated. Conservatively skip the gate when the baseline is
            // absent (legacy markers created before this field shipped).
            if (marker.PreSessionBranchPointIds == null) return false;

            ParsekScenario scenario = ParsekScenario.Instance;
            double? cutoff = TryResolveReFlyStructuralCutoffUT(
                marker, scenario, out cutoffOriginLabel);
            if (!cutoff.HasValue) return false;
            cutoffSource = cutoff.Value;

            RecordingTree tree = RecordingStore.CommittedTrees != null
                ? RecordingStore.CommittedTrees.Find(t =>
                    t != null
                    && string.Equals(t.Id, marker.TreeId,
                        StringComparison.Ordinal))
                : null;
            if (tree == null || tree.BranchPoints == null) return false;

            HashSet<string> preSessionSet = new HashSet<string>(
                marker.PreSessionBranchPointIds, StringComparer.Ordinal);
            HashSet<string> lineageSet = BuildReFlyTargetLineageRecordingIds(rec);
            baselineCount = marker.PreSessionBranchPointIds.Count;
            lineageCount = lineageSet.Count;

            // A small UT slack absorbs floating-point round-trip drift
            // between the resolved cutoff UT and a branch-point UT that
            // arose from the same physics frame. The rewind point itself
            // does not author its own branch point at its UT, so this is
            // exclusion-safe.
            const double UtSlackSeconds = 0.001;
            double cutoffWithSlack = cutoffSource + UtSlackSeconds;

            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                var bp = tree.BranchPoints[i];
                if (bp == null) continue;
                if (!IsStructuralBranchPointType(bp.Type)) continue;
                if (bp.UT < cutoffWithSlack) continue;
                if (!string.IsNullOrEmpty(bp.Id)
                    && preSessionSet.Contains(bp.Id))
                {
                    // Pre-existing BP re-spliced into the loaded tree by
                    // SpliceMissingCommittedRecordingsIntoLoadedTree —
                    // not a Re-Fly-session mutation.
                    spliceExcludedCount++;
                    continue;
                }
                if (!IsBranchPointInReFlyTargetLineage(bp, lineageSet))
                {
                    // Same tree, but the BP's parent recordings don't
                    // intersect the Re-Fly target's lineage (provisional +
                    // chain segments). A background vessel that staged /
                    // undocked / joint-broke during this session in the
                    // same tree authored its own BP with its own parent
                    // ids — that's not a mutation of the player-chosen
                    // slot and must not auto-seal it.
                    lineageExcludedCount++;
                    continue;
                }

                matchedCount++;
                if (firstBpId == null)
                {
                    firstBpId = bp.Id;
                    firstBpType = bp.Type;
                }
            }

            return matchedCount > 0;
        }

        /// <summary>
        /// Builds the set of recording ids that count as the Re-Fly target's
        /// lineage for structural-mutation detection: the provisional itself
        /// plus every committed recording in the same tree / chain /
        /// chain-branch as the provisional. Optimizer-split tails of the
        /// in-place chain share the provisional's <see cref="Recording.ChainId"/>
        /// + <see cref="Recording.ChainBranch"/> and are therefore included.
        /// A BranchPoint whose <see cref="BranchPoint.ParentRecordingIds"/>
        /// does not intersect this set was authored by an unrelated vessel
        /// (background sibling in the same tree) and must not trip the gate.
        /// </summary>
        private static HashSet<string> BuildReFlyTargetLineageRecordingIds(Recording provisional)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (provisional == null) return set;
            if (!string.IsNullOrEmpty(provisional.RecordingId))
                set.Add(provisional.RecordingId);

            string treeId = provisional.TreeId;
            string chainId = provisional.ChainId;
            int chainBranch = provisional.ChainBranch;
            if (string.IsNullOrEmpty(treeId) || string.IsNullOrEmpty(chainId))
                return set;

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return set;
            for (int i = 0; i < committed.Count; i++)
            {
                var cand = committed[i];
                if (cand == null) continue;
                if (object.ReferenceEquals(cand, provisional)) continue;
                if (string.IsNullOrEmpty(cand.RecordingId)) continue;
                if (!string.Equals(cand.TreeId, treeId, StringComparison.Ordinal))
                    continue;
                if (!string.Equals(cand.ChainId, chainId, StringComparison.Ordinal))
                    continue;
                if (cand.ChainBranch != chainBranch) continue;
                set.Add(cand.RecordingId);
            }
            return set;
        }

        private static bool IsBranchPointInReFlyTargetLineage(
            BranchPoint bp, HashSet<string> lineageSet)
        {
            if (bp == null || lineageSet == null || lineageSet.Count == 0)
                return false;
            var parents = bp.ParentRecordingIds;
            if (parents == null || parents.Count == 0)
                return false;
            for (int i = 0; i < parents.Count; i++)
            {
                string pid = parents[i];
                if (!string.IsNullOrEmpty(pid) && lineageSet.Contains(pid))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Resolves the UT cutoff used by structural-mutation detection.
        /// Prefers the marker's rewind point UT (where the player was
        /// placed back to by the RP quicksave); falls back to
        /// <c>marker.InvokedUT</c> if the RP cannot be located on the
        /// live scenario. Returns null only when neither source supplies
        /// a finite, non-negative UT.
        /// </summary>
        private static double? TryResolveReFlyStructuralCutoffUT(
            ReFlySessionMarker marker,
            ParsekScenario scenario,
            out string originLabel)
        {
            originLabel = "<none>";
            if (marker == null) return null;

            if (!object.ReferenceEquals(null, scenario)
                && scenario.RewindPoints != null
                && !string.IsNullOrEmpty(marker.RewindPointId))
            {
                for (int i = 0; i < scenario.RewindPoints.Count; i++)
                {
                    var candidate = scenario.RewindPoints[i];
                    if (candidate == null) continue;
                    if (!string.Equals(candidate.RewindPointId,
                            marker.RewindPointId, StringComparison.Ordinal))
                        continue;
                    if (double.IsNaN(candidate.UT)
                        || double.IsInfinity(candidate.UT)
                        || candidate.UT < 0)
                        break;
                    originLabel = "rpUT";
                    return candidate.UT;
                }
            }

            if (!double.IsNaN(marker.InvokedUT)
                && !double.IsInfinity(marker.InvokedUT)
                && marker.InvokedUT >= 0)
            {
                originLabel = "invokedUT";
                return marker.InvokedUT;
            }

            return null;
        }

        private static bool IsStructuralBranchPointType(BranchPointType type)
        {
            // Dock / Board / Launch / Terminal are not "the player changed
            // the vessel's shape": Dock and Board attach to a pre-existing
            // vessel without spawning a new one mid-flight; Launch is the
            // tree root; Terminal marks the recording's end. Everything
            // else (Undock, EVA, JointBreak, Breakup) creates a new
            // sibling vessel and counts as structural mutation.
            switch (type)
            {
                case BranchPointType.Undock:
                case BranchPointType.EVA:
                case BranchPointType.JointBreak:
                case BranchPointType.Breakup:
                    return true;
                default:
                    return false;
            }
        }

        private static bool ShouldAutoSealReFlySlotAfterMerge(
            Recording rec,
            bool classifierQualifies,
            ReFlyCloseReason closeReason)
        {
            if (!closeReason.HasValue)
                return false;
            if (closeReason.Kind == ReFlyCloseReasonKind.RecordingAction)
                return true;
            if (closeReason.Kind == ReFlyCloseReasonKind.StructuralMutation)
                return true;
            if (classifierQualifies)
                return true;

            if (closeReason.Kind != ReFlyCloseReasonKind.ClassifierClosed)
                return false;

            if (string.Equals(closeReason.Detail, "downstreamBp",
                    StringComparison.Ordinal))
                return true;
            if (string.Equals(closeReason.Detail, "stableTerminal",
                    StringComparison.Ordinal))
                return IsHardSafetyTerminal(rec);
            // The Re-Fly target slot (player-chosen, either static focus or
            // promoted via focusSlotOverride) reached a stable Orbiting /
            // SubOrbital terminal. Per playtest contract: a successful
            // Re-Fly to stable state seals the slot — the player is done
            // with that line of flight, no further retry expected.
            if (string.Equals(closeReason.Detail, "stableTerminalFocusSlot",
                    StringComparison.Ordinal))
                return true;

            return false;
        }

        private static bool TryBuildRecordingActionCloseReason(
            string classifierReason,
            out ReFlyCloseReason closeReason)
        {
            closeReason = default(ReFlyCloseReason);
            if (string.IsNullOrEmpty(classifierReason)
                || !classifierReason.StartsWith(
                    UnfinishedFlightClassifier.RecordingActionReasonPrefix,
                    StringComparison.Ordinal))
                return false;

            closeReason = new ReFlyCloseReason
            {
                Kind = ReFlyCloseReasonKind.RecordingAction,
                Detail = classifierReason.Substring(
                    UnfinishedFlightClassifier.RecordingActionReasonPrefix.Length),
            };
            return true;
        }

        private static bool IsHardSafetyTerminal(Recording rec)
        {
            Recording terminalRec = EffectiveState.ResolveChainTerminalRecording(rec) ?? rec;
            TerminalState? terminal = terminalRec?.TerminalStateValue;
            if (!terminal.HasValue)
                return false;

            return terminal.Value == TerminalState.Recovered
                || terminal.Value == TerminalState.Docked
                || terminal.Value == TerminalState.Boarded;
        }

        private static bool IsSafeStableRetryClassifierReason(string classifierReason)
        {
            return string.Equals(classifierReason, "stableLeafUnconcluded",
                    StringComparison.Ordinal)
                || string.Equals(classifierReason, "stashedStableLeaf",
                    StringComparison.Ordinal);
        }

        // Strict mode intentionally stays available as the tested ledger-safety
        // contract below retry mode. STASH/Re-Fly retry callers should use
        // TryFindRetryBlockingWorldAction so automatic consequence rows do not
        // close terminal-failure retries by themselves.
        internal static bool TryFindRecordingScopedWorldAction(
            Recording rec,
            out string actionSummary)
        {
            return TryFindRecordingScopedWorldAction(
                rec, retryBlockingOnly: false, out actionSummary);
        }

        internal static bool TryFindRetryBlockingWorldAction(
            Recording rec,
            out string actionSummary)
        {
            return TryFindRecordingScopedWorldAction(
                rec, retryBlockingOnly: true, out actionSummary);
        }

        private static bool TryFindRecordingScopedWorldAction(
            Recording rec,
            bool retryBlockingOnly,
            out string actionSummary)
        {
            actionSummary = null;
            string cacheKey = BuildWorldActionSafetyCacheKey(
                rec, retryBlockingOnly);
            if (cacheKey == null)
                return false;

            ParsekScenario scenario = ParsekScenario.Instance;
            WorldActionSafetyCacheEntry cached;
            if (TryGetCachedWorldActionSafetyVerdict(
                    cacheKey, scenario, out cached))
            {
                actionSummary = cached.ActionSummary;
                return cached.HasAction;
            }

            var computed = ComputeRecordingScopedWorldAction(
                rec, retryBlockingOnly);
            CacheWorldActionSafetyVerdict(cacheKey, scenario, computed);
            actionSummary = computed.ActionSummary;
            return computed.HasAction;
        }

        private static WorldActionSafetyCacheEntry ComputeRecordingScopedWorldAction(
            Recording rec,
            bool retryBlockingOnly)
        {
            var result = new WorldActionSafetyCacheEntry();
            var recordingIds = CollectRecordingIdsForSafetyGate(rec);
            if (recordingIds.Count == 0)
                return result;

            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null || string.IsNullOrEmpty(action.RecordingId))
                    continue;
                if (!recordingIds.Contains(action.RecordingId))
                    continue;
                bool hasAction = retryBlockingOnly
                    ? IsRetryBlockingRecordingAction(action, actions)
                    : IsWorldStateChangingRecordingAction(action, actions);
                if (!hasAction)
                    continue;

                result.HasAction = true;
                result.ActionSummary = action.Type + ":" + (action.ActionId ?? "<no-action-id>");
                return result;
            }

            return result;
        }

        private static bool TryGetCachedWorldActionSafetyVerdict(
            string cacheKey,
            ParsekScenario scenario,
            out WorldActionSafetyCacheEntry entry)
        {
            lock (worldActionSafetyCacheLock)
            {
                EnsureWorldActionSafetyCacheCurrent(scenario);
                return worldActionSafetyCache.TryGetValue(cacheKey, out entry);
            }
        }

        private static void CacheWorldActionSafetyVerdict(
            string cacheKey,
            ParsekScenario scenario,
            WorldActionSafetyCacheEntry entry)
        {
            lock (worldActionSafetyCacheLock)
            {
                EnsureWorldActionSafetyCacheCurrent(scenario);
                worldActionSafetyCache[cacheKey] = entry;
            }
        }

        internal static void ResetWorldActionSafetyCacheForTesting()
        {
            lock (worldActionSafetyCacheLock)
            {
                worldActionSafetyCache.Clear();
                worldActionSafetyCacheScenarioIdentity = null;
                worldActionSafetyCacheLedgerVersion = int.MinValue;
                worldActionSafetyCacheStoreVersion = int.MinValue;
                worldActionSafetyCacheSupersedeVersion = int.MinValue;
            }
        }

        private static void EnsureWorldActionSafetyCacheCurrent(
            ParsekScenario scenario)
        {
            int supersedeVersion = !object.ReferenceEquals(null, scenario)
                ? scenario.SupersedeStateVersion
                : 0;
            if (ReferenceEquals(worldActionSafetyCacheScenarioIdentity, scenario)
                && worldActionSafetyCacheLedgerVersion == Ledger.StateVersion
                && worldActionSafetyCacheStoreVersion == RecordingStore.StateVersion
                && worldActionSafetyCacheSupersedeVersion == supersedeVersion)
                return;

            worldActionSafetyCache.Clear();
            worldActionSafetyCacheScenarioIdentity = scenario;
            worldActionSafetyCacheLedgerVersion = Ledger.StateVersion;
            worldActionSafetyCacheStoreVersion = RecordingStore.StateVersion;
            worldActionSafetyCacheSupersedeVersion = supersedeVersion;
        }

        private static string BuildWorldActionSafetyCacheKey(
            Recording rec,
            bool retryBlockingOnly)
        {
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return null;

            string mode = retryBlockingOnly ? "retry" : "strict";
            return mode
                + WorldActionCacheKeySeparator
                + rec.RecordingId
                + WorldActionCacheKeySeparator
                + (rec.SupersedeTargetId ?? string.Empty)
                + WorldActionCacheKeySeparator
                + (rec.TreeId ?? string.Empty)
                + WorldActionCacheKeySeparator
                + (rec.ChainId ?? string.Empty)
                + WorldActionCacheKeySeparator
                + rec.ChainBranch.ToString(CultureInfo.InvariantCulture);
        }

        // Internal so the pre-transition merge dialog's auto-seal preview
        // (ReFlyAutoSealPreviewer) can build the same lineage set the
        // production retry-blocking science check uses.
        internal static HashSet<string> CollectRecordingIdsForSafetyGate(Recording rec)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            AddRecordingId(ids, rec);
            AddRecordingId(ids, rec?.SupersedeTargetId);

            Recording terminalRec = EffectiveState.ResolveChainTerminalRecording(rec) ?? rec;
            AddRecordingId(ids, terminalRec);

            AddMatchingChainRecordingIds(ids, rec, RecordingStore.CommittedRecordings);
            for (int i = 0; i < RecordingStore.CommittedTrees.Count; i++)
            {
                var tree = RecordingStore.CommittedTrees[i];
                if (tree?.Recordings == null) continue;
                AddMatchingChainRecordingIds(ids, rec, tree.Recordings.Values);
            }
            AddSupersedeLineageRecordingIds(
                ids, ParsekScenario.Instance?.RecordingSupersedes);

            return ids;
        }

        private static void AddRecordingId(HashSet<string> ids, Recording rec)
        {
            if (ids == null || rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return;
            ids.Add(rec.RecordingId);
        }

        private static void AddRecordingId(HashSet<string> ids, string recordingId)
        {
            if (ids == null || string.IsNullOrEmpty(recordingId))
                return;
            ids.Add(recordingId);
        }

        private static void AddMatchingChainRecordingIds(
            HashSet<string> ids,
            Recording anchor,
            IEnumerable<Recording> candidates)
        {
            if (ids == null || anchor == null || candidates == null)
                return;
            if (string.IsNullOrEmpty(anchor.ChainId))
                return;

            foreach (var candidate in candidates)
            {
                if (candidate == null || string.IsNullOrEmpty(candidate.RecordingId))
                    continue;
                if (!string.Equals(candidate.ChainId, anchor.ChainId,
                        StringComparison.Ordinal))
                    continue;
                if (candidate.ChainBranch != anchor.ChainBranch)
                    continue;
                if (!string.IsNullOrEmpty(anchor.TreeId)
                    && !string.IsNullOrEmpty(candidate.TreeId)
                    && !string.Equals(candidate.TreeId, anchor.TreeId,
                        StringComparison.Ordinal))
                    continue;

                ids.Add(candidate.RecordingId);
            }
        }

        private static void AddSupersedeLineageRecordingIds(
            HashSet<string> ids,
            IReadOnlyList<RecordingSupersedeRelation> supersedes)
        {
            if (ids == null || supersedes == null || supersedes.Count == 0)
                return;

            bool added;
            do
            {
                added = false;
                for (int i = 0; i < supersedes.Count; i++)
                {
                    var rel = supersedes[i];
                    if (rel == null
                        || string.IsNullOrEmpty(rel.OldRecordingId)
                        || string.IsNullOrEmpty(rel.NewRecordingId))
                        continue;
                    if (!ids.Contains(rel.NewRecordingId)
                        || ids.Contains(rel.OldRecordingId))
                        continue;

                    ids.Add(rel.OldRecordingId);
                    added = true;
                }
            }
            while (added);
        }

        internal static bool IsWorldStateChangingRecordingAction(
            GameAction action,
            IReadOnlyList<GameAction> sameTimelineActions)
        {
            if (action == null || string.IsNullOrEmpty(action.RecordingId))
                return false;

            switch (action.Type)
            {
                case GameActionType.FundsInitial:
                case GameActionType.ScienceInitial:
                case GameActionType.ReputationInitial:
                    return false;
            }

            if (TombstoneEligibility.IsEligible(action))
                return false;

            GameAction pairedDeathAction;
            if (TombstoneEligibility.TryPairBundledRepPenalty(
                    action, sameTimelineActions, out pairedDeathAction))
                return false;

            return true;
        }

        internal static bool IsRetryBlockingRecordingAction(
            GameAction action,
            IReadOnlyList<GameAction> sameTimelineActions)
        {
            if (!IsWorldStateChangingRecordingAction(action, sameTimelineActions))
                return false;

            // Retry-blocking auto-seal fires only on intentional player actions taken
            // on the vessel that produce sticky world-state effects. ScienceEarning
            // (Crew Report / EVA Report / Surface Sample / Transmit / Recover) is the
            // only ledger row in this category — every other GameActionType is either
            //
            //   - an automatic game consequence (MilestoneAchievement,
            //     ContractComplete/Fail, FundsEarning, ReputationEarning/Penalty,
            //     KerbalAssignment/Rescue/StandIn, FacilityDestruction) — denylisted
            //     by IsWorldStateChangingRecordingAction or by exclusion here;
            //
            //   - a KSC-scene player action (ContractAccept/Cancel, KerbalHire,
            //     FacilityUpgrade/Repair, StrategyActivate/Deactivate,
            //     ScienceSpending) that cannot reach this gate with a flight-recording
            //     tag because GameStateRecorder.Emit only stamps a tag in FLIGHT
            //     scene with a live recorder; or
            //
            //   - a paid-once setup row (FundsSpending(VesselBuild) adopted to the
            //     recording via TryAdoptRolloutAction) whose effect survives a
            //     revert/retag without re-charging the player, so sealing on it
            //     would punish retries for spending the player already accepted.
            //
            // Structural mutations (decouple / stage / undock / EVA / joint break)
            // and hard safety terminals (Recovered / Docked / Boarded) seal via
            // their own dedicated gates (HasReFlySessionStructuralMutation +
            // IsHardSafetyTerminal), not through this predicate.
            return action.Type == GameActionType.ScienceEarning;
        }

        private static void ApplyAutoSealAfterSafetyClose(
            MergeStateClassification classification,
            Recording provisional,
            ParsekScenario scenario)
        {
            if (!classification.AutoSealSlot || classification.Slot == null
                || object.ReferenceEquals(null, scenario))
                return;

            if (classification.Slot.Sealed)
                return;

            classification.Slot.Sealed = true;
            classification.Slot.SealedRealTime =
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            scenario.BumpSupersedeStateVersion();
            ParsekLog.Info(Tag,
                $"Auto-sealed re-fly slot={classification.SlotListIndex.ToString(CultureInfo.InvariantCulture)} " +
                $"rec={provisional?.RecordingId ?? "<no-id>"} " +
                $"rp={classification.RewindPoint?.RewindPointId ?? "<no-rp>"} " +
                $"terminal={DescribeTerminalForLogs(provisional)} " +
                $"reason={classification.AutoSealReason ?? "<none>"}");
        }

        private static bool TryResolveSlotForMergeClassification(
            ReFlySessionMarker marker,
            Recording provisional,
            ParsekScenario scenario,
            out RewindPoint rp,
            out int slotListIndex,
            out string rejectReason)
        {
            if (UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                    provisional, out rp, out slotListIndex, out rejectReason))
                return true;

            return TryResolveSlotByMarkerTarget(
                marker, provisional, scenario, out rp, out slotListIndex, out rejectReason);
        }

        private static bool TryResolveSlotByMarkerTarget(
            ReFlySessionMarker marker,
            Recording provisional,
            ParsekScenario scenario,
            out RewindPoint rp,
            out int slotListIndex,
            out string rejectReason)
        {
            rp = null;
            slotListIndex = -1;
            rejectReason = null;
            if (marker == null || provisional == null)
            {
                rejectReason = "marker-or-provisional-null";
                return false;
            }

            string slotTarget = marker.SupersedeTargetId ?? marker.OriginChildRecordingId;
            if (string.IsNullOrEmpty(slotTarget))
            {
                rejectReason = "noMarkerSlotTarget";
                return false;
            }

            string parentBp = provisional.ParentBranchPointId;
            string childBp = provisional.ChildBranchPointId;
            bool hasBranchLink =
                !string.IsNullOrEmpty(parentBp)
                || !string.IsNullOrEmpty(childBp);

            if (object.ReferenceEquals(null, scenario) || scenario.RewindPoints == null)
            {
                rejectReason = "noScenario";
                return false;
            }

            IReadOnlyList<RecordingSupersedeRelation> supersedes =
                scenario.RecordingSupersedes
                ?? (IReadOnlyList<RecordingSupersedeRelation>)Array.Empty<RecordingSupersedeRelation>();
            var targetRec = new Recording { RecordingId = slotTarget };
            bool matchedRp = false;
            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                var candidate = scenario.RewindPoints[i];
                if (candidate == null) continue;
                bool matchesParent = !string.IsNullOrEmpty(parentBp)
                    && string.Equals(candidate.BranchPointId, parentBp, StringComparison.Ordinal);
                bool matchesChild = !string.IsNullOrEmpty(childBp)
                    && string.Equals(candidate.BranchPointId, childBp, StringComparison.Ordinal);
                if (!matchesParent && !matchesChild)
                    continue;

                matchedRp = true;
                int resolved = EffectiveState.ResolveRewindPointSlotIndexForRecording(
                    candidate, targetRec, supersedes);
                if (resolved < 0)
                    continue;

                rp = candidate;
                slotListIndex = resolved;
                return true;
            }

            if (!IsInPlaceContinuation(marker, provisional)
                && TryResolveSlotByOriginTarget(
                    marker,
                    targetRec,
                    scenario,
                    supersedes,
                    out rp,
                    out slotListIndex))
            {
                return true;
            }

            if (!hasBranchLink)
            {
                rejectReason = "noParentBp";
                return false;
            }

            rejectReason = matchedRp ? "noMatchingMarkerTargetSlot" : "noMatchingRP";
            return false;
        }

        private static bool TryResolveSlotByOriginTarget(
            ReFlySessionMarker marker,
            Recording targetRec,
            ParsekScenario scenario,
            IReadOnlyList<RecordingSupersedeRelation> supersedes,
            out RewindPoint rp,
            out int slotListIndex)
        {
            rp = null;
            slotListIndex = -1;
            if (targetRec == null
                || object.ReferenceEquals(null, scenario)
                || scenario.RewindPoints == null)
                return false;

            string markerRpId = marker?.RewindPointId;
            if (!string.IsNullOrEmpty(markerRpId))
            {
                for (int i = 0; i < scenario.RewindPoints.Count; i++)
                {
                    var candidate = scenario.RewindPoints[i];
                    if (candidate == null) continue;
                    if (!string.Equals(candidate.RewindPointId, markerRpId, StringComparison.Ordinal))
                        continue;

                    int resolved = EffectiveState.ResolveRewindPointSlotIndexForRecording(
                        candidate, targetRec, supersedes);
                    if (resolved < 0)
                        return false;

                    rp = candidate;
                    slotListIndex = resolved;
                    return true;
                }
            }

            return UnfinishedFlightClassifier.TryResolveRewindPointByOriginSlot(
                targetRec,
                scenario.RewindPoints,
                supersedes,
                out rp,
                out slotListIndex);
        }

        private static bool RequiresSlotAwareMergeClassification(Recording rec)
        {
            if (rec == null) return false;
            Recording terminalRec = EffectiveState.ResolveChainTerminalRecording(rec) ?? rec;
            TerminalState? terminal = terminalRec.TerminalStateValue;
            if (!terminal.HasValue) return false;
            if (terminal.Value == TerminalState.Orbiting
                || terminal.Value == TerminalState.SubOrbital)
                return true;
            return !string.IsNullOrEmpty(terminalRec.EvaCrewName)
                && terminal.Value != TerminalState.Boarded;
        }

        private static bool IsInPlaceContinuation(ReFlySessionMarker marker, Recording provisional)
        {
            return marker != null
                && provisional != null
                && !string.IsNullOrEmpty(marker.OriginChildRecordingId)
                && string.Equals(
                    marker.OriginChildRecordingId,
                    provisional.RecordingId,
                    StringComparison.Ordinal);
        }

        private static string DescribeTerminalForLogs(Recording rec)
        {
            if (rec == null) return "<null>";
            Recording terminalRec = EffectiveState.ResolveChainTerminalRecording(rec) ?? rec;
            return terminalRec.TerminalStateValue.HasValue
                ? terminalRec.TerminalStateValue.Value.ToString()
                : "<none>";
        }

        /// <summary>
        /// Phase 9 of Rewind-to-Staging (design §6.6 step 4 / §7.13-§7.17 /
        /// §7.41 / §7.44 / §10.4): walk <see cref="Ledger.Actions"/> and
        /// append <see cref="LedgerTombstone"/>s for every non-seed,
        /// recording-scoped career action in the supersede subtree.
        ///
        /// <para>
        /// Null-scoped actions still survive because they are not attributable
        /// to one superseded recording branch. Seed rows also survive because
        /// they define the career baseline. Rollout build costs survive because
        /// Re-Fly reuses the already-paid launch rather than asking KSP to charge
        /// a fresh rollout.
        /// </para>
        ///
        /// <para>
        /// Idempotent: an action that already carries a tombstone (matched by
        /// <see cref="GameAction.ActionId"/>) is skipped. Null-scoped actions
        /// (<see cref="GameAction.RecordingId"/> == null) are never tombstoned
        /// (§7.41).
        /// </para>
        ///
        /// <para>
        /// After appending, bumps
        /// <see cref="ParsekScenario.TombstoneStateVersion"/> so the ELS cache
        /// invalidates on the next <see cref="EffectiveState.ComputeELS"/>
        /// call, then asks <see cref="CrewReservationManager.RecomputeAfterTombstones"/>
        /// to re-derive the reservation dictionary from the broad post-merge
        /// ELS rather than retaining old-subtree crew assignments (§7.16), and
        /// refreshes the recalculated KSP state when ledger content was removed.
        /// </para>
        /// </summary>
        internal static void CommitTombstones(
            ReFlySessionMarker marker,
            IReadOnlyCollection<string> subtreeIds,
            string retiringRecordingId,
            double mergeUT,
            string nowIso,
            ParsekScenario scenario)
        {
            // ReferenceEquals to bypass Unity's Object == null override (test
            // fixtures install plain-CLR ParsekScenario instances without a
            // Unity lifecycle; scenario == null would be true there even
            // though the reference is valid).
            if (ReferenceEquals(null, scenario))
            {
                ParsekLog.Warn(LedgerSwapTag, "CommitTombstones: no scenario — skipping");
                return;
            }
            if (scenario.LedgerTombstones == null)
                scenario.LedgerTombstones = new List<LedgerTombstone>();

            string originId = marker != null ? (marker.OriginChildRecordingId ?? "<no-origin>") : "<no-marker>";

            // No subtree means nothing to retire, but still bump the tombstone
            // state version so cache invalidation is unconditional at merge time.
            if (subtreeIds == null || subtreeIds.Count == 0)
            {
                ParsekLog.Info(LedgerSwapTag,
                    "Tombstoned 0 career actions (Contract=0, Milestone=0, Facility=0, " +
                    "Strategy=0, ScienceSpending=0, Science=0, Funds=0, Reputation=0, Kerbal=0, Other=0); " +
                    "0 excluded (Seed=0, Rollout=0)");
                ParsekLog.Info(Tag,
                    "Supersede tombstone effects: tombstoned 0 recording-scoped career actions; ELS unchanged.");
                scenario.BumpTombstoneStateVersion();
                CrewReservationManager.RecomputeAfterTombstones();
                return;
            }

            // Pre-index existing tombstones by ActionId for O(1) idempotence.
            var alreadyTombstoned = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < scenario.LedgerTombstones.Count; i++)
            {
                var t = scenario.LedgerTombstones[i];
                if (t == null || string.IsNullOrEmpty(t.ActionId)) continue;
                alreadyTombstoned.Add(t.ActionId);
            }

            // Group actions in the subtree scope by RecordingId so the rep-
            // bundling rule can scan a bounded slice per candidate. Materialize
            // the subtree into a HashSet so InSupersedeScope gets O(1) lookup
            // and the ICollection<string> contract is met.
            var subtreeSet = new HashSet<string>(subtreeIds, StringComparer.Ordinal);
            var actions = Ledger.Actions;
            var sliceByRecording = new Dictionary<string, List<GameAction>>(StringComparer.Ordinal);
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a == null) continue;
                if (!TombstoneAttributionHelper.InSupersedeScope(a, subtreeSet))
                    continue;
                List<GameAction> slice;
                if (!sliceByRecording.TryGetValue(a.RecordingId, out slice))
                {
                    slice = new List<GameAction>();
                    sliceByRecording[a.RecordingId] = slice;
                }
                slice.Add(a);
            }

            int contractCount = 0;
            int milestoneCount = 0;
            int facilityCount = 0;
            int strategyCount = 0;
            int scienceSpendingCount = 0;
            int scienceCount = 0;
            int fundsCount = 0;
            int reputationCount = 0;
            int kerbalCount = 0;
            int otherCount = 0;
            int seedExcluded = 0;
            int rolloutExcluded = 0;
            int otherExcluded = 0;
            var tombstonedRosterActions = new List<GameAction>();
            HashSet<string> tombstonedScienceSpendingNodeIds = null;

            foreach (var kv in sliceByRecording)
            {
                var slice = kv.Value;
                for (int i = 0; i < slice.Count; i++)
                {
                    var a = slice[i];
                    if (string.IsNullOrEmpty(a.ActionId))
                    {
                        // Shouldn't happen post-Phase-1 (every action gets an
                        // ActionId at construction / legacy hash on load) but
                        // tolerate defensively; can't tombstone without an id.
                        continue;
                    }
                    if (alreadyTombstoned.Contains(a.ActionId))
                    {
                        ParsekLog.Verbose(LedgerSwapTag,
                            $"Skip: action '{a.ActionId}' already tombstoned (idempotent re-entry)");
                        continue;
                    }

                    bool eligible = TombstoneEligibility.IsSupersedeTombstoneEligible(a);

                    if (!eligible)
                    {
                        CountExcludedByType(a,
                            ref seedExcluded,
                            ref rolloutExcluded,
                            ref otherExcluded);
                        continue;
                    }

                    CountTombstonedByType(a.Type,
                        ref contractCount,
                        ref milestoneCount,
                        ref facilityCount,
                        ref strategyCount,
                        ref scienceSpendingCount,
                        ref scienceCount,
                        ref fundsCount,
                        ref reputationCount,
                        ref kerbalCount,
                        ref otherCount);
                    if (a.Type == GameActionType.ScienceSpending &&
                        !string.IsNullOrEmpty(a.NodeId))
                    {
                        if (tombstonedScienceSpendingNodeIds == null)
                            tombstonedScienceSpendingNodeIds = new HashSet<string>(
                                StringComparer.Ordinal);
                        tombstonedScienceSpendingNodeIds.Add(a.NodeId);
                    }
                    string rosterKerbalName;
                    if (KerbalsModule.TryGetRosterCreatedKerbalName(a, out rosterKerbalName))
                        tombstonedRosterActions.Add(a);

                    var tomb = new LedgerTombstone
                    {
                        TombstoneId = "tomb_" + Guid.NewGuid().ToString("N"),
                        ActionId = a.ActionId,
                        RetiringRecordingId = retiringRecordingId,
                        UT = mergeUT,
                        CreatedRealTime = nowIso,
                    };
                    scenario.LedgerTombstones.Add(tomb);
                    alreadyTombstoned.Add(a.ActionId);

                    ParsekLog.Verbose(LedgerSwapTag,
                        $"tomb={tomb.TombstoneId} action={a.ActionId} type={a.Type} " +
                        $"rec={a.RecordingId} ut={a.UT.ToString("R", CultureInfo.InvariantCulture)}");
                }
            }

            int tombstoned = contractCount + milestoneCount + facilityCount + strategyCount
                + scienceSpendingCount + scienceCount + fundsCount + reputationCount + kerbalCount + otherCount;
            int excluded = seedExcluded + rolloutExcluded + otherExcluded;

            ParsekLog.Info(LedgerSwapTag,
                $"Tombstoned {tombstoned} career actions " +
                $"(Contract={contractCount}, Milestone={milestoneCount}, " +
                $"Facility={facilityCount}, Strategy={strategyCount}, ScienceSpending={scienceSpendingCount}, " +
                $"Science={scienceCount}, Funds={fundsCount}, Reputation={reputationCount}, " +
                $"Kerbal={kerbalCount}, Other={otherCount}); " +
                $"{excluded} excluded (Seed={seedExcluded}, Rollout={rolloutExcluded}, Other={otherExcluded})");

            ParsekLog.Info(Tag,
                $"Supersede tombstone effects: tombstoned {tombstoned} recording-scoped career actions; " +
                "contracts/milestones/facilities/strategies/science-spending/science/funds/reputation/kerbals from the old subtree are removed from ELS.");

            scenario.BumpTombstoneStateVersion();

            // Queue tombstoned roster cleanup before the reservation recompute so
            // the next ApplyToRoster pass during RecalculateAndPatchAfterTombstones
            // sees the cleanup work after reservations and ledger-created kerbals
            // have been rebuilt from the post-tombstone ELS.
            if (tombstonedRosterActions.Count > 0)
            {
                if (LedgerOrchestrator.TryEnsureKerbalsModuleForTombstoneRosterCleanup())
                    LedgerOrchestrator.Kerbals.QueueTombstonedRosterKerbals(tombstonedRosterActions);
            }

            // Design §6.6 step 6 / §7.16: reservation walker re-derives so
            // old-subtree crew assignments disappear from reservations.
            CrewReservationManager.RecomputeAfterTombstones();

            RecalculateAfterTombstones(tombstoned, tombstonedScienceSpendingNodeIds);
        }

        private static void RecalculateAfterTombstones(
            int tombstoned,
            IReadOnlyCollection<string> currentTombstonedScienceSpendingNodeIds)
        {
            if (tombstoned <= 0)
                return;

            ParsekLog.Info(Tag,
                $"CommitTombstones: refreshing recalculated KSP state after {tombstoned.ToString(CultureInfo.InvariantCulture)} tombstone(s)");
            LedgerOrchestrator.RecalculateAndPatchAfterTombstones(
                currentTombstonedScienceSpendingNodeIds);
        }

        private static void CountTombstonedByType(
            GameActionType type,
            ref int contract,
            ref int milestone,
            ref int facility,
            ref int strategy,
            ref int scienceSpending,
            ref int science,
            ref int funds,
            ref int reputation,
            ref int kerbal,
            ref int other)
        {
            switch (type)
            {
                case GameActionType.ContractAccept:
                case GameActionType.ContractComplete:
                case GameActionType.ContractFail:
                case GameActionType.ContractCancel:
                    contract++;
                    break;
                case GameActionType.MilestoneAchievement:
                    milestone++;
                    break;
                case GameActionType.FacilityUpgrade:
                case GameActionType.FacilityRepair:
                case GameActionType.FacilityDestruction:
                    facility++;
                    break;
                case GameActionType.StrategyActivate:
                case GameActionType.StrategyDeactivate:
                    strategy++;
                    break;
                case GameActionType.ScienceSpending:
                    scienceSpending++;
                    break;
                case GameActionType.ScienceEarning:
                    science++;
                    break;
                case GameActionType.FundsEarning:
                case GameActionType.FundsSpending:
                    funds++;
                    break;
                case GameActionType.ReputationEarning:
                case GameActionType.ReputationPenalty:
                    reputation++;
                    break;
                case GameActionType.KerbalAssignment:
                case GameActionType.KerbalHire:
                case GameActionType.KerbalRescue:
                case GameActionType.KerbalStandIn:
                    kerbal++;
                    break;
                default:
                    other++;
                    break;
            }
        }

        private static void CountExcludedByType(
            GameAction action,
            ref int seed,
            ref int rollout,
            ref int other)
        {
            if (action == null)
            {
                other++;
                return;
            }

            if (RecalculationEngine.IsSeedType(action.Type))
            {
                seed++;
                return;
            }

            if (action.Type == GameActionType.FundsSpending
                && action.FundsSpendingSource == FundsSpendingSource.VesselBuild)
            {
                rollout++;
                return;
            }

            other++;
        }

        /// <summary>
        /// Design invariant for re-fly merge supersede targets: the recording
        /// pointed at by <see cref="RecordingSupersedeRelation.NewRecordingId"/>
        /// must have playable trajectory payload AND a non-null terminal
        /// state. Returns true iff the target satisfies both clauses;
        /// otherwise <paramref name="reason"/> carries one of "null recording",
        /// "null Points", "empty Points", or "null TerminalState".
        /// </summary>
        internal static bool ValidateSupersedeTarget(Recording rec, out string reason)
        {
            if (rec == null)
            {
                reason = "null recording";
                return false;
            }
            bool hasPlayablePayload = HasPlayableSupersedePayload(rec);
            if (rec.Points == null && !hasPlayablePayload)
            {
                reason = "null Points";
                return false;
            }
            if (!hasPlayablePayload)
            {
                reason = "empty Points";
                return false;
            }
            if (!rec.TerminalStateValue.HasValue)
            {
                reason = "null TerminalState";
                return false;
            }
            reason = null;
            return true;
        }

        private static bool HasPlayableSupersedePayload(Recording rec)
        {
            if (rec == null)
                return false;
            if (rec.Points != null && rec.Points.Count > 0)
                return true;
            if (rec.OrbitSegments != null && rec.OrbitSegments.Count > 0)
                return true;
            if (rec.TrackSections != null)
            {
                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    if (PlaybackTrajectoryBoundsResolver.HasPlayablePayload(rec.TrackSections[i]))
                        return true;
                }
            }

            return false;
        }

        private static bool RelationExists(
            List<RecordingSupersedeRelation> relations, string oldId, string newId)
        {
            if (relations == null || relations.Count == 0) return false;
            for (int i = 0; i < relations.Count; i++)
            {
                var rel = relations[i];
                if (rel == null) continue;
                if (string.Equals(rel.OldRecordingId, oldId, StringComparison.Ordinal)
                    && string.Equals(rel.NewRecordingId, newId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static double SafeNow()
        {
            try
            {
                return Planetarium.GetUniversalTime();
            }
            catch
            {
                // Unit tests outside Unity: Planetarium is unavailable.
                return 0.0;
            }
        }
    }
}
