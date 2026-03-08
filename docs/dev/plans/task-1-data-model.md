# Task 1: RecordingTree Data Model + Serialization - Implementation Plan

## Overview

Create the core data structures for the recording tree (RecordingTree, BranchPoint, SurfacePosition, TerminalState enum) and prove they round-trip through ConfigNode serialization. Add new tree-related fields to the existing Recording class. All work is pure data model and serialization - no runtime behavior, no KSP event subscriptions, no UI changes.

---

## 1. New Files to Create

### 1.1 `Source/Parsek/TerminalState.cs`

```csharp
namespace Parsek
{
    public enum TerminalState
    {
        Orbiting   = 0,
        Landed     = 1,
        Splashed   = 2,
        SubOrbital = 3,
        Destroyed  = 4,
        Recovered  = 5,
        Docked     = 6,
        Boarded    = 7
    }
}
```

Follows the same pattern as `PartEventType` - explicit int values for stable serialization. Serialized as `(int)terminalState` in ConfigNode values.

### 1.2 `Source/Parsek/BranchPoint.cs`

```csharp
using System.Collections.Generic;

namespace Parsek
{
    public enum BranchPointType
    {
        Undock = 0,
        EVA    = 1,
        Dock   = 2,
        Board  = 3
    }

    public class BranchPoint
    {
        public string id;
        public double ut;
        public BranchPointType type;
        public List<string> parentRecordingIds = new List<string>();
        public List<string> childRecordingIds = new List<string>();

        public override string ToString()
        {
            return $"BP id={id ?? "?"} type={type} ut={ut:F1} " +
                   $"parents={parentRecordingIds.Count} children={childRecordingIds.Count}";
        }
    }
}
```

**Design note:** `BranchPoint` is a `class` (not struct) because it contains `List<string>` fields. A struct with reference-type fields has copy-semantics footguns - copying the struct shares the lists, leading to aliasing bugs. As a class, BranchPoint has clear reference semantics. Lists are initialized in field declarations to avoid null checks.

### 1.3 `Source/Parsek/SurfacePosition.cs`

```csharp
using UnityEngine;

namespace Parsek
{
    public enum SurfaceSituation
    {
        Landed   = 0,
        Splashed = 1
    }

    public struct SurfacePosition
    {
        public string body;
        public double latitude;
        public double longitude;
        public double altitude;
        public Quaternion rotation;
        public SurfaceSituation situation;

        public override string ToString()
        {
            return $"body={body ?? "?"} lat={latitude:F4} lon={longitude:F4} " +
                   $"alt={altitude:F1} sit={situation}";
        }
    }
}
```

### 1.4 `Source/Parsek/RecordingTree.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    public class RecordingTree
    {
        // --- Serialized fields ---
        public string Id;
        public string TreeName;                     // root vessel name at launch
        public string RootRecordingId;
        public string ActiveRecordingId;            // nullable: null if all background

        public Dictionary<string, RecordingStore.Recording> Recordings
            = new Dictionary<string, RecordingStore.Recording>();
        public List<BranchPoint> BranchPoints = new List<BranchPoint>();

        // Tree-level resource tracking
        public double PreTreeFunds;
        public double PreTreeScience;
        public float PreTreeReputation;
        public double DeltaFunds;
        public double DeltaScience;
        public float DeltaReputation;

        // --- Runtime-only fields (not serialized) ---
        // Maps vessel persistentId -> recordingId for background vessel lookup.
        // Rebuilt from Recordings on load via RebuildBackgroundMap().
        public Dictionary<uint, string> BackgroundMap
            = new Dictionary<uint, string>();

        // --- Serialization ---
        public void Save(ConfigNode treeNode) { ... }
        public static RecordingTree Load(ConfigNode treeNode) { ... }
        public void RebuildBackgroundMap() { ... }
    }
}
```

Full field inventory and serialization details are in sections 3 and 7 below.

---

## 2. Existing Files to Modify

### 2.1 `Source/Parsek/RecordingStore.cs` - Recording class

Add these fields to the `Recording` class (all nullable/defaulted so existing recordings remain valid):

```csharp
// --- Tree linkage (null for legacy/standalone recordings) ---
public string TreeId;                          // null = standalone (pre-tree recording)
public uint VesselPersistentId;                // 0 = not set

// --- Terminal state ---
public TerminalState? TerminalStateValue;      // null = not yet terminated (still recording or legacy)

