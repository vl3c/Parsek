# Auto-Record Testing Checklist

## 1. Auto-start on launch (happy path)
1. Go to launchpad with any vessel
2. Stage / throttle up so the vessel lifts off
3. Verify: screen message "Recording STARTED (auto)" appears
4. Verify: Parsek UI (Alt+P) shows "RECORDING" status
5. Fly for 30+ seconds, press F9 to stop, revert, merge as usual

## 2. Auto-start on runway
1. Launch a plane from the runway
2. Throttle up and roll — once wheels leave the ground (situation changes from PRELAUNCH), recording should auto-start
3. Verify same screen message appears

## 3. EVA from pad
1. Sit on the pad, do NOT launch
2. EVA a kerbal (right-click crew hatch → EVA)
3. Verify: recording auto-starts on the EVA kerbal
4. Verify: screen message "Recording STARTED (auto — EVA from pad)"
5. Walk around, F9 to stop, revert, check merge dialog works

## 4. EVA from orbit (negative test)
1. Launch to orbit normally (auto-record triggers on launch — that's fine)
2. F9 to stop recording
3. EVA a kerbal in orbit
4. Verify: recording does NOT auto-start (vessel was not PRELAUNCH)

## 5. Manual F9 still works
1. Sit on the pad (don't launch)
2. Press F9 manually — recording should start
3. Press F9 again — recording should stop
4. Verify no double-start when you then launch (already recording, guard should prevent it)

## 6. No double-trigger
1. Launch from pad — auto-record starts
2. Verify it doesn't start a second recording (the `isRecording` guard)
3. Check KSP.log: should see exactly one "Auto-record started" entry per launch

## 7. Full revert cycle with auto-record
1. Launch (auto-records) → fly 30-60s → F9 stop → Revert to Launch
2. Merge dialog should appear as normal
3. Ghost should play back correctly
4. No regressions in crew replacement, vessel spawning, etc.

## Log verification
- Search `KSP.log` for `[Parsek Spike]` — look for:
  - `"Auto-record started (vessel left pad/runway)"` on launch
  - `"EVA from pad detected — pending auto-record"` + `"Auto-record started (EVA from pad)"` for EVA case
  - No unexpected errors or duplicate triggers
