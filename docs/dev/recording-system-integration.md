# Recording System Redesign — Integration Plan

*Maps the design document (recording-system-design.md) onto the existing Parsek codebase. Identifies what changes, what stays, what's new, and the task breakdown for implementation.*

*Living document — updated as implementation progresses.*

---

## 1. Architecture Overview: Current vs. Redesign

### 1.1 Current Architecture (v5)

The current system records **one vessel at a time** as a flat list of `TrajectoryPoint` entries, with `OrbitSegment` entries for on-rails coasting and `PartEvent` entries for visual state changes. Multi-segment missions use a lightweight **chain** system (`ChainId`, `ChainIndex`, `ChainBranch`) and **RecordingTree** with **BranchPoint** nodes for undock/EVA/dock events.

```
Current data flow:
  FlightRecorder (sampling)
    → List<TrajectoryPoint> + List<OrbitSegment> + List<PartEvent>
    → Recording object (flat, one vessel)
    → RecordingStore (serialize to .prec sidecar)
    → ParsekScenario (metadata to .sfs)
    → ParsekFlight (playback: interpolate + position ghost)
    → GhostVisualBuilder (build mesh + FX from snapshot)

Key structures:
  Recording          — one vessel, one continuous trajectory
  RecordingTree      — DAG of Recording objects connected by BranchPoints
  BranchPoint        — split/merge event (Undock, EVA, Dock, Board, JointBreak)
  TrajectoryPoint    — (ut, lat, lon, alt, rotation, velocity, body, resources)
  OrbitSegment       — Keplerian elements for on-rails coasting
  PartEvent          — discrete visual state change (35 event types)
  GhostPlaybackState — runtime per-ghost rendering state
```

### 1.2 Redesign Architecture (v2 Design Doc)

The redesign introduces **VesselSegment** (replaces Recording), **TreeEvent** (replaces BranchPoint), **SegmentEvent** (new: within-segment state changes), **RecordingSession** (new: multi-vessel coordination), **TrackSection** (new: environment+reference frame typed trajectory chunks), and **CommittedVesselTrack** (new: merged per-vessel output).

```
Redesign data flow:
  FlightRecorder (focus vessel, full rate)
    + BackgroundRecorder (physics-bubble vessels, reduced rate)  [EXTEND]
    + OrbitalCheckpoints (on-rails vessels)                      [NEW]
    → RecordingSession (multi-vessel, multi-track)               [NEW]
    → Merge (highest-fidelity-wins per UT per vessel)            [NEW]
    → CommittedVesselTrack per vessel                            [NEW]
    → RecordingTree (DAG of VesselSegments + TreeEvents)         [EVOLVE]
    → ParsekFlight (zone-based playback)                         [EXTEND]
    → GhostVisualBuilder (unchanged mesh/FX building)            [KEEP]

Key structures:
  VesselSegment       — replaces Recording (adds controllers, tracks, segmentEvents)
  TreeEvent           — replaces BranchPoint (adds LAUNCH, BREAKUP, TERMINAL, richer metadata)
  SegmentEvent        — NEW: within-segment state changes (controller change, part destroyed, crew)
  RecordingSession    — NEW: multi-vessel recording coordination
  TrackSection        — NEW: typed trajectory chunk (environment + reference frame)
  CommittedVesselTrack — NEW: merged per-vessel output for playback
  OrbitalCheckpoint   — NEW: Keplerian snapshot for on-rails vessels
  SurfaceCheckpoint   — NEW: surface position snapshot
```

### 1.3 What Can Be Reused As-Is

| Component | Status | Notes |
|-----------|--------|-------|
| `TrajectoryPoint` struct | **Keep** | Fields are correct. Will be used inside TrackSection instead of directly in Recording. |
| `OrbitSegment` struct | **Keep** | Maps to OrbitalCheckpoint concept. Already has the right Keplerian fields. |
| `PartEvent` struct + enum | **Keep** | All 35 event types are correct. Will be routed per-vessel in multi-vessel mode. |
| `GhostVisualBuilder` | **Keep** | Ghost mesh/FX building is independent of recording structure. |
| `GhostPlaybackState` | **Keep** | Runtime ghost state is unchanged. |
| `GhostTypes.cs` | **Keep** | All ghost info types (Engine, RCS, Deployable, etc.) unchanged. |
| `GhostPlaybackLogic` | **Keep** | Warp/loop policy, interpolation helpers unchanged. |
| `TrajectoryMath` | **Keep** | Pure math (ShouldRecordPoint, interpolation, orbit propagation) unchanged. |
| `VesselSpawner` | **Keep** | Vessel spawn/recover utilities unchanged. |
| `MergeDialog` | **Extend** | Will need to show multi-vessel merge options. |
| `ParsekUI` | **Extend** | Will need segment taxonomy display, multi-vessel views. |
| `RecordingPaths` | **Extend** | May need new sidecar file types for sessions. |
| `ParsekLog` | **Keep** | Logging infrastructure unchanged. |
| `PartStateSeeder` | **Keep** | Part state seeding shared between FlightRecorder and BackgroundRecorder. |

