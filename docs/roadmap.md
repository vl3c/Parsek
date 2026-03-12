# Parsek Roadmap

## Philosophy

Parsek is a **git-like recording system** for KSP missions. You record flights sequentially, commit them to a timeline, and they play back automatically as ghost vessels while you fly new missions. There is one timeline, recordings are immutable commits, and the game always moves forward.

Like git, you can go back to any earlier point and start new work. Existing recordings don't change - they play out as ghosts alongside your new missions. There is no branching, no state reversal, and no paradoxes. Conflicts are prevented through resource budgeting: committed recordings have already claimed their costs, so the player can only spend what's actually available.

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
- External recording files (v5 format, surface-relative rotation)

### Phase 2: Ghost Visual Fidelity

28 part event types recorded and replayed on ghost vessels: decoupling, staging, parachutes, engines (with FX), solar panels, antennas, radiators, landing gear, lights, cargo bays, fairings (runtime mesh), RCS (with FX), docking/undocking, inventory parts.

Re-entry heating FX with mesh-surface fire particles matching stock KSP aeroFX intensity formula.

Recordings Manager UI with sortable columns, per-recording loop/delete, and status indicators.

### Orbital Rotation Fidelity

Ghost vessels in orbital segments now preserve their recorded attitude instead of always facing prograde. Rotation is stored relative to the orbital velocity frame (prograde/radial/normal) at the on-rails boundary and reconstructed at playback from the Keplerian orbit. SAS-locked orientations (retrograde, normal, radial, etc.) hold correctly throughout the orbit.

When the PersistentRotation mod is detected, Parsek also records the vessel's angular velocity. Spinning vessels are spun forward during playback, matching what the player saw with PersistentRotation active. Without the mod, ghosts hold their boundary attitude (correct for stock KSP, which freezes rotation on rails).

Old recordings without attitude data fall back to prograde (unchanged behavior). No format version bump required.

**Design:** `docs/dev/done/design-orbital-rotation.md`

### Camera Follow for Ghost Vessels

The W (Watch) button in the Recordings Manager moves the camera to a ghost vessel during playback. The camera follows the ghost until the player presses Backspace to return. Camera automatically recenters on visible parts after separation events.

**Design:** `docs/dev/done/design-camera-follow-ghost.md`

---

## Phase 3: Polish & Usability - Complete

Make the existing loop frictionless and configurable for different play styles.

### ~~Settings panel~~ (Done)
`GameParameters.CustomParameterNode` with per-save persistence. Six settings: auto-record on launch, auto-record on EVA, auto-stop time warp, max sample interval, direction threshold, speed threshold. Accessible from both the Parsek UI window (Settings button) and KSP's Difficulty Settings screen (Esc > Settings > Parsek).

### ~~Recording stats~~ (Done)
Computed from existing trajectory data: max altitude, max speed, max G-force, distance travelled, final destination body, duration, point count. Displayed in Recordings Manager as expandable detail per recording.

### ~~Non-revert recording commitment~~ (Done)
"Commit Flight" UI button snapshots the current vessel state and adds the recording to the timeline without reverting. Unlocks the mod for players who don't revert.

### ~~Two-phase parachute deploy~~ (Done)
`ParachuteSemiDeployed` and `ParachuteDeployed` as separate event types. Ghost playback shows streamer canopy for semi-deployed and real/fake canopy mesh for full deployed.

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

## Phase 5: Going Back in Time - Complete

Full going-back-in-time system: milestones, resource budgeting, epoch isolation, action blocking, per-recording rewind saves, and Rewind UI.

### Milestones (done)
Game state events (tech research, part purchases, facility upgrades, contracts, crew changes) are captured into milestones - immutable timeline commits independent of recordings. Created at recording commit time and on save (FlushPendingEvents captures events that happen without a flight). Deleting a recording does not delete its milestone.

### Resource budget (done)
Computed on-the-fly from recordings + milestones, partial-replay aware. Displayed in the Parsek UI when any resources are reserved. Red text + yellow "Over-committed!" warning when available resources go negative.

### Epoch isolation (done)
Reverts increment CurrentEpoch. New events are stamped with the current epoch. Old-branch events are excluded from new milestones, preventing abandoned timeline branches from leaking.

### Resource deduction on revert (done)
On revert, committed funds/science/reputation are deducted from game state so KSP's top bar and purchase checks reflect available resources.

### Action blocking (done)
Harmony patches on `RDTech.UnlockTech` and `UpgradeableFacility.SetLevel` prevent re-researching committed tech or re-upgrading committed facilities. Explanatory popup dialog shows what's blocked and why.

