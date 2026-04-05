# Refactor-3 Inventory

**Date:** 2026-04-05. Fresh analysis from `origin/main` at `9f5b48d` (v0.6.1).
**Pass 1 completed:** 2026-04-05. 48 sub-methods extracted across 9 files. 0 logic changes, 4766 tests pass.

**Total source:** 68,282 lines across 60+ .cs files (excluding Tests/, InGameTests/, obj/, bin/).

**Scope:** The entire `GameActions/` system (6,485 lines, 15 files) plus all god classes (ParsekFlight, FlightRecorder, GhostVisualBuilder, GhostPlaybackEngine, GhostPlaybackLogic, ParsekUI, ParsekScenario, RecordingStore, VesselSpawner).

---

## File Size Summary

### Tier 1 — Large (>2000 lines)

| File | Lines | Since v0.5.2 | Notes |
|------|-------|-------------|-------|
| ParsekFlight.cs | 8,765 | +1673 changed | **Pass1-Done — 14 extractions from 10 methods** |
| GhostVisualBuilder.cs | 6,484 | +231 changed | **Pass1-Done — 7 extractions (AddPartVisuals, ReentryFx, PuffFx)** |
| FlightRecorder.cs | 5,267 | +336 changed | **Pass1-Done — 10 extractions (OnPhysicsFrame, StartRecording, OnVesselGoOnRails)** |
| ParsekUI.cs | 4,773 | +1536 changed | **Pass1-Done — 9 extractions from 4 Draw methods** |
| RecordingStore.cs | 2,958 | +715 changed | **Pass1-Done — 4 extractions from CommitTree** |
| BackgroundRecorder.cs | 2,788 | +42 changed | Pass1-Done — no extraction needed |
| GhostPlaybackLogic.cs | 2,589 | +369 changed | **Pass1-Done — 1 extraction (ApplyParachuteDeployedEvent)** |
| ParsekScenario.cs | 2,248 | +740 changed | **Pass1-Done — 8 extractions from OnSave/OnLoad** |

### Tier 2 — Medium-large (800-2000 lines)

| File | Lines | Since v0.5.2 | Notes |
|------|-------|-------------|-------|
| GhostPlaybackEngine.cs | 1,770 | +273 changed | **Pass1-Done — 2 extractions (loop pause, overlap iteration)** |
| VesselSpawner.cs | 1,473 | +421 changed | **Pass1-Done — 2 extractions from SpawnOrRecoverIfTooClose** |
| GhostMapPresence.cs | 1,211 | +1165 changed | **Pass1-Done — clean** |
| RecordingTree.cs | 1,013 | +72 changed | Pass1-Done — no extraction needed |
| EngineFxBuilder.cs | 988 | +34 changed | Stable (out of scope) |
| GameStateRecorder.cs | 975 | +269 changed | **Pass1-Done — clean event handler pattern** |
| **GameActions/LedgerOrchestrator.cs** | 900 | **+900 new** | **Pass1-Done — well-structured, comprehensive logging** |
| ParsekKSC.cs | 897 | +201 changed | Pass1-Done — no extraction needed |
| **GameActions/GameAction.cs** | 895 | **+895 new** | **Pass1-Done — well-structured, 37 tests exist, no extraction needed** |
| ParsekPlaybackPolicy.cs | 892 | +730 changed | **Pass1-Done — clean** |
| **KerbalsModule.cs** | 892 | **+892 new** | **Pass1-Done — clean** |
| RecordingOptimizer.cs | 863 | **+863 new** | **Pass1-Done — pure algorithmic, clean** |
| **GameActions/KspStatePatcher.cs** | 777 | **+777 new** | **Pass1-Done — well-structured, comprehensive logging** |
| ChainSegmentManager.cs | 714 | stable | Stable |
| VesselGhoster.cs | 709 | +2 changed | Stable |
| GameStateStore.cs | 709 | +15 changed | Stable |
| TrajectoryMath.cs | 702 | +31 changed | Stable |
| GhostChainWalker.cs | 700 | +43 changed | Stable |
| PartStateSeeder.cs | 696 | +33 changed | Stable |

