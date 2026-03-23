# Task 5: Merge Event Detection (Dock, Board)

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

Task 5 is where the recording tree merges branches back together at runtime. Where Task 4 branches the tree (undock, EVA, joint break), Task 5 converges branches (dock, board). When two vessels dock or an EVA kerbal boards a vessel, two parent recordings end and one child recording begins for the merged entity.

This is conceptually the inverse of `CreateSplitBranch`: instead of 1 parent and 2 children, merges create 2 parents and 1 child. The `BranchPoint` data structure already supports this via its `parentRecordingIds` list (which supports 2+ entries) and `childRecordingIds` list (which will have exactly 1 entry for merges).

Tasks 1-4 built the foundation:
- **Task 1**: `BranchPoint` with `BranchPointType.Dock` (=2), `BranchPointType.Board` (=3); `parentRecordingIds` supports lists of 2+ parents; `TerminalState.Docked` (=6), `TerminalState.Boarded` (=7)
- **Task 2**: `DockMergePending` handling, `ChainToVesselPending` handling, `OnPartCouple` subscription, `OnCrewBoardVessel` subscription, `OnVesselSwitchComplete`, `FlushRecorderToTreeRecording`, `PromoteRecordingFromBackground`
- **Task 3**: `BackgroundRecorder` with `OnVesselRemovedFromBackground(pid)`, `OnBackgroundVesselWillDestroy(v)`, `OnVesselBackgrounded(pid)`
- **Task 4**: `CreateSplitBranch`, `BuildSplitBranchData` (pure data model method), `pendingSplitInProgress` guard, `ResumeSplitRecorder`, `FallbackCommitSplitRecorder`, deferred coroutine pattern

### 1.1 Merge Scenarios

There are four distinct merge scenarios:

1. **Active vessel docks with background vessel** -- Both are in the tree. The active vessel approaches a background vessel (KSP auto-loads it into physics range), then docks. Two parent recordings end, one child starts.

2. **Active vessel docks with foreign vessel** -- Active vessel is in the tree, partner is NOT in the tree (spawned from a previous recording tree, or a vessel the player manually launched). Only one parent recording exists. Single-parent merge.

3. **EVA kerbal boards tree vessel** -- Kerbal and vessel are both in the tree (kerbal went EVA via split, now returns). Both parent recordings end, child recording starts.

4. **EVA kerbal boards foreign vessel** -- Kerbal is in the tree, vessel is NOT. Only one parent (kerbal recording). Single-parent merge.

---

### 2. Existing Infrastructure Audit

#### 2.1 `OnPartCouple` Handler (ParsekFlight.cs, lines 1785-1820)

The existing `OnPartCouple` handler fires when two vessels dock. Current behavior:

```csharp
void OnPartCouple(GameEvents.FromToAction<Part, Part> data)
```

- `data.to.vessel` is the **dominant** (surviving) vessel -- its PID becomes the merged vessel's PID
- `data.from` is the **absorbed** vessel's part -- it gets absorbed into the dominant vessel
- The handler identifies the merged PID as `data.to.vessel.persistentId` (line 1788)
- **If recording as TARGET** (merged PID == recorder PID, line 1793): sets `pendingDockAsTarget = true`, calls `recorder.StopRecordingForChainBoundary()` (line 1800)
- **If recording as INITIATOR** (merged PID != recorder PID, line 1803): sets `recorder.DockMergePending = true` (line 1806) -- OnPhysicsFrame will detect the PID change and stop via `CaptureAtStop`
- **If already stopped** (retroactive, line 1812): sets `recorder.DockMergePending = true` (line 1815)

**Key KSP behavior**: `data.to.vessel` is the vessel that survives. The `data.from` part's vessel is absorbed and destroyed by KSP shortly after. `onVesselWillDestroy` fires for the absorbed vessel in the same frame or next frame.

#### 2.2 `DockMergePending` Flow (FlightRecorder.cs, lines 86, 3421-3501; ParsekFlight.cs, lines 248-263)

When `DockMergePending` is true on the recorder:

1. In `OnPhysicsFrame` (FlightRecorder.cs, line 3424): If `DockMergePending`, the decision is forced to `VesselSwitchDecision.DockMerge` (bypasses `DecideOnVesselSwitch`). This falls through to the `CaptureAtStop` block which stops recording and sets `IsRecording = false`.

2. In `Update()` (ParsekFlight.cs, lines 248-263): The guard checks `recorder.DockMergePending && !recorder.IsRecording && recorder.CaptureAtStop != null`. When true:
   - Calls `CommitDockUndockSegment(recorder, PartEventType.Docked, pendingDockMergedPid)` (line 251)
   - Sets `recorder = null`, calls `StartRecording()` (lines 252-253)
   - Clears pending state (lines 261-263)

#### 2.3 `pendingDockAsTarget` Flow (ParsekFlight.cs, lines 93, 267-282)

When the recorded vessel is the dock target (PID unchanged):

1. `OnPartCouple` (line 1796): sets `pendingDockAsTarget = true`, calls `StopRecordingForChainBoundary()`
2. `Update()` (lines 267-282): same pattern as initiator -- `CommitDockUndockSegment`, restart recording

**Critical**: The target case uses `StopRecordingForChainBoundary()` which stops recording synchronously in the event handler (the PID does not change, so OnPhysicsFrame would NOT catch it).

#### 2.4 `CommitDockUndockSegment` (ParsekFlight.cs, lines 1887-1953)

Commits the current segment as a dock/undock chain boundary:
- Calls `RecordingStore.StashPending()` with captured data
- Initializes chain if needed (`activeChainId`)
- Tags with chain metadata
- Nulls VesselSnapshot (mid-chain segments are ghost-only)
- Commits and reserves crew

**In tree mode, this method will NOT be used for dock merges.** Instead, a new `CreateMergeBranch` method will handle tree merge logic. The existing `CommitDockUndockSegment` continues to work for non-tree mode.

#### 2.5 `OnCrewBoardVessel` Handler (ParsekFlight.cs, lines 1488-1504)

```csharp
void OnCrewBoardVessel(GameEvents.FromToAction<Part, Part> data)
```

- `data.from` = EVA kerbal's part (being absorbed)
- `data.to` = vessel's part (receiving the kerbal)
- Current behavior: only acts if `activeChainId != null` (chain mode)
- Sets `pendingBoardingTargetPid = data.to.vessel.persistentId` (line 1501)
- This is consumed in `Update()` (lines 339-373) when `ChainToVesselPending` fires

**In tree mode**: boarding should create a merge branch point instead of a chain segment.

#### 2.6 `ChainToVesselPending` Flow (FlightRecorder.cs, lines 85, 3490-3494, 3895-3900)

When `DecideOnVesselSwitch` determines EVA-to-vessel transition:
- Returns `VesselSwitchDecision.ChainToVessel` (line 3900)
- `OnPhysicsFrame` sets `ChainToVesselPending = true` (line 3492), creates `CaptureAtStop`
- `Update()` handles it (lines 339-373): checks `activeChainId` and `pendingBoardingTargetPid` confirmation

**Key**: `DecideOnVesselSwitch` checks `!currentIsEva && recordingStartedAsEva` to detect boarding (line 3899). This runs BEFORE tree decisions (lines 3902-3907). So boarding is always classified as `ChainToVessel`, never `TransitionToBackground`.

#### 2.7 `OnVesselWillDestroy` Handler (ParsekFlight.cs, lines 731-762)

Forwards to `recorder?.OnVesselWillDestroy(v)` and `backgroundRecorder?.OnBackgroundVesselWillDestroy(v)`. Also handles continuation vessel cleanup.

**Docking race condition**: When vessel A absorbs vessel B via docking, KSP destroys B's Vessel object. `OnVesselWillDestroy` fires for B. If B is a background vessel in the tree, `OnBackgroundVesselWillDestroy` will close its orbit segments. Task 6's destruction detection (deferred `onVesselDestroy` check) would incorrectly mark B as destroyed.

#### 2.8 `OnVesselSwitchComplete` (ParsekFlight.cs, lines 769-827)

Handles vessel switch transitions in tree mode. When the active vessel changes:
- Transitions old recorder to background (if not already done by OnPhysicsFrame)
- Promotes new vessel from background if it's in the BackgroundMap

