# Design: Restore Points & Go Back UI

Detailed implementation design for the two remaining items in Phase 5 ("Going Back in Time"): auto-saved restore points at recording commit points, and a "Go Back" UI to load them. Builds on the rationale and foundation in `design-going-back-in-time.md`.

## Problem

The player has no way to go back in time. All Phase 5 infrastructure exists (resource budgeting, epoch isolation, action blocking, milestone tracking), but there is no mechanism to create checkpoints or load them. The player can only move forward.

Going back is essential for the core Parsek vision: a common timeline where recorded missions play out autonomously. The recording system is a foundation for other mods (logistics, replay, visualization) that depend on a reliable, navigable timeline. Without restore points, the timeline is write-only.

## Terminology

- **Restore point**: a KSP quicksave paired with Parsek metadata, created automatically when a recording is committed to the timeline. Captures the game state at the recording's launch time — the moment before that mission began.
- **Launch save**: a quicksave captured at the moment a recording starts, if the vessel is in a stable state. Held temporarily until the recording is committed (becomes a restore point) or discarded (deleted).
- **Go back**: loading a restore point to return to an earlier UT. Parsek re-injects all committed recordings (including ones committed after the restore point) so they replay as ghosts.
- **Forward recording**: a committed recording whose StartUT is after the restore point's UT. These replay as ghosts when the player plays forward from the restore point.

## Mental Model

Every committed recording has a restore point at its launch time (if the vessel was in a stable state when recording started). The launch save is captured at recording start and promoted to a restore point when the recording is committed — regardless of the commit mechanism (merge dialog, Commit Flight, auto-commit on scene exit, tree commit). Going back loads a launch snapshot and lets all recordings — past and future — play out as ghosts.

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

### Launch save captured at recording start

To ensure every committed recording has a restore point at its launch time regardless of commit path, Parsek captures a "launch save" (quicksave) when a recording starts — if the vessel is in a stable state (`PRELAUNCH`, `LANDED`, `SPLASHED`, `ORBITING`). This launch save is held in `RestorePointStore` as a pending launch save until the recording is committed or discarded.

**Why at recording start, not commit time:** for merge dialog commits, the player has reverted to launch so the current state matches. But for Commit Flight and auto-commits, the player is post-flight — the game state has moved on. Capturing at recording start ensures the quicksave always reflects the pre-flight state, regardless of how the recording is later committed.

**What triggers a launch save:**
- Auto-recording on launch (vessel leaves PRELAUNCH → recording starts)
- Manual recording start via F9 while in a stable state

**What does NOT get a launch save:**
- Recording started mid-flight from an unstable state (FLYING, SUB_ORBITAL, ESCAPING)
- Tree child recordings (created by undock/EVA/split events mid-flight)
- Chain continuation segments (subsequent segments after the first)

For tree commits, the restore point uses the root recording's launch save. For chain commits, the first segment's launch save. This means one restore point per mission launch, not per recording.

### Resource adjustment on go-back

The quicksave captures funds at the launch point. Prior budget deductions (from `ApplyBudgetDeductionWhenReady` on earlier reverts) may or may not be baked into the save's funds, depending on when the launch save was captured relative to those deductions. To handle this correctly regardless of save timing, the restore point stores a `ReservedAtSave` snapshot computed with a consistent baseline.

#### ReservedAtSave computation (at launch save capture time)

`ReservedAtSave` is computed using a new method `ResourceBudget.ComputeTotalFullCost()` that evaluates all committed costs as if nothing has been applied yet:
- All recordings: `CommittedFundsCost` with LARI treated as -1 (full `PreLaunchFunds - Points[last].funds`)
- All trees: `TreeCommittedFundsCost` with `ResourcesApplied` treated as false (full `-DeltaFunds`)
- All milestones: `MilestoneCommittedFunds` with `LastReplayedEventIndex` treated as -1 (all events)

This ensures `ReservedAtSave` always represents the full committed cost at save time, regardless of how much has been applied via playback or budget deduction. The same method is used after go-back, making the comparison consistent.

#### Go-back resource adjustment

```
// Step 1: Reset all playback state (LARI=-1, ResourcesApplied=false, etc.)
// Step 2: Compute current full cost (same method as at save time)
currentReserved  = ResourceBudget.ComputeTotalFullCost()

// Step 3: Differential deduction
additionalCost   = currentReserved - restorePoint.ReservedAtSave
SuppressResourceEvents = true
game.Funds      -= additionalCost.funds
game.Science    -= additionalCost.science
game.Reputation -= additionalCost.rep
SuppressResourceEvents = false

// Step 4: Mark everything as fully applied (prevents ghost playback from re-applying)
foreach recording: LARI = Points.Count - 1
foreach tree: ResourcesApplied = true
foreach milestone: LastReplayedEventIndex = Events.Count - 1

// Step 5: Prevent double-deduction from existing revert mechanism
budgetDeductionEpoch = MilestoneStore.CurrentEpoch
```

