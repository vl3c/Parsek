# Design: Restore Points & Go Back UI

Detailed implementation design for the two remaining items in Phase 5 ("Going Back in Time"): auto-saved restore points at recording commit points, and a "Go Back" UI to load them. Builds on the rationale and foundation in `design-going-back-in-time.md`.

## Problem

The player has no way to go back in time. All Phase 5 infrastructure exists (resource budgeting, epoch isolation, action blocking, milestone tracking), but there is no mechanism to create checkpoints or load them. The player can only move forward.

Going back is essential for the core Parsek vision: a common timeline where recorded missions play out autonomously. The recording system is a foundation for other mods (logistics, replay, visualization) that depend on a reliable, navigable timeline. Without restore points, the timeline is write-only.

## Terminology

- **Restore point**: a KSP quicksave paired with Parsek metadata, created automatically when the player commits a recording via the merge dialog. Captures the game state at the recording's launch time — the moment before that mission began.
- **Go back**: loading a restore point to return to an earlier UT. Parsek re-injects all committed recordings (including ones committed after the restore point) so they replay as ghosts.
- **Forward recording**: a committed recording whose StartUT is after the restore point's UT. These replay as ghosts when the player plays forward from the restore point.

## Mental Model

Restore points are launch-time snapshots. When the player commits a recording via the merge dialog (which requires reverting to launch), the game state is already at the launch point. Parsek captures this as a restore point. Going back loads a launch snapshot and lets all recordings — past and future — play out as ghosts.

```
Timeline:  ──────────────────────────────────────────────────► UT

Recordings:    [rec-1]    [rec-2]       [rec-3]
               100-200    250-350       400-500

Restore points:  RP1        RP2           RP3
                (UT 100)  (UT 250)      (UT 400)
                 launch     launch        launch
                 of rec-1   of rec-2      of rec-3

Player is at UT 500. Goes back to RP1 (UT 100):

             ────┬───────────────────────────────────────────► UT
                 │
            Player is here (UT 100, on the pad)

              [rec-1] will replay as ghost from UT 100
                   [rec-2] ghost appears at UT 250
                        [rec-3] ghost appears at UT 400

Player launches a new mission. All three recordings
replay as ghosts alongside the new flight.
```

There is no concept of a "present" or "timeline head." Restore points are just a bag of launch snapshots. The player can jump to any of them in any order. The existing Recordings window shows all committed recordings and their status — no additional awareness UI is needed.

## Design Decisions

### Commit Flight requires stationary vessel

The "Commit Flight" button (commits without reverting) is only enabled when the vessel is in a stable situation: `PRELAUNCH`, `LANDED`, `SPLASHED`, or `ORBITING`. Disabled during `FLYING`, `SUB_ORBITAL`, and `ESCAPING`.

Rationale: committing mid-flight would create an ambiguous game state — the vessel is at an arbitrary position with unpredictable trajectory. Requiring a stable situation (grounded or in a stable orbit) ensures the commit captures a clean, reproducible state.

### Only merge dialog commits create restore points

Restore points are created only when a recording is committed via the merge dialog ("Merge to Timeline"). This is the natural commit path: the player reverts to launch, then commits, so the game state IS the launch state. The quicksave captures this directly.

"Commit Flight" (stationary commit without revert) does NOT create a restore point because the game state at commit time is the POST-flight state, not the launch state. The recording's StartUT is the launch time, but the game state has moved on (contracts completed, science gathered, resources spent). A restore point here would load a post-flight state while claiming to be at launch time — inconsistent.

If the player wants a restore point for a recording, they should use the normal revert → merge dialog path.

### Resource adjustment on go-back

The quicksave captures funds at the launch point. Budget deductions from prior committed recordings are already reflected in the save's funds. On go-back:

1. Load the save → game funds reflect the launch-point state (with prior deductions baked in)
2. Parsek needs to deduct costs for recordings committed AFTER this restore point

