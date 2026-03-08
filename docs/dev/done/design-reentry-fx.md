# Design: Atmospheric Heating Visual Effects for Ghost Vessels

## Problem

When a ghost vessel moves through the atmosphere at high speed — during reentry, aggressive ascent, or aerobraking — it looks like a silent, cold object gliding through the sky. Real atmospheric flight at high speed produces dramatic visuals — a glowing fireball trailing flame and smoke. This is one of the most spectacular moments in spaceflight, and ghost vessels should look epic when other players on the ground see a return vehicle streaking overhead or a rocket punching through the atmosphere.

Currently, ghost vessels have engine FX (flames, smoke), RCS thruster FX, heat animations (`ModuleAnimateHeat` glow), deployable animations, lights, fairings, and parachutes. But there is no atmospheric heating visual — no plasma glow, no flame trail, no smoke wake. The ghost looks identical whether it's in vacuum or punching through the atmosphere at 2 km/s.

The effect is direction-agnostic: dynamic pressure (and therefore aerodynamic heating) depends on speed and atmospheric density, not on whether the vessel is ascending or descending. A vessel climbing at 1500 m/s through the lower atmosphere produces the same heating as one descending at 1500 m/s. Reentry is the most dramatic case because of the combination of high speed and increasing density, but the FX system triggers for any high-q atmospheric flight.

## Terminology

**Dynamic pressure (q)**: `0.5 * density * speed^2`. The primary driver of aerodynamic heating. KSP uses this internally; we compute it from recorded trajectory data at playback time.

**Reentry intensity**: A normalized 0-1 float derived from dynamic pressure and speed. Controls the visual strength of all reentry FX layers. 0 = no effect (vacuum or slow flight), 1 = maximum plasma trail (orbital reentry speeds).

**FX layers**: Independent visual components that compose the full reentry effect. Four layers, each activating at different intensity thresholds and scaling independently:
- **Layer A** — Heat glow (material emission on vessel parts)
- **Layer B** — Flame particles (short-lived, vessel-attached)
- **Layer C** — Smoke/plasma trail (long-lived, world-space particles)
- **Layer D** — Condensation trail (TrailRenderer, persistent sky streak)

## Mental Model

Reentry FX is a **playback-time visual effect** — it requires no changes to the recording format. The trajectory already contains velocity, altitude, and body reference at every sample point. At playback time, we interpolate these values, query the body's atmosphere, and compute reentry intensity. The visual components scale with intensity.

```
Recorded data (existing):
  TrajectoryPoint { velocity, altitude, bodyName }
        │
        ▼
Playback-time computation (new):
  body = lookup(bodyName)
  density = body.GetDensity(pressure, temperature)
  speed = velocity.magnitude
  q = 0.5 * density * speed^2
  intensity = ComputeReentryIntensity(q, speed)
        │
        ▼
Visual layers (new):
  Layer A: Heat glow (material emission on vessel parts)
  Layer B: Flame particles (short-lived, vessel-attached)
  Layer C: Smoke/plasma trail (long-lived, world-space particles)
  Layer D: Condensation trail (TrailRenderer, persistent sky streak)
```

The effect ramps on gradually as the vessel descends into denser atmosphere and ramps off as it slows down. A vessel in low orbit has zero effect; a vessel hitting the upper atmosphere gets a faint glow; a vessel in full reentry gets the complete fireball-with-trail treatment.

No recording format changes. No new PartEvent types. No serialization changes. No save format bump. This is purely a visual enhancement computed from existing data.

## Data Model

### New types

```
ReentryFxInfo (class, in GhostVisualBuilder.cs)
├── flameParticles: List<ParticleSystem>     — short-lived flame particles near vessel
├── smokeParticles: List<ParticleSystem>     — long-lived smoke trail in world space
├── trailRenderer: TrailRenderer             — persistent condensation/plasma trail
├── glowMaterials: List<HeatMaterialState>   — reuses existing struct from heat animation system
├── lastIntensity: float                     — previous frame intensity (for smoothing)
└── lastVelocity: Vector3                    — cached interpolated velocity from current frame
```

`ReentryFxInfo` is a class (reference type) because it holds Unity object references (ParticleSystem, TrailRenderer) and is stored as a nullable field on `GhostPlaybackState`.

**Reuses `HeatMaterialState`** (existing struct) for glow materials rather than introducing a new type. The existing struct already has the exact fields needed: `material`, `colorProperty`, `coldColor`/`hotColor`, `emissiveProperty`, `coldEmission`/`hotEmission`. The "cold" values are the original material state and the "hot" values are the reentry glow target. This struct is a value type stored in a `List<>`, which is correct for small data without heap allocation per material.

**No separate `active` flag.** Activation state is derived from `lastIntensity > 0`. Hysteresis (different on/off thresholds) is not needed — the exponential smoothing already prevents flickering at boundaries.

### No serialization

