# Parsek: Preliminary Architecture

## Document Status
**Version:** 0.3
**Phase:** MVP (recording chaining)
**Last Updated:** February 2026

---

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                         PARSEK MOD                               │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────────────┐  │
│  │   RECORDER   │  │   TIMELINE   │  │      PLAYBACK        │  │
│  │              │  │   MANAGER    │  │      ENGINE          │  │
│  │ - Captures   │  │              │  │                      │  │
│  │   events     │  │ - Single     │  │ - Kinematic replay   │  │
│  │ - Buffers    │  │   source of  │  │ - Ghost vessels      │  │
│  │   recording  │  │   truth      │  │ - Event execution    │  │
│  │ - Handles    │  │ - Merges     │  │ - Vessel spawning    │  │
│  │   commit/    │  │   recordings │  │ - Engine FX          │  │
│  │   discard    │  │              │  │                      │  │
│  │              │  │              │  │                      │  │
│  └──────┬───────┘  └──────┬───────┘  └──────────┬───────────┘  │
│         │                 │                      │              │
│         └────────────────┼──────────────────────┘              │
│                          │                                      │
│                 ┌────────┴────────┐                            │
│                 │  TIME CONTROLLER │                            │
│                 │                  │                            │
│                 │ - Revert logic   │                            │
│                 │ - Warp control   │                            │
│                 │ - Event dispatch │                            │
│                 └────────┬─────────┘                            │
│                          │                                      │
├──────────────────────────┼──────────────────────────────────────┤
│                          │                                      │
│  ┌───────────────────────┴───────────────────────────────────┐ │
│  │                     PERSISTENCE LAYER                      │ │
│  │                                                            │ │
│  │  - Recording files (.parsek)                               │ │
│  │  - Timeline state (in save game)                           │ │
│  │  - Configuration                                           │ │
│  └────────────────────────────────────────────────────────────┘ │
│                                                                  │
├──────────────────────────────────────────────────────────────────┤
│                          UI LAYER                                │
│                                                                  │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────────┐ │
│  │  Recording  │  │  Timeline   │  │  Playback Controls      │ │
│  │  Controls   │  │  Viewer     │  │  (take control, etc.)   │ │
│  └─────────────┘  └─────────────┘  └─────────────────────────┘ │
│                                                                  │
└──────────────────────────────────────────────────────────────────┘
```

---

## Core Components

### 1. MainTimeline (Singleton)

The **single source of truth** for all committed events.

```csharp
public class MainTimeline : ScenarioModule
{
    // Singleton instance
    public static MainTimeline Instance { get; private set; }
    
    // All committed timeline events, sorted by UT
    private SortedList<double, TimelineEvent> events;
    
    // Active playback vessels (recorded missions in progress)
    private Dictionary<Guid, PlaybackVessel> activePlaybacks;
    
    // Pending recordings not yet committed
    private List<MissionRecording> pendingRecordings;
    
    // Core operations
    public void CommitRecording(MissionRecording recording);
    public void DiscardRecording(MissionRecording recording);
    public void ProcessEventsUntil(double universalTime);
    public bool HasConflict(MissionRecording recording);
}
```

**Key Responsibilities:**
- Store all committed events
- Merge recordings chronologically
- Detect and resolve conflicts
- Persist to save game via ScenarioModule

---

### 2. MissionRecorder

Captures vessel state during active recording.

```csharp
public class MissionRecorder : VesselModule
{
    // Recording state
    public bool IsRecording { get; private set; }
    public MissionRecording CurrentRecording { get; private set; }
    
    // Adaptive sampling thresholds (source: PersistentTrails)
    public float OrientationThreshold = 2.0f;   // degrees
    public float VelocityDirThreshold = 2.0f;   // degrees
    public float SpeedChangeThreshold = 0.05f;  // 5% relative change
    
    // Recording control
    public void StartRecording();
    public void StopRecording();
    public void CommitRecording();
    public void DiscardRecording();
    