To compute the correct deduction, the restore point stores `ReservedFundsAtSave` / `ReservedScienceAtSave` / `ReservedRepAtSave` — the total reserved amount from `ResourceBudget.ComputeTotal()` at save time. On go-back:

```
currentReserved  = ResourceBudget.ComputeTotal()  // all recordings, LARI reset to -1
additionalCost   = currentReserved - restorePoint.ReservedAtSave
game.Funds      -= additionalCost.funds
game.Science    -= additionalCost.science
game.Reputation -= additionalCost.rep
```

If recordings were deleted since the restore point, `additionalCost` can be negative (funds freed). This is correct — the player deleted recordings to free resources.

**Implementation note:** The exact interaction between budget deductions, playback resource application, and LARI reset needs careful investigation during the Plan phase. The formula above captures the intent; the Plan agent should trace through the existing `ApplyBudgetDeductionWhenReady()` and `UpdateTimelinePlayback()` resource flows to verify correctness or adjust the approach.

## Data Model

### RestorePoint

```
RestorePoint
    Id                  : string  — GUID, unique identifier
    UT                  : double  — recording's StartUT (launch time)
    SaveFileName        : string  — KSP save file name (without .sfs extension)
    Label               : string  — auto-generated description for UI display
    RecordingCount      : int     — committed recordings at creation time (display)
    Funds               : double  — game funds at launch point (display)
    Science             : double  — game science at launch point (display)
    Reputation          : float   — game reputation at launch point (display)
    ReservedFundsAtSave : double  — ResourceBudget total reserved funds at save time
    ReservedScienceAtSave: double — ResourceBudget total reserved science at save time
    ReservedRepAtSave   : float   — ResourceBudget total reserved rep at save time
```

### RestorePointStore (new static class)

```
RestorePointStore
    static restorePoints    : List<RestorePoint>
    static initialLoadDone  : bool
    static lastSaveFolder   : string

    // Go-back flags (survive scene change via static fields)
    static IsGoingBack      : bool
    static GoBackUT         : double
```

Parallel structure to `MilestoneStore` — static fields for in-memory state, external file for persistence, `initialLoadDone` + `lastSaveFolder` for load-once-per-save semantics.

### Serialization

External file: `saves/<saveName>/Parsek/restore_points.pgrp`

```
RESTORE_POINTS
{
    RESTORE_POINT
    {
        id = a1b2c3d4e5f6
        ut = 17030.5
        saveFile = parsek_rp_a1b2c3
        label = "Flea Rocket" launch (1 recording)
        recCount = 1
        funds = 47500.0
        science = 0.0
        rep = 0.0
        resFunds = 15000.0
        resSci = 0.0
        resRep = 0.0
    }
}
```

KSP quicksave files: `saves/<saveName>/parsek_rp_<shortId>.sfs` (standard KSP save format, in the normal save directory).

### File path additions to RecordingPaths

```
RestorePointFilePath  → saves/<saveName>/Parsek/restore_points.pgrp
RestorePointSaveName  → parsek_rp_<shortId>  (no .sfs extension)
```

## Behavior

### Commit Flight stationary constraint

**Check:** `Commit Flight` button enabled only when:
- `FlightGlobals.ActiveVessel.situation` is `PRELAUNCH`, `LANDED`, or `SPLASHED`
- Active recording exists with at least 2 points

When disabled due to vessel situation, tooltip: "Land or stop before committing."

### Creating restore points

**Trigger:** merge dialog → "Merge to Timeline" (standalone, chain, or tree). After `CommitPending()` / `CommitTreePending()` completes.

**Not triggered by:**
- "Commit Flight" button — post-flight state doesn't match launch time
- Auto-commit on scene exit (CommitTreeSceneExit) — not a deliberate player action
- EVA child auto-commit — internal mechanism
- Intermediate chain segment commits — only the final merge creates a restore point

