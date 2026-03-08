# Part Event Recording Analysis

**Date:** February 2026
**Purpose:** Exhaustive audit of what events we currently record vs what we should record to make ghost playback visually accurate.

---

## Currently Recorded Events (9 types)

| # | Event | Enum Value | Source | Ghost Playback | Visual Effect |
|---|-------|------------|--------|----------------|---------------|
| 0 | Decoupled | 0 | `onPartJointBreak` callback | Hide part + entire subtree recursively | Staged parts disappear |
| 1 | Destroyed | 1 | `onPartDie` callback | Hide single part | Exploded parts disappear |
| 2 | ParachuteDeployed | 2 | Polled every physics frame (`ModuleParachute.deploymentState`) | Scale canopy to deployed size (real mesh or fake sphere fallback) | Canopy opens |
| 3 | ParachuteCut | 3 | Polled every physics frame | Scale canopy to zero, hide cap | Canopy disappears |
| 4 | ShroudJettisoned | 4 | Polled every physics frame (`ModuleJettison.isJettisoned`) | Hide jettison transform | Engine shroud disappears |
| 5 | EngineIgnited | 5 | Polled every physics frame (`ModuleEngines`) | Start particle FX emission | Flames/smoke appear |
| 6 | EngineShutdown | 6 | Polled every physics frame | Stop particle FX emission | Flames/smoke stop |
| 7 | EngineThrottle | 7 | Polled every physics frame (>1% delta) | Adjust emission rate/speed | Flame intensity changes |
| 8 | ParachuteDestroyed | 8 | Derived from `onPartDie` + deployed tracking | Clean up canopy visuals, hide part | Chute ripped off by aero |

### Implicitly Recorded (not PartEvents)

- **Trajectory** — position/rotation/velocity via adaptive sampling every physics frame
- **Orbital segments** — Keplerian elements captured on `onVesselGoOnRails`
- **Resource deltas** — funds/science/reputation sampled per trajectory point
- **Vessel snapshot** — periodic backup for spawn/ghost building
- **SOI changes** — orbit segment boundaries with body name transitions

---

## Events We Should Add

### Tier 1 — High visual impact, common, feasible

#### 1. Deployable Parts (Solar / Antenna / Radiator)

**Modules:** `ModuleDeployableSolarPanel`, `ModuleDeployableAntenna`, `ModuleDeployableRadiator` — all inherit `ModuleDeployablePart`

**Why:** Very common on orbital vessels. Solar panels unfolding is one of the most recognizable visual changes in spaceflight. Antennas and radiators share the same base class, so one implementation covers all three.

**Record method:** Poll `deployState` every physics frame (same pattern as parachutes). States: `RETRACTED`, `EXTENDING`, `EXTENDED`, `RETRACTING`, `BROKEN`. Record transitions to EXTENDED and RETRACTED.

**Ghost playback:** Sample the deploy animation from the prefab (same technique as parachute canopy — clone model, seek animation to end frame, read transform state). Or simpler: just toggle visibility of the deployable transform between stowed/deployed states.

**New enum values:** `DeployableExtended`, `DeployableRetracted`, `DeployableBroken`

**Effort:** Medium. Shared base class means one polling function covers solar panels, antennas, and radiators.

#### 2. Landing Gear Deploy/Retract

**Module:** `ModuleWheelDeployment` (or `ModuleWheels.ModuleWheelDeployment` in newer KSP)

**Why:** Very common on aircraft and spaceplanes. Landing gear extending/retracting is a large, obvious visual change.

**Record method:** Poll deployment state (has `stateString` for current position). Record deploy/retract transitions.

**Ghost playback:** Animation-based — sample the deploy animation like parachutes. Gear models have distinct stowed and deployed positions.

**New enum values:** `GearDeployed`, `GearRetracted`

**Effort:** Medium. Similar animation sampling as parachutes but different module type.

#### 3. Procedural Fairing Jettison

