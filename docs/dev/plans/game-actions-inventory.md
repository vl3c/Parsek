# Game Actions & Resource System — Codebase Inventory

*Baseline snapshot before implementation begins.*

**Baseline test count:** 3374 tests pass on `game-action-recording-redesign` at `cb7f9cb`
**Worktree:** `Parsek-game-action-recording-redesign` branch `game-action-recording-redesign` off `main` at `5cd5ffe`
**Design doc:** `docs/parsek-game-actions-system-design.md`

---

## Existing Files — Will Be Modified

| File | Lines | Role | Changes Needed | Status |
|------|-------|------|----------------|--------|
| `ParsekScenario.cs` | 1757 | ScenarioModule, save/load | Add ledger file reference, lifecycle hooks for recalculation | Pending |
| `RecordingStore.cs` | 2534 | Static recording storage, file I/O | Pattern reference for safe-write; may need commit hook | Pending |
| `ResourceApplicator.cs` | 306 | Current resource delta application | **Replace** with ledger-based patching (existing point-by-point delta system becomes obsolete) | Pending |
| `CrewReservationManager.cs` | 497 | Current crew reservation (name→replacement map) | **Expand significantly** — chain system, retired pool, XP tracking, per-slot chains | Pending |
| `GameStateRecorder.cs` | 752 | KSP event capture (science, funds, rep, facilities) | Extend to capture events for ledger extraction; already has `OnScienceReceived`, facility polling | Pending |
| `GameStateStore.cs` | ~200 | Stores captured events | May need new event types for ledger-relevant data | Pending |
| `ResourceBudget.cs` | 446 | Budget computation for UI | **Replace** with ledger-derived available balances | Pending |
| `MilestoneStore.cs` | 474 | Milestone tracking | Integrate with new milestones module | Pending |
| `ParsekFlight.cs` | 8092 | Flight controller, commit path | Add ledger extraction on commit, warp cycle integration | Pending |
| `FlightRecorder.cs` | 4914 | Recording state, physics frame sampling | May need new event subscriptions for contract/milestone capture | Pending |
| `ParsekUI.cs` | ~3500 | UI windows | Add available balance display, resource breakdown panels | Pending |
| `Recording.cs` | 275 | Recording data class | May need fields for linking to ledger earning actions | Pending |
| `Patches/ScienceSubjectPatch.cs` | ~100 | Harmony patch for science subjects | Review for interaction with new science recalculation | Pending |

## Existing Files — Pattern References (Read-Only)

| File | Lines | What to Learn From It |
|------|-------|----------------------|
| `RecordingStore.cs` | 2534 | Safe-write pattern (.tmp + rename), external file I/O, ConfigNode serialization |
| `TrajectoryPoint.cs` | 30 | Compact serialization with `ToString("R", InvariantCulture)` |
| `BranchPoint.cs` | 59 | Enum with explicit int values for stable serialization |
| `RecordingTree.cs` | 953 | Complex data structure with ConfigNode serialization, `BackgroundMap`, derived state |
| `GhostPlaybackEngine.cs` | ~1553 | Event-driven architecture pattern (lifecycle events, subscribers) |

## New Files to Create

| File | Purpose | Estimated Lines |
|------|---------|----------------|
| `GameAction.cs` | Base type + all action schema types (enums, structs) | 300-400 |
| `Ledger.cs` | Ledger file I/O, reconciliation, in-memory action store | 400-500 |
| `RecalculationEngine.cs` | Sort, walk, module dispatch, derived state computation | 300-400 |
| `ScienceModule.cs` | Science earnings, subject caps, spending reservation | 200-300 |
| `FundsModule.cs` | Seeded balance, earnings, spendings, reservation | 200-300 |
| `ReputationModule.cs` | Curve simulation, nominal→effective | 150-250 |
| `MilestonesModule.cs` | Once-ever flags, fund/rep flow to second-tier | 100-150 |
| `ContractsModule.cs` | State machine, slot reservation, deadline generation | 300-400 |
| `FacilitiesModule.cs` | Upgrade/destroy/repair, visual state during warp | 200-300 |
| `StrategiesModule.cs` | Time-windowed transforms, slot reservation | 200-300 |
| `KspStatePatcher.cs` | Write derived state into KSP singletons after recalculation | 300-400 |
| `LedgerBuilder.cs` (test) | Fluent test builder for ledger entries | 200-300 |

