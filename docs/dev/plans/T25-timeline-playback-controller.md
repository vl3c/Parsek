# T25: Ghost Playback Engine Extraction

**Status: COMPLETED** (March 2026, 31 commits, PR #84)

| Component | Planned | Actual |
|-----------|---------|--------|
| GhostPlaybackEngine | ~2000 lines | 1553 lines |
| ParsekPlaybackPolicy | ~400 lines | 192 lines |
| IPlaybackTrajectory | ~50 lines | 48 lines |
| IGhostPositioner | ~30 lines | 52 lines |
| GhostPlaybackEvents | ~60 lines | 169 lines |
| ParsekFlight reduction | 9900→7200 | 9900→8657 (forwarding properties add ~500 lines of tech debt) |
| New tests | — | 109 tests |

## Context

ParsekFlight.cs is ~9900 lines. The `#region Timeline Auto-Playback` (lines 5895–8337) is the largest region at **2443 lines** (~25% of the file). It contains 38 methods covering ghost spawn/destroy lifecycle, per-frame positioning, loop playback, overlap playback, resource deltas, reentry FX, explosion FX, deferred spawns, soft caps, and watch camera.

The deferred items doc (D20) identified this as the highest-impact extraction opportunity: ParsekFlight would shrink from ~9900 to ~7500 lines. It was previously blocked by cross-region field coupling — primarily `ghostStates` (33 references across 8 regions).

### Architectural Direction

Per `parsek-architectural-direction-of-flight-recorder-refactor.txt`, the ghost recording and playback system will **eventually become a standalone mod**. This refactor is where we prepare the code so the eventual extraction is a clean move rather than a painful untangling. Concretely:

1. **Interface for trajectory data** — the ghost renderer accesses trajectory/visual data through `IPlaybackTrajectory`, not `Recording` directly. Recording carries tree linkage, resource deltas, spawn tracking, crew data — none of which the renderer uses.
2. **Two layers: playback mechanics vs policy** — playback mechanics = creating/positioning/destroying ghost GameObjects, firing part events, managing zone transitions and soft caps. Policy = deciding whether a ghost should exist, what happens when playback ends, whether to spawn a real vessel. These must be separate classes.
3. **Event-driven lifecycle** — ghost created/destroyed/playback-completed are events that policy subscribes to, not hardwired calls into spawn/resource code.
4. **Visual vs identity SegmentEvents** — `PartDestroyed`/`PartRemoved`/`PartAdded` affect rendering; `ControllerChange`/`CrewLost`/etc. are policy-only. The renderer filters to visual events without knowing what a "controller" is.
5. **Segment group concept** — a staged rocket becoming multiple parallel ghosts at split UT should be expressible without walking RecordingTree.
6. **Resource fields off the hot path** — the renderer never reads `TrajectoryPoint.funds/science/reputation`. Resource replay is policy, not rendering.
7. **Game actions zero dependency on ghosts** — already confirmed true.
8. **Mesh construction generalizable** — `BuildTimelineGhostFromSnapshot` is already a clean entry point; future non-vessel content paths can be added alongside it.

## Design Goals

1. **Clean mod boundary** — the playback engine class should be extractable into a separate assembly with zero references to `Recording`, `RecordingTree`, `BranchPoint`, `ChainId`, `ParsekScenario`, or any Parsek-specific type
2. **IPlaybackTrajectory interface** — the renderer only sees trajectory and visual data through this interface
3. **Event-driven lifecycle** — ghost spawned/destroyed/completed/loop-restarted are events, not direct calls
4. **Comprehensive logging** — every lifecycle transition, every spawn/destroy, every event fired, logged with subsystem tags
5. **Testable** — all pure logic methods `internal static`, events testable via mock subscribers, log assertions for lifecycle verification
6. **Enable downstream dedup** — D2 (commit-pattern), D5 (frame application), D8 (UpdateTimelinePlayback decomposition) become feasible

## Current Architecture

```
ParsekFlight (MonoBehaviour, 9900 lines)
  ├── State: ~85 instance fields
  ├── Unity Lifecycle: Update → UpdateTimelinePlayback, LateUpdate → ghostPosEntries
  ├── Scene Change: event handlers that read ghostStates for camera/anchor cleanup
  ├── Recording: chain commit methods
  ├── Manual Playback: separate preview ghost
  ├── Timeline Auto-Playback: 2443 lines, 38 methods ← EXTRACT
  ├── Camera Follow: reads ghostStates/overlapGhosts for camera targeting
  ├── Zone Rendering: called from TAP
  ├── Ghost Positioning: shared by manual playback + TAP
  └── Utilities: proximity check iterates ghostStates

GhostPlaybackLogic (static)   — part event application, FX control, policy queries
GhostVisualBuilder (static)   — ghost mesh/FX construction
GhostPlaybackState (class)    — per-ghost mutable state bag
GhostTypes.cs                 — info classes (EngineGhostInfo, etc.)
```

## Target Architecture

```
┌─────────────────────────────────────────────────────────┐
│  ParsekFlight (MonoBehaviour, ~7200 lines)              │
│  "Policy layer" — Parsek-specific orchestration         │
│                                                         │
│  ├── Owns: GhostPlaybackEngine engine                   │
│  ├── Subscribes to engine.OnGhostCreated/Destroyed/etc  │
│  ├── Update() → engine.UpdatePlayback(trajectories)     │
│  ├── Scene Change → engine.DestroyGhost(i)              │
│  ├── Camera Follow → engine.TryGetGhostState(i, ...)   │
│  ├── Ghost Positioning: stays (shared with preview)     │
│  ├── Policy: spawn decisions, resource deltas, chain    │
│  │   walking, watch mode, deferred spawn queue          │
│  └── Adapts Recording → IPlaybackTrajectory             │
│                                                         │
│  ParsekPlaybackPolicy (new, ~400 lines)                 │
│  "Event subscriber" — reacts to engine lifecycle events  │
│  ├── OnPlaybackCompleted → spawn real vessel or defer   │
│  ├── OnPlaybackCompleted → apply resource deltas        │
│  ├── OnGhostDestroyed → cleanup notifications           │
│  └── OnLoopRestarted → camera anchor management         │
└─────────────────────────────────────────────────────────┘
          │ owns            │ subscribes
          ▼                 ▼
┌─────────────────────────────────────────────────────────┐
│  GhostPlaybackEngine (new, ~2000 lines)                 │
│  "Playback mechanics" — future standalone mod core      │
│                                                         │
│  ├── Owns: ghostStates, overlapGhosts, loopPhaseOffsets │
│  ├── Owns: activeExplosions, soft cap state             │
│  ├── Input: List<IPlaybackTrajectory> + config          │
│  ├── Per-frame: position ghosts, apply part events,     │
│  │   manage zone transitions, reentry FX, explosions    │
│  ├── Loop: spawn/destroy on cycle boundaries,           │
│  │   overlap ghost management                           │
│  ├── Events: OnGhostCreated, OnGhostDestroyed,         │
│  │   OnPlaybackCompleted, OnLoopRestarted               │
│  ├── Query API: HasGhost, GhostCount, TryGetGhost, ... │
│  └── NO references to: Recording, RecordingTree,        │
│       BranchPoint, ChainId, ParsekScenario, resource $  │
│                                                         │
│  Depends on: IPlaybackTrajectory, IGhostPositioner,     │
│              GhostPlaybackState, GhostPlaybackLogic,    │
│              GhostVisualBuilder, GhostSoftCapManager    │
└─────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────┐
│  IPlaybackTrajectory (new interface)                    │
│  "What the renderer needs" — 20 fields from Recording   │
│                                                         │
│  Trajectory: Points, OrbitSegments, TrackSections,      │
│              StartUT, EndUT, FormatVersion               │
│  Events:     PartEvents, FlagEvents                     │
│  Visuals:    GhostSnapshot, VesselSnapshot, VesselName  │
│  Loop:       LoopPlayback, LoopInterval, LoopTimeUnit,  │
│              LoopAnchorVesselId                          │
│  Terminal:   TerminalState, SurfacePos                   │
│  Hints:      PlaybackEnabled, IsDebris                  │
│                                                         │
│  Recording implements IPlaybackTrajectory               │
│  (Future: ContentPackTrajectory also implements it)     │
└─────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────┐
│  IGhostPositioner (new interface)                       │
│  "How to place a ghost in the world"                    │
│                                                         │
│  InterpolateAndPosition(index, traj, state, ut, sup)    │
│  InterpolateRelative(index, traj, state, ut, anchor)    │
│  PositionAtPoint(index, traj, state, point)             │
│  PositionAtSurface(index, traj, state)                  │
│  PositionFromOrbit(index, traj, state, ut)              │
│  PositionLoop(index, traj, state, ut, suppressFx)       │
│  CreateSphere(name, index) → GameObject                 │
│  ApplyZoneRendering(index, state)                       │
│                                                         │
│  ParsekFlight implements IGhostPositioner               │
│  (shares positioning with manual playback)              │
└─────────────────────────────────────────────────────────┘
```

## IPlaybackTrajectory Interface

The renderer needs exactly 20 fields from Recording. The remaining 57 are policy-only.

```csharp
/// <summary>
/// Trajectory and visual data for ghost playback.
/// The ghost playback engine accesses recordings only through this interface.
/// Recording implements this. Future content pack trajectories will too.
/// </summary>
internal interface IPlaybackTrajectory
{
    // === Trajectory data ===
    List<TrajectoryPoint> Points { get; }
    List<OrbitSegment> OrbitSegments { get; }
    List<TrackSection> TrackSections { get; }
    double StartUT { get; }
    double EndUT { get; }
    int RecordingFormatVersion { get; }

    // === Part/flag events (visual only) ===
    List<PartEvent> PartEvents { get; }
    List<FlagEvent> FlagEvents { get; }

    // === Visual snapshots ===
    ConfigNode GhostVisualSnapshot { get; }
    ConfigNode VesselSnapshot { get; }  // fallback for ghost mesh building
    string VesselName { get; }

    // === Loop configuration ===
    bool LoopPlayback { get; }
    double LoopIntervalSeconds { get; }
    LoopTimeUnit LoopTimeUnit { get; }
    uint LoopAnchorVesselId { get; }

    // === Terminal state (for explosion FX) ===
    TerminalState? TerminalStateValue { get; }

    // === Surface hold ===
    SurfacePosition? SurfacePos { get; }

    // === Rendering hints ===
    bool PlaybackEnabled { get; }
    bool IsDebris { get; }
}
```

### What Recording exposes through IPlaybackTrajectory vs what stays hidden

**Through the interface (20 fields):** Points, OrbitSegments, TrackSections, StartUT, EndUT, RecordingFormatVersion, PartEvents, FlagEvents, GhostVisualSnapshot, VesselSnapshot, VesselName, LoopPlayback, LoopIntervalSeconds, LoopTimeUnit, LoopAnchorVesselId, TerminalStateValue, SurfacePos, PlaybackEnabled, IsDebris.

**Hidden from renderer (57 fields):** RecordingId, TreeId, ChainId, ChainIndex, ChainBranch, ParentRecordingId, EvaCrewName, SegmentEvents, Controllers, RecordingGroups, SegmentPhase, SegmentBodyName, Hidden, all terminal orbit fields, all ghost geometry probe fields, all rewind/resource fields, all spawn tracking fields, ParentBranchPointId, ChildBranchPointId, ExplicitStartUT/EndUT, CachedStats, AntennaSpecs, VesselPersistentId, DistanceFromLaunch, VesselSituation, MaxDistanceFromLaunch, etc.

## IGhostPositioner Interface

Replaces the delegate struct. Cleaner, discoverable, and the interface itself can move to the standalone mod later.

```csharp
/// <summary>
/// Positions ghost GameObjects in the world. Implemented by the host
/// (ParsekFlight for flight scene, ParsekKSC for KSC scene).
/// The engine calls these methods but doesn't know how positioning works.
/// </summary>
internal interface IGhostPositioner
{
    void InterpolateAndPosition(int index, IPlaybackTrajectory traj,
        GhostPlaybackState state, double ut, bool suppressFx);
    void InterpolateAndPositionRelative(int index, IPlaybackTrajectory traj,
        GhostPlaybackState state, double ut, bool suppressFx,
        IPlaybackTrajectory anchorTraj, double anchorUT);
    void PositionAtPoint(int index, IPlaybackTrajectory traj,
        GhostPlaybackState state, TrajectoryPoint point);
    void PositionAtSurface(int index, IPlaybackTrajectory traj,
        GhostPlaybackState state);
    void PositionFromOrbit(int index, IPlaybackTrajectory traj,
        GhostPlaybackState state, double ut);
    void PositionLoop(int index, IPlaybackTrajectory traj,
        GhostPlaybackState state, double ut, bool suppressFx);
    GameObject CreateSphere(string name, int index);
    void ApplyZoneRendering(int index, GhostPlaybackState state);
    void ClearOrbitCache();
}
```

## Lifecycle Events

The engine fires events. Policy subscribes. No hardwired calls from engine into Parsek code.

```csharp
/// <summary>
/// Fired when the engine creates, destroys, or completes playback of a ghost.
/// Policy layer subscribes to react (spawn vessel, apply resources, manage camera, etc.)
/// </summary>
internal class GhostLifecycleEvent
{
    public int Index { get; }
    public IPlaybackTrajectory Trajectory { get; }
    public GhostPlaybackState State { get; }
}

internal class PlaybackCompletedEvent : GhostLifecycleEvent
{
    public bool HasPoints { get; }          // false = empty trajectory
    public bool GhostWasActive { get; }     // was a ghost GO visible?
    public TrajectoryPoint LastPoint { get; } // final position (for spawn)
}

internal class LoopRestartedEvent : GhostLifecycleEvent
{
    public int PreviousCycleIndex { get; }
    public int NewCycleIndex { get; }
    public bool ExplosionFired { get; }
    public Vector3 ExplosionPosition { get; }
}

internal class OverlapExpiredEvent : GhostLifecycleEvent
{
    public int CycleIndex { get; }
    public bool ExplosionFired { get; }
    public Vector3 ExplosionPosition { get; }
}

internal class GhostPlaybackEngine
{
    // === Lifecycle events ===
    internal event Action<GhostLifecycleEvent> OnGhostCreated;
    internal event Action<GhostLifecycleEvent> OnGhostDestroyed;
    internal event Action<PlaybackCompletedEvent> OnPlaybackCompleted;
    internal event Action<LoopRestartedEvent> OnLoopRestarted;
    internal event Action<OverlapExpiredEvent> OnOverlapExpired;
    // ...
}
```

### What moves to events vs what stays in the engine

| Currently in TAP | Engine or Event? | Why |
|---|---|---|
| Position ghost at final point | **Engine** | Visual mechanics |
| Fire explosion FX | **Engine** | Visual mechanics |
| Decide to spawn real vessel | **Event** → policy subscriber | Policy: spawn decisions |
| Call `SpawnVesselOrChainTip` | **Event** → policy subscriber | Policy: spawn routing |
| Queue deferred spawn during warp | **Event** → policy subscriber | Policy: warp spawn queue |
| Apply resource deltas | **Event** → policy subscriber | Policy: economy replay |
| Exit/transfer watch mode | **Event** → policy subscriber | Policy: camera management |
| Camera hold timer on explosion | **Event** → policy subscriber | Policy: camera UX |
| Spawn death loop detection | **Event** → policy subscriber | Policy: safety valve |
| Soft cap evaluation | **Engine** | Rendering mechanics (ghost count limit) |
| Zone rendering transitions | **Engine** | Rendering mechanics |
| Reentry FX update | **Engine** | Visual mechanics |
| Part event application | **Engine** | Visual mechanics |

## Resource Deltas: Stays in Policy

`ApplyResourceDeltas` reads `TrajectoryPoint.funds/science/reputation` and calls KSP `Funding.Instance.AddFunds` etc. This is entirely policy — the renderer doesn't need it. It moves to the policy subscriber that handles `OnPlaybackCompleted`.

The engine's per-frame update will track `lastAppliedIndex` internally (needed for knowing which trajectory point we're at), and the `PlaybackCompletedEvent` provides the range of points traversed so the policy layer can compute deltas.

