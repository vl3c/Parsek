# Implementation Plan: Camera Follow for Ghost Vessels

Reference: `docs/design-camera-follow-ghost.md`

---

## Phase 1: Data Model + Core State Management

**Goal:** Add all watch-mode fields to `ParsekFlight`, implement `EnterWatchMode()` / `ExitWatchMode()` methods (without camera or UI calls yet), and wire index management into `DeleteRecording()`.

### Files to modify

**`Source/Parsek/ParsekFlight.cs`**

1. **Add fields** after the existing `#region State` block (after line ~118, near the other transient flight fields):

```csharp
// Camera follow (watch mode) — transient, never serialized
int watchedRecordingIndex = -1;       // -1 = not watching
string watchedRecordingId = null;     // stable across index shifts
Vessel savedCameraVessel = null;
float savedCameraDistance = 0f;
float savedCameraPitch = 0f;
float savedCameraHeading = 0f;
double watchEndHoldUntilUT = -1;      // non-looped end hold timer
```

2. **Add a public read-only property** for UI to check state:

```csharp
internal bool IsWatchingGhost => watchedRecordingIndex >= 0;
internal int WatchedRecordingIndex => watchedRecordingIndex;
```

3. **Add `EnterWatchMode(int index)` method** (new method, place near `TakeControlOfGhost` around line ~5744):
   - Validate: `index >= 0`, `index < CommittedRecordings.Count`
   - Validate: `ghostStates[index]` exists and `.ghost != null`
   - Validate: ghost is on same celestial body as `FlightGlobals.ActiveVessel` (compare `ghostStates[index].lastInterpolatedBodyName` against `FlightGlobals.ActiveVessel.mainBody.name`)
   - Validate: `!CommittedRecordings[index].TakenControl`
   - If already watching a different recording, call `ExitWatchMode(skipCameraRestore: true)` first (switch case)
   - Set `watchedRecordingIndex = index`, `watchedRecordingId = CommittedRecordings[index].RecordingId`
   - Save camera state: `savedCameraVessel`, distance, pitch, heading (actual camera calls deferred to Phase 2)
   - Clear `watchEndHoldUntilUT = -1`
   - Log entry (deferred to Phase 6, placeholder comment for now)

4. **Add `ExitWatchMode(bool skipCameraRestore = false)` method:**
   - If `watchedRecordingIndex < 0`, return (already not watching)
   - Restore camera (deferred to Phase 2) unless `skipCameraRestore`
   - Remove control locks (deferred to Phase 4)
   - Reset: `watchedRecordingIndex = -1`, `watchedRecordingId = null`, `savedCameraVessel = null`, `watchEndHoldUntilUT = -1`

5. **Add index management to `DeleteRecording(int index)`** (modify existing method around line ~4518):
   - After `RecordingStore.RemoveRecordingAt(index)` and before/during the ghostStates key rebuild, add:
     ```
     if (watchedRecordingIndex == index)
         ExitWatchMode();
     else if (watchedRecordingIndex > index)
         watchedRecordingIndex--;
     // After adjustment, verify watchedRecordingId matches
     if (watchedRecordingIndex >= 0)
     {
         var c = RecordingStore.CommittedRecordings;
         if (watchedRecordingIndex >= c.Count ||
             c[watchedRecordingIndex].RecordingId != watchedRecordingId)
         {
             // Scan for correct index
             int found = -1;
             for (int j = 0; j < c.Count; j++)
                 if (c[j].RecordingId == watchedRecordingId) { found = j; break; }
             if (found < 0) ExitWatchMode();
             else watchedRecordingIndex = found;
         }
     }
     ```
   - This block must go **before** the ghostStates key rebuild (line ~4521), since `ExitWatchMode` may reference `ghostStates`.

6. **Add `IsVesselSituationSafe(Vessel.Situations situation, double periapsis, double atmosphereAltitude)` static method:**
   - Pure static, `internal static`, testable without Unity.
   - Returns `true` for `LANDED`, `SPLASHED`, `PRELAUNCH`, `DOCKED`.
   - Returns `true` for `ORBITING` only if `periapsis > atmosphereAltitude`.
   - Returns `false` for `FLYING`, `SUB_ORBITAL`, `ESCAPING`.
   - Returns `false` for `ORBITING` with `periapsis <= atmosphereAltitude`.

