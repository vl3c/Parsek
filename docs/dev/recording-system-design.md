# Parsek Recording System — Design Document v2

*Design specification for the next-generation recording, playback, and multi-vessel tracking system.*

*Status: Design complete, ready for implementation planning.*

---

## 1. Purpose and Scope

This document specifies the vessel recording system architecture for Parsek. It covers:

- The recording tree model (DAG structure) and vessel identity
- Segment boundary rule (only structural separation creates segments)
- Within-segment events (SegmentEvents) for controller changes, part destruction, etc.
- Crash breakup coalescing
- Multi-vessel recording sessions with focus switching
- Segment taxonomy (environment, reference frame, rendering zones)
- Background vessel recording in the physics bubble
- Orbital checkpoint system for on-rails vessels
- Looped recording playback anchored to real vessels
- Multi-vessel merge to a single committed timeline
- Distance-based rendering zones
- Rewind and fast-forward with ghost playback
- Ghost implementation constraints
- Recording file format
- Performance budget
- Error recovery

---

## 2. Vessel Identity Model

### 2.1 Why KSP's Vessel ID Is Insufficient

KSP assigns each vessel a `persistentId`. This ID is unstable across vessel lifecycle events: when a vessel decouples, one side inherits the original ID (the side with the root part) and the other gets a new ID. When two vessels dock, one ID survives and the other is destroyed. The surviving ID depends on which vessel was active at docking time.

This means a vessel that launches as one craft, drops stages, undocks a lander, docks to a station, and later gets recycled will have its KSP vessel ID change multiple times in ways that don't correspond to what the player considers "the same thing."

### 2.2 Controller-Based Identity

Parsek defines vessel identity around **command authority** — the presence of a controller part that gives a vessel agency.

**Controller parts** (in priority order):

- Crewed command pod (Mk1, Mk1-3, etc.)
- Occupied external command seat
- Probe core (with or without electric charge)
- Kerbal on EVA

**A vessel segment** is the continuous history of a command-capable assembly of parts, from the moment it gains command authority to the moment it loses it permanently.

**Gains command authority:** A controller part becomes the root of an independent vessel — either at launch, at a split event where this side has a controller, or via construction (a kerbal places and activates a probe core).

**Loses command authority permanently:** Recovery, destruction, recycling, or removal of all controller parts with no replacement.

### 2.3 Debris

An assembly of parts with no controller is debris. Parsek does not record debris trajectories. KSP's own persistence handles debris positions.

If a controlled vessel acquires debris (e.g., claw grab of a fuel tank), that's a merge event where one parent is a controller-based vessel and the other is inert. The recording notes the acquisition; the debris's prior existence is irrelevant to the recording.

If a vessel loses all controllers (crew removed, probe core out of EC with no restoration), its active segment ends and it becomes debris. If a controller is later restored (another vessel docks and provides control, a kerbal boards it, EC is restored to a probe core), a new active segment begins.

---

## 3. The Recording Tree (DAG)

### 3.1 Structure

A recording tree is a **directed acyclic graph (DAG)** of vessel segments connected by lifecycle events. It is not a simple tree because merge events (docking, boarding) join branches back together.

### 3.2 Primitives

**VesselSegment**: A continuous stretch of time during which a physically connected assembly of parts exists as one vessel with at least one controller part present (whether or not that controller is currently functional).

```
VesselSegment
  id:                unique segment identifier
  controllers:       list of controller parts at segment start
  isDebris:          bool (true if no controllers — minimal recording only)
  startEvent:        TreeEvent that created this segment
  endEvent:          TreeEvent that ended this segment (null if ongoing)
  tracks:            list of TrackSection (see Section 5 - Segment Taxonomy)
  partEvents:        recorded discrete part events (see Section 6.3)
  segmentEvents:     recorded within-segment state changes (see Section 3.7)
  resourceSnapshot:  {start: {resourceName: amount}, end: {resourceName: amount}}
  origin:            LaunchOrigin (only for root segments — body, lat, lon, alt, site name)
```

**TreeEvent**: A point where the DAG branches or merges. These ONLY occur when the vessel physically separates into independent assemblies or merges with another assembly.

```
TreeEvent
  type:              LAUNCH | SPLIT | BREAKUP | MERGE | TERMINAL
  ut:                universal time
  parentSegments:    list of segments that ended here (empty for LAUNCH)
  childSegments:     list of segments that began here (empty for TERMINAL)
  metadata:          type-specific data (see below)
  resourceDelta:     any funds/science/rep changes at this moment

  SPLIT metadata:
    cause:           DECOUPLE | UNDOCK | EVA
    decouplerPartId: the part that triggered separation

  BREAKUP metadata:
    cause:           CRASH | OVERHEAT | STRUCTURAL_FAILURE
    duration:        time window of the breakup (typically < 1 second)
    controlledChildren: list of child segments that have controllers
    debrisCount:     number of debris fragments (not individually tracked)
    coalesceWindow:  time threshold used for grouping rapid splits (default 0.5s)

  MERGE metadata:
    cause:           DOCK | BOARD | CONSTRUCT | CLAW
    targetVesselId:  if merging with a pre-existing persistent vessel,
                     its persistentId (links recording tree to game state)

  TERMINAL metadata:
    cause:           RECOVERED | DESTROYED | RECYCLED | DESPAWNED
```

**SegmentEvent**: A significant state change within a continuing segment that does NOT create new DAG branches. See Section 3.7 for full specification.

**RecordingTree**: The complete DAG for a mission.

```
RecordingTree
  id:             unique identifier for this mission/tree
  rootSegment:    the first segment (launch)
  segments:       list of all VesselSegments
  events:         list of all TreeEvents connecting segments
```

### 3.3 The Segment Boundary Rule

**CRITICAL: Only physical structural separation creates new segments. Controller changes never create segment boundaries.**

A segment boundary (TreeEvent) occurs ONLY when:
- The vessel physically splits into two or more independent assemblies (SPLIT, BREAKUP)
- The vessel physically merges with another assembly (MERGE)
- The vessel ceases to exist (TERMINAL)
- The vessel is created (LAUNCH)

A segment boundary does NOT occur when:
- A controller part is destroyed but the vessel remains one connected assembly
- A controller part loses power, hibernates, or becomes non-functional
- A controller part regains power or is repaired
- The player uses "Control From Here" to change the active control point to a different part
- KSP internally reassigns the root part (e.g., after a part is destroyed)
- Crew transfers between parts within the same vessel
- The primary/root controller changes to a different part for any reason
- Parts are destroyed, added, or removed without splitting the vessel into independent assemblies

These non-structural changes are recorded as SegmentEvents (Section 3.7) within the continuing segment.

**Rationale:** A player who launches a vessel, crashes it partially (destroying one probe core while another survives), and recovers the wreckage should see ONE recording from launch to recovery — not multiple disconnected segments that split at every controller change. The vessel's identity persists as long as it's one connected physical assembly with at least one controller part present.

### 3.4 Identity Persistence Rule

A vessel segment persists as long as at least one controller part **physically exists** on the vessel, regardless of whether that controller is currently **functional**.

- A probe core with no electric charge is still a controller (it physically exists).
- A command pod with no crew is still a controller (the part exists).
- A hibernating probe core is still a controller.
- A damaged but not destroyed controller is still a controller.

Only physical destruction or removal of ALL controller parts ends the segment. If the last controller is destroyed and the vessel has no other controllers, the segment transitions to debris status (isDebris = true) with minimal recording. If a new controller is later added (KIS attachment, docking with a controller-bearing vessel), a new segment begins.

### 3.5 Split Events

