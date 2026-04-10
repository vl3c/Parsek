# Phase 11: Resource Snapshots — Detailed Plan

## Goal

Make recordings resource-aware so they know what physical resources (LF, Ox, Ore, MonoProp, etc.) a vessel carried at recording start and end. This is the prerequisite for Phase 12 (Looped Transport Logistics) where looped recordings become automated supply routes.

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
// In a new ResourceManifest.cs (same pattern as TrajectoryPoint.cs, PartEvent.cs)
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
        if name == "ElectricCharge" or name == "IntakeAir" → skip
        // flowState field exists in RESOURCE nodes but is intentionally ignored here.
        // flowState is a delivery-time concern for Phase 12, not capture-time.
        parse amount (double.TryParse, InvariantCulture, default 0)
        parse maxAmount (double.TryParse, InvariantCulture, default 0)
        if manifest.ContainsKey(name):
            // IMPORTANT: ResourceAmount is a struct — indexer returns a copy.
            // Must read-modify-write, not mutate through the indexer.
            var ra = manifest[name]
            ra.amount += amount
            ra.maxAmount += maxAmount
            manifest[name] = ra
        else:
            manifest[name] = new ResourceAmount { amount, maxAmount }
return manifest.Count > 0 ? manifest : null
```

### Design notes

- **Return null, not empty dict**, for "no resources" — matches the established null-means-no-data pattern for additive fields (same as `CrewEndStates`, `RecordingGroups`, etc.)
- **Exclude ElectricCharge** — EC fluctuates constantly (solar panels, reaction wheels, SAS) and start/end values are meaningless noise. No one ships EC via logistics routes.
- **Exclude IntakeAir** — IntakeAir has dynamic maxAmount based on air intake area and speed. Amounts are environmental noise, not meaningful cargo. No one transports IntakeAir.
- **No other resource name filtering** — capture everything else including modded resources. Ablator stays (meaningful consumable — heat shield ablation). SolidFuel stays (real booster resource, summed across all parts including staged boosters).
- **Sum across parts** — a vessel with 3 fuel tanks of 400 LF each produces `LiquidFuel: { amount: 1200, maxAmount: 1200 }`.
- **Include zero-amount resources** — `maxAmount` matters for capacity checks in Phase 12 logistics. An empty ore tank (amount=0, maxAmount=1500) is meaningful: "this vessel can carry ore."
- **Ignore `flowState`** — RESOURCE nodes include a `flowState` boolean (player-disabled tank flow). Irrelevant at capture time — we want total capacity. Phase 12 checks `flowState` at delivery time.
- **Struct mutation trap** — `ResourceAmount` is a value type. `manifest[name].amount += x` silently modifies a copy, not the dict entry. Must read into local, mutate, write back. This is called out in the algorithm above.

### Tests (xUnit, pure — no Unity)

| Test | Input | Expected |
|------|-------|----------|
| `NullInput_ReturnsNull` | null | null |
| `EmptyVesselNode_ReturnsNull` | ConfigNode with no PART nodes | null |
| `SinglePart_SingleResource` | 1 PART, 1 RESOURCE (LF 400/400) | { LF: 400/400 } |
| `SinglePart_MultipleResources` | 1 PART, 2 RESOURCE (LF 400, Ox 488) | { LF: 400/400, Ox: 488/488 } |
| `MultipleParts_SameResource_Summed` | 3 PARTs each with LF 400/400 | { LF: 1200/1200 } |
| `MultipleParts_MixedResources` | realistic vessel (LF+Ox+MP) | all three summed correctly |
| `ZeroAmountResource_Included` | Ore 0/1500 | { Ore: 0/1500 } (maxAmount matters) |
| `PartWithNoResources_Skipped` | structural part (no RESOURCE node) | only resources from other parts |
| `MissingAmountField_DefaultsZero` | RESOURCE node with name but no amount | amount=0 |
| `MalformedAmount_DefaultsZero` | amount = "abc" | amount=0 |
| `ElectricCharge_Excluded` | RESOURCE with name=ElectricCharge | not in result |
| `IntakeAir_Excluded` | RESOURCE with name=IntakeAir | not in result |
| `Ablator_Included` | RESOURCE with name=Ablator | present in result |
| `RoundTrip_Precision` | amount = 3600.123456789 | round-trip exact via "R" format |
| `VesselSnapshotBuilder_Integration` | snapshot built via VesselSnapshotBuilder with resources | extraction matches expected sums |

Build test ConfigNodes manually for unit tests. The last test uses `VesselSnapshotBuilder` to verify extraction works against a realistic snapshot structure.

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
| Tree | `SaveRecordingResourceAndState` (~line 368) | `LoadRecordingResourceAndState` (~line 708) | RecordingTree.cs |

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
| `RoundTrip_Precision` | amount = 3600.123456789 → round-trip exact |
| `LocaleSafety` | amount with comma separator → parsed correctly via InvariantCulture |
| `LegacyRecording_NoNode_NullFields` | ConfigNode without RESOURCE_MANIFEST → both fields null |
| `MalformedResource_Skipped` | RESOURCE with empty name → skipped, counter incremented |

---

## T11.3 — Capture at recording boundaries

### Overview

Every site that takes a vessel snapshot is a candidate for resource extraction. But not every site needs it — we only need StartResources at recording start and EndResources at recording end. Mid-recording snapshots (periodic refresh, continuation refresh) don't need extraction.

### Capture strategy: extract from existing snapshots

We do NOT add new snapshot calls. We extract from snapshots that are already being taken. The extraction is a lightweight ConfigNode walk — no KSP API calls, no vessel access needed.

### StartResources vs start location fields — design tension

`Recording.cs` has a separate `CopyStartLocationFrom` method for start fields (StartBodyName, StartBiome, etc.), and `ApplyPersistenceArtifactsFrom` intentionally excludes them. StartResources is conceptually a "start field" but needs different handling: chain continuations need the **boundary-time** resources (what the vessel has when the new segment begins), not the parent's recording-start resources. This is why StartResources is copied in `ApplyPersistenceArtifactsFrom` (as a baseline) and then overridden at the continuation start site with the boundary-time value. Location fields don't need this override — a continuation's start body/biome is always freshly captured.

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

**Chain of custody:** `pendingStartResources` is set once in `StartRecording`, consumed once in `BuildCaptureRecording`, and must not be cleared between those two calls. `BuildCaptureRecording` sets `capture.StartResources` before the capture object is passed anywhere else. `ApplyPersistenceArtifactsFrom` (called later in the commit path) then copies it from the capture to the pending Recording. Verify this ordering during implementation.

**Why here and not later:** `StartRecording` is the canonical "beginning of a recording" site. The snapshot is freshest here. `BuildCaptureRecording` is called at stop time — by then the vessel's resources have changed.

#### B. Recording stop → EndResources

**Site:** `FlightRecorder.BuildCaptureRecording()` (line 4563-4570)

After `VesselSpawner.SnapshotVessel(capture, ...)` sets `capture.VesselSnapshot`, extract:

```csharp
capture.EndResources = VesselSpawner.ExtractResourceManifest(capture.VesselSnapshot);
```

This covers all three callers of `BuildCaptureRecording`: `StopRecording`, `StopRecordingForChainBoundary`, and the vessel-switch detection path.

#### C. Chain boundary continuations → continuation StartResources

Chain boundaries (dock/undock/EVA/split/atmosphere) commit the outgoing segment and start a new continuation recording. The outgoing segment gets EndResources from site B (via `BuildCaptureRecording`). The incoming continuation needs StartResources from the same boundary moment.

**Decision: StartResources = resources at the start of THIS segment.** For a continuation, that's the vessel state at boundary time = the outgoing segment's EndResources.

**Flow:** `CommitSegmentCore` operates on the **outgoing** segment (commits it). The continuation recording is created afterward when `FlightRecorder.StartRecording(isPromotion: true)` runs for the next segment. So the continuation's StartResources is set by site A — `StartRecording` captures `pendingStartResources` from the vessel's current snapshot, which at boundary time reflects the boundary state.

This means site A already handles continuations correctly: `StartRecording(isPromotion: true)` calls `RefreshBackupSnapshot` which takes a fresh snapshot of the vessel at boundary time. `pendingStartResources` is extracted from that fresh snapshot. No additional mechanism needed.

**What `ApplyPersistenceArtifactsFrom` copies:** It copies the parent's StartResources as a baseline. For continuations started via `StartRecording(isPromotion: true)`, the `pendingStartResources` from the fresh snapshot in `StartRecording` overwrites this baseline when `BuildCaptureRecording` runs at stop time. For continuations that don't go through `StartRecording` (e.g., `ChainSegmentManager.StartUndockContinuation`), `ApplyPersistenceArtifactsFrom` provides a reasonable fallback (the parent's resources), which is close enough for display purposes. Phase 12 logistics looks at the chain-level start/end, not mid-chain segments.

#### D. Breakup child recordings

**Site:** `ParsekFlight.CreateBreakupChildRecording()` (line 2890-2937)

After line 2913-2915 captures `VesselSnapshot` for the child:

```csharp
childRec.StartResources = VesselSpawner.ExtractResourceManifest(childRec.VesselSnapshot);
```

Debris typically doesn't stop recording cleanly (destroyed by physics), so EndResources for debris is left null. This is correct — debris resources are not meaningful for logistics.

#### E. Background recorder — child snapshots

**Site:** `BackgroundRecorder.HandleBackgroundVesselSplit()` (line 633-635)

After child snapshots are captured:

```csharp
child.StartResources = VesselSpawner.ExtractResourceManifest(child.VesselSnapshot);
```

**EndResources for background recordings:** Leave null. The background recorder's existing VesselSnapshot is from the initial split time, not finalization time. Extracting EndResources from it would give the same value as StartResources — no useful information. A fresh snapshot at finalization time is not available (vessel may be unloaded or destroyed). Null EndResources is the honest answer for background-recorded debris.

#### F. Tree promotion from breakup

**Site:** `ParsekFlight.PromoteToTreeForBreakup()` (line 3166-3167)

After copying snapshots from CaptureAtStop to rootRec:

```csharp
rootRec.StartResources = cap.StartResources;   // from original standalone recording
rootRec.EndResources = VesselSpawner.ExtractResourceManifest(rootRec.VesselSnapshot);
```

#### G. Optimizer propagation

**`RecordingOptimizer.SplitAtSection`** (line 296): The optimizer splits a recording at a TrackSection boundary. Resource manifest policy:

```csharp
// After existing VesselSnapshot transfer (line ~438-439):
// Step N+1: Resource manifests — first half keeps start, second half gets end.
second.EndResources = original.EndResources;
original.EndResources = null;
// original.StartResources unchanged (keeps the recording-start resources)
// second.StartResources stays null (no snapshot at environment boundary)
```

This is acceptable because optimizer splits are environment boundaries (atmosphere/exo transition), not dock/undock events. Resources don't meaningfully change at these boundaries. Phase 12 logistics uses the chain-level start/end, not optimizer-split segments.

**`RecordingOptimizer.MergeInto`** (line 217): The absorbed recording's EndResources replaces target's (later segment wins):

```csharp
// After existing VesselSnapshot transfer (line ~260-262):
if (absorbed.EndResources != null)
    target.EndResources = absorbed.EndResources;
