# Game Actions & Resource System — Task Decomposition

Task breakdown for implementing `docs/parsek-game-actions-system-design.md`. Follows the ordering from `docs/dev/redesign-task-template.md`: data model → core mechanism → modules → integration → polish.

**Design doc:** `docs/parsek-game-actions-system-design.md` (1967 lines, 12 sections + appendix)
**Inventory:** `docs/dev/plans/game-actions-inventory.md`
**Deferred items:** `docs/dev/plans/game-actions-deferred.md`

---

## Phase 1: Foundation

### Task 1: Core Data Types and Action Schemas

**Overview:** Define all action schema types from the design doc as C# types. No behavior, no I/O — just types and serialization round-trips.

**New file:** `Source/Parsek/GameAction.cs` (~350 lines)

Types to create:
- `GameActionType` enum — `ScienceEarning`, `ScienceSpending`, `FundsEarning`, `FundsSpending`, `MilestoneAchievement`, `ContractAccept`, `ContractComplete`, `ContractFail`, `ContractCancel`, `ReputationEarning`, `ReputationPenalty`, `KerbalAssignment`, `KerbalHire`, `KerbalRescue`, `KerbalStandIn`, `FacilityUpgrade`, `FacilityDestruction`, `FacilityRepair`, `StrategyActivate`, `StrategyDeactivate`, `FundsInitial`
- `ScienceMethod` enum — `Transmitted`, `Recovered`
- `FundsSource` enum — `ContractComplete`, `ContractAdvance`, `Recovery`, `Milestone`, `Other`
- `FundsSpendingSource` enum — `VesselBuild`, `FacilityUpgrade`, `FacilityRepair`, `KerbalHire`, `ContractPenalty`, `Strategy`, `Other`
- `ReputationSource` enum — `ContractComplete`, `Milestone`, `Other`
- `ReputationPenaltySource` enum — `ContractFail`, `ContractDecline`, `KerbalDeath`, `Strategy`, `Other`
- `KerbalEndState` enum — `Recovered`, `Dead`, `MIA`, `Stranded`
- `GameAction` class — base with `ut`, `type`, `recordingId` (nullable), `sequence`
- Derived types or fields for each action schema (section 4.3, 4.4, 5.5, 6.4, 7.2, 8.4, 9.8, 10.2, 11.2)

**Serialization:** ConfigNode round-trip. Each action serializes to a `GAME_ACTION` ConfigNode with a `type` discriminator and schema-specific fields. Use `ToString("R", InvariantCulture)` for all floats/doubles.

**Tests:** `GameActionSerializationTests.cs` — round-trip every action type, verify all fields survive save/load. ~20 tests.

**Depends on:** Nothing.
**Enables:** Tasks 2-12.
**Done when:** All types compile, serialization round-trips pass.

---

### Task 2: Ledger File I/O

**Overview:** Implement the ledger file — persistence layer for all game actions. Follows the safe-write pattern from `RecordingStore`. Single file `saves/<save>/parsek/ledger.dat` containing all actions.

**New file:** `Source/Parsek/Ledger.cs` (~400 lines)

Functionality:
- `SaveToFile(string path, List<GameAction> actions)` — safe-write (.tmp + rename)
- `LoadFromFile(string path) → List<GameAction>` — parse all actions from file
- `Reconcile(List<GameAction> actions, HashSet<string> validRecordingIds, double maxUT)` — prune orphaned earnings and future spendings (design doc 2.3)
- Path utilities: `GetLedgerPath(string saveName)`

**Integration with ParsekScenario:** Add `ledgerPath` field to ScenarioModule save/load. Reference-only in .sfs, bulk data in external file.

**Tests:** `LedgerIOTests.cs` — save/load round-trip, reconciliation (prune orphaned, prune future), empty file, corrupt file handling. ~15 tests.

**Depends on:** Task 1 (action types).
**Enables:** Task 3.
**Done when:** Ledger file saves, loads, and reconciles correctly.

---

### Task 3: Recalculation Engine

**Overview:** The core recalculation walk from design doc section 1.8. Sorts all actions by (UT, type, sequence), dispatches to module handlers, produces derived state.

**New file:** `Source/Parsek/RecalculationEngine.cs` (~350 lines)

