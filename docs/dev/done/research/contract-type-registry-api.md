# KSP Contract System & Type Registry Investigation

## Summary

KSP's contract system uses assembly scanning to discover all `Contract` subclasses at startup, stores them in `ContractSystem.ContractTypes`, and uses `Activator.CreateInstance` + `Contract.Load(contract, configNode)` to recreate contracts from ConfigNode data. The `type` field in saved CONTRACT nodes maps to the class name, looked up via `ContractSystem.GetContractType(typeName)`. This means Parsek can fully recreate any contract (stock or modded, including Contract Configurator) from a saved ConfigNode snapshot, as long as the mod's assemblies are loaded -- which they always are at runtime. The `state` field is deserialized directly from the node, so contracts can be restored in any state without triggering side effects. The `Contracts` and `ContractsFinished` lists on `ContractSystem.Instance` are plain `List<Contract>` with public getters, making direct manipulation straightforward.

## Contract Lifecycle & States

The `Contract.State` enum has 10 values:

| Value | Int | Description |
|-------|-----|-------------|
| Generated | 0 | Internal: just created by `Contract.Generate()`, not yet offered |
| Offered | 1 | Shown in Mission Control, waiting for player action |
| OfferExpired | 2 | Offer timed out (`dateExpire` passed) |
| Declined | 3 | Player explicitly declined |
| Cancelled | 4 | Player cancelled an active contract |
| Active | 5 | Player accepted, parameters being tracked |
| Completed | 6 | All parameters met |
| DeadlineExpired | 7 | Active contract deadline passed |
| Failed | 8 | Parameter failure or explicit fail |
| Withdrawn | 9 | System withdrew the offer (surplus management) |

Typical lifecycle:
- `Generated -> Offered` (via `Offer()`)
- `Offered -> Active` (via `Accept()`) or `Offered -> Declined` (via `Decline()`) or `Offered -> Withdrawn` (via `Withdraw()`)
- `Active -> Completed` (via `Complete()` or parameter state change) or `Active -> Failed` (via `Fail()`) or `Active -> Cancelled` (via `Cancel()`)

`IsFinished()` returns true for states 2-4 (OfferExpired, Declined, Cancelled) and 6-9 (Completed, DeadlineExpired, Failed, Withdrawn).

## Contract Creation from ConfigNode

### The Load path (critical for Parsek)

`Contract.Load(Contract contract, ConfigNode node)` is a **static method** that populates an already-instantiated contract from a ConfigNode. The call chain is:

1. `ContractSystem.LoadContract(ConfigNode cNode)`:
   - Reads `type` value from the node (the class name, e.g. "BaseContract", "ExplorationContract")
   - **Removes** the `type` value from the node (mutates input!) via `cNode.RemoveValues("type")`
   - Calls `GetContractType(typeName)` to find the `System.Type`
   - Creates instance via `Activator.CreateInstance(contractType)`
   - Calls `Contract.Load(contract, cNode)` to populate it

2. `Contract.Load(Contract contract, ConfigNode node)`:
   - Reads: `guid`, `prestige`, `seed`, `state`, `viewed`, `values` (12 comma-separated floats), `agent`/`agentName`, `deadlineType`, `expiryType`, `autoAccept`, `ignoresWeight`
   - The `values` field packs: TimeExpiry, TimeDeadline, FundsAdvance, FundsCompletion, FundsFailure, ScienceCompletion, ReputationCompletion, ReputationFailure, dateExpire, dateAccepted, dateDeadline, dateFinished
   - Iterates PARAM child nodes, uses `ContractSystem.GetParameterType(name)` + `Activator.CreateInstance` + `parameter.Load(configNode)` to rebuild parameters
   - Calls `contract.OnLoad(node)` for subclass-specific data
   - Calls `contract.SetupID()` to regenerate the deterministic contract ID

**Key insight: `Contract.Load` sets the `state` field directly via enum parsing -- it does NOT call `SetState()`, so no side effects (no rewards, no penalties, no events) are triggered during load.** This is exactly what Parsek needs.

