# Task 6: Terminal Event Detection (Destruction, Recovery)

## Workflow

This task follows a multi-stage review pipeline using Opus 4.6 agents, orchestrated by the main session:

1. **Plan** -- Opus 4.6 subagent explores the codebase and writes a detailed implementation plan
2. **Plan review** -- Fresh Opus 4.6 subagent reviews the plan for correctness, completeness, and risk
3. **Orchestrator review** -- Main session reviews the plan with full project context and fixes issues
4. **Implement** -- Fresh Opus 4.6 subagent implements the plan
5. **Implementation review** -- Fresh Opus 4.6 subagent reviews the implementation and fixes issues
6. **Final review** -- Main session reviews the implementation considering the larger architectural context
7. **Commit** -- Main session commits the implementation
8. **Next task briefing** -- Main session presents the next task, explains its role and how it fits into the overall plan

---

## Plan

### 1. Overview

Task 6 detects when a background vessel in the recording tree reaches a terminal state -- either **destruction** (crash, explosion, atmospheric burnup) or **recovery** (player recovers vessel via Tracking Station or StageRecovery mod). When a terminal event occurs, the recording is finalized: `ExplicitEndUT` is set, `TerminalStateValue` is assigned (`Destroyed` or `Recovered`), the last known position/orbit is captured, and the vessel is removed from `BackgroundMap` so it is no longer tracked.

This is conceptually simpler than Tasks 4 (split) and 5 (merge) -- no new recordings are created, no branch points are added, no recorders need to start. A terminal event is a leaf-making operation: it marks the recording as finished with no children.

Tasks 1-5 built the foundation:
- **Task 1**: `TerminalState` enum with `Destroyed` (=4), `Recovered` (=5); `TerminalStateValue` nullable field on Recording; terminal orbit fields (`TerminalOrbitInclination`, `TerminalOrbitEccentricity`, etc.); `TerminalPosition` nullable `SurfacePosition`; serialization for all of these in `RecordingTree.SaveRecordingInto`/`LoadRecordingFrom`
- **Task 3**: `BackgroundRecorder` with `OnBackgroundVesselWillDestroy(v)` (closes orbit segments, cleans up tracking state) and `OnVesselRemovedFromBackground(pid)` (closes orbit segments, cleans up tracking state)
- **Task 5**: `dockingInProgress` HashSet guarding docking-induced destruction from being misclassified

The existing `OnVesselWillDestroy` handler in `ParsekFlight.cs` (line 827) already:
1. Delegates to `recorder?.OnVesselWillDestroy(v)` for active vessel destruction
2. Calls `backgroundRecorder?.OnBackgroundVesselWillDestroy(v)` (if not in `dockingInProgress`)
3. Handles continuation vessel destruction for legacy chain mode

Task 6 adds the deferred destruction check and recovery handler specifically for tree-mode background vessels.

### 1.1 Why Deferred Check Is Necessary

KSP fires `onVesselWillDestroy` (and `onVesselDestroy`) for two completely different reasons:

1. **Actual destruction** -- vessel crashed, exploded, burned up in atmosphere, or was terminated. The vessel ceases to exist in the game.
2. **Unloading** -- vessel crosses out of physics range (~2.5km). KSP destroys the loaded `Vessel` object but keeps the vessel in `FlightGlobals.Vessels` as an unloaded entry. The vessel is still alive -- it is just on rails.

These two cases are indistinguishable at the time `onVesselWillDestroy` fires. The vessel object is about to be destroyed in both cases. The `vessel.loaded` flag does not help because on-rails destruction (orbit decay) also has `loaded = false`.

The design doc specifies the solution: **defer the check by one frame** using a coroutine. After one frame:
- If `FlightGlobals.Vessels` still contains a vessel with the same `persistentId` -- it was an unload. Ignore.
- If no vessel with that `persistentId` exists in `FlightGlobals.Vessels` -- it was real destruction. Terminate the recording.

This is the same deferred-check pattern used in Tasks 4 (deferred joint break) and 5 (deferred dock merge confirmation), so it is well-established in the codebase.

---

### 2. Existing Infrastructure Audit

#### 2.1 `OnVesselWillDestroy` in ParsekFlight.cs (line 827)

```csharp
void OnVesselWillDestroy(Vessel v)
{
    recorder?.OnVesselWillDestroy(v);

    // Skip background recorder cleanup for vessels being absorbed by docking.
    if (!dockingInProgress.Contains(v.persistentId))
        backgroundRecorder?.OnBackgroundVesselWillDestroy(v);

    // continuation vessel tracking (legacy chain mode)...
}
```

Subscribed via `GameEvents.onVesselWillDestroy.Add(OnVesselWillDestroy)` in `Start()` (line 191), removed in `OnDestroy()` (line 648).

**Current behavior for background vessels**: `OnBackgroundVesselWillDestroy` closes open orbit segments and removes tracking state from `loadedStates`/`onRailsStates`. However, it does NOT set `TerminalStateValue` on the recording, does NOT set `ExplicitEndUT`, does NOT capture terminal orbit/position, and does NOT remove from `BackgroundMap`. That is what Task 6 adds.

#### 2.2 `BackgroundRecorder.OnBackgroundVesselWillDestroy` (line 448)

```csharp
public void OnBackgroundVesselWillDestroy(Vessel v)
{
    if (v == null || tree == null) return;
    uint pid = v.persistentId;
    if (!tree.BackgroundMap.ContainsKey(pid)) return;
    double ut = Planetarium.GetUniversalTime();
    // Closes orbit segment, removes from onRailsStates/loadedStates
}
```

