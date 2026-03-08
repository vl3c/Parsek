# Task 4: Split Event Detection (Undock, EVA, Joint Break)

## Workflow

This task follows a multi-stage review pipeline using Opus 4.6 agents, orchestrated by the main session:

1. **Plan** — Opus 4.6 subagent explores the codebase and writes a detailed implementation plan
2. **Plan review** — Fresh Opus 4.6 subagent reviews the plan for correctness, completeness, and risk
3. **Orchestrator review** — Main session reviews the plan with full project context and fixes issues
4. **Implement** — Fresh Opus 4.6 subagent implements the plan
5. **Implementation review** — Fresh Opus 4.6 subagent reviews the implementation and fixes issues
6. **Final review** — Main session reviews the implementation considering the larger architectural context
7. **Commit** — Main session commits the implementation
8. **Next task briefing** — Main session presents the next task, explains its role and how it fits into the overall plan

---

## Plan

### 1. Overview

Task 4 is where the recording tree actually branches at runtime. When a vessel splits — via undocking, EVA, or structural failure — the code must:

1. Detect the split event
2. Filter out debris (spent boosters, fairings) from trackable vessels
3. End the parent recording at the branch UT
4. Create a `BranchPoint` linking parent to two children
5. Create two child `Recording` objects in the tree
6. Make one child the active recording (player's vessel), the other a background recording
7. Instantiate `BackgroundRecorder` (first time a tree becomes active)
8. Wire up the BackgroundRecorder to the `PhysicsFramePatch.BackgroundRecorderInstance`

Tasks 1-3 built the foundation:
- **Task 1** created `RecordingTree`, `BranchPoint`, `SurfacePosition`, `TerminalState`, and new fields on `Recording` (treeId, parentBranchPointId, childBranchPointId, terminalState, etc.)
- **Task 2** added `TransitionToBackground` / `PromoteFromBackground` to `DecideOnVesselSwitch`, `FlushRecorderToTreeRecording`, `PromoteRecordingFromBackground`, and `OnVesselSwitchComplete` handler — all currently dormant because no code creates a `RecordingTree` at runtime
- **Task 3** added `BackgroundRecorder` with dual-mode recording (on-rails: OrbitSegment/SurfacePosition; loaded/physics: full trajectory + part events) — also dormant because no code creates a `BackgroundRecorder` at runtime

Task 4 activates all of this dormant infrastructure by creating `RecordingTree` and `BackgroundRecorder` instances when the first split event occurs.

---

### 2. Existing Infrastructure Audit

#### 2.1 Undock Detection — `OnPartUndock` (ParsekFlight.cs line 1223)

The existing handler:
```csharp
void OnPartUndock(Part undockedPart)
```
- Subscribed at `Start()` (line 183), unsubscribed at `OnDestroy()` (line 523)
- Checks `recorder != null && recorder.IsRecording`
- Gets `newPid = undockedPart.vessel.persistentId`
- If `newPid != recorder.RecordingVesselId` (something undocked FROM us):
  - Sets `pendingUndockOtherPid = newPid` and `undockConfirmFrames = 0`
  - Calls `recorder.StopRecordingForChainBoundary()` (line 1240)
  - The `Update()` loop (line 276-293) then picks this up, calls `CommitDockUndockSegment`, starts a new recording, and starts an `undockContinuation` for the other vessel
- If the player follows the undocked vessel, `OnPhysicsFrame` detects the PID mismatch and `UndockSiblingPid` routes it to `UndockSwitch` (line 1243-1245)

**What this currently does:** Creates a chain segment boundary (commits current recording, starts new one) and a ghost-only "undock continuation" recording for the sibling vessel. No tree branching.

**What Task 4 changes:** When a tree is active (or when this is the first split that creates a tree), intercept this event BEFORE the chain logic runs, and create a tree branch instead.

#### 2.2 Undock Switch — `UndockSwitchPending` (ParsekFlight.cs lines 296-318)

When the player switches to the undocked vessel (PID changes), `OnPhysicsFrame` detects the mismatch, `DecideOnVesselSwitch` returns `UndockSwitch` (because `UndockSiblingPid` matches), and sets `UndockSwitchPending = true`.

The `Update()` handler (lines 296-318):
1. Calls `CommitDockUndockSegment` with the old recorder
2. Stops any existing undock continuation
3. Starts a new recording on the new vessel
4. Starts an undock continuation for the OLD vessel (role swap)
5. Sets `recorder.UndockSiblingPid = undockContinuationPid`

**What Task 4 changes:** In tree mode, `UndockSwitch` is no longer needed for undocks because the tree branch handles both vessels. The `DecideOnVesselSwitch` priority chain (line 3838-3839) checks `UndockSiblingPid` before tree decisions, but in tree mode we should NOT set `UndockSiblingPid` at all — the tree's BackgroundMap handles vessel tracking.

#### 2.3 EVA Detection — `OnCrewOnEva` (ParsekFlight.cs line 1109)

The existing handler:
```csharp
void OnCrewOnEva(GameEvents.FromToAction<Part, Part> data)
```
Two paths:

**Mid-recording EVA** (line 1112-1134): When recording and EVA is from the recorded vessel:
- Sets `pendingChainContinuation = true`, `pendingChainIsBoarding = false`
- Sets `pendingChainEvaName = kerbalName`, `activeChainCrewName = kerbalName`
- Sets `pendingAutoRecord = true`
- The chain flow then: stops parent recording via `StopRecordingForChainBoundary()` (when the vessel switch fires), commits it via `CommitChainSegment` (Update line 322-328), starts EVA child recording via `pendingAutoRecord` (Update line 442-467)
- Parent recording gets `ParentRecordingId` linkage and `EvaCrewName` set

**EVA from pad** (line 1136-1156): When not recording and vessel is PRELAUNCH:
- Sets `pendingAutoRecord = true`
- No chain, just starts a fresh recording on the EVA kerbal

**What this currently does:** Creates chain segments: parent vessel recording is committed (ghost-only, no spawn), EVA kerbal gets a linked child recording with `ParentRecordingId` and `EvaCrewName`. On revert, the EVA child is auto-committed without merge dialog if its parent exists (ParsekFlight.cs line 1644-1661).

**What Task 4 changes:** In tree mode, EVA creates a tree branch instead of a chain segment. Both the vessel and the EVA kerbal get their own recording nodes in the tree. The EVA kerbal recording tracks via BackgroundRecorder when not active.

#### 2.4 Joint Break — `OnPartJointBreak` (FlightRecorder.cs line 203)

The existing handler:
```csharp
private void OnPartJointBreak(PartJoint joint, float breakForce)
```
- Subscribed/unsubscribed by `SubscribePartEvents()`/`UnsubscribePartEvents()` in FlightRecorder (lines 149-165)
- Currently: records a `Decoupled` part event for the child part (lines 214-219)
- Does NOT check if the break creates a new vessel — it only records the part event

**What Task 4 changes:** After recording the part event, defer one frame, check if a new controllable vessel was created, and if so, create a tree branch. This is the same deferred-snapshot pattern used for undock.

#### 2.5 Undock Continuation System (ParsekFlight.cs lines 1432-1553)

`StartUndockContinuation(uint otherPid)` (line 1432):
- Creates a committed recording with `ChainBranch = 1` (ghost-only, never spawns)
- Seeds it with a single trajectory point from the other vessel
- `UpdateUndockContinuationSampling()` (line 1491) samples the vessel each frame with adaptive thresholds
- `RefreshUndockContinuationSnapshot()` (line 1558) updates the ghost snapshot before stopping

**Relationship to tree mode:** The undock continuation system is the pre-tree way of tracking the "other" vessel after undock. In tree mode, the background recording subsystem replaces this entirely. When tree mode is active, undock continuation should NOT be started — the new background recording handles it.

#### 2.6 EVA Child Recording Linkage (RecordingStore.cs lines 70-71)

`ParentRecordingId` and `EvaCrewName` fields on `Recording`:
- Set during `CommitChainSegment` (ParsekFlight.cs line 954-955)
- Used by `OnFlightReady` to auto-commit EVA child recordings without showing merge dialog (line 1644-1661)
- Used by `VesselSpawner.RemoveSpecificCrewFromSnapshot` to strip EVA'd crew from parent vessel at spawn time

**Relationship to tree mode:** In tree mode, these fields are still useful for identifying EVA recordings and removing crew from vessel snapshots. However, the auto-commit logic in `OnFlightReady` (lines 1644-1661) should be skipped when the recording is part of a tree — the tree merge dialog handles everything.

#### 2.7 Tree Fields on Recording (RecordingStore.cs lines 84-110)

Already implemented by Task 1:
- `TreeId` (line 85) — links recording to its tree
- `VesselPersistentId` (line 86) — stable vessel identity
- `TerminalStateValue` (line 89) — null = still recording, set on terminal event
- Terminal orbit fields (lines 91-100) — for orbital terminal state
- `TerminalPosition` (line 103) — for landed/splashed terminal state
- `SurfacePos` (line 106) — background surface position
- `ParentBranchPointId` (line 109) — null for root
- `ChildBranchPointId` (line 110) — null for leaves
- `ExplicitStartUT` / `ExplicitEndUT` (lines 116-117) — for recordings with no trajectory points

#### 2.8 Tree Vessel Switch Infrastructure (Task 2, dormant)

In `ParsekFlight.cs`:
- `activeTree` field (line 120) — currently always null
- `backgroundRecorder` field (line 123) — currently always null
- `OnVesselSwitchComplete` (line 719) — checks `activeTree == null` and returns immediately
- `FlushRecorderToTreeRecording` (line 784) — flushes recorder data to tree recording
- `PromoteRecordingFromBackground` (line 832) — creates fresh FlightRecorder for promoted vessel
- `Update()` TransitionToBackgroundPending handler (lines 414-433) — flushes backgrounded recorder

In `FlightRecorder.cs`:
- `ActiveTree` property (line 92) — currently always null
- `TransitionToBackgroundPending` (line 93) — tree transition flag
- `IsBackgrounded` (line 99) — derived from IsRecording + Harmony patch state
- `TransitionToBackground()` (line 3717) — captures orbit segment, disconnects Harmony
- `DecideOnVesselSwitch` (line 3830) — tree-aware decision (lines 3851-3855)

#### 2.9 BackgroundRecorder (Task 3, dormant)

In `BackgroundRecorder.cs`:
- Constructor takes `RecordingTree` (line 37)
- `OnVesselBackgrounded(uint vesselPid)` (line 229) — initializes tracking state
- `OnVesselRemovedFromBackground(uint vesselPid)` (line 282) — cleans up tracking state
- `UpdateOnRails(double ut)` (called from ParsekFlight.Update line 438) — updates on-rails background recordings
- `OnBackgroundPhysicsFrame(Vessel bgVessel)` — called from PhysicsFramePatch (line 67) for loaded background vessels
- `Shutdown()` (line 479) — cleans up all background states

In `PhysicsFramePatch.cs`:
- `BackgroundRecorderInstance` field (line 27) — currently always null
- Postfix checks `BackgroundRecorderInstance != null` (line 62) and dispatches non-active vessels to it

---

### 3. Tree Lifecycle — When Is the Tree Created?

#### 3.1 Design Decision: Create on First Split, Not at Launch

**Option A (create at launch):** Every recording is a tree (even simple ones with no splits). This adds complexity to merge dialog, playback, and serialization for the common case.

**Option B (create on first split):** Standalone recordings work exactly as before. The tree is only created when the first split event occurs. This preserves backward compatibility and simplicity for the common case.

**Decision: Option B.** The tree is created on the first split event. This means:
- A standalone recording (no undock/EVA) stays as a standalone recording — no tree overhead
- When the first split occurs, the existing standalone recording is "wrapped" into a tree as the root node
- All subsequent splits add branches to this tree

#### 3.2 Tree Creation Flow (First Split)

When `OnPartUndock`, `OnCrewOnEva`, or `OnPartJointBreak` fires during active recording and `activeTree == null`:

1. **Create the tree:**
   ```
   activeTree = new RecordingTree {
       Id = Guid.NewGuid().ToString("N"),
       TreeName = recorder.CaptureAtStop?.VesselName ?? FlightGlobals.ActiveVessel?.vesselName ?? "Unknown",
       RootRecordingId = <parentRecordingId>,
       ActiveRecordingId = null  // will be set to child recording id
   }
   ```

2. **Create root recording from current recorder data:**
   - Flush current recorder's accumulated data (points, orbit segments, part events) into a new `Recording` object
   - Set `Recording.TreeId = tree.Id`
   - Set `Recording.VesselPersistentId = recorder.RecordingVesselId`
   - Set `Recording.VesselName` from the recorder/vessel
   - Capture `GhostVisualSnapshot` from `recorder.InitialGhostVisualSnapshot`
   - Set `ExplicitStartUT` from first trajectory point's UT (or current UT if no points)
   - Add to `tree.Recordings`

3. **Create the branch point and child recordings** (detailed in sections 4/5/6)

4. **Create BackgroundRecorder:**
   ```
   backgroundRecorder = new BackgroundRecorder(activeTree);
   Patches.PhysicsFramePatch.BackgroundRecorderInstance = backgroundRecorder;
   ```

5. **Set recorder.ActiveTree:**
   ```
   recorder.ActiveTree = activeTree;  // for subsequent vessel switches
   ```

6. **Stop chain infrastructure:**
   - Stop any existing undock continuation (`StopUndockContinuation`)
   - Stop any existing vessel continuation (`StopContinuation`)
   - Do NOT start new continuations — the tree handles them

#### 3.3 Wrapping the Existing Recording into the Root Node

The current recorder has accumulated data (trajectory points, orbit segments, part events). This data needs to be moved into the tree's root recording. The process:

1. Call `recorder.FinalizeOpenOrbitSegment()` to close any in-progress orbit segment
2. Create a new `Recording` object (the root node)
3. Copy `recorder.Recording` (points), `recorder.OrbitSegments`, `recorder.PartEvents` into it
4. Copy `recorder.InitialGhostVisualSnapshot` into `Recording.GhostVisualSnapshot`
5. Take a vessel snapshot at branch time (the root's VesselSnapshot captures the pre-split vessel state)
6. Set `Recording.ChildBranchPointId` to the new branch point's id
7. Set `ExplicitEndUT` to the branch UT (current UT)
8. Clear the recorder's accumulated data (it will start fresh for the child recording)

This is very similar to `FlushRecorderToTreeRecording` but with the additional step of creating the root recording from scratch rather than appending to an existing one.

---

### 4. Undock Branching

#### 4.1 Detection Point

`OnPartUndock` in `ParsekFlight.cs` (line 1223). The existing handler already detects when a part undocks and creates a new vessel. The key change: **when tree mode is active OR when this is the first split that creates a tree**, intercept before the chain logic.

#### 4.2 Modified OnPartUndock Flow

```
void OnPartUndock(Part undockedPart)
{
    if (recorder == null || !recorder.IsRecording) return;
    if (undockedPart?.vessel == null) return;

    uint newPid = undockedPart.vessel.persistentId;
    if (newPid == recorder.RecordingVesselId) return;  // transient state, ignore

    // TREE MODE: create branch instead of chain segment
    // This fires both when activeTree != null (existing tree)
    // and when activeTree == null (first split creates the tree)
    StartCoroutine(DeferredUndockBranch(newPid, undockedPart.vessel));
}
```

**Critical change:** The existing `StopRecordingForChainBoundary()` and `pendingUndockOtherPid` logic is bypassed in tree mode. Instead, we defer by one frame (coroutine) and then create the branch.

#### 4.3 Deferred Undock Branch Coroutine

```
IEnumerator DeferredUndockBranch(uint newVesselPid, Vessel undockedVessel)
{
    yield return null;  // defer one frame for KSP to finalize the split

    // 1. Deduplication: skip if branch already created at this UT
    double branchUT = Planetarium.GetUniversalTime();
    if (lastBranchUT >= 0 && Math.Abs(branchUT - lastBranchUT) < 0.01)
    {
        // Already branched in this frame/physics tick
        return;
    }

    // 2. Find the new vessel (may have changed PID after split finalization)
    Vessel newVessel = FlightRecorder.FindVesselByPid(newVesselPid);
    if (newVessel == null) return;  // vessel already gone (debris destroyed instantly)

    // 3. Debris filter
    if (!IsTrackableVessel(newVessel))
    {
        // Not trackable — record as part event only (existing behavior)
        // Part event already recorded by OnPartJointBreak/OnPartDie
        return;
    }

    // 4. Create the branch
    CreateSplitBranch(BranchPointType.Undock, newVessel, branchUT);
}
```

#### 4.4 Deferred Snapshot Rationale

Per the design doc (line 178): "onPartUndock can fire before KSP finalizes the vessel split. The new vessel's part list, mass, and orbital parameters may not be correct in the same frame. Defer the snapshot by one frame."

The `yield return null` coroutine defers exactly one Unity frame. After this frame, both child vessels have correct part lists and orbital elements.

---

### 5. EVA Branching

#### 5.1 Detection Point

`OnCrewOnEva` in `ParsekFlight.cs` (line 1109). The mid-recording path (line 1112-1134) currently sets up chain continuation. In tree mode, it should create a tree branch instead.

#### 5.2 Modified OnCrewOnEva Flow

```
void OnCrewOnEva(GameEvents.FromToAction<Part, Part> data)
{
    // Mid-recording EVA: branch in tree mode, chain in legacy mode
    if (IsRecording)
    {
        if (data.from?.vessel == null ||
            data.from.vessel.persistentId != recorder.RecordingVesselId)
            return;

        string kerbalName = ExtractEvaKerbalName(data);
        if (string.IsNullOrEmpty(kerbalName)) return;

        // TREE MODE: create branch
        // Note: EVA is immediate (no deferred snapshot needed — both vessels exist now)
        // However, the EVA kerbal vessel may not be the active vessel yet.
        // Defer by one frame to let the vessel switch complete.
        StartCoroutine(DeferredEvaBranch(kerbalName, data));
        return;
    }

    // EVA from pad (not recording) — unchanged
    // ...existing code...
}
```

#### 5.3 EVA vs. Undock Differences

- **EVA kerbal PID:** The EVA kerbal gets a new vessel with a new persistentId. The `data.to` part contains the kerbal's part, and `data.to.vessel` is the EVA vessel. After one frame, this vessel is stable.
- **Who is active:** KSP switches the active vessel to the EVA kerbal. So after EVA, the kerbal is the active recording and the vessel becomes background.
- **Ghost visual snapshot:** The vessel's snapshot should be taken AFTER the crew member is removed (it's the post-EVA vessel state). The kerbal's snapshot is a simple EVA kerbal.
- **EvaCrewName:** Set on the kerbal's child recording for crew tracking during spawn. This preserves the existing `EvaCrewName` field usage.

#### 5.4 Interaction with Existing EVA Infrastructure

**ParentRecordingId / EvaCrewName:** These fields remain on Recording and are still set on the EVA child recording within the tree. They enable:
- `VesselSpawner.RemoveSpecificCrewFromSnapshot` to strip EVA'd crew from the vessel's spawn snapshot
- Identification of which recording is an EVA kerbal vs. a vessel

**pendingAutoRecord:** In legacy mode, this flag triggers auto-recording for the EVA kerbal after vessel switch completes. In tree mode, we don't use this flag — the tree branch creates the child recording directly.

**Chain metadata:** In tree mode, EVA does NOT create chain segments. `activeChainId`, `pendingChainContinuation`, `pendingChainEvaName` are NOT set. The tree branch replaces all of this.

---

### 6. Joint Break Branching

#### 6.1 Detection Point

`OnPartJointBreak` in `FlightRecorder.cs` (line 203). Currently records a `Decoupled` part event. Task 4 adds a deferred vessel-creation check.

#### 6.2 Approach

The joint break handler itself stays in FlightRecorder (it records the part event). A NEW callback is added: after recording the part event, signal to ParsekFlight that a potential vessel split occurred. ParsekFlight then runs the deferred check.

**Option A:** Add a public flag `JointBreakVesselSplitPending` on FlightRecorder, checked in ParsekFlight.Update().

**Option B:** FlightRecorder raises an event/callback that ParsekFlight subscribes to.

**Option C:** Move the joint break subscription to ParsekFlight entirely.

**Decision: Option A** — simplest, mirrors the existing `DockMergePending`/`UndockSwitchPending` pattern. FlightRecorder sets a flag and list of candidate PIDs; ParsekFlight checks in Update() with a one-frame delay.

#### 6.3 Modified OnPartJointBreak

In FlightRecorder.cs:
```csharp
private void OnPartJointBreak(PartJoint joint, float breakForce)
{
    if (!IsRecording) return;
    if (joint?.Child?.vessel == null) return;
    if (joint.Child.vessel.persistentId != RecordingVesselId) return;

    // Record part event (existing behavior)
    PartEvents.Add(new PartEvent { ... });

    // NEW: Signal potential vessel split for deferred check
    // Record the child part's vessel PID — after one frame, this may become
    // a new vessel if the break split the vessel in two.
    pendingJointBreakPartPids.Add(joint.Child.persistentId);
}
```

In ParsekFlight.Update(), add a deferred check:
```
if (recorder != null && recorder.HasPendingJointBreakChecks)
{
    StartCoroutine(DeferredJointBreakCheck(recorder.ConsumePendingJointBreakPids()));
}
```

#### 6.4 Deferred Joint Break Check

After one frame:
1. Scan `FlightGlobals.Vessels` for any new vessels that weren't tracked before
2. For each new vessel: apply debris filter
3. If any new vessel is trackable: create a tree branch

**Practical complexity:** Joint breaks can fire multiple times in one frame for a multi-part breakup. The deferred check runs once after one frame and collects ALL new vessels from that breakup, creating one branch per trackable new vessel. The deduplication guard (last branch UT) prevents multiple branches at the same UT.

**Simplification for v1:** Joint break vessel splits that produce trackable vessels are rare (usually structural failures on crewed vessels). The common case (staging) produces debris that fails the debris filter. This path can be conservative — if in doubt, don't branch.

---

### 7. Debris Filter

#### 7.1 Criteria

Per the design doc (lines 160-168):

```csharp
internal static bool IsTrackableVessel(Vessel v)
{
    if (v == null) return false;

    // Space objects (asteroids, comets) are always trackable
    if (v.vesselType == VesselType.SpaceObject) return true;

    // Check for command capability
    for (int i = 0; i < v.parts.Count; i++)
    {
        Part p = v.parts[i];
        if (p.FindModuleImplementing<ModuleCommand>() != null) return true;
        // [ORCHESTRATOR FIX] ModuleProbeCore does not exist in KSP.
        // Probe cores use ModuleCommand with minimumCrew=0. ModuleCommand alone is sufficient.
    }
    return false;
}
```

#### 7.2 Where the Filter Runs

The filter runs in the deferred check (after one frame), when the new vessel's part list is finalized. It does NOT run in the event handler itself (where the vessel may not be fully split yet).

#### 7.3 What Happens to Debris

When the new vessel fails the debris filter:
- No branch point is created
- No child recording is created
- The existing part event (`Decoupled`/`Destroyed`) on the parent recording handles ghost visual hiding (existing behavior, unchanged)
- The debris vessel is left to KSP's normal handling (it may be destroyed, recovered, etc.)

#### 7.4 Edge Cases

- **Fairings:** `FairingJettisoned` part events already handle fairing ghost visuals. Fairings have no `ModuleCommand`, so they fail the filter. Correct.
- **Heat shields:** No command module, fail the filter. Correct.
- **Probe cores on upper stages:** Have `ModuleCommand` (with `minimumCrew=0`), pass the filter, get a branch. Correct. [ORCHESTRATOR FIX: `ModuleProbeCore` does not exist in KSP — probe cores use `ModuleCommand`.]
- **EVA kerbals:** Not checked by this filter (EVA uses a separate code path). EVA kerbals always get a branch.
- **Asteroids (claw release):** `VesselType.SpaceObject` passes the filter, gets a branch. Correct per design doc line 170.

#### 7.5 Testability

`IsTrackableVessel` is a `static` method that takes a `Vessel` parameter. It cannot be unit-tested without Unity (requires `Vessel.parts` and module queries). It will be tested in-game. However, the logic is simple enough that the risk is low.

For the debris filter logic itself, we can extract a pure testable version:
```csharp
internal static bool IsTrackableVesselType(VesselType vesselType)
```
This handles the `SpaceObject` check. The module check requires live Part objects and cannot be unit-tested.

---

### 8. Deduplication — "Last Branch UT" Guard

#### 8.1 Problem

`onPartUndock` fires once per separated part, not once per event. A multi-port undock or structural break can fire multiple times in the same frame. Without deduplication, each firing would try to create a branch point.

#### 8.2 Solution

Add a `lastBranchUT` field to ParsekFlight:
```csharp
private double lastBranchUT = -1;
```

In the deferred branch creation:
```csharp
double branchUT = Planetarium.GetUniversalTime();
if (lastBranchUT >= 0 && Math.Abs(branchUT - lastBranchUT) < 0.01)
{
    Log("Skipping duplicate branch at same UT");
    return;
}
lastBranchUT = branchUT;
```

Reset `lastBranchUT = -1` in `OnFlightReady` and `OnSceneChangeRequested`.

#### 8.3 Multi-Part Breakup

A structural failure can split a vessel into 3+ pieces in one frame. After one-frame deferral:
- Multiple new vessels exist in `FlightGlobals.Vessels`
- Each trackable vessel gets its own branch point
- But they all fire at the same UT, so the deduplication guard must allow multiple branches at the same UT for DIFFERENT new vessels while blocking duplicate events for the SAME vessel

**Refinement:** Track `lastBranchVesselPids` (a set of PIDs already branched at this UT) instead of just `lastBranchUT`:

```csharp
private double lastBranchUT = -1;
private HashSet<uint> lastBranchVesselPids = new HashSet<uint>();
```

```csharp
double branchUT = Planetarium.GetUniversalTime();
if (Math.Abs(branchUT - lastBranchUT) > 0.01)
{
    lastBranchVesselPids.Clear();
    lastBranchUT = branchUT;
}
if (lastBranchVesselPids.Contains(newVesselPid))
{
    Log($"Skipping duplicate branch for pid={newVesselPid} at same UT");
    return;
}
lastBranchVesselPids.Add(newVesselPid);
```

---

### 9. Deferred Snapshot

#### 9.1 Why Deferred

Per the design doc: `onPartUndock` can fire before KSP finalizes the vessel split. The coroutine `yield return null` defers by one Unity frame, ensuring both child vessels have correct part lists and orbital elements.

#### 9.2 Snapshot Capture

After the one-frame deferral, capture snapshots for BOTH child vessels:

```csharp
// Active vessel (player's vessel after undock)
Vessel activeVessel = FlightGlobals.ActiveVessel;
ConfigNode activeSnapshot = VesselSpawner.TryBackupSnapshot(activeVessel);

// Background vessel (the one that split off)
Vessel bgVessel = FlightRecorder.FindVesselByPid(newVesselPid);
ConfigNode bgSnapshot = bgVessel != null ? VesselSpawner.TryBackupSnapshot(bgVessel) : null;
```

These snapshots become the `GhostVisualSnapshot` on each child recording (used for ghost mesh building during playback). They also serve as the initial `VesselSnapshot` (used for spawning at EndUT).

#### 9.3 Root Recording Snapshot

The root (parent) recording's `VesselSnapshot` should capture the pre-split vessel state. This is already available as `recorder.LastGoodVesselSnapshot` or `recorder.InitialGhostVisualSnapshot`.

The `GhostVisualSnapshot` for the root was already captured at recording start (`recorder.InitialGhostVisualSnapshot`). The `VesselSnapshot` for the root is the `LastGoodVesselSnapshot` at branch time.

---

### 10. Child Recording Creation — `CreateSplitBranch`

#### 10.1 Method Signature

```csharp
void CreateSplitBranch(BranchPointType branchType, Vessel newVessel, double branchUT,
    string evaCrewName = null)
```

#### 10.2 Flow

```
1. Stop the active recorder (capture remaining data)
   - recorder.FinalizeOpenOrbitSegment()
   - Sample final boundary point if possible

2. Create or get the tree (if activeTree == null, create it)
   a. Create RecordingTree
   b. Create root Recording from current recorder data
   c. Create BackgroundRecorder
   d. Wire PhysicsFramePatch.BackgroundRecorderInstance

3. Create the BranchPoint
   bp = new BranchPoint {
       id = Guid.NewGuid().ToString("N"),
       ut = branchUT,
       type = branchType,
       parentRecordingIds = { parentRecordingId },
       childRecordingIds = { activeChildId, backgroundChildId }
   }

4. Set parent recording's ChildBranchPointId = bp.id
   Set parent recording's ExplicitEndUT = branchUT

5. Create active child Recording (player's vessel)
   - RecordingId = new GUID
   - TreeId = tree.Id
   - VesselPersistentId = activeVessel.persistentId
   - VesselName = activeVessel.vesselName
   - ParentBranchPointId = bp.id
   - ExplicitStartUT = branchUT
   - GhostVisualSnapshot = snapshot of active vessel (post-split)
   - EvaCrewName = evaCrewName (for EVA branches; null for undock)
   - Add to tree.Recordings

6. Create background child Recording (other vessel)
   - RecordingId = new GUID
   - TreeId = tree.Id
   - VesselPersistentId = newVessel.persistentId
   - VesselName = newVessel.vesselName
   - ParentBranchPointId = bp.id
   - ExplicitStartUT = branchUT
   - GhostVisualSnapshot = snapshot of new vessel (post-split)
   - EvaCrewName = (for EVA: null on vessel child, evaCrewName on kerbal child)
   - Add to tree.Recordings

7. Add BranchPoint to tree.BranchPoints

8. Determine active vs. background:
   - For undock: player stays on the "remaining" vessel (activeVessel) = active child
   - For EVA: player switches to kerbal = active child is the kerbal, vessel is background
   - For joint break: player stays on activeVessel = active child

9. Set tree.ActiveRecordingId = active child's RecordingId

10. Add background child to tree.BackgroundMap:
    tree.BackgroundMap[newVessel.persistentId] = backgroundChildId

11. Notify BackgroundRecorder:
    backgroundRecorder.OnVesselBackgrounded(newVessel.persistentId)

12. Create new FlightRecorder for active child:
    recorder = new FlightRecorder();
    recorder.ActiveTree = activeTree;
    recorder.StartRecording(isPromotion: true);  // skip screen message, skip resource capture

13. Set recorder.UndockSiblingPid = 0  (tree handles vessel tracking, no sibling needed)
```

#### 10.3 EVA-Specific Logic

For EVA branches, the active/background assignment is inverted:
- KSP switches the active vessel to the EVA kerbal
- The kerbal recording is the active child
- The vessel recording is the background child
- `EvaCrewName` is set on the kerbal child recording (not the vessel child)

```
if (branchType == BranchPointType.EVA)
{
    // Kerbal is active (KSP switched to it), vessel is background
    activeChildRec = kerbalRecording;
    backgroundChildRec = vesselRecording;
    kerbalRecording.EvaCrewName = evaCrewName;
    // ParentRecordingId on EVA child for VesselSpawner crew stripping
    kerbalRecording.ParentRecordingId = vesselRecording.RecordingId;
}
```

#### 10.4 Active Vessel Determination

After the one-frame deferral:
- For undock: `FlightGlobals.ActiveVessel` is the player's vessel. The undocked vessel is `newVessel`.
- For EVA: `FlightGlobals.ActiveVessel` is the EVA kerbal. The vessel is `newVessel` = the ship the kerbal left. Wait — actually, in `OnCrewOnEva`, `data.from` is the vessel part and `data.to` is the EVA kerbal part. After one frame, the active vessel is the EVA kerbal. The "new vessel" for branching purposes depends on perspective.

**Clarification for EVA:** After EVA, the original vessel is NOT a "new" vessel — it retains its PID. The EVA kerbal IS the new vessel. So:
- `newVesselPid` = EVA kerbal's vessel PID
- `recorder.RecordingVesselId` = original vessel PID (the one we were recording)
- Active vessel after switch = EVA kerbal
- Background vessel = original vessel (still has its PID)

This means for EVA:
- The "parent" recording is the original vessel recording
- Child 1 = original vessel (continues with same PID, goes to background)
- Child 2 = EVA kerbal (new PID, becomes active)

---

### 11. BackgroundRecorder Wiring

#### 11.1 First-Time Instantiation

The BackgroundRecorder is created during the first split event (when `activeTree` is first created):

```csharp
// In CreateSplitBranch, after creating the tree:
if (backgroundRecorder == null)
{
    backgroundRecorder = new BackgroundRecorder(activeTree);
    Patches.PhysicsFramePatch.BackgroundRecorderInstance = backgroundRecorder;
}
```

The BackgroundRecorder constructor (BackgroundRecorder.cs line 37-56) iterates `tree.BackgroundMap` and creates `BackgroundOnRailsState` for each existing background vessel. Since we just added the background child to the BackgroundMap, the call to `OnVesselBackgrounded` (which comes after the constructor) will initialize the proper tracking state.

#### 11.2 Subsequent Splits

For subsequent splits (when `activeTree` is already non-null), the BackgroundRecorder already exists. Just call `backgroundRecorder.OnVesselBackgrounded(newVesselPid)` after adding the new vessel to the BackgroundMap.

#### 11.3 Lifecycle

- Created: first split event
- Updated: `ParsekFlight.Update()` calls `backgroundRecorder.UpdateOnRails(ut)` (line 438)
- Physics: `PhysicsFramePatch.Postfix` calls `BackgroundRecorderInstance.OnBackgroundPhysicsFrame(bgVessel)` (line 67)
- Shutdown: `OnSceneChangeRequested` (line 578-583) and `OnFlightReady` (line 1597-1602)

---

### 12. Integration with Tasks 2-3

#### 12.1 Chain Events During Tree Mode

Per the Task 2 plan (section 7.3): "Chain events during tree mode explicitly deferred to Tasks 4/5."

**Resolution:** When a tree is active, the following chain events are **replaced** by tree branches:

| Event | Legacy behavior | Tree mode behavior |
|-------|----------------|-------------------|
| Undock (remaining vessel) | `CommitDockUndockSegment` + `StartUndockContinuation` | Tree branch + BackgroundRecorder |
| Undock (switch to other) | `UndockSwitchPending` + role swap | Tree branch + BackgroundRecorder |
| EVA (mid-recording) | `CommitChainSegment` + `pendingAutoRecord` | Tree branch + BackgroundRecorder |
| EVA (boarding) | `ChainToVesselPending` + `CommitChainSegment` | Deferred to Task 5 (merge event) |
| Dock | `DockMergePending` + `CommitDockUndockSegment` | Deferred to Task 5 (merge event) |

**Implementation:** At the top of `OnPartUndock` and `OnCrewOnEva` (mid-recording path), check if we should use tree branching vs. chain logic. The decision is simple: **always use tree branching when recording.**

The rationale: once we create the first tree branch, ALL subsequent events should use tree semantics. And the first undock/EVA creates the tree. So effectively, every undock/EVA during recording creates a tree branch. The legacy chain logic only runs when the player is NOT recording (which doesn't apply to these events — they only fire during active recording).

**Guard:** If for some reason tree creation fails (e.g., vessel not found after deferral), fall back to legacy chain behavior. This ensures no data loss.

#### 12.2 Preventing Chain Logic from Interfering

When tree branching is triggered, we must prevent the existing chain logic from running:

For `OnPartUndock`:
- Do NOT set `pendingUndockOtherPid`
- Do NOT call `recorder.StopRecordingForChainBoundary()`
- The coroutine handles everything

For `OnCrewOnEva`:
- Do NOT set `pendingChainContinuation`, `pendingChainEvaName`, `activeChainCrewName`
- Do NOT set `pendingAutoRecord`
- The coroutine handles everything

For `OnPartJointBreak`:
- The existing part event recording is unchanged
- Only the new deferred vessel-creation check is added

#### 12.3 DockMergePending Guard

Task 2's `DockMergePending` guard in `OnPhysicsFrame` (line 3369-3376) continues to work correctly. Dock events are handled by Task 5 and are orthogonal to split events.

#### 12.4 DecideOnVesselSwitch with Active Tree

After tree creation, `recorder.ActiveTree = activeTree` is set. This means `DecideOnVesselSwitch` (line 3830) will use tree-aware decisions for subsequent vessel switches:
- Switching to a background tree vessel → `PromoteFromBackground`
- Switching to a non-tree vessel → `TransitionToBackground`

The existing `UndockSiblingPid` check (line 3838) fires before tree checks. **In tree mode, we should NOT set `UndockSiblingPid`** — otherwise undock switches would be intercepted by the legacy `UndockSwitch` decision before the tree decisions can fire. Set `recorder.UndockSiblingPid = 0` when creating the new recorder after a tree branch.

#### 12.5 EVA Boarding During Tree Mode

When an EVA kerbal boards a vessel that's already in the tree (or the vessel the kerbal came from), this is a merge event. **Deferred to Task 5.** For now, if boarding occurs during tree mode:
- `DecideOnVesselSwitch` returns `ChainToVessel` (line 3847-3848, EVA→vessel)
- This takes priority over tree decisions (per Task 2 design)
- The existing chain handling runs (`ChainToVesselPending` → `CommitChainSegment` → start new recording)
- This is **incorrect for tree semantics** but **acceptable** — Task 5 will intercept boarding events and create proper merge branch points

---

### 13. Unit Tests

New file: `Source/Parsek.Tests/SplitEventDetectionTests.cs`

Tests that can run without Unity:

1. **`IsTrackableVesselType_SpaceObject_ReturnsTrue`** — VesselType.SpaceObject always trackable
2. **`IsTrackableVesselType_Debris_ReturnsFalse`** — VesselType.Debris not trackable
3. **`IsTrackableVesselType_Ship_ReturnsFalse`** — VesselType.Ship alone not trackable (need module check)
4. **`DeduplicationGuard_SameUT_SamePid_SkipsDuplicate`** — last branch UT/PID guard works
5. **`DeduplicationGuard_SameUT_DifferentPid_AllowsBranch`** — different PIDs at same UT are allowed
6. **`DeduplicationGuard_DifferentUT_AllowsBranch`** — different UTs always allowed
7. **`CreateSplitBranch_CreatesCorrectBranchPoint`** — synthetic: verify BranchPoint fields
8. **`CreateSplitBranch_CreatesCorrectChildRecordings`** — synthetic: verify child Recording fields
9. **`CreateSplitBranch_ParentRecordingHasChildBranchPointId`** — verify parent linkage
10. **`CreateSplitBranch_EvaBranch_KerbalIsActive_VesselIsBackground`** — verify EVA active/bg assignment
11. **`CreateSplitBranch_UndockBranch_PlayerVesselIsActive`** — verify undock active/bg assignment
12. **`TreeCreation_FirstSplit_WrapsExistingRecording`** — verify root recording created from existing data
13. **`TreeCreation_FirstSplit_SetsTreeId`** — verify TreeId set on all recordings
14. **`BackgroundMap_ContainsBackgroundChild_AfterSplit`** — verify BackgroundMap population
15. **`EvaBranch_SetsEvaCrewName`** — verify EvaCrewName on kerbal child
16. **`EvaBranch_SetsParentRecordingId`** — verify ParentRecordingId on kerbal child

**Testing strategy:** Most of these tests work with synthetic `RecordingTree` objects (no Unity dependency). Tests 7-16 test the data model output of `CreateSplitBranch` but the method itself needs Unity (`FlightGlobals`, `Vessel`, etc.). Extract the pure data-model logic into a static testable helper:

```csharp
internal static (BranchPoint bp, Recording activeChild, Recording backgroundChild)
    BuildSplitBranchData(
        string parentRecordingId, string treeId, double branchUT,
        BranchPointType branchType, uint activeVesselPid, string activeVesselName,
        uint backgroundVesselPid, string backgroundVesselName,
        string evaCrewName = null)
```

This pure method creates the `BranchPoint` and child `Recording` objects without touching Unity APIs. The `CreateSplitBranch` method calls this helper and then does the Unity-specific work (snapshots, recorder creation, etc.).

---

### 14. In-Game Test Scenarios

1. **Basic undock — controllable vessel splits:** Launch a two-stage rocket where the upper stage has a probe core. Stage → verify the lower booster (no command module) does NOT create a branch. Undock the probe-equipped upper stage → verify a branch IS created. Check `KSP.log` for tree creation and branch point messages.

2. **EVA mid-recording:** Launch with crew, start recording (auto or F9), EVA a kerbal → verify tree created with EVA branch. Switch back to vessel → verify `PromoteFromBackground` fires. Revert → verify tree dialog shows both vessels.

3. **Joint break (structural failure):** Build a rocket with a weak joint (reduce max temp, use ALT+F12 to add heat). Start recording. Break the joint → verify deferred check creates a branch if both pieces have command modules.

4. **Staging debris filter:** Launch a typical multi-stage rocket (SRBs, decouplers, fairings). Record the ascent. Stage away SRBs → verify no branch (debris filter). Stage away upper stage with probe core → verify branch created.

5. **Rapid sequential undocks:** Build a vessel with multiple docking ports. Undock them in quick succession → verify separate branch points created for each, no crashes, no duplicate branches.

6. **Vessel switch after tree creation:** Create a tree via undock. Press `]` to switch to the background vessel → verify `PromoteFromBackground` fires. Press `[` to switch back → verify `TransitionToBackground` + `PromoteFromBackground`. Both vessels' recordings should have data.

7. **Scene exit with active tree:** Create a tree via undock, then go to Space Center → verify tree state is cleaned up (no crash, no orphan recordings).

---

### 15. Files Modified/Created

| File | Changes |
|------|---------|
| `Source/Parsek/ParsekFlight.cs` | New `CreateSplitBranch` method, `DeferredUndockBranch` coroutine, `DeferredEvaBranch` coroutine, `IsTrackableVessel` static method, `lastBranchUT`/`lastBranchVesselPids` fields, modified `OnPartUndock` (tree branch instead of chain), modified `OnCrewOnEva` (tree branch instead of chain), deferred joint break check in Update(), BackgroundRecorder instantiation on first tree creation, `OnFlightReady` reset of new fields |
| `Source/Parsek/FlightRecorder.cs` | New `pendingJointBreakPartPids` field, modified `OnPartJointBreak` (signal potential split), `HasPendingJointBreakChecks` property, `ConsumePendingJointBreakPids` method |
| `Source/Parsek.Tests/SplitEventDetectionTests.cs` | New test file — unit tests for debris filter, deduplication, branch data model |

---

### 16. Implementation Order

1. **Add `IsTrackableVessel` static method** to ParsekFlight. Extract `IsTrackableVesselType` for testability.

2. **Add deduplication fields** (`lastBranchUT`, `lastBranchVesselPids`) to ParsekFlight. Reset in `OnFlightReady` and `OnSceneChangeRequested`.

3. **Add `BuildSplitBranchData` static helper** — pure data-model branch creation logic, testable without Unity.

4. **Add `CreateSplitBranch` method** to ParsekFlight — orchestrates tree creation (or extension), calls `BuildSplitBranchData`, handles snapshots, creates recorder, wires BackgroundRecorder.

5. **Add `DeferredUndockBranch` coroutine** to ParsekFlight.

6. **Modify `OnPartUndock`** — intercept with tree branching when recording, bypass chain logic.

7. **Add `DeferredEvaBranch` coroutine** to ParsekFlight.

8. **Modify `OnCrewOnEva` (mid-recording path)** — intercept with tree branching, bypass chain logic.

9. **Add joint break signaling** to FlightRecorder (`pendingJointBreakPartPids`, `HasPendingJointBreakChecks`, `ConsumePendingJointBreakPids`).

10. **Add deferred joint break check** in ParsekFlight.Update().

11. **Add `DeferredJointBreakCheck` coroutine** to ParsekFlight.

12. **Write unit tests** for `BuildSplitBranchData`, deduplication guard, vessel type filter.

13. **Run `dotnet test`** — all existing + new tests pass.

14. **Run `dotnet build`** — verify compilation with KSP assemblies.

---

### 17. Risk Assessment

| Risk | Mitigation |
|------|-----------|
| **Coroutine timing:** One-frame deferral may not be enough for KSP to finalize vessel split in all cases | KSP documentation and modding community confirm one-frame is sufficient. If issues arise, increase to `yield return new WaitForFixedUpdate()` (waits for next physics frame). |
| **Race between OnPartUndock and OnPhysicsFrame:** Undock changes the vessel PID. OnPhysicsFrame may detect the mismatch before the deferred branch runs | The deferred coroutine runs in the NEXT Update(). Meanwhile, OnPhysicsFrame may detect the PID mismatch and take action (Stop/UndockSwitch/TransitionToBackground). **Mitigation:** Stop the recorder BEFORE starting the coroutine (capture data first), so OnPhysicsFrame won't see a stale recorder. The coroutine creates the tree from the captured data. |
| **OnPhysicsFrame fires during the one-frame deferral** | If the recorder is still active during the deferral frame, OnPhysicsFrame could stop it (PID changed due to undock). **Mitigation:** Call `recorder.StopRecordingForChainBoundary()` (or equivalent) in the event handler BEFORE the coroutine starts. The coroutine uses the stopped recorder's captured data to build the tree. |
| **Multiple event handlers firing for the same split** | OnPartUndock + OnPartJointBreak can fire for the same event. Deduplication guard (lastBranchUT + lastBranchVesselPids) prevents duplicate branches. |
| **Undock continuation interference** | In tree mode, the undock continuation system must NOT run. Guard: if tree branch was created, do NOT call `StartUndockContinuation`. |
| **Chain state interference** | In tree mode, chain state (`activeChainId`, etc.) must NOT be modified by split events. Guard: check before setting chain fields. |
| **BackgroundRecorder not yet created for first background vessel** | The BackgroundRecorder is created BEFORE the background vessel is added to BackgroundMap, then `OnVesselBackgrounded` is called after. Constructor + OnVesselBackgrounded is the correct initialization sequence. |
| **EVA vessel switch timing** | KSP switches the active vessel to the EVA kerbal asynchronously. The one-frame deferral should be sufficient, but the active vessel may still be the ship. **Mitigation:** Check `FlightGlobals.ActiveVessel` in the coroutine and handle both cases. |
| **Existing chain recordings coexist with tree recordings** | Per design doc section "Backward compatibility": chains remain fully intact within each tree node. A recording in the tree can itself be part of a chain (atmosphere/SOI splits). No conflict. |
| **Deferred coroutine runs after scene change** | If the player exits Flight during the one-frame deferral, the coroutine may run in a bad state. **Mitigation:** Guard in the coroutine: check `FlightGlobals.ActiveVessel != null` and `recorder != null` before proceeding. |

---

### 18. Detailed Recorder Lifecycle During Split

This section clarifies the exact sequence of operations during a split, addressing the race condition between `OnPartUndock`/`OnCrewOnEva` and `OnPhysicsFrame`.

#### 18.1 Problem: Recorder State During Deferral

When `OnPartUndock` fires, the recorder is still active and sampling. In the next physics frame (which may fire BEFORE the deferred coroutine's next Update), `OnPhysicsFrame` will see a PID mismatch (the vessel split changed PIDs). Without intervention, `OnPhysicsFrame` would:
- If no tree: return `Stop` → create CaptureAtStop, set IsRecording=false
- If tree exists: return `TransitionToBackground` → call TransitionToBackground()

Neither of these is correct for a split event. The split creates a NEW tree (or extends an existing one), which is different from a simple vessel switch.

#### 18.2 Solution: Stop Recorder Synchronously, Defer Tree Creation

The event handler (`OnPartUndock` / `OnCrewOnEva`) should:

1. **Immediately** stop the recorder and capture its data:
   ```csharp
   recorder.StopRecordingForChainBoundary();
   // recorder.CaptureAtStop now has all accumulated data
   // recorder.IsRecording is false
   // OnPhysicsFrame will see IsRecording=false and return immediately
   ```

2. **Store the captured data** in fields that the deferred coroutine can access:
   ```csharp
   pendingSplitRecorder = recorder;
   recorder = null;  // prevent other Update() handlers from consuming it
   ```

3. **Start the deferred coroutine** which, after one frame, uses `pendingSplitRecorder` to build the tree.

This way:
- `OnPhysicsFrame` sees `IsRecording = false` → returns immediately (no interference)
- The `Update()` chain handlers see `recorder = null` → skip all their checks
- The deferred coroutine has exclusive access to the captured data

#### 18.3 Fallback

If the deferred coroutine fails (vessel not found, debris filtered, etc.), the captured recording data must not be lost. **Fallback:** fall back to legacy chain behavior — set `pendingUndockOtherPid` and let the existing `Update()` handler commit it as a chain segment.

However, this is complex. **Simpler approach:** if tree creation fails, create a standalone recording from the captured data (StashPending + CommitPending) and log a warning. The split recording may be incomplete, but data is preserved.

---

### 19. State Cleanup Checklist

Fields to reset in `OnFlightReady()` (line 1592):
- `lastBranchUT = -1`
- `lastBranchVesselPids.Clear()`
- `pendingSplitRecorder = null` (if applicable)
- `activeTree = null` (already done, line 1605)
- `backgroundRecorder = null` (already done via Shutdown, line 1597-1602)

Fields to reset in `OnSceneChangeRequested()` (line 543):
- `lastBranchUT = -1`
- `lastBranchVesselPids.Clear()`
- `pendingSplitRecorder = null`
- `activeTree = null` (already done, line 584)
- `backgroundRecorder` shutdown (already done, line 578-583)

---

### 20. Interaction with Existing Undock Continuation and Chain Systems

#### 20.1 What Gets Replaced by Tree Branching

When tree branching is active (which is always after the first split, since the first split creates the tree):

| Legacy mechanism | Tree replacement |
|-----------------|-----------------|
| `pendingUndockOtherPid` → `CommitDockUndockSegment` → `StartUndockContinuation` | Tree branch + BackgroundRecorder |
| `UndockSwitchPending` → role swap + continuation | Tree branch + BackgroundRecorder |
| `pendingChainContinuation` + `pendingAutoRecord` (EVA) | Tree branch + BackgroundRecorder |
| `CommitChainSegment` (EVA) | Tree branch |
| `activeChainCrewName` / `pendingChainEvaName` | `EvaCrewName` on child Recording |

#### 20.2 What Stays

| Mechanism | Why it stays |
|-----------|-------------|
| `CommitDockUndockSegment` | Still needed for dock events (Task 5), and as fallback for non-tree mode |
| `StartUndockContinuation` | Still needed for non-tree mode (manual recording start/stop without auto-record) |
| `CommitChainSegment` | Still needed for non-tree EVA (when auto-record is off and player manually records) |
| `CommitBoundarySplit` (atmosphere/SOI) | Still needed — these create intra-node chain segments, not tree branches |
| `pendingDockAsTarget` / `DockMergePending` | Dock events deferred to Task 5 |
| `ChainToVesselPending` | Boarding events deferred to Task 5 |

#### 20.3 Coexistence Strategy

The key question: when a split event fires, should we ALWAYS use tree branching, or only sometimes?

**Decision: Always use tree branching during recording.** Rationale:
- The first split creates the tree. All subsequent events during the same recording session should use tree semantics.
- There's no scenario where a split event fires during recording but we want legacy chain behavior instead of tree behavior.
- Edge case: player starts recording manually (F9), undocks, stops recording (F9). In this case, the tree was created at undock, and stopping recording should finalize the tree (not the standalone recording). This is correct behavior.

**Guard for non-recording state:** If `!IsRecording`, the event handlers use existing behavior (or do nothing). Tree branching only applies during active recording.

---

### 21. Summary of Key Design Decisions

1. **Tree created on first split, not at launch** — preserves simplicity for standalone recordings
2. **Always use tree branching during recording** — no mixed chain+tree mode for split events
3. **Recorder stopped synchronously, tree built in deferred coroutine** — avoids race with OnPhysicsFrame
4. **UndockSiblingPid = 0 in tree mode** — prevents legacy UndockSwitch from intercepting tree vessel switches
5. **Undock continuation NOT started in tree mode** — BackgroundRecorder handles it
6. **Debris filter: ModuleCommand || VesselType.SpaceObject** — [ORCHESTRATOR FIX] `ModuleProbeCore` doesn't exist in KSP; `ModuleCommand` covers both crewed pods and probe cores
7. **Deduplication: lastBranchUT + lastBranchVesselPids** — handles multi-part breakup and rapid events
8. **One-frame deferral via coroutine** — per design doc, ensures KSP finalizes vessel split
9. **EVA child gets EvaCrewName + ParentRecordingId** — preserves crew stripping at spawn time
10. **BuildSplitBranchData extracted as pure static** — testable without Unity

---

### 22. Orchestrator Review Notes

**Issues fixed from plan review:**

1. **`ModuleProbeCore` does not exist in KSP** (CRITICAL): Removed all references. In KSP, probe cores use `ModuleCommand` with `minimumCrew=0`. The `ModuleCommand` check alone is sufficient for the debris filter. Fixed in sections 7.1, 7.4, and 21.

2. **Missing `JointBreak` BranchPointType** (CRITICAL): The `BranchPointType` enum (BranchPoint.cs) only has `Undock=0, EVA=1, Dock=2, Board=3`. Must add `JointBreak=4`. Integer serialization handles this automatically. Add `BranchPoint.cs` to the files modified list.

3. **Race condition guard during deferred coroutine** (CRITICAL): Between `recorder = null` and the coroutine running next frame, `Update()` could create a new recorder via `pendingAutoRecord` or other paths. **Fix: Add a `pendingSplitInProgress` boolean field.** Set `true` when the split event handler fires, set `false` when the deferred coroutine completes. Guard `StartRecording()` calls and `Update()` auto-record paths with `if (pendingSplitInProgress) return;`. Reset in `OnFlightReady` and `OnSceneChangeRequested`.

4. **`BranchPoint.cs` missing from files modified list** (IMPORTANT): Must be listed since `JointBreak=4` needs to be added to `BranchPointType`.

5. **Fallback on tree creation failure** (IMPORTANT): If the deferred coroutine fails (debris filtered, vessel not found), the captured recording data should be committed as a standalone recording via `StashPending` + `CommitPending` path. Data preservation is the priority. Full legacy chain restoration is too complex for a fallback path.

6. **Deduplication: use refined version with `lastBranchVesselPids` HashSet** (MINOR): The plan has two versions — the simple UT-only guard in section 4.3 and the refined version with PID tracking in section 8.3. Implementation must use the refined version. The UT-only guard would incorrectly reject legitimate multi-vessel splits at the same UT.

7. **`pendingJointBreakPartPids` simplification** (MINOR): The part PIDs stored in this field are never used — the deferred check scans `FlightGlobals.Vessels` for new vessels regardless. Simplify to a `bool pendingJointBreakCheck` flag instead.

8. **EVA active/background assignment** (IMPORTANT): In the deferred coroutine, explicitly check `FlightGlobals.ActiveVessel.persistentId` to determine which child is active. If it matches the EVA kerbal → kerbal is active, vessel is background. If it matches the ship → ship is active, kerbal is background. Handle both cases; do not assume KSP's vessel switch has completed.
