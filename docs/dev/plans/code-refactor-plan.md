# Parsek Code Refactor Plan

## Overview

Multi-pass structural refactoring of ~34,000 lines across 38 C# source files. **Hard requirement: no logic changes.** Only structural reshuffling, logging additions, and new unit tests.

## Setup Phase

1. **Worktree created:** `Parsek-code-refactor` branch `code-refactor` off `main` at `10298cf`
2. **Create research doc** `docs/dev/research/code-refactor-inventory.md` — exhaustive listing of every source file with structured inventory table. Updated as files are processed/split. Serves as the canonical tracking document for the entire refactor.
3. **Pre-flight cross-reference map** — grep all call sites for `public`/`internal` methods in Tier 1-2 files, included in the research doc.

### Research Doc Schema

The inventory doc uses this table per file:

| Column | Description |
|--------|-------------|
| File | Relative path from `Source/Parsek/` |
| Lines | Line count |
| Types | All classes, structs, enums defined in the file |
| Nested Types | Types defined inside other types |
| Public/Internal Methods | Count |
| Regions | `#region` names if any |
| Notes | Complexity notes, dependencies, special concerns |
| Status | `Pending` / `Pass1-InProgress` / `Pass1-Done` / `Pass3-Done` |

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

1. **Read & analyze** — subagent reads the entire file (or section-by-section for Tier 1), identifies extraction candidates, notes methods lacking logging, notes testable methods without tests
2. **Extract methods** — apply the Extraction Rules
3. **Add/verify logging** — per the Logging Carve-out rules
4. **Add unit tests** — per the Unit Test Rules
5. **Update research doc** — mark file status as `Pass1-Done`

### Processing Order

#### Canary — TrajectoryMath.cs (534 lines)

Process first as a dry run. Pure static math — lowest risk. Validates the entire workflow (extraction → logging → tests → build → test) before touching critical files. Review thoroughly, fix any process issues before proceeding.

#### Tier 1 — Critical (>4000 lines), one file at a time

| File | Lines | Processing Strategy |
|------|-------|---------------------|
| ParsekFlight.cs | 8,218 | Process by `#region` sections. Subagent reads field declarations + one region at a time. Coroutine methods are off-limits for extraction (logging + tests only). |
| GhostVisualBuilder.cs | 6,395 | Process in natural sections. Subagent identifies logical boundaries by reading the file — do NOT use pre-specified line ranges. Data classes (top of file) are read-only in Pass 1 (moved in Pass 3). |
| FlightRecorder.cs | 4,107 | Do NOT attempt to unify the 20+ `Check*State`/`Check*Transition` polling methods into a shared generic pattern — they look similar but vary in key/value types and classification logic. Focus on extracting sub-steps within individual methods where they exceed the 30-line threshold. |

**After each file:** `cd Source/Parsek && dotnet build` then `cd Source/Parsek.Tests && dotnet test` (relative to worktree root). Commit individually per file (3 commits).

#### Tier 2 — Large (1500-2100 lines), 2 parallel Opus subagents

| Batch | Files |
|-------|-------|
| 2A | ParsekScenario.cs (2072), RecordingStore.cs (1815) |
| 2B | ParsekUI.cs (1997), BackgroundRecorder.cs (1524) |

**After batch:** Merge-check for any file-level conflicts between parallel agents, then `dotnet build` + `dotnet test`. Commit batch (1 commit).

#### Tier 3 — Medium (400-1000 lines), 3 parallel Opus subagents

| Batch | Files |
|-------|-------|
| 3A | VesselSpawner.cs (978), ParsekKSC.cs (852), MergeDialog.cs (532) |
| 3B | GameStateRecorder.cs (757), GameStateStore.cs (694), RecordingTree.cs (681) |
| 3C | ActionReplay.cs (529), MilestoneStore.cs (461), ResourceBudget.cs (400) |

**After batch:** Merge-check, then `dotnet build` + `dotnet test`. Commit batch (1 commit).

#### Tier 4 — Small (<300 lines), single pass

21 files total (15 root-level small files + 5 Patches/ + AssemblyInfo.cs). Note: `GameStateBaseline.cs` (274 lines) and `GameStateEvent.cs` (302 lines) are the largest in this tier and may need more attention. Quick scan for missing logging and test opportunities. Most will need zero or minimal changes. Commit once.

Complete Tier 4 file list:
- GameStateEvent.cs (302), GameStateBaseline.cs (274), RecordingPaths.cs (166), ParsekLog.cs (161)
- FlightResultsPatch.cs (103), Milestone.cs (75), PhysicsFramePatch.cs (72), SurfacePosition.cs (65)
- FacilityUpgradePatch.cs (62), TechResearchPatch.cs (61), ParsekSettings.cs (58), PartEvent.cs (56)
- ParsekHarmony.cs (54), OrbitSegment.cs (51), ScienceSubjectPatch.cs (50)
- CommittedActionDialog.cs (36), ParsekToolbarRegistration.cs (32), TrajectoryPoint.cs (30)
- BranchPoint.cs (28), TerminalState.cs (14)
- Properties/AssemblyInfo.cs (auto-generated, skip)

