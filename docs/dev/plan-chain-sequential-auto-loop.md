# Implementation Plan: Chain-Sequential Auto Looping

Maps the approved design doc (`docs/dev/design-chain-sequential-auto-loop.md`) onto
the current codebase. ASCII only. No schema change. Detection is host-side
(RecordingStore); the engine consumes opaque index-keyed loop-unit descriptors;
`IPlaybackTrajectory` stays chain-agnostic.

All file:line anchors are against the worktree state at planning time and will
drift as edits land; treat them as "the method named X near here", not exact
post-edit lines.

---

## Key facts established from reading the code

- The engine never reads `Recording`. It receives host knowledge through
  host-set delegates: `engine.ResolveChainNextIndex` (Func<int,int>) and
  `engine.IsGhostHeld` (Func<int,bool>), wired in
  `ParsekPlaybackPolicy` ctor (`ParsekPlaybackPolicy.cs:78,86`). This is the
  injection pattern the loop-unit descriptors must follow - NOT a new field on
  `IPlaybackTrajectory`.
- Per-frame loop dispatch is `GhostPlaybackEngine.cs:944-972` ("=== Loop dispatch
  (before main rendering) ==="): `if (ShouldLoopPlayback(traj))` ->
  `UpdateLoopingPlayback(...)` -> `continue;`. The follower interception must sit
  at the TOP of this block (before the standalone loop path), per design.
- The loop-synced-debris block is `GhostPlaybackEngine.cs:981-986` ->
  `TryUpdateLoopSyncedDebris` (`GhostPlaybackEngine.cs:1494`). It computes the
  parent clock via `TryComputeLoopPlaybackUT(parent, ...)` at line 1508 - this is
  the edge-9 site.
- The engine schedule rebuild is `RebuildAutoLoopLaunchScheduleCache`
  (`GhostPlaybackEngine.cs:4006`), called once per frame from `UpdatePlayback`
  at `GhostPlaybackEngine.cs:778`. Trajectories are built 1:1 from
  `committedRecordings` in `ParsekFlight.cs:18153-18155` and passed to
  `engine.UpdatePlayback(cachedTrajectories, flags, ctx)` at
  `ParsekFlight.cs:18164`. Index alignment invariant holds (committed index ==
  trajectory index == descriptor key).
- Global-queue eligibility: `GhostPlaybackLogic.ShouldUseGlobalAutoLaunchQueue`
  (`GhostPlaybackLogic.cs:593`), consulted in both
  `RebuildAutoLoopLaunchScheduleCache` (engine `:4018`) and
  `ParsekKSC.RebuildAutoLoopLaunchScheduleCache` (`ParsekKSC.cs:576`). This is
  the single place to exclude unit members from the global parade.
- Span-clock math home: `GhostPlaybackLogic` (`GhostPlaybackLogic.cs:12`) next to
  `ComputeLoopPhaseFromUT` (`:782`), `TryComputeLoopPlaybackUT` (`:413`),
  `AutoLoopLaunchSchedule` struct (`:73`). `LoopTiming.MinCycleDuration = 5.0`
  (`ParsekConfig.cs:262`), `LoopTiming.BoundaryEpsilon = 1e-6` (`:270`),
  `LoopTiming.MinLoopDurationSeconds = 1.0` (`:252`).
- KSC parity scheduler: `ParsekKSC.RebuildAutoLoopLaunchScheduleCache`
  (`ParsekKSC.cs:564`), consumed in the per-frame loop branch
  `ParsekKSC.cs:411-455` (`if (rec.LoopPlayback)` -> `TryComputeLoopUT` /
  `UpdateOverlapKsc` / `UpdateSingleGhostKsc`). KSC owns its own
  `autoLoopLaunchSchedules` dict (`ParsekKSC.cs:46`).
- Watch transfer mechanism: `WatchModeController.TransferWatchToNextSegment`
  (`WatchModeController.cs:2728`) + `FindNextWatchTarget` (`:2704`), driven from
  `ProcessWatchEndHoldTimer` (`:3205`) which is fed by the chain-seam
  PlaybackCompleted -> hold path. Looping members never reach that path
  (they `continue` at the loop dispatch), so a NEW trigger is needed (edge 10).
  `watchedOverlapCycleIndex` is the cycle the camera follows
  (`WatchModeController.cs:94,142`).
- Edge 11 spawn guard already covered: `ShouldSpawnAtRecordingEnd` returns
  `(false, "chain looping")` (`GhostPlaybackLogic.cs:4887`) via
  `RecordingStore.IsChainLooping`. No spawn change needed.
- Detection mirror target: `RecordingStore.PopulateLoopSyncParentIndices`
  (`RecordingStore.cs:4965`), invoked from `RunOptimizationPass`
  (`RecordingStore.cs:4310`, call at `:4325`). Chain helpers to reuse:
  `GetChainRecordings` (`:3627`, branch+index sort), `GetChainEndUT` (`:3609`,
  branch-0 only), `GetChainPredecessorIndex` (`:3585`).
- Test injection pattern: `RecordingStore.CreateRecordingFromFlightData(points,
  name)` + set `ChainId/ChainIndex/ChainBranch/LoopPlayback/LoopTimeUnit` +
  `CommitRecordingDirect(rec)` (see `ChainTests.cs:463-490`). Reset via
  `RecordingStore.ResetForTesting()` + `GhostPlaybackLogic.ResetForTesting()`
  (`AutoLoopTests.cs:16,22-23`). Log capture via
  `ParsekLog.TestSinkForTesting`. `LoopTimeUnit` enum is
  `Sec, Min, Hour, Auto` (`Recording.cs:6`).

### Central design decision: how descriptors reach the engine and KSC

Mirror `ResolveChainNextIndex`. The host (RecordingStore detection result) is
pushed in per schedule rebuild as a transient, index-keyed structure. Concretely:

- `GhostPlaybackLogic` gains a `LoopUnit` readonly struct + a `LoopUnitSet`
  value object (the two dictionaries from the design:
  `Dictionary<int,LoopUnit> unitsByOwner` and `Dictionary<int,int> ownerByIndex`).
- The engine gains a per-frame setter `internal void SetLoopUnits(LoopUnitSet)`
  called once per frame before `UpdatePlayback`. The engine stores it in a
  per-frame `currentLoopUnits` field, exactly the lifetime of
  `autoLoopLaunchSchedules`.
- The host builds the `LoopUnitSet` once per frame from
  `RecordingStore.DetectChainLoopUnits(committedRecordings)` and hands the same
  value to both `engine` (FLIGHT) and `ParsekKSC` (tracking station). This is the
  "fed to BOTH schedulers" requirement; the detection function is pure-static and
  shared.

Decision (doc silent on the exact transport): use a per-frame setter, not a
delegate that recomputes mid-frame, so detection runs exactly once per frame and
both schedulers consume an identical snapshot. Rationale: detection touches
`committedRecordings` ordering which must be frozen for the index-keying to be
valid across the frame.

---

## Phase order

P1 pure span-clock helper + LoopUnit/LoopUnitSet types (GhostPlaybackLogic) + tests
P2 host-side detection (RecordingStore.DetectChainLoopUnits) + tests
P3 wire descriptors into engine schedule rebuild + exclude unit members from
   the global queue + tests
P4 follower interception + per-member span-clock render dispatch (engine)
P5 ParsekKSC parity wiring
P6 debris-on-unit-member fix (edge 9)
P7 watch transfer at unit-internal boundaries OR documented owner-only fallback
   (edge 10)
P8 in-game tests + logging audit

This matches the design's preferred order. One adjustment: P3 (global-queue
exclusion + descriptor plumbing) is split so the *exclusion* half can ship and be
tested before the *render* half (P4) exists, keeping each phase independently
green. The exclusion is safe alone: a unit member excluded from the global queue
but not yet routed through the span clock falls back to its own per-recording loop
clock (today's behavior minus the global stagger), which is a strictly smaller
regression surface than leaving it double-scheduled.

---

## Phase 1 - Span-clock helper + unit types (GhostPlaybackLogic)

### Files

- MODIFY `Source/Parsek/GhostPlaybackLogic.cs` (add types + helper near
  `AutoLoopLaunchSchedule` at `:73` and `ComputeLoopPhaseFromUT` at `:782`).

### New types (proposed)

```
internal readonly struct LoopUnit
{
    internal LoopUnit(int ownerIndex, int[] memberIndices,
        double spanStartUT, double spanEndUT, double cadenceSeconds);
    internal int OwnerIndex { get; }
    internal int[] MemberIndices { get; }   // committed indices, ChainIndex order
    internal double SpanStartUT { get; }
    internal double SpanEndUT { get; }
    internal double CadenceSeconds { get; } // = span duration, clamped to MinCycleDuration
}

internal sealed class LoopUnitSet
{
    internal static readonly LoopUnitSet Empty;
    internal IReadOnlyDictionary<int, LoopUnit> UnitsByOwner { get; }
    internal IReadOnlyDictionary<int, int> OwnerByIndex { get; } // memberIdx -> ownerIdx
    internal bool TryGetUnitForMember(int memberIndex, out LoopUnit unit);
    internal bool IsMember(int index);
}
```

### New pure helper (proposed)

```
// loopUT inside [spanStartUT, spanEndUT] plus the unit cycle index.
// cadenceSeconds clamped to MinCycleDuration INSIDE this helper (edge 14;
// design 235-241, 347-351). Seamless wrap when cadence == span duration.
internal static bool TryComputeSpanLoopUT(
    double currentUT, double spanStartUT, double spanEndUT, double cadenceSeconds,
    out double loopUT, out long cycleIndex);

// Member selection over the span. Returns the index INTO memberWindows of the
// member whose [Start,End] contains loopUT, applying edge-5 precedence (higher
// ChainIndex wins an overlap) and edge-6 gap rule (returns false = no member).
// memberWindows is ChainIndex-ordered.
internal static bool TrySelectSpanMember(
    double loopUT, IReadOnlyList<(double startUT, double endUT)> memberWindows,
    out int selectedSlot, out bool inInterMemberGap);
```

Reuse `LoopTiming.BoundaryEpsilon` for overlap/boundary comparisons so selection
agrees with `TryComputeLoopPlaybackUT` boundary handling.

### Integration point

None yet - pure helpers + types. P3/P4/P5/P6 call them.

### Tests (new `Source/Parsek.Tests/ChainLoopUnitTests.cs`, class
`[Collection("Sequential")]`, Dispose calls `GhostPlaybackLogic.ResetForTesting`)

