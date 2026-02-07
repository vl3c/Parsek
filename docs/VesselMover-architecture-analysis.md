# VesselMover Architecture Analysis
**For Parsek Project - Programmatic Vessel Positioning Reference**

Based on thorough exploration of the VesselMover mod (by BahamutoD, v1.12.0), this document provides detailed analysis of vessel positioning, physics handling, and spawning patterns directly applicable to Parsek's ghost vessel positioning during playback.

---

## 1. OVERALL PROJECT STRUCTURE AND ORGANIZATION

### File Organization
The project consists of 8 C# files organized into two functional groups:

**Core Vessel Manipulation:**
- `VesselMove.cs` - Runtime vessel repositioning (position, rotation, velocity zeroing)
- `VesselSpawn.cs` - Spawning new vessels from craft files at arbitrary positions
- `VMUtils.cs` - Shared utility functions (sphere-ray intersection, window positioning)
- `VesselMoverToolbar.cs` - Application launcher toolbar and GUI windows

**Move Launch Subsystem (MoveLaunch/):**
- `MoveLaunch.cs` - Editor-to-flight launch site relocation (teleport to GPS coordinates)
- `MoveLaunchGPSLogger.cs` - GPS coordinate logging utility for finding launch sites
- `MoveLaunchMassModifier.cs` - PartModule that zeroes mass during teleportation

**Metadata:**
- `Properties/AssemblyInfo.cs` - Assembly metadata (BahamutoD, 2014-2021)

### Architectural Pattern
VesselMover uses a **singleton MonoBehaviour** pattern with three `[KSPAddon]` classes instantiated at flight scene start:

```csharp
[KSPAddon(KSPAddon.Startup.Flight, false)]
public class VesselMove : MonoBehaviour        // Singleton via Instance
[KSPAddon(KSPAddon.Startup.Flight, false)]
public class VesselSpawn : MonoBehaviour       // Singleton via instance
[KSPAddon(KSPAddon.Startup.FlightEditorAndKSC, true)]
public class MoveLaunch : MonoBehaviour        // Persistent, DontDestroyOnLoad
```

The `VesselMove` and `VesselSpawn` classes are tightly coupled -- after spawning, `VesselSpawn` hands off to `VesselMove.Instance.StartMove()` for placement.

---

## 2. VESSEL POSITIONING - CORE MECHANISM

### Primary Class: `VesselMove` (VesselMove.cs)

This is the most relevant class for Parsek. It demonstrates how to programmatically set a vessel's position and rotation each physics frame.

### The Positioning Pipeline (UpdateMove method)

**Step 1: Suppress G-forces**
```csharp
MovingVessel.IgnoreGForces(240);
```
Called every `FixedUpdate`. The `240` parameter tells KSP to ignore G-forces for 240 frames. This prevents the vessel from being destroyed by apparent extreme accelerations when its position is being set programmatically.

**Step 2: Calculate the "up" vector**
```csharp
_up = (MovingVessel.transform.position - FlightGlobals.currentMainBody.transform.position).normalized;
```
The local "up" direction is computed as the vector from the planet center to the vessel. This is critical on a spherical body where "up" varies with position.

**Step 3: Set position via `Vessel.Translate()` or `Vessel.SetPosition()`**

Two methods are used depending on context:

**Surface-detected positioning (raycast hit):**
```csharp
Vector3 rOffset = Vector3.Project(ringHit.point - vSrfPt, _up);
Vector3 mOffset = (vSrfPt + offset) - MovingVessel.CoM;
finalOffset = rOffset + mOffset + (MoveHeight * _up);
MovingVessel.Translate(finalOffset);
```

**No-surface fallback (terrain or water):**
```csharp
// Over terrain:
Vector3 terrainPos = MovingVessel.mainBody.position + (float)srfHeight * _up;
MovingVessel.SetPosition(terrainPos + (MoveHeight * _up) + offset);

// Over water:
Vector3 waterSrfPoint = FlightGlobals.currentMainBody.position
    + ((float)FlightGlobals.currentMainBody.Radius * _up);
MovingVessel.SetPosition(waterSrfPoint + (MoveHeight * _up) + offset);
```