This is a cleanup method that handles the recording infrastructure (orbit segments, tracking dictionaries). It is called synchronously from `OnVesselWillDestroy`. It does NOT modify the recording's metadata (terminal state, end UT, etc.) -- that is the caller's responsibility. Task 6 will call this method as part of the terminal event flow.

#### 2.3 `BackgroundRecorder.OnVesselRemovedFromBackground` (line 282)

Similar to `OnBackgroundVesselWillDestroy` but called when a vessel is deliberately removed from background tracking (promotion to active, merge absorption). Also closes orbit segments and cleans up dictionaries. Used by the merge handler in Task 5.

For terminal events, we should use `OnBackgroundVesselWillDestroy` (which checks `BackgroundMap.ContainsKey` and performs the same cleanup) rather than `OnVesselRemovedFromBackground` (which does not check `BackgroundMap`). However, since `OnBackgroundVesselWillDestroy` is already called from the existing `OnVesselWillDestroy` handler before the deferred check, we need to ensure the cleanup is not doubled. See section 7 for the sequencing.

#### 2.4 `dockingInProgress` HashSet (ParsekFlight.cs, line 139)

```csharp
internal HashSet<uint> dockingInProgress = new HashSet<uint>();
```

Set by Task 5's dock merge handler in `OnPartCouple`: when a dock merge is detected, the absorbed vessel's PID is added. Cleared in the deferred dock merge completion handler in `Update()` (line 287) and in `OnDestroy`/`CleanupTreeState` (lines 703, 2587).

The existing `OnVesselWillDestroy` already checks this: `if (!dockingInProgress.Contains(v.persistentId))`. Task 6's deferred destruction check must also check `dockingInProgress` because the docking confirmation can take multiple frames -- the absorbed vessel's destruction event fires within the docking window.

#### 2.5 `TerminalState` Enum (TerminalState.cs)

```csharp
public enum TerminalState
{
    Orbiting   = 0,
    Landed     = 1,
    Splashed   = 2,
    SubOrbital = 3,
    Destroyed  = 4,
    Recovered  = 5,
    Docked     = 6,
    Boarded    = 7
}
```

Task 6 uses `Destroyed` (=4) and `Recovered` (=5). Already defined -- no enum changes needed.

#### 2.6 Terminal Data Fields on Recording (RecordingStore.cs, lines 88-103)

