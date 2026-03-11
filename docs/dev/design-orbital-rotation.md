# Design: Orbital Rotation Fidelity

## Problem

When a vessel goes on rails during orbital flight (time warp), Parsek records the Keplerian orbital elements but not the vessel's attitude. During playback, the ghost's rotation is derived from the orbital velocity vector (`Quaternion.LookRotation(velocity)`), making every vessel appear to face prograde. A vessel holding retrograde, normal, radial, or any other SAS orientation will appear prograde-locked during the orbital segment. This is visually wrong and breaks the fidelity of mission replay.

### Relationship to Known-Bugs #16

Known-bugs.md #16 documents an earlier analysis (based on PersistentRotation mod research) that recommended storing angular momentum and `Planetarium.Rotation`. After further review, that approach was rejected for v1 because:

1. **Angular momentum requires MOI** to convert to angular velocity (`omega = L/I`). MOI is unavailable on ghosts (no rigidbody), so playback can't use `angularMomentum` without also storing MOI or pre-computing `angularVelocity`.
2. **The spin-forward formula is wrong for multi-axis tumble.** `AngleAxis(omega.mag * dt, axis) * storedRot` is a single-axis approximation that misses precession.
3. **`Planetarium.right` drift compensation** adds complexity whose value is unclear without empirical measurement.

Orbital-frame-relative rotation handles the primary use case (SAS-locked attitudes) perfectly with 4 floats and zero reference-frame issues. Free-spinning vessels are deferred to Phase 5, `Planetarium.right` drift to Phase 6 -- both contingent on empirical need.

This design replaces the implementation plan in known-bugs.md #16 and resolves its "Open" status. The format v6 version bump mentioned there is not needed (see Backward Compatibility).

## Terminology

- **Orbital frame**: The reference frame defined by the velocity vector (forward/prograde), the radial-out vector (up), and their cross product (normal). Changes continuously as the vessel orbits.
- **Orbital-frame-relative rotation**: The vessel's rotation expressed relative to the orbital frame. Identity = vessel facing prograde with "up" toward radial-out.
- **On-rails boundary**: The moment KSP transitions a vessel from physics simulation to Keplerian propagation (going on rails) or vice versa (going off rails).

## Mental Model

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

The stored rotation is a constant offset from the orbital frame. As the orbital frame rotates around the orbit, the ghost maintains the same attitude relative to it. A vessel holding retrograde at the boundary will appear to hold retrograde throughout the segment. A vessel holding normal will track normal as it orbits.

**Quaternion multiplication order:** `Inverse(orbFrame) * worldRot` is the correct encoding because it expresses `worldRot` in the coordinate system defined by `orbFrame`. The decoding `orbFrame' * storedRot` reverses this: it takes the stored offset and re-expresses it in world space using the new orbital frame. This is the standard "change of basis" pattern for quaternions.

**`LookRotation` degenerate case guard:** Unity's `LookRotation(forward, up)` degenerates when `forward` and `up` are near-parallel. For orbital mechanics, velocity is tangential and radial-out is perpendicular to the orbit, so they are always ~90 degrees apart for circular/elliptical orbits. For hyperbolic orbits near periapsis or at extreme eccentricities, the angle between velocity and radial-out varies but remains well-separated. However, as a safety measure, `ComputeOrbitalFrameRotation` checks `Vector3.Dot(velocity.normalized, radialOut.normalized)` and falls back to `LookRotation(velocity)` (without the `up` hint) if the dot product exceeds 0.99 (vectors within ~8 degrees of parallel). This produces a less-precise frame but avoids numerical instability.

This is correct for the dominant use case: SAS-locked attitudes. For free-spinning vessels, the ghost holds its boundary attitude (constant, not spinning) -- an acceptable v1 limitation.

## Gameplay Simulation

### Scenario 1: Retrograde Station-Keeping in Low Orbit

The player launches a science satellite into low Kerbin orbit with SAS locked to retrograde (heat shield forward for planned de-orbit). They time-warp several orbits to wait for a transfer window.

**Recording:** When time warp starts, `OnVesselGoOnRails` fires. The recorder captures Keplerian elements and the vessel's rotation relative to the orbital frame. Since the vessel faces retrograde, the stored offset is a 180-degree yaw from prograde.

**Playback:** The ghost orbits Kerbin. At every point along the orbit, the orbital frame is reconstructed from the Keplerian elements, and the 180-degree yaw offset is applied. The ghost consistently faces retrograde -- heat shield pointing into the velocity vector -- exactly matching what the player saw during recording.