### Important caveat: `RemoveValues("type")`

`ContractSystem.LoadContract` mutates the input ConfigNode by removing the `type` value. Parsek must either:
- Clone the snapshot ConfigNode before passing it to the load pipeline, or
- Re-add the `type` value to the snapshot after load, or
- Use the same pattern: read type, remove it, then call `Contract.Load`

## Type Registry & Discovery

### Assembly scanning at startup

`ContractSystem.GenerateContractTypes()` runs once (guarded by null check) and uses KSP's `AssemblyLoader.loadedAssemblies.TypeOperation(action)` to scan all loaded assemblies. It collects every type that:
- `IsSubclassOf(typeof(Contract))` -- true
- Is not `Contract` itself
- Is not in `Expansions.Serenity.Contracts` namespace unless Serenity DLC is installed

Results stored in `ContractSystem.ContractTypes` (static `List<Type>`).

### Lookup

`ContractSystem.GetContractType(string typeName)` does a linear scan by `Type.Name` (not full qualified name). This means the lookup key in saved data is just the class name, e.g. "BaseContract", "SatelliteContract", "SurveyContract".

### Parameter types

Same pattern: `ContractSystem.ParameterTypes` populated via assembly scanning for `ContractParameter` subclasses. Looked up by `GetParameterType(typeName)`.

### Predicate types

`ContractSystem.PredicateTypes` -- same scanning pattern for `ContractPredicate` subclasses.

### Mandatory types

`ContractSystem.MandatoryTypes` -- hardcoded list: `{ typeof(ExplorationContract) }`.

## State Mutation API

### Public state transition methods

All transition methods on `Contract` have **state guards** and **side effects**:

| Method | Required State | New State | Side Effects |
|--------|---------------|-----------|-------------|
| `Offer()` | Generated | Offered | Sets dateExpire, dateDeadline if Floating |
| `Accept()` | Offered | Active | Sets dateAccepted, dateDeadline if Floating |
| `Decline()` | Offered | Declined | Sets dateFinished, reputation penalty |
| `Cancel()` | Active | Cancelled | Sets dateFinished |
| `Complete()` | Active | Completed | Sets dateFinished |
| `Fail()` | Active | Failed | Sets dateFinished |
| `Withdraw()` | Any except Withdrawn | Withdrawn | (no date change) |

All of these call `SetState(newState)` which has extensive side effects.

### `SetState(State newState)` internals

When `SetState` is called, it:

1. **Register/Unregister**: If new state is Active, calls `Register()` (subscribes parameters to GameEvents). Otherwise calls `Unregister()`.
2. **Fires `OnStateChange` event** on the contract instance.
3. **Adjusts contract weights** via `ContractSystem.AdjustWeight()` (unless IgnoresWeight/AutoAccept).
4. **State-specific handlers**:
   - `Offered`: fires `GameEvents.Contract.onOffered`
   - `Active`: calls `AwardAdvance()` (gives funds!), fires `onAccepted`
   - `Completed`: calls `AwardCompletion()` (gives funds/science/rep!), sends message, fires `onCompleted` + `onFinished`
   - `Failed`: calls `PenalizeFailure()` (takes funds/rep!), sends message, fires `onFailed` + `onFinished`
   - `Cancelled`: calls `PenalizeCancellation()` (takes funds/rep!), sends message, fires `onCancelled` + `onFailed` + `onFinished`
   - `DeadlineExpired`: calls `PenalizeFailure()`, fires `onFailed` + `onFinished`
   - `Declined`: fires `onDeclined`
   - `Withdrawn`: fires `onFinished`

**Critical for Parsek: Do NOT use `Complete()`/`Fail()`/`Cancel()`/`Accept()` etc. to restore state. These trigger rewards/penalties and fire GameEvents, which would corrupt the ledger. Instead, restore contracts from ConfigNode snapshots where the `state` field is already set to the desired value -- `Contract.Load` sets state directly without calling `SetState`.**

### The `state` field

