# Plan: #460 - mainLoop-dominated playback-budget breakdown (third one-shot latch)

## Problem

Post-B3 playtest `logs/2026-04-18_1947_450-b3-playtest/KSP.log` fired four
`Playback frame budget exceeded` WARNs. The first (line 14355) was spawn-
driven at 12.2 ms and correctly consumed the existing #414 + #450 Phase A
one-shot latches, which produced the expected breakdown lines at
14356-14499. The next three fired at `ghosts=0, warp=1x`, with no
breakdown attribution because both latches were already burnt:

```
28224 19:45:37  Playback frame budget exceeded: 24.8ms (0 ghosts, warp: 1x) | suppressed=12
31864 19:46:21  Playback frame budget exceeded: 17.7ms (0 ghosts, warp: 1x) | suppressed=3
96325 19:46:54  Playback frame budget exceeded: 18.8ms (0 ghosts, warp: 1x)
```

In all three post-B3 spikes the WARN also reports `spawn=0 destroy=0`,
so per-ghost spawn/destroy work is not the culprit - the main loop
itself (per-trajectory iteration including any overlap-ghost work it
spawns, plus the end-of-frame deferred/observability phases) owns the
cost. The pre-B3 smoke breakdown (`2026-04-18_0221`, also 0 ghosts,
0 spawn) captured the shape via the #414 latch: `mainLoop=8.55 ms,
trajectories=289` - ~30 us per top-level trajectory. Scaling that to
the 17-25 ms post-B3 spikes implies ~75 us per trajectory on those
frames, which is large enough to matter but we have no phase attribution.

**Note on the "0 ghosts means 0 spawn" simplification.** The
`ghostsProcessed` counter is incremented early in `UpdatePlayback`
(`GhostPlaybackEngine.cs:444`) when a trajectory's state was already
`HasLoadedGhostVisuals` at frame start. Same trajectory can still
`SpawnGhost` later in the frame (primary path at `:737`, overlap path
at `:1225`), and overlap ghosts are iterated in their own loops
(`UpdateExpireAndPositionOverlaps` at `:1301`). So `ghostsProcessed == 0`
alone does NOT imply `spawnMicroseconds == 0`. The post-B3 log coincides
because the observed spikes sit in warp / policy-hold where nothing
was spawning, but the gating must not assume that. See "Proposed
third-latch design" for the combined classifier.

**Concern:** without a dedicated latch the cost attribution is guesswork.
Candidate phases: per-trajectory dispatch (the `for` loop in
`UpdatePlayback`), lazy reentry-pending-flag scans, overlap cycle
bookkeeping, deferred event drain (`deferredCreated`, `deferredCompleted`),
post-loop observability capture.

## Scope

Diagnostic only. Third one-shot latch independent of #414 and #450,
gated on the mainLoop-dominated zero-ghost case. Produces a single WARN
on the next such spike. **Not** a fix - the follow-up Phase B branch is
gated on the breakdown's output, same pattern as #414->#450 and
#450 Phase A->B.

## Current telemetry (what's already captured)

`PlaybackBudgetPhases` already carries most of what this latch needs:

- `mainLoopMicroseconds` - total per-trajectory dispatch time including
  any overlap-ghost work spawned from it (the thing we're attributing).
- `trajectoriesIterated` - count of TOP-LEVEL trajectories dispatched
  (not overlap ghost iterations - see gap below).
- `deferredCreatedEventsMicroseconds` + `createdEventsFired`.
- `deferredCompletedEventsMicroseconds` + `completedEventsFired`.
- `observabilityCaptureMicroseconds` - post-loop ghost observability capture.
- `explosionCleanupMicroseconds` - post-loop explosion sweep.
- `spawnMicroseconds`, `destroyMicroseconds` - used by the new gate to
  rule out per-ghost work and also logged for sanity checking.

### Telemetry gap: overlap-ghost iteration count

`mainLoopMicroseconds` includes the cost of overlap-ghost iteration
(`UpdateExpireAndPositionOverlaps` at `GhostPlaybackEngine.cs:1301`),
but `trajectoriesIterated` only counts top-level trajectories. A
`meanPerTraj = mainLoopMicroseconds / trajectoriesIterated` ratio
therefore conflates "slow per-trajectory dispatch" with "many overlap
ghosts per trajectory", which are two different Phase B fixes. To pick
the right fix from the WARN alone, Phase A adds one new field:

- `overlapGhostIterationCount` (int) - total count of overlap-ghost
  iterations across every primary trajectory in this frame. Incremented
  inside the overlap loop in `UpdateExpireAndPositionOverlaps`.

This is the only new field. Every other bucket (mainLoop total,
deferred events, observability, explosion cleanup) already resolves
to a top-level sub-phase without needing a denominator.

### Why NOT add lazyReentryPendingScanCount etc now

The reviewer also floated adding more specific counters up-front
(`lazyReentryPendingScanCount`, per-sub-phase mainLoop timers). Those
are speculative: we don't know whether the cost sits in reentry
scanning, per-trajectory dispatch, or overlap iteration. Adding them
now commits us to instrumentation that may tell us nothing. The plan
ships just enough observability to localize the bucket, then Phase B
adds targeted timers inside whichever one dominates.

**Cheapest plan.** Phase A ships one new struct field
(`overlapGhostIterationCount`) plus one counter write inside the
overlap loop, plus the third latch. Phase B adds per-sub-phase timers
only inside whichever bucket the WARN reveals.

## Proposed third-latch design

### Gating condition

Fire on the NEXT frame where all of these hold:

1. `totalMs > PlaybackBudgetThresholdMs` (8 ms) - standard budget-exceeded
   gate, already checked by the calling code path.
2. `ghostsProcessed == 0` - no ghosts were active at frame start; the
   spike cannot be attributed to ongoing per-ghost rendering.
3. `phases.spawnMicroseconds < MainLoopBreakdownMaxSpawnMicroseconds`
   (1 000 us = 1 ms) - exclude frames where a spawn happened mid-frame
   (which may not increment `ghostsProcessed` but still belongs to
   the #414/#450 territory).
4. `phases.destroyMicroseconds < MainLoopBreakdownMaxDestroyMicroseconds`
   (1 000 us = 1 ms) - symmetric to #3; a same-frame destroy on a
   ghost that never became active is also ghost-activity-adjacent.
5. `phases.mainLoopMicroseconds >= MainLoopBreakdownMinMainLoopMicroseconds`
   (10 000 us = 10 ms) - the main loop itself accounts for enough of
   the total to be the dominant suspect (floor check). Inclusive so
   an exact 10.00 ms mainLoop frame is still worth localizing (the
   next sub-ms of noise tips it over the 8 ms total budget anyway).
6. `phases.mainLoopMicroseconds > phases.deferredCreatedEventsMicroseconds`
   AND `phases.mainLoopMicroseconds > phases.deferredCompletedEventsMicroseconds`
   AND `phases.mainLoopMicroseconds > phases.observabilityCaptureMicroseconds`
   AND `phases.mainLoopMicroseconds > phases.explosionCleanupMicroseconds` -
   dominance check: mainLoop is strictly the largest non-spawn/non-destroy
   phase. An absolute floor alone does not prove dominance - a frame with
   `mainLoop=11 ms, deferredCompleted=28 ms` belongs to the deferred-events
   bucket (future #460-sibling diagnostic), not here. Keeping the #460
   latch reserved for true mainLoop-dominated spikes preserves the
   single-sample-per-session discipline.
7. `!s_mainLoopBreakdownOneShotFired` - new latch, independent of #414
   and #450. Consumed the first time the other conditions all hold.

**Threshold rationale (gate 5):** 10 ms is above the 8 ms total-budget
threshold (otherwise any exceeded 0-ghost frame at all would trip),
and below the 17-25 ms range observed in the post-B3 log (so all three
real spikes would have tripped). 10 ms also keeps the gate above the
pre-B3 smoke's already-captured `mainLoop=8.55 ms` sample so the
latch is not wasted reproducing a data point we already have. 15 ms
(the #450 threshold) is too high: a 12 ms mainLoop 0-ghost spike is
still worth localizing, and #450's threshold reflects a single-spawn
cliff that doesn't apply here.

**Dominance check (gate 6):** required because the WARN's whole
purpose is to say "mainLoop is where you should instrument next".
Without this gate, a 20 ms frame that is really 4 ms mainLoop + 16 ms
`deferredCompletedEvents` would still fire the #460 breakdown and
point the reader in the wrong direction.

**Spawn/destroy suppression (gates 3, 4):** the post-B3 spikes had
`spawn=0 destroy=0`. But a frame where a ghost spawns and is
immediately destroyed without ever entering `ghostsProcessed` is
possible - and that frame belongs to #450's bimodal territory, not
#460. Symmetric destroy gate catches the mirror case. 1 ms threshold
leaves room for ghost state bookkeeping that can pay a few hundred
microseconds into `spawnStopwatch` without being a true spawn.

### Classifier rationale (gates 2 + 3 + 4)

The goal is to localize costs that per-ghost instrumentation (#414,
#450) does not already cover. Three complementary signals are needed:

- `ghostsProcessed == 0` rules out ongoing per-ghost rendering cost
  (the common case #414 already characterizes).
- `spawnMicroseconds < 1 ms` rules out mid-frame spawns on trajectories
  whose `state` was null at frame start (those don't increment
  `ghostsProcessed` but still belong to #450's bimodal territory).
- `destroyMicroseconds < 1 ms` rules out the mirror case.

Together they express: **this latch fires only when the spike cannot
be attributed to any per-ghost activity.**

### Independence from #414 and #450

- #414's `s_playbackBreakdownOneShotFired` already fired on the first
  spawn-driven spike (line 14355 in the log). A second, separate latch
  is required so this breakdown still captures the next 0-ghost spike.
- #450's `s_buildBreakdownOneShotFired` is tied to `spawnMaxMicroseconds
  >= 15 ms`. It CAN in principle fire on a frame with `ghostsProcessed
  == 0` if a single ghost spawned and was destroyed same-frame at > 15 ms
  of build cost (no counter increment), but that case is also exactly
  what gates 3+4 exclude from #460 - so in practice the two latches
  fire on disjoint frames. We still make them independent so that
  all three latches are armed/consumed separately, and tests exercise
  every pairwise pre-consumption case.

### WARN format

Single line, mirrors the tone of the #414 and #450 breakdowns:

```
[WARN][Diagnostics] Playback mainLoop breakdown (one-shot, first mainLoop-dominated spike):
  total=X.Xms mainLoop=X.XXms trajectories=N overlapIterations=M
  meanPerTraj=X.XXus meanPerDispatch=X.XXus
  deferredCreated=X.XXms (E evts) deferredCompleted=X.XXms (E evts)
  observabilityCapture=X.XXms explosionCleanup=X.XXms
  spawn=X.XXms destroy=X.XXms ghosts=0 warp=Xx
