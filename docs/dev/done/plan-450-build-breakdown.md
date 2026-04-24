# Plan: #450 — per-spawn build-phase breakdown (Phase A diagnostic)

## Problem

Smoke-test `logs/2026-04-18_0221_v0.8.2-smoke/KSP.log:11489` surfaced the
bimodal-single-spawn case that #414 explicitly deferred:

```
Playback budget breakdown (one-shot, first exceeded frame):
total=40.1ms mainLoop=11.34ms
spawn=28.11ms (built=1 throttled=0 max=28.11ms)
destroy=0.00ms ... trajectories=1 ghosts=0 warp=1x
```

One spawn cost 28.11 ms on its own. #414's count cap is structurally useless
here — there was only one spawn to cap. The #450 entry in
`docs/dev/todo-and-known-bugs.md` mandates **investigation-first**: instrument
the spawn build to identify which sub-phase dominates BEFORE committing to a
fix shape.

## Scope (Phase A only)

Ship the diagnostic alone. Do NOT pick the Phase B fix shape until a real
playtest produces data. The diagnostic-then-fix cycle mirrors #414's
original sequencing.

## Current spawn path (reconfirmed from code)

`GhostPlaybackEngine.SpawnGhost` →
`BuildGhostVisualsWithMetrics` (outer `spawnStopwatch` window,
`frameSpawnCount++`, `frameMaxSpawnTicks` update in `finally`) →
`TryPopulateGhostVisuals` (the method with the actual work).

Inside `TryPopulateGhostVisuals`, the natural sub-phase boundaries are:

1. **snapshotResolve** — `GhostVisualBuilder.GetGhostSnapshot(traj)` (trivial
   lookup; included so the breakdown reconciles arithmetically).
2. **timelineFromSnapshot** — `BuildTimelineGhostFromSnapshot` (on snapshot
   path) OR `CreateGhostSphere` (fallback). This is the dominant suspect:
   part instantiation + engine-FX size-boost + audio wiring.
3. **dictionaries** — `BuildPartSubtreeMap` + `BuildSnapshotPartIdSet` +
   `GhostPlaybackLogic.PopulateGhostInfoDictionaries` +
   `InitializeInventoryPlacementVisibility` + `RefreshCompoundPartVisibility`.
4. **reentryFx** — conditional `TryBuildReentryFx` on the
   `HasReentryPotential` branch (skip branch is trivial and not timed).

Residual work (camera pivot + horizonProxy GameObject construction,
`MaterialPropertyBlock`, `SetActive(false)`, `ResetGhostAppearanceTracking`)
is captured arithmetically as `other = deltaTicks − sum(sub-phases)`, which
holds sum-reconciliation as an invariant the tests can assert. Materials-list
allocation sits inside the dictionaries window (it runs between the subtree-map
build and `PopulateGhostInfoDictionaries`, so bracketing it separately would
artificially split a single contiguous region of snapshot-walking work).

## Design (addresses clean-context review findings)

### New enum — `HeaviestSpawnBuildType`

```csharp
internal enum HeaviestSpawnBuildType : byte
{
    None = 0,
    RecordingStartSnapshot = 1,
    VesselSnapshot = 2,
    SphereFallback = 3
}
```