// target.StartResources intentionally unchanged — it represents the earlier start.
```

#### H. ApplyPersistenceArtifactsFrom

**Site:** `Recording.ApplyPersistenceArtifactsFrom()` (line 252)

Add to the field copy block:

```csharp
StartResources = source.StartResources;  // shallow copy OK — dict is immutable after extraction
EndResources = source.EndResources;
```

No deep copy needed — resource manifests are never mutated after extraction. StartResources is copied here as a baseline for chain continuations; it may be overridden by the continuation's own StartRecording call (site A). See "StartResources vs start location fields" section above for rationale.

### Summary of capture sites

| Site | Sets | Extracts from |
|------|------|---------------|
| `FlightRecorder.StartRecording` | `pendingStartResources` | `lastGoodVesselSnapshot` |
| `FlightRecorder.BuildCaptureRecording` | `capture.StartResources`, `capture.EndResources` | `pendingStartResources` (stashed), `capture.VesselSnapshot` |
| `CreateBreakupChildRecording` | `childRec.StartResources` | `childRec.VesselSnapshot` |
| `BackgroundRecorder.HandleBackgroundVesselSplit` | `child.StartResources` | `child.VesselSnapshot` |
| `PromoteToTreeForBreakup` | `rootRec.StartResources`, `rootRec.EndResources` | `cap.StartResources`, `rootRec.VesselSnapshot` |
| `RecordingOptimizer.SplitAtSection` | `second.EndResources` (moved) | `original.EndResources` |
| `RecordingOptimizer.MergeInto` | `target.EndResources` | `absorbed.EndResources` |
| `ApplyPersistenceArtifactsFrom` | both fields | source recording |

---

## T11.4 — UI display

### Where

**Recordings Manager tooltip** — extend the existing hover tooltip (`DrawRecordingTooltip` at `RecordingsTableUI.cs:2352`). Add a "Resources" section after the storage section.

### Format

```
Resources:
  LiquidFuel: 3600 → 200 (-3400)
  Oxidizer: 4400 → 244 (-4156)
  Ore: 0 → 1500 (+1500)
  MonoPropellant: 30 → 28 (-2)
