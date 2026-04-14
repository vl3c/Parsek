# Ghost Map Icon Position Fix (#172) - Decompilation Analysis

Investigation of why ghost ProtoVessel MapNode click targets diverge from ghost mesh positions in map view/tracking station. Decompiled from `Assembly-CSharp.dll` (KSP 1.12.x).

See also: [ksp-map-presence-api-decompilation.md](ksp-map-presence-api-decompilation.md) for ProtoVessel creation reference.

---

## MapNode Position Chain (Decompiled)

The complete chain from orbit to screen pixel:

```
OrbitDriver.FixedUpdate()
  -> UpdateOrbit()
    -> [UPDATE mode] updateFromParameters(setPosition: true)
      -> orbit.UpdateFromUT(currentUT)                   // Kepler solver -> pos/vel
      -> pos = orbit.pos; pos.Swizzle()                  // Planetarium -> Unity frame
      -> vessel.SetPosition(body.position + pos - comOffset)  // sets vessel.CoMD

OrbitRendererBase.LateUpdate()
  -> objectNode.NodeUpdate()
    -> OnUpdatePosition delegate:
      -> objectNode_OnUpdatePosition(node)
        -> ScaledSpace.LocalToScaledSpace(vessel.GetWorldPos3D())
          -> vessel.CoMD / 6000 - totalOffset             // scaled space transform
    -> trf.localPosition = screenPos                       // final icon position
```

**Key: MapNode icon position = `ScaledSpace.LocalToScaledSpace(vessel.CoMD)`**, where `vessel.CoMD` is set by OrbitDriver via `vessel.SetPosition()`.

### Ghost mesh position (our code)

```
ParsekFlight.PositionGhostFromOrbit()
  -> orbit.getPositionAtUT(ut)
    -> getPositionAtT(getObtAtUT(ut))
      -> referenceBody.position + getRelativePositionAtT(T).xzy
  -> ghost.transform.position = worldPos
```

Both paths compute `body.position + orbitRelativePosition.xzy`. They diverge ONLY if the Orbit objects have different internal elements.

---

## OrbitDriver Update Modes (Decompiled)

```csharp
public enum UpdateMode { TRACK_Phys, UPDATE, IDLE }
```

### UPDATE mode (on-rails vessels, ghost vessels)
```csharp
case UpdateMode.UPDATE:
    updateFromParameters();     // orbit -> vessel position
    CheckDominantBody(...);     // SOI checks
    break;
```
Standard: orbit is authority, vessel follows. Called every `FixedUpdate`.

### IDLE mode (NOT viable for ghosts)
```csharp
case UpdateMode.TRACK_Phys:
case UpdateMode.IDLE:
    if (vessel == null) break;
    if (vessel.rootPart == null) break;
    if (vessel.rootPart.rb == null) break;  // GHOSTS FAIL HERE
    TrackRigidbody(...);                    // never reached
    break;
```
IDLE shares the TRACK_Phys case. Both require `rootPart.rb != null` (a Rigidbody). Ghost ProtoVessels are unloaded — `rootPart.rb` is always null. **IDLE mode exits early without updating anything.** The vessel position freezes.

### updateFromParameters (Decompiled)
```csharp
internal void updateFromParameters(bool setPosition)
{
    updateUT = Planetarium.GetUniversalTime();
    orbit.UpdateFromUT(updateUT);           // Kepler solve at current UT
    pos = orbit.pos;
    vel = orbit.vel;
    pos.Swizzle();                          // swap Y/Z: Planetarium -> Unity
    vel.Swizzle();

    if (double.IsNaN(pos.x)) { /* destroy vessel */ return; }
    if (!setPosition) return;

    if ((bool)vessel) {
        Vector3d comOffset = (QuaternionD)driverTransform.rotation * (Vector3d)vessel.localCoM;
        vessel.SetPosition(referenceBody.position + pos - comOffset);
    }
}
```
Note: `comOffset` shifts position by the vessel's center-of-mass. For ghost vessels with a single sensorBarometer part, this is small but non-zero.

---

## UpdateFromOrbitAtUT: The Lossy Roundtrip (Decompiled)

This is what `ApplyOrbitToVessel` currently uses. For same-body updates, it hits the GENERAL case:

```csharp
public void UpdateFromOrbitAtUT(Orbit orbit, double UT, CelestialBody toBody)
{
    // Check 1: orbit.referenceBody is parent of toBody (SOI descent)
    if (orbit.referenceBody.HasChild(toBody)) { /* descent path */ return; }
    
    // Check 2: toBody is parent of orbit.referenceBody (SOI ascent)
    if (toBody.HasChild(orbit.referenceBody)) { /* ascent path */ return; }
    
    // GENERAL case — also runs for SAME BODY (neither HasChild passes)
    pos = (orbit.getTruePositionAtUT(UT) - toBody.getTruePositionAtUT(UT)).xzy;
    vel = orbit.getOrbitalVelocityAtUT(UT) 
          + orbit.referenceBody.GetFrameVelAtUT(UT) 
          - toBody.GetFrameVelAtUT(UT);
    UpdateFromStateVectors(pos, vel, toBody, UT);
}
```