7. **Wire cleanup into `OnDestroy()` and `OnSceneChangeRequested()`:**
   - In `OnDestroy()` (line ~701), before `DestroyAllTimelineGhosts()`: call `ExitWatchMode()`.
   - In `OnSceneChangeRequested()` (line ~748), near the top: call `ExitWatchMode()`.

### Verification

- **Unit tests:** Create `Source/Parsek.Tests/CameraFollowTests.cs` with:
  - `WatchModeIndex_EnterSetsIndex`: Create committed recordings, call logic that sets `watchedRecordingIndex`, verify it transitions from -1 to the index.
  - `WatchModeIndex_ExitClearsIndex`: Verify transitions back to -1.
  - `WatchModeIndex_DeleteBelowShiftsDown`: Set watched index to 3, simulate delete at 1, verify index becomes 2.
  - `WatchModeIndex_DeleteWatchedExits`: Set watched index to 2, simulate delete at 2, verify index becomes -1.
  - `WatchModeIndex_DeleteAboveNoChange`: Set watched index to 1, simulate delete at 3, verify index stays 1.
  - `IsVesselSituationSafe_AllCases`: Test all `Vessel.Situations` enum values per the design doc.
  - `WatchedRecordingId_StableAcrossDeletion`: Verify `watchedRecordingId` stays the same string after index shift.
- **Build:** `dotnet build` succeeds.

### Dependencies
None (first phase).

---

## Phase 2: Camera Integration

**Goal:** Wire the FlightCamera API into `EnterWatchMode()` and `ExitWatchMode()`. Add the `onVesselChange` guard to re-target camera.

### Files to modify

**`Source/Parsek/ParsekFlight.cs`**

1. **In `EnterWatchMode()`**, after setting fields, add camera calls:
   ```csharp
   savedCameraVessel = FlightGlobals.ActiveVessel;
   savedCameraDistance = FlightCamera.fetch.Distance;
   savedCameraPitch = FlightCamera.fetch.camPitch;
   savedCameraHeading = FlightCamera.fetch.camHdg;

   FlightCamera.fetch.SetTargetTransform(ghostStates[index].ghost.transform);
   FlightCamera.fetch.distance = 50f;  // override [75,400] clamp
   ```

2. **In `ExitWatchMode()`**, before clearing fields (unless `skipCameraRestore`):
   ```csharp
   if (!skipCameraRestore)
   {
       if (savedCameraVessel != null && savedCameraVessel.gameObject != null)
       {
           FlightCamera.fetch.SetTargetVessel(savedCameraVessel);
           FlightCamera.fetch.distance = savedCameraDistance;
           FlightCamera.fetch.camPitch = savedCameraPitch;
           FlightCamera.fetch.camHdg = savedCameraHeading;
       }
       else
       {
           FlightCamera.fetch.SetTargetVessel(FlightGlobals.ActiveVessel);
       }
   }
   ```

3. **Add `onVesselChange` guard in `OnVesselSwitchComplete(Vessel newVessel)`** (line ~1071):
   - At the very top of the method (before the `if (activeTree == null) return;` check), add:
     ```csharp
     if (watchedRecordingIndex >= 0)
     {
         // Re-target camera to ghost — KSP reparents pivot on vessel switch
         GhostPlaybackState ws;
         if (ghostStates.TryGetValue(watchedRecordingIndex, out ws) && ws.ghost != null)
             FlightCamera.fetch.SetTargetTransform(ws.ghost.transform);
         // Log (Phase 6)
         return;  // Don't process tree vessel-switch logic while watching
     }
     ```
   - The early return prevents tree mode's vessel-switch handler from interfering.

4. **Add per-frame ghost validity check in `UpdateTimelinePlayback()`** (after line ~3982, at the end of the method):
   ```csharp
   // Watch mode: verify ghost still exists
   if (watchedRecordingIndex >= 0)
   {
       GhostPlaybackState ws;
       bool ghostOk = ghostStates.TryGetValue(watchedRecordingIndex, out ws)
                       && ws != null && ws.ghost != null;
       if (!ghostOk)
           ExitWatchMode();
   }
   ```

