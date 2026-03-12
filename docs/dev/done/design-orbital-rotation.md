# Design: Orbital Rotation Fidelity

## Problem

When a vessel goes on rails during orbital flight (time warp), Parsek records the Keplerian orbital elements but not the vessel's attitude. During playback, the ghost's rotation is derived from the orbital velocity vector (`Quaternion.LookRotation(velocity)`), making every vessel appear to face prograde. A vessel holding retrograde, normal, radial, or any other SAS orientation will appear prograde-locked during the orbital segment. This is visually wrong and breaks the fidelity of mission replay.

Additionally, the PersistentRotation mod (commonly installed) actively rotates vessels during time warp — spinning vessels continue to spin, and body-relative orientations are maintained. Without support for this, ghost playback cannot reproduce what the player actually saw during recording.

### Relationship to Known-Bugs #16

Known-bugs.md #16 documents an earlier analysis (based on PersistentRotation mod research) that recommended storing angular momentum and `Planetarium.Rotation`. After further review, that approach was refined:

1. **Angular momentum requires MOI** to convert to angular velocity (`omega = L/I`). MOI is unavailable on ghosts (no rigidbody). Instead, we store angular velocity directly — MOI doesn't change during on-rails (no fuel burn, no staging), so angular velocity is constant for the segment.
2. **The spin-forward formula** `AngleAxis(omega.mag * dt, axis) * storedRot` is a single-axis approximation. This matches PersistentRotation's own `PackedSpin` implementation, which uses the same approximation.
3. **`Planetarium.right` drift compensation** adds complexity whose value is unclear without empirical measurement. Deferred to Phase 6.

This design stores orbital-frame-relative rotation (for SAS-locked/stable vessels) plus angular velocity (for spinning vessels when PersistentRotation is active). It replaces the implementation plan in known-bugs.md #16. The format v6 version bump mentioned there is not needed (see Backward Compatibility).

## Terminology

- **Orbital frame**: The reference frame defined by the velocity vector (forward/prograde), the radial-out vector (up), and their cross product (normal). Changes continuously as the vessel orbits.
- **Orbital-frame-relative rotation**: The vessel's rotation expressed relative to the orbital frame. Identity = vessel facing prograde with "up" toward radial-out.
- **On-rails boundary**: The moment KSP transitions a vessel from physics simulation to Keplerian propagation (going on rails) or vice versa (going off rails).
- **Spin-forward**: Reconstructing a spinning vessel's orientation during playback by applying the recorded angular velocity over the elapsed time since the segment start.
- **PersistentRotation**: A KSP mod that preserves vessel rotation and angular momentum across time warp. Without it, KSP freezes vessel rotation when going on rails. With it, spinning vessels continue to spin and body-relative orientations are maintained.

## Mental Model

### Stable vessels (SAS-locked or non-spinning)

```
Recording (on-rails boundary):
  worldRot = vessel.transform.rotation
  orbVel   = vessel.obt_velocity
  radialOut = (vessel.position - body.position).normalized
  orbFrame = LookRotation(orbVel, radialOut)
  storedRot = Inverse(orbFrame) * worldRot    <-- orbital-frame-relative

Playback (any UT during orbital segment):
  orbVel'   = orbit.getOrbitalVelocityAtUT(ut)
  radialOut' = (worldPos - body.position).normalized
  orbFrame' = LookRotation(orbVel', radialOut')
  ghostRot  = orbFrame' * storedRot           <-- reconstruct world rotation
```

The stored rotation is a constant offset from the orbital frame. As the orbital frame rotates around the orbit, the ghost maintains the same attitude relative to it. A vessel holding retrograde at the boundary will appear to hold retrograde throughout the segment.

### Spinning vessels (PersistentRotation active)

```
Recording (on-rails boundary, PersistentRotation detected):
  storedRot  = Inverse(orbFrame) * worldRot   <-- same as above
  angVelLocal = Inverse(vessel.rotation) * vessel.angularVelocity  <-- convert world→vessel-local

Playback (any UT during orbital segment):
  if angVelLocal above threshold:
    boundaryWorldRot = orbFrame(startUT) * storedRot
    worldAxis = boundaryWorldRot * angVelLocal        <-- local→world transform
    dt = ut - startUT
    ghostRot = AngleAxis(|angVelLocal| * dt * Rad2Deg, worldAxis) * boundaryWorldRot
  else:
    ghostRot = orbFrame(ut) * storedRot       <-- stable path
```

When PersistentRotation is active and the vessel is spinning, the ghost is spun forward from the boundary world rotation using the recorded angular velocity. This matches PersistentRotation's `PackedSpin` method, which applies `AngleAxis(omega.magnitude * warpRate, ReferenceTransform.rotation * omega) * currentRotation` each frame. Note: PersistentRotation stores angular velocity in vessel-local frame (via `_angularVelocity` which is set from vessel-frame quantities). We do the same: convert `vessel.angularVelocity` (world-space) to vessel-local at recording time via `Inverse(vessel.rotation) * vessel.angularVelocity`.

**Quaternion multiplication order:** `Inverse(orbFrame) * worldRot` is the correct encoding — it expresses `worldRot` in the coordinate system defined by `orbFrame`. The decoding `orbFrame' * storedRot` reverses this. This is the standard change-of-basis pattern.

**`LookRotation` degenerate case guard:** Unity's `LookRotation(forward, up)` degenerates when `forward` and `up` are near-parallel. For orbital mechanics, velocity is tangential and radial-out is perpendicular, so they are always ~90 degrees apart. As a safety measure, `ComputeOrbitalFrameRotation` checks `Vector3.Dot(velocity.normalized, radialOut.normalized)` and falls back to `LookRotation(velocity)` (without the `up` hint) if the dot product exceeds 0.99.

