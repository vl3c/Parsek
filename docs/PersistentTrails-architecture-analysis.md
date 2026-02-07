# KSPPersistentTrails Architecture Analysis

**For Parsek Project - Trajectory Recording and Playback Reference**

Based on thorough exploration of the KSPPersistentTrails project, this document provides detailed analysis for building Parsek's kinematic playback system.

---

## 1. OVERALL PROJECT STRUCTURE AND ORGANIZATION

**File Location:** `KSPPersistentTrails/`

**Core Architecture Pattern:**
- **MonoBehaviour-based plugin** for KSP integration
- **Singleton pattern** for TrackManager
- **MVC-style separation**: Track data model, Manager controllers, Window views
- **Event-driven updates** via Unity's `InvokeRepeating` and KSP GameEvents

**Key Files:**
- `Track.cs` - Data model for trajectory recordings (922 lines)
- `TrackManager.cs` - Singleton controller managing all tracks (455 lines)
- `ReplayWindow.cs` - Playback UI and behavior (267 lines)
- `CraftLoader.cs` - Vessel geometry serialization/reconstruction (359 lines)
- `OffRailsObject.cs` - Physics simulation for ghost vessels (130 lines)
- `RecordingThresholds.cs` - Sampling optimization parameters (18 lines)
- `MainWindow.cs` - Primary UI for track management (290 lines)
- `Utilities.cs` - File I/O and helper functions (560 lines)

---

## 2. TRACK RECORDING SYSTEM - TRAJECTORY DATA SAMPLING

**Primary Class:** `Track` (Track.cs, lines 77-921)

**Recording Flow:**
1. **Initialization** - `TrackManager.startNewTrack()` (line 293-307)
2. **Periodic Sampling** - `ExplorerTrackBehaviour.updateCurrentTrack()` invoked via `InvokeRepeating` (line 175)
3. **Threshold Evaluation** - `Track.tryAddWaypoint(RecordingThresholds)` (lines 210-242)
4. **Conditional Storage** - `Track.sufficientChange()` (lines 177-208)

**Recording Interval Configuration:**
```csharp
// ExplorerTrackBehaviour.cs, lines 172-176
public void setupRepeatingUpdate(float updateIntervalSeconds)
{
    CancelInvoke("updateCurrentTrack");
    InvokeRepeating("updateCurrentTrack", updateIntervalSeconds, updateIntervalSeconds);
}
```

**Recording Precision Presets (MainWindow.cs, lines 105-119):**
- **High Precision**: 0.2s interval, thresholds(2°, 2°, 0.05)
- **Medium Precision**: 1.0s interval, thresholds(10°, 10°, 0.1)
- **Low Precision**: 5.0s interval, thresholds(20°, 20°, 0.25)

---

## 3. SAMPLING STRATEGIES - INTERVALS, THRESHOLDS, OPTIMIZATION

**Data Structure:** `RecordingThresholds` (RecordingThresholds.cs, lines 4-16)
```csharp
public struct RecordingThresholds
{
    public float minOrientationAngleChange; // degrees
    public float minVelocityAngleChange;    // degrees
    public float minSpeedChangeFactor;      // percentage (0.2 = 20%)
}
```

**Threshold Logic** (Track.cs, lines 177-208):
```csharp
private bool sufficientChange(Waypoint newNode, RecordingThresholds thresholds)
{
    if (waypoints.Count == 0)
        return true; // Always record first waypoint

    // Check orientation change
    if (Quaternion.Angle(waypoints.Last().orientation, newNode.orientation)
        > thresholds.minOrientationAngleChange)
        return true;

    // Check velocity direction change (only if moving)
    if (Vector3.Angle(waypoints.Last().velocity.normalized, newNode.velocity.normalized)
        > thresholds.minVelocityAngleChange)
    {
        if (newNode.velocity.sqrMagnitude > 0.5f)
            return true;
    }

    // Check speed change
    float relativeSpeedChange = waypoints.Last().velocity.magnitude / newNode.velocity.magnitude;
    if (Mathf.Abs(1 - relativeSpeedChange) > thresholds.minSpeedChangeFactor)
        return true;

    return false; // Skip recording this frame
}
```

