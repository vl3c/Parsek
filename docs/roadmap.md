# Parsek Roadmap

## Philosophy

Parsek is a **git-like recording system** for KSP missions. You record flights sequentially, commit them to a timeline, and they play back automatically as ghost vessels while you fly new missions. There is one timeline, recordings are immutable commits, and the game always moves forward.

Like git, you can go back to any earlier point and start new work. Existing recordings don't change — they play out as ghosts alongside your new missions. There is no branching, no state reversal, and no paradoxes. Conflicts are prevented through resource budgeting: committed recordings have already claimed their costs, so the player can only spend what's actually available.

Time travel paradoxes are avoided by two invariants: **causality** (events are always processed in time-axis order) and **additivity** (the timeline is append-only — committed recordings and game actions can be added but never deleted). The world state at any point in time is fully determined by the ordered sequence of committed events before it. When a new event is inserted into the timeline (e.g., a recording committed at an earlier UT after a rewind), all derived resource values — available science, funds, reputation — are recalculated from scratch across the full timeline. Events themselves are immutable; the computed state that flows from them is not stored but recomputed whenever the event set changes.

---

## Completed

### v0.3 — Foundation

Initial release. The core loop shipped end-to-end: record a flight, commit it, rewind, see it play back as a ghost alongside new missions.

**Recording & playback:** Position recording with adaptive sampling and geographic coordinates. Kinematic ghost playback with opaque vessel replicas. Orbital recording with analytical Keplerian orbits. Auto-recording on launch and EVA. Chained recordings across vessel lifecycle events (undock, EVA, dock). Context-aware merge dialog (Merge to Timeline / Discard). Vessel persistence with deferred spawn, crew reservation, and resource deltas.

**Ghost visual fidelity:** Comprehensive part event types recorded and replayed on ghost vessels — decoupling, staging, parachutes, engines (with FX), solar panels, antennas, radiators, landing gear, lights, cargo bays, fairings, RCS (with FX), docking/undocking, inventory parts.

**Career mode:** Milestones (tech research, part purchases, facility upgrades, contracts, crew changes) captured as immutable timeline commits. Resource budget computed on-the-fly from recordings + milestones. Epoch isolation to prevent abandoned timeline branches from leaking. Resource deduction on revert so KSP's top bar reflects available resources. Action blocking to prevent re-researching committed tech or re-upgrading committed facilities. Per-recording rewind saves with quicksave-based timeline restoration. Rewind UI with confirmation dialog, resource reset, crew re-reservation, and action replay. Fast-forward to advance past committed recordings.

**Recordings Manager UI** with sortable columns, per-recording loop/hide, rewind/fast-forward, and status indicators.

**Design:** `docs/dev/done/design-restore-points.md`, `docs/dev/done/design-going-back-in-time.md`

### v0.4 — Visual Fidelity & Polish

Orbital rotation fidelity, watch camera, heat effects, KSC scene playback, UI polish, and code refactoring.

**Orbital rotation:** Ghost vessels in orbital segments preserve their recorded attitude instead of always facing prograde. Rotation stored relative to the orbital velocity frame at the on-rails boundary and reconstructed from the Keplerian orbit. SAS-locked orientations hold correctly throughout the orbit. PersistentRotation mod detected and supported — spinning vessels reproduced during playback.

**Watch camera:** Camera follows a ghost vessel during playback. Automatically recenters on visible parts after separation events.

**Ghost visuals:** Re-entry heating FX with mesh-surface fire particles. Heat shield ablation, smoke and spark FX on decouple/destroy. Fairing ghost visuals. EVA kerbal facing fix. Explosion visual effect on impact with camera hold.

**KSC playback:** Ghosts visible in KSC scene with overlap support and distance-based part event culling.

**UI & usability:** Per-save settings panel (auto-record, sampling thresholds). Recording stats (max altitude, max speed, max G, distance, duration). Non-revert "Commit Flight" button. Two-phase parachute deploy. Auto-loop with per-recording interval controls. Recording groups with multi-membership. Time warp visual cutoffs and deferred spawn queue. Context-aware rewind button.

**Code health:** Major refactor reducing coupling between modules. Sandbox rewind fix.