The `state` field is a `private` `[SerializeField]` field with only a read-only public property `ContractState`. There is no public setter. Direct mutation options:
- **Load from ConfigNode** (preferred -- sets state as part of deserialization, no side effects)
- **Reflection** (fallback -- set the private `state` field directly)
- The `Withdraw()` method is the only one that doesn't check prior state (except self-transition guard), but it only transitions to Withdrawn

## Active Contracts List Management

### Data structures

```csharp
[SerializeField] private List<Contract> contracts = new List<Contract>();
[SerializeField] private List<Contract> contractsFinished = new List<Contract>();
```

Exposed as:
```csharp
public List<Contract> Contracts => contracts;          // active + offered
public List<Contract> ContractsFinished => contractsFinished;  // terminal states
```

These are **direct references** to the internal lists, not copies. Parsek can manipulate them directly:
- `ContractSystem.Instance.Contracts.Clear()`
- `ContractSystem.Instance.Contracts.Add(contract)`
- `ContractSystem.Instance.ContractsFinished.Clear()`
- `ContractSystem.Instance.ContractsFinished.Add(contract)`

### Existing manipulation methods

- `ClearContractsCurrent()`: calls `Kill()` on each contract (fires `onFinished`, calls `Unregister()`), then clears the list. **Do not use -- has side effects.**
- `ClearContractsFinished()`: just clears the list. **Safe to use.**
- No `AddContract()` or `RemoveContract()` methods exist. Direct list manipulation is the only option.

### The UpdateDaemon

`ContractSystem` runs an `UpdateDaemon` coroutine that:
- Calls `UpdateContracts()` each frame (checks for finished contracts, moves them to `contractsFinished`, calls `Update()` on each)
- Calls `RefreshContracts()` periodically (generates new offers to fill slots, withdraws surplus)

After patching, the daemon will immediately start processing the new contract list. Contracts in terminal states will be moved to `contractsFinished` on the next update frame. Active contracts will have their parameters updated.

## Save/Load Serialization

### ConfigNode structure in .sfs

```
SCENARIO
{
    name = ContractSystem
    CONTRACTS
    {
        CONTRACT
        {
            guid = <GUID>
            type = BaseContract    // Class name -- used for type lookup
            prestige = 0           // int: 0=Trivial, 1=Significant, 2=Exceptional
            seed = 12345
            state = Active         // enum name string
            viewed = Read          // enum name string
            agent = Kerbodyne
            agentName = Kerbodyne
            deadlineType = Floating
            expiryType = Floating
            values = 432000,5961600,36000,91000,36000,0,21,14,17432000,17000,22961600,0
            // Subclass-specific data (from OnSave):
            targetBody = 1
            capacity = 5
            contextual = False
            PARAM
            {
                name = VesselSystemsParameter
                // parameter-specific data
            }
            PARAM
            {
                name = CrewCapacityParameter
                // ...
            }
        }
        CONTRACT_FINISHED
        {
            // Same structure as CONTRACT
        }
    }
    WEIGHTS
    {
        BaseContract = 42
        SatelliteContract = 30
        // ...
    }
}
```

### The `values` field

12 comma-separated numbers packed into a single string:
```
TimeExpiry, TimeDeadline, FundsAdvance, FundsCompletion, FundsFailure,
ScienceCompletion, ReputationCompletion, ReputationFailure,
dateExpire, dateAccepted, dateDeadline, dateFinished
```

### Parsek's existing snapshot

Parsek already captures full ConfigNode snapshots at contract accept time via `contract.Save(contractNode)` and stores them in `GameStateStore.contractSnapshots`. The snapshot includes the `type` field (added by `Contract.Save()`), all values, parameters, and subclass data. This is exactly what's needed for restoration.

## Offered Contracts & Generation

### Generation pipeline

1. `RefreshContracts()` is called periodically (every `updateInterval=10` seconds, or on scene/vessel change)
2. It calculates target counts per prestige tier based on reputation
3. Withdraws surplus offered contracts via `WithdrawSurplusContracts()`
4. Generates new contracts via `GenerateContracts()`:
   - `WeightedContractChoice()` picks a contract type based on weights
   - `Contract.Generate(type, difficulty, seed, State.Generated)` creates the contract
   - Checks for duplicate ContractIDs against active and recent finished contracts
   - `contract.Offer()` transitions to Offered state
   - `contracts.Add(contract)`
