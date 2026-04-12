# Fix Plan: Bug #326 — Landed EVA branch seeded as Atmospheric

**Bug:** #326  
**Branch:** `fix/phase-11-5-bug-investigation`  
**Status:** Investigation complete, implementation not started

## Problem

When a player EVAs from a landed vessel, KSP can spend one frame with the ship still
as `FlightGlobals.ActiveVessel` before switching focus to the kerbal. In that window,
Parsek creates the EVA branch with:

- the ship as the active child recording
- the new EVA kerbal as the background child recording

That background EVA child is initialized from a transient pre-switch state. On
atmospheric bodies, the raw classifier sees `situation != LANDED` and
`altitude < atmosphereDepth`, so the first background `TrackSection` becomes
`Atmospheric` even though the kerbal is effectively on the ground.

This workflow currently exposes two related defects:

1. the branch keeps a bogus 1-point non-leaf EVA stub
2. later near-ground EVA motion on Kerbin can still be misclassified as `Atmospheric`,
   which creates a real optimizer split candidate

## Investigation Findings

### Repro sequence from `logs/2026-04-12_2242_quickload-branch-gaps-s10/KSP.log`

First EVA branch:

- `13085`: `DeferredEvaBranch: ship still active (KSP hasn't switched)`
- `13094`: `BgRecorder TrackSection started: env=Atmospheric ... pid=1614280122 at UT=70.24`
- `13213`: one background point sampled for that EVA child before the switch
- `13257`: the atmospheric background section closes with `frames=1`
- `13260`: the EVA child is removed from background and promoted
- `13289`: the promoted active EVA recorder starts correctly as `SurfaceStationary`

Second EVA branch repeats the same pattern:

- `15552`: `DeferredEvaBranch: ship still active`
- `15559`: background EVA child starts as `Atmospheric`

Saved/runtime outcome later in the same log:

- `19010`: `Recording #2: "Jebediah Kerman" UT 70-79, 1 pts, vessel`
- `19013-19014`: second EVA branch becomes two chain entries:
  - `#5 ... UT 92-99, 13 pts, ghost, chain idx=0`
  - `#6 ... UT 99-127, 44 pts, vessel, chain idx=1`

Later in the second EVA branch, there is also a separate background-classification flip:

- `15918-15921`: `SurfaceMobile -> Atmospheric` at `UT=98.68`, then a new atmospheric
  background section starts for the EVA child
- `17537-17540`: the optimizer later splits at exactly that `UT=98.68` boundary

### Runtime path for the first symptom (the 1-point stub)

1. `ParsekFlight.OnCrewOnEva` stops the active recorder and defers branch creation.
2. `ParsekFlight.DeferredEvaBranch` can still observe the ship as active one frame later.
3. `ParsekFlight.CreateSplitBranch` immediately adds the EVA child to `BackgroundMap`
   and calls `backgroundRecorder.OnVesselBackgrounded(...)`.
4. `BackgroundRecorder.InitializeLoadedState` opens the first `TrackSection` using
   `ClassifyBackgroundEnvironment(...)`, which currently trusts the raw loaded-vessel
   situation/altitude at that instant.
5. `OnVesselSwitchComplete` promotes the EVA child moments later, but by then the bad
   background section has already been flushed into the recording.

### Runtime path for the later split

After the second EVA branch is promoted correctly, the EVA child becomes backgrounded
again when the player switches back to the rover. At that point:

1. the background EVA child starts correctly as `SurfaceMobile`
2. `BackgroundRecorder.OnBackgroundPhysicsFrame` later re-classifies it as
   `Atmospheric` at `UT=98.68`
3. that atmospheric section lasts long enough to survive into the saved recording
4. the optimizer legitimately splits on that later section boundary

## Root Cause

This is not a single isolated defect.

Bug `326` is really two related defects:

1. **Initial branch handoff defect:** the EVA child is sometimes background-initialized
   before its surface state stabilizes, so the first section is seeded from a transient
   pre-switch state.
2. **Ongoing atmospheric-body EVA classification defect:** once the EVA is backgrounded
   later, near-ground movement on an atmospheric body can still flip to
   `Atmospheric` because the current near-surface override only exists for airless
   bodies.

The fix plan should address both. Fixing only the first symptom would remove the
1-point stub, but would not remove the later `UT=98.68` split boundary seen in the log.

## Fix Goal