These types exist only at runtime during ghost playback. Nothing persists to save files. No ConfigNode keys. No format version bump.

### Integration into existing types

```
GhostPlaybackState (existing class in ParsekFlight.cs)
├── ... existing fields ...
└── reentryFxInfo: ReentryFxInfo   — NEW, nullable (null = no reentry FX built)
```

`reentryFxInfo` is always built for ghosts that have a vessel snapshot (glow + particles + trail) and for sphere-fallback ghosts (particles + trail only, no glow materials). It is null only if `TryBuildReentryFx` fails entirely (should not happen in practice).

## Behavior

### Obtaining interpolated velocity

The current `InterpolateAndPosition` method positions the ghost but does not output the interpolated velocity. `UpdateReentryFx` needs the velocity each frame. Two options:

**Option A (chosen): Store interpolated velocity on `GhostPlaybackState`.** Add a `lastInterpolatedVelocity` field (Vector3) to `GhostPlaybackState`. In the `InterpolateAndPosition` call path, after computing the interpolation fraction `t`, also compute `Vector3.Lerp(before.velocity, after.velocity, t)` and store it on the state. `UpdateReentryFx` reads this cached value. This avoids duplicating the interpolation logic and follows the pattern of `playbackIndex` (state cached per frame).

**Option B (alternative):** Duplicate the `FindWaypointIndex` + interpolation in `UpdateReentryFx`. Rejected — duplicates ~15 lines of index searching and adds a second binary search per frame.

### Velocity frame of reference

`TrajectoryPoint.velocity` is labeled "surface-relative" and is recorded as `rb_velocityD + Krakensbane.GetFrameVelocity()` for unpacked vessels. However, **packed vessels record `obt_velocity` (orbital velocity)** — see `FlightRecorder.cs:3590` and `BackgroundRecorder.cs:679`. This means trajectory points recorded at rail transitions store orbital velocity, not surface velocity.

For reentry FX purposes, the error is small: Kerbin's equatorial rotational velocity is ~175 m/s, which is <10% of typical reentry speeds (2000+ m/s). At the speed threshold (~400 m/s), the error could produce a false positive or false negative for a narrow speed band.

**Decision: apply uniform surface velocity conversion for all trajectory-point playback.** Subtract `body.getRFrmVel(worldPosition)` from the interpolated velocity in `UpdateReentryFx`, regardless of whether the point was packed or unpacked. This makes the calculation uniformly correct for both cases and matches the orbital segment conversion (see "Orbital segments" below). The cost is one `getRFrmVel` call per frame per ghost, which is negligible.

### Computing reentry intensity

At each playback frame, after positioning the ghost, compute reentry intensity from the interpolated trajectory state:

1. Read interpolated velocity from `state.lastInterpolatedVelocity`
2. Convert to surface velocity: `surfaceVel = interpolatedVel - body.getRFrmVel(ghost.transform.position)`
3. Compute speed: `speed = surfaceVel.magnitude`
4. Look up `CelestialBody` from `point.bodyName`
5. Check `body.atmosphere` — if false, intensity = 0 (Mun, Minmus, etc.)
6. Check `altitude < body.atmosphereDepth` — if false, intensity = 0
7. Compute atmospheric state:
   - `pressure = body.GetPressure(altitude)`
   - `temperature = body.GetTemperature(altitude)`
   - `density = body.GetDensity(pressure, temperature)`
   - **Note:** These `CelestialBody` API methods are new to this codebase. They are standard KSP APIs but need runtime verification — specifically that they return sane values when the ghost is at a different position/body than the active vessel, and during time warp. Add NaN/negative guards: if any returns NaN or negative, treat as density=0 (intensity=0).
8. Compute dynamic pressure: `q = 0.5 * density * speed * speed`
9. Map to intensity using threshold curves:
   - Below `qThresholdLow` (~500 Pa): intensity = 0
   - Between `qThresholdLow` and `qThresholdHigh` (~20000 Pa): linear ramp 0→1
   - Above `qThresholdHigh`: intensity = 1
   - Additionally require `speed > speedThresholdLow` (~400 m/s) to avoid triggering on low-altitude slow flight in thick atmosphere
10. Smooth intensity over time using frame-rate-independent exponential decay:
   `intensity = Lerp(lastIntensity, rawIntensity, 1 - Mathf.Exp(-smoothingRate * Time.deltaTime))`
   with `smoothingRate = 5.0f` (gives ~200ms response time). This formula produces the same visual result regardless of frame rate, unlike the naive `Lerp(a, b, factor * dt)` which converges faster at lower frame rates.

The thresholds are tuned for Kerbin. Other atmospheric bodies (Eve, Duna, Laythe, Jool) have different atmospheric densities — the dynamic pressure calculation naturally adapts because density varies by body. Eve's thick atmosphere produces high q at lower speeds; Duna's thin atmosphere requires higher speeds. No per-body tuning needed.

### Layer A: Heat glow (material emission)

