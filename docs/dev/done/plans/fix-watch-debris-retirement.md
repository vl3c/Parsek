# Fix Plan: Watch-Mode Parent-Anchored Debris Retirement

Date: 2026-05-09

Worktree: `C:\Users\vlad3\Documents\Code\Parsek\Parsek-fix-watch-debris-retirement`

Branch: `fix-watch-debris-retirement`

Base: `06f676a1 Merge PR #777 fix-debris-hide-parent-range for testing`

Evidence bundle:
`C:\Users\vlad3\Documents\Code\Parsek\logs\2026-05-09_0003_watch-mode-debris-crazy-776-777`

## Problem Statement

The 776/777 combined build correctly captured parent-relative debris seeds, then
hid parent-anchored debris when playback left recorded relative coverage. The
remaining bug is lifetime ordering during watch mode:

1. `GhostPlaybackEngine.RenderInRangeGhost` applies zone rendering before it
   checks whether parent-anchored debris still has a covering Relative section.
2. `ParsekFlight.ApplyZoneRenderingImpl` treats child debris of the watched
   recording as watch-protected and can force full fidelity for that ghost even
   when it is far beyond the normal visual range.
3. Watch mode also has a direct visual-load/sync path:
   `EnsureGhostVisualsLoadedForWatch` can rebuild visuals and call
   `SynchronizeLoadedGhostForWatch` without passing through
   `RenderInRangeGhost`.
4. Loop and overlap playback have their own first-spawn and rehydrate paths
   before their zone calls, so "gate before zone" is insufficient unless the
   gate also runs before `CreatePendingSpawnState` and `EnsureGhostVisualsLoaded`.
5. The engine has activation sites outside `ParsekFlight.ShouldAutoActivateGhost`;
   `ActivateGhostVisualsIfNeeded` can still call `SetActive(true)` after a
   retired state was hidden.
6. Only after the current zone/watch path runs does the engine discover `sec=-1`,
   call `TryRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage`, hide the
   ghost, and set the frame-local `state.anchorRetiredThisFrame` flag.
7. The next frame clears `anchorRetiredThisFrame` before positioning, so the
   same ghost can re-enter the zone/watch path again. This produces repeated
   hide/restore/hide processing against a stale transform.

The result visible in the log is debris that appears to jump or go unstable
around the coverage boundary during watch mode. The log evidence does not point
to NaN/Infinity positions or bad PR #776 part-origin seeds. The strongest
failure signature is PR #777's coverage retirement firing after watch-protected
zone handling.

## Concrete Evidence

From the collected `KSP.log`:

- Around `23:56:14`, ghost `#3` is already outside section coverage:
  `FrameStart ... sec=-1 secUT=[NaN,NaN] ref=none`, followed by
  `reason=resolver-miss-or-retired retired=true active=false`.
- Around `23:56:18`, ghost `#4` transitions to `Beyond` with a render distance
  around `828964m`, then logs the watch-protected full-fidelity exemption:
  `beyond visual range ... but watch-protected debris`.
- The same ghost then logs:
  `recorded-relative-retired: reason=parent-anchored-debris-outside-relative-coverage`
  from `GhostPlaybackEngine.RenderInRangeGhost`.
- The stale pose in the post-update trace is near the surface, not an invalid
  floating-point value. The bad user-visible behavior is therefore ordering and
  reactivation around a retired ghost, not corrupted coordinates.

Relevant code paths in this base:

- `Source/Parsek/GhostPlaybackEngine.cs`
  - `RenderInRangeGhost`: `ApplyFlagEvents` currently runs at line 962 before
    any planned first-spawn coverage gate.
  - `RenderInRangeGhost`: zone rendering runs at lines 1030-1047 before the
    parent-relative coverage retirement check at lines 1095-1100.
  - `UpdateLoopingPlayback`: loop first-spawn begins around lines 1628-1644,
    before loop zone rendering and before `PositionLoopAtPlaybackUT`.
  - `UpdateOverlapPlayback`: overlap primary first-spawn begins around lines
    1850-1857 before overlap zone rendering.
  - `TryRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage`: lines
    2507-2542 hide the ghost and log the retirement.
  - `EnsureGhostVisualsLoadedForWatch`: lines 3406-3425 can load/rebuild visuals
    and synchronize watch state directly.
  - `ReusePrimaryGhostAcrossCycle`: lines 3676-3706 primes the reused ghost and
    can emit `RetargetToNewGhost` after the prime.
  - `ActivateGhostVisualsIfNeeded`: lines 4604-4615 unconditionally reactivates
    loaded ghosts outside the positioner contract.
  - `HideGhostForRetire`: lines 2545-2548 only calls `SetActive(false)`.
  - `HandlePastEndGhost`: lines 1297-1337 already has special completion logic
    for deterministic parent-anchored debris endpoint coverage misses.
