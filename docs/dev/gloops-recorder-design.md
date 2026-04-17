# Gloops — Ghost Loop Playback System

*Design document for Gloops, a standalone KSP1 mod for ghost vessel recording, playback, and content pack loading. Gloops is extracted from Parsek's existing ghost subsystem. This document describes the extraction boundary grounded in the actual codebase.*

---

## 1. Introduction

### 1.1 What Gloops Is

Gloops is a standalone KSP1 mod that records vessel flights and plays them back as ghost vessels — visual replicas that follow recorded trajectories with full part event fidelity. Ghosts have no physics, no colliders, no game state — they are animated scenery.

Gloops also loads content packs: pre-authored ghost loop packages that add background activity to the game world — KSC air traffic, rover patrols, EVA kerbals, or any other visual presence.

### 1.2 What Gloops Is Not

Gloops has no concept of timelines, rewind, resource budgets, vessel identity beyond visual tracking, or game state management. It does not spawn real KSP vessels. It does not know about DAG structures, merge events, ghost chains, or spawn policy. These are concerns of mods that build on top of Gloops (such as Parsek).

### 1.3 Relationship to Parsek

Gloops is extracted from Parsek's existing ghost recording and playback code. The extraction boundary follows the interfaces and class boundaries that already exist in the codebase. Parsek depends on Gloops as a hard dependency. Gloops depends on nothing mod-wise.

| Layer | Owner | Existing code |
|---|---|---|
| Ghost playback engine | Gloops | `GhostPlaybackEngine.cs` — already has zero Recording references |
| Ghost mesh construction | Gloops | `GhostVisualBuilder.cs` — builds GameObjects from snapshots |
| Part event replay & FX | Gloops | Engine-layer methods in `GhostPlaybackLogic.cs` |
| Trajectory interpolation | Gloops | `TrajectoryMath.cs` (sampling, interpolation, orbit search) |
| Zone rendering | Gloops | `RenderingZoneManager.cs` |
| Soft cap management | Gloops | `GhostSoftCapManager.cs` |
| Trajectory recorder | Gloops | Trajectory sampling + part event capture (extracted from `FlightRecorder.cs`) |
| Content pack system | Gloops | New code (loader, manifest parser, pack management) |
| Standalone UI | Gloops | New code (loop manager, settings — disabled when consumer provides UI) |
| Recording tree / DAG | Consumer (Parsek) | `RecordingTree.cs`, `BranchPoint.cs` |
| Chain ghost logic | Consumer (Parsek) | `GhostChainWalker.cs`, `GhostChain.cs` |
| Spawn policy | Consumer (Parsek) | `ParsekPlaybackPolicy.cs` — subscribes to Gloops lifecycle events |
| Background recording | Consumer (Parsek) | `BackgroundRecorder.cs` — multi-vessel sessions |
| Post-commit optimization | Consumer (Parsek) | `RecordingOptimizer.cs` — environment-boundary splitting |
| World presence | Consumer (Parsek) | CommNet relay, tracking station, map view markers |
| Game state | Consumer (Parsek) | Resource tracking, milestones, rewind, timeline |

---

## 2. Design Philosophy

1. **Gloops plays trajectories, not trees.** It receives a flat indexed list of trajectories. Tree topology (staging decomposition, chain linking, merge events) is invisible to the engine. This is already how `GhostPlaybackEngine` works — it takes `IReadOnlyList<IPlaybackTrajectory>` and processes each independently.

2. **A trajectory is the atom.** One vessel, one continuous path, with part events and optional loop configuration. This maps to `IPlaybackTrajectory` — the interface that `Recording` already implements and that future content pack trajectories will too.

3. **Policy flows in as flags, lifecycle flows out as events.** The engine reads `TrajectoryPlaybackFlags` (skip this ghost, hold at end, needs spawn) and `FrameContext` (current UT, warp rate). It fires lifecycle events (`PlaybackCompletedEvent`, `LoopRestartedEvent`, `OverlapExpiredEvent`, `CameraActionEvent`). It never makes policy decisions.

4. **Positioning is delegated.** The engine doesn't know about celestial bodies, floating-origin corrections, or orbit propagation. It delegates all world-space positioning through `IGhostPositioner` — 8 methods covering interpolation, surface hold, orbital positioning, and zone rendering.

5. **Content packs are trajectories with metadata.** A `.gloop` file is a serialized trajectory that implements the same interface as a Parsek recording. The content pack system is a loader and spawn-condition manager around this data.

