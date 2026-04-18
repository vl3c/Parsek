# Plan: #406 follow-up — reuse ghost GameObject across loop cycles

## Problem

When a looping recording reaches its loop-cycle boundary the single-ghost
path in `GhostPlaybackEngine.UpdateLoopingPlayback` calls
`DestroyGhost(..., reason: "loop cycle boundary")` immediately followed by
`SpawnGhost` a few lines below. `SpawnGhost` then re-runs the full
`TryPopulateGhostVisuals` pipeline: part-mesh instantiation, engine FX
size-boost, audio wiring, info-dictionary populate, and — for
reentry-capable recordings — an eventual `TryBuildReentryFx` through the
#450 B3 lazy path.

For a flight recording like "Learstar A1" (268 s, 28 parts, 234 meshes
combined into a 55,878-vert emission shape) looping with a 40 s period
after the cap clamp, this rebuild fires every 40 s. The 2026-04-18 B3
playtest captured the smoking-gun cadence as budget-exceeded WARNs at
exactly 40 s intervals, 21-24 ms per rebuild:

- `logs/2026-04-18_1947_450-b3-playtest/KSP.log` line 38608
  (`Loop cadence #284 ... 40.000s`)
- Same log, lines 41133, 43824, 46395 — three subsequent spawn WARNs
  exactly 40 s apart, each with the heaviest-spawn breakdown showing a
  full timeline+dicts+reentry rebuild.