- `Source/Parsek/ParsekFlight.cs`
  - `ApplyZoneRenderingImpl`: lines 15864-15888 can restore full fidelity and
    `SetActive(true)` for watch-protected debris before the engine later retires
    that ghost.
  - `ShouldAutoActivateGhost`: lines 15976-15981 only respects deferred
    playback-sync visibility, not deterministic coverage retirement.

## Target Behavior

Parent-anchored debris whose playback UT is outside recorded Relative coverage
must be treated as unavailable before any visual spawn, zone rendering,
watch-protected LOD override, watch-mode direct visual load, camera targeting,
loop retarget, flag/part-event playback, or FX activation can run.

The fix must preserve these existing behaviors:

- PR #776's part-origin seed handling remains unchanged.
- Legacy debris without `DebrisParentRecordingId` keeps the older playback path.
- Non-debris Relative anchor misses remain frame-local because recorded anchors
  can become resolvable again through fallback paths.
- Parent-anchored debris may play again if playback time moves back into a
  valid Relative section, such as via rewind or a loop clock returning inside
  coverage.
- Past-end completion for parent-anchored debris still fires through
  `HandlePastEndGhost` so policy subscribers can clean up, spawn, or transfer
  camera state as before.
- If the user is directly watching a debris ghost when it coverage-retires, the
  camera must exit watch mode via the existing `CameraActionType.ExitWatch`
  path instead of continuing to follow an inactive pivot. Debris that is merely
  watch-protected because its parent is watched should retire silently without
  exiting the parent's watch mode.

## Proposed Code Changes

### 1. Add an early coverage gate before visual spawn and zone rendering

In `GhostPlaybackEngine.RenderInRangeGhost`, compute the playback UT used for
visible positioning before flag events and before the first-spawn block:

- Use `ResolveVisiblePlaybackUT(traj, state, ctx.currentUT)`.
- Call the existing
  `ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(traj, visiblePlaybackUT)`
  predicate before `GhostPlaybackLogic.ApplyFlagEvents`, `TryReserveSpawnSlot`,
  `CreatePendingSpawnState`, `EnsureGhostVisualsLoaded`, and
  `positioner.ApplyZoneRendering`.
- If the predicate is true:
  - Do not apply flag events from this debris recording. If implementation
    proves parent-anchored debris cannot carry permanent flag events, document
    that invariant in code and keep this branch explicit.
  - Do not spawn or load visuals when `state == null`.
  - If a state/ghost already exists, hide it and run FX teardown without
    transient effects.
  - Do not call `ApplyZoneRenderingImpl`.
  - Do not call any positioner method.
  - Emit one rate-limited log and a `GhostRenderTrace` guard skip that includes
    recording index, recording id, vessel name, playback UT, and callsite.
  - Assign `ghostActive = false` before returning.
  - Return `true` from `RenderInRangeGhost` so the caller treats the frame as
    handled and does not fall through into stale past-end behavior while still
    in range.

This is the main fix. It removes the ordering bug by making coverage retirement
an input to rendering, not a side effect after zone/watch has already touched
the ghost.

The existing `state.anchorRetiredThisFrame = false` reset in
`RenderInRangeGhost` can stay where it is for the normal covered path. The new
early uncovered path returns before reaching that reset or the post-position
block, and the centralized helper owns FX teardown on the retired frame. On a
later covered frame, execution reaches the existing reset before positioning,
preserving the current frame-local anchor-retirement contract.

### 2. Make coverage retirement explicit in runtime state

Add a runtime-only state flag to `GhostPlaybackState`, for example:

```csharp
public bool parentAnchoredDebrisCoverageRetired;
```

Use this flag for defensive visibility gating, not as the source of truth. The
source of truth remains the deterministic predicate over `(traj, playbackUT)`.

Rules:

- Set it when the early coverage gate suppresses a parent-anchored debris ghost.
- Clear it when the same ghost is evaluated at a playback UT that is covered by
  a Relative section again.
