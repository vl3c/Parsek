> STATUS: Implemented in PR #926 (GhostPlaybackEngine hot-path decomposition). Archived as a historical reference. The Part 6 hot-path invariants still constrain any future edit to UpdatePlayback / RenderInRangeGhost.

# GhostPlaybackEngine Hot-Path Decomposition Plan

Read-only planning doc. NO `.cs` edits land with this commit. This plan covers the two
large per-frame hot-path methods in `Source/Parsek/GhostPlaybackEngine.cs`:

- `UpdatePlayback(...)` (the per-frame outer loop + diagnostics finalization)
- `RenderInRangeGhost(...)` (in-range spawn / position / visuals pipeline)

Both were flagged as too risky for opportunistic one-shot extraction: many
`continue`-terminated guard branches sharing mutable locals (`state`, `ghostActive`),
and allocation sensitivity (these run for every trajectory every frame, with many
ghosts active simultaneously).

The implementation that follows this plan is a separate, disciplined,
zero-logic-change pass governed by `docs/dev/refactor-guidelines.md` (the 13-item
behavior-preserving checklist). Every extraction below names which checklist items
make it safe, the allocation analysis, and the risk.

## Hard constraints carried from the guidelines

- Same call position, no reordering (item 2).
- No logic changes: no condition added / removed / reordered, no new branch (item 3).
- Control flow preserved: every `continue` / `break` / `return` keeps identical timing
  (item 4). In the per-frame loop this is the dominant hazard: a guard that does
  `if (cond) { log; continue; }` must keep firing the `continue` from inside the loop
  body, so a guard cluster can only be extracted as a **predicate** that the loop body
  still acts on (`if (ShouldSkipX(...)) { ...same body...; continue; }`), never as a
  method that itself owns the `continue`.
- Logging content + timing + level unchanged (item 9). Several guard branches emit a
  `GhostRenderTrace.EmitGuardSkip`, a `CountFrameSkip(reason)`, and sometimes a
  `ParsekLog.VerboseRateLimited`. The order and the exact strings stay put.
- No pre-existing access-modifier change (items 7, 13). If a candidate helper needs a
  `private` type, it stays `private` (scale back, never widen the pre-existing type).
- **ZERO new allocation in these per-frame paths**: no closures, no params objects, no
  LINQ, no `string.Format`/interpolation that wasn't already there, no boxing. New pure
  helpers take primitives / structs / interface refs by value and return a primitive or
  a `readonly struct`. `out` is used instead of returning a tuple where the parent needs
  more than one value back (a tuple of value types does not allocate, but `out` keeps
  the call shape closest to the inline original and is the project convention).

## Pre-condition: M2 is already merged on this base

This base (`origin/main`) already contains the M2 narrow-pass extractions:
`RelativeAnchorResolution.ShouldSkipPostPositionPipeline(...)` (called at 11 sites) and
the static `ResolveRenderSurface(...)` helper. Do NOT re-plan those. This plan anchors
on block/function semantics, not line numbers, because the surrounding line numbers will
keep shifting.

A large amount of pure logic is **already** carved out of these two methods into
`GhostPlaybackLogic`, `ChainHandoffLogic`, `RelativeAnchorResolution`, and
`PlaybackTrajectoryBoundsResolver` (`ShouldSuppressGhosts`, `ShouldSuppressVisualFx`,
`ShouldSuppressGhostMeshAtWarp`, `ShouldFireHiddenPastEndCompletion`, `DecideShadow`,
`DecideBridgeHold`, `ShouldLoopPlayback`, `HasRenderableGhostData`,
`ResolveGhostActivationStartUT`, `ShouldHoldInitialActivationHiddenThisFrame`,
`ShouldRestoreDeferredRuntimeFxState`, `ResolveVisiblePlaybackUT`, ...). This shrinks the
high-value testability surface that remains: most decision math is already a tested pure
helper. What is left inline is mostly *orchestration* (call this engine method, then
that one, mutate `state`, `continue`) which is exactly the part that resists
behavior-preserving extraction.

---

# Part 1 - `UpdatePlayback`

## 1.1 Structural map

`UpdatePlayback(IReadOnlyList<IPlaybackTrajectory> trajectories, TrajectoryPlaybackFlags[] flags, FrameContext ctx)`

### Phase A - Preamble (before the loop)
1. `ClearPrimaryGhostPositionedThisFrame()`.
2. Three early-return guards (null/empty trajectories, null positioner, flags array
   mismatch). Each sets `DiagnosticsState.playbackBudget = default` and returns; the
   third also warns.
3. Compute frame-wide flags: `suppressGhosts`, `suppressVisualFx`,
   `RebuildAutoLoopLaunchScheduleCache(...)`, `ResetPerFramePlaybackCounters(...)`.
4. Initialize loop-scoped accumulators: `spawnMicroseconds`, `ghostsProcessed`,
   `trajectoriesIterated`.
5. Build the optional engine-iteration trace `StringBuilder` (allocated **only** when
   `ghostRenderTracing == true`; steady-state cost zero).

### Phase B - Per-trajectory loop (`for i in [0, count)`)
Shared mutable locals that flow across blocks inside one iteration:
- `traj` (read-only after assignment), `f` (read-only struct copy).
- `state` (`GhostPlaybackState`, **mutated**: assigned from dictionary, nulled and
  rebuilt in loop-sync cycle-change, reassigned inside `RenderInRangeGhost` via `ref`).
- `ghostActive` (`bool`, **mutated** across blocks and via `ref` into
  `RenderInRangeGhost`).
- Per-iteration read-only derived booleans: `hasPointData`, `hasInterpolatedPoints`,
  `hasOrbitData`, `hasSurfaceData`, `inRange`, `pastEnd`, `pastEffectiveEnd`,
  `activationStartUT`, `chainNextIndex`, `continuationHasActiveGhost`.

