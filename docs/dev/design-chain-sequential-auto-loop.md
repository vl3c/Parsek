# Design: Chain-Sequential Auto Looping

*When two or more consecutive recordings in the same chain are loop-enabled with
period set to `auto`, they should loop as a single unit: the end of one segment
syncs with the start of the next, and the whole multi-segment span loops back to
the beginning. The chain looks like one continuous looping flight, not a set of
independently relaunching ghosts.*

---

## Problem

A chain is a sequence of recordings that capture one continuous flight broken
into segments by vessel switches, staging, or continuations (`ChainId` +
ordered `ChainIndex`). The segments are recorded at contiguous absolute UTs:
segment 0 spans `[t0, t1]`, segment 1 spans `[t1, t2]`, and so on. When played
back without looping, they already hand off seamlessly end-to-start (the
chain-seam handoff / chain-shadow logic in `GhostPlaybackEngine`).

Looping breaks this. Today, the moment a recording is loop-enabled it takes the
loop dispatch and is scheduled independently. With period set to `auto`, every
auto-looped recording in the save (chain member or not) is dropped into one
flat, save-wide launch queue and relaunched on a single fixed gap (the global
"Auto-launch every" setting, default 10s), regardless of its own duration or
chain membership. Two consecutive chain members therefore do NOT sync end-to-
start. A 5-second segment and a 200-second segment get the same 10s gap, the
segments overlap or drift apart, and the chain stops reading as one flight.

What the player wants: set the segments of a chain to loop + `auto`, and have
the chain replay as a single looping unit. Segment N+1 begins exactly when
segment N ends; when the last segment ends, the unit wraps to the first
segment's start. The whole flight loops, not each piece.

This is a player-facing playback-quality feature. It makes looped chains
(launch -> ascent -> stage -> orbit, captured as a chain) look like the
continuous repeating flights players intuitively expect, instead of a stutter
of mistimed relaunches.

---

## Terminology

- **Chain**: a set of recordings sharing a `ChainId`, ordered by `ChainIndex`,
  capturing one continuous flight split into segments. `ChainBranch == 0` is the
  primary path; `ChainBranch > 0` are parallel ghost-only continuations.
- **Chain-loop unit** (new): a maximal run of consecutive primary-path
  (`ChainBranch == 0`) chain members that are ALL loop-enabled AND have period
  `auto`. A unit must contain at least 2 members. The unit is the thing that
  loops as a whole.
- **Unit span**: `[spanStart, spanEnd]` = the first member's `StartUT` to the
  last member's `EndUT` in the unit. The shared loop clock walks this span.
- **Span loop clock**: a single loop phase computed over the unit span. At any
  real UT, it resolves to a `loopUT` inside `[spanStart, spanEnd]`. Exactly one
  member (the one whose recorded sub-window covers `loopUT`) is rendered.
- **Unit anchor / owner**: the member with the lowest `ChainIndex` in the unit.
  It owns the span loop clock; the other members read it. (An implementation
  detail surfaced here only because the data model below references it.)
- **Global launch queue**: today's flat, save-wide auto stagger
  (`AutoLoopLaunchSchedule`). Standalone auto-looped recordings keep using it;
  chain-loop units are removed from it.

This document uses "auto" for `LoopTimeUnit.Auto` and "manual period" for
`Sec`/`Min`/`Hour`.

---

## Mental Model

Today, three auto-looped recordings (whether or not they form a chain) are a
"launch parade": each relaunches every `count * gap` seconds, staggered by
`gap`, on the global clock. Durations are ignored.

```
Global auto stagger (current), gap = 10s, 3 recordings:

  A: |==A==|         |==A==|         |==A==|        (relaunch every 30s)
  B:        |B|             |B|             |B|      (offset 10s)
  C:            |====C====|     |====C====|          (offset 20s, overlaps next A)
```

With a chain-loop unit, the contiguous auto-looped run of a chain becomes one
virtual recording that loops over its whole span. A single clock walks
`spanStart -> spanEnd` and wraps. Whichever member's recorded window contains the
clock position is the visible ghost; the others are hidden. Members are
contiguous, so exactly one is visible at a time and the visible ghost changes
exactly at each segment boundary.