**Key Optimization Pattern:**
- **Adaptive sampling**: Records more frequently during maneuvers, less during steady flight
- **OR-based criteria**: Any threshold breach triggers recording
- **Velocity sqrMagnitude check**: Avoids recording spurious rotations when stationary

---

## 4. DATA STRUCTURES FOR TRAJECTORY STORAGE

**Primary Structure:** `Waypoint` class (Track.cs, lines 10-51)

```csharp
public class Waypoint
{
    public double latitude;      // Geographic coordinate
    public double longitude;     // Geographic coordinate
    public double altitude;      // Above terrain
    public double recordTime;    // Universal Time (UT)
    public Quaternion orientation;  // Vessel rotation
    public Vector3 velocity;     // Surface-relative velocity

    // Constructor from live vessel
    public Waypoint(Vessel v)
    {
        longitude = v.longitude;
        latitude = v.latitude;
        altitude = v.altitude;
        velocity = v.GetSrfVelocity();
        orientation = v.GetComponent<Rigidbody>().rotation;
        recordTime = Planetarium.GetUniversalTime();
    }
}
```

**Extended Structure:** `LogEntry` (Track.cs, lines 53-75)
- Inherits from Waypoint
- Adds: `label`, `description`, `gameObject`, `guiLabel`, `unityPos`
- Used for mission markers/annotations

**Track Container:** `Track` class holds:
```csharp
private List<Waypoint> waypoints;      // Main trajectory data
private List<LogEntry> logEntries;     // Annotation markers
private CelestialBody referenceBody;   // Planet/moon reference
public Vessel SourceVessel;            // Original recording vessel
```

**Critical Design Choice:**
- **Geographic coordinates** (lat/lon/alt) instead of Unity world coordinates
- **Reference body anchoring** prevents floating-point drift on large scales
- **Time-based indexing** enables temporal interpolation

---

## 5. GHOST VESSEL SPAWNING AND MANAGEMENT

**Primary Class:** `ReplayWindow` (ReplayWindow.cs, lines 164-266)

**Spawning Process:**

```csharp
public ReplayWindow(Track track) : base("Replay Track: " + track.TrackName)
{
    // 1. Load vessel geometry from .crf file
    string fileName = Utilities.CraftPath + track.VesselName + ".crf";
    if (System.IO.File.Exists(fileName))
    {
        ghost = CraftLoader.assembleCraft(fileName, track.ReplayColliders);
    }
    else
    {
        // 2. Fallback to simple sphere
        Mesh sphere = MeshFactory.createSphere();
        ghost = MeshFactory.makeMeshGameObject(ref sphere, "Track playback sphere");
        ghost.transform.localScale = new Vector3(scale, scale, scale);
        ghost.GetComponent<Renderer>().material.SetColor("_EmissiveColor", track.LineColor);
    }

    // 3. Attach replay behavior component
    behaviour = ghost.AddComponent<ReplayBehaviour>();
    behaviour.initialize(track, ghost);
    behaviour.enabled = true;
}
```

**Craft Assembly** (CraftLoader.cs, lines 109-152):
- Loads `.crf` file containing part names, positions, rotations, scales
- Fetches Unity models from KSP GameDatabase
- Reconstructs vessel hierarchy as GameObject tree
- Disables lights, animations, and colliders by default

**Ghost Lifecycle:**
- **Creation**: On replay window open
- **Update**: Every frame via `ReplayBehaviour.Update()`
- **Destruction**: On window close or timeout (OffRailsObject.cs)

---

## 6. PLAYBACK ENGINE - TRAJECTORY REPLAY

**Primary Class:** `ReplayBehaviour` (ReplayWindow.cs, lines 7-161)