### 1.4 What Changes

| Component | Change Type | Phase 1 Status | Details |
|-----------|------------|----------------|---------|
| `Recording` class | **Evolve** | **Done** | Added `SegmentEvents`, `TrackSections`, `Controllers`, `IsDebris`. Kept existing fields. |
| `BranchPoint` | **Evolve** | **Done** | Added Launch=5, Breakup=6, Terminal=7 + type-specific metadata fields. |
| `RecordingTree` | **Evolve** | **Done** | BranchPoint metadata serialization + ControllerInfo serialization. |
| `FlightRecorder` | **Extend** | **Done** | Environment detection, reference frame tracking, segment boundary rule, crash coalescing via CrashCoalescer. |
| `BackgroundRecorder` | **Extend** | **Done** | `CheckpointAllVessels()` at warp/scene boundaries, `onPartDie`/`onPartJointBreak` for background vessels. |
| `ParsekFlight` | **Extend** | **Partial** | CrashCoalescer wiring, time warp/scene checkpoints done. Zone-based rendering, multi-vessel playback, looped anchoring deferred to Phases 2-5. |
| `RecordingStore` | **Extend** | **Done** | SegmentEvent + TrackSection serialization. Version bump 5→6. |
| `ParsekScenario` | **Extend** | Pending | Session persistence, loop config, quicksave management (Phases 2-4). |

### 1.5 What's New

| Component | Status | Purpose |
|-----------|--------|---------|
| `SegmentEvent.cs` | **Done (Phase 1)** | Struct — within-segment state changes (8 types) |
| `TrackSection.cs` | **Done (Phase 1)** | Struct — environment+reference frame typed trajectory chunk |
| `ControllerInfo.cs` | **Done (Phase 1)** | Struct — controller part descriptor |
| `EnvironmentDetector.cs` | **Done (Phase 1)** | Static class — 5-state environment classification + hysteresis |
| `CrashCoalescer.cs` | **Done (Phase 1)** | Class — breakup event coalescing (0.5s window) |
| `SegmentBoundaryLogic.cs` | **Done (Phase 1)** | Static class — joint break classification (split vs within-segment) |
| `RecordingSession.cs` | Phase 2 | Class — multi-vessel session coordination |
| `CommittedVesselTrack.cs` | Phase 2 | Class — merged per-vessel output |
| `SessionMerger.cs` | Phase 2 | Static class — highest-fidelity-wins merge logic |
| `RenderingZoneManager.cs` | Phase 5 | Class — distance-based ghost rendering zone management |

---

## 2. Mapping Design Concepts to Existing Code

### 2.1 VesselSegment ← Recording

The design's `VesselSegment` maps closely to the existing `Recording` class. Strategy: **evolve Recording in-place** rather than creating a parallel class, to minimize disruption.

**Fields to add to Recording:**
```csharp
// Controller tracking (Section 2.2, 3.4)
public List<ControllerInfo> Controllers;      // controllers at segment start
public bool IsDebris;                          // true if no controllers

// Segment events (Section 3.7)
public List<SegmentEvent> SegmentEvents;       // within-segment state changes

// Track sections (Section 5.3) — replaces flat Points list for new recordings
public List<TrackSection> TrackSections;        // environment+reference typed chunks

// Origin info (Section 3.2) — only for root segments
public LaunchOrigin Origin;                    // body, lat, lon, alt, site name
```

**Fields that map directly (no change needed):**
- `Points` → still used within TrackSection (each section has its own frame list)
- `OrbitSegments` → maps to ORBITAL_CHECKPOINT track sections
- `PartEvents` → stays at segment level (already correct)
- `VesselName`, `RecordingId`, `TreeId` → unchanged
- `TerminalStateValue` → maps to TreeEvent.TERMINAL.cause
- `VesselPersistentId` → unchanged

**Backward compatibility:** Old recordings (v5) have flat `Points` lists with no `TrackSections`. The playback code checks `rec.TrackSections.Count > 0` for track-based playback; else falls back to flat `Points` list (legacy mode). No migration needed — old recordings play as before.

### 2.2 TreeEvent ← BranchPoint

The design's `TreeEvent` maps to the existing `BranchPoint` class. Strategy: **extend BranchPoint** with new types and metadata.

