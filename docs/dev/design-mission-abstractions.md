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
- A selection is PERSISTED as the set of EXCLUDED through-line head ids; the included
  set is DERIVED live from current topology, not stored as a frozen list of selected
  leg ids. So when the optimizer re-splits or re-merges legs inside an INCLUDED
  through-line on a later pass, the selection survives: new sub-legs inside the
  interval auto-join the through-line and stay included. The excluded boundary itself
  is keyed by the through-line head's RecordingId, which is stable because the
  optimizer preserves the earliest segment's id on both split and merge (see open
  question 3 for the verification, the v1 decision, and the residual re-fly case).

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

When a Mission has loop on, ALL its included legs replay together over its span
[earliest included start, latest included end], relaunching every loop period. The
behavior depends on how the loop PERIOD compares to the span length:

- Period >= span (or `Auto` resolves to >= span): ONE shared clock (a "span clock")
  sweeps the whole span and wraps as a unit. Each leg renders only while the shared
  clock is inside its own time window, so a multi-leg, multi-branch mission plays back
  exactly like the original flight, then wraps as a whole.
- Period < span: the whole mission OVERLAPS itself. It relaunches every `period`
  seconds, so multiple staggered instances of the mission play concurrently, exactly
  like a single recording with `period < duration` spawns overlapping ghost instances.

Either way this replaces independent per-recording looping for the mission's members,
and debris rides along its parent leg (on the same overlapping cadence when the mission
overlaps).

Multiple missions loop concurrently, at most one per recording tree (see open question
7): each looping mission builds its own `LoopUnit` and the engine dispatches per
committed index, so units on different trees (disjoint indices) never conflict.

The elegant reduction (the mechanism). A mission instance launched at
`anchorUT + k*cadence` places member m at phase
`currentUT - (anchorUT + k*cadence + memberOffset)` within m's own recording, where
`memberOffset = memberStartUT - spanStartUT`. So for member m the active staggered
instances are EXACTLY a per-recording overlap loop with `scheduleStartUT =
PhaseAnchorUT + (memberStartUT - SpanStartUT)`, `intervalSeconds =
OverlapCadenceSeconds`, `duration = memberEndUT - memberStartUT`, `playbackStartUT =
memberStartUT`. This maps directly onto the EXISTING per-recording overlap machinery
(`GhostPlaybackEngine.UpdateOverlapPlayback` / `overlapGhosts`), so a member renders its
staggered instances through the same code a single overlapping recording uses. The
flight engine's `UpdateUnitMemberPlayback` routes a member through that path when the
unit overlaps, and keeps the single span-clock instance otherwise. Debris uses the same
reduction with its own [debrisStart, debrisEnd] window.

Two cadences on the unit (`GhostPlaybackLogic.LoopUnit`):

- `CadenceSeconds` (span-clock cadence): the loop period RAISED to at least the span so
  a SINGLE span instance never truncates (Auto = span). Consumed by the single-instance
  scenes - the Space Center and the Tracking Station have no overlap machinery and always
  render one span instance - and by the flight engine's no-overlap branch.
