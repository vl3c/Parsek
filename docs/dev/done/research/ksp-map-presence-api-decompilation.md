# KSP Map Presence API - Decompilation Reference

Decompiled from `Assembly-CSharp.dll` (KSP 1.12.x). Focus: creating lightweight ghost ProtoVessels with orbit lines, tracking station entries, and navigation targeting.

---

## Priority 1 -- ProtoVessel Creation

### ProtoVessel

**Key fields** (all public):
```csharp
public List<ProtoPartSnapshot> protoPartSnapshots;
public OrbitSnapshot orbitSnapShot;
public Guid vesselID;
public uint persistentId;
public string vesselName;
public VesselType vesselType;
public Vessel.Situations situation;
public ConfigNode discoveryInfo;     // DISCOVERY node
public ConfigNode actionGroups;      // ACTIONGROUPS node
public ConfigNode flightPlan;        // FLIGHTPLAN node
public ConfigNode ctrlState;         // CTRLSTATE node
public ConfigNode vesselModules;     // VESSELMODULES node
public ProtoTargetInfo targetInfo;
public Vessel vesselRef;             // set during Load()
public double latitude, longitude, altitude;
public float height;
public Vector3 normal;
public Quaternion rotation;          // srfRelRotation
public Vector3 CoM;
public int rootIndex;
public double missionTime, launchTime, lastUT;
public bool landed, splashed;
public bool autoClean;
public bool persistent;
public uint refTransform;
```

#### Constructor from ConfigNode

```csharp
public ProtoVessel(ConfigNode node, Game st)
```

Parses these **value keys** from the node:
- `pid` (Guid), `persistentId` (uint), `name`, `type` (VesselType enum), `sit` (Vessel.Situations enum)
- `landed` (bool), `splashed` (bool), `lat` (double), `lon` (double), `alt` (double)
- `hgt` (float), `nrm` (Vector3), `rot` (Quaternion), `CoM` (Vector3)
- `met` (double), `lct` (double), `lastUT` (double), `root` (int), `ref` (uint)
- `stg` (int), `prst` (bool), `ctrl` (bool)
- `PQSMin`, `PQSMax` (int), `distanceTraveled` (double)
- `cln` (bool), `clnRsn` (string), `GroupOverride`, `OverrideDefault`, `OverrideActionControl`, `OverrideAxisControl`, `OverrideGroupNames`
- `skipGroundPositioning`, `skipGroundPositioningForDroppedPart`, `vesselSpawning` (all bool)
- `launchedFrom`, `landedAt`, `displaylandedAt` (strings)
- `altDispState` (AltimeterDisplayState enum)

Parses these **child nodes**:
- `ORBIT` -> `OrbitSnapshot`
- `PART` -> `ProtoPartSnapshot` (one per part)
- `DISCOVERY` -> stored as ConfigNode
- `ACTIONGROUPS` -> stored as ConfigNode
- `FLIGHTPLAN` -> stored as ConfigNode
- `CTRLSTATE` -> stored as ConfigNode
- `VESSELMODULES` -> stored as ConfigNode
- `TARGET` -> `ProtoTargetInfo`
- `WAYPOINT` -> `ProtoWaypointInfo`

**Important**: `persistentId` is validated via `FlightGlobals.CheckVesselpersistentId(persistentId, null, removeOldId: false, addNewId: true)` which generates a new unique ID if collision detected.

#### CreateVesselNode (static factory)

```csharp
public static ConfigNode CreateVesselNode(
    string vesselName, VesselType vesselType, Orbit orbit,
    int rootPartIndex, ConfigNode[] partNodes,
    params ConfigNode[] additionalNodes)
```

This is the **canonical way to create a vessel ConfigNode** for injection. It:
1. Computes lat/lon/alt from the orbit at current UT
2. Generates a new GUID and persistentId
3. Hardcodes `sit = ORBITING`, `landed = false`, `splashed = false`
4. Sets `vesselSpawning = true`
5. Adds the ORBIT node via `CreateOrbitNode`
6. Appends all part nodes
7. Appends additional nodes (DISCOVERY, ACTIONGROUPS, etc.)
8. Adds empty ACTIONGROUPS if not already present

**Gotcha**: This method only supports ORBITING situation. For landed ghosts, you must build the ConfigNode manually.

#### CreateOrbitNode (static)

