# Parsek General Manual Testing

Use career mode for all tests (resource tracking requires it).

---

## Recording + Timeline (core flow)

1. Launch any vessel from the pad
2. Recording auto-starts on liftoff if enabled, or click `Start Recording` in the Parsek window
3. Fly around for 30-60 seconds
4. Click `Stop Recording` in the Parsek window
5. Revert to Launch (Esc → Revert to Launch)
6. Merge dialog appears - pick any option
7. Wait on the pad until UT reaches the recording's timestamps
8. Verify: opaque ghost vessel appears (original part meshes/textures) and replays the flight
9. Verify: funds/science/reputation deltas are applied at the correct UT

## Merge Dialog Options

### Vessel destroyed
1. Launch, crash into the ground, revert
2. Verify: dialog shows "Merge to Timeline" and "Discard" buttons
3. Pick "Merge to Timeline" - ghost plays back, no vessel spawns

### Vessel intact
1. Launch to orbit, stop the recording from the Parsek window, revert
2. Verify: dialog shows "Merge to Timeline" and "Discard" buttons
3. Pick "Merge to Timeline" - vessel appears in Tracking Station after ghost finishes

### Discard
1. Record anything, revert
2. Pick "Discard" - no recording in timeline, no ghost

## Vessel Persistence

1. Launch to orbit → record → revert → "Merge to Timeline"
2. Go to Tracking Station → verify vessel appears in orbit

## Crew Replacement

### Basic swap (regression test for crew-not-swapped bug)
1. Launch a vessel with Jeb as pilot
2. Start recording from the Parsek window, fly around briefly, then stop recording from the Parsek window
3. Revert to Launch
4. Merge dialog appears - pick "Merge to Timeline"
5. **Verify on the pad:** the vessel's crew portrait should show a REPLACEMENT kerbal, NOT Jeb
   - If Jeb is still shown, the swap failed (this was the bug)
6. Check `KSP.log` for `[Parsek Scenario] Swapped 'Jebediah Kerman' → '...'`
7. Launch the new flight with the replacement pilot
8. Wait for EndUT → verify Jeb's recorded vessel spawns WITH Jeb aboard
   - Go to Tracking Station, click the spawned vessel, check crew tab
   - Jeb should be listed as crew on the spawned vessel
9. Verify: replacement kerbal is removed from roster (Astronaut Complex)

### Astronaut Complex verification
1. Record with Jeb → revert → "Merge to Timeline"
2. Go to Space Center → Astronaut Complex
3. Jeb should be Assigned (not Available)
4. A new kerbal with Pilot trait should appear as Available
5. Wait for EndUT → Jeb's vessel spawns, replacement is removed from roster
6. Repeat: record again → replacement pool stays stable (no kerbal leak)

### Multi-crew vessel swap
1. Build a vessel with 3 crew (e.g. Mk1-3 pod with Jeb, Bill, Valentina)
2. Record → revert → "Merge to Timeline"
3. Verify: ALL three originals are swapped for replacements on the pad vessel
4. Check log for three `Swapped` entries
5. At EndUT: spawned vessel should have all three original kerbals

## Wipe Cleanup

1. Record + merge several times with "Keep Vessel"
2. Open Parsek UI from the toolbar button, click "Wipe Recordings"
3. Verify: all reserved kerbals return to Available, all replacements removed
4. Verify: timeline empty, no ghosts

## Manual Preview

1. Record a flight, then stop the recording from the Parsek window (do NOT revert)
2. Click `Preview Playback` - ghost replays your flight in real time
3. Click `Stop Preview`
4. Verify: ghost disappears cleanly

## Recording Safeguards

### Paused game
1. Pause the game (Esc)
2. Click `Start Recording` in the Parsek window - should show "Cannot record while paused"

### Vessel change stops recording
1. Start recording on one vessel
2. Press `]` to switch vessels (or dock with another vessel)
3. Verify: recording auto-stops with "Recording stopped - vessel changed"

### Very short recordings
1. Launch, immediately revert (fewer than 2 sample points)
2. Verify: no merge dialog appears, recording silently dropped

## Ghost Playback

### Time warp protection
1. Merge a recording with "Keep Vessel"
2. Time warp forward toward the recording's start UT
3. Verify: time warp stops automatically when UT reaches the recording range
4. Verify: time warp during active ghost playback is allowed

### Multiple recordings
1. Record + merge 2-3 flights
2. Wait for all ghosts to play - each should appear and despawn independently
3. Vessels spawn at correct positions when their ghosts finish

## Vessel Spawning

### Proximity offset
1. Record two flights that end near the same location
2. Verify: second vessel spawns offset to 250m away, not on top of the first

