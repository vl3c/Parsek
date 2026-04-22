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

## 5. Post-switch landed motion
1. Be idle in FLIGHT, then switch to a real non-ghost landed vessel or rover
2. Verify: switching alone does NOT start recording
3. Wait a moment for the vessel to settle, then drive / roll enough to move while it stays `LANDED`
4. Verify: exactly one screen message `Recording STARTED (auto - post switch)` appears
5. Verify: Parsek UI shows `RECORDING`
6. Check `KSP.log` for one `Auto-record started (post-switch LandedMotion)` line and no duplicate start for the same switch

## 6. Post-switch orbital engine or sustained RCS
1. Be idle in FLIGHT, then switch to a real non-ghost vessel already in orbit
2. Verify: switching alone does NOT start recording
3. Start an engine burn or hold sustained RCS long enough to defeat the debounce
4. Verify: recording starts exactly once without requiring a situation change
5. Check `KSP.log` for one `Auto-record started (post-switch EngineActivity)` or `...SustainedRcsActivity)` line

## 7. Post-switch non-cosmetic part-state change (gear-toggle case)
1. Be idle in FLIGHT, then switch to a landed real vessel with deployable landing gear
2. Verify: switching alone does NOT start recording
3. Toggle the landing gear once
4. Verify: recording starts exactly once
5. Verify: the same craft does NOT auto-start from cosmetic-only actions such as lights alone

## 8. Post-switch no-op negative case
1. Be idle in FLIGHT, then switch to a real non-ghost vessel
2. Verify: switching alone does NOT start recording
3. Do nothing meaningful for several seconds
4. Verify: recording does NOT auto-start
5. Check `KSP.log` for arm/baseline lines but no `Auto-record started (post-switch ...)` line

## 9. Manual recording via UI still works
1. Sit on the pad (don't launch)
2. Click `Start Recording` in the Parsek window - recording should start
3. Click `Stop Recording` in the Parsek window - recording should stop
4. Verify no double-start when you then launch (already recording, guard should prevent it)

## 10. No double-trigger
1. Launch from pad - auto-record starts
2. Verify it doesn't start a second recording (the `isRecording` guard)
3. Check KSP.log: should see exactly one "Auto-record started" entry per launch

## 11. Full revert cycle with auto-record
1. Launch (auto-records) → fly 30-60s → stop the recording from the Parsek window → Revert to Launch
2. Merge dialog should appear as normal
3. Ghost should play back correctly
4. No regressions in crew replacement, vessel spawning, etc.

## 12. Auto-record on launch disabled via Settings
1. Open Parsek UI → Settings → uncheck "Auto-record on launch"
2. Launch from pad - verify recording does NOT auto-start
3. Click `Start Recording` in the Parsek window - verify manual recording still works
4. Re-enable "Auto-record on launch" → launch again → verify auto-record works

## 13. Auto-EVA-record disabled via Settings
1. Open Parsek UI → Settings → uncheck "Auto-record on EVA"
2. Sit on pad, EVA a kerbal - verify recording does NOT auto-start
3. Re-enable → EVA again → verify auto-record works

## 14. Auto-record on first modification after switch disabled via Settings
1. Open Parsek UI → Settings → uncheck `Auto-record on first modification after switch`
2. Switch to a real landed or orbital vessel while idle
3. Drive / burn / toggle gear / change resources
4. Verify: recording does NOT auto-start
5. Re-enable the toggle and repeat one of the post-switch cases above - verify auto-record works again

## 15. Auto-warp-stop disabled via Settings
1. Record a flight, revert, merge with Keep Vessel
2. Open Settings → uncheck "Auto-stop time warp"
3. Time warp past the recording's StartUT - verify warp is NOT stopped
4. Re-enable → time warp past another recording → verify warp stops

## 16. Settings persistence
1. Change some settings (uncheck toggles, move sliders)
2. Save game (F5 or Esc → Save)
3. Reload (F9 or Load)
4. Open Settings → verify values persisted
5. Also verify via Esc → Settings → Parsek (KSP difficulty screen)

## 17. Defaults button
1. Change all settings to non-default values
2. Click "Defaults" in the Settings window
3. Verify all values reset to defaults (`Auto-record on launch`, `Auto-record on EVA`, and `Auto-record on first modification after switch` all on; 3.0s interval, 2.0° direction, 5% speed)

## Log verification
- Search `KSP.log` for `[Parsek]` - look for:
  - `"Auto-record started (vessel left pad/runway)"` on launch
  - `"EVA from pad detected - pending auto-record"` + `"Auto-record started (EVA from pad)"` for EVA case
  - `"Post-switch auto-record armed:"` after an idle switch to a real vessel
  - `"Post-switch baseline captured:"` on the first stable physics frame after the switch
  - `"Auto-record started (post-switch ...)"` exactly once for the landed-motion / orbital-burn / gear-toggle cases
  - No unexpected errors or duplicate triggers
