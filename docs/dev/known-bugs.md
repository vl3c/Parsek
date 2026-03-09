# Known Bugs

## 1. Tech tree nodes stay unlocked after rewind
After rewinding to an earlier recording, all tech tree nodes unlocked later in the game remain unlocked. Expected: revert to the tech tree state from that earlier time and only unlock nodes / buy parts when recorded actions fire at the correct time.

**Status:** Open

## 2. Craft orientation wrong on earlier recording playback
When playing back an earlier recording, the vessel orientation is incorrect. New issue — not observed before.

**Status:** Open

## 3. Vessels fly erratically during playback
Recorded vessels move unpredictably / fly all over the place during ghost playback.

**Status:** Open

## 4. Green sphere instead of vessel model during playback
One recording appears as a green sphere during playback with slight time warp. Root cause: `ParsekScenario.UpdateRecordingsForTerminalEvent()` cleared `GhostVisualSnapshot` on vessel recovery, causing `GetGhostSnapshot()` to return null and triggering the sphere fallback. Fix: preserve `GhostVisualSnapshot` (immutable) — only clear `VesselSnapshot`.

**Status:** Fixed

## 5. Atmospheric heating trails look wrong
Re-entry heating effects appeared as orange square sprites. Root cause: particle materials created without a texture — Unity renders textureless particles as solid squares. Fix: extract particle texture from stock KSP FX prefab and assign to flame, smoke, and trail materials with proper `_TintColor`.

**Status:** Fixed

## 6. Loop checkboxes not centered in UI cells
Merged `ColW_LoopLabel` + `ColW_LoopToggle` into single `ColW_Loop` column, wrapped toggle in horizontal group with `FlexibleSpace` on both sides.

**Status:** Fixed

## 7. Rewind button inactive for most recordings
Root cause: `StartRecording()` called `FlightRecorder.StartRecording()` with default `isPromotion=false` for chain continuations, creating rewind saves for every segment. Fix: detect continuations via `activeChainId != null` and pass `isPromotion=true` to skip rewind save capture.

**Status:** Fixed

## 8. Exo-atmospheric segment incorrectly has rewind button active
Same root cause as #7 — atmosphere boundary splits created continuation segments with their own rewind saves. Fixed by the same `isPromotion: isContinuation` change.

**Status:** Fixed

## 9. Watch camera does not follow recording segment transitions
Added `FindNextWatchTarget` (chain continuation + tree branching) and `TransferWatchToNextSegment` to auto-follow the camera to the next active ghost when a watched segment ends. Preserves saved camera state for Backspace restore.

**Status:** Fixed

## 10. Ghost wobbles at large distances from Kerbin
Root cause: `GetWorldSurfacePosition` returns `Vector3d` but was truncated to `Vector3` (float) before interpolation. Fix: use `Vector3d` and `Vector3d.Lerp` throughout, only truncating at the final `transform.position` assignment.

**Status:** Fixed

## 11. Verify game actions are recorded and reapplied correctly
Check that all game actions (tech unlocks, part purchases, contract completions, science gains, funds/reputation changes, etc.) are properly recorded during gameplay and then reapplied in the correct order during rewind/playback. Resource variables (funds, science, reputation) should reflect the state at the replayed point in time.

**Status:** Open