// Terminal orbit (for Orbiting/SubOrbital terminal state)
// Stored as Keplerian elements to avoid runtime Orbit object dependency in tests.
public double TerminalOrbitInclination;
public double TerminalOrbitEccentricity;
public double TerminalOrbitSemiMajorAxis;
public double TerminalOrbitLAN;
public double TerminalOrbitArgumentOfPeriapsis;
public double TerminalOrbitMeanAnomalyAtEpoch;
public double TerminalOrbitEpoch;
public string TerminalOrbitBody;

// Terminal surface position (for Landed/Splashed terminal state)
public SurfacePosition? TerminalPosition;      // null if not landed/splashed

// Background recording: surface position for landed/splashed vessels
public SurfacePosition? SurfacePos;            // null if not a background landed vessel

// Branch linkage
public string ParentBranchPointId;             // null for root recording
public string ChildBranchPointId;              // null for leaf recordings
```

**Why Keplerian elements instead of an Orbit object for terminalOrbit:**
- `Orbit` is a KSP runtime class requiring a `CelestialBody` reference to construct. It cannot be instantiated in unit tests without the full KSP game loaded.
- Keplerian elements (7 doubles + body name) are the same data already used by `OrbitSegment` and can be stored/loaded identically.
- At runtime, `RecordingTree` or `VesselSpawner` can construct an `Orbit` object from these elements when needed (same as existing orbit segment playback).
- The elements are identical to `OrbitSegment` fields minus `startUT`/`endUT`: `inclination`, `eccentricity`, `semiMajorAxis`, `longitudeOfAscendingNode`, `argumentOfPeriapsis`, `meanAnomalyAtEpoch`, `epoch`, `bodyName`.

**What NOT to add to Recording:**
- `startUT` / `endUT` as stored fields: these remain computed properties from `Points[0].ut` / `Points[last].ut`. For tree recordings that are purely background (only orbit segments, no trajectory points), `startUT` and `endUT` must be stored explicitly. Add:

```csharp
// Explicit UT range for recordings that may have no trajectory points
// (background-only recordings). When Points.Count > 0, these are ignored
// in favor of Points[0].ut / Points[last].ut.
// Default is double.NaN (not set). 0.0 is a valid KSP UT.
public double ExplicitStartUT = double.NaN;
public double ExplicitEndUT = double.NaN;
```

Update the `StartUT`/`EndUT` properties:

```csharp
public double StartUT => Points.Count > 0 ? Points[0].ut :
                         !double.IsNaN(ExplicitStartUT) ? ExplicitStartUT : 0.0;
public double EndUT => Points.Count > 0 ? Points[Points.Count - 1].ut :
                       !double.IsNaN(ExplicitEndUT) ? ExplicitEndUT : 0.0;
