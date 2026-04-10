# Phase 11: Resource Snapshots — Detailed Plan

## Goal

Make recordings resource-aware so they know what physical resources (LF, Ox, Ore, MonoProp, EC, etc.) a vessel carried at recording start and end. This is the prerequisite for Phase 12 (Looped Transport Logistics) where looped recordings become automated supply routes.

## What we already have

**Vessel snapshots contain full resource state.** Every boundary already calls `VesselSpawner.TryBackupSnapshot()`, which serializes a full ProtoVessel to ConfigNode. Each PART node contains RESOURCE nodes with `amount` and `maxAmount`. The data is already captured — it's just buried inside ConfigNode trees.

**Snapshot capture points already exist** at recording start, stop, chain boundaries (dock/undock/decouple/split/EVA), background recorder finalization, breakup child creation, and optimizer splits.

**Serialization patterns are established.** The `CrewEndStates` Dictionary serialization in `RecordingStore.cs:3037-3099` is the exact pattern to follow: wrapper `AddNode` → child `AddNode("ENTRY")` per key-value pair → batch logging. Both standalone (`ParsekScenario`) and tree (`RecordingTree`) paths follow identical conventions.

---

## T11.1 — ExtractResourceManifest (pure extraction)

### What

A pure static function that walks PART > RESOURCE nodes in a vessel snapshot ConfigNode and returns a summed-by-resource-name dictionary.

### Signature

```csharp
// In VesselSpawner.cs (co-located with TryBackupSnapshot)
internal static Dictionary<string, ResourceAmount> ExtractResourceManifest(ConfigNode vesselSnapshot)
```

### Struct

```csharp
// In Recording.cs or a new ResourceManifest.cs
internal struct ResourceAmount
{
    public double amount;
    public double maxAmount;
}
```

### Algorithm

```
if vesselSnapshot is null → return null
parts = vesselSnapshot.GetNodes("PART")
if parts.Length == 0 → return null
manifest = new Dictionary<string, ResourceAmount>()
for each PART node:
    resources = partNode.GetNodes("RESOURCE")
    for each RESOURCE node:
        name = resNode.GetValue("name")
        if name is null/empty → skip
        if name == "ElectricCharge" → skip
        parse amount (double.TryParse, InvariantCulture, default 0)
        parse maxAmount (double.TryParse, InvariantCulture, default 0)
        if manifest.ContainsKey(name):
            manifest[name].amount += amount
            manifest[name].maxAmount += maxAmount
        else:
            manifest[name] = new ResourceAmount { amount, maxAmount }
return manifest.Count > 0 ? manifest : null
```

### Design notes

- **Return null, not empty dict**, for "no resources" — matches the established null-means-no-data pattern for additive fields (same as `CrewEndStates`, `RecordingGroups`, etc.)
- **Exclude ElectricCharge** — EC fluctuates constantly (solar panels, reaction wheels, SAS) and start/end values are meaningless noise. Skip `name == "ElectricCharge"` at extraction time. No one ships EC via logistics routes.
- **No other resource name filtering** — capture everything else including modded resources. The extraction function is name-agnostic beyond the EC exclusion.
- **Sum across parts** — a vessel with 3 fuel tanks of 400 LF each produces `LiquidFuel: { amount: 1200, maxAmount: 1200 }`.
- **Include zero-amount resources** — `maxAmount` matters for capacity checks in Phase 12 logistics. An empty ore tank (amount=0, maxAmount=1500) is meaningful: "this vessel can carry ore."
- **Ignore `flowState`** — that's a delivery-time concern for Phase 12, not a capture-time concern.

### Tests (xUnit, pure — no Unity)