```csharp
public static ConfigNode CreateOrbitNode(Orbit orbit)
```
Creates an `ORBIT` node with: `SMA`, `ECC`, `INC`, `LPE`, `LAN`, `MNA`, `EPH`, `REF` (body index).

#### CreateDiscoveryNode (static)

```csharp
public static ConfigNode CreateDiscoveryNode(
    DiscoveryLevels level, UntrackedObjectClass size,
    double lifeTime, double maxLifeTime)
```
Creates a `DISCOVERY` node. Sets `lastObservedTime = current UT`. For a permanently-visible ghost:
```csharp
ProtoVessel.CreateDiscoveryNode(
    DiscoveryLevels.Owned,      // -1, all flags set
    UntrackedObjectClass.C,     // doesn't matter for owned vessels
    double.PositiveInfinity,    // never fade
    double.PositiveInfinity     // never fade
)
```

#### CreatePartNode (static)

```csharp
public static ConfigNode CreatePartNode(string partName, uint id, params ProtoCrewMember[] crew)
```
Creates a minimal PART node with position=zero, rotation=identity. Reads part prefab for resources, attach nodes, temperature, mass, etc.

**Gotcha**: `partName` must use KSP's dot-form (e.g., `solidBooster.v2` not `solidBooster_v2`).

#### ProtoVessel.Load (the vessel materialization method)

```csharp
internal void Load(FlightState st, Vessel vessel)
```

This is what brings a ProtoVessel to life as a Vessel GameObject:

1. **If `vessel == null`**: Creates a new `GameObject` with `Vessel` component, assigns `vesselRef.id`, `vesselRef.persistentId`
2. Copies all fields: vesselName, vesselType, situation, lat/lon/alt, etc.
3. **If `vessel == null`**: Creates `OrbitDriver` component, loads orbit from snapshot, sets `updateMode`:
   - `OrbitDriver.UpdateMode.UPDATE` if NOT landed/splashed (Keplerian propagation)
   - `OrbitDriver.UpdateMode.IDLE` if landed/splashed
4. Sets `vesselRef.transform.position` from orbit body surface position
5. Sets `vesselRef.srfRelRotation = rotation`
6. Loads DISCOVERY info into `vesselRef.DiscoveryInfo`
7. Calls `vesselRef.LoadVesselModules(vesselModules)`
8. Calls `vesselRef.StartFromBackup(this, st)` which calls `AddOrbitRenderer()`
9. **Calls `FlightGlobals.AddVessel(vesselRef)`** -- adds to global vessel list
10. **Fires `GameEvents.onVesselCreate`** if this was a new vessel

#### ProtoVessel.Save

```csharp
public void Save(ConfigNode node)
```

Writes all values and child nodes in the order shown above. Key: fires `GameEvents.onProtoVesselSave`.

### ProtoPartSnapshot

**Minimal fields needed** for a single-part ghost:
- `partName` (string) - must match PartLoader name (dot-form)
- `craftID`, `flightID`, `persistentId` (uint)
- `position` (Vector3d), `rotation` (Quaternion)
- `state` (int, 0 = IDLE is fine)
- `mass` (float), `temperature` (double)
- `modules` (List<ProtoPartModuleSnapshot>)
- `resources` (List<ProtoPartResourceSnapshot>)
- `attachNodes`, `srfAttachNode`

The ConfigNode constructor parses these value keys:
- `part` (name), `persistentId`, `uid`/`mid`, `parent`, `position`, `rotation`, `mirror`
- `srfN`, `attN` (attach nodes)
- `state`, `temp`, `tempExt`, `mass`, `expt`
- `connected`, `attached`, `shielded`
- `crew` (names), `flag`

And child nodes: `MODULE`, `RESOURCE`, `EVENTS`, `ACTIONS`, `PARTDATA`, `VESSEL_NAMING`

### DiscoveryInfo

```csharp
public class DiscoveryInfo : IConfigNode
```

**Key properties**:
- `Level` (DiscoveryLevels) -- controls what's visible in tracking station
- `lastObservedTime` (double)
- `fadeUT` (double) -- when the object disappears
- `unobservedLifetime` (double)
- `referenceLifetime` (double)
- `objectSize` (UntrackedObjectClass)

**Persistence format** (DISCOVERY node):
```
state = <int cast of DiscoveryLevels>
lastObservedTime = <double>
lifetime = <double>
refTime = <double>
size = <int cast of UntrackedObjectClass>
```