Guard / dispatch branches in order (each terminates the iteration with `continue`,
except the in-range render which `continue`s only on `true`, and the past-end tail which
falls through):

| # | Block | Terminator | Mutates before terminator |
|---|-------|-----------|----------------------------|
| B0 | engine-iter trace append | none (side effect) | builder |
| B1 | `f.skipGhost` (destroy + maybe hidden-past-end completion) | `continue` | destroys ghost, may fire completion |
| B2 | `!HasRenderableGhostData` | `continue` | none |
| B3 | re-fly `sessionSuppressed` (unless companion debris) | `continue` | destroys ghost |
| B4 | read `state` / compute `ghostActive` / `ghostsProcessed++` | none | reads state |
| B5 | `f.anchorReFlyUnstable` | `continue` | deactivates ghost, resets appearance |
| B6 | `currentUT < activationStartUT` | `continue` | removes dedup sets, destroys ghost |
| B7 | compute `inRange` / `pastEnd` / `pastEffectiveEnd` | none | none |
| B8 | `ShouldLoopPlayback(traj)` -> loop dispatch | `continue` | calls `UpdateLoopingPlayback`, anchor-gate destroy |
| B9 | non-loop overlap-ghost cleanup | none | destroys overlap ghosts |
| B10 | loop-synced debris (parent loop clock) | `continue` | destroys ghost, rebuilds state, renders via parent clock |
| B11 | warp suppression | `continue` | deactivates ghost |
| B12 | chain-seam **shadow** (head hidden while continuation renders) | `continue` | deactivates ghost, clears bridge map |
| B13 | in-range render `if (RenderInRangeGhost(...)) continue;` | conditional `continue` | mutates `state`/`ghostActive` by `ref` |
| B14 | past-end completion (`HandlePastEndGhost`) | fall-through | fires completion |
| B15 | stale past-end cleanup + chain-seam **bridge-hold** | fall-through (loop end) | holds or destroys head |

### Phase C - Post-loop finalization (after the loop)
1. Emit the engine-iteration trace line (only when builder was created).
2. Frame batch summary (`BuildCurrentFrameCounters` -> `ShouldEmitFrameSummary` ->
   `VerboseRateLimited`).
3. Capture `elapsedTicksAtLoopEnd`.
4. Fire deferred created events; fire deferred completed events (two simple loops over
   `deferredCreatedEvents` / `deferredCompletedEvents`, each timed by a stopwatch).
5. Observability capture (timed).
6. Stop `updateStopwatch`; convert ticks->microseconds for total / spawn / destroy.
7. Populate `DiagnosticsState.playbackBudget.*` and append frame history.
8. Compute the `#414` / `#450` per-phase microsecond breakdown locals (a dozen
   tick->us divisions, several `if (x < 0) x = 0` floors).
9. Build the `PlaybackBudgetPhases phases = new PlaybackBudgetPhases { ... }` struct
   (it is a `struct`; the object-initializer is a stack value, **not** a heap alloc).
10. `DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown(...)`.

## 1.2 Decomposition proposal for `UpdatePlayback`

The loop body (Phase B) is the part the maintainer flagged, and it is genuinely the
hardest to carve safely because every block shares `state` / `ghostActive` and ends in a
`continue` that must fire from the loop body. The high-value, low-risk wins here are
**(a) the Phase C finalization tail** (no loop, no `continue`, no shared mutable ghost
state, pure arithmetic + struct population) and **(b) a small number of read-only
predicate / pure-computation extractions inside the loop**.

### U1 (lowest risk, do first) - Extract the budget-phase arithmetic into a pure builder

- **Helper:** `internal static PlaybackBudgetPhases BuildPlaybackBudgetPhases(...)`
- **What it covers:** Phase C step 8 + step 9 - the dozen tick->microsecond conversions,
  the three `if (x < 0) x = 0` floors, and the `new PlaybackBudgetPhases { ... }`
  initializer. It is currently a straight-line block with no branches other than the
  floors and no instance-state writes (it only reads stopwatch elapsed-tick values and
  per-frame counter fields, then returns the struct).
- **Signature (all by value, no `out`):**
  ```
  internal static PlaybackBudgetPhases BuildPlaybackBudgetPhases(
      long elapsedTicksAtLoopEnd,
      long spawnMicroseconds,
      long destroyMicroseconds,
      long deferredCreatedTicks, long deferredCompletedTicks, long observabilityTicks,
      int trajectoriesIterated, int overlapGhostIterationCount,
      int createdEventsFired, int completedEventsFired,
      int spawnsAttempted, int spawnsThrottled, long frameMaxSpawnTicks,
      long buildSnapshotResolveTicks, long buildTimelineTicks,
      long buildDictionariesTicks, long buildReentryFxTicks,
      long heaviestSnapshotResolveTicks, long heaviestTimelineTicks,
      long heaviestDictionariesTicks, long heaviestReentryTicks,
      long heaviestOtherTicks, HeaviestSpawnBuildType heaviestBuildType)
  ```
  The parent still does the four `*Stopwatch.ElapsedTicks` reads and passes ticks in;
  the helper does the `* 1000000L / Stopwatch.Frequency` divisions and floors. Reason for
  passing ticks (not pre-divided microseconds): the parent already pre-divides
  `spawnMicroseconds` / `destroyMicroseconds` before this point (they feed the
  `DiagnosticsState` writes), so those two come in as microseconds; everything else is
  internal to the breakdown and is passed as raw ticks so the division stays in one
  place. Match the existing pre-divided values exactly (`spawnMicroseconds`,
  `destroyMicroseconds` are reused, not recomputed).