When a vessel physically separates into independent assemblies (staging, undocking, EVA), the parent segment ends and two or more child segments begin. Each child inherits whatever controller parts end up on its side. Children without any controller are tagged as debris and receive minimal recording (position only until destruction/despawn).

### 3.6 Breakup Events (Crash Coalescing)

Crashes, overheating, and structural failures can produce many fragments across multiple physics frames. Rather than recording 20 individual SPLIT events in rapid succession, Parsek coalesces all separation events within a short time window (default 0.5 seconds) into a single BREAKUP tree event.

The BREAKUP event records:
- The parent segment (the vessel that broke up)
- All child segments that have controllers (these get proper segments with trajectory recording)
- A count of debris fragments (not individually tracked)

**Detection logic:** When a SPLIT event fires, start a coalescing timer (0.5s). If additional SPLIT events fire on the same parent vessel or its immediate fragments within the window, group them all into one BREAKUP. When the timer expires with no new splits, emit the BREAKUP event.

**Example — Mun crash with partial survival:**

```
Vessel with probe core A (front) and probe core B (rear) crashes.
Multiple parts break off across 0.3 seconds.

Instead of:
  SPLIT at T+0.00 → [front half + A] + [debris]
  SPLIT at T+0.05 → [front half + A] + [more debris]  
  SPLIT at T+0.12 → [rear half + B] separates from [middle debris]
  SPLIT at T+0.20 → [front section breaks] → [A survives alone] + [debris]
  ... 12 more splits ...

Parsek records:
  BREAKUP at T+0.00 (duration: 0.30s)
    parent: [original vessel]
    controlledChildren:
      - [assembly containing probe core A] → new segment
      - [assembly containing probe core B] → new segment
    debrisCount: 14
```

### 3.7 Within-Segment Events (SegmentEvents)

SegmentEvents record significant state changes that do NOT create DAG branches. They are annotations on a continuing segment, important for crew tracking, ghost visual fidelity, and vessel state history.

```
SegmentEvent
  type:      CONTROLLER_CHANGE | CONTROLLER_DISABLED | CONTROLLER_ENABLED |
             CREW_LOST | CREW_TRANSFER | PART_DESTROYED | PART_REMOVED | PART_ADDED
  ut:        universal time
  details:   type-specific data

  CONTROLLER_CHANGE:
    lostControllers:      [{type, partName, crew}]
    gainedControllers:    [{type, partName}]
    remainingControllers: [{type, partName, crew}]
    // Fired when a controller part is destroyed, added, or changes type.
    // The segment continues — this is NOT a split.

  CONTROLLER_DISABLED:
    controller:    {type, partName}
    reason:        NO_ELECTRIC_CHARGE | HIBERNATION | DAMAGED
    // Controller part still physically exists but is non-functional.
    // Segment continues. Identity persists (Section 3.4).

  CONTROLLER_ENABLED:
    controller:    {type, partName}
    reason:        ELECTRIC_CHARGE_RESTORED | REPAIR | DOCKED_POWER | WAKE_FROM_HIBERNATION
    // Controller part regains functionality.

  CREW_LOST:
    kerbalName:    string
    cause:         DESTROYED | MISSING_IN_ACTION (other causes deferred to crew module)
    // Crew member died. Relevant for crew tracking module.

  CREW_TRANSFER:
    kerbalName:    string
    fromPart:      string
    toPart:        string
    // Crew moved between parts within the same vessel (not via EVA).
    // EVA transfers are handled as SPLIT/MERGE in the DAG.

  PART_DESTROYED:
    partName:      string
    partId:        int
    wasController: bool
    // Part destroyed without splitting the vessel.
    // Ghost stops rendering this part mesh after this UT.

  PART_REMOVED:
    partName:      string
    partId:        int
    wasController: bool
    // Part removed via KIS/KAS without splitting the vessel.
    // Ghost stops rendering this part mesh after this UT.

  PART_ADDED:
    partName:      string
    // Part added via KIS/KAS without creating a new vessel.
    // Ghost starts rendering this part mesh after this UT.
```

**Interaction with ghost visuals:** PART_DESTROYED, PART_REMOVED, and PART_ADDED events directly affect ghost rendering. When playing back a segment, the ghost mesh must add/remove part meshes at the correct UTs so the visual matches what the player originally saw. For example, a probe core that explodes during reentry should disappear from the ghost at the recorded UT, even though the segment continues (another controller survived).

### 3.8 Merge Events

When two vessels merge (docking, boarding, claw attachment), both parent segments end and one child segment begins. The child inherits all controllers from both parents.

**Merging with a pre-existing persistent vessel** (e.g., docking to a station from a previous recording): The current recording's segment ends with a merge event. The `targetVesselId` field links the recording tree to the persistent game state. The combined vessel is a new segment in the current recording's tree. The historical recording tree of the station is NOT modified — it is immutable.

### 3.9 Example DAG

A crewed lander with a probe-controlled transfer stage, launching, undocking at Mun, landing, EVA, re-docking, and returning:

```
LAUNCH (capsule + transfer + booster)
  │
  ├─ SPLIT: staging
  │    ├─ [booster] ── no controller ── DEBRIS
  │    └─ [capsule + transfer] ── controller: capsule crew
  │         │
  │         ├─ SPLIT: undock
  │         │    ├─ [capsule/lander] ── controller: crew
  │         │    │     ├─ SPLIT: EVA
  │         │    │     │    ├─ [kerbal] ── controller: self
  │         │    │     │    │     └─ MERGE: board
  │         │    │     │    └─ [lander] ── controller: remaining crew
  │         │    │     │         └─ MERGE: board
  │         │    │     └─ [reunified lander]
  │         │    │          └─ MERGE: dock with transfer
  │         │    └─ [transfer] ── controller: probe core
  │         │         └─ MERGE: dock (lander returns)
  │         │
  │         └─ [reunified vessel]
  │              ├─ SPLIT: decouple for reentry
  │              │    ├─ [capsule] ── RECOVERED
  │              │    └─ [transfer] ── DESTROYED
```

### 3.10 Example: Crash with Controller Survival (SegmentEvents, not splits)

A vessel with two probe cores (A at front, B at rear) crashes into the Mun. Probe core A is destroyed, but B survives. The vessel breaks into fragments.

**WRONG (old behavior — splitting on controller change):**
```
Vessel segment ends at crash → SPLIT → new segment for B → confusing multi-segment recording
```

**CORRECT (new behavior — structural separation only):**
```
LAUNCH (vessel with probe cores A and B)
  │
  ONE CONTINUOUS SEGMENT (recording the full flight)
  │  ... ascent, transfer, Mun approach ...
  │  ... powered descent ...
  │  ... impact ...
  │
  │  SegmentEvent: PART_DESTROYED (solar panel) at UT=X
  │  SegmentEvent: PART_DESTROYED (engine) at UT=X+0.05
  │  SegmentEvent: CONTROLLER_CHANGE at UT=X+0.10
  │     lostControllers: [probe core A — destroyed]
  │     remainingControllers: [probe core B]
  │
  │  IF vessel stays physically connected (one assembly):
  │     Segment continues. Probe core B is now primary.
  │     ... comes to rest on surface ...
  │     environment: SURFACE_STATIONARY
  │
  │  IF vessel physically breaks apart (structural failure):
  │     BREAKUP at UT=X+0.10 (coalesced over 0.3s window)
  │       controlledChildren: [assembly with probe core B]
  │       debrisCount: 8
  │       │
  │       └─ [assembly with probe core B] → new segment, lands, survives
```

The key difference: if the vessel stays in one piece, the player sees ONE recording from launch to rest. Controller A dying is a SegmentEvent annotation, not a segment boundary. Only if the assembly physically breaks into separate pieces does a BREAKUP tree event occur.

---

