# Design: Camera Follow for Ghost Vessels

## Problem
Ghost recordings playing at high altitude or far from the player's vessel are invisible from the launchpad camera. The player has no way to observe a ghost's reentry trail, orbital path, or distant maneuver up close. The only perspective available is wherever the active vessel happens to be.

## Terminology
- **Watch mode**: Camera is anchored on a ghost vessel instead of the active vessel. The active vessel continues flying unattended.
- **Watched ghost**: The ghost currently being followed by the camera. At most one at a time.

## Mental Model
The player opens the Parsek recordings window, sees a list of recordings, and for each one that has an active ghost (currently playing), there's a Watch button. Clicking it moves the standard KSP flight camera to orbit the ghost, exactly like it orbits a normal vessel. Clicking the button again (or clicking Watch on a different recording) returns to the active vessel. The player's vessel keeps flying - this is purely a camera operation.

```
  Normal mode              Watch mode                  Return
  ┌──────────┐   click    ┌──────────────┐   click    ┌──────────┐
  │ Camera on │──Watch───▶│ Camera on    │──Watch───▶│ Camera on │
  │ active    │  button   │ ghost vessel │  button   │ active    │
  │ vessel    │           │              │  (toggle) │ vessel    │
  └──────────┘           └──────────────┘           └──────────┘
       │                        │                         │
       │  vessel keeps          │  vessel keeps           │
       │  flying normally       │  flying normally        │
       └────────────────────────┴─────────────────────────┘
```

## KSP FlightCamera API (verified from decompiled Assembly-CSharp.dll)

The camera system supports four target modes via the `TargetMode` enum: `None`, `Vessel`, `Part`, `Transform`.

Key API surface:
- `FlightCamera.fetch` - singleton instance
- `FlightCamera.SetTarget(Transform t)` - static, calls `fetch.SetTargetTransform(t)`
- `FlightCamera.fetch.SetTargetTransform(Transform t)` - sets `targetMode = TargetMode.Transform`, parents camera pivot to the target, zeroes endPos
- `FlightCamera.fetch.SetTargetVessel(Vessel v)` - restores normal vessel-tracking mode
- `FlightCamera.fetch.distance` / `camHdg` / `camPitch` - camera orbit state (save/restore for transitions)
- `FlightCamera.fetch.Distance` - read-only accessor

Gotchas discovered from decompilation:
- **Distance clamp on entry**: Entering Transform mode clamps distance to `[minDistOnDestroy=75, maxDistOnDestroy=400]`. Player can scroll to adjust afterward (range `[3, 150000]`), but the initial view may be farther than expected. After clamping, immediately set `distance` to a reasonable default (e.g., 50m) to give a close-up view.
- **OnVesselChange reparenting**: When `GameEvents.onVesselChange` fires (e.g., `[`/`]` keys), KSP forcibly reparents `pivot.parent` to the new vessel, breaking Transform-mode targeting. Must re-call `SetTargetTransform` after this event, or suppress vessel switching while in watch mode.
- **No auto-snap-back**: Unlike Part mode, Transform mode has no distance-based snap back. Camera stays on target until explicitly moved.
- **Frame of reference**: `FlightGlobals.upAxis` and altitude calculations use the active vessel's body. For ghosts on the same body, this is correct. For cross-body ghosts, the frame of reference is wrong.

## Data Model
No new persistent data. All state is transient (flight-scene only), stored on `ParsekFlight`:

```csharp
// In ParsekFlight class:
int watchedRecordingIndex = -1;       // -1 = not watching, ≥0 = index into CommittedRecordings
string watchedRecordingId = null;     // recording ID (stable across index shifts from deletion)
Vessel savedCameraVessel = null;      // the ActiveVessel before entering watch mode
float savedCameraDistance = 0f;       // restore zoom level on return
float savedCameraPitch = 0f;          // restore pitch on return
float savedCameraHeading = 0f;        // restore heading on return
double watchEndHoldUntilUT = -1;      // UT until which to hold camera at last position (non-looped end)
```

The `watchedRecordingId` field is the stable identifier. When recordings are deleted and indices shift, `watchedRecordingIndex` is recomputed from `watchedRecordingId` by scanning `CommittedRecordings`. If the watched recording itself is deleted, watch mode exits.

The ghost is accessed via `ghostStates[watchedRecordingIndex].ghost.transform` - the existing `GhostPlaybackState` class already holds the ghost `GameObject`.

No serialization needed - watch mode is always exited on scene change.

## Behavior