## PersistentRotation Compatibility

### Detection

Parsek detects PersistentRotation at recording start via assembly check:

```csharp
bool hasPersistentRotation = AssemblyLoader.loadedAssemblies.Any(
    a => a.name == "PersistentRotation");
```

Checked once per recording start, cached in a field on `FlightRecorder`. Used to decide whether to record angular velocity.

### Why detection is needed

Without PersistentRotation, KSP freezes vessel rotation when going on rails. The player sees a static vessel during warp. With PersistentRotation, spinning vessels visibly spin during warp. The ghost should reproduce what the player actually saw:

| PersistentRotation installed? | Vessel spinning? | Player sees during warp | Ghost should... |
|------|------|------|------|
| No | Yes (at boundary) | Frozen | Hold boundary attitude |
| No | No (SAS-locked) | Frozen | Hold SAS-relative attitude |
| Yes | Yes (angVel > threshold) | Spinning | Spin forward |
| Yes | No (SAS-locked, ABSOLUTE mode) | Frozen | Hold SAS-relative attitude |
| Yes | No (StabilityAssist, RELATIVE mode) | Body-relative locked | Hold orbital-frame-relative attitude |

Recording angular velocity unconditionally (without detection) would cause the ghost to spin even when the player saw a frozen vessel (PersistentRotation not installed). Detection ensures visual fidelity.

### No conflicts at runtime

- **Event handlers**: PersistentRotation's `OnVesselGoOnRails` is empty. No conflict with Parsek capturing rotation at the same event.
- **FixedUpdate**: PersistentRotation manages rotation in `FixedUpdate` for packed+loaded vessels. Parsek's physics-frame recording patch (`VesselPrecalculate.CalculatePhysicsStats`) only fires for unpacked vessels. No overlap.
- **Ghost objects**: PersistentRotation iterates `FlightGlobals.Vessels`. Parsek's ghosts are plain GameObjects, not registered as KSP Vessels. PersistentRotation will never touch them.
- **SOI changes**: Both mods subscribe to `onVesselSOIChanged`. PersistentRotation does not have an SOI handler; it handles the transition via FixedUpdate state. No conflict.

### PersistentRotation stability modes (reference)

PersistentRotation categorizes vessels into stability modes that determine on-rails behavior:

- **ABSOLUTE** (SAS prograde/retrograde/normal/radial/target/etc.): PersistentRotation does NOT change rotation during warp (when angMom < threshold). Defers to SAS.
- **RELATIVE** (SAS StabilityAssist): PersistentRotation applies body-relative rotation lock (`PackedRotation`). The vessel maintains its orientation relative to a reference body.
- **OFF** (no SAS, no control): PersistentRotation applies `PackedSpin` with stored momentum.
- **Any mode with angMom >= threshold**: PersistentRotation applies `PackedSpin` (except AUTOPILOT).

For Parsek's recording:
- ABSOLUTE + stable: our orbital-frame-relative rotation perfectly captures the frozen attitude.
- RELATIVE + stable: our orbital-frame-relative rotation closely approximates body-relative lock (for circular orbits, exactly; for eccentric orbits, minor differences). This is acceptable.
- Spinning: we record angular velocity and reproduce the spin.

## Gameplay Simulation

### Scenario 1: Retrograde Station-Keeping in Low Orbit

The player launches a science satellite into low Kerbin orbit with SAS locked to retrograde (heat shield forward for planned de-orbit). They time-warp several orbits.

**Recording:** When time warp starts, `OnVesselGoOnRails` fires. The recorder captures Keplerian elements and the vessel's rotation relative to the orbital frame. Since the vessel faces retrograde, the stored offset is a 180-degree yaw from prograde. PersistentRotation (if installed) enters ABSOLUTE mode for retrograde SAS and doesn't change rotation during warp.

**Playback:** The ghost orbits Kerbin with the 180-degree yaw offset applied at every point along the orbit. The ghost consistently faces retrograde.

**Without this feature:** The ghost would face prograde at all times. Visually wrong.

### Scenario 2: Normal-Hold During Plane Change Warp

The player locks SAS to normal and warps to the ascending node.

**Recording:** The recorder captures the normal-relative attitude. PersistentRotation enters ABSOLUTE mode for normal SAS and doesn't change rotation.

**Playback:** The ghost maintains normal-hold as the orbital frame rotates around the orbit.

### Scenario 3: Old Recording Without Orbital Rotation Data

The player loads a save with recordings made before this feature existed.

**Playback:** No `ofr` keys in ORBIT_SEGMENT → `orbitalFrameRotation` stays at default `(0,0,0,0)` → `HasOrbitalFrameRotation` returns false → prograde fallback. Identical to current behavior.

### Scenario 4: New Recording on Old Parsek Version

The player shares a recording with someone on an older Parsek version.

**Loading:** Old Parsek ignores unknown ConfigNode keys (`ofrX/Y/Z/W`, `avX/Y/Z`). Plays back as prograde. No crash, graceful degradation.

### Scenario 5: SOI Change During Orbital Warp

The player warps from Kerbin orbit to Mun encounter.

**Recording:** The closed Kerbin segment already has its orbital-frame rotation (captured at `OnVesselGoOnRails`). For the new Mun segment, rotation is captured from `v.transform.rotation` and `v.obt_velocity` at SOI change time.

**Reliability of `v.transform.rotation` during SOI change:** KSP fires `OnVesselSOIChanged` while the vessel is on rails. On-rails vessels maintain a valid `transform.rotation` that KSP updates each frame from the Keplerian propagation. This is confirmed by PersistentRotation's approach, which also reads `vessel.transform.rotation` during on-rails events.