- Do not clear it in generic visual lifecycle reset paths such as
  `GhostPlaybackState.ClearLoadedVisualReferences`; those paths do not have the
  trajectory/UT context needed to know whether coverage has resumed.
- Clear it only inside the deterministic coverage helper after evaluating a
  playback UT that is covered by a Relative section.
- Continue setting `anchorRetiredThisFrame = true` on the frame where teardown
  is needed, so existing post-position/FX suppression remains compatible.

This protects against remaining paths that might still call a visibility helper
in the same frame.

### 3. Teach all activation helpers about terminal coverage retirement

Update `ParsekFlight.ShouldAutoActivateGhost` so it returns `false` when
`state.parentAnchoredDebrisCoverageRetired` is true.

Update `GhostPlaybackEngine.ActivateGhostVisualsIfNeeded` with the same guard
before it calls `state.ghost.SetActive(true)`.

This is a defensive belt around these existing activation sites:

- Watch-protected full-fidelity restore inside `ApplyZoneRenderingImpl`.
- Normal `zone-show` reactivation.
- Positioner methods that receive `allowActivation: ShouldAutoActivateGhost(state)`.
- Engine-owned activation after normal playback, loop playback, overlap
  playback, loop-pause, and watch synchronization.

The early engine gate should prevent the zone path from being reached for this
case, but the activation helper should still encode the invariant: coverage
retired debris must not be re-shown.

### 4. Centralize the retire handling helper

Replace the current "hide only" helper with one helper that owns all side
effects for coverage retirement:

```csharp
private bool TryHandleParentAnchoredDebrisCoverageRetired(
    int index,
    IPlaybackTrajectory traj,
    GhostPlaybackState state,
    double playbackUT,
    double currentUT,
    double warpRate,
    string callsite,
    out bool ghostActive)
```

Responsibilities:

- Evaluate the existing coverage predicate.
- Clear `parentAnchoredDebrisCoverageRetired` and return `false` when covered.
- Mark `parentAnchoredDebrisCoverageRetired = true` and
  `anchorRetiredThisFrame = true` when uncovered.
- Run before any path that can load, position, activate, retarget, or emit
  events for parent-anchored debris.
- Hide the ghost if present.
- If loaded visuals exist, call `ApplyFrameVisuals` with:
  - `skipPartEvents: true`
  - `suppressVisualFx: true`
  - `allowTransientEffects: false`
- Reset appearance tracking so a later valid reappearance is logged cleanly.
- Set `ghostActive = false`.
- Log a rate-limited `[Parsek][WARN][Anchor]` line and a trace guard skip using
  one canonical key/reason shape:
  `parent-anchored-debris-outside-relative-coverage|<recordingId>|<index>`.
  Do not introduce a second warning cadence for the same event.
- Return a result that callers can use to suppress camera/retarget events after
  a failed prime.
- If `index == ctx.protectedIndex` for a direct watched debris ghost, emit
  `CameraActionType.ExitWatch` after teardown. Do not emit this action for
  sibling debris that are only watch-protected by lineage while the parent
  recording is watched.

Keep the existing
`TryRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage` name only if it
is refactored to do this full handling. Otherwise rename it so the callsite
makes clear that this is a deterministic coverage gate, not a generic anchor
miss.

### 5. Cover loop and overlap playback paths before spawn/load

Audit every path that can position parent-anchored debris:

- Non-loop in-range playback via `RenderInRangeGhost`.
- Loop-synced debris that uses the parent's loop clock and calls
  `RenderInRangeGhost`.
- Primary loop playback in `UpdateLoopingPlayback`.
- Overlap loop playback in `UpdateOverlapPlayback`.
- Endpoint positioning via `PositionGhostAtRecordingEndpoint`.

`RenderInRangeGhost` covers the first two. The loop and overlap paths need the
same early coverage gate using the loop playback UT before all of these:

- `TryReserveSpawnSlot`
- `CreatePendingSpawnState`
- `EnsureGhostVisualsLoaded`
- loop/overlap `ApplyZoneRendering`
- `PositionLoopAtPlaybackUT`

Specific placements:

- In primary loop playback, gate before the state-null loop first-spawn branch
  around `UpdateLoopingPlayback` lines 1628-1644.
- In overlap primary playback, gate before the overlap primary first-spawn
  branch around `UpdateOverlapPlayback` lines 1850-1857.
- In rehydrate paths, gate before `EnsureGhostVisualsLoaded` attempts to rebuild
  visuals for a coverage-retired state.