```

All sub-phases use the same `F2` millisecond precision as the #414 line
(`F2` for fields that routinely fall below 1 ms; `F1` only on the total
which is always > 8 ms). Two per-dispatch means are reported because
the `mainLoop` window covers both top-level trajectories and overlap
iterations:

- `meanPerTraj` = `mainLoopMicroseconds / trajectoriesIterated` - the
  headline number from the pre-B3 reference (~30 us/traj). Useful for
  direct comparison against that baseline. Zero-trajectory frames
  (degenerate) render this field as `n/a` instead of a division
  result, so the reader notices the degenerate case instead of seeing
  a misleading huge number from a tiny divisor.
- `meanPerDispatch` = `mainLoopMicroseconds / (trajectoriesIterated +
  overlapGhostIterationCount)` - accounts for overlap fan-out. When
  the same value as `meanPerTraj`, no overlap work happened and the
  top-level loop owns everything. When much smaller than `meanPerTraj`,
  overlap iteration is a significant chunk of the mainLoop cost and
  Phase B should focus on overlap instead of top-level dispatch.
  Zero-divisor case rendered as `n/a` for the same reason.

**Rule for the reader:** the largest non-spawn-non-destroy field in
the WARN is the Phase B target. The two means disambiguate "top-level
loop" from "overlap fan-out" within the mainLoop bucket.

Emitted only when all seven gating conditions hold (latch-not-fired,
ghosts==0, spawn<1ms, destroy<1ms, mainLoop>=10ms, mainLoop exceeds
summed deferred events, mainLoop exceeds observability+explosionCleanup
buckets individually). Zero steady-state log output, allocation-free on
healthy frames (the struct populate already happens for #414/#450;
reading the same fields is a handful of integer comparisons plus one
long sum for the deferred-events-total short-circuit).

### Reset / test seams

- Add `s_mainLoopBreakdownOneShotFired` alongside the two existing latches.
- Extend `ResetPlaybackBreakdownOneShotForTesting()` and
  `ResetForTesting()` to clear all three latches.
- Add `SetBug414BreakdownLatchFiredForTesting()` companion:
  `SetBug450BreakdownLatchFiredForTesting()` (new - not present today
  because #450's independence test only needed to pre-consume #414).
  This lets #460 tests pre-consume either prior latch individually to
  verify three-way independence.
- #414's `SetBug414BreakdownLatchFiredForTesting` already exists; reuse it.

## New fields

One new field on `PlaybackBudgetPhases`:

- `int overlapGhostIterationCount` - total overlap-ghost iterations
  this frame. Populated by incrementing a new `int
  frameOverlapGhostIterationCount` inside `UpdateExpireAndPositionOverlaps`
  (once per iteration of the inner `for` loop, before any continue).

No new stopwatches, no spawn/destroy instrumentation, no additions to
any other struct. Phase B adds targeted timers inside whichever bucket
the #460 WARN localizes.

## Files touched

- `Source/Parsek/Diagnostics/DiagnosticsStructs.cs`
  - New `int overlapGhostIterationCount` field on `PlaybackBudgetPhases`.
- `Source/Parsek/Diagnostics/DiagnosticsComputation.cs`
  - New `MainLoopBreakdownMinMainLoopMicroseconds` const (10 000 us).
  - New `MainLoopBreakdownMaxSpawnMicroseconds` const (1 000 us).
  - New `MainLoopBreakdownMaxDestroyMicroseconds` const (1 000 us).
  - New `s_mainLoopBreakdownOneShotFired` private static field.
  - New `SetBug450BreakdownLatchFiredForTesting()` test seam (companion
    to the existing `SetBug414BreakdownLatchFiredForTesting()`).
  - Extended `ResetPlaybackBreakdownOneShotForTesting()` and
    `ResetForTesting()` to reset the new latch.
  - New third branch inside `CheckPlaybackBudgetThresholdWithBreakdown`
    (after the #450 branch, following the same shape), gated on the
    seven conditions above, emits the single-line WARN.
- `Source/Parsek/GhostPlaybackEngine.cs`
  - New private field `int frameOverlapGhostIterationCount` next to
    the existing `frameSpawnCount` / `frameDestroyCount`.
  - Reset to 0 at the top of `UpdatePlayback` alongside the other
    per-frame counters.
  - Increment inside `UpdateExpireAndPositionOverlaps`'s inner `for`
    loop (once per iteration, before any continue).
  - Copy into `PlaybackBudgetPhases.overlapGhostIterationCount` in
    the existing phases populate block.
- `Source/Parsek.Tests/Bug460MainLoopBreakdownTests.cs` (new) - xUnit
  test class mirroring `Bug450BuildBreakdownTests`. See Tests below.

**Not touched:** no new stopwatches, no spawn/destroy code paths,
no loop/overlap decomposition work (#406 follow-up), no CHANGELOG
(per `feedback_changelog_style` CHANGELOG is user-facing only; this
is diagnostic plumbing that does not change user-observable behaviour).

## Tests (`Bug460MainLoopBreakdownTests.cs`)

Class-level setup mirrors `Bug450BuildBreakdownTests`:
`[Collection("Sequential")]` (shared latch state), `ResetForTesting` in
ctor and Dispose, log sink capture via `ParsekLog.TestSinkForTesting`,
verbose override on.

Helper: `MainLoopDominatedPhases()` builds a `PlaybackBudgetPhases`
matching the shape of the post-B3 `24.8 ms, 0 ghosts` spike:
- `mainLoopMicroseconds = 20_000` (20 ms, above the 10 ms floor)
- `trajectoriesIterated = 289` (from the pre-B3 smoke reference)
- `overlapGhostIterationCount = 0`
- `spawnMicroseconds = 0`, `destroyMicroseconds = 0`
- `explosionCleanupMicroseconds = 50`
- `deferredCreatedEventsMicroseconds = 2_000`, `createdEventsFired = 3`
- `deferredCompletedEventsMicroseconds = 2_000`, `completedEventsFired = 1`
- `observabilityCaptureMicroseconds = 750`
- All spawn/heaviest-spawn sub-fields zeroed.

With these values, gate 5 (mainLoop > 10 ms) and gate 6 (mainLoop
> every other non-spawn/destroy phase) both pass. Total budget:
feed `24_800` us to the check.

Tests (every test has a concrete "what makes it fail" justification):

1. **`AboveGates_MainLoopDominated_FiresBreakdown_WithAllFields`**
   Feed a 24_800 us total with the helper phases. Assert exactly one
   WARN line contains `Playback mainLoop breakdown` and all the
   per-phase tokens: `mainLoop=20.00ms`, `trajectories=289`,
   `overlapIterations=0`, `meanPerTraj=69.20us`,
   `meanPerDispatch=69.20us`, each deferred-* ms and count,
   `observabilityCapture=0.75ms`, `explosionCleanup=0.05ms`,
   `spawn=0.00ms`, `destroy=0.00ms`, `ghosts=0`, `warp=1x`.
   *Fails if: format string drops a field, microsecond divisor uses
   the wrong units, latch forgets to fire when all gates pass.*

2. **`MeanPerTrajectory_IsUsPerDispatch_NotMs`**
   Pass `mainLoopMicroseconds=20_000`, `trajectoriesIterated=100`,
   `overlapGhostIterationCount=0` -> assert the WARN shows
   `meanPerTraj=200.00us` and `meanPerDispatch=200.00us`, not
   `0.20ms`.
   *Fails if: the units get confused or the divisor uses the ms value.*

3. **`ZeroTrajectoriesAndOverlap_RendersMeanAsNa`**
   Pass `trajectoriesIterated=0`, `overlapGhostIterationCount=0`
   (degenerate - the main loop ran nothing but still spent 20 ms
   somehow; implausible in practice but defensively tested). Assert
   the WARN still emits, `meanPerTraj=n/a` and `meanPerDispatch=n/a`
   appear verbatim, no exception is thrown.
   *Fails if: implementation does integer divide without a guard -
   would throw DivideByZeroException on integer 0.*

4. **`OverlapFanOut_MeanPerDispatchSmallerThanMeanPerTraj`**
   Pass `mainLoopMicroseconds=20_000`, `trajectoriesIterated=100`,
   `overlapGhostIterationCount=300`. Assert `meanPerTraj=200.00us`
   and `meanPerDispatch=50.00us`. Demonstrates the overlap-fan-out
   signal the plan's "rule for the reader" relies on.
   *Fails if: `meanPerDispatch` denominator omits overlap count or
   uses a wrong formula.*

5. **`GhostsProcessedNonZero_DoesNotFireBreakdown`**
   Feed the helper phases but with `ghostsProcessed=1`. Assert the
   mainLoop breakdown line does NOT appear, and the latch is still
   armed - then fire the helper with `ghostsProcessed=0` and assert
   it DOES emit.
   *Fails if: gate omits the `ghostsProcessed==0` check.*

6. **`SpawnOverOneMs_DoesNotFireBreakdown`**
   Feed `ghostsProcessed=0` but `spawnMicroseconds=2_000` (2 ms -
   could be a same-frame spawn+destroy on a trajectory whose `state`
   was null at frame start, classic #450 territory). Assert #460
   breakdown does NOT fire and latch is still armed for a real
   no-spawn spike.
   *Fails if: gate 3 (`spawnMicroseconds < 1_000`) is missing - this
   is the exact ambiguity the plan reviewer flagged.*

7. **`DestroyOverOneMs_DoesNotFireBreakdown`**
   Symmetric to #6 but with `destroyMicroseconds=2_000`.
   *Fails if: gate 4 is missing.*

8. **`MainLoopBelowFloor_DoesNotFireBreakdown`**
   Feed `mainLoopMicroseconds=8_000` (below 10 ms floor) but total
   over the 8 ms budget (pad with 8 ms deferredCompleted to trip the
   first WARN). Assert no breakdown line and latch still armed. Then
   fire helper and assert it DOES emit.
   *Fails if: gate 5 uses total-ms-exceeded instead of the mainLoop
   floor.*

9. **`MainLoopNotDominant_DoesNotFireBreakdown`**
   Feed `mainLoopMicroseconds=11_000` (above 10 ms floor), but
   `deferredCompletedEventsMicroseconds=30_000` (dominant non-spawn
   bucket). Assert no breakdown line and latch still armed - the
   deferred-events-dominated case is NOT in scope for #460. Then
   fire helper and assert it DOES emit.
   *Fails if: gate 6 (dominance check) is missing.*

10. **`TotalBelowBudget_DoesNotFireBreakdown`**
    Feed `totalMicroseconds=7_000` (healthy frame) with helper phases.
    Assert no line and latch still armed.
    *Fails if: the new branch runs before the `totalMs > threshold`
    check - would burn the latch on healthy frames.*

11. **`LatchIndependentOfBug414Latch`**
    Pre-consume `s_playbackBreakdownOneShotFired` via
    `SetBug414BreakdownLatchFiredForTesting()`. Fire the helper spike.
    Assert the #460 breakdown emits, the #414 breakdown does NOT.
    *Fails if: the new branch is gated on
    `s_playbackBreakdownOneShotFired` or shares the latch.*

12. **`LatchIndependentOfBug450Latch`**
    Pre-consume `s_buildBreakdownOneShotFired` via the new
    `SetBug450BreakdownLatchFiredForTesting()`. Fire the helper spike.
    Assert the #460 breakdown emits. (Note: #450 wouldn't fire anyway
    at `spawnMaxMicroseconds=0` - gates 3/4 exclude it - but the test
    proves the new latch is not gated on #450's state either.)
    *Fails if: the new seam or the latch coupling is wrong.*

13. **`SpawnDominated0GhostSpike_FiresBug414And450_But_NOT_460`**
    Fresh session. Fire a bimodal-single-spawn spike (the `Bug450`
    helper shape: 28 ms spawn, 11 ms mainLoop, `ghosts=0`). Assert
    #414 AND #450 breakdowns emit. Assert #460 breakdown does NOT
    fire - gate 3 (`spawnMicroseconds < 1 ms`) correctly suppresses
    it. Then fire the #460 helper shape and assert the #460 breakdown
    DOES emit (latch still armed).
    *Fails if: gate 3 is missing or wrong - the reviewer-flagged
    plan bug.*

14. **`FreshSession_HelperSpike_FiresBug414And460_But_NOT_450`**
    Fresh session, feed the #460 helper shape (20 ms mainLoop, 0
    spawn, 0 ghosts). Assert #414 fires (first spike in session),
    #460 fires (all gates pass), #450 does NOT (no spawn). Second
    spike of the same shape: only the rate-limited budget-exceeded
    WARN lands, no breakdowns.

15. **`ResetForTesting_ReArmsAllThreeLatches`**
    Fire a spike that consumes all three latches (use test 14's
    ordering). Reset. Fire the same spikes. Assert each breakdown
    line appears twice in `logLines`.
    *Fails if: `ResetForTesting` forgets the new latch.*

16. **`BelowThreshold_DoesNotFireAnyBreakdown`**
    Healthy frame. Assert no breakdown lines. Next real spike still
    emits them.

## Plan review resolutions

Plan reviewer (clean-context codex pass, 2026-04-18) raised issues
that are now folded into the design above. Summary of the resolutions:

- **Q1 (spawn suppression):** Yes, added as gate 3 (`spawnMicroseconds
  < 1 ms`). Without it a spawn-driven 0-ghost spike could consume the
  #460 latch and defeat its purpose.
- **Q2 (destroy suppression):** Yes, added as gate 4 (symmetric).
- **Q3 (threshold):** Keep 10 ms. 8 ms too low (collides with the
  already-captured pre-B3 8.55 ms sample); 12 ms needlessly conservative.
- **Q4 (single vs multi line):** Single line. #460 is one logical
  group (phase attribution), unlike #450 which had aggregate +
  heaviest-spawn as two groups.
- **Dominance check:** Added as gate 6. Absolute floor alone does not
  prove mainLoop is dominant - a 4 ms mainLoop + 16 ms
  `deferredCompletedEvents` frame would fire a misleading WARN otherwise.
- **Overlap iteration count:** Added `overlapGhostIterationCount` as a
  new `PlaybackBudgetPhases` field. Without it `meanPerTraj` conflates
  "slow top-level dispatch" with "many overlap ghosts per trajectory",
  which are two different Phase B fixes and need different Phase B
  instrumentation. The WARN now reports `meanPerTraj` and
  `meanPerDispatch` so the reader can distinguish.
- **Test #3 contradiction:** Plan previously said "divide by
  `max(1, trajectoriesIterated)`" but then expected `n/a` output for
  zero. Resolved: render `n/a` sentinel when the denominator is 0,
  and the test asserts that literal string.
- **Test #9 open question:** Resolved. Spawn-dominated 0-ghost spike
  fires #414 + #450, NOT #460 (gate 3 suppresses it). Test #13 is
  the new form of the test.
- **"0 ghosts = 0 spawn" premise:** Corrected throughout the Problem
  and classifier sections - they now state the correct relationship
  (ghostsProcessed measures ghosts active at frame start, spawn and
  destroy stopwatches accumulate independently during the frame).

The corresponding todo entry at `docs/dev/todo-and-known-bugs.md:71`
retains its original wording because it is the bug report (the
author's pre-plan observation), not the plan itself.

## Rollout / risk

- Zero behavior change on any playback path.
- Steady-state overhead: minimal, allocation-free. The #460 WARN
  branch itself is truly zero-cost on healthy frames (early return
  inside `CheckPlaybackBudgetThresholdWithBreakdown` when
  `totalMs <= PlaybackBudgetThresholdMs` fires before the latch check),
  but the engine adds three new ops per `UpdatePlayback` tick: (i) an
  `int = 0` reset in the frame-counter block; (ii) one `int` copy into
  the existing `PlaybackBudgetPhases` populate block; (iii) an `int++`
  per overlap-ghost iteration inside `UpdateExpireAndPositionOverlaps`.
  These are the same order of magnitude as the existing
  `frameSpawnCount++` ops #414 already pays and do not allocate.
- Memory: one new `int` field (`overlapGhostIterationCount`) on
  `PlaybackBudgetPhases` (4 bytes), one new `int` field
  (`frameOverlapGhostIterationCount`) on `GhostPlaybackEngine`
  (4 bytes), one new static `bool` on `DiagnosticsComputation`.
- Log output: zero new lines in steady state. One new line on the
  first qualifying spike per session.

## Post-ship validation

On the next playtest, look for a `Playback mainLoop breakdown` WARN in
KSP.log. The breakdown fields point to whichever bucket dominates (the
largest `X.XXms` value). Decision criteria for Phase B:

| Dominant phase | Phase B candidate |
|---|---|
| `mainLoop` with `meanPerDispatch ~= meanPerTraj` (no overlap fan-out) and high value (~50+ us/traj) | Add per-sub-phase timers inside the top-level `for` loop: lazy reentry pending scan, position update, flag checks. Repeat the diagnose-first pattern. |
| `mainLoop` with `meanPerDispatch << meanPerTraj` (overlap fan-out dominates) | Profile `UpdateExpireAndPositionOverlaps` - the per-overlap cost or the overlap count is driving the spike, not top-level dispatch. |
| `mainLoop` with low `meanPerTraj` and `meanPerDispatch` + many trajectories | Cull inactive trajectories from the iteration set (e.g. skip disabled / past-end ghosts earlier). |
| `deferredCompletedEvents` | Profile the `PlaybackCompleted` subscribers in `ParsekPlaybackPolicy`; likely a handler doing heavy work. This case is NOT consumed by #460's latch (gate 6) - a separate #460-sibling diagnostic would be added if observed. |
| `deferredCreatedEvents` | Same, for `OnGhostCreated` subscribers. Also not in #460 scope. |
| `observabilityCapture` | Profile `CaptureGhostObservability`; may be iterating over destroyed ghosts. Also not in #460 scope. |

## CHANGELOG entry

**None.** Per `feedback_changelog_style`, CHANGELOG is user-facing only.
This PR is diagnostic plumbing that emits a single WARN on a spike that
users have no action on. Record lives in `docs/dev/todo-and-known-bugs.md`
(#460 heading flipped to `~~460.~~` with a "Fix: diagnostic only" note
pointing at this plan doc).

## Diagnostic logging

The one-shot WARN itself is the diagnostic log. No additional log lines
on the instrumentation path (we reuse `ParsekLog.Warn` on the single
qualifying branch). The test seams
(`SetBug414BreakdownLatchFiredForTesting`, new
`SetBug450BreakdownLatchFiredForTesting`) do not log.