Functionality:
- `Recalculate(List<GameAction> actions) → RecalculationResult` — the unified walk
- Sort: UT ascending, earnings before spendings, then sequence
- Module interface: `IResourceModule` with `Reset()`, `ProcessAction(GameAction)`, `GetDerivedState()`
- Tier ordering: first-tier modules (Science, Milestones, Contracts, Kerbals) → strategy transform → second-tier modules (Funds, Reputation)
- `RecalculationResult` — aggregated derived state from all modules

The engine is pure computation — no KSP mutation, no file I/O.

**Tests:** `RecalculationEngineTests.cs` — sort order verification, tier ordering, empty walk, single-action walk. ~10 tests.

**Depends on:** Tasks 1-2.
**Enables:** Tasks 4-12 (modules register as handlers).
**Done when:** Engine sorts, dispatches, and produces results for empty and trivial action lists.

---

## Phase 2: Simple Modules (First-Tier)

### Task 4: Science Earnings Module

**Overview:** Science subject tracking, hard-cap walk, effective science computation. Design doc sections 4.1-4.3, 4.6.

**New file:** `Source/Parsek/ScienceModule.cs` (~200 lines)

Implements `IResourceModule`:
- `Reset()`: clear per-subject credited totals and running science
- `ProcessEarning(ScienceEarning)`: headroom = maxValue - creditedTotal, effectiveScience = min(awarded, headroom), update credited
- Per-subject state: `Dictionary<string, SubjectState>` with `creditedTotal` and `maxValue`
- Running science balance

**Tests:** `ScienceModuleTests.cs` — basic earning, retroactive priority (design doc 4.7 scenarios), cap hit, multiple collections within one recording, mixed transmit/recover. ~15 tests.

**Depends on:** Task 3 (engine interface).
**Enables:** Task 5 (science spending).
**Done when:** All verified scenarios from design doc 4.7 pass.

---

### Task 5: Science Spending and Reservation

**Overview:** Tech node unlock spending, science reservation system. Design doc sections 4.4-4.5.

**Extends:** `ScienceModule.cs` (~+100 lines)

Functionality:
- `ProcessSpending(ScienceSpending)`: check affordability, deduct from running science
- Reservation: `availableScience(ut) = sum(effective earnings up to ut) - sum(ALL spendings)`
- `GetAvailableScience(double ut)` for commit-time and KSC-time checks

**Tests:** Reservation blocks new spending (design doc 4.5 scenario), interleaved earnings/spendings (design doc 4.7 scenario 3). ~8 tests.

**Depends on:** Task 4.
**Enables:** Task 8 (funds uses same reservation pattern).
**Done when:** Reservation scenarios pass, spending blocked correctly.

---

### Task 6: Milestones Module

**Overview:** Once-ever flags, fund/rep flow to second-tier. Design doc section 7.

**New file:** `Source/Parsek/MilestonesModule.cs` (~120 lines)

Implements `IResourceModule`:
- Binary cap: each milestoneId credited exactly once
- Chronologically first recording gets `effective = true`
- `fundsAwarded` and `repAwarded` flow to Funds and Reputation modules when effective

**Tests:** `MilestonesModuleTests.cs` — basic achievement, retroactive priority shift (design doc 7.4), multiple milestones across recordings. ~8 tests.

**Depends on:** Task 3.
**Enables:** Task 8 (funds reads milestone effective flags).
**Done when:** All verified scenarios from design doc 7.4 pass.

---

### Task 7: Contracts Module

**Overview:** State machine, slot reservation (UT=0), once-ever completion, deadline generation. Design doc section 8. This is a complex first-tier module.

**New file:** `Source/Parsek/ContractsModule.cs` (~350 lines)

Functionality:
- Contract state tracking: `activeContracts` map, `creditedContracts` set
- Accept: slot consumed from UT=0
- Complete: once-ever (effective flag), rewards flow to Funds/Rep/Science
- Fail/Cancel: penalties flow, slot freed
- Deadline handling: walk generates derived `ContractFail` at deadline UT
- `availableSlots(ut)` for slot reservation check

**Tests:** `ContractsModuleTests.cs` — basic accept/complete, once-ever with retroactive priority (design doc 8.10), slot reservation across rewind, deadline failure generation. ~12 tests.

