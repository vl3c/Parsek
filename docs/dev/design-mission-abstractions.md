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
- `ParentAnchorRecordingId` - non-null on a SUBSET of children (genuine debris and
  background-split controlled-decoupled children); points at the parent recording.
  It is a parent-anchored PLAYBACK contract (which recording owns playback), NOT
  the tree topology, and it is null on cross-vessel continuations. Use it only as
  part of the debris-vs-controlled discriminator, never to find forks.
- `RecordingTree.BranchPoints` (+ `Recording.ParentBranchPointId` /
  `ChildBranchPointId`) - the actual topology, a DAG (`ParentRecordingIds` /
  `ChildRecordingIds` are lists, so Dock/Board merges have two parents). Each
  `BranchPoint` is a typed parent->child edge (`Undock`, `EVA`, `Dock`, `Board`,
  `JointBreak`, `Launch`, `Breakup`, `VesselSwitchContinuation`; `Terminal` exists
  in the enum but is a state classifier on `Recording.TerminalStateValue`, not a
  constructed edge). THIS is where forks, merges, and continuations live.
- `StartUT` / `EndUT` - the recording's recorded time window. COMPUTED properties
  (from the trajectory points, with serialized `ExplicitStartUT` / `ExplicitEndUT`
  values that only EXTEND the point-derived bounds, never shrink them), not raw
  stored timestamps. Sequence ordering uses the computed value.

Terminology note: a "node" means a boundary point between two consecutive legs,
including a path's Start and End points. It is NOT a recording. A "leg" is one
recording (post-optimizer). "Subtree" / selection means a bounded selected
sub-graph (forks may be followed, merges traversed per-path), not a graph-theory
rooted subtree.

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
  recovery (`FinalizeTreeOnSceneChange`), and controlled decouple (a fork point;
  the child recording is produced by the background recorder,
  `BackgroundRecorder.HandleBackgroundVesselSplit`).
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

The recorded structure for one played mission, scoped by `TreeId`. It is a
DIRECTED GRAPH, not a linear spine and not even a strict tree: lines FORK at
controlled separations and JOIN at docks/boards. The topology is
`RecordingTree.BranchPoints` (typed parent->child edges; both `ParentRecordingIds`
and `ChildRecordingIds` are lists). Edge roles, with `IsDebris` on the CHILD as
the discriminator:

- FORKS (split) = controlled separations (Undock / EVA / JointBreak whose child is
  `IsDebris=false`). Each downstream child is an alternative through-line, a real
  flight you flew (probe, lander, capsule); a path can follow one.