```

### 2.2 `Source/Parsek/RecordingStore.cs` - Static methods

No changes to the static `RecordingStore` methods in Task 1. The tree serialization lives in `RecordingTree.Save/Load`. Integration with `RecordingStore.CommittedRecordings` and `ParsekScenario` is deferred to Task 7/11.

### 2.3 `Source/Parsek/RecordingStore.cs` - ApplyPersistenceArtifactsFrom

Add copying of the new fields:

```csharp
TreeId = source.TreeId;
VesselPersistentId = source.VesselPersistentId;
TerminalStateValue = source.TerminalStateValue;
TerminalOrbitInclination = source.TerminalOrbitInclination;
// ... all terminal orbit fields ...
TerminalOrbitBody = source.TerminalOrbitBody;
TerminalPosition = source.TerminalPosition;
SurfacePos = source.SurfacePos;
ParentBranchPointId = source.ParentBranchPointId;
ChildBranchPointId = source.ChildBranchPointId;
ExplicitStartUT = source.ExplicitStartUT;
ExplicitEndUT = source.ExplicitEndUT;
```

---

## 3. Serialization Format

### 3.1 RECORDING_TREE ConfigNode structure

```
RECORDING_TREE
{
    id = <guid-string>
    treeName = <string>
    rootRecordingId = <guid-string>
    activeRecordingId = <guid-string>          // omitted when null
    preTreeFunds = <double-R>
    preTreeScience = <double-R>
    preTreeRep = <float-R>
    deltaFunds = <double-R>
    deltaScience = <double-R>
    deltaRep = <float-R>

    RECORDING
    {
        recordingId = <guid-string>
        treeId = <guid-string>
        vesselName = <string>
        vesselPersistentId = <uint>            // 0 if not set
        explicitStartUT = <double-R>           // omitted when NaN
        explicitEndUT = <double-R>             // omitted when NaN
        terminalState = <int>                  // omitted when null (recording still active)
        parentBranchPointId = <guid-string>    // omitted when null (root)
        childBranchPointId = <guid-string>     // omitted when null (leaf)

        // Terminal orbit fields (only present when terminalState = 0 (Orbiting) or 3 (SubOrbital))
        tOrbInc = <double-R>
        tOrbEcc = <double-R>
        tOrbSma = <double-R>
        tOrbLan = <double-R>
        tOrbArgPe = <double-R>
        tOrbMna = <double-R>
        tOrbEpoch = <double-R>
        tOrbBody = <string>

        // Terminal position (only present when terminalState = 1 (Landed) or 2 (Splashed))
        TERMINAL_POSITION
        {
            body = <string>
            lat = <double-R>
            lon = <double-R>
            alt = <double-R>
            rotX = <float-R>
            rotY = <float-R>
            rotZ = <float-R>
            rotW = <float-R>
            situation = <int>
        }

        // Surface position for background landed/splashed recordings
        // (only present when recording has a background surface position)
        SURFACE_POSITION
        {
            body = <string>
            lat = <double-R>
            lon = <double-R>
            alt = <double-R>
            rotX = <float-R>
            rotY = <float-R>
            rotZ = <float-R>
            rotW = <float-R>
            situation = <int>
        }

        // Existing recording metadata (same keys as current system):
        recordingFormatVersion = <int>
        ghostGeometryVersion = <int>
        loopPlayback = <bool>
        loopPauseSeconds = <double-R>
        playbackEnabled = <bool>               // omitted when true (default)
        // ... other existing metadata fields ...

        // NOTE: trajectory points, orbit segments, part events, and snapshots
        // remain in external sidecar files (.prec, _vessel.craft, _ghost.craft, .pcrf)
        // referenced by recordingId. The tree file holds metadata only.
    }

    RECORDING
    {
        // ... second recording ...
    }

    BRANCH_POINT
    {
        id = <guid-string>
        ut = <double-R>
        type = <int>                           // 0=Undock, 1=EVA, 2=Dock, 3=Board
        parentId = <guid-string>               // repeated: one per parent
        parentId = <guid-string>               // (2 entries for Dock/Board)
        childId = <guid-string>                // repeated: one per child
        childId = <guid-string>                // (2 entries for Undock/EVA)
    }

    BRANCH_POINT
    {
        // ... second branch point ...
    }
}
```

### 3.2 Key naming conventions

Following existing patterns observed in the codebase:

| Pattern | Example from codebase | New usage |
|---|---|---|
| camelCase keys | `recordingId`, `vesselName`, `startUT` | `treeId`, `rootRecordingId`, `activeRecordingId` |
| Abbreviated orbit keys | `inc`, `ecc`, `sma`, `lan`, `argPe`, `mna`, `epoch`, `body` (OrbitSegment) | `tOrbInc`, `tOrbEcc`, `tOrbSma`, `tOrbLan`, `tOrbArgPe`, `tOrbMna`, `tOrbEpoch`, `tOrbBody` (prefixed with `tOrb` to distinguish from OrbitSegment keys in .prec files) |
| Rotation components | `rotX`, `rotY`, `rotZ`, `rotW` (TrajectoryPoint) | Same in TERMINAL_POSITION / SURFACE_POSITION |
| Geographic coords | `lat`, `lon`, `alt` (TrajectoryPoint) | Same in TERMINAL_POSITION / SURFACE_POSITION |
| Enum as int | `type` key with `(int)enumValue` (PartEvent) | `terminalState`, `type`, `situation` |
| Omit when null/default | `parentRecordingId` omitted when null | `activeRecordingId`, `terminalState`, `parentBranchPointId`, `childBranchPointId` omitted when null |
| Omit when default bool | `playbackEnabled` omitted when true | Same |
| Repeated keys for lists | (new pattern, documented in design: "one `parentId = <id>` per parent") | `parentId`, `childId` on BRANCH_POINT |

### 3.3 Float/double serialization

All `float` and `double` values use `ToString("R", CultureInfo.InvariantCulture)` for serialization, parsed with `NumberStyles.Float` + `CultureInfo.InvariantCulture` on deserialization. This is the established codebase convention (see `SerializeTrajectoryInto`, `SaveRecordingMetadata`).

### 3.4 Nullable field handling

| Field | Type | When null/absent |
|---|---|---|
| `ActiveRecordingId` | `string` | Omit key entirely from ConfigNode |
| `TerminalStateValue` | `TerminalState?` | Omit `terminalState` key |
| `TerminalPosition` | `SurfacePosition?` | Omit `TERMINAL_POSITION` child node |
| `SurfacePos` | `SurfacePosition?` | Omit `SURFACE_POSITION` child node |
| `ParentBranchPointId` | `string` | Omit key |
| `ChildBranchPointId` | `string` | Omit key |
| `TerminalOrbitBody` | `string` | Omit all `tOrb*` keys (they are a group) |
| `ExplicitStartUT` | `double` | Omit when `double.IsNaN` |
| `ExplicitEndUT` | `double` | Omit when `double.IsNaN` |

On deserialization, missing keys leave fields at their default values (`null` for strings/nullable structs, `0` for numerics, `NaN` for ExplicitStartUT/ExplicitEndUT).

---

## 4. TerminalState Enum

```csharp
public enum TerminalState
{
    Orbiting   = 0,   // in orbit at recording end
    Landed     = 1,   // on the surface, landed
    Splashed   = 2,   // on the surface, in water
    SubOrbital = 3,   // suborbital trajectory (will crash or land)
    Destroyed  = 4,   // vessel was destroyed
    Recovered  = 5,   // vessel was recovered (e.g. StageRecovery)
    Docked     = 6,   // recording ended because vessel docked (merge event)
    Boarded    = 7    // recording ended because kerbal boarded vessel (merge event)
}
```

Serialized as `(int)value` via `ToString(CultureInfo.InvariantCulture)`. Deserialized with `int.TryParse` + `Enum.IsDefined` guard (same pattern as `PartEventType` in `DeserializeTrajectoryFrom`).

---

## 5. BranchPointType Enum

```csharp
public enum BranchPointType
{
    Undock = 0,   // 1 parent -> 2 children (vessel splits)
    EVA    = 1,   // 1 parent -> 2 children (kerbal exits vessel)
    Dock   = 2,   // 2 parents -> 1 child (vessels merge)
    Board  = 3    // 2 parents -> 1 child (kerbal enters vessel)
}
```

Same serialization pattern as TerminalState.

---

## 6. SurfacePosition Struct

### Fields

| Field | Type | ConfigNode Key | Serialization |
|---|---|---|---|
| `body` | `string` | `body` | Raw string |
| `latitude` | `double` | `lat` | `ToString("R", ic)` |
| `longitude` | `double` | `lon` | `ToString("R", ic)` |
| `altitude` | `double` | `alt` | `ToString("R", ic)` |
| `rotation` | `Quaternion` | `rotX`, `rotY`, `rotZ`, `rotW` | Each component: `float.ToString("R", ic)` |
| `situation` | `SurfaceSituation` | `situation` | `(int)situation` via `ToString(ic)` |

### SurfaceSituation Enum

```csharp
public enum SurfaceSituation
{
    Landed   = 0,
    Splashed = 1
}
```

### Serialization helper methods

Add static serialization helpers to `SurfacePosition` (or as extension methods on the struct):

```csharp
public static void SaveInto(ConfigNode node, SurfacePosition pos)
{
    var ic = CultureInfo.InvariantCulture;
    node.AddValue("body", pos.body ?? "");
    node.AddValue("lat", pos.latitude.ToString("R", ic));
    node.AddValue("lon", pos.longitude.ToString("R", ic));
    node.AddValue("alt", pos.altitude.ToString("R", ic));
    node.AddValue("rotX", pos.rotation.x.ToString("R", ic));
    node.AddValue("rotY", pos.rotation.y.ToString("R", ic));
    node.AddValue("rotZ", pos.rotation.z.ToString("R", ic));
    node.AddValue("rotW", pos.rotation.w.ToString("R", ic));
    node.AddValue("situation", ((int)pos.situation).ToString(ic));
}