### Scenario 6: Free-Spinning Vessel, PersistentRotation Installed

The player disables SAS and lets the vessel tumble freely, then warps. PersistentRotation's `PackedSpin` makes the vessel visibly spin during warp.

**Recording:** PersistentRotation is detected. Angular velocity is above threshold (0.05 rad/s). The recorder stores both orbital-frame rotation and `vessel.angularVelocity`.

**Playback:** The ghost reconstructs the boundary world rotation from the orbit at `startUT` and the stored orbital-frame offset. Then it applies spin-forward: `AngleAxis(|angVel| * dt, worldAxis) * boundaryWorldRot`. The ghost visibly spins, matching what the player saw with PersistentRotation active.

### Scenario 7: Free-Spinning Vessel, PersistentRotation NOT Installed

Same as Scenario 6, but without PersistentRotation.

**Recording:** PersistentRotation is not detected. Angular velocity is NOT recorded (even though `vessel.angularVelocity` has a non-zero value at the boundary). The recorder stores only orbital-frame rotation.

**Playback:** No angular velocity data → ghost holds boundary attitude (frozen). This matches what the player saw: KSP froze the rotation during warp.

### Scenario 8: Recording with PersistentRotation, Playback Without

The player made a recording with PersistentRotation installed (spinning vessel). Later, they uninstall PersistentRotation.

**Playback:** The recording has `avX/Y/Z` keys. The ghost spins forward using the recorded angular velocity. This correctly reproduces what the player saw during the original recording, even though PersistentRotation is no longer installed.

### Scenario 9: Near-Zero Velocity at Orbital Apex

Suborbital vessel at apex, velocity near zero.

**Recording:** `ComputeOrbitalFrameRotation` detects `velocity.sqrMagnitude < 0.001`, returns identity (degenerate case).

**Playback:** If playback velocity is also near-zero, the `sqrMagnitude > 0.001` guard skips rotation. Same as current behavior.

### Scenario 10: Chain Recording with Orbital Segment

A chain recording with atmospheric and orbital segments.

**Recording:** Surface segments use `v.srfRelRotation` (v5). Orbital segments use orbital-frame-relative rotation. Each path is independent.

**Playback:** Point-based interpolation uses `bodyTransform.rotation * storedRot`. Orbit-based playback uses `orbFrame * storedRot`. The ghost transitions between them as the timeline progresses.

### Scenario 11: Recording Stopped Mid-Orbit

Player presses Stop while time warping.

**Recording:** `StopRecording` finalizes the current orbit segment (sets `endUT`, adds to list). The orbital-frame rotation and angular velocity (if any) were already captured at the earlier `OnVesselGoOnRails`. No additional logic needed.

### Scenario 12: Background-Only Recording

A background vessel that stayed on rails, played back via `PositionGhostFromOrbitOnly`.

**Playback:** `PositionGhostFromOrbitOnly` delegates to `PositionGhostFromOrbit`, which contains the new rotation and spin-forward logic. Works automatically.

### Scenario 13: Looped Recording with Orbital Segment

Recording with orbital segment and `LoopPlayback = true`.

**Playback:** Loop UT remapping is transparent to the orbit positioning code. For spin-forward, `dt = loopUT - startUT` is correctly bounded within the segment duration. Ghost spins consistently on each loop.

## Data Model

### OrbitSegment (extended)

```csharp
public struct OrbitSegment
{
    // Existing fields
    public double startUT, endUT;
    public double inclination, eccentricity, semiMajorAxis;
    public double longitudeOfAscendingNode, argumentOfPeriapsis;
    public double meanAnomalyAtEpoch, epoch;
    public string bodyName;

    // New: orbital-frame-relative rotation
    public Quaternion orbitalFrameRotation;  // relative to orbital velocity frame; identity = prograde

    // New: angular velocity for spin-forward (when PersistentRotation was active)
    public Vector3 angularVelocity;  // vessel-local angular velocity at boundary (rad/s)
                                     // recorded as: Inverse(v.transform.rotation) * v.angularVelocity
}
```

**Sentinels:**
- `orbitalFrameRotation`: default `Quaternion(0,0,0,0)` = no rotation data (old recordings)
- `angularVelocity`: default `Vector3(0,0,0)` = not spinning / no PersistentRotation at recording time

### New Static Methods (TrajectoryMath)

```csharp
/// Returns true if the segment has recorded orbital-frame rotation data.
/// Default (0,0,0,0) = no data.
internal static bool HasOrbitalFrameRotation(OrbitSegment seg)
    => seg.orbitalFrameRotation.x != 0f || seg.orbitalFrameRotation.y != 0f
    || seg.orbitalFrameRotation.z != 0f || seg.orbitalFrameRotation.w != 0f;

/// Returns true if the segment has spin data (angular velocity above threshold).
internal static bool IsSpinning(OrbitSegment seg)
    => seg.angularVelocity.sqrMagnitude > 0.0025f;  // 0.05^2

/// Spin-forward threshold in rad/s (matches PersistentRotation's threshold).
internal const float SpinThreshold = 0.05f;

/// Computes vessel rotation relative to the orbital velocity frame.
/// Returns Inverse(orbFrame) * worldRotation.
/// Returns identity if velocity is near-zero (degenerate frame).
/// Falls back to LookRotation(velocity) without up hint if velocity
/// and radialOut are near-parallel (dot > 0.99).
internal static Quaternion ComputeOrbitalFrameRotation(
    Quaternion worldRotation, Vector3d orbitalVelocity, Vector3d radialOut)
```

### Serialization Format

