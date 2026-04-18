# Plan: Fix watch camera auto-switching to next loop-iteration ghost

Branch: `fix/watch-camera-loop-switch`
Worktree: `Parsek-fix-watch-camera-loop-switch`
Date: 2026-04-18 (revision 3 -- after review by Opus)

## Problem

When the flight-scene Parsek camera is watching a ghost of a recording that
has loop or Gloops-style overlap playback enabled, the camera gets pulled
away from the ghost the user is watching whenever ANOTHER overlap cycle of
the same recording expires. The visible result: camera flickers off the
watched ghost onto a bridge anchor then lands on the current primary (the
newest iteration), even though the user's watched ghost is still alive and
playing.

The user's stated expectation: stay on the ghost until THAT ghost's own
instance ends; then auto-retarget to the most-recently-launched ghost of the
same recording (which is always the current primary). `[` and `]` remain
the only manual release.

## What the code actually does today

### Overlap-loop event emission (`GhostPlaybackEngine.UpdateOverlapPlayback`)

Two distinct camera events fire from the overlap path:

1. At primary cycle boundary (a NEW iteration launches, old primary demoted
   to overlap list): `OnOverlapCameraAction` with
   `Action = RetargetToNewGhost`, `NewCycleIndex = lastCycle`, pivot of the
   NEW primary. (`GhostPlaybackEngine.cs:1088-1095`)