Actually — resource deltas are applied **per-frame** (not just at completion) for smooth economy replay. The engine needs to report "I advanced from point X to point Y this frame" so the policy layer can apply deltas. This is best modeled as a per-frame callback rather than a lifecycle event:

```csharp
/// <summary>
/// Called each frame per active ghost with the point index range traversed.
/// Policy layer uses this for resource delta application.
/// </summary>
internal event Action<int, IPlaybackTrajectory, int, int> OnPointsTraversed;
// (index, trajectory, fromPointIndex, toPointIndex)
```

## Watch State

Watch state (`watchedRecordingIndex`, `watchedOverlapCycleIndex`, `overlapRetargetAfterUT`, `overlapCameraAnchor`, `watchEndHoldUntilUT`) stays in ParsekFlight. It's camera UX policy, not playback mechanics.

The engine doesn't know about watch mode. It fires events. The policy layer's event handlers check watch state and make camera decisions. The engine exposes query methods so Camera Follow can look up ghost state.

For the few places where the engine currently checks `watchedRecordingIndex` (e.g., "don't hide watched ghost during soft cap"), the engine receives this as a `protectedIndex` parameter:

```csharp
internal void EvaluateGhostSoftCaps(int protectedIndex);
```

The engine doesn't know *why* that index is protected — just that it shouldn't be culled.

