# Implementation Plan: Split-At-Rewind-UT for Identity-Correct Re-Fly Supersede

## Revision history

- **r1**: write-set-filter-only approach (`SupersedeCommit.IsPreRewindCarveOut` extended to non-debris closure root). Rejected after review found it broke kerbal-permanent-death tombstoning, RP reap, Unfinished-Flights, nested Re-Fly, and tracking-station ghost suppression.
- **r2**: split-the-origin approach (this plan, first form). Reviewed and found to have 5 wrong/structural issues plus 8 incomplete cases.
- **r3 (current)**: fixes addressing the r2 review:
  - **W1** carve-out math: epsilon was subtracted on both predicates; HEAD's `EndUT == rewindUT` failed the test and would have shipped the original bug. Fix uses `rewindUT + epsilon` for the chain-head case, in a separate cutoff inline (not via `ComputePreRewindCutoff`).
  - **W2/W5** journal rollback ordering: Split is now a post-durable phase with its own `DurableSave("split")` barrier between Begin and Supersede. Pre-Begin-durable rollback only; Split-and-after drive forward via idempotent re-run. Wider classification change: Supersede/Tombstone/Finalize move from pre-Durable1 to post-Begin-durable, requiring an audit that those phases' in-RunMerge steps are already idempotent (they are; AppendRelations skips existing rows, FlipMergeState rewrites the same fields, Tombstone deduplicates).
  - **W3** orbit-segment straddle guard added to `SplitAtUT`: returns null when a segment crosses splitUT, the caller falls back to today's whole-recording supersede (the bug remains for that one recording but the merge completes cleanly).
  - **W4** `ChildBranchPointId` rule corrected: reuse `ShouldMoveChildBranchPointToSplitSecondHalf` from `RecordingStore.cs:3543`. HEAD keeps the BP if its UT is pre-rewind; moves to TIP otherwise.
  - **I1** anchor sweep extended to walk reparented debris's TrackSections, not just TIP's.
  - **I2** milestone retag added as Step 9b.
  - **I3** TrackSection metadata for the synthetic boundary spelled out (boundaryDiscontinuityMeters=0 on tail, isBoundarySeam=false on both halves, environment / referenceFrame / sampleRateHz / source / anchorRecordingId / min/maxAltitude all copied).
  - **I4** v13 debris frame contract guard added: split fails (and falls back) if either half ends up with fewer than 2 `bodyFixedFrames` samples.
  - **I6** `InvokeRPStripAndActivateTest.cs:213` test rebase explicitly called out under §7c.
  - **I7** tree.RootRecordingId / tree.ActiveRecordingId verbose log invariant added as Step 2.12.
  - **I8** EVA recording test fixture added as case 10 in §7a.
  - **C1** corrected: fork1.ChainId is `null` at creation (RewindInvoker.BuildProvisionalRecording doesn't set it), not "new". The new ChainId is allocated when fork1 becomes the next split's HEAD2.
  - **C3** RunFinisher dispatch made explicit: `phase == Begin → RollBack; otherwise CompleteFromPostDurable`. CompleteFromPostDurable updated to handle phase=Split as the entry point for forward-completion.
- **r4 (current)**: fixes addressing the r3 review:
  - **W4 prose** corrected: the parenthetical "(which after split equals tip.StartUT)" wrongly conflated `origin.StartUT` (recording's original launch UT, e.g., 8.42) with `tip.StartUT` (= rewindUT, e.g., 34.24). The `ShouldMoveChildBranchPointToSplitSecondHalf` helper takes `secondStartUT` = TIP's StartUT, not origin's. Plan now states this explicitly.
  - **Milestone retag predicate** corrected: was `EndUT >= rewindUT` (incorrectly retags pre-rewind achievements whose enclosing recording ends post-rewind), now `StartUT >= rewindUT - epsilon`. Step 9b also clarifies that `Milestone.RecordingId` is read only by `StockUiOverlayController` and indirectly by `GameStateEventConverter`; the retag is a UI-consistency pass and is NOT load-bearing for tombstone correctness — drop Step 9b if it ever turns out to misfire.
  - **Idempotency check tightened**: was `ChainId match + ChainIndex == origin.ChainIndex + 1`, which would false-positive on existing env-class chain siblings. Now also requires `Math.Abs(StartUT - rewindUT) < epsilon` so only the split-produced TIP matches.
  - **RunFinisher dispatch defensive arm**: unknown phase strings now log a Warn and roll back, not blindly forward-complete. Plus a try/catch around `CompleteFromPostDurable` so a drive-forward failure surfaces a loud diagnostic naming the save state instead of leaving the merge in an undefined half-finished state.
  - **SplitAtUT mutation ordering**: Step 4 explicitly says the synthetic-boundary clone + v13 minimum-sample check run against local-variable lists before writing back to `original.TrackSections`. A guarded return leaves the input recording byte-identical.
  - **W3 fallback scope out-of-scope note** added: the orbit-segment guard only checks the closure root; debris with straddling orbit segments continues to lose its post-rewind portion. Documented as narrow / low-impact follow-up.
  - **In-memory rollback ledger** replaces the earlier `_splitInProgressMarker` field idea. The transient `SplitMutationLedger` lives only inside `SplitOriginAtRewindUT`'s stack and is never serialized — codec exclusion is enforced by scope, not by attribute.
  - **Composite walker cross-edge cycle test** added as §7b case 6: chain hop + supersede hop forming a cross-edge cycle is rejected by the single shared visited set.
  - **`FindRecordingById` prose** corrected: it does not exist in `EffectiveState` today (verified via grep); the implementation must add it as a private static helper.
  - **`MilestoneStore.Milestones` API** corrected from `scenario.MilestoneStore.Milestones` (instance, wrong) to `MilestoneStore.Milestones` (static, correct).
  - **`BranchPoint.ChildRecordingIds` audit note** added in Step 6: the field references divergent debris/fork recording ids which are identity-stable across the split, so no rewriting needed.
- **r5 (current)**: fixes addressing the r4 review:
  - **Rollback approach overhaul** — replaced "incremental ledger only" with "deep-clone snapshot + ChainIndex map + incremental ledger". The earlier ledger-only approach couldn't undo `SplitAtSection`'s wholesale in-place trimming of `original.{Points, PartEvents, SegmentEvents, FlagEvents, TrackSections, OrbitSegments, terminal/end-resources/inventory/crew, VesselSnapshot}` or `ReindexChain`'s global ChainIndex re-sort. The new `SplitSnapshot { originClone, chainSiblingsBefore, tipAdded, ledger }` carries a deep clone of origin captured BEFORE `SplitAtUT` runs, plus a per-sibling ChainIndex snapshot for the chain, and rollback restores by field-copyback. The incremental ledger remains for the targeted retag steps (BP/debris/action/milestone/anchor). `StateVersion` is bumped twice on rollback (once at mutation, once at rollback) to invalidate any observer that read the in-between state.
  - **Milestone invalidation hook** named explicitly as `LedgerOrchestrator.OnTimelineDataChanged?.Invoke()` (no dedicated `MilestoneStore.Version` field exists). `StockUiOverlayController.cs:90` subscribes and rebuilds.
  - **Milestone `StartUT` prose** corrected: `StartUT` is the start of the milestone's *events-window* (= previous milestone's EndUT, or 0), not the milestone-creation UT. The predicate using `StartUT >= rewindUT - eps` cleanly separates post-rewind milestone windows from pre-rewind ones; the straddling case stays on HEAD as an acceptable edge.
  - **User-impact note** added to the RunFinisher try/catch: drive-forward failures during scenario load surface a `[Parsek][WARN]` with manual-recovery instructions (clear `ActiveMergeJournal` and `ActiveReFlySessionMarker` in the save). Parsek is unusable until then. Loud failure is the right trade-off vs silent corruption.
  - **Existing unknown-phase rollback** at `MergeJournalOrchestrator.cs:321-326` explicitly acknowledged — the new defensive arm preserves that behavior, doesn't replace it.
- **r6 (current)**: fixes addressing the r5 review:
  - **`Ledger.StateVersion` / `Ledger.BumpStateVersion()`** API names corrected throughout (was incorrectly `scenario.Ledger.Version`). `Ledger` is a static class; `BumpStateVersion` is the public bump method.
  - **`CompleteFromPostDurable` extension sketched out** — Step 5 now contains the structural extension for each new entry point (Split / Supersede / Tombstone / Finalize), with helper notes for `ResolveProvisional` and `RebuildSubtree`. The r5 review correctly noted the single-sentence "fall through to the existing post-Durable1 sequence" hid the new branches; r6 spells them out.
  - **`docs/parsek-rewind-to-separation-design.md` added to Step 8** — the existing phase-classification table at lines ~682-694 must be updated to reflect the reclassification of Supersede/Tombstone/Finalize from Rollback to forward-completion, plus the new Split phase row.
  - **`Recording.DeepClone()` hedge dropped** — it exists at `Recording.cs:734-853` and is complete. Plan cites it directly.
  - **Reference-swap rollback chosen over field-copyback** — simpler, equally safe because all backrefs to origin are by string id. The plan now spells out the swap mechanics in `RecordingStore.CommittedRecordings` and `tree.Recordings`.
  - **`LookupSupersedeNext` clarified as pseudocode** — implementation extracts the inner supersede-hop loop from `EffectiveRecordingId` (`EffectiveState.cs:108-118`).
- **r7 (current)**: fixes addressing the r6 review:
  - **Real bug in `CompleteFromPostDurable` dispatch corrected.** The existing `Durable1Done` block at `MergeJournalOrchestrator.cs:409` is keyed on `fromPhase ==`; the new entries at Split/Supersede/Tombstone/Finalize advance `journal.Phase` through to `Durable1Done` mid-function. Without changing the line-409 check to `journal.Phase ==`, the tail completion (RpReap, MarkerCleared, Durable2Done) silently skips for any non-Durable1Done entry. The plan now flags this one-line fix explicitly.
  - **`RollBackInMemory` ownership clarified.** Earlier drafts inconsistently called `RollBackInMemory(scenario)` from RunMerge's catch as if the stack-local snapshot were reachable across the throw boundary. The split is now internally transactional: `SplitOriginAtRewindUT` owns the snapshot, uses an internal try/catch to call `RollBackInMemory(scenario, snapshot)` before re-throwing. The outer catch in `RunMerge` only logs.
  - **`journal.RecoveredSubtreeIds` field convention** flagged with required XML doc note: "DO NOT add to SaveInto/LoadFrom; transient cross-block thread inside CompleteFromPostDurable only." Every other `MergeJournal` field is persisted, so the break must be visible to future maintainers. Type is `IReadOnlyCollection<string>` to match `AppendRelations` return.
  - **`AppendRelations` overload** clarification: the three-arg form delegates to the four-arg with `extraSelfSkipRecordingIds: null`. Either is correct.

## Summary

`RecordingSupersedeRelation` is whole-recording. When a Re-Fly's origin recording spans the rewind point UT (one row covers both pre- and post-rewind identities) the supersede write replaces all of it — including the launch portion that was never re-flown. The first version of this plan tried a write-set filter (Approach A); a clean-Opus review found that approach silently breaks kerbal-permanent-death tombstoning, RewindPoint reap eligibility, Unfinished-Flight classification, nested Re-Fly chains, and tracking-station ghost suppression, because every reader of the supersede table is wired around the "id-only, whole recording" invariant.

This plan now fixes the data model so the readers' id-only assumption stays correct: **at merge time, split the origin recording at the rewind-point UT into a HEAD (pre-rewind, kept visible) and a TIP (post-rewind, superseded by the fork)**. After the split, every existing reader (ERS visibility, slot tip resolution, tombstone scope, RP reap, ghost map presence, Unfinished-Flights, etc.) becomes correct without further changes, because the boundary that used to live inside one recording now lives between two distinct recording ids.

## Repro recap

User flew Kerbal X mission 1, rewound, launched Kerbal X (2) at UT 8.42, crashed at UT 52.7 (single recording `94806c0b…`, no clean stage events — five CRASH BranchPoints, no env-class splits). User clicked Re-Fly → forked `rec_f512…` from rewind point at UT 34.24. Today: `94806c0b` is wholly superseded → its launch row vanishes from the timeline, its Watch button greys, the on-board kerbal stays in the dead state from origin's terminal, the second Re-Fly on the same slot can't find fork1 via the supersede chain.

After this plan ships: at merge time, `94806c0b` becomes HEAD covering `[8.42, 34.24]` (kept) and TIP covering `[34.24, 52.7]` (new id, superseded by fork). HEAD's row stays in the timeline. The kerbal's `Dead` action moves to TIP and gets tombstoned. The slot's effective-tip walker reaches fork.

## Existing-code anchor points (read before implementing)

### Mechanism: split

- [RecordingOptimizer.cs:774-1068](../../../Source/Parsek/RecordingOptimizer.cs) — `SplitAtSection(Recording original, int sectionIndex)`. The canonical in-recording split. Partitions Points / PartEvents / SegmentEvents / FlagEvents / TrackSections / OrbitSegments by UT, interpolates a synthetic boundary point if the split UT falls between two stored points (lines 797-848), partitions visual snapshots, terminal state, terminal-orbit fields, end-resources / end-inventory / end-crew (transfers to second), and refreshes endpoint decisions. Mutates `original` to the first half, returns `second`. Reusable as-is once we pre-create a TrackSection boundary at the requested UT.
- [RecordingStore.cs:3340-3541](../../../Source/Parsek/RecordingStore.cs) — `RunOptimizationSplitPass`. The existing wire-up site: assigns `second.RecordingId = Guid.NewGuid().ToString("N")`, shares ChainId, copies vessel-name / VesselPersistentId / PreLaunch resources / RecordingGroups / CreatingSessionId / ProvisionalForRpId / SupersedeTargetId, moves `ChildBranchPointId` via `ShouldMoveChildBranchPointToSplitSecondHalf` if BP.UT >= second.StartUT (lines 3431-3493), rewrites BP `ParentRecordingIds` to point at second (lines 3461-3492), inserts second into `recordings[recIdx+1]`, calls `tree.AddOrReplaceRecording(second)`, and runs `RecordingOptimizer.ReindexChain(recordings, original.ChainId)` (line 3515). **The full BP-reparenting in `RunOptimizationSplitPass` only handles the recording's single own `ChildBranchPointId`**, not other BPs that name the recording in their `ParentRecordingIds`. The plan must generalize that.
- [BranchPoint.cs](../../../Source/Parsek/BranchPoint.cs) — `ParentRecordingIds` (list), `ChildRecordingIds` (list), `UT`, `Type`. Multiple BPs can reference one recording as parent. For mission 2's repro, 5 BREAKUP BPs (UT 22.58, 31.46, 32.46, 33.18, 34.24+) all list `94806c0b` in `ParentRecordingIds`. After split, BPs whose UT ≥ rewindUT must be reparented HEAD → TIP.

### Mechanism: chain-aware walker

- [ChildSlot.cs:148-151](../../../Source/Parsek/ChildSlot.cs) — `ChildSlot.EffectiveRecordingId(supersedes)` delegates to `EffectiveState.EffectiveRecordingId`.
- [EffectiveState.cs:92-134](../../../Source/Parsek/EffectiveState.cs) — `EffectiveRecordingId(originId, supersedes)`. **Pure supersede walker** — no chain awareness. Cycle-safe via visited set. After the split, the slot's `OriginChildRecordingId` still names HEAD; the supersede row is `TIP → fork`. A pure supersede walk from HEAD finds no outgoing edge and returns HEAD. Slot tip is wrong; the fix needs a chain-then-supersede composite.
- [EffectiveState.cs:464-525](../../../Source/Parsek/EffectiveState.cs) — `ResolveChainTerminalRecording(rec, treeContext)`. The existing chain-tip finder: matches on `(ChainId, ChainBranch)`, picks largest `ChainIndex` from the owning tree's `Recordings` dict. **Reusable** as the chain hop inside the composite walker.
- [EffectiveState.cs:156-222](../../../Source/Parsek/EffectiveState.cs) — `ResolveRewindPointSlotIndexForRecording` and `IsInSupersedeForwardTrail`. The slot-resolver used by `UnfinishedFlightClassifier.TryResolveRewindPointForRecording`. Both must learn the chain hop or call the new composite.
- [EffectiveState.cs:143-154](../../../Source/Parsek/EffectiveState.cs) — `IsVisible`. **Must stay supersede-only** (chain members are NOT hidden by chain alone — both HEAD and TIP can be simultaneously visible until a supersede row replaces one of them).
- [EffectiveState.cs:230-240](../../../Source/Parsek/EffectiveState.cs) — `IsSupersededByRelation`. **Must stay supersede-only**, same rationale.

### Mechanism: subtree closure

- [EffectiveState.cs:727-932](../../../Source/Parsek/EffectiveState.cs) — `ComputeSubtreeClosureInternal`. The walk that drives AppendRelations. Already includes chain-sibling enqueue (`EnqueueChainSiblings` at line 1003) — so once HEAD and TIP exist as separate records sharing a ChainId, the closure naturally picks up both. The plan does NOT change closure semantics; instead, it tightens the closure root so HEAD is excluded.
- [EffectiveState.cs:1058-1098](../../../Source/Parsek/EffectiveState.cs) — `EnqueuePidPeerSiblings`. UT-cutoff predicate already filters peers to `StartUT >= marker.InvokedUT - 0.05`. No change.

### Mechanism: supersede write

- [SupersedeCommit.cs:140-333](../../../Source/Parsek/SupersedeCommit.cs) — `AppendRelations`. Closure root at line 153: `marker.SupersedeTargetId ?? marker.OriginChildRecordingId`. **The plan reroutes this to TIP after the split** — the marker is mutated in place so `SupersedeTargetId = TIP.RecordingId`. Closure now starts at TIP; HEAD is not in the closure (it has a different RecordingId and is on the *parent* side of any chain-sibling walk that starts at TIP — verify this in `EnqueueChainSiblings`).
- [SupersedeCommit.cs:357-372 / 390-398](../../../Source/Parsek/SupersedeCommit.cs) — `IsPreRewindDebris` / `ComputePreRewindCutoff`. **Both retained unchanged.** Pre-rewind debris is still pre-rewind after the split; the carve-out still filters it from the write-set.
- [SupersedeCommit.cs:108 / MergeJournalOrchestrator.cs:208](../../../Source/Parsek/SupersedeCommit.cs) — `CommitTombstones(marker, subtree, …)`. Consumes the filtered subtree. With TIP as the closure root, TIP's ledger actions get tombstoned; HEAD's actions are not touched.

### Mechanism: marker + invoker

- [ReFlySessionMarker.cs](../../../Source/Parsek/ReFlySessionMarker.cs) — `RewindPointUT` (the drift-immune rewind point UT, present since PR #858), `OriginChildRecordingId`, `SupersedeTargetId`, `ActiveReFlyRecordingId`, `SessionId`, `TreeId`, `InvokedUT`. The plan mutates `SupersedeTargetId` after split. **`OriginChildRecordingId` is intentionally left pointing at HEAD** — that's the slot's stable origin, and the slot resolver gets a chain-then-supersede walker that reaches fork via HEAD → (chain) → TIP → (supersede) → fork.
- [RewindInvoker.cs:1124,1186,1254-1273](../../../Source/Parsek/RewindInvoker.cs) — `AtomicMarkerWrite`. Sets `priorTip = selected.EffectiveRecordingId(scenario.RecordingSupersedes)`. **After this fix**, `priorTip` is computed via the new composite walker (chain + supersede) so the second Re-Fly on the same slot finds fork1 as the prior tip, not HEAD. Same priorTip is used for `provisional.SupersedeTargetId` (transient diagnostic, line 1186) and `marker.SupersedeTargetId` (line 1254). No code change at the invoker beyond swapping which walker `ChildSlot.EffectiveRecordingId` delegates to.

### Mechanism: ledger action retag

- [GameActions/Ledger.cs](../../../Source/Parsek/GameActions/Ledger.cs) — `GameAction.RecordingId` (string), `GameAction.UT`. `Ledger` is a **static class** with `internal static int StateVersion` (line 30) bumped via `Ledger.BumpStateVersion()` (line 37). Actions are accessed via `Ledger.Actions` (static list). The plan introduces a UT-partitioning pass that walks actions, rewrites `RecordingId = HEAD → TIP` for any action whose `UT >= splitUT`, then calls `Ledger.BumpStateVersion()` so ELS cache invalidates.
- [GameActions/TombstoneAttributionHelper.cs:33](../../../Source/Parsek/GameActions/TombstoneAttributionHelper.cs) — `InSupersedeScope(action, subtreeIds)`. Pure id-set membership. After retag, the helper correctly identifies TIP-tagged actions for tombstoning and leaves HEAD-tagged actions alone.

### Mechanism: debris parent retag

- [Recording.cs:177-282](../../../Source/Parsek/Recording.cs) — `DebrisParentRecordingId`. Per the v13 debris-parent-anchor contract, every debris recording names its parent recording id. After splitting `94806c0b` into HEAD + TIP, debris that started **before** the rewind UT (e.g., 06bdba71, 4a3dd0cc — both at UT 22.58) keeps HEAD as parent; debris started **at or after** the rewind UT reparent to TIP.
- [BackgroundRecorder.cs](../../../Source/Parsek/BackgroundRecorder.cs) — the RELATIVE-section `anchorRecordingId` was populated at recording time and references the parent recording id at the moment of decouple. **This identifier is in trajectory-internal data** (per `TrackSection.anchorRecordingId`, format-v11 contract); it must also be rewritten for any TIP-side TrackSection that points back to HEAD's id. The repro happens to fall into the in-place mid-recording case where TrackSections in the original were all parented at the live PID, so the anchor walk is a small, targeted update.

### Mechanism: journal + crash recovery

- [MergeJournalOrchestrator.cs:184-264](../../../Source/Parsek/MergeJournalOrchestrator.cs) — `RunMerge`. Phases: Begin → Supersede → Tombstone → Finalize → Durable1Done → RpReap → MarkerCleared → Durable2Done. The plan adds a new step BETWEEN Begin and Supersede, executed inside the Begin phase so it shares Begin's pre-Durable1 rollback contract.
- [MergeJournalOrchestrator.cs:267-320](../../../Source/Parsek/MergeJournalOrchestrator.cs) — `RunFinisher`. Pre-Durable1 phases roll back; post-Durable1 drive forward. The split is journaled-but-not-Durable until Durable1; if a crash interrupts mid-split, the rollback path needs to undo the partial split (delete TIP, re-merge data into HEAD, reverse BP/debris/ledger retags). Idempotent re-run is fine.
- [MergeJournal.cs](../../../Source/Parsek/MergeJournal.cs) — `Phases` constants. Adds a new constant `Split` (between Begin and Supersede).

### Tests

- [Source/Parsek.Tests/SupersedeCommitTests.cs](../../../Source/Parsek.Tests/SupersedeCommitTests.cs) — existing AppendRelations fixture; pattern reused.
- [Source/Parsek.Tests/VesselSwitchTreeTests.cs](../../../Source/Parsek.Tests/VesselSwitchTreeTests.cs) — recording-tree fixture pattern.
- [Source/Parsek.Tests/SessionSuppressedSubtreeTests.cs](../../../Source/Parsek.Tests/SessionSuppressedSubtreeTests.cs) — closure-walk fixture pattern with marker.
- [Source/Parsek/InGameTests/MergeCrashedReFlyCreatesCPSupersedeTest.cs](../../../Source/Parsek/InGameTests/MergeCrashedReFlyCreatesCPSupersedeTest.cs) — in-game live-scene Crashed-Re-Fly fixture; needs an updated assertion shape.
- [Source/Parsek/InGameTests/MergeLandedReFlyCreatesImmutableSupersedeTest.cs](../../../Source/Parsek/InGameTests/MergeLandedReFlyCreatesImmutableSupersedeTest.cs) — same for Landed.

## Design overview

### When the split fires

The split is a single new step in `MergeJournalOrchestrator.RunMerge`, executed after journal Begin and before Supersede. It runs iff all three predicates hold:

1. `marker.RewindPointUT` is a usable cutoff (`!double.IsNaN(marker.RewindPointUT) && marker.RewindPointUT > 0`).
2. The marker's closure root (`marker.SupersedeTargetId ?? marker.OriginChildRecordingId`) names a recording in `RecordingStore.CommittedRecordings`.
3. That recording's UT bounds **strictly span** the rewind point: `rec.StartUT < marker.RewindPointUT - PidPeerStartUtEpsilonSeconds` AND `rec.EndUT > marker.RewindPointUT + PidPeerStartUtEpsilonSeconds`.

When the predicates miss (any of: no usable UT / closure root not found / recording entirely pre- or post-rewind / recording is a placeholder), the split is a no-op and AppendRelations runs as today.

### What the split produces

After `SplitOriginAtRewindUT(marker, scenario)` completes:

- `HEAD` (in-place mutation of the original Recording object) covers `[rec.StartUT, marker.RewindPointUT]`. Keeps `RecordingId`, gets `ChainId` and `ChainIndex = 0` (or reuses existing chain assignments if the recording was already chained), `TerminalStateValue = null`, no terminal-orbit fields, no end-resources / end-inventory / end-crew. `ChildBranchPointId` is moved to TIP iff the BP's `UT >= marker.RewindPointUT` (reusing the exact rule in `RecordingStore.ShouldMoveChildBranchPointToSplitSecondHalf` at `RecordingStore.cs:3543`); otherwise HEAD keeps it.
- `TIP` (new Recording, allocated by the split) covers `[marker.RewindPointUT, rec.EndUT]`. New `RecordingId = Guid.NewGuid().ToString("N")`. Same `ChainId`, `ChainIndex = HEAD.ChainIndex + 1`. Carries the original's `TerminalStateValue`, terminal-orbit fields, end-resources, end-inventory, end-crew, `VesselSnapshot`, all transferred via `SplitAtSection`'s existing step-10 logic. `ChildBranchPointId` follows the same `ShouldMoveChildBranchPointToSplitSecondHalf` rule.
- `marker.SupersedeTargetId = TIP.RecordingId` (mutated in-place). The closure walk now starts at TIP, finds debris children whose `StartUT >= rewindUT` reparented to TIP, finds the existing chain HEAD on the other side (which the closure walk's chain-sibling enqueue does include — but the existing pre-rewind-debris carve-out is renamed and generalized to carve out HEAD too, since HEAD is now a pre-rewind chain sibling).

### Critical wiring decisions

1. **HEAD keeps the original id.** This preserves all existing backrefs from BPs, debris, ledger actions, group hierarchy entries, sidecar filenames. Only the post-rewind subset moves.
2. **TIP gets the new id.** Every backref that was pointing at the original AND is post-rewind UT (BP.ParentRecordingIds, DebrisParentRecordingId, GameAction.RecordingId, TrackSection.anchorRecordingId on TIP-side sections) is rewritten in the split step.
3. **Slot.OriginChildRecordingId is unchanged.** The slot still points at HEAD (= original id). The fork's resolution from the slot uses the new composite walker (HEAD → chain → TIP → supersede → fork).
4. **No new schema fields.** Reuses ChainId / ChainIndex (existing optimizer-split contract). No UT range on `RecordingSupersedeRelation`.
5. **No reader changes** except the composite walker is added to `EffectiveState` and `ChildSlot.EffectiveRecordingId` is rewired to it. Other readers (`IsVisible`, `IsSupersededByRelation`, `ComputeERS`) stay on the pure supersede walker.

### Chain-then-supersede composite walker

New helper in `EffectiveState`:

```
internal static string EffectiveTipRecordingId(
    string originRecordingId,
    IReadOnlyList<RecordingSupersedeRelation> supersedes)
```

Algorithm:
1. Start at `originRecordingId`. Visited set guard for cycles.
2. Repeat:
   a. **Chain hop**: if the current recording has a non-empty `ChainId`, find the chain-tip (same `ChainId`, same `ChainBranch`, largest `ChainIndex`) via `ResolveChainTerminalRecording`. Replace current with chain tip.
   b. **Supersede hop**: look for a supersede row with `OldRecordingId == current`. If found, replace current with `NewRecordingId`. If not, return current.
   c. Loop back to (a).
3. Cycle-safe via visited set; warn on cycle as the existing walker does.

**Who uses the composite vs the pure walker:**

| Caller | Walker | Rationale |
|---|---|---|
| `ChildSlot.EffectiveRecordingId` | **composite** | Slot's logical tip must follow chains across env-class AND rewind-UT splits, then through supersede |
| `RewindInvoker.AtomicMarkerWrite priorTip` (l. 1124) | **composite** (via ChildSlot.EffectiveRecordingId) | Same |
| `ResolveRewindPointSlotIndexForRecording` (l. 156) | **composite** for the comparison hop | Fork must match its slot's tip regardless of how many chain/supersede hops away |
| `IsInSupersedeForwardTrail` | **composite** for forward-trail edges | Same reason |
| `UnfinishedFlightClassifier.TryResolveRewindPointForRecording` | indirectly via the two above | Same |
| `IsVisible` | **pure supersede** | A chain member is not hidden just because a sibling chain member exists. Hiding is supersede-only. |
| `IsSupersededByRelation` / `ComputeSupersededRecordingIdsByRelation` | **pure supersede** | Same |
| `ComputeERS` | **pure supersede** | Same — `IsVisible` is the gate |

This split-of-concerns is the architectural lever: the "is this row visible?" question stays id-local (supersede-only); the "where does this slot point now?" question follows the chain.

### Pre-rewind carve-out generalization

The existing `IsPreRewindDebris` filter in `AppendRelations` is renamed `IsPreRewindCarveOut` and extended:

```
internal static bool IsPreRewindCarveOut(
    Recording rec, ReFlySessionMarker marker,
    out PreRewindCarveOutReason reason)
{
    reason = PreRewindCarveOutReason.None;
    if (rec == null || marker == null) return false;

    // Debris case (PR #858): pre-rewind debris uses the existing
    // ComputePreRewindCutoff = rewindUT - epsilon (StartUT < cutoff
    // means StartUT strictly before rewind, with the epsilon absorbing
    // sampler-jitter forward of the rewind point).
    double debrisCutoff = ComputePreRewindCutoff(marker);
    if (!double.IsNaN(debrisCutoff)
        && rec.IsDebris && !string.IsNullOrEmpty(rec.DebrisParentRecordingId)
        && rec.StartUT < debrisCutoff)
    {
        reason = PreRewindCarveOutReason.PreRewindDebris;
        return true;
    }

    // Chain-head case (new): a non-debris recording whose entire
    // lifetime ends at or just past the rewind UT. After
    // SplitOriginAtRewindUT, HEAD's EndUT == marker.RewindPointUT
    // exactly (the split UT). The predicate uses + epsilon (NOT -
    // epsilon) because we are testing the *upper* boundary against
    // the rewind point, not the *lower* boundary like the debris
    // case. Concretely: HEAD.EndUT == rewindUT is allowed through,
    // while a post-rewind chain sibling with StartUT > rewindUT (so
    // EndUT > rewindUT + epsilon comfortably) is NOT.
    double headCutoff = double.IsNaN(marker.RewindPointUT) || marker.RewindPointUT <= 0.0
        ? double.NaN
        : marker.RewindPointUT + EffectiveState.PidPeerStartUtEpsilonSeconds;
    if (!double.IsNaN(headCutoff)
        && !rec.IsDebris
        && rec.EndUT <= headCutoff)
    {
        reason = PreRewindCarveOutReason.PreRewindChainHead;
        return true;
    }

    return false;
}
```

The debris and chain-head predicates are **not** symmetric — the debris case tests the *lower* boundary (`StartUT < rewindUT − ε`, "started strictly before the rewind") and the chain-head case tests the *upper* boundary (`EndUT <= rewindUT + ε`, "ended at the rewind"). The first-revision plan got this wrong by trying to reuse `ComputePreRewindCutoff = rewindUT − ε` for both, which would have made HEAD's `EndUT == rewindUT` fail the test (false: `rewindUT <= rewindUT − ε`) and shipped the exact bug this plan exists to fix. The chain-head cutoff is therefore `rewindUT + PidPeerStartUtEpsilonSeconds`, computed inline rather than borrowing `ComputePreRewindCutoff`.

Post-rewind chain siblings (those whose `StartUT >= rewindUT − ε`) have `EndUT > rewindUT + ε` (because they extend forward from the rewind point), so the chain-head predicate correctly rejects them — they still receive supersede rows.

## Step-by-step implementation

### Step 1 — `RecordingOptimizer.SplitAtUT` helper

File: [Source/Parsek/RecordingOptimizer.cs](../../../Source/Parsek/RecordingOptimizer.cs)

New public-internal method, near `SplitAtSection`:

```
internal static Recording SplitAtUT(Recording original, double splitUT)
```

Steps inside:
1. Pre-conditions: `original != null && !double.IsNaN(splitUT) && original.StartUT < splitUT && original.EndUT > splitUT`. Fail-loud (log Warn + return null) on violation.
2. **Orbit-segment-straddle guard.** Walk `original.OrbitSegments`; if any segment has `seg.startUT < splitUT && seg.endUT > splitUT`, return null with a Warn log. The orbiter caller (`SplitOriginAtRewindUT`) treats null as "skip split, fall back to whole-recording supersede" so the merge still completes — the player just gets the today's behavior for that one recording. Rationale: `SplitAtSection`'s orbit-segment partition (`RecordingOptimizer.cs:878-891`) moves whole segments to either half and trims the straddler's first half via `TrimFirstHalfOrbitSegmentsAtSplit` (`RecordingOptimizer.cs:1096-1121`), but never tail-clones the post-rewind portion to TIP. For optimizer env-class splits this never bites (orbit segments are checkpoint-aligned with section boundaries by construction), but a rewind UT at an arbitrary point in an orbital flight would silently drop the TIP-side orbit data. Tail-cloning an OrbitSegment at an arbitrary UT requires orbit-state propagation and is deferred as a separate work item (see "Out of scope").
3. Ensure checkpoint sections (same as `SplitAtSection`'s line 776-779 call).
4. **Find or insert TrackSection boundary at `splitUT`:**
   - Walk `original.TrackSections` to find the section where `section.startUT <= splitUT < section.endUT`.
   - If `Math.Abs(section.startUT - splitUT) < epsilon` (the start aligns within `PidPeerStartUtEpsilonSeconds = 0.05`), use that section's index. No new boundary needed.
   - Otherwise insert a synthetic boundary at `sectionIndex + 1`. Clone the straddling section into head (`startUT, endUT` rewritten to `[section.startUT, splitUT]`) and tail (`[splitUT, section.endUT]`). All other section metadata copies as follows:
     - `environment`: copy (TIP's first section inherits parent's environment classification).
     - `referenceFrame`: copy.
     - `anchorRecordingId`: copy (anchor-chain contract — at this stage the anchor is still origin's id; Step 2.8 of `SplitOriginAtRewindUT` rewrites it to TIP's id after).
     - `sampleRateHz`, `source`, `minAltitude`, `maxAltitude`: copy from original; the per-half max/min get recomputed by `SplitAtSection`'s downstream `RecordingEndpointResolver.RefreshEndpointDecision` and the `StampExplicitBoundsFromPayload` call.
     - `isBoundarySeam`: set FALSE on both halves of the synthetic split. The rewind-UT cut is not a recorder bookkeeping artifact; the seam-protection logic in `RecordingOptimizer.IsSplittableEnvOrBodyBoundary` step 1 (CLAUDE.md "Optimizer split predicate") must not fire on these synthetic boundaries. If the straddling section had `isBoundarySeam == true` pre-split, log a Warn naming the section index and proceed — splitting a seam-protected section at an arbitrary UT is a deliberate one-off override scoped to this Re-Fly commit.
     - `boundaryDiscontinuityMeters`: set 0.0 on the tail (the synthetic split is continuous in world space); preserve the head's pre-existing value.
   - Partition the section's `frames` (anchor-local samples) and `bodyFixedFrames` (body-fixed primary surface for v13 debris) by `frame.UT`: frames with `UT < splitUT` stay on head, frames with `UT >= splitUT` move to tail. If `bodyFixedFrames` is present on the section AND the post-split tail ends up with fewer than 2 samples, return null with a Warn ("v13 debris contract: tail bodyFixedFrames sample count below minimum"). The caller falls back to no-op. (Symmetric guard on the head; in practice the head always has the recording-start sample so this fires only on pathological tiny recordings.)
   - Edge: if `splitUT` falls in a gap between sections (no straddling section found), pick the index of the first section whose `startUT >= splitUT`. No synthetic boundary needed.
5. Delegate to existing `SplitAtSection(original, sectionIndex)`. The interpolated-boundary-point branch (lines 797-848) handles point-list partitioning across the cut.
6. Log: `[Parsek][INFO][Optimizer] SplitAtUT: split {RecordingId} at UT={splitUT} (head=[{HEAD.StartUT}..{HEAD.EndUT}], tail=[{TIP.StartUT}..{TIP.EndUT}], syntheticBoundaryInserted={true|false})`.

Returns `second` (TIP), or null if any guard failed. Caller treats null as "skip the split for this recording" and proceeds with the today's whole-recording supersede path (write a single row `origin → fork` — the old buggy behavior, but at least the merge completes without partial state). The caller also logs a Warn naming origin's id and the guard reason so the player can investigate.

**Mutation ordering (Step 4 guards before mutation):** the synthetic-boundary clone in Step 4 builds the head + tail section pair in **local variables** before writing them back to `original.TrackSections`. The v13 minimum-sample check runs against the local-variable lists; if either fails, the function returns null without ever mutating `original`. This guarantees a guarded-return leaves the input recording byte-identical to its pre-call state, so the caller's fallback (whole-recording supersede) operates on a clean origin.

**Out of scope: closure-member straddling.** The orbit-segment straddle guard only checks the closure root (origin). A debris recording included in the closure via `EnqueueDebrisChildren` whose own OrbitSegments straddle rewindUT is NOT guarded and continues to receive a supersede row covering its full range (including the straddler's post-rewind half being silently lost). The bug surface for this case is "the post-rewind portion of a debris's orbital trajectory becomes inaccessible after a Re-Fly" — narrow and low-impact compared to the canonical bug. Track separately if it ever surfaces.

### Step 2 — `RecordingTreeSplitter.SplitOriginAtRewindUT` orchestrator

File: New `Source/Parsek/RecordingTreeSplitter.cs` (or fold into `RecordingStore`; reviewer can suggest placement). The class is static-internal.

Method signature:

```
internal static SplitOriginResult SplitOriginAtRewindUT(
    ReFlySessionMarker marker, ParsekScenario scenario)
```

Where `SplitOriginResult` carries `{HeadRecordingId, TipRecordingId, SplitUT, BpReparented, DebrisReparented, ActionsRetagged, Skipped, SkipReason}` for logging + test assertions.

Algorithm:

1. **Pre-check predicates** (the three predicates listed under "When the split fires" above). Return `Skipped=true` with reason if any miss.
2. **Resolve the origin recording** via `RecordingStore.CommittedRecordings.FirstOrDefault(r => r.RecordingId == marker.SupersedeTargetId ?? marker.OriginChildRecordingId)`. Return `Skipped=true` if not found.
3. **Idempotency check**: a recording already exists in the same tree that matches **all three** of (a) `ChainId == origin.ChainId`, (b) `ChainIndex == origin.ChainIndex + 1` (the canonical post-split chain shape after `ReindexChain` runs), and (c) `Math.Abs(StartUT - marker.RewindPointUT) < EffectiveState.PidPeerStartUtEpsilonSeconds`. Without check (c), a pre-existing env-class chain sibling at `ChainIndex + 1` would false-positive (matches a/b but its StartUT lies elsewhere on the chain — usually at the env transition UT, not at the rewind UT). If all three match, set `tip = thatRecording`, skip steps 4-5 (split + identity wiring), and **also skip steps 6-9b if `tip.ChildBranchPointId`, the marker's `SupersedeTargetId`, the ledger version, and the milestone-store version have all advanced** — those are detectable post-conditions. Otherwise re-run steps 6-9b idempotently (each retag step is "for each row matching the predicate, rewrite once" — running twice is a no-op because the predicate no longer matches). Proceed to step 10 marker mutation, step 11 store version bump, step 12 invariant logs, step 13 summary log; all idempotent.
4. **Perform the split** via `RecordingOptimizer.SplitAtUT(origin, marker.RewindPointUT)`. If it returns null (orbit-segment straddle guard, v13 frame-count guard, or other pre-condition failure), set `Skipped = true`, `SkipReason = SplitAtUT_Guarded`, fall back: log Warn naming origin's id, set `marker.SupersedeTargetId = origin.RecordingId` (unchanged), and **return without further mutation so the downstream Supersede phase writes a single whole-recording supersede row for origin** (the today's buggy behavior). The merge completes; the launch row stays hidden for this one recording. Future work can lift the guard once orbit-segment tail-cloning is implemented.
5. **Wire identity + tree** (same shape as `RunOptimizationSplitPass` at `RecordingStore.cs:3406-3515`):
   - `tip.RecordingId = Guid.NewGuid().ToString("N")`.
   - If `origin.ChainId` is empty, assign a new `Guid.NewGuid().ToString("N")` to BOTH origin (HEAD) and tip. ChainIndex assignments: `origin.ChainIndex = 0` if previously unset; `tip.ChainIndex = origin.ChainIndex + 1`. `tip.ChainBranch = origin.ChainBranch`.
   - Copy: `tip.TreeId = origin.TreeId`, `tip.VesselName = origin.VesselName`, `tip.VesselPersistentId = origin.VesselPersistentId`, PreLaunch fields, `RecordingGroups` (defensive copy), `CreatingSessionId`, `ProvisionalForRpId`, `SupersedeTargetId`. (Note: `SupersedeTargetId` is the *transient* field on Recording; it's about to be cleared in `FlipMergeStateAndClearTransient`.)
   - **Move `ChildBranchPointId` per `ShouldMoveChildBranchPointToSplitSecondHalf` (`RecordingStore.cs:3543`):** the helper takes `secondStartUT` (= TIP's StartUT, which after split equals `marker.RewindPointUT`) and returns true iff `BP.UT >= secondStartUT - 0.0001`. If true, move to TIP — set `tip.ChildBranchPointId = origin.ChildBranchPointId; origin.ChildBranchPointId = null`. Else keep on HEAD — set `tip.ChildBranchPointId = null`. Also rewrite the moved BP's `ParentRecordingIds` entry from `origin.RecordingId` to `tip.RecordingId` (same logic as `RecordingStore.cs:3461-3492`). **Without this rule, a Re-Fly target whose own `ChildBranchPointId` points at a pre-rewind BP (e.g., decouple at UT 20, rewind at UT 34) would silently drop its parent reference.**

   Note: `origin.StartUT` and `tip.StartUT` are NOT equal post-split. `origin` is mutated in place to become HEAD with its original `StartUT` preserved (e.g., 8.42 for the repro). `tip.StartUT` equals the rewind UT (e.g., 34.24). The helper's `secondStartUT` argument must be `tip.StartUT`, not `origin.StartUT`. (Earlier revisions of this plan got this wrong — flagged in the r3 review.)
   - Add `tip` to `RecordingStore.CommittedRecordings` at the index immediately after `origin`.
   - Add `tip` to the owning tree's `Recordings` dict via `tree.AddOrReplaceRecording(tip)`.
   - Run `RecordingOptimizer.ReindexChain(recordings, origin.ChainId)` to settle ChainIndex assignments.
   - Mark both as `FilesDirty = true` so sidecar persistence picks them up.
6. **Reparent BranchPoints**:
   - Walk the owning tree's `BranchPoints`. For each BP:
     - If `BP.UT >= marker.RewindPointUT` AND `BP.ParentRecordingIds` contains `origin.RecordingId`: replace that entry with `tip.RecordingId`.
     - If `BP.UT < marker.RewindPointUT`: no change (stays parented to HEAD).
   - Increment `BpReparented` counter; log a summary line.
   - **Edge: the BP whose UT == marker.RewindPointUT.** This is the BP that the rewind was triggered FROM (Breakup at UT 34.24 in the repro). Treat its UT as belonging to TIP — it represents the breakup state TIP starts from. Reparent it to TIP. The marker's `SupersedeTargetId` was set from `slot.EffectiveRecordingId` which returned origin's id pre-split, so the slot's BP back-pointer is unaffected.
   - **`BP.ChildRecordingIds` audit (not modified by this step):** the BP's `ChildRecordingIds` lists the *divergent* recordings that branched off at this BP (debris and forks, not the continuing parent). Those entries reference debris/fork recording ids, which are *unchanged* by this split (only origin → HEAD/TIP changes; the divergent children are separate recordings keeping their own ids). The post-rewind-debris reparent in Step 7 rewrites `DebrisParentRecordingId` on those children, but the BP's `ChildRecordingIds` list of their ids is correct as-is. No rewriting needed here. Verified via grep: `EffectiveState.ComputeSubtreeClosureInternal:869-873` and `GhostChainWalker` (only readers) both interpret `ChildRecordingIds` as "which recordings diverge at this BP", which is identity-stable across the split.
7. **Reparent debris**:
   - Walk `RecordingStore.CommittedRecordings`. Collect the set `reparentedDebrisIds = {}`. For each debris `d` with `d.DebrisParentRecordingId == origin.RecordingId` AND `d.StartUT >= marker.RewindPointUT`: set `d.DebrisParentRecordingId = tip.RecordingId`; add `d.RecordingId` to `reparentedDebrisIds`. Pre-rewind debris (`d.StartUT < marker.RewindPointUT`) keeps origin as parent — origin's id is still HEAD's id, so the reference stays valid.
   - Counter + log summary.
8. **Retag TrackSection anchor references** (the v11 anchor-chain contract):
   - **TIP-side sections**: walk `tip.TrackSections`. For each section with `referenceFrame == ReferenceFrame.Relative` AND `anchorRecordingId == origin.RecordingId`: rewrite to `tip.RecordingId`. In practice these are anchor-self loops on TIP-side sections (the section's vessel-of-record is the same physical body whose recording is being split); the rewrite reflects that TIP-side sections are now anchored to TIP's id rather than HEAD's.
   - **Reparented-debris sections**: for each debris id in `reparentedDebrisIds`, look up the Recording and walk its `TrackSections`. For each section with `referenceFrame == ReferenceFrame.Relative` AND `anchorRecordingId == origin.RecordingId` AND `section.startUT >= marker.RewindPointUT`: rewrite to `tip.RecordingId`. **This is the case I1 from the second review**: a post-rewind debris's anchor-self reference was authored against the live PID's recording (origin's id pre-split), which after the split represents the U+L phase. Post-rewind debris is physically anchored to TIP's lifetime, so its anchor reference must follow the reparent.
   - **HEAD-side sections**: walk `head.TrackSections` (= origin's mutated TrackSections after the in-place split). For Relative sections with `anchorRecordingId == origin.RecordingId`: **no rewrite** — HEAD keeps origin's id, so these stay correct.
   - Log a summary: `[Parsek][VERBOSE][Splitter] Anchor rewrites: tipSections={N1} debrisSections={N2}`.
9. **Partition ledger actions**:
   - Walk `Ledger.Actions` (static; `Ledger.cs:14`). For each action with `action.RecordingId == origin.RecordingId` AND `action.UT >= marker.RewindPointUT`: rewrite `action.RecordingId = tip.RecordingId`.
   - Call `Ledger.BumpStateVersion()` (`Ledger.cs:37`) so the ELS cache invalidates on next read.
   - Counter + log summary.
9b. **Retag milestones**:
   - Walk `MilestoneStore.Milestones` (static — `MilestoneStore.cs:31` exposes `internal static IReadOnlyList<Milestone> Milestones => milestones;`). Per `MilestoneStore.cs:90-101`, each Milestone carries `RecordingId`, `StartUT` (= the previous milestone's EndUT, or 0 — the start of this milestone's events-window), and `EndUT` (= the currentUT at flush time — the *end* of the events window). For each milestone with `RecordingId == origin.RecordingId` AND `StartUT >= marker.RewindPointUT - EffectiveState.PidPeerStartUtEpsilonSeconds`: rewrite `RecordingId = tip.RecordingId`.
   - `StartUT` is **the start of the milestone's events-window**, not the milestone-creation UT (the creation UT is `EndUT`). Using `StartUT` cleanly separates milestones whose entire window lies post-rewind (retag → TIP) from those whose window predates the rewind (stay on HEAD). Milestones whose window *straddles* rewindUT (StartUT < rewindUT < EndUT) stay on HEAD — narrow edge case, acceptable.
   - Invalidate downstream subscribers via `LedgerOrchestrator.OnTimelineDataChanged?.Invoke()` (`LedgerOrchestrator.cs:113, 1753`). `StockUiOverlayController.cs:90` subscribes to this and rebuilds event-overlay marks. No dedicated `MilestoneStore.Version` field exists today; the timeline-changed signal is the correct hook.
   - Counter + log summary.
   - **What this achieves**: `Milestone.RecordingId` is read by `StockUiOverlayController.cs:688-690` as a fallback recordingId for event overlay marks, and indirectly by `GameStateEventConverter` when rebuilding ledger actions on supersede-state-version bump. After the retag, milestones earned post-rewind point at TIP's id; if TIP is later superseded by fork, the fallback recordingId becomes a superseded id (= invisible in ERS) which `StockUiOverlayController` already filters via `IsEventVisibleToCurrentTimeline`. Pre-rewind milestones stay tagged HEAD and remain in the player's career history regardless of the supersede chain.
   - **What this does NOT achieve**: `CommitTombstones` does NOT read `Milestone.RecordingId` directly; it walks ledger actions only (Step 9 handles those). The milestone retag is a UI-consistency pass that prevents a "post-rewind achievement attributed to HEAD" mismatch in event overlays; it is not load-bearing for tombstone correctness. If a future review finds the retag introduces a regression (e.g., a converted-action's recordingId now mismatches the milestone's), drop Step 9b — the bug fix's correctness does not depend on it.
10. **Mutate marker**: `marker.SupersedeTargetId = tip.RecordingId`. The closure walk in AppendRelations will now start at TIP. HEAD is reachable via chain-sibling enqueue but is carved out by `IsPreRewindCarveOut` (the renamed/generalized debris filter, which now also catches HEAD because its `EndUT == rewindUT <= rewindUT + epsilon`).
11. **Bump `RecordingStore.StateVersion`** so the ERS cache invalidates.
12. **Assert tree-root and tree-active invariants** (defensive Verbose logs):
    - If `tree.RootRecordingId == origin.RecordingId`: log `[Parsek][VERBOSE][Splitter] tree.RootRecordingId references HEAD (= origin's id unchanged)` — confirming HEAD's id preservation kept the tree root pointer valid.
    - If `tree.ActiveRecordingId == origin.RecordingId`: same Verbose log for ActiveRecordingId.
    - These are no-op assertions today (HEAD keeps origin's id by design), but the log lets a future regression where either field gets repointed at TIP show up immediately. Don't fail-loud — just log; the wiring is correct.
13. **Log** the summary: `[Parsek][INFO][Splitter] Split origin {OriginId} at UT={SplitUT}: HEAD=[{HEAD.StartUT}..{HEAD.EndUT}] (kept, id unchanged), TIP={NewTipId} [{TIP.StartUT}..{TIP.EndUT}] (will be superseded). bpReparented={N1} debrisReparented={N2} debrisAnchorRewrites={N3} actionsRetagged={N4} milestonesRetagged={N5} ledgerVersion={V} milestoneVersion={V}`.

Return `SplitOriginResult` with all counters populated.

### Step 3 — Generalize the carve-out

File: [Source/Parsek/SupersedeCommit.cs](../../../Source/Parsek/SupersedeCommit.cs)

- Rename `IsPreRewindDebris` → `IsPreRewindCarveOut`. Update the body to the form shown in the design overview (debris case + chain-head case).
- Add an enum `PreRewindCarveOutReason { None, PreRewindDebris, PreRewindChainHead }` near the top of the file.
- Update the call site at lines 285-298 to use the new signature and pass `out reason` for the log. Rename local counter `skippedPreRewindDebris → skippedPreRewindCarveOut` and `preRewindDebrisIds → preRewindCarveOutIds`. Update the summary log at lines 314-320.
- Keep `IsPreRewindDebris` as a thin wrapper that calls `IsPreRewindCarveOut` and returns `reason == PreRewindCarveOutReason.PreRewindDebris`. **Required**: in-game tests at `MergeCrashedReFlyCreatesCPSupersedeTest.cs:78` and `MergeLandedReFlyCreatesImmutableSupersedeTest.cs` (verify the exact call site) reference the old symbol; the wrapper preserves the binary contract.

### Step 4 — Composite walker

File: [Source/Parsek/EffectiveState.cs](../../../Source/Parsek/EffectiveState.cs)

Add `EffectiveTipRecordingId` as described in the design overview. Implementation sketch (cycle-safe, references existing `ResolveChainTerminalRecording`):

```
internal static string EffectiveTipRecordingId(
    string originRecordingId,
    IReadOnlyList<RecordingSupersedeRelation> supersedes)
{
    if (string.IsNullOrEmpty(originRecordingId)) return null;
    var visited = new HashSet<string>(StringComparer.Ordinal);
    string current = originRecordingId;
    visited.Add(current);

    while (true)
    {
        // Chain hop: only if current is a chain member.
        var currentRec = FindRecordingById(current);
        if (currentRec != null && !string.IsNullOrEmpty(currentRec.ChainId))
        {
            var chainTip = ResolveChainTerminalRecording(currentRec);
            if (chainTip != null && chainTip.RecordingId != current)
            {
                if (!visited.Add(chainTip.RecordingId))
                {
                    ParsekLog.Warn("Supersede", $"EffectiveTipRecordingId: cycle at chain-hop from {current} to {chainTip.RecordingId}; returning {current}");
                    return current;
                }
                current = chainTip.RecordingId;
                continue;
            }
        }

        // Supersede hop.
        string next = LookupSupersedeNext(current, supersedes);
        if (string.IsNullOrEmpty(next)) return current;
        if (!visited.Add(next))
        {
            ParsekLog.Warn("Supersede", $"EffectiveTipRecordingId: cycle at supersede-hop from {current} to {next}; returning {current}");
            return current;
        }
        current = next;
    }
}
```

Where `FindRecordingById` is a local helper that scans `RecordingStore.CommittedRecordings` for a recording matching the given id. **There is no such helper in `EffectiveState` today (verified via grep)**; the implementation must add one as a `private static Recording FindRecordingById(string id)` near the existing private utilities. Reuse the small loop pattern from `SupersedeCommit.BuildCommittedRecordingIndex` if dictionary-backed performance matters.

`LookupSupersedeNext(current, supersedes)` is also pseudocode in the sketch — it's the supersede-hop body extracted from `EffectiveRecordingId` (`EffectiveState.cs:108-118`, the inner `for` loop that finds `rel.OldRecordingId == current` and returns `rel.NewRecordingId`). Implementation can either extract it as a private static helper or inline the loop into the composite walker; both are fine.

Update `ChildSlot.EffectiveRecordingId` (`Source/Parsek/ChildSlot.cs:148-151`) to delegate to `EffectiveTipRecordingId` instead of `EffectiveRecordingId`.

Update `ResolveRewindPointSlotIndexForRecording` (`Source/Parsek/EffectiveState.cs:156-182`):
- Line 171: read `slot.OriginChildRecordingId`. Unchanged.
- Line 172 / 174-175: also compare via composite — `string effective = EffectiveTipRecordingId(origin, effectiveSupersedes);` if it matches `rec.RecordingId`, return that slot.
- Line 177: keep `IsInSupersedeForwardTrail` as a fallback for legacy mid-chain cases (its semantics are unchanged for the pure supersede graph; the composite handles the chain hop separately).

Update `IsInSupersedeForwardTrail` (`Source/Parsek/EffectiveState.cs:184-222`): add a chain hop inside the BFS — when dequeuing a recording, also enqueue its chain tip (via `ResolveChainTerminalRecording`) before walking supersede edges. Cycle-safe via the existing visited set.

Leave `IsVisible`, `IsSupersededByRelation`, `ComputeSupersededRecordingIdsByRelation`, `ComputeERS` **unchanged** — they stay on the pure supersede walker.

### Step 5 — Journal phase integration (Split as a post-durable barrier)

The split mutates persistent in-memory state (CommittedRecordings, tree.Recordings, BranchPoints, debris parents, ledger actions). Rolling those back in flight is high-risk; driving forward to completion is well-defined. The plan therefore makes Split a **post-durable phase with its own DurableSave barrier**, sitting between Begin's barrier and Durable1's barrier.

**Why a new barrier:** if Split were pre-Durable1 (like the previous-revision plan attempted), a fault-injected exception during Split's in-memory mutations would leave a partial split in memory; an autosave on scene change before the next durable barrier could leak that partial state to disk; `RunFinisher`'s rollback path would then clear the marker without undoing the split, leaving the save corrupt. The post-durable barrier prevents this: Split's mutations are committed-or-rolled-back atomically within the Split phase itself (via the internal try/catch in `SplitOriginAtRewindUT` that calls `RollBackInMemory` and re-throws); once `DurableSave("split")` returns, the split is durably committed and resume drives forward.

**Phase classification update:**

File: [Source/Parsek/MergeJournal.cs](../../../Source/Parsek/MergeJournal.cs)

- Add the phase constant: `public const string Split = "Split";`
- Update `IsPreDurablePhase` in-place: now returns true only for `Begin`. (Today it returns true for Begin/Supersede/Tombstone/Finalize per the design doc table at line 682-694.) All other phases (Split, Supersede, Tombstone, Finalize, Durable1Done, RpReap, MarkerCleared, Durable2Done) are post-Begin-durable.
- Update `IsPostDurablePhase` correspondingly: now returns true for everything except `Begin`.
- The plan **modifies these existing predicates in place** rather than adding a new `IsKnownPostBeginPhase` helper — the existing `IsPreDurablePhase`/`IsPostDurablePhase` pair is sufficient to express the new classification. The `IsKnownPostBeginPhase` symbol referenced in the dispatch sketch below is **a documentation alias for `IsPostDurablePhase`** in the new classification (because post-Begin-durable == post-durable). At implementation time, the dispatch sketch's `MergeJournal.IsKnownPostBeginPhase(phase)` can be inlined to `MergeJournal.IsPostDurablePhase(phase)` for the same behavior. The two names are kept distinct in the plan text to emphasize the rename of the semantic ("Durable1Done" was the prior boundary; "Begin" is the new boundary).

This is a **wider change than just adding Split** — Supersede / Tombstone / Finalize used to be pre-Durable1 (rolled back on crash). Under the new classification they would drive forward on crash. Audit `RollBack` and `CompleteFromPostDurable` (in `MergeJournalOrchestrator.cs`) to verify each post-Begin phase is idempotent on forward-completion:

- **Split**: idempotent per Step 2.3 (detects existing TIP, skips re-creation, idempotent retag for each step).
- **Supersede**: `SupersedeCommit.AppendRelations` already skips existing relations (`SupersedeCommit.cs:265-271`), so re-running is a no-op.
- **Tombstone**: `SupersedeCommit.CommitTombstones` walks ledger actions and writes `LedgerTombstone` rows; duplicate-write is guarded by relation-id existence check (verify the helper at `TombstoneAttributionHelper.cs`).
- **Finalize**: `FlipMergeStateAndClearTransient` rewrites MergeState fields; re-running with the same values is a no-op.

If any post-Begin phase turns out NOT to be idempotent, the alternative is to keep that phase pre-Durable1 by extending the barrier to a per-phase decision rather than a single Split-vs-rest split. Re-audit and decide at implementation time. **Verify before merging this PR that every post-Begin phase's RunMerge step is idempotent.**

**RunMerge change:**

File: [Source/Parsek/MergeJournalOrchestrator.cs](../../../Source/Parsek/MergeJournalOrchestrator.cs)

Modify `RunMerge` after line 199 (`MaybeInject(Phase.Begin)`) and before line 202 (`AppendRelations`):

```
// New step 1.5: split the origin recording at the rewind UT if it spans
// the rewind point. Mutates marker.SupersedeTargetId on success so the
// Supersede step's closure starts at TIP. The split is internally
// transactional: SplitOriginAtRewindUT owns its stack-local
// SplitSnapshot and uses a try/finally inside its own body to roll back
// in-memory mutations before throwing. The catch here only logs and
// re-throws; the actual rollback already ran inside the inner function.
SplitOriginResult splitResult;
try
{
    splitResult = RecordingTreeSplitter.SplitOriginAtRewindUT(marker, scenario);
}
catch (Exception ex)
{
    // Inner already rolled back via try/finally + RollBackInMemory(scenario, snapshot).
    ParsekLog.Warn(Tag, $"Split step threw {ex.GetType().Name}: {ex.Message} — inner rolled back; re-throwing");
    throw; // surfaces to the catch in CommitSupersede/RunMerge's caller;
           // journal phase is still Begin on disk → next load rolls back.
}
AdvancePhase(scenario, MergeJournal.Phases.Split);
DurableSave("split", persistSynchronously: true);
MaybeInject(Phase.Split);
```

`SplitOriginAtRewindUT`'s internal structure:

```
internal static SplitOriginResult SplitOriginAtRewindUT(
    ReFlySessionMarker marker, ParsekScenario scenario)
{
    var snapshot = new SplitSnapshot { ... };
    try
    {
        // Steps 1-13 as described above. Each mutation appends to snapshot.ledger.
        return new SplitOriginResult { Skipped = false, ... };
    }
    catch
    {
        RollBackInMemory(scenario, snapshot);
        throw;
    }
}

private static void RollBackInMemory(ParsekScenario scenario, SplitSnapshot snapshot)
{
    // Steps 1-5 of the rollback algorithm (remove TIP, swap origin reference,
    // restore ChainIndex map, walk ledger in reverse, bump StateVersion).
}
```

This keeps rollback ownership entirely inside `RecordingTreeSplitter` — `RunMerge`'s catch only logs and re-throws. The earlier draft of this plan inconsistently called `RollBackInMemory(scenario)` (one arg) from the outer catch as if a stack-local snapshot were reachable across the throw boundary; r6 puts the rollback responsibility inside the splitter where the snapshot lives.

Note: `RecordingTreeSplitter.RollBackInMemory(scenario, snapshot)` is a new helper that reverses the in-memory mutations of an incomplete `SplitOriginAtRewindUT` call. It's only invoked from the catch above — the post-durable contract means a successful Split has already been persisted by the time `MaybeInject(Phase.Split)` runs.

**Rollback approach — snapshot + incremental ledger combined.** `SplitAtUT`'s delegation to `SplitAtSection` performs wholesale in-place mutations on `original`: trims `original.Points` / `PartEvents` / `SegmentEvents` / `FlagEvents` / `TrackSections` / `OrbitSegments` past splitUT, transfers terminal state and end-resources / end-inventory / end-crew / `VesselSnapshot` to TIP, and `RecordingOptimizer.ReindexChain` may rewrite ChainIndex on every chain sibling. An incremental ledger cannot undo these. Therefore:

- **Before** Step 4's `SplitAtUT` call, snapshot `origin` via `Recording.DeepClone()` (`Recording.cs:734-853`; copies all persisted + transient fields including `CachedStats`/`LoopSyncParentIdx`/`PreReFlyAnchor*` lists; deep-copies `TrackSections` via `DeepCopyTrackSections` with `frames`/`bodyFixedFrames`/`checkpoints`). Also capture `chainSiblingsBefore = { rec.RecordingId → rec.ChainIndex }` for every recording in the same `TreeId` + `ChainId`. (ChainBranch is immutable post-creation in the split path; no need to snapshot it.)
- Carry these in a stack-local `SplitSnapshot { originClone, chainSiblingsBefore, tipAdded, ledger }` struct. `originClone` is null for the unsplit-yet case.
- **Step 5 onward** also appends to `SplitSnapshot.ledger`: each per-row mutation (BP reparent, debris reparent, action retag, milestone retag, TrackSection anchor rewrite) is a small record `{kind, target, oldValue, newValue}` reversible in O(1).
- On exception, `RollBackInMemory(scenario, snapshot)`:
  1. If `snapshot.tipAdded`: remove TIP from `RecordingStore.CommittedRecordings` and `tree.Recordings`.
  2. If `snapshot.originClone != null`: **replace** origin in `RecordingStore.CommittedRecordings` (find by id, swap reference to `originClone`) and in `tree.Recordings` (update dict entry). Reference-swap is simpler than field-copyback and equally safe because all backrefs to origin are by *string id*, not object reference — `BranchPoint.ParentRecordingIds`, `debris.DebrisParentRecordingId`, `action.RecordingId`, `marker.SupersedeTargetId` all hold strings, not Recording pointers. Object identity is preserved across the swap from the consumers' point of view because `originClone.RecordingId == origin.RecordingId`.
  3. Restore `chainSiblingsBefore` ChainIndexes.
  4. Walk `snapshot.ledger` in reverse and apply `oldValue` to each `target`.
  5. Decrement `RecordingStore.StateVersion` is NOT possible — once observers have read a bumped version they may have cached state. Instead, bump it AGAIN to invalidate any observer that read between mutation and rollback. (Logical no-op for clean snapshots; mandatory for clean rollback semantics.)
- On success, `SplitSnapshot` is discarded at function return.

**Codec safety**: `SplitSnapshot` and its `originClone` Recording instance are stack-local per-call objects that live only inside `SplitOriginAtRewindUT` — never assigned to `Recording`, `ParsekScenario`, or any persisted type. **Do not add `originClone` to `RecordingTreeRecordCodec`, sidecar codecs, `RecordingTree.SaveTo`/`LoadFrom`, or any other serialization path.** The earlier draft of this plan mentioned a `_splitInProgressMarker` field on Recording; that approach is rejected for codec-pollution risk. Transience is enforced by the snapshot never leaving the function scope.

Add a corresponding entry in the `Phase` enum near line 73 (if a separate test-injection enum exists; verify by reading lines 73-89 at implementation time).

**RunFinisher dispatch update:**

Modify `RunFinisher` (lines 309-312 region):

```
if (phase == MergeJournal.Phases.Begin)
{
    // Pre-Split rollback: clear marker, drop session-provisional recordings,
    // remove any session-provisional RPs. SplitOriginAtRewindUT never ran
    // durably so no split state to undo on disk.
    RollBack(scenario, journal, sessionId, phase);
    return true;
}

// Post-Split phases (Split, Supersede, Tombstone, Finalize, Durable1Done,
// RpReap, MarkerCleared, Durable2Done) drive forward via the existing
// CompleteFromPostDurable path. Split itself is re-entrant via
// SplitOriginAtRewindUT's idempotency check (Step 2.3).
if (MergeJournal.IsKnownPostBeginPhase(phase))
{
    try
    {
        return CompleteFromPostDurable(scenario, journal, sessionId, phase);
    }
    catch (Exception ex)
    {
        // Drive-forward failure is unrecoverable in-place: the save is
        // already mid-merge and we cannot safely return to pre-Begin.
        // Log a loud diagnostic so the player can take manual action
        // (e.g., delete the save's parsek scenario node). Re-throw so
        // the scenario load surface bubbles the failure rather than
        // continuing in an undefined state.
        ParsekLog.Warn(Tag,
            $"RunFinisher drive-forward FAILED at phase={phase}: {ex.GetType().Name}: {ex.Message}. " +
            $"Save is mid-merge and may be inconsistent. Manual recovery: copy save, " +
            $"clear scenario.ActiveMergeJournal and scenario.ActiveReFlySessionMarker, " +
            $"reload. sess={sessionId}");
        throw;
    }
}

// Unknown phase string — defensive rollback. A future phase added
// without updating IsKnownPostBeginPhase would land here.
ParsekLog.Warn(Tag, $"RunFinisher: unknown phase {phase} — rolling back defensively");
RollBack(scenario, journal, sessionId, phase);
return true;
```

Add a new predicate `MergeJournal.IsKnownPostBeginPhase(string phase)` that returns true for `Split / Supersede / Tombstone / Finalize / Durable1Done / RpReap / MarkerCleared / Durable2Done / Complete`. The defensive `else` branch protects against future phase additions made without updating this dispatcher — unknown phase strings roll back rather than blindly forward-completing into ambiguous state. **Note**: `MergeJournalOrchestrator.cs:321-326` already has unknown-phase rollback behavior; the new dispatcher must preserve that, not replace it. Verify at implementation time that the existing path still handles the case where neither `phase == Begin` nor `IsKnownPostBeginPhase(phase)` matches — the explicit `else RollBack` in the code block above is the same intent.

**`CompleteFromPostDurable` structural extension.** Today (`MergeJournalOrchestrator.cs:404-448`) the function only entry-points at `Durable1Done` (the existing post-durable boundary). The W2/W5 fix reclassifies Split/Supersede/Tombstone/Finalize as post-Begin-durable, so `CompleteFromPostDurable` needs new entry points for each of those phases. The function's body is a sequence of `if (phase == X) { do X work; AdvancePhase(Y); }` blocks; new blocks are inserted at the top in order so a resume at Split runs all subsequent steps, a resume at Supersede skips Split, etc.

Sketch of the extended structure (insert ABOVE the existing `Durable1Done` block at line 409):

```
if (fromPhase == MergeJournal.Phases.Split)
{
    // Re-run the split idempotently. SplitOriginAtRewindUT's Step 2.3
    // detects an existing TIP and skips re-creation; remaining retag
    // steps are idempotent via predicate-no-longer-matches.
    RecordingTreeSplitter.SplitOriginAtRewindUT(scenario.ActiveReFlySessionMarker, scenario);
    AdvancePhase(scenario, MergeJournal.Phases.Supersede);
    stepsDriven++;
}

if (journal.Phase == MergeJournal.Phases.Supersede)
{
    // Re-run AppendRelations. Skips existing rows via RelationExists at
    // SupersedeCommit.cs:265-271; safe to call twice.
    var subtree = SupersedeCommit.AppendRelations(
        scenario.ActiveReFlySessionMarker,
        ResolveProvisional(scenario), // helper that finds the fork by ActiveReFlyRecordingId
        scenario);
    // Cache subtree on the journal so the next phase can reuse it.
    journal.RecoveredSubtreeIds = subtree;
    AdvancePhase(scenario, MergeJournal.Phases.Tombstone);
    stepsDriven++;
}

if (journal.Phase == MergeJournal.Phases.Tombstone)
{
    // Re-run tombstone scan. CommitTombstones dedups via
    // alreadyTombstoned HashSet at SupersedeCommit.cs:1850-1856.
    SupersedeCommit.CommitTombstones(
        scenario.ActiveReFlySessionMarker,
        journal.RecoveredSubtreeIds ?? RebuildSubtree(scenario),
        ResolveProvisional(scenario)?.RecordingId,
        SafeNow(), DateTime.UtcNow.ToString("o"), scenario);
    AdvancePhase(scenario, MergeJournal.Phases.Finalize);
    stepsDriven++;
}

if (journal.Phase == MergeJournal.Phases.Finalize)
{
    // Re-run merge-state flip + transient clear. Idempotent.
    SupersedeCommit.FlipMergeStateAndClearTransient(
        scenario.ActiveReFlySessionMarker,
        ResolveProvisional(scenario), scenario, preserveMarker: true);
    AdvancePhase(scenario, MergeJournal.Phases.Durable1Done);
    DurableSave("finisher-durable1", persistSynchronously: false);
    stepsDriven++;
}
```

**Existing `Durable1Done` block at line 409 must change** — change the condition from `fromPhase == Phases.Durable1Done` to `journal.Phase == Phases.Durable1Done`. Today it's keyed on `fromPhase` (the entry value) because the only entry point was `Durable1Done`; after the reclassification, entries at Split/Supersede/Tombstone/Finalize advance `journal.Phase` through `Durable1Done` mid-function. Without this change, a recovery entering at Split (or any new entry) would never run `TagRpsForReap` + `ReapOrphanedRPs`, never advance to `RpReap`, never clear the marker, never emit the "End reason=merged" log, and leave orphan RPs in the save. The fix is one-line:

```
// Was: if (fromPhase == MergeJournal.Phases.Durable1Done)
if (journal.Phase == MergeJournal.Phases.Durable1Done)
{
    TagRpsForReap(scenario.ActiveReFlySessionMarker, scenario);
    ...
}
```

Notes for the implementer:
- `ResolveProvisional(scenario)` is a helper to add: lookup `scenario.ActiveReFlySessionMarker.ActiveReFlyRecordingId` in `RecordingStore.CommittedRecordings`. Existing pattern in `RewindInvoker`; copy.
- `journal.RecoveredSubtreeIds` is a NEW **transient (non-persisted)** field on `MergeJournal` to thread the closure across two `CompleteFromPostDurable` calls within the same load session. Type: `IReadOnlyCollection<string>` (matches `AppendRelations`' return type). **Add an XML doc comment at the field declaration: `// DO NOT add to SaveInto/LoadFrom; transient cross-block thread inside CompleteFromPostDurable only.`** Every other field on `MergeJournal` is persisted, so this convention break must be flagged to prevent a future implementer from naively wiring it into the codec. If the player re-loads mid-recovery the journal is re-read from disk fresh and the subtree is recomputed via `RebuildSubtree`.
- `RebuildSubtree(scenario)` is the fallback when `RecoveredSubtreeIds` is null on a fresh load. Implementation: recompute the subtree from the marker's current `SupersedeTargetId` (which is TIP if Split has completed durably) via `EffectiveState.ComputeSubtreeClosureInternal(marker, closureRoot)`. The walk is deterministic over `(marker, RecordingStore.CommittedRecordings, tree state)`; supersede relation additions don't affect closure membership, so recomputation produces the identical subtree.
- `SupersedeCommit.AppendRelations` has a four-arg overload; the sketch uses the three-arg form which delegates to the four-arg with `extraSelfSkipRecordingIds: null`. Use either at implementation.
- Each phase's failure surfaces to the outer try/catch in `RunFinisher` (above), which logs the user-impact diagnostic and re-throws.

**User impact of a drive-forward failure during scenario load.** The re-throw at the end of the catch block surfaces to `ParsekScenario.OnLoad`'s outer handler (`ParsekScenario.cs:2440-2445`), which already catches and re-throws. KSP's ScenarioModule loader logs the exception to Unity and continues loading other ScenarioModules in a degraded state. **Parsek will be unusable post-load until the user manually clears `scenario.ActiveMergeJournal` and `scenario.ActiveReFlySessionMarker` from their save file's `parsek` node** (or copies a pre-merge save). The loud `[Parsek][WARN]` message names the failing phase and includes the manual-recovery hint. Acceptable trade-off: silent corruption from forward-completing into ambiguous state would be far worse than a clearly-flagged broken save.

**Crash-safety summary:**

| Crash phase on disk | Resume action | Persistent state |
|---|---|---|
| Begin (pre-Split) | Rollback | Pre-Begin snapshot; no split |
| Split (post-Split DurableSave) | Drive forward, re-run idempotent steps from Split onward | Split mutations + marker.SupersedeTargetId=TIP on disk |
| Supersede / Tombstone / Finalize (pre-Durable1) | Drive forward, re-run idempotent steps | Split on disk; supersede rows may be partial |
| Durable1Done onward | Drive forward (existing path) | Full Phase-10 invariant |

Document this in [MergeJournalOrchestrator.cs](../../../Source/Parsek/MergeJournalOrchestrator.cs)'s class-level XML comment: "Split is post-durable; pre-Split phases roll back, Split-and-after drive forward via idempotent re-run."

### Step 6 — RewindInvoker priorTip composite walker

File: [Source/Parsek/RewindInvoker.cs:1124](../../../Source/Parsek/RewindInvoker.cs)

Change:
```
string priorTip = selected.EffectiveRecordingId(scenario.RecordingSupersedes);
```

to:
```
string priorTip = EffectiveState.EffectiveTipRecordingId(
    selected.OriginChildRecordingId, scenario.RecordingSupersedes);
```

This is the load-bearing line for nested-Re-Fly correctness: priorTip now traces HEAD → (chain) → TIP → (supersede) → fork, picking up fork1 when the player invokes a second Re-Fly on the same slot.

(`ChildSlot.EffectiveRecordingId` is also rewired in Step 4 to delegate to the composite, so this could equivalently call `selected.EffectiveRecordingId(...)` — pick one to keep readable.)

### Step 7 — Tests

#### 7a. Unit tests in [Source/Parsek.Tests/RecordingTreeSplitterTests.cs](../../../Source/Parsek.Tests/RecordingTreeSplitterTests.cs) (new file)

Eight cases:

1. `SplitOriginAtRewindUT_OriginSpansRewindUT_SplitsHeadAndTip`
   - Recording UT [8, 53], rewindUT 34.
   - Assert: HEAD UT [8, 34], TIP UT [34, 53], TIP.RecordingId != origin.RecordingId, HEAD.ChainId == TIP.ChainId, HEAD.ChainIndex == 0, TIP.ChainIndex == 1.
   - Assert: HEAD.TerminalStateValue == null, TIP.TerminalStateValue == origin's pre-split terminal.
   - Assert: marker.SupersedeTargetId == TIP.RecordingId.

2. `SplitOriginAtRewindUT_OriginEntirelyPreRewind_NoSplit`
   - Recording UT [8, 30], rewindUT 34.
   - Assert: result.Skipped = true, no new recording added, marker unchanged.

3. `SplitOriginAtRewindUT_OriginEntirelyPostRewind_NoSplit`
   - Recording UT [40, 53], rewindUT 34.
   - Assert: result.Skipped = true, no new recording added.

4. `SplitOriginAtRewindUT_NaNRewindUT_NoSplit`
   - marker.RewindPointUT = NaN.
   - Assert: result.Skipped = true with reason indicating NaN cutoff.

5. `SplitOriginAtRewindUT_BPsReparented`
   - Origin with 3 BPs at UT 20, 34, 40 (one matches rewindUT exactly).
   - Assert: BP at UT 20 keeps origin (HEAD) as parent; BPs at UT 34 and 40 reparent to TIP.
   - Verify the eq-UT BP goes to TIP per the "edge" rule in Step 2.6.

6. `SplitOriginAtRewindUT_DebrisReparented`
   - Origin with 4 debris children: 2 at UT 22 (pre-rewind), 2 at UT 40 (post-rewind).
   - Assert: 2 pre-rewind debris stay parented to HEAD; 2 post-rewind debris reparent to TIP.

7. `SplitOriginAtRewindUT_LedgerActionsRetagged`
   - 3 ledger actions tagged to origin at UT 10, 20, 50.
   - Assert: UT 10 & 20 stay tagged HEAD (= origin.RecordingId); UT 50 is retagged to TIP.RecordingId. `Ledger.StateVersion` incremented.

8. `SplitOriginAtRewindUT_PartialResumeIdempotent`
   - Simulate a crash post-Step 5 (TIP added) but pre-Step 6 (BPs not yet reparented). Re-run `SplitOriginAtRewindUT`.
   - Assert: idempotency check detects existing TIP, proceeds to finish BP reparent / debris reparent / action retag / marker mutation. Counters report `Skipped=false` but `BpReparented` only counts the unfinished work.

9. `SplitOriginAtRewindUT_OrbitSegmentStraddlesRewindUT_NoSplit`
   - Origin spans rewindUT but has an OrbitSegment with `startUT < rewindUT < endUT`.
   - Assert: result.Skipped = true with reason `SplitAtUT_Guarded`. `marker.SupersedeTargetId == origin.RecordingId` (unchanged). No new recording added. Warn log present.
   - Locks in W3 fallback behavior.

10. `SplitOriginAtRewindUT_OriginIsEVARecording_SplitsAndPreservesEVALinkage`
    - Origin has `EvaCrewName = "Bob"`, `ParentRecordingId = parentVesselId`. Spans rewindUT [8, 53].
    - Assert: HEAD and TIP both carry `EvaCrewName = "Bob"` and `ParentRecordingId = parentVesselId` (SplitAtSection copies both at lines 1003-1005). Both halves are still classified as EVA recordings. Supersede `TIP → fork` writes a row; HEAD stays visible.
    - Verifies I8: EVA chains through the split correctly. Mention in the test docstring: kerbal-reservation tests for EVA chains live in `KerbalsModuleTests`; this fixture asserts the *structural* preservation only.

11. `SplitOriginAtRewindUT_ChildBranchPointIdMovesPerRewindUTRule`
    - Origin's `ChildBranchPointId` points at a BP with `bp.UT = 20` (pre-rewind, rewindUT=34).
    - Assert: HEAD.ChildBranchPointId == bp.Id (kept, pre-rewind); TIP.ChildBranchPointId == null. Locks in W4 fix.
    - Second sub-case: Origin's ChildBranchPointId points at a BP with `bp.UT = 34` (== rewindUT). Assert: HEAD.ChildBranchPointId == null; TIP.ChildBranchPointId == bp.Id (moved). BP's `ParentRecordingIds` updated from origin to tip.

12. `SplitOriginAtRewindUT_MilestonesRetaggedByUT`
    - Origin has 3 milestones: FirstLaunch (UT 9), RecordsAltitude (UT 22), CrashedAt52 (UT 52).
    - Run split with rewindUT 34.
    - Assert: FirstLaunch + RecordsAltitude keep RecordingId = origin.RecordingId (= HEAD's id). CrashedAt52's RecordingId = tip.RecordingId. MilestoneStore version bumped. Locks in I2.

#### 7b. Composite walker tests in [Source/Parsek.Tests/EffectiveStateCompositeWalkerTests.cs](../../../Source/Parsek.Tests/EffectiveStateCompositeWalkerTests.cs) (new file)

Five cases:

1. `EffectiveTipRecordingId_SupersedeOnly_WalksSupersede` — no chains, A→B→C in supersede; assert composite returns C.
2. `EffectiveTipRecordingId_ChainOnly_WalksChainTip` — three chain segments A→B→C via ChainIndex, no supersede; assert composite returns C.
3. `EffectiveTipRecordingId_ChainThenSupersede_WalksBoth` — chain A→B (B is chain tip), supersede B→C; assert composite returns C.
4. `EffectiveTipRecordingId_SupersedeIntoChain_WalksBoth` — supersede A→B, then B has a chain tip C; assert composite returns C.
5. `EffectiveTipRecordingId_CycleDetected_ReturnsLastVisited` — pathological cycle A→B→A in supersede; assert composite returns A (or B, document the policy) with a Warn log.
6. `EffectiveTipRecordingId_CrossEdgeCycle_ReturnsLastVisited` — pathological cross-edge cycle: A and B share ChainId+ChainBranch (B is chain tip of A), AND a supersede row `B → A` exists. Walking A: chain-hop → B; supersede-hop `B → A` is rejected by the cycle guard (A already visited); returns B with a Warn log. Verifies the single shared `visited` set spans both hop kinds.

#### 7c. Existing test rebases

- [SupersedeCommitTests.cs](../../../Source/Parsek.Tests/SupersedeCommitTests.cs): the existing fixtures continue to pass — `AppendRelations` now runs against a post-split tree, but the test setup builds recordings directly without the split path. Add an integration-style test `AppendRelations_AfterSplit_CarvesOutHead` that builds origin pre-split, runs `SplitOriginAtRewindUT`, then runs `AppendRelations`, and asserts a single row `TIP → fork` (no HEAD row).
- [SessionSuppressedSubtreeTests.cs](../../../Source/Parsek.Tests/SessionSuppressedSubtreeTests.cs): existing closure-walk tests should pass unchanged — closure walks chain siblings and includes HEAD; HEAD is filtered at the write-set stage, not the closure stage.
- [MergeCrashedReFlyCreatesCPSupersedeTest.cs](../../../Source/Parsek/InGameTests/MergeCrashedReFlyCreatesCPSupersedeTest.cs): the in-game live-scene test asserts `expectedAdded`. The signature of `SupersedeCommit.IsPreRewindDebris` is preserved via the wrapper (Step 3), so this test compiles as-is. **But** in scenarios where the live test produces an origin that spans the rewind UT, the test's `expectedAdded` calculation needs to account for HEAD being carved out. Update the loop to call the new `IsPreRewindCarveOut` with the new signature, OR (simpler) build the live test scenario such that the origin is wholly post-rewind so the existing assertion shape holds.
- [InvokeRPStripAndActivateTest.cs](../../../Source/Parsek/InGameTests/InvokeRPStripAndActivateTest.cs) (~ line 213): this test reads `slot.EffectiveRecordingId(supersedes)` and compares against `rec.RecordingId`. After Step 4's rewire, that call returns the chain-walked + supersede-walked tip rather than the pure-supersede tip. Test fixtures that don't set up chain linkage get the same answer (chain hop is a no-op when `ChainId` is null). Fixtures that DO set up chain linkage get the chain-tip answer. **Audit the fixture setup**: if `slot.OriginChildRecordingId`'s recording has a `ChainId`, the assertion needs to expect the chain tip. If the fixture does NOT set ChainId, no change.
- Any other in-game/unit test calling `ChildSlot.EffectiveRecordingId` or `EffectiveState.EffectiveRecordingId` — grep before merging to confirm no surprise breakages.

#### 7d. Bug-repro acceptance test (in-game)

New in [Source/Parsek/InGameTests/RuntimeTests.cs](../../../Source/Parsek/InGameTests/RuntimeTests.cs):

`ReFlyFromSpannedRecording_PreservesLaunchRowAndTombstonesPostRewindCrew`:
1. Programmatically install a tree with a single recording covering UT [8.42, 52.7], terminal=Destroyed, one on-board kerbal with `KerbalEndState=Dead`.
2. Build a marker with `RewindPointUT=34.24` and a provisional fork at UT 34.5 with terminal=Landed.
3. Call `MergeJournalOrchestrator.RunMerge`.
4. Assert:
   - Two recordings in the tree: HEAD `[8.42, 34.24]` and TIP `[34.24, 52.7]`, sharing ChainId.
   - HEAD is visible in `EffectiveState.ComputeERS()`; TIP is hidden (superseded by fork).
   - The kerbal's `Dead` action has been retagged to TIP, then tombstoned. ELS does not credit the kerbal as Dead → roster shows the kerbal as alive.
   - `TimelineBuilder.Build(…)` produces a Start entry for HEAD ("Launch: Kerbal X from Launch Pad").
   - `ChildSlot.EffectiveRecordingId(supersedes)` for the slot returns the fork (not HEAD).
   - Watch button via `IsWatchButtonEnabled(hasGhost=true, sameBody=true, inRange=true, isDebris=false) == true` — modulo Bug 1's separate playback-window issue.

### Step 8 — Documentation

- [CHANGELOG.md](../../../CHANGELOG.md): one-line entry under "Unreleased" → `Re-Fly now correctly preserves the pre-rewind portion of recordings that span the rewind point. The origin is split at the rewind UT into a visible HEAD (launch portion) and a superseded TIP (replaced by the new fork).`
- [docs/dev/todo-and-known-bugs.md](../../../docs/dev/todo-and-known-bugs.md): mark the Bug 2 repro (Kerbal X (2) missing from timeline) as fixed by this PR; add a note that Bug 1 (Watch button greyed out for short crashed recordings whose playback window has closed) remains open and is separate.
- [.claude/CLAUDE.md](../../../.claude/CLAUDE.md): add `EffectiveTipRecordingId` to the "Key source files" cheatsheet entry for `EffectiveState.cs` (one line).
- [SupersedeCommit.cs](../../../Source/Parsek/SupersedeCommit.cs) class XML docstring (lines 7-35): mention the SplitAtRewindUT pre-step and the carve-out generalization.
- [MergeJournalOrchestrator.cs](../../../Source/Parsek/MergeJournalOrchestrator.cs) class XML docstring: mention the new `Split` phase and the "forward-completion" reclassification of Supersede/Tombstone/Finalize (all post-Begin phases now drive forward, only Begin rolls back).
- **[docs/parsek-rewind-to-separation-design.md](../../parsek-rewind-to-separation-design.md)**: the phase classification table (lines ~682-694) currently documents `Begin/Supersede/Tombstone/Finalize` as Rollback. The W2/W5 fix reclassifies Supersede/Tombstone/Finalize to forward-completion and adds Split. Update:
  - The phase rows for `Begin` (stays Rollback), add new row for `Split` (forward-completion via SplitOriginAtRewindUT idempotency), change `Supersede`/`Tombstone`/`Finalize` rows from Rollback to forward-completion (via AppendRelations/CommitTombstones/FlipMergeStateAndClearTransient idempotency).
  - The `IsPreDurablePhase`/`IsPostDurablePhase` summary text at line 694.
  - Any rationale paragraph explaining the original rollback choice for those phases — replace with the new "forward-completion via idempotent re-run, post-Split-durable barrier" rationale.

## Edge cases and risks

1. **Rewind UT exactly on a TrackSection boundary.** `SplitAtUT`'s find-or-insert step detects this via `Math.Abs(section.startUT - splitUT) < epsilon` and reuses the existing boundary. No synthetic section is inserted. Test 1 above should include a variant with this exact alignment.

2. **Rewind UT exactly on a BranchPoint UT.** Per Step 2.6, treat as belonging to TIP. The BP at the rewind UT reparents to TIP. The BP's `RewindPointId` (if set, identifying the RP that captured this state) still resolves correctly because the RP itself is unchanged.

3. **Origin already chain-split before merge time.** If the optimizer had already split origin into env-class segments before the Re-Fly invocation, `selected.OriginChildRecordingId` names the HEAD of THAT chain. The marker's `SupersedeTargetId` (set in `RewindInvoker.AtomicMarkerWrite` from `priorTip` which under the new composite walker reaches the chain tip via env-split chain-walking) will name the env-split TIP. `SplitOriginAtRewindUT` then operates on that TIP — if it spans the rewind UT, sub-split it (the new HEAD ends at rewindUT, the new TIP carries the terminal state). Increment ChainIndex from the env-split TIP's existing ChainIndex. Verified-safe by Test 8 in 7a.

4. **Nested Re-Fly.** Player Re-Flies origin → fork1; then re-Re-Flies the same slot:
   - First merge: `SplitOriginAtRewindUT` produces HEAD + TIP1, supersede `TIP1 → fork1`.
   - Second merge: `RewindInvoker.AtomicMarkerWrite` calls `EffectiveTipRecordingId(slot.OriginChildRecordingId = HEAD, supersedes)` → chain-hop HEAD → TIP1 → supersede-hop TIP1 → fork1 → no more edges → returns fork1. priorTip = fork1, SupersedeTargetId = fork1. Closure walk starts at fork1.
   - `SplitOriginAtRewindUT` runs on fork1. At this point **`fork1.ChainId == null`** (fork recordings are allocated via `RewindInvoker.BuildProvisionalRecording` at `RewindInvoker.cs:1457-1481` and that constructor does not set ChainId — it stays default-null until something assigns it). Step 2.5's "if `origin.ChainId` is empty, assign a new ChainId to BOTH" rule fires: a fresh Guid `Y` is assigned to fork1 (now HEAD2) and to the newly-split TIP2. Chain X (HEAD ↔ TIP1) and chain Y (HEAD2 ↔ TIP2) are independent.
   - After the second merge: supersede chain `TIP1 → fork1 (HEAD2)` AND `TIP2 → fork2`. Plus chains `HEAD ↔ TIP1` and `HEAD2 ↔ TIP2`.
   - Composite walker on slot.OriginChildRecordingId = HEAD: chain X → TIP1 → supersede → fork1 (= HEAD2) → chain Y → TIP2 → supersede → fork2 → return fork2.
   - **Verified algorithmically clean**. Test 1 in 7b plus a new explicit nested case in 7d (a 3-stage Re-Fly chain) would lock this in.

5. **In-place continuation (rare).** When the marker's `OriginChildRecordingId == ActiveReFlyRecordingId`, the in-place self-link guard in `AppendRelations` (line 230-248) skips the row. After the new split, marker's `SupersedeTargetId = TIP` and `ActiveReFlyRecordingId = fork`. These are distinct, so the in-place self-link guard doesn't fire. (If a future change reintroduces in-place semantics post-split, audit this; currently the path is dormant.)

6. **The split mutates ledger actions in memory.** `Ledger` is a static class whose `actions` list is persisted via the scenario save path. The journal-driven `DurableSave("durable1")` at line 224 of `MergeJournalOrchestrator` already persists the scenario (and thus the ledger) after Supersede + Tombstone + Finalize; the retag survives the durable barrier. If the player force-quits between Begin and Durable1, the next load picks up the journal in `Split` phase and re-runs the split idempotently (the retag is reapplied).

7. **TrackSection split for the v11/v12/v13 debris parent-anchor contract.** The post-rewind portion of `94806c0b` may have TrackSections that reference an `anchorRecordingId` (Relative-frame sections). These were authored at recording time and named the live PID's recording, which is HEAD's (= origin's) id. After split, TIP-side Relative sections that name origin.RecordingId as their anchor still mean "anchored to this same physical vessel" — which is now TIP. Step 2.8 rewrites them. **Loop-anchored** Relative sections (Recording.LoopAnchorVesselId / LoopAnchorRecordingId) reference the loop anchor, which is external; no change needed.

8. **No backward compatibility for existing saves with pre-fix supersede rows.** Per the project's pre-1.0 no-migration policy ([feedback memory: no-recording-compat]), saves that already have a whole-recording supersede row written by the pre-fix code keep it. On load, the carve-out doesn't retroactively fix them. The launch row stays hidden for that one save. Acceptable.

9. **Trajectory point interpolation at exactly the rewind UT.** `SplitAtSection` already interpolates a synthetic boundary point if `splitUT` falls between two sample UTs (lines 797-848). For mission 2's repro, the sampler captured a point right at the breakup event UT 34.24, so no interpolation needed. For other cases, the interpolated point is `t`-lerped on lat/lon/alt/rotation/velocity/resources — same fidelity as the existing env-split path.

10. **What if `marker.RewindPointUT` is approximate (e.g., post-PR-#858 drift-immunity capture was rounded)?** The PidPeerStartUtEpsilonSeconds = 0.05s tolerance absorbs sampler jitter at the boundary. The split UT lands at `marker.RewindPointUT` exactly; the cutoff used for HEAD/TIP classification is `RewindPointUT - epsilon`.

## Out of scope (separate work items)

- **Bug 1** (Watch button greyed because the short crashed recording's playback window closed before the player returned to KSC). Different root cause in the ghost playback engine. Filed separately.
- **Re-naming Re-Fly UI semantics** — conversation only.
- **Cross-tree supersede chains** — the existing TODO at `EffectiveState.cs:90` flags this as Phase 3+ work.

## Acceptance criteria

After this PR lands, replay the 2026-05-16 user repro and verify:

1. Mission 2 launches off the pad, crashes mid-flight. Player returns to KSC. Recordings table shows Kerbal X and Kerbal X (2) (HEAD's row). Watch button on Kerbal X (2) is enabled (subject to Bug 1).
2. Player clicks Re-Fly. Fork plays out. Player returns to KSC.
3. Recordings table now shows: Kerbal X (mission 1), Kerbal X (2) (HEAD only — the U+L launch row), and the new fork. **Three rows**, not two.
4. Timeline window: Start entries for **both** Kerbal X and Kerbal X (2) at their respective launch UTs.
5. Kerbal roster: the kerbal who was on Kerbal X (2) is alive (the Dead action moved to TIP, got tombstoned).
6. Re-Fly the same slot a second time: priorTip resolves to fork1, marker correctly attaches to fork1, split fires again on fork1, fork2 is created. No duplicate slot tip drift.
7. RP reap: after the first merge, the RP captured at UT 34.24 is reaped (because the slot resolves to fork via the composite walker, fork is the canonical tip, RP is no longer needed).
8. Tracking station: only HEAD and fork show as ProtoVessels for the slot in the overlap UT window. TIP is suppressed via `IsSupersededByRelation`.

Log assertion: a Re-Fly commit run produces a `[Parsek][INFO][Splitter] Split origin … at UT=34.24: HEAD=[8.42..34.24] (kept), TIP=… [34.24..52.7]` line, followed by the `[Supersede] Added 1 supersede relations for subtree rooted at <TIP_id>` line, with `skippedPreRewindCarveOut=1` (HEAD).
