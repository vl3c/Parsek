# Fix Plan: Orphan Parent-Anchored Debris After Recording Retirement

Date: 2026-05-19

Worktree: `C:\Users\vlad3\Documents\Code\Parsek\Parsek-fix-orphan-debris-on-retirement`

Branch: `fix-orphan-debris-on-retirement`

Base: `origin/main` at `ad73ec5f` (post-PR #909 merge).

Evidence bundle:
`C:\Users\vlad3\Documents\Code\Parsek\logs\2026-05-19_2329_pr909-narrowed-gate-playtest`

## Problem Statement

After a re-fly session is retired ("rewound-out-supersede-fork"), the retired
recording's parent-anchored debris children remain visible as ghosts. The
visible result is duplicate upper-stage debris ghosts: the restored recording's
children render, and the retired recording's children also render at similar
trajectories.

### Evidence

Playtest save: `logs/2026-05-19_2329_pr909-narrowed-gate-playtest/saves/x4/persistent.sfs`.

Tree `42135b7d7cb045b384eba35b13bdecd6` (Kerbal X slot-0 lineage) contains:

- `f4c6f48913f444e9afc3ad17bb1db548` (oldest launch). 6 children with
  `debrisParentRecordingId = f4c6f489...`.
- `ab1f54b089f54312b02add0aa049e156` (restored after retirement). 3 children
  with `debrisParentRecordingId = ab1f54b0...`.
- `rec_2c68978d84054474b804c579c92f5d40` (re-fly provisional, retired via
  `rewound-out-supersede-fork`). 1 child `3d4713df2ba449d99455de98db3085f4`
  (Kerbal X Debris) with `debrisParentRecordingId = rec_2c68978d...`.

The retirement entry in the save (line 2228 of the .sfs):

```
ENTRY
{
    retirementId = rrt_33919eadcd674138baef970cb3e7b5b7
    recordingId = rec_2c68978d84054474b804c579c92f5d40
    restoredRecordingId = ab1f54b089f54312b02add0aa049e156
    sourceSupersedeRelationId = rsr_43cd2b53605348c482cc45312157423a
    rewindUT = 163.00814361574527
    createdUT = 221.44814361576749
    reason = rewound-out-supersede-fork
}
```

The orphan child `3d4713df...` has `mergeState=CommittedProvisional`,
`debrisParentRecordingId = rec_2c68978d...`, and renders normally even though
its parent was retired.

## Root Cause

`RecordingStore.EnsureRewindRetirementsForRollback` writes a
`RecordingRewindRetirement` row for the dropped supersede fork (Pass 1) and
optionally for the priorTip (Pass 2). Neither pass walks the
`DebrisParentRecordingId` link: the retirement set is exactly the rows the
rollback identified, not the transitive parent-anchored subtree under them.

Downstream visibility computations (`EffectiveState.ComputeERS`,
`EffectiveState.ComputeTimelineInactiveRecordingIds`,
`EffectiveState.IsRewindRetired`) treat retirement as a per-row predicate over
`scenario.RecordingRewindRetirements`, so a child whose `DebrisParentRecordingId`
points at a retired recording is not classified as inactive. It passes the
ERS visible-set, the timeline inactive map, the ghost playback skip-state
gate, the KSC marker gate, the tracking-station spawn suppression set, and the
recordings table inactive-row predicate. Result: the orphan debris ghost
renders and spawns at endpoints alongside the restored recording's debris.

## Approach

**Approach B: derived playback-visibility cascade.** Compute the retired
recording id set transitively over the parent-anchor link
(`Recording.DebrisParentRecordingId -> Recording.RecordingId`) at the point of
use, rather than materializing additional retirement rows.

Rejected alternatives:

- **A (cascade rows at retirement time).** Write extra
  `RecordingRewindRetirement` rows for every parent-anchored descendant.
  Cleans save data but introduces lifecycle coupling: any future code path
  that un-retires the parent (none today, but the legacy-Immutable load-time
  sweep already removes retirement rows under specific conditions) must also
  reverse the cascade rows, otherwise the children stay hidden after the parent
  reappears. Adds persistent state for a question that can be derived live
  from existing fields.

- **C (both).** Cascade rows for new saves plus the derived cascade for legacy
  saves. The derived cascade alone covers both populations and keeps the
  retirement audit trail one-row-per-direct-cause.

### Why derived works for the existing playtest save

Approach B is a pure read-side change: the playtest save is loaded as-is, no
data migration runs, and the orphan child `3d4713df...` becomes invisible on
the next ERS rebuild because its parent appears in the derived retired set.
The save state itself does not need to be rewritten.

### Reversibility

Today, `RecordingRewindRetirement` rows are removed only by housekeeping paths
(orphan cleanup, tree-discard purge, the legacy-Immutable load-time sweep
reconstructing a priorTip -> canon supersede relation). Each of those paths
already removes the retired row, so the derived child cascade automatically
clears: on the next ERS rebuild the parent is no longer in the retired set,
so the children are no longer cascade-retired either. No additional cleanup
code is required.

### Scope discipline

The cascade walks only the retirement edge today. Supersede-cascade (a
parent-anchored child whose parent is invisible via supersede rather than
retirement) is a related question with a wider blast radius: it would change
visibility for every re-fly merge, not just rewind-rollback retirements. Out
of scope for this fix.

## Implementation

Single file change in `Source/Parsek/EffectiveState.cs`:

1. Add a new overload
   `ComputeRewindRetiredRecordingIds(IReadOnlyList<Recording> recordings, IReadOnlyList<RecordingRewindRetirement> retirements)`.
   Seeds from the existing per-retirement overload, then iterates the
   recordings list applying a fixed-point closure: any recording whose
   `DebrisParentRecordingId` is in the retired set is added, repeat until the
   set stops growing. Bounded by recording count (no cycles possible: a
   parent-anchored child cannot be its own ancestor).

2. Add an overload
   `IsRewindRetired(Recording rec, IReadOnlyList<Recording> recordings, IReadOnlyList<RecordingRewindRetirement> retirements)`
   that does the cascade check for a single recording.

3. Update `ComputeERS` to call the new overload (already has access to
   `RecordingStore.CommittedRecordings`).

4. Update `ComputeTimelineInactiveRecordingIds` to call the new overload
   (already takes `recordings`).

5. Update per-recording call sites that already have access to a recordings
   list to use the cascade overload:
   - `GhostMapPresence.AddRewindRetiredSuppressedRecordingIds`
   - `RecordingsTableUI.IsInactiveForDisplay` and
     `IsEffectiveReplacementForLaunchRewindOwner`
   - `ParsekKSC.IsTimelineInactiveForKsc`
   - `RecordingStore.CanFastForwardPreRuntime`

6. `RecordingStore.EnsureRewindRetirementsForRollback` (the retirement-writing
   path) keeps using the raw per-retirement overload: its `seenIds` set is a
   one-pass dedup over rows being written, not a visibility check.

### Logging

`ComputeRewindRetiredRecordingIds` cascade overload logs one Verbose line per
rebuild noting the seed size and the cascade-added count under the
`[Parsek][VERBOSE][ERS]` tag (consistent with the existing ERS log site). Skip
the log when the cascade adds zero so quiet steady-state ERS rebuilds do not
gain new noise.

### Caching

Per-frame consumers (`ParsekKSC.Update` per-rec, `RecordingsTableUI` per-row,
`GhostMapPresence`) hit the cascade overload with the live store + scenario
lists every frame. Without a cache, each per-frame loop pays the closure cost
N times and re-emits the Verbose log on every call. The cascade overload
fast-paths reference-equality calls against `RecordingStore.CommittedRecordings`
and `ParsekScenario.Instance.RecordingRewindRetirements` through a HashSet
cache keyed on `(RecordingStore.StateVersion,
ParsekScenario.SupersedeStateVersion)` (same shape as the ERS cache). Every
retirement-list mutation site (`EnsureRewindRetirementsForRollback`,
`LoadTimeSweep`, `TreeDiscardPurge`) already bumps `SupersedeStateVersion`
alongside the write, so cache invalidation falls out for free. Pure-function
tests construct ad-hoc lists that miss reference equality and stay on the
deterministic compute path; an `AdHocCall_DoesNotPollLiveCache` regression
test pins the live cache's isolation from ad-hoc inputs.

## Test Plan

New xUnit fixture `OrphanDebrisOnRetirementTests` in
`Source/Parsek.Tests/EffectiveState/`. `[Collection("Sequential")]` because
it touches shared static state via `EffectiveState.ResetCachesForTesting`.

1. `Cascade_RetiredParent_HidesParentAnchoredChild` — retirement of recording
   P with a parent-anchored child C marks C inactive in the cascade overload.
2. `Cascade_MultipleChildren_HidesAllParentAnchoredChildren` — cascade adds
   every child of the retired recording.
3. `Cascade_TransitiveChain_HidesGrandchildren` — child whose
   `DebrisParentRecordingId` resolves to another child of the retired parent
   is also marked inactive (fixed-point closure).
4. `Cascade_UnrelatedRecording_StaysVisible` — recording with no parent-anchor
   link to a retired id stays visible.
5. `Cascade_ChildOfNonRetiredParent_StaysVisible` — parent-anchored child of a
   non-retired recording stays visible.
6. `Cascade_NoRetirements_NoChange` — empty retirements list returns an empty
   retired set even when parent-anchored recordings exist.
7. `Cascade_ParentNotRetiredButChildHasStaleDebrisParentId_StaysVisible` —
   negative test for the `DebrisParentRecordingId` lookup landing on a
   non-retired recording.
8. `ComputeERS_RetiredParentCascade_OmitsOrphanDebrisChild` — end-to-end
   through `ComputeERS` using `RecordingStore`. Asserts the orphan child is
   not in the ERS list and that the cascade log line fires.
9. `ComputeTimelineInactiveRecordingIds_RetiredParentCascade_MarksChildRewindRetired` —
   the dictionary returned by the central inactive-id helper includes the
   orphan child with `TimelineInactiveReason.RewindRetired`.
10. `Cascade_PlaytestShape_HidesOrphanKerbalXDebrisChild` — uses the playtest
    save's exact id pattern (retired parent + one parent-anchored
    `CommittedProvisional` child) to pin the user-reported case.
