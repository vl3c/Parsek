# Parsek: Preliminary Architecture

## Document Status
**Version:** 0.1 (Draft)  
**Phase:** Research & Planning  
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
    
    // Sampling configuration
    public float PositionSampleInterval = 1.0f;  // seconds
    public float OrientationSampleInterval = 0.5f;
    
    // Recording control
    public void StartRecording();
    public void StopRecording();
    public void CommitRecording();
    public void DiscardRecording();
    
    // Called every physics frame when recording
    private void SampleState();
    
    // Event handlers
    private void OnStaging(int stage);
    private void OnPartDecouple(Part part);
    private void OnVesselSOIChanged(Vessel vessel, CelestialBody body);
}
```

**Sampling Strategy (MVP):**
- Position: Every 1 second (configurable)
- Orientation: Every 0.5 seconds
- Staging events: On occurrence
- SOI changes: On occurrence

**Sampling Strategy (Full Vision):**
- Adaptive sampling based on acceleration
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
    public Vector3d Position;      // World space or body-relative
    public QuaternionD Rotation;
    public Vector3d Velocity;      // For interpolation
    public CelestialBody RefBody;  // Reference frame
}

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
    private void UpdatePlaybackVessel(PlaybackVessel pv, double ut);
    private void DespawnPlaybackVessel(PlaybackVessel pv);
    
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
    {"ut": 12345.67, "pos": [x,y,z], "rot": [x,y,z,w], "ref": "Kerbin"},
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

## Dependencies

### Required

| Dependency | Purpose | License |
|------------|---------|---------|
| Module Manager | Config patching | CC-BY-SA |
| Harmony | Runtime patching | MIT |

### Recommended

| Dependency | Purpose | License |
|------------|---------|---------|
| ClickThroughBlocker | UI click handling | GPL-3.0 |
| ToolbarController | Toolbar buttons | GPL-3.0 |

### Optional Integration

| Mod | Integration |
|-----|-------------|
| Kerbal Alarm Clock | Auto-create alarms for events |
| MechJeb | Adaptive playback execution |
| kOS | Script-based milestone definition |

---

## Development Phases

### Phase 1: MVP (Target: 4-6 weeks)

**Features:**
- [ ] Basic recording (position + staging)
- [ ] Kinematic playback (ghost vessels)
- [ ] Single recording at a time
- [ ] Simple commit/discard UI
- [ ] Take control functionality
- [ ] Persistence to save game

**Technical Tasks:**
- [ ] Project setup with dependencies
- [ ] MissionRecorder implementation
- [ ] TrajectoryFrame sampling
- [ ] PlaybackEngine (kinematic mode)
- [ ] Basic UI panel
- [ ] Save/load integration

### Phase 2: Core Features (Target: 2-3 months)

**Features:**
- [ ] Multiple concurrent recordings
- [ ] Event-based recording (not just trajectory)
- [ ] Timeline viewer UI
- [ ] Conflict detection
- [ ] KAC integration

### Phase 3: Advanced (Target: 3-6 months)

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

---

## Open Questions

1. **Trajectory interpolation:** Linear, cubic, or physics-based?
2. **Ghost vessel rendering:** Same model or simplified?
3. **SOI transitions:** Record position per-body or transform?
4. **Recording file size:** Embed in save or external files?
5. **Multiplayer architecture:** Peer-to-peer or server-based?

---

## Next Steps

1. **Set up development environment**
   - KSP 1.12.5 install
   - Visual Studio / Rider configuration
   - Mod project template

2. **Create proof-of-concept**
   - Basic recording of vessel position
   - Simple playback loop
   - Verify feasibility

3. **Study FMRS and Persistent Trails code**
   - Understand save point mechanism
   - Understand replay implementation

4. **Implement MVP incrementally**
   - Start with recording
   - Add playback
   - Add UI
   - Add persistence

---

*Document version: 0.1 — Subject to significant revision as development progresses*
