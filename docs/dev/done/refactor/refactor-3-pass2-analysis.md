# Refactor-3 Pass 2 Analysis

**Date:** 2026-04-05. Read-only analysis of branch `refactor-3` after Pass 1 (48 extractions, 4766 tests pass).

---

## 1. Cross-File Dependency Graph

### GameActions/ Internal Dependencies

```
LedgerOrchestrator (HUB — orchestrates all others)
  ├── Ledger                    (storage: AddActions, Actions, SaveToFile, LoadFromFile, Reconcile, SeedInitial*)
  ├── RecalculationEngine       (dispatch: RegisterModule, Recalculate, ClearModules)
  ├── GameStateEventConverter   (conversion: ConvertEvents, ConvertScienceSubjects, ConvertEvent)
  ├── KspStatePatcher           (apply: PatchAll, PatchFacilities)
  └── Module instances (8x IResourceModule — registered via RecalculationEngine)

RecalculationEngine
  ├── IResourceModule interface (Reset, PrePass, ProcessAction, PostWalk — called on each module)
  └── Ledger (none — receives actions as parameter)

KspStatePatcher
  ├── GameStateRecorder         (writes: SuppressResourceEvents, IsReplayingActions)
  └── Module accessors          (reads: ScienceModule, FundsModule, etc. — passed as params)

GameStateEventConverter
  └── GameStateEventDisplay.ExtractDetailField (detail parsing utility)

Modules (FundsModule, ScienceModule, ReputationModule, ContractsModule,
         StrategiesModule, KerbalsModule, FacilitiesModule, MilestonesModule)
  └── GameAction type (read fields, no cross-module dependencies)
```

**Key insight:** The GameActions/ system is a clean layered architecture. LedgerOrchestrator is the sole entry point. No module calls another module. RecalculationEngine only knows about IResourceModule. This is well-structured — no decomposition needed within GameActions/ itself.

### External Callers into GameActions/

| External File | Calls | Target |
|---------------|-------|--------|
| **ParsekScenario** | `OnRecordingCommitted` (5x), `NotifyLedgerTreeCommitted` (3x), `RecalculateAndPatch` (3x), `OnKspLoad`, `OnSave`, `OnLoad`, `Kerbals.SaveSlots/LoadSlots` | LedgerOrchestrator |
| **ParsekFlight** | `OnRecordingCommitted` (4x), `NotifyLedgerTreeCommitted` (2x), `RecalculateAndPatch` (1x), `IsInitialized`, `HasFacilityActionsInRange`, `KspStatePatcher.PatchFacilities` (1x) | LedgerOrchestrator, KspStatePatcher |
| **ParsekUI** | `Kerbals.GetRetiredKerbals` (1x), `Ledger.Actions` (read-only for display) | LedgerOrchestrator, Ledger |
| **MergeDialog** | `OnRecordingCommitted` (1x) | LedgerOrchestrator |
| **ChainSegmentManager** | `OnRecordingCommitted` (1x) | LedgerOrchestrator |
| **GameStateRecorder** | `OnRecordingCommitted` (via pipeline) | LedgerOrchestrator |
| **Harmony Patches** (4x) | `SuppressCrewEvents`, `IsReplayingActions` | GameStateRecorder flags |

**Key insight:** ParsekScenario is the primary lifecycle driver (17 calls). ParsekFlight is the secondary driver (8 calls). The API surface is narrow: `OnRecordingCommitted`, `RecalculateAndPatch`, save/load hooks. No file outside Parsek needs refactoring to change GameActions/ internals.

**Notable bypass:** ParsekFlight calls `KspStatePatcher.PatchFacilities(LedgerOrchestrator.Facilities)` directly on warp-start (line 3255), bypassing LedgerOrchestrator. This is an optimization — only facility levels need patching during warp, not the full recalculate+patch pipeline. Acceptable as-is.

---

## 2. Static Mutable State Mutation Sites

### High-Traffic State (written by multiple files)

| Field | Owner | Writers | Readers |
|-------|-------|---------|---------|
| `GameStateRecorder.SuppressCrewEvents` | GameStateRecorder | **CrewReservationManager** (5x set+clear), **KerbalsModule** (1x), **ParsekFlight** (1x) | GameStateRecorder event handlers, KerbalDismissalPatch |
| `GameStateRecorder.SuppressResourceEvents` | GameStateRecorder | **KspStatePatcher** (1x set+clear) | GameStateRecorder event handlers |
| `GameStateRecorder.IsReplayingActions` | GameStateRecorder | **KspStatePatcher** (1x set+clear) | **Harmony patches** (FacilityUpgradePatch, TechResearchPatch, KerbalDismissalPatch) |