### Rewind (done)
Each recording owns a quicksave captured at recording start, stored in `Parsek/Saves/` (invisible to KSP's load menu). Resource snapshot (funds, science, reputation) captured alongside the quicksave for baseline resource reset on rewind. Quicksave deleted when recording is deleted or discarded. Only chain/tree roots get rewind saves (promotions skip capture).

### Rewind UI (done)
"Rewind" button per recording in the Recordings window. Confirmation dialog shows vessel name, launch date, future recording count, and warnings. On confirm: loads the quicksave, strips recorded vessel from flight state, transitions to Space Center. Deferred coroutine sets UT via Planetarium.SetUniversalTime (must happen after scene load - setting before LoadScene gets overwritten by the scene transition). Increments epoch, resets milestone mutable state, resets playback state, resets resources to PreLaunch baseline values, re-reserves crew, replays committed actions (tech, parts, facilities, crew via ActionReplay). All committed recordings replay as ghosts from the rewound point, re-applying resource deltas at the correct UT.

### Action replay (done)
After rewind resource adjustment, `ActionReplay.ReplayCommittedActions` programmatically re-applies committed game actions from milestones: tech unlock (via `UnlockProtoTechNode`, no science deduction), part purchase, facility upgrade, and crew hire. Each handler has idempotent guards (skip if already applied). Suppression flags prevent re-recording during replay.

**Design:** `docs/dev/done/design-restore-points.md`, `docs/dev/done/design-going-back-in-time.md`

**Test coverage:** 1263 tests.

---

## Phase 6: Recording Tree (Multi-Vessel Recording) - Complete

Record entire multi-vessel missions as a single unit. When the player undocks, goes EVA, or docks, Parsek tracks all resulting vessels simultaneously. On revert, all vessels spawn at their correct positions.

**Design:** `docs/dev/done/design-mission-tree.md`

The recording tree builds on top of the existing chain system. Each node in the tree is a vessel's recording, and each recording can itself be a chain of segments (atmospheric/SOI phase splits, dock sequences). The tree adds a new layer for tracking vessel splits and merges - it does not replace chains.

**New components:** `RecordingTree` (rooted DAG), `BranchPoint` (split/merge linkage), `BackgroundRecorder` (on-rails capture), `TerminalState` (8 end conditions), `SurfacePosition` (landed/splashed state).

**13 tasks completed across ~21,000 lines:**

| Task | Summary |
|------|---------|
| 1. Data model + serialization | RecordingTree, BranchPoint, SurfacePosition, TerminalState structs. ConfigNode round-trip. |
| 2. Vessel switch refactoring | Tree-aware TransitionToBackground / PromoteFromBackground decisions. |
| 3. Background recording | Dual-mode: on-rails (OrbitSegment/SurfacePosition) + loaded/physics (full trajectory + part events). |
| 4. Split event detection | Undock/EVA/joint break branching, debris filter, resume-on-false-alarm. |
| 5. Merge event detection | Dock/board merges, dual-lookup for initiator/target, dockingInProgress guard. |
| 6. Terminal event detection | Deferred destruction check, Destroyed/Recovered/Orbiting/Landed/Splashed/SubOrbital states. |
| 7. Tree commit + leaf spawn | CommitTreeFlight, CommitTreeSceneExit, multi-vessel leaf spawning, tree persistence. |
| 8. Tree-aware merge dialog | ShowTreeDialog, revert vs scene-exit branching, per-vessel situation display. |
| 9. Tree ghost playback | Background orbit/surface ghosts, spawn suppression, surface rotation. |
| 10. Tree-level resource tracking | Tree-level delta computation, lump sum playback, budget integration. |
| 11. Backward compatibility | Verification only - existing saves load correctly, no production changes needed. |
| 12. Tree verbose logging | 11 logging gaps filled across RecordingTree, ParsekFlight, ResourceBudget. |
| 13. Tree test coverage | 18 non-vacuous tests + 3 synthetic tree recordings for in-game validation. |

**Test coverage:** 1263 tests.

---

## Future

### Take Control stabilization
Making "jump into a ghost and fly it" reliable is desirable but creates paradox problems: what happens to the recording's future events, reserved crew, and applied resource deltas? Each answer requires hard restrictions that may frustrate players. This needs careful design and should not be rushed.

Current status: experimental button exists in UI, not recommended for normal play.

### Planetarium.right drift compensation for long orbital segments
KSP's inertial reference frame (`Planetarium.right`) may drift over very long time warp durations. This could cause ghost orientation mismatch for interplanetary transfer segments. Needs empirical measurement first — if drift is sub-degree for typical segment lengths, no fix needed. If significant, store `Planetarium.right` snapshot at recording time and apply correction at playback (~10 lines + 3 ConfigNode keys). See `docs/dev/done/design-orbital-rotation.md` Phase 6.

### Additional part event coverage
- Control surface deflection (continuous float - thousands of events per flight, unclear visual value)
- Robotics / Breaking Ground DLC (continuous servo motion, DLC-dependent)
- Two-phase engine startup (spool-up animations on some engines)

---

## Out of Scope

- **Racing modes or lap timing**
- **AI playback or autopilot**
- **Multiplayer synchronization**
- **Timeline branching or alternate histories**
- **Logistics network** - Parsek's recording infrastructure (looped playback, chain segments, vessel snapshots, game state events, resource tracking) forms a natural foundation for automated supply routes between bases. The concept: fly a cargo mission once, Parsek records it, then that recording becomes a reusable logistics route that periodically deducts fuel at the origin and delivers cargo at the destination, with the ghost replaying visually during transit. This will be built as a separate mod on top of Parsek rather than integrated directly. See `docs/dev/research/logistics-network-design.md` for the full design exploration.
