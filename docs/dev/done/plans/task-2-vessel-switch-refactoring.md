# Task 2: Vessel Switch Refactoring (Active ↔ Background Transition)

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

Task 2 transforms vessel switching from a recording-terminating event into a recording-transitioning event when a recording tree is active. Today, when the player presses `[`/`]` or clicks in map view, `OnPhysicsFrame` detects the PID mismatch and `DecideOnVesselSwitch` returns `Stop`, ending the recording. After this task, when a tree is active, the decision instead returns `TransitionToBackground` (if switching to a non-tree vessel or capturing the old vessel's orbital state) or `PromoteFromBackground` (if switching to a vessel already tracked in the tree's `BackgroundMap`).

The key invariant is that **a single recording can hold interleaved trajectory points and orbit segments**, which is already the mechanism used for time warp on-rails/off-rails transitions. Transitioning to background is logically the same as going on rails: stop physics sampling, start an orbit segment. Promoting from background is logically the same as going off rails: close the orbit segment, resume physics sampling. The existing `OrbitSegment` data format and playback code handle this with no changes.

All existing behavior is preserved when no tree is active. The new decision values only fire when `ParsekFlight` has an active `RecordingTree`. All existing `VesselSwitchDecision` values (`None`, `ContinueOnEva`, `Stop`, `ChainToVessel`, `DockMerge`, `UndockSwitch`) continue to exist and function identically.

---

### 2. Current Behavior Analysis

#### 2.1 The Physics Frame Loop (`FlightRecorder.OnPhysicsFrame`)

Located at line 3334 of `FlightRecorder.cs`. The Harmony postfix on `VesselPrecalculate.CalculatePhysicsStats()` calls this every physics frame for the active vessel. The method:

1. Checks `if (!IsRecording) return;` and `if (isOnRails) return;`
2. Compares `v.persistentId != RecordingVesselId` to detect vessel switch
3. If PID mismatch: calls `DecideOnVesselSwitch`, which returns one of 6 values
4. For `Stop`: captures `CaptureAtStop`, disconnects from Harmony patch, sets `IsRecording = false`
5. If no mismatch: polls part states, samples position with adaptive thresholds

#### 2.2 The Decision Function (`FlightRecorder.DecideOnVesselSwitch`)

Located at line 3709. A pure `internal static` method with signature:
```csharp
internal static VesselSwitchDecision DecideOnVesselSwitch(
    uint recordingVesselId, uint currentVesselId, bool currentIsEva,
    bool recordingStartedAsEva, uint undockSiblingPid = 0)
```

Current logic (in priority order):
1. Same PID → `None`
2. Current = undock sibling PID → `UndockSwitch`
3. EVA-to-EVA when started as EVA → `ContinueOnEva`
4. Non-EVA when started as EVA → `ChainToVessel`
5. Default → `Stop`

The function has no tree awareness. It does not receive a `RecordingTree` parameter.

#### 2.3 On-Rails Transition (Existing Pattern)

When the vessel goes on rails (time warp), `OnVesselGoOnRails` (line 3507):
- Samples a boundary point
- Creates a new `OrbitSegment` with current Keplerian elements
- Sets `isOnRails = true`

When the vessel goes off rails, `OnVesselGoOffRails` (line 3538):
- Closes the orbit segment (`endUT = now`)
- Adds it to `OrbitSegments`
- Sets `isOnRails = false`
- Samples a boundary point

This is exactly the same mechanism that will be used for active-to-background transitions. The key insight is that `TransitionToBackground` is semantically equivalent to "going on rails" - stop physics sampling, capture an orbit segment.

#### 2.4 ParsekFlight's Update Loop