**Design:** `docs/dev/done/design-orbital-rotation.md`, `docs/dev/done/design-camera-follow-ghost.md`

### v0.5 — Recording System Redesign (in progress)

Comprehensive redesign of the recording and playback systems. Multi-vessel sessions, ghost chain paradox prevention, spawn safety, time jump, rendering zones, and ghost visual hardening.

**Design:** `docs/parsek-flight-recorder-design.md` (consolidated design document)

**Recording model:**
- Segment boundary rule — only physical structural separation creates new segments; controller changes, part destruction without splitting, and crew transfers are SegmentEvents within a continuing segment
- Crash coalescing — rapid split events grouped into single BREAKUP events
- Environment taxonomy — classification (Atmospheric, ExoPropulsive, ExoBallistic, SurfaceMobile, SurfaceStationary) with hysteresis
- TrackSections — typed trajectory chunks tagged with environment, reference frame, and data source
- Reference frames — ABSOLUTE for physics, ORBITAL_CHECKPOINT for on-rails, RELATIVE for anchor-vessel proximity

**Multi-vessel sessions:**
- Recording tree (DAG) — multi-vessel missions recorded as a single unit with split/merge tracking across undock, EVA, dock, joint break, and breakup events
- Background vessel recording — all vessels in the physics bubble sampled at proximity-based rates with full part event capture
- Booster/debris recording — automatic tree promotion on staging with debris TTL
- Highest-fidelity-wins merge — overlapping Active/Background/Checkpoint data merged per vessel
- Flag planting recording and playback

**Ghost chain paradox prevention:**
- Committed recordings that interact with pre-existing vessels cause those vessels to become ghosts from rewind until the chain tip resolves
- Intermediate spawn suppression — multi-link chains only spawn at the tip
- Ghost conversion — real vessels despawned and replaced with ghost GameObjects during chain windows
- PID preservation — chain-tip spawns preserve the original vessel's persistent ID for cross-tree chain linking
- Ghosting trigger taxonomy — structural events, orbital changes, and part state changes trigger ghosting; cosmetic events do not

**Spawn safety:**
- Bounding box collision detection — spawn blocked when overlapping with loaded vessels
- Ghost extension — ghost continues on propagated orbit/surface past recording end while spawn is blocked
- Trajectory walkback — for immovable blockers, walks backward along recorded trajectory to find a valid spawn position
- Terrain correction — surface spawns adjusted for terrain height changes between recording and playback
- Spawn-die-respawn prevention, spawn abandon flags, retry limits

**Time jump:**
- Relative-state time jump — discrete UT skip preserving rendezvous geometry across ghost chain windows
- Epoch-shifted orbits — orbital elements recomputed at the new UT from state vectors

**Rendering & performance:**
- Distance-based zones — Physics, Visual, Beyond — with zone-aware playback and part event gating
- Ghost soft caps — configurable thresholds with priority-based despawning (partial — despawn works, reduced fidelity and orbit line simplification are placeholder)

**Ghost visual hardening:** Variant textures, damaged wheel filtering, fairing meshes and internal structure, SRB nozzle glow, engine shrouds, initial state seeding for all tracking sets. Compound part visuals (fuel lines, struts). Plume and smoke trail fixes. Control surfaces, robotics/servo detection, cabin lights, animation-based deployables. RCS debounce.

**Ghost world presence:** CommNet relay via antenna specs. Ghost labels. Map view / tracking station stubs (full KSP integration pending).

**Real Spawn Control:** Proximity-based UI for warping to when nearby ghost craft become real vessels. Per-craft warp buttons, sortable columns, countdown display, screen notifications. Countdown column in Recordings Manager.

**UI:** Fast-forward redesigned as instant UT jump. Watch mode distance limits. Spawn abandon status display.

**Remaining work** tracked in `docs/dev/todo-and-known-bugs.md`. Key areas: log spam reduction, ghost map presence KSP integration (tracking station, orbit lines, nav target), UI subgroup controls and EVA recording scope expansion, minor performance optimizations.

---

## Phase 8: Game Actions Recording Redesign