**Step 4: Set rotation**
```csharp
Quaternion srfRotFix = Quaternion.FromToRotation(_startingUp, _up);
_currRotation = srfRotFix * _startRotation;
MovingVessel.SetRotation(_currRotation);
```
Rotation is tracked relative to the starting "up" vector. As the vessel moves over a curved surface, a correction quaternion (`srfRotFix`) adjusts for the changing up vector, keeping the vessel oriented correctly relative to the local surface normal.

**Step 5: Zero all velocities**
```csharp
MovingVessel.SetWorldVelocity(Vector3d.zero);
MovingVessel.angularVelocity = Vector3.zero;
MovingVessel.angularMomentum = Vector3.zero;
```
Every physics frame, all linear and angular velocity is zeroed. This prevents the physics engine from accumulating velocity as the position changes.

### Key KSP Vessel API Methods Used

| Method | Purpose |
|--------|---------|
| `Vessel.SetPosition(Vector3d)` | Absolute world position placement |
| `Vessel.Translate(Vector3)` | Relative offset from current position |
| `Vessel.SetRotation(Quaternion)` | Set vessel rotation |
| `Vessel.SetWorldVelocity(Vector3d)` | Set vessel velocity (zero it) |
| `Vessel.IgnoreGForces(int)` | Suppress G-force destruction |
| `Vessel.UpdateLandedSplashed()` | Refresh situation after repositioning |
| `Vessel.GoOffRails()` | Force vessel into physics simulation |

---

## 3. PHYSICS AND COLLISION HANDLING DURING MOVES

### G-Force Suppression
The most critical pattern. Without `IgnoreGForces(240)`, instantly repositioning a vessel causes KSP to calculate extreme G-forces (the vessel "teleported" at infinite speed) and destroy it:

```csharp
private void FixedUpdate()
{
    if (!_moving) return;
    MovingVessel.IgnoreGForces(240);  // Called FIRST, every frame
    UpdateMove();
}
```

### Surface Detection via CapsuleCast
VesselMover uses a physics capsule cast to detect the terrain surface below the vessel:

```csharp
private RaycastHit[] CapsuleCast()
{
    float radius = _vBounds.Radius + Mathf.Clamp(_currMoveSpeed, 0, 200);
    return Physics.CapsuleCastAll(
        MovingVessel.CoM + (250 * _up),     // Start 250m above vessel
        MovingVessel.CoM + (249 * _up),      // End 249m above (1m capsule)
        radius,                               // Cast radius matches vessel
        -_up,                                 // Cast downward
        2000,                                 // Max distance
        1 << 15                               // Layer 15 (terrain)
    );
}
```

The cast explicitly filters out parts belonging to the moving vessel itself:
```csharp
foreach (var hit in rayCastHits)
{
    var partHit = hit.collider.gameObject.GetComponentInParent<Part>();
    if (partHit == null)                      // Terrain hit (no Part component)
    {
        ringHit = hit;
        surfaceDetected = true;
        break;
    }
    if (partHit?.vessel == MovingVessel) continue;  // Skip own vessel
    ringHit = hit;                            // Hit another vessel
    surfaceDetected = true;
    break;
}
```

### RCS Disabling
During moves, RCS is forcibly disabled to prevent thrusters from firing while the vessel is being positioned:
```csharp
MovingVessel.ActionGroups.SetGroup(KSPActionGroup.RCS, false);
```

### Launch Clamp Handling
Before starting a move, launch clamps are explicitly released:
```csharp
foreach (LaunchClamp clamp in v.FindPartModulesImplementing<LaunchClamp>())
{
    if (forceReleaseClamps) clamp.Release();
    else return;  // Abort move if clamps present and not forcing
}
```

### Mass Zeroing During Teleport (MoveLaunch)
For long-distance teleportation, VesselMover adds a temporary PartModule that zeroes mass:
```csharp
public class MoveLaunchMassModifier : PartModule
{
    public override void OnStart(StartState state)
    {
        defaultMass = this.part.mass;
        this.part.mass = 0;   // Zero mass during teleport
    }
}
```

When placement completes, mass is gradually restored over several frames to prevent physics shocks:
```csharp
IEnumerator Drop()
{
    this.part.mass = defaultMass / 6;    // 1/6 mass
    yield return new WaitForEndOfFrame();
    this.part.mass = defaultMass / 4;    // 1/4 mass
    yield return new WaitForEndOfFrame();
    this.part.mass = defaultMass / 2;    // 1/2 mass
    yield return new WaitForEndOfFrame();
    this.part.mass = defaultMass;        // Full mass
    Destroy(this);                       // Remove modifier
}
```