**Note**: This handler fires for ALL vessel switches, including dock-induced switches. After docking, KSP may switch the active vessel (especially if the player's vessel was the absorbed one). The handler must not interfere with dock merge processing.

#### 2.9 `BackgroundRecorder.OnVesselRemovedFromBackground` (BackgroundRecorder.cs, lines 282-314)

Cleanly removes a vessel from background tracking:
- Closes any open orbit segment
- Samples a final boundary point if the vessel is still loaded
- Removes from `onRailsStates` and `loadedStates` dictionaries

This method is called when a vessel is promoted or terminates. For merges, it will be called for the absorbed vessel.

#### 2.10 `BuildSplitBranchData` Pattern (ParsekFlight.cs, lines 955-1013)

Pure data-model method used by Task 4 for splits. The merge equivalent (`BuildMergeBranchData`) will follow the same pattern:
- Static `internal` method -- testable without Unity
- Returns a tuple of `(BranchPoint, Recording child)`
- Creates new GUIDs for IDs
- Sets up linkage (ParentBranchPointId on child, ChildBranchPointId on parents)

#### 2.11 `RebuildBackgroundMap` (RecordingTree.cs, lines 110-124)

Populates BackgroundMap from recordings on load. Key filter: recordings with `TerminalStateValue == null && ChildBranchPointId == null && RecordingId != ActiveRecordingId`. This means:
- A recording with `ChildBranchPointId` set (i.e., it has already branched or merged) is excluded from BackgroundMap
- A recording with `TerminalStateValue` set (destroyed, docked, boarded) is excluded
- Both filters must be set on parent recordings when a merge is created

---

### 3. Dock Merge Flow

#### 3.1 High-Level Sequence

When two vessels dock:

1. KSP fires `onPartCouple(FromToAction<Part, Part>)` (same frame as dock physics)
2. `OnPartCouple` handler determines:
   - `data.to.vessel` = dominant/surviving vessel (receives the merged PID)
   - `data.from.vessel` = absorbed vessel (will be destroyed by KSP)
3. **Tree mode check**: if `activeTree != null`, intercept BEFORE existing chain logic
4. Identify absorbed vessel's PID: the vessel that is NOT the surviving vessel
5. Look up absorbed vessel's recording in `activeTree.BackgroundMap`
6. **If found** (both in tree): two-parent merge
7. **If not found** (foreign vessel): single-parent merge
8. End both parent recordings at merge UT
9. Add absorbed vessel PID to `dockingInProgress` set (prevents false destruction in Task 6)
10. Create `BranchPoint` (type=Dock) with 2 parents (or 1 for foreign) and 1 child
11. Start child recording on merged vessel
12. Clean up: remove absorbed vessel from BackgroundMap, notify BackgroundRecorder

#### 3.2 Determining Absorbed vs. Surviving Vessel

KSP's `onPartCouple` provides:
- `data.to.vessel` -- the vessel that **survives** (dominant vessel). This is the merged vessel.
- `data.from.vessel` -- the vessel being **absorbed**. After coupling completes, this vessel's `Vessel` object is destroyed by KSP.

**Which vessel survives?** KSP typically makes the vessel with more parts the dominant one. But the deterministic answer comes from the event data: `data.to.vessel` is always the survivor.

**The absorbed vessel PID**: At the time `onPartCouple` fires, `data.from.vessel` is still alive but about to be destroyed. We can read its `persistentId` safely. However, note that by the time `onPartCouple` fires, the part tree may already be rearranged (the `data.from` part is now parented to the `data.to` vessel). So we need to identify the absorbed vessel BEFORE the coupling -- we can get the original vessel PID from the `data.from` part.

**Important nuance**: KSP calls `onPartCouple` AFTER the coupling has occurred. By this point, `data.from.vessel` may already be the same as `data.to.vessel` (since the part has been reparented). We need an alternative approach:

Looking at the existing code (line 1788): `uint mergedPid = data.to.vessel.persistentId;`. The handler then compares this to `recorder.RecordingVesselId`. This works because:
- If our recorded vessel is the TARGET (PID matches merged PID), we know the OTHER vessel was absorbed
- If our recorded vessel is the INITIATOR (PID doesn't match merged PID), we know WE are being absorbed (our PID changes to merged PID)

For tree mode, we need to identify the absorbed vessel's PID. The approach:
- `mergedPid = data.to.vessel.persistentId` (the surviving vessel's PID)
- If `recorder.RecordingVesselId == mergedPid`: we are the target. The absorbed vessel was the OTHER vessel. We need its PID from somewhere.
- If `recorder.RecordingVesselId != mergedPid`: we are the initiator. We are being absorbed. `recorder.RecordingVesselId` is the absorbed PID (it will change to mergedPid).

**Finding the absorbed vessel's PID when we are the target**: The absorbed vessel was `data.from`'s original vessel. At event time, `data.from.vessel` may already point to the merged vessel. But `data.from.flightID` or `data.from.persistentId` can be used. Actually, the key insight is simpler: every vessel in the tree's BackgroundMap has a known PID. When `onPartCouple` fires and we are the target, the absorbed vessel must be one of the background vessels. We scan the BackgroundMap for a vessel that is currently within physics range / docking distance. But this is fragile.

**Better approach**: Use the KSP event data directly. Before coupling, `data.from.vessel` and `data.to.vessel` are different vessels. At the time `onPartCouple` fires, the coupling has already happened, but `data.from.vessel` may still reference the (now-empty, about-to-be-destroyed) vessel. Testing the existing code shows the handler at line 1788 successfully reads `data.to.vessel.persistentId`, so `data.from.vessel` should also be accessible.

Actually, re-reading the KSP API and the existing code more carefully: `data.from` is the part on the vessel that initiated the coupling. `data.to` is the part on the vessel that received the coupling. The coupling merges `data.from.vessel` INTO `data.to.vessel`. After coupling, `data.from.vessel == data.to.vessel` (the part was reparented). But `data.to.vessel.persistentId` is the surviving PID.

The absorbed vessel's original PID is NOT directly available from the event data after coupling. However, we can compute it: the absorbed vessel was whichever vessel had a different PID from `mergedPid` immediately before the event. For the active recorder, if `recorder.RecordingVesselId != mergedPid`, then `recorder.RecordingVesselId` was the absorbed PID. For background vessels, we can check `tree.BackgroundMap` entries.

**Practical approach** (matching existing code patterns):

```
mergedPid = data.to.vessel.persistentId  // surviving vessel
activeRecorderPid = recorder.RecordingVesselId  // what we were recording

if (activeRecorderPid == mergedPid):
    // We are the target. The absorbed vessel is some other vessel.
    // Check BackgroundMap for the partner.
    absorbedPid = FindDockingPartner(mergedPid)  // scan BackgroundMap for loaded vessel near us
else:
    // We are the initiator. We are being absorbed.
    absorbedPid = activeRecorderPid
```

**Even simpler**: Store `pendingDockAbsorbedPid` in `OnPartCouple`. Before the coupling, when `onPartCouple` fires, we can save both vessel PIDs. But the coupling has already happened by the time the event fires...

After careful analysis, the cleanest approach is:

1. When we are the **initiator** (PID changes): `absorbedPid = recorder.RecordingVesselId` (our own old PID). The background partner is at `mergedPid` -- look up in BackgroundMap.
2. When we are the **target** (PID unchanged): We need the absorbed vessel's PID. Since the absorbed vessel was a background vessel, scan `tree.BackgroundMap` for any vessel whose `Vessel` object is about to be destroyed (or no longer exists). In practice, we can track this by noting which PIDs in the BackgroundMap are loaded into physics range. But a simpler approach: use the existing `pendingDockMergedPid` to store the merged vessel PID, and add a new `pendingDockAbsorbedPid` field.

**Final solution**: Add `pendingDockAbsorbedPid` field. In `OnPartCouple`, before the part is fully reparented, we can try to identify the absorbed vessel PID. If `data.from` has a reference to its original vessel (which it may or may not), use that. Otherwise, for the target case, iterate `tree.BackgroundMap` and find the vessel that just docked by checking loaded vessels near the docking port.

Actually, the simplest reliable approach: KSP's `GameEvents.onPartCouple` passes `FromToAction<Part, Part>`. The `from` part is the part that initiated docking (on the approaching vessel). The `to` part is the docking port on the receiving vessel. Even after coupling, we can check every vessel in BackgroundMap to see if it has been absorbed (its Vessel object is gone or its parts have been reparented to the merged vessel). The timing is: `onPartCouple` fires after the merge but before `onVesselDestroy`. So the absorbed vessel's `Vessel` object may still exist momentarily.

Let me look at this from a different angle. The issue is only for the **target** case (recorder PID unchanged). In this case, the docking partner approached US. That partner could be:
- A vessel in the tree's BackgroundMap
- A foreign vessel (not in the tree)

For the target case, we need to find the partner's PID. We know `data.from` was the partner's docking port. Even after coupling, `data.from.vessel` now points to OUR vessel (merged). But before that, the partner had a different PID.

**Key insight from KSP modding**: `data.from.flightID` is the part's unique flight ID. The vessel that owned it before coupling had a specific PID. We can iterate `FlightGlobals.Vessels` and find any vessel that just lost all its parts (empty vessel about to be destroyed). Or better: iterate `tree.BackgroundMap` and for each background vessel PID, check if `FlightRecorder.FindVesselByPid(pid)` returns null or a vessel with 0 parts. The one that just got absorbed is our partner.

**Simplest robust solution**: Iterate all PIDs in `tree.BackgroundMap`. For each, check if the vessel is still alive and separate from the merged vessel. If a background vessel PID no longer corresponds to a live separate vessel, it was just absorbed. This works because:
- The background vessel was loaded into physics range moments before docking
- After coupling, its parts are reparented to the merged vessel
- Its `Vessel` object either no longer exists or has 0 parts

But even simpler: we track `dockPartnerPid` directly. In the target case (our PID unchanged), the `from` part was on the incoming vessel. We know the incoming vessel's PID changed to match ours (it was absorbed). We can scan `tree.BackgroundMap` to find which background vessel just disappeared.

**Final design decision**: Use a scanning approach in `OnPartCouple`. When tree mode is active:

```csharp
// Store all background PIDs that are currently loaded
// Before coupling completes, one of them may match the docking partner
foreach (var bgPid in tree.BackgroundMap.Keys)
{
    // The absorbed vessel is the background vessel whose Vessel object
    // is about to be destroyed or has been merged
    Vessel bgVessel = FlightRecorder.FindVesselByPid(bgPid);
    if (bgVessel == null || bgVessel == data.to.vessel)
    {
        // This background vessel was absorbed
        absorbedPid = bgPid;
        break;
    }
}
```

This is clean and works for both initiator and target cases. For the initiator case, we already know `absorbedPid = recorder.RecordingVesselId`.

#### 3.3 Detailed Step-by-Step

**In `OnPartCouple` (modified for tree mode):**

```
1. Guard: data.to?.vessel == null → return
2. mergedPid = data.to.vessel.persistentId
3. If activeTree == null → fall through to existing chain logic (unchanged)
4. If recorder == null or !recorder.IsRecording → fall through to existing logic
5. Determine role:
   a. If recorder.RecordingVesselId == mergedPid → we are TARGET
      - absorbedPid = scan BackgroundMap for partner (see above)
      - Stop recorder synchronously: recorder.StopRecordingForChainBoundary()
   b. If recorder.RecordingVesselId != mergedPid → we are INITIATOR
      - absorbedPid = recorder.RecordingVesselId
      - Set recorder.DockMergePending = true (OnPhysicsFrame will stop via CaptureAtStop)
6. Store: pendingDockMergedPid = mergedPid, pendingDockAbsorbedPid = absorbedPid
7. Add absorbedPid to dockingInProgress set (race condition fix)
8. Set pendingMergeDock = true
9. Return (skip existing chain logic)
```

**In `Update()` (new tree dock merge handler, BEFORE existing dock chain handlers):**

```
1. Guard: activeTree != null && pendingMergeDock
2. Guard: recorder stopped (CaptureAtStop available)
   - For TARGET: !recorder.IsRecording && CaptureAtStop != null (StopRecordingForChainBoundary already called)
   - For INITIATOR: DockMergePending && !IsRecording && CaptureAtStop != null (OnPhysicsFrame stopped us)
3. Call CreateMergeBranch(BranchPointType.Dock, ...)
4. Clear: pendingMergeDock = false, dockingInProgress.Remove(absorbedPid)
```

Wait -- this won't work cleanly because there are two paths (target vs initiator) with different stopping mechanisms. Let me reconsider.

**Revised approach**: Unify the stopping mechanism. In tree mode, ALWAYS stop the recorder synchronously in `OnPartCouple` (for both target and initiator cases). This avoids the complexity of the DockMergePending path for tree mode.

For the initiator case: the PID is about to change, which would cause OnPhysicsFrame to stop us via the normal path. But if we stop synchronously in `OnPartCouple`, we need to ensure OnPhysicsFrame doesn't also try to stop us (double-stop). Since `StopRecordingForChainBoundary` sets `IsRecording = false` and disconnects the Harmony patch, OnPhysicsFrame won't fire again. But there's a subtlety: the OnPhysicsFrame that's CURRENTLY running (the one that triggered the dock physics) might try to detect the PID change. However, `OnPartCouple` fires as a GameEvent callback, not from within OnPhysicsFrame. The dock physics happen during KSP's internal FixedUpdate, and `onPartCouple` fires during that same FixedUpdate. Our Harmony postfix on `VesselPrecalculate.CalculatePhysicsStats()` fires at a different point. So there's no reentrant call.

Actually, the timing is more subtle. `onPartCouple` fires during KSP's coupling logic. After `onPartCouple` returns, KSP completes the coupling and may destroy the absorbed vessel. Then, when the next physics frame runs, `VesselPrecalculate.CalculatePhysicsStats()` fires for the merged vessel with a different PID. If we already stopped in `OnPartCouple`, the Harmony patch is disconnected, so it won't fire.

**But wait**: the existing code (non-tree mode) uses a split-phase approach for a reason. The initiator path relies on OnPhysicsFrame detecting the PID change to create `CaptureAtStop` with proper vessel snapshots. `StopRecordingForChainBoundary` also creates `CaptureAtStop`, so the data capture is equivalent.

**Revised design**: In tree mode, for BOTH target and initiator:

```
In OnPartCouple (tree mode):
1. Stop recorder synchronously: recorder.StopRecordingForChainBoundary()
2. Set pendingMergeDock = true with role/pid info
3. Return (skip existing chain logic)

In Update() (tree dock merge handler):
1. Guard: pendingMergeDock && recorder stopped (CaptureAtStop available)
2. CreateMergeBranch(...)
3. Clear state
```

This is cleaner because:
- Both cases stop synchronously (same timing guarantee as the target case in existing code)
- `CaptureAtStop` is always available in `Update()`
- No need for `DockMergePending` in tree mode (that's a legacy chain mechanism)

**But there's a problem**: For the initiator case, after `onPartCouple` returns, the vessel's PID changes. When `Update()` runs, `FlightGlobals.ActiveVessel.persistentId` is now the merged PID. The recorder was stopped with the old PID. This is fine -- `CaptureAtStop` has the old data, and we use `pendingDockMergedPid` to know the new PID.

Actually, the timing concern is: does the PID change happen BEFORE or AFTER `onPartCouple`? Looking at the existing code, `onPartCouple` fires AFTER the coupling. The `data.to.vessel` already has the merged PID. The `data.from` part has been reparented. So at the time `onPartCouple` fires, the PID has already changed (for the initiator case). This means our recorder's `RecordingVesselId` is stale -- it still holds the old PID. But the Harmony patch fires for the merged vessel (new PID) on the next physics frame. Since we stop synchronously in `onPartCouple`, the Harmony patch is disconnected and won't see the PID mismatch. Good.

**One more concern**: What if `onPartCouple` fires between two physics frames, and the Harmony postfix for the CURRENT frame has already run (detecting the PID mismatch and stopping the recorder via the `DockMerge` path)? In this case, when `OnPartCouple` runs, `recorder.IsRecording` might already be false. The existing code handles this at lines 1812-1818 (retroactive initiator dock). We need the same guard for tree mode.

**Three entry states for `OnPartCouple` in tree mode:**

1. `recorder.IsRecording == true, recorder.RecordingVesselId == mergedPid` -- TARGET (common)
2. `recorder.IsRecording == true, recorder.RecordingVesselId != mergedPid` -- INITIATOR, early event (before physics frame)
3. `recorder.IsRecording == false, CaptureAtStop != null` -- INITIATOR, late event (physics frame already stopped us)

For state 3, the recorder was already stopped by OnPhysicsFrame's `DockMerge` path. We need to intercept this in tree mode to prevent the existing chain handler from processing it.

**Revised complete design:**

In `OnPartCouple` (tree mode addition):
```
if (activeTree != null && recorder != null):
    mergedPid = data.to.vessel.persistentId

    if (recorder.IsRecording):
        // State 1 (target) or State 2 (initiator, early)
        isTarget = (recorder.RecordingVesselId == mergedPid)
        absorbedPid = isTarget ? FindAbsorbedPartnerPid() : recorder.RecordingVesselId
        recorder.StopRecordingForChainBoundary()
        pendingTreeDockMerge = true
        pendingDockMergedPid = mergedPid
        pendingDockAbsorbedPid = absorbedPid
        dockingInProgress.Add(absorbedPid)  // race condition fix for Task 6
        return  // skip existing chain logic

    else if (!recorder.IsRecording && CaptureAtStop != null):
        // State 3 (initiator, late -- OnPhysicsFrame already stopped us)
        absorbedPid = recorder.RecordingVesselId  // old PID from before the stop
        pendingTreeDockMerge = true
        pendingDockMergedPid = mergedPid
        pendingDockAbsorbedPid = absorbedPid
        dockingInProgress.Add(absorbedPid)
        // Clear DockMergePending to prevent existing chain handler
        recorder.DockMergePending = false
        return
```

---

### 4. Board Merge Flow

#### 4.1 High-Level Sequence

When an EVA kerbal boards a vessel:

1. KSP fires `onCrewBoardVessel(FromToAction<Part, Part>)` -- `data.from` is the EVA kerbal's part, `data.to` is the vessel's part
2. KSP fires vessel switch -- kerbal vessel is destroyed, active vessel becomes the boarded vessel
3. In `DecideOnVesselSwitch`: `!currentIsEva && recordingStartedAsEva` returns `ChainToVessel` (line 3899-3900)
4. In `OnPhysicsFrame`: `CaptureAtStop` created, `ChainToVesselPending = true`
5. Currently handled by chain logic in `Update()` (lines 339-373)

**In tree mode**, boarding should create a merge branch point instead:

1. `OnCrewBoardVessel` fires -- record the boarding target vessel PID
2. EVA kerbal's vessel is destroyed by KSP (vessel switch happens)
3. `OnPhysicsFrame` detects PID mismatch, returns `ChainToVessel`, sets `ChainToVesselPending`
4. **New tree handler in `Update()`**: intercepts `ChainToVesselPending` when `activeTree != null`
5. Determines: is the boarding target vessel in the tree?
6. If yes (both in tree): two-parent merge (kerbal recording + vessel recording)
7. If no (foreign vessel): single-parent merge (kerbal recording only)
8. Creates `BranchPoint(type=Board)`, ends parent recording(s), starts child recording

#### 4.2 Identifying the Board Partner

When `OnCrewBoardVessel` fires:
- `data.to.vessel` is the vessel being boarded
- `data.to.vessel.persistentId` is stored as `pendingBoardingTargetPid` (existing code, line 1501)

In tree mode, we also need to check if this vessel is in the tree:
- Check `tree.BackgroundMap.ContainsKey(pendingBoardingTargetPid)` -- if yes, two-parent merge
- If not in BackgroundMap -- foreign vessel, single-parent merge

#### 4.3 Boarding vs. Docking Key Differences

| Aspect | Dock | Board |
|--------|------|-------|
| Detection event | `onPartCouple` | `onCrewBoardVessel` + `ChainToVesselPending` |
| Active vessel role | Can be target OR initiator | Always the EVA kerbal (absorbed) |
| PID change | Initiator PID changes; target PID unchanged | EVA PID destroyed; boarded vessel PID is the "merged" PID |
| Partner identification | Scan BackgroundMap or known PID | `pendingBoardingTargetPid` from event |
| Vessel destruction | Absorbed vessel destroyed by KSP | EVA kerbal vessel destroyed by KSP |
| Deferred processing | May need one-frame deferral | `ChainToVesselPending` already deferred |

#### 4.4 Board Merge -- Detailed Flow

**In `OnCrewBoardVessel` (modified for tree mode):**

```
if (activeTree != null):
    pendingBoardingTargetPid = data.to.vessel.persistentId
    pendingBoardingInTree = tree.BackgroundMap.ContainsKey(pendingBoardingTargetPid)
    boardingConfirmFrames = 0
    Log(...)
    return  // skip the activeChainId != null guard
```

Note: the existing handler has `if (activeChainId == null) return;` which would skip tree mode events where no chain is active. In tree mode, we always want to capture the boarding event. The handler must be modified to also proceed when `activeTree != null`.

**In `Update()` (tree board merge handler):**

When `activeTree != null && recorder != null && recorder.ChainToVesselPending && !recorder.IsRecording && recorder.CaptureAtStop != null`:

```
1. Confirm boarding: pendingBoardingTargetPid != 0 and FlightGlobals.ActiveVessel.persistentId == pendingBoardingTargetPid
2. mergedPid = pendingBoardingTargetPid
3. absorbedPid = recorder.RecordingVesselId  // EVA kerbal's PID
4. kerbalName = activeChainCrewName or extract from CaptureAtStop
5. partnerRecordingId = tree.BackgroundMap[mergedPid]  // may be null (foreign vessel)
6. CreateMergeBranch(BranchPointType.Board, mergedPid, absorbedPid, partnerRecordingId, kerbalName)
7. Clear: pendingBoardingTargetPid = 0, boardingConfirmFrames = 0, ChainToVesselPending = false
```

---

### 5. `CreateMergeBranch` Method

This is the core method that creates the merge branch point. It is the merge equivalent of `CreateSplitBranch`.

#### 5.1 `BuildMergeBranchData` -- Pure Data Model Method

```csharp
internal static (BranchPoint bp, RecordingStore.Recording mergedChild)
    BuildMergeBranchData(
        List<string> parentRecordingIds,  // 1 or 2 parent recording IDs
        string treeId,
        double mergeUT,
        BranchPointType branchType,       // Dock or Board
        uint mergedVesselPid,
        string mergedVesselName)
{
    string childId = Guid.NewGuid().ToString("N");
    string bpId = Guid.NewGuid().ToString("N");

    var bp = new BranchPoint
    {
        id = bpId,
        ut = mergeUT,
        type = branchType,
        parentRecordingIds = new List<string>(parentRecordingIds),
        childRecordingIds = new List<string> { childId }
    };

    var mergedChild = new RecordingStore.Recording
    {
        RecordingId = childId,
        TreeId = treeId,
        VesselPersistentId = mergedVesselPid,
        VesselName = mergedVesselName,
        ParentBranchPointId = bpId,
        ExplicitStartUT = mergeUT
    };

    return (bp, mergedChild);
}
```

**Key differences from `BuildSplitBranchData`**:
- Takes a list of parent IDs (1 or 2) instead of a single parent
- Returns only 1 child recording (not 2)
- Child is the merged vessel
- No EvaCrewName logic (not needed for merge child)

#### 5.2 `CreateMergeBranch` -- Runtime Method

```csharp
void CreateMergeBranch(
    BranchPointType branchType,
    uint mergedVesselPid,
    uint absorbedVesselPid,
    string activeParentRecordingId,      // from the active recorder (always present)
    string backgroundParentRecordingId,  // from BackgroundMap lookup (null for foreign vessel)
    double mergeUT,
    FlightRecorder stoppedRecorder)
{
    // 1. Build parent recording ID list
    var parentIds = new List<string>();
    parentIds.Add(activeParentRecordingId);
    if (backgroundParentRecordingId != null)
        parentIds.Add(backgroundParentRecordingId);

    // 2. Flush captured data from stopped recorder into the active parent recording
    // (Same pattern as CreateSplitBranch for subsequent splits)
    RecordingStore.Recording activeParentRec;
    if (activeTree.Recordings.TryGetValue(activeParentRecordingId, out activeParentRec))
    {
        if (stoppedRecorder.CaptureAtStop != null)
        {
            activeParentRec.Points.AddRange(stoppedRecorder.CaptureAtStop.Points);
            activeParentRec.OrbitSegments.AddRange(stoppedRecorder.CaptureAtStop.OrbitSegments);
            activeParentRec.PartEvents.AddRange(stoppedRecorder.CaptureAtStop.PartEvents);
            activeParentRec.PartEvents.Sort((a, b) => a.ut.CompareTo(b.ut));
        }
        activeParentRec.ExplicitEndUT = mergeUT;
        // Set terminal state based on merge type
        activeParentRec.TerminalStateValue = (branchType == BranchPointType.Dock)
            ? TerminalState.Docked : TerminalState.Boarded;
    }

    // 3. End background parent recording (if two-parent merge)
    if (backgroundParentRecordingId != null)
    {
        RecordingStore.Recording bgParentRec;
        if (activeTree.Recordings.TryGetValue(backgroundParentRecordingId, out bgParentRec))
        {
            bgParentRec.ExplicitEndUT = mergeUT;
            bgParentRec.TerminalStateValue = (branchType == BranchPointType.Dock)
                ? TerminalState.Docked : TerminalState.Boarded;
        }

        // Remove absorbed vessel from BackgroundMap and notify BackgroundRecorder
        backgroundRecorder?.OnVesselRemovedFromBackground(absorbedVesselPid);
        activeTree.BackgroundMap.Remove(absorbedVesselPid);
    }

    // 4. Get merged vessel name
    Vessel mergedVessel = FlightRecorder.FindVesselByPid(mergedVesselPid);
    string mergedVesselName = mergedVessel?.vesselName ?? "Merged Vessel";

    // 5. Build merge branch data
    var (bp, mergedChild) = BuildMergeBranchData(
        parentIds, activeTree.Id, mergeUT, branchType,
        mergedVesselPid, mergedVesselName);

    // 6. Take snapshot of merged vessel
    ConfigNode mergedSnapshot = (mergedVessel != null)
        ? VesselSpawner.TryBackupSnapshot(mergedVessel) : null;
    mergedChild.GhostVisualSnapshot = mergedSnapshot;
    mergedChild.VesselSnapshot = mergedSnapshot?.CreateCopy();

    // 7. Set ChildBranchPointId on all parent recordings
    if (activeParentRec != null)
        activeParentRec.ChildBranchPointId = bp.id;
    if (backgroundParentRecordingId != null)
    {
        RecordingStore.Recording bgParentRec2;
        if (activeTree.Recordings.TryGetValue(backgroundParentRecordingId, out bgParentRec2))
            bgParentRec2.ChildBranchPointId = bp.id;
    }

    // 8. Add to tree
    activeTree.BranchPoints.Add(bp);
    activeTree.Recordings[mergedChild.RecordingId] = mergedChild;

    // 9. Set active recording
    activeTree.ActiveRecordingId = mergedChild.RecordingId;

    // 10. Start new FlightRecorder for merged child
    recorder = new FlightRecorder();
    recorder.ActiveTree = activeTree;
    recorder.StartRecording(isPromotion: true);

    if (!recorder.IsRecording)
    {
        ParsekLog.Warn("Flight", "CreateMergeBranch: StartRecording failed for merged child");
        recorder = null;
    }

    // 11. Log
    ParsekLog.Info("Flight", $"Tree merge created: type={branchType}, " +
        $"bp={bp.id}, parents=[{string.Join(",", parentIds)}], " +
        $"child={mergedChild.RecordingId} (pid={mergedVesselPid})");
}
```

---

### 6. Docking Race Condition (`dockingInProgress`)

#### 6.1 The Problem

When vessel A docks with vessel B:
1. `onPartCouple` fires (Task 5 handles this)
2. KSP destroys vessel B's `Vessel` object
3. `onVesselWillDestroy` fires for B (same frame or next frame)
4. Task 6's deferred destruction check sees B gone from `FlightGlobals.Vessels` --> incorrectly marks as Destroyed

#### 6.2 The Fix

New field in `ParsekFlight`:
```csharp
private HashSet<uint> dockingInProgress = new HashSet<uint>();
```

**Set** in `OnPartCouple` (tree mode): `dockingInProgress.Add(absorbedPid);`

**Checked** by Task 6's destruction handler: skip any vessel PID in `dockingInProgress`.

**Cleared** after merge is processed: `dockingInProgress.Remove(absorbedPid);` in `CreateMergeBranch`.

**Also cleared** on scene change and flight ready (fail-safe): `dockingInProgress.Clear();`

#### 6.3 Interaction with `OnVesselWillDestroy`

The current `OnVesselWillDestroy` handler (line 731) calls `backgroundRecorder?.OnBackgroundVesselWillDestroy(v)`. This closes open orbit segments for the background vessel. For dock merges, `CreateMergeBranch` calls `backgroundRecorder?.OnVesselRemovedFromBackground(absorbedPid)` which performs similar cleanup. So there's a potential double-cleanup.

**Fix**: In `OnVesselWillDestroy`, check `dockingInProgress`:
```csharp
if (dockingInProgress.Contains(v.persistentId))
{
    // Skip background recorder cleanup -- merge handler will handle it
    return;
}
```

Wait, this would also skip `recorder?.OnVesselWillDestroy(v)` which is needed for part event tracking. Better approach: only skip the BackgroundRecorder notification:

```csharp
void OnVesselWillDestroy(Vessel v)
{
    recorder?.OnVesselWillDestroy(v);

    // Skip background recorder cleanup for vessels being absorbed by docking
    // The merge handler (CreateMergeBranch) will clean up via OnVesselRemovedFromBackground
    if (!dockingInProgress.Contains(v.persistentId))
        backgroundRecorder?.OnBackgroundVesselWillDestroy(v);

    // ... existing continuation cleanup ...
}
```

---

### 7. Foreign Vessel Docking/Boarding

When the docking partner or boarding target is NOT in the tree:

- `backgroundParentRecordingId` is null
- The `BranchPoint` has only 1 parent (the active recording)
- No background recording cleanup needed
- The child recording starts normally on the merged vessel

This is handled naturally by `CreateMergeBranch` -- it checks `if (backgroundParentRecordingId != null)` before processing the background parent.

---

### 8. `DockMergePending` Integration

#### 8.1 Tree Mode vs. Chain Mode

The existing `DockMergePending` flow was designed for chain mode. In tree mode, we need different behavior:

- **Chain mode** (no tree active): Use existing flow -- `DockMergePending` set by `OnPartCouple`, consumed by `Update()` which calls `CommitDockUndockSegment`, starts new recording in same chain
- **Tree mode**: New flow -- `OnPartCouple` stops recorder and sets `pendingTreeDockMerge`, consumed by `Update()` which calls `CreateMergeBranch`

**The `OnPhysicsFrame` DockMergePending guard** (FlightRecorder.cs, line 3424) forces the decision to `VesselSwitchDecision.DockMerge` when `DockMergePending` is true. This bypasses tree decisions. In tree mode, we don't set `DockMergePending` at all (we stop synchronously in `OnPartCouple`), so this guard is irrelevant. Good.

#### 8.2 Preventing Existing Chain Handlers from Firing

The existing dock chain handlers in `Update()` (lines 248-282) check:
- `recorder.DockMergePending && !recorder.IsRecording && CaptureAtStop != null`
- `pendingDockAsTarget && ...`

In tree mode, we must ensure these don't fire. Two approaches:
1. Add `activeTree == null` guards to existing handlers
2. Process tree merge BEFORE existing handlers and clear the pending state

**Approach 2 is cleaner**: Place the tree dock merge handler in `Update()` BEFORE the existing dock chain handlers. When the tree handler fires, it clears `pendingTreeDockMerge` and sets `recorder = null`, so the existing handlers' guards (`recorder != null`) fail.

But we also need to ensure `DockMergePending` is NOT set on the recorder in tree mode. In `OnPartCouple`, when `activeTree != null`, we skip setting `recorder.DockMergePending`. Instead we set `pendingTreeDockMerge`. This prevents the existing chain handler from matching.

---

### 9. `ChainToVessel` Integration

#### 9.1 Tree Mode vs. Chain Mode

Similarly to docking:
- **Chain mode**: `ChainToVesselPending` set by OnPhysicsFrame, consumed by `Update()` which checks `activeChainId` and boarding confirmation
- **Tree mode**: `ChainToVesselPending` still set by OnPhysicsFrame (it's in the decision priority chain before tree decisions). The tree handler in `Update()` intercepts it when `activeTree != null`

**Key difference**: `ChainToVessel` is ALWAYS returned by `DecideOnVesselSwitch` for EVA-to-vessel transitions (line 3899-3900), regardless of tree mode. This is correct because tree logic is checked AFTER chain logic in the priority chain. So `ChainToVesselPending` will be set even in tree mode.

#### 9.2 Tree Board Handler Placement

The tree board handler in `Update()` must be placed BEFORE the existing `ChainToVesselPending` handler (lines 339-373). When tree mode is active:
- Intercept `ChainToVesselPending`
- Create merge branch instead of chain segment
- Clear `ChainToVesselPending`

The existing handler's guard (`activeChainId != null && pendingBoardingTargetPid != 0`) provides some natural protection, but explicitly checking `activeTree != null` first is more robust.

---

### 10. Deferred Processing

#### 10.1 Does Merge Need Deferral?

**Docking**: No one-frame deferral needed (unlike splits). The reason splits need deferral is that new vessels take a frame to be fully initialized by KSP. For merges, the merged vessel already exists (it's the surviving vessel from the dock). `onPartCouple` fires after the coupling, so the merged vessel is already in its final state.

However, there's a subtle timing issue: the ghost visual snapshot should capture the merged vessel AFTER coupling completes (including all parts from both vessels). Since `onPartCouple` fires after coupling, the snapshot taken in `CreateMergeBranch` (called from `Update()`, one frame after `onPartCouple`) will correctly capture the combined vessel.

**Boarding**: Also no deferral needed beyond what already exists. `ChainToVesselPending` is already deferred through the `CaptureAtStop` mechanism. By the time `Update()` processes it, the vessel switch is complete and the boarding target vessel is the active vessel.

#### 10.2 Timing of `CreateMergeBranch`

`CreateMergeBranch` is called from `Update()`, which runs once per rendered frame. By this point:
- The recorder has been stopped (either synchronously in `OnPartCouple` or by OnPhysicsFrame)
- `CaptureAtStop` is available with the captured data
- The merged vessel exists and is accessible via `FlightGlobals.ActiveVessel`
- The absorbed vessel's `Vessel` object may or may not still exist (KSP destroys it asynchronously)

This timing is identical to how the existing dock chain handlers work, so it's proven safe.

---

### 11. Child Recording Creation

The child recording of a merge represents the merged vessel going forward. It is created by `BuildMergeBranchData` with:
- `VesselPersistentId = mergedVesselPid` (the surviving vessel's PID)
- `VesselName` from the surviving vessel
- `ParentBranchPointId` pointing to the merge branch point
- `ExplicitStartUT = mergeUT`
- `GhostVisualSnapshot` captured from the merged vessel (includes parts from both vessels)

A new `FlightRecorder` is created and started with `isPromotion: true` (same as split branch creation). This skips pre-launch resource capture and the "Recording STARTED" screen message.

---

### 12. BackgroundRecorder Cleanup

When a merge occurs with a background vessel partner:

1. `backgroundRecorder.OnVesselRemovedFromBackground(absorbedPid)` -- closes orbit segments, removes tracking state
2. `activeTree.BackgroundMap.Remove(absorbedPid)` -- removes from background map
3. `dockingInProgress.Remove(absorbedPid)` -- removes from race condition guard

For boarding, the EVA kerbal's vessel is destroyed by KSP. If the kerbal was the active recording, its data is already in `CaptureAtStop`. If the boarding target was a background vessel:
- The target vessel remains alive (it's the merged vessel)
- It transitions from background to active via `CreateMergeBranch`
- `backgroundRecorder.OnVesselRemovedFromBackground(targetPid)` is called
- `activeTree.BackgroundMap.Remove(targetPid)` removes it from background

Wait -- for boarding, the kerbal is always the active vessel (EVA recording). The boarding target is the vessel being boarded. If the target is in the tree's BackgroundMap, it's a two-parent merge. The target's background recording ends (terminal state = Boarded), and the child recording starts on the same vessel PID. The target vessel doesn't disappear -- it gets the kerbal aboard and continues as the child recording.

So for board merges:
- Kerbal recording (active) ends with `TerminalState.Boarded`
- Target vessel recording (background) ends with `TerminalState.Boarded`
- Child recording starts on the target vessel PID
- `BackgroundMap` removes the target vessel PID
- `BackgroundRecorder` cleans up the target vessel's tracking state

---

### 13. `OnCrewBoardVessel` Modification

The existing handler at line 1488 has:
```csharp
if (activeChainId == null)
{
    ParsekLog.Verbose("Flight", "OnCrewBoardVessel: no active chain -- ignoring");
    return;
}
```

This guard would prevent tree mode boarding detection (tree mode doesn't use chains). Modify to:

```csharp
if (activeChainId == null && activeTree == null)
{
    ParsekLog.Verbose("Flight", "OnCrewBoardVessel: no active chain or tree -- ignoring");
    return;
}
```

This allows the handler to record `pendingBoardingTargetPid` when a tree is active, even without an active chain.

Additionally, store whether the boarding target is in the tree:
```csharp
pendingBoardingTargetInTree = (activeTree != null &&
    activeTree.BackgroundMap.ContainsKey(data.to.vessel.persistentId));
```

---

### 14. New State Fields

Add to `ParsekFlight`:

```csharp
// Merge event detection (tree mode)
private bool pendingTreeDockMerge;           // true when a tree dock merge is pending
private uint pendingDockAbsorbedPid;         // PID of absorbed vessel in dock merge
private bool pendingBoardingTargetInTree;    // true if boarding target is in the tree

// Docking race condition guard (Task 5 sets, Task 6 checks)
private HashSet<uint> dockingInProgress = new HashSet<uint>();
```

Note: `pendingDockMergedPid` (line 92) already exists and will be reused.

---

### 15. `OnPartCouple` Modification (Complete)

```csharp
void OnPartCouple(GameEvents.FromToAction<Part, Part> data)
{
    if (data.to?.vessel == null) return;
    uint mergedPid = data.to.vessel.persistentId;

    // --- TREE MODE: create merge branch instead of chain segment ---
    if (activeTree != null && recorder != null)
    {
        if (recorder.IsRecording)
        {
            bool isTarget = (recorder.RecordingVesselId == mergedPid);
            uint absorbedPid;

            if (isTarget)
            {
                // We are the TARGET -- find the absorbed partner
                absorbedPid = FindAbsorbedDockPartnerPid(mergedPid);
            }
            else
            {
                // We are the INITIATOR -- we are being absorbed
                absorbedPid = recorder.RecordingVesselId;
            }

            // Stop recorder synchronously
            recorder.StopRecordingForChainBoundary();

            // Set pending state for tree merge
            pendingTreeDockMerge = true;
            pendingDockMergedPid = mergedPid;
            pendingDockAbsorbedPid = absorbedPid;
            dockConfirmFrames = 0;

            // Race condition guard: prevent Task 6 from misclassifying absorption as destruction
            if (absorbedPid != 0)
                dockingInProgress.Add(absorbedPid);

            Log($"onPartCouple (tree): dock merge pending " +
                $"(merged={mergedPid}, absorbed={absorbedPid}, isTarget={isTarget})");
            return;
        }
        else if (!recorder.IsRecording && recorder.CaptureAtStop != null)
        {
            // OnPhysicsFrame already stopped us (initiator, late event)
            uint absorbedPid = recorder.RecordingVesselId;

            pendingTreeDockMerge = true;
            pendingDockMergedPid = mergedPid;
            pendingDockAbsorbedPid = absorbedPid;
            dockConfirmFrames = 0;

            // Clear DockMergePending to prevent chain handler
            recorder.DockMergePending = false;

            if (absorbedPid != 0)
                dockingInProgress.Add(absorbedPid);

            Log($"onPartCouple (tree, retroactive): dock merge pending " +
                $"(merged={mergedPid}, absorbed={absorbedPid})");
            return;
        }
        // else: recorder exists but in some other state -- fall through to legacy
    }

    // --- LEGACY (non-tree) chain mode: unchanged ---
    if (recorder != null && recorder.IsRecording)
    {
        if (mergedPid == recorder.RecordingVesselId)
        {
            pendingDockAsTarget = true;
            pendingDockMergedPid = mergedPid;
            dockConfirmFrames = 0;
            recorder.StopRecordingForChainBoundary();
            Log($"onPartCouple: target dock detected (mergedPid={mergedPid})");
        }
        else
        {
            recorder.DockMergePending = true;
            pendingDockMergedPid = mergedPid;
            dockConfirmFrames = 0;
            Log($"onPartCouple: initiator dock detected (mergedPid={mergedPid})");
        }
    }
    else if (recorder != null && !recorder.IsRecording && recorder.CaptureAtStop != null)
    {
        recorder.DockMergePending = true;
        pendingDockMergedPid = mergedPid;
        dockConfirmFrames = 0;
        Log($"onPartCouple: retroactive initiator dock (mergedPid={mergedPid})");
    }
}
```

#### 15.1 `FindAbsorbedDockPartnerPid` Helper

```csharp
/// <summary>
/// When we are the dock target (our PID unchanged), finds the PID of the vessel
/// that was absorbed into us. Scans BackgroundMap for a vessel that is no longer
/// a separate entity (its Vessel object is gone or merged into ours).
/// Returns 0 if the partner is not in the tree (foreign vessel).
/// </summary>
uint FindAbsorbedDockPartnerPid(uint mergedPid)
{
    if (activeTree == null) return 0;

    foreach (var kvp in activeTree.BackgroundMap)
    {
        uint bgPid = kvp.Key;
        if (bgPid == mergedPid) continue; // skip ourselves

        Vessel bgVessel = FlightRecorder.FindVesselByPid(bgPid);

        // The absorbed vessel's Vessel object either doesn't exist anymore,
        // or has been reassigned to the merged vessel (0 parts, about to be destroyed)
        if (bgVessel == null || bgVessel.parts == null || bgVessel.parts.Count == 0)
        {
            return bgPid;
        }
    }

    // No background vessel was absorbed -- partner is a foreign vessel
    return 0;
}
```

**Note**: This scan runs synchronously in `OnPartCouple`. At this point, the coupling has already happened, so the absorbed vessel's parts have been reparented. The scan should find exactly one background vessel that has been emptied/destroyed.

**Edge case**: If two background vessels are in the process of being destroyed simultaneously (unlikely), this could match the wrong one. In practice, only one vessel is absorbed per dock event, so this is safe.

---

### 16. `Update()` Modifications

#### 16.1 Tree Dock Merge Handler

Add BEFORE the existing dock chain handlers (lines 248-282):

```csharp
// Tree mode: dock merge -- create merge branch point
if (activeTree != null && pendingTreeDockMerge && recorder != null
    && !recorder.IsRecording && recorder.CaptureAtStop != null)
{
    double mergeUT = Planetarium.GetUniversalTime();
    if (recorder.CaptureAtStop.Points.Count > 0)
        mergeUT = recorder.CaptureAtStop.Points[recorder.CaptureAtStop.Points.Count - 1].ut;

    // Determine parent recording IDs
    string activeParentId = activeTree.ActiveRecordingId;
    string bgParentId = null;
    if (pendingDockAbsorbedPid != 0)
        activeTree.BackgroundMap.TryGetValue(pendingDockAbsorbedPid, out bgParentId);

    CreateMergeBranch(
        BranchPointType.Dock,
        pendingDockMergedPid,
        pendingDockAbsorbedPid,
        activeParentId,
        bgParentId,
        mergeUT,
        recorder);

    // Clean up
    if (pendingDockAbsorbedPid != 0)
        dockingInProgress.Remove(pendingDockAbsorbedPid);

    recorder = null;  // CreateMergeBranch created a new recorder
    pendingTreeDockMerge = false;
    pendingDockMergedPid = 0;
    pendingDockAbsorbedPid = 0;
    dockConfirmFrames = 0;
    pendingDockAsTarget = false;

    Log("Tree dock merge completed");
}
```

Wait -- there's an issue. `CreateMergeBranch` creates a new recorder and assigns it to `this.recorder`. But then we set `recorder = null` after the call. That would null the newly created recorder. The fix: `CreateMergeBranch` sets `this.recorder` internally (same pattern as `CreateSplitBranch`). We should NOT null it after the call. Instead, only null the `stoppedRecorder` reference.

Actually, looking at `CreateSplitBranch` (line 1166-1167): it creates a new recorder and assigns to `this.recorder`. The old `pendingSplitRecorder` is consumed inside the method. After `CreateSplitBranch` returns, `this.recorder` is the new recorder. So the correct pattern for `CreateMergeBranch`:

```csharp
// In Update(), before CreateMergeBranch:
var stoppedRecorder = recorder;
recorder = null;  // clear so CreateMergeBranch can set the new one

CreateMergeBranch(
    BranchPointType.Dock,
    pendingDockMergedPid,
    pendingDockAbsorbedPid,
    activeParentId,
    bgParentId,
    mergeUT,
    stoppedRecorder);

// recorder is now the new recorder set by CreateMergeBranch
// (or null if StartRecording failed)
```

#### 16.2 Tree Board Merge Handler

Add BEFORE the existing `ChainToVesselPending` handler (lines 339-373):

```csharp
// Tree mode: board merge -- create merge branch point
if (activeTree != null && recorder != null && recorder.ChainToVesselPending
    && !recorder.IsRecording && recorder.CaptureAtStop != null
    && pendingBoardingTargetPid != 0
    && FlightGlobals.ActiveVessel != null
    && FlightGlobals.ActiveVessel.persistentId == pendingBoardingTargetPid)
{
    double mergeUT = Planetarium.GetUniversalTime();
    if (recorder.CaptureAtStop.Points.Count > 0)
        mergeUT = recorder.CaptureAtStop.Points[recorder.CaptureAtStop.Points.Count - 1].ut;

    uint mergedPid = pendingBoardingTargetPid;
    uint absorbedPid = recorder.RecordingVesselId;  // EVA kerbal's PID

    string activeParentId = activeTree.ActiveRecordingId;
    string bgParentId = null;
    if (pendingBoardingTargetInTree)
        activeTree.BackgroundMap.TryGetValue(mergedPid, out bgParentId);

    var stoppedRecorder = recorder;
    recorder = null;

    CreateMergeBranch(
        BranchPointType.Board,
        mergedPid,
        absorbedPid,
        activeParentId,
        bgParentId,
        mergeUT,
        stoppedRecorder);

    pendingBoardingTargetPid = 0;
    boardingConfirmFrames = 0;
    pendingBoardingTargetInTree = false;
    activeChainCrewName = null;

    Log("Tree board merge completed");
}
```

**Note about boarding direction**: For board merges, the "absorbed" entity is the EVA kerbal (its vessel is destroyed by KSP). The "merged" vessel is the boarding target. The active recording was the kerbal, and the background recording (if any) was the target vessel. So:
- `activeParentId` = kerbal's recording (the one being actively recorded)
- `bgParentId` = target vessel's recording (from BackgroundMap, if in tree)
- `mergedPid` = target vessel's PID (the vessel that continues)

The `CreateMergeBranch` method's `absorbedVesselPid` parameter is the kerbal's PID, which is not used for BackgroundMap cleanup (the kerbal wasn't in BackgroundMap -- it was the active recording). The target vessel IS in BackgroundMap and needs to be removed. So `CreateMergeBranch` needs to handle this case:

For board merges, the vessel being removed from BackgroundMap is the MERGED vessel (the boarding target), not the absorbed vessel (the kerbal). This is different from dock merges where the absorbed vessel is removed.

**Revised `CreateMergeBranch`**: Handle both cases explicitly.

For Dock: remove `absorbedVesselPid` from BackgroundMap
For Board: remove `mergedVesselPid` from BackgroundMap (since the boarding target transitions from background to active child)

Actually, let me reconsider. For board merges with a background target:
- The kerbal was the ACTIVE recording
- The boarding target was in BackgroundMap
- The merged vessel continues with the target's PID
- The target's background recording should end (TerminalState.Boarded)
- The target should be removed from BackgroundMap
- A new child recording starts on the target's PID

So `CreateMergeBranch` should remove the background parent from the BackgroundMap regardless of merge type. The background parent's PID needs to be determined from the recording's VesselPersistentId, not from the absorbedPid parameter.

Let me redesign `CreateMergeBranch` to be more explicit:

```csharp
void CreateMergeBranch(
    BranchPointType branchType,
    uint mergedVesselPid,           // PID of the vessel that continues
    string activeParentRecordingId, // always present (from active recorder)
    string bgParentRecordingId,     // from BackgroundMap (null for foreign vessel)
    double mergeUT,
    FlightRecorder stoppedRecorder)
```

Remove `absorbedVesselPid` as a parameter. Instead, derive the background vessel's PID from the recording's `VesselPersistentId`. This simplifies the interface.

---

### 17. `pendingSplitInProgress` Interaction

During a split (Task 4), `pendingSplitInProgress` is true. If a dock/board event fires during a pending split, it should be ignored (the split must complete first).

Add guard in `OnPartCouple` (tree mode):
```csharp
if (pendingSplitInProgress) return;
```

Similarly for `OnCrewBoardVessel` (tree mode):
```csharp
if (pendingSplitInProgress) return;
```

This matches the existing pattern in `OnPartUndock` (line 1825) and `OnCrewOnEva` (line 1710).

---

### 18. First Merge Without Prior Split

What happens if the first merge event occurs before any split? Example: player launches, EVA kerbal goes out (split creates tree), kerbal boards back (merge).

In this case, `activeTree != null` (created by the EVA split). The active recording is the kerbal's EVA recording. The vessel recording is in BackgroundMap. The merge creates a BranchPoint(type=Board) with two parents and one child. This works correctly with the current design.

What if docking happens with NO tree active? (Player docks with a random vessel while recording standalone.) In this case, `activeTree == null`, and the existing chain logic handles it. No tree merge is created.

**Can a merge be the FIRST tree event?** No. A merge requires at least one vessel to be in the tree (the active recording). A tree is created by `CreateSplitBranch` on the first split. Without a prior split, there's no tree. So the first tree event is always a split, and merges only happen after at least one split.

---

### 19. Unit Tests

New file: `Source/Parsek.Tests/MergeEventDetectionTests.cs`

#### 19.1 `BuildMergeBranchData` Tests

```
1. BuildMergeBranchData_Dock_TwoParents_CreatesCorrectBranchPoint
   - Two parent IDs, type=Dock, verify BP has 2 parents, 1 child

2. BuildMergeBranchData_Dock_SingleParent_CreatesCorrectBranchPoint
   - One parent ID (foreign vessel), type=Dock, verify BP has 1 parent, 1 child

3. BuildMergeBranchData_Board_TwoParents_CreatesCorrectBranchPoint
   - Two parent IDs, type=Board, verify BP has 2 parents, 1 child

4. BuildMergeBranchData_Dock_ChildRecordingHasCorrectFields
   - Verify child has correct TreeId, VesselPersistentId, VesselName, ParentBranchPointId, ExplicitStartUT

5. BuildMergeBranchData_GeneratesUniqueIds
   - Two calls produce different BP IDs and child IDs

6. BuildMergeBranchData_BpIdMatchesChildParentBranchPointId
   - bp.id == child.ParentBranchPointId

7. BuildMergeBranchData_ChildIdMatchesBpChildRecordingIds
   - bp.childRecordingIds[0] == child.RecordingId
```

#### 19.2 BranchPoint Serialization Tests (Merge-Specific)

```
8. BranchPoint_Dock_TwoParents_SerializesRoundTrip
   - Create BP with type=Dock, 2 parents, 1 child. Save/load. Verify all fields.

9. BranchPoint_Board_SingleParent_SerializesRoundTrip
   - Create BP with type=Board, 1 parent, 1 child. Save/load. Verify.
```

#### 19.3 RebuildBackgroundMap Tests (Merge-Specific)

```
10. RebuildBackgroundMap_ExcludesDockedRecordings
    - Recording with TerminalState.Docked should NOT appear in BackgroundMap

11. RebuildBackgroundMap_ExcludesBoardedRecordings
    - Recording with TerminalState.Boarded should NOT appear in BackgroundMap

12. RebuildBackgroundMap_ExcludesRecordingsWithChildBranchPoint
    - Recording with ChildBranchPointId set should NOT appear in BackgroundMap

13. RebuildBackgroundMap_IncludesActiveRecordingsWithNoTerminalState
    - Recording with null TerminalState and no ChildBranchPointId appears in BackgroundMap
```

#### 19.4 DecideOnVesselSwitch Tests (Boarding in Tree Mode)

```
14. DecideOnVesselSwitch_EvaToVessel_ReturnsChainToVessel_EvenInTreeMode
    - Verify that ChainToVessel takes priority over tree decisions
```

#### 19.5 FindAbsorbedDockPartnerPid Tests

This method requires Unity for `FindVesselByPid`, so it cannot be unit-tested directly. Test coverage comes from in-game tests.

---

### 20. In-Game Test Scenarios

#### 20.1 Dock Merge -- Active Vessel Docks with Background Vessel

1. Launch a vessel with a docking port and a probe core
2. EVA kerbal (creates tree with split)
3. Board kerbal back (creates merge -- tests board merge too)
4. Undock the probe section (creates split)
5. Switch to probe via `]` (background transition)
6. Fly probe back and dock with main vessel
7. Verify: merge branch point created (type=Dock), two parent recordings ended (TerminalState.Docked), child recording started on merged vessel
8. Revert -- verify tree structure in logs

#### 20.2 Dock Merge -- Active Vessel Initiator

1. Launch vessel A with docking port
2. Undock probe (creates tree)
3. Stay on main vessel (active), approach probe (background)
4. Dock with probe
5. Verify: initiator dock detected, merge branch created

#### 20.3 Dock Merge -- Active Vessel Target

1. Same setup as 20.2 but switch to probe after undock
2. Fly probe toward main vessel (background)
3. Dock -- probe is absorbed into main vessel
4. Verify: target dock detected, merge branch created

#### 20.4 Board Merge -- EVA Kerbal Boards Tree Vessel

1. Launch crewed vessel
2. EVA kerbal (creates tree)
3. Board kerbal back onto vessel
4. Verify: board merge created (type=Board), kerbal recording ended (TerminalState.Boarded), vessel background recording ended (TerminalState.Boarded), child recording started

#### 20.5 Board Merge -- EVA Kerbal Boards Foreign Vessel

1. Launch vessel A, record, revert, merge (spawns vessel A in world)
2. Launch vessel B, EVA kerbal from B (creates tree)
3. Board kerbal onto spawned vessel A (foreign vessel -- not in current tree)
4. Verify: single-parent merge created (only kerbal recording as parent)

#### 20.6 Dock Merge -- Foreign Vessel Docking

1. Launch vessel A, record, revert, merge (spawns vessel A)
2. Launch vessel B, approach vessel A, dock
3. Verify: dock merge with single parent (vessel B recording only, vessel A is foreign)

#### 20.7 Docking Race Condition

1. Undock probe (creates tree with two branches)
2. Dock probe back (merge)
3. Verify: probe's background recording shows TerminalState.Docked (NOT Destroyed)
4. Check logs: no false destruction logged for the absorbed vessel

#### 20.8 Rapid Undock-Dock Cycle

1. Undock probe, immediately dock again (within seconds)
2. Verify: split branch point, then merge branch point, correct tree structure

---

### 21. Files Modified/Created

| File | Changes |
|------|---------|
| `Source/Parsek/ParsekFlight.cs` | `OnPartCouple` tree mode interception; `OnCrewBoardVessel` tree mode guard; `Update()` tree dock/board merge handlers (before existing chain handlers); `OnVesselWillDestroy` docking race condition guard; `CreateMergeBranch` method; `FindAbsorbedDockPartnerPid` method; `BuildMergeBranchData` static method; new fields (`pendingTreeDockMerge`, `pendingDockAbsorbedPid`, `pendingBoardingTargetInTree`, `dockingInProgress`); `OnSceneChangeRequested` / `OnFlightReady` cleanup for new fields |
| `Source/Parsek.Tests/MergeEventDetectionTests.cs` | **New file** -- unit tests for `BuildMergeBranchData`, BranchPoint serialization round-trips, BackgroundMap rebuild exclusion of Docked/Boarded recordings |

**Files NOT modified** (no changes needed):
- `FlightRecorder.cs` -- `ChainToVesselPending` and `DockMergePending` already exist; `DecideOnVesselSwitch` already returns correct values; `StopRecordingForChainBoundary` already works for merges
- `RecordingTree.cs` -- serialization already handles 2-parent BranchPoints and TerminalState
- `BranchPoint.cs` -- `Dock` and `Board` types already exist
- `BackgroundRecorder.cs` -- `OnVesselRemovedFromBackground` already works for merge cleanup
- `RecordingStore.cs` -- `TerminalState.Docked` and `TerminalState.Boarded` already exist

---

### 22. Implementation Order

1. **Add state fields** -- `pendingTreeDockMerge`, `pendingDockAbsorbedPid`, `pendingBoardingTargetInTree`, `dockingInProgress` to ParsekFlight
2. **Add `BuildMergeBranchData`** -- pure static method, immediately testable
3. **Write unit tests for `BuildMergeBranchData`** -- verify data model correctness
4. **Add `FindAbsorbedDockPartnerPid`** helper
5. **Add `CreateMergeBranch`** -- runtime method (follows `CreateSplitBranch` pattern)
6. **Modify `OnPartCouple`** -- add tree mode interception before existing chain logic
7. **Modify `OnCrewBoardVessel`** -- add `activeTree != null` to the guard
8. **Add tree dock merge handler** in `Update()` (before existing dock chain handlers)
9. **Add tree board merge handler** in `Update()` (before existing ChainToVessel handler)
10. **Modify `OnVesselWillDestroy`** -- add `dockingInProgress` guard for BackgroundRecorder
11. **Add cleanup** to `OnSceneChangeRequested` and `OnFlightReady`
12. **Write remaining unit tests** -- serialization round-trips, BackgroundMap exclusion
13. **Run `dotnet test`** -- all existing + new tests pass
14. **Build and in-game test** -- run through scenarios 20.1 through 20.8

---

### 23. Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| `FindAbsorbedDockPartnerPid` scan fails (absorbed vessel not yet removed from FlightGlobals) | Low | Medium -- single-parent merge instead of two-parent | Log warning, proceed with single-parent merge. The background vessel's recording still ends via BackgroundRecorder cleanup when its Vessel is destroyed. |
| `onPartCouple` fires before KSP reparents parts (absorbed vessel still has parts) | Medium | Medium -- scan would not find the absorbed vessel | Alternative: track all loaded background vessel PIDs BEFORE docking in a separate structure (e.g., on `OnVesselGoOffRails` for background vessels entering physics range). If the scan fails, fall back to single-parent merge. |
| `OnPhysicsFrame` detects PID change before `onPartCouple` fires (initiator, late event) | Known | None -- handled by retroactive initiator path in `OnPartCouple` (sets `pendingTreeDockMerge`, clears `DockMergePending`) |
| Existing dock chain handler fires before tree merge handler | Medium | High -- chain commit instead of tree merge | Place tree merge handler FIRST in `Update()`, set `pendingTreeDockMerge` flag, and do NOT set `recorder.DockMergePending` in tree mode. Chain handler guards on `DockMergePending` will fail. |
| Double-cleanup: `OnVesselWillDestroy` + `CreateMergeBranch` both clean up background vessel | Medium | Low -- BackgroundRecorder methods are idempotent | `dockingInProgress` guard in `OnVesselWillDestroy` skips BackgroundRecorder cleanup for dock-absorbed vessels. |
| Boarding without prior EVA split (no tree exists) | None -- by design | None | Guard: `activeTree == null` in all tree handlers. Boarding without a tree uses existing chain logic. |
| Rapid split-then-merge (undock + immediate dock) | Medium | Medium -- `pendingSplitInProgress` might block dock detection | `pendingSplitInProgress` guard in `OnPartCouple` returns early. The split must complete (deferred coroutine yields one frame) before the dock event can be processed. If dock happens during the same frame as split, the dock event is lost. Mitigation: log warning. In practice, docking requires vessel approach time (seconds), so this is extremely unlikely. |
| Thread/timing: `dockingInProgress` checked by Task 6 concurrently | None | None | All code runs on the Unity main thread. No threading issues. |
| Scene change during pending merge | Low | Low -- data loss for the merge | `OnSceneChangeRequested` clears `pendingTreeDockMerge` and `dockingInProgress`. The stopped recorder's data is handled by existing scene-change logic (fallback stash). |
| Backward compatibility -- non-tree recordings | None | None | All tree merge logic is gated by `activeTree != null`. Existing chain logic is unchanged. |

---

### 24. Deferred Design Questions (Answered)

**Q: When `onPartCouple` fires, which vessel survives and which is absorbed?**
A: `data.to.vessel` is the surviving (dominant) vessel. `data.from`'s original vessel is absorbed. After coupling, `data.from.vessel` may equal `data.to.vessel` (reparented). The surviving vessel's PID is `data.to.vessel.persistentId`.

**Q: Does the absorbed vessel get destroyed immediately, or after a frame?**
A: KSP destroys the absorbed vessel's `Vessel` object shortly after coupling (same frame or next frame). `onVesselWillDestroy` fires for it. The timing is fast enough that we need the `dockingInProgress` guard.

**Q: How does `DockMergePending` currently work end-to-end?**
A: See Section 2.2 above. In tree mode, we bypass `DockMergePending` entirely by stopping the recorder synchronously in `OnPartCouple` and using `pendingTreeDockMerge` instead.

**Q: Is there a `GameEvents.onCrewBoardVessel` event?**
A: Yes. `GameEvents.onCrewBoardVessel` (subscribed at line 186, handler at line 1488). It fires with `FromToAction<Part, Part>` where `data.from` is the EVA part and `data.to` is the vessel part.

**Q: What happens to the absorbed vessel's recording data?**
A: If the absorbed vessel is in the tree's BackgroundMap, its recording has been maintained by BackgroundRecorder (orbit segments, trajectory points for loaded vessels). This data is already in the tree's Recordings dict. `CreateMergeBranch` ends the recording by setting `ExplicitEndUT` and `TerminalStateValue`, then removes it from BackgroundMap and notifies BackgroundRecorder.

**Q: Can two tree vessels dock? Can a tree vessel dock with a non-tree vessel?**
A: Yes to both. Two tree vessels docking: both recordings are parents of the merge (two-parent merge). Tree vessel docking with non-tree vessel: single-parent merge (only the tree vessel's recording is a parent). The non-tree vessel's parts are absorbed into the merged vessel but have no recording history in this tree.

---

## Orchestrator Review Fixes

The following fixes MUST be applied during implementation. They address issues found by the plan review and verified by the orchestrator against the actual codebase.

### Fix 1 (CRITICAL): Guard `OnVesselSwitchComplete` Against Dock/Board Merge Interference

**Problem**: `OnVesselSwitchComplete` (line 769) fires for ALL vessel switches, including dock-induced switches. When `onPartCouple` fires and we stop the recorder + set `pendingTreeDockMerge = true`, KSP may trigger a vessel switch (e.g., if the player's vessel was absorbed). `OnVesselSwitchComplete` would then promote the merged vessel from BackgroundMap BEFORE `Update()` runs the tree dock merge handler. This would corrupt the merge: the background parent's recording would be removed from BackgroundMap prematurely, causing `CreateMergeBranch` to produce a single-parent merge instead of two-parent.

Same issue for board merges: when `ChainToVesselPending` is set and `activeTree != null`, the vessel switch is part of the boarding flow.

**Fix**: Add guards at the top of `OnVesselSwitchComplete`, after the existing `if (activeTree == null) return;` check:

```csharp
// Don't interfere with pending merge processing
if (pendingTreeDockMerge) return;

// Don't promote during pending board merge - the board handler in Update() owns this
if (recorder != null && recorder.ChainToVesselPending) return;
```

### Fix 2 (CRITICAL): Replace `FindAbsorbedDockPartnerPid` Scan with Robust Identification

**Problem**: The plan's `FindAbsorbedDockPartnerPid` scans BackgroundMap for vessels with null/0-part `Vessel` objects. At `onPartCouple` time, the absorbed vessel may still be alive (parts reparented but `Vessel` object not yet destroyed). The scan `bgVessel.parts.Count == 0` is fragile - KSP may not have cleaned up parts yet.

**Fix**: Pass `data.to.vessel` (the merged/surviving Vessel reference) to `FindAbsorbedDockPartnerPid`. After coupling, `data.from.vessel == data.to.vessel` (the part was reparented). So any background vessel whose `Vessel` object IS the merged vessel OR is null is the absorbed partner. Also add a fallback check: if the background vessel's `Vessel` object still exists but equals the merged vessel (i.e., the vessel reference was updated to point to the surviving vessel after reparenting), it's the absorbed partner.

Updated signature and body:

```csharp
uint FindAbsorbedDockPartnerPid(uint mergedPid, Vessel mergedVessel)
{
    if (activeTree == null) return 0;

    foreach (var kvp in activeTree.BackgroundMap)
    {
        uint bgPid = kvp.Key;
        if (bgPid == mergedPid) continue;

        Vessel bgVessel = FlightRecorder.FindVesselByPid(bgPid);

        // After coupling, the absorbed vessel either:
        // (a) has its Vessel object destroyed (null)
        // (b) has its Vessel reference pointing to the merged vessel (reparented parts)
        // (c) still exists but with 0 parts (about to be destroyed)
        if (bgVessel == null || bgVessel == mergedVessel
            || bgVessel.parts == null || bgVessel.parts.Count == 0)
        {
            return bgPid;
        }
    }

    return 0; // partner not in tree (foreign vessel)
}
```

Update the call site in `OnPartCouple`:
```csharp
absorbedPid = FindAbsorbedDockPartnerPid(mergedPid, data.to.vessel);
```

### Fix 3 (IMPORTANT): Board Merge BackgroundMap Cleanup Uses Wrong PID

**Problem**: In section 5.2, `CreateMergeBranch` calls `backgroundRecorder?.OnVesselRemovedFromBackground(absorbedVesselPid)` and `activeTree.BackgroundMap.Remove(absorbedVesselPid)`. For dock merges, the absorbed vessel is in BackgroundMap - correct. But for board merges, the absorbed entity is the EVA kerbal (active recorder), NOT in BackgroundMap. The boarding TARGET vessel is in BackgroundMap and needs to be removed.

**Fix**: Remove `absorbedVesselPid` parameter from `CreateMergeBranch`. Instead, derive the background vessel PID from the background parent recording:

```csharp
// In CreateMergeBranch, when removing from BackgroundMap:
if (bgParentRecordingId != null)
{
    RecordingStore.Recording bgParentRec;
    if (activeTree.Recordings.TryGetValue(bgParentRecordingId, out bgParentRec))
    {
        uint bgVesselPid = bgParentRec.VesselPersistentId;
        backgroundRecorder?.OnVesselRemovedFromBackground(bgVesselPid);
        activeTree.BackgroundMap.Remove(bgVesselPid);
    }
}
```

This correctly handles both cases:
- **Dock merge**: background parent is the absorbed vessel → removes absorbed PID
- **Board merge**: background parent is the boarding target → removes target PID

### Fix 4 (IMPORTANT): `dockingInProgress` Guard Scope

**Problem**: The `dockingInProgress` set is used to prevent Task 6's destruction detection from misclassifying dock-absorbed vessels as destroyed. For board merges, the EVA kerbal's vessel is also destroyed by KSP, but it's the ACTIVE recording (not background). The EVA kerbal PID should also be added to `dockingInProgress` to prevent a false destruction event for the kerbal vessel.

Wait - actually, for board merges the kerbal vessel destruction is expected and the kerbal's recording is ended with `TerminalState.Boarded` by `CreateMergeBranch`. Task 6 would check background vessels, not the active recorder's vessel. So `dockingInProgress` is only needed for dock merges where the absorbed vessel is a BACKGROUND vessel being destroyed by KSP.

For board merges: the boarding target (background) transitions to child recording - it's NOT destroyed. The kerbal vessel IS destroyed, but its recording was the active one (already ended). So no `dockingInProgress` is needed for board merges.

**No code change needed** - the current design is correct. But add a comment in the plan to clarify this reasoning.

### Fix 5 (IMPORTANT): `CreateMergeBranch` Must Clear `ChainToVesselPending` for Board Merges

**Problem**: For board merges, `OnPhysicsFrame` sets `ChainToVesselPending = true` on the recorder. The tree board handler in `Update()` calls `CreateMergeBranch` which creates a NEW recorder. But the old recorder's `ChainToVesselPending` flag is on the OLD recorder object (passed as `stoppedRecorder`). Since `CreateMergeBranch` creates a brand new recorder, there's no leak. However, the tree board handler should explicitly clear `ChainToVesselPending` on the stopped recorder before `CreateMergeBranch` to prevent any edge case where the old recorder reference is held elsewhere.

**Fix**: In the `Update()` tree board merge handler, clear the flag:
```csharp
recorder.ChainToVesselPending = false;
var stoppedRecorder = recorder;
recorder = null;
CreateMergeBranch(...);
```

### Fix 6 (IMPORTANT): `OnPartCouple` State 3 (Retroactive Initiator) - `RecordingVesselId` After Stop

**Problem**: In State 3 (section 3.3, line 367), the plan uses `recorder.RecordingVesselId` as the absorbed PID. But after `OnPhysicsFrame` stops the recorder via the `DockMerge` path, does `RecordingVesselId` still hold the OLD PID? Let me check.

Looking at `FlightRecorder` - `RecordingVesselId` is set at `StartRecording()` time and never changes. `CaptureAtStop` snapshots the data. `OnPhysicsFrame` detects the PID mismatch and creates `CaptureAtStop` with the data from BEFORE the PID change. So `recorder.RecordingVesselId` still holds the OLD PID (the absorbed vessel's PID). This is correct.

**No code change needed** - confirmed correct.

### Fix 7 (IMPORTANT): Scene Change and FlightReady Cleanup

**Problem**: The plan mentions cleanup in `OnSceneChangeRequested` and `OnFlightReady` but doesn't list all new fields.

**Fix**: Ensure ALL new fields are cleaned up:
- `OnSceneChangeRequested`: `pendingTreeDockMerge = false; pendingDockAbsorbedPid = 0; pendingBoardingTargetInTree = false; dockingInProgress.Clear();`
- `OnFlightReady`: same cleanup
- `pendingDockMergedPid` and `pendingBoardingTargetPid` are existing fields - already cleaned up

### Fix 8 (IMPORTANT): `CreateMergeBranch` Snapshot Timing for Dock Merges

**Problem**: The merged vessel snapshot is taken in `CreateMergeBranch` (called from `Update()`). For dock merges, by this time the coupling is complete and the merged vessel has parts from BOTH vessels. This is correct - the snapshot captures the combined vessel.

However, for the INITIATOR case where we stop synchronously in `OnPartCouple`, the `CaptureAtStop` data from `StopRecordingForChainBoundary()` uses `RecordingVesselId` which is the OLD PID. The child recording should use `mergedVesselPid` (the surviving vessel's PID). This is handled correctly because `CreateMergeBranch` receives `mergedVesselPid` explicitly and passes it to `BuildMergeBranchData`.

**No code change needed** - confirmed correct.

### Summary of Required Code Changes

1. **`OnVesselSwitchComplete`**: Add `pendingTreeDockMerge` and `ChainToVesselPending` guards (Fix 1)
2. **`FindAbsorbedDockPartnerPid`**: Accept `Vessel mergedVessel` parameter, add `bgVessel == mergedVessel` check (Fix 2)
3. **`CreateMergeBranch`**: Remove `absorbedVesselPid` parameter, derive background PID from recording's `VesselPersistentId` (Fix 3)
4. **`Update()` board handler**: Clear `ChainToVesselPending` before calling `CreateMergeBranch` (Fix 5)
5. **Cleanup methods**: Add all new fields to `OnSceneChangeRequested` and `OnFlightReady` (Fix 7)