```

If only StartResources (recording in progress or no end snapshot):
```
Resources at start:
  LiquidFuel: 3600 / 3600
  Oxidizer: 4400 / 4400
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
- Use full resource names as stored (LiquidFuel, Oxidizer, MonoPropellant, Ore, etc.) — no abbreviation for v1.
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
// In ResourceManifest.cs (co-located with ResourceAmount struct)
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

Phase 12 needs per-dock/undock boundary deltas to compute delivery manifests. Phase 11's `ComputeResourceDelta` computes whole-recording start-vs-end, which gives the **total** delta (fuel consumed + cargo transferred). For Phase 12's delivery manifest, it must walk the chain structure, find the dock and undock boundary segments, and compute: `EndResources[dock-segment] - EndResources[undock-segment]` (per resource, only decreases). This chain-walking is Phase 12's responsibility — Phase 11 provides the per-segment data it needs.

```
Phase 12 usage:
  DeliveryManifest = per-resource decrease between dock and undock EndResources
  CostManifest = StartResources on the full recording (transport vessel start state)
  RouteCost = negative entries in whole-recording ComputeResourceDelta
```

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

## T11.6 — Dock target vessel PID capture

### Problem

When a vessel docks to a station/base, Parsek records the dock event with the **docking port PID** (`CommitDockUndockSegment` takes `dockPortPid`). But the **target vessel's persistentId** — the station the transport docked to — is not persisted to the Recording. It's available at event time (`data.to.vessel.persistentId` in `OnPartCouple`) and stored transiently in `pendingDockMergedPid`, but lost after the segment commits.

For logistics routes, the target vessel PID identifies the endpoint — "which station did I dock to?" Without it, the route analysis can't determine delivery destinations.

### What already exists

- `BranchPoint.TargetVesselPersistentId` — populated for tree-mode dock merges. But this is on the BranchPoint, not on the Recording, and only exists in tree mode.
- `pendingDockMergedPid` — local variable in `HandleDockUndockCommitRestart`, available at event time.
- `ChainSegmentManager.StartUndockContinuation(otherPid)` — the undock path already passes the other vessel's PID for continuation tracking, but it's transient.

### Fix

New field on Recording:

```csharp
public uint DockTargetVesselPid;  // PID of vessel docked to at this segment's boundary (0 = not a dock segment)
```

**Capture site:** `ChainSegmentManager.CommitDockUndockSegment` — when `type == PartEventType.Docked`, set `pending.DockTargetVesselPid = dockPortPid`. Wait — `dockPortPid` is currently the merged vessel PID (confusingly named). Looking at the call site in `ParsekFlight.HandleDockUndockCommitRestart`, `pendingDockMergedPid` is `data.to.vessel.persistentId` — the target vessel that stays. This is the value we need.