## 4. Multi-Vessel Recording Sessions

### 4.1 The Problem

KSP gameplay frequently involves multiple vessels that are simultaneously relevant: rendezvous, base construction, orbital assembly. The player switches control between vessels, and the recording must capture all of them at appropriate fidelity so that playback looks correct regardless of camera position.

### 4.2 Recording Session Definition

A **recording session** is scoped to a single RecordingTree. The session starts when recording starts or joins a tree, and ends on revert/commit. There is no separate `RecordingSession` object — the RecordingTree already tracks all vessels and their relationships.

During a session, the recorder maintains:

1. **The focus track**: Full recording (source=Active) of whichever vessel the player is currently controlling.
2. **Background physics tracks**: Reduced-rate trajectory sampling (source=Background) + all discrete part events for all other vessels within the physics bubble (~2.3km). Sample rate varies by proximity: <200m → 5Hz, 200m-1km → 2Hz, 1km-2.3km → 0.5Hz.
3. **Orbital checkpoints**: Keplerian element snapshots (source=Checkpoint) captured automatically at on-rails transitions, time warp boundaries, and scene changes.

**Implementation note (decided during Phase 2 planning):** The `RecordingSession` class from the original design is NOT implemented. The RecordingTree serves as the session scope. Focus history is derived from the tree's `ActiveRecordingId` changes over time — it is a transient input to the merge algorithm, not a persisted data structure. Scene-change gaps are handled implicitly: when the player leaves flight, vessels go on rails → orbital checkpoints are captured → when the player returns, new checkpoints are captured. The absence of trajectory points between checkpoints IS the gap, and playback propagates from checkpoints during these intervals.

### 4.3 Data Model

Each vessel's data lives in a `Recording` within the `RecordingTree.Recordings` dict. Each Recording's `TrackSections` list contains typed trajectory chunks tagged with a `TrackSectionSource` (Active, Background, or Checkpoint) for diagnostics. The merge algorithm produces a single merged Recording per vessel with non-overlapping TrackSections selected by highest-fidelity-wins.

```
RecordingTree (serves as session scope)
  Recordings:         dict of {recordingId: Recording}
  BranchPoints:       list of BranchPoint (DAG events)
  ActiveRecordingId:  currently focused recording (changes on focus switch)
  BackgroundMap:      dict of {vesselPid: recordingId} for background vessels

Recording (per vessel)
  TrackSections:      list of TrackSection (each tagged with source + environment + reference frame)
  SegmentEvents:      list of SegmentEvent (within-segment state changes)
  Points:             flat list of TrajectoryPoint (v5 backward compat, dual-written)
  OrbitSegments:      flat list of OrbitSegment (v5 backward compat)
  PartEvents:         list of PartEvent (discrete visual events)

TrackSection
  environment:        ATMOSPHERIC | EXO_PROPULSIVE | EXO_BALLISTIC | SURFACE_MOBILE | SURFACE_STATIONARY
  referenceFrame:     ABSOLUTE | RELATIVE | ORBITAL_CHECKPOINT
  source:             ACTIVE | BACKGROUND | CHECKPOINT (provenance for diagnostics)
  frames:             list of TrajectoryPoint (for ABSOLUTE/RELATIVE)
  checkpoints:        list of OrbitSegment (for ORBITAL_CHECKPOINT)
  boundaryDiscontinuityMeters:  position gap at section start vs previous section end
```

### 4.4 Focus Switching Behavior

When the player switches focus from vessel A to vessel B:

1. Vessel A's active recording transitions to background (or orbital checkpoint if going on rails).
2. Vessel B's background recording transitions to active.
3. The tree's `ActiveRecordingId` changes from A's recording to B's recording.

The focus history is implicit in the sequence of `ActiveRecordingId` changes on the tree — no explicit focus log is stored. The merge algorithm reconstructs which vessel was active at each UT by scanning recording metadata.

### 4.5 Scene Changes (KSC, Tracking Station, Buildings)

When the player leaves the flight scene:

1. ALL vessels go on rails → `onVesselGoOnRails` fires → orbital/surface checkpoints captured automatically.
2. `CheckpointAllVessels` fires for all background vessels at the scene-change boundary.
3. No explicit "gap" is recorded — the absence of trajectory points between checkpoints IS the gap.
4. When the player returns to flight, vessels come off rails → new checkpoints captured → physics sampling resumes.

During playback of a gap interval: ghosts propagate from orbital checkpoints or hold surface position. The checkpoint system handles this identically to any other on-rails interval — scene changes are not a special case.

**Warning:** If a vessel was mid-flight (e.g., descending to a surface) when the player left the scene, KSP's on-rails handling may destroy or misplace it. Parsek records whatever outcome KSP produces — this is a game-behavior consequence, not a Parsek bug.

### 4.6 Time Warp Boundaries

Every time the player enters or exits time warp, Parsek snapshots the orbital elements of all vessels involved in the current recording session. This creates clean reference points for orbital propagation during playback and prevents drift accumulation over long time periods.

---

## 5. Segment Taxonomy

Every vessel segment carries two classification axes that are independent of each other. A third axis (rendering zone) is a runtime playback decision, not a recording-time property.

### 5.1 Axis 1: Environment

Determined automatically at recording time by vessel state. Changes trigger segment boundaries.

| Environment | Trigger | Sample Rate | Notes |
|---|---|---|---|
| `ATMOSPHERIC` | Vessel below atmosphere ceiling of current body | High (~50 Hz physics tick) | Trajectory is chaotic (drag, lift, heating). No analytical reconstruction possible. KSP blocks time warp above 4x. |
| `EXO_PROPULSIVE` | Above atmosphere (or no atmosphere) AND any engine producing thrust | High (~50 Hz physics tick) | Thrust is player-controlled, trajectory not Keplerian. |
| `EXO_BALLISTIC` | Above atmosphere AND no engines producing thrust | Low (orbital checkpoints) | Trajectory is Keplerian, analytically propagable. Checkpoint per orbit or per ~10-50 Kerbin days for long-period orbits. |
| `SURFACE_MOBILE` | Landed/splashed AND ground speed > 0.1 m/s | Medium (~5-10 Hz) | Rover driving, vessel repositioning. |
| `SURFACE_STATIONARY` | Landed/splashed AND ground speed < 0.1 m/s for >3 seconds | Minimal (~0.1 Hz or checkpoint only) | Parked vessel, base module. Body-fixed coordinates. |

**Transition detection:**

- `ATMOSPHERIC` ↔ `EXO_*`: Monitor `vessel.altitude` vs `body.atmosphere`. Boundary is the atmosphere ceiling.
- `EXO_PROPULSIVE` ↔ `EXO_BALLISTIC`: Monitor whether any engine module on the vessel is currently producing thrust (nonzero `currentThrust`). Hysteresis of ~1 second to avoid rapid toggling during pulsed burns.
- `SURFACE_*` ↔ `EXO_*` / `ATMOSPHERIC`: Monitor `vessel.situation` (Landed, Splashed, Flying, Sub_Orbital, Orbiting, Escaping).
- `SURFACE_MOBILE` ↔ `SURFACE_STATIONARY`: Monitor `vessel.srfSpeed`. Threshold 0.1 m/s with 3-second debounce.

### 5.2 Axis 2: Reference Frame

Determined at recording time by context. Changes trigger segment boundaries.