    // Called every physics frame when recording
    // velocity = vessel.rb_velocityD + Krakensbane.GetFrameVelocity()
    private void SampleState();
    
    // Event handlers
    private void OnStaging(int stage);
    private void OnPartDecouple(Part part);
    private void OnVesselSOIChanged(Vessel vessel, CelestialBody body);
}
```

**Recording Hook:**
- Primary: Harmony postfix on `VesselPrecalculate.CalculatePhysicsStats()` for per-physics-frame data (source: KSPCommunityFixes)
- Fallback: `FixedUpdate()` polling for compatibility

**Sampling Strategy:**
- Adaptive threshold-based: record frame if orientation changes > 2deg, velocity direction changes > 2deg, or speed changes > 5% (source: PersistentTrails)
- Staging events: On occurrence (with 0.2s delay for state to settle — source: FMRS)
- SOI changes: On occurrence
- Event detection: GameEvents + polling hybrid for redundant reliable detection (source: StageRecovery)

---

### 3. MissionRecording

Data structure for a recorded mission.

```csharp
public class MissionRecording
{
    // Identity
    public Guid RecordingId { get; }
    public Guid OriginalVesselId { get; }
    public string VesselName { get; }
    
    // Time bounds
    public double StartUT { get; }
    public double EndUT { get; }
    
    // Recorded data
    public List<TrajectoryFrame> Trajectory { get; }
    public List<TimelineEvent> Events { get; }
    
    // Recording metadata
    public CelestialBody StartingBody { get; }
    public Orbit StartingOrbit { get; }
    public ConfigNode VesselSnapshot { get; }  // Full vessel state at start
    
    // State
    public RecordingState State { get; }  // Recording, Completed, Committed, Discarded
}

public struct TrajectoryFrame
{
    public double UT;
    public double Latitude;         // Geographic (double precision)
    public double Longitude;        // Geographic (double precision)
    public double Altitude;         // Above sea level
    public QuaternionD Rotation;
    public Vector3d Velocity;       // True velocity (rb_velocity + Krakensbane)
    public string BodyName;         // Reference body name (for SOI transitions)
}
// NOTE: Geographic coords avoid floating-point drift at large distances.
// Convert to world position via: body.GetWorldSurfacePosition(lat, lon, alt)
// Source: VesselMover + PersistentTrails patterns.

public enum RecordingState
{
    Recording,
    Completed,
    Committed,
    Discarded
}
```

---

### 4. TimelineEvent (Base Class)

Abstract base for all timeline events.

```csharp
public abstract class TimelineEvent
{
    public Guid EventId { get; }
    public Guid VesselId { get; }
    public double UT { get; }
    public bool Executed { get; protected set; }
    
    public abstract void Execute(PlaybackVessel vessel);
    public abstract bool CanExecute(PlaybackVessel vessel);
    public abstract ConfigNode Save();
    public static TimelineEvent Load(ConfigNode node);
}
```

**Event Types (MVP):**

```csharp
public class TrajectoryUpdateEvent : TimelineEvent
{
    public Vector3d Position { get; }
    public QuaternionD Rotation { get; }
    public CelestialBody RefBody { get; }
    
    public override void Execute(PlaybackVessel vessel)
    {
        vessel.SetPositionRotation(Position, Rotation, RefBody);
    }
}

public class StagingEvent : TimelineEvent
{
    public int StageNumber { get; }
    
    public override void Execute(PlaybackVessel vessel)
    {
        vessel.ActivateStage(StageNumber);
    }
}

public class SOIChangeEvent : TimelineEvent
{
    public CelestialBody NewBody { get; }
    public Orbit NewOrbit { get; }
}
```

---

### 5. PlaybackEngine

Executes recorded events on playback vessels.

```csharp
public class PlaybackEngine : MonoBehaviour
{
    // Active playbacks
    private List<PlaybackVessel> activeVessels;
    