5. `GameEvents.Contract.onContractsListChanged.Fire()` if anything changed

### Controlling offers after rewind

After patching the contract list, `RefreshContracts()` will run on the next update cycle and may:
- Withdraw "extra" offered contracts if the count exceeds the target for that tier
- Generate new contracts to fill empty slots

**Parsek cannot directly control which contracts are offered** -- generation is seed-based but uses `Random.Range` with runtime state. However, Parsek can:
1. Restore all contracts (active, offered, finished) from snapshots
2. The offered pool will be whatever was offered at the snapshot time
3. After restoration, the daemon may gradually replace expired offers with new ones (which is correct behavior)

### Contract weights

`ContractWeights` is a `static Dictionary<string, int>` loaded from the WEIGHTS node. Parsek should consider saving/restoring this dictionary to preserve the player's contract preference history. However, weight changes are small and cumulative, so this is low priority.

## Contract Configurator Compatibility

### How CC contracts work

Contract Configurator (CC) defines a single contract type class (typically `ConfiguredContract`) that acts as a generic container. CC contracts:
- Are subclasses of `Contract`, so they appear in `ContractSystem.ContractTypes` via assembly scanning
- Use `OnLoad(ConfigNode)` / `OnSave(ConfigNode)` to serialize CC-specific data (contract groups, requirements, behaviors, custom parameters)
- The `type` field in the saved node will be the CC class name (e.g. `ConfiguredContract`)
- CC parameters are subclasses of `ContractParameter` and are found by the same assembly scanning

### Round-trip safety

