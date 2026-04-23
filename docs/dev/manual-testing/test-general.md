# Parsek General Manual Testing

Use career mode for all tests (resource tracking requires it).

---

## Release Closeout Evidence

For a release candidate or shipping build, keep at minimum these four
timestamped bundles under `logs/`:

- `YYYY-MM-DD_HHMM_release-auto-record`
- `YYYY-MM-DD_HHMM_release-core-playback`
- `YYYY-MM-DD_HHMM_release-scene-transitions`
- `YYYY-MM-DD_HHMM_release-tracking-station`

Each retained bundle must include:

- `KSP.log`
- `Player.log`
- `parsek-test-results.txt`
- `log-validation.txt`
- `release-bundle-validation.txt`

Use `python scripts/collect-logs.py <label>` as the standard bundling path.
That script copies `KSP.log`, `Player.log`, `parsek-test-results.txt`, and
writes `log-validation.txt` into the timestamped bundle under `logs/`.

Before capturing any release bundle, verify that the deployed
`GameData/Parsek/Plugins/Parsek.dll` is the DLL you just built. Follow the
`.claude/CLAUDE.md` verification recipe:

- compare file size and mtime against the build output for the configuration
  under test (`Source/Parsek/bin/Release/Parsek.dll` for RC / shipping bundles,
  `Source/Parsek/bin/Debug/Parsek.dll` only for local debug-only validation)
- grep the deployed DLL for a distinctive UTF-16 string from the build under
  test
- if they do not match, force-copy the worktree DLL before launching KSP

### `release-auto-record`

- Manual scope: `docs/dev/manual-testing/test-auto-record.md` scenarios 1, 3, and 6
- Exported runtime evidence:
  - `RuntimeTests.AutoRecordOnLaunch_StartsExactlyOnce`
  - `RuntimeTests.AutoRecordOnEvaFromPad_StartsExactlyOnce`
- Runner note: use `Run+` for the `AutoRecord` category, or run the two rows
  individually in a disposable `FLIGHT` session

### `release-core-playback`

- Manual scope: `Recording + Timeline (core flow)` plus all `Merge Dialog Options`
  in this file
- Exported runtime evidence:
  - `RuntimeTests.TreeMergeDialog_DiscardButton_ClearsPendingTree`
  - `RuntimeTests.TreeMergeDialog_DeferredMergeButton_CommitsPendingTree`
  - `RuntimeTests.KeepVessel_FastForwardIntoPlayback_SpawnsExactlyOnce`
- Runner note: use `Run+` for `MergeDialog`, then run
  `RuntimeTests.KeepVessel_FastForwardIntoPlayback_SpawnsExactlyOnce` from an
  idle `FLIGHT` session

### `release-scene-transitions`

- Manual scope: `Scene Transitions` in this file, specifically the deferred
  merge-dialog `Space Center` exit path plus the `Missed EndUT` check
- Exported runtime evidence:
  - `RuntimeTests.RevertToLaunch_SoftUnstashesPendingTree_WithoutMergeDialog`
  - `RuntimeTests.ExitToSpaceCenter_DeferredMergeButton_CommitsPendingTree`
  - `RuntimeTests.ExitToSpaceCenter_DeferredDiscardButton_ClearsPendingTree`
- Runner note: run these three rows individually from a disposable
  `PRELAUNCH` `FLIGHT` session

### `release-tracking-station`

- Manual scope: `Vessel Persistence` plus a Tracking Station visit after at
  least one orbital recording is committed or materialized
- Exported runtime evidence:
  - `TrackingStationRuntimeTests.TrackingStationSceneEntry_HostIsActive`
  - `TrackingStationRuntimeTests.TrackingStationGhostToggle_SyntheticOrbit_RemovesAndRecreates`
  - `TrackingStationRuntimeTests.TrackingStationGhostObjects_SyntheticOrbit_ResolvableAndQuiet`
- Runner note: run `Run All` or the `TrackingStation` category from the
  Tracking Station scene. The optional materialized-vessel Fly canary,
  `TrackingStationRuntimeTests.TrackingStationMaterializedOrbit_FlyLoadsMaterializedVessel_NotStaleSelection`,
  is manual-only because it first exercises the ghost-selection stale-clear path
  and then drives stock Fly into `FLIGHT`; the bundle validator reports that row
  separately when it was not captured.

`validate-ksp-log` is part of the release gate, not an optional follow-up:

- Reset the in-game test results immediately before each release bundle
  (`Reset` in the Test Runner) or gather each bundle from a fresh KSP session.
  `parsek-test-results.txt` is cumulative, so stale rows from an earlier run do
  not count as evidence for the current bundle.
- Use `python scripts/collect-logs.py <label>` immediately after each bundle
  while that bundle's `KSP.log` is still the latest session log. The script
  runs the validator and saves its output as `log-validation.txt`.
- Run `python scripts/validate-release-bundle.py <bundle-dir>` on the collected
  folder immediately afterward. That script writes
  `release-bundle-validation.txt` and fails if the bundle is missing required
  artifacts, the log validator did not pass, or any required runtime row is
  missing / not `PASSED`.
- Treat any non-zero validator result, missing `log-validation.txt`, missing
  `release-bundle-validation.txt`, missing required rows, or required rows that
  are not `PASSED` after the explicit reset/fresh-session boundary as a failed
  bundle.

## Recording + Timeline (core flow)

1. Launch any vessel from the pad
2. Recording auto-starts on liftoff if enabled, or click `Start Recording` in the Parsek window
3. Fly around for 30-60 seconds
4. Click `Stop Recording` in the Parsek window
5. Exit to Space Center (Esc → Space Center)
6. Deferred merge dialog appears - pick any option
7. Rewind / fast-forward back before the recording window so playback can run again
8. Verify: opaque ghost vessel appears (original part meshes/textures) and replays the flight
9. Verify: funds/science/reputation deltas are applied at the correct UT

## Merge Dialog Options

### Vessel destroyed
1. Launch, crash into the ground, then exit to Space Center
2. Verify: deferred dialog shows "Merge to Timeline" and "Discard" buttons
3. Pick "Merge to Timeline", rewind / fast-forward back before the recording
   window, then verify: ghost plays back and no vessel spawns

### Vessel intact
1. Launch to orbit, stop the recording from the Parsek window, then exit to Space Center
2. Verify: deferred dialog shows "Merge to Timeline" and "Discard" buttons
3. Pick "Merge to Timeline", rewind / fast-forward back before the recording
   window, then verify: vessel appears in Tracking Station after ghost finishes

### Discard
1. Record anything, then exit to Space Center
2. Pick "Discard" - no recording in timeline, no ghost

## Vessel Persistence

1. Launch to orbit → record → Space Center exit → "Merge to Timeline" → rewind / fast-forward back before recording start
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

### Exit To Space Center (deferred merge dialog)
1. Record a flight, then Esc → Space Center (without reverting)
2. Verify: the deferred `ParsekMerge` dialog appears because the shipped
   default is `Auto-merge recordings = off`
3. Choose `Merge to Timeline` once and verify the pending tree commits
4. Repeat on a disposable run and choose `Discard` once, verifying the pending
   tree clears without a new committed recording

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

Treat a non-zero exit as a failed local validation. For release-closeout
bundles, save the validator output as `log-validation.txt` and do not count the
bundle as complete without a passing run.