- MERGES (join) = Dock / Board: two SAME-TREE parent lines converge into one child
  (the branch point carries two `ParentRecordingIds`; the co-parent is resolved
  from the tree's own `BackgroundMap`). A dock to a FOREIGN vessel (another
  mission's tree) is instead a SINGLE-parent branch point; that cross-tree
  relationship is reconstructed at playback (PID linking in `GhostChainWalker`),
  not as a two-parent edge. Either way a mission PATH traverses a merge by following
  its OWN incoming line into the child; the co-parent belongs to its own line /
  Mission and is not pulled in.
- TWIGS (parallel) = debris (`IsDebris=true` children: spent boosters, jettisoned
  tanks). NEVER spine-eligible; they only ride along in parallel with whichever
  leg they left, and are not shown as rows in the Missions UI.

Within a single controlled run between branch points, the optimizer may env-split
the recording into several consecutive legs; those are grouped by shared `ChainId`
and ordered by `StartUT` (see layer 3).

Example. Mothership stack (controller M) carries a drop pod (controller D). At
separation the graph forks: one branch continues as M, the other as D (D:
separation -> transit -> land; M: separation -> deorbit -> recover). If D later
docks back to M (same tree) that is a two-parent merge; a dock to a foreign station
(another mission) is instead single-parent. Either way D's path follows into the
docked child and the co-parent is not pulled in. Debris shed by either rides along
its parent.

### 3. Main line (a spine, per path)

A spine is a PATH through the branching graph from a Start node to an End node,
choosing which fork to follow at each separation. It is not one fixed thing per
tree; different selections trace different paths.

- Scope: one `TreeId`.
- Spine-eligible legs: `IsDebris == false` (forks included; the old
  `ParentAnchorRecordingId == null` / `ChainBranch == 0` exclusions only described
  topology and are dropped from eligibility).
- Reconstructing a line: follow `RecordingTree.BranchPoints` edges across forks /
  merges / switches (these survive scene changes and vessel switches via
  `VesselSwitchContinuation` and the like). Within one controlled run between
  branch points, the optimizer's env-split legs are grouped by shared `ChainId`
  and ordered by `StartUT`.
- `ChainId` is NOT the whole-spine thread: it RESETS at vessel switches
  (`ChainSegmentManager.CommitVesselSwitchTermination`) and at scene exit/resume
  (only `ActiveTreeId` is restored), so it cannot identify a multi-session,
  multi-vessel through-line. Its only role here is grouping the env-split legs of a
  single run; `BranchPoints` carry the cross-run topology and `TreeId` is the
  stable scope.
- Some continuations have NO incoming edge: restore-completed post-switch
  recordings can have `ParentBranchPointId == null` (a disconnected root in the
  same tree), joinable only by `TreeId` + `StartUT`. The derivation must handle a
  tree with multiple roots.
- Contiguity is causal/chronological with UT gaps allowed (a switch-and-coast
  with no input leaves a real gap until first modification produces the next
  recording). A gap is not a break.

A single spine is one path. A layer-4 selection may follow several forks at once,
which makes the selection a SUBTREE of paths (each path independently contiguous)
that loop together on one shared clock. So "spine = a path" and "selection = one
or more spine paths" are the two distinct levels; the headline whole-mission case
is the multi-path one.

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
  entire branch from the separation onward. At a merge, dropping this path's parent
  drops the merged child from THIS path only; the co-parent's own Mission is
  unaffected.
- Forks may select MULTIPLE branches: including only one fork = a single-path
  mission; including both = the whole-mission subtree (both post-fork branches
  loop together).
- tree : selection is 1 : many, and selections may OVERLAP (share the trunk or any
  legs). Overlap is fine: the engine already supports many concurrent ghost
  instances of one recording (the loop-overlap path, capped by
  `MaxOverlapGhostsPerRecording`); rendering one recording on several Mission
  clocks is that same capability.
- A selection is defined by its boundary NODES (Start and End per path) plus the
  fork choices, NOT by a frozen list of leg ids. So when the optimizer re-splits
  or re-merges legs inside the interval on a later pass, the selection survives:
  new sub-legs that fall inside [Start, End] stay included. Re-split exactly at a
  trim boundary is the one edge to nail at build time (open question 3).

### 5. Mission (a saved, configurable entity)

A Mission is a persisted, named object that wraps a selection:

- a reference to a mission tree (`TreeId`),
- a selection (layer 4: per-path boundary nodes + fork choices),
- loop on/off and a loop period,
- a name.

Multiple Missions may target the same tree with different selections. CLONE
duplicates the Mission DEFINITION (leg references + included state + settings),
never the recording data, so variants are cheap. A Mission is the persistence
answer for the abstraction: it is what gets saved, listed, cloned, and looped.

Invariant: every recorded tree always has at least one Mission. A default Mission
(everything included) is auto-created when the tree is first recorded, and DELETE
is blocked on the last remaining Mission for a tree, so the tree can never be left
without a Mission. Extra (cloned, reconfigured) Missions are freely deletable.

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
graph: the controlled-leg fork-tree (derived by walking `RecordingTree.BranchPoints`
for fork / merge / continuation edges, grouping each run's env-split legs by
`ChainId` and ordering by `StartUT`, with debris children excluded). At a merge the
rendered path follows its own incoming line into the child; the co-parent (a
docking target with its own Mission) is not expanded.

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

- Each leg row = one recording (post-optimizer), with an include checkbox; debris
  is not shown (it rides along its parent leg). Rows are labeled by their end
  event. Note: optimizer env-splits mean a single controlled activity (e.g.
  transit) can become several stacked rows whose end events are environmental
  (entering atmosphere, SOI change), not gameplay milestones. Whether to visually
  collapse consecutive same-line env-split rows is an open UI detail (open
  question 5).
- Checkbox behavior is the layer-4 trim rule: excluding a leg drops it and
  everything downstream; greying enforces the contiguous interval from both ends.
- Columns on the Mission (root) row: a Clone button, a Delete button, a loop
  checkbox, and a loop period (the loop/period mirror the Recordings window's
  controls but apply to the whole Mission). Delete removes that Mission; it is
  greyed/blocked on the last remaining Mission for a tree (every tree always
  keeps at least one).

First real usage (the viability test): set loop + period on a Mission and have
the whole selected subtree loop as one unit (one shared span clock over the
included legs, debris riding along, members excluded from per-recording loop
scheduling), instead of each recording looping independently. This is the
concrete proof the abstraction is viable and the basis for later logistics.

---

## Mission-level looping (span-clock integration)

This resolves open question 4 with the concrete plan derived from reading both the
`design-chain-auto-loop` branch (the span clock) and this branch's current playback.

### What a looped Mission means

When a Mission has loop on, ALL its included legs replay together on ONE shared
clock (a "span clock") covering [earliest included start, latest included end],
repeating every loop period. Each leg renders only while the shared clock is inside
that leg's own time window, so a multi-leg, multi-branch mission plays back exactly
like the original flight, then wraps as a whole. This replaces independent
per-recording looping for the mission's members. Debris rides along its parent leg.

### What we lift from design-chain-auto-loop, and what we leave

The span-clock mechanism is cleanly separable from that branch's abandoned
auto-detection. We lift the mechanism and replace detection with the Mission
selection.

LIFT (essentially verbatim; the member set is the only thing that changes):

- Span-clock core in `GhostPlaybackLogic.cs`: `LoopUnit` / `LoopUnitSet` types and
  the pure helpers `TryComputeSpanLoopUT`, `IsLoopUTInMemberWindow`,
  `DecideUnitMemberRender` (+ `UnitMemberRenderDecision`), `ShouldSourceDebrisFromUnitSpan`,
  `ShouldRetargetWatchOnUnitHandoff`.
- Engine routing in `GhostPlaybackEngine.cs`: `currentLoopUnits` + `lastUnitSelection`
  state, `SetLoopUnits`, the per-recording interception, `UpdateUnitMemberPlayback`
  (anchor gating, warp suppression, the `currentUT = spanLoopUT` render override,
  keep-watched-owner-alive), `LogUnitTransitionIfChanged`, and the loop-synced debris
  seam.
- Events: `CameraActionType.UnitHandoffRetarget` and the
  `GhostPlaybackSkipReason.ChainLoopUnitInactive` skip reason.
- Watch-transfer (KEPT, per decision): `WatchModeController.HandleLoopCameraAction`
  branch + the `OnLoopCameraAction` subscription. The camera follows the live member
  across member boundaries as the span clock advances.
- KSC consumer in `ParsekKSC.cs`: `UpdateUnitMemberKsc`,
  `DestroyUnitMemberKscGhostIfActive`, `LogUnitTransitionKscIfChanged`, and the
  auto-launch-queue exclusion for unit members.
- The PURE span-clock xUnit tests and the three in-game tests (single-ghost wrap,
  watch-follows-member, debris-follows-span) carry over.

LEAVE BEHIND: `RecordingStore.DetectChainLoopUnits` and its `IsMainLink` /
`IsRunEligibleMainLink` / `IsRideAlongSecondary` / `BuildLoopUnit` predicates and
their tests. The Mission selection IS the member set; no inference is needed.

### The index contract (the seam that ties it together)

`LoopUnit.OwnerIndex` / `MemberIndices` and `LoopUnitSet.OwnerByIndex` are positional
integer indices into `RecordingStore.CommittedRecordings`. The flight engine, the KSC
consumer, and the Tracking Station bookkeeping ALL key per-recording state on that
same `int`, so one `LoopUnitSet` is valid in every scene. The adapter must emit
indices into that exact list, in that order, and must never hand the engine a stale
or out-of-range index (the consumers guard bounds but assume positional alignment).

Two helpers do not exist yet and must be added (both pure and unit-tested):

- A RecordingId -> committed-index map (today `FindCommittedRecordingIndex` is
  private and callers do ad-hoc linear scans).
- A Mission -> included-RecordingId-set extractor. The include cascade (a head is
  included unless it or an upstream head is in `ExcludedThroughLineHeadIds`) currently
  lives inline in `MissionsWindowUI`; extract it so the adapter and the UI share one
  definition.

### The adapter (Mission -> LoopUnitSet)

A new pure builder, `MissionLoopUnitBuilder.Build(missions, committedRecordings)
-> LoopUnitSet`. For each Mission with loop on:

- Members = the included legs' committed indices (skip ids not currently in
  `CommittedRecordings`).
