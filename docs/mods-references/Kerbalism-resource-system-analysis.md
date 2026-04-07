# Kerbalism Resource System Analysis

Source: `Kerbalism/src/Kerbalism/` — focused on resource flow and background processing patterns relevant to Parsek logistics routes.

## Architecture Overview

Kerbalism replaces KSP's stock background resource simulation with its own deferred-execution system. The key insight: all resource changes are **accumulated as deferred deltas**, then **synchronized to actual part resources in a single pass**. This avoids the incoherence problems of stock's per-module immediate writes.

### Core Classes

| Class | Role |
|---|---|
| `ResourceCache` | Global `Dictionary<Guid, VesselResources>` — one entry per vessel |
| `VesselResources` | Per-vessel: holds all `ResourceInfo` handlers + pending `ResourceRecipe` list |
| `ResourceInfo` | Per-resource-per-vessel: tracks Amount, Capacity, Level, Deferred delta, broker accounting |
| `ResourceRecipe` | Multi-input/multi-output conversion with proportional scaling |
| `ResourceBroker` | Named tag for UI/debug tracking of who consumed/produced what |
| `Background` | Dispatcher that runs each unloaded module's background update |
| `DB` / `VesselData` | Per-vessel custom data persistence inside KSP's ScenarioModule save |

## The Update Loop (Kerbalism.FixedUpdate)

### Loaded Vessels (every physics tick)
```
foreach vessel in FlightGlobals.Vessels:
    if loaded:
        vd.Evaluate()
        Profile.Execute(v, vd, resources, elapsed_s)   // rules + processes
        vd.ResourceUpdate(resources, elapsed_s)         // API module loaded calls
        resources.Sync(v, vd, elapsed_s)                // apply all deferred to parts
```

### Unloaded Vessels (one per tick, oldest first)
```
// Accumulate wall-clock time per unloaded vessel
ud.time += elapsed_s

// Pick the vessel with the largest accumulated time
if last_v != null:
    last_vd.Evaluate(false, last_time)
    Profile.Execute(last_v, last_vd, last_resources, last_time)
    Background.Update(last_v, last_vd, last_resources, last_time)
    last_resources.Sync(last_v, last_vd, last_time)
    unloaded.Remove(last_vd.VesselId)
```

**Key pattern**: Only ONE unloaded vessel is processed per physics tick. The vessel with the longest time since its last update is chosen. `elapsed_s` for that vessel is the total accumulated real time, not `fixedDeltaTime`. This means at high warp with many unloaded vessels, each gets updated infrequently but with large time deltas.

## Deferred Resource Modification

### Produce / Consume
`ResourceInfo.Produce(quantity, broker)` and `.Consume(quantity, broker)` simply accumulate into the `Deferred` field:
```csharp
public void Produce(double quantity, ResourceBroker broker) {
    Deferred += quantity;
    // track broker contribution for UI
}
public void Consume(double quantity, ResourceBroker broker) {
    Deferred -= quantity;
}
```
Nothing touches actual part resources until `Sync()`.

### Sync: Applying Deferred to Parts
`ResourceInfo.Sync()` is where `ProtoPartResourceSnapshot.amount` actually changes:

1. Read current amount/capacity from all parts (detecting external changes)
2. Clamp `Deferred` to `[-Amount, Capacity - Amount]` (cannot go negative or exceed capacity)
3. Call `PriorityTankSets.ApplyDelta(Deferred)` which distributes the delta across part resources
4. Reset `Deferred = 0`

For **unloaded vessels**, the sync set wraps `ProtoPartResourceSnapshot`:
```csharp
class WrapPPRS : Wrap {
    ProtoPartResourceSnapshot res;
    override double amount { get => res.amount; set => res.amount = value; }
    override double maxAmount { get => res.maxAmount; set => res.maxAmount = value; }
}
```
This is how Kerbalism modifies `ProtoPartResourceSnapshot.amount` — through these wrappers during the `ApplyDelta` call inside `Sync()`.