- `TryComputeSpanLoopUT_WrapsSeamlessly_AtSpanEnd` - loopUT at spanEnd maps to
  spanEnd; one cadence step later wraps to spanStart with cycleIndex+1. Fails if
  a pause window is inserted at the wrap (would prove cadence took the global gap
  path instead of span duration).
- `TryComputeSpanLoopUT_ClampsTinySpanToMinCycleDuration` (edge 14) - span of 2s
  still advances on a 5s cadence. Fails if the helper divides by the raw 2s span.
- `TryComputeSpanLoopUT_BeforeSpanStart_ReturnsFalseOrSpanStart` - currentUT
  before spanStart. Fails if it returns a negative phase.
- `TrySelectSpanMember_TilesSpan_ExactlyOneMember` - sample loopUT across one
  cycle of 3 contiguous windows; exactly the covering member is selected. Fails
  if two are selected outside an overlap.
- `TrySelectSpanMember_OverlapPicksHigherIndex` (edge 5) - i and i+1 overlap by
  0.5s; loopUT in overlap selects i+1. Fails if i wins.
- `TrySelectSpanMember_GapReturnsNoMember` (edge 6) - loopUT in a UT gap between
  member i end and i+1 start returns false, inInterMemberGap == true. Fails if it
  clamps to a stale member.