**Current BranchPointType enum:**
```csharp
Undock = 0, EVA = 1, Dock = 2, Board = 3, JointBreak = 4
```

**Design TreeEvent types to add:**
```csharp
Launch = 5,     // new: root segment creation
Breakup = 6,    // new: crash coalescing (replaces rapid JointBreak sequences)
Terminal = 7,   // new: segment end (Recovered, Destroyed, Recycled, Despawned)
// SPLIT maps to existing Undock/EVA/JointBreak (with cause metadata)
// MERGE maps to existing Dock/Board (with target vessel metadata)
```

**Metadata to add to BranchPoint:**
```csharp
// SPLIT metadata
public string SplitCause;         // DECOUPLE, UNDOCK, EVA
public uint DecouplerPartId;      // part that triggered separation

// BREAKUP metadata
public string BreakupCause;       // CRASH, OVERHEAT, STRUCTURAL_FAILURE
public double BreakupDuration;    // time window
public int DebrisCount;           // non-tracked fragment count
public double CoalesceWindow;     // threshold used (default 0.5s)

// MERGE metadata
public string MergeCause;         // DOCK, BOARD, CONSTRUCT, CLAW
public uint TargetVesselPersistentId;  // pre-existing vessel if applicable

// TERMINAL metadata
public string TerminalCause;      // RECOVERED, DESTROYED, RECYCLED, DESPAWNED
```

### 2.3 SegmentEvent (New)

No existing equivalent. New struct:

```csharp
public enum SegmentEventType
{
    ControllerChange = 0,
    ControllerDisabled = 1,
    ControllerEnabled = 2,
    CrewLost = 3,
    CrewTransfer = 4,
    PartDestroyed = 5,
    PartRemoved = 6,
    PartAdded = 7
}

public struct SegmentEvent
{
    public double ut;
    public SegmentEventType type;
    public string details;  // JSON or ConfigNode string with type-specific data
}
```

**Recording impact:** These events are recorded by `FlightRecorder` alongside `PartEvents`. They are serialized in the .prec sidecar file as `SEGMENT_EVENT` ConfigNodes. Ghost visual updates (mesh add/remove at correct UT) are handled by `ParsekFlight` during playback.

### 2.4 TrackSection (New)

Replaces the flat `Points` list with typed trajectory chunks:

```csharp
public enum SegmentEnvironment
{
    Atmospheric = 0,
    ExoPropulsive = 1,
    ExoBallistic = 2,
    SurfaceMobile = 3,
    SurfaceStationary = 4
}

public enum ReferenceFrame
{
    Absolute = 0,
    Relative = 1,
    OrbitalCheckpoint = 2
}

public struct TrackSection
{
    public SegmentEnvironment environment;
    public ReferenceFrame referenceFrame;
    public double startUT;
    public double endUT;
    public uint anchorVesselId;           // for Relative frame only
    public List<TrajectoryPoint> frames;  // for Absolute/Relative
    public List<OrbitSegment> checkpoints; // for OrbitalCheckpoint
    public float sampleRateHz;
    public bool isFromBackground;
}
```

**Relationship to existing code:** Each `TrackSection` contains the same `TrajectoryPoint` and `OrbitSegment` data that currently lives flat on `Recording`. The track section adds environment and reference frame context that enables:
- Variable sample rates (50Hz atmospheric vs checkpoint-only ballistic)
- Relative positioning near anchor vessels
- Orbital propagation for on-rails segments

### 2.5 RecordingSession (New) → Multi-Vessel Coordination

```csharp
public class RecordingSession
{
    public string Id;
    public double StartUT;
    public double EndUT;
    public List<string> TreeIds;          // recording trees in this session
    public List<FocusSwitchEvent> FocusLog;
    public Dictionary<uint, VesselTrackData> VesselTracks;
}

public struct FocusSwitchEvent
{
    public double ut;
    public uint fromVesselId;
    public uint toVesselId;
}

public class VesselTrackData
{
    public List<TrackSection> ActiveSections;
    public List<TrackSection> BackgroundSections;
    public List<OrbitSegment> OrbitalCheckpoints;
}
```

**Relationship to existing code:** `BackgroundRecorder` already handles background vessel recording. The session concept wraps the coordination between `FlightRecorder` (focus) and `BackgroundRecorder` (background), plus adds the focus log and merge capability.

### 2.6 Environment Detection → EnvironmentDetector

New static class that classifies vessel state each frame:

```csharp
internal static class EnvironmentDetector
{
    // Returns current environment for a vessel
    internal static SegmentEnvironment Classify(Vessel v)
    {
        if (v.situation == Vessel.Situations.LANDED || v.situation == Vessel.Situations.SPLASHED)
        {
            return v.srfSpeed > 0.1 ? SegmentEnvironment.SurfaceMobile
                                     : SegmentEnvironment.SurfaceStationary;
        }
        if (v.altitude < v.mainBody.atmosphereDepth && v.mainBody.atmosphere)
            return SegmentEnvironment.Atmospheric;
        // Check if any engine is producing thrust
        if (HasActiveThrust(v))
            return SegmentEnvironment.ExoPropulsive;
        return SegmentEnvironment.ExoBallistic;
    }
}
```

**Integration point:** Called by `FlightRecorder.OnPhysicsFrame()` each tick. When the environment changes, a new `TrackSection` begins. Hysteresis (1s for thrust toggle, 3s for surface speed) prevents rapid toggling.

### 2.7 Crash Coalescing → CrashCoalescer

New class that groups rapid split events:

```csharp
internal class CrashCoalescer
{
    private const double DefaultWindow = 0.5; // seconds
    private double windowStart = double.NaN;
    private List<PendingSplit> pendingSplits = new List<PendingSplit>();

    // Called when a split event fires. Returns null if still coalescing,
    // or a BREAKUP TreeEvent when the window expires.
    internal TreeEvent OnSplit(double ut, uint parentPid, List<uint> childPids, bool hasController)
    {
        // ... coalescing logic ...
    }
}
```

**Integration point:** `FlightRecorder` currently subscribes to `onPartJointBreak`. Instead of immediately creating a BranchPoint, it feeds the event to the coalescer. The coalescer emits a single BREAKUP event after the window expires.

### 2.8 Rendering Zones → RenderingZoneManager

New class for distance-based ghost rendering:

```csharp
internal class RenderingZoneManager
{
    internal enum Zone { Physics, Visual, Beyond }

    internal static Zone ClassifyDistance(double distanceKm)
    {
        if (distanceKm < 2.3) return Zone.Physics;
        if (distanceKm < 120) return Zone.Visual;
        return Zone.Beyond;
    }
}
```

**Integration point:** `ParsekFlight.Update()` currently positions all ghosts unconditionally. With zones, it should:
- Zone 1 (Physics): full mesh + part events (current behavior)
- Zone 2 (Visual): mesh only, orbital propagation, no part events
- Zone 3 (Beyond): no mesh, position tracked for map view only

---

## 3. Existing Code That Needs Careful Surgery

### 3.1 FlightRecorder.cs — The Heart of Recording

**Phase 1 status:** Extended with segment boundary rule, environment tracking, reference frame tracking, crash coalescing.

**Done (Phase 1):**
1. Segment boundary rule — `SegmentBoundaryLogic.ClassifyJointBreakResult()` classifies joint breaks; `WithinSegment` emits SegmentEvents via `EmitBreakageSegmentEvents()`, structural splits go to CrashCoalescer
2. Environment tracking — `EnvironmentHysteresis` + `EnvironmentDetector.Classify()` using cached engines; creates TrackSections on transitions
3. Reference frame tracking — ABSOLUTE ↔ ORBITAL_CHECKPOINT on rails transitions
4. Crash coalescing — `CrashCoalescer` wired into `ParsekFlight.DeferredJointBreakCheck`

**Remaining (Phase 2+):**
5. **RELATIVE frame activation (Phase 3):** Detect proximity to pre-existing vessels, switch to RELATIVE frame
6. **Controller monitoring per-frame (Phase 2):** Poll for controller disabled/enabled (power loss, hibernation). Currently only destruction-path is handled via joint break classification

### 3.2 BackgroundRecorder.cs — Already Exists!

**Phase 1 status:** Extended with orbital checkpointing and full part event coverage.

**Done (Phase 1):**
1. `CheckpointAllVessels()` at time warp and scene change boundaries
2. `onPartDie` and `onPartJointBreak` subscriptions for background vessels (was missing — only had 17 polled events)
3. Coverage audit documented in PollPartEvents comment block (19 event types total)

**Remaining (Phase 2+):**
4. **Proximity-based sample rate (Phase 2):** Variable rate based on distance (<200m: 5Hz, 200-1km: 2Hz, 1-2.3km: 0.5Hz)
5. **Background structural events wired to tree (Phase 2):** Background joint breaks currently emit Decoupled PartEvents but don't create BranchPoints in the tree
6. **TrackSection wrapping for background data (Phase 2):** Background trajectory data should use `isFromBackground = true`
7. **Periodic safety-net checkpoints (low priority):** Every 3-5 orbits for long coasts

### 3.3 ParsekFlight.cs — The Playback Controller