## GhostPlaybackEngine API

```csharp
/// <summary>
/// Core ghost playback engine. Manages ghost GameObjects, per-frame positioning,
/// part event application, loop/overlap playback, zone transitions, and soft caps.
///
/// This class has no knowledge of Recording, RecordingTree, BranchPoint, chain IDs,
/// resource deltas, vessel spawning, or any Parsek-specific concept. It renders
/// trajectories as visual ghosts and nothing more.
///
/// Future: this class becomes the core of the standalone ghost playback mod.
/// </summary>
internal class GhostPlaybackEngine
{
    // === Construction ===
    internal GhostPlaybackEngine(IGhostPositioner positioner, Action<IEnumerator> startCoroutine);

    // === Per-frame update ===
    /// <summary>
    /// Main update loop. Iterates all active trajectories, spawns/positions/destroys
    /// ghosts, fires lifecycle events. Called from host's Update().
    /// </summary>
    internal void UpdatePlayback(
        IReadOnlyList<IPlaybackTrajectory> trajectories,
        double currentUT,
        float warpRate,
        bool isMapMode,
        Vector3d activeVesselPos,
        int protectedIndex);          // watch mode: don't soft-cap this ghost

    // === Ghost lifecycle (called by host for external triggers) ===
    internal void DestroyGhost(int index);
    internal void DestroyAllGhosts();
    internal void DestroyAllOverlapGhosts(int index);

    // === Ghost chain positioning (called by host) ===
    internal void SetGhostChains(VesselGhoster ghoster, Dictionary<uint, GhostChain> chains);
    internal void ClearGhostChains();
    internal void PositionChainGhosts(double currentUT, float warpRate, Vector3d activeVesselPos);

    // === Delete with reindex ===
    internal void DeleteGhost(int index);

    // === Query API (for camera, scene change, UI, proximity) ===
    internal int GhostCount { get; }
    internal bool HasGhost(int index);
    internal bool HasActiveGhost(int index);
    internal bool TryGetGhostState(int index, out GhostPlaybackState state);
    internal bool TryGetGhostPivot(int index, out Transform pivot);
    internal bool IsGhostWithinVisualRange(int index);
    internal bool IsGhostOnSameBody(int index);
    internal string GetGhostBodyName(int index);
    internal Dictionary<int, GameObject> GetGhostGameObjects();
    internal bool TryGetOverlapGhosts(int index, out List<GhostPlaybackState> overlaps);
    internal IReadOnlyDictionary<uint, GhostChain> ActiveGhostChains { get; }

    // === Loop queries ===
    internal double GetLoopIntervalSeconds(IPlaybackTrajectory traj);
    internal bool TryComputeLoopPlaybackUT(int index, IPlaybackTrajectory traj,
        double currentUT, out double loopUT);

    // === Reentry FX (also used by preview playback) ===
    internal void UpdateReentryFx(GhostPlaybackState state, int index,
        IPlaybackTrajectory traj, double currentUT, bool suppressFx);

    // === Proximity iteration (for Real Spawn Control) ===
    internal IEnumerable<(int index, Vector3 position)> GetActiveGhostPositions();

    // === Explosion tracking ===
    internal void TrackExplosion(GameObject explosion);

    // === Lifecycle events ===
    internal event Action<GhostLifecycleEvent> OnGhostCreated;
    internal event Action<GhostLifecycleEvent> OnGhostDestroyed;
    internal event Action<PlaybackCompletedEvent> OnPlaybackCompleted;
    internal event Action<LoopRestartedEvent> OnLoopRestarted;
    internal event Action<OverlapExpiredEvent> OnOverlapExpired;
    internal event Action<int, IPlaybackTrajectory, int, int> OnPointsTraversed;

    // === Teardown ===
    internal void Dispose();
}
```