Endpoint handling should stay close to the current `HandlePastEndGhost` logic.
It already suppresses stale endpoint side effects, finalizes completion when
`ShouldCompleteParentAnchoredDebrisEndpointCoverageMiss` is true, and avoids the
zone/watch path before endpoint classification. It calls
`PositionGhostAtRecordingEndpoint`, which checks
`TryRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage` before endpoint
positioning; preserve that ordering while refactoring the helper.

### 6. Cover watch-mode direct-load and synchronization

Add the same deterministic coverage check at the start of
`GhostPlaybackEngine.EnsureGhostVisualsLoadedForWatch`.

Requirements:

- Evaluate coverage before `forceRebuildLoadedVisuals` destroys/rebuilds the
  current visual state.
- If uncovered, do not call `EnsureGhostVisualsLoaded`.
- Do not call `SynchronizeLoadedGhostForWatch`.
- Mark/hide/teardown through the centralized helper.
- Return `false` so `WatchModeController` cannot target or follow this debris
  pivot from the direct watch-load path.
- Add a log/trace callsite such as
  `GhostPlaybackEngine.EnsureGhostVisualsLoadedForWatch`.

### 7. Suppress loop retarget after coverage-retired priming

`ReusePrimaryGhostAcrossCycle` calls `PrimeLoadedGhostForPlaybackUT`, then may
emit `CameraActionType.RetargetToNewGhost`. If priming determines that
parent-anchored debris is outside Relative coverage, suppress the retarget just
like the existing endpoint-retired branches suppress camera side effects.

Implementation options:

- Make `PrimeLoadedGhostForPlaybackUT` return a result describing whether it
  coverage-retired the state.
- Or have the centralized helper set `state.parentAnchoredDebrisCoverageRetired`
  and check that flag immediately after priming before emitting the retarget.

The first option is preferable because it keeps the decision tied to the exact
playback UT used for the prime and avoids treating the state flag as the source
of truth.

### 8. Add a specific skip reason if the frame summary needs it

If the implementation counts these frames in the per-frame skip summary, add a
new internal enum value such as:

```csharp
ParentAnchoredDebrisCoverageRetired
```

Update:

- `GhostPlaybackSkipReason` and `ToLogToken`.
- `GhostPlaybackEngine.CountFrameSkip`.
- `GhostPlaybackFrameCounters` if a dedicated counter is warranted.
- `FlightPlaybackExplainabilityTests.GhostPlaybackSkipReasonTokens_AreStable`.

If this feels too invasive for the bug fix, keep the frame summary unchanged
and rely on the explicit Anchor warning plus `GhostRenderTrace` guard skip.

## Tests To Add Or Update

### Headless xUnit

Add focused tests in `Source/Parsek.Tests/GhostPlaybackEngineTests.cs`:

1. `RenderInRangeGhost_ParentAnchoredDebrisOutsideCoverage_DoesNotSpawnVisuals`
   - Arrange `state == null`, parent-anchored debris with a Relative section
     ending before the playback UT.
   - Invoke the render path.
   - Assert no flag-event application, no spawn reservation, no visual-load
     attempt, no zone call, no positioner call, and a coverage-retired log
     exists.
   - Use an `IGhostPositioner` fake for zone/position counters and add a narrow
     engine seam/counter for `TryReserveSpawnSlot` and `EnsureGhostVisualsLoaded`
     attempts. Do not rely on a missing snapshot failure as proof that the early
     gate ran.

2. `RenderInRangeGhost_LoadedParentAnchoredDebrisOutsideCoverage_SkipsZoneAndHides`
   - Arrange an existing `GhostPlaybackState` with a loaded ghost or test
     substitute.
   - Assert the state is marked coverage-retired, ghost is inactive, zone count
     stays zero, positioner count stays zero, and FX teardown is called with
     transient effects suppressed.

3. `RenderInRangeGhost_BackInsideRelativeCoverage_ClearsCoverageRetired`
   - Arrange a state previously marked coverage-retired.
   - Evaluate at a playback UT inside the Relative section.
   - Assert the flag clears and normal positioning can proceed.

4. `PositionLoopAtPlaybackUT_ParentAnchoredDebrisOutsideCoverage_DoesNotZoneOrPosition`
   - Cover loop UT, not wall-clock UT.
   - Assert no watch/zone reactivation path runs before the retirement decision.