### Diagnostic logs

None inside the pure helpers (callers log). Keep helpers log-silent so the
per-frame callers own the rate-limiting.

### Done condition

New types compile; all P1 tests pass; no existing test regresses.

---

## Phase 2 - Host-side detection (RecordingStore.DetectChainLoopUnits)

### Files

- MODIFY `Source/Parsek/RecordingStore.cs` (new helper modeled on
  `PopulateLoopSyncParentIndices` at `:4965`; reuse `GetChainRecordings` at
  `:3627`).

### New method (proposed)

```
// Pure with respect to Unity. Groups committed recordings by ChainId; within
// each chain takes branch-0 members sorted by ChainIndex; finds every maximal
// run of >= 2 consecutive members that are all (LoopPlayback &&
// LoopTimeUnit == Auto && Points.Count >= 2 && positive duration); emits one
// LoopUnit per run keyed by ownerIndex (lowest ChainIndex member committed
// index). Returns LoopUnitSet.Empty when no unit forms.
internal static LoopUnitSet DetectChainLoopUnits(IReadOnlyList<Recording> recordings);
```

Detection rules (design 197-204, edges 1-8):
- Group by `ChainId`; skip null/empty ChainId.
- Within a chain: filter `ChainBranch == 0` (edge 8), sort by `ChainIndex` (use
  the same comparator shape as `GetChainRecordings`).
- A member qualifies iff `LoopPlayback && LoopTimeUnit == LoopTimeUnit.Auto &&
  Points != null && Points.Count >= 2 && EndUT - StartUT > 0`. A disqualified
  member (manual period, not looping, zero-duration) BREAKS the run (edges 2,3,4,7).
- Emit units only for runs of length >= 2 (edge 1).
- `spanStartUT = first.StartUT`, `spanEndUT = last.EndUT`,
  `cadenceSeconds = Max(spanEndUT - spanStartUT, LoopTiming.MinCycleDuration)`.
- `memberIndices` are the COMMITTED-list indices (the key invariant), in
  ChainIndex order.
- Edge 7 re-check: if filtering a zero-duration member drops a run below 2, the
  run does not emit (split at the degenerate member just like a non-auto member).

### Integration point

`DetectChainLoopUnits` is NOT called from `RunOptimizationPass` (that runs at
load, but unit membership flips at runtime when the player toggles loop/period -
design 191-195). It is called per-frame by the host playback loop (P3/P5). It is
also the function the dual-scheduler-parity test feeds.

### Tests (extend `ChainLoopUnitTests.cs`; inject via
`CreateRecordingFromFlightData` + chain fields + `CommitRecordingDirect`)

- `DetectChainLoopUnits_TwoConsecutiveAuto_FormsOneUnit` (edge 18 headline) -
  span = first.Start..last.End, members = [0,1]. Fails if not grouped or wrong
  span.
- `DetectChainLoopUnits_SingleAutoMember_NotAUnit` (edge 1) - run length 1, so
  `OwnerByIndex` does not contain that index. Fails if a lone member is unitized.
- `DetectChainLoopUnits_ManualMemberBreaksRun` (edges 3,4) - [auto, manual, auto,
  auto] gives one unit over the trailing pair only; leading lone auto is not a
  unit; manual is in neither.
- `DetectChainLoopUnits_NonContiguousAuto_DoesNotMerge` (edge 2) - [auto,
  not-looping, auto] gives two length-1 runs, no unit.
- `DetectChainLoopUnits_Branch1MembersExcluded` (edge 8) - branch-0 pair forms a
  unit; a branch-1 member at the same ChainIndex is not pulled in.
- `DetectChainLoopUnits_ZeroDurationMemberExcluded_DissolvesIfBelowTwo` (edge 7).
- `DetectChainLoopUnits_SpanAndCadence_FromWindowsNotGlobalGap` - cadence equals
  span duration, not the 30s/10s global gap.

### Diagnostic logs (design 421-432)

- One summary per detection call (Verbose, batch-count convention):
  `RecordingStore`/`Loop`: "Chain-loop units: built N unit(s) from chain
  <chainId> [members=i,j,k span=A..B cadence=Xs]". Per-unit detail only when N>0.
