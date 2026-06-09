# Parsek Design Document Template

*The single, canonical structure for a Parsek subsystem design document. Copy the skeleton at the bottom, then fill it in.*

This template is distilled from the eight live top-level design docs in `docs/`:
`parsek-architecture.md`, `parsek-flight-recorder-design.md`,
`parsek-game-actions-and-resources-recorder-design.md`,
`parsek-ghost-trajectory-rendering-design.md`,
`parsek-logistics-supply-routes-design.md`,
`parsek-recording-finalization-design.md`,
`parsek-rewind-to-separation-design.md`, and `parsek-timeline-design.md`.
It describes what those docs actually do, not an idealized form. It is the structure document; for the multi-artifact redesign workflow (inventory, task plans, deferred items, review checklist) see `docs/dev/redesign-task-template.md`, and for the agent orchestration process see `docs/dev/development-workflow.md`.

---

## How to use this template

1. A design doc is the single source of truth for one subsystem. Implementation agents read it; review agents verify against it. Everything flows from this document.
2. Copy the skeleton at the end into `docs/<feature>-design.md` (live subsystem docs live at the top level of `docs/`; pre-implementation specs may start under `docs/dev/` and move to `docs/dev/done/` when superseded).
3. Include every REQUIRED section. Add OPTIONAL sections only when the trigger applies. Reorder or rename a heading only when clarity demands it: the headings should serve the reader, not this template.
4. Scale the depth to the feature. The smaller live docs (timeline, finalization) run 8 to 12 sections with flat headings. The largest (flight recorder, game actions, rewind-to-separation) run 20-plus numbered sections with sub-templates. Both are correct. Match the doc to the subsystem.
5. Keep to the house style below. Every doc in the set follows it.

---

## House style conventions

These hold across all eight docs and are non-negotiable:

- **Plain ASCII only.** No emoji, no special Unicode. (A few legacy docs contain stray arrow or epsilon glyphs; do not add new ones. Write `->` for arrows.)
- **No em dashes.** Use a colon, parentheses, a comma, or split the sentence. (This rule is project-wide; see MEMORY and CLAUDE.md.)
- **Title line:** `# Parsek <Feature Name> - Design Document` (or `# Parsek - <Feature> Design`). Immediately followed by a one-line italic spec summary: `*Design specification for <one-line description>.*`
- **Status block** directly under the title (before the first `---`). Use bold lead-in labels on their own lines. The recurring labels are `**Status:**` (e.g. `shipped`, `IN PROGRESS`, `Complete`, `IMPLEMENTATION COMPLETE (date, PR #)`), optionally `**Version:**`, and `**Out of scope:**` with a pointer to the doc that owns the excluded area. Most docs also carry an italic one-line restatement of what Parsek is plus `**Related docs:**` cross-links.
- **`---` separators** between every top-level section.
- **file:line references** for code: `RewindInvoker.cs:463`, `Source/Parsek/EffectiveState.cs:236`, `FlightRecorder.cs:5502-5543`. Reference real class and method names. When a design concept maps to a differently-named class, state the mapping explicitly (design-concept-to-implementation-class table).
- **Enum and struct shapes** go in fenced code blocks listing `fieldName: type - purpose`, one per line. Give enums explicit int values when serialization order matters (`Undock=0, EVA=1, ...`).
- **Tables** for any "axis vs property" content: terminology, what-the-player-sees, environment-vs-sample-rate, module-vs-KSP-state, risk-vs-mitigation, concept-to-code.
- **ASCII diagrams** in fenced blocks for block diagrams, state machines, DAGs, data flow, and worked timelines. Use `+`, `-`, `|`, `v`, `->`.
- **Edge-case numbering:** either `### E1: <Name>` heading-per-case (the redesign-template convention) or a bare numbered list under `## Edge Cases` (the finalization and ghost-rendering convention). Pick one per doc and be consistent. Larger docs use a flat `### N.M <Name>` deep numbering; deeply numbered docs sometimes carry mid-section non-sequential numbers (a known artifact) but new docs should keep numbering sequential.
- **Worked examples** are concrete: real UTs, real subject ids, real fund amounts, run-by-run tables. "When X happens, Y happens", never an abstract description.