| Reference Frame | When Used | Position Data | Playback |
|---|---|---|---|
| `ABSOLUTE` | Default for active flight | Body-fixed coordinates (body, lat, lon, alt, attitude) with universal timestamps | Ghost placed at recorded coordinates. Pinned to specific times and places. |
| `RELATIVE` | Within physics bubble of a pre-existing persistent vessel | Offset from anchor vessel (dx, dy, dz in anchor's reference frame) with timestamps | Ghost position computed from anchor vessel's current actual position. Tracks anchor precisely. |
| `ORBITAL_CHECKPOINT` | On-rails coasting, no physics | Keplerian elements (body, sma, ecc, inc, lan, argPe, meanAnomaly) at discrete timestamps | Ghost position computed by propagating Kepler orbit to current UT. |

**Automatic transitions:**

- Switch to `RELATIVE` when the focused vessel enters the physics bubble (<2.3km) of a pre-existing persistent vessel. The anchor vessel ID is recorded.
- Switch from `RELATIVE` back to `ABSOLUTE` when leaving the physics bubble of the anchor vessel.
- Switch to `ORBITAL_CHECKPOINT` when the vessel goes on rails (scene change, time warp, or leaving physics range of all loaded vessels).
- Switch from `ORBITAL_CHECKPOINT` to `ABSOLUTE` or `RELATIVE` when the vessel loads into physics.

**Docking approach rule:** Any approach within docking range (~200m) of a pre-existing vessel should use `RELATIVE` reference frame to ensure positional accuracy for visually correct docking playback.

### 5.3 Track Sections

A VesselSegment's trajectory is stored as a sequence of TrackSections, each with a single environment + reference frame combination:

```
TrackSection
  environment:      ATMOSPHERIC | EXO_PROPULSIVE | EXO_BALLISTIC |
                    SURFACE_MOBILE | SURFACE_STATIONARY
  referenceFrame:   ABSOLUTE | RELATIVE | ORBITAL_CHECKPOINT
  startUT:          section start time
  endUT:            section end time
  anchorVesselId:   set only for RELATIVE frame, null otherwise
  
  // Data varies by type:
  trajectoryFrames: [{ut, pos, rot}]           // for ABSOLUTE and RELATIVE
  orbitalSnapshots: [{ut, body, elements...}]  // for ORBITAL_CHECKPOINT
  
  // Metadata:
  sampleRateHz:     actual recording sample rate
  isFromBackground: bool (true if this section came from background recording)
```

### 5.4 Combined Segment Example

A cargo delivery to a Mun base:

```
Recording: "Cargo Run Alpha"

Segment 1: [ATMOSPHERIC + ABSOLUTE]
  Kerbin launch through atmosphere. ~50 Hz sampling.
  
Segment 2: [EXO_PROPULSIVE + ABSOLUTE]
  Gravity turn completion and orbital insertion burn.
  
Segment 3: [EXO_BALLISTIC + ORBITAL_CHECKPOINT]  
  Kerbin parking orbit coast. Checkpoint once per orbit.
  
Segment 4: [EXO_PROPULSIVE + ABSOLUTE]
  Transfer burn to Mun.
  
Segment 5: [EXO_BALLISTIC + ORBITAL_CHECKPOINT]
  Transfer coast. Checkpoints every ~10 Kerbin days.
  
Segment 6: [EXO_PROPULSIVE + ABSOLUTE]
  Munar orbit insertion and deorbit burn.
  
Segment 7: [EXO_PROPULSIVE + ABSOLUTE]
  Powered descent (Mun has no atmosphere). ~50 Hz sampling.
  
Segment 8: [EXO_PROPULSIVE + RELATIVE to Mun-Base]
  Final approach within physics bubble of base. Relative frame.
  
Segment 9: [SURFACE_STATIONARY + RELATIVE to Mun-Base]
  Landed near base. Minimal sampling.
```

---

## 6. Background Vessel Recording

### 6.1 Principle

When the player controls one vessel, all other vessels within the physics bubble (~2.3km) are actively simulated by KSP's physics engine. Parsek records these background vessels to ensure playback looks correct regardless of which vessel the camera follows.

### 6.2 Background Trajectory Recording

Background vessels are sampled at reduced rate compared to the focused vessel. The rate depends on proximity and relative velocity:

| Distance to focused vessel | Sample Rate |
|---|---|
| < 200m (docking range) | ~5 Hz |
| 200m - 1km | ~2 Hz |
| 1km - 2.3km (physics edge) | ~0.5 Hz |

Position is recorded in the same reference frame as the focused vessel's current segment (ABSOLUTE or RELATIVE). If the focused vessel is using RELATIVE frame anchored to vessel X, the background vessels' positions are also recorded relative to vessel X for consistency.

### 6.3 Background Part Event Recording

**All discrete (visual) part events are recorded for ALL vessels in the physics bubble**, not just the focused vessel. This ensures recordings look identical regardless of camera position during playback.

Events captured for all physics-bubble vessels:

- Engine ignition / shutdown
- Staging
- Decoupling / undocking / docking
- Parachute deploy / cut
- Landing gear / legs deploy / retract
- Solar panels extend / retract
- Antennas extend / retract
- Cargo bays open / close
- Fairings jettison
- Lights on / off
- RCS toggle
- EVA start / end
- Inventory deployables

Events captured ONLY for the focused vessel:

- Engine gimbal angles (continuous)
- Control surface deflections (continuous)
- Throttle percentage (continuous)
- Any other high-frequency animation state

Implementation: KSP fires GameEvents globally for all loaded vessels. Parsek subscribes to these events and routes each to the correct vessel's track, tagged with the vessel ID and UT.

### 6.4 Structural Events for All Physics-Bubble Vessels

Events that affect the DAG structure (staging, decoupling, docking, undocking) MUST be captured for all vessels in the physics bubble, not just the focused vessel. A background vessel that stages while the player is focused elsewhere must still produce a SPLIT event in the recording tree, or the DAG will be incorrect.

**Implementation note (decided during Phase 2 planning):** Background vessel splits create proper tree structure — BranchPoint + child recordings for ALL new vessels from intentional separations, including spent stages and boosters (not just controlled vessels). This enables cinematic booster-separation ghost playback. Debris children from intentional splits (staging, fairing jettison) get a TTL: recording stops after 30 seconds, or on crash/destruction, or when leaving the physics bubble. This captures the visually interesting separation moment without storing long-lived debris trajectories. Rapid-fire crash fragments are still coalesced into BREAKUP events via the CrashCoalescer (Phase 1) and not individually tracked.

---

## 7. Orbital Checkpoint System

### 7.1 Purpose

For vessels that are on rails (outside the physics bubble, or during time warp / scene changes), full trajectory recording is impossible and unnecessary. Instead, Parsek records Keplerian orbital elements at discrete points and propagates analytically between them.

### 7.2 Checkpoint Data

```
OrbitalCheckpoint
  ut:           universal time of snapshot
  body:         reference body name
  sma:          semi-major axis
  ecc:          eccentricity
  inc:          inclination
  lan:          longitude of ascending node
  argPe:        argument of periapsis
  meanAnomaly:  mean anomaly at epoch
  epoch:        epoch time (usually same as ut)
```

For surface vessels, an equivalent surface checkpoint:

```
SurfaceCheckpoint
  ut:           universal time of snapshot
  body:         reference body name
  latitude:     double
  longitude:    double
  altitude:     double (above terrain)
  heading:      vessel heading (degrees)
```

### 7.3 When Checkpoints Are Captured

Automatic checkpoint triggers:

- **Session start**: All on-rails vessels involved in the session.
- **Focus switch**: The vessel being switched away from, if it's going on rails.
- **Time warp enter/exit**: All vessels in the session.
- **Scene change** (to KSC, Tracking Station, etc.): All vessels in the session.
- **SOI transition**: The vessel changing reference body.
- **Periodic safety net**: Every 3-5 orbits for orbiting vessels, or every 10-50 Kerbin days for long-period transfers.

### 7.4 Playback from Checkpoints

During playback, the ghost's position between checkpoints is computed by Keplerian propagation from the nearest prior checkpoint. For circular orbits, one checkpoint is sufficient for arbitrary duration. For elliptical or transfer orbits, checkpoints at key moments (burn start, burn end, SOI transition) are needed.

If a ghost's propagated orbit intersects terrain during a gap (e.g., a vessel on a suborbital trajectory when the player left the scene), the ghost follows the orbit into the ground. This accurately represents what KSP computed — the outcome during the gap is the game's responsibility.

---

## 8. Distance-Based Rendering Zones

### 8.1 The Floating Point Problem

KSP uses Unity's single-precision float coordinate system with the scene origin pinned to the active vessel. At large distances, positional precision degrades:

- At 100km: ~0.01m precision (acceptable)
- At 500km: ~0.05m precision (visible jitter on ghosts)
- At 1000km+: severe jitter, unusable

### 8.2 Zone Definitions

Distance is computed from the **player's currently controlled vessel** (scene origin) to each ghost.

| Zone | Range | Recording | Playback | Looped Ghosts |
|---|---|---|---|---|
| Zone 1: Physics Bubble | 0 – 2.3 km | Full trajectory + all discrete part events for all vessels | Full mesh, part event replay, relative segments active | Spawned, full fidelity |
| Zone 2: Visual Range | 2.3 – 120 km | Orbital checkpoints only | Ghost mesh rendered, orbital propagation, no part events | Spawned if < 50km, simplified rendering |
| Zone 3: Beyond Visual | 120 km+ | Orbital checkpoints only | No ghost rendered, position tracked for map view only | Not rendered, loop cycle tracked internally |

### 8.3 Ghost Rendering Rules

- Ghosts crossing from Zone 2 → Zone 3 (outward): mesh disappears. If camera was following the ghost, camera returns to player's controlled vessel.
- Ghosts crossing from Zone 3 → Zone 2 (inward): mesh appears with short fade-in (~2 seconds).
- Ghosts crossing from Zone 2 → Zone 1 (inward): switch from orbital propagation to physics-bubble trajectory data. Part events begin replaying.
- Ghosts crossing from Zone 1 → Zone 2 (outward): switch from trajectory data to orbital propagation. Part events stop.

### 8.4 Looped Ghost Spawn Rules

Looped recordings have tighter spawn thresholds than full-timeline recordings because there may be many active loops and their individual visual contribution at distance is low.

```
Anchor vessel in Zone 1 (< 2.3km):   Spawn with full fidelity
Anchor vessel in Zone 2 (< 50km):    Spawn with simplified rendering (no part events)
Anchor vessel in Zone 2 (50-120km):  Do not spawn
Anchor vessel in Zone 3 (> 120km):   Do not spawn
```

Full-timeline recordings render out to the full 120km Zone 2 boundary, as the player may deliberately be watching historical events from a distance.

### 8.5 Map View

In map view, KSP uses double-precision coordinates. Parsek can display ghost positions as icons on the map regardless of distance, without jitter. Ghost orbital paths can be drawn on the map for the full duration of their recording.

---

## 9. Looped Recordings Anchored to Real Vessels

### 9.1 Principle

Looped recording segments are anchored to a real persistent vessel (station, base, etc.). The ghost only exists when that real vessel is loaded. Parsek never modifies the real vessel's state — it only reads the vessel's position to compute ghost placement.

### 9.2 Lifecycle

```
Real vessel loads into physics range (or into visual range for simplified loops)
  → Parsek checks: any looped recordings anchored to this vessel?
  → If yes: compute where each loop is in its cycle based on elapsed UT
  → Spawn ghost(s) at the correct loop phase
  → Ghosts update position relative to real vessel's actual current position

Real vessel unloads
  → Destroy all ghosts anchored to it
  → Store each ghost's current loop phase for resumption

Real vessel loads again later
  → Recompute loop phase from elapsed UT
  → Respawn ghosts at correct phase (no reset to loop start)
```

### 9.3 Segment Looping

The recordings manager displays segment breakdown. The player marks which segments to loop:

- Atmospheric segments (dramatic launch/landing visuals) — natural loop candidates.
- Relative approach segments (docking with a station) — natural loop candidates.
- EXO_PROPULSIVE segments (burns) — useful for cinematic loops.
- EXO_BALLISTIC segments (orbital coasting) — rarely worth looping visually.
- SURFACE_STATIONARY — not useful to loop.

Multiple segments can be looped together as a sequence (e.g., powered descent + approach = complete arrival visual). The loop restarts from the first selected segment after the last selected segment completes.

### 9.4 Looped Segments Are Always Relative

When a looped segment plays back, it uses the real anchor vessel's current position, not the historical position from recording time. This means:

- Zero positional drift between ghost and anchor, regardless of how much time has passed.
- If the anchor vessel has moved (different orbit, repositioned base), the ghost adapts automatically.
- If the anchor vessel no longer exists (destroyed, recovered), the loop is broken — Parsek skips it and optionally notifies the player.

### 9.5 Validation on Spawn

When spawning a looped ghost, Parsek performs a basic validation:

- Does the anchor vessel still exist?
- Is the anchor vessel on approximately the expected body? (A station moved from Kerbin orbit to Mun orbit invalidates loops recorded for Kerbin approach.)
- Is the docking port (if applicable) still available?

If validation fails, the loop is marked as broken in the recordings manager.

---

## 10. Multi-Vessel Merge to Single Timeline

### 10.1 The Problem

A recording session involving multiple vessels produces overlapping trajectory data: vessel A has active data and background data for B and C; vessel B has active data and background data for A and C; etc. This must be merged into one continuous track per vessel for the committed timeline.

### 10.2 Merge Rule: Highest Fidelity Wins

For any vessel at any moment in time, use the best available data source:

```
Priority 1: Active focus recording (full trajectory + events)
Priority 2: Background physics recording from nearest focused vessel
Priority 3: Orbital checkpoint propagation
```

### 10.3 Merge Procedure

```
For each vessel V in the RecordingTree:

  1. Collect all data sources from V's Recording:
     - TrackSections with source=Active (time intervals when V was the focus)
     - TrackSections with source=Background (from physics bubble recording)
     - TrackSections with source=Checkpoint (orbital checkpoint propagation)

  2. For each UT interval, select source by priority:
     - If V has Active data at this UT → use Active TrackSection
     - Else if V has Background data → use Background TrackSection
     - Else if V has Checkpoint data → propagate from nearest checkpoint
     - Else → V wasn't in range, no track needed for this interval

  3. Stitch selected sources into one continuous track.
     At source-switch boundaries, snap-switch (no interpolation).
     Log the position discontinuity magnitude at each boundary
     (stored as boundaryDiscontinuityMeters on the TrackSection).
```

**Implementation note (decided during Phase 2 planning):** Source-switch boundaries use snap-switch, not crossfade. A 0.5s crossfade at orbital velocities (2200 m/s in LKO) would create 1100m of fake trajectory. The discontinuity from snap-switch is typically sub-meter for in-bubble switches. The logged magnitude enables future analysis of whether smoothing is ever needed in practice.

### 10.4 No Circular References

At any given UT, exactly one vessel is the active focus. That vessel's position is absolute truth (the scene origin). All other vessels' positions derive from that vessel's perspective (background tracks) or from orbital mechanics (checkpoints). There is no circularity because focus is a total order — one and only one vessel owns the reference frame at each moment.

If a gap exists where no vessel has active recording (scene change to KSC), all vessels fall back to checkpoint propagation during the gap.

### 10.5 Committed Timeline Output

After merge, each vessel has a single clean Recording:

```
Recording (merged, per vessel)
  TrackSections:  list of TrackSection (non-overlapping, covering full session,
                  each tagged with source=Active/Background/Checkpoint)
  PartEvents:     all discrete events, merged from active + background sources
  Points:         flat trajectory (v5 backward compat, populated from merged TrackSections)
  OrbitSegments:  flat orbit segments (v5 backward compat)
```

**Implementation note (decided during Phase 2 planning):** There is no separate `CommittedVesselTrack` type. The merge output is a regular `Recording` object with its TrackSections stitched from the best sources. Each TrackSection's `source` field preserves provenance for diagnostics. The existing playback code works unchanged — it already knows how to play a Recording with TrackSections.

This is what gets stored in the timeline and used for all subsequent playback, including full-timeline replay and looped segment extraction.

---

## 11. Interaction with Pre-Existing Persistent Vessels

### 11.1 Core Rule

Parsek NEVER modifies any parameter of a real persistent vessel. Parsek is read-only with respect to game-state vessels. The only objects Parsek creates and controls are ghost vessels for playback.

### 11.2 Background Tracks of Existing Vessels

When a pre-existing vessel (from a previous recording or from the game start) appears in the physics bubble during recording, Parsek records its trajectory as background data (source=Background). This applies to external vessels not connected to the recording tree (e.g., a station the player passes near but doesn't dock with).

This background track serves as:

- **Anchor reference data** for Phase 3's relative-frame positioning (computing ghost position relative to the real vessel for pixel-perfect docking replay).
- **Fallback ghost data** if the real vessel no longer exists at playback time (Section 11.4).
- **Validation data** to check that the real vessel is approximately where the recording expected it.

If the real vessel IS present during playback, Parsek does NOT spawn a ghost for it. The real vessel serves as its own visual presence. External vessel background data is NOT committed to the playback timeline — it is stored as reference data only. Ghosts are only spawned for tree-connected vessels (those linked by split/merge/EVA events).

### 11.3 Merge Events with Existing Vessels

When a recording's vessel docks to a pre-existing vessel, the recording tree notes: `MERGE {targetVesselId: X}`. During playback, the ghost approaches the real vessel. At the merge UT, the ghost despawns (it has "docked" to the real vessel). The real vessel is unaffected.

### 11.4 Fallback: Real Vessel Missing

If a recording references a pre-existing vessel that no longer exists (destroyed, recovered, deorbited since the recording was made):

- For background tracks: spawn a ghost from the background track data as a visual placeholder.
- For merge events: the ghost reaches the merge point and despawns. The merge target's absence is logged.
- For looped recordings: the loop is marked as broken (see Section 9.5).

---

## 12. Summary of Data Flow

### 12.1 Recording Phase

```
Player flies missions, switches vessels, interacts with world
  │
  ├─ Focus vessel → ACTIVE recording (full trajectory + all events)
  ├─ Physics-bubble vessels → BACKGROUND recording (reduced trajectory + discrete events)
  ├─ On-rails vessels → ORBITAL CHECKPOINTS at key moments
  ├─ Focus switches → logged with UT
  ├─ Environment transitions → trigger segment boundaries
  ├─ Reference frame transitions → trigger segment boundaries
  └─ Scene changes / time warp → checkpoint all session vessels
  
Output: RecordingSession with multi-vessel, multi-track data
```

### 12.2 Merge Phase

```
RecordingSession
  │
  ├─ For each vessel: select highest-fidelity track at each UT
  ├─ Stitch into continuous single-source tracks
  ├─ Smooth transitions at source-switch boundaries
  └─ Collect DAG events into recording tree
  
Output: CommittedVesselTrack per vessel + RecordingTree (DAG)
```

### 12.3 Playback Phase

```
Committed timeline with recordings
  │
  ├─ For each recording active at current UT:
  │    ├─ Is vessel pre-existing and currently loaded? → don't spawn ghost, validate position
  │    ├─ Is ghost within Zone 1 of player? → full mesh + part events
  │    ├─ Is ghost within Zone 2 of player? → mesh only, orbital propagation
  │    └─ Is ghost in Zone 3? → no render, track logically
  │
  ├─ For looped recordings:
  │    ├─ Is anchor vessel loaded? → spawn ghost at current loop phase
  │    ├─ Compute ghost position relative to anchor's current position
  │    └─ Apply zone-based rendering rules
  │
  └─ Part events fire at recorded UT for all visible ghosts
```

---

## 13. Implementation Priorities

### 13.1 Phase 1: Foundation (extend current recording system)

Note: Bump the existing recording file version number when implementing these changes.

1. **Fix segment boundary rule**: Refactor so that only physical structural separation creates new segments. Controller changes (destruction, power loss, primary transfer) become SegmentEvents within a continuing segment. This fixes the current behavior where crashes split recordings into multiple segments.
2. Implement SegmentEvents (CONTROLLER_CHANGE, CONTROLLER_DISABLED, CONTROLLER_ENABLED, PART_DESTROYED, PART_REMOVED, PART_ADDED) with ghost visual updates (mesh add/remove at correct UT).
3. Implement BREAKUP event coalescing for rapid-fire crash splits (0.5s window).
4. Implement the five-state environment taxonomy with automatic transition detection.
5. Add reference frame axis (ABSOLUTE / RELATIVE / ORBITAL_CHECKPOINT) to segments.
6. Implement orbital checkpoint capture at time warp and scene change boundaries.
7. Extend part event recording to all physics-bubble vessels (not just focused vessel).

### 13.2 Phase 2: Multi-Vessel Sessions

8. Implement focus-switch tracking within a recording session.
9. Implement background trajectory recording for non-focused physics-bubble vessels with proximity-based sample rate.
10. Implement the highest-fidelity-wins merge procedure.
11. Handle scene-change gaps (all vessels on checkpoints).

### 13.3 Phase 3: Relative Frames and Anchoring

12. Implement relative-frame recording for approach segments near pre-existing vessels.
13. Implement automatic RELATIVE ↔ ABSOLUTE transition at physics bubble boundary.
14. Implement looped ghost spawning anchored to real vessel positions.
15. Implement loop phase tracking across vessel load/unload cycles.

### 13.4 Phase 4: Rewind and Timeline Operations (Vessel Recording Scope)

16. Implement quicksave capture at recording commit time.
17. Implement vessel list reconstruction from committed recordings on rewind.
18. Implement fast-forward with ghost playback during time warp.
19. Implement quicksave pruning options (manual and auto).
20. Note: Infrastructure patch recalculation, resource reservations, and deficit detection depend on the game actions system (separate design).

### 13.5 Phase 5: Rendering and Polish

21. Implement distance-based rendering zones with the 120km visual cutoff.
22. Implement ghost fade-in/fade-out at zone boundaries.
23. Implement looped ghost spawn thresholds (50km for simplified, 2.3km for full).
24. Implement anchor vessel validation for looped recordings.
25. Implement soft cap system for ghost count with configurable thresholds.

---

## 14. Rewind and Timeline Operations

### 14.1 Core Principle

Committed recordings are permanent — they survive rewind. Rewinding moves the player's position on the timeline but does not erase committed recordings. All committed recording ghosts play at their recorded UTs regardless of the player's current position on the timeline.

Each committed recording has an associated quicksave taken at its launch UT. This quicksave captures the game state needed to resume from that point.

**Timeline immutability policy (decided during Phase 4 planning):** Individual recordings cannot be deleted after commit. The player's only decision point is at recording end: commit to timeline or discard. The entire timeline can be wiped, but individual recordings cannot be removed. This prevents time paradoxes — deleting a recording mid-timeline could orphan spawned vessels, break chain continuity, or create inconsistent ghost playback.

**Future:** Timeline wipe from current UT forward — clear all future recordings while keeping everything that exists, is ongoing, or was already added to the timeline up to the current UT.

### 14.2 Rewind Procedure (Vessel Recording Scope)

The player is at UT=6000 and rewinds to Recording B's launch at UT=2000:

```
1. Load quicksave at UT=2000.
   The quicksave captures the game state at recording start. Vessels that
   were spawned by earlier completed recordings ARE in the quicksave
   (they existed when the quicksave was taken).

2. Ghost playback resumes from committed recordings:
   - Recordings completed before UT=2000: their spawned vessels are already
     in the quicksave — no action needed.
   - Recordings in flight at UT=2000: ghosts play at their trajectory positions.
   - Recordings not yet started at UT=2000: no ghosts yet, they will appear
     when currentUT reaches their StartUT.

3. Player resumes at UT=2000 with correct state.
```

**Implementation note (decided during Phase 4 planning):** There is no explicit "vessel list reconstruction" algorithm. The quicksave is the source of truth for which real vessels exist. Ghosts handle the visual replay. At each recording's end UT, the spawn-at-end logic checks: if the vessel doesn't exist in the game world (`SpawnedVesselPersistentId == 0`), spawn it from the recording's snapshot. If it already exists (from the quicksave), skip — the ghost just despawns. On revert, `SpawnedVesselPersistentId` resets to 0 from the quicksave, so spawns re-trigger naturally when the ghost replays and reaches the end.

### 14.3 Fast-Forward (Time Warp with Ghost Playback)

After rewinding to UT=2000, the player can time warp forward. During fast-forward:

- All committed recording ghosts play at their recorded UTs.
- Recording A (launched UT=100, completed UT=1500): already complete, vessel persisted in quicksave.
- Recording C (launched UT=4000): ghosts spawn at UT=4000 and play through.

The player can drop out of time warp at any point and start flying. Ghosts continue playing around them. The player can launch new missions that coexist with committed ghosts on the timeline.

**Implementation note (decided during Phase 4 planning):** The existing timeline playback system handles fast-forward naturally — ghosts are positioned by comparing `currentUT` to each recording's `StartUT`/`EndUT` every frame. During time warp, `currentUT` advances and ghosts appear/play/despawn at the correct UTs without special fast-forward code.

**Warp safety:** During time warp, vessel spawning is deferred to a spawn queue (`pendingSpawnRecordingIds`). When a recording "completes" during warp (ghost reaches end UT), the spawn is queued rather than executed immediately. The queue is flushed when the player exits time warp (`ShouldFlushDeferredSpawns`). This prevents physics instability from spawning vessels during warp. Visual FX are suppressed above 10x warp, ghost meshes hidden above 50x warp for performance.

### 14.4 Quicksave Pruning

**Deferred (decided during Phase 4 planning).** All quicksaves are kept. Pruning is purely storage management with no architectural impact — it can be added later without changing the rewind or timeline systems.

When implemented, the plan is:

- **Manual control:** Player can mark specific recordings as "rewind-protected" (keep quicksave) or "archive" (allow quicksave deletion, recording data preserved but no rewind-to-launch).
- **Auto-pruning policy (optional):** Keep the N most recent quicksaves. Older recordings lose rewind-to-launch capability but their ghost playback and timeline deltas are preserved.
- **Cascading rewind:** If a quicksave was pruned, rewinding to that recording instead rewinds to the nearest prior quicksave that still exists, then fast-forwards to the target UT with ghost playback.

---

## 15. Ghost Implementation Constraints

### 15.1 Ghosts Are Purely Visual

Ghosts are playback-only visual objects. They never become real vessels. They never carry enough state to be "promoted" to real vessels. There is no "take control of a ghost" feature — this is explicitly out of scope due to the complexity of reconstructing full vessel state from recorded data mid-flight.

### 15.2 Implementation: Raw Unity GameObjects (Confirmed)

Ghosts are implemented as raw Unity GameObjects with meshes cloned from part prefabs — NOT as KSP Vessel objects. All visual effects (engine flames, staging, parachutes, solar panels, etc.) are re-implemented by Parsek specifically for ghost playback.

**Advantages of this approach:**
- Ghosts are invisible to KSP's vessel management — no tracking station entries, no persistence overhead, no accidental interaction with game systems.
- No KSP per-vessel overhead (orbit calculation, thermal simulation, resource processing).
- Cleaner separation of concerns: ghosts are entirely Parsek's responsibility.
- Safer — no risk of ghost state leaking into the save file or confusing other mods.

**Tradeoffs:**
- All visual effects must be manually re-implemented for ghost playback. This is already done for the current set of part events (engine FX, staging, parachutes, solar panels, antennas, lights, landing gear, cargo bays, fairings, RCS, inventory deployables).
- New part events added in future (or by mods with custom visual behaviors) require explicit Parsek support to replay on ghosts.
- Modded parts with custom shader effects or animations may not replay perfectly unless Parsek adds specific support.

**Performance implication:** Since ghosts are not KSP Vessels, the ghost count limit is driven purely by Unity rendering cost (draw calls, polygon count), not by KSP's vessel management overhead. This is favorable for the multi-ghost scenarios described in this document.

### 15.3 Mod Compatibility for Ghost Visuals

Ghosts load part meshes from whatever parts the original vessel used, including modded parts. Base mesh rendering works for any part. Custom visual effects from mods (animated modules, shader effects, particle systems) may require explicit support to replay on ghosts. Unsupported modded visuals degrade gracefully — the mesh is in the right place but the custom animation doesn't play.

### 15.4 Ghost Interaction Rule

Ghosts do not physically interact with anything:
- No collisions with real vessels, terrain, or other ghosts.
- No resource transfer to or from ghosts.
- No docking with real vessels (ghost docking events are visual only — the ghost approaches and despawns at the merge point).
- The player cannot click on or select ghosts for vessel control.

Ghosts are visible scenery. They make the world feel alive but do not participate in game mechanics.

---

## 16. Recording File Format

### 16.1 Version Header (Already Implemented)

Parsek already versions its recording sidecar files. This existing versioning should be maintained and incremented as the new features in this document are implemented. Each structural change to the recording format (adding background tracks, orbital checkpoints, relative segments, focus logs, etc.) should bump the version.

Any code that reads recordings checks the version first. If the version is higher than the code supports, it fails with a clear message ("this recording requires Parsek X.Y+") rather than attempting to parse unknown structures.

### 16.2 Additive Format Evolution

As new features are added, the format should evolve additively: new fields are added, old fields are never renamed or removed. A reader encountering fields it doesn't recognize safely ignores them and plays back the data it does understand.

This provides forward tolerance: an older Parsek version encountering a newer recording with extra fields (e.g., relative-frame data it doesn't implement) can still play back the absolute trajectory data. Visual fidelity may be reduced but playback doesn't break.

This becomes important for async multiplayer (Layer 4) where players sharing recordings may be running different Parsek versions.

### 16.3 Separation of Concerns

```
Save file (ScenarioModule):
  - Recording IDs and metadata
  - Committed timeline structure
  - Loop configurations
  - Resource reservations and infrastructure patches
  - Quicksave references

Sidecar files (one per recording):
  - Version header
  - Recording tree (DAG) structure
  - Vessel segment data
  - Track sections with trajectory frames
  - Part events
  - Orbital checkpoints
  - Background tracks
  - Focus log
```

This separation keeps saves lightweight and makes the async multiplayer file-sharing model natural — you share sidecar files, not save games.

---

## 17. Performance Budget

### 17.1 Cost Model

Costs that scale with ghost count:
- Unity draw calls (mesh rendering) — dominant cost for visual ghosts
- Part event processing (checking if any event fires this tick)
- Trajectory interpolation (computing position from recorded frames)

Costs independent of ghost count:
- Recording (only physics bubble matters, regardless of total recordings)
- Timeline management (data structures in memory)
- Orbital propagation (cheap analytical math)

### 17.2 Soft Cap System

Rather than hard limits, Parsek uses configurable soft caps that degrade gracefully:

```
Total ghosts in Zone 1 > 8:
  → Reduce background ghost fidelity (fewer mesh parts rendered per ghost)

Total ghosts in Zone 1 > 15:
  → Despawn lowest-priority ghosts (oldest loops first, background detail last)

Total ghosts in Zone 2 > 20:
  → Reduce Zone 2 rendering to orbit-line-only (no mesh)
```

Default thresholds are conservative. Players can adjust them in Parsek's settings based on their hardware capability.

### 17.3 Profiling Guidance

These thresholds are estimates. The coding agent should implement basic ghost spawning first, then profile with increasing ghost counts (5, 10, 20, 50) to find actual bottlenecks. The architecture (zone-based rendering, looped ghost spawn thresholds, anchor-dependent lifecycle) provides all the levers needed to tune performance based on empirical data.

---

## 18. Error Recovery

### 18.1 General Principle

Parsek never crashes or corrupts a save due to bad recording data. The player's actual game state is never at risk because Parsek only creates ghosts (raw Unity GameObjects) — it never modifies real vessels or real game state. Recording failures degrade gracefully: ghosts may be missing or visually imperfect, but gameplay continues.

All errors are logged to KSP.log with a `[Parsek]` prefix for debugging.

### 18.2 Sidecar File Errors

**Corrupted sidecar file** (fails to parse, truncated data, malformed structure):
→ Log warning with filename and parse error details.
→ Skip recording entirely. Show as "damaged" in recordings manager with option to delete.
→ Other recordings are unaffected.

**Missing sidecar file** (ScenarioModule references a recording whose sidecar is gone):
→ Log warning. Show recording as "missing data" in recordings manager.
→ Timeline metadata (commit time, launch UT, quicksave reference) preserved.
→ No ghost playback possible. Player can delete the orphaned entry.

**Unknown version header** (sidecar version higher than current Parsek supports):
→ Fail with clear message: "Recording [name] requires Parsek [version]+."
→ Do not attempt to parse. Recording visible in manager as "incompatible."

**Partially readable file** (valid version, but contains unknown fields from a newer format):
→ Parse what is understood, ignore unknown fields (additive format principle).
→ Playback at reduced fidelity (e.g., no relative-frame data if that field is unknown).
→ Log info-level notice about skipped fields.

### 18.3 DAG Structure Errors

**Segment references a parent event that doesn't exist:**
→ Treat segment as a root segment (orphaned). Log warning.
→ Ghost plays from its own trajectory data without DAG context.

**Split event produces a child segment with no trajectory data:**
→ Skip that child. Log warning. Other children of the split play normally.

**Merge event references a target segment that doesn't exist in the recording:**
→ Treat as a terminal event instead. Ghost despawns at the merge UT.
→ Log warning noting the missing merge target.

**Circular reference in DAG** (segment A's parent is segment B, B's parent is A):
→ Detect during recording load (topological sort fails).
→ Reject the entire recording as corrupted. Log error. Show as "damaged."

### 18.4 Trajectory and Checkpoint Errors

**Trajectory frames with NaN or Infinity positions:**
→ Skip affected frames during interpolation. Use last valid frame.
→ If no valid frames exist in a section, skip that track section entirely.
→ Log warning with affected UT range.

**Orbital checkpoint with invalid Keplerian elements** (NaN, negative SMA, eccentricity >= 1 for what should be a closed orbit):
→ Skip ghost for the affected interval.
→ Attempt recovery from the next valid checkpoint.
→ If no valid checkpoints exist, skip the orbital segment entirely.

**Trajectory position underground** (below terrain surface on the recorded body):
→ Allow it — this can happen legitimately (mining bases, cave-like terrain).
→ Only flag if position is below body center (clearly invalid).

**Large position discontinuity between adjacent track sections** (>1km jump at section boundary):
→ Log warning. Still play back — interpolate over 1 second to smooth the jump.
→ This can happen legitimately during SOI transitions or after scene-change gaps.

### 18.5 Multi-Vessel Session Errors

**Focus log gap** (no vessel recorded as active focus for a time interval):
→ Fall back to orbital checkpoint propagation for all vessels during the gap.
→ Log warning. Playback continues with reduced fidelity.

**Focus log overlap** (two vessels claimed as active focus at the same UT):
→ Use the later entry (assume the earlier one was superseded).
→ Log warning.

**Background track exists for a vessel not in the session's vessel list:**
→ Ignore the orphaned background track. Log info-level notice.

**Merge produces different vessel count than expected** (e.g., merge event says 2 parents but only 1 segment found):
→ Proceed with available data. The missing parent's ghost simply won't appear.
→ Log warning.

### 18.6 Playback and Ghost Errors

**Anchor vessel for looped recording no longer exists** (destroyed, recovered, deorbited):
→ Mark loop as broken in recordings manager. Do not spawn ghost.
→ Notify player on first detection: "Route to [vessel name] is broken — vessel not found."

**Anchor vessel exists but on wrong body** (station moved from Kerbin to Mun since recording):
→ Mark loop as broken. Same handling as missing vessel.

**Anchor vessel exists but docking port occupied** (for docking-approach loops):
→ Ghost plays approach but despawns before the dock event. Log info notice.
→ Loop continues cycling — the port may free up later.

**Part event references a part type that isn't installed** (modded part removed):
→ Skip the visual event for that part. Ghost mesh may be missing the part entirely (Unity will show nothing for an unresolvable prefab).
→ Log warning on first occurrence per part type, not per event.

**Ghost mesh fails to instantiate** (prefab missing, Unity error):
→ Skip that ghost entirely for the current spawn attempt.
→ Retry on next loop cycle or next time the anchor vessel loads.
→ Log error with part list for debugging.

**Too many ghosts active** (exceeds soft cap):
→ Degrade gracefully per the soft cap rules (Section 17.2).
→ Never hard-fail. Always show at least the highest-priority ghost.

### 18.7 Persistence Errors

**ScenarioModule data doesn't match sidecar files** (e.g., timeline references recordings that don't have sidecar files, or sidecar files exist with no timeline entry):
→ For orphaned timeline entries (no sidecar): show as "missing data" in manager.
→ For orphaned sidecar files (no timeline entry): ignore on load, but preserve the files. Player can manually import or delete them.

**Quicksave referenced by a recording is missing or corrupted:**
→ Rewind to that recording is unavailable. Notify player.
→ Fall back to nearest prior valid quicksave if player attempts rewind.
→ Recording playback (ghosts) is unaffected — only rewind capability is lost.

**Save/load during active recording session:**
→ If the player saves while recording, Parsek should capture the session state so it can resume after load. If this fails, the in-progress recording is lost but committed recordings are unaffected.
→ This is an implementation challenge — the coding agent should examine how KSP's save/load interacts with in-flight state and determine the safest approach.

---

*Document version: 2.6*
*Updated: 2026-03-18 — Phase 2-4 implementation decisions integrated (no RecordingSession, snap-switch merge, background split TTL, external vessel handling, source-tagged TrackSections, loop anchor extension, timeline immutability policy, spawn-at-end logic, warp safety queue, pruning deferred)*
*Parsek Recording System Design — Vessel recordings only.*
*Covers: Recording tree (DAG), segment boundary rule, SegmentEvents, breakup coalescing, identity persistence, multi-vessel sessions, segment taxonomy, rendering zones, looped playback anchoring, rewind (vessel scope), ghost constraints, file format, performance budget, error recovery.*