For landed/splashed EVA workflows on atmospheric bodies:

- prevent the initial background EVA child from seeding as `Atmospheric` during the
  pre-switch race window
- keep near-ground EVA motion in `SurfaceStationary` / `SurfaceMobile` while it remains
  ground-adjacent in background recording
- leave genuine in-flight EVA behavior unchanged
- avoid any optimizer-side cleanup heuristic unless the root fix proves insufficient

## Preferred Fix

### 1. Add a narrow branch-time override for the first loaded init

Add a small scalar-only helper in `ParsekFlight` that decides whether the background
child of an EVA branch should receive a forced initial surface environment.

Suggested shape:

```csharp
internal static SegmentEnvironment? ResolveEvaBackgroundInitialEnvironmentOverride(
    BranchPointType branchType,
    bool activeChildIsEva,
    int activeChildSituation,
    bool backgroundChildIsEva,
    uint backgroundChildPid,
    uint evaVesselPid,
    double backgroundChildSurfaceSpeed)
```

Return a surface override only when all of these are true:

- `branchType == BranchPointType.EVA`
- `backgroundChildPid == evaVesselPid`
- `backgroundChildIsEva`
- `!activeChildIsEva`
- `activeChildSituation` is `LANDED`, `SPLASHED`, or `PRELAUNCH`

Choose the specific surface state from the EVA vessel's own speed:

- `backgroundChildSurfaceSpeed > 0.1` -> `SurfaceMobile`
- otherwise -> `SurfaceStationary`

If any guard fails, return `null`.

Why this scope is right:

- it only affects the exact race window proven by the log
- it does not touch true in-flight EVA from flying vessels
- it does not change the classifier used by normal active recording or non-EVA backgrounding

### 2. Persist that override until the first loaded initialization consumes it

Do not rely on `CreateSplitBranch -> OnVesselBackgrounded -> InitializeLoadedState`
being a single uninterrupted path. `OnVesselBackgrounded` can fall back to minimal
state if the vessel is not found yet, and the first real loaded init can happen later
via `OnBackgroundVesselGoOffRails`.

Instead, add a PID-keyed pending override in `BackgroundRecorder`:

- `SetPendingInitialEnvironmentOverride(uint pid, SegmentEnvironment env)`
- consume-and-clear on the first loaded-state initialization for that PID

Suggested plumbing:

- `ParsekFlight.CreateSplitBranch` decides whether the EVA child needs the override
- if yes, it registers the override with `BackgroundRecorder` before/alongside
  backgrounding the child
- both `OnVesselBackgrounded(...loaded...)` and `OnBackgroundVesselGoOffRails(...)`
  consult the same pending override store when they reach `InitializeLoadedState`

Suggested API shape:

```csharp
internal void SetPendingInitialEnvironmentOverride(
    uint vesselPid,
    SegmentEnvironment env)
```

Inside `InitializeLoadedState`, use the override only for the first section:

- if `initialEnvOverride.HasValue`, initialize `EnvironmentHysteresis` and the first
  `TrackSection` from that override
- otherwise keep the current `ClassifyBackgroundEnvironment(...)` path

Add an explicit log so future playtests can confirm the override fired:

```csharp
Loaded state initialized: pid=... initialEnv=SurfaceStationary source=eva-surface-override
```

### 3. Extend EVA near-ground classification on atmospheric bodies

The later `UT=98.68` split proves there is still a second defect after promotion:
background EVA movement near the ground on Kerbin can flip from `SurfaceMobile` to
`Atmospheric`.

The current near-surface override in `EnvironmentDetector.Classify(...)` only applies
to airless bodies. Add an EVA-specific near-ground override that works on atmospheric
bodies too, but only when the vessel is ground-adjacent.

Preferred shape:

- extend the pure classifier inputs so callers can supply:
  - `isEva`
  - `heightFromTerrain`
  - `heightFromTerrainValid`
- keep the override narrow:
  - only for EVA
  - only when `heightFromTerrain` is valid
  - only below a small threshold such as `NearGroundEvaMeters`
  - still return `SurfaceMobile` vs `SurfaceStationary` from `srfSpeed`

This should be implemented in the pure classifier layer, not as a special case buried
inside background recorder update logic, so active and background EVA recording stay
consistent.

### 4. Leave promotion and optimizer logic unchanged initially

Do not add an optimizer-side band-aid in the first fix.

