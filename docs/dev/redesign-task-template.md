# Redesign Task Template

Template for structuring large multi-phase redesign efforts in Parsek. Derived from how the recording system redesign (PR #59, 38,902 lines, 114 files) and subsequent refactors (T25, refactor-2) were planned and executed.

This template covers the full lifecycle: system design doc, per-task implementation plans, deferred items tracking, and review checklists. Follow `development-workflow.md` for the agent orchestration process (steps 1-5). This template focuses on the **document artifacts** each step produces.

---

## Document Set for a Redesign

A redesign produces these artifacts, roughly in order:

```
docs/
  parsek-<feature>-system-design.md          ← Step 3 output: the system design spec
docs/dev/plans/
  <feature>-task-N-<component>.md            ← Step 4a output: per-task implementation plans
  <feature>-deferred.md                      ← Accumulated during steps 3-4: parking lot for future work
  <feature>-review-checklist.md              ← Pre-merge checklist specific to the redesign
```

When the redesign is complete, move all artifacts to `docs/dev/done/` (or a subdirectory like `docs/dev/done/<feature>/`).

---

## 1. System Design Document

**File:** `docs/parsek-<feature>-system-design.md`
**When:** Step 3 of the workflow, after vision and gameplay scenarios are settled.
**Purpose:** The single source of truth for what the system does. Implementation agents read this; review agents verify against this. Everything flows from this document.

### Template

```markdown
# Parsek <Feature Name> — Design Document

*Design specification for <one-line description of what this system does>.*

*Status: IN PROGRESS / IMPLEMENTATION COMPLETE (date, PR #)*

---

## 1. Purpose and Scope

What this document covers (bulleted list of subsystems/concerns).
What it does NOT cover (explicit exclusions to prevent scope creep).

---

## 2. Terminology

Define new concepts introduced by this feature. Distinguish from KSP's existing
model. Keep it short — only terms that would confuse someone reading the doc cold.

| Term | Definition |
|------|------------|
| <term> | <definition, with distinction from KSP concept if relevant> |

---

## 3. Mental Model

How the feature works conceptually. Write for someone who has never seen the code.
Include ASCII diagrams for data flow, state machines, or structural relationships.

```
  Example:
  ┌──────────┐   trigger   ┌──────────┐   commit   ┌──────────┐
  │ Idle     │────────────▶│ Active   │───────────▶│ Complete │
  └──────────┘             └──────────┘            └──────────┘
```

If the system has phases or modes, show them as a state machine or flowchart.

---

## 4. Data Model

### 4.1 New Types

For each new struct/class/enum:

```csharp
// File: Source/Parsek/<TypeName>.cs
public struct/class/enum <TypeName>
{
    <field type> <field name>;  // purpose
    ...
}
```

Design notes:
- Why class vs struct (reference semantics, contains lists, etc.)
- Explicit int values on enums for stable serialization
- Any field that needs special serialization (Quaternion, Vector3d, etc.)

### 4.2 Changes to Existing Types

For each existing type that gains new fields:

**<ClassName>** (`Source/Parsek/<File>.cs`):
- `<new field>` — purpose, default value, when populated

### 4.3 Serialization Format

Show the ConfigNode layout for anything that persists to save files or sidecar files:

```
SECTION_NAME
{
    key1 = <type, format notes>
    key2 = <type, format notes>
    NESTED_NODE
    {
        ...
    }
}
```

If using external files (sidecar format), document:
- File naming convention
- Directory location (`saves/<save>/Parsek/...`)
- Safe-write strategy (.tmp + rename)
- Fallback behavior if file write fails

---

## 5. Behavior

Concrete scenarios, one subsection per trigger/action. Each scenario should map
directly to a gameplay scenario from Step 2.

### 5.N <Trigger/Action Name>

**Trigger:** What causes this behavior (player action, game event, timer, etc.)

**Preconditions:** What must be true for this to activate.

**Actions (in order):**
1. First thing that happens
2. Second thing that happens
3. ...

**Result:** Observable outcome from the player's perspective.

**Interaction with existing systems:** How this interacts with recording, ghosts,
chains, time warp, scene changes, etc. Be explicit about what is NOT affected.

---

## 6. Edge Cases

Exhaustive. Target 15+ for any non-trivial feature. Each edge case:

| # | Scenario | Expected Behavior | v1 or Deferred |
|---|----------|--------------------|----------------|
| 1 | <what triggers it> | <what should happen> | v1 / Deferred (reason) |
| 2 | ... | ... | ... |

Group by category if helpful (timing edge cases, destruction edge cases, UI edge
cases, save/load edge cases, etc.).

---

## 7. What Doesn't Change

Explicitly list systems and behaviors that are NOT affected by this redesign.
This prevents scope creep and gives reviewers confidence.

- <System A> — why it's unaffected
- <System B> — why it's unaffected

---

## 8. Backward Compatibility

### 8.1 Save File Migration

How existing saves interact with the new system:
- Auto-migration strategy (version field, migration code location)
- What happens if a save is loaded without the new version
- What happens if a new save is loaded by an old version of the mod

### 8.2 Recording Format

If the recording format changes:
- New format version number
- What changes vs previous version
- Migration path (one-time at load, on-save, etc.)

---

## 9. Performance Budget

For systems that run per-frame or scale with entity count:

| Operation | Budget | Justification |
|-----------|--------|---------------|
| <per-frame operation> | <target cost> | <why this matters> |

---

## 10. Error Recovery

What happens when things go wrong:
- File I/O failure during save
- Corrupt data in loaded files
- Unexpected null references from KSP API
- Scene changes mid-operation

---

## 11. Diagnostic Logging

For each section in Behavior (5.N) and each Edge Case (6.N), list the log lines:

### 11.1 <Behavior/Edge Case Name>
- `[Parsek][INFO][<Subsystem>] <message with context>` — when this fires
- `[Parsek][VERBOSE][<Subsystem>] <message>` — per-frame/high-frequency variant
- `[Parsek][WARN][<Subsystem>] <message>` — unexpected but handled condition

Every decision branch that picks one path over another gets a log line explaining
why. Every state transition logs old→new with enough context to reconstruct the
sequence from KSP.log alone.

---

## 12. Test Plan

### 12.1 Unit Tests
For each testable method/decision:
- **Test:** `<ClassName>.<MethodName>_<Scenario>_<ExpectedResult>`
- **What makes it fail:** <specific bug this catches>

### 12.2 Integration Tests
- **Test:** <description>
- **Fixture:** <what synthetic data is needed>
- **What makes it fail:** <specific regression>

### 12.3 Log Assertion Tests
- **Test:** <description>
- **Asserts:** `logLines.Contains("[<Subsystem>]") && logLines.Contains("<key phrase>")`
- **What makes it fail:** <silent removal of diagnostic logging>

### 12.4 Synthetic Recordings
If the feature needs new test recordings:
- **Recording:** <name, scenario description>
- **What it exercises:** <which behaviors/edge cases>

### 12.5 Manual In-Game Tests
- **Test:** <description of what to do in KSP>
- **Expected:** <what the player should see>
- **Log check:** `grep "[Parsek].*<key phrase>" KSP.log`
```

---

## 2. Per-Task Implementation Plan

**File:** `docs/dev/plans/<feature>-task-N-<component>.md`
**When:** Step 4a, after the Plan agent has explored the codebase and mapped the design onto existing code.
**Purpose:** Precise instructions for an implementation agent. Zero ambiguity — the agent should be able to execute without asking questions.

### Template

```markdown
# Task N: <Component Name> — Implementation Plan

## Workflow

This task follows the multi-stage review pipeline:

1. **Plan** — Opus subagent explores codebase, writes implementation plan
2. **Plan review** — Fresh subagent reviews plan for correctness and risk
3. **Orchestrator review** — Main session reviews with full project context
4. **Implement** — Fresh subagent implements in isolated worktree
5. **Implementation review** — Fresh subagent reviews diff against design doc
6. **Final review** — Main session reviews architectural fit
7. **Commit** — Main session commits
8. **Next task briefing** — Main session presents next task with context

---

## Plan

### 1. Overview

One paragraph: what this task does, what invariant it establishes, and how it
fits into the overall redesign. State the key insight or design decision.

Mention what existing behavior is preserved and what changes.

---

### 2. Current Behavior Analysis

#### 2.1 <Relevant Subsystem/Method>

Located at <file>:<line>. Describe what currently happens, step by step.
Include the method signature if it will be modified.
Reference specific line numbers for key decision points.

```csharp
// Current signature (for methods being modified)
<return type> <MethodName>(<params>)
```

Current logic (in priority order):
1. <Step 1>
2. <Step 2>
...

#### 2.2 <Another Relevant Subsystem>

(Repeat for each area of existing code that this task touches)

---

### 3. Changes to <Component/File>

#### 3.1 <Specific Change>

What changes and why. Show the new code shape:

```csharp
// New/modified code
```

**Critical:** Call out any subtle correctness requirements, ordering
dependencies, or things that look like they could be simplified but can't.

#### 3.2 <Another Change>

(Repeat for each discrete change)

---

### N. Unit Tests

New file `Source/Parsek.Tests/<TestFile>.cs` (or additions to existing test file):

1. `<TestName>` — <what it tests, what makes it fail>
2. `<TestName>` — <what it tests, what makes it fail>
...

---

### N+1. In-Game Tests

1. **<Scenario>**: <steps to reproduce>, verify <expected behavior>
2. ...

---

### N+2. Risk Assessment

| Risk | Mitigation |
|------|-----------|
| <what could go wrong> | <how the design prevents/handles it> |

---

### N+3. Implementation Order

Numbered list of discrete steps, in dependency order. An implementation agent
follows this list top-to-bottom:

1. <First change — no dependencies>
2. <Second change — depends on 1>
...
N. Run `dotnet test` — all existing + new tests pass

---

### N+4. Files Modified

| File | Changes |
|------|---------|
| `Source/Parsek/<File>.cs` | <summary of changes> |
| `Source/Parsek.Tests/<File>.cs` | New file — <what tests> |

---

### N+5. What This Task Does NOT Do

Bulleted list of explicitly excluded work. Reference which future task handles
each item. This prevents implementation agents from over-building and gives
reviewers a clear scope boundary.

- Does not <X> (Task M)
- Does not <Y> (Task K)

**Dependency notes:** For each downstream task that depends on scaffolding
introduced here, explain what this task provides and what remains dormant
until the downstream task activates it.
```

---

## 3. Deferred Items Document

**File:** `docs/dev/plans/<feature>-deferred.md`
**When:** Accumulated throughout steps 3-4. Created early, updated as decisions are made.
**Purpose:** Parking lot for good ideas that are out of scope. Prevents re-debating closed decisions. Each item has explicit justification and a trigger for revisiting.

### Template

```markdown
# <Feature Name> — Deferred Items

Items identified during design and implementation that are out of scope for this
redesign. Each has a justification and a trigger for when to revisit.

---

## D1. <Item Name>

**What:** One sentence describing the feature/fix.
**Why deferred:** Explicit reason (too risky, needs other work first, rare scenario,
                  performance concern, etc.)
**Revisit when:** Trigger condition (after Task N, when X is refactored, if users report Y).
**Status:** OPEN / DONE (Task/PR reference) / WONT-DO (reason)

---

## D2. <Item Name>

(Repeat pattern)
```

---

## 4. Review Checklist

**File:** `docs/dev/plans/<feature>-review-checklist.md`
**When:** Created before implementation begins. Used by review agents after each task.
**Purpose:** Feature-specific review items beyond the standard post-change checklist in CLAUDE.md.

### Template

```markdown
# <Feature Name> — Review Checklist

Checklist for reviewing implementation of each task in this redesign.
Supplements the standard post-change checklist in CLAUDE.md.

## Standard Checks (every task)

- [ ] `dotnet build` succeeds
- [ ] `dotnet test` — all existing tests pass
- [ ] New tests present with "what makes it fail" justification
- [ ] Log assertion tests verify diagnostic coverage
- [ ] ParsekScenario OnSave/OnLoad handles any new persisted data
- [ ] No silent code paths (every branch logs why)
- [ ] Edge cases from design doc are handled or explicitly deferred

## Feature-Specific Checks

- [ ] <Check specific to this feature's correctness requirements>
- [ ] <Serialization round-trip for new data types>
- [ ] <Backward compatibility with existing saves>
- [ ] <Performance check for per-frame operations>
- [ ] <Interaction with <existing system> verified>

## Naming and Convention Checks

- [ ] New enums have explicit int values for stable serialization
- [ ] New `internal static` methods for pure/testable logic
- [ ] ConfigNode keys follow existing naming convention
- [ ] Log messages include subsystem tag and relevant IDs
```

---

## 5. Task Decomposition Principles

Derived from how the recording system redesign was broken into tasks 1-13:

### Ordering

1. **Data model first** (Task 1) — structs, enums, serialization. No behavior. Proves round-trip.
2. **Core mechanism** (Tasks 2-3) — the fundamental behavioral change. Minimal scope.
3. **Event detection** (Tasks 4-6) — each event type gets its own task with its own tests.
4. **Integration** (Tasks 7-9) — connecting the new system to existing subsystems (commit, UI, playback).
5. **Polish** (Tasks 10-13) — resource tracking, backward compat, logging audit, test coverage.

### Granularity Rules

- A task touches **1-3 files** (excluding tests). More than 3 → split it.
- A task establishes **one invariant** or implements **one behavior**. If you need "and" to describe it, split.
- A task has a **clear done condition**: specific tests pass, specific behavior works.
- Data model tasks produce **no runtime behavior** — just types and serialization.
- Each task's plan includes a **"What This Task Does NOT Do"** section listing explicit exclusions.

### Naming Convention

Task plans: `<feature>-task-N-<component>.md`
- N is sequential (1, 2, 3...)
- Component is a short descriptor (data-model, vessel-switch, split-events, etc.)

Commit messages reference task numbers: `<Feature> Task N: <description>`

### Dependency Tracking

Each task plan states:
- **Depends on:** Which previous tasks must be complete
- **Enables:** Which downstream tasks this unblocks
- **Scaffolding:** Code introduced here that remains dormant until a downstream task activates it

Independent tasks can run as parallel agents in separate worktrees.

---

## 6. Lifecycle Summary

```
Step 1-2: Vision + Scenarios (human + Claude, no code)
    ↓
Step 3: System Design Doc (docs/parsek-<feature>-system-design.md)
    ↓  also produces: deferred.md (initial), review-checklist.md
    ↓
Step 4a: Codebase Exploration (Explore agents, read-only)
    ↓
Step 4a: Task Decomposition (Plan agent → task-N-*.md plans)
    ↓
Step 4b: Plan Review (orchestrator approves/adjusts)
    ↓
Step 4c-f: Implement → Review → Fix cycle (per task, in worktrees)
    ↓  update deferred.md as decisions are made
    ↓
Step 5: Commit + Update docs
    ↓  move plans to docs/dev/done/<feature>/
    ↓  update roadmap, MEMORY.md, CLAUDE.md
```