---

## 4. VESSEL SPAWNING PATTERNS

### Primary Class: `VesselSpawn` (VesselSpawn.cs)

### Spawning Pipeline

**Step 1: Load craft file via ShipConstruction**
```csharp
ConfigNode currentShip = ShipConstruction.ShipConfig;      // Save current editor state
shipConstruct = ShipConstruction.LoadShip(vesselData.craftURL);
ShipConstruction.ShipConfig = currentShip;                  // Restore editor state
```
Important: The current ShipConstruction state must be saved/restored, otherwise the spawned vessel will appear in the VAB/SPH next time the player opens the editor.

**Step 2: Assign flight IDs to parts**
```csharp
uint missionID = (uint)Guid.NewGuid().GetHashCode();
uint launchID = HighLogic.CurrentGame.launchID++;
foreach (Part p in shipConstruct.parts)
{
    p.flightID = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
    p.missionID = missionID;
    p.launchID = launchID;
    p.temperature = 1.0;   // Prevent explosion from negative temperature
}
```

**Step 3: Create ProtoVessel via dummy vessel**
The craft file format differs from the ProtoVessel format, so a dummy vessel is created as a conversion intermediary:
```csharp
ProtoVessel dummyProto = new ProtoVessel(empty, null);
Vessel dummyVessel = new Vessel();
dummyVessel.parts = shipConstruct.Parts;
dummyProto.vesselRef = dummyVessel;

foreach (Part p in shipConstruct.parts)
{
    dummyProto.protoPartSnapshots.Add(new ProtoPartSnapshot(p, dummyProto, true));
}
```

**Step 4: Compute orbit from surface position**
```csharp
Vector3d pos = vesselData.body.GetRelSurfacePosition(
    vesselData.latitude, vesselData.longitude, vesselData.altitude.Value);
vesselData.orbit = new Orbit(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, vesselData.body);
vesselData.orbit.UpdateFromStateVectors(pos, vesselData.body.getRFrmVel(pos),
    vesselData.body, Planetarium.GetUniversalTime());
```

**Step 5: Build ProtoVessel ConfigNode**
```csharp
ConfigNode protoVesselNode = ProtoVessel.CreateVesselNode(
    vesselData.name, vesselData.vesselType,
    vesselData.orbit, 0, partNodes, additionalNodes);

// Set landed properties
protoVesselNode.SetValue("sit", Vessel.Situations.LANDED.ToString());
protoVesselNode.SetValue("lat", vesselData.latitude.ToString());
protoVesselNode.SetValue("lon", vesselData.longitude.ToString());
protoVesselNode.SetValue("alt", vesselData.altitude.ToString());
protoVesselNode.SetValue("rot", KSPUtil.WriteQuaternion(normal * rotation), true);
```

**Step 6: Add vessel to game**
```csharp
ProtoVessel protoVessel = HighLogic.CurrentGame.AddVessel(protoVesselNode);
```

**Step 7: Place spawned vessel via coroutine**
```csharp
private IEnumerator PlaceSpawnedVessel(Vessel v, bool moveVessel)
{
    v.isPersistent = true;
    v.Landed = false;
    v.situation = Vessel.Situations.FLYING;
    while (v.packed) yield return null;        // Wait for vessel to unpack
    v.SetWorldVelocity(Vector3d.zero);
    FlightGlobals.ForceSetActiveVessel(v);
    v.Landed = true;
    v.situation = Vessel.Situations.PRELAUNCH;
    v.GoOffRails();
    v.IgnoreGForces(240);
    StageManager.BeginFlight();

    if (moveVessel)
    {
        VesselMove.Instance.StartMove(v, false);  // Hand off to movement system
        VesselMove.Instance.MoveHeight = 35;
    }
}
```

### VesselData Structure
The internal data class used for spawning configuration:
```csharp
private class VesselData
{
    public string craftURL;         // Path to .craft file
    public double latitude, longitude;
    public double? altitude;
    public CelestialBody body;
    public Orbit orbit;
    public float heading, pitch, roll;
    public VesselType vesselType;
    public string flagURL;
    public bool orbiting;
    public List<CrewData> crew;
}
```

---

## 5. COORDINATE SYSTEM HANDLING

