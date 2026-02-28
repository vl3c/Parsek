# Parsek Roadmap

## Philosophy

Parsek is a **git-like recording system** for KSP missions. You record flights sequentially, commit them to a timeline, and they play back automatically as ghost vessels while you fly new missions. There is one timeline, recordings are immutable commits, and the game always moves forward.

Like git, you can go back to any earlier point and start new work. Existing recordings don't change — they play out as ghosts alongside your new missions. There is no branching, no state reversal, and no paradoxes. Conflicts are prevented through resource budgeting: committed recordings have already claimed their costs, so the player can only spend what's actually available.

---

## Completed

### Phase 1: Core Recording & Playback

- Position recording with geographic coordinates and adaptive sampling
- Kinematic ghost playback with opaque vessel replicas
- Context-aware merge dialog (Keep Vessel / Recover / Discard)
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

### KAC integration
Auto-create Kerbal Alarm Clock alarms for ghost playback windows. When a recording is committed, create an alarm at its StartUT so the player gets notified before a ghost appears. Optional — only active if KAC is installed.

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

## Future

### Take Control stabilization
Making "jump into a ghost and fly it" reliable is desirable but creates paradox problems: what happens to the recording's future events, reserved crew, and applied resource deltas? Each answer requires hard restrictions that may frustrate players. This needs careful design and should not be rushed.

Current status: experimental button exists in UI, not recommended for normal play.

### Multiple concurrent recordings
Playback already supports multiple simultaneous ghosts. Recording is currently serial (one vessel at a time). Enabling concurrent recording requires:
- UI for managing multiple active recordings
- Conflict detection (same vessel recorded twice)
- Merge ordering when multiple recordings commit at once

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