ORBIT_SEGMENT ConfigNode (after existing `body` key):

```
ORBIT_SEGMENT
{
    startUT = 17680
    endUT = 20680
    inc = 28.5
    ecc = 0.001
    sma = 700000
    lan = 90
    argPe = 45
    mna = 0
    epoch = 17680
    body = Kerbin
    ofrX = 0          // orbital-frame rotation, optional
    ofrY = 1          // orbital-frame rotation, optional
    ofrZ = 0          // orbital-frame rotation, optional
    ofrW = 0          // orbital-frame rotation, optional
    avX = 0.1         // vessel-local angular velocity, optional (only when spinning + PersistentRotation)
    avY = 0           // vessel-local angular velocity, optional
    avZ = 0.03        // vessel-local angular velocity, optional
}
```

- `ofrX/Y/Z/W`: written only when any component is non-zero. Missing = `(0,0,0,0)` = prograde fallback.
- `avX/Y/Z`: vessel-local angular velocity (`Inverse(v.rotation) * v.angularVelocity`), written only when magnitude > `SpinThreshold` (0.05 rad/s) AND PersistentRotation was detected at recording time. Missing = `(0,0,0)` = not spinning.
- All floats serialized with `ToString("R", CultureInfo.InvariantCulture)` and parsed with `float.TryParse(..., NumberStyles.Float, CultureInfo.InvariantCulture, ...)`.

**No format version bump.** Missing keys default to sentinels. Old Parsek ignores unknown keys. Fully backward and forward compatible.

### GhostPosEntry (extended)

```csharp
private struct GhostPosEntry
{
    // Existing fields...

    // New orbit rotation fields
    public Quaternion orbitFrameRot;       // Orbital-frame-relative rotation from segment
    public bool hasOrbitFrameRot;          // True if segment has rotation data
    public CelestialBody orbitBody;        // Body reference for radial-out computation in LateUpdate

    // New spin-forward fields
    public Vector3 orbitAngularVelocity;   // Vessel-local angular velocity (rad/s), stored as Inverse(rot)*worldAngVel
    public bool isSpinning;                // True if above threshold
    public double orbitSegmentStartUT;     // Segment start UT for computing dt
    public Quaternion boundaryWorldRot;    // Pre-computed boundary world rotation (for spinning case)
}
```

`orbitBody` stores a `CelestialBody` reference (not a string name) to avoid per-frame `FlightGlobals.Bodies.Find` lookups in LateUpdate. Follows the existing `bodyBefore`/`bodyAfter` pattern.

`boundaryWorldRot` is computed once in `PositionGhostFromOrbit` from `orbFrame(startUT) * orbitalFrameRotation` and cached for LateUpdate, avoiding redundant orbit queries.

### Required Code Changes (RecordingBuilder)

`RecordingBuilder.AddOrbitSegment` gains optional `ofrX/Y/Z/W` and `avX/Y/Z` float parameters (default 0). When any is non-zero, the corresponding ConfigNode keys are written.

### PersistentRotation Detection (FlightRecorder)

```csharp
private bool hasPersistentRotation;  // Cached at recording start

// In StartRecording():
hasPersistentRotation = AssemblyLoader.loadedAssemblies.Any(
    a => a.name == "PersistentRotation");
```

## Behavior

### Recording: On-Rails Boundary (`FlightRecorder.OnVesselGoOnRails`)

When the active vessel goes on rails:
1. Record a boundary TrajectoryPoint (existing behavior)
2. Capture Keplerian orbital elements (existing behavior)
3. **New:** Compute orbital-frame-relative rotation via `TrajectoryMath.ComputeOrbitalFrameRotation`
4. **New:** If `hasPersistentRotation` AND `v.angularVelocity.magnitude > SpinThreshold`: convert to vessel-local frame via `Inverse(v.transform.rotation) * v.angularVelocity` and store in `currentOrbitSegment.angularVelocity`

   **Why vessel-local?** Unity's `Rigidbody.angularVelocity` (which `vessel.angularVelocity` returns) is world-space. Storing it directly would break spin-forward playback, because the spin-forward formula `boundaryWorldRot * angVel` expects vessel-local input to convert local→world. Additionally, vessel-local is stable across FloatingOrigin shifts and matches PersistentRotation's internal representation.

- Log: `Verbose("Recorder", "Vessel went on rails -- orbit segment (body={body}, ofrRot={rot}, angVel={vel}, persistentRotation={hasPR})")`
- Log (degenerate velocity): `Verbose("Recorder", "Orbital-frame rotation: degenerate velocity (sqrMag={mag}), using identity")`
- Log (near-parallel guard): `Verbose("Recorder", "Orbital-frame rotation: velocity/radialOut near-parallel (dot={dot}), frame approximated")`
- Log (spinning detected): `Verbose("Recorder", "Spinning vessel detected (|angVel|={mag}), recording angular velocity for spin-forward")`
- **Defensive note:** `vessel.angularVelocity` reads from the rigidbody, which should still exist when `OnVesselGoOnRails` fires (event fires during `Vessel.GoOnRails()` before the rigidbody is destroyed). As a safety measure, guard with a null-check on `v.rootPart?.rb` and fall back to zero angular velocity if null.

### Recording: SOI Change (`FlightRecorder.OnVesselSOIChanged`)

When SOI changes during on-rails flight:
1. Close current segment (existing behavior)
2. Open new segment with new body's orbital elements (existing behavior)
3. **New:** Compute orbital-frame-relative rotation for the new segment
4. **New:** If `hasPersistentRotation` AND spinning: convert angular velocity to vessel-local and store