- Per rejected run (Verbose): `Loop`: "chain <chainId> run [i..j] not a unit:
  reason" with reason in {length<2, member-not-auto, member-not-looping,
  member-zero-duration, branch>0}.

### Log-assertion tests

- `DetectChainLoopUnits_EmitsUnitBuiltSummary` - asserts the summary line with
  member indices + span (protects observability).
- `DetectChainLoopUnits_RejectedRunLogsReason_LengthLtTwo` /
  `_MemberNotAuto` / `_BranchGtZero` - each asserts the specific reason.

### Done condition

Detection produces correct `LoopUnitSet`s for all edge fixtures; logs assert
green; no schema/serialization change (verified by a no-new-keys assertion in P8
serialization test).

---

## Phase 3 - Plumb descriptors into engine schedule rebuild + exclude unit members from the global queue

### Files

- MODIFY `Source/Parsek/GhostPlaybackEngine.cs` (`RebuildAutoLoopLaunchScheduleCache`
  `:4006`; add `currentLoopUnits` field near `autoLoopLaunchSchedules` `:64`; add
  host setter near the other host delegates `:209-253`).
- MODIFY `Source/Parsek/ParsekFlight.cs` (build `LoopUnitSet` and push it before
  `engine.UpdatePlayback` at `:18164`; the trajectory build is `:18153`).

### New surface (proposed)

- Engine field: `private LoopUnitSet currentLoopUnits = LoopUnitSet.Empty;`
- Engine setter: `internal void SetLoopUnits(LoopUnitSet units)` - called by the
  host once per frame before `UpdatePlayback` (alongside building
  `cachedTrajectories`). Stored, then read inside
  `RebuildAutoLoopLaunchScheduleCache`.

### Integration points

1. `RebuildAutoLoopLaunchScheduleCache` (engine `:4015-4019`): candidate filter is
   `if (!GhostPlaybackLogic.ShouldUseGlobalAutoLaunchQueue(traj)) continue;`. Add a
   second skip: `if (currentLoopUnits.IsMember(i)) { log-once; continue; }`. This is
   the global-queue exclusion (design 215-221). Unit members never get a staggered
   global slot.
2. `ParsekFlight.UpdateTimelinePlaybackViaEngine` (`:18152-18164`): after building
   `cachedTrajectories`, call
   `engine.SetLoopUnits(RecordingStore.DetectChainLoopUnits(committed))` (committed
   is the same list cachedTrajectories was built from, preserving index alignment).

### Tests (extend `ChainLoopUnitTests.cs` and/or a new `ChainLoopSchedulingTests.cs`)

- `RebuildSchedule_UnitMembersExcludedFromGlobalQueue` - feed a `LoopUnitSet` with
  members {0,1} plus a standalone auto recording at index 2; assert the engine
  `autoLoopLaunchSchedules` contains index 2 but NOT 0/1. Fails if a unit member
  also receives a global slot (double-scheduling).
  - `autoLoopLaunchSchedules` is private. Add an
    `internal bool TryGetAutoLoopScheduleForTesting(int idx, out ...)` accessor or
    assert via the new exclusion log line (preferred - avoids widening API).
- `RebuildSchedule_MixedList_StandaloneStaysInParade` (design 494-497) - standalone
  auto + a 3-member auto chain; standalone keeps its slot, chain members excluded.

### Diagnostic logs

- `Loop`: "chain-loop member recIdx=i excluded from global auto queue (unit
  owner=o)" - one-shot-per-rebuild via VerboseOnChange keyed on the unit
  fingerprint (mirror the KSC `VerboseOnChange("KSC","auto-loop-queue",...)`
  pattern at `ParsekKSC.cs:599`).

### Done condition

Unit members are absent from `autoLoopLaunchSchedules`; standalone autos
unaffected; exclusion log asserts green. (Render still falls back to per-recording
loop clock until P4 - acceptable interim, see phase-order note.)

---

## Phase 4 - Follower interception + per-member span-clock render dispatch (engine)

Highest-structural-risk phase (design 388-394, risk 1).

### Files

- MODIFY `Source/Parsek/GhostPlaybackEngine.cs` (insert interception at the loop
  dispatch `:944-972`; new method `UpdateUnitMemberPlayback`).

### Integration point

At the TOP of the `if (ShouldLoopPlayback(traj))` block (`:945`), BEFORE the
anchor-gating check, insert:

```
if (currentLoopUnits.TryGetUnitForMember(i, out LoopUnit unit))
{
    UpdateUnitMemberPlayback(i, traj, f, ctx, unit, suppressGhosts, suppressVisualFx);
    continue;
}
```

Placement rationale: anchor gating at `:948` must still apply PER MEMBER (edge 17),
so `UpdateUnitMemberPlayback` re-checks
`traj.LoopAnchorVesselId != 0 && !loadedAnchorVessels.Contains(...)` itself and
short-circuits (skip when anchor unloaded) - the doc requires anchor gating to
still short-circuit per-member.

### New method (proposed)

```
private void UpdateUnitMemberPlayback(
    int i, IPlaybackTrajectory traj, TrajectoryPlaybackFlags f, FrameContext ctx,
    LoopUnit unit, bool suppressGhosts, bool suppressVisualFx)
```