public static SurfacePosition LoadFrom(ConfigNode node)
{
    var inv = NumberStyles.Float;
    var ic = CultureInfo.InvariantCulture;
    var pos = new SurfacePosition();
    pos.body = node.GetValue("body") ?? "Kerbin";
    double.TryParse(node.GetValue("lat"), inv, ic, out pos.latitude);
    double.TryParse(node.GetValue("lon"), inv, ic, out pos.longitude);
    double.TryParse(node.GetValue("alt"), inv, ic, out pos.altitude);
    float rx, ry, rz, rw;
    float.TryParse(node.GetValue("rotX"), inv, ic, out rx);
    float.TryParse(node.GetValue("rotY"), inv, ic, out ry);
    float.TryParse(node.GetValue("rotZ"), inv, ic, out rz);
    float.TryParse(node.GetValue("rotW"), inv, ic, out rw);
    pos.rotation = new Quaternion(rx, ry, rz, rw);
    int sitInt;
    if (int.TryParse(node.GetValue("situation"), NumberStyles.Integer, ic, out sitInt)
        && Enum.IsDefined(typeof(SurfaceSituation), sitInt))
        pos.situation = (SurfaceSituation)sitInt;
    return pos;
}
```

---

## 7. RecordingTree - Serialized vs Runtime Fields

### Serialized fields (written to RECORDING_TREE ConfigNode)

| Field | Type | ConfigNode Key | Notes |
|---|---|---|---|
| `Id` | `string` | `id` | GUID, `Guid.NewGuid().ToString("N")` |
| `TreeName` | `string` | `treeName` | Root vessel name at launch |
| `RootRecordingId` | `string` | `rootRecordingId` | |
| `ActiveRecordingId` | `string` | `activeRecordingId` | Omit when null |
| `Recordings` | `Dict<string, Recording>` | Child `RECORDING` nodes | Key = recordingId, one node per recording |
| `BranchPoints` | `List<BranchPoint>` | Child `BRANCH_POINT` nodes | One node per branch point |
| `PreTreeFunds` | `double` | `preTreeFunds` | `ToString("R", ic)` |
| `PreTreeScience` | `double` | `preTreeScience` | `ToString("R", ic)` |
| `PreTreeReputation` | `float` | `preTreeRep` | `ToString("R", ic)` |
| `DeltaFunds` | `double` | `deltaFunds` | `ToString("R", ic)` |
| `DeltaScience` | `double` | `deltaScience` | `ToString("R", ic)` |
| `DeltaReputation` | `float` | `deltaRep` | `ToString("R", ic)` |

### Runtime-only fields (NOT serialized)

| Field | Type | Rebuilt via |
|---|---|---|
| `BackgroundMap` | `Dict<uint, string>` | `RebuildBackgroundMap()` after load |

### RebuildBackgroundMap logic

```
Clear BackgroundMap.
For each recording in Recordings.Values:
    If recording.VesselPersistentId != 0
       AND recording.TerminalStateValue == null  (still active/recording)
       AND recording.RecordingId != ActiveRecordingId  (not the active one)
    Then:
        BackgroundMap[recording.VesselPersistentId] = recording.RecordingId
