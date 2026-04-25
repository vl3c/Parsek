# Plan: #414 — per-frame ghost spawn throttle (rev 2, post-review)

## Problem

`GhostPlaybackEngine.UpdatePlayback` builds every eligible ghost's visuals in
the same tick. When several trajectories come into range simultaneously —
typically ~11 s after scene load, when all committed recordings get their
first positional check — the spawn work bunches into a single frame.

Representative zero-ghost (pure-spawn) budget WARNs from the last ~10 days:

| log                                                        | ms    |
| ---------------------------------------------------------- | ----- |
| 2026-04-17_1629_c2-postfix-retest (only breakdown-decomposed frame) | 23.8  |
| 2026-04-11_2136_242-smoke-fixed (outlier)                  | 174.7 |
| 2026-04-10_engine-fx-regression / engine-plume-bug         | 55-59 |
| 2026-04-09_1117_kerbalx-rover                              | 67.1  |
| many others                                                | 15-40 |

The only breakdown-decomposed frame (2026-04-17) attributes the cost:

```
total=23.8ms mainLoop=8.34ms spawn=14.91ms destroy=0.00ms
explosionCleanup=0.00ms deferredCreated=0.23ms deferredCompleted=0.00ms
observabilityCapture=0.30ms trajectories=4 ghosts=0 warp=1x
```

Spawn = 62%. Other phases sum to ~9 ms, dominated by per-trajectory main-loop
dispatch over 4 trajectories. Spawn is the lever with the most headroom.

## Root cause

`BuildGhostVisualsWithMetrics` (`GhostPlaybackEngine.cs:1998`) is called from
nine sites inside the main dispatch tick. `frameSpawnCount` at line 55 is a
diagnostic counter, not a budget guard. Every spawn-eligible trajectory pays
its full build cost in the same frame.

### Call-site taxonomy (critical — revised after Blocker B1/B2 review)

Not every spawn site is safe to throttle. Re-classified:

**Must NEVER throttle** (user-visible failure or state corruption on defer):
- `GhostPlaybackEngine.cs:1871` — `EnsureGhostVisualsLoadedForWatch`. Called
  from `WatchModeController.TryEnterWatchMode` / `TryGetWatchGhostState`.
  Neither caller retries on failure. A throttled watch request is "user
  clicks 'Watch', nothing happens, has to click again" — exactly the kind of
  friction the mod works hard to eliminate.
- `GhostPlaybackEngine.cs:999` — `SpawnGhost` inside the `primaryCycleChanged`
  branch of `UpdateLoopingPlayback`. The old primary is moved to the overlap
  list at lines 990-996 **unconditionally before** this spawn call. If the
  spawn is throttled, the recording has zero primary ghost for one or more
  frames, and the `OnOverlapCameraAction` retarget at line 1020-1026 is
  skipped. The "Warn: SpawnGhost failed" + return at 1002-1004 is already
  there for genuine failures; throttling this path would conflate with that
  rare bug-signal log.

**Safe to throttle** (deferral costs at most a 1-frame visual delay,
self-recovering on next tick):
- `GhostPlaybackEngine.cs:543` — first-ever `SpawnGhost` from the main
  dispatch loop (the primary target of this fix: scene-load warm-up burst).
- `GhostPlaybackEngine.cs:583` — `EnsureGhostVisualsLoaded` for a ghost that
  "entered visible distance tier". Ghost was out of LOD range; 1-frame delay
  when it comes back into range is invisible.
- `GhostPlaybackEngine.cs:871` — `SpawnGhost` inside `UpdateLoopingPlayback`
  non-cycle-change branch (loop primary first spawn, not cycle rebuild).
- `GhostPlaybackEngine.cs:918` — loop re-entered visible distance.
- `GhostPlaybackEngine.cs:1051` — overlap primary re-entered visible
  distance. The overlap-move guard at line 985-996 only runs under
  `primaryCycleChanged`, which is a separate code path; a rehydration from
  hidden-distance is not conflated with cycle rebuild.
- `GhostPlaybackEngine.cs:1158` — overlap ghost re-entered visible distance.
- `GhostPlaybackEngine.cs:2126` — hidden-tier prewarm. Ghost is
  **deliberately not visible**. This is the safest possible throttle victim:
  a 1-frame prewarm delay is invisible by construction.

## Proposed fix

### Single cap with explicit per-call-site gating

`MaxSpawnsPerFrame = 2` (const). Introduced at the engine level:

```csharp
// GhostPlaybackEngine.cs near frameSpawnCount
private const int MaxSpawnsPerFrame = 2;
private int frameSpawnDeferred;  // diagnostic only; reset each frame
```

A small gate method:

```csharp
// Callers that intend to spawn MUST call this first and skip on false.
// Exempt call sites (watch-mode, loop-cycle-change) do NOT call this —
// their spawns still bump frameSpawnCount via the existing increment inside
// BuildGhostVisualsWithMetrics, so they DO consume the cap budget for any
// throttle-eligible sites that run later in the same frame. That is the
// correct semantics: the cap is a total-work-per-frame guard, not a
// throttle-eligible-only guard, so one watch-mode spawn legitimately reduces
// the slots left for elective spawns in the same tick.
private bool TryReserveSpawnSlot(int index, string site)
{
    if (frameSpawnCount >= MaxSpawnsPerFrame)
    {
        frameSpawnDeferred++;
        ParsekLog.VerboseRateLimited("Engine", "spawn-throttle",
            $"Spawn throttled ({site}): #{index} deferred to next frame " +
            $"(used {frameSpawnCount}/{MaxSpawnsPerFrame})", 1.0);
        return false;
    }
    return true;
}
```

Applied at each safe-to-throttle site. Example at line 543:

```csharp
if (!TryReserveSpawnSlot(i, "first-spawn")) continue;
SpawnGhost(i, traj, ctx.currentUT);
```

`EnsureGhostVisualsLoaded` gets a new overload (or opt-in parameter) so that
throttle-opt-in is explicit at call sites, not global:

```csharp
private bool TryEnsureGhostVisualsLoaded(
    int index, IPlaybackTrajectory traj, GhostPlaybackState state,
    double playbackUT, string reason, string site, bool allowThrottle)
{
    if (allowThrottle && !TryReserveSpawnSlot(index, site)) return false;
    return EnsureGhostVisualsLoaded(index, traj, state, playbackUT, reason);
}
```

`EnsureGhostVisualsLoadedForWatch` continues to call the un-throttled
`EnsureGhostVisualsLoaded` directly — watch mode pays whatever build cost it
has to.

### Concern C1 (cap vs bimodal cost) — capture data first, then tune

The 174.7 ms outlier is explained either by "lots of spawns" or "a few very
heavy spawns". We don't know which because the breakdown only tracks the
aggregate. Extend `PlaybackBudgetPhases` with:

```csharp
public int spawnsAttempted;     // count of BuildGhostVisualsWithMetrics calls
public int spawnsThrottled;     // count of TryReserveSpawnSlot returning false
public long spawnMaxMicroseconds; // max single-spawn cost this frame
```

Capture `spawnMaxMicroseconds` by wrapping the stopwatch Start/Stop pair
inside `BuildGhostVisualsWithMetrics` to record a delta and `max` it into the
field. Emit on the one-shot breakdown line so the next spike log tells us
whether `Max(single spawn)` > 8 ms — i.e. whether one ghost alone blows the
budget. If so, a count cap cannot solve this, and the follow-up is per-spawn
time budgeting or a coroutine split.

Ship the cap AND the diagnostic together. The cap fixes the common case
(`spawnsAttempted > 2` on one frame, shown to be true by today's 4-trajectory
breakdown). The diagnostic tells us whether a follow-up is needed.

### Concern C2 (starvation) — address with a test, not extra code

The dispatch loop walks `trajectories[]` in index order. If indices 0-1 need
rehydration every frame (e.g. oscillating around the visible-distance tier
boundary), indices 2+ can be starved. In practice the tier hysteresis + loop
cycle periods are much longer than a frame, so starvation is bounded.

Ship a regression test that pins the ordering: 4 trajectories, all
spawn-eligible; drive `UpdatePlayback` for 3 frames; assert `ghostStates`
grows to 4 and the order is deterministic (indices 0,1 in frame 1, 2,3 in
frame 2). If starvation ever surfaces in the wild, a priority queue is a
follow-up — out of scope here.

### Concern C3 (log format) — disambiguate

Post-loop frame-summary line (`GhostPlaybackEngine.cs:446-448`) becomes:

```
Frame: spawned=N destroyed=M active=K deferred=D
```

One-shot breakdown WARN gets appended fields:

```
... spawn=14.91ms (built=2/2cap throttled=3 max=4.8ms) ...
```

`built=X/Ycap` reads as "X spawns actually built out of a cap of Y".

### Rejected: coroutine-based in-spawn spreading (N1)

Splitting `TryPopulateGhostVisuals` across multiple frames via a coroutine
would let a single heavy spawn amortize its own cost. Rejected for v1 because:
- Partial build state is a new invariant-surface (what frames see a
  half-built ghost? does the ghost render before its engine-FX are live?).
- The four spawns on today's log are not individually heavy (3.7 ms
  average); the problem is count, not per-spawn bulk.
- The C1 diagnostic will tell us objectively whether per-spawn cost is the
  real ceiling. If it is, coroutine-spreading is the right follow-up then.

### Interaction with #406 (loop-cycle reentry FX rebuild) — benign