**Steps (executed after commit completes, player is on the pad):**
1. Compute `reservedAtSave` from `ResourceBudget.ComputeTotal()`
2. Build label: `"\"{vesselName}\" launch ({N} recordings)"`
3. Generate short ID: first 6 chars of new GUID
4. Build save file name: `parsek_rp_{shortId}`
5. Call `GamePersistence.SaveGame(saveFileName, HighLogic.SaveFolder, SaveMode.OVERWRITE)`
6. Create `RestorePoint` with recording's StartUT, resource snapshot, reserved amounts
7. Add to `RestorePointStore.restorePoints`
8. Save metadata to external file (safe-write: `.tmp` + rename)

**Label examples:**
- `"Flea Rocket" launch (1 recording)`
- `"Mun Lander" launch (5 recordings)`
- `"Rescue Mission" tree launch (8 recordings)`

### Persisting restore point metadata

**Save:** `RestorePointStore.SaveRestorePointFile()` — called after creating/deleting a restore point. Uses safe-write pattern (write `.tmp`, rename). Not called from ParsekScenario.OnSave — restore point metadata is managed independently to avoid coupling with the quicksave's own OnSave cycle.

**Load:** `RestorePointStore.LoadRestorePointFile()` — called on initial load (once per save game, guarded by `initialLoadDone` + `lastSaveFolder`). Same pattern as MilestoneStore/GameStateStore.

**Validation on load:** for each restore point, check that the .sfs file exists. If missing, mark the restore point but keep it in the list (shown as unavailable in UI).

### Go Back UI

**Access:** "Go Back" button in the main Parsek window, in the timeline controls section near "Commit Flight" and "Wipe Recordings." Visible only when `restorePoints.Count > 0`. Disabled when `FlightRecorder.IsRecording` or `RecordingStore.HasPending` or `RecordingStore.HasPendingTree`.

**Picker dialog** (PopupDialog with DialogGUI elements):

```
┌──────────────────────────────────────────────────┐
│              Go Back in Time                      │
├──────────────────────────────────────────────────┤
│                                                   │
│  Select a launch point to return to:             │
│                                                   │
│  ┌──────────────────────────────────────────────┐│
│  │ Year 1, Day 1  03:22                         ││
│  │ "Flea Rocket" launch — 1 recording           ││
│  │ Funds: 47,500  Sci: 0  Rep: 0                ││
│  │                    [ Go Back ] [ Delete ]     ││
│  ├──────────────────────────────────────────────┤│
│  │ Year 1, Day 1  03:52                         ││
│  │ "KSC Hopper" launch — 2 recordings           ││
│  │ Funds: 43,200  Sci: 5  Rep: 2                ││
│  │                    [ Go Back ] [ Delete ]     ││
│  └──────────────────────────────────────────────┘│
│                                                   │
│  All committed recordings will replay as ghosts. │
│  Uncommitted progress since the selected point   │
│  will be lost.                                    │
│                                                   │
│                    [ Cancel ]                     │
└──────────────────────────────────────────────────┘
```

Each restore point entry is a row with info + "Go Back" and "Delete" buttons. Entries sorted chronologically (earliest first). If the restore point's save file is missing, the "Go Back" button is replaced with "(save file missing)".

**Confirmation dialog** (shown after clicking "Go Back"):

```
┌──────────────────────────────────────────────────┐
│              Confirm Go Back                      │
├──────────────────────────────────────────────────┤
│                                                   │
│  Going back to Year 1, Day 1  03:22              │
│  ("Flea Rocket" launch point).                   │
│                                                   │
│  • 2 future recordings will replay as ghosts     │
│  • Game state (funds, tech, facilities) will      │
│    revert to this launch point                    │
│  • Any uncommitted progress will be lost          │
│                                                   │
│           [ Confirm ]    [ Cancel ]               │
└──────────────────────────────────────────────────┘
```

The "future recordings" count = total committed recordings minus the restore point's `RecordingCount`.

### Go Back mechanism

