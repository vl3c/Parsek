# Refactor-3 Plan

## Overview

Structural refactoring focused on code written since v0.5.2 (refactor-2). Primary target: the GameActions/ ledger system (6,485 lines, 15 files — never refactored) plus other files that grew significantly. **Hard requirement: no logic changes.** Only structural reshuffling, logging additions, and new unit tests.

**Baseline:** 68,282 lines, 4,766 tests passing, branch `refactor-3` at `9f5b48d`.

**Scope exclusions:** Files already refactored in refactor-1/refactor-2 that remained stable (<100 lines changed since v0.5.2) are out of scope: ParsekFlight.cs, GhostVisualBuilder.cs, FlightRecorder.cs, BackgroundRecorder.cs, GhostPlaybackEngine.cs, ChainSegmentManager.cs, TrajectoryMath.cs, EngineFxBuilder.cs, PartStateSeeder.cs. Files with significant growth but already refactored (ParsekUI +1536, RecordingStore +715, ParsekScenario +740, GhostPlaybackLogic +369) may be revisited in a future pass if warranted, but their growth was feature-driven (test runner UI, ledger integration, optimizer) rather than structural decay.

## Setup Phase

1. **Worktree created:** `Parsek-refactor-3` branch `refactor-3` off `origin/main`
2. **Inventory:** `docs/dev/plans/refactor-3-inventory.md` — file sizes, long methods, extraction candidates, static state census
3. **Version bumped:** 0.6.1 → 0.6.2

---

## Subagent Rules (all passes)

- **All subagents use Opus** (`model: "opus"`) — non-negotiable for reliability
- Each subagent receives the Extraction Rules below as part of its prompt
- Orchestrator reviews every diff before accepting

---

## Extraction Rules for Subagents

These rules are given verbatim to every Pass 1 subagent.

### ALLOWED