```
Chain-loop unit (new): members A,B,C contiguous, span = [t0, t3]:

  unit clock:  t0====t1====t2========t3 | t0====t1====t2========t3 | ...
  visible:     [==A==][=B=][===C====]    [==A==][=B=][===C====]
               ^seg boundary handoffs    ^wrap: t3 end syncs to t0 start
```

The unit cadence (launch-to-launch period of the whole unit) defaults to the
span duration `spanEnd - spanStart`, so cycles are back-to-back: no gap, no
overlap, seamless wrap. That is the literal meaning of "the whole segment is
looped."

This is the same shape as the existing loop-synced-debris mechanism
(`LoopSyncParentIdx` / `TryUpdateLoopSyncedDebris`), where a debris recording
renders on its parent's loop clock whenever that clock enters the debris's own
UT range. The difference is only the geometry of the windows: debris windows
overlap their parent's window (they co-exist in time); chain members tile their
parent's span end-to-end (they succeed each other in time). The clock-sharing
and "render only when the shared clock is inside my window" rule are identical.

---

## Data Model

No new persisted fields. Chain-loop units are derived at playback-schedule build
time from existing serialized state, exactly as `LoopSyncParentIdx` is derived
by `PopulateLoopSyncParentIndices`. This keeps the recording schema
(`CurrentRecordingSchemaGeneration`) unchanged and adds no save-compat surface.

Existing fields the detection reads (all already serialized):

```
Recording.ChainId          string   // unit membership key
Recording.ChainIndex       int      // ordering within chain
Recording.ChainBranch      int      // 0 = primary path (only branch 0 forms a unit)
Recording.LoopPlayback     bool     // must be true for all members of a unit
Recording.LoopTimeUnit     LoopTimeUnit // must be Auto for all members of a unit
Recording.StartUT/EndUT    double   // member sub-window; span = [first.Start, last.End]
```

New transient (NonSerialized) per-recording field, populated each schedule
rebuild (mirrors the existing `LoopSyncParentIdx` pattern):

```
[NonSerialized] internal int ChainLoopOwnerIdx = -1;
// -1  = not a chain-loop-unit member (use existing behavior)
// == own committed index  => this recording is the unit owner/anchor
// other index             => follow that owner's span loop clock
```

New transient unit descriptor, computed once per rebuild and keyed by owner
index (held by the engine alongside `autoLoopLaunchSchedules`, not persisted):

```
struct ChainLoopUnit {
    int    ownerIndex;       // committed index of the lowest-ChainIndex member
    double spanStartUT;      // first member StartUT
    double spanEndUT;        // last member EndUT
    double cadenceSeconds;   // v1: spanEndUT - spanStartUT (seamless wrap)
    int[]  memberIndices;    // committed indices of all members, ChainIndex order
}
```

Detection (new helper, analogous to `RecordingStore.PopulateLoopSyncParentIndices`):
walk committed recordings grouped by `ChainId`; within each chain, sort
primary-path members by `ChainIndex`; find every maximal run of >= 2 consecutive
members that are all `LoopPlayback && LoopTimeUnit == Auto`; emit one
`ChainLoopUnit` per run and set each member's `ChainLoopOwnerIdx`. Members not in
any unit keep `ChainLoopOwnerIdx == -1` and behave exactly as today.

---

## Behavior

### Forming the unit

- At playback schedule rebuild (the same point that builds
  `autoLoopLaunchSchedules`), detect chain-loop units as described above.
- Members of a chain-loop unit are EXCLUDED from the flat global launch queue
  (`ShouldUseGlobalAutoLaunchQueue` is suppressed for them, or they are filtered
  out when the queue is built). They are scheduled by their unit instead.
- A standalone auto-looped recording, or a single auto-looped chain member that
  has no auto-looped neighbor, is NOT a unit (run length 1) and keeps today's
  global-stagger behavior.

### Playing the unit

- The unit owner computes the span loop phase: a `loopUT` inside
  `[spanStart, spanEnd]` repeating every `cadenceSeconds` (v1 = span duration,
  so the wrap from `spanEnd` back to `spanStart` is seamless).
- Each member (owner included) renders only when the shared `loopUT` falls in
  its own `[StartUT, EndUT]`, positioned at `loopUT` via the normal in-range
  render path. When `loopUT` is outside its window, the member is hidden /
  destroyed for that frame.