If both creation-time defects are fixed, then:

- the 1-point atmospheric EVA stub is never produced
- the saved child recording no longer picks up the later fake atmospheric section
- the optimizer no longer sees a false split boundary for the landed EVA chain

This keeps the fix at the point where the bad data is created, not where it is later
consumed.

## Explicit Non-Goals

### Do not broaden `EnvironmentDetector.Classify` for non-EVA vessels

Avoid adding a general atmospheric-body "near ground means surface" override for all
vessel types.

Why not:

- it would affect rockets, planes, and other non-EVA cases
- it risks misclassifying genuine low-altitude flight / descent / jetpack movement
- the evidence points to EVA-specific ground-adjacent behavior, not a generic
  atmospheric-body issue

### Do not add optimizer special-casing first

Avoid logic like "ignore a 1-point atmospheric section before a surface EVA segment" as
the primary fix.

Why not:

- it hides bad data after the fact
- it leaves the recording itself polluted
- it would not help any other consumer of `TrackSections`

## Target Files

- `Source/Parsek/ParsekFlight.cs`
- `Source/Parsek/BackgroundRecorder.cs`
- `Source/Parsek/EnvironmentDetector.cs`
- `Source/Parsek.Tests/SplitEventDetectionTests.cs`
- `Source/Parsek.Tests/BackgroundTrackSectionTests.cs`
- `Source/Parsek.Tests/EnvironmentDetectorTests.cs`

Potentially:

- `Source/Parsek.Tests/RecordingOptimizerTests.cs` for one downstream regression built
  from the exact log shape

## Test Plan

### 1. Pure helper tests for the branch-time override decision

Add tests in `SplitEventDetectionTests.cs` for the new scalar-only
`ResolveEvaBackgroundInitialEnvironmentOverride(...)` helper:

- landed ship + background EVA + low speed -> `SurfaceStationary`
- landed ship + background EVA + moving EVA -> `SurfaceMobile`
- flying ship + background EVA -> `null`
- EVA already active (ship is background) -> `null`
- non-EVA branch type -> `null`

These tests lock down the intended scope so the override cannot silently expand later.

### 2. Background override persistence / consumption tests

Add tests in `BackgroundTrackSectionTests.cs` for the pending override store:

- registering an override for a PID causes the first loaded init to use that env
- the override is consumed exactly once
- missing overrides preserve the current classification path unchanged
- delayed loaded init paths still see the override

This addresses the timing hole where `OnVesselBackgrounded` may not be the hook that
first sees the loaded vessel.

### 3. Atmospheric-body EVA near-ground classification tests

Add tests in `EnvironmentDetectorTests.cs` for the second defect:

- atmospheric-body EVA, `FLYING`, near ground -> `SurfaceStationary`
- atmospheric-body EVA, `FLYING`, near ground and moving -> `SurfaceMobile`
- atmospheric-body non-EVA, same inputs -> still `Atmospheric`
- EVA above the near-ground threshold -> `Atmospheric`

These tests keep the override narrow and prove we are not broadening normal
atmospheric classification.

### 4. Regression test for the exact log shape

Add one focused downstream regression that models the saved section sequence from the
evidence log:

- initial short atmospheric stub
- later surface -> atmospheric -> surface flip around `UT=98.68`
- optimizer split candidate produced from that shape

After the fix, that log-shaped sequence should no longer be constructible from the
intended landed-EVA classification path.

This can live in `RecordingOptimizerTests.cs` if the fixture is easiest there.

## Manual Verification

After implementation, replay the `s10` scenario or an equivalent landed-EVA test and
confirm:

- `DeferredEvaBranch: ship still active` can still occur without harm
- the background EVA child initializes as `SurfaceStationary` / `SurfaceMobile`
- no `TrackSection started: env=Atmospheric` appears for the landed EVA child
- the first EVA branch no longer saves as a 1-point non-leaf atmospheric stub
- later near-ground background EVA motion no longer flips to `Atmospheric`
- the second EVA branch no longer produces the false optimizer split at `UT=98.68`

## Rollout Notes

This should be a small, local fix. The safest order is:

1. add the pure branch-decision helper and its tests
2. add the pending PID override store and its tests
3. extend EVA near-ground classification for atmospheric bodies
4. add the downstream regression test for the log shape
5. run the relevant test subset
6. only then update `docs/dev/todo-and-known-bugs.md`