**Module:** `ModuleProceduralFairing`

**Why:** Very common on orbital rockets. Fairing separation is a dramatic visual event. We currently handle `ModuleJettison` (engine shrouds) but NOT procedural fairings.

**Current gap:** Fairing panels are procedurally generated at runtime (not from prefab meshes), so the ghost builder can't create them from part prefabs. When fairings jettison, the panels become separate parts that die — we'd catch the `Destroyed` events, but there's no visual to hide because the ghost never had fairing geometry.

**Possible approaches:**
- (a) At ghost build time, detect parts with `ModuleProceduralFairing` and flag them. The fairing itself (base ring) stays, but child parts hidden by the fairing could be revealed on jettison. Low visual fidelity but simple.
- (b) Record the fairing panel mesh data at record time and serialize it. High fidelity but complex serialization.
- (c) Accept that procedural fairings won't show on ghosts but record the jettison event for logging/stats purposes.

**New enum value:** `FairingJettisoned`

**Effort:** Hard (for visual replay). Easy (for event-only recording).

#### 4. Cargo Bay / Service Bay Open/Close

**Module:** `ModuleCargoBay` (references `ModuleAnimateGeneric` for the door animation)

**Why:** Service bays (1.25m, 2.5m) and Mk3 cargo bays have large door animations. Visually dramatic, especially on space stations and cargo planes.

**Record method:** Poll `ModuleAnimateGeneric.animTime` or `ModuleAnimateGeneric.Events` for open/close state changes.

**Ghost playback:** Requires cloning and playing the animation on the ghost. `ModuleAnimateGeneric` uses Unity `Animation` clips which could be sampled from the prefab.

**New enum values:** `CargoBayOpened`, `CargoBayClosed`

**Effort:** Medium-Hard. Solving `ModuleAnimateGeneric` animation replay unlocks many other animated parts too.

### Tier 2 — Moderate visual impact, situationally useful

#### 5. Lights On/Off

**Module:** `ModuleLight`, `ModuleColoredLensLight`

**Why:** Subtle but atmospheric, especially during night launches, Mun landings, or docking. Simple bool toggle.

**Record method:** Poll `isOn` boolean each physics frame.

**Ghost playback:** Toggle emissive material property on the light's transform. Could also enable/disable a point light on the ghost for actual illumination.

**New enum values:** `LightOn`, `LightOff`

**Effort:** Low. Simple state toggle, minimal ghost integration.

#### 6. Airbrake Deploy

**Module:** Uses `ModuleAnimateGeneric` (same as cargo bays)

**Why:** Airbrakes have a visible deploy animation. Less common (spaceplanes only).

**Ghost playback:** Same animation technique as cargo bays — solving cargo bays automatically solves airbrakes.

**New enum values:** Could reuse a generic `AnimationToggled` event or add `AirbrakeDeployed`/`AirbrakeRetracted`.

**Effort:** Low (if cargo bay animation is already implemented).

#### 7. RCS Thruster Fire

**Module:** `ModuleRCSFX`

**Why:** Small visual jets from attitude thrusters. Adds realism to docking and maneuvering replays.

**Record method:** Poll `thrusterFX` or `thrustForce` each physics frame. High frequency — would need aggressive throttling (only record on/off transitions, or periodic snapshots).

**Ghost playback:** Clone particle FX from RCS prefab (similar technique to engine FX). Simpler than engines since RCS has no throttle curve — just on/off.

**New enum values:** `RCSFiring`, `RCSStopped`

**Effort:** Medium-High. Many RCS thrusters per vessel, high event frequency, diminishing visual returns.

### Tier 3 — Low impact, high complexity, or niche

#### 8. Control Surface Deflection

**Module:** `ModuleControlSurface`

**Why:** Ailerons, elevons, canards deflect continuously during flight.

**Verdict: Skip.** Extremely high frequency (continuous float per surface per frame). Would massively bloat event list with minimal ghost visual benefit. Deflection angles are small and hard to notice on a ghost.