5. **Handle non-looped recording end while watching.** In the `UpdateTimelinePlayback()` main loop, in the `pastChainEnd && needsSpawn && ghostActive` branch (line ~3942):
   - After `VesselSpawner.SpawnOrRecoverIfTooClose(rec, i)` and before `DestroyTimelineGhost(i)`, add:
     ```csharp
     if (watchedRecordingIndex == i)
     {
         ExitWatchMode();
         // Switch player to spawned vessel
         StartCoroutine(DeferredActivateVessel(rec.SpawnedVesselPersistentId));
     }
     ```
   - In the `else` branch (line ~3970, "Outside time range, no spawn needed"), before `DestroyTimelineGhost(i)`, add:
     ```csharp
     if (watchedRecordingIndex == i)
     {
         // Non-looped end without spawn: hold camera for 3 seconds
         if (watchEndHoldUntilUT < 0)
         {
             watchEndHoldUntilUT = Planetarium.GetUniversalTime() + 3.0;
             // Keep ghost alive during hold — skip DestroyTimelineGhost
             continue;
         }
         else if (Planetarium.GetUniversalTime() < watchEndHoldUntilUT)
         {
             // Still holding — keep ghost alive
             continue;
         }
         else
         {
             // Hold expired
             ExitWatchMode();
         }
     }
     ```
   - This requires restructuring the `else` block slightly. The ghost-only non-looped end case is in the final `else` at line 3970. The `continue` skips the `DestroyTimelineGhost` call.

6. **Add Backspace key handling in `HandleInput()`** (after line ~3085):
   ```csharp
   if (watchedRecordingIndex >= 0 && Input.GetKeyDown(KeyCode.Backspace))
   {
       ExitWatchMode();
   }
   ```

7. **Handle `onVesselWillDestroy` for saved camera vessel.** In `OnVesselWillDestroy(Vessel v)` (line ~926), add near the top:
   ```csharp
   if (watchedRecordingIndex >= 0 && savedCameraVessel != null && v == savedCameraVessel)
   {
       savedCameraVessel = null;
       // Log (Phase 6)
   }
   ```

### Verification

- **Manual in-game test:** Launch vessel on pad. Spawn a looped ghost (or wait for timeline ghost). Open Parsek window. (Watch button added in Phase 3 -- for now, add a temporary debug key like `F12` that calls `EnterWatchMode(0)`.) Camera should move to ghost. Press Backspace -- camera returns to vessel.
- **Build:** `dotnet build` succeeds.

### Dependencies
Phase 1 (fields and methods must exist).

---

## Phase 3: UI (Watch Button + On-Screen Label)

**Goal:** Add the Watch button to each recording row, the on-screen "Watching" label, and the flight warning tooltip.

### Files to modify

**`Source/Parsek/ParsekUI.cs`**

1. **Add column width constant** (near line ~63):
   ```csharp
   private const float ColW_Watch = 40f;
   ```

2. **Add Watch button to `DrawRecordingRow()`** (after the Loop Period cell at line ~1118, before the Delete button at line ~1121):
   ```csharp
   // Watch button
   bool hasGhost = flight.HasActiveGhost(ri);
   bool sameBody = flight.IsGhostOnSameBody(ri);
   bool isWatching = flight.WatchedRecordingIndex == ri;
   bool canWatch = hasGhost && sameBody && !committed[ri].TakenControl;

   GUI.enabled = canWatch;
   string watchLabel = isWatching ? "W*" : "W";
   if (GUILayout.Button(watchLabel, GUILayout.Width(ColW_Watch)))
   {
       if (isWatching)
           flight.ExitWatchMode();
       else
           flight.EnterWatchMode(ri);
   }
   if (!canWatch && hasGhost && !sameBody)
   {
       // Tooltip explains why disabled (different body)
       // Use existing tooltip pattern from DrawRecordingTooltip
   }
   GUI.enabled = flight.CanDeleteRecording;  // restore for delete button below
   ```

3. **Add Watch column header** in the header row (around line ~870, near the Loop header):
   ```csharp
   GUILayout.Label("W", GUILayout.Width(ColW_Watch));
   ```

4. **Add on-screen watch label.** In `ParsekFlight.OnGUI()` (line ~680), before the `if (showUI)` block:
   ```csharp
   if (watchedRecordingIndex >= 0)
       DrawWatchModeOverlay();
   ```