---

## Required sections

Every design doc includes these. Order is the default; adapt headings to the feature.

1. **Title + Status block.** Title, one-line italic spec summary, and the bold Status / Version / Out-of-scope / Related-docs block before the first separator. Establishes at a glance what shipped, what version, and what this doc does NOT own.
2. **Introduction (Purpose and Scope).** What the subsystem is and the problem it solves. State explicitly what the doc covers and what it does not (scope-creep fence). Almost every doc includes a "what the player sees" table and at least one end-to-end worked example (a full career Mun landing, a fuel-delivery route, a staging-split re-fly). For a pure-internal subsystem, replace "what the player sees" with "what changes vs what's new".
3. **Design Philosophy.** A numbered list of the principles that govern every later decision (commit-is-permanent, ghosts-are-the-only-paradox-prevention, correct-visually-minimal-efficient, append-only-history, observable-from-logs-alone). Stated up front because they justify the rest of the doc.
4. **Data Model.** The authoritative types: every struct, class, and enum the feature introduces or changes, shown as field-name/type/purpose blocks. Note class-vs-struct rationale and explicit enum int values. Include a Serialization Format subsection (ConfigNode layout, sidecar file naming and directory, safe-write strategy, what stays in `.sfs` vs sidecar, what is runtime-only and never persisted) whenever anything is persisted.
5. **Behavior.** One subsection per trigger, action, or logical area, each mapping to a gameplay scenario. Be concrete: what causes it, what must be true to activate, what happens in order, the observable player-facing outcome, and how it interacts with existing systems (what IS and is NOT affected).
6. **Edge Cases.** Exhaustive for the feature's complexity (5 to 8 for a small feature, 15-plus for a major subsystem, 75 for rewind-to-separation). Group by category (timing, destruction, save/load, UI). Each case names the scenario and the expected behavior; mark deferred cases with a reason.
7. **What Doesn't Change / Out of Scope.** The existing systems and behaviors this feature does not touch (reviewer confidence) and the things it could do but deliberately won't (the primary scope-creep fence for implementation agents). Many docs carry both; small docs may merge them.
8. **Diagnostic Logging.** Organized by subsystem tag or category, never by section number. List the tag catalog (one tag per concern so a developer can grep one concern in isolation), and for each logged event give the level (Info / Verbose / Warn), when it fires, and the context it must include (ids, old to new values, the "why"). State the per-frame rate-limit / batch-counter convention. Goal: a developer reading KSP.log reconstructs what the system did and why without reading source.
9. **Test Plan.** Subsections for Unit tests (pure logic, what makes each fail), Integration tests (synthetic-recording fixtures, the regression each catches), Log-assertion tests (which log lines must appear, catching silent removal of diagnostics), and In-game tests (`InGameTests/RuntimeTests.cs` scenarios, steps, expected result, log-check command). Add a Synthetic Recordings subsection when end-to-end fixtures are needed.

---

## Optional sections

Include each only when its trigger applies. Place it where it reads best (the suggested home is noted).