- **Allocation:** zero. `PlaybackBudgetPhases` is a value struct; the `new { ... }`
  expression constructs it on the caller's stack slot via copy-return. No params object,
  no closure.
- **Checklist items that make it safe:** 2 (called from exact same position), 3 (no
  condition change; the floors are copied verbatim), 11 (primitives in, struct out - no
  field-to-param laundering beyond what the block already read locally), 12 (pure
  `static`, touches no instance field once the tick values are passed in).
- **Risk:** Low. The one trap is the `spawnMicroseconds` value: it is both written into
  `DiagnosticsState.playbackBudget.spawnMicroseconds` AND fed into the breakdown's
  `buildOtherMicroseconds = spawnMicroseconds - (sub-phases)`. The helper must receive
  the **same** already-divided `spawnMicroseconds` long, not recompute it from ticks, or
  rounding could differ by 1us. Pass the local through; do not re-read the stopwatch.
- **Testability win:** Yes. A `GhostPlaybackEngineExtractedTests` case can assert the
  floor behavior (negative `buildOtherMicroseconds` clamps to 0; negative
  `mainLoopMicroseconds` clamps to 0) and the tick->us conversion, with deterministic
  `Stopwatch.Frequency`-independent inputs by passing ticks chosen relative to
  `Stopwatch.Frequency`. This is the single best testability win in `UpdatePlayback`.

### U2 (low risk) - Extract the deferred-event firing tail into a private method

- **Helper:** `private void FireDeferredFrameEvents(out int createdEventsFired, out int completedEventsFired)`
- **What it covers:** Phase C step 4 - start/stop of `deferredCreatedStopwatch`, the loop
  firing `OnGhostCreated?.Invoke(...)`, the count capture, then the same for completed.
- **Signature:** `out int, out int` (parent needs both counts back for the budget struct).
  No `ref`. Reads `deferredCreatedEvents` / `deferredCompletedEvents` / the two
  stopwatches / the two events - all instance state, so the method is `private`
  (non-static) per item 12.
- **Allocation:** zero (two index `for` loops, no `foreach` enumerator boxing - keep the
  index-`for` form exactly; do NOT switch to `foreach`, which would change IL and could
  allocate an enumerator for some collection types).
- **Checklist items:** 2, 3, 4 (the loops fire the same events in the same order), 8 (NOT
  a loop-split - these are two already-separate loops, kept separate), 9 (event
  invocations are the existing side effects, unchanged), 12.
- **Risk:** Low-medium. The subtle point: the `out` counts are captured **before** the
  loops in the original (`int createdEventsFired = deferredCreatedEvents.Count;` then the
  loop). Preserve that ordering - capture count, then iterate - because nothing adds to
  the lists during firing, but matching the original read order is the safe default.
- **Testability:** Low (touches events + stopwatches; better verified by the existing
  budget/event integration tests than a new unit test). Extract for readability, not for
  a new test.

### U3 (low risk) - Extract the three Phase-A early-return guards' shared epilogue? NO.

Considered and rejected. The three guards each do
`DiagnosticsState.playbackBudget = default; return;` and the third also warns. They look
dedup-able but they are three **distinct** `return` sites with different conditions, and
collapsing them would either (a) change when the warn fires or (b) bury a `return` inside
a helper (forbidden by item 4 unless done as `if (TryX()) return;`, which buys nothing
here because there is no shared body to extract). Leave inline. See Do-NOT-touch list.

### U4 (medium risk, optional) - Extract the loop-synced-debris block (B10) as a whole

- **Helper:** `private bool TryUpdateLoopSyncedDebris(int i, IPlaybackTrajectory traj, TrajectoryPlaybackFlags f, FrameContext ctx, IReadOnlyList<IPlaybackTrajectory> trajectories, bool suppressGhosts, bool suppressVisualFx, ref GhostPlaybackState state, ref bool ghostActive)`
  returning `true` when the caller should `continue` (i.e. when B10's parent-loop branch
  was taken, including all its internal `continue` paths).
