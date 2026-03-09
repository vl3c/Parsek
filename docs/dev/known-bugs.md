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
Re-entry heating effects appear as orange square sprites instead of the normal trail effect.

**Status:** Open

## 6. Loop checkboxes not centered in UI cells
The per-recording loop checkboxes should be centered within their table cells for a cleaner look.

**Status:** Open (UI polish)

## 7. Rewind button inactive for most recordings
Most recording entries have the rewind button disabled. Likely because orbit segments and decouple continuation segments create separate entries that lack launch start points. These child segments should be grouped under their parent recording, with only the launch recording exposing the rewind button.

**Status:** Open

## 8. Exo-atmospheric segment incorrectly has rewind button active
Recording segment index 19 (an exo-atmosphere segment inside a group) has the rewind button enabled. Only actual launch/start recordings should offer rewind.

Related to #7 — rewind availability logic needs to be tightened.

**Status:** Open

## 9. Watch camera does not follow recording segment transitions
When watching a recording and the current segment ends, the next segment in the tree begins playback but the camera stays on the old segment. The watch camera should automatically move to focus on the continuation segment for seamless viewing.

**Status:** Open

## 10. Ghost wobbles at large distances from Kerbin
When the ghost vessel is far from Kerbin, it starts to wobble/jitter. Likely a floating-point precision issue with large world coordinates. May need origin-relative positioning or double-precision handling for ghost placement.

**Status:** Open

## 11. Verify game actions are recorded and reapplied correctly
Check that all game actions (tech unlocks, part purchases, contract completions, science gains, funds/reputation changes, etc.) are properly recorded during gameplay and then reapplied in the correct order during rewind/playback. Resource variables (funds, science, reputation) should reflect the state at the replayed point in time.

**Status:** Open
