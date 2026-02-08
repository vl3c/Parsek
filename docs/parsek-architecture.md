# Parsek: Preliminary Architecture

## Document Status
**Version:** 0.2
**Phase:** Research & Planning (Post-Analysis)
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
│  │ - Handles    │  │ - Merges     │  │ - Milestone pursuit  │  │
│  │   commit/    │  │   recordings │  │   (future)           │  │
│  │   discard    │  │ - Resolves   │  │                      │  │
│  │              │  │   conflicts  │  │                      │  │
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

**Sampling Strategy (MVP):**
- Adaptive threshold-based: record frame if orientation changes > 2deg, velocity direction changes > 2deg, or speed changes > 5% (source: PersistentTrails)
- Staging events: On occurrence (with 0.2s delay for state to settle — source: FMRS)
- SOI changes: On occurrence
- Event detection: GameEvents + polling hybrid for redundant reliable detection (source: StageRecovery)

**Sampling Strategy (Full Vision):**
- Milestone events (orbit achieved, landing, etc.)
- Maneuver node executions
- Resource states at key moments

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

**Event Types (Full Vision):**

```csharp
public class MilestoneEvent : TimelineEvent
{
    public MilestoneType Type { get; }  // Orbit, Landing, Rendezvous, etc.
    public MilestoneParameters Parameters { get; }
}

public class ManeuverEvent : TimelineEvent
{
    public Vector3d DeltaV { get; }
    public double BurnDuration { get; }
}

public class InteractionEvent : TimelineEvent
{
    // When player takes control
    public bool PlayerTookControl { get; }
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

| Mode | Description | MVP | Full |
|------|-------------|-----|------|
| Kinematic | Direct position/rotation setting | ✓ | ✓ |
| On-Rails | KSP orbital mechanics | | ✓ |
| Adaptive | Milestone pursuit with MechJeb | | ✓ |

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
    public void ReleaseControl();  // Return to playback (future feature)
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

Using KSP's `ScenarioModule` system:

```csharp
[KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, 
             GameScenes.TRACKINGSTATION, GameScenes.SPACECENTER)]
public class ParsekScenario : ScenarioModule
{
    public override void OnSave(ConfigNode node)
    {
        // Save timeline state
        ConfigNode timelineNode = node.AddNode("TIMELINE");
        MainTimeline.Instance.Save(timelineNode);
        
        // Save pending recordings
        ConfigNode recordingsNode = node.AddNode("RECORDINGS");
        foreach (var rec in pendingRecordings)
            rec.Save(recordingsNode.AddNode("RECORDING"));
    }
    