The `Update()` method in `ParsekFlight.cs` (line 195) processes pending transitions set by `OnPhysicsFrame` or event handlers. It checks for:
- `DockMergePending` → commit segment, restart recording
- `pendingDockAsTarget` → commit segment, restart recording
- `pendingUndockOtherPid != 0` → commit segment, restart recording
- `UndockSwitchPending` → commit segment with role swap
- `pendingChainContinuation` → commit EVA chain segment
- `ChainToVesselPending` → commit EVA segment, start vessel recording
- Atmosphere/SOI boundary splits

#### 2.5 Chain Behavior

The existing chain system (EVA, boarding, dock, undock, atmosphere splits, SOI splits) uses `StopRecordingForChainBoundary()` to silently stop, then commits the segment and starts a new recording. Chains are identified by `ChainId`, `ChainIndex`, and `ChainBranch` on each recording.

Chains are **intra-node** - they split a single vessel's continuous recording into segments for UX purposes (per-segment loop control, enable/disable). The recording tree is a **new layer on top** of chains - each recording node in the tree may itself be a chain of segments.

---

### 3. Changes to `VesselSwitchDecision` Enum

Add two new values at the end of the enum (preserving existing ordinal values):

```csharp
internal enum VesselSwitchDecision
{
    None,
    ContinueOnEva,
    Stop,
    ChainToVessel,
    DockMerge,
    UndockSwitch,
    TransitionToBackground,   // NEW: active recording → background (orbit segment)
    PromoteFromBackground     // NEW: background recording → active (resume physics sampling)
}
```

No existing values are removed or renumbered. These values are never serialized (they are transient runtime decisions), so adding them is safe.

---

### 4. Changes to `DecideOnVesselSwitch`

#### 4.1 New Signature

```csharp
internal static VesselSwitchDecision DecideOnVesselSwitch(
    uint recordingVesselId, uint currentVesselId, bool currentIsEva,
    bool recordingStartedAsEva, uint undockSiblingPid = 0,
    RecordingTree activeTree = null)
```

The `activeTree` parameter is the currently active recording tree (null when operating in standalone/legacy mode). All existing callers pass `activeTree = null` by default, preserving current behavior.

#### 4.2 New Logic

The decision priority chain becomes:

1. Same PID → `None` (unchanged)
2. Current = undock sibling PID → `UndockSwitch` (unchanged - undock is a chain/tree event, not a simple switch)
3. EVA-to-EVA when started as EVA → `ContinueOnEva` (unchanged)
4. Non-EVA when started as EVA → `ChainToVessel` (unchanged - boarding is a tree event handled separately)
5. **NEW**: If `activeTree != null` and `currentVesselId` in `activeTree.BackgroundMap` → `PromoteFromBackground`
6. **NEW**: If `activeTree != null` → `TransitionToBackground`
7. Default → `Stop` (unchanged - no tree active)

The critical design decision: **existing chain decisions (UndockSwitch, ContinueOnEva, ChainToVessel, DockMerge) take priority over tree decisions.** This ensures chains are fully preserved. The tree decisions only activate when none of the chain conditions match, and only when a tree is active.

#### 4.3 DockMergePending Handling - NEW GUARD REQUIRED

When docking, the initiator vessel's PID changes to the merged vessel's PID. `DockMerge` is set as a pending flag on the recorder by `OnPartCouple`, NOT returned by `DecideOnVesselSwitch`. In the current code, `OnPhysicsFrame` does NOT check `DockMergePending` before calling `DecideOnVesselSwitch` - it calls `DecideOnVesselSwitch` which returns `Stop`, creates `CaptureAtStop`, sets `IsRecording = false`, then `ParsekFlight.Update()` picks up `DockMergePending` from the stopped recorder.

**This is a problem for tree mode.** Without a guard, `DecideOnVesselSwitch` would return `TransitionToBackground` instead of `Stop` for dock PID changes, intercepting them incorrectly. **A NEW explicit early-return guard must be added** in `OnPhysicsFrame`:

```csharp
if (v.persistentId != RecordingVesselId)
{
    // Dock merge detected by OnPartCouple - let existing capture+stop flow handle it
    if (DockMergePending)
    {
        // Fall through to existing CaptureAtStop block (unchanged behavior)
        // Do NOT enter tree decision logic
    }
    else
    {
        // Tree and chain decision logic here
    }
}
```

This guard must be the FIRST check inside the PID mismatch block, before `DecideOnVesselSwitch` is called.

---

### 5. Changes to `FlightRecorder`

#### 5.1 New Method: `TransitionToBackground`

Captures the current vessel's orbital state as the start of a background orbit segment, stops physics-frame sampling. Nearly identical to `OnVesselGoOnRails` but without the dependency on a specific Vessel reference.

```csharp
public void TransitionToBackground()
{
    if (!IsRecording) return;

    Vessel v = FindVesselByPid(RecordingVesselId);

    // Sample a boundary point if vessel is still accessible
    if (v != null)
        SamplePosition(v);

    // Finalize any in-progress orbit segment (if already on rails during switch)
    if (isOnRails)
    {
        currentOrbitSegment.endUT = Planetarium.GetUniversalTime();
        OrbitSegments.Add(currentOrbitSegment);
        isOnRails = false;
    }

    // Start a new orbit segment for the background phase
    if (v != null && v.orbit != null)
    {
        currentOrbitSegment = new OrbitSegment
        {
            startUT = Planetarium.GetUniversalTime(),
            inclination = v.orbit.inclination,
            eccentricity = v.orbit.eccentricity,
            semiMajorAxis = v.orbit.semiMajorAxis,
            longitudeOfAscendingNode = v.orbit.LAN,
            argumentOfPeriapsis = v.orbit.argumentOfPeriapsis,
            meanAnomalyAtEpoch = v.orbit.meanAnomalyAtEpoch,
            epoch = v.orbit.epoch,
            bodyName = v.mainBody?.name ?? "Kerbin"
        };
        isOnRails = true;
    }

    // Disconnect from Harmony patch (stop physics-frame sampling)
    Patches.PhysicsFramePatch.ActiveRecorder = null;
    UnsubscribePartEvents();

    // NOTE: IsRecording stays TRUE. The recording is still conceptually active,
    // just in background mode. The Harmony patch is disconnected so OnPhysicsFrame
    // won't fire.
}
```

**Critical**: `IsRecording` remains `true`. `CaptureAtStop` is NOT created. The orbit segment is left open.

#### 5.2 New Property: `IsBackgrounded`

```csharp
public bool IsBackgrounded => IsRecording && Patches.PhysicsFramePatch.ActiveRecorder != this;
```

#### 5.3 New Method: `StartRecording(bool isPromotion)` overload

When `isPromotion` is true:
- Skip pre-launch resource capture (they belong to the tree root)
- Skip the "Recording STARTED" screen message
- All other initialization is the same (clear lists, cache modules, seed fairings, connect Harmony)

#### 5.4 Extracting Part Event State Reset

Extract `ResetPartEventTrackingState(Vessel v)` from `StartRecording` to share with promotion path. Pure refactoring.

#### 5.5 New Fields

```csharp
public RecordingTree ActiveTree { get; set; }  // null when not in tree mode
public bool TransitionToBackgroundPending { get; internal set; }
```

(`TransitionTargetPid` is not needed - `onVesselSwitchComplete` provides the new vessel directly.)

#### 5.6 Changes to `OnPhysicsFrame` - Structural Refactor of PID Mismatch Block

The current PID mismatch block (lines 3339-3409) has this structure:
1. `ContinueOnEva` early return (line 3347)
2. `CaptureAtStop` creation + `IsRecording = false` (lines 3356-3382) - runs for ALL other decisions
3. Decision dispatch (ChainToVessel, DockMerge, UndockSwitch, Stop)

**Tree decisions MUST NOT create `CaptureAtStop` or set `IsRecording = false`.** They must early-return BEFORE the capture block. The refactored structure:

```csharp
if (v.persistentId != RecordingVesselId)
{
    // 1. Dock merge guard (NEW) - must be first
    if (DockMergePending)
    {
        // Fall through to existing capture+stop flow below
        // (DockMergePending was set by OnPartCouple)
    }
    else
    {
        VesselSwitchDecision decision = DecideOnVesselSwitch(
            RecordingVesselId, v.persistentId, v.isEVA, RecordingStartedAsEva,
            UndockSiblingPid, activeTree: ActiveTree);

        // 2. ContinueOnEva - early return (existing, unchanged)
        if (decision == VesselSwitchDecision.ContinueOnEva) { ... return; }

        // 3. Tree decisions - early return BEFORE CaptureAtStop (NEW)
        if (decision == VesselSwitchDecision.TransitionToBackground)
        {
            TransitionToBackground();
            TransitionToBackgroundPending = true;
            return;
        }
        if (decision == VesselSwitchDecision.PromoteFromBackground)
        {
            // Cannot promote here - vessel may not be loaded yet.
            // TransitionToBackground the current recorder, let onVesselSwitchComplete handle promotion.
            TransitionToBackground();
            TransitionToBackgroundPending = true;
            return;
        }
    }

    // 4. Existing CaptureAtStop + IsRecording=false block (unchanged)
    // 5. Existing decision dispatch (ChainToVessel, DockMerge, UndockSwitch, Stop) (unchanged)
}
```

**Key insight for PromoteFromBackground**: When `OnPhysicsFrame` detects the PID mismatch, it fires for the NEW active vessel. The old vessel's recording transitions to background regardless. The actual promotion of the new vessel's recording happens later in `onVesselSwitchComplete`. So both `TransitionToBackground` and `PromoteFromBackground` do the same thing in `OnPhysicsFrame` - background the current recording. The difference is that `onVesselSwitchComplete` will then promote the target.

---

### 6. Changes to `ParsekFlight`

#### 6.1 New State Fields

```csharp
private RecordingTree activeTree;  // null when not in tree mode
```

#### 6.2 New Event: `onVesselSwitchComplete`

Subscribe in `Start()`, unsubscribe in `OnDestroy()`.

#### 6.3 New Method: `OnVesselSwitchComplete` - Primary Detection Mechanism

`onVesselSwitchComplete` is the **primary** mechanism for detecting vessel switches in tree mode, not just a secondary handler for promotion. This is necessary because:

- `OnPhysicsFrame` has an `if (isOnRails) return;` guard at line 3337 that prevents PID mismatch detection during time warp.
- If the player presses `]` during time warp, `OnPhysicsFrame` never sees it.
- `onVesselSwitchComplete` fires regardless of on-rails state.

The handler must handle BOTH transition (old vessel → background) AND promotion (new vessel → active):

```csharp
void OnVesselSwitchComplete(Vessel newVessel)
{
    if (activeTree == null) return;
    if (newVessel == null) return;

    // If the current recorder is still active (OnPhysicsFrame didn't catch the switch,
    // e.g. because isOnRails was true), transition it to background now
    if (recorder != null && recorder.IsRecording && !recorder.IsBackgrounded
        && recorder.RecordingVesselId != newVessel.persistentId)
    {
        recorder.TransitionToBackground();
        FlushRecorderToTreeRecording(recorder, activeTree);
        // Add old vessel to BackgroundMap
        string oldRecId = activeTree.ActiveRecordingId;
        RecordingStore.Recording oldRec;
        if (oldRecId != null && activeTree.Recordings.TryGetValue(oldRecId, out oldRec)
            && oldRec.VesselPersistentId != 0)
        {
            activeTree.BackgroundMap[oldRec.VesselPersistentId] = oldRecId;
        }
        activeTree.ActiveRecordingId = null;
        recorder.IsRecording = false;  // cleanup: old recorder is done
        recorder = null;
    }

    // Promote the new vessel if it's in the tree
    uint newPid = newVessel.persistentId;
    string backgroundRecordingId;
    if (activeTree.BackgroundMap.TryGetValue(newPid, out backgroundRecordingId))
    {
        PromoteRecordingFromBackground(backgroundRecordingId, newVessel);
    }
}
```

