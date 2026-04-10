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
4. **T11.3** — Capture calls at all boundary sites. Depends on T11.1 + T11.2. This is the biggest task — many call sites, each needs careful placement.
5. **T11.4** — UI display. Depends on T11.1 + T11.2 + T11.5. Can only be tested with KSP running.

T11.1 and T11.5 are independent and can be done in the same commit. T11.2 builds on them. T11.3 is the integration work. T11.4 is polish.

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

### Implications for Phase 11

Phase 11 stays exactly as planned — it adds data fields and extraction functions, all within existing files. No `Logistics/` directory yet. No route concepts leak into the resource snapshot code.

The modularity constraint for Phase 11 is: **`StartResources`/`EndResources` must be usable by code that has no knowledge of routes.** This is already the case — they're plain Dictionary fields on Recording, serialized as additive ConfigNode data, extracted by a pure function. The UI tooltip (T11.4) consumes them directly. Phase 12's `RouteManifestComputer` will consume them through the same public fields. No coupling.