**Estimated total new code:** ~3000-4000 lines + tests

## Dependency Map

```
                    ┌─────────────────┐
                    │  ParsekScenario │ ← lifecycle hooks, save/load
                    │  (owns ledger)  │
                    └────────┬────────┘
                             │
                    ┌────────▼────────┐
                    │     Ledger      │ ← file I/O, action store
                    │   (ledger.dat)  │
                    └────────┬────────┘
                             │
                    ┌────────▼────────┐
                    │ RecalcEngine    │ ← sort, walk, dispatch
                    └───┬────┬────┬──┘
                        │    │    │
            ┌───────────┘    │    └───────────┐
            ▼                ▼                ▼
    ┌───────────┐    ┌───────────┐    ┌───────────┐
    │ First-Tier│    │ Transform │    │Second-Tier│
    │ Modules   │───▶│ Strategies│───▶│ Modules   │
    │ Science   │    └───────────┘    │ Funds     │
    │ Milestones│                     │ Reputation│
    │ Contracts │                     └─────┬─────┘
    │ Kerbals   │                           │
    └───────────┘                           │
                                   ┌────────▼────────┐
                                   │ KspStatePatcher  │ ← write to KSP
                                   └─────────────────┘

    ┌──────────────┐           ┌──────────────────┐
    │ ParsekFlight │──commit──▶│ Ledger extraction │
    │ (commit path)│           │ (sidecar → ledger)│
    └──────────────┘           └──────────────────┘
```

## Existing Patterns to Follow

- **Safe-write:** `RecordingStore.SaveRecordingFiles` uses `.tmp` + rename for atomic writes
- **Float serialization:** `ToString("R", InvariantCulture)` everywhere (locale-safe)
- **Suppress pattern:** `GameStateRecorder.SuppressResourceEvents = true` before mutating KSP state
- **ConfigNode:** `node.AddValue("key", value)` / `node.GetValue("key")` for flat data; `node.AddNode("CHILD")` for nested
- **Logging:** `ParsekLog.Info("Subsystem", $"message with {context}")`, `.Verbose` for high-frequency, `.VerboseRateLimited` for per-frame
- **Pure static methods:** `internal static` for testable logic, extracted from MonoBehaviour classes
- **Test cleanup:** `RecordingStore.ResetForTesting()`, `CrewReservationManager.ResetReplacementsForTesting()`

## KSP API Surface — Already Used

| API | Where Used | Relevance to Game Actions |
|-----|-----------|--------------------------|
| `Funding.Instance.AddFunds` | ResourceApplicator | Funds patching — already known pattern |
| `ResearchAndDevelopment.Instance.AddScience` | ResourceApplicator | Science patching — already known |
| `Reputation.Instance.AddReputation` | ResourceApplicator | Reputation patching — already known |
| `GameEvents.OnScienceRecieved` (KSP typo) | GameStateRecorder | Science event capture — already subscribed |
| `GameEvents.onFundsChanged` | GameStateRecorder | Funds change capture |
| `GameEvents.onReputationChanged` | GameStateRecorder | Reputation change capture |
| `ScenarioUpgradeableFacilities.protoUpgradeables` | GameStateRecorder | Facility level polling — already implemented |
| `DestructibleBuilding` | GameStateRecorder | Building destruction detection — already polling |

## KSP API Surface — New (Needs Investigation)

| API | Purpose | Priority |
|-----|---------|----------|
| `ResearchAndDevelopment.Instance.Science` (read) | Current science balance for patching | High |
| `ResearchAndDevelopment.Instance.SetScience` | Direct balance set (vs AddScience) | High |
| `ProgressTracking.Instance` | Milestone state patching | Medium |
| `ContractSystem.Instance` | Contract state patching | Medium |
| `StrategySystem.Instance` | Strategy state patching | Low |
| `Reputation.AddReputation` (decompiled curve) | Curve formula extraction (D1) | Medium |
| `ScienceSubject.scienceCap` | Per-subject science maximum | High |

## Static Mutable State

These require `[Collection("Sequential")]` in tests:
- `RecordingStore` — static recording lists
- `CrewReservationManager.crewReplacements` — static dict
- `ParsekLog.TestSinkForTesting` — global test sink
- New: `Ledger` (if static) — ledger action store
- New: Module state during recalculation
