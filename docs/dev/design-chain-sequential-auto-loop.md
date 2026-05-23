# Design: Chain-Sequential Auto Looping

*A mission is a recording tree: a MAIN through-line (the primary vessel's
launch -> ... -> descent recordings) plus SECONDARY recordings (debris and
probes that separated and play in parallel). When two or more CONSECUTIVE main
links of a mission are loop-enabled with period `auto`, they loop as a single
unit on one shared mission clock: the end of one segment runs into the start of
the next and the whole multi-segment span loops back to the beginning, with the
ride-along debris of that mission replaying alongside their parents exactly like
a rewind. The mission looks like one continuous looping flight (vessel plus its
debris), not a set of independently relaunching ghosts. There is NO requirement
that the main links be contiguous in UT: gaps between them are fine. A main link
missing loop+auto breaks the run; secondaries never break it.*

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

What the player wants: set the main links of a mission to loop + `auto`, and have
the mission replay as a single looping unit on one shared clock. The main line
advances through its segments (gaps between them are fine), the mission's debris
replay alongside their parents (like a rewind), and when the span ends the unit
wraps (or waits for the cadence and then wraps). The whole mission loops, not
each piece.

This is a player-facing playback-quality feature. It makes looped chains
(launch -> ascent -> stage -> orbit, captured as a chain) look like the
continuous repeating flights players intuitively expect, instead of a stutter
of mistimed relaunches.

---

## Terminology

- **Tree / Mission**: a set of recordings sharing a `TreeId` (always-tree mode
  gives every recording one), capturing one mission. A mission has a MAIN
  through-line plus SECONDARY recordings; loop-unit detection is scoped per tree.
- **MAIN link**: a recording on the primary-vessel through-line of its tree:
  `IsDebris == false` AND `ParentAnchorRecordingId == null` AND
  `ChainBranch == 0`. The main links, sorted by `StartUT`, are the launch ->
  ... -> descent sequence of the primary vessel.
- **SECONDARY**: everything else in the tree (`IsDebris == true` OR
  `ParentAnchorRecordingId != null` OR `ChainBranch > 0`): debris, probes,
  controlled-decoupled children, loose/orphan debris, parallel ghost-only
  continuations. Secondaries play in PARALLEL with the main line, not in sequence.
- **Run-eligible main link**: a main link that can extend a run: `LoopPlayback`
  AND `LoopTimeUnit == Auto` AND a renderable window (`Points.Count >= 2` and
  positive duration). A main link missing any of these BREAKS the run.
- **Ride-along secondary**: a secondary that loops + auto with a renderable
  window AND whose `[StartUT, EndUT]` overlaps the run span. It rides along as a
  unit member (replays alongside its parent). A secondary WITHOUT loop+auto is
  simply omitted (not rendered) and does NOT affect the chain.
- **Loop unit** (formerly "chain-loop unit"): a maximal run of >= 2 CONSECUTIVE
  run-eligible main links (adjacent in `StartUT` order; UT gaps between them are
  fine, no contiguity requirement) PLUS its ride-along secondaries. The unit is
  the thing that loops as a whole.
- **Unit span**: `[spanStart, spanEnd]` = the minimum `StartUT` to the maximum
  `EndUT` across ALL the unit's members (so a ride-along debris tail can extend
  it). The shared mission clock walks this span.
- **Shared mission clock**: a single loop phase computed over the unit span via
  `TryComputeSpanLoopUT`. At any real UT it resolves to a `loopUT` inside
  `[spanStart, spanEnd]`, the unit cycle index, and an `isInInterCycleTail` flag.
  Each member renders independently when `loopUT` is inside its OWN
  `[StartUT, EndUT]` (so multiple members render concurrently, like a rewind);
  during the inter-cycle tail-wait every member hides.