---

## Pass 2 — Architecture Analysis (read-only)

**Goal:** Systematic analysis BEFORE any cross-file restructuring. **No code changes.**

Moved before SOLID restructuring so the analysis informs splitting decisions.

### Deliverables

1. **Dependency graph** — which files/classes depend on which others
2. **Static mutable state inventory** — every static field that gets mutated, and which files mutate it
3. **Cross-reference map** — for every `internal static` and `public` method in the large files, list all call sites in both production code and test code (this is critical for Pass 3 — every moved method requires updating all call sites)
4. **Nested type inventory** — all classes/structs/enums defined inside other classes (e.g., ParsekFlight nested types, GhostVisualBuilder data types)
5. **Multi-class file inventory** — all files containing more than one type (including nested types like `MaterialCleanup : MonoBehaviour` inside GhostVisualBuilder)
6. **Concrete split recommendations** with dependency-aware ordering

**Output:** Analysis document presented to user for approval before Pass 3 begins.

---

## Pass 3 — SOLID Restructuring (cross-file changes)

**Goal:** Split bloated files into focused single-responsibility classes. One class per file.

**Starts only after Pass 2 analysis is approved by user.**

### Phase 3A — Zero-risk type extractions (no method moves)

- Extract data types from top of GhostVisualBuilder.cs (15 types: 10 classes, 4 structs, 1 enum) → individual files
- Extract `MaterialCleanup : MonoBehaviour` from GhostVisualBuilder.cs → own file
- Extract nested types from ParsekFlight (`GhostPlaybackState`, `LightPlaybackState`, `InterpolationResult`, `PendingDestruction`, `GhostPosEntry`, `GhostPosMode`) → individual files
- Update all references (ParsekKSC.cs depends heavily on ParsekFlight nested types, test files reference these by qualified name)
- `dotnet build` + `dotnet test` + commit

### Phase 3B — Logical class splits (informed by Pass 2 analysis)

Likely candidates (to be confirmed by Pass 2):
- **GhostVisualBuilder.cs** → core builder + FX-specific builders (engine, RCS, fairing, deployable, reentry)
- **FlightRecorder.cs** → recording state machine + `PartEventPoller` (the 20+ Check* methods)
- **ParsekFlight.cs** → playback controller + ghost management + `GhostEventApplicator` (the Apply* methods used by both ParsekFlight and ParsekKSC)
- **ParsekScenario.cs** → save/load + crew management

Each split:
1. Present the specific move plan to user
2. Execute the move
3. Update ALL call sites (production + tests)
4. `cd Source/Parsek && dotnet build` + `cd Source/Parsek.Tests && dotnet test`
5. Commit

### Phase 3C — Final cleanup

- Verify one class per file everywhere
- Verify namespace consistency
- Final `dotnet build` + `dotnet test`
- Update research doc — mark all files `Pass3-Done`

---

## Quality Gates

| Gate | When | Criteria |
|------|------|----------|
| `dotnet build` | After every file (Tier 1) or batch (Tier 2-4) | Zero errors, zero new warnings from our code |
| `dotnet test` | After every build gate | 1250+ tests pass, 0 failures (count increases as we add tests) |
| Orchestrator diff review | Every file change | Verify no logic changes, logging is correct, tests are meaningful |
| Merge-check | After parallel batches | No file-level conflicts between parallel agents |
| User approval | Before Pass 3 execution | User reviews Pass 2 analysis and split plan |

### Build Commands (relative to worktree root)

```bash
cd Source/Parsek && dotnet build
cd Source/Parsek.Tests && dotnet test
```

---

## Rollback Strategy

- Each tier/file gets its own commit — `git revert` targets a single unit of work
- If tests break mid-tier and root cause is unclear: revert the last file's changes, investigate, re-attempt with tighter constraints
- The worktree is disposable — worst case, `git worktree remove` and start fresh from main

---

## Commit Strategy

| Phase | Granularity | Est. Commits |
|-------|-------------|--------------|
| Pass 1 Canary | 1 file | 1 |
| Pass 1 Tier 1 | 1 per file | 3 |
| Pass 1 Tier 2 | 1 per batch | 1 |
| Pass 1 Tier 3 | 1 per batch | 1 |
| Pass 1 Tier 4 | 1 | 1 |
| Pass 3 Phase A | 1 (type extractions) | 1 |
| Pass 3 Phase B | 1 per split group | ~4-5 |
| Pass 3 Phase C | 1 (cleanup) | 1 |
| **Total** | | **~13-14** |

No `Co-Authored-By` lines (per CLAUDE.md).