**Depends on:** Task 3.
**Enables:** Task 8 (contract fund/rep values), Task 9 (contract rep values).
**Done when:** All verified scenarios from design doc 8.10 pass.

---

## Phase 3: Second-Tier Modules

### Task 8: Funds Module

**Overview:** Seeded balance, all earning sources, spendings, reservation system. Design doc section 5.

**New file:** `Source/Parsek/FundsModule.cs` (~250 lines)

Implements `IResourceModule` (second-tier):
- `initialFunds` seed from save file
- Running balance: seed + earnings - spendings
- Earnings: milestone funds (check effective flag), contract funds, vessel recovery, other
- Spendings: vessel build, facility upgrade/repair, kerbal hire, contract penalties
- Reservation: `availableFunds(ut) = seed + earnings up to ut - ALL spendings`
- Defensive `affordable` flag on spendings

**Tests:** `FundsModuleTests.cs` — seeded balance, reservation blocks overspending (design doc 5.12), interleaved facility/hire spendings, new recording expands budget. ~12 tests.

**Depends on:** Tasks 5 (reservation pattern), 6 (milestone effective funds), 7 (contract funds).
**Enables:** Task 15 (patching).
**Done when:** All verified scenarios from design doc 5.12 pass.

---

### Task 9: Reputation Module

**Overview:** Curve simulation, nominal→effective conversion. Design doc section 6.

**New file:** `Source/Parsek/ReputationModule.cs` (~200 lines)

Implements `IResourceModule` (second-tier):
- Running rep starting at 0
- `applyGainCurve(nominal, currentRep)` — replicate KSP's gain curve
- `applyLossCurve(nominal, currentRep)` — replicate KSP's loss curve
- Reads effective flags from milestones and contracts
- Order-dependent totals (expected, documented in design doc 6.8)

**Note:** Requires decompilation investigation for exact curve (D1). Initial implementation can use a reasonable approximation; exact formula is pluggable.

**Tests:** `ReputationModuleTests.cs` — basic gain, retroactive reordering (design doc 6.7), penalty application, curve diminishing at high rep. ~8 tests.

**Depends on:** Tasks 6, 7 (milestone/contract effective rep values).
**Enables:** Task 15 (patching).
**Done when:** Verified scenario from design doc 6.7 passes (with approximate curve).

---

## Phase 4: Complex Modules

### Task 10: Kerbals Module

**Overview:** Expand existing `CrewReservationManager` into full kerbals module. Design doc section 9.

**Extends:** `CrewReservationManager.cs` and/or new `KerbalsModule.cs` (~400 lines)

Functionality:
- UT=0 reservation per kerbal (continuous block, no gaps)
- End states: RECOVERED (temporary), STRANDED (open-ended), DEAD/MIA (permanent)
- Replacement chains: per-slot, ordered by generation
- Stand-in generation: same class, randomized attributes, 0 XP, free
- Retired pool: displaced stand-ins that were used in recordings
- XP accumulation: walk assignments, sum xpGained
- Hiring: `KerbalHire` spending action, funds cost
- Rescue: `KerbalRescue` closes stranded reservation
- Dismissal protection: Parsek-managed kerbals cannot be dismissed

**Tests:** `KerbalsModuleTests.cs` — all 8 verified scenarios from design doc 9.11, chain depth 3, stand-in dies within chain, permanent loss, multi-crew mission. ~20 tests.

**Depends on:** Tasks 3, 8 (hiring is a funds spending).
**Enables:** Task 15 (roster patching).
**Done when:** All verified scenarios from design doc 9.11 pass.

---

### Task 11: Facilities Module

**Overview:** Upgrade/destroy/repair lifecycle, visual state during warp. Design doc section 10.

**New file:** `Source/Parsek/FacilitiesModule.cs` (~200 lines)

Functionality:
- Action processing: upgrade (spending), destruction (recording-associated), repair (spending)
- Derived facility level at any UT: walk actions to determine level/destroyed state
- Visual state management: real-time update during warp (like ghost playback)
- Funds flow: upgrade and repair costs into Funds module

**Tests:** `FacilitiesModuleTests.cs` — upgrade sequence, destruction→repair window, funds accounting. ~8 tests.

