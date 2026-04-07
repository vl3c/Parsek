# Resource Snapshots: What We Have and What We Need

Preparation analysis for Phase 11 (Resource Snapshots) and its role as prerequisite for Phase 12 (Looped Transport Logistics).

## The Goal

Recordings should know what physical resources (LiquidFuel, Oxidizer, Ore, MonoPropellant, ElectricCharge, etc.) a vessel carried at key moments: recording start, segment boundaries (dock/undock/decouple/EVA), and recording end. This enables:

1. **Visibility** -- "this Minmus run carried 4000 units of ore on arrival"
2. **Delta computation** -- "this ascent consumed 3600 LF and 4400 Ox"
3. **Logistics routes** (Phase 12) -- resource manifest defines what a looped route delivers

## What We Already Have

### Vessel snapshots contain full resource state

Every separation boundary already calls `VesselSpawner.TryBackupSnapshot()`, which serializes a full `ProtoVessel` to ConfigNode. Each PART node inside contains RESOURCE nodes:

```
PART
{
    name = fuelTankSmallFlat
    RESOURCE
    {
        name = LiquidFuel
        amount = 50
        maxAmount = 50
    }
    RESOURCE
    {
        name = Oxidizer
        amount = 61.111112
        maxAmount = 61.111112
    }
}
```

Snapshots are taken at:
- **Recording start** -- `VesselSnapshot` / `GhostVisualSnapshot` on the Recording
- **Undock** -- `GhostVisualSnapshot` on continuation recording (ChainSegmentManager.cs:358)
- **Decouple / breakup** -- snapshot of each child vessel (ParsekFlight.cs:2436)
- **Dock (merge)** -- snapshot of merged vessel (ParsekFlight.cs:1903)
- **Split (EVA, staging)** -- snapshots of both active and background vessels (ParsekFlight.cs:1750-1751)

**The resource data is already captured and persisted.** It's buried inside ConfigNode trees, but it's there.

### Career-level resource tracking is fully implemented

TrajectoryPoint carries `funds`, `science`, `reputation` (absolute values per point). ResourceBudget computes deltas. The game actions system (ledger, recalculation engine, 8 resource modules) handles the full career-mode economic layer. This is a proven pattern for the "deduct at origin, deliver at destination" logistics model.

### Loop system has the right event hooks

- `LoopRestartedEvent` fires each loop cycle with cycle index
- `PlaybackCompletedEvent` fires when ghost reaches trajectory end
- `HandleLoopRestarted()` in ParsekPlaybackPolicy is currently a logging stub -- ready for delivery logic
- Per-recording loop period, time unit, start/end UT range already configurable

### Location context exists (Phase 10)

Recordings know their start/end body, biome, situation, coordinates, and launch site. This defines the "origin" and "destination" of a logistics route.

## What's Missing

### 1. Resource manifest extraction

A lightweight summary of what resources a vessel carries, extracted from the full ProtoVessel snapshot. Instead of walking 200 PART nodes every time we need to know fuel levels, extract once at boundary time:

```csharp
// Proposed: Dictionary<string, ResourceAmount>
// "LiquidFuel" -> { amount: 3600, maxAmount: 3600 }
// "Oxidizer"   -> { amount: 4400, maxAmount: 4400 }
// "Ore"        -> { amount: 0, maxAmount: 1500 }
```

This is a pure extraction function: walk PART nodes in a ConfigNode, sum RESOURCE amounts by name.

### 2. Resource manifest on Recording

New fields on Recording to store the extracted manifest:

- `StartResourceManifest` -- resources at recording start
- `EndResourceManifest` -- resources at recording end (from final snapshot)
- Or: a single `ResourceManifest` dictionary stored at each boundary

Format options:
- **Inline in .sfs metadata** -- simple, small (typically 3-8 resource types)
- **In sidecar file** -- overkill for a few key-value pairs

### 3. Resource delta computation (physical)

Analogous to how `ResourceBudget.ComputeStandaloneDelta` works for funds/science/rep, but for physical resources:

```
Route cost  = StartManifest - EndManifest  (what was consumed)
Cargo       = EndManifest                   (what arrives at destination)
```

