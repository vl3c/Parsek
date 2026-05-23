# Design: Mission Abstraction Hierarchy

*Status: abstraction definition only. This document fixes the layered vocabulary
that sits above raw recordings (recording -> main line -> mission tree ->
mission subtree -> supply run -> supply route). It does not specify
implementation; that comes later and may reuse parts of the
design-chain-auto-loop branch. The upper layers (supply run, supply route) are
named here but defined later by the logistics work.*

---

## Why this exists

Recordings are raw building blocks. To do anything gameplay-meaningful with them
(the headline case: loop a whole supply run as one continuous repeating flight,
not a stutter of independently relaunching ghosts) we need higher abstractions
that put recordings in the right order and hierarchy, so we can say cleanly
which group of recordings is one logical, loopable unit.

Today the Recordings window groups recordings by TYPE and storage structure, not
by gameplay logic. On launch a mission group is created (one per tree), and
inside it the auto-grouping creates at most two subgroups:

- a `/ Debris` subgroup, pooling all debris regardless of which parent shed it;
- a `/ Crew` subgroup, holding EVA recordings.

Everything else (the main-line recordings) sits directly under the mission
group, ungrouped among itself. In the table view, recordings are additionally
blocked by `ChainId` (a within-session continuity link), not by any
chronological mission through-line. (A stash virtual group exists for re-fly and
is out of scope here.)

None of those groupings encode "this recording continues into that one." There
is no chronological through-line anywhere in what the window shows. So any
feature that needs "this contiguous set of recordings is one logical unit"
(looping, supply runs) has nothing clean to stand on and ends up inferring the
unit from low-level structure (group type, ChainId blocks, or per-row loop
flags). That inference is fragile. This document builds the missing middle layer
so the unit is a defined thing, not an inferred one.

---

## Existing recording fields this hierarchy is built from

On `Recording`:

- `TreeId`        - mission identifier; stable across scene changes (serialized).
- `ChainId`       - intra-session continuity link; NOT stable (serialized).
- `ChainIndex`    - order within a chain (serialized).
- `ChainBranch`   - 0 on the primary through-line; > 0 on parallel continuations.
- `IsDebris`      - true on debris.
- `ParentAnchorRecordingId` - non-null on parent-anchored recordings (debris AND
  controlled-decoupled children); null on a main-line recording.
- `StartUT` / `EndUT` - the recording's recorded time window. These are COMPUTED
  properties (derived from the trajectory points, with `ExplicitStartUT` /
  `ExplicitEndUT` serialized overrides and a `0.0` fallback), not raw stored
  timestamps. Spine ordering must use the computed value.

Terminology note: a "node" in this document means a boundary point between two
consecutive main-line recordings, including the spine's Start and End points. It
does NOT mean a recording, and "subtree" does not mean a graph-theory rooted
subtree (see layer 5).

---

## The hierarchy (bottom-up)

### 1. Recording (the atom)

A bounded recording is the atom. Crucially, "recording" throughout this document
means the recording AS IT EXISTS AFTER the optimizer has run: exactly what the
Recordings window shows once the optimization pass completes. The abstraction
never operates on the recorder's raw in-flight buffer; it operates on the final,
optimized recording set. Every higher structure is composed of WHOLE
(post-optimizer) recordings, and boundaries always fall between recordings, never
mid-recording.

Atom boundaries in the final set come from two sources, but the distinction is
only PROVENANCE: by the time the abstraction reads the recordings, both have
already been applied, so the hierarchy never needs to care which produced a given
boundary.

- Recorder-side commits at discrete gameplay events: launch, dock
  (`OnPartCouple`), undock (`OnVesselsUndocking`), EVA / board (ChainToVessel),
  scene exit / recovery (`FinalizeTreeOnSceneChange`).
- Optimizer-side splits at environment / body transitions (atmosphere / altitude
  / SOI crossings). In always-tree mode the recorder SUPPRESSES the in-flight
  split (`ShouldSuppressBoundarySplit`); the split into separate recordings is
  applied afterwards by `RecordingOptimizer.IsSplittableEnvOrBodyBoundary` (on
  commit, merge, and load), over a FILTERED subset of transitions (surface
  grazes, brief bracketed runs under 120s, cohesive cross-body coasts, and
  boundary seams are suppressed).

Boundaries do NOT exist in the final set at a general vessel switch (the prior
vessel drops to background; the spine continues on a new recording via layer 2)
or at staging / part-composition change (the recording continues, logging part
events).