**Critical method**:
```csharp
public bool HaveKnowledgeAbout(DiscoveryLevels lvl)
{
    return (lvl & Level) != 0;
}
```

For `DiscoveryLevels.StateVectors` (0x8) -- required for orbit display and targeting.
For `DiscoveryLevels.Owned` (-1, all bits) -- maximum visibility.

**For ghost vessels**: Use `DiscoveryLevels.Owned` to ensure full visibility everywhere.

### DiscoveryLevels (Flags enum)

```csharp
[Flags]
public enum DiscoveryLevels
{
    None = 0,
    Presence = 1,        // detected in tracking station
    Name = 4,            // name is known
    StateVectors = 8,    // orbit is known (REQUIRED for orbit lines)
    Appearance = 0x10,   // mass and type known (REQUIRED for correct filter category)
    Unowned = 0x1D,      // all above combined
    Owned = -1           // all flags, used for normal player vessels
}
```

**Orbit rendering requires `StateVectors`**. Map filter type-checking requires `Appearance`. Use `Owned` for ghosts.

---

## Priority 2 -- Orbit & Map Rendering

### OrbitDriver

```csharp
public class OrbitDriver : MonoBehaviour
```

**UpdateMode enum**:
```csharp
public enum UpdateMode
{
    TRACK_Phys,  // tracks rigidbody physics (loaded vessel off-rails)
    UPDATE,      // Keplerian propagation (unloaded vessel or on-rails)
    IDLE         // no updates (landed/splashed)
}
```

**Key fields**:
```csharp
public Orbit orbit;
public UpdateMode updateMode;
public OrbitRendererBase Renderer;
public Vessel vessel;
public CelestialBody celestialBody;
public Color orbitColor;
```

**Lifecycle**:
- `Start()`: Registers self in `Planetarium.Orbits`, calls `updateFromParameters()` for UPDATE mode
- `OnDestroy()`: Removes from `Planetarium.Orbits`, destroys Renderer
- `FixedUpdate()`: Calls `UpdateOrbit()` unless queued
- `UpdateOrbit()`: For `UPDATE` mode, calls `updateFromParameters()` (Keplerian propagation). For `TRACK_Phys`/`IDLE`, tracks rigidbody.

**For ghost vessels**: Set `updateMode = UpdateMode.UPDATE` so the orbit is Keplerian-propagated automatically. The OrbitDriver needs a valid `orbit` (from OrbitSnapshot.Load()). No physics needed.

### OrbitRendererBase

```csharp
public class OrbitRendererBase : MonoBehaviour
```

**Key fields**:
```csharp
public OrbitDriver driver;
public Vessel vessel;
public CelestialBody celestialBody;
public DiscoveryInfo discoveryInfo;
public Color orbitColor;
public Color nodeColor;
public DrawMode drawMode;
public DrawIcons drawIcons;
public bool isFocused;
```

**DrawMode enum**:
```csharp
public enum DrawMode { OFF, REDRAW_ONLY, REDRAW_AND_FOLLOW, REDRAW_AND_RECALCULATE }
```

**Line creation**:
```csharp
protected internal void MakeLine(ref VectorLine l)
```
Uses `MapView.OrbitLinesMaterial` for the line material. Creates a Vectrosity `VectorLine` with 180 segments.

**LateUpdate**: Calls `DrawOrbit(drawMode)` and `DrawNodes()`. For `IDLE` updateMode, passes `DrawMode.OFF` (no orbit drawing for landed vessels).

**Start()**: Sets default orbit color to grey `(0.5 * nodeColor)`, creates orbit line, finds MapObject, creates scaled space nodes (Ap/Pe/AN/DN icons).

### OrbitRenderer (subclass)

```csharp
public class OrbitRenderer : OrbitRendererBase
```

**DrawOrbit override** -- the key rendering gate:
```csharp
protected override void DrawOrbit(DrawMode mode)
{
    if (MapView.fetch == null) return;
    if (!MapView.MapIsEnabled) return;
    if (PlanetariumCamera.fetch.target == null) return;
    if (OrbitLine == null) return;

    bool isDebris = IsDebris();
    bool isUnfiltered = MapViewFiltering.CheckAgainstFilter(vessel);
    OrbitLine.active = GetActive(mode, isDebris, isUnfiltered);

    if (isDebris && !mouseOver) return;
    if (!isUnfiltered) return;

    base.DrawOrbit(mode);
}
```