### What the engine does NOT know about

- `Recording` (only sees `IPlaybackTrajectory`)
- `RecordingTree`, `BranchPoint`, DAG structure
- `ChainId`, `ChainIndex`, `ChainBranch`
- `RecordingId`, `ParentRecordingId`, `EvaCrewName`
- `ParsekScenario`, crew reservation/replacement
- Resource deltas (`funds`, `science`, `reputation` on TrajectoryPoint)
- `VesselSpawner`, vessel spawning/recovery
- `RecordingStore`, committed recordings list
- Deferred spawn queue (`pendingSpawnRecordingIds`)
- Watch mode, camera follow
- `activeChainId`, recording state

### What the engine DOES know about

- `IPlaybackTrajectory` — trajectory points, part events, loop config, terminal state
- `IGhostPositioner` — how to place a ghost in the world
- `GhostPlaybackState` — per-ghost mutable state
- `GhostPlaybackLogic` — part event application, FX control, zone transitions
- `GhostVisualBuilder` — ghost mesh/FX construction
- `GhostSoftCapManager` — ghost count limits
- `TrajectoryMath` — interpolation utilities
- Zone rendering, reentry FX, explosion FX — all visual concerns

## ParsekPlaybackPolicy

New class (~400 lines) that subscribes to engine events and implements Parsek-specific reactions.