Consequence: discrete waypoints through dock / undock / EVA / scene exit, and
major environmental transitions, are all guaranteed to appear as atom boundaries
in the final set. If a future discrete waypoint is NONE of these, making it an
atom boundary is recorder-side work (a `StopRecordingForChainBoundary`-style
commit on the event), not an optimizer change.

### 2. Main line (the spine)

The mission's primary through-line: an ordered sequence of recordings from a
Start node to an End node.

- Scope: one `TreeId` (the mission).
- Members: main-link recordings, defined structurally as
  `IsDebris == false AND ParentAnchorRecordingId == null AND ChainBranch == 0`.
- Order: by `StartUT`. NOT by `ChainId` (see the spine-threading decision below).
- Contiguity: causal / chronological, with UT gaps allowed. Gaps (typically
  small, between recordings) are normal and must be analyzed, not treated as a
  break. Contiguity means an unbroken ordered through-line, not abutting UT
  intervals.
- Start node: usually launch. End node: a final state (landed, in orbit,
  recovered, and so on).

How the spine crosses controller vessels: a general vessel switch creates no
boundary, so the spine does not advance at the switch itself. Instead the new
active vessel produces its own main-link recording (same `TreeId`,
`IsDebris=false`, `ParentAnchorRecordingId=null`, `ChainBranch=0`) through one of
two paths: the post-switch first-modification auto-record
(`PrepareActiveTreeForFreshPostSwitchRecording`), or an explicit Fly / Switch-To
continuation (`SwitchSegmentBuilder.CreateSwitchContinuationSegment`, a
`VesselSwitchContinuation`). Caveat the spine must account for: the
first-modification path only materializes a recording once the player acts on
the new vessel. A switch-and-coast with no input leaves a real UT gap with no
main-line recording for that stretch. This is one source of the "UT gaps
allowed" rule, and it means the spine cannot assume gap-free coverage.

### 3. Branches

Debris and parent-anchored children, attached to a main-line recording via
`ParentAnchorRecordingId`. Secondary: they play in PARALLEL with the spine (like
a rewind), never in sequence with it.

Caveat (see open question 4): `ParentAnchorRecordingId != null` covers two
populations. Genuine debris (`IsDebris=true`) is correctly a parallel branch. But
controlled-decoupled children (`IsDebris=false`: probes, landers, capsules that
come off through a decoupler) are independent controlled flights that record
their own Absolute tails and get their own map presence and orbit lines. The
current main-link predicate excludes them (because their
`ParentAnchorRecordingId` is non-null), so they are treated as parallel branches.
A supply run that wants to express "decouple a lander and fly it down to a base"
as part of the loopable unit cannot do so under the predicate as written.

### 4. Mission tree

The whole thing: the full main line (launch -> final state) plus all its
branches, scoped by `TreeId`. This is what exists in the data today.

### 5. Mission subtree (= mission segment)

An arbitrarily-defined bounded slice of the main line (Start node -> End node,
both on node boundaries) plus the branches attached within that slice.

- tree : mission-subtree is 1 : many.
- A mission subtree may be the whole tree or a proper part of it.
- "Subtree" here means a BOUNDED SLICE, closed at both ends. It is not a
  root-anchored graph subtree (which would be open-ended down to the leaves).
  The end-bound is exactly what makes a segment a segment.
- Mission subtrees MAY OVERLAP (share recordings). Overlap semantics belong to
  the logistics layer later. Overlap is not a blocker: the engine already
  renders the same recording concurrently on multiple clocks for looping
  (the overlap-ghost path, `MaxOverlapGhostsPerRecording`).

### 6. Supply run (deferred)

An ordered list of mission segments from a defined start to a defined end. The
loopable unit; it repeats periodically. Defined later by logistics.

### 7. Supply route / list of supply routes (deferred)

The top layer. A supply route is a supply run that repeats periodically; the
list of supply routes is the top-level collection. Defined later by logistics.

---

## Key decisions and the evidence behind them

### Boundaries are on nodes only

Start and End of a mission subtree always land on node boundaries (between
recordings), never mid-recording. A subtree is therefore always a whole number
of consecutive main-line recordings. This holds regardless of which layer
(recorder or optimizer) created a given boundary.

### Spine threading: TreeId + StartUT, NOT ChainId

The spine is ordered by `StartUT` within a `TreeId`, not by following the
`ChainId` / `ChainIndex` linkage. This was verified against the recorder code:

- `TreeId` is stable across every scene change (on scene resume only
  `ActiveTreeId` is restored).
- `ChainId` is continuous WITHIN a single flight session through env / altitude /
  body splits, dock, and EVA / board, but it RESETS in at least two places:
  - vessel switch: `ChainSegmentManager.CommitVesselSwitchTermination` clears
    chain identity (`advanceChain: false` then `ClearChainIdentity`).
  - scene exit / resume: `ActiveChainId` is not restored on resume, so the
    post-resume recording gets a fresh `ChainId`.

Because `ChainId` resets at vessel switches and at every scene change, it cannot
thread a mission spine that spans multiple sessions and vessels. The through-line
must be reconstructed from `TreeId` scope plus `StartUT` order over the main-link
recordings.

`ChainId` / `ChainIndex` are still meaningful: they drive the non-loop
chain-seam handoff within a session. They are an intra-session sub-link, not the
mission through-line.

Note: the design-chain-auto-loop branch independently arrived at "main-link run
ordered by StartUT" (it deliberately dropped ChainId-based grouping). That model
is correct for the spine; this document promotes it from a transient
loop-detection device into the durable main-line abstraction.

### Overlap is allowed

Two mission subtrees may share recordings (for example S-A-B-E and S-A). This is
fine because a mission subtree is a saved definition, and looping activates one
run at a time; the only rule looping needs is that overlapping subtrees are
mutually exclusive while simultaneously active, while non-overlapping ones may
loop concurrently. The full overlap semantics are a logistics-layer concern for
later.

---

## What exists today vs what is new

- Layers 1 to 4 (recording, main line, branches, mission tree) already exist in
  the data. The main line is not yet a first-class object, but every field
  needed to reconstruct it is present and serialized.
- Layer 5 (mission subtree) is the new abstraction this work introduces.
- Layers 6 and 7 (supply run, supply route) are named but defined later by
  logistics.

---

## Open questions

1. How do Start / End nodes get DEFINED on the spine: authored by the player,
   derived from gameplay state transitions, or a mix (derive candidates, let the
   player confirm and name)? Unresolved.
2. Persistence of a mission subtree: it is derived from existing fields, but a
   named, player-defined slice needs to be saved somehow. Storage shape TBD.
3. Reuse from the design-chain-auto-loop branch: the span-clock playback
   mechanism (one shared clock walking a multi-recording span, members rendering
   when the clock is in their own window, debris riding along) is the playback
   primitive a looped mission subtree needs. Decide what to lift vs rebuild.
4. Controlled-decoupled children in the spine. They are independent controlled
   flights but the current main-link predicate buries them as parallel branches
   (layer 3 caveat). Options: (a) refine the predicate so a controlled-decoupled
   child that carries an independent controlled flight can join the spine, or be
   promotable to its own spine; (b) leave it as a documented limitation the
   supply-run layer must work around. This blocks "decouple a lander and fly it
   down" as part of a loopable run, so it needs an answer before the supply-run
   layer is built.

---

## References

- `design-chain-auto-loop` branch: prior attempt at looping consecutive
  auto-loop main links as a unit. Its span-clock playback mechanism is
  reusable; its unit-detection-from-loop-flags model is superseded by the
  defined mission-subtree abstraction here.
- Recorder boundary creation: `ParsekFlight.OnPartCouple` /
  `OnVesselsUndocking`, `ChainSegmentManager` commit sites,
  `FinalizeTreeOnSceneChange`.
- Boundary-split suppression in tree mode: `ParsekFlight.ShouldSuppressBoundarySplit`
  and the `HandleAtmosphereBoundarySplit` / `HandleSoiChangeSplit` /
  `HandleAltitudeBoundarySplit` early-returns.
- Environment / body recording splits (optimizer):
  `RecordingOptimizer.IsSplittableEnvOrBodyBoundary`, run by
  `RecordingStore.RunOptimizationPass`.
- Chain identity reset: `ChainSegmentManager.CommitVesselSwitchTermination`
  (vessel switch) and scene-resume restoring only `ActiveTreeId`.
- Cross-vessel spine continuation: `PrepareActiveTreeForFreshPostSwitchRecording`
  and `SwitchSegmentBuilder.CreateSwitchContinuationSegment`.
- Auto-grouping (today's window structure): `RecordingGroupStore.AutoGroupTreeRecordings`.
- Concurrent same-recording render for looping: `MaxOverlapGhostsPerRecording`
  (overlap-ghost path in `GhostPlaybackEngine`).