**Current behavior:** Manages ghost lifecycle, positions ghosts each frame via interpolation, fires part events at correct UTs, handles orbit segment transitions.

**Changes needed:**
1. **Zone-based rendering (Design 8):** Compute distance from player vessel to each ghost. Apply zone rules (mesh visible/hidden, part events on/off, orbital propagation vs trajectory data).

2. **Multi-vessel playback:** When a session has multiple committed vessel tracks, manage ghosts for all vessels simultaneously.

3. **Looped playback anchoring (Design 9):** For looped recordings, compute ghost position relative to anchor vessel's current position (not historical position).

4. **TrackSection-based playback:** Instead of interpolating across the flat Points list, interpolate within the current TrackSection. Handle section transitions (environment/reference frame changes).

5. **SegmentEvent application:** At playback time, apply PART_DESTROYED/PART_ADDED SegmentEvents to add/remove parts from ghost mesh.

### 3.4 RecordingStore.cs — Serialization

**Changes needed:**
1. **New ConfigNode types:** SEGMENT_EVENT, TRACK_SECTION, FOCUS_SWITCH, SESSION_METADATA.
2. **Version bump:** Increment `CurrentRecordingFormatVersion` (currently 5 → 6 for segment events, → 7 for track sections, etc. — or single bump to 6 with all new fields).
3. **Backward compat:** Old recordings without TrackSections play back using legacy flat Points list.

### 3.5 ParsekScenario.cs — Save/Load

**Changes needed:**
1. **Session persistence:** RECORDING_SESSION nodes with focus log, vessel track references.
2. **Loop configuration persistence:** Anchor vessel ID, looped segment IDs, loop phase.
3. **Quicksave management:** Quicksave references per committed recording for rewind.

---

## 4. Implementation Phases

Based on the design document's Section 13 (Implementation Priorities), mapped to actual code changes:

### Phase 1: Foundation (Design items 1-7)

**Goal:** Extend the current single-vessel recording system with the new segment boundary rule, segment events, crash coalescing, environment taxonomy, reference frames, and orbital checkpoints.

**Why first:** These are structural changes to the recording data model. Everything else builds on them.

#### Phase 1 — COMPLETE (PR #59)

All 7 design items implemented, tested, reviewed. 32 files changed, ~10k lines added, 2086 tests pass.

**New source files:**
- `SegmentEvent.cs` — 8-type enum (ControllerChange..PartAdded) + struct (ut, type, details)
- `TrackSection.cs` — SegmentEnvironment (5 states), ReferenceFrame (3 frames), TrackSection struct
- `ControllerInfo.cs` — controller part descriptor (type, partName, partPersistentId)
- `EnvironmentDetector.cs` — pure static `Classify()` + `EnvironmentHysteresis` (1s thrust, 3s speed debounce)
- `CrashCoalescer.cs` — 0.5s window coalescing, emits BREAKUP BranchPoints
- `SegmentBoundaryLogic.cs` — `ClassifyJointBreakResult()`, `EmitBreakageSegmentEvents()`, `IsControllerPart()`

**Modified source files:**
- `BranchPoint.cs` — added Launch=5, Breakup=6, Terminal=7 + metadata (SplitCause, BreakupCause, DebrisCount, MergeCause, TerminalCause, etc.)
- `Recording.cs` — added SegmentEvents, TrackSections, Controllers, IsDebris fields
- `RecordingStore.cs` — version 5→6, `SerializeSegmentEvents`/`DeserializeSegmentEvents`, `SerializeTrackSections`/`DeserializeTrackSections`, `TrackSectionMinVersion` constant
- `RecordingTree.cs` — BranchPoint metadata serialization, ControllerInfo serialization
- `FlightRecorder.cs` — environment tracking (TrackSections created on transitions), reference frame tracking (ABSOLUTE↔ORBITAL_CHECKPOINT on rails transitions), dual-write to flat Points + TrackSection.frames, uses cached engines for environment detection
- `ParsekFlight.cs` — CrashCoalescer wired into DeferredJointBreakCheck, SegmentBoundaryLogic classification, time warp checkpoint handler, scene change checkpoint call
- `BackgroundRecorder.cs` — `CheckpointAllVessels()` for warp/scene boundaries, `onPartDie`/`onPartJointBreak` subscriptions for background vessels

**Log subsystem tags (standardized):**
- `Recorder` — FlightRecorder (environment tracking, TrackSection lifecycle)
- `BgRecorder` — BackgroundRecorder (background vessel events, checkpoints)
- `Coalescer` — CrashCoalescer + ParsekFlight crash wiring
- `Environment` — EnvironmentDetector + EnvironmentHysteresis
- `Boundary` — SegmentBoundaryLogic (joint break classification)
- `Checkpoint` — orbital checkpoint operations