### Tier 3 — Medium (400-800 lines)

| File | Lines | Since v0.5.2 | Notes |
|------|-------|-------------|-------|
| CrewReservationManager.cs | 686 | +197 changed | Grew via kerbal system |
| TimeJumpManager.cs | 632 | +57 changed | Minor growth |
| MergeDialog.cs | 595 | -365 changed | Shrunk (simplified) |
| SessionMerger.cs | 526 | +46 changed | Minor growth |
| **GameActions/GameStateEventConverter.cs** | 514 | **+514 new** | **Pass1-Done — clean** |
| **GameActions/FundsModule.cs** | 513 | **+513 new** | **Pass1-Done — clean** |
| **GameActions/Ledger.cs** | 506 | **+506 new** | **Pass1-Done — Reconcile 115 lines but coherent single loop** |
| SpawnCollisionDetector.cs | 489 | stable | Stable |
| MilestoneStore.cs | 474 | stable | Stable |
| ResourceBudget.cs | 433 | +23 changed | Minor growth |
| GhostCommNetRelay.cs | 427 | +68 changed | Minor growth |
| SelectiveSpawnUI.cs | 392 | +203 changed | Grew |

### Tier 4 — Small (<400 lines, GameActions/ only)

| File | Lines | Notes |
|------|-------|-------|
| GameActions/ScienceModule.cs | 386 | Pass1-Done — clean |
| GameActions/ReputationModule.cs | 378 | Pass1-Done — clean |
| GameActions/RecalculationEngine.cs | 351 | Pass1-Done — clean |
| GameActions/ContractsModule.cs | 327 | Pass1-Done — clean |
| GameActions/StrategiesModule.cs | 298 | Pass1-Done — clean |
| GameActions/GameActionDisplay.cs | 246 | Pass1-Done — clean |
| GameActions/FacilitiesModule.cs | 239 | Pass1-Done — clean |
| GameActions/MilestonesModule.cs | 100 | Pass1-Done — clean |
| GameActions/IResourceModule.cs | 55 | Pass1-Done — clean |

---

## Priority Area 1: GameActions/ System (6,485 lines, all new)

The entire ledger-based game actions system was built since v0.5.2. It replaced the old `ActionReplay.cs` + `ResourceApplicator.cs` system. It has never been through a refactoring pass.

### Architecture

```
ParsekScenario (lifecycle hooks)
  └─ LedgerOrchestrator (central coordinator)
       ├─ Ledger (action storage, file I/O, reconciliation)
       ├─ GameStateEventConverter (old events → GameAction conversion)
       ├─ RecalculationEngine (tiered module dispatch)
       │    ├─ Tier 1: ScienceModule, FundsModule, ReputationModule, MilestonesModule
       │    ├─ Transform: StrategiesModule
       │    ├─ Tier 2: ContractsModule, KerbalsModule
       │    └─ Parallel: FacilitiesModule
       └─ KspStatePatcher (apply recalculated state to KSP)

GameStateRecorder (captures KSP events → GameStateEvent)
  └─ GameStateStore (stores raw events, baselines)
       └─ GameStateEventConverter (converts to GameAction for ledger)
```

### Key Issues

**LedgerOrchestrator.cs (900 lines) — God Object**
- 6+ distinct responsibilities: module lifecycle, recording commit pipeline, save/load orchestration, data migration, seeding, KSC event handling
- 13 static mutable fields (8 module instances + 3 seed flags + counter + initialized)
- 33 methods total: highest method count in GameActions/
- `CreateVesselCostActions` ~66 lines, `CreateKerbalAssignmentActions` ~36 lines, `OnRecordingCommitted` ~44 lines, `MigrateKerbalAssignments` ~44 lines
- `OnLoad` is only ~22 lines (delegates to Ledger.LoadFromFile); `OnKspLoad` is ~24 lines (delegates to migration + recalculate)