**Idempotency**: If `OnPhysicsFrame` already handled the transition (set `TransitionToBackgroundPending`), the `recorder.IsRecording && !recorder.IsBackgrounded` check will be false (recorder was already backgrounded), so the transition block is skipped. Only the promotion fires. If `OnPhysicsFrame` did NOT handle it (on-rails), this handler does both.

#### 6.4 New Method: `FlushRecorderToTreeRecording`

Appends the current recorder's accumulated data (points, orbit segments, part events) to the tree's Recording object. Sorts part events by UT. **After flushing, sets `recorder.IsRecording = false`** to prevent dangling recorders with `IsRecording = true` from causing subtle bugs. Also ensures `VesselPersistentId` is populated on the tree Recording (required for `RebuildBackgroundMap` to work after save/load).

#### 6.5 New Method: `PromoteRecordingFromBackground`

Creates a new `FlightRecorder` for the promoted vessel, calls `StartRecording(isPromotion: true)`, updates `activeTree.ActiveRecordingId` and `BackgroundMap`.

**Architectural decision**: Each active→background→active cycle creates a fresh `FlightRecorder`. The recorder's accumulated data is flushed to the tree's Recording object on transition. Part module caches are rebuilt fresh on promotion (cached references are stale after unload/reload). This mirrors the existing on-rails pattern.

#### 6.6 `TransitionToBackgroundPending` Handler in `Update()`

When `OnPhysicsFrame` detects the switch (not on rails), it sets `TransitionToBackgroundPending`. The `Update()` handler:
- Flush recorder data to tree Recording (sets `IsRecording = false`)
- Add old vessel to `BackgroundMap`
- Set `ActiveRecordingId` to null
- Clear `recorder = null`

**Note**: This handler is for the `OnPhysicsFrame` path. The `onVesselSwitchComplete` path handles its own flush directly. The `Update()` handler must check that the recorder hasn't already been cleaned up by `onVesselSwitchComplete` (guard: `recorder != null && recorder.TransitionToBackgroundPending`).

#### 6.7 Scene Change Handling

Minimum for Task 2: if tree active, stop recorder, flush data, clear tree. Full tree commit deferred to Task 7.

#### 6.8 `OnFlightReady` Changes

Clear tree state: `activeTree = null`.

---

### 7. Interaction with Existing Chain Behavior

#### 7.1 Chain Decisions Take Priority

The decision priority in `DecideOnVesselSwitch` ensures chains are unaffected:
- `UndockSwitch` checked before tree logic (step 2)
- `ContinueOnEva` checked before tree logic (step 3)
- `ChainToVessel` checked before tree logic (step 4)
- `DockMergePending` short-circuits before `DecideOnVesselSwitch` is even called

#### 7.2 No Change to Atmosphere/SOI Splits

Atmosphere and SOI boundary splits use `StopRecordingForChainBoundary()` followed by `StartRecording()`. These are intra-recording chain segments and do not involve vessel switching. Completely orthogonal to tree vessel switch logic.

#### 7.3 Chain Events During Tree Mode - Deferred to Tasks 4/5

When a tree is active and the player undocks, docks, or goes EVA, the existing chain logic runs identically to non-tree mode (creates chain segments via `CommitDockUndockSegment` / `CommitChainSegment`). This produces chain segments instead of tree branch points, which is **incorrect for tree semantics** but **correct for Task 2 scope**.