- **Cadence**: the unit's launch-to-launch period = `max(autoInterval n, span,
  MinCycleDuration)`, where `n` is the resolved global "Auto-launch every N"
  value. If `n > span` there is a wait between cycles (the inter-cycle tail); if
  `n <= span` the unit plays back-to-back (no mid-mission overlap in v1).
- **Unit anchor / owner**: the earliest (lowest-`StartUT`) MAIN link in the run
  (tie-broken by committed index). It owns the shared clock.
- **Global launch queue**: today's flat, save-wide auto stagger
  (`AutoLoopLaunchSchedule`). Standalone auto-looped recordings keep using it;
  loop-unit members are removed from it.

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

With a loop unit, the consecutive auto-looped run of a mission's MAIN links plus
its ride-along debris becomes one virtual recording that loops over its whole
span. A single shared mission clock walks `spanStart -> spanEnd` and wraps. Each
member renders independently when the clock is inside its OWN recorded window;
when the clock is outside that member's window, that member is hidden. Main links
typically tile the span (one main vessel visible at a time), while debris render
CONCURRENTLY with their parent main link, exactly like a rewind.

```
Loop unit (new): main links A,B,C, debris D rides along, span = [t0, t3]:

  shared clock:  t0====t1====t2========t3 | t0====t1====t2========t3 | ...
  main visible:  [==A==][=B=][===C====]    [==A==][=B=][===C====]
  debris D:          [==D==]                   [==D==]   <- alongside its parent
                 ^main-link handoffs           ^wrap: t3 end syncs to t0 start
