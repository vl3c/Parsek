# Phase 11.5: Ghost LOD Implementation Plan

## Status

Completed on `fix/phase-11-5-lod-culling`.

Foundation landed first:

- `4718cda` `Centralize ghost distance thresholds`
- `b495fe0` `Apply watch cutoff uniformly`

Implementation then landed in these logical units:

- `d075ab2` `Remove ghost soft-cap settings knobs`
- `8e24266` `Keep watched ghosts at full fidelity inside cutoff`
- `51457c5` `Add distance-based unwatched ghost LOD tiers`
- `1fee511` `Remove ghost soft-cap system`
- `ffd91e6` `Report live ghost LOD tiers in diagnostics`
- follow-up review fix commit(s) to harden exact watched-state matching, logical loop distance, and diagnostics counting

This document now serves as the implementation record for the shipped Phase 11.5 ghost LOD behavior.

---

## Locked Decisions

These are already decided and should not be reopened during implementation:

- Distance thresholds stay internal. Do not expose new LOD tuning settings.
- `ghostCameraCutoffKm` applies uniformly to all ghosts.
- A watched ghost that is inside the watch cutoff renders at full fidelity.
- Distance-based LOD never degrades a watched ghost inside cutoff.
- For watched ghosts, do not suppress part-event playback, audio, mesh, or FX due to distance.
- For unwatched ghosts, distance-based LOD may suppress part events and visuals by tier.
- KSC keeps separate behavior from Flight. Do not try to unify KSC culling with Flight LOD.
- Remove the performance soft-cap knobs from settings UI, and do not keep the old soft-cap subsystem alive in parallel with distance LOD.

---

## Target Runtime Behavior

### Flight Scene — Unwatched Ghosts

| Distance from active vessel | Tier | Behavior |
|---|---|---|
| `0 - 2300 m` | Full | Current full-fidelity playback: mesh, positioning, part events, audio, FX |
| `2300 m - 50000 m` | Reduced | Keep ghost mesh and positioning, but suppress expensive detail |
| `50000 m - 120000 m` | Hidden mesh | Hide ghost mesh, keep logical playback/orbit-line/map presence |
| `120000 m+` | Beyond | Keep current logical-only / no-mesh behavior |

### Flight Scene — Watched Ghosts

If a ghost is actively watched and is still within `ghostCameraCutoffKm`, force full fidelity
regardless of the unwatched distance tier.

That means restoring:

- full mesh visibility
- full part-event playback
- ghost audio
- engine / RCS / reentry FX
- any renderer reductions applied by distance LOD

This watched override is for distance-based LOD only. Existing non-distance suppression
paths such as pause handling or unrelated warp-specific policies should remain unchanged
unless they are proven to conflict with watch mode.

Implementation constraint: this override must be applied in the zone-result pipeline itself
(`shouldHideMesh`, `shouldSkipPartEvents`, audio/FX suppression, fidelity restore), not just
in watch-mode entry/exit code. Otherwise watched ghosts beyond `120 km` can still lose part
events or other runtime detail while remaining watched.

### Part Events Covered By “Full”

“Full part events” means the normal `PartEvent` playback handled in `GhostPlaybackLogic`,
including:

- decouple / destroy
- parachute and fairing events
- engine ignite / throttle / shutdown
- RCS activate / throttle / stop
- deployables, lights, gear, cargo bays
- robotics motion / position sampling
- inventory placed / removed

For unwatched reduced tiers, these are eligible to be skipped by distance policy.

---

## Settings Surface After Cleanup

Keep in the `Ghosts` settings section:

- `Ghost audio`
- `Camera cutoff`

Remove from the user-facing settings surface:

- `Enable soft caps`
- `Zone 1 reduce`
- `Zone 1 despawn`
- `Zone 2 simplify`

Backend policy after cleanup:

- The old ghost soft-cap subsystem is removed for this pass.
- Flight ghost degradation is owned by the distance LOD path only.
- Remaining optimization work is deferred to separate follow-ups (`T7`, `T8`) instead of keeping two overlapping runtime policies.

---

## Implementation Slices

Each slice should land as its own logical commit.

### Slice 1: Remove Soft-Cap Settings Knobs

Goal: remove user tuning for ghost performance behavior.

Files:

- `Source/Parsek/ParsekSettings.cs`
- `Source/Parsek/UI/SettingsWindowUI.cs`
- `Source/Parsek/ParsekFlight.cs`
- `Source/Parsek/GhostSoftCapManager.cs`
- related tests under `Source/Parsek.Tests`

Changes:

- Remove `ghostCapEnabled`, `ghostCapZone1Reduce`, `ghostCapZone1Despawn`, `ghostCapZone2Simplify`
  from `ParsekSettings`.
- Remove the Ghosts-section soft-cap controls and defaults-reset plumbing from `SettingsWindowUI`.
- Replace the `ParsekFlight` scene-load settings sync with a single internal
  `GhostSoftCapManager` bootstrap call such as `ApplyAutomaticDefaults()`.
- Keep the soft-cap backend alive, but internalize the defaults and enabling policy.
- Rewrite tests that currently assert settings-to-soft-cap wiring.
- Add explicit coverage that `OnFlightReady` (or its extracted bootstrap helper) still applies
  internal defaults after the settings fields are removed.

Expected commit shape:

- “Remove ghost soft-cap settings knobs”

### Slice 2: Watched-Ghost Full-Fidelity Override

Goal: watched ghosts inside cutoff must bypass distance LOD.

Files:

- `Source/Parsek/ParsekFlight.cs`
- `Source/Parsek/GhostPlaybackLogic.cs`
- `Source/Parsek/GhostPlaybackEngine.cs`
- `Source/Parsek/WatchModeController.cs`
- targeted tests

Changes:

- Define a single watched-ghost distance override path in zone policy.
- If watched and within cutoff:
  - do not hide the mesh
  - do not skip part events
  - restore fidelity reductions
  - unmute audio if distance LOD muted it
  - keep engine / RCS / reentry visual playback active
- Make the override explicit in the zone-result path rather than scattered through ad hoc exemptions.
- Retire or supersede `ShouldHideGhostForZone` / `ShouldExitWatchModeForZone` so the old
  zone-only watch policy cannot diverge from the cutoff-based policy again.

Expected commit shape:

- “Keep watched ghosts at full fidelity inside cutoff”

### Slice 3: Unwatched Reduced Tier (`2300 m - 50000 m`)

Goal: make the near-far tier cheaper without hiding the ghost completely.

Behavior:

- keep ghost mesh visible
- keep positioning / transform updates
- skip part events
- mute ghost audio
- stop engine / RCS / reentry FX
- apply renderer reduction (`ReduceGhostFidelity`)

Implementation notes:

- Reuse existing `ReduceGhostFidelity`, `RestoreGhostFidelity`, `MuteAllAudio`,
  `StopAllEngineFx`, `StopAllRcsFx`, and zone-policy hooks instead of adding parallel systems.
- The reduced tier should be deterministic by distance, not by current ghost count.

Expected commit shape:

- “Add reduced unwatched ghost LOD tier”

### Slice 4: Unwatched Hidden-Mesh Tier (`50000 m - 120000 m`)

Goal: get the large visual win without changing timeline semantics.

Behavior:

- hide ghost mesh
- keep logical playback advancing
- keep map presence / orbit-line visibility
- skip part events
- keep audio and FX off

Implementation notes:

- This tier is mesh-hidden, not despawned.
- Watch mode remains allowed to override this if inside cutoff.
- Preserve current `Beyond` semantics for `120000 m+`.

Expected commit shape:

- “Hide unwatched ghost meshes beyond reduced tier”

### Slice 5: Diagnostics, Stress Tests, and Final Cleanup

Goal: make the new policy observable and safe to tune later.

Changes:

- Extend diagnostics/reporting with counts for:
  - full ghosts
  - reduced ghosts
  - hidden-mesh ghosts
  - watched full-fidelity overrides
- Add focused tests for threshold transitions and watched overrides.
- Run synthetic stress scenarios to compare counts and action distribution.
- Update roadmap / plan docs if implementation meaningfully changes the old “configurable soft caps” wording.

Expected commit shape:

- “Add diagnostics for ghost LOD policy”

---

## Code-Level Control Points

Primary files to touch during implementation:

- `Source/Parsek/DistanceThresholds.cs`
- `Source/Parsek/RenderingZoneManager.cs`
- `Source/Parsek/ParsekFlight.cs`
- `Source/Parsek/GhostPlaybackLogic.cs`
- `Source/Parsek/GhostPlaybackEngine.cs`
- `Source/Parsek/WatchModeController.cs`
- `Source/Parsek/GhostSoftCapManager.cs`
- `Source/Parsek/UI/SettingsWindowUI.cs`
- `Source/Parsek/ParsekSettings.cs`

Supporting docs:

- `docs/dev/plans/phase-11-5-distance-threshold-centralization.md`
- `docs/dev/todo-and-known-bugs.md`
- `docs/roadmap.md`

---

## Test Plan

Minimum targeted verification after each slice:

- `WatchCutoffTests`
- `RenderingZoneTests`
- `ZoneRenderingTests`
- `AnchorDetectorTests`
- `BugFixTests`
- `Bug156Tests`
- `DistanceThresholdsTests`
- `GhostSoftCapTests`
- `SoftCapWiringTests` or their replacement coverage after settings removal

Additional tests to add:

- watched ghost inside cutoff ignores reduced tier
- watched ghost inside cutoff ignores hidden-mesh tier
- watched ghost beyond `120 km` but inside cutoff still keeps part events
- unwatched ghost in reduced tier skips part events
- unwatched ghost in reduced tier mutes audio and stops FX
- unwatched ghost in hidden-mesh tier hides mesh but keeps logical playback
- settings defaults/reset path no longer references removed soft-cap fields
- internal soft-cap bootstrap path applies defaults without `ParsekSettings`
- exact-boundary transition tests at `2300`, `50000`, `120000`, and watch cutoff

---

## Non-Goals For This Pass

Do not mix these into the LOD pass:

- particle pooling (`T8`)
- ghost mesh unload / rebuild lifecycle refactor (`T7`)
- KSC culling redesign
- new public settings for LOD thresholds

---

## Primary Risk Areas

- Watch mode can easily regress if distance LOD and camera-follow exemptions diverge again.
- If the watched override is applied only to mesh hide and not to the full zone-result state,
  watched ghosts can remain visible while silently losing part events or FX.
- Mesh-hidden ghosts must not accidentally break map presence, loop playback, or chain/watch transfer.
- Reduced-tier suppression must not leave stale FX or muted-state leakage when the ghost returns to full.
- Removing settings fields will break tests and any code that still assumes persisted soft-cap tuning.

The implementation should bias toward explicit state transitions and restoration paths rather than
layering more one-off conditionals into the existing zone logic.