```csharp
/// <summary>
/// Subscribes to GhostPlaybackEngine lifecycle events and implements
/// Parsek-specific policy: vessel spawning, resource replay, camera
/// management, deferred spawn queue, spawn-death-loop detection.
/// </summary>
internal class ParsekPlaybackPolicy
{
    private readonly GhostPlaybackEngine engine;
    private readonly ParsekFlight host;  // for camera/spawn/chain access

    // === Deferred spawn state (moved from ParsekFlight) ===
    private readonly HashSet<string> pendingSpawnRecordingIds;
    private string pendingWatchRecordingId;

    internal ParsekPlaybackPolicy(GhostPlaybackEngine engine, ParsekFlight host);

    // Event handlers (subscribed to engine events):
    private void HandlePlaybackCompleted(PlaybackCompletedEvent evt);
    private void HandleGhostDestroyed(GhostLifecycleEvent evt);
    private void HandleLoopRestarted(LoopRestartedEvent evt);
    private void HandleOverlapExpired(OverlapExpiredEvent evt);
    private void HandlePointsTraversed(int index, IPlaybackTrajectory traj, int from, int to);

    // === Deferred spawn flush (called from ParsekFlight.Update) ===
    internal void FlushDeferredSpawns(double currentUT, float warpRate);

    internal void Dispose(); // unsubscribe
}
```