- Log: `Verbose("Recorder", "SOI changed {from} -> {to} -- new segment ofrRot={rot}, angVel={vel}")`

### Playback: `PositionGhostFromOrbit` (shared by timeline and orbit-only paths)

When positioning a ghost from an orbital segment:
1. Compute world position from orbit (existing behavior)
2. Look up `CelestialBody` by `segment.bodyName` (existing behavior)
3. **New rotation logic:**

```
if IsSpinning(segment):
    // Spin-forward path
    velAtStart = orbit.getOrbitalVelocityAtUT(segment.startUT)
    posAtStart = orbit.getPositionAtUT(segment.startUT)
    radialAtStart = (posAtStart - body.position).normalized
    orbFrameAtStart = LookRotation(velAtStart, radialAtStart)
    boundaryWorldRot = orbFrameAtStart * segment.orbitalFrameRotation

    dt = ut - segment.startUT
    worldAxis = boundaryWorldRot * segment.angularVelocity
    angle = |segment.angularVelocity| * dt * Rad2Deg
    ghost.rotation = AngleAxis(angle, worldAxis) * boundaryWorldRot

else if HasOrbitalFrameRotation(segment) AND velocity.sqrMagnitude > 0.001:
    // Orbital-frame-relative path
    radialOut = (worldPos - body.position).normalized
    guard: if Dot(velocity.normalized, radialOut) > 0.99 → LookRotation(velocity) fallback
    orbFrame = LookRotation(velocity, radialOut)
    ghost.rotation = orbFrame * segment.orbitalFrameRotation

else:
    // Prograde fallback (old recordings)
    ghost.rotation = LookRotation(velocity)
```

4. Populate `GhostPosEntry` with fields for LateUpdate, including pre-computed `boundaryWorldRot` (for spinning case)

- Log (first activation per segment, via `loggedOrbitSegments`): `Verbose("Playback", "Orbit segment {key}: spin-forward (|angVel|={mag})")` or `"...orbital-frame rotation"` or `"...velocity-derived prograde (no ofr data)"`

**Note:** `PositionGhostFromOrbitOnly` (background recordings) delegates to `PositionGhostFromOrbit` — no changes needed there.

### Playback: `InterpolateAndPosition` Orbit Branch

The main timeline playback calls `PositionGhostFromOrbit` from `InterpolateAndPosition` (~line 7005). No changes needed — the new rotation logic is encapsulated in `PositionGhostFromOrbit`.

### Playback: `LateUpdate` Orbit Case

LateUpdate re-positioning after FloatingOrigin shift mirrors `PositionGhostFromOrbit`:

```
if e.isSpinning:
    // Recompute boundary world rotation (positions shifted by FloatingOrigin)
    velAtStart = orbit.getOrbitalVelocityAtUT(e.orbitSegmentStartUT)
    posAtStart = orbit.getPositionAtUT(e.orbitSegmentStartUT)
    radialAtStart = (posAtStart - e.orbitBody.position).normalized
    orbFrameAtStart = LookRotation(velAtStart, radialAtStart)
    boundaryWorldRot = orbFrameAtStart * e.orbitFrameRot

    dt = e.orbitUT - e.orbitSegmentStartUT
    worldAxis = boundaryWorldRot * e.orbitAngularVelocity
    angle = |e.orbitAngularVelocity| * dt * Rad2Deg
    ghost.rotation = AngleAxis(angle, worldAxis) * boundaryWorldRot

else if e.hasOrbitFrameRot AND velocity.sqrMagnitude > 0.001:
    if e.orbitBody is null → LookRotation(velocity) fallback
    radialOut = (pos - orbitBody.position).normalized
    guard: near-parallel check
    orbFrame = LookRotation(velocity, radialOut)
    ghost.rotation = orbFrame * e.orbitFrameRot

else:
    ghost.rotation = LookRotation(velocity)
```

- Log (body null): `Verbose("Playback", "Orbit LateUpdate: orbitBody null for cache={key}, velocity fallback")`

## Edge Cases

### 1. Old recording without `ofr` keys

**Scenario:** Recording created before this feature, deserialized by new Parsek.

**Expected:** `orbitalFrameRotation = (0,0,0,0)`, `HasOrbitalFrameRotation` returns false, prograde fallback. Identical to current behavior.

**Status:** Handled

### 2. New recording on old Parsek

**Scenario:** Recording with `ofrX/Y/Z/W` and `avX/Y/Z` keys loaded by old Parsek.

**Expected:** Old code ignores unknown ConfigNode keys, plays back as prograde. No crash.

**Status:** Handled by ConfigNode design

### 3. Zero velocity at recording time

**Scenario:** Suborbital apex or nearly-stopped vessel goes on rails.

**Expected:** `ComputeOrbitalFrameRotation` detects `sqrMagnitude < 0.001`, returns identity. Logged.

**Status:** Handled

### 4. Zero velocity at playback time

**Scenario:** Ghost passes through orbital apex where velocity crosses zero.

**Expected:** `velocity.sqrMagnitude > 0.001` guard skips rotation — ghost keeps previous rotation.

**Status:** Handled

### 5. Free-spinning vessel, PersistentRotation installed

**Scenario:** Tumbling vessel goes on rails. PersistentRotation makes it visibly spin during warp.

**Expected:** Angular velocity recorded. Ghost spins forward, matching what the player saw.

**Status:** Handled

### 6. Free-spinning vessel, PersistentRotation NOT installed

**Scenario:** Tumbling vessel goes on rails. KSP freezes rotation during warp.

**Expected:** Angular velocity NOT recorded (PersistentRotation not detected). Ghost holds boundary attitude (frozen). Matches what the player saw.

**Status:** Handled

