# KSP Rescue Contract & Docking Detection Investigation

## Summary

KSP rescue contracts create kerbals with `KerbalType.Unowned` placed in EVA or seated in parts, spawned at `DiscoveryLevels.Unowned`. The `RecoverKerbal` parameter completes on `onVesselRecovered` (landing back at KSC), not on docking. The most reliable detection signal for Parsek is `GameEvents.onKerbalTypeChange` which fires when the kerbal transitions from `Unowned` to `Crew` -- this happens at the AcquireCrew parameter completion, triggered by `onCrewBoardVessel`, `onCrewTransferred`, or `onPartCouple`. This single event cleanly identifies the rescue moment without needing to cross-reference contracts or track docking state.

## Rescue Contract Structure

### Contract Generation (`RecoverAsset.Generate`)

1. **Kerbal creation**: `HighLogic.CurrentGame.CrewRoster.GetNewKerbal(ProtoCrewMember.KerbalType.Unowned)` -- creates a new kerbal with type `Unowned` (not `Crew`). The kerbal's `rosterStatus` is set to `Assigned`, `seat` to `null`, `seatIdx` to `-1`.

2. **Recovery types**: The contract system has three types:
   - `KERBAL` -- rescue a stranded kerbal only
   - `PART` -- recover a part only
   - `COMPOUND` -- rescue a kerbal who is inside a part

3. **Location types**: `ORBITLOW`, `ORBITHIGH`, or `SURFACE` (ground-based rescue on Kerbin).

4. **Contract parameters added**:
   - `AcquireCrew` -- detects when the kerbal is aboard the player's vessel (the "pickup" moment)
   - `RecoverKerbal` -- detects when the vessel containing the kerbal is recovered at KSC
   - Optionally `AcquirePart` and `RecoverPart` for COMPOUND type

### Vessel Spawning (`OnAccepted`)

When the player accepts the contract:

1. **Orbital**: `GenerateOrbitalRecovery()` creates a ProtoVessel via:
   - `GenerateLoneKerbal()` -- EVA kerbal alone (part = `kerbalEVA`/`kerbalEVAfemale`/vintage variants)
   - `GenerateLonePart()` -- part alone (for PART type)
   - `GenerateKerbalInPart()` -- kerbal seated in a part (COMPOUND type, uses `SeatKerbal`)
   
2. **Surface**: `GenerateLandRecovery()` -- same generation but with `GroundNode()` applied (sets `sit=LANDED`, lat/lon/alt).

3. **Vessel properties**:
   - `VesselType.EVA` for lone kerbal, `VesselType.Station` (7) for orbital with part, `VesselType.Ship` (6) for surface with part
   - `DiscoveryLevels.Unowned` -- vessel is initially unknown/untracked
   - `prst=true` (persistent) -- vessel won't be cleaned up by KSP
   - Resources set to 0 for non-EVA parts (stranded vessel has no fuel)

4. **Discovery**: `DiscoverAsset()` is called on `onVesselLoaded` and `onFlightReady`. When the player's vessel gets close enough to render the target, KSP calls `VesselUtilities.DiscoverVessel(v)` which changes discovery level to `Owned`, making it visible on the map.

### Cleanup

`Cleanup()` is called on contract decline/expiry/finish. If the kerbal is still `Unowned`, it is expunged (`SystemUtilities.ExpungeKerbal`). If the vessel was never claimed (still `Unowned` discovery level), it is destroyed via `vessel.Die()`.

## RecoverKerbal Parameter Mechanics

### Events Subscribed

```csharp
GameEvents.onVesselRecovered  // Primary: checks if recovered vessel has the target kerbal
GameEvents.onCrewKilled       // Failure: kerbal died
GameEvents.onPartDie          // Failure: part containing kerbal destroyed
GameEvents.onVesselLoaded     // Discovery: calls RecoverAsset.DiscoverAsset
GameEvents.onFlightReady      // Discovery: scans all loaded vessels
```

### Completion Logic (`OnVesselRecovered`)

The parameter completes when the vessel is recovered at KSC (not when docked). It checks `v.GetVesselCrew()` against `kerbalsToRecover` list:

- **winCondition = All**: removes each found kerbal from the list; completes when list is empty
- **winCondition = Any**: completes when any single kerbal from the list is found

Important: `RecoverKerbal` is about the final step (landing at KSC), not the pickup. The pickup is handled by `AcquireCrew`.

### Failure Conditions

- `onCrewKilled`: kerbal name matches `evt.sender`
- `onPartDie`: checks `part.protoModuleCrew` for target kerbals

### Type Transition at AcquireCrew Completion