```

cadence = `max(autoInterval n, span, MinCycleDuration)`. When `n <= span` the
cycles are back-to-back (no gap, seamless wrap). When `n > span` there is a WAIT
between cycles (the inter-cycle tail): the mission plays fully, then ALL members
hide until the next dispatch `n` seconds after the cycle started. There is no
mid-mission overlap in v1.

The loop-synced-debris mechanism (`LoopSyncParentIdx` /
`TryUpdateLoopSyncedDebris`) is conceptual inspiration, NOT a code path we can
reuse. It shares the idea "render only when a shared clock is inside my window,"
but it does not wire up the same way, for two concrete reasons that this design
must own as new work:

1. **The span clock is new computation.** The engine's loop clock is strictly
   per-recording: `TryComputeLoopPlaybackUT` sets
   `playbackStartUT = EffectiveLoopStartUT(traj)` and bounds `loopUT` by
   `EffectiveLoopEndUT(traj)` (`GhostPlaybackEngine.cs`), and those derive from
   the recording's own `LoopStartUT/LoopEndUT/EndUT`. A recording's loop clock
   can never sweep past its own `EndUT` into a sibling's window. So the unit
   span clock (walking `[spanStart, spanEnd]` across multiple members' windows)
   is a genuinely new phase computation, not the owner's existing loop phase.
2. **Followers are loop-enabled, so the debris path never runs for them.** A
   recording that satisfies `ShouldLoopPlayback` takes the loop dispatch and
   `continue`s in the per-frame update BEFORE the loop-synced-debris block is
   reached. `TryUpdateLoopSyncedDebris` only runs for NON-looping recordings
   whose parent loops. Chain-loop members are all loop-enabled, so a NEW
   interception must run at (or before) the loop dispatch site to route unit
   members through the span clock instead of their own independent loop clock.

In short: same idea, new route. The doc treats the span clock and the unit
dispatch as new code throughout.

---

## Data Model

No new persisted fields and no recording-schema change
(`CurrentRecordingSchemaGeneration` unchanged). Units are computed at runtime and
held in transient engine-side state, not on `Recording`.

### Why detection is host-side and the descriptor is opaque to the engine

`GhostPlaybackEngine` is deliberately chain-agnostic: it has zero `Recording`
references and reads only `IPlaybackTrajectory`, which exposes `LoopSyncParentIdx`
but NOT `ChainId` / `ChainIndex` / `ChainBranch` (those live on `Recording`). The
engine is the seed of a future standalone mod where "chain" is a consumer
(Parsek) concept, not an engine concept. Therefore:

- **Detection runs host-side** (in `RecordingStore`, which owns
  `committedRecordings` and the chain fields), producing index-keyed unit
  descriptors.
- **The engine consumes opaque "loop units"** - a set of member indices with a
  shared span schedule and per-member windows - without knowing they came from a
  chain. `IPlaybackTrajectory` does NOT gain chain fields.
- This keeps the chain concept on the consumer side and lets the same descriptor
  feed both schedulers (flight engine and tracking station; see Behavior).

### Index alignment (the invariant the descriptors rely on)

The host builds `cachedTrajectories` 1:1 in `committedRecordings` order before
handing them to the engine, so a committed index is a valid key into the
trajectory list on both sides. Unit descriptors are keyed by this shared index.

### Existing fields detection reads (all already serialized)

```
Recording.TreeId                  string        // mission scope (one tree's recordings)
Recording.IsDebris                bool          // false on a MAIN link
Recording.ParentAnchorRecordingId string        // null on a MAIN link; non-null = secondary
Recording.ChainBranch             int           // 0 on a MAIN link; >0 = parallel continuation (secondary)
Recording.LoopPlayback            bool          // must be true for a run-eligible member
Recording.LoopTimeUnit            LoopTimeUnit  // must be Auto for a run-eligible member
Recording.StartUT/EndUT           double        // member window; run ordering, ride-along overlap, span
```

`Recording.ChainId` / `Recording.ChainIndex` are NOT read by detection (the
main-vs-secondary split is structural, and the run is by `StartUT` order). They
remain on the recording for chain topology / non-loop chain playback.

### New transient types (not persisted)

Host-built descriptor, one per unit, keyed by owner index:

```
struct LoopUnit {
    int    ownerIndex;       // earliest MAIN link's committed/trajectory index
    int[]  memberIndices;    // committed indices of all members (main + ride-along), StartUT order
    double spanStartUT;      // min member StartUT
    double spanEndUT;        // max member EndUT (a ride-along debris tail can extend it)
    double cadenceSeconds;   // max(autoInterval n, span, MinCycleDuration)
}
```

Engine-side per-rebuild state (mirrors how `autoLoopLaunchSchedules` is held -
a per-index dictionary rebuilt each schedule pass, NOT a field written onto
`Recording` every frame):

```
Dictionary<int, LoopUnit> loopUnits;          // keyed by ownerIndex
Dictionary<int, int>      loopUnitOwnerByIdx;  // memberIndex -> ownerIndex (-1 / absent = not a unit member)
```

Note: do NOT model this on `LoopSyncParentIdx`, which is a serializable
`{ get; set; }` property populated once at load by
`PopulateLoopSyncParentIndices`. Unit membership is volatile (it flips when the
player toggles loop/period) and must be recomputed on the schedule rebuild, so it
belongs in transient engine state, not on the persisted recording.

### Detection (host-side helper `RecordingStore.DetectChainLoopUnits`)

The MAIN-LINK-RUN model, scoped per tree (`globalAutoIntervalSeconds` = the
resolved global "Auto-launch every N" value, passed in by both callers):

1. Dormant fast-out: return `Empty` when fewer than 2 run-eligible MAIN links
   exist (the common save), with no allocation. A single main link is not a unit,
   and secondaries only ride along, so this is a guaranteed no-op.
2. Group every recording by `TreeId` (a null/empty `TreeId` cannot belong to a
   mission, so it is skipped).
3. Within each tree, collect the MAIN links (`IsMainLink`) sorted by `StartUT`
   (tie-break by committed index). Sweep them: extend the current run while the
   next main link is run-eligible (loop+auto+renderable). A run-INELIGIBLE main
   link BREAKS the run (there is NO UT-contiguity requirement: gaps between
   consecutive main links are fine). Each run of >= 2 main links forms a unit.
4. For each run of >= 2 main links, build a `LoopUnit`:
   - `ownerIndex` = the earliest main link in the run.
   - Members = the run's main links + every ride-along SECONDARY in the same tree
     (`IsRideAlongSecondary`: loops+auto+renderable) whose `[StartUT, EndUT]`
     overlaps the run's main-link span. Members are sorted by `StartUT`.
   - `spanStartUT` = min `StartUT`, `spanEndUT` = max `EndUT` over ALL members
     (so a ride-along debris tail can extend the span).
   - `cadenceSeconds` = `max(globalAutoIntervalSeconds, span, MinCycleDuration)`.

Members not in any unit are absent from `loopUnitOwnerByIdx` and behave exactly as
today. Two main links with different (or no) `ChainId` but consecutive in
`StartUT` order in the same tree DO form one unit. Secondaries without loop+auto
are omitted (not rendered) and never affect the run.

---

## Behavior

### Forming the unit

- The host detects units (host-side helper, above) and rebuilds the descriptors
  on each playback schedule rebuild, the same pass that builds
  `autoLoopLaunchSchedules`. The descriptors are handed to both the flight engine
  and the tracking-station scheduler (see below).
- Members of a unit are EXCLUDED from the flat global launch queue: the
  global-queue eligibility check skips any index present in
  `loopUnitOwnerByIdx`, so unit members never receive a staggered global slot.
  They are scheduled by their unit instead.
- A standalone auto-looped recording, or a single auto-looped chain member that
  has no auto-looped neighbor, is NOT a unit (run length 1) and keeps today's
  global-stagger behavior.

### Playing the unit (new interception + span clock)

- A NEW dispatch runs at the loop dispatch site, BEFORE the standalone loop
  dispatch that would otherwise route each looping recording onto its own clock.
  If the current index is a unit member, it is routed through the span clock and
  the standalone loop path is skipped for it.
- A pure helper (`TryComputeSpanLoopUT` in `GhostPlaybackLogic`, alongside the
  existing loop-phase math) computes the shared mission clock: given `currentUT`,
  `spanStartUT`, `spanEndUT`, and `cadenceSeconds`, it returns a `loopUT` inside
  `[spanStart, spanEnd]`, the unit cycle index, and `isInInterCycleTail`. When
  `cadence <= span` the wrap from `spanEnd` back to `spanStart` is seamless. When
  `cadence > span` the phase parks at `spanEnd` for the remainder of the cadence
  (`isInInterCycleTail == true`): the WAIT between cycles. The cadence is clamped
  to `LoopTiming.MinCycleDuration` inside this helper (the clock does NOT get
  `ResolveLoopInterval`'s clamp for free).
- Each member renders INDEPENDENTLY (no cross-member selection): the pure
  `DecideUnitMemberRender` checks ONLY whether the shared `loopUT` falls in THIS
  member's own `[StartUT, EndUT]`. If so it renders at `loopUT` via the normal
  in-range render path (the same clock-override technique `TryUpdateLoopSyncedDebris`
  uses); otherwise the member is hidden / destroyed for that frame.
- During the inter-cycle tail-wait (`isInInterCycleTail`), ALL members hide
  (render nothing) until the next cycle.
- Multiple members render CONCURRENTLY: a debris and its parent main link, whose
  windows overlap, both render at the same `loopUT`, exactly like a rewind. Main
  links typically tile the span (one main vessel visible at a time) and the
  visible main ghost changes at each main-link boundary, reproducing the chain
  handoff under looping. On a cycle wrap the ghosts are rebuilt for a clean state.
- Same-vessel main-link overlap at a seam (two main links of the same vessel
  whose windows overlap sub-second from an optimizer split): in v1 BOTH may
  briefly render for the sliver of overlap (acceptable). Different vessels always
  render concurrently by design.

### Tracking-station scheduler (scoped IN)

`ParsekKSC` has its own copy of the auto-loop machinery (its own
`autoLoopLaunchSchedules` dict, its own `RebuildAutoLoopLaunchScheduleCache`, its
own loop-UT math). If units were implemented only in `GhostPlaybackEngine`, a
looped chain would replay as a unit in flight but as the old independent parade
in the tracking station - a visible inconsistency. v1 scopes both schedulers in
by sharing code: unit detection and the span-clock helper are pure/host-side and
consumed by BOTH `GhostPlaybackEngine` and `ParsekKSC`. Map-marker positioning is
driven by the engine positioning ghosts, so no third scheduler is involved
(`GhostMapPresence` only makes spawn/lifecycle decisions, e.g. `IsChainLooping`).

### Cycle transitions

- When the span clock wraps (`loopUT` returns to `spanStart`), the unit cycle
  index increments. Members rebuild their ghost on cycle change (same
  cycle-change ghost rebuild the debris loop-sync path already performs) so each
  cycle starts from a clean visual state.

### Cadence / period meaning

- For a loop unit, cadence = `max(autoInterval n, span, MinCycleDuration)`, where
  `n` is the global "Auto-launch every N" value. When `n <= span` the unit plays
  back-to-back (no wait); when `n > span` the unit plays fully, then ALL members
  hide for the rest of `n` (the inter-cycle wait) before the next cycle. Either
  way the whole chain always plays in full. This is intentionally a different
  resolution of `auto` than the standalone meaning ("relaunch on the global
  gap"). The two never apply to the same recording at once because unit members
  are removed from the global queue.

### Interaction with non-unit ghosts

- Standalone auto-looped recordings continue to share the global stagger parade
  among themselves; the unit does not join that parade.
- Manual-period (`Sec`/`Min`/`Hour`) main links are never part of a unit and
  loop independently as today. A manual-period main link sitting between two auto
  main links BREAKS the run (it is not run-eligible).

---

## Edge Cases

Each: scenario -> expected behavior -> v1 disposition.

1. **Only one run-eligible main link in a tree.** Run length 1. -> Not a unit;
   behaves exactly as today (global stagger). v1.
2. **Auto main links with a UT gap between them** (an edit left a gap, or a
   non-overlapping coast). -> The gap is IRRELEVANT: as long as the main links are
   consecutive and all run-eligible, they form one unit. cadence = `max(n, span)`,
   so the gap is simply part of the span. v1, the headline fix this revision adds.
3. **Two auto main links with different (or no) `ChainId`** (a chainless launch
   recording followed by a separate descent chain head in one tree). -> They form
   ONE unit (the run is by `StartUT` order, not `ChainId`). v1.
4. **A non-run-eligible main link (manual period / not looping) between two auto
   main links.** It BREAKS the run. The main links before and after are then NOT
   consecutive, so each side of length >= 2 is its own unit; a lone side is not a
   unit. v1.
5. **Adjacent main links with overlapping UT windows** (post-optimizer splits
   routinely leave a sub-second overlap). They are still consecutive main links,
   so they join the same run. When `loopUT` is in both windows, BOTH render for
   the sliver of overlap (concurrent render, no single-member selection). For two
   main links of the SAME vessel this brief double-render is acceptable in v1;
   different vessels always render concurrently by design. v1.
6. **Gap inside the span where no member's window covers `loopUT`** (UT
   discontinuity from edits/splits, between consecutive main links). The members
   are still part of one unit (gaps do not break the run). If `loopUT` lands in
   such a sub-gap, no member renders for that sliver, then the next member picks
   up. -> Accept the brief invisibility. v1.
7. **A main link has < 2 trajectory points / zero duration.** It is NOT
   renderable, so it is run-INELIGIBLE and BREAKS the run (same as a manual main
   link). v1.
8. **Branch > 0 members, debris, controlled children = SECONDARY.** A
   `ChainBranch > 0` parallel continuation, `IsDebris == true` debris, or
   `ParentAnchorRecordingId != null` controlled-decoupled child is NEVER a main
   link. It can RIDE ALONG (replay alongside the main line) if it loops+auto and
   overlaps the run span; otherwise it is simply omitted. A secondary never seeds
   or breaks the run. v1.
9. **Debris loop-synced to a member that is now a unit member**
   (`LoopSyncParentIdx` points at a unit member). `TryUpdateLoopSyncedDebris`
   would otherwise call `TryComputeLoopPlaybackUT(parent, ...)`, giving the
   parent's OWN per-recording loop clock - wrong once the parent is a unit member
   whose timing is governed by the shared mission clock. -> When the debris's
   parent is a unit member, the debris sources the unit's shared `loopUT`
   (resolved via the parent's `ownerIndex` in `loopUnitOwnerByIdx`) and renders
   when that `loopUT` is in the debris's own `[StartUT, EndUT]`. v1.
10. **Watch mode on a unit.** For NON-loop chains, watch hands off by the
    chain-seam handoff. Unit members are loop-enabled and take the shared-clock
    route, so that transfer never fires. The camera follows ONE watched member;
    when the shared clock leaves that member's window (it stops rendering) and a
    different live member exists, a unit-handoff retarget moves the camera to the
    new live member (the in-window member with the highest `StartUT`). During the
    inter-cycle wait or a gap (no live member) the camera holds its current anchor
    rather than yanking to nothing, and a watched member's ghost is hidden (not
    destroyed) so the camera target is never a destroyed ghost. v1.
11. **Terminal vessel spawn at loop end.** A looping chain spawns NOTHING:
    `ShouldSpawnAtRecordingEnd` returns `(false, "chain looping")` for the whole
    chain whenever any branch-0 member loops (via `IsChainLooping`). There is no
    per-segment spawn and no terminal spawn while the chain loops. The unit
    changes ghost timing only; it does not touch spawn policy, and the existing
    "chain looping -> no spawn" guard already covers it. v1, no new code needed.
12. **Time warp.** Warp suppression hides moving ghosts at high warp today. The
    unit uses the same per-frame render path, so warp suppression applies to the
    currently-live member unchanged. v1.
13. **Member edited / chain re-topologized at runtime** (rare; merges, reverts).
    The unit is rebuilt from scratch on the next schedule rebuild, so membership
    self-heals. No persisted unit state to go stale. v1.
14. **Very short span** (span below `MinCycleDuration` AND a tiny autoInterval).
    The detection clamps `cadenceSeconds` to `LoopTiming.MinCycleDuration` as the
    third term of `max(n, span, MinCycleDuration)`, and the span-clock helper
    re-applies the clamp (it does NOT route through `ResolveLoopInterval`). v1.
15. **Very long span / many cycles live.** Because cadence >= span, only one
    cycle's worth of the unit is ever live at a time (no inter-cycle overlap), so
    the `MaxOverlapGhostsPerRecording` cap is not stressed by the unit itself. v1.
16. **Chain spans a body change or scene-relevant transition mid-unit.** The unit
    is purely a playback-timing construct over recorded windows; each member still
    renders through its own recorded surface (absolute / relative / orbit) at
    `loopUT`. No new positioning math. v1.
17. **Anchor / relative-frame member inside a unit** (`LoopAnchorVesselId` set).
    The member still resolves its own anchor at `loopUT` via existing relative
    playback. The unit only supplies the clock. Anchor gating (skip when anchor
    unloaded) short-circuits per-member. v1.
18. **All main links of a mission are auto-loop, with debris** (the common case).
    Single unit covering the whole mission; the main line loops as one flight and
    its debris replay alongside their parents. v1, the headline scenario.
19. **cadence > span (inter-cycle wait).** When the global autoInterval `n` is
    larger than the span, the unit plays fully then ALL members hide
    (`isInInterCycleTail`) until the next cycle `n` seconds after the cycle start.
    This is the literal "Auto-launch every N" cadence applied to the whole unit. v1.

---

## What Doesn't Change

- The recording schema and all serialization. No new persisted fields, no
  generation bump.
- `IPlaybackTrajectory` stays chain-agnostic - no `ChainId` / `ChainIndex` /
  `ChainBranch` added to the engine interface. The engine consumes opaque loop
  units; chain detection stays host-side.
- Standalone (non-chain) auto-loop behavior and the global launch queue for
  standalone recordings.
- Manual-period looping (`Sec`/`Min`/`Hour`) for any recording.
- Non-looping chain playback and the existing chain-seam handoff / chain-shadow
  logic (the unit is a looping construct; non-loop chains are untouched).
- Ghost mesh construction, trajectory interpolation, per-member positioning
  surfaces (absolute / relative / orbit), part-event and FX replay.
- Spawn policy, resource ledger, rewind, merge, and timeline semantics. A
  looping chain already spawns nothing (the `"chain looping"` guard), so the unit
  needs no spawn-side change.

## Risk and Hardest Part

The hard part is NOT the span-clock arithmetic (that is a small pure helper).
The risk concentrates in three new code routes that the loop-synced-debris
analogy does NOT give us for free:

1. **Follower interception** at the loop dispatch site: loop-enabled unit members
   must be pulled out of the standalone loop dispatch and onto the span clock,
   in BOTH `GhostPlaybackEngine` and `ParsekKSC`. (Highest structural risk.)
2. **Debris-on-unit-member** (edge 9): the existing debris dispatch reads the
   parent's own loop clock and must be taught to read the span clock when the
   parent is a unit member.
3. **Watch transfer at unit-internal boundaries** (edge 10): the chain-seam
   transfer the camera relies on is bypassed for loopers, so a new transfer
   trigger is needed (or the documented owner-only-follow fallback).

The plan should make each of these its own task and order them after the pure
helper + detection land and are unit-tested.

---

## Backward Compatibility

No save migration. Loop units are derived at runtime from existing fields, so old
saves and recordings load unchanged and gain the behavior automatically once a
mission has >= 2 consecutive loop+auto main links. There is no on-disk
representation of a unit to version. Removing the feature (or a main link dropping
out of auto) simply reverts those members to the global-stagger behavior on the
next schedule rebuild.

---

## Diagnostic Logging

Subsystem tags: `Loop` for detection/scheduling decisions, `Engine` for
per-frame render dispatch (rate-limited).

Detection (one-shot per rebuild, `Verbose` with a summary count, per
batch-counting convention):
- `RecordingStore`/`Loop`: "Chain-loop units: built N unit(s), rejected M run(s)
  from K tree(s)", plus a per-unit detail line "Chain-loop unit: owner=o
  members=i,j,k span=spanStart..spanEnd cadence=Xs". One summary line per
  rebuild; per-unit detail only when units exist.
- `Loop`: a lone run-eligible main link logs "tree <treeId> run [i..i] not a
  unit: length<2". A main link that breaks the run logs "tree <treeId> main link
  recIdx=j (StartUT=...) breaks run: <reason>" (e.g. member-not-auto /
  member-not-looping / member-zero-duration) so the player can see WHY two autos
  did not merge. This makes "why didn't these loop as one" answerable from the
  log alone.

Scheduling:
- `Loop`: "chain-loop member recIdx=i excluded from global auto queue (unit
  owner=o)" so the global-queue / unit split is visible.

Per-frame render (rate-limited via `VerboseRateLimited`, per-unit key):
- `Engine`: on camera-live-member handoff: "unit owner=o camera-live member
  #i->#j at loopUT=..." (rate-limited, key per unit).
- `Engine`: on cycle wrap: "unit owner=o wrapped cycle c-1->c at UT=...".
- `Engine`: on the inter-cycle wait: "unit owner=o in inter-cycle wait at
  loopUT=... - all members hidden" (rate-limited, key per unit).
- `Engine`: on a member hidden outside its own window: "unit owner=o member #i
  hidden: loopUT=... outside its window [start,end]" (rate-limited, per member).
- `CameraFollow`: "unit watch retarget owner=o member #i->#j at loopUT=..." when
  the watched member stops rendering and a new live member exists.

Every branch that hides a member or skips a frame must log its reason, consistent
with the project's "silent code paths are debugging blind spots" rule.

---

## Test Plan

Pure-logic and serialization tests are xUnit; anything needing live KSP (ghost
GameObjects, watch camera, real spawn) is an in-game test in `RuntimeTests.cs`.

### Unit detection (xUnit, `ChainLoopUnitTests`)
- **>= 2 consecutive auto-loop main links form one unit EVEN WITH UT GAPS**
  between them (the headline fix). Fails if a UT gap between main links still
  breaks the run.
- **A middle main link missing loop+auto breaks the run** so the surrounding main
  links are not consecutive and no unit forms. Fails if a non-eligible main link
  is silently skipped (bridging the two halves).
- **A,B loop+auto, trailing C does not -> unit {A,B}.** Fails if the trailing
  break drops earlier members too.
- **A single main link is not a unit** (loops standalone as today). Fails if a
  lone main link is unitized.
- **Ride-along debris** (loop+auto, overlaps the run) is included as a unit
  member; **debris WITHOUT loop+auto is omitted and does NOT break the unit.**
  Fails if a non-loop secondary is pulled in, or if its absence breaks the run.
- **A parent-anchored probe / orphan debris is SECONDARY** (never a main link,
  never breaks the run). Fails if it is treated as a main link.
- **Different `TreeId` -> not merged.** Detection is scoped per tree.
- **Zero-duration / < 2-point main link breaks the run** (not renderable). Fails
  if a degenerate main link is treated as run-eligible.
- **Cadence = max(autoInterval n, span, MinCycleDuration)**: when `n > span`
  there is a tail (`isInInterCycleTail` reachable); when `n <= span`, cadence ==
  span. **Span = min/max over members including ride-along debris.** Fails if
  cadence ignores `n` or the span ignores the debris tail.

### Span loop-phase math (xUnit)
- **TryComputeSpanLoopUT**: seamless wrap when cadence <= span; the parked tail
  (`isInInterCycleTail`) when cadence > span; MinCycleDuration clamp; before-start
  / zero-span early returns.
- **IsLoopUTInMemberWindow / DecideUnitMemberRender**: a member renders iff the
  shared clock is in its OWN window; two OVERLAPPING members BOTH render in the
  overlap (concurrent, no single-member selection); the inter-cycle tail hides
  EVERY member; before span start the clock is unresolved.

### Scheduling integration (xUnit)
- **Unit members excluded from the global queue**; standalone auto recordings
  still get staggered slots. Fails if a unit member also receives a global slot.
- **Mixed list**: standalone auto + a 3-member auto chain -> standalone stays in
  the parade, chain forms one unit.
- **Dual-scheduler parity**: the identical recording set feeds both schedulers;
  both produce the same unit (owner, members, span, cadence).

### Log-assertion tests (xUnit, via test sink)
- Detection emits the unit-built summary with member indices and span.
- A lone main link logs `length<2`; a run break logs `breaks run: <reason>`.

### Serialization (xUnit, `AutoLoopTests`)
- Round-trip a chain of auto-loop main links through `ParsekScenario` save/load
  and confirm NO new ConfigNode keys are written for unit membership, and that
  the unit reconstitutes from the loaded loop/period state on the next detection.

### In-game (RuntimeTests.cs, FLIGHT)
- Inject a synthetic 3-segment auto-loop chain; over several frames assert the
  active main ghost advances through the members in order, then wraps.
- Watch a unit (edge 10): the camera target advances with the live member and
  survives the wrap; the camera target is NEVER a deactivated/destroyed ghost.
- Debris loop-synced to a unit member follows the shared mission clock (edge 9),
  rendering concurrently with its parent.

---

## Open Questions (resolve before plan)

1. **UI affordance.** v1 forms the unit automatically from per-row loop + auto
   state. Should the Period column show "auto (chain)" for unit members, and/or
   should chain blocks in `RecordingsTableUI` get a single loop toggle that sets
   all members at once? Recommendation: ship v1 with a display hint only; add a
   chain-block toggle as a fast follow.
2. **Inter-cycle pause.** RESOLVED: cadence = `max(autoInterval n, span)`, so when
   `n > span` the unit already has a wait between cycles (the global "Auto-launch
   every N" gap applied to the whole unit); when `n <= span` it wraps seamlessly.
3. **Branch-aware units.** v1 treats branch > 0 as SECONDARY (rides along if
   loop+auto and overlapping). A branch > 0 continuation never seeds or breaks a
   main-link run. Is promoting a branch to its own main line in-scope later?