- **What it covers:** the entire `if (traj.LoopSyncParentIdx >= 0 && ... ) { if
  (ShouldLoopPlayback(parent)) { ... } }` block. The outer `if` body's only exit when the
  inner `ShouldLoopPlayback(parent)` is true is `continue` (every internal path either
  `continue`s or falls to the trailing `continue` at the block's end). When the parent is
  NOT looping, the block falls through to B11.
- **Shape:** `if (TryUpdateLoopSyncedDebris(...)) continue;` placed at B10's exact
  position. The helper returns `false` in exactly two cases that today fall through:
  (a) `LoopSyncParentIdx` out of range, (b) parent not looping. Returns `true` in every
  case where the original block reaches a `continue`.
- **Allocation:** The block contains `var syncCtx = ctx; syncCtx.currentUT = parentLoopUT;`
  - `FrameContext` is a struct, this is a stack copy, zero heap. Keep it as a local inside
  the helper. No closures.
- **Checklist items:** 2, 3, 4 (every `continue` becomes `return true`, the fall-throughs
  become `return false`; the caller's `if (...) continue;` reproduces the control flow
  exactly), 5 (the block is cohesive - it does not interleave with B11's mutations because
  B10 always either `continue`s or falls through having mutated nothing B11 reads beyond
  `state`/`ghostActive`, which are passed by `ref`), 11 (`ref state` / `ref ghostActive`
  because the cycle-change path nulls `state` and the inner render reassigns both; the
  caller does not actually read them after a fall-through, but passing by `ref` keeps the
  mutation contract identical to inline), 12 (`private`, reads `overlapGhosts`,
  `ghostStates`, `completedEventFired`, calls instance methods - must be non-static).
- **Risk:** Medium. This is the most aggressive `UpdatePlayback` extraction proposed. The
  hazard is the `out`-style multi-exit: B10 has 4 distinct `continue` points plus a
  trailing one, and a `parentLoopUT`/`parentCycle`/`parentPaused` triple from
  `TryComputeLoopPlaybackUT`. The implementer must map each original `continue` to a
  `return true` and each fall-through to `return false`, and must NOT let the
  `state = null` / `ghostActive = false` writes in the cycle-change path escape the helper
  in a way that differs from inline (they are `ref`, so they propagate identically - but
  the caller never reads them on the `true` path because it `continue`s, and on the
  `false` path B10 did not write them, so it is safe either way). **Recommend gating U4
  behind a dedicated review pass** (clean-context Opus reviewer per the guidelines)
  because it is the one block here where a misplaced `continue`/`return` mapping would
  silently change ghost lifecycle.
- **Testability:** Low directly (instance-heavy), but it isolates the parent-loop-clock
  dispatch into one named method, which makes the surrounding loop readable.

### Verdict for `UpdatePlayback`

Safe extractions found: **U1, U2, U4** (3). U3 rejected for cause.
- Top picks: **U1 `BuildPlaybackBudgetPhases`** (pure, the one real testability win) and
  **U2 `FireDeferredFrameEvents`** (clean tail, readability).
- U4 is worth doing but only with its own review gate.

**Honest verdict:** `UpdatePlayback` should be **only partially / conservatively
decomposed.** The Phase C finalization tail (U1 + U2) carves cleanly and is worth it. The
Phase B loop body should stay largely inline: it is 15 sequential guard/dispatch blocks
that all share `state` + `ghostActive` and each end in a `continue` that must fire from
the loop body. Carving more than U4 out of it would either (a) require predicate helpers
so thin they add a call without removing logic (the decisions are already in
`GhostPlaybackLogic`/`ChainHandoffLogic`), or (b) bury `continue`s inside helpers
(forbidden). A "full" decomposition of the loop body cannot be done behavior-preservingly
without turning each block into a `Try…(…, out bool shouldContinue)` shim, which is more
error-prone than the inline form and adds zero testability (the math is already extracted).
Stop at U1 + U2 (+ optionally U4 behind review).

---

# Part 2 - `RenderInRangeGhost`

## 2.1 Structural map

`private bool RenderInRangeGhost(int i, IPlaybackTrajectory traj, TrajectoryPlaybackFlags f, FrameContext ctx, bool suppressVisualFx, bool hasPointData, bool hasInterpolatedPoints, bool hasSurfaceData, bool hasOrbitData, bool allowEarlyDestroyedDebrisCompletion, ref GhostPlaybackState state, ref bool ghostActive)`

Returns `bool`: `true` => caller should `continue` (ghost processed / handled);
`false` => caller falls through to the past-end / cleanup tail.

Shared mutable locals across the method: `state` (`ref`, reassigned), `ghostActive`
(`ref`, reassigned), `usedBodyFixedPrimary` (`bool`, set by the positioning chain via
`out`), `visiblePlaybackUT`, `retired`, `zoneResult` (struct).

### Phase R0 - early-completion short-circuit
`if (allowEarlyDestroyedDebrisCompletion && earlyDestroyedDebrisCompleted.Contains(i)) return true;`

### Phase R1 - pre-spawn parent-anchored coverage retire
Resolve `initialCoveragePlaybackUT`, then
`if (TryHandleParentAnchoredDebrisCoverageRetired(...)) return true;` (sets `ghostActive`
via `out`).

### Phase R2 - flag events
`GhostPlaybackLogic.ApplyFlagEvents(state, traj, ctx.currentUT);` (runs whether or not
`state` exists - independent of ghost visuals).

### Phase R3 - first-spawn (when `state == null`)
1. dead-on-arrival suppression: `if (deadOnArrivalPastEnd && deadOnArrivalNotHeld) { count; log; return false; }`
2. spawn-slot throttle: `if (!TryReserveSpawnSlot(...)) return false;`
3. create pending state, store in `ghostStates[i]`, `EnsureGhostVisualsLoaded(...)`.
4. on `Failed`: count, remove, `ghostActive = false`, `return false`.
5. on `Pending`: `ghostActive = false`, `return true`.

### Phase R4 - `if (state == null) return false;` (defensive)

### Phase R5 - zone rendering
1. resolve `renderDistance`, `activeVesselDistance`, `CachePlaybackDistances(...)`.
2. `zoneResult = positioner.ApplyZoneRendering(...)`.
3. `if (zoneResult.hiddenByZone) { trace; ghostActive = HandleHiddenGhostVisualState(...); return true; }`

### Phase R6 - rehydrate visuals when not loaded
`if (!HasLoadedGhostVisuals(state)) { throttle; EnsureGhostVisualsLoaded(...); handle Failed/Pending; }`
(Failed -> `return false`; Pending -> `return true`.)

### Phase R7 - recompute `ghostActive`; `if (!ghostActive) return false;`

### Phase R8 - LOD fidelity + begin-frame trace + clear retire signal
`ApplyDistanceLodFidelity`, resolve `visiblePlaybackUT`, `BeginFrame`,
`state.anchorRetiredThisFrame = false`, `usedBodyFixedPrimary = false`.

### Phase R9 - positioning chain (the big nested `if`)
`if (!TryRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(...)) { ... }` containing
the `hasInterpolatedPoints` / `hasPointData` / `hasSurfaceData` / `hasOrbitData` dispatch,
each branch choosing orbit-tail vs relative-section vs interpolate/point/surface/orbit
positioning. Sets `usedBodyFixedPrimary` via `out`.

### Phase R10 - post-position pipeline
1. `retired = RelativeAnchorResolution.ShouldSkipPostPositionPipeline(state.anchorRetiredThisFrame);`
2. `GhostRenderTrace.EmitPostUpdate(... ResolveRenderSurface(usedBodyFixedPrimary, retired) ...)`.
3. `if (!retired) MarkPrimaryGhostPositionedThisFrame(state);`
4. `if (retired) { ApplyFrameVisuals(skipPartEvents:true, suppress:true, transient:false); }`
   `else { ... initial-activation-hidden decision + ApplyFrameVisuals + activation ... }`
   The `else` branch is itself a two-way split on `initialActivationHidden`.

### Phase R11 - playback trace (non-retired only)
`if (!retired) PlaybackTrace.MaybeEmitFrame(...);`

### Phase R12 - early-destroyed-debris completion (side-effect only)
`if (allowEarlyDestroyedDebrisCompletion && !retired) TryHandleEarlyDestroyedDebrisCompletion(...);`
`else if (allowEarlyDestroyedDebrisCompletion && retired) VerboseRateLimited(...);`

### Phase R13 - `return true;`

## 2.2 Decomposition proposal for `RenderInRangeGhost`

`RenderInRangeGhost` is already heavily delegated: most of its phases are single calls to
already-extracted helpers (`TryHandleParentAnchoredDebrisCoverageRetired`,
`ApplyZoneRendering`, `EnsureGhostVisualsLoaded`, `ApplyFrameVisuals`,
`ShouldHoldInitialActivationHiddenThisFrame`, `RelativeAnchorResolution.*`,
`ResolveRenderSurface`). The two remaining cohesive multi-statement blocks worth carving
are R3 (first-spawn) and the R10 non-retired `else` branch (the
initial-activation-hidden vs activate split).

### R-A (low risk, do first) - Extract the dead-on-arrival predicate (pure)

- **Helper:** `internal static bool IsSpawnSuppressedDeadOnArrival(double currentUT, double endUT, double chainEndUT, bool ghostHeld)`
- **What it covers:** the boolean computation inside R3 step 1:
  `bool deadOnArrivalPastEnd = currentUT > endUT || currentUT > chainEndUT;` and
  `bool deadOnArrivalNotHeld = !ghostHeld;` collapsed into the single decision
  `deadOnArrivalPastEnd && deadOnArrivalNotHeld`. The call site keeps the `count + log +
  return false` body inline (item 4: do not bury the `return`).
- **Call shape:** `if (IsSpawnSuppressedDeadOnArrival(ctx.currentUT, traj.EndUT, f.chainEndUT, IsGhostHeld != null && IsGhostHeld(i))) { ...count/log/return false... }`
  The `IsGhostHeld == null || !IsGhostHeld(i)` short-circuit must be evaluated **at the
  call site** and passed in as the already-resolved `ghostHeld` bool, NOT re-derived in
  the helper (the helper has no access to the `IsGhostHeld` delegate, and we will not pass
  a delegate - that would be a closure-ish param the project avoids). Compute
  `bool ghostHeld = IsGhostHeld != null && IsGhostHeld(i);` at the site and pass
  `ghostHeld`. Watch the De Morgan (item 1): original is
  `deadOnArrivalNotHeld = IsGhostHeld == null || !IsGhostHeld(i)`, i.e.
  "not held". The helper's last param is named `ghostHeld`, so the site passes
  `IsGhostHeld != null && IsGhostHeld(i)` and the helper does
  `... && !ghostHeld`. Verify this inversion carefully in review.
- **Allocation:** zero (4 primitives in, bool out).
- **Checklist items:** 1 (De Morgan on the held inversion - flagged above), 2, 3, 11
  (primitives, no delegate param), 12 (`static`, no instance state).
- **Risk:** Low, with one explicit De Morgan trap called out above.
- **Testability:** Yes. Truth-table test across `currentUT` vs `endUT`/`chainEndUT` and
  the held flag. Good unit-test win.

### R-B (low risk) - Extract the first-spawn block (R3) as a private method

- **Helper:** `private FirstSpawnOutcome TryFirstSpawn(int i, IPlaybackTrajectory traj, TrajectoryPlaybackFlags f, FrameContext ctx, ref GhostPlaybackState state, ref bool ghostActive)`
  where `FirstSpawnOutcome` is a `private enum { ContinueOuter, ReturnTrue, ReturnFalse, Proceed }`.
- **What it covers:** all of R3 (the `if (state == null) { ... }` block): dead-on-arrival
  (using R-A), spawn-slot throttle, create pending state + store, `EnsureGhostVisualsLoaded`,
  Failed/Pending handling.
- **Call shape:**
  ```
  if (state == null)
  {
      switch (TryFirstSpawn(i, traj, f, ctx, ref state, ref ghostActive))
      {
          case FirstSpawnOutcome.ReturnTrue:  return true;
          case FirstSpawnOutcome.ReturnFalse: return false;
          // Proceed: fall through to R4
      }
  }
  ```
  This keeps every `return` at the **caller's** statement level (item 4: the helper
  returns an outcome enum; the caller owns the `return`). `state` and `ghostActive` are
  `ref` because the helper assigns `state = CreatePendingSpawnState(...)`, writes
  `ghostStates[i] = state`, and sets `ghostActive = false` on Failed/Pending.