**Preconditions (checked before showing picker):**
1. `FlightRecorder.IsRecording == false` — no active recording
2. `RecordingStore.HasPending == false` — no pending merge
3. `RecordingStore.HasPendingTree == false` — no pending tree merge
4. Player is in flight scene (GameScenes.FLIGHT)

If any precondition fails, the "Go Back" button is disabled with a tooltip explaining why.

**Execution steps:**

1. Player confirms go-back in the confirmation dialog
2. Set static flags:
   - `RestorePointStore.IsGoingBack = true`
   - `RestorePointStore.GoBackUT = restorePoint.UT`
   - `RestorePointStore.GoBackReserved = restorePoint.ReservedAtSave`
3. Load the quicksave via KSP API:
   ```
   Game game = GamePersistence.LoadGame(restorePoint.SaveFileName,
       HighLogic.SaveFolder, true, false);
   FlightDriver.StartAndFocusVessel(game, game.flightState.activeVesselIdx);
   ```
4. KSP begins scene transition → `OnSceneChangeRequested` fires
   - Existing logic runs but there's no active recording to stash
   - Ghost cleanup runs normally
5. New scene loads → `ParsekScenario.OnLoad` fires
   - **Detect `IsGoingBack` flag** (new code path, checked before revert detection)
   - **Skip loading recordings from .sfs** — in-memory `committedRecordings` and `committedTrees` already have ALL recordings (including ones committed after this quicksave)
   - **Skip loading crew replacements from .sfs** — in-memory `crewReplacements` is the source of truth
   - **Increment epoch** — isolates game state events from the abandoned future branch
   - **Reset milestone mutable state** with `resetUnmatched = true` — milestones created after the restore point are reset to unreplayed
   - **Reset ALL recording playback state:**
     - `VesselSpawned = false`
     - `SpawnedVesselPersistentId = 0`
     - `LastAppliedResourceIndex = -1`
     - `TakenControl = false`
     - `SceneExitSituation = -1`
   - **Apply resource adjustment:**
     - `currentReserved = ResourceBudget.ComputeTotal()` (with LARI reset)
     - `additional = currentReserved - GoBackReserved`
     - Deduct `additional` from game funds/science/reputation
   - **Clear `IsGoingBack` flag**, preserve `GoBackUT` for OnFlightReady
6. `OnFlightReady` fires
   - **Detect `GoBackUT` is set** (non-zero)
   - **Skip merge dialog** — no pending recording exists
   - Run normal timeline setup (subscribe events, initialize playback)
   - Apply crew swaps via `ParsekScenario.SwapReservedCrewInFlight()`
   - Re-reserve snapshot crew via `ParsekScenario.ReserveSnapshotCrew()`
   - **Clear `GoBackUT`**

**After go-back completes:**
- Player is on the pad at the recording's launch UT
- All committed recordings exist in the timeline
- Recordings whose EndUT < currentUT: vessels spawn on first Update (currentUT > EndUT check passes immediately)
- Recordings whose StartUT > currentUT: ghosts appear when time reaches their StartUT
- Resource budget reflects all committed costs
- Action blocking prevents re-researching/re-upgrading committed tech/facilities

### Deleting restore points

**From UI:** each restore point in the picker has a "Delete" button. Confirmation prompt: "Delete this restore point? The save file will be removed."

**Steps:**
1. Remove from `restorePoints` list
2. Delete the .sfs file: `saves/<saveName>/<saveFileName>.sfs`
3. Save updated metadata to external file

**No cascade:** deleting a restore point does NOT affect recordings, milestones, or other restore points.

### Scene restrictions

**v1:** "Go Back" UI only accessible from the flight scene. The toolbar button and Parsek window are flight-scene controls. Going back always loads a flight-scene save (player on the pad).

**Future enhancement:** allow going back from Space Center or Tracking Station.

## Edge Cases