If recordings were deleted since the restore point, `additionalCost` can be negative (funds freed). This is correct — the player deleted recordings to free resources.

**Why `SuppressResourceEvents`:** Without suppression, the funds/science/reputation changes would be captured by `GameStateRecorder` as game state events, polluting the milestone system with synthetic deductions.

**Why mark everything as fully applied (step 4):** Ghost playback calls `ApplyResourceDeltas` every frame, advancing LARI and modifying funds. If LARI were left at -1 after the budget adjustment, playback would re-apply the same deltas — double-deduction. Setting LARI to max and `ResourcesApplied = true` ensures playback sees "nothing left to apply."

**Why set `budgetDeductionEpoch` (step 5):** The go-back path bypasses `ApplyBudgetDeductionWhenReady` (no revert detection triggers it). But if something unexpected triggers the coroutine later, the epoch guard prevents it from running again.

#### Worked example: two recordings, go back to first

```
Timeline: Mission A (UT 100-200, earns 4k), Mission B (UT 250-350, loses 2k)

=== Step 1: Launch A ===
  Start: 100,000 funds
  KSP deducts vessel cost (20k) → funds = 80,000
  Launch save captured: saveFunds = 80,000
  No committed recordings → ComputeTotalFullCost() = 0
  ReservedAtSave_A = (0, 0, 0)

=== Step 2: Fly A, revert, commit ===
  Recording A: PreLaunchFunds=80k, Points[last].funds=84k (earned 4k in-flight)
  Revert to launch → funds = 80,000 (from quicksave)
  ApplyBudgetDeductionWhenReady:
    ComputeTotal: CommittedFundsCost(A) = 80k - 84k = -4k
    Deduct -(-4k) = +4k → funds = 84,000
    LARI_A = max
  Merge A to timeline. RP_A created.

=== Step 3: Launch B ===
  KSP deducts vessel cost (10k) → funds = 74,000
  Launch save captured: saveFunds = 74,000
  ComputeTotalFullCost(): CommittedFundsCost(A) with LARI=-1 = -4k
  ReservedAtSave_B = (-4000, 0, 0)

=== Step 4: Fly B, revert, commit ===
  Recording B: PreLaunchFunds=74k, Points[last].funds=72k (lost 2k)
  Revert → funds = 74,000. Budget deduction: CommittedFundsCost(B) = 2k
  Deduct 2k → funds = 72,000. LARI_A = max, LARI_B = max.
  Merge B. RP_B created.

=== Step 5: Go back to RP_A (UT 100) ===
  Load A's quicksave → funds = 80,000
  Reset: LARI_A = -1, LARI_B = -1
  ComputeTotalFullCost(): A=-4k, B=+2k → total = -2k
  additionalCost = -2k - 0 = -2k
  Deduct -(-2k) = +2k → funds = 82,000
  Set LARI_A = max, LARI_B = max

  Verify: 82k = 80k(save) - (-4k + 2k)(net committed cost) ✓
  Player can spend 82k. A's earnings and B's losses are pre-applied.
  Ghost playback: no additional resource changes (LARI = max).

=== Step 6: Go back to RP_B (UT 250) ===
  Load B's quicksave → funds = 74,000
  Reset: LARI_A = -1, LARI_B = -1
  ComputeTotalFullCost(): A=-4k, B=+2k → total = -2k
  additionalCost = -2k - (-4k) = +2k
  Deduct 2k → funds = 72,000
  Set LARI_A = max, LARI_B = max

  Verify: 72k = 74k(save) - 2k(B's cost, the only one not baked in) ✓
  A's cost was already baked into the 74k save. Only B's cost is new.
```

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

### PendingLaunchSave

```
PendingLaunchSave
    SaveFileName            : string  — quicksave file name (without .sfs)
    UT                      : double  — recording StartUT (launch time)
    Funds                   : double  — game funds at launch
    Science                 : double  — game science at launch
    Reputation              : float   — game reputation at launch
    ReservedFundsAtSave     : double  — ResourceBudget reserved funds at launch
    ReservedScienceAtSave   : double  — ResourceBudget reserved science at launch
    ReservedRepAtSave       : float   — ResourceBudget reserved rep at launch
```

Temporary structure held between recording start and commit/discard. Not persisted — exists only in static memory.

### RestorePointStore (new static class)

```
RestorePointStore
    static restorePoints        : List<RestorePoint>
    static pendingLaunchSave    : PendingLaunchSave?  — current mission's launch save
    static initialLoadDone      : bool
    static lastSaveFolder       : string

    // Go-back flags (survive scene change via static fields)
    static IsGoingBack          : bool
    static GoBackUT             : double
    static GoBackReserved       : BudgetSummary
```