### Duplicate prevention
1. Merge a recording with "Keep Vessel", let the vessel spawn
2. Go to Space Center and back to Flight
3. Verify: vessel is not spawned a second time

## Resources

### No negative balance
1. Record a mission that spends funds (e.g. staging)
2. Revert, merge, play back
3. Verify: funds delta applied correctly, never goes below zero

### Quicksave safety
1. Merge a recording, quicksave (F5) mid-playback
2. Quickload (F9 hold) - verify deltas don't double-apply

## Scene Transitions

### Abort Mission
1. Record a flight, then Esc → Space Center (without reverting)
2. Verify: recording auto-committed to timeline (no merge dialog)

### Missed EndUT
1. Merge a recording with "Keep Vessel"
2. Go to Space Center, time warp past the EndUT
3. Verify: reserved crew are auto-freed (check Astronaut Complex)

## UI

1. Toolbar button toggles the Parsek window on/off
2. Window shows correct status: Idle / RECORDING / PREVIEWING
3. Recorded points count and duration update in real time
4. Timeline count and active ghosts count are accurate
5. All buttons work: Start/Stop Recording, Preview/Stop Preview, Clear, Wipe

## Recordings Manager

### Open and close
1. Click "Recordings" button in main Parsek window
2. Verify: secondary window appears to the right with a table of committed recordings
3. Click "Close" or the "Recordings" button again - window closes
4. Reopen - window state (position, size) is preserved within the session

### Table content
1. Merge 2-3 recordings (mix of ghost-only and vessel-spawn)
2. Open Recordings Manager
3. Verify: each recording shows correct name, launch time (KSP calendar), duration, and status
4. Verify: status updates in real time - `future` before ghost starts, `active` during, `past` after

### Sorting
1. Click "Name" column header - recordings sort alphabetically
2. Click again - sort reverses
3. Repeat for Launch Time, Duration, Status columns
4. Verify: sort arrow indicator shows in the active column header

### Per-recording loop
1. Check the Loop checkbox for one recording
2. Wait for ghost to finish - verify it restarts after a pause
3. Uncheck - verify ghost plays once and stops
4. Check the select-all checkbox in the Loop header - all recordings toggle on
5. Uncheck one - header shows mixed state

### Hide recording
1. Check the Hide checkbox on a recording
2. Verify: with header Hide checkbox active (default), recording disappears from list
3. Uncheck header Hide checkbox - hidden recording reappears with its Hide checkbox checked
4. Uncheck the recording's Hide checkbox - it stays visible when header is re-checked
5. Verify: hidden recordings still play as ghosts normally

### Hide group
1. Check the Hide checkbox on a group header row
2. Verify: entire group disappears when header Hide is active
3. Uncheck header Hide - group reappears with its checkbox checked
4. Verify: group members are still accessible when group is unhidden

### Scroll and resize
1. Merge many recordings (5+) to exceed the visible area
2. Verify: scroll view appears, scrolling works without moving the camera
3. Drag the bottom-right corner to resize the window
4. Verify: table content reflows to new size

## Part Event Playback

### Deployables
1. Build a vessel with solar panels and antennas
2. Record: extend solar panels, extend antenna, retract, re-extend
3. Revert, merge, watch ghost - verify panels/antennas animate at correct times

### Lights
1. Build a vessel with lights
2. Record: toggle lights on, off, enable blink mode
3. Revert, merge - verify ghost lights turn on/off at correct times

### Landing gear
1. Build a plane with retractable gear
2. Record: retract gear after takeoff, deploy before landing
3. Revert, merge - verify ghost gear retracts/deploys at correct times

### Cargo bays
1. Build a vessel with a cargo bay / service bay
2. Record: open and close the bay doors
3. Revert, merge - verify ghost bay doors animate at correct times

### Fairings
1. Build a vessel with a procedural fairing
2. Record: jettison fairing during ascent
3. Revert, merge - verify ghost has fairing cone that disappears at jettison time

### RCS
1. Build a vessel with RCS thrusters
2. Record: fire RCS (translate/rotate)
3. Revert, merge - verify ghost RCS emits particle FX during firing

## Log Verification

Search `KSP.log` for `[Parsek]` and `[Parsek Scenario]`:
- No unexpected errors or exceptions
- Auto-record triggers logged correctly
- Crew replacement actions logged as "Hired replacement ..." / "Removed replacement ..."

### Post-run validation command
After running a manual scenario, run:

```bash
pwsh -File scripts/validate-ksp-log.ps1
```

Treat a non-zero exit as a failed local validation.
