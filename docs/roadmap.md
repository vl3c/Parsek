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

### Settings panel
`GameParameters.CustomParameterNode` for in-game settings:
- Toggle auto-record on/off (some players find it intrusive)
- Adjust sampling thresholds (orientation, velocity, max interval)
- Toggle auto-warp-stop on/off

This is the single highest-impact QoL feature — players who dislike auto-record currently have no way to turn it off.

### Recording stats
Compute from existing trajectory data (no new recording needed):
- Max altitude, max speed, max G-force
- Distance travelled, final destination body
- Duration (already shown), point count

Display in Recordings Manager as expandable detail or tooltip per recording.

### Non-revert recording commitment
Currently the flow requires revert-to-launch to commit. Add a "Commit Flight" action (via UI button) that snapshots the current vessel state and adds the recording to the timeline without reverting. This unlocks the mod for players who don't revert — they fly a mission, commit it, and the recording is available for future playback. The player continues flying the same vessel normally.

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

## Phase 5: Going Back in Time

The headline feature: go back to any earlier point in time and launch new missions while existing recordings play out as ghosts.

### How it works
Like `git checkout` — the player picks a restore point, the game loads that earlier state, and all committed recordings remain on the timeline. Ghosts appear at their scheduled UTs as the player plays forward. No branching, no state reversal.

### Restore points
Parsek auto-saves at recording commit points, creating tagged restore points. A "Go Back" UI lets the player pick any restore point and load it. Recording data (all recordings, crew reservations, resource commitments) is preserved across the load.

### Resource budgeting
Committed recordings have already claimed their resource costs. When the player goes back in time, available funds/science/reputation = the game state at the restore point minus resources committed to future recordings. If a recording at UT 15000 costs 15,000 funds to launch, those funds are unavailable at UT 10000. The player cannot launch a mission they can't afford after accounting for future commitments.

This prevents paradoxes through simple accounting: "you can't spend money you've already committed to future missions."

### What doesn't need to change
- Recordings are immutable — they play back exactly as flown
- Crew reservation already prevents double-booking
- Ghost playback and vessel spawning work at any UT
- Resource deltas re-apply naturally during ghost playback (reset `LastAppliedResourceIndex` for recordings after the restore point)

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

- Racing modes or lap timing
- AI playback or autopilot
- Multiplayer synchronization
- Timeline branching or alternate histories