### Single-Owner State (written only by declaring file)

| Field Group | Owner | External Readers |
|-------------|-------|-----------------|
| LedgerOrchestrator: 8 module instances | LedgerOrchestrator.Initialize | Module accessors (Funds, Science, etc.) used by ParsekFlight (1x: PatchFacilities), ParsekUI (1x: Kerbals) |
| LedgerOrchestrator: 3 seed flags | LedgerOrchestrator.OnLoad/OnKspLoad | Internal only |
| LedgerOrchestrator: counter, initialized | LedgerOrchestrator | ParsekFlight (reads IsInitialized) |
| Ledger: actions list | Ledger.AddActions/LoadFromFile | LedgerOrchestrator, ParsekUI (display) |
| GameStateStore: 8 fields | GameStateStore methods | ParsekScenario (save/load), ParsekUI (counts), Harmony patches (SuppressLogging) |
| MilestoneStore: 5 fields | MilestoneStore methods | ParsekScenario, ParsekUI (counts), Harmony patches (SuppressLogging) |
| RecalculationEngine: 4 lists | RecalculationEngine.RegisterModule | Internal only |
| ResourceBudget: 2 fields | ResourceBudget methods | ParsekUI (budget display) |
| CrewReservationManager: crewReplacements | CrewReservationManager methods | ParsekScenario (save/load) |

### Analysis

**4 fields are mutated by multiple files:**
- `SuppressCrewEvents` — written by **3 files** (CrewReservationManager 5x, KerbalsModule 1x, ParsekFlight 1x)
- `SuppressResourceEvents` — written by **2 files** (KspStatePatcher 1x, ParsekFlight 2x)
- `IsReplayingActions` — written by **1 file** (KspStatePatcher), read by 4 files
- `PendingScienceSubjects` — mutated by **3 files** (GameStateRecorder adds, ParsekFlight/RecordingStore clear)

All suppression flags follow the same try/finally guard pattern (see Pattern 5 below). The remaining 34 fields are well-encapsulated — written only by their declaring class with narrow external read access.

**Notable finding:** `MilestoneStore.SuppressLogging` has no production readers or writers — appears to be dead code (test-only).

**Risk assessment:** The codebase's static state discipline is good. No god-field situation where 5+ files write the same field. The suppression flags are the primary concern, and their try/finally pattern prevents state corruption.

---

## 3. Duplicated Pattern Verification

### Pattern 1: Safe-write file I/O — CONFIRMED, WORTH DEDUPLICATING

Four independent `SafeWriteConfigNode(ConfigNode, string)` implementations:

| File | Lines | Dir Creation | Error Handling | Logging |
|------|-------|-------------|----------------|---------|
| Ledger.cs:470 | 34 | Yes | try/catch delete + try/catch move | `ParsekLog.Warn` |
| RecordingStore.cs:2906 | 26 | No | try/catch delete + try/catch move | `Log()` (old-style) |
| GameStateStore.cs:641 | 26 | No | try/catch delete + try/catch move | `ParsekLog.Warn` |
| MilestoneStore.cs:301 | 7 | No | **None** | None |

**Differences:**
- Ledger includes directory creation; others assume directory exists
- MilestoneStore has NO error handling (bare `File.Delete` + `File.Move`)
- RecordingStore uses old `Log()` instead of `ParsekLog.Warn`