- **Access-modifier note (item 13):** `FirstSpawnOutcome` is a NEW `private enum`, so the
  helper that consumes it must be `private` (not `internal`) per item 13 - a `private`
  param/return type forces the method `private`. That is acceptable: `TryFirstSpawn`
  touches `ghostStates`, `IsGhostHeld`, `TryReserveSpawnSlot`, `CreatePendingSpawnState`,
  `EnsureGhostVisualsLoaded` (all instance), so it must be `private` anyway.
- **Allocation:** zero. The enum return is a value type; no params object; the existing
  `VerboseRateLimited` string concat inside the dead-on-arrival branch is the SAME string
  building that already exists (do not "improve" it). The `state` rebuild allocates a
  `GhostPlaybackState` exactly as the inline code does today - that is pre-existing
  allocation, not new.
- **Checklist items:** 2, 3, 4 (outcome enum keeps `return`s at caller), 5 (R3 is
  cohesive; on `Proceed` the only mutation that escapes is `state`/`ghostActive` by `ref`,
  which is exactly what R4+ read next - identical to inline), 9 (logs unchanged), 11
  (`ref` justified, no delegate param), 12/13 (`private` forced by the `private enum`).
- **Risk:** Medium. Two traps: (1) the `Pending` path returns `true` with
  `ghostActive = false`, while `Failed` returns `false` with `ghostActive = false` and a
  `ghostStates.Remove(i)` - the enum mapping must preserve which one removes from the
  dictionary; (2) on the success path the original FALLS THROUGH to R4 (`if (state ==
  null) return false;`) - the helper must return `Proceed` (not touch the dictionary
  beyond the store) so R4 still runs. Recommend a review gate on R-B.