**Depends on:** Tasks 3, 8 (facility costs are fund spendings).
**Enables:** Task 15 (facility level patching), Task 16 (visual state during warp).
**Done when:** Facility state derivable from action walk, costs flow to funds correctly.

---

### Task 12: Strategies Module

**Overview:** Time-windowed transforms on contract rewards. Design doc section 11. Lowest priority module.

**New file:** `Source/Parsek/StrategiesModule.cs` (~250 lines)

Functionality:
- Active window tracking: activate/deactivate actions
- UT=0 slot reservation (like contracts and kerbals)
- Transform application: during walk, between first-tier resolution and second-tier crediting
- Only transforms contract rewards (not milestones, not science, not recovery)
- Setup cost: spending in source resource on activation

**Tests:** `StrategiesModuleTests.cs` — basic transform (design doc 11.6 scenario 1), retroactive commit outside window (scenario 2), reservation blocks new strategy (scenario 3). ~8 tests.

**Depends on:** Tasks 7 (contract effective flags), 8 (funds), 9 (reputation).
**Enables:** None (final module).
**Done when:** All verified scenarios from design doc 11.6 pass.

---

## Phase 5: Integration

### Task 13: Flight Event Capture

**Overview:** Extend `GameStateRecorder` to capture ledger-relevant events during flight, writing rich data to the recording sidecar.

**Modifies:** `GameStateRecorder.cs`, `FlightRecorder.cs`

Functionality:
- Science: already captured (`OnScienceReceived`) — extend to capture full schema fields (`subjectId`, `scienceAwarded`, `method`, `transmitScalar`, `subjectMaxValue`)
- Milestones: subscribe to `GameEvents.OnProgressComplete`, capture `milestoneId`, `fundsAwarded`, `repAwarded`
- Contracts: subscribe to contract completion events, capture `contractId`, rewards
- Vessel cost: capture at launch (already exists as resource delta, formalize)
- Recovery: capture `OnVesselRecoveryProcessing` recovery value
- Kerbal end states: capture death (`onCrewKilled`), EVA, recovery status

**Tests:** Event capture logging tests — verify correct log lines for each captured event type. ~10 tests.

**Depends on:** Task 1 (action schema types).
**Enables:** Task 14 (commit extraction reads sidecar data).
**Done when:** All relevant KSP events are captured and written to sidecar.

---

### Task 14: Commit Path — Sidecar to Ledger Extraction

**Overview:** When a recording is committed, extract earning actions from the sidecar and append to the ledger. Design doc section 2.2.

**Modifies:** `ParsekFlight.cs` (commit path), `Ledger.cs`

Functionality:
- On commit: read sidecar, extract schema fields, create `GameAction` entries
- Append to ledger file
- Trigger recalculation
- Vessel build cost: create `FundsSpending` with `VesselBuild` source at launch UT
- Block commit if resources insufficient (reservation check)

**Tests:** `CommitExtractionTests.cs` — extract science earnings from mock sidecar, extract milestone, extract vessel cost. ~10 tests.

**Depends on:** Tasks 2 (ledger I/O), 13 (captured data in sidecar).
**Enables:** Task 15 (recalculation produces derived state to patch).
**Done when:** Commit adds earning actions to ledger and triggers recalculation.

---

### Task 15: KSP State Patching

**Overview:** After recalculation, write derived state into KSP singletons. Design doc section 3.1-3.2. Replaces existing `ResourceApplicator` point-by-point delta system.

**New file:** `Source/Parsek/KspStatePatcher.cs` (~350 lines)

Functionality:
- Phase 1 (recalculate): pure computation, produces derived values per module
- Phase 2 (patch): write into KSP state
  - Science: `ResearchAndDevelopment.Instance` — per-subject totals, available science balance, tech tree unlock state
  - Funds: `Funding.Instance.Funds` — set to `availableFunds(currentUT)`
  - Reputation: `Reputation.Instance.reputation` — set to `runningRep`
  - Milestones: `ProgressTracking.Instance` — set achieved flags
  - Kerbals: `HighLogic.CurrentGame.CrewRoster` — roster entries, XP, status
  - Facilities: `ScenarioUpgradeableFacilities` — building levels
- Use `SuppressResourceEvents` during patching
- Patch triggers: commit, rewind, warp exit, KSP load