Behavior (design 224-262):
1. Per-member anchor gating (edge 17): if anchor configured + unloaded, DestroyGhost
   + DestroyAllOverlapGhosts + CountFrameSkip(AnchorMissing) + return.
2. Compute span clock for the unit: `TryComputeSpanLoopUT(ctx.currentUT,
   unit.SpanStartUT, unit.SpanEndUT, unit.CadenceSeconds, out spanLoopUT, out
   unitCycle)`. Per member per frame (cheap, deterministic, no shared mutable cache).
3. Member selection: build the member-window list for the unit from the trajectory
   list (`unit.MemberIndices` mapped to each member raw `StartUT`/`EndUT`; see
   decision D2). `TrySelectSpanMember(spanLoopUT, windows, out selectedSlot, out inGap)`.
4. If i is NOT the selected member (or in a gap): hide/destroy this member ghost for
   this frame (DestroyGhost or SetActive(false) consistent with the warp-suppression
   branch) + CountFrameSkip + return. Log the gap case (rate-limited).
5. If i IS the selected member: render it at `spanLoopUT` via the existing in-range
   path. Reuse the debris pattern: copy `ctx`, set `syncCtx.currentUT = spanLoopUT`,
   call `RenderInRangeGhost(i, traj, f, syncCtx, ...)`. On cycle change
   (`state.loopCycleIndex != unitCycle`): DestroyGhost + null state (clean visual per
   cycle, design 257-262) and clear `completedEventFired`.
6. Warp suppression: apply `ShouldSuppressGhostMeshAtWarp(ctx.warpRate, traj,
   spanLoopUT)` to the live member (edge 12) - same as the standalone loop path.

### Tests

Pure member-selection / span math already covered in P1. The dispatch itself is
runtime (ghost GameObjects), so engine-level assertions are in-game (P8). The
window-selection seam is already factored out via `TrySelectSpanMember` (P1 covers
it); the GameObject activation count is P8.

### Diagnostic logs (design 438-449, rate-limited, per-unit key)

- `Engine`: segment-boundary handoff: "unit owner=o handoff member i->j at loopUT=..."
  (VerboseRateLimited, key per unit owner).
- `Engine`: cycle wrap: "unit owner=o wrapped cycle c-1->c at UT=...".
- `Engine`: gap case (edge 6): "unit owner=o loopUT=... in inter-member gap, no member
  visible" (rate-limited).
- `Engine`: overlap precedence (edge 5): "unit owner=o overlap at loopUT=... rendering
  higher-index member j over i" (rate-limited).
Every hide/skip branch logs its reason (project rule).

### Done condition

In-game (P8): exactly one unit ghost active at a time, advances through members in
order, wraps seamlessly. Standalone and manual loops unchanged.

---

## Phase 5 - ParsekKSC parity wiring

Risk 1 (follower interception) also lands here (design 248-254, 392-394).

### Files

- MODIFY `Source/Parsek/ParsekKSC.cs` (`RebuildAutoLoopLaunchScheduleCache` `:564`;
  per-frame loop branch `:411-455`; add `currentLoopUnits` field near `:46`).

### Integration points

1. KSC main update (`:330-342`) builds `committed` and calls
   `RebuildAutoLoopLaunchScheduleCache(committed)` at `:342`. Add right before it:
   `currentLoopUnits = RecordingStore.DetectChainLoopUnits(committed);` (same value
   object the engine got - this is the dual-scheduler share).
2. In `RebuildAutoLoopLaunchScheduleCache` (`:573-577`), add the same
   `if (currentLoopUnits.IsMember(i)) continue;` exclusion as the engine.
3. In the per-recording loop (`:411` `if (rec.LoopPlayback)`), add a parity
   interception BEFORE the existing overlap/single dispatch:
   `if (currentLoopUnits.TryGetUnitForMember(i, out var unit)) { UpdateUnitMemberKsc(i, rec, currentUT, unit, ...); continue; }`.
4. New `UpdateUnitMemberKsc` mirrors `UpdateUnitMemberPlayback`: compute
   `TryComputeSpanLoopUT`, `TrySelectSpanMember`, render via `UpdateSingleGhostKsc`
   at `spanLoopUT` only for the selected member, destroy otherwise.

### Tests

- `DualScheduler_SameRecordingSet_ProducesIdenticalUnits` (design 498-500) - call
  `DetectChainLoopUnits` once; assert the same `LoopUnitSet` drives both. Since
  detection is the shared pure function, the parity test asserts that function is
  the single source (both schedulers call `DetectChainLoopUnits` and pass through
  `IsMember`/`TryGetUnitForMember` without re-deriving). Fails if KSC re-derives
  units differently.
- Reuse the global-queue exclusion test against the KSC rebuild path.

### Diagnostic logs

Same exclusion + handoff lines under tag `KSC` (or `Loop` for the detection share).
Reuse the `VerboseOnChange` fingerprint pattern already in `ParsekKSC.cs:597-605`.

### Done condition

A looped chain replays as one unit in the tracking station identically to flight;
parity test green.

---

## Phase 6 - Debris-on-unit-member fix (edge 9)

Risk 2 (design 313-324, 390-391).

### Files

- MODIFY `Source/Parsek/GhostPlaybackEngine.cs` (`TryUpdateLoopSyncedDebris` `:1494`,
  specifically the parent-clock computation at `:1505-1517`).

### Problem