### Geographic to World Position Conversion
VesselMover consistently uses geographic coordinates (latitude, longitude, altitude) as the canonical representation, converting to Unity world coordinates only when needed:

```csharp
// World position -> Geographic coords
private Vector3d WorldPositionToGeoCoords(Vector3d worldPosition, CelestialBody body)
{
    double lat = body.GetLatitude(worldPosition);
    double longi = body.GetLongitude(worldPosition);
    double alt = body.GetAltitude(worldPosition);
    return new Vector3d(lat, longi, alt);
}

// Geographic coords -> World position (for spawning)
Vector3d pos = vesselData.body.GetWorldSurfacePosition(latitude, longitude, altitude);

// Geographic coords -> Relative surface position (for orbit computation)
Vector3d pos = vesselData.body.GetRelSurfacePosition(latitude, longitude, altitude);
```

### PQS Surface Height Query
To find the actual terrain height at a given position:
```csharp
PQS bodyPQS = MovingVessel.mainBody.pqsController;

Vector3d bodyUpVector = new Vector3d(1, 0, 0);
bodyUpVector = QuaternionD.AngleAxis(lat, Vector3d.forward) * bodyUpVector;
bodyUpVector = QuaternionD.AngleAxis(lng, Vector3d.down) * bodyUpVector;
double srfHeight = bodyPQS.GetSurfaceHeight(bodyUpVector);
```

### Surface Normal Computation
For placed vessels, the surface normal at a geographic position:
```csharp
Vector3d norm = vesselData.body.GetRelSurfaceNVector(vesselData.latitude, vesselData.longitude);
Quaternion normal = Quaternion.LookRotation((Vector3)norm);
```

### Up Vector Pattern
The "up" vector (away from planet center) is computed consistently:
```csharp
_up = (MovingVessel.transform.position - FlightGlobals.currentMainBody.transform.position).normalized;
```

### North Vector
For camera-relative movement, a local "north" direction is derived:
```csharp
private Vector3 North()
{
    Vector3 n = MovingVessel.mainBody.GetWorldSurfacePosition(
        MovingVessel.latitude + 1, MovingVessel.longitude, MovingVessel.altitude)
        - MovingVessel.GetWorldPos3D();
    n = Vector3.ProjectOnPlane(n, _up);
    return n.normalized;
}
```

---

## 6. VESSEL BOUNDS COMPUTATION

### VesselBounds Struct
VesselMover computes vessel dimensions by iterating mesh vertices of all parts:

```csharp
public struct VesselBounds
{
    public Vessel vessel;
    public float BottomLength;   // Distance from CoM to lowest point
    public float Radius;         // Horizontal radius from CoM

    public void UpdateBounds()
    {
        Vector3 up = (vessel.CoM - vessel.mainBody.transform.position).normalized;

        foreach (Part p in vessel.parts)
        {
            foreach (MeshFilter mf in p.GetComponentsInChildren<MeshFilter>())
            {
                foreach (Vector3 vert in mf.mesh.vertices)
                {
                    Vector3 worldVertPoint = mf.transform.TransformPoint(vert);

                    // Bottom check: find lowest vertex relative to CoM
                    float bSqrDist = (downPoint - worldVertPoint).sqrMagnitude;
                    if (bSqrDist < closestSqrDist) { ... }

                    // Radius check: find farthest vertex horizontally from CoM
                    float hSqrDist = Vector3.ProjectOnPlane(
                        vessel.CoM - worldVertPoint, up).sqrMagnitude;
                    if (hSqrDist > furthestSqrDist) { ... }
                }
            }
        }

        BottomLength = Vector3.Project(closestVert - vessel.CoM, up).magnitude;
        Radius = Vector3.ProjectOnPlane(furthestVert - vessel.CoM, up).magnitude;
        Radius += Mathf.Clamp(Radius, 2, 10);  // Add safety margin
    }
}
```

This is used to compute hover height (so the vessel hovers above terrain rather than clipping into it) and for the surface-detection capsule cast radius.

---

## 7. MOVEMENT PLACEMENT LIFECYCLE

### Start -> Move -> End/Drop Flow

**StartMove:**
1. Verify vessel exists, is unpacked, and is landed/splashed
2. Release launch clamps if present
3. Compute initial "up" vector and bounds
4. Store starting rotation
5. Set initial hover height to `BottomLength + 0.5f`

