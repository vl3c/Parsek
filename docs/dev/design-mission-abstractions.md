# Design: Mission Abstraction Hierarchy

*Status: abstraction definition plus a first concrete feature (the Missions
window and Mission-level looping). This document fixes the layered vocabulary
above raw recordings and specifies the first UI/feature that exercises it. The
upper logistics layers (supply run, supply route) are named here but defined
later; a looped Mission is their v1 precursor.*

---

## Why this exists

Recordings are raw building blocks. To do anything gameplay-meaningful with them
(the headline case: loop a whole mission as one continuous repeating flight, not
a stutter of independently relaunching ghosts) we need higher abstractions that
put recordings in the right order and hierarchy, so we can say cleanly which
group of recordings is one logical, loopable unit.

Today the Recordings window groups recordings by TYPE and storage structure, not
by gameplay logic. On launch a mission group is created (one per tree), and
inside it the auto-grouping creates at most two subgroups: a `/ Debris` subgroup
pooling all debris regardless of parent, and a `/ Crew` subgroup for EVA
recordings. The main-line recordings sit directly under the mission group,
ungrouped among themselves; the table additionally blocks recordings by `ChainId`
(a within-session continuity link), not by any chronological mission through-line.

None of those groupings encode "this recording continues into that one," and none
expresses a separation fork. There is no chronological through-line and no
branch structure anywhere in what the window shows, so any feature that needs
"this contiguous set of recordings is one logical unit" (looping, supply runs)
has nothing clean to stand on. This document builds the missing middle layer so
the unit is a defined, player-configurable thing.

---

## Existing recording fields this hierarchy is built from

On `Recording`:

- `TreeId`        - mission identifier; stable across scene changes (serialized).
- `ChainId`       - intra-session continuity link; NOT stable (serialized).
- `ChainIndex`    - order within a chain (serialized).
- `ChainBranch`   - 0 on a primary line; > 0 on parallel continuations.
- `IsDebris`      - true on debris. THE discriminator between a spine-eligible
  leg and a parallel-only twig (see layer 2).
- `ParentAnchorRecordingId` - non-null on parent-anchored recordings (debris AND
  controlled-decoupled children); points at the parent recording. This is what
  carries the FORK topology.
- `StartUT` / `EndUT` - the recording's recorded time window. COMPUTED properties
  (from the trajectory points, with `ExplicitStartUT` / `ExplicitEndUT`
  serialized overrides and a `0.0` fallback), not raw stored timestamps. Sequence
  ordering uses the computed value.

Terminology note: a "node" means a boundary point between two consecutive legs,
including a path's Start and End points. It is NOT a recording. A "leg" is one
recording (post-optimizer). "Subtree" means a bounded selection, not a
graph-theory rooted subtree.

---

## The hierarchy (bottom-up)

### 1. Recording (the atom)

A bounded recording is the atom. "Recording" throughout means the recording AS IT
EXISTS AFTER the optimizer has run: exactly what the Recordings window shows once
the optimization pass completes. The abstraction never operates on the recorder's
raw in-flight buffer. Every higher structure is composed of WHOLE (post-optimizer)
recordings, and boundaries always fall between recordings, never mid-recording.

Atom boundaries in the final set come from two sources, but the distinction is
only PROVENANCE; by the time the abstraction reads the recordings, both have
already been applied.

- Recorder-side commits at discrete gameplay events: launch, dock (`OnPartCouple`),
  undock (`OnVesselsUndocking`), EVA / board (ChainToVessel), scene exit /
  recovery (`FinalizeTreeOnSceneChange`), and controlled decouple (the fork point,
  which produces the child recording).
- Optimizer-side splits at environment / body transitions (atmosphere / altitude
  / SOI). In always-tree mode the recorder SUPPRESSES the in-flight split
  (`ShouldSuppressBoundarySplit`); the split into separate recordings is applied
  afterwards by `RecordingOptimizer.IsSplittableEnvOrBodyBoundary` (on commit,
  merge, and load), over a FILTERED subset of transitions (surface grazes, brief
  bracketed runs under 120s, cohesive cross-body coasts, boundary seams are
  suppressed).

Boundaries do NOT exist at a general vessel switch (the prior vessel drops to
background; the spine continues on a new recording via layer 3) or at staging.

### 2. Mission tree (a branching structure)

The recorded tree for one played mission, scoped by `TreeId`. It is genuinely a
TREE, not a linear spine: a trunk that FORKS at controlled separations, with
debris hanging off as parallel twigs. There are two kinds of edges, and
`IsDebris` is the discriminator:

- FORKS = controlled decouples (`IsDebris=false` children, found via
  `ParentAnchorRecordingId`). Each downstream is an alternative through-line.
  These are real flights you flew (a probe, lander, capsule) and are
  spine-eligible: a path can follow one.