`TryUpdateLoopSyncedDebris` computes the parent clock via
`TryComputeLoopPlaybackUT(parent, ctx.currentUT, ...)` (`:1508`) - the parent OWN
per-recording loop clock. When the parent is a unit member, its timing is the SPAN
clock, not its own loop clock. The debris would render against the wrong phase.

### Integration point

Inside `TryUpdateLoopSyncedDebris`, after resolving
`parent = trajectories[traj.LoopSyncParentIdx]` (`:1502`) and
`ShouldLoopPlayback(parent)` (`:1503`): branch on whether the parent index is a
unit member.

```
int parentIdx = traj.LoopSyncParentIdx;
if (currentLoopUnits.TryGetUnitForMember(parentIdx, out LoopUnit parentUnit))
{
    // Source the span clock for the parent owner, then position the debris at
    // its own window if spanLoopUT is inside [activationStartUT, traj.EndUT].
    GhostPlaybackLogic.TryComputeSpanLoopUT(ctx.currentUT, parentUnit.SpanStartUT,
        parentUnit.SpanEndUT, parentUnit.CadenceSeconds, out parentLoopUT, out parentCycle);
    parentPaused = false; // span clock has no pause window (cadence == span)
}
else
{
    // existing per-recording path: the engine instance wrapper
    // TryComputeLoopPlaybackUT (`:4117`, 3 out-params incl. parentPaused)
}
```

The rest of the block (cycle-change rebuild at `:1536`, debrisInRange test at
`:1545`, RenderInRangeGhost at `:1551`) is unchanged - it already keys off
`parentLoopUT`/`parentCycle`.

### Tests

- Span-clock sourcing is pure (P1). The debris-vs-window logic is in-game (ghost
  GameObjects), so the assertion is P8: debris loop-synced to a unit member follows
  the unit clock (design 525-527). One xUnit guard: a small extracted predicate
  `ShouldSourceDebrisFromUnitSpan(parentIdx, LoopUnitSet)` can be unit tested for the
  branch decision.

### Diagnostic logs

- `Engine`: "loop-sync debris #i parent #p is unit member (owner=o) - sourcing span
  clock" (VerboseOnChange keyed on the debris index, fires when the parent unit
  membership flips).

### Done condition

In-game: debris renders on the span clock, not the raw member window. No regression
for debris whose parent is a non-unit looper.

---

## Phase 7 - Watch transfer at unit-internal boundaries (edge 10)

Risk 3 (design 325-334, 392-394). Highest behavioral uncertainty.

### Files

- MODIFY `Source/Parsek/WatchModeController.cs`: new branch in
  `HandleLoopCameraAction` `:800` that calls the existing cross-index
  `TransferWatchToNextSegment` `:2728`. The new branch must run BEFORE the existing
  `if (watchedRecordingIndex != evt.Index) return;` early-return `:802` (a
  unit-internal handoff is precisely the `evt.Index != watchedRecordingIndex` case,
  so the existing guard would otherwise drop it).
- MODIFY `Source/Parsek/GhostPlaybackEvents.cs`: add a new
  `CameraActionType.UnitHandoffRetarget` (preferred over overloading the existing
  `RetargetToNewGhost`, which is gated on `watchedOverlapCycleIndex == -1` for the
  explosion-bridge and consumes `evt.GhostPivot`, not a sibling index). The new
  type carries the new live member index in `CameraActionEvent.Index`.
- MODIFY `Source/Parsek/GhostPlaybackEngine.cs` to fire the new event on
  unit-internal handoff via `OnLoopCameraAction`.

### Problem

For non-loop chains the camera advances via PlaybackCompleted -> hold ->
`FindNextWatchTarget` -> `TransferWatchToNextSegment`. Unit members are loop-enabled
and `continue` at the loop dispatch (P4), so PlaybackCompleted for the head segment
never fires; the chain-seam transfer never runs. Under the unit the visible ghost
changes at member boundaries with no transfer, so a watched camera sticks to a
now-hidden ghost.

### Approach (preferred): engine-driven retarget on unit handoff

When `UpdateUnitMemberPlayback` detects the selected member changed from the previous
frame (the boundary handoff at design 240-242), and the watched index belongs to this
unit, fire `OnLoopCameraAction(CameraActionEvent{ Action = UnitHandoffRetarget,
Index = newSelectedMemberIndex, ... })`. This requires NEW code on both sides, not
reuse:
- NEW `CameraActionType.UnitHandoffRetarget` (the existing `RetargetToNewGhost` case
  `:840` is gated on `watchedOverlapCycleIndex == -1` and consumes `evt.GhostPivot`,
  so it is the wrong vehicle).
- NEW branch in `HandleLoopCameraAction`, placed BEFORE the
  `watchedRecordingIndex != evt.Index` early-return `:802`, that calls the existing
  cross-index `TransferWatchToNextSegment(evt.Index)` `:2728` (this method already
  does cross-index transfer with camera-state preservation - that part IS reused).
The retarget target is a DIFFERENT recording index (a sibling member), not a new
cycle of the same index. On the wrap (last member to first member), the same
mechanism transfers back to the owner.

### Fallback (documented limitation)