6. **Non-vessel meshes are a future extension.** The ghost mesh builder currently constructs from KSP vessel snapshots. The architecture supports adding a custom mesh path (for birds, scenery, etc.) without restructuring — the builder already has a clean entry point that produces a GameObject from a snapshot.

---

## 3. Core Data Model

The data model maps 1:1 to existing Parsek types. Gloops defines the canonical versions; Parsek's `Recording` wraps them with its metadata envelope.

### 3.1 Trajectory Interface

The engine accesses all trajectory data through `IPlaybackTrajectory` (31 properties). This interface is already the extraction boundary — `GhostPlaybackEngine` references nothing else.

```
IPlaybackTrajectory
  // Trajectory data
  Points:              list of TrajectoryFrame
  OrbitSegments:       list of OrbitalCheckpoint
  HasOrbitSegments:    bool
  TrackSections:       list of TrackSection
  StartUT:             double
  EndUT:               double
  RecordingFormatVersion: int

  // Visual events
  PartEvents:          list of PartEvent
  FlagEvents:          list of FlagEvent

  // Visual snapshots
  GhostVisualSnapshot: ConfigNode — part layout for ghost mesh construction
  VesselSnapshot:      ConfigNode — full vessel state (for spawn — used by consumer)
  VesselName:          string

  // Loop configuration
  LoopPlayback:        bool
  LoopIntervalSeconds: double
  LoopTimeUnit:        enum (Sec, Min, Hour, Auto)
  LoopAnchorVesselId:  uint — anchor vessel for relative loop playback
  LoopStartUT:         double — loop range start (NaN = use StartUT)
  LoopEndUT:           double — loop range end (NaN = use EndUT)

  // Terminal state
  TerminalStateValue:  TerminalState? — for explosion FX at trajectory end
  SurfacePos:          SurfacePosition? — for surface hold after trajectory end

  // Terminal orbit (for post-trajectory orbit propagation)
  TerminalOrbitBody:            string
  TerminalOrbitSemiMajorAxis:   double
  TerminalOrbitEccentricity:    double
  TerminalOrbitInclination:     double
  TerminalOrbitLAN:             double
  TerminalOrbitArgumentOfPeriapsis: double
  TerminalOrbitMeanAnomalyAtEpoch: double
  TerminalOrbitEpoch:           double

  // Rendering hints
  PlaybackEnabled:     bool
  IsDebris:            bool
  LoopSyncParentIdx:   int — debris follows parent trajectory's loop clock (-1 = independent)
```

**Note on VesselSnapshot:** The interface exposes it because the consumer needs it for spawn decisions at playback end. Gloops itself only reads `GhostVisualSnapshot` for mesh construction. VesselSnapshot passes through the interface unchanged.

**Note on TerminalOrbit fields:** The engine uses these for orbit propagation when a ghost reaches trajectory end but the consumer requests continued positioning (mid-chain hold, ghost extension). These are engine concerns, not consumer metadata.

### 3.2 Trajectory Frame

Position sample. Equivalent to Parsek's `TrajectoryPoint` minus game-state fields.

```
TrajectoryFrame
  ut:           double
  latitude:     double
  longitude:    double
  altitude:     double
  rotation:     Quaternion — surface-relative
  velocity:     Vector3 — surface-relative
  bodyName:     string — reference celestial body
```

Parsek's `TrajectoryPoint` additionally carries `funds`, `science`, `reputation` — game-state fields the engine never reads. On extraction, TrajectoryFrame drops these fields. Parsek stores game-state deltas in a parallel structure indexed by UT (already the case for the resource ledger).

### 3.3 Track Section

A typed trajectory chunk with environment and reference frame metadata.

```
TrackSection
  environment:        ATMOSPHERIC | EXO_PROPULSIVE | EXO_BALLISTIC |
                        SURFACE_MOBILE | SURFACE_STATIONARY | APPROACH
  referenceFrame:     ABSOLUTE | RELATIVE | ORBITAL_CHECKPOINT
  startUT:            double
  endUT:              double
  anchorVesselId:     uint — for RELATIVE frame only
  frames:             list of TrajectoryFrame — for ABSOLUTE/RELATIVE
  checkpoints:        list of OrbitalCheckpoint — for ORBITAL_CHECKPOINT
  sampleRateHz:       float
  source:             ACTIVE | BACKGROUND | CHECKPOINT
  boundaryDiscontinuityMeters: float
  minAltitude:        float
  maxAltitude:        float
```

This struct is identical to the existing `TrackSection` in `TrackSection.cs`.

### 3.4 Orbital Checkpoint

Keplerian elements for on-rails coasting. Equivalent to existing `OrbitSegment`.