**UpdateMove (every FixedUpdate):**
1. Suppress G-forces
2. Lerp hover height to target
3. Compute camera-relative movement directions
4. Apply user input for translation, rotation, altitude
5. Cast for surface below vessel
6. Set position via Translate/SetPosition
7. Apply rotation with surface normal correction
8. Zero all velocities

**EndMove (place vessel):**
1. Stop movement processing
2. Gradually lower vessel toward surface using velocity-based descent:
   ```csharp
   float placeSpeed = Mathf.Clamp(((altitude - bottomLength) * 2), 0.1f, maxPlacementSpeed);
   if (placeSpeed > 3)
       v.Translate(placeSpeed * Time.fixedDeltaTime * -_up);
   else
       v.SetWorldVelocity(placeSpeed * -_up);
   ```
3. Wait until `v.LandedOrSplashed` becomes true
4. Release vessel to physics

**DropMove (release immediately):**
1. Stop movement processing immediately
2. Let physics take over (vessel falls under gravity)

---

## 8. MOVE LAUNCH - LONG-DISTANCE TELEPORTATION

### Multi-Step Position Jump (MoveLaunch.cs)

For teleporting vessels across large distances (which would cause KSP terrain loading issues if done in a single step), VesselMover uses incremental position jumps:

```csharp
IEnumerator Launch()
{
    // Make vessel kinematic
    FlightGlobals.ActiveVessel.GetComponent<Rigidbody>().isKinematic = true;

    // Zero mass on all parts
    foreach (Part p in FlightGlobals.ActiveVessel.parts)
        p.AddModule("MoveLaunchMassModifier", true);

    // Step 1: Move to high altitude at original position
    altitude = 65000;
    FlightGlobals.ActiveVessel.SetPosition(LaunchPosition(), true);
    // Zero velocity and G-forces
    yield return new WaitForFixedUpdate();

    // Step 2: Move halfway (negative)
    latitude = lat / 2 * -1;  longitude = lon / 2 * -1;
    FlightGlobals.ActiveVessel.SetPosition(LaunchPosition(), true);
    yield return new WaitForFixedUpdate();

    // Step 3: Move to origin
    latitude = 0;  longitude = 0;
    FlightGlobals.ActiveVessel.SetPosition(LaunchPosition(), true);
    yield return new WaitForFixedUpdate();

    // Step 4: Move halfway to target
    latitude = lat / 2;  longitude = lon / 2;
    FlightGlobals.ActiveVessel.SetPosition(LaunchPosition(), true);
    yield return new WaitForFixedUpdate();

    // Step 5: Final position at target altitude
    latitude = lat;  longitude = lon;  altitude = altAdjust;
    FlightGlobals.ActiveVessel.SetPosition(LaunchPosition(), true);
    FlightGlobals.ActiveVessel.GetComponent<Rigidbody>().isKinematic = false;
    yield return new WaitForFixedUpdate();

    // Hand off to VesselMove for fine placement
    VesselMove.Instance.StartMove(FlightGlobals.ActiveVessel, true);
}
```

**Key insight**: Each step waits for a physics frame (`WaitForFixedUpdate`) to allow KSP to load terrain tiles. The intermediate positions help KSP progressively load the correct terrain data.

---

## 9. PATTERNS USEFUL FOR PARSEK'S GHOST VESSEL POSITIONING

### Pattern 1: Per-Frame Position + Rotation + Velocity Zero
The core pattern for kinematic vessel control:
```csharp
void FixedUpdate()
{
    vessel.IgnoreGForces(240);
    vessel.SetPosition(targetPosition);
    vessel.SetRotation(targetRotation);
    vessel.SetWorldVelocity(Vector3d.zero);
    vessel.angularVelocity = Vector3.zero;
    vessel.angularMomentum = Vector3.zero;
}
```
**Parsek application**: During ghost playback, apply this pattern each frame using interpolated position/rotation from recorded waypoints. The `IgnoreGForces` call is essential -- without it, programmatic repositioning will destroy the vessel.

### Pattern 2: Surface Normal Rotation Correction
When replaying a vessel that moves across a curved planetary surface:
```csharp
Quaternion srfRotFix = Quaternion.FromToRotation(startingUp, currentUp);
Quaternion correctedRotation = srfRotFix * recordedRotation;
vessel.SetRotation(correctedRotation);
```
**Parsek application**: If recording stores rotations relative to a local "up" at recording time, this correction ensures correct visual orientation during playback even if the ghost has drifted to a different latitude (where "up" points differently).

