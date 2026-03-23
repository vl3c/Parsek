# Task 3: Background Recording Infrastructure

## Workflow

This task follows a multi-stage review pipeline using Opus 4.6 agents, orchestrated by the main session:

1. **Plan** - Opus 4.6 subagent explores the codebase and writes a detailed implementation plan
2. **Plan review** - Fresh Opus 4.6 subagent reviews the plan for correctness, completeness, and risk
3. **Orchestrator review** - Main session reviews the plan with full project context and fixes issues
4. **Implement** - Fresh Opus 4.6 subagent implements the plan
5. **Implementation review** - Fresh Opus 4.6 subagent reviews the implementation and fixes issues
6. **Final review** - Main session reviews the implementation considering the larger architectural context
7. **Commit** - Main session commits the implementation
8. **Next task briefing** - Main session presents the next task, explains its role and how it fits into the overall plan

---

## Plan

### 1. Overview

Task 3 adds continuous recording for background vessels in a recording tree. When the player switches to vessel A, vessel B (now backgrounded) continues to accumulate recording data. This ensures that when the tree is committed and leaf vessels are spawned, every branch has a complete time history - no gaps between the last active recording and the tree's end.

Task 2 introduced the `TransitionToBackground` / `PromoteFromBackground` flow. When a vessel transitions to background, `TransitionToBackground()` (FlightRecorder.cs line 3717) opens an orbit segment and disconnects the Harmony patch. When promoted back, `PromoteRecordingFromBackground` (ParsekFlight.cs line 806) creates a fresh `FlightRecorder` that resumes physics sampling. Between those two events, the background vessel's recording has a single open-ended orbit segment that gets closed on promotion or flush.

This is insufficient for two reasons:

1. **On-rails background vessels** may change SOI, change from orbiting to landed (lithobraking), or be time-warped for extended periods. The single orbit segment from `TransitionToBackground` becomes stale. The recording needs periodic orbit-segment refresh and SOI handling.

2. **Loaded/physics background vessels** (within physics range, `vessel.loaded == true`, with active rigidbodies) are fully simulated by KSP. They may be accelerating, burning engines, deploying parachutes. They deserve the same full-fidelity recording as the active vessel: trajectory points, part events, adaptive sampling.

Task 3 adds a `BackgroundRecorder` class that runs in `ParsekFlight.Update()` and monitors all background vessels in the tree, dispatching to the correct mode based on vessel load state.

---

### 2. Background Recording Modes

#### 2.1 Mode Selection

For each vessel `persistentId` in `activeTree.BackgroundMap`:

| Condition | Mode | Fidelity |
|-----------|------|----------|
| Vessel not found in `FlightGlobals.Vessels` | Skip (vessel may be destroyed or not yet loaded) | - |
| `vessel.loaded == false` (on rails, outside physics range) | **On-rails** | OrbitSegment or SurfacePosition snapshots |
| `vessel.loaded == true` but `vessel.packed == true` (loaded but on rails, e.g. nearby during time warp) | **On-rails** | OrbitSegment or SurfacePosition snapshots |
| `vessel.loaded == true` and `vessel.packed == false` (full physics) | **Loaded/physics** | Full trajectory points, part events, adaptive sampling |

The `vessel.packed` flag is the reliable discriminator. In KSP, `vessel.loaded` means the vessel's GameObjects are instantiated (within ~2.5km), and `vessel.packed` means physics is disabled (on rails). A vessel can be `loaded && packed` during time warp - GameObjects exist but no rigidbody simulation. Only `loaded && !packed` means full physics is running.

#### 2.2 Mode Transitions

Mode transitions are detected on every `Update()` tick by checking the vessel's current `loaded`/`packed` state against the previous state tracked in `BackgroundRecorder`:

| Transition | Action |
|-----------|--------|
| On-rails → Loaded/physics | Close current orbit segment. Initialize loaded-mode state (cache part modules, seed part event tracking). Begin trajectory sampling. |
| Loaded/physics → On-rails | Sample a boundary trajectory point. Open a new orbit segment. Clear loaded-mode state. |
| Any → Vessel not found | Mark as stale. If vessel is confirmed destroyed (one-frame deferred check in Task 6), this recorder will be cleaned up. For now, skip silently. |

---

### 3. On-Rails Background Recording

#### 3.1 Data Captured

For orbiting/sub-orbital vessels:
- `OrbitSegment` from `vessel.orbit` - Keplerian elements (inclination, eccentricity, semiMajorAxis, LAN, argumentOfPeriapsis, meanAnomalyAtEpoch, epoch, bodyName).

For landed/splashed vessels:
- `SurfacePosition` from `vessel.latitude`, `vessel.longitude`, `vessel.altitude`, `vessel.srfRelRotation`, `vessel.mainBody.name`, and `vessel.situation`.
- Landed vessels are static. A single `SurfacePosition` capture at transition time is sufficient. No periodic refresh needed unless SOI change (impossible for landed vessels).

#### 3.2 Orbit Segment Management

The orbit segment logic mirrors the existing `OnVesselGoOnRails` / `OnVesselGoOffRails` / `OnVesselSOIChanged` pattern in FlightRecorder (lines 3561-3656), but adapted for background vessels:

- **Open segment**: When a vessel enters on-rails mode (at transition-to-background time, or when a loaded vessel goes back on rails), capture current `vessel.orbit` as a new `OrbitSegment` with `startUT = now`.
- **Close segment**: When the vessel leaves on-rails mode (promotion, loaded/physics transition, SOI change), set `endUT = now` and append to the recording's `OrbitSegments`.
- **SOI change**: Close current segment, open a new one with the new body's orbit. Subscribe to `GameEvents.onVesselSOIChanged` for background vessels.
- **Periodic refresh**: Not needed for the orbit segment itself - Keplerian elements are constant within an SOI. The existing `FindOrbitSegment` + analytical position calculation handles arbitrary time ranges. However, `ExplicitEndUT` on the tree Recording should be updated periodically so the tree knows the recording is still alive. Update `ExplicitEndUT` every 30 seconds.