For same body: frame velocities cancel, position difference = relative position. But then:

```csharp
public void UpdateFromStateVectors(Vector3d pos, Vector3d vel, CelestialBody refBody, double UT)
{
    pos = Planetarium.Zup.LocalToWorld(pos);   // coordinate transform
    vel = Planetarium.Zup.LocalToWorld(vel);   // coordinate transform
    UpdateFromFixedVectors(pos, vel, refBody, UT);  // re-derive ALL Keplerian elements
}
```

**The roundtrip**: Kepler elements -> state vectors -> `.xzy` swizzle -> `LocalToWorld` transform -> re-derive Keplerian elements from state vectors. This re-derivation introduces:
1. Floating-point precision loss from the forward-backward conversion
2. Potential `argumentOfPeriapsis` NaN fixup (near-circular/equatorial orbits)
3. Different `epoch`/`meanAnomalyAtEpoch` values (re-anchored to the given UT)

**The ghost's cached Orbit** is constructed directly: `new Orbit(inc, ecc, sma, lan, argPe, mna, epoch, body)` — no roundtrip, no coordinate transform. The constructor calls `Init()` which computes derived values directly from elements.

**The OrbitDriver's Orbit** goes through `UpdateFromOrbitAtUT` -> state vector roundtrip. This produces DIFFERENT internal state for what should be the same orbit.

---

## Orbit Core Methods (Decompiled)

### Orbit.UpdateFromUT
```csharp
public void UpdateFromUT(double UT)
{
    ObT = getObtAtUT(UT);
    meanAnomaly = ObT * meanMotion;
    eccentricAnomaly = solveEccentricAnomaly(meanAnomaly, eccentricity);
    trueAnomaly = GetTrueAnomaly(eccentricAnomaly);
    radius = GetOrbitalStateVectorsAtTrueAnomaly(trueAnomaly, UT, out pos, out vel);
    // ... derived values (orbitalSpeed, orbitalEnergy, altitude, timeToPe, etc.)
}
```

### Orbit.getObtAtUT (time since periapsis)
```csharp
public double getObtAtUT(double UT)
{
    double num;
    if (eccentricity < 1.0) {
        num = (UT - epoch + ObTAtEpoch) % period;      // modulo period
        if (num > period / 2.0) num -= period;          // center on [-T/2, T/2]
    } else {
        num = ObTAtEpoch + (UT - epoch);                // hyperbolic: linear
    }
    return num;
}
```

### Orbit.getPositionAtUT / getRelativePositionAtT
```csharp
public Vector3d getPositionAtUT(double UT)
    => getPositionAtT(getObtAtUT(UT));

public Vector3d getPositionAtT(double T)
    => referenceBody.position + getRelativePositionAtT(T).xzy;

public Vector3d getRelativePositionAtT(double T)
{
    double m = T * meanMotion;
    double e = solveEccentricAnomaly(m, eccentricity);
    double tA = GetTrueAnomaly(e);
    return getRelativePositionFromTrueAnomaly(tA);
}
```

### GetOrbitalStateVectorsAtTrueAnomaly (core position/velocity)
```csharp
public double GetOrbitalStateVectorsAtTrueAnomaly(double tA, double UT,
    bool worldToLocal, out Vector3d pos, out Vector3d vel)
{
    double p = semiMajorAxis * (1.0 - eccentricity * eccentricity);
    double sqrtMuOverP = Math.Sqrt(referenceBody.gravParameter / p);
    double r = p / (1.0 + eccentricity * Math.Cos(tA));
    
    pos = OrbitFrame.X * (Math.Cos(tA) * r) + OrbitFrame.Y * (Math.Sin(tA) * r);
    vel = OrbitFrame.X * (-Math.Sin(tA) * sqrtMuOverP) 
        + OrbitFrame.Y * ((Math.Cos(tA) + eccentricity) * sqrtMuOverP);

    if (worldToLocal) {
        CelestialFrame tempZup = default;
        Planetarium.ZupAtT(UT, referenceBody, ref tempZup);
        pos = tempZup.WorldToLocal(pos);
        vel = tempZup.WorldToLocal(vel);
    }
    return r;
}
```

`OrbitFrame.X`/`OrbitFrame.Y` are computed by `Init()` from INC, LAN, argPe. They define the orbital plane orientation in the Planetarium frame.

---

## MapNode.Create API (from Principia)

For reference — an alternative to ProtoVessel-based map presence:

```csharp
var node = KSP.UI.Screens.Mapview.MapNode.Create(
    "label",               // name
    XKCDColors.Pale,       // default color
    pixelSize: 32,         // icon pixel size
    hoverable: true,       // hover interaction
    pinnable: true,        // pin interaction
    blocksInput: true);    // blocks input behind

// Position delegate — called every frame by NodeUpdate()
node.OnUpdatePosition += (MapNode n) =>
    ScaledSpace.LocalToScaledSpace(worldPosition);

// Visibility delegate
node.OnUpdateVisible += (MapNode n, MapNode.IconData icon) => {
    icon.visible = true;
    icon.color = myColor;
};

// Must call per-frame:
node.NodeUpdate();

// Cleanup:
node.Terminate();
```

Principia uses this to create custom trajectory markers completely decoupled from KSP's OrbitDriver. Full control over position, but requires manual lifecycle management.

---

## Why Previous Fix Attempts Failed

### Attempt 1: SetPosition per-frame
```csharp
vessel.SetPosition(ghostMesh.transform.position);
```
**Failure**: OrbitDriver in UPDATE mode calls `updateFromParameters()` every FixedUpdate, which re-sets vessel position from orbit. Our SetPosition was immediately overwritten.

### Attempt 2: UpdateFromStateVectors per-frame
```csharp
vessel.orbit.UpdateFromStateVectors(ghostWorldPos, vel, body, ut);
```
**Failure**: `UpdateFromStateVectors` expects position in a specific coordinate frame (body-relative, xzy-swizzled). Ghost mesh world position is in Unity world space. Mixing coordinate systems produced invalid Keplerian elements — OrbitDriver destroyed.

### Attempt 3: Mean anomaly sync per-frame
```csharp
orbit.meanAnomalyAtEpoch = computedMNA;
orbit.epoch = currentUT;
vessel.orbitDriver.updateFromParameters();
```
**Failure**: Attempted WITHOUT IDLE mode. In UPDATE mode, OrbitDriver's own FixedUpdate also calls `updateFromParameters()`. The two calls fought each other. Additionally, IDLE mode wouldn't have helped — ghost vessels have no Rigidbody, so IDLE mode exits early without updating anything (see decompilation above).

### The actual solution: Direct element assignment
Bypass `UpdateFromOrbitAtUT` entirely. Copy raw Keplerian elements to OrbitDriver's orbit and call `Init()`. This makes both Orbit objects (ghost cache + OrbitDriver) use identical elements with identical derived values. KSP's UPDATE mode then propagates correctly from these elements.

---

## Segment Change Detection Gap

Current code in `CheckPendingMapVessels` and `UpdateChainGhostOrbitIfNeeded`:
```csharp
if (seg.Value.bodyName == kvp.Value.body
    && seg.Value.semiMajorAxis == kvp.Value.sma)
    continue;  // Skip — "same orbit"
```

This ONLY checks body and SMA. An orbital maneuver that changes inclination, LAN, eccentricity, or argument of periapsis WITHOUT changing SMA would be missed. Example: a pure inclination change at constant altitude.

In practice this rarely triggers because:
1. Most recordings have few orbit segments
2. Any real maneuver changes SMA by at least a few meters (floating-point)
3. SOI transitions change body name

But for correctness, the comparison should include all six orbital elements. At minimum, adding eccentricity and inclination would catch the common cases.

---

## comOffset Consideration

`updateFromParameters` subtracts `comOffset` from the orbital position:
```csharp
Vector3d comOffset = (QuaternionD)driverTransform.rotation * (Vector3d)vessel.localCoM;
vessel.SetPosition(referenceBody.position + pos - comOffset);
```

The ghost mesh position does NOT include this offset:
```csharp
ghost.transform.position = orbit.getPositionAtUT(ut);  // no comOffset
```

For the ghost's sensorBarometer part, `localCoM` is tiny (small part). But this is a constant offset between MapNode and ghost mesh. At map view scale it's invisible, but worth noting.

---

## Reference: Coordinate Frame Conventions

| Context | Frame | Conversion |
|---------|-------|------------|
| `orbit.pos` (after UpdateFromUT) | Planetarium | `.Swizzle()` or `.xzy` to Unity |
| `getRelativePositionAtT` | Planetarium | `.xzy` to Unity (done in getPositionAtT) |
| `OrbitFrame.X/Y` vectors | Planetarium | set by `Init()` from INC/LAN/argPe |
| `vessel.CoMD` / `vessel.SetPosition` | Unity world | direct |
| `referenceBody.position` | Unity world | direct |
| `ScaledSpace.LocalToScaledSpace` | Unity world -> scaled | divide by 6000, subtract offset |
| `Planetarium.Zup.LocalToWorld` | Unity local -> Planetarium | rotation transform |
| `Planetarium.Zup.WorldToLocal` | Planetarium -> Unity local | inverse rotation |

The `.xzy` swizzle swaps Y and Z components (KSP uses Y-up in Unity but Z-up in orbital mechanics). It is self-inverse: `v.xzy.xzy == v`.