- **Testability:** Low (instance + Unity-ish via `CreatePendingSpawnState`). Extract for
  readability; rely on existing `Bug414SpawnThrottleTests` / `HeldGhostSpawnTests` for
  behavioral coverage rather than a new unit test.

### R-C (low risk, do first alongside R-A) - Extract the orbit-tail-vs-relative decision booleans (pure)

- **Helper:** none new needed at the top level, but the two booleans at the head of R9
  are pure and worth a named pure helper IF they are not already one:
  `bool authoredGapHasShadow = AuthoredFrameGapHasShadowCoverage(traj, visiblePlaybackUT);`
  and `bool orbitTailPlayback = !authoredGapHasShadow && ShouldUseOrbitTailPlayback(traj, visiblePlaybackUT);`
  Both `AuthoredFrameGapHasShadowCoverage` and `ShouldUseOrbitTailPlayback` are ALREADY
  extracted helpers. The composite `orbitTailPlayback` is a one-liner that is read in two
  sibling branches (`hasInterpolatedPoints` and `hasPointData`).
- **Recommendation:** Do **not** extract a `ResolveOrbitTailPlayback(...)` wrapper. It
  would save nothing (the inputs are already computed once and the expression is one
  `&&`), and folding it into a helper risks moving the `!authoredGapHasShadow`
  short-circuit. **Leave inline.** Listed here so the implementer does not "discover" it
  as a candidate and carve a pointless wrapper.

### R-D (medium risk, optional, review-gated) - Extract the non-retired post-position branch (R10 `else`)

- **Helper:** `private void ApplyNonRetiredPostPosition(int i, IPlaybackTrajectory traj, FrameContext ctx, GhostPlaybackState state, double visiblePlaybackUT, bool suppressVisualFx, ZoneRenderResult zoneResult, ref bool ghostActive)`
  (param type `ZoneRenderResult` must match `positioner.ApplyZoneRendering`'s return type;
  if that type is `private`/nested, the helper must be `private` too - it already is).
- **What it covers:** the `else` arm of `if (retired) { ... } else { ... }` in R10 -
  computing `effectiveSkipPartEvents` / `effectiveSuppressVisualFx`, the
  `ShouldHoldInitialActivationHiddenThisFrame` call, the `EmitActivationDecision` trace,
  and the two-way split (`initialActivationHidden` -> hide + suppressed `ApplyFrameVisuals`
  + the long `VerboseRateLimited`; else -> `ActivateGhostVisualsIfNeeded` +
  `ApplyFrameVisuals` + `RestoreDeferredRuntimeFxState` + `TrackGhostAppearance`).
- **Allocation:** zero new - the long `VerboseRateLimited` message concat already exists;
  pass `zoneResult` by value (struct) so no boxing. `ghostActive` by `ref` (the hidden
  path sets `ghostActive = false`).
- **Checklist items:** 2, 3, 4 (no `return`/`continue` inside - it is straight-line with
  an inner `if/else`, both arms fall out the bottom), 5 (cohesive; the `ghostActive`
  write is the only escape, by `ref`), 9 (the activation-decision trace + the long
  verbose line are byte-for-byte preserved), 11, 12.
- **Risk:** Medium. It is a faithful block move with no control-flow exits, which is the
  *easiest* shape to preserve, BUT it reads several locals (`zoneResult`,
  `visiblePlaybackUT`, `suppressVisualFx`) and the `state?.ghost` null-checks must keep
  their exact short-circuit form (item 1). The `Vector3 ghostPosition = hasGhostTransform
  ? state.ghost.transform.position : Vector3.zero;` and the `EmitActivationDecision`
  argument list must be copied verbatim. Recommend review gate.
- **Testability:** Low (Unity transforms, FX). Readability extraction only.

### R-E (rejected) - Do NOT extract the R9 positioning dispatch

The nested `if (!TryRetire...) { if (hasInterpolatedPoints) {...} else if (hasPointData)
{...} ... }` looks extractable but it (a) writes `usedBodyFixedPrimary` via `out` that
R10 reads, (b) calls four different `positioner.*` methods plus
`TryPositionRelativeSectionAtPlaybackUT`, and (c) the branch ordering
(`hasInterpolatedPoints` before `hasPointData`) is load-bearing. Extracting it gains no
testability (it is pure dispatch into already-tested positioners) and risks reordering the
data-shape branches (item 3) or mis-threading `usedBodyFixedPrimary` (item 11). Leave
inline. See Do-NOT-touch list.

### Verdict for `RenderInRangeGhost`