#### 3.3 Where the Loop Runs

The on-rails background loop runs in `ParsekFlight.Update()` (not `FixedUpdate`). Rationale:

- `Update()` runs once per frame. For on-rails background data (orbit parameters that change only at SOI boundaries), this is more than sufficient.
- SOI changes are caught by `GameEvents.onVesselSOIChanged` (event-driven), not polling.
- The `ExplicitEndUT` periodic update is low-cost (one double comparison per vessel per frame).
- Using `Update()` keeps all tree management logic in one place (ParsekFlight already runs all tree logic in `Update()`).

#### 3.4 Landed/Splashed Vessels

For landed or splashed background vessels (detected via `vessel.situation == Vessel.Situations.LANDED || vessel.situation == Vessel.Situations.SPLASHED`):

- Capture a `SurfacePosition` once when the vessel enters background.
- Store it in `Recording.SurfacePos` (the field already exists on Recording, line 106 of RecordingStore.cs).
- No orbit segment is opened (there is no orbit).
- Set `ExplicitEndUT` to the current UT (updated periodically).
- If the vessel's situation changes from landed to flying (e.g. physics kicked it off the surface), this will be detected as a mode transition when the vessel loads into physics range.

---

### 4. Loaded/Physics Background Recording

#### 4.1 The Key Insight: PhysicsFramePatch Already Fires for All Vessels

The Harmony postfix on `VesselPrecalculate.CalculatePhysicsStats()` (PhysicsFramePatch.cs) fires for **every vessel in physics range**, not just the active one. This is the critical architectural fact.

Currently, the patch filters to only process the active vessel (PhysicsFramePatch.cs lines 38-53):

```csharp
Vessel v = FlightGlobals.ActiveVessel;
if (v == null) { ... return; }
if (__instance.gameObject != v.gameObject) { ... return; }
ActiveRecorder.OnPhysicsFrame(v);
```

When `__instance.gameObject != v.gameObject`, the patch logs "Skipping physics callback: patch fired for non-active vessel" and returns. **This is exactly where background physics recording hooks in.** Instead of skipping non-active vessels, the patch should check if the vessel is tracked in a background recorder and dispatch to it.

#### 4.2 Modifying PhysicsFramePatch

The `Postfix` method needs a new static field for background recorders and a dispatch path:

```csharp
internal static BackgroundRecorder BackgroundRecorderInstance;

static void Postfix(VesselPrecalculate __instance)
{
    // ... existing ActiveRecorder != null check + dispatch ...

    // Background physics recording for loaded vessels in tree
    if (BackgroundRecorderInstance != null && __instance.gameObject != null)
    {
        // Resolve the vessel from the VesselPrecalculate instance.
        // Cannot use FlightGlobals.ActiveVessel here - that's the active vessel.
        // VesselPrecalculate.vessel is protected, so resolve via gameObject.
        Vessel bgVessel = __instance.gameObject.GetComponent<Vessel>();
        if (bgVessel != null && bgVessel != FlightGlobals.ActiveVessel)
        {
            BackgroundRecorderInstance.OnBackgroundPhysicsFrame(bgVessel);
        }
    }
}
```

**Critical**: `VesselPrecalculate.vessel` is protected (noted in MEMORY.md), so we resolve the vessel via `__instance.gameObject.GetComponent<Vessel>()`. The existing active-vessel path uses `FlightGlobals.ActiveVessel` comparison; the background path uses the opposite - it processes vessels that are NOT the active vessel.

**Performance**: `GetComponent<Vessel>()` is called every physics frame for every loaded vessel. This is acceptable because:
- The number of loaded vessels in physics range is typically 1-5.
- `GetComponent<T>()` on a known component type is a fast Unity operation.
- The `BackgroundRecorderInstance != null` check gates the entire path (zero cost when no tree is active).

#### 4.3 What BackgroundRecorder.OnBackgroundPhysicsFrame Does

For each call, the method:

1. Checks if `bgVessel.persistentId` is in the tree's `BackgroundMap`. If not, return immediately. (O(1) dictionary lookup.)
2. Checks if the vessel is in loaded/physics mode (`!bgVessel.packed`). If packed, return - on-rails handling is done in `Update()`.
3. Looks up or creates a per-vessel `BackgroundVesselState` tracking object (see section 5.2).
4. Polls part events: parachutes, engines, RCS, deployables, etc. - same part-state polling as `OnPhysicsFrame` (FlightRecorder.cs lines 3466-3486). Uses the per-vessel cached module lists.
5. Computes adaptive sampling: `TrajectoryMath.ShouldRecordPoint` with the vessel's Krakensbane-corrected velocity.
6. If sampling triggers, records a `TrajectoryPoint` into the tree Recording's `Points` list.