    // Main update loop
    void FixedUpdate()
    {
        double currentUT = Planetarium.GetUniversalTime();
        
        foreach (var vessel in activeVessels)
        {
            if (vessel.ShouldSpawn(currentUT))
                SpawnPlaybackVessel(vessel);
            
            if (vessel.IsSpawned)
                UpdatePlaybackVessel(vessel, currentUT);
        }
    }
    
    // Vessel management
    private void SpawnPlaybackVessel(PlaybackVessel pv);
    private void DespawnPlaybackVessel(PlaybackVessel pv);

    // 5-step positioning pipeline (source: VesselMover)
    private void UpdatePlaybackVessel(PlaybackVessel pv, double ut)
    {
        var frame = InterpolateTrajectory(pv, ut);
        var body = FlightGlobals.Bodies.Find(b => b.name == frame.BodyName);
        Vector3d worldPos = body.GetWorldSurfacePosition(
            frame.Latitude, frame.Longitude, frame.Altitude);

        pv.KSPVessel.IgnoreGForces(240);  // CRITICAL: every physics frame
        pv.KSPVessel.SetPosition(worldPos);
        pv.KSPVessel.SetRotation(frame.Rotation);
        pv.KSPVessel.SetWorldVelocity(Vector3d.zero);
        pv.KSPVessel.angularVelocity = Vector3.zero;
    }

    // Interpolation
    private TrajectoryFrame InterpolateTrajectory(
        PlaybackVessel pv,
        double ut
    );
}
```

**Playback Modes:**

| Mode | Description | Status |
|------|-------------|--------|
| Kinematic | Direct position/rotation setting | Done |
| On-Rails | Analytical Keplerian orbit during time warp segments | Done |

---

### 6. TimeController

Manages time revert and warp operations.

```csharp
public class TimeController
{
    // Revert to recording start
    public void RevertToRecordingStart(MissionRecording recording)
    {
        // 1. Save current game state
        // 2. Load quicksave from recording start
        // 3. Inject committed events into timeline
        // 4. Resume play
    }
    
    // Warp control
    public void WarpToNextEvent();
    public void WarpToTime(double ut);
    public void PauseAtEvent(TimelineEvent evt);
    
    // Integration with KAC
    public void CreateAlarmForEvent(TimelineEvent evt);
}
```

---

### 7. PlaybackVessel

Represents a vessel being played back from recording.

```csharp
public class PlaybackVessel
{
    // Identity
    public Guid RecordingId { get; }
    public Guid SpawnedVesselId { get; }
    
    // State
    public bool IsSpawned { get; }
    public bool IsCompleted { get; }
    public bool PlayerTookControl { get; }
    
    // Data
    public MissionRecording Recording { get; }
    public int CurrentEventIndex { get; }
    
    // Vessel reference (when spawned)
    public Vessel KSPVessel { get; }
    
    // Control
    public void TakeControl();  // Player assumes control
}
```

---

## Data Flow

### Recording Flow

```
Player flies vessel
        │
        ▼
┌───────────────────┐
│  MissionRecorder  │
│  samples state    │
│  captures events  │
└────────┬──────────┘
         │
         ▼
┌───────────────────┐
│ MissionRecording  │
│ (in memory)       │
└────────┬──────────┘
         │
    ┌────┴────┐
    │         │
    ▼         ▼
 COMMIT    DISCARD
    │
    ▼
┌───────────────────┐
│   MainTimeline    │
│   merges events   │
└────────┬──────────┘
         │
         ▼
┌───────────────────┐
│   Persistence     │
│   (save game)     │
└───────────────────┘
```

### Playback Flow

```
Time advances
      │
      ▼
┌─────────────────────┐
│    TimeController   │
│    checks events    │
└──────────┬──────────┘
           │
           ▼