The original `#406` (shipped in PR #309) exempted STATIONARY showcase
ghosts from the reentry-FX build. #450 Phase B3 further deferred the
reentry-FX build to the first in-atmosphere frame — which saves the
reentry work on the FIRST spawn but is paid all over again on every
loop-cycle rebuild (destroy clears the reentryFxPendingBuild state, the
respawn re-defers, and the first post-wrap in-atmosphere frame re-runs
the full ~7 ms build). Both fixes left the per-cycle
destroy+rebuild hitch in place.

The entry `## ~~406.~~` in `docs/dev/todo-and-known-bugs.md` flags this
explicitly as an unshipped follow-up: "reuse ghost GameObject across
loop cycles instead of destroy/rebuild (would eliminate the remaining
per-cycle material/audio clone cost)".

## Current behaviour

`Source/Parsek/GhostPlaybackEngine.cs:1018-1058`:

```csharp
bool cycleChanged = HasLoopCycleChanged(state, cycleIndex);
if (cycleChanged && state != null)
{
    if (state.ghost != null)
        PositionGhostAtLoopEndpoint(index, traj, state);

    bool needsExplosion = ...;
    if (needsExplosion)
        TriggerExplosionIfDestroyed(state, traj, index, ctx.warpRate);

    OnLoopCameraAction?.Invoke(...);      // ExplosionHoldStart/End
    OnLoopRestarted?.Invoke(...);

    GhostPlaybackLogic.ResetReentryFx(state, index);
    DestroyGhost(index, traj, flags, reason: "loop cycle boundary");
    ghostActive = false;
    state = null;
}
// ... then, a few lines below, the `if (state == null)` branch
// calls SpawnGhost and fires OnLoopCameraAction(RetargetToNewGhost).
```

`DestroyGhost` removes from `ghostStates`, calls
`DestroyGhostResourcesWithMetrics` (which destroys materials, stops
engine/RCS particle systems — `lingerParticleSystems: true` so smoke
trails linger briefly — stops all audio, destroys reentry FX resources,
`UnityEngine.Object.Destroy(state.ghost)`, destroys fake canopies), then
clears `loopPhaseOffsets` and the missing-audio-clip dedupe set.

`SpawnGhost` allocates a fresh `GhostPlaybackState`, runs
`BuildGhostVisualsWithMetrics` (which is the heavy one — part-mesh
clone, engine FX size-boost, audio wiring, info-dictionary populate,
and the #450 B3 reentry deferral branch), then
`PrimeLoadedGhostForPlaybackUT` positions the ghost and applies
`ApplyFrameVisuals` with `allowTransientEffects: false`.

The destroy+rebuild is roughly 21-22 ms on Learstar-class recordings,
paid every 40 s.

## Proposed reuse path

Introduce a new engine method, `ReusePrimaryGhostAcrossCycle(int index,
IPlaybackTrajectory traj, double playbackUT, long newCycleIndex)`, that
replaces the `DestroyGhost` call at the cycle boundary and short-
circuits the subsequent `state == null` spawn branch. The call site
becomes:

```csharp
if (cycleChanged && state != null)
{
    if (state.ghost != null)
        PositionGhostAtLoopEndpoint(index, traj, state);

    bool needsExplosion = ...;
    if (needsExplosion)
        TriggerExplosionIfDestroyed(state, traj, index, ctx.warpRate);

    OnLoopCameraAction?.Invoke(... ExplosionHoldStart/End ...);
    OnLoopRestarted?.Invoke(...);

    // #406 follow-up: reuse the ghost GameObject and its visuals.
    // Preserves reentryFxInfo + pendingBuild state; resets playback
    // iterators, explosionFired, pauseHidden/rcsSuppressed; restores
    // visibility for parts hidden by prior-cycle decouple/destroy
    // events. This is NOT a spawn (frameSpawnCount unchanged) and
    // NOT a lazy-reentry-build (frameLazyReentryBuildCount unchanged).
    ReusePrimaryGhostAcrossCycle(index, traj, loopUT, cycleIndex);
    // state is the SAME object. ghostActive stays true. No
    // OnGhostCreated is fired — the ghost was never destroyed.
    // OnLoopCameraAction(RetargetToNewGhost) is STILL fired because
    // WatchModeController/ParsekFlight uses it to re-snap the camera
    // to the ghost's new pivot position after the wrap.
}
```

`ReusePrimaryGhostAcrossCycle` is a small orchestrator that:

1. Calls a new pure static `GhostPlaybackLogic.ResetForLoopCycle(state)`
   that handles all the field-level resets (see the state-preservation
   table below).
2. Calls existing helper `GhostPlaybackLogic.HideAllGhostParts(state)`
   to resurface the "state of the world at cycle start" — so parts
   decoupled mid-cycle get reactivated by `ApplyPartEvents` on the
   rebuild cycle.
3. Re-runs `GhostPlaybackLogic.InitializeInventoryPlacementVisibility`
   and `GhostPlaybackLogic.RefreshCompoundPartVisibility` so inventory
   placements / compound part visibility match the "pre-event"
   baseline.
4. Calls `PrimeLoadedGhostForPlaybackUT(index, traj, state, loopUT)` —
   the same function `SpawnGhost` already uses to walk the event list
   up to `playbackUT`, position the ghost, and apply visuals with
   `allowTransientEffects: false` (so the first frame of the reuse does
   NOT fire decouple puffs for events the previous cycle already
   applied).
5. Emits a Verbose log with a distinctive UTF-16 string for DLL
   verification.
6. Fires `OnLoopCameraAction(RetargetToNewGhost)` with the same
   `state.cameraPivot` as before — the pivot Transform is a child of
   the reused ghost, so its position has moved to the loop endpoint
   (from step 1 in the existing cycle-boundary branch) and the camera
   handshake behaves the same as if a fresh ghost was spawned.

Explicit non-action: do NOT call `SpawnGhost` and do NOT pass through
the `TryReserveSpawnSlot` throttle gate. Reuse does not consume a spawn
slot (#414 invariant) and does not count toward `frameSpawnCount` or
`MaxSpawnsPerFrame`.

Step-3 visibility resets are orchestrated in the helper rather than
inline at the call site so the invariant "a reused ghost starts each
cycle with the same visibility baseline a fresh spawn would have" is
stated in one place and covered by unit tests.

### Why step 2 exists — worked example

On a Learstar A1 loop cycle, the previous cycle's event stream has:

- `t=95s`  PartEventType.Decoupled   (first-stage booster, 4 parts hidden)
- `t=125s` PartEventType.Decoupled   (second-stage, 6 parts hidden)
- `t=210s` PartEventType.Destroyed   (command pod on reentry)

At `cycleChanged` (`cycleIndex == last+1`), 10 part GameObjects in the
hierarchy are inactive. `ApplyPartEvents` only advances
`state.partEventIndex` forward — it never rewinds and it never re-shows
a hidden part. On a destroy+rebuild today this is fine because the
rebuild instantiates a fresh ghost with all parts visible. On reuse,
if we don't re-show the 10 hidden parts, they stay hidden across all
subsequent cycles — a visible regression.

`HideAllGhostParts` is the wrong name for the fix; the step we want is
"re-show all parts that snapshot semantics say should be visible at
`cycleStartUT`". Concretely that is:

- Every child under `state.ghost.transform` except `state.cameraPivot`:
  `SetActive(true)`.
- `InitializeInventoryPlacementVisibility` then re-hides inventory
  parts whose first placement event is `InventoryPartPlaced` (matches
  the snapshot-spawn baseline).
- `RefreshCompoundPartVisibility` then re-hides compound parts whose
  target is missing (matches snapshot-spawn baseline).
- `logicalPartIds` is restored to the full snapshot set via a cheap
  re-materialization through `GhostVisualBuilder.BuildSnapshotPartIdSet`
  (same call the spawn path makes, so identical semantics; the set is
  only mutated by part events, so post-event it's missing the 10
  decoupled pids).

Concrete helper name chosen to avoid confusion:
`GhostPlaybackLogic.ReactivateGhostPartHierarchyForLoopRewind(state)`.

### Interaction with #450 B3 `reentryFxPendingBuild`

The #450 B3 flag is **preserved across reuse**. The rules are:

- If `state.reentryFxInfo != null` — the lazy build already fired this
  ghost lifetime. The combined emission mesh, fire-particle system,
  cloned glow materials are alive and correct. Reuse leaves them alone.
  `ResetReentryFx` (existing helper) is called as part of the reuse so
  `lastIntensity` drops to 0 and the emissive/color materials revert to
  their cold values — same as what the existing cycle-boundary branch
  already does at line 1054 right before today's destroy.
- If `state.reentryFxInfo == null && state.reentryFxPendingBuild == true`
  — the ghost has not yet been in atmosphere. Reuse does not touch the
  flag. The next in-atmosphere frame will still fire the lazy build at
  most once per ghost lifetime, as B3 intends.
- If `state.reentryFxInfo == null && state.reentryFxPendingBuild == false`
  — the trajectory has no reentry potential (showcase/EVA). Reuse does
  not touch either field.

Compare with today's destroy+rebuild path: the destroy calls
`DestroyReentryFxResources(state.reentryFxInfo)` and nulls the state,
the spawn runs `HasReentryPotential` again and re-defers
(`reentryFxPendingBuild = true`), and the first in-atmosphere frame
after the wrap re-pays the full build. The net reuse savings here are
exactly the 7 ms reentry rebuild cost on flight recordings that reach
atmosphere, plus the destroy-side resource destruction.

### Interaction with overlap-primary-cycle-change

The overlap path at `GhostPlaybackEngine.cs:1211-1235` explicitly moves
the old primary to the overlap list (`overlaps.Add(primaryState)`) and
spawns a NEW primary. This is a different behaviour from the single-
ghost path: the old primary has to keep playing (it may still be
visibly rendered by another cycle's camera/follow) while the new
primary starts at cycle 0. Two separate ghost GameObjects must
coexist.

**This plan leaves the overlap path unchanged.** The reuse optimisation
applies only to the single-ghost loop path where there is exactly one
primary and the old "version" disappears the instant the new cycle
starts. The overlap branch does not even share a control-flow path with
the single-ghost branch — it returns early at line 1002.

### State preservation table

One row per `GhostPlaybackState` field (from `GhostPlaybackState.cs:15-69`),
categorised as **preserve** (reuse keeps the value), **reset** (reuse
sets to the spawn baseline), or **conditional** (reuse resets only when
a specific condition holds).

| Field | Category | Reason |
|---|---|---|
| `vesselName` | preserve | Same ghost, same vessel. |
| `ghost` | preserve | The whole point of the reuse. |
| `materials` | preserve | Cloned once at spawn; reused. |
| `playbackIndex` | reset (0) | Cycle restarts — iterators go back to 0. |
| `partEventIndex` | reset (0) | Same. |
| `loopCycleIndex` | reset (newCycleIndex) | Caller passes the new cycle. |
| `partTree` | preserve | Snapshot-derived, stable. |
| `logicalPartIds` | reset (re-materialize) | Prior-cycle events pruned pids from this set; rebuild from snapshot via `GhostVisualBuilder.BuildSnapshotPartIdSet` so cycle-start baseline matches spawn. |
| `parachuteInfos` | preserve | Dictionaries keyed by pid; GameObjects still alive. |
| `jettisonInfos` | preserve | Same. |
| `engineInfos` | preserve | Same. |
| `rcsInfos` | preserve | Same. |
| `audioInfos` | preserve | Same. The audio sources stay alive; `ApplyFrameVisuals` with `allowTransientEffects: false` then re-drives active state. |
| `oneShotAudio` | preserve | Shared source, no per-cycle invalidation. |
| `audioMuted` | reset (false) | Let the next frame's atmosphere/mute pipeline re-decide. |
| `atmosphereFactor` | reset (1f) | Matches spawn default; re-computed next frame. |
| `cachedAudioBody` | preserve | Body lookup is cache-valid across cycles. |
| `cachedAudioBodyName` | preserve | Same. |
| `roboticInfos` | preserve | Snapshot-derived. |
| `deployableInfos` | preserve | Snapshot-derived. |
| `heatInfos` | preserve | Needed for lazy reentry build if still pending. |
| `lightInfos` | preserve | Snapshot-derived. |
| `lightPlaybackStates` | conditional (clear) | Per-part runtime on/off/blink state accrued from events; cycle rewinds to snapshot baseline. Clear the dictionary; events on the new cycle repopulate. |
| `colorChangerInfos` | preserve | Snapshot-derived. |
| `fairingInfos` | preserve | Snapshot-derived. |
| `compoundPartInfos` | preserve | Snapshot-derived. |
| `fakeCanopies` | reset (destroy and null) | These are transient decoupled canopy substitutes; leftover mid-cycle ones would look wrong on the rewind. Call `GhostPlaybackLogic.DestroyAllFakeCanopies(state)`. |
| `reentryFxInfo` | preserve (and reset-to-cold) | Call existing `ResetReentryFx` — cold emissive, Stop+Clear particle system. Structure survives. |
| `reentryFxPendingBuild` | preserve | See interaction section above. |
| `reentryMpb` | preserve | Per-ghost property block; no per-cycle invalidation. |
| `explosionFired` | reset (false) | The previous cycle may have fired it; the new cycle needs to re-decide. |
| `pauseHidden` | reset (false) | Stale flag from a prior cycle's pause-window pass; next frame re-decides. |
| `rcsSuppressed` | reset (false) | Call `RestoreAllRcsEmissions` before resetting so suppressed emitters get their renderers/audio state back; next frame re-decides suppression. |
| `fidelityReduced` / `distanceLodReduced` | preserve | Distance LOD is evaluated every frame on the new cycle too; values are self-correcting on the next `ApplyDistanceLodFidelity` call. |
| `fidelityDisabledRenderers` | preserve | Paired with distanceLodReduced; self-correcting. |
| `simplified` | preserve | Ditto; self-correcting. |
| `deferVisibilityUntilPlaybackSync` | reset (true) | Mirrors spawn behaviour so the first reused-cycle frame stays hidden until `PrimeLoadedGhostForPlaybackUT` positions at `loopUT`. |
| `cameraPivot` | preserve | Camera event handshake depends on Transform identity. |
| `horizonProxy` | preserve | Same. |
| `lastInterpolatedVelocity` / `lastInterpolatedBodyName` / `lastInterpolatedAltitude` | preserve | Overwritten by `PrimeLoadedGhostForPlaybackUT` on the first frame of the new cycle; safe to leave. |
| `lastValidHorizonForward` | preserve | Overwritten on first positioned frame. |
| `currentZone` | preserve | Recomputed every frame via `ApplyZoneRendering`. |
| `lastDistance` / `lastRenderDistance` | preserve | Recomputed every frame. |
| `flagEventIndex` | reset (0) | Flag events index walks monotonically with UT; cycle restart re-walks. Flag duplicate check prevents re-spawn. |
| `hadVisibleRenderersLastFrame` | reset (false) | `ResetGhostAppearanceTracking` handles this, same as spawn. |
| `appearanceCount` | reset (0) | Mirrors spawn-path behaviour via the same helper. |

### Invariants preserved

- Non-loop-cycle `DestroyGhost` call sites still fully destroy. Only
  the `reason: "loop cycle boundary"` call site at line 1055 is
  rerouted. The other nine destroy call sites in
  `GhostPlaybackEngine.cs` (disabled/suppressed, before-activation-start,
  anchor unloaded, parent-loop cycle change, debris UT range,
  stale-past-end, loop-UT-computation-failure, before-activation-start
  in overlap, ghost-hidden-by-distance-LOD unload) are untouched.
- Distance-tier LOD still applies every frame via the existing
  `zoneResult` / `ApplyDistanceLodFidelity` chain. Reuse does not
  bypass it — in fact by preserving `fidelityDisabledRenderers` we
  ensure zero flicker on the reuse frame.
- #414 spawn throttle: reuse does NOT call `TryReserveSpawnSlot` and
  does NOT increment `frameSpawnCount`. A cycle-rebuild via reuse is
  strictly cheaper than a cycle-rebuild via destroy+spawn was, so
  budget headroom improves.
- Overlap `overlapStates` and `UpdateOverlapPlayback` are untouched.
- Camera handshake: `OnLoopCameraAction(ExplosionHoldStart/End)` fires
  before the reuse (unchanged). `OnLoopCameraAction(RetargetToNewGhost)`
  still fires after the reuse so WatchModeController / ParsekFlight can
  re-snap the camera to the ghost's new pivot position. `cameraPivot`
  Transform identity is preserved, which means any hard references the
  camera code holds through the retarget remain valid.
- `OnLoopRestarted` still fires. Subscribers that inspect
  `State` see the same object; `PreviousCycleIndex` and
  `NewCycleIndex` are identical to today's behaviour because they are
  captured from the pre-reuse state/cycle.
- `OnGhostCreated` does NOT fire on reuse — the ghost was not
  recreated. `OnGhostDestroyed` does NOT fire either. The "ghost map
  ProtoVessel creation" deferred event only fires on the first spawn
  of a ghost; across the reuse we do not recreate the ghost-map
  vessel. This matches the "ghost GameObject survives" contract.

### Files touched

- `Source/Parsek/GhostPlaybackEngine.cs` — new method
  `ReusePrimaryGhostAcrossCycle`, replacement of the
  `DestroyGhost(... loop cycle boundary)` call, and deletion of the
  subsequent `state == null` spawn branch's cycle-change code path
  (the `cycleChanged` exemption from `TryReserveSpawnSlot` becomes
  dead code and is removed with a comment pointing to this plan).
- `Source/Parsek/GhostPlaybackLogic.cs` — new pure static helpers
  `ResetForLoopCycle(GhostPlaybackState state)` and
  `ReactivateGhostPartHierarchyForLoopRewind(GhostPlaybackState state)`.
- `Source/Parsek/GhostPlaybackState.cs` — no new fields.
- `Source/Parsek.Tests/GhostPlaybackLoopCycleReuseTests.cs` — new test
  class.
- `Source/Parsek/InGameTests/RuntimeTests.cs` — one new runtime test
  (see Tests section).
- `CHANGELOG.md` — new entry under `### Bug Fixes` (0.8.2).
- `docs/dev/todo-and-known-bugs.md` — append a "Follow-up shipped"
  section to the existing `## ~~406.~~` entry.

### Tests

Unit tests (`GhostPlaybackLoopCycleReuseTests.cs`,
`[Collection("Sequential")]` because we touch `ParsekLog`):

1. `ResetForLoopCycle_ResetsPlaybackIterators_PreservesDictionaries` —
   preload a state with `playbackIndex=100`, `partEventIndex=50`,
   `flagEventIndex=3`, `explosionFired=true`, `pauseHidden=true`,
   `rcsSuppressed=true`, non-null `engineInfos`/`rcsInfos`/`audioInfos`/
   `heatInfos`. After reset: iterators are 0, flags false, dictionaries
   are the same object references (Assert.Same).
   **What makes it fail:** reset accidentally nulls a dictionary.
2. `ResetForLoopCycle_PreservesReentryFxPendingBuild` — two sub-cases:
   `reentryFxPendingBuild=true` stays true; `reentryFxPendingBuild=false`
   stays false. `reentryFxInfo` reference is preserved. Emits
   a log assertion that the Verbose "reuse" line appears.
   **What makes it fail:** a bug where the reset clears the pending
   flag would let the reentry work re-defer and re-pay on every cycle.
3. `ResetForLoopCycle_ResetsLoopCycleIndex_ToArgument` — verify the
   new cycle index is written.
   **What makes it fail:** off-by-one on the cycle index would cause
   `HasLoopCycleChanged` to re-fire on the very next frame.
4. `ResetForLoopCycle_ClearsLightPlaybackStates` — dictionary cleared
   but reference preserved.
   **What makes it fail:** blink state from the previous cycle leaks
   into the new cycle as a visible blinking light that shouldn't be on.
5. `ResetForLoopCycle_ClearsDeferVisibilityFlag_AfterPrime` —
   actually, this is a post-condition of the orchestrator, not the
   pure helper. Covered in #8 below instead.
6. `ReactivateGhostPartHierarchyForLoopRewind_ReactivatesHiddenParts` —
   unit test using a synthetic root GameObject with 5 children, 2
   inactive. After the call: 5 active (camera pivot excluded).
   **What makes it fail:** decouple-hidden parts stay hidden forever
   on subsequent cycles.
7. `ReactivateGhostPartHierarchyForLoopRewind_SkipsCameraPivot` — the
   camera pivot child, if set, must remain active AND be skipped from
   the iteration so its child horizonProxy is not reactivated
   redundantly.
   **What makes it fail:** FlightCamera snaps away from the ghost
   because the pivot was disabled and re-enabled mid-frame.
8. `ReusePrimaryGhostAcrossCycle_DoesNotIncrementFrameSpawnCount` —
   driven via a narrow test seam on the engine. Ghost reuse fires,
   `FrameSpawnCountForTesting` stays 0.
   **What makes it fail:** reuse accidentally calls `SpawnGhost`,
   silently regressing #414 by consuming a spawn slot.
9. `ReusePrimaryGhostAcrossCycle_DoesNotIncrementLazyReentryBuildCount`
   — same harness as #8, with `reentryFxPendingBuild=true` and a
   simulated body lookup. `FrameLazyReentryBuildCountForTesting`
   stays 0 across the reuse call itself (the NEXT frame's
   `UpdateReentryFx` might build, but that's the B3 path, not reuse).
   **What makes it fail:** the reuse inadvertently triggers the lazy
   build on the cycle-rebuild frame, reintroducing the #450 B3 hitch.
10. `ReusePrimaryGhostAcrossCycle_LogsVerboseLine` — capture log via
    `ParsekLog.TestSinkForTesting`; assert the reuse line contains
    `[Engine]`, the distinctive UTF-16 string (`"ghost reused across
    loop cycle"`), the recording index, vessel name, old cycle, new
    cycle.
    **What makes it fail:** the diagnostic log is missing, reducing
    the reuse path to an unobservable optimisation and breaking the
    DLL-verification recipe.
11. `ReusePrimaryGhostAcrossCycle_PreservesCameraPivotIdentity` — hold
    a reference to `state.cameraPivot` before the call, assert it is
    the same instance after. Same for `state.ghost`.
    **What makes it fail:** a bug where the reuse path accidentally
    re-parents or replaces the pivot would break WatchModeController.
12. `ReusePrimaryGhostAcrossCycle_ResetsExplosionFired` — pre-set
    `explosionFired=true`; post-condition false. Then simulate a
    mid-cycle destroy event: `TriggerExplosionIfDestroyed` should fire
    again.
    **What makes it fail:** explosion fails to re-fire on the next
    cycle, losing the visual punch at loop wraps.

In-game runtime test (`RuntimeTests.cs`):

13. `Ghost_LoopCycleBoundary_ReusesGhostGameObject` — spawn an
    injected synthetic recording with a short loop period. Record
    `state.ghost` instance id and `state.reentryFxInfo` reference
    before the cycle boundary, wait for the cycle wrap, assert both
    are the same instance ids after. Also assert
    `DiagnosticsState.health.ghostBuildsThisSession` did not increase
    across the boundary and no `ghost-destroy` log line fired with
    `reason: loop cycle boundary` during the wrap window.
    **What makes it fail:** the runtime behaviour silently regresses
    to destroy+spawn (e.g. if the call-site branch is merged wrong,
    the unit tests pass but the engine still destroys).

### Diagnostic logging

Every reuse fires exactly one Verbose line (distinctive UTF-16 string
for the DLL verification recipe):

```
[Parsek][VERBOSE][Engine] Ghost #3 "Learstar A1" ghost reused across
  loop cycle: from cycle=12 to cycle=13 (playbackIndex->0,
  partEventIndex->0, reentryFxPendingBuild=false,
  reentryFxInfo=<present|null>)
```

Rate-limited to one per 1.0s per (index) key — matches the existing
`spawn-{index}` cadence. For a stationary showcase looping every 40 s
this is one line per wrap; for dense overlap this path doesn't fire
(overlap uses the separate branch).

No new Warn or Info lines — the reuse is a quiet optimisation. The
existing `Ghost ENTERED range` log at spawn remains only for true
first-spawn cases; subsequent cycles log the reuse line instead. The
`Ghost #... destroyed (loop cycle boundary)` line disappears (it
never fires on the reuse path).

Test-sink assertion: the unit tests for `ReusePrimaryGhostAcrossCycle`
capture via `ParsekLog.TestSinkForTesting` and assert the reuse line
appears (test #10 above). This doubles as the DLL-verification string
source.

### Rollout / risk

The highest-probability visual regression is parts looking wrong on the
first frame of the reused cycle — either decoupled parts not
reappearing, or a decouple-puff accidentally firing for a part that
was already hidden. The plan addresses both:

- `ReactivateGhostPartHierarchyForLoopRewind` re-shows hidden parts
  before `PrimeLoadedGhostForPlaybackUT` walks forward.
- `PrimeLoadedGhostForPlaybackUT` calls `ApplyFrameVisuals` with
  `allowTransientEffects: false`, which is the same guard `SpawnGhost`
  already uses to suppress decouple puffs on the initial spawn.

Secondary risk: the pause-window branch at line 1435-1446 reads
`state.pauseHidden` to know if the ghost is already in the pause
window. Resetting `pauseHidden` to false on cycle boundary matches
the spawn-path semantics (a fresh ghost also has
`pauseHidden == false`).

Tertiary risk: the audio state machine. Engine/RCS/audio sources stay
alive across reuse. `ApplyFrameVisuals` on the first reuse frame calls
`MuteAllAudio` or `UnmuteAllAudio` based on warp state, and
`UpdateAudioAtmosphere` re-drives atmospheric attenuation. There is no
audible glitch because the audio source is not destroyed.

Quaternary risk: a compound part whose target pid was decoupled in the
prior cycle. After `ReactivateGhostPartHierarchyForLoopRewind`,
`logicalPartIds` is restored from the snapshot, so the compound-
visibility refresh sees the target pid again and does not hide the
compound. Covered implicitly by step-3 in the reuse orchestrator.

Out-of-scope visual risk: smoke trails from engine/RCS particle
systems from the end of the previous cycle. Today's destroy path
detaches these with `lingerParticleSystems: true` (they persist in the
world and auto-expire). The reuse path calls `ResetReentryFx` which
stops+clears the reentry particle system only; engine/RCS trails
follow the existing `StopAllEngineFx`/`StopAllRcsFx` path from inside
`ApplyFrameVisuals` when `suppressVisualFx` is true or via the first
frame's natural event application. Cycle-wrap smoke-trail continuity
was already imperfect on destroy+rebuild (old trails linger as orphan
particles in the world for a few seconds); reuse preserves that exact
visual at zero incremental code cost.

### Stale-reentry-emission-mesh risk (newly identified during plan self-review)

`GhostVisualBuilder.RebuildReentryMeshes` at
`Source/Parsek/GhostVisualBuilder.cs:6412` uses
`GetComponentsInChildren<MeshFilter>(false)` — false meaning
"active only". Every time a part is decoupled or destroyed mid-cycle,
`ApplyPartEvents` sets `needsReentryMeshRebuild=true` and the combined
emission mesh is rebuilt to drop the removed verts.

On reuse, the moment after
`ReactivateGhostPartHierarchyForLoopRewind` re-activates the
previously-decoupled parts, the combined emission mesh is stale (it
was last rebuilt AFTER the decouple, so it has too few verts). If the
new cycle fires another decouple event before any reentry FX renders,
the stale mesh is harmless (the next event rebuilds it anyway). But if
the new cycle re-enters atmosphere BEFORE the first decouple event —
which for the Learstar A1 case never happens (decouples at ~95 s and
~125 s precede reentry at ~210 s) but could happen on other
recordings — reentry FX would emit from only the post-decouple subset.

Mitigation: `ReactivateGhostPartHierarchyForLoopRewind` also calls
`GhostVisualBuilder.RebuildReentryMeshes(state.ghost, state.reentryFxInfo)`
when `state.reentryFxInfo != null`. This is the same call
`ApplyPartEvents` already makes on decouple events, so it re-uses the
tested code path. Cost per reuse: one mesh combine over all active
MeshFilters in the ghost hierarchy — ~1-2 ms on Learstar, still far
below the 21 ms destroy+rebuild cost this plan eliminates.

When `reentryFxInfo == null` (showcase/EVA, or still-pending B3
deferral), the call is skipped — `RebuildReentryMeshes` early-returns
on null info anyway, but the gate avoids the MeshFilter
`GetComponentsInChildren` scan in those cases.

### CHANGELOG entry wording

Under `## 0.8.2 > ### Bug Fixes`:

```
- `#406 follow-up` Ghost GameObject is now reused across loop-cycle
  boundaries instead of destroyed and rebuilt, eliminating ~21 ms of
  stutter every loop period on flight recordings with reentry FX.
```

One line, two short sentences, user-facing. Tech detail (reentryFx
preservation, the new helpers, etc.) lives in this plan and in the
todo-and-known-bugs "Follow-up shipped" section.

### todo-and-known-bugs.md update

Append to the existing `## ~~406.~~` entry a new "Follow-up shipped"
subsection:

```
**Follow-up shipped (PR #xxx):** reuse ghost GameObject across loop
cycles — the per-cycle rebuild hitch was the dominant remaining cost
on flight recordings after #309 and #450 B3. Cycle-boundary path now
calls `ReusePrimaryGhostAcrossCycle` instead of `DestroyGhost + SpawnGhost`;
the ghost, `reentryFxInfo`, `reentryFxPendingBuild`, and all info
dictionaries survive. Only playback iterators, per-cycle flags
(`explosionFired`, `pauseHidden`, `rcsSuppressed`,
`lightPlaybackStates`, `fakeCanopies`, `logicalPartIds`), and part
visibility are reset. The #414 spawn throttle is NOT consumed by a
reuse, and the #450 B3 lazy-reentry-build counter is NOT consumed
either. Evidence: 2026-04-18 B3 playtest showed four budget-exceeded
WARNs at exactly 40 s cadence, each with 21-24 ms spawn attribution,
confirming the cycle-rebuild was the remaining dominant cost.
```

Keep the `~~406.~~` strike-through and the original body untouched.
```

## Worked call-site diff preview

Before (current, `GhostPlaybackEngine.cs:1018-1088`):

```csharp
bool cycleChanged = HasLoopCycleChanged(state, cycleIndex);
if (cycleChanged && state != null)
{
    if (state.ghost != null)
        PositionGhostAtLoopEndpoint(index, traj, state);
    bool needsExplosion = ...;
    if (needsExplosion)
        TriggerExplosionIfDestroyed(state, traj, index, ctx.warpRate);
    OnLoopCameraAction?.Invoke(... ExplosionHoldStart/End ...);
    OnLoopRestarted?.Invoke(...);
    GhostPlaybackLogic.ResetReentryFx(state, index);
    DestroyGhost(index, traj, flags, reason: "loop cycle boundary");
    ghostActive = false;
    state = null;
}

// ...

if (state == null)
{
    if (!cycleChanged && !TryReserveSpawnSlot(index, "loop-first-spawn"))
        return;
    SpawnGhost(index, traj, loopUT);
    ...
    state.loopCycleIndex = cycleIndex;
    OnLoopCameraAction?.Invoke(... RetargetToNewGhost ...);
    ghostActive = true;
}
```

After (proposed):

```csharp
bool cycleChanged = HasLoopCycleChanged(state, cycleIndex);
if (cycleChanged && state != null)
{
    if (state.ghost != null)
        PositionGhostAtLoopEndpoint(index, traj, state);
    bool needsExplosion = ...;
    if (needsExplosion)
        TriggerExplosionIfDestroyed(state, traj, index, ctx.warpRate);
    OnLoopCameraAction?.Invoke(... ExplosionHoldStart/End ...);
    OnLoopRestarted?.Invoke(...);
    GhostPlaybackLogic.ResetReentryFx(state, index);

    // #406 follow-up: reuse ghost GameObject across loop cycles.
    ReusePrimaryGhostAcrossCycle(index, traj, loopUT, cycleIndex);
    OnLoopCameraAction?.Invoke(... RetargetToNewGhost ...);
    // state is the SAME object. ghostActive stays true.
    // Skip the "state == null" spawn branch below.
}
else if (state == null)
{
    // True first-spawn path — ghost has never existed this session.
    if (!TryReserveSpawnSlot(index, "loop-first-spawn"))
        return;
    SpawnGhost(index, traj, loopUT);
    ...
    state.loopCycleIndex = cycleIndex;
    OnLoopCameraAction?.Invoke(... RetargetToNewGhost ...);
    ghostActive = true;
}
```

The #414 cycleChanged-exempt throttle comment is deleted; the comment
body at line 1069-1081 is replaced with a one-liner pointing to this
plan.