For a Mun resupply run:
- Start: 3600 LF, 4400 Ox, 1500 Ore (empty)
- End: 200 LF, 244 Ox, 1500 Ore (mined on Mun)
- Cost: 3400 LF, 4156 Ox consumed in flight
- Delivery: 1500 Ore delivered to origin on return

### 4. UI display

Recordings Manager tooltip or detail panel showing resource signature. Compact format:

```
LF: 3600 -> 200 (-3400)
Ox: 4400 -> 244 (-4156)
Ore: 0 -> 1500 (+1500)
```

### 5. Delivery trigger on loop completion (Phase 12)

In `HandleLoopRestarted()`, on each cycle:
1. Check if origin has enough fuel to dispatch (cost check)
2. Deduct route cost from origin vessel's resources
3. After transit time, add cargo to destination vessel's resources
4. Log as game action

## Task Breakdown

### Phase 11 Tasks (Resource Snapshots)

**T11.1. ExtractResourceManifest -- pure extraction function**

```csharp
internal static Dictionary<string, ResourceAmount> ExtractResourceManifest(ConfigNode vesselSnapshot)
```

Walk all PART > RESOURCE nodes, sum `amount` and `maxAmount` by resource name. Pure, static, testable. No Unity dependency.

Test cases: empty vessel, single-tank, multi-tank same resource, multiple resource types, null/missing RESOURCE nodes.

**T11.2. Capture manifests at recording boundaries**

At recording start and at each boundary where a snapshot is taken, call `ExtractResourceManifest` and store the result. Two new fields on Recording:

```csharp
public Dictionary<string, ResourceAmount> StartResources;
public Dictionary<string, ResourceAmount> EndResources;
```

Capture points:
- `FlightRecorder.StartRecording()` -- capture StartResources from initial snapshot
- `FlightRecorder.StopRecording()` -- capture EndResources from final snapshot
- Chain boundaries (dock/undock/decouple/split) -- update EndResources on outgoing segment, StartResources on incoming segment

**T11.3. Serialize/deserialize resource manifests**

Add RESOURCE_MANIFEST ConfigNode to recording metadata in .sfs:

```
RESOURCE_MANIFEST
{
    RESOURCE { name = LiquidFuel  startAmount = 3600  startMax = 3600  endAmount = 200  endMax = 3600 }
    RESOURCE { name = Oxidizer    startAmount = 4400  startMax = 4400  endAmount = 244  endMax = 4400 }
}
```

Lightweight -- typically 3-8 entries. Lives in .sfs metadata alongside other recording fields. No format version bump needed (additive, missing = no data).

**T11.4. Display resource summary in UI**

Recordings Manager: resource signature in tooltip or collapsible detail row. Shows start/end amounts and delta for each resource type. Only displayed if manifest exists (backward compat with old recordings).

**T11.5. Resource delta computation**

```csharp
internal static Dictionary<string, double> ComputeResourceDelta(
    Dictionary<string, ResourceAmount> start,
    Dictionary<string, ResourceAmount> end)
```

Returns per-resource change (positive = gained, negative = consumed). Pure, testable.

### Phase 12 Prerequisites (from Phase 11)

Phase 12 (Logistics Routes) needs Phase 11's manifests plus:

- **Route cost** = resources consumed during flight (start - end delta, negative values)
- **Cargo manifest** = resources aboard at arrival (end manifest)
- **Dispatch check** = does origin vessel have enough of each resource to cover route cost?
- **Delivery action** = add cargo manifest resources to destination vessel

The abstracted logistics model (recommended MVP from logistics-network-design.md) needs no physical vessel during transit -- just deduct at origin, wait, add at destination. The ghost replays visually via existing loop system.

## Relationship to Existing Design Documents

- **`docs/dev/research/logistics-network-design.md`** -- full logistics design. Phase 11 implements the "Physical resource snapshots per trajectory point" prerequisite (their Phase 1). Our approach is simpler: snapshot-boundary manifests instead of per-trajectory-point resource tracking. Per-point tracking is overkill -- we only need start/end state to compute cost and cargo.