#### 9. Docking/Undocking

**Module:** `ModuleDockingNode`

**Why:** Vessel topology changes dramatically.

**Verdict: Defer to Phase 3+.** Undocking could be treated like Decoupled (hide departing subtree). Docking requires building a combined ghost — too complex for now. Would also need multi-vessel recording support.

#### 10. Robotics (Breaking Ground DLC)

**Modules:** `ModuleRoboticServoHinge`, `ModuleRoboticServoPiston`, `ModuleRoboticServoRotor`

**Verdict: Defer.** DLC-dependent, continuous motion, complex to serialize and replay. Very niche use case.

#### 11. Science Experiment Deploy

**Module:** `ModuleScienceExperiment`

**Verdict: Skip.** Some have visible animations (Mystery Goo opens) but the visual change is subtle and inconsistent across experiments.

#### 12. Ladder Deploy/Retract

**Module:** `RetractableLadder`

**Verdict: Skip.** Small visual change, rare mid-flight toggle, low priority.

#### 13. Intake Air Open/Close

**Module:** `ModuleResourceIntake`

**Verdict: Skip.** Some intakes have open/close animations but the visual difference is minimal.

#### 14. Wheel Steering/Suspension

**Modules:** `ModuleWheelSteering`, `ModuleWheelSuspension`

**Verdict: Skip.** Continuous high-frequency per-wheel animation. Only relevant for rovers. Too complex for ghost replay with minimal benefit.

---

## Implementation Strategy

### Shared Base Class Advantage

`ModuleDeployableSolarPanel`, `ModuleDeployableAntenna`, and `ModuleDeployableRadiator` all inherit from `ModuleDeployablePart`. This means:
- One polling function (`CheckDeployableState`) covers all three part types
- One `DeployableGhostInfo` class for ghost tracking
- Animation sampling technique from parachutes can be reused
- Three useful events for the cost of one implementation

### Ghost Animation Sampling Pattern

For animation-based events (deployables, landing gear, cargo bays), the existing parachute canopy technique can be generalized:

1. At ghost build time, clone the part's model subtree into a temp object
2. Find the `Animation` component, seek to the deployed/retracted keyframe
3. Sample the transform states (position, rotation, scale)
4. Store the stowed and deployed transform states in a `GhostInfo` struct
5. During playback, snap transforms to the appropriate state on event

This is already proven to work for `ModuleParachute` canopy deployment.

### Event Frequency Considerations

| Event Type | Frequency | Storage Impact |
|------------|-----------|----------------|
| Deployable extend/retract | ~1-2 per flight | Negligible |
| Landing gear deploy/retract | ~1-2 per flight | Negligible |
| Cargo bay open/close | ~1-4 per flight | Negligible |
| Light toggle | ~2-10 per flight | Negligible |
| Fairing jettison | ~1 per flight | Negligible |
| RCS fire | Hundreds per flight | Could bloat .prec files |
| Control surfaces | Thousands per flight | Unacceptable |

Low-frequency events (Tier 1-2) add minimal storage overhead. RCS and control surfaces would need special handling or should be skipped.

---

## Recommended Implementation Order

| Priority | Event | Effort | Visual Impact | Covers |
|----------|-------|--------|---------------|--------|
| 1 | Deployable parts (solar/antenna/radiator) | Medium | High | 3 event types from 1 implementation |
| 2 | Landing gear deploy/retract | Medium | High | Aircraft/spaceplane replays |
| 3 | Lights on/off | Low | Moderate | Atmosphere in dark replays |
| 4 | Cargo bay open/close | Medium-Hard | High | Also unlocks airbrakes and other `ModuleAnimateGeneric` parts |
| 5 | Procedural fairing jettison | Hard | High | Orbital rocket replays |
| 6 | RCS thruster fire | Medium-High | Low-Moderate | Docking/maneuver replays |