**GetActive logic**: Orbit line is active when ALL of:
1. `mode != OFF`
2. `lineOpacity > 0`
3. Not debris (or mouse is hovering)
4. `orbitDisplayUnlocked` (tracking station upgrade level)
5. `discoveryInfo.HaveKnowledgeAbout(DiscoveryLevels.StateVectors)`
6. Passes `MapViewFiltering.CheckAgainstFilter`

**Start()**: Sets default vessel orbit color to `(0.71, 0.71, 0.71, 1.0)` grey. Sets `discoveryInfo = vessel.DiscoveryInfo`.

**For ghost vessels**: The OrbitRenderer is created automatically by `Vessel.AddOrbitRenderer()`. It reads discoveryInfo from the vessel. If `DiscoveryLevels.StateVectors` is set, orbit lines will appear.

### MapObject

```csharp
public class MapObject : MonoBehaviour
```

**ObjectType enum**:
```csharp
public enum ObjectType
{
    Null, Generic, CelestialBody, Vessel, ManeuverNode,
    Periapsis, Apoapsis, AscendingNode, DescendingNode,
    ApproachIntersect, CelestialBodyAtUT, PatchTransition,
    MENode, Site
}
```

**Key fields**:
```csharp
public Transform trf;
public Transform tgtRef;
public Vessel vessel;
public CelestialBody celestialBody;
public ObjectType type;
public Orbit orbit;
public IDiscoverable Discoverable;
public MapNode uiNode;       // the clickable UI icon
public string DisplayName;
```

**Lifecycle**:
- `Awake()`: Parents self under `ScaledSpace.Instance.transform`, calls `ScaledSpace.AddScaledSpaceObject(this)`
- `Start()`: If `tgtRef` is set, resolves `vessel`/`celestialBody` from it, sets `type`, `orbit`, `Discoverable`
- `OnDestroy()`: Calls `ScaledSpace.RemoveScaledSpaceObject(this)`
- `Terminate()`: Destroys uiNode and self

**Factory** (via ScaledMovement subclass):
```csharp
public static ScaledMovement Create(string name, Vessel vessel)
{
    ScaledMovement sm = new GameObject(name).AddComponent<ScaledMovement>();
    sm.tgtRef = vessel.transform;
    sm.type = ObjectType.Vessel;
    sm.vessel = FlightGlobals.ActiveVessel;  // NOTE: sets vessel to ActiveVessel!
    sm.orbit = vessel.orbit;
    return sm;
}
```

**Gotcha in ScaledMovement.Create**: The `vessel` field is set to `FlightGlobals.ActiveVessel`, NOT the passed vessel parameter! However, the `tgtRef` is set to the vessel's transform, and `Start()` resolves the correct vessel from `tgtRef.GetComponent<Vessel>()`. So `vessel` gets corrected in `Start()`.

**ScaledMovement.OnLateUpdate**: Updates position via `ScaledSpace.LocalToScaledSpace(vessel.GetWorldPos3D())`.

### Vessel.AddOrbitRenderer

This is the method that sets up map presence:

```csharp
private void AddOrbitRenderer()
{
    if (MapView.fetch == null) return;

    if (mapObject == null)
        mapObject = ScaledMovement.Create(GetDisplayName(), this);

    if (orbitRenderer == null)
    {
        orbitRenderer = GetComponent<OrbitRenderer>()
            ?? gameObject.AddComponent<OrbitRenderer>();
        orbitRenderer.driver = orbitDriver;
        orbitRenderer.vessel = this;
        orbitDriver.Renderer = orbitRenderer;
    }
}
```

**Called from**: `StartFromBackup()`, which is called from `ProtoVessel.Load()`.

**Critical requirement**: `MapView.fetch != null` -- this is only true in FLIGHT and TRACKSTATION scenes. If called before MapView is initialized, nothing happens.

---

## Priority 3 -- Tracking Station & Targeting

### SpaceTracking

The tracking station scene controller. Could not fully decompile due to size, but key observations from partial decompilation and cross-references:

- SpaceTracking iterates `FlightGlobals.Vessels` to populate its list
- It uses `MapViewFiltering.CheckAgainstFilter(vessel)` for filtering
- Vessels appear if they are in `FlightGlobals.fetch.vessels`
- The tracking station creates its own `OrbitRenderer` instances for vessels

**Ghost vessels automatically appear in the tracking station** if they are registered in `FlightGlobals.Vessels` (which happens via `FlightGlobals.AddVessel()` during `ProtoVessel.Load()`).

### MapViewFiltering

```csharp
public static bool CheckAgainstFilter(Vessel v)
```

The instance method `checkAgainstFilter` logic:

```csharp
private bool checkAgainstFilter(Vessel v)
{
    VesselType vesselType = VesselType.Unknown;  // default if not enough knowledge
    if (v.DiscoveryInfo.HaveKnowledgeAbout(DiscoveryLevels.Appearance))
        vesselType = v.vesselType;  // use actual type only if Appearance known

    // Active vessel and target always pass
    if (FlightGlobals.ActiveVessel == v) return true;
    if (FlightGlobals.fetch.VesselTarget?.GetVessel() == v) return true;

    // Type-based filtering
    return vesselType switch
    {
        VesselType.Debris => (filter & Debris) != 0,
        VesselType.SpaceObject => (filter & SpaceObjects) != 0,
        VesselType.Probe => (filter & Probes) != 0,
        // ... etc for all types
        VesselType.DeployedSciencePart => false,  // always hidden!
        _ => (filter & Unknown) != 0,  // fallback
    };
}
```

**For ghost vessels**: If `DiscoveryLevels.Appearance` is NOT set, the vessel is treated as `Unknown` type for filtering. With `DiscoveryLevels.Owned`, the actual `vesselType` is used. Choose a VesselType that makes sense (e.g., `Ship`, `Probe`) for the ghost to appear under the correct filter button.

**VesselTypeFilter flags**:
```csharp
[Flags]
public enum VesselTypeFilter
{
    None = 0, Debris = 1, Unknown = 2, SpaceObjects = 4,
    Probes = 8, Rovers = 0x10, Landers = 0x20, Ships = 0x40,
    Stations = 0x80, Bases = 0x100, EVAs = 0x200, Flags = 0x400,
    Plane = 0x800, Relay = 0x1000, Site = 0x2000,
    DeployedScienceController = 0x4000, All = -1
}
```

### ITargetable

```csharp
public interface ITargetable
{
    Transform GetTransform();
    Vector3 GetObtVelocity();
    Vector3 GetSrfVelocity();
    Vector3 GetFwdVector();
    Vessel GetVessel();
    string GetName();
    string GetDisplayName();
    Orbit GetOrbit();
    OrbitDriver GetOrbitDriver();
    VesselTargetModes GetTargetingMode();
    bool GetActiveTargetable();
}
```

**Vessel implements ITargetable**:
```csharp
public class Vessel : MonoBehaviour, IShipconstruct, ITargetable, IDiscoverable
```

Vessel's implementations:
- `GetTransform()` -> `ReferenceTransform`
- `GetOrbit()` -> `orbit` (from orbitDriver)
- `GetOrbitDriver()` -> `orbitDriver`
- `GetTargetingMode()` -> `VesselTargetModes.DirectionAndVelocity` (hardcoded)
- `GetActiveTargetable()` -> `false` (hardcoded)
- `GetVessel()` -> `this`

**VesselTargetModes**:
```csharp
public enum VesselTargetModes
{
    None,
    Direction,
    DirectionAndVelocity,
    DirectionVelocityAndOrientation
}
```

### FlightGlobals

**Key fields**:
```csharp
public List<Vessel> vessels;
public List<Vessel> vesselsLoaded;
public List<Vessel> vesselsUnloaded;
public DictionaryValueList<uint, Vessel> persistentVesselIds;
private ITargetable _vesselTarget;
```

**AddVessel**:
```csharp
public static void AddVessel(Vessel vessel)
{
    fetch.vessels.Add(vessel);
    if (vessel.loaded) fetch.vesselsLoaded.Add(vessel);
    else fetch.vesselsUnloaded.Add(vessel);
    PersistentVesselIds.Add(vessel.persistentId, vessel);
    // Also adds part persistent IDs
    GameEvents.onFlightGlobalsAddVessel.Fire(vessel);
}
```