**Playback State Machine:**
```csharp
public double currentReplayTime;     // Time within track (0 to totalReplayTime)
public double trackStartUT;          // Absolute start time
public double replayStartUT;         // When playback began
public int playbackFactor;           // Speed multiplier (0=paused, 1=1x, 2=2x...)
public bool isOffRails;              // Kinematic vs physics-driven
```

**Core Update Loop** (lines 118-147):
```csharp
public void Update()
{
    double currentTimeUT = Planetarium.GetUniversalTime();

    // Advance playback time based on speed multiplier
    currentReplayTime += playbackFactor * (currentTimeUT - lastUpdateUT);

    // Update ghost position/rotation (unless physics-driven)
    if (!isOffRails)
        setGhostToPlaybackAt(trackStartUT + currentReplayTime);

    lastUpdateUT = currentTimeUT;

    // Handle end-of-track behavior
    if (currentReplayTime >= totalReplayTime)
    {
        if (track.EndAction == Track.EndActions.LOOP)
            currentReplayTime = 0;  // Loop back to start
        else if (track.EndAction == Track.EndActions.OFFRAILS)
            isOffRails = true;       // Enable physics
    }
}
```

**End-of-Track Actions** (Track.cs, line 116):
```csharp
public enum EndActions { STOP, LOOP, OFFRAILS, DELETE }
```

---

## 7. INTERPOLATION METHODS FOR SMOOTH PLAYBACK

**Core Interpolation Function:** `Track.evaluateAtTime()` (Track.cs, lines 551-621)

**Algorithm:**
1. **Boundary Conditions:**
   - Before first waypoint: Return first waypoint data
   - After last waypoint: Return last waypoint data

2. **Segment Search:**
   - Linear search through waypoints to find time bracket
   - Find waypoint[i] and waypoint[i+1] where `time[i] < ut < time[i+1]`

3. **Linear Interpolation:**
```csharp
float progress = (float)(ut - timeThis) / (float)timeOnSegment;

// Position interpolation (geographic -> Unity coords)
Vector3 start = referenceBody.GetWorldSurfacePosition(
    waypoints[i].latitude, waypoints[i].longitude, waypoints[i].altitude);
Vector3 end = referenceBody.GetWorldSurfacePosition(
    waypoints[i+1].latitude, waypoints[i+1].longitude, waypoints[i+1].altitude);
position = Vector3.Lerp(start, end, progress);

// Rotation interpolation (quaternion lerp)
orientation = Quaternion.Lerp(
    waypoints[i].orientation, waypoints[i+1].orientation, progress);

// Velocity interpolation
velocity = Vector3.Lerp(
    waypoints[i].velocity, waypoints[i+1].velocity, progress);
```

**NaN Protection** (lines 603-613):
```csharp
if (float.IsNaN(orientation.x)) orientation.x = 0;
if (float.IsNaN(orientation.y)) orientation.y = 0;
if (float.IsNaN(orientation.z)) orientation.z = 0;
if (float.IsNaN(orientation.w)) orientation.w = 0;
```

**Loop Closure Support** (lines 623-638):
- Temporarily appends first waypoint to end with transition time
- Removes after interpolation completes

**Performance Note:**
- Uses `Quaternion.Lerp` instead of `Slerp` for speed (linear interpolation is sufficient)

---

## 8. FILE FORMAT FOR STORING TRACK DATA

**Format Specification:** Custom text-based format

**File Extension:** `.trk` (stored in `PluginData/Tracks/`)

**Structure** (Track.cs, lines 657-700):

