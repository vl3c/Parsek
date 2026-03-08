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

- `ModuleAeroSurface` (`airbrake1`) is now recorded and showcased.
- `ModuleRobotArmScanner` (`RobotArmScanner_S1/S2/S3`) is now recorded and showcased.
- `ModuleControlSurface` (24 stock/DLC control-surface parts) is now recorded and showcased.
- `ModuleAnimateHeat` (13 stock thermal-animation parts) is now recorded and showcased for hot/cold endpoint transitions.
- Dynamic wheel/leg motion (`ModuleWheelSuspension`, `ModuleWheelSteering`, `ModuleWheelMotor`, `ModuleWheelMotorSteering`) is now recorded and showcased for:
  - `GearFixed`
  - `GearFree`
  - `roverWheel1`
  - `roverWheel2`
  - `roverWheel3`
  - `wheelMed`

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