This is a **direct analog** of the active vessel's `OnPhysicsFrame` (FlightRecorder.cs lines 3362-3524), but:
- Writes directly to the tree Recording (not to a FlightRecorder's internal buffers).
- Uses per-vessel state (not the singleton recorder's state).
- Does NOT interact with `PhysicsFramePatch.ActiveRecorder` (that's exclusively for the active vessel).
- Does NOT detect vessel switches (background vessels don't switch).
- Does NOT check `isOnRails` (that's per-FlightRecorder state; this uses the vessel's packed flag).

#### 4.4 Part Event Tracking for Background Vessels

Each background vessel in loaded/physics mode needs its own part event tracking state:
- Parachute states, jettison tracking, engine/RCS caches, deployable tracking, lights, gear, cargo bays, fairings, robotics.
- These are currently all instance fields on `FlightRecorder` (lines 33-78).

The `BackgroundVesselState` class (section 5.2) holds a full set of these tracking collections. When a vessel transitions from on-rails to loaded/physics, the state is initialized (mirrors `ResetPartEventTrackingState` at FlightRecorder.cs line 3184). When the vessel goes back on rails, the state is cleared.

#### 4.5 Adaptive Sampling Thresholds

Background vessels use the same adaptive sampling thresholds as the active vessel:
- `maxSampleInterval` (default 3.0s)
- `velocityDirThreshold` (default 2.0 degrees)
- `speedChangeThreshold` (default 5%)

These are read from `ParsekSettings.Current` (same as FlightRecorder lines 109-114).

#### 4.6 Krakensbane-Corrected Velocity

For loaded/physics background vessels, velocity is computed identically to the active vessel (FlightRecorder.cs line 3489):

```csharp
Vector3 currentVelocity = (Vector3)(bgVessel.rb_velocityD + Krakensbane.GetFrameVelocity());
```

`Krakensbane.GetFrameVelocity()` is a global correction (not per-vessel), so it applies to all vessels in the physics frame.

---

### 5. BackgroundRecorder Class

#### 5.1 Why a Separate Class

The background recording logic is architecturally distinct from `FlightRecorder`:

- `FlightRecorder` manages a single vessel's recording lifecycle (start, stop, chain transitions, vessel switch detection). It is tightly coupled to `PhysicsFramePatch.ActiveRecorder`.
- `BackgroundRecorder` manages N vessels simultaneously, dispatches to different modes per vessel, and writes directly to tree Recording objects.

Embedding this in `FlightRecorder` would bloat an already 3860-line class and create confusing dual responsibilities. A separate class keeps concerns clean.

`BackgroundRecorder` is NOT a MonoBehaviour. It is a plain C# class owned by `ParsekFlight`, which calls its methods from `Update()` and `PhysicsFramePatch.Postfix`.

#### 5.2 Class Design

```csharp
// Source/Parsek/BackgroundRecorder.cs

internal class BackgroundRecorder
{
    private RecordingTree tree;

    // Per-vessel tracking state for loaded/physics mode
    private Dictionary<uint, BackgroundVesselState> loadedStates
        = new Dictionary<uint, BackgroundVesselState>();

    // Per-vessel on-rails tracking
    private Dictionary<uint, BackgroundOnRailsState> onRailsStates
        = new Dictionary<uint, BackgroundOnRailsState>();

    public BackgroundRecorder(RecordingTree tree) { this.tree = tree; }

    // Called from ParsekFlight.Update() once per frame
    public void UpdateOnRails(double currentUT) { ... }

    // Called from PhysicsFramePatch.Postfix for each non-active loaded vessel
    public void OnBackgroundPhysicsFrame(Vessel bgVessel) { ... }

    // Called when a vessel is added to the background map (transition or branch creation)
    public void OnVesselBackgrounded(uint vesselPid) { ... }

    // Called when a vessel is removed from the background map (promotion, terminal event)
    public void OnVesselRemovedFromBackground(uint vesselPid) { ... }

    // Called on scene change or tree teardown
    public void Shutdown() { ... }
}
```

**`BackgroundVesselState`** - per-vessel state for loaded/physics recording:

```csharp
private class BackgroundVesselState
{
    public uint vesselPid;
    public string recordingId;

    // Adaptive sampling state
    public double lastRecordedUT = -1;
    public Vector3 lastRecordedVelocity;

    // Part event tracking (mirrors FlightRecorder's instance fields)
    public Dictionary<uint, int> parachuteStates;
    public HashSet<uint> jettisonedShrouds;
    public HashSet<uint> extendedDeployables;
    public HashSet<uint> lightsOn;
    public HashSet<uint> blinkingLights;
    public Dictionary<uint, float> lightBlinkRates;
    public HashSet<uint> deployedGear;
    public HashSet<uint> openCargoBays;
    public HashSet<uint> deployedFairings;
    public HashSet<ulong> deployedLadders;
    public HashSet<ulong> deployedAnimationGroups;
    public HashSet<ulong> deployedAnimateGenericModules;
    public HashSet<ulong> deployedAeroSurfaceModules;
    public HashSet<ulong> deployedControlSurfaceModules;
    public HashSet<ulong> deployedRobotArmScannerModules;
    public HashSet<ulong> hotAnimateHeatModules;

    // Jettison caches (ORCHESTRATOR FIX - were missing from original plan)
    public Dictionary<uint, string> jettisonNameRawCache;
    public Dictionary<uint, List<string>> parsedJettisonNamesCache;

    // Engine/RCS/robotic caches
    public List<(Part part, ModuleEngines engine, int moduleIndex)> cachedEngines;
    public HashSet<ulong> activeEngineKeys;
    public Dictionary<ulong, float> lastThrottle;
    public HashSet<ulong> loggedEngineModuleKeys;
    public List<(Part part, ModuleRCS rcs, int moduleIndex)> cachedRcsModules;
    public HashSet<ulong> activeRcsKeys;
    public Dictionary<ulong, float> lastRcsThrottle;
    public HashSet<ulong> loggedRcsModuleKeys;
    public List<(Part part, PartModule module, int moduleIndex, string moduleName)> cachedRoboticModules;
    public HashSet<ulong> activeRoboticKeys;
    public Dictionary<ulong, float> lastRoboticPosition;
    public Dictionary<ulong, double> lastRoboticSampleUT;
    public HashSet<ulong> loggedRoboticModuleKeys;

    // Diagnostic guards - prevent log spam (one per module type)
    // (ORCHESTRATOR FIX - were omitted from original plan)
    public HashSet<ulong> loggedLadderClassificationMisses;
    public HashSet<ulong> loggedAnimationGroupClassificationMisses;
    public HashSet<ulong> loggedAnimateGenericClassificationMisses;
    public HashSet<ulong> loggedAeroSurfaceClassificationMisses;
    public HashSet<ulong> loggedControlSurfaceClassificationMisses;
    public HashSet<ulong> loggedRobotArmScannerClassificationMisses;
    public HashSet<ulong> loggedAnimateHeatClassificationMisses;
    public HashSet<ulong> loggedCargoBayDeployIndexIssues;
    public HashSet<ulong> loggedCargoBayAnimationIssues;
    public HashSet<ulong> loggedCargoBayClosedPositionIssues;
    public HashSet<uint> loggedFairingReadFailures;
}
```

**`BackgroundOnRailsState`** - per-vessel state for on-rails recording:

```csharp
private class BackgroundOnRailsState
{
    public uint vesselPid;
    public string recordingId;
    public bool hasOpenOrbitSegment;
    public OrbitSegment currentOrbitSegment;
    public bool isLanded;  // true if captured as SurfacePosition instead of orbit
    public double lastExplicitEndUpdate;  // UT of last ExplicitEndUT refresh
}
```

#### 5.3 Initialization and Lifecycle

- Created by `ParsekFlight` when `activeTree` is set (Task 4 will wire this up; for now, Task 3 provides the class and ParsekFlight will instantiate it alongside the tree).
- Destroyed when `activeTree` is cleared (scene change, tree commit, revert).
- On creation, iterates `tree.BackgroundMap` and initializes `BackgroundOnRailsState` for each existing background vessel.

#### 5.4 Part-Event Polling Architecture

**[ORCHESTRATOR FIX - Corrected from plan review]**

The part-event polling methods in FlightRecorder have a layered architecture:

**Layer 1: Pure static transition methods** (already `internal static`, directly reusable):
- `CheckParachuteTransition`, `CheckJettisonTransition`, `CheckDeployableTransition`, `CheckLightTransition`, `CheckLightBlinkTransition`, `CheckEngineTransition`, `CheckRcsTransition`, `CheckFairingTransition`, `CheckLadderTransition`, `CheckAnimationGroupTransition`, `CheckAnimateHeatTransition`, `CheckGearTransition`, `CheckCargoBayTransition`, `CheckRoboticTransition`
- Also: `TryClassifyAnimateGenericState`, `TryClassifyAnimateHeatState`, `TryClassifyLadderState`, `TryClassifyAnimationGroupState`, `TryClassifyAeroSurfaceState`, `TryClassifyControlSurfaceState`, `TryClassifyRobotArmScannerState`

**Layer 2: Private instance "Check*State" methods** - these are the vessel-part iteration wrappers. They are **private**, not static, and contain non-trivial logic:
- Simple wrappers (~15-30 lines): `CheckParachuteState`, `CheckJettisonState`, `CheckDeployableState`, `CheckLightState`, `CheckGearState`, `CheckCargoBayState`, `CheckFairingState`
- Engine/RCS/robotic wrappers (~30-50 lines): `CheckEngineState`, `CheckRcsState`, `CheckRoboticState` - use pre-cached module lists
- Complex wrappers with classification + exclusion logic (~40-80 lines): `CheckAnimateGenericState` (line 1702), `CheckAeroSurfaceState` (line 1564), `CheckControlSurfaceState` (line 1610), `CheckRobotArmScannerState` (line 1656), `CheckAnimateHeatState` (line 1782), `CheckLadderState`, `CheckAnimationGroupState`

**Important correction**: `CheckAnimateGenericState` is NOT a static method - it's a **private instance method** (line 1702) with 80 lines of module exclusion logic (checks for ModuleDeployablePart, ModuleWheelDeployment, ModuleCargoBay, RetractableLadder, ModuleAnimationGroup, ModuleAeroSurface, ModuleControlSurface, ModuleRobotArmScanner, ModuleAnimateHeat before processing). It delegates to `TryClassifyAnimateGenericState` (static) and `CheckAnimationGroupTransition` (static) but the wrapper itself is complex.

Similarly, `CheckAeroSurfaceState`, `CheckControlSurfaceState`, `CheckRobotArmScannerState`, `CheckAnimateHeatState` are all **private instance methods** that use `TryClassify*` static methods + `CheckAnimationGroupTransition`/`CheckAnimateHeatTransition` as shared static helpers.

**Also important**: `ResetPartEventTrackingState` (line 3184) is **`private void`**, not `internal static`. BackgroundRecorder cannot call it directly. Must duplicate the 65-line initialization logic including fairing seeding (lines 3186-3248), jettison caches (`jettisonNameRawCache`, `parsedJettisonNamesCache`), and ~10 `logged*` diagnostic HashSets (lines 3204-3214).

**Approach: Duplicate the polling loop in BackgroundRecorder.**

BackgroundRecorder's `PollPartEvents` will duplicate the Layer 2 wrapper methods, calling the same Layer 1 static transition/classification methods. This is more duplication than initially estimated (~300 lines for the complex wrappers) but avoids a risky refactor of FlightRecorder's 3860-line file.

The `BackgroundVesselState` must include ALL tracking collections from FlightRecorder (lines 33-78), plus the missing ones identified by review:
- `jettisonNameRawCache` (Dictionary<uint, string>)
- `parsedJettisonNamesCache` (Dictionary<uint, List<string>>)
- All `logged*ClassificationMisses` HashSets (lines 3204-3214): `loggedLadderClassificationMisses`, `loggedAnimationGroupClassificationMisses`, `loggedAnimateGenericClassificationMisses`, `loggedAeroSurfaceClassificationMisses`, `loggedControlSurfaceClassificationMisses`, `loggedRobotArmScannerClassificationMisses`, `loggedAnimateHeatClassificationMisses`, `loggedCargoBayDeployIndexIssues`, `loggedCargoBayAnimationIssues`, `loggedCargoBayClosedPositionIssues`, `loggedFairingReadFailures`, `loggedEngineModuleKeys`, `loggedRcsModuleKeys`

**Future refactoring note**: After Task 3 is stable, the polling logic can be extracted into a shared `PartEventPoller` static class. This would eliminate the duplication. Deferred to Task 11 or post-Phase-6 cleanup.

---

### 6. Data Flow

#### 6.1 On-Rails Background Data Flow

```
ParsekFlight.Update()
  → backgroundRecorder.UpdateOnRails(currentUT)
    → for each vessel in BackgroundMap:
      → look up Vessel by persistentId in FlightGlobals.Vessels
      → if vessel.loaded && !vessel.packed: skip (handled by physics path)
      → if vessel not found: skip (may be destroyed)
      → if landed/splashed and SurfacePos already captured: update ExplicitEndUT only
      → if orbiting: check SOI changes (handled by event subscription)
      → update ExplicitEndUT on tree Recording every 30s
```

SOI changes for background vessels:

```
GameEvents.onVesselSOIChanged fires (HostedFromToAction<Vessel, CelestialBody>)
  → BackgroundRecorder.OnBackgroundVesselSOIChanged(data)
    → data.host = vessel, data.from = oldBody, data.to = newBody
    → close current orbit segment (endUT = now)
    → append to tree Recording's OrbitSegments
    → open new orbit segment with new body's orbit params
```

#### 6.2 Loaded/Physics Background Data Flow

```
VesselPrecalculate.CalculatePhysicsStats() fires (Harmony postfix)
  → PhysicsFramePatch.Postfix
    → if BackgroundRecorderInstance != null && vessel != ActiveVessel:
      → backgroundRecorder.OnBackgroundPhysicsFrame(bgVessel)
        → look up vesselPid in BackgroundMap → get recordingId
        → look up or create BackgroundVesselState
        → detect mode transition (was on-rails, now loaded/physics)
        → poll part events → append to tree Recording's PartEvents
        → adaptive sampling → if triggered, append TrajectoryPoint to tree Recording's Points
```

#### 6.3 Writing to Tree Recordings

Both paths write directly to the tree's `Recording` objects:

```csharp
RecordingStore.Recording treeRec;
if (tree.Recordings.TryGetValue(recordingId, out treeRec))
{
    treeRec.Points.Add(point);          // trajectory
    treeRec.OrbitSegments.Add(segment); // orbit
    treeRec.PartEvents.Add(evt);        // part events
}
```

No intermediate buffers. The tree Recording is the single source of truth. This is safe because:
- Only one writer per recording at a time (the active recorder writes to the active recording; background recorders write to their respective recordings).
- The `BackgroundMap` ensures a 1:1 mapping from vessel to recording.

#### 6.4 ExplicitStartUT and ExplicitEndUT

Background-only recordings may have no trajectory points (purely on-rails). They use `ExplicitStartUT` / `ExplicitEndUT` (RecordingStore.cs lines 113-117) to define their time range. These are set:
- `ExplicitStartUT`: set when the recording first enters background (at branch creation time, Task 4).
- `ExplicitEndUT`: updated periodically by `BackgroundRecorder.UpdateOnRails()`.

When a background recording also has trajectory points (was loaded at some point), `StartUT` / `EndUT` properties prefer points over explicit values (RecordingStore.cs lines 146-149). The explicit values serve as a fallback for pure orbit-segment recordings.

---

### 7. Vessel Lifecycle Events

#### 7.1 Background Vessel Goes On Rails

Trigger: `GameEvents.onVesselGoOnRails` fires for a vessel in `BackgroundMap`.

Action:
- If the vessel had a `BackgroundVesselState` (was in loaded/physics mode):
  - Sample a final boundary trajectory point.
  - Clear the `BackgroundVesselState` (engine caches, part event tracking).
  - Remove from `loadedStates`.
- Create/update `BackgroundOnRailsState`:
  - Open an orbit segment from the vessel's current orbit.
  - Or capture a `SurfacePosition` if landed/splashed.

Current code: `ParsekFlight.OnVesselGoOnRails` (line 1142) delegates to `recorder?.OnVesselGoOnRails(v)`, which only processes the active vessel (FlightRecorder.cs line 3564: `if (v != FlightGlobals.ActiveVessel) return`). The background recorder must handle its own vessels:

```csharp
void OnVesselGoOnRails(Vessel v)
{
    recorder?.OnVesselGoOnRails(v);
    backgroundRecorder?.OnBackgroundVesselGoOnRails(v);
}
```

#### 7.2 Background Vessel Goes Off Rails

Trigger: `GameEvents.onVesselGoOffRails` fires for a vessel in `BackgroundMap`.

Action:
- If the vessel had a `BackgroundOnRailsState` with an open orbit segment:
  - Close the orbit segment (`endUT = now`), append to tree Recording's `OrbitSegments`.
  - Remove from `onRailsStates`.
- If the vessel is now `loaded && !packed` (full physics):
  - Create a `BackgroundVesselState` (initialize part event tracking).
  - Sample a boundary trajectory point.

#### 7.3 Background Vessel SOI Change

Trigger: `GameEvents.onVesselSOIChanged` fires for a vessel in `BackgroundMap`.

Action:
- Close current orbit segment in the old SOI.
- Open new orbit segment in the new SOI.
- This mirrors `FlightRecorder.OnVesselSOIChanged` (line 3618) but for background vessels.

Current code: `ParsekFlight.OnVesselSOIChanged` (line 1152) delegates to `recorder?.OnVesselSOIChanged(data)`, which only processes the active vessel (FlightRecorder.cs line 3621). Add:

```csharp
void OnVesselSOIChanged(GameEvents.HostedFromToAction<Vessel, CelestialBody> data)
{
    recorder?.OnVesselSOIChanged(data);
    backgroundRecorder?.OnBackgroundVesselSOIChanged(data);
}
```

#### 7.4 Background Vessel Destroyed

Trigger: `GameEvents.onVesselWillDestroy` fires for a vessel in `BackgroundMap`.

Action for Task 3 (minimal): Close any open orbit segment. Clean up tracking state. Log a warning. The full terminal-state handling (marking recording as Destroyed, removing from BackgroundMap) is deferred to Task 6.

Add:

```csharp
void OnVesselWillDestroy(Vessel v)
{
    // Existing active vessel handling...
    backgroundRecorder?.OnBackgroundVesselWillDestroy(v);
}
```

#### 7.5 Background Vessel Recovered

Deferred to Task 6. `onVesselRecoveryProcessing` handler will mark recording as Recovered.

#### 7.6 Vessel Enters/Exits Physics Range

When a background vessel crosses the physics-range boundary:
- **Enters range** (`onVesselGoOffRails` fires): Transition from on-rails to loaded mode. The `onVesselGoOffRails` event is the trigger.
- **Exits range** (`onVesselGoOnRails` fires): Transition from loaded to on-rails mode.

These are handled by 7.1 and 7.2 above - no additional event subscription needed.

---

### 8. Integration with Task 2

Task 2 established:
- `FlightRecorder.TransitionToBackground()` (line 3717): Opens an orbit segment, disconnects Harmony, keeps `IsRecording = true`.
- `ParsekFlight.FlushRecorderToTreeRecording()` (line 758): Appends recorder data to tree Recording, calls `FinalizeOpenOrbitSegment()`, sets `IsRecording = false`.
- `ParsekFlight.OnVesselSwitchComplete()` (line 699): Transitions old recorder, promotes new vessel.
- `ParsekFlight.PromoteRecordingFromBackground()` (line 806): Creates fresh FlightRecorder, starts with `isPromotion: true`.

Task 3 integrates as follows:

**When a vessel transitions to background** (either via `OnPhysicsFrame` tree decision or `OnVesselSwitchComplete`):
1. Task 2's `FlushRecorderToTreeRecording` runs, closing the orbit segment and appending data.
2. Task 3's `BackgroundRecorder.OnVesselBackgrounded(vesselPid)` initializes tracking:
   - Looks up the vessel in `FlightGlobals.Vessels`.
   - If loaded/physics: creates `BackgroundVesselState`, starts trajectory sampling on next physics frame.
   - If on-rails: creates `BackgroundOnRailsState`, opens orbit segment.

**When a vessel is promoted from background**:
1. Task 3's `BackgroundRecorder.OnVesselRemovedFromBackground(vesselPid)` runs:
   - Closes any open orbit segment, appends final data to tree Recording.
   - Cleans up tracking state.
2. Task 2's `PromoteRecordingFromBackground` creates a fresh `FlightRecorder`.

**Ordering in ParsekFlight**:

In `OnVesselSwitchComplete`:
```csharp
// After flushing old recorder and updating BackgroundMap:
backgroundRecorder?.OnVesselBackgrounded(oldVesselPid);

// Before promoting new vessel:
backgroundRecorder?.OnVesselRemovedFromBackground(newVessel.persistentId);
PromoteRecordingFromBackground(backgroundRecordingId, newVessel);
```

In `Update()` transition handler:
```csharp
// After flushing recorder to tree:
backgroundRecorder?.OnVesselBackgrounded(oldVesselPid);
```

**The orbit segment opened by `TransitionToBackground` (Task 2) is closed by `FinalizeOpenOrbitSegment` during flush (Task 2).** Task 3's `OnVesselBackgrounded` then opens a fresh orbit segment if the vessel is still on rails. This avoids double-counting.

---

### 9. Unit Tests

New file: `Source/Parsek.Tests/BackgroundRecorderTests.cs`

All tests use the existing `internal static` pure methods and do not require Unity runtime.

#### 9.1 On-Rails State Management

1. **`OnRailsState_InitializesOrbitSegment_WhenVesselOrbiting`** - Verify that `BackgroundOnRailsState` captures orbit parameters correctly.

2. **`OnRailsState_CapturesSurfacePosition_WhenVesselLanded`** - Verify that landed vessels get `SurfacePos` set and no orbit segment.

3. **`OnRailsState_SOIChange_ClosesOldOpensNew`** - Verify orbit segment is closed at SOI change UT and a new one is opened.

4. **`OnRailsState_ExplicitEndUT_UpdatedPeriodically`** - Verify `ExplicitEndUT` is updated after the refresh interval.

5. **`OnRailsState_ExplicitEndUT_NotUpdatedTooFrequently`** - Verify throttling works.

#### 9.2 Loaded/Physics State Management

6. **`LoadedState_InitializesPartTracking`** - Verify that part event tracking collections are created.

7. **`LoadedState_ModeTransition_OnRailsToLoaded_ClosesOrbitSegment`** - Verify orbit segment closed when vessel transitions to loaded.

8. **`LoadedState_ModeTransition_LoadedToOnRails_OpensOrbitSegment`** - Verify new orbit segment opened when vessel goes on rails.

#### 9.3 Part Event Polling (Reuse Existing Static Methods)

9. **`BackgroundPartPolling_ParachuteTransition_RecordsEvent`** - Call `CheckParachuteTransition` with background state collections, verify event produced.

10. **`BackgroundPartPolling_EngineTransition_RecordsEvent`** - Call `CheckEngineTransition` with background state, verify event.

11. **`BackgroundPartPolling_DeployableTransition_RecordsEvent`** - Call `CheckDeployableTransition` with background state, verify event.

#### 9.4 Adaptive Sampling

12. **`BackgroundSampling_UsesAdaptiveThresholds`** - Verify `TrajectoryMath.ShouldRecordPoint` is called with correct parameters and respects thresholds (existing `ShouldRecordPoint` tests already cover the math; this tests the integration wiring).

#### 9.5 Vessel Lifecycle

13. **`OnVesselBackgrounded_InitializesState`** - Verify that calling `OnVesselBackgrounded` creates correct tracking state.

14. **`OnVesselRemovedFromBackground_CleansUp`** - Verify state is cleaned up and orbit segment finalized.

15. **`Shutdown_CleansUpAllStates`** - Verify all per-vessel states are cleaned up on shutdown.

#### 9.6 Data Flow

16. **`BackgroundPhysicsFrame_WritesDirectlyToTreeRecording`** - Create a tree with a background recording, simulate a physics frame, verify the tree Recording has a new trajectory point.

17. **`BackgroundSOIChange_WritesOrbitSegmentToTreeRecording`** - Simulate SOI change, verify orbit segment in tree Recording.

---

### 10. In-Game Test Scenarios

1. **Basic background on-rails recording**: Launch, undock into two vessels. Switch to vessel A. Time warp for one orbit. Switch to vessel B. Verify vessel A's recording has orbit segments covering the background period. Switch back to A. Verify recording continues seamlessly.

2. **Background SOI change**: Launch two vessels, send one toward Mun. Switch to the other vessel. Time warp until the first vessel enters Mun SOI. Switch back. Verify recording has two orbit segments (Kerbin orbit + Mun orbit) with correct body names.

3. **Background loaded/physics recording**: Launch, undock two vessels in close proximity (within physics range ~2.5km). Switch to vessel A. Verify vessel B (loaded, in physics range) accumulates trajectory points. Apply thrust to A. Verify B records its inertial drift correctly.

4. **Background mode transition (loaded → on-rails)**: Undock two close vessels. Record both in physics range. Thrust vessel A away from B until B exits physics range (>2.5km). Verify B's recording transitions from trajectory points to orbit segments at the boundary.

5. **Background landed vessel**: Land a vessel, switch to an orbiting vessel. Time warp. Verify the landed vessel's recording has `SurfacePos` set and `ExplicitEndUT` keeps updating.

6. **Rapid vessel switching**: Launch, undock, rapidly press `]` `]` `]` to cycle through vessels. Verify no crashes, all recordings accumulate data, no data loss.

7. **Background recording across time warp**: Switch to vessel A during time warp. Verify vessel B's on-rails recording works correctly during time warp (orbit segments, ExplicitEndUT updates).

---

### 11. Files Modified/Created

| File | Changes |
|------|---------|
| `Source/Parsek/BackgroundRecorder.cs` | **NEW** - BackgroundRecorder class, BackgroundVesselState, BackgroundOnRailsState |
| `Source/Parsek/Patches/PhysicsFramePatch.cs` | Add `BackgroundRecorderInstance` static field; add dispatch to `BackgroundRecorder.OnBackgroundPhysicsFrame` for non-active vessels |
| `Source/Parsek/ParsekFlight.cs` | Add `BackgroundRecorder` instance field; wire up `OnVesselBackgrounded`/`OnVesselRemovedFromBackground` calls in `OnVesselSwitchComplete` and `Update()` transition handler; delegate on-rails/off-rails/SOI/destroy events to `BackgroundRecorder`; add `backgroundRecorder.UpdateOnRails()` call in `Update()`; cleanup in `OnSceneChangeRequested` and `OnFlightReady` |
| `Source/Parsek/FlightRecorder.cs` | No changes needed. `CacheEngineModules` (line 1884), `CacheRcsModules` (line 2253), `CacheRoboticModules` (line 2405), and `EncodeEngineKey` (line 1908) are already `internal static` and callable from `BackgroundRecorder`. All static transition methods (`CheckParachuteTransition`, `CheckEngineTransition`, etc.) are already `internal static`. |
| `Source/Parsek.Tests/BackgroundRecorderTests.cs` | **NEW** - Unit tests for background recording infrastructure |

---

### 12. Implementation Order

1. **Verify module cache methods are already internal static** in `FlightRecorder.cs`:
   - `CacheEngineModules(Vessel v)` - already `internal static` (line 1884)
   - `CacheRcsModules(Vessel v)` - already `internal static` (line 2253)
   - `CacheRoboticModules(Vessel v)` - already `internal static` (line 2405)
   - `EncodeEngineKey(uint pid, int moduleIndex)` - already `internal static` (line 1908)
   - No changes needed. These are directly callable from `BackgroundRecorder`.

2. **Create `BackgroundRecorder.cs`** with:
   - `BackgroundOnRailsState` inner class
   - `BackgroundVesselState` inner class
   - Constructor accepting `RecordingTree`
   - `UpdateOnRails(double currentUT)` - on-rails loop
   - `OnVesselBackgrounded(uint vesselPid)` - initialize tracking
   - `OnVesselRemovedFromBackground(uint vesselPid)` - cleanup
   - `OnBackgroundVesselGoOnRails(Vessel v)` - loaded→on-rails transition
   - `OnBackgroundVesselGoOffRails(Vessel v)` - on-rails→loaded transition
   - `OnBackgroundVesselSOIChanged(Vessel v, CelestialBody fromBody)` - SOI change
   - `OnBackgroundVesselWillDestroy(Vessel v)` - destruction cleanup
   - `OnBackgroundPhysicsFrame(Vessel bgVessel)` - loaded/physics recording
   - `Shutdown()` - cleanup all state
   - Part event polling for background vessels (calling existing static transition methods)

3. **Modify `PhysicsFramePatch.cs`**:
   - Add `internal static BackgroundRecorder BackgroundRecorderInstance`
   - In `Postfix`, after the active recorder dispatch, add background recorder dispatch for non-active vessels

4. **Wire up in `ParsekFlight.cs`**:
   - Add `private BackgroundRecorder backgroundRecorder` field
   - In `OnVesselSwitchComplete`: call `OnVesselBackgrounded` / `OnVesselRemovedFromBackground`
   - In `Update()` transition handler: call `OnVesselBackgrounded`
   - In `Update()`: call `backgroundRecorder?.UpdateOnRails(currentUT)`
   - In `OnVesselGoOnRails`: delegate to `backgroundRecorder`
   - In `OnVesselGoOffRails`: delegate to `backgroundRecorder`
   - In `OnVesselSOIChanged`: delegate to `backgroundRecorder`
   - In `OnVesselWillDestroy`: delegate to `backgroundRecorder`
   - In `OnSceneChangeRequested`: call `backgroundRecorder?.Shutdown()`; set to null
   - In `OnFlightReady`: set `backgroundRecorder = null`
   - When `activeTree` is set (placeholder for Task 4): create `backgroundRecorder = new BackgroundRecorder(activeTree)`; set `PhysicsFramePatch.BackgroundRecorderInstance`
   - When `activeTree` is cleared: shutdown and null both

5. **Write unit tests** in `BackgroundRecorderTests.cs`

6. **Run `dotnet test`** - all existing + new tests pass

7. **Run `dotnet build`** - verify compilation

---

### 13. Risk Assessment

| Risk | Mitigation |
|------|-----------|
| Performance: physics-frame dispatch for every loaded vessel | `BackgroundRecorderInstance != null` check gates entire path (zero cost when no tree). Dictionary lookup is O(1). Loaded vessel count is typically 1-5. Part polling is the same cost as active recording. |
| `GetComponent<Vessel>()` on every physics frame for every VesselPrecalculate | Small constant cost per loaded vessel. Could cache in a dictionary keyed by `GameObject.GetInstanceID()` if profiling shows issues. Premature optimization - defer unless KSP.log shows frame rate drop. |
| Thread safety: `BackgroundRecorder` methods called from both `Update()` and `Postfix` | `Update()` runs on the main thread. `Postfix` is a Harmony patch on a Unity `FixedUpdate`-driven method, also main thread. No threading issue - Unity is single-threaded for gameplay logic. However, `Update()` and `FixedUpdate` can interleave within a frame. **[ORCHESTRATOR FIX]** Mode transitions are owned by KSP events (`onVesselGoOnRails`/`onVesselGoOffRails`), NOT by polling in `UpdateOnRails` or `OnBackgroundPhysicsFrame`. This eliminates races: `OnBackgroundVesselGoOffRails` moves the vessel from `onRailsStates` to `loadedStates`; `OnBackgroundVesselGoOnRails` does the reverse. Neither `UpdateOnRails` nor `OnBackgroundPhysicsFrame` creates or destroys state entries - they only operate on entries already in the correct dictionary. `OnBackgroundPhysicsFrame` checks `if (!loadedStates.ContainsKey(pid)) return;` and `UpdateOnRails` checks `if (!onRailsStates.ContainsKey(pid)) continue;`. |
| Race between promotion and background physics frame | When `OnVesselRemovedFromBackground` is called, it removes the vessel from `BackgroundMap`. The next `OnBackgroundPhysicsFrame` call will fail the `BackgroundMap` lookup and skip. The `loadedStates` dictionary is also cleaned up. |
| Stale engine/part module caches after vessel structure change | Cache invalidation on decouple/dock events. For Task 3, background vessels are not expected to dock/undock (those are tree events handled in Tasks 4/5). If a background vessel loses parts (destruction), the cache entry for the missing part will fail the null check in the polling loop and be skipped. |
| Orbit segment continuity: double orbit segment at transition | `FlushRecorderToTreeRecording` (Task 2) calls `FinalizeOpenOrbitSegment` which closes any open segment. `OnVesselBackgrounded` opens a new one. No overlap - the old segment's `endUT` equals the new segment's `startUT`. |
| `vessel.orbit` null for vessels on launchpad | Guard: check `vessel.orbit != null` before capturing orbit segment. Landed vessels use `SurfacePosition` instead. |
| Backward compatibility | `BackgroundRecorderInstance == null` when no tree is active. All new code paths gated on tree existence. Existing standalone recording is completely unaffected. |
| Large vessel count in BackgroundMap | Practical limit: KSP rarely has more than 10-20 vessels in a tree. Dictionary operations are O(1). Not a concern. |
| Duplicate part events from active and background recording | Impossible: the active recorder processes `FlightGlobals.ActiveVessel` only (filtered by `__instance.gameObject == v.gameObject`). The background recorder explicitly excludes the active vessel (`bgVessel != FlightGlobals.ActiveVessel`). |
| ExplicitEndUT update frequency (30s) too coarse | 30s is sufficient for tree management. The actual recording data (orbit segments, trajectory points) has sub-second resolution. `ExplicitEndUT` is only used for time-range queries and UI display. |
| Part event polling extraction: code duplication | Pragmatic choice. The static transition methods are shared; only the vessel-iteration wrappers are duplicated (~300 lines). Future refactoring into a shared `PartEventPoller` is straightforward. |

---

### 14. Orchestrator Review Notes

**Issues fixed from plan review:**

1. **Scope conflict resolved**: The design doc's "Background recording depth" section stated part events are only for active vessels. Updated design doc to reflect the user's explicit requirement for dual-mode background recording (on-rails lightweight + loaded/physics full fidelity).

2. **Static method inventory corrected** (section 5.4): `CheckAnimateGenericState`, `CheckAeroSurfaceState`, `CheckControlSurfaceState`, `CheckRobotArmScannerState`, `CheckAnimateHeatState` are private instance methods, NOT static. They delegate to shared static helpers (`CheckAnimationGroupTransition`, `CheckAnimateHeatTransition`, `TryClassify*` methods) but contain complex module-exclusion logic. Section 5.4 rewritten with accurate architecture description.

3. **ResetPartEventTrackingState is private** (line 3184): Cannot be called from BackgroundRecorder. Must duplicate the 65-line initialization including fairing seeding and all diagnostic HashSet clearing. Noted in section 5.4.

4. **Missing BackgroundVesselState fields added**: `jettisonNameRawCache`, `parsedJettisonNamesCache`, and all 13 `logged*` diagnostic HashSets now listed in section 5.2.

5. **Mode transition ownership clarified** (risk table): Mode transitions are driven by `onVesselGoOnRails`/`onVesselGoOffRails` events, not by polling. This eliminates Update/FixedUpdate interleaving races on state dictionaries.

6. **SOI event signature fixed** (section 6.1): Changed from `(vessel, fromBody, toBody)` to `HostedFromToAction<Vessel, CelestialBody>` with `.host`/`.from`/`.to` fields.