**Without this feature:** The ghost would face prograde at all times, heat shield trailing. Visually wrong.

### Scenario 2: Normal-Hold During Plane Change Warp

The player locks SAS to normal (perpendicular to orbital plane, pointing "up" relative to the orbit) and warps to the ascending node for a plane change burn.

**Recording:** At the on-rails boundary, the recorder captures the normal-relative attitude. Since the vessel points along the normal vector, the stored offset captures this.

**Playback:** As the ghost orbits, the orbital frame rotates (prograde and radial-out change continuously). The normal vector also rotates with the orbit. The ghost maintains its normal-hold attitude relative to the changing orbital frame, matching the in-game behavior.

### Scenario 3: Old Recording Without Orbital Rotation Data

The player loads a save with recordings made before this feature existed. These recordings have orbital segments but no `ofrX/Y/Z/W` keys.

**Playback:** The deserialization finds no `ofr` keys, so `orbitalFrameRotation` stays at the struct default `(0,0,0,0)`. `HasOrbitalFrameRotation` returns false. The playback code falls back to the current behavior: `LookRotation(velocity)` -- prograde-only.

**Result:** Identical to current behavior. No regression, no crash, no migration needed.

### Scenario 4: New Recording on Old Parsek Version

The player downgrades Parsek or shares a recording with someone on an older version. The recording has `ofrX/Y/Z/W` keys in the ORBIT_SEGMENT ConfigNode (present when any component is non-zero; absent for prograde recordings).

**Loading:** Old Parsek ignores unknown ConfigNode keys. The orbital elements load normally. The `ofrX/Y/Z/W` keys are silently skipped.

**Playback:** Old code uses velocity-derived prograde rotation as before.

**Result:** No crash, graceful degradation. The vessel just faces prograde instead of the recorded attitude.

### Scenario 5: SOI Change During Orbital Warp

The player warps from Kerbin orbit to Mun encounter. KSP fires `OnVesselSOIChanged`. The recorder closes the Kerbin segment and opens a new Mun segment.

**Recording:** The closed Kerbin segment already has its orbital-frame rotation (captured at `OnVesselGoOnRails`). For the new Mun segment, rotation is captured from `v.transform.rotation` and `v.obt_velocity` at SOI change time.