**Deferred to later phases:**
- RELATIVE reference frame activation (Phase 3 — requires proximity detection)
- Background vessel SegmentEvents (Phase 2 — requires multi-vessel session infrastructure)
- Child recording creation after BREAKUP (Phase 2 — requires multi-vessel recording)

**Test files (17 new):**
- `SegmentEventTests.cs` (16 tests), `SegmentEventSerializationTests.cs` (14 tests)
- `TrackSectionTests.cs` (20 tests), `TrackSectionSerializationTests.cs` (15 tests)
- `BranchPointExtensionTests.cs` (18 tests), `RecordingFieldExtensionTests.cs` (11 tests)
- `FormatVersionTests.cs` (10 tests)
- `EnvironmentDetectorTests.cs` (40 tests), `EnvironmentTrackingIntegrationTests.cs` (21 tests)
- `CrashCoalescerTests.cs` (32 tests), `CrashCoalesceWiringTests.cs` (19 tests)
- `SegmentBoundaryRuleTests.cs` (37 tests)
- `ReferenceFrameTrackingTests.cs` (20 tests)
- `BackgroundRecorderTests.cs` (extended), `BackgroundPartEventAuditTests.cs` (19 tests)

### Phase 2 — COMPLETE (PR #59)

All design items 8-11 implemented plus background vessel split detection with TTL.

**Decision: No RecordingSession class.** Merge operates directly on RecordingTree. Focus history derived from ActiveRecordingId. Scene-change gaps implicit via orbital checkpoints.

**New source files:**
- `ProximityRateSelector.cs` — distance-based background sample rate (<200m: 5Hz, 200-1km: 2Hz, 1-2.3km: 0.5Hz)
- `SessionMerger.cs` — highest-fidelity-wins merge (Active > Background > Checkpoint), overlap resolution with gap preservation, snap-switch boundaries with logged discontinuity, PartEvent deduplication by (ut, pid, type, moduleIndex, value)

**Modified source files:**
- `TrackSection.cs` — added `TrackSectionSource` enum (Active/Background/Checkpoint), `source` field, `boundaryDiscontinuityMeters` field
- `BackgroundRecorder.cs` — proximity-based sample rate wired in, background vessel split detection via `HandleBackgroundVesselSplit`, debris TTL (30s / crash / out-of-bubble), background TrackSection creation with environment tracking and source=Background tagging, flush on all exit paths
- `MergeDialog.cs` — per-vessel rows via `ShowMultiVesselTreeDialog`, `CanPersistVessel` / `BuildDefaultVesselDecisions` pure logic, `ApplyVesselDecisions` for ghost-only nulling
- `RecordingStore.cs` — `SessionMerger.MergeTree` wired into `CommitTree`, merge-in-place preserving all original fields by default
- `GhostPlaybackLogic.cs` — `RealVesselExists` + `ShouldSkipExternalVesselGhost` for external vessel fallback
- `ParsekFlight.cs` — external vessel ghost skip in timeline playback, background split/TTL tick calls

**Log subsystem tags:**
- `Merger` — SessionMerger overlap resolution, commit-time merge
- `BgRecorder` — background splits, TTL, TrackSection lifecycle (existing tag, extended)

**Test files (7 new):**
- `TrackSectionSourceTests.cs` (17 tests), `ProximityRateSelectorTests.cs` (19 tests)
- `BackgroundSplitTests.cs` (37 tests), `BackgroundTrackSectionTests.cs` (30 tests)
- `SessionMergerTests.cs` (32 tests), `MergeDialogVesselTests.cs` (19 tests)
- `CommitFlowTests.cs` (21 tests)

**Review fixes applied:**
- Part event dedup key includes moduleIndex + value (multi-engine correctness)
- CommitTree merge uses in-place field overwrite (new fields preserved by default)
- Overlap gap loss bug fixed (lower-priority section between two higher-priority preserved)
- Double-flush bug fixed (trackSections cleared after flush)
- Dialog text corrected (removed nonexistent toggle promise)

### Phase 3: Relative Frames and Anchoring (Design items 12-15)

**Goal:** Relative-frame recording near anchor vessels, looped ghost anchoring.

#### Task 3.1: Relative-frame recording
- **Modify:** `FlightRecorder.cs` — record positions relative to anchor vessel
- **Modify:** `TrajectoryMath.cs` — relative position computation
- **Scope:** ~150 lines modified

#### Task 3.2: RELATIVE ↔ ABSOLUTE transition
- **Modify:** `FlightRecorder.cs` — auto-detect physics bubble entry/exit
- **Scope:** ~80 lines modified

