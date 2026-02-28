# Parsek Roadmap

## Philosophy

Parsek is a **git-like recording system** for KSP missions. You record flights sequentially, commit them to a timeline, and they play back automatically as ghost vessels while you fly new missions. There is one timeline, recordings are immutable commits, and the game always moves forward.

Like git, you can go back to any earlier point and start new work. Existing recordings don't change — they play out as ghosts alongside your new missions. There is no branching, no state reversal, and no paradoxes. Conflicts are prevented through resource budgeting: committed recordings have already claimed their costs, so the player can only spend what's actually available.

---

## Completed

### Phase 1: Core Recording & Playback

- Position recording with geographic coordinates and adaptive sampling
- Kinematic ghost playback with opaque vessel replicas
- Context-aware merge dialog (Merge to Timeline / Discard)
- Vessel persistence with deferred spawn, crew reservation, and resource deltas
- Orbital/time-warp recording with analytical Keplerian orbits
- Auto-recording on launch and EVA
- Chained recordings (vessel → EVA → vessel, docking/undocking)
- External recording files (v4 format)

### Phase 2: Ghost Visual Fidelity

28 part event types recorded and replayed on ghost vessels: decoupling, staging, parachutes, engines (with FX), solar panels, antennas, radiators, landing gear, lights, cargo bays, fairings (runtime mesh), RCS (with FX), docking/undocking, inventory parts.

Recordings Manager UI with sortable columns, per-recording loop/delete, and status indicators.

---

## Phase 3: Polish & Usability (Next)

Make the existing loop frictionless and configurable for different play styles.

### ~~Settings panel~~ (Done)
`GameParameters.CustomParameterNode` with per-save persistence. Six settings: auto-record on launch, auto-record on EVA, auto-stop time warp, max sample interval, direction threshold, speed threshold. Accessible from both the Parsek UI window (Settings button) and KSP's Difficulty Settings screen (Esc > Settings > Parsek).

### ~~Recording stats~~ (Done)
Computed from existing trajectory data: max altitude, max speed, max G-force, distance travelled, final destination body, duration, point count. Displayed in Recordings Manager as expandable detail per recording.

### ~~Non-revert recording commitment~~ (Done)
"Commit Flight" UI button snapshots the current vessel state and adds the recording to the timeline without reverting. Unlocks the mod for players who don't revert.

### Two-phase parachute deploy
Visual distinction between SEMIDEPLOYED (streamer) and DEPLOYED (full canopy) on ghost vessels. Currently all chute states show the same mesh.

---

## Phase 4: Scale & Integration

Make the mod work well with many recordings and integrate with the broader KSP ecosystem.

