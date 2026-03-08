# Part Event Showcase Plan (Modern Shape, Loop-First)

Date: 2026-02-17

## Objective
Build a synthetic "Part Showcase" category for visual QA of ghost playback from the launch pad:
- Row of simple vessels 100-200m in front of the pad.
- One part-under-test per vessel.
- Event cadence every 3s for high readability.
- First deliverable: lights on/off proof of concept that can be watched continuously in-game.

## Decision Update
Implement loop playback support first for showcase recordings, then build lights on top.

Rationale:
- Avoid waiting for end-of-recording vessel spawn.
- Keep observer workflow simple (stand on pad and watch cycles).
- Create reusable infrastructure for all later part-event showcases.

## Current Constraints (Confirmed)
- Part events are emitted via `RecordingBuilder.AddPartEvent(...)`.
- Light playback is already applied through `PartEventType.LightOn/LightOff`.
- Showcase recordings can be ghost-only (`GhostVisualSnapshot`) with no vessel spawn snapshot.
- Existing injector path is `InjectAllRecordings`; sidecar `.prec` and `.craft` files are written via `ScenarioWriter.WithV3Format()`.

## Implementation Phases

### Phase 0: Loop Infrastructure
Goal: add opt-in loop behavior per recording, without changing default timeline behavior.

Tasks:
1. Add a recording flag `LoopPlayback` to `RecordingStore.Recording`.
2. Persist `LoopPlayback` in scenario metadata save/load.
3. Add fluent builder support so synthetic test builders can mark looped recordings explicitly.
4. Update timeline playback logic:
   - If `LoopPlayback` is false: keep current behavior unchanged.
   - If `LoopPlayback` is true: do not enter spawn path at end.
   - Compute loop-local UT: `loopUT = StartUT + ((currentUT - StartUT) % duration)`.
   - Re-run interpolation and part-event application against `loopUT`.
5. Handle cycle boundary cleanly:
   - On wrap, reset playback indices (`playbackIndex`, `partEventIndex`).
   - Reset visual state robustly by despawn+respawn ghost once per wrap.
6. Prevent repeated economy effects:
   - Skip `ApplyResourceDeltas` for looped recordings.
7. Add/extend tests:
   - Metadata round-trip for `LoopPlayback`.
   - Playback helper tests for loop UT and wrap detection.
   - Regression assertion that non-loop recordings still spawn normally.

Acceptance:
- Looped recording never spawns vessel.
- Visuals and part events repeat indefinitely.
- Existing recordings are behavior-identical.

### Phase 1: Lights Proof of Concept
Goal: first in-game visible showcase using loop mode.

Recording design:
- Name: `Part Showcase - Lights v1`.
- Category: binary.
- Style: ghost-only (no `VesselSnapshot`), loop enabled.
- Duration: 24s.
- Pre-roll: 3s.
- Cadence: 3s transitions.

Vessel template:
- Root: `probeCoreSphere`.
- Part under test: `spotLight1` (stock part with `ModuleLight`).
- Deterministic part IDs from builder:
  - root PID = `100000`
  - light PID = `101111` (event target)

Trajectory/layout:
- Static landed points at KSC coordinates.
- Place first vessel around 140m in front of pad.
- Keep altitude at ground level and fixed rotation (`KscRot*`) for readability.

Light event schedule (example):
- `t+3 LightOn`
- `t+6 LightOff`
- `t+9 LightOn`
- `t+12 LightOff`
- `t+15 LightOn`
- `t+18 LightOff`
- `t+21 LightOn`
- `t+24 LightOff`

Injection path:
- Add showcase builder into a manual injection test path.
- Prefer separate showcase injector method to avoid unrelated ghost noise while validating.

Acceptance:
- From pad, ghost appears at expected position.
- Light toggles exactly every 3s.
- At 24s boundary, cycle restarts with no spawn and no visible glitch.

### Phase 2: Row Expansion for Lights
Goal: prove the "line of vessels" concept before other part types.

Tasks:
1. Expand to 3-5 light vessels in one row.
2. Spacing: 20m between vessels.
3. Keep one vessel per part-under-test.
4. Optionally phase-shift each vessel by +1.5s for easier left-to-right scanning.
5. Validate no overlap and good pad visibility.

Acceptance:
- Clear, stable row visible from pad.
- Each vessel independently cycles on cadence.

## Rollout Order After Lights (Easiest -> Hardest)
1. Deployables (`DeployableExtended`/`DeployableRetracted`)
2. Gear (`GearDeployed`/`GearRetracted`)
3. Cargo bays (`CargoBayOpened`/`CargoBayClosed`)
4. Engines binary (`EngineIgnited`/`EngineShutdown`)
5. RCS binary (`RCSActivated`/`RCSStopped`)
6. Engine analog (`EngineThrottle`)
7. RCS analog (`RCSThrottle`)
8. Fairings (`FairingJettisoned`)
9. Decouple (`Decoupled`)
10. Destroy (`Destroyed`)

## Out of Scope (This Wave)
- `Docked` and `Undocked` (enum exists, no ghost visual application path yet).
- Parachute/shroud showcase until binary/analog loop framework is stable.

## Risks and Mitigations
- Risk: loop wrap leaves stale part state.
  - Mitigation: wrap-triggered ghost rebuild.
- Risk: loops repeatedly apply resources.
  - Mitigation: bypass resource replay for looped recordings.
- Risk: interaction with chain/spawn logic.
  - Mitigation: loop behavior opt-in and guarded early in spawn branch.

## Definition of Done
- Loop infrastructure merged with tests.
- `Part Showcase - Lights v1` injectable and visually stable in-game.
- Observer can stand on pad and watch continuous light cycling with no vessel spawn.