**Tests:** Patching is hard to unit-test (KSP singletons). Focus on verifying derived state computation. Manual in-game testing for actual patching.

**Depends on:** Tasks 4-12 (all modules produce derived state).
**Enables:** Tasks 16, 17.
**Done when:** Derived state is correctly computed; KSP patching calls are structured.

---

### Task 16: Warp Cycle Integration

**Overview:** Event queue during warp, visual updates (facility state), recalculation on exit. Design doc section 3.3.

**Modifies:** `ParsekFlight.cs`, `FacilitiesModule.cs`

Functionality:
- During warp: collect game action UTs crossed, update facility visuals in real-time
- On warp exit: full recalculation from UT=0, patch all KSP state
- Single recalculation per warp session (not per event)
- Player feedback summary on exit ("During warp: N recordings completed, X science earned")

**Depends on:** Tasks 11, 15.
**Enables:** Task 17 (polish).
**Done when:** Warp exit triggers recalculation and patching.

---

### Task 17: Rewind Integration

**Overview:** Post-quickload recalculation and patching. Design doc section 3.4.

**Modifies:** `ParsekScenario.cs`, `ParsekFlight.cs`

Functionality:
- After KSP loads quicksave: full recalculation, patch KSP state
- KSP load: reconcile ledger (prune), recalculate, patch
- Existing rewind path already resets resources (`ResourceApplicator.CorrectToBaseline`) — replace with ledger-based recalculation

**Depends on:** Tasks 2, 15.
**Enables:** None (integration complete).
**Done when:** Rewind produces correct state via ledger recalculation.

---

## Phase 6: Polish

### Task 18: KSC Spending Actions

**Overview:** Implement KSC-time spending actions — tech node unlock, facility upgrade, kerbal hire. These happen outside flight recordings.

**Modifies:** Need investigation — how does Parsek currently intercept KSC actions? May need new `GameEvents` subscriptions or Harmony patches.

Functionality:
- Detect tech node unlock: `GameEvents.OnTechnologyResearched`
- Detect facility upgrade: facility event subscription
- Detect kerbal hire: `GameEvents.onKerbalAdded` (verify API)
- Write spending actions directly to ledger (no sidecar)
- Check reservation before allowing (block if insufficient)

**Depends on:** Tasks 2, 4-8.
**Done when:** KSC spendings are recorded in ledger and reservation blocks overspending.

---

### Task 19: UI Integration

**Overview:** Display available balances, resource breakdowns, action history in Parsek's UI.

**Modifies:** `ParsekUI.cs`

Functionality:
- Replace current resource display with ledger-derived available balances
- Breakdown panel: earned, reserved, available per resource
- Action history: scrollable list of game actions with details

**Depends on:** All modules.
**Done when:** UI shows correct ledger-derived values.

---

### Task 20: Logging Audit and Test Coverage

**Overview:** Ensure every decision point in the new system has diagnostic logging. Expand test coverage to all verified scenarios from the design doc.

**Tests:** Log assertion tests for all module decision points, edge case tests for all design doc edge cases.

**Depends on:** All tasks.
**Done when:** KSP.log can reconstruct full recalculation walk. All design doc verified scenarios have corresponding tests.

---

## Summary

| Phase | Tasks | Description |
|-------|-------|-------------|
| 1. Foundation | 1-3 | Data types, ledger I/O, recalculation engine |
| 2. Simple Modules | 4-7 | Science, milestones, contracts (first-tier) |
| 3. Second-Tier | 8-9 | Funds, reputation (depend on first-tier) |
| 4. Complex Modules | 10-12 | Kerbals, facilities, strategies |
| 5. Integration | 13-17 | Event capture, commit, patching, warp, rewind |
| 6. Polish | 18-20 | KSC spendings, UI, logging/tests |

**Parallelization opportunities:**
- Tasks 4, 6, 7 are independent first-tier modules — can run in parallel
- Tasks 8, 9 can run in parallel (both second-tier, independent)
- Tasks 10, 11, 12 can run in parallel (independent complex modules)
- Tasks 13, 14 are sequential
- Tasks 18, 19, 20 can mostly run in parallel

**Estimated new tests:** ~140-160 tests across all modules
**Estimated new code:** ~3500-4500 lines (excluding tests)