5. **Add `DrawWatchModeOverlay()` method** in `ParsekFlight`:
   ```csharp
   private GUIStyle watchOverlayStyle;
   private GUIStyle watchOverlayHintStyle;

   void DrawWatchModeOverlay()
   {
       if (watchOverlayStyle == null)
       {
           watchOverlayStyle = new GUIStyle(GUI.skin.label)
           {
               fontSize = 16,
               fontStyle = FontStyle.Bold,
               alignment = TextAnchor.UpperCenter,
               normal = { textColor = Color.white }
           };
           watchOverlayHintStyle = new GUIStyle(GUI.skin.label)
           {
               fontSize = 12,
               alignment = TextAnchor.UpperCenter,
               normal = { textColor = new Color(1f, 1f, 1f, 0.7f) }
           };
       }

       string vesselName = "";
       var committed = RecordingStore.CommittedRecordings;
       if (watchedRecordingIndex < committed.Count)
           vesselName = committed[watchedRecordingIndex].VesselName;

       float boxW = 300f, boxH = 50f;
       float x = (Screen.width - boxW) / 2f;
       float y = 10f;
       Rect bgRect = new Rect(x, y, boxW, boxH);

       GUI.color = new Color(0f, 0f, 0f, 0.5f);
       GUI.DrawTexture(bgRect, Texture2D.whiteTexture);
       GUI.color = Color.white;

       GUI.Label(new Rect(x, y + 5, boxW, 22f), $"Watching: {vesselName}", watchOverlayStyle);
       GUI.Label(new Rect(x, y + 27, boxW, 18f), "[Backspace] Return to vessel", watchOverlayHintStyle);
   }
   ```

6. **Add flight warning.** In `EnterWatchMode()`, before saving camera state:
   ```csharp
   var av = FlightGlobals.ActiveVessel;
   if (av != null)
   {
       double pe = av.orbit?.PeA ?? 0;
       double atmoHeight = av.mainBody?.atmosphereDepth ?? 0;
       if (!IsVesselSituationSafe(av.situation, pe, atmoHeight))
       {
           ParsekLog.ScreenMessage("Your vessel continues unattended", 3f);
       }
   }
   ```

**`Source/Parsek/ParsekFlight.cs`**

7. **Add helper methods** exposed to ParsekUI:
   ```csharp
   internal bool HasActiveGhost(int index)
   {
       GhostPlaybackState s;
       return ghostStates.TryGetValue(index, out s) && s != null && s.ghost != null;
   }

   internal bool IsGhostOnSameBody(int index)
   {
       GhostPlaybackState s;
       if (!ghostStates.TryGetValue(index, out s) || s == null) return false;
       string ghostBody = s.lastInterpolatedBodyName;
       string activeBody = FlightGlobals.ActiveVessel?.mainBody?.name;
       if (string.IsNullOrEmpty(ghostBody) || string.IsNullOrEmpty(activeBody)) return false;
       return ghostBody == activeBody;
   }
   ```

### Verification

- **Manual in-game test:** Open Parsek recordings window. Each recording with an active ghost on the same body shows an enabled "W" button. Recordings without ghosts or on different bodies show a disabled button. Clicking "W" enters watch mode -- button changes to "W*", overlay appears at top center.
- **Build:** `dotnet build` succeeds.

### Dependencies
Phase 1 (data model), Phase 2 (camera integration).

---

## Phase 4: Input Blocking

**Goal:** Block staging, throttle, vessel switching, EVA, and camera modes while in watch mode.

### Files to modify

**`Source/Parsek/ParsekFlight.cs`**

1. **Define the lock mask constant** (near the watch mode fields):
   ```csharp
   private const string WatchModeLockId = "ParsekWatch";
   private const ControlTypes WatchModeLockMask =
       ControlTypes.STAGING | ControlTypes.THROTTLE |
       ControlTypes.VESSEL_SWITCHING | ControlTypes.EVA |
       ControlTypes.CAMERAMODES;
   ```

2. **Set control lock in `EnterWatchMode()`** (after camera targeting):
   ```csharp
   InputLockManager.SetControlLock(WatchModeLockMask, WatchModeLockId);
   ```

3. **Remove control lock in `ExitWatchMode()`** (before clearing fields):
   ```csharp
   InputLockManager.RemoveControlLock(WatchModeLockId);
   ```

4. **Safety net in `OnDestroy()`** -- ensure lock is removed even if `ExitWatchMode()` somehow didn't run:
   ```csharp
   InputLockManager.RemoveControlLock(WatchModeLockId);
   ```
   Place this right after the `ExitWatchMode()` call, as a belt-and-suspenders cleanup.

5. **Safety net in `OnSceneChangeRequested()`** -- same:
   ```csharp
   InputLockManager.RemoveControlLock(WatchModeLockId);
   ```

### Verification