┌─────────────────────┐
│   PlaybackEngine    │
│   processes due     │
│   events            │
└──────────┬──────────┘
           │
     ┌─────┴─────┐
     │           │
     ▼           ▼
  SPAWN      UPDATE
  vessel     position
     │           │
     └─────┬─────┘
           │
           ▼
┌─────────────────────┐
│   PlaybackVessel    │
│   (ghost in scene)  │
└─────────────────────┘
```

---

## Persistence Strategy

### Save Game Integration

Using KSP's `ScenarioModule` system (`ParsekScenario`). Lightweight metadata + mutable playback state stored in the `.sfs` save file; bulk data stored in external per-recording sidecar files (format version 3).

**In .sfs (per RECORDING node):** `recordingId`, `vesselName`, `pointCount`, `recordingFormatVersion = 3`, mutable state (`vesselDestroyed`, `takenControl`, `spawnedPid`, `lastResIdx`), EVA linkage, ghost geometry metadata.

**No inline POINT, ORBIT_SEGMENT, PART_EVENT, or snapshot nodes** — all bulk data lives in external files.

### External Recording Files (v3)

```
saves/<save-name>/Parsek/Recordings/
├── <recordingId>.prec          # Trajectory (POINT, ORBIT_SEGMENT, PART_EVENT nodes)
├── <recordingId>_vessel.craft  # Vessel snapshot (ProtoVessel ConfigNode)
├── <recordingId>_ghost.craft   # Ghost visual snapshot (ProtoVessel ConfigNode)
└── <recordingId>.pcrf          # Ghost geometry artifact
```

- `.prec` = Parsek Recording (ConfigNode format with version + recordingId header)
- `.craft` = KSP-standard ConfigNode for vessel snapshots
- Safe-write via `.tmp` + rename to prevent corruption
- Stale vessel snapshots cleaned up on save when in-memory snapshot is null
- `RecordingPaths.ValidateRecordingId` rejects path-traversal and invalid filename characters

---

---

## Technical Patterns & Gotchas

Critical patterns discovered from analysis of 8 reference KSP mods. Ignoring these will cause hard-to-debug failures.

### Vessel State

- **Packed vs Unpacked:** Never manipulate a packed vessel (on-rails). Always check `vessel.packed` before calling `SetPosition`, staging, or any physics operation. Wait for `vessel.loaded && !vessel.packed`.
- **IgnoreGForces(240):** Must be called **every physics frame** before repositioning a vessel, or KSP's acceleration checks will destroy it. The `240` value is the number of frames to suppress. (Source: VesselMover)
- **ProtoVessel access:** Use `vessel.protoVessel` for accessing data on unloaded vessels (e.g., vessel name, parts list, crew).

### Coordinate Systems

- **Geographic storage:** Store trajectory as `(lat, lon, alt)` not world-space `Vector3d`. World positions suffer floating-point drift at large distances. Convert back via `body.GetWorldSurfacePosition(lat, lon, alt)`.
- **Krakensbane velocity:** KSP shifts the entire universe to keep the active vessel near the origin. True velocity = `vessel.rb_velocityD + Krakensbane.GetFrameVelocity()`. Recording `vessel.velocity` alone will be wrong.
- **Quaternion NaN protection:** Sanitize quaternion values before applying. NaN quaternions crash Unity rendering.

### Scene & Lifecycle

- **Scene switch cleanup:** Clear all tracked state (recording buffers, playback vessels, coroutines) in `GameEvents.onGameSceneLoadRequested` handler. Failing to do this causes null references and memory leaks.
- **Async save coordination:** Never load data while a save is in progress. Listen for `GameEvents.onGameStateSaved` to know when it's safe. (Source: FMRS)
- **Staging delay:** Wait ~0.2s after a staging event before capturing vessel state, because KSP takes several frames to finalize part decoupling and vessel splitting. (Source: FMRS)

### Performance & Time

- **Time warp awareness:** Destroy or hide ghost vessels during physics warp (warp > 1x with `TimeWarp.WarpMode == TimeWarp.Modes.LOW`). Pause kinematic playback entirely during high time warp.
- **Unity null checks:** In hot paths (FixedUpdate), use the `IsNotNullOrDestroyed()` pattern (direct reference check) instead of Unity's overloaded `== null` operator, which is 4-5x slower. (Source: KSPCommunityFixes)

---

## Recording Architecture

Summary of the recording strategy derived from mod analysis:

| Aspect | Approach | Source |
|--------|----------|--------|
| Hook | Harmony postfix on `VesselPrecalculate.CalculatePhysicsStats()` | KSPCommunityFixes |
| Storage | Geographic coordinates (lat/lon/alt, double precision) | VesselMover, PersistentTrails |
| Sampling | Adaptive thresholds (orientation > 2deg, velocity dir > 2deg, speed > 5%) | PersistentTrails |
| Events | GameEvents + polling hybrid for redundant detection | StageRecovery |
| Velocity | `rb_velocityD + Krakensbane.GetFrameVelocity()` | VesselMover |
| Staging | Capture with 0.2s delay after event | FMRS |

---

## UI Design (Preliminary)

### Recording Panel

```
┌─────────────────────────────────┐
│ ● RECORDING: Duna Explorer      │
│ ─────────────────────────────── │
│ Duration: 2d 4h 32m             │
│ Events: 12                      │
│ Size: 1.2 MB                    │
│                                 │
│ [■ Stop] [✓ Commit] [✗ Discard] │
└─────────────────────────────────┘
```

### Timeline Viewer (Future)

```
┌───────────────────────────────────────────────────────────────┐
│ TIMELINE                                            [NOW ▼]  │
├───────────────────────────────────────────────────────────────┤
│                                                               │
│ Y1 D1 ──●────────────────●─────────────────────────●───────  │
│         │                │                         │          │
│         Launch           Orbit                     Landing    │
│         Duna Explorer    achieved                  (Duna)     │
│                                                               │
│ Y1 D1 ──●───●────────────────────────────────────────────────│
│         │   │                                                 │
│         │   Landing                                           │
│         Launch (Mun)                                          │
│         Mun Lander                                            │
│                                                               │
│ [<<] [<] ────────────────○──────────────────────── [>] [>>]  │
│           Y1 D15 (current)                                    │
└───────────────────────────────────────────────────────────────┘
```

### Playback Controls

```
┌─────────────────────────────────┐
│ ▶ Duna Explorer (playback)      │
│ ─────────────────────────────── │
│ Status: In transit              │
│ Next event: SOI change (D182)   │
│                                 │
│ [Take Control] [Warp to Event]  │
└─────────────────────────────────┘
```

---

## Stock Integration Patterns

Patterns for integrating with KSP's built-in systems (source: FMRS, KSPCommunityFixes):

### Settings via GameParameters

Use `GameParameters.CustomParameterNode` for mod settings that appear in the KSP difficulty settings menu:

```csharp
public class ParsekSettings : GameParameters.CustomParameterNode
{
    [GameParameters.CustomParameterUI("Enable Recording")]
    public bool enableRecording = true;