11. `Reversibility_RemovingRetirement_ReinstatesChild` — pin the derived
    behavior: removing the parent's retirement row makes the cascade
    re-include the child as visible. Documents that no extra cleanup is
    needed when the existing housekeeping paths remove a retirement.
12. `LiveStoreCall_RepeatsCacheCascade_LogsOnceUntilVersionBump` — first call
    fires the Verbose cascade log; five repeat calls with the same versions
    return the cached HashSet by reference and emit zero new log lines; a
    `BumpSupersedeStateVersion` re-runs the closure and re-logs.
13. `AdHocCall_DoesNotPollLiveCache` — ad-hoc test-fixture inputs miss the
    reference-equality fast-path, so the live cache stays empty and a
    subsequent live-store call returns the live result rather than the
    ad-hoc one.
14. `Cascade_DepthFourChain_HidesAllDescendants` — fixed-point closure reaches
    arbitrary depth (P -> c1 -> c2 -> c3), not just two levels.
15. `Cascade_ReverseListOrder_StillReachesAllDescendants` — recordings ordered
    descendant-first so a single pass would add only the first-level child;
    pins that the `do/while` loop (not a single scan) completes the cascade.
16. `Cascade_SelfParentRecording_TerminatesAndStaysVisibleWhenNotRetired` and
    the two-node-cycle pair
    (`Cascade_TwoNodeCycleNeitherRetired_TerminatesAndStaysVisible`,
    `Cascade_TwoNodeCycleOneRetired_HidesBothAndTerminates`) — corrupt-save
    defense: cyclic `DebrisParentRecordingId` graphs terminate (no infinite
    loop) and resolve correctly whether or not a cycle member is retired.