### Pattern 3: Vessel.Translate vs Vessel.SetPosition
- `SetPosition(Vector3d)` - Absolute world position. Used when you know exactly where the vessel should be (e.g., computed from lat/lon/alt).
- `Translate(Vector3)` - Relative offset. Used when making incremental adjustments from the current position (e.g., surface-following corrections).

**Parsek application**: For playback, use `SetPosition` with positions computed from `body.GetWorldSurfacePosition(lat, lon, alt)` (geographic coordinates from recorded waypoints). Use `Translate` only if doing surface-following corrections on top of the base position.

### Pattern 4: Wait for Vessel to Unpack Before Positioning
```csharp
while (v.packed) yield return null;
v.SetWorldVelocity(Vector3d.zero);
```
**Parsek application**: Newly created or loaded vessels start in a "packed" (on-rails) state. You must wait for them to unpack before setting position/velocity. The `GoOffRails()` call can force this.

### Pattern 5: Rigidbody Kinematic Mode for Teleportation
```csharp
vessel.GetComponent<Rigidbody>().isKinematic = true;
// ... position changes ...
vessel.GetComponent<Rigidbody>().isKinematic = false;
```
**Parsek application**: For the ghost vessel during playback, keeping the rigidbody kinematic prevents physics from fighting your position updates. This is an alternative to (or can supplement) the velocity-zeroing approach.

### Pattern 6: Gradual Mass Restoration
```csharp
part.mass = defaultMass / 6;   // Restore gradually
yield return new WaitForEndOfFrame();
part.mass = defaultMass / 4;
yield return new WaitForEndOfFrame();
part.mass = defaultMass / 2;
yield return new WaitForEndOfFrame();
part.mass = defaultMass;       // Full mass
```
**Parsek application**: If a ghost vessel transitions from kinematic playback to physics-enabled mode (e.g., "release at end of track"), gradually restoring mass prevents extreme physics reactions.

### Pattern 7: Geographic Position Computation for Vessel Placement
```csharp
Vector3d worldPos = body.GetWorldSurfacePosition(latitude, longitude, altitude);
vessel.SetPosition(worldPos);
```
**Parsek application**: Since recorded waypoints should use geographic coordinates (per PersistentTrails analysis), this is the direct conversion path for playback positioning.

### Pattern 8: VesselBounds for Collision Avoidance
Computing vessel extents from mesh data enables surface-aware placement:
```csharp
float hoverHeight = vBounds.BottomLength + desiredClearance;
Vector3 position = surfacePoint + (hoverHeight * up);
```
**Parsek application**: If the ghost vessel needs to be placed on terrain during playback (e.g., for landed segments), computing bounds ensures it sits correctly on the surface rather than clipping through.

---

## SUMMARY

VesselMover demonstrates the complete toolkit for programmatic vessel control in KSP:

1. **`IgnoreGForces(240)`** - The single most important call. Without it, any programmatic position change destroys the vessel. Must be called every physics frame.

2. **`SetPosition` + `SetRotation` + `SetWorldVelocity(zero)`** - The core three-call pattern for kinematic vessel control. Position and rotation set the state; velocity zeroing prevents physics drift.

3. **Geographic coordinates** - Used as the storage format (lat/lon/alt), with `GetWorldSurfacePosition()` for conversion to Unity coordinates at render time.

4. **Surface normal correction** - `Quaternion.FromToRotation(startUp, currentUp)` handles rotation across curved surfaces.

5. **Coroutine-based async flow** - Spawning and placement use `IEnumerator` coroutines with `WaitForFixedUpdate` to synchronize with physics frames and terrain loading.

6. **Physics suppression** - Multiple techniques: G-force ignoring, velocity zeroing, RCS disabling, kinematic rigidbody, and mass zeroing. The right combination depends on the use case.

7. **Packed vessel handling** - Always wait for `v.packed == false` before attempting position/velocity manipulation.

For Parsek's ghost vessel playback, the essential subset is patterns 1 (per-frame positioning), 2 (surface rotation correction), 3 (SetPosition from geographic coords), and 4 (wait for unpack). The mass zeroing and multi-step teleportation patterns are less relevant since Parsek's ghost will already be in the scene near the active vessel.