### Ghost performance at scale
Profile and optimize for 20+ recordings:
- LOD culling (don't render ghost meshes far from camera)
- Ghost mesh unloading outside active time range
- Particle system pooling for engine/RCS FX
- Benchmark memory and frame time with synthetic recording stress tests

### Recording export/import
Share recordings as standalone files:
- Export: bundle `.prec` + `.craft` + metadata into a single `.parsek` archive
- Import: validate, assign new IDs, inject into current save
- Handle missing parts gracefully (warn, skip, or substitute)
- Community sharing potential (forum/SpaceDock)

### Recording file size optimization
Reduce sidecar file sizes for long recordings:
- Shorter key names in trajectory serialization (version bump)
- Compact numeric encoding (fixed decimal places where full precision isn't needed)
- Optional gzip compression for `.prec` files
- Part event name table (index-based deduplication)

---

## Phase 5: Going Back in Time (Foundation Complete)

Foundation for going back in time — milestones, resource budgeting, epoch isolation, and action blocking are implemented. Restore point UI is future work.

### Milestones (done)
Game state events (tech research, part purchases, facility upgrades, contracts, crew changes) are captured into milestones — immutable timeline commits independent of recordings. Created at recording commit time and on save (FlushPendingEvents captures events that happen without a flight). Deleting a recording does not delete its milestone.

### Resource budget (done)
Computed on-the-fly from recordings + milestones, partial-replay aware. Displayed in the Parsek UI when any resources are reserved. Red text + yellow "Over-committed!" warning when available resources go negative.

### Epoch isolation (done)
Reverts increment CurrentEpoch. New events are stamped with the current epoch. Old-branch events are excluded from new milestones, preventing abandoned timeline branches from leaking.

### Resource deduction on revert (done)
On revert, committed funds/science/reputation are deducted from game state so KSP's top bar and purchase checks reflect available resources.

### Action blocking (done)
Harmony patches on `RDTech.UnlockTech` and `UpgradeableFacility.SetLevel` prevent re-researching committed tech or re-upgrading committed facilities. Explanatory popup dialog shows what's blocked and why.

### Remaining
- [ ] Auto-save restore points at recording commit points
- [ ] "Go Back" UI — pick a restore point, load it, preserve recording state

See `docs/design-going-back-in-time.md` for full design rationale.

---

## Phase 6: Recording Tree (Multi-Vessel Recording) — In Progress

Record entire multi-vessel missions as a single unit. When the player undocks, goes EVA, or docks, Parsek tracks all resulting vessels simultaneously. On revert, all vessels spawn at their correct positions.

**Branch:** `recording-tree`
**Design:** `docs/design-mission-tree.md`

The recording tree builds on top of the existing chain system. Each node in the tree is a vessel's recording, and each recording can itself be a chain of segments (atmospheric/SOI phase splits, dock sequences). The tree adds a new layer for tracking vessel splits and merges — it does not replace chains.

### Task 1: RecordingTree data model + serialization (done)
New data structures: `RecordingTree`, `BranchPoint`, `SurfacePosition`, `TerminalState`. ConfigNode round-trip serialization with 22 unit tests. No runtime behavior changes.

### Task 2: Vessel switch refactoring
Currently vessel switch stops recording. In tree mode, it transitions the active recording to background instead. New `TransitionToBackground` / `PromoteFromBackground` decisions.

### Task 3: Background recording infrastructure
Capture on-rails state for non-active vessels: Keplerian orbit for orbiting vessels, surface position for landed/splashed. Background map (persistentId → recordingId) management.

### Task 4: Split event detection (undock, EVA, joint break)
Subscribe to `onPartUndock`, `onPartJointBreak`, EVA events. Create branch points + child recordings. Debris filter — only branch for vessels with command capability.

### Task 5: Merge event detection (dock, board)
Subscribe to `onPartCouple`. Handle the docking race condition (`dockingInProgress` set). Create merge branch points.

### Task 6: Terminal event detection (destruction, recovery)
`onVesselDestroy` with one-frame deferred check (unload vs destruction). Mark recordings as Destroyed/Recovered terminal state.

### Task 7: Tree commit + multi-vessel leaf spawning
Commit entire tree. Identify leaves. Spawn all leaf vessels at correct positions/orbits. Crew reservation for N vessels. Scene exit auto-commit.

### Task 8: Tree merge dialog
New `ShowTreeDialog` alongside existing standalone/chain dialogs. Shows all vessels with their situations.

### Task 9: Tree ghost playback
All recordings in a committed tree play as simultaneous ghosts. Background ghosts at orbit positions. Ghost transitions at branch points.

### Task 10: Tree-level resource tracking
Aggregate resource deltas at tree level. Per-recording resource fields stay for chain segment granularity.

### Task 11: Backward compatibility
Wrap existing recordings in single-node trees. Chain fields preserved — chains remain fully functional.

### Dependency flow
```
Task 1 ──→ Task 2 ──→ Task 3 ──→ Task 4+5+6 ──→ Task 7 ──→ Task 8+9+10 ──→ Task 11
```

---

## Future

### Take Control stabilization
Making "jump into a ghost and fly it" reliable is desirable but creates paradox problems: what happens to the recording's future events, reserved crew, and applied resource deltas? Each answer requires hard restrictions that may frustrate players. This needs careful design and should not be rushed.

Current status: experimental button exists in UI, not recommended for normal play.

### Additional part event coverage
- Control surface deflection (continuous float — thousands of events per flight, unclear visual value)
- Robotics / Breaking Ground DLC (continuous servo motion, DLC-dependent)
- Two-phase engine startup (spool-up animations on some engines)

---

## Out of Scope

- **Racing modes or lap timing**
- **AI playback or autopilot**
- **Multiplayer synchronization**
- **Timeline branching or alternate histories**
- **Logistics network** — Parsek's recording infrastructure (looped playback, chain segments, vessel snapshots, game state events, resource tracking) forms a natural foundation for automated supply routes between bases. The concept: fly a cargo mission once, Parsek records it, then that recording becomes a reusable logistics route that periodically deducts fuel at the origin and delivers cargo at the destination, with the ghost replaying visually during transit. This will be built as a separate mod on top of Parsek rather than integrated directly. See `docs/research/logistics-network-design.md` for the full design exploration.