### Priority-Based Distribution
Resources are grouped into `TankSet` objects sorted by KSP's part resource priority. When pulling (consuming), highest-priority tanks drain first. When pushing (producing), lowest-priority tanks fill first. Each tank within a set receives a proportional share based on its current amount (pulling) or free space (pushing).

## ResourceRecipe: Multi-Resource Conversion

Recipes handle "consume X to produce Y" with automatic proportional scaling when inputs are scarce or outputs are full.

### Recipe Execution
`ResourceRecipe.ExecuteRecipes()` runs iteratively until all recipes are fully executed or bottlenecked:

```csharp
while (executing) {
    for each recipe:
        if recipe.left > 0:
            executing |= recipe.ExecuteRecipeStep(v, resources);
}
```

Each step:
1. Find `worst_input` — the smallest ratio of (available amount / requested amount) across all inputs
2. Find `worst_output` — the smallest ratio of (free capacity / requested output) across non-dumpable outputs
3. `worst_io = min(worst_input, worst_output)` — the fraction of the recipe that can execute
4. Consume `input.quantity * worst_io` from each input
5. Produce `output.quantity * worst_io` to each output
6. `left -= worst_io`

The iterative loop handles chains: recipe A produces resource R, recipe B consumes R. Multiple passes let B consume what A produced in the same tick.

### Dump Flag
Outputs marked `dump = true` ignore capacity limits — excess is vented. This prevents a full output tank from blocking the entire conversion chain.

### Usage Example (stock converter background)
```csharp
ResourceRecipe recipe = new ResourceRecipe(ResourceBroker.StockConverter);
foreach (var ir in converter.inputList)
    recipe.AddInput(ir.ResourceName, ir.Ratio * exp_bonus * elapsed_s);
foreach (var or in converter.outputList)
    recipe.AddOutput(or.ResourceName, or.Ratio * exp_bonus * elapsed_s, or.DumpExcess);
resources.AddRecipe(recipe);
```

## Background Module Processing

`Background.Update()` iterates all `ProtoPartSnapshot` + `ProtoPartModuleSnapshot` pairs in the unloaded vessel, matches each to a known module type, and calls the appropriate handler.

### Reading Persisted Module State
Uses `Lib.Proto` helpers to read KSPField values from `ProtoPartModuleSnapshot.moduleValues`:
```csharp
bool isActive = Lib.Proto.GetBool(m, "IsActivated");
double lastUpdate = Lib.Proto.GetDouble(m, "lastUpdateTime");
string resourceName = Lib.Proto.GetString(m, "ResourceName");
```

### Writing Back Module State
```csharp
Lib.Proto.Set(m, "lastUpdateTime", Planetarium.GetUniversalTime());
```
This writes to `module.moduleValues.SetValue(name, value.ToString(), true)`.

### Defeating Stock Background Sim
Stock converters/drills have their own catch-up simulation. Kerbalism prevents double-counting by forcing `lastUpdateTime` to now after its own processing:
```csharp
Lib.Proto.Set(m, "lastUpdateTime", Planetarium.GetUniversalTime());
```

### External Module API (IKerbalismModule)
Third-party mods can implement background processing by providing a static `BackgroundUpdate` method with a specific signature. Kerbalism discovers it via reflection and calls it with:
- The vessel, proto snapshots, prefab references
- `Dictionary<string, double> availableResources` — current amounts
- `List<KeyValuePair<string, double>> resourceChangeRequest` — the module fills this with rates
- `elapsed_s` — time delta

After the call, Kerbalism converts the change requests into Produce/Consume calls.

## Per-Vessel Data Persistence (DB / VesselData)

Kerbalism uses a `ScenarioModule` (`Kerbalism` class, decorated with `ScenarioCreationOptions.AddToAllGames`) to persist its database.