Actually, the parameter `dockPortPid` in `CommitDockUndockSegment` is already receiving the target vessel PID from `pendingDockMergedPid` at the call site (`ParsekFlight.cs:4861/4871`). It's just misnamed. We can capture it directly:

```csharp
// In CommitDockUndockSegment, before the commit:
if (type == PartEventType.Docked)
    pending.DockTargetVesselPid = dockPortPid;  // actually the merged vessel PID
```

**Serialization:** additive field, conditional write (only when non-zero), same pattern as other uint PIDs. Add to both `SaveRecordingMetadata`/`LoadRecordingMetadata` and `SaveRecordingResourceAndState`/`LoadRecordingResourceAndState`.

**Propagation:**
- `ApplyPersistenceArtifactsFrom` — copy from source
- `RecordingOptimizer.SplitAtSection` — first half keeps it (dock happens before the split point), second half gets 0
- `RecordingOptimizer.MergeInto` — absorbed recording's value wins if non-zero (later segment may have the dock)

### Tests

| Test | What |
|------|------|
| `DockSegment_CapturesTargetPid` | commit with Docked type → DockTargetVesselPid set |
| `UndockSegment_DoesNotCapture` | commit with Undocked type → DockTargetVesselPid stays 0 |
| `RoundTrip_Serialization` | serialize/deserialize → value preserved |
| `LegacyRecording_DefaultsZero` | missing field in ConfigNode → 0 |

### Why in Phase 11

This is boundary metadata captured alongside resource snapshots — same moment, same code path, same commit. It's a one-field addition that makes Phase 12 route analysis possible without post-hoc guesswork. Deferring it to Phase 12 would mean Phase 12 has to retrofit a capture mechanism into chain boundary code that Phase 11 already touches.

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

This enables the T11.1 integration test (`VesselSnapshotBuilder_Integration`) that builds a realistic snapshot, extracts the manifest, and verifies values.

### ScenarioWriter

Add V3 support for resource manifests so `InjectAllRecordings` produces recordings with resource data. At least one synthetic recording (e.g., "Flea Flight") should have resources for end-to-end verification.

---

## Implementation order

1. **T11.1** — `ResourceAmount` struct in `ResourceManifest.cs` + `ExtractResourceManifest` in `VesselSpawner.cs` + tests. Pure code, zero dependencies.
2. **T11.5** — `ComputeResourceDelta` in `ResourceManifest.cs` + tests. Pure code, depends only on T11.1 struct.
3. **T11.2** — Recording fields + serialization helpers + round-trip tests. Depends on T11.1.
4. **T11.6** — `DockTargetVesselPid` field + capture + serialization. Small, self-contained. Can be in the same commit as T11.2 or T11.3.
5. **T11.3** — Capture calls at all boundary sites. Depends on T11.1 + T11.2. This is the biggest task — many call sites, each needs careful placement.
6. **T11.4** — UI display. Depends on T11.1 + T11.2 + T11.5. Can only be tested with KSP running.

T11.1 and T11.5 are independent and can be done in the same commit. T11.2 + T11.6 build on them. T11.3 is the integration work. T11.4 is polish.

---

## Risks and edge cases

**Electric charge and IntakeAir excluded.** EC fluctuates every physics frame (solar, SAS, reaction wheels). IntakeAir has dynamic maxAmount based on intake area and speed. Both are environmental noise with no logistics meaning. Excluded at extraction time. Trivially reversible if ever needed.

**Docking fuel rebalance.** When two vessels dock, KSP rebalances fuel across connected tanks. The post-dock snapshot captures the rebalanced state. For logistics, this is correct — the delivery manifest should reflect what was actually transferred, not what the transport held before docking.

**Destroyed vessels.** If a vessel is destroyed before a snapshot, VesselSnapshot is null and EndResources stays null. Correct — a destroyed vessel has no meaningful end resources.

**Background vessels.** Background-recorded debris gets StartResources from the split-time snapshot. EndResources is left null — the background recorder has no fresh finalization snapshot, and extracting from the stale split-time snapshot would just duplicate StartResources. Null is the honest answer.

**Optimizer splits.** Split halves don't get boundary resource snapshots because the split is at an environment boundary (atmosphere transition), not a vessel state change. Resources are unchanged across these boundaries. The full chain's start/end resources (from recording start and final stop) are what logistics needs.

**Mining vessels.** A mining vessel's EndResources will show more ore than StartResources. `ComputeResourceDelta` correctly returns positive values. Phase 12 handles this naturally: a "mining route" delivers ore gained during the mission. The design doc already anticipates this (§5.2: "the delivery amount is the positive delta").

**SolidFuel on boosters.** Extraction sums across all parts including not-yet-staged boosters. This is correct behavior (total vessel resources at snapshot time). After staging, the booster's resources are on a separate debris recording.

---

## Architecture: logistics routes as a self-contained module

### The big picture

Parsek has three major subsystems that grew in phases, each with a clear boundary:

1. **Recording + playback** (v0.3–v0.5) — `FlightRecorder`, `GhostPlaybackEngine`, `RecordingStore`, `RecordingTree`, etc. Records flights, plays them back as ghosts.
2. **Game actions** (v0.6) — `GameActions/` folder: `Ledger`, `LedgerOrchestrator`, `RecalculationEngine`, 8 `IResourceModule` implementations. Tracks career-mode economic events. Lives in its own directory. Integrates with the rest of Parsek through a thin orchestrator (`LedgerOrchestrator`) called from `ParsekScenario` lifecycle hooks.
3. **Ghost playback engine** (extraction-ready for Gloops) — `GhostPlaybackEngine`, `IPlaybackTrajectory`, `IGhostPositioner`. Already has zero `Recording` references. Communicates outward through events (`OnLoopRestarted`, `OnPlaybackCompleted`, etc.) and reads inward through the `IPlaybackTrajectory` interface.