2. For each overlap-list ghost whose phase exceeds duration (an OLD cycle
   expires and is destroyed): `OnOverlapCameraAction` with
   `Action = ExplosionHoldStart` or `ExplosionHoldEnd`, `NewCycleIndex =
   cycle` (the expiring cycle's index). (`GhostPlaybackEngine.cs:1186-1196`)

### Regular-loop event emission (`GhostPlaybackEngine.UpdateLoopingPlayback`)

At each cycle boundary the engine fires BOTH `ExplosionHoldStart`/`End` and
then `RetargetToNewGhost` in the same update, same frame:

- `ExplosionHoldStart` / `ExplosionHoldEnd` at `GhostPlaybackEngine.cs:889-896`.
  `NewCycleIndex` is not set (defaults to 0); with a single-ghost regular
  loop there is only ever one cycle alive, so identity is implied.
- `RetargetToNewGhost` at `GhostPlaybackEngine.cs:953-960`, carrying the new
  ghost's pivot and new cycle index.

### Controller handlers

`WatchModeController.HandleOverlapCameraAction` at `WatchModeController.cs:708-756`:

- `RetargetToNewGhost`: gated by `watchedOverlapCycleIndex == -1`. User's
  watched cycle in the overlap case is always a real cycle index (>= 0) for
  the live watched ghost, so this gate normally FAILS and the handler
  no-ops. The current retarget-to-new-primary at primary-spawn is ALREADY
  a no-op in the common case.
- `ExplosionHoldStart` (`:725-737`): unconditionally sets
  `watchedOverlapCycleIndex = -2`, installs the hold anchor at
  `evt.AnchorPosition`, and calls `SetTargetTransform` onto that anchor.
  Runs for ANY overlap cycle expiry on a watched recording -- even when
  the expiring cycle is NOT the one the user is watching.
- `ExplosionHoldEnd` (`:739-754`): unconditionally sets
  `watchedOverlapCycleIndex = -1`, installs the bridge anchor at
  `evt.AnchorPosition`, then calls `TryResolveOverlapBridgeRetarget` which
  retargets the camera to the current primary via `WatchModeController.cs:
  2669-2696`. Again runs for any cycle expiry, not just the watched one.

`WatchModeController.HandleLoopCameraAction` at `WatchModeController.cs:654-706`
behaves analogously for the single-ghost regular-loop path, but because only
one cycle exists at a time it is always legitimately about the user's cycle.

## Root cause

`HandleOverlapCameraAction` treats `ExplosionHoldStart` and
`ExplosionHoldEnd` as global "an overlap cycle expired on your recording"
notifications instead of "the cycle you're watching expired" notifications.
Any other overlap cycle expiring on the same recording wipes
`watchedOverlapCycleIndex`, installs a hold/bridge anchor at that cycle's
position, and (for `ExplosionHoldEnd`) immediately retargets via the bridge
resolver to the current primary. The user perceives this as the camera
auto-jumping to the new primary ghost.

The `RetargetToNewGhost` path is NOT the root cause in the overlap case --
its `watchedOverlapCycleIndex == -1` gate already prevents it from doing
anything in the common case. The bug enters through `ExplosionHoldEnd`,
which resets cycle index to `-1` AND runs the bridge resolver in the same
frame.

## Fix

One surgical change in `WatchModeController.cs`: in
`HandleOverlapCameraAction`, guard both `ExplosionHoldStart` and
`ExplosionHoldEnd` so they only process when the expiring cycle is the one
the user is actually watching.

```csharp
case CameraActionType.ExplosionHoldStart:
    if (evt.NewCycleIndex != watchedOverlapCycleIndex)
    {
        ParsekLog.VerboseRateLimited("CameraFollow",
            $"overlap-hold-start-skip-{evt.Index}",
            $"Overlap: hold start for #{evt.Index} cycle={evt.NewCycleIndex} " +
            $"ignored (watching cycle={watchedOverlapCycleIndex})");
        break;
    }
    // existing body unchanged
    ...

case CameraActionType.ExplosionHoldEnd:
    if (evt.NewCycleIndex != watchedOverlapCycleIndex)
    {
        ParsekLog.VerboseRateLimited("CameraFollow",
            $"overlap-hold-end-skip-{evt.Index}",
            $"Overlap: hold end for #{evt.Index} cycle={evt.NewCycleIndex} " +
            $"ignored (watching cycle={watchedOverlapCycleIndex})");
        break;
    }
    // existing body unchanged
    ...
```

Nothing else changes. `RetargetToNewGhost` handlers stay as-is (the gate
already prevents them firing on primary cycle boundaries when a watched
ghost is alive). `HandleLoopCameraAction` stays as-is (single-ghost regular
loop never has a non-watched cycle to worry about). `FindWatchedGhostState`
and `TryResolveOverlapBridgeRetarget` stay as-is.

## Why this matches the user's intent (scenario walkthrough)

| Scenario | Events fired | Handler response | Visible result |
|---|---|---|---|
| Overlap, user watches primary cycle K; older cycle M expires | `ExplosionHoldEnd(NewCycleIndex=M)` (or Start if destroyed explosively) | Guard M != K -> no-op. | Camera stays on K. The other cycle quietly expires in the overlap list. |
| Overlap, user watches cycle N (now in overlap list because a newer primary K has spawned); cycle N expires | `ExplosionHoldEnd(NewCycleIndex=N)` | Guard N == N -> existing path runs: cycle=-1, bridge anchor, `TryResolveOverlapBridgeRetarget` retargets to current primary (K or newer). | Camera follows N to its end then hands off to the newest primary -- the user's stated desired behaviour. |
| Overlap, new primary cycle K+1 spawns; user on K | `RetargetToNewGhost(NewCycleIndex=K+1)` | Gate cycle==-1 fails (user's cycle is K). No retarget. | Unchanged (already worked). Camera stays on K. |
| Regular loop, cycle boundary | `ExplosionHoldEnd` then `RetargetToNewGhost` in the same frame | ExplosionHoldEnd bridges, RetargetToNewGhost hands off to the new ghost (gate passes because ExplosionHoldEnd just set cycle=-1). | Unchanged. Camera hands off to new cycle at boundary -- matches user's "switch to next launched when current ends" exactly, because for single-ghost regular loops the watched ghost IS destroyed at the boundary. |
| Non-looped recording ends | No camera events emitted at end | Safety net at `:2285-2295` catches null target after 3 frames, exits watch. | Unchanged. |
| Real explosion of watched cycle N | `ExplosionHoldStart(NewCycleIndex=N)` then `ExplosionHoldEnd(NewCycleIndex=N)` later | Guard N == N -> existing hold-and-resolve path runs. | Unchanged when watching the exploding cycle. |
| Real explosion of non-watched cycle M (watching K) | `ExplosionHoldStart(NewCycleIndex=M)` etc. | Guard M != K -> no-op. | Camera stays on K. The explosion renders visually but does not steal the camera. |

This is the minimum edit that fixes the observed bug without touching the
bridge-resolver state machine, `FindWatchedGhostState`'s fallback, or any
engine-side emission logic.

## What was considered and rejected (from review feedback)

The prior revision of this plan proposed:

- dropping `watchedOverlapCycleIndex = -1` from both `ExplosionHoldEnd`
  cases (preserving the watched-cycle identity through the boundary), AND
- routing retarget exclusively through `FindWatchedGhostState`'s fallback
  (which already retargets to primary when the watched cycle is lost), AND
- making `RetargetToNewGhost` log-only.

Reviewer identified that this breaks `TryResolveOverlapBridgeRetarget`
(gated on `watchedOverlapCycleIndex == -1` at `WatchModeController.cs:2599`
via `HasPendingOverlapBridgeRetarget`), disables the 45-frame bridge-exit
safety, and introduces a new bug where watching a primary and having an
unrelated overlap cycle expire would still clobber the camera before the
fallback could recover. Those problems are all downstream of the same
missing guard this plan now adds. Guarding at the entry of the overlap
handler means `-1` is still set only when it should be, the bridge resolver
still works as designed, and the 45-frame safety is preserved.

## Files to touch

- `Source/Parsek/WatchModeController.cs`
  - `HandleOverlapCameraAction`, case `ExplosionHoldStart` (`:725-737`)
    -- add cycle-mismatch guard at top.
  - `HandleOverlapCameraAction`, case `ExplosionHoldEnd` (`:739-754`)
    -- add cycle-mismatch guard at top.
- `Source/Parsek.Tests/WatchModeControllerTests.cs`
  - New tests (below).
- `CHANGELOG.md` -- 1-line entry under the current cycle (draft below).
- `docs/dev/todo-and-known-bugs.md` -- new numbered section (draft below).

## Test plan

Existing `WatchModeControllerTests.cs` uses reflection to seed private
fields (confirmed via review). All four new tests follow that pattern and
assert on `ParsekLog.TestSinkForTesting` output. None need real
`FlightCamera` or Unity GameObjects.

1. `HandleOverlapCameraAction_ExplosionHoldStart_NonWatchedCycle_NoOp`
   - Arrange: reflection-set `watchedRecordingIndex = 0`,
     `watchedOverlapCycleIndex = 5`.
   - Act: dispatch `CameraActionEvent { Index = 0, Action = ExplosionHoldStart,
     NewCycleIndex = 3, AnchorPosition = Vector3.zero, HoldUntilUT = 100 }`.
   - Assert: log contains `"hold start for #0 cycle=3 ignored (watching cycle=5)"`;
     `watchedOverlapCycleIndex` unchanged (still 5); `overlapRetargetAfterUT`
     unchanged.
2. `HandleOverlapCameraAction_ExplosionHoldEnd_NonWatchedCycle_NoOp`
   - Mirror of #1 with `ExplosionHoldEnd`. Assert cycle index unchanged and
     the `"hold end ... ignored"` log emitted.
3. `HandleOverlapCameraAction_ExplosionHoldStart_WatchedCycle_ProcessesNormally`
   - Arrange: `watchedOverlapCycleIndex = 5`.
   - Act: dispatch `ExplosionHoldStart { NewCycleIndex = 5, HoldUntilUT = 200 }`.
   - Assert: log `"holding at explosion for #0 cycle=5"` emitted; cycle
     index set to `-2`; `overlapRetargetAfterUT == 200`.
4. `HandleOverlapCameraAction_ExplosionHoldEnd_WatchedCycle_ProcessesNormally`
   - Arrange: `watchedOverlapCycleIndex = 5`.
   - Act: dispatch `ExplosionHoldEnd { NewCycleIndex = 5 }`.
   - Assert: log `"bridged at quiet expiry"` emitted; cycle index set to
     `-1`; `overlapRetargetAfterUT == -1`.

In-game manual verification (catches what unit tests cannot):

1. Record a ~60 s mission. Play back as Gloops (`period < duration`, e.g.
   period=30, duration=60). Enter watch on the primary at t=0. Observe
   cycle 1 launch at t=30; stay on cycle 0. Cycle 0 expires at t=60; camera
   hands off to the currently-live primary (cycle 1 or 2 depending on
   warp). Visually: no flicker when cycle 0 expiry happens cleanly.
2. Deep stack: period=20, duration=80 -> up to 4 cycles live at once.
   Enter watch on cycle 0. At t=80 the camera should jump straight to the
   newest primary (cycle 3 or 4), not to cycle 1.
3. Regression: non-loop recording, watch to natural end. Verify unchanged.
4. Regression: regular loop (`period == duration`). Verify camera hands off
   at cycle boundary to the new ghost, unchanged.
5. Regression: explosion of a non-watched overlap cycle. Verify camera
   does not flicker off the watched cycle.
6. `[` / `]` keys: verify unchanged manual cycle/exit behaviour.

## Logging impact

New log lines (rate-limited, verbose -- will not spam):

- `"Overlap: hold start for #<idx> cycle=<M> ignored (watching cycle=<N>)"`
- `"Overlap: hold end for #<idx> cycle=<M> ignored (watching cycle=<N>)"`

No existing log lines are removed or reworded. `validate-ksp-log.ps1` and
`LogContractTests` do not reference either of the old or new strings
(checked by reviewer via grep).

## CHANGELOG entry (draft, to be staged in the implementation commit)

```
### Fixed
- Watch camera no longer jumps to the newest iteration when an unrelated
  overlap cycle of the watched Gloops recording expires; camera stays with
  the watched ghost until that ghost's own cycle ends.
```

## `docs/dev/todo-and-known-bugs.md` entry (draft)

Slot number to be chosen at implementation time based on the latest section
numbers. Wording:

```markdown
## ~~XXX. Watch camera jumps to next iteration when an unrelated overlap cycle expires~~

**Source:** user report `2026-04-18`.

**Symptom:** while watching a ghost of a Gloops recording (period <
duration), the camera abruptly retargets to the current primary whenever
another overlap cycle of the same recording expires -- even when the
watched cycle is still playing normally. With a short period (deep overlap
stack), the camera can "walk" through cycle expiries and never settle on
the ghost the user chose.

**Cause:** `HandleOverlapCameraAction` did not filter
`ExplosionHoldStart` / `ExplosionHoldEnd` events by `NewCycleIndex`, so
every overlap expiry on a watched recording reset
`watchedOverlapCycleIndex` and ran the bridge resolver, which retargets to
the current primary.

**Fix:** added a cycle-mismatch early-return at the top of both cases
(`ExplosionHoldStart` and `ExplosionHoldEnd`) in `HandleOverlapCameraAction`.
Events whose `NewCycleIndex` is not the watched cycle are logged at
verbose-rate-limited level and skipped. Events for the watched cycle keep
their existing behaviour (hold -> bridge -> resolver retargets to the
newest primary), which matches the user-stated "follow until this ghost
ends, then switch to the most-recently-launched".

**Status:** ~~Fixed~~. Size: XS.
```

## Known follow-ups (explicitly out of scope)

- Overlap ghosts have audio muted by `GhostPlaybackLogic.MuteAllAudio`
  (`GhostPlaybackEngine.cs:1061`). Users who enter watch on the primary and
  then stay with that cycle through its demotion-to-overlap will lose audio
  at the cycle boundary. Addressing this requires unmuting the ghost the
  camera is following or gating the mute on "not watched". Track separately.
- The `[` and `]` manual-cycle bindings are unchanged by this fix (they
  operate on `watchedRecordingIndex`, not `watchedOverlapCycleIndex`).

## Implementation checklist

1. [ ] Add cycle-mismatch guards to both `ExplosionHoldStart` and
       `ExplosionHoldEnd` cases in `HandleOverlapCameraAction`
       (`WatchModeController.cs`).
2. [ ] Add the 4 unit tests in `WatchModeControllerTests.cs`.
3. [ ] Stage `CHANGELOG.md` update in the same commit.
4. [ ] Stage `docs/dev/todo-and-known-bugs.md` section in the same commit.
5. [ ] Build; verify deployed DLL contains a distinctive new UTF-16 string
       (e.g. `"hold end for #"`).
6. [ ] Run the full test suite.
7. [ ] In-game verification: Gloops deep stack, regular loop, non-loop, and
       `[`/`]` regression.
8. [ ] Commit on `fix/watch-camera-loop-switch`, push, open PR.