- **Manual in-game test:** Enter watch mode. Press spacebar -- no staging. Press `[`/`]` -- no vessel switch. Press C -- no IVA mode change. Press Backspace -- exit watch mode. Press spacebar -- staging works again.
- **Unit test** in `CameraFollowTests.cs`:
  - `ControlLockLogLine_SetOnEnter`: Use `ParsekLog.TestSinkForTesting` to capture logs. Call enter logic. Verify `[VERBOSE][CameraFollow] InputLockManager control lock "ParsekWatch" set` line is emitted (this test is for Phase 6, but the structure is set here).
- **Build:** `dotnet build` succeeds.

### Dependencies
Phase 1 (enter/exit methods).

---

## Phase 5: Edge Case Handling

**Goal:** Handle vessel destruction, docking, ghost destruction, non-looped end hold timer, and loop restart gracefully.

### Files to modify

**`Source/Parsek/ParsekFlight.cs`**

1. **E1: Ghost destroyed while watching** -- Already handled by the per-frame check added in Phase 2 (at end of `UpdateTimelinePlayback()`). Also, when a recording is disabled via the checkbox (`rec.PlaybackEnabled = false`), the ghost is destroyed in `UpdateTimelinePlayback()` at line ~3797. The per-frame watch-mode validity check will catch this on the next frame. No additional code needed.

2. **E2: Active vessel destroyed while watching** -- Already handled in Phase 2 via `OnVesselWillDestroy` nulling `savedCameraVessel`. The `ExitWatchMode()` fallback to `FlightGlobals.ActiveVessel` handles this. However, add explicit exit if the active vessel is destroyed:
   ```csharp
   // In OnVesselWillDestroy, after nulling savedCameraVessel:
   if (watchedRecordingIndex >= 0 && v == FlightGlobals.ActiveVessel)
   {
       // Vessel died while we're watching ghost -- exit watch mode
       // KSP will assign a new ActiveVessel or show revert dialog
       ExitWatchMode();
   }
   ```

3. **E3: Scene change** -- Already handled in Phase 1 via `OnSceneChangeRequested()` and `OnDestroy()`.

4. **E10: Vessel spawns at end of recording while watching** -- Already handled in Phase 2 (the `pastChainEnd && needsSpawn && ghostActive` branch).

5. **E14: Vessel switch keys / docking while watching** -- The `OnVesselSwitchComplete` guard from Phase 2 handles re-targeting. Additionally, detect docking specifically. In `OnPartCouple` (find existing handler):
   ```csharp
   // At the top of OnPartCouple, add:
   if (watchedRecordingIndex >= 0)
   {
       // Log docking while watching (Phase 6)
   }
   ```
   No special action needed beyond the existing `OnVesselSwitchComplete` guard -- the camera re-targets to the ghost after the vessel switch.

6. **E15: Docking makes savedCameraVessel stale** -- The `OnVesselWillDestroy` handler already nulls `savedCameraVessel` if the saved vessel is destroyed. Docking destroys the absorbed vessel (fires `onVesselWillDestroy`). If `savedCameraVessel` was the absorbed vessel, it gets nulled. The fallback in `ExitWatchMode()` uses `FlightGlobals.ActiveVessel` when `savedCameraVessel` is null. This is correct.

7. **Non-looped end hold timer** -- Already implemented in Phase 2. Refine: in `UpdateLoopingTimelinePlayback()`, verify that loop restart while watching works. The loop code destroys and respawns the ghost each cycle (line ~4059-4064). The per-frame validity check in `UpdateTimelinePlayback()` runs after the loop update, so during the destroy-respawn window, the ghost is immediately rebuilt. No additional handling needed -- the camera pivot is parented to the ghost Transform, and when the ghost is rebuilt with the same name, `SetTargetTransform` must be re-called. Add a re-target after loop ghost rebuild:
   ```csharp
   // In UpdateLoopingTimelinePlayback, after SpawnTimelineGhost (line ~4069):
   if (watchedRecordingIndex == recIdx)
   {
       // Ghost was rebuilt for new loop cycle -- re-target camera
       FlightCamera.fetch.SetTargetTransform(state.ghost.transform);
   }
   ```

8. **E13: Quickload** -- Scene reload fires `onGameSceneLoadRequested`, which calls `ExitWatchMode()`. Covered.

### Verification

- **Manual in-game tests** (from design doc test plan items 14-19):
  - Watch a ghost, disable the recording checkbox. Camera auto-returns.
  - Watch a ghost while vessel is suborbital. Let vessel crash. Revert dialog appears.
  - Watch ghost #3, delete #1. Camera stays on correct ghost (now #2).
  - Watch mode, press spacebar. No staging.
  - Watch mode, press `[`/`]`. Camera stays on ghost.
  - Watch mode, press M. Map opens. Close map. Camera on ghost.