**GameAction.cs (895 lines) — Massive Union Type**
- 85+ fields with type discriminator determining which subset is valid
- `SerializeInto` — 23-case switch (~72 lines)
- `DeserializeFrom` — 23-case switch (~98 lines)
- 72+ private per-type serialize/deserialize helpers

**KspStatePatcher.cs (777 lines) — Large Patch Methods**
- `PatchContracts` ~180 lines — complex contract state restoration with nested loops
- `PatchFacilities` ~150 lines — building level + destruction state restoration
- `PatchScience` ~70 lines — per-subject science cap restoration
- All methods tightly coupled to KSP API singletons

**GameStateEventConverter.cs (514 lines) — Switch-Heavy**
- `ConvertEvent` — 24-case switch dispatching to per-type converters
- Each converter 5-40 lines, all extracting detail fields from semicolon-separated strings

**FundsModule.cs (513 lines) — Largest Resource Module**
- `ProcessAction` — 9-case switch (~60 lines)
- Pattern duplicated across ScienceModule (4-case), ReputationModule (7-case), ContractsModule (8-case)

**Ledger.cs (506 lines) — Reconciliation Complexity**
- `Reconcile` ~100 lines — 3-category pruning with different rules per category
- Seeding methods (SeedInitialFunds/Science/Reputation) follow identical patterns
- File I/O mixed with in-memory collection management

**GameStateRecorder.cs (975 lines) — Mixed Concerns**
- Static suppression flags (`SuppressCrewEvents`, `SuppressResourceEvents`, `IsReplayingActions`) used globally
- Crew debouncing via `PendingCrewEvent` dictionary
- Facility polling via cached level/intact dictionaries
- Contract completion handler ~80 lines with nested if-chains

---

## Priority Area 2: Other Files That Grew Significantly

### GhostMapPresence.cs (1,211 lines — almost entirely new)
ProtoVessel lifecycle for tracking station ghost presence. Built across many commits with multiple revert/retry cycles for state-vector ghosts.

### ParsekPlaybackPolicy.cs (892 lines — mostly new)
Event subscriber for ghost playback decisions. Grew from 192 lines at v0.5.2 to 892.

### RecordingOptimizer.cs (863 lines — entirely new)
Recording compression: point simplification, temporal deduplication, tail trimming. Pure algorithmic code.

### VesselSpawner.cs (1,426 lines — +421)
Grew via identity regeneration, crew filtering, snapshot backup features.

### ParsekUI.cs (4,736 lines — +1536)
Grew via test runner UI, spawn control window, actions table improvements.

---

## Duplicated Patterns Identified

1. **Safe-write file I/O** — `.tmp` + rename pattern appears in Ledger.cs, GameStateStore.cs, MilestoneStore.cs, RecordingStore.cs. Same ~10-line pattern each time.

2. **Detail field parsing** — `ExtractDetailField(detail, "key")` for semicolon-separated key=value strings used across GameStateEventConverter, GameActionDisplay, ResourceBudget, GameStateEventDisplay.

3. **Seed-check-update pattern** — `SeedInitialFunds`/`SeedInitialScience`/`SeedInitialReputation` in Ledger.cs follow identical "already exists? stale zero? update : create" logic (~25 lines each).

4. **ComputeTotalSpendings** — Identical walk-and-sum pattern in FundsModule, ScienceModule (and similar in ReputationModule).

5. **Suppression flag try/finally** — `GameStateRecorder.SuppressCrewEvents = true; try { ... } finally { = false; }` pattern in KspStatePatcher and CrewReservationManager.

---

## Static Mutable State Inventory (Game Actions Area)