- Span = [min StartUT, max EndUT] over those member recordings.
- Cadence = the Mission's loop period, clamped to `LoopTiming.MinCycleDuration`; the
  `Auto` unit means cadence = span length (loop the whole mission with no gap).
- Owner = the earliest-start member (the mission's root leg), used as the unit's
  representative for the camera and debris-parent lookup.

Debris is NOT added to `MemberIndices`. The engine's `ShouldSourceDebrisFromUnitSpan`
sources a debris recording from its parent leg's unit (it checks `TryGetUnitForMember`
on the debris's resolved parent index), so debris rides along when its parent leg is
an included member, and is simply left out of the loop when the parent leg is not.
This keeps the adapter operating purely on legs. Caveat for the builder: the debris ->
parent index linkage is `RecordingStore.PopulateLoopSyncParentIndices`, which picks
the FIRST non-debris same-tree recording whose [StartUT, EndUT] covers the debris
start. That is independent of `ParentAnchorRecordingId` AND of the Mission selection,
so if a debris's covering leg is excluded while a different overlapping leg is
included, the debris will not ride (it belongs to the leg it physically left). That is
acceptable v1 behavior, but the adapter must not assume "parent leg" means the
anchor parent; it means the UT-covering leg the engine resolves.

### Engine-dispatch changes to the lifted code

On `design-chain-auto-loop` the unit machinery assumes each member carries a
per-recording `LoopPlayback` flag (its group UI set them), so several engine sites are
gated behind `ShouldLoopPlayback(traj)` (which requires `traj.LoopPlayback == true`).
In the Mission model loop is a MISSION property, so member recordings do NOT carry
their own loop flag. That gate therefore has to be bypassed for unit members at every
site that drives looping, otherwise members silently fail to loop. There are three:

1. Member render. The unit-membership interception currently sits INSIDE
   `if (ShouldLoopPlayback(traj))`. Hoist it ABOVE that gate: a recording that is a
   member of an active unit renders via `UpdateUnitMemberPlayback` regardless of its
   own `LoopPlayback`. (`UpdateUnitMemberPlayback` re-checks anchor / window /
   pre-activation / warp / overlap / cycle internally, so the hoist is safe.)
2. Debris ride-along. `TryUpdateLoopSyncedDebris` (which calls
   `ShouldSourceDebrisFromUnitSpan`) is also reached only when
   `ShouldLoopPlayback(parent)` is true. The same bypass must apply: reach the debris
   seam when the parent is a unit member, not only when the parent carries
   `LoopPlayback`. Without this, debris of a looped Mission stops riding.
3. Auto-launch queue. Unit members are excluded from the global auto-loop launch
   schedule (`RebuildAutoLoopLaunchScheduleCache`, and the KSC mirror). Today that
   exclusion is itself behind `ShouldLoopPlayback`; with members no longer carrying
   the flag those gates become no-ops (harmless: a flagless member is not queued).
   But if a member ALSO happens to carry its own `LoopPlayback`, exclude it from the
   queue by UNIT MEMBERSHIP, independent of the flag, so it cannot be double-scheduled.

Net consequence: for a given recording, Mission looping wins over per-recording
looping; the hoisted membership check (with its `continue`) is the single arbiter at
the render site, and the queue exclusion covers the scheduling site.

### Persistence

Add to `Mission` (and to `Mission.Save` / `Load` / `Clone`): `LoopPlayback` (bool),
`LoopIntervalSeconds` (double, serialized "R" / InvariantCulture), and `LoopTimeUnit`.
Unlike the per-recording codec (which drops the unit and resets it to seconds on
reload), serialize the Mission's unit explicitly so the row reads back as the user set
it. `ParsekScenario` OnSave/OnLoad already route through `MissionStore.Save` / `Load`,
so the new fields persist with no extra wiring. `Mission.Clone` must copy all three.

### UI

A per-Mission-row loop checkbox + period cell, mirroring
`RecordingsTableUI.DrawLoopPeriodCell`: a value text field plus a unit button cycling
Sec / Min / Hour / Auto. Reuse the existing `ParsekUI` helpers (`TryParseLoopInput`,
`ConvertToSeconds`, `ConvertFromSeconds`, `FormatLoopValue`, `UnitLabel`) and the same
clamp-to-`MinCycleDuration` rule.

### KSC and Tracking Station parity (render exactly what flight renders)

Per decision, KSC and TS must show exactly what flight renders. The `LoopUnitSet` is
computed once per frame from Missions + committed recordings and fed to every scene
(the index space is shared), rather than each scene re-deriving units.

- KSC: near-duplicate of the flight path already exists on the old branch; lift it and
  point its unit source at the adapter. Its ghost map / orbit-line presence rides the
  same per-recording lifecycle, so no separate icon path is needed.
- Tracking Station: the LARGEST gap and its own sub-phase. TS does not use
  `GhostPlaybackEngine`; it renders ProtoVessel-based map presence via
  `GhostMapPresence` (keyed by the same committed index) and has no per-frame
  loop-phase machinery today. To match flight, TS must position a unit member's
  presence at the span-clock `loopUT`. First step in that sub-phase: confirm whether
  TS animates per-recording loops at all today, then drive the span-clock UT into
  `GhostMapPresence` positioning.

### Overlap of looping Missions (decision needed)

`LoopUnitSet.OwnerByIndex` maps each recording index to ONE owner. The headline case
(loop a single Mission) never hits this. But selections may overlap, so two
SIMULTANEOUSLY looping Missions that share a recording are a single-owner conflict.
v1 options: (a) assign the shared recording to one unit by a defined precedence and
warn-log the rest, or (b) restrict to one actively-looping Mission per tree at a time.
True concurrent rendering of one recording on several Mission clocks (one ghost per
Mission) is the overlap-ghost capability and is deferred to logistics; do not try to
make the single-owner span clock do it in v1.

### Build phasing (reviews at the milestones, not every commit)

- A. Lift the span-clock core + types + pure tests verbatim (no callers yet).
- B. Mission persistence (loop fields) + `Clone` + the per-Mission-row UI toggle/period.
- C. The adapter + the two new helpers (id->index, included-set) + adapter tests.
  REVIEW after C.
- D. Flight wiring: the `SetLoopUnits` call site in `UpdateTimelinePlaybackViaEngine`,
  the three engine-dispatch changes (member-render hoist, debris-seam bypass, queue
  exclusion by unit membership), and watch-transfer. THE VIABILITY TEST happens here
  (loop one Mission, watch it and its debris replay as a unit in flight). REVIEW
  after D.
- E. KSC parity.
- F. Tracking Station parity (largest). REVIEW after F.

---

## Key decisions

- Atom = the post-optimizer recording. Recorder-vs-optimizer boundary creation is
  provenance only.
- Boundaries are on nodes only; a selection is always a whole number of legs.
- Tree topology (forks, merges, continuations) lives in `RecordingTree.BranchPoints`
  (typed parent->child edges), NOT `ParentAnchorRecordingId`. `IsDebris` on a
  branch point's child is the spine-eligibility discriminator: non-debris children
  are spine-eligible forks; debris children are parallel-only twigs.
- Line reconstruction: follow `RecordingTree.BranchPoints` across forks / merges /
  switches, group a run's env-split legs by `ChainId`, order by `StartUT`. `ChainId`
  does not thread the whole spine (it resets); `TreeId` is the stable scope.
- A selection is a contiguous interval per path; uncheck = drop it and everything
  downstream.
- Selections may overlap freely.
- A Mission is a saved, named selection + loop settings; Clone copies the
  definition, not the recording data.
- Loop is a Mission-level property (the whole mission loops as a unit), reusing
  the span-clock playback mechanism.

---

## What exists today vs what is new

- Layers 1 to 2 (recording, mission tree) exist in the data; the topology is
  already carried by `RecordingTree.BranchPoints` (+ `ParentBranchPointId` /
  `ChildBranchPointId`), and `IsDebris` discriminates spine legs from debris twigs.
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
2. Default Mission creation. RESOLVED: each recorded tree auto-spawns a default
   Mission (everything included) and always keeps at least one (Delete is blocked
   on the last remaining Mission for a tree).
3. Selection vs a changing tree. (a) When a tree gains new legs (mission continues
   after a Mission was defined): default-excluded, or auto-extend the End? Leaning
   default-excluded. (b) When the optimizer RE-SPLITS or RE-MERGES already-referenced
   legs on a later pass: sub-legs inside [Start, End] stay included by the
   boundary-node model, but a re-split exactly at a trim boundary needs a defined
   rule. Resolve before persistence is built.
4. Span-clock reuse specifics. RESOLVED: see "Mission-level looping (span-clock
   integration)" above. Lift the span clock + engine routing + watch-transfer + KSC
   consumer; leave behind the auto-detection; replace it with a Mission -> LoopUnitSet
   adapter over committed-recording indices.