One flight-scene end-to-end test in `FlightPlaybackExplainabilityTests`
(`ComputePlaybackFlags_RetiredParentCascade_SkipsOrphanDebrisChildGhostAndSpawn`)
drives the cascade through `ParsekFlight.ComputePlaybackFlags` (the live
per-frame skip-state computation) and asserts the orphan child resolves to
`GhostPlaybackSkipReason.RewindRetired` with `needsSpawn=false`, closing the
gap between the pure cascade and the user-visible flight-scene behavior. No
Unity-dependent in-game test is needed: `ComputePlaybackFlags` runs against an
uninitialized `ParsekFlight` host with the recordings list passed in.

## Files Touched

- `Source/Parsek/EffectiveState.cs` (new overloads, callers updated).
- `Source/Parsek/RecordingStore.cs` (update `CanFastForwardPreRuntime` call
  site).
- `Source/Parsek/GhostMapPresence.cs` (update
  `AddRewindRetiredSuppressedRecordingIds`).
- `Source/Parsek/ParsekKSC.cs` (update `IsTimelineInactiveForKsc`).
- `Source/Parsek/UI/RecordingsTableUI.cs` (update `IsInactiveForDisplay` and
  `IsEffectiveReplacementForLaunchRewindOwner`).
- `Source/Parsek.Tests/OrphanDebrisOnRetirementTests.cs` (new fixture).
- `Source/Parsek.Tests/FlightPlaybackExplainabilityTests.cs` (one added
  flight-scene end-to-end test).
- `CHANGELOG.md` v0.10.0 Bug Fixes section.
- `docs/dev/todo-and-known-bugs.md` new Open / Done entry.
