# Plan: Safe discard of in-flight committed-restore clones + auto-discard no-op committed-mission resumes

## Context

Follows `autodiscard-noop-switch-segment.md`. The no-op auto-discard shipped
Standalone-only because auto-discarding a **committed-restore clone** in-flight
caused total mission data loss (2026-06-14 playtest, a 13-recording "Kerbal X"
mission deleted; recovered from a snapshot). The dominant use case — Fly to a
vessel Parsek already tracks, do nothing, leave/switch — IS a committed clone,
so the feature currently does not help it (it falls back to a merge dialog).

This plan (a) fixes the underlying data-loss bug so an in-flight committed-clone
discard reverts cleanly, then (b) extends the no-op auto-discard to committed
missions in both hooks.

## Verified invariant (copy-on-write)

`ParsekFlight.TryTakeCommittedTreeForSpawnedVesselRestore` (`ParsekFlight.cs:14476`)
`DeepClone`s the committed tree into the live `activeTree` and arms a
(non-durable) restore attempt; it **does NOT remove the committed original from
`RecordingStore.committedTrees`** (`:14497-14521`). The original stays in
`committedTrees` the entire in-flight session. It is only removed later, in
OnLoad's `ParsekScenario.TryRestoreActiveTreeNode` → `RemoveCommittedTreeById`,
when the clone has been serialized as `isActive` and is being restored. **So if
the live clone is torn down (activeTree nulled) before it is ever written
`isActive`, the committed original is never removed and survives untouched.**

## The bug (lifecycle)

1. Take: clone becomes `activeTree`; original stays in `committedTrees`.
2. In-flight discard of the clone's no-op segment → `DiscardPriorAndSwitchTo`
   (`MapFocusObjectOnSelectPatch.cs`) → `RecordingStore.TryDiscardActiveSwitchSegmentAttempt`.
   The committed-restore-clone branch only drops the clone when it is in the
   PENDING slot (`pendingTree.Id == committedTreeRestoreAttemptTreeId`). In-flight
   the clone is the live `activeTree`, not pending → `droppedPendingClone=False`
   → **the live clone is left intact**, and the caller switches vessels without
   tearing it down.
3. The switch serializes the clone as `isActive`; OnLoad removes the committed
   original and stashes the clone into the Limbo pending slot; it times out
   waiting for the discarded vessel.
4. The stranded Limbo tree surfaces as a whole-tree merge dialog; Discard →
   `DiscardPendingTree` deletes the entire mission (the `IsCommittedRecordingId`
   guard is defeated because the original was already removed in step 3).

The SCENE-EXIT manual discard is safe only by ordering: it stashes the clone
into the pending slot FIRST, so the discard sees `droppedPendingClone=True`,
drops the clone, and the original (never removed) survives.

## Fix

### Goal 1 (data-loss fix) — a shared helper tears down the still-live in-flight clone

A new `ParsekFlight` helper, used by every PRE-STASH discard path (in-flight
manual pre-switch Discard, in-flight no-op auto-discard, and scene-exit Hook 1's
committed-clone path):

```
internal SwitchSegmentDiscardDisposition DiscardActiveSwitchSegmentAttemptRevertingLiveClone(
    reason, screenMessage, ledgerRecalcReason)
{
    // Capture the live committed-restore-clone tree id BEFORE the discard
    // clears the restore attempt (SHOULD-FIX #1: tear down ONLY this exact tree).
    string cloneId = (activeTree != null
        && RecordingStore.IsCommittedTreeRestoreAttemptTree(activeTree.Id))
        ? activeTree.Id : null;

    var disposition = RecordingStore.TryDiscardActiveSwitchSegmentAttempt(out _);
    // ^ prunes the fresh-GUID segment subtree, clears the session AND the
    //   restore attempt (SHOULD-FIX #2 handled here), deletes ONLY segment sidecars.

    // In-flight (pre-stash) the clone is the live activeTree, not the pending
    // slot, so TryDiscard leaves it intact (droppedPendingClone=False). Tear it
    // down so it is never serialized isActive; the committed original survives in
    // committedTrees (copy-on-write — verified). Guarded to the captured clone id
    // so we never null an unrelated activeTree.
    if (cloneId != null && activeTree != null
        && string.Equals(activeTree.Id, cloneId, StringComparison.Ordinal))
    {
        AutoDiscardActiveTreeWithMessage(reason, screenMessage, ledgerRecalcReason);
    }
    return disposition;
}
```

- **Reuse** `ParsekFlight.AutoDiscardActiveTreeWithMessage` →
  `AutoDiscardActiveTreeCore` (`ParsekFlight.cs:2417/2529`): in-memory teardown
  only (stops continuations / gloops, ForceStops + nulls the recorder, discards
  the BG recorder, nulls `activeTree`, clears the session, ledger recalc). The
  review **verified it deletes NO files** and never touches `committedTrees`, so
  the committed original (which shares recording IDs / sidecar paths with the
  clone) is untouched. Same teardown the idle-on-pad path already runs on a live
  committed clone without data loss (proven precedent).
- New `RecordingStore.IsCommittedTreeRestoreAttemptTree(treeId)` accessor
  (`committedTreeRestoreAttemptTreeId == treeId`).
- Keep the existing `ActiveMergeJournal` / `ActiveReFlySessionMarker` guards in
  the callers (the ledger recalc must not race a journal finisher).
- Standalone is NOT torn down by this helper (cloneId is null for it) — it keeps
  its existing path; the empty post-prune activeTree is harmless (commits to
  nothing). This avoids churning the working standalone path.