- **Terminology** (after Introduction): when the feature introduces concepts that would confuse a reader or that differ from KSP's own model. A Term / Definition table or a bulleted glossary. Distinguish each term from the nearest KSP concept. Include the design-concept-to-implementation-class mapping here when names diverge.
- **Mental Model** (after Terminology): when the system has non-obvious state, flow, or a state machine. ASCII diagrams written for someone who has never seen the code. The rewind doc uses annotated DAG diagrams; the finalization doc uses a two-product pseudo-code sketch.
- **Architecture Overview** (before Data Model): when the feature is a standalone module with its own coupling story. Document the coupling surface to other systems (often "exactly one field"), the cross-system API, and the recalculation or update triggers.
- **Existing Systems: What Changes vs What's New** (before Data Model): a Component / Current-behavior / Required-change / Complexity table. Maps the redesign onto existing code up front. Essential for refactors and features layered on existing subsystems.
- **KSP API Surface** (near Data Model or Mental Model): when design decisions depend on verified KSP API behavior (decompiled signatures, gotchas, radians-vs-degrees, event ordering). Put it in the design doc, do not defer it to the inventory, when the design hinges on it.
- **Backward Compatibility** (after Behavior or near the end): whenever the feature touches saves, the recording schema, or the ledger. State the format-version or schema-generation change, behavior with old saves on the new version and vice versa, and the migration stance. Note: Parsek's standing rule is no legacy migration paths for pre-1.0 recordings; if the contract changes, pick the correct end-to-end contract and reject incompatible old data on load rather than migrating. Say so explicitly.
- **Performance Budget** (near the end): when the feature has per-frame or per-ghost work that scales (cost-that-scales vs cost-that-does-not table, target budgets, profiling guidance).
- **Error Recovery** (near the end): when I/O, scene changes, or KSP API failures are concerns. Failure-mode tables with recovery and data-loss columns. State the default-safe principle (leave the vessel alone, the quicksave has it).
- **Implementation Phasing / Status** (near the end): when the feature ships in phases. A phase table (scope, status, test count) and the list of new source files. Mark what is complete and what is deferred.
- **Open Questions** (near the end): unresolved design decisions with enough context to revisit. Include only while genuinely open.
- **Code Layout / Implementation Map** (near the end): a concept-to-code table plus the file responsibilities. Most useful for shipped docs so a reader can jump from design concept to source file.
- **Risks** (near the end): a risk / mitigation table when the feature modifies hot paths, concurrency, KSP event handlers, or save state.
- **References / Related Docs / Appendices** (at the end): cross-links to plans, research notes, and reference URLs. Appendices hold gameplay-scenario catalogs and reference-document lists (the logistics doc does this). Shipped docs often end with a one-line "consolidated from" provenance note.

---

## Copy-pasteable skeleton

```markdown
# Parsek <Feature Name> - Design Document

*Design specification for <one-line description of the subsystem>.*

*Parsek is a KSP1 mod for time-rewind mission recording. Players fly missions, commit recordings to a timeline, rewind to earlier points, and see previously recorded missions play back as ghost vessels alongside new ones. This document specifies <what this doc owns>.*

**Status:** IN PROGRESS / shipped (vX.Y.Z) / IMPLEMENTATION COMPLETE (date, PR #)
**Version:** <optional, e.g. 0.5>
**Out of scope:** <explicit exclusions, each with a pointer to the doc that owns it>
**Related docs:** <cross-links>

---

## 1. Introduction

<What this subsystem is and the problem it solves. What this document covers (bulleted) and what it deliberately does not.>

### 1.x What the player sees

| Situation | What happens |
|-----------|--------------|
| <player action / game event> | <observable outcome> |

### 1.x Worked example

<A concrete end-to-end walkthrough with real UTs / ids / amounts. Use a fenced ASCII block or a run-by-run table.>

---

## 2. Design Philosophy

<Numbered principles that govern every later decision. Each is one bold lead-in sentence plus a clause of justification.>

1. **<Principle>.** <Why it holds and what it constrains.>
2. **<Principle>.** <...>

---

## 3. Terminology
<!-- OPTIONAL: include when introducing concepts distinct from KSP's model. -->

| Term | Definition |
|------|------------|
| <Term> | <definition, distinguished from the nearest KSP concept> |

<!-- Include the design-concept-to-implementation-class mapping here when names diverge:
| Design concept | Implementation class | Meaning | -->

---

## 4. Mental Model
<!-- OPTIONAL: include when the system has non-obvious state or flow. -->

<ASCII diagram(s) of the state machine / data flow / DAG, written for someone who has never seen the code.>

---

## 5. Architecture / Existing Systems
<!-- OPTIONAL: include for a standalone module (coupling + API + triggers)
     or a feature layered on existing code (what-changes-vs-what's-new table). -->

| Component | Current Behavior | Required Change | Complexity |
|-----------|------------------|-----------------|------------|
| `Source/Parsek/<File>.cs` | <now> | <change> | Low/Med/High |

---

## 6. Data Model

### 6.x New Types

<For each new struct / class / enum, a fenced block of `fieldName: type - purpose`.
 Note class vs struct rationale and explicit enum int values for stable serialization.>

### 6.x Changes to Existing Types

**<ClassName>** (`Source/Parsek/<File>.cs`):
- `<new field>` - purpose, default value, when populated.

### 6.x Serialization Format
<!-- Include whenever anything is persisted. -->

<ConfigNode / sidecar layout: file naming, directory, safe-write (tmp+rename),
 what lives in .sfs vs sidecar, what is runtime-only and never persisted,
 schema/format version or generation change.>

---

## 7. Behavior

### 7.x <Trigger / Action Name>

<Concrete description: what causes it, what must be true to activate, what happens
 in order, the observable player-facing outcome, and what existing systems IS and
 is NOT affected.>

---

## 8. Edge Cases

<!-- Pick ONE numbering style per doc and keep it consistent:
     bare numbered list, OR `### E1: <Name>` heading-per-case. -->