Parallel structure to `MilestoneStore` — static fields for in-memory state, external file for persistence, `initialLoadDone` + `lastSaveFolder` for load-once-per-save semantics. The `pendingLaunchSave` is a singleton — at most one launch save exists at a time (one mission in progress).

### Serialization

External file: `saves/<saveName>/Parsek/GameState/restore_points.pgrp`

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
RestorePointFilePath  → saves/<saveName>/Parsek/GameState/restore_points.pgrp
RestorePointSaveName  → parsek_rp_<shortId>  (no .sfs extension)
```

## Behavior

### Commit Flight stationary constraint

**Check:** `Commit Flight` button enabled only when:
- `FlightGlobals.ActiveVessel.situation` is `PRELAUNCH`, `LANDED`, `SPLASHED`, or `ORBITING`
- Active recording exists with at least 2 points

When disabled due to vessel situation, tooltip: "Land or stop before committing."

### Capturing launch saves

**Trigger:** when a new mission recording starts and the vessel is in a stable state.

**Stable states:** `PRELAUNCH`, `LANDED`, `SPLASHED`, `ORBITING`.

**When to capture:**
- Auto-recording starts on launch (vessel transitions from PRELAUNCH)
- Manual recording start (F9) while vessel is in a stable state

**When NOT to capture:**
- Recording starts from an unstable state (FLYING, SUB_ORBITAL, ESCAPING) — no launch save
- Tree child recordings (created by undock/EVA/split) — use root's launch save
- Chain continuation segments — use first segment's launch save

**Steps (executed at recording start):**
1. Check vessel situation — skip if not stable
2. If a `pendingLaunchSave` already exists, delete its quicksave file (previous mission wasn't committed)
3. Generate short ID: first 6 chars of new GUID
4. Build save file name: `parsek_rp_{shortId}`
5. Call `GamePersistence.SaveGame(saveFileName, HighLogic.SaveFolder, SaveMode.OVERWRITE)`
6. Capture resource snapshot: current funds/science/reputation
7. Capture `reservedAtSave` from `ResourceBudget.ComputeTotalFullCost()` (full costs, ignoring LARI/ResourcesApplied/LastReplayedEventIndex)
8. Store as `RestorePointStore.pendingLaunchSave`

**Side effect of SaveGame:** `GamePersistence.SaveGame` triggers `ParsekScenario.OnSave`, which flushes pending game state events via `MilestoneStore.FlushPendingEvents`. This is harmless — events accumulated since the last save are correctly captured into milestones. No pending recording data is saved (the recording just started).

**Performance:** Quicksaving serializes the entire game state (typically 100-500ms on complex saves). This happens at recording start — usually the moment of launch, which is already a busy frame. If the hitch is noticeable, the save can be deferred by 2-3 frames via `StartCoroutine` to avoid compounding with physics initialization. v1: save immediately, measure impact.

### Creating restore points

**Trigger:** a root recording commit to the timeline. ALL commit paths promote the launch save:
- Merge dialog → "Merge to Timeline" (standalone or chain — the committed recording has no `ParentRecordingId`)
- "Commit Flight" button (standalone or tree)
- Auto-commit on scene exit (CommitTreeSceneExit)
- Tree merge dialog → "Merge to Timeline"

**What does NOT promote the launch save:**
- EVA child auto-commit — the EVA is a child recording (`ParentRecordingId != null`), not the root mission. The launch save belongs to the parent mission.
- Chain continuation segment commits — these are intermediate; only the final chain commit matters.

**Guard:** only promote `pendingLaunchSave` if the committed recording is a root: `rec.ParentRecordingId == null` for standalone/chain, or the tree commit itself for trees.

**Condition:** `pendingLaunchSave` exists AND the committed recording is a root. If there is no pending launch save (recording started from unstable state), no restore point is created.

**Steps (executed after CommitPending / CommitTreePending / CommitTreeSceneExit completes):**
1. Check `RestorePointStore.pendingLaunchSave` — if null, skip
2. Check root guard — if the committed recording is a child (EVA, continuation), skip
3. Build label: `"\"{vesselName}\" launch ({N} recordings)"`
4. Create `RestorePoint` from `pendingLaunchSave` fields (UT, save file, resources, reserved amounts)
5. Add to `RestorePointStore.restorePoints`
6. Save metadata to external file (safe-write: `.tmp` + rename)
7. Clear `pendingLaunchSave` (consumed — now a permanent restore point)

**Discarding recordings:**
- When a recording is discarded (merge dialog "Discard", or recording cleared), and `pendingLaunchSave` exists:
  - Delete the quicksave file
  - Clear `pendingLaunchSave`

**Label examples:**
- `"Flea Rocket" launch (1 recording)`
- `"Mun Lander" launch (5 recordings)`
- `"Rescue Mission" tree launch (8 recordings)`

### Persisting restore point metadata

**Save:** `RestorePointStore.SaveRestorePointFile()` — called after creating/deleting a restore point. Uses safe-write pattern (write `.tmp`, rename). Not called from `ParsekScenario.OnSave` — this is an intentional divergence from `MilestoneStore`/`GameStateStore` (which save from OnSave). Reason: the launch save quicksave itself triggers OnSave, which would create a circular dependency if restore point metadata were also saved from OnSave. Instead, restore point metadata is saved at the point of mutation (create/delete), which is simpler and avoids the circular trigger.

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

The "future recordings" count = count of committed recordings whose `StartUT > restorePoint.UT`. Computed live from in-memory recordings, not from `RecordingCount` (which can become stale if recordings are deleted after the restore point was created).

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
   - **Detect `IsGoingBack` flag at the VERY TOP of OnLoad** — before `LoadCrewReplacements`, before revert detection, before anything else in the `initialLoadDone == true` branch. This is critical: `LoadCrewReplacements` is currently called unconditionally and would overwrite in-memory crew data with stale .sfs data.
   - **Skip `LoadCrewReplacements`** — in-memory `crewReplacements` is the source of truth (it has replacements for recordings committed after this quicksave)
   - **Skip loading recordings from .sfs** — in-memory `committedRecordings` and `committedTrees` already have ALL recordings (including ones committed after this quicksave)
   - **Skip revert detection** — go-back is not a revert; it has its own handling
   - **Do NOT read `milestoneEpoch` from the loaded .sfs** — the quicksave has an old epoch value. Instead, increment the current in-memory `MilestoneStore.CurrentEpoch` (which has been maintained across all reverts/go-backs in this session)
   - **Reset milestone mutable state** with `resetUnmatched = true` — milestones created after the restore point are reset to unreplayed. Pass the loaded ConfigNode so milestones present in the quicksave get their saved `LastReplayedEventIndex`.
   - **Reset ALL recording playback state (standalone):**
     - `VesselSpawned = false`
     - `SpawnAttempts = 0`
     - `SpawnedVesselPersistentId = 0`
     - `LastAppliedResourceIndex = -1`
     - `TakenControl = false`
     - `SceneExitSituation = -1`
   - **Reset ALL tree playback state:**
     - All tree recordings: same fields as standalone above
     - `tree.ResourcesApplied = false` (critical — without this, `TreeCommittedFundsCost` returns 0 and tree costs are excluded from the adjustment)
   - **Schedule resource adjustment via `StartCoroutine(ApplyGoBackResourceAdjustment())`:**
     The adjustment runs as a deferred coroutine (not inline in OnLoad) because `Funding.Instance`, `ResearchAndDevelopment.Instance`, and `Reputation.Instance` may not be initialized yet during OnLoad. The coroutine follows the same wait-for-singletons pattern as `ApplyBudgetDeductionWhenReady` (wait up to 120 frames).
     Once singletons are available:
     - `currentReserved = ResourceBudget.ComputeTotalFullCost()` (same method used at save time — consistent baseline)
     - `additional = currentReserved - GoBackReserved`
     - `GameStateRecorder.SuppressResourceEvents = true` (prevent synthetic game state events)
     - Deduct `additional` from game funds/science/reputation via `Funding.Instance.AddFunds` etc.
     - `GameStateRecorder.SuppressResourceEvents = false`
     - Mark everything as fully applied (same as `ApplyBudgetDeductionWhenReady` step 5):
       - All recordings: `LARI = Points.Count - 1`
       - All trees: `ResourcesApplied = true`
       - All milestones: `LastReplayedEventIndex = Events.Count - 1`
   - **Set `budgetDeductionEpoch = MilestoneStore.CurrentEpoch`** — prevents any accidental `ApplyBudgetDeductionWhenReady` from double-deducting (set in OnLoad, before the coroutine runs)
   - **`ReserveSnapshotCrew()`** — re-reserves crew from all recording snapshots
   - **Clear `IsGoingBack` flag**, preserve `GoBackUT` for OnFlightReady
   - **Recreate `GameStateRecorder`** — the loaded save has different game state; the recorder needs fresh facility cache seeding and event subscriptions. Follow the same pattern as the existing OnLoad path (unsubscribe old, create new, seed, subscribe).
6. `OnFlightReady` fires
   - **Detect `GoBackUT` is set** (non-zero)
   - **Skip merge dialog** — no pending recording exists
   - Run normal timeline setup (subscribe events, initialize playback)
   - Apply crew swaps via `ParsekScenario.SwapReservedCrewInFlight()`
   - Re-reserve snapshot crew via `ParsekScenario.ReserveSnapshotCrew()`
   - **Clear `GoBackUT`**

**After go-back completes:**
- Player is at the recording's launch state (on the pad for launch recordings, in orbit for manually-started orbital recordings)
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

**v1:** "Go Back" UI only accessible from the flight scene. The toolbar button and Parsek window are flight-scene controls. Going back loads a flight-scene save — usually on the pad (auto-recorded launches), but possibly in orbit (manually-started orbital recordings).

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
   - Resource deltas do NOT re-apply during playback — LARI was set to max by the go-back resource adjustment (step 4). The adjustment already pre-applied all costs.
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
    - Creates a restore point using the launch save captured at recording start (not the current post-flight state).

11. **Recording started from unstable state**
    - No launch save captured → no restore point created on commit.
    - Recording is committed normally, just without a restore point.
    - Log: `[Parsek][RestorePoint] No launch save for recording {id} (started from {situation})`

12. **KSP contracts after go-back**
    - Contract state reverts via quicksave load.
    - Milestone events referencing contracts committed after the restore point remain in milestone history but the contracts themselves are in their pre-commitment state.
    - Acceptable v1 limitation — contracts are not part of Parsek's action blocking.

13. **Tech tree after go-back**
    - Tech reverts to quicksave state (pre-future-research).
    - Action blocking prevents re-researching committed tech.
    - Already handled by existing Harmony patches.

14. **Facility levels after go-back**
    - Facilities revert to quicksave state.
    - Action blocking prevents re-upgrading committed facilities.
    - Already handled by existing Harmony patches.

15. **Crew roster after go-back**
    - Roster reverts to quicksave state (crew statuses from that moment).
    - Crew reservations re-applied from in-memory `crewReplacements` dict.
    - `ReserveSnapshotCrew()` called to maintain reservation state.
    - Crew swaps happen when vessels spawn.

16. **Ghost vessels from previous playback**
    - All ghost GameObjects destroyed during scene change (existing cleanup in `OnSceneChangeRequested`).
    - New ghosts spawn fresh after restore point loads.

17. **Multiple restore points at the same UT**
    - Possible if two recordings committed from the same launch (chain final segment).
    - Each gets unique ID and save file. Both shown in picker.

18. **Many restore points (storage concern)**
    - Each quicksave is a full .sfs file (typically 100KB-2MB).
    - 20 restore points ≈ 2-40 MB. Acceptable.
    - v1: no limit. Future: configurable limit with auto-prune of oldest.
    - Log warning if > 20 restore points.

19. **Player loads restore point via KSP's native load menu (not Parsek UI)**
    - The .sfs is a standard KSP save — KSP can load it directly.
    - `IsGoingBack` flag is NOT set → Parsek detects this as a normal revert.
    - Existing revert detection handles it: epoch increments, merge dialog may show if there's stale pending data in the .sfs.
    - In-memory recordings persist. Acceptable v1 behavior.

20. **GamePersistence.SaveGame fails**
    - Quicksave write fails (disk full, permissions, etc.).
    - Do NOT create the restore point metadata. Log error.
    - Player is not affected — commit already succeeded, only the restore point is skipped.
    - Log: `[Parsek][RestorePoint] Failed to create quicksave: {error}`

21. **Game load API fails**
    - Load fails (corrupted save, missing vessel, etc.).
    - Clear `IsGoingBack` and `GoBackUT` flags.
    - KSP may show its own error dialog. Log error.
    - Log: `[Parsek][RestorePoint] Failed to load restore point save: {error}`

22. **Going "forward" to a later restore point**
    - Player at UT 200, selects restore point at UT 400.
    - Loads the game state from UT 400 (on the pad before that mission launched).
    - Recordings before UT 400 spawn vessels immediately. Recordings after UT 400 replay as ghosts.
    - The game state reflects the original timeline at UT 400. This is inherent to snapshot-based restore.

23. **Wipe Recordings also wipes restore points**
    - When the player wipes all recordings, restore points are also deleted.
    - Restore points without recordings are useless — the timeline is empty.
    - Both the metadata file and all quicksave files are deleted.

24. **Delete a committed recording, then go back**
    - Recording removed from committed list → its cost freed.
    - Going back: the deleted recording is not in memory → not replayed.
    - Resource adjustment formula handles this: `currentReserved < savedReserved` → negative adjustment → funds increase.

25. **Go back to arbitrary UT (not a restore point)**
    - Not supported in v1. Player must use a restore point.
    - Workaround: go back to the nearest earlier restore point, then time-warp forward.
    - Future enhancement: timeline slider that loads nearest restore point + warps to target UT.

26. **Launch save orphaned (player quits without committing or discarding)**
    - `pendingLaunchSave` is static and not persisted to disk (except the .sfs file itself).
    - On next session start, the orphaned .sfs file remains on disk but `pendingLaunchSave` is null.
    - Acceptable v1 limitation — orphaned files are small and rare. Future: scan for unlinked `parsek_rp_*.sfs` files on load.

27. **New mission launched while previous launch save still pending**
    - Previous `pendingLaunchSave` quicksave file is deleted before creating the new one.
    - This handles the case where a player launches, discards mid-flight without using the UI, and launches again.

28. **Resource adjustment with partially-applied recordings**
    - At save time, some recordings may have LARI > -1 (partially played) or LARI = max (fully applied via budget deduction).
    - `ReservedAtSave` is computed via `ComputeTotalFullCost()` which ignores LARI — it always returns the full cost. After go-back, the same method is used. The comparison is always full-cost vs full-cost, so partial application state is irrelevant.
    - This is why `ComputeTotalFullCost()` exists: it provides a consistent baseline regardless of playback progress at save time.

29. **EVA child auto-commit while parent recording active**
    - EVA child auto-commits when the player boards back. `pendingLaunchSave` may exist from the parent mission's launch.
    - The root guard (step 2 in "Creating restore points") prevents the EVA commit from consuming the launch save. The parent mission's commit later consumes it.
    - Log: `[Parsek][RestorePoint] Skipping restore point for child recording {id} (ParentRecordingId={parentId})`

30. **Commit Flight during active tree recording**
    - Tree mode has its own commit path (`CommitTreeFlight`). The stationary vessel constraint applies equally: vessel must be in `PRELAUNCH`, `LANDED`, `SPLASHED`, or `ORBITING`.
    - Tree commit consumes the root's `pendingLaunchSave`, creating one restore point for the entire tree.

31. **Restore point label becomes stale after recording deletion**
    - RP_A's label says `"Flea Rocket" launch (3 recordings)`. Player deletes 2 recordings.
    - The label is frozen at creation time and becomes misleading. Acceptable v1 — labels are for human identification, not live status. `RecordingCount` is metadata, not an invariant.
    - The "future recordings" count in the confirmation dialog IS computed live, so the player gets accurate information before confirming.

32. **Go back, commit new recording, go back to older restore point**
    - Player at UT 500. Goes back to RP_A (UT 100). Launches D (UT 100-150). Commits D → RP_D created.
    - Goes back to RP_B (UT 250, from original timeline).
    - RP_B's quicksave has the original game state from UT 250 (before D existed).
    - Resource adjustment: `ComputeTotalFullCost(A,B,C,D) - ReservedAtSave_B(A's cost)` = B+C+D costs. Correctly accounts for D.
    - Game state (tech, facilities): from RP_B's snapshot. Any tech researched after going back to A is lost (inherent to snapshot restore). Action blocking prevents re-researching committed tech.

33. **Overlapping UT ranges from go-back**
    - Player commits A (UT 100-200), goes back to RP_A (UT 100), launches B (UT 100-300).
    - A and B overlap in UT range. Both ghosts play simultaneously from UT 100. This is expected — recordings are independent.
    - RP_A and RP_B both at UT 100 but with different save files (different game states). Both shown in picker.

34. **KSP crash mid-flight with pending launch save**
    - Launch save .sfs exists on disk, `pendingLaunchSave` lost (static field).
    - Same as edge case 26 (quit without committing), but crash-triggered. The .sfs file is orphaned.
    - The active recording is also lost (not saved).
    - Acceptable v1 limitation. Future: cleanup scan on load.

35. **Resource adjustment timing — Funding.Instance not yet available in OnLoad**
    - `Funding.Instance`, `ResearchAndDevelopment.Instance`, and `Reputation.Instance` may not be initialized during `OnLoad`.
    - The existing `ApplyBudgetDeductionWhenReady` solves this by waiting up to 120 frames. The go-back resource adjustment must use the same pattern: defer via `StartCoroutine` and wait for singletons before applying.
    - The adjustment logic is inline (not reusing `ApplyBudgetDeductionWhenReady`), but follows the same wait pattern.

36. **pendingLaunchSave across scene changes (launch → tracking station → new launch)**
    - Player launches (launch save captured), goes to tracking station.
    - `OnSceneChangeRequested`: active recording stashed as pending (or auto-committed if going to non-flight).
    - If auto-committed (CommitTreeSceneExit / ghost-only): `pendingLaunchSave` consumed → restore point created.
    - If stashed as pending (going to flight): `pendingLaunchSave` remains. Merge dialog on next flight ready, commit consumes it.
    - If player launches new vessel without committing pending: pending is still in RecordingStore, merge dialog shows first. Launch save for new vessel replaces the old one (edge case 27).

## What Doesn't Change

- Recording format — no new fields in Recording, TrajectoryPoint, or PartEvent
- Ghost visual building — GhostVisualBuilder unchanged
- Part event recording and playback — all 28 event types unchanged
- Engine/RCS/parachute/fairing FX — unchanged
- Merge dialog — unchanged (creates restore point after commit, but dialog logic itself is unchanged)
- Existing revert detection — still works for normal reverts (IsGoingBack check is upstream)
- Resource budget computation — `ComputeTotal` unchanged; new sibling method `ComputeTotalFullCost` added (same logic but ignores LARI/ResourcesApplied/LastReplayedEventIndex)
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

### Launch save capture
- `[Parsek][RestorePoint] Capturing launch save at UT {ut}: vessel "{vesselName}" in {situation} (save: {saveFile})`
- `[Parsek][RestorePoint] Launch save skipped: vessel in {situation} (not stable)`
- `[Parsek][RestorePoint] Replacing orphaned launch save: deleting {oldSaveFile}`
- `[Parsek][RestorePoint] Failed to capture launch save: {error}` (error path)

### Restore point creation (at commit time)
- `[Parsek][RestorePoint] Promoting launch save to restore point at UT {ut}: "{label}" (save: {saveFile})`
- `[Parsek][RestorePoint] No launch save for recording {id} — no restore point created`
- `[Parsek][RestorePoint] Skipping restore point for child recording {id} (ParentRecordingId={parentId})`
- `[Parsek][RestorePoint] Metadata saved: {count} restore points to {path}`

### Launch save cleanup
- `[Parsek][RestorePoint] Recording discarded — deleting launch save: {saveFile}`

### Go Back execution
- `[Parsek][RestorePoint] Go Back initiated to UT {ut} (restore point {id}, save: {saveFile})`
- `[Parsek][RestorePoint] Loading save file: {saveFile}`
- `[Parsek][RestorePoint] OnLoad: go-back detected, skipping .sfs recording/crew load (using {count} in-memory recordings)`
- `[Parsek][RestorePoint] OnLoad: resetting playback state for {standaloneCount} recordings + {treeCount} trees`
- `[Parsek][RestorePoint] OnLoad: epoch incremented to {epoch}`
- `[Parsek][RestorePoint] OnLoad: resource adjustment deferred (waiting for singletons)`
- `[Parsek][RestorePoint] Resource adjustment: reserved {savedReserved} → {currentReserved}, delta {delta}`
- `[Parsek][RestorePoint] Resource adjustment: marking {recCount} recordings + {treeCount} trees as fully applied`
- `[Parsek][RestorePoint] OnFlightReady: go-back complete at UT {ut}. Timeline: {recCount} recordings, {rpCount} restore points`

### Commit Flight constraint
- `[Parsek][UI] Commit Flight disabled: vessel situation {situation} (requires PRELAUNCH, LANDED, SPLASHED, or ORBITING)` (verbose)

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

8. **Launch save stable state check**
   - PRELAUNCH → capture. LANDED → capture. SPLASHED → capture. ORBITING → capture.
   - FLYING → skip. SUB_ORBITAL → skip. ESCAPING → skip.
   - Guards against: capturing launch saves from unstable states.

9. **Launch save lifecycle: capture → commit → promote**
   - Capture launch save → verify pendingLaunchSave exists → commit recording → verify RestorePoint created and pendingLaunchSave cleared.
   - Guards against: launch save not promoted on commit, or not cleared after promotion.

10. **Launch save lifecycle: capture → discard → cleanup**
    - Capture launch save → verify pendingLaunchSave exists → discard recording → verify pendingLaunchSave cleared.
    - Guards against: orphaned launch saves after discard.

11. **Launch save replacement on new mission**
    - Capture launch save A → start new recording → verify A's file deleted and new pendingLaunchSave exists.
    - Guards against: accumulating orphaned quicksave files.

12. **Resource adjustment calculation**
   - savedReserved = (15000, 5, 2). currentReserved = (25000, 10, 5). → adjustment = (10000, 5, 3).
   - savedReserved = (20000, 0, 0). currentReserved = (8000, 0, 0) (recording deleted). → adjustment = (-12000, 0, 0) — funds freed.
   - Guards against: incorrect deduction formula, double-deduction, sign errors.

### Integration tests

13. **Restore point metadata file round-trip**
    - Create 3 restore points → save to file → clear in-memory list → load from file → assert all 3 restored with correct values.
    - Guards against: file I/O bugs, ConfigNode structure errors.

14. **Restore point deletion cleanup**
    - Create restore point → delete → assert removed from list, metadata file updated.
    - Guards against: incomplete cleanup, stale entries in metadata file.

15. **Epoch increment on go-back**
    - Record initial epoch → call `OnLoadAfterGoBack()` → assert epoch incremented exactly once.
    - Guards against: missing or double epoch increment.

16. **Wipe recordings also wipes restore points**
    - Create recordings + restore points → wipe → assert both lists empty, all files deleted.
    - Guards against: orphaned restore points after wipe.

### Log assertion tests

17. **Launch save capture log**
    - Start recording from stable state → assert log contains `Capturing launch save at UT`.
    - Start recording from unstable state → assert log contains `Launch save skipped`.
    - Guards against: silent launch save capture/skip without diagnostic output.

18. **Restore point promotion log**
    - Commit recording with pending launch save → assert log contains `Promoting launch save to restore point`.
    - Commit recording without pending launch save → assert log contains `No launch save for recording`.
    - Guards against: silent restore point creation without diagnostic output.

19. **Go back state reset log**
    - Simulate go-back with 5 recordings → assert log contains `resetting playback state for 5 recordings`.
    - Guards against: state reset happening silently.

20. **Precondition failure log**
    - Set recording active → call CanGoBack → assert log contains `Go Back disabled: recording in progress`.
    - Guards against: precondition check without diagnostic output.

21. **Missing save file warning log**
    - Create restore point → remove the .sfs path from validation → assert log contains `Save file missing`.
    - Guards against: missing save file handled silently.

22. **Resource adjustment log**
    - Simulate go-back → assert log contains `resource adjustment: reserved {old} → {new}, delta {delta}`.
    - Guards against: resource adjustment without diagnostic output.

### Edge case tests

23. **No restore points → UI hidden**
    - Empty restore point list → assert `HasRestorePoints == false`.
    - Guards against: null reference on empty list, UI showing when it shouldn't.

24. **Restore point with missing save file**
    - Create restore point → mark save as missing → assert `IsAvailable == false` and picker shows it as unavailable.
    - Guards against: unhandled file-not-found, crash when selecting unavailable point.

25. **Multiple rapid go-backs**
    - Go back → reset state → go back again → assert state consistent after second go-back.
    - Guards against: stale flags from first go-back corrupting second.

26. **Restore points survive metadata save/load cycle**
    - Create 3 restore points → save file → simulate new session (reset initialLoadDone) → load file → assert all 3 preserved.
    - Guards against: metadata not persisted across game restarts.

27. **ComputeTotalFullCost vs ComputeTotal consistency**
    - Given 3 recordings all with LARI=-1, ResourcesApplied=false, LastReplayedEventIndex=-1.
    - Assert `ComputeTotalFullCost() == ComputeTotal()` (when nothing is applied, both should agree).
    - Then set LARI=max on one, ResourcesApplied=true on a tree. Assert `ComputeTotalFullCost()` still returns the full cost while `ComputeTotal()` returns less.
    - Guards against: `ComputeTotalFullCost` not ignoring application state correctly.

28. **Resource adjustment worked example end-to-end**
    - Create 2 recordings with known PreLaunchFunds and endpoint funds.
    - Set RP_A with ReservedAtSave = (0, 0, 0), RP_B with ReservedAtSave = (-4000, 0, 0).
    - Simulate go-back to RP_A: assert adjustment matches worked example in design doc.
    - Simulate go-back to RP_B: assert adjustment matches worked example in design doc.
    - Guards against: formula error in resource adjustment calculation.

29. **EVA child commit does not consume launch save**
    - Capture launch save → commit EVA child recording (ParentRecordingId != null) → assert pendingLaunchSave still exists.
    - Then commit root recording → assert pendingLaunchSave consumed and restore point created.
    - Guards against: EVA auto-commit stealing parent's launch save.

30. **Tree reset includes ResourcesApplied**
    - Given tree with ResourcesApplied=true and recordings with LARI=max.
    - After `ResetAllPlaybackState()`: assert tree.ResourcesApplied=false and all LARI=-1.
    - Guards against: tree costs excluded from budget after go-back.

31. **Future recordings count computed live**
    - Create RP at UT 100 with RecordingCount=3. Delete 2 recordings (1 before UT 100, 1 after).
    - Assert "future recordings" count = count of recordings with StartUT > 100, not `total - RecordingCount`.
    - Guards against: negative or stale future recordings display.

## Open Questions

1. **Should there be a maximum number of restore points?** v1: no limit. Monitor storage impact. Add configurable limit with auto-prune of oldest if needed.

2. **Should the player be able to create manual restore points?** v1: auto-only. Manual creation is a natural future enhancement (button in UI or hotkey).

3. **Exact KSP API for loading a save programmatically.** `GamePersistence.LoadGame` + `FlightDriver.StartAndFocusVessel` is the expected approach but needs verification during implementation. This triggers a FLIGHT→FLIGHT scene transition: `OnSceneChangeRequested(FLIGHT)` → all MonoBehaviours destroyed → `ParsekScenario.OnLoad` from loaded save → `ParsekFlight.Start` fresh → `OnFlightReady`. Other mods (e.g., quicksave mods) may provide reference implementations.

4. **Launch save performance deferral.** If the `GamePersistence.SaveGame` call at recording start causes a noticeable frame hitch, defer it by 2-3 frames via `StartCoroutine`. v1: try immediate save first, measure impact.