```
OrbitalCheckpoint
  startUT:            double
  endUT:              double
  inclination:        double
  eccentricity:       double
  semiMajorAxis:      double
  longitudeOfAscendingNode: double
  argumentOfPeriapsis: double
  meanAnomalyAtEpoch: double
  epoch:              double
  bodyName:           string
  orbitalFrameRotation: Quaternion
```

### 3.5 Part Event

Discrete visual state change on a specific part. 35 event types covering all visually-relevant vessel state changes.

```
PartEvent
  ut:                 double
  partPersistentId:   uint
  eventType:          PartEventType (35 values: EngineIgnited, EngineShutdown,
                        EngineThrottle, DeployableExtended, DeployableRetracted,
                        LightOn, LightOff, GearDeployed, GearRetracted,
                        ParachuteDeployed, ParachuteSemiDeployed, ParachuteCut,
                        CargoBayOpened, CargoBayClosed, FairingJettisoned,
                        RCSActivated, RCSStopped, RCSThrottle, Decoupled, Destroyed,
                        Docked, Undocked, LightBlinkEnabled, LightBlinkDisabled,
                        LightBlinkRate, InventoryPartPlaced, InventoryPartRemoved,
                        RoboticMotionStarted, RoboticPositionSample, RoboticMotionStopped,
                        ThermalAnimationHot, ThermalAnimationCold, ThermalAnimationMedium,
                        ShroudJettisoned, ParachuteDestroyed)
  partName:           string
  value:              float — throttle 0-1, blink rate, robotic position, etc.
  moduleIndex:        int — disambiguates multi-engine/multi-module parts
```

---

## 4. Ghost Playback Engine

The engine is `GhostPlaybackEngine.cs` — already has zero Recording references. It manages ghost GameObjects, per-frame positioning, part event application, loop/overlap playback, zone transitions, and soft caps.

### 4.1 Engine Interface

```
UpdatePlayback(
  trajectories: IReadOnlyList<IPlaybackTrajectory>,   // one per trajectory
  flags:        TrajectoryPlaybackFlags[],            // per-trajectory policy
  ctx:          FrameContext                           // per-frame context
)
```

Called once per physics frame by the host. The engine iterates trajectories, creates/destroys/positions ghost GameObjects, applies part events, manages loops, evaluates soft caps.

### 4.2 Per-Trajectory Policy Flags

```
TrajectoryPlaybackFlags
  skipGhost:              bool — don't render (chain-suppressed, disabled)
  isStandalone:           bool — not part of a tree (gates resource events). Always false post-T56 (all recordings are tree recordings)
  isMidChain:             bool — hold ghost at end position instead of destroying
  chainEndUT:             double — when the full chain ends
  needsSpawn:             bool — pre-computed spawn decision
  isActiveChainMember:    bool — belongs to currently recording chain
  isChainLooping:         bool — chain has a branch-0 looping segment
  segmentLabel:           string — for logging
  recordingId:            string — identity key for events
  vesselPersistentId:     uint — identity for events
```