Phase 12 (logistics routes) should follow the game actions pattern: **a self-contained module in its own directory, with a thin orchestrator that connects it to Parsek's lifecycle hooks.** The route system should be removable by deleting the folder and removing the orchestrator calls — no behavioral changes to recording, playback, or the game actions system.

### Module structure

```
Source/Parsek/Logistics/
    Route.cs                    // data model (Route, RouteEndpoint, RouteStatus)
    RouteStore.cs               // static storage surviving scene changes (like RecordingStore)
    RouteScheduler.cs           // dispatch/delivery evaluation (pure logic, called per tick)
    RouteDelivery.cs            // ProtoPartResourceSnapshot modification on unloaded vessels
    RouteEndpointResolver.cs    // vessel finding by PID + surface proximity fallback
    RouteManifestComputer.cs    // derive delivery/cost manifests from recording chain resources
    RouteOrchestrator.cs        // thin integration layer — called from ParsekScenario hooks
```

### Routes are not loops

A logistics route is a **chain of recordings** (launch → transit → dock → transfer → undock → return), not a single recording with loop toggled on. The existing per-recording loop system replays one trajectory on repeat. A route replays an entire chain in sequence, then restarts from the first segment after a dispatch interval.

This means:
- **Route recordings do NOT use the per-recording loop toggle.** The route scheduler owns all timing.
- **The route scheduler orchestrates chain playback** — it tells the playback engine "play segment N now," and when that segment completes, starts segment N+1. When the last segment finishes, it triggers delivery and waits for the dispatch interval before restarting from segment 1.
- **The integration hook is `OnPlaybackCompleted`** (segment finished), not `OnLoopRestarted`. The route scheduler listens for "trajectory index N completed" and decides what to do next (start next segment, or if last → deliver + schedule next dispatch).
- **Routes and loops are siblings**, not parent-child. Both use the ghost playback engine to replay trajectories. Loops repeat a single trajectory; routes chain multiple trajectories sequentially with delivery logic between cycles.

### Integration seams (4 total)

These are the **only** places where logistics code touches existing Parsek code:

| Seam | Where | What | How to guard |
|------|-------|------|-------------|
| **Save/Load** | `ParsekScenario.OnSave`/`OnLoad` | `RouteOrchestrator.OnSave(node)`/`OnLoad(node)` | Null-check. Missing ROUTES node = no routes. |
| **Scheduler tick** | `ParsekScenario.Update` | `RouteOrchestrator.Tick(currentUT)` | Single call, no-op if no active routes. |
| **Playback completed** | `ParsekPlaybackPolicy.HandlePlaybackCompleted` | `RouteOrchestrator.OnSegmentCompleted(evt)` | Check if the completed trajectory belongs to an active route. No-op otherwise. |
| **Timeline events** | `Ledger` / `LedgerOrchestrator` | New `GameActionType` entries for ROUTE_DISPATCHED/ROUTE_DELIVERED | Additive enum values + display strings. |

No changes to `FlightRecorder`, `GhostPlaybackEngine`, `RecordingStore`, `RecordingTree`, `ChainSegmentManager`, `RecordingOptimizer`, or any recording/playback code.

### What the route module reads (Phase 11 output)

The route module is a **read-only consumer** of recording data:

```csharp
// RouteManifestComputer reads these from committed Recording objects:
rec.StartResources      // transport vessel start state → cost manifest
rec.EndResources        // transport vessel end state
// + chain segment walk for dock/undock boundary EndResources → delivery manifest

// RouteEndpointResolver reads:
rec.StartBodyName, rec.StartLatitude, rec.StartLongitude  // origin location
// dock event coordinates from chain boundary trajectory points  // destination
```

The route module never writes to Recording objects. It creates Route objects in its own `RouteStore`, serialized in its own `ROUTES` ConfigNode section inside `ParsekScenario`.

### What the route module writes (resource modification)

Resource delivery modifies vessels that are **not** part of the recording system — they're real KSP vessels at endpoint locations. The delivery path is:

```
RouteDelivery.DeliverResources(destVessels, deliveryManifest)
    for each vessel:
        if loaded:  part.RequestResource(name, -amount)     // KSP API
        if unloaded: protoPartResource.amount += amount      // direct field write
```

This is completely independent of Parsek's recording/playback/ghost systems. No ghost, no recording, no trajectory — just a vessel and a number.

### Lifecycle isolation

Routes have their own lifecycle, independent of recordings:

- **Creation:** from a committed recording (post-commit UI button), but the route is a separate entity with its own GUID.
- **Persistence:** own ConfigNode section (`ROUTES` in ParsekScenario), not part of recording metadata.
- **Deletion:** deleting a route does not affect its source recording. Deleting a recording orphans the route (no ghost replay, but resource transfers continue).
- **Revert:** route state is serialized in .sfs — quicksave/load restores it. Timeline events use the existing epoch isolation.

### Why not a separate assembly now

The roadmap defers assembly extraction to the Gloops boundary (pre-Phase 13). For Phase 12, a directory-level module within `Parsek.csproj` is the right granularity:

- Routes need direct access to `Recording.StartResources`/`EndResources`, `RecordingStore.CommittedRecordings`, `Ledger`, and `ParsekScenario` lifecycle. Cross-assembly access would require making all of these `public` or adding an interface layer — friction without benefit.
- The game actions system (v0.6) followed the same pattern: directory-level module, static orchestrator, integrated through ParsekScenario hooks. It works well and is easy to reason about.
- If Gloops extraction happens, routes stay in Parsek (they're Parsek policy, not ghost playback). The Gloops boundary is clean of route concerns.

### Route recording workflow (Phase 12)

The player explicitly declares a route recording session. Parsek records everything normally, then analyzes the committed chain to extract stops.

**Player flow:**

```
1. Player clicks "Start Route Recording" in Parsek UI
2. Player flies mission normally — launch, transit, dock at Base A,
   transfer resources via KSP UI, undock, transit, dock at Base B,
   pick up ore, undock, fly home
3. Player clicks "End Route Recording"
4. Parsek analyzes the committed chain → presents route summary
5. Player sets dispatch interval, confirms
6. Route goes live
```

**What "Start Route Recording" does:**
- Sets `IsRecordingRoute = true` on `ParsekScenario` (serialized, survives scene changes)
- UI shows "Route Recording Active" indicator
- Recording behavior is unchanged — same FlightRecorder, same chain boundaries, same snapshots

**What "End Route Recording" does:**
- Sets `IsRecordingRoute = false`
- Triggers `RouteAnalysisEngine.AnalyzeChain(recordings)` — the route analysis pass
- Shows route confirmation UI

**Route analysis pass** (`RouteAnalysisEngine` in `Logistics/`):

```
Input: ordered list of recordings committed during route session
       (tagged by a shared RouteSessionId or by UT range)

Walk chronologically through the chain:
  For each recording with DockTargetVesselPid != 0 (a dock event):
    Find the matching undock recording (next segment with Undocked event)
    stop.endpoint = {
        body, lat, lon from dock-time trajectory point,
        vesselPid = DockTargetVesselPid,
        isOrbital = altitude > body atmosphere height
    }
    stop.deliveryManifest = dock-segment EndResources - undock-segment StartResources
                            (per resource: positive = delivered to station,
                             negative = picked up from station)
    stop.transitDuration = dock UT - previous stop UT (or recording start UT)

  origin = first recording's start location + body
  isRoundTrip = last recording ends within 500m of origin (same body)
  totalTransit = last recording EndUT - first recording StartUT
```

This is pure logic over committed Recording fields — `DockTargetVesselPid` (T11.6), `StartResources`/`EndResources` (T11.1-T11.3), location context (Phase 10). Fully testable without KSP.

**Multi-stop example:**

```
Player flies: KSC → Base A (dock, deliver 150 LF) → Base B (dock, pick up 1200 Ore) → KSC

Route analysis produces:
  Origin: KSC, Kerbin
  Stop 1: Base A on Mun — delivers 150 LF, 183 Ox
  Stop 2: Base B on Mun — picks up 1200 Ore
  Total transit: 2d 4h
  Round-trip: Yes

Each dispatch cycle:
  UT+0:      Deduct cost from origin (if non-KSC)
  UT+0:      Ghost starts replaying segment 1 (KSC → Base A)
  UT+seg1:   Ghost starts segment 2 (Base A → Base B)
             Deliver 150 LF, 183 Ox to Base A endpoint vessels
  UT+seg2:   Ghost starts segment 3 (Base B → KSC)
             Pick up 1200 Ore from Base B (deduct from Base B vessels)
  UT+total:  Route cycle complete. 1200 Ore added to KSC depot (if exists).
             Schedule next dispatch.
```

**What Phase 11 provides for this:**
- `StartResources`/`EndResources` on every recording → delivery/pickup computation
- `DockTargetVesselPid` on dock segments → endpoint identification
- `StartBodyName`, `StartLatitude`, `StartLongitude` → origin/stop location
- `ExtractResourceManifest` and `ComputeResourceDelta` → pure analysis functions

**What Phase 12 adds:**
- Route recording mode flag (`IsRecordingRoute`)
- `RouteAnalysisEngine` — chain walk + stop extraction
- `Route`/`RouteStop` data model with multi-stop support
- `RouteScheduler` — chain-sequential ghost playback + delivery timing
- `RouteDelivery` — `ProtoPartResourceSnapshot` modification
- Route UI — confirmation panel, status display, dispatch interval config

### Implications for Phase 11

Phase 11 stays exactly as planned — it adds data fields and extraction functions, all within existing files. No `Logistics/` directory yet. No route concepts leak into the resource snapshot code.

The modularity constraint for Phase 11 is: **`StartResources`/`EndResources` must be usable by code that has no knowledge of routes.** This is already the case — they're plain Dictionary fields on Recording, serialized as additive ConfigNode data, extracted by a pure function. The UI tooltip (T11.4) consumes them directly. Phase 12's `RouteManifestComputer` will consume them through the same public fields. No coupling.

---

## Extension: Inventory Manifests

### Goal

Extend resource snapshots to capture KSP 1.12 inventory contents — parts stored in `ModuleInventoryPart` cargo containers. Players carrying spare solar panels, batteries, or science instruments to a base should see the inventory transfer reflected in the recording, and Phase 12 should replicate it each cycle.

The data is already in vessel snapshots (same situation as liquid resources before Phase 11). We just need to extract it.

### KSP 1.12 inventory internals (confirmed via decompilation)

**ConfigNode structure** (from `ModuleInventoryPart.OnSave`):

```
MODULE
{
    name = ModuleInventoryPart
    InventorySlots = 9
    packedVolumeLimit = 300
    STOREDPARTS
    {
        STOREDPART
        {
            slotIndex = 0
            partName = evaChute
            quantity = 1
            stackCapacity = 1
            variantName =
            PART
            {
                name = evaChute
                persistentId = 2769214603
                MODULE { name = ModuleCargoPart ... }
                RESOURCE { name = EVA Propellant  amount = 5  maxAmount = 5 }
            }
        }
        STOREDPART
        {
            slotIndex = 1
            partName = solarPanels5
            quantity = 1
            stackCapacity = 1
            variantName =
            PART { ... }
        }
    }
}
```

**Key facts:**
- `StoredPart` class: `slotIndex` (int), `partName` (string, KSP dot-form), `quantity` (int, >1 for stacked items), `stackCapacity` (int), `variantName` (string).
- `ModuleInventoryPart.storedParts`: `DictionaryValueList<int, StoredPart>` keyed by slot index.
- `InventorySlots` controls slot count. `packedVolumeLimit` (liters) and `massLimit` (tons) are optional capacity constraints.
- Stacking: only items with identical `partName` + `variantName` can share a slot. Controlled by `ModuleCargoPart.stackableQuantity` on the part cfg.
- Each STOREDPART's inner PART node is a full ProtoPartSnapshot with its own RESOURCE nodes. Resources inside stored items are independent of vessel resource flow.
- **Unloaded vessel access:** `ProtoPartModuleSnapshot.moduleValues.GetNode("STOREDPARTS")` → `GetNodes("STOREDPART")` → `GetValue("partName")`, `GetValue("quantity")`.
- **Part name dot conversion applies:** stored `partName` uses KSP's runtime dot-form.

### T11-INV.1 — Data model

```csharp
// New file: InventoryManifest.cs (mirrors ResourceManifest.cs)
internal struct InventoryItem
{
    public int count;       // total quantity across all inventories on the vessel
    public int slotsTaken;  // total inventory slots occupied by this item type
}

internal static class InventoryManifest
{
    internal static Dictionary<string, InventoryItem> ComputeInventoryDelta(
        Dictionary<string, InventoryItem> start,
        Dictionary<string, InventoryItem> end)
    // Mirrors ComputeResourceDelta: merged keys, delta = endCount - startCount, endSlots - startSlots
    // Returns InventoryItem with both count and slot deltas (Phase 12 needs slots for capacity checks)
}
```

**Why `InventoryItem` has `slotsTaken`:** Phase 12 delivery needs to know if the destination has capacity. A destination with 2 free slots can accept 2 non-stackable panels but not 3. `slotsTaken` is the inventory analog of `maxAmount` in `ResourceAmount`.

**Why the delta returns `InventoryItem` not `int`:** Phase 12 needs both count deltas (how many items to move) and slot deltas (how many destination slots are required). Delivering 4 non-stackable panels needs 4 slots; delivering 4 stackable EVA kits needs only 1 slot. Returning the full struct avoids Phase 12 re-deriving slot requirements from raw manifests.

### T11-INV.2 — Extraction function

```csharp
// In VesselSpawner.cs (co-located with ExtractResourceManifest)
internal static Dictionary<string, InventoryItem> ExtractInventoryManifest(ConfigNode vesselSnapshot)
```

**Algorithm:**

```
if vesselSnapshot is null → return null
parts = vesselSnapshot.GetNodes("PART")
if parts.Length == 0 → return null

manifest = new Dictionary<string, InventoryItem>()
for each PART node:
    modules = partNode.GetNodes("MODULE")
    for each MODULE node:
        if moduleNode.GetValue("name") != "ModuleInventoryPart" → skip
        storedPartsNode = moduleNode.GetNode("STOREDPARTS")
        if storedPartsNode is null → skip
        storedParts = storedPartsNode.GetNodes("STOREDPART")
        for each STOREDPART node:
            partName = storedPartNode.GetValue("partName")
            if partName is null/empty → skip
            quantity = int.TryParse(storedPartNode.GetValue("quantity"), default 1)
            if manifest.ContainsKey(partName):
                var item = manifest[partName]  // struct copy — read-modify-write
                item.count += quantity
                item.slotsTaken += 1
                manifest[partName] = item
            else:
                manifest[partName] = new InventoryItem { count = quantity, slotsTaken = 1 }

// Also accumulate total slot capacity across all inventory modules
totalInventorySlots += int.TryParse(moduleNode.GetValue("InventorySlots"), default 0)

return manifest.Count > 0 ? manifest : null
// Caller stores totalInventorySlots in Recording.Start/EndInventorySlots
```

The function returns both the manifest dict and the total slot count (via out parameter or a return tuple).

**Design notes:**
- No item filtering (unlike resources where EC/IntakeAir are excluded). All inventory items are meaningful cargo.
- Variants ignored for grouping — `partName` is the key. A white panel and gray panel both count as `solarPanels5`. Variant-aware delivery is v2.
- Each STOREDPART = 1 slot. Stacked items (quantity > 1) occupy 1 slot with count = quantity.

**Tests:**

| Test | Input | Expected |
|------|-------|----------|
| `NullInput_ReturnsNull` | null | null |
| `NoInventoryModules_ReturnsNull` | parts without ModuleInventoryPart | null |
| `SingleItem` | 1 STOREDPART (solarPanel, qty 1) | { solarPanel: count=1, slots=1 } |
| `MultipleItems` | 2 different STOREDPART nodes | both in manifest |
| `SameItem_MultipleInventories_Summed` | solarPanel in two different parts' inventories | count+slots summed |
| `StackableItem_QuantityRespected` | STOREDPART with quantity=3 | count=3, slotsTaken=1 |
| `EmptyStoredParts_ReturnsNull` | ModuleInventoryPart with empty STOREDPARTS node | null |
| `MissingPartName_Skipped` | STOREDPART with no partName | skipped |
| `MissingQuantity_DefaultsOne` | STOREDPART with no quantity value | count=1 |
| `MultipleInventoryModulesOnOnePart` | PART with two ModuleInventoryPart modules | items from both summed |

### T11-INV.3 — Recording fields + serialization

```csharp
// Recording.cs — alongside StartResources/EndResources
internal Dictionary<string, InventoryItem> StartInventory;  // null = no data
internal Dictionary<string, InventoryItem> EndInventory;     // null = no data
public int StartInventorySlots;  // total inventory slot capacity at start (0 = no data / no inventory)
public int EndInventorySlots;    // total inventory slot capacity at end
```

`StartInventorySlots`/`EndInventorySlots` are vessel-level totals (sum of `InventorySlots` across all `ModuleInventoryPart` modules). Phase 12 uses `EndInventorySlots - sum(slotsTaken)` to check destination capacity. Analogous to how `ResourceAmount.maxAmount` enables capacity checks for liquids.

**Serialization format:**

```
INVENTORY_MANIFEST
{
    ITEM { name = solarPanels5   startCount = 4  startSlots = 4  endCount = 0  endSlots = 0 }
    ITEM { name = batteryPack    startCount = 2  startSlots = 2  endCount = 0  endSlots = 0 }
}
```

Helpers `SerializeInventoryManifest`/`DeserializeInventoryManifest` in RecordingStore.cs, called from same 4 save/load sites immediately after the resource manifest helpers. Identical pattern (merged keys, conditional start/end fields, batch logging). Integer fields use `int.TryParse` with InvariantCulture.

### T11-INV.4 — Capture at boundaries

**Every site that captures resource manifests also captures inventory manifests.** Same snapshots, same moments, one additional extraction call per site. The capture table from T11.3 applies identically — just add `StartInventory`/`EndInventory` alongside `StartResources`/`EndResources` at each site. Also add to `ApplyPersistenceArtifactsFrom`.

No new capture sites needed.

### T11-INV.5 — UI display

Extend tooltip after the Resources section:

```
Inventory:
  solarPanels5: 4 → 0 (-4)
  batteryPack: 2 → 0 (-2)
```

`FormatInventoryManifest` helper mirrors `FormatResourceManifest`. Uses KSP part display names if `PartLoader.getPartInfoByName(name)?.title` is available (graceful fallback to internal name).

### Phase 12 implications

**Inventory delivery is harder than liquid resource delivery:**
- Liquid: modify `ProtoPartResourceSnapshot.amount` (one field write)
- Inventory: construct and insert `STOREDPART` ConfigNodes into `ModuleInventoryPart` MODULE data on unloaded vessels (subtree construction)

**Recommendation:** Phase 12 ships liquid-resource delivery first. Inventory delivery is a follow-on (v1.1) — the capture and analysis data is available from day one, the delivery mechanism is the risky part.

**Stored item internal state:** When Phase 12 delivers inventory items to unloaded vessels, it must decide whether to deliver items with their captured internal state (e.g., a half-charged stored battery) or in pristine state. The recording's STOREDPART > PART snapshot preserves the exact state at recording time. v1 recommendation: deliver in pristine state (use `PartLoader.getPartInfoByName` to construct a fresh snapshot). Captured state is a v2 refinement.

**Route data model extension** (Phase 12): `Route.InventoryDeliveryManifest` as `Dictionary<string, int>` alongside `DeliveryManifest`. Route validation: recording qualifies if resources OR inventory items decreased between dock and undock.

### Edge cases

**Volume limits:** v1 tracks slots only. Volume-precise delivery (`packedVolume` per item vs `packedVolumeLimit` per container) deferred to v2. Slots are an acceptable approximation.

**Items with internal resources:** A stored fuel tank contains fuel inside its STOREDPART > PART > RESOURCE nodes. These resources are NOT captured in the resource manifest (they live inside STOREDPART, not directly under PART > RESOURCE). This is correct — stored items' internal resources are not part of the vessel's operational fuel supply.

**EVA kerbal inventories:** EVA kerbals have ModuleInventoryPart. Extraction captures them naturally. No special handling.

**KIS (Kerbal Inventory System):** v1 targets stock ModuleInventoryPart only. KIS uses `ModuleKISInventory` with a different format. Invisible to extraction. KIS compatibility is Phase 15 territory.

### Implementation order

Inventory tasks slot after the resource manifest tasks:

```
T11.1 → T11.5 → T11.2 → T11.3 → T11.4  (resource manifests — done)
    ↓
T11-INV.1 → T11-INV.2 → T11-INV.3 → T11-INV.4 → T11-INV.5  (inventory manifests)
```

Each inventory task mirrors the corresponding resource task and follows the same patterns. Total new code: ~200 lines implementation + ~200 lines tests.