- **Build:** `dotnet build` succeeds.

### Dependencies
Phase 2 (camera integration), Phase 4 (input blocking).

---

## Phase 6: Diagnostic Logging

**Goal:** Add all log lines specified in the design doc under the `CameraFollow` subsystem.

### Files to modify

**`Source/Parsek/ParsekFlight.cs`**

Add logging calls at each location. All use `ParsekLog.Info("CameraFollow", ...)`, `ParsekLog.Warn("CameraFollow", ...)`, or `ParsekLog.Verbose("CameraFollow", ...)`.

1. **`EnterWatchMode()` -- entry log:**
   ```csharp
   var gs = ghostStates[index];
   string body = gs.lastInterpolatedBodyName ?? "?";
   double alt = gs.lastInterpolatedAltitude;
   ParsekLog.Info("CameraFollow",
       $"Entering watch mode for recording #{index} \"{rec.VesselName}\" " +
       $"-- ghost at alt {alt:F0}m on {body}");
   ```
   For lat/lon, use the ghost Transform position with the body's `GetLatitude`/`GetLongitude` if available, or omit if body reference is not accessible (the design shows lat/lon but the ghost state doesn't cache them -- altitude and body are sufficient).

2. **`ExitWatchMode()` -- exit log (manual):**
   ```csharp
   string targetName = savedCameraVessel != null ? savedCameraVessel.vesselName
       : (FlightGlobals.ActiveVessel?.vesselName ?? "unknown");
   ParsekLog.Info("CameraFollow",
       $"Exiting watch mode for recording #{watchedRecordingIndex} " +
       $"\"{RecordingStore.CommittedRecordings[watchedRecordingIndex].VesselName}\" " +
       $"-- returning to {targetName}");
   ```

3. **Ghost destroyed auto-exit** (in the per-frame validity check):
   ```csharp
   ParsekLog.Warn("CameraFollow",
       $"Watched ghost #{watchedRecordingIndex} destroyed -- auto-exiting watch mode");
   ```

4. **Recording deleted auto-exit** (in `DeleteRecording`):
   ```csharp
   ParsekLog.Warn("CameraFollow",
       $"Watched recording \"{watchedRecordingId}\" deleted -- auto-exiting watch mode");
   ```

5. **Scene change exit** (in `OnSceneChangeRequested`):
   ```csharp
   if (watchedRecordingIndex >= 0)
       ParsekLog.Info("CameraFollow", "Watch mode cleared on scene change");
   ```

6. **Active vessel destroyed** (in `OnVesselWillDestroy`):
   ```csharp
   ParsekLog.Warn("CameraFollow",
       $"Active vessel destroyed while watching ghost #{watchedRecordingIndex} " +
       $"-- savedCameraVessel is null, falling back to FlightGlobals.ActiveVessel");
   ```

7. **Switch ghost:**
   ```csharp
   ParsekLog.Info("CameraFollow",
       $"Switching watch from #{watchedRecordingIndex} to #{index} \"{newVesselName}\"");
   ```
   Place this in `EnterWatchMode()` before calling `ExitWatchMode(skipCameraRestore: true)` on the old ghost.

8. **Vessel switch intercepted** (in `OnVesselSwitchComplete` guard):
   ```csharp
   ParsekLog.Verbose("CameraFollow",
       $"onVesselChange fired while watching -- re-targeting camera to ghost #{watchedRecordingIndex}");
   ```

9. **Non-looped end hold started:**
   ```csharp
   ParsekLog.Info("CameraFollow",
       $"Recording #{i} ended -- holding camera at last position until UT {watchEndHoldUntilUT:F1}");
   ```

10. **Hold expired:**
    ```csharp
    ParsekLog.Info("CameraFollow",
        $"Hold expired for recording #{watchedRecordingIndex} -- returning to active vessel");
    ```

11. **Vessel spawn while watching:**
    ```csharp
    ParsekLog.Info("CameraFollow",
        $"Recording #{i} spawned vessel pid={rec.SpawnedVesselPersistentId} -- switching active vessel");
    ```

12. **Flight warning shown:**
    ```csharp
    ParsekLog.Verbose("CameraFollow",
        $"Showing flight warning -- active vessel situation: {av.situation}");
    ```