### Storage Structure
```
SCENARIO { name = Kerbalism }
    vessels2 {
        <vessel-guid> {
            msg_signal = False
            cfg_ec = True
            supplies { ... }
            parts {
                <flightID> { drive { ... } }
            }
        }
    }
```

### Load/Save
- `DB.Load()`: Iterates `HighLogic.CurrentGame.flightState.protoVessels`, creates `VesselData` for each, keyed by `vesselID`
- `DB.Save()`: Iterates the same protoVessel list, saves only vessels that still exist in KSP persistence
- VesselData is accessed via extension methods: `vessel.KerbalismData()` / `protoVessel.KerbalismData()`
- Creates VesselData on demand if missing (lazy initialization with logging)

### Edge Cases
- Vessels with `Guid.Empty` (flags) are skipped
- Dead EVA kerbals, asteroids, debris, flags, deployed ground parts are marked `IsSimulated = false` and skip all processing
- The `ResourceCache` is purged per-vessel when needed; fully cleared on scene change

## Vessel-to-Vessel Resource Transfer

**Kerbalism has NO vessel-to-vessel physical resource transfer for unloaded vessels.** The `Drive.Transfer` methods found in the codebase transfer only science data between vessels (via the HardDrive module), not physical resources like fuel or ore.

The `Callbacks.cs` file handles resource transfer only during EVA boarding/departing — moving supplies from a vessel to an EVA kerbal's personal container. This is done via `Part.RequestResource()` on loaded vessels only.

This is a gap that Parsek's logistics routes would fill.

## Patterns Reusable for Parsek Logistics

### 1. Deferred Accumulate-Then-Sync
Parsek could adopt the same pattern for logistics deliveries:
- During route evaluation: calculate resource delta for the tick
- Accumulate into a deferred buffer per resource per vessel
- Apply to `ProtoPartResourceSnapshot.amount` in a single sync pass
- Clamp to [0, maxAmount] to handle tank-full / empty-tank edge cases

### 2. ProtoPartResourceSnapshot Access for Unloaded Vessels
```csharp
foreach (ProtoPartSnapshot p in v.protoVessel.protoPartSnapshots)
    foreach (ProtoPartResourceSnapshot r in p.resources)
        if (r.resourceName == "LiquidFuel" && r.flowState)
            // r.amount and r.maxAmount are directly readable/writable
```

### 3. Priority-Based Tank Filling
Kerbalism's `PriorityTankSets` pattern — filling low-priority tanks first, draining high-priority first — is a nice-to-have for realism but adds complexity. A simpler approach for logistics: distribute proportionally across all flowing tanks.

### 4. One-Unloaded-Per-Tick Throttling
Processing only one unloaded vessel per tick with accumulated time is a proven performance pattern. For logistics routes, Parsek could process one route per tick, or batch all routes for a vessel pair when one of them comes up for processing.

### 5. ScenarioModule Persistence
The `DB.Load/Save` pattern of iterating `flightState.protoVessels` and keying by `vesselID` is directly applicable to Parsek's route persistence. Extension methods like `vessel.KerbalismData()` provide clean access.

### 6. Clamping and Safety
- Always clamp deferred deltas: `Clamp(delta, -amount, capacity - amount)`
- Check `r.flowState` before touching a resource (user may have disabled flow)
- Skip vessels that are invalid (debris, flags, dead EVA)
- Guard against zero capacity (avoid division by zero in level calculation)

## Key Differences for Parsek

Unlike Kerbalism's single-vessel resource sim, Parsek logistics routes are inherently **cross-vessel**: one vessel's Produce is another's Consume. This means:
- Both source and destination vessels must be processed atomically in the same tick
- The amount transferred is `min(source.available, destination.freeSpace, route.ratePerSecond * elapsed_s)`
- Both vessels' `ProtoPartResourceSnapshot.amount` values must be updated together
- If either vessel is loaded, use `PartResource` directly; if unloaded, use `ProtoPartResourceSnapshot`