| Test | Input | Expected |
|------|-------|----------|
| `NullInput_ReturnsNull` | null | null |
| `EmptyVesselNode_ReturnsNull` | ConfigNode with no PART nodes | null |
| `SinglePart_SingleResource` | 1 PART, 1 RESOURCE (LF 400/400) | { LF: 400/400 } |
| `SinglePart_MultipleResources` | 1 PART, 2 RESOURCE (LF 400, Ox 488) | { LF: 400/400, Ox: 488/488 } |
| `MultipleParts_SameResource_Summed` | 3 PARTs each with LF 400/400 | { LF: 1200/1200 } |
| `MultipleParts_MixedResources` | realistic vessel (LF+Ox+EC+MP) | all four summed correctly |
| `ZeroAmountResource_Included` | Ore 0/1500 | { Ore: 0/1500 } (maxAmount matters) |
| `PartWithNoResources_Skipped` | structural part (no RESOURCE node) | only resources from other parts |
| `MissingAmountField_DefaultsZero` | RESOURCE node with name but no amount | amount=0 |
| `MalformedAmount_DefaultsZero` | amount = "abc" | amount=0 |
| `ElectricCharge_Excluded` | RESOURCE with name=ElectricCharge | not in result |
| `RoundTrip_Precision` | amount = 3600.123456789 | round-trip exact via "R" format |

Build test ConfigNodes manually (no VesselSnapshotBuilder needed — just `new ConfigNode("VESSEL")` + `AddNode("PART")` + `AddNode("RESOURCE")` + `AddValue`).

---

## T11.2 — Recording fields + serialization

### New fields on Recording

```csharp
// Recording.cs — alongside existing Phase 10 location fields (line ~98)
public Dictionary<string, ResourceAmount> StartResources;  // null = no data (legacy)
public Dictionary<string, ResourceAmount> EndResources;     // null = no data (legacy)
```

### Serialization format

```
RESOURCE_MANIFEST
{
    RESOURCE { name = LiquidFuel  startAmount = 3600  startMax = 3600  endAmount = 200  endMax = 3600 }
    RESOURCE { name = Oxidizer    startAmount = 4400  startMax = 4400  endAmount = 244  endMax = 4400 }
    RESOURCE { name = Ore         startAmount = 0     startMax = 1500  endAmount = 1500  endMax = 1500 }
}
```

### Serialization helpers

```csharp
// In RecordingStore.cs (alongside SerializeCrewEndStates/DeserializeCrewEndStates)
internal static void SerializeResourceManifest(ConfigNode parent, Recording rec)
internal static void DeserializeResourceManifest(ConfigNode parent, Recording rec)
```

**Serialize pattern** (follows `SerializeCrewEndStates` at `RecordingStore.cs:3037`):

```
if both StartResources and EndResources are null → return (no node written)
create RESOURCE_MANIFEST node via parent.AddNode()

build merged key set from StartResources ∪ EndResources keys
for each resource name in the merged set:
    create RESOURCE child node
    write name
    if StartResources has this key: write startAmount, startMax (ToString "R", InvariantCulture)
    if EndResources has this key: write endAmount, endMax (ToString "R", InvariantCulture)
    increment counter

log batch summary: "SerializeResourceManifest: wrote {count} resource(s) for recording={RecordingId}"
```

**Deserialize pattern** (follows `DeserializeCrewEndStates` at `RecordingStore.cs:3060`):

```
get RESOURCE_MANIFEST node from parent → if null, return (legacy recording)
get RESOURCE child nodes → if empty, return

for each RESOURCE node:
    parse name → skip if null/empty
    parse startAmount, startMax → if present, add to StartResources dict
    parse endAmount, endMax → if present, add to EndResources dict
    track loaded/skipped counters

log batch summary: "DeserializeResourceManifest: loaded={loaded} skipped={skipped}"
```

### Save/Load call sites (4 total)

The same `SerializeResourceManifest`/`DeserializeResourceManifest` helpers are called from both paths:

| Path | Save method | Load method | File |
|------|------------|-------------|------|
| Standalone | `SaveRecordingMetadata` (~line 3012) | `LoadRecordingMetadata` (~line 3179) | ParsekScenario.cs |
| Tree | `SaveRecordingResourceAndState` (~line 369) | `LoadRecordingResourceAndState` (~line 709) | RecordingTree.cs |

Both call the same static helper — no duplicated serialization logic.

### Design notes

- **Single RESOURCE_MANIFEST node** with both start and end in each RESOURCE child — not two separate nodes. This keeps related data together and avoids doubling the node count. If only StartResources is set (no EndResources yet, recording in progress), the endAmount/endMax fields are simply absent.
- **Merged key set** — a resource that appears only at start (consumed completely) or only at end (gained from docking) must still be serialized. The merged set covers both.
- **No format version bump** — additive. Missing RESOURCE_MANIFEST node = no data. Old recordings work unchanged.