5. Env-split leg rows. Should consecutive same-controlled-line legs that exist only
   because of optimizer env-splits be visually collapsed into one row (environmental
   sub-boundaries hidden), or shown individually? UI clarity only, not the data model.
6. Merge / DAG handling. Dock / Board within one tree are two-parent merges; a dock
   to a foreign vessel is single-parent (the cross-tree link is reconstructed at
   playback). v1 rule: a path follows its own incoming line into the merged child and
   does not pull in the co-parent. Open: how the multi-path (whole-mission) outline
   renders a reconvergence, and whether a foreign dock target is ever surfaced.
7. Overlapping looping Missions. `LoopUnitSet` is single-owner-per-index, so two
   Missions looping at once that share a recording conflict. v1: precedence + warn, or
   one active looping Mission per tree. True multi-clock-per-recording (one ghost per
   Mission via the overlap path) is deferred to logistics. Decide the v1 rule before
   wiring the adapter (build phase C).
8. Tracking Station loop parity. TS renders ProtoVessel map presence
   (`GhostMapPresence`), not engine ghosts, and positions them at the LIVE `currentUT`
   against each recording's recorded window with NO loop-phase remap; confirmed it
   does not loop per-recording at all today. So phase F is genuinely new work: feed the
   span-clock `loopUT` (for unit members) into `GhostMapPresence` positioning so a
   looped Mission's map presence tracks the same clock flight uses. This is the largest
   gap (no existing loop machinery to lift on the TS side).

---

## References

- `design-chain-auto-loop` branch: span-clock playback mechanism (one shared clock
  over a multi-recording span, members render when the clock is in their own
  window, debris ride along). Reusable for Mission-level looping. Its
  unit-detection-from-loop-flags model is superseded by the defined Mission
  selection here.
- Tree topology (forks / merges / continuations): `RecordingTree.BranchPoints`,
  `Recording.ParentBranchPointId` / `ChildBranchPointId`, `BranchPointType`.
- Debris-vs-controlled discriminator + parent-anchored playback contract:
  `Recording.IsDebris`, `Recording.ParentAnchorRecordingId`.
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
- Concurrent same-recording render: `MaxOverlapGhostsPerRecording` (defined in
  `ParsekConfig`, consumed by `GhostPlaybackEngine`).