1. **Active recording when Go Back requested**
   - Button disabled. Tooltip: "Stop recording before going back."
   - Log: `[Parsek][RestorePoint] Go Back disabled: recording in progress`

2. **Pending recording when Go Back requested**
   - Button disabled. Tooltip: "Merge or discard pending recording first."
   - Log: `[Parsek][RestorePoint] Go Back disabled: pending recording awaiting merge`

3. **Restore point save file missing**
   - Detected on metadata load and on picker open
   - Shown in picker with "(save file missing)" instead of "Go Back" button
   - Log: `[Parsek][RestorePoint] Save file missing for restore point {id}: {path}`

4. **Player goes back multiple times**
   - Each go-back is independent. Restore points from all timelines remain valid.
   - New recordings committed after a go-back create new restore points.
   - No special handling needed.

5. **Going back to before all recordings**
   - All recordings become forward recordings. All replay as ghosts.
   - All vessel spawns deferred to their EndUTs.
   - Resource deductions applied for all recording costs.

6. **Recordings spanning the restore UT (StartUT < restoreUT < EndUT)**
   - Ghost is already "in progress" at the restore UT.
   - Playback picks up naturally: on first Update, currentUT is mid-recording, ghost interpolates to the correct position.
   - VesselSpawned was reset, so vessel will re-spawn at EndUT.

7. **Recordings with EndUT < restoreUT**
   - Vessel spawns immediately on first Update tick (currentUT > EndUT).
   - Resource deltas re-apply during playback (LastAppliedResourceIndex was reset).
   - Existing playback logic handles this.

8. **Chain recordings spanning the restore UT**
   - All segments get playback state reset. Each segment replays during its UT range.
   - Vessel spawns from the final segment at its EndUT.

9. **Tree recordings with mixed before/after branches**
   - All branches get playback state reset. Each branch replays during its UT range.
   - Leaf vessels spawn at their EndUTs.

10. **Commit Flight while flying/moving**
    - Button disabled. Tooltip: "Land or stop before committing."
    - Allowed situations: `PRELAUNCH`, `LANDED`, `SPLASHED`, `ORBITING`.
    - Does NOT create a restore point (post-flight state, not launch state).

11. **KSP contracts after go-back**
    - Contract state reverts via quicksave load.
    - Milestone events referencing contracts committed after the restore point remain in milestone history but the contracts themselves are in their pre-commitment state.
    - Acceptable v1 limitation — contracts are not part of Parsek's action blocking.

12. **Tech tree after go-back**
    - Tech reverts to quicksave state (pre-future-research).
    - Action blocking prevents re-researching committed tech.
    - Already handled by existing Harmony patches.

13. **Facility levels after go-back**
    - Facilities revert to quicksave state.
    - Action blocking prevents re-upgrading committed facilities.
    - Already handled by existing Harmony patches.

14. **Crew roster after go-back**
    - Roster reverts to quicksave state (crew statuses from that moment).
    - Crew reservations re-applied from in-memory `crewReplacements` dict.
    - `ReserveSnapshotCrew()` called to maintain reservation state.
    - Crew swaps happen when vessels spawn.

15. **Ghost vessels from previous playback**
    - All ghost GameObjects destroyed during scene change (existing cleanup in `OnSceneChangeRequested`).
    - New ghosts spawn fresh after restore point loads.

16. **Multiple restore points at the same UT**
    - Possible if two recordings committed from the same launch (chain final segment).
    - Each gets unique ID and save file. Both shown in picker.

17. **Many restore points (storage concern)**
    - Each quicksave is a full .sfs file (typically 100KB-2MB).
    - 20 restore points ≈ 2-40 MB. Acceptable.
    - v1: no limit. Future: configurable limit with auto-prune of oldest.
    - Log warning if > 20 restore points.

18. **Player loads restore point via KSP's native load menu (not Parsek UI)**
    - The .sfs is a standard KSP save — KSP can load it directly.
    - `IsGoingBack` flag is NOT set → Parsek detects this as a normal revert.
    - Existing revert detection handles it: epoch increments, merge dialog may show if there's stale pending data in the .sfs.
    - In-memory recordings persist. Acceptable v1 behavior.