**RemoveVessel**:
```csharp
public static void RemoveVessel(Vessel vessel)
{
    fetch.vessels.Remove(vessel);
    // ... removes from loaded/unloaded lists, persistent ID dicts
    GameEvents.onFlightGlobalsRemoveVessel.Fire(vessel);
}
```

**FindVessel**:
```csharp
public static Vessel FindVessel(Guid id)  // linear scan of vessels list
public static bool FindVessel(uint id, out Vessel vessel)  // uses PersistentVesselIds dict
```

**SetVesselTarget**:
```csharp
public void SetVesselTarget(ITargetable tgt, bool overrideInputLock = false)
```
Checks `ControlTypes.TARGETING` input lock. Sets `_vesselTarget = tgt`. If target is `IDiscoverable` without `StateVectors`, sets target mode to `None`. Otherwise sets target mode from `tgt.GetTargetingMode()`. Updates `orbitTargeter` on active vessel.

**For ghost targeting**: Ghost Vessel objects automatically implement ITargetable since Vessel does. Players can target ghost vessels via the map view like any other vessel, as long as `DiscoveryLevels.StateVectors` is set.

**GetUniquepersistentId**:
```csharp
public static uint GetUniquepersistentId()
```
Generates random uint via `Guid.NewGuid().GetHashCode()`, loops until no collision with existing loaded parts, unloaded parts, or vessel IDs. Returns 0-safe.

### Vessel

**Key map-related fields**:
```csharp
public OrbitDriver orbitDriver;       // drives Keplerian propagation
public OrbitRenderer orbitRenderer;   // draws orbit line
public MapObject mapObject;           // map view icon/position
public ProtoVessel protoVessel;
public VesselType vesselType;
private DiscoveryInfo discoveryInfo;  // private, exposed as property
public DiscoveryInfo DiscoveryInfo => discoveryInfo;
```

**Vessel.Awake()** creates:
- `discoveryInfo = new DiscoveryInfo(this)` (defaults to Owned, infinite lifetime)
- `VesselPrecalculate` component
- Subscribes to various events
- Calls `VesselModuleManager.AddModulesToVessel(this, vesselModules)`

**Vessel.Die()**:
1. Fires `GameEvents.onVesselWillDestroy`
2. Clears target if this vessel was targeted
3. Sets `state = DEAD`
4. Calls `DestroyVesselComponents()` (destroys OrbitTargeter, PatchedConicRenderer, PatchedConicSolver, OrbitRenderer, OrbitDriver, VesselDeltaV, CommNetVessel)
5. Calls `FlightGlobals.RemoveVessel(this)`
6. Destroys all part GameObjects
7. Fires `OnJustAboutToBeDestroyed()`
8. Destroys vessel GameObject

**Vessel cleanup** also handles `mapObject.Terminate()` in the full destruction path (found in the Die/destroy sequence around line 9187).

---

## Priority 4 -- Vessel Lifecycle & Events

### GameEvents (vessel-related)

```csharp
// Creation
EventData<Vessel> onNewVesselCreated        // when a brand new vessel is created
EventData<Vessel> onVesselCreate            // fired by ProtoVessel.Load() for new vessels
EventData<Vessel> onVesselPrecalcAssign     // during Vessel.Awake

// Loading/Unloading
EventData<Vessel> onVesselLoaded            // physics range load
EventData<Vessel> onVesselUnloaded
EventData<Vessel> onVesselGoOnRails
EventData<Vessel> onVesselGoOffRails

// Destruction
EventData<Vessel> onVesselWillDestroy       // BEFORE destruction begins
EventData<Vessel> onVesselDestroy           // during destruction
EventData<ProtoVessel> onVesselTerminated   // terminated from tracking station
EventData<ProtoVessel, bool> onVesselRecovered  // recovered

// State changes
EventData<Vessel> onVesselChange            // active vessel changed
EventData<Vessel, Vessel> onVesselSwitching
EventData<HostedFromToAction<Vessel, Vessel.Situations>> onVesselSituationChange
EventData<HostedFromToAction<Vessel, CelestialBody>> onVesselSOIChanged
EventData<Vessel> onVesselOrbitClosed
EventData<Vessel> onVesselOrbitEscaped
EventData<HostedFromToAction<Vessel, string>> onVesselRename

// FlightGlobals list changes
EventData<Vessel> onFlightGlobalsAddVessel
EventData<Vessel> onFlightGlobalsRemoveVessel

// ProtoVessel serialization
EventData<FromToAction<ProtoVessel, ConfigNode>> onProtoVesselSave
EventData<FromToAction<ProtoVessel, ConfigNode>> onProtoVesselLoad

// Persistent ID changes
EventData<uint, uint> onVesselPersistentIdChanged
```