```
VERSION:1
[HEADER]
VESSELNAME:Kerbal X
DESCRIPTION:Test flight around Mun
VISIBLE:1
MAINBODY:Kerbin
SAMPLING:1
LINECOLOR:0;1;0;1
LINEWIDTH:0.2
CONERADIUSFACTOR:30
NUMDIRECTIONMARKERS:5
REPLAYCOLLIDERS:0
END:LOOP:10
[WAYPOINTS]
12345.67;-0.123;45.678;1234.5;0;0;0;1;10.5;5.2;-3.1
12346.87;-0.124;45.679;1235.2;0.01;0.02;0.03;0.99;10.3;5.1;-3.0
...
[LOGENTRIES]
12345.67;-0.123;45.678;1234.5;0;0;0;1;10.5;5.2;-3.1;Orbit Achieved;Successful insertion
...
```

**Waypoint Format (semicolon-separated):**
```
recordTime;latitude;longitude;altitude;oriX;oriY;oriZ;oriW;velX;velY;velZ
```

**Parsing** (Track.cs, lines 702-844):
- Supports legacy format (pre-VERSION tag)
- ConfigNode-style parsing (KEY:VALUE pairs)
- Handles missing files gracefully

**Associated Craft File:** `.crf` files in `PluginData/Craft/`
- Serializes vessel geometry for ghost reconstruction
- Format: `partName;posX;posY;posZ;rotX;rotY;rotZ;rotW;scale`

---

## 9. UI COMPONENTS FOR RECORDING CONTROLS AND PLAYBACK

**Main Window:** `MainWindow.cs` (lines 10-289)

**Recording Controls:**
```csharp
// Start recording button (lines 66-68)
if (GUILayout.Button("create new Track"))
    TrackManager.Instance.startNewTrack();

// Stop recording button (lines 71-73)
if (GUILayout.Button("Stop recording"))
    TrackManager.Instance.stopRecording();

// Precision selector (lines 105-119)
if (GUILayout.Button("High Precision", toggleStyle))
{
    mainBehaviour.RecordingInterval = 0.2f;
    TrackManager.Instance.ChangeThresholds = new RecordingThresholds(2, 2, 0.05f);
}
```

**Track List Display** (lines 136-223):
- Scrollable list of all recorded tracks
- Per-track controls:
  - **Visibility toggle** (eye icon)
  - **Delete** (X icon)
  - **Continue recording** (resume icon)
  - **Edit properties** (gear icon)
  - **Playback** (play icon)

**Replay Window:** `ReplayWindow.cs` (lines 164-266)

**Playback Controls:**
```csharp
// Timeline scrubber (line 237)
behaviour.currentReplayTime = GUILayout.HorizontalSlider(
    (float)behaviour.currentReplayTime, 0, (float)behaviour.totalReplayTime);

// Transport controls (lines 240-260)
if (GUILayout.Button(playTex))        // Play 1x
    behaviour.playbackFactor = 1;
if (GUILayout.Button(ffTex))          // Fast forward
    behaviour.playbackFactor += 1;
if (GUILayout.Button(pauseTex))       // Pause
    behaviour.playbackFactor = 0;
if (GUILayout.Button(stopTex))        // Stop & rewind
    behaviour.currentReplayTime = 0;
```

---

## 10. PHYSICS HANDLING FOR REPLAYED VESSELS

**Primary Class:** `OffRailsObject` (OffRailsObject.cs, lines 5-129)

**Physics Modes:**

**On-Rails Mode (Default):**
- Kinematic positioning via `evaluateAtTime()`
- No physics simulation
- Ghost position directly set each frame
- Colliders disabled (`isTrigger = true`)

**Off-Rails Mode:**
```csharp
public void goOffRails()  // lines 77-94
{
    _isOffRails = true;
    if (!offRailsInitiliazed)
    {
        if (rbody == null)
            setupRigidBody();
        rbody.isKinematic = false;
        rbody.velocity = currentVelocity;  // Inherit track velocity
        offRailsInitiliazed = true;
        CraftLoader.setColliderStateInChildren(ghost, true);  // Enable collisions
        offRailsObject = ghost.AddComponent<OffRailsObject>();
    }
}
```