| File | Fields | Count |
|------|--------|-------|
| LedgerOrchestrator.cs | 8 modules + 3 seed flags + counter + initialized | 13 |
| GameStateStore.cs | events, contractSnapshots, baselines, committedScienceSubjects, originalScienceValues, initialLoadDone, lastSaveFolder, SuppressLogging | 8 |
| GameStateRecorder.cs | SuppressCrewEvents, SuppressResourceEvents, IsReplayingActions, PendingScienceSubjects | 4 |
| MilestoneStore.cs | milestones, initialLoadDone, lastSaveFolder, CurrentEpoch, SuppressLogging | 5 |
| RecalculationEngine.cs | firstTierModules, secondTierModules, strategyTransform, facilitiesModule | 4 |
| Ledger.cs | actions | 1 |
| ResourceBudget.cs | cachedBudget, budgetDirty | 2 |
| CrewReservationManager.cs | crewReplacements | 1 |
| **Total** | | **38** |

---

## Long Methods (>40 lines)

### GameActions/KspStatePatcher.cs (777 lines)

| Method | ~Lines | Description |
|--------|--------|-------------|
| PatchContracts | 179 | Unregisters all contracts, clears lists, rebuilds from snapshots, fires contracts-loaded event |
| PatchProgressNodeTree | 75 | Recursively patches ProgressTree nodes; tries qualified ID then bare ID fallback |
| PatchDestructionState | 71 | Collects destructible buildings, syncs destroyed/repaired state with module |
| PatchFacilities | 69 | Patches facility levels and destruction state across all upgraded facilities |
| PatchMilestones | 58 | Patches ProgressNode achievement tree via reflection; backward compat for body-specific nodes |
| PatchPerSubjectScience | 52 | Patches per-subject science totals and scientific value (diminishing returns) |

### GameActions/GameAction.cs (895 lines)

| Method | ~Lines | Description |
|--------|--------|-------------|
| DeserializeFrom | 102 | 23-case type switch with forward-compatible field parsing |
| SerializeInto | 84 | 23-case type switch; delegates to per-type serialize helpers |

### GameActions/Ledger.cs (506 lines)

| Method | ~Lines | Description |
|--------|--------|-------------|
| Reconcile | 115 | 3-category pruning (earnings vs spendings vs other) with different rules per category |
| LoadFromFile | 74 | File I/O with version check, action deserialization, parse error tracking |

### GameActions/RecalculationEngine.cs (351 lines)

| Method | ~Lines | Description |
|--------|--------|-------------|
| Recalculate | 91 | Sorts actions, resets modules, pre-pass, walks all actions with tiered dispatch, post-walk |
| RegisterModule | 63 | Registers module at tier; validates against duplicates |

### GameActions/LedgerOrchestrator.cs (900 lines)

| Method | ~Lines | Description |
|--------|--------|-------------|
| CreateVesselCostActions | 66 | Creates FundsSpending (build) and FundsEarning (recovery) from recording resource data |
| MigrateKerbalAssignments | 44 | Ensures all committed recordings have KerbalAssignment actions; old-save migration |
| OnRecordingCommitted | 44 | Full pipeline: convert → deduplicate → ledger → recalculate → patch |
| CreateKerbalAssignmentActions | 36 | Generates KerbalAssignment/KerbalRescue actions from vessel crew data |
| OnKspLoad | 24 | Reconcile + migrate old events + migrate kerbals + recalculate |
| OnLoad | 22 | Reset seed flags + load ledger from file |

### GameActions/GameStateEventConverter.cs (514 lines)

| Method | ~Lines | Description |
|--------|--------|-------------|
| ConvertEvent | 57 | 23-case dispatch to per-type conversion helpers |
| ConvertContractAccepted | 51 | Parses structured or legacy contract detail format; extracts deadline/penalties |
| ConvertEvents | 45 | Batch conversion with UT filtering and conversion stats |

### GameActions/FundsModule.cs (513 lines)