**Recommendation:** Extract to `FileIOUtils.SafeWriteConfigNode(ConfigNode node, string path, string tag)` with the most robust pattern (Ledger's: dir creation + try/catch + ParsekLog.Warn). This fixes MilestoneStore's missing error handling as a side effect. ~30 lines shared, ~90 lines removed.

### Pattern 2: Detail field parsing — ALREADY CENTRALIZED, LOW VALUE

`GameStateEventDisplay.ExtractDetailField(string detail, string fieldName)` exists in `GameStateEvent.cs:255`. `GameStateEventConverter` already delegates to it (line 511). `GameActionDisplay` does NOT have its own copy.

**One outlier:** `ResourceBudget.ParseCostFromDetail` (line 399) uses ad-hoc `Split(';')` parsing instead of `ExtractDetailField`. This is ~15 lines that could call `ExtractDetailField("cost")` + `double.TryParse`, but the savings are trivial.

**Recommendation:** SKIP. The pattern is already centralized. The one outlier is not worth a cross-file change for ~5 lines saved.

### Pattern 3: Seed-check-update in Ledger.cs — REJECT

The three `SeedInitial*` methods (lines 343-454) are structurally similar but semantically different:

| Method | Input Type | Zero Check | Target Field |
|--------|-----------|------------|-------------|
| SeedInitialFunds | `double` | `== 0f` | `InitialFunds` |
| SeedInitialScience | `float` | `== 0f` | `InitialScience` |
| SeedInitialReputation | `float` | `Math.Abs() < 0.01f` | `InitialReputation` |

A shared helper would need 7+ parameters (type, value, getter, setter, zero-checker, action factory, name) — more complex than the duplication it removes. The reputation method uses tolerance-based zero detection while the others use exact comparison.

**Recommendation:** SKIP. Three 33-line methods with semantic differences are better than one 20-line generic helper with 7 parameters.

### Pattern 4: ComputeTotalSpendings — REJECT

**FundsModule** (lines 172-224, 53 lines): 5-case switch summing 5 different fields (`FundsSpent`, `FundsPenalty`, `FacilityCost`, `HireCost`, `SetupCost`), tracks 5 separate counters.

**ScienceModule** (lines 129-153, 25 lines): Single filter (`ScienceSpending`), single field (`Cost`), single counter.

These share only the name and the concept of "sum spendings." The implementations are fundamentally different — different action types, different fields, different logging. A shared abstraction would be forced and unreadable.

**Recommendation:** SKIP. Not actually duplicated — different domain logic behind the same name.

### Pattern 5: Suppression flag try/finally — CONFIRMED, SIGNIFICANT VALUE

**10 instances across 4 files** (not 6 as initially estimated):

| File | Flag | Instances |
|------|------|-----------|
| CrewReservationManager.cs | `SuppressCrewEvents` | 5 (ReserveSnapshotCrew, UnreserveCrewInSnapshot, ClearReplacements, RescueReservedCrewAfterEvaRemoval, RescueOrphanedCrew) |
| KerbalsModule.cs | `SuppressCrewEvents` | 1 (ApplyToRoster — spans 130+ lines) |
| ParsekFlight.cs | `SuppressCrewEvents` | 1 (ReserveCrewForLeaves) |
| ParsekFlight.cs | `SuppressResourceEvents` | 2 (TickStandaloneResourceDeltas, ApplyTreeLumpSum) |
| KspStatePatcher.cs | `SuppressResourceEvents` + `IsReplayingActions` | 1 (PatchAll — sets TWO flags) |

Pattern:
```csharp
GameStateRecorder.SuppressCrewEvents = true;
try { /* work */ }
finally { GameStateRecorder.SuppressCrewEvents = false; }
```

**Recommendation:** INCLUDE in Pass 3. 10 instances of mechanical boilerplate. An `IDisposable` guard struct eliminates the risk of forgetting `finally`:

```csharp
internal struct SuppressionGuard : IDisposable
{
    private readonly bool crew, resource, replay;
    internal static SuppressionGuard Crew() { ... }
    internal static SuppressionGuard Resources() { ... }
    internal static SuppressionGuard ResourcesAndReplay() { ... }
    public void Dispose() { /* clear flags */ }
}
```

Using `struct` avoids GC allocation. All 10 call sites become `using (SuppressionGuard.Crew()) { ... }`. The `ResourcesAndReplay()` factory handles KspStatePatcher's two-flag case cleanly.

---

## 4. Additional Candidates from Old Audit

### ParsekFlight Watch Mode (R3-4) — DEFER

**State:** 15 fields (lines 241-253), 553 lines in `#region Camera Follow`.
**Methods:** `HandleLoopCameraAction`, `HandleOverlapCameraAction`, `EnterWatchMode`, `ExitWatchMode`, `DrawWatchModeOverlay`, `ResetLoopPhaseForWatch`, `FindNextWatchTarget`, `TransferWatchToNextSegment`, `IsVesselSituationSafe`, `ComputeWatchIndexAfterDelete`, `HasActiveGhost`, `IsGhostWithinVisualRange`, `IsGhostOnSameBody`.

**Partial duplication:** `HandleLoopCameraAction` and `HandleOverlapCameraAction` share ~20 lines (`RetargetToNewGhost` case, `ExplosionHoldStart` case), differing only in log prefix and loop-specific `ExplosionHoldEnd` case.

**Why defer:** The camera follow code deeply depends on ParsekFlight instance state (`engine`, `ghostStates`, `FlightCamera.fetch`, `InputLockManager`, `loopPhaseOffsets`). Extracting to a `WatchModeController` class would require passing 10+ references. The duplication is minor (~20 lines). The 553-line region is already well-organized with clear method boundaries. Cost exceeds benefit for this refactor pass.

### ParsekFlight Dock/Undock (R3-5) — RESOLVED

**State:** 6 fields (lines 145-149, 181).
**Key methods:** `ClearDockUndockState` (8 lines), `RestartRecordingAfterDockUndock` (11 lines), `HandleDockUndockCommitRestart` (52 lines).

**Old audit concern:** "ClearDockUndockState 3-line block repeated 4x, RestartRecordingAfterDockUndock 6-line block repeated 4x."

**Current state:** Both `ClearDockUndockState` and `RestartRecordingAfterDockUndock` are **already extracted as methods** (lines 4045, 4061). `HandleDockUndockCommitRestart` calls them — no inlined duplication remains. This was resolved during Pass 1 or earlier development.

**Recommendation:** No action needed.

### ParsekUI Window Splitting (R3-1) — INCLUDE 3 WINDOWS

ParsekUI: 4,773 lines, 5 distinct windows + group picker popup.

| Window | Lines | Fields | Risk | Recommendation |
|--------|-------|--------|------|---------------|
| **GroupPickerUI** | 410 | 12 | Low | **INCLUDE** — self-contained popup lifecycle |
| **SpawnControlUI** | 183 | 12+ | Low | **INCLUDE** — independent window, own sort state |
| **ActionsWindowUI** | 643 | 8+ | Low | **INCLUDE** — independent window |
| TestRunnerUI | 276 | 8 | Medium | DEFER — tightly coupled to InGameTestRunner lifecycle |
| SettingsWindowUI | 353 | ~5 | Medium | DEFER — modifies shared ParsekSettings, many callbacks to ParsekUI |
| RecordingsTableUI | 1,101 | 30+ | High | DEFER — deeply coupled to flight state, sort/rename/expand shared with main window |

**Extraction yields:** GroupPicker (410) + SpawnControl (183) + ActionsWindow (643) = **1,236 lines** moved out of ParsekUI. ParsekUI drops from 4,773 → ~3,537 lines.

---

## 5. Concrete Pass 3 Plan

### Phase 3A — SafeWriteConfigNode Extraction (1 commit)

**New file:** `Source/Parsek/FileIOUtils.cs` (~35 lines)
```csharp
internal static class FileIOUtils
{
    internal static void SafeWriteConfigNode(ConfigNode node, string path, string tag) { ... }
}
```

**Modified files:** Ledger.cs, RecordingStore.cs, GameStateStore.cs, MilestoneStore.cs — replace private `SafeWriteConfigNode` with `FileIOUtils.SafeWriteConfigNode`.

**Risk:** Zero — pure mechanical replacement. Same behavior, unified error handling.

### Phase 3B — Suppression Guard (1 commit)

**New file:** `Source/Parsek/SuppressionGuard.cs` (~30 lines)
```csharp
internal struct SuppressionGuard : IDisposable
{
    internal static SuppressionGuard Crew() { ... }
    internal static SuppressionGuard Resources() { ... }
    internal static SuppressionGuard ResourcesAndReplay() { ... }
    public void Dispose() { ... }
}
```

**Modified files (10 sites across 4 files):**
- CrewReservationManager.cs (5 sites) — `SuppressCrewEvents` → `using (SuppressionGuard.Crew())`
- KerbalsModule.cs (1 site) — `SuppressCrewEvents` → `using (SuppressionGuard.Crew())`
- ParsekFlight.cs (3 sites) — 1× `SuppressCrewEvents`, 2× `SuppressResourceEvents`
- KspStatePatcher.cs (1 site) — two flags → `using (SuppressionGuard.ResourcesAndReplay())`

**Risk:** Zero — identical semantics, struct avoids GC. Build verifies immediately.

### Phase 3C — UI Window Extractions (3 sequential commits)

**Order matters:** All three modify ParsekUI.cs, so they must be sequential.

**Commit 1: GroupPickerUI**
- New file: `Source/Parsek/UI/GroupPickerUI.cs`
- Move: 12 fields (groupPopup*) + `DrawGroupPickerPopup`, `DrawGroupPopupContents`, `DrawGroupPopupNode`, `OpenGroupPicker` (2 overloads)
- ParsekUI retains: `DrawRecordingsWindow` calls `groupPicker.Draw()` instead of `DrawGroupPickerPopup()`
- ~410 lines moved

**Commit 2: SpawnControlUI**
- New file: `Source/Parsek/UI/SpawnControlUI.cs`
- Move: 12+ fields (spawnControl*, spawnSort*, cachedSorted*, etc.) + `DrawSpawnControlWindow`, `DrawSpawnCandidateRows`, `DrawSpawnControlBottomBar`, `DrawSpawnSortableHeader` (3 overloads)
- ParsekUI retains: toggle button, calls `spawnControlUI.Draw()`
- ~183 lines moved

**Commit 3: ActionsWindowUI**
- New file: `Source/Parsek/UI/ActionsWindowUI.cs`
- Move: 8+ fields (actions*, lastRetiredKerbal*) + `DrawActionsWindow`, `DrawLedgerActionsSection`, `DrawRetiredKerbalsSection`, `DrawResourceBudget`, `DrawCompactBudgetLine`
- ParsekUI retains: toggle button, calls `actionsUI.Draw()`
- ~643 lines moved

### Phase 3D — Final Cleanup (1 commit)

- Verify namespace consistency (new files use `namespace Parsek`)
- Final `dotnet build` + `dotnet test`
- Update inventory (all files → Pass3-Done)
- Update CHANGELOG.md (v0.6.2 section)
- Update CLAUDE.md (add new files to key source files list)

### Dependency-Aware Ordering

```
Phase 3A (SafeWrite)  ─── no dependencies on 3B/3C
Phase 3B (Suppress)   ─── no dependencies on 3A/3C
Phase 3C-1 (GroupPicker)  ─┐
Phase 3C-2 (SpawnControl) ─┤── sequential (all modify ParsekUI.cs)
Phase 3C-3 (ActionsWindow) ─┘
Phase 3D (cleanup + docs) ─── after all above
```

Phases 3A, 3B, and 3C-1 could theoretically run in parallel (no shared files), but sequential is safer and builds trust incrementally.

### Estimated Line Impact

| Change | Lines Moved | New Files | Files Modified |
|--------|------------|-----------|---------------|
| SafeWriteConfigNode | ~30 shared | 1 (FileIOUtils.cs) | 4 |
| SuppressionGuard | ~30 shared | 1 (SuppressionGuard.cs) | 4 |
| GroupPickerUI | ~410 | 1 | 1 (ParsekUI) |
| SpawnControlUI | ~183 | 1 | 1 (ParsekUI) |
| ActionsWindowUI | ~643 | 1 | 1 (ParsekUI) |
| **Total** | **~1,291** | **5** | **8** (some overlap) |

### What We're NOT Doing (and Why)

| Candidate | Reason for Exclusion |
|-----------|---------------------|
| Watch mode extraction (R3-4) | 10+ dependencies on ParsekFlight instance state; cost > benefit |
| Dock/undock state extraction (R3-5) | Already resolved — methods extracted in Pass 1 |
| Ledger seed deduplication | Semantic differences (types, tolerance) make shared helper worse |
| ComputeTotalSpendings deduplication | Not actually duplicated — different domain logic |
| RecordingsTableUI extraction | 30+ shared fields, high coupling risk |
| SettingsWindowUI extraction | Modifies shared ParsekSettings, many callbacks |
| TestRunnerUI extraction | InGameTestRunner lifecycle coupling |
| LedgerOrchestrator decomposition | Already well-structured hub with narrow API |
| KspStatePatcher per-resource split | Methods are tightly coupled to KSP singletons; splitting adds indirection with no clarity gain |

---

## Quality Gates

Same as Pass 1:
- `dotnet build` after every commit
- `dotnet test` after every build (expect 4766+ pass)
- Opus review agent on each commit (per `refactor-review-checklist.md`)
- No logic changes — only structural moves and deduplication