**Rigidbody Configuration** (lines 108-116):
```csharp
public void setupRigidBody()
{
    rbody = gameObject.AddComponent<Rigidbody>();
    rbody.mass = 3.0f;
    rbody.drag = 0.01f;
    rbody.useGravity = true;
    rbody.isKinematic = false;
}
```

**Buoyancy Simulation** (lines 94-127):
```csharp
public void FixedUpdate()
{
    if (buoyancyForce > 0f && mainBody.ocean)
    {
        float seaAltitude = Vector3.Distance(mainBody.position, transform.position)
                            - (float)mainBody.Radius;
        if (seaAltitude < 0f)  // Underwater
        {
            rbody.drag = dragInWater;  // 1.0
            float floatMultiplier = -Mathf.Max(seaAltitude, -buoyancyRange) / buoyancyRange;
            Vector3 up = (transform.position - mainBody.position).normalized;
            Vector3 upLift = up * buoyancyForce * floatMultiplier;

            float verticalSpeed = Vector3.Dot(rbody.velocity, up) * rbody.velocity.magnitude;
            if (verticalSpeed < maxVerticalSpeed)
                rbody.AddForce(upLift * Time.deltaTime * 50f);
        }
        else
        {
            rbody.drag = dragInAir;  // 0.01
        }
    }
}
```

**Auto-Destruction Conditions** (lines 50-78):
- **Distance threshold**: > 3000m from player (line 62)
- **Time limit**: 600s timeout (line 67)
- **Time warp**: Destroy if physics warp > 1x (lines 53-60)

**Floating Origin Correction** (lines 81-92):
```csharp
// Handles KSP's floating origin shifts
referenceFrameCorrection = mainBody.position - lastMainBodyPosition;
if (referenceFrameCorrection.magnitude > 1f)
    gameObject.transform.position -= referenceFrameCorrection;
```

---

## 11. KEY CLASSES AND THEIR RELATIONSHIPS

```
ExplorerTrackBehaviour (MonoBehaviour, Singleton-like)
├── TrackManager (Singleton)
│   ├── List<Track> allTracks
│   ├── Track activeTrack (currently recording)
│   └── RecordingThresholds ChangeThresholds
│
├── MainWindow (UI)
│   ├── TrackEditWindow (per-track settings)
│   ├── LogEntryWindow (annotation creator)
│   └── ColorPicker (color selection dialog)
│
└── ReplayWindow (UI + Behavior)
    ├── ReplayBehaviour (MonoBehaviour)
    │   ├── Track track (data source)
    │   ├── GameObject ghost (visual representation)
    │   └── OffRailsObject (optional physics)
    │
    └── CraftLoader (static utility)
        └── PartValue[] (vessel geometry)

Track
├── List<Waypoint> waypoints (trajectory data)
├── List<LogEntry> logEntries (annotations)
├── LineRenderer lineRenderer (visualization)
├── List<GameObject> directionMarkers (cone meshes)
└── CelestialBody referenceBody (planet anchor)
```

**Singleton Access Pattern:**
```csharp
ExplorerTrackBehaviour.Instance
TrackManager.Instance
```

**Update Lifecycle:**
```
Unity OnGUI() → MainWindow.OnGUI() → User clicks "Play"
→ ReplayWindow.constructor() → CraftLoader.assembleCraft()
→ ghost.AddComponent<ReplayBehaviour>()
→ Unity Update() → ReplayBehaviour.Update()
→ Track.evaluateAtTime() → ghost.transform = interpolated position
```

---

## 12. SPECIFIC CODE PATTERNS USEFUL FOR PARSEK

### Pattern 1: Adaptive Sampling with OR-based Thresholds
```csharp
// Track.cs, lines 177-208
// Records only when ANY threshold is exceeded
bool sufficientChange =
    angleChange > threshold ||
    velocityDirectionChange > threshold ||
    speedChange > threshold;
```
**Benefit for Parsek:** Minimizes storage while capturing all important maneuvers.