13. **Button state decision (verbose, rate-limited):**
    ```csharp
    ParsekLog.Verbose("CameraFollow",
        $"Watch button for #{index}: enabled={canWatch} (ghostExists={hasGhost}, sameBody={sameBody})");
    ```
    This should NOT be in the per-frame UI draw. If needed at all, use `VerboseRateLimited` or only log on state change.

14. **Camera target set / restored:**
    ```csharp
    ParsekLog.Verbose("CameraFollow",
        $"FlightCamera.SetTargetTransform on ghost #{index} at {ghost.transform.position}, distance={FlightCamera.fetch.distance:F1}");
    ParsekLog.Verbose("CameraFollow",
        $"FlightCamera.SetTargetVessel restored to {vesselName}, distance={savedCameraDistance:F1}");
    ```

15. **Control lock set/removed:**
    ```csharp
    ParsekLog.Verbose("CameraFollow",
        $"InputLockManager control lock \"{WatchModeLockId}\" set: {WatchModeLockMask}");
    ParsekLog.Verbose("CameraFollow",
        $"InputLockManager control lock \"{WatchModeLockId}\" removed");
    ```

16. **Index shifted on delete:**
    ```csharp
    ParsekLog.Info("CameraFollow",
        $"Recording deleted at #{index} -- watchedRecordingIndex adjusted from {old} to {watchedRecordingIndex}");
    ```

17. **Index recomputed from ID:**
    ```csharp
    ParsekLog.Warn("CameraFollow",
        $"watchedRecordingIndex stale -- recomputed from recordingId \"{watchedRecordingId}\": {old} -> {found}");
    ```

### Verification

- **Log assertion tests** in `CameraFollowTests.cs` (using `ParsekLog.TestSinkForTesting`):
  - Each test sets up the test sink, calls the method that should log, and asserts the captured log lines contain the expected `[CameraFollow]` subsystem tag and level.
- **Build + grep KSP.log:** After in-game testing, `grep "[CameraFollow]"` in KSP.log should show all transitions.

### Dependencies
Phases 1-5 (all methods must exist to add logging to them).

---

## Phase 7: Tests

**Goal:** Implement all unit tests and log assertion tests specified in the design doc, plus the manual test checklist.

### Files to create/modify

**Create `Source/Parsek.Tests/CameraFollowTests.cs`**

```csharp
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class CameraFollowTests
    {
        public CameraFollowTests()
        {
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }
        // ... tests below
    }
}
```

### Unit tests (pure logic, no Unity)

1. **`IsVesselSituationSafe_Landed_ReturnsTrue`** -- call `ParsekFlight.IsVesselSituationSafe(Vessel.Situations.LANDED, 0, 70000)`, assert true.

2. **`IsVesselSituationSafe_Splashed_ReturnsTrue`** -- `SPLASHED` -> true.

3. **`IsVesselSituationSafe_Prelaunch_ReturnsTrue`** -- `PRELAUNCH` -> true.

4. **`IsVesselSituationSafe_Docked_ReturnsTrue`** -- `DOCKED` -> true.

5. **`IsVesselSituationSafe_Orbiting_SafePeriapsis_ReturnsTrue`** -- `ORBITING` with pe=100000, atmo=70000 -> true.

6. **`IsVesselSituationSafe_Orbiting_LowPeriapsis_ReturnsFalse`** -- `ORBITING` with pe=50000, atmo=70000 -> false.

7. **`IsVesselSituationSafe_Flying_ReturnsFalse`** -- `FLYING` -> false.

8. **`IsVesselSituationSafe_SubOrbital_ReturnsFalse`** -- `SUB_ORBITAL` -> false.

9. **`IsVesselSituationSafe_Escaping_ReturnsFalse`** -- `ESCAPING` -> false.

10. **`WatchIndexManagement_DeleteBelow_ShiftsDown`** -- Pure index arithmetic test. Given `watchedRecordingIndex = 3, watchedRecordingId = "abc"`, simulate delete at index 1. Verify result is index 2, id still "abc". (Test the static index-management logic extracted into a testable helper.)

11. **`WatchIndexManagement_DeleteAtWatched_ExitsToMinusOne`** -- Delete at index 3 when watched is 3. Verify index becomes -1.

12. **`WatchIndexManagement_DeleteAbove_NoChange`** -- Delete at index 5 when watched is 3. Verify index stays 3.

13. **`WatchIndexManagement_IdStableAfterShift`** -- Set up a list of recording IDs, delete one below watched. Verify the recording at the new index has the correct ID.

### Log assertion tests