### Entering Watch Mode
**Trigger:** Player clicks the Watch button on a recording row in the Parsek recordings window.

**Preconditions (button enabled only when all true):**
- Recording has an active ghost (`ghostStates[index]` exists, `.ghost != null`)
- Ghost is on the same celestial body as the active vessel (v1 restriction)
- Not already in Take Control state for that recording
- In flight scene (`HighLogic.LoadedSceneIsFlight`)

**Actions:**
1. If the player's active vessel is in an unsafe flight state, show a brief warning tooltip: "Your vessel continues unattended"
2. Save current camera state: `savedCameraVessel = FlightGlobals.ActiveVessel`, `savedCameraDistance = FlightCamera.fetch.Distance`, `savedCameraPitch = FlightCamera.fetch.camPitch`, `savedCameraHeading = FlightCamera.fetch.camHdg`
3. Set `watchedRecordingIndex = index`, `watchedRecordingId = rec.RecordingId`
4. Call `FlightCamera.fetch.SetTargetTransform(ghostStates[index].ghost.transform)`
5. Set `FlightCamera.fetch.distance = 50f` (override the [75,400] clamp to give a close initial view)
6. Block vessel switch keys while in watch mode (subscribe to `GameEvents.onVesselChange`, if in watch mode, re-call `SetTargetTransform` to counteract KSP's forced reparenting)

### Exiting Watch Mode
**Triggers:**
- Player clicks the Watch button again (toggle off)
- Player clicks Watch on a different recording (switch - exit then re-enter)
- Watched ghost is destroyed (recording ends, goes out of range, disabled)
- Watched recording is deleted
- Scene changes
- Player presses Backspace (return-to-vessel gesture)
- Active vessel destroyed (savedCameraVessel becomes null)

**Actions:**
1. If `savedCameraVessel != null` and `savedCameraVessel` is still alive: call `FlightCamera.fetch.SetTargetVessel(savedCameraVessel)`, restore `distance`, `camPitch`, `camHdg`
2. If `savedCameraVessel` is null or destroyed: call `FlightCamera.fetch.SetTargetVessel(FlightGlobals.ActiveVessel)` - KSP will have already assigned a new active vessel via its destruction flow
3. Set `watchedRecordingIndex = -1`, `watchedRecordingId = null`
4. Clear `savedCameraVessel`, `watchEndHoldUntilUT`

### Per-Frame Update (while watching)
Each frame in `UpdateTimelinePlayback()`, after positioning the ghost:
- The ghost Transform is already updated by existing interpolation code
- `FlightCamera` automatically tracks its target (pivot is parented to ghost Transform) - no extra positioning needed
- If time warp is active, ghost position updates at warped rate; camera follows naturally
- Check if `watchedRecordingIndex` is still valid (ghost still exists) - if not, exit watch mode

### Recording Deletion While Watching
When any recording is deleted via `DeleteRecording(index)`:
- If `watchedRecordingIndex == index`: exit watch mode (the watched recording was deleted)
- If `watchedRecordingIndex > index`: decrement `watchedRecordingIndex` by 1 (indices shifted)
- Additionally, verify by comparing `watchedRecordingId` against the recording at the new index - if mismatch, scan `CommittedRecordings` to find the correct index, or exit if not found

### Loop Restart
When a looped recording restarts (UT wraps to start):
- Ghost position jumps to the start of the trajectory
- Camera jumps with it (instant - pivot is parented to ghost Transform, so it follows automatically)
- During the loop pause window, ghost stays at its last position; camera stays there too

### Non-Looped Recording Ends
When the watched recording reaches its last point and doesn't loop:
- **If the recording spawns a vessel:** Ghost is destroyed, vessel is spawned. Exit watch mode, then call `FlightGlobals.ForceSetActiveVessel(spawnedVessel)` to switch the player to the new vessel. The player is now controlling the spawned craft. The original vessel goes on rails (acceptable - same behavior as KSP's normal vessel switch).
- **If ghost-only (no spawn):** Set `watchEndHoldUntilUT = Planetarium.GetUniversalTime() + 3.0`. The ghost is about to be destroyed; before destroying it, keep the ghost GameObject alive (but stop updating its position) for the hold duration. Each frame, check if `Planetarium.GetUniversalTime() >= watchEndHoldUntilUT`. When the hold expires, destroy the ghost and exit watch mode. The 3-second timer is wall-clock-equivalent (uses real UT, so it stretches during time warp - acceptable since the player is just watching a frozen ghost).

### Switching Between Ghosts
Clicking Watch on Recording B while watching Recording A:
1. Exit watch mode for A (don't restore camera to active vessel - skip the `SetTargetVessel` restore)
2. Enter watch mode for B (point camera at B's ghost)
3. Button state: A's button untoggles, B's button toggles on

### Flight Warning
When entering watch mode while the active vessel is in a non-safe state:
- **Safe states** (no warning): `LANDED`, `SPLASHED`, `PRELAUNCH`, `DOCKED`, stable `ORBITING` (periapsis above atmosphere)
- **Unsafe states** (show tooltip): `FLYING`, `SUB_ORBITAL`, `ESCAPING`, or `ORBITING` with periapsis inside atmosphere
- Tooltip text: "Your vessel continues unattended" - shown briefly near the Watch button, non-blocking, auto-fades after 3 seconds

### Watch Mode Visual Indicator
While in watch mode, the player needs clear feedback that they are not controlling a vessel:
- **On-screen label**: Render a small label at top-center of screen: `"Watching: {vesselName}"` with a semi-transparent background. Include `"[Backspace] Return to vessel"` hint below it.
- **Watch button state**: The active recording's Watch button renders with a highlighted/toggled appearance (e.g., green background or bold text)
- **Navball**: KSP's navball will show data relative to the active vessel, not the ghost. This is acceptable - the ghost has no control inputs. The navball may show stale data. No changes to navball rendering.

### Input Blocking While Watching
While in watch mode, the player should not accidentally affect their active vessel:
- **Staging (spacebar)**: Block by not forwarding staging input. Check `watchedRecordingIndex != -1` in the staging input path, or use `InputLockManager.SetControlLock(ControlTypes.STAGING, "ParsekWatch")` on enter, remove on exit.
- **Throttle**: Block similarly via `ControlTypes.THROTTLE`.
- **Vessel switch (`[`/`]`)**: If `GameEvents.onVesselChange` fires while in watch mode, re-call `SetTargetTransform(ghost)` to counteract the reparenting. Alternatively block via `ControlTypes.VESSEL_SWITCHING`.
- **EVA**: Block via `ControlTypes.EVA`.
- **Camera orbit/zoom**: Allow - the player should be able to orbit and zoom around the ghost.
- **Map view (M key)**: Allow - player can open map view. When map view is open, watch mode stays active but the camera shows the map. On closing map view, camera returns to the ghost. No special handling needed - KSP's map view already preserves camera target across map toggle.
- On exit from watch mode, remove all control locks: `InputLockManager.RemoveControlLock("ParsekWatch")`.

## Edge Cases

### E1: Ghost destroyed while watching (v1)
**Scenario:** Player is watching a ghost. The ghost goes out of playback range, is disabled by the player, or the underlying recording is deleted.
**Behavior:** Auto-exit watch mode. Camera returns to active vessel. Logged as warning.

### E2: Active vessel destroyed while watching (v1)
**Scenario:** Player's vessel crashes or explodes while the camera is on a ghost.
**Behavior:** `onVesselDestroy` fires for the active vessel. Set `savedCameraVessel = null`. KSP assigns a new active vessel (or triggers the revert dialog). Exit watch mode - call `SetTargetVessel(FlightGlobals.ActiveVessel)` which handles the null-vessel case (KSP always has an active vessel after destruction, even if it's a debris piece). If KSP triggers the revert dialog, it fires `onGameSceneLoadRequested` which also exits watch mode via scene-change handling.

### E3: Scene change while watching (v1)
**Scenario:** Player goes to Space Center, Tracking Station, quickloads, or the game loads a save while in watch mode.
**Behavior:** Watch mode exits in `OnDestroy()` / `onGameSceneLoadRequested`. Control locks removed. Transient fields cleared. No special cleanup beyond that.

### E4: Time warp while watching (v1)
**Scenario:** Player activates time warp while camera is on a ghost.
**Behavior:** Ghost playback accelerates with warp. Camera follows. Existing warp-stop logic (stop warp at recording boundaries) still applies. The 3-second hold timer for non-looped endings uses game UT, so it stretches with warp. No special handling needed.

### E5: Ghost on different celestial body (deferred to v2)
**Scenario:** Player at KSC, watches a ghost orbiting the Mun.
**Behavior (v1):** Watch button disabled for ghosts on a different body than the active vessel. The button tooltip shows "Ghost is on {bodyName}" to explain why.
**Rationale:** `FlightCamera`'s Transform mode uses `FlightGlobals.upAxis` and altitude computations tied to the active vessel's body. A ghost at the Mun would have wrong frame-of-reference. The `FloatingOrigin` is centered on the active vessel - a ghost millions of meters away would have float precision jitter. Solving this requires either shifting the FloatingOrigin (risky, affects active vessel physics) or spawning a temporary vessel at the ghost position (complex lifecycle management). Deferred to v2.

### E6: Watch button clicked when ghost hasn't spawned yet (v1)
**Scenario:** Recording is "active" time-wise but ghost hasn't been built yet (still loading).
**Behavior:** Button disabled. Ghost must exist in `ghostStates` with non-null `.ghost`.

### E7: Multiple recordings with same vessel name (v1)
**Scenario:** Several looped recordings have the same vessel name.
**Behavior:** Each recording row has its own Watch button. The button operates on the recording index (and recordingId for stability), not vessel name. No ambiguity.

### E8: Orbital ghost (orbit segment playback) (v1)
**Scenario:** Ghost is positioned from an orbit segment (not point-based).
**Behavior:** Same as point-based - the ghost Transform is positioned every frame regardless of source. Camera follows the Transform.

### E9: Manual playback (F10 preview) ghost (v1)
**Scenario:** Player presses F10 for preview, then tries to watch a timeline ghost.
**Behavior:** Manual playback is a separate system. Watch mode applies only to timeline ghosts (committed recordings). F10 preview is unaffected. Both ghosts coexist.

### E10: Vessel spawns at end of recording while watching (v1)
**Scenario:** Non-looped recording spawns a vessel at its end point. Player is watching this ghost.
**Behavior:** Ghost is destroyed, vessel is spawned. Exit watch mode. Call `FlightGlobals.ForceSetActiveVessel(spawnedVessel)` to switch the player to the new craft. The original vessel goes on-rails (same as normal KSP vessel switch - acceptable for landed/orbiting vessels, risky for atmospheric vessels but this is the player's choice since they were warned on entering watch mode).

### E11: Map view while watching (v1)
**Scenario:** Player presses M to open map view while in watch mode.
**Behavior:** Map view opens normally. KSP preserves the camera target across map toggle. On closing map, camera returns to the ghost. The map shows the active vessel's orbit, not the ghost's (the ghost has no orbit driver). Acceptable for v1.

### E12: IVA mode while watching (v1)
**Scenario:** Player presses C to enter IVA while in watch mode.
**Behavior:** Blocked via `InputLockManager` control lock (`ControlTypes.CAMERAMODES`). IVA requires a crewed part on the active camera target - the ghost has none.

### E13: Quicksave/quickload while watching (v1)
**Scenario:** Player presses F5 (quicksave) or holds F9 (quickload) while in watch mode.
**Behavior:** Quicksave works normally - saves the active vessel state, not the camera position. Watch mode is transient and not saved. Quickload triggers a scene reload, which exits watch mode via E3 (scene change handling). The camera position does not affect save data - `FloatingOrigin` has not been shifted, so vessel positions in the save are correct.

### E14: Vessel switch keys while watching (v1)
**Scenario:** Player presses `[` or `]` to cycle vessels while in watch mode.
**Behavior:** Blocked via `InputLockManager` control lock (`ControlTypes.VESSEL_SWITCHING`). If for any reason `onVesselChange` fires despite the lock (e.g., programmatic switch), re-call `SetTargetTransform(ghost)` to counteract KSP's forced pivot reparenting.

### E15: Docking while watching (v1)
**Scenario:** Player's active vessel (unattended) docks with another vessel while camera is on ghost.
**Behavior:** Docking fires `onVesselChange`. The vessel-switch guard (E14) re-calls `SetTargetTransform` to keep camera on the ghost. The docked vessel is now the new active vessel. `savedCameraVessel` may be stale (old vessel no longer exists). On exiting watch mode, fall back to `FlightGlobals.ActiveVessel` if `savedCameraVessel` is null/destroyed.

### E16: Staging/throttle input while watching (v1)
**Scenario:** Player accidentally presses spacebar or adjusts throttle while watching a ghost.
**Behavior:** Blocked via `InputLockManager` control locks (`ControlTypes.STAGING | ControlTypes.THROTTLE`). The active vessel is protected from accidental input.

## Out of Scope
- **Camera smoothing/interpolation on transitions** - instant jumps for v1. Smooth lerping is a polish item.
- **Cross-body camera follow** - deferred to v2 (see E5). Requires FloatingOrigin or temporary vessel work.
- **Free camera mode** - only the standard orbit camera around the ghost. No cinematic/chase/free cam.
- **Watching from Tracking Station** - watch mode is flight-scene only.
- **Ghost orbit in map view** - the map shows the active vessel, not the ghost. Ghost map markers already exist separately.
- **IVA view of ghost** - ghosts have no interior. Blocked.
- **Recording/replaying camera paths** - no cinematic playback system.

## What Doesn't Change
- Ghost visual building (`GhostVisualBuilder`) - no changes to how ghosts look
- Ghost interpolation and positioning - same math, same pipeline
- Recording format - no new serialized fields
- Manual playback (F10/F11) - unaffected
- Take Control feature - orthogonal, both can coexist
- Part event playback - unaffected
- Reentry FX - unaffected
- Time warp stop logic - existing behavior preserved
- Map view markers - unaffected

## Backward Compatibility
No save format changes. No new ConfigNode fields. Feature is purely runtime. Old saves work identically - the Watch button simply appears in the UI for active ghosts.

## Diagnostic Logging

**Subsystem tag:** `CameraFollow`

**State transitions:**
- **Enter watch mode:** `[Parsek][INFO][CameraFollow] Entering watch mode for recording #{index} "{vesselName}" - ghost at {lat:F2},{lon:F2} alt {alt:F0}m on {body}`
- **Exit watch mode (manual):** `[Parsek][INFO][CameraFollow] Exiting watch mode for recording #{index} "{vesselName}" - returning to {activeVessel.vesselName}`
- **Exit watch mode (ghost destroyed):** `[Parsek][WARN][CameraFollow] Watched ghost #{index} "{vesselName}" destroyed - auto-exiting watch mode`
- **Exit watch mode (recording deleted):** `[Parsek][WARN][CameraFollow] Watched recording "{recordingId}" deleted - auto-exiting watch mode`
- **Exit watch mode (scene change):** `[Parsek][INFO][CameraFollow] Watch mode cleared on scene change`
- **Exit watch mode (vessel destroyed):** `[Parsek][WARN][CameraFollow] Active vessel destroyed while watching ghost #{index} - savedCameraVessel is null, falling back to FlightGlobals.ActiveVessel`
- **Switch ghost:** `[Parsek][INFO][CameraFollow] Switching watch from #{oldIndex} to #{newIndex} "{newVesselName}"`

**Vessel interactions:**
- **Vessel switch on spawn:** `[Parsek][INFO][CameraFollow] Recording #{index} spawned vessel pid={pid} - switching active vessel`
- **Vessel switch intercepted:** `[Parsek][VERBOSE][CameraFollow] onVesselChange fired while watching - re-targeting camera to ghost #{index}`
- **Docking while watching:** `[Parsek][WARN][CameraFollow] Docking detected while watching ghost #{index} - savedCameraVessel may be stale`

**Timer and holds:**
- **Non-looped end hold started:** `[Parsek][INFO][CameraFollow] Recording #{index} ended - holding camera at last position until UT {holdUntilUT:F1}`
- **Hold expired, returning:** `[Parsek][INFO][CameraFollow] Hold expired for recording #{index} - returning to active vessel`

**UI and input:**
- **Flight warning shown:** `[Parsek][VERBOSE][CameraFollow] Showing flight warning - active vessel situation: {situation}`
- **Button state decision:** `[Parsek][VERBOSE][CameraFollow] Watch button for #{index}: enabled={enabled} (ghostExists={exists}, sameBody={sameBody})`
- **Camera target set:** `[Parsek][VERBOSE][CameraFollow] FlightCamera.SetTargetTransform on ghost #{index} at {position}, distance={distance:F1}`
- **Camera restored:** `[Parsek][VERBOSE][CameraFollow] FlightCamera.SetTargetVessel restored to {vesselName}, distance={distance:F1}`
- **Control lock set:** `[Parsek][VERBOSE][CameraFollow] InputLockManager control lock "ParsekWatch" set: {lockMask}`
- **Control lock removed:** `[Parsek][VERBOSE][CameraFollow] InputLockManager control lock "ParsekWatch" removed`

**Index management:**
- **Index shifted on delete:** `[Parsek][INFO][CameraFollow] Recording deleted at #{deletedIndex} - watchedRecordingIndex adjusted from {old} to {new}`
- **Index recomputed from ID:** `[Parsek][WARN][CameraFollow] watchedRecordingIndex stale - recomputed from recordingId "{id}": {oldIndex} → {newIndex}`

## Test Plan

### Unit Tests (pure logic, no Unity)

1. **WatchModeState transitions** - Test that `watchedRecordingIndex` transitions: -1 → index on enter, index → -1 on exit, index → newIndex on switch. Also test that index adjusts correctly when a recording at a lower index is deleted (index decrements) and when the watched recording itself is deleted (exits to -1). Guards against stale/wrong index after deletion.

2. **IsVesselSafe classification** - Test all `Vessel.Situations` values: `LANDED` → safe, `SPLASHED` → safe, `PRELAUNCH` → safe, `DOCKED` → safe, `ORBITING` (pe > atmo height) → safe, `ORBITING` (pe < atmo height) → unsafe, `FLYING` → unsafe, `SUB_ORBITAL` → unsafe, `ESCAPING` → unsafe. Guards against `DOCKED` or `ESCAPING` being misclassified.

3. **WatchedRecordingId stability** - Test that when recordings are deleted, `watchedRecordingId` remains stable and `watchedRecordingIndex` is recomputed correctly. Input: delete recording before, at, and after watched index. Guards against camera pointing at wrong ghost after deletion.

### Integration Tests (synthetic recordings)

4. **Watch button enabled only for active ghosts** - Build a synthetic recording, verify the button-enabled logic returns true only when the ghost exists in `ghostStates` and is on the same body. Test with ghost-present, ghost-absent, and different-body scenarios. Guards against button being clickable for inactive/cross-body ghosts.

### Log Assertion Tests

5. **Enter/exit watch mode log lines** - Capture log output via test sink. Call enter-watch-mode logic, verify `[INFO][CameraFollow] Entering watch mode` line is emitted with correct index and vessel name. Call exit logic, verify `[INFO][CameraFollow] Exiting watch mode` line. Guards against silent enter/exit that would be invisible in KSP.log.

6. **Ghost destroyed log line** - Trigger ghost destruction while watch mode is active. Verify `[WARN][CameraFollow] Watched ghost ... destroyed` is emitted. Guards against silent auto-exit with no diagnostic trace.

7. **Index shifted log line** - Delete a recording at a lower index than the watched one. Verify `[INFO][CameraFollow] Recording deleted ... watchedRecordingIndex adjusted` is emitted. Guards against silent index corruption.

8. **Control lock log lines** - Enter watch mode, verify `[VERBOSE][CameraFollow] InputLockManager control lock ... set` emitted. Exit, verify `... removed` emitted. Guards against leaked control locks.

### Manual In-Game Tests

9. **Basic watch and return** - Launch vessel on pad. Open Parsek, click Watch on a looped ghost. Camera moves to ghost. Verify "Watching: {name}" label on screen. Click Watch again. Camera returns. Verify vessel still on pad, controls responsive.

10. **Watch during time warp** - Enter watch mode on orbiting ghost. Activate 4x warp. Ghost moves faster, camera follows smoothly. Exit warp, camera still tracking.

11. **Loop restart** - Watch a short looped recording. When it loops, camera jumps to start position. No stutter or loss of tracking.

12. **Non-looped end with vessel spawn (E10)** - Watch a recording that spawns a vessel at the end. When spawn happens, player should end up controlling the new vessel. Original vessel goes on-rails.

13. **Non-looped end without spawn** - Watch a ghost-only non-looped recording. When recording ends, camera holds at last position for ~3 seconds, then returns to active vessel.

14. **Ghost destroyed while watching (E1)** - Watch a ghost, then disable the recording via its checkbox. Camera should auto-return to active vessel.

15. **Active vessel destroyed while watching (E2)** - Watch a ghost while a vessel with low fuel is in suborbital flight. Let it crash. Verify camera returns and KSP's revert dialog appears normally.

16. **Recording deleted while watching (E1 variant)** - Watch ghost #3, delete recording #1. Verify camera stays on the correct ghost (now at index #2). Delete the watched recording. Verify camera returns to vessel.

17. **Staging blocked (E16)** - Enter watch mode, press spacebar. Verify no staging occurs on the active vessel.

18. **Vessel switch blocked (E14)** - Enter watch mode, press `[` or `]`. Verify camera stays on ghost, no vessel switch.

19. **Map view while watching (E11)** - Enter watch mode, press M. Map opens. Press M again. Camera returns to ghost, not to active vessel.

20. **Reentry capsule** - Watch a reentry recording. See the trail and glow up close while the capsule descends through the atmosphere.

21. **Quickload while watching (E13)** - Enter watch mode, press F9. Game reloads. Verify watch mode is cleanly exited, no control locks remain.