Tasks 4 (split events) and 5 (merge events) will intercept these events BEFORE the chain handlers when a tree is active, creating proper branch points instead. Task 2 does NOT need guards or special handling for chain events - they work exactly as before. The tree decision logic only fires for plain `[`/`]` vessel switches where no chain event (dock/undock/EVA/board) is in progress.

**`pendingDockAsTarget`**: When the recorder's vessel is the dock target (pid unchanged), `OnPartCouple` calls `StopRecordingForChainBoundary()` directly. In tree mode this should eventually create a merge branch point. Deferred to Task 5.

---

### 8. `BackgroundMap` Management

| When | Action |
|------|--------|
| `TransitionToBackground` | Add old vessel to `BackgroundMap` |
| `PromoteFromBackground` | Remove promoted vessel from `BackgroundMap` |
| Tree creation (Task 4) | Non-active child added to `BackgroundMap` |
| Terminal event (Task 6) | Destroyed/recovered vessel removed |
| Dock merge (Task 5) | Absorbed vessel removed |

`ActiveRecordingId`:
- `TransitionToBackground` → set to `null`
- `PromoteFromBackground` → set to promoted recording's id
- Tree creation → set to root recording's id

---

### 9. Unit Tests

New file `Source/Parsek.Tests/VesselSwitchTreeTests.cs`:

1. `DecideOnVesselSwitch_NoTree_SameAsLegacy_Stop` - `activeTree = null`, different PIDs → `Stop`
2. `DecideOnVesselSwitch_TreeActive_TargetInBackgroundMap_ReturnsPromote` - background PID match → `PromoteFromBackground`
3. `DecideOnVesselSwitch_TreeActive_TargetNotInTree_ReturnsTransition` - no background match → `TransitionToBackground`
4. `DecideOnVesselSwitch_TreeActive_UndockSibling_TakesPriority` - undock sibling wins over tree → `UndockSwitch`
5. `DecideOnVesselSwitch_TreeActive_EvaToEva_TakesPriority` - EVA wins over tree → `ContinueOnEva`
6. `DecideOnVesselSwitch_TreeActive_ChainToVessel_TakesPriority` - boarding wins over tree → `ChainToVessel`
7. `DecideOnVesselSwitch_TreeActive_SamePid_ReturnsNone` - same PID → `None`
8. `DecideOnVesselSwitch_TreeActive_EmptyBackgroundMap_ReturnsTransition` - empty map → `TransitionToBackground`
9. `DecideOnVesselSwitch_TreeNull_DoesNotCrash` - explicit null → legacy behavior
10. `BackgroundMap_RebuiltCorrectly_AfterTransition` - map consistency check

---

### 10. In-Game Tests

1. **Basic vessel switch with tree**: launch, undock, switch `]`/`[`, verify recording continues
2. **Switch to external vessel**: switch to unrelated vessel, verify tree goes background, switch back, verify resume
3. **Chain events during tree**: EVA/board during tree, verify chain continuation works
4. **Scene exit**: leave flight with tree active, verify no crash/orphans

---

### 11. Risk Assessment

| Risk | Mitigation |
|------|-----------|
| Race between `onVesselSwitchComplete` and `OnPhysicsFrame` | `onVesselSwitchComplete` is idempotent - checks `recorder.IsRecording && !IsBackgrounded` before transitioning. If `OnPhysicsFrame` already handled it, the check is false and only promotion fires. |
| Vessel switch during time warp (`isOnRails` blocks `OnPhysicsFrame`) | `onVesselSwitchComplete` handles both transition AND promotion, so it works regardless of on-rails state |
| Rapid vessel switching (`]]]`) | Each `onVesselSwitchComplete` fires in order. Each one transitions the old recorder (if still active) and promotes the new vessel. Short intermediate recordings are acceptable. |
| Background vessel destruction | Guard added in `OnVesselWillDestroy` for tree BackgroundMap check; full logic deferred to Task 6 |
| DockMergePending during tree | NEW explicit guard in `OnPhysicsFrame` before `DecideOnVesselSwitch` - prevents tree logic from intercepting dock PID changes |
| Backward compatibility | `activeTree = null` default skips all tree logic; existing tests pass unchanged |
| Dangling recorder with `IsRecording = true` | `FlushRecorderToTreeRecording` sets `IsRecording = false` after flushing; `onVesselSwitchComplete` also cleans up |
| Chain events during tree mode | Chain logic runs unchanged in Task 2 scope; proper tree branching deferred to Tasks 4/5 |