19. **GamePersistence.SaveGame fails**
    - Quicksave write fails (disk full, permissions, etc.).
    - Do NOT create the restore point metadata. Log error.
    - Player is not affected — commit already succeeded, only the restore point is skipped.
    - Log: `[Parsek][RestorePoint] Failed to create quicksave: {error}`

20. **Game load API fails**
    - Load fails (corrupted save, missing vessel, etc.).
    - Clear `IsGoingBack` and `GoBackUT` flags.
    - KSP may show its own error dialog. Log error.
    - Log: `[Parsek][RestorePoint] Failed to load restore point save: {error}`

21. **Going "forward" to a later restore point**
    - Player at UT 200, selects restore point at UT 400.
    - Loads the game state from UT 400 (on the pad before that mission launched).
    - Recordings before UT 400 spawn vessels immediately. Recordings after UT 400 replay as ghosts.
    - The game state reflects the original timeline at UT 400. This is inherent to snapshot-based restore.

22. **Wipe Recordings also wipes restore points**
    - When the player wipes all recordings, restore points are also deleted.
    - Restore points without recordings are useless — the timeline is empty.
    - Both the metadata file and all quicksave files are deleted.

23. **Delete a committed recording, then go back**
    - Recording removed from committed list → its cost freed.
    - Going back: the deleted recording is not in memory → not replayed.
    - Resource adjustment formula handles this: `currentReserved < savedReserved` → negative adjustment → funds increase.

24. **Go back to arbitrary UT (not a restore point)**
    - Not supported in v1. Player must use a restore point.
    - Workaround: go back to the nearest earlier restore point, then time-warp forward.
    - Future enhancement: timeline slider that loads nearest restore point + warps to target UT.

25. **Resource adjustment with partially-applied recordings**
    - At save time, some recordings may be partially played (LARI > -1), so `ReservedAtSave` reflects partial costs.
    - After go-back, LARI resets to -1 (full costs). `currentReserved` uses full costs.
    - The adjustment `currentReserved - ReservedAtSave` may over-deduct because it treats partially-applied costs as new costs.
    - **Mitigation:** at save time, compute `ReservedAtSave` as if LARI were -1 for all recordings (full costs, not partial). This makes the comparison consistent.
    - The Plan agent must verify this approach against the existing `ResourceBudget.ComputeTotal()` implementation.

## What Doesn't Change

- Recording format — no new fields in Recording, TrajectoryPoint, or PartEvent
- Ghost visual building — GhostVisualBuilder unchanged
- Part event recording and playback — all 28 event types unchanged
- Engine/RCS/parachute/fairing FX — unchanged
- Merge dialog — unchanged (creates restore point after commit, but dialog logic itself is unchanged)
- Existing revert detection — still works for normal reverts (IsGoingBack check is upstream)
- Resource budget computation — ComputeTotal unchanged
- Action blocking — tech/facility Harmony patches unchanged
- Milestone and game state event system — unchanged
- Recording tree system — unchanged
- Chain recording system — unchanged
- External file formats — no changes to .prec, .craft, .pcrf
- Synthetic recording generators — no changes needed
- Recording format version — stays at v4

## Backward Compatibility

- Existing saves have no restore points → empty list on load, no issues
- No recording format version bump needed
- `restore_points.pgrp` file is new — absence means no restore points
- KSP quicksave files are standard format — no compatibility concerns
- ParsekScenario.OnLoad: `IsGoingBack` check is a new code path that doesn't affect existing revert/scene-change paths
- Commit Flight stationary constraint is a new UI restriction — no data migration needed

## Diagnostic Logging