When `AcquireCrew` completes (kerbal is aboard player's vessel), `RecoverAsset.OnParameterStateChange` sets:

```csharp
protoKerbal.type = ProtoCrewMember.KerbalType.Crew;
```

This transition from `Unowned` to `Crew` fires `GameEvents.onKerbalTypeChange`.

## Docking Events

### Event Firing Sequence (ModuleDockingNode.DockToVessel)

When two vessels dock via docking ports, the following events fire in order:

1. **`GameEvents.onVesselDocking(uint pid1, uint pid2)`** -- fires first with both vessel persistent IDs (before any vessel merging)

2. **`GameEvents.onPartCouple(FromToAction<Part, Part>)`** -- fired by `Part.Couple()`, called inside `DockToVessel`. The `from` part is the docker (initiator), the `to` part is the dockee (target). At this point, vessels have not yet merged.

3. **`GameEvents.onVesselWasModified(Vessel)`** -- fires after vessel merge is complete

4. **`GameEvents.onDockingComplete(FromToAction<Part, Part>)`** -- fires last, after everything is settled

5. **`GameEvents.onPartCoupleComplete(FromToAction<Part, Part>)`** -- fires at the end of `Part.Couple()` after staging recalculation

### Same-Vessel Docking

```csharp
GameEvents.onSameVesselDock(FromToAction<ModuleDockingNode, ModuleDockingNode>)
GameEvents.onSameVesselUndock(FromToAction<ModuleDockingNode, ModuleDockingNode>)
```

These fire for ports on the same vessel (not relevant for rescue detection).

### DockedVesselInfo

`ModuleDockingNode.vesselInfo` stores the pre-dock vessel identity:
- `name` -- vessel name before docking
- `vesselType` -- vessel type before docking
- `rootPartUId` -- root part flight ID before docking

This is populated during `DockToVessel` and persisted through save/load.

### Data Available in onPartCouple

```csharp
GameEvents.FromToAction<Part, Part> data:
  data.from  -- the docker part (initiator)
  data.to    -- the dockee part (target)
  data.from.vessel  -- docker vessel (exists at fire time)
  data.to.vessel    -- dockee vessel (exists at fire time)
  vessel.GetVesselCrew()  -- crew list per vessel
  vessel.persistentId     -- vessel PID
  vessel.vesselType       -- vessel type
  vessel.protoVessel.discoveryInfo  -- discovery level
```

At `onPartCouple` time, both vessels still exist as separate entities. After the event returns, `Part.Couple` merges them.

## EVA Pickup / Alternative Rescue Paths

### AcquireCrew Detection Methods

The `AcquireCrew` contract parameter (which detects the "pickup" moment) subscribes to:

1. **`onCrewBoardVessel(FromToAction<Part, Part>)`** -- EVA kerbal boards a vessel. `action.from.vessel.vesselName` is the kerbal's name.

2. **`onCrewTransferred(HostedFromToAction<ProtoCrewMember, Part>)`** -- crew transferred between parts (e.g., via crew transfer dialog). `action.host.name` is the kerbal name, `action.from`/`action.to` are the source/destination parts.

3. **`onPartCouple(FromToAction<Part, Part>)`** -- docking. AcquireCrew calls `ScanVessel(action.from.vessel)` to check all crew on the docker vessel.

### Rescue Scenarios

| Scenario | Events Fired | Detection |
|---|---|---|
| EVA kerbal boards player vessel | `onCrewBoardVessel` | Kerbal name matches |
| Player docks with stranded pod | `onPartCouple` | Crew scan finds target |
| Player transfers crew between docked parts | `onCrewTransferred` | Kerbal name matches |
| Claw/grab stranded vessel | `onPartCouple` | Same as docking |

### Key Insight

In all cases, KSP's own contract system detects rescue via the same events. The AcquireCrew parameter fires `SetComplete()` which triggers `RecoverAsset.OnParameterStateChange`, which sets `protoKerbal.type = Crew`. This means `onKerbalTypeChange(pcm, Unowned, Crew)` fires in all rescue scenarios.

## Stranded Kerbal Identification

### Identifying Marks

A stranded (rescue contract) kerbal has these properties:

1. **`ProtoCrewMember.KerbalType == Unowned`** -- this is the definitive mark. Normal crew are `Crew` type, tourists are `Tourist`. `Unowned` specifically means "created by the contract system, not yet claimed".

2. **Vessel discovery level `Unowned`** -- the vessel starts as `DiscoveryLevels.Unowned` until the player gets close enough for `DiscoverAsset` to run.

3. **Vessel name = kerbal's name** -- for lone EVA kerbals, the vessel is named after the kerbal.

4. **Contract cross-reference**: `RecoverAsset` contracts have a `recoveryKerbal` field. Active contracts can be queried via `ContractSystem.Instance.GetCurrentContracts<RecoverAsset>()`.

### Recommended Identification Method

`ProtoCrewMember.type == Unowned` is the most reliable signal:
- It's set at creation and only changes to `Crew` when AcquireCrew completes
- It doesn't depend on vessel state, discovery level, or contract system access
- It works for all rescue scenarios (EVA, docked in pod, grabbed by claw)

## Detection Strategy for Parsek

### Recommended Approach: `onKerbalTypeChange`

**Primary signal**: `GameEvents.onKerbalTypeChange(ProtoCrewMember pcm, KerbalType oldType, KerbalType newType)`

This event fires:
- When a rescue kerbal transitions from `Unowned` to `Crew` (AcquireCrew completion)
- When a new kerbal is hired (but that's `Applicant` to `Crew` or just straight `Crew`)

**Detection logic**:
```csharp
void OnKerbalTypeChange(ProtoCrewMember pcm, 
    ProtoCrewMember.KerbalType oldType, 
    ProtoCrewMember.KerbalType newType)
{
    if (oldType == ProtoCrewMember.KerbalType.Unowned && 
        newType == ProtoCrewMember.KerbalType.Crew)
    {
        // This is a rescue! pcm.name is the rescued kerbal
        // pcm.trait is their role (Pilot/Engineer/Scientist)
        EmitRescueEvent(pcm);
    }
}
```

### Why This Is Better Than Alternatives

| Approach | Pros | Cons |
|---|---|---|
| **onKerbalTypeChange (Unowned->Crew)** | Single event, fires for all rescue types (EVA, dock, claw), no contract dependency, clean signal | Requires new GameEvents subscription |
| onPartCouple + crew scan | Catches docking rescues | Misses EVA boarding, requires cross-referencing KerbalType |
| onCrewBoardVessel + KerbalType check | Catches EVA rescues | Misses docking rescues |
| Contract parameter completion monitoring | Exact contract semantics | Requires accessing ContractSystem, fragile if mods add contracts |
| onVesselRecovered + crew check | Matches KSP's own completion | Too late -- happens at KSC, not during flight |

### Edge Cases to Handle

1. **Parsek's own crew mutations**: `SuppressCrewEvents` flag already exists in `GameStateRecorder`. The `onKerbalTypeChange` handler should check this flag (though Parsek doesn't create `Unowned` kerbals, so false positives are unlikely).

2. **Tourist contracts**: Tourists have `KerbalType.Tourist`, not `Unowned`. No collision.

3. **Mod-added kerbals**: Some mods might use `Unowned` type. The `Unowned->Crew` transition is still meaningful -- it means "this kerbal joined the program".

4. **Multiple rescues in one session**: Each kerbal fires separately. No deduplication needed.

## Crew Transfer Events

### Available Events

```csharp
// Fires when an EVA kerbal boards a vessel
// from = EVA kerbal's part, to = vessel part being boarded
GameEvents.onCrewBoardVessel = EventData<FromToAction<Part, Part>>

// Fires when crew is transferred between parts (in-vessel or cross-vessel)
// host = the ProtoCrewMember, from = source part, to = destination part
GameEvents.onCrewTransferred = EventData<HostedFromToAction<ProtoCrewMember, Part>>

// Fires when a kerbal goes on EVA
// from = vessel part, to = EVA kerbal part
GameEvents.onCrewOnEva = EventData<FromToAction<Part, Part>>

// Fires when kerbal type changes (hire, rescue)
// args: ProtoCrewMember, oldType, newType
GameEvents.onKerbalTypeChange = EventData<ProtoCrewMember, KerbalType, KerbalType>

// Fires AFTER type change (post-change notification)
GameEvents.onKerbalTypeChanged = EventData<ProtoCrewMember, KerbalType, KerbalType>

// Fires when kerbal roster status changes (Available, Assigned, Dead, Missing)
GameEvents.onKerbalStatusChange = EventData<ProtoCrewMember, RosterStatus, RosterStatus>
```

### Event Timing Notes

- `onKerbalTypeChange` fires BEFORE the change (pre-event). The docs say "Triggered when the ProtoCrewMember.KerbalType changes; occurs upon hiring crew or rescuing Kerbal".
- `onKerbalTypeChanged` fires AFTER the change (post-event).
- For Parsek's purposes, either works. The pre-event (`onKerbalTypeChange`) is the one documented in KSP's event comments. Use `onKerbalTypeChanged` if you want the PCM to already reflect the new type when inspected.

## Implementation Plan

### Step 1: Add GameStateEventType for Rescue

In `GameStateEvent.cs`, add:
```csharp
KerbalRescued     // 19  -- kerbal transitioned from Unowned to Crew
```

### Step 2: Subscribe to onKerbalTypeChange in GameStateRecorder

In `GameStateRecorder.Subscribe()`:
```csharp
GameEvents.onKerbalTypeChange.Add(OnKerbalTypeChange);
```

Handler:
```csharp
private void OnKerbalTypeChange(ProtoCrewMember pcm,
    ProtoCrewMember.KerbalType oldType,
    ProtoCrewMember.KerbalType newType)
{
    if (SuppressCrewEvents) return;
    if (pcm == null) return;
    
    // Only care about Unowned -> Crew (rescue contract completion)
    if (oldType != ProtoCrewMember.KerbalType.Unowned || 
        newType != ProtoCrewMember.KerbalType.Crew)
        return;
    
    var name = pcm.name ?? "";
    GameStateStore.AddEvent(new GameStateEvent
    {
        ut = Planetarium.GetUniversalTime(),
        eventType = GameStateEventType.KerbalRescued,
        key = name,
        detail = $"trait={pcm.trait ?? ""}"
    });
    ParsekLog.Info("GameStateRecorder", 
        $"Game state: KerbalRescued '{name}' ({pcm.trait ?? "?"})");
}
```

### Step 3: Add GameStateEventConverter Method

In `GameStateEventConverter.cs`, replace the D6 scaffold:
```csharp
internal static GameAction ConvertKerbalRescued(GameStateEvent evt, string recordingId)
{
    string trait = ExtractDetail(evt.detail, "trait") ?? "";
    return new GameAction
    {
        UT = evt.ut,
        Type = GameActionType.KerbalRescue,
        RecordingId = recordingId,
        KerbalName = evt.key,
        KerbalRole = trait,
        EndUT = (float)evt.ut  // rescue UT
    };
}
```

### Step 4: Wire Up Conversion

Add the `KerbalRescued` case to the event-to-action conversion switch in `GameStateEventConverter.ConvertToActions` (or wherever events are mapped to actions).

### Step 5: Tests

- Test `OnKerbalTypeChange` handler: verify that `Unowned->Crew` emits `KerbalRescued` event
- Test suppression: verify `SuppressCrewEvents = true` blocks the event
- Test non-rescue transitions: verify `Tourist->Crew`, `Crew->Crew`, etc. are ignored
- Test `ConvertKerbalRescued`: verify conversion produces correct `GameAction` fields

### Step 6: Logging

All paths already follow the established pattern. The handler should log:
- `Info`: successful rescue detection
- `Verbose`: filtered transitions (non-rescue type changes)

## Risks and Open Questions

### Low Risk

1. **`onKerbalTypeChange` vs `onKerbalTypeChanged`**: The pre-event vs post-event distinction. Using the pre-event means `pcm.type` might still be `Unowned` when we read it. The event args carry both old and new type explicitly, so we don't need to read from the PCM. Recommend using the pre-event (`onKerbalTypeChange`) since it matches KSP's own documentation.

2. **Unsubscribe**: Must add `GameEvents.onKerbalTypeChange.Remove(OnKerbalTypeChange)` in `Unsubscribe()`.

### Medium Risk

3. **Recording attribution**: The `KerbalRescued` event fires in the flight scene during an active recording session. The `recordingId` should be the current recording's ID. If no recording is active (e.g., the player accepted a contract and recovered from tracking station), the event has no recordingId -- this is fine, it becomes a KSC-level action.

4. **Duplicate rescue events after revert**: If the player reverts after a rescue, the kerbal goes back to `Unowned`. On re-rescue, `onKerbalTypeChange` fires again. This is correct behavior -- the ledger handles reverts via timeline pruning, and the new rescue event will appear at the new UT.

### Open Questions

5. **Rescue without active contract**: Can a player encounter an `Unowned` kerbal without a rescue contract? In theory, only rescue contracts create `Unowned` kerbals. Mods or debug menu could create them, but `Unowned->Crew` still semantically means "rescued" even in those cases.

6. **Rescue timing precision**: The `onKerbalTypeChange` fires at AcquireCrew completion time, which is the moment the kerbal boards or the docking happens. This is the correct moment for the action -- it's when the player "did the rescue work", not when they landed back at KSC.

7. **Contract ID correlation**: Currently the `KerbalRescue` action doesn't store which contract it's associated with. To correlate with `ContractComplete`, we would need to query `ContractSystem.Instance.GetCurrentContracts<RecoverAsset>()` and find the one whose `RecoveryKerbal.name` matches. This is optional -- the kerbal name alone is sufficient for the ledger.