- `OverlapCadenceSeconds` (true launch cadence): the loop period NOT raised to the span
  (Auto = the GLOBAL auto-loop interval `ParsekSettings.autoLoopIntervalSeconds`, same as
  single recordings), cap-clamped so `ceil(span / cadence)` stays within
  `GhostPlayback.MaxOverlapMissionInstances` (mirrors the per-recording
  `MaxOverlapGhostsPerRecording` / `ComputeEffectiveLaunchCadence` semantics, but at
  mission granularity over the span; set lower because each mission instance multiplies
  across all members). The flight engine self-overlaps when this is shorter than the span.

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
-> LoopUnitSet`. Multiple Missions may loop, at most one per tree (see open question 7),
so the builder emits an empty set or one unit per looping Mission. For each looping
Mission:

- Members = the included legs' committed indices (skip ids not currently in
  `CommittedRecordings`).
- Span = [min StartUT, max EndUT] over those member recordings.
- Span-clock cadence (`CadenceSeconds`) = the Mission's loop period RAISED to at least
  the span, clamped to `LoopTiming.MinCycleDuration`; the `Auto` unit means cadence =
  span length (a single span instance plays the whole mission with no gap).
- Overlap cadence (`OverlapCadenceSeconds`) = the TRUE launch period: the Mission's loop
  period NOT raised to the span (`Auto` = the global auto-loop interval passed in by the
  scene driver, NOT the span), floored at `LoopTiming.MinCycleDuration`, then cap-clamped
  via `ComputeEffectiveLaunchCadence(rawPeriod, span, MaxOverlapMissionInstances)`. When
  it is shorter than the span the flight engine overlaps the mission with itself.
- The driver passes `ParsekSettings.Current.autoLoopIntervalSeconds` (with a safe default
  when null) into both `Build` and `BuildSignature`, and the effective overlap cadence is
  folded into the signature so a cadence change rebuilds.
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
clamp-to-`MinCycleDuration` rule. The loop toggle allows concurrent loops across trees
but one per tree: turning loop on for a Mission turns it off only on other Missions that
share its tree, per the one-loop-per-tree decision in open question 7.

### KSC and Tracking Station parity (single span instance)

The `LoopUnitSet` is computed once per frame from Missions + committed recordings and
fed to every scene (the index space is shared), rather than each scene re-deriving
units. The Space Center and Tracking Station have no overlap-ghost machinery, so they
render a SINGLE span instance via the span clock (`CadenceSeconds`, raised to the span)
even when the flight scene self-overlaps the mission. This is why `CadenceSeconds` stays
raised to at least the span: lowering it below the span would truncate the single
instance these scenes show. Self-overlap (multiple staggered instances) is a
flight-scene-only visual; the SC / TS keep showing the whole mission once through.

- KSC: near-duplicate of the flight span-clock path already exists on the old branch;
  lift it and point its unit source at the adapter. Its ghost map / orbit-line presence
  rides the same per-recording lifecycle, so no separate icon path is needed.
- Tracking Station: the LARGEST gap and its own sub-phase. TS does not use
  `GhostPlaybackEngine`; it renders ProtoVessel-based map presence via
  `GhostMapPresence` (keyed by the same committed index) and has no per-frame
  loop-phase machinery today. To match flight, TS must position a unit member's
  presence at the span-clock `loopUT`. First step in that sub-phase: confirm whether
  TS animates per-recording loops at all today, then drive the span-clock UT into
  `GhostMapPresence` positioning.

### Overlap of looping Missions (self-overlap + concurrent missions)

Two distinct kinds of "overlap", both DONE:

- SINGLE-mission self-overlap: one looping mission whose period is shorter than
  its span relaunches itself, so several staggered instances of THAT mission play at
  once. Implemented via the per-member overlap reduction onto `UpdateOverlapPlayback`
  (see "Mission-level looping" above). The `OverlapCadenceSeconds` carries the true
  launch cadence; `MaxOverlapMissionInstances` caps the instance count.
- MULTIPLE concurrent looping missions: several different missions loop at the same time.
  `LoopUnitSet.OwnerByIndex` maps each recording index to ONE owner, so two simultaneously
  looping Missions that shared a recording would be a single-owner conflict. DECISION:
  concurrent looping is allowed, at most one Mission per tree. Missions on different trees
  have DISJOINT committed indices, so their units never collide; two Missions on the same
  tree are variant selections that share trunk legs (before any fork) and would collide,
  so the store forbids them (`SetLoopEnabled` clears only same-tree siblings). The adapter
  builds one `LoopUnit` per looping Mission and the engine dispatches per committed index,
  with a defensive first-claimant collision guard. True concurrent rendering of ONE
  recording on several Mission clocks (one ghost per Mission of the same recording) is
  still deferred to logistics; one-loop-per-tree sidesteps it.

### Build phasing (reviews at the milestones, not every commit)

- A. Lift the span-clock core + types + pure tests verbatim (no callers yet).
- B. Mission persistence (loop fields) + `Clone` + the per-Mission-row UI toggle/period
  (single-selection). Also the load/use-time guard from open question 3: drop excluded
  head ids that no longer resolve to a current through-line head and warn-log them.
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

## Docking & undocking (v1)

How dock / undock events affect the Mission structure, and what a player can
loop. Resolves the docking half of open question 6.

### The decision

Dock / undock are recorded **entirely as tree topology** (the merge / fork
branch points that already exist), and the loopable "segments" a player wants
are expressed as **Mission selections over that topology**. We do NOT give the
Mission *entity* a dock/undock lifecycle: nothing auto-creates an "AB" Mission
on dock or auto-closes it on undock. A Mission stays a player-configured,
named, persisted selection (layer 5); the physical merge/fork lives only in
`RecordingTree.BranchPoints`. This keeps one source of truth for continuity
(the DAG) and keeps the Missions list free of machine-generated rows.

Rationale for *not* lifting a pause/resume/close lifecycle onto the Mission
entity: the recorder ALREADY does the equivalent "pause B, record the combined
stack, resume B" dance physically (background-record the co-vessel, merge at
`OnPartCouple`, fork at `OnVesselsUndocking`). Encoding it a second time as
Mission entities would duplicate continuity the branch-point DAG owns and
pollute the saved-Mission list.

"Main controller A or B" is not a choice we impose: a docked stack has exactly
one KSP active controller, and `CreateMergeBranch` (`ParsekFlight.cs`) records
the combined leg from that controller into its tree. The co-parent is the
other incoming line.

### What is loopable today (verified against the code)

The loop adapter (`MissionLoopUnitBuilder`) drives looping from the
**interval-level** selection (`MissionIntervalSelection.ComputeRenderWindows`
over `Mission.ExcludedIntervalKeys`), and composition intervals
(`MissionCompositionBuilder`) split at **structural peels** — a controller
separating (decouple / undock / EVA). So, given two vessels A and B that dock
into stack "AB" and later undock:

- A solo (pre-dock), B solo (pre-dock): each is its own through-line / interval
  -> individually loopable. OK
- A after undock, B after undock: undock is a structural peel, so the surviving
  line splits into a pre-undock interval and a post-undock interval, and the
  departing vessel becomes its own offshoot through-line -> both post-undock
  segments individually loopable. OK
- Peeled offshoots in general (probe / lander / EVA kerbal / the undocked
  vessel): own through-line -> individually loopable. OK
- A vessel's whole journey as one unit (the headline case): the through-line
  spans its env-splits, forks it continues through, and the docked stretch.
  Loopable as one selection. OK

### Known gaps (deferred to after supply-routes v0 integration; do NOT fix yet)

1. **Dock is not an interval boundary.** `MissionCompositionBuilder.BuildNode`
   creates interval edges only at structural *peel* UTs (children leaving),
   never at a Dock / Board *merge* UT (a vessel joining). So the docked "AB"
   stretch is lumped into the continuing vessel's pre-dock interval and CANNOT
   be isolated for looping on its own. Fix sketch: emit an interval edge on the
   continuing line at the merge UT when its controller count increases.
2. **Docked composition is understated.** Interval composition is computed as
   the head leg's start composition MINUS the structural peels removed so far
   (`BuildNode` step 4); it never ADDS controllers gained at a dock. A post-dock
   interval label therefore undercounts parts, and a later undock peel subtracts
   the departing vessel's parts that were never in the head count (clamped at 0).
   Tied to gap 1.
3. **Undock continuation vs offshoot is non-deterministic.**
   `BuildSplitBranchData` gives an Undock's backgrounded child `IsDebris=false`
   and NO `ParentAnchorRecordingId` (only the EVA path sets a parent link), and
   both undock children share the branch UT. So `ContinuationSuccessor` picks
   which post-undock vessel is the "main line" by GUID `RecordingId` tiebreaker
   rather than by which vessel held control. Both vessels stay individually
   loopable (one as the through-line continuation, one as an offshoot), so this
   is a correctness/labeling gap, not a loopability blocker. Fix sketch: mark
   the non-active undock child as the offshoot deterministically (e.g. stamp its
   `ParentAnchorRecordingId` = the active child, or carry an explicit
   is-continuation flag the read model honors).
4. **Cross-tree dock (foreign vessel) — deferred by design.** When A and B are
   independent trees, the combined leg and the post-undock continuation land in
   the *controller's* tree while the foreign partner's pre-dock flight stays in
   its own tree, so "loop the whole shared docked journey from the foreign side"
   spans two trees and is not a single contiguous selection. This is the
   remaining half of open question 6; revisit after supply-routes v0 (it likely
   wants the cross-tree dock link followed via the same PID-linking playback
   already does in `GhostChainWalker`).

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
3. Selection vs a changing tree. Persistence stores the set of EXCLUDED through-line
   head ids; the included set is DERIVED live from current topology. That split the
   problem into a robust part and a residual.
   (b) Re-split / re-merge of already-referenced legs: VERIFIED SAFE for the common
   optimizer ops. The optimizer preserves the EARLIEST segment's id:
   `RecordingOptimizer.SplitAtSection` mutates the original (first / earlier) half and
   keeps its id, the second half is a new Recording; `MergeInto` keeps the earlier
   `target`'s id and absorbs the later segment. A through-line head is by definition
   the earliest leg of its run, so it always plays the original / target role and its
   id is preserved. An excluded head id therefore keeps matching after re-split /
   re-merge and the drop-downstream cascade still applies, and new sub-legs inside an
   included through-line auto-join. So the old "resolve before persistence is built"
   concern is largely retired.
   (a) New / post-hoc topology: a genuinely NEW branch (most realistically a re-fly
   supersede split, which adds a new fork; the pre-rewind HEAD keeps its id by the same
   split convention, so existing exclusions still match) is not referenced by a Mission
   defined earlier, so under the excluded-id model it defaults to INCLUDED. This is the
   opposite of the earlier "leaning default-excluded" note; for v1 default-include is
   acceptable. Making new branches default-excluded would require recording the set of
   known head ids at definition time, a bigger change deferred to logistics.
   v1 DECISION: keep the excluded-head-id model. Reasons: (1) head ids are stable as
   verified above; (2) pre-1.0, no save-migration burden, so the persistence shape can
   change later for free; (3) the cleanest harden (key exclusions by the fork's
   `BranchPoint.Id`) has its own hole, since disconnected roots have no branch point.
   Phase-B safety to build in: when a Mission is loaded / used, drop any excluded head
   id that no longer resolves to a current through-line head and WARN-log it, so future
   id churn fails loudly instead of silently re-including a dropped branch. Revisit
   BranchPoint-keyed persistence when logistics starts persisting routes on top of
   Missions.
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
   does not pull in the co-parent. PARTIALLY RESOLVED: the docking/undocking effect on
   Mission structure and looping is settled in "Docking & undocking (v1)" above (no
   Mission-entity lifecycle; loopable segments are interval selections; four gaps
   listed and deferred to after supply-routes v0). Still open: how the multi-path
   (whole-mission) outline renders a reconvergence, and the cross-tree foreign dock
   target (gap 4 there).
7. Overlapping looping Missions. RESOLVED (two parts):
   (a) SINGLE-mission self-overlap (DONE): a looping mission whose period is shorter than
   its span now overlaps ITSELF (relaunches every period, several staggered instances
   play concurrently), exactly like a single recording with period < duration. Cadence =
   the true period (Auto = the global auto-loop interval, NOT the span); a per-mission cap
   (`MaxOverlapMissionInstances`) raises the effective cadence to keep the instance count
   bounded, mirroring `MaxOverlapGhostsPerRecording`. Implemented by reducing each member
   to a per-recording overlap loop over the existing `UpdateOverlapPlayback` machinery.
   The Space Center and Tracking Station still render a single span instance (no overlap
   machinery there); self-overlap is a flight-scene visual.
   (b) CONCURRENT multi-Mission looping (DONE): multiple Missions loop at once, at most one
   per tree. Enabling loop on a Mission disables it only on other same-tree Missions;
   different-tree Missions loop concurrently. Missions on different trees have disjoint
   committed indices so their units never collide; the adapter builds one `LoopUnit` per
   looping Mission with a defensive first-claimant collision guard. True multi-Mission
   looping of the SAME recording (one ghost per Mission on the shared index) is still
   deferred to logistics; one-loop-per-tree sidesteps the single-owner conflict.
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