### VesselType

```csharp
public enum VesselType
{
    Debris = 0,
    SpaceObject = 1,
    Unknown = 2,
    Probe = 3,
    Relay = 4,
    Rover = 5,
    Lander = 6,
    Ship = 7,
    Plane = 8,
    Station = 9,
    Base = 10,
    EVA = 11,
    Flag = 12,
    DeployedScienceController = 13,
    DeployedSciencePart = 14,
    DroppedPart = 15,
    DeployedGroundPart = 16
}
```

Note: `vessel.isCommandable` returns `vesselType > VesselType.Debris` (i.e., everything except Debris).

### FlightState

```csharp
public class FlightState
{
    public List<ProtoVessel> protoVessels;
    public double universalTime;
    public int activeVesselIdx;
    // ...
}
```

The FlightState constructor iterates `FlightGlobals.Vessels`, creates `ProtoVessel` for each non-DEAD vessel, and stores them. The `DECLUTTER_KSC` setting auto-cleans non-commandable, non-persistent, landed-at-KSC debris.

**For ghost persistence**: Ghost ProtoVessels added via `ProtoVessel.Load()` will be in `FlightGlobals.Vessels` and thus included in `FlightState` when the game saves. Set `persistent = true` and `autoClean = false` to prevent cleanup.

---

## Priority 5 -- Supplementary

### ScaledSpace

```csharp
public static float ScaleFactor => Instance.scaleFactor;  // default 6000
public static float InverseScaleFactor => 1f / Instance.scaleFactor;

public static Vector3d LocalToScaledSpace(Vector3d localSpacePoint)
{
    return localSpacePoint * InverseScaleFactor - totalOffset;
}

public static void AddScaledSpaceObject(MapObject t)  // adds to internal list
public static void RemoveScaledSpaceObject(MapObject t)
```

MapObject self-registers in `Awake()` and self-unregisters in `OnDestroy()`.

### MapView

```csharp
public static Material OrbitLinesMaterial  // used by OrbitRendererBase.MakeLine
public static bool Draw3DLines
public static bool MapIsEnabled            // true when map view is open
public static MapView fetch               // singleton
public static PlanetariumCamera MapCamera => PlanetariumCamera.fetch;
```

### Planetarium

```csharp
public static Planetarium fetch;
public List<OrbitDriver> orbits;           // all registered OrbitDrivers
public static QuaternionD Rotation         // celestial frame rotation
public static List<OrbitDriver> Orbits => fetch.orbits;
public static double GetUniversalTime()
```

OrbitDrivers self-register in `Planetarium.Orbits` during their `Start()` and self-unregister in `OnDestroy()`.

### OrbitSnapshot

```csharp
public class OrbitSnapshot
{
    public double semiMajorAxis, eccentricity, inclination;
    public double argOfPeriapsis, LAN, meanAnomalyAtEpoch, epoch;
    public int ReferenceBodyIndex;

    public Orbit Load()  // creates Orbit object from snapshot
}
```

ConfigNode keys: `SMA`, `ECC`, `INC`, `LPE`, `LAN`, `MNA`, `EPH`, `REF`

---

## Implementation Guide: Minimal Ghost ProtoVessel

### Minimal ConfigNode for an orbital ghost

```
VESSEL
{
    pid = <new-guid>
    persistentId = <unique-uint>
    name = Ghost: My Vessel
    type = Ship
    sit = ORBITING
    landed = False
    splashed = False
    met = 0
    lct = <current-UT>
    lastUT = <current-UT>
    root = 0
    lat = 0
    lon = 0
    alt = <from-orbit>
    hgt = -1
    nrm = 0,1,0
    rot = 0,0,0,1
    CoM = 0,0,0
    stg = 0
    prst = True
    ref = <part-persistent-id>
    ctrl = False
    vesselSpawning = False
    ORBIT
    {
        SMA = <semi-major-axis>
        ECC = <eccentricity>
        INC = <inclination>
        LPE = <arg-periapsis>
        LAN = <LAN>
        MNA = <mean-anomaly>
        EPH = <epoch>
        REF = <body-index>
    }
    PART
    {
        name = <any-valid-part>
        uid = <id>
        mid = <id>
        persistentId = <unique-uint>
        parent = 0
        position = 0,0,0
        rotation = 0,0,0,1
        mirror = 1,1,1
        srfN = None, -1
        connected = True
        attached = True
        state = 0
        temp = 300
        mass = 0.01
        expt = 0
    }
    ACTIONGROUPS { }
    DISCOVERY
    {
        state = -1
        lastObservedTime = <current-UT>
        lifetime = Infinity
        refTime = Infinity
        size = 2
    }
}
```