### Policy decisions in event handlers

**HandlePlaybackCompleted:**
- Check `ShouldSpawnAtRecordingEnd` (needs the full `Recording`, not just `IPlaybackTrajectory` — the policy layer casts or uses a lookup)
- If spawn needed + during warp → queue in `pendingSpawnRecordingIds`
- If spawn needed + no warp → call `VesselSpawner.SpawnOrRecoverIfTooClose` or chain tip spawn
- If watching → exit/transfer watch mode
- Camera hold timers for destroyed terminals
- Log all decisions

**HandlePointsTraversed:**
- Compute resource deltas from `TrajectoryPoint.funds/science/reputation`
- Apply via `Funding.Instance.AddFunds` etc.
- Gate on `isRecording` (pause during manual recording)

**HandleGhostDestroyed:**
- Clear proximity notifications for this index
- Log cleanup

**HandleLoopRestarted / HandleOverlapExpired:**
- Camera anchor management
- Watch mode cycle retargeting

## Coupling Analysis — Revised

### Fields that move into GhostPlaybackEngine

| Field | Type | Currently in |
|-------|------|--------------|
| `ghostStates` | `Dictionary<int, GhostPlaybackState>` | ParsekFlight State |
| `overlapGhosts` | `Dictionary<int, List<GhostPlaybackState>>` | ParsekFlight State |
| `loopPhaseOffsets` | `Dictionary<int, double>` | ParsekFlight State |
| `activeExplosions` | `List<GameObject>` | ParsekFlight State |
| `cachedZone1Ghosts` | `List<(int,GhostPriority)>` | ParsekFlight State |
| `cachedZone2Ghosts` | `List<(int,GhostPriority)>` | ParsekFlight State |
| `softCapTriggeredThisFrame` | `bool` | ParsekFlight State |
| `softCapSuppressed` | `HashSet<int>` | ParsekFlight State |
| `loggedGhostEnter` | `HashSet<int>` | ParsekFlight State |
| `loggedReshow` | `HashSet<int>` | ParsekFlight State |
| `vesselGhoster` | `VesselGhoster` | ParsekFlight State |
| `activeGhostChains` | `Dictionary<uint, GhostChain>` | ParsekFlight State |
| `loadedAnchorVessels` | `HashSet<uint>` | ParsekFlight State |
| `loggedRelativeStart` | `HashSet<long>` | ParsekFlight Ghost Positioning |
| `loggedAnchorNotFound` | `HashSet<long>` | ParsekFlight Ghost Positioning |

### Fields that move into ParsekPlaybackPolicy

| Field | Type | Currently in |
|-------|------|--------------|
| `pendingSpawnRecordingIds` | `HashSet<string>` | ParsekFlight State |
| `pendingWatchRecordingId` | `string` | ParsekFlight State |
| `timelineResourceReplayPausedLogged` | `bool` | ParsekFlight State |
| `nearbySpawnCandidates` | `List<NearbySpawnCandidate>` | ParsekFlight State |
| `notifiedSpawnRecordingIds` | `HashSet<string>` | ParsekFlight State |

### Fields that stay in ParsekFlight

| Field | Why |
|-------|-----|
| `watchedRecordingIndex` | Camera UX policy |
| `watchedRecordingId` | Camera UX policy |
| `watchedOverlapCycleIndex` | Camera UX policy |
| `overlapRetargetAfterUT` | Camera UX policy |
| `overlapCameraAnchor` | Camera UX policy |
| `watchEndHoldUntilUT` | Camera UX policy |
| `ghostPosEntries` | Floating-origin correction in LateUpdate (shared with preview) |
| `orbitCache` | Ghost Positioning (shared with preview) |
| `loggedOrbitSegments` | Ghost Positioning (shared with preview) |
| `loggedOrbitRotationSegments` | Ghost Positioning (shared with preview) |
| All chain state (`activeChainId`, etc.) | Chain building policy |
| All recording state | Recording lifecycle |

### Cross-region coupling resolution

