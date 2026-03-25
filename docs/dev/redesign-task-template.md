# Redesign Task Template

Template for structuring large multi-phase redesign efforts in Parsek. Derived from how the recording system redesign (PR #59, 38,902 lines, 114 files), T25 extraction (PR #84, 31 commits), and refactor-2 were planned and executed.

This template focuses on the **document artifacts** produced at each stage. For the agent orchestration process (vision → scenarios → design → plan/build/review → commit), see `development-workflow.md`. For the standard post-change checklist, see `CLAUDE.md`.

---

## Document Set

A redesign produces these artifacts, roughly in order:

| Artifact | Location | When Created | Required? |
|----------|----------|-------------|-----------|
| System design doc | `docs/dev/<feature>-system-design.md` | After vision + scenarios are settled | Yes — for any multi-phase redesign |
| Codebase inventory | `docs/dev/plans/<feature>-inventory.md` | During codebase exploration (before planning) | Yes — for redesigns touching existing code |
| Task plans | `docs/dev/plans/<feature>-task-N-<component>.md` | During task decomposition (after exploration) | Yes |
| Deferred items | `docs/dev/plans/<feature>-deferred.md` | Accumulated during design + implementation | Yes — even if initially empty |
| Review checklist | `docs/dev/plans/<feature>-review-checklist.md` | Before implementation begins | Optional — only if feature-specific checks go beyond CLAUDE.md |

When the redesign is complete, move all artifacts to `docs/dev/done/<feature>/`.

**When to use a subset:** A pure internal refactor (no gameplay change) may need only the inventory, task plans, and deferred items — no system design doc, since there's no new behavior to specify. See the "Decision Framework" table in `development-workflow.md` for guidance.

**Refactors have different structure:** Refactor plans (e.g., `code-refactor-2-plan.md`) follow their own conventions — processing tiers, extraction rules (ALLOWED/NOT ALLOWED lists), logging carve-outs, quality gates per tier, and conflict resolution principles. These don't map onto the feature design doc template. For refactors, the plan doc IS the design doc, and its structure should be driven by the refactor's scope and risk profile. The inventory, deferred items, and review checklist artifacts still apply.

---

## 1. System Design Document

**File:** `docs/dev/<feature>-system-design.md`
**Purpose:** The single source of truth for what the system does. Implementation agents read this; review agents verify against this. Everything flows from this document.

### Sections

The sections below are advisory — reorder, rename, merge, or add sections to match the feature. Real design docs range from 8 sections (camera-follow) to 15+ (recording system). The key principle: **every section below that's relevant to your feature must appear somewhere in the doc**, but the headings and order should serve clarity, not this template.

Required sections (always include):
- Purpose / Problem
- Data Model (with serialization if persisted)
- Behavior (concrete scenarios)
- Edge Cases (exhaustive for the feature's complexity — 15+ is typical for a major subsystem, 5-8 may be genuinely exhaustive for a smaller feature)
- Diagnostic Logging
- Test Plan

Include when relevant:
- Terminology (if introducing new concepts distinct from KSP's model)
- Mental Model (if the system has non-obvious state or flow)
- Existing Systems: What Changes vs What's New
- What Doesn't Change / Out of Scope
- Backward Compatibility (if touching saves or recording format)
- Performance Budget (if per-frame or scaling operations)
- Error Recovery (if I/O, scene changes, or KSP API failure are concerns)

### Template

```markdown
# Parsek <Feature Name> — Design Document

*Design specification for <one-line description>.*

*Status: IN PROGRESS / IMPLEMENTATION COMPLETE (date, PR #)*

---

## Purpose and Scope

What this document covers (bulleted list of subsystems/concerns).
What it does NOT cover (explicit exclusions to prevent scope creep).

## Terminology

| Term | Definition |
|------|------------|
| <term> | <definition, distinguished from KSP concept if relevant> |

Only needed when the feature introduces concepts that would confuse a reader.

## Mental Model

How the feature works conceptually. ASCII diagrams for state machines,
data flow, or structural relationships. Write for someone who has never
seen the code.

## Existing Systems: What Changes vs What's New

| Component | Current Behavior | Required Change | Complexity |
|-----------|-----------------|-----------------|------------|
| <file/system> | <what it does now> | <what changes> | Low/Med/High |

This section prevents surprises during implementation by mapping the
redesign onto existing code upfront.

## Data Model

### New Types

For each new struct/class/enum, show shape with field types and purpose.
Note class vs struct rationale, explicit enum int values for stable
serialization.

### Changes to Existing Types

**<ClassName>** (`Source/Parsek/<File>.cs`):
- `<new field>` — purpose, default value, when populated

### Serialization Format

ConfigNode layout for anything persisted. If using sidecar files: naming
convention, directory, safe-write strategy, fallback on failure.

## Behavior

One subsection per trigger/action or per logical area. Each scenario
maps to a gameplay scenario from Step 2. The structure within each
subsection should serve the content — a trigger/action feature might
use Trigger/Preconditions/Actions/Result fields, while a structural
redesign might use narrative subsections. The requirement is
concreteness: "when X happens, Y happens", not abstract descriptions.

### <Trigger/Action Name>

Describe the behavior concretely. Include:
- What causes it (player action, game event, timer)
- What must be true for it to activate
- What happens, in order
- Observable outcome from the player's perspective
- How it interacts with existing systems (what IS and is NOT affected)

## Edge Cases

Exhaustive for the feature's complexity. Group by category
(timing, destruction, UI, save/load, etc.). Each edge case gets:

### E1: <Scenario Name> (v1 / Deferred)

**Scenario:** What triggers it.
**Expected behavior:** What should happen.
**Deferred reason:** (if deferred) Why, and when to revisit.

## What Doesn't Change

Systems and behaviors NOT affected by this redesign. Prevents scope creep
and gives reviewers confidence.

## Backward Compatibility

- Save file migration strategy (version field, migration code)
- Recording format version changes
- Behavior with old saves loaded in new version (and vice versa)

## Performance Budget

| Operation | Budget | Justification |
|-----------|--------|---------------|
| <per-frame op> | <target> | <why> |

## Error Recovery

Failure modes and handling: I/O failure, corrupt data, null refs from
KSP API, scene changes mid-operation.

## Diagnostic Logging

Organize by category (state transitions, decisions, errors, per-frame),
not by section number. Each log line specifies:
- Subsystem tag, level (Info/Verbose/Warn)
- When it fires
- What context it includes (IDs, old→new values, the "why")

### State Transitions
- `[Parsek][INFO][<Subsystem>] <state> → <state>: <context>`

### Decisions
- `[Parsek][INFO][<Subsystem>] <chose path A because X>`

### Error/Fallback
- `[Parsek][WARN][<Subsystem>] <what failed, what fallback used>`

### Per-Frame (high-frequency)
- `[Parsek][VERBOSE][<Subsystem>] <message>` or VerboseRateLimited

Goal: a developer reading KSP.log should reconstruct what the system did
and why, without looking at source code.

## Test Plan

### Unit Tests
- **<TestName>** — what makes it fail: <specific bug>

### Integration Tests
- **<TestName>** — fixture: <synthetic data needed>, what makes it fail: <regression>

### Log Assertion Tests
- **<TestName>** — asserts specific log lines appear, catches: <silent removal of diagnostics>

### Synthetic Recordings (if needed)
- **<Name>** — scenario, what it exercises

### Manual In-Game Tests
- **<Scenario>** — steps, expected result, log check command
```

---

## 2. Codebase Inventory

**File:** `docs/dev/plans/<feature>-inventory.md`
**Purpose:** Map the territory before planning. Documents what exists, where things live, what's coupled to what. Produced by Explore agents in step 4a. The Plan agent reads this alongside the design doc to produce task plans.

This artifact was consistently produced in prior redesigns (refactor-1 inventory, refactor-2 inventory, T25 current architecture section) and proved essential for accurate task decomposition.

### Template

```markdown
# <Feature Name> — Codebase Inventory

*Baseline snapshot before implementation begins.*

**Baseline test count:** <N> tests pass on <branch> at <commit>
**Worktree:** `Parsek-<branch>` branch `<branch>` off `<base>` at `<hash>`

The baseline test count is the quality floor: after each task, the test
count must be >= baseline + new tests added by that task. If any existing
test breaks, the task is not done.

## Affected Files

| File | Lines | Role | Changes Needed | Complexity | Status |
|------|-------|------|----------------|------------|--------|
| `Source/Parsek/<File>.cs` | <N> | <what it does> | <summary> | Low/Med/High | Pending |

Update the Status column as tasks progress: `Pending` → `In Progress` →
`Done (Task N)` / `Skip (reason)`. This is the primary tracking artifact
the orchestrator uses to know what's complete.

## Dependency Map

Which files depend on which — identify coupling hotspots that constrain
task ordering.

## Existing Patterns to Reuse

Code patterns already in the codebase that this redesign should follow
(serialization style, event handling, logging conventions, etc.).

## KSP API Surface

Relevant KSP APIs/events, with gotchas from `docs/mods-references/` and
MEMORY.md. Verified behavior (decompiled or tested) vs assumed behavior.

## Static Mutable State

Shared state that requires `[Collection("Sequential")]` in tests or
careful initialization ordering.
```

---

## 3. Per-Task Implementation Plan

**File:** `docs/dev/plans/<feature>-task-N-<component>.md`
**Purpose:** Precise instructions for an implementation agent. Zero ambiguity — the agent should be able to execute without asking questions.

The workflow pipeline for each task (plan → plan review → implement → review → fix → commit) is defined in `development-workflow.md` step 4. Task plans do not need to restate it.

### Template

```markdown
# Task N: <Component Name> — Implementation Plan

## 1. Overview

What this task does, what invariant it establishes, how it fits the
overall redesign. State the key design decision or insight.

What existing behavior is preserved. What changes.

**Depends on:** Task(s) that must be complete before this one.
**Enables:** Task(s) this unblocks.

---

## 2. Current Behavior Analysis

### 2.1 <Relevant Subsystem/Method>

Located at `<file>:<line>`. What currently happens, step by step.
Method signature if it will be modified. Specific line numbers for
key decision points.

```csharp
// Current signature
<return type> <MethodName>(<params>)
```

Current logic (in priority order):
1. <Step 1>
2. <Step 2>

### 2.2 <Another Relevant Subsystem>

(Repeat for each area this task touches.)

---

## 3. Changes to <Component/File>

### 3.1 <Specific Change>

What changes and why. New code shape:

```csharp
// New/modified code
```

**Critical:** Subtle correctness requirements, ordering dependencies,
things that look simplifiable but can't be.

### 3.2 <Another Change>

(Repeat for each discrete change.)

---

## 4. Unit Tests

New file `Source/Parsek.Tests/<TestFile>.cs` (or additions to existing):

1. `<TestName>` — what it tests, what makes it fail
2. `<TestName>` — what it tests, what makes it fail

---

## 5. In-Game Tests

1. **<Scenario>**: steps to reproduce, verify <expected behavior>

---

## 6. Risk Assessment (if non-trivial risks exist)

| Risk | Mitigation |
|------|-----------|
| <what could go wrong> | <how the design prevents/handles it> |

Omit this section for pure data-model tasks or tasks with no runtime
risk. Include it when modifying control flow, hot paths, concurrency,
or KSP event handlers.

---

## 7. Implementation Order

Numbered steps in dependency order. Implementation agent follows
top-to-bottom:

1. <First change>
2. <Second change — depends on 1>
...
N. Run `dotnet build` and `dotnet test` — all tests pass

---

## 8. Files Modified

| File | Changes |
|------|---------|
| `Source/Parsek/<File>.cs` | <summary> |
| `Source/Parsek.Tests/<File>.cs` | New file — <what tests> |

---

## 9. What This Task Does NOT Do

Explicitly excluded work with references to which future task handles it:

- Does not <X> (Task M)
- Does not <Y> (Task K)

**Scaffolding:** Code introduced here that remains dormant until a
downstream task activates it. Example: Task 2 added `TransitionToBackground`
and `PromoteFromBackground` decision paths to `DecideOnVesselSwitch`, but
no code creates `RecordingTree` instances yet — that's Task 4. The decision
paths are dead code until Task 4 wires up tree creation. This is acceptable
when the scaffolding is tested in isolation (Task 2 has unit tests for the
decision function with mock tree data).
```

### Phased Tasks

Some tasks are large enough to have internal phases (e.g., camera-follow had 6 phases within one plan). When a task has internal phases, replace section 3 ("Changes") with numbered phase sections. The task is still one logical unit — phases are implementation ordering within it, not separate tasks.

Each phase section should have:
- **What it does** — the specific change this phase makes
- **Files modified** — which files and what changes
- **Done condition** — how to verify this phase is complete before moving to the next

Example structure:
```
## 3. Phase 1: Data Model
  Changes to <File>, adds <fields>. Done when serialization round-trip test passes.
## 4. Phase 2: Core Behavior
  Changes to <File>, modifies <method>. Done when unit tests for <behavior> pass.
## 5. Phase 3: UI Integration
  Changes to <File>, adds <button/display>. Done when in-game verification passes.
```

---

## 4. Deferred Items Document

**File:** `docs/dev/plans/<feature>-deferred.md`
**Purpose:** Parking lot for good ideas that are out of scope. Prevents re-debating closed decisions. Each item has explicit justification and a trigger for revisiting.

### Template

```markdown
# <Feature Name> — Deferred Items

Items identified during design and implementation that are out of scope.
Each has a justification and a trigger for when to revisit.

---

## D1. <Item Name>

**What:** One sentence describing the feature/fix.
**Why deferred:** Explicit reason (too risky, needs other work first,
                  rare scenario, performance concern, etc.)
**Revisit when:** Trigger condition (after Task N, when X is refactored,
                  if users report Y).
**Status:** Open / Done (Task N, PR #M) / Closed — <reason>

---

## D2. <Item Name>

(Repeat pattern.)
```

Status values used in practice:
- `Open` — not yet addressed
- `Done (T28)` or `Done (PR #82)` — completed, with cross-reference
- `Closed — below 5-line min` / `Closed — API divergence` / `Closed — not worth the risk` — decided against, with explanation

**Grouping:** Once the list exceeds ~5 items, group by source — e.g., "Deferred from ParsekFlight.cs", "Deferred from Phase 3B", "Deferred from Design Review". This matches how refactor-2's 21-item deferred list stayed navigable.

---

## 5. Review Checklist

**File:** `docs/dev/plans/<feature>-review-checklist.md`
**Purpose:** Feature-specific review items that go beyond the standard post-change checklist in CLAUDE.md. Only create this if the redesign has domain-specific correctness requirements.

Do NOT duplicate the standard checks from CLAUDE.md — just reference it. This checklist adds items unique to this feature.

### Template

```markdown
# <Feature Name> — Review Checklist

Feature-specific checks for reviewing each task. Apply the standard
post-change checklist from CLAUDE.md first, then these additional checks.

## Feature-Specific Checks

- [ ] <Correctness requirement unique to this feature>
- [ ] <Serialization round-trip for new data types>
- [ ] <Backward compatibility with existing saves>
- [ ] <Performance check for per-frame operations>
- [ ] <Interaction with <existing system> verified>

## Agent Constraints (if refactoring)

ALLOWED:
- <Specific operation agents may perform>

NOT ALLOWED:
- <Specific operation agents must NOT do>

**Conflict resolution:** <How to resolve when two rules conflict>
```

---

## 6. Task Decomposition Principles

### Ordering

Derived from the recording system redesign (tasks 1-13):

1. **Data model first** — structs, enums, serialization. No behavior. Proves round-trip.
2. **Core mechanism** — the fundamental behavioral change. Minimal scope.
3. **Event detection** — each event type gets its own task with its own tests.
4. **Integration** — connecting the new system to existing subsystems (commit, UI, playback).
5. **Polish** — resource tracking, backward compat, logging audit, test coverage.

### Granularity

- A task establishes **one invariant** or implements **one behavior**. If you need "and" to describe it, split it.
- A task has a **clear done condition**: specific tests pass, specific behavior works.
- Data model tasks produce **no runtime behavior** — just types and serialization.
- Each task plan includes **"What This Task Does NOT Do"** listing explicit exclusions.
- A task that touches many files but makes the same mechanical change everywhere (e.g., renaming, extraction) is fine — the "1-3 files" guideline is for tasks involving different logic in each file.

### Naming

Task plan files: `<feature>-task-N-<component>.md` is the default convention (N sequential, component is a short descriptor). Variations in practice:
- Todo-list numbering: `T25-timeline-playback-controller.md` (references existing todo item)
- Single-task features: `camera-follow-ghost-plan.md` (no task number needed)
- Refactors: `code-refactor-2-plan.md` (feature name IS the descriptor)

Pick whichever is clearest for the feature. Commit messages follow CLAUDE.md conventions and reference the task.

### Dependencies

Each task plan states **Depends on** and **Enables**. Independent tasks can run as parallel agents in separate worktrees.

---

## 7. Operational Conventions

These conventions supplement `development-workflow.md` step 4 and CLAUDE.md's post-change checklist with redesign-specific operational detail.

### Baseline

Before implementation begins, record in the inventory doc:
- **Test count:** `dotnet test` result on the starting branch
- **Worktree:** branch name, base commit hash
- **Build state:** `dotnet build` clean (zero warnings relevant to this feature)

### Quality Gates

| When | Check | Pass Criteria |
|------|-------|--------------|
| After each task | `dotnet build` | Zero new warnings |
| After each task | `dotnet test` | All tests pass (≥ baseline count + new tests) |
| After each phase | Manual in-game test | Per the task's in-game test section |
| Before merge | Full test suite + log validation | `pwsh -File scripts/validate-ksp-log.ps1` clean |

### Commit Strategy

- One logical change per commit within a task
- Follow CLAUDE.md for commit message conventions
- Each task's final commit should leave `dotnet test` passing

### Rollback

- Each task is implemented in an isolated worktree — discard the worktree to revert
- If a task is merged and later found broken, revert the merge commit rather than patching forward
- Deferred items doc tracks anything discovered mid-implementation that changes the plan

---

## 8. Lifecycle Summary

Quick reference for the full pipeline. See `development-workflow.md` for detailed agent orchestration at each step.

```
Step 1-2: Vision + Scenarios (human + Claude, no code)
    ↓
Step 3: System Design Doc
    ↓  also produces: deferred.md (initial)
    ↓
Step 4a: Codebase Exploration → Inventory Doc
    ↓  record baseline test count, worktree setup
    ↓
Step 4a: Task Decomposition → task-N-*.md plans
    ↓  optionally: review-checklist.md
    ↓
Step 4b: Plan Review (orchestrator approves/adjusts)
    ↓
Step 4c-f: Implement → Review → Fix cycle (per task, in worktrees)
    ↓  update deferred.md as decisions surface
    ↓
Step 5: Commit + Update docs
    ↓  move artifacts to docs/dev/done/<feature>/
    ↓  update roadmap, MEMORY.md, CLAUDE.md
```
