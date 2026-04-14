# Auto-Record Testing Checklist

## 1. Auto-start on launch (happy path)
1. Go to launchpad with any vessel
2. Stage / throttle up so the vessel lifts off
3. Verify: screen message "Recording STARTED (auto)" appears
4. Verify: Parsek UI (toolbar window) shows "RECORDING" status
5. Fly for 30+ seconds, stop the recording from the Parsek window, revert, merge as usual

## 2. Auto-start on runway
1. Launch a plane from the runway
2. Throttle up and roll - once wheels leave the ground (situation changes from PRELAUNCH), recording should auto-start
3. Verify same screen message appears

## 3. EVA from pad
1. Sit on the pad, do NOT launch
2. EVA a kerbal (right-click crew hatch → EVA)
3. Verify: recording auto-starts on the EVA kerbal
4. Verify: screen message "Recording STARTED (auto - EVA from pad)"
5. Walk around, stop the recording from the Parsek window, revert, check merge dialog works

## 4. EVA from orbit (negative test)
1. Launch to orbit normally (auto-record triggers on launch - that's fine)
2. Stop the current recording from the Parsek window
3. EVA a kerbal in orbit
4. Verify: recording does NOT auto-start (vessel was not PRELAUNCH)

## 5. Manual recording via UI still works
1. Sit on the pad (don't launch)
2. Click `Start Recording` in the Parsek window - recording should start
3. Click `Stop Recording` in the Parsek window - recording should stop
4. Verify no double-start when you then launch (already recording, guard should prevent it)

## 6. No double-trigger
1. Launch from pad - auto-record starts
2. Verify it doesn't start a second recording (the `isRecording` guard)
3. Check KSP.log: should see exactly one "Auto-record started" entry per launch

## 7. Full revert cycle with auto-record
1. Launch (auto-records) → fly 30-60s → stop the recording from the Parsek window → Revert to Launch
2. Merge dialog should appear as normal
3. Ghost should play back correctly
4. No regressions in crew replacement, vessel spawning, etc.

## 8. Auto-record disabled via Settings
1. Open Parsek UI → Settings → uncheck "Auto-record on launch"
2. Launch from pad - verify recording does NOT auto-start
3. Click `Start Recording` in the Parsek window - verify manual recording still works
4. Re-enable "Auto-record on launch" → launch again → verify auto-record works

## 9. Auto-EVA-record disabled via Settings
1. Open Parsek UI → Settings → uncheck "Auto-record on EVA"
2. Sit on pad, EVA a kerbal - verify recording does NOT auto-start
3. Re-enable → EVA again → verify auto-record works

## 10. Auto-warp-stop disabled via Settings
1. Record a flight, revert, merge with Keep Vessel
2. Open Settings → uncheck "Auto-stop time warp"
3. Time warp past the recording's StartUT - verify warp is NOT stopped
4. Re-enable → time warp past another recording → verify warp stops

## 11. Settings persistence
1. Change some settings (uncheck toggles, move sliders)
2. Save game (F5 or Esc → Save)
3. Reload (F9 or Load)
4. Open Settings → verify values persisted
5. Also verify via Esc → Settings → Parsek (KSP difficulty screen)

## 12. Defaults button
1. Change all settings to non-default values
2. Click "Defaults" in the Settings window
3. Verify all values reset to defaults (auto-record on, 3.0s interval, 2.0° direction, 5% speed)

## Log verification
- Search `KSP.log` for `[Parsek]` - look for:
  - `"Auto-record started (vessel left pad/runway)"` on launch
  - `"EVA from pad detected - pending auto-record"` + `"Auto-record started (EVA from pad)"` for EVA case
  - No unexpected errors or duplicate triggers