Watching a unit follows the owner member only; the camera does not auto-advance
across segments. Invariant preserved: the camera target is NEVER a
deactivated/destroyed ghost - so the owner-only follow must keep the OWNER ghost
alive whenever watched (treat the watched owner like the existing `IsGhostHeld` case
so `UpdateUnitMemberPlayback` does not destroy it when it is not the selected member;
instead hide-without-destroy and let the camera ride the held owner). This is the
camera-follows-owner-only fallback (design 332-334).

DECISION: implement the preferred engine-driven retarget; keep the fallback as the
failure mode if in-game testing (P8) shows the transfer flickers. The fallback
keep-owner-alive guard is cheap insurance and should land regardless so the invariant
(no camera on a dead ghost) holds even if a transfer is dropped.

### Tests

- xUnit: a pure decision helper
  `ShouldRetargetWatchOnUnitHandoff(watchedIndex, prevSelected, newSelected, LoopUnitSet)`
  returns true only when the watched index belongs to the unit and the selected
  member changed. Fails if it retargets on a non-watched unit or on a same-member
  frame.
- In-game (P8): watch a unit; camera target advances with the live member and survives
  the wrap; assert the camera target is never a hidden ghost.

### Diagnostic logs

- `CameraFollow`: "unit watch retarget owner=o member i->j at loopUT=..." on transfer;
  "unit watch fallback: following owner only (transfer unavailable)" if the fallback
  path is hit.

### Done condition

Watching a looping unit keeps the camera on the live member (or owner in fallback);
the camera is never parented to a deactivated ghost.

---

## Phase 8 - In-game tests + logging audit

### Files

- MODIFY `Source/Parsek/InGameTests/RuntimeTests.cs` (FLIGHT-scene tests).
- MODIFY `Source/Parsek.Tests/AutoLoopTests.cs` (serialization no-leak test).

### In-game tests (RuntimeTests.cs, `[InGameTest(Scene = GameScenes.FLIGHT)]`)

- `ChainLoopUnit_SingleGhostAdvancesAndWraps` - inject a synthetic 3-segment
  auto-loop chain; over several frames assert exactly one unit ghost GameObject is
  active at a time, the active one advances through members in order, then wraps.
  Fails on multiple visible unit ghosts or missing handoff/wrap.
- `ChainLoopUnit_WatchFollowsLiveMember` (edge 10) - watch a unit; camera target
  advances with the live member and survives the wrap; if fallback taken, camera
  follows the owner. Invariant: camera target is never a deactivated ghost.
- `ChainLoopUnit_DebrisFollowsSpanClock` (edge 9) - debris loop-synced to a unit
  member renders on the span clock, not the raw member window.

### Serialization test (xUnit, extend AutoLoopTests, design 508-513)

- `ChainLoopUnit_RoundTrip_NoUnitStatePersisted` - round-trip a chain of auto-loop
  members through `ParsekScenario` save/load; assert NO new ConfigNode keys for unit
  membership (units are runtime-only); confirm the unit reconstitutes from loaded
  loop/period state via `DetectChainLoopUnits` after load. Fails if any unit state
  leaks into the save.

### Logging audit

Walk every new branch in P2/P3/P4/P5/P6/P7 and confirm each hide/skip/select/wrap
path logs its reason (project no-silent-code-paths rule). Confirm rate-limit keys are
per-unit-owner where the index identity matters and shared otherwise (design 438-449;
CLAUDE.md batch-counting convention). Run
`pwsh -File scripts/validate-ksp-log.ps1` after an in-game session.

### Done condition

All in-game tests pass via Ctrl+Shift+T; serialization no-leak test green; full
`dotnet test` green; log validator clean.

---

## Top three risks and de-risking

1. Follower interception (P4, P5). De-risk: insert the interception as the FIRST
   statement of the existing loop dispatch block so the standalone path is a pure
   fallthrough for non-members; gate entirely on `currentLoopUnits.IsMember(i)`, which
   is empty (`LoopUnitSet.Empty`) until detection produces a unit - so the entire
   feature is dormant for every existing save until two consecutive auto-loop members
   exist. Validate with the P3 exclusion landing first (members fall back to
   per-recording clocks, a known-good state) before P4 routes them to the span clock.
   Keep `UpdateUnitMemberPlayback` re-checking anchor gating and warp suppression so
   it cannot regress edges 12/17.

2. Debris-on-unit (P6). De-risk: the only change is the SOURCE of
   `parentLoopUT`/`parentCycle`; the downstream debrisInRange + RenderInRangeGhost
   logic is untouched. Branch is gated on the parent being a unit member, so non-unit
   debris is byte-identical to today. Span clock has no pause window (cadence == span),
   so `parentPaused` is hard-set false on the unit branch - confirm this does not
   strand a debris that legitimately has no coverage at spanLoopUT (the existing
   debrisInRange test at `:1545` already handles out-of-range -> DestroyGhost).

3. Watch transfer (P7). De-risk: reuse the proven cross-index
   `TransferWatchToNextSegment` rather than inventing a transfer; land the
   keep-watched-owner-alive guard unconditionally so the no-dead-ghost invariant holds
   even if a single transfer event is dropped; treat the owner-only fallback as the
   documented escape hatch. Validate purely in-game (P8) since camera state is
   runtime-only.

---

## Design-doc silences and decisions made

- D1 Descriptor transport (doc describes the dictionaries, not the wire): chose a
  per-frame `engine.SetLoopUnits(LoopUnitSet)` setter + a shared pure
  `RecordingStore.DetectChainLoopUnits` called once per frame by each host scene,
  rather than a recompute-on-demand delegate, so both schedulers consume an identical
  frozen snapshot and detection runs exactly once per frame.