### Tests

| Test | What |
|------|------|
| `RoundTrip_BothStartAndEnd` | serialize with both dicts → deserialize → all values match |
| `RoundTrip_StartOnly` | only StartResources set → EndResources null after load |
| `RoundTrip_EndOnly` | only EndResources set → StartResources null after load |
| `RoundTrip_NullBoth_NoNodeWritten` | both null → no RESOURCE_MANIFEST in output |
| `RoundTrip_EmptyDicts_NoNodeWritten` | both empty dicts → no RESOURCE_MANIFEST (treated as null) |
| `RoundTrip_AsymmetricKeys` | Start has LF+Ox, End has LF+Ore → both dicts correct |
| `RoundTrip_Precision` | amount = 3600.123456789012 → round-trip exact |
| `LocaleSafety` | amount with comma separator → parsed correctly via InvariantCulture |
| `LegacyRecording_NoNode_NullFields` | ConfigNode without RESOURCE_MANIFEST → both fields null |
| `MalformedResource_Skipped` | RESOURCE with empty name → skipped, counter incremented |

---

## T11.3 — Capture at recording boundaries

### Overview

Every site that takes a vessel snapshot is a candidate for resource extraction. But not every site needs it — we only need StartResources at recording start and EndResources at recording end. Mid-recording snapshots (periodic refresh, continuation refresh) don't need extraction.

### Capture strategy: extract from existing snapshots

We do NOT add new snapshot calls. We extract from snapshots that are already being taken. The extraction is a lightweight ConfigNode walk — no KSP API calls, no vessel access needed.

### Site-by-site plan

#### A. Recording start → StartResources

**Site:** `FlightRecorder.StartRecording()` (line 4381)

After `RefreshBackupSnapshot(v, "record_start", force: true)` captures `lastGoodVesselSnapshot`, extract:

```csharp
pendingStartResources = VesselSpawner.ExtractResourceManifest(lastGoodVesselSnapshot);
```

Store in a new private field `pendingStartResources`. Transfer to the Recording in `BuildCaptureRecording()` (line ~4570):

```csharp
capture.StartResources = pendingStartResources;
```

**Why here and not later:** `StartRecording` is the canonical "beginning of a recording" site. The snapshot is freshest here. `BuildCaptureRecording` is called at stop time — by then the vessel's resources have changed.

#### B. Recording stop → EndResources

**Site:** `FlightRecorder.BuildCaptureRecording()` (line 4563-4570)

After `VesselSpawner.SnapshotVessel(capture, ...)` sets `capture.VesselSnapshot`, extract:

```csharp
capture.EndResources = VesselSpawner.ExtractResourceManifest(capture.VesselSnapshot);
```

This covers all three callers of `BuildCaptureRecording`: `StopRecording`, `StopRecordingForChainBoundary`, and the vessel-switch detection path.

#### C. Chain boundary segments

Chain boundaries (dock/undock/EVA/split/atmosphere) create new Recording objects. Each new segment needs StartResources from its initial state.

**`ChainSegmentManager.CommitChainSegment`** (line 571-614) — EVA chain. The new continuation recording starts with the parent vessel's current state. After `ApplyPersistenceArtifactsFrom(captured)` copies metadata (including StartResources from the parent segment), we need to set StartResources to the vessel's state *at boundary time*, not the parent's start:

```csharp
// After ApplyPersistenceArtifactsFrom, override StartResources with current vessel state
contRec.StartResources = VesselSpawner.ExtractResourceManifest(contRec.GhostVisualSnapshot);
```

Wait — this is wrong. The continuation recording's snapshot is the *current* vessel state at boundary time. But `ApplyPersistenceArtifactsFrom` copies the *parent's* StartResources, which is from the parent's start, not the boundary. We need to think about what StartResources means for a continuation.

**Decision: StartResources = resources at the start of THIS segment.** For a continuation recording, that's the vessel state at boundary time. The outgoing segment's EndResources = the same boundary state. So:

- **Outgoing segment:** EndResources = extracted from CaptureAtStop.VesselSnapshot (already covered by site B)
- **Incoming continuation:** StartResources = extracted from the vessel's snapshot at boundary time

The cleanest place to set the continuation's StartResources is after `ApplyPersistenceArtifactsFrom` in `CommitSegmentCore` (the common path for all chain commits). `ApplyPersistenceArtifactsFrom` copies the parent's StartResources, then we override with the boundary-time state:

```csharp
// CommitSegmentCore, after ApplyPersistenceArtifactsFrom:
pending.StartResources = VesselSpawner.ExtractResourceManifest(
    pending.GhostVisualSnapshot ?? pending.VesselSnapshot);
```

**But wait** — `CommitDockUndockSegment` (line 647) nulls `VesselSnapshot` for mid-chain ghost-only segments. And `CommitBoundarySplit` (line 674) does the same. The GhostVisualSnapshot may still be available. Need to use whichever snapshot is non-null at the commit site.

Actually, let me reconsider. The *outgoing* segment's EndResources comes from `CaptureAtStop` (site B). The *incoming* segment's StartResources should come from the same moment — the boundary. The simplest approach:

**At `BuildCaptureRecording` (site B), also stash the EndResources manifest as `pendingBoundaryResources`.** Then at the continuation commit site, set:

```csharp
contRec.StartResources = pendingBoundaryResources;  // from the same boundary moment
```

This ensures the outgoing segment's EndResources and the incoming segment's StartResources are extracted from the exact same snapshot.

#### D. Breakup child recordings

**Site:** `ParsekFlight.CreateBreakupChildRecording()` (line 2890-2937)

After line 2913-2915 captures `VesselSnapshot` for the child:

```csharp
childRec.StartResources = VesselSpawner.ExtractResourceManifest(childRec.VesselSnapshot);
```

Debris typically doesn't stop recording cleanly (destroyed by physics), so EndResources for debris will be set when the background recorder finalizes.

#### E. Background recorder finalization

**Site:** `BackgroundRecorder.HandleBackgroundVesselSplit()` (line 633-635)

After child snapshots are captured:

```csharp
child.StartResources = VesselSpawner.ExtractResourceManifest(child.VesselSnapshot);
```

For the parent's EndResources: the parent recording's EndResources should be set when the parent recording is finalized (destroyed or shutdown). The background recorder doesn't take a fresh snapshot at finalization — it uses whatever was last captured. We can extract EndResources from the tree recording's existing VesselSnapshot at finalization time:

**Sites:** `OnBackgroundVesselWillDestroy`, `Shutdown`, `EndDebrisRecording` — after `FlushTrackSectionsToRecording`, if `treeRec.VesselSnapshot != null`:

```csharp
treeRec.EndResources = VesselSpawner.ExtractResourceManifest(treeRec.VesselSnapshot);
```

If VesselSnapshot is null (destroyed before snapshot), EndResources stays null — correct behavior (destroyed vessel has no meaningful end resources).

#### F. Tree promotion from breakup

**Site:** `ParsekFlight.PromoteToTreeForBreakup()` (line 3166-3167)

After copying snapshots from CaptureAtStop to rootRec:

```csharp
rootRec.StartResources = cap.StartResources;   // from original standalone recording
rootRec.EndResources = VesselSpawner.ExtractResourceManifest(rootRec.VesselSnapshot);
```

#### G. Optimizer propagation

**`RecordingOptimizer.SplitAtSection`** (line 296): The optimizer splits a recording at a TrackSection boundary. Resource manifest policy:

- **First half:** keeps StartResources. EndResources = null (no snapshot at split point — the split is an arbitrary environment boundary, not a vessel state change).
- **Second half:** StartResources = null (same reason). Gets EndResources from the original (moved along with VesselSnapshot).

This is acceptable because optimizer splits are environment boundaries (atmosphere/exo transition), not dock/undock events. Resources don't meaningfully change at these boundaries. Phase 12 logistics uses the chain-level start/end, not optimizer-split segments.

**`RecordingOptimizer.MergeInto`** (line 217): The absorbed recording's EndResources replaces target's (later segment wins). Target keeps its StartResources.