This alone closes the data-loss path (the clone is never written `isActive`, so
OnLoad never removes the committed original, so the Limbo strand is never
entered). The `MapFocusObjectOnSelectPatch.DiscardPriorAndSwitchTo` call site
swaps its bare `TryDiscardActiveSwitchSegmentAttempt` for this helper.

### Goal 2 (DROPPED per plan-review)

A `DiscardPendingTree` re-commit guard was considered but **dropped**:
`MergeState.Immutable` is the DEFAULT for every freshly-created or loaded
recording (`MergeState.cs:15-17`), so "Immutable AND absent from committedTrees"
also matches a genuinely-never-committed pending tree the user deliberately
discarded — an auto-re-commit there would RESURRECT a tree the user meant to
delete (the inverse data-integrity bug), and `DiscardPendingTree` has many
legitimate callers (quickload-revert, Re-Fly discard, normal discard). With Goal
1 the strand is unreachable anyway. A reliable "was-once-committed" signal does
not exist today; a future belt-and-suspenders guard (if wanted) should be
NON-destructive (refuse-to-delete + warn + leave in Limbo for manual recovery),
tracked as a separate item. Not in this PR.

### Extension — auto-discard no-op committed-mission resumes

With Goal 1, tearing down a live committed clone is safe (original survives), so
the no-op auto-discard can cover committed clones:

- **Hook 1 (scene exit):** for a `CommittedRestoreClone` no-op, tear down the
  clone (the SAME `AutoDiscardActiveTreeCore` path used for Standalone — the
  committed original survives in `committedTrees`) instead of deferring to the
  dialog. `BgMemberOrMixed` still defers (the rest of a live tree must commit).
  Generalize `AutoDiscardNoOpStandaloneSwitchSegment` to also clear the restore
  attempt, and accept Standalone OR CommittedRestoreClone.
- **Hook 2 (in-flight re-switch):** allow the no-op auto-discard for
  `CommittedRestoreClone` (not just Standalone), routing through the
  now-fixed `DiscardPriorAndSwitchTo` (Goal 1 tears the clone down safely).
  `BgMemberOrMixed` still defers.

Net player experience: Fly to a tracked-mission vessel, do nothing, leave/switch
→ the boring resume segment is silently dropped and the committed mission is
preserved, with no dialog. (The boring segment was never going to add visual
value; the mission is untouched.)

## Edge cases

- Committed clone where the segment DID something (burn / dock / etc.) → not a
  no-op → kept → normal commit (re-commits the clone with the new segment). The
  classifier already gates this.
- `BgMemberOrMixed` (a live tree with other content beyond the segment) → still
  deferred in both hooks; tearing down the whole tree would lose the other live
  content.
- Manual Discard of a committed clone that DID something → still routes through
  the fixed `DiscardPriorAndSwitchTo`; tearing the clone down reverts to the
  committed original (the user explicitly chose Discard, so dropping the new
  segment is intended). Goal 1's teardown applies regardless of no-op.
- Re-Fly / merge-journal active → both hooks already guard (keep).
- Standalone unchanged (no committed original; teardown drops the throwaway).

## Files touched

- `Source/Parsek/Patches/MapFocusObjectOnSelectPatch.cs` — Goal 1 teardown in
  `DiscardPriorAndSwitchTo`; Hook 2 extension (allow CommittedRestoreClone).
- `Source/Parsek/ParsekFlight.cs` — generalize the no-op teardown entry point to
  Standalone + CommittedRestoreClone (clear restore attempt); Hook 1 extension.
- `Source/Parsek/SceneExitInterceptor.cs` — Hook 1: stop deferring
  CommittedRestoreClone; route it to the teardown.
- (Goal 2, if kept) `Source/Parsek/RecordingStore.cs` — `DiscardPendingTree`
  re-commit guard.
- Docs: CHANGELOG (the feature now covers tracked missions), todo (close the
  open data-loss bug; update the feature limitations), the two plan docs.

No schema change.

## Tests

- xUnit (`SwitchSegmentDiscardScopeTests` / a new class): the IN-FLIGHT committed
  clone case (clone as live `activeTree`, NOT pending) — assert after the
  discard+teardown: `activeTree` nulled, committed original still in
  `committedTrees`, all original IDs still `IsCommittedRecordingId==true`,
  session + restore attempt cleared. (Mirrors the existing pending-slot test at
  `SwitchSegmentDiscardScopeTests.cs:563`, with the clone live.)
- xUnit: disposition gate — `TryClassifyActiveSwitchSegmentNoOp` returns
  `CommittedRestoreClone` for a clone scenario (needs a restore-attempt arm
  helper or the source-gate pattern).
- xUnit (Goal 2, if kept): a sealed-committed Limbo tree absent from
  `committedTrees` is RE-COMMITTED by `DiscardPendingTree` (no sidecar delete);
  a `NotCommitted` pending tree is still fully discarded (negative).
- In-game: Fly to a committed-mission vessel, warp/do-nothing, leave → no
  dialog, mission intact, no boring segment committed. And the same with a
  real change → segment kept.

## Out of scope

- The `BgMemberOrMixed` in-flight/scene-exit auto-discard (defer — needs the
  rest-of-tree-still-commits handling).
- The consume-site `superseded-by-new-switch` clear (`ParsekFlight.cs:~8898`):
  a second rapid switch to a different target clears the prior session without a
  discard/teardown. Goal 1 does NOT cover this path; the subsequent branch
  selection usually overwrites `activeTree`, and it remains the documented S1
  minor gap. Don't let "closes the data-loss path" read as total — it closes the
  `DiscardPriorAndSwitchTo` + Hook-1 paths.
- Recording resource/crew transfer (separate feature, already documented).