- Because members tile the span contiguously, exactly one member is visible at a
  time, and the visible ghost changes exactly at each segment boundary -
  reproducing the seamless chain handoff under looping.

### Cycle transitions

- When the span clock wraps (`loopUT` returns to `spanStart`), the unit cycle
  index increments. Members rebuild their ghost on cycle change (same
  cycle-change ghost rebuild the debris loop-sync path already performs) so each
  cycle starts from a clean visual state.

### Cadence / period meaning

- For a chain-loop unit, `auto` means "loop the unit at its natural span"
  (cadence = span duration). This is intentionally a different resolution of
  `auto` than the standalone meaning ("relaunch on the global gap"). The two
  never apply to the same recording at the same time because unit members are
  removed from the global queue.

### Interaction with non-unit ghosts

- Standalone auto-looped recordings continue to share the global stagger parade
  among themselves; the unit does not join that parade.
- Manual-period (`Sec`/`Min`/`Hour`) chain members are never part of a unit and
  loop independently as today, even if they sit between two auto members (they
  break the contiguous run).

---

## Edge Cases

Each: scenario -> expected behavior -> v1 disposition.

1. **Only one auto-looped member in a chain.** Run length 1. -> Not a unit;
   behaves exactly as today (global stagger). v1.
2. **Non-contiguous auto members** (index 0 auto, 1 manual/not-looping, 2 auto).
   -> Two separate runs of length 1; neither forms a unit; both behave as today.
   The manual/non-looping member 1 plays per its own setting. v1.
3. **Mixed contiguous run** (0 auto-loop, 1 auto-loop, 2 manual-loop). -> Members
   0 and 1 form one unit (length 2); member 2 loops independently on its manual
   period. v1.
4. **Member with a manual period inside an otherwise-auto run.** Breaks the run
   into the sub-runs on either side. Each sub-run of length >= 2 is its own unit.
   v1.
5. **Adjacent members with overlapping UT windows** (post-optimizer splits
   routinely leave a sub-second overlap). When `loopUT` is in both member i and
   i+1 windows, render the higher `ChainIndex` member (i+1). -> Matches the
   existing chain-shadow precedence ("continuation is authoritative during
   overlap"). v1.
6. **Gap between member i end and member i+1 start** (UT discontinuity from
   edits/splits). When `loopUT` lands in the gap, no member renders for that
   sliver (brief invisible moment), then the next member picks up. -> Log the gap
   once per unit; accept the brief invisibility. v1.
7. **A member has < 2 trajectory points / zero duration.** It cannot render a
   window. Exclude it from the unit; if exclusion drops the run below 2, the unit
   dissolves and the remaining member behaves as today. v1.
8. **Branch > 0 members** (parallel ghost-only continuations). Units are built
   over the primary path (branch 0) only, consistent with `GetChainEndUT`.
   Branch > 0 members are not unit members in v1 and play per their own loop
   setting. v1 (defer branch-aware units).
9. **Debris loop-synced to a member that is now a unit member**
   (`LoopSyncParentIdx` points at a unit member). The debris should follow the
   unit's span clock when its parent member is the live one. -> v1: debris reads
   the owner's span `loopUT` (the owner is the parent's clock source). Verify the
   existing debris dispatch resolves the owner, not the raw member, when the
   member is in a unit. Flagged as the highest-risk integration point.
10. **Watch mode on a unit.** Watching a chain follows the live ghost and hands
    off at boundaries via `WatchModeController`. The camera should follow whichever
    member is currently visible and hand off at each segment boundary and at the
    wrap. -> v1: confirm watch transfer keys off the currently-rendered member;
    the wrap is treated as a normal boundary handoff back to the first member.