### Pattern 2: Geographic Coordinate Storage
```csharp
// Waypoint.cs, lines 21-24
longitude = v.longitude;
latitude = v.latitude;
altitude = v.altitude;

// Reconstruction in Track.cs, line 233
Vector3 unityPos = referenceBody.GetWorldSurfacePosition(latitude, longitude, altitude);
```
**Benefit for Parsek:** Avoids floating-point drift in large-scale space environments.

### Pattern 3: Time-based Interpolation
```csharp
// Track.cs, lines 589-602
float progress = (float)(currentTime - segmentStartTime) / segmentDuration;
position = Vector3.Lerp(startPos, endPos, progress);
orientation = Quaternion.Lerp(startRot, endRot, progress);
```
**Benefit for Parsek:** Smooth playback at arbitrary framerates.

### Pattern 4: Separate Kinematic and Physics Modes
```csharp
// ReplayBehaviour.cs, lines 29-39, 77-106
if (!isOffRails)
    setGhostToPlaybackAt(time);  // Direct positioning
else
    rbody.velocity = currentVelocity;  // Physics-driven
```
**Benefit for Parsek:** Allows deterministic replay or interactive simulation.

### Pattern 5: Craft Serialization as GameObject Hierarchy
```csharp
// CraftLoader.cs, lines 109-152
GameObject craft = new GameObject();
foreach (PartValue pv in partList)
{
    pv.model.transform.parent = craft.transform;
    pv.model.transform.localPosition = pv.position;
    pv.model.transform.localRotation = pv.rotation;
}
```
**Benefit for Parsek:** Enables visual replay of complex multi-part vessels.

### Pattern 6: Loop Closure with Transition Time
```csharp
// Track.cs, lines 623-638
void closeLoop()
{
    waypoints.Add(new Waypoint(waypoints.First()));
    waypoints.Last().recordTime = endTime + LoopClosureTime;
}
```
**Benefit for Parsek:** Creates smooth cyclic trajectories without discontinuities.

### Pattern 7: Floating Origin Compensation
```csharp
// OffRailsObject.cs, lines 81-92
referenceFrameCorrection = mainBody.position - lastMainBodyPosition;
if (referenceFrameCorrection.magnitude > 1f)
    gameObject.transform.position -= referenceFrameCorrection;
```
**Benefit for Parsek:** Handles KSP's dynamic coordinate system shifts.

### Pattern 8: Incremental Recording During Flight
```csharp
// ExplorerTrackBehaviour.cs, lines 172-176
InvokeRepeating("updateCurrentTrack", interval, interval);

// TrackManager.cs, lines 352-362
void updateCurrentTrack()
{
    if (recording && allowRecording)
        activeTrack.tryAddWaypoint(ChangeThresholds);
}
```
**Benefit for Parsek:** Zero-overhead recording with configurable precision.

### Pattern 9: File-based Persistence
```csharp
// Track.cs, lines 657-700 (serialization)
// Track.cs, lines 702-844 (deserialization)
// Simple text format: VERSION tag, key:value headers, delimited data sections
```
**Benefit for Parsek:** Human-readable format for debugging, easy versioning.

### Pattern 10: UI-driven Playback Speed Control
```csharp
// ReplayWindow.cs, lines 240-260
if (GUILayout.Button(ffTex))
    behaviour.playbackFactor += 1;  // Increment speed each press

// ReplayBehaviour.cs, line 129
currentReplayTime += playbackFactor * (currentTimeUT - lastUpdateUT);
```
**Benefit for Parsek:** Simple speed multiplier system (0=pause, 1=realtime, 2=2x, etc.)

---

## 13. ADDITIONAL COMPONENTS NOT IN INITIAL ANALYSIS

### MeshFactory (MeshFactory.cs)

Utility class for runtime mesh generation. Used to create ghost vessel fallback shapes.

**Key Methods:**