All policy is pre-computed by the consumer (Parsek's `ParsekFlight`) before calling `UpdatePlayback`. The engine reads flags, never computes policy.

### 4.3 Per-Frame Context

```
FrameContext
  currentUT:              double
  warpRate:               float
  warpRateIndex:          int
  activeVesselPos:        Vector3d — for distance checks
  protectedIndex:         int — watch mode exempt ghost (-1 if none)
  externalGhostCount:     int — chain ghosts etc. for soft cap accounting
  autoLoopIntervalSeconds: double — from settings
```

### 4.4 Ghost State

Each active ghost has a `GhostPlaybackState` containing:
- GameObject reference and material lists
- Part event cursor (current position in the event list)
- Per-part ghost info objects (engine FX, RCS FX, deployables, lights, fairings, heat, robotics, parachutes, compound parts — all defined in `GhostTypes.cs`)
- Part subtree map (for decouple hiding)
- Loop cycle tracking (current cycle index, overlap list)
- Zone rendering state
- Reentry FX state

### 4.5 Positioning

The engine delegates all world-space placement through `IGhostPositioner` (8 methods):

| Method | Purpose |
|---|---|
| `InterpolateAndPosition` | Standard trajectory interpolation (ABSOLUTE frame) |
| `InterpolateAndPositionRelative` | RELATIVE frame with anchor vessel offset |
| `PositionAtPoint` | Snap to a specific trajectory point |
| `PositionAtSurface` | Surface hold (landed/splashed post-trajectory) |
| `PositionFromOrbit` | Keplerian orbit propagation (post-trajectory or on-rails) |
| `PositionLoop` | Loop positioning (delegates to appropriate method based on track section) |
| `ApplyZoneRendering` | Distance-based rendering zone evaluation |
| `ClearOrbitCache` | Invalidate cached Orbit objects |

The host (ParsekFlight) implements this interface. It handles body lookups, floating-origin corrections, and Unity coordinate transforms. The engine knows nothing about KSP's world frame.

### 4.6 Lifecycle Events

The engine fires events through a callback list. The consumer subscribes to make policy decisions.

```
PlaybackCompletedEvent     — ghost reached trajectory end (or chain end)
  .GhostWasActive          — was a ghost GameObject visible?
  .PastEffectiveEnd        — exceeded the effective end UT?
  .LastPoint               — final trajectory point (for spawn positioning)
  .CurrentUT               — when playback completed

LoopRestartedEvent         — looping ghost completed a cycle
  .PreviousCycleIndex
  .NewCycleIndex
  .ExplosionFired          — explosion FX played at cycle end?
  .ExplosionPosition

OverlapExpiredEvent        — overlap ghost (negative-interval loop) expired
  .CycleIndex
  .ExplosionFired
  .ExplosionPosition

CameraActionEvent          — camera manipulation request
  .Action                  — ExplosionHoldStart, ExplosionHoldEnd, RetargetToNewGhost, ExitWatch
  .AnchorPosition, .GhostPivot, .HoldUntilUT, .NewCycleIndex
```

### 4.7 Loop System

The engine supports three loop modes, configured per-trajectory:
- **Positive interval:** Ghost plays, waits `LoopIntervalSeconds`, replays. Standard loop.
- **Negative interval (overlap):** New cycle starts before previous ends. Multiple concurrent ghost meshes. Capped at `MaxOverlapGhostsPerRecording = 5`.
- **Loop sync:** Debris trajectories (`LoopSyncParentIdx >= 0`) follow their parent trajectory's loop clock. Boosters replay in sync with the core stage.

Loop range can be narrowed via `LoopStartUT`/`LoopEndUT` (optimizer trims boring bookends).

### 4.8 Rendering Zones

Distance-based rendering tiers managed by `RenderingZoneManager`:

| Zone | Default Range | Behavior |
|---|---|---|
| Full fidelity | 0 – 2.3 km | Full mesh, all part events, engine FX |
| Visual range | 2.3 – 120 km | Mesh only, no part events or FX |
| Beyond visual | 120 km+ | No rendering. Position tracked logically. |

### 4.9 Soft Cap

Priority-based ghost count management via `GhostSoftCapManager`. Each ghost has a `GhostPriority` (SCENERY, LOOP, PLAYBACK, CRITICAL). When thresholds are exceeded, lower-priority ghosts are degraded (reduce FX) then despawned. CRITICAL ghosts are never despawned.

---

## 5. Ghost Visual Builder

`GhostVisualBuilder.cs` constructs ghost GameObjects from vessel snapshot ConfigNodes.

### 5.1 Construction

- Reads PART nodes from the snapshot
- Clones part meshes from KSP prefab parts via `PartLoader.getPartInfoByName`
- Applies variant textures, materials, and mesh rules from TEXTURE/MATERIAL/GAMEOBJECT configs
- Builds procedural fairing meshes from XSECTION data
- Constructs engine shrouds with variant awareness
- Names each part child by `persistentId` for O(1) lookup during part event replay

### 5.2 FX Construction

- Engine FX: clones `MODEL_MULTI_PARTICLE` particle systems from EFFECTS configs, filtered by `runningEffectName`
- RCS FX: parallel construction via `RcsGhostInfo`, filtered by `ModuleRCSFX.runningEffectName`
- Reentry FX: mesh-surface fire particles
- Separation FX: smoke puff + sparks on decouple/destroy

### 5.3 Part State Types

Each ghost part has typed info objects (defined in `GhostTypes.cs`):
- `EngineGhostInfo` — particle systems, emission rate, throttle state
- `RcsGhostInfo` — per-thruster particle systems
- `DeployableGhostInfo` — stowed/deployed transform states (sampled from animation)
- `FairingGhostInfo` — procedural cone mesh
- `LightGhostInfo` — Unity Light component reference
- `ParachuteGhostInfo` — semi-deployed/deployed mesh variants
- `RoboticGhostInfo` — servo transform and limits
- `HeatGhostInfo` — thermal animation material states
- `ColorChangerGhostInfo` — ModuleColorChanger material states
- `ReentryFxInfo` — fire particle system references

### 5.4 Future: Custom Mesh Path

For non-vessel content (birds, custom scenery), the builder needs a second entry point that takes a mesh definition (path, texture, scale, animation) instead of a vessel snapshot. The current architecture supports this — `BuildGhost` is a clean entry point that produces a GameObject. A `BuildCustomMeshGhost` method can be added alongside without restructuring.

---

## 6. Trajectory Recorder

Gloops ships a minimal recorder for standalone use. It records a single vessel's trajectory and part events — the visual data needed for ghost replay.

### 6.1 What the Recorder Captures

Extracted from the trajectory sampling and part event capture code in `FlightRecorder.cs`:

- **Trajectory frames** — adaptive sampling via `TrajectoryMath.ShouldRecordPoint` (velocity, acceleration, angular change thresholds)
- **Track sections** — environment classification (`SegmentEnvironment` taxonomy) with hysteresis, reference frame tagging
- **Part events** — 35 types across 16 tracking sets, polled every physics frame
- **Orbital checkpoints** — Keplerian elements at on-rails/off-rails boundaries
- **Vessel snapshot** — at recording start (ghost visual) and periodic refresh (`RefreshBackupSnapshot`)
- **Atmosphere/altitude/SOI boundaries** — detected during recording, emitted as metadata for the consumer to split on

### 6.2 What the Recorder Does NOT Capture

These are consumer (Parsek) concerns, not part of the Gloops recorder:

- Game state (funds, science, reputation) — Parsek stores these in a parallel ledger
- Crew assignments or transfers — Parsek's `SegmentEvent` tracks these
- Controller identity or changes — Parsek's identity tracking
- Resource levels — Parsek's Phase 11 feature
- Background vessel trajectories — Parsek's `BackgroundRecorder`
- Tree/DAG structure — Parsek's `RecordingTree` / `BranchPoint`
- Post-commit splitting — Parsek's `RecordingOptimizer`

### 6.3 Recording Flow

```
Recording triggered (manual or by consumer)
  -> Begin sampling active vessel
  -> Trajectory frames added via adaptive sampling
  -> Track sections managed by environment hysteresis
  -> Part events polled every physics frame
  -> On-rails transitions: orbit segment captured, boundary point sampled
  -> Atmosphere/altitude/SOI boundary: metadata emitted (consumer decides whether to split)
  -> On recording stop:
       Vessel snapshot captured (end-state)
       Final orbit segment closed, final track section closed
       Part events sorted chronologically
       Data returned to consumer
```

### 6.4 Staging and Splits

When the recorded vessel stages or decouples, the Gloops recorder does not create child segments or a segment group. It continues recording the vessel it was tracking (whichever piece retains the focus).

The consumer detects staging events (via KSP callbacks like `onPartJointBreak`) and handles the tree implications — creating child Recording objects, starting new recorders for each piece, linking them via BranchPoints. From Gloops's perspective, one recording stopped and another started.

---

## 7. Content Pack System

### 7.1 Pack Structure

```
GameData/Gloops/Packs/KSCTraffic/
  manifest.cfg                    — pack metadata and loop definitions
  loops/
    cargo_plane_circuit.gloop     — recorded trajectory
    rover_patrol.gloop
  meshes/                         — optional: custom (non-KSP-part) meshes
    seagull.mu
    seagull.png
```

### 7.2 Manifest Format

KSP ConfigNode for ecosystem consistency:

```
GLOOPS_PACK
{
  name = KSC Traffic
  author = ExampleAuthor
  version = 1.0
  description = Background traffic around KSC

  LOOP
  {
    file = loops/cargo_plane_circuit.gloop
    anchorBody = Kerbin
    anchorLatitude = -0.0972
    anchorLongitude = -74.5577
    spawnCondition = KSC_LOADED
    loopInterval = 300
    priority = SCENERY
    enabled = true
  }
}
```

### 7.3 Spawn Conditions

| Condition | Trigger |
|---|---|
| KSC_LOADED | KSC scene or flight near KSC |
| BODY_LOADED | Player is at the specified body |
| DISTANCE | Player is within configurable radius of anchor point |
| ALWAYS | Active whenever the game is running |

### 7.4 Validation

On game load, Gloops validates packs: manifest parses, `.gloop` files exist with valid headers, referenced KSP parts exist in part database. Failures are logged clearly. Broken loops are skipped. Missing parts degrade gracefully (incomplete mesh).

### 7.5 Pack State

Per-save state file tracks which packs and loops are enabled/disabled. Toggled via standalone UI or consumer API.

---

## 8. Consumer API

The API is defined by the existing interfaces. A consumer like Parsek interacts with Gloops through these contracts:

### 8.1 Data Contract: IPlaybackTrajectory

The consumer provides trajectory data by implementing `IPlaybackTrajectory` (Section 3.1). Parsek's `Recording` already implements this. Content pack trajectories also implement it.

### 8.2 Per-Trajectory Policy: TrajectoryPlaybackFlags

The consumer fills a `TrajectoryPlaybackFlags` struct per trajectory before each `UpdatePlayback` call. This is how the consumer tells the engine what to do without the engine knowing why (Section 4.2).

### 8.3 Per-Frame Context: FrameContext

The consumer provides physical context each frame via `FrameContext` (Section 4.3).

### 8.4 Positioning: IGhostPositioner

The consumer implements `IGhostPositioner` to handle all world-space placement (Section 4.5). Gloops calls these methods; the consumer does the KSP-specific coordinate transforms.

### 8.5 Lifecycle Events

The consumer subscribes to lifecycle events (Section 4.6) to react to playback completion, loop restarts, overlap expiry, and camera actions.

### 8.6 Recording Control

```
GloopsRecorder.Start(vessel)  -> recordingSessionId
GloopsRecorder.Stop()         -> trajectory data (frames, part events, track sections, snapshots)
GloopsRecorder.GetState()     -> in-progress trajectory data
```

The consumer calls these to drive recording. The returned trajectory data becomes the visual core of whatever the consumer stores (Parsek wraps it in a `Recording` with metadata).

### 8.7 Content Pack Control

```
GloopsAPI.GetInstalledPacks()          -> list of pack metadata
GloopsAPI.SetPackEnabled(packId, bool)
GloopsAPI.SetLoopEnabled(loopId, bool)
```

### 8.8 UI Suppression

```
GloopsAPI.RegisterConsumerUI()    — disables Gloops standalone UI
GloopsAPI.UnregisterConsumerUI()  — re-enables it
```

When Parsek is installed, it registers as consumer and provides all UI itself.

---

## 9. Parsek Integration

### 9.1 Data Flow

```
Parsek Recording (stored in timeline, serialized in .prec/.sfs)
  = Gloops trajectory data (implements IPlaybackTrajectory)
      Points, OrbitSegments, TrackSections, PartEvents, FlagEvents,
      GhostVisualSnapshot, loop config
  + Parsek metadata envelope:
      RecordingId, TreeId, VesselPersistentId
      DAG linkage (ParentBranchPointId, ChildBranchPointId)
      VesselSnapshot (full ProtoVessel — crew, resources, modules)
      ControllerInfo list, SegmentEvents (identity tracking)
      TerminalState, spawn tracking, SceneExitSituation
      AntennaSpecs (CommNet relay)
      Resource deltas, pre-launch resources, rewind save
      CrewEndStates, crew reservation
      RecordingGroups (UI grouping)
```

The Gloops trajectory data is the visual core. The Parsek metadata envelope is everything needed for timeline semantics, game state, and world presence.

### 9.2 Tree Topology

Parsek's `RecordingTree` is a DAG of Recording objects connected by `BranchPoint`s. Staging creates new Recordings (not segment groups or split points). The tree walker, optimizer, and chain walker all operate on this structure. Gloops knows nothing about it — it receives N trajectories in a flat list.

Debris recordings get `LoopSyncParentIdx` set to their parent's index, so the engine replays them in sync. This is the only tree-awareness Gloops needs, and it's expressed as a simple integer index, not a tree structure.

### 9.3 Policy Hookup

```
ParsekFlight (host)
  -> Builds TrajectoryPlaybackFlags[] from chain walker, spawn decisions, tree state
  -> Builds FrameContext from current flight state
  -> Calls GhostPlaybackEngine.UpdatePlayback()
  -> Engine fires lifecycle events
  -> ParsekPlaybackPolicy subscribes:
       PlaybackCompleted → spawn decision, resource application, camera management
       LoopRestarted → camera retarget
       OverlapExpired → cleanup
       CameraAction → FlightCamera manipulation
```

### 9.4 What Parsek Owns (Not Gloops)

- **RecordingTree / BranchPoint** — DAG structure, staging decomposition, merge tracking
- **GhostChainWalker / GhostChain** — chain ghost linking across trees
- **BackgroundRecorder** — multi-vessel recording sessions
- **RecordingOptimizer** — post-commit environment-boundary splitting
- **ParsekPlaybackPolicy** — spawn decisions, resource application, chain suppression
- **VesselSpawner** — real vessel spawning from snapshots
- **CommNet relay, GhostMapPresence** — world presence (tracking station, orbit lines)
- **Timeline, GameActions, CrewReservation** — career mode systems
- **RecordingStore, ParsekScenario** — persistence and save/load

---

## 10. File Format

### 10.1 The .gloop File

A `.gloop` file is a serialized trajectory — everything needed to play back a ghost. KSP ConfigNode syntax for ecosystem consistency.

```
GLOOP_HEADER
{
  formatVersion = 1
  createdBy = Gloops 1.0
  vesselName = Untitled Craft
  bodyName = Kerbin
  duration = 542.3
  meshSource = VESSEL_SNAPSHOT
}

TRAJECTORY
{
  TRACK_SECTION
  {
    environment = 0
    referenceFrame = 0
    startUT = 1000.0
    endUT = 1045.2
    FRAME { ut = 1000.0 lat = -0.0972 lon = -74.5577 alt = 75.2 ... }
    FRAME { ut = 1000.5 lat = -0.0971 lon = -74.5576 alt = 80.1 ... }
    ...
  }
  TRACK_SECTION
  {
    environment = 2
    referenceFrame = 2
    startUT = 1045.2
    endUT = 1542.3
    CHECKPOINT { startUT = 1045.2 endUT = 1542.3 inc = 28.5 ecc = 0.001 ... }
  }
}

PART_EVENTS
{
  EVENT { ut = 1003.2 pid = 100000 type = 5 pn = liquidEngine.v2 val = 1.0 midx = 0 }
  EVENT { ut = 1045.1 pid = 100000 type = 6 pn = liquidEngine.v2 val = 0.0 midx = 0 }
  ...
}

VESSEL_SNAPSHOT
{
  // ConfigNode from vessel.BackupVessel() — part tree, modules, mesh data
  ...
}
```

### 10.2 Relationship to Parsek's .prec

Parsek's `.prec` sidecar files contain Gloops trajectory data inline plus Parsek-specific metadata (resource deltas, segment events, etc.). The `.prec` format is a superset of `.gloop`.

**Embedding approach (recommended):** The `.prec` file embeds Gloops data using the same field names and structure as `.gloop`. Gloops never reads `.prec` files directly — Parsek extracts Gloops data and feeds it through the API. When Parsek exports a `.gloop` (for content pack creation or sharing), it strips its metadata and writes pure Gloops format.

### 10.3 Version Evolution

Additive format: new fields are added, old fields are never renamed or removed. Old `.gloop` files play at reduced fidelity in newer Gloops versions. The version field gates feature availability.

---

## 11. Extraction Plan

### 11.1 Files That Move to Gloops

These files have zero or minimal Parsek-specific references and form the engine core:

| File | Current state | Notes |
|---|---|---|
| `GhostPlaybackEngine.cs` | Zero Recording references | Ready to move |
| `GhostPlaybackEvents.cs` | Pure event types | Ready to move |
| `IPlaybackTrajectory.cs` | Interface definition | Ready to move |
| `IGhostPositioner.cs` | Interface definition | Ready to move |
| `GhostVisualBuilder.cs` | Builds GameObjects from snapshots | Minor: remove 4 lines of showcase heuristic checks |
| `GhostTypes.cs` | All ghost info types (EngineGhostInfo, RcsGhostInfo, etc.) | Ready to move |
| `GhostPlaybackState.cs` | Per-ghost render state, InterpolationResult | Ready to move |
| `GhostSoftCapManager.cs` | GhostPriority enum, cap logic | Ready to move |
| `RenderingZoneManager.cs` | RenderingZone enum, zone logic | Ready to move |
| `TrajectoryPoint.cs` | Position + game-state fields | Strip funds/science/reputation |
| `TrackSection.cs` | Environment, reference frame, frames | Ready to move |
| `OrbitSegment.cs` | Keplerian checkpoint | Ready to move |
| `PartEvent.cs` | 35 event types | Ready to move |
| `FlagEvent.cs` | Flag placement events | Ready to move |
| `SurfacePosition.cs` | Body/lat/lon/alt/rotation | Ready to move |
| `TerminalState.cs` | Terminal state enum | Ready to move |
| `TrajectoryMath.cs` | Interpolation, sampling, orbit search | Extract `RecordingStats`/`ComputeStats` to Parsek side |

### 11.2 Files That Need Pre-Extraction Splitting

| File | What moves to Gloops | What stays in Parsek |
|---|---|---|
| `GhostPlaybackLogic.cs` | Part event replay, FX application, zone rendering, warp policy, ghost info population, deployable/light/heat state | Spawn-at-recording-end decisions, chain suppression, tree navigation, watch mode target finding |
| `FlightRecorder.cs` | Trajectory sampling, part event capture, environment classification, track section management, snapshot refresh | Tree building, background vessel coordination, resource capture, rewind save, chain boundary handling |
| `TrajectoryMath.cs` | Interpolation, sampling decision, orbit math | `RecordingStats`, `ComputeStats` |
| `Recording.cs` | Extract `LoopTimeUnit` enum to shared location | Everything else (Parsek metadata envelope) |

### 11.3 Cross-Cutting Dependencies

| Dependency | Resolution |
|---|---|
| `ParsekLog` (used pervasively in engine code) | Extract as shared logging abstraction, or Gloops ships its own `GloopsLog` with same API shape |
| `FlightRecorder.EncodeEngineKey` (used in FX code, 7 call sites) | Extract to shared utility (pure function: `(pid, moduleIndex) -> ulong`) |
| `ConfigNode` (KSP type used everywhere) | KSP assembly reference — both mods need it regardless |

### 11.4 Files That Stay in Parsek

- `Recording.cs` — Parsek metadata envelope, implements Gloops interface
- `RecordingTree.cs`, `BranchPoint.cs` — tree/DAG topology
- `GhostChainWalker.cs`, `GhostChain.cs` — chain ghost logic
- `BackgroundRecorder.cs` — multi-vessel recording
- `RecordingOptimizer.cs` — post-commit splitting/merging
- `ParsekPlaybackPolicy.cs` — spawn/resource/camera policy
- `VesselSpawner.cs` — real vessel spawning
- `RecordingStore.cs`, `ParsekScenario.cs` — persistence
- `ChainSegmentManager.cs` — chain segment state
- All UI, timeline, game actions, crew, CommNet code

### 11.5 Extraction Sequence

1. **Pre-extraction refactors** — Split `GhostPlaybackLogic.cs` into engine-layer and policy-layer. Extract recorder code from `FlightRecorder.cs`. Move `LoopTimeUnit` to own file. Extract `EncodeEngineKey` to utility. Create `GloopsLog` abstraction.
2. **Create Gloops assembly** — New .csproj, move files from Section 11.1.
3. **Define `.gloop` format** — Serialization/deserialization for trajectory data.
4. **Implement content pack loader** — Manifest parser, spawn condition manager, validation.
5. **Build standalone UI** — Loop manager, pack toggles, settings. Suppressed when consumer registered.
6. **Refactor Parsek to depend on Gloops** — `Recording` implements Gloops interface, `ParsekFlight` creates Gloops engine, policy subscribes to events.
7. **Verify** — Parsek + Gloops produces identical behavior to pre-extraction Parsek.
8. **Verify** — Gloops standalone works without Parsek.

---

## 12. Open Questions

1. **ConfigNode vs compact format for trajectory frames.** ConfigNode is ecosystem-consistent but verbose. Trajectory data is the bulk of file size. Compact numeric encoding (one line per frame, tab-separated) is already used in `.prec` files. *(Decision needed: format design stage)*

2. **Custom mesh loading.** Unity AssetBundles, raw `.mu` files, or OBJ import? Balancing capability vs. authoring friction for content creators. *(Decision needed: content pack implementation)*

3. **EVA recording in Gloops standalone.** EVA kerbals are single-mesh, no staging. Useful for content packs (kerbal walking around KSC). The recorder should handle them as a regular vessel. *(Likely: yes, no special casing needed)*

4. **Logging integration.** Shared abstraction or independent logging? If shared, versioning implications. If independent, duplicate log infrastructure. *(Decision needed: extraction stage)*

5. **Multiple consumers.** Can multiple mods use Gloops simultaneously? Affects UI suppression and API design. *(Decision needed: API design)*

---

## 13. Deferred Items

| Item | Reason |
|---|---|
| Full ghost visual treatment (transparency, outlines) | Labels provide minimum viable distinction |
| LOD system for ghost meshes | Performance optimization — profile first |
| Particle system pooling for FX | Performance optimization |
| In-game content pack authoring tool | Recorder covers basic authoring |
| Gzip compression for .gloop files | File size — measure first |

---

*This document describes Gloops as an extraction of existing Parsek code. The interfaces, data structures, and engine boundaries documented here already exist in the codebase. The extraction itself will not begin until current Parsek feature work is complete, but the code is already structured for a clean move.*