**Reliability of `v.transform.rotation` during SOI change:** KSP fires `OnVesselSOIChanged` while the vessel is on rails. On-rails vessels maintain a valid `transform.rotation` that KSP updates each frame from the Keplerian propagation (it reflects the vessel's attitude as of the last physics frame before going on rails). The rotation is therefore the same attitude the vessel had when it went on rails, which is exactly what we want to capture. This is confirmed by PersistentRotation's approach, which also reads `vessel.transform.rotation` during on-rails events.

**Playback:** When the ghost transitions from the Kerbin segment to the Mun segment, it switches to the Mun orbital frame with the captured attitude offset. The ghost maintains its attitude through the SOI transition.

### Scenario 6: Free-Spinning Vessel Going On Rails

The player disables SAS and lets the vessel tumble freely, then warps. The vessel has significant angular velocity at the on-rails boundary.

**Recording:** The recorder captures the instantaneous attitude at the on-rails boundary as orbital-frame-relative rotation. Angular velocity is NOT recorded (Phase 5 future work).

**Playback:** The ghost holds the boundary attitude throughout the orbital segment. It does not spin.

**Acceptable v1 limitation:** The ghost appears frozen at its boundary attitude. This is visually imperfect but acceptable -- most orbital segments have SAS active. Free-spinning support requires Phase 5 (angular velocity recording).

### Scenario 7: Near-Zero Velocity at Orbital Apex

Edge case: a suborbital vessel at the apex of its trajectory, where velocity approaches zero. The orbital frame becomes degenerate (can't define prograde from a zero velocity vector).

**Recording:** `ComputeOrbitalFrameRotation` checks `velocity.sqrMagnitude < 0.001`. If velocity is near-zero, returns `Quaternion.identity` as the orbital-frame rotation (degenerate case -- can't define the frame).

**Playback:** If `HasOrbitalFrameRotation` is true (data was stored) but the playback velocity is near-zero, the `velocity.sqrMagnitude > 0.001` guard skips rotation entirely (same as current behavior). If velocity is non-zero but the recorded rotation was identity (degenerate recording), the ghost faces prograde -- which is the best approximation when no frame could be defined.

### Scenario 8: Chain Recording with Orbital Segment

A chain recording where one segment is atmospheric flight and the next is orbital. The atmospheric segment uses surface-relative rotation (v5). The orbital segment uses orbital-frame-relative rotation.

**Recording:** Each segment type records rotation in its own frame. Surface segments use `v.srfRelRotation`. Orbital segments use the new orbital-frame-relative approach. No interaction between them.

**Playback:** Point-based interpolation (atmospheric segments) uses `bodyTransform.rotation * storedRot`. Orbit-based playback uses `orbFrame * storedRot`. Each path is independent; the ghost transitions between them as the timeline progresses through different segments.

### Scenario 9: Recording Stopped Mid-Orbit

The player presses Stop while the vessel is on rails (time warping in orbit). `StopRecording` is called while `isOnRails` is true.

**Recording:** `StopRecording` already finalizes the current orbit segment: sets `endUT`, adds the segment to `OrbitSegments`, and resets `isOnRails`. The orbital-frame rotation was captured at the earlier `OnVesselGoOnRails` and is already stored in `currentOrbitSegment.orbitalFrameRotation`. No additional logic needed.

**Playback:** The segment plays back normally with the captured attitude. The segment simply ends where the recording was stopped.

### Scenario 10: Background-Only Recording (Orbit-Only Path)

A background vessel that stayed on rails for its entire recording. It has orbit segments but no trajectory points. Played back via `PositionGhostFromOrbitOnly`.

**Playback:** `PositionGhostFromOrbitOnly` iterates orbit segments and calls `PositionGhostFromOrbit` for the matching segment. Since `PositionGhostFromOrbit` is the shared code that applies orbital-frame rotation, background-only recordings automatically get correct attitude without any additional changes to `PositionGhostFromOrbitOnly`.

### Scenario 11: Looped Recording with Orbital Segment

A recording with `LoopPlayback = true` that includes an orbital segment. The loop system remaps time using `loopUT = startUT + ((currentUT - startUT) % duration)`.

**Playback:** The remapped UT is passed to `PositionGhostFromOrbit` via the normal timeline playback path. `FindOrbitSegment` uses the remapped UT to find the active segment. The orbital-frame rotation is applied identically on each loop iteration. No special handling needed -- the UT remapping is transparent to the orbit positioning code.

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

    // New field
    public Quaternion orbitalFrameRotation;  // relative to orbital velocity frame; identity = prograde
}
```

Default `Quaternion` struct value is `(0,0,0,0)` -- an invalid quaternion that serves as the sentinel for "no rotation data recorded."

### New Static Methods (TrajectoryMath)

```csharp
/// Returns true if the segment has recorded orbital-frame rotation data.
/// Default (0,0,0,0) = no data.
internal static bool HasOrbitalFrameRotation(OrbitSegment seg)
    => seg.orbitalFrameRotation.x != 0f || seg.orbitalFrameRotation.y != 0f
    || seg.orbitalFrameRotation.z != 0f || seg.orbitalFrameRotation.w != 0f;

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
    ofrX = 0          // new, optional
    ofrY = 1          // new, optional
    ofrZ = 0          // new, optional
    ofrW = 0          // new, optional
}
```

Keys `ofrX/Y/Z/W` are only written when any component is non-zero (i.e., when rotation data exists). Missing keys default to `(0,0,0,0)` on deserialization. All float values serialized with `ToString("R", CultureInfo.InvariantCulture)` and parsed with `float.TryParse(..., NumberStyles.Float, CultureInfo.InvariantCulture, ...)`, matching the project's established pattern for locale-safe serialization.

**No format version bump.** Known-bugs.md #16 previously labeled this as a "format v6 candidate." After analysis, a version bump is not needed: missing keys default to the sentinel, and old Parsek ignores unknown keys. Fully backward and forward compatible without a version change.

### GhostPosEntry (extended)

```csharp
private struct GhostPosEntry
{
    // Existing fields...

    // New orbit rotation fields
    public Quaternion orbitFrameRot;       // Orbital-frame-relative rotation from segment
    public bool hasOrbitFrameRot;          // True if segment has rotation data
    public CelestialBody orbitBody;        // Body reference for radial-out computation in LateUpdate
}
```

Note: `orbitBody` stores a `CelestialBody` reference (not a string name) to avoid per-frame `FlightGlobals.Bodies.Find` lookups in LateUpdate. This follows the existing pattern of `bodyBefore`/`bodyAfter` fields on the same struct.

### Required Code Changes (RecordingBuilder)

`RecordingBuilder.AddOrbitSegment` gains optional `ofrX/Y/Z/W` float parameters (default 0). When any is non-zero, the corresponding ConfigNode keys are written. This enables synthetic recordings to exercise orbital-frame rotation through the full pipeline.

## Behavior

### Recording: On-Rails Boundary (`FlightRecorder.OnVesselGoOnRails`)

When the active vessel goes on rails:
1. Record a boundary TrajectoryPoint (existing behavior)
2. Capture Keplerian orbital elements (existing behavior)
3. **New:** Compute orbital-frame-relative rotation from `v.obt_velocity`, `(v.position - body.position).normalized`, and `v.transform.rotation` via `TrajectoryMath.ComputeOrbitalFrameRotation`
4. Store in `currentOrbitSegment.orbitalFrameRotation`

- Log: `Verbose("Recorder", "Vessel went on rails -- capturing orbit segment (body={body}, ofrRot={rotation})")`
- Log (degenerate velocity): `Verbose("Recorder", "Orbital-frame rotation: degenerate velocity (sqrMag={mag}), using identity")`
- Log (near-parallel guard): `Verbose("Recorder", "Orbital-frame rotation: velocity/radialOut near-parallel (dot={dot}), frame approximated")`

### Recording: SOI Change (`FlightRecorder.OnVesselSOIChanged`)

When SOI changes during on-rails flight:
1. Close current segment (existing behavior)
2. Open new segment with new body's orbital elements (existing behavior)
3. **New:** Compute orbital-frame-relative rotation for the new segment from `v.transform.rotation` and `v.obt_velocity` relative to the new body

- Log: `Verbose("Recorder", "SOI changed {from} -> {to} -- new segment ofrRot={rotation}")`

### Playback: `PositionGhostFromOrbit` (shared by both timeline and orbit-only paths)

When positioning a ghost from an orbital segment:
1. Compute world position from orbit (existing behavior)
2. Look up `CelestialBody` by `segment.bodyName` (existing behavior)
3. **New rotation logic:**
   - If `HasOrbitalFrameRotation(segment)` is true AND `velocity.sqrMagnitude > 0.001`:
     - Compute `radialOut = (worldPos - body.position).normalized`
     - Guard: if `Dot(velocity.normalized, radialOut) > 0.99`, fall back to `LookRotation(velocity)`
     - Else: compute `orbFrame = LookRotation(velocity, radialOut)`, set `ghost.rotation = orbFrame * segment.orbitalFrameRotation`
   - Else (old recording, no data, or degenerate velocity):
     - Fallback: `ghost.rotation = LookRotation(velocity)` (existing behavior)
4. Populate `GhostPosEntry` with new fields for LateUpdate re-application, including `orbitBody` reference (the `CelestialBody` already looked up in step 2)

- Log (first time per segment, via `loggedOrbitSegments`): `Verbose("Playback", "Orbit segment {cacheKey}: using orbital-frame rotation {rotation}")` or `Verbose("Playback", "Orbit segment {cacheKey}: using velocity-derived prograde (no ofr data)")`

**Note:** `PositionGhostFromOrbitOnly` (for background-only recordings) delegates to `PositionGhostFromOrbit` -- no changes needed in `PositionGhostFromOrbitOnly` itself.

### Playback: `InterpolateAndPosition` Orbit Branch

The main timeline playback path calls `PositionGhostFromOrbit` from within `InterpolateAndPosition` (around line 7005-7027). This path finds the active orbit segment via `FindOrbitSegment` and passes it to `PositionGhostFromOrbit`. No changes needed to `InterpolateAndPosition` itself -- the new rotation logic is encapsulated in `PositionGhostFromOrbit`.

### Playback: `LateUpdate` Orbit Case

The LateUpdate re-positioning after FloatingOrigin shift must also apply the orbital-frame rotation:
1. Compute position from orbit (existing behavior)
2. **New rotation logic** (mirrors `PositionGhostFromOrbit`):
   - If `e.hasOrbitFrameRot` AND `velocity.sqrMagnitude > 0.001`:
     - Use `e.orbitBody` reference (no string lookup needed -- already resolved)
     - If `e.orbitBody` is null (destroyed between frames): fall back to `LookRotation(velocity)`
     - Compute `radialOut = (pos - orbitBody.position).normalized`
     - Guard: near-parallel check, same as `PositionGhostFromOrbit`
     - Apply `orbFrame * e.orbitFrameRot`
   - Else: fallback to `LookRotation(velocity)`

- Log (body null): `Verbose("Playback", "Orbit LateUpdate: orbitBody null for cache={cacheKey}, using velocity fallback")`

## Edge Cases

### 1. Old recording without `ofr` keys

**Scenario:** Recording created before this feature, deserialized by new Parsek.

**Expected:** `orbitalFrameRotation = (0,0,0,0)`, `HasOrbitalFrameRotation` returns false, prograde fallback. Identical to current behavior.

**Status:** Handled in v1

### 2. New recording on old Parsek

**Scenario:** Recording with `ofrX/Y/Z/W` keys loaded by old Parsek version.

**Expected:** Old code ignores unknown ConfigNode keys, plays back as prograde. No crash, graceful degradation.

**Status:** Handled by ConfigNode design

### 3. Zero velocity at recording time

**Scenario:** Suborbital apex or nearly-stopped vessel goes on rails.

**Expected:** `ComputeOrbitalFrameRotation` detects `sqrMagnitude < 0.001`, returns identity. Logged as degenerate case.

**Status:** Handled in v1

### 4. Zero velocity at playback time

**Scenario:** Ghost passes through orbital apex during playback where velocity crosses zero.

**Expected:** `velocity.sqrMagnitude > 0.001` guard skips rotation -- ghost keeps previous frame's rotation. Same as current behavior.

**Status:** Handled in v1

### 5. Free-spinning vessel

**Scenario:** Vessel tumbling with significant angular velocity goes on rails.

**Expected:** Ghost holds boundary attitude throughout segment (constant, not spinning).

**Status:** Acceptable v1 limitation (Phase 5)

### 6. SAS attitude change during warp

**Scenario:** Player locks SAS to retrograde, then warps. Does KSP adjust attitude during warp?

**Expected:** Ghost holds initial boundary attitude. KSP doesn't change vessel attitude during on-rails warp -- SAS orientation is frozen. So the stored boundary attitude IS the correct attitude for the entire segment.

**Status:** Non-issue

### 7. SOI transition

**Scenario:** Vessel warps from Kerbin to Mun, SOI changes.

**Expected:** New segment captures attitude relative to new body's orbital frame. `v.transform.rotation` is valid during on-rails SOI events (KSP maintains it from last physics frame).

**Status:** Handled in v1

### 8. Body not found during LateUpdate

**Scenario:** `e.orbitBody` is null (body destroyed or reference invalidated between frames).

**Expected:** Falls back to `LookRotation(velocity)`. Logged.

**Status:** Handled in v1

### 9. Very long orbital segment (interplanetary)

**Scenario:** Multi-day interplanetary transfer with a single orbit segment.

**Expected:** `Planetarium.right` drift may cause minor inertial frame mismatch over very long durations. The orbital frame itself is Keplerian and exact; only the inertial-to-world mapping could drift.

**Status:** Acceptable v1 limitation (Phase 6 -- needs empirical measurement first)

### 10. Degenerate orbit (ecc >= 1, hyperbolic)

**Scenario:** Vessel on escape trajectory or hyperbolic flyby.

**Expected:** Orbital frame is still well-defined from velocity + radial-out. Velocity is always non-zero on hyperbolic orbits (minimum at infinity approach). Works normally.

**Status:** Handled in v1

### 11. Multiple orbit segments with different attitudes

**Scenario:** Recording with 3 orbit segments (e.g., Kerbin orbit -> Mun encounter -> Mun orbit), each captured with different SAS orientations.

**Expected:** Each segment stores its own `orbitalFrameRotation`. Playback uses the active segment's rotation. `FindOrbitSegment` returns the correct segment for the current UT.

**Status:** Handled in v1

### 12. Near-parallel velocity and radial-out

**Scenario:** Highly eccentric orbit where velocity direction approaches radial direction (theoretically possible near certain orbital geometries).

**Expected:** `ComputeOrbitalFrameRotation` checks `Dot(velocity.normalized, radialOut.normalized) > 0.99`. If near-parallel, falls back to `LookRotation(velocity)` without the `up` hint (less precise frame but numerically stable). Logged.

**Status:** Handled in v1

### 13. Recording stopped mid-orbit

**Scenario:** Player presses Stop while vessel is on rails (time warping).

**Expected:** `StopRecording` finalizes the current orbit segment (sets `endUT`, adds to list). The orbital-frame rotation was already captured at the earlier `OnVesselGoOnRails`. The segment plays back normally up to the stop UT.

**Status:** Handled in v1 (existing StopRecording logic, no changes needed)

### 14. Looped recording with orbital segment

**Scenario:** Recording with orbital segment and `LoopPlayback = true`. Loop remaps UT via modular arithmetic.

**Expected:** Remapped UT is passed through the normal orbit positioning path. `FindOrbitSegment` matches the remapped UT to the correct segment. Orbital-frame rotation applies identically on each loop iteration.

**Status:** Handled in v1 (transparent to orbit code)

### 15. Background-only recording (PositionGhostFromOrbitOnly)

**Scenario:** A background vessel that stayed on rails, played back via the `PositionGhostFromOrbitOnly` path instead of the normal timeline path.

**Expected:** `PositionGhostFromOrbitOnly` delegates to `PositionGhostFromOrbit`, which contains the new rotation logic. Orbital-frame rotation is applied automatically.

**Status:** Handled in v1 (no changes to PositionGhostFromOrbitOnly needed)

### 16. Vessel destruction during orbital recording

**Scenario:** Vessel is destroyed while on rails (e.g., enters atmosphere from orbit and burns up during warp).

**Expected:** `OnVesselWillDestroy` checks `isOnRails`, finalizes the orbit segment with `endUT = currentUT`, and adds it to `OrbitSegments`. The orbital-frame rotation was already captured. Existing code handles this; no changes needed.

**Status:** Handled in v1 (existing OnVesselWillDestroy logic)

## What Doesn't Change

- **Surface/atmospheric recording and playback**: Unchanged. v5 surface-relative rotation path is completely independent.
- **Point-based interpolation**: Unchanged. Only the orbit-segment playback path is modified.
- **Recording format version**: Stays at v5. No migration. No version bump.
- **Serialization of non-orbital data**: All other ConfigNode keys (points, part events, metadata) are untouched.
- **Ghost visual building**: Unaffected. This feature changes rotation, not mesh/visual construction.
- **UI**: No user-facing changes. The feature is transparent -- recordings automatically include orbital attitude data.
- **Existing tests**: All existing tests remain valid. No behavioral change for recordings without orbital rotation data.
- **`PositionGhostFromOrbitOnly`**: Not modified. It delegates to `PositionGhostFromOrbit` which contains the new logic.
- **`InterpolateAndPosition`**: Not modified. It calls `PositionGhostFromOrbit` which contains the new logic.

## Backward Compatibility

| Scenario | Behavior |
|----------|----------|
| Old recording (no `ofr` keys) on new Parsek | `(0,0,0,0)` sentinel detected, prograde fallback. **Identical to current.** |
| New recording (with `ofr` keys) on old Parsek | Old code ignores unknown ConfigNode keys. Plays back as prograde. **No crash, graceful degradation.** |
| New recording on new Parsek | Uses stored orbital-frame rotation. **Correct attitude.** |

No format version bump. No migration needed. No breaking changes.

## Diagnostic Logging

### Recording Side

| Decision Point | Log Line | Level |
|----------------|----------|-------|
| Orbital-frame rotation captured at on-rails boundary | `"Vessel went on rails -- capturing orbit segment (body={body}, ofrRot={rotation})"` | Verbose |
| Orbital-frame rotation captured at SOI change | `"SOI changed {from} -> {to} -- new segment ofrRot={rotation}"` | Verbose |
| Degenerate velocity at recording (near-zero) | `"Orbital-frame rotation: degenerate velocity (sqrMag={mag}), using identity"` | Verbose |
| Near-parallel velocity/radial-out at recording | `"Orbital-frame rotation: velocity/radialOut near-parallel (dot={dot}), frame approximated"` | Verbose |

### Playback Side

| Decision Point | Log Line | Level |
|----------------|----------|-------|
| Segment has orbital-frame rotation (first activation) | `"Orbit segment {cacheKey}: using orbital-frame rotation {rotation}"` | Verbose |
| Segment lacks orbital-frame rotation (first activation) | `"Orbit segment {cacheKey}: using velocity-derived prograde (no ofr data)"` | Verbose |
| Near-parallel guard triggered at playback | `"Orbit segment {cacheKey}: velocity/radialOut near-parallel, using LookRotation fallback"` | Verbose |
| Body null in LateUpdate | `"Orbit LateUpdate: orbitBody null for cache={cacheKey}, using velocity fallback"` | Verbose |

## Test Plan

### Unit Tests (TrajectoryMath) -- in `OrbitSegmentTests.cs`

1. **`HasOrbitalFrameRotation_DefaultSegment_ReturnsFalse`**
   - Input: default `OrbitSegment` (all fields zero-initialized)
   - Expected: `false`
   - Guards against: treating default (0,0,0,0) as valid rotation data, which would apply an invalid quaternion

2. **`HasOrbitalFrameRotation_WithRotation_ReturnsTrue`**
   - Input: segment with `orbitalFrameRotation = (0, 1, 0, 0)` (retrograde)
   - Expected: `true`
   - Guards against: sentinel check being too aggressive (rejecting valid rotations that happen to have zero in some components)

3. **`HasOrbitalFrameRotation_IdentityQuaternion_ReturnsTrue`**
   - Input: segment with `orbitalFrameRotation = (0, 0, 0, 1)` (identity = prograde)
   - Expected: `true` (w=1 is non-zero)
   - Guards against: confusing "prograde attitude" (identity, valid) with "no data" (all-zero, invalid)

4. **`HasOrbitalFrameRotation_AllZero_ReturnsFalse`**
   - Input: `orbitalFrameRotation = (0, 0, 0, 0)` explicitly assigned
   - Expected: `false`
   - Guards against: the sentinel not being correctly detected after explicit assignment

5. **`ComputeOrbitalFrameRotation_Prograde_ReturnsIdentity`**
   - Input: vessel rotation = LookRotation(velocity, radialOut), i.e., vessel facing prograde
   - Expected: identity quaternion (within floating-point tolerance)
   - Guards against: incorrect frame computation that doesn't cancel out when vessel matches the frame

6. **`ComputeOrbitalFrameRotation_Retrograde_Returns180Yaw`**
   - Input: vessel rotation = LookRotation(-velocity, radialOut), i.e., vessel facing retrograde
   - Expected: 180-degree rotation around the up axis
   - Guards against: incorrect Inverse(frame) * rotation ordering

7. **`ComputeOrbitalFrameRotation_ZeroVelocity_ReturnsIdentity`**
   - Input: velocity = (0, 0, 0)
   - Expected: identity quaternion
   - Guards against: division by zero or NaN from degenerate velocity

8. **`ComputeOrbitalFrameRotation_NearParallelVectors_NoNaN`**
   - Input: velocity and radialOut nearly parallel (dot product > 0.99)
   - Expected: valid quaternion (no NaN components), falls back to approximate frame
   - Guards against: LookRotation numerical instability when forward/up are near-parallel

9. **`ComputeOrbitalFrameRotation_RoundTrip`**
   - Input: arbitrary world rotation, arbitrary velocity/radial-out (perpendicular)
   - Encode: `storedRot = Inverse(orbFrame) * worldRot`
   - Decode: `reconstructed = orbFrame * storedRot`
   - Expected: `reconstructed` matches original `worldRot` (within tolerance)
   - Guards against: encode/decode asymmetry, quaternion ordering bugs

### Serialization Tests (OrbitSegmentTests)

10. **`Serialization_RoundTrip_PreservesOrbitalFrameRotation`**
    - Build a recording with orbit segment including ofr values via `RecordingBuilder`, serialize via `SerializeTrajectoryInto`, deserialize via `DeserializeTrajectoryFrom`
    - Expected: deserialized segment has matching `orbitalFrameRotation`
    - Guards against: serialization key mismatch, locale-dependent float formatting, `ToString("R")` / `TryParse` round-trip loss

11. **`Serialization_MissingOfrKeys_DefaultsToZero`**
    - Build a recording with orbit segment WITHOUT ofr values (old format), deserialize
    - Expected: `orbitalFrameRotation = (0,0,0,0)`, `HasOrbitalFrameRotation` returns false
    - Guards against: deserialization crash on missing keys, wrong default values

12. **`Serialization_WithOfrKeys_ParsesCorrectly`**
    - Manually construct ConfigNode with specific ofrX/Y/Z/W values, deserialize
    - Expected: parsed values match input exactly
    - Guards against: key name typos, wrong parse order (X/Y/Z/W mixup)

### Edge Case Tests (OrbitSegmentTests)

13. **`ComputeOrbitalFrameRotation_BodyNotFound_FallbackSafe`**
    - Verify that playback logic produces a valid rotation even when body lookup returns null
    - Guards against: NullReferenceException in LateUpdate when body is unavailable

14. **`HasOrbitalFrameRotation_NegativeComponents_ReturnsTrue`**
    - Input: segment with `orbitalFrameRotation = (-0.5, 0, 0, 0.866)` (30-degree rotation)
    - Expected: `true`
    - Guards against: sentinel check failing for negative component values

### Integration Tests (SyntheticRecordingTests)

15. **Update `Orbit1` synthetic recording**
    - Add `ofrX/Y/Z/W` params to the existing `AddOrbitSegment` call (retrograde: `ofrY=1`)
    - Update `Orbit1_HasOrbitSegmentAndSnapshot` test to assert `ofrY` key is present in serialized ConfigNode
    - Guards against: end-to-end pipeline failure (builder -> ConfigNode -> .sfs injection -> deserialization)

### Log Assertion Tests (OrbitSegmentTests)

16. **`ComputeOrbitalFrameRotation_ZeroVelocity_LogsDegenerate`**
    - Input: zero velocity vector
    - Capture log output via test sink
    - Expected: log contains "degenerate velocity"
    - Guards against: silent degenerate case handling (loss of diagnostic observability)

17. **`ComputeOrbitalFrameRotation_NearParallel_LogsWarning`**
    - Input: velocity and radialOut nearly parallel
    - Capture log output via test sink
    - Expected: log contains "near-parallel"
    - Guards against: silent fallback that hides potential frame quality issues

## Implementation Phases

### Phase 1: Data Model + Serialization + Tests

**Files:** `OrbitSegment.cs`, `TrajectoryMath.cs`, `RecordingStore.cs`, `RecordingBuilder.cs`, `OrbitSegmentTests.cs`

1. Add `orbitalFrameRotation` field to `OrbitSegment`
2. Add `HasOrbitalFrameRotation` and `ComputeOrbitalFrameRotation` to `TrajectoryMath`
3. Serialize/deserialize `ofrX/Y/Z/W` in `RecordingStore`
4. Add optional params to `RecordingBuilder.AddOrbitSegment`
5. Write tests 1-14 above
6. Update `Orbit1` synthetic recording (test 15)

**Verification:** `dotnet build` + `dotnet test --filter OrbitSegmentTests` + `dotnet test --filter Orbit1`

### Phase 2: Recording Side

**Files:** `FlightRecorder.cs`

1. Capture orbital-frame rotation in `OnVesselGoOnRails`
2. Capture orbital-frame rotation in `OnVesselSOIChanged`
3. Add diagnostic logging

**Verification:** `dotnet build` (no new unit tests -- requires KSP runtime)

### Phase 3: Playback Side

**Files:** `ParsekFlight.cs`

1. Extend `GhostPosEntry` struct with `orbitFrameRot`, `hasOrbitFrameRot`, `orbitBody`
2. Update `PositionGhostFromOrbit` with new rotation logic
3. Update `LateUpdate` orbit case with matching logic
4. Add diagnostic logging

**Verification:** `dotnet build` + in-game testing

### Phase 4: Documentation

**Files:** `docs/dev/known-bugs.md`

1. Update bug #16 status to "Fixed (v1)" with summary
2. Remove the outdated implementation plan (angular momentum approach)

## Known Limitations (v1)

1. **Free-spinning vessels**: Appear at a fixed attitude (whatever they had at the on-rails boundary), not spinning. Requires Phase 5 (angular velocity recording).
2. **Attitude changes during warp**: Ghost holds the initial boundary attitude. This is actually correct because KSP doesn't change vessel attitude during warp -- SAS holds the locked orientation.
3. **Planetarium.right drift**: Very long orbital segments (interplanetary transfers) may accumulate minor inertial reference frame drift. Requires Phase 6 (empirical measurement needed to determine if this is significant).

## Future Phases

### Phase 5: Angular Velocity for Spinning Vessels

Add `angularVelocity` (Vector3) and `isSpinning` (bool) fields to `OrbitSegment`. Record `v.angularVelocity` at on-rails boundaries when magnitude exceeds threshold (~0.05 rad/s). Playback: spin-forward simulation using `AngleAxis(omega * dt, axis) * boundaryRot`. This is a single-axis approximation that's correct for the common case (single-axis spin) but misses precession for multi-axis tumble.

### Phase 6: Planetarium.right Drift Compensation

Measure `Planetarium.right` drift over long warp durations. If > 1 degree, store `Planetarium.right` snapshot at segment start and apply drift correction at playback. Requires empirical measurement before implementation.