Already defined by Task 1:
- `TerminalStateValue` (nullable `TerminalState`)
- `TerminalOrbitInclination`, `TerminalOrbitEccentricity`, `TerminalOrbitSemiMajorAxis`, `TerminalOrbitLAN`, `TerminalOrbitArgumentOfPeriapsis`, `TerminalOrbitMeanAnomalyAtEpoch`, `TerminalOrbitEpoch`, `TerminalOrbitBody` (Keplerian elements as doubles + body name)
- `TerminalPosition` (nullable `SurfacePosition`)
- `SurfacePos` (nullable `SurfacePosition` -- background recording's current surface position, updated continuously by `BackgroundRecorder`)

Serialization already handled in `RecordingTree.SaveRecordingInto`/`LoadRecordingFrom`. No new fields or serialization changes needed.

#### 2.7 `RebuildBackgroundMap` (RecordingTree.cs, line 110)

```csharp
public void RebuildBackgroundMap()
{
    BackgroundMap.Clear();
    foreach (var kvp in Recordings)
    {
        var rec = kvp.Value;
        if (rec.VesselPersistentId != 0
            && rec.TerminalStateValue == null
            && rec.ChildBranchPointId == null
            && rec.RecordingId != ActiveRecordingId)
        {
            BackgroundMap[rec.VesselPersistentId] = rec.RecordingId;
        }
    }
}
```

Key: recordings with a non-null `TerminalStateValue` are already excluded from `BackgroundMap` on rebuild. This means that if we set `TerminalStateValue` before removing from `BackgroundMap`, the data is self-consistent even across save/load.

#### 2.8 `FlightRecorder.FindVesselByPid` (FlightRecorder.cs, line 3831)

```csharp
internal static Vessel FindVesselByPid(uint pid)
{
    if (FlightGlobals.Vessels == null) return null;
    for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
    {
        Vessel vessel = FlightGlobals.Vessels[i];
        if (vessel != null && vessel.persistentId == pid)
            return vessel;
    }
    return null;
}
```

Used by the deferred check to see if the vessel still exists in `FlightGlobals.Vessels` after one frame.

#### 2.9 Existing Coroutine Pattern (ParsekFlight.cs)

The codebase already uses `StartCoroutine` with `yield return null` (defer one frame) extensively:
- `DeferredUndockBranch` (line 1550): `yield return null` then checks vessel state
- `DeferredEvaBranch` (line 1600): `yield return null` then sets up EVA branch
- `DeferredJointBreakCheck` (line 1668): `yield return null` then checks for new vessels

Task 6 will follow the same pattern: `yield return null` to defer by one frame, then check `FlightGlobals.Vessels` for the vessel's `persistentId`.

#### 2.10 KSP Recovery Events

KSP provides two recovery-related events:
- `GameEvents.onVesselRecoveryProcessing` -- fires when a vessel recovery is initiated (e.g., from Tracking Station, "Recover Vessel" button, or StageRecovery mod). Signature: `EventData<ProtoVessel, MissionRecovery, bool>`.
- `GameEvents.onVesselRecovered` -- fires after recovery is complete. Signature: `EventData<ProtoVessel, bool>`.

`onVesselRecoveryProcessing` is the preferred handler because it fires early enough to capture state. The vessel's `persistentId` is available via `ProtoVessel.vesselID` (but note: `ProtoVessel` uses `vesselID` as a `Guid`, not `persistentId`). For matching to our `BackgroundMap`, we need the `persistentId`. The `ProtoVessel` has a `protoPartSnapshots` list from which we can access `persistentId`, but the most reliable approach is to match via `FlightGlobals.Vessels` before the vessel is removed.

However, recovery of background vessels is an edge case in the recording tree context. Recovery typically happens from the Tracking Station (different scene -- Flight scene is not active), or via the "Recover Vessel" button on a landed active vessel. For background vessels, recovery during Flight would require StageRecovery mod or similar. The primary scenario is the player recovering from the Tracking Station after the tree is committed. Since tree recordings are only live during the Flight scene, a vessel recovered from the Tracking Station is not a tree concern -- the tree is already committed or the Flight scene is not running.

**For now, we will subscribe to `onVesselRecoveryProcessing` to catch the rare in-flight recovery case.** The handler will look up the vessel by iterating `FlightGlobals.Vessels` to find a matching `persistentId` before it is removed, then check the `BackgroundMap`.

---

### 3. Destruction Detection Flow

The destruction detection flow has three phases: immediate capture, deferred check, and terminal state assignment.

#### 3.1 Phase 1: Immediate Capture (in `OnVesselWillDestroy`)

When `onVesselWillDestroy` fires for a vessel in the `BackgroundMap`:

1. **Check `dockingInProgress`** -- if the vessel's PID is in `dockingInProgress`, skip entirely (it is a dock absorption, not destruction). The existing code already does this.
2. **Check `BackgroundMap`** -- if the vessel's PID is not in `BackgroundMap`, skip (not a tree vessel).
3. **Capture last known state** -- before the vessel object is destroyed, extract the data we will need if this turns out to be real destruction:
   - The vessel's current situation (`Vessel.Situations`)
   - If orbiting/suborbital: Keplerian elements from `vessel.orbit`
   - If landed/splashed: surface position (lat/lon/alt/rotation/body)
   - The current UT
4. **Store captured data** in a pending destruction struct and start the deferred check coroutine.
5. **Do NOT yet modify the recording.** The vessel might just be unloading.

#### 3.2 Phase 2: Deferred Check (one frame later)

After `yield return null`:

1. **Re-check `dockingInProgress`** -- the dock merge handler might have added the PID between the destroy event and this frame. If the PID is now in `dockingInProgress`, abort.
2. **Look up vessel by PID** using `FlightRecorder.FindVesselByPid(pid)`:
   - If found: vessel was just unloaded. Discard the pending destruction data. Log and return.
   - If NOT found: vessel is truly destroyed. Proceed to Phase 3.

#### 3.3 Phase 3: Terminal State Assignment

1. **Look up the recording** in `activeTree.Recordings` using the recording ID from `BackgroundMap[pid]`.
2. **Set terminal state**:
   - `rec.TerminalStateValue = TerminalState.Destroyed`
   - `rec.ExplicitEndUT = capturedUT` (the UT from Phase 1)
3. **Capture terminal position/orbit** from the data saved in Phase 1:
   - If the vessel was `ORBITING` or `SUB_ORBITAL` or `ESCAPING`: copy Keplerian elements into `rec.TerminalOrbit*` fields
   - If the vessel was `LANDED` or `SPLASHED`: set `rec.TerminalPosition` to the captured `SurfacePosition`
   - If `FLYING` (atmospheric flight): capture orbit elements (best available approximation -- the suborbital trajectory before atmospheric destruction)
4. **Remove from `BackgroundMap`**: `activeTree.BackgroundMap.Remove(pid)`
5. **Notify BackgroundRecorder**: This is already done by the existing `OnBackgroundVesselWillDestroy` call in Phase 1. The BackgroundRecorder cleanup (closing orbit segments, removing tracking state) happens synchronously before the deferred check. This is correct: the tracking state should be cleaned up immediately even if the vessel is just unloading, because the BackgroundRecorder's `Update()` cycle will re-initialize tracking when the vessel reloads (via `OnVesselGoOnRails`/`OnVesselGoOffRails` events that BackgroundRecorder handles).

Wait -- this needs clarification. The current `OnBackgroundVesselWillDestroy` cleans up the BackgroundRecorder's internal tracking state (`onRailsStates`, `loadedStates`), but it does NOT remove the vessel from `BackgroundMap`. If the vessel is just unloading, BackgroundRecorder needs the vessel to remain in `BackgroundMap` so it can be re-initialized when the vessel reloads. This is correct behavior: `BackgroundMap` removal only happens on terminal events.

However, there is a subtlety: `OnBackgroundVesselWillDestroy` closes the open orbit segment and adds it to the recording. If the vessel is just unloading and then reloads, BackgroundRecorder will re-initialize and open a new orbit segment. The small gap between unload and reload will be covered by the orbit segment that was closed. This is acceptable -- no data loss occurs.

**The existing call sequence is correct. No changes needed to the `OnBackgroundVesselWillDestroy` call.** The deferred check only needs to add the terminal state assignment and `BackgroundMap` removal.

6. **Log the terminal event**: `ParsekLog.Warn("Flight", $"Background vessel destroyed: pid={pid} recId={recId}")`.

#### 3.4 Edge Case: Multiple Destructions in Quick Succession

If multiple background vessels are destroyed in the same frame (e.g., collision), each gets its own deferred check coroutine. Since each coroutine captures the PID and recording ID independently, they do not interfere with each other.

#### 3.5 Edge Case: Vessel Destroyed During Pending Split/Merge

If a vessel is destroyed while a split or merge is pending (`pendingSplitInProgress` or `pendingTreeDockMerge`), the destruction handler should still run. The deferred split/merge handlers will handle the absence of the vessel gracefully -- they already use `FindVesselByPid` which returns null for destroyed vessels.

---

### 4. Recovery Detection Flow

#### 4.1 Event Subscription

Subscribe to `GameEvents.onVesselRecoveryProcessing` in `Start()`:
```csharp
GameEvents.onVesselRecoveryProcessing.Add(OnVesselRecoveryProcessing);
```

Unsubscribe in `OnDestroy()`:
```csharp
GameEvents.onVesselRecoveryProcessing.Remove(OnVesselRecoveryProcessing);
```

#### 4.2 Handler Signature

```csharp
void OnVesselRecoveryProcessing(ProtoVessel pv, MissionRecovery mr, bool quickRecover)
```

#### 4.3 Matching ProtoVessel to BackgroundMap

The `ProtoVessel` does not directly expose `persistentId`. We need to match it to our `BackgroundMap`. The approach:

1. Get the `ProtoVessel`'s `persistentId` via its `vesselRef.persistentId` (if `vesselRef` is not null) or by checking `pv.protoPartSnapshots[0].persistentId` as a fallback.
2. Actually, the simplest and most reliable method: `ProtoVessel` has a `vesselRef` field that is the `Vessel` object. If non-null, use `pv.vesselRef.persistentId`. If null, iterate `FlightGlobals.Vessels` to find a vessel whose `protoVessel == pv`.

However, for background vessels recovered during flight (which is the only case relevant to the tree), the `Vessel` object should still exist at the time `onVesselRecoveryProcessing` fires. The safest approach:

```csharp
void OnVesselRecoveryProcessing(ProtoVessel pv, MissionRecovery mr, bool quickRecover)
{
    if (activeTree == null) return;
    if (pv == null) return;

    // Find the vessel's persistentId
    uint pid = 0;
    if (pv.vesselRef != null)
    {
        pid = pv.vesselRef.persistentId;
    }
    else
    {
        // Fallback: search FlightGlobals
        for (int i = 0; i < FlightGlobals.Vessels.Count; i++)
        {
            if (FlightGlobals.Vessels[i]?.protoVessel == pv)
            {
                pid = FlightGlobals.Vessels[i].persistentId;
                break;
            }
        }
    }

    if (pid == 0) return;

    string recordingId;
    if (!activeTree.BackgroundMap.TryGetValue(pid, out recordingId)) return;

    // This is a tree vessel being recovered
    TerminateBackgroundRecording(pid, recordingId, TerminalState.Recovered);
}
```

#### 4.4 Terminal State for Recovery

Recovery is synchronous -- no deferred check needed. When `onVesselRecoveryProcessing` fires, the vessel is definitely being recovered (not unloading). The handler:

1. Looks up the recording in `activeTree.Recordings[recordingId]`
2. Sets `rec.TerminalStateValue = TerminalState.Recovered`
3. Sets `rec.ExplicitEndUT = Planetarium.GetUniversalTime()`
4. Captures terminal orbit/position (same logic as destruction)
5. Calls `backgroundRecorder?.OnVesselRemovedFromBackground(pid)` to clean up tracking state
6. Removes from `activeTree.BackgroundMap`
7. Logs the terminal event

#### 4.5 Recovery in Tracking Station

If the player is in the Tracking Station and recovers a vessel from a committed recording tree, the Flight scene is not running -- `ParsekFlight` does not exist. This is fine: the tree is already committed at this point, and the vessel was spawned as a real game vessel. KSP handles the recovery natively. Parsek does not need to track this because the tree's recordings are finalized.

---

### 5. Terminal Data Capture

#### 5.1 Orbit Capture

When the vessel is in a non-landed state at terminal event time, capture Keplerian elements:

```csharp
void CaptureTerminalOrbit(RecordingStore.Recording rec, Vessel v)
{
    if (v.orbit == null) return;

    rec.TerminalOrbitInclination = v.orbit.inclination;
    rec.TerminalOrbitEccentricity = v.orbit.eccentricity;
    rec.TerminalOrbitSemiMajorAxis = v.orbit.semiMajorAxis;
    rec.TerminalOrbitLAN = v.orbit.LAN;
    rec.TerminalOrbitArgumentOfPeriapsis = v.orbit.argumentOfPeriapsis;
    rec.TerminalOrbitMeanAnomalyAtEpoch = v.orbit.meanAnomalyAtEpoch;
    rec.TerminalOrbitEpoch = v.orbit.epoch;
    rec.TerminalOrbitBody = v.mainBody?.name ?? "Kerbin";
}
```

This follows the same pattern as `BackgroundRecorder.InitializeOnRailsState` (line 546), which captures orbital elements for orbit segments.

#### 5.2 Surface Position Capture

When the vessel is landed or splashed:

```csharp
void CaptureTerminalPosition(RecordingStore.Recording rec, Vessel v)
{
    rec.TerminalPosition = new SurfacePosition
    {
        body = v.mainBody?.name ?? "Kerbin",
        latitude = v.latitude,
        longitude = v.longitude,
        altitude = v.altitude,
        rotation = v.srfRelRotation,
        situation = v.situation == Vessel.Situations.SPLASHED
            ? SurfaceSituation.Splashed
            : SurfaceSituation.Landed
    };
}
```

Same pattern as `BackgroundRecorder.InitializeOnRailsState` (line 526).

#### 5.3 Situation-to-TerminalState Mapping (for non-Destroyed/Recovered cases)

For future use (Task 7 commit), we will also need to map a vessel's current situation to a `TerminalState` for surviving leaf vessels:

| `Vessel.Situations` | `TerminalState` |
|---------------------|-----------------|
| ORBITING            | Orbiting        |
| ESCAPING            | Orbiting        |
| SUB_ORBITAL         | SubOrbital      |
| FLYING              | SubOrbital      |
| LANDED              | Landed          |
| SPLASHED            | Splashed        |
| PRELAUNCH           | Landed          |

This mapping is not needed in Task 6 (which only produces `Destroyed` and `Recovered`), but the orbit/position capture helpers should be designed for reuse by Task 7.

#### 5.4 Deferred Destruction: What If Vessel Object Is Already Gone?

In Phase 1 of the destruction flow, we capture the vessel's state while the `Vessel` object is still available (inside `onVesselWillDestroy`, the vessel is not yet destroyed). We store this data in a temporary struct. By Phase 3 (after the deferred check confirms destruction), we use the stored data -- we do NOT access the vessel object, which is now destroyed.

This means we need a lightweight data struct to hold the captured state between Phase 1 and Phase 3:

```csharp
private struct PendingDestruction
{
    public uint vesselPid;
    public string recordingId;
    public double capturedUT;
    public Vessel.Situations situation;

    // Orbit (captured from vessel.orbit before destruction)
    public bool hasOrbit;
    public double inclination;
    public double eccentricity;
    public double semiMajorAxis;
    public double lan;
    public double argumentOfPeriapsis;
    public double meanAnomalyAtEpoch;
    public double epoch;
    public string bodyName;

    // Surface position
    public bool hasSurface;
    public SurfacePosition surfacePosition;
}
```

---

### 6. `dockingInProgress` Interaction

The `dockingInProgress` HashSet prevents dock-absorption from being misclassified as destruction. Here is the exact interaction:

1. `OnPartCouple` fires (Task 5): absorbed vessel's PID is added to `dockingInProgress`
2. `onVesselWillDestroy` fires for the absorbed vessel (same frame or next frame):
   - Existing check: `if (!dockingInProgress.Contains(v.persistentId))` -- skips `OnBackgroundVesselWillDestroy`
   - **New Task 6 check**: same guard skips the deferred destruction coroutine start
3. `Update()` processes the deferred dock merge (Task 5): removes PID from `dockingInProgress`

The deferred destruction coroutine must ALSO check `dockingInProgress` after the one-frame delay. This handles the race condition where:
- Frame N: vessel destruction event fires (vessel starts being absorbed but `dockingInProgress` has not been set yet -- this should not happen given Task 5's sequencing, but defense-in-depth is prudent)
- Frame N: `OnPartCouple` fires and adds PID to `dockingInProgress`
- Frame N+1: deferred check runs -- should check `dockingInProgress` again and abort if the PID is there

In practice, Task 5 always adds to `dockingInProgress` in `OnPartCouple` which fires BEFORE `onVesselWillDestroy`. But the double-check costs nothing and prevents any ordering ambiguity.

---

### 7. `OnVesselWillDestroy` Modification

The existing handler (line 827) will be modified to add the deferred destruction check for tree-mode background vessels:

```csharp
void OnVesselWillDestroy(Vessel v)
{
    // 1. Existing: delegate to active recorder
    recorder?.OnVesselWillDestroy(v);

    // 2. Existing: background recorder cleanup (orbit segments, tracking state)
    //    Already guarded by dockingInProgress check.
    if (!dockingInProgress.Contains(v.persistentId))
        backgroundRecorder?.OnBackgroundVesselWillDestroy(v);

    // 3. NEW: deferred destruction check for tree background vessels
    if (activeTree != null && !dockingInProgress.Contains(v.persistentId))
    {
        string recordingId;
        if (activeTree.BackgroundMap.TryGetValue(v.persistentId, out recordingId))
        {
            // Capture vessel state now (before destruction) and defer the check
            var pending = CaptureVesselStateForTerminal(v, recordingId);
            StartCoroutine(DeferredDestructionCheck(pending));
        }
    }

    // 4. Existing: continuation vessel tracking (legacy chain mode)
    // ... (unchanged)
}
```

Note that step 2 (BackgroundRecorder cleanup) runs BEFORE step 3 (deferred destruction start). This is intentional: `OnBackgroundVesselWillDestroy` closes open orbit segments and removes tracking state from BackgroundRecorder's internal dictionaries. This is safe regardless of whether the vessel is unloading or destroyed. If unloading, the tracking state will be re-initialized when the vessel reloads. If destroyed, the state is already cleaned up.

The `BackgroundMap` removal and `TerminalStateValue` assignment happen later in `DeferredDestructionCheck` (Phase 3), only after confirming real destruction.

---

### 8. BackgroundRecorder Cleanup Interaction

The BackgroundRecorder has two cleanup entry points:

1. **`OnBackgroundVesselWillDestroy(v)`**: Called synchronously from `OnVesselWillDestroy`. Closes orbit segments, removes from `onRailsStates`/`loadedStates`. Does NOT modify `BackgroundMap` or the recording's metadata. Called for BOTH unload and destruction. This is correct -- the BackgroundRecorder's internal tracking state is transient and will be rebuilt.

2. **`OnVesselRemovedFromBackground(pid)`**: Called when a vessel is intentionally removed from background tracking (promotion, merge). Same cleanup as above. Used by Task 5's merge handler and Task 2's promotion handler.

For Task 6:
- **Destruction**: `OnBackgroundVesselWillDestroy` is already called in the existing code path. No additional BackgroundRecorder call is needed. The `BackgroundMap.Remove` happens in the deferred check (Phase 3).
- **Recovery**: Since `onVesselRecoveryProcessing` fires independently (not through `onVesselWillDestroy`), we need to explicitly call `backgroundRecorder?.OnVesselRemovedFromBackground(pid)` to clean up tracking state.

---

### 9. Active Vessel Destruction

If the **active** vessel (the one being actively recorded) is destroyed:

- `FlightRecorder.OnVesselWillDestroy` (line 3710) already handles this: it sets `VesselDestroyedDuringRecording = true`, closes any open orbit segment, and refreshes the backup snapshot.
- This flag is later used by `VesselSpawner.SnapshotVessel` (which sets `VesselDestroyed = true` on the pending recording) and by the merge dialog.

**In tree mode**, active vessel destruction needs slightly different handling:
- The active recording should get `TerminalStateValue = TerminalState.Destroyed`
- The tree's `ActiveRecordingId` should be cleared (no more active recording)
- The active recorder should be stopped

However, active vessel destruction in tree mode is complex: after the active vessel is destroyed, KSP may switch to another vessel (which might be in the tree), or the player may be presented with a "revert" option. The existing code already handles active vessel destruction for the non-tree case through `VesselDestroyedDuringRecording`.

**For Task 6, active vessel destruction is OUT OF SCOPE.** Task 6 focuses on background vessels. Active vessel destruction in tree mode will be addressed as part of the tree commit logic (Task 7), which must handle the case where the tree has no active recording (all vessels are background or destroyed). The existing `VesselDestroyedDuringRecording` flag provides enough information for Task 7 to handle this correctly.

---

### 10. New State Fields

#### 10.1 No New Serialized Fields

All necessary fields already exist on `RecordingStore.Recording` (from Task 1):
- `TerminalStateValue` (nullable `TerminalState`)
- `TerminalOrbit*` fields (7 doubles + string)
- `TerminalPosition` (nullable `SurfacePosition`)

No new enum values, no serialization changes.

#### 10.2 New Runtime Fields (ParsekFlight.cs)

One new private struct for the deferred destruction check:

```csharp
private struct PendingDestruction
{
    public uint vesselPid;
    public string recordingId;
    public double capturedUT;
    public Vessel.Situations situation;
    public bool hasOrbit;
    public double inclination, eccentricity, semiMajorAxis, lan;
    public double argumentOfPeriapsis, meanAnomalyAtEpoch, epoch;
    public string bodyName;
    public bool hasSurface;
    public SurfacePosition surfacePosition;
}
```

This struct is allocated on the stack when `OnVesselWillDestroy` fires and passed to the coroutine. It is consumed by the coroutine and discarded.

No new instance fields on `ParsekFlight` -- the pending destruction data lives entirely in the coroutine's local state (as a parameter to the `IEnumerator` method).

---

### 11. Unit Tests

The following can be tested without Unity:

#### 11.1 `CaptureVesselStateForTerminal` Helper (Pure Method)

This is not easily unit-testable because it takes a `Vessel` object. However, we can test the data assignment from the captured struct to the recording:

#### 11.2 `ApplyTerminalDestruction` (Internal Static Pure Method)

Extract a pure static method that takes the pending destruction data and applies it to a recording:

```csharp
internal static void ApplyTerminalDestruction(
    PendingDestruction pending,
    RecordingStore.Recording rec)
{
    rec.TerminalStateValue = TerminalState.Destroyed;
    rec.ExplicitEndUT = pending.capturedUT;
    ApplyTerminalData(pending, rec);
}
```

And a shared helper for both destruction and recovery:

```csharp
internal static void ApplyTerminalData(
    PendingDestruction data,
    RecordingStore.Recording rec)
{
    if (data.hasOrbit)
    {
        rec.TerminalOrbitInclination = data.inclination;
        rec.TerminalOrbitEccentricity = data.eccentricity;
        // ... etc.
    }
    if (data.hasSurface)
    {
        rec.TerminalPosition = data.surfacePosition;
    }
}
```

**Test cases:**
1. `ApplyTerminalDestruction` sets `TerminalStateValue = Destroyed`, `ExplicitEndUT` to captured UT
2. `ApplyTerminalDestruction` with orbital data populates all `TerminalOrbit*` fields
3. `ApplyTerminalDestruction` with surface data populates `TerminalPosition`
4. `ApplyTerminalDestruction` with neither orbital nor surface data leaves both null/default (edge case: vessel with no orbit and not landed, e.g., launchpad)
5. `ApplyTerminalRecovery` (same pattern) sets `TerminalStateValue = Recovered`
6. Verify `RebuildBackgroundMap` excludes recordings with `TerminalStateValue != null` (already tested in Task 1 tests, but worth a regression test)

#### 11.3 `ShouldDeferDestructionCheck` Decision Logic

Extract a pure static method:

```csharp
internal static bool ShouldDeferDestructionCheck(
    uint vesselPid,
    bool hasTree,
    HashSet<uint> dockingInProgress,
    Dictionary<uint, string> backgroundMap)
{
    if (!hasTree) return false;
    if (dockingInProgress.Contains(vesselPid)) return false;
    return backgroundMap.ContainsKey(vesselPid);
}
```

**Test cases:**
1. No tree -> false
2. PID in `dockingInProgress` -> false
3. PID not in `backgroundMap` -> false
4. PID in `backgroundMap`, not in `dockingInProgress`, has tree -> true

#### 11.4 `IsTrulyDestroyed` Decision Logic

The post-deferral check can be tested as a pure function:

```csharp
internal static bool IsTrulyDestroyed(
    uint vesselPid,
    HashSet<uint> dockingInProgress,
    bool vesselStillExists)
{
    if (dockingInProgress.Contains(vesselPid)) return false;
    return !vesselStillExists;
}
```

**Test cases:**
1. Vessel still in `FlightGlobals.Vessels` -> false (unload, not destruction)
2. Vessel gone from `FlightGlobals.Vessels` -> true (real destruction)
3. Vessel gone but PID in `dockingInProgress` -> false (dock absorption)

---

### 12. In-Game Test Scenarios

#### 12.1 On-Rails Atmospheric Destruction

1. Launch a probe to suborbital trajectory
2. Start tree recording
3. Decouple probe (creates a background branch)
4. Switch to the main vessel
5. Time warp until the probe's orbit decays into Kerbin's atmosphere
6. **Expected**: probe recording gets `TerminalState.Destroyed`, `ExplicitEndUT` set to destruction time, `TerminalOrbit*` fields populated with last known orbit

#### 12.2 Physics-Range Destruction (Crash)

1. Launch two controllable vessels near each other
2. Start tree recording
3. Decouple one and let it fall
4. Stay within physics range
5. **Expected**: when the vessel crashes, recording gets `TerminalState.Destroyed`

#### 12.3 Unload (Not Destruction)

1. Launch a probe to orbit
2. Start tree recording
3. Decouple probe (creates background branch)
4. Switch to main vessel
5. Wait for probe to go out of physics range (unload)
6. **Expected**: `onVesselWillDestroy` fires but deferred check finds vessel still in `FlightGlobals.Vessels` -> no terminal state set, recording continues

#### 12.4 Dock Absorption (Not Destruction)

1. Launch two dockable vessels
2. Start tree recording
3. Undock, then re-dock
4. **Expected**: `onVesselWillDestroy` fires for absorbed vessel but `dockingInProgress` prevents it from being classified as destruction. Merge branch point created instead.

#### 12.5 In-Flight Recovery (StageRecovery or Manual)

1. Launch with StageRecovery mod installed (or trigger manual recovery via Tracking Station while in Flight)
2. Start tree recording
3. Decouple a landed stage
4. If StageRecovery is installed: the stage is recovered automatically
5. **Expected**: recording gets `TerminalState.Recovered`

Note: This scenario may be hard to test without StageRecovery mod. Manual recovery from Tracking Station changes the scene, so it will not be intercepted by ParsekFlight. This is the expected behavior -- see section 4.5.

#### 12.6 Multiple Background Vessels, One Destroyed

1. Launch, undock multiple vessels
2. One vessel crashes while others remain in orbit
3. **Expected**: only the crashed vessel's recording gets `Destroyed`. Others continue normally.

---

### 13. Files Modified/Created

#### Modified:

1. **`ParsekFlight.cs`**:
   - Add `PendingDestruction` struct definition
   - Add `CaptureVesselStateForTerminal` helper method
   - Modify `OnVesselWillDestroy` to start deferred destruction check for tree background vessels
   - Add `DeferredDestructionCheck` coroutine
   - Add `OnVesselRecoveryProcessing` handler
   - Add/remove event subscription in `Start()`/`OnDestroy()`
   - Add `TerminateBackgroundRecording` shared method (used by both destruction and recovery)
   - Add `ApplyTerminalDestruction`/`ApplyTerminalRecovery`/`ApplyTerminalData` static methods
   - Add `ShouldDeferDestructionCheck`/`IsTrulyDestroyed` static methods
   - Reset any deferred destruction coroutines in `CleanupTreeState`/`OnDestroy`

2. **`BackgroundRecorder.cs`**: No changes expected. Existing `OnBackgroundVesselWillDestroy` and `OnVesselRemovedFromBackground` are sufficient.

3. **`RecordingTree.cs`**: No changes expected. Existing serialization and `RebuildBackgroundMap` already handle all terminal state fields.

4. **`RecordingStore.cs`**: No changes expected. All terminal state fields already defined.

#### Created:

5. **`Source/Parsek.Tests/TerminalEventTests.cs`**: Unit tests for the pure static decision/application methods.

---

### 14. Implementation Order

1. **Define `PendingDestruction` struct** in `ParsekFlight.cs` (near the other tree-mode fields, around line 138)

2. **Add `CaptureVesselStateForTerminal` helper** that takes a `Vessel` and `recordingId`, returns a `PendingDestruction` struct with all captured data

3. **Add pure static methods** (`ShouldDeferDestructionCheck`, `IsTrulyDestroyed`, `ApplyTerminalDestruction`, `ApplyTerminalData`) -- these can be immediately unit-tested

4. **Add `DeferredDestructionCheck` coroutine** following the pattern of `DeferredUndockBranch` -- `yield return null`, then check `FindVesselByPid`

5. **Modify `OnVesselWillDestroy`** to call `CaptureVesselStateForTerminal` and `StartCoroutine(DeferredDestructionCheck)` for tree background vessels

6. **Add `TerminateBackgroundRecording` helper** that handles the shared cleanup logic (set terminal state, capture data, remove from BackgroundMap, notify BackgroundRecorder)

7. **Add `OnVesselRecoveryProcessing` handler** and subscribe/unsubscribe in `Start()`/`OnDestroy()`

8. **Write unit tests** for pure static methods

9. **In-game testing** of all scenarios from section 12

---

### 15. Risk Assessment

#### Low Risk

- **Data model**: All fields already exist (Task 1). No serialization changes. No new enum values. Risk of data corruption is minimal.
- **BackgroundRecorder interaction**: Existing cleanup methods handle all cases. No modifications to BackgroundRecorder needed.
- **Coroutine pattern**: Well-established in the codebase (three existing examples). Low risk of novel bugs.

#### Medium Risk

- **`onVesselRecoveryProcessing` handler**: Less tested code path. Recovery during Flight for background vessels is rare. The `ProtoVessel` -> `persistentId` mapping might have edge cases (null `vesselRef`, missing protoPartSnapshots). Mitigated by null checks and fallback lookup.
- **Timing of `OnBackgroundVesselWillDestroy` vs. deferred check**: The BackgroundRecorder's orbit segment closure happens in Phase 1 (synchronous), before we know if it is destruction or unload. For unload, this causes a small gap in orbit segment coverage until the vessel reloads and BackgroundRecorder re-initializes. This gap is already tolerated by the existing code (Tasks 3-5 all call `OnBackgroundVesselWillDestroy` or `OnVesselRemovedFromBackground` which close segments).

#### Low-to-Medium Risk

- **Active vessel destruction in tree mode**: Deliberately out of scope for Task 6. The existing `VesselDestroyedDuringRecording` flag handles the immediate case. Task 7 will address the tree-level implications (clearing `ActiveRecordingId`, handling trees with no active recording). No immediate risk, but this is deferred complexity.

#### Negligible Risk

- **Multiple simultaneous destructions**: Each coroutine is independent, captures its own data, and runs in a separate iteration of the coroutine scheduler. No shared mutable state between coroutines.
- **dockingInProgress interaction**: Double-checked (Phase 1 and Phase 2). Worst case: a destruction is incorrectly suppressed (vessel appears to survive when it should not). This would cause the recording to remain "active" in BackgroundMap but the vessel is gone -- detectable via logging and correctable in Task 7 commit logic.

---

## Orchestrator Review Fixes

The following fixes MUST be applied during implementation. They address issues found by the plan review and verified by the orchestrator against the actual codebase.

### Fix 1 (CRITICAL): Remove `onVesselRecoveryProcessing` Handler Entirely

**Problem**: `GameEvents.onVesselRecoveryProcessing` fires in the **Space Center / Tracking Station**, NOT in the Flight scene. Since `ParsekFlight` is a `KSPAddon(KSPAddon.Startup.Flight, false)`, subscribing to this event will never trigger a callback. The handler is dead code. Additionally, the plan uses the wrong signature: it should be `EventData<ProtoVessel, MissionRecoveryDialog, float>`, not `EventData<ProtoVessel, MissionRecovery, bool>`.

**Fix**:
- Remove Section 4 (Recovery Detection Flow) entirely. Do NOT subscribe to `onVesselRecoveryProcessing`.
- For in-flight recovery (e.g., StageRecovery mod), the deferred destruction check handles it correctly: the vessel disappears from `FlightGlobals.Vessels`, so the check marks it as `Destroyed`. Marking a StageRecovery-recovered vessel as `Destroyed` instead of `Recovered` is an acceptable approximation - the recording is correctly terminated either way.
- For stock recovery (Tracking Station), the Flight scene is not active, so ParsekFlight doesn't exist. The tree is already committed by that point.
- Remove the `TerminateBackgroundRecording` shared helper - without the recovery handler, it's not needed. Inline the terminal state assignment into `DeferredDestructionCheck`.
- Remove `OnVesselRecoveryProcessing` from the files modified list and implementation order.
- Remove `ApplyTerminalRecovery` static method - only `ApplyTerminalDestruction` is needed.

### Fix 2 (IMPORTANT): Null `activeTree` Guard After Yield in `DeferredDestructionCheck`

**Problem**: Between Phase 1 (synchronous capture) and Phase 3 (after `yield return null`), `activeTree` could become null due to scene change (`OnSceneChangeRequested` sets `activeTree = null`). The deferred check would then crash on `activeTree.Recordings` and `activeTree.BackgroundMap` null references.

**Fix**: Add at the start of Phase 2 (after `yield return null`):
```csharp
if (activeTree == null) yield break;
```

### Fix 3 (IMPORTANT): Re-check `BackgroundMap` After Yield to Detect Stale Recording ID

**Problem**: Between Phase 1 and Phase 3, a merge (Task 5) or promotion (Task 2) could remove the vessel from `BackgroundMap` and set `TerminalStateValue = Docked` on the recording. If the deferred check then overwrites `TerminalStateValue` from `Docked` to `Destroyed`, the tree structure is corrupted.

**Fix**: After yield, before applying terminal state, re-check:
```csharp
if (!activeTree.BackgroundMap.ContainsKey(pending.vesselPid)) yield break;
```
This ensures another handler hasn't already processed this vessel.

### Fix 4 (MINOR): Fix `CleanupTreeState` References

**Problem**: The plan references `CleanupTreeState` method which does not exist. The actual cleanup sites are `OnSceneChangeRequested` (line ~703) and `OnFlightReady` (line ~2587).

**Fix**: Replace all references to `CleanupTreeState` with the actual method names.

### Fix 5 (MINOR): Stack Allocation Claim for Struct in Coroutine

**Problem**: Section 10.2 claims the `PendingDestruction` struct is "allocated on the stack." When passed to a coroutine, the C# compiler captures it in a heap-allocated state machine class. Functionally correct but misleading.

**Fix**: Change claim to "captured by the coroutine state machine (heap-allocated, one per deferred check)."

### Fix 6 (MINOR): Handle `PRELAUNCH` and `FLYING` Situations in Terminal Capture

**Problem**: The capture helpers don't explicitly handle `PRELAUNCH` (treat as `LANDED`) or `FLYING` (atmospheric flight where orbit data is unreliable).

**Fix**:
- `PRELAUNCH`: Capture surface position (same as `LANDED`).
- `FLYING`: Capture orbit data as best-effort approximation. The orbit is unreliable for atmospheric flight but is the best available data.

### Summary of Required Code Changes

1. **Remove** `onVesselRecoveryProcessing` subscription and handler entirely (Fix 1)
2. **Add** `if (activeTree == null) yield break;` after yield in `DeferredDestructionCheck` (Fix 2)
3. **Add** `if (!activeTree.BackgroundMap.ContainsKey(pending.vesselPid)) yield break;` after yield (Fix 3)
4. **Remove** `TerminateBackgroundRecording` shared helper, `ApplyTerminalRecovery`, `OnVesselRecoveryProcessing` (Fix 1)
5. **Handle** `PRELAUNCH` as `LANDED` in capture logic (Fix 6)
6. **Fix** all `CleanupTreeState` references to actual method names (Fix 4)
