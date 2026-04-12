# Fix Plan: Bug #321 — Camera recovery should prefer the anchor vessel, not debris

**Bug:** #321
**Branch:** `investigate/321-anchor-vessel-camera-recovery`
**Status:** Reviewed draft

## Revision History

- **rev 1** — initial investigation write-up and broad anchor-recovery refactor draft
- **rev 2** — narrowed after clean review: fix the proven debris auto-follow selector bug first, defer any broader anchor-signal work until there is a second trace
- **rev 3** — tightened again after final review: breakup branches now disable generic different-PID fallback entirely, so crash recovery only auto-follows same-PID continuation

## Goal

When the watched/main controller vessel crashes, watch recovery should not auto-follow to debris. Instead, it should fall back to the already-preserved live return-vessel context.

This plan is intentionally scoped to the failure that is proven by the reference session.

## Investigation Summary

### Verified failure path from the reference session

The issue reproduces in `logs/2026-04-12_2055_main-stage-freeze-after-separation/Player.log`:

- `GDLV3` is watched as recording `#0`
- the watched ghost reaches its terminal crash sequence
- `GhostPlaybackLogic.FindNextWatchTarget` selects debris recording `#6`
- policy auto-follows `#0 -> #6`
- the later watch exit returns to `#autoLOC_501220`

The key log sequence is:

- `FindNextWatchTarget: currentIndex=0 ... childBpId=6dabc8e45b1742f68d610721a5050be3`
- `FindNextWatchTarget: found target at index 6`
- `Auto-follow on completion: #0 → #6 (vessel=GDLV3 Debris)`
- `Exiting watch mode for recording #6 "GDLV3 Debris" — returning to #autoLOC_501220`

### What the saved tree proves

The saved tree in `logs/.../saves/persistent.sfs` shows that the final branch point only has a debris child:

- branch point `6dabc8e45b1742f68d610721a5050be3`
- child recording `878bbc32769f42cdb59feda59ef985c5`
- `isDebris = True`

That exactly matches the `#0 -> #6` handoff from the log, so the reproduced bug is already explained by the existing "first active child" fallback.

### What this bundle does not prove

This investigation does **not** prove a second camera-restore bug:

- `WatchModeController` already preserves `savedCameraVessel` across watch transfers
- `#autoLOC_501220` resolves to `GDLV3` in the same session log, so the final exit target is consistent with the original preserved live vessel, not obviously a bad debris reassignment
- I could not find evidence in the reference bundle that `RELATIVE` track-section anchor data was involved in this repro

### Relevant source paths

- `Source/Parsek/GhostPlaybackLogic.cs:3131`
  `FindNextWatchTarget` prefers same-PID continuation, then falls back to the first active child. That fallback can be debris.
- `Source/Parsek/ParsekPlaybackPolicy.cs:328`
  `HandlePlaybackCompleted` auto-follows any non-negative target returned by `FindNextWatchTarget`.
- `Source/Parsek/WatchModeController.cs:1121`
  hold-time retry also re-runs `FindNextWatchTarget`, so the selector change must work for both immediate completion and hold-period retry.
- `Source/Parsek/WatchModeController.cs:582`
  `ExitWatchMode` restores the preserved camera vessel, which is sufficient for the proven repro once debris auto-follow is removed.

## Root Cause

`FindNextWatchTarget` currently treats "first active child" as an acceptable generic fallback when there is no same-PID continuation. In breakup/crash trees, that child may be debris.

That is what happens in the reference bundle: the watched root recording ends, the final breakup branch point exposes only a debris child, and the selector returns it. Policy then auto-follows to debris instead of letting the existing hold/exit path restore the preserved live vessel context.

## Fix Direction

### 1. Narrow the selector seam instead of adding a new recovery abstraction

Keep the existing `int FindNextWatchTarget(...)` interface. The proven bug is in the tree-branch fallback inside `GhostPlaybackLogic`, not in policy/watch/controller boundaries.