11. **Terminal vessel spawn at loop end.** Looping recordings can spawn a real
    vessel at their end. A chain has one terminal (the chain's end), not one per
    segment. -> Intermediate member ends inside the unit do NOT trigger spawns;
    only the chain terminal's existing spawn semantics apply, and only if already
    configured. The unit changes ghost timing, not spawn policy. v1.
12. **Time warp.** Warp suppression hides moving ghosts at high warp today. The
    unit uses the same per-frame render path, so warp suppression applies to the
    currently-live member unchanged. v1.
13. **Member edited / chain re-topologized at runtime** (rare; merges, reverts).
    The unit is rebuilt from scratch on the next schedule rebuild, so membership
    self-heals. No persisted unit state to go stale. v1.
14. **Very short span** (sum of segments below `MinCycleDuration`). Clamp cadence
    to `LoopTiming.MinCycleDuration` defensively, same as `ResolveLoopInterval`.
    v1.
15. **Very long span / many cycles live.** Because cadence = span duration, only
    one cycle's worth of one ghost is ever live at a time (no overlap), so the
    `MaxOverlapGhostsPerRecording` cap is not stressed by the unit itself. v1.
16. **Chain spans a body change or scene-relevant transition mid-unit.** The unit
    is purely a playback-timing construct over recorded windows; each member still
    renders through its own recorded surface (absolute / relative / orbit) at
    `loopUT`. No new positioning math. v1.
17. **Anchor / relative-frame member inside a unit** (`LoopAnchorVesselId` set).
    The member still resolves its own anchor at `loopUT` via existing relative
    playback. The unit only supplies the clock. -> Confirm anchor gating (skip
    when anchor unloaded) still short-circuits per-member. v1.
18. **All members of a chain are auto-loop** (the common case). Single unit
    covering the whole chain; loops as one flight. v1, the headline scenario.

---

## What Doesn't Change

- The recording schema and all serialization. No new persisted fields, no
  generation bump.
- Standalone (non-chain) auto-loop behavior and the global launch queue for
  standalone recordings.
- Manual-period looping (`Sec`/`Min`/`Hour`) for any recording.
- Non-looping chain playback and the existing chain-seam handoff / chain-shadow
  logic (the unit is a looping construct; non-loop chains are untouched).
- Ghost mesh construction, trajectory interpolation, per-member positioning
  surfaces (absolute / relative / orbit), part-event and FX replay.
- Spawn policy, resource ledger, rewind, merge, and timeline semantics.
- Loop-synced debris mechanics, except for the owner-resolution clarification in
  edge case 9.

---

## Backward Compatibility

No save migration. Chain-loop units are derived at runtime from existing fields,
so old saves and recordings load unchanged and gain the behavior automatically
once their consecutive members are loop + auto. There is no on-disk
representation of a unit to version. Removing the feature (or a member dropping
out of auto) simply reverts those members to the global-stagger behavior on the
next schedule rebuild.

---

## Diagnostic Logging

Subsystem tags: `Loop` for detection/scheduling decisions, `Engine` for
per-frame render dispatch (rate-limited).

Detection (one-shot per rebuild, `Verbose` with a summary count, per
batch-counting convention):
- `RecordingStore`/`Loop`: "Chain-loop units: built N unit(s) from chain
  <chainId> [members=i,j,k span=spanStart..spanEnd cadence=Xs]". One summary
  line per rebuild; per-unit detail only when units exist.
- `Loop`: when a candidate run is rejected, log why: "chain <chainId> run
  [i..j] not a unit: <reason>" where reason is one of `length<2`,
  `member-not-auto`, `member-not-looping`, `member-zero-duration`,
  `branch>0`. This makes "why didn't my chain loop as a unit" answerable from
  the log alone.

Scheduling:
- `Loop`: "chain-loop member recIdx=i excluded from global auto queue (unit
  owner=o)" so the global-queue / unit split is visible.

Per-frame render (rate-limited via `VerboseRateLimited`, per-unit key):
- `Engine`: on segment-boundary handoff: "unit owner=o handoff member i->j at
  loopUT=..." (rate-limited, key per unit).
- `Engine`: on cycle wrap: "unit owner=o wrapped cycle c-1->c at UT=...".
- `Engine`: on the gap case (edge 6): "unit owner=o loopUT=... in inter-member
  gap, no member visible" (rate-limited, key per unit).
- `Engine`: on overlap precedence (edge 5): "unit owner=o overlap at loopUT=...
  rendering higher-index member j over i" (rate-limited).

Every branch that hides a member, picks a member at an overlap, or skips a frame
must log its reason, consistent with the project's "silent code paths are
debugging blind spots" rule.

---

## Test Plan

Pure-logic and serialization tests are xUnit; anything needing live KSP (ghost
GameObjects, watch camera, real spawn) is an in-game test in `RuntimeTests.cs`.