### 7. SAS attitude change during warp

**Scenario:** Player locks SAS to retrograde, warps. Does KSP adjust attitude during warp?

**Expected:** KSP doesn't change vessel attitude during on-rails warp — SAS is frozen. PersistentRotation in ABSOLUTE mode also doesn't change rotation. The stored boundary attitude is correct for the entire segment.

**Status:** Non-issue

### 8. SOI transition

**Scenario:** Vessel warps from Kerbin to Mun.

**Expected:** New segment captures attitude relative to new body's orbital frame. `v.transform.rotation` is valid during on-rails SOI events.

**Status:** Handled

### 9. Body not found during LateUpdate

**Scenario:** `e.orbitBody` is null (reference invalidated between frames).

**Expected:** Falls back to `LookRotation(velocity)`. Logged.

**Status:** Handled

### 10. Very long orbital segment (interplanetary)

**Scenario:** Multi-day interplanetary transfer.

**Expected:** `Planetarium.right` drift may cause minor inertial frame mismatch.

**Status:** Acceptable v1 limitation (Phase 6)

### 11. Degenerate orbit (ecc >= 1, hyperbolic)

**Scenario:** Escape trajectory or hyperbolic flyby.

**Expected:** Orbital frame is well-defined from velocity + radial-out. Velocity is always non-zero on hyperbolic orbits.

**Status:** Handled

### 12. Multiple orbit segments with different attitudes

**Scenario:** Recording with 3 orbit segments, each with different SAS orientations.

**Expected:** Each segment stores its own rotation and angular velocity. Playback uses the active segment's data.

**Status:** Handled

### 13. Near-parallel velocity and radial-out

**Scenario:** Unusual orbital geometry where velocity approaches radial direction.

**Expected:** Dot product guard (> 0.99) triggers `LookRotation(velocity)` fallback. Logged.

**Status:** Handled

### 14. Recording stopped mid-orbit

**Scenario:** Player stops recording while time warping.

**Expected:** `StopRecording` finalizes orbit segment. Rotation and angular velocity were captured at go-on-rails. No additional logic needed.

**Status:** Handled

### 15. Looped recording with orbital segment

**Scenario:** `LoopPlayback = true` with orbital segment.

**Expected:** Remapped UT works transparently. For spinning, `dt = loopUT - startUT` is bounded within segment duration. Spin is consistent across loops.

**Status:** Handled

### 16. Background-only recording

**Scenario:** Background vessel played via `PositionGhostFromOrbitOnly`.

**Expected:** Delegates to `PositionGhostFromOrbit`. Rotation and spin-forward apply automatically.

**Status:** Handled

### 17. Vessel destruction during orbital recording

**Scenario:** Vessel destroyed while on rails.

**Expected:** `OnVesselWillDestroy` finalizes orbit segment. Rotation and angular velocity were captured at go-on-rails.

**Status:** Handled

### 18. PersistentRotation installed at recording but not playback

**Scenario:** Recording made with PersistentRotation (spinning vessel). Played back without it.

**Expected:** `avX/Y/Z` keys are present in the segment. Ghost spins forward. Correctly reproduces what the player saw during recording.

**Status:** Handled

### 19. PersistentRotation installed at playback but not recording

**Scenario:** Recording made without PersistentRotation. Played back with it installed.

**Expected:** No `avX/Y/Z` keys. Ghost uses orbital-frame-relative rotation (frozen). PersistentRotation doesn't affect ghost objects (not in `FlightGlobals.Vessels`).

**Status:** Handled

### 20. Very high angular velocity

**Scenario:** Vessel spinning very fast (e.g., 2+ rad/s) at on-rails boundary.

**Expected:** Angular velocity is recorded and reproduced. At high spin rates, the single-axis `AngleAxis` approximation may diverge from PersistentRotation's iterative approach for multi-axis tumble, but single-axis spins (the common case) are exact.

**Status:** Handled (minor fidelity loss for multi-axis tumble)

### 21. PersistentRotation RELATIVE mode (StabilityAssist + body-relative)

**Scenario:** SAS in StabilityAssist mode with PersistentRotation's rotation mode active. PersistentRotation applies `PackedRotation` to maintain body-relative orientation.

**Expected:** Angular velocity is below threshold (vessel is stable). We record orbital-frame-relative rotation. This closely approximates body-relative lock for circular orbits (orbital frame ≈ body-relative frame). For eccentric orbits, minor differences exist but are acceptable.

**Status:** Acceptable approximation

## What Doesn't Change

- **Surface/atmospheric recording and playback**: Unchanged. v5 surface-relative rotation path is completely independent.
- **Point-based interpolation**: Unchanged. Only the orbit-segment playback path is modified.
- **Recording format version**: Stays at v5. No migration. No version bump.
- **Serialization of non-orbital data**: All other ConfigNode keys (points, part events, metadata) are untouched.
- **Ghost visual building**: Unaffected. This feature changes rotation, not mesh/visual construction.
- **UI**: No user-facing changes. The feature is transparent.
- **Existing tests**: All existing tests remain valid.
- **`PositionGhostFromOrbitOnly`**: Not modified — delegates to `PositionGhostFromOrbit`.
- **`InterpolateAndPosition`**: Not modified — calls `PositionGhostFromOrbit`.

## Backward Compatibility

| Scenario | Behavior |
|----------|----------|
| Old recording (no `ofr`/`av` keys) on new Parsek | Sentinels detected, prograde fallback. **Identical to current.** |
| New recording (with `ofr`/`av` keys) on old Parsek | Old code ignores unknown keys. Prograde fallback. **No crash.** |
| New recording on new Parsek, no PersistentRotation | Uses orbital-frame rotation. **Correct attitude.** |
| New recording on new Parsek, PersistentRotation active | Uses spin-forward for spinning vessels. **Correct attitude.** |

