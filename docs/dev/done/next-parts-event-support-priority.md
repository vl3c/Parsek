# Next Parts / Event Support Priority

Generated: 2026-02-22  
Refreshed after full Stock + official DLC module sweep: 2026-02-22

## Current Baseline

- Showcase templates now cover 160 unique part IDs.
- Inventory target set in `deployable-parts-inventory.md`: 125 part IDs.
- Inventory coverage now effectively complete for visible transform testing:
  - covered + showcased: 123
  - intentionally excluded: 2 (`ISRU`, `OrbitalScanner`) because no useful visible transform for showcase validation.
- Runtime recording/playback currently supports:
  - parachute
  - jettison/fairing
  - deployable/ladder
  - animation-group deploy
  - standalone animate-generic deploy
  - lights/blink
  - gear deployment state
  - wheel/leg dynamic modules (`ModuleWheelSuspension`, `ModuleWheelSteering`, `ModuleWheelMotor`, `ModuleWheelMotorSteering`)
  - engine + RCS visuals
  - robotics motion events
  - aero surface deploy/retract (airbrake)
  - control surface deploy/retract endpoint transitions
  - robot arm scanner deploy/retract (BG ROC scanners)
  - animate-heat hot/cold endpoint transitions (`ModuleAnimateHeat`)
  - inventory placement/removal

## Inventory Remaining (Intentional)

- `ISRU` (`ModuleAnimationGroup`)  
  - currently excluded from showcase; no meaningful visual deploy/retract change.
- `OrbitalScanner` (`ModuleAnimationGroup`)  
  - currently excluded from showcase; no meaningful visual deploy/retract change.

## New Priority List (Post-Inventory Sweep)

These are the next visual-transform systems not yet supported or not yet showcased for their full behavior.

### Completed Since Last Refresh

> **CORRECTED 2026-08-11.** The five bullets below read "now recorded and showcased" for the whole
> of 2026. That was true of the SYNTHETIC showcase path only. On real stock parts four of the five
> probes resolved nothing, because `module.Fields` is `[KSPField]`-only
> (`FlightRecorder.FindModuleField` → `module.Fields[name]`) and every probed name was absent,
> private, or a config constant. The audit
> (`docs/dev/research/part-action-recording-audit-2026-08-09.md` §3) found them; P7 fixed them. Each
> bullet now states the true post-fix position with its decompiled reason (KSP 1.12.5).

- `ModuleAeroSurface` (`airbrake1`) — **was dead on stock, fixed 2026-08-11.** The probe table
  listed `isDeployed` / `deployed` / `isExtended` / `isBraking` / …; the real field is
  `[KSPField(isPersistant = true)] public bool deploy` on `ModuleControlSurface`, which
  `ModuleAeroSurface` inherits. The type exposes no deploy/retract `[KSPEvent]` either (only
  `[KSPAction]`s, which `module.Events` never sees), so the event stage found nothing and the
  deflection stage found nothing. Now classified from `deploy`, with a veto when the commanded
  deploy angle (`aeroDeployAngle`, then `deployAngle`) is ~0 and the surface therefore does not
  visibly move.
- `ModuleRobotArmScanner` (`RobotArmScanner_S1/S2/S3`) — **genuinely recorded; the audit's
  dead-probe claim was wrong here, and nothing was changed.**
  `ModuleRobotArmScanner : ModuleDeployablePart`, whose `[KSPEvent] Extend()` / `Retract()` the
  scanner actively toggles (`Events["Extend"].active = …`), and `BaseEvent.name` is the method name,
  so the probe's event-activity stage resolves. Its own `ArmDeployState` sits behind a `new`
  property over a private unattributed `_deployState` and is unreachable by name — and an accessor
  would be redundant anyway, since that setter mirrors every arm state onto the base `deployState`
  which `CheckDeployableState` already polls.
- `ModuleControlSurface` (24 stock/DLC control-surface parts) — **was dead on stock, fixed
  2026-08-11.** Same single missing field as the airbrake above; `TryClassifyControlSurfaceState`
  delegates to the aero core.