Redesign milestone capture, resource budgeting, and action replay to work correctly across all game modes, validated in order of increasing complexity.

**Design:** `docs/parsek-game-actions-system-design.md`

The game actions system is a standalone resource ledger that tracks every economic event on the timeline. Completely separate from the vessel recording system — coupled only by a recording ID on earning actions. Sidecar files capture rich raw data during flight; extraction to ledger on commit. Two-phase recalculate+patch on warp exit. Unified recalculation walk from UT=0 with two-tier module dependency ordering.

Modules designed:

| Module | Key mechanics |
|--------|--------------|
| Science | Immutable awarded values + derived effective values, per-subject caps via full recalculation sorted by UT |
| Funds | Seeded balance from save, reservation system prevents overspending across rewinds, vessel build costs as recording-associated spendings |
| Reputation | Non-linear gain/loss curve, nominal vs effective values |
| Milestones | Once-ever binary cap, chronological priority, first-tier feeds funds and reputation |
| Kerbals | Reservation from UT=0 as continuous block, temporary vs permanent loss, replacement chains, stand-in generation |
| Facilities | Upgrade/destruction/repair schemas, visual state management during fast-forward, KSP state patching |
| Contracts | UT=0 reservation, accept/complete/fail/cancel lifecycle, parameter progress tracking |
| Strategies | UT=0 reservation, transform layer between first-tier and second-tier modules |

Paradox prevention layers: no-delete invariant, spending reservation (science/funds), UT=0 reservation (kerbals/contracts/strategies). KSP UI patched to display available funds (not gross balance), clamped to zero; Parsek UI shows full breakdown.

### Validation by game mode

Each mode adds a layer of game state complexity. Simpler modes must work flawlessly before the next layer is validated.

**Sandbox:** No resources, no tech tree, no contracts. Recording and ghost playback as pure trajectory replay. Verify no resource budget UI shown, no milestone capture attempted, rewind loads quicksave without action replay, commit path skips all game state delta computation.

**Science mode:** Science budget and tech tree, no funds or reputation. Verify science-only resource tracking, tech unlock milestones captured and replayed correctly, part purchase milestones work without funds checks, action blocking prevents re-researching committed tech, rewind restores science balance and replays tech unlocks only.

**Career mode:** Full complexity — funds + science + reputation + contracts + crew management. Verify all three currencies tracked in resource budget, contract milestone capture (accept, complete, fail, cancel), facility upgrade milestones and action blocking, crew hire/fire milestones, action replay handles all event types correctly after rewind, resource budget remains consistent across rewind/fast-forward cycles. Edge cases: over-committed budgets, rewind past contract completion, facility downgrade on rewind.

---

## Phase 9: Timeline

A unified, chronological view of all committed events across all Parsek systems. The timeline is a read-only query layer — it does not own data. Recordings, game actions, and milestones remain in their respective systems; the timeline pulls from all of them, normalizes entries into a common shape (UT, type, description, source recording ID, visual category), and presents them sorted by UT.

### Timeline object

The timeline shows the full committed history at all times. Everything before the player's current UT has already played out (vessels spawned, resources applied). Everything after the current UT will play out as ghosts when the player advances. There are no branches and no hidden future — the player recorded that future and committed it.

Each entry in the timeline is a normalized event with at minimum: UT, event type, display text, source system (recording / game action / milestone), and optionally a link to the source recording. Ghost chain windows appear as duration entries: "Station Alpha: ghost UT 500–1600" — making it immediately clear why a vessel is untouchable and when it resolves.

### Significance tiers

Every event type belongs to a tier that controls default visibility:

- **T1 (always visible):** recording commit, recording start/end, vessel spawn, tech unlock, contract complete/fail, facility upgrade, crew hire/loss, ghost chain window
- **T2 (visible on expand or filter):** docking, undocking, staging, EVA, science collection, crew transfer, contract accept, fund/reputation transactions
- **T3 (visible on explicit request):** individual part events, controller changes, resource snapshots, SegmentEvents

Default view shows T1 only. Within recordings, events are hierarchically collapsible: top level shows the recording as a block (UT range, vessel name), expanding reveals BranchPoints and game actions, expanding further reveals part events and SegmentEvents.