- TWIGS = debris (`IsDebris=true`). Spent boosters, jettisoned tanks. They
  separate and tumble away while their parent flies on. They are NEVER
  spine-eligible; they only ever ride along in parallel (like a rewind), rendered
  alongside whichever leg they attached to. Twigs are not shown as rows in the
  Missions UI.

Example. Mothership stack (controller M) lifts off carrying a drop pod
(controller D). At separation the tree forks: one fork continues as M, the other
as D. Each fork is then a linear sequence of legs (D: separation -> transit ->
land at base; M: separation -> deorbit -> recover). Debris shed by either rides
along its parent.

### 3. Main line (a spine, per path)

A spine is a PATH through the branching tree from a Start node to an End node,
choosing which fork to follow at each separation. It is not one fixed thing per
tree; different selections trace different paths.

- Scope: one `TreeId`.
- Spine-eligible legs: `IsDebris == false` (forks included; the old
  `ParentAnchorRecordingId == null` / `ChainBranch == 0` exclusions only described
  topology and are dropped from eligibility).
- Sequence order: by `StartUT` along a controlled line; FORK edges come from
  `ParentAnchorRecordingId` (child points at the leg it separated from).
- Order is NOT `ChainId`-based: `ChainId` resets at vessel switches
  (`ChainSegmentManager.CommitVesselSwitchTermination`) and at scene exit/resume
  (only `ActiveTreeId` is restored), so it cannot thread a multi-session,
  multi-vessel through-line. `TreeId` is the stable scope; `StartUT` plus the fork
  topology gives the order.
- Contiguity is causal/chronological with UT gaps allowed (a switch-and-coast
  with no input leaves a real gap until first modification produces the next
  recording). A gap is not a break.

How the spine crosses controller vessels without a switch boundary: the new
active vessel produces its own spine-eligible recording (same `TreeId`) via the
post-switch first-modification auto-record
(`PrepareActiveTreeForFreshPostSwitchRecording`) or an explicit Fly/Switch-To
continuation (`SwitchSegmentBuilder.CreateSwitchContinuationSegment`).

### 4. Mission subtree (a selection)

A selection of legs that defines what is in the mission. Along each path it is a
CONTIGUOUS interval (a Start node to an End node), and at each fork it chooses
which branch(es) to include.

- Per-leg include state. Including a leg pulls in its debris twigs automatically
  (twigs never get their own toggle).
- Trim rule (both edges): the START edge is the first included leg (everything
  before it is greyed, pre-start). The END edge is set by exclusion: excluding a
  leg drops that leg AND everything downstream of it (its sequence-successors and,
  if it is a fork leg, that whole branch). So unchecking a fork leg drops the
  entire branch from the separation onward.
- Forks may select MULTIPLE branches: including only one fork = a single-path
  mission; including both = the whole-mission subtree (both post-fork branches
  loop together).
- tree : selection is 1 : many, and selections may OVERLAP (share the shared
  trunk, or any legs). Overlap is fine: the engine already renders the same
  recording concurrently on multiple clocks (`MaxOverlapGhostsPerRecording`).

### 5. Mission (a saved, configurable entity)

A Mission is a persisted, named object that wraps a selection:

- a reference to a mission tree (`TreeId`),
- a selection (the included-leg set of layer 4),
- loop on/off and a loop period,
- a name.

Multiple Missions may target the same tree with different selections. CLONE
duplicates the Mission DEFINITION (leg references + included state + settings),
never the recording data, so variants are cheap. A Mission is the persistence
answer for the abstraction: it is what gets saved, listed, cloned, and looped.

### 6. Supply run / supply route (deferred)

Supply run: an ordered set of mission segments, repeating periodically; the
loopable logistics unit. Supply route / list of routes: the top layer. Defined
later by logistics. A looped Mission (layer 5) is their v1 precursor: looping one
Mission is one repeating run; several looped Missions at once is the seed of
multiple routes.

---

## The Missions UI

A first-draft window, opened from a "Missions" button in the main Parsek UI under
Recordings. It reuses `RecordingsTableUI`'s rendering primitives (caret
expand/collapse, indentation, tree connectors, row layout); it does NOT reuse the
recordings hierarchy (debris/crew/chain blocks), because it renders a different
tree: the fork-tree of controlled legs.

Layout: a vertical indented outline. The one rule that keeps arbitrary missions
representable without 2D layout pain:

- Indentation increases ONLY at a fork (a controlled separation).
- A linear sequence of legs does NOT indent; it stacks as rows at the same depth,
  joined by a vertical connector.
- So depth = number of separations, not number of events.

Sketch (the drop-pod example):