### Restore point creation
- `[Parsek][RestorePoint] Creating restore point at UT {ut}: "{label}" (save: {saveFile})`
- `[Parsek][RestorePoint] Quicksave written: {fullPath}`
- `[Parsek][RestorePoint] Failed to create quicksave: {error}` (error path)
- `[Parsek][RestorePoint] Metadata saved: {count} restore points to {path}`

### Go Back execution
- `[Parsek][RestorePoint] Go Back initiated to UT {ut} (restore point {id}, save: {saveFile})`
- `[Parsek][RestorePoint] Loading save file: {saveFile}`
- `[Parsek][RestorePoint] OnLoad: go-back detected, skipping .sfs recording load (using {count} in-memory recordings)`
- `[Parsek][RestorePoint] OnLoad: resetting playback state for {standaloneCount} recordings + {treeCount} trees`
- `[Parsek][RestorePoint] OnLoad: epoch incremented to {epoch}`
- `[Parsek][RestorePoint] OnLoad: resource adjustment: reserved {savedReserved} → {currentReserved}, delta {delta}`
- `[Parsek][RestorePoint] OnFlightReady: go-back complete at UT {ut}. Timeline: {recCount} recordings, {rpCount} restore points`

### Commit Flight constraint
- `[Parsek][UI] Commit Flight disabled: vessel situation {situation} (requires PRELAUNCH, LANDED, or SPLASHED)` (verbose)

### Precondition checks
- `[Parsek][RestorePoint] Go Back disabled: recording in progress` (verbose)
- `[Parsek][RestorePoint] Go Back disabled: pending recording exists` (verbose)
- `[Parsek][RestorePoint] Go Back disabled: not in flight scene` (verbose)

### Validation
- `[Parsek][RestorePoint] Save file missing for restore point {id}: {expectedPath}` (warning, on load)
- `[Parsek][RestorePoint] Restore point {id} has invalid save file name: {name}` (warning, on load)

### File I/O
- `[Parsek][RestorePoint] Loaded {count} restore points from {path}`
- `[Parsek][RestorePoint] No restore point file found at {path} (first run)`
- `[Parsek][RestorePoint] Deleted restore point {id}: save file {saveFile} removed`
- `[Parsek][RestorePoint] Deleted restore point {id}: save file already missing`

### Edge cases
- `[Parsek][RestorePoint] Warning: {count} restore points exceed recommended limit of 20`
- `[Parsek][RestorePoint] Load failed for save {saveFile}: {error}` (error path)

## Test Plan

### Unit tests

1. **RestorePoint serialization round-trip**
   - Create RestorePoint with known values → serialize to ConfigNode → deserialize → assert all fields match (including ReservedAtSave fields).
   - Guards against: serialization key typos, missing fields, locale-dependent float formatting.

2. **RestorePoint label generation**
   - Input: vessel name "Flea Rocket", 3 recordings → assert label = `"Flea Rocket" launch (3 recordings)`.
   - Input: tree with name "Mun Lander" → assert label includes "tree".
   - Guards against: string formatting bugs, missing vessel name handling.

3. **RestorePoint save file name validation**
   - Valid short IDs → accepted. Null/empty/path-traversal chars → rejected.
   - Guards against: file name injection, invalid path construction.

4. **Go Back precondition logic**
   - `CanGoBack()` returns false when recording active, pending exists, or wrong scene.
   - Returns true when all preconditions met.
   - Guards against: state machine bugs allowing go-back at wrong time.

5. **Playback state reset**
   - Given 5 recordings with various VesselSpawned/LastAppliedResourceIndex/TakenControl states.
   - After `ResetAllPlaybackState()`: all VesselSpawned=false, LastAppliedResourceIndex=-1, SpawnedVesselPersistentId=0, TakenControl=false, SceneExitSituation=-1.
   - Guards against: incomplete reset leaving stale spawn flags or resource indices.

6. **Playback state reset for trees**
   - Given 2 trees with leaf recordings in various states.
   - After reset: all leaf recording playback state reset, tree ResourcesApplied reset.
   - Guards against: tree recordings not covered by reset logic.