Since Parsek's approach is:
1. Capture full ConfigNode snapshot via `contract.Save()` (which calls the CC override of `OnSave`)
2. Restore via the standard `LoadContract` pipeline (which calls CC's `OnLoad`)

This should work correctly as long as:
- The same CC version is installed (parameter class names haven't changed)
- CC's `OnLoad` can handle the data it produced in `OnSave` (which it should -- this is the same path KSP uses for normal save/load)
- No external state references have been invalidated (e.g., CC waypoints, vessel references)

### Potential CC issues

- **Waypoint state**: CC contracts with map waypoints may have waypoints registered with `WaypointManager`. After patching contracts, orphaned or missing waypoints could occur. CC's `OnLoad` typically recreates waypoints, so this may self-heal.
- **Behavior state**: CC "behaviours" (orbit generators, message triggers, etc.) have their own state. The ConfigNode snapshot should capture this, but some behaviours may reference runtime state that no longer exists after rewind.
- **Expression evaluation**: CC uses an expression language for dynamic contract text and requirements. These evaluate at generation time and the results are saved, so they should round-trip correctly.

## Implementation Plan for Parsek

### PatchContracts implementation

```csharp
internal static void PatchContracts()
{
    if (ContractSystem.Instance == null)
    {
        ParsekLog.Verbose(Tag, "PatchContracts: ContractSystem.Instance null -- skipping");
        return;
    }

    // 1. Unregister all current active contracts (prevents stale event subscriptions)
    var currentContracts = ContractSystem.Instance.Contracts;
    for (int i = 0; i < currentContracts.Count; i++)
    {
        if (currentContracts[i].ContractState == Contract.State.Active)
            currentContracts[i].Unregister();
    }

    // 2. Clear both lists
    currentContracts.Clear();
    ContractSystem.Instance.ContractsFinished.Clear();

    // 3. Rebuild from snapshots
    //    Need: a method to get the target contract list from the ledger/recalculation
    //    Each snapshot is a ConfigNode with a "type" field
    int restored = 0;
    int failed = 0;
    foreach (var snapshot in targetContractSnapshots)
    {
        ConfigNode cNode = snapshot.contractNode.CreateCopy(); // Clone! LoadContract mutates
        string typeName = cNode.GetValue("type");
        if (typeName == null)
        {
            failed++;
            continue;
        }
        cNode.RemoveValues("type");
        Type contractType = ContractSystem.GetContractType(typeName);
        if (contractType == null)
        {
            failed++;
            continue;
        }
        Contract contract = (Contract)Activator.CreateInstance(contractType);
        contract = Contract.Load(contract, cNode);
        if (contract == null)
        {
            failed++;
            continue;
        }

        if (contract.IsFinished())
            ContractSystem.Instance.ContractsFinished.Add(contract);
        else
            currentContracts.Add(contract);

        restored++;
    }

    // 4. Re-register active contracts
    for (int i = 0; i < currentContracts.Count; i++)
    {
        if (currentContracts[i].ContractState == Contract.State.Active)
            currentContracts[i].Register();
    }

    // 5. Fire contracts loaded event so UI refreshes
    GameEvents.Contract.onContractsLoaded.Fire();
}
```

### Snapshot capture strategy

The current approach (snapshot at accept time) captures only accepted contracts. For full restoration, Parsek needs snapshots of **all** contracts at the rewind point, including:
- Offered contracts (so the offer pool is restored)
- Active contracts (so in-progress contracts are restored)
- Recently finished contracts (so duplicate generation prevention works)

**Recommended approach**: Take a full contracts snapshot at each checkpoint/rewind-point, not just at individual contract events. This means serializing the entire `ContractSystem` state (all contracts + finished contracts + weights) into a single ConfigNode.

### What the ledger needs to provide

The `ContractsModule` currently tracks accept/complete/fail/cancel for funds/rep accounting. For PatchContracts, it needs to provide:
- The set of contract ConfigNode snapshots that should exist at the target UT
- Or: a "full system snapshot" taken at the beginning of each recording segment

### Minimal viable approach

For the first implementation, consider a simpler approach:
1. At recording start, snapshot the entire ContractSystem state
2. At rewind, restore that full snapshot
3. Then replay contract events (accept/complete/fail/cancel) that occurred between the snapshot and the rewind target UT by loading individual contract snapshots captured at each event

This avoids needing to compute intermediate contract states and leverages the existing per-accept snapshots.

## Risks and Open Questions

### Risk: GameEvents during restoration

`Contract.Load` does not fire GameEvents. However, `Register()` (called for Active contracts) subscribes parameters to events which could trigger parameter state checks on the next frame. Ensure the suppression flags (`SuppressResourceEvents`, `IsReplayingActions`) are active during and briefly after patching.

### Risk: UpdateDaemon interference

The `UpdateDaemon` runs every frame and may immediately start modifying the restored contract list (withdrawing offers, generating new contracts). Consider:
- Temporarily stopping the daemon during patching (it checks `updateDaemonRunning`)
- Or accepting that the daemon will adjust offers after patching (acceptable if offered contracts aren't critical)

### Risk: Contract ID collisions

`Contract.SetupID()` generates a deterministic ID from the contract's hash string + seed. If Parsek creates duplicate contracts (e.g., restoring a contract that already exists), IDs could collide. Clearing both lists before restoration prevents this.

### Risk: `OnLoad` failures in modded contracts

If a mod contract's `OnLoad` fails (e.g., references a body from a planet pack that was removed), `Contract.Load` catches the exception and returns the partially-loaded contract rather than null. The contract may be in a broken state. Parsek should log warnings for such cases but not abort the entire patching operation.

### Open question: Offered contract pool

Should Parsek restore the offered contract pool? If not, the player will see different offers after rewind. If yes, Parsek needs to snapshot all offered contracts, not just accepted ones. This is a UX decision -- players may not notice or care about different offers.

### Open question: Contract weights persistence

`ContractWeights` affects which contract types are more likely to be offered. Restoring weights would make the post-rewind contract generation more consistent with what the player experienced. However, weights are a minor factor and the implementation cost may not be justified.

### Open question: Contracts finished list size

The `contractsFinished` list can grow large over a long game. Parsek should consider whether to restore it in full or just the recent entries (KSP only checks the last `finishedContractIDCheck = 5` entries for duplicate prevention).