- D2 Window basis for member tiling: chose RAW `StartUT`/`EndUT` (not
  `EffectiveLoopStartUT/EndUT`) because the span is defined by raw member windows
  (design 178) and contiguity is a raw-window property. Effective-loop narrowing is a
  per-recording loop concept the unit bypasses.
- D3 Span-clock caching: chose to recompute `TryComputeSpanLoopUT` per member per
  frame (no owner-keyed cache) to avoid invalidation bugs; cost is negligible.
- D4 Global-queue exclusion vs render split: chose to land exclusion (P3) before
  render (P4); interim fallback is the per-recording loop clock, a strictly smaller
  regression than double-scheduling.
- D5 P7 transfer vs fallback: implement the engine-driven retarget; land the
  keep-owner-alive guard regardless; fallback is the documented limitation.
- D6 UI affordance / inter-cycle pause / branch-aware units (Open Questions 1-3): out
  of scope for v1 per the doc recommendations; not planned here.

---

## Edge-case coverage map

| # | Edge case | Phase | Test / disposition |
|---|-----------|-------|--------------------|
| 1 | One auto member -> not a unit | P2 | DetectChainLoopUnits_SingleAutoMember_NotAUnit |
| 2 | Non-contiguous auto members | P2 | DetectChainLoopUnits_NonContiguousAuto_DoesNotMerge |
| 3 | Mixed run (auto,auto,manual) | P2 | DetectChainLoopUnits_ManualMemberBreaksRun |
| 4 | Manual period inside auto run | P2 | DetectChainLoopUnits_ManualMemberBreaksRun |
| 5 | Overlapping member windows | P1 | TrySelectSpanMember_OverlapPicksHigherIndex (+P4 log) |
| 6 | Gap between members | P1 | TrySelectSpanMember_GapReturnsNoMember (+P4 gap log) |
| 7 | Under 2 points / zero duration member | P2 | _ZeroDurationMemberExcluded_DissolvesIfBelowTwo |
| 8 | Branch > 0 members | P2 | DetectChainLoopUnits_Branch1MembersExcluded |
| 9 | Debris synced to unit member | P6 | ShouldSourceDebrisFromUnitSpan (xUnit) + in-game P8 |
| 10 | Watch mode on a unit | P7 | ShouldRetargetWatchOnUnitHandoff (xUnit) + in-game P8 |
| 11 | Terminal spawn at loop end | n/a | Already covered: ShouldSpawnAtRecordingEnd "chain looping" (GhostPlaybackLogic.cs:4887). No code. |
| 12 | Time warp | P4 | Warp suppression reused on live member; in-game P8 spot-check |
| 13 | Runtime re-topology | P2/P3 | Self-heals: DetectChainLoopUnits runs per frame, no persisted state |
| 14 | Very short span | P1 | TryComputeSpanLoopUT_ClampsTinySpanToMinCycleDuration |
| 15 | Very long span / many cycles | P4 | cadence == span gives one cycle live; no overlap cap stress; P8 sanity |
| 16 | Body change / transition mid-unit | P4 | No new positioning math; member renders own surface at loopUT; P8 sanity |
| 17 | Anchor/relative member in unit | P4 | UpdateUnitMemberPlayback per-member anchor gating; in-game P8 |
| 18 | All members auto (headline) | P2 | DetectChainLoopUnits_TwoConsecutiveAuto_FormsOneUnit + in-game P8 |

No edge case is left without a home. Edges 11/13/15/16 require no new feature code
but are flagged with their covering guard or in-game sanity check.

---

## Internal-static-for-testability vs runtime-only

Pure / internal-static (xUnit, P1-P3, P5, P6, P7 decision helpers):
- `GhostPlaybackLogic.TryComputeSpanLoopUT`, `TrySelectSpanMember`, `LoopUnit`,
  `LoopUnitSet` (P1).
- `RecordingStore.DetectChainLoopUnits` (P2) - pure w.r.t. Unity, reads only Recording
  fields; testable via `CreateRecordingFromFlightData` + `CommitRecordingDirect`.
- Global-queue exclusion decision (`currentLoopUnits.IsMember`) - assertable via the
  exclusion log or a test accessor (P3).
- `ShouldSourceDebrisFromUnitSpan` (P6 branch predicate).
- `ShouldRetargetWatchOnUnitHandoff` (P7 decision predicate).

Runtime-only (in-game RuntimeTests.cs, P8) - require live ghost GameObjects, camera,
or PartLoader:
- `UpdateUnitMemberPlayback` GameObject activation count / handoff / wrap (P4).
- `UpdateUnitMemberKsc` map-marker positioning (P5).
- Debris-on-unit GameObject rendering against span clock (P6).
- Watch camera retarget across unit boundaries + wrap, no-dead-ghost invariant (P7).

Reset conventions: every new xUnit class touching shared static state uses
`[Collection("Sequential")]` with `RecordingStore.ResetForTesting()` and
`GhostPlaybackLogic.ResetForTesting()` in ctor/Dispose (per AutoLoopTests.cs /
ChainTests.cs). Log-assertion tests use `ParsekLog.TestSinkForTesting` +
`ParsekLog.ResetTestOverrides()`.