| Method | ~Lines | Description |
|--------|--------|-------------|
| ComputeTotalSpendings | 53 | Pre-pass: sums all fund spending costs (direct + penalties + facility + hire + strategy) |
| ProcessAction | 46 | 10-case dispatch switch delegating to type-specific processors |

### GameActions/ContractsModule.cs (327 lines)

| Method | ~Lines | Description |
|--------|--------|-------------|
| PrePass | 67 | Scans for unresolved contracts with expired deadlines; injects synthetic ContractFail actions |

### GameActions/StrategiesModule.cs (298 lines)

| Method | ~Lines | Description |
|--------|--------|-------------|
| TransformContractReward | 66 | Diverts commitment% of effective contract rewards to target resource for active strategies |

### GameActions/ScienceModule.cs (386 lines)

| Method | ~Lines | Description |
|--------|--------|-------------|
| ProcessEarning | 61 | Applies subject hard cap; computes effective science; updates credited total |

### GameStateRecorder.cs (975 lines)

| Method | ~Lines | Description |
|--------|--------|-------------|
| Subscribe | 100+ | Subscribes to 13+ GameEvents (contracts, tech, crew, resources, science) |
| OnScienceReceived | 60+ | Captures pending science subjects; deduplicates by subject; tracks transmission scalar |
| OnFacilityUpgraded | 50+ | Polls facility levels, detects changes, records upgrade+destruction events |

### KerbalsModule.cs (892 lines)

| Method | ~Lines | Description |
|--------|--------|-------------|
| ProcessAction | 50+ | Dispatch switch for kerbal-related action types; manages reservations and end states |

### GhostMapPresence.cs (1,211 lines)

| Method | ~Lines | Description |
|--------|--------|-------------|
| CreateMapVessel | 60+ | Creates lightweight ProtoVessel for tracking station; initializes orbit |
| UpdateOrbit | 50+ | Updates ghost ProtoVessel orbit from terminal data; handles inclination wrapping |

### VesselGhoster.cs (709 lines)

| Method | ~Lines | Description |
|--------|--------|-------------|
| TryBuildGhost | 70+ | Builds ghost mesh from vessel snapshot; creates parts, applies physics state |
| GhostVessel | 60+ | Snapshots real vessel, despawns, creates ghost GO with recovery path on failure |

---

## Extraction Candidates

### Phase A — Method Extraction Within Files (no cross-file changes)

These are long methods that can be broken into smaller methods within the same file. Strictly structural — no logic changes.

**LedgerOrchestrator.cs:**
- `CreateVesselCostActions` (66 lines) → extract build cost helper + recovery cost helper
- `OnRecordingCommitted` (44 lines) — acceptable length but logging could be improved
- `MigrateKerbalAssignments` (44 lines) → extract per-recording loop body
- `CreateKerbalAssignmentActions` (36 lines) — borderline; review for logging gaps
- Smaller methods (`OnLoad` 22 lines, `OnKspLoad` 24 lines) — no extraction needed, check logging

**GameAction.cs:**
- `SerializeInto` (72 lines, 23-case switch) → extract per-type `SerializeScience()`, `SerializeContract()`, etc.
- `DeserializeFrom` (98 lines, 23-case switch) → extract per-type `DeserializeScience()`, `DeserializeContract()`, etc.

**KspStatePatcher.cs:**
- `PatchContracts` (~180 lines) → extract `FindMatchingContract()`, `RestoreContractState()`, `RemoveStaleContracts()`
- `PatchFacilities` (~150 lines) → extract `PatchFacilityLevels()`, `PatchBuildingDestruction()`
- `PatchScience` (~70 lines) → extract `ApplySubjectCap()`

**GameStateEventConverter.cs:**
- `ConvertEvent` (50 lines, 24-case switch) — cases are already delegating to per-type helpers, so the switch itself is acceptable. But the per-type helpers could be grouped by domain.

**Ledger.cs:**
- `Reconcile` (~100 lines) → extract `PruneEarnings()`, `PruneSpendings()`, `PruneOther()`
- Seed methods (3x ~25 lines) → extract shared `SeedOrUpdate(type, value, label)` template