No format version bump. No migration. No breaking changes.

## Diagnostic Logging

### Recording Side

| Decision Point | Log Line | Level |
|----------------|----------|-------|
| Orbit segment captured | `"Vessel went on rails -- orbit segment (body={body}, ofrRot={rot}, angVel={vel}, persistentRotation={hasPR})"` | Verbose |
| PersistentRotation detection at recording start | `"PersistentRotation mod detected: {hasPR}"` | Info |
| Spinning vessel detected | `"Spinning vessel detected (|angVel|={mag}), recording angular velocity"` | Verbose |
| Degenerate velocity at recording | `"Orbital-frame rotation: degenerate velocity (sqrMag={mag}), using identity"` | Verbose |
| Near-parallel velocity/radial-out | `"Orbital-frame rotation: velocity/radialOut near-parallel (dot={dot}), frame approximated"` | Verbose |
| SOI change segment | `"SOI changed {from} -> {to} -- new segment ofrRot={rot}, angVel={vel}"` | Verbose |

### Playback Side

| Decision Point | Log Line | Level |
|----------------|----------|-------|
| Spin-forward path activated (first per segment) | `"Orbit segment {key}: spin-forward (|angVel|={mag})"` | Verbose |
| Orbital-frame rotation path activated (first per segment) | `"Orbit segment {key}: orbital-frame rotation"` | Verbose |
| Prograde fallback (first per segment) | `"Orbit segment {key}: velocity-derived prograde (no ofr data)"` | Verbose |
| Near-parallel guard at playback | `"Orbit segment {key}: velocity/radialOut near-parallel, LookRotation fallback"` | Verbose |
| Body null in LateUpdate | `"Orbit LateUpdate: orbitBody null for cache={key}, velocity fallback"` | Verbose |

## Test Plan

### Unit Tests (TrajectoryMath) -- in `OrbitSegmentTests.cs`

1. **`HasOrbitalFrameRotation_DefaultSegment_ReturnsFalse`**
   - Input: default `OrbitSegment`
   - Expected: `false`
   - Guards against: treating (0,0,0,0) as valid rotation data

2. **`HasOrbitalFrameRotation_WithRotation_ReturnsTrue`**
   - Input: `orbitalFrameRotation = (0, 1, 0, 0)` (retrograde)
   - Expected: `true`
   - Guards against: sentinel check rejecting valid rotations with zero components

3. **`HasOrbitalFrameRotation_IdentityQuaternion_ReturnsTrue`**
   - Input: `orbitalFrameRotation = (0, 0, 0, 1)` (prograde)
   - Expected: `true` (w=1 is non-zero)
   - Guards against: confusing prograde (valid) with no-data (invalid)

4. **`HasOrbitalFrameRotation_AllZero_ReturnsFalse`**
   - Input: `orbitalFrameRotation = (0, 0, 0, 0)` explicitly
   - Expected: `false`
   - Guards against: sentinel detection failure after explicit assignment

5. **`IsSpinning_DefaultSegment_ReturnsFalse`**
   - Input: default `OrbitSegment` (angularVelocity = zero)
   - Expected: `false`
   - Guards against: treating zero angular velocity as spinning

6. **`IsSpinning_AboveThreshold_ReturnsTrue`**
   - Input: `angularVelocity = (0.1, 0, 0)` (0.1 rad/s, above 0.05 threshold)
   - Expected: `true`
   - Guards against: wrong threshold or comparison direction

7. **`IsSpinning_BelowThreshold_ReturnsFalse`**
   - Input: `angularVelocity = (0.01, 0, 0)` (0.01 rad/s, below threshold)
   - Expected: `false`
   - Guards against: threshold too low, capturing near-zero drift as spin

8. **`ComputeOrbitalFrameRotation_Prograde_ReturnsIdentity`**
   - Input: vessel facing prograde
   - Expected: identity quaternion (within tolerance)
   - Guards against: incorrect frame computation

9. **`ComputeOrbitalFrameRotation_Retrograde_Returns180Yaw`**
   - Input: vessel facing retrograde
   - Expected: 180-degree yaw rotation
   - Guards against: incorrect Inverse(frame) * rotation ordering

10. **`ComputeOrbitalFrameRotation_ZeroVelocity_ReturnsIdentity`**
    - Input: velocity = (0, 0, 0)
    - Expected: identity
    - Guards against: NaN from degenerate velocity

11. **`ComputeOrbitalFrameRotation_NearParallelVectors_NoNaN`**
    - Input: velocity and radialOut nearly parallel (dot > 0.99)
    - Expected: valid quaternion, no NaN
    - Guards against: LookRotation numerical instability

12. **`ComputeOrbitalFrameRotation_RoundTrip`**
    - Encode then decode with same orbital frame
    - Expected: reconstructed matches original world rotation
    - Guards against: encode/decode asymmetry

13. **`SpinForward_SingleAxis_CorrectAngle`**
    - Input: boundary rotation + angular velocity around single axis, known dt
    - Expected: rotation matches `AngleAxis(angle, axis) * boundaryRot`
    - Guards against: incorrect spin-forward formula, wrong axis/angle computation

14. **`SpinForward_ZeroAngVel_FallsBackToOrbitalFrame`**
    - Input: segment with orbital-frame rotation but zero angular velocity
    - Expected: `IsSpinning` returns false, orbital-frame path used
    - Guards against: spin-forward activating on non-spinning vessels

### Serialization Tests (OrbitSegmentTests)