```csharp
// Create a GameObject with MeshFilter + MeshRenderer from any mesh
public static GameObject makeMeshGameObject(ref Mesh mesh, string name)
{
    GameObject go = new GameObject("Dynamic Mesh: " + name);
    MeshFilter filter = go.AddComponent<MeshFilter>();
    MeshRenderer renderer = go.AddComponent<MeshRenderer>();
    filter.mesh = mesh;
    return go;
}

// Create icosphere via subdivision (used as fallback ghost shape)
public static Mesh createSphere()  // Icosahedron with 2 levels of subdivision

// Create cone mesh (used for direction markers on trails)
public static Mesh createCone(float radius, float height, int approx)
```

**Relevance for Parsek:** The `makeMeshGameObject` + `createSphere` pattern is exactly what the spike uses for the green ghost sphere. For Phase 1, this same approach works. For later phases, replace with `CraftLoader.assembleCraft()` to show actual vessel geometry.

### TireTracker (TireTracker.cs)

A `PartModule`-based system that creates ground tire tracks as dynamic meshes.

```csharp
class ModuleTireTracker : PartModule
{
    [KSPField(isPersistant = false, guiActive = true, guiName = "Tire Width")]
    public float tireWidth;
}
```

**Key patterns:**
- Uses `PartModule` with `[KSPField]` attributes for in-game configuration
- Dynamic mesh generation: builds triangle strips incrementally as the vessel moves
- Movement threshold check: `(newPos - oldPos).sqrMagnitude < 0.1f` to avoid recording when stationary
- Material setup: `Shader.Find("KSP/Emissive/Diffuse")` for self-lit visuals

**Relevance for Parsek:** The `PartModule` + `[KSPField]` pattern is useful for per-vessel recording configuration (e.g., recording precision settings visible in the part's right-click menu). The movement threshold pattern reinforces the adaptive sampling approach.

### CraftLoader Details (CraftLoader.cs)

Additional detail on ghost vessel geometry reconstruction:

- Loads `.crf` files containing serialized part geometry (not full KSP parts, just visual models)
- Fetches models from `GameDatabase.Instance.GetModel(partName)`
- Builds a hierarchy: parent GameObject with child part models at correct local positions/rotations
- Disables lights, animations, and colliders by default for ghost vessels
- Fallback: if `.crf` file missing, uses `MeshFactory.createSphere()` as ghost shape

**Relevance for Parsek:** Phase 1 uses sphere fallback. Phase 2+ should serialize vessel geometry at recording start using this `.crf` pattern for realistic ghost vessels.

---

## SUMMARY OF KEY INSIGHTS FOR PARSEK

1. **Threshold-based sampling** dramatically reduces file sizes (10-100x compression vs. fixed-rate recording)

2. **Geographic coordinates** solve Unity's floating-point precision issues at planetary scales

3. **Time-indexed waypoints** enable trivial seek operations and variable playback speeds

4. **Separate .trk and .crf files** cleanly divide trajectory data from vessel geometry

5. **Linear interpolation** is "good enough" for smooth playback (Slerp not necessary)

6. **Two-mode physics system** (on-rails kinematic vs off-rails dynamic) provides flexibility

7. **Text-based file format** aids debugging and allows manual editing/tooling

8. **InvokeRepeating pattern** efficiently handles periodic sampling without frame-by-frame overhead

9. **Buoyancy simulation** demonstrates how to add specialized physics behaviors to ghosts

10. **Auto-destruction system** prevents memory leaks from abandoned replay objects

11. **MeshFactory utility** provides runtime sphere/cone generation for ghost vessel fallbacks

12. **CraftLoader serialization** enables realistic ghost vessels by reconstructing part geometry from `.crf` files

13. **PartModule with [KSPField]** pattern (TireTracker) enables per-vessel configuration visible in the right-click menu

This architecture provides an excellent foundation for Parsek's kinematic playback system. The threshold-based recording, geographic coordinate storage, and time-based interpolation patterns are directly applicable to your mission recording needs.