#### Task 3.3: Looped ghost spawning anchored to real vessels
- **Modify:** `ParsekFlight.cs` — anchor-relative position computation for looped ghosts
- **Scope:** ~150 lines modified

#### Task 3.4: Loop phase tracking across vessel load/unload
- **Modify:** `ParsekFlight.cs` — track loop phase, restore on vessel load
- **Modify:** `ParsekScenario.cs` — persist loop phase
- **Scope:** ~100 lines modified

### Phase 4: Rewind and Timeline (Design items 16-20)

**Goal:** Quicksave at commit, vessel list reconstruction on rewind, fast-forward with ghosts.

#### Task 4.1: Quicksave capture at recording commit
- **Modify:** `ParsekFlight.cs` — capture quicksave when recording is committed
- Already partially implemented (`RewindSaveFileName` exists on Recording)
- **Scope:** ~50 lines modified

#### Task 4.2: Vessel list reconstruction on rewind
- **New method in** `RecordingStore.cs` or `ParsekFlight.cs` — reconstruct vessel list from committed recordings at target UT
- **Scope:** ~200 lines new

#### Task 4.3: Fast-forward with ghost playback
- **Modify:** `ParsekFlight.cs` — during time warp, play committed ghosts at recorded UTs
- **Scope:** ~100 lines modified

#### Task 4.4: Quicksave pruning
- **Modify:** `ParsekUI.cs` — UI for rewind-protected vs archive recordings
- **Modify:** `ParsekScenario.cs` — pruning logic
- **Scope:** ~150 lines new

### Phase 5: Rendering and Polish (Design items 21-25)

**Goal:** Distance-based rendering zones, fade effects, ghost count management.

#### Task 5.1: Distance-based rendering zones
- **New file:** `RenderingZoneManager.cs` — zone classification and transition logic
- **Modify:** `ParsekFlight.cs` — apply zone rules during playback
- **Scope:** ~150 lines new, ~100 lines modified

#### Task 5.2: Ghost fade-in/fade-out at zone boundaries
- **Modify:** `ParsekFlight.cs` — alpha interpolation during zone transitions
- **Scope:** ~80 lines modified

#### Task 5.3: Looped ghost spawn thresholds
- **Modify:** `ParsekFlight.cs` — distance-based spawn rules for looped vs full-timeline
- **Scope:** ~50 lines modified

#### Task 5.4: Anchor vessel validation
- **Modify:** `ParsekFlight.cs` — validate anchor vessel existence, body, port availability
- **Scope:** ~80 lines new

#### Task 5.5: Soft cap system
- **New:** ghost count tracking and degradation logic
- **Modify:** `ParsekFlight.cs` — apply soft caps during ghost spawning
- **Scope:** ~150 lines new

---

## 5. Key Design Decisions for Implementation

### 5.1 Evolve-in-Place vs. Parallel Classes

**Decision: Evolve Recording → add segment fields, keep backward compat.**

Rationale: Creating a parallel `VesselSegment` class would require duplicating all serialization, all playback code, and maintaining two parallel code paths indefinitely. Instead, add new fields to `Recording` and use version-based branching in playback code. Old recordings (v5) use flat Points; new recordings (v6+) use TrackSections.

### 5.2 BranchPoint Extension vs. TreeEvent Replacement

**Decision: Extend BranchPoint with new types and metadata fields.**

Rationale: BranchPoint already has serialization, DAG traversal, and UI integration. Adding new enum values and optional metadata fields is simpler than replacing the class. The design's `TreeEvent` is conceptually the same as `BranchPoint` with more types.

### 5.3 Segment Boundary Detection Strategy

**Decision: Use `GameEvents.onVesselWasModified` + vessel count tracking.**

When `onPartJointBreak` fires, don't immediately create a BranchPoint. Instead, wait for `onVesselWasModified` (which fires after KSP processes the joint break). If FlightGlobals.Vessels count increased, a real split occurred → create TreeEvent. If count unchanged, parts broke but vessel stayed connected → create SegmentEvents (PART_DESTROYED, CONTROLLER_CHANGE if applicable).

### 5.4 Recording Format Version Strategy

**Decision: Single version bump to v6 with additive fields.**

All Phase 1 changes go into v6. The format is additive: v6 readers ignore unknown fields from v7+, v5 readers ignore TrackSection/SegmentEvent fields. No multi-step migration needed.

### 5.5 TrackSection Storage

**Decision: TrackSections serialized as TRACK_SECTION ConfigNodes in .prec files, alongside existing POINT/ORBIT_SEGMENT/PART_EVENT nodes.**