1. **<Scenario name>.** <Trigger.> <Expected behavior.> <Deferred reason, if deferred.>
2. **<Scenario name>.** <...>

---

## 9. What Doesn't Change
<!-- Existing systems and behaviors NOT affected. Reviewer confidence. -->

- <unaffected system / invariant>

## 10. Out of Scope
<!-- Things the feature could do but deliberately won't. Scope-creep fence. -->

- <excluded capability, with the future task/doc that would own it>

---

## 11. Backward Compatibility
<!-- OPTIONAL: include when touching saves, recording schema, or the ledger. -->

<Format-version / schema-generation change. Behavior with old saves on the new
 version and vice versa. Migration stance (default: no legacy migration; reject
 incompatible old data on load).>

---

## 12. Performance Budget
<!-- OPTIONAL: include for per-frame or scaling operations. -->

| Operation | Budget | Justification |
|-----------|--------|---------------|
| <per-frame op> | <target> | <why> |

---

## 13. Error Recovery
<!-- OPTIONAL: include when I/O, scene changes, or KSP API failures are concerns. -->

| Failure | Recovery | Data Loss |
|---------|----------|-----------|
| <what fails> | <fallback> | <none / recoverable / ...> |

---

## 14. Diagnostic Logging

<Organize by subsystem tag / category, never by section number.
 Log format is fixed: [Parsek][LEVEL][Subsystem] message.>

### 14.x Subsystem Tags

| Tag | Owns |
|-----|------|
| `[<Tag>]` | <the one concern this tag covers> |

### 14.x Logged Events

<Per category (state transitions, decisions, error/fallback, per-frame), list each
 event with its level, when it fires, and the context it must include (ids,
 old->new values, the why). State the per-frame rate-limit / batch-counter rule.>

---

## 15. Test Plan

### 15.x Unit Tests
- **<TestName>** - what it asserts, what makes it fail.

### 15.x Integration Tests
- **<TestName>** - fixture (synthetic data needed), the regression it catches.

### 15.x Log-Assertion Tests
- **<TestName>** - which log lines must appear, the silent diagnostic removal it catches.

### 15.x In-Game Tests (`InGameTests/RuntimeTests.cs`)
- **<Scenario>** - category, steps, expected result, log-check command.

### 15.x Synthetic Recordings
<!-- OPTIONAL: include when end-to-end fixtures are needed. -->
- **<Name>** - scenario, what it exercises.

---

## 16. Implementation Phasing / Status
<!-- OPTIONAL: include when the feature ships in phases. -->

| Phase | Scope | Status |
|-------|-------|--------|
| <phase> | <scope> | Done (N tests) / Deferred |

<New source files: <list>.>

---

## 17. Open Questions
<!-- OPTIONAL: include only while genuinely open. -->

### 17.x <Question>
<Context and the options under consideration.>

---

## 18. Code Layout / Implementation Map
<!-- OPTIONAL: most useful for shipped docs. -->

| Concept (this doc) | Code |
|--------------------|------|
| <design concept> | `Source/Parsek/<File>.cs` (`<Method>`) |

---

## References / Related Docs
<!-- OPTIONAL: cross-links, appendices (gameplay-scenario catalog, reference list),
     and a one-line "consolidated from <docs>" provenance note. -->

- [`<doc>`](<path>) - <what it is>.
```