| Old coupling | New pattern |
|---|---|
| Scene Change reads `ghostStates` for camera pivot | `engine.TryGetGhostPivot(i, out pivot)` |
| Scene Change calls `DestroyAllTimelineGhosts` | `engine.DestroyAllGhosts()` |
| OnVesselUnloaded checks `ghostStates.ContainsKey` | `engine.HasGhost(i)` |
| Camera Follow reads `ghostStates` for targeting | `engine.TryGetGhostState(i, out state)` |
| Camera Follow reads `overlapGhosts` for cycle tracking | `engine.TryGetOverlapGhosts(i, out overlaps)` |
| Public Accessors return ghost count/map | `engine.GhostCount`, `engine.GetGhostGameObjects()` |
| Utilities iterate ghosts for proximity | `engine.GetActiveGhostPositions()` |
| Manual Playback adds to `activeExplosions` | `engine.TrackExplosion(go)` |
| TAP reads `activeChainId` | Policy layer passes `isActiveChainMember` flag per-trajectory |
| TAP reads `isRecording` | Policy layer gates resource replay in event handler |
| TAP calls `ExitWatchMode` | Policy event handler calls it on ParsekFlight |

## File Layout

```
Source/Parsek/
  IPlaybackTrajectory.cs          ← NEW (~50 lines) interface
  IGhostPositioner.cs             ← NEW (~30 lines) interface
  GhostPlaybackEngine.cs          ← NEW (~2000 lines) playback mechanics
  GhostPlaybackEvents.cs          ← NEW (~60 lines) event types
  ParsekPlaybackPolicy.cs         ← NEW (~400 lines) policy subscriber
  ParsekFlight.cs                 ← SHRINKS to ~7200 lines
  Recording.cs                    ← ADD: implements IPlaybackTrajectory
  (all other files unchanged)
```

## Migration Steps

### Phase 1: Interface + event types (no behavior change)
1. Create `IPlaybackTrajectory.cs` with the 20-field interface
2. Create `IGhostPositioner.cs` with positioning methods
3. Create `GhostPlaybackEvents.cs` with event type classes
4. Make `Recording` implement `IPlaybackTrajectory` (trivial — all fields already exist)
5. Build passes

### Phase 2: Empty engine shell + wiring
1. Create `GhostPlaybackEngine.cs` with constructor, events, empty `UpdatePlayback`
2. Create `ParsekPlaybackPolicy.cs` with constructor, empty event handlers
3. Add `engine` and `policy` fields to ParsekFlight, construct in `Start()`
4. Build passes — engine exists but does nothing

### Phase 3: Move ghost state + lifecycle methods
1. Move `ghostStates`, `overlapGhosts`, `loopPhaseOffsets`, `activeExplosions` into engine
2. Move `SpawnTimelineGhost` → `engine.SpawnGhost` (change `Recording` params to `IPlaybackTrajectory`)
3. Move `DestroyTimelineGhost` → `engine.DestroyGhost`
4. Move `DestroyAllTimelineGhosts` → `engine.DestroyAllGhosts`
5. Move `DestroyGhostResources`, `DestroyOverlapGhostState`, `DestroyAllOverlapGhosts`
6. Move `TriggerExplosionIfDestroyed`, `CleanupActiveExplosions`
7. Add `OnGhostCreated`/`OnGhostDestroyed` event fires
8. Wire ParsekFlight callers to engine methods
9. Build + test after each method moved

### Phase 4: Move main update methods
1. Move `UpdateTimelinePlayback` → `engine.UpdatePlayback`
   - Change `Recording` refs to `IPlaybackTrajectory`
   - Replace spawn/resource/camera calls with event fires
   - Receive trajectory list as parameter instead of reading RecordingStore
2. Move `UpdateLoopingTimelinePlayback`, `UpdateOverlapLoopPlayback`
3. Move `EvaluateGhostSoftCaps`
4. Move `PositionChainGhosts`, `SpawnVesselOrChainTip` (chain tip spawn is policy → event)
5. Build + test after each

### Phase 5: Move policy into ParsekPlaybackPolicy
1. Move spawn decision logic into `HandlePlaybackCompleted`
2. Move resource delta application into `HandlePointsTraversed`
3. Move deferred spawn queue into policy class
4. Move camera management into policy event handlers
5. Build + test

### Phase 6: Move remaining methods + cleanup
1. Move reentry FX methods (keep `internal` — preview playback still calls them)
2. Move loop utility methods
3. Move static pure methods (`FindChainTipForRecording` etc.)
4. Move soft cap fields
5. Update ParsekFlight public accessors to delegate to engine
6. Final build + test

### Phase 7: Verification
1. Verify engine has zero references to `Recording` (only `IPlaybackTrajectory`)
2. Verify engine has zero references to `RecordingTree`, `BranchPoint`, `RecordingStore`
3. Verify engine has zero references to `ParsekScenario`, `VesselSpawner`
4. Verify engine never reads `TrajectoryPoint.funds/science/reputation`
5. Run full test suite (target: 3227+ all pass)
6. Write new tests for engine events and lifecycle logging
7. Update code inventory doc with new line counts
8. Update deferred items: mark D20 done, note D2/D5/D8 unblocked