7. **Commit Flight situation check**
   - PRELAUNCH → allowed. LANDED → allowed. SPLASHED → allowed. ORBITING → allowed.
   - FLYING → blocked. SUB_ORBITAL → blocked. ESCAPING → blocked.
   - Guards against: commit from unstable vessel situation.

8. **Resource adjustment calculation**
   - savedReserved = (15000, 5, 2). currentReserved = (25000, 10, 5). → adjustment = (10000, 5, 3).
   - savedReserved = (20000, 0, 0). currentReserved = (8000, 0, 0) (recording deleted). → adjustment = (-12000, 0, 0) — funds freed.
   - Guards against: incorrect deduction formula, double-deduction, sign errors.

### Integration tests

9. **Restore point metadata file round-trip**
   - Create 3 restore points → save to file → clear in-memory list → load from file → assert all 3 restored with correct values.
   - Guards against: file I/O bugs, ConfigNode structure errors.

10. **Restore point deletion cleanup**
    - Create restore point → delete → assert removed from list, metadata file updated.
    - Guards against: incomplete cleanup, stale entries in metadata file.

11. **Epoch increment on go-back**
    - Record initial epoch → call `OnLoadAfterGoBack()` → assert epoch incremented exactly once.
    - Guards against: missing or double epoch increment.

12. **Wipe recordings also wipes restore points**
    - Create recordings + restore points → wipe → assert both lists empty, all files deleted.
    - Guards against: orphaned restore points after wipe.

### Log assertion tests

13. **Restore point creation log**
    - Trigger restore point creation → assert log contains `Creating restore point at UT` with correct UT and label.
    - Guards against: silent restore point creation without diagnostic output.

14. **Go back state reset log**
    - Simulate go-back with 5 recordings → assert log contains `resetting playback state for 5 recordings`.
    - Guards against: state reset happening silently.

15. **Precondition failure log**
    - Set recording active → call CanGoBack → assert log contains `Go Back disabled: recording in progress`.
    - Guards against: precondition check without diagnostic output.

16. **Missing save file warning log**
    - Create restore point → remove the .sfs path from validation → assert log contains `Save file missing`.
    - Guards against: missing save file handled silently.

17. **Resource adjustment log**
    - Simulate go-back → assert log contains `resource adjustment: reserved {old} → {new}, delta {delta}`.
    - Guards against: resource adjustment without diagnostic output.

### Edge case tests

18. **No restore points → UI hidden**
    - Empty restore point list → assert `HasRestorePoints == false`.
    - Guards against: null reference on empty list, UI showing when it shouldn't.

19. **Restore point with missing save file**
    - Create restore point → mark save as missing → assert `IsAvailable == false` and picker shows it as unavailable.
    - Guards against: unhandled file-not-found, crash when selecting unavailable point.

20. **Multiple rapid go-backs**
    - Go back → reset state → go back again → assert state consistent after second go-back.
    - Guards against: stale flags from first go-back corrupting second.

21. **Restore points survive metadata save/load cycle**
    - Create 3 restore points → save file → simulate new session (reset initialLoadDone) → load file → assert all 3 preserved.
    - Guards against: metadata not persisted across game restarts.

## Open Questions

1. **Should there be a maximum number of restore points?** v1: no limit. Monitor storage impact. Add configurable limit with auto-prune of oldest if needed.

2. **Should the player be able to create manual restore points?** v1: auto-only. Manual creation is a natural future enhancement (button in UI or hotkey).

3. **Exact KSP API for loading a save programmatically.** `GamePersistence.LoadGame` + `FlightDriver.StartAndFocusVessel` is the expected approach but needs verification during implementation. Other mods (e.g., quicksave mods) may provide reference implementations.

4. **DOCKED situation for Commit Flight.** Should docked vessels be allowed to commit? Docked is technically stable. v1: treat as the underlying situation (if the combined vessel is orbiting, allow it).