- `ModuleAnimateHeat` (13 stock thermal-animation parts) — **was dead on stock, fixed 2026-08-11.**
  Not fixable by names at all: `ModuleAnimateHeat : ModuleAnimationSetter`, whose live scalars
  `animState` / `inputState` are plain public fields carrying no `[KSPField]`. The accessor is the
  interface property `IScalarModule.GetScalar => inputState` — the already-normalized 0..1 ratio
  `UpdateHeatEffect` writes through `SetScalar` every frame. One typed cast in the reader now feeds
  the complete, already-built `ThermalAnimationHot/Medium/Cold` recorder + playback path, so
  re-entry glow is recorded for the first time.
- Dynamic wheel/leg motion (`ModuleWheelSuspension`, `ModuleWheelSteering`, `ModuleWheelMotor`,
  `ModuleWheelMotorSteering`) — **suspension was dead on stock, fixed 2026-08-11; motor spin is a
  deliberate non-recording.** `suspensionOffset` is a plain `[KSPField]` read once in `OnStart` to
  configure the wheel collider — a config constant that never moves, and because it resolved it
  shadowed `suspensionPos`, the `[KSPField(isPersistant)] Vector3` assigned from
  `suspensionTransform.localPosition` as the wheel compresses. Dropping it lets the live vector win.
  Wheel MOTOR spin is separately and deliberately not recorded: it is derived from the ghost's own
  ground speed at playback (see `FlightRecorder.IsWheelMotorSpinModuleName`). Showcased parts:
  - `GearFixed`
  - `GearFree`
  - `roverWheel1`
  - `roverWheel2`
  - `roverWheel3`
  - `wheelMed`

Robotic servo pistons were never on this list but had the same shape and were fixed in the same
pass: `ModuleRoboticServoPiston`'s probe latched `traverseVelocity`, a `[KSPAxisField]` SPEED slider
constant for the whole stroke, shadowing the working `servoTransformPosition` fallback. The live
field is `[KSPField(guiActive)] public float currentExtension`, recomputed from the transform
geometry each frame; `targetExtension` (the persistent `[KSPAxisField]` setpoint) backs it up. The
probed `targetPosition` was `private float` with no attribute and was never resolvable at all.

### Priority 1: `ModuleControlSurface` continuous deflection values

- Parts (24):
  - `AdvancedCanard`
  - `airlinerCtrlSrf`
  - `airlinerTailFin`
  - `CanardController`
  - `elevon2`
  - `elevon3`
  - `elevon5`
  - `largeFanBlade`
  - `largeHeliBlade`
  - `largePropeller`
  - `mediumFanBlade`
  - `mediumHeliBlade`
  - `mediumPropeller`
  - `R8winglet`
  - `smallCtrlSrf`
  - `smallFanBlade`
  - `smallHeliBlade`
  - `smallPropeller`
  - `StandardCtrlSrf`
  - `tailfin`
  - `winglet3`
  - `wingShuttleElevon1`
  - `wingShuttleElevon2`
  - `wingShuttleRudder`
- Status: endpoint transitions are supported and showcased; continuous value fidelity is not.
- Why second:
  - broad coverage impact
  - requires continuous value sampling model (not just binary endpoint toggles).

### Priority 2: `ModuleAnimateHeat` continuous intensity fidelity

- Status: endpoint transitions are supported and showcased; continuous thermal intensity fidelity is not.
- Why next:
  - currently optimized for deterministic visual on/off validation in showcase loops
  - continuous heat-scalar playback would improve parity with real thermal simulation ramps.

## Suggested Next Sprint

1. Design and implement low-frequency continuous-sampling for `ModuleControlSurface` deflection values.
2. Validate `ModuleControlSurface` continuous sampling on a minimal subset (`elevon2`, `wingShuttleRudder`, `smallPropeller`) before broad rollout.
3. Add optional continuous `ModuleAnimateHeat` scalar sampling/playback (beyond current hot/cold endpoints).