5. `UpdateLoopingPlayback_ParentAnchoredDebrisOutsideCoverage_DoesNotLoopFirstSpawn`
   - Cover the state-null primary loop first-spawn branch.
   - Assert no `CreatePendingSpawnState` / visual-load attempt occurs.

6. `UpdateOverlapPlayback_ParentAnchoredDebrisOutsideCoverage_DoesNotOverlapPrimarySpawn`
   - Cover overlap primary first-spawn before zone rendering.
   - Assert the overlap primary state is not built when loop UT is outside
     recorded Relative coverage.

7. `EnsureGhostVisualsLoadedForWatch_ParentAnchoredDebrisOutsideCoverage_DoesNotLoadOrSync`
   - Arrange an existing ghost state and call the watch-load API directly.
   - Assert it returns `false`, makes no visual-load attempt, does not call
     `SynchronizeLoadedGhostForWatch`, and marks/hides through the coverage
     helper.

8. `ActivateGhostVisualsIfNeeded_CoverageRetired_ReturnsFalseAndDoesNotSetActive`
   - Cover the engine-owned activation helper, not only
     `ParsekFlight.ShouldAutoActivateGhost`.

9. `ReusePrimaryGhostAcrossCycle_CoverageRetiredPrime_SuppressesRetarget`
   - Arrange a loop-cycle reuse where the prime UT is outside Relative coverage.
   - Assert no `RetargetToNewGhost` camera action is emitted.

10. `CoverageRetiredWatchedGhost_EmitsExitWatch`
    - Arrange `ctx.protectedIndex == index` for direct debris watch.
    - Assert the helper emits one `CameraActionType.ExitWatch`.
    - Add the negative case where the debris is only lineage/watch-protected
      while another recording is watched; no exit action should fire.

11. `CoverageRetiredFlag_GenericVisualCleanup_DoesNotClear`
    - Call `GhostPlaybackState.ClearLoadedVisualReferences` on a flagged state.
    - Assert the coverage-retired flag remains set.

12. `CoverageRetiredFlag_CoveredPlaybackUTClears`
    - Evaluate the same state at a playback UT inside a Relative section.
    - Assert the deterministic helper clears the flag and normal positioning can
      proceed.

13. Keep the existing predicate tests:
   - `ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_*`
   - `ShouldCompleteParentAnchoredDebrisEndpointCoverageMiss_*`

Avoid vacuous tests:

- The existing `SpawnPrimingPositioner` does not count every zone or visual-load
  attempt. Add explicit counters/seams for visual load attempts and watch sync,
  or use a loadable visual substitute.
- Debris with no usable snapshot can fail inside `EnsureGhostVisualsLoaded`
  before reaching the zone path. A passing "no zone" assertion is not enough
  unless the test proves the coverage gate ran first.
- Prefer testing activation through engine behavior unless a small internal
  helper fits the local pattern.
- Headless xUnit should stay at state/fake-contract level. Do not assert a real
  Unity `GameObject.activeSelf` transition in xUnit; verify that property in an
  in-game runtime test.

Update `FlightPlaybackExplainabilityTests` if a new skip reason is added.

### Runtime / In-Game Validation

Do not close or restart KSP from an agent session. If the DLL is locked during
deployment, report it and ask the user to close/restart KSP manually.

Validation steps after implementation:

1. Build:

   ```bash
   dotnet build Source/Parsek/Parsek.csproj
   ```

2. Run focused and then full headless tests:

   ```bash
   dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter GhostPlaybackEngineTests
   dotnet test Source/Parsek.Tests/Parsek.Tests.csproj
   ```

3. Manual KSP repro:
   - Use the same Kerbal X / debris watch scenario from the collected log.
   - Enter watch mode before the debris relative coverage boundary.
   - Let playback pass the `23:56:14-23:56:18` equivalent point.
   - Confirm parent-anchored debris disappears cleanly instead of jumping or
     being restored by watch-protected zone handling.

4. Collect logs:

   ```bash
   python scripts/collect-logs.py watch-mode-debris-retirement-fix
   ```