Safe extractions found: **R-A, R-B, R-D** (3). R-C and R-E rejected for cause.
- Top picks: **R-A `IsSpawnSuppressedDeadOnArrival`** (pure, testable, De-Morgan flagged)
  and **R-B `TryFirstSpawn`** (the biggest cohesive block, readability + isolates the
  spawn lifecycle).
- R-D is a clean straight-line block move worth doing behind a review gate.

**Honest verdict:** `RenderInRangeGhost` can be **moderately decomposed** - more usefully
than `UpdatePlayback`'s loop body, because R3 (first-spawn) and R10-`else` are cohesive
blocks rather than a chain of `continue`-guards. But it should still be a *conservative*
carve: R-A (pure predicate) is the only new testability win; R-B and R-D are readability
moves that each need their own review pass. The positioning dispatch (R9) and the
data-shape branch ordering must stay inline. Do not attempt a "full" carve into one helper
per phase - phases R1, R2, R5, R6, R7, R8 are already single delegated calls, so wrapping
them buys nothing and multiplies `ref`-threading risk.

---

# Part 3 - Explicit ordering (lowest-risk-first, one commit each)

Each step is independently compilable, the suite (`cd Source/Parsek.Tests && dotnet test`)
must pass between steps, and each is a separate commit. Steps marked **(review gate)**
should get a clean-context Opus reviewer per `docs/dev/refactor-guidelines.md` before the
next step.

1. **U1** - `BuildPlaybackBudgetPhases` (pure; add `GhostPlaybackEngineExtractedTests`
   cases). Lowest risk, highest testability. No `ref`, no control flow.
2. **R-A** - `IsSpawnSuppressedDeadOnArrival` (pure; add tests). Watch the De Morgan.
3. **U2** - `FireDeferredFrameEvents` (private, `out` counts, two index-loops). Readability.
4. **R-D** - `ApplyNonRetiredPostPosition` (private block move, no control-flow exits).
   **(review gate)** - straight-line but reads many locals.
5. **R-B** - `TryFirstSpawn` + `private enum FirstSpawnOutcome`. **(review gate)** -
   multi-exit mapping + dictionary side effects.
6. **U4** - `TryUpdateLoopSyncedDebris`. **(review gate)** - the most aggressive carve;
   4+ `continue` sites to map to `return true`/`false`. Do this LAST so the easier wins
   are banked first and the suite is green before the riskiest change.

If review on any of steps 4-6 surfaces a behavior-preservation doubt, scale that step
back (drop the extraction) rather than weakening the guideline - a `private` method that
ships untested is fine; a behavior change is not.

---

# Part 4 - Do-NOT-touch list (stays inline)

These blocks must remain inline because extracting them would change control flow, bury a
`return`/`continue`, or let block N's mutation alter block N+1 (checklist item 5).

1. **`UpdatePlayback` Phase-A early-return guards (B-pre).** Three distinct `return`
   sites with different conditions; the third also warns. No shared body to extract;
   collapsing them risks moving the warn or burying a `return`. (Item 4.)
2. **`UpdatePlayback` Phase-B guard chain B1, B3, B5, B6, B8, B11, B12, B15.** Each is a
   guard that ends in a `continue` that MUST fire from the loop body. They share `state`
   and `ghostActive`. The decision math each one uses is already a pure helper
   (`ShouldSuppressGhostMeshAtWarp`, `DecideShadow`, `DecideBridgeHold`,
   `ShouldFireHiddenPastEndCompletion`, ...). Wrapping the *bodies* would bury the
   `continue`; wrapping the *conditions* buys nothing because they are already extracted.
   Leave inline. (Items 3, 4, 5.)
3. **`UpdatePlayback` B13 in-range render call.** It is already a single
   `if (RenderInRangeGhost(...)) continue;`. Nothing to extract.
4. **`UpdatePlayback` B14 + B15 boundary.** `HandlePastEndGhost` (B14) is already a
   helper. B15's stale-cleanup + bridge-hold mutates `chainBridgeOpenedUT` and either
   holds (logs, traces, counts) or destroys. Grouping B14 and B15 would let B14's
   completion side effects reorder against B15's bridge decision. Keep separate. (Item 5.)
5. **`RenderInRangeGhost` R9 positioning dispatch.** Branch ordering
   (`hasInterpolatedPoints` before `hasPointData` before `hasSurfaceData` before
   `hasOrbitData`) is load-bearing; `usedBodyFixedPrimary` is threaded out and read by
   R10. Extraction risks reordering data-shape branches or mis-threading the `out`.
   (Items 3, 11.)
6. **`RenderInRangeGhost` R0/R1/R2/R4/R5/R6/R7/R8 single-call phases.** Already delegated
   to extracted helpers or trivial one-liners; wrapping multiplies `ref`-threading risk
   for zero gain.
7. **`RenderInRangeGhost` R10 `retired` (true) arm.** It is a single `ApplyFrameVisuals`
   call with fixed suppress flags. Do not pair it with the `else` arm into one method -
   the two arms have different `ApplyFrameVisuals` argument sets and the `if (retired)`
   branch must stay the decision point (the `else` is R-D, extracted alone).
8. **`TryHandleEarlyDestroyedDebrisCompletion`, `HandlePastEndGhost`,
   `UpdateLoopingPlayback`, `TryComputeLoopPlaybackUT`** - already separate methods; out
   of scope for this plan (not the two flagged methods).

---

# Part 5 - New tests enabled

Add a new file `Source/Parsek.Tests/GhostPlaybackEngineExtractedTests.cs` following the
existing `*ExtractedTests.cs` pattern (`[Collection("Sequential")]`, `IDisposable`,
`ParsekLog.TestSinkForTesting` capture, `RecordingStore.SuppressLogging = true`,
`ParsekLog.ResetTestOverrides()` in `Dispose`). There is no `GhostPlaybackEngineExtracted`
file yet (`GhostPlaybackEngineTests.cs` exists and is the sibling to reference for the
constructor/teardown shape).