For v6+ recordings: TRACK_SECTION nodes contain their own POINT sub-nodes. The flat top-level POINT nodes are also written (dual-write for backward compat). For v5 recordings: flat POINT nodes only (no TrackSections). Playback code checks: if `rec.TrackSections.Count > 0`, use track-based playback; else use legacy flat Points. Gate constant: `RecordingStore.TrackSectionMinVersion = 6`.

---

## 6. Risk Areas and Mitigations

### 6.1 Segment Boundary Detection Race Conditions

**Risk:** KSP fires `onPartJointBreak` before `onVesselWasModified`. If the coalescer window (0.5s) overlaps with KSP's internal vessel splitting, we might misclassify events.

**Mitigation:** Use a two-phase approach: (1) on `onPartJointBreak`, buffer the event; (2) on next `FixedUpdate`, check vessel count. If vessel count changed, classify as split. If window expires without vessel count change, classify as breakage-within-vessel.

### 6.2 Background Vessel Part Event Routing

**Risk:** KSP GameEvents fire globally. Need to route each event to the correct vessel's recording.

**Mitigation:** `BackgroundRecorder` already handles this — it checks `part.vessel.persistentId` to route events. Verify all 35 PartEventTypes are correctly routed.

### 6.3 Relative Frame Precision

**Risk:** Switching between ABSOLUTE and RELATIVE frames at the physics bubble boundary may cause position discontinuities.

**Mitigation:** Design specifies 0.5s interpolation at source-switch boundaries. Implement as a crossfade between old and new frame positions.

### 6.4 Backward Compatibility with Existing Saves

**Risk:** Existing saves have v5 recordings. Must not break them.

**Mitigation:** No migration needed — v5 recordings play back using legacy flat Points path. New v6 recordings use TrackSections. Both coexist in the same save. The RecordingTree already handles mixed recording types.

### 6.5 Performance with Many Ghosts

**Risk:** Multi-vessel sessions + looped recordings could spawn many ghosts.

**Mitigation:** Zone-based rendering (Phase 5) limits visual cost. Soft caps degrade gracefully. Phase 5 is deliberately last — get the system working first, then optimize.

---

## 7. Testing Strategy

### 7.1 Existing Test Infrastructure

The project has extensive test infrastructure:
- `RecordingBuilder` — fluent API for building Recording ConfigNodes (default format v6)
- `VesselSnapshotBuilder` — fluent API for building vessel snapshots
- `ScenarioWriter` — injects recordings into .sfs save files
- `ParsekLog.TestSinkForTesting` — captures log output for assertions
- 2086 tests pass after Phase 1 (up from ~1770 on main)

### 7.2 New Test Fixtures Needed

| Fixture | Purpose |
|---------|---------|
| Multi-segment recording with SegmentEvents | Test CONTROLLER_CHANGE, PART_DESTROYED within continuing segment |
| Crash breakup recording | Test coalesced BREAKUP event with controlled children and debris count |
| Multi-environment recording | Test TrackSection transitions (atmospheric → exo_propulsive → exo_ballistic) |
| Multi-vessel session recording | Test focus switching, background tracks, merge |
| Relative-frame recording | Test RELATIVE TrackSection near anchor vessel |
| Looped segment recording | Test anchor-relative playback |

### 7.3 Test Categories per Phase

**Phase 1:** SegmentEvent serialization round-trip, segment boundary detection (split vs breakage), crash coalescing (timing, controlled child identification), EnvironmentDetector classification, TrackSection serialization

**Phase 2:** Focus switch event ordering, background track priority selection, merge (overlapping sources, gaps, checkpoint fallback), session serialization

**Phase 3:** Relative position computation, frame transition smoothing, loop phase calculation

**Phase 4:** Vessel list reconstruction at arbitrary UT, quicksave management

**Phase 5:** Zone classification, soft cap degradation rules

---

## 8. Task Workflow per Phase

Following `docs/dev/development-workflow.md`:

```
For each phase:
  1. Plan agent (clean context): reads this doc + design doc + codebase
     → produces ordered task list with file paths and done conditions
  2. Orchestrator reviews plan
  3. For each task:
     a. Implementation agent (worktree): implements + tests + commits
     b. Review agent (clean context): checks against design doc
     c. Fix agent (if issues found)
  4. Merge worktree branches
  5. Update this document with implementation notes
```

**Phase 1 is the critical path.** It changes the data model that everything else builds on. Get this right first.

---

*Document version: 1.2*
*Created: 2026-03-17*
*Updated: 2026-03-17 — Phase 1+2 complete*
*Status: Phase 1+2 complete (PR #59). 2261 tests pass. Phases 3-5 pending.*