- **`docs/dev/research/loop-playback-and-logistics.md`** -- confirms atmospheric loops work at any interval (geographic coordinates are body-relative). Orbital drift is negligible for visual purposes. No blocking issues for logistics loop execution.

- **`docs/roadmap.md` Phase 11** -- matches our task breakdown. Resource snapshots at boundaries, automatic capture via KSP API, resource signatures in UI, event hook for mods.

- **`docs/roadmap.md` Phase 12** -- route = recording + origin + destination + manifest. Delivery on loop completion. Our `HandleLoopRestarted()` hook is the natural integration point.

## Design Decisions

1. **Boundary manifests, not per-point tracking.** The logistics-network-design.md suggests extending TrajectoryPoint with per-resource fields. That's heavyweight -- every physics frame would sample every resource on every part. Boundary manifests (extract from existing snapshots at start/end) give us everything we need for cost/cargo computation without any recording overhead.

2. **Extract from existing snapshots.** We don't need a new capture mechanism. `TryBackupSnapshot()` already runs at every boundary. We just need to read the RESOURCE nodes out of the ConfigNode it produces.

3. **Inline in .sfs metadata.** Resource manifests are small (3-8 entries). No sidecar file needed. Additive field -- old recordings without manifests simply show no resource info.

4. **Additive format, no version bump.** Missing RESOURCE_MANIFEST node = no data. Backward compatible. Old recordings work unchanged.

## Modifying Resources on Unloaded Vessels (Investigated)

**Confirmed feasible.** `ProtoPartResourceSnapshot.amount` is a public writable field. The save path (`Save()`) writes the current field value, overwriting any cached ConfigNode. The load path (`Load()`) reads the field into the Part when the vessel comes off rails. No refresh calls needed.

Multiple mods use this pattern (BackgroundProcessing, StageRecovery, and others). Common approach: iterate tanks for the target resource, distribute the delta across them with overflow/underflow spillage, clamp to [0, maxAmount]. See `docs/mods-references/` for detailed analysis.

### Delivery pattern for Parsek

```csharp
// Find destination vessel (unloaded)
Vessel destVessel = FlightGlobals.Vessels.FirstOrDefault(v => v.persistentId == destPid);
ProtoVessel dest = destVessel.protoVessel;

// Add resources, distributing across tanks
foreach (var entry in deliveryManifest)
{
    double remaining = entry.Value;
    foreach (var part in dest.protoPartSnapshots)
        foreach (var res in part.resources)
            if (res.resourceName == entry.Key && res.flowState && remaining > 0)
            {
                double space = res.maxAmount - res.amount;
                double add = Math.Min(remaining, space);
                res.amount += add;
                remaining -= add;
            }
    // remaining > 0 means destination tanks are full -- delivery partially failed
}
```

### Edge cases to handle
- **Destination tanks full** -- partial delivery; remainder lost or queued
- **`flowState == false`** -- player disabled flow on a tank; skip it
- **Destination vessel loaded** -- use `part.RequestResource()` instead of ProtoPartResourceSnapshot
- **Destination vessel destroyed/recovered** -- route should auto-disable
- **Zero-capacity resources** -- guard against division by zero

### Cross-vessel transfer is novel
No existing KSP mod does cross-vessel physical resource transfer on unloaded vessels. Background processing mods only modify resources within a single vessel. Parsek logistics delivery would be new -- but the per-vessel modification pattern is proven. The key addition: source and destination must be updated atomically in the same tick.

## Open Questions

- **Modded resources:** KSP's resource system is extensible. Should we filter to known stock resources, or capture everything? Recommendation: capture everything -- the extraction function doesn't need to know resource names.

- **Electric charge:** EC fluctuates constantly (solar panels, reaction wheels). The start/end snapshot values may be misleading. Should we exclude EC from cost/cargo computation? Or let the player decide?

- **Resource flow during docking:** When two vessels dock, KSP may rebalance fuel across connected tanks. The post-dock snapshot captures the rebalanced state, not the pre-dock state. For logistics, this is actually correct -- we want to know what the combined vessel has after docking.

- **Mining vessels:** A mining vessel's end manifest has more ore than its start manifest. The "cost" is negative (gained resources). The logistics system should handle this naturally -- a mining route's "cargo" is the ore gained.