- Moving a contiguous block of code from method A into a new `private` method B, called from the exact same position in A
- Adding parameters to the new method for any locals it needs (prefer passing values over `ref`/`out` when the caller doesn't need the mutation back)
- Reordering `using` directives
- Adding `#region` markers for organizational clarity
- Adding `ParsekLog` calls (see Logging Carve-out below)
- Marking **newly extracted** methods as `internal static` when they are pure functions testable without Unity, or `private` otherwise
- **C# accessibility rule:** A method marked `internal static` can only use parameter types that are `internal` or `public`. If any parameter type is `private` (e.g., a `private enum`), the method MUST be `private static` instead. Do NOT change the access modifier on the pre-existing type to work around this.

### NOT ALLOWED

- Changing any access modifier on **pre-existing** methods (methods that existed before this refactoring pass)
- Converting a local variable to a field to make extraction work
- Changing a captured closure to a parameter (changes when the value is read)
- Splitting a single loop into two calls (changes iteration order of side effects)
- Extracting the body of `switch` cases into individual methods unless the case body exceeds ~20 lines
- Extracting single-use guard clauses (`if (x == null) return;`) at the top of methods — these are standard patterns
- Extracting code shorter than 5 lines into its own method
- Changing any logic, adding new conditions, reordering operations, or altering control flow

### COROUTINE RULE

Methods with `IEnumerator` return type are **off-limits for method extraction** because `yield` cannot live in a sub-method. **Logging additions and tests ARE permitted** for coroutine methods.

### NAMING

Follow existing codebase conventions — verb-prefixed descriptive names (e.g., `CheckParachuteTransition`, `ClassifyGearState`, `TryComputeLoopPlaybackUT`). The extracted method name should describe **what** the block does, not **where** it came from.

### CONFLICT RESOLUTION PRINCIPLE

**Preservation rules ALWAYS override improvement rules.** If making an extracted method `internal static` (for testability) would require changing a pre-existing type's access modifier, the correct resolution is to make the method `private static` (untestable but rule-compliant). Never "fix" a compilation error by modifying pre-existing code that the rules say not to touch.

More generally: if you hit any obstacle during extraction, the answer is to scale back the extraction, not to bend the rules to make it work. A `private` method that can't be unit-tested is always better than an `internal` method achieved by violating the no-access-modifier-change rule.

### METHOD SIZE GUIDANCE

Extract when a method exceeds ~30 lines AND contains multiple logical steps that each have a clear single-purpose name. Do not extract purely for line count — a 50-line method that does one coherent thing is fine.

---

## Logging Carve-out

Adding `ParsekLog` calls is the **one permitted non-structural change**. These calls must be:
- Purely observational — must not change control flow
- Must not appear inside conditional branches that would alter existing behavior
- Must not be added inside performance-critical tight inner loops

### Logging API (from ParsekLog.cs)

- `ParsekLog.Info(subsystem, message)` — important events (start/stop, state changes, errors)
- `ParsekLog.Warn(subsystem, message)` — warnings
- `ParsekLog.Error(subsystem, message)` — errors
- `ParsekLog.Verbose(subsystem, message)` — diagnostic detail. Has internal guard: `if (!IsVerboseEnabled) return;`
- `ParsekLog.VerboseRateLimited(subsystem, key, message)` — per-frame data with rate limiting
- `ParsekLog.Log(message)` — shorthand for `Info("General", message)`

### Verbose Performance Rule

`Verbose()` has an internal `if (!IsVerboseEnabled) return;` guard, so the method call itself is cheap when verbose is off. However, **string interpolation at the call site happens unconditionally**.

- The existing codebase has 283+ `Verbose()` calls with `$"..."` interpolation and no external guard. This is the accepted pattern — **do NOT retrofit external guards onto existing calls**.
- For **newly added** verbose calls in hot paths (per-frame, per-part iteration), prefer the external guard to avoid interpolation cost:
  ```csharp
  if (ParsekLog.IsVerboseEnabled)
      ParsekLog.Verbose("Subsystem", $"expensive {interpolation}");
  ```
- For newly added verbose calls in cold paths (one-time init, event handlers), the internal guard is sufficient — just call `ParsekLog.Verbose(...)` directly.
- `VerboseRateLimited` is already guarded — use it for per-frame diagnostic data.

### Logging Scope

Every action, state transition, guard-condition skip, and error path should have a log statement. Use:
- `Info` for important lifecycle events
- `Verbose` for diagnostic breadcrumbs
- `VerboseRateLimited` for per-frame data only
- Format: `[Parsek][LEVEL][Subsystem] message` (handled automatically by ParsekLog.Write)

---

## Unit Test Rules

For every extracted method that is testable without Unity:

- Mark the method `internal static` (newly created methods only — do not change access on pre-existing methods)
- Write tests covering: normal path, edge cases (null, empty, boundary values), and at least one failure/skip path
- **Every test must have a comment stating what bug it would catch** — no vacuous tests that pass by default
- Use `ParsekLog.TestSinkForTesting` to capture and assert on log output where logging behavior matters
- Use `[Collection("Sequential")]` for any test touching shared static state
- **Do NOT write tests for methods that require Unity types as live objects** (MonoBehaviour, GameObject, Transform, etc.) — only test pure logic
- xUnit auto-discovers test files — no need to modify the test `.csproj`

### Test File Naming

Follow the existing feature-based naming convention (e.g., `AdaptiveSamplingTests.cs`, `RewindLoggingTests.cs`). Add tests to existing test files when they cover the same feature area. Create new feature-named files (not source-file-named) when needed. See `RewindLoggingTests.cs` for the canonical log-capture test pattern.

---

## Pass 1 — Method Extraction + Logging + Tests

**Goal:** For each file: (a) extract logical units into well-named methods, (b) ensure comprehensive logging, (c) add practical unit tests where possible. **No cross-file changes.**

### Per-file Workflow

1. **Read & analyze** — subagent reads the entire file, identifies extraction candidates (methods >30 lines with multiple logical steps), notes methods lacking logging, notes testable methods without tests
2. **Extract methods** — apply the Extraction Rules above
3. **Add/verify logging** — per the Logging Carve-out rules
4. **Add unit tests** — per the Unit Test Rules
5. **Build & test** — `dotnet build` + `dotnet test`
6. **Update inventory** — mark file status as `Pass1-Done`

### Processing Order

#### Canary — GameAction.cs (895 lines)

Process first as the dry run. **Lowest risk:** pure data class with no side effects, no static mutable state, no KSP API coupling. The serialize/deserialize switch statements (84 and 102 lines) have clear per-domain extraction candidates. Many per-type helpers are pure functions testable as `internal static`. Validates the entire extraction→logging→test→build workflow before touching stateful files.

**Extraction targets:**
- `SerializeInto` (84 lines, 23-case switch) → extract per-domain helpers where case body exceeds 20 lines (science, contract, kerbal, facility, strategy)
- `DeserializeFrom` (102 lines, 23-case switch) → same pattern: per-domain helpers for cases >20 lines
- Review all 72+ per-type serialize/deserialize helpers for logging gaps

**After:** `dotnet build` + `dotnet test`. Commit individually.

#### Tier 1 — Large Files With Most Extraction Targets (one at a time)

| # | File | Lines | Key Extractions |
|---|------|-------|-----------------|
| 2 | KspStatePatcher.cs | 777 | PatchContracts (179→3-4 helpers), PatchProgressNodeTree (75), PatchDestructionState (71→per-building helper), PatchFacilities (69), PatchMilestones (58), PatchPerSubjectScience (52). **Note:** KSP API coupling means most extractions will be `private` (not directly testable); focus on logging coverage. |
| 3 | LedgerOrchestrator.cs | 900 | CreateVesselCostActions (66→build+recovery helpers), MigrateKerbalAssignments (44→loop body), OnRecordingCommitted (44→logging review). Most methods are moderate length — focus on logging gaps and `internal static` test coverage for pure helpers (DeduplicateAgainstLedger, GetActionKey, ExtractCrewFromRecording, FindRecordingById). |
| 4 | GameStateRecorder.cs | 975 | Subscribe (100+→grouped subscriptions), OnScienceReceived (60+→subject capture helper), OnFacilityUpgraded (50+→level diff helper). Mixed static/instance state — focus on logging for suppression flag usage. |

**After each file:** `dotnet build` + `dotnet test`. Commit individually per file (3 commits).

#### Tier 2 — Other New/Large Files (2 parallel subagents per batch)

| Batch | Files | Lines | Key Extractions |
|-------|-------|-------|-----------------|
| 2A | FundsModule.cs (513), Ledger.cs (506) | 1,019 | FundsModule: ComputeTotalSpendings (53), ProcessAction (46). Ledger: Reconcile (115→PruneEarnings/PruneSpendings/PruneOther), LoadFromFile (74), seed dedup (3x ~25 lines→shared helper). |
| 2B | KerbalsModule.cs (892), GameStateEventConverter.cs (514) | 1,406 | KerbalsModule: ProcessAction (50+). Converter: ConvertEvent (57→review case body sizes), ConvertContractAccepted (51), ConvertEvents (45). |
| 2C | RecordingOptimizer.cs (863), GhostMapPresence.cs (1,211) | 2,074 | RecordingOptimizer: pure algorithmic — subagent reads full file to identify extraction candidates. GhostMapPresence: CreateMapVessel (60+), UpdateOrbit (50+). **Note:** GhostMapPresence is Unity-heavy; most extractions will be `private`. |
| 2D | ParsekPlaybackPolicy.cs (892) | 892 | HandlePlaybackCompleted (45+). Mostly new — subagent reads full file to identify extraction candidates and logging gaps. |

**After each batch:** Merge-check for conflicts, then `dotnet build` + `dotnet test`. Commit per batch (4 commits).

**Note:** VesselGhoster.cs (709 lines, +2 changed) moved to Tier 4 — it's stable and Unity-heavy, making extraction value low.

#### Tier 3 — Smaller GameActions Modules (3 parallel subagents)

| Batch | Files | Lines | Key Extractions |
|-------|-------|-------|-----------------|
| 3A | ScienceModule.cs (386), ReputationModule.cs (378), ContractsModule.cs (327) | 1,091 | ScienceModule: ProcessEarning (61→hard-cap helper). ContractsModule: PrePass (67→deadline scan helper). ReputationModule: EvaluateHermiteSpline (39→borderline, review logging). |
| 3B | StrategiesModule.cs (298), RecalculationEngine.cs (351), FacilitiesModule.cs (239) | 888 | StrategiesModule: TransformContractReward (66→per-resource diversion helper). RecalculationEngine: Recalculate (91→review for sub-step extraction), RegisterModule (63). FacilitiesModule: likely no extractions needed. |

**After batch:** `dotnet build` + `dotnet test`. Commit per batch (2 commits).

#### Tier 4 — Small/Stable Files, Quick Scan

Remaining files: GameActionDisplay.cs (246), MilestonesModule.cs (100), IResourceModule.cs (55), GameStateStore.cs (709), MilestoneStore.cs (474), ResourceBudget.cs (433), CrewReservationManager.cs (686), VesselGhoster.cs (709).

Quick scan for missing logging and test opportunities. Most will need zero or minimal changes. VesselGhoster is included here (stable, +2 changed, Unity-heavy). Commit once.

---

## Pass 2 — Architecture Analysis (read-only)

**Goal:** Systematic analysis BEFORE any cross-file restructuring. **No code changes.**

### Deliverables

1. **Dependency graph** — which GameActions/ files depend on which others, and external callers
2. **Static mutable state mutation sites** — for each of the 38 static mutable fields, which files read/write them
3. **Cross-reference map** — for every `internal static` and `public` method in the large files, list all call sites
4. **Deduplication opportunities** — confirm or reject the 5 patterns identified in the inventory:
   - Safe-write file I/O (Ledger, GameStateStore, MilestoneStore, RecordingStore)
   - Detail field parsing (GameStateEventConverter, GameActionDisplay, ResourceBudget, GameStateEventDisplay)
   - Seed-check-update (3x in Ledger.cs)
   - ComputeTotalSpendings (FundsModule, ScienceModule)
   - Suppression flag try/finally (KspStatePatcher, CrewReservationManager)
5. **Concrete split recommendations** with dependency-aware ordering for Pass 3

**Output:** Analysis document presented to user for approval before Pass 3 begins.

---

## Pass 3 — SOLID Restructuring (cross-file changes)

**Starts only after Pass 2 analysis is approved by user.**

Likely candidates (to be confirmed by Pass 2):

### Phase 3A — Deduplication (low risk)

- **Ledger.cs seed methods** — 3 identical ~25-line patterns → shared `SeedOrUpdateInitial(GameActionType, double, string)` helper
- **Safe-write pattern** — if 4+ identical blocks exist, extract shared `SafeWriteConfigNode(path, node)` utility
- **ComputeTotalSpendings** — if FundsModule and ScienceModule patterns are truly identical, extract shared helper

### Phase 3B — Structural Splits (medium risk, if warranted)

- **LedgerOrchestrator decomposition** — split migration (`MigrateOldFormats`, `PopulateUnpopulatedCrewEndStates`) into `LedgerMigration` class, seeding into `LedgerSeeder` class
- **KspStatePatcher decomposition** — `PatchContracts` (179 lines) is large enough to justify a `ContractPatcher` class; other methods stay

### Phase 3C — Final Cleanup

- Verify namespace consistency
- Final `dotnet build` + `dotnet test`
- Update inventory — mark all files with final status

---

## Quality Gates

| Gate | When | Criteria |
|------|------|----------|
| `dotnet build` | After every file (Tier 1) or batch (Tier 2-4) | Zero errors, zero new warnings |
| `dotnet test` | After every build gate | 4766+ tests pass, 0 failures (count increases as we add tests) |
| Orchestrator diff review | Every file change | Verify no logic changes, logging is correct, tests are meaningful |
| Supervisor build verify | Before every commit | Orchestrator runs `dotnet build` + `dotnet test` independently — never trust agent self-reported build success |
| Merge-check | After parallel batches | No file-level conflicts between parallel agents |
| User approval | Before Pass 3 execution | User reviews Pass 2 analysis and split plan |

### Build Commands (relative to worktree root)

```bash
cd Source/Parsek && dotnet build
cd Source/Parsek.Tests && dotnet test
```

---

## Status Tracking

The inventory document (`docs/dev/plans/refactor-3-inventory.md`) tracks per-file status. After processing each file or batch, update the inventory's file tables with a Status column:

- `Pending` — not yet processed
- `Pass1-InProgress` — currently being worked on
- `Pass1-Done` — Pass 1 complete (extraction + logging + tests)
- `Pass3-Done` — Pass 3 complete (cross-file restructuring)

---

## Documentation Updates

At the end of the refactor (before final merge to main):
- **CHANGELOG.md** — add v0.6.2 section summarizing structural changes, new test count, and any latent bugs found
- **CLAUDE.md** — update "Key source files" list if Pass 3 creates new files (e.g., LedgerMigration, ContractPatcher)
- **Inventory** — mark all processed files with final status

---

## Review Checklist

Same checklist as refactor-1/refactor-2 (see `docs/dev/done/refactor/refactor-review-checklist.md`):

1. **Condition inversions** — verify De Morgan's law on every guard-clause inversion
2. **Extraction position** — extracted code called from the EXACT same position
3. **No logic changes** — no conditions added, removed, or reordered
4. **Control flow preserved** — break/continue/return semantics intact
5. **Grouped blocks** — mutations in block N don't cause block N+1 to fire when it shouldn't
6. **Coroutines untouched** — IEnumerator methods have zero structural changes
7. **Access modifiers unchanged** — no pre-existing method had its access modifier changed
8. **No loop splitting** — single loops not split into multiple method calls
9. **Logging is observational** — added ParsekLog calls have no side effects
10. **Deduplication correctness** — both original blocks were semantically identical
11. **Parameter passing** — no instance fields converted to method parameters unnecessarily
12. **Static correctness** — static methods don't access instance state

---

## Rollback Strategy

- Each tier/file gets its own commit — `git revert` targets a single unit of work
- If tests break mid-tier and root cause is unclear: revert the last file's changes, investigate, re-attempt with tighter constraints
- The worktree is disposable — worst case, `git worktree remove` and start fresh from main

---

## Commit Strategy

| Phase | Granularity | Est. Commits |
|-------|-------------|--------------|
| Pass 1 Canary | 1 file (GameAction.cs) | 1 |
| Pass 1 Tier 1 | 1 per file (KspStatePatcher, LedgerOrchestrator, GameStateRecorder) | 3 |
| Pass 1 Tier 2 | 1 per batch (2A-2D) | 4 |
| Pass 1 Tier 3 | 1 per batch (3A-3B) | 2 |
| Pass 1 Tier 4 | 1 | 1 |
| Pass 3 Phase A | 1 (dedup) | 1 |
| Pass 3 Phase B | 1 per split | ~1-2 |
| Pass 3 Phase C | 1 (cleanup + docs) | 1 |
| **Total** | | **~14-15** |

No `Co-Authored-By` lines (per CLAUDE.md).