Pure helpers that gain unit tests:

1. **`BuildPlaybackBudgetPhases` (U1).** Tests:
   - `NegativeBuildOther_ClampsToZero` - sub-phase ticks summing above `spawnMicroseconds`
     floor `buildOtherMicroseconds` to 0.
   - `NegativeMainLoop_ClampsToZero` - spawn+destroy exceeding loop-end elapsed floors
     `mainLoopMicroseconds` to 0.
   - `TicksConvertToMicroseconds` - a known tick count converts via
     `* 1000000L / Stopwatch.Frequency` (compute the expected with the same formula so the
     test is frequency-independent).
   - `PassThroughFields` - `trajectoriesIterated`, `createdEventsFired`,
     `heaviestBuildType`, etc. land in the struct unchanged.

2. **`IsSpawnSuppressedDeadOnArrival` (R-A).** Tests (truth table):
   - past `endUT` + not held => true.
   - past `chainEndUT` only + not held => true.
   - past end + held => false.
   - before both ends + not held => false.
   - boundary `currentUT == endUT` (strict `>` in original) => false.

`FireDeferredFrameEvents` (U2), `TryFirstSpawn` (R-B), `ApplyNonRetiredPostPosition`
(R-D), and `TryUpdateLoopSyncedDebris` (U4) are instance/Unity-heavy and are NOT given new
unit tests; they are covered by existing integration suites
(`Bug414BudgetBreakdownTests`, `Bug460MainLoopBreakdownTests`, `Bug414SpawnThrottleTests`,
`HeldGhostSpawnTests`, `Bug406GhostReuseLoopCycleTests`, `AutoLoopTests`). If a behavioral
question arises during their review, prefer an in-game test in
`InGameTests/RuntimeTests.cs` over a Unity-xUnit harness (per the project's Unity-runtime
test-coverage rule).

---

# Part 6 - Allocation + S16-style invariant notes for the implementer

## Allocation hazards (must stay zero-new-alloc)

- **No `foreach` substitutions.** U2's two deferred-event loops are index `for` loops
  today; keep them as index loops. Switching to `foreach` can allocate an enumerator for
  some collection types and changes IL.
- **No params arrays / no closures.** R-A and R-B pass primitives and `ref`s; do NOT pass
  the `IsGhostHeld` delegate into a helper (resolve it to a `bool` at the call site).
- **Structs stay structs.** `PlaybackBudgetPhases`, `FrameContext`, `ZoneRenderResult`,
  `TrajectoryPlaybackFlags`, and the proposed `FirstSpawnOutcome` enum are all value
  types. Passing them by value is a stack copy, not a heap allocation. Do not "optimize"
  any of them into a class.
- **The engine-iteration trace `StringBuilder`** is allocated only when
  `ghostRenderTracing == true`. None of the proposed extractions touch that gate; keep it.
- **Existing string concats** (the `VerboseRateLimited` messages in R3, R10, B12, B15)
  are pre-existing allocations gated by verbose/rate-limit. Extraction must move them
  verbatim, not duplicate or eagerly evaluate them outside their existing guards.
- **`new PlaybackBudgetPhases { ... }` is NOT a heap alloc** (struct initializer). The
  U1 helper returning it by value is allocation-free.

## Ordering invariants the implementer must preserve

- **B10 (loop-synced debris) must stay AFTER B8 (loop dispatch) and B9 (overlap cleanup)
  and BEFORE B11 (warp suppression).** The parent-loop-clock path depends on B9 having
  removed overlap ghosts for non-looping slots. If U4 is extracted, the
  `if (TryUpdateLoopSyncedDebris(...)) continue;` call must sit in exactly B10's slot.
- **R2 `ApplyFlagEvents` runs before R3 first-spawn and is independent of `state`.** Flags
  are permanent world entities applied whether or not the ghost mesh exists. R-B's
  `TryFirstSpawn` extraction must NOT pull `ApplyFlagEvents` inside it - flag application
  precedes and is independent of the spawn decision.
- **R1 pre-spawn coverage retire runs before R2 flag events** ("intentionally precedes
  permanent flag-event playback" per the inline comment). Preserve that order; do not let
  any extraction reorder R1 and R2.
- **R8 clears `state.anchorRetiredThisFrame = false` before R9 positioning,** and R9's
  relative positioner may set it back to `true`; R10 reads it via
  `ShouldSkipPostPositionPipeline`. Any extraction around R9/R10 must keep this
  clear-then-maybe-set-then-read ordering intact (this is the #613 fix invariant).
- **R10 `retired` is computed once and reused** by the post-update trace, the
  mark-positioned guard, the visuals branch, R11's `PlaybackTrace.MaybeEmitFrame` gate,
  and R12's early-completion gate. R-D extracts only the `else` arm; `retired` itself
  stays computed in the parent and is passed in / read by the parent's surrounding
  `if (retired)` so R11/R12 still see the same value.
- **U1 must reuse the already-divided `spawnMicroseconds` / `destroyMicroseconds`** longs
  rather than recomputing from ticks, so the breakdown's `buildOtherMicroseconds`
  subtraction matches `DiagnosticsState.playbackBudget.spawnMicroseconds` to the
  microsecond (avoids a 1us rounding drift between the budget total and the phase sum -
  the exact invariant bug #414 established).

## S16-style note

None of these extractions touch recording/serialization schema, the optimizer split
predicate, the on-rails BG TrackSection invariant, or the parent-anchored debris
coverage contract. They are pure structural reshuffles of in-memory per-frame
orchestration. The only contract-adjacent code touched is the #613 relative-anchor
retire ordering (R8->R9->R10), flagged above; preserve it exactly.