---

### 12. Implementation Order

1. Add enum values to `VesselSwitchDecision`
2. Add `ActiveTree` property to `FlightRecorder`
3. Modify `DecideOnVesselSwitch` - new `activeTree` parameter + tree logic
4. Extract `ResetPartEventTrackingState` helper from `StartRecording`
5. Add `TransitionToBackground` method to `FlightRecorder`
6. Add `StartRecording(bool isPromotion)` parameter
7. Add `TransitionToBackgroundPending` property
8. Subscribe to `onVesselSwitchComplete` in `ParsekFlight.Start()`
9. Add `OnVesselSwitchComplete` handler in `ParsekFlight`
10. Add `TransitionToBackgroundPending` handler in `ParsekFlight.Update()`
11. Add `FlushRecorderToTreeRecording` method in `ParsekFlight`
12. Add `PromoteRecordingFromBackground` method in `ParsekFlight`
13. Refactor `OnPhysicsFrame` PID mismatch block - DockMergePending guard first, tree decisions before CaptureAtStop block
14. Add tree cleanup to `OnSceneChangeRequested` and `OnFlightReady`
15. Write unit tests
16. Run `dotnet test` - all existing + new tests pass

---

### 13. Files Modified

| File | Changes |
|------|---------|
| `Source/Parsek/FlightRecorder.cs` | New enum values, `ActiveTree`/`IsBackgrounded`/`TransitionToBackgroundPending` properties, `TransitionToBackground()` method, `DecideOnVesselSwitch` signature + logic, `ResetPartEventTrackingState` extraction, `StartRecording(bool isPromotion)`, `OnPhysicsFrame` PID mismatch refactor (DockMergePending guard + tree decisions before CaptureAtStop block) |
| `Source/Parsek/ParsekFlight.cs` | `activeTree` field, `onVesselSwitchComplete` subscription, `OnVesselSwitchComplete`/`PromoteRecordingFromBackground`/`FlushRecorderToTreeRecording` methods, `Update()` transition handler, scene change + flight ready cleanup |
| `Source/Parsek.Tests/VesselSwitchTreeTests.cs` | New file - unit tests for `DecideOnVesselSwitch` with tree parameter |

---

### 14. What This Task Does NOT Do

- Does not create trees (Task 4: split events)
- Does not implement background recording loop (Task 3)
- Does not detect vessel destruction/recovery of background vessels (Task 6)
- Does not implement tree commit/spawn (Task 7)
- Does not change the merge dialog (Task 8)
- Does not modify ghost playback (Task 9)

Task 2 provides the **vessel switch decision logic and transition methods** that all subsequent tasks build upon. It is the behavioral foundation for tree mode.

**Dependency note for Task 3**: `TransitionToBackground` leaves an orbit segment open-ended (`isOnRails = true`, no `endUT`). Task 3 (background recording infrastructure) is responsible for monitoring background vessels and updating their orbit segments when SOI changes occur or when the recording ends. Until Task 3 is implemented, background orbit segments are only closed when the vessel is promoted back to active (via `PromoteFromBackground` closing the orbit segment).

**Dependency note for Task 4**: Until Task 4 is implemented, there is no code that creates `RecordingTree` instances or populates `activeTree`. The tree decision paths in `DecideOnVesselSwitch` and `onVesselSwitchComplete` are dormant dead code. This is acceptable scaffolding - Task 4 will wire up tree creation on split events, activating this code.