15. **`Serialization_RoundTrip_PreservesOrbitalFrameRotation`**
    - Serialize and deserialize via `SerializeTrajectoryInto`/`DeserializeTrajectoryFrom`
    - Expected: `orbitalFrameRotation` preserved
    - Guards against: key mismatch, locale-dependent formatting

16. **`Serialization_RoundTrip_PreservesAngularVelocity`**
    - Serialize and deserialize segment with angular velocity
    - Expected: `angularVelocity` preserved
    - Guards against: avX/Y/Z key mismatch

17. **`Serialization_MissingOfrKeys_DefaultsToZero`**
    - Old-format segment without ofr keys
    - Expected: `orbitalFrameRotation = (0,0,0,0)`, `HasOrbitalFrameRotation` false
    - Guards against: crash on missing keys

18. **`Serialization_MissingAvKeys_DefaultsToZero`**
    - Segment without av keys
    - Expected: `angularVelocity = (0,0,0)`, `IsSpinning` false
    - Guards against: crash on missing angular velocity keys

19. **`Serialization_WithOfrKeys_ParsesCorrectly`**
    - ConfigNode with specific ofrX/Y/Z/W values
    - Expected: parsed values match exactly
    - Guards against: key name typos, parse order bugs

### Edge Case Tests (OrbitSegmentTests)

20. **`HasOrbitalFrameRotation_NegativeComponents_ReturnsTrue`**
    - Input: `(-0.5, 0, 0, 0.866)`
    - Expected: `true`
    - Guards against: sentinel failing for negative values

21. **`SpinForward_HighAngularVelocity_NoOverflow`**
    - Input: angular velocity = (5, 0, 0) rad/s, dt = 1000s
    - Expected: valid quaternion, no overflow/NaN
    - Guards against: numerical issues at extreme spin rates

### Integration Tests (SyntheticRecordingTests)

22. **Update `Orbit1` synthetic recording**
    - Add `ofrY=1` to `AddOrbitSegment` (retrograde attitude)
    - Assert `ofrY` key present in serialized ConfigNode
    - Guards against: end-to-end pipeline failure

23. **Add `Orbit1` variant with angular velocity**
    - Test with `avX=0.1` (spinning)
    - Assert `avX` key present in serialized ConfigNode
    - Guards against: angular velocity serialization pipeline failure

### Log Assertion Tests (OrbitSegmentTests)

24. **`ComputeOrbitalFrameRotation_ZeroVelocity_LogsDegenerate`**
    - Capture log output via test sink
    - Expected: log contains "degenerate velocity"
    - Guards against: silent degenerate case handling

25. **`ComputeOrbitalFrameRotation_NearParallel_LogsWarning`**
    - Capture log output via test sink
    - Expected: log contains "near-parallel"
    - Guards against: silent fallback

## Implementation Phases

### Phase 1: Data Model + Serialization + Tests

**Files:** `OrbitSegment.cs`, `TrajectoryMath.cs`, `RecordingStore.cs`, `RecordingBuilder.cs`, `OrbitSegmentTests.cs`

1. Add `orbitalFrameRotation` and `angularVelocity` fields to `OrbitSegment`
2. Add `HasOrbitalFrameRotation`, `IsSpinning`, `ComputeOrbitalFrameRotation`, `SpinThreshold` to `TrajectoryMath`
3. Serialize/deserialize `ofrX/Y/Z/W` and `avX/Y/Z` in `RecordingStore`
4. Add optional params to `RecordingBuilder.AddOrbitSegment`
5. Write tests 1-21, 24-25
6. Update `Orbit1` synthetic recording (tests 22-23)

**Verification:** `dotnet build` + `dotnet test --filter OrbitSegmentTests` + `dotnet test --filter Orbit1`

### Phase 2: Recording Side

**Files:** `FlightRecorder.cs`

1. Add `hasPersistentRotation` detection at recording start
2. Capture orbital-frame rotation in `OnVesselGoOnRails`
3. Capture angular velocity (when PersistentRotation detected + spinning) in `OnVesselGoOnRails`
4. Same for `OnVesselSOIChanged`
5. Add diagnostic logging

**Verification:** `dotnet build`

### Phase 3: Playback Side

**Files:** `ParsekFlight.cs`

1. Extend `GhostPosEntry` struct with rotation + spin fields
2. Update `PositionGhostFromOrbit` with orbital-frame rotation + spin-forward logic
3. Update `LateUpdate` orbit case with matching logic
4. Add diagnostic logging

**Verification:** `dotnet build` + in-game testing

### Phase 4: Documentation

**Files:** `docs/dev/known-bugs.md`

1. Update bug #16 status to "Fixed" with summary
2. Replace the outdated angular-momentum implementation plan

## Known Limitations (v1)

1. **Multi-axis tumble fidelity**: The `AngleAxis` spin-forward is a single-axis approximation. For vessels tumbling around multiple axes simultaneously, the ghost's spin may diverge slightly from PersistentRotation's iterative approach. The common case (single-axis spin) is exact.
2. **PersistentRotation RELATIVE mode approximation**: Vessels in StabilityAssist with PersistentRotation's body-relative mode active use our orbital-frame-relative approach, which closely approximates body-relative lock for near-circular orbits but may diverge slightly for highly eccentric orbits.
3. **Planetarium.right drift**: Very long orbital segments (interplanetary transfers) may accumulate minor inertial reference frame mismatch. Phase 6 (empirical measurement needed).

## Future Phase 6: Planetarium.right Drift Compensation

Measure `Planetarium.right` drift over long warp durations. If > 1 degree, store `Planetarium.right` snapshot at segment start and apply drift correction at playback. Requires empirical measurement before implementation.