### Unit detection (xUnit, new `ChainLoopUnitTests`)
- **Two consecutive auto-loop members form one unit.** Fails if detection does
  not group them or computes the wrong span.
- **Run length 1 is not a unit.** A lone auto-loop chain member -> no unit,
  `ChainLoopOwnerIdx == -1`. Fails if a single member is wrongly unitized
  (which would change today's behavior).
- **Manual-period member breaks the run** into two sub-runs; each >= 2 sub-run
  is its own unit; the manual member is in neither. Fails if the run is not
  split at the non-auto member (edge 3/4).
- **Non-contiguous auto members do not merge** (edge 2). Fails if a
  non-consecutive pair is treated as one unit.
- **Branch > 0 members excluded** (edge 8). Fails if a parallel continuation
  joins the primary-path unit.
- **Zero-duration member excluded; unit dissolves if that drops below 2**
  (edge 7). Fails if a degenerate member produces an invalid span.
- **Span and cadence**: span = first.Start..last.End, cadence = span duration
  (clamped to `MinCycleDuration` for tiny spans, edge 14). Fails if cadence is
  taken from the global gap instead of the span.

### Span loop-phase math (xUnit, extend loop-phase tests)
- **Member visibility windows tile the span**: for sampled `loopUT` across one
  cycle, exactly the member whose `[Start,End]` contains `loopUT` is selected;
  boundaries select the higher index (edge 5). Fails if two members are
  simultaneously selected outside an overlap, or if the wrong member wins an
  overlap.
- **Seamless wrap**: `loopUT` at `spanEnd` maps to the last member's final
  frame; one step past wraps to the first member at `spanStart` (edge,
  back-to-back). Fails if a pause window is inserted at the wrap.
- **Gap handling**: a synthetic chain with a UT gap yields "no member" for
  `loopUT` inside the gap and resumes after (edge 6). Fails if it clamps to a
  stale member.

### Scheduling integration (xUnit)
- **Unit members excluded from the global queue**; standalone auto recordings
  still get staggered slots in the same `trajectories` list. Fails if a unit
  member also receives a global slot (double-scheduling).
- **Mixed list**: standalone auto + a 3-member auto chain -> standalone stays in
  the parade, chain forms one unit. Fails if either leaks into the other's
  scheduling.

### Log-assertion tests (xUnit, via test sink)
- Detection emits the unit-built summary with member indices and span. Fails if
  the diagnostic line is dropped (protects observability through refactors).
- A rejected run emits the specific `<reason>` line for `length<2`,
  `member-not-auto`, and `branch>0`. Fails if a rejection is silent.

### Serialization (xUnit, extend `AutoLoopTests`)
- Round-trip a chain of auto-loop members through `ParsekScenario` save/load and
  confirm `ChainLoopOwnerIdx` is NOT persisted (transient) and is recomputed on
  the next rebuild. Fails if a transient field leaks into the ConfigNode or
  survives load stale.

### In-game (RuntimeTests.cs, FLIGHT)
- Inject a synthetic 3-segment auto-loop chain; over several frames assert
  exactly one ghost GameObject of the unit is active at a time and that the
  active one advances through the members in order, then wraps. Fails if
  multiple unit ghosts are visible at once or the handoff/wrap does not occur.
- Watch a unit and confirm the camera target advances with the live member and
  survives the wrap (edge 10). Fails if the camera sticks to a hidden ghost.
- Debris loop-synced to a unit member follows the unit clock (edge 9). Fails if
  the debris renders on the raw member window instead of the owner's span clock.

---

## Open Questions (resolve before plan)

1. **UI affordance.** v1 forms the unit automatically from per-row loop + auto
   state. Should the Period column show "auto (chain)" for unit members, and/or
   should chain blocks in `RecordingsTableUI` get a single loop toggle that sets
   all members at once? Recommendation: ship v1 with a display hint only; add a
   chain-block toggle as a fast follow.
2. **Inter-cycle pause.** v1 wraps seamlessly (cadence = span). Do we ever want a
   configurable pause between unit cycles (e.g. reuse the standalone auto gap as
   a tail pause)? Recommendation: no in v1; revisit if players ask.
3. **Branch-aware units.** v1 covers branch 0 only. Parallel continuations
   (branch > 0) loop independently. Is unifying branches in-scope later?