```
Mission row  [Clone] [loop x] [period: 30s]   <- Mission-level controls
[x] Launch (M+D stack)
 |- [ ] A - lower booster + probe        (unchecked: its branch greys out)
 \- [x] B - upper stage + capsule
       [x] dock to station S
       [x] re-entry + landing
```

- Each leg row = one recording, labeled by what it accomplished (its end event),
  with an include checkbox. Debris is not shown (it rides along its parent leg).
- Checkbox behavior is the layer-4 trim rule: excluding a leg drops it and
  everything downstream; greying enforces the contiguous interval from both ends.
- Columns on the Mission (root) row: a Clone button, a loop checkbox, and a loop
  period, mirroring the Recordings window's loop/period controls but applied to
  the whole Mission.

First real usage (the viability test): set loop + period on a Mission and have
the whole selected subtree loop as one unit (one shared span clock over the
included legs, debris riding along, members excluded from per-recording loop
scheduling), instead of each recording looping independently. This is the
concrete proof the abstraction is viable and the basis for later logistics.

---

## Key decisions

- Atom = the post-optimizer recording. Recorder-vs-optimizer boundary creation is
  provenance only.
- Boundaries are on nodes only; a selection is always a whole number of legs.
- `IsDebris` is the spine-eligibility discriminator: forks (non-debris) are
  spine-eligible alternative paths; debris twigs are parallel-only.
- Spine order is `TreeId` scope + `StartUT` + fork topology, not `ChainId`.
- A selection is a contiguous interval per path; uncheck = drop it and everything
  downstream.
- Selections may overlap freely.
- A Mission is a saved, named selection + loop settings; Clone copies the
  definition, not the recording data.
- Loop is a Mission-level property (the whole mission loops as a unit), reusing
  the span-clock playback mechanism.

---

## What exists today vs what is new

- Layers 1 to 2 (recording, mission tree) exist in the data; the fork topology is
  already carried by `ParentAnchorRecordingId` + `IsDebris`.
- Layer 3 (spine as a path) is reconstructable from existing fields but is not a
  first-class object yet.
- Layers 4 and 5 (selection, Mission entity) are new: a new persisted Mission
  object plus the Missions window.
- Mission-level looping reuses the design-chain-auto-loop span clock.
- Layer 6 (supply run/route) is deferred to logistics.

---

## Open questions

1. Mid-selection unchecking. We chose uncheck = drop downstream, and start-trim is
   unchecking from the top down. Confirm there is no need for a separate "hole in
   the middle" state (there should not be: the spine must stay contiguous).
2. Default Mission creation. Does each recorded tree auto-spawn a default Mission
   (everything included), which the player then Clones and reconfigures, or are
   Missions created explicitly? Leaning auto-default.
3. Selection vs a growing tree. When a tree gains new legs (the mission continues
   after a Mission was defined), how does an existing selection treat the new
   legs: default-excluded, or auto-extend the End? Leaning default-excluded.
4. Span-clock reuse specifics. What exactly to lift from the design-chain-auto-loop
   branch for Mission-level looping (the shared span clock, member render-in-window,
   debris ride-along) vs rebuild. This is the concrete work the viability build
   forces.

---

## References

- `design-chain-auto-loop` branch: span-clock playback mechanism (one shared clock
  over a multi-recording span, members render when the clock is in their own
  window, debris ride along). Reusable for Mission-level looping. Its
  unit-detection-from-loop-flags model is superseded by the defined Mission
  selection here.
- Fork topology + debris discriminator: `Recording.ParentAnchorRecordingId`,
  `Recording.IsDebris`.
- Recorder boundary creation: `ParsekFlight.OnPartCouple` / `OnVesselsUndocking` /
  `FinalizeTreeOnSceneChange`, `ChainSegmentManager` commit sites.
- Boundary-split suppression in tree mode: `ParsekFlight.ShouldSuppressBoundarySplit`
  and the `HandleAtmosphereBoundarySplit` / `HandleSoiChangeSplit` /
  `HandleAltitudeBoundarySplit` early-returns.
- Environment / body recording splits (optimizer):
  `RecordingOptimizer.IsSplittableEnvOrBodyBoundary`, run by
  `RecordingStore.RunOptimizationPass`.
- Chain identity reset: `ChainSegmentManager.CommitVesselSwitchTermination` and
  scene-resume restoring only `ActiveTreeId`.
- Cross-vessel spine continuation: `PrepareActiveTreeForFreshPostSwitchRecording`,
  `SwitchSegmentBuilder.CreateSwitchContinuationSegment`.
- UI primitives to reuse: `RecordingsTableUI` (caret, connectors, row layout).
- Concurrent same-recording render: `MaxOverlapGhostsPerRecording` in
  `GhostPlaybackEngine`.