```csharp
// In MergeInto, after the VesselSnapshot transfer:
if (absorbed.EndResources != null)
    target.EndResources = absorbed.EndResources;
```

#### H. ApplyPersistenceArtifactsFrom

**Site:** `Recording.ApplyPersistenceArtifactsFrom()` (line 252)

Add to the field copy block:

```csharp
StartResources = source.StartResources;  // shallow copy OK — dict is immutable after extraction
EndResources = source.EndResources;
```

No deep copy needed — resource manifests are never mutated after extraction.

### Summary of capture sites

| Site | Sets | Extracts from |
|------|------|---------------|
| `FlightRecorder.StartRecording` | `pendingStartResources` | `lastGoodVesselSnapshot` |
| `FlightRecorder.BuildCaptureRecording` | `capture.EndResources` | `capture.VesselSnapshot` |
| `BuildCaptureRecording` also | `capture.StartResources` | `pendingStartResources` (stashed from start) |
| `CommitSegmentCore` continuation | `contRec.StartResources` | boundary snapshot (= outgoing EndResources) |
| `CreateBreakupChildRecording` | `childRec.StartResources` | `childRec.VesselSnapshot` |
| `BackgroundRecorder.HandleBackgroundVesselSplit` | `child.StartResources` | `child.VesselSnapshot` |
| BgRecorder finalization (3 sites) | `treeRec.EndResources` | `treeRec.VesselSnapshot` |
| `PromoteToTreeForBreakup` | `rootRec.EndResources` | `rootRec.VesselSnapshot` |
| `RecordingOptimizer.SplitAtSection` | second.EndResources (moved) | original.EndResources |
| `RecordingOptimizer.MergeInto` | target.EndResources | absorbed.EndResources |
| `ApplyPersistenceArtifactsFrom` | both fields | source recording |

---

## T11.4 — UI display

### Where

**Recordings Manager tooltip** — extend the existing hover tooltip (`DrawRecordingTooltip` at `RecordingsTableUI.cs:2352`). Add a "Resources" section after the storage section.

### Format

```
Resources:
  LF: 3600 → 200 (-3400)
  Ox: 4400 → 244 (-4156)
  Ore: 0 → 1500 (+1500)
  EC: 200 → 180 (-20)
```

If only StartResources (recording in progress or no end snapshot):
```
Resources at start:
  LF: 3600 / 3600
  Ox: 4400 / 4400
```

If neither: no resources section shown (legacy recording).

### Formatting helper

```csharp
// In RecordingsTableUI.cs
internal static string FormatResourceManifest(
    Dictionary<string, ResourceAmount> start,
    Dictionary<string, ResourceAmount> end)
```

- Merge keys from both dicts
- Sort alphabetically for stable display
- Format each: `{name}: {startAmt} → {endAmt} ({delta:+0;-0})`
- Abbreviate resource names? Stock names are short enough (LiquidFuel, Oxidizer, MonoPropellant, ElectricCharge, Ore, XenonGas, SolidFuel, IntakeAir, Ablator). Leave as-is for v1.
- Round amounts to 1 decimal place for display (full precision stored).
- Use InvariantCulture for number formatting.

### Expanded stats option

Don't add a new column — resource manifests are variable-length data (3-8 resource types). A fixed-width column can't display this. The tooltip is the right place. If the user wants more detail, a future expandable detail row could show the full manifest.

### Tests

Formatting helper is pure — test with various resource combinations, null dicts, single resource, many resources, gains vs losses vs unchanged.

---

## T11.5 — ComputeResourceDelta (pure computation)

### What

Compute per-resource change between start and end manifests. Used by UI (T11.4) and by Phase 12 for route cost/cargo derivation.

### Signature

```csharp
// In VesselSpawner.cs or a new ResourceManifest.cs
internal static Dictionary<string, double> ComputeResourceDelta(
    Dictionary<string, ResourceAmount> start,
    Dictionary<string, ResourceAmount> end)
```

### Algorithm

```
if both null → return null
merged keys from start ∪ end
for each key:
    startAmt = start?[key].amount ?? 0
    endAmt = end?[key].amount ?? 0
    delta[key] = endAmt - startAmt   // positive = gained, negative = consumed
return delta
```

### Phase 12 consumers