## Testing Strategy

### Unit tests for GhostPlaybackEngine

Since the engine operates through interfaces, it can be tested with mock implementations:

```csharp
// Mock trajectory for testing
internal class MockTrajectory : IPlaybackTrajectory
{
    public List<TrajectoryPoint> Points { get; set; } = new();
    public double StartUT => Points.Count > 0 ? Points[0].ut : 0;
    public double EndUT => Points.Count > 0 ? Points[Points.Count - 1].ut : 0;
    // ... all 20 fields with test defaults
}

// Mock positioner that records calls
internal class MockPositioner : IGhostPositioner
{
    public List<(int index, double ut)> PositionCalls = new();
    public void InterpolateAndPosition(int index, IPlaybackTrajectory traj,
        GhostPlaybackState state, double ut, bool suppressFx)
    {
        PositionCalls.Add((index, ut));
    }
    // ...
}
```

### Test categories

1. **Lifecycle event tests** — verify `OnGhostCreated`/`OnGhostDestroyed`/`OnPlaybackCompleted` fire at correct times with correct data
2. **Loop restart tests** — verify `OnLoopRestarted` fires with correct cycle indices
3. **Overlap tests** — verify overlap ghosts created/expired correctly, `OnOverlapExpired` fires
4. **Soft cap tests** — verify ghost count limits, protected index respected
5. **Interface isolation tests** — verify engine never casts `IPlaybackTrajectory` to `Recording`
6. **Log assertion tests** — capture `ParsekLog` output, verify every lifecycle transition logged with subsystem tag `[Engine]`

### Log assertions

Every engine method logs. Test pattern:

```csharp
[Fact]
public void SpawnGhost_LogsCreation()
{
    var logLines = new List<string>();
    ParsekLog.TestSinkForTesting = line => logLines.Add(line);

    engine.SpawnGhost(0, trajectory);

    Assert.Contains(logLines, l =>
        l.Contains("[Engine]") && l.Contains("Spawned ghost") && l.Contains("index=0"));
}
```

### Logging subsystem tags

| Class | Tag | Example |
|---|---|---|
| `GhostPlaybackEngine` | `[Engine]` | `[Engine] Spawned ghost index=0 vessel=Flea name=...` |
| `ParsekPlaybackPolicy` | `[Policy]` | `[Policy] PlaybackCompleted index=0, spawning vessel` |
| `ParsekFlight` (remaining) | `[Flight]` | (existing, unchanged) |

## Risks

1. **Event ordering** — events fire during `UpdatePlayback`. If a policy handler modifies engine state (e.g., destroys a ghost in response to an event), the engine's iteration could be affected. Mitigation: engine collects events during iteration, fires them after the loop completes. Document this.

2. **IPlaybackTrajectory casting** — policy layer needs full `Recording` for spawn decisions. It must cast or use a lookup table. This is acceptable — the policy layer knows about `Recording`; only the engine doesn't. Mitigation: `ParsekPlaybackPolicy` maintains a `Dictionary<int, Recording>` index→recording map.

3. **Performance of interface dispatch** — `IPlaybackTrajectory` property access is virtual dispatch vs direct field access. Mitigation: these are not in tight inner loops (trajectory points are accessed by index, not iterated through the interface). The hot path is `Points[i]` which is a direct list access after one virtual call.

4. **VesselGhoster/GhostChain** — these are Parsek-specific types currently. For the standalone mod, they'd need to be generalized. For now, the engine takes them as-is. This is acceptable technical debt — the chain ghost concept may evolve before extraction.

## SegmentEvents Note

Per the architectural direction, visual SegmentEvents (`PartDestroyed`, `PartRemoved`, `PartAdded`) should be distinguishable from identity events. Currently `SegmentEvents` are not consumed by any ghost rendering code — they're only used by `GhostingTriggerClassifier` for chain-walker decisions. The engine doesn't need SegmentEvents at all for now. When segment-group visual transitions (point 5 in the arch doc) are implemented later, the engine will need the visual subset. For now, `IPlaybackTrajectory` deliberately excludes `SegmentEvents`.

## Segment Groups (Future)

Point 5 of the architectural direction describes a "segment group" concept for staged rockets. This is NOT part of T25 — it requires new data structures and is a feature-level change. However, the engine's event-driven architecture supports it: the policy layer would subscribe to `OnPlaybackCompleted` for a parent trajectory and spawn child trajectories via the engine. The engine sees them as independent trajectories that happen to start at the same UT. No tree walking needed.