Update the tree-branch logic so fallback candidates are restricted by branch type:

- active same-PID continuation still wins immediately
- inactive same-PID continuation still keeps the existing `#158` recursion/hold behavior
- non-breakup branches may still use the first active non-debris child as fallback
- breakup branches do **not** auto-follow any different-PID child
- active debris child is never a generic fallback target
- when no valid fallback remains, return `-1` and let the current hold/exit path run

### 2. Preserve existing `#158` continuation semantics

The current logic intentionally avoids falling through to debris when there is a same-PID continuation with no active ghost yet. That behavior must stay intact.

The selector change should therefore be surgical:

- do not touch same-PID recursion
- do not bypass hold/retry
- only change the meaning of the generic `fallbackIdx`

### 3. Rely on the existing hold/exit recovery path for this bug

No `ExitWatchMode` refactor is planned in this issue.

Once debris stops winning the selector:

- `ParsekPlaybackPolicy` will start the normal watch hold instead of auto-following to debris
- `WatchModeController.ProcessWatchEndHoldTimer` will retry using the same selector
- if no non-debris continuation appears, watch expires and returns via the already-preserved `savedCameraVessel`

That is enough for the failure proven in the reference session.

### 4. Defer broader anchor-signaling work

Do **not** use `TrackSection.anchorVesselId` in this fix plan.

Why deferred:

- the reference bundle does not show `RELATIVE` activity
- `TrackSection.anchorVesselId` is a spatial playback anchor, not a proven durable "stable owner" signal for this bug
- using it here risks choosing a stale earlier docking/proximity anchor

If a future trace proves a separate real-vessel recovery problem after hold/exit, treat that as a follow-up investigation.

## Files Likely Touched

- `Source/Parsek/GhostPlaybackLogic.cs`
- `Source/Parsek/ParsekPlaybackPolicy.cs`
- `Source/Parsek/WatchModeController.cs` for retry-path consistency checks and logs

## Tests

### Unit tests

Update coverage for the selector rule itself:

- same-PID continuation still beats everything else
- non-breakup active non-debris child still beats debris child as fallback
- breakup active different-PID child returns `-1`
- debris-only children return `-1`
- same-PID inactive continuation still suppresses debris fallback (`#158`)

Likely homes:

- `Source/Parsek.Tests/BugFixTests.cs`
- `Source/Parsek.Tests/WatchModeControllerTests.cs` only if a small pure seam is added for hold-time retry behavior

The existing test `TreeBranch_DifferentPid_FallsBackToFirstActive` should be updated, not merely supplemented, because it currently codifies the buggy behavior.

### In-game / manual regression

Replay the `s6` scenario from `logs/2026-04-12_2055_main-stage-freeze-after-separation/` and verify:

- the watched main crash no longer transfers to `GDLV3 Debris`
- the watch hold starts instead of immediate debris auto-follow
- the later watch exit returns to the preserved live vessel context
- hold-time retry does not reintroduce debris auto-follow

Add one diagnostic log line for the chosen recovery result so the playtest grep is unambiguous.

## Risks

- There may be historical cases where following debris was intentional or at least tolerated. The selector change should therefore be as narrow as possible: only remove debris from the generic fallback path while keeping same-PID and non-debris continuation behavior intact.
- `ProcessWatchEndHoldTimer` reuses the same selector. If the implementation changes only policy-side call sites, hold-period behavior will diverge.
- `WatchModeControllerTests` currently have almost no coverage for watch handoff behavior, so regression risk is mostly controlled by `BugFixTests` plus manual replay.

## Review Focus

The independent review should check:

- whether the selector change should ignore debris globally in tree fallback, or only for specific branch-point types
- whether any existing behavior depends on debris fallback after non-PID tree branches
- whether one small additional test seam is needed to cover hold-time retry without introducing a broad refactor
