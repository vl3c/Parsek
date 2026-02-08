# Parsek General Manual Testing

Use career mode for all tests (resource tracking requires it).

---

## Recording + Timeline (core flow)

1. Launch any vessel from the pad
2. Recording auto-starts on liftoff (or press F9 manually)
3. Fly around for 30-60 seconds
4. Press F9 to stop recording
5. Revert to Launch (Esc → Revert to Launch)
6. Merge dialog appears — pick any option
7. Wait on the pad until UT reaches the recording's timestamps
8. Verify: green-cyan ghost sphere appears and replays the flight
9. Verify: funds/science/reputation deltas are applied at the correct UT

## Merge Dialog Options

### Vessel barely moved (<100m)
1. Launch, fly a few meters, F9 stop, revert
2. Verify: dialog default is "Merge + Recover"
3. Pick "Merge + Recover" — vessel recovered for funds, no vessel in tracking station

### Vessel destroyed
1. Launch, crash into the ground, revert
2. Verify: dialog default is "Merge to Timeline"
3. Pick "Merge to Timeline" — ghost plays back, no vessel spawns

### Vessel intact, moved far
1. Launch to orbit, F9 stop, revert
2. Verify: dialog default is "Merge + Keep Vessel"
3. Pick "Merge + Keep Vessel" — vessel appears in Tracking Station after ghost finishes

### Discard
1. Record anything, revert
2. Pick "Discard" — no recording in timeline, no ghost

## Vessel Persistence

1. Launch to orbit → record → revert → "Merge + Keep Vessel"
2. Go to Tracking Station → verify vessel appears in orbit
3. Alternatively: choose "Merge + Recover" → verify funds credited, no vessel in orbit

## Crew Replacement

1. Record with Jeb → revert → "Merge + Keep Vessel"
2. Check Astronaut Complex: Jeb should be Assigned, a new kerbal with same trait should appear
3. Launch new flight — replacement kerbal is available in crew selection
4. Wait for EndUT → Jeb's vessel spawns, replacement is removed from roster
5. Repeat: record again → replacement pool stays stable (no kerbal leak)

## Wipe Cleanup

1. Record + merge several times with "Keep Vessel"
2. Open Parsek UI (Alt+P), click "Wipe Recordings"
3. Verify: all reserved kerbals return to Available, all replacements removed
4. Verify: timeline empty, no ghosts

## Manual Preview

1. Record a flight, press F9 to stop (do NOT revert)
2. Press F10 — ghost replays your flight in real time
3. Press F11 to stop the preview
4. Verify: ghost disappears cleanly

## Recording Safeguards

### Paused game
1. Pause the game (Esc)
2. Press F9 — should show "Cannot record while paused"

### Vessel change stops recording
1. Start recording on one vessel
2. Press `]` to switch vessels (or dock with another vessel)
3. Verify: recording auto-stops with "Recording stopped — vessel changed"

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
2. Wait for all ghosts to play — each should appear and despawn independently
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
2. Quickload (F9 hold) — verify deltas don't double-apply

## Scene Transitions

### Abort Mission
1. Record a flight, then Esc → Space Center (without reverting)
2. Verify: recording auto-committed to timeline (no merge dialog)

### Missed EndUT
1. Merge a recording with "Keep Vessel"
2. Go to Space Center, time warp past the EndUT
3. Verify: reserved crew are auto-freed (check Astronaut Complex)

## UI

1. Alt+P toggles the Parsek window on/off
2. Window shows correct status: Idle / RECORDING / PREVIEWING
3. Recorded points count and duration update in real time
4. Timeline count and active ghosts count are accurate
5. All buttons work: Start/Stop Recording, Preview/Stop Preview, Clear, Despawn Ghosts, Wipe

## Log Verification

Search `KSP.log` for `[Parsek Spike]` and `[Parsek Scenario]`:
- No unexpected errors or exceptions
- Auto-record triggers logged correctly
- Crew replacement actions logged as "Hired replacement ..." / "Removed replacement ..."