```

This rebuilds the mapping of which vessel persistentIds are being background-recorded. Called at the end of `RecordingTree.Load()`.

---

## 8. Round-Trip Correctness - Special Handling

### 8.1 Quaternion (rotation in SurfacePosition)

Quaternions are stored as 4 separate float keys (`rotX/Y/Z/W`), same as `TrajectoryPoint`. Round-trip uses `float.ToString("R", ic)` which preserves all significant digits. No normalization on load (matches existing behavior).

### 8.2 Terminal orbit (Keplerian elements)

Stored as 7 doubles + 1 string, same pattern as `OrbitSegment` serialization in `SerializeTrajectoryInto`. The `tOrb` prefix distinguishes them from OrbitSegment keys that might appear in the same parent node (though in the tree format they are in different scopes - the RECORDING node vs. the .prec file). The prefix is a defensive measure against future refactoring.

All `tOrb*` keys are written/read as a group. If `TerminalOrbitBody` is null or empty, none of the `tOrb*` keys are written. On load, if `tOrbBody` is absent, all terminal orbit fields remain at their default (0.0).

### 8.3 ConfigNode snapshots (VesselSnapshot, GhostVisualSnapshot)

These remain in external sidecar files (.prec, _vessel.craft, _ghost.craft) referenced by recordingId, exactly as today. The tree file (`RECORDING_TREE` node) holds metadata only. `RecordingStore.SaveRecordingFiles` / `LoadRecordingFiles` handle sidecar I/O unchanged.

**Note:** Task 1 does NOT call `SaveRecordingFiles`/`LoadRecordingFiles`. Task 1 is pure ConfigNode round-trip - no file I/O. The tree's `Save`/`Load` methods only handle the RECORDING_TREE ConfigNode structure. File I/O integration is deferred to Task 7/11 when the tree is integrated with `ParsekScenario`.

### 8.4 Nullable TerminalState

`TerminalState?` is serialized by checking `HasValue`:
- **Save:** if `rec.TerminalStateValue.HasValue`, write `terminalState = (int)rec.TerminalStateValue.Value`.
- **Load:** if `terminalState` key is present, parse int and set `rec.TerminalStateValue = (TerminalState)parsedInt`. If absent, leave as `null`.

### 8.5 Nullable SurfacePosition

`SurfacePosition?` is serialized as child ConfigNode:
- **Save:** if `rec.TerminalPosition.HasValue`, add child node `TERMINAL_POSITION` and call `SurfacePosition.SaveInto`.
- **Load:** if child node `TERMINAL_POSITION` exists, call `SurfacePosition.LoadFrom` and assign.

Same pattern for `SurfacePos` with `SURFACE_POSITION` node name.

### 8.6 List serialization in BranchPoint (repeated keys)

`parentRecordingIds` and `childRecordingIds` use the KSP ConfigNode repeated-key pattern:

```
BRANCH_POINT
{
    parentId = abc123
    parentId = def456
    childId = ghi789
}
```

- **Save:** for each id in the list, `node.AddValue("parentId", id)` / `node.AddValue("childId", id)`.
- **Load:** `string[] parentIds = node.GetValues("parentId")` returns all values for that key. Convert to `List<string>`.

This is the standard KSP pattern (e.g., how `CREW` members are listed in vessel nodes).

### 8.7 Dictionary serialization (Recordings)

The `Recordings` dictionary is serialized as repeated `RECORDING` child nodes. Each has a `recordingId` value that serves as the dictionary key.

- **Save:** iterate `Recordings.Values`, add `RECORDING` child node for each, write `recordingId` as a value inside.
- **Load:** iterate `RECORDING` child nodes, read `recordingId`, construct `Recording`, add to dictionary keyed by `recordingId`.

---

## 9. Unit Tests

All tests go in `Source/Parsek.Tests/RecordingTreeTests.cs`.

Test class setup (following existing pattern):

```csharp
using System.Collections.Generic;
using System.Globalization;
using Parsek.Tests.Generators;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RecordingTreeTests
    {
        public RecordingTreeTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }
    }
}
```

### 9.1 Test cases

#### SurfacePosition round-trip

**`SurfacePosition_SaveLoad_RoundTrips`**
- Create a `SurfacePosition` with non-trivial values (body="Mun", lat=-0.5, lon=23.4, alt=1234.5, rotation=non-identity quaternion, situation=Landed).
- `SaveInto` a new ConfigNode.
- `LoadFrom` that ConfigNode.
- Assert all fields match within float tolerance for rotation, exact for doubles.

**`SurfacePosition_Splashed_PreservesSituation`**
- Same as above with `situation=Splashed`.
- Assert situation round-trips correctly.

#### BranchPoint serialization

**`BranchPoint_Undock_SerializesParentsAndChildren`**
- Create a BranchPoint with type=Undock, 1 parent, 2 children.
- Serialize to ConfigNode (use the tree's internal serialize method).
- Deserialize from that ConfigNode.
- Assert: id, ut, type, parentRecordingIds (count=1, correct id), childRecordingIds (count=2, correct ids).

**`BranchPoint_Dock_SerializesTwoParentsOneChild`**
- Create a BranchPoint with type=Dock, 2 parents, 1 child.
- Round-trip.
- Assert parent count=2, child count=1, all ids correct.

#### TerminalState enum

**`TerminalState_AllValues_RoundTripAsInts`**
- For each value in the enum (0-7), serialize as int string, parse back, assert match.
- Ensures no gaps or misnumbered values.

#### Recording tree with single node (simplest case)

**`RecordingTree_SingleNode_RoundTrips`**
- Create a RecordingTree with one Recording (root, no branches).
- Set tree-level fields (Id, TreeName, RootRecordingId, resource fields).
- Set recording fields (RecordingId, VesselName, VesselPersistentId=12345, ExplicitStartUT=100, ExplicitEndUT=200).
- `tree.Save(configNode)`.
- `RecordingTree.Load(configNode)`.
- Assert: tree id, name, root id match. Recording count=1. Recording fields match. BranchPoints empty.

#### Recording tree with undock branch

**`RecordingTree_UndockBranch_RoundTrips`**
- Create tree with 3 recordings: root (R1), child1 (R2), child2 (R3).
- Root has ChildBranchPointId set.
- Children have ParentBranchPointId set.
- One BranchPoint (type=Undock, parent=[R1], children=[R2, R3]).
- Round-trip.
- Assert: all recording fields, branch point fields, parent/child ids.

#### Recording tree with dock merge

**`RecordingTree_DockMerge_RoundTrips`**
- Tree with 4 recordings: root R1, two children R2+R3 (from undock), merged child R4 (from dock).
- Two BranchPoints: BP1 (Undock: R1 -> R2, R3), BP2 (Dock: R2, R3 -> R4).
- R4 is the leaf.
- Round-trip.
- Assert: all branch point topology preserved. R4 has ParentBranchPointId=BP2. R1 has ChildBranchPointId=BP1. R2 and R3 have both ParentBranchPointId=BP1 and ChildBranchPointId=BP2.

#### Terminal state fields

**`Recording_OrbitalTerminalState_RoundTrips`**
- Recording with TerminalStateValue=Orbiting.
- Set all `TerminalOrbit*` fields to non-zero values.
- Round-trip through tree Save/Load.
- Assert all terminal orbit fields preserved.

**`Recording_LandedTerminalState_RoundTrips`**
- Recording with TerminalStateValue=Landed.
- Set TerminalPosition to a non-trivial SurfacePosition.
- Round-trip.
- Assert TerminalPosition fields preserved.

**`Recording_DestroyedTerminalState_NoOrbitNoPosition`**
- Recording with TerminalStateValue=Destroyed.
- No terminal orbit or position.
- Round-trip.
- Assert TerminalStateValue=Destroyed, TerminalPosition=null, TerminalOrbitBody=null.

**`Recording_NullTerminalState_OmitsKey`**
- Recording with TerminalStateValue=null (still recording).
- Save to ConfigNode.
- Assert `terminalState` key is absent.
- Load - assert TerminalStateValue is null.

#### Background map rebuild

**`RecordingTree_RebuildBackgroundMap_CorrectEntries`**
- Tree with 3 recordings:
  - R1: VesselPersistentId=100, TerminalStateValue=Docked (terminated).
  - R2: VesselPersistentId=200, TerminalStateValue=null (still recording), not ActiveRecordingId.
  - R3: VesselPersistentId=300, TerminalStateValue=null (still recording), IS ActiveRecordingId.
- Call `RebuildBackgroundMap()`.
- Assert: BackgroundMap contains {200 -> R2.RecordingId}. Does NOT contain 100 (terminated) or 300 (active).

**`RecordingTree_RebuildBackgroundMap_ZeroPidExcluded`**
- Recording with VesselPersistentId=0. Not added to BackgroundMap.

#### Resource fields

**`RecordingTree_ResourceFields_RoundTrip`**
- Set non-zero values for PreTreeFunds, PreTreeScience, PreTreeReputation, DeltaFunds, DeltaScience, DeltaReputation.
- Round-trip.
- Assert all values preserved.

#### SurfacePos (background landed recording)

**`Recording_SurfacePos_RoundTrips`**
- Recording with SurfacePos set to a non-trivial SurfacePosition.
- Round-trip through tree.
- Assert SurfacePos fields preserved.

**`Recording_SurfacePos_NullOmitsNode`**
- Recording with SurfacePos=null.
- Save. Assert no SURFACE_POSITION child node.
- Load. Assert SurfacePos is null.

#### ExplicitStartUT / ExplicitEndUT

**`Recording_ExplicitUT_UsedWhenNoPoints`**
- Recording with no trajectory points, ExplicitStartUT=100, ExplicitEndUT=500.
- Assert StartUT==100, EndUT==500.

**`Recording_PointsUT_TakesPrecedenceOverExplicit`**
- Recording with points (ut=200, ut=400), ExplicitStartUT=100, ExplicitEndUT=500.
- Assert StartUT==200 (from points), EndUT==400 (from points).

#### Edge cases

**`RecordingTree_EmptyTree_SaveLoadDoesNotCrash`**
- Tree with no recordings and no branch points.
- Round-trip. Assert no exceptions, empty collections.

**`BranchPoint_EmptyParentChildLists_HandleGracefully`**
- BranchPoint with empty parent and child lists (degenerate but defensive).
- Round-trip. Assert empty lists, not null.

**`RecordingTree_ActiveRecordingIdNull_OmitsKey`**
- Tree with ActiveRecordingId=null.
- Save. Assert `activeRecordingId` key absent.
- Load. Assert ActiveRecordingId is null.

---

## 10. Backward Compatibility

### Approach for Task 1

Task 1 does NOT modify the existing `ParsekScenario.OnSave`/`OnLoad` flow. The new tree fields on Recording have nullable/default values that do not affect existing serialization:

| New Field | Default | Impact on existing recordings |
|---|---|---|
| `TreeId` | `null` | Not written by existing OnSave, not expected by existing OnLoad |
| `VesselPersistentId` | `0` | Same |
| `TerminalStateValue` | `null` | Same |
| `TerminalOrbit*` | `0.0` / `null` | Same |
| `TerminalPosition` | `null` | Same |
| `SurfacePos` | `null` | Same |
| `ParentBranchPointId` | `null` | Same |
| `ChildBranchPointId` | `null` | Same |
| `ExplicitStartUT` | `NaN` | `StartUT` property checks `Points.Count > 0` first |
| `ExplicitEndUT` | `NaN` | `EndUT` property checks `Points.Count > 0` first |

The `StartUT`/`EndUT` property change is safe because all existing recordings have `Points.Count >= 2` (enforced by `StashPending` which rejects recordings with fewer than 2 points). When `Points.Count > 0`, the explicit UT fields are ignored.

### Full backward compatibility integration (Task 11)

Deferred to Task 11. That task will:
- Wrap standalone recordings in single-node trees on load.
- Wrap chain recordings as single-node trees (chain segments stay as-is within each recording node - chain fields are NOT removed).
- Ensure `RecordingTree.Save/Load` is called from `ParsekScenario.OnSave/OnLoad`.

---

## 11. File Organization

### New files (all in `Source/Parsek/`)

| File | Contents |
|---|---|
| `Source/Parsek/TerminalState.cs` | `TerminalState` enum |
| `Source/Parsek/BranchPoint.cs` | `BranchPointType` enum + `BranchPoint` class |
| `Source/Parsek/SurfacePosition.cs` | `SurfaceSituation` enum + `SurfacePosition` struct + static Save/Load helpers |
| `Source/Parsek/RecordingTree.cs` | `RecordingTree` class with Save/Load/RebuildBackgroundMap |

### Modified files

| File | Changes |
|---|---|
| `Source/Parsek/RecordingStore.cs` | Add tree-related fields to `Recording` class; update `StartUT`/`EndUT` properties; update `ApplyPersistenceArtifactsFrom` |

### New test files

| File | Contents |
|---|---|
| `Source/Parsek.Tests/RecordingTreeTests.cs` | All unit tests from section 9 |

### Files NOT modified in Task 1

- `ParsekScenario.cs` - no changes to OnSave/OnLoad (deferred to Task 7/11)
- `ParsekFlight.cs` - no runtime behavior changes
- `FlightRecorder.cs` - no recording logic changes
- `RecordingPaths.cs` - no new path helpers needed yet (tree sidecar file paths deferred to when tree file I/O is implemented)
- `RecordingBuilder.cs` - no test builder changes needed yet (the tests in section 9 construct ConfigNodes directly or use Recording objects)

---

## 12. Implementation Order Within Task 1

1. **Create `TerminalState.cs`** - standalone enum, no dependencies.
2. **Create `SurfacePosition.cs`** - standalone struct with Save/Load helpers. Depends on UnityEngine (Quaternion).
3. **Create `BranchPoint.cs`** - standalone struct + enum.
4. **Modify `RecordingStore.cs`** - add new fields to `Recording`, update `StartUT`/`EndUT`, update `ApplyPersistenceArtifactsFrom`.
5. **Create `RecordingTree.cs`** - the main class with Save/Load/RebuildBackgroundMap. Uses all of the above.
6. **Create `RecordingTreeTests.cs`** - all unit tests.
7. **Run `dotnet test`** - verify all existing tests still pass plus new tests.

---

## 13. Open Questions / Decisions Captured

1. **BranchPoint as class (resolved):** Changed from struct to class because it contains `List<string>` fields. A struct with reference-type fields causes copy-semantics aliasing bugs. As a class, BranchPoint has clear reference semantics and list fields are initialized in declarations.

2. **Tree file format:** The design says "each recording tree is stored as a sidecar file." Task 1 implements Save/Load to ConfigNode but does NOT implement file I/O for the tree sidecar. File I/O (writing the RECORDING_TREE ConfigNode to a `.ptree` file or similar) is deferred to integration with ParsekScenario (Task 7/11). Task 1 proves the ConfigNode round-trip works.

3. **Recording metadata in tree vs per-recording:** The RECORDING nodes inside RECORDING_TREE contain the same metadata fields as the current RECORDING nodes in .sfs. The tree format replaces the .sfs RECORDING nodes - recordings belonging to a tree will NOT also appear as top-level RECORDING nodes in .sfs. This separation is implemented in Task 11.

4. **ExplicitStartUT/ExplicitEndUT naming:** These are named "explicit" to clearly distinguish them from the computed StartUT/EndUT properties. Alternative names considered: `BackgroundStartUT`/`BackgroundEndUT` (too narrow - could be used for other purposes), `StoredStartUT`/`StoredEndUT` (confusing with serialization).