5. Confirm in `KSP.log`:
   - No `watch-protected-zone-exempt-*` line appears for a ghost after the same
     frame has identified it as outside parent-relative coverage.
   - No watch-mode direct-load log shows `EnsureGhostVisualsLoadedForWatch`
     loading or synchronizing a parent-anchored debris ghost outside Relative
     coverage.
   - If the user directly watches a debris ghost that coverage-retires, watch
     mode exits through `CameraActionType.ExitWatch`; the camera does not keep
     following an inactive debris pivot.
   - Coverage-retired debris logs one clear Anchor warning or rate-limited
     guard skip, not repeated zone reactivation noise.
   - No `RetargetToNewGhost` camera action is emitted for a debris ghost whose
     loop/reuse prime was coverage-retired.
   - No `resolver-miss-or-retired retired=true active=false` loop with stale
     positions for the same ghost over many frames.
   - Past-end completion and watch-mode camera transfer still log normally for
     the parent recording.

## Risks And Guardrails

- Do not make `anchorRetiredThisFrame` persistent. Existing comments and tests
  depend on it being cleared each frame for normal Relative anchor misses.
- Do not permanently suppress an index without considering playback UT. Rewind
  or loop playback can legitimately move back into covered Relative data.
- Do not destroy and remove `ghostStates` without a pre-spawn gate. Otherwise
  the next frame will recreate the same ghost and repeat the retirement.
- Do not run zone rendering for coverage-retired debris. That is the root of
  the observed watch-mode interaction.
- Do not let watch-mode direct loading rebuild/synchronize coverage-retired
  debris. That path bypasses `RenderInRangeGhost`.
- Do not let engine-owned activation re-show coverage-retired debris through
  `ActivateGhostVisualsIfNeeded`.
- Do not let direct debris watch keep targeting an inactive coverage-retired
  pivot; emit the existing `ExitWatch` camera action for that case.
- Do not change recording format or reinterpret PR #776 seed data. The evidence
  points to playback lifetime ordering, not seed serialization.
- Update `CHANGELOG.md` and `docs/dev/todo-and-known-bugs.md` regardless of
  whether a skip enum is added. Their current PR #777 wording already claims
  normal, loop, watch-sync, and endpoint coverage; this follow-up changes that
  behavior contract and must keep those entries honest.
- If adding a skip enum, also follow the post-change checklist for enum changes.

## Implementation Decisions

- `anchorRetiredThisFrame` remains frame-local. The non-loop early uncovered
  path returns before the existing reset, so no reset move is needed for
  `RenderInRangeGhost`; covered frames keep the current reset-before-positioning
  behavior.
- Directly watched debris exits watch mode on coverage retirement via the
  existing `CameraActionType.ExitWatch` event. Lineage-protected sibling debris
  retirement does not exit the parent's watch mode.
- Headless tests do not inspect real Unity `GameObject.activeSelf` transitions.
  That assertion belongs in `Source/Parsek/InGameTests/RuntimeTests.cs`.
- Logging keeps the current Anchor warning cadence/key and adds guard trace
  context without introducing a second warning throttle for the same event.

## Implementation Order

1. Add the runtime state flag and comments in `GhostPlaybackState`.
2. Ensure generic cleanup does not clear the coverage-retired flag; only the
   deterministic helper clears it when coverage is valid again.
3. Extract the deterministic coverage gate helper in `GhostPlaybackEngine`.
4. Call the helper before flag events, spawn, visual load, and zone rendering in
   non-loop in-range playback.
5. Apply the same pre-spawn/pre-load gate to primary loop and overlap loop paths.
6. Apply the same gate to `EnsureGhostVisualsLoadedForWatch` before force
   rebuild/load/sync.
7. Update both `ParsekFlight.ShouldAutoActivateGhost` and
   `GhostPlaybackEngine.ActivateGhostVisualsIfNeeded` to refuse activation for
   coverage-retired debris.
8. Emit `CameraActionType.ExitWatch` when the directly watched debris ghost
   coverage-retires; suppress it for merely lineage-protected sibling debris.
9. Suppress loop reuse camera retarget when the prime was coverage-retired.
10. Add focused xUnit coverage for no-flag-events, no-spawn, no-visual-load,
    no-zone, no-position, watch direct-load, activation guard, direct-watch exit,
    retarget suppression, flag lifecycle, and loop UT behavior.
11. Add or update the in-game runtime test that proves a real loaded ghost stays
    inactive after coverage retirement.
12. Update `CHANGELOG.md` and `docs/dev/todo-and-known-bugs.md` to describe the
    follow-up fix accurately.
13. Run focused tests, then the full xUnit suite.
14. Build and perform the in-game watch-mode repro, then collect logs and verify
   the absence of the zone/reactivation loop.
