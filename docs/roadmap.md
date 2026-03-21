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

Recordings Manager UI with sortable columns, per-recording loop/hide, rewind/fast-forward, and status indicators.

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

## Phase 4: Scale & Integration — Partial

Make the mod work well with many recordings and integrate with the broader KSP ecosystem.

### ~~Ghost soft cap manager~~ (Partial)
`GhostSoftCapManager` implemented with zone-based thresholds and priority classification (looped/debris/timeline). Despawn action fully works. `ReduceFidelity` (mesh part culling) and `SimplifyToOrbitLine` (orbit line replacement) are placeholder/partial — ghosts are hidden but not replaced with simplified representations. Settings UI integrated.

Remaining performance work:
- LOD culling (don't render ghost meshes far from camera)
- Ghost mesh unloading outside active time range
- Particle system pooling for engine/RCS FX
- `ReduceFidelity` and `SimplifyToOrbitLine` full implementations
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
Game state events (tech research, part purchases, facility upgrades, contracts, crew changes) are captured into milestones - immutable timeline commits independent of recordings. Created at recording commit time and on save (FlushPendingEvents captures events that happen without a flight). Hiding a recording does not affect its milestone.

### Resource budget (done)
Computed on-the-fly from recordings + milestones, partial-replay aware. Displayed in the Parsek UI when any resources are reserved. Red text + yellow "Over-committed!" warning when available resources go negative.

### Epoch isolation (done)
Reverts increment CurrentEpoch. New events are stamped with the current epoch. Old-branch events are excluded from new milestones, preventing abandoned timeline branches from leaking.

### Resource deduction on revert (done)
On revert, committed funds/science/reputation are deducted from game state so KSP's top bar and purchase checks reflect available resources.

### Action blocking (done)
Harmony patches on `RDTech.UnlockTech` and `UpgradeableFacility.SetLevel` prevent re-researching committed tech or re-upgrading committed facilities. Explanatory popup dialog shows what's blocked and why.

### Rewind (done)
Each recording owns a quicksave captured at recording start, stored in `Parsek/Saves/` (invisible to KSP's load menu). Resource snapshot (funds, science, reputation) captured alongside the quicksave for baseline resource reset on rewind. Only chain/tree roots get rewind saves (promotions skip capture).

### Rewind UI (done)
"R" (rewind) / "FF" (fast-forward) button per recording in the Recordings window. Confirmation dialog shows vessel name, launch date, future recording count, and warnings. On confirm: loads the quicksave, strips recorded vessel from flight state, transitions to Space Center. Deferred coroutine sets UT via Planetarium.SetUniversalTime (must happen after scene load - setting before LoadScene gets overwritten by the scene transition). Increments epoch, resets milestone mutable state, resets playback state, resets resources to PreLaunch baseline values, re-reserves crew, replays committed actions (tech, parts, facilities, crew via ActionReplay). All committed recordings replay as ghosts from the rewound point, re-applying resource deltas at the correct UT.

### Action replay (done)
After rewind resource adjustment, `ActionReplay.ReplayCommittedActions` programmatically re-applies committed game actions from milestones: tech unlock (via `UnlockProtoTechNode`, no science deduction), part purchase, facility upgrade, and crew hire. Each handler has idempotent guards (skip if already applied). Suppression flags prevent re-recording during replay.

**Design:** `docs/dev/done/design-restore-points.md`, `docs/dev/done/design-going-back-in-time.md`

**Test coverage:** 1342 tests.

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

**Test coverage:** 2988 tests.

---

## Phase 7: Flight Recording System Redesign — In Progress

Comprehensive redesign of the recording and playback systems, built on the `recording-system-redesign` branch. Encompasses Phase 6 (Recording Tree) plus format evolution, ghost visual hardening, part coverage expansion, and robustness improvements.

**Design:** `docs/parsek-flight-recorder-design.md` (consolidated design document, also rendered as PDF)

**Archived working documents:** `docs/dev/done/recording-system-redesign/`

### What's done

- **Recording Tree** (Phase 6) — 13 tasks, multi-vessel recording with background capture, tree commit, leaf spawn
- **Format v6** — SegmentEvents, TrackSections, ControllerInfo, extended BranchPoint types
- **Ghost visual hardening** — variant textures, damaged wheel filtering, fairing meshes, SRB nozzle glow, engine shrouds, initial state seeding for all 15+ tracking sets
- **Part coverage expansion** — control surfaces, robotics/servo detection, cabin lights (ColorChanger), AnimateGeneric-based deployables
- **RCS debounce** — 8-frame minimum activation filter eliminates SAS micro-correction noise
- **KSC ghost playback** — ghosts visible in KSC scene with distance-based part event culling
- **Ghost soft cap manager** — zone-based priority despawn (partial, see Phase 4)
- **Log spam mitigation** — rate-limited high-volume diagnostics (SoftCap, GhostVisual, ShouldTriggerExplosion, CanRewind)
- **59 bugs documented** — 44 fixed, see `docs/dev/known-bugs.md`

### What remains

**Critical:**
- #51 — Chain ID lost on vessel-switch auto-stop (chain segments become disconnected)

**High priority:**
- #52 — CanRewind log spam (485K lines/session)
- #53 — "re-shown after warp-down" log spam (16K lines/session)
- #54 — Watch mode follows ghost beyond terrain loading range (~120km limit)
- #55 — RELATIVE anchor triggers on debris and launch pad structures

**Medium priority:**
- #45 — Suborbital vessel spawn causes explosion
- #46 — EVA kerbals disappear in water after spawn (needs investigation)
- #50 — UI subgroups missing enable/loop checkboxes
- #56 — EVA recordings only created from launch pad (design limitation)
- #60 — Ghost map presence stubs not implemented (tracking station, orbit lines, nav target)
- #61 — Controlled children have no recording segments after breakup

**Low priority:**
- #43 — Ghost variant shader not found: KSP/Emissive Specular (cosmetic)
- #47 — TestSinkForTesting race condition (workaround in place)
- #48 — ComputeBoundaryDiscontinuity hardcodes Kerbin radius (diagnostic only)
- #49 — RealVesselExists O(n) per frame
- #57 — Boarding confirmation expired on vessel switch
- #58 — Background recording requires KSP debris persistence
- #62 — Background ghost cachedIdx not persistent (minor perf)
- #63 — Log contract checker lacks error whitelist (test infra)

**Merge to main** after critical and high-priority items resolved.

---

## Phase 8: Game Actions Recording Redesign

Redesign milestone capture, resource budgeting, and action replay to work correctly across all game modes, validated in order of increasing complexity.

### Sandbox
No resources, no tech tree, no contracts. Recording and ghost playback work as pure trajectory replay with zero game state interactions. Verify:
- No resource budget UI shown
- No milestone capture attempted
- Rewind loads quicksave without action replay
- Commit path skips all game state delta computation

### Science mode
Science budget and tech tree, no funds or reputation. Verify:
- Science-only resource tracking (no funds/rep deltas)
- Tech unlock milestones captured and replayed correctly
- Part purchase milestones work without funds checks
- Action blocking prevents re-researching committed tech
- Rewind restores science balance and replays tech unlocks only

### Career mode
Full complexity: funds + science + reputation + contracts + crew management. Verify:
- All three currencies tracked in resource budget
- Contract milestone capture (accept, complete, fail, cancel)
- Facility upgrade milestones and action blocking
- Crew hire/fire milestones
- Action replay handles all event types correctly after rewind
- Resource budget remains consistent across rewind/fast-forward cycles
- Edge cases: over-committed budgets, rewind past contract completion, facility downgrade on rewind

Each mode adds a layer of game state complexity. The simpler modes must work flawlessly before the next layer is validated.

---

## Future

### Planetarium.right drift compensation for long orbital segments
KSP's inertial reference frame (`Planetarium.right`) may drift over very long time warp durations. This could cause ghost orientation mismatch for interplanetary transfer segments. Needs empirical measurement first — if drift is sub-degree for typical segment lengths, no fix needed. If significant, store `Planetarium.right` snapshot at recording time and apply correction at playback (~10 lines + 3 ConfigNode keys). See `docs/dev/done/design-orbital-rotation.md` Phase 6.

### Crash-safe pending recording recovery
If the game crashes or the player alt-F4s while a merge dialog is pending, the recording data is lost from memory. The sidecar files (`.prec`, `_vessel.craft`, `_ghost.craft`) already exist on disk, but the metadata entry in `.sfs` referencing them is missing. Solution: write a `pending_manifest.cfg` file to `Parsek/Recordings/` when a recording is stashed. On next game load, if the manifest exists but the recording ID isn't in committed recordings, auto-recover it. Delete the manifest on commit or discard.

### Additional part event coverage
- ~~Control surface deflection~~ (done — recorded via AnimateGeneric pattern)
- ~~Robotics / Breaking Ground DLC~~ (partial — servo detection implemented, continuous position tracking in place)
- Two-phase engine startup (spool-up animations on some engines)

---

## Out of Scope

- **Racing modes or lap timing**
- **AI playback or autopilot**
- **Multiplayer synchronization**
- **Taking control of recorded vessels** - jumping into a ghost mid-playback creates unresolvable paradoxes (recording future events, reserved crew, applied resource deltas). The complexity is not worth the payoff.
- **Timeline branching or alternate histories**
- **Logistics network** - Parsek's recording infrastructure (looped playback, chain segments, vessel snapshots, game state events, resource tracking) forms a natural foundation for automated supply routes between bases. The concept: fly a cargo mission once, Parsek records it, then that recording becomes a reusable logistics route that periodically deducts fuel at the origin and delivers cargo at the destination, with the ghost replaying visually during transit. This will be built as a separate mod on top of Parsek rather than integrated directly. See `docs/dev/research/logistics-network-design.md` for the full design exploration.