    public override void OnLoad(ConfigNode node)
    {
        // Restore timeline
        MainTimeline.Instance.Load(node.GetNode("TIMELINE"));
        
        // Restore recordings
        // ...
    }
}
```

### Recording File Format

External files for large recordings (optional):

```
GameData/Parsek/Recordings/
├── {save-name}/
│   ├── recording-{guid}.parsek
│   ├── recording-{guid}.parsek
│   └── index.json
```

**.parsek format:**
```json
{
  "version": "1.0",
  "recordingId": "guid",
  "vesselName": "Duna Explorer",
  "startUT": 12345.67,
  "endUT": 98765.43,
  "vesselSnapshot": "<ConfigNode as string>",
  "trajectory": [
    {"ut": 12345.67, "lat": -0.0972, "lon": -74.5575, "alt": 77.3, "rot": [x,y,z,w], "vel": [x,y,z], "body": "Kerbin"},
    ...
  ],
  "events": [
    {"type": "Staging", "ut": 12350.00, "stage": 1},
    ...
  ]
}
```

---

## Conflict Resolution

### Resource Conflicts

When two recordings need the same resource:

```csharp
public class ConflictResolver
{
    public ConflictResolution Resolve(
        MissionRecording existing,
        MissionRecording incoming)
    {
        // Check launchpad conflicts
        if (SharesLaunchpad(existing, incoming))
        {
            if (existing.StartUT < incoming.StartUT)
                return ConflictResolution.DelayIncoming;
            else
                return ConflictResolution.RejectIncoming;
        }
        
        // Check resource conflicts
        // (future: funds, parts, kerbals)
        
        return ConflictResolution.NoConflict;
    }
}
```

### Interaction Handling

When player takes control of playback vessel:

```csharp
public void OnPlayerTakesControl(PlaybackVessel pv)
{
    // 1. Stop playback for this vessel
    pv.StopPlayback();
    
    // 2. Convert to normal vessel
    pv.KSPVessel.vesselType = VesselType.Ship;
    
    // 3. Remove future events for this vessel
    MainTimeline.Instance.RemoveFutureEvents(
        pv.RecordingId, 
        Planetarium.GetUniversalTime()
    );
    
    // 4. Player now responsible for vessel
    FlightGlobals.SetActiveVessel(pv.KSPVessel);
}
```

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
| Kerbal Alarm Clock | Auto-create alarms for events |
| MechJeb | Adaptive playback execution |
| kOS | Script-based milestone definition |

---

## Development Phases

### Phase 1: MVP — Current Phase

**Core gameplay (done):**
- [x] Position recording with geographic coordinates (lat/lon/alt per body)
- [x] Kinematic ghost playback (sphere, with map view markers)
- [x] Single recording at a time
- [x] Context-aware merge dialog (Keep Vessel / Recover / Discard)
- [x] Persistence to save game via ScenarioModule
- [x] Scene transition cleanup (`onGameSceneLoadRequested`)
- [x] Basic UI panel (Alt+P toggle)
- [x] Auto-recording on launch and EVA from pad
- [x] SOI change handling during recording and playback

**Vessel persistence (done, beyond original MVP scope):**
- [x] Vessel snapshot + deferred spawn at EndUT
- [x] Crew reservation and replacement system
- [x] Resource delta tracking (funds, science, reputation)
- [x] Proximity-aware spawn offset (200m check)
- [x] Duplicate spawn prevention via persistentId
- [x] Dead crew removal from snapshots
- [x] 17 edge cases identified and resolved (see TODO-edge-cases.md)

**Remaining for MVP release:**
- [ ] Take control of playback vessel
- [x] Orbital/time-warp recording (save orbit params instead of sampling)
- [ ] Ghost as actual vessel model (replace sphere)
- [ ] Adaptive threshold sampling (currently fixed 0.5s interval)
- [ ] ClickThroughBlocker for UI windows
- [ ] ToolbarControl for toolbar button

**Deferred (nice-to-have, not blocking release):**
- [ ] Krakensbane velocity compensation
- [ ] Harmony hook on `VesselPrecalculate.CalculatePhysicsStats()`
- [ ] IgnoreGForces(240) positioning (needed when ghost becomes a vessel)
- [ ] `GameParameters.CustomParameterNode` for settings
- [ ] Localization infrastructure (en-us.cfg)

### Phase 2: Core Features

**Features:**
- [ ] Multiple concurrent recordings (timeline playback already supports multiple)
- [ ] Event-based recording (staging, maneuvers as discrete events)
- [ ] Timeline viewer UI
- [ ] Conflict detection
- [ ] KAC integration

### Phase 3: Advanced

**Features:**
- [ ] Milestone-based recording
- [ ] Adaptive playback (MechJeb integration)
- [ ] Graceful degradation on interaction
- [ ] Construction time integration
- [ ] AI agency recordings (space race mode)

### Phase 4: Multiplayer Foundation (Future)

**Features:**
- [ ] Recording export/import
- [ ] Merge conflict resolution
- [ ] Shared timeline support

---

## Risk Assessment

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| Performance with many playbacks | High | Medium | Spatial culling, LOD, on-rails when possible |
| Save file corruption | Critical | Low | Backup systems, validation, separate files |
| KSP version incompatibility | Medium | Medium | Abstract KSP APIs, Harmony patches |
| Physics issues with ghost vessels | Medium | High | Use debris type, disable collisions by default |
| Complex recording edge cases | Medium | High | Extensive testing, fail-safe defaults |
| Krakensbane velocity drift | High | High | Record true velocity (`rb_velocityD` + frame shift) |
| Packed vessel manipulation | Critical | Medium | Always check `vessel.packed` before `SetPosition` |
| Scene transition memory leaks | Medium | High | Cleanup in `onGameSceneLoadRequested` |
| Save/load race condition | Critical | Medium | Async coordination pattern (wait for `onGameStateSaved`) |
| Time warp ghost desync | Medium | High | Pause/destroy ghosts during physics warp |

---

## Open Questions (Resolved)

1. **Trajectory interpolation:** Linear (`Vector3.Lerp`, `Quaternion.Lerp`) is sufficient for MVP. Proven by PersistentTrails — cubic adds complexity with negligible visual improvement at typical sample rates.
2. **Ghost vessel rendering:** Same model for MVP (use VesselSnapshot to spawn identical vessel). Simplified/transparent rendering deferred to Phase 2.
3. **SOI transitions:** Store body name (`string BodyName`) per TrajectoryFrame. Record body change as a discrete `SOIChangeEvent`. This naturally handles multi-body trajectories.
4. **Recording file size:** Hybrid approach — metadata and event list stored in save game via ScenarioModule; large trajectory data stored in external `.parsek` files under `GameData/Parsek/Recordings/`.
5. **Multiplayer architecture:** Deferred to Phase 4. Recording export/import is the foundation — design for file-based sharing first, network layer later.

---

## Next Steps

1. ~~**Set up development environment**~~ (DONE)
   - KSP 1.12.5 local instance configured
   - dotnet SDK-style project with auto-deploy

2. ~~**Create proof-of-concept**~~ (DONE)
   - Spike: recording vessel position + playback with green sphere
   - Verified feasibility of kinematic replay

3. ~~**Study reference mods**~~ (DONE)
   - Analyzed 8 mods: FMRS, PersistentTrails, KSPCommunityFixes, ClickThroughBlocker, ToolbarControl, StageRecovery, VesselMover, KerbalAlarmClock
   - Findings incorporated into this architecture document

4. ~~**Implement core recording + playback**~~ (DONE)
   - Geographic coordinate TrajectoryFrame with SOI support
   - Timeline persistence via ScenarioModule
   - Context-aware merge dialog with vessel persistence
   - Crew reservation/replacement system
   - Resource delta tracking
   - Map view ghost markers
   - 17 edge cases resolved

5. **Complete MVP for release**
   - ~~Orbital/time-warp recording strategy~~ (DONE — hybrid OrbitSegment recording)
   - Take control of playback vessel
   - Ghost as vessel model (replace sphere)
   - ClickThroughBlocker + ToolbarControl integration
   - Adaptive sampling for maneuvers

---

*Document version: 0.3 — Updated to reflect current implementation status*