**When:** intensity > 0.05

**What:** Lerp part material emission colors toward hot orange/white. This reuses the same visual approach as `HeatGhostInfo` (which tints materials for `ModuleAnimateHeat`) but applied to ALL renderable materials on the ghost, not just parts with heat animation modules. Both systems represent thermal effects and intentionally use the same color palette.

**How:**
- At ghost build time (`TryBuildReentryFx`): collect all `Renderer` components on the ghost, resolve emissive property names using the existing `TryGetHeatEmissiveProperty` method (which probes `_EmissiveColor`, `_EmissionColor`, `_Emissive` in order — the correct candidates for KSP's custom shaders). Build `HeatMaterialState` structs with cold=original colors, hot=reentry target colors.
- At playback time: lerp emissive color from `coldEmission` → `hotEmission` based on intensity, using `material.SetColor(emissiveProperty, lerpedColor)`. This is the same approach used by `ApplyHeatState` — directly setting the color property, with no `_EMISSION` keyword toggling (KSP shaders do not reliably support the Unity Standard `_EMISSION` keyword).
- Reentry hot emission color: `HeatEmissionColor (1.5, 0.6, 0.15, 1.0)` at low intensity, lerping toward `(2.0, 1.5, 0.8, 1.0)` at full intensity (white-hot). The low-intensity color intentionally matches the existing `HeatEmissionColor` constant because both represent the same physical phenomenon (thermal heating). Color tint uses `HeatTintColor (1.0, 0.45, 0.2, 1.0)`.
- Restore original colors when intensity drops to 0.

**Interaction with existing `HeatGhostInfo`:** Skip materials on parts that have a `HeatGhostInfo` entry. At build time, `TryBuildReentryFx` receives the set of `partPersistentId`s that have `HeatGhostInfo` entries (from the already-built `heatInfos` dictionary). When iterating ghost renderers, skip any renderer whose parent part's `persistentId` is in this set. This prevents double-tinting — parts with `ModuleAnimateHeat` (like heat shields) are managed by their own animation-driven heat system; reentry glow applies to all other parts.

**Decoupled parts:** When checking glow materials each frame, skip materials whose `renderer.gameObject.activeInHierarchy` is false (subtree was hidden by a decouple event). No per-frame iteration change — the material is still in the list, but the set-color call on a disabled renderer is a no-op visually.

### Layer B: Flame particles (vessel-attached)

**When:** intensity > 0.15

**What:** Short-lived bright particles emitted from the vessel's leading surface, simulating the plasma sheath.

**How:**
- A single `ParticleSystem` attached to the ghost root
- `simulationSpace = Local` (moves with the vessel)
- `startLifetime = 0.3-0.8s` (short-lived, stays near the vessel)
- `startColor`: orange-yellow at low intensity, white-yellow at high intensity
- `startSize`: scales with intensity (larger fireball at higher speeds)
- `emission.rateOverTime`: scales with intensity (0 at threshold, ~200 at max)
- `startSpeed`: moderate, directed backward (particles stream backward from the leading edge into the wake)
- Shader: `KSP/Particles/Additive` (stock KSP particle shader, produces plasma-like glow). Material uses a simple circular gradient texture or the stock particle sprite.
- Shape: `Cone` or `Hemisphere` aimed backward (away from velocity direction), emitting from the vessel center

**Orientation:** Each frame, set the particle system's transform rotation to face into the velocity vector: `Quaternion.LookRotation(surfaceVelocity.normalized)`. The shape module emits backward (cone opening faces away from velocity), so particles appear on the leading face and stream backward into the wake. This is the correct geometry: plasma forms on the forward-facing surface and trails behind the vessel.

**Particle `maxParticleSize`:** Set to a large value (~50-100) so particles remain visible when the camera is far from the ghost. Unity culls particles that would render smaller than `maxParticleSize` screen pixels — the default (0.5) is too aggressive for distant reentry viewing.

### Layer C: Smoke/plasma trail (world-space)

**When:** intensity > 0.3

**What:** Longer-lived particles that persist in world space after the vessel passes, creating the visible trail across the sky.

**How:**
- A `ParticleSystem` attached to the ghost root but with `simulationSpace = World` — particles stay where they were emitted as the vessel moves on
- `startLifetime = 4-10s` (long enough to form a visible trail)
- `startColor`: bright orange fading to dark gray/transparent over lifetime (color over lifetime gradient)
- `startSize`: medium, grows slightly over lifetime (expanding wake)
- `emission.rateOverTime`: scales with intensity (0 at threshold, ~80 at max)
- `startSpeed`: very low (~0.5 m/s random spread — particles should mostly stay put)
- `maxParticles`: capped at ~2000 to prevent performance issues during extended atmosphere traversal
- `gravityModifier`: very slight (0.01-0.05) so old trail particles drift downward slightly
- Shader: `KSP/Particles/Alpha Blended` (stock KSP particle shader, supports color-over-lifetime fading)
- `maxParticleSize`: set high (~50-100) for distance visibility, same reasoning as Layer B

**This is the "epic sky trail" layer** — when the player looks up from the ground, they see a glowing streak across the sky that lingers for several seconds after the vessel passes.

**Floating-origin note:** KSP uses floating-origin correction — the world origin shifts as the camera moves far from the physics bubble. `simulationSpace = World` particles store positions in world coordinates. When the floating origin shifts, these positions are NOT automatically corrected (only `Vessel` and `CelestialBody` transforms are). This could cause old trail particles to jump when the origin shifts. In practice this is unlikely to be noticeable: the trail lifetime (4-10s) is short enough that the camera rarely moves far enough to trigger an origin shift while trail particles are alive. **v1 limitation — acceptable.** If it becomes visible, a future fix could subscribe to `FloatingOrigin.TerrainOffsetUpdate` and manually offset the particle system, but this is not worth the complexity for v1.

### Layer D: Condensation/plasma trail (TrailRenderer)

**When:** intensity > 0.1

**What:** A continuous line trailing behind the vessel, thinner and more persistent than the particle trail. Visible at extreme distances where individual particles would be too small to see.

**How:**
- A `TrailRenderer` component on a child transform of the ghost root
- `time = 5s` (how long the trail persists — kept short to minimize floating-origin issues, see below)
- `startWidth = 5-20m` scaling with intensity
- `endWidth = 1-5m`
- `material`: `KSP/Particles/Additive` shader with an orange-white color
- `widthCurve`: starts wide, narrows over lifetime
- `colorGradient`: bright orange/white → transparent over lifetime
- Automatically handles trail persistence — Unity's `TrailRenderer` leaves the trail in world space as the object moves

**Distance visibility:** `TrailRenderer` is geometry-based (a mesh strip), not a particle system. It remains visible at distances where particles would be culled or too small to render. This makes it the primary visual cue when the player is far from the reentry vessel (e.g., watching from KSC as a vessel reenters over the ocean).

**Floating-origin note:** Same concern as Layer C — `TrailRenderer` stores vertices in world space, which are not automatically corrected on floating-origin shifts. The `time = 5s` keeps the trail short enough that origin shifts during the trail's lifetime are unlikely. If the trail time is increased in future tuning, floating-origin glitches become more likely. See Layer C note for potential fix.

### Layer activation and deactivation

When the ghost enters its playback range (`SpawnTimelineGhost`), the reentry FX components are created but inactive (emission = 0, trail disabled). They activate only when the computed intensity exceeds their threshold.

When the ghost exits its playback range (`DestroyTimelineGhost`), the method already destroys particle systems for engines/RCS explicitly (iterating `engineInfos`/`rcsInfos`), then destroys the ghost `GameObject`, then removes the `GhostPlaybackState` from the `ghostStates` dictionary. The reentry FX particle systems and trail renderer are children of the ghost `GameObject`, so they are destroyed automatically with it. The `reentryFxInfo` reference is cleaned up when the `GhostPlaybackState` dictionary entry is removed. No additional cleanup code needed — the existing `DestroyTimelineGhost` pattern handles everything.

When intensity drops to 0 (vessel slows below threshold or exits atmosphere):
- Flame particles: stop emission, let existing particles finish their lifetime naturally
- Smoke trail: stop emission, existing trail lingers for its full lifetime before fading
- Trail renderer: stop emitting new trail points, existing trail fades over its `time` duration
- Heat glow: restore original material colors

This gives a natural fadeout — the vessel doesn't snap from fireball to cold metal.

### Orbital segments

During orbital segment playback (`PositionGhostFromOrbit`), velocity comes from `orbit.getOrbitalVelocityAtUT()` which returns **orbital velocity** (inertial frame). For reentry computation we need **surface velocity** (atmosphere-relative). Convert:

```
surfaceVelocity = orbitalVelocity - body.getRFrmVel(worldPosition)
```

`body.getRFrmVel()` returns the body's rotational velocity at a world position. Subtracting it gives the velocity relative to the rotating atmosphere.

For trajectory-point-based playback, the same `body.getRFrmVel()` subtraction is applied uniformly (see "Velocity frame of reference" above). This handles both the common case (unpacked surface-relative velocity) and the packed case (orbital velocity) correctly.

### Interaction with existing FX

- **Engine FX**: Independent. A vessel can have engines firing during reentry (powered landing). Both engine flames and reentry flames display simultaneously. No conflict — they're separate particle systems on separate transforms.
- **RCS FX**: Independent. Same reasoning as engines.
- **Heat animation (`HeatGhostInfo`)**: Reentry glow skips materials already managed by `HeatGhostInfo` to avoid double-tinting (see Layer A above).
- **Parachute events**: A vessel deploying parachutes during reentry is realistic. No interaction — parachute visuals are mesh transforms, not particle systems.
- **Part decoupling**: When parts are decoupled during reentry (heat shield jettison), the decoupled subtree is hidden. Reentry glow materials for hidden parts are skipped (check `gameObject.activeInHierarchy`).

### Build-time integration: where `TryBuildReentryFx` is called

`TryBuildReentryFx` is called in `SpawnTimelineGhost` (in `ParsekFlight.cs`), **after** `BuildTimelineGhostFromSnapshot` returns. It is NOT inside `BuildTimelineGhostFromSnapshot` itself. Rationale:

- `BuildTimelineGhostFromSnapshot` already has a very complex signature with many `out` parameters (parachutes, engines, RCS, deployables, heat, lights, fairings, robotics). Adding another `out` parameter increases signature bloat.
- Reentry FX needs the `heatInfos` dictionary (to know which parts to skip) — this is only available after `BuildTimelineGhostFromSnapshot` returns.
- Reentry FX is not part-specific — it operates on the ghost root. This is architecturally different from per-part info classes.

The call site in `SpawnTimelineGhost`:
```
// After BuildTimelineGhostFromSnapshot and all info collections:
state.reentryFxInfo = GhostVisualBuilder.TryBuildReentryFx(
    state.ghost,
    state.heatInfos   // to exclude HeatGhostInfo-managed parts
);
```

### Preview playback (F10/F11)

The manual preview playback path (`UpdatePlayback`, triggered by F10) uses a separate ghost object and simpler positioning code. Preview ghosts do NOT get reentry FX in v1 — the preview is intended for quick review of the recording shape, not a full visual replay. The preview ghost already lacks engine FX, RCS FX, and most visual features. Adding reentry FX to preview would require duplicating the intensity computation in a second code path.

**v1 scope: timeline playback only.** Preview playback is listed in "What Doesn't Change."

## Edge Cases

### Body without atmosphere
**Scenario:** Ghost vessel approaches Mun surface at high speed.
**Expected:** No reentry FX. `body.atmosphere == false` → intensity = 0.
**Handled in v1.**

### Vessel ascending through atmosphere (launch)
**Scenario:** Ghost replaying a launch — vessel accelerating upward through atmosphere.
**Expected:** Mild FX at very high speeds (late in ascent), none during early ascent. Dynamic pressure is lower during ascent because speed is lower and the vessel is accelerating upward into thinner air. The same q-based thresholds handle this naturally — a launch at 300 m/s produces negligible q compared to a reentry at 2200 m/s.
**Handled in v1.**

### Aerobraking at Eve
**Scenario:** Ghost vessel aerobraking in Eve's thick atmosphere at moderate speed.
**Expected:** Strong FX even at moderate speeds because Eve's atmospheric density is ~5x Kerbin's. The q calculation naturally produces high values. No per-body tuning needed.
**Handled in v1.**

### Duna thin atmosphere
**Scenario:** Ghost vessel entering Duna's thin atmosphere.
**Expected:** Weaker FX than Kerbin reentry at the same speed, because Duna's density is much lower. The q threshold means the vessel needs higher speed to trigger visible effects. Realistic behavior.
**Handled in v1.**

### Jool atmosphere entry
**Scenario:** Ghost vessel entering Jool's deep atmosphere.
**Expected:** Extreme FX — Jool's atmosphere is very dense and deep. Intensity saturates at 1.0 quickly. The vessel is likely destroyed during recording, so the ghost may end abruptly (destruction part event). FX should be active until the ghost disappears.
**Handled in v1.**

### Multiple ghosts reentering simultaneously
**Scenario:** Two committed recordings have vessels reentering at overlapping UTs.
**Expected:** Each ghost has independent `ReentryFxInfo`. Independent particle systems, independent trail renderers, independent intensity calculations. No shared state. Performance consideration: 2x the particle load. Should be fine for 2-3 simultaneous ghosts; unlikely to have more.
**Handled in v1.**

### Ghost with no vessel snapshot (sphere fallback)
**Scenario:** A ghost that has no vessel snapshot renders as a green sphere.
**Expected:** Reentry FX still apply — the sphere gets heat glow and particle trail. `TryBuildReentryFx` should handle the case where there are no part materials (skip Layer A glow) but still create Layers B-D (particles and trail on the ghost root).
**Handled in v1.**

### Rapid altitude changes (suborbital bounce)
**Scenario:** Ghost vessel on a suborbital trajectory skimming the upper atmosphere — altitude oscillates around `atmosphereDepth`.
**Expected:** FX activate and deactivate as the vessel enters and exits the atmosphere. The intensity smoothing (`Lerp` with `smoothingFactor`) prevents visual flickering. The smoke trail persists in world space even when the vessel exits the atmosphere, showing the path through the atmosphere.
**Handled in v1.**

### On-rails orbital segment entering atmosphere
**Scenario:** A recording has an orbital segment with periapsis inside the atmosphere (aerobraking pass recorded during time warp).
**Expected:** The ghost follows the Keplerian orbit. As it descends into the atmosphere, reentry FX activate based on the computed orbital velocity converted to surface velocity. The Keplerian orbit doesn't account for drag (which is expected — the original vessel experienced drag during recording, but the orbit parameters were captured at segment start). The visual is approximate but looks correct to the player.
**Handled in v1 — acceptable approximation.**

### Very high time warp during reentry playback
**Scenario:** Player time-warps through a ghost's reentry (10x or higher).
**Expected:** Particle systems and trail renderers still work — Unity handles large `deltaTime` internally. Trail segments may be spaced farther apart, creating a chunkier trail. Particles may be emitted in fewer bursts. This is acceptable — at high warp, the player isn't closely watching the visuals.
**v1 limitation — acceptable. No special handling needed.**

### Looping playback
**Scenario:** A recording with `LoopPlayback = true` loops through an atmospheric entry phase repeatedly.
**Expected:** At each loop reset (UT jumps back to StartUT), the reentry FX state must reset. `TrailRenderer.Clear()` clears the trail. `ParticleSystem.Clear(true)` clears lingering particles. `lastIntensity` resets to 0. Without this, trails from the previous loop would persist and overlap with the new loop.
**Handled in v1.**

### Vessel never enters atmosphere
**Scenario:** An orbital-only recording (e.g., orbit raising maneuver).
**Expected:** `ReentryFxInfo` is built at ghost spawn time but never activates (intensity stays 0). Particle systems remain stopped, trail renderer stays empty. Zero performance cost beyond the initial creation.
**Handled in v1.**

### SOI change during atmosphere entry
**Scenario:** Ghost vessel enters Laythe's atmosphere from Jool's SOI — the `bodyName` changes between adjacent trajectory points at the SOI boundary.
**Expected:** The interpolation uses `before.bodyName` and `after.bodyName`. If they differ, the velocity interpolation is still performed (same as position interpolation, which already handles cross-body interpolation). The intensity computation uses the `after` body's atmosphere properties when `bodyName` changes, since the vessel is transitioning into that body's SOI. In practice, SOI transitions happen well outside the atmosphere (SOI boundaries are at high altitude), so this edge case should never produce visible reentry FX at the transition point. The FX activate later as the vessel descends into the new body's atmosphere.
**Handled in v1 — no special code needed.**

### Looping playback with pause gap
**Scenario:** A recording with `LoopPlayback = true` and `LoopPauseSeconds > 0` finishes one loop. During the pause, should reentry FX show?
**Expected:** During the loop pause window, the ghost is held at its final position with zero velocity. Intensity naturally drops to 0 (no velocity = no dynamic pressure). The exponential smoothing produces a brief fadeout at the end of the loop. When the next loop starts, the FX state was already reset (see "Looping playback" edge case above). No special pause handling needed.
**Handled in v1.**

### Negative altitude (below sea level)
**Scenario:** A recording has trajectory points with negative altitude (splashed vessel or terrain collision).
**Expected:** `body.GetPressure(altitude)` with negative altitude may return higher-than-surface pressure. Since the vessel is at or below sea level, dynamic pressure is very high if the vessel is moving fast — but this is physically correct (dense atmosphere = strong heating). No special handling needed. If the API returns NaN or negative for extreme negative altitudes, the NaN/negative guards in step 7 of the intensity computation catch it.
**Handled in v1 — guarded by NaN/negative fallback.**

### Camera distance and particle visibility
**Scenario:** Player is on the ground at KSC watching a reentry ghost at 50+ km altitude.
**Expected:** Individual particles from Layers B and C may be too small to render at that distance. This is mitigated by: (a) `maxParticleSize` set high on both particle systems, and (b) Layer D (TrailRenderer) remains visible at extreme distances because it's geometry-based. The combined effect is: at close range, the player sees the full fireball; at long range, they see the trail streak across the sky with a faint glow at the head.
**Handled in v1 — Layer D is the distance-visibility solution.**

### Ascending through atmosphere at high speed (exit, not reentry)
**Scenario:** Ghost vessel during a launch reaches high speeds while still in atmosphere (e.g., 1500 m/s at 30km altitude during aggressive gravity turn).
**Expected:** FX activate if dynamic pressure exceeds threshold. This is physically correct — a vessel moving fast through atmosphere experiences heating regardless of whether it's ascending or descending. During typical Kerbin launches, q is moderate during ascent (speed increases as density decreases). An aggressive gravity turn or a rocket maintaining high speed at low altitude would show mild FX. The same effect applies to a vessel climbing OUT of an atmosphere after aerobraking — FX fade as the vessel gains altitude and density drops.
**Handled in v1 — q-based thresholds are direction-agnostic by design.**

## What Doesn't Change

- **Recording format**: No new fields in `TrajectoryPoint`, no new `PartEvent` types, no new ConfigNode keys. Format version stays the same.
- **Save files**: Nothing new persists. `ReentryFxInfo` is purely runtime state.
- **FlightRecorder**: No recording-side changes. All computation happens at playback time.
- **RecordingStore**: No changes. No new serialization.
- **ParsekScenario**: No changes. No new save/load logic.
- **Existing ghost visuals**: Engine FX, RCS FX, heat animations, deployables, lights, fairings, parachutes — all unchanged.
- **Preview playback (F10/F11)**: Preview ghosts do not get reentry FX. Preview is for quick shape review, not full visual replay.
- **Test generators**: No changes needed to `RecordingBuilder`, `VesselSnapshotBuilder`, `ScenarioWriter`.
- **Existing tests**: No changes. New tests are additive.

## Backward Compatibility

No backward compatibility concerns. This feature:
- Adds no new serialized data
- Requires no format migration
- Works with all existing recordings (v2, v3, v4 — any recording with trajectory points)
- Falls back gracefully: if no atmosphere or low speed, no FX displayed (same as current behavior)

Old recordings gain reentry FX automatically when played back with the new code. No migration, no opt-in.

## Diagnostic Logging

### Ghost build time
- `[Parsek][Verbose] ReentryFx: Built for ghost #{i} "{vesselName}" — {flameCount} flame systems, {smokeCount} smoke systems, trail={hasTrail}, glow materials={glowCount}` — when `TryBuildReentryFx` completes
- `[Parsek][Verbose] ReentryFx: Skipped for ghost #{i} "{vesselName}" — no snapshot, sphere fallback (particles only)` — when ghost has no vessel snapshot (Layers B-D only)
- `[Parsek][Verbose] ReentryFx: Skipped {count} materials already managed by HeatGhostInfo for ghost #{i}` — when heat-animation materials are excluded from glow list

### Playback-time state transitions
- `[Parsek][Verbose] ReentryFx: Activated for ghost #{i} "{vesselName}" — intensity={intensity:F2}, q={q:F0} Pa, speed={speed:F0} m/s, alt={alt:F0} m, body={bodyName}` — when intensity crosses from 0 to >0 (first activation)
- `[Parsek][Verbose] ReentryFx: Deactivated for ghost #{i} "{vesselName}" — intensity dropped to 0 (speed={speed:F0} m/s, alt={alt:F0} m)` — when intensity returns to 0 after being active
- `[Parsek][Verbose] ReentryFx: Loop reset for ghost #{i} — cleared trail and particles` — when looping playback resets FX state

### Per-frame diagnostics (rate-limited)
- `[Parsek][Verbose] ReentryFx: ghost #{i} intensity={intensity:F2} q={q:F0} speed={speed:F0} alt={alt:F0}` — via `ParsekLog.VerboseRateLimited("ReentryFx", "ghost-{i}-intensity", ...)`, rate-limited (every 5s) while FX are active, for tuning and debugging intensity curves

### Decision points
- `[Parsek][Verbose] ReentryFx: body {bodyName} has no atmosphere — skipping` — rate-limited, when body lacks atmosphere
- `[Parsek][Verbose] ReentryFx: altitude {alt:F0} above atmosphereDepth {depth:F0} — skipping` — rate-limited, when vessel is above atmosphere
- `[Parsek][Verbose] ReentryFx: orbital velocity converted to surface velocity for orbit segment playback (orbital={orbV:F0}, surface={surfV:F0})` — rate-limited, when orbital→surface velocity conversion occurs during orbit segment playback
- `[Parsek][Verbose] ReentryFx: GetDensity/GetPressure/GetTemperature returned invalid value for ghost #{i} — density fallback to 0` — when KSP atmosphere API returns NaN or negative

## Test Plan

### Unit tests (pure logic, no Unity)

**`ComputeReentryIntensity` — basic threshold behavior**
- Input: q=0, speed=0 → expected: intensity=0. Fails if: threshold logic broken, returns non-zero for vacuum.
- Input: q=10000, speed=1500 → expected: intensity in (0,1). Fails if: linear ramp doesn't interpolate.
- Input: q=30000, speed=2500 → expected: intensity=1.0. Fails if: saturation clamp doesn't work.
- Input: q=15000, speed=200 → expected: intensity=0. Fails if: speed threshold not applied (slow flight in thick atmosphere shouldn't glow).

**`ComputeReentryIntensity` — edge values**
- Input: q=NaN → expected: intensity=0 (safe fallback). Fails if: NaN propagates through FX system.
- Input: q=negative → expected: intensity=0. Fails if: negative density or speed not guarded.
- Input: speed=NaN → expected: intensity=0. Fails if: NaN speed not guarded.
- Input: q=float.PositiveInfinity → expected: intensity=1.0 (clamped). Fails if: infinity not handled.

**`ComputeReentryIntensity` — body-agnostic via q**
- Compute q for Eve-like density (5x Kerbin) at moderate speed → high intensity. Compute q for Duna-like density (0.07x Kerbin) at same speed → low intensity. Fails if: the function has hardcoded Kerbin-specific thresholds instead of using q directly.

**`ComputeReentryIntensity` — ascending vs descending produces same result**
- Same q and speed → same intensity regardless of velocity direction. Fails if: the function incorrectly considers velocity direction.

### Integration tests

**Synthetic recording through atmosphere**
- Build a recording with `RecordingBuilder` that has trajectory points descending from 75km to 30km altitude on Kerbin with velocity ~2200 m/s.
- Verify that `ComputeReentryIntensity` returns >0 for points below `atmosphereDepth` with sufficient speed, and 0 for points above.
- Fails if: the function doesn't correctly combine altitude/speed/density checks.

**Looping playback reset**
- Verify that after loop reset, `lastIntensity` returns to 0. Fails if: stale intensity from previous loop bleeds into new loop.

### Log assertion tests

**Activation/deactivation logging**
- Feed a trajectory that transitions from vacuum to atmosphere to vacuum. Assert log output contains "Activated" followed by "Deactivated". Fails if: state transition logging is missing.

### Manual in-game tests

**Suborbital arc recording (existing synthetic)**
- The "Suborbital Arc" synthetic recording reaches 71km and descends. After implementing reentry FX, load the test career and observe whether the ghost shows reentry effects during descent. Expected: visible glow and trail during the steep descent phase.

**New synthetic: Orbital reentry recording**
- Create a new synthetic recording with trajectory points simulating a deorbit burn + atmospheric reentry (e.g., 75km→30km at 2000+ m/s). Inject via `dotnet test --filter InjectAllRecordings`. Verify visually in-game that the ghost shows progressive reentry FX.

## Implementation Phases

### Phase 1: Core intensity computation
- Add `ComputeReentryIntensity` as a pure `internal static` method in `GhostVisualBuilder.cs` (not `TrajectoryMath.cs` — this is ghost-visual-specific computation, not general trajectory math)
- Input: `float speed, float dynamicPressure` (both pre-computed by the caller; the function does not need `hasAtmosphere` or altitude — those are checked by the caller before invoking)
- Output: `float` 0-1
- Unit tests for all threshold cases (NaN, negative, ramp, saturation, speed gate)
- No visual changes yet — just the math

### Phase 2: ReentryFxInfo and ghost build
- Add `ReentryFxInfo` class to `GhostVisualBuilder.cs` (reuses existing `HeatMaterialState` struct for glow materials)
- Add `TryBuildReentryFx` static method — creates particle systems (flame + smoke) and trail renderer on ghost root, collects glow materials (excluding `HeatGhostInfo`-managed parts)
- Add `reentryFxInfo` field to `GhostPlaybackState`
- Wire into `SpawnTimelineGhost` (called after `BuildTimelineGhostFromSnapshot`, not inside it)
- Add `lastInterpolatedVelocity` field to `GhostPlaybackState`, set during `InterpolateAndPosition`
- No playback logic yet — FX components exist but are dormant

### Phase 3: Playback integration
- Add `UpdateReentryFx` method to `ParsekFlight.cs`
- Wire into `UpdateTimelinePlayback` after `InterpolateAndPosition` + `ApplyPartEvents`
- Read `state.lastInterpolatedVelocity`, convert to surface velocity via `body.getRFrmVel()`
- Query `body.GetPressure`/`GetTemperature`/`GetDensity` with NaN/negative guards
- Compute dynamic pressure → intensity → drive particle emission, trail renderer, material glow
- Handle orbital-segment velocity conversion (same `getRFrmVel` path)
- Add loop reset logic: `TrailRenderer.Clear()` + `ParticleSystem.Clear(true)` + `lastIntensity = 0`
- Diagnostic logging for state transitions (activation/deactivation, rate-limited intensity)

### Phase 4a: Edge case handling
- Handle sphere-fallback ghosts (Layers B-D only, no Layer A glow — no renderers to tint)
- Handle interaction with `HeatGhostInfo` materials (skip parts by persistentId)
- Verify decoupled-part glow skip (`activeInHierarchy` check)
- Test with existing "Suborbital Arc" synthetic recording

### Phase 4b: Synthetic recording and visual tuning
- Add new synthetic recording: "Orbital Reentry" — trajectory descending from 75km to 30km at 2000+ m/s
- Tune particle colors, sizes, lifetimes, emission rates in-game
- Tune intensity thresholds (`qThresholdLow`, `qThresholdHigh`) and smoothing rate
- Verify on Kerbin via synthetic recording

### Phase 4c: Performance and multi-body verification
- Performance check: verify `maxParticles` cap prevents particle accumulation during extended atmosphere traversal
- Test with multiple simultaneous reentry ghosts
- If possible (requires recordings on other bodies), verify Eve/Duna/Jool intensity behavior — otherwise verify via unit tests with body-equivalent q values