**FundsModule.cs / ScienceModule.cs / ContractsModule.cs:**
- Large `ProcessAction` switches — each case is ~10-20 lines delegating to helpers. Acceptable structure for now but could extract grouped handlers.

**GameStateRecorder.cs:**
- `OnContractCompleted` (~80 lines) → extract `CaptureContractRewards()`, `BuildContractDetail()`
- `OnKerbalStatusChange` (~50 lines) → extract `DebouncedCrewEvent()`

**RecordingOptimizer.cs:**
- Survey needed for long methods (new file, 863 lines)

**GhostMapPresence.cs:**
- Survey needed for long methods (1,211 lines, mostly new)

### Phase B — Deduplication (cross-method, same file)

- **Ledger.cs seed methods** — 3 identical patterns → shared `SeedOrUpdateInitial(GameActionType, double, string)`
- **GameAction.cs serialize/deserialize** — per-type methods follow repetitive patterns; not mechanical dedup but could use helper for common field reading

### Phase C — Cross-File Structural (later, after A+B)

Potential candidates (to be confirmed after Phase A):
- **LedgerOrchestrator decomposition** — split migration logic, seeding logic, KSC event handling into separate classes
- **KspStatePatcher decomposition** — per-resource patchers into separate files
- **Suppression context** — shared `IDisposable` guard for try/finally suppression patterns

---

## Processing Plan

### Pass 1 — Method Extraction + Logging + Tests

Follow the established refactor process: extract logical units into well-named methods, verify comprehensive logging, add tests where possible. **No logic changes.**

**Order:**

| # | File | Lines | Priority | Rationale |
|---|------|-------|----------|-----------|
| 1 | LedgerOrchestrator.cs | 900 | **Canary** | God object, highest density of extraction candidates |
| 2 | GameAction.cs | 895 | Tier 1 | Large switch statements, many per-type helpers |
| 3 | KspStatePatcher.cs | 777 | Tier 1 | 3 large methods (PatchContracts, PatchFacilities, PatchScience) |
| 4 | GameStateRecorder.cs | 975 | Tier 1 | Mixed concerns, long event handlers |
| 5 | GameStateEventConverter.cs | 514 | Tier 2 | Switch-heavy but mostly delegating |
| 6 | FundsModule.cs | 513 | Tier 2 | Largest resource module |
| 7 | Ledger.cs | 506 | Tier 2 | Reconcile complexity, seed dedup |
| 8 | KerbalsModule.cs | 892 | Tier 2 | New, complex crew inference |
| 9 | RecordingOptimizer.cs | 863 | Tier 2 | New, pure algorithmic |
| 10 | GhostMapPresence.cs | 1,211 | Tier 2 | New, large |
| 11 | ParsekPlaybackPolicy.cs | 892 | Tier 2 | Mostly new |
| 12 | ScienceModule + ReputationModule + ContractsModule + StrategiesModule | 1,389 | Tier 3 | Smaller modules, parallel |
| 13 | RecalculationEngine + FacilitiesModule + MilestonesModule + GameActionDisplay + GameStateStore + MilestoneStore | 2,128 | Tier 4 | Smaller files, scan for logging/tests |

### Pass 2 — Architecture Analysis (read-only)

After Pass 1, systematic analysis of:
- Cross-file dependency graph for GameActions/ system
- Static mutable state mutation sites
- Deduplication opportunities across files
- Split recommendations for Phase C

### Pass 3 — SOLID Restructuring (if warranted)

Based on Pass 2 findings. Likely candidates:
- LedgerOrchestrator decomposition
- KspStatePatcher per-resource split
- Suppression context utility

---

## Quality Gates

Same as previous refactors:
- `dotnet build` after every file change
- `dotnet test` after every build
- Orchestrator diff review for every change
- No logic changes — only structural reshuffling, logging additions, new unit tests