Byte-backed to keep `PlaybackBudgetPhases` a pure value type with no heap
references (review finding #3). The enum is plumbed directly through the
spawn path as an out-param; the log-token string lives in one place (an
extension method beside the enum) so engine and diagnostics layers cannot
drift apart.

### Extended `PlaybackBudgetPhases`

Two groups of new fields:

**Aggregate (sum across every spawn this frame):**
- `long buildSnapshotResolveMicroseconds`
- `long buildTimelineFromSnapshotMicroseconds`
- `long buildDictionariesMicroseconds`
- `long buildReentryFxMicroseconds`
- `long buildOtherMicroseconds` (residual)

**Heaviest-spawn breakdown (latched on whichever single spawn was the most
expensive this frame — addresses review finding #2):**
- `long heaviestSpawnSnapshotResolveMicroseconds`
- `long heaviestSpawnTimelineFromSnapshotMicroseconds`
- `long heaviestSpawnDictionariesMicroseconds`
- `long heaviestSpawnReentryFxMicroseconds`
- `long heaviestSpawnOtherMicroseconds`
- `HeaviestSpawnBuildType heaviestSpawnBuildType`

The heaviest-spawn breakdown is the signal that localizes the bimodal case.
The aggregate rows tell us whether the cost is spread (3 × 10 ms spawns)
or concentrated (1 × 28 ms spawn). Both are needed.

### `GhostPlaybackEngine` instrumentation

Four new pre-allocated `Stopwatch` fields mirroring the established
`spawnStopwatch` pattern (review finding #6):

- `buildSnapshotResolveStopwatch`
- `buildTimelineStopwatch`
- `buildDictionariesStopwatch`
- `buildReentryFxStopwatch`

These accumulate across all spawns in a frame; `ElapsedTicks` at
`PlaybackBudgetPhases` populate-time gives the aggregate.

Per-frame heaviest-breakdown fields (reset each frame alongside the existing
`frameMaxSpawnTicks`):

- `frameHeaviestSpawnSnapshotResolveTicks`
- `frameHeaviestSpawnTimelineTicks`
- `frameHeaviestSpawnDictionariesTicks`
- `frameHeaviestSpawnReentryTicks`
- `frameHeaviestSpawnOtherTicks`
- `frameHeaviestSpawnBuildType`

Per-call "last spawn" fields populated inside `TryPopulateGhostVisuals`
(the sub-phase timer reads `ElapsedTicks` before/after each Start/Stop pair
to compute the delta for *this* call):

- `lastSpawnSnapshotResolveTicks`
- `lastSpawnTimelineTicks`
- `lastSpawnDictionariesTicks`
- `lastSpawnReentryTicks`

`BuildGhostVisualsWithMetrics.finally` — extends the existing
`deltaTicks > frameMaxSpawnTicks` branch to also latch the heaviest
breakdown (review finding #10 BLOCKER): when this call is the new heaviest,
copy `lastSpawn*Ticks` + `buildType` into `frameHeaviestSpawn*Ticks`, and
compute `frameHeaviestSpawnOtherTicks = deltaTicks − sum(lastSpawn*Ticks)`.

### Log format

Two lines (review finding #4) — second line added on the same one-shot
branch inside `DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown`:

```
[WARN][Diagnostics] Playback budget breakdown (one-shot, first exceeded frame): total=... (existing #414 line)
[WARN][Diagnostics] Playback spawn build breakdown (one-shot):
    sum[snapshot=X.XXms timeline=X.XXms dicts=X.XXms reentry=X.XXms other=X.XXms]
    heaviestSpawn[type=X snapshot=X.XXms timeline=X.XXms dicts=X.XXms reentry=X.XXms other=X.XXms total=X.XXms]
```

Emitted only when the aggregate `spawnMicroseconds > 0` (no point logging
a breakdown for a budget spike whose spawn phase didn't contribute).
`grep "build breakdown"` returns just the attribution line; `grep "budget
breakdown"` returns the aggregate line.

### One-shot latch

Separate from #414's latch (review finding on "latch sharing"): a new
`s_buildBreakdownOneShotFired` lives alongside `s_playbackBreakdownOneShotFired`
so the build breakdown fires on the next spike after #450 rollout even if
the session's first spike already consumed #414's latch before this code
loaded. `ResetForTesting` clears both.

**Threshold gate.** The #450 latch additionally requires
`spawnMaxMicroseconds >= BuildBreakdownMinHeaviestSpawnMicroseconds` (15 ms,
matching the bimodal threshold the original #450 todo entry proposes). An
incidental cheap prewarm or watch-mode spawn on a frame whose hitch was
driven by something else (mainLoop, deferred events) will NOT consume the
latch — the only sample per session is reserved for the real single-spawn
regression we're trying to diagnose. `spawnMaxMicroseconds` (not aggregate)
is the right gate because this is the bimodal-single-spawn case.

### Reentry bucket covers the potential-scan too

`TrajectoryMath.HasReentryPotential` is `O(n)` in trajectory-point count
on non-orbital recordings. Bracketing the entire `if (HasReentryPotential)`
decision (classification + build or skip) in the reentry window ensures the
scan cost is attributed to the reentry bucket rather than leaking into
`other`, so Phase B branches on the correct dominant bucket.

### Steady-state overhead

Per spawn: 4 `Start`/`Stop` pairs + 8 `ElapsedTicks` reads = 12 Stopwatch
calls, plus a handful of local-field writes. Under Mono on Windows each
`ElapsedTicks` read is backed by `QueryPerformanceCounter` (~20-30 ns), so
the total is ≈ 400-800 ns per spawn — negligible vs the 28 ms spike we're
attributing and the same order of magnitude as the existing `spawnStopwatch`
overhead #414 already pays. Zero steady-state log output (one-shot latch,
independent from #414's).

## Files touched (Phase A)

- `Source/Parsek/GhostPlaybackEngine.cs` — 4 stopwatch fields, 6
  per-frame heaviest-spawn fields, 4 "last spawn" fields, reset hooks,
  4 instrumentation windows in `TryPopulateGhostVisuals`, heaviest-spawn
  latch in `BuildGhostVisualsWithMetrics.finally`, populate
  `PlaybackBudgetPhases` at the existing site.
- `Source/Parsek/Diagnostics/DiagnosticsStructs.cs` — new
  `HeaviestSpawnBuildType` byte enum, 11 new `PlaybackBudgetPhases` fields.
- `Source/Parsek/Diagnostics/DiagnosticsComputation.cs` — new
  `s_buildBreakdownOneShotFired` latch, new second WARN line, reset helper.
- `Source/Parsek.Tests/Bug450BuildBreakdownTests.cs` (new) — mirror
  `Bug414BudgetBreakdownTests` format assertions; independent one-shot
  latch test; sum+max-reconciliation invariant test.
- `docs/dev/todo-and-known-bugs.md` — note Phase A diagnostic shipped;
  Phase B branch (B1/B2/B3) gated on next playtest's breakdown output.
- `docs/dev/done/plan-450-build-breakdown.md` (this file, checked in per
  review finding #9).

No CHANGELOG entry: per `feedback_changelog_style` the CHANGELOG is
user-facing only; this is pure internal diagnostic plumbing, so the record
lives in `todo-and-known-bugs.md`.

## Phase B branch table (NOT part of this PR — gated on playtest data)

When the next budget-exceeded spike fires after Phase A is in a user's KSP
install, the new build-breakdown WARN will attribute the cost to one of
four buckets. The fix branch depends on the answer:

| Dominant sub-phase | Fix | Scope |
|---|---|---|
| `timeline` (part instantiation), heaviest-spawn < ~15 ms, aggregate > cap | **B1: per-frame build-time budget.** `MaxSpawnBuildMsPerFrame` const; new `GhostPlaybackLogic.ShouldThrottleSpawnByTime(frameSpawnTotalTicks, capTicks)`; `TryReserveSpawnSlot` ORs count-cap and time-cap. One monolithic >cap spawn still runs; prevents second spawn from stacking. | Small |
| `timeline`, single spawn alone > ~15 ms | **B2: coroutine split of `TryPopulateGhostVisuals`.** Yield between (1) snapshot resolve, (2) timeline build, (3) dict populate, (4) reentry FX. Hold `ghost.SetActive(false)` across yields (extends the existing `deferVisibilityUntilPlaybackSync` mechanism at `:2283`). `ghostStates[index]` populated only on final yield to keep the "fully-built or not there" invariant. | Medium-large |
| `reentryFx` | **B3: lazy reentry pre-warm.** Gate `TryBuildReentryFx` on first-frame-in-atmosphere + speed > floor, not on trajectory peak. | Small |
| `dictionaries` | Unlikely (pure data-structure walks). Profile with more targeted instrumentation before committing to a fix. | Unknown |

## Tests

Log-format assertions mirroring `Bug414BudgetBreakdownTests` (the proven
shape for this type of plumbing) — the diagnostic is a pure transformation
from `PlaybackBudgetPhases` values to a log line. Specific tests:

1. **Synthetic heaviest-spawn breakdown formats correctly** — populate all
   11 new fields with distinct values; assert each appears in the WARN
   with the expected prefix (`sum[snapshot=…`, `heaviestSpawn[type=…`).
2. **Build-type enum renders as human-readable string** — three positive
   cases (`RecordingStartSnapshot`, `VesselSnapshot`, `SphereFallback`) and
   one `None` case (no heaviest spawn captured — aggregate only).
3. **One-shot latch fires once per session, independently of #414** —
   set `s_playbackBreakdownOneShotFired = true` first; trip a budget
   exceeded frame; assert build-breakdown line still emits (new latch
   hasn't fired yet); trip a second spike; assert build-breakdown line
   does NOT re-emit.
4. **No build breakdown line when `spawnMicroseconds == 0`** — the
   aggregate spike happened but zero spawn time; build-breakdown line
   suppressed (no useful data to log).
5. **Sum+max reconciliation invariant** — given a synthetic heaviest spawn
   with `total=10ms snapshot=1ms timeline=6ms dicts=1ms reentry=1ms`,
   the formatted line shows `other=1.0ms` (= 10 − 1 − 6 − 1 − 1).
6. **Reset clears both latches** —
   `DiagnosticsComputation.ResetForTesting` clears the new latch plus the
   existing one.

Integration-style testing of the producer (the stopwatch instrumentation
in `TryPopulateGhostVisuals` actually populating the per-call fields)
deferred to manual verification in-game: Phase A's value is the log
evidence on a real KSP playtest, and the sub-phase accumulators are
straightforward Stopwatch reads with no branch logic to gate.

## Rollout / risk

- Zero behavior change on any playback path.
- Steady-state overhead: ~300-600 ns per spawn (4 × Stopwatch Start/Stop);
  on a healthy non-spike frame this is invisible against the existing
  `spawnStopwatch` overhead.
- Memory: `PlaybackBudgetPhases` grows by 10 longs + 1 byte ≈ 81 bytes.
  Struct is allocated once per `UpdatePlayback` tick (already the case
  from #414); no additional allocation pressure.
- Log output: zero new log lines in steady state. One new line on the
  first spike per session.

## Post-ship validation

On the next playtest, observe the one-shot "spawn build breakdown" WARN
adjacent to the existing #414 "playback budget breakdown" WARN in
`KSP.log`. The `heaviestSpawn[type=X total=Xms]` fields tell us which
fix branch to take in Phase B. Decision criteria recorded in the Phase B
branch table above.
