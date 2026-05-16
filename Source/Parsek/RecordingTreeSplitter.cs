// [ERS-exempt] RecordingTreeSplitter.SplitOriginAtRewindUT is the Re-Fly
// merge-time orchestrator that splits the origin recording at the rewind UT
// into HEAD (kept visible) and TIP (about to be superseded). It must read
// the raw RecordingStore.CommittedRecordings list to resolve origin / chain
// siblings / debris children by id, and the raw Ledger.Actions list to
// UT-partition action RecordingIds across the split. Routing through
// ERS / ELS would hide TIP (which the supersede write is about to introduce)
// and pre-existing NotCommitted provisionals on neighboring slots.
// See scripts/ers-els-audit-allowlist.txt for the allowlist entry.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Re-Fly merge-time orchestrator that splits the origin recording at the
    /// rewind UT into a kept HEAD half (pre-rewind) and a TIP half (post-rewind,
    /// will be superseded). Plan §"Step 2 — `RecordingTreeSplitter.SplitOriginAtRewindUT`
    /// orchestrator". Owns chain id allocation, tree-dict insertion, BP / debris /
    /// TrackSection-anchor / ledger-action / milestone retags, marker mutation, and
    /// stack-local snapshot rollback when any step throws.
    ///
    /// <para>The splitter is internally transactional: every call captures a
    /// pre-mutation deep clone of origin plus a per-sibling ChainIndex map plus an
    /// incremental ledger of single-row retags; on exception the splitter calls
    /// <see cref="RollBackInMemory"/> on its own snapshot and re-throws. Callers
    /// (<c>MergeJournalOrchestrator.RunMerge</c>) only log the re-throw — the
    /// rollback already ran inside this function.</para>
    /// </summary>
    internal static class RecordingTreeSplitter
    {
        private const string Tag = "Splitter";

        /// <summary>
        /// Result of <see cref="SplitOriginAtRewindUT"/>. The boolean fields
        /// communicate whether a split actually happened (vs skipped because
        /// the origin doesn't span the rewind UT). The counters report the
        /// per-step retag count for log/test assertions.
        /// </summary>
        internal struct SplitOriginResult
        {
            internal string HeadRecordingId;
            internal string TipRecordingId;
            internal double SplitUT;
            internal int BpReparented;
            internal int DebrisReparented;
            internal int DebrisAnchorRewrites;
            internal int TipAnchorRewrites;
            internal int ActionsRetagged;
            internal int MilestonesRetagged;
            internal bool Skipped;
            internal string SkipReason;
        }

        /// <summary>
        /// Stack-local rollback bundle assembled by
        /// <see cref="SplitOriginAtRewindUT"/>. NEVER serialized; never assigned
        /// to <see cref="Recording"/>, <see cref="ParsekScenario"/>, or any
        /// persisted type. Codec safety is enforced by scope: this instance lives
        /// only inside the splitter's stack frame and is discarded on return.
        /// </summary>
        internal sealed class SplitSnapshot
        {
            /// <summary>
            /// <see cref="Recording.DeepClone"/> of origin captured BEFORE
            /// <see cref="RecordingOptimizer.SplitAtUT"/> trimmed origin's
            /// payload lists in place. Null until the snapshot has been built.
            /// On rollback, this clone replaces origin's slot in
            /// <c>RecordingStore.CommittedRecordings</c> and the owning tree's
            /// <c>Recordings</c> dictionary — all backrefs are by string id so
            /// object identity changes are invisible to consumers.
            /// </summary>
            internal Recording OriginClone;

            /// <summary>
            /// Map of <c>recordingId → ChainIndex</c> for every recording in the
            /// same <c>TreeId</c> + <c>ChainId</c> as origin, captured BEFORE
            /// <see cref="RecordingOptimizer.ReindexChain"/> ran. Rollback restores
            /// each sibling's ChainIndex from this map.
            /// </summary>
            internal Dictionary<string, int> ChainSiblingsBefore =
                new Dictionary<string, int>(StringComparer.Ordinal);

            /// <summary>
            /// True once TIP has been inserted into
            /// <c>RecordingStore.CommittedRecordings</c> + the owning tree's
            /// <c>Recordings</c> dictionary. Rollback removes TIP from both when
            /// set.
            /// </summary>
            internal bool TipAdded;

            /// <summary>TIP's allocated <see cref="Recording.RecordingId"/>. Captured for rollback removal.</summary>
            internal string TipRecordingId;

            /// <summary>Origin's <see cref="Recording.TreeId"/> — captured up front for rollback's tree lookup.</summary>
            internal string TreeId;

            /// <summary>
            /// Incremental ledger of retag mutations applied AFTER
            /// <see cref="RecordingOptimizer.SplitAtUT"/> (BP reparent, debris
            /// reparent, TrackSection anchor rewrite, ledger-action retag,
            /// milestone retag). Replayed in REVERSE on rollback.
            /// </summary>
            internal List<SplitMutationLedger.Entry> Ledger =
                new List<SplitMutationLedger.Entry>();

            /// <summary>
            /// Marker reference for rollback's <see cref="ReFlySessionMarker.SupersedeTargetId"/>
            /// restoration. Captured up front so rollback can locate the
            /// mutated marker even when called from outside the original
            /// stack frame (tests or future call sites). Null if the
            /// splitter hasn't reached step 2.10 yet.
            /// </summary>
            internal ReFlySessionMarker MarkerForRollback;

            /// <summary>
            /// Pre-mutation value of <see cref="ReFlySessionMarker.SupersedeTargetId"/>,
            /// captured in step 2.10 immediately BEFORE the orchestrator overwrites
            /// it with TIP's id. Pass 2 review Opus-H3 / User-M1: any exception
            /// in steps 2.11-2.13 (state-version bump, invariant logs, summary
            /// log) leaves the marker pointing at TIP's about-to-be-removed id
            /// unless rollback restores it. Without this field rollback removes
            /// TIP from the store/tree but the marker's SupersedeTargetId
            /// remains a dangling id reference.
            ///
            /// Default <c>null</c> means "marker mutation has not happened yet,
            /// no restoration needed."
            /// </summary>
            internal string PreSplitSupersedeTargetId;

            /// <summary>
            /// True once step 2.10's marker.SupersedeTargetId mutation has
            /// been recorded in the snapshot. Distinguishes "marker mutation
            /// not yet attempted" from "marker mutation captured and rolled
            /// back" so rollback can restore <see cref="PreSplitSupersedeTargetId"/>
            /// (which may legitimately be null on a marker with no prior
            /// target).
            /// </summary>
            internal bool SupersedeTargetIdCaptured;
        }

        /// <summary>
        /// Incremental rollback ledger entries written by
        /// <see cref="SplitOriginAtRewindUT"/> at every per-row retag. Each entry
        /// records enough state to reverse the mutation in O(1). Entries are
        /// replayed in reverse order in <see cref="RollBackInMemory"/>.
        /// </summary>
        internal static class SplitMutationLedger
        {
            internal enum Kind
            {
                BpParentReplacement = 0,
                DebrisParentRewrite = 1,
                TrackSectionAnchorRewrite = 2,
                LedgerActionRetag = 3,
                MilestoneRetag = 4,
            }

            /// <summary>
            /// Heterogeneous reversible-mutation record. Field meaning varies by
            /// <see cref="EntryKind"/>:
            /// <list type="bullet">
            /// <item><see cref="Kind.BpParentReplacement"/>: <see cref="Bp"/> + <see cref="IntKey"/>=ParentRecordingIds index + <see cref="OldId"/> / <see cref="NewId"/>.</item>
            /// <item><see cref="Kind.DebrisParentRewrite"/>: <see cref="Recording"/>=debris + <see cref="OldId"/> / <see cref="NewId"/>.</item>
            /// <item><see cref="Kind.TrackSectionAnchorRewrite"/>: <see cref="Recording"/>=owning recording + <see cref="IntKey"/>=section index + <see cref="OldId"/> / <see cref="NewId"/>.</item>
            /// <item><see cref="Kind.LedgerActionRetag"/>: <see cref="Action"/>=GameAction reference + <see cref="OldId"/> / <see cref="NewId"/>.</item>
            /// <item><see cref="Kind.MilestoneRetag"/>: <see cref="Milestone"/>=milestone reference + <see cref="OldId"/> / <see cref="NewId"/>.</item>
            /// </list>
            /// </summary>
            internal struct Entry
            {
                internal Kind EntryKind;
                internal BranchPoint Bp;
                internal Recording Recording;
                internal GameAction Action;
                internal Milestone Milestone;
                internal int IntKey;
                internal string OldId;
                internal string NewId;
            }

            internal static Entry BpParent(BranchPoint bp, int parentIdx, string oldId, string newId)
            {
                return new Entry
                {
                    EntryKind = Kind.BpParentReplacement,
                    Bp = bp,
                    IntKey = parentIdx,
                    OldId = oldId,
                    NewId = newId,
                };
            }

            internal static Entry DebrisParent(Recording debris, string oldId, string newId)
            {
                return new Entry
                {
                    EntryKind = Kind.DebrisParentRewrite,
                    Recording = debris,
                    OldId = oldId,
                    NewId = newId,
                };
            }

            internal static Entry TrackSectionAnchor(Recording rec, int sectionIdx, string oldId, string newId)
            {
                return new Entry
                {
                    EntryKind = Kind.TrackSectionAnchorRewrite,
                    Recording = rec,
                    IntKey = sectionIdx,
                    OldId = oldId,
                    NewId = newId,
                };
            }

            internal static Entry LedgerAction(GameAction action, string oldId, string newId)
            {
                return new Entry
                {
                    EntryKind = Kind.LedgerActionRetag,
                    Action = action,
                    OldId = oldId,
                    NewId = newId,
                };
            }

            internal static Entry MilestoneRetag(Milestone ms, string oldId, string newId)
            {
                return new Entry
                {
                    EntryKind = Kind.MilestoneRetag,
                    Milestone = ms,
                    OldId = oldId,
                    NewId = newId,
                };
            }

            /// <summary>
            /// Applies the inverse mutation for an entry: writes
            /// <see cref="Entry.OldId"/> back onto the recorded target. Safe to
            /// call when the target is already at <see cref="Entry.OldId"/>
            /// (rollback after a partial step).
            /// </summary>
            internal static void Undo(Entry e)
            {
                switch (e.EntryKind)
                {
                    case Kind.BpParentReplacement:
                        if (e.Bp != null
                            && e.Bp.ParentRecordingIds != null
                            && e.IntKey >= 0
                            && e.IntKey < e.Bp.ParentRecordingIds.Count)
                        {
                            e.Bp.ParentRecordingIds[e.IntKey] = e.OldId;
                        }
                        break;
                    case Kind.DebrisParentRewrite:
                        if (e.Recording != null)
                            e.Recording.DebrisParentRecordingId = e.OldId;
                        break;
                    case Kind.TrackSectionAnchorRewrite:
                        if (e.Recording != null
                            && e.Recording.TrackSections != null
                            && e.IntKey >= 0
                            && e.IntKey < e.Recording.TrackSections.Count)
                        {
                            var s = e.Recording.TrackSections[e.IntKey];
                            s.anchorRecordingId = e.OldId;
                            e.Recording.TrackSections[e.IntKey] = s;
                        }
                        break;
                    case Kind.LedgerActionRetag:
                        if (e.Action != null)
                            e.Action.RecordingId = e.OldId;
                        break;
                    case Kind.MilestoneRetag:
                        if (e.Milestone != null)
                            e.Milestone.RecordingId = e.OldId;
                        break;
                }
            }
        }

        /// <summary>
        /// Splits the origin recording named by <paramref name="marker"/>'s
        /// closure root at <c>marker.RewindPointUT</c> when the origin's UT
        /// bounds strictly span the rewind point. See plan §"Step 2" — the
        /// thirteen numbered sub-steps documented at the implementation site.
        ///
        /// <para>On success: HEAD keeps the origin id, TIP gets a fresh id,
        /// <paramref name="marker"/>.<see cref="ReFlySessionMarker.SupersedeTargetId"/>
        /// is mutated to TIP's id, and the closure walk in
        /// <c>AppendRelations</c> starts at TIP. On guard-failure or any
        /// exception: <paramref name="marker"/> is left at its pre-call state
        /// and any partial in-memory mutations are rolled back via
        /// <see cref="RollBackInMemory"/>; the exception is re-thrown to the
        /// caller (<c>MergeJournalOrchestrator.RunMerge</c>).</para>
        /// </summary>
        internal static SplitOriginResult SplitOriginAtRewindUT(
            ReFlySessionMarker marker, ParsekScenario scenario)
        {
            var ic = CultureInfo.InvariantCulture;

            // Step 2.1: pre-check predicates.
            if (marker == null)
            {
                ParsekLog.Warn(Tag, "SplitOriginAtRewindUT: skip — marker is null");
                return new SplitOriginResult { Skipped = true, SkipReason = "MarkerNull" };
            }
            if (double.IsNaN(marker.RewindPointUT) || marker.RewindPointUT <= 0.0)
            {
                ParsekLog.Warn(Tag,
                    "SplitOriginAtRewindUT: skip — marker.RewindPointUT is NaN or non-positive " +
                    $"(value={marker.RewindPointUT.ToString("R", ic)})");
                return new SplitOriginResult { Skipped = true, SkipReason = "RewindPointUTUnset" };
            }
            string closureRootId = !string.IsNullOrEmpty(marker.SupersedeTargetId)
                ? marker.SupersedeTargetId
                : marker.OriginChildRecordingId;
            if (string.IsNullOrEmpty(closureRootId))
            {
                ParsekLog.Warn(Tag,
                    "SplitOriginAtRewindUT: skip — marker has no SupersedeTargetId or OriginChildRecordingId");
                return new SplitOriginResult { Skipped = true, SkipReason = "ClosureRootMissing" };
            }

            // Step 2.2: resolve origin recording.
            Recording origin = FindCommittedRecordingById(closureRootId);
            if (origin == null)
            {
                ParsekLog.Warn(Tag,
                    $"SplitOriginAtRewindUT: skip — closure root '{closureRootId}' " +
                    "not found in RecordingStore.CommittedRecordings");
                return new SplitOriginResult { Skipped = true, SkipReason = "OriginNotFound" };
            }

            double rewindUT = marker.RewindPointUT;
            const double epsilon = EffectiveState.PidPeerStartUtEpsilonSeconds;

            // Step 2.3a: idempotency-after-marker-mutation pass. If the closure
            // root already names a TIP (its StartUT ≈ rewindUT and it has a
            // chain predecessor at ChainIndex-1), this is a partial-resume
            // re-entry. Resolve the chain predecessor as "origin" and re-run
            // steps 6-13 idempotently.
            if (LooksLikeAlreadyMutatedClosureRoot(origin, rewindUT, epsilon))
            {
                Recording predecessor = TryFindChainPredecessor(origin);
                if (predecessor != null)
                {
                    ParsekLog.Info(Tag,
                        $"SplitOriginAtRewindUT: idempotent re-entry — closure root " +
                        $"'{origin.RecordingId}' is already TIP on chain {origin.ChainId} " +
                        $"(predecessor HEAD={predecessor.RecordingId} at chainIndex=" +
                        $"{predecessor.ChainIndex.ToString(ic)}); replaying post-split steps");
                    var snapshotReplay = new SplitSnapshot
                    {
                        TreeId = predecessor.TreeId,
                        TipRecordingId = origin.RecordingId,
                    };
                    try
                    {
                        return RunPostSplitSteps(
                            marker, predecessor, origin, rewindUT, snapshotReplay,
                            addedTipThisCall: false);
                    }
                    catch
                    {
                        RollBackInMemory(scenario, snapshotReplay);
                        throw;
                    }
                }
            }

            // Strict span check: origin must extend on both sides of rewindUT
            // by more than the sampler-jitter tolerance.
            if (!(origin.StartUT < rewindUT - epsilon)
                || !(origin.EndUT > rewindUT + epsilon))
            {
                ParsekLog.Verbose(Tag,
                    $"SplitOriginAtRewindUT: skip — origin '{origin.RecordingId}' UT bounds " +
                    $"[{origin.StartUT.ToString("F2", ic)},{origin.EndUT.ToString("F2", ic)}] " +
                    $"do not strictly span rewindUT={rewindUT.ToString("F2", ic)} " +
                    $"(epsilon={epsilon.ToString("R", ic)})");
                return new SplitOriginResult
                {
                    Skipped = true,
                    SkipReason = "OriginDoesNotSpanRewindUT",
                };
            }

            // Step 2.3b: idempotency-without-marker-mutation pass. If the marker
            // closure root is HEAD and there's already a chain sibling at
            // ChainIndex+1 with StartUT ≈ rewindUT, treat as resumed-after-Step-2.5.
            // All three predicates must match to avoid false-positive on an
            // env-class chain sibling that happens to sit at ChainIndex+1 but
            // with a different StartUT.
            Recording existingTip = TryFindExistingTip(origin, rewindUT, epsilon);
            if (existingTip != null)
            {
                ParsekLog.Info(Tag,
                    $"SplitOriginAtRewindUT: idempotent re-entry — found existing TIP " +
                    $"'{existingTip.RecordingId}' on chain {origin.ChainId} at " +
                    $"chainIndex={existingTip.ChainIndex.ToString(ic)} " +
                    $"startUT={existingTip.StartUT.ToString("F2", ic)} " +
                    $"(rewindUT={rewindUT.ToString("F2", ic)})");
                // Idempotent re-run: snapshot is empty (no rollback needed,
                // nothing new to mutate) and downstream steps 6-13 each are
                // for-each-row-rewrite-once and become no-ops once their
                // predicate no longer matches.
                var snapshot = new SplitSnapshot
                {
                    TreeId = origin.TreeId,
                    TipRecordingId = existingTip.RecordingId,
                };
                try
                {
                    return RunPostSplitSteps(
                        marker, origin, existingTip, rewindUT, snapshot,
                        addedTipThisCall: false);
                }
                catch
                {
                    RollBackInMemory(scenario, snapshot);
                    throw;
                }
            }

            // Build the snapshot first so any exception inside the split is
            // recoverable.
            var freshSnapshot = new SplitSnapshot
            {
                TreeId = origin.TreeId,
            };

            try
            {
                // Step 2.4: perform the split via SplitAtUT. Capture the
                // deep-clone BEFORE the call so the clone reflects pre-split
                // state (SplitAtSection trims original.Points / TrackSections /
                // etc. in place — no incremental ledger can undo that).
                freshSnapshot.OriginClone = Recording.DeepClone(origin);
                CaptureChainSiblingsBefore(origin, freshSnapshot.ChainSiblingsBefore);

                Recording tip = RecordingOptimizer.SplitAtUT(origin, rewindUT);
                if (tip == null)
                {
                    // SplitAtUT's remaining guards (v13 debris contract
                    // minimum bodyFixedFrames sample count, precondition
                    // violations such as origin not strictly spanning
                    // splitUT or NaN splitUT) all return null without
                    // mutating origin. Marker stays as-is so the caller's
                    // AppendRelations writes today's whole-recording
                    // supersede row (the bug remains for this one
                    // recording, but the merge completes cleanly).
                    // Note: straddling OrbitSegments no longer return null;
                    // SplitAtSection tail-clones them into TIP.
                    ParsekLog.Warn(Tag,
                        $"SplitOriginAtRewindUT: SplitAtUT returned null for origin " +
                        $"'{origin.RecordingId}' at rewindUT={rewindUT.ToString("F2", ic)} — " +
                        "falling back to whole-recording supersede; " +
                        "marker.SupersedeTargetId unchanged");
                    return new SplitOriginResult
                    {
                        HeadRecordingId = origin.RecordingId,
                        SplitUT = rewindUT,
                        Skipped = true,
                        SkipReason = "SplitAtUT_Guarded",
                    };
                }

                // Step 2.5: wire identity + tree.
                tip.RecordingId = Guid.NewGuid().ToString("N");
                if (string.IsNullOrEmpty(origin.ChainId))
                    origin.ChainId = Guid.NewGuid().ToString("N");
                tip.ChainId = origin.ChainId;
                tip.ChainBranch = origin.ChainBranch;
                tip.TreeId = origin.TreeId;
                tip.VesselName = origin.VesselName;
                tip.VesselPersistentId = origin.VesselPersistentId;
                tip.PreLaunchFunds = origin.PreLaunchFunds;
                tip.PreLaunchScience = origin.PreLaunchScience;
                tip.PreLaunchReputation = origin.PreLaunchReputation;
                tip.RecordingGroups = origin.RecordingGroups != null
                    ? new List<string>(origin.RecordingGroups) : null;
                tip.CreatingSessionId = origin.CreatingSessionId;
                tip.ProvisionalForRpId = origin.ProvisionalForRpId;
                tip.SupersedeTargetId = origin.SupersedeTargetId;

                // ChildBranchPointId move: reuse the optimizer split's
                // helper. secondStartUT == tip.StartUT (= rewindUT post-split),
                // NOT origin.StartUT.
                string movedChildBranchPointId = null;
                bool moveBp = RecordingStore.ShouldMoveChildBranchPointToSplitSecondHalf(
                    origin.TreeId,
                    origin.ChildBranchPointId,
                    tip.StartUT);
                if (moveBp)
                {
                    movedChildBranchPointId = origin.ChildBranchPointId;
                    tip.ChildBranchPointId = origin.ChildBranchPointId;
                    origin.ChildBranchPointId = null;
                }
                else
                {
                    tip.ChildBranchPointId = null;
                }

                // Rewrite the moved BP's ParentRecordingIds entry from origin
                // to tip (mirrors RunOptimizationSplitPass at RecordingStore:3461-3493).
                RecordingTree owningTree = FindCommittedTreeById(origin.TreeId);
                if (!string.IsNullOrEmpty(movedChildBranchPointId)
                    && owningTree != null
                    && owningTree.BranchPoints != null)
                {
                    for (int b = 0; b < owningTree.BranchPoints.Count; b++)
                    {
                        var bp = owningTree.BranchPoints[b];
                        if (bp == null
                            || !string.Equals(bp.Id, movedChildBranchPointId, StringComparison.Ordinal)
                            || bp.ParentRecordingIds == null)
                        {
                            continue;
                        }
                        for (int p = 0; p < bp.ParentRecordingIds.Count; p++)
                        {
                            if (string.Equals(bp.ParentRecordingIds[p], origin.RecordingId, StringComparison.Ordinal))
                            {
                                freshSnapshot.Ledger.Add(SplitMutationLedger.BpParent(
                                    bp, p, origin.RecordingId, tip.RecordingId));
                                bp.ParentRecordingIds[p] = tip.RecordingId;
                                ParsekLog.Verbose(Tag,
                                    $"Step5: moved BranchPoint '{movedChildBranchPointId}' " +
                                    $"ParentRecordingIds[{p}] {origin.RecordingId} → {tip.RecordingId}");
                                break;
                            }
                        }
                        break;
                    }
                }

                // Insert TIP into the committed list immediately after origin.
                RecordingStore.InsertCommittedAfter(origin.RecordingId, tip);
                if (owningTree != null)
                    owningTree.AddOrReplaceRecording(tip);
                freshSnapshot.TipAdded = true;
                freshSnapshot.TipRecordingId = tip.RecordingId;

                origin.FilesDirty = true;
                tip.FilesDirty = true;

                RecordingOptimizer.ReindexChain(
                    GetMutableCommittedRecordingsForReindex(),
                    origin.ChainId);

                // Pass 2 review User-H3: BackgroundMap eligibility depends on
                // TerminalStateValue / HasNextChainSegment / ChildBranchPointId
                // — all of which the splitter mutates on HEAD (HEAD loses terminal
                // state, gains TIP as chain sibling, may have its
                // ChildBranchPointId nulled). RunOptimizationSplitPass calls
                // RebuildBackgroundMap after its analogous insert + reindex
                // (RecordingStore.cs:3362-3369); mirror it here so the splitter's
                // map state matches the optimizer-split contract. Without this
                // call, a future change to RebuildBackgroundMap's PID-collision
                // tie-break could silently produce divergent map state between
                // the two code paths.
                owningTree?.RebuildBackgroundMap();

                // Steps 2.6 - 2.13 are encapsulated so the idempotent re-run path
                // can share the same code.
                var result = RunPostSplitSteps(
                    marker, origin, tip, rewindUT, freshSnapshot, addedTipThisCall: true);
                return result;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag,
                    $"SplitOriginAtRewindUT: caught {ex.GetType().Name} during split of " +
                    $"origin '{origin.RecordingId}' at rewindUT={rewindUT.ToString("F2", ic)}: " +
                    $"{ex.Message} — rolling back in memory and re-throwing");
                RollBackInMemory(scenario, freshSnapshot);
                throw;
            }
        }

        /// <summary>
        /// Steps 2.6 – 2.13 of the orchestrator. Shared between the
        /// freshly-split path and the idempotent re-entry path. Mutations
        /// recorded into <paramref name="snapshot"/>.Ledger for rollback.
        /// </summary>
        private static SplitOriginResult RunPostSplitSteps(
            ReFlySessionMarker marker,
            Recording origin,
            Recording tip,
            double rewindUT,
            SplitSnapshot snapshot,
            bool addedTipThisCall)
        {
            var ic = CultureInfo.InvariantCulture;
            var result = new SplitOriginResult
            {
                HeadRecordingId = origin.RecordingId,
                TipRecordingId = tip.RecordingId,
                SplitUT = rewindUT,
                Skipped = false,
            };

            RecordingTree tree = FindCommittedTreeById(origin.TreeId);

            // Step 2.6: BranchPoint reparent walk. Edge case: BP.UT == rewindUT
            // reparents to TIP (BP belongs to TIP's lifetime).
            if (tree != null && tree.BranchPoints != null)
            {
                for (int b = 0; b < tree.BranchPoints.Count; b++)
                {
                    var bp = tree.BranchPoints[b];
                    if (bp == null || bp.ParentRecordingIds == null) continue;
                    if (!(bp.UT >= rewindUT)) continue;
                    for (int p = 0; p < bp.ParentRecordingIds.Count; p++)
                    {
                        if (string.Equals(
                                bp.ParentRecordingIds[p],
                                origin.RecordingId,
                                StringComparison.Ordinal))
                        {
                            snapshot.Ledger.Add(SplitMutationLedger.BpParent(
                                bp, p, origin.RecordingId, tip.RecordingId));
                            bp.ParentRecordingIds[p] = tip.RecordingId;
                            result.BpReparented++;
                        }
                    }
                }
            }
            ParsekLog.Verbose(Tag,
                $"Step6: BP reparent walk complete — bpReparented={result.BpReparented.ToString(ic)} " +
                $"(rewindUT={rewindUT.ToString("F2", ic)} origin={origin.RecordingId} tip={tip.RecordingId})");

            // Step 2.7: debris reparent walk. Build the reparented-debris id
            // list so Step 2.8 can revisit them for anchor rewrites.
            var reparentedDebrisIds = new List<string>();
            var debrisSource = RecordingStore.CommittedRecordings;
            if (debrisSource != null)
            {
                for (int i = 0; i < debrisSource.Count; i++)
                {
                    var d = debrisSource[i];
                    if (d == null) continue;
                    if (!string.Equals(
                            d.DebrisParentRecordingId,
                            origin.RecordingId,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (!(d.StartUT >= rewindUT)) continue;
                    snapshot.Ledger.Add(SplitMutationLedger.DebrisParent(
                        d, origin.RecordingId, tip.RecordingId));
                    d.DebrisParentRecordingId = tip.RecordingId;
                    reparentedDebrisIds.Add(d.RecordingId);
                    result.DebrisReparented++;
                }
            }
            ParsekLog.Verbose(Tag,
                $"Step7: debris reparent walk complete — debrisReparented={result.DebrisReparented.ToString(ic)}");

            // Step 2.8: TrackSection anchor rewrites.
            // TIP-side first.
            if (tip.TrackSections != null)
            {
                for (int i = 0; i < tip.TrackSections.Count; i++)
                {
                    var s = tip.TrackSections[i];
                    if (s.referenceFrame == ReferenceFrame.Relative
                        && !string.IsNullOrEmpty(s.anchorRecordingId)
                        && string.Equals(s.anchorRecordingId, origin.RecordingId, StringComparison.Ordinal))
                    {
                        snapshot.Ledger.Add(SplitMutationLedger.TrackSectionAnchor(
                            tip, i, s.anchorRecordingId, tip.RecordingId));
                        s.anchorRecordingId = tip.RecordingId;
                        tip.TrackSections[i] = s;
                        result.TipAnchorRewrites++;
                    }
                }
            }
            // Reparented-debris side: only sections whose startUT is post-rewind.
            for (int r = 0; r < reparentedDebrisIds.Count; r++)
            {
                Recording d = FindCommittedRecordingById(reparentedDebrisIds[r]);
                if (d == null || d.TrackSections == null) continue;
                for (int i = 0; i < d.TrackSections.Count; i++)
                {
                    var s = d.TrackSections[i];
                    if (s.referenceFrame == ReferenceFrame.Relative
                        && !string.IsNullOrEmpty(s.anchorRecordingId)
                        && string.Equals(s.anchorRecordingId, origin.RecordingId, StringComparison.Ordinal)
                        && s.startUT >= rewindUT)
                    {
                        snapshot.Ledger.Add(SplitMutationLedger.TrackSectionAnchor(
                            d, i, s.anchorRecordingId, tip.RecordingId));
                        s.anchorRecordingId = tip.RecordingId;
                        d.TrackSections[i] = s;
                        result.DebrisAnchorRewrites++;
                    }
                }
            }
            ParsekLog.Verbose(Tag,
                $"Step8: anchor rewrites — tipSections={result.TipAnchorRewrites.ToString(ic)} " +
                $"debrisSections={result.DebrisAnchorRewrites.ToString(ic)}");

            // Step 2.9: ledger action retag. Walk Ledger.Actions; rewrite
            // RecordingId on every action tagged to origin whose UT >= rewindUT.
            var actions = Ledger.Actions;
            if (actions != null)
            {
                for (int i = 0; i < actions.Count; i++)
                {
                    var a = actions[i];
                    if (a == null) continue;
                    if (!string.Equals(a.RecordingId, origin.RecordingId, StringComparison.Ordinal)) continue;
                    if (!(a.UT >= rewindUT)) continue;
                    snapshot.Ledger.Add(SplitMutationLedger.LedgerAction(
                        a, origin.RecordingId, tip.RecordingId));
                    a.RecordingId = tip.RecordingId;
                    result.ActionsRetagged++;
                }
                if (result.ActionsRetagged > 0)
                    Ledger.BumpStateVersion();
            }
            ParsekLog.Verbose(Tag,
                $"Step9: ledger action retag — actionsRetagged={result.ActionsRetagged.ToString(ic)} " +
                $"ledgerStateVersion={Ledger.StateVersion.ToString(ic)}");

            // Step 2.9b: milestone retag. Predicate: StartUT >= rewindUT - epsilon.
            // StartUT is the start of the milestone's events-window (not the
            // creation UT); milestones whose entire window lies post-rewind
            // retag to TIP. Straddling milestones (StartUT < rewindUT < EndUT)
            // stay on HEAD as an acceptable edge.
            const double msEpsilon = EffectiveState.PidPeerStartUtEpsilonSeconds;
            double msCutoff = rewindUT - msEpsilon;
            var milestones = MilestoneStore.Milestones;
            if (milestones != null)
            {
                for (int i = 0; i < milestones.Count; i++)
                {
                    var ms = milestones[i];
                    if (ms == null) continue;
                    if (!string.Equals(ms.RecordingId, origin.RecordingId, StringComparison.Ordinal)) continue;
                    if (!(ms.StartUT >= msCutoff)) continue;
                    snapshot.Ledger.Add(SplitMutationLedger.MilestoneRetag(
                        ms, origin.RecordingId, tip.RecordingId));
                    ms.RecordingId = tip.RecordingId;
                    result.MilestonesRetagged++;
                }
            }
            if (result.MilestonesRetagged > 0)
            {
                LedgerOrchestrator.OnTimelineDataChanged?.Invoke();
            }
            ParsekLog.Verbose(Tag,
                $"Step9b: milestone retag — milestonesRetagged={result.MilestonesRetagged.ToString(ic)} " +
                $"milestoneStateChanged={(result.MilestonesRetagged > 0 ? "true" : "false")}");

            // Step 2.10: mutate marker. After this point AppendRelations'
            // closure walk starts at TIP; HEAD is reached only via chain-sibling
            // enqueue and is filtered by IsPreRewindCarveOut.
            //
            // Pass 2 review Opus-H3 / User-M1: capture the pre-mutation value
            // (and the marker reference) BEFORE the overwrite so RollBackInMemory
            // can restore SupersedeTargetId on any exception in steps 2.11-2.13.
            // Without this, a partial-failure rollback removes TIP from the
            // store but leaves the marker pointing at TIP's removed id —
            // dangling reference picked up by LoadTimeSweep as an orphan.
            snapshot.MarkerForRollback = marker;
            snapshot.PreSplitSupersedeTargetId = marker.SupersedeTargetId;
            snapshot.SupersedeTargetIdCaptured = true;
            marker.SupersedeTargetId = tip.RecordingId;

            // Step 2.11: bump RecordingStore.StateVersion to invalidate the ERS
            // cache. (InsertCommittedAfter / AddOrReplaceRecording already bump
            // for the freshly-added path; the explicit bump here keeps the
            // idempotent re-entry path correct too.)
            RecordingStore.BumpStateVersion();

            // Step 2.12: invariant logs — tree root / active recording id
            // should still match origin's id (HEAD preserved the id by design).
            if (tree != null)
            {
                if (string.Equals(tree.RootRecordingId, origin.RecordingId, StringComparison.Ordinal))
                {
                    ParsekLog.Verbose(Tag,
                        $"Step12: tree.RootRecordingId references HEAD ({origin.RecordingId}) — " +
                        "id preservation kept the tree root pointer valid");
                }
                if (string.Equals(tree.ActiveRecordingId, origin.RecordingId, StringComparison.Ordinal))
                {
                    ParsekLog.Verbose(Tag,
                        $"Step12: tree.ActiveRecordingId references HEAD ({origin.RecordingId}) — " +
                        "id preservation kept the active recording pointer valid");
                }
            }

            // Step 2.13: success summary log.
            ParsekLog.Info(Tag,
                $"Split origin {origin.RecordingId} at UT={rewindUT.ToString("F2", ic)}: " +
                $"HEAD=[{origin.StartUT.ToString("F2", ic)}..{origin.EndUT.ToString("F2", ic)}] " +
                $"(kept, id unchanged), " +
                $"TIP={tip.RecordingId} [{tip.StartUT.ToString("F2", ic)}..{tip.EndUT.ToString("F2", ic)}] " +
                $"(will be superseded). " +
                $"bpReparented={result.BpReparented.ToString(ic)} " +
                $"debrisReparented={result.DebrisReparented.ToString(ic)} " +
                $"debrisAnchorRewrites={result.DebrisAnchorRewrites.ToString(ic)} " +
                $"tipAnchorRewrites={result.TipAnchorRewrites.ToString(ic)} " +
                $"actionsRetagged={result.ActionsRetagged.ToString(ic)} " +
                $"milestonesRetagged={result.MilestonesRetagged.ToString(ic)} " +
                $"ledgerVersion={Ledger.StateVersion.ToString(ic)} " +
                $"milestoneStateChanged={(result.MilestonesRetagged > 0 ? "true" : "false")} " +
                $"addedTipThisCall={(addedTipThisCall ? "true" : "false")}");

            return result;
        }

        /// <summary>
        /// Reverses every in-memory mutation recorded into
        /// <paramref name="snapshot"/>, in reverse order of application. Plan
        /// §"Rollback approach" — replays the incremental ledger, swaps
        /// origin's reference back to the pre-split deep clone, removes TIP
        /// from the committed list + tree dict, restores ChainIndex map, and
        /// bumps cache version counters.
        ///
        /// <para><b>Internal visibility is for tests only.</b> In production this
        /// is invoked exclusively from <see cref="SplitOriginAtRewindUT"/>'s
        /// catch block; callers MUST NOT invoke it directly because the snapshot
        /// is a stack-local data structure that goes out of scope when the
        /// splitter returns.</para>
        /// </summary>
        internal static void RollBackInMemory(ParsekScenario scenario, SplitSnapshot snapshot)
        {
            if (snapshot == null) return;
            var ic = CultureInfo.InvariantCulture;

            int tipsRemoved = 0;
            // Step 1: remove TIP from CommittedRecordings + tree.Recordings.
            if (snapshot.TipAdded && !string.IsNullOrEmpty(snapshot.TipRecordingId))
            {
                if (RecordingStore.RemoveCommittedById(snapshot.TipRecordingId))
                    tipsRemoved++;
                RecordingTree tree = FindCommittedTreeById(snapshot.TreeId);
                if (tree != null && tree.Recordings != null)
                    tree.Recordings.Remove(snapshot.TipRecordingId);
            }

            // Step 2: swap origin reference back to the pre-split deep clone.
            if (snapshot.OriginClone != null)
            {
                RecordingStore.ReplaceCommittedReference(snapshot.OriginClone);
                RecordingTree tree = FindCommittedTreeById(snapshot.TreeId);
                if (tree != null && tree.Recordings != null)
                {
                    // Replace dict entry — string id key, new value reference.
                    tree.Recordings[snapshot.OriginClone.RecordingId] = snapshot.OriginClone;
                }
            }

            // Step 2b: restore marker.SupersedeTargetId. Pass 2 review Opus-H3
            // / User-M1: step 2.10 may have overwritten this with TIP's id;
            // TIP has just been removed from the store/tree, so leaving the
            // marker pointing at it would create a dangling id that
            // LoadTimeSweep would flag as an orphan on next load.
            bool restoredMarker = false;
            string preMarkerTargetForLog = null;
            if (snapshot.SupersedeTargetIdCaptured && snapshot.MarkerForRollback != null)
            {
                preMarkerTargetForLog = snapshot.MarkerForRollback.SupersedeTargetId;
                snapshot.MarkerForRollback.SupersedeTargetId = snapshot.PreSplitSupersedeTargetId;
                restoredMarker = true;
            }

            // Step 2c: rebuild BackgroundMap for the owning tree. The splitter
            // populated the map post-insert (User-H3); the rollback removed TIP,
            // so the map must be rebuilt to drop the stale entry. Mirror the
            // optimizer-split-pass pattern (RecordingStore.cs:3362-3369).
            RecordingTree owningTreeAfterRollback = FindCommittedTreeById(snapshot.TreeId);
            owningTreeAfterRollback?.RebuildBackgroundMap();

            // Step 3: restore ChainIndex map. Each sibling looked up by id;
            // ChainIndex written back. After origin reference swap the lookup
            // resolves to the clone, which is fine because the clone shares the
            // sibling id.
            int chainSiblingsRestored = 0;
            if (snapshot.ChainSiblingsBefore != null)
            {
                foreach (var kvp in snapshot.ChainSiblingsBefore)
                {
                    Recording rec = FindCommittedRecordingById(kvp.Key);
                    if (rec == null) continue;
                    rec.ChainIndex = kvp.Value;
                    chainSiblingsRestored++;
                }
            }

            // Step 4: walk the ledger in reverse and undo every recorded mutation.
            int ledgerEntries = snapshot.Ledger != null ? snapshot.Ledger.Count : 0;
            if (snapshot.Ledger != null)
            {
                for (int i = snapshot.Ledger.Count - 1; i >= 0; i--)
                {
                    SplitMutationLedger.Undo(snapshot.Ledger[i]);
                }
            }

            // Step 5: bump state version counters AGAIN. Observers that read the
            // half-mutated state see the post-rollback bump and invalidate their
            // caches; decrementing is not safe because cached deltas may have
            // already propagated. Bump Ledger + invoke OnTimelineDataChanged so
            // ELS and milestone overlays rebuild.
            RecordingStore.BumpStateVersion();
            Ledger.BumpStateVersion();
            LedgerOrchestrator.OnTimelineDataChanged?.Invoke();

            ParsekLog.Warn(Tag,
                $"RollBackInMemory: rolled back split " +
                $"(tipsRemoved={tipsRemoved.ToString(ic)} " +
                $"chainSiblings={chainSiblingsRestored.ToString(ic)} " +
                $"ledgerEntries={ledgerEntries.ToString(ic)} " +
                $"markerRestored={(restoredMarker ? "true" : "false")} " +
                $"markerWas={preMarkerTargetForLog ?? "<none>"} " +
                $"markerRestoredTo={(restoredMarker ? (snapshot.PreSplitSupersedeTargetId ?? "<null>") : "<not-touched>")} " +
                $"backgroundMapRebuilt={(owningTreeAfterRollback != null ? "true" : "false")})");
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private static Recording FindCommittedRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            var source = RecordingStore.CommittedRecordings;
            if (source == null) return null;
            for (int i = 0; i < source.Count; i++)
            {
                var rec = source[i];
                if (rec == null) continue;
                if (string.Equals(rec.RecordingId, recordingId, StringComparison.Ordinal))
                    return rec;
            }
            return null;
        }

        private static RecordingTree FindCommittedTreeById(string treeId)
        {
            if (string.IsNullOrEmpty(treeId)) return null;
            var trees = RecordingStore.CommittedTrees;
            if (trees == null) return null;
            for (int i = 0; i < trees.Count; i++)
            {
                var t = trees[i];
                if (t == null) continue;
                if (string.Equals(t.Id, treeId, StringComparison.Ordinal))
                    return t;
            }
            return null;
        }

        /// <summary>
        /// Returns true when <paramref name="closureRoot"/> looks like a TIP
        /// produced by a previous, partially-completed split call: it has a
        /// non-empty ChainId, ChainIndex &gt;= 1, and StartUT within epsilon of
        /// rewindUT. The chain-predecessor lookup is the
        /// <see cref="TryFindChainPredecessor"/> companion.
        /// </summary>
        private static bool LooksLikeAlreadyMutatedClosureRoot(
            Recording closureRoot, double rewindUT, double epsilon)
        {
            if (closureRoot == null) return false;
            if (string.IsNullOrEmpty(closureRoot.ChainId)) return false;
            if (closureRoot.ChainIndex < 1) return false;
            return Math.Abs(closureRoot.StartUT - rewindUT) < epsilon;
        }

        /// <summary>
        /// Finds the chain predecessor (ChainIndex = current.ChainIndex - 1)
        /// for <paramref name="current"/> on the same TreeId+ChainId+ChainBranch.
        /// Returns null if no predecessor exists (current is the head of its chain).
        /// </summary>
        private static Recording TryFindChainPredecessor(Recording current)
        {
            if (current == null || string.IsNullOrEmpty(current.ChainId)) return null;
            int expectedIndex = current.ChainIndex - 1;
            if (expectedIndex < 0) return null;
            var source = RecordingStore.CommittedRecordings;
            if (source == null) return null;
            for (int i = 0; i < source.Count; i++)
            {
                var rec = source[i];
                if (rec == null) continue;
                if (!string.Equals(rec.ChainId, current.ChainId, StringComparison.Ordinal)) continue;
                if (rec.ChainBranch != current.ChainBranch) continue;
                if (rec.ChainIndex != expectedIndex) continue;
                return rec;
            }
            return null;
        }

        /// <summary>
        /// Idempotency probe — looks for an existing recording on origin's chain
        /// at the rewind UT. All three predicates must match: same ChainId,
        /// ChainIndex == origin.ChainIndex + 1, |StartUT - rewindUT| &lt;
        /// epsilon. Without (c) a pre-existing env-class chain sibling at
        /// ChainIndex+1 would false-positive (it sits at the env-transition UT,
        /// not the rewind UT). Returns null when no match.
        /// </summary>
        private static Recording TryFindExistingTip(Recording origin, double rewindUT, double epsilon)
        {
            if (origin == null || string.IsNullOrEmpty(origin.ChainId)) return null;
            var source = RecordingStore.CommittedRecordings;
            if (source == null) return null;
            int expectedIndex = origin.ChainIndex + 1;
            for (int i = 0; i < source.Count; i++)
            {
                var rec = source[i];
                if (rec == null) continue;
                if (!string.Equals(rec.ChainId, origin.ChainId, StringComparison.Ordinal)) continue;
                if (rec.ChainBranch != origin.ChainBranch) continue;
                if (rec.ChainIndex != expectedIndex) continue;
                if (Math.Abs(rec.StartUT - rewindUT) >= epsilon) continue;
                return rec;
            }
            return null;
        }

        /// <summary>
        /// Captures origin's chain siblings' ChainIndex into <paramref name="destination"/>
        /// BEFORE <see cref="RecordingOptimizer.ReindexChain"/> reshuffles them.
        /// Restores correctly on rollback after a partial split.
        /// </summary>
        private static void CaptureChainSiblingsBefore(
            Recording origin, Dictionary<string, int> destination)
        {
            destination.Clear();
            if (origin == null) return;
            var source = RecordingStore.CommittedRecordings;
            if (source == null) return;
            string treeId = origin.TreeId;
            string chainId = origin.ChainId;
            for (int i = 0; i < source.Count; i++)
            {
                var rec = source[i];
                if (rec == null) continue;
                if (!string.IsNullOrEmpty(treeId)
                    && !string.Equals(rec.TreeId, treeId, StringComparison.Ordinal))
                    continue;
                if (!string.IsNullOrEmpty(chainId)
                    && !string.Equals(rec.ChainId, chainId, StringComparison.Ordinal))
                    continue;
                if (string.IsNullOrEmpty(treeId) && string.IsNullOrEmpty(chainId))
                {
                    // Origin had no tree+chain assignment; only origin itself
                    // is in scope. Skip every other recording.
                    if (!string.Equals(rec.RecordingId, origin.RecordingId, StringComparison.Ordinal))
                        continue;
                }
                destination[rec.RecordingId] = rec.ChainIndex;
            }
        }

        /// <summary>
        /// Returns a mutable <see cref="List{Recording}"/> copy of the
        /// committed recordings list for
        /// <see cref="RecordingOptimizer.ReindexChain"/>. The reindex helper
        /// mutates each member's <c>ChainIndex</c> in place — the wrapper list
        /// is discarded after the call. We materialize a fresh list rather
        /// than exposing the private backing store to keep
        /// <c>RecordingStore</c>'s encapsulation intact.
        /// </summary>
        private static List<Recording> GetMutableCommittedRecordingsForReindex()
        {
            var source = RecordingStore.CommittedRecordings;
            if (source == null) return new List<Recording>();
            var copy = new List<Recording>(source.Count);
            for (int i = 0; i < source.Count; i++)
                copy.Add(source[i]);
            return copy;
        }
    }
}