14. **`EnterWatchMode_EmitsInfoLog`** -- Set up `ParsekLog.TestSinkForTesting`, trigger enter logic, assert log contains `[INFO][CameraFollow] Entering watch mode`.

15. **`ExitWatchMode_EmitsInfoLog`** -- Same for `[INFO][CameraFollow] Exiting watch mode`.

16. **`GhostDestroyed_EmitsWarnLog`** -- Trigger ghost validity failure, assert `[WARN][CameraFollow] Watched ghost`.

17. **`IndexShifted_EmitsInfoLog`** -- Delete below watched, assert `[INFO][CameraFollow] Recording deleted`.

18. **`ControlLockSet_EmitsVerboseLog`** -- Enter watch mode, assert `[VERBOSE][CameraFollow] InputLockManager control lock "ParsekWatch" set`.

19. **`ControlLockRemoved_EmitsVerboseLog`** -- Exit watch mode, assert `[VERBOSE][CameraFollow] InputLockManager control lock "ParsekWatch" removed`.

### Implementation notes for testability

- **Extract `ComputeWatchIndexAfterDelete(int watchedIndex, string watchedId, int deletedIndex, List<RecordingStore.Recording> recordings)` as `internal static`** method in `ParsekFlight.cs`. This returns a tuple `(int newIndex, string newId)` where newIndex is -1 if watch mode should exit. This makes tests 10-13 pure and trivially testable.

- **`IsVesselSituationSafe`** is already `internal static` from Phase 1.

- **Log assertion tests** will call the extracted static methods plus use `ParsekLog.TestSinkForTesting` / `ParsekLog.VerboseOverrideForTesting = true` / `ParsekLog.SuppressLogging = false` to capture output. They'll need to construct minimal `Recording` objects with just `RecordingId` and `VesselName` set.

### Manual test checklist

(Copied from design doc test plan, items 9-21. No code changes needed -- this is the QA pass.)

- [ ] 9. Basic watch and return (pad vessel, looped ghost)
- [ ] 10. Watch during time warp (orbiting ghost, 4x warp)
- [ ] 11. Loop restart (short looped recording)
- [ ] 12. Non-looped end with vessel spawn (E10)
- [ ] 13. Non-looped end without spawn (3-sec hold)
- [ ] 14. Ghost destroyed while watching (disable checkbox)
- [ ] 15. Active vessel destroyed while watching
- [ ] 16. Recording deleted while watching (delete other, then delete watched)
- [ ] 17. Staging blocked (spacebar in watch mode)
- [ ] 18. Vessel switch blocked (`[`/`]` in watch mode)
- [ ] 19. Map view while watching (M key toggle)
- [ ] 20. Reentry capsule (close-up reentry view)
- [ ] 21. Quickload while watching (F9 in watch mode)

### Verification

- `dotnet test` -- all new tests pass.
- `dotnet test --filter CameraFollow` -- runs only the new test class.
- All 408 existing tests still pass.

### Dependencies
Phase 6 (logging must be in place for log assertion tests).

---

## Summary: File Change Map

| File | Phases | Changes |
|------|--------|---------|
| `Source/Parsek/ParsekFlight.cs` | 1-6 | Watch mode fields, `EnterWatchMode`, `ExitWatchMode`, `IsVesselSituationSafe`, `HasActiveGhost`, `IsGhostOnSameBody`, `ComputeWatchIndexAfterDelete`, `DrawWatchModeOverlay`, camera integration in enter/exit, `onVesselChange` guard, per-frame validity check, hold timer in `UpdateTimelinePlayback`, Backspace in `HandleInput`, control locks, cleanup in `OnDestroy`/`OnSceneChangeRequested`, index management in `DeleteRecording`, all diagnostic logging |
| `Source/Parsek/ParsekUI.cs` | 3 | `ColW_Watch` constant, Watch button in `DrawRecordingRow`, column header |
| `Source/Parsek.Tests/CameraFollowTests.cs` | 7 | New test file: 19 tests (9 situation classification, 4 index management, 6 log assertions) |

## Implementation Order

```
Phase 1 ──► Phase 2 ──► Phase 3 ──► Phase 5
                │                      │
                └──► Phase 4 ──────────┘
                                       │
                                       ▼
                                   Phase 6 ──► Phase 7
```

Phases 3 and 4 can be done in parallel after Phase 2. Phase 5 depends on 2 and 4. Phase 6 depends on all prior phases. Phase 7 is the final verification phase.