Loop-cycle primary respawn (line 999) is explicitly **not** throttled (B2),
so the #406-preserved rebuild-reentry-FX-per-cycle invariant is unaffected.
Hidden-tier prewarm (2126) does trigger reentry FX build if the trajectory
has reentry potential; a 1-frame delay there is invisible because the ghost
is hidden at that distance tier. No visual regression.

## Files touched

- `Source/Parsek/GhostPlaybackEngine.cs` — `MaxSpawnsPerFrame` const,
  `TryReserveSpawnSlot` helper, `frameSpawnDeferred` field, seven call-site
  gates, post-loop log line extension, breakdown-struct population (with
  per-spawn max).
- `Source/Parsek/Diagnostics/DiagnosticsStructs.cs` — extend
  `PlaybackBudgetPhases` with `spawnsAttempted`, `spawnsThrottled`,
  `spawnMaxMicroseconds`.
- `Source/Parsek/Diagnostics/DiagnosticsComputation.cs` — extend the
  breakdown log line format.
- `Source/Parsek.Tests/Bug414SpawnThrottleTests.cs` (new).
- `CHANGELOG.md` — short user-facing entry.
- `docs/dev/todo-and-known-bugs.md` — mark #414 ~~fixed~~ with summary.

## Tests

Unit tests on a pure static helper
`GhostPlaybackLogic.ShouldThrottleSpawn(int spawnsThisFrame, int maxPerFrame)`:

1. `(0, 2)` → false
2. `(1, 2)` → false
3. `(2, 2)` → true
4. `(3, 2)` → true

`MaxSpawnsPerFrame` stays a `const int` — no tests against `cap = 0` or
negative values (they'd be unreachable by design). Nit #3 addressed.

Integration-style tests on `GhostPlaybackEngine`:

5. **Happy path (nit #2):** 1 eligible trajectory, drive one frame.
   `ghostStates.Count == 1`, `frameSpawnDeferred == 0` (assert on the field
   via `DiagnosticsState.playbackBudget.spawnsThrottled`), no throttle log
   line emitted.

6. **Burst cap:** 5 eligible trajectories, drive 3 frames. After frame 1:
   `ghostStates.Count == 2`, `frameSpawnDeferred == 3`. After frame 2:
   `ghostStates.Count == 4`, `frameSpawnDeferred == 1`. After frame 3:
   `ghostStates.Count == 5`, `frameSpawnDeferred == 0`. The throttle log line
   fires in frames 1 and 2.

7. **Watch-mode exemption:** 4 trajectories already spawn-eligible this
   frame (cap exhausted after index 0, 1). Then invoke
   `EnsureGhostVisualsLoadedForWatch(index=3, ...)`. Assert it returns true
   (spawn succeeded even over cap) and `ghostStates[3]` is loaded.

8. **Loop-cycle-change exemption:** Simulate a primary cycle change on a
   looping trajectory when cap is already exhausted. Assert the new primary
   spawns and `OnOverlapCameraAction` fires. Construct the scenario via the
   existing looping-ghost test fixture if one exists; otherwise a small
   stub `FrameContext` over a `RecordingBuilder` loop recording.

9. **Starvation regression (C2):** 4 trajectories; drive 2 frames; assert
   `ghostStates` keys include 0,1 after frame 1 and 2,3 after frame 2 —
   i.e. the throttle respects iteration order and does not permute.

10. **Log format (C3):** Capture logs via `ParsekLog.TestSinkForTesting`,
    exceed the budget with a mocked `totalMicroseconds = 20000`, assert the
    breakdown line contains `built=N/2cap throttled=M max=X.Xms`.

## Rollout / risk

- Zero behavior change on saves where ≤ 2 trajectories become spawn-eligible
  per frame (common steady-state).
- Up to 1-frame visual delay (~16 ms at 60 FPS) on first-appearance spawns
  and LOD-tier rehydrations when 3+ ghosts arrive in the same tick.
- Watch mode, loop-cycle boundary, and loop-overlap cycle-rebuild are NOT
  delayed — see exemption list above.
- Hidden-tier prewarm is delayed; invisible by construction.
- Destroy is unthrottled (0 ms measured; teardown needs to complete cleanly).

## Post-ship validation

Next playtest expected to show:
- `0 ghosts` spikes ≤ 10 ms under steady-state.
- Breakdown WARN (if it fires) reports `built=2/2cap throttled=N` with N > 0
  during scene load.
- `max=X.Xms` ≤ ~5 ms on typical saves. If max > 10 ms, requeue the
  per-spawn-cost work as a follow-up (coroutine split or
  heavy-vessel-specific pruning).

If the WARN still fires with `throttled=0` and `max` low, the `mainLoop`
phase is the next culprit and is tracked under its own new bug.