### UI

Sub-window within the Parsek UI with a vertical scrollable list. Current UT is marked — events before the marker are styled as "completed," events after are styled as "upcoming" (dimmed or distinct color, not hidden). Filter buttons at the top allow toggling event types and tiers. Clicking a recording entry highlights it in the Recordings Manager (and vice versa — selecting a recording in the Manager scrolls the timeline to it).

### Timeline operations

Rewind and fast-forward are timeline operations. The player can rewind to any recording's launch point directly from the timeline (equivalent to the existing rewind button in the Recordings Manager). The per-recording rewind/fast-forward buttons in the Recordings Manager remain as shortcuts.

### Resource graph overlay

A small sparkline running alongside the timeline showing funds, science, and reputation over time. The recalculation walk already computes these values at every event — the overlay exposes them visually. Optional, toggled from the filter bar.

### Static UT marker during warp

The current UT marker does not live-update during time warp. It jumps to the correct position on warp exit, when game state is recalculated and vessels spawn. Live-updating is a potential optimization for later.

### Relationship to Recordings Manager

The Recordings Manager is vessel-centric (list of recordings, per-recording controls). The timeline is time-centric (what happened when). They are complementary views of the same data, cross-linked but not merged.

---

## Phase 10: Launch Origin Awareness

Recordings become location-aware. Each recording knows where it started — body, coordinates, and optionally a named launch site.

- Integration with launch site mods via reflection where available
- Detection of off-world construction mod vessel modules
- Fallback to raw coordinates for mods without formal APIs
- UI grouping and filtering by launch site in the Recordings Manager
- Prerequisite for logistics routes (Phase 12) and async multiplayer (Phase 13)

Small, additive feature. A few metadata fields at recording start.

---

## Phase 11: Resource Snapshots

Recordings capture physical resource inventories (ore, fuel, monoprop, etc.) at segment boundaries.

- Resource snapshots at recording start, end, and key events (docking)
- Uses KSP's resource API — any resource captured automatically, including modded resources
- Event hook for external mods on recording completion
- Simple built-in mode: on playback completion, transfer resources to nearest vessel within configurable range
- Recordings Manager shows resource signatures ("this Minmus run carries 4000 units of ore")

---

## Phase 12: Looped Transport Logistics

Automated supply routes realized through Parsek's existing loop mechanic. Fly a cargo run once, loop the recording, each iteration is a supply delivery.

- **Route definition** — a recording with declared origin (Phase 10) and destination (Phase 10) plus resource manifest (Phase 11)
- **Delivery logic on loop completion** — recording completion triggers resource transfer to nearest vessel within range
- **Round-trip support** — initially two separate looped recordings (outbound loaded, return empty); eventual round-trip recording mode
- **Time scaling** — deliveries at realistic intervals with UI showing "next delivery in: 3d 4h"
- **Visual presence** — ghost supply ships actually fly (as ghosts) on every loop iteration; only approach/departure bubbles spawn visible ghosts, transit is invisible (the boring middle)

Every supply ship is a replay of a real mission the player flew — more immersive than abstracted route systems, more performance-demanding, but achievable within the existing architecture.

---

## Phase 13: Cooperative Async Multiplayer

Multiple players contribute recordings to a shared timeline. The Kerbal system feels populated with vessels flying and bases being built. All players share one game actions timeline — science, funds, reputation, contracts, and kerbals are pooled.

### Recording Export/Import

Prerequisite for multiplayer. Share recordings as standalone archive files. Export bundles recording data, vessel craft, and metadata. Import validates, assigns new IDs, and injects into current save. Handles missing parts gracefully (warn, skip, or substitute).

### Shared Timeline

- **Shared recordings folder** — local or cloud-synced where players drop recording files
- **Import/merge on load** — Parsek scans the shared folder for new recordings and merges them into the local timeline as ghosts; purely additive, no conflict with live game state
- **Player identity** — each recording tagged with a player ID for filtering, color-coding, and attribution
- **Launch pad allocation** — different players claim different launch sites (Phase 10); each player's recordings originate from their claimed sites
- **Version tolerance** — additive recording format means players on different Parsek versions can share recordings safely