    [GameParameters.CustomFloatParameterUI("Orientation Threshold (deg)", minValue = 0.5f, maxValue = 10f)]
    public float orientationThreshold = 2.0f;
}
```

### Localization

Use `Localizer.GetStringByTag()` for all user-facing strings:

```csharp
// In en-us.cfg:
// #Parsek_RecordingStarted = Recording started for <<1>>
string msg = Localizer.Format("#Parsek_RecordingStarted", vesselName);
```

Localization files go in `GameData/Parsek/Localization/en-us.cfg`.

---

## Dependencies

### Required

| Dependency | Purpose | License |
|------------|---------|---------|
| Module Manager | Config patching | CC-BY-SA |
| Harmony | Runtime patching | MIT |
| ClickThroughBlocker | UI click handling | GPL-3.0 |
| ToolbarControl | Toolbar buttons | GPL-3.0 |

**Integration patterns:**
- ClickThroughBlocker: Replace `GUILayout.Window()` with `ClickThruBlocker.GUILayoutWindow()` — drop-in replacement that prevents clicks passing through UI windows to vessels/parts behind them.
- ToolbarControl: Two-phase registration — register at `Startup.Instantly`, then create scene-specific buttons in the appropriate scene callback. Supports both stock and Blizzy toolbar.

### Optional Integration

| Mod | Integration |
|-----|-------------|
| Kerbal Alarm Clock | Auto-create alarms for ghost playback windows |

---

## Development Phases (Roadmap)

### Phase 1: MVP — Current Phase

**Core gameplay (done):**
- [x] Position recording with geographic coordinates (lat/lon/alt per body)
- [x] Kinematic ghost playback (sphere, with map view markers)
- [x] Single recording at a time
- [x] Context-aware merge dialog (Keep Vessel / Recover / Discard)
- [x] Persistence to save game via ScenarioModule
- [x] Scene transition cleanup (`onGameSceneLoadRequested`)
- [x] Basic UI panel (toolbar button)
- [x] Auto-recording on launch and EVA from pad
- [x] SOI change handling during recording and playback

**Vessel persistence (done):**
- [x] Vessel snapshot + deferred spawn at EndUT
- [x] Crew reservation and replacement system
- [x] Resource delta tracking (funds, science, reputation)
- [x] Proximity-aware spawn offset (200m check)
- [x] Duplicate spawn prevention via persistentId
- [x] Dead crew removal from snapshots
- [x] 17 edge cases identified and resolved (see TODO-edge-cases.md)

**Recording & playback mechanics (done):**
- [x] Take control of playback vessel (spawn at ghost position with velocity, crew cleanup)
- [x] Orbital/time-warp recording (save orbit params instead of sampling)
- [x] Ghost as actual vessel model (opaque replica from prefab meshes)
- [x] Adaptive threshold sampling (velocity direction >2deg, speed >5%, 3s backstop)
- [x] ClickThroughBlocker for UI windows
- [x] ToolbarControl for toolbar button
- [x] Krakensbane velocity compensation (`rb_velocityD + Krakensbane.GetFrameVelocity()`)
- [x] Harmony hook on `VesselPrecalculate.CalculatePhysicsStats()` (FlightRecorder + PhysicsFramePatch)

**Ghost visual fidelity (done):**
- [x] Opaque ghost vessels from prefab meshes with original materials
- [x] Part event playback (decoupled subtrees hidden, destroyed parts hidden)
- [x] Real parachute canopy deploy on ghost (semi-deployed animation sampled from prefab)
- [x] Event-driven shroud jettison for ghost engine parts
- [x] External recording files (v3) — bulk data in sidecar files, lightweight .sfs
- [x] Engine FX on ghost vessels (modern EFFECTS + legacy fx_* prefab fallback)

**Remaining for MVP:**
- [x] Fix spawned vessel not selectable in map view / tracking station (career mode limitation)
- [x] Chained recordings — land → EVA → walk → board → fly again as a continuous mission replay
- [x] Seamless ghost handoff between chained segments (vessel ghost ends, EVA ghost begins, vessel ghost resumes)
- [x] Verify and fix edge cases: crew continuity, vessel state across chain boundaries, merge dialog for chained missions

### Phase 2: Ghost Visual Fidelity — New Events

Expand the event recording system to capture more visually significant part state changes. See `docs/research/event-recording-analysis.md` for full analysis.

**Currently recorded (9 event types):**
- [x] Decoupled (subtree hide via `onPartJointBreak`)
- [x] Destroyed (part hide via `onPartDie`)
- [x] ParachuteDeployed / ParachuteCut / ParachuteDestroyed (polled `ModuleParachute`)
- [x] ShroudJettisoned (polled `ModuleJettison`)
- [x] EngineIgnited / EngineShutdown / EngineThrottle (polled `ModuleEngines`, particle FX)

**Tier 1 — High impact, common (priority order):**
- [ ] Deployable parts: solar panel / antenna / radiator deploy & retract (`ModuleDeployablePart` base class — one implementation covers all three)
- [ ] Landing gear deploy / retract (`ModuleWheelDeployment`)
- [ ] Lights on / off (`ModuleLight` — simple bool toggle, emissive material on ghost)
- [ ] Cargo bay / service bay open & close (`ModuleCargoBay` + `ModuleAnimateGeneric` — also unlocks airbrakes and other animated parts)
- [ ] Procedural fairing jettison (`ModuleProceduralFairing` — hard for ghost visuals since geometry is runtime-generated)

**Tier 2 — Moderate impact, situational:**
- [ ] Airbrake deploy (uses `ModuleAnimateGeneric` — free if cargo bays are done)
- [ ] RCS thruster fire (`ModuleRCSFX` — high event frequency, needs throttling)

**Deferred (too complex or low value):**
- Control surface deflection — continuous float, thousands of events per flight, skip
- Docking/undocking — vessel topology change, needs multi-vessel recording
- Robotics (Breaking Ground DLC) — continuous motion, DLC-dependent
- Science experiment deploy — subtle visual, inconsistent across experiments
- Ladder / intake / wheel steering — minimal visual change

### Phase 3: Polish & UX

**Timeline & navigation:**
- [ ] Timeline viewer UI (list recordings, delete individual ones, visual timeline)
- [ ] Timeline navigation — select a point in time to revert/return to
- [ ] Multiple concurrent recordings (playback already supports multiple, needs recording flow + UI)

**Quality of life:**
- [ ] Settings panel (`GameParameters.CustomParameterNode` — toggle auto-record, adjust thresholds)
- [ ] KAC integration (auto-create alarms for ghost playback windows)
- [ ] Two-phase parachute deploy (SEMIDEPLOYED streamer vs DEPLOYED full canopy)

**Already done (moved from earlier phases):**
- [x] Event-based recording (part events: decoupled, destroyed, parachute deployed/cut, engine ignition/shutdown)
- [x] Real parachute canopy on ghost vessels
- [x] Event-driven shroud jettison for ghost vessels
- [x] External recording files (v3 format)
- [x] Engine FX on ghost vessels (modern EFFECTS + legacy fx_* prefab fallback)

### Phase 4: Recording Stats & Export

- [ ] Recording stats — distance travelled, final destination, duration, max altitude, max speed
- [ ] Stats display in timeline viewer (extends Phase 3 timeline UI)
- [ ] Recording export/import — share recordings as standalone files

---

## Scope

Parsek is a **parallel mission replay** mod: record missions, revert, and have them play out as ghosts alongside new missions. The architecture naturally enables use cases like racing your own ghosts, but the mod does not include dedicated racing modes, AI playback, or multiplayer features. Those are gameplay possibilities that emerge from the core recording/playback system.

---

## Design Decisions (Resolved)

1. **Trajectory interpolation:** Linear (`Vector3.Lerp`, `Quaternion.Lerp`) — cubic adds complexity with negligible visual improvement at typical adaptive sample rates.
2. **Ghost vessel rendering:** Opaque replica from prefab meshes with original materials. No shader modification.
3. **SOI transitions:** Body name (`string BodyName`) per TrajectoryFrame. Naturally handles multi-body trajectories.
4. **Recording file size:** External sidecar files (v3) — bulk data in `.prec` and `.craft` files, lightweight metadata in `.sfs`.

---

*Document version: 1.0 — Engine FX on ghosts, external recording files (v3), ghost parachute canopy, shroud jettison*