```
DeliveryManifest = only positive entries in delta (resources gained by destination)
CostManifest = StartResources (full transport vessel start state — fuel + cargo)
RouteCost = only negative entries in delta (resources consumed during transit)
```

Phase 12 will call `ComputeResourceDelta` and filter by sign. That's Phase 12's job, not Phase 11's.

### Tests

| Test | Start | End | Expected |
|------|-------|-----|----------|
| `NormalConsumption` | LF:3600 | LF:200 | LF:-3400 |
| `ResourceGained` | Ore:0 | Ore:1500 | Ore:+1500 |
| `MixedGainsAndLosses` | LF:3600,Ore:0 | LF:200,Ore:1500 | LF:-3400,Ore:+1500 |
| `ResourceOnlyInStart` | LF:3600 | (empty) | LF:-3600 |
| `ResourceOnlyInEnd` | (empty) | Ore:1500 | Ore:+1500 |
| `Unchanged` | LF:400 | LF:400 | LF:0 |
| `BothNull` | null | null | null |
| `StartNull` | null | LF:200 | LF:+200 |
| `EndNull` | LF:3600 | null | LF:-3600 |

---

## Test generator updates

### RecordingBuilder

Add fluent methods to the existing `RecordingBuilder` in `Tests/Generators/`:

```csharp
public RecordingBuilder WithStartResources(Dictionary<string, ResourceAmount> resources)
public RecordingBuilder WithEndResources(Dictionary<string, ResourceAmount> resources)
```

### VesselSnapshotBuilder

Add a method to add RESOURCE nodes to PART nodes:

```csharp
public VesselSnapshotBuilder AddResourceToPart(int partIndex, string name, double amount, double maxAmount)
```

This enables integration tests that build a snapshot → extract manifest → verify values.

### ScenarioWriter

Add V3 support for resource manifests so `InjectAllRecordings` produces recordings with resource data. At least one synthetic recording (e.g., "Flea Flight") should have resources for end-to-end verification.

---

## Implementation order

1. **T11.1** — `ResourceAmount` struct + `ExtractResourceManifest` + tests. Pure code, zero dependencies.
2. **T11.5** — `ComputeResourceDelta` + tests. Pure code, depends only on T11.1 struct.
3. **T11.2** — Recording fields + serialization helpers + round-trip tests. Depends on T11.1.
4. **T11.3** — Capture calls at all boundary sites. Depends on T11.1 + T11.2. This is the biggest task — many call sites, each needs careful placement.
5. **T11.4** — UI display. Depends on T11.1 + T11.2 + T11.5. Can only be tested with KSP running.

T11.1 and T11.5 are independent and can be done in the same commit. T11.2 builds on them. T11.3 is the integration work. T11.4 is polish.

---

## Risks and edge cases

**Electric charge excluded.** EC fluctuates constantly (solar panels, reaction wheels, SAS) and start/end snapshots are arbitrary noise. Excluded at extraction time (`name == "ElectricCharge"` skip). No one ships EC via logistics routes. If ever needed, removing the one-line check is trivial.

**Docking fuel rebalance.** When two vessels dock, KSP rebalances fuel across connected tanks. The post-dock snapshot captures the rebalanced state. For logistics, this is correct — the delivery manifest should reflect what was actually transferred, not what the transport held before docking.

**Destroyed vessels.** If a vessel is destroyed before a snapshot, VesselSnapshot is null and EndResources stays null. Correct — a destroyed vessel has no meaningful end resources.

**Background vessels without snapshots.** Background-recorded debris may never get a fresh snapshot (destroyed while unloaded). EndResources stays null. Acceptable — debris resources are not meaningful for logistics.

**Optimizer splits.** Split halves don't get boundary resource snapshots because the split is at an environment boundary (atmosphere transition), not a vessel state change. Resources are unchanged across these boundaries. The full chain's start/end resources (from recording start and final stop) are what logistics needs.

**Mining vessels.** A mining vessel's EndResources will show more ore than StartResources. `ComputeResourceDelta` correctly returns positive values. Phase 12 can handle this: a "mining route" delivers ore gained during the mission. The design doc already anticipates this (§5.2: "the delivery amount is the positive delta").