Players never need to be online simultaneously. Fly missions, export recordings. Others import whenever they play. The timeline converges over time — correspondence chess, not real-time multiplayer.

---

## Phase 14: Competitive Play & Space Race

Player boundaries that enable competitive multiplayer. Each player has their own game actions timeline (separate science, funds, reputation, contracts, kerbals). Only things with visible effect on the common game world are shared — vessel recordings play as ghosts in everyone's game, but each player's economy is independent.

### Player Boundaries

- **Per-player game actions timeline** — each player's resource ledger is independent; importing another player's recordings adds their ghosts to the world but does not affect the local player's science, funds, or reputation
- **Shared world layer** — vessel recordings (trajectories, part events, ghost chains) are shared and visible to all players; the physical world is common, the economic world is separate
- **Milestone visibility** — all players can see each other's milestones (first orbit, first Mun landing) but milestones only feed into the achieving player's resource pipeline

### Space Race

- **Pre-built recording packs** — a set of recordings representing an AI space program's progression (first orbit on Day 30, Mun landing on Day 150, etc.); hand-crafted or community-contributed
- **Milestone racing** — Parsek already tracks milestones; space race is a comparison UI showing both programs' achievements side by side
- **Difficulty scaling** — different packs for different skill levels
- **Community races** — players export their full campaign as a space race opponent ("Can you beat Scott Manley's career run?")

---

## The Dependency Chain

```
Foundation: Parsek core recording system (v0.3–v0.5, COMPLETE)
    │  Recording, playback, loops, ghost chains, spawn safety,
    │  time jump, rendering zones, multi-vessel sessions
    │
    ▼
Phase 8: Game Actions System (NEXT)
    │  Science, funds, reputation, kerbals, contracts,
    │  facilities, strategies, reservations, recalculation
    │
    ▼
Phase 9: Timeline
    │  Unified chronological view of all committed events,
    │  significance tiers, filtering, rewind from timeline
    │
    ▼
Phase 10: Launch Origin Awareness
    │  Recordings know WHERE they start/end
    │
    ▼
Phase 11: Resource Snapshots
    │  Recordings know WHAT they carry
    │
    ▼
Phase 12: Looped Transport Logistics
    │  Routes = looped recordings with resource delivery
    │
    ▼
Phase 13: Cooperative Async Multiplayer
    │  Shared recordings folder, player identity, shared timeline
    │
    ▼
Phase 14: Competitive Play + Space Race
       Per-player game actions, milestone racing, opponent packs
```

---

## Deferred

### Planetarium.right drift compensation for long orbital segments
KSP's inertial reference frame may drift over very long time warp durations. This could cause ghost orientation mismatch for interplanetary transfer segments. Needs empirical measurement first — if drift is sub-degree for typical segment lengths, no fix needed.

### Crash-safe pending recording recovery
If the game crashes or the player alt-F4s while a merge dialog is pending, the recording data is lost from memory. The sidecar files already exist on disk, but the metadata entry referencing them is missing. Solution: write a pending manifest file when a recording is stashed, auto-recover on next load if the recording wasn't committed or discarded.

### Additional part event coverage
- ~~Control surface deflection~~ (done)
- ~~Robotics / Breaking Ground DLC~~ (partial)
- Two-phase engine startup (spool-up animations on some engines)

### Recording file size optimization
Shorter key names, compact numeric encoding, optional compression, event name deduplication. Becomes important at scale (many recordings, long missions).

### Ghost soft cap completion
Reduced fidelity (mesh part culling) and orbit line simplification full implementations. LOD culling, ghost mesh unloading outside active time range, particle system pooling. Benchmark with synthetic stress tests.

---

## Out of Scope

- **Racing modes or lap timing**
- **AI playback or autopilot**
- **Real-time multiplayer synchronization** — Parsek's multiplayer model is async (Phases 13–14). No shared physics simulation, no lockstep networking.
- **Taking control of recorded vessels** — jumping into a ghost mid-playback creates unresolvable paradoxes (recording future events, reserved crew, applied resource deltas). The complexity is not worth the payoff.
- **Timeline branching or alternate histories**