### Runtime creation sequence

```csharp
// 1. Build ConfigNode (as above, or use ProtoVessel.CreateVesselNode for orbital)
ConfigNode vesselNode = BuildGhostVesselNode(orbit, name);

// 2. Create ProtoVessel from ConfigNode
ProtoVessel pv = new ProtoVessel(vesselNode, HighLogic.CurrentGame);

// 3. Load into game (creates Vessel GameObject, OrbitDriver, registers in FlightGlobals)
pv.Load(HighLogic.CurrentGame.flightState);
// This fires: GameEvents.onVesselCreate

// 4. The vessel now has:
//    - pv.vesselRef (the Vessel component)
//    - OrbitDriver with Keplerian propagation
//    - MapObject (if MapView.fetch != null)
//    - OrbitRenderer (orbit line)
//    - Appears in tracking station
//    - Targetable via FlightGlobals.SetVesselTarget
```

### Cleanup sequence

```csharp
// To remove a ghost vessel:
Vessel ghost = pv.vesselRef;
ghost.Die();
// This handles: events, target clearing, component destruction,
//               FlightGlobals removal, mapObject termination, GameObject destruction
```

### Key gotchas

1. **MapView.fetch must be non-null** for orbit rendering setup. In TRACKSTATION this is always true. In FLIGHT it's true after scene load. If creating ghosts very early, `AddOrbitRenderer()` may silently skip -- the vessel will exist but have no map presence until the next `AddOrbitRenderer()` call.

2. **persistentId collisions**: Both vessel and part persistentIds are validated. Use `FlightGlobals.GetUniquepersistentId()` or let the ProtoVessel constructor handle it.

3. **Discovery state = Owned (-1)** is essential. Without `StateVectors` (0x8), the orbit line won't draw. Without `Appearance` (0x10), the vessel shows as "Unknown" in filters.

4. **vesselSpawning = false** for ghosts. If true, KSP applies special ground-positioning logic.

5. **Part name must exist in PartLoader**. If the part name is invalid, `ProtoVessel.Load()` will show a missing parts popup dialog and abort.

6. **FlightState persistence**: Ghost vessels in `FlightGlobals.Vessels` will be saved by FlightState. Set `persistent = true` to survive DECLUTTER_KSC. On next load, they'll be recreated from the .sfs file. You may need to intercept `onProtoVesselSave`/`onProtoVesselLoad` or mark them for cleanup.

7. **OrbitDriver.UpdateMode.UPDATE** is required for unloaded orbital vessels. `IDLE` is for landed (no orbit updates). `TRACK_Phys` requires a loaded vessel with rigidbodies.

8. **ScaledMovement.Create sets vessel = ActiveVessel initially**, but `MapObject.Start()` corrects it from `tgtRef`. This means there's a brief frame where `mapObject.vessel` is wrong. Don't query it before `Start()` runs.

9. **Vessel.Die() fires onVesselWillDestroy** which MapViewFiltering listens to. It also clears the target if this vessel was targeted. Full cleanup chain handles everything.

10. **VesselType affects filter visibility**. `DeployedSciencePart` is ALWAYS hidden from filters (`return false`). `DroppedPart` and `DeployedGroundPart` fall through to `Unknown` filter. Choose `Ship` or `Probe` for predictable ghost visibility.

11. **Vessel.Awake() creates DiscoveryInfo with Owned level** by default. But `ProtoVessel.Load()` then overwrites it from the DISCOVERY ConfigNode. So the DISCOVERY node in your ConfigNode is what matters.

12. **autoClean = false and persistent = true** are both needed. `autoClean` triggers deletion of debris near KSC. `persistent` prevents MAX_VESSELS_BUDGET culling.
